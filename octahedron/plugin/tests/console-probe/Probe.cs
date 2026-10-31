using System;
using System.IO;
using System.Linq;
using PluginSdk.Commands;
using Sandbox.Game.World;
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
///   anything else    a chat command line, for example "!oct market show Alpha"
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
        if (path == null || outPath == null || (only != null && node != only))
            return;
        planPath = path;
        output = new StreamWriter(outPath, append: true) { AutoFlush = true };
    }

    public void Update()
    {
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

    // A reply that comes later belongs to the command whose step was current when it was sent; the
    // commands answer once, so the test waits for each answer before the next command
    public void Send(in CommandReply reply, in CommandCaller caller) => Write(replying, reply.Text);

    private void Write(int index, string text) =>
        output.WriteLine(index + "\t" + (text ?? "").Replace("\n", " | "));

    public void Dispose() => output?.Dispose();
}
