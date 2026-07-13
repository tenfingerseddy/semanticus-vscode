using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>
    /// The ~10 things an agent can do that a policy gates on — NOT a list of 235 ops. A capability is the unit the
    /// user reasons about ("can the agent push to production"), and every op maps to exactly one (see
    /// <see cref="CapabilityMap"/>). An op that maps to nothing is default-denied for the agent, so the list can never
    /// silently rot into a permit-by-omission.
    ///
    /// Only the LIVE-touching capabilities carry a target label — editing the local working copy or writing a file is
    /// never a production action, however the model was opened. The label matters exactly when you reach the endpoint.
    /// </summary>
    public enum AgentCapability
    {
        Read,          // list / get / preview-metadata / dry_run / scan / model_diff — never gated
        QueryCalc,     // run a CALCULATION (scalar / measure result) — allowed everywhere, even prod
        QueryData,     // preview ROWS of source data (preview_table, run_sql, row-returning run_dax) — the exfiltration surface
        EditLocal,     // mutate the open model / save to a file — the working copy, never the live target
        DeployFile,    // write a model to disk
        DeployLive,    // push metadata to a PUBLISHED model
        DeployDelete,  // delete an object from a published model — the irreversible one
        Rollback,      // rollback_push — recovery, but still a live write
        Refresh,       // refresh a partition on the live model
        Governance,    // set policy / label a target — NEVER the agent's to do
    }

    public enum PolicyAction { Allow, Ask, Deny }

    /// <summary>A user-declared policy: the global switch, the chosen preset, and the capability×label matrix. Stored as
    /// strings so the webview renders it directly and a hand-edit can't produce an enum the parser rejects — an
    /// unknown value reads as the strictest option (see <see cref="Resolve"/>).</summary>
    public sealed class AgentPolicy
    {
        // The global kill-switch. OFF ⇒ the MATRIX stands down (gated actions allowed, honestly — no silent enforcement).
        // It does NOT lift the op-level hard safety gates that predate this policy (an agent still cannot promote to a
        // production pipeline stage, force-overwrite a workspace from git, cicd-publish a full override, or disconnect
        // git) — those are properties of those operations, not cells in this matrix.
        public bool Enabled { get; set; } = true;
        public string Preset { get; set; } = "standard";
        // capability name -> (label -> action name). Labels: local | dev | uat | prod.
        public Dictionary<string, Dictionary<string, string>> Matrix { get; set; } = new();

        /// <summary>The action for a capability at a label. Fail CLOSED on anything unrecognised: an unknown capability,
        /// an unknown label, or a hand-edited action string that isn't allow/ask/deny all resolve to <see
        /// cref="PolicyAction.Deny"/> for a gated capability. Read and QueryCalc are structurally ungated and return
        /// Allow regardless of the matrix — a policy can never accidentally lock a user out of reading their own model.</summary>
        public PolicyAction Resolve(AgentCapability cap, string label)
        {
            if (cap == AgentCapability.Read || cap == AgentCapability.QueryCalc) return PolicyAction.Allow;
            if (cap == AgentCapability.Governance) return PolicyAction.Deny;   // never, in any preset

            var lbl = NormalizeLabel(label);
            if (Matrix != null && Matrix.TryGetValue(cap.ToString(), out var row) && row != null
                && row.TryGetValue(lbl, out var action) && TryParseAction(action, out var parsed))
                return parsed;
            return PolicyAction.Deny;   // absent / null / malformed ⇒ strictest
        }

        // Parse ONLY the three explicit words — never Enum.TryParse, which also accepts numeric text ("0" ⇒ Allow), so
        // a hand-edited or corrupt "0" in the file would silently permit the agent.
        internal static bool TryParseAction(string s, out PolicyAction action)
        {
            switch (s?.Trim().ToLowerInvariant())
            {
                case "allow": action = PolicyAction.Allow; return true;
                case "ask": action = PolicyAction.Ask; return true;
                case "deny": action = PolicyAction.Deny; return true;
                default: action = PolicyAction.Deny; return false;
            }
        }

        // A label is one of the four we understand; anything else (including unlabelled, which arrives here as "prod"
        // from ConnectionRegistry.EffectiveLabel) is treated as prod — the strictest ROW. The registry already did the
        // unlabelled→prod mapping; this is the belt-and-braces so a bad string can't dodge the strict row.
        internal static string NormalizeLabel(string label)
        {
            var l = label?.Trim().ToLowerInvariant();
            return l == "local" || l == "dev" || l == "uat" ? l : "prod";
        }

        public AgentPolicy Clone() => new()
        {
            Enabled = Enabled,
            Preset = Preset,
            Matrix = Matrix.ToDictionary(k => k.Key, v => new Dictionary<string, string>(v.Value)),
        };
    }

    /// <summary>The presets, strictness ascending. A preset is just a fully-populated matrix; the user picks one, then
    /// (Pro) may tweak individual cells. read + QueryCalc are ungated in every preset; Governance is deny-agent in
    /// every preset. The gradient lives entirely in the LIVE-touching capabilities.</summary>
    public static class AgentPolicyPresets
    {
        public const string Default = "standard";

        private static readonly string[] Labels = { "local", "dev", "uat", "prod" };

        // The capabilities a preset actually sets (the gated, label-bearing ones). The ungated ones are handled in Resolve.
        private static readonly AgentCapability[] Gated =
        {
            AgentCapability.QueryData, AgentCapability.EditLocal, AgentCapability.DeployFile,
            AgentCapability.DeployLive, AgentCapability.DeployDelete, AgentCapability.Rollback, AgentCapability.Refresh,
        };

        public static IReadOnlyList<string> Names => new[] { "open", "standard", "cautious", "client", "locked" };

        public static AgentPolicy Build(string preset)
        {
            var p = (preset ?? Default).Trim().ToLowerInvariant();
            var m = new Dictionary<string, Dictionary<string, string>>();
            foreach (var cap in Gated)
                m[cap.ToString()] = Labels.ToDictionary(l => l, l => Cell(p, cap, l).ToString().ToLowerInvariant());
            return new AgentPolicy { Enabled = true, Preset = Names.Contains(p) ? p : Default, Matrix = m };
        }

        /// <summary>The policy to use when a SAVED policy file exists but cannot be read/parsed — an error state, so it
        /// fails CLOSED: every live-touching capability is denied at every label until the human repairs it. Local-only
        /// work (editing the working copy, writing a file) is still permitted, so a parse error doesn't brick the agent
        /// entirely. Preset name "unreadable" tells the UI to surface a repair prompt.</summary>
        public static AgentPolicy FailClosed()
        {
            var m = new Dictionary<string, Dictionary<string, string>>();
            foreach (var cap in Gated)
            {
                var deny = cap != AgentCapability.EditLocal && cap != AgentCapability.DeployFile;   // live ⇒ deny; local ⇒ allow
                m[cap.ToString()] = Labels.ToDictionary(l => l, _ => deny ? "deny" : "allow");
            }
            return new AgentPolicy { Enabled = true, Preset = "unreadable", Matrix = m };
        }

        // EditLocal and DeployFile never touch a live target, so they are Allow at every label in every preset — the
        // label on those is meaningless (you are editing a working copy / writing a file). Only the endpoint-reaching
        // capabilities tighten with the label and the preset.
        private static PolicyAction Cell(string preset, AgentCapability cap, string label)
        {
            if (cap == AgentCapability.EditLocal || cap == AgentCapability.DeployFile) return PolicyAction.Allow;

            // Rank the label 0..3 so a preset is a threshold, not a table of 28 hand-typed values that can drift.
            var lvl = label switch { "local" => 0, "dev" => 1, "uat" => 2, _ => 3 };

            return preset switch
            {
                // Zero friction — a power user who wants the agent unrestricted, even on prod.
                "open" => PolicyAction.Allow,

                // The DEFAULT and the Free posture: nothing is hard-blocked, so it "just works", but a live touch on a
                // labelled uat/prod (or an unlabelled target, which resolves to prod) asks for a one-time approval.
                "standard" => lvl <= 1 ? PolicyAction.Allow : PolicyAction.Ask,

                // Ask earlier — from dev up.
                "cautious" => lvl == 0 ? PolicyAction.Allow : PolicyAction.Ask,

                // Client/team: prod is a wall; data preview off uat+prod (Kane's split — never preview prod rows).
                "client" => cap == AgentCapability.QueryData
                    ? (lvl <= 1 ? PolicyAction.Allow : PolicyAction.Deny)
                    : (lvl <= 0 ? PolicyAction.Allow : lvl == 3 ? PolicyAction.Deny : PolicyAction.Ask),

                // Enterprise/locked: maximum separation of duties. Data preview local-only; every live write to uat/prod
                // denied; even local/dev writes ask.
                "locked" => cap == AgentCapability.QueryData
                    ? (lvl == 0 ? PolicyAction.Allow : PolicyAction.Deny)
                    : (lvl <= 1 ? PolicyAction.Ask : PolicyAction.Deny),

                _ => lvl <= 1 ? PolicyAction.Allow : PolicyAction.Ask,   // unknown preset ⇒ standard
            };
        }
    }
}
