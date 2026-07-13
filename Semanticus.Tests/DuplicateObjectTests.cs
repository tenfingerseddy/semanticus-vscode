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
    /// duplicate_object — the paste engine behind the Model tree's Copy/Paste. Covers the extension shipped
    /// with tree copy/paste: the optional TARGET CONTAINER (a measure pasted onto another table, a calculation
    /// item onto another group), the new model-scope kinds (table / calculated table incl. field parameters /
    /// calculation group), target-side collision-safe naming (Clone() names against the SOURCE collection, so
    /// cross-container copies are named against the target and renamed in-batch), the teaching refusals for
    /// containers that can't receive the kind, and the undo invariant (one batch, temp-name rename included).
    /// </summary>
    public sealed class DuplicateObjectTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static async Task<(LocalEngine engine, SessionManager sessions)> NewModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false));
            await engine.CreateModelAsync("Dup", 1604);
            return (engine, sessions);
        }

        // ---- cross-table measure paste (the new targetRef) --------------------------------------------

        [Fact]
        public async Task Measure_pastes_onto_another_table_with_a_collision_safe_name()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var budget = await engine.CreateTableAsync("Budget", "human");
                var mRef = await engine.CreateMeasureAsync(sales, "Total", "1", "human");

                // Measure names are MODEL-wide unique: the original keeps "Total", so the copy lands as "Total 2".
                var newRef = await engine.DuplicateObjectAsync(mRef, null, budget, "human");
                Assert.Equal("measure:Budget/Total 2", newRef);
                Assert.Contains(await engine.ListMeasuresAsync(), m => m.Table == "Budget" && m.Name == "Total 2");
            }
        }

        [Fact]
        public async Task Measure_pastes_onto_another_table_keeping_an_explicit_free_name()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var budget = await engine.CreateTableAsync("Budget", "human");
                var mRef = await engine.CreateMeasureAsync(sales, "Total", "1", "human");

                var newRef = await engine.DuplicateObjectAsync(mRef, "Budget Total", budget, "human");
                Assert.Equal("measure:Budget/Budget Total", newRef);
            }
        }

        [Fact]
        public async Task Measure_paste_avoids_a_column_name_on_the_landing_table()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var budget = await engine.CreateTableAsync("Budget", "human");
                await engine.CreateCalculatedColumnAsync(budget, "Forecast", "1", "human");
                var mRef = await engine.CreateMeasureAsync(sales, "Amount", "1", "human");

                // "Forecast" is free among measures but taken by a COLUMN on the landing table — same-table
                // measure/column name conflicts are illegal, so the copy bumps to "Forecast 2".
                var newRef = await engine.DuplicateObjectAsync(mRef, "Forecast", budget, "human");
                Assert.Equal("measure:Budget/Forecast 2", newRef);
            }
        }

        [Fact]
        public async Task Target_equal_to_the_source_table_collapses_to_the_classic_in_place_duplicate()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var mRef = await engine.CreateMeasureAsync(sales, "Total", "1", "human");

                // The UI always passes its selection — pasting onto the measure's own table must behave
                // exactly like the legacy no-target duplicate ("<name> Copy" via the wrapper's clone naming).
                var newRef = await engine.DuplicateObjectAsync(mRef, null, sales, "human");
                Assert.Equal("measure:Sales/Total Copy", newRef);
            }
        }

        // ---- model-scope kinds: table / field parameter / calculation group ---------------------------

        [Fact]
        public async Task Table_duplicates_at_model_scope_with_its_children()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                await engine.CreateMeasureAsync(sales, "Total", "1", "human");
                await engine.CreateCalculatedColumnAsync(sales, "Flag", "1", "human");

                var newRef = await engine.DuplicateObjectAsync(sales, null, null, "human");
                Assert.Equal("table:Sales Copy", newRef);

                var x = await sessions.Require().ReadAsync(m =>
                {
                    var t = m.Tables["Sales Copy"];
                    return new { Cols = t.Columns.Count, Measures = t.Measures.Count, MeasureName = t.Measures.First().Name };
                });
                Assert.Equal(1, x.Cols);
                Assert.Equal(1, x.Measures);
                Assert.NotEqual("Total", x.MeasureName);   // model-wide measure uniqueness: the clone's measure is renamed
            }
        }

        [Fact]
        public async Task Field_parameter_duplicates_as_a_calculated_table_with_its_parameter_marker()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                var t = await engine.CreateTableAsync("Sales", "human");
                var mRef = await engine.CreateMeasureAsync(t, "Total Sales", "1", "human");
                var cRef = await engine.CreateCalculatedColumnAsync(t, "Category", "\"X\"", "human");
                var pRef = await engine.CreateFieldParameterAsync("My Parameter", new[]
                {
                    new FieldParameterItem { ObjectRef = mRef },
                    new FieldParameterItem { ObjectRef = cRef },
                }, "human");

                var newRef = await engine.DuplicateObjectAsync(pRef, null, null, "human");
                Assert.Equal("table:My Parameter Copy", newRef);

                var x = await sessions.Require().ReadAsync(m =>
                {
                    var ct = m.Tables["My Parameter Copy"] as CalculatedTable;
                    return new
                    {
                        IsCalc = ct != null,
                        Cols = ct?.Columns.Count ?? 0,
                        Marker = ct?.Columns["My Parameter Fields"].GetExtendedProperty("ParameterMetadata"),
                    };
                });
                Assert.True(x.IsCalc);                                    // still a calculated table, not a flattened data table
                Assert.Equal(3, x.Cols);
                Assert.Equal("{\"version\":3,\"kind\":2}", x.Marker);     // the Power BI field-parameter marker survived the clone
            }
        }

        [Fact]
        public async Task Calculation_group_duplicates_with_its_items()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                var g = await engine.CreateCalculationGroupAsync("Time Intelligence", "human");
                await engine.CreateCalculationItemAsync(g, "YTD", "SELECTEDMEASURE()", "human");
                await engine.CreateCalculationItemAsync(g, "PY", "SELECTEDMEASURE()", "human");

                var newRef = await engine.DuplicateObjectAsync(g, null, null, "human");
                Assert.Equal("table:Time Intelligence Copy", newRef);

                var groups = await engine.ListCalculationGroupsAsync();
                Assert.Equal(2, groups.Length);
                var copy = groups.Single(x => x.Ref == newRef);
                Assert.Equal(new[] { "YTD", "PY" }, copy.Items.Select(i => i.Name).ToArray());
            }
        }

        [Fact]
        public async Task Table_paste_onto_a_DIFFERENT_table_refuses_and_teaches_model_scope()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var budget = await engine.CreateTableAsync("Budget", "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.DuplicateObjectAsync(sales, null, budget, "human"));
                Assert.Contains("model scope", ex.Message);
            }
        }

        // ---- calculation items: in-place + cross-group -------------------------------------------------

        [Fact]
        public async Task Calculation_item_duplicates_in_place_and_pastes_onto_another_group()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                var g1 = await engine.CreateCalculationGroupAsync("G1", "human");
                var g2 = await engine.CreateCalculationGroupAsync("G2", "human");
                var item = await engine.CreateCalculationItemAsync(g1, "YTD", "SELECTEDMEASURE()", "human");

                Assert.Equal("calcitem:G1/YTD Copy", await engine.DuplicateObjectAsync(item, null, null, "human"));

                // Cross-group: item names are per-group, so the copy KEEPS its name on the other group.
                Assert.Equal("calcitem:G2/YTD", await engine.DuplicateObjectAsync(item, null, g2, "human"));

                // …and a second paste onto the same target bumps to the collision-safe "YTD 2".
                Assert.Equal("calcitem:G2/YTD 2", await engine.DuplicateObjectAsync(item, null, g2, "human"));
            }
        }

        [Fact]
        public async Task Calculation_item_paste_onto_a_plain_table_refuses_and_teaches()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                var g1 = await engine.CreateCalculationGroupAsync("G1", "human");
                var item = await engine.CreateCalculationItemAsync(g1, "YTD", "SELECTEDMEASURE()", "human");
                var sales = await engine.CreateTableAsync("Sales", "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.DuplicateObjectAsync(item, null, sales, "human"));
                Assert.Contains("calculation group", ex.Message);
            }
        }

        // ---- columns / hierarchies stay on their own table ---------------------------------------------

        [Fact]
        public async Task Column_paste_onto_another_table_refuses_and_teaches()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var budget = await engine.CreateTableAsync("Budget", "human");
                var col = await engine.CreateCalculatedColumnAsync(sales, "Flag", "1", "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.DuplicateObjectAsync(col, null, budget, "human"));
                Assert.Contains("its own table", ex.Message);

                // …while the in-place duplicate still works exactly as before.
                Assert.Equal("column:Sales/Flag Copy", await engine.DuplicateObjectAsync(col, null, null, "human"));
            }
        }

        [Fact]
        public async Task Hierarchy_paste_onto_another_table_refuses_and_teaches()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var budget = await engine.CreateTableAsync("Budget", "human");
                await engine.CreateCalculatedColumnAsync(sales, "Year", "1", "human");
                await engine.CreateCalculatedColumnAsync(sales, "Month", "1", "human");
                var h = await engine.CreateHierarchyAsync(sales, "Calendar", new[] { "Year", "Month" }, "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.DuplicateObjectAsync(h, null, budget, "human"));
                Assert.Contains("levels", ex.Message);
            }
        }

        [Fact]
        public async Task Measure_paste_onto_a_non_table_target_refuses_and_teaches()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var mRef = await engine.CreateMeasureAsync(sales, "Total", "1", "human");
                await engine.CreateRoleAsync("Readers", "Read", "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.DuplicateObjectAsync(mRef, null, "role:Readers", "human"));
                Assert.Contains("must be a table", ex.Message);
            }
        }

        [Fact]
        public async Task Unknown_target_ref_refuses_and_teaches()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var mRef = await engine.CreateMeasureAsync(sales, "Total", "1", "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.DuplicateObjectAsync(mRef, null, "table:Nope", "human"));
                Assert.Contains("not found", ex.Message);
            }
        }

        // ---- the undo invariant -------------------------------------------------------------------------

        [Fact]
        public async Task Cross_table_paste_is_one_undoable_batch()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                var sales = await engine.CreateTableAsync("Sales", "human");
                var budget = await engine.CreateTableAsync("Budget", "human");
                var mRef = await engine.CreateMeasureAsync(sales, "Total", "1", "human");

                await engine.DuplicateObjectAsync(mRef, null, budget, "human");
                Assert.Contains(await engine.ListMeasuresAsync(), m => m.Table == "Budget" && m.Name == "Total 2");

                // ONE undo removes the whole paste (clone + the in-batch rename off its temp name) — the copy
                // is gone, the original untouched, and no temp-named residue survives anywhere.
                await engine.UndoAsync("human");
                var after = await engine.ListMeasuresAsync();
                Assert.DoesNotContain(after, m => m.Table == "Budget");
                Assert.Contains(after, m => m.Table == "Sales" && m.Name == "Total");
                Assert.DoesNotContain(after, m => m.Name.StartsWith("__paste_"));
            }
        }
    }
}
