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
    /// The universal dry-run contract (docs/harness-engineering.md §4). The load-bearing invariants: a rehearsal
    /// applies through the REAL op path but leaves NOTHING — no mutation, no undo entry, no revision bump, no
    /// broadcast, no audit record — while returning the exact would-be deltas + the op's own result; a rehearsal
    /// that finds the op WOULD fail is still a successful dry-run (carrying the op's teaching error); denied
    /// families refuse with a recovery-teaching message; the scope never leaks (the same op applied for real after
    /// a rehearsal still works and still broadcasts); and even the raw-TOM/TMDL custom undo batch (calendars)
    /// rolls back cleanly inside the scope.
    /// </summary>
    public sealed class DryRunTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static async Task<(LocalEngine engine, SessionManager sm)> FreshAsync(bool pro = false, int cl = 1701)
        {
            var sm = new SessionManager();
            var engine = new LocalEngine(sm, new Fake(pro));
            await engine.CreateModelAsync("DryRunTest", cl);
            return (engine, sm);
        }

        private static async Task<string> AddFactsAsync(LocalEngine engine)
        {
            var t = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            return t;
        }

        private static Task<UndoState> UndoStateAsync(SessionManager sm) =>
            sm.Current.ReadAsync(_ => sm.Current.UndoStateNow());

        // 1) The headline: a rehearsed create_measure yields deltas + the would-be ref, and leaves the model,
        //    the undo stack, and the revision exactly as they were.
        [Fact]
        public async Task Dry_run_create_measure_rehearses_without_touching_the_model()
        {
            var (engine, sm) = await FreshAsync();
            await AddFactsAsync(engine);
            var revBefore = sm.Current.Revision;
            var undoBefore = await UndoStateAsync(sm);

            var rpt = await engine.DryRunOpAsync("create_measure",
                "{\"tableRef\":\"table:Facts\",\"name\":\"Margin\",\"expression\":\"SUM(Facts[Amount])\"}");

            Assert.True(rpt.WouldSucceed);
            Assert.Null(rpt.Error);
            Assert.NotEmpty(rpt.Deltas);                                   // the would-be change set
            Assert.NotEmpty(rpt.Mutations);                               // the mutation label was rehearsed
            Assert.Contains("Margin", rpt.Result);                        // the op's own return value (the new ref)
            Assert.Contains("Rehearsal only", rpt.Note);

            Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "Margin");   // nothing created
            Assert.Equal(revBefore, sm.Current.Revision);                 // no revision bump
            var undoAfter = await UndoStateAsync(sm);
            Assert.Equal(undoBefore.CanUndo, undoAfter.CanUndo);          // no undo entry left behind
        }

        // 2) No broadcast during a dry-run (the ChangeBus is silent — which also means the ExperienceTee, which
        //    persists off bus broadcasts, records nothing: verified, not assumed).
        [Fact]
        public async Task Dry_run_publishes_no_change_notification()
        {
            var (engine, sm) = await FreshAsync();
            await AddFactsAsync(engine);

            var seen = new List<ChangeNotification>();
            Action<ChangeNotification> handler = n => seen.Add(n);
            sm.Bus.Changed += handler;
            try
            {
                await engine.DryRunOpAsync("create_measure",
                    "{\"tableRef\":\"table:Facts\",\"name\":\"Ghost\",\"expression\":\"1\"}");
                Assert.Empty(seen);   // no model/didChange — no tee, no stale-door signal
            }
            finally { sm.Bus.Changed -= handler; }
        }

        // 3) A rehearsal that finds the op would FAIL is a SUCCESSFUL dry-run: WouldSucceed=false, the op's own
        //    teaching text in Error, model untouched.
        [Fact]
        public async Task Dry_run_of_a_failing_op_reports_the_error_and_leaves_the_model_untouched()
        {
            var (engine, sm) = await FreshAsync();
            await AddFactsAsync(engine);
            var revBefore = sm.Current.Revision;

            var rpt = await engine.DryRunOpAsync("create_measure",
                "{\"tableRef\":\"table:NoSuch\",\"name\":\"X\",\"expression\":\"1\"}");

            Assert.False(rpt.WouldSucceed);
            Assert.Contains("not a table", rpt.Error);      // CreateMeasureAsync's own teaching text
            Assert.Equal(revBefore, sm.Current.Revision);
        }

        // 4) set_description on an existing object — the deltas name the object ref + the changed property.
        [Fact]
        public async Task Dry_run_set_description_deltas_name_the_object_and_property()
        {
            var (engine, sm) = await FreshAsync();
            var t = await AddFactsAsync(engine);
            var mref = await engine.CreateMeasureAsync(t, "Total", "1", "human");

            var rpt = await engine.DryRunOpAsync("set_description",
                "{\"objRef\":\"" + mref + "\",\"text\":\"Sum of amount\"}");

            Assert.True(rpt.WouldSucceed);
            Assert.Contains(rpt.Deltas, d => d.Ref == mref);
            Assert.Contains(rpt.Deltas, d => d.Props != null && d.Props.Contains("Description"));
            // Exactly ONE Description delta: the snapshot is taken BEFORE the rollback, so the revert (which
            // re-fires ObjectChanged on the way back) must never double-count into the report.
            Assert.Single(rpt.Deltas, d => d.Props != null && d.Props.Contains("Description"));
            // The real object still has no description (rolled back).
            var obj = await engine.GetObjectAsync(mref);
            Assert.False(obj.Properties.TryGetValue("Description", out var desc) && !string.IsNullOrEmpty(desc?.ToString()));
        }

        // 5) No scope leak: the SAME op applied for real right after a rehearsal actually mutates.
        [Fact]
        public async Task Dry_run_does_not_leak_the_scope_to_the_next_real_op()
        {
            var (engine, sm) = await FreshAsync();
            await AddFactsAsync(engine);

            await engine.DryRunOpAsync("create_measure",
                "{\"tableRef\":\"table:Facts\",\"name\":\"Margin\",\"expression\":\"1\"}");
            Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "Margin");

            var mref = await engine.CreateMeasureAsync("table:Facts", "Margin", "1", "human");   // now for REAL
            Assert.Contains(await engine.ListMeasuresAsync(), m => m.Name == "Margin");
            Assert.NotNull(mref);
        }

        // 6) Audit suppression: with Verified Mode ON, a rehearsed update_measure leaves the audit chain unchanged
        //    (a rolled-back rehearsal must never mint an append-only record).
        [Fact]
        public async Task Dry_run_under_verified_mode_records_no_audit_edit()
        {
            var (engine, sm) = await FreshAsync(pro: true);
            var t = await AddFactsAsync(engine);
            var mref = await engine.CreateMeasureAsync(t, "Total", "1", "human");   // created BEFORE verified mode → no record
            await engine.SetVerifiedModeAsync(true, "human");
            Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);          // baseline: clean chain

            var rpt = await engine.DryRunOpAsync("update_measure",
                "{\"objRef\":\"" + mref + "\",\"expression\":\"SUM(Facts[Amount])\"}");

            Assert.True(rpt.WouldSucceed);
            Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);          // still no audit record
        }

        // 7) Teaching refusals name the recovery — one per family + the unknown-op case.
        [Fact]
        public async Task Denied_ops_refuse_with_a_recovery_teaching_message()
        {
            var (engine, _) = await FreshAsync();

            var io = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.DryRunOpAsync("save_model", null));
            Assert.Contains("run it directly", io.Message);

            var composite = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.DryRunOpAsync("apply_plan", null));
            Assert.Contains("propose_plan", composite.Message);

            var timeline = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.DryRunOpAsync("undo_change", null));
            Assert.Contains("undo is already reversible", timeline.Message);

            var cloud = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.DryRunOpAsync("deploy_stage", null));
            Assert.Contains("commit", cloud.Message);   // "already defaults to dry-run … commit/consent flag"

            var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.DryRunOpAsync("frobnicate", null));
            Assert.Contains("get_op_catalog", unknown.Message);
        }

        // 8) A missing REQUIRED argument teaches the parameter name + the op signature.
        [Fact]
        public async Task Missing_required_argument_teaches_the_parameter_and_signature()
        {
            var (engine, _) = await FreshAsync();
            await AddFactsAsync(engine);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.DryRunOpAsync("create_measure", "{\"tableRef\":\"table:Facts\",\"name\":\"M\"}"));   // no expression
            Assert.Contains("expression", ex.Message);
            Assert.Contains("create_measure(", ex.Message);   // names the signature
        }

        // 9) The raw-TOM / TMDL-undo seam (calendars) rolls back cleanly inside the scope — proving custom
        //    IUndoAction batches, not just wrapper edits, undo under dry-run.
        [Fact]
        public async Task Dry_run_define_calendar_rehearses_and_rolls_back_the_custom_undo_batch()
        {
            var (engine, sm) = await FreshAsync(cl: 1701);
            var t = await engine.CreateTableAsync("Dim Date", "human");
            await engine.CreateColumnAsync(t, "Date", "DateTime", "Date", "human");

            var rpt = await engine.DryRunOpAsync("define_calendar",
                "{\"tableRef\":\"Dim Date\",\"name\":\"Gregorian\",\"mappings\":[{\"column\":\"Date\",\"timeUnit\":\"Date\"}]}");

            Assert.True(rpt.WouldSucceed);
            // Calendars mutate via raw TOM (CalendarOps.Mutate), which doesn't emit wrapper ObjectChanged deltas —
            // so the proof here is the rehearsed-then-rolled-back custom undo batch, not the delta count.
            Assert.NotEmpty(rpt.Mutations);
            Assert.Empty((await engine.ListCalendarsAsync(null)).Calendars);   // nothing left after the rehearsal
        }

        // 10) Health delta (feature #4): a rehearsal never reaches the tracked-commit path, so it computes NO
        //     health delta — the agent mailbox stays empty (and test 2 already proves no broadcast rides out).
        //     Pro engine, so the probe is genuinely installed and the suppression is the dry-run short-circuit,
        //     not the free-tier gate.
        [Fact]
        public async Task Dry_run_emits_no_health_delta()
        {
            var (engine, _) = await FreshAsync(pro: true);
            await AddFactsAsync(engine);
            var rpt = await engine.DryRunOpAsync("create_measure",
                "{\"tableRef\":\"table:Facts\",\"name\":\"m9\",\"expression\":\"1\"}");
            Assert.True(rpt.WouldSucceed);
            Assert.Null(await engine.PullAgentHealthAsync());
        }

        // 11) Concurrency guard: a normal (non-dry) mutate issued AFTER a dry-run completed publishes normally —
        //     revision bumps, the broadcast fires (the scope did not leak into the live path).
        [Fact]
        public async Task A_real_mutate_after_a_dry_run_broadcasts_normally()
        {
            var (engine, sm) = await FreshAsync();
            await AddFactsAsync(engine);
            await engine.DryRunOpAsync("create_measure", "{\"tableRef\":\"table:Facts\",\"name\":\"Ghost\",\"expression\":\"1\"}");

            var seen = new List<ChangeNotification>();
            Action<ChangeNotification> handler = n => seen.Add(n);
            sm.Bus.Changed += handler;
            try
            {
                var before = sm.Current.Revision;
                await engine.CreateMeasureAsync("table:Facts", "Real", "1", "agent");
                Assert.Single(seen);
                Assert.True(seen[0].Revision > before);
                Assert.NotEmpty(seen[0].Deltas);
            }
            finally { sm.Bus.Changed -= handler; }
        }
    }
}
