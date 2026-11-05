using System;
using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace OctahedronFilaments
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

        class Web
        {
            public Vector3D Hit;
            public bool Active;

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
        int tick;

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
                web.Active = false;
                if (
                    !tool.IsWorking
                    || Vector3D.DistanceSquared(camera, tool.GetPosition())
                        > DrawDistance * DrawDistance
                    || !Shooting(tool)
                )
                    continue;
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
                // The nearest block of another grid
                var nearest = double.MaxValue;
                foreach (var hit in hits)
                {
                    var grid = hit.HitEntity?.GetTopMostParent() as IMyCubeGrid;
                    if (grid == null || grid == tool.CubeGrid || hit.Fraction >= nearest)
                        continue;
                    nearest = hit.Fraction;
                    web.Hit = hit.Position;
                    web.Active = true;
                }
            }
            if (webs.Count > Tools.Count)
                foreach (var tool in new List<IMyTerminalBlock>(webs.Keys))
                    if (!Tools.Contains(tool))
                        webs.Remove(tool);
        }

        public override void Draw()
        {
            if (MyAPIGateway.Utilities.IsDedicated)
                return;
            var time = MyAPIGateway.Session.ElapsedPlayTime.TotalSeconds;
            foreach (var pair in webs)
                if (pair.Value.Active && !pair.Key.Closed)
                    Draw(pair.Key, pair.Value, time);
        }

        void Draw(IMyTerminalBlock tool, Web web, double time)
        {
            if (web.Anchors == null)
                web.Anchors = AnchorsOf(tool);
            for (var i = 0; i < Threads; i++)
            {
                var start = Vector3D.Transform(web.Anchors[i], tool.WorldMatrix);
                var end = web.Hit + web.Ends[i];
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
                for (var s = 0; s < Segments; s++)
                    Line(points[i, s], points[i, s + 1], ThreadWidth);
            }
            for (var k = 0; k < Strands; k++)
            {
                var i = web.StrandThreads[k];
                var j = (i + 1) % Threads;
                Line(
                    points[i, (int)(web.StrandAt[2 * k] * Segments)],
                    points[j, (int)(web.StrandAt[2 * k + 1] * Segments)],
                    StrandWidth
                );
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
