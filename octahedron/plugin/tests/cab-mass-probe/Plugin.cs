using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using VRage;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Plugins;
using VRage.Utils;

namespace CabMassProbe;

// Test only, for tests/test_taxi_cab_mass.py. Once the "Taxi Mass Cab" grid has been loaded for 2 s, it gets a
// piston head and a wheel (its mechanical subgrids), then its connectors lock to the static "TM Station" and
// the "TM Parked" ship. 2 s later cab-mass-probe.txt gets the masses the server has for the cab, the one the
// taxi pickup offer reports (Taxi.CabMass, called by reflection) among them. Then 5,000 kg of ice goes into
// the cab's cargo container, and 2 s later the file gets the masses again with "after cargo".
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private const int Wait = 120;

    private readonly List<string> lines = new();
    private int frame,
        stage,
        next = -1;

    public void Init(object gameInstance) { }

    public void Dispose() { }

    public void Update()
    {
        if (MySession.Static == null || stage > 4)
            return;
        frame++;
        var grids = MyEntities.GetEntities().OfType<MyCubeGrid>().ToList();
        var cab = grids.FirstOrDefault(g => g.DisplayName == "Taxi Mass Cab");
        if (cab?.Physics == null)
            return;
        if (next < 0)
            next = frame + Wait;
        if (frame < next)
            return;
        next = frame + Wait;
        try
        {
            Step(cab, grids);
        }
        catch (Exception e)
        {
            lines.Add("error " + e.ToString().Replace("\n", " | "));
            stage = 5;
        }
        var path = Path.Combine(MyFileSystem.UserDataPath, "cab-mass-probe.txt");
        File.WriteAllLines(path + ".tmp", lines);
        File.Move(path + ".tmp", path, true);
    }

    private void Step(MyCubeGrid cab, List<MyCubeGrid> grids)
    {
        switch (stage++)
        {
            case 0:
                // What a player gets with Add Head and Add Wheel, built
                var create = typeof(MyMechanicalConnectionBlockBase).GetMethod(
                    "CreateTopPartAndAttach",
                    BindingFlags.Instance | BindingFlags.NonPublic
                );
                var normal = Enum.ToObject(create.GetParameters()[1].ParameterType, 0);
                foreach (var b in cab.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>())
                    create.Invoke(b, new object[] { 0L, normal, true });
                break;
            case 1:
                foreach (var c in cab.GetFatBlocks().OfType<MyShipConnector>())
                    ((IMyShipConnector)c).Connect();
                break;
            case 2:
                Measure("docked", cab, grids);
                break;
            case 3:
                var container = cab.GetFatBlocks().OfType<MyCargoContainer>().First();
                container
                    .GetInventory()
                    .AddItems(
                        (MyFixedPoint)5000,
                        MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Ore>("Ice")
                    );
                break;
            case 4:
                Measure("after cargo", cab, grids);
                lines.Add("done");
                break;
        }
    }

    private void Measure(string when, MyCubeGrid cab, List<MyCubeGrid> grids)
    {
        string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        double Shape(MyCubeGrid g) => g?.Physics?.Shape?.MassProperties?.Mass ?? 0;

        var tops = cab.GetFatBlocks()
            .OfType<MyMechanicalConnectionBlockBase>()
            .Select(b => b.TopGrid)
            .ToList();
        foreach (var b in cab.GetFatBlocks().OfType<MyMechanicalConnectionBlockBase>())
            lines.Add(
                $"{when} top of {b.BlockDefinition.Id.SubtypeName} at {b.Position} {F(Shape(b.TopGrid))}"
            );
        foreach (var c in cab.GetFatBlocks().OfType<MyShipConnector>())
            lines.Add(
                $"{when} connector {((IMyShipConnector)c).Status} to {((IMyShipConnector)c).OtherConnector?.CubeGrid.DisplayName}"
            );
        lines.Add($"{when} tops {tops.Count(t => t != null)} of {tops.Count}");
        lines.Add($"{when} physics_mass {F(cab.Physics.Mass)}");
        lines.Add($"{when} main_shape {F(Shape(cab))}");
        lines.Add($"{when} subgrid_shapes {F(tops.Sum(Shape))}");
        lines.Add(
            $"{when} parked_shape {F(Shape(grids.FirstOrDefault(g => g.DisplayName == "TM Parked")))}"
        );
        lines.Add($"{when} terminal_mass {F(cab.GetCurrentMass(GridLinkTypeEnum.Mechanical))}");
        var taxi = AppDomain
            .CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("ServerPlugin.Taxi", false))
            .First(t => t != null);
        var cabMass = taxi.GetMethod("CabMass", BindingFlags.NonPublic | BindingFlags.Static);
        lines.Add($"{when} offer_mass {F((double)cabMass.Invoke(null, new object[] { cab }))}");
        MyLog.Default.WriteLine("CabMassProbe: " + string.Join(" ; ", lines));
    }
}
