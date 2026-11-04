using System.Linq;
using Sandbox.Definitions;
using VRage.Game;
using VRage.Game.Components;
using VRage.ObjectBuilders;
using VRage.Utils;

namespace OctahedronFilaments
{
    // The filament tools (SE-1067) are OmniArc's welder and deconstructor with
    // other effects, and they look like them: this gives them the models and
    // icons of their OmniArc originals. A mod's block can only name files in
    // its own folder or the game's, so the definitions name a vanilla model
    // until this runs. LoadData runs after the definitions are loaded and
    // before any grid is, on the server and on every client.
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class FilamentModels : MySessionComponentBase
    {
        // make_filament_mod.py names every filament tool "<prefix><variant>"
        static readonly string[][] Originals =
        {
            new[] { "OctahedronFilamentWelder", "OmniArcBeam" },
            new[] { "OctahedronFilamentDeconstructor", "OmniArcDestro" },
        };

        public override void LoadData()
        {
            var sorter = MyObjectBuilderType.Parse("MyObjectBuilder_ConveyorSorter");
            foreach (
                var definition in MyDefinitionManager
                    .Static.GetAllDefinitions()
                    .OfType<MyCubeBlockDefinition>()
            )
            {
                if (definition.Id.TypeId != sorter)
                    continue;
                var subtype = definition.Id.SubtypeName;
                var pair = Originals.FirstOrDefault(p => subtype.StartsWith(p[0]));
                if (pair == null)
                    continue;
                MyCubeBlockDefinition original;
                if (
                    !MyDefinitionManager.Static.TryGetCubeBlockDefinition(
                        new MyDefinitionId(sorter, pair[1]),
                        out original
                    )
                )
                {
                    MyLog.Default.WriteLineAndConsole(
                        $"OctahedronFilaments: {pair[1]} not found, {subtype} keeps its placeholder model. Is OmniArc loaded?"
                    );
                    continue;
                }
                definition.Model = original.Model;
                definition.BuildProgressModels = original.BuildProgressModels;
                definition.Icons = original.Icons;
            }
        }
    }
}
