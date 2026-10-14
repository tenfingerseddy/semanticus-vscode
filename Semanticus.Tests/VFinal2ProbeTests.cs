// Verifier v-final2 (14 Oct 2026): the two final2 fixes on a SAVED model. A row whose findings were all declined is never
// counted done; a row the assistant really fixed with direct tools still is; the date row stays away after DATE-MARK is accepted.
using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class VFinal2ProbeTests
    {
        private const string Why = "Declined on purpose.";
        private readonly ITestOutputHelper _out;
        public VFinal2ProbeTests(ITestOutputHelper o) => _out = o;

        private static async Task<DecisionFixture> StarAsync(string file, bool dates = false)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("VFinal2", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "ProductKey", "Int64", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await e.CreateTableAsync("Product", "human");
            await e.CreateColumnAsync("table:Product", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Product", "Name", "String", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            if (dates)
            {
                await e.CreateColumnAsync("table:Sales", "Order Date", "DateTime", null, "human");
                await e.CreateTableAsync("Date", "human");
                await e.CreateColumnAsync("table:Date", "Date", "DateTime", null, "human");
                await e.CreateRelationshipAsync("column:Sales/Order Date", "column:Date/Date", null, true, "human");
                await e.CreateMeasureAsync("table:Sales", "Sales YTD", "TOTALYTD ( [Total Sales], 'Date'[Date] )", "human");
            }
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        private void Dump(string label, ModelPlanView p) =>
            _out.WriteLine($"{label}: done {p.Strip?.TasksDone}/{p.Strip?.TasksAll}; " + string.Join(", ", p.Items.Select(i => $"{i.Id}={i.Status}")));

        [Fact]
        public async Task Saved_a_row_whose_findings_were_all_declined_is_never_counted_done()
        {
            using var f = await StarAsync("VF2Declined.bim");
            var inv = new PlanInvariant(f.Engine, _out);
            var start = await inv.CheckAsync("start (saved)", findings: false);
            Dump("start", start);
            Assert.Contains(start.Items, i => i.Id == "safefix/Visibility" && i.Status != ModelPlanStatuses.Done);
            var doneBefore = start.Strip.TasksDone;

            await McpTools.WaiveFinding(f.Engine, "bpa", "HIDE_FOREIGN_KEYS", "column:Sales/CustomerKey", Why);
            await new EngineRpcTarget(f.Engine).waiveFinding("bpa", "HIDE_FOREIGN_KEYS", "column:Sales/ProductKey", Why);
            // an unrelated assistant edit after the decline must not turn the declined row into the assistant's done work
            await McpTools.SetDescription(f.Engine, "column:Sales/Amount", "What was sold.");

            for (var n = 1; n <= 3; n++)
            {
                var p = await inv.CheckAsync("after decline, read " + n, findings: false);
                Dump("read " + n, p);
                Assert.DoesNotContain(p.Items, i => i.Id == "safefix/Visibility");
                Assert.True(p.Strip.TasksDone <= doneBefore, $"Plan done rose from {doneBefore} to {p.Strip.TasksDone} after a decline");
            }
            await f.SaveAsAsync("VF2Declined.bim");
            var afterSave = await inv.CheckAsync("after save", findings: false);
            Dump("after save", afterSave);
            Assert.DoesNotContain(afterSave.Items, i => i.Id == "safefix/Visibility" && i.Status == ModelPlanStatuses.Done);
        }

        [Fact]
        public async Task Saved_a_row_the_assistant_fixed_with_direct_tools_counts_done()
        {
            using var f = await StarAsync("VF2Fixed.bim");
            var inv = new PlanInvariant(f.Engine, _out);
            var start = await inv.CheckAsync("start (saved)", findings: false);
            Dump("start", start);
            var row = start.Items.Single(i => i.Id == "safefix/Visibility");
            _out.WriteLine("row who " + row.Who);
            var doneBefore = start.Strip.TasksDone;

            await f.Engine.SetObjectPropertiesAsync(new[] { "column:Sales/CustomerKey", "column:Sales/ProductKey" }, "IsHidden", "true", "agent");
            var p = await inv.CheckAsync("after the assistant hid the keys", findings: false);
            Dump("after", p);
            if (p.Items.Any(i => i.Id == "safefix/Visibility" && i.Status != ModelPlanStatuses.Done))
            {
                // the row may still hold the MDX line for the hidden keys: finish with the safe fixes from the assistant's door
                await f.Engine.ApplySafeFixesAsync("agent");
                p = await inv.CheckAsync("after the assistant's safe fixes", findings: false);
                Dump("after2", p);
            }
            Assert.Contains(p.Items, i => i.Id == "safefix/Visibility" && i.Status == ModelPlanStatuses.Done);
            Assert.True(p.Strip.TasksDone > doneBefore, $"Plan done stayed {p.Strip.TasksDone} after the assistant fixed the row");
        }

        [Fact]
        public async Task Saved_mixed_assistant_fixed_one_person_declined_the_other_reports_what_happens()
        {
            using var f = await StarAsync("VF2Mixed.bim");
            var inv = new PlanInvariant(f.Engine, _out);
            var start = await inv.CheckAsync("start (saved)", findings: false);
            Dump("start", start);
            await f.Engine.SetObjectPropertiesAsync(new[] { "column:Sales/CustomerKey" }, "IsHidden", "true", "agent");
            await McpTools.WaiveFinding(f.Engine, "bpa", "HIDE_FOREIGN_KEYS", "column:Sales/ProductKey", Why);
            var p = await inv.CheckAsync("mixed", findings: false);
            Dump("mixed", p);
            // The assistant fixed one key and the person declined the other: the row keeps the assistant's credit. Plan done counts it, and it
            // stays done on a re-read (a declined row is never done, but real work keeps its credit).
            Assert.Contains(p.Items, i => i.Id == "safefix/Visibility" && i.Status == ModelPlanStatuses.Done);
            Assert.True(p.Strip.TasksDone > start.Strip.TasksDone, $"Plan done did not count the assistant's fix: {start.Strip.TasksDone} before, {p.Strip.TasksDone} after");
            var again = await inv.CheckAsync("mixed, read again", findings: false);
            Dump("mixed again", again);
            Assert.Contains(again.Items, i => i.Id == "safefix/Visibility" && i.Status == ModelPlanStatuses.Done);
            Assert.Equal(p.Strip.TasksDone, again.Strip.TasksDone);
        }

        [Fact]
        public async Task Saved_date_row_seen_then_DATE_MARK_accepted_never_comes_back_and_is_not_done()
        {
            using var f = await StarAsync("VF2Date.bim", dates: true);
            var inv = new PlanInvariant(f.Engine, _out);
            var start = await inv.CheckAsync("start (saved)", findings: false);
            Dump("start", start);
            Assert.Contains(start.Items, i => i.Id == "datetable/model");
            var doneBefore = start.Strip.TasksDone;
            var dm = (await f.Engine.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "DATE-MARK");
            await McpTools.WaiveFinding(f.Engine, "air", "DATE-MARK", dm.ObjectRef, Why);
            for (var n = 1; n <= 3; n++)
            {
                var p = await inv.CheckAsync("after accept, read " + n, findings: false);
                Dump("read " + n, p);
                Assert.DoesNotContain(p.Items, i => i.Id is "datetable/model" or "calendar/model");
                Assert.True(p.Strip.TasksDone <= doneBefore, $"Plan done rose from {doneBefore} to {p.Strip.TasksDone} after accepting DATE-MARK");
            }
            await McpTools.ProposePlan(f.Engine);
            await McpTools.ApplyPlan(f.Engine);
            var after = await inv.CheckAsync("after run the plan", findings: false);
            Dump("after run", after);
            Assert.DoesNotContain(after.Items, i => i.Id is "datetable/model" or "calendar/model");
        }
        [Fact]
        public async Task Saved_one_key_declined_earlier_then_the_assistant_fixes_what_the_row_still_asks_counts_done()
        {
            using var f = await StarAsync("VF2Earlier.bim");
            var inv = new PlanInvariant(f.Engine, _out);
            await McpTools.WaiveFinding(f.Engine, "bpa", "HIDE_FOREIGN_KEYS", "column:Sales/ProductKey", Why);
            var start = await inv.CheckAsync("start, one key declined before the row was seen", findings: false);
            Dump("start", start);
            Assert.Contains(start.Items, i => i.Id == "safefix/Visibility" && i.Status != ModelPlanStatuses.Done);
            var doneBefore = start.Strip.TasksDone;
            await f.Engine.SetObjectPropertiesAsync(new[] { "column:Sales/CustomerKey" }, "IsHidden", "true", "agent");
            var p = await inv.CheckAsync("after the assistant hid the one key the row asked for", findings: false);
            Dump("after", p);
            if (p.Items.Any(i => i.Id == "safefix/Visibility" && i.Status != ModelPlanStatuses.Done))
            {
                await f.Engine.ApplySafeFixesAsync("agent");
                p = await inv.CheckAsync("after the assistant's safe fixes", findings: false);
                Dump("after2", p);
            }
            Assert.Contains(p.Items, i => i.Id == "safefix/Visibility" && i.Status == ModelPlanStatuses.Done);
            Assert.True(p.Strip.TasksDone > doneBefore, $"Plan done stayed {p.Strip.TasksDone} after the assistant fixed what the row asked");
        }
        // Round 2 (fix-final2): the row's own remembered findings decide it; an older plan file without them falls back.
        private static int StripClaims(DecisionFixture f)
        {
            var file = System.IO.Path.Combine(f.Workspace, ".semanticus", PlanMemory.FileName);
            var root = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(file))!;
            var n = 0;
            void Walk(System.Text.Json.Nodes.JsonNode? node)
            {
                if (node is System.Text.Json.Nodes.JsonObject o)
                {
                    if (o.Remove("claims")) n++;
                    foreach (var kv in o.ToList()) Walk(kv.Value);
                }
                else if (node is System.Text.Json.Nodes.JsonArray a) foreach (var c in a.ToList()) Walk(c);
            }
            Walk(root);
            System.IO.File.WriteAllText(file, root.ToJsonString());
            return n;
        }

        [Fact]
        public async Task R2_older_plan_file_without_keys_declined_row_is_not_done()
        {
            using var f = await StarAsync("VF2OldDecl.bim");
            var inv = new PlanInvariant(f.Engine, _out);
            var start = await inv.CheckAsync("start", findings: false);
            Assert.Contains(start.Items, i => i.Id == "safefix/Visibility");
            Assert.True(StripClaims(f) > 0, "the plan file carried no claims to strip");
            await McpTools.WaiveFinding(f.Engine, "bpa", "HIDE_FOREIGN_KEYS", "column:Sales/CustomerKey", Why);
            await McpTools.WaiveFinding(f.Engine, "bpa", "HIDE_FOREIGN_KEYS", "column:Sales/ProductKey", Why);
            var p = await inv.CheckAsync("after decline, older file", findings: false);
            Dump("old decl", p);
            Assert.DoesNotContain(p.Items, i => i.Id == "safefix/Visibility");
            Assert.Equal(start.Strip.TasksDone, p.Strip.TasksDone);
        }

        [Fact]
        public async Task R2_older_plan_file_without_keys_assistant_fixed_row_is_done()
        {
            using var f = await StarAsync("VF2OldFix.bim");
            var inv = new PlanInvariant(f.Engine, _out);
            var start = await inv.CheckAsync("start", findings: false);
            Assert.True(StripClaims(f) > 0, "the plan file carried no claims to strip");
            await f.Engine.SetObjectPropertiesAsync(new[] { "column:Sales/CustomerKey", "column:Sales/ProductKey" }, "IsHidden", "true", "agent");
            var p = await inv.CheckAsync("after fix, older file", findings: false);
            Dump("old fix", p);
            Assert.Contains(p.Items, i => i.Id == "safefix/Visibility" && i.Status == ModelPlanStatuses.Done);
            Assert.True(p.Strip.TasksDone > start.Strip.TasksDone);
        }

        [Fact]
        public async Task R2_one_finding_gone_one_declined_after_seen_keeps_the_credit()
        {
            using var f = await StarAsync("VF2Mixed2.bim");
            var inv = new PlanInvariant(f.Engine, _out);
            var start = await inv.CheckAsync("start", findings: false);
            var file = System.IO.Path.Combine(f.Workspace, ".semanticus", PlanMemory.FileName);
            Assert.Contains("VIS-FK|column:Sales/CustomerKey", System.IO.File.ReadAllText(file));   // the keys are on disk
            await f.Engine.SetObjectPropertiesAsync(new[] { "column:Sales/CustomerKey" }, "IsHidden", "true", "agent");
            await McpTools.WaiveFinding(f.Engine, "bpa", "HIDE_FOREIGN_KEYS", "column:Sales/ProductKey", Why);
            var p = await inv.CheckAsync("mixed", findings: false);
            Dump("mixed2", p);
            Assert.Contains(p.Items, i => i.Id == "safefix/Visibility" && i.Status == ModelPlanStatuses.Done);
            var again = await inv.CheckAsync("mixed re-read", findings: false);
            Assert.Equal(p.Strip.TasksDone, again.Strip.TasksDone);
        }
    }
}
