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
    /// Lane author-gaps (slice 11, the audit on 82572fa1). The planned pieces of the composer that were refused or
    /// missing on that base, each proven through the doors a person and an assistant use:
    ///   B8, the L7 finish line: a draft becomes a format-3 workflow (edit_draft open_in_author), a step can be moved
    ///       (edit_draft reorder), and the workflow saved from it drafts again and asks its inputs.
    ///   B9: a composed workflow with a person's stop and an assistant's step runs, step by step, with one undo entry
    ///       per change group and one per assistant's step (composer contract 1.3, 3.2 and 3.4).
    ///   B10: the draft names what one Undo takes back, for the status bar ("Undo: ran N steps from your words").
    /// Contoso-style names only.
    /// </summary>
    public sealed class DraftAuthorGapsTests
    {
        private static async Task<(LocalEngine engine, SessionManager sm)> OpenAsync()
        {
            var workspace = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sem-gaps-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(workspace);
            return await ComposerFrame.OpenAsync(pro: true, workspace: workspace);
        }

        private static string Json(object o) => JsonSerializer.Serialize(o);

        private static Task<bool> HasMeasureAsync(SessionManager sm, string name) =>
            sm.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains(name));

        private static object Measure(string name, string dax) =>
            new { op = "create_measure", args = new { tableRef = "table:Sales", name, expression = dax } };

        // ---- B9: a person's stop ------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_persons_stop_pauses_the_run_between_two_change_groups_until_the_person_continues()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var steps = Json(new object[]
                {
                    Measure("Units", "SUM ( Sales[Quantity] )"),
                    new { performer = "person", title = "Check the units before the next step" },
                    Measure("Revenue", "SUM ( Sales[Amount] )"),
                });
                var v = await engine.ComposeTaskAsync(null, null, null, steps, "Units then revenue", "human");
                Assert.Equal(2, v.UndoSteps);                                     // two change groups
                Assert.Contains("you", v.Steps.Single(s => s.Number == 2).Tags);
                Assert.Equal(DraftCopy.PersonStepChanges, v.Steps.Single(s => s.Number == 2).Changes);
                // It does not promise one change: it says how many, and where it stops (contract 3.2).
                Assert.Equal(DraftCopy.ReadyInSteps(2), v.Summary);
                Assert.Contains(DraftCopy.StopsAt(2), v.Notes);

                var undo = await ComposerFrame.UndoStepsAsync(sm);
                var paused = await engine.RunDraftAsync(v.DraftId, "human");
                Assert.Equal(DraftStates.NeedsYou, paused.State);
                Assert.Equal("step-2", paused.PausedAt);
                Assert.True(paused.CanContinue);
                Assert.Equal(DraftCopy.PersonStop(2, "Check the units before the next step"), paused.Summary);
                Assert.True(await HasMeasureAsync(sm, "Units"));
                Assert.False(await HasMeasureAsync(sm, "Revenue"));
                Assert.Equal(undo + 1, await ComposerFrame.UndoStepsAsync(sm));
                Assert.Equal("Done", paused.Steps.Single(s => s.Number == 1).Status);
                Assert.Equal("Waiting", paused.Steps.Single(s => s.Number == 2).Status);

                // The assistant cannot pass a stop that asks the person.
                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.EditDraft(engine, v.DraftId, "continue", step: "2"));
                Assert.Contains("Continue is the person's own", refused.Message);
                Assert.False(await HasMeasureAsync(sm, "Revenue"));

                var done = await engine.ContinueDraftAsync(v.DraftId);
                Assert.Equal(DraftStates.Done, done.State);
                Assert.True(await HasMeasureAsync(sm, "Revenue"));
                Assert.Equal(undo + 2, await ComposerFrame.UndoStepsAsync(sm));   // one entry per change group
                Assert.Equal("Undo: ran 1 step of \"Units then revenue\"", done.UndoLabel);
                // Two changes, so the summary does not promise that one Undo takes back all of it (found in the UAT lane).
                Assert.Equal(DraftCopy.DoneInSteps(2), done.Summary);

                // One Undo takes back the newest group only, as the label says.
                await engine.UndoAsync("human");
                Assert.False(await HasMeasureAsync(sm, "Revenue"));
                Assert.True(await HasMeasureAsync(sm, "Units"));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_question_on_a_persons_stop_is_asked_at_the_stop_not_before_the_run()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string Doc = "---\nschemaVersion: 3\nname: stop-and-ask\ntitle: Stop and ask\n---\n"
                    + "## Step 1: Add Units\n```yaml step\nid: units\nperformer: semanticus\naction:\n  op: create_measure\n"
                    + "  tableRef: table:Sales\n  name: Units\n  expression: SUM ( Sales[Quantity] )\n```\n"
                    + "## Step 2: Is it right?\n```yaml step\nid: look\nperformer: person\nquestions:\n  - name: verdict\n    question: Does Units look right?\n    type: enum\n    values: [yes, no]\n```\n"
                    + "## Step 3: Add Revenue\n```yaml step\nid: revenue\nperformer: semanticus\naction:\n  op: create_measure\n"
                    + "  tableRef: table:Sales\n  name: Revenue\n  expression: SUM ( Sales[Amount] )\n```\n";
                await engine.SaveWorkflowAsync("stop-and-ask", Doc, "human");
                var check = await engine.CheckWorkflowAsync("stop-and-ask");
                Assert.DoesNotContain(check.Findings, f => f.Message.Contains("performer", StringComparison.Ordinal));

                var v = await engine.ComposeTaskAsync(null, "stop-and-ask", null, null, null, "human");
                Assert.NotEqual(DraftStates.NeedsYou, v.State);                   // the stop's question does not hold the start
                Assert.True(v.Questions.Single(q => q.Name == "verdict").AtStop);
                var paused = await engine.RunDraftAsync(v.DraftId, "human");
                Assert.Equal("look", paused.PausedAt);
                var ask = await Assert.ThrowsAsync<ArgumentException>(() => engine.ContinueDraftAsync(v.DraftId));
                Assert.Contains("Does Units look right?", ask.Message);
                await engine.EditDraftAsync(v.DraftId, "answer", null, null, "verdict", "yes", "human");
                var done = await engine.ContinueDraftAsync(v.DraftId);
                Assert.Equal(DraftStates.Done, done.State);
                Assert.True(await HasMeasureAsync(sm, "Revenue"));
            }
        }

        // ---- B9: an assistant's step ----------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_assistants_step_waits_for_its_writes_and_applies_them_as_one_undo_entry()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var steps = Json(new object[]
                {
                    Measure("Units", "SUM ( Sales[Quantity] )"),
                    new { performer = "assistant", title = "Add the helper measures", brief = "Two helpers over Sales.",
                          checks = new[] { new { kind = "refs_within", phase = "written", of = new[] { "Sales" } } } },
                    new { op = "set_description", args = new { objRef = "measure:Sales/Units", text = "Units sold." } },
                });
                var v = await engine.ComposeTaskAsync(null, null, null, steps, "Units and helpers", "human");
                Assert.Equal(3, v.UndoSteps);                                     // group, assistant's step, group
                Assert.Equal(DraftCopy.DirectStep, v.Steps.Single(s => s.Number == 2).Note);

                var undo = await ComposerFrame.UndoStepsAsync(sm);
                var waiting = await engine.RunDraftAsync(v.DraftId, "human");
                Assert.Equal(DraftStates.WaitingForAssistant, waiting.State);
                Assert.Equal("step-2", waiting.PausedAt);
                // Step 1 already changed the model, so the draft never says nothing has changed (found in the UAT lane).
                Assert.DoesNotContain(waiting.Notes, n => n.StartsWith(DraftCopy.NothingChangedYet, StringComparison.Ordinal));
                Assert.Contains(DraftCopy.OnNextCall, waiting.Notes);
                Assert.False(waiting.CanContinue && waiting.State != DraftStates.WaitingForAssistant);
                Assert.Equal(undo + 1, await ComposerFrame.UndoStepsAsync(sm));

                var request = (await engine.ListAssistantRequestsAsync()).Requests.Single(r => r.Need == "Add the helper measures");
                var claim = await engine.ClaimAssistantRequestAsync(request.RequestId);
                Assert.True(claim.Ok);
                Assert.Contains("Two helpers over Sales.", claim.Request.Brief);

                // A formula that reaches outside the step's checks is refused, and the claim stays.
                var bad = Json(new[] { new { op = "create_measure", args = new { tableRef = "table:Sales", name = "Days", expression = "COUNTROWS ( 'Date' )" } } });
                var refused = await engine.CompleteAssistantRequestAsync(request.RequestId, claim.Request.ClaimToken, bad);
                Assert.False(refused.Ok);
                Assert.Contains("not in Sales", refused.Message);
                Assert.Equal(undo + 1, await ComposerFrame.UndoStepsAsync(sm));

                var good = Json(new[]
                {
                    new { op = "create_measure", args = new { tableRef = "table:Sales", name = "Average quantity", expression = "AVERAGE ( Sales[Quantity] )" } },
                    new { op = "create_measure", args = new { tableRef = "table:Sales", name = "Largest amount", expression = "MAX ( Sales[Amount] )" } },
                });
                var ok = await engine.CompleteAssistantRequestAsync(request.RequestId, claim.Request.ClaimToken, good);
                Assert.True(ok.Ok, ok.Message);
                var done = await engine.GetDraftAsync(v.DraftId);
                Assert.Equal(DraftStates.Done, done.State);
                Assert.Equal(undo + 3, await ComposerFrame.UndoStepsAsync(sm));
                Assert.Equal("Units sold.", await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures["Units"].Description));

                // Undo the description, then the assistant's step: its two writes go together.
                await engine.UndoAsync("human");
                await engine.UndoAsync("human");
                Assert.False(await HasMeasureAsync(sm, "Average quantity"));
                Assert.False(await HasMeasureAsync(sm, "Largest amount"));
                Assert.True(await HasMeasureAsync(sm, "Units"));
            }
        }

        [Fact]
        public async Task A_composed_workflow_whose_person_stop_carries_a_gate_is_refused_not_drafted_without_it()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string Doc = "---\nschemaVersion: 3\nname: gated-helper\ntitle: Gated helper\n---\n"
                    + "## Step 1: Add Units\n```yaml step\nid: units\nperformer: semanticus\naction:\n  op: create_measure\n"
                    + "  tableRef: table:Sales\n  name: Units\n  expression: SUM ( Sales[Quantity] )\n```\n"
                    + "## Step 2: Check it\n```yaml step\nid: check\nperformer: person\n```\n"
                    + "```yaml gate\nverify:\n  - kind: bpa_clean\n    scope: object\n```\n";
                await engine.SaveWorkflowAsync("gated-helper", Doc, "human");
                // Lane l9-finish: an assistant's gated step now runs in the draft's guided sub-run (DraftGuidedStepTests);
                // a person's stop changes nothing, so a gate on one is still a field no path runs, and it is refused.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ComposeTaskAsync(null, "gated-helper", null, null, null, "human"));
                Assert.Contains("'gate:'", ex.Message);
            }
        }

        // ---- B8: open_in_author, reorder, and the saved workflow ---------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_draft_from_words_opens_in_Author_as_a_format_3_workflow_that_saves_and_drafts_again()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await engine.ComposeTaskAsync("add a measure with the total quantity", null, null, null, null, "human");
                var added = await engine.EditDraftAsync(v.DraftId, "add", null, null, null, "add-measure", "human");
                var step = added.Steps.Single();
                var table = added.Questions.Single(q => q.Name == step.Id + "-tableRef");
                await engine.EditDraftAsync(v.DraftId, "answer", null, null, table.Name, "table:Sales", "human");

                var opened = await McpTools.EditDraft(engine, v.DraftId, "open_in_author");
                Assert.NotNull(opened.Document);
                Assert.Equal("add-a-measure-with-the-total-quantity", opened.Document.Name);
                var def = WorkflowParser.Parse(opened.Document.Text);
                Assert.Null(def.Error);
                Assert.Equal(3, def.SchemaVersion);
                var st = def.Steps.Single();
                Assert.Equal(WorkflowPerformers.Semanticus, st.Performer);
                Assert.Equal("create_measure", st.Action.Op);
                Assert.Contains(st.Blanks, b => b.Fills == "expression" && b.Kind == "dax");
                Assert.Contains(st.Questions, q => q.Name == "add_measure_tableRef");      // a plain name the format accepts
                Assert.Equal("{{questions.add_measure_tableRef}}", st.Action.Argument("tableRef").Value);
                Assert.Equal("add a measure with the total quantity", def.Description);  // the words, intact
                Assert.DoesNotContain("table:Sales", opened.Document.Text);           // the answer is run state, dropped

                // Save workflow, then it drafts again and asks its question on the next run.
                await engine.SaveWorkflowAsync(opened.Document.Name, opened.Document.Text, "human");
                var again = await engine.ComposeTaskAsync(null, opened.Document.Name, null, null, null, "human");
                Assert.Equal(DraftStates.NeedsYou, again.State);
                Assert.Contains(again.Questions, q => q.Name == "add_measure_tableRef" && q.Answer == null);
            }
        }

        [Fact]
        public async Task A_mixed_draft_renders_who_does_each_step()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var steps = Json(new object[]
                {
                    Measure("Units", "SUM ( Sales[Quantity] )"),
                    new { performer = "person", title = "Look at it" },
                    new { performer = "assistant", title = "Tidy the helpers", brief = "Keep it short." },
                });
                var v = await engine.ComposeTaskAsync(null, null, null, steps, "Units, look, tidy", "human");
                var doc = (await engine.EditDraftAsync(v.DraftId, "open_in_author", null, null, null, null, "human")).Document;
                var def = WorkflowParser.Parse(doc.Text);
                Assert.Null(def.Error);
                Assert.Equal(new[] { WorkflowPerformers.Semanticus, WorkflowPerformers.Person, WorkflowPerformers.Assistant },
                    def.Steps.Select(s => s.Performer).ToArray());
                Assert.Equal("Keep it short.", def.Steps[2].Brief);
                // Nothing was saved and nothing changed.
                Assert.DoesNotContain((await engine.ListWorkflowsAsync()), w => w.Name == doc.Name);
                Assert.False(await HasMeasureAsync(sm, "Units"));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Reorder_moves_a_step_only_where_its_dependencies_allow()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var steps = Json(new object[]
                {
                    Measure("Units", "SUM ( Sales[Quantity] )"),
                    Measure("Revenue", "SUM ( Sales[Amount] )"),
                    new { op = "set_description", args = new { objRef = "measure:Sales/Units", text = "Units sold." } },
                });
                var v = await engine.ComposeTaskAsync(null, null, null, steps, "Three steps", "human");

                var moved = await McpTools.EditDraft(engine, v.DraftId, "reorder", step: "2", text: "1");
                Assert.Equal(new[] { "step-2", "step-1", "step-3" }, moved.Steps.OrderBy(s => s.Number).Select(s => s.Id).ToArray());
                Assert.Equal("Moved step 2 to 1.", moved.Sentence);

                // The description names the measure step-1 creates, so it cannot come before it.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.EditDraft(engine, v.DraftId, "reorder", step: "step-3", text: "1"));
                Assert.Contains("needs step", ex.Message);
                var still = await engine.GetDraftAsync(v.DraftId);
                Assert.Equal(new[] { "step-2", "step-1", "step-3" }, still.Steps.OrderBy(s => s.Number).Select(s => s.Id).ToArray());
            }
        }

        // ---- B10: the status bar's undo label -------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_draft_from_words_that_ran_names_what_one_Undo_takes_back()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await engine.ComposeTaskAsync("add a measure with the total quantity", null, null, null, null, "human");
                var added = await engine.EditDraftAsync(v.DraftId, "add", null, null, null, "add-measure", "human");
                var step = added.Steps.Single();
                await engine.EditDraftAsync(v.DraftId, "answer", null, null, step.Id + "-tableRef", "table:Sales", "human");
                await engine.EditDraftAsync(v.DraftId, "write", step.Id, "name", null, "Quantity total", "human");
                await engine.EditDraftAsync(v.DraftId, "write", step.Id, "expression", null, "SUM ( Sales[Quantity] )", "human");
                Assert.Null((await engine.GetDraftAsync(v.DraftId)).UndoLabel);   // nothing to undo yet
                var ran = await engine.RunDraftAsync(v.DraftId, "human");
                Assert.Equal(DraftStates.Done, ran.State);
                Assert.Equal("Undo: ran 1 step from your words", ran.UndoLabel);
            }
        }

        // ---- B6: Run once on unsaved edits (Plan-heart contract 21.5) -------------------------------------------

        private const string Composed = "---\nschemaVersion: 3\nname: run-once\ntitle: Run once\n---\n"
            + "## Step 1: Add Units\n```yaml step\nid: units\nperformer: semanticus\naction:\n  op: create_measure\n"
            + "  tableRef: table:Sales\n  name: Units\n  expression: SUM ( Sales[Quantity] )\n```\n";

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Run_once_drafts_the_unsaved_text_of_a_composed_workflow_and_saves_nothing()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                await engine.SaveWorkflowAsync("run-once", Composed, "human");
                var edited = Composed.Replace("name: Units\n", "name: Units sold\n");
                var v = await engine.ComposeTaskAsync(null, "run-once", null, null, null, "human", edited);
                var ran = await engine.RunDraftAsync(v.DraftId, "human");
                Assert.Equal(DraftStates.Done, ran.State);
                Assert.True(await HasMeasureAsync(sm, "Units sold"));
                Assert.False(await HasMeasureAsync(sm, "Units"));
                // The saved file is as it was.
                Assert.Contains("name: Units\n", (await engine.GetWorkflowDocumentAsync("run-once")).ExactText);
                // Text that does not parse is refused with the reason, and nothing is drafted.
                var bad = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ComposeTaskAsync(null, "run-once", null, null, null, "human", edited.Replace("op: create_measure", "op:")));
                Assert.Contains("does not run as it stands", bad.Message);
            }
        }

        [Fact]
        public async Task Run_once_starts_a_guided_workflow_from_its_unsaved_text_unless_a_team_rule_requires_it()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string Guided = "---\nschemaVersion: 2\nname: guided-once\ntitle: Guided once\n---\n## Step 1: Look\n```yaml step\nid: look\n```\nLook at Sales.\n";
                await engine.SaveWorkflowAsync("guided-once", Guided, "human");
                var edited = Guided.Replace("## Step 1: Look", "## Step 1: Look closely");
                var run = await engine.StartWorkflowAsync("guided-once", "human", edited);
                Assert.Equal("Look closely", run.Steps.First().Title);

                var wrong = await Assert.ThrowsAsync<ArgumentException>(() => engine.StartWorkflowAsync("some-other", "human", edited));
                Assert.Contains("not 'some-other'", wrong.Message);

                await engine.AbortWorkflowAsync(run.RunId, "done looking", "human");
                await engine.SetWorkflowBindingAsync("create_measure", new[] { "guided-once" }, "hard", "human");
                var required = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartWorkflowAsync("guided-once", "human", edited));
                Assert.Contains("runs only as saved", required.Message);
            }
        }

        // ---- C7: the draft carries the checks the parked starters had ------------------------------------------

        [Fact]
        public void The_add_a_measure_step_carries_every_check_the_parked_new_measure_ran()
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "workflows-compat", "v1.2.2");
            var parked = WorkflowParser.ParseFile(System.IO.Path.Combine(dir, "new-measure.md"), "stock");
            Assert.Null(parked.Error);
            var verifies = parked.Steps.SelectMany(s => s.Gate?.Verify ?? Array.Empty<VerifySpec>()).Select(v => v.Kind).ToHashSet();
            var checks = DraftMenu.Find("add-measure").Checks.Select(c => c.Kind).ToHashSet();
            Assert.Subset(checks, verifies);                                   // dax_probe and bpa_clean
            Assert.Contains("valid_dax", checks);                              // its step 2 ran validate_dax
            Assert.Contains("dax_lint", checks);                               // and lint_dax
            Assert.True(DraftMenu.Find("add-measure").Expected);               // and asked for a number it may decline
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_measure_drafted_from_words_is_checked_against_the_number_the_person_gave()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                engine.DraftProbeForTest = what => Task.FromResult<double?>(what == "measure:Sales/Quantity total" ? 42.0 : null);
                async Task<DraftView> DraftAsync(string expected)
                {
                    var v = await engine.ComposeTaskAsync("add a measure", null, null, null, null, "human");
                    var added = await engine.EditDraftAsync(v.DraftId, "add", null, null, null, "add-measure", "human");
                    var step = added.Steps.Single();
                    Assert.Equal("Checked once written", step.Check.Phase);
                    await engine.EditDraftAsync(v.DraftId, "answer", null, null, step.Id + "-tableRef", "table:Sales", "human");
                    if (expected != null) await engine.EditDraftAsync(v.DraftId, "answer", null, null, step.Id + "-expected", expected, "human");
                    await engine.EditDraftAsync(v.DraftId, "write", step.Id, "name", null, "Quantity total", "human");
                    await engine.EditDraftAsync(v.DraftId, "write", step.Id, "expression", null, "SUM ( Sales[Quantity] )", "human");
                    return await engine.RunDraftAsync(v.DraftId, "human");
                }
                var right = await DraftAsync("42");
                Assert.Equal(DraftStates.Done, right.State);
                Assert.Contains(right.Record.After, a => a.Kind == "dax_probe" && a.Status == DraftAfterStatus.Passed && a.Text.Contains("the number you gave"));
                Assert.Contains(right.Record.After, a => a.Kind == "bpa_clean" && (a.Status == DraftAfterStatus.Passed || a.Status == DraftAfterStatus.Failed));
                await engine.UndoAsync("human");

                var wrong = await DraftAsync("40");
                Assert.Equal("check_failed", wrong.Banner.Kind);
                Assert.Contains("The number you gave is 40", wrong.Banner.Text);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_relationship_drafted_from_words_checks_its_key_first()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await engine.ComposeTaskAsync("relate the tables", null, null, null, null, "human");
                var added = await engine.EditDraftAsync(v.DraftId, "add", null, null, null, "relate", "human");
                var step = added.Steps.Single();
                Assert.Equal("Checked", step.Check.Phase);
                Assert.Equal(DraftCopy.CheckWords(new ComposedCheck { Kind = "key_unique" }), step.Check.Text);
            }
        }
    }
}
