// VERIFIER round 3 (v-coverage-fix): the de-dup must not hide an open row whose gap stands.
// The assistant says done on a task without doing it: the row is open on the Plan, once, and it names the finding.
using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class VNoHideTests
    {
        private readonly ITestOutputHelper _out;
        public VNoHideTests(ITestOutputHelper o) => _out = o;

        // (The verifier also ran "Hide 1 technical field", a task the Run makes with no row of that title at the start: there is no row to reopen, so the premise does not hold.)
        [Theory, InlineData("Fix 1 best-practice issue", "Fix 1 best-practice issue")]
        public async Task A_task_said_done_with_its_gap_standing_leaves_one_open_row(string need, string rowTitle)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("NoHide", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", null, "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", "OneDirection", true, "human");
            await e.CreateMeasureAsync("table:Sales", "Safe Total", "IFERROR ( SUM ( Sales[Amount] ), 0 )", "human");
            await f.SaveAsAsync("NoHide.bim");
            f.UseNull();
            var inv = new PlanInvariant(e, _out);
            var plan = await inv.CheckAsync("start");
            var rules = plan.Items.Single(i => i.Title == rowTitle).Rules ?? Array.Empty<string>();
            _out.WriteLine("row rules: " + string.Join(",", rules));
            Assert.NotEmpty(rules);
            await McpTools.ApplyPlanItems(e, null, plan.ReadyDigest);
            await inv.CheckAsync("after Run the plan");
            var q = await McpTools.ListAssistantRequests(e);
            _out.WriteLine("offered: " + string.Join(" ; ", q.Requests.Select(r => r.Need)) + " | not offered: " + string.Join(" ; ", q.NotOffered.Select(n => n.Title)));
            var req = q.Requests.Concat(q.Held).FirstOrDefault(r => r.Need == need);
            Assert.True(req != null, "not offered: " + need);
            var c = await McpTools.ClaimRequest(e, req.RequestId);
            Assert.True(c.Ok, c.Message);
            await inv.CheckAsync("after claim");
            // Says done, does nothing.
            var d = await McpTools.CompleteRequest(e, req.RequestId, c.Request.ClaimToken, "Done.");
            Assert.True(d.Ok, d.Message);
            var after = await inv.CheckAsync("after done, gap standing");
            foreach (var i in after.Items) _out.WriteLine($"{i.Id} | {i.StateWord} | {i.Title} | rules={string.Join(",", i.Rules ?? Array.Empty<string>())}");
            _out.WriteLine($"strip {after.Strip?.TasksDone}/{after.Strip?.TasksAll}");
            var same = after.Items.Where(i => i.Title == need).ToArray();
            Assert.True(same.Length == 1, need + " is listed " + same.Length + " times");
            Assert.True(PlanStateWords.IsOpen(same[0].StateWord) || same[0].StateWord == PlanStateWords.Checking, "the job reads " + same[0].StateWord + " while its gap stands");
            Assert.Contains(after.Items, i => PlanStateWords.IsOpen(i.StateWord) && (i.Rules ?? Array.Empty<string>()).Intersect(rules).Any());
            Assert.DoesNotContain(after.Items, i => i.Title == need && i.StateWord == PlanStateWords.Done);
        }
    }
}
