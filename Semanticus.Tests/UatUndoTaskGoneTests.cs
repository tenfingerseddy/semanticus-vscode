using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Jev;
using Xunit;
using static Semanticus.Tests.PlanQueueKit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane m11/uat-undo, item U4 (Sol UAT-S12-5-006, still there on the final build): a task whose object was deleted
    /// is never claimed or completed as Done. The earlier fix grounded a task to its objects when it was added, so a task
    /// the grounding never saw (saved before grounding existed, or written by something else) had nothing to look up
    /// and its deleted object read as one still to be made. Class: a task that names an object and holds no ground for it.
    /// </summary>
    public sealed class UatUndoTaskGoneTests
    {
        private const string Total = "measure:Sales/Total Sales";

        // A task as an older plan file holds it: its objects named, no grounds.
        private static void Ungrounded(DecisionFixture f)
        {
            var bound = f.Engine.BindPlanMemory();
            f.Engine.WritePlanTasks(bound, state => { foreach (var t in state.Tasks) t.Grounds = null; });
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_task_no_grounding_saw_is_not_claimed_or_completed_once_its_object_is_deleted()
        {
            using var f = await GappyAsync("UatGoneLegacy.bim");
            var held = await AskAsync(f.Engine, "Check the Total Sales format", new[] { Total });
            var next = await AskAsync(f.Engine, "Describe Total Sales for the report authors", new[] { Total });
            var claim = await f.Engine.ClaimAssistantRequestAsync(held);
            Assert.True(claim.Ok, claim.Message);
            Ungrounded(f);

            await f.Engine.DeleteObjectAsync(Total, "human");

            var refused = await f.Engine.ClaimAssistantRequestAsync(next);
            Assert.Equal(HandoffRefusal.NotReady, refused.Refusal);
            Assert.Contains("Sales[Total Sales] is no longer in the model", refused.Message);

            var answer = await f.Engine.CompleteAssistantRequestAsync(held, claim.Request.ClaimToken, "The target is gone. No description was applied.");
            Assert.Equal(HandoffRefusal.NotReady, answer.Refusal);
            Assert.Contains("is no longer in the model", answer.Message);

            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Equal(PlanStateWords.NeedsYou, Row(plan, next).StateWord);
            Assert.Equal("Sales[Total Sales] is no longer in the model.", Row(plan, next).StateNote);
            Assert.NotEqual(PlanStateWords.Done, Row(plan, held).StateWord);
            Assert.Null(Row(plan, held).Reply);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_deleted_table_takes_the_objects_on_it_that_a_task_names()
        {
            using var f = await GappyAsync("UatGoneTable.bim");
            var id = await AskAsync(f.Engine, "Check the Total Sales format", new[] { Total });
            Ungrounded(f);
            await f.Engine.DeleteObjectAsync("table:Sales", "human");
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.Equal(HandoffRefusal.NotReady, claim.Refusal);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_deletion_that_was_taken_back_leaves_the_task_claimable()
        {
            using var f = await GappyAsync("UatGoneUndone.bim");
            var id = await AskAsync(f.Engine, "Check the Total Sales format", new[] { Total });
            Ungrounded(f);
            await f.Engine.DeleteObjectAsync(Total, "human");
            await f.Engine.UndoAsync("human");
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.True(claim.Ok, claim.Message);
        }

        [Fact]
        public async Task An_object_a_task_is_to_create_is_never_called_gone()
        {
            using var f = await GappyAsync("UatGoneToCreate.bim");
            // Deleted long before the task, and named by it only because the task is to make it again.
            await f.Engine.CreateMeasureAsync("table:Sales", "Margin", "1", "human");
            await f.Engine.DeleteObjectAsync("measure:Sales/Margin", "human");
            await Task.Delay(30);
            var id = await AskAsync(f.Engine, "Add a Margin measure again", new[] { "measure:Sales/Margin" });
            Ungrounded(f);
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.True(claim.Ok, claim.Message);

            // And one nobody ever deleted.
            var other = await AskAsync(f.Engine, "Add a Discount measure", new[] { "measure:Sales/Discount" });
            Ungrounded(f);
            var again = await f.Engine.ClaimAssistantRequestAsync(other);
            Assert.False(again.Refusal == HandoffRefusal.NotReady && again.Message.Contains("no longer in the model"), again.Message);
        }
    }
}
