"use strict";
// F-242: a licence change reaches the RUNNING engine. Imports no vscode, so the rule is unit tested
// (test/licence-swap.test.mjs) with a fake engine.
//
// The engine re-verifies the token and swaps its entitlement in place through the person-only applyLicense RPC
// (Semanticus.Engine/HumanGovernanceRpcTarget.cs). Before this, the engine read the licence once at start, and a
// window that had adopted an engine it did not start could only ask for a reload, so Remove license on the Pro
// page looked like it did nothing. The restart survives only as the fallback for an engine that cannot swap.
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.swapLicenseInEngine = swapLicenseInEngine;
exports.precheckLicense = precheckLicense;
exports.removeMirroredLicenceFile = removeMirroredLicenceFile;
const fs = __importStar(require("fs"));
const os = __importStar(require("os"));
const path = __importStar(require("path"));
/// Hand the running engine the token it should now hold ('' is a removal). Any failure to reach it (no connection,
/// an engine older than the swap, a dropped pipe) runs `fallback`, which is the old restart-or-reload path.
async function swapLicenseInEngine(door, token, fallback) {
    let result;
    if (door) {
        try {
            result = await door.applyLicense(token, false);
        }
        catch {
            result = undefined;
        }
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
async function precheckLicense(door, token) {
    if (!door || !token)
        return {};
    try {
        const r = await door.applyLicense(token, true);
        return r && !r.accepted ? { refused: r.reason || 'the license did not verify' } : {};
    }
    catch {
        return {};
    }
}
/// The restart fallback for a REMOVAL: a restarted engine given no token reads the user licence file that Connect
/// another assistant wrote (SEMANTICUS_HOME, else ~/.semanticus), so it would come back on Pro. The engine does the
/// same delete itself when it can swap; this is the same step for an engine that cannot. Only a file that is there
/// is touched, and a file that cannot be removed is left (the restart then reports what it reads).
function removeMirroredLicenceFile(env = process.env, home = os.homedir()) {
    const root = (env.SEMANTICUS_HOME || '').trim() || path.join(home, '.semanticus');
    try {
        fs.rmSync(path.join(root, 'license'), { force: true });
    }
    catch { /* read as before: the next start reports the licence it holds */ }
}
//# sourceMappingURL=licenseSwap.js.map