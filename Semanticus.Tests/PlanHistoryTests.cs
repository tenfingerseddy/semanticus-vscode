using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// History inside the Plan, the engine half (Plan-heart contract 8.5 to 8.9): the merged view nests edits under
    /// the cause they served, undo to a row takes it and everything later back in one commit, the ledger outlives the
    /// engine and shows the earlier session under one seam with no Undo, a restore draws its own seam, the Verified
    /// word is joined, one object can be asked about, and a finished run's report opens after a restart with a run id
    /// that never repeats (Sol 24 Sep).
    /// </summary>
    public sealed class PlanHistoryTests
    {
        private static IEnumerable<PlanHistoryRow> Work(PlanHistoryView v) =>
            v.Rows.Where(r => r.Kind != "decision");

        [Fact]
        public async Task Said_undo_preserves_later_assistant_edits_and_is_itself_undoable()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("SaidUndo.bim");
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "first group", "agent");
            var first = f.Sessions.Ledger.Entries(f.Sessions.Current.Id).Last();
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "second commit in the group", "agent");
            var lastRevision = f.Sessions.Current.Revision;
            await f.Engine.SetDescriptionAsync("column:Sales/Amount", "later group", "agent");
            await f.Engine.UndoToEntryAsync(first.EntryId, "human", lastRevision);
            var values = await f.Sessions.Current.ReadAsync(m => new[] { m.Tables["Sales"].Measures["Total Sales"].Description, m.Tables["Sales"].Columns["Amount"].Description });
            Assert.NotEqual("first group", values[0]);
            Assert.NotEqual("second commit in the group", values[0]);
            Assert.Equal("later group", values[1]);
            await f.Engine.UndoAsync("human");
            Assert.Equal("second commit in the group", await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total Sales"].Description));
            await f.Engine.RedoAsync("human");
            Assert.NotEqual("first group", await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total Sales"].Description));
        }

        // D3 (launch video, 12 Oct): get_plan_history(limit 15) returned 617,866 characters. The assistant's answer is one summary line per row,
        // says what it left out, and gives one row's changes on request. The engine's own answer keeps every value.
        [Fact]
        public async Task The_assistants_history_is_summary_lines_that_say_what_they_left_out_and_one_row_opens_on_request()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("LeanHistory.bim");
            for (var i = 0; i < 26; i++)
                await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", new string((char)('a' + i), 1500), "agent");
            var full = await McpTools.GetPlanHistory(f.Engine);
            var fullSize = System.Text.Json.JsonSerializer.Serialize(full).Length;

            var lean = (PlanHistoryLeanView)await McpTools.GetPlanHistoryOp(f.Engine);

            var leanSize = System.Text.Json.JsonSerializer.Serialize(lean).Length;
            Assert.True(fullSize > 30000, "the fixture must be big enough to matter: " + fullSize);
            Assert.True(leanSize < 12500, "the summary must fit a tool result: " + leanSize);
            Assert.Contains("left out", lean.LeftOut);
            var row = lean.Rows.First(r => r.Kind == "edit");
            Assert.True(row.Changes > 0);

            var opened = (PlanHistoryEntryView)await McpTools.GetPlanHistoryOp(f.Engine, entry: row.Id);
            Assert.Equal(row.Id, opened.Entry.Id);
            Assert.Contains(opened.Entry.Deltas, d => d.After != null && d.After.Length >= 300);

            var asked = (PlanHistoryView)await McpTools.GetPlanHistoryOp(f.Engine, detail: true, limit: 50);
            Assert.Equal(full.Rows.Length, asked.Rows.Length);
            await Assert.ThrowsAsync<ArgumentException>(() => McpTools.GetPlanHistoryOp(f.Engine, entry: "no-such-row"));
        }

        // D7 (launch video, 12 Oct): after Redo the footer said unsaved while the saved copy equalled the model. The session's own answer follows the saved state.
        [Fact]
        public async Task After_undo_and_redo_the_model_reads_saved_exactly_when_it_equals_the_saved_copy()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("UndoSaved.bim");
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "first", "agent");
            await f.Engine.SetDescriptionAsync("column:Sales/Amount", "second", "agent");
            await f.SaveAsAsync("UndoSaved.bim");
            Assert.False((await f.Engine.SessionInfoAsync()).HasUnsavedChanges);

            await f.Engine.UndoAsync("human");
            await f.Engine.UndoAsync("human");
            Assert.True((await f.Engine.SessionInfoAsync()).HasUnsavedChanges);   // the model differs from the saved copy

            await f.Engine.RedoAsync("human");
            await f.Engine.RedoAsync("human");
            var info = await f.Engine.SessionInfoAsync();
            Assert.False(info.HasUnsavedChanges);                                 // the model equals the saved copy again
            Assert.False(info.HasUnsavedEdits);
        }

        [Theory]
        [InlineData("agent", "measure:Sales/Total Sales")]
        [InlineData("human", "column:Sales/Amount")]
        public async Task Said_undo_opens_history_for_a_conflict_or_a_person_edit(string origin, string objRef)
        {
            using var f = await ChangeCauseTests.PlanModelAsync("SaidConflict.bim");
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "first group", "agent");
            var first = f.Sessions.Ledger.Entries(f.Sessions.Current.Id).Last();
            await f.Engine.SetDescriptionAsync(objRef, "later group", origin);
            var revision = f.Sessions.Current.Revision;
            Assert.True((await f.Engine.UndoToEntryAsync(first.EntryId, "human", first.Revision)).HistoryRequired);
            Assert.Equal(revision, f.Sessions.Current.Revision);
        }

        [Fact]
        public async Task Said_undo_of_an_add_preserves_other_objects_and_can_be_taken_back()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("SaidAdd.bim");
            await f.Engine.CreateMeasureAsync("table:Sales", "New measure", "1", "agent");
            var first = f.Sessions.Ledger.Entries(f.Sessions.Current.Id).Last();
            await f.Engine.SetDescriptionAsync("column:Sales/Amount", "later group", "agent");
            await f.Engine.UndoToEntryAsync(first.EntryId, "human", first.Revision);
            Assert.False(await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("New measure")));
            await f.Engine.UndoAsync("human");
            Assert.True(await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("New measure")));
            Assert.Equal("later group", await f.Sessions.Current.ReadAsync(m => m.Tables["Sales"].Columns["Amount"].Description));
        }

        [Fact]
        public async Task Said_revert_keeps_the_file_guard_when_it_is_undone()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("SaidSidecar.bim");
            var starter = (await f.Engine.GetPrimerAsync()).Markdown;
            var initial = await f.Engine.SetPrimerAsync(starter, "human");
            var before = await File.ReadAllTextAsync(initial.FilePath);
            var changed = starter.Replace("_Add what people and the AI Assistant should know._", "Sales uses the Date table.");
            await f.Engine.SetPrimerAsync(changed, "agent");
            var first = f.Sessions.Ledger.Entries(f.Sessions.Current.Id).Last(e => e.Origin == "agent");
            await f.Engine.SetDescriptionAsync("column:Sales/Amount", "later group", "agent");
            await f.Engine.UndoToEntryAsync(first.EntryId, "human", first.Revision);
            Assert.Equal(before, await File.ReadAllTextAsync(initial.FilePath));
            var external = before + Environment.NewLine + "A newer note.";
            await File.WriteAllTextAsync(initial.FilePath, external);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.UndoAsync("human"));
            Assert.Equal(external, await File.ReadAllTextAsync(initial.FilePath));
        }

        /// <summary>The person edits, the assistant edits, the plan applies a batch: three rows.</summary>
        private static async Task<(string Person, string Assistant, string[] Items)> ThreeDoersAsync(DecisionFixture f)
        {
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "by a person", "human");
            var person = f.Sessions.Ledger.Entries(f.Sessions.Current.Id).Last().EntryId;
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "by an assistant", "agent");
            var assistant = f.Sessions.Ledger.Entries(f.Sessions.Current.Id).Last().EntryId;
            var plan = await f.Engine.GetModelPlanAsync();
            var ids = plan.Items.Where(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal)).Select(i => i.Id).Take(2).ToArray();
            foreach (var id in ids) await f.Engine.ModelPlanActionAsync(id, "propose", "human");
            var ready = await f.Engine.GetModelPlanAsync();
            var result = await f.Engine.ApplyPlanItemsAsync(
                ids.Select(id => new PlanItemDigest { Id = id, Digest = ready.Items.First(i => i.Id == id).ChangeSet.Digest }).ToArray(), "human");
            Assert.True(result.Performed, result.Sentence);
            return (person, assistant, ids);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_merged_view_nests_by_cause_and_undo_to_the_first_takes_all_three_back_in_one_commit()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("Merged.bim");
            var (person, assistant, items) = await ThreeDoersAsync(f);

            var view = await f.Engine.GetPlanHistoryAsync();
            var rows = Work(view).ToList();
            Assert.Equal(new[] { "semanticus", "assistant", "you" }, rows.Take(3).Select(r => r.By));
            var batch = rows[0];
            Assert.Equal("item", batch.Kind);
            Assert.Equal(items.OrderBy(x => x), batch.Cause.Ids.OrderBy(x => x));
            var child = Assert.Single(batch.Children);
            Assert.Equal("edit", child.Kind);
            Assert.NotEmpty(child.Deltas);
            Assert.Equal("batch", batch.Verified);               // the Verified Edits record apply_plan wrote
            Assert.Equal(person, rows[2].Id);
            Assert.True(rows[2].Undoable);
            Assert.Equal(2, rows[2].LaterCount);
            Assert.All(rows.Take(3), r => Assert.False(r.BeforeThisSession));
            Assert.Contains(view.Rows, r => r.Kind == "decision");   // the proposals, which wrote nothing

            var commits = 0;
            Action<ChangeNotification> count = _ => commits++;
            f.Sessions.Bus.Changed += count;
            try { await f.Engine.UndoToEntryAsync(person, "agent"); }
            finally { f.Sessions.Bus.Changed -= count; }
            Assert.Equal(1, commits);

            var after = Work(await f.Engine.GetPlanHistoryAsync()).Take(3).ToList();
            Assert.All(after, r => Assert.True(r.Undone && r.Redoable && !r.Undoable));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task One_object_and_one_doer_can_be_asked_about()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("Filter.bim");
            await ThreeDoersAsync(f);
            await f.Engine.SetDescriptionAsync("column:Sales/Amount", "elsewhere", "human");

            // The measure's own create (the fixture made it before saving), the person's edit and the assistant's.
            var measure = Work(await f.Engine.GetPlanHistoryAsync(objectRef: "measure:Sales/Total Sales")).ToList();
            Assert.Equal(3, measure.Count);
            Assert.Equal("add", measure.Last().Deltas.First().Kind);
            Assert.All(measure, r => Assert.Contains(r.Deltas, d => d.Ref == "measure:Sales/Total Sales"));

            var mine = (await f.Engine.GetPlanHistoryAsync(by: "you")).Rows;
            Assert.All(mine, r => Assert.Equal("you", r.By));
            Assert.Contains(mine, r => r.Title != null && r.Deltas.Any(d => d.Ref == "column:Sales/Amount"));

            var page = await f.Engine.GetPlanHistoryAsync(limit: 2);
            Assert.Equal(2, page.Rows.Length);
            Assert.True(page.More);
            var next = await f.Engine.GetPlanHistoryAsync(before: page.Rows[1].When.ToString("o"), limit: 500);
            Assert.All(next.Rows, r => Assert.True(r.When < page.Rows[1].When));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task After_an_engine_restart_the_rows_sit_under_the_seam_with_no_Undo()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("Restart.bim");
            var (person, _, _) = await ThreeDoersAsync(f);
            await f.Sessions.Ledger.FlushAsync();
            Assert.True(File.Exists(Path.Combine(f.Workspace, ".semanticus", "history.json")));

            var sessions = new SessionManager();
            using var again = new LocalEngine(sessions, TestEntitlements.Pro, f.Workspace);
            await again.OpenAsync(Path.Combine(f.Workspace, "Restart.bim"));

            var rows = Work(await again.GetPlanHistoryAsync()).ToList();
            Assert.True(rows.Count >= 3, "the earlier session's rows did not come back");
            Assert.All(rows, r => Assert.True(r.BeforeThisSession && !r.Undoable));
            Assert.Contains(rows, r => r.Id == person && r.By == "you");
            Assert.Contains(rows, r => r.Kind == "item" && r.By == "semanticus" && r.Children.Length == 1);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => again.UndoToEntryAsync(person, "human"));
            Assert.Equal(EditLedger.BeforeThisSessionSentence, ex.Message);

            // A new edit in the new session is above the seam and undoable.
            await again.SetDescriptionAsync("measure:Sales/Total Sales", "after the restart", "human");
            var top = Work(await again.GetPlanHistoryAsync()).First();
            Assert.False(top.BeforeThisSession);
            Assert.True(top.Undoable);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_restore_draws_its_own_seam_and_a_saved_version_is_a_flag_row()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-history-restore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var model = LedgerFixture.Write(dir, "model.bim");
                Git(dir, "init", "-q");
                Git(dir, "config", "user.email", "history@test.local");
                Git(dir, "config", "user.name", "History Test");
                Git(dir, "config", "commit.gpgsign", "false");
                Git(dir, "add", "--", "model.bim");
                Git(dir, "commit", "-q", "-m", "initial");

                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(model);
                await engine.SetDescriptionAsync("measure:Sales/M1", "known good", "human");
                var good = await engine.CreateHistoryCheckpointAsync("Known good", commit: true, "human");
                Assert.True(good.Committed, good.Error);
                await engine.SetDescriptionAsync("measure:Sales/M1", "experiment", "agent");

                var saved = (await engine.GetPlanHistoryAsync()).Rows.Single(r => r.Kind == "saved");
                Assert.Equal("you", saved.By);
                Assert.Equal(good.Checkpoint.Hash, saved.Detail);

                var restored = await engine.RestoreHistoryCheckpointAsync(good.Checkpoint.Hash, restore: true, "human");
                Assert.True(restored.Restored, restored.Error);

                var rows = (await engine.GetPlanHistoryAsync()).Rows.Where(r => r.Kind != "decision").ToList();
                var seam = rows.First(r => r.Kind == "restored");
                Assert.False(seam.BeforeThisSession);
                Assert.Contains(good.Checkpoint.ShortHash, seam.Detail);
                var older = rows.Where(r => r.Kind == "edit").ToList();
                Assert.Equal(2, older.Count);
                Assert.All(older, r => Assert.True(r.BeforeThisSession && r.BeforeRestore && !r.Undoable));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public async Task A_finished_run_opens_from_History_after_a_restart_and_run_ids_never_repeat()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("Runs.bim");
            await FrozenStarter.UseAsync(f.Engine, "check-blast-radius");   // lane l9-finish: a guided run, from the frozen guided file
            var started = await f.Engine.StartWorkflowAsync("check-blast-radius", "human");
            Assert.Matches("^wfr-[0-9a-f]{8}$", started.RunId);
            await f.Engine.AbortWorkflowAsync(started.RunId, "Not today", "agent");
            await f.Sessions.Ledger.FlushAsync();

            var sessions = new SessionManager();
            using var again = new LocalEngine(sessions, TestEntitlements.Pro, f.Workspace);
            await again.OpenAsync(Path.Combine(f.Workspace, "Runs.bim"));
            var run = (await again.GetPlanHistoryAsync()).Rows.Single(r => r.Kind == "run");
            Assert.True(run.BeforeThisSession);
            Assert.Equal(started.RunId, run.Report.RunId);
            Assert.Equal("aborted", run.Report.Status);
            Assert.Equal("check-blast-radius", run.Report.Workflow);
            Assert.NotEmpty(run.Report.Steps);
            Assert.False(string.IsNullOrEmpty(run.Report.Evidence));

            var second = await again.StartWorkflowAsync("check-blast-radius", "human");
            Assert.Matches("^wfr-[0-9a-f]{8}$", second.RunId);
            Assert.NotEqual(started.RunId, second.RunId);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_history_file_never_shows_as_a_change_in_the_models_git_repo()
        {
            // Found by CicdSmoke: the ledger writes .semanticus/history.json in the background after an edit, so a
            // commit made right after it either swept the file in and then saw it modified, or left it untracked,
            // and "clean after commit" failed. The History is this machine's record, not the team's model.
            var dir = Path.Combine(Path.GetTempPath(), "sem-history-git-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var model = LedgerFixture.Write(dir, "model.bim");
                Git(dir, "init", "-q");
                Git(dir, "config", "user.email", "history@test.local");
                Git(dir, "config", "user.name", "History Test");
                Git(dir, "config", "commit.gpgsign", "false");
                Git(dir, "add", "--", "model.bim");
                Git(dir, "commit", "-q", "-m", "initial");

                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(model);
                await engine.SetDescriptionAsync("measure:Sales/M1", "committed", "agent");
                await sessions.Ledger.FlushAsync();
                var commit = await engine.GitCommitAsync("describe a measure", null, true, "agent");
                Assert.True(commit.Committed, commit.Error);
                await engine.SetDescriptionAsync("measure:Sales/M2", "after the commit", "agent");
                await engine.UndoAsync("agent");
                await sessions.Ledger.FlushAsync();

                Assert.True(File.Exists(Path.Combine(dir, ".semanticus", "history.json")));
                Assert.Equal("", Git(dir, "status", "--porcelain"));
                Assert.DoesNotContain("history.json", Git(dir, "log", "--name-only", "--pretty=format:"));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Disposing_the_sessions_waits_for_the_ledger_write_so_the_folder_can_be_deleted()
        {
            // Found by the full suite: a test disposed its SessionManager and deleted the model's folder while the
            // ledger's background write was still creating .semanticus/history.json ("Directory not empty").
            var dir = Path.Combine(Path.GetTempPath(), "sem-history-dispose-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            EditLedger.WriteDelayMsForTest = 400;
            try
            {
                var sessions = new SessionManager();
                var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(LedgerFixture.Write(dir));
                await engine.SetDescriptionAsync("measure:Sales/M1", "written before dispose returns", "human");
                sessions.Dispose();

                var file = Path.Combine(dir, ".semanticus", "history.json");
                Assert.True(File.Exists(file), "Dispose returned before the ledger was written");
                Assert.Contains("written before dispose returns", File.ReadAllText(file));
                Directory.Delete(dir, recursive: true);
            }
            finally
            {
                EditLedger.WriteDelayMsForTest = 0;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public async Task Undo_to_here_and_redo_to_here_on_a_parent_row_walk_its_whole_group()
        {
            // Codex on pull request 393, finding 2 (LocalEngine.History.cs:140): a parent row said Undoable, but its id
            // was the cause, which the ledger could not resolve, so Undo to here always said "No change with that id".
            using var f = await ChangeCauseTests.PlanModelAsync("ParentUndo.bim");
            var (person, assistant, _) = await ThreeDoersAsync(f);
            var batch = Work(await f.Engine.GetPlanHistoryAsync()).First();
            Assert.Equal("item", batch.Kind);
            Assert.True(batch.Undoable);

            var commits = 0;
            Action<ChangeNotification> count = _ => commits++;
            f.Sessions.Bus.Changed += count;
            try { await f.Engine.UndoToEntryAsync(batch.Id, "human"); }
            finally { f.Sessions.Bus.Changed -= count; }
            Assert.Equal(1, commits);

            var after = Work(await f.Engine.GetPlanHistoryAsync()).ToList();
            var parent = after.Single(r => r.Id == batch.Id);
            Assert.True(parent.Undone && parent.Redoable && !parent.Undoable);
            Assert.All(parent.Children, c => Assert.True(c.Undone));
            Assert.False(after.Single(r => r.Id == assistant).Undone);   // the older rows stay
            Assert.False(after.Single(r => r.Id == person).Undone);

            await f.Engine.RedoToEntryAsync(batch.Id, "human");
            parent = Work(await f.Engine.GetPlanHistoryAsync()).Single(r => r.Id == batch.Id);
            Assert.True(parent.Undoable && !parent.Undone && !parent.Redoable);
            Assert.All(parent.Children, c => Assert.False(c.Undone));
        }

        [Fact]
        public async Task The_first_save_of_a_new_model_keeps_the_edits_made_before_it()
        {
            // Codex on pull request 393, finding 3 (EditLedger.cs:417): the entries made before a new model's first save
            // had no home, and the save did not give them one, so closing right after it lost the whole history.
            var dir = Path.Combine(Path.GetTempPath(), "sem-history-firstsave-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var path = Path.Combine(dir, "Pathless.bim");
                var sessions = new SessionManager();
                var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.CreateModelAsync("Pathless", 1604);
                await engine.CreateTableAsync("Sales", "human");
                Assert.False((await engine.GetPlanHistoryAsync()).Kept);
                await engine.SaveAsync(path, null, false, "human");
                engine.Dispose();                 // closed straight after the save, with no edit or History read between
                sessions.Dispose();

                Assert.True(File.Exists(Path.Combine(dir, ".semanticus", EditLedger.FileName)), "the first save wrote no history");
                using var again = new SessionManager();
                using var reopened = new LocalEngine(again, TestEntitlements.Pro);
                await reopened.OpenAsync(path);
                var rows = Work(await reopened.GetPlanHistoryAsync()).ToList();
                Assert.Contains(rows, r => r.BeforeThisSession && r.Deltas.Any(d => d.Ref == "table:Sales"));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        /// <summary>UAT-S07b-1-001. A rename TOM refuses (a table named with the reserved word Measures) threw after
        /// TE2's Name setter had opened its own undo batch, and the refusal closed only one of the two batches. The
        /// open one then swallowed every later edit: Undo to here on the new table said it "made no step on the undo
        /// stack", the next New Table got no History row, and Redo all stayed on offer for an edit already replaced.</summary>
        [Fact]
        public async Task A_refused_rename_leaves_undo_and_History_working_for_the_next_new_table()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Contoso", 1604);
            await engine.CreateTableAsync("Sales", "human");
            var made = await engine.CreateTableAsync("Measures", "human");   // TOM hands back "Measures 1"
            await Assert.ThrowsAnyAsync<Exception>(() => engine.RenameObjectAsync(made, "Measures", "agent"));

            var createRow = Work(await engine.GetPlanHistoryAsync()).First(r => (r.Deltas ?? Array.Empty<ChangeDelta>()).Any(d => d.Ref == made));
            await engine.UndoToEntryAsync(createRow.Id, "human");   // threw "it made no step on the undo stack"
            Assert.False(await sessions.Current.ReadAsync(m => m.Tables.Contains("Measures 1")));
            Assert.True(await sessions.Current.ReadAsync(m => m.Tables.Contains("Sales")));

            var probe = await engine.CreateTableAsync("UAT Rename Probe", "human");
            var rows = Work(await engine.GetPlanHistoryAsync()).ToList();
            var top = rows.First();
            Assert.Contains(top.Deltas ?? Array.Empty<ChangeDelta>(), d => d.Ref == probe);
            Assert.True(top.Undoable);
            Assert.DoesNotContain(rows, r => r.Redoable);
            await engine.UndoAsync("human");
            Assert.False(await sessions.Current.ReadAsync(m => m.Tables.Contains("UAT Rename Probe")));
            Assert.True(await sessions.Current.ReadAsync(m => m.Tables.Contains("Sales")));
        }

        /// <summary>UAT-S07b-1-004, -005 and -006, the same session as S07b-1-001: after the refused rename left an undo
        /// batch open, a field parameter, a calculation group from the builder (create, then its precedence) and an ISO
        /// calendar each landed without an undoable History row. Every builder must leave a row that undoes it, with or
        /// without an earlier refusal in the session.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("field parameter", false)]
        [InlineData("field parameter", true)]
        [InlineData("calculation group", false)]
        [InlineData("calculation group", true)]
        [InlineData("calendar", false)]
        [InlineData("calendar", true)]
        public async Task Each_Advanced_modelling_builder_leaves_an_undoable_History_row(string builder, bool afterRefusedRename)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Contoso", 1702);
            await engine.CreateTableAsync("Customer", "human");
            await engine.CreateColumnAsync("table:Customer", "CustomerName", "String", null, "human");
            // Reopened, so the undo stack starts empty as it did for the UAT's freshly opened model.
            var dir = Path.Combine(Path.GetTempPath(), "sem-builders-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "Model.bim");
            await engine.SaveAsync(bim, "BIM", false, "human");
            await engine.OpenAsync(bim);
            try
            {
            if (afterRefusedRename)
            {
                var made = await engine.CreateTableAsync("Measures", "human");
                await Assert.ThrowsAnyAsync<Exception>(() => engine.RenameObjectAsync(made, "Measures", "agent"));
                // What the UAT session did next: Undo to here on the new table (refused before the fix), then the
                // assistant deleted it instead.
                var row = Work(await engine.GetPlanHistoryAsync()).First(r => (r.Deltas ?? Array.Empty<ChangeDelta>()).Any(d => d.Ref == made));
                try { await engine.UndoToEntryAsync(row.Id, "human"); } catch (InvalidOperationException) { }
                if (await sessions.Current.ReadAsync(m => m.Tables.Contains("Measures 1")))
                    await engine.DeleteObjectAsync(made, "agent");
            }
            var before = Work(await engine.GetPlanHistoryAsync()).Select(r => r.Id).ToHashSet();
            var stepsBefore = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow().Steps);

            Func<Task<bool>> exists;
            switch (builder)
            {
                case "field parameter":
                    await engine.CreateFieldParameterAsync("UAT Customer Field", new[] { new FieldParameterItem { ObjectRef = "column:Customer/CustomerName" } }, "human");
                    exists = () => sessions.Current.ReadAsync(m => m.Tables.Contains("UAT Customer Field"));
                    break;
                case "calculation group":
                    var group = await engine.CreateCalculationGroupAsync("UAT Builder Group", "human");
                    await engine.SetCalcGroupPrecedenceAsync(group, 11, "human");
                    exists = () => sessions.Current.ReadAsync(m => m.Tables.Contains("UAT Builder Group"));
                    break;
                default:
                    await engine.DefineCalendarFromTemplateAsync("iso", null, null, 7, null, null, null, "human");
                    exists = async () => (await engine.ListCalendarsAsync(null)).Calendars.Length > 0;
                    break;
            }
            Assert.True(await exists());

            var fresh = Work(await engine.GetPlanHistoryAsync()).Where(r => !before.Contains(r.Id)).ToList();
            Assert.NotEmpty(fresh);
            // Each commit is one step on the shared undo stack; an open batch left by the refusal swallowed them.
            Assert.Equal(stepsBefore + fresh.Count, await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow().Steps));
            Assert.All(fresh, r => Assert.True(r.Undoable, $"'{r.Title}' is not undoable"));
            await engine.UndoToEntryAsync(fresh.Last().Id, "human");
            Assert.False(await exists());
            Assert.True(await sessions.Current.ReadAsync(m => m.Tables.Contains("Customer")));
            }
            finally { engine.Dispose(); sessions.Dispose(); try { Directory.Delete(dir, true); } catch { } }
        }

        private static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(stderr);
            return stdout.Trim();
        }
    }

    /// <summary>
    /// Sol 6, slice 7, finding 4: two engine processes on the same model (the owner lock is per workspace, the sidecar
    /// is per model) could lose each other's plan memory or History. Both stores replaced the whole file under a lock
    /// that only one process could see, and the ledger wrote a model's whole entry list from its own snapshot.
    ///
    /// A second engine process is stood in for by a second copy of Semanticus.Engine loaded into its own
    /// AssemblyLoadContext: it has its own static locks, exactly as another process would, and shares only the disk.
    /// </summary>
    public sealed class PlanHistoryTwoWriterTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sem-twowriter-" + Guid.NewGuid().ToString("N").Substring(0, 10));

        public PlanHistoryTwoWriterTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private sealed class SecondEngine : AssemblyLoadContext
        {
            private readonly string _path = typeof(PlanMemory).Assembly.Location;
            public SecondEngine() : base("second-engine-process") { }
            protected override Assembly Load(AssemblyName name) =>
                name.Name == "Semanticus.Engine" ? LoadFromAssemblyPath(_path) : null;
        }

        private static readonly Lazy<Assembly> Second = new Lazy<Assembly>(() =>
        {
            var asm = new SecondEngine().LoadFromAssemblyPath(typeof(PlanMemory).Assembly.Location);
            Assert.NotSame(typeof(PlanMemory).Assembly, asm);
            return asm;
        });

        private static Type T(string name) => Second.Value.GetType("Semanticus.Engine." + name, throwOnError: true)!;

        private static Action<TState> Act<TState>(Action<object> a) => s => a(s!);
        private static Func<string, THome> Const<THome>(object v) => _ => (THome)v;

        /// <summary>The other process remembers a skip for <paramref name="itemId"/>.</summary>
        private static void SecondProcessSkips(string file, string identity, string itemId)
        {
            var state = T("PlanMemoryState");
            var skip = T("PlanSkip");
            Action<object> add = s =>
            {
                var k = Activator.CreateInstance(skip)!;
                skip.GetProperty("ItemId")!.SetValue(k, itemId);
                skip.GetProperty("Reason")!.SetValue(k, "from the other process");
                skip.GetProperty("When")!.SetValue(k, DateTimeOffset.UtcNow);
                var list = state.GetProperty("Skips")!.GetValue(s)!;
                list.GetType().GetMethod("Add")!.Invoke(list, new[] { k });
            };
            var action = typeof(PlanHistoryTwoWriterTests).GetMethod(nameof(Act), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(state).Invoke(null, new object[] { add });
            T("PlanMemory").GetMethod("Mutate")!.Invoke(null, new object[] { file, identity, action! });
        }

        /// <summary>The other process's own ledger writes one flag row for its own session.</summary>
        private static void SecondProcessRecords(string file, string identity, string sessionId, string label)
        {
            var ledgerType = T("EditLedger");
            var homeType = T("LedgerHome");
            var home = Activator.CreateInstance(homeType)!;
            homeType.GetProperty("File")!.SetValue(home, file);
            homeType.GetProperty("Identity")!.SetValue(home, identity);
            var ledger = Activator.CreateInstance(ledgerType)!;
            var locate = typeof(PlanHistoryTwoWriterTests).GetMethod(nameof(Const), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(homeType).Invoke(null, new[] { home });
            ledgerType.GetProperty("Locate")!.SetValue(ledger, locate);
            ledgerType.GetMethod("RecordFlag")!.Invoke(ledger, new object?[] { sessionId, EditLedgerKinds.Saved, label, "you", null, null, null });
            ((Task)ledgerType.GetMethod("FlushAsync")!.Invoke(ledger, null)!).GetAwaiter().GetResult();
        }

        private static EditLedger LedgerAt(string file, string identity) =>
            new EditLedger { Locate = _ => new LedgerHome { File = file, Identity = identity } };

        private static string[] LabelsOnDisk(string file, string identity)
        {
            var fresh = LedgerAt(file, identity);
            return fresh.ForModel("reader-" + Guid.NewGuid().ToString("N")).Select(e => e.Label).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }

        [Fact]
        public void Plan_memory_written_by_another_process_inside_this_write_is_kept()
        {
            var file = Path.Combine(_dir, ".semanticus", PlanMemory.FileName);
            const string identity = "model-contoso";
            Task? other = null;
            var otherFinishedInside = false;
            PlanMemory.AfterReadForTest[file] = () =>
            {
                other = Task.Run(() => SecondProcessSkips(file, identity, "from-b"));
                otherFinishedInside = other.Wait(1000);
            };

            PlanMemory.Mutate(file, identity, s => s.Skips.Add(new PlanSkip { ItemId = "from-a", Reason = "this process", When = DateTimeOffset.UtcNow }));
            Assert.NotNull(other);
            Assert.True(other!.Wait(20000), "the other process never finished its write");

            var kept = PlanMemory.Read(file, identity).Skips.Select(s => s.ItemId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "from-a", "from-b" }, kept);
            Assert.False(otherFinishedInside, "the other process wrote while this one held the file between read and write");
        }

        [Fact]
        public async Task Two_ledgers_on_one_model_that_take_turns_keep_every_entry()
        {
            var file = Path.Combine(_dir, ".semanticus", EditLedger.FileName);
            const string identity = "model-contoso";
            var a = LedgerAt(file, identity);
            var b = LedgerAt(file, identity);

            a.RecordFlag("session-a", EditLedgerKinds.Saved, "a1", "you");
            await a.FlushAsync();
            b.RecordFlag("session-b", EditLedgerKinds.Saved, "b1", "you");
            await b.FlushAsync();
            a.RecordFlag("session-a", EditLedgerKinds.Saved, "a2", "you");   // A still holds only its own snapshot
            await a.FlushAsync();

            Assert.Equal(new[] { "a1", "a2", "b1" }, LabelsOnDisk(file, identity));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_ledger_entry_written_by_another_process_inside_this_write_is_kept()
        {
            var file = Path.Combine(_dir, ".semanticus", EditLedger.FileName);
            const string identity = "model-contoso";
            var a = LedgerAt(file, identity);
            Task? other = null;
            var otherFinishedInside = false;
            EditLedger.AfterReadForTest[file] = () =>
            {
                other = Task.Run(() => SecondProcessRecords(file, identity, "session-b", "b1"));
                otherFinishedInside = other.Wait(1000);
            };

            a.RecordFlag("session-a", EditLedgerKinds.Saved, "a1", "you");
            await a.FlushAsync();
            Assert.NotNull(other);
            Assert.True(other!.Wait(20000), "the other process never finished its write");

            Assert.Equal(new[] { "a1", "b1" }, LabelsOnDisk(file, identity));
            Assert.False(otherFinishedInside, "the other process wrote while this one held the file between read and write");
        }
    }
}
