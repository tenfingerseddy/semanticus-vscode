using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The verifier's failing cases for m11/rule-clashes (656ed4cf), each found by replaying the Plan statement check's own
    /// starts over the assistant door, old engine (eb996ca6) against new. They are left failing on purpose: each one is a
    /// place where a clash the lane reports as fixed is still there, or where the lane's own new text says something the
    /// rules do not do.
    /// </summary>
    public sealed class VerifyRuleClashesTests
    {
        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "test setup", edit);

        private static async Task<string[]> FlaggedAsync(DecisionFixture f, string ruleId) =>
            (await f.Engine.BpaScanAsync()).Violations.Where(v => v.RuleId == ruleId && !v.Waived)
                .Select(v => v.ObjectRef).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        /// <summary>Clash (b) on the check's SQL-style start. "Draft from a SQL source" marks every primary-key column as a
        /// key and hides it (key columns hidden by default, the readiness best practice), and the spec build writes IsKey
        /// on each one. TOM keeps IsKey on one column per table, so a two-part key leaves its first part hidden,
        /// unreferenced and not IsKey, and "Remove unnecessary columns" asks to delete half of the key the source declared.
        /// On the check's model that is ref_codes[CODE_TYPE], one of the four "hidden keys" findings it counted; the lane
        /// clears the other three. Following the advice makes the lookup ambiguous, because a code repeats across types.</summary>
        [Fact]
        public async Task Verify_no_part_of_a_two_part_source_key_is_raised_as_an_unnecessary_column()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("TwoPartKey", 1604);
            var schema = new FabricSchema
            {
                Server = "contoso-sql.example.com",
                Database = "Contoso",
                Tables = new[]
                {
                    new FabricTable
                    {
                        Schema = "dbo", Name = "ref_codes", KeyColumns = new[] { "CODE_TYPE", "CODE" },
                        Columns = new[]
                        {
                            new FabricColumn { Name = "CODE_TYPE", DataType = "String", SqlType = "varchar", Ordinal = 1 },
                            new FabricColumn { Name = "CODE", DataType = "String", SqlType = "varchar", Ordinal = 2 },
                            new FabricColumn { Name = "CODE_DESC", DataType = "String", SqlType = "varchar", Ordinal = 3 },
                        },
                    },
                },
            };
            var spec = LocalEngine.FabricSchemaToSpec(schema, schema.Server, schema.Database, "import");
            await f.Engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            var report = await f.Engine.BuildModelFromSpecAsync("human");
            Assert.Contains("column:ref_codes/CODE_TYPE", report.Created);
            // The setup is the product's own: both key columns hidden by the build, and TOM holding IsKey on only one.
            Assert.True(await f.Sessions.Require().ReadAsync(m =>
                m.Tables["ref_codes"].Columns["CODE_TYPE"].IsHidden && m.Tables["ref_codes"].Columns["CODE"].IsHidden));

            var flagged = await FlaggedAsync(f, "UNNECESSARY_COLUMNS");
            Assert.DoesNotContain("column:ref_codes/CODE", flagged);
            Assert.DoesNotContain("column:ref_codes/CODE_TYPE", flagged);
        }

        /// <summary>VIS-TECH-COLUMN's new text says "if no report uses it, remove it (once it is hidden and unused, the
        /// Best Practice Analyzer asks for that too)". Its population keeps a relationship's one-side key on purpose (a
        /// visible Customer_SK "is exactly the target here"), and it owns a tech-named IsKey column that VIS-KEY defers to
        /// it. BPA never raises either one once hidden: a relationship uses the first, and the lane itself now spares
        /// every IsKey column. For both, the sentence is false, and following it deletes the key a relationship or a
        /// table's identity runs on.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("relationship")]
        [InlineData("iskey")]
        public async Task Verify_the_technical_column_finding_never_advises_removing_a_key(string shape)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("SurrogateKey", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Customer_SK", "Int64", "Customer_SK", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "Customer_SK", "Int64", "Customer_SK", "human");
            await e.CreateColumnAsync("table:Customer", "Customer Name", "String", "Customer Name", "human");
            if (shape == "relationship")
                await e.CreateRelationshipAsync("column:Sales/Customer_SK", "column:Customer/Customer_SK", null, true, "human");
            else
                await Mutate(f, m => m.Tables["Customer"].Columns["Customer_SK"].IsKey = true);

            var finding = (await e.AiReadinessScanAsync()).Findings
                .Single(x => x.RuleId == "VIS-TECH-COLUMN" && x.ObjectRef == "column:Customer/Customer_SK");

            // Do what the finding asks first, then look at what BPA says: it does not ask for the column's removal.
            await Mutate(f, m => m.Tables["Customer"].Columns["Customer_SK"].IsHidden = true);
            Assert.DoesNotContain("column:Customer/Customer_SK", await FlaggedAsync(f, "UNNECESSARY_COLUMNS"));

            Assert.DoesNotContain("remove", finding.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("delete", finding.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
