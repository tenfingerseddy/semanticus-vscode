using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Facts;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// A shared .bim or repo can name any host in its partitions. Four places read that host and used to mint a sign-in token for it:
    /// Update Schema, the Start plan's schema read, the source sample and the spec read from a Fabric warehouse. A token now goes only to
    /// a saved host, a Microsoft SQL host, or one the person said yes to when asked once in plain words.
    /// </summary>
    [Collection("restore-root")]
    public sealed class SqlTokenSitesTests : IDisposable
    {
        private const string Attacker = "attacker.example.net";
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly List<string> _tokens = new List<string>();
        private readonly List<string> _asked = new List<string>();

        public SqlTokenSitesTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-tokensites-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
            SqlHostTrust.ResetSessionForTest();
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot; AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            SqlHostTrust.ResetSessionForTest();
            try { Directory.Delete(_root, true); } catch { }
        }

        private LocalEngine NewEngine(bool? answer)
        {
            var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro, Path.Combine(_root, "ws"));
            e.SqlTokenFetchForTests = (mode, tenant, origin) => { lock (_tokens) _tokens.Add(origin); return "token"; };
            e.SqlHostAsker = host => { lock (_asked) _asked.Add(host); return Task.FromResult(answer); };
            return e;
        }

        /// <summary>A model whose one table reads from the given server in its partition, the way a shared .bim does.</summary>
        private static async Task<string> TableOnHostAsync(LocalEngine e, string host)
        {
            await e.CreateModelAsync("Shared", 1604);
            var tableRef = await e.CreateImportTableAsync("Sales",
                "let Source = Sql.Database(\"" + host + "\",\"db\"), t = Source{[Schema=\"dbo\",Item=\"Sales\"]}[Data] in t", "human");
            await e.CreateColumnAsync(tableRef, "Amount", "Decimal", null, "human");
            return tableRef;
        }

        // ---- the suffix rule ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("contoso.database.windows.net")]
        [InlineData("Contoso.Database.Windows.NET.")]
        [InlineData("tcp:contoso.database.windows.net,1433")]
        [InlineData("x.datawarehouse.fabric.microsoft.com")]
        [InlineData("x.datawarehouse.pbidedicated.windows.net")]
        [InlineData("x-ondemand.sql.azuresynapse.net")]
        [InlineData("x.pbidedicated.windows.net")]
        [InlineData("api.fabric.microsoft.com")]
        [InlineData("contoso.database.usgovcloudapi.net")]
        [InlineData("contoso.database.chinacloudapi.cn")]
        public void A_microsoft_sql_host_is_recognised_whatever_its_case_dot_or_port(string server) =>
            Assert.True(SqlHostTrust.IsMicrosoftHost(server), server);

        [Theory]
        [InlineData("contoso.database.windows.net.attacker.net")]
        [InlineData("attacker.example.net")]
        [InlineData("database.windows.net")]
        [InlineData("evildatabase.windows.net")]
        [InlineData("contoso.database.windows.net@attacker.net")]
        [InlineData("attacker.net/contoso.database.windows.net")]
        [InlineData("")]
        public void A_look_alike_or_foreign_host_is_not_a_microsoft_host(string server) =>
            Assert.False(SqlHostTrust.IsMicrosoftHost(server), server);

        // ---- the question ------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_host_nobody_trusts_asks_once_in_plain_words_and_yes_saves_it()
        {
            var e = NewEngine(true);
            Assert.Equal("This model reads from attacker.example.net. Send your sign-in there?", LocalEngine.SqlHostQuestion(Attacker));

            Assert.Null(await e.SqlHostRefusalAsync(Attacker, "db", "azcli", null, "agent", typedByPerson: false));
            Assert.Null(await e.SqlHostRefusalAsync(Attacker, "db", "azcli", null, "agent", typedByPerson: false));

            Assert.Equal(new[] { Attacker }, _asked);   // the second time needs no question
            Assert.True(SqlHostTrust.IsSaved(Attacker));   // yes went into the same store a saved source lives in
        }

        [Fact]
        public async Task No_stops_with_a_plain_line_and_is_not_asked_again()
        {
            var e = NewEngine(false);
            var first = await e.SqlHostRefusalAsync(Attacker, "db", "azcli", null, "human", typedByPerson: false);
            var second = await e.SqlHostRefusalAsync(Attacker, "db", "azcli", null, "human", typedByPerson: false);

            Assert.Contains("nothing was sent", first);
            Assert.Equal(first, second);
            Assert.Single(_asked);
            Assert.False(SqlHostTrust.IsSaved(Attacker));
        }

        [Fact]
        public async Task With_nobody_to_ask_nothing_is_sent_and_the_line_says_why()
        {
            var e = NewEngine(null);
            var words = await e.SqlHostRefusalAsync(Attacker, "db", "azcli", null, "agent", typedByPerson: false);
            Assert.Contains("nobody could be asked", words);
        }

        [Theory]
        [InlineData("contoso.database.windows.net")]
        [InlineData("x.datawarehouse.fabric.microsoft.com")]
        public async Task A_microsoft_host_runs_with_no_question(string host)
        {
            var e = NewEngine(false);
            Assert.Null(await e.SqlHostRefusalAsync(host, "db", "azcli", null, "agent", typedByPerson: false));
            Assert.Empty(_asked);
        }

        [Fact]
        public async Task A_host_the_person_typed_runs_with_no_question_but_an_assistants_does_not()
        {
            var e = NewEngine(false);
            Assert.Null(await e.SqlHostRefusalAsync("typed.example.net", "db", "azcli", null, "human", typedByPerson: true));
            Assert.Empty(_asked);
            Assert.NotNull(await e.SqlHostRefusalAsync("assistant.example.net", "db", "azcli", null, "agent", typedByPerson: true));
            Assert.Single(_asked);
        }

        // ---- the four sites, against a model whose partition names the attacker -------------------------------------

        [Fact]
        public async Task Update_Schema_mints_no_token_for_a_host_the_model_names()
        {
            var e = NewEngine(false);
            var tableRef = await TableOnHostAsync(e, Attacker);

            var src = await e.GetSourceSchemaAsync(tableRef, "azcli", null, "agent");

            Assert.False(src.Reachable);
            Assert.Contains("nothing was sent", src.Error);
            Assert.Empty(_tokens);
            Assert.Equal(new[] { Attacker }, _asked);
        }

        [Fact]
        public async Task The_Start_plan_schema_read_mints_no_token_for_an_assistants_host()
        {
            var e = NewEngine(false);
            AgentPolicyStore.SetPreset("open", "human");
            var folder = Path.Combine(_root, "start");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.StartModelFromSqlAsync(
                new Semanticus.Engine.StartPlan.StartModelRequest { Name = "Gold", Folder = folder, Server = Attacker, Database = "db" }, "agent"));

            Assert.Contains("nothing was sent", ex.Message);
            Assert.Empty(_tokens);
        }

        [Fact]
        public async Task The_source_sample_mints_no_token_for_a_host_that_is_not_trusted()
        {
            var e = NewEngine(false);
            AgentPolicyStore.SetPreset("open", "human");
            var target = new SourceSampleTarget { Server = Attacker, Database = "db", AuthMode = "azcli" };
            var tables = new[] { new SourceSampleTable { Schema = "dbo", Name = "Sales", Columns = new[] { "Amount" } } };

            var r = await e.ReadSourceSampleAsync(target, tables, "human", CancellationToken.None);

            Assert.NotNull(r.Refused);
            Assert.Empty(_tokens);
        }

        [Fact]
        public async Task The_spec_read_from_a_warehouse_mints_no_token_for_an_assistants_host()
        {
            var e = NewEngine(false);
            await e.CreateModelAsync("Spec", 1604);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => e.AutogenerateSpecFromFabricAsync(Attacker, "db", "azcli", "import", "agent"));

            Assert.Contains("nothing was sent", ex.Message);
            Assert.Empty(_tokens);
        }

        [Theory]
        [InlineData("engine")]
        [InlineData("workflow")]
        [InlineData("agent")]
        public async Task A_non_human_origin_is_asked_about_an_unsaved_host_and_sends_nothing_on_no(string origin)
        {
            var e = NewEngine(false);
            var tableRef = await TableOnHostAsync(e, Attacker);

            var src = await e.GetSourceSchemaAsync(tableRef, "azcli", null, origin);

            Assert.False(src.Reachable);
            Assert.Empty(_tokens);
            Assert.Equal(new[] { Attacker }, _asked);
        }

        // ---- the read-only guard (the coordinator's finding 2) ----------------------------------------------------

        [Theory]
        [InlineData("SELECT 1UPDATE dbo.Sales SET Amount = 0")]
        [InlineData("SELECT 1DELETE FROM dbo.Sales")]
        [InlineData("SELECT 12drop TABLE dbo.Sales")]
        public void A_keyword_glued_to_a_number_is_still_a_keyword(string sql) =>
            Assert.NotNull(SqlReadOnlyGuard.Refusal(sql));

        [Theory]
        [InlineData("SELECT 1 UPDATETEXT dbo.T.Notes @p 0 0 'x'")]
        [InlineData("SELECT 1 WRITETEXT dbo.T.Notes @p 'x'")]
        [InlineData("SELECT 1 BULK INSERT dbo.T FROM 'x'")]
        [InlineData("SELECT 1 DBCC CHECKDB")]
        [InlineData("SELECT 1 RECEIVE TOP(1) * FROM dbo.Queue")]
        [InlineData("SELECT 1 KILL 55")]
        [InlineData("SELECT 1 BACKUP DATABASE Db TO DISK = 'x'")]
        [InlineData("SELECT 1 RESTORE DATABASE Db FROM DISK = 'x'")]
        [InlineData("SELECT * FROM OPENQUERY(srv, 'select 1')")]
        [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'x', 'select 1')")]
        [InlineData("SELECT * FROM OPENDATASOURCE('SQLNCLI', 'x').Db.dbo.T")]
        [InlineData("SELECT 1 SHUTDOWN")]
        [InlineData("SELECT 1 RECONFIGURE")]
        [InlineData("SELECT 1 SEND ON CONVERSATION @h MESSAGE TYPE m")]
        public void A_statement_with_a_banned_first_word_is_refused_even_with_no_semicolon(string sql) =>
            Assert.NotNull(SqlReadOnlyGuard.Refusal(sql));

        [Fact]
        public void A_line_comment_ends_at_a_bare_carriage_return()
        {
            var cr = ((char)13).ToString();
            Assert.NotNull(SqlReadOnlyGuard.Refusal("SELECT 1 -- note" + cr + "DELETE FROM dbo.Sales"));
            Assert.Null(SqlReadOnlyGuard.Refusal("SELECT 1 -- DELETE in a comment\nFROM dbo.Sales"));
        }

        [Theory]
        [InlineData("SELECT 1FROM dbo.Sales")]
        [InlineData("SELECT 2020Q1, [Delete Flag], UpdateDate FROM dbo.Sales")]
        public void Real_reads_with_numbers_next_to_names_still_run(string sql) =>
            Assert.Null(SqlReadOnlyGuard.Refusal(sql));

        // ---- the check path (item 3): a Microsoft host needs no saved source ----------------------------------------

        [Fact]
        public void A_check_accepts_a_saved_or_microsoft_host_and_still_refuses_the_look_alike()
        {
            Assert.True(SqlHostTrust.IsSavedOrMicrosoft("contoso.database.windows.net"));
            Assert.False(SqlHostTrust.IsSavedOrMicrosoft("contoso.database.windows.net.attacker.net"));
            Assert.False(SqlHostTrust.IsSavedOrMicrosoft(Attacker));
            // Any Azure SQL server name is Microsoft's: its gateway, not the name's owner, receives the token.
            Assert.True(SqlHostTrust.IsSavedOrMicrosoft("xcontoso-sql.database.windows.net"));
        }
    }
}
