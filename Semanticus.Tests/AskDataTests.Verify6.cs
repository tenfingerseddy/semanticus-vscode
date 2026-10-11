// VERIFY, m11/jev-query-tune round 6 of verification (verifier, 30 Sep), on review-fix round 7 (every period said in dates). Jev is
// scripted the lane's way. Each case below fails at eb1a407f and is left failing on purpose:
//
//   R16  a period column named for ANOTHER kind of period is said in calendar dates. PeriodDates reads a column by
//        QueryPeriods, and IsQuarter has no "fiscal" exclusion while IsYear excludes only the word "fiscal": so "Fiscal
//        Quarter is Q2" is said as "(1 April to 30 June)", "Financial Year is 2025" and "Tax Year is 2025" as "(1 January to
//        31 December 2025)", a numbered "Fiscal Month is 1" as "(1 to 31 January)", and a split by any of them as "(a calendar
//        quarter)" or "(a calendar year, 1 January to 31 December)". The engine cannot know when such a period starts, so each
//        date is a guess, which PeriodDates' own header rules out ("A label this cannot read says nothing rather than guess").
//        End to end: "net sales in Q2 2025" on a model whose only quarter is Fiscal Quarter answers the fiscal Q2 under the
//        calendar dates of Q2, so the dates meant to show a gap now hide one (before round 7 the statement said only "Fiscal
//        Quarter is Q2", which at least named the column). Semanticus's own fiscal calendar template makes exactly these columns
//        (LocalEngine.CalendarTemplates.cs:212, 214: "Fiscal Quarter" = "FY2025 Q1", "Fiscal Month" = "FY2025 M01"), and both are
//        said as calendar January to March and January.

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
        [InlineData("Fiscal Quarter", "String", "Q2", "April")]
        [InlineData("Fiscal Quarter", "String", "FY Q2", "April")]
        [InlineData("Financial Year", "Int64", "2025", "January")]
        [InlineData("Tax Year", "Int64", "2025", "January")]
        [InlineData("Fiscal Month", "String", "1", "January")]
        // Semanticus's own fiscal calendar template (LocalEngine.CalendarTemplates.cs:212, 214) labels these "FY2025 Q1" and "FY2025 M01".
        [InlineData("Fiscal Quarter", "String", "FY2025 Q1", "January")]
        [InlineData("Fiscal Month", "String", "FY2025 M01", "January")]
        public async Task Verify6_R16_a_period_of_another_kind_is_not_said_in_calendar_dates(string column, string type, string value, string calendarMonth)
        {
            using var rig = await BuildAsync();
            await rig.Engine.CreateColumnAsync("table:Date", column, type, column, "human");
            var cat = await CatalogAsync(rig);
            object v = type == "Int64" ? long.Parse(value) : value;
            var stated = StatedReading.From(Spec(cat, new string[0], new[] { "Net Sales" }, ("Date[" + column + "]", QueryOps.Is, new object[] { v })), null, cat);
            Assert.False(stated.Person.Contains(calendarMonth),
                "a " + column + " label said in calendar dates, which the engine cannot know: '" + stated.Person + "'");
        }

        [Theory]
        [InlineData("Fiscal Quarter", "String")]
        [InlineData("Financial Year", "Int64")]
        [InlineData("Tax Year", "Int64")]
        public async Task Verify6_R16_a_split_by_a_period_of_another_kind_is_not_called_a_calendar_period(string column, string type)
        {
            using var rig = await BuildAsync();
            await rig.Engine.CreateColumnAsync("table:Date", column, type, column, "human");
            var cat = await CatalogAsync(rig);
            var stated = StatedReading.From(Spec(cat, new[] { "Date[" + column + "]" }, new[] { "Net Sales" }), null, cat);
            Assert.False(stated.Person.Contains("calendar"), "each row of " + column + " called a calendar period: '" + stated.Person + "'");
        }

        [Fact]
        public async Task Verify6_R16_a_calendar_quarter_answered_by_the_fiscal_quarter_is_not_shown_under_calendar_dates()
        {
            Values["Date[Fiscal Quarter]"] = new object[] { "Q1", "Q2", "Q3", "Q4" };
            using var rig = await BuildAsync(script: Gate("nothing", 0.97, parts: VerifySure));
            await rig.Engine.CreateColumnAsync("table:Date", "Fiscal Quarter", "String", "Fiscal Quarter", "human");
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = "net sales in Q2 2025" });
            ScriptTaken(a);
            Assert.True(V3Keeps(a, "'Date'[Fiscal Quarter]", QueryOps.Is, "Q2"), V4Seen(a, rig));
            Assert.False(Answered(a) && (a.Statement ?? "").Contains("1 April to 30 June"),
                "HIGH: the fiscal Q2 shown under the calendar dates of Q2: " + V4Seen(a, rig));
        }
    }
}
