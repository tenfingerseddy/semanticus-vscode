// Who does each Plan row (Kane, 13 Oct 2026, on the 1.4.101 test build): "the assistant only seems to execute its tasks in the plan but
// it should try and do all tasks ... assigning tasks to a human is the last resort."
//
// What these tests hold, one line each:
//   a check row whose report reading was lost (a restart) stays with the assistant; it never falls to the person
//   "Look at 1 report again" is the assistant's: it reads the reports with check_reports
//   "Save the changes you have made" is the assistant's: save_model is one call
//   a Semanticus row whose every change is unticked never says Needs you; it goes to the assistant, which asks
//   the journey: Kane's plan after a restart, Run the plan, and the assistant takes every check row and the save off its queue
//   every model-edit tool says the edit is local until a save back or a publish, and that undo takes it back (Claude's own safety
//   check refused delete_objects and define_calendar "as a change to a shared resource" on a workspace-linked model, 13 Oct)

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanDoerTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public PlanDoerTests(ModelTemplates templates) => _templates = templates;

        private async Task<DecisionFixture> GappyAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "gappy-doer", BuildGappyAsync, file);
            f.UseNull();
            return f;
        }

        private static async Task BuildGappyAsync(DecisionFixture f)
        {
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Lines", "COUNTROWS ( Sales )", "human");
        }

        /// <summary>A local report that shows both measures, chosen for the model. Not read yet.</summary>
        private static async Task ChooseReportAsync(DecisionFixture f)
        {
            string Field(string m) => "{ \"field\": { \"Measure\": { \"Expression\": { \"SourceRef\": { \"Entity\": \"Sales\" } }, \"Property\": \"" + m + "\" } } }";
            var visual = "{ \"visual\": { \"query\": { \"queryState\": { \"Values\": { \"projections\": [ "
                       + Field("Total Sales") + ", " + Field("Order Lines") + " ] } } } } }";
            var reports = Path.Combine(f.Workspace, "Reports");
            var visualDir = Path.Combine(reports, "Sales Overview.Report", "definition", "pages", "p1", "visuals", "v1");
            Directory.CreateDirectory(visualDir);
            File.WriteAllText(Path.Combine(reports, "Sales Overview.Report", "definition", "report.json"), "{}");
            File.WriteAllText(Path.Combine(visualDir, "visual.json"), visual);
            await f.Engine.SetReportScopeAsync(
                new[] { new Semanticus.Engine.Lineage.ReportScopeChoice { Kind = "local", Name = "Sales Overview", Path = reports } }, "human");
        }

        private static ModelPlanItem Row(ModelPlanView plan, string id) => plan.Items.Single(i => i.Id == id);

        // ---- the doer of each row ------------------------------------------------------------------------------

        [Theory]
        [InlineData(PlanDoers.Assistant)]
        [InlineData(null)]   // a plan file written before the doer was remembered
        public async Task A_check_row_whose_reports_could_not_be_read_stays_with_the_assistant(string seenDoer)
        {
            using var f = await GappyAsync("DoerUnchecked.bim");
            await f.Engine.GetModelPlanAsync();
            Assert.True(f.Engine.SeedPlanMemoryForTest(state => state.LastItems.Add(new PlanSeenItem
            {
                ItemId = "check/measure:Sales/Total Sales", Title = "Add a check for Total Sales",
                Section = ModelPlanSections.Trust, Doer = seenDoer,
            })));

            var row = Row(await f.Engine.GetModelPlanAsync(), "check/measure:Sales/Total Sales");

            Assert.Contains("could not be checked", row.Sub, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(PlanDoers.Assistant, row.Doer);
            Assert.NotEqual(PlanStateWords.NeedsYou, row.StateWord);
        }

        [Fact]
        public async Task The_report_row_and_the_save_row_are_the_assistants()
        {
            using var f = await GappyAsync("DoerReportSave.bim");
            await ChooseReportAsync(f);   // chosen, never read: the plan asks for it to be looked at
            await f.Engine.SetDescriptionAsync("table:Sales", "One row per order line.", "human");   // an unsaved change

            var plan = await f.Engine.GetModelPlanAsync();

            var report = Row(plan, "recheck/reports");
            Assert.Equal(PlanDoers.Assistant, report.Doer);
            Assert.NotEqual(PlanStateWords.NeedsYou, report.StateWord);
            var save = Row(plan, "ship/unsaved");
            Assert.Equal(PlanDoers.Assistant, save.Doer);
            Assert.NotEqual(PlanStateWords.NeedsYou, save.StateWord);
        }

        /// <summary>Kane, 13 Oct: "Add folders and formats to 1 measure in Discount Scenario" sat under Semanticus saying Needs you, beside an
        /// Approve that could not be pressed (every change was unticked because Jev was not sure). A row never says Needs you with a dead
        /// button: nothing is kept, so Semanticus has nothing to run, and choosing what to keep is the assistant's to ask about.</summary>
        [Fact]
        public void A_Semanticus_row_with_every_change_unticked_goes_to_the_assistant_and_never_says_needs_you()
        {
            var item = new ModelPlanItem
            {
                Id = "jev/measures.Discount Scenario", Title = "Add folders and formats to 1 measure in Discount Scenario",
                Doer = PlanDoers.Semanticus, Status = ModelPlanStatuses.Proposed, WaitsFor = Array.Empty<PlanWaitRef>(),
                ChangeSet = new ModelPlanChangeSet
                {
                    State = ModelPlanChangeStates.Proposed, Digest = "d1",
                    Lines = new[] { new ModelPlanChangeLine { Id = "l1", Target = "measure:Discount Scenario/Discount", Kept = false } },
                },
            };

            PlanTasks.StampState(item, null, false, PlanAssistantQueue.On);

            Assert.NotEqual(PlanStateWords.NeedsYou, item.StateWord);
            Assert.Equal(PlanDoers.Assistant, item.Doer);
            Assert.Contains("your assistant", item.StateNote ?? "", StringComparison.OrdinalIgnoreCase);
        }

        // ---- the words on the edit tools ----------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_model_edit_tool_says_the_edit_stays_local_until_a_save_back_or_a_publish()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var (host, client) = await ToolMergeTests.StartDoorAsync(f.Engine);
            try
            {
                var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.Description ?? "", StringComparer.Ordinal);
                foreach (var name in McpDoorFilters.LocalEditTools)
                {
                    Assert.True(tools.ContainsKey(name), name + " is named as an edit tool but is not a tool");
                    Assert.Contains(McpDoorFilters.LocalEditSentence, tools[name], StringComparison.Ordinal);
                }
                Assert.Contains("delete_objects", McpDoorFilters.LocalEditTools);
                Assert.Contains("define_calendar", McpDoorFilters.LocalEditTools);
                // A tool that writes to the workspace says its own thing, never that it stays local.
                Assert.DoesNotContain(McpDoorFilters.LocalEditSentence, tools["deploy_live"], StringComparison.Ordinal);
                Assert.DoesNotContain(McpDoorFilters.LocalEditSentence, tools["get_model_plan"], StringComparison.Ordinal);
            }
            finally { await client.DisposeAsync(); await host.StopAsync(); host.Dispose(); }
        }

        // ---- the journey ---------------------------------------------------------------------------------------

        /// <summary>Kane's plan, on a real model file through the engine and the MCP door's own methods: the report was read, the engine
        /// restarted (the reading is gone, so the check rows cannot be computed), the person made a change. Run the plan, then do what the
        /// semanticus skill says: list the queue, claim each task, do it with a tool, complete it. The assistant takes every check row and
        /// the save; the You group holds nothing a tool can do.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task After_a_restart_run_the_plan_and_the_assistant_takes_every_check_row_and_the_save()
        {
            string file;
            {
                using var first = await GappyAsync("DoerJourney.bim");
                await ChooseReportAsync(first);
                await first.Engine.CheckReportsAsync(Array.Empty<string>(), false, null, null, null, "human", default);
                file = Path.Combine(first.Workspace, "DoerJourney.bim");
                await first.Engine.SaveAsync(file, "bim", false, "human");
                var read = await first.Engine.GetModelPlanAsync();
                Assert.Contains(read.Items, i => i.Id == "check/measure:Sales/Total Sales");   // the reading landed, so the rows were seen

                // A new engine over the same workspace: the report choice comes back, its reading does not.
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro, first.Workspace);
                await engine.OpenAsync(file);
                await engine.SetDescriptionAsync("table:Customer", "One row per customer.", "human");

                var plan = await McpTools.GetModelPlan(engine);
                Assert.All(plan.Items.Where(i => i.Id.StartsWith("check/", StringComparison.Ordinal)),
                    i => Assert.Equal(PlanDoers.Assistant, i.Doer));

                // "Run the plan" (the skill: apply_plan_items with no items and the readyDigest).
                var ran = await McpTools.ApplyPlanItems(engine, null, plan.ReadyDigest);
                Assert.NotNull(ran);

                // The skill: list_assistant_requests, claim_request, do it, complete_request; until the queue is empty.
                var did = new System.Collections.Generic.List<string>();
                for (var guard = 0; guard < 20; guard++)
                {
                    var queue = await McpTools.ListAssistantRequests(engine);
                    var next = queue.Requests?.FirstOrDefault();
                    if (next == null) break;
                    var claim = await McpTools.ClaimRequest(engine, next.RequestId);
                    Assert.True(claim.Ok, claim.Message);
                    var words = next.Need ?? "";
                    if (words.StartsWith("Add a check for ", StringComparison.Ordinal))
                    {
                        var measure = words.Substring("Add a check for ".Length);
                        await McpToolsTesting.SaveTest(engine, new TestDefinition
                        {
                            Kind = TestKinds.MeasureValue, Title = measure + " is right",
                            TargetRef = "measure:Sales/" + measure,
                            ParamsJson = "{\"measureRef\":\"measure:Sales/" + measure + "\",\"expectedValue\":\"0\"}",
                        });
                    }
                    else if (words.StartsWith("Save", StringComparison.Ordinal)) await engine.SaveAsync(null, null, false, "agent");
                    else if (words.StartsWith("Look at", StringComparison.Ordinal))
                        await engine.CheckReportsAsync(Array.Empty<string>(), false, null, null, null, "agent", default);
                    var done = await McpTools.CompleteRequest(engine, next.RequestId, claim.Request.ClaimToken, "Done.");
                    Assert.True(done.Ok, done.Message);
                    did.Add(words);
                }

                Assert.Contains("Add a check for Total Sales", did);
                Assert.Contains("Add a check for Order Lines", did);
                Assert.Contains(did, w => w.StartsWith("Save", StringComparison.Ordinal));

                var after = await engine.GetModelPlanAsync();
                var yours = after.Items.Where(i => i.Doer == PlanDoers.You && PlanStateWords.IsOpen(i.StateWord)).Select(i => i.Id).ToArray();
                Assert.DoesNotContain(yours, id => id.StartsWith("check/", StringComparison.Ordinal) || id == "ship/unsaved" || id == "recheck/reports");
                Assert.False(sessions.Current.HasUnsavedEdits);
            }
        }
    }
}
