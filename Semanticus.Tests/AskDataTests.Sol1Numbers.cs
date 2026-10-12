// Sol 6.1's review of the joined M11 build (30 Sep), area 3, the four findings that live in the engine. Each case is Sol's own
// scenario and each failed before its fix:
//   3.3  "net sales last year excluding 2023" kept 2022 and 2025 beside 2024
//   3.4  a growth measure, DIVIDE ( [Sales], [Sales LY] ) - 1, was said to be Sales divided by Sales LY
//   3.5  a Quarter (Retail) column was said in the calendar's dates
//   3.7  the reading described the open copy's edited formula while the connected model ran its own
// (The webview's findings, 3.1, 3.2, 3.6 and 3.8, are in Semanticus.VSCode/test/sol1-numbers-webview.test.mjs.)

using System;
using System.Collections.Generic;
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
        // ---- 3.3. "last year" beside a filter on the same year column ----------------------------------------------------------

        private static bool KeepsYear(QueryFilter f, long year) => f.Op == QueryOps.Is
            ? f.Values.Any(v => Convert.ToInt64(v) == year)
            : f.Op == QueryOps.IsNot && f.Values.All(v => Convert.ToInt64(v) != year);

        private static async Task<(JevQueryReader.Reading Reading, QueryColumn Year)> ResolveLastYearAsync(Rig rig, params QueryFilter[] existing)
        {
            var cat = await CatalogAsync(rig);
            var year = cat.Column("Date[Year]");
            var reading = new JevQueryReader.Reading();
            reading.Spec.Measures.Add(cat.Measure("Net Sales"));
            foreach (var f in existing) { f.Column = year; reading.Spec.Filters.Add(f); }
            var said = QueryPeriods.Read("net sales last year");
            var cols = QueryPeriods.Find(cat, reading.Spec.Measures);
            var naming = QueryPeriods.ResolveColumns(reading, said, cols);
            Assert.NotNull(naming);
            Assert.True(QueryPeriods.Resolve(reading, said, cols, cat, new DateTime(2025, 10, 11), naming, new List<object[]> { new object[] { 2024L } }));
            return (reading, year);
        }

        [Fact]
        [Trait("Sol1", "3.3")]
        public async Task Sol1_last_year_beside_a_year_that_is_left_out_keeps_last_year_only()
        {
            using var rig = await BuildAsync(calendar: true);
            var (reading, year) = await ResolveLastYearAsync(rig, new QueryFilter { Op = QueryOps.IsNot, Values = { 2023L } });
            var kept = new[] { 2022L, 2023L, 2024L, 2025L }.Where(y => reading.Spec.Filters.Where(f => f.Column == year).All(f => KeepsYear(f, y))).ToArray();
            Assert.Equal(new[] { 2024L }, kept);
        }

        [Fact]
        [Trait("Sol1", "3.3")]
        public async Task Sol1_last_year_beside_a_wider_list_of_years_keeps_last_year_only()
        {
            using var rig = await BuildAsync(calendar: true);
            var (reading, year) = await ResolveLastYearAsync(rig, new QueryFilter { Op = QueryOps.Is, Values = { 2024L, 2025L } });
            var kept = new[] { 2022L, 2023L, 2024L, 2025L }.Where(y => reading.Spec.Filters.Where(f => f.Column == year).All(f => KeepsYear(f, y))).ToArray();
            Assert.Equal(new[] { 2024L }, kept);
        }

        [Fact]
        [Trait("Sol1", "3.3")]
        public async Task Sol1_last_year_beside_the_same_year_is_not_filtered_twice()
        {
            using var rig = await BuildAsync(calendar: true);
            var (reading, year) = await ResolveLastYearAsync(rig, new QueryFilter { Op = QueryOps.Is, Values = { 2024L } });
            Assert.Single(reading.Spec.Filters.Where(f => f.Column == year));
        }

        // ---- 3.4. a ratio is the whole formula, not a division somewhere in it -----------------------------------------------------

        [Theory]
        [Trait("Sol1", "3.4")]
        [InlineData("DIVIDE ( [Sales], [Sales LY] ) - 1")]
        [InlineData("DIVIDE ( [Sales], [Sales LY] ) * 100")]
        [InlineData("1 - DIVIDE ( [A], [B] )")]
        [InlineData("[Sales] / [Sales LY] - 1")]
        [InlineData("[Sales] * 100 / [Sales LY]")]
        [InlineData("( [Sales] - [Sales LY] ) / [Sales LY]")]
        [InlineData("DIVIDE ( [Sales] - [Sales LY], [Sales LY] )")]
        [InlineData("VAR d = [Sales] - [Sales LY] RETURN DIVIDE ( d, [Sales LY] )")]
        [InlineData("[A] / [B] / [C]")]
        [InlineData("IF ( [B] = 0, BLANK (), [A] / [B] ) + 1")]
        public void Sol1_a_division_inside_more_arithmetic_is_not_said_to_be_a_ratio(string dax)
        {
            var m = new QueryMeasure { Table = "Sales", Name = "Growth", Expression = dax, FormatString = "0.0%" };
            Assert.False(m.IsRatio, dax);
            Assert.Null(m.RatioParts);
        }

        [Fact]
        [Trait("Sol1", "3.4")]
        public async Task Sol1_a_growth_measure_is_said_by_its_name_and_never_as_one_divided_by_another()
        {
            using var rig = await BuildAsync();
            await rig.Engine.CreateMeasureAsync("table:Sales", "Sales Growth", "DIVIDE ( [Net Sales], [Units] ) - 1", "human");
            var cat = await CatalogAsync(rig);
            var person = StatedReading.From(Spec(cat, new string[0], new[] { "Sales Growth" }), null, cat).Person;
            Assert.Contains("Sales Growth", person);
            Assert.DoesNotContain("divided by", person);
        }

        // ---- 3.5. a period qualified AFTER its noun is not the calendar's ----------------------------------------------------------

        [Theory]
        [Trait("Sol1", "3.5")]
        [InlineData("Quarter (Retail)", "String", "Q2")]
        [InlineData("Quarter Retail", "String", "Q2")]
        [InlineData("Month (Trading)", "String", "March")]
        [InlineData("Year (Tax)", "Int64", "2025")]
        [InlineData("Quarter - Store Calendar", "String", "Q2")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Sol1_a_period_qualified_after_its_noun_is_said_without_dates(string column, string type, string value)
        {
            using var rig = await BuildAsync();
            await rig.Engine.CreateColumnAsync("table:Date", column, type, column, "human");
            var cat = await CatalogAsync(rig);
            object v = type == "Int64" ? long.Parse(value) : value;
            var person = StatedReading.From(Spec(cat, new string[0], new[] { "Net Sales" }, ("Date[" + column + "]", QueryOps.Is, new object[] { v })), null, cat).Person;
            Assert.Contains(column + " is " + value, person);
            Assert.False(person.Contains(" to ") || person.Contains("calendar"), "a " + column + " label said in calendar dates: '" + person + "'");
            var split = StatedReading.From(Spec(cat, new[] { "Date[" + column + "]" }, new[] { "Net Sales" }), null, cat).Person;
            Assert.False(split.Contains("calendar"), "each row of " + column + " called a calendar period: '" + split + "'");
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("Quarter (Retail)", "String", "Q2")]
        public Task Sol1_a_period_qualified_after_its_noun_is_said_without_dates_sample(string column, string type, string value) => Sol1_a_period_qualified_after_its_noun_is_said_without_dates(column, type, value);

        [Theory]
        [Trait("Sol1", "3.5")]
        [InlineData("Quarter Name", "String", "Q2", "Quarter Name is Q2 (1 April to 30 June)")]
        [InlineData("Month Name", "String", "March", "Month Name is March (1 to 31 March)")]
        [InlineData("Year Number", "Int64", "2025", "Year Number is 2025 (1 January to 31 December 2025)")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Sol1_the_plain_calendar_names_keep_their_dates(string column, string type, string value, string expected)
        {
            using var rig = await BuildAsync();
            await rig.Engine.CreateColumnAsync("table:Date", column, type, column, "human");
            var cat = await CatalogAsync(rig);
            object v = type == "Int64" ? long.Parse(value) : value;
            Assert.Equal("This shows Net Sales as one total, where " + expected + ".",
                StatedReading.From(Spec(cat, new string[0], new[] { "Net Sales" }, ("Date[" + column + "]", QueryOps.Is, new object[] { v })), null, cat).Person);
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("Quarter Name", "String", "Q2", "Quarter Name is Q2 (1 April to 30 June)")]
        public Task Sol1_the_plain_calendar_names_keep_their_dates_sample(string column, string type, string value, string expected) => Sol1_the_plain_calendar_names_keep_their_dates(column, type, value, expected);

        // ---- 3.7. what the reading says is the formula the connected model runs -----------------------------------------------------

        [Fact]
        [Trait("Sol1", "3.7")]
        public async Task Sol1_an_edited_formula_is_read_from_the_connected_model_and_the_answer_says_so()
        {
            using var rig = await BuildAsync(ratio: true);
            // The connected model still holds Net Sales divided by Units. The copy open here is edited and not published.
            await rig.Engine.SetDaxAsync("measure:Sales/Average Price", "DIVIDE ( [Net Sales], [Product Count] )", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Average Price" } });
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("Net Sales divided by Units", a.Statement);
            Assert.DoesNotContain("Product Count", a.Statement);
            Assert.Contains("Average Price", a.Note);
            Assert.Contains("connected model", a.Note);
        }

        [Fact]
        [Trait("Sol1", "3.7")]
        public async Task Sol1_an_unchanged_formula_says_nothing_extra()
        {
            using var rig = await BuildAsync(ratio: true);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Average Price" } });
            Assert.True(Answered(a), Seen(a));
            Assert.Null(a.Note);
        }

        [Fact]
        [Trait("Sol1", "3.7")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Sol1_a_connected_model_that_will_not_say_its_formulas_is_said()
        {
            using var rig = await BuildAsync(ratio: true);
            rig.Data.Connected = null;
            var a = await rig.Engine.AskDataAsync(new DataAsk { Measures = new[] { "Average Price" } });
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("could not read", a.Note);
        }
    }
}
