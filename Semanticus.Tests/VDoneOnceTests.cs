// VERIFIER round 2 (v-coverage-fix): a finished assistant task is ONE done row and counts once in Plan done.
using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class VDoneOnceTests
    {
        private readonly ITestOutputHelper _out;
        public VDoneOnceTests(ITestOutputHelper o) => _out = o;

        [Fact]
        public async Task A_finished_assistant_task_is_one_done_row_and_counts_once()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Once", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", null, "human");
            await f.SaveAsAsync("Once.bim");
            f.UseNull();
            var inv = new PlanInvariant(e, _out);
            var plan = await inv.CheckAsync("start");
            Assert.Contains(plan.Items, i => i.Title == "Raise compatibility level to 1701");
            await McpTools.ApplyPlanItems(e, null, plan.ReadyDigest);
            await inv.CheckAsync("after Run the plan");
            var req = (await McpTools.ListAssistantRequests(e)).Requests.Single(r => r.Need == "Raise compatibility level to 1701");
            var c = await McpTools.ClaimRequest(e, req.RequestId);
            Assert.True(c.Ok, c.Message);
            await inv.CheckAsync("after claim");
            await McpTools.SetCompatibilityLevel(e, 1701);
            var d = await McpTools.CompleteRequest(e, req.RequestId, c.Request.ClaimToken, "Raised.");
            Assert.True(d.Ok, d.Message);
            var after = await inv.CheckAsync("after done");
            var rows = after.Items.Where(i => i.Title == "Raise compatibility level to 1701").ToArray();
            foreach (var i in after.Items) _out.WriteLine($"{i.Id} | {i.StateWord} | {i.Title}");
            _out.WriteLine($"strip {after.Strip?.TasksDone}/{after.Strip?.TasksAll}");
            Assert.True(after.Strip?.TasksDone == 3 && after.Strip?.TasksAll == 8, $"Plan done {after.Strip?.TasksDone} of {after.Strip?.TasksAll}, the rows by hand give 3 of 8");
            Assert.True(rows.Length == 1, "the raise is on the Plan " + rows.Length + " times: " + string.Join(", ", rows.Select(r => r.Id + " " + r.StateWord)));
        }
    }
}
