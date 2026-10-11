using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The verifier's failing cases for the m11/rule-clashes final batch (dbfbbe15). R2's promise is that while SYN-SCHEMA
    /// is waived the change plan never seeds OR APPLIES the declined schema and the grade stays; the batch stops the seed,
    /// but a plan proposed before the waiver still holds its pre-approved item, and applying it carries it out. The same
    /// path overrides the person's BPA waivers. The third case is R1's cause one line further on: the spec generator still
    /// reads TOM's single IsKey when it proposes starter totals, so it proposes a sum over a key part it now marks as a key.
    /// They are left failing on purpose.
    /// </summary>
    public sealed class VerifyRuleClashesFinalTests
    {
        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private const string Why = "No Copilot questions on this model, so no linguistic schema on purpose.";

        private static async Task<DecisionFixture> StaffStarAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("StaffStar", 1604);
            await e.CreateTableAsync("Timesheets", "human");
            await e.CreateColumnAsync("table:Timesheets", "Staff Number", "Int64", "Staff Number", "human");
            await e.CreateColumnAsync("table:Timesheets", "Hours", "Decimal", "Hours", "human");
            await e.CreateTableAsync("Staff", "human");
            await e.CreateColumnAsync("table:Staff", "Staff Number", "Int64", "Staff Number", "human");
            await e.CreateColumnAsync("table:Staff", "Staff Name", "String", "Staff Name", "human");
            await e.CreateRelationshipAsync("column:Timesheets/Staff Number", "column:Staff/Staff Number", null, true, "human");
            await f.Sessions.Require().MutateAsync("human", "test setup", m => m.Tables["Timesheets"].Columns["Staff Number"].IsHidden = true);
            return f;
        }

        private static Task<ChangePlanView> ProposeAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).proposePlan()
            : McpTools.ProposePlan(f.Engine);

        private static Task<SetResult> WaiveAsync(DecisionFixture f, string door, string system, string ruleId, string objRef, string reason) => door == "studio"
            ? new EngineRpcTarget(f.Engine).waiveFinding(system, ruleId, objRef, reason)
            : McpTools.WaiveFinding(f.Engine, system, ruleId, objRef, reason);

        private static Task<ApplyPlanReport> ApplyAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).applyPlan()
            : McpTools.ApplyPlan(f.Engine);

        private static Task<bool> HasJsonSchemaAsync(DecisionFixture f) =>
            f.Sessions.Require().ReadAsync(m => m.Cultures.Any(c => c.ContentType == ContentType.Json && !string.IsNullOrEmpty(c.Content)));

        private static double SynonymsScore(Scorecard card) => card.Categories.Single(c => c.Category == "Synonyms").Score;

        /// <summary>R2, the order a person meets it in Studio: "Review these together" on the AI readiness page proposes the
        /// plan, with "Create the synonym store Copilot reads" pre-approved. The person then accepts SYN-SCHEMA with a reason
        /// ("no schema on purpose"). Pressing "Review these together" again shows the plan already there, because the page
        /// seeds only an empty plan (optimize.tsx), so "Apply approved" still carries the item, and nothing re-checks the
        /// waiver at apply time (ApplyPlanCoreAsync). The declined schema is created and the grade drops, measured D 63.6 to
        /// F 60.0, Synonyms 100 to 42.9, on both doors and on one session across them (the assistant proposes, the person
        /// waives in Studio, the assistant applies).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_a_plan_proposed_before_the_waiver_never_creates_the_declined_schema(string door)
        {
            using var f = await StaffStarAsync();
            var plan = await ProposeAsync(f, door);
            Assert.Contains(plan.Items, i => i.Kind == "create_linguistic_schema" && i.Status == "approved");   // not vacuous

            await WaiveAsync(f, door, "air", "SYN-SCHEMA", "*", Why);   // rule-wide, as "Accept for the whole model" sends it
            var before = await f.Engine.AiReadinessScanAsync();
            Assert.Contains((await f.Engine.GetPlanAsync()).Items, i => i.Kind == "create_linguistic_schema");   // what the page shows again

            await ApplyAsync(f, door);
            var after = await f.Engine.AiReadinessScanAsync();
            Assert.False(await HasJsonSchemaAsync(f));
            Assert.Equal(SynonymsScore(before), SynonymsScore(after));
        }

        /// <summary>The same path overrides BPA waivers with real model changes. The plan is proposed, then the person
        /// accepts "Mark primary keys" on Staff[Staff Number] ("it repeats across contracts here, so it is not the key")
        /// and "Do not summarize numeric columns" on Timesheets[Hours]; applying the plan still makes Staff Number the
        /// table's key, which the next refresh refuses on duplicate values, and stops Hours summing.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_a_plan_proposed_before_bpa_waivers_never_overrides_them(string door)
        {
            using var f = await StaffStarAsync();
            var plan = await ProposeAsync(f, door);
            Assert.Contains(plan.Items, i => i.RuleId == "MARK_PRIMARY_KEYS" && i.ObjectRef == "column:Staff/Staff Number");
            Assert.Contains(plan.Items, i => i.RuleId == "NUMERIC_COLUMN_SUMMARIZE_BY" && i.ObjectRef == "column:Timesheets/Hours");

            await WaiveAsync(f, door, "bpa", "MARK_PRIMARY_KEYS", "column:Staff/Staff Number", "Staff Number repeats across contracts here, so it is not the key.");
            await WaiveAsync(f, door, "bpa", "NUMERIC_COLUMN_SUMMARIZE_BY", "column:Timesheets/Hours", "Hours are summed on purpose in every report.");
            var before = await f.Sessions.Require().ReadAsync(m => (m.Tables["Staff"].Columns["Staff Number"].IsKey, m.Tables["Timesheets"].Columns["Hours"].SummarizeBy));

            await ApplyAsync(f, door);
            var after = await f.Sessions.Require().ReadAsync(m => (m.Tables["Staff"].Columns["Staff Number"].IsKey, m.Tables["Timesheets"].Columns["Hours"].SummarizeBy));
            Assert.Equal(before, after);
        }

        /// <summary>R1's cause, one line further on in the same generator (LOW). The generated spec now marks every part of a
        /// declared key, but its starter totals still ask TOM's IsKey, which TOM keeps on one column, so for a fact table
        /// keyed on (Sheet ID, Line No) it proposes "Total Sheet ID" = SUM ( 'Lines'[Sheet ID] ) beside isKey true on that
        /// very column, and the model built from the spec gets a total of an id. Round trips add the other part's total too.
        /// The Plan's starter row (StarterMeasuresFamily, doer Semanticus, ready) reads the same IsKey and Run the plan
        /// creates the same measure.</summary>
        [Fact]
        public async Task Verify_a_spec_generated_from_the_model_proposes_no_total_over_a_declared_key_part()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Lines", 1604);
            var spec = new ModelSpec
            {
                Name = "Lines", CompatibilityLevel = 1604, StorageMode = "import",
                Tables = new[]
                {
                    new SpecTable
                    {
                        Name = "Staff", Role = "dimension", Columns = new[]
                        {
                            new SpecColumn { Name = "Staff Number", DataType = "Int64", IsKey = true, Hidden = true },
                            new SpecColumn { Name = "Staff Name", DataType = "String" },
                        },
                    },
                    new SpecTable
                    {
                        Name = "Lines", Role = "fact", Columns = new[]
                        {
                            new SpecColumn { Name = "Sheet ID", DataType = "Int64", IsKey = true, Hidden = true },
                            new SpecColumn { Name = "Line No", DataType = "Int64", IsKey = true, Hidden = true },
                            new SpecColumn { Name = "Staff Number", DataType = "Int64", Hidden = true },
                            new SpecColumn { Name = "Hours", DataType = "Decimal" },
                        },
                    },
                },
                Relationships = new[] { new SpecRelationship { FromTable = "Lines", FromColumn = "Staff Number", ToTable = "Staff", ToColumn = "Staff Number" } },
            };
            await f.Engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            Assert.Empty((await f.Engine.BuildModelFromSpecAsync("human")).Errors);

            var generated = (await f.Engine.AutogenerateSpecFromModelAsync("agent")).Spec;
            var keys = generated.Tables.Single(t => t.Name == "Lines").Columns.Where(c => c.IsKey).Select(c => c.Name).ToArray();
            Assert.Equal(new[] { "Line No", "Sheet ID" }, keys.OrderBy(x => x, StringComparer.Ordinal));   // the batch's fix holds
            Assert.Contains(generated.Measures, m => m.Name == "Total Hours");                            // starters are proposed
            Assert.DoesNotContain(generated.Measures, m => keys.Any(k => (m.Dax ?? "").Contains("[" + k + "]", StringComparison.Ordinal)));
        }
    }
}
