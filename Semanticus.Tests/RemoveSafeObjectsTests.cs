using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// remove_safe_objects — the safe-to-remove sweep's ACT half (the Lineage tab's "Measure Killer" delete).
    /// Pins the safety bar the feature is sold on: the verified-safe set is deleted as ONE undoable transaction
    /// through the tracked mutation path; each item is RE-VERIFIED at apply time so a verdict that went stale
    /// since the caller's scan downgrades to skipped (never a stale delete); the free tier keeps the per-item
    /// path but is refused the &gt;1 bulk with the model left intact; ONE audit record carries the evidence;
    /// and dry_run rehearses the whole sweep without mutating.
    /// </summary>
    public sealed class RemoveSafeObjectsTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static async Task<(LocalEngine engine, SessionManager sm, string table)> OpenAsync(bool pro)
        {
            var sm = new SessionManager();
            var engine = new LocalEngine(sm, new Fake(pro));
            await engine.OpenAsync(TestModels.FindBim());   // AdventureWorks
            var table = (await engine.ListMeasuresAsync()).First().Table;
            return (engine, sm, table);
        }

        private static async Task<bool> ExistsAsync(LocalEngine engine, string objRef)
        {
            try { await engine.GetObjectAsync(objRef); return true; }        // get_object throws on a missing ref
            catch (InvalidOperationException) { return false; }
        }

        [Fact]
        public async Task Pro_sweep_removes_the_safe_set_atomically_and_one_undo_restores_all()
        {
            var (engine, sm, table) = await OpenAsync(pro: true);
            using var _ = engine;
            var a = await engine.CreateMeasureAsync("table:" + table, "Sweep_A", "1", "human");
            var b = await engine.CreateMeasureAsync("table:" + table, "Sweep_B", "2", "human");
            var c = await engine.CreateCalculatedColumnAsync("table:" + table, "Sweep_Col", "1", "human");

            var scan = await engine.UnusedObjectsAsync();
            foreach (var r in new[] { a, b, c })
                Assert.Contains(scan.Items, i => i.Ref == r && i.Verdict == "safe");

            var report = await engine.RemoveSafeObjectsAsync(new[] { a, b, c }, null, "human");
            Assert.Equal(3, report.Count);
            Assert.Equal(3, report.Removed.Length);
            Assert.Empty(report.Skipped);
            Assert.Contains(report.Removed, r => r.Ref == a);
            Assert.Contains(report.Removed, r => r.Ref == c);
            Assert.False(await ExistsAsync(engine, a));
            Assert.False(await ExistsAsync(engine, b));
            Assert.False(await ExistsAsync(engine, c));

            // ONE undo restores the whole sweep — the "atomic" the button promises.
            await engine.UndoAsync("human");
            Assert.True(await ExistsAsync(engine, a));
            Assert.True(await ExistsAsync(engine, b));
            Assert.True(await ExistsAsync(engine, c));
        }

        [Fact]
        public async Task Item_that_gained_a_dependent_after_the_scan_is_skipped_not_deleted()
        {
            var (engine, _, table) = await OpenAsync(pro: true);
            using var __ = engine;
            var a = await engine.CreateMeasureAsync("table:" + table, "Sweep_Stale", "1", "human");
            var b = await engine.CreateMeasureAsync("table:" + table, "Sweep_StillSafe", "2", "human");
            var scan = await engine.UnusedObjectsAsync();   // the caller's scan: both "safe"
            Assert.Contains(scan.Items, i => i.Ref == a && i.Verdict == "safe");
            Assert.Contains(scan.Items, i => i.Ref == b && i.Verdict == "safe");

            // Between the scan and the apply, A gains a (visible) dependent — its "safe" verdict is now stale.
            await engine.CreateMeasureAsync("table:" + table, "Sweep_User", "[Sweep_Stale] + 1", "human");

            var report = await engine.RemoveSafeObjectsAsync(new[] { a, b }, null, "human");
            Assert.Equal(1, report.Count);
            Assert.Single(report.Removed);
            Assert.Equal(b, report.Removed[0].Ref);
            var skipped = Assert.Single(report.Skipped);
            Assert.Equal(a, skipped.Ref);
            Assert.False(string.IsNullOrWhiteSpace(skipped.Reason));    // the reason says why, in plain English
            Assert.True(await ExistsAsync(engine, a));                  // the stale item was NOT deleted
            Assert.False(await ExistsAsync(engine, b));
        }

        [Fact]
        public async Task Free_tier_may_remove_one_item_but_a_bulk_sweep_is_refused_with_the_model_intact()
        {
            var (engine, sm, table) = await OpenAsync(pro: false);
            using var _ = engine;
            var a = await engine.CreateMeasureAsync("table:" + table, "Sweep_FreeA", "1", "human");
            var b = await engine.CreateMeasureAsync("table:" + table, "Sweep_FreeB", "2", "human");
            var revBefore = sm.Current.Revision;

            // Bulk (>1 verified-safe) refuses with the teach message and mutates NOTHING.
            var ex = await Assert.ThrowsAsync<EntitlementException>(() => engine.RemoveSafeObjectsAsync(new[] { a, b }, null, "human"));
            Assert.Contains("one at a time free", ex.Message);
            Assert.Equal(revBefore, sm.Current.Revision);   // the refusal left no mutation behind
            Assert.True(await ExistsAsync(engine, a));
            Assert.True(await ExistsAsync(engine, b));

            // A single item stays free — the same per-item primitive delete_object offers.
            var one = await engine.RemoveSafeObjectsAsync(new[] { a }, null, "human");
            Assert.Equal(1, one.Count);
            Assert.False(await ExistsAsync(engine, a));
            Assert.True(await ExistsAsync(engine, b));
        }

        [Fact]
        public async Task A_requested_item_that_is_in_use_is_skipped_with_a_reason_never_deleted()
        {
            var (engine, _, table) = await OpenAsync(pro: true);
            using var __ = engine;
            var used = await engine.CreateMeasureAsync("table:" + table, "Sweep_Used", "1", "human");
            await engine.CreateMeasureAsync("table:" + table, "Sweep_UsedBy", "[Sweep_Used] * 2", "human");

            var report = await engine.RemoveSafeObjectsAsync(new[] { used }, null, "human");
            Assert.Equal(0, report.Count);
            var skipped = Assert.Single(report.Skipped);
            Assert.Equal(used, skipped.Ref);
            Assert.True(await ExistsAsync(engine, used));
            Assert.Contains("Nothing removed", report.Note);
        }

        [Fact]
        public async Task Sweep_writes_ONE_audit_record_with_the_removed_and_skipped_evidence()
        {
            var (engine, _, table) = await OpenAsync(pro: true);
            using var __ = engine;
            var a = await engine.CreateMeasureAsync("table:" + table, "Sweep_AuditA", "1", "human");
            var b = await engine.CreateMeasureAsync("table:" + table, "Sweep_AuditB", "2", "human");
            var before = (await engine.ListVerifiedEditsAsync()).Records.Length;

            var report = await engine.RemoveSafeObjectsAsync(new[] { a, b }, null, "human");
            Assert.Equal(2, report.Count);

            var chain = await engine.ListVerifiedEditsAsync();
            Assert.True(chain.ChainIntact);
            var recs = chain.Records.Where(r => r.Op == "remove_safe_objects").ToArray();
            var rec = Assert.Single(recs);                       // ONE record for the whole sweep
            Assert.Equal(before + 1, chain.Records.Length);
            Assert.Equal("batch", rec.Verdict);
            Assert.Equal(report.Revision, rec.Revision);         // welded to the mutation it records
            Assert.Contains(a, rec.Evidence);                    // the evidence names what was removed
            Assert.Contains(b, rec.Evidence);
            Assert.Contains("model-only", rec.Evidence);         // and which verification basis vouched for "safe"
        }

        [Fact]
        public async Task Dry_run_rehearses_the_sweep_without_mutating_or_leaving_an_audit_record()
        {
            var (engine, sm, table) = await OpenAsync(pro: true);
            using var __ = engine;
            var a = await engine.CreateMeasureAsync("table:" + table, "Sweep_DryA", "1", "human");
            var b = await engine.CreateMeasureAsync("table:" + table, "Sweep_DryB", "2", "human");
            var revBefore = sm.Current.Revision;
            var auditBefore = (await engine.ListVerifiedEditsAsync()).Records.Length;

            var rpt = await engine.DryRunOpAsync("remove_safe_objects",
                $"{{\"refs\":[\"{a}\",\"{b}\"]}}");

            Assert.True(rpt.WouldSucceed);
            Assert.Null(rpt.Error);
            Assert.Contains("\"count\":2", rpt.Result);          // the op's own would-be report rode along
            Assert.NotEmpty(rpt.Mutations);                      // it really went through MutateAsync

            Assert.True(await ExistsAsync(engine, a));           // nothing was deleted
            Assert.True(await ExistsAsync(engine, b));
            Assert.Equal(revBefore, sm.Current.Revision);        // no revision bump
            Assert.Equal(auditBefore, (await engine.ListVerifiedEditsAsync()).Records.Length);   // no phantom audit record
        }

        [Fact]
        public async Task Report_paths_supplied_but_NONE_readable_refuses_the_whole_sweep()
        {
            // FAIL-LOUD (review blocker): the caller passed report paths for PROTECTION. If none can be read
            // (typo / legacy .pbix), the sweep must refuse entirely — never silently degrade to model-only and
            // delete a field the intended report uses.
            var (engine, sm, table) = await OpenAsync(pro: true);
            using var __ = engine;
            var a = await engine.CreateMeasureAsync("table:" + table, "Sweep_RptRefuseA", "1", "human");
            var b = await engine.CreateMeasureAsync("table:" + table, "Sweep_RptRefuseB", "2", "human");
            var revBefore = sm.Current.Revision;
            var auditBefore = (await engine.ListVerifiedEditsAsync()).Records.Length;

            var bogus = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sem_norpt_" + Guid.NewGuid().ToString("N"));
            var report = await engine.RemoveSafeObjectsAsync(new[] { a, b }, new[] { bogus }, "human");

            Assert.Equal(0, report.Count);
            Assert.Empty(report.Removed);
            Assert.Contains("could be read", report.Note);
            Assert.DoesNotContain("undo", report.Note, StringComparison.OrdinalIgnoreCase);   // nothing to undo
            Assert.Contains("0 of 1", report.Verification);            // the label counts READ reports, not paths
            Assert.Equal(2, report.Skipped.Length);                    // every requested ref is accounted for
            Assert.All(report.Skipped, s => Assert.Contains("could be read", s.Reason));

            Assert.True(await ExistsAsync(engine, a));                 // the model was not touched
            Assert.True(await ExistsAsync(engine, b));
            Assert.Equal(revBefore, sm.Current.Revision);              // no mutation, no revision bump
            Assert.Equal(auditBefore, (await engine.ListVerifiedEditsAsync()).Records.Length);   // no audit record
        }

        [Fact]
        public async Task Partially_readable_report_coverage_cannot_delete_anything()
        {
            // With PARTIAL readability the sweep must still not delete: AnalyzeReports demotes every bare "safe"
            // to "caution" when coverage is incomplete, so the candidate set is empty and each requested item
            // skips with the coverage reason. Pin that inherited fail-safe here so a future AnalyzeReports change
            // can't silently re-open the hole.
            var (engine, _, table) = await OpenAsync(pro: true);
            using var __ = engine;
            var orphan = await engine.CreateMeasureAsync("table:" + table, "Sweep_PartialOrphan", "1", "human");
            // A column a REAL readable report uses (so read>0 and the report reconciles to the open model).
            var colRef = await engine.CreateCalculatedColumnAsync("table:" + table, "Sweep_PartialCol", "1", "human");

            var entity = table.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var visual = "{ \"visual\": { \"query\": { \"queryState\": { \"Values\": { \"projections\": [ " +
                "{ \"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"" + entity + "\" } }, \"Property\": \"Sweep_PartialCol\" } } } ] } } } } }";
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sem_partial_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var def = System.IO.Path.Combine(root, "R.Report", "definition");
            var visualDir = System.IO.Path.Combine(def, "pages", "p1", "visuals", "v1");
            try
            {
                System.IO.Directory.CreateDirectory(visualDir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(def, "report.json"), "{}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(visualDir, "visual.json"), visual);
                var bogus = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sem_bogus_" + Guid.NewGuid().ToString("N"));

                var report = await engine.RemoveSafeObjectsAsync(new[] { orphan, colRef }, new[] { root, bogus }, "human");

                Assert.Equal(0, report.Count);                          // incomplete coverage ⇒ nothing deletes
                Assert.Contains("1 of 2", report.Verification);         // one readable, one not — labelled honestly
                Assert.Equal(2, report.Skipped.Length);
                Assert.True(await ExistsAsync(engine, orphan));
                Assert.True(await ExistsAsync(engine, colRef));
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch { /* best-effort temp cleanup */ } }
        }

        [Fact]
        public async Task Fully_readable_report_coverage_protects_report_used_fields_and_still_removes_true_orphans()
        {
            // The report-aware happy path: full coverage removes the true orphan and skips the report-used field.
            var (engine, _, table) = await OpenAsync(pro: true);
            using var __ = engine;
            var orphan = await engine.CreateMeasureAsync("table:" + table, "Sweep_RptOrphan", "1", "human");
            var colRef = await engine.CreateCalculatedColumnAsync("table:" + table, "Sweep_RptUsedCol", "1", "human");

            var entity = table.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var visual = "{ \"visual\": { \"query\": { \"queryState\": { \"Values\": { \"projections\": [ " +
                "{ \"field\": { \"Column\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"" + entity + "\" } }, \"Property\": \"Sweep_RptUsedCol\" } } } ] } } } } }";
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sem_full_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var def = System.IO.Path.Combine(root, "R.Report", "definition");
            var visualDir = System.IO.Path.Combine(def, "pages", "p1", "visuals", "v1");
            try
            {
                System.IO.Directory.CreateDirectory(visualDir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(def, "report.json"), "{}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(visualDir, "visual.json"), visual);

                var report = await engine.RemoveSafeObjectsAsync(new[] { orphan, colRef }, new[] { root }, "human");

                Assert.Equal(1, report.Count);
                Assert.Equal(orphan, report.Removed.Single().Ref);
                var skipped = Assert.Single(report.Skipped);
                Assert.Equal(colRef, skipped.Ref);                       // the report-used field survived
                Assert.Contains("1 of 1", report.Verification);
                Assert.False(await ExistsAsync(engine, orphan));
                Assert.True(await ExistsAsync(engine, colRef));
            }
            finally { try { System.IO.Directory.Delete(root, true); } catch { /* best-effort temp cleanup */ } }
        }

        [Fact]
        public async Task Sweep_with_no_refs_removes_the_whole_current_safe_set()
        {
            var (engine, _, table) = await OpenAsync(pro: true);
            using var __ = engine;
            await engine.CreateMeasureAsync("table:" + table, "Sweep_AllA", "1", "human");
            var preSafe = (await engine.UnusedObjectsAsync()).SafeCount;
            Assert.True(preSafe >= 1);

            var report = await engine.RemoveSafeObjectsAsync(null, null, "human");
            // Every pre-scan candidate is accounted for: removed, or skipped with a reason (e.g. a column kind
            // the wrapper refuses to delete) — never silently dropped.
            Assert.Equal(preSafe, report.Count + report.Skipped.Length);
            foreach (var r in report.Removed)
                Assert.False(await ExistsAsync(engine, r.Ref));
        }
    }
}
