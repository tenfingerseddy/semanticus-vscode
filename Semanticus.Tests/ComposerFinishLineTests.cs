using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Jev;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane L4's finish line (composer-lanes L4, Sol 24 Sep, with Plan-heart lanes section 4): the explicit-step engine
    /// run. A scripted MCP client composes the frames' seven change steps and the refresh, the two parts are filled
    /// through the queue (one bad first try, one decline with choices, one Write it yourself from the person's door),
    /// and the draft runs: the model changes exactly once, one Undo takes it back, and the failed after-check reads as
    /// the frame shows. The draft row's word on get_model_plan matches the draft's state at every step, and the change
    /// nests under the draft in get_plan_history on both doors.
    ///
    /// The after-check reads a live value. There is no live engine in a unit test, so the value comes through the
    /// engine's probe seam (DraftProbeForTest); without it the same check reads "Could not check: not connected",
    /// which the last assertion pins. That is a simulated live value, not a live pass.
    /// </summary>
    public sealed class ComposerFinishLineTests
    {
        private static async Task<string> PlanWordAsync(LocalEngine engine, string draftId)
        {
            var plan = await McpTools.GetModelPlan(engine);
            var row = plan.Items.Single(i => i.Id == "draft/" + draftId);
            var draft = await McpTools.GetDraft(engine, draftId);
            Assert.Equal(draft.PlanWord, row.StateWord);        // one mapping, read twice, never disagreeing
            Assert.Equal(draft.PlanNote, row.StateNote);
            return row.StateWord;
        }

        /// <summary>N2: the two doors that run a draft told the agent the whole change is ONE undo step. A step-by-step
        /// draft makes one undo step per run of Semanticus steps plus one per assistant step (DraftStops), so both
        /// descriptions say which draft is one undo step and which is several.</summary>
        [Theory]
        [InlineData(nameof(McpTools.ComposeTask))]
        [InlineData(nameof(McpTools.RunDraft))]
        public void The_run_doors_do_not_promise_one_undo_step_for_a_step_by_step_draft(string tool)
        {
            var method = typeof(McpTools).GetMethod(tool);
            var description = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.ComponentModel.DescriptionAttribute>(method)?.Description ?? "";
            Assert.DoesNotContain("the whole change runs as ONE undo step", description, StringComparison.Ordinal);
            Assert.DoesNotContain("The change runs as ONE undo step", description, StringComparison.Ordinal);
            Assert.Contains("no step for the person or the assistant runs as ONE undo step", description, StringComparison.Ordinal);
            Assert.Contains("one undo step per run of Semanticus steps, plus one per assistant step", description, StringComparison.Ordinal);
        }

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task The_explicit_step_engine_run()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true);
            using (sm)
            {
                engine.DraftProbeForTest = what => Task.FromResult<double?>(what == "measure:Returns/Return rate" ? 1.34 : (double?)null);
                var commits = new List<ChangeNotification>();
                Action<ChangeNotification> watch = n => { if (n.Deltas?.Length > 0) commits.Add(n); };
                sm.Bus.Changed += watch;
                var undoBefore = await ComposerFrame.UndoStepsAsync(sm);
                var words = new List<string>();
                try
                {
                    // compose_task with explicit steps: the parts go on the queue at once, nothing changes.
                    var v = await McpTools.ComposeTask(engine, JsonDocument.Parse(DraftFrame.Steps()).RootElement, title: DraftFrame.Title);
                    Assert.Equal(8, v.Steps.Length + v.After.Length);
                    Assert.Equal(DraftStates.WaitingForAssistant, v.State);
                    Assert.Equal("Your assistant writes 2 parts, then it runs by itself.", v.Summary);
                    Assert.Contains(DraftCopy.LoadingNote, v.Notes);
                    words.Add(await PlanWordAsync(engine, v.DraftId));                   // ready, on its next call

                    // run_draft records the approval; the change waits for the parts.
                    var approved = await McpTools.RunDraft(engine, v.DraftId);
                    Assert.True(approved.Approved);
                    Assert.Empty(commits);

                    var list = await McpTools.ListAssistantRequests(engine);
                    Assert.Equal(2, list.Requests.Length);
                    var summary = await engine.GetOrientationAsync();
                    Assert.Contains(summary.ActiveWork.Tasks.Drafts, d => d.DraftId == v.DraftId && d.PartsWaiting == 2);
                    Assert.All(summary.ActiveWork.Tasks.Ready, r => Assert.Equal(v.DraftId, r.Draft));

                    // The formula: claimed, a bad first try refused with the check named, then a good one.
                    var formula = DraftFrame.RequestOf(approved, "formula");
                    var claim = await McpTools.ClaimRequest(engine, formula);
                    Assert.True(claim.Ok, claim.Message);
                    Assert.Contains("Returns (ReturnKey, SalesLineKey, ReturnDate, Quantity, RefundAmount, Reason, Channel)", claim.Request.Brief);
                    words.Add(await PlanWordAsync(engine, v.DraftId));                   // on it: the part is being written
                    var bad = await McpTools.CompleteRequest(engine, formula, claim.Request.ClaimToken, "DIVIDE ( SUM ( Returns[Refund] ), SUM ( Sales[Quantity] ) )");
                    Assert.Equal(HandoffRefusal.ChecksFailed, bad.Refusal);
                    Assert.Contains("Refund", bad.Message);
                    Assert.Contains("2 tries left.", bad.Message);
                    Assert.Equal(2, bad.Request.AttemptsLeft);
                    words.Add(await PlanWordAsync(engine, v.DraftId));                   // still on it: the claim is kept
                    var good = await McpTools.CompleteRequest(engine, formula, claim.Request.ClaimToken, ComposerFrame.ReturnRate);
                    Assert.True(good.Ok, good.Message);
                    Assert.Empty(commits);
                    words.Add(await PlanWordAsync(engine, v.DraftId));                   // ready: the description waits

                    // The description: declined with choices; the one that names nothing is dropped.
                    var desc = DraftFrame.RequestOf(approved, "description");
                    var c2 = await McpTools.ClaimRequest(engine, desc);
                    var declined = await McpTools.DeclineRequest(engine, desc, c2.Request.ClaimToken,
                        "Should it say what counts as a return?", new[] { "measure:Returns/Return rate", "measure:Nowhere/Ghost" });
                    Assert.True(declined.Ok, declined.Message);
                    var needs = await McpTools.GetDraft(engine, v.DraftId);
                    Assert.Equal(DraftStates.NeedsYou, needs.State);
                    Assert.Equal(new[] { "measure:Returns/Return rate" }, needs.Blanks.Single(b => b.Name == "description").Choices);
                    words.Add(await PlanWordAsync(engine, v.DraftId));                   // needs you
                    Assert.Empty(commits);

                    // Write it yourself, from the person's door: the last part passes, the approved draft runs.
                    var ran = await new EngineRpcTarget(engine).editDraft(v.DraftId, "write", "describe", "description", text: DraftFrame.Description);
                    Assert.Equal(DraftStates.Done, ran.State);
                    words.Add(await PlanWordAsync(engine, v.DraftId));                   // needs you: a check failed after it

                    // The model changed exactly once, as one undo entry, filed under the draft.
                    var commit = Assert.Single(commits);
                    Assert.Equal(DraftFrame.Title, commit.Label);
                    Assert.Equal("semanticus", commit.By);
                    Assert.Equal(new[] { v.DraftId }, commit.Cause.Ids);
                    Assert.Equal(undoBefore + 1, await ComposerFrame.UndoStepsAsync(sm));

                    // The failed after-check reads as the frame shows, and the refresh could not run offline.
                    Assert.Equal("check_failed", ran.Banner.Kind);
                    Assert.Equal(DraftCopy.CheckFailedTitle, ran.Banner.Title);
                    Assert.Equal("Return rate is 134%. The check needs 0% to 100%. One Undo takes back steps 1 to 7, and the table goes with its data.", ran.Banner.Text);
                    Assert.Equal("Failed after it ran", ran.Steps.Single(s => s.Id == "return-rate").Status);
                    Assert.Equal("Done. One Undo takes back steps 1 to 7.", ran.Summary);
                    Assert.Contains(ran.Record.Blanks, r => r.FilledBy == "Your assistant");
                    Assert.Contains(ran.Record.Blanks, r => r.FilledBy == "You, with Write it yourself");

                    // History on both doors: one draft row with the change nested under it.
                    foreach (var history in new[] { await McpTools.GetPlanHistory(engine), await new EngineRpcTarget(engine).getPlanHistory() })
                    {
                        var parent = history.Rows.Single(r => r.Cause?.Kind == ChangeCauseKinds.Draft && r.Cause.Ids.Contains(v.DraftId));
                        Assert.Equal("semanticus", parent.By);
                        Assert.NotEmpty(parent.Children ?? new[] { parent });
                    }
                    Assert.Equal(new[] { PlanStateWords.Ready, PlanStateWords.OnIt, PlanStateWords.OnIt, PlanStateWords.Ready,
                                         PlanStateWords.NeedsYou, PlanStateWords.NeedsYou }, words);

                    // One Undo takes all of it back.
                    await engine.UndoAsync("agent");
                    Assert.False(await ComposerFrame.HasTableAsync(sm, "Returns"));
                    Assert.Equal(undoBefore, await ComposerFrame.UndoStepsAsync(sm));
                }
                finally { sm.Bus.Changed -= watch; }
            }
        }

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Without_a_live_value_the_after_check_could_not_check_and_is_not_a_pass()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true);
            using (sm)
            {
                var rpc = new EngineRpcTarget(engine);
                var v = await rpc.composeTask(stepsJson: DraftFrame.Steps(after: false), title: DraftFrame.Title);
                await rpc.editDraft(v.DraftId, "write", "return-rate", "formula", text: ComposerFrame.ReturnRate);
                await rpc.editDraft(v.DraftId, "write", "describe", "description", text: DraftFrame.Description);
                var ran = await rpc.runDraft(v.DraftId);
                Assert.Equal(DraftStates.Done, ran.State);
                Assert.Equal(PlanStateWords.Checking, ran.PlanWord);                // applied, checking: never Done
                Assert.Equal("Step 5: " + DraftCopy.NotConnected, ran.PlanNote);
                Assert.Null(ran.Banner);
            }
        }
    }
}
