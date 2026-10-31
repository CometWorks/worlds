using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.FileSystem;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Components;
using VRage.ModAPI;
using VRage.Plugins;

namespace BeamProbe;

// Test only, for tests/test_shipyard_beams.py: one line a second with what ToolCore's own
// state says about the tools of shipyard grids, read by reflection as they are the mod's
// internals, and one line per safe zone event about a shipyard grid.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private const BindingFlags All =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static StreamWriter log;
    private int frame;
    private Type session;

    public void Init(object gameInstance)
    {
        var harmony = new Harmony("BeamProbe");
        foreach (
            var name in new[]
            {
                "InsertEntity_Implementation",
                "RemoveEntity_Implementation",
                "SendInsertedEntity",
                "SendRemovedEntity",
            }
        )
            harmony.Patch(
                AccessTools.Method(typeof(MySafeZone), name),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(ZoneEvent))
            );
        harmony.Patch(
            AccessTools.Method(typeof(MyModAPIHelper.MyMultiplayer), "SendMessageTo"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(ToolCoreSent))
        );
    }

    public void Update()
    {
        if (++frame % 60 != 0 || MySession.Static == null || !MySession.Static.Ready)
            return;
        try
        {
            Sample();
        }
        catch (Exception e)
        {
            Write($"ERROR {e.GetType().Name}: {e.Message}");
        }
    }

    public void Dispose() => log?.Dispose();

    private static void Write(string line)
    {
        log ??= new StreamWriter(Path.Combine(MyFileSystem.UserDataPath, "beam-probe.log"), true)
        {
            AutoFlush = true,
        };
        log.WriteLine($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0:F1} {line}");
    }

    // The shipyard the test builds at, BEAM_PROBE_YARD in the environment
    private static readonly string Yard = Environment.GetEnvironmentVariable("BEAM_PROBE_YARD");

    private static bool IsShipyard(MyCubeGrid grid) => grid?.DisplayName == Yard;

    private static void ZoneEvent(MethodBase __originalMethod, long entityId)
    {
        if (MyEntities.GetEntityById(entityId) is MyCubeGrid grid && IsShipyard(grid))
            Write($"ZONE {__originalMethod.Name} {grid.DisplayName}");
    }

    // ToolCore's channel to the clients
    private const ushort ToolCoreClients = 65352;
    private static long sent;

    private static void ToolCoreSent(ushort id)
    {
        if (id == ToolCoreClients)
            sent++;
    }

    private static object Get(object o, string name)
    {
        var type = o.GetType();
        return (object)AccessTools.Field(type, name)?.GetValue(o)
            ?? AccessTools.Property(type, name)?.GetValue(o);
    }

    private void Sample()
    {
        session ??= AppDomain
            .CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("ToolCore.Session.ToolSession"))
            .LastOrDefault(t => t != null);
        var instance = session?.GetField("Instance", All)?.GetValue(null);
        if (instance == null)
            return;

        int tools = 0,
            activated = 0,
            track = 0,
            powered = 0,
            enabled = 0,
            functional = 0;
        int hitting = 0,
            target = 0,
            stocked = 0;
        var replicated = 0;
        MyCubeGrid yard = null;
        foreach (var comp in (IEnumerable)Get(Get(instance, "ToolMap"), "Values"))
        {
            var grid = Get(comp, "Grid") as MyCubeGrid;
            if (!IsShipyard(grid))
                continue;
            yard = grid;
            tools++;
            activated += (bool)Get(comp, "_activated") ? 1 : 0;
            track += (bool)Get(comp, "_trackTargets") ? 1 : 0;
            powered += (bool)Get(comp, "Powered") ? 1 : 0;
            enabled += (bool)Get(comp, "Enabled") ? 1 : 0;
            functional += (bool)Get(comp, "Functional") ? 1 : 0;
            hitting += (bool)Get(comp, "WasHitting") ? 1 : 0;
            stocked += Get(comp, "Inventory") is IMyInventory i && i.ItemCount > 0 ? 1 : 0;
            replicated += ((ICollection)Get(comp, "ReplicatedClients")).Count;
            foreach (var mode in (IEnumerable)Get(Get(comp, "ModeMap"), "Values"))
                if (Get(mode, "Turret") is { } turret && (bool)Get(turret, "HasTarget"))
                {
                    target++;
                    break;
                }
        }
        if (yard == null)
            return;
        var allowed = MySessionComponentSafeZones.IsActionAllowed(yard, MySafeZoneAction.Welding);
        Write(
            $"TOOLS tools={tools} activated={activated} track={track} powered={powered} "
                + $"enabled={enabled} functional={functional} target={target} hitting={hitting} "
                + $"stocked={stocked} replicated={replicated} sent={sent} zone_allows={allowed} {Ships(yard)}"
        );
    }

    // The arm and the ship the shipyard builds: what's mechanically connected to it, and what
    // of that isn't complete yet
    private static string Ships(MyCubeGrid yard)
    {
        int blocks = 0,
            unfinished = 0;
        foreach (var grid in MyCubeGridGroups.Static.Mechanical.GetGroupNodes(yard))
            if (grid != yard)
            {
                blocks += grid.CubeBlocks.Count;
                unfinished += grid.CubeBlocks.Count(b => !b.IsFullIntegrity);
            }
        return $"ship={blocks} unfinished={unfinished}";
    }
}
