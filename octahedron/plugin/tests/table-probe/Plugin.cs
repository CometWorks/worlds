using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using ParallelTasks;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using VRage.FileSystem;
using VRage.Plugins;

namespace TableProbe;

// Test only, for tests/test_table_watchers.py (SE-1195, SE-1196): one line per call that sets,
// clears, hides or activates a projector's clipboard, and OVERLAP with the caller's stack when
// one that changes the clipboard comes while its activation still runs on a worker thread. That
// overlap throws in the activation ("Blueprint can't be found") or leaves a stale hologram.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private static StreamWriter log;
    private static readonly object writeLock = new();

    private static readonly AccessTools.FieldRef<MyGridClipboard, Task> Activation =
        AccessTools.FieldRefAccess<MyGridClipboard, Task>("ActivationTask");

    private static readonly string[] Harmless =
    {
        "OnOffsetChangedSuccess",
        "InitializeClipboard",
        "ActivateNoAlign",
        "Show",
    };

    public void Init(object gameInstance)
    {
        var harmony = new Harmony("TableProbe");
        var clip = new HarmonyMethod(typeof(Plugin), nameof(ClipboardCall));
        foreach (
            var name in new[]
            {
                "ChangeClipboardPreview",
                "SetGridFromBuilders",
                "ClearClipboard",
                "Deactivate",
                "Hide",
                "Show",
                "ActivateNoAlign",
            }
        )
            harmony.Patch(AccessTools.Method(typeof(MyGridClipboard), name), prefix: clip);
        var projector = new HarmonyMethod(typeof(Plugin), nameof(ProjectorCall));
        foreach (
            var name in new[]
            {
                "SetNewBlueprint",
                "RemoveProjection",
                "InitializeClipboard",
                "OnNewBlueprintSuccess",
                "OnRemoveProjectionRequest",
                "OnOffsetChangedSuccess",
            }
        )
            harmony.Patch(AccessTools.Method(typeof(MyProjectorBase), name), prefix: projector);
        Write("READY");
    }

    public void Update() { }

    public void Dispose() => log?.Dispose();

    private static void Write(string line)
    {
        lock (writeLock)
        {
            log ??= new StreamWriter(
                Path.Combine(MyFileSystem.UserDataPath, "table-probe.log"),
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

    private static void ClipboardCall(MethodBase __originalMethod, MyGridClipboard __instance)
    {
        if (__instance is not MyProjectorClipboard projectorClipboard)
            return;
        Report(__originalMethod.Name, projectorClipboard.Projector, __instance);
    }

    private static void ProjectorCall(MethodBase __originalMethod, MyProjectorBase __instance) =>
        Report(__originalMethod.Name, __instance, __instance.Clipboard);

    private static void Report(string what, MyProjectorBase projector, MyGridClipboard clipboard)
    {
        var activating = clipboard != null && !Activation(clipboard).IsComplete;
        var line =
            $"{what} {projector?.EntityId} activating={activating} copied={clipboard?.CopiedGrids.Count} preview={clipboard?.PreviewGrids.Count}";
        // While the worker runs, only its own preview change and calls the game ignores or that
        // leave the clipboard's grids alone may come
        if (
            activating
            && (
                what == "ChangeClipboardPreview"
                    ? Thread.CurrentThread.ManagedThreadId == 1
                    : !Harmless.Contains(what)
            )
        )
            line += " OVERLAP\n" + new StackTrace(2, false);
        Write(line);
    }
}
