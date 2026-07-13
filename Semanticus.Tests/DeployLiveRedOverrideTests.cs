using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// #141 (Kane's ruling) — clearing a RED deploy/readiness gate on a live deploy is HUMAN-only. Inside a granted
    /// deploy window an agent-authored overrideReason is REFUSED with a teaching report (the grant approved a plan,
    /// not "ship even if the gate turns red"); a human with a reason proceeds and the override is recorded BEFORE the
    /// push. Two invariants preserved: a scanner FAILURE (gate could not run) still proceeds+records (this issue is
    /// only about a scanner that RAN and said RED), and the override is recorded before the push happens.
    /// </summary>
    [Collection("restore-root")]   // mutates the static AgentPolicyStore/ApprovalLedger roots — serialize with the family
    public sealed class DeployLiveRedOverrideTests : IDisposable
    {
        // A loopback endpoint is auto-labelled 'local' (ResolveTargetLabel) ⇒ under the default 'standard' preset an
        // AGENT may deploy to it (the DeployLive permission gate passes — the "granted window") — so a run reaches the
        // readiness gate we're testing. The push then fails fast on the dead port, AFTER the readiness decision.
        private const string LocalEndpoint = "localhost:2";
        private readonly string _root;
        private readonly string _safeRoot;

        public DeployLiveRedOverrideTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-redoverride-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            AgentPolicyStore.RootOverride = _root;
            ApprovalLedger.RootOverride = _root;
            ConnectionRegistry.RootOverride = _root;
        }
        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot; ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // A gate-BLOCKING model: a visible, undescribed measure ⇒ >50% of visible measures undescribed ⇒ the readiness
        // hard-gate caps the grade and the deploy gate returns RED (mirrors DeployFeatureTests.BlockedModelAsync).
        private static async Task<LocalEngine> RedModelAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(true));
            await engine.CreateModelAsync("Blocked", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Customer Name", "String", "Customer Name", "human");
            await engine.CreateMeasureAsync(t, "Total Sales", "1", "human");   // visible, undescribed
            return engine;
        }

        // ---- the pure decision (covers the scanner-FAILURE invariant deterministically) ----
        [Fact]
        public void IsAgentRedOverrideRefused_only_refuses_an_agent_on_a_gate_that_ran_red()
        {
            var red = new DeployGate { Pass = false, Blockers = new[] { "measures undescribed" } };
            var pass = new DeployGate { Pass = true };

            Assert.True(LocalEngine.IsAgentRedOverrideRefused(red, "agent"));    // RED + agent ⇒ refused
            Assert.True(LocalEngine.IsAgentRedOverrideRefused(red, "system"));   // any non-human origin fails closed
            Assert.False(LocalEngine.IsAgentRedOverrideRefused(red, "human"));   // the human is the authority
            Assert.False(LocalEngine.IsAgentRedOverrideRefused(pass, "agent"));  // a passing gate needs no override
            Assert.False(LocalEngine.IsAgentRedOverrideRefused(null, "agent"));  // scanner FAILURE ⇒ proceed+record (invariant a)
        }

        // ---- an agent inside a granted window cannot clear a RED gate: refused with a teaching report, no push ----
        [Fact]
        public async Task Agent_override_of_a_red_gate_is_refused_and_records_nothing()
        {
            using var engine = await RedModelAsync();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.DeployLiveAsync(LocalEndpoint, "Blocked", "serviceprincipal", null, null,
                    commit: true, origin: "agent", overrideReason: "agent decided to ship it"));

            Assert.Contains("HUMAN-only", ex.Message);                 // names the rule…
            Assert.Contains("agent cannot override", ex.Message);
            Assert.Contains("overrideReason", ex.Message);             // …and what a human must do

            // Refused BEFORE recording — no accountable override was written to the audit trail.
            var chain = await engine.ListVerifiedEditsAsync();
            Assert.DoesNotContain(chain.Records, r => r.Op == "deploy_live" && r.Verdict == "overridden");
        }

        // ---- a human with a reason proceeds past the RED gate, and the override is recorded BEFORE the push ----
        [Fact]
        public async Task Human_override_of_a_red_gate_proceeds_and_records_before_the_push()
        {
            using var engine = await RedModelAsync();

            // The push fails on the dead loopback endpoint — but only AFTER the gate is cleared and the override is
            // recorded, so the throw is the connection failure, never the gate refusal.
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.DeployLiveAsync(LocalEndpoint, "Blocked", null, null, null,
                    commit: true, origin: "human", overrideReason: "data owner approved the hotfix"));
            Assert.DoesNotContain("HUMAN-only", ex.Message);
            Assert.DoesNotContain("blocked by the deploy gate", ex.Message);

            // The accountable override was appended to the audit chain BEFORE the (failed) push.
            var chain = await engine.ListVerifiedEditsAsync();
            var rec = Assert.Single(chain.Records.Where(r => r.Op == "deploy_live" && r.Verdict == "overridden"));
            Assert.Equal("data owner approved the hotfix", rec.OverrideReason);
        }

        // ---- an agent WITHOUT a reason gets the HUMAN-only refusal too: the origin check deliberately runs BEFORE
        //      the missing-reason branch, so an agent is never taught "pass overrideReason" only to be refused again ----
        [Fact]
        public async Task Agent_with_no_reason_gets_the_human_only_refusal()
        {
            using var engine = await RedModelAsync();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.DeployLiveAsync(LocalEndpoint, "Blocked", "serviceprincipal", null, null,
                    commit: true, origin: "agent", overrideReason: null));

            // The human-only refusal is what teaches recovery (an agent can never supply a valid override).
            Assert.Contains("HUMAN-only", ex.Message);
        }
    }
}
