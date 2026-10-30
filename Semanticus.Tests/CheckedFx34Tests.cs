using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

// fx34 (rc33 tester, gap 1): on pilot2 the person saved the trusted number 628,370 for Total Quantity, then edited the formula to
// SUM ( Sales[Quantity] ) * 2 with no run going. Equivalence said the edit moved numbers, but Trusted number, Filters, Contexts
// and Totals kept their pre-edit results and did not rerun; after Keep the measure read "Verified by 5 checks" with Measure
// 628,370 while Try calculation gave 1,256,740. Only a get_measure_marks read started the rerun. Finish line C3: an edit that
// changes a measure's expression makes every number mark of it (and of the measures that use it) stale at once, they never read
// as their old pass, and they rerun, edited measure first; Keep never turns stale marks into Verified. Here the edit adds 50, so
// the measure answers 350 against the trusted 300.
[Collection("restore-root")]
public sealed class CheckedFx34Tests : IDisposable
{
    private const string Edited = "measure:Sales/Total Sales";
    private const string User = "measure:Sales/Sales Copy 1";
    private static readonly string[] NumberMarks = { "Totals", "Filters", "SQL", "Trusted number", "Contexts" };
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "fx34-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedFx34Tests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

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

    private static string Marks(MeasureCheckCache s) => s.MeasureRef + " " + s.Verdict + (s.Running ? " running" : "") + ": "
        + string.Join(", ", s.Marks.Select(m => $"{m.Name}:{m.State}:{m.Verdict}:{(m.Pending ? "pending:" : "")}{m.Sentence}"
            + (m.Cells.Length > 0 ? " [" + string.Join(" ", m.Cells.Select(c => c.Before + ">" + c.After)) + "]" : "")));

    // Both doors: the tree and the Checks rows (RPC) and the assistant (MCP) read the same verdict and marks.
    private static async Task<MeasureCheckCache[]> BothDoors(LocalEngine engine)
    {
        var rows = await engine.GetMeasureCheckCacheAsync();
        var agent = await McpTools.GetMeasureCheckCache(engine);
        foreach (var row in rows)
        {
            var other = agent.Single(a => a.MeasureRef == row.MeasureRef);
            Assert.True(other.Verdict == row.Verdict, $"the doors differ on {row.MeasureRef}: {Marks(row)} | {Marks(other)}");
            Assert.Equal(string.Join("|", row.Marks.Select(m => m.Name + m.State + m.Verdict + m.Sentence)), string.Join("|", other.Marks.Select(m => m.Name + m.State + m.Verdict + m.Sentence)));
        }
        return rows;
    }

    /// <summary>A number mark that still shows its result from before the edit as a pass. Stale or Checking is right.</summary>
    private static string OldPass(MeasureCheckCache state, DateTimeOffset edit) => string.Join(", ", state.Marks
        .Where(m => NumberMarks.Contains(m.Name) && m.Verdict == "verified"
            && (!DateTimeOffset.TryParse(m.LastRan, out var ran) || ran < edit))
        .Select(m => m.Name));

