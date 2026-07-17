using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using StreamJsonRpc;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class RpcRoleBindingTests
    {
        private const string Challenge = "rpc-role-test-challenge-0123456789abcdef";

        [Fact]
        public void Weak_ui_challenge_is_refused_before_listening()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            Assert.Throws<ArgumentException>(() => new RpcServer(sessions, engine, "unused", "short"));
        }

        [Fact]
        public async Task Connection_role_overrides_every_legacy_origin_value()
        {
            var pipeName = "semanticus-role-" + Guid.NewGuid().ToString("N");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            using var server = new RpcServer(sessions, engine, pipeName, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            RpcClient human = null;
            RpcClient agent = null;
            try
            {
                human = await RpcClient.ConnectAsync(pipeName, "human", Challenge);
                agent = await RpcClient.ConnectAsync(pipeName, "agent");

                await human.InvokeAsync<OpenResult>("createModel", "Role binding", 1604);
                var table = await human.InvokeAsync<string>("createTable", "Facts", "agent");

                var humanChange = NextChange(sessions);
                var measure = await human.InvokeAsync<string>("createMeasure", table, "Amount", "1", "agent", null);
                Assert.Equal("human", (await humanChange.WaitAsync(TimeSpan.FromSeconds(5))).Origin);

                var agentChange = NextChange(sessions);
                await agent.InvokeAsync<SetResult>("setDax", measure, "2", "human");
                Assert.Equal("agent", (await agentChange.WaitAsync(TimeSpan.FromSeconds(5))).Origin);
            }
            finally
            {
                agent?.Dispose();
                human?.Dispose();
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                sessions.Dispose();
            }
        }

        [Fact]
        public async Task Agent_surface_structurally_excludes_governance_mutations()
        {
            Assert.Null(typeof(EngineRpcTarget).GetMethod("setAgentPolicyEnabled"));
            Assert.NotNull(typeof(HumanGovernanceRpcTarget).GetMethod("setAgentPolicyEnabled"));

            var pipeName = "semanticus-role-" + Guid.NewGuid().ToString("N");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            using var server = new RpcServer(sessions, engine, pipeName, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            RpcClient agent = null;
            try
            {
                agent = await RpcClient.ConnectAsync(pipeName, "agent");
                var ex = await Assert.ThrowsAnyAsync<Exception>(
                    () => agent.InvokeAsync<AgentPolicy>("setAgentPolicyEnabled", true, "human"));
                Assert.Contains("method", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                agent?.Dispose();
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                sessions.Dispose();
            }
        }

        [Fact]
        public async Task Wrong_ui_challenge_receives_no_rpc_surface()
        {
            var pipeName = "semanticus-role-" + Guid.NewGuid().ToString("N");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            using var server = new RpcServer(sessions, engine, pipeName, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            RpcClient impostor = null;
            try
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    RpcClient.ConnectAsync(pipeName, "human", "wrong-challenge-0123456789abcdefghijk"));
            }
            finally
            {
                impostor?.Dispose();
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                sessions.Dispose();
            }
        }

        [Fact]
        public async Task Mcp_owned_engine_explicitly_rejects_human_and_keeps_agent_door_available()
        {
            var pipeName = "semanticus-role-" + Guid.NewGuid().ToString("N");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            using var server = new RpcServer(sessions, engine, pipeName);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            RpcClient agent = null;
            try
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    RpcClient.ConnectAsync(pipeName, "human", Challenge));
                agent = await RpcClient.ConnectAsync(pipeName, "agent");
                Assert.Equal(0, (await agent.InvokeAsync<SessionInfo>("sessionInfo")).Revision);
            }
            finally
            {
                agent?.Dispose();
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                sessions.Dispose();
            }
        }

        private static Task<ChangeNotification> NextChange(SessionManager sessions)
        {
            var ready = new TaskCompletionSource<ChangeNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(ChangeNotification change)
            {
                sessions.Bus.Changed -= Handler;
                ready.TrySetResult(change);
            }
            sessions.Bus.Changed += Handler;
            return ready.Task;
        }

        private sealed class RpcClient : IDisposable
        {
            private readonly NamedPipeClientStream _pipe;
            private readonly JsonRpc _rpc;

            private RpcClient(NamedPipeClientStream pipe, JsonRpc rpc)
            {
                _pipe = pipe;
                _rpc = rpc;
            }

            internal static async Task<RpcClient> ConnectAsync(string pipeName, string role, string challenge = null)
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                await RpcHandshake.WriteAsync(pipe,
                    role == "human" ? RpcConnectionRole.Human : RpcConnectionRole.Agent, challenge);
                await RpcHandshake.ReadAcceptedAsync(pipe);
                var rpc = new JsonRpc(RpcServer.CreateHandler(pipe));
                rpc.StartListening();
                return new RpcClient(pipe, rpc);
            }

            internal Task<T> InvokeAsync<T>(string method, params object[] args) => _rpc.InvokeAsync<T>(method, args);

            public void Dispose()
            {
                try { _rpc.Dispose(); } catch { }
                try { _pipe.Dispose(); } catch { }
            }
        }
    }
}
