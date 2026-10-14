using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier, m11/rule-clashes round 6 (at 4e1bbfb2). The promise: an accepted finding is never carried out, by any rule or
    /// any path; an acceptance is never invisible while it still declines a change; and a finding the person did not accept is
    /// never shown or counted as accepted. Each case below breaks one of the three, on both doors, and was left failing on
    /// purpose.
    /// </summary>
    public sealed class VerifyRuleClashesRound6Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyRuleClashesRound6Tests(ModelTemplates templates) => _templates = templates;

        private static Task<SetResult> WaiveAsync(DecisionFixture f, string door, string system, string ruleId, string objRef, string why) => door == "studio"
            ? new EngineRpcTarget(f.Engine).waiveFinding(system, ruleId, objRef, why)
            : McpTools.WaiveFinding(f.Engine, system, ruleId, objRef, why);

        private static Task<SetResult> UnwaiveAsync(DecisionFixture f, string door, string system, string ruleId, string objRef) => door == "studio"
            ? new EngineRpcTarget(f.Engine).unwaiveFinding(system, ruleId, objRef)
            : McpTools.UnwaiveFinding(f.Engine, system, ruleId, objRef);

        private static Task<ChangePlanView> ProposeAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).proposePlan()
            : McpTools.ProposePlan(f.Engine);

        private static Task<T> Read<T>(DecisionFixture f, Func<Model, T> read) => f.Sessions.Require().ReadAsync(read);

        /// <summary>A small star: Sales to Customer and Product, a Date table the time intelligence reads (so "Mark the date
        /// table" is a Run the plan row), and measures.</summary>
        private async Task<DecisionFixture> StarAsync(string? save = null)
        {
            // The build runs once per class; every test opens a copy of the saved model (ModelTemplates, lean suite).
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "star", BuildStarAsync, (save ?? "star") + ((save ?? "star").EndsWith(".bim", StringComparison.Ordinal) ? "" : ".bim"));
            if (save != null) f.UseNull();
            return f;
        }

        private static async Task BuildStarAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Round6Verify", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Order Date", "DateTime", null, "human");
            await e.CreateColumnAsync("table:Sales", "Order Year", "Int64", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Product", "human");
            await e.CreateColumnAsync("table:Product", "ProductKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Product", "ProductName", "String", null, "human");
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", "Date", "DateTime", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/Order Date", "column:Date/Date", null, true, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Sales YTD", "TOTALYTD ( [Total Sales], 'Date'[Date] )", "human");
        }

        /// <summary>Run the plan (Pro): press until nothing Semanticus can do is ready. Returns the rows that ran.</summary>
        private static async Task<string[]> RunThePlanAsync(DecisionFixture f, string door)
        {
            var ran = new System.Collections.Generic.List<string>();
            for (var press = 0; press < 6; press++)
            {
                var plan = await f.Engine.GetModelPlanAsync();
                foreach (var r in plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.ChangeSet?.Stale == true))
                    await f.Engine.ModelPlanActionAsync(r.Id, "propose", "human");
                plan = await f.Engine.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held)) break;
                var result = door == "studio"
                    ? await new EngineRpcTarget(f.Engine).applyPlanItems(null, "human", plan.ReadyDigest)
                    : DoorResult.AsItems(await McpTools.ApplyPlanItems(f.Engine, null, plan.ReadyDigest));
                if (!result.Performed) break;
                ran.AddRange(result.ItemIds ?? Array.Empty<string>());
            }
            return ran.ToArray();
        }

        // ---- 1. Run the plan marks a Date table whose "mark it" finding the person accepted --------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_run_the_plan_does_not_mark_a_date_table_whose_mark_it_finding_was_accepted(string door)
        {
            using var f = await StarAsync("VerifyR6DateMark-" + door + ".bim");
            const string rule = "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE";
            Assert.Contains((await f.Engine.BpaScanAsync()).Violations, v => v.RuleId == rule && v.ObjectRef == "table:Date" && !v.Waived);   // not vacuous

            await WaiveAsync(f, door, "bpa", rule, "table:Date", "Not marked on purpose: this Date table has gaps and marking it breaks refresh.");
            var row = (await f.Engine.GetModelPlanAsync()).Items.SingleOrDefault(i => i.Id == "datetable/model");
            Assert.NotNull(row);   // the Model Plan still offers "Mark Date as the date table" for the engine to do

            var ran = await RunThePlanAsync(f, door);
            // Not vacuous: Run the plan ran other rows. Once the finding is accepted every change on the date row is unticked, so the row goes to the assistant and
            // Run the plan skips it (the plan's AllUntickedNote), where it used to run the row and leave the line out.
            Assert.NotEmpty(ran);
            Assert.DoesNotContain("datetable/model", ran);

            var (category, key) = await Read(f, m => (m.Tables["Date"].DataCategory, m.Tables["Date"].Columns["Date"].IsKey));
            Assert.NotEqual("Time", category);
            Assert.False(key);
        }

        // ---- 2. Accepting one DAX rewrite reads a different DAX finding on the measure as accepted, past the deploy gate ---

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_accepting_one_dax_finding_does_not_accept_a_different_dax_finding_on_the_same_measure(string door)
        {
            using var f = await StarAsync();
            await f.Engine.CreateMeasureAsync("table:Sales", "Margin Ratio", "IFERROR ( SUM ( Sales[Amount] ) / SUM ( Sales[Order Year] ), 0 )", "human");
            const string m = "measure:Sales/Margin Ratio";
            var scan = await f.Engine.BpaScanAsync();
            Assert.Contains(scan.Violations, v => v.RuleId == "USE_THE_DIVIDE_FUNCTION_FOR_DIVISION" && v.ObjectRef == m && !v.Waived);   // not vacuous: both fire
            Assert.Contains(scan.Violations, v => v.RuleId == "AVOID_USING_THE_IFERROR_FUNCTION" && v.ObjectRef == m && !v.Waived && v.Severity >= 2 && !v.CanAutoFix);
            Assert.Equal(0, (await f.Engine.DeployGateAsync(null, "human")).BpaWaivedBlocking);

            await WaiveAsync(f, door, "bpa", "USE_THE_DIVIDE_FUNCTION_FOR_DIVISION", m, "The denominator is never zero in this model; kept as written.");

            var iferror = (await f.Engine.BpaScanAsync()).Violations.Single(v => v.RuleId == "AVOID_USING_THE_IFERROR_FUNCTION" && v.ObjectRef == m);
            Assert.False(iferror.Waived, "IFERROR is a different problem the person did not accept, but it reads as \"" + iferror.WaiverReason + "\"");
            Assert.Equal(1, (await f.Engine.DeployGateAsync(null, "human")).BpaWaivedBlocking);   // only the finding the person accepted
        }

        // ---- 3. Accepting an intended many-to-many reads the direction findings on it as accepted, past the deploy gate -----

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_accepting_an_intended_many_to_many_does_not_accept_its_direction_findings(string door)
        {
            using var f = await StarAsync();
            await f.Engine.CreateTableAsync("Budget", "human");
            await f.Engine.CreateColumnAsync("table:Budget", "ProductKey", "Int64", null, "human");
            await f.Sessions.Require().MutateAsync("human", "setup: a many-to-many that filters both ways", mdl =>
            {
                var r = mdl.AddRelationship();
                r.FromColumn = mdl.Tables["Budget"].Columns["ProductKey"]; r.ToColumn = mdl.Tables["Product"].Columns["ProductKey"];
                r.FromCardinality = RelationshipEndCardinality.Many; r.ToCardinality = RelationshipEndCardinality.Many;
                r.CrossFilteringBehavior = CrossFilteringBehavior.BothDirections;
            });
            var rel = "relationship:" + await Read(f, mdl => mdl.Relationships.OfType<SingleColumnRelationship>().First(r => r.FromTable.Name == "Budget").Name);
            var air = await f.Engine.AiReadinessScanAsync();
            Assert.Contains(air.Findings, x => x.RuleId == "REL-M2M" && x.ObjectRef == rel);   // not vacuous
            Assert.Contains(air.Findings, x => x.RuleId == "REL-BIDI" && x.ObjectRef == rel && !x.Waived);
            Assert.Contains((await f.Engine.BpaScanAsync()).Violations, v => v.RuleId == "MANY-TO-MANY_RELATIONSHIPS_SHOULD_BE_SINGLE-DIRECTION" && v.ObjectRef == rel && !v.Waived);

            await WaiveAsync(f, door, "air", "REL-M2M", rel, "The budget bridge is many-to-many on purpose.");

            Assert.False((await f.Engine.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "REL-BIDI" && x.ObjectRef == rel).Waived);
            Assert.False((await f.Engine.BpaScanAsync()).Violations.Single(v => v.RuleId == "MANY-TO-MANY_RELATIONSHIPS_SHOULD_BE_SINGLE-DIRECTION" && v.ObjectRef == rel).Waived);
            Assert.Equal(0, (await f.Engine.DeployGateAsync(null, "human")).BpaWaivedBlocking);   // the person accepted no Model quality finding
        }

        // ---- 4. A reopened instructions ask comes back wearing the words of the ask still declined ----------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_a_reopened_instructions_ask_brings_back_its_own_words_not_the_declined_one(string door)
        {
            using var f = await StarAsync();
            await f.Engine.CreateMeasureAsync("table:Sales", "QRX Revenue", "1", "human");
            await f.Engine.CreateLinguisticSchemaAsync(null, "agent");
            await f.Engine.SetAiInstructionsAsync("MARKER-TEXT use fiscal years and report currency.", null, "agent");
            await f.Sessions.Require().MutateAsync("human", "setup: instructions over the limit", mdl =>
            {
                var cult = mdl.Cultures.First(c => c.Content != null && c.Content.Contains("MARKER-TEXT"));
                cult.Content = cult.Content.Replace("MARKER-TEXT", new string('a', 10600));
            });
            var card = await f.Engine.AiReadinessScanAsync();
            var len = card.Findings.Single(x => x.RuleId == "DAC-AI-INSTRUCTIONS-LEN");
            var gap = card.Findings.Single(x => x.RuleId == "DAC-GLOSSARY-GAP");
            ChangeItem Line(ChangePlanView p) => p.Items.Single(i => i.Target == "aiInstructions");
            var planned = Line(await ProposeAsync(f, door));
            Assert.Contains(len.Message, planned.Rationale);   // not vacuous: one line carries both asks
            Assert.Contains(gap.Message, planned.Rationale);

            await WaiveAsync(f, door, "air", "DAC-AI-INSTRUCTIONS-LEN", len.ObjectRef, "The long instructions are read by a custom agent.");
            await WaiveAsync(f, door, "air", "DAC-GLOSSARY-GAP", gap.ObjectRef, "QRX is known to everyone who reads this model.");
            Assert.Equal("skipped", Line(await f.Engine.GetPlanAsync()).Status);   // both declined: nothing left to ask

            await UnwaiveAsync(f, door, "air", "DAC-AI-INSTRUCTIONS-LEN", len.ObjectRef);   // the person wants the instructions condensed after all

            var line = Line(await f.Engine.GetPlanAsync());
            Assert.NotEqual("skipped", line.Status);
            Assert.Contains(len.Message, line.Rationale ?? "");
            Assert.DoesNotContain(gap.Message, line.Rationale ?? "");   // the glossary is still declined; the line must not ask for it
        }

        /// <summary>The default-tier witness of the theory above (lean suite): one row, the hardest door. The full form runs in the sweep.</summary>
        [Fact]
        public Task Verify_a_reopened_instructions_ask_brings_back_its_own_words_not_the_declined_one_sample() => Verify_a_reopened_instructions_ask_brings_back_its_own_words_not_the_declined_one("mcp");

        // ---- 5. A rule-level acceptance whose rule fires nowhere is shown nowhere, while it still declines ------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", "bpa", "HIDE_FOREIGN_KEYS")]
        [InlineData("mcp", "air", "VIS-FK")]
        public async Task Verify_a_rule_level_acceptance_is_still_shown_while_it_declines_after_its_rule_stops_firing(string door, string system, string rule)
        {
            using var f = await StarAsync();
            await WaiveAsync(f, door, system, rule, null!, "Rule-wide: our keys stay visible.");
            foreach (var key in new[] { "column:Sales/CustomerKey", "column:Sales/ProductKey", "column:Sales/Order Date", "column:Customer/CustomerKey", "column:Product/ProductKey" })
                await f.Engine.SetColumnMetadataAsync(key, true, null, null, null, "human");   // hidden by hand: the accepted rule fires nowhere now
            await f.Engine.CreateColumnAsync("table:Sales", "Row Hash", "String", null, "human");   // a technical column added later

            // Not vacuous: the acceptance still declines, because the new column's "hide it" finding reads as accepted under it.
            var air = await f.Engine.AiReadinessScanAsync();
            Assert.Contains(air.Findings, x => x.RuleId == "VIS-TECH-COLUMN" && x.ObjectRef == "column:Sales/Row Hash" && x.Waived && x.WaivedAsTwin);

            var bpa = await f.Engine.BpaScanAsync();
            var shown = system == "bpa"
                ? bpa.Violations.Any(v => v.RuleId == rule && v.Waived && v.WaiverRuleLevel)
                : air.Findings.Any(x => x.RuleId == rule && x.Waived && x.WaiverRuleLevel);
            Assert.True(shown, "the rule-level acceptance of " + rule + " keeps declining but has no row on its page, so it cannot be seen or reopened there");
        }
    }
}
