using System;
using System.IO;

namespace Semanticus.Engine
{
    /// <summary>
    /// Persists the one agent policy at <c>~/.semanticus/agent-policy.json</c>. Free vs Pro (Kane's line): the whole
    /// GUARDRAIL is free — the default policy protects everyone and the global Off switch is always available — but
    /// CONFIGURING it (choosing a preset, editing a cell) is Pro. A Free user gets the default preset, read-only.
    /// Changing the policy is refused from the agent door in every case: an agent that can rewrite the matrix that
    /// gates it is not gated.
    /// </summary>
    public static class AgentPolicyStore
    {
        private const string FileName = "agent-policy.json";

        /// <summary>Test seam.</summary>
        public static string RootOverride { get; set; }

        private static string Path_() => Path.Combine(HomeFile.Root(RootOverride), FileName);

        /// <summary>The active policy. NO saved file ⇒ the default preset (the normal case). A saved file that exists
        /// but cannot be read/parsed ⇒ FAIL CLOSED (deny all live actions), never the weaker default — a transient read
        /// error or a corrupt file must not silently downgrade a strict policy the user set. That includes a file whose
        /// content is the JSON literal <c>null</c>: it deserializes without throwing, but it is still saved state we
        /// could not use — an error state, not "no file". Free, both doors.</summary>
        public static AgentPolicy Get()
        {
            var path = Path_();
            if (!HomeFile.Exists(path)) return AgentPolicyPresets.Build(AgentPolicyPresets.Default);
            try { return HomeFile.ReadStrict<AgentPolicy>(path) ?? AgentPolicyPresets.FailClosed(); }
            catch { return AgentPolicyPresets.FailClosed(); }
        }

        // Exact "human" only — an unknown/whitespace/agent origin is refused (fail closed), matching the guard.
        private static void RequireHuman(string origin)
        {
            if (!string.Equals(origin, "human", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only a human can change the agent policy — an agent cannot rewrite the matrix that gates it. This must come from the UI.");
        }

        private static void RequirePro(bool isPro)
            => Entitlement.EntitlementGuard.RequirePro(isPro, "Customising the agent policy",
                "The default guardrail (and the global Off switch) are free; upgrade to choose a preset or edit the matrix.");

        /// <summary>Switch to a named preset. Human + Pro.</summary>
        public static AgentPolicy SetPreset(string preset, string origin, bool isPro)
        {
            RequireHuman(origin); RequirePro(isPro);
            var built = AgentPolicyPresets.Build(preset);
            return HomeFile.Mutate<AgentPolicy, AgentPolicy>(Path_(), Get, cur =>
            {
                cur.Preset = built.Preset;
                cur.Matrix = built.Matrix;
                // Enabled is intentionally NOT reset by a preset change — the kill-switch is orthogonal to the matrix.
                return cur;
            });
        }

        /// <summary>Override a single cell. Human + Pro. A cell change makes the preset "custom" so the UI stops
        /// claiming a named preset that no longer describes the matrix.</summary>
        public static AgentPolicy SetCell(string capability, string label, string action, string origin, bool isPro)
        {
            RequireHuman(origin); RequirePro(isPro);
            if (!Enum.TryParse<AgentCapability>(capability, ignoreCase: true, out var cap))
                throw new ArgumentException($"Unknown capability '{capability}'.");
            if (!AgentPolicy.TryParseAction(action, out _))   // explicit allow|ask|deny — never numeric "0"
                throw new ArgumentException($"Unknown action '{action}' — use allow, ask, or deny.");
            var lbl = AgentPolicy.NormalizeLabel(label);

            return HomeFile.Mutate<AgentPolicy, AgentPolicy>(Path_(), Get, cur =>
            {
                if (!cur.Matrix.TryGetValue(cap.ToString(), out var row))
                    cur.Matrix[cap.ToString()] = row = new System.Collections.Generic.Dictionary<string, string>();
                row[lbl] = action.Trim().ToLowerInvariant();
                cur.Preset = "custom";
                return cur;
            });
        }

        /// <summary>Flip the global kill-switch. Human, but FREE — safety's off-switch is never paywalled, and a user
        /// who finds the guardrail in their way must always be able to turn it off honestly rather than route around it.</summary>
        public static AgentPolicy SetEnabled(bool enabled, string origin)
        {
            RequireHuman(origin);
            return HomeFile.Mutate<AgentPolicy, AgentPolicy>(Path_(), Get, cur => { cur.Enabled = enabled; return cur; });
        }
    }
}
