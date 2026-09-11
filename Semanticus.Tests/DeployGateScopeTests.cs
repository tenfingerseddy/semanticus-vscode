using System;
using System.IO;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// D-005: the deploy gate used to refuse a one-measure change because of warnings on objects
    /// this change did not touch. The gate now scores the change set. Older whole-model warnings
    /// become a count, not a block.
    /// </summary>
    public sealed class DeployGateScopeTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static async Task<string> WriteDirtyBimAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.CreateModelAsync("Dirty", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            var canary = await engine.CreateMeasureAsync(t, "UAT Canary", "1 + 1", "human");
            await engine.SetMeasureFormatAsync(canary, "#,0", "human");
            await engine.SetDescriptionAsync(canary,
                "A canary measure used to prove a one-object change does not inherit older warnings.", "human");
            for (var i = 0; i < 8; i++)
                await engine.CreateMeasureAsync(t, "Junk" + i, "1", "human");
            var dt = await engine.CreateTableAsync("Date", "human");
            var dkey = await engine.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await engine.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await engine.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await engine.MarkDateTableAsync(dt, "Date", "human");
            var path = Path.Combine(Path.GetTempPath(), "sem-gate-scope-" + Guid.NewGuid().ToString("N") + ".bim");
            await engine.SaveAsync(path, "bim");
            engine.Dispose();
            return path;
        }

        private static async Task<(LocalEngine engine, string path)> OpenDirtyAsync()
        {
            var path = await WriteDirtyBimAsync();
            var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.OpenAsync(path);
            return (engine, path);
        }

        // The D-005 repro: open a model that already has many warnings, change one clean measure,
        // and the gate must still pass. Older warnings stay visible as a count.
        [Fact]
        public async Task One_measure_change_does_not_block_on_older_whole_model_warnings()
        {
            var (engine, path) = await OpenDirtyAsync();
            using (engine)
            {
                try
                {
                    await engine.SetObjectPropertyAsync("measure:Sales/UAT Canary", "Expression", "2 + 2", "human");
                    var gate = await engine.DeployGateAsync(null, "human");

                    Assert.True(gate.Pass,
                        "a one-measure change must not inherit the model's older warnings; blockers were: "
                        + string.Join(" | ", gate.Blockers));
                    Assert.Empty(gate.Blockers);
                    Assert.True(gate.OlderWarnings >= 8, "older warnings must still be counted, got " + gate.OlderWarnings);
                    Assert.Contains("nothing to fix", gate.CheckLine, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("older warning", gate.CheckLine, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("AI Readiness", gate.CheckLine, StringComparison.Ordinal);
                    Assert.DoesNotContain("\u2014", gate.CheckLine, StringComparison.Ordinal);
                    Assert.DoesNotContain("BPA", gate.CheckLine, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("list_waivers", gate.CheckLine, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal("Gate passed.", gate.Note);
                }
                finally { File.Delete(path); }
            }
        }

        // Dual drive: the agent door reads the same scoped gate.
        [Fact]
        public async Task Agent_door_sees_the_same_scoped_gate()
        {
            var (engine, path) = await OpenDirtyAsync();
            using (engine)
            {
                try
                {
                    await engine.SetObjectPropertyAsync("measure:Sales/UAT Canary", "Expression", "2 + 2", "human");
                    var human = await engine.DeployGateAsync(null, "human");
                    var agent = await McpTools.DeployGate(engine);
                    Assert.Equal(human.Pass, agent.Pass);
                    Assert.Equal(human.OlderWarnings, agent.OlderWarnings);
                    Assert.Equal(human.CheckLine, agent.CheckLine);
                    Assert.True(agent.Pass);
                }
                finally { File.Delete(path); }
            }
        }

        // A finding this change introduces still blocks, and names the object.
        [Fact]
        public async Task A_problem_this_change_introduces_still_blocks()
        {
            var (engine, path) = await OpenDirtyAsync();
            using (engine)
            {
                try
                {
                    await engine.CreateMeasureAsync("table:Sales", "New Bad", "1", "human");
                    var gate = await engine.DeployGateAsync(null, "human");
                    Assert.False(gate.Pass);
                    Assert.NotEmpty(gate.Blockers);
                    var text = string.Join(" ", gate.Blockers) + " " + gate.CheckLine;
                    Assert.True(
                        text.IndexOf("New Bad", StringComparison.Ordinal) >= 0
                        || text.IndexOf("undescribed", StringComparison.OrdinalIgnoreCase) >= 0,
                        "the new measure must be why the gate blocked; saw: " + text);
                    Assert.Contains("Checks on this change", gate.CheckLine, StringComparison.Ordinal);
                    Assert.Contains("AI Readiness", gate.CheckLine, StringComparison.Ordinal);
                    Assert.DoesNotContain("\u2014", gate.CheckLine, StringComparison.Ordinal);
                    Assert.DoesNotContain("list_waivers", gate.CheckLine, StringComparison.OrdinalIgnoreCase);
                }
                finally { File.Delete(path); }
            }
        }

        // D-005 live path: a one-measure commit against the recorded live double does not need an override.
        [Fact]
        public async Task Live_commit_of_one_measure_does_not_need_an_override_for_older_warnings()
        {
            var (engine, path) = await OpenDirtyAsync();
            using (engine)
            {
                try
                {
                    engine.DeployLiveSyncHook = (bim, endpoint, database, commit, _dels) =>
                        new DeployReport
                        {
                            Committed = commit,
                            TotalChanges = 1,
                            Endpoint = endpoint,
                            Database = database,
                            Changes = new[] { "measure:Sales/UAT Canary expression" },
                        };
                    await engine.SetObjectPropertyAsync("measure:Sales/UAT Canary", "Expression", "2 + 2", "human");

                    var preview = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: false, origin: "human");
                    Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));

                    var rep = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: true, origin: "human",
                        overrideReason: null, confirmToken: preview.ConfirmToken);
                    Assert.True(rep.Committed, rep.Error);
                }
                finally { File.Delete(path); }
            }
        }

        [Fact]
        public void Check_line_copy_has_the_four_states()
        {
            var clean = Semanticus.Analysis.GateBlockerCopy.CheckLine(Array.Empty<string>(), 0);
            Assert.Equal("Checks on this change: nothing to fix.", clean);

            var older = Semanticus.Analysis.GateBlockerCopy.CheckLine(Array.Empty<string>(), 20);
            Assert.Equal(
                "Checks on this change: nothing to fix. The model has 20 older warnings this change did not cause. See them on AI Readiness.",
                older);

            var one = Semanticus.Analysis.GateBlockerCopy.CheckLine(Array.Empty<string>(), 1);
            Assert.Contains("1 older warning", one, StringComparison.Ordinal);
            Assert.DoesNotContain("warnings", one, StringComparison.Ordinal);

            var blocked = Semanticus.Analysis.GateBlockerCopy.CheckLine(
                new[] { "1 warning to fix before this change can ship. Start with: Provide format string for measures (UAT Canary)." },
                20);
            Assert.StartsWith("Checks on this change:", blocked, StringComparison.Ordinal);
            Assert.Contains("UAT Canary", blocked, StringComparison.Ordinal);
            Assert.Contains("20 older warnings", blocked, StringComparison.Ordinal);
            Assert.Contains("AI Readiness", blocked, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2014", blocked, StringComparison.Ordinal);
            Assert.DoesNotContain("BPA", blocked, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("list_waivers", blocked, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Scope_match_is_exact_object_or_child_of_a_changed_table()
        {
            var changed = new[] { "measure:Sales/UAT Canary" };
            Assert.True(DeployGateScope.Touches("measure:Sales/UAT Canary", changed, wholeModel: false));
            Assert.False(DeployGateScope.Touches("measure:Sales/Junk0", changed, wholeModel: false));
            Assert.False(DeployGateScope.Touches("obj:Model/Model", changed, wholeModel: false));
            Assert.True(DeployGateScope.Touches("obj:Model/Model", changed, wholeModel: true));
            Assert.True(DeployGateScope.Touches("measure:Sales/Junk0", Array.Empty<string>(), wholeModel: true));

            var tableChanged = new[] { "table:Sales" };
            Assert.True(DeployGateScope.Touches("measure:Sales/UAT Canary", tableChanged, wholeModel: false));
            Assert.False(DeployGateScope.Touches("measure:Other/X", tableChanged, wholeModel: false));

            Assert.True(DeployGateScope.ForcesWholeModel(Array.Empty<string>()));
            Assert.True(DeployGateScope.ForcesWholeModel(new[] { "model:" }));
            Assert.False(DeployGateScope.ForcesWholeModel(new[] { "measure:Sales/UAT Canary" }));
        }
    }
}
