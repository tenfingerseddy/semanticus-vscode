using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

// fx35 (rc34 tester). Gap 1: with the licence and Pro advice on, the Properties card's Totals detail read "Not scored yet. This
// score turns on when Jev scoring is released." for about 20 s after every expression edit, until the rerun's answer came. fx34
// made a Totals with a result stale ("stale", Unknown) and the copy every door gets rewrote any unscored-looking Totals line to
// the calm line, the stale line too. Finish line B2: that line only on a real service fault. Gap 2: the person kept an edit
// (* 2), pressed Undo, then made the same edit again during a run. Every screen showed no Equivalence: the Keep of the undone
// edit still matched the new comparison's inputs, so Equivalence read "kept" and did not apply. Finish line C3 and E2: every edit
// that moves numbers shows Equivalence with before and after, in both doors and the push. Here the edit adds 50 (300 to 350).
[Collection("restore-root")]
public sealed class CheckedFx35Tests : IDisposable
{
    private const string Edited = "measure:Sales/Total Sales";
    private const string Released = "Jev scoring is released";
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "fx35-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedFx35Tests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    // The licensed scorer: it answers, so no line here is a service fault.
    private sealed class SumScorer : ITotalsScorer
    {
        public Task<MeasureMark> ScoreAsync(TotalsInput input, TotalProbe[] probes, TotalsSettings settings, bool expected, CancellationToken ct) =>
            Task.FromResult(probes.Length > 0
                ? new MeasureMark { Name = "Totals", State = "passed", Sentence = "Total adds up as a sum, as expected.", LastRan = DateTime.UtcNow.ToString("o") }
                : new MeasureMark { Name = "Totals", State = "info", Sentence = "Totals for this type are shown, not judged.", ReasonCode = "totals_not_judged" });
    }

