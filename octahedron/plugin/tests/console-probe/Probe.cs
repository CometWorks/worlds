using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using PluginSdk.Commands;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Plugins;

namespace ConsoleProbe;

/// <summary>
/// Test hook of the cluster tests (tests/console_probe.py): runs the chat command lines of
/// the file CONSOLEPROBE_PLAN as the server console, owner level, once the session is ready, on the node
/// CONSOLEPROBE_NODE (CLUSTER_NODE_ID) only. Lines appended later run as they arrive. Every reply goes to
/// CONSOLEPROBE_OUT as "STEP\tTEXT", STEP counting the plan's command lines from 0; replies of a command
/// that answers later (a change on the World Authority) come when they come.
///   wait SECONDS     let the game run
///   clock DAYS       moves the clock that ages offline partitions in the contracts, a test's week
///   anything else    a chat command line, for example "!oct market show Alpha"
///   pculimit N       sets the player's PCU limit in the contracts, so a test needn't own a million PCU
/// Grid ids in the file CONSOLEPROBE_PLAN.convert become ships on the process holding them, as the game's
/// Convert to Ship does, and are written to CONSOLEPROBE_PLAN.converted; on every process it is loaded on.
/// Lines "tug grid player" in CONSOLEPROBE_PLAN.tow give the tug to the player and join it to the grid as a
/// connector would, on the process holding the tug, and are written to CONSOLEPROBE_PLAN.towed.
/// While the file CONSOLEPROBE_PLAN.air exists, suits stay charged and full on every process: long tests outlast them.
/// </summary>
public class Probe : IPlugin, ICommandResponder
{
    private string planPath;
    private string[] plan = [];
    private long planLength = -1;
    private int line;
    private int step;
    private DateTime resumeAt = DateTime.MaxValue;
    private StreamWriter output;
    private int replying;
    private string convertPath;
    private string planPathAll;
    private DateTime nextConvert;

    private static readonly CommandCaller Console = new(
        0UL,
        0L,
        "ConsoleProbe",
        MyPromoteLevel.Owner,
        isConsole: true
    );

    public void Init(object gameInstance)
    {
        var path = Environment.GetEnvironmentVariable("CONSOLEPROBE_PLAN");
        var outPath = Environment.GetEnvironmentVariable("CONSOLEPROBE_OUT");
        var node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID");
        var only = Environment.GetEnvironmentVariable("CONSOLEPROBE_NODE");
        convertPath = path == null ? null : path + ".convert";
        planPathAll = path;
        if (path == null || outPath == null || (only != null && node != only))
            return;
        planPath = path;
        output = new StreamWriter(outPath, append: true) { AutoFlush = true };
    }

    public void Update()
    {
        Convert();
        Tow();
        if (planPath == null || MySession.Static is not { Ready: true })
            return;
        if (line >= plan.Length)
        {
            long length = File.Exists(planPath) ? new FileInfo(planPath).Length : 0;
            if (length == planLength)
                return;
            planLength = length;
            plan = File.ReadAllLines(planPath)
                .Where(l => l.Trim().Length > 0 && !l.StartsWith("#"))
                .ToArray();
            if (line >= plan.Length)
                return;
        }
        if (resumeAt == DateTime.MaxValue)
            resumeAt = DateTime.UtcNow.AddSeconds(10);
        if (DateTime.UtcNow < resumeAt)
            return;

        string next = plan[line++].Trim();
        if (next.StartsWith("wait "))
        {
            resumeAt = DateTime.UtcNow.AddSeconds(double.Parse(next.Substring(5)));
            return;
        }
        if (next.StartsWith("pculimit "))
        {
            ContractsField("PcuLimitForTests")
                .SetValue(null, int.Parse(next.Substring(9), CultureInfo.InvariantCulture));
            return;
        }
        if (next.StartsWith("clock "))
        {
            ShiftContractClock(double.Parse(next.Substring(6), CultureInfo.InvariantCulture));
            return;
        }
        replying = step++;
        try
        {
            if (!ServerCommands.Execute(next, Console, this))
                Write(replying, "not a command");
        }
        catch (Exception e)
        {
            Write(replying, "error " + e.Message);
        }
    }

    private void Convert()
    {
        var now = DateTime.UtcNow;
        if (convertPath == null || now < nextConvert || MySession.Static is not { Ready: true })
            return;
        nextConvert = now.AddSeconds(5);
        if (File.Exists(planPathAll + ".air"))
            foreach (var player in MySession.Static.Players.GetOnlinePlayers())
                if (player.Character is { OxygenComponent: not null } character)
                {
                    character.OxygenComponent.SuitOxygenAmount = character
                        .OxygenComponent
                        .OxygenCapacity;
                    character.SuitBattery.ResourceSource.SetRemainingCapacityByType(
                        MyResourceDistributorComponent.ElectricityId,
                        MyEnergyConstants.BATTERY_MAX_CAPACITY
                    );
                }
        if (!File.Exists(convertPath))
            return;
        foreach (var text in File.ReadAllLines(convertPath))
            if (
                long.TryParse(text, out var id)
                && MyEntities.TryGetEntityById(id, out var entity)
                && entity is MyCubeGrid { IsStatic: true, Physics: not null } grid
            )
            {
                grid.OnConvertToDynamic();
                File.AppendAllText(convertPath + "ed", $"{id}\n");
            }
    }

    private readonly System.Collections.Generic.HashSet<string> towed = [];

    private void Tow()
    {
        var path = planPathAll + ".tow";
        if (planPathAll == null || !File.Exists(path) || MySession.Static is not { Ready: true })
            return;
        foreach (var text in File.ReadAllLines(path))
        {
            var p = text.Split(' ');
            if (
                p.Length != 3
                || towed.Contains(text)
                || !MyEntities.TryGetEntityById(long.Parse(p[0]), out var a)
                || a is not MyCubeGrid tug
                || !MyEntities.TryGetEntityById(long.Parse(p[1]), out var b)
                || b is not MyCubeGrid grid
            )
                continue;
            towed.Add(text);
            tug.ChangeGridOwnership(long.Parse(p[2]), MyOwnershipShareModeEnum.Faction);
            MyCubeGrid.CreateGridGroupLink(GridLinkTypeEnum.Physical, tug.EntityId, tug, grid);
            File.AppendAllText(planPathAll + ".towed", text + "\n");
        }
    }

    private static void ShiftContractClock(double days) =>
        ContractsField("OfflineClockShift").SetValue(null, TimeSpan.FromDays(days));

    private static FieldInfo ContractsField(string name) =>
        AppDomain
            .CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("ServerPlugin.Contracts"))
            .First(t => t != null)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Static);

    // A reply that comes later belongs to the command whose step was current when it was sent; the
    // commands answer once, so the test waits for each answer before the next command
    public void Send(in CommandReply reply, in CommandCaller caller) => Write(replying, reply.Text);

    private void Write(int index, string text) =>
        output.WriteLine(index + "\t" + (text ?? "").Replace("\n", " | "));

    public void Dispose() => output?.Dispose();
}
