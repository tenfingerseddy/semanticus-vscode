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
    /// Slice 10, lane U5 and L5a: what the composer's doors need from the engine. The three Codex findings deferred to
    /// the doors (F-276 prepare-phase reads, F-277 blank generators, F-278 question defaults that name answers), the
    /// after-check controls the Plan row offers (Try again, Keep it anyway), the request a person copies, the options a
    /// "You choose" question lists, and the menu a draft from words opens as while Jev drafting is not live (L5a).
    /// Every one of these reaches both doors: edit_draft and get_draft are the same method on MCP and RPC.
    /// </summary>
    public sealed class DraftDoorsTests
    {
        private static async Task<(LocalEngine engine, SessionManager sm)> OpenAsync()
        {
            var workspace = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sem-doors-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(workspace);
            return await ComposerFrame.OpenAsync(pro: true, workspace: workspace);
        }

        private static JsonElement Json(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

        // ---- F-276: a prepare-phase read runs, before the change --------------------------------------------------

        private const string ReadThenAdd = "---\nschemaVersion: 3\nname: read-then-add\ntitle: Read then add\n---\n"
            + "## Step 1: Read the columns\n```yaml step\nid: read\nperformer: semanticus\naction:\n  op: list_columns\n```\n"
            + "## Step 2: Add the measure Units\n```yaml step\nid: units\nperformer: semanticus\naction:\n  op: create_measure\n"
            + "  tableRef: table:Sales\n  name: Units\n  expression: SUM ( Sales[Quantity] )\n```\n";

        // Codex on pull request 395, DraftRunner.cs:304: a format-3 workflow with a prepare-phase read passed
        // check_workflow, but compose_task refused it because the op is neither a change nor an after op.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_prepare_phase_read_is_drafted_and_runs_before_the_change()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                await engine.SaveWorkflowAsync("read-then-add", ReadThenAdd, "human");
                var v = await McpTools.ComposeTask(engine, workflow: "read-then-add");
                var read = v.Steps.Single(s => s.Id == "read");
                Assert.Equal("Reads the model", read.Changes);

                var undoBefore = await ComposerFrame.UndoStepsAsync(sm);
                var ran = await McpTools.RunDraft(engine, v.DraftId);
                Assert.Equal(DraftStates.Done, ran.State);
                Assert.Equal("Done", ran.Steps.Single(s => s.Id == "read").Status);
                Assert.Contains(ran.Record.After, a => a.StepId == "read" && a.What == "read" && a.Status == DraftAfterStatus.Ran);
                Assert.Equal(undoBefore + 1, await ComposerFrame.UndoStepsAsync(sm));
                Assert.True(await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("Units")));
            }
        }

        // A read that cannot run stops the draft before anything changes, and says which step and why.
        [Fact]
        public async Task A_prepare_read_that_fails_stops_the_draft_with_nothing_changed()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                await engine.SaveWorkflowAsync("read-then-add", ReadThenAdd, "human");
                engine.DraftAfterOpForTest = (op, json) => throw new InvalidOperationException("the source is not reachable.");
                var v = await McpTools.ComposeTask(engine, workflow: "read-then-add");
                var ran = await McpTools.RunDraft(engine, v.DraftId);
                Assert.Equal(DraftStates.Stopped, ran.State);
                Assert.Equal("nothing_changed", ran.Banner.Kind);
                Assert.Contains("Step 1 could not run: the source is not reachable.", ran.Banner.Text);
                Assert.False(await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("Units")));
            }
        }

        // Sol on slice 10 (F-288): every read runs before every change, so a read written after a change step would be
        // shown after it and run before it. The draft is refused with the step named, the way a change after an after
        // step already is, and nothing is drafted.
        [Fact]
        public async Task A_read_after_a_change_step_is_refused_when_drafted()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string AddThenRead = "---\nschemaVersion: 3\nname: add-then-read\ntitle: Add then read\n---\n"
                    + "## Step 1: Add the measure Units\n```yaml step\nid: units\nperformer: semanticus\naction:\n  op: create_measure\n"
                    + "  tableRef: table:Sales\n  name: Units\n  expression: SUM ( Sales[Quantity] )\n```\n"
                    + "## Step 2: Read the columns\n```yaml step\nid: read\nperformer: semanticus\naction:\n  op: list_columns\n```\n";
                await engine.SaveWorkflowAsync("add-then-read", AddThenRead, "human");
                var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.ComposeTask(engine, workflow: "add-then-read"));
                Assert.Contains("Step 2 reads after a step that changes the model", ex.Message);
            }
        }

        // ---- F-277: a blank's generator writes it; it never reaches the assistant ----------------------------------

        private const string StarterDoc = "---\nschemaVersion: 3\nname: starter-total\ntitle: Starter total\n"
            + "inputs:\n  - name: column\n    question: Which column?\n    type: objectRef\n    from: columns\n"
            + "  - name: option\n    question: Which total?\n    type: enum\n    values: [sum, average]\n---\n"
            + "## Step 1: Add a starter measure\n```yaml step\nid: total\nperformer: semanticus\naction:\n  op: create_measure\n"
            + "  tableRef: table:Sales\n  name: Quantity total\n  expression: \"{{blanks.formula}}\"\n"
            + "blanks:\n  - name: formula\n    kind: dax\n    fills: expression\n    generator: starter_measure\n    dependsOn: [column, option]\n```\n";

        // Codex on pull request 395, DraftRunner.cs:246: the generator was dropped when a saved workflow became a draft,
        // so the blank went to the assistant instead of being filled by the engine's fixed template.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_blank_with_a_starter_measure_generator_is_written_by_Semanticus_and_never_queued()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                await engine.SaveWorkflowAsync("starter-total", StarterDoc, "human");
                var v = await McpTools.ComposeTask(engine, workflow: "starter-total",
                    inputs: Json(new { column = "column:Sales/Quantity", option = "sum" }));
                var b = v.Blanks.Single(x => x.Name == "formula");
                Assert.Equal(DraftBlankStates.Generated, b.State);
                Assert.Equal("semanticus", b.FilledBy);
                Assert.Equal(StarterMeasureTemplates.Dax("sum", "Sales", "Quantity"), b.Text);
                Assert.Equal(0, v.PartsWaiting);
                Assert.Empty((await engine.ListAssistantRequestsAsync()).Requests);

                var ran = await McpTools.RunDraft(engine, v.DraftId);
                Assert.Equal(DraftStates.Done, ran.State);
                Assert.Equal(StarterMeasureTemplates.Dax("sum", "Sales", "Quantity"),
                    await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures["Quantity total"].Expression));
                Assert.Equal("Semanticus, fixed template", ran.Record.Blanks.Single().FilledBy);
            }
        }

        // A generator that does not apply (its answers name no column) leaves the blank to the assistant, as before. The
        // column is asked as free text here: an OBJECT input that names nothing is now refused when the draft is made
        // (UAT S12-2-005's rule, applied to compose_task inputs), so the generator's own "no such column" is reached
        // through a text answer.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_generator_that_does_not_apply_leaves_the_blank_to_the_assistant()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                await engine.SaveWorkflowAsync("starter-total", StarterDoc.Replace("type: objectRef\n    from: columns\n", "type: text\n"), "human");
                var v = await McpTools.ComposeTask(engine, workflow: "starter-total",
                    inputs: Json(new { column = "Nothing here", option = "sum" }));
                Assert.Equal(DraftBlankStates.Queued, v.Blanks.Single(x => x.Name == "formula").State);
            }
        }

        // ---- F-278: a Semanticus question's default that names an answer is filled from that answer -----------------

        // Codex on pull request 395, DraftRunner.cs:283: the default was copied verbatim, so the action read the token.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_semanticus_default_that_names_an_input_is_filled_from_the_input()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string doc = "---\nschemaVersion: 3\nname: count-rows\ntitle: Count rows\n"
                    + "inputs:\n  - name: table\n    question: Which table?\n    type: objectRef\n    from: tables\n---\n"
                    + "## Step 1: Add the measure Rows\n```yaml step\nid: rows\nperformer: semanticus\naction:\n  op: create_measure\n"
                    + "  tableRef: \"{{questions.target}}\"\n  name: Rows\n  expression: COUNTROWS ( Sales )\n"
                    + "questions:\n  - name: target\n    question: Which table gets it?\n    type: objectRef\n    by: semanticus\n"
                    + "    default: \"{{inputs.table}}\"\n```\n";
                await engine.SaveWorkflowAsync("count-rows", doc, "human");
                var v = await McpTools.ComposeTask(engine, workflow: "count-rows", inputs: Json(new { table = "table:Sales" }));
                Assert.Equal("table:Sales", v.Questions.Single(q => q.Name == "target").Answer);

                var ran = await McpTools.RunDraft(engine, v.DraftId);
                Assert.Equal(DraftStates.Done, ran.State);
                Assert.True(await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("Rows")));
            }
        }

        // A default that names an answer nobody has given yet stays unanswered, never the literal token, and fills in
        // once the answer arrives.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_semanticus_default_that_names_an_open_question_waits_for_it()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string doc = "---\nschemaVersion: 3\nname: count-rows2\ntitle: Count rows\n---\n"
                    + "## Step 1: Add the measure Rows\n```yaml step\nid: rows\nperformer: semanticus\naction:\n  op: create_measure\n"
                    + "  tableRef: \"{{questions.target}}\"\n  name: Rows\n  expression: COUNTROWS ( Sales )\n"
                    + "questions:\n  - name: pick\n    question: Which table?\n    type: enum\n    by: you\n    values: [\"table:Sales\", \"table:Date\"]\n"
                    + "  - name: target\n    question: Which table gets it?\n    type: objectRef\n    by: semanticus\n"
                    + "    default: \"{{questions.pick}}\"\n```\n";
                await engine.SaveWorkflowAsync("count-rows2", doc, "human");
                var v = await McpTools.ComposeTask(engine, workflow: "count-rows2");
                Assert.Null(v.Questions.Single(q => q.Name == "target").Answer);

                var answered = await engine.EditDraftAsync(v.DraftId, "answer", null, null, "pick", "table:Date", "human");
                Assert.Equal("table:Date", answered.Questions.Single(q => q.Name == "target").Answer);
            }
        }

        // ---- the after-check controls: Try again and Keep it anyway ----------------------------------------------

        private static string DescribeThenRefresh() => JsonSerializer.Serialize(new object[]
        {
            new { id = "describe", title = "Describe Amount", op = "set_description",
                  args = new { objRef = "column:Sales/Amount", text = "What the line sold for." } },
            new { id = "load", title = "Load the Sales data", op = "refresh_partition", args = new { partitionRef = "partition:Sales/Sales" } },
        });

        // Sol F-269 follow-up: an after step that did not run could only be run by hand. Try again reruns the after steps
        // that did not run, and nothing else: the change is not made twice.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Try_again_reruns_only_the_after_steps_that_did_not_run()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var calls = 0;
                var commits = false;
                engine.DraftAfterOpForTest = (op, json) => { calls++; return Task.FromResult<object>(new RefreshReport { Committed = commits }); };
                var v = await McpTools.ComposeTask(engine, JsonDocument.Parse(DescribeThenRefresh()).RootElement, title: "Describe and load");
                var ran = await McpTools.RunDraft(engine, v.DraftId);
                Assert.Equal(DraftStates.Done, ran.State);
                Assert.True(ran.CanTryAgain);
                Assert.Equal(PlanStateWords.Checking, ran.PlanWord);
                var undo = await ComposerFrame.UndoStepsAsync(sm);

                commits = true;
                var again = await McpTools.EditDraft(engine, v.DraftId, "try_again");
                Assert.Equal(2, calls);
                Assert.False(again.CanTryAgain);
                Assert.Equal("Done", again.After.Single().Status);
                Assert.Equal(PlanStateWords.Done, again.PlanWord);
                Assert.Equal(undo, await ComposerFrame.UndoStepsAsync(sm));

                var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => McpTools.EditDraft(engine, v.DraftId, "try_again"));
                Assert.Contains("Nothing is left to try again", ex.Message);
            }
        }

        private static string MeasureWithRange() => JsonSerializer.Serialize(new object[]
        {
            new { id = "rate", title = "Add the measure Rate", op = "create_measure",
                  args = new { tableRef = "table:Sales", name = "Rate", expression = "DIVIDE ( 1, 2 )" },
                  checks = new[] { new { kind = "result_range", phase = "after", min = 0, max = 1 } } },
        });

        // Composer contract 10.3: on a failed after-check the person may Keep it anyway. The row then reads Done and says
        // the person kept it; the model is not touched.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Keep_it_anyway_closes_a_failed_after_check_without_touching_the_model()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                engine.DraftProbeForTest = _ => Task.FromResult<double?>(1.34);
                var v = await McpTools.ComposeTask(engine, JsonDocument.Parse(MeasureWithRange()).RootElement, title: "Rate");
                var ran = await McpTools.RunDraft(engine, v.DraftId);
                Assert.Equal("check_failed", ran.Banner.Kind);
                Assert.Equal(PlanStateWords.NeedsYou, ran.PlanWord);
                var undo = await ComposerFrame.UndoStepsAsync(sm);

                var kept = await engine.EditDraftAsync(v.DraftId, "keep_anyway", null, null, null, null, "human");
                Assert.Null(kept.Banner);
                Assert.Equal(PlanStateWords.Done, kept.PlanWord);
                Assert.Contains(DraftCopy.KeptAnyway, kept.Notes);
                Assert.Equal(undo, await ComposerFrame.UndoStepsAsync(sm));
                Assert.True(await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("Rate")));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Keep_it_anyway_is_refused_when_no_check_failed()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await McpTools.ComposeTask(engine, JsonDocument.Parse(DescribeThenRefresh()).RootElement, title: "Describe");
                var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => engine.EditDraftAsync(v.DraftId, "keep_anyway", null, null, null, null, "human"));
                Assert.Contains("No check failed", ex.Message);
            }
        }

        // ---- Copy the request (composer contract 9) -----------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_waiting_part_carries_the_request_a_person_can_copy()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await McpTools.ComposeTask(engine, JsonDocument.Parse(DraftFrame.Steps(after: false)).RootElement, title: DraftFrame.Title);
                Assert.StartsWith(DraftCopy.CopyHead(2, DraftFrame.Title) + "\n", v.RequestText);
                var formula = v.Blanks.Single(b => b.Name == "formula");
                Assert.StartsWith(DraftCopy.CopyHead(1, DraftFrame.Title) + "\n", formula.Request);
                Assert.Contains("Brief: Compare returned units to units sold", formula.Request);
                Assert.Contains("Submit the text with complete_request", formula.Request);

                // Once a part is written it has nothing to copy.
                await engine.EditDraftAsync(v.DraftId, "write", "describe", "description", null, DraftFrame.Description, "human");
                var now = await engine.GetDraftAsync(v.DraftId);
                Assert.Null(now.Blanks.Single(b => b.Name == "description").Request);
                Assert.StartsWith(DraftCopy.CopyHead(1, DraftFrame.Title) + "\n", now.RequestText);
            }
        }

        // ---- the options a "You choose" question lists (composer contract 1.4 and 2.2) --------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_question_from_a_model_list_carries_its_options()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string doc = "---\nschemaVersion: 3\nname: pick-table\ntitle: Pick a table\n---\n"
                    + "## Step 1: Add the measure Rows\n```yaml step\nid: rows\nperformer: semanticus\naction:\n  op: create_measure\n"
                    + "  tableRef: \"{{questions.table}}\"\n  name: Rows\n  expression: COUNTROWS ( Sales )\n"
                    + "questions:\n  - name: table\n    question: Which table?\n    type: objectRef\n    by: you\n    from: tables\n```\n";
                await engine.SaveWorkflowAsync("pick-table", doc, "human");
                var v = await McpTools.ComposeTask(engine, workflow: "pick-table");
                var q = v.Questions.Single(x => x.Name == "table");
                Assert.Equal(new[] { "table:Date", "table:Sales" }, q.Options.Select(o => o.Ref).OrderBy(r => r, StringComparer.Ordinal));
                Assert.Contains(q.Options, o => o.Ref == "table:Sales" && o.Label == "Sales");
            }
        }

        // ---- L5a: a draft from words opens as the menu --------------------------------------------------------------

        // Composer contract 8, "below the bar, the menu": Jev is not live, so a draft from words opens with no steps
        // chosen and the matching steps listed to pick from, with the engine's line. Nothing is guessed.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_draft_from_words_opens_as_the_menu_with_the_matching_steps_first()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await engine.ComposeTaskAsync("Add a measure for total quantity and describe it", null, null, null, null, "human");
                Assert.Empty(v.Steps);
                // Honesty lane: Jev is never asked here, so the line claims no ask. Pinned byte for byte.
                Assert.Equal("Pick the steps. The ones your words name come first.", v.MenuLine);
                Assert.Equal(DraftCopy.PickTheSteps, v.Summary);
                Assert.DoesNotContain("Jev", v.MenuLine, StringComparison.Ordinal);
                Assert.Equal(DraftStates.NeedsYou, v.State);
                Assert.Equal("From your words", v.Source);
                var first = v.Menu.Take(2).Select(m => m.Id).ToArray();
                Assert.Contains("add-measure", first);
                Assert.Contains("describe", first);
                Assert.True(v.Menu.First().Matched);
                Assert.Contains(v.Menu, m => !m.Matched);          // the rest stay listed, below the matches

                // Refine keeps what was picked and lists again with the new words.
                var picked = await engine.EditDraftAsync(v.DraftId, "add", null, null, null, "add-measure", "human");
                Assert.Single(picked.Steps);
                var refined = await engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "and hide the key column", "human");
                Assert.Single(refined.Steps);
                Assert.Equal("hide", refined.Menu.First().Id);
                Assert.Contains("hide the key column", refined.Words);
            }
        }

        // A6: the menu and Describe it read words with ONE matcher. The menu counted raw substrings, so "separate" and
        // "create" hit add-measure's "rate", and a slip ("meaure") was never read as measure there, though Best read it.
        [Fact]
        public async Task The_menu_reads_words_the_way_Describe_it_does()
        {
            Assert.DoesNotContain(DraftMenu.For("separate the Sales columns", null), m => m.Id == "add-measure" && m.Matched);
            Assert.DoesNotContain(DraftMenu.For("create it", null), m => m.Id == "add-measure" && m.Matched);
            var slip = DraftMenu.For("create a meaure", null);
            Assert.Equal("add-measure", slip.First().Id);
            Assert.True(slip.First().Matched);
            Assert.Single(slip, m => m.Matched);

            // The Plan's door: compose_task with the typed words lists add-measure first.
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await McpTools.ComposeTask(engine, words: "create a meaure");
                Assert.Equal("add-measure", v.Menu.First().Id);
                Assert.True(v.Menu.First().Matched);
            }
        }

        // L5a finish line: words drafting from a scripted MCP client produces a menu draft that L4's run path accepts.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_menu_draft_from_the_agent_door_runs_as_one_change()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await McpTools.ComposeTask(engine, words: "add a measure with the total quantity");
                Assert.Equal(DraftCopy.PickTheSteps, v.MenuLine);
                var added = await McpTools.EditDraft(engine, v.DraftId, "add", text: "add-measure");
                var step = added.Steps.Single();
                Assert.Contains("you", step.Tags);                  // the table is the person's choice
                Assert.Contains("asst", step.Tags);                 // the name and the formula are written
                // The table is the one question this step needs; its other one (a number you know, lane author-gaps C7)
                // is optional.
                var table = added.Questions.Single(q => q.Step == step.Id && q.Type == "objectRef");
                Assert.Contains(table.Options, o => o.Ref == "table:Sales");

                await McpTools.EditDraft(engine, v.DraftId, "answer", question: table.Name, text: "table:Sales");
                await McpTools.EditDraft(engine, v.DraftId, "write", step: step.Id, blank: "name", text: "Quantity total");
                await McpTools.EditDraft(engine, v.DraftId, "write", step: step.Id, blank: "expression", text: "SUM ( Sales[Quantity] )");
                var undo = await ComposerFrame.UndoStepsAsync(sm);
                var ran = await McpTools.RunDraft(engine, v.DraftId);
                Assert.Equal(DraftStates.Done, ran.State);
                Assert.Equal(undo + 1, await ComposerFrame.UndoStepsAsync(sm));
                Assert.Equal("SUM ( Sales[Quantity] )", await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures["Quantity total"].Expression));
            }
        }

        // Found by the U5 driver: a step whose object is still a question showed the raw token as its object line
        // ("{{questions.add-measure-tableRef}}"), and the part names built from it. A reference is never an object name.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_step_whose_object_is_still_a_question_names_no_object()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await engine.ComposeTaskAsync("add a measure", null, null, null, null, "human");
                var added = await engine.EditDraftAsync(v.DraftId, "add", null, null, null, "add-measure", "human");
                Assert.Null(added.Steps.Single().Object);
                Assert.DoesNotContain("{{", added.Steps.Single().Title);
            }
        }

        // The menu names only ops a draft runs in its change group, each with an applier, so a picked step can run.
        [Fact]
        public void Every_menu_step_is_an_allowlisted_change_op()
        {
            foreach (var t in DraftMenu.All)
            {
                Assert.Equal(OpParameterKinds.Change, OpParameterKinds.GroupOf(t.Op));
                Assert.True(ComposedOps.Kinds.ContainsKey(t.Op), t.Op + " has no applier");
                Assert.False(string.IsNullOrEmpty(DraftCopy.Changes(new[] { t.Op })), t.Op + " has no Changes words");
            }
            Assert.Equal(DraftMenu.All.Length, DraftMenu.All.Select(t => t.Id).Distinct().Count());
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Adding_a_step_that_is_not_on_the_menu_is_refused_with_the_list()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await engine.ComposeTaskAsync("describe the sales table", null, null, null, null, "human");
                var ex = await Assert.ThrowsAnyAsync<ArgumentException>(() => engine.EditDraftAsync(v.DraftId, "add", null, null, null, "deploy-everything", "human"));
                Assert.Contains("add-measure", ex.Message);
            }
        }
    }
}
