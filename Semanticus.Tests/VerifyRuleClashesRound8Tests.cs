using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier, m11/rule-clashes round 8 (at c95c861f). The round 8 ruling: accepted findings and declined writes are two separate
    /// things, never mixed. "The gate and the scores read accepted findings by their OWN rule id only: a twin covered by another
    /// acceptance still counts as open for the gate." The case below breaks that sentence on the readiness half of the deploy gate,
    /// on both doors, and was left failing on purpose.
    /// </summary>
    public sealed class VerifyRuleClashesRound8Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyRuleClashesRound8Tests(ModelTemplates templates) => _templates = templates;

        private const string Why = "Declined on purpose; reports depend on it as it is.";
        private const string Measure = "measure:Sales/Total Sales";

        private static Task<SetResult> WaiveAsync(DecisionFixture f, string door, string system, string ruleId, string? objRef) => door == "studio"
            ? new EngineRpcTarget(f.Engine).waiveFinding(system, ruleId, objRef!, Why)
            : McpTools.WaiveFinding(f.Engine, system, ruleId, objRef!, Why);

        private static Task<DeployGate> GateAsync(DecisionFixture f, string door) => door == "studio"
            ? new EngineRpcTarget(f.Engine).deployGate(null!, "human")
            : McpTools.DeployGate(f.Engine);

        /// <summary>A star whose four visible measures have no description, so the AI readiness grade is capped ("more than half of
        /// visible measures undescribed"). Saved, so the deploy gate scores the change made after it is reopened, not the whole model.</summary>
        private async Task<(DecisionFixture F, string Path)> SavedStarAsync(string name)
        {
            // Built once per class; each test opens a copy under its own name (ModelTemplates, lean suite).
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "star", BuildStarAsync, name);
            f.UseNull();
            return (f, Path.Combine(f.Workspace, name));
        }

        private static async Task BuildStarAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Round8Verify", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Order Date", "DateTime", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", "Date", "DateTime", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/Order Date", "column:Date/Date", null, true, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Sales YTD", "TOTALYTD ( [Total Sales], 'Date'[Date] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Units", "SUM ( Sales[Quantity] )", "human");
            await f.Sessions.Require().MutateAsync("human", "setup: formats on the measures", m =>
            {
                foreach (var x in m.Tables["Sales"].Measures) x.FormatString = "#,0";
            });
        }

        /// <summary>Reopen the saved model and touch one measure, so the gate checks that change.</summary>
        private static async Task TouchAsync(DecisionFixture f, string path)
        {
            await f.Engine.OpenAsync(path, true);
            await f.Engine.SetDisplayFolderAsync(new[] { Measure }, "Folder", "human");
        }

        private static bool CapBlocks(DeployGate g) => !g.Pass && (g.Blockers ?? Array.Empty<string>()).Any(b => b.Contains("undescribed", StringComparison.OrdinalIgnoreCase));

        // ---- 1. a related acceptance lets a change past the readiness cap on the deploy gate ----------------------------------

        /// <summary>The person accepts "Visible objects with no description" (Model quality, severity 1) for one measure, or for the whole
        /// model. That is a related acceptance of the measure's AI readiness finding "Measure has no description" (DESC-MEASURE): its row
        /// reads open, "You accepted a related finding. Accept this one too, or fix it." The deploy gate must still hold the change to the
        /// readiness cap, because the person never accepted DESC-MEASURE. It lets it pass instead: the gate asks whether the change touches an
        /// open DESC-MEASURE finding and skips every finding flagged Waived, which a related (twin) finding still is
        /// (DeployGateScope.ReadinessGateTouches reads f.Waived, not the person's own acceptance).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", "per object")]
        [InlineData("mcp", "whole rule")]
        public async Task Verify_a_related_acceptance_does_not_let_a_change_past_the_readiness_cap_on_the_deploy_gate(string door, string reach)
        {
            var (f, path) = await SavedStarAsync("r8-cap-" + door + ".bim");
            using (f)
            {
                // Not vacuous: with nothing accepted, the gate scores this change (not the whole model) and the cap blocks it.
                await TouchAsync(f, path);
                var before = await GateAsync(f, door);
                Assert.False(before.WholeModel, "the gate checked the whole model, so the change set is not what it scored");
                Assert.True(CapBlocks(before), "with nothing accepted the readiness cap should block this change: " + string.Join(" | ", before.Blockers ?? Array.Empty<string>()));

                // The person accepts the Model quality advisory about descriptions, and nothing else, then saves.
                await f.Engine.OpenAsync(path, true);
                var took = await WaiveAsync(f, door, "bpa", "OBJECTS_WITH_NO_DESCRIPTION", reach == "per object" ? Measure : null);
                Assert.True(took.Changed, "the acceptance did not take");
                await f.Engine.SaveAsync(path, "bim", true, "human");

                await TouchAsync(f, path);
                var desc = (await f.Engine.AiReadinessScanAsync()).Findings.Single(x => x.RuleId == "DESC-MEASURE" && x.ObjectRef == Measure);
                Assert.False(desc.Waived && !desc.WaivedAsTwin, "the person did not accept DESC-MEASURE on " + Measure + ", yet it reads accepted");
                var after = await GateAsync(f, door);
                Assert.False(after.WholeModel, "the gate checked the whole model, so the change set is not what it scored");
                Assert.True(CapBlocks(after),
                    "the deploy gate let the change to " + Measure + " past the readiness cap (Pass " + after.Pass + ", blockers: "
                    + string.Join(" | ", after.Blockers ?? Array.Empty<string>()) + ") although the person accepted only \"Visible objects with no description\" ("
                    + reach + "); DESC-MEASURE on the measure is open and reads \"" + desc.WaiverReason + "\"");
            }
        }

        /// <summary>The default-tier witness of the theory above (lean suite): one row, the hardest door. The full form runs in the sweep.</summary>
        [Fact]
        public Task Verify_a_related_acceptance_does_not_let_a_change_past_the_readiness_cap_on_the_deploy_gate_sample() => Verify_a_related_acceptance_does_not_let_a_change_past_the_readiness_cap_on_the_deploy_gate("studio", "per object");
    }
}
