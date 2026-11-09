using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace MesClusterProbe;

// Reputation steps of the SE-1294 test (MES damage triggers and the OCT faction):
//   rep-setup                         players A, B, C, D; new factions OCT founded by A and TST by C
//   rep-join                          B joins OCT and D joins TST: rep-join A=id:OCT B=id:OCT C=id:TST D=id:TST
//   rep-spawn TYPE GROUP X Y Z        forced MES spawn of GROUP near (X, Y, Z): rep-spawned GROUP True|False
//   rep-target NAME                   the NPC grid whose name contains NAME is the target; counts the damage it takes
//   rep-grid G P DIST                 grid G: a 27-block heavy armor cube with a cargo container owned by player P, DIST m
//                                     from the target's surface (negative: on the other side)
//   rep-ram G SECONDS SPEED           grid G flies into the target at SPEED m/s for SECONDS
//   rep-hit G TYPE COUNT MS           COUNT damage events of TYPE on the target from grid G, one every MS ms
//   rep LABEL                         rep LABEL A=n B=n C=n D=n (reputation with THIE) damage=Type:count,...
public partial class Probe
{
    private const string NpcTag = "THIE";
    private readonly Dictionary<string, long> players = new();
    private readonly Dictionary<string, MyCubeGrid> playerGrids = new();
    private readonly Dictionary<string, int> damageTaken = new();
    private MyCubeGrid target;
    private bool damageHandler;

    private string ramGrid;
    private double ramSpeed;
    private DateTime ramUntil;

    private string hitGrid;
    private MyStringHash hitType;
    private int hitsLeft;
    private double hitInterval;
    private DateTime nextHit;

    private bool RepStep(string[] words)
    {
        switch (words[0])
        {
            case "rep-setup":
                RepSetup();
                return true;
            case "rep-join":
                RepJoin();
                return true;
            case "rep-spawn":
                RepSpawn(words);
                return true;
            case "rep-target":
                RepTarget(words[1]);
                return true;
            case "rep-grid":
                RepGrid(words[1], words[2], D(words[3]));
                return true;
            case "rep-ram":
                ramGrid = words[1];
                ramUntil = DateTime.UtcNow.AddSeconds(D(words[2]));
                ramSpeed = D(words[3]);
                return true;
            case "rep-hit":
                hitGrid = words[1];
                hitType = MyStringHash.GetOrCompute(words[2]);
                hitsLeft = int.Parse(words[3]);
                hitInterval = D(words[4]);
                nextHit = DateTime.UtcNow;
                return true;
            case "rep":
                RepReport(words[1]);
                return true;
        }
        return false;
    }

    // Every frame: the ram and the hits run over many frames while the plan waits
    private void RepTick()
    {
        if (target == null || target.Closed)
            return;
        var now = DateTime.UtcNow;
        if (ramGrid != null && playerGrids.TryGetValue(ramGrid, out var ram) && !ram.Closed)
        {
            if (now < ramUntil)
            {
                var direction = Vector3D.Normalize(
                    target.PositionComp.WorldAABB.Center - ram.PositionComp.WorldAABB.Center
                );
                ram.Physics.LinearVelocity =
                    target.Physics.LinearVelocity + (Vector3)(direction * ramSpeed);
                ram.Physics.AngularVelocity = Vector3.Zero;
            }
            else
            {
                ram.Physics.LinearVelocity = target.Physics.LinearVelocity;
                ramGrid = null;
            }
        }
        while (hitsLeft > 0 && now >= nextHit && playerGrids.TryGetValue(hitGrid, out var gun))
        {
            hitsLeft--;
            if (gun.Closed)
                output.WriteLine($"error rep-hit {hitGrid} is gone");
            nextHit = nextHit.AddMilliseconds(hitInterval);
            var block = target.GetBlocks().First(b => b.Integrity > 10);
            block.DoDamage(1f, hitType, true, null, gun.EntityId);
        }
    }

    private void RepSetup()
    {
        foreach (var name in new[] { "A", "B", "C", "D" })
            players[name] = MySession.Static.Players.CreateNewIdentity("Rep" + name).IdentityId;
        var factions = MySession.Static.Factions;
        factions.CreateFaction(players["A"], "OCT", "Octahedron", "", "", "None");
        factions.CreateFaction(players["C"], "TST", "Test Team", "", "", "None");
        output.WriteLine("rep-setup factions requested");
    }

    private void RepJoin()
    {
        var factions = MySession.Static.Factions;
        factions.AddPlayerToFaction(players["B"], factions.TryGetFactionByTag("OCT").FactionId);
        factions.AddPlayerToFaction(players["D"], factions.TryGetFactionByTag("TST").FactionId);
        output.WriteLine(
            "rep-join "
                + string.Join(
                    " ",
                    players.Select(p =>
                        $"{p.Key}={p.Value}:{factions.GetPlayerFaction(p.Value)?.Tag}"
                    )
                )
        );
    }

