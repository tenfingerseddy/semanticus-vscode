using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>A Lineage removal is staged through the shared Change Plan. It is review-only until a human
    /// approves it, applies as one undoable mutation, and reports a stale target instead of deleting another object.</summary>
    public sealed class ChangePlanDeleteTests
    {
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
    }
}
