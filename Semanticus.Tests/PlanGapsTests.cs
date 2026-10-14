// The Plan-heart gaps an audit found on 82572fa1 (lane m11/plan-gaps). Each test names the contract line it holds.
//
//   B1  contract 3.2 and 4: "Give to your assistant" on a found row reaches the assistant, as a linked task.
//   B2  contract 1.2 and 1.6: a row's Events are the ledger entries filed under it, so a Done row shows what it did.
//   B3  contract 8.4: while a reader has a guided run on a step, its edits are filed under the run at the door.
//   B4  contract 8.8: History is one engine answer; rescue versions and verified records come from the engine.
//   B5  contract 6.1: draft parts sit under their draft's one line in the fold.
//   C1  the engine says on the wire when a commit only walked the history.
//   C2  what the assistant sees is an engine field on the plan view, not a page port.
//   C3  contract 1.5: model_check reads Model quality findings too; has_words covers doc sections and change slots.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Semanticus.Engine.Jev;
using Xunit;
using static Semanticus.Tests.PlanQueueKit;

namespace Semanticus.Tests
{
    public sealed class PlanGapsTests
    {
        private static List<ChangeNotification> Watch(SessionManager sessions)
        {
            var seen = new List<ChangeNotification>();
            sessions.Bus.Changed += n => { if (n.Deltas?.Length > 0) seen.Add(n); };
            return seen;
        }

        // ---- B1 ------------------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Giving_describe_model_to_the_assistant_puts_a_linked_task_in_its_queue()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("GiveFound.bim");   // Pro: Run the plan lands the safe fixes
            var plan = await f.Engine.GetModelPlanAsync();
            var describe = Row(plan, "words/model");   // the one words row; descriptions are in it (round 1, 8 Oct)
            Assert.Equal(PlanDoers.Assistant, describe.Doer);
            Assert.Empty((await f.Engine.ListAssistantRequestsAsync()).Requests);

            var after = await f.Engine.SetPlanItemDoerAsync("words/model", "assistant");

            // One row now: the task carries the work, and the found row is not shown beside it.
            Assert.DoesNotContain(after.Items, i => i.Id == "words/model");
            var task = after.Items.Single(i => i.OriginLink?.Kind == PlanOriginKinds.Item);
            Assert.True(PlanTasks.IsTaskId(task.Id));
            Assert.Equal("words/model", task.OriginLink.Ref);
            Assert.Equal(PlanDoers.Assistant, task.Doer);
            Assert.Equal(describe.Title, task.Title);
            // The words row carries names, synonyms and instructions as well as descriptions, so it is done when the row leaves the
            // plan, which the engine checks (contract 1.5), and not when one kind of word is written.
            Assert.Equal(PlanDoneWhen.ModelCheck, task.DoneWhen);
            Assert.Contains("measure:Sales/Total Sales", task.Objects);

