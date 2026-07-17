using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("deploy-serial")]
    public sealed class FabricContainmentTests : IDisposable
    {
        private sealed class ProEntitlement : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new() { Tier = "pro" };
        }

        public void Dispose()
        {
            FabricRest.TestClientFactory = null;
            EntraToken.FabricTokenForTests = null;
        }

        [Fact]
        public async Task Send_rejects_a_declared_body_over_the_limit_before_reading_it()
        {
            using var http = Client((_, _) =>
            {
                var content = new ByteArrayContent(Array.Empty<byte>());
                content.Headers.ContentLength = FabricRest.MaxResponseBodyBytes + 1L;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => FabricRest.SendAsync(http, HttpMethod.Get, "workspaces", CancellationToken.None));

            Assert.Contains("64 MiB", ex.Message);
            Assert.DoesNotContain("body", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Body_reader_stops_a_chunked_response_that_crosses_the_limit()
        {
            using var content = new StreamContent(new System.IO.MemoryStream(new byte[9]));
            content.Headers.ContentLength = null;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => FabricRest.ReadBodyAsync(content, maxBytes: 8, CancellationToken.None));

            Assert.Contains("8 bytes", ex.Message);
        }

        [Fact]
        public async Task Body_reader_wraps_malformed_UTF8_as_a_door_safe_failure()
        {
            using var content = new ByteArrayContent(new byte[] { 0xc3, 0x28 });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => FabricRest.ReadBodyAsync(content, maxBytes: 8, CancellationToken.None));

            Assert.Contains("not valid UTF-8", ex.Message);
            Assert.IsNotType<DecoderFallbackException>(ex);
        }

        [Fact]
        public async Task Pagination_fails_loud_when_the_page_backstop_is_reached()
        {
            var requests = 0;
            using var http = Client((_, _) =>
            {
                requests++;
                return Task.FromResult(Json("{\"value\":[{\"id\":\"a\"}],\"continuationToken\":\"again\"}"));
            });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => FabricRest.GetAllPagesAsync<FabricWorkspace>(http, "workspaces", CancellationToken.None, maxPages: 2));

            Assert.Equal(2, requests);
            Assert.Contains("2-page safety limit", ex.Message);
            Assert.Contains("results are incomplete", ex.Message);
        }

        [Fact]
        public async Task Pagination_caps_the_aggregate_bytes_across_pages()
        {
            const string first = "{\"value\":[{\"id\":\"a\"}],\"continuationToken\":\"next\"}";
            const string second = "{\"value\":[{\"id\":\"b\"}]}";
            var requests = 0;
            using var http = Client((_, _) => Task.FromResult(Json(requests++ == 0 ? first : second)));
            var firstBytes = Encoding.UTF8.GetByteCount(first);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => FabricRest.GetAllPagesAsync<FabricWorkspace>(http, "workspaces", CancellationToken.None,
                    maxResponseBytes: firstBytes + 1));

            Assert.Equal(2, requests);
            Assert.Contains("aggregate response limit", ex.Message);
            Assert.Contains("results are incomplete", ex.Message);
        }

        [Fact]
        public void Definition_decode_caps_the_aggregate_output()
        {
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"));
            var body = "{\"definition\":{\"parts\":[" +
                "{\"path\":\"one.json\",\"payload\":\"" + payload + "\"}," +
                "{\"path\":\"two.json\",\"payload\":\"" + payload + "\"}]}}";

            var ex = Assert.Throws<InvalidOperationException>(() => FabricRest.DecodeDefinitionParts(body, maxDecodedBytes: 3));
            Assert.Contains("decoded Fabric definition", ex.Message);
            Assert.Contains("3 bytes", ex.Message);
        }

        [Fact]
        public async Task Polling_honours_cancellation_while_waiting_for_the_next_poll()
        {
            var requests = 0;
            using var cts = new CancellationTokenSource();
            using var http = Client((_, _) =>
            {
                requests++;
                var response = Json("{\"status\":\"Running\"}");
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                cts.CancelAfter(TimeSpan.FromMilliseconds(50));
                return Task.FromResult(response);
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => FabricRest.PollOperationAsync(http, "op-cancel", cts.Token));

            Assert.Equal(1, requests);
        }

        [Fact]
        public async Task Local_engine_threads_cancellation_into_the_Fabric_transport()
        {
            EntraToken.FabricTokenForTests = () => "test-bearer";
            FabricRest.TestClientFactory = () => Client(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable");
            });
            using var engine = new LocalEngine(new SessionManager());
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => engine.ListDeploymentPipelinesAsync("azcli", null, cts.Token));
        }

        [Fact]
        public async Task HttpClient_timeout_is_contained_but_caller_cancellation_still_propagates()
        {
            EntraToken.FabricTokenForTests = () => "test-bearer";
            FabricRest.TestClientFactory = () => Client((_, _) =>
                Task.FromException<HttpResponseMessage>(new TaskCanceledException("HTTP client timeout")));
            using var engine = new LocalEngine(new SessionManager());

            var timedOut = await engine.FabricGitStatusAsync("workspace", "azcli", null, CancellationToken.None);
            Assert.Contains("HTTP client timeout", timedOut.Error);

            FabricRest.TestClientFactory = () => Client(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable");
            });
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => engine.FabricGitStatusAsync("workspace", "azcli", null, cts.Token));
        }

        [Fact]
        public async Task Data_agent_write_contains_transport_timeout_on_its_result()
        {
            EntraToken.FabricTokenForTests = () => "test-bearer";
            FabricRest.TestClientFactory = () => Client((_, _) =>
                Task.FromException<HttpResponseMessage>(new TaskCanceledException("HTTP client timeout")));
            using var engine = new LocalEngine(new SessionManager(), new ProEntitlement());

            var result = await engine.CreateDataAgentAsync(
                "workspace", "Agent", "instructions", commit: true, "azcli", null, "human");

            Assert.Equal("error", result.Status);
            Assert.Contains("HTTP client timeout", result.Message);
        }

        [Fact]
        public async Task Fabric_git_writes_contain_transport_timeout_after_the_status_read()
        {
            const string status = "{\"workspaceHead\":\"workspace-head\",\"remoteCommitHash\":\"remote-head\",\"changes\":[{\"workspaceChange\":\"Modified\",\"remoteChange\":\"Modified\"}]}";
            EntraToken.FabricTokenForTests = () => "test-bearer";
            FabricRest.TestClientFactory = () => Client((request, _) => request.Method == HttpMethod.Get
                ? Task.FromResult(Json(status))
                : Task.FromException<HttpResponseMessage>(new TaskCanceledException("HTTP client timeout")));
            using var engine = new LocalEngine(new SessionManager(), new ProEntitlement());

            var commit = await engine.FabricGitCommitAsync(
                "workspace", "test", null, commit: true, "azcli", null, "human");
            var update = await engine.FabricGitUpdateAsync(
                "workspace", "PreferRemote", allowOverride: false, commit: true, "azcli", null, "human");

            Assert.Equal("Failed", commit.Status);
            Assert.Contains("HTTP client timeout", commit.Error);
            Assert.Equal("Failed", update.Status);
            Assert.Contains("HTTP client timeout", update.Error);
        }

        [Fact]
        public async Task Deployment_write_contains_transport_timeout_after_the_preview_reads()
        {
            EntraToken.FabricTokenForTests = () => "test-bearer";
            FabricRest.TestClientFactory = () => Client((request, _) =>
            {
                if (request.Method != HttpMethod.Get)
                    return Task.FromException<HttpResponseMessage>(new TaskCanceledException("HTTP client timeout"));

                var path = request.RequestUri.AbsolutePath;
                return Task.FromResult(path.EndsWith("/stages/source/items", StringComparison.Ordinal)
                    ? Json("{\"value\":[{\"itemId\":\"model\",\"itemDisplayName\":\"Model\",\"itemType\":\"SemanticModel\"}]}")
                    : Json("{\"value\":[{\"id\":\"source\",\"order\":0,\"displayName\":\"Development\"},{\"id\":\"target\",\"order\":1,\"displayName\":\"Test\"},{\"id\":\"production\",\"order\":2,\"displayName\":\"Production\"}]}"));
            });
            using var engine = new LocalEngine(new SessionManager(), new ProEntitlement());

            var result = await engine.DeployStageAsync(
                "pipeline", "source", "target", null, null, commit: true, confirmToken: null,
                forceOverride: false, "azcli", null, "human");

            Assert.Equal("Failed", result.Status);
            Assert.Contains("HTTP client timeout", result.Error);
        }

        [Fact]
        public async Task Remote_engine_cancellation_crosses_the_RPC_door_and_stops_the_owner_transport()
        {
            EntraToken.FabricTokenForTests = () => "test-bearer";
            FabricRest.TestClientFactory = () => Client(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("unreachable");
            });
            var pipe = "semanticus-fabric-cancel-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var sessions = new SessionManager();
            using var owner = new LocalEngine(sessions);
            using var server = new RpcServer(sessions, owner, pipe);
            using var serverCts = new CancellationTokenSource();
            var serverTask = server.RunAsync(serverCts.Token);
            RemoteEngine remote = null;
            try
            {
                remote = await RemoteEngine.ConnectAsync(pipe);
                using var callCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => remote.ListDeploymentPipelinesAsync("azcli", null, callCts.Token));
            }
            finally
            {
                remote?.Dispose();
                serverCts.Cancel();
                try { await serverTask; } catch { }
                sessions.Dispose();
            }
        }

        [Fact]
        public async Task Git_timeout_stops_the_process_and_returns_a_clear_failure()
        {
            var run = await GitCli.RunWithTimeoutAsync(Environment.CurrentDirectory, TimeSpan.FromMilliseconds(250),
                "-c", "alias.semanticus-hang=!sleep 30", "semanticus-hang");

            Assert.True(run.TimedOut);
            Assert.False(run.Ok);
            Assert.Contains("was stopped", GitCli.Error(run));
            Assert.DoesNotContain("Authenticate outside Semanticus", GitCli.Error(run));
        }

        [Fact]
        public void Git_transfer_verbs_get_a_distinct_long_running_budget()
        {
            Assert.Equal(TimeSpan.FromMinutes(15), GitCli.TimeoutFor(new[] { "clone", "https://example.test/repo" }));
            Assert.Equal(TimeSpan.FromMinutes(15), GitCli.TimeoutFor(new[] { "push", "origin", "main" }));
            Assert.Equal(TimeSpan.FromMinutes(15), GitCli.TimeoutFor(new[] { "pull", "--ff-only" }));
            Assert.Equal(TimeSpan.FromMinutes(15), GitCli.TimeoutFor(new[] { "fetch", "origin" }));
            Assert.Equal(TimeSpan.FromMinutes(2), GitCli.TimeoutFor(new[] { "status", "--short" }));
        }

        [Fact]
        public async Task Git_caller_cancellation_is_not_misreported_as_an_internal_timeout()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GitCli.RunAsync(Environment.CurrentDirectory, cts.Token,
                "-c", "alias.semanticus-hang=!sleep 30", "semanticus-hang"));
        }

        private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
            => new(new DelegateHandler(send), disposeHandler: true) { BaseAddress = new Uri(FabricRest.BaseUrl) };

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed class DelegateHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
            public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => _send(request, cancellationToken);
        }
    }
}
