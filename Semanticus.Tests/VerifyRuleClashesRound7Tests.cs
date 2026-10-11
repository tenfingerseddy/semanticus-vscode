using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier, m11/rule-clashes round 7 (at e510205c). The promise: an accepted finding is never carried out, by any rule or any
    /// path, and an acceptance never hides a finding the person did not accept, never past the deploy gate. The ruling for this
    /// round: "A deploy-gate blocker is never shown as accepted unless the person accepted THAT finding", and every seeded row
    /// asks. Each case below breaks one of these on both doors and was left failing on purpose.
    /// </summary>
    public sealed class VerifyRuleClashesRound7Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyRuleClashesRound7Tests(ModelTemplates templates) => _templates = templates;

        private const string Why = "Declined on purpose; reports depend on it as it is.";

        private static Task<SetResult> WaiveAsync(DecisionFixture f, string door, string system, string ruleId, string? objRef) => door == "studio"
            ? new EngineRpcTarget(f.Engine).waiveFinding(system, ruleId, objRef!, Why)
            : McpTools.WaiveFinding(f.Engine, system, ruleId, objRef!, Why);

        private static Task<BpaScorecard> BpaAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).bpaScan()
            : McpTools.BpaScan(f.Engine);

        private static Task<DeployGate> GateAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).deployGate(null!, "human")
            : McpTools.DeployGate(f.Engine);

        private static Task<RenameResult> RenameAsync(DecisionFixture f, string door, string objRef, string newName) => door == "studio"
            ? new EngineRpcTarget(f.Engine).renameObject(objRef, newName)
            : McpTools.RenameObject(f.Engine, objRef, newName);

        private static Task<T> Read<T>(DecisionFixture f, Func<Model, T> read) => f.Sessions.Require().ReadAsync(read);

        /// <summary>A star: Sales to Customer, Product and Date; a Budget fact on Product; a flag column; a measure with no format
        /// string; a measure whose name starts lower case and ends with a space.</summary>
        private async Task<DecisionFixture> StarAsync(string? save = null)
        {
            // The build is ~30 engine calls; it runs once per class and every test opens a copy (ModelTemplates).
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "star", BuildStarAsync, save ?? "R7Verify.bim");
            if (save != null) f.UseNull();
            return f;
        }

        private static async Task BuildStarAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Round7Verify", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Order Date", "DateTime", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Customer", "IsActive", "Int64", null, "human");
            await e.CreateTableAsync("Product", "human");
            await e.CreateColumnAsync("table:Product", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Product", "ProductName", "String", null, "human");
            await e.CreateTableAsync("Budget", "human");
            await e.CreateColumnAsync("table:Budget", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Budget", "Budget Amount", "Double", null, "human");
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", "Date", "DateTime", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/Order Date", "column:Date/Date", null, true, "human");
            await e.CreateRelationshipAsync("column:Budget/ProductKey", "column:Product/ProductKey", null, true, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] ) + SUM ( Sales[Quantity] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Sales YTD", "TOTALYTD ( [Total Sales], 'Date'[Date] )", "human");
            await e.CreateMeasureAsync("table:Sales", "total margin", "SUM ( Sales[Amount] ) - SUM ( Sales[Quantity] )", "human");
            await f.Sessions.Require().MutateAsync("human", "setup: a name with a trailing space, formats on the measures that need none here", m =>
            {
                m.Tables["Sales"].Measures["total margin"].Name = "total margin ";
                m.Tables["Sales"].Measures["Total Sales"].FormatString = "#,0.00";
                m.Tables["Sales"].Measures["Sales YTD"].FormatString = "#,0.00";
            });
        }

        /// <summary>Run the plan (Pro): press until nothing Semanticus can do is ready. Returns the rows that ran.</summary>
        private static async Task<string[]> RunThePlanAsync(DecisionFixture f, string door)
        {
            var ran = new List<string>();
            for (var press = 0; press < 6; press++)
            {
                var plan = await f.Engine.GetModelPlanAsync();
                foreach (var r in plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.ChangeSet?.Stale == true))
                    await f.Engine.ModelPlanActionAsync(r.Id, "propose", "human");
                plan = await f.Engine.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held)) break;
                var result = door == "studio"
                    ? await new EngineRpcTarget(f.Engine).applyPlanItems(null!, "human", plan.ReadyDigest)
                    : DoorResult.AsItems(await McpTools.ApplyPlanItems(f.Engine, null!, plan.ReadyDigest));
                if (!result.Performed) break;
                ran.AddRange(result.ItemIds ?? Array.Empty<string>());
            }
            return ran.ToArray();
        }

        // ---- 1. an acceptance lifts a deploy-gate blocker that asks something else ------------------------------------------

        /// <summary>The person accepts one Model quality finding on an object; another finding on that object is a deploy-gate
        /// blocker (severity 2 or 3, no fix) that asks for a different change: "#,0" against any format string at all, a Yes/No
        /// format against any format string, a capital first letter against no space at the end. The blocker must stay open and
        /// keep blocking. It reads as "accepted under another rule" instead and the gate stops counting it, because the rule table
        /// treats every write to one property as the same change.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", "INTEGER_FORMATTING", "measure:Sales/Order Count", "PROVIDE_FORMAT_STRING_FOR_MEASURES")]
        [InlineData("mcp", "INTEGER_FORMATTING", "measure:Sales/Order Count", "PROVIDE_FORMAT_STRING_FOR_MEASURES")]
        [InlineData("studio", "FORMAT_FLAG_COLUMNS_AS_YES/NO_VALUE_STRINGS", "column:Customer/IsActive", "SEM_COLUMN_NEEDS_FORMAT_STRING")]
        [InlineData("mcp", "FORMAT_FLAG_COLUMNS_AS_YES/NO_VALUE_STRINGS", "column:Customer/IsActive", "SEM_COLUMN_NEEDS_FORMAT_STRING")]
        [InlineData("studio", "FIRST_LETTER_OF_OBJECTS_MUST_BE_CAPITALIZED", "measure:Sales/total margin ", "OBJECTS_SHOULD_NOT_START_OR_END_WITH_A_SPACE")]
        [InlineData("mcp", "FIRST_LETTER_OF_OBJECTS_MUST_BE_CAPITALIZED", "measure:Sales/total margin ", "OBJECTS_SHOULD_NOT_START_OR_END_WITH_A_SPACE")]
        public async Task Verify_accepting_one_finding_does_not_lift_a_deploy_gate_blocker_that_asks_a_different_change(string door, string accepted, string objRef, string blocker)
        {
            using var f = await StarAsync();
            var before = await BpaAsync(f, door);
            var open = before.Violations.Single(v => v.RuleId == blocker && v.ObjectRef == objRef);
            Assert.True(open.Severity >= 2 && !open.CanAutoFix && !open.Waived, blocker + " is not an open deploy-gate blocker here");   // not vacuous
            Assert.Contains(before.Violations, v => v.RuleId == accepted && v.ObjectRef == objRef && !v.Waived && (v.Severity < 2 || v.CanAutoFix));
            var gate = await GateAsync(f, door);

            await WaiveAsync(f, door, "bpa", accepted, objRef);

            var after = await BpaAsync(f, door);
            Assert.Contains(after.Violations, v => v.RuleId == accepted && v.ObjectRef == objRef && v.Waived);   // the acceptance took
            var now = after.Violations.Single(v => v.RuleId == blocker && v.ObjectRef == objRef);
            Assert.False(now.Waived, blocker + " on " + objRef + " reads as accepted (\"" + now.WaiverReason + "\") although the person accepted only "
                + accepted + ", which asks for a different change");
            var gateNow = await GateAsync(f, door);
            Assert.Equal((gate.BpaBlocking, gate.BpaWaivedBlocking), (gateNow.BpaBlocking, gateNow.BpaWaivedBlocking));
        }

        /// <summary>The default-tier witness of the theory above (lean suite): one row, the hardest door. The full form runs in the sweep.</summary>
        [Fact]
        public Task Verify_accepting_one_finding_does_not_lift_a_deploy_gate_blocker_that_asks_a_different_change_sample() => Verify_accepting_one_finding_does_not_lift_a_deploy_gate_blocker_that_asks_a_different_change("mcp", "FIRST_LETTER_OF_OBJECTS_MUST_BE_CAPITALIZED", "measure:Sales/total margin ", "OBJECTS_SHOULD_NOT_START_OR_END_WITH_A_SPACE");

        // ---- 2. the starter row carries out an accepted "relies on implicit aggregation" finding ------------------------------

        /// <summary>DAC-IMPLICIT-MEASURE asks for a real measure ("set SummarizeBy=None and add one"). The starter row is the row that
        /// adds that measure: the plan's own proposal row drops the columns the starter family owns. The person accepts the finding
        /// for Budget[Budget Amount] ("read as a plain column; no measure wanted"), yet Run the plan adds "Total Budget Amount",
        /// because the starter lines carry STARTER-MEASURE, a rule id no acceptance can name, and ask only about the new measure.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", "object")]
        [InlineData("mcp", "rule")]
        public async Task Verify_run_the_plan_does_not_add_the_measure_an_accepted_implicit_aggregation_finding_asked_for(string door, string level)
        {
            using var f = await StarAsync(save: "V7Starter-" + door + "-" + level + ".bim");
            const string col = "column:Budget/Budget Amount";
            var finding = (await f.Engine.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "DAC-IMPLICIT-MEASURE" && x.ObjectRef == col);
            Assert.Contains("add one", finding.Message);   // not vacuous: its ask is a measure
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "starter/table:Budget");   // and the row that adds one is offered

            await WaiveAsync(f, door, "air", "DAC-IMPLICIT-MEASURE", level == "rule" ? null : col);
            Assert.Contains((await f.Engine.AiReadinessScanAsync()).Findings, x => x.RuleId == "DAC-IMPLICIT-MEASURE" && x.ObjectRef == col && x.Waived);

            var ran = await RunThePlanAsync(f, door);

            var added = await Read(f, m => m.AllMeasures.Where(x => (x.Expression ?? "").Contains("[Budget Amount]")).Select(x => x.Name + " = " + x.Expression).ToArray());
            Assert.True(added.Length == 0, "Run the plan (" + string.Join(", ", ran) + ") added " + string.Join("; ", added)
                + " for a column whose \"relies on implicit aggregation\" finding the person accepted");
        }

        // ---- 3. a rename orphans a readiness acceptance, and Run the plan then does what it declined -------------------------

        /// <summary>The person keeps a foreign key visible (VIS-FK accepted), then renames the column with rename_object. The
        /// acceptance is stored under the old ref: it no longer reads on the column, is listed nowhere on the page and is not
        /// counted as an orphan, and Run the plan hides the key. A Model quality acceptance survives the same rename through its
        /// mirrored Tabular Editor annotation. Older than this lane (round 5's K4), never ruled into scope.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_a_renamed_key_keeps_its_accepted_finding_and_run_the_plan_leaves_it_visible(string door)
        {
            using var f = await StarAsync(save: "V7Rename-" + door + ".bim");
            await WaiveAsync(f, door, "air", "VIS-FK", "column:Sales/CustomerKey");
            Assert.Contains((await f.Engine.AiReadinessScanAsync()).Findings, x => x.RuleId == "VIS-FK" && x.ObjectRef == "column:Sales/CustomerKey" && x.Waived);

            await RenameAsync(f, door, "column:Sales/CustomerKey", "Customer Key");

            var card = await f.Engine.AiReadinessScanAsync();
            var visFk = card.Findings.Single(x => x.RuleId == "VIS-FK" && x.ObjectRef == "column:Sales/Customer Key");
            Assert.True(visFk.Waived, "the renamed key's VIS-FK finding reads open; the acceptance still names column:Sales/CustomerKey and is shown nowhere (orphans: "
                + card.OrphanedWaiverCount + ")");
            await RunThePlanAsync(f, door);
            Assert.False(await Read(f, m => m.Tables["Sales"].Columns["Customer Key"].IsHidden), "Run the plan hid the key the person had accepted as visible");
        }

        // ---- 4. an accepted "Model should have a date table" finding is carried out by the date row ---------------------

        /// <summary>Model quality's MODEL_SHOULD_HAVE_A_DATE_TABLE (severity 2, on the model) fires exactly when no table is marked Time
        /// with a date key: the same condition as DATE-MARK, and the same change. The person accepts it ("no date table here: it has
        /// gaps"). Run the plan still marks 'Date' as the date table, because the rule's row says it declines nothing ("the change
        /// adds, removes or restructures objects"), so the date row's line is asked about DATE-MARK and the table-level twin only.
        /// Afterwards a per-object acceptance is shown nowhere: the finding stopped firing and a row that declines nothing gets no
        /// "no longer fires" line.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", "object")]
        [InlineData("mcp", "rule")]
        public async Task Verify_run_the_plan_does_not_mark_a_date_table_after_the_model_should_have_a_date_table_finding_was_accepted(string door, string level)
        {
            using var f = await StarAsync(save: "V7ModelDate-" + door + "-" + level + ".bim");
            var finding = (await BpaAsync(f, door)).Violations.Single(v => v.RuleId == "MODEL_SHOULD_HAVE_A_DATE_TABLE");
            Assert.True(finding.Severity >= 2 && !finding.Waived);   // not vacuous: an open deploy-gate blocker on the model
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "datetable/model");   // and the row that marks one is offered

            await WaiveAsync(f, door, "bpa", "MODEL_SHOULD_HAVE_A_DATE_TABLE", level == "rule" ? null : finding.ObjectRef);
            Assert.Contains((await BpaAsync(f, door)).Violations, v => v.RuleId == "MODEL_SHOULD_HAVE_A_DATE_TABLE" && v.Waived);

            var ran = await RunThePlanAsync(f, door);

            Assert.True(await Read(f, m => m.Tables["Date"].DataCategory) != "Time",
                "Run the plan (" + string.Join(", ", ran) + ") marked 'Date' as the date table after the person accepted \"Model should have a date table\"");
        }
    }
}
