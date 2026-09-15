using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Named SQL sources on BOTH doors, one engine path (golden rule 2). The MCP wrappers here are the real ones the
    /// agent calls; they are driven against the same LocalEngine the VS Code door drives through JSON-RPC, so the two
    /// cannot diverge.
    ///
    /// The refusal is the part worth pinning: removing a source that checks or table mappings still point at would
    /// break them silently, so the delete says no AND says how many of each.
    /// </summary>
    [Collection("restore-root")]
    public sealed class SqlSourceDoorTests : IDisposable
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _modelDir;

        public SqlSourceDoorTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-sqldoor-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            _modelDir = Path.Combine(_root, "model");
            Directory.CreateDirectory(_modelDir);
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            SqlSourceProbe.TokenForTests = null;
            SqlSourceProbe.QueryForTests = null;
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private async Task<LocalEngine> OpenModelAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.CreateModelAsync("Rows", 1604);
            await engine.CreateImportTableAsync("Sales",
                "let Source = Sql.Database(\"fabric.example.com\", \"LH_Gold\"), t = Source{[Schema=\"dbo\",Item=\"fact_sales\"]}[Data] in t", "human");
            await engine.CreateMeasureAsync("Sales", "Total Rows", "COUNTROWS('Sales')", "human");
            await engine.SaveAsync(_modelDir, "TMDL");
            return engine;
        }

        [Fact]
        public async Task The_agent_door_saves_lists_and_removes_a_source_through_the_same_engine()
        {
            using var engine = await OpenModelAsync();

            var saved = await McpTools.SaveSqlSource(engine, "Contoso warehouse",
                "contoso-sql.database.windows.net", "Warehouse", "interactive");
            Assert.Equal("Contoso warehouse", saved.Name);

            // The UI door reads the same registry, not a copy of it.
            var listed = await engine.ListSqlSourcesAsync();
            Assert.Equal(saved.Id, Assert.Single(listed).Id);
            Assert.Equal(saved.Id, Assert.Single(await McpTools.ListSqlSources(engine)).Id);

            var removed = await McpTools.DeleteSqlSource(engine, saved.Id);
            Assert.True(removed.Deleted);
            Assert.Contains("Nothing was using it", removed.Note);
            Assert.Empty(await engine.ListSqlSourcesAsync());
        }

        [Fact]
        public async Task Removing_a_source_a_check_still_uses_is_refused_and_says_how_many()
        {
            using var engine = await OpenModelAsync();
            var source = await McpTools.SaveSqlSource(engine, "Contoso warehouse",
                "contoso-sql.database.windows.net", "Warehouse", "interactive");

            foreach (var title in new[] { "revenue vs source", "units vs source" })
                await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureReconcile, Title = title, TargetRef = "Total Rows",
                    ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                    {
                        MeasureRef = "Total Rows", SqlSourceId = source.Id, Sql = "SELECT 1", BlankPolicy = "zero",
                    }),
                }, "human");

            var refused = await McpTools.DeleteSqlSource(engine, source.Id);

            Assert.False(refused.Deleted);
            Assert.Equal(2, refused.ChecksUsing);
            Assert.Equal(0, refused.TableMappingsUsing);
            // The refusal NAMES what needs repair, so the person is not sent hunting.
            Assert.Contains("is still in use, so it was not removed.", refused.Note);
            Assert.Contains("2 checks use it (revenue vs source, units vs source)", refused.Note);
            Assert.Equal(new[] { "revenue vs source", "units vs source" }, refused.CheckTitles);
            Assert.Empty(refused.MappedTables);
            Assert.DoesNotContain("—", refused.Note);
            Assert.Single(await engine.ListSqlSourcesAsync());
        }

        [Fact]
        public async Task Removing_a_source_a_table_mapping_still_uses_is_refused_and_says_how_many()
        {
            using var engine = await OpenModelAsync();
            var source = await McpTools.SaveSqlSource(engine, "Contoso warehouse",
                "contoso-sql.database.windows.net", "Warehouse", "interactive");
            await McpTools.SetTableSourceMapping(engine, "Sales", source.Id, "sales", "orders");

            var refused = await McpTools.DeleteSqlSource(engine, source.Id);

            Assert.False(refused.Deleted);
            Assert.Equal(0, refused.ChecksUsing);
            Assert.Equal(1, refused.TableMappingsUsing);
            Assert.Contains("1 table is mapped to it (Sales)", refused.Note);
            Assert.Equal(new[] { "Sales" }, refused.MappedTables);

            // Clearing the mapping frees the source.
            Assert.True(await McpTools.ClearTableSourceMapping(engine, "Sales"));
            Assert.True((await McpTools.DeleteSqlSource(engine, source.Id)).Deleted);
        }

        [Fact]
        public async Task With_no_model_open_the_delete_refuses_rather_than_guessing_that_nothing_uses_it()
        {
            using var engine = new LocalEngine(new SessionManager());
            var source = SqlSourceRegistry.Save(null, "Contoso warehouse",
                "contoso-sql.database.windows.net", "Warehouse", "interactive", null);

            var refused = await engine.DeleteSqlSourceAsync(source.Id);

            Assert.False(refused.Deleted);
            Assert.Contains("Open the model first", refused.Note);
            Assert.Single(SqlSourceRegistry.List());
        }

        [Fact]
        public async Task Testing_a_source_through_the_agent_door_returns_the_same_shape_as_the_ui_door()
        {
            using var engine = await OpenModelAsync();
            var source = await McpTools.SaveSqlSource(engine, "Contoso warehouse",
                "contoso-sql.database.windows.net", "Warehouse", "devicecode", "tenant-7");
            SqlSourceProbe.TokenForTests = (mode, tenant, ct) => Task.FromResult(mode + "/" + tenant);
            SqlSourceProbe.QueryForTests = (rec, token, ct) => Task.FromResult(new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "semanticus_connection_test", Type = "Int64" } },
                Rows = new[] { new object[] { 1L } },
                RowCount = 1,
            });

            // The human door tests it and it works.
            var viaUi = await engine.TestSqlSourceAsync(source.Id);
            Assert.True(viaUi.Ok);
            Assert.Equal("Connected to Warehouse on contoso-sql.database.windows.net.", viaUi.Note);
            Assert.True(SqlSourceRegistry.Find(source.Id).LastTestOk);

            // The agent door is the SAME engine call, governed by the standard QueryData gate on that SQL
            // target: an unlabelled target is read as production, the strictest reading. A gated agent gets a
            // plain refusal in the result, never an exception and never a silent ok. The policy root is pinned
            // to a fresh directory for this assertion so the DEFAULT policy is what decides, not whatever
            // another test in this collection last wrote into the shared fixture root.
            var policyRoot = Path.Combine(_root, "policy-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var policyBefore = AgentPolicyStore.RootOverride;
            var ledgerBefore = ApprovalLedger.RootOverride;
            AgentPolicyStore.RootOverride = policyRoot;
            ApprovalLedger.RootOverride = policyRoot;
            try
            {
                var viaAgent = await McpTools.TestSqlSource(engine, source.Id);
                Assert.Equal(source.Id, viaAgent.Id);
                Assert.False(string.IsNullOrWhiteSpace(viaAgent.Note));
                Assert.False(viaAgent.Ok);
            }
            finally
            {
                AgentPolicyStore.RootOverride = policyBefore;
                ApprovalLedger.RootOverride = ledgerBefore;
            }
        }

        [Fact]
        public async Task The_saved_checks_list_names_the_source_each_check_uses()
        {
            using var engine = await OpenModelAsync();
            var source = await McpTools.SaveSqlSource(engine, "Contoso warehouse",
                "contoso-sql.database.windows.net", "Warehouse", "interactive");
            await engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureReconcile, Title = "revenue vs source", TargetRef = "Total Rows",
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                {
                    MeasureRef = "Total Rows", SqlSourceId = source.Id, Sql = "SELECT 1", BlankPolicy = "zero",
                }),
            }, "human");

            var info = await McpToolsTesting.ListTests(engine);
            var use = Assert.Single(info.SqlSourceUse);

            Assert.Equal("Contoso warehouse", use.SourceName);
            Assert.Equal(source.Id, use.SqlSourceId);
            Assert.False(use.Missing);
        }

        [Fact]
        public async Task The_agent_door_reads_the_table_mapping_list_through_the_same_engine()
        {
            using var engine = await OpenModelAsync();
            var source = await McpTools.SaveSqlSource(engine, "Contoso warehouse",
                "contoso-sql.database.windows.net", "Warehouse", "interactive");
            await McpTools.SetTableSourceMapping(engine, "Sales", source.Id, "sales", "orders");

            var viaAgent = await McpTools.ListTableMappings(engine);
            var viaUi = await engine.ListTableSourceMappingsAsync();

            var agentRow = viaAgent.Rows.Single(r => r.ModelTable == "Sales");
            var uiRow = viaUi.Rows.Single(r => r.ModelTable == "Sales");
            Assert.Equal("mapping", agentRow.Source);
            Assert.Equal(uiRow.SqlSourceId, agentRow.SqlSourceId);
            Assert.Equal(uiRow.SqlSourceName, agentRow.SqlSourceName);
            Assert.True(agentRow.CanCount);
        }

        [Fact]
        public void Every_new_operation_has_a_home_in_the_ratified_taxonomy()
        {
            foreach (var op in new[]
            {
                "list_sql_sources", "save_sql_source", "test_sql_source", "delete_sql_source",
                "set_table_source_mapping", "clear_table_source_mapping", "list_table_mappings",
            })
            {
                Assert.True(OpTaxonomy.TryGet(op, out var question, out var shelf), op + " has no taxonomy home");
                Assert.False(string.IsNullOrWhiteSpace(question));
                Assert.False(string.IsNullOrWhiteSpace(shelf));
            }
        }
    }
}
