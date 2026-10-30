using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests;

// L1 (ra1 section 2): one engine-owned check state per measure. Every surface and both doors read it; every write pushes it
// with a newer version; Keep is an engine acknowledgement keyed by the closure inputs; Stop ends a run.
[Collection("restore-root")]
public sealed class CheckStateTests : IDisposable
{
    private readonly string safeRoot;
    private readonly string root = Path.Combine(Path.GetTempPath(), "contoso-state-" + Guid.NewGuid().ToString("N")[..8]);
    public CheckStateTests(RestoreRootFixture fixture) { safeRoot = fixture.Root; ConnectionRegistry.RootOverride = root; }
    public void Dispose()
    {
        ConnectionRegistry.RootOverride = safeRoot;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static async Task<MeasureCheckCache> Settled(IEngine engine, string reference, Func<MeasureCheckCache, bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var state = (await engine.GetMeasureCheckCacheAsync()).Single(c => c.MeasureRef == reference);
            if (!state.Running && done(state)) return state;
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), "The check state did not settle: " + JsonSerializer.Serialize(state.Marks.Select(m => m.Name + ":" + m.State)));
            await Task.Delay(10);
        }
    }

    private static MeasureMark Mark(MeasureCheckCache state, string name) => state.Marks.SingleOrDefault(m => m.Name == name);

    [Fact]
    public async Task Both_doors_read_one_verdict_and_every_write_pushes_a_newer_version()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open();
        var pushed = new ConcurrentQueue<CheckStateChange>();
        f.Sessions.Bus.ChecksChanged += pushed.Enqueue;
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        var rpc = (await f.Engine.GetMeasureCheckCacheAsync()).Single(c => c.MeasureRef == "measure:Sales/Total Sales");
        var mcp = (await McpTools.GetMeasureCheckCache(f.Engine)).Single(c => c.MeasureRef == "measure:Sales/Total Sales");
        var marks = await McpTools.GetMeasureMarks(f.Engine, "measure:Sales/Total Sales");
        // The verdict is the engine's: the same on the RPC read, the MCP read and the single-measure read.
        Assert.Equal(rpc.Verdict, mcp.Verdict);
        Assert.NotNull(rpc.Verdict);
        foreach (var mark in rpc.Marks.Where(m => MeasureCheckVerdict.Seven.Contains(m.Name)))
        {
            var other = marks.Marks.Single(m => m.Name == mark.Name);
            Assert.Equal((mark.Applies, mark.Verdict), (other.Applies, other.Verdict));
        }
        Assert.False(Mark(rpc, "SQL").Applies);
        Assert.Equal("no_sql", Mark(rpc, "SQL").ReasonCode);
        Assert.Equal("verified", Mark(rpc, "Filters").Verdict);
        // Each write was pushed, in order, and the last push is the state both doors read.
        var versions = pushed.Where(p => p.States.Any(s => s.MeasureRef == "measure:Sales/Total Sales")).Select(p => p.Version).ToArray();
        Assert.True(versions.Length >= 3, "a run start, its marks and its end are pushed");
        Assert.Equal(versions.OrderBy(v => v), versions);
        Assert.Equal(rpc.Version, pushed.Last(p => p.States.Any(s => s.MeasureRef == "measure:Sales/Total Sales")).States.Single(s => s.MeasureRef == "measure:Sales/Total Sales").Version);
        Assert.Equal(rpc.Version, marks.Version);
    }

    [Fact]
    public async Task Keep_is_the_engines_and_holds_by_the_inputs_the_edit_was_compared_on()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open();
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        await f.Sessions.Current.MutateAsync("human", "Contoso edit", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        var moved = await Settled(f.Engine, "measure:Sales/Total Sales", s => Mark(s, "Equivalence")?.State == "suspect");
        Assert.Equal("review", Mark(moved, "Equivalence").Verdict);
        var kept = await f.Engine.KeepMeasureEditAsync("measure:Sales/Total Sales");
        Assert.False(Mark(kept, "Equivalence").Applies);
        Assert.Equal("kept", Mark(kept, "Equivalence").ReasonCode);
        Assert.Equal(Mark(moved, "Equivalence").Inputs, kept.Keep.Inputs);
        // The assistant reads the Keep too (ra1 item 6): it lives in the engine, not in one window.
        var agent = await McpTools.GetMeasureMarks(f.Engine, "measure:Sales/Total Sales");
        Assert.False(agent.Marks.Single(m => m.Name == "Equivalence").Applies);
        Assert.NotEqual("review", (await f.Engine.GetMeasureCheckCacheAsync()).Single(c => c.MeasureRef == "measure:Sales/Total Sales").Verdict);
        // An edit of an unrelated measure keeps it; a new edit of this measure brings Equivalence back.
        await f.Sessions.Current.MutateAsync("human", "Contoso other", m => m.Tables["Sales"].AddMeasure("Contoso Other", "1"));
        Assert.False(Mark(await Settled(f.Engine, "measure:Sales/Total Sales", _ => true), "Equivalence").Applies);
        await f.Sessions.Current.MutateAsync("human", "Contoso edit again", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 1");
        var again = await Settled(f.Engine, "measure:Sales/Total Sales", s => Mark(s, "Equivalence")?.Inputs != kept.Keep.Inputs && Mark(s, "Equivalence")?.State != "not run");
        Assert.True(Mark(again, "Equivalence").Applies);
        Assert.NotEqual("kept", Mark(again, "Equivalence").ReasonCode);
    }

    [Fact]
    public async Task Undo_clears_a_dependents_Equivalence_without_opening_it()
    {
        // rc27 tester gap 3: Quantity PY stayed ▲ after Undo of Total Quantity until it was opened.
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(2);
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Sales Copy 1");
        await f.Sessions.Current.MutateAsync("human", "Change Contoso dependency", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        var moved = await Settled(f.Engine, "measure:Sales/Sales Copy 1", s => Mark(s, "Equivalence")?.State == "suspect");
        Assert.Equal("review", moved.Verdict);
        await f.Sessions.Current.UndoAsync("human");
        // No getMeasureMarks: only the state the tree and the Checks rows read.
        var undone = await Settled(f.Engine, "measure:Sales/Sales Copy 1", s => Mark(s, "Equivalence")?.State != "suspect");
        Assert.NotEqual("review", undone.Verdict);
        Assert.True(Mark(undone, "Equivalence") is null || Mark(undone, "Equivalence").Verdict != "review");
    }

    [Fact]
    public async Task On_purpose_turns_a_lost_filter_to_overridden_on_both_doors_and_Undo_brings_it_back()
    {
        // 1.7 item 2 at the join: the person's word reaches the one finished state, so the card, the Checks rows and the
        // assistant agree at once, before any new run, and Undo takes the word back.
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open();
        const string reference = "measure:Sales/Total Sales";
        await f.Sessions.Current.MutateAsync("human", "Contoso filter clear",
            m => m.Tables["Sales"].Measures["Total Sales"].Expression = "CALCULATE(SUM('Sales'[Amount]), ALL('Product'))");
        await f.Engine.GetMeasureMarksAsync(reference, refresh: true);
        await MeasureMarksTests.Finish(f.Engine, reference);
        var rpc = new EngineRpcTarget(f.Engine);
        async Task<(MeasureMark Rpc, MeasureMark Mcp, MeasureMark Card)> Filters()
        {
            var row = (await rpc.getMeasureCheckCache()).Single(c => c.MeasureRef == reference);
            var agent = (await McpTools.GetMeasureCheckCache(f.Engine)).Single(c => c.MeasureRef == reference);
            var card = await McpTools.GetMeasureMarks(f.Engine, reference);
            return (Mark(row, "Filters"), Mark(agent, "Filters"), card.Marks.Single(m => m.Name == "Filters"));
        }
        static void Agree((MeasureMark Rpc, MeasureMark Mcp, MeasureMark Card) marks, string code, string verdict)
        {
            foreach (var mark in new[] { marks.Rpc, marks.Mcp, marks.Card })
            {
                Assert.Equal(code, mark.ReasonCode);
                Assert.Equal(verdict, mark.Verdict);
                Assert.True(mark.Applies);
            }
        }
        var lost = await Filters();
        Agree(lost, "filters_lost", "review");
        Assert.Equal(new[] { "fix_formula", "on_purpose" }, lost.Rpc.ActionCodes);

        var pushed = new ConcurrentQueue<CheckStateChange>();
        f.Sessions.Bus.ChecksChanged += pushed.Enqueue;
        Assert.True(await rpc.setMeasureFiltersOnPurpose(reference, true));
        var marked = await Filters();
        Agree(marked, "filters_on_purpose", "overridden");
        Assert.Equal(new[] { "not_on_purpose" }, marked.Mcp.ActionCodes);
        Assert.Contains(pushed, p => p.States.Any(s => s.MeasureRef == reference && Mark(s, "Filters")?.Verdict == "overridden"));
        // C6 is the Filters mark. The row still reads review for one other reason: the formula edit moved the numbers, so
        // Equivalence asks for Keep (C3). No mark but Equivalence may hold the row at review.
        var rowAfter = (await rpc.getMeasureCheckCache()).Single(c => c.MeasureRef == reference);
        Assert.Equal(new[] { "Equivalence" }, rowAfter.Marks.Where(m => m.Applies && m.Verdict == "review").Select(m => m.Name).ToArray());

        await f.Engine.UndoAsync("human");
        Agree(await Filters(), "filters_lost", "review");
        Assert.Contains(pushed, p => p.States.Any(s => s.MeasureRef == reference && Mark(s, "Filters")?.Verdict == "review"));

        // The assistant door says the same word, and a finished run after it keeps the override.
        Assert.True(await McpTools.SetMeasureFiltersOnPurpose(f.Engine, reference, true));
        Agree(await Filters(), "filters_on_purpose", "overridden");
        await f.Engine.GetMeasureMarksAsync(reference, refresh: true);
        await MeasureMarksTests.Finish(f.Engine, reference);
        Agree(await Filters(), "filters_on_purpose", "overridden");
    }

    [Fact]
    public async Task A_queued_timing_says_how_many_checks_are_ahead_of_it()
    {
        // rc24 release 1, gap 1: "About 30 seconds" for 7 minutes. The pending Performance mark carries the queue.
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(3);
        await f.Engine.GetMeasureCheckCacheAsync();
        f.Release.Reset();
        await f.Engine.GetMeasureMarksAsync("measure:Sales/Total Sales", refresh: true);
        await f.Engine.GetMeasureMarksAsync("measure:Sales/Sales Copy 1", refresh: true);
        var timed = await f.Engine.GetMeasureMarksAsync("measure:Sales/Sales Copy 2", refresh: true, includePerformance: true);
        var performance = timed.Marks.Single(m => m.Name == "Performance");
        Assert.True(performance.Pending);
        Assert.Null(performance.Verdict);
        Assert.Contains("Waiting for 2 other checks first", performance.Progress);
        f.Release.Set();
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Sales Copy 2");
    }

    [Fact]
    public async Task Stop_ends_the_run_and_the_result_says_the_person_stopped_it()
    {
        // ra1 item 25: a run could only stop on an edit.
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(3);
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 });
        f.Release.Reset();
        var run = f.Engine.RunMeasurePlanAsync(plan.Id);
        var clock = Stopwatch.StartNew();
        while (!await f.Engine.StopMeasureRunAsync())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "the run never started");
            await Task.Delay(10);
        }
        f.Release.Set();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Stopped);
        Assert.False(result.StoppedOnEdit);
        Assert.False(await f.Engine.StopMeasureRunAsync(), "nothing runs after Stop");
        Assert.All(await f.Engine.GetMeasureCheckCacheAsync(), s => Assert.False(s.Running));
    }

    [Fact]
    public async Task Every_cell_carries_the_engines_display_text_in_the_measure_format()
    {
        // 1.7 items 10 and 11 at the join: the card prints the engine's text, so every surface shows one number one way.
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open();
        const string reference = "measure:Sales/Total Sales";
        await f.Sessions.Current.MutateAsync("human", "Contoso format", m => m.Tables["Sales"].Measures["Total Sales"].FormatString = "$#,0.00");
        await f.Engine.GetMeasureMarksAsync(reference, refresh: true);
        var filters = Mark((await Settled(f.Engine, reference, s => Mark(s, "Filters")?.State == "passed")), "Filters");
        Assert.Contains(filters.Cells, c => c.AfterText == "$300.00");
        Assert.All(filters.Cells, c => Assert.StartsWith("$", c.AfterText));
        await f.Sessions.Current.MutateAsync("human", "Contoso edit", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        var moved = Mark(await Settled(f.Engine, reference, s => Mark(s, "Equivalence")?.State == "suspect"), "Equivalence");
        Assert.Contains(moved.Cells, c => c.BeforeText == "$300.00" && c.AfterText == "$350.00");
        // The assistant reads the same text.
        var agent = (await McpTools.GetMeasureMarks(f.Engine, reference)).Marks.Single(m => m.Name == "Equivalence");
        Assert.Equal(moved.Cells.Select(c => c.AfterText), agent.Cells.Select(c => c.AfterText));
    }

    // The G1 timing tests (handed over by L3, 28 Oct): they need the check state, so they live with it.
    private sealed class PausedTiming : IMeasureTimingSource
    {
        internal bool Pause;
        internal Func<Task<bool>>? PauseWhen;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BenchmarkResult> WarmAsync(string query, int runs) => WarmAsync(query, runs, _ => { });
        public async Task<BenchmarkResult> WarmAsync(string query, int runs, Action<int> completed)
        {
            completed(runs);
            if (Pause && (PauseWhen == null || await PauseWhen()))
            {
                Started.TrySetResult();
                await Continue.Task;
            }
            return new BenchmarkResult { Runs = runs, RunsMs = Enumerable.Repeat(10L, runs).ToArray() };
        }
        public Task<ColdWarmBenchmark> ColdAsync(string query) => Task.FromResult(new ColdWarmBenchmark());
        public Task<ServerTimings> ProfileAsync(string query) => Task.FromResult(new ServerTimings());
    }

    // While a mark is pending its last result stays in Sentence; the run's own words are in Progress.
    private static string Said(MeasureMark mark) => mark.Progress ?? mark.Sentence;

    [Fact]
    public async Task Timing_alone_keeps_the_changed_selection_empty_but_invalidates_check_inputs()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open();
        const string reference = "measure:Sales/Total Sales";
        f.Engine.MeasureTimingSourceForTests = new PausedTiming();
        var initial = await f.Engine.PlanMeasureRunAsync();
        await f.Engine.RunMeasurePlanAsync(initial.Id);
        await f.Engine.SetMeasurePerformanceAsync(measureRef: reference, includePerformance: true);
        Assert.True(Assert.Single(await f.Engine.GetMeasureCheckCacheAsync()).Stale);
        await f.Engine.GetMeasureMarksAsync(reference, includePerformance: true);
        await MeasureMarksTests.Finish(f.Engine, reference);
        Assert.Empty((await f.Engine.PlanMeasureRunAsync(new() { Selection = new() { Kind = "changed" } })).Picks);
        await f.Sessions.Current.MutateAsync("human", "Edit Contoso amount", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        Assert.Single((await f.Engine.PlanMeasureRunAsync(new() { Selection = new() { Kind = "changed" } })).Picks);
    }

    [Fact]
    public async Task Timing_after_an_undone_edit_keeps_the_changed_selection_empty()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open();
        const string reference = "measure:Sales/Total Sales";
        f.Engine.MeasureTimingSourceForTests = new PausedTiming();
        var initial = await f.Engine.PlanMeasureRunAsync();
        await f.Engine.RunMeasurePlanAsync(initial.Id);
        await f.Sessions.Current.MutateAsync("human", "Edit Contoso amount", m => m.Tables["Sales"].Measures["Total Sales"].Expression += " + 50");
        await f.Engine.UndoAsync("human");
        await f.Engine.SetMeasurePerformanceAsync(measureRef: reference, includePerformance: true);
        await f.Engine.GetMeasureMarksAsync(reference, includePerformance: true);
        await MeasureMarksTests.Finish(f.Engine, reference);
        Assert.Empty((await f.Engine.PlanMeasureRunAsync(new() { Selection = new() { Kind = "changed" } })).Picks);
    }

    [Fact]
    public async Task Queued_timing_reports_the_number_of_checks_ahead()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open(3);
        var references = new[] { "measure:Sales/Total Sales", "measure:Sales/Sales Copy 1", "measure:Sales/Sales Copy 2" };
        foreach (var reference in references) await MeasureMarksTests.Finish(f.Engine, reference);
        var timer = new PausedTiming { Pause = true };
        f.Engine.MeasureTimingSourceForTests = timer;
        await f.Engine.GetMeasureMarksAsync(references[0], includePerformance: true);
        await timer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await f.Engine.GetMeasureMarksAsync(references[1], refresh: true);
            await f.Engine.GetMeasureMarksAsync(references[2], includePerformance: true);
            await Task.Delay(100);
            var mark = Assert.Single((await f.Engine.GetMeasureMarksAsync(references[2])).Marks, m => m.Name == "Performance");
            Assert.Contains("Waiting for 2 other checks first", Said(mark));
        }
        finally
        {
            timer.Continue.TrySetResult();
            foreach (var reference in references) await MeasureMarksTests.Finish(f.Engine, reference);
        }
    }

    [Fact]
    public async Task Performance_uses_one_context_count_and_singular_seconds()
    {
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open();
        const string reference = "measure:Sales/Total Sales";
        var timer = new PausedTiming { Pause = true };
        timer.PauseWhen = async () =>
        {
            var current = await f.Engine.GetMeasureMarksAsync(reference);
            // fx30e F3: the line carries no run counts; the time left is the whole line, singular near the end.
            return Said(current.Marks.Single(m => m.Name == "Performance")).Contains("About 1 second.");
        };
        f.Engine.MeasureTimingSourceForTests = timer;
        await f.Engine.GetMeasureMarksAsync(reference, includePerformance: true);
        string progress;
        try
        {
            await timer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            progress = Said(Assert.Single((await f.Engine.GetMeasureMarksAsync(reference)).Marks, m => m.Name == "Performance"));
        }
        finally { timer.Continue.TrySetResult(); }
        var performance = Assert.Single((await MeasureMarksTests.Finish(f.Engine, reference)).Marks, m => m.Name == "Performance");
        Assert.Contains("About 1 second.", progress);
        Assert.DoesNotContain("runs", progress);
        Assert.DoesNotContain("1 seconds", progress);
        Assert.Equal(performance.Timings.Length, performance.ChosenContexts.Length);
        Assert.DoesNotContain("contexts timed", performance.Sentence);   // fx30e F6: spec section 5, cut 30
    }

    [Fact]
    public async Task Timing_alone_does_not_count_a_measure_as_changed()
    {
        // rc24 release 1, gap 2: "1 changed" after only a timing run.
        using var f = new MeasureMarksDoorTests.Fixture(root); await f.Open();
        var plan = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Cap = 25 });
        await f.Engine.RunMeasurePlanAsync(plan.Id);
        await f.Engine.SetMeasurePerformanceAsync(measureRef: "measure:Sales/Total Sales", includePerformance: true);
        var changed = await f.Engine.PlanMeasureRunAsync(new MeasureRunRequest { Selection = new() { Kind = "changed" } });
        Assert.Empty(changed.Picks);
    }
}
