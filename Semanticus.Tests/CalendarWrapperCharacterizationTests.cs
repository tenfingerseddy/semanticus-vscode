using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// CHARACTERIZATION (A0 deliverable 6): why did the product bypass the wrapper's OWN Calendar API and mutate
    /// raw TOM through <see cref="CalendarOps"/> instead? <see cref="CalendarOps"/> claims "the pinned wrapper
    /// predates and does not wrap" Calendar objects (LocalEngine.Calendars.cs) — but the pinned submodule DOES
    /// carry a full Calendar wrapper: <c>Calendar.CreateNew</c>, <c>Table.Calendars</c>, <c>Calendar.AddTimeUnit</c>,
    /// <c>TimeUnitColumnAssociation</c>/<c>TimeRelatedColumnGroup</c> with undo-tracked setters
    /// (external/TabularEditor/TOMWrapper/TOMWrapper/Calendar.cs + Base/WrapperBase.generated.cs §Calendar).
    ///
    /// These tests do NOT go through the product's calendar ops (that IS the bypass under question). They drive the
    /// WRAPPER Calendar API directly, on the engine's live single-writer session (<c>sm.Current</c>), and assert the
    /// ACTUAL behavior — knowledge is the deliverable, not a green/red verdict. They pin: (A) creation lands in
    /// <c>Table.Calendars</c> and in the same raw TOM the product's read path uses; (B) it rides the SHARED undo
    /// timeline as one step and the ObjectChanged firehose broadcasts the mapping edits; (C) time-unit + associated +
    /// time-related mappings author cleanly and read back through the engine's own <c>list_calendars</c>; (D/E) a
    /// wrapper-authored calendar survives a TMDL round-trip and serializes byte-identically (modulo lineage GUIDs)
    /// to the <see cref="CalendarOps"/> path. Assembly parallelism is already disabled (TestModels.cs, hole #4).
    /// </summary>
    public sealed class CalendarWrapperCharacterizationTests
    {
        private static async Task<(LocalEngine engine, SessionManager sm)> FreshAsync(int cl = 1701)
        {
            var sm = new SessionManager();
            var engine = new LocalEngine(sm, TestEntitlements.Pro);
            await engine.CreateModelAsync("CalWrap", cl);
            return (engine, sm);
        }

        /// <summary>A bare table with real columns to hang wrapper calendars on (engine ops — the calendar is
        /// authored later via the WRAPPER, which is what these tests characterize).</summary>
        private static async Task<string> AddDateTableAsync(LocalEngine engine, string name = "Dim Date")
        {
            var tref = await engine.CreateTableAsync(name, "agent");
            await engine.CreateColumnAsync(tref, "Date", "DateTime", "Date", "agent");
            await engine.CreateColumnAsync(tref, "Year", "Int64", "Year", "agent");
            await engine.CreateColumnAsync(tref, "IsHoliday", "Boolean", "IsHoliday", "agent");
            return tref;
        }

        /// <summary>Captures the coalesced ObjectChanged deltas each tracked commit broadcasts on the ChangeBus —
        /// the proof that a wrapper calendar edit reaches BOTH doors (dual-drive).</summary>
        private sealed class DeltaCapture : ISessionObserver
        {
            public readonly List<ChangeDelta> Deltas = new();
            public int Commits;
            public void BeforeCommit(Model model) { }
            public void AfterCommit(SessionCommit commit)
            {
                Commits++;
                if (commit.Deltas != null) Deltas.AddRange(commit.Deltas);
            }
        }

        private static string NormalizeLineageTags(string tmdl) =>
            Regex.Replace(tmdl ?? "", @"(lineageTag|sourceLineageTag):\s*\S+", "$1: <GUID>");

        // ----------------------------------------------------------------------------------------------------
        // (A) Creation via the wrapper factory lands the calendar where the product reads it.
        // ----------------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_wrapper_CreateNew_lands_a_calendar_in_Table_Calendars_and_the_raw_seam()
        {
            var (engine, sm) = await FreshAsync();
            using (engine)
            {
                await AddDateTableAsync(engine);

                // The wrapper's own factory — NOT CalendarOps. If governance blocked it (Power BI restricted mode)
                // or the type were unwrapped, this would throw; it does not, because a fresh in-memory model is
                // Unrestricted and Calendar IS wrapped in the pinned submodule.
                await sm.Current.MutateAsync("agent", "wrapper create calendar", m =>
                {
                    var t = m.Tables["Dim Date"];
                    Calendar.CreateNew(t, "WrapperGregorian");
                });

                // The wrapper collection sees it...
                var (wrapperCount, name, parentTable) = await sm.Current.ReadAsync(m =>
                {
                    var cal = m.Tables["Dim Date"].Calendars.Single();
                    return (m.Tables["Dim Date"].Calendars.Count, cal.Name, cal.Table?.Name);
                });
                Assert.Equal(1, wrapperCount);
                Assert.Equal("WrapperGregorian", name);
                Assert.Equal("Dim Date", parentTable);   // Table back-pointer resolves through the wrapper lookup

                // ...and so does the RAW TOM seam the product's read path uses (CalendarOps.Raw): the wrapper Add
                // wrote through to the very same Table.MetadataObject.Calendars — the wrapper is not a shadow graph.
                var rawCount = await sm.Current.ReadAsync(m => CalendarOps.Raw(m.Tables["Dim Date"]).Calendars.Count);
                Assert.Equal(1, rawCount);

                // ...so the engine's OWN read op returns the wrapper-authored calendar unchanged.
                var list = await engine.ListCalendarsAsync("Dim Date");
                Assert.Equal("WrapperGregorian", Assert.Single(list.Calendars).Name);
                // PROVES: the CalendarOps comment ("the pinned wrapper ... does not wrap Calendar objects") is false
                // for CREATION — Calendar.CreateNew works and is transparently visible to the raw-TOM read path.
            }
        }

        // ----------------------------------------------------------------------------------------------------
        // (B) The wrapper create is ONE step on the shared undo timeline and broadcasts on the firehose.
        // ----------------------------------------------------------------------------------------------------

        [Fact]
        public async Task B_wrapper_calendar_is_one_undoable_step_and_broadcasts_mapping_edits()
        {
            var (engine, sm) = await FreshAsync();
            using (engine)
            {
                await AddDateTableAsync(engine);
                var capture = new DeltaCapture();
                sm.Current.RegisterObserver(capture);

                // Calendar + two time-unit associations, all inside ONE engine mutation batch. AddTimeUnit opens its
                // OWN nested BeginUpdate/EndUpdate; TE2's UndoManager collapses nested batches to a single step.
                await sm.Current.MutateAsync("agent", "wrapper create calendar + mappings", m =>
                {
                    var t = m.Tables["Dim Date"];
                    var cal = Calendar.CreateNew(t, "WrapperGregorian");
                    cal.AddTimeUnit(TimeUnit.Date, t.Columns["Date"]);
                    cal.AddTimeUnit(TimeUnit.Year, t.Columns["Year"]);
                });

                var (calCount, unitCount) = await sm.Current.ReadAsync(m =>
                {
                    var raw = CalendarOps.Raw(m.Tables["Dim Date"]);
                    var cal = raw.Calendars.Single();
                    return (raw.Calendars.Count, cal.CalendarColumnGroups.OfType<TOM.TimeUnitColumnAssociation>().Count());
                });
                Assert.Equal(1, calCount);
                Assert.Equal(2, unitCount);

                // FIREHOSE: the tracked commit carried ObjectChanged deltas (broadcast to both doors). NOTE: a bare
                // collection Add (the Calendar / association itself) raises CollectionChanged, not ObjectChanged — true
                // of ALL wrapper object creation, not calendar-specific; the PrimaryColumn assignment inside AddTimeUnit
                // IS a tracked property edit, and that is what fires here.
                Assert.NotEmpty(capture.Deltas);

                // SHARED TIMELINE: the OTHER driver undoes the agent's calendar in one step (it is not on a private
                // TMDL-snapshot timeline — the wrapper's native UndoAddRemove/UndoPropertyChanged actions carry it).
                Assert.True(sm.Current.UndoStateNow().CanUndo);
                await engine.UndoAsync("human");
                Assert.Equal(0, await sm.Current.ReadAsync(m => CalendarOps.Raw(m.Tables["Dim Date"]).Calendars.Count));

                await engine.RedoAsync("human");
                var (redoCount, redoUnits) = await sm.Current.ReadAsync(m =>
                {
                    var cal = CalendarOps.Raw(m.Tables["Dim Date"]).Calendars.Single();
                    return (1, cal.CalendarColumnGroups.OfType<TOM.TimeUnitColumnAssociation>().Count());
                });
                Assert.Equal(1, redoCount);
                Assert.Equal(2, redoUnits);   // redo restored the calendar AND both mappings
                // PROVES: a wrapper-authored calendar is natively dual-drive + undoable. The bespoke TMDL-snapshot
                // undo in CalendarOps was NOT required to get calendars onto the shared timeline.
            }
        }

        // ----------------------------------------------------------------------------------------------------
        // (C) Time-unit, associated, and time-related mappings author through the wrapper and read back cleanly.
        // ----------------------------------------------------------------------------------------------------

        [Fact]
        public async Task C_wrapper_authors_primary_associated_and_time_related_mappings()
        {
            var (engine, sm) = await FreshAsync();
            using (engine)
            {
                await AddDateTableAsync(engine);

                await sm.Current.MutateAsync("agent", "wrapper author mappings", m =>
                {
                    var t = m.Tables["Dim Date"];
                    var cal = Calendar.CreateNew(t, "WrapperFiscal");
                    cal.AddTimeUnit(TimeUnit.Date, t.Columns["Date"]);
                    // Year primary + an ASSOCIATED column (the params overload authors the AssociatedColumns collection).
                    cal.AddTimeUnit(TimeUnit.Year, t.Columns["Year"], t.Columns["IsHoliday"]);
                    // A time-related bucket (the untagged group) authored via the wrapper's TimeRelatedColumnGroup.
                    var bucket = TimeRelatedColumnGroup.CreateNew(cal);
                    bucket.Columns.Add(t.Columns["IsHoliday"]);
                });

                // Read back through the engine's OWN list_calendars (the product read path, over CalendarOps.Raw):
                var cal = Assert.Single((await engine.ListCalendarsAsync("Dim Date")).Calendars);
                Assert.Equal("WrapperFiscal", cal.Name);
                Assert.Contains(cal.Groups, g => g.TimeUnit == "Date" && g.PrimaryColumn == "Date");
                Assert.Contains(cal.Groups, g => g.TimeUnit == "Year" && g.PrimaryColumn == "Year"
                                                 && g.AssociatedColumns.Contains("IsHoliday"));
                Assert.Contains(cal.Groups, g => g.TimeRelatedColumns.Contains("IsHoliday"));
                // PROVES: the wrapper authors the full CalendarColumnGroup surface (TimeUnitColumnAssociation with
                // primary + associated columns, and TimeRelatedColumnGroup) and the product reads every part of it.
            }
        }

        // ----------------------------------------------------------------------------------------------------
        // (D) A wrapper-authored calendar survives a TMDL save -> reopen.
        // ----------------------------------------------------------------------------------------------------

        [Fact]
        public async Task D_wrapper_authored_calendar_survives_a_tmdl_save_and_reopen()
        {
            var dir = Path.Combine(Path.GetTempPath(), "semanticus-calwrap-" + Guid.NewGuid().ToString("N"));
            try
            {
                var (engine, sm) = await FreshAsync();
                using (engine)
                {
                    await AddDateTableAsync(engine);
                    await sm.Current.MutateAsync("agent", "wrapper author + save", m =>
                    {
                        var t = m.Tables["Dim Date"];
                        var cal = Calendar.CreateNew(t, "WrapperGregorian");
                        cal.AddTimeUnit(TimeUnit.Date, t.Columns["Date"]);
                        cal.AddTimeUnit(TimeUnit.Year, t.Columns["Year"]);
                    });
                    await engine.SaveAsync(dir, "TMDL");
                }

                using (var reopened = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
                {
                    await reopened.OpenAsync(dir);
                    var cal = Assert.Single((await reopened.ListCalendarsAsync(null)).Calendars);
                    Assert.Equal("WrapperGregorian", cal.Name);
                    Assert.Contains(cal.Groups, g => g.TimeUnit == "Date" && g.PrimaryColumn == "Date");
                    Assert.Contains(cal.Groups, g => g.TimeUnit == "Year" && g.PrimaryColumn == "Year");
                }
                // PROVES: wrapper-authored calendars serialize and reload with no drop (the partition-M deploy-drop
                // failure mode does not recur through the wrapper path).
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
            }
        }

        // ----------------------------------------------------------------------------------------------------
        // (E) The wrapper path and the CalendarOps path serialize to the SAME TMDL (modulo lineage GUIDs).
        // ----------------------------------------------------------------------------------------------------

        [Fact]
        public async Task E_wrapper_and_calendarops_paths_serialize_to_identical_tmdl()
        {
            // Two models built identically (engine ops), then the SAME calendar authored two different ways. The
            // handler Singleton is process-global and wrapper ctors bind to it, so the two engines are built and used
            // strictly sequentially, with no overlap.
            string wrapperTmdl, opsTmdl;

            var (e1, sm1) = await FreshAsync();
            using (e1)
            {
                await AddDateTableAsync(e1);
                await sm1.Current.MutateAsync("agent", "wrapper author", m =>
                {
                    var t = m.Tables["Dim Date"];
                    var cal = Calendar.CreateNew(t, "Gregorian");
                    cal.AddTimeUnit(TimeUnit.Date, t.Columns["Date"]);
                    cal.AddTimeUnit(TimeUnit.Year, t.Columns["Year"]);
                });
                wrapperTmdl = await e1.ScriptObjectsAsync(new[] { "table:Dim Date" }, "TMDL");
            }

            var (e2, sm2) = await FreshAsync();
            using (e2)
            {
                await AddDateTableAsync(e2);
                await e2.DefineCalendarAsync("Dim Date", "Gregorian", new[]
                {
                    new CalendarMappingSpec { Column = "Date", TimeUnit = "Date" },
                    new CalendarMappingSpec { Column = "Year", TimeUnit = "Year" },
                }, null, "agent");
                opsTmdl = await e2.ScriptObjectsAsync(new[] { "table:Dim Date" }, "TMDL");
            }

            // Both serialize the SAME calendar block. LineageTags are fresh GUIDs on each path, so normalize them out;
            // any REMAINING difference would be a real structural divergence between the two authoring routes.
            var w = NormalizeLineageTags(wrapperTmdl);
            var o = NormalizeLineageTags(opsTmdl);
            Assert.Contains("calendar Gregorian", w);   // the wrapper path emits the calendar block at all
            Assert.Equal(o, w);
            // PROVES: the two routes produce byte-identical TMDL. The CalendarOps bypass buys NO serialization
            // fidelity the wrapper API lacks — the on-disk result is the same metadata.
        }
    }
}
