using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Update Schema (Tabular-Editor-style): the pure M-source parser + the source-vs-model diff + the apply-subset
    /// mutation. The live source-read leg needs a reachable SQL/Fabric endpoint, so it's exercised only for its
    /// GRACEFUL-unreachable contract (no throw); the diff + apply are proven end-to-end offline by supplying a
    /// synthesized source schema — exactly the manual/offline path the UI falls back to.
    /// </summary>
    public sealed class SchemaSyncTests
    {
        // ---- pure M-source parser ------------------------------------------------------------------

        [Fact]
        public void Parses_Sql_Database_two_arg_with_schema_and_item()
        {
            var m = "let\n  Source = Sql.Database(\"srv.database.windows.net\", \"MyDB\"),\n  t = Source{[Schema=\"dbo\",Item=\"Sales\"]}[Data]\nin t";
            Assert.True(SchemaSync.TryParseSqlSource(m, out var server, out var db, out var schema, out var item));
            Assert.Equal("srv.database.windows.net", server);
            Assert.Equal("MyDB", db);
            Assert.Equal("dbo", schema);
            Assert.Equal("Sales", item);
        }

        [Fact]
        public void Parses_Sql_Databases_navigation_form()
        {
            var m = "let\n  Source = Sql.Databases(\"myserver\"),\n  DB = Source{[Name=\"Warehouse\"]}[Data],\n  t = DB{[Schema=\"sales\",Item=\"FactOrders\"]}[Data]\nin t";
            Assert.True(SchemaSync.TryParseSqlSource(m, out var server, out var db, out var schema, out var item));
            Assert.Equal("myserver", server);
            Assert.Equal("Warehouse", db);
            Assert.Equal("sales", schema);
            Assert.Equal("FactOrders", item);
        }

        [Fact]
        public void A_non_sql_source_is_not_reachable()
        {
            Assert.False(SchemaSync.TryParseSqlSource("let Source = #table({\"A\"},{{1}}) in Source", out _, out _, out _, out _));
            Assert.False(SchemaSync.TryParseSqlSource("", out _, out _, out _, out _));
            Assert.False(SchemaSync.TryParseSqlSource(null, out _, out _, out _, out _));
        }

        // ---- pure diff -----------------------------------------------------------------------------

        private static ColumnRow Col(string table, string name, string type, bool calc = false) =>
            new ColumnRow { Ref = "column:" + table + "/" + name, Table = table, Name = name, DataType = type, IsCalculated = calc };

        [Fact]
        public void Diff_finds_added_removed_and_type_changed_and_ignores_calculated_columns()
        {
            var model = new[]
            {
                Col("Sales", "OrderId", "Int64"),
                Col("Sales", "Amount", "Decimal"),
                Col("Sales", "Region", "String"),
                Col("Sales", "LegacyCode", "String"),
                Col("Sales", "Margin", "Double", calc: true),   // calc column: never source-driven
            };
            var source = new[]
            {
                new SourceColumn { Name = "OrderId", DataType = "Int64" },
                new SourceColumn { Name = "Amount", DataType = "Double" },      // type drift
                new SourceColumn { Name = "Region", DataType = "String" },
                new SourceColumn { Name = "CustomerId", DataType = "Int64" },   // added at the source
                // LegacyCode dropped at the source → Removed
            };

            var diff = SchemaSync.Diff("table:Sales", "Sales", model, source, "supplied");

            Assert.True(diff.Reachable);
            Assert.Equal(1, diff.Added);
            Assert.Equal(1, diff.Removed);
            Assert.Equal(1, diff.TypeChanged);
            Assert.Contains(diff.Items, i => i.Change == "Added" && i.Column == "CustomerId" && i.SourceType == "Int64");
            Assert.Contains(diff.Items, i => i.Change == "Removed" && i.Column == "LegacyCode" && i.ColumnRef == "column:Sales/LegacyCode");
            Assert.Contains(diff.Items, i => i.Change == "TypeChanged" && i.Column == "Amount" && i.ModelType == "Decimal" && i.SourceType == "Double");
            // The calculated column is neither Removed (absent from source) nor otherwise flagged.
            Assert.DoesNotContain(diff.Items, i => i.Column == "Margin");
            // Ordering is Added, TypeChanged, Removed.
            Assert.Equal(new[] { "Added", "TypeChanged", "Removed" }, diff.Items.Select(i => i.Change).ToArray());
        }

        [Fact]
        public void Diff_of_a_matching_schema_is_empty()
        {
            var model = new[] { Col("D", "K", "Int64"), Col("D", "Name", "String") };
            var source = new[] { new SourceColumn { Name = "K", DataType = "Int64" }, new SourceColumn { Name = "Name", DataType = "String" } };
            var diff = SchemaSync.Diff("table:D", "D", model, source, "supplied");
            Assert.Empty(diff.Items);
            Assert.Equal(0, diff.Added + diff.Removed + diff.TypeChanged);
        }

        // ---- end-to-end diff + apply subset (offline, supplied source schema) ----------------------

        // A minimal injectable entitlement (mirrors EntitlementGateTests.Fake) that lets a test pin the tier
        // explicitly, independent of the CI env. Power Query (get_source_schema / diff_schema /
        // apply_schema_update) became Pro on 2026-09-15, so the diff+apply subject runs entitled.
        private sealed class Ent : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Ent(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        [Fact]
        public async Task Diff_then_apply_accepted_subset_is_one_undoable_change()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);   // Power Query is Pro; the subject is the diff+apply
            try
            {
                await engine.CreateModelAsync("SchemaSyncTest", 1604);
                var tableRef = await engine.CreateImportTableAsync("Sales", "let Source = Sql.Database(\"srv\",\"db\"), t = Source{[Schema=\"dbo\",Item=\"Sales\"]}[Data] in t", "agent");
                await engine.CreateColumnAsync(tableRef, "OrderId", "Int64", null, "agent");
                await engine.CreateColumnAsync(tableRef, "Amount", "Decimal", null, "agent");
                await engine.CreateColumnAsync(tableRef, "Region", "String", null, "agent");
                await engine.CreateColumnAsync(tableRef, "LegacyCode", "String", null, "agent");
                await engine.CreateCalculatedColumnAsync(tableRef, "Margin", "1", "agent");

                // Diff against a SUPPLIED source schema (offline path — no live probe).
                var source = new[]
                {
                    new SourceColumn { Name = "OrderId", DataType = "Int64" },
                    new SourceColumn { Name = "Amount", DataType = "Double" },
                    new SourceColumn { Name = "Region", DataType = "String" },
                    new SourceColumn { Name = "CustomerId", DataType = "Int64" },
                };
                var diff = await engine.DiffSchemaAsync(tableRef, source, "azcli", null);
                Assert.True(diff.Reachable);
                Assert.Equal("supplied", diff.Source);
                Assert.Equal(1, diff.Added);
                Assert.Equal(1, diff.Removed);
                Assert.Equal(1, diff.TypeChanged);

                // Accept ONLY the add + the retype (leave LegacyCode; also try to remove the calc column → skipped).
                var accepted = new[]
                {
                    new SchemaUpdateItem { Change = "Added", Column = "CustomerId", DataType = "Int64" },
                    new SchemaUpdateItem { Change = "TypeChanged", Column = "Amount", DataType = "Double" },
                    new SchemaUpdateItem { Change = "Removed", Column = "Margin" },   // calc column → skipped, not applied
                };
                var res = await engine.ApplySchemaUpdateAsync(tableRef, accepted, "agent");
                Assert.True(res.Changed);
                Assert.Equal(1, res.Added);
                Assert.Equal(1, res.Retyped);
                Assert.Equal(0, res.Removed);
                Assert.Contains(res.Skipped, s => s.StartsWith("Margin:"));

                var cols = (await engine.ListColumnsAsync()).Where(c => c.Table == "Sales").ToDictionary(c => c.Name);
                Assert.True(cols.ContainsKey("CustomerId"));
                Assert.Equal("Int64", cols["CustomerId"].DataType);
                Assert.Equal("Double", cols["Amount"].DataType);       // retyped
                Assert.True(cols.ContainsKey("LegacyCode"));            // NOT removed (we didn't accept it)
                Assert.True(cols.ContainsKey("Margin"));               // calc column untouched

                // One undo step reverts the WHOLE batch (add + retype together).
                await engine.UndoAsync("human");
                var after = (await engine.ListColumnsAsync()).Where(c => c.Table == "Sales").ToDictionary(c => c.Name);
                Assert.False(after.ContainsKey("CustomerId"));
                Assert.Equal("Decimal", after["Amount"].DataType);
            }
            finally { engine.Dispose(); }
        }

        // ---- graceful unreachable source -----------------------------------------------------------

        [Fact]
        public async Task Source_read_on_a_non_sql_table_fails_gracefully_without_throwing()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);   // the subject is the graceful read, not the gate
            try
            {
                await engine.CreateModelAsync("OfflineTest", 1604);
                // An M partition with NO SQL source → the parser can't derive server/db → unreachable, no connect attempt.
                var tableRef = await engine.CreateImportTableAsync("Inline", "let Source = #table({\"A\"},{{1}}) in Source", "agent");
                await engine.CreateColumnAsync(tableRef, "A", "Int64", null, "agent");

                var src = await engine.GetSourceSchemaAsync(tableRef, "azcli", null);
                Assert.False(src.Reachable);
                Assert.Contains("source unreachable", src.Error);
                Assert.Empty(src.Columns);

                // The diff op surfaces the same graceful failure (Reachable=false), not an exception.
                var diff = await engine.DiffSchemaAsync(tableRef, null, "azcli", null);
                Assert.False(diff.Reachable);
                Assert.Contains("source unreachable", diff.Error);
                Assert.Empty(diff.Items);
            }
            finally { engine.Dispose(); }
        }

        // ---- apply_schema_update is gated by FEATURE, never by item count ----
        // Power Query is Pro since 2026-09-15, so the tier decides whether you may update a schema at all. What
        // is still pinned is that SIZE decides nothing: the entitled caller applies a multi-item (bulk) subset
        // and a single change on exactly the same terms, and the free caller is refused the same way for both.

        [Fact]
        public async Task Bulk_and_single_schema_updates_are_the_same_gate_not_a_size_gate()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Ent(true));
            try
            {
                await engine.CreateModelAsync("SchemaTier", 1604);
                var tableRef = await engine.CreateImportTableAsync("Sales", "let Source = Sql.Database(\"srv\",\"db\"), t = Source{[Schema=\"dbo\",Item=\"Sales\"]}[Data] in t", "human");
                await engine.CreateColumnAsync(tableRef, "Amount", "Decimal", null, "human");

                // A >1-item accepted subset (the bulk primitive) applies with no extra condition on its size.
                var two = new[]
                {
                    new SchemaUpdateItem { Change = "TypeChanged", Column = "Amount", DataType = "Double" },
                    new SchemaUpdateItem { Change = "Added", Column = "CustomerId", DataType = "Int64" },
                };
                var bulk = await engine.ApplySchemaUpdateAsync(tableRef, two, "human");
                Assert.True(bulk.Changed && bulk.Added == 1 && bulk.Retyped == 1);
                var cols = (await engine.ListColumnsAsync()).Where(c => c.Table == "Sales").ToDictionary(c => c.Name);
                Assert.True(cols.ContainsKey("CustomerId"));
                Assert.Equal("Double", cols["Amount"].DataType);

                // ...and a single accepted change goes through the identical path.
                var one = new[] { new SchemaUpdateItem { Change = "Added", Column = "CustomerName", DataType = "String" } };
                Assert.True((await engine.ApplySchemaUpdateAsync(tableRef, one, "human")).Changed);
                Assert.Contains("CustomerName", (await engine.ListColumnsAsync()).Where(c => c.Table == "Sales").Select(c => c.Name));
            }
            finally { engine.Dispose(); }

            var freeSessions = new SessionManager();
            var free = new LocalEngine(freeSessions, new Ent(false));
            try
            {
                await free.CreateModelAsync("SchemaFree", 1604);
                var tableRef = await free.CreateImportTableAsync("Sales", "let Source = Sql.Database(\"srv\",\"db\"), t = Source{[Schema=\"dbo\",Item=\"Sales\"]}[Data] in t", "human");
                await free.CreateColumnAsync(tableRef, "Amount", "Decimal", null, "human");

                // One item or two, the free tier meets the same sentence: the refusal is about the feature.
                var one = new[] { new SchemaUpdateItem { Change = "Added", Column = "CustomerName", DataType = "String" } };
                var two = new[]
                {
                    new SchemaUpdateItem { Change = "TypeChanged", Column = "Amount", DataType = "Double" },
                    new SchemaUpdateItem { Change = "Added", Column = "CustomerId", DataType = "Int64" },
                };
                var single = await Assert.ThrowsAsync<EntitlementException>(() => free.ApplySchemaUpdateAsync(tableRef, one, "human"));
                var bulk = await Assert.ThrowsAsync<EntitlementException>(() => free.ApplySchemaUpdateAsync(tableRef, two, "human"));
                Assert.Contains("Power Query is a Semanticus Pro feature.", single.Message);
                Assert.Equal(single.Message, bulk.Message);
            }
            finally { free.Dispose(); }
        }
    }
}
