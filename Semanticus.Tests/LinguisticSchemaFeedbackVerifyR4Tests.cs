using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier, targeted round 4 of m11/feedback-lsdl. Round 3's D2 (find and replace over synonyms reports a
    /// replacement it did not make) is fixed on the single replace (replace_in_object: the removed words are passed to
    /// the write, and After is read back from the model), but not on the bulk one. Studio's "Replace all" and MCP
    /// propose_replace build a set_synonyms plan item from the same replace context (LocalEngine.BuildReplaceSeeds,
    /// Before = every live term, After = the replaced list), and apply_plan writes it with
    /// LsdlSynonyms.SetSynonyms(obj, culture, After): no removed words. So Power BI Desktop's derived "name" beside
    /// 'Customer Name' stays live, and the approved item's After ("customer title, title, client") is not what the model
    /// then holds (the name term always stays). The first is round 3's regression on the bulk path: at 8dacc189 a
    /// synonym write tombstoned every Generated word a list left out. The second is the bulk twin of the After the
    /// single replace now reads back.
    /// </summary>
    public sealed class LinguisticSchemaFeedbackVerifyR4Tests
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

        // 'Customer Name' as Power BI Desktop saves it: the name term, Desktop's derived "name" (weight 0.97), and the
        // author's "client".
        private static async Task<(LocalEngine Engine, SessionManager Sessions)> CustomerNameAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("Shop", 1604);
            var customer = await engine.CreateTableAsync("Customer", "human");
            await engine.CreateColumnAsync(customer, "Customer Name", "String", "Customer Name", "human");
            await sessions.Require().MutateAsync("human", "seed linguistic schema", m =>
            {
                var cult = m.Cultures.Contains("en-US") ? m.Cultures["en-US"] : m.AddTranslation("en-US");
                cult.Content = Lsdl(
                    PbiEnt("customer", "Customer", null, ("customer", Gen)),
                    PbiEnt("customer.customer_name", "Customer", "Customer Name", ("customer name", Gen), ("name", Derived), ("client", "{\"State\":\"Authored\"}")));
            });
            return (engine, sessions);
        }

        private static async Task<ChangeItem> ReplaceAllAsync(LocalEngine engine)
        {
            var view = await engine.ProposeReplaceAsync(new SearchOptions { Query = "name", WholeWord = true, Fields = new[] { "synonyms" } }, "title", 0, "human");
            var item = Assert.Single(view.Items, i => i.Kind == "set_synonyms");
            await engine.ApplyPlanAsync(new[] { item.Id }, "human");
            return item;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_replace_all_over_synonyms_replaces_the_word_it_reports()
        {
            var (engine, sessions) = await CustomerNameAsync();
            using (engine)
            {
                var item = await ReplaceAllAsync(engine);

                var terms = await TermsAsync(sessions, "Customer", "Customer Name");
                Assert.True(!terms.Any(t => Live(t.State) && t.Term == "name"),
                    $"the applied item said \"{item.Before}\" becomes \"{item.After}\", but the model holds: " + string.Join(", ", terms.Select(t => $"{t.Term} [{t.State}]")));
            }
        }

        [Fact]
        public async Task Verify_replace_all_over_synonyms_item_shows_what_the_model_then_holds()
        {
            var (engine, sessions) = await CustomerNameAsync();
            using (engine)
            {
                var item = await ReplaceAllAsync(engine);

                var live = (await TermsAsync(sessions, "Customer", "Customer Name")).Where(t => Live(t.State)).Select(t => t.Term);
                Assert.Equal(string.Join(", ", live), item.After);
            }
        }
    }
}