    private void RepSpawn(string[] words)
    {
        var calculate = Mes.GetType("ModularEncountersSystems.Spawning.SpawnRequest")
            .GetMethod("CalculateSpawn", BindingFlags.Public | BindingFlags.Static);
        bool result = false;
        for (int i = 0; i < 20 && !result; i++)
        {
            var args = calculate
                .GetParameters()
                .Select(p => p.HasDefaultValue ? p.DefaultValue : null)
                .ToArray();
            args[0] =
                new Vector3D(D(words[3]), D(words[4]), D(words[5]))
                + 1000
                    * new Vector3D(random.NextDouble(), random.NextDouble(), random.NextDouble());
            args[1] = "MesClusterProbe";
            args[2] = Enum.Parse(
                Mes.GetType("ModularEncountersSystems.Spawning.SpawningType"),
                words[1]
            );
            args[3] = true; // forceSpawn
            args[4] = true; // adminSpawn
            args[5] = new List<string> { words[2] };
            result = (bool)calculate.Invoke(null, args);
        }
        output.WriteLine($"rep-spawned {words[2]} {result}");
    }

    private void RepTarget(string name)
    {
        target = MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(g => !g.Closed && g.DisplayName.Contains(name))
            .OrderByDescending(g => g.BlocksCount)
            .FirstOrDefault();
        if (target == null)
        {
            output.WriteLine($"rep-target none {name}");
            return;
        }
        if (!damageHandler)
        {
            damageHandler = true;
            MyAPIGateway.Session.DamageSystem.RegisterAfterDamageHandler(
                0,
                (object hit, MyDamageInformation info) =>
                {
                    if (target != null && hit is IMySlimBlock block && block.CubeGrid == target)
                    {
                        string type = info.Type.String;
                        damageTaken[type] = damageTaken.TryGetValue(type, out int n) ? n + 1 : 1;
                    }
                }
            );
        }
        var owner = target.BigOwners.FirstOrDefault();
        output.WriteLine(
            $"rep-target {target.EntityId} {target.DisplayName} blocks={target.BlocksCount} "
                + $"faction={MySession.Static.Factions.GetPlayerFaction(owner)?.Tag}"
        );
    }

    private void RepGrid(string name, string player, double distance)
    {
        var box = target.PositionComp.WorldAABB;
        var side = Vector3D.Normalize(
            Vector3D.CalculatePerpendicularVector(target.WorldMatrix.Forward)
        );
        var position =
            box.Center
            + Math.Sign(distance) * side * (box.HalfExtents.Length() + Math.Abs(distance));
        var grid = new MyObjectBuilder_CubeGrid
        {
            GridSizeEnum = MyCubeSize.Large,
            IsStatic = false,
            PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.Enabled,
            PositionAndOrientation = new MyPositionAndOrientation(
                position,
                Vector3.Forward,
                Vector3.Up
            ),
            DisplayName = "Rep" + name,
        };
        long owner = players[player];
        for (int x = 0; x < 3; x++)
        for (int y = 0; y < 3; y++)
        for (int z = 0; z < 3; z++)
        {
            bool center = x == 1 && y == 1 && z == 1;
            var block = (MyObjectBuilder_CubeBlock)
                MyObjectBuilderSerializer.CreateNewObject(
                    center
                        ? typeof(MyObjectBuilder_CargoContainer)
                        : typeof(MyObjectBuilder_CubeBlock),
                    center ? "LargeBlockSmallContainer" : "LargeHeavyBlockArmorBlock"
                );
            block.Min = new SerializableVector3I(x, y, z);
            block.Owner = owner;
            block.BuiltBy = owner;
            block.ShareMode = MyOwnershipShareModeEnum.None;
            grid.CubeBlocks.Add(block);
        }
        MyEntities.RemapObjectBuilder(grid);
        var created = (MyCubeGrid)MyEntities.CreateFromObjectBuilderAndAdd(grid, false);
        playerGrids[name] = created;
        output.WriteLine(
            $"rep-grid {name} {player} {created.EntityId} owner={created.BigOwners.FirstOrDefault()} at {F(position)}"
        );
    }

    private void RepReport(string label)
    {
        var faction = MySession.Static.Factions.TryGetFactionByTag(NpcTag);
        var reps = players.Select(p =>
            $"{p.Key}={MyAPIGateway.Session.Factions.GetReputationBetweenPlayerAndFaction(p.Value, faction.FactionId)}"
        );
        var damage = string.Join(
            ",",
            damageTaken.OrderBy(d => d.Key).Select(d => $"{d.Key}:{d.Value}")
        );
        output.WriteLine($"rep {label} {string.Join(" ", reps)} damage={damage}");
    }
}
