using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

// v-fx27 (verifier, round 2 of rc28): attacks on the D1 fix. Stop and an edit must end every check they cancel: no row reads
// Running, no mark stays pending, each measure keeps its last verdict (a Broken one stays Broken), and a cancelled measure
// stamps no new CheckedOn. The doors read are the tree and Checks rows (RPC cache), the assistant (MCP cache) and the card.
// The two Stop_while_a_plan_run_times_a_measure_ tests fail on 0e5439007: a plan run with Include performance times a measure
// after that measure's mark worker has finished (PlannedMeasureRunExtras.PerformanceAsync), so the worker's finally has already
// stamped CheckedOn on the new revision, and the timing's progress mark (Performance pending, "About 1 second remaining") is
// left in the record. SettleCancelledRuns skips that run (View.Running is false) and the plan's own write is cancelled, so the
// row, the assistant and the card's timing note keep reading it for good (5 s seen, every run).
[Collection("restore-root")]
public sealed class CheckedVFx27Tests : IDisposable
{
    private const string Broken = "measure:Sales/Total Sales";
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "vfx27-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedVFx27Tests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class GateTiming : IMeasureTimingSource
    {
        internal volatile bool Pause;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BenchmarkResult> WarmAsync(string query, int runs) => WarmAsync(query, runs, _ => { });
        public async Task<BenchmarkResult> WarmAsync(string query, int runs, Action<int> completed)
        {
            completed(runs);
            if (Pause) { Started.TrySetResult(); await Continue.Task; }
            return new BenchmarkResult { Runs = runs, RunsMs = Enumerable.Repeat(10L, runs).ToArray() };
        }
        public Task<ColdWarmBenchmark> ColdAsync(string query) => Task.FromResult(new ColdWarmBenchmark());
        public Task<ServerTimings> ProfileAsync(string query) => Task.FromResult(new ServerTimings());
    }

    private static string Describe(MeasureCheckCache s) => s.MeasureRef + (s.Running ? " running" : "") + " ["
        + string.Join(", ", s.Marks.Where(m => m.Pending || m.Progress != null).Select(m => $"{m.Name}:{(m.Pending ? "pending" : "")}:{m.Progress}")) + "]";

    private static bool Busy(MeasureCheckCache s) => s.Running || s.Marks.Any(m => m.Pending || m.Progress != null);

    // Every door must be settled: not running, no pending mark, no progress words, and the same verdict as before.
    private static async Task AssertSettled(LocalEngine engine, IReadOnlyDictionary<string, string> before, string why)
    {
        var rows = await engine.GetMeasureCheckCacheAsync();
        var agent = await McpTools.GetMeasureCheckCache(engine);
        var busy = rows.Concat(agent).Where(Busy).Select(Describe).ToArray();
        Assert.True(busy.Length == 0, $"{why}: {busy.Length} rows still read Checking or pending: {string.Join(" | ", busy)}");
        foreach (var reference in before.Keys)
        {
            // Opening a card checks a measure that has no check on this revision (by design), so a card read may start one.
            // That check must end by itself and keep the verdict; only a check that never ends is stuck.
            var card = await McpTools.GetMeasureMarks(engine, reference);
            var clock = Stopwatch.StartNew();
            while ((card.Running || card.Marks.Any(m => m.Pending)) && clock.Elapsed < TimeSpan.FromSeconds(20))
            { await Task.Delay(25); card = await McpTools.GetMeasureMarks(engine, reference); }
            var pending = card.Marks.Where(m => m.Pending || m.Progress != null).Select(m => m.Name).ToArray();
            Assert.True(!card.Running && pending.Length == 0, $"{why}: the card of {reference} reads running={card.Running}, pending {string.Join(", ", pending)}");
            var row = (await engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == reference);
            Assert.True(row.Verdict == before[reference], $"{why}: {reference} was {before[reference]}, now {row.Verdict}: "
                + string.Join(", ", row.Marks.Select(m => $"{m.Name}:{m.State}:{m.ReasonCode}:{m.Verdict}:{m.Sentence}")));
        }
    }

    private static async Task<MeasureCheckCache[]> WaitSettled(LocalEngine engine, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        var states = await engine.GetMeasureCheckCacheAsync();
        while (states.Any(Busy) && clock.Elapsed < limit) { await Task.Delay(25); states = await engine.GetMeasureCheckCacheAsync(); }
        return states;
    }

