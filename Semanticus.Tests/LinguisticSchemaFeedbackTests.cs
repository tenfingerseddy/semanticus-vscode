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
    /// Three linguistic-schema defects found by driving a real Northstar model (27 Sep dogfooding feedback, items 2,
    /// 3 and 5). Copilot and data agents read this schema, so each one changes what they understand:
    /// (2) rename_object followed the entity binding but kept the OLD Generated name term, so the collision the
    ///     rename was meant to fix survived in the schema, and DAC-QNA-NAME-COLLISIONS (names only) called it fixed;
    /// (3) set_synonyms created a new entity in the donor's shape (top-level Binding, no Generated name term,
    ///     LastModified stamps, case-kept key) instead of the shape and key style every other entity in the model
    ///     uses; and it marked the object's own Generated name term Deleted whenever the caller omitted it;
    /// (5) DIM-PRIMARY-NAME recommended renaming 'Product Name' to 'Product', which DAC-QNA-NAME-COLLISIONS then
    ///     flags High against the Product table. No rule may recommend a change another rule flags High.
    /// The LSDL below is copied from the real Northstar model's en-US culture (a subset of its entities).
    /// </summary>
    public sealed class LinguisticSchemaFeedbackTests : IClassFixture<TemplateCopies>
    {
        private readonly TemplateCopies _copies;
        public LinguisticSchemaFeedbackTests(TemplateCopies copies) => _copies = copies;

        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        // ---- fixture ---------------------------------------------------------------------------------------

        /// <summary>A Northstar-shaped model. The raw model carries the two collisions the real one shipped with
        /// ('Date'[Date] against the Date table; 'Variance to Budget %' against 'Variance to Budget', since '%' is
        /// punctuation to the term normaliser); the clean one carries the names those collisions were renamed to.</summary>
        private async Task<(LocalEngine Engine, SessionManager Sessions)> NorthstarAsync(bool raw)
        {
            var sessions = new SessionManager();
            return (await _copies.OpenFromFixtureAsync(raw ? "northstar-raw" : "northstar-clean", f => BuildNorthstarAsync(f, raw), new Fake(), sessions), sessions);
        }

        private static async Task BuildNorthstarAsync(DecisionFixture f, bool raw)
        {
            var engine = f.Engine;
            var sessions = f.Sessions;
            await engine.CreateModelAsync("Northstar", 1604);
            var sales = await engine.CreateTableAsync("Sales", "human");
            var salesProductKey = await engine.CreateColumnAsync(sales, "Product Key", "Int64", "Product Key", "human");
            await engine.CreateMeasureAsync(sales, "Net Sales", "1", "human");
            await engine.CreateMeasureAsync(sales, "Margin %", "1", "human");
            await engine.CreateMeasureAsync(sales, "Return Rate %", "1", "human");
            var budget = await engine.CreateTableAsync("Budget", "human");
            await engine.CreateMeasureAsync(budget, "Budget Amount", "1", "human");
            await engine.CreateMeasureAsync(budget, "Variance to Budget", "1", "human");
            await engine.CreateMeasureAsync(budget, raw ? "Variance to Budget %" : "Variance to Budget Percent", "1", "human");
            var date = await engine.CreateTableAsync("Date", "human");
            await engine.CreateColumnAsync(date, raw ? "Date" : "Calendar Date", "DateTime", "Date", "human");
            await engine.CreateColumnAsync(date, "Year", "Int64", "Year", "human");
            var product = await engine.CreateTableAsync("Product", "human");
            var productKey = await engine.CreateColumnAsync(product, "Product Key", "Int64", "Product Key", "human");
            await engine.CreateColumnAsync(product, "Product Name", "String", "Product Name", "human");
            var selector = await engine.CreateTableAsync("Metric Selector", "human");
            await engine.CreateColumnAsync(selector, "Metric", "String", "Metric", "human");
            await engine.CreateRelationshipAsync(salesProductKey, productKey, null, true, "human");
            await sessions.Require().MutateAsync("human", "hide keys", m =>
            {
                m.Tables["Sales"].Columns["Product Key"].IsHidden = true;
                m.Tables["Product"].Columns["Product Key"].IsHidden = true;
            });
        }

        private static string Ent(string key, string table, string prop, params string[] terms)
        {
            var binding = prop == null
                ? $"{{\"ConceptualEntity\":\"{table}\"}}"
                : $"{{\"ConceptualEntity\":\"{table}\",\"ConceptualProperty\":\"{prop}\"}}";
            // First term is the Generated name term, the rest are Authored: the real model's shape exactly.
            var t = terms.Select((x, i) => $"{{\"{x}\":{{\"State\":\"{(i == 0 ? "Generated" : "Authored")}\"}}}}");
            return $"\"{key}\":{{\"Definition\":{{\"Binding\":{binding}}},\"Terms\":[{string.Join(",", t)}]}}";
        }

        private static string Lsdl(params string[] entities) =>
            "{\"Version\":\"1.0.0\",\"Language\":\"en-US\",\"DynamicImprovement\":\"HighConfidence\",\"Entities\":{"
            + string.Join(",", entities) + "}}";

        /// <summary>The entities shared by the raw and clean fixtures, verbatim from the real model.</summary>
        private static readonly string[] Shared =
        {
            Ent("sales", "Sales", null, "sales", "transactions", "sales lines", "orders"),
            Ent("sales.net_sales", "Sales", "Net Sales", "net sales", "revenue", "turnover", "sales", "net revenue"),
            Ent("sales.margin_percent", "Sales", "Margin %", "margin %", "margin", "margin percent", "gross margin"),
            Ent("sales.return_rate_percent", "Sales", "Return Rate %", "return rate %", "return rate"),
            Ent("budget", "Budget", null, "budget", "plan"),
            Ent("budget.budget_amount", "Budget", "Budget Amount", "budget amount", "budget"),
            Ent("budget.variance_to_budget", "Budget", "Variance to Budget", "variance to budget", "variance"),
            Ent("date", "Date", null, "date", "calendar"),
            Ent("date.year", "Date", "Year", "year", "calendar year"),
            Ent("product", "Product", null, "product", "products"),
            Ent("product.product_name", "Product", "Product Name", "product name", "product"),
            Ent("metric_selector", "Metric Selector", null, "metric selector"),
        };

        private static string RawLsdl() => Lsdl(Shared.Concat(new[]
        {
            // As in the real model, the column already carried the word it was later renamed to.
            Ent("date.date", "Date", "Date", "date", "day", "calendar date"),
            Ent("budget.variance_to_budget_percent", "Budget", "Variance to Budget %", "variance to budget %", "variance percent"),
        }).ToArray());

        private static string CleanLsdl() => Lsdl(Shared.Concat(new[]
        {
            Ent("date.calendar_date", "Date", "Calendar Date", "calendar date", "day"),
            Ent("budget.variance_to_budget_percent", "Budget", "Variance to Budget Percent", "variance to budget percent", "variance percent"),
        }).ToArray());

        private static Task InjectAsync(SessionManager sessions, string content) =>
            sessions.Require().MutateAsync("human", "seed linguistic schema", m =>
            {
                var cult = m.Cultures.Contains("en-US") ? m.Cultures["en-US"] : m.AddTranslation("en-US");
                cult.Content = content;
            });

        private static async Task<JObject> EntitiesAsync(SessionManager sessions) =>
            (JObject)JObject.Parse(await sessions.Require().ReadAsync(m => m.Cultures["en-US"].Content))["Entities"];

        /// <summary>Every entity bound to (table[, prop]), whatever its key.</summary>
        private static List<JObject> Bound(JObject entities, string table, string prop) =>
            entities.Properties().Select(p => (JObject)p.Value).Where(e =>
            {
                var b = (JObject)(e["Definition"]?["Binding"] ?? e["Binding"]);
                return (string)b["ConceptualEntity"] == table && (string)b["ConceptualProperty"] == prop;
            }).ToList();

        private static List<(string Term, string State)> Terms(JObject entity) =>
            ((JArray)entity["Terms"]).OfType<JObject>().SelectMany(o => o.Properties())
                .Select(p => (p.Name, (string)p.Value["State"] ?? "Authored")).ToList();

        private static RuleEvaluation Eval(SessionManager sessions, string id)
        {
            PrepForAiReader.Invalidate(sessions.Current.Model);   // the reader memoizes per model; an injected LSDL must show
            return ReadinessRuleSet.Default().Single(r => r.Id == id).Evaluate(sessions.Current.Model);
        }

        // ---- item 2: a rename regenerates the Generated name term ------------------------------------------

        [Fact]
        public async Task Rename_regenerates_the_generated_name_term_and_keeps_authored_synonyms()
        {
            var (engine, sessions) = await NorthstarAsync(raw: true);
            using (engine)
            {
                await InjectAsync(sessions, RawLsdl());

                await engine.RenameObjectAsync("column:Date/Date", "Calendar Date", "agent");
                await engine.RenameObjectAsync("measure:Budget/Variance to Budget %", "Variance to Budget Percent", "agent");

                var entities = await EntitiesAsync(sessions);
                // The name term stays first and Generated (Power BI's shape); the authored "calendar date" it now
                // spells is folded into it rather than written twice.
                var column = Assert.Single(Bound(entities, "Date", "Calendar Date"));
                Assert.Equal(new[] { ("calendar date", "Generated"), ("day", "Authored") }, Terms(column));

                var measure = Assert.Single(Bound(entities, "Budget", "Variance to Budget Percent"));
                Assert.Equal(new[] { ("variance to budget percent", "Generated"), ("variance percent", "Authored") }, Terms(measure));

                // A table rename regenerates the table entity's own name term; its authored words stay.
                await engine.RenameObjectAsync("table:Sales", "Sales Line Items", "agent");
                entities = await EntitiesAsync(sessions);
                var table = Assert.Single(Bound(entities, "Sales Line Items", null));
                Assert.Equal(new[] { ("sales line items", "Generated"), ("transactions", "Authored"), ("sales lines", "Authored"), ("orders", "Authored") }, Terms(table));
                // ...and its child entities' terms are untouched (a table rename does not rename a measure).
                Assert.Equal("net sales", Terms(Assert.Single(Bound(entities, "Sales Line Items", "Net Sales")))[0].Term);

                // Same for a table renamed onto a word the person already authored: ONE copy, as the name term.
                await engine.RenameObjectAsync("table:Date", "Calendar", "agent");
                entities = await EntitiesAsync(sessions);
                Assert.Equal(new[] { ("calendar", "Generated") }, Terms(Assert.Single(Bound(entities, "Calendar", null))));
            }
        }

        [Fact]
        public async Task Rename_then_scan_reports_no_collision_and_undo_restores_the_old_term()
        {
            var (engine, sessions) = await NorthstarAsync(raw: true);
            using (engine)
            {
                await InjectAsync(sessions, RawLsdl());
                // 'Variance to Budget %' beside 'Variance to Budget'. 'Date'[Date] beside the Date table is not a clash
                // (Power BI Desktop saves it; Kane's ruling, 27 Sep).
                Assert.Single(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);

                await engine.RenameObjectAsync("column:Date/Date", "Calendar Date", "agent");
                await engine.RenameObjectAsync("measure:Budget/Variance to Budget %", "Variance to Budget Percent", "agent");
                Assert.Empty(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);

                // The term rewrite rides the rename's undo step.
                await engine.UndoAsync("agent");
                var entities = await EntitiesAsync(sessions);
                Assert.Equal("variance to budget %", Terms(Assert.Single(Bound(entities, "Budget", "Variance to Budget %")))[0].Term);
            }
        }

        [Fact]
        public async Task Name_collision_rule_also_checks_the_generated_name_terms()
        {
            // The state a pre-fix rename left behind: names are clean, but each renamed entity still carries its old
            // Generated term, which collides with another visible object. A names-only scan called this fixed.
            var (engine, sessions) = await NorthstarAsync(raw: false);
            using (engine)
            {
                await InjectAsync(sessions, Lsdl(Shared.Concat(new[]
                {
                    Ent("date.date", "Date", "Calendar Date", "date", "day"),
                    Ent("budget.variance_to_budget_percent", "Budget", "Variance to Budget Percent", "variance to budget %", "variance percent"),
                }).ToArray()));

                // The renamed column's old "date" now meets only the Date table, and a table beside its own field is not
                // a clash (27 Sep ruling), so the one clash is the measure's old term against its visible sibling.
                var ev = Eval(sessions, "DAC-QNA-NAME-COLLISIONS");
                Assert.Single(ev.Violations);
                var variance = Assert.Single(ev.Violations, v => v.Message.Contains("'variance to budget'", StringComparison.Ordinal));
                Assert.Contains("measure 'Budget'[Variance to Budget Percent]", variance.Message, StringComparison.Ordinal);
                Assert.Contains("measure 'Budget'[Variance to Budget]", variance.Message, StringComparison.Ordinal);

                // Authored words shared across objects ("sales", "product", "budget") are ordinary synonyms, not
                // name collisions: the clean schema carries several and must scan clean.
                await InjectAsync(sessions, CleanLsdl());
                Assert.Empty(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);
            }
        }

        // ---- item 3: set_synonyms matches the model's entity shape and key style ---------------------------

        [Fact]
        public async Task Set_synonyms_creates_an_entity_in_the_models_own_shape_and_key_style()
        {
            var (engine, sessions) = await NorthstarAsync(raw: false);
            using (engine)
            {
                await InjectAsync(sessions, CleanLsdl());
                await engine.SetSynonymsAsync("column:Metric Selector/Metric", new[] { "measure picker", "kpi" }, null, "agent");

                var entities = await EntitiesAsync(sessions);
                Assert.False(entities.ContainsKey("Metric_Selector.Metric"));
                var created = (JObject)entities["metric_selector.metric"];
                Assert.NotNull(created);
                var expected = JObject.Parse(
                    "{\"Definition\":{\"Binding\":{\"ConceptualEntity\":\"Metric Selector\",\"ConceptualProperty\":\"Metric\"}}," +
                    "\"Terms\":[{\"metric\":{\"State\":\"Generated\"}},{\"measure picker\":{\"State\":\"Authored\"}},{\"kpi\":{\"State\":\"Authored\"}}]}");
                Assert.True(JToken.DeepEquals(expected, created), created.ToString());
            }
        }

        [Fact]
        public async Task Set_synonyms_round_trips_every_entity_of_a_real_model()
        {
            // Delete each entity in turn and recreate it with set_synonyms from its own authored words. The
            // recreated entity must come back under the same key, byte-for-byte the same JSON.
            var (engine, sessions) = await NorthstarAsync(raw: false);
            using (engine)
            {
                var original = (JObject)JObject.Parse(CleanLsdl())["Entities"];
                foreach (var prop in original.Properties())
                {
                    var entity = (JObject)prop.Value;
                    var b = (JObject)entity["Definition"]["Binding"];
                    var table = (string)b["ConceptualEntity"];
                    var field = (string)b["ConceptualProperty"];
                    var objRef = await sessions.Require().ReadAsync(m => field == null ? $"table:{table}"
                        : m.Tables[table].Measures.Contains(field) ? $"measure:{table}/{field}" : $"column:{table}/{field}");

                    var without = JObject.Parse(CleanLsdl());
                    ((JObject)without["Entities"]).Remove(prop.Name);
                    await InjectAsync(sessions, without.ToString(Newtonsoft.Json.Formatting.None));

                    var authored = Terms(entity).Where(t => t.State == "Authored").Select(t => t.Term).ToArray();
                    await engine.SetSynonymsAsync(objRef, authored, null, "agent");

                    var after = await EntitiesAsync(sessions);
                    Assert.True(after.ContainsKey(prop.Name), $"{objRef}: expected key '{prop.Name}', got [{string.Join(", ", after.Properties().Select(p => p.Name).Except(original.Properties().Select(p => p.Name)))}]");
                    Assert.True(JToken.DeepEquals(entity, after[prop.Name]), $"{objRef}: expected {entity.ToString(Newtonsoft.Json.Formatting.None)} got {after[prop.Name].ToString(Newtonsoft.Json.Formatting.None)}");
                }
            }
        }

        [Fact]
        public async Task Set_synonyms_on_an_existing_entity_keeps_its_generated_name_term()
        {
            var (engine, sessions) = await NorthstarAsync(raw: false);
            using (engine)
            {
                await InjectAsync(sessions, CleanLsdl());
                await engine.SetSynonymsAsync("measure:Sales/Net Sales", new[] { "revenue", "takings" }, null, "agent");

                var terms = Terms((JObject)(await EntitiesAsync(sessions))["sales.net_sales"]);
                Assert.Contains(("net sales", "Generated"), terms);    // the object's own name stays: omitting it is not deleting it
                Assert.Contains(("revenue", "Authored"), terms);
                Assert.Contains(("takings", "Authored"), terms);
                Assert.Contains(("turnover", "Deleted"), terms);       // a dropped authored word is tombstoned, as before
            }
        }

        [Fact]
        public async Task Set_synonyms_on_a_fresh_schema_writes_the_power_bi_shape()
        {
            var (engine, sessions) = await NorthstarAsync(raw: false);
            using (engine)
            {
                await engine.CreateLinguisticSchemaAsync(null, "agent");
                await engine.SetSynonymsAsync("column:Product/Product Name", new[] { "item" }, null, "agent");

                var entities = await EntitiesAsync(sessions);
                var created = (JObject)entities["product.product_name"];
                Assert.NotNull(created);
                Assert.Null(created["Binding"]);
                Assert.Equal("Product Name", (string)created["Definition"]["Binding"]["ConceptualProperty"]);
                var terms = Terms(created);
                Assert.Equal(("product name", "Generated"), terms[0]);
                Assert.Contains(("item", "Authored"), terms);
                Assert.DoesNotContain("LastModified", created.ToString());
            }
        }

        [Fact]
        public async Task Rename_and_set_synonyms_on_power_bi_desktop_shapes_undo_to_the_same_bytes()
        {
            // Power BI Desktop's own term shapes: a plural table's singular name term, a camelCase column's split term
            // plus its as-written copy, and a measure whose terms lost their State after an author edit. Every edit
            // below changes the schema, and each undo must put back exactly the bytes that were there.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Contoso", 1604);
                var stores = await engine.CreateTableAsync("Stores", "human");
                await engine.CreateColumnAsync(stores, "StoreName", "String", "StoreName", "human");
                await engine.CreateMeasureAsync(stores, "Store Count", "1", "human");
                const string gen = "{\"State\":\"Generated\"}";
                string E(string key, string prop, params string[] terms) =>
                    $"\"{key}\":{{\"Definition\":{{\"Binding\":{{\"ConceptualEntity\":\"Stores\"{(prop == null ? "" : $",\"ConceptualProperty\":\"{prop}\"")}}}}},\"State\":\"Generated\",\"Terms\":[{string.Join(",", terms)}]}}";
                await InjectAsync(sessions, Lsdl(
                    E("store", null, $"{{\"store\":{gen}}}", "{\"shop\":{\"Type\":\"Noun\",\"State\":\"Suggested\",\"Weight\":0.7}}"),
                    E("store.store_name", "StoreName", $"{{\"store name\":{gen}}}", "{\"name\":{\"State\":\"Generated\",\"Weight\":0.97}}",
                        "{\"StoreName\":{\"State\":\"Generated\",\"Type\":\"Noun\",\"Weight\":0.99}}"),
                    E("store.store_count", "Store Count", "{\"store count\":{}}", "{\"outlets\":{\"LastModified\":\"2025-08-26T10:57:21.382Z\"}}")));
                async Task<string> Content() => await sessions.Require().ReadAsync(m => m.Cultures["en-US"].Content);

                foreach (var edit in new Func<Task>[]
                {
                    () => engine.RenameObjectAsync("table:Stores", "Outlets", "agent"),
                    () => engine.RenameObjectAsync("column:Stores/StoreName", "Outlet Name", "agent"),
                    () => engine.RenameObjectAsync("measure:Stores/Store Count", "Number of Stores", "agent"),
                    () => engine.SetSynonymsAsync("table:Stores", new[] { "shop", "branch" }, null, "agent"),
                    () => engine.SetSynonymsAsync("column:Stores/StoreName", new[] { "shop name" }, null, "agent"),
                    () => engine.SetSynonymsAsync("measure:Stores/Store Count", new[] { "branches" }, null, "agent"),
                })
                {
                    var before = await Content();
                    await edit();
                    Assert.NotEqual(before, await Content());
                    await engine.UndoAsync("agent");
                    Assert.Equal(before, await Content());
                }
            }
        }

        // ---- item 5: no rule recommends a rename another rule flags High ------------------------------------

        [Fact]
        public async Task A_plan_that_creates_the_linguistic_schema_sets_the_row_label_instead_of_a_rename()
        {
            // DIM-PRIMARY-NAME advised renaming 'Product Name' to 'Product', which is what the collision rule then
            // flagged. It now sets the row label (27 Sep ruling), which collides with nothing, so the plan carries both
            // the schema and the row label, and no rename for the rule.
            var (engine, sessions) = await NorthstarAsync(raw: false);
            using (engine)
            {
                var plan = await engine.ProposePlanAsync(null, includeAi: true, maxAiItems: 200, origin: "agent");
                Assert.Contains(plan.Items, i => i.Kind == "create_linguistic_schema");
                Assert.Contains(plan.Items, i => i.RuleId == "DIM-PRIMARY-NAME" && i.Kind == "set_default_label" && i.ObjectRef == "column:Product/Product Name");
                Assert.DoesNotContain(plan.Items, i => i.RuleId == "DIM-PRIMARY-NAME" && i.Kind == "rename");
                await engine.ApplyPlanAsync(null, "agent");
                Assert.Empty(Eval(sessions, "DIM-PRIMARY-NAME").Violations);
                Assert.Empty(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);
            }
        }

        /// <summary>Every readiness rule whose finding asks for a rename, split by whether it names the new name. A
        /// rule that names ONE target is the one that can recommend a change another rule then flags High, so each is
        /// followed below; a rule that leaves the name to the person or the assistant has no advice to follow. The
        /// split is checked against the rules themselves, not their wording (see
        /// <see cref="Every_rename_rule_is_classified_by_its_category_and_its_plan_item"/>).</summary>
        private static readonly Dictionary<string, Func<ReadinessFinding, string>> DeterministicRenames = new(StringComparer.Ordinal)
        {
            // "drop the prefix": 'Dim Customer' reads 'Customer'.
            ["NAME-TECH-PREFIX"] = f => f.ObjectName.Substring(f.ObjectName.IndexOfAny(new[] { ' ', '_', '-' }) + 1),
            // "clean it": edge spaces, tabs, line breaks and emoji out.
            ["NAME-INVALID-CHARS"] = f => string.Join(" ", new string(f.ObjectName.Where(c => !char.IsSurrogate(c))
                .Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries)),
        };

        private static readonly HashSet<string> OpenRenames = new(StringComparer.Ordinal)
        {
            "NAME-MEASURE", "NAME-COLUMN", "NAME-TABLE", "NAME-HIERARCHY", "NAME-COLUMN-ID", "NAME-GENERIC", "NAME-DUP",
            "DAC-QNA-NAME-COLLISIONS",
        };

        /// <summary>Naming rules that advise no rename at all. DIM-PRIMARY-NAME sets the dimension's row label (27 Sep). DAX-REFERENCE is a formula that names an object the model does not have (plan statement check 4):
        /// it sits in Naming for the score, and the way out is to fix the formula, not to rename anything.</summary>
        private static readonly HashSet<string> NotRenames = new(StringComparer.Ordinal) { "DIM-PRIMARY-NAME", "DAX-REFERENCE" };

        /// <summary>A model that trips every deterministic rename rule where following it is safe, and carries the two
        /// shapes that used to be called collisions: 'Dim Product' labelled by a visible 'Product' column, and a
        /// 'Store' dimension labelled 'Store Name'. 'cust_nm' is a codey column, so the plan carries a rename for it.</summary>
        private async Task<(LocalEngine Engine, SessionManager Sessions)> RenameRulesModelAsync(int cl, bool schema)
        {
            var sessions = new SessionManager();
            return (await _copies.OpenFromFixtureAsync("renames-" + cl + "-" + schema, f => BuildRenameRulesAsync(f, cl, schema), new Fake(), sessions), sessions);
        }

        private static async Task BuildRenameRulesAsync(DecisionFixture f, int cl, bool schema)
        {
            var engine = f.Engine;
            var sessions = f.Sessions;
            if (cl < 1465) await TestModels.OpenEmptyBimAsync(engine, "Renames", cl);
            else await engine.CreateModelAsync("Renames", cl);
            var sales = await engine.CreateTableAsync("Sales", "human");
            var fks = new List<string>();
            foreach (var k in new[] { "Customer Key", "Product Key", "Store Key" })
                fks.Add(await engine.CreateColumnAsync(sales, k, "Int64", k, "human"));
            await engine.CreateMeasureAsync(sales, "Net Sales", "1", "human");
            await engine.CreateMeasureAsync(sales, "Gross Margin ", "1", "human");   // NAME-INVALID-CHARS: trailing space
            await engine.CreateColumnAsync(sales, "cust_nm", "String", "cust_nm", "human");   // NAME-COLUMN: the assistant names it
            var pks = new List<string>();
            foreach (var (table, key, label) in new[] { ("Dim Customer", "Customer Key", "Customer Name"), ("Dim Product", "Product Key", "Product"), ("Store", "Store Key", "Store Name") })
            {
                var t = await engine.CreateTableAsync(table, "human");
                pks.Add(await engine.CreateColumnAsync(t, key, "Int64", key, "human"));
                await engine.CreateColumnAsync(t, label, "String", label, "human");
            }
            for (var i = 0; i < fks.Count; i++) await engine.CreateRelationshipAsync(fks[i], pks[i], null, true, "human");
            await sessions.Require().MutateAsync("human", "hide keys", m =>
            {
                foreach (var t in m.Tables) foreach (var c in t.Columns.Where(c => c.Name.EndsWith(" Key", StringComparison.Ordinal))) c.IsHidden = true;
            });
            if (schema) await engine.CreateLinguisticSchemaAsync(null, "agent");
        }

        private static Scorecard Scan(SessionManager sessions)
        {
            PrepForAiReader.Invalidate(sessions.Current.Model);
            return new ReadinessAnalyzer().Analyze(sessions.Current.Model);
        }

        private static Dictionary<string, int> HighCounts(Scorecard card) =>
            card.Findings.Where(f => f.Severity == "High").GroupBy(f => f.RuleId).ToDictionary(g => g.Key, g => g.Count());

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task No_rule_recommends_a_rename_that_another_rule_flags_high()
        {
            // Every pair at once: follow each deterministic rename (and, where SYN-SCHEMA asks for a schema, follow that
            // too), rescan the WHOLE ruleset, and no High rule may fire more often than before. Three model states,
            // because the collision rule's reach differs in each: a schema engaged; a 1465+ model with none (SYN-SCHEMA,
            // High, asks for one); and a pre-1465 model where no linguistic schema is possible.
            var followed = DeterministicRenames.Keys.ToDictionary(k => k, _ => 0);
            foreach (var (cl, schema) in new[] { (1604, true), (1604, false), (1400, false) })
            {
                var (engine, sessions) = await RenameRulesModelAsync(cl, schema);
                using (engine)
                {
                    var card = Scan(sessions);

                    // 'Dim Product' is advised again: dropping its prefix puts the table beside its own 'Product' column,
                    // which is not a clash (27 Sep ruling), and the rescan below proves no High rule fires more often.
                    Assert.Contains(card.Findings, f => f.RuleId == "NAME-TECH-PREFIX" && f.ObjectName == "Dim Product");

                    var before = HighCounts(card);
                    var clashesBefore = card.Findings.Count(x => x.RuleId == "DAC-QNA-NAME-COLLISIONS");
                    var schemaAdvised = card.Findings.Any(f => f.RuleId == "SYN-SCHEMA");
                    foreach (var f in card.Findings.Where(f => DeterministicRenames.ContainsKey(f.RuleId)).ToList())
                    {
                        var target = DeterministicRenames[f.RuleId](f);
                        await engine.RenameObjectAsync(f.ObjectRef, target, "agent");
                        var steps = 1;
                        if (schemaAdvised) { await engine.CreateLinguisticSchemaAsync(null, "agent"); steps++; }
                        var rescan = Scan(sessions);
                        var after = HighCounts(rescan);
                        foreach (var (rule, n) in after)
                            Assert.True(n <= (before.TryGetValue(rule, out var b) ? b : 0) || (schemaAdvised && rule == "SYN-SCHEMA"),
                                $"following {f.RuleId} ('{f.ObjectName}' to '{target}') at CL {cl}, schema {schema}: {rule} now fires {n} time(s) High");
                        // The name clash rule is Medium now (27 Sep), and a rename advised by one rule must not create one
                        // either.
                        Assert.True(rescan.Findings.Count(x => x.RuleId == "DAC-QNA-NAME-COLLISIONS") <= clashesBefore,
                            $"following {f.RuleId} ('{f.ObjectName}' to '{target}') at CL {cl}, schema {schema}: a new name clash");
                        followed[f.RuleId]++;
                        for (var i = 0; i < steps; i++) await engine.UndoAsync("agent");
                    }
                }
            }
            // No vacuous pass: each deterministic rename rule was actually followed at least once.
            foreach (var (rule, n) in followed) Assert.True(n > 0, $"{rule} was never followed, so the invariant was not tested for it");
        }

        private static bool Classified(string ruleId) => DeterministicRenames.ContainsKey(ruleId) || OpenRenames.Contains(ruleId) || NotRenames.Contains(ruleId);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_rename_rule_is_classified_by_its_category_and_its_plan_item()
        {
            // The invariant above is only as complete as its two lists. A rule is a rename rule by what it IS, not by
            // whether its message happens to say "rename" (NAME-TECH-PREFIX says "drop it", NAME-DUP never says it): every
            // Naming rule, fired or not, and every rule whose finding the change plan turns into a rename item.
            var rules = ReadinessRuleSet.Default();
            var ids = rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var r in rules.Where(r => r.Category == ReadinessCategory.Naming))
                Assert.True(Classified(r.Id), $"Naming rule {r.Id} is in none of DeterministicRenames, OpenRenames and NotRenames");
            foreach (var id in DeterministicRenames.Keys.Concat(OpenRenames).Concat(NotRenames))
                Assert.True(ids.Contains(id), $"{id} is classified but is not a readiness rule");

            var planned = 0;
            foreach (var (cl, schema) in new[] { (1604, true), (1604, false), (1400, false) })
            {
                var (engine, sessions) = await RenameRulesModelAsync(cl, schema);
                using (engine)
                {
                    var plan = await engine.ProposePlanAsync(null, includeAi: true, maxAiItems: 500, origin: "agent");
                    foreach (var it in plan.Items.Where(i => i.Kind == "rename"))
                    {
                        planned++;
                        Assert.True(DeterministicRenames.ContainsKey(it.RuleId) || OpenRenames.Contains(it.RuleId), $"the plan renames {it.ObjectRef} for {it.RuleId}, which is not classified as a rename rule");
                    }
                }
            }
            Assert.True(planned > 0, "no plan item renamed anything, so the plan check tested nothing");
        }
    }
}
