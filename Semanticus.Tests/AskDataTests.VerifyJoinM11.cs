// The M11 join verifier (1 Oct), nearest sibling of JOIN MUST FIX item 4 (join-2.md section 2): calendar dates go only on a column
// that is plainly the calendar. The join reads the COLUMN's name for a fiscal word and, when the model marks a date table, whether
// the column is on it. A plain "Quarter" or "Year" on a table the model itself calls a fiscal calendar or a financial year, with no
// date table marked, is still dated as the calendar: "Quarter is Q2 (1 April to 30 June)", "Financial Year Year is 2025 (1 January to
// 31 December 2025)", for periods that may start in July.

using System.Threading.Tasks;
using Semanticus.Engine.Query;
using Xunit;

namespace Semanticus.Tests
{
    public sealed partial class AskDataTests
    {
        [Theory]
        [Trait("VerifyJoin", "calendar-only")]
        [InlineData("Fiscal Calendar", "Quarter", "String", "Q2")]
        [InlineData("Financial Year", "Year", "Int64", "2025")]
        public async Task Verify_a_plain_period_on_a_fiscal_table_is_said_without_calendar_dates(string table, string column, string type, string value)
        {
            using var rig = await BuildAsync();
            await rig.Engine.CreateTableAsync(table, "human");
            await rig.Engine.CreateColumnAsync("table:" + table, column, type, column, "human");
            var cat = await CatalogAsync(rig);
            Assert.Empty(cat.DateTables);   // no date table is marked, as in the join's own qualified-name cases
            object v = type == "Int64" ? long.Parse(value) : value;
            var person = StatedReading.From(Spec(cat, new string[0], new[] { "Net Sales" }, (table + "[" + column + "]", QueryOps.Is, new object[] { v })), null, cat).Person;
            Assert.Contains(column + " is " + value, person);   // not vacuous: the filter was read and stated
            Assert.False(person.Contains(" to ") || person.Contains("calendar"), "a " + table + " " + column + " label said in calendar dates: '" + person + "'");
        }
    }
}
