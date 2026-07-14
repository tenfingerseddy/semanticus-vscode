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
    /// Catalog batch 3: deterministic missing rows first, then deterministic partial upgrades. Every touched rule
    /// proves a violation, a clean state and a genuinely dormant population. The tests drive the shared LocalEngine
    /// path so the scorecard sees the same model state as both public doors.
    /// </summary>
    public sealed class ReadinessCatalogBatch3Tests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static async Task<(LocalEngine Engine, SessionManager Sessions)> BaseModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("CatalogBatch3", 1604);
            var table = await engine.CreateTableAsync("Customers", "human");
            await engine.CreateColumnAsync(table, "Customer Name", "String", "Customer Name", "human");
            await engine.CreateColumnAsync(table, "Account Name", "String", "Account Name", "human");
            return (engine, sessions);
        }

        private static bool Fires(Scorecard score, string id) => score.Findings.Any(f => f.RuleId == id);
        private static int FindingCount(Scorecard score, string id) => score.Findings.Count(f => f.RuleId == id);

        private static Task<RuleEvaluation> EvaluateAsync(SessionManager sessions, string id) =>
            sessions.Require().ReadAsync(m => ReadinessRuleSet.Default().Single(r => r.Id == id).Evaluate(m));

        [Fact]
        public async Task SynCollide_checks_only_primary_authored_terms_and_is_dormant_without_them()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Equal(0, (await EvaluateAsync(sessions, "SYN-COLLIDE")).Applicable);

                await engine.EnableQnaAsync(null, "agent");
                await engine.SetSynonymsAsync("column:Customers/Customer Name", new[] { "client" }, null, "agent");
                await engine.SetSynonymsAsync("column:Customers/Account Name", new[] { "client" }, null, "agent");

                var collision = await engine.AiReadinessScanAsync();
                Assert.Equal(2, FindingCount(collision, "SYN-COLLIDE"));
                Assert.All(collision.Findings.Where(f => f.RuleId == "SYN-COLLIDE"),
                    f => Assert.Contains("Primary authored synonym 'client'", f.Message));

                await engine.SetSynonymsAsync("column:Customers/Account Name", new[] { "account" }, null, "agent");
                var clean = await engine.AiReadinessScanAsync();
                Assert.False(Fires(clean, "SYN-COLLIDE"));
                Assert.Equal(2, (await EvaluateAsync(sessions, "SYN-COLLIDE")).Applicable);
            }
        }

        [Fact]
        public async Task SynLsdlXml_owns_legacy_xml_while_json_and_no_schema_are_dormant()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Equal(0, (await EvaluateAsync(sessions, "SYN-LSDL-XML")).Applicable);

                await sessions.Require().MutateAsync("human", "seed XML linguistic schema", m =>
                {
                    m.Culture = "en-US";
                    var culture = m.Cultures.Contains("en-US") ? m.Cultures["en-US"] : m.AddTranslation("en-US");
                    culture.Content = "<LinguisticSchema />";
                });
                var xml = await engine.AiReadinessScanAsync();
                Assert.True(Fires(xml, "SYN-LSDL-XML"));
                Assert.False(Fires(xml, "SYN-SCHEMA"));
                Assert.Equal(1, (await EvaluateAsync(sessions, "SYN-LSDL-XML")).Applicable);

                await sessions.Require().MutateAsync("human", "upgrade linguistic schema", m =>
                    m.Cultures["en-US"].Content = "{\"Version\":\"1.0.0\",\"Language\":\"en-US\",\"Entities\":{}}");
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "SYN-LSDL-XML"));
                Assert.Equal(0, (await EvaluateAsync(sessions, "SYN-LSDL-XML")).Applicable);
            }
        }

        [Fact]
        public async Task FmtColumn_checks_visible_business_numbers_and_dates_then_clears_with_formats()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Equal(0, (await EvaluateAsync(sessions, "FMT-COLUMN")).Applicable);

                var amount = await engine.CreateColumnAsync("table:Customers", "Lifetime Amount", "Decimal", "Lifetime Amount", "human");
                var joined = await engine.CreateColumnAsync("table:Customers", "Joined Date", "DateTime", "Joined Date", "human");
                var missing = await engine.AiReadinessScanAsync();
                Assert.Equal(2, FindingCount(missing, "FMT-COLUMN"));

                await engine.SetObjectPropertyAsync(amount, "FormatString", "$#,0.00", "agent");
                await engine.SetObjectPropertyAsync(joined, "FormatString", "yyyy-MM-dd", "agent");
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "FMT-COLUMN"));
                Assert.Equal(2, (await EvaluateAsync(sessions, "FMT-COLUMN")).Applicable);
            }
        }

        [Fact]
        public async Task FmtMeasure_accepts_dynamic_format_expressions_and_is_dormant_without_measures()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Equal(0, (await EvaluateAsync(sessions, "FMT-MEASURE")).Applicable);

                await engine.CreateMeasureAsync("table:Customers", "Customer Value", "1", "human");
                Assert.True(Fires(await engine.AiReadinessScanAsync(), "FMT-MEASURE"));

                await sessions.Require().MutateAsync("human", "set dynamic format", m =>
                    m.AllMeasures.Single(x => x.Name == "Customer Value").FormatStringExpression = "\"$#,0\"");
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "FMT-MEASURE"));
                Assert.Equal(1, (await EvaluateAsync(sessions, "FMT-MEASURE")).Applicable);
            }
        }

        [Fact]
        public async Task CatGeo_covers_coordinates_address_and_place_without_word_fragment_false_positives()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Equal(0, (await EvaluateAsync(sessions, "CAT-GEO")).Applicable);

                await engine.CreateColumnAsync("table:Customers", "Customer Latitude", "Decimal", "Customer Latitude", "human");
                await engine.CreateColumnAsync("table:Customers", "Customer Longitude", "Decimal", "Customer Longitude", "human");
                await engine.CreateColumnAsync("table:Customers", "Street Address", "String", "Street Address", "human");
                await engine.CreateColumnAsync("table:Customers", "BirthPlace", "String", "BirthPlace", "human");
                await engine.CreateColumnAsync("table:Customers", "Addressable Market", "String", "Addressable Market", "human");
                await engine.CreateColumnAsync("table:Customers", "Workplace Satisfaction", "String", "Workplace Satisfaction", "human");
                await engine.CreateColumnAsync("table:Customers", "Email Address", "String", "Email Address", "human");
                await engine.CreateColumnAsync("table:Customers", "Service Capacity", "String", "Service Capacity", "human");
                await engine.CreateColumnAsync("table:Customers", "Statement Date", "String", "Statement Date", "human");

                var missing = await engine.AiReadinessScanAsync();
                Assert.Equal(4, FindingCount(missing, "CAT-GEO"));
                foreach (var finding in missing.Findings.Where(f => f.RuleId == "CAT-GEO"))
                    Assert.True((await engine.ApplyFixAsync("CAT-GEO", finding.ObjectRef, "agent")).Changed);

                Assert.False(Fires(await engine.AiReadinessScanAsync(), "CAT-GEO"));
                Assert.Equal(4, (await EvaluateAsync(sessions, "CAT-GEO")).Applicable);
                var categories = await sessions.Require().ReadAsync(m => m.AllColumns.ToDictionary(c => c.Name, c => c.DataCategory));
                Assert.Equal("Latitude", categories["Customer Latitude"]);
                Assert.Equal("Longitude", categories["Customer Longitude"]);
                Assert.Equal("Address", categories["Street Address"]);
                Assert.Equal("Place", categories["BirthPlace"]);
                Assert.True(string.IsNullOrEmpty(categories["Addressable Market"]));
                Assert.True(string.IsNullOrEmpty(categories["Workplace Satisfaction"]));
                Assert.True(string.IsNullOrEmpty(categories["Email Address"]));
                Assert.True(string.IsNullOrEmpty(categories["Service Capacity"]));
                Assert.True(string.IsNullOrEmpty(categories["Statement Date"]));
            }
        }

        [Fact]
        public async Task CatUrl_covers_barcode_and_is_dormant_for_non_category_business_phrases()
        {
            var (engine, sessions) = await BaseModelAsync();
            using (engine)
            {
                Assert.Equal(0, (await EvaluateAsync(sessions, "CAT-URL")).Applicable);

                var barcode = await engine.CreateColumnAsync("table:Customers", "Product Barcode", "String", "Product Barcode", "human");
                await engine.CreateColumnAsync("table:Customers", "Barcode Adoption", "String", "Barcode Adoption", "human");
                var missing = await engine.AiReadinessScanAsync();
                Assert.Equal(1, FindingCount(missing, "CAT-URL"));
                Assert.Contains("set Barcode", missing.Findings.Single(f => f.RuleId == "CAT-URL").Message);

                await engine.SetColumnMetadataAsync(barcode, null, null, "Barcode", null, "agent");
                Assert.False(Fires(await engine.AiReadinessScanAsync(), "CAT-URL"));
                Assert.Equal(1, (await EvaluateAsync(sessions, "CAT-URL")).Applicable);
            }
        }
    }
}
