using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    // H14: the assistant needs the SQL endpoint host of a lakehouse or warehouse to start a model from SQL. The
    // listing uses the person's own saved sign-in and never falls back to az. Offline through the FabricRest and
    // EntraToken seams: no tenant, no token, no network.
    [Collection("deploy-serial")]
    public sealed class WorkspaceSqlEndpointListTests : IDisposable
    {
        public void Dispose()
        {
            FabricRest.TestClientFactory = null;
            EntraToken.FabricTokenForTests = null;
            EntraToken.CredentialBuiltForTests = null;
        }

        [Fact]
        public async Task A_lakehouse_and_a_warehouse_come_back_with_their_hosts_and_names()
        {
            var asked = new List<string>();
            EntraToken.FabricTokenForTests = () => "test-bearer";
            FabricRest.TestClientFactory = () => Client(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                asked.Add(path);
                if (path.EndsWith("/lakehouses"))
                    return Json("{\"value\":[{\"id\":\"lh1\",\"displayName\":\"Gold\",\"properties\":{\"sqlEndpointProperties\":{\"id\":\"se1\",\"connectionString\":\"abc.datawarehouse.fabric.microsoft.com\",\"provisioningStatus\":\"Success\"}}},"
                        + "{\"id\":\"lh2\",\"displayName\":\"Bronze\"}]}");
                if (path.EndsWith("/warehouses"))
                    return Json("{\"value\":[{\"id\":\"wh1\",\"displayName\":\"Sales DW\",\"properties\":{\"connectionString\":\"xyz.datawarehouse.fabric.microsoft.com\"}}]}");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free);

            var list = await engine.ListWorkspaceSqlEndpointsAsync("ws1", "interactive", null, "agent");

            Assert.False(list.SignInNeeded);
            var gold = list.Endpoints.Single(e => e.Database == "Gold");
            Assert.Equal(("Lakehouse", "abc.datawarehouse.fabric.microsoft.com", "se1", "Success"), (gold.Kind, gold.Server, gold.ItemId, gold.Status));
            var dw = list.Endpoints.Single(e => e.Database == "Sales DW");
            Assert.Equal(("Warehouse", "xyz.datawarehouse.fabric.microsoft.com"), (dw.Kind, dw.Server));
            Assert.Null(list.Endpoints.Single(e => e.Database == "Bronze").Server);   // no properties: no invented host
            Assert.Equal(new[] { "/v1/workspaces/ws1/lakehouses", "/v1/workspaces/ws1/warehouses" }, asked);
        }

        [Fact]
        public async Task Every_workspace_is_read_when_none_is_named_and_one_failure_stays_on_its_row()
        {
            EntraToken.FabricTokenForTests = () => "test-bearer";
            FabricRest.TestClientFactory = () => Client(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path == "/v1/workspaces") return Json("{\"value\":[{\"id\":\"a\",\"displayName\":\"Contoso A\"},{\"id\":\"b\",\"displayName\":\"Contoso B\"}]}");
                if (path.StartsWith("/v1/workspaces/b/")) return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"no Bearer secret-x\"}") };
                if (path.EndsWith("/warehouses")) return Json("{\"value\":[{\"id\":\"w\",\"displayName\":\"DW\",\"properties\":{\"connectionString\":\"h.fabric\"}}]}");
                return Json("{\"value\":[]}");
            });
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free);

            var list = await engine.ListWorkspaceSqlEndpointsAsync(null, "interactive", null, "agent");

            var ok = list.Endpoints.Single(e => e.WorkspaceId == "a");
            Assert.Equal(("Contoso A", "DW", "h.fabric"), (ok.WorkspaceName, ok.Database, ok.Server));
            var bad = list.Endpoints.Single(e => e.WorkspaceId == "b");
            Assert.NotNull(bad.Error);
            Assert.DoesNotContain("secret-x", bad.Error);
        }

        [Fact]
        public async Task With_no_sign_in_it_says_so_makes_no_call_and_never_builds_an_az_credential()
        {
            var built = new List<string>();
            var calls = 0;
            EntraToken.CredentialBuiltForTests = m => { lock (built) built.Add(m); };
            FabricRest.TestClientFactory = () => Client(_ => { calls++; return Json("{\"value\":[]}"); });
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free);
            Assert.DoesNotContain(EntraToken.ListProfiles(), p => p.SignedIn);   // the scratch home has no saved sign-in

            var list = await engine.ListWorkspaceSqlEndpointsAsync(null, null, null, "agent");

            Assert.True(list.SignInNeeded);
            Assert.Equal(LocalEngine.SqlEndpointSignInNeeded, list.Message);
            Assert.Contains("press Sign in", list.Message);
            Assert.Empty(list.Endpoints);
            Assert.Equal(0, calls);
            Assert.DoesNotContain("azcli", built);
            Assert.Equal((null, null), engine.ResolveSqlListSignIn("", null));
        }

        [Fact]
        public void Both_doors_and_the_free_tier_carry_it()
        {
            Assert.NotNull(typeof(McpTools).GetMethod("ListWorkspaceSqlEndpoints"));
            Assert.NotNull(typeof(EngineRpcTarget).GetMethod("listWorkspaceSqlEndpoints"));
            Assert.True(OpTaxonomy.TryGet("list_workspace_sql_endpoints", out var question, out _));
            Assert.Equal("Open a model and connect", question);
        }

        private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send)
            => new(new DelegateHandler(send), disposeHandler: true) { BaseAddress = new Uri(FabricRest.BaseUrl) };

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed class DelegateHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _send;
            public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) => _send = send;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(_send(request));
        }
    }
}
