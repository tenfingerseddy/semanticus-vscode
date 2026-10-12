// The M11 join, part 2 (30 Sep), on the verifier's R16 (AskDataTests.Verify6.cs): calendar dates go ONLY on a column that is
// plainly the calendar. A period column whose own name carries a qualifier before its period noun (Fiscal, Financial, Tax, FY,
// Academic, Retail, any word that is not a plain calendar word), or names a kind of year anywhere in it ("Quarter (FY)"), is a
// period the engine cannot date: it says the label and no dates, and a split by it is not called a calendar period. When the model
// marks a date table, only that table's columns are the calendar. Both directions are pinned here: the plain calendar columns
// keep their dates (Round7's cases cover Year, YearMonth, YearQuarter, Quarter and MonthName; these add Calendar Year, Calendar
// Quarter and Month).

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
        [Theory]
        [Trait("Join2", "calendar-only")]
        [InlineData("FY Quarter", "String", "Q2")]
        [InlineData("Academic Year", "Int64", "2025")]
        [InlineData("School Year", "Int64", "2025")]
        [InlineData("Retail Quarter", "String", "Q2")]
        [InlineData("Trading Month", "String", "March")]
        [InlineData("Fiscal Year", "Int64", "2025")]
        [InlineData("Quarter (FY)", "String", "Q2")]
        [InlineData("Financial Quarter", "String", "2025 Q2")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Join2_a_qualified_period_is_said_without_dates(string column, string type, string value)
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
        [InlineData("FY Quarter", "String", "Q2")]
        public Task Join2_a_qualified_period_is_said_without_dates_sample(string column, string type, string value) => Join2_a_qualified_period_is_said_without_dates(column, type, value);

        [Theory]
        [Trait("Join2", "calendar-only")]
        [InlineData("Calendar Year", "Int64", "2025", "Calendar Year is 2025 (1 January to 31 December 2025)", "a calendar year, 1 January to 31 December")]
        [InlineData("Calendar Quarter", "String", "Q2", "Calendar Quarter is Q2 (1 April to 30 June)", "a calendar quarter")]
        [InlineData("Month", "String", "March", "Month is March (1 to 31 March)", "a calendar month")]
        [InlineData("Year", "Int64", "2025", "Year is 2025 (1 January to 31 December 2025)", "a calendar year, 1 January to 31 December")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Join2_a_plain_calendar_period_keeps_its_dates(string column, string type, string value, string expected, string row)
        {
            using var rig = await BuildAsync();
            if (column != "Year") await rig.Engine.CreateColumnAsync("table:Date", column, type, column, "human");
            var cat = await CatalogAsync(rig);
            object v = type == "Int64" ? long.Parse(value) : value;
            Assert.Equal("This shows Net Sales as one total, where " + expected + ".",
                StatedReading.From(Spec(cat, new string[0], new[] { "Net Sales" }, ("Date[" + column + "]", QueryOps.Is, new object[] { v })), null, cat).Person);
            Assert.Equal("This shows Net Sales for each " + column + " (" + row + ").",
                StatedReading.From(Spec(cat, new[] { "Date[" + column + "]" }, new[] { "Net Sales" }), null, cat).Person);
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("Calendar Year", "Int64", "2025", "Calendar Year is 2025 (1 January to 31 December 2025)", "a calendar year, 1 January to 31 December")]
        public Task Join2_a_plain_calendar_period_keeps_its_dates_sample(string column, string type, string value, string expected, string row) => Join2_a_plain_calendar_period_keeps_its_dates(column, type, value, expected, row);

        [Fact]
        [Trait("Join2", "calendar-only")]
        public async Task Join2_with_a_marked_date_table_only_its_columns_are_dated()
        {
            using var rig = await BuildAsync(calendar: true);
            await rig.Engine.MarkDateTableAsync("table:Date", "Date", "human");
            await rig.Engine.CreateColumnAsync("table:Product", "Year", "Int64", "Year", "human");
            var cat = await CatalogAsync(rig);
            Assert.Contains("Date", cat.DateTables);
            string Filtered(string col) => StatedReading.From(Spec(cat, new string[0], new[] { "Net Sales" }, (col, QueryOps.Is, new object[] { 2025L })), null, cat).Person;
            // Two columns named Year: the statement names each by its table.
            Assert.Equal("This shows Net Sales as one total, where Date Year is 2025 (1 January to 31 December 2025).", Filtered("Date[Year]"));
            // A product's Year (a model year, a launch year) is not the calendar the model marked: no dates, no "a calendar year".
            Assert.DoesNotContain("January", Filtered("Product[Year]"));
            Assert.DoesNotContain("calendar", StatedReading.From(Spec(cat, new[] { "Product[Year]" }, new[] { "Net Sales" }), null, cat).Person);
        }
    }
}
