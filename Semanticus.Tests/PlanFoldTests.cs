// The Plan fold line (Plan at the heart, lane E3; contract sections 4.1, 5.4 and 6.1).
//
// What these tests hold, one line each:
//   the line appears once per change of the offer and not again, so an unchanged offer costs nothing
//   it lists at most five tasks then "and N more", and is never longer than 600 characters
//   the independence wording: all independent, a task Semanticus cannot read, and a pair that overlaps
//   a task that waits for a Semanticus row is named once as not offered yet
//   the paused line comes once, with the tasks this assistant may still finish
//   the owner path and the attached path each keep their own reader's fingerprint
//   the F-246 line and this line sit together on one result

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Jev;
using Xunit;
using static Semanticus.Tests.PlanQueueKit;

namespace Semanticus.Tests
{
    public sealed class PlanFoldTests
    {
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_line_appears_once_per_offer_change_and_not_again()
        {
            using var f = await GappyAsync("FoldOnce.bim");
            await CallAsync(f.Engine);                                     // the assistant's first look: nothing offered
            var a = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            var b = await AskAsync(f.Engine, "Describe the Cost measure", Free("Cost"));

            var line = PlanLine(await CallAsync(f.Engine));
            Assert.NotNull(line);
            // Free-form tasks are never called independent (Sol 6, slice 7, finding 3), so no independence sentence.
            Assert.StartsWith("Plan: 2 tasks are ready for you.\n", line);
            Assert.DoesNotContain(PlanTaskQueue.AllIndependentSentence, line);
            Assert.Contains("1. Describe the Margin measure (" + a + ")", line);
            Assert.Contains("2. Describe the Cost measure (" + b + ")", line);
            Assert.EndsWith(PlanTaskQueue.Footer, line);

            Assert.Null(PlanLine(await CallAsync(f.Engine)));              // the same offer: no line, no tokens
            Assert.Null(PlanLine(await CallAsync(f.Engine)));

            var c = await AskAsync(f.Engine, "Describe the Units measure", Free("Units"));
            var again = PlanLine(await CallAsync(f.Engine));
            Assert.NotNull(again);
            Assert.Contains("(" + c + ")", again);
            Assert.Null(PlanLine(await CallAsync(f.Engine)));
        }

