using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier round 3 of m11/feedback-lsdl (item 2: a rename moves the object's name terms). Power BI Desktop splits
    /// a camelCase name at an acronym run and before a digit, and writes that split form as the entity's FIRST name
    /// term, beside the name as written: 'CustomerPONumber' gets "customer PO number" and "CustomerPONumber"
    /// (a public Desktop-saved schema, entity fact_sale.customer_PO_number); 'Column1' gets "column 1" and "Column1"
    /// (another, entity table.column_1). QnaTerms.ReadsAsName accepts only the as-written copy, so a rename moves that
    /// copy and leaves Desktop's primary name term live. The entity then reads as named, so the scan cannot see the
    /// leftover and set_synonyms leaves it alone as one of Desktop's own words. Round 2's N4 on a new family of names.
    /// A third form is the name without its bracketed qualifier ('Average Lead Time (Days)' gets "average lead time").
    /// Also here: set_synonyms now leaves every State Generated word alone except the stale name, so a word the author
    /// listed can no longer be dropped and find and replace reports replacements it did not make (both regressions
    /// against 8dacc189), and two MCP tool descriptions still describe the behaviour the lane removed.
    /// </summary>
    public sealed class LinguisticSchemaFeedbackVerifyR3Tests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

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

        private static async Task<List<(string Term, string State)>> TermsAsync(SessionManager sessions, string table, string prop)
        {
            var entities = (JObject)JObject.Parse(await sessions.Require().ReadAsync(m => m.Cultures["en-US"].Content))["Entities"];
            var e = entities.Properties().Select(p => (JObject)p.Value).Single(x =>
            {
                var b = (JObject)(x["Definition"]?["Binding"] ?? x["Binding"]);
                return (string)b["ConceptualEntity"] == table && (string)b["ConceptualProperty"] == prop;
            });
            return ((JArray)e["Terms"]).OfType<JObject>().SelectMany(o => o.Properties())
                .Select(p => (p.Name, (string)p.Value["State"] ?? "Authored")).ToList();
        }

        private static bool Live(string state) => state != "Deleted" && state != "Suggested";

        private static async Task<(LocalEngine Engine, SessionManager Sessions)> ModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("Shop", 1604);
            return (engine, sessions);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_rename_moves_a_name_term_power_bi_wrote_with_an_acronym_split()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateColumnAsync(sales, "CustomerPONumber", "String", "CustomerPONumber", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("fact_sale", "Sales", null, ("sale", Gen)),
                    PbiEnt("fact_sale.customer_PO_number", "Sales", "CustomerPONumber",
                        ("customer PO number", Gen), ("CustomerPONumber", Gen), ("PO number", Derived))));

                await engine.RenameObjectAsync("column:Sales/CustomerPONumber", "Order Reference", "agent");

                var terms = await TermsAsync(sessions, "Sales", "Order Reference");
                Assert.True(!terms.Any(t => Live(t.State) && t.Term == "customer PO number"),
                    "the old name term Desktop wrote first is still live after the rename: " + string.Join(", ", terms.Select(t => $"{t.Term} [{t.State}]")));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_rename_moves_a_name_term_power_bi_wrote_with_a_digit_split()
        {
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                var orders = await engine.CreateTableAsync("Orders", "human");
                await engine.CreateColumnAsync(orders, "Column1", "DateTime", "Column1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("order", "Orders", null, ("order", Gen)),
                    PbiEnt("order.column_1", "Orders", "Column1", ("column 1", Gen), ("Column1", Gen))));

                await engine.RenameObjectAsync("column:Orders/Column1", "Order Date", "agent");

                var terms = await TermsAsync(sessions, "Orders", "Order Date");
                Assert.True(!terms.Any(t => Live(t.State) && t.Term == "column 1"),
                    "the old name term Desktop wrote first is still live after the rename: " + string.Join(", ", terms.Select(t => $"{t.Term} [{t.State}]")));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_rename_moves_a_name_term_power_bi_wrote_without_the_bracketed_qualifier()
        {
            // A third Desktop form: the name without its bracketed unit, written first and twice, beside the full name
            // (a public Desktop-saved schema, entity all_measure.average_lead_time). A rename moves only the full form.
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                var all = await engine.CreateTableAsync("All Measure", "human");
                await engine.CreateMeasureAsync(all, "Average Lead Time (Days)", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("all_measure", "All Measure", null, ("all measure", Gen)),
                    PbiEnt("all_measure.average_lead_time", "All Measure", "Average Lead Time (Days)",
                        ("average lead time", Gen), ("average lead time", Gen), ("average lead time (Days)", Gen))));

                await engine.RenameObjectAsync("measure:All Measure/Average Lead Time (Days)", "Delivery Days", "agent");

                var terms = await TermsAsync(sessions, "All Measure", "Delivery Days");
                Assert.True(!terms.Any(t => Live(t.State) && t.Term == "average lead time"),
                    "the old name term Desktop wrote first is still live after the rename: " + string.Join(", ", terms.Select(t => $"{t.Term} [{t.State}]")));
            }
        }

        // ---- set_synonyms now leaves every State Generated word alone except the stale name (LsdlSynonyms.SetTerms) -----

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_a_word_the_author_listed_can_be_removed_by_a_later_synonym_write()
        {
            // 'Revenue' was renamed from 'Sales Amount' outside Semanticus, so its entity still says "sales amount"
            // (Generated). The author keeps that word as a synonym, then drops it. The first write leaves it live but still
            // State Generated; the entity then has a name term, so the word is no longer the stale name, and the second
            // write can no longer remove it. At 8dacc189 the second write tombstoned it.
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateMeasureAsync(sales, "Revenue", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("sale", "Sales", null, ("sale", Gen)),
                    PbiEnt("sale.sales_amount", "Sales", "Revenue", ("sales amount", Gen))));

                await engine.SetSynonymsAsync("measure:Sales/Revenue", new[] { "sales amount", "turnover" }, null, "agent");
                await engine.SetSynonymsAsync("measure:Sales/Revenue", new[] { "turnover" }, null, "agent");

                var terms = await TermsAsync(sessions, "Sales", "Revenue");
                Assert.True(!terms.Any(t => Live(t.State) && t.Term == "sales amount"),
                    "the word the author dropped is still live: " + string.Join(", ", terms.Select(t => $"{t.Term} [{t.State}]")));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_find_and_replace_over_synonyms_replaces_the_word_it_reports()
        {
            // Find and replace reads every live term (Desktop's generated words included) and writes the edited list back
            // through set_synonyms, which now leaves Desktop's generated words alone. The result reports the word replaced
            // (After, Changed) while the model still holds it. At 8dacc189 the old word was tombstoned.
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                var customer = await engine.CreateTableAsync("Customer", "human");
                await engine.CreateColumnAsync(customer, "Customer Name", "String", "Customer Name", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("customer", "Customer", null, ("customer", Gen)),
                    PbiEnt("customer.customer_name", "Customer", "Customer Name", ("customer name", Gen), ("name", Derived), ("client", "{\"State\":\"Authored\"}"))));

                var r = await engine.ReplaceInObjectAsync(new ReplaceRequest
                {
                    Ref = "column:Customer/Customer Name", Field = "synonyms", Find = "name", Replace = "title", WholeWord = true,
                }, "agent");

                Assert.True(r.Changed);
                Assert.DoesNotContain("name", (r.After ?? "").Split(',').Select(x => x.Trim()));
                var terms = await TermsAsync(sessions, "Customer", "Customer Name");
                Assert.True(!terms.Any(t => Live(t.State) && t.Term == "name"),
                    $"the replace reported \"{r.After}\" but the model holds: " + string.Join(", ", terms.Select(t => $"{t.Term} [{t.State}]")));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_an_old_name_term_clash_points_at_the_field_that_carries_the_old_word()
        {
            // The remedy is on the field whose entity still says the old word ("set that field's synonyms without the old
            // word"), but the finding is anchored on the clash's first member in table order, here the innocent
            // 'Customer'[Country]; the Model Plan row then says "~ Customer[Country]: rename it or hide it". Shape: the
            // 2025 Copilot demo with 'Store'[Country] renamed to 'Store Country' outside Semanticus.
            var (engine, sessions) = await ModelAsync();
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

                PrepForAiReader.Invalidate(sessions.Current.Model);
                var f = Assert.Single(ReadinessRuleSet.Default().Single(r => r.Id == "DAC-QNA-NAME-COLLISIONS").Evaluate(sessions.Current.Model).Violations);
                Assert.Contains("only through an old name term", f.Message, StringComparison.Ordinal);
                Assert.Equal("column:Store/Store Country", f.ObjectRef);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_set_synonyms_leaves_the_derived_words_power_bi_rewrote_without_a_state()
        {
            // The ruling: Desktop's derived words are Desktop's, not the author's, so a list that leaves them out does not
            // tombstone them. After an author edit, Desktop rewrites its generated words with no State: the author's own
            // word carries a LastModified stamp, Desktop's derived words carry its 0.97 weight and no stamp. Shape copied
            // from the Microsoft Regional Sales sample (entity opportunity.revenue_in_pipeline). Only explicit State
            // Generated words are left alone today, so these are tombstoned. Not a regression: 8dacc189 did the same.
            var (engine, sessions) = await ModelAsync();
            using (engine)
            {
                var opp = await engine.CreateTableAsync("Opportunities", "human");
                await engine.CreateMeasureAsync(opp, "Revenue In Pipeline", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("opportunity", "Opportunities", null, ("opportunity", "{}")),
                    PbiEnt("opportunity.revenue_in_pipeline", "Opportunities", "Revenue In Pipeline",
                        ("revenue in pipeline", "{}"), ("Rev In Pipeline", "{\"LastModified\":\"2021-11-05T20:09:30.477Z\"}"),
                        ("pipeline", "{\"Weight\":0.97}"), ("revenue", "{\"Weight\":0.97}"))));

                await engine.SetSynonymsAsync("measure:Opportunities/Revenue In Pipeline", new[] { "Rev In Pipeline", "pipeline value" }, null, "agent");

                var terms = await TermsAsync(sessions, "Opportunities", "Revenue In Pipeline");
                Assert.True(terms.Any(t => Live(t.State) && t.Term == "pipeline") && terms.Any(t => Live(t.State) && t.Term == "revenue"),
                    "Desktop's derived words were tombstoned: " + string.Join(", ", terms.Select(t => $"{t.Term} [{t.State}]")));
            }
        }

        [Fact]
        public void Verify_the_synonym_and_safe_fix_tool_descriptions_match_what_the_tools_do()
        {
            // The assistant reads these. set_synonyms no longer refuses a write because two visible names read alike, and
            // apply_safe_fixes now sets DIM-PRIMARY-NAME's row label, a Naming finding.
            // A tool's text sits on the method that carries [McpServerTool]: the compact wrapper in McpTools.LeanResults.cs for
            // a tool that has one, the method itself otherwise. So the text is found by the tool's name, not by a method name.
            string Describe(string tool) => typeof(McpTools).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(m => m.GetCustomAttributes(true).Any(a => a.GetType().Name == "McpServerToolAttribute"
                    && (string)a.GetType().GetProperty("Name")!.GetValue(a)! == tool))
                .Single()
                .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;

            Assert.DoesNotContain("a write that would collide two names in the linguistic schema fails", Describe("set_synonyms"), StringComparison.Ordinal);
            Assert.DoesNotContain("naming findings are left alone", Describe("apply_safe_fixes"), StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("customer PO number", "CustomerPONumber")]
        [InlineData("PL line name", "PLLineName")]
        [InlineData("dim PL line", "Dim_PLLine")]
        [InlineData("column 1", "Column1")]
        [InlineData("dynamic measure 1", "DynamicMeasure1")]
        [InlineData("people 2016", "People2016")]
        public void Verify_the_first_name_term_power_bi_desktop_wrote_reads_as_the_name(string term, string name) =>
            Assert.True(QnaTerms.ReadsAsName(term, name), $"\"{term}\" is the name term Power BI Desktop wrote for '{name}'");
    }
}
