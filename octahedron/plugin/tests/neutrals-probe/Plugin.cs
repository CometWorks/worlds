using System;
using System.IO;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Plugins;
using VRageMath;

namespace NeutralsProbe;

// Test only, for tests/test_target_neutrals.py: does what neutrals-probe.plan in the game's user
// data folder says, then deletes it. On the client that's the requests the game's own screens
// send, a purchase from a store block and a respawn in a ship. On the server, a ship for an NPC
// and the death of the players online.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private int frame;

    public void Init(object gameInstance) { }

    public void Dispose() { }

    public void Update()
    {
        if (++frame % 30 != 0 || MySession.Static == null)
            return;
        var path = Path.Combine(MyFileSystem.UserDataPath, "neutrals-probe.plan");
        if (!File.Exists(path))
            return;
        var words = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        File.Delete(path);
        switch (words[0])
        {
            case "buy":
                AccessTools
                    .Method(typeof(MyStoreBlock), "CreateBuyRequest")
                    .Invoke(
                        MyEntities.GetEntityById(long.Parse(words[1])),
                        new object[]
                        {
                            long.Parse(words[2]),
                            1,
                            MySession.Static.LocalCharacter.EntityId,
                            0d,
                            null,
                        }
                    );
                break;
            case "die":
                foreach (var player in Sync.Players.GetOnlinePlayers())
                    player.Character?.DoDamage(1000f, MyDamageType.Suicide, true);
                break;
            case "respawn":
                MyPlayerCollection.RespawnRequest(
                    false,
                    false,
                    0,
                    words[1],
                    0,
                    "Default_Astronaut",
                    Color.White
                );
                break;
            case "npc":
                MyPrefabManager.Static.SpawnPrefab(
                    words[1],
                    new Vector3D(
                        double.Parse(words[2]),
                        double.Parse(words[3]),
                        double.Parse(words[4])
                    ),
                    Vector3.Forward,
                    Vector3.Up,
                    spawningOptions: SpawningOptions.None,
                    ownerId: long.Parse(words[5]),
                    updateSync: true
                );
                break;
        }
    }
}
