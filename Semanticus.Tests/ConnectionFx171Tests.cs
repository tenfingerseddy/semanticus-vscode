using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// fx171, from the connections tester round cx1: a wrong-tenant sign-in said only "ClientSecretCredential
    /// authentication failed:" (gap 7), a failed History row did not name its tenant or model (gap 8), and Forget left the
    /// connection's History rows behind (gap 9).
    /// </summary>
    [Collection("restore-root")]
    public sealed class ConnectionFx171Tests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public ConnectionFx171Tests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-fx171-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ConnectionRegistry.RootOverride = _root;
            EntraToken.PersistDirOverride = Path.Combine(_root, "auth");
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            EntraToken.PersistDirOverride = null;
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class EmptyFailureCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => throw new AuthenticationFailedException("ClientSecretCredential authentication failed: ");
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) => throw new AuthenticationFailedException("ClientSecretCredential authentication failed: ");
        }

        private sealed class WrappedEmptyFailureCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => throw new NotSupportedException();
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) =>
                throw new AuthenticationFailedException("ClientSecretCredential authentication failed: ", new Exception(""));
        }

        // The wrong-tenant failure the Sol confirm run saw (cx3 Q2): the first line ends at the colon and the reason is on
        // the next line, so the screen (which shows the first line) read "ClientSecretCredential authentication failed:".
        private sealed class FirstLineEmptyFailureCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext c, CancellationToken ct) => throw new NotSupportedException();
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext c, CancellationToken ct) =>
                throw new AuthenticationFailedException("ClientSecretCredential authentication failed: \nAADSTS90002: Tenant '11111111-aaaa-4aaa-8aaa-111111111111' not found.",
                    new Exception("AADSTS90002: Tenant '11111111-aaaa-4aaa-8aaa-111111111111' not found."));
        }

        [Fact]
        public async Task Cx3_service_identity_failure_whose_first_line_ends_at_the_colon_has_plain_words_on_that_line()
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => EntraToken.FabricTokenFromAsync(new FirstLineEmptyFailureCredential(), CancellationToken.None));
            var firstLine = ex.Message.Split('\n')[0];
            Assert.DoesNotContain("ClientSecretCredential", firstLine);
            Assert.Contains("service identity could not sign in", firstLine);
            Assert.Contains("tenant", firstLine);
        }

        [Fact]
        public async Task Fx174_service_identity_workspace_failure_with_empty_inner_detail_has_plain_words()
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => EntraToken.FabricTokenFromAsync(new WrappedEmptyFailureCredential(), CancellationToken.None));
            Assert.DoesNotContain("ClientSecretCredential", ex.Message);
            Assert.Contains("service identity could not sign in", ex.Message);
            Assert.Contains("tenant", ex.Message);
        }

        [Fact]
        public async Task A_sign_in_failure_with_no_detail_says_in_plain_words_what_failed_and_what_to_do()
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => EntraToken.GetTokenAsync(new EmptyFailureCredential(), CancellationToken.None));
            Assert.DoesNotContain("ClientSecretCredential", ex.Message);
            Assert.DoesNotContain("failed:", ex.Message);
            Assert.Contains("could not sign in", ex.Message);
            Assert.Contains("tenant", ex.Message);
            Assert.Contains("try again", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("—", ex.Message);
        }

        [Fact]
        public async Task A_failed_open_row_names_the_model_and_the_tenant_of_the_saved_connection()
        {
            var r = ConnectionRegistry.Remember("xmla", "powerbi://x/fx171open", "Contoso Sales", "Contoso Sales", "11111111-aaaa-4aaa-8aaa-111111111111", "interactive", "alice@contoso.com");
            using var engine = new LocalEngine(new SessionManager());
            engine.OpenLiveFailureProbeForTests = () => throw new InvalidOperationException("network unreachable");
            await Assert.ThrowsAnyAsync<Exception>(() => engine.OpenLiveAsync("powerbi://x/fx171open", "Contoso Sales", "interactive", null, null));
            var ev = ConnectionHistory.List(r.Id).Single();
            Assert.False(ev.Ok);
            Assert.Equal("Contoso Sales", ev.ModelName);
            Assert.Equal("11111111-aaaa-4aaa-8aaa-111111111111", ev.TenantId);
        }

        [Fact]
        public async Task Forget_removes_that_connections_history_rows_and_keeps_the_others()
        {
            var a = ConnectionRegistry.Remember("xmla", "powerbi://x/fx171a", "A", "A", "contoso.com", "interactive", "alice@contoso.com");
            var b = ConnectionRegistry.Remember("xmla", "powerbi://x/fx171b", "B", "B", "contoso.com", "interactive", "alice@contoso.com");
            ConnectionHistory.Append(new ConnectionHistoryEvent { Id = a.Id, Kind = "open", Endpoint = a.Endpoint, Ok = true });
            ConnectionHistory.Append(new ConnectionHistoryEvent { Id = a.Id, Kind = "signin", Endpoint = a.Endpoint, Ok = false });
            ConnectionHistory.Append(new ConnectionHistoryEvent { Id = b.Id, Kind = "open", Endpoint = b.Endpoint, Ok = true });
            using var engine = new LocalEngine(new SessionManager());
            Assert.True(await engine.ForgetConnectionAsync(a.Id, "human"));
            Assert.Empty(ConnectionHistory.List(a.Id));
            Assert.Single(ConnectionHistory.List(b.Id));
            Assert.DoesNotContain(ConnectionHistory.List(), e => e.Id == a.Id);
            Assert.DoesNotContain(a.Id, File.ReadAllText(Path.Combine(_root, "connection-history.json")));
        }
    }
}
