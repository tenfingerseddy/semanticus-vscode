using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>A Lineage removal is staged through the shared Change Plan. It is review-only until a human
    /// approves it, applies as one undoable mutation, and reports a stale target instead of deleting another object.</summary>
    public sealed class ChangePlanDeleteTests
    {
        // Pro entitlement — the multi-item apply chokepoint is the one-click bulk primitive (Pro), so the same-batch
        // regression below (a set_dax + a delete_if_unused in ONE apply) needs a Pro engine to reach the mutate loop.
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        [Fact]
        public async Task Delete_stays_proposed_until_approved_then_undo_restores_it()
        {
            using var engine = new LocalEngine(new SessionManager());
            await engine.OpenAsync(TestModels.FindBim());
            var target = (await engine.ListMeasuresAsync()).First();

            var plan = await engine.AddPlanItemAsync(target.Ref, "delete", null, "Remove " + target.Name,
                null, null, "human");
            var item = Assert.Single(plan.Items);
            Assert.Equal("proposed", item.Status);
            Assert.Equal("structural", item.Risk);
            Assert.Equal("Structure", item.Category);

            var refused = await engine.ApplyPlanAsync(new[] { item.Id }, "human");
            Assert.Equal(0, refused.AppliedCount);
            Assert.Contains((await engine.ListMeasuresAsync()), m => m.Ref == target.Ref);

            await engine.SetPlanItemAsync(item.Id, null, approved: true, "human");
            var applied = await engine.ApplyPlanAsync(new[] { item.Id }, "human");
            Assert.Equal(1, applied.AppliedCount);
            Assert.DoesNotContain((await engine.ListMeasuresAsync()), m => m.Ref == target.Ref);

            await engine.UndoAsync("human");
            Assert.Contains((await engine.ListMeasuresAsync()), m => m.Ref == target.Ref);
        }

        [Fact]
        public async Task A_stale_delete_plan_item_fails_loudly()
        {
            using var engine = new LocalEngine(new SessionManager());
            await engine.OpenAsync(TestModels.FindBim());
            var target = (await engine.ListMeasuresAsync()).First();
            var plan = await engine.AddPlanItemAsync(target.Ref, "delete", null, "Remove " + target.Name,
                null, null, "human");
            var item = Assert.Single(plan.Items);
            await engine.SetPlanItemAsync(item.Id, null, approved: true, "human");

            await engine.DeleteObjectAsync(target.Ref, "agent");
            var report = await engine.ApplyPlanAsync(new[] { item.Id }, "human");

            Assert.Equal(0, report.AppliedCount);
            Assert.Equal(1, report.FailedCount);
            Assert.Contains("Object not found", Assert.Single(report.Items).Note);
        }

        // ---- delete_if_unused: the Storage tab's plan-routed removal (apply-time unused re-verification) ----

        private static async Task<(LocalEngine engine, string orphanRef)> WithOrphanAsync(bool pro = false)
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(pro));
            await engine.CreateModelAsync("StorageLoop", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            var orphan = await engine.CreateMeasureAsync(t, "Orphan", "1", "human");   // nothing references it → verdict "safe"
            return (engine, orphan);
        }

        [Fact]
        public async Task Delete_if_unused_applies_when_the_safe_verdict_still_holds()
        {
            var (engine, orphan) = await WithOrphanAsync();
            using (engine)
            {
                var plan = await engine.AddPlanItemAsync(orphan, "delete_if_unused", null, null, null, null, "human");
                var item = Assert.Single(plan.Items);
                Assert.Equal("proposed", item.Status);          // opt-in, like delete
                Assert.Equal("structural", item.Risk);

                await engine.SetPlanItemAsync(item.Id, null, approved: true, "human");
                var report = await engine.ApplyPlanAsync(new[] { item.Id }, "human");
                Assert.Equal(1, report.AppliedCount);
                Assert.Contains("re-verified", Assert.Single(report.Items).Note);
                Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Ref == orphan);

                await engine.UndoAsync("human");                // one undoable batch, same as delete
                Assert.Contains(await engine.ListMeasuresAsync(), m => m.Ref == orphan);
            }
        }

        [Fact]
        public async Task Delete_if_unused_skips_with_the_reason_when_a_referencer_appeared_after_staging()
        {
            var (engine, orphan) = await WithOrphanAsync();
            using (engine)
            {
                var plan = await engine.AddPlanItemAsync(orphan, "delete_if_unused", null, null, null, null, "human");
                var item = Assert.Single(plan.Items);
                await engine.SetPlanItemAsync(item.Id, null, approved: true, "human");

                // The verdict goes stale AFTER staging: a new measure now references the "unused" one.
                await engine.CreateMeasureAsync("table:Sales", "RefIt", "[Orphan] + 0", "agent");

                var report = await engine.ApplyPlanAsync(new[] { item.Id }, "human");
                Assert.Equal(0, report.AppliedCount);
                Assert.Equal(0, report.FailedCount);            // a stale verdict is a SKIP, never a failure
                Assert.Equal(1, report.SkippedCount);
                var note = Assert.Single(report.Items).Note;
                Assert.Contains("no longer holds at apply time", note);
                Assert.Contains(await engine.ListMeasuresAsync(), m => m.Ref == orphan);   // never deleted
            }
        }

        [Fact]
        public async Task Delete_if_unused_skips_when_an_EARLIER_item_in_the_SAME_batch_references_the_target()
        {
            // The apply-time re-verification must see edits made EARLIER in the SAME apply batch, not just referencers
            // that existed before apply: the unused sweep runs INSIDE the single-writer mutate, after the edits (deletes
            // are ordered last), so a set_dax that adds a referencer in the same batch must make the delete skip.
            var (engine, orphan) = await WithOrphanAsync(pro: true);   // multi-item apply is the Pro bulk primitive
            using (engine)
            {
                // A second measure that does NOT yet reference the orphan; a staged set_dax will make it reference it.
                var referer = await engine.CreateMeasureAsync("table:Sales", "RefIt", "0", "human");
                var editPlan = await engine.AddPlanItemAsync(referer, "set_dax", "[Orphan] + 0", null, null, null, "human");
                var editItem = editPlan.Items.First(i => i.Kind == "set_dax");
                await engine.SetPlanItemAsync(editItem.Id, null, approved: true, "human");   // opt-in set_dax → explicit approve

                var delPlan = await engine.AddPlanItemAsync(orphan, "delete_if_unused", null, null, null, null, "human");
                var delItem = delPlan.Items.First(i => i.Kind == "delete_if_unused");
                await engine.SetPlanItemAsync(delItem.Id, null, approved: true, "human");

                // Apply BOTH in one batch: edits run before removals, so the set_dax adds the referencer BEFORE the
                // unused sweep — the delete must skip with the reason, never delete a now-referenced measure.
                var report = await engine.ApplyPlanAsync(new[] { editItem.Id, delItem.Id }, "human");

                Assert.Equal(1, report.AppliedCount);          // the set_dax applied
                Assert.Equal(0, report.FailedCount);
                Assert.Equal(1, report.SkippedCount);          // the delete skipped (a stale verdict is a SKIP, not a failure)
                var del = report.Items.First(i => i.Kind == "delete_if_unused");
                Assert.Equal("skipped", del.Status);
                Assert.Contains("no longer holds at apply time", del.Note);
                Assert.Contains(await engine.ListMeasuresAsync(), m => m.Ref == orphan);   // never deleted
            }
        }

        [Fact]
        public async Task Add_plan_item_dedupes_an_identical_pending_item_but_not_a_resolved_one()
        {
            var (engine, orphan) = await WithOrphanAsync();
            using (engine)
            {
                var first = await engine.AddPlanItemAsync(orphan, "delete_if_unused", null, null, null, null, "human");
                var again = await engine.AddPlanItemAsync(orphan, "delete_if_unused", null, null, null, null, "human");
                Assert.Single(again.Items);                                        // no-op: the pending duplicate was returned
                Assert.Equal(Assert.Single(first.Items).Id, Assert.Single(again.Items).Id);

                // A different KIND on the same ref is a different proposal — still adds.
                var other = await engine.AddPlanItemAsync(orphan, "rename", "Orphan2", null, null, null, "human");
                Assert.Equal(2, other.Items.Length);

                // A resolved (rejected) item never blocks a fresh attempt.
                var pending = other.Items.First(i => i.Kind == "delete_if_unused");
                await engine.SetPlanItemAsync(pending.Id, null, approved: false, "human");
                var readded = await engine.AddPlanItemAsync(orphan, "delete_if_unused", null, null, null, null, "human");
                Assert.Equal(2, readded.Items.Count(i => i.Kind == "delete_if_unused"));
            }
        }
    }
}
