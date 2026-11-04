using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.Utils;
using VRage.Voxels;
using VRageMath;

namespace MoonShipyardAsteroid
{
    // Builds the Moon Shipyard's asteroid (SE-986) in a world without it: the
    // voxel content comes from asteroid.bin, which make_moon_shipyard.py
    // writes, the storage's min corner (world meters, "x y z") from place.txt,
    // both in this mod's folder. It looks for place.txt every two seconds, so a
    // test can paste the grid first and then say where the rock goes. Once the
    // world is saved, the asteroid is an ordinary voxel map with its own .vx2
    // file, and the world no longer needs this mod.
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class AsteroidBuilder : MySessionComponentBase
    {
        const string Name = "MoonShipyard";

        int ticks;
        bool done;

        public override void UpdateBeforeSimulation()
        {
            if (done || ++ticks % 120 != 0)
                return;
            if (!MyAPIGateway.Session.IsServer)
            {
                done = true;
                return;
            }
            try
            {
                done = Build();
            }
            catch (Exception e)
            {
                done = true;
                Log("failed: " + e);
            }
        }

        // True when there is nothing more to do
        bool Build()
        {
            var maps = new List<IMyVoxelBase>();
            MyAPIGateway.Session.VoxelMaps.GetInstances(maps, v => v.StorageName == Name);
            if (maps.Count > 0)
            {
                Log("already in the world");
                return true;
            }
            var mod = ModContext.ModItem;
            if (!MyAPIGateway.Utilities.FileExistsInModLocation("place.txt", mod))
                return false;
            Vector3D corner;
            using (var reader = MyAPIGateway.Utilities.ReadFileInModLocation("place.txt", mod))
            {
                var p = reader
                    .ReadToEnd()
                    .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                corner = new Vector3D(
                    double.Parse(p[0], CultureInfo.InvariantCulture),
                    double.Parse(p[1], CultureInfo.InvariantCulture),
                    double.Parse(p[2], CultureInfo.InvariantCulture)
                );
            }

            using (
                var reader = MyAPIGateway.Utilities.ReadBinaryFileInModLocation("asteroid.bin", mod)
            )
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "MSA1")
                    throw new Exception("asteroid.bin: not MSA1");
                var size = reader.ReadInt32();
                var chunk = reader.ReadInt32();
                var count = reader.ReadInt32();
                var name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
                var material = MyDefinitionManager.Static.GetVoxelMaterialDefinition(name).Index;
                var storage = MyAPIGateway.Session.VoxelMaps.CreateStorage(new Vector3I(size));
                var length = chunk * chunk * chunk;
                for (var i = 0; i < count; i++)
                {
                    var min =
                        new Vector3I(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32())
                        * chunk;
                    var runs = reader.ReadInt32();
                    var content = new byte[length];
                    var at = 0;
                    for (var r = 0; r < runs; r++)
                    {
                        var value = reader.ReadByte();
                        var n = reader.ReadUInt16();
                        for (var k = 0; k < n; k++)
                            content[at++] = value;
                    }
                    var materials = new byte[length];
                    for (var k = 0; k < length; k++)
                        materials[k] = material;
                    var data = new MyStorageData(new Vector3I(chunk), content, materials);
                    storage.WriteRange(
                        data,
                        MyStorageDataTypeFlags.ContentAndMaterial,
                        min,
                        min + chunk - 1
                    );
                }
                MyAPIGateway.Session.VoxelMaps.CreateVoxelMap(
                    Name,
                    storage,
                    corner,
                    (6L << 56) | 986986L
                );
                Log($"built from {count} chunks of {name}, storage {size}, corner {corner}");
            }
            return true;
        }

        static void Log(string text)
        {
            MyLog.Default.WriteLineAndConsole("MoonShipyardAsteroid: " + text);
        }
    }
}
