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
    /// Catalog batch 2 — 7 deterministic AI-readiness rules (docs/ai-readiness-catalog.json gaps). Mirrors batch 1:
    /// each rule is verified on BOTH paths — a synthetic model that induces the violation (fires) and a conforming
    /// variant (dormant/cleared). Presence-design rules stay DORMANT on a clean model (never inflate a category to an
    /// always-pass 100); advisory rules (Applicable=0) surface a finding but never score. Built through the LocalEngine
    /// (the dual-drive path) and scanned with ai_readiness_scan.
    /// </summary>
    public sealed class ReadinessCatalogBatch2Tests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // A clean base model: one fact table, clean well-typed business-named columns, one measure, no hierarchies /
        // perspectives / field parameters / date columns — trips NONE of the batch.
        private static async Task<(LocalEngine engine, SessionManager sessions)> BaseModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(true));
            await engine.CreateModelAsync("Batch2", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Sales Amount", "Decimal", "Sales Amount", "human");
            await engine.CreateColumnAsync(t, "Customer Name", "String", "Customer Name", "human");
            await engine.CreateMeasureAsync(t, "Total Sales", "SUM ( Sales[Sales Amount] )", "human");
            return (engine, sessions);
        }

        private static bool Fires(Scorecard c, string ruleId) => c.Findings.Any(f => f.RuleId == ruleId);
        private static bool FiresOn(Scorecard c, string ruleId, string objName) =>
            c.Findings.Any(f => f.RuleId == ruleId && f.ObjectName == objName);

        // ---- LIMIT-QNA-INDEX (scored presence: fires only over the 1,000-entity ceiling) --------------------------

        [Fact]
        public async Task LimitQnaIndex_dormant_under_the_ceiling_and_fires_over_1000_entities()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "LIMIT-QNA-INDEX"));   // ~4 entities — dormant
                // Add >1000 columns in one mutate (fast single batch) to cross the Q&A 1,000-entity index ceiling.
                await sessions.Current.MutateAsync("human", "widen model past the Q&A index ceiling", m =>
                {
                    var sales = m.Tables.Single(t => t.Name == "Sales");
                    for (int i = 0; i < 1001; i++) sales.AddDataColumn("C" + i, "C" + i);
                });
                Assert.True(Fires(await engine.AiReadinessScanAsync(), "LIMIT-QNA-INDEX"));
            }
        }

        // ---- LIMIT-DATAAGENT-TABLES (advisory: Applicable=0, never scores) ----------------------------------------

        [Fact]
        public async Task LimitDataAgentTables_advisory_fires_over_25_visible_tables_and_dormant_below()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "LIMIT-DATAAGENT-TABLES"));   // 1 table
                for (int i = 0; i < 25; i++) await engine.CreateTableAsync("Dim " + i, "human");       // → 26 visible tables
                var scan = await engine.AiReadinessScanAsync();
                Assert.True(Fires(scan, "LIMIT-DATAAGENT-TABLES"));
                Assert.Equal(nameof(FixKind.Proposal), scan.Findings.First(f => f.RuleId == "LIMIT-DATAAGENT-TABLES").Fix);
            }
        }

        // ---- DATE-AMBIGUOUS (multiple EVENT dates; Start/End range boundaries excluded) ---------------------------

        [Fact]
        public async Task DateAmbiguous_fires_on_two_event_dates_and_clears_when_one_is_hidden()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                var orders = await engine.CreateTableAsync("Orders", "human");
                await engine.CreateColumnAsync(orders, "Order Date", "DateTime", "Order Date", "human");
                var ship = await engine.CreateColumnAsync(orders, "Ship Date", "DateTime", "Ship Date", "human");
                Assert.True(FiresOn(await engine.AiReadinessScanAsync(), "DATE-AMBIGUOUS", "Orders"));
                await engine.SetColumnMetadataAsync(ship, true, null, null, null, "agent");   // hide one → 1 date left
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "DATE-AMBIGUOUS"));
            }
        }

        [Fact]
        public async Task DateAmbiguous_does_not_fire_on_a_start_end_validity_range()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                var p = await engine.CreateTableAsync("Product Validity", "human");
                await engine.CreateColumnAsync(p, "Start Date", "DateTime", "Start Date", "human");
                await engine.CreateColumnAsync(p, "End Date", "DateTime", "End Date", "human");
                // Start/End are one event's validity window (a range), not the ambiguous "which date" case — dormant.
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "DATE-AMBIGUOUS"));
            }
        }

        // ---- REL-HIERARCHY-SINGLE-LEVEL ---------------------------------------------------------------------------

        [Fact]
        public async Task RelHierarchySingleLevel_fires_on_a_one_level_hierarchy_and_dormant_on_multi_level()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                var geo = await engine.CreateTableAsync("Geography", "human");
                await engine.CreateColumnAsync(geo, "Country", "String", "Country", "human");
                await engine.CreateColumnAsync(geo, "State", "String", "State", "human");
                await engine.CreateHierarchyAsync("table:Geography", "Geo Multi", new[] { "Country", "State" }, "agent");
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "REL-HIERARCHY-SINGLE-LEVEL"));   // 2 levels — fine
                await engine.CreateHierarchyAsync("table:Geography", "Geo Stub", new[] { "Country" }, "agent");
                Assert.True(FiresOn(await engine.AiReadinessScanAsync(), "REL-HIERARCHY-SINGLE-LEVEL", "Geo Stub"));
            }
        }

        // ---- NAME-HIERARCHY (cryptic hierarchy name) --------------------------------------------------------------

        [Fact]
        public async Task NameHierarchy_fires_on_a_cryptic_name_and_dormant_on_a_clean_one()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                var geo = await engine.CreateTableAsync("Geography", "human");
                await engine.CreateColumnAsync(geo, "Country", "String", "Country", "human");
                await engine.CreateColumnAsync(geo, "State", "String", "State", "human");
                await engine.CreateHierarchyAsync("table:Geography", "Geography Drill", new[] { "Country", "State" }, "agent");
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "NAME-HIERARCHY"));       // clean name
                await engine.CreateHierarchyAsync("table:Geography", "tbl_Drill", new[] { "Country", "State" }, "agent");
                Assert.True(FiresOn(await engine.AiReadinessScanAsync(), "NAME-HIERARCHY", "tbl_Drill"));
            }
        }

        // ---- DAC-PERSPECTIVE-NOT-SCOPE (advisory) -----------------------------------------------------------------

        [Fact]
        public async Task DacPerspectiveNotScope_advisory_fires_when_perspectives_exist()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "DAC-PERSPECTIVE-NOT-SCOPE"));
                await engine.CreatePerspectiveAsync("Sales View", "human");
                Assert.True(Fires(await engine.AiReadinessScanAsync(), "DAC-PERSPECTIVE-NOT-SCOPE"));
            }
        }

        // ---- DAC-FIELD-PARAM-COMPLEXITY (advisory) ----------------------------------------------------------------

        [Fact]
        public async Task DacFieldParamComplexity_advisory_fires_when_a_field_parameter_exists()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "DAC-FIELD-PARAM-COMPLEXITY"));
                await engine.CreateFieldParameterAsync("Metric Selector",
                    new[] { new FieldParameterItem { ObjectRef = "measure:Sales/Total Sales" } }, "human");
                Assert.True(Fires(await engine.AiReadinessScanAsync(), "DAC-FIELD-PARAM-COMPLEXITY"));
            }
        }

        // ---- Anti-inflation guard: every batch-2 rule is dormant on the clean base model --------------------------

        [Fact]
        public async Task Batch2_rules_are_dormant_on_a_clean_model()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                var c = await engine.AiReadinessScanAsync();
                foreach (var id in new[] { "LIMIT-QNA-INDEX", "LIMIT-DATAAGENT-TABLES", "DATE-AMBIGUOUS",
                                           "REL-HIERARCHY-SINGLE-LEVEL", "NAME-HIERARCHY",
                                           "DAC-PERSPECTIVE-NOT-SCOPE", "DAC-FIELD-PARAM-COMPLEXITY" })
                    Assert.False(Fires(c, id));
                // No new rule introduces a NaN / out-of-range category score (the Weights KeyNotFound hazard guard).
                Assert.All(c.Categories, x => Assert.InRange(x.Score, 0, 100));
            }
        }
    }
}
