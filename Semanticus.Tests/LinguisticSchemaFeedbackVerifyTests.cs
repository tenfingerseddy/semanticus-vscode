using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier tests for m11/feedback-lsdl (Graphicus feedback items 2, 3 and 5). Each one is left FAILING on the
    /// lane and names one defect. The lane was proved against the Graphicus generator's schema, where every name
    /// term is the object's name in lower case with State Generated. Power BI Desktop does not always write that:
    /// the entity shapes below are copied from Power BI Desktop output in public PBIP samples (Microsoft's
    /// Regional Sales sample, the FabCon 2024 Product Sales workshop model, a 2025 Copilot demo model):
    /// (a) a plural name's name term is its singular ('Customers' carries "customer", 'Sales' carries "sale");
    /// (b) a camelCase name carries a lower-case split term first, then the name as written ("CustomerId",
    ///     Type Noun, Weight 0.99);
    /// (c) once the author has edited synonyms in Desktop, the name term is written with no State at all
    ///     ({"sales amount":{}}), and added synonyms carry only LastModified.
    /// Two tests cover the cross-rule invariant beyond the one case the lane checks: NAME-TECH-PREFIX's advice, and
    /// DIM-PRIMARY-NAME's advice followed by SYN-SCHEMA's (the builder's open question).
    /// </summary>
    public sealed class LinguisticSchemaFeedbackVerifyTests : IClassFixture<TemplateCopies>
    {
        private readonly TemplateCopies _copies;
        public LinguisticSchemaFeedbackVerifyTests(TemplateCopies copies) => _copies = copies;

        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        // ---- fixture helpers --------------------------------------------------------------------------------

        /// <summary>An entity in Power BI Desktop's own shape: Definition.Binding, entity-level State Generated.
        /// Each term is (word, its property object as JSON).</summary>
        private static string PbiEnt(string key, string table, string prop, params (string Term, string Props)[] terms)
        {
            var binding = prop == null
                ? $"{{\"ConceptualEntity\":\"{table}\"}}"
                : $"{{\"ConceptualEntity\":\"{table}\",\"ConceptualProperty\":\"{prop}\"}}";
            var t = terms.Select(x => $"{{\"{x.Term}\":{x.Props}}}");
            return $"\"{key}\":{{\"Definition\":{{\"Binding\":{binding}}},\"State\":\"Generated\",\"Terms\":[{string.Join(",", t)}]}}";
        }

        private static string Lsdl(params string[] entities) =>
            "{\"Version\":\"1.0.0\",\"Language\":\"en-US\",\"DynamicImprovement\":\"HighConfidence\",\"Entities\":{"
            + string.Join(",", entities) + "}}";

        private const string Gen = "{\"State\":\"Generated\"}";

        private static Task InjectAsync(SessionManager sessions, string content) =>
            sessions.Require().MutateAsync("human", "seed linguistic schema", m =>
            {
                var cult = m.Cultures.Contains("en-US") ? m.Cultures["en-US"] : m.AddTranslation("en-US");
                cult.Content = content;
            });

        private static async Task<JObject> EntitiesAsync(SessionManager sessions) =>
            (JObject)JObject.Parse(await sessions.Require().ReadAsync(m => m.Cultures["en-US"].Content))["Entities"];

        private static JObject BoundTo(JObject entities, string table, string prop) =>
            entities.Properties().Select(p => (JObject)p.Value).Single(e =>
            {
                var b = (JObject)(e["Definition"]?["Binding"] ?? e["Binding"]);
                return (string)b["ConceptualEntity"] == table && (string)b["ConceptualProperty"] == prop;
            });

        private static List<(string Term, string State, JObject Props)> Terms(JObject entity) =>
            ((JArray)entity["Terms"]).OfType<JObject>().SelectMany(o => o.Properties())
                .Select(p => (p.Name, (string)p.Value["State"] ?? "Authored", (JObject)p.Value)).ToList();

        private static bool Live(string state) => state != "Deleted" && state != "Suggested";

        private static RuleEvaluation Eval(SessionManager sessions, string id)
        {
            PrepForAiReader.Invalidate(sessions.Current.Model);
            return ReadinessRuleSet.Default().Single(r => r.Id == id).Evaluate(sessions.Current.Model);
        }

        /// <summary>A 'Customers' dimension: visible table, visible 'Customer Name', 'Customer Email' and 'CustomerId',
        /// hidden 'Customer Key', related from a small 'Orders' fact.</summary>
        private async Task<(LocalEngine Engine, SessionManager Sessions)> CustomersAsync()
        {
            var sessions = new SessionManager();
            return (await _copies.OpenFromFixtureAsync("customers", BuildCustomersAsync, new Fake(), sessions), sessions);
        }

        private static async Task BuildCustomersAsync(DecisionFixture f)
        {
            var engine = f.Engine;
            var sessions = f.Sessions;
            await engine.CreateModelAsync("Shop", 1604);
            var orders = await engine.CreateTableAsync("Orders", "human");
            var fk = await engine.CreateColumnAsync(orders, "Customer Key", "Int64", "Customer Key", "human");
            await engine.CreateMeasureAsync(orders, "Order Count", "1", "human");
            var customers = await engine.CreateTableAsync("Customers", "human");
            var pk = await engine.CreateColumnAsync(customers, "Customer Key", "Int64", "Customer Key", "human");
            await engine.CreateColumnAsync(customers, "Customer Name", "String", "Customer Name", "human");
            await engine.CreateColumnAsync(customers, "Customer Email", "String", "Customer Email", "human");
            await engine.CreateColumnAsync(customers, "CustomerId", "String", "CustomerId", "human");
            await engine.CreateRelationshipAsync(fk, pk, null, true, "human");
            await sessions.Require().MutateAsync("human", "hide keys", m =>
            {
                m.Tables["Orders"].Columns["Customer Key"].IsHidden = true;
                m.Tables["Customers"].Columns["Customer Key"].IsHidden = true;
            });
        }

        /// <summary>The Customers schema as Power BI Desktop writes it: singular name terms for the plural table,
        /// the split-plus-as-written pair for the camelCase column, a thesaurus suggestion.</summary>
        private static string CustomersLsdl() => Lsdl(
            PbiEnt("order", "Orders", null, ("order", Gen)),
            PbiEnt("order.order_count", "Orders", "Order Count", ("order count", Gen)),
            PbiEnt("customer", "Customers", null, ("customer", Gen),
                ("client", "{\"Type\":\"Noun\",\"State\":\"Suggested\",\"Source\":{\"Agent\":\"Thesaurus\"},\"Weight\":0.736}")),
            PbiEnt("customer.customer_name", "Customers", "Customer Name", ("customer name", Gen), ("name", "{\"State\":\"Generated\",\"Weight\":0.97}")),
            PbiEnt("customer.customer_id", "Customers", "CustomerId", ("customer id", Gen), ("id", "{\"State\":\"Generated\",\"Weight\":0.97}"),
                ("CustomerId", "{\"State\":\"Generated\",\"Type\":\"Noun\",\"Weight\":0.99}")));

        // ---- item 5, the cross-rule invariant beyond DIM-PRIMARY-NAME ---------------------------------------

        [Fact]
        public async Task Verify_tech_prefix_advice_never_leads_to_a_high_name_collision()
        {
            // A star-schema dimension 'Dim Product' labels its rows with a visible 'Product' column. NAME-TECH-PREFIX
            // says drop the prefix; with the linguistic schema engaged, the table then reads 'Product', the same term
            // as its own label column, and DAC-QNA-NAME-COLLISIONS flags that High. This is the DIM-PRIMARY-NAME
            // contradiction from item 5 in another rule, and the lane's "no rule recommends a change another rule
            // flags High" test only enumerates DIM-PRIMARY-NAME (and runs zero iterations once that rule is quiet).
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Shop", 1604);
                var sales = await engine.CreateTableAsync("Sales", "human");
                var fk = await engine.CreateColumnAsync(sales, "Product Key", "Int64", "Product Key", "human");
                await engine.CreateMeasureAsync(sales, "Net Sales", "1", "human");
                var dim = await engine.CreateTableAsync("Dim Product", "human");
                var pk = await engine.CreateColumnAsync(dim, "Product Key", "Int64", "Product Key", "human");
                await engine.CreateColumnAsync(dim, "Product", "String", "Product", "human");
                await engine.CreateRelationshipAsync(fk, pk, null, true, "human");
                await sessions.Require().MutateAsync("human", "hide keys", m =>
                {
                    m.Tables["Sales"].Columns["Product Key"].IsHidden = true;
                    m.Tables["Dim Product"].Columns["Product Key"].IsHidden = true;
                });
                await engine.CreateLinguisticSchemaAsync(null, "agent");
                Assert.Empty(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);

                foreach (var f in Eval(sessions, "NAME-TECH-PREFIX").Violations.ToList())
                {
                    var name = f.ObjectRef.Substring("table:".Length);
                    var bare = name.Substring(name.IndexOf(' ') + 1);   // the advice: drop the prefix
                    await engine.RenameObjectAsync(f.ObjectRef, bare, "agent");
                    var collisions = Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations;
                    Assert.DoesNotContain(collisions, v => v.Message.Contains($"table '{bare}'", StringComparison.Ordinal));
                    await engine.UndoAsync("agent");
                }
            }
        }

        [Fact]
        public async Task Verify_dimension_name_advice_and_schema_advice_do_not_combine_into_a_high_collision()
        {
            // The builder's open question, reproduced. On a 1465+ model with no linguistic schema DIM-PRIMARY-NAME still
            // advises renaming 'Product Name' to 'Product', and SYN-SCHEMA (High) advises creating the schema. Following
            // both, in two steps, lands in the High collision the lane set out to prevent: the plan guard only covers
            // the case where both land in one batch. Any resolution passes this (quiet on 1465+, a non-rename fix, or a
            // collision rule that accepts a table's own label column).
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Shop", 1604);
                var sales = await engine.CreateTableAsync("Sales", "human");
                var fk = await engine.CreateColumnAsync(sales, "Product Key", "Int64", "Product Key", "human");
                await engine.CreateMeasureAsync(sales, "Net Sales", "1", "human");
                var product = await engine.CreateTableAsync("Product", "human");
                var pk = await engine.CreateColumnAsync(product, "Product Key", "Int64", "Product Key", "human");
                await engine.CreateColumnAsync(product, "Product Name", "String", "Product Name", "human");
                await engine.CreateRelationshipAsync(fk, pk, null, true, "human");
                await sessions.Require().MutateAsync("human", "hide keys", m =>
                {
                    m.Tables["Sales"].Columns["Product Key"].IsHidden = true;
                    m.Tables["Product"].Columns["Product Key"].IsHidden = true;
                });
                Assert.NotEmpty(Eval(sessions, "SYN-SCHEMA").Violations);

                foreach (var f in Eval(sessions, "DIM-PRIMARY-NAME").Violations.ToList())
                {
                    var table = f.ObjectRef.Substring("column:".Length).Split('/')[0];
                    await engine.RenameObjectAsync(f.ObjectRef, table, "agent");   // DIM-PRIMARY-NAME's advice
                    await engine.CreateLinguisticSchemaAsync(null, "agent");        // SYN-SCHEMA's advice
                    Assert.DoesNotContain(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations,
                        v => v.Message.Contains($"column '{table}'[{table}]", StringComparison.Ordinal));
                    await engine.UndoAsync("agent");
                    await engine.UndoAsync("agent");
                }
            }
        }

        // ---- item 2 on Power BI Desktop's name terms ---------------------------------------------------------

        [Fact]
        public async Task Verify_rename_regenerates_a_name_term_power_bi_wrote_as_the_singular()
        {
            // Power BI Desktop writes the name term of 'Customers' as "customer". The lane matches the old term only
            // when it normalises exactly to the old name, so after this rename the entity still carries "customer"
            // as its live Generated name term: the stale word item 2 is about, on the shape Desktop writes.
            var (engine, sessions) = await CustomersAsync();
            using (engine)
            {
                await InjectAsync(sessions, CustomersLsdl());
                await engine.RenameObjectAsync("table:Customers", "Accounts", "agent");

                var terms = Terms(BoundTo(await EntitiesAsync(sessions), "Accounts", null));
                Assert.DoesNotContain(terms, t => t.State == "Generated" && QnaTerms.NormalizeFolded(t.Term) == QnaTerms.NormalizeFolded("Customers"));
                Assert.Contains(terms, t => t.State == "Generated" && QnaTerms.NormalizeFolded(t.Term) == QnaTerms.NormalizeFolded("Accounts"));
            }
        }

        [Fact]
        public async Task Verify_rename_keeps_the_primary_name_term_of_a_camel_case_name_in_place()
        {
            // For 'CustomerId' Power BI Desktop writes "customer id" (the name term, first, no Weight), then "id", then
            // "CustomerId" (Type Noun, Weight 0.99). The lane rewrites the first to "client number", then the third to
            // "Client Number", sees the first as its twin and removes it: the entity ends with no plain name term and
            // "id" in first place. RegenerateNameTerm's own contract is "in place (same array slot, same Type/Weight)".
            var (engine, sessions) = await CustomersAsync();
            using (engine)
            {
                await InjectAsync(sessions, CustomersLsdl());
                await engine.RenameObjectAsync("column:Customers/CustomerId", "Client Number", "agent");

                var first = Terms(BoundTo(await EntitiesAsync(sessions), "Customers", "Client Number"))[0];
                Assert.Equal("client number", first.Term);
                Assert.True(JToken.DeepEquals(JObject.Parse(Gen), first.Props), first.Props.ToString(Newtonsoft.Json.Formatting.None));
            }
        }

        [Fact]
        public async Task Verify_rename_does_not_leave_a_stateless_name_term_that_the_scan_calls_fixed()
        {
            // After an author edits synonyms in Power BI Desktop, Desktop writes the name term with no State
            // ({"margin %":{}}; public samples from 2021 and 2025 carry exactly this). A missing State reads as
            // Authored, so the lane neither regenerates it on rename nor lets DAC-QNA-NAME-COLLISIONS read it. The
            // item 2 complaint then reproduces as filed: the old word still collides with 'Margin' in the schema,
            // and the scan reports no collision.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Shop", 1604);
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateMeasureAsync(sales, "Margin", "1", "human");
                await engine.CreateMeasureAsync(sales, "Margin %", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("sale", "Sales", null, ("sale", "{}")),
                    PbiEnt("sale.margin", "Sales", "Margin", ("margin", "{}")),
                    PbiEnt("sale.margin_", "Sales", "Margin %", ("margin %", "{}"), ("gross margin rate", "{\"LastModified\":\"2025-08-26T10:57:21.382Z\"}"))));
                Assert.NotEmpty(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);   // the collision item 2 set out to fix

                await engine.RenameObjectAsync("measure:Sales/Margin %", "Margin Percent", "agent");

                var stale = Terms(BoundTo(await EntitiesAsync(sessions), "Sales", "Margin Percent"))
                    .Where(t => Live(t.State) && QnaTerms.Normalize(t.Term) == QnaTerms.Normalize("Margin %")).ToList();
                var flagged = Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations
                    .Any(v => v.Message.Contains("[Margin Percent]", StringComparison.Ordinal));
                Assert.True(stale.Count == 0 || flagged,
                    $"the renamed entity still carries the old name term '{stale.FirstOrDefault().Term}' and the scan reports no collision");
            }
        }

        [Fact]
        public async Task Verify_one_collision_is_reported_once_when_power_bi_name_terms_are_singular()
        {
            // Table 'Sales' and its visible measure 'Sales': one collision. Power BI Desktop writes both name terms as
            // "sale", so each object now enters under "sales" (its name) AND "sale" (its term), and the exact tier
            // reports the same pair twice. On the base this model has one finding.
            // Edited by the round 3 builder for Kane's ruling of 27 Sep: a table beside its own field is no longer a
            // clash, so the pair is two fields ('Sales'[Sales] and 'Returns'[Sales]) carrying the same singular terms;
            // the defect the test pins (one pair reported twice) is unchanged.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Shop", 1604);
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateMeasureAsync(sales, "Sales", "1", "human");
                await engine.CreateMeasureAsync(sales, "Units", "1", "human");
                var returns = await engine.CreateTableAsync("Returns", "human");
                await engine.CreateColumnAsync(returns, "Sales", "Decimal", "Sales", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("sale", "Sales", null, ("sale", Gen)),
                    PbiEnt("sale.sale", "Sales", "Sales", ("sale", Gen)),
                    PbiEnt("sale.unit", "Sales", "Units", ("unit", Gen)),
                    PbiEnt("return", "Returns", null, ("return", Gen)),
                    PbiEnt("return.sale", "Returns", "Sales", ("sale", Gen))));

                var ev = Eval(sessions, "DAC-QNA-NAME-COLLISIONS");
                Assert.True(ev.Violations.Count == 1, string.Join(" | ", ev.Violations.Select(v => v.Message)));
            }
        }

        [Fact]
        public async Task Verify_a_term_weight_that_is_not_a_number_does_not_fail_the_scan()
        {
            // Valid JSON the schema readers must survive (the contract TryReadBinding states: a non-object where an
            // object is expected must leave a rule dormant, never fail the scan). GeneratedNameTerms calls
            // JsonElement.TryGetDouble on Weight, which throws InvalidOperationException when Weight is null or a
            // string; the JsonException catch does not cover it, and the analyzer has no per-rule catch, so the whole
            // readiness scan throws. The base never read term weights.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Shop", 1604);
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateMeasureAsync(sales, "Net Sales", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("sale", "Sales", null, ("sale", "{\"State\":\"Generated\",\"Weight\":null}")),
                    PbiEnt("sale.net_sales", "Sales", "Net Sales", ("net sales", "{\"State\":\"Generated\",\"Weight\":\"0.9\"}"))));

                var ex = Record.Exception(() => new ReadinessAnalyzer().Analyze(sessions.Current.Model));
                Assert.Null(ex);
            }
        }

        // ---- item 3 on Power BI Desktop's shapes -------------------------------------------------------------

        [Fact]
        public async Task Verify_set_synonyms_keeps_a_name_term_power_bi_wrote_as_the_singular()
        {
            // The lane protects the Generated term that normalises exactly to the name. Desktop's "customer" on
            // 'Customers' is not protected: set_synonyms tombstones it and inserts a new "customers" name term.
            var (engine, sessions) = await CustomersAsync();
            using (engine)
            {
                await InjectAsync(sessions, CustomersLsdl());
                await engine.SetSynonymsAsync("table:Customers", new[] { "clients" }, null, "agent");

                var terms = Terms(BoundTo(await EntitiesAsync(sessions), "Customers", null));
                Assert.Contains(("customer", "Generated"), terms.Select(t => (t.Term, t.State)));
                Assert.Single(terms, t => t.State == "Generated" && QnaTerms.NormalizeFolded(t.Term) == QnaTerms.NormalizeFolded("Customers"));
            }
        }

        [Fact]
        public async Task Verify_set_synonyms_never_writes_the_same_term_twice_in_one_entity()
        {
            // Desktop's post-edit shape: the name term has no State. The lane governs it like any authored word, so a
            // synonym list that leaves the name out tombstones it, and then the "every entity keeps its name term"
            // step inserts the same word again as Generated. The entity ends holding "sales amount" twice (Generated
            // and Deleted). The base wrote it once.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Shop", 1604);
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateMeasureAsync(sales, "Sales Amount", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("sale", "Sales", null, ("sale", "{}")),
                    PbiEnt("sale.sales_amount", "Sales", "Sales Amount", ("sales amount", "{}"), ("sales", "{\"Weight\":0.97}"),
                        ("takings", "{\"LastModified\":\"2025-08-26T10:57:21.382Z\"}"))));

                await engine.SetSynonymsAsync("measure:Sales/Sales Amount", new[] { "revenue" }, null, "agent");

                var words = Terms(BoundTo(await EntitiesAsync(sessions), "Sales", "Sales Amount")).Select(t => t.Term.ToLowerInvariant()).ToList();
                Assert.True(words.Count == words.Distinct().Count(), "terms: " + string.Join(", ", words));
            }
        }

        [Fact]
        public async Task Verify_set_synonyms_on_a_camel_case_name_writes_each_term_once()
        {
            // The real raw Northstar fixture names its columns in camelCase (ProductName, ListPrice). CreateEntity writes
            // the name term as the name in lower case ("productname"), but SetTerms protects only a Generated term that
            // camel-splits to the name ("product name"). So the very first set_synonyms tombstones the name term it just
            // wrote and inserts it again, and every later call adds one more tombstone: "productname" x2 after one
            // call, x3 after two. Desktop would write "product name". The base wrote each word once.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Shop", 1604);
                var product = await engine.CreateTableAsync("Product", "human");
                await engine.CreateColumnAsync(product, "ProductName", "String", "ProductName", "human");
                await engine.CreateLinguisticSchemaAsync(null, "agent");

                await engine.SetSynonymsAsync("column:Product/ProductName", new[] { "item" }, null, "agent");
                await engine.SetSynonymsAsync("column:Product/ProductName", new[] { "sku name" }, null, "agent");

                var words = Terms(BoundTo(await EntitiesAsync(sessions), "Product", "ProductName")).Select(t => t.Term.ToLowerInvariant()).ToList();
                Assert.True(words.Count == words.Distinct().Count(), "terms: " + string.Join(", ", words));
            }
        }

        [Fact]
        public async Task Verify_set_synonyms_keys_a_new_child_entity_under_its_tables_own_key()
        {
            // Item 3 asks for the siblings' key style. Desktop keys a plural table by its singular ("customer") and every
            // child under that prefix ("customer.customer_name"). None of the lane's three sluggers reproduces it, so the
            // new entity lands at "customers.customer_email", the one key in the model with a different prefix.
            var (engine, sessions) = await CustomersAsync();
            using (engine)
            {
                await InjectAsync(sessions, CustomersLsdl());
                await engine.SetSynonymsAsync("column:Customers/Customer Email", new[] { "email" }, null, "agent");

                var keys = (await EntitiesAsync(sessions)).Properties().Select(p => p.Name).ToList();
                Assert.Contains("customer.customer_email", keys);
            }
        }
    }
}
