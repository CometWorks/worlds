using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using PluginSdk.Logging;
using Sandbox.Game.World;
using VRage.Network;
using VRageMath;

namespace ServerPlugin;

/// <summary>
/// Patches MES at runtime so its spawns follow the <see cref="CellPattern"/> and the NPC density limits.
///
/// Every MES spawn that is not a static encounter places its grids through
/// <c>PathPlacements.GetSpawnPlacement</c>, called from <c>SpawnRequest.CalculateSpawn</c>, which gives up
/// when the returned <c>PathDetails.ValidPath</c> is false. A postfix checks the placement MES computed:
/// the spawn point, and the end of the path for cargo ships, must lie in one cell core, and the NPC
/// density there must be under the limits. If not, it lets MES compute up to PlacementAttempts more
/// placements (MES's own obstruction checks included) and takes the first that fits; if none does, it
/// marks the placement invalid and MES skips that spawn.
///
/// MES is a Workshop mod compiled at session load, so nothing here references it: the types are looked up
/// by name in the compiled mod assemblies. Before patching, the MES version and a hash of the
/// instructions of the methods the patch relies on must match the accepted values in the config. On a
/// mismatch, or if anything fails later, the plugin logs a Critical line and MES runs unpatched.
/// </summary>
internal static class MesSpawnGate
{
    private const string PathPlacementsType = "ModularEncountersSystems.Spawning.PathPlacements";
    private const string PathDetailsType = "ModularEncountersSystems.Spawning.PathDetails";
    private const string SpawnRequestType = "ModularEncountersSystems.Spawning.SpawnRequest";
    private const string SessionCoreType = "ModularEncountersSystems.Core.MES_SessionCore";

    private static Logger Log => Plugin.Log;

    private static FieldInfo validPath,
        startCoords,
        pathDirection,
        pathDistance,
        spawnType,
        pathDebugging;
    private static FieldInfo[] pathFields;

    [ThreadStatic]
    private static bool reentered;

    public static bool Active { get; private set; }
    public static CellPattern Pattern { get; private set; }

    // Totals since start, logged by Plugin
    public static long Accepted,
        Moved,
        RefusedPattern,
        RefusedDensity,
        RefusedCluster,
        RefusedUnknown;

    private enum Verdict
    {
        Fits,
        Pattern,
        Density,
        Cluster,
        Unknown,
    }

    /// <summary>Looks for MES and patches it. Call once, after the session has loaded.</summary>
    public static void Install(Harmony harmony, OctahedronConfig config)
    {
        var assembly = MyScriptManager.Static?.Scripts.Values.FirstOrDefault(a =>
            a.GetType(PathPlacementsType) != null
        );
        if (assembly == null)
        {
            Log.Info("MES (Workshop 1521905890) is not loaded in this world: nothing to patch");
            return;
        }

        var placements = assembly.GetType(PathPlacementsType);
        var details = assembly.GetType(PathDetailsType);
        var getSpawnPlacement = placements.GetMethod(
            "GetSpawnPlacement",
            BindingFlags.Public | BindingFlags.Static
        );
        var calculateSpawn = assembly
            .GetType(SpawnRequestType)
            ?.GetMethod("CalculateSpawn", BindingFlags.Public | BindingFlags.Static);
        var version =
            assembly
                .GetType(SessionCoreType)
                ?.GetField("ModVersion", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as string;

        validPath = details?.GetField("ValidPath");
        startCoords = details?.GetField("StartCoords");
        pathDirection = details?.GetField("PathDirection");
        pathDistance = details?.GetField("PathDistance");
        spawnType = details?.GetField("SpawnType");
        pathDebugging = details?.GetField("PathDebugging");

        if (
            getSpawnPlacement == null
            || getSpawnPlacement.ReturnType != details
            || calculateSpawn == null
            || validPath == null
            || startCoords == null
            || pathDirection == null
            || pathDistance == null
            || spawnType == null
        )
        {
            Log.Critical(
                $"MES {version ?? "(no version)"} no longer has the methods or fields this plugin patches. MES runs UNPATCHED: NPC spawns are not gated"
            );
            return;
        }

        pathFields = details.GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        );

        string hash = InstructionHash(getSpawnPlacement, calculateSpawn, details);
        Log.Info($"MES {version} found, instruction hash {hash}");

        if (
            !Split(config.MesAcceptedVersions).Contains(version ?? "")
            || !Split(config.MesAcceptedMethodHashes).Contains(hash)
        )
        {
            Log.Critical(
                $"MES {version} with instruction hash {hash} was not checked against this plugin "
                    + $"(accepted versions: {config.MesAcceptedVersions}; hashes: {config.MesAcceptedMethodHashes}). "
                    + "MES runs UNPATCHED: NPC spawns are not gated. Review the MES change, then add the version and hash to the config"
            );
            return;
        }

        if (!Configure(config))
            return;

        harmony.Patch(
            getSpawnPlacement,
            postfix: new HarmonyMethod(typeof(MesSpawnGate), nameof(Postfix))
        );
        Active = true;
        string gapSource =
            config.MesGap > 0
                ? "set in metres"
                : $"{config.MesGapSyncDistances} x sync distance {MyLayers.GetSyncDistance()} m";
        Log.Info(
            $"MES spawn placements gated: cell {Pattern.CellSize} m, gap {Pattern.Gap} m ({gapSource}), jitter {Pattern.Jitter}, seed {Pattern.Seed}, "
                + $"pattern fingerprint {Pattern.Fingerprint}, "
                + $"at most {config.MesMaxNpcGridsInRadius} NPC grids within {config.MesDensityRadius} m, cluster cap {config.MesMaxClusterNpcGrids}, "
                + $"census {(NpcCensus.ClusterWide ? "cluster-wide" : "this process only")}"
        );
    }

