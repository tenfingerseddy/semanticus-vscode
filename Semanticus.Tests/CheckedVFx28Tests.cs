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

// v-fx28 (verifier, round 3 of rc28): attacks on the fx28 fix (a plan run ends each measure's run once, after its last extra).
// Stop then Check now after a stop during timing, two runs on the measure being timed where the older one is stopped (by Stop,
// by an edit of that measure), the older plan run ending while a newer card check is still open, and a real structure edit
// after the measure-delete rule changed. Every door must settle, a stopped run stamps no CheckedOn, Broken stays Broken.
[Collection("restore-root")]
public sealed class CheckedVFx28Tests : IDisposable
{
    private const string Broken = "measure:Sales/Total Sales";
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "vfx28-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedVFx28Tests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
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

    private static async Task AssertSettled(LocalEngine engine, IReadOnlyDictionary<string, string> before, string why)
    {
        var rows = await engine.GetMeasureCheckCacheAsync();
        var agent = await McpTools.GetMeasureCheckCache(engine);
        var busy = rows.Concat(agent).Where(Busy).Select(Describe).ToArray();
        Assert.True(busy.Length == 0, $"{why}: {busy.Length} rows still read Checking or pending: {string.Join(" | ", busy)}");
        foreach (var reference in before.Keys)
        {
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

    private sealed record Timed(GateTiming Timer, Dictionary<string, string> Verdicts, Dictionary<string, string> CheckedOn,
        Task<MeasureRunResult> Run, string Measure);

    // A full timed run, a Description edit (the revision moves, no number does), then a timed plan run paused while it times a
    // measure. Returns the measure being timed (its Performance mark carries the timing words).
    private static async Task<Timed> PauseInTiming(MeasureMarksDoorTests.Fixture f, int measures)
    {
        await f.Open(measures);
        await SaveBrokenTrustedNumber(f);
        var timer = new GateTiming();
        f.Engine.MeasureTimingSourceForTests = timer;
        Assert.False((await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25, IncludePerformance = true })).Id)).Stopped);
        var states = await f.Engine.GetMeasureCheckCacheAsync();
        var verdicts = states.ToDictionary(s => s.MeasureRef, s => s.Verdict);
        Assert.Equal("broken", verdicts[Broken]);
        await f.Sessions.Current.MutateAsync("human", "Contoso note", m => m.Tables["Product"].Description = "Contoso products");
        var checkedOn = (await f.Engine.GetMeasureCheckCacheAsync()).ToDictionary(s => s.MeasureRef, s => s.CheckedOn);
        timer.Pause = true;
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = verdicts.Keys.ToArray() }, Cap = 25, IncludePerformance = true });
        var run = f.Engine.RunMeasurePlanAsync(plan.Id);
        await timer.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var timing = (await f.Engine.GetMeasureCheckCacheAsync()).Where(s => s.Marks.Any(m => m.Name == "Performance" && m.Progress != null)).Select(s => s.MeasureRef).ToArray();
        Assert.True(timing.Length == 1, "while timing, rows with timing words: " + string.Join(", ", timing));
        return new(timer, verdicts, checkedOn, run, timing[0]);
    }

    private static string[] Moved(MeasureCheckCache[] after, Dictionary<string, string> checkedOn, params string[] only) =>
        after.Where(s => checkedOn.ContainsKey(s.MeasureRef) && (only.Length == 0 || only.Contains(s.MeasureRef)) && s.CheckedOn != checkedOn[s.MeasureRef])
            .Select(s => $"{s.MeasureRef}: {checkedOn[s.MeasureRef]} -> {s.CheckedOn}").ToArray();

    [Fact]
    public async Task Stop_during_timing_then_Check_now_times_and_stamps_every_measure_and_keeps_Broken()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        var t = await PauseInTiming(f, 4);
        Assert.True(await f.Engine.StopMeasureRunAsync());
        t.Timer.Pause = false; t.Timer.Continue.TrySetResult();
        Assert.True((await t.Run.WaitAsync(TimeSpan.FromSeconds(30))).Stopped);
        var stopped = await f.Engine.GetMeasureCheckCacheAsync();
        Assert.True(!stopped.Any(Busy), "right after the stopped run returned: " + string.Join(" | ", stopped.Where(Busy).Select(Describe)));
        var moved = Moved(stopped, t.CheckedOn);
        Assert.True(moved.Length == 0, "the stopped run stamped CheckedOn: " + string.Join(" | ", moved));

        // Check now on the run line, with Include performance: every measure is checked and timed on this revision.
        var started = DateTimeOffset.UtcNow;
        var again = await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = t.Verdicts.Keys.ToArray() }, Cap = 25, IncludePerformance = true })).Id);
        Assert.False(again.Stopped || again.StoppedOnEdit);
        var states = await f.Engine.GetMeasureCheckCacheAsync();
        var revision = "|" + f.Sessions.Current.Revision + "|";
        var unstamped = states.Where(s => s.CheckedOn == null || !s.CheckedOn.Contains(revision) || s.Stale).Select(s => $"{s.MeasureRef}: {s.CheckedOn} stale={s.Stale}").ToArray();
        Assert.True(unstamped.Length == 0, "after Check now: " + string.Join(" | ", unstamped));
        var untimed = states.Where(s => s.Marks.FirstOrDefault(m => m.Name == "Performance") is not { } p || p.Pending
            || !DateTimeOffset.TryParse(p.LastRan, out var ran) || ran < started).Select(s => s.MeasureRef).ToArray();
        Assert.True(untimed.Length == 0, "after Check now, Performance not timed again: " + string.Join(", ", untimed));
        await AssertSettled(f.Engine, t.Verdicts, "after Stop during timing, then Check now");
    }

    // Two runs on the measure being timed: the plan's (older) and the card's own timed Check now (newer, queued behind the timing).
    // Stop stops both. Neither may leave the measure Checking, and neither stamps CheckedOn.
    [Fact]
    public async Task Stop_with_a_card_check_queued_on_the_measure_being_timed_settles_both_and_stamps_nothing()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        var t = await PauseInTiming(f, 3);
        var card = await f.Engine.GetMeasureMarksAsync(t.Measure, refresh: true, includePerformance: true);
        Assert.True(card.Running, "the card's Check now did not start");
        Assert.True(await f.Engine.StopMeasureRunAsync());
        t.Timer.Pause = false; t.Timer.Continue.TrySetResult();
        Assert.True((await t.Run.WaitAsync(TimeSpan.FromSeconds(30))).Stopped);
        var after = await WaitSettled(f.Engine, TimeSpan.FromSeconds(5));
        Assert.True(!after.Any(Busy), "5 s after Stop: " + string.Join(" | ", after.Where(Busy).Select(Describe)));
        await Task.Delay(1500);
        after = await f.Engine.GetMeasureCheckCacheAsync();
        Assert.True(!after.Any(Busy), "1.5 s later: " + string.Join(" | ", after.Where(Busy).Select(Describe)));
        var moved = Moved(after, t.CheckedOn);
        Assert.True(moved.Length == 0, "a stopped run stamped CheckedOn: " + string.Join(" | ", moved));
        await AssertSettled(f.Engine, t.Verdicts, "after Stop with two runs on the timed measure");
    }

    // The older plan run ends (no Stop) while the card's newer check on the same measure is still queued: the row reads Checking
    // until the card's check ends, then settles, and the card's finished check stamps the measure.
    [Fact]
    public async Task The_plan_run_ending_under_a_newer_card_check_settles_when_the_card_check_ends()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        var t = await PauseInTiming(f, 3);
        Assert.True((await f.Engine.GetMeasureMarksAsync(t.Measure, refresh: true, includePerformance: true)).Running);
        t.Timer.Pause = false; t.Timer.Continue.TrySetResult();
        var result = await t.Run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.Stopped || result.StoppedOnEdit);
        var after = await WaitSettled(f.Engine, TimeSpan.FromSeconds(20));
        Assert.True(!after.Any(Busy), "20 s after the run: " + string.Join(" | ", after.Where(Busy).Select(Describe)));
        var revision = "|" + f.Sessions.Current.Revision + "|";
        var unstamped = after.Where(s => s.CheckedOn == null || !s.CheckedOn.Contains(revision)).Select(s => $"{s.MeasureRef}: {s.CheckedOn}").ToArray();
        Assert.True(unstamped.Length == 0, "a finished check left a measure unstamped: " + string.Join(" | ", unstamped));
        await AssertSettled(f.Engine, t.Verdicts, "after the plan run and the card check");
    }

    // An edit of the measure being timed: the plan's run (older) is cancelled by the edit and the edit's comparison is the newer run.
    // The comparison ends the record; the cancelled run stamps nothing; Broken stays Broken (the formula keeps every number).
    // fx34: the edit's run reruns the number marks of each measure it compares, so it stamps them on the edit's revision.
    [Fact]
    public async Task An_edit_of_the_measure_being_timed_settles_through_its_comparison_and_keeps_Broken()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        var t = await PauseInTiming(f, 3);
        var name = t.Measure[(t.Measure.IndexOf('/') + 1)..];
        await f.Sessions.Current.MutateAsync("human", "Contoso same number", m =>
        {
            var measure = m.Tables["Sales"].Measures[name];
            measure.Expression = "(" + measure.Expression + ") + 0";
        });
        t.Timer.Pause = false; t.Timer.Continue.TrySetResult();
        Assert.True((await t.Run.WaitAsync(TimeSpan.FromSeconds(30))).StoppedOnEdit);
        var after = await WaitSettled(f.Engine, TimeSpan.FromSeconds(20));
        Assert.True(!after.Any(Busy), "20 s after the edit: " + string.Join(" | ", after.Where(Busy).Select(Describe)));
        var revision = "|" + f.Sessions.Current.Revision + "|";
        var moved = Moved(after, t.CheckedOn).Where(m => !m[(m.IndexOf(" -> ", StringComparison.Ordinal) + 4)..].Contains(revision)).ToArray();
        Assert.True(moved.Length == 0, "the cancelled run stamped CheckedOn, not the edit's rerun: " + string.Join(" | ", moved));
        Assert.Equal("broken", after.Single(s => s.MeasureRef == Broken).Verdict);
        var timingWords = after.SelectMany(s => s.Marks.Where(m => ((m.Sentence ?? "").StartsWith("Timing", StringComparison.Ordinal) || m.Progress != null)).Select(m => s.MeasureRef)).ToArray();
        Assert.True(timingWords.Length == 0, "timing words stayed: " + string.Join(", ", timingWords));
    }

    // fx28 changed which deltas are a structure edit. fx31: a structure change holds only the measures it touches (through
    // Lineage), so a new column that no measure uses holds none of them, and none reads Unknown or a held change.
    [Fact]
    public async Task A_new_column_no_measure_uses_holds_no_measure()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(3);
        await SaveBrokenTrustedNumber(f);
        Assert.False((await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id)).Stopped);
        var refs = (await f.Engine.GetMeasureCheckCacheAsync()).Select(s => s.MeasureRef).ToArray();
        await f.Sessions.Current.MutateAsync("human", "Contoso column", m => m.Tables["Sales"].AddDataColumn("Contoso Extra", "ContosoExtra", null, TabularEditor.TOMWrapper.DataType.Int64));
        await WaitSettled(f.Engine, TimeSpan.FromSeconds(20));
        Assert.False((await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = refs }, Cap = 25 })).Id)).Stopped);
        var states = await f.Engine.GetMeasureCheckCacheAsync();
        var structure = states.Where(s => s.Marks.Any(m => (m.Sentence ?? "").Contains(LocalEngine.StructureHoldReason))).Select(s => s.MeasureRef).ToArray();
        Assert.True(structure.Length == 0, "a new column no measure uses holds: " + string.Join(", ", structure));
        Assert.Equal("broken", states.Single(s => s.MeasureRef == Broken).Verdict);
        Assert.DoesNotContain(states, s => s.Verdict == "unknown");
    }
    // Gap words, past the fix's own cases: a scaling format, an optional-digit format, a word format, a three-section format with
    // a word for zero, a whole-percent format. Each reads a non-zero gap in the measure's format, rounded as the format rounds
    // (0.008 under "$#,0.00" is $0.01, as the two values themselves read $0.00 and ($0.00)).
    [Theory]
    [InlineData("1000000", "999700", "#,0,", "0.3")]
    [InlineData("0.5", "0.504", "0%", "0.4%")]
    [InlineData("1", "1.0001", "0.0##", "0.0001")]
    [InlineData("10", "7", "Yes/No", "3")]
    [InlineData("5", "5.004", "#,0;(#,0);\"-\"", "0.004")]
    [InlineData("-0.004", "0.004", "$#,0.00;($#,0.00)", "$0.01")]
    [InlineData("123456789.12", "123456789.125", "#,0.00", "0.01")]
    public void A_gap_in_other_formats_reads_right_and_never_zero(string before, string after, string format, string expected) =>
        Assert.Equal(expected, MarkNumberFormat.Gap(before, after, format));
}
