using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Jev;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The frame's draft as compose_task takes it: the seven change steps and the refresh after them.</summary>
    internal static class DraftFrame
    {
        internal const string Description = "Share of units sold that came back as returns. Higher is worse.";
        internal const string Title = "Import Returns and add return measures";

        private static object A(string op, params (string k, object v)[] args) =>
            new Dictionary<string, object> { ["op"] = op, ["args"] = args.ToDictionary(a => a.k, a => a.v) };

        private static object Column(string name, string type) =>
            A("create_column", ("tableRef", "table:Returns"), ("name", name), ("dataType", type), ("sourceColumn", name));

        /// <summary>The steps, as compose_task's JSON. <paramref name="after"/> adds the refresh after the change.</summary>
        internal static string Steps(bool after = true, bool rangeCheck = true)
        {
            var import = new List<object> { A("create_import_table", ("name", "Returns"), ("mExpression", ComposerFrame.ReturnsM)) };
            foreach (var c in new[] { "ReturnKey", "SalesLineKey", "ReturnDate", "Quantity", "RefundAmount", "Reason", "Channel" })
                import.Add(Column(c, c == "SalesLineKey" ? "Int64" : "String"));
            var rateChecks = new List<object>
            {
                new { kind = "valid_dax", phase = "written" },
                new { kind = "refs_within", phase = "written", of = new[] { "Returns", "Sales" } },
            };
            if (rangeCheck) rateChecks.Add(new { kind = "result_range", phase = "after", min = 0, max = 1 });
            var steps = new List<object>
            {
                new { id = "import", title = "Import the Returns table", actions = import },
                new { id = "rel-sales", title = "Relate Returns to Sales", op = "create_relationship",
                      args = new { fromColumnRef = "column:Returns/SalesLineKey", toColumnRef = "column:Sales/SalesLineKey" } },
                new { id = "rel-date", title = "Relate Returns to Date", op = "create_relationship",
                      args = new { fromColumnRef = "column:Returns/ReturnDate", toColumnRef = "column:Date/Date" } },
                new { id = "types", title = "Set data types on 4 columns", actions = new[]
                {
                    A("set_column_data_type", ("columnRef", "column:Returns/ReturnDate"), ("dataType", "DateTime")),
                    A("set_column_data_type", ("columnRef", "column:Returns/Quantity"), ("dataType", "Int64")),
                    A("set_column_data_type", ("columnRef", "column:Returns/RefundAmount"), ("dataType", "Decimal")),
                    A("set_column_data_type", ("columnRef", "column:Returns/ReturnKey"), ("dataType", "Int64")),
                } },
                new { id = "return-rate", title = "Add the measure Return rate", actions = new[]
                {
                    A("create_measure", ("tableRef", "table:Returns"), ("name", "Return rate"), ("expression", "{{blanks.formula}}"), ("displayFolder", "Returns")),
                    A("set_measure_format", ("objRef", "measure:Returns/Return rate"), ("formatString", "0.0%")),
                }, blanks = new[] { new { name = "formula", kind = "dax", fills = "expression" } }, checks = rateChecks,
                   brief = "Compare returned units to units sold for the same period. Count each return once." },
                new { id = "refund-total", title = "Add the measure Refund total", op = "starter_measure",
                      args = new { tableRef = "table:Returns", column = "RefundAmount", option = "sum", name = "Refund total" } },
                new { id = "describe", title = "Describe Return rate", op = "set_description",
                      args = new { objRef = "measure:Returns/Return rate", text = "{{blanks.description}}" },
                      blanks = new[] { new { name = "description", kind = "description", fills = "text", maxChars = 200 } },
                      checks = new[] { new { kind = "max_chars", phase = "written" }, new { kind = "no_formula_text", phase = "written" } } },
            };
            if (after)
                steps.Add(new { id = "load", title = "Load the Returns data", op = "refresh_partition",
                                args = new { partitionRef = "partition:Returns/Returns", commit = true } });
            return JsonSerializer.Serialize(steps);
        }

        internal static ModelPlanItem Row(ModelPlanView plan, string draftId) =>
            plan.Items.Single(i => i.Id == "draft/" + draftId);

        internal static async Task<string> WordAsync(LocalEngine engine, string draftId) =>
            Row(await engine.GetModelPlanAsync(), draftId).StateWord;

        /// <summary>The Plan task that carries a blank's request.</summary>
        internal static string RequestOf(DraftView v, string blank) => v.Blanks.Single(b => b.Name == blank).RequestId;
    }

    /// <summary>
    /// Composer contract 4.1 and 5.4, Plan-heart contract 14: the six run states and their precedence, the held
    /// drafts, the drop cascade, evidence that moved, and the one word the Plan row shows at each state.
    /// </summary>
    public sealed class DraftStateTests
    {
        /// <summary>The frame's model on a Pro engine with a workspace, so a workflow file has somewhere to live. Not a
        /// "semanticus-" temp name: that prefix reads as an ephemeral anchor.</summary>
        private static async Task<(LocalEngine engine, SessionManager sm)> OpenAsync()
        {
            var workspace = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sem-drafts-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(workspace);
            return await ComposerFrame.OpenAsync(pro: true, workspace: workspace);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Needs_you_wins_over_waiting_and_waiting_over_ready()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                // Composed from the agent door: the blanks go on the queue at once.
                var v = await McpTools.ComposeTask(engine, JsonDocument.Parse(DraftFrame.Steps(after: false)).RootElement, title: DraftFrame.Title);
                Assert.Equal(DraftStates.WaitingForAssistant, v.State);
                Assert.Equal(2, v.PartsWaiting);
                Assert.Equal(PlanStateWords.Ready, v.PlanWord);                 // Ready, on its next call
                Assert.Equal(PlanTasks.OnNextCallNote, v.PlanNote);

                // A part declined with choices needs the person, which beats the part still waiting.
                var list = await engine.ListAssistantRequestsAsync();
                var desc = list.Requests.Single(r => r.RequestId == DraftFrame.RequestOf(v, "description"));
                var claim = await engine.ClaimAssistantRequestAsync(desc.RequestId);
                var declined = await engine.DeclineAssistantRequestAsync(desc.RequestId, claim.Request.ClaimToken, "Which measure should it describe?");
                Assert.True(declined.Ok, declined.Message);
                var now = await engine.GetDraftAsync(v.DraftId);
                Assert.Equal(DraftStates.NeedsYou, now.State);
                Assert.Equal("Your assistant asks: Which measure should it describe?", now.PlanNote);
                Assert.Equal(PlanStateWords.NeedsYou, await DraftFrame.WordAsync(engine, v.DraftId));

                // Write it yourself clears the need; the formula still waits for the assistant.
                await engine.EditDraftAsync(v.DraftId, "write", "describe", "description", null, DraftFrame.Description, "human");
                Assert.Equal(DraftStates.WaitingForAssistant, (await engine.GetDraftAsync(v.DraftId)).State);

                // The formula passes: not approved, so Ready with Approve and run, and nothing changed.
                var formula = DraftFrame.RequestOf(now, "formula");
                var f = await engine.ClaimAssistantRequestAsync(formula);
                var done = await engine.CompleteAssistantRequestAsync(formula, f.Request.ClaimToken, ComposerFrame.ReturnRate);
                Assert.True(done.Ok, done.Message);
                var ready = await engine.GetDraftAsync(v.DraftId);
                Assert.Equal(DraftStates.Ready, ready.State);
                Assert.Equal(DraftCopy.ReadySentence, ready.Summary);
                Assert.Equal(PlanStateWords.Ready, await DraftFrame.WordAsync(engine, v.DraftId));
                Assert.False(await ComposerFrame.HasTableAsync(sm, "Returns"));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Approved_and_the_last_part_passing_runs_it_by_itself_as_one_undo_entry()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await McpTools.ComposeTask(engine, JsonDocument.Parse(DraftFrame.Steps(after: false)).RootElement, title: DraftFrame.Title);
                var undoBefore = await ComposerFrame.UndoStepsAsync(sm);
                var approved = await McpTools.RunDraft(engine, v.DraftId);
                Assert.True(approved.Approved);
                Assert.Equal(DraftStates.WaitingForAssistant, approved.State);   // approval recorded, nothing ran

                foreach (var (blank, text) in new[] { ("formula", ComposerFrame.ReturnRate), ("description", DraftFrame.Description) })
                {
                    var id = DraftFrame.RequestOf(approved, blank);
                    var c = await engine.ClaimAssistantRequestAsync(id);
                    Assert.True((await engine.CompleteAssistantRequestAsync(id, c.Request.ClaimToken, text)).Ok);
                }

                var after = await engine.GetDraftAsync(v.DraftId);
                Assert.Equal(DraftStates.Done, after.State);
                Assert.True(await ComposerFrame.HasTableAsync(sm, "Returns"));
                Assert.Equal(undoBefore + 1, await ComposerFrame.UndoStepsAsync(sm));
                await engine.UndoAsync("human");
                Assert.False(await ComposerFrame.HasTableAsync(sm, "Returns"));
            }
        }

        private static string RowFilterSteps(string filter) => JsonSerializer.Serialize(new object[]
        {
            new { id = "role", title = "Add the role Readers", op = "create_role", args = new { name = "Readers" } },
            new { id = "filter", title = "Filter Sales for Readers", op = "set_table_permission",
                  args = new { roleName = "Readers", tableRef = "table:Sales", filterDax = filter } },
        });

        // Decision 2's first exception: a kept row filter never runs by itself, however its filter was supplied, and
        // run_draft refuses it on the agent door. Only the person's Approve and run, on the human-only target, runs it.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_draft_with_a_literal_row_filter_is_held_and_only_the_person_runs_it()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await McpTools.ComposeTask(engine, JsonDocument.Parse(RowFilterSteps("[Quantity] > 0")).RootElement, title: "Readers see sold rows");
                Assert.Equal(DraftStates.NeedsYou, v.State);
                Assert.Equal(DraftCopy.HeldRowSecurity, v.PlanNote);
                Assert.Equal(DraftCopy.HeldRowSecurity, v.Held);

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.RunDraft(engine, v.DraftId));
                Assert.Equal(DraftCopy.HeldRefusal(DraftHolds.RowSecurity), ex.Message);
                // The UI door's run_draft is refused too: the approval is the person's own, on the human-only target.
                await Assert.ThrowsAsync<InvalidOperationException>(() => new EngineRpcTarget(engine).runDraft(v.DraftId));
                Assert.Equal(0, await sm.Current.ReadAsync(m => m.Roles.Count));

                var ran = await new HumanGovernanceRpcTarget(engine).approveDraft(v.DraftId);
                Assert.Equal(DraftStates.Done, ran.State);
                Assert.Equal("[Quantity] > 0", await sm.Current.ReadAsync(m => m.Roles["Readers"].RowLevelSecurity["Sales"]));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Dropping_a_step_drops_the_steps_that_need_it_and_says_so()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var v = await new EngineRpcTarget(engine).composeTask(stepsJson: DraftFrame.Steps(after: false), title: DraftFrame.Title);
                Assert.Equal(DraftStates.NeedsYou, v.State);            // composed in Studio: nobody asked yet
                Assert.Equal(DraftCopy.NotSentYet, v.PlanNote);

                var dropped = await engine.EditDraftAsync(v.DraftId, "drop", "return-rate", null, null, null, "human");
                Assert.Equal("Dropped step 5. Also dropped step 7, which need it.", dropped.Sentence);
                var describe = dropped.Steps.Single(s => s.Id == "describe");
                Assert.False(describe.Kept);
                Assert.Equal("Also dropped: needs step 5", describe.Note);
                Assert.Equal("Dropped", describe.Status);
                // Only the description's part and the formula's part went with them: no part is left to write.
                Assert.Equal(DraftStates.Ready, dropped.State);

                var kept = await engine.EditDraftAsync(v.DraftId, "keep", "5", null, null, null, "human");
                Assert.True(kept.Steps.Single(s => s.Id == "describe").Kept);
            }
        }

        // A part that passed while it waited is checked again when the draft runs; the model moved under it, so it
        // goes back to the person and nothing runs.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Evidence_that_moved_while_it_waited_sends_it_to_needs_you()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var steps = JsonSerializer.Serialize(new object[]
                {
                    new { id = "rate", title = "Add Sales rate", op = "create_measure",
                          args = new { tableRef = "table:Sales", name = "Sales rate", expression = "{{blanks.formula}}" },
                          blanks = new[] { new { name = "formula", kind = "dax", fills = "expression" } } },
                });
                var rpc = new EngineRpcTarget(engine);
                var v = await rpc.composeTask(stepsJson: steps, title: "Add Sales rate");
                await rpc.editDraft(v.DraftId, "write", "rate", "formula", text: "SUM ( Sales[Quantity] )");
                Assert.Equal(DraftStates.Ready, (await rpc.getDraft(v.DraftId)).State);

                await engine.DeleteObjectAsync("column:Sales/Quantity", "human");
                var run = await rpc.runDraft(v.DraftId);

                Assert.Equal(DraftStates.NeedsYou, run.State);
                Assert.StartsWith("Sales changed while this waited.", run.PlanNote);
                Assert.DoesNotContain("Sales rate", await sm.Current.ReadAsync(m => m.AllMeasures.Select(x => x.Name).ToArray()));
            }
        }

        // Composer contract 3.4: a direct assistant step submitting three writes leaves exactly one new undo entry, and
        // one Undo removes all three.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_direct_step_submitting_three_writes_is_one_undo_entry()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string doc = "---\nschemaVersion: 3\nname: tidy-sales\ntitle: Tidy Sales\n---\n"
                    + "## Step 1: Tidy the Sales columns\n```yaml step\nid: tidy\nperformer: assistant\n```\nHide the keys and describe the amount.\n";
                await engine.SaveWorkflowAsync("tidy-sales", doc, "human");
                var run = await engine.StartWorkflowAsync("tidy-sales", "agent");
                var undoBefore = await ComposerFrame.UndoStepsAsync(sm);
                var writes = JsonSerializer.Serialize(new object[]
                {
                    new { op = "set_column_hidden", args = new { objRef = "column:Sales/SalesLineKey", hidden = true } },
                    new { op = "set_description", args = new { objRef = "column:Sales/Amount", text = "What the line sold for." } },
                    new { op = "set_display_folder", args = new { refs = new[] { "column:Sales/Quantity" }, folder = "Counts" } },
                });
                var seen = new List<ChangeNotification>();
                Action<ChangeNotification> handler = n => { if (n.Deltas?.Length > 0) seen.Add(n); };
                sm.Bus.Changed += handler;
                try { await engine.SubmitWorkflowStepAsync(run.RunId, "tidy", null, "agent", null, writes); }
                finally { sm.Bus.Changed -= handler; }

                Assert.Equal(undoBefore + 1, await ComposerFrame.UndoStepsAsync(sm));
                var commit = Assert.Single(seen);
                Assert.Equal("Tidy the Sales columns", commit.Label);
                Assert.Equal("workflow", commit.By);
                Assert.Equal(run.RunId, commit.Cause.Ids.Single());
                Assert.True(await sm.Current.ReadAsync(m => m.Tables["Sales"].Columns["SalesLineKey"].IsHidden));

                await engine.UndoAsync("human");
                var back = await sm.Current.ReadAsync(m => (m.Tables["Sales"].Columns["SalesLineKey"].IsHidden,
                    m.Tables["Sales"].Columns["Amount"].Description, m.Tables["Sales"].Columns["Quantity"].DisplayFolder));
                Assert.Equal((false, "", ""), back);
            }
        }

        private static async Task<string> SalesShapeAsync(SessionManager sm) => await sm.Current.ReadAsync(m => string.Join("|",
            m.Tables["Sales"].Columns.Select(c => c.Name + ":" + c.IsHidden + ":" + c.Description + ":" + c.DisplayFolder))
            + "|roles:" + m.Roles.Count + "|measures:" + m.AllMeasures.Count());

        // Sol 6 slice 8 review, finding 1 (F-267): a direct assistant step may not write a row filter. Composer contract
        // 5.4 holds every set_table_permission for the person's own Approve and run, and a direct step has no such
        // approval, so the step is refused and nothing is written. The composed path itself refuses a row filter that
        // no person approved, so no other caller can reach it either.
        [Fact]
        public async Task A_direct_step_cannot_write_a_row_filter_without_the_person()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string doc = "---\nschemaVersion: 3\nname: secure-sales\ntitle: Secure Sales\n---\n"
                    + "## Step 1: Filter Sales for Readers\n```yaml step\nid: secure\nperformer: assistant\n```\nAdd the role and its filter.\n";
                await engine.SaveWorkflowAsync("secure-sales", doc, "human");
                var run = await engine.StartWorkflowAsync("secure-sales", "agent");
                var undoBefore = await ComposerFrame.UndoStepsAsync(sm);
                var shapeBefore = await SalesShapeAsync(sm);
                var writes = JsonSerializer.Serialize(new object[]
                {
                    new { op = "create_role", args = new { name = "Readers" } },
                    new { op = "set_table_permission", args = new { roleName = "Readers", tableRef = "table:Sales", filterDax = "Sales[Quantity] > 0" } },
                });

                var ex = await Assert.ThrowsAnyAsync<Exception>(() => engine.SubmitWorkflowStepAsync(run.RunId, "secure", null, "agent", null, writes));
                Assert.Contains("who can see which data", ex.Message);
                Assert.Equal(shapeBefore, await SalesShapeAsync(sm));
                Assert.Equal(undoBefore, await ComposerFrame.UndoStepsAsync(sm));
                Assert.Equal(0, (await engine.GetWorkflowRunAsync(run.RunId)).StepIndex);

                var bare = new ComposedChange { Title = "Readers see sold rows" };
                bare.Steps.Add(new ComposedStep { Id = "filter", Title = "Filter Sales for Readers", Actions =
                {
                    new ComposedAction { Op = "create_role", Args = { ["name"] = "Readers" } },
                    new ComposedAction { Op = "set_table_permission", Args = { ["roleName"] = "Readers", ["tableRef"] = "table:Sales", ["filterDax"] = "Sales[Quantity] > 0" } },
                } });
                var r = await engine.ApplyComposedChangeAsync(bare, "agent");
                Assert.NotEqual(ComposedOutcome.Applied, r.Outcome);
                Assert.False(r.Changed);
                Assert.Equal(shapeBefore, await SalesShapeAsync(sm));
                Assert.Equal(undoBefore, await ComposerFrame.UndoStepsAsync(sm));
            }
        }

        private static string RowFilterBlankSteps() => JsonSerializer.Serialize(new object[]
        {
            new { id = "role", title = "Add the role Readers", op = "create_role", args = new { name = "Readers" } },
            new { id = "filter", title = "Filter Sales for Readers", op = "set_table_permission",
                  args = new { roleName = "Readers", tableRef = "table:Sales", filterDax = "{{blanks.filter}}" },
                  blanks = new[] { new { name = "filter", kind = "dax", fills = "filterDax" } } },
        });

        private static async Task FillAsync(LocalEngine engine, string requestId, string text)
        {
            var c = await engine.ClaimAssistantRequestAsync(requestId);
            Assert.True(c.Ok, c.Message);
            var done = await engine.CompleteAssistantRequestAsync(requestId, c.Request.ClaimToken, text);
            Assert.True(done.Ok, done.Message);
        }

        // Sol 6 slice 8 review, finding 2 (F-268): the person's Approve and run on a held draft counts only once every
        // part has passed, and it stands only for the content the person read. Pressed while the filter is still
        // being written, it sends the parts but approves nothing, so the filter the assistant writes later does not
        // run by itself. And an approval whose content then changes (the filter written again after the model moved)
        // is cleared, so the new filter waits for the person too.
        [Fact]
        public async Task A_held_approval_counts_only_for_passed_parts_and_clears_when_they_change()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var person = new HumanGovernanceRpcTarget(engine);

                // Approve with the blank open, then fill it: nothing runs.
                var v = await McpTools.ComposeTask(engine, JsonDocument.Parse(RowFilterBlankSteps()).RootElement, title: "Readers see sold rows");
                var early = await person.approveDraft(v.DraftId);
                Assert.NotEqual(DraftStates.Done, early.State);
                await FillAsync(engine, DraftFrame.RequestOf(v, "filter"), "Sales[Quantity] > 0");
                var filled = await engine.GetDraftAsync(v.DraftId);
                Assert.Equal(DraftStates.NeedsYou, filled.State);
                Assert.Equal(DraftCopy.HeldRowSecurity, filled.PlanNote);
                Assert.Equal(0, await sm.Current.ReadAsync(m => m.Roles.Count));

                // Every part passed: now the person's approval counts, and it runs.
                var ran = await person.approveDraft(v.DraftId);
                Assert.Equal(DraftStates.Done, ran.State);
                Assert.Equal("Sales[Quantity] > 0", await sm.Current.ReadAsync(m => m.Roles["Readers"].RowLevelSecurity["Sales"]));
                await engine.UndoAsync("human");

                // Approved content that changes loses the approval. The filter passed and the person approved it, but
                // the model moved under it, so nothing ran and the filter went back to the assistant.
                var w = await McpTools.ComposeTask(engine, JsonDocument.Parse(RowFilterBlankSteps()).RootElement, title: "Readers see sold rows");
                await FillAsync(engine, DraftFrame.RequestOf(w, "filter"), "Sales[Quantity] > 0");
                await engine.DeleteObjectAsync("column:Sales/Quantity", "human");
                var moved = await person.approveDraft(w.DraftId);
                Assert.Equal(DraftStates.NeedsYou, moved.State);
                Assert.Equal(0, await sm.Current.ReadAsync(m => m.Roles.Count));
                var asked = await engine.EditDraftAsync(w.DraftId, "ask_again", "filter", "filter", null, null, "agent");
                await FillAsync(engine, DraftFrame.RequestOf(asked, "filter"), "Sales[Amount] > 0");
                var rewritten = await engine.GetDraftAsync(w.DraftId);
                Assert.Equal(DraftStates.NeedsYou, rewritten.State);
                Assert.Equal(DraftCopy.HeldRowSecurity, rewritten.PlanNote);
                Assert.Equal(0, await sm.Current.ReadAsync(m => m.Roles.Count));
            }
        }

        // Sol 6 slice 8 review, finding 3 (F-269): a step that leaves the model (publish, deploy, push) runs only when
        // every check before it passed. A check that could not run, before the change or after it, is not a pass
        // (composer contract 1.7), so the outward step does not run and says why in the check's own words.
        [Fact]
        public async Task An_outward_step_does_not_run_after_a_check_that_could_not_run()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                string Steps(object check) => JsonSerializer.Serialize(new object[]
                {
                    new { id = "describe", title = "Describe Amount", op = "set_description",
                          args = new { objRef = "column:Sales/Amount", text = "What the line sold for." }, checks = new[] { check } },
                    new { id = "publish", title = "Publish to test", op = "deploy_stage", args = new { stage = "test" } },
                });
                var cases = new[]
                {
                    // Before the change: a live check with no connection.
                    Steps(new { kind = "key_unique", phase = "before", target = "column:Sales/SalesLineKey" }),
                    // After the change: a live row count with no connection.
                    Steps(new { kind = "row_count", phase = "after", target = "table:Sales", min = 1 }),
                };
                foreach (var steps in cases)
                {
                    var v = await McpTools.ComposeTask(engine, JsonDocument.Parse(steps).RootElement, title: "Describe and publish");
                    await McpTools.RunDraft(engine, v.DraftId);
                    Assert.Equal("What the line sold for.", await sm.Current.ReadAsync(m => m.Tables["Sales"].Columns["Amount"].Description));
                    var d = await engine.GetDraftAsync(v.DraftId);
                    var publish = d.Record.After.Single(a => a.Op == "deploy_stage");
                    Assert.Equal(DraftAfterStatus.NotRun, publish.Status);
                    Assert.StartsWith("Did not run. Could not check: ", publish.Text);
                    await engine.UndoAsync("human");
                }
            }
        }

        // Sol 6 slice 8 review, finding 4 (F-270): a direct step's writes land only after the submission is valid. A
        // submission its gate refuses (a required answer missing) leaves the model exactly as it was and adds no undo
        // entry, and so does one whose check refuses it after the writes were tried: those are taken back.
        [Fact]
        public async Task A_refused_direct_step_submission_leaves_the_model_as_it_was()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string doc = "---\nschemaVersion: 3\nname: tidy-keys\ntitle: Tidy keys\n---\n"
                    + "## Step 1: Tidy the Sales columns\n```yaml step\nid: tidy\nperformer: assistant\n```\nHide the key.\n"
                    + "```yaml gate\ninputs:\n  - name: why\n    question: Why hide it?\n    type: text\n    required: required\n```\n";
                await engine.SaveWorkflowAsync("tidy-keys", doc, "human");
                var run = await engine.StartWorkflowAsync("tidy-keys", "agent");
                var undoBefore = await ComposerFrame.UndoStepsAsync(sm);
                var shapeBefore = await SalesShapeAsync(sm);
                var writes = JsonSerializer.Serialize(new object[]
                {
                    new { op = "set_column_hidden", args = new { objRef = "column:Sales/SalesLineKey", hidden = true } },
                    new { op = "set_description", args = new { objRef = "column:Sales/Amount", text = "What the line sold for." } },
                });

                await Assert.ThrowsAnyAsync<Exception>(() => engine.SubmitWorkflowStepAsync(run.RunId, "tidy", null, "agent", null, writes));
                Assert.Equal(shapeBefore, await SalesShapeAsync(sm));
                Assert.Equal(undoBefore, await ComposerFrame.UndoStepsAsync(sm));

                // The same writes with the answer land as one change and the step moves on.
                var answered = JsonSerializer.Serialize(new { why = "Keys are not for report users." });
                var after = await engine.SubmitWorkflowStepAsync(run.RunId, "tidy", answered, "agent", null, writes);
                Assert.Equal(undoBefore + 1, await ComposerFrame.UndoStepsAsync(sm));
                Assert.True(await sm.Current.ReadAsync(m => m.Tables["Sales"].Columns["SalesLineKey"].IsHidden));
                Assert.True(after.StepIndex >= 1 || after.Status != "active");

                // A check that refuses the step after its writes were tried takes them back.
                const string checkedDoc = "---\nschemaVersion: 3\nname: probe-keys\ntitle: Probe keys\nstrictness: hard\n---\n"
                    + "## Step 1: Describe Quantity\n```yaml step\nid: describe\nperformer: assistant\n```\nDescribe the quantity.\n"
                    + "```yaml gate\ninputs:\n  - name: probe\n    question: The value it should show.\n    type: text\n    required: required\nverify:\n  - kind: dax_probe\n    probe: probe\n```\n";
                await engine.SaveWorkflowAsync("probe-keys", checkedDoc, "human");
                var run2 = await engine.StartWorkflowAsync("probe-keys", "agent");
                var undo2 = await ComposerFrame.UndoStepsAsync(sm);
                var shape2 = await SalesShapeAsync(sm);
                var describe = JsonSerializer.Serialize(new object[]
                {
                    new { op = "set_description", args = new { objRef = "column:Sales/Quantity", text = "Units sold on the line." } },
                });
                await Assert.ThrowsAnyAsync<Exception>(() => engine.SubmitWorkflowStepAsync(run2.RunId, "describe",
                    JsonSerializer.Serialize(new { probe = "42" }), "agent", null, describe));
                Assert.Equal(shape2, await SalesShapeAsync(sm));
                Assert.Equal(undo2, await ComposerFrame.UndoStepsAsync(sm));
                Assert.Equal(0, (await engine.GetWorkflowRunAsync(run2.RunId)).StepIndex);
            }
        }

        // Plan-heart contract 14, "Guided run rows get the Plan's state words": a step waiting for a person's answer is
        // Needs you; a step the assistant is doing is On it, step N of M.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_guided_run_carries_the_Plans_word_for_who_it_waits_on()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string doc = "---\nschemaVersion: 2\nname: note-step\ntitle: Note step\n---\n"
                    + "## Step 1: Ask\n```yaml step\nid: ask\n```\nWrite a note.\n```yaml gate\ninputs:\n  - name: note\n    question: What is the note?\n    required: optional\n```\n"
                    + "## Step 2: Tidy\n```yaml step\nid: tidy\n```\nTidy up.\n";
                await engine.SaveWorkflowAsync("note-step", doc, "human");
                var run = await engine.StartWorkflowAsync("note-step", "agent");
                ModelPlanItem Row(ModelPlanView plan) => plan.Items.SingleOrDefault(i => i.Id == "run/" + run.RunId);
                var asking = Row(await engine.GetModelPlanAsync());
                Assert.Equal(PlanStateWords.NeedsYou, asking.StateWord);
                Assert.Equal("Answer: What is the note?", asking.StateNote);
                Assert.Equal(PlanDoers.You, asking.Doer);
                Assert.Equal(PlanSources.Workflow, asking.Source);

                await engine.SubmitWorkflowStepAsync(run.RunId, "ask", "{\"note\":\"x\"}", "agent");
                var doing = Row(await engine.GetModelPlanAsync());
                Assert.Equal(PlanStateWords.OnIt, doing.StateWord);
                Assert.Equal("step 2 of 2", doing.StateNote);
                Assert.Equal(PlanDoers.Assistant, doing.Doer);

                // It ends as a History entry, not a Plan row.
                await engine.SubmitWorkflowStepAsync(run.RunId, "tidy", null, "agent");
                Assert.Null(Row(await engine.GetModelPlanAsync()));
            }
        }

        // F14 (pre-handover journey): after the last step was skipped the run's row still read Needs you, with Choose,
        // over "Finished. Its report is in History." The engine composed the row correctly (it leaves the Plan when the
        // run ends) but never told the page, because a run publishes workflow/didChange and not plan/didChange, and the
        // page re-reads the Plan only on the second. Every move of a run changes its row, so every move says so.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_move_of_a_guided_run_tells_the_page_to_read_the_plan_again()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string doc = "---\nschemaVersion: 2\nname: two-steps\ntitle: Two steps\n---\n"
                    + "## Step 1: Ask\n```yaml step\nid: ask\n```\nWrite a note.\n```yaml gate\ninputs:\n  - name: note\n    question: What is the note?\n    required: optional\n```\n"
                    + "## Step 2: Tidy\n```yaml step\nid: tidy\n```\nTidy up.\n";
                await engine.SaveWorkflowAsync("two-steps", doc, "human");
                var told = 0;
                void Count(ChangePlanView _) => Interlocked.Increment(ref told);
                sm.Bus.PlanChanged += Count;
                try
                {
                    var run = await engine.StartWorkflowAsync("two-steps", "human");
                    Assert.True(Volatile.Read(ref told) >= 1, "starting a run did not tell the page");
                    var before = Volatile.Read(ref told);
                    await engine.SubmitWorkflowStepAsync(run.RunId, "ask", "{\"note\":\"x\"}", "human");
                    Assert.True(Volatile.Read(ref told) > before, "a step moving on did not tell the page");
                    before = Volatile.Read(ref told);
                    var ended = await engine.SkipWorkflowStepAsync(run.RunId, "tidy", "Nothing to tidy.", "human");
                    Assert.Equal("completed", ended.Status);
                    Assert.True(Volatile.Read(ref told) > before, "the run ending did not tell the page, so its row kept saying Needs you");
                    Assert.DoesNotContain((await engine.GetModelPlanAsync()).Items, i => i.Id == "run/" + run.RunId);
                }
                finally { sm.Bus.PlanChanged -= Count; }
            }
        }

        // compose_task from a saved format-3 workflow: its inputs fill the action, its question is the person's, and
        // the guided runner refuses the same file and names compose_task.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_saved_v3_workflow_drafts_with_its_inputs_and_questions()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string doc = "---\nschemaVersion: 3\nname: sales-rate\ntitle: Sales rate\n"
                    + "inputs:\n  - name: table\n    question: Which table?\n    type: objectRef\n    from: tables\n---\n"
                    + "## Step 1: Add the measure Sales rate\n```yaml step\nid: rate\nperformer: semanticus\naction:\n  op: create_measure\n"
                    + "  tableRef: \"{{inputs.table}}\"\n  name: Sales rate\n  expression: \"{{blanks.formula}}\"\n"
                    + "questions:\n  - name: basis\n    question: Rate of\n    type: enum\n    by: you\n    values: [Units, Amount]\n"
                    + "blanks:\n  - name: formula\n    kind: dax\n    fills: expression\n    dependsOn: [basis]\n```\n";
                await engine.SaveWorkflowAsync("sales-rate", doc, "human");
                var v = await McpTools.ComposeTask(engine, workflow: "sales-rate", inputs: JsonDocument.Parse("{\"table\":\"table:Sales\"}").RootElement);
                Assert.Equal(DraftStates.NeedsYou, v.State);               // the person's question beats the queued part
                Assert.Equal("Answer: Rate of", v.PlanNote);
                Assert.Equal("sales-rate", v.Source);

                var answered = await engine.EditDraftAsync(v.DraftId, "answer", null, null, "basis", "Units", "human");
                Assert.Equal(DraftStates.WaitingForAssistant, answered.State);
                var id = DraftFrame.RequestOf(answered, "formula");
                var c = await engine.ClaimAssistantRequestAsync(id);
                Assert.Contains("Rate of: Units.", c.Request.Brief);
                Assert.True((await engine.CompleteAssistantRequestAsync(id, c.Request.ClaimToken, "SUM ( Sales[Quantity] )")).Ok);
                var ran = await McpTools.RunDraft(engine, v.DraftId);
                Assert.Equal(DraftStates.Done, ran.State);
                Assert.Equal("SUM ( Sales[Quantity] )", await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures["Sales rate"].Expression));

                var ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => engine.StartWorkflowAsync("sales-rate", "human"));
                Assert.Contains("compose_task", ex.Message);
            }
        }

        // Found by the RpcSmoke draft scenario: a part carries the objects its step names, for the claim's grounding,
        // and the footprint rule took them as the part's floor, so a part about a table a Semanticus row describes
        // waited for that row and was never offered. A part writes words into the draft, not the model; it takes no
        // part in waits (like a task with no objects), and it is still never called independent.
        [Fact]
        public void A_drafts_part_takes_no_part_in_waits_whatever_objects_it_names()
        {
            var part = new ModelPlanItem
            {
                Id = "pt-0a1b2c3d",
                Objects = new[] { "table:Date" },
                OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Draft, Ref = "dr-0a1b2c3d" },
            };
            var fp = PlanFootprint.Asked(part);
            Assert.False(fp.Known);
            Assert.False(fp.HasKeys);

            // An ordinary task keeps its floor.
            part.OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Words };
            Assert.True(PlanFootprint.Asked(part).HasKeys);
        }

        // ---- Codex on pull request 395 ---------------------------------------------------------------------------

        /// <summary>Stand in for the ops an after step calls, and keep what each call was given.</summary>
        private static List<(string Op, JsonElement Args)> RecordAfterOps(LocalEngine engine, Func<string, JsonElement, object> reply)
        {
            var calls = new List<(string Op, JsonElement Args)>();
            engine.DraftAfterOpForTest = (op, json) =>
            {
                var args = JsonDocument.Parse(json).RootElement.Clone();
                calls.Add((op, args));
                return Task.FromResult(reply(op, args));
            };
            return calls;
        }

        private static bool Commits(JsonElement args) =>
            args.TryGetProperty("commit", out var c) && c.ValueKind == JsonValueKind.True;

        private static async Task<DraftAfterResult> RunOneAfterAsync(LocalEngine engine, string op, object args)
        {
            var steps = JsonSerializer.Serialize(new object[]
            {
                new { id = "describe", title = "Describe Amount", op = "set_description",
                      args = new { objRef = "column:Sales/Amount", text = "What the line sold for." } },
                new { id = "after", title = "After the change", op, args },
            });
            var v = await McpTools.ComposeTask(engine, JsonDocument.Parse(steps).RootElement, title: "Describe and " + op);
            await McpTools.RunDraft(engine, v.DraftId);
            var d = await engine.GetDraftAsync(v.DraftId);
            await engine.UndoAsync("human");
            return d.Record.After.Single(a => a.Op == op);
        }

        // Codex on pull request 395, DraftRunner.cs:993: a refresh or a deploy writes only when told to commit, and a
        // workflow cannot set commit, so the step ran as a preview and read Ran. It now runs committed, and reads Ran
        // only when its report says the write happened.
        [Fact]
        public async Task An_after_write_runs_committed_and_reads_Ran_only_when_its_report_says_it_wrote()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                // A report that says nothing was written is not a run.
                var calls = RecordAfterOps(engine, (op, a) => new RefreshReport { Committed = false, Plan = "Would refresh." });
                var preview = await RunOneAfterAsync(engine, "refresh_partition", new { partitionRef = "partition:Sales/Sales" });
                Assert.True(Commits(Assert.Single(calls).Args), "the refresh was not asked to commit");
                Assert.Equal(DraftAfterStatus.CouldNotRun, preview.Status);

                calls = RecordAfterOps(engine, (op, a) => new RefreshReport { Committed = true });
                Assert.Equal(DraftAfterStatus.Ran, (await RunOneAfterAsync(engine, "refresh_partition", new { partitionRef = "partition:Sales/Sales" })).Status);
                Assert.True(Commits(Assert.Single(calls).Args));

                // deploy_live commits with the token its own preview returned.
                calls = RecordAfterOps(engine, (op, a) => Commits(a)
                    ? new DeployReport { Committed = a.TryGetProperty("confirmToken", out var t) && t.GetString() == "tok-1", TotalChanges = 1 }
                    : new DeployReport { ConfirmToken = "tok-1", TotalChanges = 1 });
                Assert.Equal(DraftAfterStatus.Ran, (await RunOneAfterAsync(engine, "deploy_live", new { })).Status);
                Assert.Equal(new[] { false, true }, calls.Select(c => Commits(c.Args)));

                // A refused deploy says why, in the report's words.
                calls = RecordAfterOps(engine, (op, a) => new DeployStageReport { Committed = false, Error = "Promoting to production needs a person." });
                var refused = await RunOneAfterAsync(engine, "deploy_stage", new { pipelineId = "p1", sourceStageId = "dev", targetStageId = "prod" });
                Assert.True(Commits(Assert.Single(calls).Args));
                Assert.Equal(DraftAfterStatus.CouldNotRun, refused.Status);
                Assert.Contains("Promoting to production needs a person.", refused.Text);
            }
        }

        // Codex on pull request 395, DraftRunner.cs:970: an after step's arguments are filled from the draft's inputs
        // and answers, as a change step's are, before the op is called.
        [Fact]
        public async Task An_after_step_reads_its_inputs_and_answers_filled_in()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string doc = "---\nschemaVersion: 3\nname: describe-and-load\ntitle: Describe and load\n"
                    + "inputs:\n  - name: partition\n    question: Which partition?\n    type: text\n---\n"
                    + "## Step 1: Describe Amount\n```yaml step\nid: describe\nperformer: semanticus\naction:\n  op: set_description\n"
                    + "  objRef: column:Sales/Amount\n  text: What the line sold for.\n```\n"
                    + "## Step 2: Load the data\n```yaml step\nid: load\nperformer: semanticus\naction:\n  op: refresh_partition\n"
                    + "  partitionRef: \"{{inputs.partition}}\"\n  refreshType: \"{{questions.how}}\"\n"
                    + "questions:\n  - name: how\n    question: How to load it\n    type: enum\n    by: you\n    values: [Full, DataOnly]\n```\n";
                await engine.SaveWorkflowAsync("describe-and-load", doc, "human");
                var calls = RecordAfterOps(engine, (op, a) => new RefreshReport { Committed = true });
                var v = await McpTools.ComposeTask(engine, workflow: "describe-and-load", inputs: JsonDocument.Parse("{\"partition\":\"partition:Sales/Sales\"}").RootElement);
                await engine.EditDraftAsync(v.DraftId, "answer", null, null, "how", "DataOnly", "human");
                await McpTools.RunDraft(engine, v.DraftId);

                var call = Assert.Single(calls);
                Assert.Equal("partition:Sales/Sales", call.Args.GetProperty("partitionRef").GetString());
                Assert.Equal("DataOnly", call.Args.GetProperty("refreshType").GetString());
                Assert.Equal(DraftAfterStatus.Ran, (await engine.GetDraftAsync(v.DraftId)).Record.After.Single(a => a.Op == "refresh_partition").Status);
            }
        }

        // Codex on pull request 395, DraftRunner.cs:251: a check's `when` is kept, and a check whose condition does not
        // hold for the draft's answers does not run. Before, every conditional check ran, so a check meant only for
        // another answer stopped the publish after it.
        [Fact]
        public async Task A_check_runs_only_when_its_condition_holds_for_the_drafts_answers()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                const string doc = "---\nschemaVersion: 3\nname: describe-and-publish\ntitle: Describe and publish\n"
                    + "inputs:\n  - name: strict\n    question: Check the rows first?\n    type: text\n    required: optional\n---\n"
                    + "## Step 1: Describe Amount\n```yaml step\nid: describe\nperformer: semanticus\naction:\n  op: set_description\n"
                    + "  objRef: column:Sales/Amount\n  text: What the line sold for.\n"
                    + "checks:\n  - kind: key_unique\n    phase: before\n    target: column:Sales/SalesLineKey\n    when: inputs.strict.answered\n"
                    + "  - kind: row_count\n    phase: after\n    target: table:Sales\n    min: 1\n    when: inputs.strict.answered\n```\n"
                    + "## Step 2: Publish to test\n```yaml step\nid: publish\nperformer: semanticus\naction:\n  op: deploy_stage\n"
                    + "  pipelineId: p1\n  sourceStageId: dev\n  targetStageId: test\n```\n";
                await engine.SaveWorkflowAsync("describe-and-publish", doc, "human");
                RecordAfterOps(engine, (op, a) => new DeployStageReport { Committed = true });

                // Not answered: neither check applies, so nothing unchecked stands before the publish.
                var v = await McpTools.ComposeTask(engine, workflow: "describe-and-publish");
                await McpTools.RunDraft(engine, v.DraftId);
                var d = await engine.GetDraftAsync(v.DraftId);
                Assert.Empty(d.Record.BeforeChecks);
                Assert.DoesNotContain(d.Record.After, a => a.What == "check");
                Assert.Equal(DraftAfterStatus.Ran, d.Record.After.Single(a => a.Op == "deploy_stage").Status);
                await engine.UndoAsync("human");

                // Answered: the after check runs.
                engine.DraftProbeForTest = _ => Task.FromResult<double?>(12);
                var w = await McpTools.ComposeTask(engine, workflow: "describe-and-publish", inputs: JsonDocument.Parse("{\"strict\":\"yes\"}").RootElement);
                await McpTools.RunDraft(engine, w.DraftId);
                var e = await engine.GetDraftAsync(w.DraftId);
                Assert.Contains(e.Record.After, a => a.What == "check" && a.Kind == "row_count");
                Assert.Contains(e.Record.BeforeChecks, c => c.Kind == "key_unique");
            }
        }

        // Codex on pull request 395, ComposedChangeExecutor.cs:598: a direct step's writes that its own verify then
        // refuses are taken back, and so is what they would have recorded: no verified-edit record (that trail is
        // append-only, so it is written only once the step is accepted) and no workflow-authored hash.
        [Fact]
        public async Task A_refused_direct_step_leaves_no_audit_record_and_no_authored_hash()
        {
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                await engine.SetVerifiedModeAsync(true, "human");
                string Doc(string name, bool verify) => "---\nschemaVersion: 3\nname: " + name + "\ntitle: Add units\nstrictness: hard\n---\n"
                    + "## Step 1: Add the measure Units\n```yaml step\nid: add\nperformer: assistant\n```\nAdd the measure.\n"
                    + "```yaml gate\nops: [create_measure]\ninputs:\n  - name: probe\n    question: The value it should show.\n    type: text\n    required: required\n"
                    + (verify ? "verify:\n  - kind: dax_probe\n    probe: probe\n" : "") + "```\n"
                    + "## Step 2: Tidy up\n```yaml step\nid: tidy\nperformer: assistant\n```\nTidy up.\n";   // keeps the run open to read
                var writes = JsonSerializer.Serialize(new object[]
                {
                    new { op = "create_measure", args = new { tableRef = "table:Sales", name = "Units", expression = "SUM ( Sales[Quantity] )" } },
                });
                var answers = JsonSerializer.Serialize(new { probe = "42" });

                // Refused by its verify (no live connection to probe): taken back, and nothing recorded.
                await engine.SaveWorkflowAsync("add-units-probed", Doc("add-units-probed", true), "human");
                var refused = await engine.StartWorkflowAsync("add-units-probed", "agent");
                await Assert.ThrowsAnyAsync<Exception>(() => engine.SubmitWorkflowStepAsync(refused.RunId, "add", answers, "agent", null, writes));
                Assert.False(await sm.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("Units")));
                Assert.Empty((await engine.ListVerifiedEditsAsync()).Records);
                var hashes = sm.CurrentContext.WorkflowAux.TryGetValue(refused.RunId, out var aux) ? aux?.AuthoredMeasureHashes : null;
                Assert.True(hashes == null || !hashes.ContainsKey("measure:Sales/Units"), "the refused write left its authored hash");
                await engine.AbortWorkflowAsync(refused.RunId, "Refused in a test.", "agent");

                // Accepted: the record and the hash are there.
                await engine.SaveWorkflowAsync("add-units", Doc("add-units", false), "human");
                var run = await engine.StartWorkflowAsync("add-units", "agent");
                await engine.SubmitWorkflowStepAsync(run.RunId, "add", answers, "agent", null, writes);
                Assert.Contains((await engine.ListVerifiedEditsAsync()).Records, r => r.ObjectRef == "measure:Sales/Units");
                Assert.Contains("measure:Sales/Units", sm.CurrentContext.WorkflowAux[run.RunId].AuthoredMeasureHashes.Keys);
            }
        }

        // ---- the frozen view shape (lane L4's first deliverable, docs/redesign/contracts/draft-view.json) ----------

        private static readonly JsonSerializerOptions Wire = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>One view per frame scene, made by the engine itself.</summary>
        private static async Task<List<(string Scene, DraftView View)>> ScenesAsync()
        {
            var scenes = new List<(string, DraftView)>();
            var (engine, sm) = await OpenAsync();
            using (sm)
            {
                var rpc = new EngineRpcTarget(engine);
                var draft = await rpc.composeTask(stepsJson: DraftFrame.Steps(), title: DraftFrame.Title);
                scenes.Add(("draft", draft));

                var waiting = await McpTools.ComposeTask(engine, JsonDocument.Parse(DraftFrame.Steps()).RootElement, title: DraftFrame.Title);
                scenes.Add(("started_waiting_for_your_assistant", waiting));

                var desc = DraftFrame.RequestOf(waiting, "description");
                var claim = await engine.ClaimAssistantRequestAsync(desc);
                await engine.DeclineAssistantRequestAsync(desc, claim.Request.ClaimToken, "Should it say what counts as a return?", null, new[] { "measure:Returns/Return rate" });
                scenes.Add(("needs_you_assistant_asks", await engine.GetDraftAsync(waiting.DraftId)));

                await rpc.editDraft(waiting.DraftId, "write", "describe", "description", text: DraftFrame.Description);
                var formula = DraftFrame.RequestOf(waiting, "formula");
                var fc = await engine.ClaimAssistantRequestAsync(formula);
                await engine.CompleteAssistantRequestAsync(formula, fc.Request.ClaimToken, ComposerFrame.ReturnRate);
                scenes.Add(("ready", await engine.GetDraftAsync(waiting.DraftId)));

                engine.DraftProbeForTest = what => Task.FromResult<double?>(1.34);
                scenes.Add(("done_one_check_failed_after", await McpTools.RunDraft(engine, waiting.DraftId)));
                engine.DraftProbeForTest = null;

                var held = await McpTools.ComposeTask(engine, JsonDocument.Parse(JsonSerializer.Serialize(new object[]
                {
                    new { id = "role", title = "Add the role Readers", op = "create_role", args = new { name = "Readers" } },
                    new { id = "filter", title = "Filter Sales for Readers", op = "set_table_permission",
                          args = new { roleName = "Readers", tableRef = "table:Sales", filterDax = "[Quantity] > 0" } },
                })).RootElement, title: "Readers see sold rows");
                scenes.Add(("held_for_the_person", held));

                await engine.CreateMeasureAsync("table:Sales", "Taken", "1", "human");
                var stop = await rpc.composeTask(stepsJson: JsonSerializer.Serialize(new object[]
                {
                    new { id = "fresh", title = "Add Fresh", op = "create_measure", args = new { tableRef = "table:Sales", name = "Fresh", expression = "1" } },
                    new { id = "taken", title = "Add Taken", op = "create_measure", args = new { tableRef = "table:Sales", name = "Taken", expression = "2" },
                          checks = new[] { new { kind = "name_free", phase = "before" } } },
                }), title: "Add two measures");
                scenes.Add(("stopped", await rpc.runDraft(stop.DraftId)));

                // Slice 10 (lane L5a): a draft from words opens as the menu, and a step picked from it asks its question.
                var menu = await rpc.composeTask(words: "Add a measure for total quantity and describe it");
                scenes.Add(("from_words_menu", menu));
                scenes.Add(("from_words_step_picked", await rpc.editDraft(menu.DraftId, "add", text: "add-measure")));
            }
            return scenes;
        }

        private static IEnumerable<string> Keys(JsonElement e, string at = "")
        {
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject())
                {
                    yield return at + p.Name;
                    foreach (var k in Keys(p.Value, at + p.Name + ".")) yield return k;
                }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var item in e.EnumerateArray())
                    foreach (var k in Keys(item, at + "[].")) yield return k;
        }

        /// <summary>The contract file holds one fixture per frame scene, each exactly the shape the engine produces
        /// for that scene today, so U5 and W2 can build against it. Regenerate with SEMANTICUS_WRITE_DRAFT_VIEW=1.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_frozen_draft_view_file_is_the_engines_own_shape_for_every_scene()
        {
            var scenes = await ScenesAsync();
            Assert.Equal(new[] { DraftStates.NeedsYou, DraftStates.WaitingForAssistant, DraftStates.NeedsYou, DraftStates.Ready,
                                 DraftStates.Done, DraftStates.NeedsYou, DraftStates.Stopped, DraftStates.NeedsYou, DraftStates.NeedsYou }, scenes.Select(x => x.View.State));
            var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var file = System.IO.Path.Combine(root, "docs", "redesign", "contracts", "draft-view.json");
            if (Environment.GetEnvironmentVariable("SEMANTICUS_WRITE_DRAFT_VIEW") == "1")
            {
                var doc = new System.Text.Json.Nodes.JsonObject
                {
                    ["_about"] = new System.Text.Json.Nodes.JsonArray(
                        "The frozen wire shape of one composer draft (composer contract 6.1, lane L4), in the camelCase both doors send.",
                        "One fixture per frame scene, each produced by the engine itself: Semanticus.Tests/DraftStateTests.cs holds every scene's key set to this file.",
                        "state is one of the six run states; planWord and planNote are the Plan row's word for it (Plan-heart contract 14).",
                        "Ids, times and request ids differ run to run; the shape does not."),
                    ["version"] = 1,
                    ["states"] = new System.Text.Json.Nodes.JsonArray(DraftStates.All.Select(x => (System.Text.Json.Nodes.JsonNode)x).ToArray()),
                    ["blankStates"] = new System.Text.Json.Nodes.JsonArray(new[] { DraftBlankStates.Empty, DraftBlankStates.Queued, DraftBlankStates.Writing,
                        DraftBlankStates.Filled, DraftBlankStates.Self, DraftBlankStates.Generated, DraftBlankStates.Needs }.Select(x => (System.Text.Json.Nodes.JsonNode)x).ToArray()),
                };
                var fixtures = new System.Text.Json.Nodes.JsonObject();
                foreach (var (scene, view) in scenes) fixtures[scene] = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(view, Wire));
                doc["scenes"] = fixtures;
                System.IO.File.WriteAllText(file, doc.ToJsonString(Wire) + "\n");
            }
            using var frozen = JsonDocument.Parse(System.IO.File.ReadAllText(file));
            var frozenScenes = frozen.RootElement.GetProperty("scenes");
            Assert.Equal(scenes.Select(x => x.Scene), frozenScenes.EnumerateObject().Select(p => p.Name));
            foreach (var (scene, view) in scenes)
            {
                using var live = JsonDocument.Parse(JsonSerializer.Serialize(view, Wire));
                Assert.Equal(Keys(frozenScenes.GetProperty(scene)).Distinct().OrderBy(k => k, StringComparer.Ordinal),
                             Keys(live.RootElement).Distinct().OrderBy(k => k, StringComparer.Ordinal));
                Assert.Equal(view.State, frozenScenes.GetProperty(scene).GetProperty("state").GetString());
            }
            Assert.Equal(DraftStates.All, frozen.RootElement.GetProperty("states").EnumerateArray().Select(x => x.GetString()));
        }
    }
}
