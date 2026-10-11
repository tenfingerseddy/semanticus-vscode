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
    /// m11/rule-clashes, final batch: the verifier's round-2 defects (VerifyRuleClashesRound2Tests) proved again through
    /// both doors, the Studio's RPC target and the MCP tool, and widened where the cause was wider than the case. R1: the
    /// spec generator keeps every part of a declared key. R2: the change plan seeds no waived finding, of any system, so
    /// applying it never overrides a decision the person recorded. L-b: the technical-column text does not name BPA's
    /// advice for a column BPA lists as accepted, Tabular Editor's own ignore annotation included.
    /// </summary>
    public sealed class RuleClashesFinalFixTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public RuleClashesFinalFixTests(ModelTemplates templates) => _templates = templates;

        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private static async Task<string[]> FlaggedAsync(DecisionFixture f, string ruleId) =>
            (await f.Engine.BpaScanAsync()).Violations.Where(v => v.RuleId == ruleId && !v.Waived)
                .Select(v => v.ObjectRef).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        private static async Task BuildAsync(DecisionFixture f, ModelSpec spec)
        {
            await f.Engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            Assert.Empty((await f.Engine.BuildModelFromSpecAsync("human")).Errors);
        }

        private static ModelSpec ThreePartKey() => new()
        {
            Name = "Rates", CompatibilityLevel = 1604, StorageMode = "import",
            Tables = new[]
            {
                new SpecTable
                {
                    Name = "Rates", Role = "isolated", Columns = new[]
                    {
                        new SpecColumn { Name = "Region", DataType = "String", IsKey = true, Hidden = true },
                        new SpecColumn { Name = "Grade", DataType = "String", IsKey = true, Hidden = true },
                        new SpecColumn { Name = "Year", DataType = "Int64", IsKey = true, Hidden = true },
                        new SpecColumn { Name = "Rate", DataType = "Decimal" },
                    },
                },
            },
        };

        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task A_spec_generated_from_the_model_keeps_every_part_of_a_three_part_key_on_both_doors(string door)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Rates", 1604);
            await BuildAsync(f, ThreePartKey());
            Assert.Empty(await FlaggedAsync(f, "UNNECESSARY_COLUMNS"));

            var generated = (door == "studio"
                ? await new EngineRpcTarget(f.Engine).autogenerateSpecFromModel()
                : await McpTools.AutogenerateSpecFromModel(f.Engine)).Spec;
            var cols = generated.Tables.Single(t => t.Name == "Rates").Columns;
            Assert.Equal(new[] { "Grade", "Region", "Year" }, cols.Where(c => c.IsKey).Select(c => c.Name).OrderBy(x => x, StringComparer.Ordinal));
            Assert.False(cols.Single(c => c.Name == "Rate").IsKey);   // the mark is read, not every column promoted

            using var g = new DecisionFixture(TestEntitlements.Pro);
            await g.Engine.CreateModelAsync("Rates copy", 1604);
            await BuildAsync(g, generated);
            Assert.Empty(await FlaggedAsync(g, "UNNECESSARY_COLUMNS"));
        }

        private async Task<DecisionFixture> StaffStarAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "staff-star", BuildStaffStarAsync, "StaffStar.bim");
            return f;
        }

        private static async Task BuildStaffStarAsync(DecisionFixture f)
        {
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
        }

        private static Task<ChangePlanView> ProposeAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).proposePlan()
            : McpTools.ProposePlan(f.Engine);

        private static Task<ApplyPlanReport> ApplyAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).applyPlan()
            : McpTools.ApplyPlan(f.Engine);

        /// <summary>R2's cause is every seeding step, not only the schema one: a readiness safe fix, a BPA auto-fix and
        /// the schema seed are each pre-approved, so apply_plan carried out whatever the person had waived. The plan now
        /// leaves every waived finding out, and applying it changes none of the waived objects.</summary>
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task The_change_plan_seeds_no_waived_finding_of_any_system_on_both_doors(string door)
        {
            using var f = await StaffStarAsync();
            var e = f.Engine;

            // Unwaived, the plan seeds all three kinds, so the test below is not vacuous.
            var open = await ProposeAsync(f, door);
            Assert.Contains(open.Items, i => i.Kind == "create_linguistic_schema");
            var bpa = open.Items.First(i => i.Source == "bpa");
            var air = open.Items.First(i => i.Source == "deterministic" && i.Kind != "create_linguistic_schema");

            await e.WaiveFindingAsync("air", "SYN-SCHEMA", null, "No Copilot questions on this model.", "human");
            await e.WaiveFindingAsync("bpa", bpa.RuleId, bpa.ObjectRef, "Kept as it is on purpose.", "human");
            await e.WaiveFindingAsync("air", air.RuleId, air.ObjectRef, "Kept as it is on purpose.", "human");
            var before = await f.Sessions.Require().ReadAsync(m => Snapshot(m, bpa.ObjectRef, bpa.Target) + "|" + Snapshot(m, air.ObjectRef, air.Target));

            var plan = await ProposeAsync(f, door);
            Assert.DoesNotContain(plan.Items, i => i.Kind == "create_linguistic_schema");
            Assert.DoesNotContain(plan.Items, i => i.RuleId == bpa.RuleId && i.ObjectRef == bpa.ObjectRef);
            Assert.DoesNotContain(plan.Items, i => i.RuleId == air.RuleId && i.ObjectRef == air.ObjectRef);

            await ApplyAsync(f, door);
            var after = await f.Sessions.Require().ReadAsync(m => Snapshot(m, bpa.ObjectRef, bpa.Target) + "|" + Snapshot(m, air.ObjectRef, air.Target));
            Assert.Equal(before, after);
            Assert.False(await f.Sessions.Require().ReadAsync(m => m.Cultures.Any(c => !string.IsNullOrWhiteSpace(c.Content))));
        }

        private static string Snapshot(Model m, string objRef, string target)
        {
            var o = ObjectRefs.Resolve(m, objRef);
            var p = o?.GetType().GetProperties().FirstOrDefault(x => string.Equals(x.Name, target, StringComparison.OrdinalIgnoreCase));
            return objRef + "." + target + "=" + p?.GetValue(o);
        }

        /// <summary>L-b through Tabular Editor's own ignore annotation: BPA lists the column as accepted, so the text must
        /// not call removing it BPA's advice. A column beside it with no ignore keeps the BPA sentence.</summary>
        [Fact]
        public async Task The_technical_column_text_does_not_name_bpa_advice_under_a_tabular_editor_ignore()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await _templates.OpenCopyAsync(f, "tech-column", async b =>
            {
                var be = b.Engine;
                await be.CreateModelAsync("TechColumn", 1604);
                await be.CreateTableAsync("Customer", "human");
                await be.CreateColumnAsync("table:Customer", "Customer_SK", "Int64", "Customer_SK", "human");
                await be.CreateColumnAsync("table:Customer", "Load_Batch_ID", "Int64", "Load_Batch_ID", "human");
                await be.CreateColumnAsync("table:Customer", "Customer Name", "String", "Customer Name", "human");
                await b.Sessions.Require().MutateAsync("human", "test setup", m =>
                    m.Tables["Customer"].Columns["Customer_SK"].SetAnnotation("BestPracticeAnalyzer_IgnoreRules", "{\"RuleIDs\":[\"UNNECESSARY_COLUMNS\"]}"));
            }, "TechColumn.bim");

            var card = await e.AiReadinessScanAsync();
            string Text(string col) => card.Findings.Single(x => x.RuleId == "VIS-TECH-COLUMN" && x.ObjectRef == "column:Customer/" + col).Message;
            Assert.DoesNotContain("Model quality (BPA) suggests removing it", Text("Customer_SK"), StringComparison.Ordinal);
            Assert.Contains("once it is hidden you can remove it", Text("Customer_SK"), StringComparison.Ordinal);
            Assert.Contains("Model quality (BPA) suggests removing it", Text("Load_Batch_ID"), StringComparison.Ordinal);

            await f.Sessions.Require().MutateAsync("human", "test setup", m =>
            {
                m.Tables["Customer"].Columns["Customer_SK"].IsHidden = true;
                m.Tables["Customer"].Columns["Load_Batch_ID"].IsHidden = true;
            });
            Assert.Equal(new[] { "column:Customer/Load_Batch_ID" }, await FlaggedAsync(f, "UNNECESSARY_COLUMNS"));
        }

        /// <summary>L-a's other half: with SYN-SCHEMA not waived the gaps really are listed, and the text still says so.</summary>
        [Fact]
        public async Task An_unwaived_schema_finding_still_says_the_synonym_gaps_are_listed()
        {
            using var f = await StaffStarAsync();
            var card = await f.Engine.AiReadinessScanAsync();
            Assert.Contains("already listed as its own finding", card.Findings.Single(x => x.RuleId == "SYN-SCHEMA").Message, StringComparison.Ordinal);
            Assert.Contains(card.Findings, x => x.RuleId == "SYN-FIELD" || x.RuleId == "SYN-TABLE");
        }
    }
}