            // The assistant hears of it at once: offered, or named as not offered yet while Semanticus's own safe fixes
            // to the same objects go first (contract 5.2), then offered once they land.
            var listed = await f.Engine.ListAssistantRequestsAsync();
            Assert.Contains(task.Id, listed.Requests.Select(r => r.RequestId).Concat(listed.NotOffered.Select(n => n.Id)));
            await LandSafeFixesAsync(f);
            Assert.Contains((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == task.Id);

            // The assistant takes it, describes the named objects and says so; Semanticus checks the words.
            var claim = await f.Engine.ClaimAssistantRequestAsync(task.Id);
            Assert.True(claim.Ok, claim.Message);
            // The words row also names the model itself (the AI instructions), which takes instructions and not a description.
            foreach (var objRef in task.Objects.Where(o => !o.StartsWith("obj:", StringComparison.Ordinal)))
                await f.Engine.SetDescriptionAsync(objRef, "Described for the test.", "agent");
            var said = await f.Engine.CompleteAssistantRequestAsync(task.Id, claim.Request.ClaimToken, "Described them.");
            Assert.True(said.Ok, said.Message);
            var closed = await f.Engine.GetModelPlanAsync();
            // Done, or checking while the other words on the row (names, synonyms) are still to be written. A checking task is not listed beside
            // its reopened row: the row is the one job (09b07890, 13 Oct), so the task's row is either Done or the origin row is back.
            var finished = closed.Items.FirstOrDefault(i => i.Id == task.Id);
            if (finished != null) Assert.Equal(PlanStateWords.Done, finished.StateWord);
            else Assert.True(PlanStateWords.IsOpen(Row(closed, "words/model").StateWord));

        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Giving_a_found_row_twice_is_one_task()
        {
            using var f = await GappyAsync("GiveTwice.bim");
            await f.Engine.SetPlanItemDoerAsync("words/model", "assistant");
            var again = await f.Engine.SetPlanItemDoerAsync("words/model", "assistant");
            Assert.Single(again.Items, i => i.OriginLink?.Kind == PlanOriginKinds.Item);
        }

        // ---- Give again after Could not (coordinator follow-up) ---------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Giving_a_declined_task_back_offers_it_again_as_a_new_claim_and_records_the_give()
        {
            using var f = await GappyAsync("GiveAgain.bim");
            var id = await AskAsync(f.Engine, "Describe Net Sales", Free("Net Sales"));
            var first = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.True(first.Ok, first.Message);
            var declined = await f.Engine.DeclineAssistantRequestAsync(id, first.Request.ClaimToken, "No such measure yet.");
            Assert.True(declined.Ok, declined.Message);
            Assert.DoesNotContain((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == id);

            var plan = await f.Engine.SetPlanItemDoerAsync(id, "assistant");

            var row = Row(plan, id);
            Assert.Equal(PlanStateWords.Ready, row.StateWord);
            Assert.Null(row.Claim);
            Assert.DoesNotContain("No such measure yet.", row.Sub ?? "");
            Assert.Contains((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == id);
            var again = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.True(again.Ok, again.Message);
            Assert.NotEqual(first.Request.ClaimToken, again.Request.ClaimToken);
            // The person's give is on the record, in the Plan's history and in both doors' History.
            Assert.Contains(plan.History, h => h.Kind == ModelPlanHistoryKinds.Given && h.ItemId == id && h.Actor == ModelPlanActors.You);
            Assert.Contains((await f.Engine.GetPlanHistoryAsync()).Rows, r => r.Kind == "decision" && r.Cause?.Ids?.Contains(id) == true
                && r.Detail.StartsWith(ModelPlanHistoryKinds.Given, StringComparison.Ordinal));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Giving_a_declined_task_whose_doer_is_still_the_assistant_offers_it_again()
        {
            // A task read back from an older file (or any path that kept the doer) can be declined while its doer is
            // already the assistant. Give must still ask again, not return early because the doer did not change.
            using var f = await GappyAsync("GiveSameDoer.bim");
            var id = await AskAsync(f.Engine, "Describe Gross Sales", Free("Gross Sales"));
            var bound = f.Engine.BindPlanMemory();
            f.Engine.WritePlanTasks(bound, m => { var t = m.FindTask(id); t.DeclineReason = "Not today."; t.Order = null; });
            Assert.Equal(PlanStateWords.NeedsYou, Row(await f.Engine.GetModelPlanAsync(), id).StateWord);

            var plan = await f.Engine.SetPlanItemDoerAsync(id, "assistant");

            Assert.Equal(PlanStateWords.Ready, Row(plan, id).StateWord);
            Assert.NotNull(Row(plan, id).Order);
            Assert.Contains((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == id);
            Assert.Contains(plan.History, h => h.Kind == ModelPlanHistoryKinds.Given && h.ItemId == id);
        }

        /// <summary>Land every safe fix on the plan in one batch, as Run the plan does (Pro).</summary>
        private static async Task LandSafeFixesAsync(DecisionFixture f)
        {
            var plan = await f.Engine.GetModelPlanAsync();
            var ids = plan.Items.Where(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal) && PlanStateWords.IsOpen(i.StateWord))
                .Select(i => i.Id).ToArray();
            foreach (var id in ids) await f.Engine.ModelPlanActionAsync(id, "propose", "human");
            var ready = await f.Engine.GetModelPlanAsync();
            var sent = ids.Select(id => new PlanItemDigest { Id = id, Digest = Row(ready, id).ChangeSet?.Digest }).ToArray();
            if (sent.Length == 0) return;
            var landed = await f.Engine.ApplyPlanItemsAsync(sent, "human");
            Assert.True(landed.Performed, landed.Sentence);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_found_row_the_person_gives_away_closes_when_its_gap_is_gone()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("GiveCleanup.bim");
            await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, "GiveCleanup.bim");
            // The safe fixes land first, and the reports are read again after them: the cleanup offers only while the
            // report reading is for the model as it is (a stale one makes "unused" unknown, and its row goes).
            await LandSafeFixesAsync(f);
            await PlanHarmKit.CheckReportsAgainAsync(f);
            var plan = await f.Engine.GetModelPlanAsync();
            // The assistant is the default doer of every row a tool can do (doer lane, 13 Oct), so the cleanup row is the assistant's before it is given.
            var mine = Row(plan, "cleanup/model");
            Assert.True(PlanStateWords.IsOpen(mine.StateWord));

            var after = await f.Engine.SetPlanItemDoerAsync(mine.Id, "assistant");

            var task = after.Items.Single(i => PlanTasks.IsTaskId(i.Id) && i.OriginLink?.Ref == mine.Id);
            Assert.Equal(PlanDoneWhen.ModelCheck, task.DoneWhen);
            Assert.DoesNotContain(after.Items, i => i.Id == mine.Id);
            Assert.Contains((await f.Engine.ListAssistantRequestsAsync()).Requests, r => r.RequestId == task.Id);
            Assert.True(PlanStateWords.IsOpen(task.StateWord));

            // Set aside, the task lets the found row come back.
            await f.Engine.SkipModelPlanItemAsync(task.Id, "Not now", "human");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == mine.Id);
        }

        // ---- B2 ------------------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_done_row_lists_the_history_entries_it_made()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("Events.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var id = plan.Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal)).Id;
            await f.Engine.ModelPlanActionAsync(id, "propose", "human");
            var ready = await f.Engine.GetModelPlanAsync();
            var result = await f.Engine.ApplyPlanItemsAsync(
                new[] { new PlanItemDigest { Id = id, Digest = Row(ready, id).ChangeSet.Digest } }, "human");
            Assert.True(result.Performed, result.Sentence);

            var done = Row(await f.Engine.GetModelPlanAsync(), id);
            Assert.Equal(PlanStateWords.Done, done.StateWord);
            var entry = Assert.Single(done.Events);
            var history = await f.Engine.GetPlanHistoryAsync();
            var edit = history.Rows.SelectMany(r => new[] { r }.Concat(r.Children ?? Array.Empty<PlanHistoryRow>()))
                .Single(r => r.Id == entry);
            Assert.Contains(id, edit.Cause.Ids);
        }

        // ---- B3 ------------------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Edits_an_assistant_makes_while_its_run_is_on_a_step_are_filed_under_the_run()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("RunCause.bim");
            var seen = Watch(f.Sessions);
            WorkflowRunView run = null;
            // check-blast-radius drafts since lane l9-finish (a composed starter); its frozen guided file still runs as a
            // guided run, which is what this test needs (a run on a step), so it starts from that copy.
            await FrozenStarter.UseAsync(f.Engine, "check-blast-radius");
            await CallAsync(f.Engine, async () => run = await f.Engine.StartWorkflowAsync("check-blast-radius", "agent"));
            Assert.Equal("active", run.Status);

            await CallAsync(f.Engine, () => f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "during the run", "agent"));
            // Another door's edit is not the run's: the person did not start it.
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "by the person", "human");
            await CallAsync(f.Engine, () => f.Engine.AbortWorkflowAsync(run.RunId, "Done for now", "agent"));
            await CallAsync(f.Engine, () => f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "after the run", "agent"));

            Assert.Equal(3, seen.Count);
            Assert.Equal(ChangeCauseKinds.WorkflowRun, seen[0].Cause?.Kind);
            Assert.Equal(run.RunId, seen[0].Cause.Ids.Single());
            // N7: History names the run by its title, the words a person reads, not the file's kebab-case name.
            var def = await f.Engine.GetWorkflowAsync("check-blast-radius");
            Assert.False(string.IsNullOrWhiteSpace(def.Title));
            Assert.Equal(def.Title, seen[0].Cause.Title);
            Assert.Equal(ChangeBy.Workflow, seen[0].By);
            Assert.Null(seen[1].Cause);
            Assert.Null(seen[2].Cause);
        }

        // ---- B4 ------------------------------------------------------------------------------------------------

        [Fact]
        public async Task History_carries_the_rescue_version_and_a_record_with_no_edit()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-plangaps-history-" + Guid.NewGuid().ToString("N"));
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
                using (var engine = new LocalEngine(sessions, TestEntitlements.Pro))
                {
                    await engine.OpenAsync(model);
                    await engine.SetDescriptionAsync("measure:Sales/M1", "known good", "human");
                    var good = await engine.CreateHistoryCheckpointAsync("Known good", commit: true, "human");
                    Assert.True(good.Committed, good.Error);
                    await engine.SetDescriptionAsync("measure:Sales/M1", "experiment", "agent");
                    var restored = await engine.RestoreHistoryCheckpointAsync(good.Checkpoint.Hash, restore: true, "human");
                    Assert.True(restored.Restored, restored.Error);

                    // A verified record written by a read: revision 0, so no edit carries it.
                    await sessions.Current.MutateAsync("agent", "record", m => VerifiedEditsStore.Append(m, new VerifiedEditRecord
                    {
                        SessionId = sessions.Current.Id, Revision = 0, Origin = "agent", Op = "explain_value",
                        ObjectRef = "measure:Sales/M1", Verdict = "explained", Summary = "Explained the value.",
                    }));

                    var rows = (await engine.GetPlanHistoryAsync()).Rows;
                    var rescue = Assert.Single(rows, r => r.Kind == "saved" && r.Title.Contains("Before the restore", StringComparison.Ordinal));
                    Assert.Equal(ChangeBy.Semanticus, rescue.By);
                    var record = Assert.Single(rows, r => r.Kind == "record");
                    Assert.Equal("explain_value", record.Record.Op);
                    Assert.Equal(ChangeBy.Assistant, record.By);
                    Assert.Equal("explained", record.Verified);

                    // The object filter keeps the record (it names the object) and drops the versions.
                    var one = (await engine.GetPlanHistoryAsync(objectRef: "measure:Sales/M1")).Rows;
                    Assert.Contains(one, r => r.Kind == "record");
                    Assert.DoesNotContain(one, r => r.Kind == "saved" && r.Id.StartsWith("checkpoint:", StringComparison.Ordinal));
                    await sessions.Ledger.FlushAsync();
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        private static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("git " + string.Join(" ", args) + " failed: " + error);
            return output;
        }

        // ---- B5 ------------------------------------------------------------------------------------------------

        private static ModelPlanItem Ready(string id, string title, int order, string draft = null) => new ModelPlanItem
        {
            Id = id, Title = title, Order = order, Doer = PlanDoers.Assistant, StateWord = PlanStateWords.Ready,
            FootprintKnown = false, Writes = new[] { "measure:Sales/" + title },
            OriginLink = draft == null ? null : new PlanOriginLink { Kind = PlanOriginKinds.Draft, Ref = draft },
        };

        [Fact]
        public void Draft_parts_sit_under_their_draft_in_the_fold()
        {
            var view = new ModelPlanView
            {
                AssistantQueue = PlanAssistantQueue.On,
                Items = new[]
                {
                    Ready("pt-00000001", "Describe Net Sales", 1),
                    Ready("pt-00000002", "Write the return rate formula", 2, "dr-3f9a01c2"),
                    Ready("pt-00000003", "Describe Returned Units", 3, "dr-3f9a01c2"),
                    new ModelPlanItem { Id = "draft/dr-3f9a01c2", Title = "Add return measures", Doer = PlanDoers.Assistant, StateWord = PlanStateWords.Ready },
                },
            };

            var line = PlanTaskQueue.OfferLine(PlanOffer.From(view));

            Assert.Equal(string.Join("\n",
                "Plan: 2 tasks are ready for you.",
                "1. Describe Net Sales (pt-00000001)",
                "2. Add return measures (draft dr-3f9a01c2): 2 parts to write.",
                "Call list_assistant_requests, then claim_request."), line);
        }

        // ---- C1 ------------------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_history_walk_says_so_on_the_wire_and_an_edit_does_not()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("Replay.bim");
            var all = new List<ChangeNotification>();
            f.Sessions.Bus.Changed += n => all.Add(n);
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "Undo to the old words", "human");
            await f.Engine.UndoAsync("human");

            var serializer = new JsonSerializer();
            RpcServer.ConfigureSerializer(serializer);
            var edit = JObject.FromObject(all.First(n => n.Label != "undo" && n.Deltas?.Length > 0), serializer);
            var walk = JObject.FromObject(all.Last(), serializer);
            Assert.Null(edit["replay"]);
            Assert.Equal("undo", (string)walk["replay"]);
        }

        // ---- C2 ------------------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_plan_view_carries_the_line_the_assistant_reads_and_the_offer_numbers()
        {
            using var f = await GappyAsync("Sees.bim");
            var first = await AskAsync(f.Engine, "Describe Net Sales", Free("Net Sales"));
            var second = await AskAsync(f.Engine, "Describe Gross Sales", Free("Gross Sales"));

            var view = await f.Engine.GetModelPlanAsync();
            var told = PlanLine(await CallAsync(f.Engine));
            Assert.Equal(told, view.AssistantSees);
            Assert.Equal(1, Row(view, first).OfferNumber);
            Assert.Equal(2, Row(view, second).OfferNumber);
            Assert.Null(Row(view, "words/model").OfferNumber);

            var paused = await f.Engine.SetAssistantQueueAsync(PlanAssistantQueue.Paused);
            Assert.Equal(PlanTaskQueue.PausedSentence, paused.AssistantSees);
            Assert.All(paused.Items, i => Assert.Null(i.OfferNumber));
        }

        // ---- C3 ------------------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_model_quality_task_closes_when_the_scan_no_longer_finds_it()
        {
            using var f = await GappyAsync("BpaDone.bim");
            var scan = await f.Engine.BpaScanAsync();
            var v = scan.Violations.First(x => !x.Waived && x.CanAutoFix && x.ObjectRef != null);
            var id = (await f.Engine.AddPlanTaskAsync(new PlanTaskRequest
            {
                Words = "Fix it", SourcePage = "bpa", Objects = new[] { v.ObjectRef }, DoneWhen = PlanDoneWhen.ModelCheck,
                OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Finding, Ref = v.RuleId + "|" + v.ObjectRef },
            })).ItemId;
            Assert.True(PlanStateWords.IsOpen(Row(await f.Engine.GetModelPlanAsync(), id).StateWord));

            await f.Engine.BpaFixAsync(v.RuleId, v.ObjectRef, "agent");

            Assert.Equal(PlanStateWords.Done, Row(await f.Engine.GetModelPlanAsync(), id).StateWord);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_docs_task_closes_when_the_section_has_words()
        {
            using var f = await GappyAsync("DocsDone.bim");
            var id = (await f.Engine.AddPlanTaskAsync(new PlanTaskRequest
            {
                Words = "Write the purpose docs for Total Sales", SourcePage = "docs", DoneWhen = PlanDoneWhen.HasWords,
                Objects = new[] { "measure:Sales/Total Sales" }, Slot = "doc:purpose|measure:Sales/Total Sales",
            })).ItemId;
            // The object's Description is not the doc section: describing it must not close the task.
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "All sales.", "agent");
            Assert.True(PlanStateWords.IsOpen(Row(await f.Engine.GetModelPlanAsync(), id).StateWord));

            await f.Engine.SetDocSectionAsync("measure:Sales/Total Sales", "purpose", "What the business sells.", "agent");

            Assert.Equal(PlanStateWords.Done, Row(await f.Engine.GetModelPlanAsync(), id).StateWord);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_model_docs_task_closes_when_the_model_section_has_words()
        {
            using var f = await GappyAsync("ModelDocsDone.bim");
            var id = (await f.Engine.AddPlanTaskAsync(new PlanTaskRequest
            {
                Words = "Write the overview docs for the model", SourcePage = "docs", DoneWhen = PlanDoneWhen.HasWords,
                Slot = "doc:overview|model",
            })).ItemId;
            Assert.True(PlanStateWords.IsOpen(Row(await f.Engine.GetModelPlanAsync(), id).StateWord));

            await f.Engine.SetDocSectionAsync("model", "overview", "Sales for the whole company.", "agent");

            Assert.Equal(PlanStateWords.Done, Row(await f.Engine.GetModelPlanAsync(), id).StateWord);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_change_slot_task_closes_when_the_slot_has_a_value()
        {
            using var f = await GappyAsync("SlotDone.bim");
            var changes = await f.Engine.AddPlanItemAsync("measure:Sales/Total Sales", "set_description", null, "Describe Total Sales", null, null, "human");
            var slot = changes.Items.Single(i => i.ObjectRef == "measure:Sales/Total Sales" && i.Kind == "set_description");
            var id = (await f.Engine.AddPlanTaskAsync(new PlanTaskRequest
            {
                Words = "Write the description for Total Sales", SourcePage = "optimize", DoneWhen = PlanDoneWhen.HasWords,
                Objects = new[] { "measure:Sales/Total Sales" }, Slot = "change:" + slot.Id,
            })).ItemId;
            Assert.True(PlanStateWords.IsOpen(Row(await f.Engine.GetModelPlanAsync(), id).StateWord));

            await f.Engine.SetPlanItemAsync(slot.Id, "All sales, in dollars.", null, "agent");

            Assert.Equal(PlanStateWords.Done, Row(await f.Engine.GetModelPlanAsync(), id).StateWord);
        }
    }
}
