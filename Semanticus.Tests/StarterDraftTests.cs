using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane l9-finish, item 2 (composer L9's finish line: "the six featured cards run as composed drafts", which Kane
    /// made the six starters' files on 24 Sep). Each starter is hand-converted: a step Semanticus can do alone runs as
    /// performer: semanticus with its op, and every assistant step keeps its gate, whose inputs, checks and strictness
    /// are byte for byte the gate of the frozen v1.2.2 file. Each one drafts, and its gates fire inside the draft.
    /// </summary>
    public sealed class StarterDraftTests
    {
        public static readonly TheoryData<string> Starters = new TheoryData<string>
        {
            "verified-measure", "optimize-dax", "deploy-to-production", "governed-rename", "check-blast-radius", "secure-with-rls",
        };

        private static WorkflowDef Today(string name) =>
            WorkflowParser.ParseFile(Path.Combine(AppContext.BaseDirectory, "workflows", name + ".md"), "stock");

        private static WorkflowDef Frozen(string name) =>
            WorkflowParser.ParseFile(Path.Combine(AppContext.BaseDirectory, "workflows-compat", WorkflowSeedLibraryTests.ParkRelease, name + ".md"), "stock");

        private static string Json(object o) => JsonSerializer.Serialize(o);

        [Theory]
        [MemberData(nameof(Starters))]
        public void A_starter_is_a_composed_workflow_that_keeps_every_gate_of_its_frozen_file(string name)
        {
            var today = Today(name);
            var frozen = Frozen(name);
            Assert.Null(today.Error);
            Assert.Null(frozen.Error);
            Assert.Equal(3, today.SchemaVersion);
            Assert.True(WorkflowParser.IsComposed(today), $"{name} has no step Semanticus runs, so it cannot draft.");
            Assert.Empty(WorkflowParser.UnexecutableControlFields(today));

            // Every gate that asks or checks anything is still there, on the same step, done by the assistant, with the
            // same inputs, the same checks and the same strictness.
            foreach (var before in frozen.Steps.Where(s => s.Gate != null && (s.Gate.Inputs.Length > 0 || s.Gate.Verify.Length > 0)))
            {
                var now = today.Steps.SingleOrDefault(s => s.Id == before.Id);
                Assert.True(now != null, $"{name} lost {before.Id}, which carried a gate.");
                Assert.Equal(WorkflowPerformers.Assistant, now.Performer);
                Assert.NotNull(now.Gate);
                Assert.Equal(Json(before.Gate.Inputs), Json(now.Gate.Inputs));
                Assert.Equal(Json(before.Gate.Verify), Json(now.Gate.Verify));
                Assert.Equal(before.Gate.Strictness, now.Gate.Strictness);
            }
            Assert.Equal(frozen.Strictness, today.Strictness);

            // Every op a frozen step named is still named: on the same step's gate, or as the op of a step Semanticus runs
            // (export_workflow_evidence folds into save_evidence, which exports the record as it saves it).
            var semanticusOps = today.Steps.Where(s => s.Performer == WorkflowPerformers.Semanticus).Select(s => s.Action.Op).ToHashSet();
            foreach (var before in frozen.Steps)
                foreach (var op in before.Ops)
                {
                    var now = today.Steps.SingleOrDefault(s => s.Id == before.Id);
                    var kept = (now != null && now.Ops.Contains(op)) || (now?.Action?.Op == op) || semanticusOps.Contains(op)
                               || (op == "export_workflow_evidence" && semanticusOps.Contains("save_evidence"));
                    Assert.True(kept, $"{name} {before.Id}: '{op}' is no longer named anywhere.");
                }

            // The frozen steps keep their order; a step Semanticus runs is added only after the change.
            var order = today.Steps.Select(s => s.Id).ToList();
            var frozenOrder = frozen.Steps.Select(s => s.Id).Where(order.Contains).ToList();
            Assert.Equal(frozenOrder, frozenOrder.OrderBy(order.IndexOf).ToList());
            Assert.All(today.Steps.Where(s => s.Performer == WorkflowPerformers.Semanticus), s => Assert.Equal("after", s.Phase));
            // No person's stop was made or lost: the frozen files had none, and none was invented.
            Assert.DoesNotContain(today.Steps, s => s.Performer == WorkflowPerformers.Person);
        }

        [Theory]
        [MemberData(nameof(Starters))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_starter_drafts_with_its_gated_steps_marked_and_passes_check_workflow(string name)
        {
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true, workspace: NewWorkspace());
            using (sm)
            {
                var check = await engine.CheckWorkflowAsync(name);
                Assert.Null(check.ParseError);
                Assert.DoesNotContain(check.Findings ?? Array.Empty<CheckFinding>(), f => f.Severity == "warn");
                var v = await engine.ComposeTaskAsync(null, name, null, null, null, "human");
                var today = Today(name);
                Assert.Equal(today.Steps.Length, v.Steps.Length + v.After.Length);
                foreach (var st in today.Steps.Where(s => s.Performer == WorkflowPerformers.Assistant && (s.Gate != null || s.Ops.Length > 0)))
                    Assert.Equal((st.Gate?.Verify.Length ?? 0) > 0 ? DraftCopy.GuidedStep : DraftCopy.GuidedStepNoChecks,
                                 v.Steps.Concat(v.After).Single(s => s.Id == st.Id).Note);
                // The guided start refuses it now: it runs as a draft.
                var guided = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartWorkflowAsync(name, "human"));
                Assert.Contains("runs as a draft", guided.Message);
            }
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> A_starter_drafts_with_its_gated_steps_marked_and_passes_check_workflow_SampleRows() => ((IEnumerable<object[]>)Starters).Take(1);

        [Theory]
        [MemberData(nameof(A_starter_drafts_with_its_gated_steps_marked_and_passes_check_workflow_SampleRows))]
        public Task A_starter_drafts_with_its_gated_steps_marked_and_passes_check_workflow_sample(string name) => A_starter_drafts_with_its_gated_steps_marked_and_passes_check_workflow(name);

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Publish_changes_runs_end_to_end_as_a_draft_and_its_input_gate_fires()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true, workspace: NewWorkspace());
            using (sm)
            {
                var saved = new List<string>();
                engine.DraftAfterOpForTest = (op, args) => { saved.Add(op); return Task.FromResult<object>(null); };
                var v = await engine.ComposeTaskAsync(null, "deploy-to-production", null, null, null, "human");
                // Found in the UAT lane: "Ready. It runs as 0 changes, one Undo each." A draft that changes nothing says so.
                Assert.Equal(0, v.UndoSteps);
                Assert.Equal(DraftCopy.ReadyInSteps(0), v.Summary);
                Assert.DoesNotContain("0 changes", v.Summary);
                var run = await engine.RunDraftAsync(v.DraftId, "human");
                Assert.Equal(DraftStates.WaitingForAssistant, run.State);
                var runId = run.Steps.Concat(run.After).First(s => s.RunId != null).RunId;

                // Step 1's gate asks for the destination: without it the step is refused, and the draft waits.
                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(runId, "step-1", "{}", "agent"));
                Assert.Contains("'targetStage' is unanswered", refused.Message);
                Assert.Equal("step-1", (await engine.GetDraftAsync(v.DraftId)).PausedAt);

                await engine.SubmitWorkflowStepAsync(runId, "step-1", Json(new { targetStage = "Contoso Test workspace" }), "agent");
                await engine.SubmitWorkflowStepAsync(runId, "step-2", Json(new { previewReviewed = "No blocking findings. The model has no roles." }), "agent");
                await engine.SubmitWorkflowStepAsync(runId, "step-3", "{}", "agent");
                Assert.Empty(saved);                                            // the save waits for step 4
                var last = await engine.SubmitWorkflowStepAsync(runId, "step-4", "{}", "agent");
                Assert.Equal("completed", last.Status);
                var done = await engine.GetDraftAsync(v.DraftId);
                Assert.Equal(DraftStates.Done, done.State);
                Assert.Equal(DraftCopy.DoneInSteps(0), done.Summary);
                Assert.DoesNotContain("Undo", done.Summary);
                Assert.Equal(new[] { "save_model" }, saved);
                Assert.Equal("Done", done.After.Single(s => s.Id == "step-5").Status);
            }
        }

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Test_a_complex_measure_stops_its_draft_when_the_anchor_gate_fails()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true, workspace: NewWorkspace());
            using (sm)
            {
                var v = await engine.ComposeTaskAsync(null, "verified-measure", null, null, null, "human");
                var run = await engine.RunDraftAsync(v.DraftId, "human");
                var runId = run.Steps.First(s => s.RunId != null).RunId;
                Assert.Equal(v.DraftId, (await engine.GetWorkflowRunAsync(runId)).DraftId);
                await engine.SubmitWorkflowStepAsync(runId, "step-1", Json(new
                {
                    requirement = "Quantity sold, summed over every dimension.",
                    clarification = new { declined = true, reason = "Nothing was ambiguous." },
                }), "agent");
                // No anchors for a declared grid: the hard anchor_coverage gate blocks, and the draft stops with its sentence.
                var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(runId, "step-2", Json(new
                {
                    expectedValues = "```json\n[]\n```",
                    equivalenceGrid = "'Sales'[Quantity]",
                    openGrains = new { declined = true, reason = "Every grain is anchored." },
                    naiveForm = "SUM over Sales[Quantity] everywhere.",
                }), "agent"));
                Assert.Contains("hard gate", blocked.Message);
                var stopped = await engine.GetDraftAsync(v.DraftId);
                Assert.Equal(DraftStates.Stopped, stopped.State);
                Assert.Contains(DraftCopy.GuidedCheckFailed(2, ""), stopped.Banner.Text);
                var aborted = await engine.GetWorkflowRunAsync(runId);
                Assert.Equal("aborted", aborted.Status);
                // One count everywhere (found in lane e: the banner said "of 5", the Plan row "of 4", the header chip
                // "step 2 of 4"). The true number is the draft's own steps, as its list numbers them: 5, the save included.
                Assert.Equal(DraftCopy.StoppedTitle(2, 5), stopped.Banner.Title);
                Assert.Equal(DraftCopy.StoppedTitle(2, 5), stopped.Summary);
                Assert.Equal(5, stopped.Steps.Length + stopped.After.Length);
                // The guided sub-run is part of the draft: its view says so, and the header chip (which counts the run's
                // own steps and opens a run row that a sub-run does not have) leaves it out.
                Assert.Equal(v.DraftId, aborted.DraftId);
            }
        }

        // ---- item 4: the parked starters' sentence, true on the Plan door (C7) --------------------------------------

        /// <summary>Contract 21.2's describe-it starters and the Plan menu step that replaced each.</summary>
        public static readonly TheoryData<string, string> Parked = new TheoryData<string, string>
        {
            { "new-measure", "add-measure" }, { "import-table", "import" }, { "add-relationship", "relate" },
        };

        [Theory]
        [MemberData(nameof(Parked))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_parked_starters_sentence_sends_the_person_to_the_Plan_where_the_draft_carries_its_checks(string parked, string menu)
        {
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true, workspace: NewWorkspace());
            using (sm)
            {
                // The sentence names the Plan's ask box (and compose_task), whose menu steps carry the checks. Since
                // describe-jev's C7 the steps New in Workflows describes carry their checks too (DescribedStep.Checks), so
                // finish-2 names both doors; what it claims of each must be what that door does.
                var taught = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartWorkflowAsync(parked, "human"));
                Assert.Contains("Describe the task in words", taught.Message);
                Assert.Contains("On the Plan, or through compose_task from your assistant, it becomes a draft that runs its own checks", taught.Message);
                Assert.Contains("In Workflows, New turns the words into steps in Author, and each step Semanticus knows carries its checks", taught.Message);
                Assert.DoesNotContain("without checks", taught.Message);

                // Every check kind the parked starter's gates ran rides the menu step that replaced it.
                var frozen = WorkflowParser.ParseFile(WorkflowSeedLibraryTests.ParkedFreeze(parked), "stock");
                var verifies = frozen.Steps.SelectMany(s => s.Gate?.Verify ?? Array.Empty<VerifySpec>()).Select(v => v.Kind).ToHashSet();
                var v = await engine.ComposeTaskAsync("do it", null, null, null, null, "human");
                var added = await engine.EditDraftAsync(v.DraftId, "add", null, null, null, menu, "human");
                var step = added.Steps.Single();
                var draftChecks = DraftMenu.Find(menu).Checks.Select(c => c.Kind).ToHashSet();
                // import-table's control total was "a row count or additive total from the source", probed through a
                // control measure it asked the assistant to add. A draft's import checks the loaded rows against the
                // source's row count itself (row_count with the number), so no measure has to be written for it.
                if (parked == "import-table") { Assert.Contains("dax_probe", verifies); verifies.Remove("dax_probe"); verifies.Add("row_count"); }
                Assert.Subset(draftChecks, verifies);
                // Each such check compares with a number the person may give, and runs only when they give one, as the
                // starter's gate did ("when: inputs.controlTotal.answered").
                Assert.Contains(added.Questions, q => q.Step == step.Id && q.Name == step.Id + "-expected");
            }
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> A_parked_starters_sentence_sends_the_person_to_the_Plan_where_the_draft_carries_its_checks_SampleRows() => ((IEnumerable<object[]>)Parked).Take(1);

        [Theory]
        [MemberData(nameof(A_parked_starters_sentence_sends_the_person_to_the_Plan_where_the_draft_carries_its_checks_SampleRows))]
        public Task A_parked_starters_sentence_sends_the_person_to_the_Plan_where_the_draft_carries_its_checks_sample(string parked, string menu) => A_parked_starters_sentence_sends_the_person_to_the_Plan_where_the_draft_carries_its_checks(parked, menu);

        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_relationship_drafted_on_the_Plan_checks_a_measure_through_it_against_the_number_given()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true, workspace: NewWorkspace());
            using (sm)
            {
                var customer = await engine.CreateTableAsync("Customer", "human");
                await engine.CreateColumnAsync(customer, "CustomerKey", "Int64", "CustomerKey", "human");
                await engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", "CustomerKey", "human");
                await engine.CreateMeasureAsync("table:Sales", "Total quantity", "SUM ( Sales[Quantity] )", "human");
                engine.DraftProbeForTest = what => Task.FromResult<double?>(what == "measure:Sales/Total quantity" ? 42.0 : null);
                async Task<DraftView> RelateAsync(string expected)
                {
                    var v = await engine.ComposeTaskAsync("relate sales to customers", null, null, null, null, "human");
                    var added = await engine.EditDraftAsync(v.DraftId, "add", null, null, null, "relate", "human");
                    var id = added.Steps.Single().Id;
                    await engine.EditDraftAsync(v.DraftId, "answer", null, null, id + "-fromColumnRef", "column:Sales/CustomerKey", "human");
                    await engine.EditDraftAsync(v.DraftId, "answer", null, null, id + "-toColumnRef", "column:Customer/CustomerKey", "human");
                    foreach (var q in added.Questions.Where(q => q.Step == id && q.Type == "enum"))
                        await engine.EditDraftAsync(v.DraftId, "answer", null, null, q.Name, q.Values.First(), "human");
                    await engine.EditDraftAsync(v.DraftId, "answer", null, null, id + "-measure", "measure:Sales/Total quantity", "human");
                    if (expected != null) await engine.EditDraftAsync(v.DraftId, "answer", null, null, id + "-expected", expected, "human");
                    return await engine.RunDraftAsync(v.DraftId, "human");
                }
                var right = await RelateAsync("42");
                Assert.Contains(right.Record.After, a => a.Kind == "dax_probe" && a.Status == DraftAfterStatus.Passed && a.Text.Contains("the number you gave"));
                await engine.UndoAsync("human");
                var wrong = await RelateAsync("40");
                Assert.Contains(wrong.Record.After, a => a.Kind == "dax_probe" && a.Status == DraftAfterStatus.Failed);
                await engine.UndoAsync("human");
                // No number given: the check does not run, as the starter's gate did not.
                var none = await RelateAsync(null);
                Assert.DoesNotContain(none.Record.After, a => a.Kind == "dax_probe");
            }
        }

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "sem-starter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            return ws;
        }
    }

    /// <summary>Lane l9-finish: a test of a starter's guided gates runs them from the starter's frozen format-1 bytes,
    /// saved as the project copy (which wins over the stock file), because the stock file now runs as a draft.</summary>
    internal static class FrozenStarter
    {
        internal static Task UseAsync(LocalEngine engine, string name) =>
            engine.SaveWorkflowAsync(name, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "workflows-compat", WorkflowSeedLibraryTests.ParkRelease, name + ".md")), "human");
    }
}
