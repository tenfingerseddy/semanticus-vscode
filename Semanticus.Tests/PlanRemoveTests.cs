// Remove a row from the Plan, and Undo it (Kane's UAT on build ee57ba84, 25 Sep: "no way to delete a task once you add
// it"). Before this there was no remove at all: a task could only be set aside, and a draft not even that.
//
// What these tests hold, one line each:
//   a task the person added is removed, leaves the plan and the assistant's offer, and Undo puts it back where it stood
//   a task the assistant has claimed is removed too; the assistant's next fold line says so once, and its answer is refused
//   a draft row is removed with its parts, and Undo brings both back
//   a row Semanticus found cannot be removed (Set aside is its way off), and a draft's part goes only with its draft
//   remove and restore live on the human-only target: an agent connection and an attached assistant are refused, and
//   no MCP tool can reach them

using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Jev;
using Xunit;
using static Semanticus.Tests.PlanQueueKit;

namespace Semanticus.Tests
{
    public sealed class PlanRemoveTests
    {
        [Fact]
        public async Task A_task_is_removed_and_Undo_puts_it_back_where_it_stood()
        {
            using var f = await GappyAsync("RemoveTask.bim");
            var first = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            var second = await AskAsync(f.Engine, "Check the Store table names", Free("Store names"));
            var third = await AskAsync(f.Engine, "Agree the wording with finance", Free("Wording"));
            Assert.Contains((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == second);

            var removed = await f.Engine.RemovePlanRowAsync(second);
            Assert.Equal(second, removed.ItemId);
            Assert.Equal("task", removed.Kind);
            Assert.Equal("Check the Store table names", removed.Title);
            Assert.False(removed.ToldAssistant);
            Assert.DoesNotContain(removed.Plan.Items, i => i.Id == second);
            Assert.DoesNotContain((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == second);

            // Undo: the same task, in the same place, offered again.
            var back = await f.Engine.RestorePlanRowAsync(second);
            var order = back.Items.Where(i => i.Id == first || i.Id == second || i.Id == third).Select(i => i.Id).ToArray();
            Assert.Equal(new[] { first, second, third }, order);
            Assert.Equal("Check the Store table names", Row(back, second).Title);
            Assert.Contains((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == second);

            // Undo is once: there is nothing left to put back.
            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.RestorePlanRowAsync(second));
        }

        [Fact]
        public async Task A_claimed_task_is_removed_and_the_assistant_reads_so_on_its_next_call()
        {
            using var f = await GappyAsync("RemoveClaimed.bim");
            var id = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.True(claim.Ok, claim.Message);
            await CallAsync(f.Engine);   // the fold the assistant already read

            var removed = await f.Engine.RemovePlanRowAsync(id);
            Assert.True(removed.ToldAssistant);
            Assert.DoesNotContain(removed.Plan.Items, i => i.Id == id);

            // The normal channel: its next call carries the line, once.
            var line = PlanLine(await CallAsync(f.Engine));
            Assert.Equal(LocalEngine.RemovedTaskLine("Describe the Margin measure"), line);
            Assert.Null(PlanLine(await CallAsync(f.Engine)));

            // An answer it sends anyway is refused with the same news, and nothing lands.
            var answer = await f.Engine.CompleteAssistantRequestAsync(id, claim.Request.ClaimToken, "Done it.");
            Assert.Equal(HandoffRefusal.NotFound, answer.Refusal);
            Assert.Contains("The person removed the task", answer.Message);

            // Undo brings it back unclaimed, offered again.
            var back = await f.Engine.RestorePlanRowAsync(id);
            Assert.Null(Row(back, id).Claim);
            Assert.Equal(PlanStateWords.Ready, Row(back, id).StateWord);
        }

        [Fact]
        public async Task A_found_row_is_set_aside_not_removed()
        {
            using var f = await GappyAsync("RemoveFound.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var found = plan.Items.First(i => !PlanTasks.IsTaskId(i.Id) && PlanStateWords.IsOpen(i.StateWord));
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.RemovePlanRowAsync(found.Id));
            Assert.Contains("set it aside", ex.Message);
        }

        [Fact]
        public async Task A_draft_row_is_removed_with_its_parts_and_Undo_brings_both_back()
        {
            using var f = await ProGappyAsync("RemoveDraft.bim");
            // A draft whose one step needs a description written: its part goes on the assistant's queue at once.
            var steps = JsonSerializer.Serialize(new object[]
            {
                new { id = "describe", title = "Describe Total Sales", op = "set_description", args = new { objRef = "measure:Sales/Total Sales" } },
            });
            var draft = await f.Engine.ComposeTaskAsync(null, null, null, steps, "Describe Total Sales", "agent");
            var rowId = "draft/" + draft.DraftId;
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Contains(plan.Items, i => i.Id == rowId);
            var part = plan.Items.Single(i => i.OriginLink?.Kind == PlanOriginKinds.Draft && i.OriginLink.Ref == draft.DraftId && PlanTasks.IsTaskId(i.Id));

            // A part goes only with its draft.
            var partRefused = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.RemovePlanRowAsync(part.Id));
            Assert.Contains("Remove the draft's row", partRefused.Message);

            var removed = await f.Engine.RemovePlanRowAsync(rowId);
            Assert.Equal("draft", removed.Kind);
            Assert.DoesNotContain(removed.Plan.Items, i => i.Id == rowId || i.Id == part.Id);
            Assert.DoesNotContain((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == part.Id);
            await Assert.ThrowsAnyAsync<ArgumentException>(() => f.Engine.GetDraftAsync(draft.DraftId));

            var back = await f.Engine.RestorePlanRowAsync(rowId);
            Assert.Contains(back.Items, i => i.Id == rowId);
            Assert.Contains(back.Items, i => i.Id == part.Id);
            Assert.Equal(draft.DraftId, (await f.Engine.GetDraftAsync(draft.DraftId)).DraftId);
        }

        [Fact]
        public async Task Remove_and_restore_are_the_persons_only()
        {
            using var f = await GappyAsync("RemovePersonOnly.bim");
            await using var pipe = await Pipe.OpenAsync(f);
            var task = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));

            async Task NotFound(Func<Task> call)
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(call);
                Assert.Contains("method", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            await NotFound(() => pipe.Agent.InvokeAsync<PlanRemoveResult>("removePlanRow", task));
            await NotFound(() => pipe.Agent.InvokeAsync<ModelPlanView>("restorePlanRow", task));
            await NotFound(() => pipe.Assistant.RemovePlanRowAsync(task));
            Assert.True(PlanStateWords.IsOpen(Row(await f.Engine.GetModelPlanAsync(), task).StateWord));

            // No MCP tool reaches either method.
            var tools = typeof(McpTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name == "McpServerToolAttribute")).ToList();
            Assert.NotEmpty(tools);
            Assert.DoesNotContain(tools, m => m.Name.Contains("RemovePlanRow", StringComparison.Ordinal)
                                              || m.Name.Contains("RestorePlanRow", StringComparison.Ordinal));

            // The person's door does both.
            var removed = await pipe.Human.InvokeAsync<PlanRemoveResult>("removePlanRow", task);
            Assert.DoesNotContain(removed.Plan.Items, i => i.Id == task);
            var back = await pipe.Human.InvokeAsync<ModelPlanView>("restorePlanRow", task);
            Assert.Contains(back.Items, i => i.Id == task);
        }

        /// <summary>The gappy model on Pro, for a draft.</summary>
        private static async Task<DecisionFixture> ProGappyAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }
    }
}
