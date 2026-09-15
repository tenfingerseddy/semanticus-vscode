using System;
using System.IO;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The SQL source a person saves ONCE and then picks by name (Kane's Tests redesign, 2026-09-15). Astra's UAT
    /// found the opposite: two checks against the same warehouse needed the endpoint and the database typed twice,
    /// and a newly authored check carried no auth mode at all, so the token helper silently fell back to azcli.
    /// These tests pin the record, the rules that keep a credential out of it, and the plain-words refusals.
    /// </summary>
    [Collection("restore-root")]   // mutates the static registry root the gate family reads — serialize it
    public sealed class SqlSourceRegistryTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public SqlSourceRegistryTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-sqlsrc-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static SqlSourceRecord Save(string name = "Contoso warehouse", string server = "contoso-sql.database.windows.net",
            string database = "Warehouse", string authMode = "interactive", string tenantId = null, string id = null)
            => SqlSourceRegistry.Save(id, name, server, database, authMode, tenantId);

        [Fact]
        public void Save_then_list_then_delete_round_trips_one_named_source()
        {
            Assert.Empty(SqlSourceRegistry.List());

            var saved = Save();
            Assert.False(string.IsNullOrWhiteSpace(saved.Id));
            Assert.Equal("Contoso warehouse", saved.Name);
            Assert.Equal("contoso-sql.database.windows.net", saved.Server);
            Assert.Equal("Warehouse", saved.Database);
            Assert.Equal("interactive", saved.AuthMode);
            Assert.False(string.IsNullOrWhiteSpace(saved.CreatedUtc));
            // Never tested is its own state, distinct from "the last test failed".
            Assert.Null(saved.LastTestedUtc);
            Assert.Null(saved.LastTestOk);

            var listed = SqlSourceRegistry.List();
            Assert.Single(listed);
            Assert.Equal(saved.Id, listed[0].Id);
            Assert.Equal(saved.Id, SqlSourceRegistry.Find(saved.Id).Id);

            Assert.True(SqlSourceRegistry.Delete(saved.Id));
            Assert.Empty(SqlSourceRegistry.List());
            Assert.Null(SqlSourceRegistry.Find(saved.Id));
            Assert.False(SqlSourceRegistry.Delete(saved.Id));
        }

        [Fact]
        public void Saving_with_an_id_updates_that_record_and_keeps_its_id_and_created_date()
        {
            var first = Save();
            var updated = SqlSourceRegistry.Save(first.Id, "Contoso warehouse (UAT)", "uat-sql.database.windows.net", "WarehouseUat", "devicecode", "tenant-1");

            Assert.Equal(first.Id, updated.Id);
            Assert.Equal(first.CreatedUtc, updated.CreatedUtc);
            Assert.Equal("Contoso warehouse (UAT)", updated.Name);
            Assert.Equal("uat-sql.database.windows.net", updated.Server);
            Assert.Equal("WarehouseUat", updated.Database);
            Assert.Equal("devicecode", updated.AuthMode);
            Assert.Equal("tenant-1", updated.TenantId);
            Assert.Single(SqlSourceRegistry.List());
        }

        [Fact]
        public void A_second_source_cannot_take_a_name_already_in_use()
        {
            Save();
            var ex = Assert.Throws<ArgumentException>(() => Save(server: "other-sql.database.windows.net"));
            Assert.Contains("already a SQL source called", ex.Message);
            Assert.Single(SqlSourceRegistry.List());

            // Re-saving the SAME record under its own name is not a clash.
            var mine = SqlSourceRegistry.List()[0];
            var again = SqlSourceRegistry.Save(mine.Id, mine.Name, "moved-sql.database.windows.net", mine.Database, mine.AuthMode, null);
            Assert.Equal("moved-sql.database.windows.net", again.Server);
        }

        [Fact]
        public void A_source_needs_a_name_a_server_and_a_database()
        {
            Assert.Contains("name", Assert.Throws<ArgumentException>(() => Save(name: "  ")).Message);
            Assert.Contains("server", Assert.Throws<ArgumentException>(() => Save(server: "")).Message);
            Assert.Contains("database", Assert.Throws<ArgumentException>(() => Save(database: null)).Message);
        }

        [Fact]
        public void Only_the_four_engine_auth_modes_are_accepted_and_a_raw_token_is_refused()
        {
            foreach (var mode in new[] { "interactive", "devicecode", "azcli", "serviceprincipal" })
            {
                var rec = Save(name: "src-" + mode, authMode: mode.ToUpperInvariant());
                Assert.Equal(mode, rec.AuthMode);   // stored in canonical lower case
            }

            var token = Assert.Throws<ArgumentException>(() => Save(name: "raw", authMode: "token"));
            Assert.Contains("never holds a password or a token", token.Message);

            var nonsense = Assert.Throws<ArgumentException>(() => Save(name: "nonsense", authMode: "banana"));
            Assert.Contains("Sign in with", nonsense.Message);
        }

        [Fact]
        public void An_empty_auth_mode_is_refused_rather_than_silently_becoming_azcli()
        {
            // The exact defect Astra recorded: a check saved with no mode reached EntraToken, which defaults an
            // absent mode to azcli. A saved SOURCE must state its mode out loud.
            var ex = Assert.Throws<ArgumentException>(() => Save(authMode: null));
            Assert.Contains("Sign in with", ex.Message);
        }

        [Fact]
        public void A_connection_string_or_a_credential_is_refused_at_the_boundary()
        {
            var pasted = Assert.Throws<ArgumentException>(() => Save(
                server: "Server=contoso-sql.database.windows.net;Database=Warehouse;User Id=sa;Password=hunter2"));
            Assert.Contains("just the server address", pasted.Message);

            var inDb = Assert.Throws<ArgumentException>(() => Save(database: "Warehouse;Password=hunter2"));
            Assert.Contains("just the database name", inDb.Message);

            Assert.Empty(SqlSourceRegistry.List());
        }

        [Fact]
        public void The_saved_file_holds_no_password_and_no_token()
        {
            Save(tenantId: "tenant-1");
            var file = Path.Combine(_root, "sql-sources.json");
            Assert.True(File.Exists(file));
            var bytes = File.ReadAllText(file);

            Assert.Contains("Contoso warehouse", bytes);
            foreach (var forbidden in new[] { "password", "pwd", "secret", "accesstoken", "access_token" })
                Assert.DoesNotContain(forbidden, bytes, StringComparison.OrdinalIgnoreCase);

            // And the model records keep their own file: the two roles are never mixed into one list.
            Assert.False(File.Exists(Path.Combine(_root, "connections.json")));
        }

        [Fact]
        public void Recording_a_test_stamps_the_outcome_and_a_failure_is_never_rounded_up()
        {
            var rec = Save();

            var failed = SqlSourceRegistry.RecordTest(rec.Id, false);
            Assert.False(failed.LastTestOk);
            Assert.False(string.IsNullOrWhiteSpace(failed.LastTestedUtc));

            var passed = SqlSourceRegistry.RecordTest(rec.Id, true);
            Assert.True(passed.LastTestOk);
            Assert.Equal(passed.LastTestedUtc, SqlSourceRegistry.Find(rec.Id).LastTestedUtc);

            Assert.Null(SqlSourceRegistry.RecordTest("no-such-id", true));
        }

        [Fact]
        public void The_list_is_capped_so_the_file_cannot_grow_without_end()
        {
            for (var i = 0; i < SqlSourceRegistry.MaxRecords; i++) Save(name: "source " + i);
            var ex = Assert.Throws<InvalidOperationException>(() => Save(name: "one too many"));
            Assert.Contains("Remove one you no longer use", ex.Message);
            Assert.Equal(SqlSourceRegistry.MaxRecords, SqlSourceRegistry.List().Count);
        }

        [Fact]
        public void Newest_first_so_the_picker_reads_sensibly()
        {
            var a = Save(name: "alpha");
            var b = Save(name: "bravo");
            var ids = SqlSourceRegistry.List().Select(r => r.Id).ToArray();
            Assert.Equal(new[] { b.Id, a.Id }, ids);
        }
    }
}
