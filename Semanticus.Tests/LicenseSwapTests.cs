using System;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using StreamJsonRpc;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>F-242 (Kane, 23 September, the twelfth drop). Remove license on the Pro page appeared to do nothing:
    /// the engine read the licence once at start, and the window had adopted an engine it did not start, so nothing
    /// restarted it. Once it finally restarted, Free showed correctly.
    ///
    /// A licence change now reaches the RUNNING engine through a person-only RPC that re-verifies the token and swaps
    /// the entitlement in place, in both directions. It lives on the human governance target, so an assistant
    /// connection cannot even see it, and there is no MCP op for it: activating a licence is a person's action. The
    /// agent door learns of the change on its next call, as it does of any change.</summary>
    public sealed class LicenseSwapTests
    {
        private const string Challenge = "license-swap-test-challenge-0123456789abcdef";
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

        /// <summary>A real signed token and a real verifier with a key minted for the test, so the swap runs the
        /// same signature check a person's key goes through. An empty token falls to "no license" here, which is what
        /// a machine with no environment licence and no licence file gets at a restart.</summary>
        private static (LocalEngine engine, SessionManager sessions, string token) FreeEngineWithIssuer()
        {
            var (pub, priv) = LicenseVerifier.GenerateKeyPair();
            var token = LicenseVerifier.Mint(priv, new LicenseClaims { Sub = "buyer@contoso.com", Tier = "pro", Iat = Now.ToUnixTimeSeconds(), Exp = 0 });
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, LicenseEntitlement.Evaluate(null, pub, devPro: false, Now));
            engine.UseLicenseEvaluatorForTest(t => LicenseEntitlement.Evaluate(t, pub, devPro: false, Now));
            return (engine, sessions, token);
        }

        [Fact]
        public async Task Activate_then_remove_without_any_restart_flips_get_entitlement_on_both_doors()
        {
            var (engine, sessions, token) = FreeEngineWithIssuer();
            var pipeName = TestPipe.Mint("semanticus-swap-");
            using var server = new RpcServer(sessions, engine, pipeName, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            RpcClient human = null;
            RemoteEngine agent = null;
            try
            {
                human = await RpcClient.ConnectAsync(pipeName, "human", Challenge);
                agent = await RemoteEngine.ConnectAsync(pipeName);

                Assert.Equal("free", (await human.InvokeAsync<EntitlementInfo>("getEntitlement")).Tier);
                Assert.Equal("free", (await McpTools.GetEntitlement(agent)).Tier);

                // Free to Pro, on the engine that is already running.
                var on = await human.InvokeAsync<LicenseSwapResult>("applyLicense", token, false);
                Assert.True(on.Accepted);
                Assert.True(on.Applied);
                Assert.Equal("pro", on.Entitlement.Tier);
                Assert.Equal("buyer@contoso.com", on.Entitlement.LicensedTo);
                Assert.Contains("workflows", on.Entitlement.Features);
                Assert.Equal("pro", (await human.InvokeAsync<EntitlementInfo>("getEntitlement")).Tier);
                Assert.Equal("pro", (await McpTools.GetEntitlement(agent)).Tier);

                // Pro to free, the Remove license direction that did nothing on Kane's machine.
                var off = await human.InvokeAsync<LicenseSwapResult>("applyLicense", "", false);
                Assert.True(off.Accepted);
                Assert.True(off.Applied);
                Assert.Equal("free", off.Entitlement.Tier);
                Assert.Empty(off.Entitlement.Features);
                Assert.Equal("free", (await human.InvokeAsync<EntitlementInfo>("getEntitlement")).Tier);
                Assert.Equal("free", (await McpTools.GetEntitlement(agent)).Tier);
            }
            finally
            {
                (agent as IDisposable)?.Dispose();
                human?.Dispose();
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                sessions.Dispose();
            }
        }

        [Fact]
        public void A_bad_token_is_refused_and_changes_nothing()
        {
            var (engine, sessions, token) = FreeEngineWithIssuer();
            using (sessions)
            {
                Assert.True(engine.ApplyLicense(token, verifyOnly: false).Applied);
                var before = engine.GetEntitlementAsync().Result;

                foreach (var bad in new[] { "not-a-real-signed-license", token + "x" })
                {
                    var refused = engine.ApplyLicense(bad, verifyOnly: false);
                    Assert.False(refused.Accepted);
                    Assert.False(refused.Applied);
                    Assert.False(string.IsNullOrWhiteSpace(refused.Reason));
                    Assert.Equal("pro", refused.Entitlement.Tier);   // the engine's answer: still what it was
                }
                var after = engine.GetEntitlementAsync().Result;
                Assert.Equal(before.Tier, after.Tier);
                Assert.Equal(before.LicensedTo, after.LicensedTo);
                Assert.Equal(before.Features, after.Features);
            }
        }

        [Fact]
        public void Verify_only_answers_without_swapping()
        {
            var (engine, sessions, token) = FreeEngineWithIssuer();
            using (sessions)
            {
                var check = engine.ApplyLicense(token, verifyOnly: true);
                Assert.True(check.Accepted);
                Assert.False(check.Applied);
                Assert.Equal("free", engine.GetEntitlementAsync().Result.Tier);
                Assert.False(engine.ApplyLicense("garbage", verifyOnly: true).Accepted);
            }
        }

        [Fact]
        public async Task The_swap_is_person_only_and_on_no_assistant_door()
        {
            // Structural: the governance target has it, the agent target, IEngine and every MCP op do not.
            Assert.NotNull(typeof(HumanGovernanceRpcTarget).GetMethod("applyLicense"));
            Assert.Null(typeof(EngineRpcTarget).GetMethod("applyLicense"));
            Assert.DoesNotContain(typeof(IEngine).GetMethods(), m => m.Name.Contains("License", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(ProFeatureMapTests.CompiledOperationNames(),
                n => n.Contains("license", StringComparison.OrdinalIgnoreCase) || n.Contains("licence", StringComparison.OrdinalIgnoreCase));

            // On the wire: an agent connection gets no such method, and the tier does not move.
            var (engine, sessions, token) = FreeEngineWithIssuer();
            var pipeName = TestPipe.Mint("semanticus-swap-");
            using var server = new RpcServer(sessions, engine, pipeName, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            RpcClient agent = null;
            try
            {
                agent = await RpcClient.ConnectAsync(pipeName, "agent");
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => agent.InvokeAsync<LicenseSwapResult>("applyLicense", token, false));
                Assert.Contains("method", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("free", (await agent.InvokeAsync<EntitlementInfo>("getEntitlement")).Tier);
            }
            finally
            {
                agent?.Dispose();
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                sessions.Dispose();
            }
        }

        /// <summary>F-243 (Sol 6, slice 5 review). A request that passed the Pro check BEFORE the licence was removed
        /// held the old service, the old client and the old Pro answer, and could still send once it got going. The
        /// window here is the exact one Sol named: the ask has cleared every gate and is waiting to send when Remove
        /// license lands. A removed licence must never send one more request.</summary>
        [Fact]
        public async Task Removing_the_license_while_a_request_waits_to_send_stops_it()
        {
            var (pub, priv) = LicenseVerifier.GenerateKeyPair();
            var token = LicenseVerifier.Mint(priv, new LicenseClaims { Sub = "buyer@contoso.com", Tier = "pro", Iat = Now.ToUnixTimeSeconds(), Exp = 0 });
            using (var f = new DecisionFixture(LicenseEntitlement.Evaluate(token, pub, devPro: false, Now)))
            {
                f.Engine.UseLicenseEvaluatorForTest(t => LicenseEntitlement.Evaluate(t, pub, devPro: false, Now));
                var measureRef = await f.BuildModelAsync();
                await f.ConsentAsync();
                var oracle = f.UseEmptyRecorded();
                var service = f.Engine.Decisions;

                using var reached = new ManualResetEventSlim(false);
                using var release = new ManualResetEventSlim(false);
                service.BeforeSendForTest = () => { reached.Set(); release.Wait(TimeSpan.FromSeconds(30)); };

                var asking = Task.Run(() => f.Engine.ProposeMeasureDefaultsAsync(measureRef));
                Assert.True(reached.Wait(TimeSpan.FromSeconds(30)), "the ask never reached the moment before sending");

                var removed = f.Engine.ApplyLicense("", verifyOnly: false);
                Assert.True(removed.Applied);
                Assert.Equal("free", removed.Entitlement.Tier);
                release.Set();

                var proposal = await asking;
                Assert.False(proposal.Asked);
                Assert.Empty(oracle.AskedDigests);
                Assert.Equal(0, service.RequestsStarted);
            }
        }

        private sealed class RpcClient : IDisposable
        {
            private readonly NamedPipeClientStream _pipe;
            private readonly JsonRpc _rpc;

            private RpcClient(NamedPipeClientStream pipe, JsonRpc rpc) { _pipe = pipe; _rpc = rpc; }

            internal static async Task<RpcClient> ConnectAsync(string pipeName, string role, string challenge = null)
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                await RpcHandshake.WriteAsync(pipe, role == "human" ? RpcConnectionRole.Human : RpcConnectionRole.Agent, challenge);
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
