using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests;

// fx30e: the engine faults of the wr1 review (faults.md K1 to K5, F2 to F7, S1), each at its cause. A stored line follows the
// state that decided it; Stop is heard from the moment Run is pressed; the engine's own words are the spec's words; a Totals
// mark the scorer does not judge does not apply; a Description edit is not an edit of the measure's numbers.
[Collection("restore-root")]
public sealed class CheckedFx30eTests : IDisposable
{
    private const string Reference = "measure:Sales/Total Sales";
    private const string AdviceOff = "Pro advice is off for this model.";
    private const string NeedsPro = "Scoring needs a Pro plan.";
    private const string Refused = "you have not given permission to send this model's details for advice";
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "fx30e-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckedFx30eTests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class NoRelay : IDecisionOracle
    {
        public bool Available => false;
        public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct) => throw new InvalidOperationException("no relay");
    }

    private static async Task<MeasureRunResult> RunAll(LocalEngine engine)
        => await engine.RunMeasurePlanAsync((await engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 })).Id);

    private static async Task TurnOn(LocalEngine engine)
    {
        var consent = await engine.GetConsentAsync();
        Assert.True((await engine.SetConsentAsync(consent.FieldsInForce.ToArray(), consent.SchemaVersionInForce, "human")).Current);
    }

    private static string Totals(MeasureMark[] marks) => marks.Single(m => m.Name == "Totals").Sentence;

    // Every door that hands out a measure's Totals line: the run's check cache (RPC and the assistant) and the card.
    private static async Task<string[]> TotalsLines(LocalEngine engine, string reference)
    {
        var rpc = new EngineRpcTarget(engine);
        return new[] {
            Totals((await engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == reference).Marks),
            Totals((await McpTools.GetMeasureCheckCache(engine)).Single(s => s.MeasureRef == reference).Marks),
            Totals((await rpc.getMeasureCheckCache()).Single(s => s.MeasureRef == reference).Marks),
            Totals((await engine.GetMeasureMarksAsync(reference)).Marks)
        };
    }

    // K1 addendum: Turn on rewrote every stored consent line, and a run under the earlier permission had replaced a scored total
    // with one. Turn on itself must never throw a scored total away: it changes only the lines permission decided.
    [Fact]
    public async Task Turn_on_keeps_a_Totals_that_was_already_scored()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open(2);
        f.Engine.UseDecisionsForTest(new DecisionService(new CheckedFx29cTests.Relay()));
        await TurnOn(f.Engine);
        Assert.False((await RunAll(f.Engine)).Stopped);
        var scored = (await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Reference).Marks.Single(m => m.Name == "Totals");
        Assert.NotNull(scored.Total);
        Assert.False((await f.Engine.WithdrawConsentAsync("human")).Current);
        await TurnOn(f.Engine);
        foreach (var line in await TotalsLines(f.Engine, Reference))
            Assert.True(line == scored.Sentence, $"Turn on threw the scored total away: read \"{line}\", scored \"{scored.Sentence}\"");
    }

    // K3: after Turn on, the measure whose recheck runs is checking now. "Check again to score it" asked the person to do what
    // the engine was already doing, for the 13 s the recheck took.
    [Fact]
    public async Task After_Turn_on_a_measure_being_rechecked_reads_checking_now()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open();
        f.Engine.UseDecisionsForTest(new DecisionService(new CheckedFx29cTests.Relay()));
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var given = false;
        f.Engine.TotalsScorer = new JevTotalsScorer(async (_, _, _, ct) =>
        {
            if (!given) return DecisionOutcome.Skipped(Refused);
            await hold.Task.WaitAsync(ct);
            return DecisionOutcome.Skipped("no suggestion service is set up, so nothing was sent and nothing changed");
        });
        Assert.False((await RunAll(f.Engine)).Stopped);
        Assert.EndsWith(AdviceOff, Totals((await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Reference).Marks));
        var pushed = new List<MeasureCheckCache>();
        f.Sessions.Bus.ChecksChanged += c => { lock (pushed) pushed.AddRange(c.States.Where(s => s.MeasureRef == Reference)); };
        given = true;
        await TurnOn(f.Engine);
        try
        {
            await f.Engine.GetMeasureMarksAsync(Reference);   // the card reads again; its recheck starts
            var clock = Stopwatch.StartNew();
            while (!(await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Reference).Running)
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the recheck after Turn on never started");
                await Task.Delay(20);
            }
            var card = await f.Engine.GetMeasureMarksAsync(Reference);
            Assert.True(card.Running, "the card does not read checking now while its recheck runs");
            MeasureCheckCache last;
            lock (pushed) last = pushed.Last();
            Assert.True(last.Running, "the last push does not say the measure is checking");
            foreach (var line in (await TotalsLines(f.Engine, Reference)).Append(Totals(last.Marks)))
            {
                Assert.DoesNotContain("Check again", line);
                Assert.DoesNotContain(AdviceOff, line);
            }
        }
        finally { hold.TrySetResult(); }
    }

    // K5: a blocking line stored under one cause is kept after the cause changed. Here permission was off, then the licence went
    // (no licence means no relay client): every door must read "needs a Pro plan", not the advice line.
    [Fact]
    public async Task A_stored_advice_off_line_gives_way_when_the_licence_is_gone()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open();
        f.Engine.UseDecisionsForTest(new DecisionService(new CheckedFx29cTests.Relay()));
        f.Engine.TotalsScorer = new JevTotalsScorer((_, _, _, _) => Task.FromResult(DecisionOutcome.Skipped(Refused)));
        Assert.False((await RunAll(f.Engine)).Stopped);
        Assert.EndsWith(AdviceOff, Totals((await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Reference).Marks));
        f.Engine.UseDecisionsForTest(new DecisionService(new NoRelay()));
        foreach (var line in await TotalsLines(f.Engine, Reference))
            Assert.True(line == "Not scored yet. " + NeedsPro, "the stored line outlived its cause: " + line);
    }

    // K4: Stop pressed while the run still reads its candidates was not heard (activeMeasureRun was set after that read), so the
    // run went on for minutes. Here the candidate read waits behind another read of the model.
    [Fact]
    public async Task Stop_pressed_before_the_run_registers_stops_the_run()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open(3);
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 });
        var held = new ManualResetEventSlim(false);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = f.Sessions.Current.ReadAsync(_ => { reading.TrySetResult(); held.Wait(TimeSpan.FromSeconds(20)); return 0; });
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var run = f.Engine.RunMeasurePlanAsync(plan.Id);
        bool stopped;
        try { stopped = await f.Engine.StopMeasureRunAsync(); }
        finally { held.Set(); }
        await block;
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(stopped, "Stop before the run registered returned false");
        Assert.True(result.Stopped, "the run went on after Stop");
        Assert.DoesNotContain(await f.Engine.GetMeasureCheckCacheAsync(), s => s.Running);
    }

    // F3: the engine's timing progress words reach the assistant and every push. No run counts, no Cold or Warm words (spec D4).
    [Fact]
    public async Task Timing_progress_has_no_run_counts_and_no_cold_or_warm_words()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open();
        var seen = new List<string>();
        f.Sessions.Bus.ChecksChanged += c =>
        {
            lock (seen) seen.AddRange(c.States.SelectMany(s => s.Marks).Where(m => m.Name == "Performance").Select(m => m.Progress ?? m.Sentence).Where(t => t != null));
        };
        f.Engine.MeasureTimingSourceForTests = new StepTiming();
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Measures = new[] { Reference } }, Cap = 25, IncludePerformance = true });
        Assert.False((await f.Engine.RunMeasurePlanAsync(plan.Id)).Stopped);
        string[] words;
        lock (seen) words = seen.Distinct().ToArray();
        Assert.Contains(words, w => w.StartsWith("Timing", StringComparison.Ordinal));
        foreach (var w in words)
            Assert.False(System.Text.RegularExpressions.Regex.IsMatch(w, @"\d+ of \d+ runs|\bruns done\b|\bCold\b|\bWarm\b"), "timing words with machinery: " + w);
        var performance = (await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Reference).Marks.Single(m => m.Name == "Performance");
        Assert.DoesNotContain("Request a full sweep", performance.Sentence);
        Assert.DoesNotContain("contexts timed", performance.Sentence);
    }

    private sealed class StepTiming : IMeasureTimingSource
    {
        public Task<BenchmarkResult> WarmAsync(string query, int runs) => WarmAsync(query, runs, _ => { });
        public Task<BenchmarkResult> WarmAsync(string query, int runs, Action<int> completed)
        {
            for (var i = 1; i <= runs; i++) completed(i);
            return Task.FromResult(new BenchmarkResult { Runs = runs, RunsMs = Enumerable.Repeat(10L, runs).ToArray() });
        }
        public Task<ColdWarmBenchmark> ColdAsync(string query) => Task.FromResult(new ColdWarmBenchmark());
        public Task<ServerTimings> ProfileAsync(string query) => Task.FromResult(new ServerTimings());
    }

    // F6: the cut sentences of spec section 5 that the engine still wrote (the screens rewrote them, the assistant read them).
    [Fact]
    public void Engine_sentences_carry_no_cut_text()
    {
        var plan = MeasureRunPlanner.Build(Enumerable.Range(0, 12).Select(i => new MeasureRunCandidate {
            MeasureRef = "measure:Contoso/Measure " + i, Inputs = "Contoso", Formula = "SUM(Contoso[Amount])"
        }).ToArray());
        Assert.True(plan.FirstRun);
        Assert.DoesNotContain("estimate improves", plan.Sentence);
        Assert.EndsWith(" About 3 minutes.", plan.Sentence);
        var moved = MeasureMarkJudge.Equivalence(new BaselineDiff { Verdict = "moved", RowsCompared = 4, MismatchCount = 2, Mismatches = new[] {
            new EquivalenceMismatch { Context = "Bikes", ValueA = "10", ValueB = "20" }, new EquivalenceMismatch { Context = "Cars", ValueA = "1", ValueB = "2" } } });
        Assert.Equal("Your edit moved numbers in 2 places.", moved.Sentence);
    }

    // F5: a sum whose grand total is exactly the sum of its rows is right, whatever the relay's fit score (0.38 on pilot2, between
    // the cuts, read Unknown "not clearly right for a sum" on SUM ( Sales[Quantity] ) * 2).
    [Fact]
    public async Task A_sum_whose_rows_add_up_passes_whatever_the_fit_score()
    {
        var probe = new TotalProbe("Product[Category]", new[] {
            new TotalProbeRow("Bikes", 628370), new TotalProbeRow("Accessories", 314185.5), new TotalProbeRow("Clothing", 314184.5) }, 1256740);
        var asked = new List<string>();
        var mark = await Scorer("sum_of_rows", .93, .38, asked).ScoreAsync(new("Total Quantity", "", "SUM ( Sales[Quantity] ) * 2"),
            new[] { probe }, new(), false, CancellationToken.None);
        Assert.Equal("passed", mark.State);
        Assert.Equal("Total adds up as a sum, as expected.", mark.Sentence);
        // A total that does not add up is still the relay's to judge.
        var off = await Scorer("sum_of_rows", .93, .38, asked).ScoreAsync(new("Total Quantity", "", "SUM ( Sales[Quantity] ) * 2"),
            new[] { probe with { Total = 1256741 } }, new(), false, CancellationToken.None);
        Assert.Equal("unknown", off.State);
        Assert.Equal("totals_unclear", off.ReasonCode);
        Assert.EndsWith(".", off.Sentence);
    }

    // F5: every Totals sentence the scorer writes ends with a full stop.
    [Theory]
    [InlineData("sum_of_rows", .93, .9)]
    [InlineData("recomputed_at_total", .95, .9)]
    [InlineData("average", .3, .9)]
    [InlineData("distinct_count", .99, .9)]
    [InlineData("recomputed_at_total", .6, .9)]
    public async Task Every_Totals_sentence_ends_with_a_full_stop(string rule, double confidence, double fit)
    {
        var probe = new TotalProbe("Product[Category]", new[] { new TotalProbeRow("Bikes", 10), new TotalProbeRow("Clothing", 30) }, 25);
        var mark = await Scorer(rule, confidence, fit, new()).ScoreAsync(new("Contoso", "", "[Sales]"), new[] { probe }, new(), false, CancellationToken.None);
        Assert.EndsWith(".", mark.Sentence);
        var expected = await Scorer(rule, confidence, fit, new()).ScoreAsync(new("Contoso", "", "[Sales]"), new[] { probe }, new(), true, CancellationToken.None);
        Assert.EndsWith(".", expected.Sentence);
    }

    private static JevTotalsScorer Scorer(string rule, double confidence, double fit, List<string> asked) => new((point, state, questions, ct) =>
    {
        lock (asked) asked.Add(point);
        var request = new DecisionRequest(point, 1, state, questions);
        using var json = JsonDocument.Parse(point == TotalsQuestions.TypePoint
            ? $"{{\"rule\":{{\"type\":\"choice\",\"choice\":\"{rule}\",\"confidence\":{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}}}"
            : $"{{\"fits\":{{\"type\":\"noul\",\"noul\":{fit.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}}}");
        return Task.FromResult(DecisionOutcome.Answered(DecisionAnswerReader.Read(request, json.RootElement, "Contoso fake")));
    });

    // F7, overturned by Kane on 1 Nov (f173): a Totals the scorer does not judge for its type, or whose type is unclear, applies
    // and shows the type Jev chose, with no verdict, so it never counts. Between the cuts stays Unknown.
    [Theory]
    [InlineData("totals_not_judged", true, null)]
    [InlineData("totals_type_unclear", true, null)]
    [InlineData("totals_distinct", true, null)]
    [InlineData("totals_unclear", true, "unknown")]
    public void A_Totals_the_scorer_does_not_judge_is_shown_and_never_counts(string code, bool applies, string? verdict)
    {
        var totals = MeasureCheckVerdict.Finish(new MeasureMark { Name = "Totals", State = code == "totals_unclear" ? "unknown" : "info",
            Sentence = "Checked as a recalc.", ReasonCode = code, LastRan = DateTimeOffset.UtcNow.ToString("O") }, null, false);
        Assert.Equal(applies, totals.Applies);
        Assert.Equal(verdict, totals.Verdict);
        var filters = MeasureCheckVerdict.Finish(new MeasureMark { Name = "Filters", State = "passed", Sentence = "No ignored filter in 4 places." }, null, false);
        Assert.Equal(verdict ?? "verified", MeasureCheckVerdict.Worst(new[] { totals, filters }));
    }

    // S1: keeping or editing a Description is not an edit of the measure's numbers, so Equivalence does not apply (spec section 3).
    [Fact]
    public async Task Keeping_a_Description_does_not_run_Equivalence()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open();
        await MeasureMarksTests.Finish(f.Engine, Reference);
        // Keeping a drafted Description writes it and its provenance annotation, as the card's Keep does.
        await f.Sessions.Current.MutateAsync("human", "Keep the Description", m =>
        {
            m.Tables["Sales"].Measures["Total Sales"].Description = "Total sales amount.";
            m.Tables["Sales"].Measures["Total Sales"].SetAnnotation(LocalEngine.DescriptionProvenanceAnnotation, "ai");
        });
        var read = await MeasureMarksTests.Finish(f.Engine, Reference);
        var equivalence = read.Marks.Single(m => m.Name == "Equivalence");
        Assert.False(equivalence.Applies, $"Equivalence applies after a Description edit: {equivalence.State}: {equivalence.Sentence}");
        var state = (await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Reference);
        Assert.DoesNotContain(state.Marks, m => m.Name == "Equivalence" && m.Applies);
        // A formula edit still compares.
        await f.Sessions.Current.MutateAsync("human", "Edit the formula", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " * 2");
        var edited = await MeasureMarksTests.Finish(f.Engine, Reference);
        Assert.True(edited.Marks.Single(m => m.Name == "Equivalence").Applies, "a formula edit no longer compares");
    }

    // F4: a check record written while the session was offline (Filters and Contexts "connect a model") is not a check of the
    // connected model. Once connected, no door reads it as checked, so the tree cannot read Unknown "connect a model".
    [Fact]
    public async Task An_offline_check_record_does_not_read_as_checked_once_connected()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root);
        await f.Open(2);
        var live = LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", f.Query);
        f.Engine.SetLiveConnectionForTest(null);
        Assert.False((await RunAll(f.Engine)).Stopped);
        var offline = (await f.Engine.GetMeasureCheckCacheAsync()).Single(s => s.MeasureRef == Reference);
        Assert.Contains(offline.Marks, m => m.ReasonCode == "not_connected");
        var pushed = new List<MeasureCheckCache>();
        f.Sessions.Bus.ChecksChanged += c => { lock (pushed) pushed.AddRange(c.States); };
        f.Engine.SetLiveConnectionForTest(live);
        foreach (var state in await f.Engine.GetMeasureCheckCacheAsync())
        {
            Assert.DoesNotContain(state.Marks, m => m.ReasonCode == "not_connected");
            Assert.True(state.LastChecked == null, $"{state.MeasureRef} reads as checked on the connected model before any run");
        }
        // A door holding the offline record is told: the newest push of each measure carries no offline line.
        lock (pushed)
            foreach (var group in pushed.GroupBy(s => s.MeasureRef))
                Assert.DoesNotContain(group.OrderBy(s => s.Version).Last().Marks, m => m.ReasonCode == "not_connected");
        Assert.Contains(pushed, s => s.MeasureRef == Reference);
    }
}
