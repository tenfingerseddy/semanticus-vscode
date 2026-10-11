// The person's side of the queue, and the aim tools refused to an assistant (Plan at the heart, lane E3; contract
// sections 3.3 and 12, Sol 24 Sep).
//
// What these tests hold, one line each:
//   I'll take it: the person claims the task, it reads On it "you took it", and it leaves the assistant's offer
//   the person cannot take a task the assistant holds: first claim wins
//   Mark done with a reply closes it; Could not moves it to Needs you with the reason, and giving it back offers it
//   the three live on the human-only target, and an agent connection is refused them by method-not-found
//   add_plan_target, remove_plan_target and model_plan_action('done') are refused from the MCP door and from an
//   agent pipe connection with one sentence, and still work from the UI door

using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Jev;
using Xunit;
using static Semanticus.Tests.PlanQueueKit;

namespace Semanticus.Tests
{
    public sealed class PersonQueueTests
    {
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_person_takes_a_task_and_it_leaves_the_offer()
        {
            using var f = await GappyAsync("PersonTake.bim");
            var id = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            Assert.Contains((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == id);

            var plan = await f.Engine.ClaimPlanTaskAsync(id);
            var row = Row(plan, id);
            Assert.Equal(PlanStateWords.OnIt, row.StateWord);
            Assert.Equal(PlanTasks.YouTookItNote, row.StateNote);
            Assert.Equal(PlanDoers.You, row.Doer);
            Assert.Equal(PlanDoers.You, row.Claim.ClaimedBy);
            Assert.DoesNotContain((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == id);

            var refused = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.Equal(HandoffRefusal.AlreadyClaimed, refused.Refusal);
            Assert.Contains("The person took this one", refused.Message);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_person_cannot_take_a_task_the_assistant_holds()
        {
            using var f = await GappyAsync("PersonHeld.bim");
            var id = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.True(claim.Ok, claim.Message);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.ClaimPlanTaskAsync(id));
            Assert.Contains("Your assistant is working on this now", ex.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.CompletePlanTaskAsync(id, "done"));
            var row = Row(await f.Engine.GetModelPlanAsync(), id);
            Assert.Equal(PlanDoers.Assistant, row.Claim.ClaimedBy);
            Assert.Equal(PlanTasks.SinceLastCallNote, row.StateNote);
        }

        [Fact]
        public async Task Mark_done_with_a_reply_closes_the_task()
        {
            using var f = await GappyAsync("PersonDone.bim");
            var id = await AskAsync(f.Engine, "Check the Customer table", Free("Margin"));
            await f.Engine.ClaimPlanTaskAsync(id);

            var plan = await f.Engine.CompletePlanTaskAsync(id, "Looked fine to me.");
            var row = Row(plan, id);
            Assert.Equal(PlanStateWords.Done, row.StateWord);
            Assert.Equal("Looked fine to me.", row.Reply);
            Assert.Null(row.Claim);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.CompletePlanTaskAsync(id, null));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_has_words_task_marked_done_reads_checking_until_the_words_are_there()
        {
            using var f = await GappyAsync("PersonCheck.bim");
            var id = await AskAsync(f.Engine, "Describe Total Sales", new[] { "measure:Sales/Total Sales" }, doneWhen: "has_words");
            var said = Row(await f.Engine.CompletePlanTaskAsync(id, null), id);
            Assert.Equal(PlanStateWords.Checking, said.StateWord);

            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "Sum of the sales amount.", "human");
            Assert.Equal(PlanStateWords.Done, Row(await f.Engine.GetModelPlanAsync(), id).StateWord);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Could_not_moves_it_to_needs_you_with_the_reason_and_giving_it_back_offers_it_again()
        {
            using var f = await GappyAsync("PersonCouldNot.bim");
            var id = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            await f.Engine.ClaimPlanTaskAsync(id);
            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.DeclinePlanTaskAsync(id, "  "));

            var row = Row(await f.Engine.DeclinePlanTaskAsync(id, "There is no Margin measure yet."), id);
            Assert.Equal(PlanStateWords.NeedsYou, row.StateWord);
            Assert.Equal("There is no Margin measure yet.", row.StateNote);
            Assert.Equal("Could not: There is no Margin measure yet.", row.Sub);
            Assert.Null(row.Claim);

            // Give to your assistant: the same control E1 built, and the task is offered again.
            var back = Row(await f.Engine.SetPlanItemDoerAsync(id, "assistant"), id);
            Assert.Equal(PlanStateWords.Ready, back.StateWord);
            Assert.Contains((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == id);
        }

        [Fact]
        public async Task An_assistants_decline_hands_the_task_back_the_same_way()
        {
            using var f = await GappyAsync("PersonDeclined.bim");
            var id = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            var declined = await f.Engine.DeclineAssistantRequestAsync(id, claim.Request.ClaimToken, "No such measure.");
            Assert.True(declined.Ok, declined.Message);

            var row = Row(await f.Engine.GetModelPlanAsync(), id);
            Assert.Equal(PlanStateWords.NeedsYou, row.StateWord);
            Assert.Equal("No such measure.", row.StateNote);
            Assert.Equal(PlanDoers.You, row.Doer);
        }

        [Fact]
        public void The_three_live_on_the_human_target_and_the_four_ops_on_both()
        {
            foreach (var name in new[] { "claimPlanTask", "completePlanTask", "declinePlanTask" })
            {
                Assert.NotNull(typeof(HumanGovernanceRpcTarget).GetMethod(name));
                Assert.Null(typeof(EngineRpcTarget).GetMethod(name));
            }
            foreach (var name in new[] { "listAssistantRequests", "claimRequest", "completeRequest", "declineRequest" })
                Assert.NotNull(typeof(EngineRpcTarget).GetMethod(name));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_aim_tools_are_refused_from_both_agent_routes_and_work_from_the_ui_door()
        {
            using var f = await GappyAsync("PersonAims.bim");
            await using var pipe = await Pipe.OpenAsync(f);

            // The MCP door (this process owns the model).
            var mcp = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                McpTools.AddPlanTarget(f.Engine, "Every measure has a description", ModelPlanSections.Meaning));
            Assert.Equal(LocalEngine.AimsArePersonsSentence, mcp.Message);

            // The UI door: the person adds an aim and an asked task.
            var added = await pipe.Human.InvokeAsync<ModelPlanView>("addPlanTarget", "Every measure has a description", ModelPlanSections.Meaning, "human");
            var aim = added.Items.Single(i => i.Title == "Every measure has a description");
            var task = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));

            await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.RemovePlanTarget(f.Engine, aim.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.ModelPlanAction(f.Engine, aim.Id, "done"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.ModelPlanAction(f.Engine, task, "done"));

            // An agent pipe connection, and the attached assistant's engine: the same sentence, even when the agent
            // passes the page's own "plan" origin on the wire.
            async Task Refused(Func<Task> call)
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(call);
                Assert.Contains("Aims and tasks are the person's", ex.Message);
            }
            await Refused(() => pipe.Agent.InvokeAsync<ModelPlanView>("addPlanTarget", "Invented aim", ModelPlanSections.Meaning, "human"));
            await Refused(() => pipe.Agent.InvokeAsync<ModelPlanView>("removePlanTarget", aim.Id, "human"));
            await Refused(() => pipe.Agent.InvokeAsync<ModelPlanActionResult>("modelPlanAction", task, "done", "plan", null));
            await Refused(() => pipe.Agent.InvokeAsync<ModelPlanActionResult>("markPlanTargetDone", aim.Id, "human"));
            await Refused(() => pipe.Assistant.AddPlanTargetAsync("Invented aim", ModelPlanSections.Meaning, "agent"));
            await Refused(() => pipe.Assistant.ModelPlanActionAsync(aim.Id, "done", "agent"));

            // The person's own queue controls are absent from an agent connection.
            async Task NotFound(Func<Task> call)
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(call);
                Assert.Contains("method", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            await NotFound(() => pipe.Agent.InvokeAsync<ModelPlanView>("claimPlanTask", task));
            await NotFound(() => pipe.Assistant.CompletePlanTaskAsync(task, "x"));
            await NotFound(() => pipe.Assistant.DeclinePlanTaskAsync(task, "x"));

            // Nothing any of them tried landed, and the UI door still does all three.
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(plan.Items, i => i.Title == "Invented aim");
            Assert.True(PlanStateWords.IsOpen(Row(plan, task).StateWord));
            var marked = await pipe.Human.InvokeAsync<ModelPlanActionResult>("modelPlanAction", aim.Id, "done", "plan", null);
            Assert.Equal(PlanStateWords.Done, Row(marked.Plan, aim.Id).StateWord);
            var removed = await pipe.Human.InvokeAsync<ModelPlanView>("removePlanTarget", aim.Id, "human");
            Assert.DoesNotContain(removed.Items, i => i.Id == aim.Id);
            var mine = await pipe.Human.InvokeAsync<ModelPlanView>("claimPlanTask", task);
            Assert.Equal(PlanStateWords.OnIt, Row(mine, task).StateWord);
        }
    }
}