    /// <summary>
    /// Builds the cell pattern from the config; false (and a Critical line) when it cannot form one. Called
    /// once, at install: every node of a cluster must cut space the same way, so a changed pattern setting
    /// takes effect at the next start, when all nodes read the same profile.
    /// </summary>
    public static bool Configure(OctahedronConfig config)
    {
        try
        {
            Pattern = CellPattern.FromSettings(
                config.MesCellSize,
                config.MesGap,
                config.MesGapSyncDistances,
                MyLayers.GetSyncDistance(),
                config.MesJitter,
                config.MesSeed
            );
        }
        catch (ArgumentOutOfRangeException e)
        {
            Active = false;
            Log.Critical(
                $"Invalid spawn pattern (cell {config.MesCellSize} m, gap {config.MesGap} m or {config.MesGapSyncDistances} sync distances, "
                    + $"jitter {config.MesJitter}): {e.Message}. MES runs UNGATED"
            );
            return false;
        }

        return true;
    }

    // ReSharper disable InconsistentNaming
    private static void Postfix(object __result, object[] __args, MethodBase __originalMethod)
    // ReSharper restore InconsistentNaming
    {
        if (
            reentered
            || !Active
            || !Plugin.Config.Enabled
            || !Plugin.Config.MesGateEnabled
            || __result == null
        )
            return;

        try
        {
            string type = __args[0].ToString();
            if (
                Split(Plugin.Config.MesExemptSpawnTypes).Contains(type)
                || !(bool)validPath.GetValue(__result)
            )
                return;

            var config = Plugin.Config;
            var verdict = Check(__result, config, out var cell, out var near);
            int attempts = 1;

            // A full cluster stays full wherever MES looks, and an unknown census stays unknown
            if (verdict is Verdict.Pattern or Verdict.Density)
            {
                reentered = true;
                try
                {
                    for (
                        ;
                        attempts < config.MesPlacementAttempts && verdict != Verdict.Fits;
                        attempts++
                    )
                    {
                        var args = (object[])__args.Clone();
                        var candidate = __originalMethod.Invoke(null, args);
                        if (candidate == null || !(bool)validPath.GetValue(candidate))
                            continue;

                        var candidateVerdict = Check(
                            candidate,
                            config,
                            out var candidateCell,
                            out var candidateNear
                        );
                        if (candidateVerdict == Verdict.Fits)
                        {
                            foreach (var field in pathFields)
                                field.SetValue(__result, field.GetValue(candidate));
                            cell = candidateCell;
                        }
                        near = candidateNear;
                        // Keep the most informative refusal: density beats pattern
                        if (candidateVerdict != Verdict.Pattern || verdict == Verdict.Pattern)
                            verdict = candidateVerdict;
                        if (verdict is Verdict.Cluster or Verdict.Unknown)
                            break;
                    }
                }
                finally
                {
                    reentered = false;
                }
            }

            var start = (Vector3D)startCoords.GetValue(__result);
            if (verdict == Verdict.Fits)
            {
                if (attempts == 1)
                    Accepted++;
                else
                    Moved++;
                NpcCensus.Reserve(start, PrefabCount(__args[2]));
                Log.Info(
                    $"spawn placed: type {type}, cell {cell}, start {Format(start)}, placement {attempts}, near {near}"
                );
                return;
            }

            validPath.SetValue(__result, false);
            (pathDebugging?.GetValue(__result) as StringBuilder)?.AppendLine(
                $" - MesCluster: refused ({verdict}) after {attempts} placements"
            );
            switch (verdict)
            {
                case Verdict.Pattern:
                    RefusedPattern++;
                    break;
                case Verdict.Density:
                    RefusedDensity++;
                    break;
                case Verdict.Cluster:
                    RefusedCluster++;
                    break;
                default:
                    RefusedUnknown++;
                    break;
            }
            Log.Info(
                $"spawn refused: type {type}, reason {verdict}, placements {attempts}, last start {Format(start)}, near {near}"
            );
        }
        catch (Exception e)
        {
            Active = false;
            Log.Critical("Spawn gate failed; MES runs UNGATED from now on", e);
        }
    }

