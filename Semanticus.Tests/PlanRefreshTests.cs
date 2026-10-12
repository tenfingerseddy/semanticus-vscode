// Refresh plan (Kane, 11 Oct 2026: "a refresh the plan button that clears out done tasks and checks for any new tasks").
//
// What these tests hold, one line each:
//   a refresh scans again now: a finding the kept scan could not see gets its row, which a plain read does not show
//   a refresh takes the finished rows off the list, keeps them in the plan's history, and says so in one sentence
//   a cleared row whose gap comes back (Undo) is open again, and finished again (Redo) it shows again
//   the same through refresh_plan on the gated MCP door, and the button and the tool are one engine call

using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanRefreshTests
    {
        [Fact]
        public async Task Refresh_counts_completed_tasks_that_left_the_live_plan_after_the_run()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("RefreshCompleted.bim");
            await f.Engine.GetModelPlanAsync();
            await CompleteMissingTasksAsync(f);
            var refreshed = await f.Engine.RefreshPlanAsync();
            Assert.Equal(8, refreshed.Cleared);
            Assert.Equal("8 done tasks cleared.", refreshed.Sentence);
            Assert.Equal("Nothing new.", (await f.Engine.RefreshPlanAsync()).Sentence);
        }

        // Verifier v-words2: tasks applied in an EARLIER session have no row and no Studio ghost (justDone resets on
        // reload). The list and get_model_plan show none of them, so Refresh must not say they were cleared.
        [Fact]
        public async Task Refresh_count_matches_the_done_tasks_the_plan_showed_when_applies_are_from_an_earlier_session()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("RefreshEarlier.bim");
            await f.Engine.GetModelPlanAsync();
            f.Engine.SeedPlanMemoryForTest(state =>
            {
                for (var n = 0; n < 8; n++)
                    state.Changes["jev.columns.table:Earlier" + n] = new PlanChangeHeader { State = ModelPlanChangeStates.Applied };
            });
            var shown = (await f.Engine.GetModelPlanAsync()).Items.Count(Done);
            var refreshed = await f.Engine.RefreshPlanAsync();
            Assert.Equal(shown, refreshed.Cleared);
        }

        [Theory]
        [InlineData("undone")]
        [InlineData("gone")]
        [InlineData("earlier session")]
        public async Task Refresh_does_not_count_missing_tasks_from_an_inactive_ledger_entry(string state)
        {
            const string file = "RefreshInactive.bim";
            using var f = await ChangeCauseTests.PlanModelAsync(file);
            await f.Engine.GetModelPlanAsync();
            await CompleteMissingTasksAsync(f);
            var sessionId = f.Sessions.Current.Id;
            if (state == "earlier session")
            {
                await f.Engine.SaveAsync(Path.Combine(f.Workspace, file), "bim", true, "human");
                await f.Engine.OpenAsync(Path.Combine(f.Workspace, file));
                Assert.NotEqual(sessionId, f.Sessions.Current.Id);
            }
            else
            {
                await f.Engine.UndoAsync("human");
                if (state == "gone")
                    await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "Another edit", "human");
            }

            // NewestByCause includes this model's earlier sessions, so membership alone is insufficient.
            var newest = f.Sessions.Ledger.NewestByCause(f.Sessions.Current.Id, ChangeCauseKinds.PlanItem);
            Assert.Equal(8, newest.Count);
            Assert.All(newest.Values, entry => Assert.Equal(state != "earlier session", entry.TakenBack));
            var shown = (await f.Engine.GetModelPlanAsync()).Items.Count(Done);
            Assert.Equal(shown, (await f.Engine.RefreshPlanAsync()).Cleared);
        }

        private static async Task CompleteMissingTasksAsync(DecisionFixture f)
        {
            var ids = Enumerable.Range(0, 8).Select(n => "jev.columns.table:Completed" + n).ToArray();
            using (ChangeCause.Begin(ChangeCauseKinds.PlanItem, "Completed tasks", "you", ids))
                await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "Completed", "human");
            f.Engine.SeedPlanMemoryForTest(state =>
            {
                foreach (var id in ids)
                {
                    state.Changes[id] = new PlanChangeHeader { State = ModelPlanChangeStates.Applied };
                    state.History.Add(new ModelPlanHistoryEntry { ItemId = id, Title = id,
                        Kind = ModelPlanHistoryKinds.Approved });
                }
            });
            Assert.Equal(ids.OrderBy(id => id), f.Sessions.Ledger
                .NewestByCause(f.Sessions.Current.Id, ChangeCauseKinds.PlanItem).Keys.OrderBy(id => id));
        }

        private static bool Done(ModelPlanItem i) => i.Status == ModelPlanStatuses.Done;
        private static bool Open(ModelPlanItem i) => i.Status != ModelPlanStatuses.Done && i.Status != ModelPlanStatuses.Skipped;

        /// <summary>Run the plan until nothing Semanticus can do is Ready, so its rows turn done.</summary>
        private static async Task RunAllAsync(LocalEngine engine)
        {
            for (var press = 0; press < 4; press++)
            {
                var plan = await engine.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.StateWord == PlanStateWords.Ready && i.Doer == PlanDoers.Semanticus)) return;
                var ran = await engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                Assert.True(ran.Performed, ran.Sentence);
            }
        }

        [Fact]
        public async Task A_refresh_scans_again_and_a_finding_the_kept_scan_missed_gets_its_row()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("RefreshNew.bim");
            await f.Engine.GetModelPlanAsync();
            var gathers = f.Engine.PlanGatherCountForTest;

            // A change the revision never saw: a table added straight into the model, outside any commit. The kept scan
            // cannot know about it, so a plain read still shows the old rows.
            await f.Sessions.Current.ReadAsync(m =>
            {
                var t = m.AddTable("Store");
                t.AddDataColumn("StoreKey", "StoreKey", null, TabularEditor.TOMWrapper.DataType.Int64);
                t.AddDataColumn("Region", "Region", null, TabularEditor.TOMWrapper.DataType.String);
                return 0;
            });
            var stale = await f.Engine.GetModelPlanAsync();
            Assert.Equal(gathers, f.Engine.PlanGatherCountForTest);   // the kept scan answered: nothing was scanned again

            var refreshed = await f.Engine.RefreshPlanAsync();
            Assert.True(f.Engine.PlanGatherCountForTest > gathers);
            Assert.True(refreshed.Added > 0, refreshed.Sentence);
            Assert.Equal(refreshed.Added, refreshed.AddedIds.Length);
            foreach (var id in refreshed.AddedIds)
            {
                Assert.DoesNotContain(stale.Items.Where(Open), i => i.Id == id);
                Assert.Contains(refreshed.Plan.Items.Where(Open), i => i.Id == id);
            }
            Assert.StartsWith(refreshed.Added + " new task", refreshed.Sentence);
            Assert.Equal(LocalEngine.RefreshSentence(refreshed.Added, refreshed.Cleared), refreshed.Sentence);
        }

        [Fact]
        public async Task A_refresh_clears_done_rows_off_the_list_and_keeps_them_in_the_history()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("RefreshDone.bim");
            await RunAllAsync(f.Engine);
            var ran = await f.Engine.GetModelPlanAsync();
            var done = ran.Items.Where(Done).ToList();
            Assert.NotEmpty(done);

            var refreshed = await f.Engine.RefreshPlanAsync();
            Assert.Equal(done.Count, refreshed.Cleared);
            Assert.Contains(done.Count + " done task", refreshed.Sentence);
            Assert.DoesNotContain(refreshed.Plan.Items, Done);
            // The open rows are all still there.
            Assert.Equal(ran.Items.Where(Open).Select(i => i.Id).OrderBy(x => x), refreshed.Plan.Items.Where(Open).Select(i => i.Id).OrderBy(x => x));

            // Kept, not deleted: the history names every cleared row, and a fresh read keeps them off the list.
            var entry = refreshed.Plan.History.Last(h => h.Sentence != null && h.Sentence.StartsWith("Refreshed the plan"));
            foreach (var row in done) Assert.Contains(row.Title, entry.Detail);
            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items, Done);

            // A second refresh has nothing to say.
            Assert.Equal("Nothing new.", (await f.Engine.RefreshPlanAsync()).Sentence);

            // Undo puts the gaps back, so the rows are open again; Redo finishes them again, and they show again as done.
            await f.Engine.UndoAsync("human");
            var undone = await f.Engine.GetModelPlanAsync();
            Assert.Contains(undone.Items.Where(Open), i => done.Any(d => d.Id == i.Id));
            await f.Engine.RedoAsync("human");
            var redone = await f.Engine.GetModelPlanAsync();
            Assert.Contains(redone.Items.Where(Done), i => done.Any(d => d.Id == i.Id));
        }

        [Fact]
        public async Task Refresh_plan_on_the_gated_MCP_door_does_what_the_button_does()
        {
            Assert.NotNull(typeof(EngineRpcTarget).GetMethod("refreshPlan"));
            Assert.Null(typeof(HumanGovernanceRpcTarget).GetMethod("refreshPlan"));
            var tool = typeof(McpTools).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == "refresh_plan");
            Assert.Equal(typeof(Task<PlanRefreshResult>), tool.ReturnType);

            using var f = await ChangeCauseTests.PlanModelAsync("RefreshMcp.bim");
            await RunAllAsync(f.Engine);
            var done = (await f.Engine.GetModelPlanAsync()).Items.Count(Done);
            Assert.True(done > 0);

            var gated = AssistantEngine.ForAssistant(f.Engine);
            var r = await McpTools.RefreshPlan(gated);
            Assert.Equal(done, r.Cleared);
            Assert.Equal(LocalEngine.RefreshSentence(r.Added, r.Cleared), r.Sentence);
            Assert.Null(r.Plan);   // the sentence says what changed; get_model_plan reads the plan
            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items, Done);
            Assert.Equal("Nothing new.", (await McpTools.RefreshPlan(gated)).Sentence);
        }

        // Verifier probe (v-refresh, 11 Oct): a second refresh that clears a NEW finished row must keep the rows the first
        // refresh cleared off the list. The compose already leaves them out, so the refresh never sees them as done.
        [Fact]
        public async Task A_second_refresh_keeps_the_rows_the_first_one_cleared_off_the_list()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("RefreshTwice.bim");
            await RunAllAsync(f.Engine);
            var first = await f.Engine.RefreshPlanAsync();
            Assert.True(first.Cleared > 0, first.Sentence);
            Assert.DoesNotContain(first.Plan.Items, Done);

            var added = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = "Tell the team the plan is ready" });
            var marked = await f.Engine.ModelPlanActionAsync(added.ItemId, "done", "human");
            Assert.Contains(marked.Plan.Items.Where(Done), i => i.Id == added.ItemId);

            var second = await f.Engine.RefreshPlanAsync();
            Assert.Equal(1, second.Cleared);
            Assert.DoesNotContain(second.Plan.Items, Done);
            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items, Done);
        }
    }
}
