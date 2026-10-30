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
    [Collection("restore-root")]
    public sealed class SolS1ReviewTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "s1-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private readonly string? _oldHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
        private readonly ITestOutputHelper _output;

        public SolS1ReviewTests(ITestOutputHelper output)
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
            await engine.CreateModelAsync("S1 review", 1604);
            await engine.CreateTableAsync("Sales", "human");
            foreach (var name in new[] { "Amount", "Profit", "Revenue", "Cost" })
                await engine.CreateColumnAsync("table:Sales", name, "Double", name, "human");
            await engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM(Sales[Amount])", "human");
            await engine.CreateTableAsync("Date", "human");
            await engine.CreateColumnAsync("table:Date", "Date", "DateTime", "Date", "human");
            await engine.CreateColumnAsync("table:Date", "Year", "Int64", "Year", "human");
            return (sessions, engine);
        }

        [Fact]
        public async Task S1_A_unit_conversion_is_not_an_average_of_percentages_and_does_not_dock_the_grade()
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                var reference = await engine.CreateMeasureAsync("table:Sales", "Average sale in thousands", "DIVIDE(AVERAGE(Sales[Amount]), 1000)", "human");
                await engine.SetDaxAsync(reference, "AVERAGEX(Sales, DIVIDE(Sales[Amount], 1000))", "human");
                var clean = new ReadinessAnalyzer(ReadinessRuleSet.Default().Where(r => r.Id != "AVG-OF-RATIO").ToArray()).Analyze(sessions.Current.Model);
                var card = new ReadinessAnalyzer().Analyze(sessions.Current.Model);
                var evaluation = ReadinessRuleSet.Default().Single(r => r.Id == "AVG-OF-RATIO").Evaluate(sessions.Current.Model);
                // Both expressions return 2 for sales of 1000 and 3000. No row percentage exists.
                Assert.Equal(2.0, new[] { 1000.0, 3000.0 }.Average() / 1000);
                _output.WriteLine($"Equivalent unit conversion: raw grade {clean.RawOverall} -> {card.RawOverall}; findings {evaluation.Violations.Count}.");
                Assert.Empty(evaluation.Violations);
                Assert.Equal(clean.RawOverall, card.RawOverall);
            }
        }

        [Fact]
        public async Task S1_The_ratio_fix_does_not_move_a_term_across_the_division_operator()
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                var reference = await engine.CreateMeasureAsync("table:Sales", "Adjusted ratio", "AVERAGEX(Sales, Sales[Profit] / Sales[Revenue] + Sales[Cost])", "human");
                var measure = (TabularEditor.TOMWrapper.Measure)ObjectRefs.Resolve(sessions.Current.Model, reference);
                var candidate = WrongNumberReadiness.RatioOfTotals(measure);
                _output.WriteLine("Candidate: " + candidate);
                // A plus outside division is not part of its denominator. This shape needs an authored candidate.
                Assert.Null(candidate);
            }
        }

        [Fact]
        public async Task S1_A_balance_filtered_to_one_date_is_not_docked_as_a_sum_across_dates()
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateColumnAsync("table:Sales", "Balance", "Double", "Balance", "human");
                await engine.CreateColumnAsync("table:Sales", "OrderDate", "DateTime", "OrderDate", "human");
                await engine.CreateRelationshipAsync("column:Sales/OrderDate", "column:Date/Date", null, true, "human");
                var reference = await engine.CreateMeasureAsync("table:Sales", "Closing balance",
                    "CALCULATE(SUM(Sales[Balance]), LASTDATE('Date'[Date]))", "human");
                await engine.SetDaxAsync(reference,
                    "CALCULATE(SUM(Sales[Balance]), 'Date'[Date] = MAX('Date'[Date]))", "human");
                var clean = new ReadinessAnalyzer(ReadinessRuleSet.Default().Where(r => r.Id != "SEMI-ADDITIVE-SUM").ToArray()).Analyze(sessions.Current.Model);
                var card = new ReadinessAnalyzer().Analyze(sessions.Current.Model);
                var evaluation = ReadinessRuleSet.Default().Single(r => r.Id == "SEMI-ADDITIVE-SUM").Evaluate(sessions.Current.Model);
                _output.WriteLine($"Single-date closing balance: raw grade {clean.RawOverall} -> {card.RawOverall}; findings {evaluation.Violations.Count}.");
                Assert.Empty(evaluation.Violations);
                Assert.Equal(clean.RawOverall, card.RawOverall);
            }
        }

        [Fact]
        public async Task S1_Apply_standard_keeps_a_literal_percent_suffix_instead_of_scaling_the_value_by_100()
        {
            var (_, engine) = await ModelAsync();
            using (engine)
            {
                var reference = await engine.CreateMeasureAsync("table:Sales", "Percent points", "50", "human");
                const string format = "0.0\"%\"";
                await engine.SetMeasureFormatAsync(reference, format, "human");
                await engine.SetStandardAsync("{\"formats\":{\"percent\":\"0.0%\"}}", "your-default", "human");
                var result = await engine.ApplyStandardAsync("human");
                var actual = (await engine.GetObjectPropertiesAsync(reference)).Single(p => p.Name == "FormatString").Value;
                _output.WriteLine("Applied: " + string.Join("; ", result.Applied));
                _output.WriteLine("50 with a literal percent suffix displays 50.0%; a percent operator displays 5000.0%.");
                Assert.Equal(format, actual);
            }
        }

        [Fact]
        public void S1_Calendar_migration_keeps_a_mid_month_fiscal_boundary()
        {
            const string before = "TOTALYTD([Total Sales], 'Date'[Date], \"6/15\")";
            var moved = CalendarMeasureMove.Rewrite(before, "Date", "Date", "Calendar year", "Fiscal year", 7);
            // The old year begins June 16; the generated July calendar begins July 1.
            // A June 20 sale belongs to different fiscal years. There is no day field in the target template.
            _output.WriteLine("Original: " + before + "; replacement: " + moved.After);
            Assert.Null(moved.After);
        }

        [Fact]
        public async Task S1_Calendar_setup_preserves_the_prior_year_selection_for_leap_day()
        {
            var (_, engine) = await ModelAsync();
            using (engine)
            {
                const string expression = "CALCULATE([Total Sales], SAMEPERIODLASTYEAR('Date'[Date]))";
                var reference = await engine.CreateMeasureAsync("table:Sales", "Sales Last Year", expression, "human");
                var setup = await engine.SetupCalendarsAsync("Date", new[] { "year" }, 1, null, true, "human");
                var actual = (await engine.GetModelObjectsAsync()).Measures.Single(m => m.Name == "Sales Last Year").Expression;
                // Microsoft's DAX contract: Feb 29 2008 -> Feb 28 2007 for column input,
                // but -> Mar 1 2007 for calendar input. They are different sales cells.
                // https://learn.microsoft.com/en-us/dax/sameperiodlastyear-function-dax#differences-in-behavior-between-classic-and-calendar-time-intelligence
                _output.WriteLine("Before: " + expression + "; after: " + actual + "; number check: " + setup.NumberCheck.Status);
                Assert.DoesNotContain("SAMEPERIODLASTYEAR('Calendaryear')", actual.Replace(" ", ""));
            }
        }

        [Fact]
        public async Task S1_Calendar_coverage_does_not_pass_a_fact_date_in_an_interior_calendar_gap()
        {
            var (_, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateColumnAsync("table:Sales", "OrderDate", "DateTime", "OrderDate", "human");
                await engine.CreateRelationshipAsync("column:Sales/OrderDate", "column:Date/Date", null, true, "human");
                await engine.MarkDateTableAsync("table:Date", "Date", "human");
                var calendar = new[] { new DateTime(2025, 1, 1), new DateTime(2025, 1, 3) };
                var facts = new[] { new DateTime(2025, 1, 2) };
                Assert.Single(facts.Except(calendar));
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "x", "db", q =>
                    new ResultSet
                    {
                        Columns = new[] { "calMin", "calMax", "rows", "days", "f0min", "f0max" }.Select(n => new ColumnDef { Name = n }).ToArray(),
                        Rows = new[] { new object[] { calendar.Min(), calendar.Max(), 2L, 2L, facts.Min(), facts.Max() } }, RowCount = 1,
                    }));
                var result = await engine.CheckCalendarDataAsync("table:Date");
                _output.WriteLine("Coverage result: " + result.Status + "; " + result.Sentence);
                Assert.NotEqual("ok", result.Status);
            }
        }

        [Fact]
        public async Task S1_Standard_apply_and_one_undo_restore_model_bytes_and_existing_plan_rows()
        {
            var (_, engine) = await ModelAsync();
            using (engine)
            {
                var path = Path.Combine(_root, "model.bim");
                await engine.SaveAsync(path, "BIM", false, "human");
                await engine.AddPlanTaskAsync(new PlanTaskRequest { Words = "Keep my existing task", Brief = "A note written before Apply.", SourcePage = "readiness" });
                await engine.SetStandardAsync("{\"preset\":\"strict\",\"names\":\"title-case\"}", null, "human");
                string Rows() => System.Text.Json.JsonSerializer.Serialize(engine.ReadPlanState(engine.BindPlanMemory()).Tasks);
                var rowsBefore = Rows();
                var bytesBefore = File.ReadAllBytes(path);
                await engine.ApplyStandardAsync("human");
                await engine.UndoAsync("human");
                await engine.SaveAsync(path, "BIM", false, "human");
                Assert.Equal(bytesBefore, File.ReadAllBytes(path));
                Assert.Equal(rowsBefore, Rows());
            }
        }
    }

    public sealed partial class AskDataTests
    {
        [Fact]
        public async Task S1_Explain_rechecks_a_new_data_denial_before_returning_a_cached_dossier()
        {
            using var rig = await BuildAsync();
            AgentPolicyStore.SetPreset("open", "human");
            var model = SplitModel(SplitParts(), SplitCounts(), rig.Data.Execute);
            rig.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail", model));
            var agentDoor = AssistantEngine.For(rig.Engine);
            var first = await McpTools.ExplainValue(agentDoor, "Net Sales", Year2025());
            Assert.True(first.ValueEvaluated);
            Assert.NotEmpty(first.Contributors.Rows);
            await rig.Engine.SetAgentPolicyPresetAsync("client", "human");
            var afterDenial = await McpTools.ExplainValue(agentDoor, "Net Sales", Year2025());
            Assert.False(afterDenial.ValueEvaluated);
            Assert.False(afterDenial.Evidence.Available);
            Assert.Null(afterDenial.Value);
            Assert.True(afterDenial.Contributors == null || afterDenial.Contributors.Rows.Length == 0);
        }
    }
}
