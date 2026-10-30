using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper.Utils;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// The Power BI service refuses a model in which one column is "associated" by the same calendar more than once: as a primary,
    /// an associated column, a time-related column, or the sort-by column of a primary (found by the live calendar test, 21 Oct:
    /// setup_calendars wrote Month Number both as an associated column and as the sort-by of Month). Every calendar Semanticus
    /// writes uses a column once, and the engine refuses, in plain words, to write one that does not.
    /// </summary>
    public sealed class CalendarAssociationTests
    {
        // The rule, written out here on purpose and read straight from the TOM the service reads. It is NOT the product's own check.
        private static List<string> DoubleUses(TOM.Table t)
        {
            var bad = new List<string>();
            foreach (var cal in t.Calendars)
            {
                var uses = new List<string>();
                foreach (var g in cal.CalendarColumnGroups)
                {
                    if (g is TOM.TimeUnitColumnAssociation a)
                    {
                        if (a.PrimaryColumn != null) uses.Add(a.PrimaryColumn.Name);
                        uses.AddRange(a.AssociatedColumns.Select(c => c.Name));
                    }
                    else if (g is TOM.TimeRelatedColumnGroup r) uses.AddRange(r.Columns.Select(c => c.Name));
                }
                uses.AddRange(cal.CalendarColumnGroups.OfType<TOM.TimeUnitColumnAssociation>()
                    .Select(a => a.PrimaryColumn?.SortByColumn?.Name).Where(n => n != null).Distinct());
                bad.AddRange(uses.GroupBy(n => n).Where(x => x.Count() > 1).Select(x => cal.Name + ": " + x.Key));
            }
            return bad;
        }

        private static async Task<(int calendars, string[] doubles)> ReadAsync(SessionManager sm)
            => await sm.Current.ReadAsync(m =>
            {
                var all = CalendarOps.Enumerate(m).Select(x => x.Table).Distinct().ToArray();
                return (CalendarOps.Enumerate(m).Count(), all.SelectMany(t => DoubleUses(CalendarOps.Raw(t))).ToArray());
            });

        private static async Task<(LocalEngine e, SessionManager sm)> ClassicAsync(int cl = 1604)
        {
            var sm = new SessionManager();
            var e = new LocalEngine(sm, TestEntitlements.Pro);
            await e.CreateModelAsync("Contoso", cl);
            var date = await e.CreateTableAsync("Date", "agent");
            foreach (var (n, t) in new[] { ("Date", "DateTime"), ("Year", "Int64"), ("Quarter", "String"), ("Year Month", "String"), ("Month Name", "String"), ("Month Number", "Int64"),
                                           ("Day of Week", "String"), ("Day of Week Number", "Int64"), ("Is Working Day", "Boolean"), ("Holiday Name", "String") })
                await e.CreateColumnAsync(date, n, t, n, "agent");
            var sales = await e.CreateTableAsync("Sales", "agent");
            await e.CreateColumnAsync(sales, "Order Date", "DateTime", "Order Date", "agent");
            await e.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "agent");
            await e.CreateRelationshipAsync("column:Sales/Order Date", "column:Date/Date", null, null, "agent");
            await e.MarkDateTableAsync(date, "Date", "agent");
            await e.CreateMeasureAsync(sales, "Total Sales", "SUM ( Sales[Amount] )", "agent");
            return (e, sm);
        }

        // ---- every template -------------------------------------------------------------------------------

        [Theory]
        [InlineData("gregorian")] [InlineData("fiscal")] [InlineData("iso")] [InlineData("445")] [InlineData("13period")]
        public async Task A_template_calendar_uses_no_column_more_than_once(string template)
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, TestEntitlements.Pro);
            await e.CreateModelAsync("T", 1701);
            await e.DefineCalendarFromTemplateAsync(template, null, null, 7, null, null, null, "agent");
            var (calendars, doubles) = await ReadAsync(sm);
            Assert.Equal(1, calendars);
            Assert.Empty(doubles);
        }

        [Fact]
        public async Task The_gregorian_template_keeps_the_sort_by_and_does_not_also_associate_the_sort_by_column()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, TestEntitlements.Pro);
            await e.CreateModelAsync("T", 1701);
            await e.DefineCalendarFromTemplateAsync("gregorian", null, null, 7, null, null, null, "agent");
            var tmdl = await e.ScriptObjectsAsync(new[] { "table:Date" }, "TMDL");
            Assert.Contains("sortByColumn: 'Month Number'", tmdl);
            Assert.Contains("sortByColumn: 'Day of Week Number'", tmdl);
            Assert.DoesNotContain("associatedColumn: 'Month Number'", tmdl);
            Assert.DoesNotContain("associatedColumn: 'Day of Week Number'", tmdl);
        }

        // ---- every calendar kind setup_calendars writes -----------------------------------------------------

        [Theory]
        [InlineData("year")] [InlineData("fiscal")] [InlineData("retail")] [InlineData("iso")] [InlineData("p13")]
        public async Task Setup_writes_each_kind_with_no_column_used_more_than_once(string kind)
        {
            var (e, sm) = await ClassicAsync();
            using (e)
            {
                // a Date table that already has Month Number (the live test's shape), then a fresh one that has to add it
                await e.SetupCalendarsAsync(null, new[] { "year", kind }.Distinct().ToArray(), 0, null, false, "agent");
                var (calendars, doubles) = await ReadAsync(sm);
                Assert.True(calendars >= 1);
                Assert.Empty(doubles);
            }
        }

        [Fact]
        public async Task Setup_on_a_model_with_no_Date_table_writes_calendars_with_no_column_used_twice()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, TestEntitlements.Pro);
            await e.CreateModelAsync("NoDate", 1604);
            var sales = await e.CreateTableAsync("Sales", "agent");
            await e.CreateColumnAsync(sales, "Order Date", "DateTime", "Order Date", "agent");
            await e.CreateColumnAsync(sales, "Amount", "Decimal", "Amount", "agent");
            await e.CreateMeasureAsync(sales, "Total Sales", "SUM ( Sales[Amount] )", "agent");
            await e.SetupCalendarsAsync(null, new[] { "year", "fiscal", "iso", "retail", "p13" }, 0, null, false, "agent");
            var (calendars, doubles) = await ReadAsync(sm);
            Assert.Equal(5, calendars);
            Assert.Empty(doubles);
            var tmdl = await e.ScriptObjectsAsync(new[] { "table:Date" }, "TMDL");
            Assert.Contains("sortByColumn: 'Month Number'", tmdl);
            Assert.DoesNotContain("associatedColumn: 'Month Number'", tmdl);
        }

        [Fact]
        public async Task Setup_of_the_live_test_set_is_one_undo_step_and_comes_back_whole_on_redo()
        {
            var (e, sm) = await ClassicAsync();
            using (e)
            {
                await e.SetupCalendarsAsync(null, new[] { "year", "fiscal", "iso" }, 0, null, false, "agent");
                var first = await ReadAsync(sm);
                Assert.Equal(3, first.calendars);
                Assert.Empty(first.doubles);

                await e.UndoAsync("human");   // ONE step takes the version, the columns and all three calendars back
                Assert.Equal(0, (await ReadAsync(sm)).calendars);
                Assert.False((await e.ListCalendarsAsync(null)).CalendarsSupported);

                await e.RedoAsync("human");
                var (calendars, doubles) = await ReadAsync(sm);
                Assert.Equal(3, calendars);
                Assert.Empty(doubles);
            }
        }

        // ---- the guard for any other path ------------------------------------------------------------------

        [Fact]
        public async Task Tagging_the_sort_by_column_of_a_primary_as_associated_is_refused_in_plain_words_and_changes_nothing()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, TestEntitlements.Pro);
            await e.CreateModelAsync("T", 1701);
            await e.DefineCalendarFromTemplateAsync("gregorian", null, null, 7, null, null, null, "agent");
            var before = await e.ScriptObjectsAsync(new[] { "table:Date" }, "TMDL");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                e.TagCalendarColumnAsync("Date", "Gregorian", "Month Number", "MonthOfYear", true, false, "agent"));

            Assert.Contains("Month Number", ex.Message);
            Assert.Contains("Gregorian", ex.Message);
            Assert.Contains("more than once", ex.Message);
            Assert.Equal(before, await e.ScriptObjectsAsync(new[] { "table:Date" }, "TMDL"));
            await e.UndoAsync("human");   // the refused edit left no undo step behind: this undoes the template itself
            Assert.Empty((await e.ListCalendarsAsync(null)).Calendars);
        }

        [Fact]
        public async Task A_column_used_as_two_levels_or_as_a_level_and_a_time_related_column_is_refused()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, TestEntitlements.Pro);
            await e.CreateModelAsync("T", 1701);
            var t = await e.CreateTableAsync("Dim Date", "agent");
            foreach (var n in new[] { "Date", "Year", "Flag" })
                await e.CreateColumnAsync(t, n, n == "Date" ? "DateTime" : "Int64", n, "agent");

            var twice = await Assert.ThrowsAsync<InvalidOperationException>(() => e.DefineCalendarAsync("Dim Date", "C1", new[]
            {
                new CalendarMappingSpec { Column = "Date", TimeUnit = "Date" },
                new CalendarMappingSpec { Column = "Year", TimeUnit = "Year" },
                new CalendarMappingSpec { Column = "Year", TimeUnit = "Quarter" },
            }, null, "agent"));
            Assert.Contains("more than once", twice.Message);

            var mixed = await Assert.ThrowsAsync<InvalidOperationException>(() => e.DefineCalendarAsync("Dim Date", "C2", new[]
            {
                new CalendarMappingSpec { Column = "Date", TimeUnit = "Date" },
                new CalendarMappingSpec { Column = "Year", TimeUnit = "Year" },
                new CalendarMappingSpec { Column = "Year", TimeUnit = "timeRelated" },
            }, null, "agent"));
            Assert.Contains("Year", mixed.Message);
            Assert.Equal(0, (await ReadAsync(sm)).calendars);
        }

        [Fact]
        public async Task An_associated_column_with_no_sort_by_on_its_primary_is_still_allowed()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, TestEntitlements.Pro);
            await e.CreateModelAsync("T", 1701);
            var t = await e.CreateTableAsync("Dim Date", "agent");
            foreach (var (n, ty) in new[] { ("Date", "DateTime"), ("Month Name", "String"), ("Month Number", "Int64") })
                await e.CreateColumnAsync(t, n, ty, n, "agent");
            await e.DefineCalendarAsync("Dim Date", "Plain", new[]
            {
                new CalendarMappingSpec { Column = "Date", TimeUnit = "Date" },
                new CalendarMappingSpec { Column = "Month Name", TimeUnit = "MonthOfYear" },
                new CalendarMappingSpec { Column = "Month Number", TimeUnit = "MonthOfYear", Associated = true },
            }, null, "agent");
            var (calendars, doubles) = await ReadAsync(sm);
            Assert.Equal(1, calendars);
            Assert.Empty(doubles);
        }
    }
}
