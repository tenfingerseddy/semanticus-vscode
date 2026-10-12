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
        public async Task The_aim_tools_work_from_both_agent_routes_and_the_person_s_queue_controls_stay_off_the_agent_connection()
        {
            using var f = await GappyAsync("PersonAims.bim");
            await using var pipe = await Pipe.OpenAsync(f);

            // Kane, 10 Oct 2026: the person's word in chat is enough. The MCP door (the gated engine the assistant holds) adds an aim,
            // marks it done and removes it; the same on an agent pipe connection.
            var gated = AssistantEngine.ForAssistant(f.Engine);
            var viaMcp = await McpTools.AddPlanTarget(gated, "Every measure has a description", ModelPlanSections.Meaning);
            var aim = viaMcp.Items.Single(i => i.Title == "Every measure has a description");
            var task = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));

            var done = await McpTools.ModelPlanAction(gated, aim.Id, "done");
            Assert.Contains(done.Rows, r => r.Id == aim.Id && r.StateWord == PlanStateWords.Done);
            var taskDone = await McpTools.ModelPlanAction(gated, task, "done");
            Assert.Contains(taskDone.Rows, r => r.Id == task && r.StateWord == PlanStateWords.Done);
            var removed = await McpTools.RemovePlanTarget(gated, aim.Id);
            Assert.DoesNotContain(removed.Items, i => i.Id == aim.Id);

            var viaPipe = await pipe.Agent.InvokeAsync<ModelPlanView>("addPlanTarget", "Pipe aim", ModelPlanSections.Meaning, "human");
            var pipeAim = viaPipe.Items.Single(i => i.Title == "Pipe aim");
            var marked = await pipe.Assistant.ModelPlanActionAsync(pipeAim.Id, "done", "agent");
            Assert.Equal(PlanStateWords.Done, Row(marked.Plan, pipeAim.Id).StateWord);
            var gone = await pipe.Agent.InvokeAsync<ModelPlanView>("removePlanTarget", pipeAim.Id, "human");
            Assert.DoesNotContain(gone.Items, i => i.Id == pipeAim.Id);

            // The person's own claim, Mark done and Could not are absent from an agent connection: the assistant uses claim_request and the rest.
            async Task NotFound(Func<Task> call)
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(call);
                Assert.Contains("method", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            var open = await AskAsync(f.Engine, "Describe the Net Sales measure", Free("Net Sales"));
            await NotFound(() => pipe.Agent.InvokeAsync<ModelPlanView>("claimPlanTask", open));
            await NotFound(() => pipe.Assistant.CompletePlanTaskAsync(open, "x"));
            await NotFound(() => pipe.Assistant.DeclinePlanTaskAsync(open, "x"));
            Assert.True(PlanStateWords.IsOpen(Row(await f.Engine.GetModelPlanAsync(), open).StateWord));

            // The UI door still does all three.
            var mine = await pipe.Human.InvokeAsync<ModelPlanView>("claimPlanTask", open);
            Assert.Equal(PlanStateWords.OnIt, Row(mine, open).StateWord);
        }
    }
}