    /// <summary><paramref name="near"/>: NPC grids within DensityRadius, -1 when not counted.</summary>
    private static Verdict Check(
        object path,
        OctahedronConfig config,
        out (long, long, long)? cell,
        out int near
    )
    {
        var start = (Vector3D)startCoords.GetValue(path);
        cell = Pattern.CoreOf(start.X, start.Y, start.Z);
        near = -1;

        if (config.MesMaxClusterNpcGrids > 0)
        {
            int total = NpcCensus.Count(start, double.PositiveInfinity);
            if (total < 0)
                return Verdict.Unknown;
            if (total >= config.MesMaxClusterNpcGrids)
                return Verdict.Cluster;
        }

        if (cell == null)
            return Verdict.Pattern;

        // A cargo ship flies a straight path; both ends in one convex core keep all of it there
        if (spawnType.GetValue(path).ToString().Contains("CargoShip"))
        {
            var end =
                start
                + (Vector3D)pathDirection.GetValue(path) * (double)pathDistance.GetValue(path);
            if (!cell.Equals(Pattern.CoreOf(end.X, end.Y, end.Z)))
                return Verdict.Pattern;
        }

        if (config.MesMaxNpcGridsInRadius > 0)
        {
            near = NpcCensus.Count(start, config.MesDensityRadius);
            if (near < 0)
                return Verdict.Unknown;
            if (near >= config.MesMaxNpcGridsInRadius)
                return Verdict.Density;
        }

        return Verdict.Fits;
    }

    /// <summary>Grids the spawn group will add: SpawnGroupCollection.PrefabIndexes, at least 1.</summary>
    private static int PrefabCount(object collection) =>
        Math.Max(
            1,
            (
                collection?.GetType().GetField("PrefabIndexes")?.GetValue(collection)
                as System.Collections.ICollection
            )?.Count
                ?? 1
        );

    /// <summary>
    /// SHA-256 over the instructions of the patched method and of its caller, with operands resolved to
    /// names, plus the PathDetails field list: stable across unrelated MES changes and compilations, but
    /// any change to the code this patch relies on gives a new hash.
    /// </summary>
    private static string InstructionHash(MethodInfo patched, MethodInfo caller, Type details)
    {
        var text = new StringBuilder();
        foreach (var method in new[] { patched, caller })
        {
            text.AppendLine(method.ToString());
            foreach (var instruction in PatchProcessor.GetOriginalInstructions(method))
                text.Append(instruction.opcode)
                    .Append(' ')
                    .Append(Operand(instruction.operand))
                    .AppendLine();
        }
        foreach (var field in details.GetFields().OrderBy(f => f.Name, StringComparer.Ordinal))
            text.Append(field.FieldType.Name).Append(' ').AppendLine(field.Name);

        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
        return BitConverter.ToString(digest, 0, 8).Replace("-", "").ToLowerInvariant();
    }

    private static string Operand(object operand) =>
        operand switch
        {
            null => "",
            MemberInfo member => member.DeclaringType?.FullName + "::" + member,
            System.Reflection.Emit.Label => "label",
            System.Reflection.Emit.LocalBuilder local => "local " + local.LocalIndex,
            _ => operand.ToString(),
        };

    private static IEnumerable<string> Split(string list) =>
        (list ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);

    private static string Format(Vector3D v) => $"{v.X:F0},{v.Y:F0},{v.Z:F0}";
}
