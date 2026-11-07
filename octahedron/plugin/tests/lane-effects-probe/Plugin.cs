using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.FileSystem;
using VRage.Game;
using VRage.Plugins;

namespace LaneEffectsProbe;

// Test only, for tests/test_lane_effects.py. The client counts every frame how many of the Trade
// Lanes mod's particle effects play: the ship's two lane effects and the gates' idle one. It writes
// the count and the most seen at once, as WarpY=now/most, when they change: on a client that loses
// the ship a moment into the trip its effects last only a few frames.
// The mod stops an effect before it closes it, so an effect it forgot keeps playing, even after the
// entity it was put on is gone.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private static readonly string[] Names =
    [
        "WarpY",
        "TradeLaneStarsAndTunnel",
        "RingsActivateAndIdle",
    ];
    private readonly Dictionary<string, int> most = [];
    private string written;

    public void Init(object gameInstance) { }

    public void Dispose() { }

    public void Update()
    {
        if (MySession.Static == null || Sync.IsServer)
            return;
        var playing = new Dictionary<string, int>();
        foreach (var effect in MyParticlesManager.Effects.ToList())
            if (effect.Data != null && !effect.IsStopped && Names.Contains(effect.GetName()))
                playing[effect.GetName()] = playing.GetValueOrDefault(effect.GetName()) + 1;
        foreach (var (name, count) in playing)
            most[name] = Math.Max(count, most.GetValueOrDefault(name));
        var line = string.Join(
            " ",
            Names.Select(n => $"{n}={playing.GetValueOrDefault(n)}/{most.GetValueOrDefault(n)}")
        );
        if (line == written)
            return;
        written = line;
        File.WriteAllText(Path.Combine(MyFileSystem.UserDataPath, "lane-effects-probe.txt"), line);
    }
}
