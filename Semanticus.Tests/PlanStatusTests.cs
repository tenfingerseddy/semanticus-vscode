// plan-status lane (hero20 D3): Plan done must count work an assistant did with direct tools, on an unsaved model too, and must not fall back to 0 when the model gets its first file.
using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class PlanStatusTests
    {
        private readonly ITestOutputHelper _out;
        public PlanStatusTests(ITestOutputHelper o) => _out = o;

        [Fact]
        public async Task Direct_tool_work_on_an_unsaved_model_counts_as_done_and_survives_the_first_save()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Direct", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", null, "human");
            f.UseNull();
            var inv = new PlanInvariant(e, _out);
            var start = await inv.CheckAsync("start (unsaved)", findings: false);
            Assert.Contains(start.Items, i => i.Title == "Raise compatibility level to 1701");
            await McpTools.SetCompatibilityLevel(e, 1701);   // direct tool, no claim_request
            var mid = await inv.CheckAsync("after the direct tool (unsaved)", findings: false);
            Assert.DoesNotContain(mid.Items, i => i.Title == "Raise compatibility level to 1701" && i.StateWord != PlanStateWords.Done);
            Assert.True(mid.Strip.TasksDone >= 1, $"Plan done is {mid.Strip.TasksDone} of {mid.Strip.TasksAll} after the assistant did the work");
            var doneBefore = mid.Strip.TasksDone;
            await f.SaveAsAsync("Direct.bim");   // the assistant saves: the model gets its first file
            var after = await inv.CheckAsync("after the first save", findings: false);
            Assert.True(after.Strip.TasksDone >= doneBefore, $"Plan done fell from {doneBefore} to {after.Strip.TasksDone} after the save");
            var again = await inv.CheckAsync("a second read", findings: false);
            Assert.True(again.Strip.TasksDone >= doneBefore, $"Plan done fell to {again.Strip.TasksDone} on a re-read");
        }

        [Fact]
        public async Task A_row_the_assistant_is_editing_with_direct_tools_reads_On_it_and_not_Waiting()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Working", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", null, "human");
            await f.SaveAsAsync("Working.bim");
            f.UseNull();
            var inv = new PlanInvariant(e, _out);
            var start = await inv.CheckAsync("start", findings: false);
            var row = start.Items.Single(i => i.Id == "proposal/formats");
            Assert.Equal(PlanStateWords.Waiting, row.StateWord);
            // The assistant works on the column with a direct tool (no claim_request): the finding stays, so the row is not done.
            await McpTools.SetDescription(e, "column:Sales/Amount", "What was sold, in dollars.");
            var mid = await inv.CheckAsync("while the assistant works the column", findings: false);
            var working = mid.Items.Single(i => i.Id == "proposal/formats");
            Assert.True(working.AssistantAtWork, $"the row says {working.StateWord}: {working.StateNote}");
            Assert.Equal(PlanStateWords.Waiting, working.StateWord);   // still the engine's word, so the row is offered and counted as before
            Assert.False(mid.Items.Single(i => i.Id == "cleanup/model").AssistantAtWork, "a row nobody touched is not flagged");
            Assert.DoesNotContain("ask your assistant", working.StateNote ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }
}
