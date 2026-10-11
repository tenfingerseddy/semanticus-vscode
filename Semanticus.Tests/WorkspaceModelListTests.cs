using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    // Connections (slice C, contract 19.1): "a workspace, then a model" needs the engine to list the models in one
    // workspace. list_workspace_models (MCP) and listWorkspaceModels (RPC) are that listing, one IEngine method.
    // Driven offline through the FabricRest and EntraToken seams: no tenant, no token, no network.
    [Collection("deploy-serial")]
    public sealed class WorkspaceModelListTests : IDisposable
    {
        public void Dispose()
        {
            FabricRest.TestClientFactory = null;
            EntraToken.FabricTokenForTests = null;
        }

        [Fact]
        public async Task Lists_every_page_of_a_workspaces_models_sorted_by_name()
        {
            var asked = new List<string>();
            EntraToken.FabricTokenForTests = () => "test-bearer";
            FabricRest.TestClientFactory = () => Client(req =>
            {
                asked.Add(req.RequestUri!.PathAndQuery);
                Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
                return req.RequestUri.Query.Contains("continuationToken")
                    ? Json("{\"value\":[{\"id\":\"m2\",\"displayName\":\"Contoso Budget\",\"workspaceId\":\"ws 1\"}]}")
                    : Json("{\"value\":[{\"id\":\"m1\",\"displayName\":\"contoso Finance\",\"description\":\"Finance\",\"workspaceId\":\"ws 1\"}],"
                        + "\"continuationToken\":\"t2\",\"continuationUri\":\"https://api.fabric.microsoft.com/v1/workspaces/ws%201/semanticModels?continuationToken=t2\"}");
            });
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free);

            var models = await engine.ListWorkspaceModelsAsync("ws 1", "azcli", null);

            Assert.Equal(new[] { "Contoso Budget", "contoso Finance" }, Array.ConvertAll(models, m => m.DisplayName));
            Assert.Equal("m1", models[1].Id);
            Assert.Equal("Finance", models[1].Description);
            Assert.StartsWith("/v1/workspaces/ws%201/semanticModels", asked[0]);   // the id is escaped into the path
            Assert.Equal(2, asked.Count);                                         // the second page was followed
        }

        [Fact]
        public async Task A_workspace_with_no_models_is_an_empty_list_not_an_error()
        {
            EntraToken.FabricTokenForTests = () => "test-bearer";
            FabricRest.TestClientFactory = () => Client(_ => Json("{\"value\":[]}"));
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free);

            Assert.Empty(await engine.ListWorkspaceModelsAsync("ws-empty", "azcli", null));
        }

        [Fact]
        public async Task A_missing_workspace_id_names_the_op_that_finds_one()
        {
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free);
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => engine.ListWorkspaceModelsAsync(" ", "azcli", null));
            Assert.Contains("list_workspaces", ex.Message);
        }

        [Fact]
        public async Task A_refusal_from_Fabric_reaches_the_caller_without_the_bearer()
        {
            EntraToken.FabricTokenForTests = () => "secret-bearer-value";
            FabricRest.TestClientFactory = () => Client(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"errorCode\":\"InsufficientPrivileges\",\"message\":\"The caller does not have permission. Bearer secret-bearer-value\"}", Encoding.UTF8, "application/json"),
            });
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free);

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => engine.ListWorkspaceModelsAsync("ws", "azcli", null));
            Assert.DoesNotContain("secret-bearer-value", ex.Message);
        }

        [Fact]
        public void Both_doors_and_the_free_tier_carry_it()
        {
            // The agent door: an MCP tool by that name. The UI door: the RPC target method. Free on every tier,
            // like list_workspaces, because picking where to connect is not a Pro feature.
            Assert.NotNull(typeof(McpTools).GetMethod("ListWorkspaceModels"));
            Assert.NotNull(typeof(EngineRpcTarget).GetMethod("listWorkspaceModels"));
            Assert.True(OpTaxonomy.TryGet("list_workspace_models", out var question, out _));
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
