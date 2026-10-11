namespace Semanticus.Engine.Entitlement
{
    /// <summary>The engine that holds the entitlement and can change it while it runs (F-242). Deliberately NOT on
    /// <see cref="IEngine"/>: every IEngine method reaches both doors, and activating a licence is a person's action,
    /// so the only caller is the human governance RPC target (<see cref="HumanGovernanceRpcTarget.applyLicense"/>),
    /// which an assistant connection never has registered.</summary>
    internal interface ILicenseHolder
    {
        LicenseSwapResult ApplyLicense(string token, bool verifyOnly);
    }

    /// <summary>What the engine made of a licence change. <see cref="Entitlement"/> is always the engine's answer
    /// AFTER the call, so a page shows that and never its own guess.</summary>
    public sealed class LicenseSwapResult
    {
        /// <summary>The token verified as Pro, or this was a removal. False means refused.</summary>
        public bool Accepted { get; set; }
        /// <summary>The running entitlement was replaced. False on a refusal and on a verify-only check.</summary>
        public bool Applied { get; set; }
        /// <summary>The engine's own reason for a refusal (the same teaching line get_entitlement carries).</summary>
        public string Reason { get; set; }
        public EntitlementInfo Entitlement { get; set; }
    }
}
