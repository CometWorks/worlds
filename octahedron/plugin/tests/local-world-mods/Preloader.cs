// ReSharper disable CheckNamespace

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Mono.Cecil;
using Sandbox.Engine.Networking;
using VRage.Game;
using VRage.GameServices;

// Must not be in a namespace, or Magnetar does not find it.
//
// The dedicated server loads its first world, mods included, before it calls
// IPlugin.Init, so the patch goes in from the preloader's Finish hook.
// ReSharper disable once UnusedType.Global
public static class Preloader
{
    // ReSharper disable once UnusedMember.Global
    public static IEnumerable<string> TargetDLLs { get; } = [];

    // ReSharper disable once UnusedMember.Global
    public static void Patch(AssemblyDefinition asmDef) { }

    // ReSharper disable once UnusedMember.Global
    public static void Finish()
    {
        new Harmony("LocalWorldMods").Patch(
            AccessTools.Method(
                typeof(MyWorkshop),
                nameof(MyWorkshop.DownloadWorldModsBlockingInternal)
            ),
            prefix: new HarmonyMethod(typeof(Preloader), nameof(Prefix))
        );
        Console.WriteLine("[LocalWorldMods] world mods are taken from the Mods folder");
    }

    // Same approach as LocalWorldModsPatch of the cluster plugin: skip the download
    // and attach a stand-in Workshop item pointing at <data>/Mods/<id>.sbm
    private static bool Prefix(
        List<MyObjectBuilder_Checkpoint.ModItem> mods,
        ref MyWorkshop.ResultData __result
    )
    {
        __result = new MyWorkshop.ResultData(MyGameServiceCallResult.OK, false);
        if (mods == null)
            return false;
        for (int i = 0; i < mods.Count; i++)
        {
            // ModItem is a struct: work on a copy and write it back
            MyObjectBuilder_Checkpoint.ModItem mod = mods[i];
            string path = mod.GetPath();
            if (mod.PublishedFileId == 0 || !Directory.Exists(path))
            {
                Console.WriteLine(
                    $"[LocalWorldMods] MISSING id={mod.PublishedFileId} name={mod.Name} path={path}"
                );
                __result = new MyWorkshop.ResultData(MyGameServiceCallResult.Fail, false);
                return false;
            }
            var item = new MyWorkshopItem
            {
                Id = mod.PublishedFileId,
                Title = mod.FriendlyName ?? mod.Name,
            };
            AccessTools
                .PropertySetter(typeof(MyWorkshopItem), nameof(MyWorkshopItem.Folder))
                .Invoke(item, [path]);
            ulong size = (ulong)
                new DirectoryInfo(path)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
            AccessTools
                .PropertySetter(typeof(MyWorkshopItem), nameof(MyWorkshopItem.Size))
                .Invoke(item, [size]);
            mod.SetModData(item);
            mods[i] = mod;
            Console.WriteLine(
                $"[LocalWorldMods] local id={mod.PublishedFileId} name={mod.Name} path={path}"
            );
        }
        return false;
    }
}
