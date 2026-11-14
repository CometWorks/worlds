using System;
using System.Text;
using System.Threading.Tasks;
using Sandbox.Game;
using Sandbox.Game.World;

namespace ServerPlugin;

// Good.bot tells the players a hint in global chat now and then (SE-1538): one of Hints in the
// config, after a random wait between HintMinMinutes and HintMaxMinutes, never the one it said
// last. The timer runs in one process, a plain server or the World Authority of a cluster, which
// has no players of its own: it broadcasts the hint and every node says it to the players it has,
// so each player hears it once. It is scripted chat, which no chat history keeps. With nobody
// online the hint reaches nobody, and the timer goes on.
internal static class Hints
{
    private const string Topic = "hints/say";
    private const string Author = "Good.bot"; // the game's name for the server in chat

    private static readonly Random random = new();
    private static IDisposable handler;
    private static DateTime next;
    private static int last = -1;

    public static void Update()
    {
        if (MySession.Static?.Ready != true)
            return;
        var cluster = Plugin.Cluster;
        if (cluster != null)
        {
            if (!cluster.Context.Available)
                return;
            // Handlers start on the game thread
            handler ??= cluster.RegisterHandler(
                Topic,
                message =>
                {
                    Say(Encoding.UTF8.GetString(message.Payload));
                    return Task.FromResult<byte[]>([]);
                }
            );
        }
        if (!Stations.IsAuthority)
            return;

        var config = Plugin.Config;
        var hints = config.Hints;
        if (!config.HintsEnabled || hints == null || hints.Count == 0)
        {
            next = default;
            return;
        }
        var now = DateTime.UtcNow;
        var longest = TimeSpan.FromMinutes(
            HintMath.Wait(config.HintMinMinutes, config.HintMaxMinutes, 1)
        );
        // The first one waits too, and a wait set shorter live cuts the one running
        if (next == default || next - now > longest)
            next = now + Wait(config);
        if (now < next)
            return;
        next = now + Wait(config);

        last = HintMath.Pick(hints.Count, last, random.Next);
        var text = hints[last].Text;
        if (string.IsNullOrWhiteSpace(text))
            return;
        Plugin.Log.Info($"Hints: {text}");
        if (cluster == null)
            Say(text);
        else
            _ = cluster.BroadcastAsync(
                Topic,
                Encoding.UTF8.GetBytes(text),
                Guid.NewGuid(),
                TimeSpan.FromSeconds(30),
                includeWorldAuthority: false
            );
    }

    private static TimeSpan Wait(OctahedronConfig config) =>
        TimeSpan.FromMinutes(
            HintMath.Wait(config.HintMinMinutes, config.HintMaxMinutes, random.NextDouble())
        );

    private static void Say(string text) =>
        MyVisualScriptLogicProvider.SendChatMessage(text, Author);

    public static void Dispose()
    {
        handler?.Dispose();
        handler = null;
    }
}
