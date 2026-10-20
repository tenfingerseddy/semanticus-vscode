// UAT 17 Oct (speed): measuring a 70 to 128 ms query took about a minute, because every run of a cold and a warm set set up its own
// trace. A fast query is now timed on the wall clock alone (no trace, so a second or less), and a slow one is traced once.

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class BenchmarkProportionTests
    {
        private static LiveConnection Live(long elapsedMs, Action onRun = null) =>
            LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Test", "Test", q =>
            {
                onRun?.Invoke();
                return new ResultSet { Columns = new[] { new ColumnDef { Name = "[x]" } }, Rows = new[] { new object[] { 1L } }, RowCount = 1, ElapsedMs = elapsedMs };
            });

        [Fact]
        public async Task A_fast_query_is_timed_on_the_wall_clock_alone_and_never_traced()
        {
            using var live = Live(70);
            var r = await DaxBench.BenchmarkColdWarmAsync(live, "EVALUATE ROW(\"x\", 1)", 5, clearForCold: false);
            Assert.Null(r.Error);
            Assert.Equal(5, r.Detail.Length);
            Assert.DoesNotContain(r.Detail, d => d.Profiled);
            Assert.Equal(5, r.WarmTotal.N);
            Assert.Equal(70, r.WarmTotal.AvgMs);
            Assert.Contains("Fast query", r.Note);
            Assert.Equal(0, r.WarmSe.N);
        }

        [Fact]
        public async Task A_slow_query_is_traced_for_exactly_one_run_of_the_set()
        {
            using var live = Live(150);
            var r = await DaxBench.BenchmarkColdWarmAsync(live, "EVALUATE ROW(\"x\", 1)", 5, clearForCold: false);
            Assert.Null(r.Error);
            Assert.Equal(5, r.Detail.Length);
            Assert.Equal(1, r.Detail.Count(d => d.Profiled));
            Assert.False(r.Detail[0].Profiled);   // the first run says it is slow; the next is the one that is traced
            Assert.True(r.Detail[1].Profiled);
        }

        [Fact]
        public async Task A_query_of_two_seconds_or_more_needs_only_three_runs()
        {
            using var live = Live(2500);
            var r = await DaxBench.BenchmarkColdWarmAsync(live, "EVALUATE ROW(\"x\", 1)", 5, clearForCold: false);
            Assert.Null(r.Error);
            Assert.Equal(3, r.Detail.Length);
            Assert.Equal(3, r.Runs);
        }
    }
}
