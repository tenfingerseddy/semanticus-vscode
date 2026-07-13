using System;
using System.IO;
using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The agent-permissions engine: the pure guard matrix, the presets, the policy store (human-only / Pro-to-config),
    /// and the approval ledger (the generalised MintToken — a human-granted, one-shot, expiring, intent-bound token).
    /// All offline. The end-to-end wiring into deploy_live / apply_model_diff is pinned in <see cref="AgentPolicyGateTests"/>.
    /// </summary>
    [Collection("restore-root")]   // mutates the static AgentPolicyStore/ApprovalLedger roots — serialize with the family
    public sealed class AgentPolicyTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        public AgentPolicyTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-policy-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            AgentPolicyStore.RootOverride = _root;
            ApprovalLedger.RootOverride = _root;
        }
        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        // ---- the wire contract: properties camelCase, dictionary KEYS verbatim ----
        // Regression for the Permissions "every cell shows Deny" bug: the RPC serializer's
        // ProcessDictionaryKeys=true rewrote the matrix capability keys (QueryData -> queryData), so the webview's
        // PascalCase lookups all missed and defaulted to deny. Keys are DATA and must round-trip verbatim.
        [Fact]
        public void Rpc_serializer_camelcases_properties_but_preserves_matrix_dictionary_keys()
        {
            var policy = new AgentPolicy
            {
                Enabled = true,
                Preset = "standard",
                Matrix = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>
                {
                    ["QueryData"] = new() { ["local"] = "allow", ["prod"] = "ask" },
                },
            };
            var ser = new Newtonsoft.Json.JsonSerializer();
            RpcServer.ConfigureSerializer(ser);
            var sw = new StringWriter();
            ser.Serialize(sw, policy);
            var json = sw.ToString();

            Assert.Contains("\"enabled\"", json);    // property -> camelCase (the webview DTO shape)
            Assert.Contains("\"matrix\"", json);
            Assert.Contains("\"QueryData\"", json);   // dictionary KEY preserved verbatim (the fix)
            Assert.DoesNotContain("\"queryData\"", json);
            Assert.Contains("\"local\"", json);
        }

        // ---- the pure guard: precedence, top to bottom ----

        [Fact]
        public void A_human_is_never_gated()
        {
            var p = AgentPolicyPresets.Build("locked");   // the strictest preset
            var d = AgentPolicyGuard.Decide(AgentCapability.DeployLive, "prod", "human", isCommit: true, p);
            Assert.Equal(GateOutcome.Allow, d.Outcome);
        }

        [Fact]
        public void A_preview_is_never_gated_even_for_the_agent()
        {
            var p = AgentPolicyPresets.Build("locked");
            var d = AgentPolicyGuard.Decide(AgentCapability.DeployLive, "prod", "agent", isCommit: false, p);
            Assert.Equal(GateOutcome.Allow, d.Outcome);
        }

        [Fact]
        public void The_global_kill_switch_off_allows_everything_except_governance()
        {
            var p = AgentPolicyPresets.Build("locked"); p.Enabled = false;
            Assert.Equal(GateOutcome.Allow, AgentPolicyGuard.Decide(AgentCapability.DeployDelete, "prod", "agent", true, p).Outcome);
            // ...but a governance op is a hard rule the switch does not relax.
            Assert.Equal(GateOutcome.Deny, AgentPolicyGuard.Decide(AgentCapability.Governance, "prod", "agent", true, p).Outcome);
        }

        [Fact]
        public void Reading_and_calculating_are_never_gated_in_any_preset()
        {
            foreach (var name in AgentPolicyPresets.Names)
            {
                var p = AgentPolicyPresets.Build(name);
                Assert.Equal(GateOutcome.Allow, AgentPolicyGuard.Decide(AgentCapability.Read, "prod", "agent", true, p).Outcome);
                Assert.Equal(GateOutcome.Allow, AgentPolicyGuard.Decide(AgentCapability.QueryCalc, "prod", "agent", true, p).Outcome);
            }
        }

        // ---- unlabelled = prod = strictest, and it is the ONLY inference ----

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("staging")]     // not one of our four ⇒ prod
        [InlineData("PROD")]        // case-folds to prod
        public void An_unrecognised_label_is_treated_as_prod(string label)
        {
            var p = AgentPolicyPresets.Build("client");   // client: prod deploy = deny
            var d = AgentPolicyGuard.Decide(AgentCapability.DeployLive, label, "agent", true, p);
            Assert.Equal(GateOutcome.Deny, d.Outcome);
        }

        // ---- the default (Free) preset "just works": local/dev allowed, uat/prod ask, nothing hard-denied ----

        [Fact]
        public void The_default_preset_allows_local_and_dev_and_asks_on_uat_and_prod()
        {
            var p = AgentPolicyPresets.Build("standard");
            Assert.Equal(GateOutcome.Allow, AgentPolicyGuard.Decide(AgentCapability.DeployLive, "local", "agent", true, p).Outcome);
            Assert.Equal(GateOutcome.Allow, AgentPolicyGuard.Decide(AgentCapability.DeployLive, "dev", "agent", true, p).Outcome);
            Assert.Equal(GateOutcome.Ask, AgentPolicyGuard.Decide(AgentCapability.DeployLive, "uat", "agent", true, p).Outcome);
            Assert.Equal(GateOutcome.Ask, AgentPolicyGuard.Decide(AgentCapability.DeployLive, "prod", "agent", true, p).Outcome);
        }

        [Fact]
        public void The_client_preset_denies_prod_and_forbids_previewing_prod_rows()
        {
            var p = AgentPolicyPresets.Build("client");
            Assert.Equal(GateOutcome.Deny, AgentPolicyGuard.Decide(AgentCapability.DeployLive, "prod", "agent", true, p).Outcome);
            Assert.Equal(GateOutcome.Deny, AgentPolicyGuard.Decide(AgentCapability.QueryData, "uat", "agent", true, p).Outcome);
            Assert.Equal(GateOutcome.Deny, AgentPolicyGuard.Decide(AgentCapability.QueryData, "prod", "agent", true, p).Outcome);
            Assert.Equal(GateOutcome.Allow, AgentPolicyGuard.Decide(AgentCapability.QueryData, "dev", "agent", true, p).Outcome);
        }

        [Fact]
        public void Editing_the_local_copy_and_writing_a_file_are_allowed_regardless_of_label()
        {
            // These never touch a live target, so the label on them is meaningless — allowed in every preset.
            foreach (var name in AgentPolicyPresets.Names)
            {
                var p = AgentPolicyPresets.Build(name);
                Assert.Equal(GateOutcome.Allow, AgentPolicyGuard.Decide(AgentCapability.EditLocal, "prod", "agent", true, p).Outcome);
                Assert.Equal(GateOutcome.Allow, AgentPolicyGuard.Decide(AgentCapability.DeployFile, "prod", "agent", true, p).Outcome);
            }
        }

        // ---- a malformed on-disk matrix fails CLOSED ----

        [Fact]
        public void A_missing_or_malformed_matrix_cell_denies()
        {
            var p = new AgentPolicy { Enabled = true, Preset = "custom" };   // empty matrix
            Assert.Equal(GateOutcome.Deny, AgentPolicyGuard.Decide(AgentCapability.DeployLive, "dev", "agent", true, p).Outcome);
            p.Matrix["DeployLive"] = new() { ["dev"] = "banana" };            // unparseable action
            Assert.Equal(GateOutcome.Deny, AgentPolicyGuard.Decide(AgentCapability.DeployLive, "dev", "agent", true, p).Outcome);
        }

        // ---- capability map: writes never fall through to Read ----

        [Fact]
        public void Deploy_and_data_ops_map_to_gated_capabilities_reads_do_not()
        {
            Assert.Equal(AgentCapability.DeployLive, CapabilityMap.For("deploy_live"));
            Assert.Equal(AgentCapability.QueryData, CapabilityMap.For("preview_table"));
            Assert.Equal(AgentCapability.QueryData, CapabilityMap.For("run_sql"));
            Assert.Equal(AgentCapability.QueryCalc, CapabilityMap.For("run_dax"));
            Assert.Equal(AgentCapability.Governance, CapabilityMap.For("set_agent_policy"));
            Assert.Equal(AgentCapability.Read, CapabilityMap.For("get_model_summary"));
            // an unknown MUTATION floors at EditLocal, never Read — a write can't sneak through as ungated.
            Assert.Equal(AgentCapability.EditLocal, CapabilityMap.For("some_new_mutating_op", isKnownMutation: true));
            Assert.Equal(AgentCapability.Read, CapabilityMap.For("some_new_reading_op", isKnownMutation: false));
            // the generic property-setter pair is named EXPLICITLY (#150 review) — the advisory surface must not
            // depend on the isKnownMutation fallback to label the property grid's write ops.
            Assert.Equal(AgentCapability.EditLocal, CapabilityMap.For("set_property"));
            Assert.Equal(AgentCapability.EditLocal, CapabilityMap.For("set_properties"));
        }

        // ---- the store: human-only, Pro-to-configure, kill-switch free ----

        [Fact]
        public void An_agent_cannot_change_the_policy()
        {
            Assert.Throws<InvalidOperationException>(() => AgentPolicyStore.SetPreset("open", "agent", isPro: true));
            Assert.Throws<InvalidOperationException>(() => AgentPolicyStore.SetEnabled(false, "agent"));
            Assert.Throws<InvalidOperationException>(() => AgentPolicyStore.SetCell("DeployLive", "prod", "allow", "agent", isPro: true));
        }

        [Fact]
        public void Configuring_the_matrix_needs_Pro_but_the_kill_switch_is_free()
        {
            Assert.Throws<Semanticus.Engine.Entitlement.EntitlementException>(() => AgentPolicyStore.SetPreset("open", "human", isPro: false));
            Assert.Throws<Semanticus.Engine.Entitlement.EntitlementException>(() => AgentPolicyStore.SetCell("DeployLive", "prod", "allow", "human", isPro: false));
            // ...but a free user can always turn the whole guardrail off (never route around safety).
            var p = AgentPolicyStore.SetEnabled(false, "human");
            Assert.False(p.Enabled);
        }

        [Fact]
        public void The_default_policy_is_the_standard_preset_when_nothing_is_saved()
        {
            var p = AgentPolicyStore.Get();
            Assert.Equal("standard", p.Preset);
            Assert.True(p.Enabled);
        }

        [Fact]
        public void A_pro_human_can_switch_preset_and_override_a_cell()
        {
            AgentPolicyStore.SetPreset("client", "human", isPro: true);
            Assert.Equal("client", AgentPolicyStore.Get().Preset);

            AgentPolicyStore.SetCell("DeployLive", "prod", "ask", "human", isPro: true);   // loosen prod from deny to ask
            var p = AgentPolicyStore.Get();
            Assert.Equal("custom", p.Preset);   // a cell edit means it no longer matches a named preset
            Assert.Equal(GateOutcome.Ask, AgentPolicyGuard.Decide(AgentCapability.DeployLive, "prod", "agent", true, p).Outcome);
        }

        // ---- the approval ledger: the generalised MintToken ----

        [Fact]
        public void An_agent_cannot_approve_only_a_human_can()
        {
            var req = ApprovalLedger.Request(AgentCapability.DeployLive, "prod", "intent-1", "push to prod", "prod-ws");
            Assert.Throws<InvalidOperationException>(() => ApprovalLedger.Approve(req.Id, "agent"));
        }

        [Fact]
        public void An_approval_is_one_shot_and_intent_bound()
        {
            var req = ApprovalLedger.Request(AgentCapability.DeployLive, "prod", "intent-A", "push A", "prod-ws");
            Assert.False(ApprovalLedger.TryConsume(AgentCapability.DeployLive, "prod", "intent-A"));   // not yet approved

            ApprovalLedger.Approve(req.Id, "human");
            // a DIFFERENT intent is not covered by this approval
            Assert.False(ApprovalLedger.TryConsume(AgentCapability.DeployLive, "prod", "intent-B"));
            // the exact intent is — once
            Assert.True(ApprovalLedger.TryConsume(AgentCapability.DeployLive, "prod", "intent-A"));
            Assert.False(ApprovalLedger.TryConsume(AgentCapability.DeployLive, "prod", "intent-A"));   // consumed
        }

        [Fact]
        public void Re_requesting_revokes_a_prior_grant_so_a_new_ask_needs_a_new_approval()
        {
            var req = ApprovalLedger.Request(AgentCapability.DeployLive, "prod", "intent-R", "push", "ws");
            ApprovalLedger.Approve(req.Id, "human");
            ApprovalLedger.Request(AgentCapability.DeployLive, "prod", "intent-R", "push again", "ws");   // fresh ask
            Assert.False(ApprovalLedger.TryConsume(AgentCapability.DeployLive, "prod", "intent-R"));      // prior grant revoked
        }

        [Fact]
        public void Denying_removes_the_request_from_the_queue()
        {
            var req = ApprovalLedger.Request(AgentCapability.DeployLive, "uat", "intent-D", "push", "ws");
            Assert.Single(ApprovalLedger.List());
            Assert.True(ApprovalLedger.Deny(req.Id, "human"));
            Assert.Empty(ApprovalLedger.List());
        }

        [Fact]
        public void The_queue_shows_pending_requests_for_the_UI()
        {
            ApprovalLedger.Request(AgentCapability.DeployLive, "prod", "i1", "push X to prod", "prod-ws");
            ApprovalLedger.Request(AgentCapability.DeployDelete, "uat", "i2", "delete Y from uat", "uat-ws");
            var q = ApprovalLedger.List();
            Assert.Equal(2, q.Count);
            Assert.Contains(q, r => r.Summary.Contains("push X to prod"));
        }

        // ================= HARDENING (from the gpt-5.6 adversarial review) =================

        // ---- origin is FAIL-CLOSED: only an exact "human" is the authority; everything else is gated as an agent ----
        [Theory]
        [InlineData(null)]
        [InlineData("system")]
        [InlineData("human ")]   // trailing space is not "human"
        [InlineData("Agent")]
        [InlineData("")]
        public void Only_an_exact_human_origin_bypasses_the_gate(string origin)
        {
            var p = AgentPolicyPresets.Build("standard");   // prod ⇒ ask
            var d = AgentPolicyGuard.Decide(AgentCapability.DeployLive, "prod", origin, isCommit: true, p);
            Assert.NotEqual(GateOutcome.Allow, d.Outcome);   // gated, not waved through
        }

        // ---- a preview must NOT exempt QueryData: reading rows IS the action, there is no dry-run of exfiltration ----
        [Fact]
        public void A_non_commit_query_data_is_still_gated()
        {
            var p = AgentPolicyPresets.Build("client");   // QueryData on prod ⇒ deny
            var d = AgentPolicyGuard.Decide(AgentCapability.QueryData, "prod", "agent", isCommit: false, p);
            Assert.Equal(GateOutcome.Deny, d.Outcome);
        }

        // ---- Governance can never be reached through the preview exemption or the kill-switch ----
        [Fact]
        public void Governance_is_denied_for_an_agent_even_on_a_preview_or_with_the_switch_off()
        {
            var p = AgentPolicyPresets.Build("open"); p.Enabled = false;
            Assert.Equal(GateOutcome.Deny, AgentPolicyGuard.Decide(AgentCapability.Governance, "local", "agent", isCommit: false, p).Outcome);
        }

        // ---- a null / malformed policy fails CLOSED, not open ----
        [Fact]
        public void A_null_policy_denies_gated_actions()
        {
            Assert.Equal(GateOutcome.Deny, AgentPolicyGuard.Decide(AgentCapability.DeployLive, "dev", "agent", true, null).Outcome);
            var noMatrix = new AgentPolicy { Enabled = true, Matrix = null };
            Assert.Equal(GateOutcome.Deny, AgentPolicyGuard.Decide(AgentCapability.DeployLive, "dev", "agent", true, noMatrix).Outcome);
        }

        // ---- a numeric action string ("0") must NOT parse as Allow (Enum.TryParse would have) ----
        [Fact]
        public void A_numeric_action_string_is_treated_as_deny_not_allow()
        {
            var p = new AgentPolicy { Enabled = true, Matrix = { ["DeployLive"] = new() { ["dev"] = "0" } } };
            Assert.Equal(GateOutcome.Deny, AgentPolicyGuard.Decide(AgentCapability.DeployLive, "dev", "agent", true, p).Outcome);
        }

        // ---- a saved-but-unreadable policy fails CLOSED (deny live), never silently downgrades to the weaker default ----
        [Fact]
        public void An_unreadable_saved_policy_denies_all_live_actions()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "agent-policy.json"), "{ not json — a locked policy the user set");

            var p = AgentPolicyStore.Get();
            Assert.Equal("unreadable", p.Preset);
            Assert.Equal(GateOutcome.Deny, AgentPolicyGuard.Decide(AgentCapability.DeployLive, "dev", "agent", true, p).Outcome);
            Assert.Equal(GateOutcome.Deny, AgentPolicyGuard.Decide(AgentCapability.DeployDelete, "local", "agent", true, p).Outcome);
            // ...but local-only work (editing the working copy) is still permitted, so a parse error doesn't brick the agent.
            Assert.Equal(GateOutcome.Allow, AgentPolicyGuard.Decide(AgentCapability.EditLocal, "prod", "agent", true, p).Outcome);
        }

        [Fact]
        public void A_missing_policy_file_is_the_normal_default_not_fail_closed()
        {
            var p = AgentPolicyStore.Get();   // nothing saved
            Assert.Equal("standard", p.Preset);
        }

        // ---- a granted approval with a missing/unparsable expiry is NOT consumable (fail closed) ----
        [Fact]
        public void A_grant_with_a_malformed_expiry_cannot_be_consumed()
        {
            var req = ApprovalLedger.Request(AgentCapability.DeployLive, "prod", "intent-X", "push", "ws");
            ApprovalLedger.Approve(req.Id, "human");

            // Corrupt the expiry on disk (a partial write / hand-edit) via a JSON node so escaping can't foil it, then
            // attempt to consume — a grant whose expiry doesn't parse must fail closed.
            var path = Path.Combine(_root, "agent-approvals.json");
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            node["items"][0]["expiresUtc"] = "not-a-date";
            File.WriteAllText(path, node.ToJsonString());

            Assert.False(ApprovalLedger.TryConsume(AgentCapability.DeployLive, "prod", "intent-X"));
        }
    }
}
