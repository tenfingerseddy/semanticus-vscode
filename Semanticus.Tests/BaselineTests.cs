using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Value-capture-at-edit-start (capture_baseline / compare_baseline) — the RESTRUCTURE pipeline's
    /// load-bearing primitive. Offline contract pinned here: FREE on both ops (Kane 2026-07-07 — the
    /// manual capture/compare safety net must never be gated; only ambient auto-capture + blame_value
    /// are Pro); structured refusals (no-connection / not-found), never a crash; blast-radius target
    /// selection is deterministic (self-inclusion for a measure root, unresolvable ref throws, capped
    /// overflow is REPORTED); the pure diff uses equivalence semantics (numeric tolerance, blank ≠ 0,
    /// a context present on one side only IS a moved number) and discloses truncated coverage. The live
    /// capture → edit → compare loop is exercised against a real XMLA endpoint, not here.
    /// </summary>
    public sealed class BaselineTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static async Task<LocalEngine> OpenAsync(bool pro = true)
        {
            var e = new LocalEngine(new SessionManager(), new Fake(pro));
            await e.OpenAsync(TestModels.FindBim());
            return e;
        }

        // ---- free tier + structured refusals ----

        // Pins the 2026-07-07 gate line: the MANUAL safety net is free. A free-tier capture/compare must
        // never throw an EntitlementException — offline it reaches the honest structured refusal instead
        // (no-connection / not-found). If this test starts failing with a gate throw, someone re-locked
        // the pre-2026-07-07 bug.
        [Fact]
        public async Task Both_ops_are_free_and_refuse_honestly_offline()
        {
            using var e = await OpenAsync(pro: false);
            var cap = await e.CaptureBaselineAsync("measure:Date/Days In Current Quarter", null, null, true, 25, 2000, null, "human");
            Assert.Equal("no-connection", cap.Status);
            var cmp = await e.CompareBaselineAsync(null, null, "human");
            Assert.Equal("not-found", cmp.Status);
        }

        // The FULL 2026-07-07 gate line in one place: manual capture/compare = FREE; the automatic side
        // (ambient vital-signs capture + blame_value + list_value_history, feature #3) = Pro with a SOFT
        // gate — free callers get Status="pro" + a plain invitation, never an EntitlementException.
        // Deep coverage lives in ValueBlameTests; this pins the tier split next to the free half it names.
        [Fact]
        public async Task Automatic_what_moved_side_is_pro_soft_while_manual_stays_free()
        {
            using var e = await OpenAsync(pro: false);
            var blame = await e.BlameValueAsync("measure:Date/Days In Current Quarter", null, null, "human");
            Assert.Equal("pro", blame.Status);
            Assert.Contains("capture a baseline", blame.Note);   // the invitation names the free manual path
            var hist = await e.ListValueHistoryAsync("measure:Date/Days In Current Quarter", null);
            Assert.Equal("pro", hist.Status);
        }

        [Fact]
        public async Task Offline_capture_refuses_with_no_connection_not_a_crash()
        {
            using var e = await OpenAsync();
            var r = await e.CaptureBaselineAsync("measure:Date/Days In Current Quarter", new[] { "'Date'[Fiscal Year]" }, null, true, 25, 2000, null, "human");
            Assert.Equal("no-connection", r.Status);
        }

        [Fact]
        public async Task Compare_with_no_captures_is_not_found()
        {
            using var e = await OpenAsync();
            var r = await e.CompareBaselineAsync(null, null, "human");
            Assert.Equal("not-found", r.Status);   // store checked before the live check — the message tells you what to run
        }

        // ---- blast-radius target selection (offline, real model) ----

        [Fact]
        public async Task Measure_root_includes_itself_first()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(true));
            await e.OpenAsync(TestModels.FindBim());
            var (targets, skipped) = await sm.Current.ReadAsync(m => Baseline.Targets(m, "measure:Date/Days In Current Quarter", includeDependents: false, maxMeasures: 25));
            Assert.Single(targets);
            Assert.Equal("Days In Current Quarter", targets[0].Name);
            Assert.Empty(skipped);
        }

        [Fact]
        public async Task Overflow_beyond_the_cap_is_reported_not_silent()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(true));
            await e.OpenAsync(TestModels.FindBim());
            // 'Date'[Date] feeds the date-intelligence measures in AdventureWorks — expect several dependents.
            var (all, _) = await sm.Current.ReadAsync(m => Baseline.Targets(m, "column:Date/Date", includeDependents: true, maxMeasures: 0));
            if (all.Count < 2) return;   // fixture-shape guard: the cap contract needs >=2 downstream measures
            var (capped, skipped) = await sm.Current.ReadAsync(m => Baseline.Targets(m, "column:Date/Date", includeDependents: true, maxMeasures: 1));
            Assert.Single(capped);
            Assert.Equal(all.Count - 1, skipped.Count);
            Assert.All(skipped, sk => Assert.Contains("cap", sk));
        }

        [Fact]
        public async Task Unresolvable_ref_throws_the_standard_error()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(true));
            await e.OpenAsync(TestModels.FindBim());
            await Assert.ThrowsAnyAsync<Exception>(() => sm.Current.ReadAsync(m => Baseline.Targets(m, "measure:Nope/Missing", true, 25)));
        }

        [Fact]
        public async Task Captured_identity_follows_a_rename_and_rejects_an_impostor_at_the_old_name()
        {
            var sm = new SessionManager();
            using var e = new LocalEngine(sm, new Fake(true));
            await e.OpenAsync(TestModels.FindBim());
            await e.SetCompatibilityLevelAsync(1600, "human");
            var target = (await e.ListMeasuresAsync()).First();
            var tag = await sm.Current.ReadAsync(m => (ObjectRefs.Resolve(m, target.Ref) as TabularEditor.TOMWrapper.Measure)?.LineageTag);
            if (string.IsNullOrWhiteSpace(tag))
            {
                tag = "baseline-rename-" + Guid.NewGuid().ToString("N");
                await sm.Current.MutateAsync("human", "seed test identity", m =>
                    ((TabularEditor.TOMWrapper.Measure)ObjectRefs.Resolve(m, target.Ref)).LineageTag = tag);
            }
            var captured = new BaselineEntryState { Ref = target.Ref, Name = target.Name, LineageTag = tag };
            var renamed = target.Name + " Renamed";

            await e.RenameObjectAsync(target.Ref, renamed, "human");
            var rest = target.Ref.Substring("measure:".Length);
            var tableRef = "table:" + rest.Substring(0, rest.LastIndexOf('/'));
            await e.CreateMeasureAsync(tableRef, target.Name, "0", "human");   // an impostor now owns the old name

            var binding = await sm.Current.ReadAsync(m => Baseline.ResolveMeasure(m, captured));
            Assert.Null(binding.Error);
            Assert.NotNull(binding.Measure);
            Assert.Equal(renamed, binding.Measure.Name);                       // tag wins over the old-name impostor
            Assert.Equal(tag, binding.Measure.LineageTag);
        }

        // ---- the pure diff (equivalence semantics) ----

        private static BaselineEntryState Entry(Dictionary<string, object> rows, bool truncated = false)
            => new BaselineEntryState { Ref = "measure:T/M", Name = "M", Rows = rows, Truncated = truncated };

        [Fact]
        public void Identical_rows_are_unchanged()
        {
            var d = Baseline.Diff(Entry(new Dictionary<string, object> { ["Y=2024"] = 10.0, ["Y=2025"] = null }),
                                  new Dictionary<string, object> { ["Y=2024"] = 10.0, ["Y=2025"] = null }, false);
            Assert.Equal("unchanged", d.Verdict);
            Assert.Equal(2, d.RowsCompared);
            Assert.Equal(0, d.MismatchCount);
            Assert.False(d.Truncated);
        }

        [Fact]
        public void Float_noise_within_tolerance_is_unchanged()
        {
            var d = Baseline.Diff(Entry(new Dictionary<string, object> { ["Y=2024"] = 1234.5678 }),
                                  new Dictionary<string, object> { ["Y=2024"] = 1234.5678 * (1 + 1e-9) }, false);
            Assert.Equal("unchanged", d.Verdict);
        }

        [Fact]
        public void A_moved_value_carries_the_context_and_both_values()
        {
            var d = Baseline.Diff(Entry(new Dictionary<string, object> { ["Y=2024"] = 10.0 }),
                                  new Dictionary<string, object> { ["Y=2024"] = 12.0 }, false);
            Assert.Equal("moved", d.Verdict);
            var m = d.Mismatches.Single();
            Assert.Equal("Y=2024", m.Context);
            Assert.Equal("10", m.ValueA);
            Assert.Equal("12", m.ValueB);
        }

        [Fact]
        public void Blank_to_value_is_moved_not_equal()
        {
            var d = Baseline.Diff(Entry(new Dictionary<string, object> { ["Y=2024"] = null }),
                                  new Dictionary<string, object> { ["Y=2024"] = 0.0 }, false);
            Assert.Equal("moved", d.Verdict);   // BLANK ≠ 0 — the classic silent regression
            Assert.Equal("(blank)", d.Mismatches.Single().ValueA);
        }

        [Fact]
        public void A_context_present_on_only_one_side_is_a_moved_number()
        {
            var d = Baseline.Diff(Entry(new Dictionary<string, object> { ["Y=2024"] = 10.0, ["Y=2025"] = 5.0 }),
                                  new Dictionary<string, object> { ["Y=2024"] = 10.0 }, false);
            Assert.Equal("moved", d.Verdict);
            var m = d.Mismatches.Single();
            Assert.Equal("Y=2025", m.Context);
            Assert.Equal("(no row)", m.ValueB);   // the row vanished — that IS an impact
        }

        [Fact]
        public void Truncated_coverage_is_disclosed_even_when_nothing_moved()
        {
            var d = Baseline.Diff(Entry(new Dictionary<string, object> { ["Y=2024"] = 10.0 }, truncated: true),
                                  new Dictionary<string, object> { ["Y=2024"] = 10.0 }, false);
            Assert.Equal("unchanged", d.Verdict);
            Assert.True(d.Truncated);
            Assert.Contains("not proof", d.Note);
        }

        // ---- helpers + store ----

        [Fact]
        public void Measure_ref_expr_escapes_brackets()
        {
            Assert.Equal("[Total Sales]", Baseline.MeasureRefExpr("Total Sales"));
            Assert.Equal("[A]]B]", Baseline.MeasureRefExpr("A]B"));
        }

        [Fact]
        public void Key_rows_use_the_axis_columns_and_grand_total_key()
        {
            var rs = new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "Date[Year]" }, new ColumnDef { Name = "[v]" }, new ColumnDef { Name = "[__present]" } },
                Rows = new[] { new object[] { 2024, 10.0, 1 }, new object[] { 2025, null, 1 } },
            };
            var keyed = Baseline.KeyRows(rs, 1);
            Assert.Equal(10.0, keyed["Date[Year]=2024"]);
            Assert.Null(keyed["Date[Year]=2025"]);

            var grand = new ResultSet { Columns = new[] { new ColumnDef { Name = "[v]" }, new ColumnDef { Name = "[__present]" } }, Rows = new[] { new object[] { 42.0, 1 } } };
            Assert.Equal(42.0, Baseline.KeyRows(grand, 0)["(model context)"]);
        }

        [Fact]
        public void Store_returns_latest_by_default_and_reports_the_lru_drop()
        {
            var store = new BaselineStore();
            for (var i = 0; i < BaselineStore.MaxHeld; i++)
                Assert.Null(store.Add(new BaselineState { Id = "bl-" + i }));
            Assert.Equal("bl-" + (BaselineStore.MaxHeld - 1), store.Get(null).Id);   // latest
            Assert.Equal("bl-2", store.Get("bl-2").Id);                              // by id
            Assert.Equal("bl-0", store.Add(new BaselineState { Id = "bl-new" }));    // the drop is RETURNED, not silent
            Assert.Null(store.Get("bl-0"));
        }
    }
}
