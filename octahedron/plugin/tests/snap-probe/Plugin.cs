using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Replication;
using Sandbox.Game.Replication.StateGroups;
using Sandbox.Game.World;
using VRage.FileSystem;
using VRage.Library.Collections;
using VRage.Library.Utils;
using VRage.Network;
using VRage.Plugins;
using VRageMath;

namespace SnapProbe;

// Test only, for tests/snapback.py (SE-1527): on the client, every frame, compares the local
// character's position with where its velocity said it would be, and writes to snap-probe.csv:
//   SNAP,time,frame,err,speed,predicted,forceDisabled,parent,serverParent,avgCorrection  (err > 0.1 m)
//   FLAG,time,frame,predicted,forceDisabled,alwaysDisabled                                 (on change)
//   PARENT,time,frame,old,new                                     (ClosestParentId changed on the client)
//   RX,time,frame,wallMs,serverTimestampMs,echoedClientTimestampMs   (a snapshot of the character arrived)
//   SEC,time,frames,predictedFrames,forceFrames,maxErr,maxAvgCorrection,speed,jetpack,moving,x,y,z
// time is the client's simulation time in seconds.
// ReSharper disable once UnusedType.Global
public class Plugin : IPlugin
{
    private const double SnapMeters = 0.1;

    private static readonly FieldInfo PhysicsSyncField = typeof(MyEntityReplicableBase<MyCharacter>)
        .GetField("m_physicsSync", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo ServerParentField = typeof(MyCharacterPhysicsStateGroup)
        .GetField("m_lastParentId", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly List<string> Received = new();
    private static long frameNow;
    private static double timeNow;

    private StreamWriter writer;
    private long frame;
    private MyCharacter last;
    private Vector3D lastPosition;
    private Vector3 lastVelocity;
    private double lastTime;
    private bool lastPredicted, lastForce, lastAlways;
    private long lastParent;
    private int secFrames, secPredicted, secForce;
    private double secMaxErr, secMaxCorrection, secStart;

    public void Init(object gameInstance)
    {
        new Harmony("SnapProbe").Patch(
            AccessTools.Method(typeof(MyCharacterPhysicsStateGroup), nameof(MyCharacterPhysicsStateGroup.Serialize)),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(OnSerialize))
        );
    }

    // ReSharper disable once InconsistentNaming
    private static void OnSerialize(MyCharacterPhysicsStateGroup __instance, BitStream stream,
        MyTimeSpan serverTimestamp, MyTimeSpan lastClientTimestamp)
    {
        if (stream.Writing || __instance.Entity != MySession.Static?.LocalCharacter)
            return;
        Received.Add(string.Format(CultureInfo.InvariantCulture, "RX,{0:F3},{1},{2:F1},{3:F1},{4:F1}", timeNow, frameNow,
            Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency, serverTimestamp.Milliseconds,
            lastClientTimestamp.Milliseconds));
    }

    public void Dispose()
    {
        writer?.Dispose();
        writer = null;
    }

    public void Update()
    {
        frame++;
        var character = MySession.Static?.LocalCharacter;
        if (character?.Physics == null || character.MarkedForClose || MySandboxGame.Static == null)
        {
            last = null;
            return;
        }
        writer ??= new StreamWriter(Path.Combine(MyFileSystem.UserDataPath, "snap-probe.csv"), append: false);
        double now = MySandboxGame.Static.SimulationTime.Seconds;
        frameNow = frame;
        timeNow = now;
        foreach (var line in Received)
            writer.WriteLine(line);
        Received.Clear();
        Vector3D position = character.PositionComp.GetPosition();
        Vector3 velocity = character.Physics.LinearVelocity;
        var group = MyExternalReplicable.FindByObject(character) is MyEntityReplicableBase<MyCharacter> replicable
            ? PhysicsSyncField?.GetValue(replicable) as MyCharacterPhysicsStateGroup
            : null;
        double correction = group?.AverageCorrection ?? -1;
        long serverParent = group != null && ServerParentField != null ? (long)ServerParentField.GetValue(group) : -1;
        bool predicted = character.IsClientPredicted, force = character.ForceDisablePrediction,
            always = character.AlwaysDisablePrediction;
        long parent = character.ClosestParentId;

        if (character != last)
        {
            last = character;
            lastParent = parent;
            lastPredicted = predicted;
            lastForce = force;
            lastAlways = always;
            secStart = now;
        }
        else
        {
            double dt = now - lastTime;
            if (dt > 0 && dt < 0.5)
            {
                Vector3D expected = lastPosition + (lastVelocity + velocity) * 0.5f * (float)dt;
                double err = (position - expected).Length();
                secMaxErr = Math.Max(secMaxErr, err);
                if (err > SnapMeters)
                    Write("SNAP", now, err, velocity.Length(), predicted, force, parent, serverParent, correction);
            }
            if (predicted != lastPredicted || force != lastForce || always != lastAlways)
                Write("FLAG", now, predicted, force, always);
            if (parent != lastParent)
                Write("PARENT", now, lastParent, parent);
        }
        secFrames++;
        if (predicted) secPredicted++;
        if (force) secForce++;
        secMaxCorrection = Math.Max(secMaxCorrection, correction);
        if (now - secStart >= 1.0)
        {
            Write("SEC", now, secFrames, secPredicted, secForce, secMaxErr, secMaxCorrection, velocity.Length(),
                character.JetpackRunning, character.MoveIndicator != Vector3.Zero, position.X, position.Y, position.Z);
            writer.Flush();
            secStart = now;
            secFrames = secPredicted = secForce = 0;
            secMaxErr = secMaxCorrection = 0;
        }
        lastPosition = position;
        lastVelocity = velocity;
        lastTime = now;
        lastPredicted = predicted;
        lastForce = force;
        lastAlways = always;
        lastParent = parent;
    }

    private void Write(string kind, double now, params object[] values)
    {
        writer.Write(kind);
        writer.Write(',');
        writer.Write(now.ToString("F3", CultureInfo.InvariantCulture));
        writer.Write(',');
        writer.Write(frame);
        foreach (var value in values)
        {
            writer.Write(',');
            writer.Write(value switch
            {
                double d => d.ToString("F3", CultureInfo.InvariantCulture),
                float f => f.ToString("F3", CultureInfo.InvariantCulture),
                bool b => b ? "1" : "0",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            });
        }
        writer.WriteLine();
    }
}
