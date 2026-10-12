// Nothing stays done once its change is taken back (lane m11/uat-fix-4; Sol UAT S01-j2-007, S01-j5-004, S12-3-002),
// and what a person gives the Plan is kept as given (S12-1-005, S12-2-005).
//
// What these tests hold, one line each:
//   a task whose edits were undone is open again and offered again; redo closes it again
//   a draft whose change was undone to a point is not done and not approved; redo makes it done again; it can run again
//   a Semanticus row whose applied change the model no longer holds is Ready again, even when the model was reversed
//     by a restore and a hand edit rather than by undo
//   a 5,000-word paste keeps every word: the title is its start, the whole text is kept with the task and handed over
//   an object answer that names nothing in the model is refused and the question stays unanswered

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Xunit;
using static Semanticus.Tests.PlanQueueKit;

namespace Semanticus.Tests
{
    public sealed class UndoRederivesPlanTests
    {
        private static JsonElement Json(object o) => JsonDocument.Parse(JsonSerializer.Serialize(o)).RootElement;

        // ---- UAT-S01-j2-007: a task whose work was undone -------------------------------------------------------

        [Fact]
        public async Task A_finding_task_whose_edit_is_undone_is_offered_again_and_redo_closes_it_again()
        {
            using var f = await GappyAsync("UndoTask.bim");
            var added = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest
            {
                Words = "Describe Total Sales",
                SourcePage = "readiness",
                OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Finding, Ref = "DESC-MEASURE|measure:Sales/Total Sales" },
            });
            var id = added.ItemId;
            Assert.Equal(PlanDoneWhen.ModelCheck, Row(added.Plan, id).DoneWhen);

