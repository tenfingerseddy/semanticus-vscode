using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Next-release catalog completion batch 4. The newly-added table-synonym rule and the previously-shipped
    /// Prep-for-AI rules prove violation, clean and dormant states before their research rows move to covered.
    /// DATE-AMBIGUOUS and the three summarization rules retain their dedicated batch-2/batch-3 behavior tests.
    /// </summary>
    public sealed class ReadinessCatalogCompletionBatch4Tests : IClassFixture<TemplateCopies>
    {
        private readonly TemplateCopies _copies;
        public ReadinessCatalogCompletionBatch4Tests(TemplateCopies copies) => _copies = copies;

        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private async Task<(LocalEngine Engine, SessionManager Sessions)> BaseModelAsync(string name = "CatalogCompletionBatch4", int compatibilityLevel = 1604)
        {
            var sessions = new SessionManager();
            return (await _copies.OpenAsync("base-" + compatibilityLevel, e => BuildBaseModelAsync(e, compatibilityLevel), new Fake(), sessions), sessions);
        }

        // The model's name is not read by any test here, so one template serves every caller (a name per test would be a build per test).
        private static async Task BuildBaseModelAsync(LocalEngine engine, int compatibilityLevel)
        {
            await engine.CreateModelAsync("CatalogCompletionBatch4", compatibilityLevel);
            var table = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(table, "Customer Name", "String", "Customer Name", "human");
        }

        private static RuleEvaluation Eval(SessionManager sessions, string id)
        {
            PrepForAiReader.Invalidate(sessions.Current.Model);
            return ReadinessRuleSet.Default().Single(r => r.Id == id).Evaluate(sessions.Current.Model);
        }

        private static string TempDir(string name)
        {
            var path = Path.Combine(Path.GetTempPath(), "semanticus-readiness-" + name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        // ---- create_linguistic_schema (renamed from enable_qna, 2026-09-23) ------------------------------------
        // The op creates the synonym store Copilot and data agents read. It never touched the Power BI Q&A
        // setting, so the Q&A name was wrong twice over. enable_qna survives only as a hidden alias for saved
        // plans and workflow files: never a tool, never in the op catalogue.

        [Fact]
        public async Task Create_linguistic_schema_runs_on_both_doors_and_opens_the_synonym_chain()
        {
            var (agentEngine, agentSessions) = await BaseModelAsync("LinguisticMcp");
            using (agentEngine)
            {
                Assert.Single(Eval(agentSessions, "SYN-SCHEMA").Violations);
                // The synonym gap is counted before the schema exists (the Plan statement check, 28 Sep), so creating
                // it opens nothing new: the same one field and one table are still the work afterwards.
                Assert.Single(Eval(agentSessions, "SYN-FIELD").Violations);
                var r = await McpTools.CreateLinguisticSchema(agentEngine);
                Assert.True(r.Changed);
                Assert.Empty(Eval(agentSessions, "SYN-SCHEMA").Violations);
                Assert.Single(Eval(agentSessions, "SYN-FIELD").Violations);
                Assert.Single(Eval(agentSessions, "SYN-TABLE").Violations);
            }

            var (uiEngine, uiSessions) = await BaseModelAsync("LinguisticRpc");
            using (uiEngine)
            {
                var r = await new EngineRpcTarget(uiEngine).createLinguisticSchema();
                Assert.True(r.Changed);
                Assert.Empty(Eval(uiSessions, "SYN-SCHEMA").Violations);
                Assert.Single(Eval(uiSessions, "SYN-FIELD").Violations);
                Assert.Single(Eval(uiSessions, "SYN-TABLE").Violations);
            }
        }

        [Fact]
        public async Task The_tool_list_and_op_catalogue_carry_create_linguistic_schema_and_never_enable_qna()
        {
            var tools = ProFeatureMapTests.CompiledOperationNames();
            Assert.Contains("create_linguistic_schema", tools);
            Assert.DoesNotContain("enable_qna", tools);
            Assert.Contains(ProFeatureMapTests.RpcEntries(), m => m.Name == "createLinguisticSchema");
            Assert.DoesNotContain(ProFeatureMapTests.RpcEntries(), m => m.Name == "enableQna");
            Assert.DoesNotContain(ProFeatureMapTests.EngineMethods(), m => m.Name == "EnableQnaAsync");

            var (engine, _) = await BaseModelAsync("LinguisticCatalog");
            using (engine)
            {
                var catalog = (await engine.GetOpCatalogAsync()).Select(o => o.Name).ToArray();
                Assert.Contains("create_linguistic_schema", catalog);
                Assert.DoesNotContain("enable_qna", catalog);
            }
        }

        [Fact]
        public async Task The_plan_seeds_the_schema_under_the_new_kind_and_a_legacy_enable_qna_item_still_applies()
        {
            var (engine, sessions) = await BaseModelAsync("LinguisticPlan");
            using (engine)
            {
                var plan = await engine.ProposePlanAsync(null, false, 0, "human");
                var seed = plan.Items.Single(i => i.RuleId == "SYN-SCHEMA");
                Assert.Equal("create_linguistic_schema", seed.Kind);
                Assert.Equal("approved", seed.Status);
                var report = await engine.ApplyPlanAsync(new[] { seed.Id }, "human");
                Assert.Equal(0, report.FailedCount);
                Assert.Empty(Eval(sessions, "SYN-SCHEMA").Violations);
                Assert.Single(Eval(sessions, "SYN-FIELD").Violations);   // the chain opens under the new name
            }

            var (legacyEngine, legacySessions) = await BaseModelAsync("LinguisticLegacyPlan");
            using (legacyEngine)
            {
                // An agent or saved instruction that still says enable_qna must keep working.
                var view = await legacyEngine.AddPlanItemAsync("model:", "enable_qna", null, null, null, null, "agent");
                var item = view.Items.Single(i => i.Target == "linguisticSchema");
                Assert.Equal("create_linguistic_schema", item.Kind);
                var report = await legacyEngine.ApplyPlanAsync(new[] { item.Id }, "human");
                Assert.Equal(0, report.FailedCount);
                Assert.Empty(Eval(legacySessions, "SYN-SCHEMA").Violations);
            }
        }

        [Fact]
        public async Task Synonym_rules_partition_schema_fields_and_tables_without_scoring_unreadable_models()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                var missingSchema = Eval(sessions, "SYN-SCHEMA");
                Assert.Equal(1, missingSchema.Applicable);
                Assert.Single(missingSchema.Violations);
                // Before the schema every visible field and table is counted, and all of them lack synonyms.
                var fieldsBefore = Eval(sessions, "SYN-FIELD");
                Assert.True(fieldsBefore.Applicable > 0);
                Assert.Equal(fieldsBefore.Applicable, fieldsBefore.Violations.Count);
                var tablesBefore = Eval(sessions, "SYN-TABLE");
                Assert.Equal(1, tablesBefore.Applicable);
                Assert.Single(tablesBefore.Violations);

                await engine.CreateLinguisticSchemaAsync(null, "agent");
                Assert.Empty(Eval(sessions, "SYN-SCHEMA").Violations);
                Assert.Single(Eval(sessions, "SYN-FIELD").Violations);
                var tableMissing = Eval(sessions, "SYN-TABLE");
                Assert.Equal(1, tableMissing.Applicable);
                Assert.Single(tableMissing.Violations);

                await engine.SetSynonymsAsync("column:Sales/Customer Name", new[] { "client name" }, null, "agent");
                await engine.SetSynonymsAsync("table:Sales", new[] { "orders" }, null, "agent");
                Assert.Empty(Eval(sessions, "SYN-FIELD").Violations);
                Assert.Empty(Eval(sessions, "SYN-TABLE").Violations);
            }

            using var legacy = new TabularModelHandler(1400);
            var rules = ReadinessRuleSet.Default().ToDictionary(r => r.Id, StringComparer.Ordinal);
            Assert.Equal(0, rules["SYN-SCHEMA"].Evaluate(legacy.Model).Applicable);
            Assert.Equal(0, rules["SYN-FIELD"].Evaluate(legacy.Model).Applicable);
            Assert.Equal(0, rules["SYN-TABLE"].Evaluate(legacy.Model).Applicable);
        }

        [Fact]
        public async Task Ai_instruction_rules_cover_missing_grounding_and_undefined_business_codes()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Equal(0, Eval(sessions, "DAC-AI-INSTRUCTIONS").Applicable);
                Assert.Equal(0, Eval(sessions, "DAC-GLOSSARY-GAP").Applicable);

                await engine.CreateLinguisticSchemaAsync(null, "agent");
                Assert.Single(Eval(sessions, "DAC-AI-INSTRUCTIONS").Violations);
                Assert.Equal(0, Eval(sessions, "DAC-GLOSSARY-GAP").Applicable);

                await engine.SetAiInstructionsAsync("Use fiscal years and report currency.", null, "agent");
                Assert.Empty(Eval(sessions, "DAC-AI-INSTRUCTIONS").Violations);
                Assert.Empty(Eval(sessions, "DAC-GLOSSARY-GAP").Violations);

                await engine.CreateMeasureAsync("table:Sales", "ZXQ Revenue", "1", "human");
                Assert.Single(Eval(sessions, "DAC-GLOSSARY-GAP").Violations);

                await engine.SetAiInstructionsAsync("ZXQ means channel-adjusted revenue. Use fiscal years and report currency.", null, "agent");
                var grounded = Eval(sessions, "DAC-GLOSSARY-GAP");
                Assert.Equal(1, grounded.Applicable);
                Assert.Empty(grounded.Violations);
            }
        }

        [Fact]
        public async Task Qna_prerequisite_is_scored_only_when_the_pbip_switch_is_readable()
        {
            var (memoryOnly, memorySessions) = await BaseModelAsync("MemoryOnlyCatalogCompletionBatch4");
            using (memoryOnly)
                Assert.Equal(0, Eval(memorySessions, "DAC-QNA-DISABLED").Applicable);

            var dir = TempDir("qna-prerequisite");
            var sessions = new SessionManager();
            try
            {
                var copy = Path.Combine(dir, "AdventureWorks.bim");
                var definition = Path.Combine(dir, "definition.pbism");
                File.Copy(TestModels.FindBim(), copy);
                File.WriteAllText(definition, "{ \"settings\": { \"qnaEnabled\": false } }");
                using var engine = new LocalEngine(sessions, new Fake(), dir);
                await engine.OpenAsync(copy);

                var disabled = Eval(sessions, "DAC-QNA-DISABLED");
                Assert.Equal(1, disabled.Applicable);
                var finding = Assert.Single(disabled.Violations);
                // enable_qna seeds the linguistic schema; it never flips definition.pbism qnaEnabled, so the finding
                // must not send the agent there. The copy is about what Copilot and data agents lose.
                Assert.DoesNotContain("enable_qna", finding.Message);
                Assert.Null(finding.DisplayMessage);
                Assert.Contains("Copilot", finding.Message);

                File.WriteAllText(definition, "{ \"settings\": { \"qnaEnabled\": true } }");
                var enabled = Eval(sessions, "DAC-QNA-DISABLED");
                Assert.Equal(1, enabled.Applicable);
                Assert.Empty(enabled.Violations);
            }
            finally
            {
                sessions.Dispose();
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
