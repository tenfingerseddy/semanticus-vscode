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
    // Verifier v-fix-s1: one more variant per Sol S1 finding, plus the happy path each fix must keep.
    [Collection("restore-root")]
    public sealed class VerifierS1VariantTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "vs1-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private readonly string? _oldHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
        private readonly ITestOutputHelper _output;

        public VerifierS1VariantTests(ITestOutputHelper output)
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
            await engine.CreateModelAsync("S1 variants", 1604);
            await engine.CreateTableAsync("Sales", "human");
            foreach (var name in new[] { "Amount", "Profit", "Revenue", "Cost", "Balance" })
                await engine.CreateColumnAsync("table:Sales", name, "Double", name, "human");
            await engine.CreateColumnAsync("table:Sales", "OrderDate", "DateTime", "OrderDate", "human");
            await engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM(Sales[Amount])", "human");
            await engine.CreateTableAsync("Date", "human");
            await engine.CreateColumnAsync("table:Date", "Date", "DateTime", "Date", "human");
            await engine.CreateColumnAsync("table:Date", "Year", "Int64", "Year", "human");
            await engine.CreateRelationshipAsync("column:Sales/OrderDate", "column:Date/Date", null, true, "human");
            return (sessions, engine);
        }

        // F4 variant: Microsoft's DATEADD page carries the same paragraph as SAMEPERIODLASTYEAR's: on a calendar,
        // Feb 29 2008 shifted back one year is Mar 1 2007; on a date column it is Feb 28 2007.
        // https://learn.microsoft.com/en-us/dax/dateadd-function-dax#differences-in-behavior-between-classic-and-calendar-time-intelligence
        [Theory]
        [InlineData("CALCULATE ( [Total Sales], DATEADD ( 'Date'[Date], -1, YEAR ) )")]
        [InlineData("CALCULATE ( [Total Sales], DATEADD ( 'Date'[Date], -4, QUARTER ) )")]
        public void V4_A_year_shift_by_dateadd_stays_like_sameperiodlastyear(string dax)
        {
            var o = CalendarMeasureMove.Rewrite(dax, "Date", "Date", "Calendar year", null, 7);
            _output.WriteLine("Before: " + dax + "; after: " + o.After + "; why: " + o.Why);
            Assert.Null(o.After);
        }

        [Fact]
        public void V4_Happy_path_a_year_to_date_still_moves()
        {
            var o = CalendarMeasureMove.Rewrite("TOTALYTD ( [Total Sales], 'Date'[Date] )", "Date", "Date", "Calendar year", null, 7);
            Assert.Equal("TOTALYTD ( [Total Sales], 'Calendar year' )", o.After);
        }

        [Theory]
        [InlineData("PARALLELPERIOD('Date'[Date], -1, YEAR)")]
        [InlineData("PREVIOUSYEAR('Date'[Date])")]
        [InlineData("NEXTYEAR('Date'[Date])")]
        [InlineData("DATEADD('Date'[Date], -12, MONTH)")]
        [InlineData("DATESINPERIOD('Date'[Date], MAX('Date'[Date]), -1, YEAR)")]
        [InlineData("DATESINPERIOD('Date'[Date], MAX('Date'[Date]), -4, QUARTER)")]
        [InlineData("TOTALYTD([Total Sales], 'Date'[Date]) + COUNTROWS(DATEADD('Date'[Date], -1, YEAR))")]
        public void Fix2_Unproved_time_shifts_keep_the_old_expression(string dax)
        {
            var result = CalendarMeasureMove.Rewrite(dax, "Date", "Date", "Calendar year", null, 7);
            Assert.Null(result.After);
            Assert.Contains("29 February", result.Why);
        }

        private static ResultSet NumberAnswer(string answer) => new ResultSet
        {
            Columns = new[] { new ColumnDef { Name = "r" } },
            Rows = new[] { new object[] { answer } }, RowCount = 1,
        };

        [Theory]
        [InlineData(null, false)]
        [InlineData("36|0|0|", false)]
        [InlineData("366|0|0||0", false)]
        [InlineData("366|0|1|2024-02-29: 100 vs 90|3", false)]
        [InlineData("366|0|0||3", true)]
        public async Task Fix2_Setup_moves_only_after_day_numbers_match_with_a_leap_day(string answer, bool moves)
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                const string before = "TOTALYTD([Total Sales], 'Date'[Date])";
                await engine.CreateMeasureAsync("table:Sales", "YTD", before, "human");
                var asked = new System.Collections.Generic.List<string>();
                if (answer != null)
                    engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "x", "db", q =>
                    {
                        // The proof must precede the write, including when the query succeeds.
                        asked.Add(q);
                        Assert.Equal(before, sessions.Current.Model.AllMeasures.Single(m => m.Name == "YTD").Expression);
                        return NumberAnswer(answer);
                    }));
                var setup = await engine.SetupCalendarsAsync("Date", new[] { "year" }, 1, null, true, "human");
                var actual = (await engine.GetModelObjectsAsync()).Measures.Single(m => m.Name == "YTD").Expression;
                Assert.Equal(moves ? "TOTALYTD([Total Sales], 'Calendar year')" : before, actual);
                Assert.Equal(moves ? 1 : 0, setup.Moved.Length);
                if (moves)
                {
                    Assert.Equal("same", setup.NumberCheck.Status);
                    var query = Assert.Single(asked);
                    Assert.Contains("SUMMARIZECOLUMNS ( 'Date'[Date]", query);
                    Assert.Contains("29", query);
                    Assert.Contains("'Sales'[OrderDate]", query);
                    Assert.True(DaxQueryClassifier.IsScalar(query));
                }
                else
                {
                    Assert.Contains(setup.Stays, x => x.Measure == "YTD" && x.Why.Contains(answer == null ? "compares its numbers" : "29 February"));   // no live model says so; a wrong answer names the leap day
                    Assert.NotEqual("same", setup.NumberCheck.Status);
                }
                await engine.UndoAsync("human");
                Assert.Equal(before, (await engine.GetModelObjectsAsync()).Measures.Single(m => m.Name == "YTD").Expression);
                Assert.Empty((await engine.ListCalendarsAsync(null)).Calendars);
            }
        }

        [Theory]
        [InlineData("VAR k = 1000 VAR scale = k * 2 RETURN AVERAGEX(Sales, DIVIDE(Sales[Amount], scale))")]
        [InlineData("AVERAGEX(Sales, VAR k = 1000 RETURN Sales[Amount] / k)")]
        [InlineData("VAR k = 1000 RETURN AVERAGEX(Sales, Sales[Amount] / (k + 1))")]
        public async Task Fix2_Constant_variable_expressions_do_not_lower_the_grade(string dax)
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "Converted average", dax, "human");
                var score = Score(sessions, "AVG-OF-RATIO");
                Assert.Equal(0, score.findings);
                Assert.Equal(score.clean, score.card);
            }
        }

        [Theory]
        [InlineData("VAR k = SUM(Sales[Revenue]) RETURN AVERAGEX(Sales, Sales[Profit] / k)")]
        [InlineData("AVERAGEX(Sales, VAR k = Sales[Revenue] RETURN DIVIDE(Sales[Profit], k))")]
        [InlineData("IF(TRUE(), (VAR k = 1000 RETURN AVERAGEX(Sales, Sales[Amount] / k)), (VAR k = Sales[Revenue] RETURN AVERAGEX(Sales, Sales[Profit] / k)))")]
        public async Task Fix2_Data_dependent_variables_are_still_ratios(string dax)
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "Average ratio", dax, "human");
                Assert.Equal(1, Score(sessions, "AVG-OF-RATIO").findings);
            }
        }

        [Theory]
        [InlineData("VAR d = MAX('Date'[Date]) VAR last = d RETURN CALCULATE(SUM(Sales[Balance]), KEEPFILTERS('Date'[Date] = last))")]
        [InlineData("VAR days = FILTER(ALL('Date'[Date]), MAX('Date'[Date]) = 'Date'[Date]) RETURN CALCULATE(SUM(Sales[Balance]), days)")]
        [InlineData("CALCULATE(SUM(Sales[Balance]), 'Date'[Date] = MIN('Date'[Date]))")]
        [InlineData("CALCULATE(SUM(Sales[Balance]), FILTER(ALL('Date'), 'Date'[Date] = MAX('Date'[Date]) && 'Date'[Year] > 2000))")]
        public async Task Fix2_One_date_filter_expressions_do_not_lower_the_grade(string dax)
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "Closing balance", dax, "human");
                var score = Score(sessions, "SEMI-ADDITIVE-SUM");
                Assert.Equal(0, score.findings);
                Assert.Equal(score.clean, score.card);
            }
        }

        [Theory]
        [InlineData("CALCULATE(SUM(Sales[Balance]), FILTER(ALL('Date'[Date]), 'Date'[Date] = MAX('Date'[Date]) || 'Date'[Date] = MIN('Date'[Date])))")]
        [InlineData("CALCULATE(SUM(Sales[Balance]), YEAR('Date'[Date]) = MAX('Date'[Year]))")]
        [InlineData("VAR d = MAX('Date'[Date]) RETURN CALCULATE(SUM(Sales[Balance]), FILTER(ALL('Date'[Date]), 'Date'[Date] <= d))")]
        public async Task Fix2_Multiple_date_filters_still_warn(string dax)
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "Closing balance", dax, "human");
                Assert.Equal(1, Score(sessions, "SEMI-ADDITIVE-SUM").findings);
            }
        }

        [Theory]
        [InlineData("AVERAGEX(Sales, VAR k = 1000 VAR converted = Sales[Amount] / k RETURN converted)", "AVG-OF-RATIO")]
        [InlineData("AVERAGEX(Sales, Sales[Amount] / -1000)", "AVG-OF-RATIO")]
        [InlineData("CALCULATE(SUM(Sales[Balance]), 'Date'[Date] = EDATE(MAX('Date'[Date]), -1))", "SEMI-ADDITIVE-SUM")]
        public async Task Fix2_Equivalent_scalar_expressions_stay_quiet(string dax, string rule)
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "Result", dax, "human");
                var score = Score(sessions, rule);
                Assert.Equal(0, score.findings);
                Assert.Equal(score.clean, score.card);
            }
        }

        [Fact]
        public async Task Fix2_A_scalar_expression_that_varies_by_date_still_warns()
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "Closing balance",
                    "CALCULATE(SUM(Sales[Balance]), FILTER(ALL('Date'), 'Date'[Date] = DATE(YEAR('Date'[Date]), 12, 31)))", "human");
                Assert.Equal(1, Score(sessions, "SEMI-ADDITIVE-SUM").findings);
            }
        }

        [Theory]
        [InlineData("15 June", null)]
        [InlineData("June 15", null)]
        [InlineData("06-15", null)]
        [InlineData("15/6", null)]
        [InlineData("3/30", null)]
        [InlineData("30 June", 7)]
        [InlineData("June 30", 7)]
        [InlineData("6/30", 7)]
        [InlineData("31/3", 4)]
        [InlineData("June", 7)]
        public void V3_Year_end_reads_only_a_month_end(string text, int? start)
            => Assert.Equal(start, CalendarMeasureMove.StartMonthFromYearEnd(text));

        [Fact]
        public void V3_Mid_month_year_end_in_dates_ytd_stays()
        {
            var o = CalendarMeasureMove.Rewrite("CALCULATE ( [Total Sales], DATESYTD ( 'Date'[Date], \"15 June\" ) )", "Date", "Date", "Calendar year", "Fiscal year", 7);
            Assert.Null(o.After);
        }

        private static ResultSet Range(DateTime calMin, DateTime calMax, long rows, long days, DateTime fMin, DateTime fMax) => new ResultSet
        {
            Columns = new[] { "calMin", "calMax", "rows", "days", "f0min", "f0max" }.Select(n => new ColumnDef { Name = n }).ToArray(),
            Rows = new[] { new object[] { calMin, calMax, rows, days, fMin, fMax } }, RowCount = 1,
        };

        // F5 variant: a five-day calendar missing one day near its end, facts spanning the whole range.
        [Fact]
        public async Task V5_A_gap_near_the_end_of_the_calendar_is_a_problem_and_a_whole_calendar_is_not()
        {
            var (_, engine) = await ModelAsync();
            using (engine)
            {
                await engine.MarkDateTableAsync("table:Date", "Date", "human");
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "x", "db", q =>
                    Range(new DateTime(2025, 1, 1), new DateTime(2025, 1, 5), 4L, 4L, new DateTime(2025, 1, 1), new DateTime(2025, 1, 5))));
                var gap = await engine.CheckCalendarDataAsync("table:Date");
                _output.WriteLine("Gap: " + gap.Status + "; " + string.Join(" | ", gap.Findings.Select(f => f.Check + ": " + f.Sentence)));
                Assert.NotEqual("ok", gap.Status);
                Assert.Contains(gap.Findings, f => f.Check == "gap-in-calendar" && f.Level == "problem");

                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "x", "db", q =>
                    Range(new DateTime(2025, 1, 1), new DateTime(2025, 1, 5), 5L, 5L, new DateTime(2025, 1, 1), new DateTime(2025, 1, 5))));
                var whole = await engine.CheckCalendarDataAsync("table:Date");
                _output.WriteLine("Whole: " + whole.Status + "; " + string.Join(" | ", whole.Findings.Select(f => f.Check + ": " + f.Sentence)));
                Assert.DoesNotContain(whole.Findings, f => f.Check == "gap-in-calendar");
            }
        }

        private static async Task<string> Candidate(SessionManager sessions, LocalEngine engine, string name, string dax)
        {
            var reference = await engine.CreateMeasureAsync("table:Sales", name, dax, "human");
            var measure = (TabularEditor.TOMWrapper.Measure)ObjectRefs.Resolve(sessions.Current.Model, reference);
            return WrongNumberReadiness.RatioOfTotals(measure);
        }

        // F6 variant: the subtracted term on the other side of the division, and a leading minus term.
        [Fact]
        public async Task V6_Ratio_offer_never_moves_a_term_across_the_division_and_keeps_its_happy_paths()
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                Assert.Null(await Candidate(sessions, engine, "R1", "AVERAGEX(Sales, Sales[Profit] - Sales[Cost] / Sales[Revenue])"));
                Assert.Null(await Candidate(sessions, engine, "R2", "AVERAGEX(Sales, Sales[Profit] / Sales[Revenue] - Sales[Cost])"));
                Assert.Null(await Candidate(sessions, engine, "R3", "AVERAGEX(Sales, DIVIDE(Sales[Profit], Sales[Revenue]) + Sales[Cost])"));
                Assert.Equal("DIVIDE(SUM('Sales'[Profit]), SUM('Sales'[Revenue]))", await Candidate(sessions, engine, "R4", "AVERAGEX(Sales, Sales[Profit] / Sales[Revenue])"));
                var sum = await Candidate(sessions, engine, "R5", "AVERAGEX(Sales, DIVIDE(Sales[Revenue] - Sales[Cost], Sales[Revenue]))");
                _output.WriteLine("R5: " + sum);
                Assert.Equal("DIVIDE(SUM('Sales'[Revenue]) - SUM('Sales'[Cost]), SUM('Sales'[Revenue]))", sum);
            }
        }

        // F2 variant: a backslash-escaped percent sign is a literal too.
        [Fact]
        public async Task V2_Escaped_percent_is_left_alone_and_a_real_percent_format_still_gets_the_standard()
        {
            var (_, engine) = await ModelAsync();
            using (engine)
            {
                var escaped = await engine.CreateMeasureAsync("table:Sales", "Points", "50", "human");
                const string literal = "0.0\\%";
                await engine.SetMeasureFormatAsync(escaped, literal, "human");
                var real = await engine.CreateMeasureAsync("table:Sales", "Margin", "DIVIDE([Total Sales], 2)", "human");
                await engine.SetMeasureFormatAsync(real, "0.00%", "human");
                await engine.SetStandardAsync("{\"formats\":{\"percent\":\"0.0%\"}}", "your-default", "human");
                var result = await engine.ApplyStandardAsync("human");
                _output.WriteLine("Applied: " + string.Join("; ", result.Applied));
                Assert.Equal(literal, (await engine.GetObjectPropertiesAsync(escaped)).Single(p => p.Name == "FormatString").Value);
                Assert.Equal("0.0%", (await engine.GetObjectPropertiesAsync(real)).Single(p => p.Name == "FormatString").Value);
            }
        }

        private static (double clean, double card, int findings) Score(SessionManager sessions, string ruleId)
        {
            var clean = new ReadinessAnalyzer(ReadinessRuleSet.Default().Where(r => r.Id != ruleId).ToArray()).Analyze(sessions.Current.Model);
            var card = new ReadinessAnalyzer().Analyze(sessions.Current.Model);
            var evaluation = ReadinessRuleSet.Default().Single(r => r.Id == ruleId).Evaluate(sessions.Current.Model);
            return (clean.RawOverall, card.RawOverall, evaluation.Violations.Count);
        }

        // F7 variants: a "/" constant, and the real average of a ratio that must still fire.
        [Fact]
        public async Task V7_Dividing_by_a_constant_is_quiet()
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "Average sale in thousands", "AVERAGEX(Sales, Sales[Amount] / 1000)", "human");
                var s = Score(sessions, "AVG-OF-RATIO");
                _output.WriteLine($"/1000: {s.clean} -> {s.card}; findings {s.findings}");
                Assert.Equal(0, s.findings);
                Assert.Equal(s.clean, s.card);
            }
        }

        [Fact]
        public async Task V7_Dividing_by_a_constant_held_in_a_variable_is_quiet()
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "Average sale in thousands", "VAR k = 1000 RETURN AVERAGEX(Sales, Sales[Amount] / k)", "human");
                var s = Score(sessions, "AVG-OF-RATIO");
                _output.WriteLine($"VAR k: {s.clean} -> {s.card}; findings {s.findings}");
                Assert.Equal(0, s.findings);
            }
        }

        [Fact]
        public async Task V7_A_real_average_of_ratios_still_fires()
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "Average margin", "AVERAGEX(Sales, DIVIDE(Sales[Profit], Sales[Revenue]))", "human");
                await engine.CreateMeasureAsync("table:Sales", "Average margin 2", "AVERAGEX(Sales, Sales[Profit] / COUNTROWS(Sales))", "human");
                var s = Score(sessions, "AVG-OF-RATIO");
                _output.WriteLine($"real ratio: {s.clean} -> {s.card}; findings {s.findings}");
                Assert.Equal(2, s.findings);
                Assert.True(s.card < s.clean);
            }
        }

        // F8 variants.
        [Theory]
        [InlineData("CALCULATE(SUM(Sales[Balance]), LASTNONBLANK('Date'[Date], CALCULATE(SUM(Sales[Balance]))))")]
        [InlineData("CALCULATE(SUM(Sales[Balance]), MAX('Date'[Date]) = 'Date'[Date])")]
        public async Task V8_A_balance_kept_to_one_date_is_quiet(string dax)
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "Closing balance", dax, "human");
                var s = Score(sessions, "SEMI-ADDITIVE-SUM");
                _output.WriteLine($"{dax}: {s.clean} -> {s.card}; findings {s.findings}");
                Assert.Equal(0, s.findings);
                Assert.Equal(s.clean, s.card);
            }
        }

        [Theory]
        [InlineData("VAR d = MAX('Date'[Date]) RETURN CALCULATE(SUM(Sales[Balance]), 'Date'[Date] = d)")]
        [InlineData("CALCULATE(SUM(Sales[Balance]), FILTER(ALL('Date'[Date]), 'Date'[Date] = MAX('Date'[Date])))")]
        public async Task V8_A_balance_kept_to_one_date_written_another_common_way_is_quiet(string dax)
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "Closing balance", dax, "human");
                var s = Score(sessions, "SEMI-ADDITIVE-SUM");
                _output.WriteLine($"{dax}: {s.clean} -> {s.card}; findings {s.findings}");
                Assert.Equal(0, s.findings);
            }
        }

        [Theory]
        [InlineData("SUM(Sales[Balance])")]
        [InlineData("CALCULATE(SUM(Sales[Balance]), 'Date'[Date] <= MAX('Date'[Date]))")]
        [InlineData("CALCULATE(SUM(Sales[Balance]), 'Date'[Year] = MAX('Date'[Year]))")]
        public async Task V8_A_balance_summed_across_dates_still_fires(string dax)
        {
            var (sessions, engine) = await ModelAsync();
            using (engine)
            {
                await engine.CreateMeasureAsync("table:Sales", "Closing balance", dax, "human");
                var s = Score(sessions, "SEMI-ADDITIVE-SUM");
                _output.WriteLine($"{dax}: {s.clean} -> {s.card}; findings {s.findings}");
                Assert.Equal(1, s.findings);
            }
        }
    }

    public sealed partial class AskDataTests
    {
        // F1 variant: allow, deny, allow again, deny again, each inside the three-second repeat window.
        [Fact]
        public async Task V1_Explain_follows_each_grant_and_denial_inside_the_repeat_window()
        {
            using var rig = await BuildAsync();
            AgentPolicyStore.SetPreset("open", "human");
            var model = SplitModel(SplitParts(), SplitCounts(), rig.Data.Execute);
            rig.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso Retail", "Contoso Retail", model));
            var agentDoor = AssistantEngine.For(rig.Engine);
            for (var round = 0; round < 2; round++)
            {
                await rig.Engine.SetAgentPolicyPresetAsync("open", "human");
                var granted = await McpTools.ExplainValue(agentDoor, "Net Sales", Year2025());
                Assert.True(granted.ValueEvaluated);
                Assert.NotEmpty(granted.Contributors.Rows);
                await rig.Engine.SetAgentPolicyPresetAsync("client", "human");
                var denied = await McpTools.ExplainValue(agentDoor, "Net Sales", Year2025());
                Assert.False(denied.ValueEvaluated);
                Assert.Null(denied.Value);
                Assert.True(denied.Contributors == null || denied.Contributors.Rows.Length == 0);
            }
        }
    }
}
