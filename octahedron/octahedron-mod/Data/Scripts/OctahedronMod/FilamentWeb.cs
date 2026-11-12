using System;
using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using ToolCoreApi;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace OctahedronMod
{
    // Registers the filament tools for FilamentWeb to draw, on the clients. Not in Init,
    // which runs on a worker thread for grids that are pasted or streamed in
    [MyEntityComponentDescriptor(
        typeof(MyObjectBuilder_ConveyorSorter),
        false,
        "OctahedronFilamentWelder",
        "OctahedronFilamentDeconstructor"
    )]
    public class FilamentTool : MyGameLogicComponent
    {
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            if (!MyAPIGateway.Utilities.IsDedicated)
                NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            if (!Entity.MarkedForClose)
                FilamentWeb.Tools.Add((IMyTerminalBlock)Entity);
        }

        public override void Close()
        {
            FilamentWeb.Tools.Remove((IMyTerminalBlock)Entity);
        }
    }

    // The filaments (SE-1067): while a filament tool works, dark threads reach out from
    // around its ring to the block it works on, spread over the block, with short strands
    // between neighbors, like the web of Laconia Station's shipyard in The Expanse. A slow
    // wave field drifting through space bends them (SE-1089), so neighboring threads bend
    // alike, like carbon fibers drifting in a current. ToolCore draws no beams for these
    // tools (their definitions have none), so this finds the block the way ToolCore aims:
    // along the emitter dummy of the turret's ball, up to the tool's reach.
    // The threads extend in about a second when the tool starts on a run of blocks, by
    // ToolCore's Hit state, and their ends glide from block to block (SE-1095). When the
    // tool stops, they stay on the last block for Linger seconds and glide on if it starts
    // again by then, so they retract less often (SE-1116). Only the drawing moves this
    // way: the tool works as ToolCore says, without waiting for the threads.
    // The storm sound plays here too, one sample at a time while the tool works, and a
    // started sample plays to its end. ToolCore would cut it off when Hit ends.
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class FilamentWeb : MySessionComponentBase
    {
        public static readonly HashSet<IMyTerminalBlock> Tools = new HashSet<IMyTerminalBlock>();

        const double Reach = 100;
        const double DrawDistance = 2000;
        const int AimInterval = 10;
        const int Threads = 8;
        const int Segments = 32;
        const int Strands = 10;

        // Seconds to extend or retract, to stay on the last block after the tool stops
        // before retracting, and the ends' glide time to a new block
        const double ExtendTime = 1;
        const double Linger = 3;
        const double GlideTime = 0.3;
        const string Sound = "OctahedronFilamentStorm";

        // The spread of the threads' ends over the block; the sag and the bend as parts of
        // the thread's length; the bend's wave length (m) and drift speed (rad/s). Wide
        // enough to see at shipyard distances, dark gray (SE-1089). The color is linear,
        // the texture darkens it further
        const double Spread = 1.4;
        const double Sag = 0.03;
        const double Bend = 0.07;
        const double WaveLength = 45;
        const double Drift = 0.35;
        const float ThreadWidth = 0.5f;
        const float StrandWidth = 0.3f;
        static readonly Vector4 Color = new Vector4(0.08f, 0.08f, 0.085f, 1f);
        static readonly MyStringId Material = MyStringId.GetOrCompute("WeaponLaser");

        // Emitter dummy of each tool's turret ball, and the ring's dummies the threads start from
        static readonly string[] Emitters = { "emitterball1", "destroemitter" };
        static readonly string[] Anchors =
        {
            "chargeemitter1",
            "chargeemitter2",
            "chargeemitter3",
            "chargeemitter4",
            "chargeemitter5",
            "chargeemitter6",
            "chargeemitter7",
            "chargeemitter8",
        };

        // The rig's per-frame trace of each web: tool, extension, drawn and aimed end,
        // whether the tool works
        public static Action<IMyTerminalBlock, double, Vector3D, Vector3D, bool> Trace;

        class Web
        {
            // Where the tool aims, and whether it works; the drawn end follows the aim
            public Vector3D Target;
            public bool Working;
            public Vector3D End;
            public Vector3D Velocity;
            public double Extension;
            public double Idle;

            // ToolCore's events; until the first comes, a shooting tool counts as working
            public Action<int, bool> Monitor;
            public bool Monitored;
            public bool Hitting;
            public MyEntity3DSoundEmitter Emitter;

            // In the tool's frame
            public Vector3D[] Anchors;
            public readonly Vector3D[] Ends = new Vector3D[Threads];
            public readonly Vector3D[] Bows = new Vector3D[Threads];
            public readonly Vector3D[] Shifts = new Vector3D[Threads];
            public readonly int[] StrandThreads = new int[Strands];
            public readonly double[] StrandAt = new double[Strands * 2];
        }

        readonly Dictionary<IMyTerminalBlock, Web> webs = new Dictionary<IMyTerminalBlock, Web>();
        readonly List<IHitInfo> hits = new List<IHitInfo>();
        readonly Dictionary<string, IMyModelDummy> dummies =
            new Dictionary<string, IMyModelDummy>();
        readonly Vector3D[,] points = new Vector3D[Threads, Segments + 1];
        readonly TCApi toolCore = new TCApi();
        MySoundPair storm;
        int tick;
        double drawn;

        public override void LoadData()
        {
            if (MyAPIGateway.Utilities.IsDedicated)
                return;
            toolCore.Load();
            storm = new MySoundPair(Sound);
        }

        protected override void UnloadData()
        {
            if (!MyAPIGateway.Utilities.IsDedicated)
                toolCore.Unload();
            Trace = null;
        }

        public override void UpdateAfterSimulation()
        {
            if (MyAPIGateway.Utilities.IsDedicated || ++tick % AimInterval != 0)
                return;
            var camera = MyAPIGateway.Session.Camera?.Position ?? Vector3D.Zero;
            foreach (var tool in Tools)
            {
                Web web;
                if (!webs.TryGetValue(tool, out web))
                    webs[tool] = web = NewWeb(tool.EntityId);
                // ToolCore takes the monitor only once it has the tool, which can be
                // after the tool got here
                if (toolCore.IsReady && !web.Monitored)
                {
                    toolCore.UnMonitorEvents((MyEntity)tool, web.Monitor);
                    toolCore.MonitorEvents((MyEntity)tool, web.Monitor);
                }
                web.Working = false;
                if (
                    !tool.IsWorking
                    || Vector3D.DistanceSquared(camera, tool.GetPosition())
                        > DrawDistance * DrawDistance
                    || !(web.Monitored ? web.Hitting : Shooting(tool))
                )
                    continue;
                if (web.Emitter == null)
                    web.Emitter = new MyEntity3DSoundEmitter((MyEntity)tool);
                if (!web.Emitter.IsPlaying)
                    web.Emitter.PlaySound(storm);
                MatrixD emitter;
                if (!Emitter(tool, out emitter))
                    continue;
                // The dummy's matrix has the model's scale
                var forward = Vector3D.Normalize(emitter.Forward);
                hits.Clear();
                MyAPIGateway.Physics.CastRay(
                    emitter.Translation,
                    emitter.Translation + forward * Reach,
                    hits
                );
                // The nearest block of another grid; while the turret turns past a gap,
                // the threads stay on the last block
                var nearest = double.MaxValue;
                foreach (var hit in hits)
                {
                    var grid = hit.HitEntity?.GetTopMostParent() as IMyCubeGrid;
                    if (grid == null || grid == tool.CubeGrid || hit.Fraction >= nearest)
                        continue;
                    nearest = hit.Fraction;
                    web.Target = hit.Position;
                    web.Working = true;
                }
                web.Working |= web.Extension > 0;
            }
            if (webs.Count > Tools.Count)
                foreach (var tool in new List<IMyTerminalBlock>(webs.Keys))
                    if (!Tools.Contains(tool))
                    {
                        if (toolCore.IsReady)
                            toolCore.UnMonitorEvents((MyEntity)tool, webs[tool].Monitor);
                        webs[tool].Emitter?.StopSound(true);
                        webs.Remove(tool);
                    }
        }

        public override void Draw()
        {
            if (MyAPIGateway.Utilities.IsDedicated)
                return;
            var time = MyAPIGateway.Session.ElapsedPlayTime.TotalSeconds;
            var dt = MathHelper.Clamp(time - drawn, 0, 0.1);
            drawn = time;
            foreach (var pair in webs)
            {
                var web = pair.Value;
                Animate(web, dt);
                if (web.Extension <= 0 || pair.Key.Closed)
                    continue;
                Trace?.Invoke(pair.Key, web.Extension, web.End, web.Target, web.Working);
                Draw(pair.Key, web, time);
            }
        }

        // Extends or retracts the threads and glides their end toward the aim, critically
        // damped, so it neither jumps nor overshoots
        static void Animate(Web web, double dt)
        {
            if (web.Working)
            {
                if (web.Extension <= 0)
                {
                    web.End = web.Target;
                    web.Velocity = Vector3D.Zero;
                }
                web.Idle = 0;
                web.Extension = Math.Min(1, web.Extension + dt / ExtendTime);
            }
            else if ((web.Idle += dt) > Linger)
                web.Extension = Math.Max(0, web.Extension - dt / ExtendTime);
            var omega = 2 / GlideTime;
            var x = omega * dt;
            var decay = 1 / (1 + x + 0.48 * x * x + 0.235 * x * x * x);
            var offset = web.End - web.Target;
            var push = (web.Velocity + omega * offset) * dt;
            web.Velocity = (web.Velocity - omega * push) * decay;
            web.End = web.Target + (offset + push) * decay;
        }

        void Draw(IMyTerminalBlock tool, Web web, double time)
        {
            if (web.Anchors == null)
                web.Anchors = AnchorsOf(tool);
            // The threads grow along their whole curve from the ring, easing in and out
            var e = web.Extension;
            var reach = e * e * (3 - 2 * e) * Segments;
            var full = (int)reach;
            for (var i = 0; i < Threads; i++)
            {
                var start = Vector3D.Transform(web.Anchors[i], tool.WorldMatrix);
                var end = web.End + web.Ends[i];
                var along = end - start;
                var length = along.Normalize();
                var sag = web.Bows[i] * length * Sag;
                for (var s = 0; s <= Segments; s++)
                {
                    var t = (double)s / Segments;
                    var point = Vector3D.Lerp(start, end, t);
                    // Only the field's part across the thread bends it; each thread
                    // samples it a little off its place, so neighbors differ a bit
                    var bend = Wave(point + web.Shifts[i], time);
                    bend -= along * bend.Dot(along);
                    points[i, s] = point + (sag + bend * length * Bend) * Math.Sin(Math.PI * t);
                }
                for (var s = 0; s < full; s++)
                    Line(points[i, s], points[i, s + 1], ThreadWidth);
                if (full < Segments)
                    Line(
                        points[i, full],
                        Vector3D.Lerp(points[i, full], points[i, full + 1], reach - full),
                        ThreadWidth
                    );
            }
            for (var k = 0; k < Strands; k++)
            {
                var i = web.StrandThreads[k];
                var j = (i + 1) % Threads;
                var a = (int)(web.StrandAt[2 * k] * Segments);
                var b = (int)(web.StrandAt[2 * k + 1] * Segments);
                if (a <= full && b <= full)
                    Line(points[i, a], points[j, b], StrandWidth);
            }
        }

        // A smooth field of unit-ish vectors that changes over WaveLength in space and
        // drifts slowly with time
        static Vector3D Wave(Vector3D p, double time)
        {
            p *= 2 * Math.PI / WaveLength;
            return new Vector3D(
                Math.Sin(p.Y + 0.5 * p.Z - Drift * time),
                Math.Sin(p.Z + 0.6 * p.X - 0.8 * Drift * time + 2),
                Math.Sin(p.X + 0.7 * p.Y - 1.2 * Drift * time + 4)
            );
        }

        static void Line(Vector3D from, Vector3D to, float width)
        {
            var direction = (Vector3)(to - from);
            var length = direction.Normalize();
            if (length > 0.01f)
                MyTransparentGeometry.AddLineBillboard(
                    Material,
                    Color,
                    from,
                    direction,
                    length,
                    width
                );
        }

        // Each tool's web has its own shape, the same on every client
        static Web NewWeb(long seed)
        {
            var random = new Random((int)(seed ^ (seed >> 32)));
            Func<Vector3D> unit = () =>
                Vector3D.Normalize(
                    new Vector3D(
                        random.NextDouble() - 0.5,
                        random.NextDouble() - 0.5,
                        random.NextDouble() - 0.5
                    )
                );
            var web = new Web();
            web.Monitor = (state, on) =>
            {
                web.Monitored = true;
                // Any lower state that goes off takes Hit with it
                if (on ? state == TCApi.Hit : state <= TCApi.Hit)
                    web.Hitting = on;
            };
            for (var i = 0; i < Threads; i++)
            {
                web.Ends[i] = unit() * Spread * Math.Sqrt(random.NextDouble());
                web.Bows[i] = unit();
                web.Shifts[i] = unit() * WaveLength * 0.2 * random.NextDouble();
            }
            for (var k = 0; k < Strands; k++)
            {
                web.StrandThreads[k] = random.Next(Threads);
                web.StrandAt[2 * k] = 0.2 + 0.75 * random.NextDouble();
                web.StrandAt[2 * k + 1] = 0.2 + 0.75 * random.NextDouble();
            }
            return web;
        }

        // The ring's dummies, the deconstructor's with a "destro" prefix; the block's center
        // for a missing one
        Vector3D[] AnchorsOf(IMyTerminalBlock tool)
        {
            dummies.Clear();
            tool.Model.GetDummies(dummies);
            var anchors = new Vector3D[Threads];
            for (var i = 0; i < Threads; i++)
            {
                IMyModelDummy dummy;
                if (
                    dummies.TryGetValue(Anchors[i], out dummy)
                    || dummies.TryGetValue("destro" + Anchors[i], out dummy)
                )
                    anchors[i] = dummy.Matrix.Translation;
            }
            return anchors;
        }

        static bool Shooting(IMyTerminalBlock tool)
        {
            var shoot = tool.GetProperty("ToolCore_Shoot")?.AsBool();
            return shoot != null && shoot.GetValue(tool);
        }

        // The emitter dummy's world matrix, on the turret ball's subpart
        bool Emitter(IMyEntity part, out MatrixD matrix)
        {
            dummies.Clear();
            part.Model?.GetDummies(dummies);
            foreach (var name in Emitters)
            {
                IMyModelDummy dummy;
                if (dummies.TryGetValue(name, out dummy))
                {
                    matrix = (MatrixD)dummy.Matrix * part.WorldMatrix;
                    return true;
                }
            }
            var entity = part as MyEntity;
            if (entity != null)
                foreach (var subpart in entity.Subparts.Values)
                    if (Emitter(subpart, out matrix))
                        return true;
            matrix = MatrixD.Identity;
            return false;
        }
    }
}
