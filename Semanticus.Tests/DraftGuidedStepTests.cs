using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane l9-finish, item 1 (composer contract 1.3: a gate on an assistant step "runs exactly as a guided step does
    /// today"). A composed workflow whose assistant step carries a `yaml gate` drafts, and the gate runs inside the draft
    /// as a guided sub-run: the assistant submits the step with submit_workflow_step (its answers, and its writes as one
    /// change), the guided runner evaluates the gate before the draft moves on, a failed gate stops the draft with the
    /// gate's own sentence and takes the step's writes back, and each step that writes is one undo entry. Until this
    /// lane the draft refused such a file (author-gaps B9's named gap). Contoso-style names only.
    /// </summary>
    public sealed class DraftGuidedStepTests
    {
        private static async Task<(LocalEngine engine, SessionManager sm)> OpenAsync(string workspace = null)
        {
            workspace ??= System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sem-guided-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(workspace);
            return await ComposerFrame.OpenAsync(pro: true, workspace: workspace);
        }

        private static string Json(object o) => JsonSerializer.Serialize(o);

        private static Task<bool> HasMeasureAsync(SessionManager sm, string name) =>
            sm.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains(name));

        /// <summary>Step 1 Semanticus adds Units; step 2 the assistant names an object (a gate with one input and no
        /// writes); step 3 the assistant adds a helper measure and its hard gate checks the object step 2 named for new
        /// best practice breaks (bpa_clean, scope object, diffed against the run's start); step 4 Semanticus describes Units.</summary>
        internal const string Doc = "---\nschemaVersion: 3\nname: guided-helper\ntitle: Guided helper\nstrictness: hard\n---\n\n"
            + "## Step 1: Add Units\n```yaml step\nid: units\nperformer: semanticus\naction:\n  op: create_measure\n"
            + "  tableRef: table:Sales\n  name: Units\n  expression: SUM ( Sales[Quantity] )\n```\n\n"
            + "## Step 2: Name the object to review\n```yaml step\nid: pick\nperformer: assistant\n```\n\n"
            + "Name the object the next step's check reads.\n\n"
            + "```yaml gate\ninputs:\n  - name: target\n    question: \"The object to review, for example table:Date.\"\n    type: objectRef\n    required: required\n```\n\n"
            + "## Step 3: Add a helper and check the object\n```yaml step\nid: helper\nperformer: assistant\n```\n\n"
            + "Add one helper measure over Sales.\n\n"
            + "```yaml gate\nstrictness: hard\nops: [create_measure]\nverify:\n  - kind: bpa_clean\n    scope: object\n```\n\n"
            + "## Step 4: Describe Units\n```yaml step\nid: describe\nperformer: semanticus\naction:\n  op: set_description\n"
            + "  objRef: measure:Sales/Units\n  text: Units sold.\n```\n";

        private static readonly string HelperWrites = Json(new[]
        {
            new { op = "create_measure", args = new { tableRef = "table:Sales", name = "Helper", expression = "MAX ( Sales[Amount] )" } },
        });

        private static async Task<DraftView> DraftAsync(LocalEngine engine)
        {
            await engine.SaveWorkflowAsync("guided-helper", Doc, "human");
            return await engine.ComposeTaskAsync(null, "guided-helper", null, null, null, "human");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_gated_assistant_step_runs_inside_the_draft_as_a_guided_step_and_its_writes_are_one_undo_entry()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await DraftAsync(engine);
                // Step 1 is a change group, step 2 writes nothing, step 3's gate names a change op, step 4 is a group.
                Assert.Equal(3, v.UndoSteps);
                Assert.Equal(DraftCopy.GuidedStep, v.Steps.Single(s => s.Number == 3).Note);
                Assert.Equal(DraftCopy.AssistantStepChanges, v.Steps.Single(s => s.Number == 3).Changes);
                // Found in the UAT lane: a gated step that writes nothing and checks nothing said it changes the model
                // and that a check could stop the draft. It says what it does.
                Assert.Equal(DraftCopy.GuidedStepNoChecks, v.Steps.Single(s => s.Number == 2).Note);
                Assert.Equal(DraftCopy.GuidedStepChanges, v.Steps.Single(s => s.Number == 2).Changes);

                var undo = await ComposerFrame.UndoStepsAsync(sm);
                var waiting = await engine.RunDraftAsync(v.DraftId, "human");
                Assert.Equal(DraftStates.WaitingForAssistant, waiting.State);
                Assert.Equal("pick", waiting.PausedAt);
                Assert.Equal(DraftCopy.GuidedStepWaiting(2, writes: false), waiting.Summary);
                var runId = waiting.Steps.Single(s => s.Number == 2).RunId;
                Assert.False(string.IsNullOrEmpty(runId));
                Assert.True(await HasMeasureAsync(sm, "Units"));
                Assert.Equal(undo + 1, await ComposerFrame.UndoStepsAsync(sm));
                // The guided sub-run belongs to the draft: its row is the draft's, not a second Plan row.
                Assert.DoesNotContain((await engine.GetModelPlanAsync()).Items, i => i.Id == "run/" + runId);

                var request = (await engine.ListAssistantRequestsAsync()).Requests.Single(r => r.Need == "Name the object to review");
                var claim = await engine.ClaimAssistantRequestAsync(request.RequestId);
                Assert.Contains("submit_workflow_step", claim.Request.Brief);
                Assert.Contains(runId, claim.Request.Brief);
                // complete_request is the wrong door for a guided step, and says which door is right.
                var wrongDoor = await engine.CompleteAssistantRequestAsync(request.RequestId, claim.Request.ClaimToken, "[]");
                Assert.False(wrongDoor.Ok);
                Assert.Contains("submit_workflow_step", wrongDoor.Message);

                // A missing answer is the guided refusal, and the draft keeps waiting: only a failed check stops it.
                var unanswered = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(runId, "pick", "{}", "agent"));
                Assert.Contains("'target' is unanswered", unanswered.Message);
                Assert.Equal(DraftStates.WaitingForAssistant, (await engine.GetDraftAsync(v.DraftId)).State);

                await engine.SubmitWorkflowStepAsync(runId, "pick", Json(new { target = "table:Date" }), "agent");
                var atHelper = await engine.GetDraftAsync(v.DraftId);
                Assert.Equal("helper", atHelper.PausedAt);
                Assert.Equal("Done", atHelper.Steps.Single(s => s.Number == 2).Status);
                Assert.Equal(undo + 1, await ComposerFrame.UndoStepsAsync(sm));    // step 2 wrote nothing

                // The draft is not at step 3's successor yet: a submission for another step is refused.
                var early = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(runId, "pick", "{}", "agent"));
                Assert.Contains("step 3", early.Message);

                // Step 3's writes apply as one change, its gate reads step 2's answer (table:Date, unchanged) and passes,
                // and the draft runs on to the end.
                var accepted = await engine.SubmitWorkflowStepAsync(runId, "helper", "{}", "agent", null, HelperWrites);
                Assert.Equal("completed", accepted.Status);
                var done = await engine.GetDraftAsync(v.DraftId);
                Assert.Equal(DraftStates.Done, done.State);
                Assert.True(await HasMeasureAsync(sm, "Helper"));
                Assert.Equal("Units sold.", await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures["Units"].Description));
                Assert.Equal(undo + 3, await ComposerFrame.UndoStepsAsync(sm));
                Assert.Equal(DraftCopy.DoneInSteps(3), done.Summary);

                // Undo the description, then the assistant's step: its write goes as one.
                await engine.UndoAsync("human");
                await engine.UndoAsync("human");
                Assert.False(await HasMeasureAsync(sm, "Helper"));
                Assert.True(await HasMeasureAsync(sm, "Units"));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_failed_gate_stops_the_draft_with_the_gates_sentence_and_takes_the_steps_writes_back()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await DraftAsync(engine);
                var waiting = await engine.RunDraftAsync(v.DraftId, "human");
                var runId = waiting.Steps.Single(s => s.Number == 2).RunId;
                // Units is new since the run started, and a bare new measure breaks best practice rules.
                await engine.SubmitWorkflowStepAsync(runId, "pick", Json(new { target = "measure:Sales/Units" }), "agent");
                var undo = await ComposerFrame.UndoStepsAsync(sm);

                var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(runId, "helper", "{}", "agent", null, HelperWrites));
                Assert.Contains("did not clear its hard gate", failed.Message);

                var stopped = await engine.GetDraftAsync(v.DraftId);
                Assert.Equal(DraftStates.Stopped, stopped.State);
                Assert.NotNull(stopped.Banner);
                Assert.Contains(DraftCopy.GuidedCheckFailed(3, ""), stopped.Banner.Text);
                Assert.Contains("NEW BPA violation", stopped.Banner.Text);
                Assert.Equal("Stopped", stopped.Steps.Single(s => s.Number == 3).Status);
                // The step's writes were taken back, the steps before it stay, and step 4 never ran.
                Assert.False(await HasMeasureAsync(sm, "Helper"));
                Assert.True(await HasMeasureAsync(sm, "Units"));
                Assert.Null(await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures["Units"].Description is { Length: > 0 } s ? s : null));
                Assert.Equal(undo, await ComposerFrame.UndoStepsAsync(sm));
                // The guided sub-run ended with the draft, and its request is off the queue.
                Assert.Equal("aborted", (await engine.GetWorkflowRunAsync(runId)).Status);
                Assert.DoesNotContain((await engine.ListAssistantRequestsAsync()).Requests, r => r.Need == "Add a helper and check the object");
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_person_can_continue_past_a_gated_step_as_a_recorded_skip_and_the_assistant_cannot()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await DraftAsync(engine);
                var waiting = await engine.RunDraftAsync(v.DraftId, "human");
                var runId = waiting.Steps.Single(s => s.Number == 2).RunId;
                await engine.SubmitWorkflowStepAsync(runId, "pick", Json(new { target = "table:Date" }), "agent");

                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SkipWorkflowStepAsync(runId, "helper", "no time", "agent"));
                Assert.Contains("Continue", refused.Message);
                Assert.Equal("helper", (await engine.GetDraftAsync(v.DraftId)).PausedAt);

                var done = await engine.ContinueDraftAsync(v.DraftId);
                Assert.Equal(DraftStates.Done, done.State);
                Assert.False(await HasMeasureAsync(sm, "Helper"));
                var run = await engine.GetWorkflowRunAsync(runId);
                Assert.Equal("completed", run.Status);
                Assert.Equal("skipped", run.Steps.Single(s => s.StepId == "helper").Status);
            }
        }

        [Fact]
        public async Task A_draft_waiting_at_a_gated_step_when_Semanticus_restarts_reads_stopped_and_may_run_again()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", null, "human");
            await f.Engine.CreateTableAsync("Date", "human");
            await f.SaveAsAsync("Guided.bim");
            f.UseNull();
            var v = await DraftAsync(f.Engine);
            await f.Engine.RunDraftAsync(v.DraftId, "human");

            // A restart: a new engine over the same workspace. The guided sub-run lived in the old engine's memory.
            using var sessions = new SessionManager();
            using var again = new LocalEngine(sessions, TestEntitlements.Pro, f.Workspace);
            await again.OpenAsync(System.IO.Path.Combine(f.Workspace, "Guided.bim"));
            var after = await again.GetDraftAsync(v.DraftId);
            Assert.Equal(DraftStates.Stopped, after.State);
            Assert.Equal(DraftCopy.Restarted, after.Banner?.Text);
            // Step 1 already changed the saved model's copy in the old engine only; the new engine's model has no Units,
            // so nothing it holds was kept, and the person may run the draft again.
            Assert.False(after.CanContinue);
        }

        // ---- N3 (coordinator, 26 Sep): a format-3 file whose steps are all the assistant's ------------------------

        /// <summary>What Author writes for a described step once the person gives it a Brief and a written check (lane
        /// author-gaps, B7): format 3, every step the assistant's, and v3-only fields on it.</summary>
        private static string DescribedDocument(DescribedWorkflow d, string name) =>
            "---\nschemaVersion: 3\nname: " + name + "\ntitle: Test its performance\n---\n\n"
            + string.Concat(d.Steps.Select((s, i) =>
                "## Step " + (i + 1) + ": " + s.Title + "\n```yaml step\nid: step-" + (i + 1) + "\nperformer: assistant\n"
                + "instructions: Time the slow measure cold and warm, and keep only a faster version that means the same.\n"
                + "checks:\n  - kind: valid_dax\n    phase: written\n```\n\nThe step to fill in.\n\n"));

        [Fact]
        public async Task A_described_all_assistant_workflow_with_a_Brief_runs_as_a_draft_and_never_reads_as_unrunnable()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var described = await engine.DescribeWorkflowStepsAsync("test its performance");
                Assert.NotEmpty(described.Steps);
                var text = DescribedDocument(described, "perf-check");
                var def = WorkflowParser.Parse(text);
                Assert.Null(def.Error);
                Assert.All(def.Steps, st => Assert.Equal(WorkflowPerformers.Assistant, st.Performer));

                // Nothing Semanticus wrote may read "reads that field but does not run it yet": it runs as a draft.
                Assert.True(WorkflowParser.IsComposed(def));
                Assert.Empty(WorkflowParser.UnexecutableControlFields(def));
                var guided = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartWorkflowAsync("perf-check", "human", text));
                Assert.DoesNotContain("does not run it yet", guided.Message);
                Assert.Contains("runs as a draft", guided.Message);

                // Run once from Author, unsaved: the draft asks the assistant for the step, with its Brief.
                var v = await engine.ComposeTaskAsync(null, "perf-check", null, null, null, "human", text);
                Assert.Equal(described.Steps.Length, v.Steps.Length + v.After.Length);
                var waiting = await engine.RunDraftAsync(v.DraftId, "human");
                Assert.Equal(DraftStates.WaitingForAssistant, waiting.State);
                var request = (await engine.ListAssistantRequestsAsync()).Requests.Single(r => r.JobId == v.DraftId);
                var claim = await engine.ClaimAssistantRequestAsync(request.RequestId);
                Assert.Contains("Time the slow measure cold and warm", claim.Request.Brief);
            }
        }

        [Fact]
        public void A_gate_on_an_assistant_step_of_a_composed_file_is_a_field_a_draft_now_runs()
        {
            var def = WorkflowParser.Parse(Doc);
            Assert.Null(def.Error);
            Assert.True(WorkflowParser.IsComposed(def));
            Assert.Empty(WorkflowParser.UnexecutableControlFields(def));
        }
    }
}
