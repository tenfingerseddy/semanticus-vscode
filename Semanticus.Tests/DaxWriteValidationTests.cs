using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Every DAX write must validate before it commits, and a multi-object DAX batch must be all-or-nothing.
    /// Also: the model spec must keep a measure formula supplied as "expression", and autogenerate must classify
    /// tables by relationship role even when those tables are calculated (Enter Data).
    /// </summary>
    public sealed class DaxWriteValidationTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static async Task<LocalEngine> OpenAdventureAsync(bool pro)
        {
            var e = new LocalEngine(new SessionManager(), new Fake(pro));
            await e.OpenAsync(TestModels.FindBim());
            return e;
        }

        private static async Task<LocalEngine> FreshAsync(bool pro)
        {
            var e = new LocalEngine(new SessionManager(), new Fake(pro));
            await e.CreateModelAsync("DaxWrite", 1604);
            return e;
        }

        private static string Block(string objRef, string expr) => $"// @object {objRef}\n{expr}\n";

        [Theory]
        [InlineData("COUNTROWS('Sales')")]
        [InlineData("COUNTROWS(FILTER('Sales', TRUE()))")]
        [InlineData("""IF(1 = 1, "a", "b")""")]
        public async Task Quoted_values_count_as_DAX_arguments(string expression)
        {
            using var e = await FreshAsync(pro: false);
            await e.CreateTableAsync("Sales", "human");
            var measure = await e.CreateMeasureAsync("table:Sales", "Valid", expression, "human");
            Assert.Equal(expression, await e.GetDaxAsync(measure));
        }

        // D-120: update_measure / set_dax must refuse invalid DAX even when Verified Mode is off.
        [Fact]
        public async Task SetDax_refuses_unclosed_paren_when_verified_mode_is_off()
        {
            using var e = await OpenAdventureAsync(pro: true);
            var ms = await e.ListMeasuresAsync();
            Assert.NotEmpty(ms);
            var r = ms[0].Ref;
            var before = await e.GetDaxAsync(r);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => e.SetDaxAsync(r, "SUM(Sales[Quantity]", "agent"));
            Assert.Contains("not valid", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, await e.GetDaxAsync(r));
        }

        // D-114: apply_dax_script must refuse invalid DAX and write nothing, including when Verified Mode is off.
        [Fact]
        public async Task ApplyDaxScript_refuses_invalid_block_and_writes_nothing()
        {
            using var e = await OpenAdventureAsync(pro: true);
            var ms = (await e.ListMeasuresAsync()).Take(2).ToArray();
            Assert.True(ms.Length >= 2);
            var before0 = await e.GetDaxAsync(ms[0].Ref);
            var before1 = await e.GetDaxAsync(ms[1].Ref);

            var script = Block(ms[0].Ref, "555") + Block(ms[1].Ref, "SUM(Sales[Quantity]");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => e.ApplyDaxScriptAsync(script, "agent"));
            Assert.Contains("Nothing was written", ex.Message);
            Assert.Equal(before0, await e.GetDaxAsync(ms[0].Ref));
            Assert.Equal(before1, await e.GetDaxAsync(ms[1].Ref));
        }

        [Fact]
        public async Task ApplyDaxScript_refuses_plain_text_and_unbalanced_single_block_without_mutation()
        {
            using var e = await OpenAdventureAsync(pro: false);
            var measure = (await e.ListMeasuresAsync()).First();
            var before = await e.GetDaxAsync(measure.Ref);

            foreach (var bad in new[] { "THIS IS NOT DAX AT ALL", "SUM(Sales[Quantity]" })
            {
                var revision = (await e.SessionInfoAsync()).Revision;
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.ApplyDaxScriptAsync(Block(measure.Ref, bad), "agent"));
                Assert.Contains("Nothing was written", ex.Message);
                Assert.Equal(revision, (await e.SessionInfoAsync()).Revision);
                Assert.Equal(before, await e.GetDaxAsync(measure.Ref));
            }
        }

        // D-123: a missing object in a later block must not leave the earlier block written.
        [Fact]
        public async Task ApplyDaxScript_is_atomic_across_blocks()
        {
            using var e = await OpenAdventureAsync(pro: true);
            var ms = await e.ListMeasuresAsync();
            Assert.NotEmpty(ms);
            var r = ms[0].Ref;
            var before = await e.GetDaxAsync(r);

            var script = Block(r, "555") + Block("measure:Nope/Missing", "1");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => e.ApplyDaxScriptAsync(script, "agent"));
            Assert.Contains("Nothing was written", ex.Message);
            Assert.Contains("list_objects", ex.Message);
            Assert.Equal(before, await e.GetDaxAsync(r));
        }

        [Fact]
        public async Task ApplyDaxScript_still_applies_a_valid_single_block()
        {
            using var e = await OpenAdventureAsync(pro: false);
            var ms = await e.ListMeasuresAsync();
            Assert.NotEmpty(ms);
            var r = ms[0].Ref;
            var res = await e.ApplyDaxScriptAsync(Block(r, "1 + 1"), "human");
            Assert.Contains(r, res.Applied);
            Assert.Empty(res.Skipped);
            Assert.Equal("1 + 1", await e.GetDaxAsync(r));
        }

        [Fact]
        public async Task ApplyTmdl_refuses_an_invalid_measure_expression_without_mutation()
        {
            using var e = new LocalEngine(new SessionManager());
            await e.CreateModelAsync("TmdlFence", 1604);
            await e.CreateTableAsync("Sales", "human");
            var measure = await e.CreateMeasureAsync("table:Sales", "M", "1", "human");
            var before = await e.GetDaxAsync(measure);
            var revision = (await e.SessionInfoAsync()).Revision;
            var script = "// @object " + measure + "\nref table 'Sales'\n\nmeasure M = SUM(Sales[Ghost])\n";

            var result = await e.ApplyTmdlScriptAsync(script, "agent");

            Assert.Empty(result.Applied);
            Assert.Contains(result.Skipped, text => text.StartsWith("measure:", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(revision, (await e.SessionInfoAsync()).Revision);
            Assert.Equal(before, await e.GetDaxAsync(measure));
        }

        [Fact]
        public async Task Rls_filter_and_role_member_writes_refuse_bad_values_without_mutation()
        {
            using var sessions = new SessionManager();
            // Roles and OLS are Advanced Modelling (Pro) since 2026-09-15; the subject is the validation fence.
            using var e = new LocalEngine(sessions, TestEntitlements.Pro);
            await e.CreateModelAsync("RlsFence", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Region", "String", "Region", "human");
            await e.CreateRoleAsync("Readers", "Read", "human");

            var noOpRevision = sessions.Current.Revision;
            var noOp = await e.SetRolePermissionAsync("Readers", "Read", "agent");
            Assert.False(noOp.Changed);
            Assert.Equal(noOpRevision, noOp.Revision);
            Assert.Equal(noOpRevision, sessions.Current.Revision);

            var revision = sessions.Current.Revision;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                e.SetTablePermissionAsync("Readers", "table:Sales", "Sales[Missing] = 1", "agent"));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                e.SetRoleMemberAsync("Readers", "not a valid member?", true, "agent"));

            Assert.Equal(revision, sessions.Current.Revision);
            var role = Assert.Single(await e.ListRolesAsync());
            Assert.Empty(role.TableFilters);
            Assert.Empty(role.Members);
        }

        // D-119: a measure supplied with "expression" (and a description) must survive set_spec and the build.
        [Fact]
        public async Task SetSpec_keeps_measure_expression_alias_and_description()
        {
            using var e = await FreshAsync(pro: true);
            const string json = """
                {
                  "name": "Uat",
                  "compatibilityLevel": 1604,
                  "storageMode": "import",
                  "tables": [
                    {
                      "name": "Sales",
                      "role": "fact",
                      "columns": [ { "name": "Amount", "dataType": "Decimal" } ]
                    }
                  ],
                  "measures": [
                    {
                      "table": "Sales",
                      "name": "Total Amount",
                      "expression": "SUM(Sales[Amount])",
                      "description": "Sum of amount",
                      "formatString": "#,##0.00"
                    }
                  ]
                }
                """;
            await e.SetSpecAsync(json, "human");
            var view = await e.GetSpecAsync();
            var sm = Assert.Single(view.Spec.Measures);
            Assert.Equal("SUM(Sales[Amount])", sm.Dax);
            Assert.Equal("Sum of amount", sm.Description);
            Assert.Equal("#,##0.00", sm.FormatString);

            var report = await e.BuildModelFromSpecAsync("human");
            Assert.Empty(report.Errors);
            var built = (await e.ListMeasuresAsync()).Single(m => m.Name == "Total Amount");
            Assert.Equal("SUM(Sales[Amount])", built.Expression);
            Assert.Equal("Sum of amount", built.Description);
            Assert.Equal("#,##0.00", built.FormatString);
        }

        // D-124: a calculated fact/dimension must keep its star-schema role; only unrelated calculated tables stay calculated.
        [Fact]
        public async Task Autogenerate_classifies_calculated_tables_by_relationship_role()
        {
            using var e = await FreshAsync(pro: true);   // autogenerate_spec_* and date tables are Pro since 2026-09-15
            // Offline calculated tables do not infer columns from DAX, so add the keys as calculated columns.
            await e.CreateCalculatedTableAsync("Sales", "{1}", "human");
            await e.CreateCalculatedColumnAsync("table:Sales", "CustomerKey", "1", "human");
            await e.CreateCalculatedColumnAsync("table:Sales", "ProductKey", "1", "human");
            await e.CreateCalculatedTableAsync("Customer", "{1}", "human");
            await e.CreateCalculatedColumnAsync("table:Customer", "CustomerKey", "1", "human");
            await e.CreateCalculatedTableAsync("Product", "{1}", "human");
            await e.CreateCalculatedColumnAsync("table:Product", "ProductKey", "1", "human");
            await e.CreateCalculatedTableAsync("MeasureGroup", "{1}", "human");
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", "Date", "DateTime", "Date", "human");
            await e.MarkDateTableAsync("table:Date", "Date", "human");

            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", "OneDirection", true, "human");
            await e.CreateRelationshipAsync("column:Sales/ProductKey", "column:Product/ProductKey", "OneDirection", true, "human");

            var view = await e.AutogenerateSpecFromModelAsync("human");
            Assert.NotNull(view.Spec);
            string Role(string name)
            {
                var t = view.Spec.Tables.FirstOrDefault(x => x.Name == name);
                Assert.True(t != null, $"missing {name} in [{string.Join(", ", view.Spec.Tables.Select(x => x.Name + ":" + x.Role))}]");
                return t.Role;
            }
            Assert.Equal("fact", Role("Sales"));
            Assert.Equal("dimension", Role("Customer"));
            Assert.Equal("dimension", Role("Product"));
            Assert.Equal("date", Role("Date"));
            Assert.Equal("calculated", Role("MeasureGroup"));

            var sales = view.Spec.Tables.Single(t => t.Name == "Sales");
            Assert.False(string.IsNullOrWhiteSpace(sales.CalculatedExpression));
        }
    }
}
