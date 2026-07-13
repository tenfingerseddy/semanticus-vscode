using System;

namespace Semanticus.Engine
{
    public enum GateOutcome { Allow, Ask, Deny }

    public readonly struct PolicyDecision
    {
        public GateOutcome Outcome { get; }
        public string Reason { get; }        // human-readable, for Ask + Deny; null on Allow
        public AgentCapability Capability { get; }
        public string Label { get; }
        public PolicyDecision(GateOutcome o, AgentCapability cap, string label, string reason)
        { Outcome = o; Capability = cap; Label = label; Reason = reason; }
    }

    /// <summary>
    /// The agent-permissions decision, factored out as a PURE function so the whole matrix is unit-testable offline —
    /// the same shape as <see cref="DeployGuard"/>, generalised from "is this a prod pipeline stage" to "what has the
    /// user declared this target to be". It sits at ONE engine-side chokepoint, a sibling to the entitlement and deploy
    /// guards, because ops are reachable from both doors AND from dry_run — a guard in the MCP tool wrapper is
    /// walk-aroundable.
    ///
    /// Precedence, top to bottom (mirrors the workflow-enforcement toggle):
    ///   1. HUMAN origin           — never gated. The matrix restrains the AGENT; the human is the authority who set it.
    ///   2. a preview (not commit) — never gated. Previewing is what makes the policy livable; it changes nothing.
    ///   3. global kill-switch OFF — everything allowed, and the caller says so honestly.
    ///   4. capability × label     — the matrix.
    /// </summary>
    public static class AgentPolicyGuard
    {
        // FAIL CLOSED on origin: only an explicit, exact "human" is the authority. agent / system / null / "human "
        // (trailing space) / any unknown string is treated as an agent and gated. A door that forgets to declare a
        // human origin therefore gets the SAFE outcome, not a bypass. (The doors already set this: MCP hardcodes
        // "agent", RPC/UI passes "human" — there is no third caller, so this only ever hardens.)
        public static bool IsHuman(string origin) => string.Equals(origin, "human", StringComparison.OrdinalIgnoreCase);

        public static PolicyDecision Decide(AgentCapability cap, string label, string origin, bool isCommit, AgentPolicy policy)
        {
            var lbl = AgentPolicy.NormalizeLabel(label);

            // 1. The human is the authority — the matrix restrains the AGENT. Only an exact human origin bypasses.
            if (IsHuman(origin)) return Allow(cap, lbl);

            // 2. Governance is a HARD rule for a non-human caller — checked BEFORE any exemption, so neither a preview
            //    flag nor the kill-switch can ever let an agent change the policy/labels that gate it.
            if (cap == AgentCapability.Governance) return Deny(cap, lbl, GovernanceReason);

            // 3. Reading and running a calculation are structurally ungated — allowed for the agent everywhere.
            if (cap == AgentCapability.Read || cap == AgentCapability.QueryCalc) return Allow(cap, lbl);

            // 4. The preview exemption applies ONLY to capabilities that HAVE a dry-run (writes/deploys): previewing a
            //    deploy changes nothing. It must NOT exempt QueryData — reading rows IS the action, there is no "dry
            //    run" of exfiltration — so a non-commit QueryData is still gated.
            if (!isCommit && cap != AgentCapability.QueryData) return Allow(cap, lbl);

            // 5. Kill-switch. A null/malformed policy is NOT "disabled" — it is an error state and fails CLOSED (deny).
            if (policy == null) return Deny(cap, lbl, DenyReason(cap, lbl));
            if (!policy.Enabled) return new PolicyDecision(GateOutcome.Allow, cap, lbl, null);

            // 6. The matrix.
            return policy.Resolve(cap, lbl) switch
            {
                PolicyAction.Allow => Allow(cap, lbl),
                PolicyAction.Ask => new PolicyDecision(GateOutcome.Ask, cap, lbl, AskReason(cap, lbl)),
                _ => Deny(cap, lbl, DenyReason(cap, lbl)),
            };
        }

        private static PolicyDecision Allow(AgentCapability cap, string lbl) => new(GateOutcome.Allow, cap, lbl, null);
        private static PolicyDecision Deny(AgentCapability cap, string lbl, string reason) => new(GateOutcome.Deny, cap, lbl, reason);

        private const string GovernanceReason =
            "An agent cannot change governance settings — the policy and the target labels it gates on are the user's to set.";

        private static string AskReason(AgentCapability cap, string label) =>
            $"This action ({Describe(cap)} on a '{label}' target) needs the user's approval. It has been added to the approvals queue — ask the user to approve it, then retry.";

        private static string DenyReason(AgentCapability cap, string label) => cap == AgentCapability.Governance
            ? GovernanceReason
            : $"The agent policy does not permit {Describe(cap)} on a '{label}' target. "
              + (label == "prod"
                 ? "This target is labelled production (or is unlabelled, which is treated as production). A human can do this directly, or relabel the target if that classification is wrong."
                 : "A human can do this directly, or loosen the policy for this environment.");

        private static string Describe(AgentCapability cap) => cap switch
        {
            AgentCapability.QueryData => "previewing rows of data",
            AgentCapability.DeployLive => "deploying to the live model",
            AgentCapability.DeployDelete => "deleting an object from the live model",
            AgentCapability.Rollback => "rolling back the live model",
            AgentCapability.Refresh => "refreshing the live model",
            AgentCapability.DeployFile => "writing the model to a file",
            AgentCapability.EditLocal => "editing the model",
            AgentCapability.Governance => "changing governance settings",
            _ => cap.ToString(),
        };
    }
}
