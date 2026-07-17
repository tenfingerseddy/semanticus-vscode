import * as path from 'path';

export interface ResolvedEngine { kind: 'dll' | 'exe'; path: string; }
export interface McpServerEntry { command: string; args: string[]; }
export type EngineOwnerDecision = 'match' | 'legacy' | 'development-fallback' | 'mismatch' | 'unresolved';

interface EngineResolutionInput {
    mode: 'development' | 'production';
    overrideDll: string;
    bundledExecutable: string;
    exists: (candidate: string) => boolean;
}

/// Keep the F5 override inside the Extension Development Host. A Marketplace install is a verified pair of
/// extension + bundled engine; silently substituting a global debug DLL makes its displayed version meaningless.
export function resolveEngineCandidate(input: EngineResolutionInput): ResolvedEngine {
    if (input.mode === 'development' && input.overrideDll && input.exists(input.overrideDll))
        return { kind: 'dll', path: input.overrideDll };
    if (input.exists(input.bundledExecutable))
        return { kind: 'exe', path: input.bundledExecutable };
    if (input.mode === 'production')
        throw new Error(`The bundled Semanticus engine was not found at ${input.bundledExecutable}. Reinstall the extension.`);
    throw new Error(
        `Semanticus engine not found. Set "semanticus.engineDll" to a built Semanticus.Engine.dll, ` +
        `or reinstall the extension so ${input.bundledExecutable} is present.`);
}

function normalizedOwnerPath(value: string, platform: NodeJS.Platform): string {
    // Tests exercise every target from any host, so choose path semantics from the TARGET instead of Node's host.
    const resolved = (platform === 'win32' ? path.win32 : path.posix).resolve(value);
    return platform === 'win32' ? resolved.toLowerCase() : resolved;
}

/// The workspace rendezvous is shared across VS Code and MCP, but a live pipe is not proof that the owner is the
/// binary this host selected. Packaged owners must match the bundled apphost exactly. A dev DLL is dotnet-hosted,
/// so the broker's executable provenance can distinguish a dev owner from a packaged one (the exact DLL remains a
/// developer-controlled concern within Development mode).
export function engineOwnerMatches(
    expected: ResolvedEngine,
    recordedExecutable: string | undefined,
    platform: NodeJS.Platform,
): boolean {
    if (!recordedExecutable) return false;
    if (expected.kind === 'exe')
        return normalizedOwnerPath(expected.path, platform) === normalizedOwnerPath(recordedExecutable, platform);
    const ownerName = (platform === 'win32' ? path.win32 : path.posix).basename(recordedExecutable).toLowerCase();
    return ownerName === (platform === 'win32' ? 'dotnet.exe' : 'dotnet');
}

/// Missing provenance is a legacy state, not proof of a mismatch. Production still needs a resolvable bundled
/// candidate before it can compare; a Development Host may attach to an already-live owner when its checkout is not
/// currently buildable. The caller owns the one-time warnings for the two unverified attach decisions.
export function decideEngineOwner(
    expected: ResolvedEngine | undefined,
    recordedExecutable: string | undefined,
    mode: 'development' | 'production',
    platform: NodeJS.Platform,
): EngineOwnerDecision {
    if (!expected) return mode === 'development' ? 'development-fallback' : 'unresolved';
    if (!recordedExecutable) return 'legacy';
    return engineOwnerMatches(expected, recordedExecutable, platform) ? 'match' : 'mismatch';
}

function normalizedWorkspaceFromArgs(args: unknown, platform: NodeJS.Platform): string | undefined {
    if (!Array.isArray(args) || args.some((x) => typeof x !== 'string')) return undefined;
    const values = args as string[];
    if (values.length !== 3 && values.length !== 5) return undefined;
    if (values[0] !== 'mcp' || values[1] !== '--workspace' || !values[2]) return undefined;
    if (values.length === 5 && (values[3] !== '--license' || !values[4])) return undefined;
    const index = values.indexOf('--workspace');
    return normalizedOwnerPath(values[index + 1], platform);
}

function looksLikeGeneratedBundledEntry(value: unknown, platform: NodeJS.Platform): value is McpServerEntry {
    if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
    const candidate = value as Record<string, unknown>;
    if (Object.keys(candidate).some((key) => key !== 'command' && key !== 'args')) return false;
    if (typeof candidate.command !== 'string' || !Array.isArray(candidate.args)) return false;
    if (candidate.args[0] !== 'mcp' || normalizedWorkspaceFromArgs(candidate.args, platform) === undefined) return false;
    const paths = platform === 'win32' ? path.win32 : path.posix;
    const binary = paths.basename(candidate.command).toLowerCase();
    const parent = paths.basename(paths.dirname(candidate.command)).toLowerCase();
    return parent === 'engine' && binary === (platform === 'win32' ? 'semanticus.engine.exe' : 'semanticus.engine');
}

/// Activation may silently refresh only the entry shape that Semanticus itself wrote for a bundled apphost, only
/// when an extension update changed that apphost's absolute path, and only for the same workspace. Hand-authored,
/// DLL-hosted, cross-workspace, or augmented entries still require the explicit Connect AI Assistant confirmation.
export function shouldAutoHealMcpEntry(
    prior: unknown,
    desired: McpServerEntry,
    platform: NodeJS.Platform,
): boolean {
    if (!looksLikeGeneratedBundledEntry(prior, platform) || !looksLikeGeneratedBundledEntry(desired, platform))
        return false;
    const oldWorkspace = normalizedWorkspaceFromArgs(prior.args, platform);
    const newWorkspace = normalizedWorkspaceFromArgs(desired.args, platform);
    if (oldWorkspace !== newWorkspace) return false;
    return normalizedOwnerPath(prior.command, platform) !== normalizedOwnerPath(desired.command, platform);
}
