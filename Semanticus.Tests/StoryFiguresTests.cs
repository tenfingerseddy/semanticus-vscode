// The Plan header's three figures (1.5, Kane's pick A "Three small figures", 12 Oct 2026).
//
// What these tests hold, one line each:
//   AI ready is the AI readiness scan's grade and its score (the reading card the header reads is the same scan)
//   Best practice is the BPA scan's score and letter, the numbers the Review page shows
//   Plan done counts every task: done rows, the done records, and the rows Refresh plan cleared (History), over those and the open ones
//   after a one-row run all three still equal their source, read again from the engine

using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class StoryFiguresTests
    {
        private static async Task AssertFiguresMatchSourcesAsync(DecisionFixture f, ModelPlanView plan)
        {
            var bpa = await f.Engine.BpaScanAsync();
            var ai = await f.Engine.AiReadinessScanAsync();
            var card = await f.Engine.ReadingCardAsync(withAfter: false);

            Assert.Equal(ai.Grade, card.Grade);
            Assert.Equal((int)Math.Round(ai.Overall, MidpointRounding.AwayFromZero), card.Score);

            Assert.NotNull(plan.Strip);
            Assert.Equal(bpa.BestPractice, plan.Strip.BestPractice);
            Assert.Equal(bpa.BestPracticeScore, plan.Strip.BestPracticeScore);

            var rows = plan.Items.Where(i => i.Id != null).GroupBy(i => i.Id).Select(g => g.First()).ToArray();
            var open = rows.Count(i => i.StateWord != PlanStateWords.Done && i.StateWord != PlanStateWords.SetAside);
            Assert.True(plan.Strip.TasksDone >= rows.Count(i => i.StateWord == PlanStateWords.Done));
            Assert.Equal(plan.Strip.TasksDone + open, plan.Strip.TasksAll);
        }

        [Fact]
        public async Task The_three_figures_equal_their_sources_at_rest_and_after_a_one_row_run()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("StoryFigs.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            await AssertFiguresMatchSourcesAsync(f, plan);
            var all0 = plan.Strip.TasksAll;
            var done0 = plan.Strip.TasksDone;
            Assert.True(all0 > 0, "the fixture has tasks on its plan");

            var row = plan.Items.First(i => i.Doer == "semanticus" && i.StateWord == "ready" && !i.Id.StartsWith("run/"));
            var worked = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human", null);
            var digest = worked.Plan?.Items.FirstOrDefault(i => i.Id == row.Id)?.ChangeSet?.Digest;
            Assert.False(string.IsNullOrEmpty(digest));
            var r = await f.Engine.ApplyPlanItemsAsync(new[] { new PlanItemDigest { Id = row.Id, Digest = digest } }, "human", null);
            Assert.True(r.Performed, r.Sentence);

            var after = await f.Engine.GetModelPlanAsync();
            await AssertFiguresMatchSourcesAsync(f, after);
            Assert.True(after.Strip.TasksDone > done0, "the row that ran counts as done");
            Assert.True(after.Strip.TasksDone <= after.Strip.TasksAll);
        }

        [Fact]
        public async Task Refresh_plan_clears_done_rows_from_the_list_but_Plan_done_keeps_counting_them()
        {
            using var f = await ChangeCauseTests.PlanModelAsync("StoryClear.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var row = plan.Items.First(i => i.Doer == "semanticus" && i.StateWord == "ready" && !i.Id.StartsWith("run/"));
            var worked = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "human", null);
            var digest = worked.Plan?.Items.FirstOrDefault(i => i.Id == row.Id)?.ChangeSet?.Digest;
            var r = await f.Engine.ApplyPlanItemsAsync(new[] { new PlanItemDigest { Id = row.Id, Digest = digest } }, "human", null);
            Assert.True(r.Performed, r.Sentence);
            var done = await f.Engine.GetModelPlanAsync();
            var doneBefore = done.Strip.TasksDone;
            var allBefore = done.Strip.TasksAll;
            Assert.True(doneBefore >= 1);

            await f.Engine.RefreshPlanAsync();
            var cleared = await f.Engine.GetModelPlanAsync();
            Assert.False(cleared.Items.Any(i => i.StateWord == PlanStateWords.Done), "Refresh plan took the done rows off the list");
            Assert.True(cleared.Strip.TasksDone >= doneBefore, "History still holds them, so Plan done keeps them");
            Assert.Equal(allBefore, cleared.Strip.TasksAll);   // clearing a done row from the list is not a new task and not a lost one
            Assert.Equal(cleared.Strip.TasksDone + cleared.Items.Count(i => i.StateWord != PlanStateWords.Done && i.StateWord != PlanStateWords.SetAside), cleared.Strip.TasksAll);
        }
    }
}
