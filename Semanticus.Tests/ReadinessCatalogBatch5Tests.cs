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
    /// Next-release catalog completion batch 3. The catalog mappings are executable: each touched rule proves its
    /// violation, clean and dormant/advisory states without activating an inapplicable population.
    /// </summary>
    public sealed class ReadinessCatalogBatch5Tests : IClassFixture<TemplateCopies>
    {
        private readonly TemplateCopies _copies;
        public ReadinessCatalogBatch5Tests(TemplateCopies copies) => _copies = copies;

        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private async Task<(LocalEngine Engine, SessionManager Sessions)> BaseModelAsync()
        {
            var sessions = new SessionManager();
            return (await _copies.OpenAsync("base", BuildBaseModelAsync, new Fake(), sessions), sessions);
        }

        private static async Task BuildBaseModelAsync(LocalEngine engine)
        {
            await engine.CreateModelAsync("CatalogBatch5", 1604);
            var sales = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(sales, "Label", "String", "Label", "human");
        }

        private static RuleEvaluation Eval(SessionManager sessions, string id) =>
            ReadinessRuleSet.Default().Single(r => r.Id == id).Evaluate(sessions.Current.Model);

        private static RuleEvaluation LiveEval(SessionManager sessions, ReadinessLiveStats stats, string id) =>
            ReadinessRuleSet.LiveRules(stats).Single(r => r.Id == id).Evaluate(sessions.Current.Model);

        [Fact]
        public async Task Disconnected_and_key_summarization_rules_cover_violation_clean_and_dormant_states()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Equal(0, Eval(sessions, "REL-DISCONNECTED").Applicable);
                Assert.Equal(0, Eval(sessions, "FMT-SUMMARIZE").Applicable);

                var salesKey = await engine.CreateColumnAsync("table:Sales", "Product Key", "Int64", "Product Key", "human");
                var product = await engine.CreateTableAsync("Product", "human");
                var productKey = await engine.CreateColumnAsync(product, "Product Key", "Int64", "Product Key", "human");
                var disconnected = Eval(sessions, "REL-DISCONNECTED");
                Assert.Equal(2, disconnected.Applicable);
                Assert.Equal(2, disconnected.Violations.Count);

                await engine.CreateRelationshipAsync(salesKey, productKey, null, true, "human");
                var connected = Eval(sessions, "REL-DISCONNECTED");
                Assert.Equal(2, connected.Applicable);
                Assert.Empty(connected.Violations);

                var aggregatingKeys = Eval(sessions, "FMT-SUMMARIZE");
                Assert.Equal(2, aggregatingKeys.Applicable);
                Assert.Equal(2, aggregatingKeys.Violations.Count);
                await engine.SetColumnMetadataAsync(salesKey, null, "None", null, null, "human");
                await engine.SetColumnMetadataAsync(productKey, null, "None", null, null, "human");
                var cleanKeys = Eval(sessions, "FMT-SUMMARIZE");
                Assert.Equal(2, cleanKeys.Applicable);
                Assert.Empty(cleanKeys.Violations);
            }
        }

        [Fact]
        public async Task The_safe_fix_clears_every_key_summarization_finding_including_a_numeric_key_left_at_Default()
        {
            // FMT-SUMMARIZE flags a numeric key or relationship end left at SummarizeBy Default, because a numeric
            // Default sums. The safe fix used to skip every Default, so apply_safe_fixes (and the plan's Formatting row)
            // reported a fix and left the finding standing. A text column at Default is not flagged (its Default does
            // not sum) and the fix leaves it alone too: the rule and the fix read one population.
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                var salesKey = await engine.CreateColumnAsync("table:Sales", "Product Key", "Int64", "Product Key", "human");
                var product = await engine.CreateTableAsync("Product", "human");
                var productKey = await engine.CreateColumnAsync(product, "Product Key", "Int64", "Product Key", "human");
                await engine.CreateRelationshipAsync(salesKey, productKey, null, true, "human");
                var region = await engine.CreateTableAsync("Region", "human");
                await engine.CreateColumnAsync(region, "Region Id", "Int64", "Region Id", "human");
                await engine.CreateColumnAsync(region, "Region Code", "String", "Region Code", "human");
                await sessions.Current.MutateAsync("human", "keys", m =>
                {
                    m.Tables["Region"].Columns["Region Id"].IsKey = true;
                    foreach (var c in m.AllColumns) c.SummarizeBy = AggregateFunction.Default;
                });
                var before = Eval(sessions, "FMT-SUMMARIZE");
                Assert.Equal(3, before.Applicable);
                Assert.Equal(new[] { "Product Key", "Product Key", "Region Id" }, before.Violations.Select(v => v.ObjectName).OrderBy(n => n));

                await engine.ApplySafeFixesAsync("human");

                var after = Eval(sessions, "FMT-SUMMARIZE");
                Assert.Equal(3, after.Applicable);
                Assert.Empty(after.Violations);
                var model = sessions.Current.Model;
                Assert.Equal(AggregateFunction.None, model.Tables["Sales"].Columns["Product Key"].SummarizeBy);
                Assert.Equal(AggregateFunction.None, model.Tables["Product"].Columns["Product Key"].SummarizeBy);
                Assert.Equal(AggregateFunction.None, model.Tables["Region"].Columns["Region Id"].SummarizeBy);
                Assert.Equal(AggregateFunction.Default, model.Tables["Region"].Columns["Region Code"].SummarizeBy);
            }
        }

        [Fact]
        public async Task A_text_key_left_at_Default_is_neither_flagged_nor_touched_by_the_safe_fix()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                var region = await engine.CreateTableAsync("Region", "human");
                await engine.CreateColumnAsync(region, "Region Code", "String", "Region Code", "human");
                await sessions.Current.MutateAsync("human", "key", m =>
                {
                    var c = m.Tables["Region"].Columns["Region Code"];
                    c.IsKey = true;
                    c.SummarizeBy = AggregateFunction.Default;
                });
                var before = Eval(sessions, "FMT-SUMMARIZE");
                Assert.Equal(1, before.Applicable);
                Assert.Empty(before.Violations);
                await engine.ApplySafeFixesAsync("human");
                Assert.Equal(AggregateFunction.Default, sessions.Current.Model.Tables["Region"].Columns["Region Code"].SummarizeBy);
            }
        }

        [Fact]
        public async Task Implicit_and_non_additive_numeric_rules_partition_the_visible_numeric_population()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Equal(0, Eval(sessions, "DAC-IMPLICIT-MEASURE").Applicable);
                Assert.Equal(0, Eval(sessions, "SUMMARIZE-DIMENSION").Applicable);

                var revenue = await engine.CreateColumnAsync("table:Sales", "Revenue", "Decimal", "Revenue", "human");
                var implicitMeasure = Eval(sessions, "DAC-IMPLICIT-MEASURE");
                Assert.Equal(1, implicitMeasure.Applicable);
                Assert.Single(implicitMeasure.Violations);
                await engine.SetColumnMetadataAsync(revenue, null, "None", null, null, "human");
                Assert.Empty(Eval(sessions, "DAC-IMPLICIT-MEASURE").Violations);

                var age = await engine.CreateColumnAsync("table:Sales", "Customer Age", "Int64", "Customer Age", "human");
                var nonAdditive = Eval(sessions, "SUMMARIZE-DIMENSION");
                Assert.Equal(1, nonAdditive.Applicable);
                Assert.Single(nonAdditive.Violations);
                Assert.DoesNotContain(Eval(sessions, "DAC-IMPLICIT-MEASURE").Violations, f => f.ObjectName == "Customer Age");
                await engine.SetColumnMetadataAsync(age, null, "None", null, null, "human");
                Assert.Empty(Eval(sessions, "SUMMARIZE-DIMENSION").Violations);

                Assert.True(ReadinessRuleSet.IsNonAdditiveDimensionName("Sort Order"));
                Assert.True(ReadinessRuleSet.IsNonAdditiveDimensionName("Latitude"));
                Assert.False(ReadinessRuleSet.IsNonAdditiveDimensionName("Order Quantity"));
                Assert.False(ReadinessRuleSet.IsNonAdditiveDimensionName("3 Year Return"));
            }
        }

        [Fact]
        public async Task DirectLake_mode_advisory_routes_prep_for_ai_and_the_qna_only_advisory_is_retired()
        {
            // MODE-DIRECTLAKE-QNA only warned that the Desktop Q&A setup tooling cannot teach a Direct Lake model.
            // Microsoft retires Q&A in February 2027 and Copilot reads nothing from that tooling, so the rule is
            // gone. It was advisory (Applicable 0), so no grade moves. CFG-PREP-MODE keeps the Copilot routing note.
            Assert.DoesNotContain(ReadinessRuleSet.Default(), r => r.Id == "MODE-DIRECTLAKE-QNA");

            var (import, importSessions) = await BaseModelAsync();
            using (import)
            {
                Assert.Equal(0, Eval(importSessions, "CFG-PREP-MODE").Applicable);
                Assert.Empty(Eval(importSessions, "CFG-PREP-MODE").Violations);
            }

            var sessions = new SessionManager();
            using var directLake = new LocalEngine(sessions, new Fake());
            await directLake.CreateModelAsync("DirectLake", 1604);
            await directLake.CreateDirectLakeTableAsync("Lake Sales", "sales", null, null, "human");
            var prep = Eval(sessions, "CFG-PREP-MODE");
            Assert.Equal(0, prep.Applicable);
            Assert.Single(prep.Violations);
        }

        [Fact]
        public async Task Qna_unique_value_ceiling_is_dormant_without_stats_and_exact_when_measured()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                var absent = LiveEval(sessions, new ReadinessLiveStats(), "SCALE-QNA-INDEX");
                Assert.Equal(0, absent.Applicable);
                Assert.Empty(absent.Violations);

                var measured = new ReadinessLiveStats();
                measured.Set("Sales", "Label", ReadinessLiveStats.QnaUniqueValueCeiling);
                var clean = LiveEval(sessions, measured, "SCALE-QNA-INDEX");
                Assert.Equal(1, clean.Applicable);
                Assert.Empty(clean.Violations);

                measured.Set("Sales", "Label", ReadinessLiveStats.QnaUniqueValueCeiling + 1);
                var over = LiveEval(sessions, measured, "SCALE-QNA-INDEX");
                Assert.Equal(1, over.Applicable);
                Assert.Single(over.Violations);
            }
        }
    }
}
