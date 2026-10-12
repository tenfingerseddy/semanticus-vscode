using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// 1.4 chatyes (Kane, 10 Oct): the person's yes in chat is enough. A target with no label counts as TEST, so the Standard preset
    /// does not hold a publish or a row preview on it, while a target the person labelled production still is held. The assistant
    /// can turn Jev on for a model, the engine records who did it, and the person can still turn it off. The Permissions page stays
    /// the person's alone.
    /// </summary>
    [Collection("restore-root")]
    public sealed class ChatYesTests : GateFixtureBase
    {
        public ChatYesTests(RestoreRootFixture fixture) : base(fixture) { }

        // A fresh endpoint per test: the connection registry root is shared across this collection.
        private static string NewEndpoint() => "powerbi://api.powerbi.com/v1.0/myorg/Semanticus Demo " + Guid.NewGuid().ToString("N").Substring(0, 6);
        private static Semanticus.Engine.AgentPolicy Standard() => AgentPolicyPresets.Build("standard");

        [Fact]
        public void An_unlabelled_target_counts_as_test_and_is_not_held_under_Standard()
        {
            var endpoint = NewEndpoint();
            var r = ConnectionRegistry.Remember("xmla", endpoint, "Demo", "Demo", null, "azcli");
            Assert.True(ConnectionRegistry.IsUnlabelled(r));
            Assert.Equal("dev", ConnectionRegistry.EffectiveLabel(r));
            var label = LocalEngine.ResolveTargetLabel(endpoint, "Demo");
            Assert.Equal("dev", label);
            foreach (var cap in new[] { AgentCapability.DeployLive, AgentCapability.QueryData })
                Assert.Equal(GateOutcome.Allow, AgentPolicyGuard.Decide(cap, label, "agent", true, Standard()).Outcome);
        }

        [Fact]
        public void A_target_nobody_has_saved_also_counts_as_test()
        {
            Assert.Equal("dev", LocalEngine.ResolveTargetLabel("powerbi://api.powerbi.com/v1.0/myorg/Never Seen", "X"));
        }

        [Fact]
        public void A_target_the_person_labelled_production_is_still_held_under_Standard()
        {
            var endpoint = NewEndpoint();
            var r = ConnectionRegistry.Remember("xmla", endpoint, "Demo", "Demo", null, "azcli");
            ConnectionRegistry.SetLabel(r.Id, "prod", "human");
            var label = LocalEngine.ResolveTargetLabel(endpoint, "Demo");
            Assert.Equal("prod", label);
            foreach (var cap in new[] { AgentCapability.DeployLive, AgentCapability.QueryData })
                Assert.Equal(GateOutcome.Ask, AgentPolicyGuard.Decide(cap, label, "agent", true, Standard()).Outcome);
        }

        [Fact]
        public async Task The_assistants_consent_is_recorded_as_its_own_and_the_person_can_turn_it_off()
        {
            var (engine, _, _) = await OpenAsync("chatyes-consent");
            using (engine)
            {
                var gate = Gate(engine);
                var fields = Semanticus.Decisions.DecisionRegistry.DeclaredFields.ToArray();
                var version = Semanticus.Decisions.DecisionRegistry.ConsentSchemaVersion;
                var done = await gate.SetConsentAsync(fields, version, "agent");
                Assert.False(done.Asked);
                Assert.True(done.Granted);
                Assert.True(done.Current);
                Assert.Equal("agent", done.GrantedBy);
                Assert.NotNull(done.GrantedUtc);
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                var again = await engine.GetConsentAsync();   // the record is on disk, not only in the answer
                Assert.Equal("agent", again.GrantedBy);
                Assert.Equal(done.AppliesTo, again.AppliesTo);

                var off = await engine.WithdrawConsentAsync("human");
                Assert.False(off.Granted);
                Assert.False(off.Current);
                Assert.False((await engine.GetConsentAsync()).Granted);
            }
        }

        [Fact]
        public async Task The_persons_own_consent_is_recorded_as_theirs()
        {
            var (engine, _, _) = await OpenAsync("chatyes-human");
            using (engine)
            {
                var fields = Semanticus.Decisions.DecisionRegistry.DeclaredFields.ToArray();
                var done = await engine.SetConsentAsync(fields, Semanticus.Decisions.DecisionRegistry.ConsentSchemaVersion, "human");
                Assert.True(done.Granted);
                Assert.Equal("human", done.GrantedBy);
            }
        }

        [Fact]
        public async Task The_Permissions_page_stays_the_persons_the_assistant_is_refused()
        {
            var (engine, _, _) = await OpenAsync("chatyes-perm");
            using (engine)
            {
                AgentPolicyStore.SetPreset("standard", "human");
                var gate = Gate(engine);
                await RefusedAsync(() => gate.SetAgentPolicyPresetAsync("open", "agent"));
                await RefusedAsync(() => gate.SetAgentPolicyEnabledAsync(false, "agent"));
                Assert.Equal("standard", AgentPolicyStore.Get().Preset);
                Assert.True(AgentPolicyStore.Get().Enabled);
                // a held action is approved by the person only
                var r = ConnectionRegistry.Remember("xmla", NewEndpoint(), "Demo", "Demo", null, "azcli");
                ConnectionRegistry.SetLabel(r.Id, "prod", "human");
                var held = AgentPolicyGuard.Decide(AgentCapability.DeployLive, "prod", "agent", true, AgentPolicyStore.Get());
                Assert.Equal(GateOutcome.Ask, held.Outcome);
                await RefusedAsync(() => gate.ApproveAgentActionAsync("nothing", "agent"));
                // and a label it may set once, never change
                await Assert.ThrowsAsync<InvalidOperationException>(async () => ConnectionRegistry.SetLabel(r.Id, "dev", "agent"));
                Assert.Equal("prod", ConnectionRegistry.Find(r.Id).Label);
            }
        }

        // A hand-edited label the engine does not know reads prod, so the refusal must not tell the assistant the person labelled it production.
        [Fact]
        public void A_refusal_on_prod_says_the_target_counts_as_production_not_that_it_was_labelled_so()
        {
            var d = AgentPolicyGuard.Decide(AgentCapability.DeployLive, "prod", "agent", true, AgentPolicyPresets.Build("locked"));
            Assert.Equal(GateOutcome.Deny, d.Outcome);
            Assert.DoesNotContain("labelled production", d.Reason);
            Assert.Contains("counts as production", d.Reason);
        }
    }
}
