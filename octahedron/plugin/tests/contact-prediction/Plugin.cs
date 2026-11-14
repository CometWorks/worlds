using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Plugins;
using VRage.Utils;
using VRageMath;

namespace ContactPrediction;

// Prototype for SE-1527. MyCharacter.UpdatePredictionFlag sets ForceDisablePrediction whenever the
// local character's capsule overlaps a grid's shape, which turns client prediction off for 10 s: the
// character then follows the server's delayed position (lag) and jumps when prediction comes back.
// Against a grid at rest the server and the client resolve the contact the same way, so prediction
// stays on here; a moving grid keeps the game's rule.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private const float RestSpeedSq = 0.5f * 0.5f;

    private static readonly FieldInfo ForceField = AccessTools.Field(typeof(MyCharacter), "m_forceDisablePrediction");
    private static readonly MethodInfo PredictedSetter = AccessTools.PropertySetter(typeof(MyCharacter), nameof(MyCharacter.IsClientPredicted));
    private static readonly List<HkBodyCollision> Hits = new();
    private static int kept, logged;

    public void Init(object gameInstance)
    {
        new Harmony("ContactPrediction").Patch(
            AccessTools.Method(typeof(MyCharacter), nameof(MyCharacter.UpdatePredictionFlag)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(Before)),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(After))
        );
    }

    public void Dispose() { }

    public void Update()
    {
        if (kept != logged && MySession.Static != null && MySession.Static.GameplayFrameCounter % 600 == 0)
        {
            logged = kept;
            MyLog.Default.WriteLine($"ContactPrediction: prediction kept on through {kept} contacts with grids at rest");
        }
    }

    // ReSharper disable InconsistentNaming
    private static void Before(MyCharacter __instance, out bool __state) => __state = __instance.ForceDisablePrediction;

    private static void After(MyCharacter __instance, bool __state)
    {
        // Only a switch-off this call made: the only one inside the method is the overlap with a grid
        if (Sync.IsServer || __state || !__instance.ForceDisablePrediction || __instance.IsDead || __instance.IsOnLadder)
            return;
        var proxy = __instance.Physics?.CharacterProxy;
        if (proxy == null)
            return;
        var shape = proxy.GetCollisionShape();
        if (shape.IsZero)
            return;
        MatrixD matrix = __instance.Physics.GetWorldMatrix();
        matrix.Translation += Vector3D.TransformNormal(__instance.Physics.Center, ref matrix);
        Vector3D translation = matrix.Translation;
        Quaternion rotation = Quaternion.CreateFromRotationMatrix(in matrix);
        Hits.Clear();
        MyPhysics.GetPenetrationsShape(shape, ref translation, ref rotation, Hits, 30);
        try
        {
            foreach (var hit in Hits)
            {
                if (hit.Body.UserObject is not MyGridPhysics grid)
                    continue;
                if (!grid.IsStatic && grid.GetVelocityAtPoint(translation).LengthSquared() > RestSpeedSq)
                    return;
            }
        }
        finally
        {
            Hits.Clear();
        }
        ForceField.SetValue(__instance, false);
        bool predicted = MyFakes.MULTIPLAYER_CLIENT_SIMULATE_CONTROLLED_CHARACTER
            && MySession.Static.TopMostControlledEntity == __instance
            && (!__instance.JetpackRunning || MyFakes.MULTIPLAYER_CLIENT_SIMULATE_CONTROLLED_CHARACTER_IN_JETPACK)
            && !__instance.AlwaysDisablePrediction;
        if (__instance.IsClientPredicted != predicted)
            PredictedSetter.Invoke(__instance, new object[] { predicted });
        kept++;
    }
}
