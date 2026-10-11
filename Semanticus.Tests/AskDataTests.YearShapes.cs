// K21 (Kane, 9 Oct): "sales in 2008" on his live Contoso (the SQLBI DaxBook date table) ran the all-time Sales Amount with no
// year filter and nothing said. His Year column holds labels ("CY 2008") sorted by a hidden whole-number column, so no value
// matched the word 2008 and the year was dropped before the query. These facts run the question over the shapes a date
// table comes in. The rig is AskDataTests' own (the fake connection holds the shape's values; Jev is scripted: the number
// named, every plain filter "is").

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
        private static readonly object[] Years = { 2007L, 2008L, 2009L };
        private static object[] Labelled(string prefix) => Years.Select(y => (object)(prefix + y)).ToArray();

        /// <summary>One shape of the Date table: the Year column as the shape has it. The rig already holds Date[Year] as a whole number.</summary>
        private static async Task YearShapeAsync(string shape, LocalEngine e, FakeData d)
        {
            async Task Column(string name, string type) => await e.CreateColumnAsync("table:Date", name, type, name, "human");
            async Task Hide(string name) => await e.SetColumnMetadataAsync("column:Date/" + name, true, null, null, null, "human");
            async Task Text(string name) => await e.SetColumnDataTypeAsync("column:Date/" + name, "String", "human");
            switch (shape)
            {
                case "whole": d.Over["Date[Year]"] = Years; break;
                case "text": await Text("Year"); d.Over["Date[Year]"] = Years.Select(y => (object)y.ToString()).ToArray(); break;
                case "cy-labels": await Text("Year"); d.Over["Date[Year]"] = Labelled("CY "); break;
                case "year-labels": await Text("Year"); d.Over["Date[Year]"] = Labelled("Year "); break;
                case "daxbook":
                    // Kane's model: a date table marked Time, Year a string sorted by a hidden whole-number Year Number.
                    await Text("Year"); await Column("Year Number", "Int64"); await Hide("Year Number");
                    await e.SetColumnMetadataAsync("column:Date/Year", null, null, null, "Year Number", "human");
                    await e.MarkDateTableAsync("table:Date", "Date", "human");
                    d.Over["Date[Year]"] = Labelled("CY "); d.Over["Date[Year Number]"] = Years; break;
                case "year-hidden":
                    // The label column is hidden too: the plain numbers are all that is left to read.
                    await Text("Year"); await Hide("Year"); await Column("Year Number", "Int64"); await Hide("Year Number");
                    d.Over["Date[Year]"] = Labelled("CY "); d.Over["Date[Year Number]"] = Years; break;
                case "number-only":
                    await Hide("Year"); d.Over["Date[Year]"] = Years; break;
                case "year-and-month":
                    await Column("Year Month", "String"); d.Over["Date[Year]"] = Years;
                    d.Over["Date[Year Month]"] = new object[] { "2008-01", "2008-02", "2009-01" }; break;
                case "no-month":
                    // No quarter column and no month column either: only a column of days could hold a quarter.
                    await e.DeleteObjectAsync("column:Date/MonthName", "human"); d.Over["Date[Year]"] = Years; break;
                case "no-year":
                    // Nothing in this date table names a year at all.
                    await e.DeleteObjectAsync("column:Date/Year", "human"); break;
                case "year-month-only":
                    await e.DeleteObjectAsync("column:Date/Year", "human"); await Column("Year Month", "String");
                    d.Over["Date[Year Month]"] = new object[] { "2008-01", "2008-02", "2009-01" }; break;
                default: throw new ArgumentException(shape);
            }
        }

        public static IEnumerable<object[]> BindableYearShapes() =>
            new[] { "whole", "text", "cy-labels", "year-labels", "daxbook", "year-hidden", "number-only", "year-and-month" }.Select(s => new object[] { s });

        public static IEnumerable<object[]> UnbindableYearShapes() => new[] { "no-year", "year-month-only" }.Select(s => new object[] { s });

        private static async Task<DataAnswer> AskYearAsync(string shape, string question, bool calendar = false)
        {
            (string, double)? Script(DecisionRequest r, DecisionQuestion q) => V2Base(r, q);
            using var rig = await BuildAsync(script: Script, calendar: calendar || shape == "daxbook", shape: (e, d) => YearShapeAsync(shape, e, d), test: "year-" + shape);
            var a = await rig.Engine.AskDataAsync(new DataAsk { Question = question });
            ScriptTaken(a);
            return a;
        }

        /// <summary>The year the question names is a filter of the query, on a column whose value holds it.</summary>
        private static bool FiltersBy(DataAnswer a, string year) =>
            a.Parts?.Filters?.Any(f => f.Values != null && f.Values.Length > 0 && f.Values.All(v => v.Contains(year))) == true;

        /// <summary>K21. "net sales in 2008" filters to 2008 on every shape of the date table that holds the year somewhere.</summary>
        [Theory]
        [MemberData(nameof(BindableYearShapes))]
        public async Task A_year_in_the_question_filters_the_query_on_every_shape_of_the_date_table(string shape)
        {
            var a = await AskYearAsync(shape, "net sales in 2008");
            Assert.True(Answered(a), Seen(a));
            Assert.True(FiltersBy(a, "2008"), shape + ": the year was dropped: " + Seen(a) + "; parts " + string.Join(", ", a.Parts?.Filters?.Select(f => f.Column + " " + f.Op + " " + string.Join("|", f.Values)) ?? Array.Empty<string>()));
        }

        /// <summary>The invariant (K21): whatever the shape, a year the question names is either a filter of the query or said.
        /// Never the all-time total with nothing said.</summary>
        [Theory]
        [MemberData(nameof(BindableYearShapes))]
        [MemberData(nameof(UnbindableYearShapes))]
        public async Task A_year_the_question_names_is_filtered_or_said_never_dropped_in_silence(string shape)
        {
            var a = await AskYearAsync(shape, "net sales in 2008");
            Assert.True(Answered(a), Seen(a));
            Assert.True(FiltersBy(a, "2008") || (a.Assumption ?? "").Contains("Could not apply '2008', so this covers every date."),
                shape + ": 2008 was neither applied nor said: " + Seen(a));
        }

        /// <summary>A year no column of the date table can name is said in the plain line, beside the number.</summary>
        [Theory]
        [MemberData(nameof(UnbindableYearShapes))]
        public async Task A_year_no_column_can_hold_is_said_in_one_plain_line(string shape)
        {
            var a = await AskYearAsync(shape, "net sales in 2008");
            Assert.Equal("Could not apply '2008', so this covers every date.", a.Assumption);
        }

        /// <summary>The year the data does not hold, on a column of labels, is one plain line, as it is for a column of whole numbers.</summary>
        [Fact]
        public async Task A_year_the_labels_do_not_hold_says_there_is_no_data_for_it()
        {
            var a = await AskYearAsync("cy-labels", "net sales in 2012");
            Assert.Equal(DataAnswer.Refused, a.Outcome);
            Assert.Equal("There is no data for 2012. The data ends in 2009.", Assert.Single(a.Checks, c => !c.Passed).Line);
        }

        /// <summary>Two years of one label column are two rows, not one total of both.</summary>
        [Fact]
        public async Task Two_years_of_a_label_column_are_not_added_into_one_total()
        {
            var a = await AskYearAsync("year-hidden", "net sales in 2008 and 2009");
            Assert.True(Answered(a), Seen(a));
            Assert.True(SplitBy(a, "'Date'[Year]") || SplitBy(a, "'Date'[Year Number]"), "two years were added into one total: " + Seen(a));
        }

        /// <summary>A month the question names that no column holds is said too (the same invariant).</summary>
        [Fact]
        public async Task A_month_the_question_names_is_filtered_or_said()
        {
            var a = await AskYearAsync("whole", "net sales in july");
            Assert.True(Answered(a), Seen(a));
            Assert.True(HasFilter(a, "'Date'[MonthName]") || Says(a, "Could not apply 'july'"), Seen(a));
        }

        /// <summary>The words "may" and "march" as verbs name no month, so nothing is said about them.</summary>
        [Fact]
        public async Task Words_that_are_also_months_are_not_read_as_months_in_a_plain_sentence()
        {
            var a = await AskYearAsync("whole", "net sales may march up");
            Assert.False(Says(a, "Could not apply"), Seen(a));
        }

        // ---- A3 (hero run 4): a quarter, a week number, named and not applied ----------------------------------------------------

        /// <summary>A3. "net sales in Q1 2008" on a model with no quarter column ran the full year with nothing said. A quarter is read
        /// from the months it holds when the model has a column of months.</summary>
        [Theory]
        [InlineData("net sales in Q1 2008")]
        [InlineData("net sales in the first quarter of 2008")]
        public async Task A_quarter_the_model_has_no_column_for_filters_by_its_three_months(string question)
        {
            var a = await AskYearAsync("whole", question);
            Assert.True(Answered(a), Seen(a));
            Assert.True(FiltersBy(a, "2008"), Seen(a));
            var months = FilterOn(a, "'Date'[MonthName]")?.Values ?? Array.Empty<string>();
            Assert.Equal(new[] { "February", "January", "March" }, months.OrderBy(v => v, StringComparer.Ordinal).ToArray());
            Assert.False(Says(a, "Could not apply"), Seen(a));
        }

        /// <summary>A3. With no months to filter by, a column of days and the year the question names make the quarter a range of days.</summary>
        [Fact]
        public async Task A_quarter_with_only_a_column_of_days_is_a_range_of_days()
        {
            var a = await AskYearAsync("no-month", "net sales in Q1 2008", calendar: true);
            Assert.True(Answered(a), Seen(a));
            var days = a.Parts.Filters.Where(f => f.Column == "'Date'[Date]").ToList();
            Assert.Contains(days, f => f.Op == QueryOps.From && f.Values.Single() == "2008-01-01");
            Assert.Contains(days, f => f.Op == QueryOps.Before && f.Values.Single() == "2008-04-01");
            Assert.False(Says(a, "Could not apply"), Seen(a));
        }

        /// <summary>A3, the invariant for quarters: when nothing can hold the quarter it is said in the plain line, never the full year in silence.</summary>
        [Fact]
        public async Task A_quarter_nothing_can_hold_is_said_in_one_plain_line()
        {
            var a = await AskYearAsync("no-month", "net sales in Q1 2008");
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("Could not apply 'Q1', so this covers every date.", a.Assumption);
        }

        /// <summary>A3: the same invariant for a week number, which no calendar column of this model holds.</summary>
        [Fact]
        public async Task A_week_number_nothing_can_hold_is_said_in_one_plain_line()
        {
            var a = await AskYearAsync("whole", "net sales in week 12 of 2008");
            Assert.True(Answered(a), Seen(a));
            Assert.Contains("Could not apply 'week 12', so this covers every date.", a.Assumption);
        }
    }
}
