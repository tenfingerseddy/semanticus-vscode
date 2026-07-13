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
    /// Catalog batch 1 — 10 deterministic AI-readiness rules (docs/ai-readiness-catalog.json gaps). Each rule is
    /// verified on BOTH paths: a synthetic model that induces the violation (fires) and a conforming variant
    /// (dormant/cleared). Mirrors the BestPracticeRulesTests harness: build through the LocalEngine (the dual-drive
    /// path) and scan with ai_readiness_scan. Presence-design rules stay DORMANT on a clean model (never inflate a
    /// category to an always-pass 100); advisory rules (Applicable=0) surface a finding but never score.
    /// </summary>
    public sealed class ReadinessCatalogBatch1Tests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // A clean base model: one fact table with clean, well-typed, business-named columns — trips NONE of the batch.
        private static async Task<(LocalEngine engine, SessionManager sessions)> BaseModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(true));
            await engine.CreateModelAsync("Batch1", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Sales Amount", "Decimal", "Sales Amount", "human");
            await engine.CreateColumnAsync(t, "Customer Name", "String", "Customer Name", "human");
            await engine.CreateMeasureAsync(t, "Total Sales", "SUM ( Sales[Sales Amount] )", "human");
            return (engine, sessions);
        }

        private static bool Fires(Scorecard c, string ruleId) => c.Findings.Any(f => f.RuleId == ruleId);
        private static bool FiresOn(Scorecard c, string ruleId, string objName) =>
            c.Findings.Any(f => f.RuleId == ruleId && f.ObjectName == objName);
        private static CategoryScore Cat(Scorecard c, ReadinessCategory cat) =>
            c.Categories.First(x => x.Category == cat.ToString());

        // ---- NAME-GENERIC ------------------------------------------------------------------------------------------

        [Fact]
        public async Task NameGeneric_fires_on_a_placeholder_table_and_is_dormant_when_clean()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "NAME-GENERIC"));   // clean baseline
                await engine.CreateTableAsync("Table1", "human");                            // auto-generated placeholder
                Assert.True(FiresOn(await engine.AiReadinessScanAsync(), "NAME-GENERIC", "Table1"));
            }
        }

        [Fact]
        public async Task NameGeneric_does_not_fire_on_a_real_business_name()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                await engine.CreateTableAsync("Order Detail", "human");   // real name with a trailing word, not "…N"
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "NAME-GENERIC"));
            }
        }

        // ---- FMT-DATATYPE ------------------------------------------------------------------------------------------

        [Fact]
        public async Task FmtDataType_fires_on_a_string_typed_date_column_and_clears_when_retyped()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                var col = await engine.CreateColumnAsync("table:Sales", "Order Date", "String", "Order Date", "human");
                Assert.True(FiresOn(await engine.AiReadinessScanAsync(), "FMT-DATATYPE", "Order Date"));
                await engine.SetColumnDataTypeAsync(col, "DateTime", "agent");
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "FMT-DATATYPE"));
            }
        }

        [Fact]
        public async Task FmtDataType_does_not_fire_on_a_string_column_with_a_text_name()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)   // "Customer Name" (String) implies neither date nor number
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "FMT-DATATYPE"));
        }

        // ---- CAT-URL -----------------------------------------------------------------------------------------------

        [Fact]
        public async Task CatUrl_fires_on_a_url_column_without_a_category_and_clears_when_set()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                var col = await engine.CreateColumnAsync("table:Sales", "Product URL", "String", "Product URL", "human");
                Assert.True(FiresOn(await engine.AiReadinessScanAsync(), "CAT-URL", "Product URL"));
                await engine.SetColumnMetadataAsync(col, null, null, "WebUrl", null, "agent");   // set the data category
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "CAT-URL"));
            }
        }

        [Fact]
        public async Task CatUrl_does_not_fire_on_a_binary_photo_column_without_a_url_token()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                await engine.CreateColumnAsync("table:Sales", "Large Photo", "String", "Large Photo", "human");
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "CAT-URL"));
            }
        }

        // ---- REL-INACTIVE ------------------------------------------------------------------------------------------

        // A Date table + Facts with two date FKs: one active relationship, one inactive (role-playing) relationship.
        private static async Task<LocalEngine> DateRolePlayModelAsync(bool addInactive)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(true));
            await engine.CreateModelAsync("Rel", 1604);
            var d = await engine.CreateTableAsync("Date", "human");
            await engine.CreateColumnAsync(d, "DateKey", "Int64", "DateKey", "human");
            var f = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(f, "OrderDateKey", "Int64", "OrderDateKey", "human");
            await engine.CreateColumnAsync(f, "ShipDateKey", "Int64", "ShipDateKey", "human");
            await engine.CreateRelationshipAsync("column:Facts/OrderDateKey", "column:Date/DateKey", null, true, "human");
            if (addInactive)
                await engine.CreateRelationshipAsync("column:Facts/ShipDateKey", "column:Date/DateKey", null, false, "human");
            return engine;
        }

        [Fact]
        public async Task RelInactive_fires_on_an_inactive_relationship_and_is_dormant_with_only_active_ones()
        {
            using (var clean = await DateRolePlayModelAsync(addInactive: false))
                Assert.False(Fires(await clean.AiReadinessScanAsync(), "REL-INACTIVE"));
            using (var withInactive = await DateRolePlayModelAsync(addInactive: true))
                Assert.True(Fires(await withInactive.AiReadinessScanAsync(), "REL-INACTIVE"));
        }

        // ---- REL-HIERARCHY-MISSING ---------------------------------------------------------------------------------

        [Fact]
        public async Task RelHierarchyMissing_fires_on_a_geo_table_without_a_hierarchy_and_clears_when_added()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                var geo = await engine.CreateTableAsync("Geography", "human");
                await engine.CreateColumnAsync(geo, "Country", "String", "Country", "human");
                await engine.CreateColumnAsync(geo, "State", "String", "State", "human");
                await engine.CreateColumnAsync(geo, "City", "String", "City", "human");
                Assert.True(FiresOn(await engine.AiReadinessScanAsync(), "REL-HIERARCHY-MISSING", "Geography"));
                await engine.CreateHierarchyAsync("table:Geography", "Geo Drill", new[] { "Country", "State", "City" }, "agent");
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "REL-HIERARCHY-MISSING"));
            }
        }

        [Fact]
        public async Task RelHierarchyMissing_is_dormant_on_a_table_with_no_drill_columns()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)   // "Sales" has Sales Amount / Customer Name — no date-part or geo columns
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "REL-HIERARCHY-MISSING"));
        }

        // ---- VIS-KEY -----------------------------------------------------------------------------------------------

        [Fact]
        public async Task VisKey_fires_on_a_visible_key_column_and_clears_when_hidden()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                var col = await engine.CreateColumnAsync("table:Sales", "Product Code", "String", "Product Code", "human");
                await engine.SetObjectPropertyAsync(col, "IsKey", "true", "agent");   // mark it a key, still visible
                Assert.True(FiresOn(await engine.AiReadinessScanAsync(), "VIS-KEY", "Product Code"));
                await engine.SetColumnMetadataAsync(col, true, null, null, null, "agent");   // hide it
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "VIS-KEY"));
            }
        }

        // ---- VIS-TABLE-ALL-HIDDEN ----------------------------------------------------------------------------------

        [Fact]
        public async Task VisTableAllHidden_fires_when_every_column_is_hidden_and_clears_when_one_is_shown()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                var t = await engine.CreateTableAsync("Bridge", "human");
                var a = await engine.CreateColumnAsync(t, "A", "Int64", "A", "human");
                var b = await engine.CreateColumnAsync(t, "B", "Int64", "B", "human");
                await engine.SetColumnMetadataAsync(a, true, null, null, null, "agent");
                await engine.SetColumnMetadataAsync(b, true, null, null, null, "agent");
                Assert.True(FiresOn(await engine.AiReadinessScanAsync(), "VIS-TABLE-ALL-HIDDEN", "Bridge"));
                await engine.SetColumnMetadataAsync(a, false, null, null, null, "agent");   // show one column
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "VIS-TABLE-ALL-HIDDEN"));
            }
        }

        // ---- MODE-DIRECTLAKE-QNA + CFG-PREP-MODE (advisory: Applicable=0, never scores) ----------------------------

        [Fact]
        public async Task DirectLake_advisories_fire_on_a_direct_lake_partition_and_are_dormant_on_import()
        {
            var (import, _) = await BaseModelAsync();
            using (import)
            {
                var c = await import.AiReadinessScanAsync();
                Assert.False(Fires(c, "MODE-DIRECTLAKE-QNA"));
                Assert.False(Fires(c, "CFG-PREP-MODE"));
            }

            var sessions = new SessionManager();
            using var dl = new LocalEngine(sessions, new Fake(true));
            await dl.CreateModelAsync("DL", 1604);
            await dl.CreateDirectLakeTableAsync("LakeSales", "sales", null, null, "human");
            var scan = await dl.AiReadinessScanAsync();
            Assert.True(Fires(scan, "MODE-DIRECTLAKE-QNA"));
            Assert.True(Fires(scan, "CFG-PREP-MODE"));
            // Advisory contract: the finding surfaces but carries a non-scoring affordance (None advisory).
            Assert.Equal(nameof(FixKind.None), scan.Findings.First(f => f.RuleId == "MODE-DIRECTLAKE-QNA").Fix);
        }

        // ---- DESC-ECHO-OBJECT --------------------------------------------------------------------------------------

        [Fact]
        public async Task DescEchoObject_fires_on_an_echoed_column_description_and_clears_on_a_real_one()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                const string colRef = "column:Sales/Customer Name";
                await engine.SetDescriptionAsync(colRef, "Customer Name", "agent");   // echoes the name
                Assert.True(Fires(await engine.AiReadinessScanAsync(), "DESC-ECHO-OBJECT"));
                await engine.SetDescriptionAsync(colRef, "The full display name of the customer who placed the order.", "agent");
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "DESC-ECHO-OBJECT"));
            }
        }

        [Fact]
        public async Task DescEchoObject_does_not_double_count_a_placeholder_description()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                // A placeholder is owned by DESC-PLACEHOLDER — DESC-ECHO-OBJECT must NOT also fire on it.
                await engine.SetDescriptionAsync("column:Sales/Customer Name", "TODO: write this description later.", "agent");
                var c = await engine.AiReadinessScanAsync();
                Assert.True(Fires(c, "DESC-PLACEHOLDER"));
                Assert.False(Fires(c, "DESC-ECHO-OBJECT"));
            }
        }

        // ---- Anti-inflation guard: the presence-design rules stay dormant on the clean base model ------------------

        [Fact]
        public async Task Batch1_presence_rules_are_dormant_on_a_clean_model()
        {
            var (engine, _) = await BaseModelAsync();
            using (engine)
            {
                var c = await engine.AiReadinessScanAsync();
                foreach (var id in new[] { "NAME-GENERIC", "FMT-DATATYPE", "CAT-URL", "REL-INACTIVE",
                                           "REL-HIERARCHY-MISSING", "VIS-TABLE-ALL-HIDDEN", "DESC-ECHO-OBJECT" })
                    Assert.False(Fires(c, id));
                // Every category still scores in-range (the Weights KeyNotFound hazard + no NaN from a new rule).
                Assert.All(c.Categories, x => Assert.InRange(x.Score, 0, 100));
            }
        }
    }
}
