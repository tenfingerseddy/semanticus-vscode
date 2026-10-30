using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper.Utils;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// The calendar plan (propose_calendar, setup_calendars, add_time_measures). The load-bearing invariants: the plan is a pure read;
    /// setup is ONE undo step that takes the version, the columns, the calendars and the moved measures back together; a measure only
    /// moves when its date argument is the Date table's date column; and a state calendars cannot work in (live, no date) says so.
    /// </summary>
    public sealed class CalendarPlanTests
    {
        // ---- the measure mover (no model) ----------------------------------------------------------------

        [Theory]
        [InlineData("TOTALYTD ( [Total Sales], 'Date'[Date] )", "TOTALYTD ( [Total Sales], 'Calendar year' )")]
        [InlineData("TOTALYTD ( [Total Sales], Date[Date] )", "TOTALYTD ( [Total Sales], 'Calendar year' )")]
        [InlineData("TOTALMTD ( [Total Sales], 'Date'[Date] )", "TOTALMTD ( [Total Sales], 'Calendar year' )")]
        [InlineData("TOTALYTD ( [Total Sales], 'Date'[Date], \"12/31\" )", "TOTALYTD ( [Total Sales], 'Calendar year' )")]
        public void The_mover_swaps_the_date_argument_for_the_calendar(string before, string after)
        {
            var o = CalendarMeasureMove.Rewrite(before, "Date", "Date", "Calendar year", null, 7);
            Assert.Equal(after, o.After);
            Assert.Equal("year", o.Kind);
        }

        [Theory]
        [InlineData("CALCULATE ( [Total Sales], SAMEPERIODLASTYEAR ( 'Date'[Date] ) )")]
        [InlineData("CALCULATE ( [Sales YTD], DATEADD ( 'Date'[Date], -1, YEAR ) )")]
        public void A_prior_year_measure_stays_because_a_calendar_reads_29_february_differently(string dax)
        {
            var o = CalendarMeasureMove.Rewrite(dax, "Date", "Date", "Calendar year", null, 7);
            Assert.Null(o.After);
            Assert.Contains("29 February", o.Why);
        }

        [Fact]
        public void A_year_end_that_fits_the_fiscal_year_moves_to_the_fiscal_calendar_and_drops_the_year_end()
        {
            var o = CalendarMeasureMove.Rewrite("TOTALYTD ( [Total Sales], 'Date'[Date], \"6/30\" )", "Date", "Date", "Calendar year", "Fiscal year", 7);
            Assert.Equal("TOTALYTD ( [Total Sales], 'Fiscal year' )", o.After);
            Assert.Equal("fiscal", o.Kind);
        }

        [Fact]
        public void A_year_end_with_a_filter_keeps_the_filter()
        {
            var o = CalendarMeasureMove.Rewrite("TOTALYTD ( [S], 'Date'[Date], Sales[Region] = \"West\", \"6/30\" )", "Date", "Date", "Calendar year", "Fiscal year", 7);
            Assert.Equal("TOTALYTD ( [S], 'Fiscal year', Sales[Region] = \"West\" )", o.After);
        }

        [Fact]
        public void A_year_end_with_no_fiscal_calendar_or_another_start_month_stays_with_a_reason()
        {
            var none = CalendarMeasureMove.Rewrite("TOTALYTD ( [S], 'Date'[Date], \"6/30\" )", "Date", "Date", "Calendar year", null, 7);
            Assert.Null(none.After);
            Assert.Contains("fiscal calendar", none.Why);
            var other = CalendarMeasureMove.Rewrite("TOTALYTD ( [S], 'Date'[Date], \"6/30\" )", "Date", "Date", "Calendar year", "Fiscal year", 4);
            Assert.Null(other.After);
            Assert.Contains("same month", other.Why);
        }

        [Fact]
        public void The_mover_leaves_text_it_does_not_own_alone()
        {
            Assert.Null(CalendarMeasureMove.Rewrite("TOTALYTD ( [S], 'Other'[Date] )", "Date", "Date", "Calendar year", null, 7).After);
            Assert.Null(CalendarMeasureMove.Rewrite("TOTALYTD ( [S], 'Date'[Month Start] )", "Date", "Date", "Calendar year", null, 7).After);
            var o = CalendarMeasureMove.Rewrite("// TOTALYTD ( [S], 'Date'[Date] )\nTOTALYTD ( [S], 'Date'[Date] ) & \"SAMEPERIODLASTYEAR('Date'[Date])\"", "Date", "Date", "Calendar year", null, 7);
            Assert.Contains("TOTALYTD ( [S], 'Calendar year' )", o.After);
            Assert.StartsWith("// TOTALYTD ( [S], 'Date'[Date] )", o.After);
            Assert.EndsWith("\"SAMEPERIODLASTYEAR('Date'[Date])\"", o.After);
        }

        [Fact]
        public void A_rolling_window_and_a_nested_time_function_stay()
        {
            var roll = CalendarMeasureMove.Rewrite("CALCULATE ( [S], DATESINPERIOD ( 'Date'[Date], MAX ( 'Date'[Date] ), -12, MONTH ) )", "Date", "Date", "Calendar year", null, 7);
            Assert.Null(roll.After); Assert.True(roll.Rolling);
            var nested = CalendarMeasureMove.Rewrite("TOTALYTD ( CALCULATE ( [S], SAMEPERIODLASTYEAR ( 'Date'[Date] ) ), 'Date'[Date] )", "Date", "Date", "Calendar year", null, 7);
            Assert.Null(nested.After);
            Assert.Contains("nests", nested.Why);
        }

        [Theory]
        [InlineData("6/30", 7)] [InlineData("12/31", 1)] [InlineData("30 June", 7)] [InlineData("March 31", 4)] [InlineData("30/6", 7)]
        public void A_year_end_gives_the_month_the_year_starts_in(string text, int start) => Assert.Equal(start, CalendarMeasureMove.StartMonthFromYearEnd(text));

        [Fact]
        public void A_year_end_nobody_can_read_gives_nothing() => Assert.Null(CalendarMeasureMove.StartMonthFromYearEnd("the end"));

        // ---- the number check's answer (the engine's judged pairs into the page's words) -----------------------------------

        private static MeasureNumbersPair Pair(string verdict, string reason, params string[] examples) =>
            new MeasureNumbersPair { Old = "Sales YTD", New = "n", Verdict = verdict, Reason = reason, Examples = examples };

        [Fact]
        public void The_number_check_says_same_only_when_every_pair_was_compared_and_matched()
        {
            var by = new[] { "'Date'[Year]", "'Date'[Year Month]" };
            var same = LocalEngine.NumberCheckOf(new[] { Pair("same", "Same numbers in all 12 slices.") }, by);
            Assert.Equal("same", same.Status); Assert.Equal("every Year and Year Month", same.Slice); Assert.True(Assert.Single(same.Rows).Same);

            var differ = LocalEngine.NumberCheckOf(new[] { Pair("same", "ok"), Pair("differs", "2 of 12 slices differ.", "2026 / 2026-06: 10 vs 12") }, by);
            Assert.Equal("differ", differ.Status);
            Assert.Equal("2026 / 2026-06: 10 vs 12", differ.Rows.Single(r => !r.Same).Before);
        }

        [Fact]
        public void A_pair_that_could_not_run_is_not_a_pass_and_shows_no_raw_error()
        {
            var by = new[] { "'Date'[Year]" };
            var cal = LocalEngine.NumberCheckOf(new[] { Pair("notChecked", "Not checked: the query failed: Query (1, 94) Cannot find table 'Calendar year'.\n\nTechnical Details: RootActivityId: x") }, by);
            Assert.Equal("not-checked", cal.Status);
            Assert.Empty(cal.Rows);
            Assert.Contains("no calendars yet", cal.Reason);
            Assert.DoesNotContain("RootActivityId", cal.Reason);

            var mixed = LocalEngine.NumberCheckOf(new[] { Pair("same", "ok"), Pair("notChecked", "Not checked: both measures are blank everywhere in the slice, so nothing was compared.") }, by);
            Assert.Equal("not-checked", mixed.Status);   // one unchecked pair keeps the whole answer from reading as a pass
            Assert.Contains("blank everywhere", mixed.Reason);
        }

        // ---- a Contoso-style model --------------------------------------------------------------------------

        private static async Task<LocalEngine> ClassicModelAsync(int cl = 1604, bool fiscalInstructions = true, bool markDate = true, bool proveNumbers = false)
        {
            var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.CreateModelAsync("Contoso", cl);
            var date = await e.CreateTableAsync("Date", "agent");
            foreach (var (n, t) in new[] { ("Date", "DateTime"), ("Year", "Int64"), ("Quarter", "String"), ("Year Month", "String"), ("Month Name", "String"), ("Month Number", "Int64"),
                                           ("Day of Week", "String"), ("Day of Week Number", "Int64"), ("Is Working Day", "Boolean"), ("Holiday Name", "String") })
                await e.CreateColumnAsync(date, n, t, n, "agent");
            var sales = await e.CreateTableAsync("Sales", "agent");
            await e.CreateColumnAsync(sales, "Order Date", "DateTime", "Order Date", "agent");
            await e.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "agent");
            await e.CreateRelationshipAsync("column:Sales/Order Date", "column:Date/Date", null, null, "agent");
            if (markDate) await e.MarkDateTableAsync(date, "Date", "agent");
            await e.CreateMeasureAsync(sales, "Total Sales", "SUM ( Sales[Amount] )", "agent");
            await e.CreateMeasureAsync(sales, "Sales YTD", "TOTALYTD ( [Total Sales], 'Date'[Date] )", "agent");
            await e.CreateMeasureAsync(sales, "Sales Last Year", "CALCULATE ( [Total Sales], SAMEPERIODLASTYEAR ( 'Date'[Date] ) )", "agent");
            await e.CreateMeasureAsync(sales, "Sales Last Year YTD", "CALCULATE ( [Sales YTD], DATEADD ( 'Date'[Date], -1, YEAR ) )", "agent");
            await e.CreateMeasureAsync(sales, "Sales MTD", "TOTALMTD ( [Total Sales], 'Date'[Date] )", "agent");
            await e.CreateMeasureAsync(sales, "Sales Fiscal YTD", "TOTALYTD ( [Total Sales], 'Date'[Date], \"6/30\" )", "agent");
            await e.CreateMeasureAsync(sales, "Sales Rolling 12M", "CALCULATE ( [Total Sales], DATESINPERIOD ( 'Date'[Date], MAX ( 'Date'[Date] ), -12, MONTH ) )", "agent");
            if (fiscalInstructions) await e.SetAiInstructionsAsync("Our fiscal year starts on 1 July. Sales means net sales.", null, "agent");
            if (proveNumbers)
                e.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "x", "db", q => new ResultSet
                {
                    Columns = new[] { new ColumnDef { Name = "r" } },
                    Rows = new[] { new object[] { "366|0|0||3" } }, RowCount = 1,
                }));
            return e;
        }

        private static async Task<string[]> ExpressionsAsync(LocalEngine e)
        {
            var objs = await e.GetModelObjectsAsync();
            return objs.Measures.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name + "=" + x.Expression).ToArray();
        }

        [Fact]
        public async Task With_no_model_to_compare_on_no_measure_is_promised_a_move_and_each_says_why_it_stays()
        {
            using var e = await ClassicModelAsync();   // no live model: the numbers cannot be compared
            var before = await ExpressionsAsync(e);

            var p = await e.ProposeCalendarAsync(null, null, 0, null);

            Assert.Empty(p.Moves);   // the card counts Moves: it must not say "3 measures move" when none will
            foreach (var name in new[] { "Sales Fiscal YTD", "Sales MTD", "Sales YTD" })
                Assert.Contains("compares its numbers", p.Stays.Single(x => x.Measure == name).Why);
            Assert.Contains(p.Stays, x => x.Measure == "Sales Last Year");   // the ones that always stay keep their own reasons

            var r = await e.SetupCalendarsAsync(null, null, 0, null, true, "agent");
            Assert.Empty(r.Moved);
            Assert.Contains("compares its numbers", r.Stays.Single(x => x.Measure == "Sales YTD").Why);   // not the 29 February line
            Assert.Equal(before, await ExpressionsAsync(e));   // and the measure really is unchanged
        }

        [Fact]
        public async Task The_proposal_reads_the_date_table_the_fiscal_clue_and_the_old_measures_and_writes_nothing()
        {
            using var e = await ClassicModelAsync(proveNumbers: true);   // a measure is promised a move only where its numbers can be compared
            var before = await ExpressionsAsync(e);

            var p = await e.ProposeCalendarAsync(null, null, 0, null);

            Assert.Equal("ready", p.State);
            Assert.Equal("Date", p.DateTable); Assert.Equal("Date", p.DateColumn);
            Assert.Equal(1604, p.CompatibilityLevel); Assert.True(p.WillRaiseLevel);
            Assert.Equal(7, p.FiscalStartMonth);
            Assert.Contains("fiscal year starts in July", p.FiscalClue);
            Assert.Equal(new[] { "Calendar year", "Fiscal year" }, p.Calendars.Select(c => c.Name));
            Assert.Equal(new[] { "Sales Fiscal YTD", "Sales MTD", "Sales YTD" }, p.Moves.Select(x => x.Measure).OrderBy(x => x, StringComparer.Ordinal));
            Assert.Equal("Fiscal year", p.Moves.Single(x => x.Measure == "Sales Fiscal YTD").Calendar);
            Assert.Contains("TOTALYTD ( [Total Sales], 'Fiscal year' )", p.Moves.Single(x => x.Measure == "Sales Fiscal YTD").After);
            Assert.Equal(new[] { "Sales Last Year", "Sales Last Year YTD", "Sales Rolling 12M" }, p.Stays.Select(x => x.Measure).OrderBy(x => x, StringComparer.Ordinal));
            Assert.Contains(p.Choices, c => c.Kind == "year" && c.Locked && c.On);
            Assert.Contains(p.Choices, c => c.Kind == "fiscal" && c.On && !c.Locked);
            Assert.Contains(p.Choices, c => c.Kind == "retail" && !c.On);

            Assert.Equal(before, await ExpressionsAsync(e));   // a read
        }

        [Fact]
        public async Task The_calendar_year_ladder_uses_the_columns_the_Date_table_already_has_and_adds_only_what_is_missing()
        {
            using var e = await ClassicModelAsync();
            var year = (await e.ProposeCalendarAsync(null, new[] { "year" }, 0, null)).Calendars.Single();
            Assert.Equal(new[] { "Year", "Quarter", "Quarter of year", "Month", "Month of year", "Day", "Day of month", "Day of week" }, year.Levels.Select(l => l.Level).ToArray());
            var q = year.Levels.Single(l => l.Unit == "QuarterOfYear");
            Assert.Equal("Quarter", q.Column); Assert.False(q.IsNew);               // the existing column holds Q1 to Q4
            Assert.Equal("Year Quarter", year.Levels.Single(l => l.Unit == "Quarter").Column);
            Assert.True(year.Levels.Single(l => l.Unit == "Quarter").IsNew);        // and a new one fills "quarter"
            var month = year.Levels.Single(l => l.Unit == "MonthOfYear");
            Assert.Equal("Month Name", month.Column); Assert.Equal("Month Number", month.AlsoColumn); Assert.Equal("Month Number", month.SortBy);
            Assert.Equal("Year Month", year.Levels.Single(l => l.Unit == "Month").Column); Assert.False(year.Levels.Single(l => l.Unit == "Month").IsNew);
            Assert.Contains("Is Working Day", year.Other); Assert.Contains("Holiday Name", year.Other);
        }

        [Fact]
        public async Task Setup_is_one_undo_step_that_takes_back_the_version_the_columns_the_calendars_and_the_moved_measures()
        {
            using var e = await ClassicModelAsync(proveNumbers: true);
            var before = await ExpressionsAsync(e);
            var cols = (await e.ListColumnsAsync()).Where(c => c.Table == "Date").Select(c => c.Name).OrderBy(x => x).ToArray();

            var r = await e.SetupCalendarsAsync(null, null, 0, null, true, "agent");

            Assert.Equal(new[] { "Calendar year", "Fiscal year" }, r.Calendars);
            Assert.Equal(1604, r.FromLevel); Assert.Equal(1701, r.ToLevel);
            Assert.Contains("Year Quarter", r.ColumnsAdded); Assert.Contains("Fiscal Year", r.ColumnsAdded); Assert.DoesNotContain("Quarter", r.ColumnsAdded);
            Assert.Equal(3, r.Moved.Length);   // both spellings of the prior-year shift stay
            Assert.Equal("same", r.NumberCheck.Status);
            Assert.Equal("every Date", r.NumberCheck.Slice);

            var list = await e.ListCalendarsAsync(null);
            Assert.True(list.CalendarsSupported);
            Assert.Equal(new[] { "Calendar year", "Fiscal year" }, list.Calendars.Select(c => c.Name).OrderBy(x => x));
            var after = await ExpressionsAsync(e);
            Assert.Contains("Sales YTD=TOTALYTD ( [Total Sales], 'Calendar year' )", after);
            Assert.Contains("Sales Fiscal YTD=TOTALYTD ( [Total Sales], 'Fiscal year' )", after);
            Assert.Contains(after, x => x.StartsWith("Sales Rolling 12M=") && x.Contains("'Date'[Date]"));   // stays
            Assert.Contains("Sales Last Year=CALCULATE ( [Total Sales], SAMEPERIODLASTYEAR ( 'Date'[Date] ) )", after);
            Assert.Contains("Sales Last Year YTD=CALCULATE ( [Sales YTD], DATEADD ( 'Date'[Date], -1, YEAR ) )", after);

            await e.UndoAsync("human");
            Assert.Equal(before, await ExpressionsAsync(e));
            Assert.Empty((await e.ListCalendarsAsync(null)).Calendars);
            Assert.False((await e.ListCalendarsAsync(null)).CalendarsSupported);   // the version is back at 1604
            Assert.Equal(cols, (await e.ListColumnsAsync()).Where(c => c.Table == "Date").Select(c => c.Name).OrderBy(x => x).ToArray());

            await e.RedoAsync("human");
            Assert.Equal(2, (await e.ListCalendarsAsync(null)).Calendars.Length);
            Assert.Contains("Sales YTD=TOTALYTD ( [Total Sales], 'Calendar year' )", await ExpressionsAsync(e));
        }

        [Fact]
        public async Task Setup_can_leave_the_old_measures_alone()
        {
            using var e = await ClassicModelAsync(fiscalInstructions: false);
            var before = await ExpressionsAsync(e);
            var r = await e.SetupCalendarsAsync(null, new[] { "year" }, 0, null, false, "agent");
            Assert.Empty(r.Moved);
            Assert.Equal(before, await ExpressionsAsync(e));
            Assert.Single((await e.ListCalendarsAsync(null)).Calendars);
        }

        [Fact]
        public async Task A_chosen_level_column_and_a_left_out_level_change_the_calendar()
        {
            using var e = await ClassicModelAsync();
            var levels = new[]
            {
                new CalendarLevelChoice { Calendar = "year", Unit = "DayOfWeek", Column = "Day of Week Number" },
                new CalendarLevelChoice { Calendar = "year", Unit = "DayOfMonth", Column = null },
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => e.ProposeCalendarAsync(null, new[] { "year" }, 0, new[] { new CalendarLevelChoice { Calendar = "year", Unit = "Date", Column = null } }));
            var p = await e.ProposeCalendarAsync(null, new[] { "year" }, 0, levels);
            var year = p.Calendars.Single();
            Assert.Equal("Day of Week Number", year.Levels.Single(l => l.Unit == "DayOfWeek").Column);
            Assert.DoesNotContain(year.Levels, l => l.Unit == "DayOfMonth");
            await e.SetupCalendarsAsync(null, new[] { "year" }, 0, levels, false, "agent");
            var cal = (await e.ListCalendarsAsync(null)).Calendars.Single();
            Assert.Contains(cal.Groups, g => g.TimeUnit == "DayOfWeek" && g.PrimaryColumn == "Day of Week Number");
            Assert.DoesNotContain(cal.Groups, g => g.TimeUnit == "DayOfMonth");
        }

        [Fact]
        public async Task A_model_with_no_Date_table_gets_one_cut_from_its_order_dates_linked_and_marked_in_one_undo_step()
        {
            using var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.CreateModelAsync("NoDate", 1604);
            var sales = await e.CreateTableAsync("Sales", "agent");
            await e.CreateColumnAsync(sales, "Order Date", "DateTime", "Order Date", "agent");
            await e.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "agent");
            await e.CreateMeasureAsync(sales, "Total Sales", "SUM ( Sales[Amount] )", "agent");

            var p = await e.ProposeCalendarAsync(null, null, 0, null);
            Assert.Equal("no-date-table", p.State);
            Assert.Equal("Sales[Order Date]", p.DateSource);
            Assert.Equal("Date", p.DateTable);

            var r = await e.SetupCalendarsAsync(null, null, 0, null, true, "agent");
            Assert.True(r.CreatedTable);
            var objs = await e.GetModelObjectsAsync();
            Assert.Contains(objs.Tables, t => t.Name == "Date");
            var cal = Assert.Single((await e.ListCalendarsAsync(null)).Calendars);
            Assert.Equal("Calendar year", cal.Name); Assert.Equal("Date", cal.Table);
            Assert.Contains(objs.Columns, c => c.Table == "Date" && c.Name == "Year Quarter");
            Assert.True(objs.Columns.Single(c => c.Table == "Date" && c.Name == "Date").IsKey);
            Assert.Equal("set-up", (await e.ProposeCalendarAsync(null, null, 0, null)).State);

            await e.UndoAsync("human");
            Assert.DoesNotContain((await e.GetModelObjectsAsync()).Tables, t => t.Name == "Date");
            Assert.Empty((await e.ListCalendarsAsync(null)).Calendars);
            await e.RedoAsync("human");
            Assert.Contains((await e.GetModelObjectsAsync()).Tables, t => t.Name == "Date");
            Assert.Single((await e.ListCalendarsAsync(null)).Calendars);
        }

        [Fact]
        public async Task A_model_with_no_date_column_says_there_is_nothing_to_build_from()
        {
            using var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.CreateModelAsync("Bare", 1604);
            var t = await e.CreateTableAsync("Sales", "agent");
            await e.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "agent");
            var p = await e.ProposeCalendarAsync(null, null, 0, null);
            Assert.Equal("unavailable", p.State);
            Assert.Contains("no date column", p.Note);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.SetupCalendarsAsync(null, null, 0, null, true, "agent"));
            Assert.Contains("no date column", ex.Message);
        }

        [Fact]
        public async Task A_model_that_reads_another_model_live_says_calendars_go_there()
        {
            using var e = await ClassicModelAsync();
            await e.SetPartitionMAsync("partition:Sales/Sales", "let Source = AnalysisServices.Database(\"powerbi://api.powerbi.com/v1.0/myorg/WS\", \"Contoso Sales\") in Source", "agent");
            var p = await e.ProposeCalendarAsync(null, null, 0, null);
            Assert.Equal("live", p.State);
            Assert.Contains("live connection", p.Note);
            Assert.Empty(p.Calendars);
            await Assert.ThrowsAsync<InvalidOperationException>(() => e.SetupCalendarsAsync(null, null, 0, null, true, "agent"));
        }

        [Fact]
        public async Task A_model_with_calendars_already_proposes_the_missing_choices_and_the_time_measures_and_adds_them_in_one_step()
        {
            using var e = await ClassicModelAsync(fiscalInstructions: false, proveNumbers: true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => e.AddTimeMeasuresAsync(null, "agent"));   // no calendars yet

            await e.SetupCalendarsAsync(null, new[] { "year" }, 0, null, true, "agent");
            var p = await e.ProposeCalendarAsync(null, null, 0, null);
            Assert.Equal("set-up", p.State);
            Assert.True(p.Calendars.Single().Exists);
            Assert.Contains(p.Choices, c => c.Kind == "year" && c.Exists && !c.Locked);
            Assert.Contains(p.Choices, c => c.Kind == "retail" && !c.On && !c.Exists);
            var names = p.TimeMeasures.Select(x => x.Name).ToArray();
            Assert.Contains("Total Sales QTD", names); Assert.Contains("Total Sales vs Last Year %", names);
            Assert.DoesNotContain("Total Sales YTD", names);   // Sales YTD already does that on this calendar

            var r = await e.AddTimeMeasuresAsync(new[] { "Total Sales QTD" }, "agent");
            Assert.Equal("Total Sales QTD", Assert.Single(r.Created).Name);
            var m = (await e.GetModelObjectsAsync()).Measures.Single(x => x.Name == "Total Sales QTD");
            Assert.Equal("TOTALQTD ( [Total Sales], 'Calendar year' )", m.Expression);
            Assert.Equal("Time intelligence", m.DisplayFolder);
            Assert.DoesNotContain("Total Sales QTD", (await e.ProposeCalendarAsync(null, null, 0, null)).TimeMeasures.Select(x => x.Name));
            await e.UndoAsync("human");
            Assert.DoesNotContain((await e.GetModelObjectsAsync()).Measures, x => x.Name == "Total Sales QTD");
        }

        [Fact]
        public async Task A_retail_calendar_added_later_brings_the_week_measures()
        {
            using var e = await ClassicModelAsync(fiscalInstructions: false);
            await e.SetupCalendarsAsync(null, new[] { "year" }, 0, null, true, "agent");
            await e.SetupCalendarsAsync(null, new[] { "retail" }, 0, null, false, "agent");
            var cals = (await e.ListCalendarsAsync(null)).Calendars.Select(c => c.Name).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "Calendar year", "Retail 4-4-5" }, cals);
            var names = (await e.ProposeCalendarAsync(null, null, 0, null)).TimeMeasures.Select(x => x.Name).ToArray();
            Assert.Contains("Total Sales WTD", names); Assert.Contains("Total Sales Retail Period to Date", names);
        }

        [Fact]
        public async Task Setup_survives_a_save_and_reopen()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "semanticus-calplan-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var e = await ClassicModelAsync(proveNumbers: true))
                {
                    await e.SetupCalendarsAsync(null, null, 0, null, true, "agent");
                    await e.SaveAsync(dir, "TMDL", false, "human");
                }
                using var re = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                await re.OpenAsync(dir);
                Assert.Equal(2, (await re.ListCalendarsAsync(null)).Calendars.Length);
                Assert.Equal("set-up", (await re.ProposeCalendarAsync(null, null, 0, null)).State);
                Assert.Contains("Sales YTD=TOTALYTD ( [Total Sales], 'Calendar year' )", await ExpressionsAsync(re));
            }
            finally { try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
        }
    }
}
