using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

// j29 (join of fx29a and fx29b). 1. The timing baseline is a value that outlives its run, as the relationship scan was (fx29a).
// A plan run times a measure; Stop lands while its last context is timed and that benchmark still returns. The timing must not
// publish its baseline: a later edit would compare its Performance against a run the person stopped.
// 2. fx29b checks a measure again on its next read after Turn on; fx29a sends a card's check through the plan when an outside
// checker owns the checks. Joined, that recheck must go through the plan too, or the card keeps "Check again" for good.
[Collection("restore-root")]
public sealed class CheckedJ29Tests : IDisposable
{
    private const string Reference = "measure:Sales/Total Sales";
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "j29-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedJ29Tests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    // Each benchmark costs more than the quick budget, so the first context is also the last one timed: no loop turn
    // looks at the run again before the baseline is written.
    private sealed class GateTiming : IMeasureTimingSource
    {
        internal volatile bool Pause;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BenchmarkResult> WarmAsync(string query, int runs) => WarmAsync(query, runs, _ => { });
        public async Task<BenchmarkResult> WarmAsync(string query, int runs, Action<int> completed)
        {
            completed(runs);
            if (Pause) { Pause = false; Started.TrySetResult(); await Continue.Task; }
            return new BenchmarkResult { Runs = runs, RunsMs = Enumerable.Repeat(1000L, runs).ToArray() };
        }
        public Task<ColdWarmBenchmark> ColdAsync(string query) => Task.FromResult(new ColdWarmBenchmark());
        public Task<ServerTimings> ProfileAsync(string query) => Task.FromResult(new ServerTimings());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stopped_timing_leaves_no_baseline_for_the_next_edit(bool stop)
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open();
        var timer = new GateTiming { Pause = true };
        f.Engine.MeasureTimingSourceForTests = timer;
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = new[] { Reference } }, Cap = 25, IncludePerformance = true });
        var run = f.Engine.RunMeasurePlanAsync(plan.Id);
        await timer.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        if (stop) Assert.True(await f.Engine.StopMeasureRunAsync());
        timer.Continue.TrySetResult();
        Assert.Equal(stop, (await run.WaitAsync(TimeSpan.FromSeconds(30))).Stopped);

        await f.Sessions.Current.MutateAsync("human", "Edit Contoso sales", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        var performance = (await MeasureMarksTests.Finish(f.Engine, Reference)).Marks.Single(m => m.Name == "Performance");
        var compared = performance.Timings.Any(t => t.BeforeMedianMs != null);
        // The control: a timing that finished is the baseline the edit is compared against.
        if (!stop) Assert.True(compared, $"a finished timing gave the edit no baseline: {performance.State}: {performance.Sentence}");
        else Assert.False(compared, $"the edit was compared against the timing Stop cancelled: {performance.State}: {performance.Sentence}");
    }

    // An outside checker owns Filters, Contexts and Totals (the engine scores no Totals of its own then). Its Totals line is
    // the advice-off line until permission is given.
    private sealed class OutsideExtras : IMeasureRunExtras
    {
        internal volatile bool Given;
        public string Inputs => "contoso-outside-checker-j29";
        public Task<MeasureMark[]> ContextsAsync(string measureRef, CancellationToken cancellationToken) => Task.FromResult(new[]
        {
            new MeasureMark { Name = "Filters", State = "passed", Sentence = "Contoso outside filters checked." },
            new MeasureMark { Name = "Contexts", State = "passed", Sentence = "Contoso outside contexts checked." },
            Given ? new MeasureMark { Name = "Totals", State = "passed", Sentence = "Contoso outside totals checked." }
                : ScoringCopy.NotScored("Totals", ScoringCopy.AdviceOff)
        });
        public Task<MeasureMark> PerformanceAsync(string measureRef, CancellationToken cancellationToken) =>
            Task.FromResult(new MeasureMark { Name = "Performance", State = "passed", Sentence = "Contoso outside timing." });
        public Task<double?> FastMatchAsync(MeasureRunCandidate candidate, CancellationToken cancellationToken) => Task.FromResult<double?>(null);
    }

    [Fact]
    public async Task After_Turn_on_an_outside_checker_still_rechecks_the_measure_on_its_next_read()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open();
        // fx30e K5: a real state (a relay, permission off), so the stored line is what the state says, not a stub's word.
        f.Engine.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(new CheckedFx29cTests.Relay()));
        var outside = new OutsideExtras();
        f.Engine.MeasureRunExtras = outside;
        Assert.False((await f.Engine.RunMeasurePlanAsync((await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id)).Stopped);
        var first = (await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Reference);
        Assert.EndsWith(ScoringCopy.AdviceOff, first.Marks.Single(m => m.Name == "Totals").Sentence);

        var consent = await f.Engine.GetConsentAsync();
        outside.Given = true;
        Assert.True((await f.Engine.SetConsentAsync(consent.FieldsInForce.ToArray(), consent.SchemaVersionInForce, "human")).Current);
        string sentence = null;
        for (var i = 0; i < 100; i++)
        {
            var marks = await f.Engine.GetMeasureMarksAsync(Reference, null, false, "human");
            sentence = marks.Marks.Single(m => m.Name == "Totals").Sentence;
            if (!marks.Running && sentence != null && !ScoringCopy.ConsentLine(sentence)) break;
            await Task.Delay(100);
        }
        Assert.False(sentence == null || ScoringCopy.ConsentLine(sentence), "10 s after Turn on the card still reads: " + sentence);
    }
}
