using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// One SQL endpoint per model (contract 19.3, Kane 24 September 2026: "yes as recommended for sql end points").
    /// A model links to one saved SQL source; its checks read from that source unless a check names its own; a
    /// working copy inherits it; and the upgrade links existing models from what their checks already use, by id,
    /// without changing where any check points and without writing a byte to <c>sql-sources.json</c>.
    ///
    /// Both doors are the same engine call: the MCP wrappers here are the real ones the agent calls, and
    /// <see cref="EngineRpcTarget"/> is the real VS Code door.
    /// </summary>
    [Collection("restore-root")]
    public sealed class ModelSqlEndpointTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public ModelSqlEndpointTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-model-sql-endpoint-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        // A saved model whose one table reads dbo.fact_sales through Sql.Database, so a row count has a schema and a
        // table to count even when the endpoint comes from the model's link rather than the partition.
        private async Task<LocalEngine> SavedModelAsync(string name)
        {
            var dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.CreateModelAsync(name, 1604);
            await engine.CreateImportTableAsync("Sales",
                "let Source = Sql.Database(\"contoso.datawarehouse.fabric.microsoft.com\", \"LH_Gold\"), t = Source{[Schema=\"dbo\",Item=\"fact_sales\"]}[Data] in t", "human");
            await engine.CreateMeasureAsync("Sales", "Total Rows", "COUNTROWS('Sales')", "human");
            await engine.SaveAsync(dir, "TMDL", false, "human");
            return engine;
        }

        private static SqlSourceRecord Source(string name, string server = null) =>
            SqlSourceRegistry.Save(null, name, server ?? (name.Replace(" ", "-").ToLowerInvariant() + ".database.windows.net"),
                "Warehouse", "interactive", null);

        private static Task<TestDefinition> SaveCheckAsync(LocalEngine engine, string title, string sqlSourceId,
            string server = null, string database = null, string measureRef = "Total Rows") =>
            engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureReconcile, Title = title, TargetRef = "Total Rows",
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                {
                    MeasureRef = measureRef, SqlSourceId = sqlSourceId, Server = server, Database = database,
                    Sql = "SELECT 1", BlankPolicy = "zero",
                }),
            }, "human");

        private static string SqlSourcesFile => Path.Combine(ConnectionRegistry.Root(), "sql-sources.json");

        // ---- set, read on both doors, clear ----------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Set_reads_back_the_same_on_both_doors_and_clears()
        {
            using var engine = await SavedModelAsync("Contoso Retail");
            var rpc = new EngineRpcTarget(engine);
            var warehouse = Source("Contoso warehouse");

            var set = await McpTools.SetModelSqlEndpoint(engine, warehouse.Id);
            Assert.Equal(warehouse.Id, set.SqlEndpoint.SourceId);

            var viaAgent = (await McpTools.GetConnectionContext(engine)).SqlEndpoint;
            var viaUi = (await rpc.connectionContext()).SqlEndpoint;
            foreach (var endpoint in new[] { viaAgent, viaUi })
            {
                Assert.NotNull(endpoint);
                Assert.Equal(warehouse.Id, endpoint.SourceId);
                Assert.Equal("Contoso warehouse", endpoint.Name);
                Assert.Equal(warehouse.Server, endpoint.Server);
                Assert.Equal("Warehouse", endpoint.Database);
                Assert.Null(endpoint.LastTest);   // never tested stays its own state, never rounded into ok
                Assert.False(endpoint.Missing);
            }

            // Cleared from the UI door, and both doors read none.
            var cleared = await rpc.setModelSqlEndpoint(null);
            Assert.Null(cleared.SqlEndpoint);
            Assert.True(cleared.Changed);
            Assert.Null((await McpTools.GetConnectionContext(engine)).SqlEndpoint);
            Assert.Null((await rpc.connectionContext()).SqlEndpoint);

            // Clearing again is a no-op that says so.
            var again = await McpTools.SetModelSqlEndpoint(engine, null);
            Assert.False(again.Changed);
            Assert.Contains("had no SQL endpoint", again.Note);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Setting_an_endpoint_that_is_not_saved_is_refused_in_plain_words()
        {
            using var engine = await SavedModelAsync("Contoso Retail");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SetModelSqlEndpoint(engine, "sql-gone"));
            Assert.Contains("no saved SQL source", ex.Message);
            Assert.DoesNotContain("\u2014", ex.Message);
        }

        [Fact]
        public async Task A_saved_model_that_is_not_open_can_be_given_an_endpoint_from_its_row()
        {
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            var record = ConnectionRegistry.Remember("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Contoso", "Contoso Sales", "Contoso Sales", null, "interactive");
            var warehouse = Source("Contoso warehouse");

            var set = await new EngineRpcTarget(engine).setModelSqlEndpoint(warehouse.Id, record.Id);

            Assert.Equal(warehouse.Id, set.SqlEndpoint.SourceId);
            Assert.Equal(record.Id, set.ConnectionId);
            var row = Assert.Single((await McpTools.ListModelSqlEndpoints(engine)).Rows);
            Assert.Equal(record.Id, row.ConnectionId);
            Assert.Equal(warehouse.Id, row.SqlEndpoint.SourceId);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_open_model_with_no_endpoint_is_offered_what_its_own_sources_read_from()
        {
            using var engine = await SavedModelAsync("Contoso Retail");
            var lakehouse = SqlSourceRegistry.Save(null, "Contoso lakehouse", "contoso.datawarehouse.fabric.microsoft.com", "LH_Gold", "interactive", null);
            // The source is saved but no check uses it, so the upgrade pass links nothing and the model's own
            // partitions are what the add form offers first.
            Assert.Null((await McpTools.GetConnectionContext(engine)).SqlEndpoint);

            var suggestion = (await engine.ConnectionContextAsync()).SqlEndpointSuggestion;
            Assert.NotNull(suggestion);
            Assert.Equal("contoso.datawarehouse.fabric.microsoft.com", suggestion.Server);
            Assert.Equal("LH_Gold", suggestion.Database);
            Assert.Equal(lakehouse.Id, suggestion.MatchingSourceId);
        }

        // ---- a working copy inherits -----------------------------------------------------------------------------

        [Fact]
        public async Task A_working_copy_uses_the_endpoint_of_the_model_it_came_from()
        {
            var record = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            var warehouse = Source("Contoso warehouse");
            var snapshotDir = Path.Combine(_root, "snapshot");
            Directory.CreateDirectory(snapshotDir);
            var snapshot = Path.Combine(snapshotDir, "Published.bim");
            File.Copy(TestModels.FindBim(), snapshot);
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            engine.WorkspaceTokenExportForTests = _ => Task.FromResult(new LiveModelExport.Snapshot
            {
                BimPath = snapshot, DatabaseName = "Published", DatabaseCount = 1, DatabaseNames = new[] { "Published" },
            });
            engine.WorkingCopyConnectForTests = r =>
            {
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", r.Endpoint, r.Database));
                return Task.CompletedTask;
            };
            await engine.SetModelSqlEndpointAsync(warehouse.Id, record.Id, "human");

            var result = await engine.PrepareWorkingCopyAsync(record.Id, Path.Combine(_root, "models"), commit: true, origin: "human");

            Assert.True(result.Opened);
            Assert.Null(sessions.Current.LiveOrigin);   // the copy is its own model on disk, with its own identity
            Assert.Equal(warehouse.Id, result.Context.SqlEndpoint?.SourceId);
            Assert.Equal(warehouse.Id, (await engine.ConnectionContextAsync()).SqlEndpoint?.SourceId);
        }

        [Fact]
        public async Task A_working_copy_that_cleared_its_inherited_endpoint_stays_cleared_when_reopened()
        {
            // Codex on pull request 394 (F-264): CopyIfAbsent looked only for a link, so a clear was undone on reopen.
            var record = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            var warehouse = Source("Contoso warehouse");
            var snapshotDir = Path.Combine(_root, "snapshot");
            Directory.CreateDirectory(snapshotDir);
            var snapshot = Path.Combine(snapshotDir, "Published.bim");
            File.Copy(TestModels.FindBim(), snapshot);
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            engine.WorkspaceTokenExportForTests = _ => Task.FromResult(new LiveModelExport.Snapshot
            {
                BimPath = snapshot, DatabaseName = "Published", DatabaseCount = 1, DatabaseNames = new[] { "Published" },
            });
            engine.WorkingCopyConnectForTests = r =>
            {
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", r.Endpoint, r.Database));
                return Task.CompletedTask;
            };
            await engine.SetModelSqlEndpointAsync(warehouse.Id, record.Id, "human");
            var parent = Path.Combine(_root, "models");
            var first = await engine.PrepareWorkingCopyAsync(record.Id, parent, commit: true, origin: "human");
            Assert.Equal(warehouse.Id, first.Context.SqlEndpoint?.SourceId);   // inherited before any choice of its own

            Assert.True((await McpTools.SetModelSqlEndpoint(engine, null)).Changed);   // the person clears it on the copy
            var reopened = await engine.PrepareWorkingCopyAsync(record.Id, parent, commit: true, origin: "human");

            Assert.Equal("open", reopened.Action);
            Assert.Null(reopened.Context.SqlEndpoint);
            Assert.Null((await engine.ConnectionContextAsync()).SqlEndpoint);
        }

        // ---- resolution order ------------------------------------------------------------------------------------

        [Fact]
        public void Resolution_order_is_the_checks_own_choice_then_the_model_endpoint_then_the_old_fallbacks()
        {
            var checkSource = Source("Check source");
            var modelSource = Source("Model source");

            // 1. The check's named source wins over everything.
            var named = SqlSources.Resolve(checkSource.Id, "inline.example.com", "InlineDb", null, null, modelSource.Id);
            Assert.Equal(checkSource.Id, named.SqlSourceId);
            Assert.False(named.FromModelEndpoint);

            // 2. The check's own inline coordinates are its own choice too, and win over the model's endpoint.
            var inline = SqlSources.Resolve(null, "inline.example.com", "InlineDb", null, null, modelSource.Id);
            Assert.Equal("inline.example.com", inline.Server);
            Assert.Null(inline.SqlSourceId);
            Assert.False(inline.FromModelEndpoint);

            // 3. A check that names nothing reads the model's endpoint.
            var model = SqlSources.Resolve(null, null, null, null, null, modelSource.Id);
            Assert.Equal(modelSource.Id, model.SqlSourceId);
            Assert.Equal(modelSource.Server, model.Server);
            Assert.True(model.FromSavedSource);
            Assert.True(model.FromModelEndpoint);

            // 4. No endpoint either: nothing is resolved here and the caller's old fallback (reading the model's own
            //    sources) decides, exactly as before.
            var none = SqlSources.Resolve(null, null, null, null, null, null);
            Assert.Null(none.Server);
            Assert.False(none.FromSavedSource);
            Assert.Null(none.Error);

            // An endpoint whose source has been removed is said out loud, never quietly replaced by the fallback.
            var gone = SqlSources.Resolve(null, null, null, null, null, "sql-gone");
            Assert.NotNull(gone.Error);
            Assert.Contains("SQL endpoint", gone.Error);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_quiet_per_check_override_wins_and_a_check_naming_nothing_reads_the_model_endpoint()
        {
            using var engine = await SavedModelAsync("Contoso Retail");
            var modelSource = Source("Contoso warehouse");
            var otherSource = Source("Contoso archive");
            await engine.SetModelSqlEndpointAsync(modelSource.Id, null, "human");
            var overriding = await SaveCheckAsync(engine, "archive total", otherSource.Id);
            var plain = await SaveCheckAsync(engine, "warehouse total", null);

            var o1 = await engine.TryTestAsync(overriding, "human");
            var o2 = await engine.TryTestAsync(plain, "human");

            Assert.Equal("Contoso archive", o1.SourceLabel);
            Assert.Equal("Contoso warehouse", o2.SourceLabel);
            Assert.Contains("needs a live connection", o2.Message);   // resolution succeeded; only the live model is missing
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_direct_reconcile_call_with_no_source_reads_the_model_endpoint()
        {
            var modelSource = Source("Contoso warehouse");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            var dir = Path.Combine(_root, "tiny");
            Directory.CreateDirectory(dir);
            await engine.CreateModelAsync("Tiny", 1604);
            // No SQL partition, so only the model's endpoint can supply where the ground truth is read.
            await engine.CreateImportTableAsync("Nums", "let Source = #table({\"n\"}, {{1}}) in Source", "human");
            await engine.CreateMeasureAsync("Nums", "Total", "COUNTROWS('Nums')", "human");
            await engine.SaveAsync(dir, "TMDL", false, "human");
            await McpTools.SetModelSqlEndpoint(engine, modelSource.Id);

            var run = await engine.ReconcileMeasureAsync(new ReconcileRequest
            {
                MeasureRef = "measure:Nums/Total", Sql = "SELECT 1", BlankPolicy = "zero",
            });

            Assert.DoesNotContain("No Fabric SQL endpoint", run.Error ?? "");
            Assert.Equal(ReconcileStatus.InsufficientCoverage, run.Status);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_row_count_with_no_source_argument_resolves_to_the_model_endpoint_on_both_doors()
        {
            using var engine = await SavedModelAsync("Contoso Retail");
            var rpc = new EngineRpcTarget(engine);
            var warehouse = Source("Contoso warehouse");
            await McpTools.SetModelSqlEndpoint(engine, warehouse.Id);

            var run = await McpToolsTesting.RunTests(engine, sections: new[] { "tableCounts" });
            var counted = Assert.Single(run.Relationships.TableRowCounts);
            Assert.Equal("Sales", counted.ModelTable);
            Assert.Equal(warehouse.Id, counted.SqlSourceId);
            Assert.Equal(warehouse.Server, counted.Server);
            Assert.Equal("Warehouse", counted.Database);
            Assert.Equal("dbo", counted.Schema);            // which table to count still comes from the model
            Assert.Equal("fact_sales", counted.Entity);

            foreach (var list in new[] { await McpTools.ListTableMappings(engine), await rpc.listTableMappings() })
            {
                var row = Assert.Single(list.Rows);
                Assert.Equal("endpoint", row.Source);
                Assert.Equal(warehouse.Id, row.SqlSourceId);
                Assert.True(row.CanCount);
            }

            // A per-table mapping is the quiet override for the model whose tables come from two warehouses.
            var archive = Source("Contoso archive");
            await McpTools.SetTableSourceMapping(engine, "Sales", archive.Id, "arch", "sales_2019");
            var mapped = Assert.Single((await McpToolsTesting.RunTests(engine, sections: new[] { "tableCounts" })).Relationships.TableRowCounts);
            Assert.Equal(archive.Id, mapped.SqlSourceId);
            Assert.Equal("sales_2019", mapped.Entity);
        }

        // ---- deleting a linked source ----------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_source_that_is_a_models_endpoint_is_not_removed_and_the_refusal_names_the_model()
        {
            using var engine = await SavedModelAsync("Contoso Retail");
            var warehouse = Source("Contoso warehouse");
            await McpTools.SetModelSqlEndpoint(engine, warehouse.Id);

            var refused = await McpTools.DeleteSqlSource(engine, warehouse.Id);

            Assert.False(refused.Deleted);
            Assert.Contains("Contoso Retail", refused.Note);
            Assert.Equal(new[] { "Contoso Retail" }, refused.LinkedModels);
            Assert.NotNull(SqlSourceRegistry.Find(warehouse.Id));

            await McpTools.SetModelSqlEndpoint(engine, null);
            Assert.True((await McpTools.DeleteSqlSource(engine, warehouse.Id)).Deleted);
        }

        // ---- migration, by id: the four cases --------------------------------------------------------------------

        [Fact]
        public async Task Migration_a_source_used_by_one_models_checks_becomes_that_models_endpoint()
        {
            using var engine = await SavedModelAsync("Contoso Retail");
            var warehouse = Source("Contoso warehouse");
            await SaveCheckAsync(engine, "revenue", warehouse.Id);
            var before = File.ReadAllBytes(SqlSourcesFile);

            var endpoint = (await McpTools.GetConnectionContext(engine)).SqlEndpoint;

            Assert.Equal(warehouse.Id, endpoint?.SourceId);
            Assert.Equal("migration", endpoint.SetBy);
            Assert.Equal(before, File.ReadAllBytes(SqlSourcesFile));   // links go beside, never into, sql-sources.json
            Assert.True(File.Exists(Path.Combine(ConnectionRegistry.Root(), ModelSqlEndpointRegistry.FileName)));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Migration_a_source_used_by_several_models_becomes_the_endpoint_of_each()
        {
            var warehouse = Source("Contoso warehouse");
            using var retail = await SavedModelAsync("Contoso Retail");
            using var sales = await SavedModelAsync("Contoso Sales");
            await SaveCheckAsync(retail, "retail revenue", warehouse.Id);
            await SaveCheckAsync(sales, "sales revenue", warehouse.Id);
            var before = File.ReadAllBytes(SqlSourcesFile);

            Assert.Equal(warehouse.Id, (await retail.ConnectionContextAsync()).SqlEndpoint?.SourceId);
            Assert.Equal(warehouse.Id, (await new EngineRpcTarget(sales).connectionContext()).SqlEndpoint?.SourceId);

            var rows = (await McpTools.ListModelSqlEndpoints(sales)).Rows;
            Assert.Equal(new[] { "Contoso Retail", "Contoso Sales" },
                rows.Where(r => r.SqlEndpoint?.SourceId == warehouse.Id).Select(r => r.ModelName).OrderBy(n => n).ToArray());
            Assert.Single(SqlSourceRegistry.List());   // still one saved record, shared
            Assert.Equal(before, File.ReadAllBytes(SqlSourcesFile));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Migration_a_source_used_by_no_model_is_listed_once_as_not_linked()
        {
            using var engine = await SavedModelAsync("Contoso Retail");
            var unused = Source("Contoso old warehouse");
            var before = File.ReadAllBytes(SqlSourcesFile);

            Assert.Null((await engine.ConnectionContextAsync()).SqlEndpoint);
            var viaAgent = await McpTools.ListModelSqlEndpoints(engine);
            var viaUi = await new EngineRpcTarget(engine).listModelSqlEndpoints();

            foreach (var list in new[] { viaAgent, viaUi })
            {
                var notLinked = Assert.Single(list.NotLinked);
                Assert.Equal(unused.Id, notLinked.Id);
                Assert.Equal("Contoso old warehouse", notLinked.Name);
                Assert.Equal(0, notLinked.ChecksUsing);
                Assert.Empty(list.Rows);
            }
            Assert.Equal(before, File.ReadAllBytes(SqlSourcesFile));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Migration_a_model_using_two_sources_gets_the_most_used_and_no_check_changes_where_it_points()
        {
            using var engine = await SavedModelAsync("Contoso Retail");
            var warehouse = Source("Contoso warehouse");
            var archive = Source("Contoso archive");
            var a1 = await SaveCheckAsync(engine, "revenue", warehouse.Id);
            var a2 = await SaveCheckAsync(engine, "units", warehouse.Id);
            var b1 = await SaveCheckAsync(engine, "archive revenue", archive.Id);
            var inline = await SaveCheckAsync(engine, "inline total", null, "inline-sql.example.com", "InlineDb");
            var checks = new[] { a1, a2, b1, inline };
            var before = File.ReadAllBytes(SqlSourcesFile);

            // Where each check resolves BEFORE the upgrade: the code path with no model endpoint at all.
            var beforeIds = checks.Select(c => ResolveCheck(c, null)).ToArray();

            var endpoint = (await engine.ConnectionContextAsync()).SqlEndpoint;
            Assert.Equal(warehouse.Id, endpoint?.SourceId);

            var afterIds = checks.Select(c => ResolveCheck(c, endpoint.SourceId)).ToArray();
            Assert.Equal(beforeIds, afterIds);
            Assert.Equal(new[] { warehouse.Id, warehouse.Id, archive.Id, "inline-sql.example.com" }, afterIds);

            // And through the engine's own run path, a reconciliation lands on the same named source after migration.
            Assert.Equal("Contoso archive", (await engine.TryTestAsync(b1, "human")).SourceLabel);
            Assert.Equal("Contoso warehouse", (await engine.TryTestAsync(a1, "human")).SourceLabel);
            Assert.Equal(before, File.ReadAllBytes(SqlSourcesFile));
        }

        private static string ResolveCheck(TestDefinition def, string modelEndpointId)
        {
            var request = TestSuiteStore.Deserialize<ReconcileRequest>(def.ParamsJson);
            var resolved = SqlSources.Resolve(request.SqlSourceId, request.Server, request.Database, request.AuthMode,
                request.TenantId, modelEndpointId);
            return resolved.SqlSourceId ?? resolved.Server;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Migration_pins_an_existing_sourceless_check_to_the_partitions_and_a_new_one_uses_the_endpoint()
        {
            // Kane's rule: no check changes where it points. A check saved before the upgrade that named no source
            // and no address read from the model's own partitions; it still does after the upgrade links an endpoint.
            // A check saved after the upgrade that names nothing reads the model's endpoint.
            using var engine = await SavedModelAsync("Contoso Retail");
            var warehouse = Source("Contoso warehouse");
            await SaveCheckAsync(engine, "revenue", warehouse.Id);
            var old = await SaveCheckAsync(engine, "old sourceless total", null);

            Assert.Equal(warehouse.Id, (await engine.ConnectionContextAsync()).SqlEndpoint?.SourceId);   // migration ran
            var fresh = await SaveCheckAsync(engine, "new sourceless total", null);

            var oldRun = await engine.TryTestAsync(old, "human");
            var freshRun = await engine.TryTestAsync(fresh, "human");

            Assert.NotEqual("Contoso warehouse", oldRun.SourceLabel);
            Assert.Equal("LH_Gold", oldRun.SourceLabel ?? "LH_Gold");   // the partitions, or nothing named: never the endpoint
            Assert.Equal("Contoso warehouse", freshRun.SourceLabel);

            // The same through the whole run, not only a single try.
            var run = await McpToolsTesting.RunTests(engine, sections: new[] { "measures" });
            Assert.NotEqual("Contoso warehouse", run.Reconciles.Single(o => o.DefId == old.Id).SourceLabel);
            Assert.Equal("Contoso warehouse", run.Reconciles.Single(o => o.DefId == fresh.Id).SourceLabel);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_endpoint_set_from_a_row_before_the_model_was_looked_at_still_leaves_old_checks_on_the_partitions()
        {
            using var engine = await SavedModelAsync("Contoso Retail");
            var warehouse = Source("Contoso warehouse");
            var old = await SaveCheckAsync(engine, "old sourceless total", null);
            var dir = Path.Combine(_root, "Contoso Retail");
            var record = ConnectionRegistry.Remember("file", dir, null, "Contoso Retail");

            await engine.SetModelSqlEndpointAsync(warehouse.Id, record.Id, "human");   // from the row, not the open model

            Assert.Equal(warehouse.Id, (await engine.ConnectionContextAsync()).SqlEndpoint?.SourceId);   // same link
            Assert.NotEqual("Contoso warehouse", (await engine.TryTestAsync(old, "human")).SourceLabel);
            var fresh = await SaveCheckAsync(engine, "new sourceless total", null);
            Assert.Equal("Contoso warehouse", (await engine.TryTestAsync(fresh, "human")).SourceLabel);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Migration_runs_once_so_an_endpoint_the_person_cleared_is_never_put_back()
        {
            using var engine = await SavedModelAsync("Contoso Retail");
            var warehouse = Source("Contoso warehouse");
            await SaveCheckAsync(engine, "revenue", warehouse.Id);
            Assert.Equal(warehouse.Id, (await engine.ConnectionContextAsync()).SqlEndpoint?.SourceId);

            await McpTools.SetModelSqlEndpoint(engine, null);

            Assert.Null((await engine.ConnectionContextAsync()).SqlEndpoint);
            Assert.Null((await engine.ConnectionContextAsync()).SqlEndpoint);
        }

        // ---- Sol 6 slice C review: resolve once, per-table sources, migration marked complete only after a look ------

        private static ResultSet OneValue(double v) => new ResultSet
        {
            Columns = new[] { new ColumnDef { Name = "[v]" } }, Rows = new[] { new object[] { v } }, RowCount = 1,
        };

        [Fact]
        public async Task A_pinned_check_runs_its_sql_against_the_partitions_not_the_model_endpoint()
        {
            // Sol 6: the first pass pinned the old check to the partitions, but the live run resolved it a second time
            // and landed on the model's endpoint. The seam records the connection the ground-truth SQL actually runs on.
            const string Measure = "measure:Sales/Total Rows";
            using var engine = await SavedModelAsync("Contoso Retail");
            var warehouse = Source("Contoso warehouse");
            await SaveCheckAsync(engine, "revenue", warehouse.Id, measureRef: Measure);
            var old = await SaveCheckAsync(engine, "old sourceless total", null, measureRef: Measure);
            Assert.Equal(warehouse.Id, (await engine.ConnectionContextAsync()).SqlEndpoint?.SourceId);   // migration ran
            var fresh = await SaveCheckAsync(engine, "new sourceless total", null, measureRef: Measure);

            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://example/workspace", "Contoso Retail", _ => OneValue(5)));
            var ran = new System.Collections.Generic.List<(string Server, string Database)>();
            engine.ReconcileSqlForTests = (server, database, sql) => { ran.Add((server, database)); return OneValue(5); };

            var oldRun = await engine.TryTestAsync(old, "human");
            Assert.Equal(("contoso.datawarehouse.fabric.microsoft.com", "LH_Gold"), Assert.Single(ran));
            Assert.Contains("The model and the source both give 5", oldRun.Message);   // it ran to a comparison, not a stop before the query

            ran.Clear();
            await engine.TryTestAsync(fresh, "human");
            Assert.Equal((warehouse.Server, "Warehouse"), Assert.Single(ran));

            // The whole run takes the same path (as a person: an agent's run asks first before reading source rows).
            ran.Clear();
            await engine.RunTestSuiteAsync(origin: "human", sections: new[] { "measures" });
            Assert.Contains(("contoso.datawarehouse.fabric.microsoft.com", "LH_Gold"), ran);
            Assert.Equal(2, ran.Count(r => r.Server == warehouse.Server));   // revenue names it; the new check reads the endpoint
            Assert.Equal(3, ran.Count);
        }

        [Fact]
        public async Task Migration_keeps_each_existing_unmapped_table_on_its_own_detected_source()
        {
            // Sol 6: migration picked the endpoint from one table's mapping, and every unmapped table then counted
            // there, even one whose partition read a different warehouse.
            using var engine = await SavedModelAsync("Contoso Retail");
            await engine.CreateImportTableAsync("Returns",
                "let Source = Sql.Database(\"returns.example.com\", \"LH_Returns\"), t = Source{[Schema=\"dbo\",Item=\"fact_returns\"]}[Data] in t", "human");
            await engine.SaveAsync(Path.Combine(_root, "Contoso Retail"), "TMDL", false, "human");
            var warehouse = Source("Contoso warehouse");
            await McpTools.SetTableSourceMapping(engine, "Sales", warehouse.Id, "dbo", "fact_sales");

            Assert.Equal(warehouse.Id, (await engine.ConnectionContextAsync()).SqlEndpoint?.SourceId);   // picked from the mapping

            var counts = (await McpToolsTesting.RunTests(engine, sections: new[] { "tableCounts" })).Relationships.TableRowCounts;
            var returns = counts.Single(c => c.ModelTable == "Returns");
            Assert.Equal("returns.example.com", returns.Server);
            Assert.Equal("LH_Returns", returns.Database);
            Assert.Equal(warehouse.Id, counts.Single(c => c.ModelTable == "Sales").SqlSourceId);
            var row = (await McpTools.ListTableMappings(engine)).Rows.Single(r => r.ModelTable == "Returns");
            Assert.Equal("detected", row.Source);
            Assert.Equal("returns.example.com", row.Server);

            // A table added after the upgrade has no earlier place to keep, so it reads the model's endpoint.
            await engine.CreateImportTableAsync("Stock",
                "let Source = Sql.Database(\"stock.example.com\", \"LH_Stock\"), t = Source{[Schema=\"dbo\",Item=\"fact_stock\"]}[Data] in t", "human");
            var stock = (await McpToolsTesting.RunTests(engine, sections: new[] { "tableCounts" })).Relationships.TableRowCounts
                .Single(c => c.ModelTable == "Stock");
            Assert.Equal(warehouse.Server, stock.Server);

            // And a person who then chooses the endpoint on purpose moves the unmapped tables with it.
            await McpTools.SetModelSqlEndpoint(engine, warehouse.Id);
            var chosen = (await McpToolsTesting.RunTests(engine, sections: new[] { "tableCounts" })).Relationships.TableRowCounts
                .Single(c => c.ModelTable == "Returns");
            Assert.Equal(warehouse.Server, chosen.Server);
        }

        [Fact]
        public async Task A_clear_then_a_set_from_the_row_still_leaves_old_checks_on_the_partitions()
        {
            // Sol 6: a clear from a row marked the model as looked at without reading its checks, so a later set from
            // the row moved an old sourceless check onto the new endpoint.
            using var engine = await SavedModelAsync("Contoso Retail");
            var warehouse = Source("Contoso warehouse");
            var old = await SaveCheckAsync(engine, "old sourceless total", null);
            var record = ConnectionRegistry.Remember("file", Path.Combine(_root, "Contoso Retail"), null, "Contoso Retail");

            await engine.SetModelSqlEndpointAsync(null, record.Id, "human");          // clear from the row
            await engine.SetModelSqlEndpointAsync(warehouse.Id, record.Id, "human");  // then set from the row

            Assert.Equal(warehouse.Id, (await engine.ConnectionContextAsync()).SqlEndpoint?.SourceId);
            Assert.NotEqual("Contoso warehouse", (await engine.TryTestAsync(old, "human")).SourceLabel);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_clear_from_the_row_is_kept_when_the_model_is_first_looked_at()
        {
            // The other half of the same fix: deferring the look must not let the upgrade put back what the person cleared.
            using var engine = await SavedModelAsync("Contoso Retail");
            var warehouse = Source("Contoso warehouse");
            await SaveCheckAsync(engine, "revenue", warehouse.Id);
            var old = await SaveCheckAsync(engine, "old sourceless total", null);
            var record = ConnectionRegistry.Remember("file", Path.Combine(_root, "Contoso Retail"), null, "Contoso Retail");

            await engine.SetModelSqlEndpointAsync(null, record.Id, "human");

            Assert.Null((await engine.ConnectionContextAsync()).SqlEndpoint);
            // Looked at now, so a later endpoint chosen on purpose still leaves the old check where it was.
            await McpTools.SetModelSqlEndpoint(engine, warehouse.Id);
            Assert.NotEqual("Contoso warehouse", (await engine.TryTestAsync(old, "human")).SourceLabel);
        }

        [Fact]
        public async Task Reopening_a_working_copy_that_inherits_an_endpoint_leaves_its_old_checks_on_the_partitions()
        {
            // Sol 6: CopyIfAbsent marked an existing working copy as looked at without reading its checks.
            var record = ConnectionRegistry.Remember("xmla", "powerbi://example/workspace", "Published", "Published", null, "interactive");
            var snapshotDir = Path.Combine(_root, "snapshot");
            Directory.CreateDirectory(snapshotDir);
            var snapshot = Path.Combine(snapshotDir, "Published.bim");
            File.Copy(TestModels.FindBim(), snapshot);
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            engine.WorkspaceTokenExportForTests = _ => Task.FromResult(new LiveModelExport.Snapshot
            {
                BimPath = snapshot, DatabaseName = "Published", DatabaseCount = 1, DatabaseNames = new[] { "Published" },
            });
            engine.WorkingCopyConnectForTests = r =>
            {
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", r.Endpoint, r.Database));
                return Task.CompletedTask;
            };
            var parent = Path.Combine(_root, "models");
            Assert.True((await engine.PrepareWorkingCopyAsync(record.Id, parent, commit: true, origin: "human")).Opened);
            var measure = (await engine.ListMeasuresAsync()).First();
            var old = await engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureReconcile, Title = "old sourceless total", TargetRef = measure.Name,
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest { MeasureRef = measure.Name, Sql = "SELECT 1", BlankPolicy = "zero" }),
            }, "human");

            var warehouse = Source("Contoso warehouse");
            await engine.SetModelSqlEndpointAsync(warehouse.Id, record.Id, "human");   // the published model gets one later
            var reopened = await engine.PrepareWorkingCopyAsync(record.Id, parent, commit: true, origin: "human");

            Assert.Equal("open", reopened.Action);
            Assert.Equal(warehouse.Id, reopened.Context.SqlEndpoint?.SourceId);        // the copy inherits it
            Assert.NotEqual("Contoso warehouse", (await engine.TryTestAsync(old, "human")).SourceLabel);
        }

        // ---- both doors, free, on a shelf ------------------------------------------------------------------------

        [Fact]
        public void Both_ops_are_on_both_doors_free_and_on_the_connections_shelf()
        {
            foreach (var (op, rpc, method) in new[]
            {
                ("set_model_sql_endpoint", "setModelSqlEndpoint", "SetModelSqlEndpointAsync"),
                ("list_model_sql_endpoints", "listModelSqlEndpoints", "ListModelSqlEndpointsAsync"),
            })
            {
                Assert.Contains(op, ProFeatureMapTests.CompiledOperationNames());
                Assert.Contains(ProFeatureMapTests.RpcEntries(), m => m.Name == rpc);
                Assert.Contains(op, FeatureMap.FreeOperations);
                Assert.Contains(rpc, FeatureMap.FreeRpcMethods);
                Assert.Contains(method, FeatureMap.FreeEngineMethods);
                Assert.True(OpTaxonomy.TryGet(op, out var question, out var shelf));
                Assert.Equal("Open a model and connect", question);
                Assert.Equal("Connections and targets", shelf);
            }
        }

        [Fact]
        public async Task The_free_tier_can_set_and_read_an_endpoint()
        {
            var dir = Path.Combine(_root, "free");
            Directory.CreateDirectory(dir);
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free);
            await engine.CreateModelAsync("Free model", 1604);
            await engine.SaveAsync(dir, "TMDL", false, "human");
            var warehouse = Source("Contoso warehouse");

            await McpTools.SetModelSqlEndpoint(engine, warehouse.Id);

            Assert.Equal(warehouse.Id, (await engine.ConnectionContextAsync()).SqlEndpoint?.SourceId);
        }
    }
}
