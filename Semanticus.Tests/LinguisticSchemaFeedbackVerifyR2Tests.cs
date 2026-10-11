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
    /// Verifier tests for m11/feedback-lsdl round 2 (3a723512). Each one is left FAILING on the lane and names one
    /// defect in the shared name-term predicate the round added (QnaTerms.ReadsAsName, QnaTerms.NameTerm and the
    /// "no State, no LastModified is generated" reading). The shapes are copied from public schemas:
    /// (a) Power BI Desktop writes an irregular plural's name term as its lemma: 'Total Children' carries
    ///     "total child" (key "customer.total_child") in Contoso and AdventureWorks models saved by Desktop, and
    ///     'PeopleSignedUp' carries "person signed up". ReadsAsName's singular only knows suffix rules, so it does
    ///     not read those as the name.
    /// (b) NameTerm and ReadsAsName disagree on a camelCase name whose last hump is a single capital or follows a
    ///     digit ('SalesYoY', 'NetSalesQ1'): the term the writer produces is not one the reader accepts.
    /// (c) A hand-written schema (a public Fabric healthcare demo) carries authored synonyms with no State and no
    ///     LastModified stamp, which the new reading takes for generated terms.
    /// None of these objects was ever renamed, so nothing here may be reported as an "old name term".
    /// </summary>
    public sealed class LinguisticSchemaFeedbackVerifyR2Tests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        // ---- fixture helpers (same shapes as LinguisticSchemaFeedbackVerifyTests) ------------------------------

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
        private const string Thesaurus = "{\"Type\":\"Noun\",\"State\":\"Suggested\",\"Source\":{\"Agent\":\"Thesaurus\"},\"Weight\":0.582}";

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

        /// <summary>Contoso's 'Customer' dimension as Power BI Desktop saved it: the visible 'Total Children' column
        /// carries "total child" first and the derived "child" (Weight 0.97), then thesaurus suggestions.</summary>
        private static async Task<(LocalEngine Engine, SessionManager Sessions)> ContosoCustomerAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("Contoso", 1604);
            var customer = await engine.CreateTableAsync("Customer", "human");
            await engine.CreateColumnAsync(customer, "Customer Name", "String", "Customer Name", "human");
            await engine.CreateColumnAsync(customer, "Total Children", "Int64", "Total Children", "human");
            await InjectAsync(sessions, Lsdl(
                PbiEnt("customer", "Customer", null, ("customer", Gen)),
                PbiEnt("customer.customer_name", "Customer", "Customer Name", ("customer name", Gen), ("name", Derived)),
                PbiEnt("customer.total_child", "Customer", "Total Children", ("total child", Gen), ("child", Derived),
                    ("aggregate child", Thesaurus))));
            return (engine, sessions);
        }

        // ---- (b) the writer's name term must be one the reader accepts ----------------------------------------

        [Fact]
        public async Task Verify_set_synonyms_writes_the_name_term_of_a_camel_case_name_with_a_short_last_hump_once()
        {
            // NameTerm keeps a last one-letter hump joined ("sales yoy" for 'SalesYoY', "net salesq1" for
            // 'NetSalesQ1'), while ReadsAsName compares through Normalize, which splits it ("sales yo y", "net sales
            // q1"). So the name term CreateEntity writes is not a name term to SetTerms: the first call tombstones it
            // as a synonym the list left out and writes it again, and every later call adds one more tombstone. That
            // is round 1's D1 (one word live and Deleted in one entity) on a different family of names. The base
            // wrote each word once.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Shop", 1604);
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateMeasureAsync(sales, "SalesYoY", "1", "human");
                await engine.CreateMeasureAsync(sales, "NetSalesQ1", "1", "human");
                await engine.CreateLinguisticSchemaAsync(null, "agent");

                foreach (var name in new[] { "SalesYoY", "NetSalesQ1" })
                {
                    await engine.SetSynonymsAsync($"measure:Sales/{name}", new[] { "growth" }, null, "agent");
                    await engine.SetSynonymsAsync($"measure:Sales/{name}", new[] { "growth rate" }, null, "agent");
                    var words = Terms(BoundTo(await EntitiesAsync(sessions), "Sales", name)).Select(t => t.Term.ToLowerInvariant()).ToList();
                    Assert.True(words.Count == words.Distinct().Count(), $"{name} terms: " + string.Join(", ", words));
                }
            }
        }

        // ---- (a) Desktop's lemma for an irregular plural is the name term -------------------------------------

        [Fact]
        public async Task Verify_rename_moves_a_name_term_power_bi_wrote_as_an_irregular_singular()
        {
            // Item 2 on Desktop's own shape for an irregular plural: 'Total Children' carries "total child". The
            // rename does not recognise it as the name term, so after renaming the column the entity still carries
            // "total child" as its live Generated name term, the stale word item 2 is about.
            var (engine, sessions) = await ContosoCustomerAsync();
            using (engine)
            {
                await engine.RenameObjectAsync("column:Customer/Total Children", "Number of Kids", "agent");

                var terms = Terms(BoundTo(await EntitiesAsync(sessions), "Customer", "Number of Kids"));
                Assert.DoesNotContain(terms, t => Live(t.State) && t.Term == "total child");
                Assert.Contains(terms, t => t.State == "Generated" && QnaTerms.Normalize(t.Term) == QnaTerms.Normalize("Number of Kids"));
            }
        }

        [Fact]
        public async Task Verify_set_synonyms_keeps_a_name_term_power_bi_wrote_as_an_irregular_singular()
        {
            // Item 3 on the same shape: set_synonyms treats Desktop's "total child" as a synonym the list left out,
            // tombstones it, and inserts a second name term "total children".
            var (engine, sessions) = await ContosoCustomerAsync();
            using (engine)
            {
                await engine.SetSynonymsAsync("column:Customer/Total Children", new[] { "kids" }, null, "agent");

                var terms = Terms(BoundTo(await EntitiesAsync(sessions), "Customer", "Total Children"));
                Assert.Contains(("total child", "Generated"), terms.Select(t => (t.Term, t.State)));
                Assert.DoesNotContain(terms, t => t.Term == "total children");
            }
        }

        [Fact]
        public async Task Verify_an_irregular_singular_power_bi_wrote_is_not_reported_as_an_old_name_term()
        {
            // A 'People' table and its visible 'Person' label column. Desktop writes the table's name term as its
            // lemma ("person", as it writes "person signed up" for 'PeopleSignedUp'), and nothing was renamed. The
            // lane reads "person" as an OLD name of 'People', so the table enters the collision rule a second time and
            // meets the column: a new High finding that says the table was renamed. Its regular-plural twin
            // ('Industries' carrying "industry" beside 'Industry') is deliberately not flagged, and the base reports
            // nothing here.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Club", 1604);
                var people = await engine.CreateTableAsync("People", "human");
                await engine.CreateColumnAsync(people, "Person", "String", "Person", "human");
                await engine.CreateMeasureAsync(people, "Member Count", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("person", "People", null, ("person", Gen)),
                    PbiEnt("person.person", "People", "Person", ("person", Gen)),
                    PbiEnt("person.member_count", "People", "Member Count", ("member count", Gen), ("count", Derived))));

                var ev = Eval(sessions, "DAC-QNA-NAME-COLLISIONS");
                Assert.DoesNotContain(ev.Violations, v => v.Message.Contains("old name term", StringComparison.Ordinal));
            }
        }

        // ---- the scan and the write must agree on what blocks a synonym write -------------------------------

        [Fact]
        public async Task Verify_a_collision_found_only_through_an_old_name_term_does_not_claim_every_write_fails()
        {
            // DAC-QNA-NAME-COLLISIONS promises "scan and write agree: an EXACT normalised match is a certain collision
            // (the write refuses it)", and its exact-tier message says the schema "cannot hold entities for more than
            // one (every synonym write fails)". The set_synonyms guard compares object NAMES only, so a collision the
            // lane now finds through an old name term (item 2's own shape: 'Date'[Date] renamed to 'Calendar Date'
            // elsewhere, its entity still saying "date") is reported High as blocking every write, and the next write
            // goes through. Either the finding stops saying so or the write refuses: the two must agree. The base
            // reports nothing here.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Shop", 1604);
                var date = await engine.CreateTableAsync("Date", "human");
                await engine.CreateColumnAsync(date, "Calendar Date", "String", "Calendar Date", "human");
                await engine.CreateMeasureAsync(date, "Day Count", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("date", "Date", null, ("date", Gen)),
                    PbiEnt("date.date", "Date", "Calendar Date", ("date", Gen)),
                    PbiEnt("date.day_count", "Date", "Day Count", ("day count", Gen))));

                var claimsBlocked = Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations
                    .Any(v => v.Message.Contains("every synonym write fails", StringComparison.Ordinal));
                var write = await Record.ExceptionAsync(() => engine.SetSynonymsAsync("measure:Date/Day Count", new[] { "days" }, null, "agent"));
                Assert.False(claimsBlocked && write == null, "the scan says every synonym write fails, and set_synonyms just wrote one");
            }
        }

        // ---- (c) an authored synonym with no State is not a generated name term -------------------------------

        [Fact]
        public async Task Verify_authored_synonyms_with_no_state_are_not_reported_as_old_name_terms()
        {
            // A public hand-written schema (a Fabric healthcare demo) writes every synonym with no State and no
            // LastModified, and gives the measure no name term at all. The lane reads those words as generated and,
            // since none reads as the name, takes the first as the measure's OLD name: on that real model the scan
            // gains a High finding that 'Avg Length of Stay' collides with 'length_of_stay' "through its old name term
            // 'length of stay'". Nothing was renamed, and the advice (set the synonyms again) deletes the author's
            // word. The base reports nothing here.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Health", 1604);
                var enc = await engine.CreateTableAsync("fact_encounter", "human");
                await engine.CreateColumnAsync(enc, "length_of_stay", "Int64", "length_of_stay", "human");
                await engine.CreateMeasureAsync(enc, "Avg Length of Stay", "1", "human");
                await InjectAsync(sessions, "{\"Version\":\"1.0.0\",\"Language\":\"en-US\",\"DynamicImprovement\":\"HighConfidence\",\"Entities\":{"
                    + "\"fact_encounter.Avg Length of Stay\":{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"fact_encounter\",\"ConceptualProperty\":\"Avg Length of Stay\"}},"
                    + "\"Terms\":[{\"length of stay\":{}},{\"los\":{}},{\"average stay\":{}},{\"days in hospital\":{}}]}}}");

                var ev = Eval(sessions, "DAC-QNA-NAME-COLLISIONS");
                Assert.DoesNotContain(ev.Violations, v => v.Message.Contains("old name term", StringComparison.Ordinal));
            }
        }
    }
}
