using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// Governance mutations exist only on an authenticated human connection. Agent connections never register this
    /// target, so they cannot discover or invoke these methods even if they forge a request origin.
    /// </summary>
    internal sealed class HumanGovernanceRpcTarget
    {
        private readonly IEngine _engine;

        internal HumanGovernanceRpcTarget(IEngine engine) => _engine = engine;

        public Task<AgentPolicy> setAgentPolicyPreset(string preset, string legacyOrigin = null) =>
            _engine.SetAgentPolicyPresetAsync(preset, "human");
        public Task<AgentPolicy> setAgentPolicyCell(string capability, string label, string action, string legacyOrigin = null) =>
            _engine.SetAgentPolicyCellAsync(capability, label, action, "human");
        public Task<AgentPolicy> setAgentPolicyEnabled(bool enabled, string legacyOrigin = null) =>
            _engine.SetAgentPolicyEnabledAsync(enabled, "human");
        public Task<ApprovalRecord> approveAgentAction(string id, string legacyOrigin = null) =>
            _engine.ApproveAgentActionAsync(id, "human");
        public Task<bool> denyAgentAction(string id, string legacyOrigin = null) =>
            _engine.DenyAgentActionAsync(id, "human");
    }
}
