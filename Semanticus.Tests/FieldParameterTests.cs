using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Field parameters — Studio v2 "Advanced Modelling" engine gap #2. create_field_parameter builds the full
    /// Power-BI-Desktop-identical structure in an OFFLINE model: the NAMEOF(...) constructor DAX (a measure is bare,
    /// a column is table-qualified), the 3 explicitly-declared columns over [Value1..3], the ParameterMetadata
    /// marker on the Fields column, sort-by-order, hidden plumbing, and the GroupByColumns field-switch key. Also
    /// proves the up-front guards (Power-BI-mode + CL>=1400, valid field refs) fire before any mutation.
    /// </summary>
    public sealed class FieldParameterTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // CreateModelAsync builds a Power-BI-mode model (pbiDatasetModel:true) — the valid target for field params.
        private static async Task<(LocalEngine engine, SessionManager sessions)> NewPbiModelAsync(int cl = 1604)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false));
            await engine.CreateModelAsync("FP", cl);
            return (engine, sessions);
        }

        [Fact]
        public async Task Builds_the_full_power_bi_field_parameter_structure()
        {
            var (engine, sessions) = await NewPbiModelAsync();
            using (engine)
            {
                var t = await engine.CreateTableAsync("Sales", "human");
                var mRef = await engine.CreateMeasureAsync(t, "Total Sales", "1", "human");
                var cRef = await engine.CreateCalculatedColumnAsync(t, "Category", "\"X\"", "human");

                var pRef = await engine.CreateFieldParameterAsync("My Parameter", new[]
                {
                    new FieldParameterItem { ObjectRef = mRef },                  // label defaults to "Total Sales"
                    new FieldParameterItem { ObjectRef = cRef, Label = "Cat" },   // explicit label
                }, "human");
                Assert.Equal("table:My Parameter", pRef);

                var x = await sessions.Require().ReadAsync(m =>
                {
                    var ct = (CalculatedTable)m.Tables["My Parameter"];
                    var label = ct.Columns["My Parameter"];
                    var fields = ct.Columns["My Parameter Fields"];
                    var order = ct.Columns["My Parameter Order"];
                    return new
                    {
                        Cols = ct.Columns.Count,
                        Marker = fields.GetExtendedProperty("ParameterMetadata"),
                        FieldsHidden = fields.IsHidden,
                        OrderHidden = order.IsHidden,
                        LabelVisible = !label.IsHidden,
                        SortOk = label.SortByColumn == order && fields.SortByColumn == order,
                        GroupOk = label.GroupByColumns != null && label.GroupByColumns.Cast<Column>().Contains(fields),
                        Src1 = ((CalculatedTableColumn)label).SourceColumn,
                        Src2 = ((CalculatedTableColumn)fields).SourceColumn,
                        Src3 = ((CalculatedTableColumn)order).SourceColumn,
                        Expr = ct.Expression,
                    };
                });

                Assert.Equal(3, x.Cols);
                Assert.Equal("{\"version\":3,\"kind\":2}", x.Marker);     // the exact marker Power BI Desktop emits
                Assert.True(x.FieldsHidden);
                Assert.True(x.OrderHidden);
                Assert.True(x.LabelVisible);
                Assert.True(x.SortOk);                                    // both content columns sort by the order column
                Assert.True(x.GroupOk);                                   // the field-switch composite key is wired
                Assert.Equal("[Value1]", x.Src1);
                Assert.Equal("[Value2]", x.Src2);
                Assert.Equal("[Value3]", x.Src3);
                Assert.Contains("NAMEOF([Total Sales])", x.Expr);         // measure: bare
                Assert.Contains("NAMEOF('Sales'[Category])", x.Expr);     // column: table-qualified
                Assert.Contains("(\"Cat\", NAMEOF('Sales'[Category]), 1)", x.Expr);   // custom label + 0-based order
            }
        }

        [Fact]
        public async Task Escapes_brackets_and_apostrophes_in_object_names()
        {
            var (engine, sessions) = await NewPbiModelAsync();
            using (engine)
            {
                // Legal Power BI names that break naive interpolation: a ']' in a measure name, a "'" in a table name.
                var t = await engine.CreateTableAsync("O'Brien", "human");
                var mRef = await engine.CreateMeasureAsync(t, "Sales [USD]", "1", "human");
                var cRef = await engine.CreateCalculatedColumnAsync(t, "Region", "\"X\"", "human");

                await engine.CreateFieldParameterAsync("P", new[]
                {
                    new FieldParameterItem { ObjectRef = mRef },
                    new FieldParameterItem { ObjectRef = cRef },
                }, "human");

                var expr = await sessions.Require().ReadAsync(m => ((CalculatedTable)m.Tables["P"]).Expression);
                Assert.Contains("NAMEOF([Sales [USD]]])", expr);            // ']' doubled to ']]'
                Assert.Contains("NAMEOF('O''Brien'[Region])", expr);        // "'" doubled to "''"
            }
        }

        [Fact]
        public async Task Create_is_a_single_undoable_step()
        {
            var (engine, sessions) = await NewPbiModelAsync();
            using (engine)
            {
                var t = await engine.CreateTableAsync("Sales", "human");
                var mRef = await engine.CreateMeasureAsync(t, "Total Sales", "1", "human");

                await engine.CreateFieldParameterAsync("My Parameter", new[] { new FieldParameterItem { ObjectRef = mRef } }, "human");
                Assert.True(await sessions.Require().ReadAsync(m => m.Tables.Contains("My Parameter")));

                await engine.UndoAsync("human");                          // the whole build collapses to one undo
                Assert.False(await sessions.Require().ReadAsync(m => m.Tables.Contains("My Parameter")));
            }
        }

        [Fact]
        public async Task Refused_on_a_non_power_bi_or_low_cl_model()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false));
            using (engine)
            {
                await engine.OpenAsync(TestModels.FindBim());             // AdventureWorks — an Analysis Services .bim
                var mRef = (await engine.ListMeasuresAsync()).First().Ref;
                // Throws on either the CL<1400 or the not-Power-BI-mode guard (both InvalidOperationException), up-front.
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    engine.CreateFieldParameterAsync("FP", new[] { new FieldParameterItem { ObjectRef = mRef } }, "human"));
            }
        }

        [Fact]
        public async Task Validation_guards()
        {
            var (engine, sessions) = await NewPbiModelAsync();
            using (engine)
            {
                var t = await engine.CreateTableAsync("Sales", "human");
                var mRef = await engine.CreateMeasureAsync(t, "Total Sales", "1", "human");

                await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateFieldParameterAsync("  ", new[] { new FieldParameterItem { ObjectRef = mRef } }, "human"));
                await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateFieldParameterAsync("FP", Array.Empty<FieldParameterItem>(), "human"));
                await Assert.ThrowsAsync<ArgumentException>(() => engine.CreateFieldParameterAsync("FP", new[] { new FieldParameterItem { ObjectRef = "  " } }, "human"));
                // a table ref is neither a measure nor a column
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateFieldParameterAsync("FP", new[] { new FieldParameterItem { ObjectRef = t } }, "human"));
                // an unresolved ref
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.CreateFieldParameterAsync("FP", new[] { new FieldParameterItem { ObjectRef = "measure:Nope/Nope" } }, "human"));

                // a guard that threw must NOT have left a half-built table behind
                Assert.False(await sessions.Require().ReadAsync(m => m.Tables.Contains("FP")));
            }
        }
    }
}