    private static async Task CheckNow(LocalEngine engine)
    {
        var run = await engine.RunMeasurePlanAsync((await engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id);
        Assert.False(run.Stopped || run.StoppedOnEdit);
        await WaitSettled(engine);
    }

    private static Task Edit(MeasureMarksDoorTests.Fixture f) => f.Sessions.Current.MutateAsync("human", "Contoso formula",
        m => m.Tables["Sales"].Measures["Total Sales"].Expression = "SUM('Sales'[Amount]) + 50");

    [Fact]
    public async Task An_edit_makes_the_trusted_number_stale_at_once_reruns_it_without_a_read_and_Keep_never_reads_Verified()
    {
        using var g = new Gated(root); await g.Start();
        var f = g.F;
        await CheckNow(f.Engine);
        var first = (await BothDoors(f.Engine)).ToDictionary(s => s.MeasureRef);
        Assert.True(first[Edited].Verdict == "verified", Marks(first[Edited]));
        Assert.Equal("verified", first[Edited].Marks.Single(m => m.Name == "Trusted number").Verdict);
        Assert.Equal("verified", first[User].Verdict);

        // Every state a screen gets after the edit: the push the tree and the pages keep.
        var pushed = new ConcurrentQueue<MeasureCheckCache>();
        var compared = new ManualResetEventSlim(false);
        var listening = false;
        f.Sessions.Bus.ChecksChanged += change =>
        {
            if (!listening) return;
            foreach (var s in change.States)
            {
                pushed.Enqueue(s);
                // The edit's comparison is written: from now on every query waits, so the rerun cannot end before Keep.
                if (s.MeasureRef == Edited && s.Marks.Any(m => m.Name == "Equivalence" && m.State == "suspect" && !m.Pending)) { g.Hold = true; compared.Set(); }
            }
        };
        g.Open.Reset();
        var edit = DateTimeOffset.UtcNow;
        listening = true;
        await Edit(f);

        // Right after the edit, before any read starts a check: no number mark reads its old pass, in either door. The edit's
        // comparison may land between the two reads, so each door is judged on its own here.
        foreach (var door in new[] { await f.Engine.GetMeasureCheckCacheAsync(), await McpTools.GetMeasureCheckCache(f.Engine) })
            foreach (var state in door.Where(s => s.MeasureRef is Edited or User))
                Assert.True(OldPass(state, edit) == "" && state.Verdict != "verified",
                    $"right after the edit {state.MeasureRef} reads its old pass ({OldPass(state, edit)}): {Marks(state)}");
        MeasureCheckCache[] states;

        Assert.True(compared.Wait(TimeSpan.FromSeconds(15)), "the edit was not compared: " + Marks((await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Edited)));
        var clock = Stopwatch.StartNew();
        while (g.Waiting == 0 && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(g.Waiting > 0, "the edit did not start a rerun of the measure's checks after its comparison");

        // Keep while the rerun waits: the measure is not Verified, and the Trusted number is not its old pass.
        var kept = await f.Engine.KeepMeasureEditAsync(Edited);
        Assert.True(kept.Verdict != "verified" && OldPass(kept, edit) == "", "Keep turned stale marks into Verified: " + Marks(kept));
        var afterKeep = (await BothDoors(f.Engine)).ToDictionary(s => s.MeasureRef);
        Assert.True(afterKeep[Edited].Verdict != "verified" && OldPass(afterKeep[Edited], edit) == "", "after Keep: " + Marks(afterKeep[Edited]));

        // The rerun ends with no read: the Trusted number is Broken with the new value, and the measure that uses it reran too.
        g.Hold = false; g.Open.Set();
        await WaitSettled(f.Engine);
        var settled = (await BothDoors(f.Engine)).ToDictionary(s => s.MeasureRef);
        var trusted = settled[Edited].Marks.Single(m => m.Name == "Trusted number");
        Assert.True(trusted.Verdict == "broken" && trusted.Cells.Any(c => c.After == "350") && DateTimeOffset.Parse(trusted.LastRan!) >= edit,
            "the Trusted number did not rerun with the new value: " + Marks(settled[Edited]));
        Assert.Equal("broken", settled[Edited].Verdict);
        foreach (var reference in new[] { Edited, User })
            Assert.True(OldPass(settled[reference], edit) == "", $"{reference} kept a pass from before the edit: {Marks(settled[reference])}");
        var userFilters = settled[User].Marks.Single(m => m.Name == "Filters");
        Assert.True(DateTimeOffset.Parse(userFilters.LastRan!) >= DateTimeOffset.Parse(trusted.LastRan!),
            "the edited measure reruns first, then the measure that uses it: " + Marks(settled[Edited]) + " | " + Marks(settled[User]));

        // No state a screen got after the edit showed the Trusted number's old pass, and none read Verified for the edited measure.
        foreach (var s in pushed.Where(s => s.MeasureRef == Edited))
            Assert.True(s.Verdict != "verified" && OldPass(s, edit) == "", "a push after the edit read the old pass: " + Marks(s));
    }

    [Fact]
    public async Task An_edit_during_a_run_reruns_the_edited_measure_trusted_number_without_a_read()
    {
        using var g = new Gated(root); await g.Start();
        var f = g.F;
        await CheckNow(f.Engine);
        Assert.Equal("verified", (await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Edited).Verdict);

        // A run is going and waits on a query; the person edits the measure.
        g.Open.Reset(); g.Hold = true;
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25, Selection = new() { Measures = new[] { Edited, User } } });
        var running = f.Engine.RunMeasurePlanAsync(plan.Id);
        var clock = Stopwatch.StartNew();
        while (g.Waiting == 0 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
        Assert.True(g.Waiting > 0, "the run did not start");
        var edit = DateTimeOffset.UtcNow;
        await Edit(f);
        foreach (var door in new[] { await f.Engine.GetMeasureCheckCacheAsync(), await McpTools.GetMeasureCheckCache(f.Engine) })
        {
            var now = door.Single(s => s.MeasureRef == Edited);
            Assert.True(OldPass(now, edit) == "" && now.Verdict != "verified", "right after the edit: " + Marks(now));
        }
        g.Hold = false; g.Open.Set();
        var result = await running;
        Assert.True(result.StoppedOnEdit || result.Stopped, "the run went on after the edit");

        await WaitSettled(f.Engine);
        var states = (await BothDoors(f.Engine)).ToDictionary(s => s.MeasureRef);
        var trusted = states[Edited].Marks.Single(m => m.Name == "Trusted number");
        Assert.True(trusted.Verdict == "broken" && trusted.Cells.Any(c => c.After == "350") && DateTimeOffset.Parse(trusted.LastRan!) >= edit,
            "the Trusted number did not rerun after an edit during a run: " + Marks(states[Edited]));
        foreach (var reference in new[] { Edited, User })
            Assert.True(OldPass(states[reference], edit) == "", $"{reference} kept a pass from before the edit: {Marks(states[reference])}");
    }

    [Fact]
    public async Task Undo_after_the_rerun_comes_back_to_Verified_after_its_own_rerun()
    {
        using var g = new Gated(root); await g.Start();
        var f = g.F;
        await CheckNow(f.Engine);
        await Edit(f);
        var states = (await WaitSettled(f.Engine)).ToDictionary(s => s.MeasureRef);
        Assert.True(states[Edited].Verdict == "broken", Marks(states[Edited]));
        await f.Engine.UndoAsync("human");
        states = (await WaitSettled(f.Engine)).ToDictionary(s => s.MeasureRef);
        states = (await BothDoors(f.Engine)).ToDictionary(s => s.MeasureRef);
        Assert.True(states[Edited].Verdict == "verified" && states[Edited].Marks.Single(m => m.Name == "Trusted number").Verdict == "verified",
            "after Undo the measure reruns back to Verified: " + Marks(states[Edited]));
        Assert.True(states[User].Verdict == "verified", Marks(states[User]));
    }
}