        /// <summary>Sol 6, slice 7, finding 3. A free-form task's words and brief can ask for anything: "create a
        /// Margin % measure" attached to one object and "rename Cost to Margin %" attached to another both land on the
        /// same new object. The attached objects used to be taken as the task's whole footprint, so the two were
        /// offered as independent. Words bound nothing, so the footprint stays unknown and neither is independent.</summary>
        [Fact]
        public async Task Two_free_form_tasks_with_different_attached_objects_are_not_called_independent()
        {
            using var f = await GappyAsync("FoldFreeForm.bim");
            var a = await AskAsync(f.Engine, "Create a Margin % measure next to Margin", Free("Margin"));
            var b = await AskAsync(f.Engine, "Rename Cost to Margin %", Free("Cost"));

            var list = await f.Engine.ListAssistantRequestsAsync();
            var offered = list.Requests.Where(r => r.RequestId == a || r.RequestId == b).ToArray();
            Assert.Equal(2, offered.Length);
            Assert.All(offered, r => Assert.False(r.Independent, r.RequestId + " was called independent"));

            var line = PlanLine(await CallAsync(f.Engine));
            Assert.NotNull(line);
            Assert.DoesNotContain(PlanTaskQueue.AllIndependentSentence, line);

            var plan = await f.Engine.GetModelPlanAsync();
            Assert.False(Row(plan, a).FootprintKnown);
            Assert.False(Row(plan, b).FootprintKnown);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task No_line_when_nothing_is_offered()
        {
            using var f = await GappyAsync("FoldNothing.bim");
            Assert.Null(PlanLine(await CallAsync(f.Engine)));
            await AskAsync(f.Engine, "Mine to do", doer: "you");         // the person's own task is never offered
            Assert.Null(PlanLine(await CallAsync(f.Engine)));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_line_names_at_most_five_tasks_and_is_never_longer_than_600_characters()
        {
            using var f = await GappyAsync("FoldCap.bim");
            for (var i = 1; i <= 7; i++)
                await AskAsync(f.Engine, "Task " + i + " " + new string('w', 250), Free("Cap" + i));

            var line = PlanLine(await CallAsync(f.Engine));
            Assert.NotNull(line);
            Assert.True(line.Length <= PlanTaskQueue.MaxChars, "the line is " + line.Length + " characters");
            Assert.StartsWith("Plan: 7 tasks are ready for you.", line);
            var numbered = line.Split('\n').Count(l => l.Length > 2 && char.IsDigit(l[0]) && l[1] == '.');
            Assert.InRange(numbered, 1, PlanTaskQueue.MaxListed);
            Assert.Contains("and " + (7 - numbered) + " more", line);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_task_Semanticus_cannot_read_keeps_the_persons_order_carries_the_note_and_nothing_is_called_independent()
        {
            using var f = await GappyAsync("FoldUnknown.bim");
            var vague = await AskAsync(f.Engine, "Tidy up the model");                  // no objects: unknown
            var named = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));

            var line = PlanLine(await CallAsync(f.Engine));
            Assert.NotNull(line);
            Assert.StartsWith("Plan: 2 tasks are ready for you.\n", line);            // no independence sentence
            Assert.DoesNotContain(PlanTaskQueue.AllIndependentSentence, line);
            // The person's order, first asked first (UAT-S09-1-001): the vague task is not moved behind the other.
            Assert.Contains("1. Tidy up the model (" + vague + ") " + PlanTaskQueue.UnknownFootprintNote, line);
            Assert.Contains("2. Describe the Margin measure (" + named + ")", line);

            var list = await f.Engine.ListAssistantRequestsAsync();
            Assert.Equal(new[] { vague, named }, list.Requests.Select(r => r.RequestId).ToArray());
            Assert.All(list.Requests, r => Assert.False(r.Independent));             // one unknown taints the pair
            Assert.Equal(PlanTaskQueue.UnknownFootprintNote, list.Requests[0].OfferNote);
            Assert.Null(list.Requests[1].OfferNote);
        }

        /// <summary>UAT-S09-1-001 (Sol UAT, 25 Sep). The person pressed Up on a task Semanticus cannot read, and the
        /// Plan and get_model_plan put it second while list_assistant_requests and the fold line still offered it
        /// third, because the offer moved every such task behind the rest. Up is the person's order and the
        /// contract says the queue is read in it (4.1, Up), so the offer keeps that order and the task carries the
        /// "cannot tell" note in place.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Up_on_a_task_Semanticus_cannot_read_moves_it_in_the_assistants_queue_as_on_the_Plan()
        {
            using var f = await GappyAsync("FoldUpUnknown.bim");
            var margin = await AskAsync(f.Engine, "Fix Margin to a ratio of totals", Free("Margin"));
            var cost = await AskAsync(f.Engine, "Describe the Cost measure", Free("Cost"));
            var ytd = await AskAsync(f.Engine, "Add a year-to-date version of Net Sales");   // no objects: unknown
            await CallAsync(f.Engine);

            var plan = await f.Engine.MovePlanTaskUpAsync(ytd);
            var planOrder = new[] { margin, cost, ytd }.OrderBy(id => Row(plan, id).Order).ToArray();
            Assert.Equal(new[] { margin, ytd, cost }, planOrder);

            var list = await f.Engine.ListAssistantRequestsAsync();
            Assert.Equal(planOrder, list.Requests.Select(r => r.RequestId).ToArray());
            Assert.Equal(new int?[] { 1, 2, 3 }, list.Requests.Select(r => r.Order).ToArray());
            Assert.Equal(PlanTaskQueue.UnknownFootprintNote, list.Requests[1].OfferNote);

            var line = PlanLine(await CallAsync(f.Engine));
            Assert.NotNull(line);
            Assert.Contains("2. Add a year-to-date version of Net Sales (" + ytd + ") " + PlanTaskQueue.UnknownFootprintNote, line);
            Assert.Contains("3. Describe the Cost measure (" + cost + ")", line);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Two_tasks_on_the_same_object_are_never_offered_together_and_the_second_is_named_as_waiting()
        {
            using var f = await GappyAsync("FoldOverlap.bim");
            var first = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            var second = await AskAsync(f.Engine, "Rename the Margin measure", Free("Margin"));

            var line = PlanLine(await CallAsync(f.Engine));
            Assert.NotNull(line);
            Assert.StartsWith("Plan: 1 task is ready for you.\n", line);
            Assert.Contains("(" + first + ")", line);
            Assert.DoesNotContain("(" + second + ")", line);
            Assert.Contains("Not offered yet: Rename the Margin measure. It waits for Describe the Margin measure first", line);
        }

        [Fact]
        public void The_overlap_wording_groups_a_pair_and_says_take_them_one_at_a_time()
        {
            // Under the waits rule two Ready rows never conflict (the later one waits), so the grouping sentence is
            // reached only if a future caller builds an offer by hand. Its wording is still pinned here.
            var a = new ModelPlanItem { Id = "pt-00000001", Title = "Brand words", FootprintKnown = true };
            var b = new ModelPlanItem { Id = "pt-00000002", Title = "Brand format", FootprintKnown = true };
            var offer = new PlanOffer { Ready = new List<ModelPlanItem> { a, b } };
            offer.Overlaps.Add((a.Id, b.Id, "column:Product/Brand"));

            var line = PlanTaskQueue.OfferLine(offer);
            Assert.StartsWith("Plan: 2 tasks are ready for you.\n", line);
            Assert.Contains("1 and 2 both change Product[Brand]. " + PlanTaskQueue.OneAtATimeSentence, line);
            Assert.False(offer.IsIndependent(a.Id));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_task_that_waits_for_a_Semanticus_row_is_not_offered_and_is_named_once()
        {
            using var f = await GappyAsync("FoldWaits.bim");
            var (row, obj) = SemanticusRowWithObject(await f.Engine.GetModelPlanAsync());
            var waits = await AskAsync(f.Engine, "Describe that object", new[] { obj });
            var ready = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));

            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Equal(PlanStateWords.Waiting, Row(plan, waits).StateWord);
            Assert.Contains(Row(plan, waits).WaitsFor, w => w.Id == row.Id);

            var line = PlanLine(await CallAsync(f.Engine));
            Assert.StartsWith("Plan: 1 task is ready for you.\n", line);
            Assert.Contains("(" + ready + ")", line);
            Assert.Contains("Not offered yet: Describe that object. It waits for ", line);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_paused_line_comes_once_and_names_what_this_assistant_may_finish()
        {
            using var f = await GappyAsync("FoldPaused.bim");
            var held = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));
            await AskAsync(f.Engine, "Describe the Cost measure", Free("Cost"));
            Assert.NotNull(PlanLine(await CallAsync(f.Engine)));
            Assert.True((await f.Engine.ClaimAssistantRequestAsync(held)).Ok);

            await f.Engine.SetAssistantQueueAsync("paused");
            var line = PlanLine(await CallAsync(f.Engine));
            Assert.Equal(PlanTaskQueue.PausedSentence + "\nDescribe the Margin measure: you may finish it.", line);
            Assert.Null(PlanLine(await CallAsync(f.Engine)));              // once

            await f.Engine.SetAssistantQueueAsync("on");
            var back = PlanLine(await CallAsync(f.Engine));
            Assert.StartsWith("Plan: 1 task is ready for you.", back);      // the held one is no longer offered
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_owner_path_and_the_attached_path_each_keep_their_own_fingerprint()
        {
            using var f = await GappyAsync("FoldPaths.bim");
            await using var pipe = await Pipe.OpenAsync(f);
            var id = await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));

            var owner = PlanLine(await CallAsync(f.Engine));               // the MCP door in this process
            var attached = PlanLine(await CallAsync(pipe.Assistant));      // an assistant attached over the pipe
            Assert.NotNull(owner);
            Assert.NotNull(attached);
            Assert.Contains("(" + id + ")", attached);
            Assert.Null(PlanLine(await CallAsync(f.Engine)));
            Assert.Null(PlanLine(await CallAsync(pipe.Assistant)));

            // A person's own connection is shown the Plan itself, never the fold.
            Assert.Null(await pipe.Human.InvokeAsync<string>("pullPlanOffer"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_user_edit_line_and_the_plan_line_sit_together_on_one_result()
        {
            using var f = await GappyAsync("FoldBoth.bim");
            await CallAsync(f.Engine);
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "Summed sales.", "human");
            await AskAsync(f.Engine, "Describe the Margin measure", Free("Margin"));

            var texts = await CallAsync(f.Engine);
            Assert.Single(texts, t => t.StartsWith(McpHealthAppender.FoldPrefix, StringComparison.Ordinal));
            Assert.NotNull(PlanLine(texts));
        }
    }
}