            // The assistant claims it, fixes it through the MCP door (the edit is filed under the task), and completes it.
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.True(claim.Ok, claim.Message);
            await CallAsync(f.Engine, () => McpTools.SetDescription(f.Engine, "measure:Sales/Total Sales", "All sales, in dollars."));
            var done = await f.Engine.CompleteAssistantRequestAsync(id, claim.Request.ClaimToken, "Described it.");
            Assert.True(done.Ok, done.Message);
            Assert.Equal(PlanStateWords.Done, Row(await f.Engine.GetModelPlanAsync(), id).StateWord);
            Assert.DoesNotContain((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == id);

            // History: Undo to here on the task's edit. The description is gone, so the finding is back.
            var edit = f.Sessions.Ledger.ForModel(f.Sessions.Current.Id)
                .Last(e => e.Cause?.Kind == ChangeCauseKinds.Task && e.Cause.Ids.Contains(id));
            await f.Engine.UndoToEntryAsync(edit.EntryId, "human");
            Assert.True(string.IsNullOrEmpty(await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total Sales"].Description)));

            var back = Row(await f.Engine.GetModelPlanAsync(), id);
            Assert.Equal(PlanStateWords.Ready, back.StateWord);
            Assert.NotEqual(ModelPlanStatuses.Done, back.Status);
            var offer = await f.Engine.ListAssistantRequestsAsync();
            Assert.Contains(offer.Requests, r => r.RequestId == id);
            // The fold line the assistant reads on its next call names it again.
            Assert.NotNull(await f.Engine.PullPlanOfferAsync());

            // It can be claimed and worked again.
            var again = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.True(again.Ok, again.Message);
            await f.Engine.DeclineAssistantRequestAsync(id, again.Request.ClaimToken, "Checking the redo instead.");
        }

        [Fact]
        public async Task A_task_reopened_by_undo_is_done_again_after_redo()
        {
            using var f = await GappyAsync("RedoTask.bim");
            var id = await AskAsync(f.Engine, "Describe Total Sales for the finance team", Free("Total Sales"));
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            await CallAsync(f.Engine, () => McpTools.SetDescription(f.Engine, "measure:Sales/Total Sales", "All sales, in dollars."));
            Assert.True((await f.Engine.CompleteAssistantRequestAsync(id, claim.Request.ClaimToken, "Described it.")).Ok);
            Assert.Equal(PlanStateWords.Done, Row(await f.Engine.GetModelPlanAsync(), id).StateWord);

            await f.Engine.UndoAsync("human");
            Assert.Equal(PlanStateWords.Ready, Row(await f.Engine.GetModelPlanAsync(), id).StateWord);
            Assert.Contains((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == id);

            await f.Engine.RedoAsync("human");
            var redone = Row(await f.Engine.GetModelPlanAsync(), id);
            Assert.Equal(PlanStateWords.Done, redone.StateWord);
            Assert.Equal("Described it.", redone.Reply);
            Assert.DoesNotContain((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == id);
        }

        // ---- UAT-S12-3-002: a draft whose change was undone to a point -------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_draft_undone_to_a_point_is_not_done_or_approved_and_redo_makes_it_done_again()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "sem-undo-draft-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true, workspace: workspace);
            using (sm)
            {
                // The edit before the draft: History's "Undo to here" is pressed on this one.
                await engine.SetDescriptionAsync("table:Sales", "One row per sales line.", "human");
                var before = sm.Ledger.ForModel(sm.Current.Id).Last(e => e.Cause == null && e.Deltas.Any(d => d.Ref == "table:Sales"));

                var steps = Json(new object[]
                {
                    new { id = "chaos", title = "Add Chaos Check", op = "create_measure",
                          args = new { tableRef = "table:Sales", name = "Chaos Check", expression = "1" } },
                });
                var v = await McpTools.ComposeTask(engine, steps, title: "Chaos Check");
                var ran = await new EngineRpcTarget(engine).runDraft(v.DraftId);
                Assert.Equal(DraftStates.Done, ran.State);
                Assert.True(ran.Approved);
                Assert.True(await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("Chaos Check")));
                var drafted = sm.Ledger.ForModel(sm.Current.Id).Last(e => e.Cause?.Kind == ChangeCauseKinds.Draft);

                await engine.UndoToEntryAsync(before.EntryId, "human");
                Assert.False(await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("Chaos Check")));

                // Both doors read the same answer: not done, not approved, and the Plan row is not Done.
                foreach (var read in new[] { await McpTools.GetDraft(engine, v.DraftId), await new EngineRpcTarget(engine).getDraft(v.DraftId) })
                {
                    Assert.NotEqual(DraftStates.Done, read.State);
                    Assert.False(read.Approved);
                    Assert.NotEqual(PlanStateWords.Done, read.PlanWord);
                    Assert.NotEqual(PlanStateWords.Checking, read.PlanWord);
                }
                var row = (await engine.GetModelPlanAsync()).Items.Single(i => i.Id == DraftRows.Prefix + v.DraftId);
                Assert.NotEqual(PlanStateWords.Done, row.StateWord);
                Assert.NotEqual(ModelPlanStatuses.Done, row.Status);

                // Redo to the draft's change: it is done again, as it was.
                await engine.RedoToEntryAsync(drafted.EntryId, "human");
                Assert.True(await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("Chaos Check")));
                Assert.Equal(DraftStates.Done, (await engine.GetDraftAsync(v.DraftId)).State);

                // Undone again, it can be run again: "Undo the change first to run it again" is now true.
                await engine.UndoAsync("human");
                var again = await McpTools.RunDraft(engine, v.DraftId);
                Assert.Equal(DraftStates.Done, again.State);
                Assert.True(await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("Chaos Check")));
            }
        }

        // ---- UAT-S01-j5-004: a Semanticus row whose change the model no longer holds -----------------------------

        // S01-j5-004 reopened the saved file first (a restore); S11-1-006 showed the keys again in the same session.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task After_a_hand_edit_brings_the_gap_back_the_visibility_row_is_ready_again(bool reopen)
        {
            var tag = reopen ? "UndoVisibility" : "UndoVisibilitySame";
            using var f = await CommutationFixtures.All.Single(x => x.Name == "Wide").Copy(tag);
            var plan = await f.Engine.GetModelPlanAsync();
            foreach (var i in plan.Items.Where(i => i.Doer != PlanDoers.Semanticus
                                                   && (i.Status == ModelPlanStatuses.Ready || i.Status == ModelPlanStatuses.Proposed)))
                await f.Engine.SkipModelPlanItemAsync(i.Id, "out of the way for this test", "human");
            plan = await f.Engine.GetModelPlanAsync();
            Assert.Equal(PlanStateWords.Ready, plan.Items.Single(i => i.Id == "safefix/Visibility").StateWord);

            static Task<string[]> HiddenAsync(DecisionFixture x) => x.Sessions.Current.ReadAsync(m =>
                m.Tables.SelectMany(t => t.Columns).Where(c => c.IsHidden).Select(c => "column:" + c.Table.Name + "/" + c.Name).ToArray());
            var hiddenBefore = await HiddenAsync(f);
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.True(ran.Performed, ran.Sentence);
            Assert.Contains("safefix/Visibility", ran.ItemIds);
            var hidByRun = (await HiddenAsync(f)).Except(hiddenBefore).ToArray();
            Assert.NotEmpty(hidByRun);

            // Save, open the saved file again (a new session, as Restore makes), and show the keys again by hand.
            if (reopen)
            {
                var path = Path.Combine(f.Workspace, tag + ".bim");
                await f.Engine.SaveAsync(path, "bim", false, "human");
                await f.Engine.OpenAsync(path, true);
            }
            foreach (var c in hidByRun)
                await f.Engine.SetColumnMetadataAsync(c, false, null, null, null, "agent");

            var row = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "safefix/Visibility");
            Assert.NotEqual(ModelPlanItemStates.Applied, row.State);
            Assert.NotEqual(PlanStateWords.Checking, row.StateWord);
            Assert.Equal(PlanStateWords.Ready, row.StateWord);
        }

        // ---- UAT-S12-3-003 and S12-4-003: an undone edit a new edit cleared from redo ---------------------------

        /// <summary>An undo, then a new edit: TE2 clears the redo branch, so the undone edits can never come back. History
        /// showed them as ordinary, active edits (no strike, no control) while the model no longer had them. They are
        /// undone, and they are not redoable.</summary>
        [Fact]
        public async Task An_undone_edit_that_a_new_edit_cleared_from_redo_still_reads_undone_in_history()
        {
            using var f = await GappyAsync("UndoGone.bim");
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "First words.", "human");
            var first = f.Sessions.Ledger.ForModel(f.Sessions.Current.Id).Last(e => e.Deltas.Any(d => d.Ref == "measure:Sales/Total Sales"));
            await McpTools.UndoChange(f.Engine);                                   // the agent door
            await f.Engine.SetDescriptionAsync("table:Customer", "One row per customer.", "human");   // a new edit clears redo

            var history = await f.Engine.GetPlanHistoryAsync();
            var row = history.Rows.Single(r => r.Id == first.EntryId);
            Assert.True(row.Undone, "an edit the model no longer has reads undone");
            Assert.False(row.Redoable);
            Assert.False(row.Undoable);
            Assert.True(string.IsNullOrEmpty(await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total Sales"].Description)));
        }

        // ---- UAT-S12-1-005: a long paste keeps every word ---------------------------------------------------------

        [Fact]
        public async Task A_five_thousand_word_paste_keeps_every_word_and_says_how()
        {
            using var f = await GappyAsync("LongPaste.bim");
            var text = string.Join(" ", Enumerable.Range(1, 5000).Select(i => "word" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Assert.True(text.Length > PlanTasks.MaxWordsChars);

            var added = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = text });
            var row = Row(added.Plan, added.ItemId);
            Assert.True(row.Title.Length <= PlanTasks.MaxWordsChars);
            Assert.StartsWith("word1 word2 word3", row.Title, StringComparison.Ordinal);
            Assert.Equal(text, row.FullText);
            Assert.Contains("keeps all of your text", added.Sentence, StringComparison.Ordinal);

            // The assistant gets every word when it claims the task.
            var claim = await f.Engine.ClaimAssistantRequestAsync(added.ItemId);
            Assert.True(claim.Ok, claim.Message);
            Assert.Equal(text, claim.Request.FullText);

            // A short task keeps its words as its title and carries no second copy.
            var shortOne = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = "Add a Store hierarchy" });
            Assert.Null(Row(shortOne.Plan, shortOne.ItemId).FullText);

            // Past the cap nothing is cut: the add is refused, plainly, and names the limit.
            var tooLong = new string('x', PlanTasks.MaxFullTextChars + 1);
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = tooLong }));
            Assert.Contains("100,000", ex.Message, StringComparison.Ordinal);
        }

        // ---- N15: line breaks a person typed are kept ---------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_task_typed_on_several_lines_keeps_its_line_breaks()
        {
            using var f = await GappyAsync("LineBreaks.bim");
            var text = "Add a Store hierarchy" + (char)10 + "Country, then City, then Store" + (char)10 + (char)10 + "Hide the keys";
            var added = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = text });
            var row = Row(added.Plan, added.ItemId);
            // The title is one line; the words as typed, breaks and all, are kept beside it.
            Assert.DoesNotContain((char)10, row.Title);
            Assert.StartsWith("Add a Store hierarchy Country", row.Title, StringComparison.Ordinal);
            Assert.Equal(text, row.FullText);
        }

        // ---- the same lost-work class on a draft from words (coordinator, after S12-1-005) ------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_draft_from_long_words_keeps_every_word_and_refuses_only_past_the_limit()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "sem-longdraft-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true, workspace: workspace);
            using (sm)
            {
                var text = string.Join(" ", Enumerable.Range(1, 5000).Select(i => "word" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                var v = await McpTools.ComposeTask(engine, words: text);
                Assert.Equal(text, v.Words);
                Assert.True(v.Title.Length <= PlanTasks.MaxWordsChars);
                Assert.StartsWith("word1 word2 word3", v.Title, StringComparison.Ordinal);
                Assert.EndsWith("...", v.Title, StringComparison.Ordinal);

                // Tell Jev more keeps the old words and the new ones, whole. Re-labelled at the M11 join: the joined Plan joins them with
                // a space (as JevDraftBuilderTests pins), because the adding rule reads each typing as its own text (Draft.WordsTyped,
                // version 6 A), so the new words never join the old words' last clause there (the completeness lane's ", " is not needed).
                var more = string.Join(" ", Enumerable.Range(1, 100).Select(i => "more" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                var refined = await McpTools.EditDraft(engine, v.DraftId, "refine", text: more);
                Assert.Equal(text + " " + more, refined.Words);

                var tooLong = new string('x', PlanTasks.MaxFullTextChars + 1);
                var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.ComposeTask(engine, words: tooLong));
                Assert.Contains("100,000", ex.Message, StringComparison.Ordinal);
                var ex2 = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.EditDraft(engine, v.DraftId, "refine", text: tooLong));
                Assert.Contains("100,000", ex2.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task A_workflow_input_for_an_object_question_that_names_nothing_is_refused()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "sem-objinput-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true, workspace: workspace);
            using (sm)
            {
                const string doc = "---\nschemaVersion: 3\nname: count-rows-in\ntitle: Count rows\n"
                    + "inputs:\n  - name: table\n    question: Which table?\n    type: objectRef\n    from: tables\n---\n"
                    + "## Step 1: Add the measure Rows\n```yaml step\nid: rows\nperformer: semanticus\naction:\n  op: create_measure\n"
                    + "  tableRef: \"{{inputs.table}}\"\n  name: Rows\n  expression: COUNTROWS ( Sales )\n```\n";
                await engine.SaveWorkflowAsync("count-rows-in", doc, "human");
                var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.ComposeTask(engine, workflow: "count-rows-in", inputs: Json(new { table = "42" })));
                Assert.Contains("42", ex.Message, StringComparison.Ordinal);
                Assert.Contains("table:Sales", ex.Message, StringComparison.Ordinal);

                var ok = await McpTools.ComposeTask(engine, workflow: "count-rows-in", inputs: Json(new { table = "table:Sales" }));
                Assert.Equal("table:Sales", ok.Questions.Single(q => q.Name == "table").Answer);
            }
        }

        // ---- UAT-S12-2-005: an object answer must name an object -------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_object_answer_that_names_nothing_in_the_model_is_refused_and_stays_unanswered()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "sem-objref-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true, workspace: workspace);
            using (sm)
            {
                const string doc = "---\nschemaVersion: 3\nname: count-rows-pick\ntitle: Count rows\n---\n"
                    + "## Step 1: Add the measure Rows\n```yaml step\nid: rows\nperformer: semanticus\naction:\n  op: create_measure\n"
                    + "  tableRef: \"{{questions.target}}\"\n  name: Rows\n  expression: COUNTROWS ( Sales )\n"
                    + "questions:\n  - name: target\n    question: Which table gets it?\n    type: objectRef\n    by: you\n    from: tables\n```\n";
                await engine.SaveWorkflowAsync("count-rows-pick", doc, "human");
                var v = await McpTools.ComposeTask(engine, workflow: "count-rows-pick");
                Assert.Null(v.Questions.Single(q => q.Name == "target").Answer);

                var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.EditDraft(engine, v.DraftId, "answer", question: "target", text: "42"));
                Assert.Contains("42", ex.Message, StringComparison.Ordinal);
                Assert.Contains("table:Sales", ex.Message, StringComparison.Ordinal);
                var read = await engine.GetDraftAsync(v.DraftId);
                Assert.Null(read.Questions.Single(q => q.Name == "target").Answer);
                Assert.Equal(DraftStates.NeedsYou, read.State);

                // A column is an object, but not one of the tables the question lists.
                await Assert.ThrowsAsync<ArgumentException>(() => McpTools.EditDraft(engine, v.DraftId, "answer", question: "target", text: "column:Sales/Amount"));

                var ok = await McpTools.EditDraft(engine, v.DraftId, "answer", question: "target", text: "table:Sales");
                Assert.Equal("table:Sales", ok.Questions.Single(q => q.Name == "target").Answer);
            }
        }
    }
}
