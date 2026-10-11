using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Who made each edit, and what it served (Plan-heart contract 8.2 and 8.4). The cause is stamped inside the same
    /// call that writes the edit, from an ambient scope the writer opened, and is never joined afterwards by revision:
    /// two commits at the same revision number in two sessions stay two separate rows.
    /// </summary>
    public sealed class ChangeCauseTests
    {
        /// <summary>A small saved model with two safe-fix rows on the plan (an unhidden key, a summing key).</summary>
        internal static Task<DecisionFixture> PlanModelAsync(string file) => PlanModelAsync(file, TestEntitlements.Pro);

        internal static async Task<DecisionFixture> PlanModelAsync(string file, Semanticus.Engine.Entitlement.IEntitlement tier)
        {
            var f = new DecisionFixture(tier);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "ProductKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Product", "human");
            await f.Engine.CreateColumnAsync("table:Product", "ProductKey", "Int64", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
            await f.Engine.SetColumnMetadataAsync("column:Sales/ProductKey", null, "Sum", null, null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        private static List<ChangeNotification> Watch(SessionManager sessions)
        {
            var seen = new List<ChangeNotification>();
            sessions.Bus.Changed += n => { if (n.Deltas?.Length > 0) seen.Add(n); };
            return seen;
        }

        [Fact]
        public async Task A_person_and_an_assistant_editing_directly_are_you_and_assistant_with_no_cause()
        {
            using var f = await PlanModelAsync("Direct.bim");
            var seen = Watch(f.Sessions);
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "by a person", "human");
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "by an assistant", "agent");

            Assert.Equal(new[] { "you", "assistant" }, seen.Select(n => n.By));
            Assert.All(seen, n => Assert.Null(n.Cause));
            var entries = f.Sessions.Ledger.Entries(f.Sessions.Current.Id).TakeLast(2).ToArray();
            Assert.Equal(new[] { "you", "assistant" }, entries.Select(e => e.By));
            Assert.Equal(new[] { "human", "agent" }, entries.Select(e => e.Origin));   // Origin is untouched
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_plan_approve_is_semanticus_with_the_item_and_who_approved_it()
        {
            using var f = await PlanModelAsync("Approve.bim");
            var row = (await f.Engine.GetModelPlanAsync()).Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal));
            var proposed = await f.Engine.ModelPlanActionAsync(row.Id, "propose", "agent");
            var digest = proposed.Plan.Items.First(i => i.Id == row.Id).ChangeSet.Digest;
            var seen = Watch(f.Sessions);

            var result = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "agent", digest);
            Assert.True(result.Performed);

            var n = Assert.Single(seen);
            Assert.Equal("semanticus", n.By);
            Assert.Equal("plan_item", n.Cause.Kind);
            Assert.Equal(new[] { row.Id }, n.Cause.Ids);
            Assert.Equal("assistant", n.Cause.ApprovedBy);
            var entry = f.Sessions.Ledger.Entries(f.Sessions.Current.Id).Last();
            Assert.Equal("semanticus", entry.By);
            Assert.Equal(row.Id, entry.Cause.Ids.Single());
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_run_of_the_plan_is_one_commit_naming_every_item_in_the_batch()
        {
            using var f = await PlanModelAsync("Bulk.bim");
            var plan = await f.Engine.GetModelPlanAsync();
            var ids = plan.Items.Where(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal)).Select(i => i.Id).Take(2).ToArray();
            Assert.Equal(2, ids.Length);
            foreach (var id in ids) await f.Engine.ModelPlanActionAsync(id, "propose", "human");
            var ready = await f.Engine.GetModelPlanAsync();
            var sent = ids.Select(id => new PlanItemDigest { Id = id, Digest = ready.Items.First(i => i.Id == id).ChangeSet.Digest }).ToArray();
            var seen = Watch(f.Sessions);

            var result = await f.Engine.ApplyPlanItemsAsync(sent, "human");
            Assert.True(result.Performed, result.Sentence);

            var n = Assert.Single(seen);
            Assert.Equal("semanticus", n.By);
            Assert.Equal("plan_item", n.Cause.Kind);
            Assert.Equal(ids.OrderBy(x => x), n.Cause.Ids.OrderBy(x => x));
            Assert.Equal("you", n.Cause.ApprovedBy);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_workflow_run_scope_makes_its_writes_workflow_and_names_the_run()
        {
            // A guided step's submission is where the run's cause scope opens (LocalEngine.Workflows.cs). A submit
            // writes nothing today, so the scope is exercised directly: the same Begin the submit path calls.
            using var f = await PlanModelAsync("Workflow.bim");
            var seen = Watch(f.Sessions);
            using (ChangeCause.Begin(ChangeCauseKinds.WorkflowRun, "tidy-measures", null, "wfr-0a1b2c3d"))
                await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "from a step", "agent");
            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "after the scope closed", "agent");

            Assert.Equal("workflow", seen[0].By);
            Assert.Equal("workflow_run", seen[0].Cause.Kind);
            Assert.Equal("wfr-0a1b2c3d", seen[0].Cause.Ids.Single());
            Assert.Equal("assistant", seen[1].By);
            Assert.Null(seen[1].Cause);
        }

        [Fact]
        public async Task Two_commits_at_one_revision_in_two_sessions_stay_apart()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-cause-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(LedgerFixture.Write(dir, "A.bim"));
                var first = sessions.Current.Id;
                using (ChangeCause.Begin(ChangeCauseKinds.PlanItem, "An item", "you", "safefix/one"))
                    await engine.SetDescriptionAsync("measure:Sales/M1", "in A", "human");
                await engine.OpenAsync(LedgerFixture.Write(dir, "B.bim"), discardUnsaved: true);
                var second = sessions.Current.Id;
                await engine.SetDescriptionAsync("measure:Sales/M1", "in B", "human");

                var a = sessions.Ledger.Entries(first).Single();
                var b = sessions.Ledger.Entries(second).Single();
                Assert.Equal(a.Revision, b.Revision);        // the same revision number in both sessions
                Assert.NotEqual(a.EntryId, b.EntryId);
                Assert.Equal("semanticus", a.By);
                Assert.Equal("you", b.By);
                Assert.Null(b.Cause);                        // nothing from A's cause leaked onto B's row
                Assert.False(EditLedger.IsUndoable(a, second, 1));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
