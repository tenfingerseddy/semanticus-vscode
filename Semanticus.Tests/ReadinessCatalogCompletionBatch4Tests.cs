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
    public sealed class ReadinessCatalogCompletionBatch4Tests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static async Task<(LocalEngine Engine, SessionManager Sessions)> BaseModelAsync(string name = "CatalogCompletionBatch4", int compatibilityLevel = 1604)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync(name, compatibilityLevel);
            var table = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(table, "Customer Name", "String", "Customer Name", "human");
            return (engine, sessions);
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

        [Fact]
        public async Task Synonym_rules_partition_schema_fields_and_tables_without_scoring_unreadable_models()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                var missingSchema = Eval(sessions, "SYN-SCHEMA");
                Assert.Equal(1, missingSchema.Applicable);
                Assert.Single(missingSchema.Violations);
                Assert.Equal(0, Eval(sessions, "SYN-FIELD").Applicable);
                Assert.Equal(0, Eval(sessions, "SYN-TABLE").Applicable);

                await engine.EnableQnaAsync(null, "agent");
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

                await engine.EnableQnaAsync(null, "agent");
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
                Assert.Single(disabled.Violations);

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
