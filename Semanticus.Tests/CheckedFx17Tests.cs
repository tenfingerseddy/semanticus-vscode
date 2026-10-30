using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.MeasureContexts;
using Xunit;

namespace Semanticus.Tests;

[Collection("restore-root")]
public sealed class CheckedFx17Tests
{
    [Fact]
    public async Task Warm_batch_reports_each_run_and_preserves_snapshot_cancellation()
    {
        var queries = new List<string>(); var done = new List<int>();
        using var cancellation = new CancellationTokenSource();
        using var live = LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q => {
            queries.Add(q); return new ResultSet { RowCount = 1, ElapsedMs = 10 };
        });
        using var scope = live.WithMarkQueries(_ => "Contoso snapshot");
        using var token = live.WithMeasureRunCancellation(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DaxBench.BenchmarkAsync(live, "Contoso query", 6, count => {
            done.Add(count); if (count == 3) cancellation.Cancel();
        }));
        Assert.Equal(new[] { 1, 2, 3 }, done);
        Assert.Equal(Enumerable.Repeat("Contoso snapshot", 3), queries);
    }

    [Theory]
    [InlineData("0.00%", "0.00%")]
    [InlineData("", null)]
    public async Task Equivalence_carries_each_snapshot_format_without_guessing(string afterFormat, string? expected)
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        await f.Sessions.Current.MutateAsync("human", "Format Contoso sales", m =>
            m.Tables["Sales"].Measures["Total Sales"].FormatString = "#,0");
        await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        await f.Sessions.Current.MutateAsync("human", "Contoso share", m => {
            var measure = m.Tables["Sales"].Measures["Total Sales"];
            measure.Expression = "DIVIDE(SUM('Sales'[Amount]), 300) + 50";
            measure.FormatString = afterFormat;
        });
        var result = await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        var mark = Assert.Single(result.Marks, m => m.Name == "Equivalence");
        Assert.Equal("#,0", mark.BeforeFormatString);
        Assert.Equal(expected, mark.AfterFormatString);
    }

    private sealed class PausedTimer : IMeasureTimingSource
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<BenchmarkResult> WarmAsync(string query, int runs)
        {
            Started.TrySetResult();
            await Continue.Task;
            return new BenchmarkResult { Runs = runs, RunsMs = Enumerable.Repeat(10L, runs).ToArray() };
        }
        public Task<ColdWarmBenchmark> ColdAsync(string query) => Task.FromResult(new ColdWarmBenchmark());
        public Task<ServerTimings> ProfileAsync(string query) => Task.FromResult(new ServerTimings());
    }

    [Fact]
    public async Task Timing_reports_completed_runs_and_expected_time_while_running()
    {
        using var root = new DecisionFixture();
        using var f = new MeasureMarksDoorTests.Fixture(root.Workspace);
        await f.Open();
        var timer = new PausedTimer();
        f.Engine.MeasureTimingSourceForTests = timer;
        await f.Engine.GetMeasureMarksAsync("measure:Sales/Total Sales", includePerformance: true);
        try {
            await timer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var current = await f.Engine.GetMeasureMarksAsync("measure:Sales/Total Sales");
            var performance = Assert.Single(current.Marks, m => m.Name == "Performance");
            // fx30e F3: the expected time is the whole line; no run counts reach a door (spec D4).
            Assert.StartsWith("Timing. About ", performance.Sentence);
            Assert.DoesNotContain("runs", performance.Sentence);
        }
        finally {
            timer.Continue.TrySetResult();
            await MeasureMarksTests.Finish(f.Engine, "measure:Sales/Total Sales");
        }
    }

    [Theory]
    [InlineData("Ignores Date, Product and Customer filters.", true)]
    [InlineData("Ignores Date, Product, and Customer filters.", true)]
    [InlineData("Date, Product and Customer filters are ignored.", true)]
    [InlineData("Date, Product and Customer filters are removed.", true)]
    [InlineData("Removes all of the Date, Product and Customer filters.", false)]
    public void Comma_list_states_each_cleared_name(string description, bool stated)
    {
        var columns = new[] { new ContextColumn("Date", "Year"), new ContextColumn("Product", "Category"),
            new ContextColumn("Customer", "City") };
        var probe = new ProbeResult { Status = "ok", Scenarios = columns.Select(c => new ScenarioEvidence {
            Name = "by " + c.Dax, Status = "ok", Rows = new[] {
                new ProbeRow { V = 300, Members = new() { [c.Dax] = "Contoso first" } },
                new ProbeRow { V = 300, Members = new() { [c.Dax] = "Contoso second" } }
            } }).ToArray() };
        var clears = columns.Select(c => new ContextFilterClear(c, "ALL")).ToArray();
        var mark = MeasureMarkJudge.FilterIntent(new() { Name = "Filters", State = "passed" }, probe, clears,
            false);
        // 1.7 item 2 (L2): a stated list no longer passes; only the person's "It is on purpose" does.
        Assert.Equal("info", mark.State);
        Assert.False(mark.Sentence.Contains("on purpose"), stated + ": " + mark.Sentence);
    }

    [Fact]
    public async Task Warm_runs_share_one_query_lane_admission()
    {
        var queries = new List<string>();
        Task<ResultSet> other = null;
        LiveConnection live = null;
        using (live = LiveConnection.ForTest("xmla", "contoso", "Contoso Sales", q => {
            lock (queries) queries.Add(q);
            if (q == "Contoso batch" && queries.Count == 1)
                other = live.ExecuteAsync("Contoso other", 1, 120);
            return new ResultSet { ElapsedMs = 10, RowCount = 1 };
        })) {
            var result = await DaxBench.BenchmarkAsync(live, "Contoso batch", 6);
            await other;
            Assert.Null(result.Error);
            Assert.Equal(6, result.RunsMs.Length);
            Assert.Equal(Enumerable.Repeat("Contoso batch", 6).Append("Contoso other"), queries);
        }
    }
}
