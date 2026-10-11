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
    /// Round 5 of m11/feedback-lsdl, against the verifier's targeted round 4 report (V1, L1 to L4). Replace all over
    /// synonyms makes the single replace's write on both doors and reports only what it made (V1); the engine's own
    /// revived words cannot flip the schema's stamp reading (L1); and four sub-fixes of round 4 each get a guard of their
    /// own: a bracketed name's bare term is the name (D1, L2), the scan and a rename read Desktop's stamps (D5, L3), and
    /// the old-name-term note says what an old name term is (D4, L4). Shapes are the verifier's, from public Desktop files.
    /// </summary>
    public sealed class LinguisticSchemaFeedbackR5Tests
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
        private const string Stamp = "{\"LastModified\":\"2021-11-05T20:09:30.477Z\"}";

        private static Task InjectAsync(SessionManager sessions, string content) =>
            sessions.Require().MutateAsync("human", "seed linguistic schema", m =>
            {
                var cult = m.Cultures.Contains("en-US") ? m.Cultures["en-US"] : m.AddTranslation("en-US");
                cult.Content = content;
            });

        private static async Task<string> ContentAsync(SessionManager sessions) =>
            await sessions.Require().ReadAsync(m => m.Cultures["en-US"].Content);

        private static async Task<List<(string Term, string State)>> TermsAsync(SessionManager sessions, string table, string prop)
        {
            var entities = (JObject)JObject.Parse(await ContentAsync(sessions))["Entities"];
            var e = entities.Properties().Select(p => (JObject)p.Value).Single(x =>
            {
                var b = (JObject)(x["Definition"]?["Binding"] ?? x["Binding"]);
                return (string)b["ConceptualEntity"] == table && (string)b["ConceptualProperty"] == prop;
            });
            return ((JArray)e["Terms"]).OfType<JObject>().SelectMany(o => o.Properties())
                .Select(p => (p.Name, (string)p.Value["State"] ?? "Authored")).ToList();
        }

        private static bool Live(string state) => state != "Deleted" && state != "Suggested";

        private static string Show(IEnumerable<(string Term, string State)> terms) =>
            string.Join(", ", terms.Select(t => $"{t.Term} [{t.State}]"));

        private static async Task<(LocalEngine Engine, SessionManager Sessions)> ModelAsync(string name)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync(name, 1604);
            return (engine, sessions);
        }

        // 'Customer Name' as Power BI Desktop saves it: the name term, Desktop's derived "name" (weight 0.97), and the
        // author's "client".
        private static async Task<(LocalEngine Engine, SessionManager Sessions)> CustomerNameAsync()
        {
            var (engine, sessions) = await ModelAsync("Shop");
            var customer = await engine.CreateTableAsync("Customer", "human");
            await engine.CreateColumnAsync(customer, "Customer Name", "String", "Customer Name", "human");
            await InjectAsync(sessions, Lsdl(
                PbiEnt("customer", "Customer", null, ("customer", Gen)),
                PbiEnt("customer.customer_name", "Customer", "Customer Name", ("customer name", Gen), ("name", Derived), ("client", "{\"State\":\"Authored\"}"))));
            return (engine, sessions);
        }

        private static readonly SearchOptions FindName = new SearchOptions { Query = "name", WholeWord = true, Fields = new[] { "synonyms" } };

        // ---- V1: Replace all over synonyms is the single replace, item by item, on both doors ----------------------

        // Studio's door: "Replace all..." calls proposeReplace and the Changes tab applies the plan. The applied item
        // writes the linguistic schema byte for byte as the single Replace on the same match writes it, and its After is
        // what the model then holds.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Replace_all_over_synonyms_writes_what_the_single_replace_writes()
        {
            var (single, singleSessions) = await CustomerNameAsync();
            var (bulk, bulkSessions) = await CustomerNameAsync();
            using (single)
            using (bulk)
            {
                await single.ReplaceInObjectAsync(new ReplaceRequest
                {
                    Ref = "column:Customer/Customer Name", Field = "synonyms", Find = "name", Replace = "title", WholeWord = true,
                }, "human");

                var studio = new EngineRpcTarget(bulk);
                var view = await studio.proposeReplace(FindName, "title");
                var item = Assert.Single(view.Items, i => i.Kind == "set_synonyms");
                var report = await studio.applyPlan(new[] { item.Id });

                Assert.Equal(1, report.AppliedCount);
                Assert.Equal(await ContentAsync(singleSessions), await ContentAsync(bulkSessions));
                var live = (await TermsAsync(bulkSessions, "Customer", "Customer Name")).Where(t => Live(t.State)).Select(t => t.Term);
                Assert.DoesNotContain("name", live);
                Assert.Equal(string.Join(", ", live), item.After);
                Assert.Equal("applied", Assert.Single(report.Items).Note);
            }
        }

        // The assistant's door: propose_replace, then apply_plan. The report's item says what the model holds.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Replace_all_over_synonyms_reports_what_the_model_holds_through_mcp()
        {
            var (engine, sessions) = await CustomerNameAsync();
            using (engine)
            {
                var view = await McpTools.ProposeReplace(engine, "name", "title", wholeWord: true, fields: new[] { "synonyms" });
                var item = Assert.Single(view.Items, i => i.Kind == "set_synonyms");
                Assert.Equal("customer name, name, client", item.Before);
                Assert.Equal("customer name, client, customer title, title", item.After);

                var report = await McpTools.ApplyPlan(engine, new[] { item.Id });

                var terms = await TermsAsync(sessions, "Customer", "Customer Name");
                Assert.True(terms.Contains(("name", "Deleted")), Show(terms));
                var applied = Assert.Single(report.Items);
                Assert.Equal("applied", applied.Status);
                Assert.Equal(string.Join(", ", terms.Where(t => Live(t.State)).Select(t => t.Term)), applied.After);
            }
        }

        // An After re-authored on the plan is the list: a word of Before it leaves out goes, as the replace's own did.
        // The item then shows what that write leaves, before anything is applied (the name term always stays, so asking
        // to drop it shows it kept), and undo and redo find the item by that reading (Sol, round 5).
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_reauthored_replace_item_shows_what_its_write_leaves_and_survives_undo_and_redo()
        {
            var (engine, sessions) = await CustomerNameAsync();
            using (engine)
            {
                var view = await McpTools.ProposeReplace(engine, "name", "title", wholeWord: true, fields: new[] { "synonyms" });
                var item = Assert.Single(view.Items, i => i.Kind == "set_synonyms");
                var edited = (await McpTools.SetPlanItem(engine, item.Id, after: "customer title, client", approved: true)).Items.Single(i => i.Id == item.Id);
                Assert.Equal("customer name, client, customer title", edited.After);

                var report = await McpTools.ApplyPlan(engine, new[] { item.Id });

                var terms = await TermsAsync(sessions, "Customer", "Customer Name");
                Assert.True(terms.Contains(("name", "Deleted")), Show(terms));
                Assert.Equal(edited.After, string.Join(", ", terms.Where(t => Live(t.State)).Select(t => t.Term)));
                var applied = Assert.Single(report.Items);
                Assert.Equal("applied", applied.Status);
                Assert.Equal("applied", applied.Note);

                await engine.UndoAsync("human");
                Assert.Equal("approved", (await engine.GetPlanAsync()).Items.Single(i => i.Id == item.Id).Status);
                await engine.RedoAsync("human");
                Assert.Equal("applied", (await engine.GetPlanAsync()).Items.Single(i => i.Id == item.Id).Status);
            }
        }

        // Sol, round 5: a change the live terms cannot show (a word written and cleared again leaves a tombstone, which
        // the write would revive in its old place) makes the write differ from the item's After. Apply refuses it with
        // the reason rather than reporting an After the model does not hold.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_replace_item_whose_write_would_now_differ_from_its_after_is_not_applied()
        {
            var (engine, sessions) = await CustomerNameAsync();
            using (engine)
            {
                var view = await McpTools.ProposeReplace(engine, "name", "title", wholeWord: true, fields: new[] { "synonyms" });
                var item = Assert.Single(view.Items, i => i.Kind == "set_synonyms");
                await engine.SetSynonymsAsync("column:Customer/Customer Name", new[] { "client", "title" }, null, "human");
                await engine.SetSynonymsAsync("column:Customer/Customer Name", new[] { "client" }, null, "human");
                var before = await ContentAsync(sessions);

                var report = await McpTools.ApplyPlan(engine, new[] { item.Id });

                var failed = Assert.Single(report.Items);
                Assert.NotEqual("applied", failed.Status);
                Assert.Contains("changed after the plan was built", failed.Note, StringComparison.Ordinal);
                Assert.Equal(before, await ContentAsync(sessions));
            }
        }

        // A write the writer would refuse (a hidden field) is skipped on the plan with the writer's reason, before
        // anything is applied. Approved, it used to be reported as a value changed since the plan was built once another
        // item in the same apply had pruned the hidden field's entity (Microsoft's Product Sales sample, 'Sales'[Date]).
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_replace_item_on_a_hidden_field_is_skipped_with_the_hidden_reason()
        {
            var (engine, sessions) = await CustomerNameAsync();
            using (engine)
            {
                await engine.CreateColumnAsync("table:Customer", "Customer Key", "String", "Customer Key", "human");
                await sessions.Require().MutateAsync("human", "hidden twin", m =>
                {
                    m.Tables["Customer"].Columns["Customer Key"].IsHidden = true;
                    var e = (JObject)JObject.Parse(m.Cultures["en-US"].Content);
                    ((JObject)e["Entities"]).Add("customer.customer_key", JObject.Parse(PbiEnt("x", "Customer", "Customer Key", ("customer key", Gen), ("customer name key", Derived)).Substring(4)));
                    m.Cultures["en-US"].Content = e.ToString(Newtonsoft.Json.Formatting.None);
                });

                var view = await McpTools.ProposeReplace(engine, "name", "title", wholeWord: true, fields: new[] { "synonyms" });
                var hidden = Assert.Single(view.Items, i => i.ObjectRef == "column:Customer/Customer Key");
                Assert.Equal("skipped", hidden.Status);
                Assert.Contains("is hidden", hidden.Note, StringComparison.Ordinal);
                var visible = Assert.Single(view.Items, i => i.ObjectRef == "column:Customer/Customer Name");

                var report = await McpTools.ApplyPlan(engine, new[] { visible.Id });
                Assert.Equal("applied", Assert.Single(report.Items).Status);
            }
        }

        // A field hidden after the plan was built: its write is refused with the writer's own reason, not as a changed
        // value (its terms have not changed).
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_replace_item_on_a_field_hidden_since_fails_with_the_hidden_reason()
        {
            var (engine, sessions) = await CustomerNameAsync();
            using (engine)
            {
                var view = await McpTools.ProposeReplace(engine, "name", "title", wholeWord: true, fields: new[] { "synonyms" });
                var item = Assert.Single(view.Items, i => i.Kind == "set_synonyms");
                await sessions.Require().MutateAsync("human", "hide", m => m.Tables["Customer"].Columns["Customer Name"].IsHidden = true);

                var failed = Assert.Single((await McpTools.ApplyPlan(engine, new[] { item.Id })).Items);

                Assert.Equal("failed", failed.Status);
                Assert.Contains("is hidden", failed.Note, StringComparison.Ordinal);
            }
        }

        // Sol, round 5: two fields, one hidden after the plan was built. The first item's write prunes the hidden field's
        // entity, and the second still fails with the writer's reason, not as a changed value.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_field_hidden_since_fails_with_the_hidden_reason_after_another_item_prunes_it()
        {
            var (engine, sessions) = await CustomerNameAsync();
            using (engine)
            {
                await engine.CreateColumnAsync("table:Customer", "Account Name", "String", "Account Name", "human");
                await sessions.Require().MutateAsync("human", "second field", m =>
                {
                    var e = (JObject)JObject.Parse(m.Cultures["en-US"].Content);
                    ((JObject)e["Entities"]).Add("customer.account_name", JObject.Parse(PbiEnt("x", "Customer", "Account Name", ("account name", Gen), ("name", Derived)).Substring(4)));
                    m.Cultures["en-US"].Content = e.ToString(Newtonsoft.Json.Formatting.None);
                });
                var view = await McpTools.ProposeReplace(engine, "name", "title", wholeWord: true, fields: new[] { "synonyms" });
                Assert.Equal(2, view.Items.Count(i => i.Kind == "set_synonyms" && i.Status == "approved"));
                await sessions.Require().MutateAsync("human", "hide", m => m.Tables["Customer"].Columns["Account Name"].IsHidden = true);

                var report = await McpTools.ApplyPlan(engine, view.Items.Select(i => i.Id).ToArray());

                Assert.Equal("applied", report.Items.Single(i => i.ObjectRef == "column:Customer/Customer Name").Status);
                var hidden = report.Items.Single(i => i.ObjectRef == "column:Customer/Account Name");
                Assert.Equal("failed", hidden.Status);
                Assert.Contains("is hidden", hidden.Note, StringComparison.Ordinal);
            }
        }

        // Sol, round 5: the single replace's preview says when the write will be refused (a hidden field), the same
        // reason Replace all gives, rather than offering a replacement the write then refuses.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_single_replace_preview_blocks_a_write_the_writer_refuses()
        {
            var (engine, sessions) = await CustomerNameAsync();
            using (engine)
            {
                await sessions.Require().MutateAsync("human", "hide", m => m.Tables["Customer"].Columns["Customer Name"].IsHidden = true);

                var pv = await engine.ReplaceInObjectAsync(new ReplaceRequest
                {
                    Ref = "column:Customer/Customer Name", Field = "synonyms", Find = "name", Replace = "title", WholeWord = true, Preview = true,
                }, "human");

                Assert.NotNull(pv.Blocked);
                Assert.Contains("is hidden", pv.Blocked, StringComparison.Ordinal);
            }
        }

        // Sol, round 5: the removal list is read from one culture, so it is written to that culture. Propose over
        // en-US, then make en-GB the model's linguistic culture before the apply: the replace still writes en-US, and
        // en-GB's own "name" is not touched.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_replace_item_writes_the_culture_it_was_read_from()
        {
            var (engine, sessions) = await CustomerNameAsync();
            using (engine)
            {
                await sessions.Require().MutateAsync("human", "model culture", m => m.Culture = "en-US");
                var view = await McpTools.ProposeReplace(engine, "name", "title", wholeWord: true, fields: new[] { "synonyms" });
                var item = Assert.Single(view.Items, i => i.Kind == "set_synonyms");
                Assert.Equal("approved", item.Status);
                await sessions.Require().MutateAsync("human", "second culture", m =>
                {
                    var gb = m.AddTranslation("en-GB");
                    gb.Content = Lsdl(
                        PbiEnt("customer", "Customer", null, ("customer", Gen)),
                        PbiEnt("customer.customer_name", "Customer", "Customer Name", ("customer name", Gen), ("name", Derived))).Replace("en-US", "en-GB");
                    m.Culture = "en-GB";
                });

                var report = await McpTools.ApplyPlan(engine, new[] { item.Id });

                Assert.Equal("applied", Assert.Single(report.Items).Status);
                Assert.Contains(("name", "Deleted"), await TermsAsync(sessions, "Customer", "Customer Name"));
                var gbEntities = (JObject)JObject.Parse(await sessions.Require().ReadAsync(m => m.Cultures["en-GB"].Content))["Entities"];
                var gbTerms = ((JArray)gbEntities["customer.customer_name"]["Terms"]).OfType<JObject>().SelectMany(o => o.Properties())
                    .Select(p => (p.Name, (string)p.Value["State"])).ToList();
                Assert.Contains(("name", "Generated"), gbTerms);
                Assert.DoesNotContain(gbTerms, t => t.Name.Contains("title"));
            }
        }

        // Sol, round 5: undo takes an applied replace back, and the plan says so (the item is approved again, so it can
        // be applied again); redo puts it back and the item reads applied.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Undo_and_redo_of_a_replace_item_keep_the_plan_honest()
        {
            var (engine, sessions) = await CustomerNameAsync();
            using (engine)
            {
                var view = await McpTools.ProposeReplace(engine, "name", "title", wholeWord: true, fields: new[] { "synonyms" });
                var item = Assert.Single(view.Items, i => i.Kind == "set_synonyms");
                await McpTools.ApplyPlan(engine, new[] { item.Id });

                await engine.UndoAsync("human");
                Assert.Contains(("name", "Generated"), await TermsAsync(sessions, "Customer", "Customer Name"));
                Assert.Equal("approved", (await engine.GetPlanAsync()).Items.Single(i => i.Id == item.Id).Status);

                await engine.RedoAsync("human");
                Assert.Contains(("name", "Deleted"), await TermsAsync(sessions, "Customer", "Customer Name"));
                Assert.Equal("applied", (await engine.GetPlanAsync()).Items.Single(i => i.Id == item.Id).Status);

                await engine.UndoAsync("human");
                var again = await McpTools.ApplyPlan(engine, new[] { item.Id });
                Assert.Equal("applied", Assert.Single(again.Items).Status);
                Assert.Contains(("name", "Deleted"), await TermsAsync(sessions, "Customer", "Customer Name"));
            }
        }

        // ---- L1: the engine's own revived words keep the schema's authored shape -----------------------------------

        // Microsoft's Regional Sales shape: one stamped author's word, Desktop's stateless derived words, two suggestions.
        // Accepting the suggestions writes them as the author's in Desktop's shape (stateless, stamped), so the schema
        // still reads as stamped and the next list keeps Desktop's derived words. Revived as unstamped "Authored" they
        // outvoted the one stamp, and the next list tombstoned "pipeline" and "revenue".
        [Fact]
        public async Task Accepted_suggestions_keep_desktops_derived_words_desktops()
        {
            var (engine, sessions) = await ModelAsync("Stamped");
            using (engine)
            {
                var opp = await engine.CreateTableAsync("Opportunities", "human");
                await engine.CreateMeasureAsync(opp, "Revenue In Pipeline", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("opportunity", "Opportunities", null, ("opportunity", "{}")),
                    PbiEnt("opportunity.revenue_in_pipeline", "Opportunities", "Revenue In Pipeline",
                        ("revenue in pipeline", "{}"), ("Rev In Pipeline", Stamp),
                        ("pipeline", "{\"Weight\":0.97}"), ("revenue", "{\"Weight\":0.97}"),
                        ("funnel", "{\"State\":\"Suggested\",\"Weight\":0.5}"), ("pipe", "{\"State\":\"Suggested\",\"Weight\":0.5}"))));

                await engine.SetSynonymsAsync("measure:Opportunities/Revenue In Pipeline", new[] { "Rev In Pipeline", "funnel", "pipe" }, null, "agent");
                var entities = (JObject)JObject.Parse(await ContentAsync(sessions))["Entities"];
                var funnel = (JObject)((JArray)entities["opportunity.revenue_in_pipeline"]["Terms"]).OfType<JObject>().Single(o => o["funnel"] != null)["funnel"];
                Assert.True(funnel["State"] == null && funnel["LastModified"] != null && (double)funnel["Weight"] == 0.5, funnel.ToString());

                await engine.SetSynonymsAsync("measure:Opportunities/Revenue In Pipeline", new[] { "Rev In Pipeline", "funnel", "pipe", "pipeline value" }, null, "agent");
                var terms = await TermsAsync(sessions, "Opportunities", "Revenue In Pipeline");
                Assert.True(terms.Any(t => Live(t.State) && t.Term == "pipeline") && terms.Any(t => Live(t.State) && t.Term == "revenue"), Show(terms));
            }
        }

        // Sol, round 5: the schema's only stamp is on a word since deleted. The schema still reads as stamped (the
        // whole-schema vote counts it), so the author's words must carry a stamp too: an accepted suggestion written
        // stateless and unstamped would read as Power BI's, and a later list could never remove it.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_accepted_suggestion_can_be_removed_when_the_schemas_only_stamp_is_on_a_deleted_word()
        {
            var (engine, sessions) = await ModelAsync("Stamped");
            using (engine)
            {
                var opp = await engine.CreateTableAsync("Opportunities", "human");
                await engine.CreateMeasureAsync(opp, "Revenue In Pipeline", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("opportunity", "Opportunities", null, ("opportunity", "{}")),
                    PbiEnt("opportunity.revenue_in_pipeline", "Opportunities", "Revenue In Pipeline",
                        ("revenue in pipeline", "{}"), ("pipeline", "{\"Weight\":0.97}"),
                        ("Rev In Pipeline", "{\"State\":\"Deleted\",\"LastModified\":\"2021-11-05T20:09:30.477Z\"}"),
                        ("funnel", "{\"State\":\"Suggested\",\"Weight\":0.5}"))));

                await engine.SetSynonymsAsync("measure:Opportunities/Revenue In Pipeline", new[] { "funnel" }, null, "agent");
                Assert.Contains(await TermsAsync(sessions, "Opportunities", "Revenue In Pipeline"), t => t.Term == "funnel" && Live(t.State));
                await engine.SetSynonymsAsync("measure:Opportunities/Revenue In Pipeline", Array.Empty<string>(), null, "agent");

                var terms = await TermsAsync(sessions, "Opportunities", "Revenue In Pipeline");
                Assert.True(terms.Contains(("funnel", "Deleted")) && terms.Any(t => t.Term == "pipeline" && Live(t.State)), Show(terms));
            }
        }

        // Sol, round 5: in a schema that does not read as stamped, a revived word is an explicit "Authored" (and loses any
        // stamp it kept), so it never moves the reading toward stamped. Revived stateless, it let the author's own
        // deletion of "b" tie the vote, and the next list could no longer remove c, d, e and f.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_revived_word_keeps_an_unstamped_schema_unstamped()
        {
            var (engine, sessions) = await ModelAsync("Plain");
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateMeasureAsync(sales, "Revenue", "1", "human");
                const string Authored = "{\"State\":\"Authored\"}";
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("sale", "Sales", null, ("sale", Gen)),
                    PbiEnt("sale.revenue", "Sales", "Revenue", ("revenue", Gen), ("a", Authored), ("b", Authored), ("c", "{}"), ("d", "{}"), ("e", "{}"),
                        ("gone", "{\"State\":\"Deleted\",\"LastModified\":\"2021-11-05T20:09:30.477Z\"}"),
                        ("f", "{\"State\":\"Suggested\",\"Weight\":0.5}"))));

                await engine.SetSynonymsAsync("measure:Sales/Revenue", new[] { "a", "c", "d", "e", "f" }, null, "agent");
                var entities = (JObject)JObject.Parse(await ContentAsync(sessions))["Entities"];
                var f = (JObject)((JArray)entities["sale.revenue"]["Terms"]).OfType<JObject>().Single(o => o["f"] != null)["f"];
                Assert.True((string)f["State"] == "Authored" && f["LastModified"] == null, f.ToString());
                await engine.SetSynonymsAsync("measure:Sales/Revenue", new[] { "a" }, null, "agent");

                var terms = await TermsAsync(sessions, "Sales", "Revenue");
                Assert.True(new[] { "c", "d", "e", "f" }.All(w => terms.Contains((w, "Deleted"))) && terms.Contains(("a", "Authored")), Show(terms));
            }
        }

        // Sol, round 5: a stamped schema whose authored template carries a State (a tied state vote) still stamps the
        // engine's authored words, so accepting a suggestion adds a stamp rather than an unstamped "Authored" that
        // outvotes the schema's one stamp and turns Desktop's "pipeline" into the author's.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_stamped_schema_stamps_the_engines_authored_words_whatever_their_state()
        {
            var (engine, sessions) = await ModelAsync("Stamped");
            using (engine)
            {
                var opp = await engine.CreateTableAsync("Opportunities", "human");
                await engine.CreateMeasureAsync(opp, "Revenue In Pipeline", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("opportunity", "Opportunities", null, ("opportunity", Gen)),
                    PbiEnt("opportunity.revenue_in_pipeline", "Opportunities", "Revenue In Pipeline",
                        ("revenue in pipeline", Gen), ("pipeline", "{\"Weight\":0.97}"), ("client", "{\"State\":\"Authored\"}"),
                        ("Rev In Pipeline", "{\"State\":\"Deleted\",\"LastModified\":\"2021-11-05T20:09:30.477Z\"}"),
                        ("funnel", "{\"State\":\"Suggested\",\"Weight\":0.5}"))));

                await engine.SetSynonymsAsync("measure:Opportunities/Revenue In Pipeline", new[] { "client", "funnel" }, null, "agent");
                var entities = (JObject)JObject.Parse(await ContentAsync(sessions))["Entities"];
                var funnel = (JObject)((JArray)entities["opportunity.revenue_in_pipeline"]["Terms"]).OfType<JObject>().Single(o => o["funnel"] != null)["funnel"];
                Assert.True(funnel["LastModified"] != null, funnel.ToString());
                await engine.SetSynonymsAsync("measure:Opportunities/Revenue In Pipeline", new[] { "client", "funnel", "deal flow" }, null, "agent");

                var terms = await TermsAsync(sessions, "Opportunities", "Revenue In Pipeline");
                Assert.True(terms.Any(t => t.Term == "pipeline" && Live(t.State)), Show(terms));
            }
        }

        // In a schema without stamps a revived word is still written "State":"Authored", the shape it has always had.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_revived_word_in_a_schema_without_stamps_is_authored()
        {
            var (engine, sessions) = await CustomerNameAsync();
            using (engine)
            {
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("customer", "Customer", null, ("customer", Gen)),
                    PbiEnt("customer.customer_name", "Customer", "Customer Name", ("customer name", Gen), ("client", "{\"State\":\"Authored\"}"),
                        ("buyer", "{\"State\":\"Suggested\",\"Weight\":0.5}"))));

                await engine.SetSynonymsAsync("column:Customer/Customer Name", new[] { "client", "buyer" }, null, "agent");

                Assert.Contains(("buyer", "Authored"), await TermsAsync(sessions, "Customer", "Customer Name"));
            }
        }

        // ---- L2: D1's bracket reading on its own -----------------------------------------------------------------

        // An entity holding only Desktop's bare form of a bracketed name ("average lead time" for 'Average Lead Time
        // (Days)'). That term is the name, so the entity is not stale and a synonym write keeps it.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_bracketed_names_bare_term_alone_is_still_its_name()
        {
            var (engine, sessions) = await ModelAsync("Shop");
            using (engine)
            {
                var all = await engine.CreateTableAsync("All Measure", "human");
                await engine.CreateMeasureAsync(all, "Average Lead Time (Days)", "1", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("all_measure", "All Measure", null, ("all measure", Gen)),
                    PbiEnt("all_measure.average_lead_time", "All Measure", "Average Lead Time (Days)", ("average lead time", Gen))));

                await engine.SetSynonymsAsync("measure:All Measure/Average Lead Time (Days)", new[] { "lead time" }, null, "agent");

                var terms = await TermsAsync(sessions, "All Measure", "Average Lead Time (Days)");
                Assert.True(terms.Contains(("average lead time", "Generated")) && terms.Count(t => Live(t.State)) == 2, Show(terms));
            }
        }

        // ---- L3: D5's stamp reading, on the scan side and the rename side ----------------------------------------

        // 'Customer'[Country] and 'Store'[Store Country], Desktop's stamped shape: Store Country's stateless unstamped
        // "country" is Power BI's old name, so the clash is reported through it, on the field that carries it.
        private static async Task<(LocalEngine Engine, SessionManager Sessions)> StampedCountryAsync()
        {
            var (engine, sessions) = await ModelAsync("Copilot");
            var customer = await engine.CreateTableAsync("Customer", "human");
            await engine.CreateColumnAsync(customer, "Country", "String", "Country", "human");
            var store = await engine.CreateTableAsync("Store", "human");
            await engine.CreateColumnAsync(store, "Store Country", "String", "Store Country", "human");
            await InjectAsync(sessions, Lsdl(
                PbiEnt("customer", "Customer", null, ("customer", "{}")),
                PbiEnt("customer.country", "Customer", "Country", ("country", "{}"), ("nation", Stamp)),
                PbiEnt("store", "Store", null, ("store", "{}")),
                PbiEnt("store.country", "Store", "Store Country", ("country", "{}"))));
            return (engine, sessions);
        }

        private static ReadinessFinding CollisionFinding(SessionManager sessions)
        {
            PrepForAiReader.Invalidate(sessions.Current.Model);
            var eval = ReadinessRuleSet.Default().Single(r => r.Id == "DAC-QNA-NAME-COLLISIONS").Evaluate(sessions.Current.Model);
            return Assert.Single(eval.Violations);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_scan_reads_a_stateless_old_name_as_stale_in_a_stamped_schema()
        {
            var (engine, sessions) = await StampedCountryAsync();
            using (engine)
            {
                var f = CollisionFinding(sessions);
                Assert.Contains("only through an old name term", f.Message, StringComparison.Ordinal);
                Assert.Equal("column:Store/Store Country", f.ObjectRef);
            }
        }

        // A Semanticus rename in a stamped schema drops the old name's stateless unstamped derived word ("key") and keeps
        // the author's stamped word.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_rename_in_a_stamped_schema_drops_the_old_names_stateless_derived_word()
        {
            var (engine, sessions) = await ModelAsync("Stamped");
            using (engine)
            {
                var customer = await engine.CreateTableAsync("Customer", "human");
                await engine.CreateColumnAsync(customer, "Customer Key", "String", "Customer Key", "human");
                await InjectAsync(sessions, Lsdl(
                    PbiEnt("customer", "Customer", null, ("customer", "{}")),
                    PbiEnt("customer.customer_key", "Customer", "Customer Key", ("customer key", "{}"), ("key", "{\"Weight\":0.97}"), ("cust id", Stamp))));

                await engine.RenameObjectAsync("column:Customer/Customer Key", "Client Code", "agent");

                var terms = await TermsAsync(sessions, "Customer", "Client Code");
                Assert.True(!terms.Any(t => Live(t.State) && t.Term == "key") && terms.Any(t => Live(t.State) && t.Term == "cust id"), Show(terms));
            }
        }

        // ---- L4: D4's note says what an old name term is ---------------------------------------------------------

        // An old name term is left by a rename that did not update the schema, wherever that rename was made (a
        // Semanticus rename before round 3's drop, or one in another tool). The note says that, not "made outside
        // Semanticus", and names the way out.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_old_name_term_note_says_a_rename_left_it_and_how_to_clear_it()
        {
            var (engine, sessions) = await StampedCountryAsync();
            using (engine)
            {
                var f = CollisionFinding(sessions);
                Assert.EndsWith(" An old name term is a name the linguistic schema still records for a field after a rename that did not update it."
                    + " Setting that field's synonyms without the old word clears it.", f.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("outside Semanticus", f.Message, StringComparison.Ordinal);
            }
        }
    }
}