    /// <summary>The fixture's model with a gate: once <see cref="Hold"/> is set, every query waits until it is released.</summary>
    private sealed class Gated : IDisposable
    {
        internal readonly MeasureMarksDoorTests.Fixture F;
        internal readonly ManualResetEventSlim Open = new(true);
        internal volatile bool Hold;
        internal volatile int Waiting;
        internal Gated(string root) => F = new MeasureMarksDoorTests.Fixture(root);
        internal async Task Start()
        {
            await F.Open(2);
            F.Engine.TotalsScorer = new SumScorer();
            F.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q =>
            {
                if (Hold) { Interlocked.Increment(ref Waiting); Open.Wait(); Interlocked.Decrement(ref Waiting); }
                return F.Query(q);
            }));
            await F.Engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureValue, Title = "Contoso trusted total", TargetRef = Edited,
                ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = Edited, ExpectedValue = "300" })
            }, "human");
        }
        internal async Task WaitForQuery()
        {
            var clock = Stopwatch.StartNew();
            while (Waiting == 0 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
            Assert.True(Waiting > 0, "no check reached a query");
        }
        public void Dispose() { Open.Set(); F.Dispose(); Open.Dispose(); }
    }

    private static bool Busy(MeasureCheckCache s) => s.Running || s.Marks.Any(m => m.Pending || m.Progress != null);

    private static async Task<MeasureCheckCache[]> WaitSettled(LocalEngine engine, int seconds = 20)
    {
        var clock = Stopwatch.StartNew();
        var states = await engine.GetMeasureCheckCacheAsync();
        while (states.Any(Busy) && clock.Elapsed < TimeSpan.FromSeconds(seconds)) { await Task.Delay(25); states = await engine.GetMeasureCheckCacheAsync(); }
        return states;
    }

    private static string Marks(MeasureMark[] marks) => string.Join(", ", marks.Select(m => $"{m.Name}:{m.State}:{m.ReasonCode}:{m.Verdict}:{(m.Pending ? "pending:" : "")}{m.Sentence}"
        + (m.Cells.Length > 0 ? " [" + string.Join(" ", m.Cells.Select(c => c.Before + ">" + c.After)) + "]" : "")));

    private static async Task CheckNow(LocalEngine engine)
    {
        var run = await engine.RunMeasurePlanAsync((await engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id);
        Assert.False(run.Stopped || run.StoppedOnEdit);
        await WaitSettled(engine);
    }

    private static Task Edit(MeasureMarksDoorTests.Fixture f) => f.Sessions.Current.MutateAsync("human", "Contoso formula",
        m => m.Tables["Sales"].Measures["Total Sales"].Expression = "SUM('Sales'[Amount]) + 50");

    /// <summary>Every copy of the edited measure's marks a screen or the assistant can get now: both check-state doors and both
    /// get_measure_marks doors.</summary>
    private static async Task<(string Door, MeasureMark[] Marks)[]> Doors(LocalEngine engine) => new[]
    {
        ("rpc states", (await engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Edited).Marks),
        ("mcp states", (await McpTools.GetMeasureCheckCache(engine)).Single(s => s.MeasureRef == Edited).Marks),
        ("rpc marks", (await engine.GetMeasureMarksAsync(Edited)).Marks),
        ("mcp marks", (await McpTools.GetMeasureMarks(engine, Edited)).Marks)
    };

    [Fact]
    public async Task During_the_rerun_after_an_edit_no_mark_in_any_door_or_push_reads_the_released_line()
    {
        using var g = new Gated(root); await g.Start();
        var f = g.F;
        await CheckNow(f.Engine);
        var before = (await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Edited);
        Assert.True(before.Marks.Single(m => m.Name == "Totals").Verdict == "verified", Marks(before.Marks));

        var pushed = new ConcurrentQueue<MeasureCheckCache>();
        f.Sessions.Bus.ChecksChanged += change => { foreach (var s in change.States) pushed.Enqueue(s); };
        // Every query waits: the edit's comparison and rerun stay on their way while the doors are read.
        g.Open.Reset(); g.Hold = true;
        await Edit(f);
        await g.WaitForQuery();
        foreach (var (door, marks) in await Doors(f.Engine))
        {
            Assert.True(marks.All(m => m.Sentence?.Contains(Released) != true), $"{door} during the rerun: {Marks(marks)}");
            var totals = marks.Single(m => m.Name == "Totals");
            Assert.True(totals.ReasonCode is "stale" or "waiting" && totals.Verdict == "unknown", $"{door}: Totals is not stale or checking: {Marks(marks)}");
        }
        g.Hold = false; g.Open.Set();
        var settled = (await WaitSettled(f.Engine)).Single(s => s.MeasureRef == Edited);
        Assert.Equal("passed", settled.Marks.Single(m => m.Name == "Totals").State);
        foreach (var s in pushed.Where(s => s.MeasureRef == Edited))
            Assert.True(s.Marks.All(m => m.Sentence?.Contains(Released) != true), "a push after the edit read the released line: " + Marks(s.Marks));
    }

    /// <summary>Edit, Keep, Undo: the measure is back to its old formula. Returns the engine ready for the same edit again.</summary>
    private static async Task KeepThenUndo(MeasureMarksDoorTests.Fixture f)
    {
        await CheckNow(f.Engine);
        await Edit(f);
        await WaitSettled(f.Engine);
        var first = (await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Edited);
        Assert.True(first.Marks.Single(m => m.Name == "Equivalence").Applies, "the first edit showed no Equivalence: " + Marks(first.Marks));
        var kept = await f.Engine.KeepMeasureEditAsync(Edited);
        Assert.False(kept.Marks.Single(m => m.Name == "Equivalence").Applies, "Keep did not hide the kept edit: " + Marks(kept.Marks));
        await f.Engine.UndoAsync("human");
        var undone = (await WaitSettled(f.Engine)).Single(s => s.MeasureRef == Edited);
        Assert.True(undone.Marks.All(m => m.Name != "Equivalence" || !m.Applies), "after Undo: " + Marks(undone.Marks));
    }

    private static async Task AssertEquivalenceShown(MeasureMarksDoorTests.Fixture f, ConcurrentQueue<MeasureCheckCache> pushed)
    {
        foreach (var (door, marks) in await Doors(f.Engine))
        {
            var eq = marks.SingleOrDefault(m => m.Name == "Equivalence");
            Assert.True(eq != null && eq.Applies && eq.Verdict == "review" && eq.ReasonCode != "kept"
                && eq.Cells.Any(c => c.Before == "300" && c.After == "350"),
                $"{door}: the edit's Equivalence with before and after is not shown: {Marks(marks)}");
        }
        var last = pushed.Where(s => s.MeasureRef == Edited).OrderBy(s => s.Version).LastOrDefault();
        Assert.True(last != null, "no push for the edited measure");
        var pushedEq = last.Marks.SingleOrDefault(m => m.Name == "Equivalence");
        Assert.True(pushedEq != null && pushedEq.Applies && pushedEq.Cells.Any(c => c.Before == "300" && c.After == "350"),
            "the newest push has no Equivalence with before and after: " + Marks(last.Marks));
    }

    [Fact]
    public async Task The_same_edit_again_after_Keep_and_Undo_shows_Equivalence_with_before_and_after()
    {
        using var g = new Gated(root); await g.Start();
        var f = g.F;
        await KeepThenUndo(f);
        var pushed = new ConcurrentQueue<MeasureCheckCache>();
        f.Sessions.Bus.ChecksChanged += change => { foreach (var s in change.States) pushed.Enqueue(s); };
        await Edit(f);
        await WaitSettled(f.Engine);
        await AssertEquivalenceShown(f, pushed);
    }

    [Fact]
    public async Task An_edit_during_a_run_shows_Equivalence_with_before_and_after_in_every_door_and_the_push()
    {
        using var g = new Gated(root); await g.Start();
        var f = g.F;
        await KeepThenUndo(f);
        var pushed = new ConcurrentQueue<MeasureCheckCache>();
        f.Sessions.Bus.ChecksChanged += change => { foreach (var s in change.States) pushed.Enqueue(s); };

        // A run is going and waits on a query; the person makes the edit again.
        g.Open.Reset(); g.Hold = true;
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25, IncludePerformance = true });
        var running = f.Engine.RunMeasurePlanAsync(plan.Id);
        await g.WaitForQuery();
        await Edit(f);
        g.Hold = false; g.Open.Set();
        var result = await running;
        Assert.True(result.StoppedOnEdit || result.Stopped, "the run went on after the edit");
        await WaitSettled(f.Engine);
        await AssertEquivalenceShown(f, pushed);
    }
}
