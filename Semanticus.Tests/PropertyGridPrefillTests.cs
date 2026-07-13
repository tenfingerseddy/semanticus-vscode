using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Property-grid PREFILLS (feat/propgrid-prefills). get_properties enriches its reflected descriptors with:
    ///   • a Suggestions side-channel on DisplayFolder (folders in use — home table first, then model-wide),
    ///   • a Data Type dropdown restricted to the set_column_data_type allow-list (SettableColumnDataTypes — the
    ///     single source, so the grid can never offer a type the engine would refuse), locked honestly for
    ///     calculated columns,
    ///   • the measure dynamic-format slot as Kind=formatExpression, locked with an analyst-plain Hint below the
    ///     CL 1601 floor instead of a dead editor.
    /// Same payload over RPC and MCP — no new op.
    /// </summary>
    public sealed class PropertyGridPrefillTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private static LocalEngine NewEngine() => new LocalEngine(new SessionManager(), new Fake(false));

        [Fact]
        public async Task Model_properties_are_addressable_editable_and_undoable_through_the_shared_grid_surface()
        {
            using var engine = NewEngine();
            await engine.CreateModelAsync("Model settings", 1604);

            var props = await engine.GetObjectPropertiesAsync("model:");
            Assert.Contains(props, p => p.Name == "Description" && p.Kind == "string" && !p.ReadOnly);
            Assert.Contains(props, p => p.Name == "Culture" && p.Kind == "string");
            Assert.Contains(props, p => p.Name == "DiscourageImplicitMeasures" && p.Kind == "bool" && !p.ReadOnly);

            var set = await engine.SetObjectPropertyAsync("model:", "Description", "Default model selection", "human");
            Assert.True(set.Changed);
            Assert.Equal("Default model selection",
                (await engine.GetObjectPropertiesAsync("model:")).Single(p => p.Name == "Description").Value);

            await engine.UndoAsync("human");
            Assert.Equal("", (await engine.GetObjectPropertiesAsync("model:")).Single(p => p.Name == "Description").Value);
        }

        [Fact]
        public async Task Display_folder_suggestions_list_home_table_folders_first_then_model_wide()
        {
            using var engine = NewEngine();
            await engine.CreateModelAsync("Prefill", 1604);
            await engine.CreateTableAsync("Sales", "human");
            await engine.CreateTableAsync("Finance", "human");

            var m1 = await engine.CreateMeasureAsync("table:Sales", "Total Sales", "1", "human");
            var m2 = await engine.CreateMeasureAsync("table:Sales", "Zed Sales", "2", "human");
            var m3 = await engine.CreateMeasureAsync("table:Finance", "Margin", "3", "human");
            await engine.SetObjectPropertyAsync(m1, "DisplayFolder", "KPIs", "human");
            await engine.SetObjectPropertyAsync(m2, "DisplayFolder", "Internals\\Base", "human");
            await engine.SetObjectPropertyAsync(m3, "DisplayFolder", "Finance KPIs", "human");

            var props = await engine.GetObjectPropertiesAsync(m1);
            var folder = props.Single(p => p.Name == "DisplayFolder");
            // Home-table folders (alphabetical) come BEFORE the rest of the model's; no blanks, no dupes.
            Assert.Equal(new[] { "Internals\\Base", "KPIs", "Finance KPIs" }, folder.Suggestions);

            // A property with no prefills keeps a NULL side-channel (lean payload, not an empty array).
            Assert.Null(props.Single(p => p.Name == "Description").Suggestions);
        }

        [Fact]
        public async Task Display_folder_suggestions_cap_holds_even_when_one_table_exceeds_it()
        {
            using var engine = NewEngine();
            await engine.CreateModelAsync("PrefillCap", 1604);
            await engine.CreateTableAsync("Big", "human");
            await engine.CreateTableAsync("Other", "human");

            string firstRef = null;
            for (var i = 0; i < 65; i++)   // 65 distinct HOME-table folders — more than the 60 cap on its own
            {
                var r = await engine.CreateMeasureAsync("table:Big", $"M{i:D2}", "1", "human");
                firstRef ??= r;
                await engine.SetObjectPropertyAsync(r, "DisplayFolder", $"Folder {i:D2}", "human");
            }
            var om = await engine.CreateMeasureAsync("table:Other", "Elsewhere", "1", "human");
            await engine.SetObjectPropertyAsync(om, "DisplayFolder", "AAA Elsewhere", "human");   // would sort first if it got in

            var folder = (await engine.GetObjectPropertiesAsync(firstRef)).Single(p => p.Name == "DisplayFolder");
            // Exactly the documented cap — the home loop enforces it too (this payload rides EVERY get_properties),
            // and within the cap home-table folders take priority over the rest of the model.
            Assert.Equal(60, folder.Suggestions.Length);
            Assert.All(folder.Suggestions, f => Assert.StartsWith("Folder ", f));
        }

        [Fact]
        public async Task Column_data_type_options_are_the_engine_settable_set_and_calculated_columns_lock_honestly()
        {
            using var engine = NewEngine();
            await engine.CreateModelAsync("Prefill2", 1604);
            await engine.CreateTableAsync("Sales", "human");
            var dataCol = await engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            var calcCol = await engine.CreateCalculatedColumnAsync("table:Sales", "Doubled", "2", "human");

            // Data column: options == exactly the set_column_data_type allow-list (no Unknown/Variant/Binary noise).
            var dt = (await engine.GetObjectPropertiesAsync(dataCol)).Single(p => p.Name == "DataType");
            Assert.False(dt.ReadOnly);
            Assert.Equal(LocalEngine.SettableColumnDataTypes, dt.Options);
            Assert.Equal("Decimal", dt.Value);

            // And the allow-listed names all round-trip through set_column_data_type (options can't drift from the op).
            foreach (var name in dt.Options)
                await engine.SetColumnDataTypeAsync(dataCol, name, "human");

            // Calculated column: the type comes from DAX — the row is locked with a plain-language reason, matching
            // set_column_data_type's refusal rather than offering an edit that would throw.
            var cdt = (await engine.GetObjectPropertiesAsync(calcCol)).Single(p => p.Name == "DataType");
            Assert.True(cdt.ReadOnly);
            Assert.Contains("DAX", cdt.Hint);
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SetColumnDataTypeAsync(calcCol, "String", "human"));
        }

        [Fact]
        public async Task Measure_format_expression_row_is_a_dedicated_kind_and_round_trips()
        {
            using var engine = NewEngine();
            await engine.CreateModelAsync("PrefillFx", 1604);   // >= 1601: the dynamic slot is available
            await engine.CreateTableAsync("Sales", "human");
            var mRef = await engine.CreateMeasureAsync("table:Sales", "Total Sales", "1", "human");

            var fx = (await engine.GetObjectPropertiesAsync(mRef)).Single(p => p.Name == "FormatStringExpression");
            Assert.Equal("formatExpression", fx.Kind);
            Assert.Equal("Format expression", fx.DisplayName);
            Assert.False(fx.ReadOnly);
            Assert.Null(fx.Hint);
            Assert.Equal("", fx.Value);

            const string expr = "\"+0.0%;-0.0%;0.0%\"";
            await engine.SetMeasureFormatExpressionAsync(mRef, expr, "human");
            fx = (await engine.GetObjectPropertiesAsync(mRef)).Single(p => p.Name == "FormatStringExpression");
            Assert.Equal(expr, fx.Value);
        }

        [Fact]
        public async Task Measure_format_expression_row_states_the_compatibility_floor_below_1601()
        {
            using var engine = NewEngine();
            await engine.OpenAsync(TestModels.FindBim());   // AdventureWorks = CL 1200, below the floor
            var mRef = (await engine.ListMeasuresAsync()).First().Ref;

            var fx = (await engine.GetObjectPropertiesAsync(mRef)).Single(p => p.Name == "FormatStringExpression");
            Assert.Equal("formatExpression", fx.Kind);
            Assert.True(fx.ReadOnly);                        // no dead editor — the row is locked…
            Assert.Contains("1601", fx.Hint);                // …and says exactly what it needs, analyst-plain
            Assert.Contains("1200", fx.Hint);
        }
    }
}
