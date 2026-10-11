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
    /// The verifier's round-2 failing cases for m11/rule-clashes (52b28ef2). Each is a place where an outcome the round
    /// promises (D1: "Remove unnecessary columns" never fires on any column of a multi-column key; D3: while SYN-SCHEMA is
    /// waived, the grade does not drop and no fix creates the schema the person declined; D2 and the staged schema text:
    /// say only what is true) still does not hold on a product path. They are left failing on purpose.
    /// </summary>
    public sealed class VerifyRuleClashesRound2Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyRuleClashesRound2Tests(ModelTemplates templates) => _templates = templates;

        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private const string Why = "No Copilot questions on this model, so no linguistic schema on purpose.";

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "test setup", edit);

        private static async Task<string[]> FlaggedAsync(DecisionFixture f, string ruleId) =>
            (await f.Engine.BpaScanAsync()).Violations.Where(v => v.RuleId == ruleId && !v.Waived)
                .Select(v => v.ObjectRef).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        private static async Task BuildAsync(DecisionFixture f, ModelSpec spec)
        {
            await f.Engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            Assert.Empty((await f.Engine.BuildModelFromSpecAsync("human")).Errors);
        }

        /// <summary>D1 through the product's own spec generator. The spec build now marks every part of a declared key
        /// (Semanticus_SourceKey), but autogenerate_spec_from_model reads only TOM's IsKey, which TOM keeps on one column
        /// per table, so the spec it writes loses the other part of a two-part key. A model built from that spec gets the
        /// part hidden and not a key, and "Remove unnecessary columns" asks to delete half of the key again (a publish
        /// blocker whose advice leaves the code lookup ambiguous). Measured over the assistant door too.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_a_spec_generated_from_the_model_keeps_every_part_of_a_declared_key()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Codes", 1604);
            await BuildAsync(f, new ModelSpec
            {
                Name = "Codes", CompatibilityLevel = 1604, StorageMode = "import",
                Tables = new[]
                {
                    new SpecTable
                    {
                        Name = "Codes", Role = "isolated", Columns = new[]
                        {
                            new SpecColumn { Name = "Code Type", DataType = "String", IsKey = true, Hidden = true },
                            new SpecColumn { Name = "Code", DataType = "String", IsKey = true, Hidden = true },
                            new SpecColumn { Name = "Description", DataType = "String" },
                        },
                    },
                },
            });
            Assert.Empty(await FlaggedAsync(f, "UNNECESSARY_COLUMNS"));   // the round's fix, on the model built from the spec

            var generated = (await f.Engine.AutogenerateSpecFromModelAsync("agent")).Spec;
            using var g = new DecisionFixture(TestEntitlements.Pro);
            await g.Engine.CreateModelAsync("Codes copy", 1604);
            await BuildAsync(g, generated);
            Assert.Empty(await FlaggedAsync(g, "UNNECESSARY_COLUMNS"));
            var keys = generated.Tables.Single(t => t.Name == "Codes").Columns.Where(c => c.IsKey).Select(c => c.Name).OrderBy(x => x, StringComparer.Ordinal);
            Assert.Equal(new[] { "Code", "Code Type" }, keys);
        }

        private async Task<DecisionFixture> StaffStarAsync()
        {
            // The build runs once per class; every test opens a copy of the saved model (ModelTemplates, lean suite).
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "staff", BuildStaffStarAsync, "staff.bim");
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
            await Mutate(f, m => m.Tables["Timesheets"].Columns["Staff Number"].IsHidden = true);
        }

        private static Task<bool> HasJsonSchemaAsync(DecisionFixture f) =>
            f.Sessions.Require().ReadAsync(m => m.Cultures.Any(c => c.ContentType == ContentType.Json && !string.IsNullOrEmpty(c.Content)));

        /// <summary>D3, the coordinator's ruling: while SYN-SCHEMA is waived with a reason, the grade does not drop and no fix
        /// creates the schema the person declined. The change plan still seeds its "Create the synonym store Copilot reads"
        /// item, already approved, from the waived finding (BuildSeeds step 1b never reads Waived), the item also lands on
        /// the Model Plan as "Create linguistic schema, 1 change", and apply_plan creates the schema. Once the schema
        /// exists the waiver no longer holds SYN-FIELD and SYN-TABLE back, so the grade drops (measured D 63.6 to F 60.0,
        /// Synonyms 100 to 42.9, 4 synonym findings opened; the same on eb996ca6).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_the_change_plan_never_creates_a_schema_the_person_declined()
        {
            using var f = await StaffStarAsync();
            var e = f.Engine;
            await e.WaiveFindingAsync("air", "SYN-SCHEMA", null, Why, "human");
            var before = await e.AiReadinessScanAsync();

            var plan = await e.ProposePlanAsync(null, true, 40, "agent");
            await e.ApplyPlanAsync(null, "agent");
            var after = await e.AiReadinessScanAsync();

            Assert.DoesNotContain(plan.Items, i => i.Kind == "create_linguistic_schema");
            Assert.False(await HasJsonSchemaAsync(f));
            Assert.Equal(before.Categories.Single(c => c.Category == "Synonyms").Score, after.Categories.Single(c => c.Category == "Synonyms").Score);
        }

        /// <summary>The staged SYN-SCHEMA text says "each field and table without synonyms is already listed as its own
        /// finding". Under the D3 ruling, a waived SYN-SCHEMA puts SYN-FIELD and SYN-TABLE to sleep, so nothing is listed,
        /// and the waived finding (which the scorecard always surfaces with its message) says something false.</summary>
        [Fact]
        public async Task Verify_a_waived_schema_finding_does_not_say_the_synonym_gaps_are_listed()
        {
            using var f = await StaffStarAsync();
            await f.Engine.WaiveFindingAsync("air", "SYN-SCHEMA", null, Why, "human");
            var card = await f.Engine.AiReadinessScanAsync();
            var schema = card.Findings.Single(x => x.RuleId == "SYN-SCHEMA");
            Assert.True(schema.Waived);
            Assert.DoesNotContain(card.Findings, x => x.RuleId == "SYN-FIELD" || x.RuleId == "SYN-TABLE");
            Assert.DoesNotContain("already listed as its own finding", schema.Message, StringComparison.Ordinal);
        }

        /// <summary>D2: never claim BPA asks for something it does not raise. A person who waived "Remove unnecessary
        /// columns" rule-wide (the waive tool's own example, "we keep these unused columns") has BPA list the column as
        /// accepted, not as advice, yet VIS-TECH-COLUMN still says Model quality (BPA) suggests removing it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_the_technical_column_text_does_not_claim_a_waived_bpa_finding_as_advice()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("TechColumn", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "Customer_SK", "Int64", "Customer_SK", "human");
            await e.CreateColumnAsync("table:Customer", "Customer Name", "String", "Customer Name", "human");
            await e.WaiveFindingAsync("bpa", "UNNECESSARY_COLUMNS", null, "We keep these unused columns for the extract jobs.", "human");

            var finding = (await e.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "VIS-TECH-COLUMN" && x.ObjectRef == "column:Customer/Customer_SK");
            await Mutate(f, m => m.Tables["Customer"].Columns["Customer_SK"].IsHidden = true);
            Assert.DoesNotContain("column:Customer/Customer_SK", await FlaggedAsync(f, "UNNECESSARY_COLUMNS"));   // accepted, not advice
            Assert.DoesNotContain("Model quality (BPA) suggests removing it", finding.Message, StringComparison.Ordinal);
        }
    }
}
