// F-242: a licence change reaches the RUNNING engine. Imports no vscode, so the rule is unit tested
// (test/licence-swap.test.mjs) with a fake engine.
//
// The engine re-verifies the token and swaps its entitlement in place through the person-only applyLicense RPC
// (Semanticus.Engine/HumanGovernanceRpcTarget.cs). Before this, the engine read the licence once at start, and a
// window that had adopted an engine it did not start could only ask for a reload, so Remove license on the Pro
// page looked like it did nothing. The restart survives only as the fallback for an engine that cannot swap.

/// The engine's entitlement as the wire carries it (camelCased EntitlementInfo).
export interface SwapEntitlement { tier?: string; licensedTo?: string; expiry?: number; reason?: string; features?: string[]; }
/// Mirrors Semanticus.Engine.Entitlement.LicenseSwapResult.
export interface LicenseSwapResult { accepted: boolean; applied: boolean; reason?: string; entitlement?: SwapEntitlement; }
/// The one call this needs from a connection: the human door's applyLicense.
export interface LicenseSwapDoor { applyLicense(token: string, verifyOnly: boolean): Promise<LicenseSwapResult>; }

export interface SwapOutcome {
    /// 'swap': the running engine answered. 'fallback': it could not, and the restart path ran instead.
    via: 'swap' | 'fallback';
    /// The engine's own answer after the swap, for the page to show. Absent after a fallback, where the caller
    /// reads get_entitlement from whichever engine the fallback leaves running.
    entitlement?: SwapEntitlement;
    /// The engine's reason when it refused a token. Nothing changed.
    refused?: string;
}

/// Hand the running engine the token it should now hold ('' is a removal). Any failure to reach it (no connection,
/// an engine older than the swap, a dropped pipe) runs `fallback`, which is the old restart-or-reload path.
export async function swapLicenseInEngine(
    door: LicenseSwapDoor | undefined, token: string, fallback: () => Promise<void>,
): Promise<SwapOutcome> {
    let result: LicenseSwapResult | undefined;
    if (door) {
        try { result = await door.applyLicense(token, false); }
        catch { result = undefined; }
    }
    if (!result) {
        await fallback();
        return { via: 'fallback' };
    }
    return result.accepted
        ? { via: 'swap', entitlement: result.entitlement }
        : { via: 'swap', entitlement: result.entitlement, refused: result.reason || 'the license did not verify' };
}

/// Ask the engine whether a pasted key would verify, WITHOUT swapping, so a bad key is refused before anything is
/// stored. An engine that cannot answer has no opinion (refused stays undefined) and the old path carries on:
/// store, then the restart reports the verdict.
export async function precheckLicense(door: LicenseSwapDoor | undefined, token: string): Promise<{ refused?: string }> {
    if (!door || !token) return {};
    try {
        const r = await door.applyLicense(token, true);
        return r && !r.accepted ? { refused: r.reason || 'the license did not verify' } : {};
    } catch {
        return {};
    }
}
