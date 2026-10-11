using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Verifier probes for m11/sol6-gate (join 6 area B): consent, five Basis rows changed between the yes and the retry, and the
    /// below-Locked and person-door counts for the new replace and clear holds. Each goes through the real assistant door.</summary>
    [Collection("restore-root")]
    public sealed class VerifySol6GateProbeTests : GateFixtureBase
    {
        public VerifySol6GateProbeTests(RestoreRootFixture fixture) : base(fixture) { }

        private const string Locked = AssistantGateKit.Locked;

        private static async Task<ApprovalRecord[]> UnansweredAsync(LocalEngine engine) =>
            (await engine.ListPendingApprovalsAsync()).Where(p => p.GrantedUtc == null).ToArray();

        private static async Task ApproveAskedAsync(LocalEngine engine) =>
            await engine.ApproveAgentActionAsync(Assert.Single(await UnansweredAsync(engine)).Id, "human");

        private static async Task TryAsync(Func<Task> call) { try { await call(); } catch (InvalidOperationException) { } }

        // ---------------------------------------------------------------- consent

        private sealed class NeverDecides : IDecisionOracle
        {
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct) => Task.FromResult(DecisionAnswers.NotDecided(request, "never"));
        }

        private static FabricTable Table(string name, string key) => new FabricTable
        {
            Schema = "dbo", Name = name, KeyColumns = new[] { key },
            Columns = new[]
            {
                new FabricColumn { Name = key, DataType = "Int64", SqlType = "bigint", Ordinal = 1 },
                new FabricColumn { Name = "Amount", DataType = "Decimal", SqlType = "decimal", Ordinal = 2 },
            },
        };

        private async Task<LocalEngine> StartPlanAsync()
        {
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro, Path.Combine(Root, "ws"));
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(new FabricSchema
            {
                Server = "x.datawarehouse.fabric.microsoft.com", Database = "Wh",
                Tables = new[] { Table("Payroll", "EmployeeId"), Table("Bonus", "BonusId") },
                ForeignKeys = Array.Empty<FabricForeignKey>(),
            });
            engine.JevBatchedForTest = _ => true;
            engine.StartDecideKickDisabledForTest = true;
            engine.UseDecisionsForTest(new DecisionService(new NeverDecides(), new DecisionBudget()));
            var view = await engine.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "x.datawarehouse.fabric.microsoft.com", Database = "Wh", Folder = Path.Combine(Root, "proj"), Name = "Payroll Model",
            }, "human");
            Assert.True(view.Exists);
            await engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            await engine.StartSampleSettledForTest();
            return engine;
        }

        [Fact]
        public async Task Consent_a_table_added_after_the_yes_is_a_new_question_and_the_old_digest_is_stale()
        {
            using var engine = await StartPlanAsync();
            AgentPolicyStore.SetPreset("open", "human");
            var gate = Gate(engine);
            var narrowed = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "Bonus", Value = "false" }, "human");
            var d1 = narrowed.Digest;
            var asked = await McpTools.GrantStartDisclosure(gate, d1);
            Assert.True(asked.Asked);
            var q = Assert.Single(await UnansweredAsync(engine));
            Assert.Contains("Payroll", q.Summary);
            Assert.DoesNotContain("Bonus", q.Summary);
            await ApproveAskedAsync(engine);

            var widened = await engine.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "Bonus", Value = "true" }, "human");
            Assert.NotEqual(d1, widened.Digest);
            await TryAsync(() => McpTools.GrantStartDisclosure(gate, d1));            // the old digest: stale
            Assert.Null(engine.StartPlanHost.Current().Disclosure);
            var again = await McpTools.GrantStartDisclosure(gate, widened.Digest);     // the wider plan: a new question
            Assert.True(again.Asked);
            Assert.Null(engine.StartPlanHost.Current().Disclosure);
            var q2 = Assert.Single(await UnansweredAsync(engine));
            Assert.Contains("Bonus", q2.Summary);
        }

        [Theory]
        [InlineData("open")]
        [InlineData("standard")]
        [InlineData("cautious")]
        [InlineData("client")]
        [InlineData(Locked)]
        public async Task Consent_the_mcp_tool_answers_asked_and_the_persons_own_door_never_asks(string preset)
        {
            using var engine = await StartPlanAsync();
            AgentPolicyStore.SetPreset(preset, "human");
            var gate = Gate(engine);
            var digest = (await engine.GetStartPlanAsync()).Digest;
            var view = await McpTools.GrantStartDisclosure(gate, digest);
            Assert.True(view.Asked, "the tool did not say it asked");
            Assert.Null(engine.StartPlanHost.Current().Disclosure);
            // the assistant cannot answer its own question
            var id = Assert.Single(await UnansweredAsync(engine)).Id;
            await Assert.ThrowsAnyAsync<Exception>(() => gate.ApproveAgentActionAsync(id, "agent"));
            await TryAsync(() => gate.GrantStartDisclosureAsync(digest, "agent"));
            Assert.Null(engine.StartPlanHost.Current().Disclosure);
            foreach (var p in await engine.ListPendingApprovalsAsync()) await engine.DenyAgentActionAsync(p.Id, "human");
            // the person's own door grants it with no question
            await engine.GrantStartDisclosureAsync(digest, "human");
            Assert.NotNull(engine.StartPlanHost.Current().Disclosure);
            Assert.Empty(await UnansweredAsync(engine));
        }

        [Fact]
        public async Task Consent_a_rehearsal_of_the_grant_records_nothing()
        {
            using var engine = await StartPlanAsync();
            AgentPolicyStore.SetPreset("open", "human");
            var gate = Gate(engine);
            var digest = (await engine.GetStartPlanAsync()).Digest;
            await TryAsync(() => gate.DryRunOpAsync("grant_start_disclosure", "{\"digest\":\"" + digest + "\"}"));
            Assert.Null(engine.StartPlanHost.Current().Disclosure);
        }

        // ---------------------------------------------------------------- five Basis rows: change what the write reads between the yes and the retry

        private static string SpecJson(string table) =>
            "{\"name\":\"S\",\"compatibilityLevel\":1604,\"storageMode\":\"import\",\"tables\":[{\"name\":\"" + table + "\",\"role\":\"dimension\","
            + "\"mExpression\":\"let Source = 1 in Source\",\"columns\":[{\"name\":\"C\",\"dataType\":\"Int64\"}]}],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}";

        private static async Task<string[]> SpecTablesAsync(LocalEngine engine) =>
            ((await engine.GetSpecAsync()).Spec?.Tables ?? Array.Empty<SpecTable>()).Select(t => t.Name).ToArray();

        [Fact]
        public async Task Basis_save_spec_is_not_spent_on_a_spec_replaced_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("v-save-spec");
            using (engine)
            {
                await engine.SetSpecAsync(SpecJson("Shown"), "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                var target = Path.Combine(Root, "v-save-spec", "out.spec.json");
                await RefusedAsync(() => gate.SaveSpecAsync(target, "agent"));
                await ApproveAskedAsync(engine);
                await engine.SetSpecAsync(SpecJson("NotShown"), "human");
                await RefusedAsync(() => gate.SaveSpecAsync(target, "agent"));
                Assert.False(File.Exists(target), "the save wrote a spec the person was not shown");
                Assert.Single(await UnansweredAsync(engine));
            }
        }

        [Fact]
        public async Task Basis_load_spec_is_not_spent_on_a_file_changed_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("v-load-spec");
            using (engine)
            {
                await engine.SetSpecAsync(SpecJson("PersonsOwn"), "human");
                var file = Path.Combine(Root, "v-load-spec", "in.spec.json");
                File.WriteAllText(file, SpecJson("FromFileShown"));
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.LoadSpecAsync(file, "agent"));
                await ApproveAskedAsync(engine);
                File.WriteAllText(file, SpecJson("FromFileChanged"));
                await RefusedAsync(() => gate.LoadSpecAsync(file, "agent"));
                Assert.Equal(new[] { "PersonsOwn" }, await SpecTablesAsync(engine));
                Assert.Single(await UnansweredAsync(engine));
            }
        }

        [Fact]
        public async Task Basis_autogenerate_spec_is_not_spent_on_a_spec_the_person_replaced_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("v-autogen");
            using (engine)
            {
                await engine.SetSpecAsync(SpecJson("First"), "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.AutogenerateSpecFromModelAsync("agent"));
                await ApproveAskedAsync(engine);
                await engine.SetSpecAsync(SpecJson("PersonsNewer"), "human");
                await RefusedAsync(() => gate.AutogenerateSpecFromModelAsync("agent"));
                Assert.Equal(new[] { "PersonsNewer" }, await SpecTablesAsync(engine));
            }
        }

        [Fact]
        public async Task Basis_purge_is_not_spent_on_a_note_added_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("v-purge");
            using (engine)
            {
                await engine.AddInsightAsync("first note", new[] { "k" }, "insight", "project", false, "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.PurgeKnowledgeAsync("project", true, "agent"));
                await ApproveAskedAsync(engine);
                await engine.AddInsightAsync("the person's later note", new[] { "k2" }, "insight", "project", false, "human");
                await RefusedAsync(() => gate.PurgeKnowledgeAsync("project", true, "agent"));
                Assert.Equal(2, (await engine.ListInsightsAsync("project", null)).Insights.Length);
                Assert.Single(await UnansweredAsync(engine));
            }
        }

        [Fact]
        public async Task Basis_clear_plan_is_not_spent_on_a_line_the_person_added_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("v-clear-plan");
            using (engine)
            {
                var first = (await engine.AddPlanItemAsync("measure:Sales/Total", "set_description", "first words", null, null, null, "human")).Items.Single().Id;
                await engine.SetPlanItemAsync(first, null, true, "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.ClearPlanAsync("agent"));
                await ApproveAskedAsync(engine);
                await engine.AddPlanItemAsync("measure:Sales/Margin", "set_description", "later words", null, null, null, "human");
                await RefusedAsync(() => gate.ClearPlanAsync("agent"));
                Assert.Equal(2, (await engine.GetPlanAsync()).Items.Length);
                Assert.Single(await UnansweredAsync(engine));
            }
        }

        [Fact]
        public async Task Basis_propose_plan_is_not_spent_on_a_line_the_person_rewrote_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("v-propose");
            using (engine)
            {
                var line = (await engine.AddPlanItemAsync("measure:Sales/Total", "set_description", "first words", null, null, null, "human")).Items.Single().Id;
                await engine.SetPlanItemAsync(line, null, true, "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.ProposePlanAsync(null, false, 0, "agent"));
                await ApproveAskedAsync(engine);
                await engine.SetPlanItemAsync(line, "the person's rewritten words", null, "human");
                await RefusedAsync(() => gate.ProposePlanAsync(null, false, 0, "agent"));
                Assert.Contains((await engine.GetPlanAsync()).Items, i => i.Id == line);
                Assert.Single(await UnansweredAsync(engine));
            }
        }

        // ---------------------------------------------------------------- below Locked and the person's door

        [Theory]
        [InlineData("open")]
        [InlineData("standard")]
        [InlineData("cautious")]
        [InlineData("client")]
        public async Task Below_Locked_every_replace_and_clear_of_held_content_asks_nothing(string preset)
        {
            var (engine, _, _) = await OpenAsync("v-below-" + preset);
            using (engine)
            {
                var file = Path.Combine(Root, "v-below-" + preset, "in.spec.json");
                File.WriteAllText(file, SpecJson("FromFile"));
                await engine.SetSpecAsync(SpecJson("Mine"), "human");
                var line = (await engine.AddPlanItemAsync("measure:Sales/Total", "set_description", "words", null, null, null, "human")).Items.Single().Id;
                await engine.SetPlanItemAsync(line, null, true, "human");
                AgentPolicyStore.SetPreset(preset, "human");
                var gate = Gate(engine);
                await gate.SetSpecAsync(SpecJson("Other"), "agent");
                await gate.LoadSpecAsync(file, "agent");
                await gate.AutogenerateSpecFromModelAsync("agent");
                await gate.SaveSpecAsync(Path.Combine(Root, "v-below-" + preset, "out.spec.json"), "agent");
                await gate.ProposePlanAsync(null, false, 0, "agent");
                await gate.ClearSpecAsync("agent");
                line = (await engine.AddPlanItemAsync("measure:Sales/Total", "set_description", "words 2", null, null, null, "human")).Items.Last().Id;
                await engine.SetPlanItemAsync(line, null, true, "human");
                await gate.ClearPlanAsync("agent");
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                Assert.Null((await engine.GetSpecAsync()).Spec);
                Assert.Empty((await engine.GetPlanAsync()).Items);
            }
        }

        [Fact]
        public async Task Under_Locked_the_persons_own_door_never_asks_for_a_replace_or_clear()
        {
            var (engine, _, _) = await OpenAsync("v-person");
            using (engine)
            {
                var file = Path.Combine(Root, "v-person", "in.spec.json");
                File.WriteAllText(file, SpecJson("FromFile"));
                await engine.SetSpecAsync(SpecJson("Mine"), "human");
                var line = (await engine.AddPlanItemAsync("measure:Sales/Total", "set_description", "words", null, null, null, "human")).Items.Single().Id;
                await engine.SetPlanItemAsync(line, null, true, "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                await engine.SetSpecAsync(SpecJson("Other"), "human");
                await engine.LoadSpecAsync(file, "human");
                await engine.AutogenerateSpecFromModelAsync("human");
                await engine.ProposePlanAsync(null, false, 0, "human");
                await engine.ClearSpecAsync("human");
                await engine.AddPlanItemAsync("measure:Sales/Total", "set_description", "words 2", null, null, null, "human");
                await engine.ClearPlanAsync("human");
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                Assert.Null((await engine.GetSpecAsync()).Spec);
            }
        }
    }
}
