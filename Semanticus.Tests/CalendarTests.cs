using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Calendar-based time intelligence ops (define/list/tag/delete + templates). The load-bearing invariants:
    /// (1) the CL-1701 gate is loud and instructive; (2) a calendar write is ONE undoable step on the shared
    /// timeline even though the pinned TOMWrapper doesn't wrap Calendar objects (the CalendarOps raw-TOM +
    /// TMDL-undo seam); (3) calendars survive a TMDL save → reopen round-trip — a silent drop here would be
    /// the partition-M deploy-drop bug all over again; (4) templates only generate ABSENT columns and their
    /// mappings/labels are deterministic.
    /// </summary>
    public sealed class CalendarTests
    {
        private static async Task<LocalEngine> FreshModelAsync(int cl = 1701)
        {
            var engine = new LocalEngine(new SessionManager());
            await engine.CreateModelAsync("CalTest", cl);
            return engine;
        }

        /// <summary>A bare table with a real DateTime data column to hang calendars on.</summary>
        private static async Task<string> AddDateTableAsync(LocalEngine engine, string name = "Dim Date")
        {
            var tref = await engine.CreateTableAsync(name, "agent");
            await engine.CreateColumnAsync(tref, "Date", "DateTime", "Date", "agent");
            await engine.CreateColumnAsync(tref, "Year", "Int64", "Year", "agent");
            await engine.CreateColumnAsync(tref, "IsHoliday", "Boolean", "IsHoliday", "agent");
            return tref;
        }

        [Fact]
        public async Task Define_below_CL_1701_is_refused_with_the_upgrade_pointer()
        {
            using var engine = await FreshModelAsync(1604);
            await AddDateTableAsync(engine);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.DefineCalendarAsync("Dim Date", "Gregorian",
                    new[] { new CalendarMappingSpec { Column = "Date", TimeUnit = "Date" } }, null, "agent"));
            Assert.Contains("1701", ex.Message);
            Assert.Contains("Raise the compatibility level", ex.Message);
            Assert.DoesNotContain("set_compatibility_level", ex.Message);

            var list = await engine.ListCalendarsAsync(null);
            Assert.False(list.CalendarsSupported);   // the read stays tolerant — it reports, never throws
            Assert.Contains("set_compatibility_level", list.Note);
        }

        [Fact]
        public async Task Define_list_tag_delete_round_trip()
        {
            using var engine = await FreshModelAsync();
            await AddDateTableAsync(engine);

            var def = await engine.DefineCalendarAsync("Dim Date", "Gregorian", new[]
            {
                new CalendarMappingSpec { Column = "Date", TimeUnit = "Date" },
                new CalendarMappingSpec { Column = "Year", TimeUnit = "Year" },
                new CalendarMappingSpec { Column = "IsHoliday", TimeUnit = "timeRelated" },
            }, "std calendar", "agent");
            Assert.Equal(3, def.Mappings.Length);

            var list = await engine.ListCalendarsAsync("Dim Date");
            var cal = Assert.Single(list.Calendars);
            Assert.Equal("Gregorian", cal.Name);
            Assert.Equal("std calendar", cal.Description);
            Assert.Contains(cal.Groups, g => g.TimeUnit == "Date" && g.PrimaryColumn == "Date");
            Assert.Contains(cal.Groups, g => g.TimeUnit == "Year" && g.PrimaryColumn == "Year");
            Assert.Contains(cal.Groups, g => g.TimeUnit == null && g.TimeRelatedColumns.Contains("IsHoliday"));

            // A duplicate name is refused loudly.
            var dup = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.DefineCalendarAsync("Dim Date", "Gregorian",
                    new[] { new CalendarMappingSpec { Column = "Date", TimeUnit = "Date" } }, null, "agent"));
            Assert.Contains("already has a calendar", dup.Message);

            // Associated-without-primary is an instructive error, not a guess.
            var noPrimary = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.TagCalendarColumnAsync("Dim Date", "Gregorian", "Year", "Month", associated: true, remove: false, "agent"));
            Assert.Contains("no primary", noPrimary.Message);

            // Tag an associated column onto an existing unit, then untag it.
            await engine.TagCalendarColumnAsync("Dim Date", "Gregorian", "IsHoliday", "Year", associated: true, remove: false, "agent");
            list = await engine.ListCalendarsAsync("Dim Date");
            Assert.Contains(list.Calendars[0].Groups, g => g.TimeUnit == "Year" && g.AssociatedColumns.Contains("IsHoliday"));
            await engine.TagCalendarColumnAsync("Dim Date", "Gregorian", "IsHoliday", null, associated: false, remove: true, "agent");
            list = await engine.ListCalendarsAsync("Dim Date");
            Assert.DoesNotContain(list.Calendars[0].Groups, g => g.TimeUnit == "Year" && g.AssociatedColumns.Contains("IsHoliday"));

            // Removing a primary that still has associates is refused (loud), then delete_calendar clears it all.
            await engine.TagCalendarColumnAsync("Dim Date", "Gregorian", "IsHoliday", "Year", associated: true, remove: false, "agent");
            var guarded = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.TagCalendarColumnAsync("Dim Date", "Gregorian", "Year", null, associated: false, remove: true, "agent"));
            Assert.Contains("associated columns", guarded.Message);

            var del = await engine.DeleteCalendarAsync("Dim Date", "Gregorian", "agent");
            Assert.True(del.Changed);
            Assert.Empty((await engine.ListCalendarsAsync(null)).Calendars);
        }

        [Fact]
        public async Task Define_is_one_undoable_step_on_the_shared_timeline()
        {
            using var engine = await FreshModelAsync();
            await AddDateTableAsync(engine);
            await engine.DefineCalendarAsync("Dim Date", "Gregorian",
                new[] { new CalendarMappingSpec { Column = "Date", TimeUnit = "Date" } }, null, "agent");
            Assert.Single((await engine.ListCalendarsAsync(null)).Calendars);

            var u = await engine.UndoAsync("human");   // the OTHER driver undoes the agent's calendar — one timeline
            Assert.Empty((await engine.ListCalendarsAsync(null)).Calendars);
            Assert.True(u.CanRedo);

            await engine.RedoAsync("human");
            var cal = Assert.Single((await engine.ListCalendarsAsync(null)).Calendars);
            Assert.Equal("Date", Assert.Single(cal.Groups, g => g.TimeUnit == "Date").PrimaryColumn);
        }

        [Fact]
        public async Task Calendars_survive_a_tmdl_save_and_reopen()
        {
            var dir = Path.Combine(Path.GetTempPath(), "semanticus-cal-save-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var engine = await FreshModelAsync())
                {
                    await AddDateTableAsync(engine);
                    await engine.DefineCalendarAsync("Dim Date", "Gregorian", new[]
                    {
                        new CalendarMappingSpec { Column = "Date", TimeUnit = "Date" },
                        new CalendarMappingSpec { Column = "Year", TimeUnit = "Year" },
                    }, null, "agent");
                    await engine.SaveAsync(dir, "TMDL");
                }
                using (var reopened = new LocalEngine(new SessionManager()))
                {
                    await reopened.OpenAsync(dir);
                    var cal = Assert.Single((await reopened.ListCalendarsAsync(null)).Calendars);
                    Assert.Equal("Gregorian", cal.Name);
                    Assert.Contains(cal.Groups, g => g.TimeUnit == "Date" && g.PrimaryColumn == "Date");
                    Assert.Contains(cal.Groups, g => g.TimeUnit == "Year" && g.PrimaryColumn == "Year");
                }
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            }
        }

        [Fact]
        public async Task Gregorian_template_on_a_fresh_model_creates_table_columns_and_calendar_and_undoes_as_one_step()
        {
            using var engine = await FreshModelAsync();
            var r = await engine.DefineCalendarFromTemplateAsync("gregorian", null, null, 7, null, null, null, "agent");

            Assert.Equal("Date", r.Table);
            Assert.Equal("Gregorian", r.Calendar);
            Assert.Contains("Created calculated table", r.Note);
            Assert.Contains(r.CreatedColumns, c => c.Name == "Year" && c.Expression.Contains("YEAR("));
            Assert.Contains(r.CreatedColumns, c => c.Name == "Month of Year");
            Assert.Contains(r.Mappings, m => m.Contains("→ Date"));
            Assert.Contains(r.Mappings, m => m.Contains("Month Number → MonthOfYear (associated)"));

            var cal = Assert.Single((await engine.ListCalendarsAsync(null)).Calendars);
            Assert.Contains(cal.Groups, g => g.TimeUnit == "MonthOfYear" && g.PrimaryColumn == "Month of Year" && g.AssociatedColumns.Contains("Month Number"));
            Assert.Contains(cal.Groups, g => g.TimeUnit == "DayOfWeek" && g.PrimaryColumn == "Day of Week");

            // The name-by-number visual sort pairing landed in the metadata (the TMDL is the ground truth).
            var tmdl = await engine.ScriptObjectsAsync(new[] { "table:Date" }, "TMDL");
            Assert.Contains("sortByColumn: 'Month Number'", tmdl);
            Assert.Contains("calendar Gregorian", tmdl);   // and the calendar block itself serializes on the table

            await engine.UndoAsync("agent");
            Assert.Empty((await engine.ListCalendarsAsync(null)).Calendars);
            Assert.DoesNotContain((await engine.GetModelGraphAsync()).Tables, t => t.Name == "Date");   // the table creation undid too
        }

        [Fact]
        public async Task Template_new_table_survives_opened_model_compatibility_upgrade_and_cross_driver_undo_redo()
        {
            using var engine = new LocalEngine(new SessionManager());
            await engine.OpenAsync(TestModels.FindBim());   // real saved CL-1200 model, not the fresh-model fast path
            await engine.SetCompatibilityLevelAsync(1701, "human");
            using var unrelated = await FreshModelAsync();   // move the vendored process-global handler off this session

            const string table = "Modern Calendar Regression";
            const string calendar = "Regression Gregorian";
            await engine.DefineCalendarFromTemplateAsync("gregorian", table, null, 7, null, null, calendar, "agent");
            Assert.Contains((await engine.ListCalendarsAsync(table)).Calendars, c => c.Name == calendar);

            await engine.UndoAsync("human");
            Assert.DoesNotContain((await engine.GetModelGraphAsync()).Tables, t => t.Name == table);
            await engine.RedoAsync("human");
            Assert.Contains((await engine.ListCalendarsAsync(table)).Calendars, c => c.Name == calendar);
        }

        [Fact]
        public async Task Template_new_table_dry_run_rolls_back_the_complete_table_and_calendar_batch()
        {
            using var engine = await FreshModelAsync();
            var report = await engine.DryRunOpAsync("define_calendar_from_template",
                "{\"template\":\"gregorian\",\"tableName\":\"Dry Calendar\"}");

            Assert.True(report.WouldSucceed);
            Assert.DoesNotContain((await engine.GetModelGraphAsync()).Tables, t => t.Name == "Dry Calendar");
            Assert.Empty((await engine.ListCalendarsAsync(null)).Calendars);
        }

        [Fact]
        public async Task Template_existing_table_undo_redo_survives_process_global_handler_drift()
        {
            using var engine = await FreshModelAsync();
            await AddDateTableAsync(engine);
            using var unrelated = await FreshModelAsync();

            await engine.DefineCalendarFromTemplateAsync("fiscal", "Dim Date", "Date", 7, null, null, null, "agent");
            Assert.Single((await engine.ListCalendarsAsync("Dim Date")).Calendars);
            await engine.UndoAsync("human");
            Assert.Empty((await engine.ListCalendarsAsync("Dim Date")).Calendars);
            await engine.RedoAsync("human");
            Assert.Single((await engine.ListCalendarsAsync("Dim Date")).Calendars);
        }

        [Fact]
        public async Task Fiscal_template_keeps_existing_columns_and_labels_by_ending_year()
        {
            using var engine = await FreshModelAsync();
            var tref = await AddDateTableAsync(engine);
            await engine.CreateCalculatedColumnAsync(tref, "Fiscal Year", "\"MY OWN\"", "agent");   // pre-existing — must be kept

            var r = await engine.DefineCalendarFromTemplateAsync("fiscal", "Dim Date", "Date", 7, null, null, null, "agent");
            Assert.Equal("Fiscal", r.Calendar);
            Assert.Contains(r.Skipped, s => s.Contains("Fiscal Year"));                       // not regenerated…
            Assert.DoesNotContain(r.CreatedColumns, c => c.Name == "Fiscal Year");
            Assert.Contains(r.CreatedColumns, c => c.Name == "Fiscal Month of Year");         // …the rest is
            Assert.Contains("ENDING year", r.Note);

            var cal = Assert.Single((await engine.ListCalendarsAsync("Dim Date")).Calendars, c => c.Name == "Fiscal");
            Assert.Contains(cal.Groups, g => g.TimeUnit == "Year" && g.PrimaryColumn == "Fiscal Year");   // mapped as-is
            Assert.Contains(cal.Groups, g => g.TimeUnit == "MonthOfYear" && g.PrimaryColumn == "Fiscal Month of Year");
        }

        [Fact]
        public async Task Week_based_templates_share_the_iso_scaffolding_so_they_coexist_on_one_table()
        {
            using var engine = await FreshModelAsync();
            await engine.DefineCalendarFromTemplateAsync("iso", "Date", null, 7, null, null, null, "agent");
            var r445 = await engine.DefineCalendarFromTemplateAsync("445", "Date", null, 7, null, null, null, "agent");
            var r13 = await engine.DefineCalendarFromTemplateAsync("13period", "Date", null, 7, null, null, null, "agent");

            // The ISO columns were created once by the first template and REUSED (skipped) by the others.
            Assert.Contains(r445.Skipped, s => s.Contains("ISO Year"));
            Assert.Contains(r13.Skipped, s => s.Contains("ISO Week of Year"));

            var cals = (await engine.ListCalendarsAsync("Date")).Calendars;
            Assert.Equal(3, cals.Length);
            Assert.All(new[] { "ISO", "4-4-5", "13-Period" }, n => Assert.Contains(cals, c => c.Name == n));
            // All three map the SAME physical ISO Year column as their Year.
            Assert.All(cals, c => Assert.Equal("ISO Year", Assert.Single(c.Groups, g => g.TimeUnit == "Year").PrimaryColumn));
        }

        [Fact]
        public async Task Unknown_template_and_bad_time_unit_are_instructive_errors()
        {
            using var engine = await FreshModelAsync();
            await AddDateTableAsync(engine);

            var badTemplate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.DefineCalendarFromTemplateAsync("julian", null, null, 7, null, null, null, "agent"));
            Assert.Contains("gregorian, fiscal, iso, 445, or 13period", badTemplate.Message);

            var badUnit = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.DefineCalendarAsync("Dim Date", "X",
                    new[] { new CalendarMappingSpec { Column = "Date", TimeUnit = "Fortnight" } }, null, "agent"));
            Assert.Contains("not a TimeUnit", badUnit.Message);
            Assert.Contains("timeRelated", badUnit.Message);
        }

        // ---- Readiness advisory (CAL-TI-NO-CALENDAR) + grounding (slice 2) ---------------------------
        // The advisory is presence design: dormant unless {CL>=1701, >=1 classic-TI measure, no calendars}; the
        // grounding must expose the model's calendars so an agent authors calendar-aware DAX instead of classic forms.

        private const string RuleId = "CAL-TI-NO-CALENDAR";
        // A classic time-intelligence measure (function with a calendar-aware overload). Stored verbatim offline
        // (no Verified Mode / live validation), so it need only carry the classic-TI token the scanner matches.
        private static Task AddClassicTiMeasureAsync(LocalEngine engine, string table = "Dim Date", string name = "YTD Year") =>
            engine.CreateMeasureAsync("table:" + table, name, $"TOTALYTD(SUM('{table}'[Year]), '{table}'[Date])", "agent");

        private static async Task<bool> FiresAsync(LocalEngine engine) =>
            (await engine.AiReadinessScanAsync()).Findings.Any(f => f.RuleId == RuleId);

        [Fact]
        public async Task Cal_advisory_dormant_below_cl_1701_even_with_classic_ti()
        {
            using var engine = await FreshModelAsync(1604);
            await AddDateTableAsync(engine);
            await AddClassicTiMeasureAsync(engine);
            Assert.False(await FiresAsync(engine));   // pre-1701 calendars are unavailable ⇒ no advisory
        }

        [Fact]
        public async Task Cal_advisory_dormant_when_no_classic_ti_measures()
        {
            using var engine = await FreshModelAsync();
            await AddDateTableAsync(engine);
            await engine.CreateMeasureAsync("table:Dim Date", "Row Count", "COUNTROWS('Dim Date')", "agent"); // no classic TI
            Assert.False(await FiresAsync(engine));
        }

        [Fact]
        public async Task Cal_advisory_dormant_when_calendars_exist()
        {
            using var engine = await FreshModelAsync();
            await AddDateTableAsync(engine);
            await AddClassicTiMeasureAsync(engine);
            await engine.DefineCalendarAsync("Dim Date", "Gregorian",
                new[] { new CalendarMappingSpec { Column = "Date", TimeUnit = "Date" } }, null, "agent");
            Assert.False(await FiresAsync(engine));   // already calendar-based ⇒ dormant (never an always-pass finding)
        }

        [Fact]
        public async Task Cal_advisory_fires_at_cl_1701_with_classic_ti_and_no_calendars()
        {
            using var engine = await FreshModelAsync();
            await AddDateTableAsync(engine);
            await AddClassicTiMeasureAsync(engine);
            var f = Assert.Single((await engine.AiReadinessScanAsync()).Findings.Where(x => x.RuleId == RuleId));
            Assert.Contains("define_calendar_from_template", f.Message);
            Assert.Contains("1", f.Message);   // names the count of classic-TI measures
        }

        [Fact]
        public async Task Grounding_surfaces_the_models_calendars_after_define()
        {
            using var engine = await FreshModelAsync();
            await AddDateTableAsync(engine);
            await AddClassicTiMeasureAsync(engine);

            // Before any calendar: no calendar grounding.
            var g0 = await engine.GetGroundingAsync("measure:Dim Date/YTD Year");
            Assert.Empty(g0.Calendars);

            await engine.DefineCalendarAsync("Dim Date", "Fiscal", new[]
            {
                new CalendarMappingSpec { Column = "Date", TimeUnit = "Date" },
                new CalendarMappingSpec { Column = "Year", TimeUnit = "Year" },
            }, null, "agent");

            // After: the measure's grounding (model-wide) carries the calendar name + column→TimeUnit map, so the
            // agent knows it can author TOTALYTD([…], 'Fiscal') instead of the classic form.
            var g1 = await engine.GetGroundingAsync("measure:Dim Date/YTD Year");
            var line = Assert.Single(g1.Calendars);
            Assert.Contains("Fiscal", line);
            Assert.Contains("Date→Date", line);
            Assert.Contains("Year→Year", line);
        }
    }
}
