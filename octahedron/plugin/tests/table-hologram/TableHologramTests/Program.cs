using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Entities.Blocks;
using Sandbox.Game.Entities.Blocks;
using VRage.Game;

// SE-1230: TableHologram's SetNewBlueprint postfix ran on the handover import's worker
// threads and wrote a plain Dictionary, which threw "Operations that change non-concurrent
// collections must have exclusive access" and failed the grid's init. Calls the postfix as
// Harmony would, from parallel threads with a distinct projector each, and checks that every
// table was queued and nothing threw.

const int Threads = 16;
const int PerThread = 20_000;

var plugin = typeof(ServerPlugin.Plugin).Assembly;
var hologram = plugin.GetType("ServerPlugin.TableHologram", true);
var postfix = hologram
    .GetNestedType("SetNewBlueprint", BindingFlags.NonPublic)
    .GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);
var pending = (IDictionary)
    hologram.GetField("pending", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);

// Constructors would need a running game: the postfix only uses the projector as a key
var tables = Enumerable
    .Range(0, Threads * PerThread)
    .Select(_ => (MyProjectorBase)RuntimeHelpers.GetUninitializedObject(typeof(MySpaceProjector)))
    .ToArray();
var blueprint = new List<MyObjectBuilder_CubeGrid>();

var failures = 0;
string firstFailure = null;
using var start = new Barrier(Threads);
Parallel.For(
    0,
    Threads,
    new ParallelOptions { MaxDegreeOfParallelism = Threads },
    t =>
    {
        start.SignalAndWait();
        for (var i = 0; i < PerThread; i++)
        {
            try
            {
                postfix.Invoke(null, [tables[t * PerThread + i], blueprint]);
            }
            catch (TargetInvocationException e)
            {
                if (Interlocked.Increment(ref failures) == 1)
                    firstFailure =
                        e.InnerException?.GetType().Name + ": " + e.InnerException?.Message;
            }
        }
    }
);

var queued = tables.Count(pending.Contains);
var ok = failures == 0 && queued == tables.Length && pending.Count == tables.Length;
Console.WriteLine(
    $"{(ok ? "PASS" : "FAIL")}: {Threads} threads x {PerThread} tables, {failures} postfix exceptions, "
        + $"{queued}/{tables.Length} queued, pending.Count {pending.Count}"
);
if (firstFailure != null)
    Console.WriteLine($"  first exception: {firstFailure}");
return ok ? 0 : 1;
