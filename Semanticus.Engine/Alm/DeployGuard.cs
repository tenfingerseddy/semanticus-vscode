using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Semanticus.Engine
{
    /// <summary>
    /// The deploy-to-stage safety decision, factored out as a PURE function so the whole gate matrix is unit-testable
    /// offline (no tenant, no HTTP). Two orthogonal protections:
    ///  • A PRODUCTION target needs a <see cref="MintToken"/> confirm token that is bound to the exact (pipeline,
    ///    source→target, item-set) intent. The token is surfaced ONLY by the human door's dry-run (the engine
    ///    withholds it from the agent door), AND an agent can never commit a prod promotion — so an agent literally
    ///    cannot satisfy a prod deploy alone; a human must read the token from the preview and confirm.
    ///  • A failing readiness gate (BPA + AI-readiness) blocks any commit unless <c>forceOverride</c> — which is
    ///    an ACCOUNTABLE override, not a flag: it must carry an overrideReason (recorded in the model's audit
    ///    trail by the caller). An unexplained override is a silent suppression; a reasoned one is a decision.
    /// </summary>
    internal static class DeployGuard
    {
        // FAIL CLOSED, mirroring AgentPolicyGuard.IsHuman: anything that is not an exact "human" is treated as an
        // agent — null, "system", a typo, a door that forgot to declare itself. The old shape ("agent" ⇒ agent, all
        // else ⇒ human) meant a caller that FORGOT origin inherited human authority on the prod-stage / force-override
        // / publish / disconnect gates. Every legitimate human caller already passes "human" (the RPC door defaults it
        // on all 147 origin parameters), so only the unsafe cases change outcome.
        internal static bool IsAgent(string origin) => !AgentPolicyGuard.IsHuman(origin);

        // Whether the prod confirm token may be surfaced to THIS caller's dry-run. Only a production target, and only
        // the human door — the agent door never receives it. Factored out so a regression (e.g. && → ||) fails a test.
        internal static bool SurfaceConfirmToken(bool targetIsProd, string origin) => targetIsProd && !IsAgent(origin);

        // A deterministic-but-unguessable token bound to the deploy intent + a per-process secret. Re-derivable
        // server-side from the same inputs (no stored state), so a token minted for a smaller item-set can't be
        // replayed to push a larger change to prod.
        internal static string MintToken(string pipelineId, string sourceStageId, string targetStageId, IEnumerable<string> itemIds, string secret)
        {
            var ids = string.Join(",", (itemIds ?? Enumerable.Empty<string>()).Where(s => !string.IsNullOrEmpty(s)).OrderBy(s => s, StringComparer.Ordinal));
            var basis = $"{pipelineId}|{sourceStageId}|{targetStageId}|{ids}|{secret}";
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(basis));
            return "PROD-" + Convert.ToHexString(h).Substring(0, 10);
        }

        /// <summary>Returns null when the deploy is allowed to proceed, else the (human-readable) refusal reason.</summary>
        internal static string Refusal(bool targetIsProd, bool commit, string origin, string confirmToken, string expectedToken, bool gatePass, bool forceOverride, bool hasOverrideReason)
        {
            if (!commit) return null;   // a dry-run is a preview — never blocked
            if (targetIsProd && IsAgent(origin))
                return "An agent cannot promote to a production stage. A human must confirm this deploy from the Deploy tab.";
            if (targetIsProd && !string.Equals(confirmToken, expectedToken, StringComparison.Ordinal))
                return "A production deploy needs the confirmToken shown by the dry-run preview. Run the preview, then deploy with that exact token.";
            if (!gatePass && !forceOverride)
                return "Deploy is blocked by the readiness gate (BPA / AI-readiness). Pass forceOverride=true AND an overrideReason to deploy anyway (the override is recorded).";
            if (!gatePass && forceOverride && !hasOverrideReason)
                return "forceOverride needs an overrideReason — say why you're shipping past the gate (it is recorded in the audit trail).";
            return null;
        }
    }
}
