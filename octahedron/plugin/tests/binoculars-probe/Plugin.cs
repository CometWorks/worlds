using System.Collections.Generic;
using System.IO;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Plugins;

namespace BinocularsProbe;

// Test only, for tests/test_binoculars_zone.py. The server gives each player binoculars and a rifle
// with ammunition. The client writes a line for the gun in the local character's hands, a few times
// a second: its subtype, what CanShoot says of the secondary and primary actions, and the zoom mode.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private readonly HashSet<long> given = [];
    private int frame;

    public void Init(object gameInstance) { }

    public void Dispose() { }

    public void Update()
    {
        if (++frame % 10 != 0 || MySession.Static == null)
            return;
        if (Sync.IsServer)
            Give();
        else
            Write();
    }

    private void Give()
    {
        foreach (var player in MySession.Static.Players.GetOnlinePlayers())
        {
            if (player.Character == null || !given.Add(player.Character.EntityId))
                continue;
            var inventory = player.Character.GetInventory();
            Add(inventory, typeof(MyObjectBuilder_PhysicalGunObject), "BinocularsItem", 1);
            Add(inventory, typeof(MyObjectBuilder_PhysicalGunObject), "AutomaticRifleItem", 1);
            Add(inventory, typeof(MyObjectBuilder_AmmoMagazine), "AutomaticRifleGun_Mag_20rd", 5);
        }
    }

    private static void Add(
        MyInventory inventory,
        MyObjectBuilderType type,
        string subtype,
        int amount
    ) =>
        inventory.AddItems(
            amount,
            MyObjectBuilderSerializer.CreateNewObject(new MyDefinitionId(type, subtype))
        );

    private static void Write()
    {
        var character = MySession.Static.LocalCharacter;
        var line = "none";
        if (character?.CurrentWeapon is MyAutomaticRifleGun gun)
        {
            gun.CanShoot(MyShootActionEnum.SecondaryAction, 0, out var secondary);
            gun.CanShoot(MyShootActionEnum.PrimaryAction, 0, out var primary);
            var subtype = gun.PhysicalItemDefinition.Id.SubtypeName;
            line = $"{subtype} {secondary} {primary} {character.ZoomMode}";
        }
        File.WriteAllText(Path.Combine(MyFileSystem.UserDataPath, "binoculars-probe.txt"), line);
    }
}
