using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Testing a saved SQL source. The point Astra's UAT made is the one pinned hardest here: the sign-in reads the
    /// SOURCE's own mode and tenant, never the helper's azcli default. Driven through a fake connection so no live
    /// SQL, no tenant and no browser prompt are needed to prove the shape.
    /// </summary>
    [Collection("restore-root")]
    public sealed class SqlSourceProbeTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public SqlSourceProbeTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-sqlprobe-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            SqlSourceProbe.TokenForTests = null;
            SqlSourceProbe.QueryForTests = null;
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static SqlSourceRecord Save(string authMode = "devicecode", string tenantId = "tenant-7")
            => SqlSourceRegistry.Save(null, "Contoso warehouse", "contoso-sql.database.windows.net", "Warehouse", authMode, tenantId);

        private static ResultSet OneValue() => new ResultSet
        {
            Columns = new[] { new ColumnDef { Name = "semanticus_connection_test", Type = "Int64" } },
            Rows = new[] { new object[] { 1L } },
            RowCount = 1,
        };

        [Fact]
        public async Task A_working_source_returns_ok_with_a_plain_note_and_records_the_test()
        {
            var rec = Save();
            SqlSourceProbe.TokenForTests = (mode, tenant, ct) => Task.FromResult("fake-token");
            SqlSourceProbe.QueryForTests = (source, token, ct) => Task.FromResult(OneValue());

            var result = await SqlSourceProbe.TestAsync(rec, false, CancellationToken.None);

            Assert.True(result.Ok);
            Assert.Equal(rec.Id, result.Id);
            Assert.Equal("Contoso warehouse", result.Name);
            Assert.Equal("Connected to Warehouse on contoso-sql.database.windows.net.", result.Note);
            Assert.DoesNotContain("—", result.Note);
            Assert.False(string.IsNullOrWhiteSpace(result.TestedUtc));

            var stored = SqlSourceRegistry.Find(rec.Id);
            Assert.True(stored.LastTestOk);
            Assert.Equal(result.TestedUtc, stored.LastTestedUtc);
        }

        [Fact]
        public async Task The_sign_in_uses_the_sources_own_mode_and_tenant_not_the_azcli_default()
        {
            var rec = Save(authMode: "devicecode", tenantId: "tenant-7");
            var seen = new List<string>();
            SqlSourceProbe.TokenForTests = (mode, tenant, ct) => { seen.Add(mode + "|" + tenant); return Task.FromResult("fake-token"); };
            SqlSourceProbe.QueryForTests = (source, token, ct) => Task.FromResult(OneValue());

            await SqlSourceProbe.TestAsync(rec, false, CancellationToken.None);

            Assert.Equal(new[] { "devicecode|tenant-7" }, seen);
            Assert.DoesNotContain("azcli", seen[0]);
        }

        [Fact]
        public async Task A_sign_in_failure_says_so_in_plain_words_and_is_recorded_as_a_failure()
        {
            var rec = Save();
            SqlSourceProbe.TokenForTests = (mode, tenant, ct) => throw new InvalidOperationException("AADSTS50076: the account needs another factor.");
            SqlSourceProbe.QueryForTests = (source, token, ct) => throw new Xunit.Sdk.XunitException("the query must not be reached when the sign-in failed");

            var result = await SqlSourceProbe.TestAsync(rec, false, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.StartsWith("Semanticus could not sign in to this source.", result.Note);
            Assert.Contains("another factor", result.Note);
            Assert.False(SqlSourceRegistry.Find(rec.Id).LastTestOk);
        }

        [Fact]
        public async Task A_source_that_will_not_answer_says_so_and_never_reads_as_ok()
        {
            var rec = Save();
            SqlSourceProbe.TokenForTests = (mode, tenant, ct) => Task.FromResult("fake-token");
            SqlSourceProbe.QueryForTests = (source, token, ct) =>
                Task.FromResult(new ResultSet { Error = "Cannot open database \"Warehouse\" requested by the login." });

            var result = await SqlSourceProbe.TestAsync(rec, false, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.StartsWith("Semanticus signed in, but the source did not answer.", result.Note);
            Assert.Contains("Cannot open database", result.Note);
            Assert.False(SqlSourceRegistry.Find(rec.Id).LastTestOk);
        }

        [Fact]
        public async Task An_unexpected_answer_shape_is_not_a_pass()
        {
            var rec = Save();
            SqlSourceProbe.TokenForTests = (mode, tenant, ct) => Task.FromResult("fake-token");
            SqlSourceProbe.QueryForTests = (source, token, ct) => Task.FromResult(new ResultSet());

            var result = await SqlSourceProbe.TestAsync(rec, false, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Contains("not with the single value the test asked for", result.Note);
        }

        [Fact]
        public async Task A_credential_in_a_provider_message_never_reaches_the_note()
        {
            var rec = Save();
            SqlSourceProbe.TokenForTests = (mode, tenant, ct) => Task.FromResult("fake-token");
            SqlSourceProbe.QueryForTests = (source, token, ct) =>
                Task.FromResult(new ResultSet { Error = "Login failed for 'Server=x;Password=hunter2;'" });

            var result = await SqlSourceProbe.TestAsync(rec, false, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.DoesNotContain("hunter2", result.Note);
            Assert.Contains("Password=***", result.Note);
        }
    }
}
