using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// D-009: deploy validation refused a live push over
    /// "Data columns must have a source column Time Intelligence[Time Calc]".
    ///
    /// Time Calc is the visible column of a calculation group, not a column loaded from a
    /// query. Calculation-group columns are typed as data columns in TOM, so the shipped
    /// source-column rule treated a working Time Intelligence group as a processing error
    /// and blocked the push. The Contoso-derived survey model already had that group live.
    ///
    /// A real data column with no source still has to fail. Both doors share the scan and
    /// the gate, so the tests call the engine path and the agent path on the same session.
    /// </summary>
    [Collection("restore-root")]
    public sealed class TimeCalcValidationTests
    {
        private const string SourceColumnRuleId = "DATA_COLUMNS_MUST_HAVE_A_SOURCE_COLUMN";

        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        // Same clean shape DeployFeatureTests uses so the only new error can come from Time Calc.
        private static async Task<(LocalEngine engine, SessionManager sessions)> PassingModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Pro());
            await engine.CreateModelAsync("TimeCalc", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            var amt = await engine.CreateColumnAsync(t, "Sales Amount", "Decimal", "Sales Amount", "human");
            await engine.CreateColumnAsync(t, "Customer Name", "String", "Customer Name", "human");
            await engine.SetColumnMetadataAsync(amt, true, null, null, null, "human");
            var mref = await engine.CreateMeasureAsync(t, "Total Sales", "SUM ( Sales[Sales Amount] )", "human");
            await engine.SetMeasureFormatAsync(mref, "#,0", "human");
            await engine.SetDescriptionAsync(mref, "The sum of all sales amounts across the model.", "human");
            var dt = await engine.CreateTableAsync("Date", "human");
            var dkey = await engine.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await engine.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await engine.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await engine.MarkDateTableAsync(dt, "Date", "human");
            return (engine, sessions);
        }

        // Bravo-style Time Intelligence group: visible column named Time Calc, no source
        // column. That is the Contoso-derived shape the survey hit.
        private static async Task AddTimeCalcGroupAsync(LocalEngine engine, SessionManager sessions)
        {
            var g = await engine.CreateCalculationGroupAsync("Time Intelligence", "human");
            await engine.CreateCalculationItemAsync(g, "YTD", "SELECTEDMEASURE()", "human");
            await engine.RenameObjectAsync("column:Time Intelligence/Name", "Time Calc", "human");
            await sessions.Require().MutateAsync("human", "clear Time Calc source", m =>
            {
                var col = m.Tables["Time Intelligence"].Columns["Time Calc"] as DataColumn
                    ?? throw new InvalidOperationException("Time Calc is not a data column.");
                col.SourceColumn = "";
            });
        }

        private static bool IsTimeCalcSourceFinding(Semanticus.Analysis.BpaViolation v) =>
            v.RuleId == SourceColumnRuleId
            && v.ObjectName != null
            && v.ObjectName.IndexOf("Time Calc", StringComparison.Ordinal) >= 0;

        [Fact]
        public async Task Time_Calc_is_not_a_missing_source_column()
        {
            var (engine, sessions) = await PassingModelAsync();
            using (engine)
            {
                await AddTimeCalcGroupAsync(engine, sessions);

                var scan = await engine.BpaScanAsync();
                Assert.DoesNotContain(scan.Violations, IsTimeCalcSourceFinding);

                var gate = await engine.DeployGateAsync(null, "human");
                Assert.True(gate.Pass, "expected a passing gate, blockers were: " + string.Join(" | ", gate.Blockers));
                Assert.DoesNotContain(gate.Blockers, b =>
                    b != null && b.IndexOf("Time Calc", StringComparison.Ordinal) >= 0);
            }
        }

        [Fact]
        public async Task Agent_door_agrees_Time_Calc_is_not_a_missing_source_column()
        {
            var (engine, sessions) = await PassingModelAsync();
            using (engine)
            {
                await AddTimeCalcGroupAsync(engine, sessions);

                var scan = await McpTools.BpaScan(engine, ruleId: SourceColumnRuleId);
                Assert.DoesNotContain(scan.Violations, IsTimeCalcSourceFinding);

                var gate = await McpTools.DeployGate(engine);
                Assert.True(gate.Pass, "expected a passing gate on the agent door, blockers were: "
                    + string.Join(" | ", gate.Blockers));
                Assert.DoesNotContain(gate.Blockers, b =>
                    b != null && b.IndexOf("Time Calc", StringComparison.Ordinal) >= 0);
            }
        }

        [Fact]
        public async Task A_real_data_column_with_no_source_still_blocks()
        {
            var (engine, sessions) = await PassingModelAsync();
            using (engine)
            {
                await engine.CreateColumnAsync("table:Sales", "Orphan", "String", "Orphan", "human");
                await sessions.Require().MutateAsync("human", "clear Orphan source", m =>
                {
                    var col = m.Tables["Sales"].Columns["Orphan"] as DataColumn
                        ?? throw new InvalidOperationException("Orphan is not a data column.");
                    col.SourceColumn = "";
                });

                var scan = await engine.BpaScanAsync();
                Assert.Contains(scan.Violations, v =>
                    v.RuleId == SourceColumnRuleId
                    && v.ObjectName != null
                    && v.ObjectName.IndexOf("Orphan", StringComparison.Ordinal) >= 0);

                var gate = await engine.DeployGateAsync(null, "human");
                Assert.False(gate.Pass);
                Assert.Contains(gate.Blockers, b =>
                    b != null && b.IndexOf("source column", StringComparison.OrdinalIgnoreCase) >= 0);

                var mcp = await McpTools.BpaScan(engine, ruleId: SourceColumnRuleId);
                Assert.Contains(mcp.Violations, v =>
                    v.RuleId == SourceColumnRuleId
                    && v.ObjectName != null
                    && v.ObjectName.IndexOf("Orphan", StringComparison.Ordinal) >= 0);
            }
        }

        [Fact]
        public async Task Live_push_is_not_refused_over_Time_Calc()
        {
            var (engine, sessions) = await PassingModelAsync();
            using (engine)
            {
                await AddTimeCalcGroupAsync(engine, sessions);
                var target = RecordedLiveTarget.ContosoDerived();
                target.Attach(engine);

                // Before the rule change this commit threw deploy_live: blocked by the deploy gate
                // naming Time Intelligence[Time Calc]. The fake target is a different model, so the
                // push itself may not apply every object; the defect was the refusal, not the sync.
                var preview = await engine.DeployLiveAsync(
                    RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                    "azcli", null, null, commit: false, origin: "human");
                Assert.False(preview.Committed);

                var ex = await Record.ExceptionAsync(() => engine.DeployLiveAsync(
                    RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                    "azcli", null, null, commit: true, origin: "human",
                    overrideReason: null, confirmToken: preview.ConfirmToken));
                Assert.Null(ex);
            }
        }
    }
}
