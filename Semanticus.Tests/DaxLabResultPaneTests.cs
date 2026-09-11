using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// C8.1 / D-068, D-069, D-071, D-072: the DAX Lab result pane must say when a result is capped,
    /// name the query that produced it, stop a running query, and refuse to present a 0 ms time as
    /// a measured round trip. Proven on the recorded live double (ForTest), never a real endpoint.
    /// </summary>
    public sealed class DaxLabResultPaneTests
    {
        private static ResultSet TwentyRows() => new ResultSet
        {
            Columns = new[] { new ColumnDef { Name = "n" } },
            Rows = Enumerable.Range(0, 20).Select(i => new object[] { i }).ToArray(),
            RowCount = 20,
            Truncated = false,
        };

        // D-068: a maxRows cap must mark the result truncated and cut the returned rows.
        [Fact]
        public async Task Capped_result_is_truncated_and_cut_to_maxRows()
        {
            using var live = LiveConnection.ForTest("local", "cap-test", execute: _ => TwentyRows());
            var r = await live.ExecuteAsync("EVALUATE Twenty", 5, 30);
            Assert.True(r.Truncated);
            Assert.Equal(5, r.RowCount);
            Assert.Equal(5, r.Rows.Length);
        }

        // D-068: both doors share one announcement so a capped grid is never reported as complete.
        [Fact]
        public void Capped_announcement_names_the_shown_rows_and_that_more_exist()
        {
            var text = ResultSet.RowAnnouncement(50000, truncated: true);
            Assert.Contains("50000", text, StringComparison.Ordinal);
            Assert.Contains("shown", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("More rows exist", text, StringComparison.Ordinal);
            Assert.DoesNotContain("50000 rows.", text + ".", StringComparison.Ordinal); // not a bare count
            Assert.Equal("8 rows", ResultSet.RowAnnouncement(8, truncated: false));
            Assert.Equal("1 row", ResultSet.RowAnnouncement(1, truncated: false));
        }

        // D-071: the result names the query that produced it, even if the editor later changes.
        [Fact]
        public async Task Result_names_the_query_that_produced_it()
        {
            const string query = "EVALUATE ROW(\"Revenue\", 900000000)";
            using var live = LiveConnection.ForTest("local", "query-id", execute: _ => TwentyRows());
            var r = await live.ExecuteAsync(query, 20, 30);
            Assert.Equal(query, r.Query);
        }

        // D-069: cancelling a running query stops it and returns a stopped result, not a hang.
        [Fact]
        public async Task Cancel_stops_a_running_query()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var live = LiveConnection.ForTest("local", "cancel-test", "cancel-test",
                (q, ct) =>
                {
                    entered.TrySetResult(true);
                    if (!ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("the query was not stopped");
                    throw new OperationCanceledException(ct);
                });

            using var cts = new CancellationTokenSource();
            var run = live.ExecuteAsync("EVALUATE Slow", 10, 30, cts.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            live.CancelCurrent();
            var r = await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(r.Cancelled);
            Assert.Equal(ResultSet.StoppedMessage, r.Error);
        }

        // D-069: the same stop is reachable as an engine call (UI Stop and the agent door both use it).
        [Fact]
        public async Task CancelDax_stops_the_in_flight_query_on_the_engine()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var engine = new LocalEngine(new SessionManager());
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("local", "cancel-engine", "cancel-engine",
                (q, ct) =>
                {
                    entered.TrySetResult(true);
                    if (!ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("the query was not stopped");
                    throw new OperationCanceledException(ct);
                }));

            var run = engine.RunDaxAsync("EVALUATE Slow", 10, "human");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var stopped = await engine.CancelDaxAsync();
            Assert.True(stopped.Stopped);
            var r = await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(r.Cancelled);
            Assert.Equal(ResultSet.StoppedMessage, r.Error);
        }

        // D-072: a 0 ms figure is not presented as a measured round trip.
        [Fact]
        public void Zero_ms_timing_is_labelled_not_a_measurement()
        {
            Assert.Equal("under 1 ms", DaxBench.FormatTimingMs(0));
            Assert.Equal("under 1 ms", DaxBench.FormatTimingMs(0.4));
            Assert.Equal("3.2 ms", DaxBench.FormatTimingMs(3.2));
            var note = DaxBench.TimingCredibilityNote(new[] { 0L, 3L, 16L }, seAllZero: true, traceAvailable: true);
            Assert.Contains("under 1 ms", note, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not a round trip", note, StringComparison.OrdinalIgnoreCase);
        }
    }
}
