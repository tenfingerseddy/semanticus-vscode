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
    /// #141, relaxed by Kane on 10 Oct ("telling Claude you approve something should be enough"): the person's word in chat clears a RED
    /// deploy/readiness gate for the assistant. Inside a granted deploy window the assistant passes those words as overrideReason and the
    /// publish goes on, recorded in the audit chain BEFORE the push. With no words it is refused and told to ask the person in chat; a human
    /// with a reason proceeds the same way. Two invariants preserved: a scanner FAILURE (gate could not run) still proceeds+records (this
    /// issue is only about a scanner that RAN and said RED), and the override is recorded before the push happens.
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

            Assert.True(LocalEngine.IsAgentRedOverrideRefused(red, "agent"));    // RED + agent with no words ⇒ told to ask the person in chat
            Assert.True(LocalEngine.IsAgentRedOverrideRefused(red, "system"));   // any non-human origin fails closed
            Assert.False(LocalEngine.IsAgentRedOverrideRefused(red, "agent", "the owner said yes in chat"));   // the person's word in chat is enough (hero17 P5)
            Assert.False(LocalEngine.IsAgentRedOverrideRefused(red, "human"));   // the human is the authority
            Assert.False(LocalEngine.IsAgentRedOverrideRefused(pass, "agent"));  // a passing gate needs no override
            Assert.False(LocalEngine.IsAgentRedOverrideRefused(null, "agent"));  // scanner FAILURE ⇒ proceed+record (invariant a)
        }

        // ---- Kane 10 Oct: the person's word in chat clears a RED gate for the assistant. No Studio click. The words are recorded. ----
        [Fact]
        public async Task Agent_with_the_persons_words_publishes_past_a_red_gate_and_the_words_are_recorded()
        {
            using var engine = await RedModelAsync();

            var token = await ReviewFenceTest.TokenAsync(engine, LocalEndpoint, "Blocked", "agent");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.DeployLiveAsync(LocalEndpoint, "Blocked", "serviceprincipal", null, null,
                    commit: true, origin: "agent", overrideReason: "the owner said yes in chat", confirmToken: token));
            Assert.DoesNotContain("HUMAN-only", ex.Message);           // the throw is the dead loopback port, after the gate
            Assert.DoesNotContain("overrideReason", ex.Message);

            var chain = await engine.ListVerifiedEditsAsync();
            var rec = Assert.Single(chain.Records.Where(r => r.Op == "deploy_live" && r.Verdict == "overridden"));
            Assert.Equal("the owner said yes in chat", rec.OverrideReason);
        }

        // ---- a human with a reason proceeds past the RED gate, and the override is recorded BEFORE the push ----
        [Fact]
        public async Task Human_override_of_a_red_gate_proceeds_and_records_before_the_push()
        {
            using var engine = await RedModelAsync();

            // The push fails on the dead loopback endpoint — but only AFTER the gate is cleared and the override is
            // recorded, so the throw is the connection failure, never the gate refusal.
            var token = await ReviewFenceTest.TokenAsync(engine, LocalEndpoint, "Blocked");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.DeployLiveAsync(LocalEndpoint, "Blocked", null, null, null,
                    commit: true, origin: "human", overrideReason: "data owner approved the hotfix", confirmToken: token));
            Assert.DoesNotContain("HUMAN-only", ex.Message);
            Assert.DoesNotContain("blocked by the deploy gate", ex.Message);

            // The accountable override was appended to the audit chain BEFORE the (failed) push.
            var chain = await engine.ListVerifiedEditsAsync();
            var rec = Assert.Single(chain.Records.Where(r => r.Op == "deploy_live" && r.Verdict == "overridden"));
            Assert.Equal("data owner approved the hotfix", rec.OverrideReason);

            // The record's SUMMARY is the sentence both doors show a person, and it used to open "gate RED (…):
            // override accepted to deploy to …" — three pieces of engine vocabulary in the one line Kane reads off
            // Changes > History. It says the same facts in plain words now: what the check found, that the publish
            // still went ahead because a reason was written, and where it went. The destination ends the sentence
            // with NO full stop, because gluing one onto an endpoint changes an address a person may copy.
            Assert.StartsWith("Red safety check (", rec.Summary);
            Assert.Contains("Published anyway with a written reason, to " + LocalEndpoint + "/Blocked", rec.Summary);
            Assert.EndsWith(LocalEndpoint + "/Blocked", rec.Summary);
            Assert.DoesNotContain("gate RED", rec.Summary);
            Assert.DoesNotContain("override accepted", rec.Summary);
            // Every fact the old line carried is still there: the gate's own blocker text is unedited inside it.
            var blockers = string.Join("; ", (await engine.DeployGateAsync(null, "human")).Blockers);
            Assert.Contains(blockers, rec.Summary);
        }

        // ---- Kane 8 Oct: a person is never asked for a reason. A red check goes in the history as "Published with
        //      warnings" and the commit goes on; the assistant needs the person's chat words as overrideReason (tests above and below) ----
        [Fact]
        public async Task A_person_with_no_reason_publishes_past_a_red_gate_and_the_record_says_published_with_warnings()
        {
            using var engine = await RedModelAsync();

            var token = await ReviewFenceTest.TokenAsync(engine, LocalEndpoint, "Blocked");
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                engine.DeployLiveAsync(LocalEndpoint, "Blocked", null, null, null,
                    commit: true, origin: "human", overrideReason: null, confirmToken: token));
            // The throw is the dead loopback port, reached AFTER the gate. Never the gate, never a request for a reason.
            Assert.DoesNotContain("blocked by the deploy gate", ex.Message);
            Assert.DoesNotContain("overrideReason", ex.Message);

            var chain = await engine.ListVerifiedEditsAsync();
            var rec = Assert.Single(chain.Records.Where(r => r.Op == "deploy_live" && r.Verdict == "overridden"));
            Assert.True(string.IsNullOrEmpty(rec.OverrideReason));   // no reason was given, none is invented
            Assert.StartsWith("Published with warnings (", rec.Summary);
            Assert.EndsWith(LocalEndpoint + "/Blocked", rec.Summary);
            Assert.DoesNotContain("written reason", rec.Summary);
        }

        // ---- an agent with no words is told to ask the person in chat and to pass their answer: no Studio click is named ----
        [Fact]
        public async Task Agent_with_no_reason_is_told_to_ask_the_person_in_chat()
        {
            using var engine = await RedModelAsync();

            var token = await ReviewFenceTest.TokenAsync(engine, LocalEndpoint, "Blocked", "agent");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.DeployLiveAsync(LocalEndpoint, "Blocked", "serviceprincipal", null, null,
                    commit: true, origin: "agent", overrideReason: null, confirmToken: token));

            Assert.DoesNotContain("HUMAN-only", ex.Message);
            Assert.Contains("ask the person in chat", ex.Message);
            Assert.Contains("overrideReason", ex.Message);
            var chain = await engine.ListVerifiedEditsAsync();
            Assert.DoesNotContain(chain.Records, r => r.Op == "deploy_live" && r.Verdict == "overridden");
        }

        // ---- hero19 D6: after the person's yes clears a finding, later publishes do not ask again for it while it is unchanged ----
        [Fact]
        public async Task A_finding_the_person_said_yes_to_does_not_ask_again_until_it_changes_and_a_new_red_finding_still_asks()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            await engine.CreateModelAsync("Blocked", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateMeasureAsync(t, "Total Sales", "1", "human");   // visible, undescribed: the first red finding
            async Task<Exception> AgentPublish(string reason)
            {
                var token = await ReviewFenceTest.TokenAsync(engine, LocalEndpoint, "Blocked", "agent");
                return await Assert.ThrowsAnyAsync<Exception>(() =>
                    engine.DeployLiveAsync(LocalEndpoint, "Blocked", "serviceprincipal", null, null,
                        commit: true, origin: "agent", overrideReason: reason, confirmToken: token));
            }
            // 1) the person's yes clears the red check; the throw is the dead loopback port, after the gate
            Assert.DoesNotContain("deploy gate is RED", (await AgentPublish("the owner said yes in chat")).Message);
            // 2) the same finding, unchanged: no second ask
            Assert.DoesNotContain("deploy gate is RED", (await AgentPublish(null)).Message);
            // 3) a NEW red finding (a second undescribed measure) asks again
            var second = await engine.CreateMeasureAsync(t, "Total Cost", "2", "human");
            Assert.Contains("deploy gate is RED", (await AgentPublish(null)).Message);
            // 4) once the person says yes to that one too, both are cleared
            Assert.DoesNotContain("deploy gate is RED", (await AgentPublish("yes, that one too")).Message);
            Assert.DoesNotContain("deploy gate is RED", (await AgentPublish(null)).Message);
            // 5) a cleared finding that is edited asks again
            await engine.SetDaxAsync(second, "3", "human");
            Assert.Contains("deploy gate is RED", (await AgentPublish(null)).Message);
        }
    }
}
