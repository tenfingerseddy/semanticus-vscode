using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// C5.3 UAT: relationships and columns. Dual-drive: every refusal and every readable property is on
    /// IEngine, so the tree, the property grid, and the agent door share one check.
    /// D-038 incompatible types, D-040 one active relationship per table pair, D-041 summarization vs type,
    /// D-045 calculated-table column delete, D-053 sort-by column on the grid, D-156 rename report warning,
    /// D-161 inactive marker in the tree.
    /// </summary>
    public sealed class ColumnRelationshipSafetyTests
    {
        private static async Task<(LocalEngine engine, SessionManager sessions)> NewModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            await engine.CreateModelAsync("ColRel", 1604);
            return (engine, sessions);
        }

        // ---- D-038 incompatible types -----------------------------------------------------------

        [Fact]
        public async Task Create_relationship_refuses_mismatched_column_types_without_a_revision()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateTableAsync("Sales", "human");
                await engine.CreateTableAsync("Product", "human");
                var qty = await engine.CreateColumnAsync("table:Sales", "Quantity", "Int64", "Quantity", "human");
                var name = await engine.CreateColumnAsync("table:Product", "Name", "String", "Name", "human");
                var before = sessions.Current.Revision;

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.CreateRelationshipAsync(qty, name, null, true, "human"));
                Assert.Contains("same type", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("whole number", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("text", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("isActive", ex.Message, StringComparison.Ordinal);
                Assert.Equal(before, sessions.Current.Revision);
                Assert.Empty((await engine.ListTreeAsync("folder:relationships")));
            }
        }

        [Fact]
        public async Task Create_relationship_accepts_matching_types_and_mcp_door_uses_the_same_refusal()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateTableAsync("Sales", "human");
                await engine.CreateTableAsync("Product", "human");
                var fk = await engine.CreateColumnAsync("table:Sales", "ProductKey", "Int64", "ProductKey", "human");
                var pk = await engine.CreateColumnAsync("table:Product", "ProductKey", "Int64", "ProductKey", "human");
                var name = await engine.CreateColumnAsync("table:Product", "Name", "String", "Name", "human");

                var rel = await engine.CreateRelationshipAsync(fk, pk, null, true, "human");
                Assert.StartsWith("relationship:", rel);

                var before = sessions.Current.Revision;
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.CreateRelationship(engine, fk, name, "OneDirection", true));
                Assert.Contains("same type", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, sessions.Current.Revision);
            }
        }

        // ---- D-040 one active relationship per table pair ---------------------------------------

        [Fact]
        public async Task Second_active_relationship_on_the_same_tables_is_refused()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateTableAsync("Sales", "human");
                await engine.CreateTableAsync("Dim", "human");
                var amount = await engine.CreateColumnAsync("table:Sales", "Amount", "Int64", "Amount", "human");
                var dimKey = await engine.CreateColumnAsync("table:Sales", "DimKey", "Int64", "DimKey", "human");
                var pk = await engine.CreateColumnAsync("table:Dim", "DimKey", "Int64", "DimKey", "human");

                await engine.CreateRelationshipAsync(amount, pk, null, true, "human");
                var before = sessions.Current.Revision;

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.CreateRelationshipAsync(dimKey, pk, null, true, "human"));
                Assert.Contains("active relationship already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Sales", ex.Message);
                Assert.Contains("Dim", ex.Message);
                Assert.Equal(before, sessions.Current.Revision);

                var inactive = await engine.CreateRelationshipAsync(dimKey, pk, null, false, "human");
                Assert.StartsWith("relationship:", inactive);
            }
        }

        [Fact]
        public async Task Activating_a_second_relationship_on_the_same_tables_is_refused()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateTableAsync("Sales", "human");
                await engine.CreateTableAsync("Date", "human");
                var order = await engine.CreateColumnAsync("table:Sales", "OrderDateKey", "Int64", "OrderDateKey", "human");
                var ship = await engine.CreateColumnAsync("table:Sales", "ShipDateKey", "Int64", "ShipDateKey", "human");
                var dateKey = await engine.CreateColumnAsync("table:Date", "DateKey", "Int64", "DateKey", "human");

                await engine.CreateRelationshipAsync(order, dateKey, null, true, "human");
                var second = await engine.CreateRelationshipAsync(ship, dateKey, null, false, "human");
                var name = second.StartsWith("relationship:", StringComparison.Ordinal)
                    ? second.Substring("relationship:".Length) : second;
                var before = sessions.Current.Revision;

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetRelationshipAsync(name, null, true, "human"));
                Assert.Contains("active relationship already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, sessions.Current.Revision);
            }
        }

        // ---- D-041 summarization vs type --------------------------------------------------------

        [Fact]
        public async Task Sum_on_a_text_column_is_refused_on_both_write_paths()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateTableAsync("Date", "human");
                var month = await engine.CreateColumnAsync("table:Date", "Month Name", "String", "Month Name", "human");
                var before = sessions.Current.Revision;

                var viaMeta = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetColumnMetadataAsync(month, null, "Sum", null, null, "human"));
                Assert.Contains("text", viaMeta.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Sum", viaMeta.Message);
                Assert.Equal(before, sessions.Current.Revision);

                var viaProp = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SetObjectPropertyAsync(month, "SummarizeBy", "Sum", "human"));
                Assert.Contains("text", viaProp.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, sessions.Current.Revision);

                var viaMcp = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.SetSummarizeBy(engine, month, "Sum"));
                Assert.Contains("text", viaMcp.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, sessions.Current.Revision);

                var ok = await engine.SetColumnMetadataAsync(month, null, "Count", null, null, "human");
                Assert.True(ok.Changed);
                Assert.Equal("Count", (await engine.ListColumnsAsync()).Single(c => c.Ref == month).SummarizeBy);
            }
        }

        [Fact]
        public async Task Summarize_by_options_on_the_grid_match_the_column_type()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateTableAsync("Sales", "human");
                var amount = await engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
                var name = await engine.CreateColumnAsync("table:Sales", "Name", "String", "Name", "human");

                var num = (await engine.GetObjectPropertiesAsync(amount)).Single(p => p.Name == "SummarizeBy");
                Assert.Contains("Sum", num.Options);
                Assert.Contains("Average", num.Options);

                var text = (await engine.GetObjectPropertiesAsync(name)).Single(p => p.Name == "SummarizeBy");
                Assert.DoesNotContain("Sum", text.Options);
                Assert.Contains("Count", text.Options);
                Assert.Contains("None", text.Options);
            }
        }

        // ---- D-053 sort by column on the grid ---------------------------------------------------

        [Fact]
        public async Task Sort_by_column_is_readable_and_settable_on_the_shared_grid_surface()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateTableAsync("Date", "human");
                var month = await engine.CreateColumnAsync("table:Date", "Month Name", "String", "Month Name", "human");
                await engine.CreateColumnAsync("table:Date", "Month Number", "Int64", "Month Number", "human");

                var props = await engine.GetObjectPropertiesAsync(month);
                var sort = Assert.Single(props, p => p.Name == "SortByColumn");
                Assert.Equal("Sort by column", sort.DisplayName);
                Assert.Equal("enum", sort.Kind);
                Assert.Contains("(none)", sort.Options);
                Assert.Contains("Month Number", sort.Options);
                Assert.DoesNotContain("Month Name", sort.Options);
                Assert.Equal("(none)", sort.Value);
                Assert.False(sort.ReadOnly);

                var set = await engine.SetObjectPropertyAsync(month, "SortByColumn", "Month Number", "human");
                Assert.True(set.Changed);
                Assert.Equal("Month Number", (await engine.ListColumnsAsync()).Single(c => c.Ref == month).SortByColumn);
                Assert.Equal("Month Number", (await engine.GetObjectPropertiesAsync(month)).Single(p => p.Name == "SortByColumn").Value);

                var mcp = await McpTools.GetProperties(engine, month);
                Assert.Equal("Month Number", mcp.Single(p => p.Name == "SortByColumn").Value);

                var clear = await engine.SetObjectPropertyAsync(month, "SortByColumn", "(none)", "human");
                Assert.True(clear.Changed);
                Assert.Null((await engine.ListColumnsAsync()).Single(c => c.Ref == month).SortByColumn);
            }
        }

        [Fact]
        public async Task Set_column_metadata_clears_sort_by_when_the_name_is_empty()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateTableAsync("Date", "human");
                var month = await engine.CreateColumnAsync("table:Date", "Month Name", "String", "Month Name", "human");
                await engine.CreateColumnAsync("table:Date", "Month Number", "Int64", "Month Number", "human");
                await engine.SetColumnMetadataAsync(month, null, null, null, "Month Number", "human");

                var cleared = await engine.SetColumnMetadataAsync(month, null, null, null, "", "human");
                Assert.True(cleared.Changed);
                Assert.Null((await engine.ListColumnsAsync()).Single(c => c.Ref == month).SortByColumn);
            }
        }

        // ---- D-045 calculated-table column delete -----------------------------------------------

        [Fact]
        public async Task Deleting_a_calculated_table_column_is_refused_without_a_revision()
        {
            var (engine, sessions) = await NewModelAsync();
            using (engine)
            {
                var t = await engine.CreateTableAsync("Sales", "human");
                var mRef = await engine.CreateMeasureAsync(t, "Total Sales", "1", "human");
                await engine.CreateFieldParameterAsync("Date", new[] { new FieldParameterItem { ObjectRef = mRef } }, "human");
                var col = (await engine.ListColumnsAsync()).First(c => c.Table == "Date");
                var before = sessions.Current.Revision;

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.DeleteObjectAsync(col.Ref, "human"));
                Assert.Contains("formula", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Calculated Table", ex.Message, StringComparison.Ordinal);
                Assert.Equal(before, sessions.Current.Revision);
                Assert.Contains((await engine.ListColumnsAsync()), c => c.Ref == col.Ref);

                var mcp = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.DeleteObject(engine, col.Ref));
                Assert.Contains("formula", mcp.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, sessions.Current.Revision);
            }
        }

        // ---- D-161 inactive marker in the tree --------------------------------------------------

        [Fact]
        public async Task Inactive_relationships_carry_an_inactive_marker_in_the_tree()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateTableAsync("Sales", "human");
                await engine.CreateTableAsync("Date", "human");
                var fk = await engine.CreateColumnAsync("table:Sales", "DateKey", "Int64", "DateKey", "human");
                var pk = await engine.CreateColumnAsync("table:Date", "DateKey", "Int64", "DateKey", "human");
                var rel = await engine.CreateRelationshipAsync(fk, pk, null, true, "human");
                var name = rel.Substring("relationship:".Length);

                var active = (await engine.ListTreeAsync("folder:relationships")).Single();
                Assert.DoesNotContain("inactive", active.Name, StringComparison.OrdinalIgnoreCase);

                await engine.SetRelationshipAsync(name, null, false, "human");
                var inactive = (await engine.ListTreeAsync("folder:relationships")).Single();
                Assert.Contains("(inactive)", inactive.Name, StringComparison.Ordinal);
            }
        }

        // ---- D-156 rename report-impact warning -------------------------------------------------

        [Fact]
        public async Task Rename_warns_that_reports_are_not_rewritten()
        {
            var (engine, _) = await NewModelAsync();
            using (engine)
            {
                await engine.CreateTableAsync("Sales", "human");
                var mRef = await engine.CreateMeasureAsync("table:Sales", "Total", "1", "human");
                var col = await engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");

                var renamed = await engine.RenameObjectAsync(mRef, "Revenue", "human");
                Assert.True(renamed.Changed);
                Assert.Contains("report", renamed.Warning, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("DAX", renamed.Warning ?? "", StringComparison.Ordinal);

                var viaProp = await engine.SetObjectPropertyAsync(col, "Name", "Qty", "human");
                Assert.True(viaProp.Changed);
                Assert.Contains("report", viaProp.Warning, StringComparison.OrdinalIgnoreCase);

                var mcp = await McpTools.RenameObject(engine, "table:Sales", "Fact Sales");
                Assert.Contains("report", mcp.Warning, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