    private static async Task SaveBrokenTrustedNumber(MeasureMarksDoorTests.Fixture f) =>
        await f.Engine.SaveTestDefinitionAsync(new TestDefinition
        {
            Kind = TestKinds.MeasureValue, Title = "Contoso trusted total", TargetRef = Broken,
            ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = Broken, ExpectedValue = "999" })
        }, "human");

    // A plan run with Include performance, stopped while it times a measure (the timing runs after that measure's mark worker
    // has finished). Returns the verdicts and CheckedOn stamps before the stopped run, and the states after it.
    private static async Task<(Dictionary<string, string> Verdicts, Dictionary<string, string> CheckedOn, MeasureCheckCache[] After)>
        StopDuringTiming(MeasureMarksDoorTests.Fixture f, bool edit = false)
    {
        await f.Open(3);
        await SaveBrokenTrustedNumber(f);
        var timer = new GateTiming();
        f.Engine.MeasureTimingSourceForTests = timer;
        var first = await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25, IncludePerformance = true })).Id);
        Assert.False(first.Stopped);
        var states = await f.Engine.GetMeasureCheckCacheAsync();
        Assert.All(states, s => Assert.Contains(s.Marks, m => m.Name == "Performance" && m.LastRan != null && !m.Pending));
        var verdicts = states.ToDictionary(s => s.MeasureRef, s => s.Verdict);
        Assert.Equal("broken", verdicts[Broken]);
        // A Description edit moves the revision and no number (and is not a structure edit), so a new CheckedOn would differ from the one held.
        await f.Sessions.Current.MutateAsync("human", "Contoso note", m => m.Tables["Product"].Description = "Contoso products");
        var checkedOn = (await f.Engine.GetMeasureCheckCacheAsync()).Where(s => verdicts.ContainsKey(s.MeasureRef)).ToDictionary(s => s.MeasureRef, s => s.CheckedOn);

        timer.Pause = true;
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = verdicts.Keys.ToArray() }, Cap = 25, IncludePerformance = true });
        var run = f.Engine.RunMeasurePlanAsync(plan.Id);
        await timer.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var during = (await f.Engine.GetMeasureCheckCacheAsync()).Where(Busy).Select(Describe).ToArray();
        Console.WriteLine($"v-fx27 timing: while timing, {string.Join(" | ", during)}");
        // fx28: an edit cancels the run the same way Stop does (a Description edit moves no number and is not a structure edit).
        if (edit) await f.Sessions.Current.MutateAsync("human", "Contoso note again", m => m.Tables["Product"].Description = "Contoso products, all");
        else Assert.True(await f.Engine.StopMeasureRunAsync());
        // The live timing source cancels on the run token; this one is let go at once, as that would be.
        timer.Pause = false;
        timer.Continue.TrySetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(edit ? result.StoppedOnEdit : result.Stopped);
        var after = await f.Engine.GetMeasureCheckCacheAsync();
        Console.WriteLine($"v-fx27 timing: right after Stop, {string.Join(" | ", after.Where(Busy).Select(Describe))}");
        after = await WaitSettled(f.Engine, TimeSpan.FromSeconds(5));
        Console.WriteLine($"v-fx27 timing: 5 s after Stop, {string.Join(" | ", after.Where(Busy).Select(Describe))}");
        return (verdicts, checkedOn, after);
    }

    [Fact]
    public async Task Stop_while_a_plan_run_times_a_measure_leaves_no_mark_pending_on_any_door()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        var (verdicts, _, _) = await StopDuringTiming(f);
        await AssertSettled(f.Engine, verdicts, "after Stop during timing");
    }

    [Fact]
    public async Task Stop_while_a_plan_run_times_a_measure_stamps_no_new_CheckedOn()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        var (_, checkedOn, after) = await StopDuringTiming(f);
        var moved = after.Where(s => checkedOn.ContainsKey(s.MeasureRef) && s.CheckedOn != checkedOn[s.MeasureRef])
            .Select(s => $"{s.MeasureRef}: {checkedOn[s.MeasureRef]} -> {s.CheckedOn}").ToArray();
        Assert.True(moved.Length == 0, $"a measure Stop cancelled stamped a new CheckedOn: {string.Join(" | ", moved)}");
    }

    // fx28: an edit that lands while the plan run times a measure takes the same path as Stop.
    [Fact]
    public async Task An_edit_while_a_plan_run_times_a_measure_settles_every_door_and_stamps_no_new_CheckedOn()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        var (verdicts, checkedOn, after) = await StopDuringTiming(f, edit: true);
        var moved = after.Where(s => checkedOn.ContainsKey(s.MeasureRef) && s.CheckedOn != checkedOn[s.MeasureRef])
            .Select(s => $"{s.MeasureRef}: {checkedOn[s.MeasureRef]} -> {s.CheckedOn}").ToArray();
        Assert.True(moved.Length == 0, $"a measure the edit cancelled stamped a new CheckedOn: {string.Join(" | ", moved)}");
        await AssertSettled(f.Engine, verdicts, "after an edit during timing");
    }

    // An extra the run does not know by name: it runs after the measure's mark worker, as timing does, and waits on the run token.
    private sealed class FutureExtras : IMeasureRunExtras
    {
        internal volatile bool Hold;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Inputs => "contoso-future-extra-v1";
        public async Task<MeasureMark[]> ContextsAsync(string measureRef, CancellationToken cancellationToken)
        {
            if (Hold) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return Array.Empty<MeasureMark>();
        }
        public Task<MeasureMark> PerformanceAsync(string measureRef, CancellationToken cancellationToken) =>
            Task.FromResult(new MeasureMark { Name = "Performance", State = "passed", Sentence = "Contoso future timing." });
        public Task<double?> FastMatchAsync(MeasureRunCandidate candidate, CancellationToken cancellationToken) => Task.FromResult<double?>(null);
    }

    // fx28: the run ends once, after its last extra, whatever that extra is: until then the measure reads Checking, and Stop
    // during it settles every door and stamps no new CheckedOn.
    [Fact]
    public async Task Stop_during_an_extra_the_run_does_not_know_by_name_settles_and_stamps_no_new_CheckedOn()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(3);
        await SaveBrokenTrustedNumber(f);
        var extras = new FutureExtras();
        f.Engine.MeasureRunExtras = extras;
        Assert.False((await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25, IncludePerformance = true })).Id)).Stopped);
        var before = (await f.Engine.GetMeasureCheckCacheAsync()).ToDictionary(s => s.MeasureRef, s => s.Verdict);
        Assert.Equal("broken", before[Broken]);
        await f.Sessions.Current.MutateAsync("human", "Contoso note", m => m.Tables["Product"].Description = "Contoso products");
        var checkedOn = (await f.Engine.GetMeasureCheckCacheAsync()).ToDictionary(s => s.MeasureRef, s => s.CheckedOn);

        extras.Hold = true;
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = before.Keys.ToArray() }, Cap = 25, IncludePerformance = true });
        var run = f.Engine.RunMeasurePlanAsync(plan.Id);
        await extras.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var during = await f.Engine.GetMeasureCheckCacheAsync();
        Assert.True(during.Any(s => s.Running), "while the extra runs, no measure reads Checking: the run ended before its last extra");
        Assert.True(await f.Engine.StopMeasureRunAsync());
        Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(30))).Stopped);
        var after = await f.Engine.GetMeasureCheckCacheAsync();
        var busy = after.Where(Busy).Select(Describe).ToArray();
        Assert.True(busy.Length == 0, $"right after Stop, {busy.Length} rows still read Checking or pending: {string.Join(" | ", busy)}");
        var moved = after.Where(s => s.CheckedOn != checkedOn[s.MeasureRef]).Select(s => $"{s.MeasureRef}: {checkedOn[s.MeasureRef]} -> {s.CheckedOn}").ToArray();
        Assert.True(moved.Length == 0, $"a measure Stop cancelled stamped a new CheckedOn: {string.Join(" | ", moved)}");
        await AssertSettled(f.Engine, before, "after Stop during an unknown extra");
    }

    [Fact]
    public async Task Stop_ends_a_check_the_cards_own_read_started_during_a_plan_run()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(6);
        await SaveBrokenTrustedNumber(f);
        Assert.False((await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id)).Stopped);
        var before = (await f.Engine.GetMeasureCheckCacheAsync()).ToDictionary(s => s.MeasureRef, s => s.Verdict);
        var checkedOn = (await f.Engine.GetMeasureCheckCacheAsync()).ToDictionary(s => s.MeasureRef, s => s.CheckedOn);
        await f.Sessions.Current.MutateAsync("human", "Contoso note", m => m.Tables["Product"].Description = "Contoso products");

        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q => { Thread.Sleep(30); return f.Query(q); }));
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = before.Keys.ToArray() }, Cap = 25 });
        var run = f.Engine.RunMeasurePlanAsync(plan.Id);
        var clock = Stopwatch.StartNew();
        while ((await f.Engine.GetMeasureCheckCacheAsync()).Count(s => s.Running) == 0 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        // The person opens the Broken measure's card and presses Check now on it, while the plan run checks another measure.
        var card = await f.Engine.GetMeasureMarksAsync(Broken, refresh: true);
        Assert.True(card.Running, "the card's Check now did not start");
        Assert.True(await f.Engine.StopMeasureRunAsync());
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(result.Stopped);
        var after = await f.Engine.GetMeasureCheckCacheAsync();
        var busy = after.Where(Busy).Select(Describe).ToArray();
        Assert.True(busy.Length == 0, $"right after Stop, {busy.Length} rows still read Checking or pending: {string.Join(" | ", busy)}");
        Assert.True(after.Single(s => s.MeasureRef == Broken).CheckedOn == checkedOn[Broken], "the card's cancelled check stamped a new CheckedOn");
        // The workers Stop cancelled end later; nothing they do afterwards may bring Checking back or change a verdict.
        await Task.Delay(1500);
        after = await f.Engine.GetMeasureCheckCacheAsync();
        busy = after.Where(Busy).Select(Describe).ToArray();
        Assert.True(busy.Length == 0, $"1.5 s after Stop, {busy.Length} rows read Checking or pending: {string.Join(" | ", busy)}");
        Assert.True(after.Single(s => s.MeasureRef == Broken).CheckedOn == checkedOn[Broken], "a late worker stamped a new CheckedOn");
        await AssertSettled(f.Engine, before, "after Stop, every door");
    }

    [Fact]
    public async Task An_edit_of_the_measure_being_checked_ends_the_run_and_its_marks_settle()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(6);
        await SaveBrokenTrustedNumber(f);
        Assert.False((await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id)).Stopped);
        var before = (await f.Engine.GetMeasureCheckCacheAsync()).ToDictionary(s => s.MeasureRef, s => s.Verdict);
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q => { Thread.Sleep(30); return f.Query(q); }));
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = before.Keys.ToArray() }, Cap = 25 });
        var run = f.Engine.RunMeasurePlanAsync(plan.Id);
        var clock = Stopwatch.StartNew();
        MeasureCheckCache? checking = null;
        while (checking == null && clock.Elapsed < TimeSpan.FromSeconds(10))
        { checking = (await f.Engine.GetMeasureCheckCacheAsync()).FirstOrDefault(s => s.Running); await Task.Delay(5); }
        Assert.NotNull(checking);
        var name = checking!.MeasureRef[(checking.MeasureRef.IndexOf('/') + 1)..];
        // "+ 0" moves no number, so the measure's own compare run finds nothing moved and its last verdict holds.
        await f.Sessions.Current.MutateAsync("human", "Contoso plus zero", m => m.Tables["Sales"].Measures[name].Expression += " + 0");
        Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(30))).StoppedOnEdit);
        var after = await WaitSettled(f.Engine, TimeSpan.FromSeconds(20));
        Assert.True(!after.Any(Busy), "20 s after the edit: " + string.Join(" | ", after.Where(Busy).Select(Describe)));
        Assert.Equal("broken", after.Single(s => s.MeasureRef == Broken).Verdict);
    }

    // fx28 (v-fx27 polish b): adding a measure and undoing it changes no structure the other measures are checked on, so a Broken
    // measure stays Broken and no mark reads "Reopen the deployed model".
    [Fact]
    public async Task Add_then_Undo_of_a_measure_keeps_Broken_and_reads_no_structure_edit()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(6);
        await SaveBrokenTrustedNumber(f);
        Assert.False((await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id)).Stopped);
        var before = (await f.Engine.GetMeasureCheckCacheAsync()).ToDictionary(s => s.MeasureRef, s => s.Verdict);
        Assert.Equal("broken", before[Broken]);
        await f.Sessions.Current.MutateAsync("human", "Contoso add", m => m.Tables["Sales"].AddMeasure("Contoso Extra", "1"));
        await f.Engine.UndoAsync("human");
        var undone = await WaitSettled(f.Engine, TimeSpan.FromSeconds(20));
        Assert.True(!undone.Any(Busy), "20 s after Undo: " + string.Join(" | ", undone.Where(Busy).Select(Describe)));
        await AssertSettled(f.Engine, before, "after Add then Undo");
        // Check now after the Undo: every measure checks again on the connected model, and none reads a structure edit.
        var again = await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = before.Keys.ToArray() }, Cap = 25 })).Id);
        Assert.False(again.Stopped || again.StoppedOnEdit);
        var states = await f.Engine.GetMeasureCheckCacheAsync();
        var structure = states.SelectMany(s => s.Marks.Where(m => (m.Sentence ?? "").Contains(LocalEngine.StructureHoldReason)).Select(m => $"{s.MeasureRef} {m.Name}")).ToArray();
        Assert.True(structure.Length == 0, "after Add then Undo, marks read a structure edit: " + string.Join(", ", structure));
        Assert.Equal("broken", states.Single(s => s.MeasureRef == Broken).Verdict);
        await AssertSettled(f.Engine, before, "after Add, Undo and Check now");
    }

    // Control for the Stop then Undo case: the same Undo with no Stop before it. (These tests move the revision with a Description.)
    [Fact]
    public async Task Undo_alone_after_a_full_run_keeps_Broken()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(6);
        await SaveBrokenTrustedNumber(f);
        Assert.False((await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id)).Stopped);
        var before = (await f.Engine.GetMeasureCheckCacheAsync()).ToDictionary(s => s.MeasureRef, s => s.Verdict);
        await f.Sessions.Current.MutateAsync("human", "Contoso note", m => m.Tables["Product"].Description = "Contoso products");
        await f.Engine.UndoAsync("human");
        var undone = await WaitSettled(f.Engine, TimeSpan.FromSeconds(20));
        Assert.True(!undone.Any(Busy), "20 s after Undo: " + string.Join(" | ", undone.Where(Busy).Select(Describe)));
        await AssertSettled(f.Engine, before, "Undo with no Stop");
    }

    [Fact]
    public async Task Stop_then_Undo_then_Check_now_settles_keeps_Broken_and_stamps_the_new_revision()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(6);
        await SaveBrokenTrustedNumber(f);
        Assert.False((await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id)).Stopped);
        var before = (await f.Engine.GetMeasureCheckCacheAsync()).ToDictionary(s => s.MeasureRef, s => s.Verdict);
        await f.Sessions.Current.MutateAsync("human", "Contoso note", m => m.Tables["Product"].Description = "Contoso products");

        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q => { Thread.Sleep(30); return f.Query(q); }));
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = before.Keys.ToArray() }, Cap = 25 });
        var run = f.Engine.RunMeasurePlanAsync(plan.Id);
        var clock = Stopwatch.StartNew();
        while ((await f.Engine.GetMeasureCheckCacheAsync()).Count(s => s.Running) == 0 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        Assert.True(await f.Engine.StopMeasureRunAsync());
        Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(30))).Stopped);
        await AssertSettled(f.Engine, before, "after Stop");

        await f.Engine.UndoAsync("human");
        var undone = await WaitSettled(f.Engine, TimeSpan.FromSeconds(20));
        Assert.True(!undone.Any(Busy), "20 s after Undo: " + string.Join(" | ", undone.Where(Busy).Select(Describe)));
        await AssertSettled(f.Engine, before, "after Stop then Undo");

        // Check now on the run line, after Stop: a full run that stamps every measure on the current revision.
        f.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", f.Query));
        var again = await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = before.Keys.ToArray() }, Cap = 25 })).Id);
        Assert.False(again.Stopped);
        Assert.False(again.StoppedOnEdit);
        await AssertSettled(f.Engine, before, "after Check now");
        var revision = "|" + f.Sessions.Current.Revision + "|";
        var stale = (await f.Engine.GetMeasureCheckCacheAsync()).Where(s => before.ContainsKey(s.MeasureRef) && (s.CheckedOn == null || !s.CheckedOn.Contains(revision) || s.Stale))
            .Select(s => $"{s.MeasureRef}: {s.CheckedOn} stale={s.Stale}").ToArray();
        Assert.True(stale.Length == 0, "after Check now: " + string.Join(" | ", stale));
    }
}
