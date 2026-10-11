using System;
using System.Collections.Generic;
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
    /// Kane's decisions of 27 Sep on m11/feedback-lsdl round 3.
    /// (A) DIM-PRIMARY-NAME gives Microsoft's row-label advice instead of a rename: a dimension whose label column is
    ///     its row label (Column.IsDefaultLabel) is clean; otherwise the finding says to set the row label, with a
    ///     one-click fix on both doors that undoes in one step. It collides with nothing, so it runs on 1465+ models.
    /// (B) The name-clash premise is narrowed to what has evidence. Power BI Desktop saves a table beside its own label
    ///     column, 'Date'[Date], a same-named key on two tables and 'Sales' beside '# Sales' in Microsoft's own samples,
    ///     so none is a collision: the scan does not flag them and set_synonyms does not refuse them. What stays is
    ///     Microsoft's documented Copilot ambiguity: two visible fields that read as the same word ("avoid duplicate
    ///     field names across tables"; 'Profit' read for 'Profit %'), reported at Medium, never as a failed write.
    /// </summary>
    public sealed class LinguisticSchemaAddendumTests : IClassFixture<TemplateCopies>
    {
        private readonly TemplateCopies _copies;
        public LinguisticSchemaAddendumTests(TemplateCopies copies) => _copies = copies;

        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static RuleEvaluation Eval(SessionManager sessions, string id)
        {
            PrepForAiReader.Invalidate(sessions.Current.Model);
            return ReadinessRuleSet.Default().Single(r => r.Id == id).Evaluate(sessions.Current.Model);
        }

        /// <summary>A star schema: 'Sales' facts, a 'Product' dimension labelled by 'Product Name', related on
        /// 'Product Key' (keys hidden, the way VIS-FK leaves them).</summary>
        private async Task<(LocalEngine Engine, SessionManager Sessions)> StarAsync(int cl = 1604, bool schema = true)
        {
            var sessions = new SessionManager();
            return (await _copies.OpenFromFixtureAsync("star-" + cl + "-" + schema, f => BuildStarAsync(f, cl, schema), new Fake(), sessions), sessions);
        }

        private static async Task BuildStarAsync(DecisionFixture f, int cl, bool schema)
        {
            var engine = f.Engine;
            var sessions = f.Sessions;
            if (cl < 1465) await TestModels.OpenEmptyBimAsync(engine, "Shop", cl);
            else await engine.CreateModelAsync("Shop", cl);
            var sales = await engine.CreateTableAsync("Sales", "human");
            var fk = await engine.CreateColumnAsync(sales, "Product Key", "Int64", "Product Key", "human");
            await engine.CreateMeasureAsync(sales, "Net Sales", "1", "human");
            var product = await engine.CreateTableAsync("Product", "human");
            var pk = await engine.CreateColumnAsync(product, "Product Key", "Int64", "Product Key", "human");
            await engine.CreateColumnAsync(product, "Product Name", "String", "Product Name", "human");
            await engine.CreateColumnAsync(product, "Color", "String", "Color", "human");
            await engine.CreateRelationshipAsync(fk, pk, null, true, "human");
            await sessions.Require().MutateAsync("human", "hide keys", m =>
            {
                m.Tables["Sales"].Columns["Product Key"].IsHidden = true;
                m.Tables["Product"].Columns["Product Key"].IsHidden = true;
            });
            if (schema && cl >= 1465) await engine.CreateLinguisticSchemaAsync(null, "agent");
        }

        private static Task<bool> IsRowLabelAsync(SessionManager sessions, string table, string column) =>
            sessions.Require().ReadAsync(m => m.Tables[table].Columns[column].IsDefaultLabel);

        // ---- (A) DIM-PRIMARY-NAME: the row label -------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(1604, true)]
        [InlineData(1604, false)]
        [InlineData(1400, false)]
        public async Task A_dimension_without_a_row_label_is_told_to_set_one_not_to_rename(int cl, bool schema)
        {
            var (engine, sessions) = await StarAsync(cl, schema);
            using (engine)
            {
                var f = Assert.Single(Eval(sessions, "DIM-PRIMARY-NAME").Violations);
                Assert.Equal("column:Product/Product Name", f.ObjectRef);
                Assert.Equal(nameof(FixKind.SafeFix), f.Fix);
                Assert.Contains("row label", f.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("Rename", f.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("—", f.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task A_dimension_whose_label_column_is_its_row_label_is_clean()
        {
            var (engine, sessions) = await StarAsync();
            using (engine)
            {
                await sessions.Require().MutateAsync("human", "row label", m => m.Tables["Product"].Columns["Product Name"].IsDefaultLabel = true);
                var ev = Eval(sessions, "DIM-PRIMARY-NAME");
                Assert.Equal(1, ev.Applicable);   // evaluated, and passes: never an always-pass rule
                Assert.Empty(ev.Violations);
            }
        }

        [Fact]
        public async Task The_row_label_fix_works_through_both_doors_and_undoes_in_one_step()
        {
            foreach (var door in new[] { "mcp", "rpc" })
            {
                var (engine, sessions) = await StarAsync();
                using (engine)
                {
                    var f = Assert.Single(Eval(sessions, "DIM-PRIMARY-NAME").Violations);
                    var result = door == "mcp"
                        ? await McpTools.ApplyFix(engine, "air", f.RuleId, f.ObjectRef)
                        : await new EngineRpcTarget(engine).applyFix(f.RuleId, f.ObjectRef);
                    Assert.True(result.Changed, door);
                    Assert.True(await IsRowLabelAsync(sessions, "Product", "Product Name"), door);
                    Assert.Empty(Eval(sessions, "DIM-PRIMARY-NAME").Violations);

                    await engine.UndoAsync("agent");
                    Assert.False(await IsRowLabelAsync(sessions, "Product", "Product Name"), door);
                    Assert.Single(Eval(sessions, "DIM-PRIMARY-NAME").Violations);
                }
            }
        }

        [Fact]
        public async Task Apply_safe_fixes_and_the_change_plan_set_the_row_label()
        {
            var (engine, sessions) = await StarAsync();
            using (engine)
            {
                var fixes = await engine.ApplySafeFixesAsync("agent");
                Assert.Contains(fixes.Applied, a => a.Contains("row label", StringComparison.Ordinal));
                Assert.True(await IsRowLabelAsync(sessions, "Product", "Product Name"));
                await engine.UndoAsync("agent");
                Assert.False(await IsRowLabelAsync(sessions, "Product", "Product Name"));

                var plan = await engine.ProposePlanAsync(null, includeAi: true, maxAiItems: 200, origin: "agent");
                var item = Assert.Single(plan.Items, i => i.RuleId == "DIM-PRIMARY-NAME");
                Assert.Equal("set_default_label", item.Kind);
                Assert.Equal("approved", item.Status);
                Assert.DoesNotContain(plan.Items, i => i.RuleId == "DIM-PRIMARY-NAME" && i.Kind == "rename");
                await engine.ApplyPlanAsync(null, "agent");
                Assert.True(await IsRowLabelAsync(sessions, "Product", "Product Name"));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_hidden_row_label_moves_to_the_visible_label_column_so_a_table_keeps_one()
        {
            // Sol, round 3: a hidden column still marked as the row label does not count (Copilot ignores hidden
            // columns), and every fix route must MOVE the row label rather than add a second one.
            foreach (var route in new[] { "apply_fix", "apply_safe_fixes", "apply_plan" })
            {
                var (engine, sessions) = await StarAsync();
                using (engine)
                {
                    var legacy = await engine.CreateColumnAsync("table:Product", "Legacy Name", "String", "Legacy Name", "human");
                    await sessions.Require().MutateAsync("human", "old label", m =>
                    {
                        m.Tables["Product"].Columns["Legacy Name"].IsDefaultLabel = true;
                        m.Tables["Product"].Columns["Legacy Name"].IsHidden = true;
                    });
                    var f = Assert.Single(Eval(sessions, "DIM-PRIMARY-NAME").Violations);
                    if (route == "apply_fix") await engine.ApplyFixAsync(f.RuleId, f.ObjectRef, "agent");
                    else if (route == "apply_safe_fixes") await engine.ApplySafeFixesAsync("agent");
                    else { await engine.ProposePlanAsync(null, includeAi: true, maxAiItems: 200, origin: "agent"); await engine.ApplyPlanAsync(null, "agent"); }

                    var labels = await sessions.Require().ReadAsync(m => m.Tables["Product"].Columns.Where(c => c.IsDefaultLabel).Select(c => c.Name).ToArray());
                    Assert.Equal(new[] { "Product Name" }, labels);
                    await engine.UndoAsync("agent");
                    Assert.True(await IsRowLabelAsync(sessions, "Product", "Legacy Name"), route);
                }
            }
        }

        [Fact]
        public async Task A_plan_item_does_not_overwrite_a_row_label_the_person_set_after_it_was_proposed()
        {
            var (engine, sessions) = await StarAsync();
            using (engine)
            {
                await engine.CreateColumnAsync("table:Product", "Product Description", "String", "Product Description", "human");
                var plan = await engine.ProposePlanAsync(null, includeAi: true, maxAiItems: 200, origin: "agent");
                var item = Assert.Single(plan.Items, i => i.RuleId == "DIM-PRIMARY-NAME");
                await sessions.Require().MutateAsync("human", "person's label", m => m.Tables["Product"].Columns["Product Description"].IsDefaultLabel = true);

                await engine.ApplyPlanAsync(null, "agent");

                var labels = await sessions.Require().ReadAsync(m => m.Tables["Product"].Columns.Where(c => c.IsDefaultLabel).Select(c => c.Name).ToArray());
                Assert.Equal(new[] { "Product Description" }, labels);
            }
        }

        // ---- (B) only collisions with evidence -----------------------------------------------------------------

        /// <summary>The four shapes Power BI Desktop saves in Microsoft's own samples, each with its linguistic
        /// schema: 'Customer' beside its label column 'Customer'[Customer]; 'Date'[Date]; 'CustomerKey' on 'Customer'
        /// and on 'Sales', related; and the 'Sales' table beside its '# Sales' measure.</summary>
        private static async Task<(LocalEngine Engine, SessionManager Sessions)> DesktopShapesAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("Contoso", 1604);
            var sales = await engine.CreateTableAsync("Sales", "human");
            var fk = await engine.CreateColumnAsync(sales, "CustomerKey", "Int64", "CustomerKey", "human");
            await engine.CreateMeasureAsync(sales, "# Sales", "1", "human");
            await engine.CreateMeasureAsync(sales, "Sales Amount", "1", "human");
            var customer = await engine.CreateTableAsync("Customer", "human");
            var pk = await engine.CreateColumnAsync(customer, "CustomerKey", "Int64", "CustomerKey", "human");
            await engine.CreateColumnAsync(customer, "Customer", "String", "Customer", "human");
            await engine.CreateRelationshipAsync(fk, pk, null, true, "human");
            var date = await engine.CreateTableAsync("Date", "human");
            await engine.CreateColumnAsync(date, "Date", "DateTime", "Date", "human");
            await engine.CreateColumnAsync(date, "Year", "Int64", "Year", "human");
            await engine.CreateLinguisticSchemaAsync(null, "agent");
            return (engine, sessions);
        }

        [Fact]
        public async Task The_shapes_power_bi_desktop_saves_are_not_collisions()
        {
            var (engine, sessions) = await DesktopShapesAsync();
            using (engine)
            {
                Assert.Empty(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);
                foreach (var target in new[] { "table:Customer", "column:Customer/Customer", "table:Date", "column:Date/Date",
                             "column:Customer/CustomerKey", "column:Sales/CustomerKey", "table:Sales", "measure:Sales/# Sales" })
                {
                    var r = await engine.SetSynonymsAsync(target, new[] { "verify synonym" }, null, "agent");
                    Assert.True(r.Changed, target);
                    Assert.True(r.Warning == null || !r.Warning.Contains("same", StringComparison.OrdinalIgnoreCase), target + ": " + r.Warning);
                }
                Assert.Empty(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);
                var advisory = (await engine.CreateLinguisticSchemaAsync(null, "agent")).Warning;
                Assert.True(advisory == null || !advisory.Contains("refuses", StringComparison.Ordinal), advisory);
            }
        }

        [Fact]
        public async Task Two_fields_that_read_as_one_word_are_a_medium_copilot_ambiguity_and_writes_still_work()
        {
            // Microsoft's own examples: 'Country' on 'Customer' and on 'Sales Region' (Copilot filtered the wrong one),
            // and 'Profit' read for 'Profit %'.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Shop", 1604);
                var customer = await engine.CreateTableAsync("Customer", "human");
                await engine.CreateColumnAsync(customer, "Country", "String", "Country", "human");
                var region = await engine.CreateTableAsync("Sales Region", "human");
                await engine.CreateColumnAsync(region, "Country", "String", "Country", "human");
                await engine.CreateMeasureAsync(region, "Profit", "1", "human");
                await engine.CreateMeasureAsync(region, "Profit %", "1", "human");
                await engine.CreateLinguisticSchemaAsync(null, "agent");

                var ev = Eval(sessions, "DAC-QNA-NAME-COLLISIONS");
                Assert.Equal(2, ev.Violations.Count);
                Assert.All(ev.Violations, v => Assert.Equal("Medium", v.Severity));
                Assert.All(ev.Violations, v => Assert.DoesNotContain("every synonym write fails", v.Message, StringComparison.Ordinal));
                Assert.Contains(ev.Violations, v => v.Message.Contains("column 'Customer'[Country]", StringComparison.Ordinal)
                                                    && v.Message.Contains("column 'Sales Region'[Country]", StringComparison.Ordinal));
                Assert.Contains(ev.Violations, v => v.Message.Contains("measure 'Sales Region'[Profit %]", StringComparison.Ordinal));

                Assert.True((await engine.SetSynonymsAsync("column:Customer/Country", new[] { "nation" }, null, "agent")).Changed);
                Assert.True((await engine.SetSynonymsAsync("measure:Sales Region/Profit %", new[] { "margin rate" }, null, "agent")).Changed);
            }
        }

        [Fact]
        public async Task A_plural_fold_is_no_longer_a_collision()
        {
            // The fold tier was our own approximation of the validator's lemmatiser, with no evidence behind it.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Shop", 1604);
                var plan = await engine.CreateTableAsync("Plan", "human");
                await engine.CreateColumnAsync(plan, "Cycle Months", "Int64", "Cycle Months", "human");
                var task = await engine.CreateTableAsync("Task", "human");
                await engine.CreateColumnAsync(task, "Cycle Month", "Int64", "Cycle Month", "human");
                await engine.CreateLinguisticSchemaAsync(null, "agent");

                Assert.Empty(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);
                var r = await engine.SetSynonymsAsync("column:Task/Cycle Month", new[] { "cadence" }, null, "agent");
                Assert.True(r.Warning == null || !r.Warning.Contains("folding", StringComparison.Ordinal), r.Warning);
            }
        }

        [Fact]
        public async Task A_key_family_joined_through_a_hidden_hub_is_still_one_key()
        {
            // Sol, round 3: 'Product Key' visible on 'Sales' and 'Returns', both related to the hidden key on 'Product'.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Shop", 1604);
                var product = await engine.CreateTableAsync("Product", "human");
                var pk = await engine.CreateColumnAsync(product, "Product Key", "Int64", "Product Key", "human");
                await engine.CreateColumnAsync(product, "Product Name", "String", "Product Name", "human");
                foreach (var fact in new[] { "Sales", "Returns" })
                {
                    var t = await engine.CreateTableAsync(fact, "human");
                    var fk = await engine.CreateColumnAsync(t, "Product Key", "Int64", "Product Key", "human");
                    await engine.CreateRelationshipAsync(fk, pk, null, true, "human");
                }
                await sessions.Require().MutateAsync("human", "hide hub", m => m.Tables["Product"].Columns["Product Key"].IsHidden = true);
                await engine.CreateLinguisticSchemaAsync(null, "agent");
                Assert.Empty(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);
            }
        }

        [Fact]
        public async Task Creating_the_schema_the_first_time_names_the_fields_copilot_cannot_tell_apart()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Shop", 1604);
                var customer = await engine.CreateTableAsync("Customer", "human");
                await engine.CreateColumnAsync(customer, "Country", "String", "Country", "human");
                var region = await engine.CreateTableAsync("Sales Region", "human");
                await engine.CreateColumnAsync(region, "Country", "String", "Country", "human");

                var r = await engine.CreateLinguisticSchemaAsync(null, "agent");
                Assert.True(r.Changed);
                Assert.NotNull(r.Warning);
                Assert.Contains("'country'", r.Warning, StringComparison.Ordinal);
                Assert.Single(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);
            }
        }

        [Fact]
        public async Task A_same_named_key_family_across_three_tables_is_one_key_not_a_collision()
        {
            // A key used by two fact tables: 'Product Key' on 'Product', 'Sales' and 'Returns', all visible and related.
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("Shop", 1604);
                var product = await engine.CreateTableAsync("Product", "human");
                var pk = await engine.CreateColumnAsync(product, "Product Key", "Int64", "Product Key", "human");
                foreach (var fact in new[] { "Sales", "Returns" })
                {
                    var t = await engine.CreateTableAsync(fact, "human");
                    var fk = await engine.CreateColumnAsync(t, "Product Key", "Int64", "Product Key", "human");
                    await engine.CreateRelationshipAsync(fk, pk, null, true, "human");
                }
                await engine.CreateLinguisticSchemaAsync(null, "agent");
                Assert.Empty(Eval(sessions, "DAC-QNA-NAME-COLLISIONS").Violations);
            }
        }
    }
}
