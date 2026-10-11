using System;
using System.Collections.Generic;
using System.IO;
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
    /// Round 3 of m11/feedback-lsdl, against the verifier's round 2 report (N1 to N7). Each fix gets a test that fails
    /// without it: the writer and the reader of a name term agree by construction (N1); a term with no State is the
    /// author's, never an old name (N3); a collision found only through an old name term does not claim that every
    /// synonym write fails (N6); Power BI Desktop's lemmas of irregular plurals read as the name (N2, N4); and each of
    /// round 2's root fixes (R2 to R5) has a guard of its own (N7). Shapes are copied from public Desktop-saved schemas.
    /// </summary>
    public sealed class LinguisticSchemaFeedbackR3Tests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        // ---- fixture helpers (the verifier's shapes) --------------------------------------------------------

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
        private const string Derived = "{\"State\":\"Generated\",\"Weight\":0.97}";

        private static Task InjectAsync(SessionManager sessions, string content) =>
            sessions.Require().MutateAsync("human", "seed linguistic schema", m =>
            {
                var cult = m.Cultures.Contains("en-US") ? m.Cultures["en-US"] : m.AddTranslation("en-US");
                cult.Content = content;
            });

        private static async Task<string> ContentAsync(SessionManager sessions) =>
            await sessions.Require().ReadAsync(m => m.Cultures["en-US"].Content);

        private static async Task<JObject> EntitiesAsync(SessionManager sessions) =>
            (JObject)JObject.Parse(await ContentAsync(sessions))["Entities"];

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

        private static async Task<(LocalEngine Engine, SessionManager Sessions)> ModelAsync(string name)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync(name, 1604);
            return (engine, sessions);
        }

        // ---- N1: the writer's name term is always one the reader accepts --------------------------------------

        /// <summary>Names across every shape the writer meets: camelCase with short or digit humps, acronyms and their
        /// plurals, '%', digits, spaced, underscored, non-English and single letters.</summary>
        public static readonly string[] Names =
        {
            "SalesYoY", "ProfitMoM", "RevenueQoQ", "NetSalesQ1", "SalesLast12Months", "CustomerAge30Plus", "SalesModelX",
            "NetSalesΔ", "NetSalesYoY", "ProductName", "CustomerId", "CustomerID", "OrderIDs", "KPIs", "SKUs", "URLs",
            "HTTPStatus", "eBay", "iPhoneSales", "Q1Sales", "Top10", "Sales 2024", "Sales YoY", "Margin %", "Margin%",
            "Return Rate %", "% of Total", "YoY %", "Total Children", "TotalChildren", "PeopleSignedUp", "Children At Home",
            "Customers", "Industries", "Statuses", "Movies", "Analyses", "Heroes", "Leaves", "Quizzes", "A", "X1", "ID",
            "customer_name", "DimCustomer", "Dim_Customer", "Sales.Amount", "Días", "Países", "Straße", "Ventas Netas",
            "İl", "ΣΥΝΟΛΟ", "売上", "Net Sales (USD)", "Sales/Unit", "ABC", "aBC", "AbC", "ABc", "Measure 1",
        };

        [Fact]
        public void Every_name_term_the_writer_writes_reads_back_as_the_name()
        {
            // The writer (NameTerm, used by set_synonyms and rename_object) and the reader (ReadsAsName, used to protect
            // a name term and to find a stale one) must agree on every name, or a synonym write tombstones the name term
            // it just wrote and writes it again (N1: 'SalesYoY' written "sales yoy" but read as "sales yo y").
            var failures = Names.Where(n => !QnaTerms.ReadsAsName(QnaTerms.NameTerm(n), n)).ToList();
            Assert.True(failures.Count == 0, "the writer's name term does not read as the name for: "
                + string.Join(", ", failures.Select(n => $"'{n}' -> \"{QnaTerms.NameTerm(n)}\"")));
            // The name as written reads as the name too (a model whose name terms keep case writes it that way).
            Assert.All(Names, n => Assert.True(QnaTerms.ReadsAsName(n, n), n));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Set_synonyms_writes_each_name_term_once_for_every_name_shape()
        {
            // The same agreement through the engine: two synonym writes on each name leave every word once.
            var (engine, sessions) = await ModelAsync("Shapes");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                // One name per linguistic-schema term, so the write guard's own collision check never refuses a write
                // here ('Margin %' and 'Margin%' are the same term to it), and object names are unique regardless of
                // case ('ABC', 'aBC'); the table's own term is taken too.
                var names = Names.Where(n => n.Any(char.IsLetter)).Distinct(StringComparer.OrdinalIgnoreCase).GroupBy(QnaTerms.NormalizeFolded)
                    .Where(g => g.Key != QnaTerms.NormalizeFolded("Sales")).Select(g => g.First()).ToList();
                foreach (var n in names) await engine.CreateMeasureAsync(sales, n, "1", "human");
                await engine.CreateLinguisticSchemaAsync(null, "agent");
                foreach (var n in names)
                {
                    await engine.SetSynonymsAsync($"measure:Sales/{n}", new[] { "alpha" }, null, "agent");
                    await engine.SetSynonymsAsync($"measure:Sales/{n}", new[] { "beta" }, null, "agent");
                    var entities = await EntitiesAsync(sessions);
                    Assert.True(entities.Properties().Count(p => (string)(p.Value["Definition"]?["Binding"] ?? p.Value["Binding"])["ConceptualProperty"] == n) == 1,
                        $"'{n}' has no entity of its own: " + string.Join(", ", entities.Properties().Select(p => p.Name)));
                    var words = Terms(BoundTo(entities, "Sales", n)).Select(t => t.Term.ToLowerInvariant()).ToList();
                    Assert.True(words.Count == words.Distinct().Count(), $"'{n}' terms: " + string.Join(", ", words));
                }
            }
        }

        // ---- N2 and N4: Desktop's lemmas read as the name ---------------------------------------------------

        [Theory]
        // irregular plurals Desktop lemmatises (Contoso, AdventureWorks, public schemas)
        [InlineData("total child", "Total Children")]
        [InlineData("child at home", "Children At Home")]
        [InlineData("child", "children")]
        [InlineData("person signed up", "PeopleSignedUp")]
        [InlineData("person", "People")]
        [InlineData("man", "Men")]
        [InlineData("woman", "Women")]
        [InlineData("criterion", "Criteria")]
        [InlineData("index", "Indices")]
        [InlineData("matrix", "Matrices")]
        [InlineData("mouse", "Mice")]
        [InlineData("salesperson", "Salespeople")]
        [InlineData("grandchild", "Grandchildren")]
        // regular classes the round 2 singular already read
        [InlineData("customer", "Customers")]
        [InlineData("industry", "Industries")]
        [InlineData("address", "Addresses")]
        [InlineData("branch", "Branches")]
        [InlineData("customer id", "CustomerID")]
        [InlineData("kpi", "KPIs")]
        // classes it missed: -uses, -ies to -ie, -oes, -ves, -ses to -sis, -zzes, -e before -es
        [InlineData("status", "Statuses")]
        [InlineData("bonus", "Bonuses")]
        [InlineData("campus", "Campuses")]
        [InlineData("movie", "Movies")]
        [InlineData("cookie", "Cookies")]
        [InlineData("hero", "Heroes")]
        [InlineData("potato", "Potatoes")]
        [InlineData("leaf", "Leaves")]
        [InlineData("shelf", "Shelves")]
        [InlineData("life", "Lives")]
        [InlineData("knife", "Knives")]
        [InlineData("analysis", "Analyses")]
        [InlineData("diagnosis", "Diagnoses")]
        [InlineData("quiz", "Quizzes")]
        [InlineData("cache", "Caches")]
        [InlineData("gas", "Gases")]
        [InlineData("alias", "Aliases")]
        // acronym plurals
        [InlineData("id", "IDs")]
        [InlineData("order id", "Order IDs")]
        [InlineData("url", "URLs")]
        public void A_power_bi_lemma_reads_as_the_name(string term, string name) =>
            Assert.True(QnaTerms.ReadsAsName(term, name), $"\"{term}\" should read as '{name}'");

        [Theory]
        [InlineData("customer", "Customer Name")]
        [InlineData("date", "Calendar Date")]
        [InlineData("child", "Total Children")]
        [InlineData("name", "Customer Name")]
        [InlineData("sales", "Revenue")]
        [InlineData("person", "Personnel")]
        [InlineData("man", "Manager")]
        [InlineData("index", "Indexes Used")]
        [InlineData("length of stay", "Avg Length of Stay")]
        public void A_different_word_does_not_read_as_the_name(string term, string name) =>
            Assert.False(QnaTerms.ReadsAsName(term, name), $"\"{term}\" should not read as '{name}'");

        [Fact]
        public async Task Rename_moves_both_name_terms_of_a_camel_case_irregular_plural()
        {
            // N4's camelCase variant: Desktop writes 'TotalChildren' as "total child" plus the as-written "TotalChildren".
            // A rename that moved only the as-written copy left "total child" live, and the entity then read as named,
            // so the scan could not see the leftover.
            var (engine, sessions) = await ModelAsync("Contoso");
            using (engine)
            {
                var customer = await engine.CreateTableAsync("Customer", "human");
                await engine.CreateColumnAsync(customer, "TotalChildren", "Int64", "TotalChildren", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("customer", "Customer", null, ("customer", Gen)),
                    PbiEnt("customer.total_child", "Customer", "TotalChildren", ("total child", Gen), ("TotalChildren", Gen), ("child", Derived))));

                await engine.RenameObjectAsync("column:Customer/TotalChildren", "KidCount", "agent");

                var terms = Terms(BoundTo(await EntitiesAsync(sessions), "Customer", "KidCount"));
                Assert.DoesNotContain(terms, t => Live(t.State) && (t.Term == "total child" || t.Term == "TotalChildren"));
                Assert.Contains(terms, t => t.State == "Generated" && t.Term == "kid count");
                Assert.Contains(terms, t => t.State == "Generated" && t.Term == "KidCount");
            }
        }

        [Fact]
        public async Task Set_synonyms_leaves_power_bi_derived_words_untouched()
        {
            // Ruling (round 3 brief): "child", "name" and "id" are Desktop's own derived words, not the author's, so a
            // synonym list that leaves them out does not tombstone them. The donor, and every round before this one, did.
            var (engine, sessions) = await ModelAsync("Contoso");
            using (engine)
            {
                var customer = await engine.CreateTableAsync("Customer", "human");
                await engine.CreateColumnAsync(customer, "Customer Name", "String", "Customer Name", "human");
                await engine.CreateColumnAsync(customer, "Total Children", "Int64", "Total Children", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("customer", "Customer", null, ("customer", Gen)),
                    PbiEnt("customer.customer_name", "Customer", "Customer Name", ("customer name", Gen), ("name", Derived)),
                    PbiEnt("customer.total_child", "Customer", "Total Children", ("total child", Gen), ("child", Derived))));

                await engine.SetSynonymsAsync("column:Customer/Customer Name", new[] { "client" }, null, "agent");
                await engine.SetSynonymsAsync("column:Customer/Total Children", new[] { "kids" }, null, "agent");

                var entities = await EntitiesAsync(sessions);
                Assert.Contains(("name", "Generated"), Terms(BoundTo(entities, "Customer", "Customer Name")).Select(t => (t.Term, t.State)));
                Assert.Contains(("child", "Generated"), Terms(BoundTo(entities, "Customer", "Total Children")).Select(t => (t.Term, t.State)));
            }
        }

        // ---- N3: a term with no State is the author's --------------------------------------------------------

        [Fact]
        public async Task Only_an_explicitly_generated_term_becomes_an_old_name()
        {
            // The same entity twice. With Desktop's explicit State Generated, a first term that does not read as the name
            // is a rename's leftover (item 2) and the scan says so. With no State (the documented contract: Authored), the
            // same word is the author's synonym, and nothing may call it an old name or advise clearing it.
            foreach (var (props, stale) in new[] { (Gen, true), ("{}", false), ("{\"State\":\"Authored\"}", false) })
            {
                var (engine, sessions) = await ModelAsync("Health");
                using (engine)
                {
                    var enc = await engine.CreateTableAsync("Encounter", "human");
                    await engine.CreateColumnAsync(enc, "Stay Length", "Int64", "Stay Length", "human");
                    await engine.CreateMeasureAsync(enc, "Avg Length of Stay", "1", "human");
                    await InjectAsync(sessions, Lsdl(
                        PbiEnt("encounter", "Encounter", null, ("encounter", Gen)),
                        PbiEnt("encounter.stay_length", "Encounter", "Stay Length", ("stay length", Gen)),
                        PbiEnt("encounter.avg_length_of_stay", "Encounter", "Avg Length of Stay", ("stay length", props), ("los", props))));

                    var flagged = Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations.Any(v => v.Message.Contains("old name term", StringComparison.Ordinal));
                    Assert.True(flagged == stale, $"terms {props}: flagged {flagged}");
                }
            }
        }

        [Fact]
        public async Task Set_synonyms_on_a_hand_written_schema_writes_terms_like_their_siblings()
        {
            // A hand-written schema writes every synonym with no State. A new term matches its siblings ({}), rather than
            // the {"State":"Authored"} the round 2 reader chose because it took those siblings for generated terms.
            var (engine, sessions) = await ModelAsync("Health");
            using (engine)
            {
                var enc = await engine.CreateTableAsync("Encounter", "human");
                await engine.CreateMeasureAsync(enc, "Avg Length of Stay", "1", "human");
                await engine.CreateMeasureAsync(enc, "Visit Count", "1", "human");
                await InjectAsync(sessions, "{\"Version\":\"1.0.0\",\"Language\":\"en-US\",\"Entities\":{"
                    + "\"Encounter.Avg Length of Stay\":{\"Binding\":{\"ConceptualEntity\":\"Encounter\",\"ConceptualProperty\":\"Avg Length of Stay\"},\"Terms\":[{\"los\":{}},{\"average stay\":{}}]},"
                    + "\"Encounter.Visit Count\":{\"Binding\":{\"ConceptualEntity\":\"Encounter\",\"ConceptualProperty\":\"Visit Count\"},\"Terms\":[{\"visits\":{}}]}}}");

                await engine.SetSynonymsAsync("measure:Encounter/Visit Count", new[] { "visits", "encounters" }, null, "agent");

                var added = Terms(BoundTo(await EntitiesAsync(sessions), "Encounter", "Visit Count")).Single(t => t.Term == "encounters");
                Assert.False(added.Props.ContainsKey("State"), added.Props.ToString());
                // The author's other words were not touched by a write to a sibling.
                Assert.Equal(new[] { "los", "average stay" }, Terms(BoundTo(await EntitiesAsync(sessions), "Encounter", "Avg Length of Stay")).Where(t => Live(t.State)).Select(t => t.Term));
            }
        }

        [Fact]
        public async Task Set_synonyms_never_writes_the_name_twice_when_the_author_wrote_it()
        {
            // An entity whose only copy of the name is an explicitly Authored term. A list that leaves it out may govern it,
            // but the name term the write then guarantees must be that same term, not a second copy beside a tombstone.
            var (engine, sessions) = await ModelAsync("Shop");
            using (engine)
            {
                var date = await engine.CreateTableAsync("Date", "human");
                await engine.CreateColumnAsync(date, "Calendar Date", "DateTime", "Calendar Date", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("date", "Date", null, ("date", Gen)),
                    PbiEnt("date.calendar_date", "Date", "Calendar Date", ("calendar date", "{\"State\":\"Authored\"}"), ("day", "{\"State\":\"Authored\"}"))));

                await engine.SetSynonymsAsync("column:Date/Calendar Date", new[] { "day" }, null, "agent");

                var words = Terms(BoundTo(await EntitiesAsync(sessions), "Date", "Calendar Date")).Select(t => t.Term.ToLowerInvariant()).ToList();
                Assert.True(words.Count == words.Distinct().Count(), string.Join(", ", words));
                Assert.Contains(Terms(BoundTo(await EntitiesAsync(sessions), "Date", "Calendar Date")), t => t.Term == "calendar date" && Live(t.State));
            }
        }

        // ---- N6: an old-name-term collision is not the exact tier's certain collision ----------------------------

        [Fact]
        public async Task An_old_name_term_collision_is_reported_at_medium_and_the_write_it_names_works()
        {
            // Item 2's shape between two fields: 'Date'[Date] renamed to 'Calendar Date' elsewhere, its entity still
            // saying "date", beside a visible 'Orders'[Date]. The stale word is worth flagging (item 2), but the write
            // guard never refuses it, so a claim that every synonym write fails is false and so is High. The note's own
            // remedy (set that field's synonyms without the old word) must work, and must clear the finding.
            var (engine, sessions) = await ModelAsync("Shop");
            using (engine)
            {
                var date = await engine.CreateTableAsync("Date", "human");
                await engine.CreateColumnAsync(date, "Calendar Date", "String", "Calendar Date", "human");
                var orders = await engine.CreateTableAsync("Orders", "human");
                await engine.CreateColumnAsync(orders, "Date", "DateTime", "Date", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("date", "Date", null, ("date", Gen)),
                    PbiEnt("date.date", "Date", "Calendar Date", ("date", Gen)),
                    PbiEnt("order", "Orders", null, ("order", Gen)),
                    PbiEnt("order.date", "Orders", "Date", ("date", Gen))));

                var finding = Assert.Single(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);
                Assert.Equal("Medium", finding.Severity);
                Assert.Contains("old name term 'date'", finding.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("every synonym write fails", finding.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("renaming through Semanticus refreshes it", finding.Message, StringComparison.Ordinal);
                // The remedy is clearing the stale word, not renaming either field.
                Assert.Contains("only through an old name term", finding.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("Rename or hide", finding.Message, StringComparison.Ordinal);

                await engine.SetSynonymsAsync("column:Date/Calendar Date", new[] { "day" }, null, "agent");
                Assert.Empty(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);
            }
        }

        [Fact]
        public async Task Clearing_a_stale_name_clears_every_spelling_of_it()
        {
            // Sol, round 3: Desktop writes a camelCase name twice ("customer key" and the as-written "CustomerKey", as in
            // the AdventureWorks sample's dim_customer.customer_key) beside a derived "key". After a rename made outside
            // Semanticus, a synonym write that cleared only the top stale spelling left "CustomerKey" live, still
            // colliding with the other 'Customer Key' column, while the scan (now seeing a name term) went quiet.
            var (engine, sessions) = await ModelAsync("Shop");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateColumnAsync(sales, "Customer Key", "Int64", "Customer Key", "human");
                var customer = await engine.CreateTableAsync("Customer", "human");
                await engine.CreateColumnAsync(customer, "ClientCode", "Int64", "ClientCode", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("sale", "Sales", null, ("sale", Gen)),
                    PbiEnt("sale.customer_key", "Sales", "Customer Key", ("customer key", Gen), ("key", Derived)),
                    PbiEnt("customer", "Customer", null, ("customer", Gen)),
                    PbiEnt("customer.customer_key", "Customer", "ClientCode", ("customer key", Gen),
                        ("CustomerKey", "{\"Type\":\"Noun\",\"State\":\"Generated\",\"Weight\":0.99}"), ("key", Derived))));
                Assert.NotEmpty(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);

                await engine.SetSynonymsAsync("column:Customer/ClientCode", new[] { "client identifier" }, null, "agent");

                var terms = Terms(BoundTo(await EntitiesAsync(sessions), "Customer", "ClientCode"));
                Assert.DoesNotContain(terms, t => Live(t.State) && QnaTerms.Normalize(t.Term) == "customer key");
                // Desktop derived "key" from the OLD name, not from 'ClientCode', so it is a leftover too and goes with it
                // (round 4, D2: only words made of the current name's words are Desktop's to keep).
                Assert.Contains(("key", "Deleted"), terms.Select(t => (t.Term, t.State)));
                Assert.Empty(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);
            }
        }

        [Fact]
        public async Task A_name_clash_that_also_meets_an_old_term_is_one_medium_finding_and_writes_still_work()
        {
            // Two fields that share a NAME ('Order Date' on 'Orders' and on 'Shipments') and a measure whose old name
            // term is that word: one clash (a set of fields), Medium, carrying the old-term note; the write goes through,
            // since nothing shows the validator refusing two visible fields (27 Sep ruling).
            var (engine, sessions) = await ModelAsync("Shop");
            using (engine)
            {
                var orders = await engine.CreateTableAsync("Orders", "human");
                await engine.CreateColumnAsync(orders, "Order Date", "DateTime", "Order Date", "human");
                var ship = await engine.CreateTableAsync("Shipments", "human");
                await engine.CreateColumnAsync(ship, "Order Date", "DateTime", "Order Date", "human");
                await engine.CreateMeasureAsync(ship, "Ship Count", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("order", "Orders", null, ("order", Gen)),
                    PbiEnt("order.order_date", "Orders", "Order Date", ("order date", Gen)),
                    PbiEnt("shipment", "Shipments", null, ("shipment", Gen)),
                    PbiEnt("shipment.order_date", "Shipments", "Order Date", ("order date", Gen)),
                    PbiEnt("shipment.ship_count", "Shipments", "Ship Count", ("order date", Gen))));

                var clash = Assert.Single(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);
                Assert.Equal("Medium", clash.Severity);
                Assert.Contains("measure 'Shipments'[Ship Count] (through its old name term 'order date')", clash.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("every synonym write fails", clash.Message, StringComparison.Ordinal);
                Assert.True((await engine.SetSynonymsAsync("column:Orders/Order Date", new[] { "ordered on" }, null, "agent")).Changed);
            }
        }

        // ---- N7: one guard per round 2 root fix ----------------------------------------------------------------

        [Fact]
        public async Task R2_guard_a_new_name_term_is_written_in_power_bi_split_form()
        {
            // R2: the name term set_synonyms writes for a camelCase name is Desktop's split lower form ("product name"),
            // not the name lower-cased whole ("productname").
            var (engine, sessions) = await ModelAsync("Shop");
            using (engine)
            {
                var product = await engine.CreateTableAsync("Product", "human");
                await engine.CreateColumnAsync(product, "ProductName", "String", "ProductName", "human");
                await InjectAsync(sessions, Lsdl(PbiEnt("product", "Product", null, ("product", Gen))));

                await engine.SetSynonymsAsync("column:Product/ProductName", new[] { "item" }, null, "agent");

                var terms = Terms(BoundTo(await EntitiesAsync(sessions), "Product", "ProductName"));
                Assert.Equal(("product name", "Generated"), (terms[0].Term, terms[0].State));
            }
        }

        [Fact]
        public async Task R3_guard_a_regular_plural_name_term_is_not_an_old_name()
        {
            // R3: an entity whose generated term reads as its name in Desktop's singular is not stale, so an 'Industries'
            // table beside a visible 'Industry' column (Regional Sales) is not reported through an "old name term".
            var (engine, sessions) = await ModelAsync("Regional");
            using (engine)
            {
                var ind = await engine.CreateTableAsync("Industries", "human");
                await engine.CreateColumnAsync(ind, "Industry", "String", "Industry", "human");
                await engine.CreateMeasureAsync(ind, "Deal Count", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("industry", "Industries", null, ("industry", Gen)),
                    PbiEnt("industry.industry", "Industries", "Industry", ("industry", Gen)),
                    PbiEnt("industry.deal_count", "Industries", "Deal Count", ("deal count", Gen), ("count", Derived))));

                Assert.DoesNotContain(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations, v => v.Message.Contains("old name term", StringComparison.Ordinal));
            }
        }

        [Fact]
        public async Task R4_guard_two_objects_that_meet_on_two_words_are_one_finding()
        {
            // R4: 'Revenue' was renamed from 'Sales' and 'Sales' from 'Revenue', both elsewhere. The two fields meet on
            // "sales" and again on "revenue": one set of fields, so one finding.
            var (engine, sessions) = await ModelAsync("Shop");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateMeasureAsync(sales, "Revenue", "1", "human");
                var budget = await engine.CreateTableAsync("Budget", "human");
                await engine.CreateMeasureAsync(budget, "Sales", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("sale", "Sales", null, ("sale", Gen)),
                    PbiEnt("sale.sales", "Sales", "Revenue", ("sales", Gen)),
                    PbiEnt("budget", "Budget", null, ("budget", Gen)),
                    PbiEnt("budget.revenue", "Budget", "Sales", ("revenue", Gen))));

                Assert.Single(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);
            }
        }

        [Fact]
        public async Task R5_guard_a_weight_that_is_not_a_number_on_a_stale_term_does_not_fail_the_scan()
        {
            // R5: the stale-term reader reaches the Weight of a generated term that is NOT the name, which a hand edit can
            // leave as a string. JsonElement.TryGetDouble throws on a non-number; the scan must survive it and still
            // pick the stale word.
            var (engine, sessions) = await ModelAsync("Shop");
            using (engine)
            {
                var date = await engine.CreateTableAsync("Date", "human");
                await engine.CreateColumnAsync(date, "Calendar Date", "String", "Calendar Date", "human");
                var orders = await engine.CreateTableAsync("Orders", "human");
                await engine.CreateColumnAsync(orders, "Date", "DateTime", "Date", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("date", "Date", null, ("date", Gen)),
                    PbiEnt("date.date", "Date", "Calendar Date", ("date", Gen), ("day", "{\"State\":\"Generated\",\"Weight\":\"high\"}")),
                    PbiEnt("order", "Orders", null, ("order", Gen))));

                var ev = Eval(sessions, "DAC-QNA-NAME-COLLISIONS");
                Assert.Contains(ev.Violations, v => v.Message.Contains("old name term 'date'", StringComparison.Ordinal));
            }
        }

        // ---- also found --------------------------------------------------------------------------------------

        [Fact]
        public async Task An_authored_term_is_stamped_in_utc_like_power_bi_desktop()
        {
            // Desktop stamps an authored term in UTC with a trailing Z ("2025-08-26T10:57:21.382Z"); Semanticus stamped
            // local time with an offset.
            var (engine, sessions) = await ModelAsync("Shop");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateMeasureAsync(sales, "Net Sales", "1", "human");
                await engine.CreateMeasureAsync(sales, "Margin", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("sale", "Sales", null, ("sale", "{}")),
                    PbiEnt("sale.margin", "Sales", "Margin", ("margin", "{}"), ("gross margin", "{\"LastModified\":\"2025-08-26T10:57:21.382Z\"}"))));

                await engine.SetSynonymsAsync("measure:Sales/Net Sales", new[] { "revenue" }, null, "agent");

                var raw = await ContentAsync(sessions);
                var stamp = System.Text.RegularExpressions.Regex.Match(raw, "\"revenue\":\\{[^}]*\"LastModified\":\"([^\"]+)\"");
                Assert.True(stamp.Success, raw);
                Assert.Matches("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z$", stamp.Groups[1].Value);
            }
        }

        [Fact]
        public async Task A_synonym_write_or_rename_keeps_every_other_timestamp_byte_for_byte()
        {
            // Desktop stamps with seven fractional digits ("2020-07-13T13:47:21.8380000Z", 20 stamps in the verifier's
            // public samples). Parsed as dates, they were written back trimmed (".838Z"), so one synonym write or rename
            // rewrote every stamp in the schema.
            const string stamp = "2020-07-13T13:47:21.8380000Z", offset = "2025-08-26T10:57:21.382+10:00";
            var (engine, sessions) = await ModelAsync("Shop");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateMeasureAsync(sales, "Net Sales", "1", "human");
                await engine.CreateMeasureAsync(sales, "Margin", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("sale", "Sales", null, ("sale", "{}")),
                    PbiEnt("sale.net_sale", "Sales", "Net Sales", ("net sale", "{}")),
                    PbiEnt("sale.margin", "Sales", "Margin", ("margin", "{}"), ("gross margin", $"{{\"LastModified\":\"{stamp}\"}}"), ("gm", $"{{\"LastModified\":\"{offset}\"}}"))));

                await engine.SetSynonymsAsync("measure:Sales/Net Sales", new[] { "revenue" }, null, "agent");
                await engine.RenameObjectAsync("measure:Sales/Net Sales", "Net Revenue", "agent");

                var raw = await ContentAsync(sessions);
                Assert.Contains($"\"gross margin\":{{\"LastModified\":\"{stamp}\"}}", raw);
                Assert.Contains($"\"gm\":{{\"LastModified\":\"{offset}\"}}", raw);
            }
        }

        [Fact]
        public async Task Opening_an_old_empty_model_leaves_no_temp_folder_behind()
        {
            var root = Path.Combine(Path.GetTempPath(), "semanticus-tests");
            var before = Directory.Exists(root) ? Directory.GetDirectories(root).ToHashSet() : new HashSet<string>();
            var sessions = new SessionManager();
            using (var engine = new LocalEngine(sessions, new Fake()))
            {
                await TestModels.OpenEmptyBimAsync(engine, "Old", 1400);
                Assert.Equal(1400, sessions.Current.Model.Database.CompatibilityLevel);
            }
            var after = Directory.Exists(root) ? Directory.GetDirectories(root).ToHashSet() : new HashSet<string>();
            Assert.Empty(after.Except(before));
        }

        // ---- round 4: the verifier's round 3 report (D1 to D6) ------------------------------------------------

        [Fact]
        public async Task R3_guard_a_field_whose_lemma_is_another_fields_name_is_not_an_old_name()
        {
            // D6: R3's guards paired a table with a field, and tables left the clash population, so undoing R3 failed
            // nothing. Two FIELDS: Desktop writes "square meter" for github-flow's 'Square Meters'; beside a visible
            // 'Square Meter' field in another table, a stale reading of that lemma is a false Medium clash.
            var (engine, sessions) = await ModelAsync("Flow");
            using (engine)
            {
                var prop = await engine.CreateTableAsync("Property", "human");
                await engine.CreateColumnAsync(prop, "Square Meters", "Double", "Square Meters", "human");
                var unit = await engine.CreateTableAsync("Unit", "human");
                await engine.CreateColumnAsync(unit, "Square Meter", "Double", "Square Meter", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("property", "Property", null, ("property", Gen)),
                    PbiEnt("property.square_meter", "Property", "Square Meters", ("square meter", Gen)),
                    PbiEnt("unit", "Unit", null, ("unit", Gen)),
                    PbiEnt("unit.square_meter", "Unit", "Square Meter", ("square meter", Gen))));

                Assert.Empty(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);
            }
        }

        [Fact]
        public async Task A_rename_drops_the_words_power_bi_derived_from_the_old_name()
        {
            // D1 with D2: after 'CustomerPONumber' becomes 'Order Reference', Desktop's "PO number" belongs to no current
            // name, and once the name term has moved nothing records the old name, so no later synonym write could tell it
            // from Desktop's own words. The rename drops it, as Desktop's own regeneration would.
            var (engine, sessions) = await ModelAsync("Sale");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateColumnAsync(sales, "CustomerPONumber", "String", "CustomerPONumber", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("fact_sale", "Sales", null, ("sale", Gen)),
                    PbiEnt("fact_sale.customer_PO_number", "Sales", "CustomerPONumber",
                        ("customer PO number", Gen), ("CustomerPONumber", Gen), ("PO number", Derived))));

                await engine.RenameObjectAsync("column:Sales/CustomerPONumber", "Order Reference", "agent");

                var live = Terms(BoundTo(await EntitiesAsync(sessions), "Sales", "Order Reference")).Where(t => Live(t.State)).Select(t => t.Term).ToList();
                Assert.Equal(new[] { "order reference" }, live);
            }
        }

        [Fact]
        public async Task A_word_power_bi_wrote_becomes_the_authors_when_listed_in_the_schemas_own_shape()
        {
            // D2 and D5 together, on a Desktop schema whose authored words carry stamps. The stateless unstamped "sales
            // amount" is Desktop's old name for 'Revenue' (not derived from it), so listing it makes it the author's
            // (stateless and stamped, like the author's "turnover"), and a later list without it clears it.
            var (engine, sessions) = await ModelAsync("Stamped");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateMeasureAsync(sales, "Revenue", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("sale", "Sales", null, ("sale", "{}")),
                    PbiEnt("sale.sales_amount", "Sales", "Revenue", ("sales amount", "{}"), ("turnover", "{\"LastModified\":\"2021-11-05T20:09:30.477Z\"}"))));

                await engine.SetSynonymsAsync("measure:Sales/Revenue", new[] { "sales amount", "turnover" }, null, "agent");
                var kept = Terms(BoundTo(await EntitiesAsync(sessions), "Sales", "Revenue")).Single(t => t.Term == "sales amount");
                Assert.Null(kept.Props["State"]);
                Assert.NotNull(kept.Props["LastModified"]);

                await engine.SetSynonymsAsync("measure:Sales/Revenue", new[] { "turnover" }, null, "agent");
                Assert.Equal("Deleted", Terms(BoundTo(await EntitiesAsync(sessions), "Sales", "Revenue")).Single(t => t.Term == "sales amount").State);
            }
        }

        [Fact]
        public async Task Set_synonyms_leaves_power_bi_words_that_are_not_made_of_the_name()
        {
            // Guard for D2's rule: most of Desktop's own words are not made of the name's words. Over 39 public Desktop
            // schemas, 52 such words sit on names that are still current: "date" on a date column, "postal code" on
            // 'Zip Code', "customer name" on the 'Customer' table. They are Power BI's, so a list that leaves them out
            // keeps them; only a leftover of an OLD name is governed.
            var (engine, sessions) = await ModelAsync("Flow");
            using (engine)
            {
                var customer = await engine.CreateTableAsync("Customer", "human");
                await engine.CreateColumnAsync(customer, "Birthday", "DateTime", "Birthday", "human");
                await engine.CreateColumnAsync(customer, "Zip Code", "String", "Zip Code", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("customer", "Customer", null, ("customer", Gen), ("customer name", Derived)),
                    PbiEnt("customer.birthday", "Customer", "Birthday", ("birthday", Gen), ("date", Derived)),
                    PbiEnt("customer.zip_code", "Customer", "Zip Code", ("zip code", Gen), ("postal code", Derived))));

                await engine.SetSynonymsAsync("table:Customer", new[] { "client" }, null, "agent");
                await engine.SetSynonymsAsync("column:Customer/Birthday", new[] { "dob" }, null, "agent");
                await engine.SetSynonymsAsync("column:Customer/Zip Code", new[] { "zip" }, null, "agent");

                var entities = await EntitiesAsync(sessions);
                var table = entities.Properties().Select(p => (JObject)p.Value).Single(e => (string)e["Definition"]["Binding"]["ConceptualProperty"] == null);
                Assert.Contains(("customer name", "Generated"), Terms(table).Select(t => (t.Term, t.State)));
                Assert.Contains(("date", "Generated"), Terms(BoundTo(entities, "Customer", "Birthday")).Select(t => (t.Term, t.State)));
                Assert.Contains(("postal code", "Generated"), Terms(BoundTo(entities, "Customer", "Zip Code")).Select(t => (t.Term, t.State)));
            }
        }

        [Fact]
        public void In_a_stamped_schema_a_stateless_old_name_is_stale_and_in_a_hand_written_one_it_is_the_authors()
        {
            // D5: after an author edit Desktop rewrites its own words with no State and stamps only the author's, so there
            // a stateless unstamped word left by a rename elsewhere is Power BI's old name. Without stamps (a hand-written
            // schema) the same word is the author's synonym (N3).
            var terms = new[] { ("sales amount", (string)null, false, 1.0), ("turnover", (string)null, true, 1.0) };
            Assert.True(QnaTerms.AuthoredWordsStamped(terms.Select(t => (t.Item2, t.Item3))));
            Assert.Equal("sales amount", QnaTerms.StaleNameTerm(terms, "Revenue", desktopStamps: true));
            Assert.Null(QnaTerms.StaleNameTerm(terms, "Revenue", desktopStamps: false));
            Assert.False(QnaTerms.AuthoredWordsStamped(new[] { ((string)null, false), ("Authored", false) }));
        }

        [Fact]
        public async Task Find_and_replace_over_synonyms_reports_what_the_model_holds_in_preview_and_apply()
        {
            // D2: the replace also rewrites the name term ("customer name" to "customer title"), which a synonym write
            // always keeps. After, in the preview and the apply alike, is the object's terms as the model holds them.
            var (engine, sessions) = await ModelAsync("Replace");
            using (engine)
            {
                var customer = await engine.CreateTableAsync("Customer", "human");
                await engine.CreateColumnAsync(customer, "Customer Name", "String", "Customer Name", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("customer", "Customer", null, ("customer", Gen)),
                    PbiEnt("customer.customer_name", "Customer", "Customer Name", ("customer name", Gen), ("name", Derived), ("client", "{\"State\":\"Authored\"}"))));
                var req = new ReplaceRequest { Ref = "column:Customer/Customer Name", Field = "synonyms", Find = "name", Replace = "title", WholeWord = true };

                req.Preview = true;
                var preview = await engine.ReplaceInObjectAsync(req, "agent");
                req.Preview = false;
                var applied = await engine.ReplaceInObjectAsync(req, "agent");

                var live = Terms(BoundTo(await EntitiesAsync(sessions), "Customer", "Customer Name")).Where(t => Live(t.State)).Select(t => t.Term);
                Assert.Equal(string.Join(", ", live), applied.After);
                Assert.Equal(applied.After, preview.After);
                Assert.Contains("customer name", applied.After, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task An_old_name_term_clash_is_fixed_in_the_plan_and_the_advisory_by_clearing_the_old_word()
        {
            // D4: the Model Plan line and create_linguistic_schema's advisory name the old-term remedy on the field that
            // carries the old word, not a rename.
            var (engine, sessions) = await ModelAsync("Copilot");
            using (engine)
            {
                var customer = await engine.CreateTableAsync("Customer", "human");
                await engine.CreateColumnAsync(customer, "Country", "String", "Country", "human");
                var store = await engine.CreateTableAsync("Store", "human");
                await engine.CreateColumnAsync(store, "Store Country", "String", "Store Country", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("customer", "Customer", null, ("customer", Gen)),
                    PbiEnt("customer.country", "Customer", "Country", ("country", Gen)),
                    PbiEnt("store", "Store", null, ("store", Gen)),
                    PbiEnt("store.country", "Store", "Store Country", ("country", Gen))));

                var advisory = await sessions.Require().ReadAsync(m => LsdlSynonyms.DescribeCollisions(m, m.Cultures["en-US"]));
                Assert.Contains("setting that field's synonyms without the old word clears it", advisory, StringComparison.Ordinal);
                Assert.DoesNotContain("Rename or hide", advisory, StringComparison.OrdinalIgnoreCase);

                PrepForAiReader.Invalidate(sessions.Current.Model);
                var plan = await engine.GetModelPlanAsync();
                var line = Assert.Single(plan.Items.Single(i => i.Id == "proposal/collisions").Diff);
                Assert.Contains("Store Country", line, StringComparison.Ordinal);
                Assert.EndsWith(": clear its old name term from its synonyms", line, StringComparison.Ordinal);
            }
        }
    }
}
