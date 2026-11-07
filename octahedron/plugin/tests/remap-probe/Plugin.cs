using System;
using System.IO;
using System.Threading;
using HarmonyLib;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using VRage.FileSystem;
using VRage.Network;
using VRage.Plugins;

namespace RemapProbe;

// Test only, for tests/test_first_load_mgp.py (SE-1248). A client's blueprint set into a projector on
// the server starts the clipboard's activation, whose worker thread remaps the blueprint's ids in place.
// The probe holds that worker right after it remapped a rotor, piston or hinge base, so the base's
// TopBlockId is new while its head still has the old id, until the main thread has run every
// SetNewBlueprint postfix. The interleaving is otherwise down to timing: it refused a player's first
// blueprint from Multigrid Projector, while the check still ran in the postfix.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private static StreamWriter log;
    private static readonly object writeLock = new();
    private static readonly ManualResetEventSlim reached = new();
    private static readonly ManualResetEventSlim release = new();
    private static volatile Thread main;

    public void Init(object gameInstance)
    {
        var harmony = new Harmony("RemapProbe");
        var setNewBlueprint = AccessTools.Method(typeof(MyProjectorBase), "SetNewBlueprint");
        harmony.Patch(
            setNewBlueprint,
            prefix: new HarmonyMethod(typeof(Plugin), nameof(Arm)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(Wait)) { priority = Priority.First }
        );
        harmony.Patch(
            setNewBlueprint,
            postfix: new HarmonyMethod(typeof(Plugin), nameof(Release)) { priority = Priority.Last }
        );
        harmony.Patch(
            AccessTools.Method(typeof(MyObjectBuilder_MechanicalConnectionBlock), "Remap"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(Hold))
        );
        Write("READY");
    }

    public void Update() { }

    public void Dispose() => log?.Dispose();

    private static void Write(string line)
    {
        lock (writeLock)
        {
            log ??= new StreamWriter(
                Path.Combine(MyFileSystem.UserDataPath, "remap-probe.log"),
                true
            )
            {
                AutoFlush = true,
            };
            log.WriteLine(
                $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0:F3} t{Thread.CurrentThread.ManagedThreadId} {line}"
            );
        }
    }

    private static void Arm()
    {
        if (!Sync.IsServer || MyEventContext.Current.IsLocallyInvoked)
            return;
        reached.Reset();
        release.Reset();
        main = Thread.CurrentThread;
    }

    // Before the other postfixes: the worker has remapped a base and waits
    private static void Wait()
    {
        if (main != Thread.CurrentThread)
            return;
        Write(reached.Wait(5000) ? "HELD" : "NOT REACHED");
    }

    private static void Release()
    {
        if (main != Thread.CurrentThread)
            return;
        main = null;
        release.Set();
        Write("RELEASED");
    }

    private static void Hold()
    {
        var thread = main;
        if (thread == null || thread == Thread.CurrentThread || reached.IsSet)
            return;
        reached.Set();
        release.Wait(10000);
    }
}
