using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    // Verifier v-fix-s1 round 2: one more variant per finding on fix2-s1.
    [Collection("restore-root")]
    public sealed class VerifierS1Round2Tests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "vr2-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private readonly string? _oldHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
        private readonly ITestOutputHelper _output;

        public VerifierS1Round2Tests(ITestOutputHelper output)
        {
            _output = output;
            Directory.CreateDirectory(_root);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _oldHome);
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private static async Task<(SessionManager sessions, LocalEngine engine)> ModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("S1 round 2", 1604);
            await engine.CreateTableAsync("Sales", "human");
            foreach (var name in new[] { "Amount", "Profit", "Revenue", "Balance" })
                await engine.CreateColumnAsync("table:Sales", name, "Double", name, "human");
            await engine.CreateColumnAsync("table:Sales", "OrderDate", "DateTime", "OrderDate", "human");
            await engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM(Sales[Amount])", "human");
            await engine.CreateTableAsync("Date", "human");
            await engine.CreateColumnAsync("table:Date", "Date", "DateTime", "Date", "human");
            await engine.CreateColumnAsync("table:Date", "Year", "Int64", "Year", "human");
            await engine.CreateRelationshipAsync("column:Sales/OrderDate", "column:Date/Date", null, true, "human");
            await engine.MarkDateTableAsync("table:Date", "Date", "human");
            return (sessions, engine);
        }

        private static async Task<string> ExpressionOf(LocalEngine engine, string name) =>
            (await engine.GetModelObjectsAsync()).Measures.Single(m => m.Name == name).Expression;

        // Finding 1 variant: no live model means no proof, so a period-to-date measure stays, with the plain reason.
        [Fact]
        public async Task R2_Calendar_setup_without_a_live_model_moves_nothing_and_says_why()
        {
            var (_, engine) = await ModelAsync();
            using (engine)
            {
                const string ytd = "TOTALQTD ( [Total Sales], 'Date'[Date] )";
                await engine.CreateMeasureAsync("table:Sales", "Sales QTD", ytd, "human");
                var r = await engine.SetupCalendarsAsync("Date", new[] { "year" }, 1, null, true, "human");
                _output.WriteLine("Moved " + r.Moved.Length + "; stays: " + string.Join(" | ", r.Stays.Select(s => s.Measure + ": " + s.Why)) + "; check " + r.NumberCheck.Status);
                Assert.Empty(r.Moved);
                Assert.Equal(ytd, await ExpressionOf(engine, "Sales QTD"));
                Assert.Contains(r.Stays, s => s.Measure == "Sales QTD" && s.Why == LocalEngine.NoModelToCompareReason);
                Assert.NotEqual("same", r.NumberCheck.Status);
            }
        }

        // Finding 1 variant: matching numbers whose comparison saw only two of the three days around 29 February stay.
        [Theory]
        [InlineData("366|0|0||2", false)]
        [InlineData("366|0|0||3", true)]
        public async Task R2_Calendar_setup_needs_all_three_days_around_the_leap_day(string answer, bool moves)
        {
            var (_, engine) = await ModelAsync();
            using (engine)
            {
                const string ytd = "TOTALMTD ( [Total Sales], 'Date'[Date] )";
                await engine.CreateMeasureAsync("table:Sales", "Sales MTD", ytd, "human");
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "x", "db", q => new ResultSet
                {
                    Columns = new[] { new ColumnDef { Name = "r" } }, Rows = new[] { new object[] { answer } }, RowCount = 1,
                }));
                var r = await engine.SetupCalendarsAsync("Date", new[] { "year" }, 1, null, true, "human");
                var after = await ExpressionOf(engine, "Sales MTD");
                _output.WriteLine(answer + ": " + after + "; check " + r.NumberCheck.Status);
                Assert.Equal(moves, after != ytd);
                if (moves) Assert.Equal("TOTALMTD ( [Total Sales], 'Calendar year' )", after);
            }
        }

        private static (double clean, double card, int findings) Score(SessionManager sessions, string ruleId)
        {
            var clean = new ReadinessAnalyzer(ReadinessRuleSet.Default().Where(r => r.Id != ruleId).ToArray()).Analyze(sessions.Current.Model);
            var card = new ReadinessAnalyzer().Analyze(sessions.Current.Model);
            var evaluation = ReadinessRuleSet.Default().Single(r => r.Id == ruleId).Evaluate(sessions.Current.Model);
            return (clean.RawOverall, card.RawOverall, evaluation.Violations.Count);
        }

        // Finding 2 variant: 'Date'[Date] = [Last Date] in a CALCULATE filter is FILTER(ALL('Date'[Date]), 'Date'[Date] = [Last Date]).
        // The measure is evaluated per date with context transition, so it equals each row's own date: every date is kept and
        // the balance is summed across all of them. Held in a VAR, the measure is evaluated once and keeps one date.
        [Fact]
        public async Task R2_A_direct_measure_reference_in_the_date_filter_still_warns()
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "Last Date", "MAX('Date'[Date])", "human");
                await engine.CreateMeasureAsync("table:Sales", "Closing balance", "CALCULATE(SUM(Sales[Balance]), 'Date'[Date] = [Last Date])", "human");
                var s = Score(sessions, "SEMI-ADDITIVE-SUM");
                _output.WriteLine($"direct measure: {s.clean} -> {s.card}; findings {s.findings}");
                Assert.Equal(1, s.findings);
            }
        }

        [Fact]
        public async Task R2_A_measure_held_in_a_variable_keeps_one_date_and_is_quiet()
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "Last Date", "MAX('Date'[Date])", "human");
                await engine.CreateMeasureAsync("table:Sales", "Closing balance", "VAR d = [Last Date] RETURN CALCULATE(SUM(Sales[Balance]), 'Date'[Date] = d)", "human");
                var s = Score(sessions, "SEMI-ADDITIVE-SUM");
                _output.WriteLine($"var measure: {s.clean} -> {s.card}; findings {s.findings}");
                Assert.Equal(0, s.findings);
                Assert.Equal(s.clean, s.card);
            }
        }

        [Fact]
        public async Task R2_A_constant_through_a_wrapper_is_quiet_and_a_wrapped_column_still_warns()
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "In thousands", "VAR k = POWER(10, 3) RETURN AVERAGEX(Sales, Sales[Amount] / ROUND(k, 0))", "human");
                var quiet = Score(sessions, "AVG-OF-RATIO");
                _output.WriteLine($"constant wrapper: {quiet.clean} -> {quiet.card}; findings {quiet.findings}");
                Assert.Equal(0, quiet.findings);
                await engine.CreateMeasureAsync("table:Sales", "Average margin", "AVERAGEX(Sales, Sales[Profit] / ABS(Sales[Revenue]))", "human");
                var real = Score(sessions, "AVG-OF-RATIO");
                _output.WriteLine($"wrapped column: {real.clean} -> {real.card}; findings {real.findings}");
                Assert.Equal(1, real.findings);
                Assert.True(real.card < real.clean);
            }
        }
    }
}
