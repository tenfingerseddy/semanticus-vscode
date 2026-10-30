using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The calendar data check and the number check (1.7 engine groundwork). The pure halves (the DAX asked, the judging of what came back)
    /// are pinned offline; the live halves run against a stand-in live connection on a real model. The promise that matters most: a run that could
    /// not happen is "notChecked", never a pass.</summary>
    public sealed class CalendarChecksTests
    {
        private static DateTime D(int y, int m, int d) => new DateTime(y, m, d);

        private static CalendarCheckInput Input(params string[] facts) => new CalendarCheckInput
        {
            Table = "Date", DateColumn = "Date", FactColumns = facts,
            Calendars = new[]
            {
                new CalendarCheckShape
                {
                    Name = "Gregorian",
                    Levels = new[] { ("Year", "Year"), ("Quarter", "Quarter"), ("Month", "Month"), ("Date", "Date") },
                    Pairs = new[] { ("Month", new[] { "Month Name" }) },
                },
            },
        };

        // ---- the DAX they ask --------------------------------------------------------------------

        [Fact]
        public void Both_checks_ask_in_the_scalar_shape_so_no_rows_leave_the_model_and_no_data_approval_is_needed()
        {
            var i = Input("'Sales'[OrderDate]");
            Assert.True(DaxQueryClassifier.IsScalar(CalendarDataChecks.RangeQuery(i)));
            var (structure, _) = CalendarDataChecks.StructureQuery("Date", i.Calendars[0]);
            Assert.True(DaxQueryClassifier.IsScalar(structure));
            Assert.True(DaxQueryClassifier.IsScalar(MeasureNumbers.Query("Sales YTD", "Sales YTD (calendar)", new[] { "'Date'[Year]", "'Date'[Month]" })));
        }

        [Fact]
        public void The_range_query_reads_the_calendar_and_every_fact_column()
        {
            var q = CalendarDataChecks.RangeQuery(Input("'Sales'[OrderDate]", "'Returns'[ReturnDate]"));
            Assert.Contains("MIN ( 'Date'[Date] )", q);
            Assert.Contains("MAX ( 'Returns'[ReturnDate] )", q);
            Assert.Contains("\"f1max\"", q);
        }

        [Fact]
        public void The_structure_query_covers_blanks_nesting_and_one_to_one_and_skips_levels_that_cycle()
        {
            var shape = Input().Calendars[0];
            var (dax, labels) = CalendarDataChecks.StructureQuery("Date", shape);
            Assert.Equal(4 + 3 + 1, labels.Length);   // a blank check per level, a nesting check per neighbour pair, one pair
            Assert.Contains("DISTINCTCOUNT ( 'Date'[Year] )", dax);   // the quarter sits in one year
            Assert.Contains("ISBLANK ( 'Date'[Quarter] )", dax);
            Assert.Contains("DISTINCTCOUNT ( 'Date'[Month Name] )", dax);

            Assert.True(CalendarDataChecks.NestingRank("Year") < CalendarDataChecks.NestingRank("Quarter"));
            Assert.Equal(-1, CalendarDataChecks.NestingRank("QuarterOfYear"));
            Assert.Equal(-1, CalendarDataChecks.NestingRank("MonthOfYear"));
        }

        [Fact]
        public void A_name_with_a_quote_or_bracket_is_escaped_in_every_question()
        {
            Assert.Equal("'Kane''s Date'[Day]]x]", CalendarDataChecks.Quote("Kane's Date", "Day]x"));
            var q = MeasureNumbers.Query("Sales ]x[", "Sales 2", new[] { "'Date'[Year]" });
            Assert.Contains("[Sales ]]x[]", q);
        }

        // ---- judging the dates -------------------------------------------------------------------

        [Fact]
        public void Facts_that_start_before_or_run_past_the_calendar_are_problems()
        {
            var i = Input("'Sales'[OrderDate]");
            var f = CalendarDataChecks.JudgeRange(i, new CalendarRange
            {
                CalendarFrom = D(2024, 1, 1), CalendarTo = D(2025, 12, 31), DataFrom = D(2023, 12, 20), DataTo = D(2026, 1, 9), Rows = 731, Days = 731,
            }).ToArray();
            Assert.Equal(2, f.Count(x => x.Check == "covers-facts" && x.Level == "problem"));
            Assert.Contains(f, x => x.Sentence.Contains("12 days before the calendar"));
            Assert.Contains(f, x => x.Sentence.Contains("9 days past the end of the calendar"));
        }

        [Fact]
        public void A_calendar_built_past_the_last_date_with_data_is_a_note_not_a_problem()
        {
            var f = CalendarDataChecks.JudgeRange(Input("'Sales'[OrderDate]"), new CalendarRange
            {
                CalendarFrom = D(2024, 1, 1), CalendarTo = D(2030, 12, 31), DataFrom = D(2024, 1, 5), DataTo = D(2026, 10, 17), Rows = 2557, Days = 2557,
            }).ToArray();
            var note = Assert.Single(f);
            Assert.Equal("past-the-data", note.Check);
            Assert.Equal("note", note.Level);
            Assert.Contains("50 months past the last date with data", note.Sentence);
            Assert.Contains("start from the last date with data", note.Sentence);
        }

        [Fact]
        public void A_calendar_that_ends_with_the_month_of_the_data_has_nothing_to_report()
        {
            var f = CalendarDataChecks.JudgeRange(Input("'Sales'[OrderDate]"), new CalendarRange
            {
                CalendarFrom = D(2024, 1, 1), CalendarTo = D(2026, 10, 31), DataFrom = D(2024, 1, 5), DataTo = D(2026, 10, 17), Rows = 1035, Days = 1035,
            }).ToArray();
            Assert.Empty(f);
        }

        [Fact]
        public void Duplicate_dates_and_an_empty_calendar_are_problems_and_no_fact_dates_is_said_not_assumed()
        {
            var dup = CalendarDataChecks.JudgeRange(Input("'Sales'[OrderDate]"), new CalendarRange
            {
                CalendarFrom = D(2024, 1, 1), CalendarTo = D(2024, 12, 31), DataFrom = D(2024, 2, 1), DataTo = D(2024, 3, 1), Rows = 400, Days = 366,
            }).ToArray();
            Assert.Contains(dup, x => x.Check == "duplicate-dates" && x.Level == "problem");

            var empty = CalendarDataChecks.JudgeRange(Input("'Sales'[OrderDate]"), new CalendarRange { DataFrom = D(2024, 2, 1), DataTo = D(2024, 3, 1) }).ToArray();
            Assert.Contains(empty, x => x.Level == "problem" && x.Sentence.Contains("holds no dates"));

            var noFacts = CalendarDataChecks.JudgeRange(Input("'Sales'[OrderDate]"), new CalendarRange { CalendarFrom = D(2024, 1, 1), CalendarTo = D(2024, 12, 31) }).ToArray();
            Assert.Contains(noFacts, x => x.Level == "note" && x.Sentence.Contains("not checked"));
        }

        [Fact]
        public void Structure_counts_become_plain_findings()
        {
            var (_, labels) = CalendarDataChecks.StructureQuery("Date", Input().Calendars[0]);
            var counts = new Dictionary<string, long> { ["b1"] = 3, ["s0"] = 4, ["a0"] = 1, ["b0"] = 0, ["s1"] = 0 };
            var f = CalendarDataChecks.JudgeStructure("Gregorian", labels, counts).ToArray();
            Assert.Equal(3, f.Length);
            Assert.All(f, x => Assert.Equal("problem", x.Level));
            Assert.Contains(f, x => x.Check == "blanks" && x.Sentence.Contains("3 dates have no quarter"));
            Assert.Contains(f, x => x.Check == "level-split" && x.Sentence.Contains("4 values of Quarter sit in more than one Year") && x.Sentence.Contains("2025 Q1"));
            Assert.Contains(f, x => x.Check == "one-to-one" && x.Sentence.Contains("Month and Month Name do not pair"));
        }

        // ---- judging the numbers -----------------------------------------------------------------

        [Fact]
        public void The_same_numbers_everywhere_is_same()
        {
            var p = MeasureNumbers.Judge("A", "B", "24|0|0|");
            Assert.Equal("same", p.Verdict);
            Assert.Equal(24, p.Slices);
            Assert.Contains("all 24 slices", p.Reason);
        }

        [Fact]
        public void A_difference_names_the_slices_that_differ()
        {
            var p = MeasureNumbers.Judge("A", "B", "24|2|2|2025 / 3: 100 vs 90;2025 / 4: 55.5 vs 50");
            Assert.Equal("differs", p.Verdict);
            Assert.Equal(2, p.Differing);
            Assert.Equal(new[] { "2025 / 3: 100 vs 90", "2025 / 4: 55.5 vs 50" }, p.Examples);
        }

        [Theory]
        [InlineData("0|0|0|", "no rows in the slice")]
        [InlineData("12|12|0|", "blank everywhere")]
        [InlineData("not a number", "could not be read")]
        [InlineData(null, "could not be read")]
        public void A_run_that_compared_nothing_is_not_checked_never_same(string answer, string why)
        {
            var p = MeasureNumbers.Judge("A", "B", answer);
            Assert.Equal("notChecked", p.Verdict);
            Assert.Contains(why, p.Reason);
        }

        [Fact]
        public void A_failed_query_is_not_checked_and_says_why()
        {
            var p = MeasureNumbers.Judge("A", "B", null, "The calendar 'Fiscal' was not found.");
            Assert.Equal("notChecked", p.Verdict);
            Assert.Contains("Fiscal", p.Reason);
        }

        [Fact]
        public void Overall_is_same_only_when_every_pair_is_same()
        {
            MeasureNumbersPair P(string v) => new MeasureNumbersPair { Verdict = v };
            Assert.Equal("same", MeasureNumbers.Overall(new[] { P("same"), P("same") }));
            Assert.Equal("differs", MeasureNumbers.Overall(new[] { P("same"), P("differs"), P("notChecked") }));
            Assert.Equal("notChecked", MeasureNumbers.Overall(new[] { P("same"), P("notChecked") }));
            Assert.Equal("notChecked", MeasureNumbers.Overall(new MeasureNumbersPair[0]));
        }

        // ---- the engine, on a real model with a stand-in live connection --------------------------

        private static async Task<LocalEngine> CalendarModelAsync()
        {
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.CreateModelAsync("CalChecks", 1701);
            var date = await engine.CreateTableAsync("Date", "agent");
            var dateCol = await engine.CreateColumnAsync(date, "Date", "DateTime", "Date", "agent");
            await engine.CreateColumnAsync(date, "Year", "Int64", "Year", "agent");
            await engine.CreateColumnAsync(date, "Quarter", "String", "Quarter", "agent");
            await engine.CreateColumnAsync(date, "Month", "Int64", "Month", "agent");
            var sales = await engine.CreateTableAsync("Sales", "agent");
            var orderDate = await engine.CreateColumnAsync(sales, "OrderDate", "DateTime", "OrderDate", "agent");
            await engine.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "agent");
            await engine.CreateRelationshipAsync(orderDate, dateCol, null, true, "agent");
            await engine.DefineCalendarAsync(date, "Gregorian", new[]
            {
                new CalendarMappingSpec { Column = "Date", TimeUnit = "Date" },
                new CalendarMappingSpec { Column = "Year", TimeUnit = "Year" },
                new CalendarMappingSpec { Column = "Quarter", TimeUnit = "Quarter" },
                new CalendarMappingSpec { Column = "Month", TimeUnit = "Month" },
            }, null, "agent");
            await engine.CreateMeasureAsync(sales, "Sales", "SUM ( Sales[Amount] )", "agent");
            await engine.CreateMeasureAsync(sales, "Sales YTD", "TOTALYTD ( [Sales], 'Date'[Date] )", "agent");
            await engine.CreateMeasureAsync(sales, "Sales YTD Calendar", "TOTALYTD ( [Sales], 'Gregorian' )", "agent");
            return engine;
        }

        private static ResultSet Row(params (string Name, object Value)[] cells) => new ResultSet
        {
            Columns = cells.Select(c => new ColumnDef { Name = "[" + c.Name + "]" }).ToArray(),
            Rows = new[] { cells.Select(c => c.Value).ToArray() },
            RowCount = 1,
        };

        [Fact]
        public async Task The_data_check_flags_a_calendar_built_past_the_data_on_a_live_model()
        {
            using var engine = await CalendarModelAsync();
            var asked = new List<string>();
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "x", "db", q =>
            {
                asked.Add(q);
                return q.Contains("calMin")
                    ? Row(("calMin", D(2024, 1, 1)), ("calMax", D(2030, 12, 31)), ("rows", 2557L), ("days", 2557L), ("f0min", D(2024, 1, 5)), ("f0max", D(2026, 10, 17)))
                    : Row(("b0", 0L), ("b1", 0L), ("b2", 0L), ("b3", 0L), ("s0", 0L), ("s1", 0L), ("s2", 0L));
            }));

            var r = await engine.CheckCalendarDataAsync(null);

            Assert.Equal("found", r.Status);
            var t = Assert.Single(r.Tables);
            Assert.Equal("Date", t.Table);
            Assert.Equal("Date", t.DateColumn);
            Assert.Equal(new[] { "'Sales'[OrderDate]" }, t.FactColumns);
            Assert.Equal("2030-12-31", t.CalendarTo);
            Assert.Equal("2026-10-17", t.DataTo);
            var note = Assert.Single(r.Findings);
            Assert.Equal("past-the-data", note.Check);
            Assert.Equal("note", note.Level);
            Assert.All(asked, q => Assert.True(DaxQueryClassifier.IsScalar(q)));
        }

        [Fact]
        public async Task The_data_check_on_a_sound_calendar_is_ok()
        {
            using var engine = await CalendarModelAsync();
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "x", "db", q =>
                q.Contains("calMin")
                    ? Row(("calMin", D(2024, 1, 1)), ("calMax", D(2026, 10, 31)), ("rows", 1035L), ("days", 1035L), ("f0min", D(2024, 1, 5)), ("f0max", D(2026, 10, 17)))
                    : Row(("b0", 0L), ("b1", 0L), ("b2", 0L), ("b3", 0L), ("s0", 0L), ("s1", 0L), ("s2", 0L))));
            var r = await engine.CheckCalendarDataAsync("table:Date");
            Assert.Equal("ok", r.Status);
            Assert.Empty(r.Findings);
        }

        [Fact]
        public async Task The_data_check_reports_a_broken_calendar_as_problems()
        {
            using var engine = await CalendarModelAsync();
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "x", "db", q =>
                q.Contains("calMin")
                    ? Row(("calMin", D(2024, 3, 1)), ("calMax", D(2026, 10, 31)), ("rows", 972L), ("days", 972L), ("f0min", D(2024, 1, 5)), ("f0max", D(2026, 10, 17)))
                    : Row(("b0", 0L), ("b1", 2L), ("b2", 0L), ("b3", 0L), ("s0", 3L), ("s1", 0L), ("s2", 0L))));
            var r = await engine.CheckCalendarDataAsync(null);
            Assert.Equal("found", r.Status);
            Assert.Contains(r.Findings, f => f.Check == "covers-facts" && f.Level == "problem");
            Assert.Contains(r.Findings, f => f.Check == "blanks" && f.Calendar == "Gregorian");
            Assert.Contains(r.Findings, f => f.Check == "level-split");
            Assert.Contains("problem", r.Sentence);
        }

        [Fact]
        public async Task Without_a_live_model_the_data_check_is_not_checked_and_is_not_ok()
        {
            using var engine = await CalendarModelAsync();
            var r = await engine.CheckCalendarDataAsync(null);
            Assert.Equal("notChecked", r.Status);
            Assert.NotEqual("ok", r.Status);
            Assert.Contains("live model", r.Reason);
        }

        [Fact]
        public async Task A_query_that_fails_is_not_checked()
        {
            using var engine = await CalendarModelAsync();
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "x", "db", q => new ResultSet { Error = "The column 'Date'[Date] was not found." }));
            var r = await engine.CheckCalendarDataAsync(null);
            Assert.Equal("notChecked", r.Status);
            Assert.Contains("was not found", r.Reason);
        }

        [Fact]
        public async Task A_model_with_no_calendar_and_no_date_table_has_nothing_to_check_and_says_so()
        {
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.CreateModelAsync("NoCal", 1701);
            await engine.CreateTableAsync("Sales", "agent");
            var r = await engine.CheckCalendarDataAsync(null);
            Assert.Equal("notChecked", r.Status);
            Assert.Contains("no calendar", r.Reason);
        }

        [Fact]
        public async Task The_number_check_says_same_and_differs_from_what_the_live_model_returns()
        {
            using var engine = await CalendarModelAsync();
            string lastQuery = null;
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "x", "db", q =>
            {
                lastQuery = q;
                return Row(("r", q.Contains("[Sales YTD Calendar]") ? "36|0|0|" : "36|0|2|2025 / 3: 100 vs 90;2025 / 4: 55 vs 50"));
            }));

            var same = await engine.CheckSameNumbersAsync(new[] { "measure:Sales/Sales" }, new[] { "measure:Sales/Sales" }, null);
            Assert.Equal("differs", same.Verdict);   // the stand-in answers "differs" for any pair that is not the calendar one

            var r = await engine.CheckSameNumbersAsync(new[] { "Sales YTD" }, new[] { "Sales YTD Calendar" }, null);
            Assert.Equal("same", r.Verdict);
            Assert.Equal(new[] { "'Date'[Year]", "'Date'[Month]" }, r.By);   // the date table's own Year and Month
            Assert.Contains("[Sales YTD]", lastQuery);
            Assert.True(DaxQueryClassifier.IsScalar(lastQuery));

            var d = await engine.CheckSameNumbersAsync(new[] { "Sales YTD" }, new[] { "Sales" }, new[] { "column:Date/Year" });
            Assert.Equal("differs", d.Verdict);
            Assert.Equal(new[] { "'Date'[Year]" }, d.By);
            Assert.Equal(2, d.Pairs.Single().Examples.Length);
        }

        [Fact]
        public async Task Without_a_live_model_the_number_check_is_not_checked_and_is_not_same()
        {
            using var engine = await CalendarModelAsync();
            var r = await engine.CheckSameNumbersAsync(new[] { "Sales YTD" }, new[] { "Sales YTD Calendar" }, null);
            Assert.Equal("notChecked", r.Verdict);
            Assert.Contains("live model", r.Pairs.Single().Reason);
        }

        [Fact]
        public async Task The_number_check_refuses_a_name_that_is_not_a_measure_and_two_lists_of_different_length()
        {
            using var engine = await CalendarModelAsync();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CheckSameNumbersAsync(new[] { "Nope" }, new[] { "Sales" }, null));
            Assert.Contains("is not a measure", ex.Message);
            await Assert.ThrowsAsync<ArgumentException>(() => engine.CheckSameNumbersAsync(new[] { "Sales" }, new string[0], null));
        }
    }
}
