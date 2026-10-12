// ask_data review-fix round 7 (30 Sep, narrow): what the verifier's round-5 acceptance test sent back, and nothing more. Written
// before the fixes; each test below failed on 4d3fa6a5 for the reason in its summary.
//   1  every period the statement filters or splits is named in calendar dates, so a person who asked for another kind of year
//      sees the gap on the screen
//   2  G4 by shape: a qualifier before a period noun that the reading does not use and the model has no column for (tax,
//      fiscal, academic, school, trading, or a slip of any of them) is said beside the number (K16: it was a stop)
//   3  G5 by grain: a share of a count answered by a ratio of amounts is said beside the number (K16: it was a stop); a percent
//      the model has as its own ratio runs with no line
//   4  the ranking questions whose order the parts call dropped are sorted or run, not asked back
// Since K16 (Kane, 8 Oct) none of these stops a run; CI never calls Jev (the answers are scripted).

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed partial class AskDataTests
    {
        // ---- 1. every period is shown in dates -------------------------------------------------------------------------------

        private static async Task<QueryCatalog> CalendarCatalogAsync(Rig rig)
        {
            await rig.Engine.CreateColumnAsync("table:Date", "Quarter", "String", "Quarter", "human");
            await rig.Engine.CreateColumnAsync("table:Date", "YearQuarter", "String", "YearQuarter", "human");
            return await CatalogAsync(rig);
        }

        [Theory]
        [Trait("Round7", "1")]
        [InlineData("Date[Year]", QueryOps.Is, "2025", "Year is 2025 (1 January to 31 December 2025)")]
        [InlineData("Date[Year]", QueryOps.Is, "2024,2025", "Year is 2024 or 2025 (1 January to 31 December 2024 and 1 January to 31 December 2025)")]
        [InlineData("Date[Year]", QueryOps.From, "2024", "Year is 2024 or later (from 1 January 2024)")]
        [InlineData("Date[Year]", QueryOps.After, "2024", "Year is after 2024 (from 1 January 2025)")]
        [InlineData("Date[Year]", QueryOps.Before, "2025", "Year is before 2025 (up to 31 December 2024)")]
        [InlineData("Date[Year]", QueryOps.UpTo, "2024", "Year is 2024 or earlier (up to 31 December 2024)")]
        [InlineData("Date[YearMonth]", QueryOps.Is, "2025-12", "YearMonth is 2025-12 (1 to 31 December 2025)")]
        [InlineData("Date[YearMonth]", QueryOps.Is, "2024-02", "YearMonth is 2024-02 (1 to 29 February 2024)")]
        [InlineData("Date[YearQuarter]", QueryOps.Is, "2025-Q4", "YearQuarter is 2025-Q4 (1 October to 31 December 2025)")]
        [InlineData("Date[Quarter]", QueryOps.Is, "Q3", "Quarter is Q3 (1 July to 30 September)")]
        [InlineData("Date[MonthName]", QueryOps.Is, "December", "MonthName is December (1 to 31 December)")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Every_period_a_statement_filters_is_named_in_calendar_dates(string column, string op, string values, string expected)
        {
            using var rig = await BuildAsync(calendar: true);
            var cat = await CalendarCatalogAsync(rig);
            var vals = values.Split(',').Select(v => column == "Date[Year]" ? (object)long.Parse(v) : v).ToArray();
            var stated = StatedReading.From(Spec(cat, new string[0], new[] { "Net Sales" }, (column, op, vals)), null, cat);
            Assert.Equal("This shows Net Sales as one total, where " + expected + ".", stated.Person);
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("Date[Year]", QueryOps.Is, "2025", "Year is 2025 (1 January to 31 December 2025)")]
        public Task Every_period_a_statement_filters_is_named_in_calendar_dates_sample(string column, string op, string values, string expected) => Every_period_a_statement_filters_is_named_in_calendar_dates(column, op, values, expected);

        [Fact]
        [Trait("Round7", "1")]
        public async Task A_period_a_statement_splits_by_says_what_each_row_is()
        {
            using var rig = await BuildAsync(calendar: true);
            var cat = await CalendarCatalogAsync(rig);
            string P(string col) => StatedReading.From(Spec(cat, new[] { col }, new[] { "Net Sales" }), null, cat).Person;
            Assert.Equal("This shows Net Sales for each Year (a calendar year, 1 January to 31 December).", P("Date[Year]"));
            Assert.Equal("This shows Net Sales for each YearMonth (a calendar month).", P("Date[YearMonth]"));
            Assert.Equal("This shows Net Sales for each Quarter (a calendar quarter).", P("Date[Quarter]"));
            Assert.Equal("This shows Net Sales for each MonthName (a calendar month).", P("Date[MonthName]"));
            // A column of days already is dates, and a column that is not a period says nothing more.
            Assert.Equal("This shows Net Sales for each Date.", P("Date[Date]"));
            Assert.Equal("This shows Net Sales for each Region.", P("Store[Region]"));
        }

        [Fact]
        [Trait("Round7", "1")]
        public async Task The_dates_are_said_to_the_person_and_never_sent_to_Jev()
        {
            using var rig = await BuildAsync(calendar: true);
            var cat = await CalendarCatalogAsync(rig);
            var stated = StatedReading.From(Spec(cat, new[] { "Date[Year]" }, new[] { "Net Sales" }, ("Date[Year]", QueryOps.Is, new object[] { 2025L })), null, cat);
            Assert.DoesNotContain("31 December", stated.ForJev);
            Assert.DoesNotContain("1 January", stated.ForJev);
            Assert.Contains("31 December 2025", stated.Person);
        }

        [Fact]
        [Trait("Round7", "1")]
        public async Task The_answer_says_the_calendar_dates_for_a_year_asked_in_another_kind()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, parts: VerifySure));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales for the 2025 tax year" });
            ScriptTaken(a);
            Assert.StartsWith("This shows Net Sales as one total, where Year is 2025 (1 January to 31 December 2025).", a.Statement);
            Assert.True(Answered(a), Seen(a));
        }

        // ---- 2. G4 by shape --------------------------------------------------------------------------------------------------

        [Theory]
        [Trait("Round7", "1")]
        [InlineData("net sales for the 2025 tax year", "tax year")]
        [InlineData("net sales in the academic year 2025", "academic year")]
        [InlineData("net sales for school year 2025", "school year")]
        [InlineData("net sales in the trading year 2025", "trading year")]
        [InlineData("net sales for the 2025 reporting period", "reporting period")]
        [InlineData("net sales in fisacl 2025", "fisacl")]
        [InlineData("net sales for the fiscl year 2025", "fiscl year")]
        [InlineData("net sales for the financial yaer 2025", "financial yaer")]
        [InlineData("net sales for the tax yeer 2025", "tax yeer")]
        [InlineData("net sales for the 2025 acedemic year", "acedemic year")]
        [InlineData("net sales for the 2025 tax quarter", "tax quarter")]
        [InlineData("net sales in fiscal 2025", "fiscal")]
        [InlineData("net sales for financial year 2025", "financial year")]
        [InlineData("net sales in FY 2025", "fy")]
        public async Task G4_a_qualified_period_the_reading_does_not_use_is_said_beside_the_calendar_number(string question, string named)
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, parts: VerifySure));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            ScriptTaken(a);
            Assert.True(Answered(a), "stopped, where the calendar number is shown with a line: " + V4Seen(a, rig));
            Assert.StartsWith("This shows Net Sales as one total, where Year is 2025 (1 January to 31 December 2025).", a.Statement);
            var line = a.Assumption ?? "";
            Assert.Contains(named, line, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("calendar", line);
            Assert.Empty(a.Unsure);
            Assert.Single(rig.Data.Runs);
            Assert.Empty(Calls(rig));
        }

        [Fact]
        [Trait("Round7", "1")]
        public async Task G4_a_qualified_period_with_no_period_in_the_reading_runs_and_says_the_reading_has_none()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, parts: VerifySure));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales in FY25" });
            ScriptTaken(a);
            Assert.True(Answered(a), V4Seen(a, rig));
            var line = a.Assumption ?? "";
            Assert.Contains("'FY25'", line);
            Assert.Contains("does not filter or split by that period", line);
            Assert.Single(rig.Data.Runs);
        }

        /// <summary>The guard is a shape (an unexplained word before a period word), so it must leave alone the words a person uses
        /// for the calendar, for a relative period, for a rank, for a count, and for the model's own words.</summary>
        [Theory]
        [Trait("Round7", "1")]
        [InlineData("net sales for calendar year 2025, not fiscal")]
        [InlineData("net sales for calendar year 2025 not the fiscal year")]
        [InlineData("net sales in the calendar year 2025")]
        [InlineData("net sales for the year 2025")]
        [InlineData("net sales for each year in 2025")]
        [InlineData("net sales for the whole of 2025 year")]
        [InlineData("net sales in the same period 2025")]
        [InlineData("net sales for the full year 2025")]
        [InlineData("net sales for the entire year 2025")]
        [InlineData("net sales in the 12 months of 2025")]
        [InlineData("net sales for the twelve months to 2025")]
        [InlineData("net sales in our best month of 2025")]
        [InlineData("net sales in the busiest month of 2025")]
        [InlineData("net sales for the calender year 2025")]
        [InlineData("net sales for thsi year 2025")]
        [InlineData("sales month over month in 2025")]
        [InlineData("net sales in december 2025")]
        [InlineData("net sales in Q4 2025")]
        [InlineData("net sales since 2025")]
        [InlineData("net sales in 2025")]
        public async Task G4_leaves_the_calendar_and_the_ordinary_words_for_it_alone(string question)
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, parts: VerifySure));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            ScriptTaken(a);
            var line = string.Join(" ", (a.Unsure ?? Array.Empty<string>()).Append(a.Assumption ?? ""));
            Assert.DoesNotContain("this model has no", line);
            Assert.DoesNotContain("The model has", line);
        }

        [Fact]
        [Trait("Round7", "1")]
        public async Task G4_leaves_a_word_the_model_itself_uses_and_a_word_of_a_value_alone()
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, parts: VerifySure));
            // "sales", "store" and "product" are words of the model's own names, so "sales year" and "store month" are the model's words.
            foreach (var q in new[] { "net sales for the sales year 2025", "net sales for the store month of 2025", "net sales by product category per month in 2025" })
            {
                var a = await rig.Engine.AskDataAsync(new DataAsk { Question = q });
                var line = string.Join(" ", (a.Unsure ?? Array.Empty<string>()).Append(a.Assumption ?? ""));
                Assert.DoesNotContain("this model has no", line);
                Assert.DoesNotContain("The model has", line);
            }
        }

        [Fact]
        [Trait("Round7", "1")]
        public async Task G4_names_the_column_the_model_has_for_a_qualifier_the_reading_does_not_use()
        {
            Values["Date[Fiscal Quarter]"] = new object[] { "Q1", "Q2", "Q3", "Q4" };
            using var rig = await BuildAsync(script: Gate("nothing", 0.95), calendar: true);
            await rig.Engine.CreateColumnAsync("table:Date", "Fiscal Quarter", "String", "Fiscal Quarter", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales for the fiscl quarter of 2025" });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("Fiscal Quarter", a.Assumption);
        }

        // ---- 3. G5 by grain --------------------------------------------------------------------------------------------------

        [Theory]
        [Trait("Round7", "1")]
        [InlineData("what percentage of orders had a return")]
        [InlineData("what percent of sales orders had a return in 2025")]
        [InlineData("what share of our customers returned something")]
        [InlineData("what proportion of orders that shipped were returned")]
        [InlineData("% of orders that had a return by category")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task G5_a_share_of_a_count_answered_by_a_ratio_of_amounts_is_said_beside_the_number(string question)
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.96, measure: "Return Rate %"));
            await ReturnRateAsync(rig);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            ScriptTaken(a);
            Assert.True(Answered(a), "stopped, where the ratio is shown with a line: " + V4Seen(a, rig));
            Assert.Contains("Return Rate % divides amounts of money", a.Assumption ?? "");
            Assert.Single(rig.Data.Runs);
            Assert.Empty(Calls(rig));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("what percentage of orders had a return")]
        public Task G5_a_share_of_a_count_answered_by_a_ratio_of_amounts_is_said_beside_the_number_sample(string question) => G5_a_share_of_a_count_answered_by_a_ratio_of_amounts_is_said_beside_the_number(question);

        [Theory]
        [Trait("Round7", "1")]
        [InlineData("return % by category")]
        [InlineData("returns percentage by category")]
        [InlineData("what percentage of sales were returned")]
        [InlineData("what percent of net sales were returns")]
        [InlineData("what is the return rate %")]
        [InlineData("return rate percent by region")]
        [InlineData("what share of gross sales was returned")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task G5_a_percent_the_model_has_as_its_own_ratio_runs(string question)
        {
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, measure: "Return Rate %", splits: question.Contains("by category") ? "1" : "0", parts: (r, q) => V2Split(q, 1, "Product[Category]")));
            await ReturnRateAsync(rig);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            ScriptTaken(a);
            Assert.True(Answered(a), "friction (G5): " + V4Seen(a, rig));
            Assert.Null(a.Assumption);
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("return % by category")]
        public Task G5_a_percent_the_model_has_as_its_own_ratio_runs_sample(string question) => G5_a_percent_the_model_has_as_its_own_ratio_runs(question);

        // ---- 4. the order Jev picked and the engine dropped -------------------------------------------------------------------

        /// <summary>Live in fresh-r5: "Which category has the worst return rate %?" (measure_high 0.84 by Jev, confidence 0.80) and "Rank
        /// states by revenue per unit" (measure_high 0.85, confidence 0.81) were read without their order, because the order needed the
        /// full bar; the check then asked back, rightly, that the order was missing. An order changes no number and the statement names
        /// it ("with the highest Net Sales first"), so a pick short of the bar is taken and said.</summary>
        [Theory]
        [Trait("Round7", "1")]
        [InlineData("rank cities by net sales", "measure_high", 0.81, "with the highest Net Sales first")]
        [InlineData("which city has the worst net sales", "measure_low", 0.7, "with the lowest Net Sales first")]
        [InlineData("cities in order", "group_a_z", 0.6, "with City A to Z")]
        public async Task A_sort_Jev_picked_short_of_the_bar_is_applied_and_said(string question, string sort, double confidence, string said)
        {
            (string, double)? Parts(DecisionRequest r, DecisionQuestion q) =>
                V2Split(q, 1, "Store[City]") ?? (q.Name == JevQueryReader.SortQ ? (sort, confidence) : null);
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, splits: "1", parts: Parts));
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            ScriptTaken(a);
            Assert.True(Answered(a), Seen(a));
            Assert.Contains(said, a.Statement);
        }

        [Fact]
        [Trait("Round7", "1")]
        public async Task A_sort_Jev_is_torn_over_or_calls_none_is_not_applied()
        {
            foreach (var (sort, confidence) in new[] { ("measure_high", 0.45), ("none", 0.9) })
            {
                (string, double)? Parts(DecisionRequest r, DecisionQuestion q) =>
                    V2Split(q, 1, "Store[City]") ?? (q.Name == JevQueryReader.SortQ ? (sort, confidence) : null);
                using var rig = await BuildAsync(script: Gate("nothing", 0.97, splits: "1", parts: Parts));
                var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales by city" });
                Assert.True(Answered(a), Seen(a));
                Assert.DoesNotContain("first", a.Statement);
            }
        }
    }
}
