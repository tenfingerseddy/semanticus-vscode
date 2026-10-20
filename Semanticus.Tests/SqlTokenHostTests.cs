using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The engine mints a SQL sign-in token only for a server the person chose on this machine. A cloned repo can ship
    /// .semanticus/tests/suite.jsonl whose check names a server the sender owns, with a lineage tag copied from the
    /// model so the open model claims it. Without this rule, pressing Run checks sent the person's Azure SQL token to that
    /// host. The check now says so in plain words and runs nothing; a saved source for the same host runs as ever.
    /// </summary>
    [Collection("restore-root")]
    public sealed class SqlTokenHostTests : IDisposable
    {
        private const string Attacker = "attacker.example.net";
        private const string SavedHost = "contoso-sql.database.windows.net";
        private const string DaxEndpoint = "powerbi://example/workspace";

        private readonly string _root;
        private readonly string _safeRoot;

        public SqlTokenHostTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-tokenhost-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        // F-295 (Codex on pull request 402): HostOf cut "tcp:[2001:db8::1],1433" at its first colon and returned "[2001", so trusting one
        // 2001:* server trusted every server with that first group, and the person's SQL token could go to a stranger without a question.
        [Theory]
        [InlineData("tcp:[2001:db8::1],1433")]
        [InlineData("TCP:[2001:DB8::1],1433")]
        [InlineData("[2001:db8::1]:1433")]
        [InlineData("[2001:db8::1]")]
        [InlineData("2001:db8::1")]
        [InlineData("tcp:2001:db8::1,1433")]
        [InlineData("[2001:db8:0:0:0:0:0:1]\\Instance,1433")]
        [InlineData(" tcp:[2001:db8::0001] ")]
        public void An_ipv6_server_is_its_whole_address_in_every_form_it_is_written(string written)
        {
            Assert.Equal("2001:db8::1", SqlHostTrust.HostOf(written));
        }

        [Fact]
        public void Two_ipv6_servers_with_the_same_first_group_are_different_hosts()
        {
            Assert.NotEqual(SqlHostTrust.HostOf("tcp:[2001:db8::1],1433"), SqlHostTrust.HostOf("tcp:[2001:db8::2],1433"));
            Assert.NotEqual(SqlHostTrust.HostOf("[2001:db8::1]"), SqlHostTrust.HostOf("[2001:4860::1]"));
            Assert.Equal("2001:4860::1", SqlHostTrust.HostOf("[2001:4860::1]:1433"));
            // The names and the IPv4 addresses that worked before still read as they did.
            Assert.Equal("contoso.database.windows.net", SqlHostTrust.HostOf("TCP:Contoso.database.windows.net,1433"));
            Assert.Equal("10.0.0.5", SqlHostTrust.HostOf("10.0.0.5,1433"));
            Assert.Equal("sqlbox", SqlHostTrust.HostOf("sqlbox:1433"));
            Assert.Equal("sqlbox", SqlHostTrust.HostOf("sqlbox\\inst"));
        }

        [Fact]
        public void A_saved_ipv6_server_is_trusted_and_another_server_with_the_same_first_group_is_not()
        {
            ResetSession();
            SqlSourceRegistry.Save(null, "Lab", "tcp:[2001:db8::1],1433", "Db", "azcli", null);
            Assert.True(SqlHostTrust.IsSaved("[2001:db8::1]:1433"));
            Assert.True(SqlHostTrust.MayReceiveToken("2001:DB8::1"));
            Assert.False(SqlHostTrust.IsSaved("tcp:[2001:db8::2],1433"));
            Assert.False(SqlHostTrust.MayReceiveToken("tcp:[2001:db8::2],1433"));
            Assert.False(SqlHostTrust.MayReceiveToken("[2001:4860::1]"));
            // A yes in this session covers that one address and no other.
            SqlHostTrust.TrustForSession("tcp:[2001:4860::1],1433");
            Assert.True(SqlHostTrust.IsTrustedThisSession("[2001:4860::1]"));
            Assert.False(SqlHostTrust.IsTrustedThisSession("[2001:4860::2]"));
            ResetSession();
        }

        [Theory]
        [InlineData("[2001")]
        [InlineData("tcp:[2001:db8::1")]
        [InlineData("[2001:zz::1],1433")]
        [InlineData("[]")]
        [InlineData("2001:db8:::1")]
        public void An_ipv6_address_that_cannot_be_read_is_never_trusted_by_its_first_group(string written)
        {
            ResetSession();
            SqlSourceRegistry.Save(null, "Lab", "[2001:db8::1]", "Db", "azcli", null);
            Assert.Equal("", SqlHostTrust.HostOf(written));
            Assert.False(SqlHostTrust.IsSaved(written));
            SqlHostTrust.TrustForSession(written);
            Assert.False(SqlHostTrust.IsTrustedThisSession(written));
            Assert.False(SqlHostTrust.MayReceiveToken(written));
            ResetSession();
        }

        // Verifier v-fix-codex402 (F-295 bar): two different servers never share a trust key, whatever the spelling.
        [Theory]
        [InlineData("tcp:[2001:db8::1],1433", "tcp:[2001:db8::1:0],1433")]
        [InlineData("[fe80::1%1]", "[fe80::1%2]")]
        [InlineData("[::ffff:10.0.0.5]", "[::ffff:10.0.0.6]")]
        [InlineData("::ffff:10.0.0.5,1433", "::ffff:10.0.0.6,1433")]
        [InlineData("[2001:db8::1]\\inst", "[2001:db8::2]\\inst")]
        [InlineData("tcp: [2001:db8::1] ,1433", "tcp:[2001:db8::2],1433")]
        [InlineData("np:\\\\sqlA\\pipe\\sql\\query", "np:\\\\sqlB\\pipe\\sql\\query")]
        [InlineData("np:\\\\sqlA\\pipe\\one", "np:\\\\sqlA\\pipe\\two")]
        [InlineData("admin:sqlA", "admin:sqlB")]
        [InlineData("admin:sqlA", "lpc:sqlA")]
        [InlineData("admin:sqlA", "sqlA")]
        [InlineData("np:\\\\sqlA\\pipe\\sql\\query", "tcp:sqlA")]
        [InlineData("lpc:sqlA", "lpc:sqlB")]
        public void Verifier_two_different_servers_never_share_a_trust_key(string one, string other)
        {
            var a = SqlHostTrust.HostOf(one);
            var b = SqlHostTrust.HostOf(other);
            Assert.True(a.Length == 0 || a != b, $"'{one}' and '{other}' share the key '{a}'");
            ResetSession();
            SqlHostTrust.TrustForSession(one);
            Assert.False(SqlHostTrust.IsTrustedThisSession(other), $"trusting '{one}' trusted '{other}'");
            ResetSession();
        }

        // A prefix with nothing after it, or another prefix, names no server: it gives no key and is never trusted.
        [Theory]
        [InlineData("np:")]
        [InlineData("np:   ")]
        [InlineData("lpc:")]
        [InlineData("admin:")]
        [InlineData("admin:admin:sqlA")]
        [InlineData("lpc:np:\\\\sqlA\\pipe")]
        public void A_prefix_with_no_readable_server_gives_no_key_and_is_never_trusted(string written)
        {
            ResetSession();
            Assert.Equal("", SqlHostTrust.HostOf(written));
            SqlHostTrust.TrustForSession(written);
            Assert.False(SqlHostTrust.IsTrustedThisSession(written));
            ResetSession();
        }

        // The same pipe, however it is written, is one key, and the colons inside the prefix are not where the host is cut.
        [Fact]
        public void One_named_pipe_written_two_ways_is_one_key_and_keeps_its_whole_path()
        {
            Assert.Equal(SqlHostTrust.HostOf("np:\\\\SqlA\\pipe\\sql\\query"), SqlHostTrust.HostOf("NP: \\\\sqla\\PIPE\\sql\\query "));
            Assert.Equal("np:\\\\sqla\\pipe\\sql\\query", SqlHostTrust.HostOf("np:\\\\SqlA\\pipe\\sql\\query"));
            Assert.Equal("admin:sqla", SqlHostTrust.HostOf("ADMIN:SqlA,1434"));
            Assert.Equal("lpc:sqla", SqlHostTrust.HostOf("lpc:sqlA\\inst"));
        }

        // An address that cannot be read never earns trust from a near spelling.
        [Theory]
        [InlineData("[2001:db8::1")]
        [InlineData("[2001:db8::1]]")]
        [InlineData("[[2001:db8::1]]")]
        [InlineData("[fe80::1%]")]
        [InlineData("2001:db8::1::2")]
        [InlineData("[:]")]
        public void Verifier_an_unreadable_ipv6_spelling_is_never_trusted(string written)
        {
            ResetSession();
            SqlSourceRegistry.Save(null, "Lab", "[2001:db8::1]", "Db", "azcli", null);
            Assert.False(SqlHostTrust.MayReceiveToken(written) && SqlHostTrust.HostOf(written) != "2001:db8::1", $"'{written}' read as '{SqlHostTrust.HostOf(written)}'");
            Assert.NotEqual("2001", SqlHostTrust.HostOf(written));
            Assert.NotEqual("[2001", SqlHostTrust.HostOf(written));
            ResetSession();
        }

        // Verifier v2-codex402 (F-295): keeping the prefix in the key must not make a pipe or admin form pass as a Microsoft host by its tail.
        [Theory]
        [InlineData("np:\\\\attacker\\pipe\\x.database.windows.net")]
        [InlineData("np:\\\\attacker\\pipe\\sql\\query.database.windows.net")]
        [InlineData("np:attacker.database.windows.net")]
        [InlineData("lpc:x.database.windows.net")]
        [InlineData("admin:x.database.windows.net")]
        [InlineData("tcp:np:\\\\attacker\\pipe\\x.fabric.microsoft.com")]
        public void V2_a_pipe_or_prefixed_form_is_never_a_microsoft_host(string written)
        {
            ResetSession();
            Assert.False(SqlHostTrust.IsMicrosoftHost(written), $"'{written}' read as a Microsoft host ('{SqlHostTrust.HostOf(written)}')");
            Assert.False(SqlHostTrust.MayReceiveToken(written));
            ResetSession();
        }

        // A saved named pipe trusts that pipe only, never another pipe on the same machine or another machine.
        [Fact]
        public void V2_a_saved_pipe_trusts_that_pipe_only()
        {
            ResetSession();
            SqlSourceRegistry.Save(null, "Pipe", "np:\\\\sqlA\\pipe\\sql\\query", "Db", "azcli", null);
            Assert.True(SqlHostTrust.MayReceiveToken("NP:\\\\SQLA\\pipe\\sql\\query"));
            Assert.False(SqlHostTrust.MayReceiveToken("np:\\\\sqlA\\pipe\\other"));
            Assert.False(SqlHostTrust.MayReceiveToken("np:\\\\sqlB\\pipe\\sql\\query"));
            Assert.False(SqlHostTrust.MayReceiveToken("np"));
            Assert.False(SqlHostTrust.MayReceiveToken("np:"));
            ResetSession();
        }

        private static void ResetSession() => SqlHostTrust.ResetSessionForTest();

        private SessionManager _sessions;
        private readonly List<string> _tokenCalls = new List<string>();
        private readonly List<string> _sqlServers = new List<string>();

        private async Task<LocalEngine> ModelAsync(string name)
        {
            var dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            _sessions = new SessionManager();
            var engine = new LocalEngine(_sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync(name, 1604);
            await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync("Sales", "Region", "string", null, "human");
            await engine.CreateMeasureAsync("Sales", "Total Rows", "COUNTROWS('Sales')", "human");
            await engine.SaveAsync(dir, "TMDL", false, "human");
            engine.SqlTokenFetchForTests = (mode, tenant, origin) => { lock (_tokenCalls) _tokenCalls.Add(origin); return "token"; };
            engine.ReconcileSqlForTests = (server, db, sql) => { lock (_sqlServers) _sqlServers.Add(server); return SqlAnswer(sql); };
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, name, DaxAnswer));
            return engine;
        }

        private static ReconcileRequest Request(string server, string sqlSourceId = null, string database = "Db") => new ReconcileRequest
        {
            MeasureRef = "Total Rows", Server = server, Database = database, SqlSourceId = sqlSourceId, BlankPolicy = "zero",
            GroupBy = new[] { "Sales[Region]" }, Sql = "SELECT members", SqlGrandTotal = "SELECT total",
        };

        private static TestDefinition Def(ReconcileRequest request) => new TestDefinition
        {
            Kind = TestKinds.MeasureReconcile, Title = "Total Rows by region", TargetRef = "measure:Sales/Total Rows",
            ParamsJson = TestSuiteStore.Serialize(request),
        };

        /// <summary>Make the saved check read like one that arrived in a cloned repo: no model identity, bound only by the
        /// measure's lineage tag, and its own server in the file.</summary>
        private void MakeItArrivedFromAClone(string server)
        {
            var file = Directory.GetFiles(_root, TestSuiteStore.SuiteFile, SearchOption.AllDirectories).Single();
            var def = TestSuiteStore.Deserialize<TestDefinition>(File.ReadAllLines(file).First(l => l.Trim().Length > 0));
            Assert.False(string.IsNullOrEmpty(def.TargetTag), "the check should be bound by the measure's lineage tag");
            def.ModelIdentity = null;
            var request = TestSuiteStore.Deserialize<ReconcileRequest>(def.ParamsJson);
            request.Server = server;
            request.SqlSourceId = null;
            def.ParamsJson = TestSuiteStore.Serialize(request);
            File.WriteAllText(file, TestSuiteStore.Serialize(def) + "\n");
        }

        private static ResultSet DaxAnswer(string q) => q.Contains("SUMMARIZECOLUMNS")
            ? new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "Region" }, new ColumnDef { Name = "__recon_value" }, new ColumnDef { Name = "__recon_present" } },
                Rows = new[] { new object[] { "East", 10.0, 1L }, new object[] { "West", 5.0, 1L } }, RowCount = 2, ElapsedMs = 11,
            }
            : Value(15.0);

        private static ResultSet SqlAnswer(string sql) => sql.Contains("total")
            ? Value(15.0)
            : new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "region" }, new ColumnDef { Name = "value" } },
                Rows = new[] { new object[] { "East", 10.0 }, new object[] { "West", 5.0 } }, RowCount = 2, ElapsedMs = 7,
            };

        private static ResultSet Value(object v) => new ResultSet
        {
            Columns = new[] { new ColumnDef { Name = "[v]" } }, Rows = new[] { new object[] { v } }, RowCount = 1,
        };

        private static ReconcileOutcome[] Ran(TestSuiteRunResult run) =>
            run.Reconciles.Where(o => !o.DefId.StartsWith("unmapped:", StringComparison.Ordinal)).ToArray();

        private string ExpectedWords(string host) => $"This check names a server you have not saved: {host}. Open it to choose a source.";

        [Fact]
        public async Task A_cloned_checks_inline_server_gets_no_token_and_no_sql_in_Run_checks()
        {
            using var engine = await ModelAsync("Clone");
            await engine.SaveTestDefinitionAsync(Def(Request(SavedHost)), "human");
            MakeItArrivedFromAClone(Attacker);

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });

            var outcome = Assert.Single(Ran(run));
            Assert.Empty(_tokenCalls);
            Assert.Empty(_sqlServers);
            Assert.Equal(Verdict.NotVerifiable, outcome.Verdict);
            Assert.Equal(ExpectedWords(Attacker), outcome.Message);
            Assert.Equal(Attacker, outcome.UnsavedServer);
        }

        [Fact]
        public async Task Try_it_now_and_the_assistant_run_get_the_same_refusal()
        {
            using var engine = await ModelAsync("TryIt");
            var tried = await engine.TryTestAsync(Def(Request(Attacker)), "human");
            Assert.Equal(ExpectedWords(Attacker), tried.Message);
            Assert.Equal(Attacker, tried.UnsavedServer);

            var asked = await engine.TryTestAsync(Def(Request(Attacker)), "agent");
            Assert.Equal(ExpectedWords(Attacker), asked.Message);

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" }, origin: "agent");
            Assert.All(Ran(run), o => Assert.DoesNotContain(Attacker, o.Message ?? "", StringComparison.Ordinal) /* nothing saved here */);
            Assert.Empty(_tokenCalls);
            Assert.Empty(_sqlServers);
        }

        [Fact]
        public async Task The_assistants_direct_reconcile_refuses_an_unsaved_server_and_a_person_who_typed_it_is_not_refused()
        {
            using var engine = await ModelAsync("Direct");

            var agent = await engine.ReconcileMeasureAsync(Request(Attacker), "agent");
            Assert.Equal(ExpectedWords(Attacker), agent.Error);
            Assert.Empty(_tokenCalls);
            Assert.Empty(_sqlServers);

            // A person at the keyboard typed this server into a call of their own in this session.
            var human = await engine.ReconcileMeasureAsync(Request(Attacker), "human");
            Assert.Null(human.Error);
            Assert.Single(_tokenCalls.Distinct());
            Assert.Contains(Attacker, _sqlServers);
        }

        [Fact]
        public async Task A_saved_source_for_the_same_host_runs_green_whatever_the_spelling()
        {
            using var engine = await ModelAsync("Saved");
            var source = SqlSourceRegistry.Save(null, "Contoso", SavedHost, "Db", "azcli", null);
            await engine.SaveTestDefinitionAsync(Def(Request(null, source.Id)), "human");
            await engine.SaveTestDefinitionAsync(Def(Request("TCP:" + SavedHost.ToUpperInvariant() + ",1433")), "human");

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });

            Assert.Equal(2, Ran(run).Length);
            Assert.All(Ran(run), o => Assert.True(o.Verdict == Verdict.Pass, o.Message));
            Assert.All(_sqlServers, s => Assert.Contains(SavedHost, s, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task The_open_models_own_sql_endpoint_is_trusted_and_the_card_can_say_where_the_check_runs()
        {
            using var engine = await ModelAsync("Endpoint");
            var source = SqlSourceRegistry.Save(null, "Contoso", SavedHost, "Warehouse", "azcli", null);
            await engine.SetModelSqlEndpointAsync(source.Id, null, "human");
            await engine.SaveTestDefinitionAsync(Def(Request(null, null, null)), "human");

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });

            var outcome = Assert.Single(Ran(run));
            Assert.True(outcome.Verdict == Verdict.Pass, outcome.Message);
            Assert.Equal(SavedHost, outcome.Server);
            Assert.Equal("Warehouse", outcome.Database);
            Assert.Null(outcome.UnsavedServer);
        }

        [Fact]
        public async Task A_refused_check_still_says_which_server_it_names()
        {
            using var engine = await ModelAsync("Says");
            var tried = await engine.TryTestAsync(Def(Request(Attacker)), "human");
            Assert.Equal(Attacker, tried.Server);
            Assert.Equal("Db", tried.Database);
        }

        [Fact]
        public async Task The_mapping_test_connection_refuses_an_assistant_with_an_unsaved_server()
        {
            using var engine = await ModelAsync("Mapping");
            var review = await engine.ReviewReconcileMappingAsync(new ReconcileMappingRequest
            {
                MeasureRef = "Total Rows", Server = Attacker, Database = "Db", TestConnection = true,
            }, "agent");
            Assert.Equal(ExpectedWords(Attacker), review.TestError);
            Assert.Empty(_tokenCalls);
        }

        // ---- item 4: a check can only read ---------------------------------------------------------------------

        private const string ReadOnlyWords = "This check's SQL changes data, so Semanticus will not run it. A check can only read.";

        [Theory]
        [InlineData("INSERT INTO t VALUES (1)")]
        [InlineData("UPDATE t SET a = 1")]
        [InlineData("DELETE FROM t")]
        [InlineData("MERGE t USING s ON 1=1 WHEN MATCHED THEN DELETE;")]
        [InlineData("DROP TABLE t")]
        [InlineData("ALTER TABLE t ADD c int")]
        [InlineData("CREATE TABLE t (a int)")]
        [InlineData("TRUNCATE TABLE t")]
        [InlineData("EXEC sp_who")]
        [InlineData("EXECUTE sp_who")]
        [InlineData("GRANT SELECT ON t TO u")]
        [InlineData("REVOKE SELECT ON t FROM u")]
        [InlineData("DENY SELECT ON t TO u")]
        [InlineData("SELECT 1; DROP TABLE t")]
        [InlineData("SELECT 1;\nSELECT 2")]
        [InlineData("SELECT a INTO #copy FROM t")]
        [InlineData("WITH x AS (SELECT 1 AS a) DELETE FROM x")]
        [InlineData("select 1 /* ok */ ; update t set a = 1")]
        [InlineData("-- a read\nupdate t set a = 1")]
        [InlineData("SELECT 1 /* /* nested */ */ ; delete from t")]
        [InlineData("SELECT * FROM t WHERE a = 'x''y' ; drop table t")]
        [InlineData("   ")]
        public void The_guard_refuses_anything_that_is_not_one_read(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) { Assert.Null(SqlReadOnlyGuard.Refusal(sql)); return; }   // an empty box has its own message
            Assert.Equal(ReadOnlyWords, SqlReadOnlyGuard.Refusal(sql));
        }

        [Theory]
        [InlineData("SELECT Region, SUM(Amount) AS v FROM Sales GROUP BY Region")]
        [InlineData("WITH m AS (SELECT Region, SUM(Amount) AS v FROM Sales GROUP BY Region) SELECT Region, v FROM m")]
        [InlineData("SELECT SUM(Revenue) - SUM(Cost) AS margin FROM Sales;")]
        [InlineData("SELECT UpdateDate, [Delete Flag], \"Drop Reason\", CreatedBy FROM t WHERE [Delete Flag] = 0")]
        [InlineData("SELECT * FROM dbo.Update_Log WHERE Reason = 'please delete; drop it' -- DELETE later")]
        [InlineData("select a /* insert */ from t")]
        [InlineData("(SELECT 1) UNION ALL (SELECT 2)")]
        [InlineData("SELECT [a]]; DROP]] FROM t")]
        [InlineData("SELECT TOP 5 a FROM t ORDER BY a;  ")]
        public void The_guard_lets_a_single_read_through_including_names_that_look_like_words(string sql)
        {
            Assert.Null(SqlReadOnlyGuard.Refusal(sql));
        }

        [Fact]
        public async Task A_check_whose_sql_changes_data_runs_nothing_even_on_a_saved_source()
        {
            using var engine = await ModelAsync("WriteSql");
            var source = SqlSourceRegistry.Save(null, "Contoso", SavedHost, "Db", "azcli", null);
            var write = Request(null, source.Id);
            write.SqlGrandTotal = "SELECT total; DELETE FROM Sales";

            var tried = await engine.TryTestAsync(Def(write), "human");
            Assert.Equal(ReadOnlyWords, tried.Message);
            Assert.Equal(Verdict.NotVerifiable, tried.Verdict);

            var update = Request(SavedHost);
            update.Sql = "UPDATE Sales SET a = 1";
            var direct = await engine.ReconcileMeasureAsync(update, "agent");
            Assert.Equal(ReadOnlyWords, direct.Error);
            Assert.Empty(_tokenCalls);
            Assert.Empty(_sqlServers);
        }

        [Fact]
        public async Task The_northstar_margin_and_brand_by_year_checks_still_run()
        {
            using var engine = await ModelAsync("Northstar");
            var source = SqlSourceRegistry.Save(null, "Northstar", SavedHost, "Db", "azcli", null);
            var margin = Request(null, source.Id);
            margin.GroupBy = null;
            margin.Sql = "WITH t AS (SELECT SUM(Revenue) - SUM(Cost) AS margin FROM dbo.Sales) SELECT margin FROM t";
            margin.SqlGrandTotal = null;
            var brand = Request(null, source.Id);
            brand.Sql = "SELECT Brand AS region, SUM(Amount) AS value FROM dbo.Sales s GROUP BY Brand, YEAR(s.UpdateDate)";
            brand.SqlGrandTotal = "SELECT SUM([Delete Flag]) AS total FROM dbo.Sales";
            var marginTry = await engine.TryTestAsync(Def(margin), "human");
            var brandTry = await engine.TryTestAsync(Def(brand), "human");

            Assert.NotEqual(ReadOnlyWords, marginTry.Message);
            Assert.NotEqual(ReadOnlyWords, brandTry.Message);
            Assert.Null(marginTry.UnsavedServer);
            Assert.True(brandTry.Verdict == Verdict.Pass, brandTry.Message);
            Assert.Contains(_sqlServers, s => s == SavedHost);
        }
    }
}
