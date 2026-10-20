import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import { argsContainLicenseToken } from './licenseDelivery';
import { isLauncherCommand } from './mcpLauncher';

export interface ResolvedEngine { kind: 'dll' | 'exe'; path: string; }
export interface McpServerEntry { command: string; args: string[]; }
export type EngineOwnerDecision = 'match' | 'legacy' | 'development-fallback' | 'mismatch' | 'unresolved';
export type StaleEngineVerdict = 'keep' | 'stop-stale' | 'leave-foreign';

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

/// The extension install folder a bundled engine belongs to. Every packaged engine lives at
/// <extensions>/<publisher>.<name>-<version>/engine/Semanticus.Engine[.exe], so the grandparent of the
/// executable IS the version folder. Undefined for anything that is not that shape: a development owner
/// records dotnet, which belongs to no extension and must never be judged by this rule.
export function engineExtensionFolder(executable: string | undefined, platform: NodeJS.Platform): string | undefined {
    if (!executable) return undefined;
    const paths = platform === 'win32' ? path.win32 : path.posix;
    const binary = paths.basename(executable).toLowerCase();
    if (binary !== 'semanticus.engine' && binary !== 'semanticus.engine.exe') return undefined;
    const engineDir = paths.dirname(executable);
    if (paths.basename(engineDir).toLowerCase() !== 'engine') return undefined;
    const folder = paths.dirname(engineDir);
    return folder && folder !== engineDir ? folder : undefined;
}

export interface StaleEngineInput {
    /// ExePath from engine.json.
    recordedExecutable?: string;
    /// The install folder of the extension doing the sweeping.
    extensionPath?: string;
    /// ParentPid from engine.json. Absent on any engine built before the parent watch existed.
    recordedParentPid?: number;
    /// Is the process named by recordedParentPid still running? Ignored when there is no recorded parent.
    recordedParentAlive?: boolean;
    /// Is the engine process itself still running?
    enginePidAlive: boolean;
    /// StartedBy from engine.json: 'assistant' when the assistant's connection started this owner itself.
    recordedStartedBy?: string;
    platform: NodeJS.Platform;
}

/// Should the sweep stop the engine this record describes?
///
/// A DIFFERENT install folder is evidence of a version mismatch, NOT of an orphan. VS Code installs each
/// version in its own folder and can update one while older windows stay open, so an engine from 1.2.0
/// answering on a machine now running 1.2.1 may still be serving a window with unsaved work. Stopping it on
/// the folder alone discarded that work (Codex P1 on PR 386). The folder says WHICH engine this is; the
/// recorded parent says whether anyone still owns it, and only both together justify stopping one.
///
///   keep           the folder matches, or there is no bundled-engine folder to judge (a dev dotnet owner).
///   stop-stale     nobody owns it: the engine pid is gone, or its recorded parent is gone.
///   leave-foreign  a different version whose window is still open, or an older record that cannot prove it
///                  either way. Left running, and the caller must not start anything over the top of it.
///
/// An engine whose recorded parent is dead is inside its own eight-second grace (ParentLifetime) or already
/// leaving. It cannot be rescued by adoption, because the only window that would come back is this newer
/// version and adoption never crosses install folders, so the grace can be cut short rather than waited out.
///
/// An OLD-FORMAT record (no recordedParentPid at all) comes from a build with no parent watch and therefore
/// no grace to consult. The only evidence left is the engine pid: gone means stale, answering means we
/// cannot prove it is unowned, so it is left alone. That is deliberately conservative, and it means an
/// orphan from before this change is never swept automatically. That is the right way round: failing to
/// reap a leftover costs memory, stopping a live session costs someone's work.
export function decideStaleEngine(input: StaleEngineInput): StaleEngineVerdict {
    const recorded = engineExtensionFolder(input.recordedExecutable, input.platform);
    if (!recorded || !input.extensionPath) return 'keep';
    if (normalizedOwnerPath(recorded, input.platform) === normalizedOwnerPath(input.extensionPath, input.platform))
        return 'keep';
    if (!input.enginePidAlive) return 'stop-stale';
    // An owner the assistant started (VS Code was closed) records the assistant's connection as its parent, which
    // stays alive, so the parent rule would read it as an open window and lock the updated window out of the
    // folder. No window is using it, and the old connection tells the assistant to reconnect once it is gone.
    if (input.recordedStartedBy === 'assistant') return 'stop-stale';
    if (input.recordedParentPid === undefined) return 'leave-foreign';
    return input.recordedParentAlive ? 'leave-foreign' : 'stop-stale';
}

/// What VS Code says, and does, when an owner it would stop still holds the assistant's unsaved work.
/// The older-engine line carries a Save button (F-239): after an update the assistant's new connection cannot reach
/// the older owner, so "ask it to save" was a dead end. Save goes through that owner's own agent door instead.
export const ASSISTANT_UNSAVED_OLDER = 'Your assistant has unsaved changes in an older engine. Save them and this window will carry on.';
export const OLDER_OWNER_SAVE = 'Save';
export const OLDER_OWNER_ACTIONS: readonly string[] = [OLDER_OWNER_SAVE];
export const ASSISTANT_UNSAVED_SAME = 'Your assistant has unsaved changes in this folder. Ask it to save them, then reload the window.';

/// May VS Code stop this owner? Only on a definite "no unsaved changes". An owner that has them, or that could not
/// be asked, keeps running: stopping it would throw the person's work away in silence, and waiting costs nothing.
export function decideOwnerStop(hasUnsavedChanges: boolean | undefined): 'stop' | 'leave-running' {
    return hasUnsavedChanges === false ? 'stop' : 'leave-running';
}

/// The older owner as the Save button sees it: its agent door's save, and nothing else.
export interface OlderOwnerDoor { save(): Promise<unknown>; }
export interface OlderOwnerSaveOutcome { saved: boolean; line: string; }
/// Whose changes the lines name and what happens after a save. The F-239 wording is the default; an older BUILD
/// kept for unsaved work (F-241) holds whoever's changes they are, and after the save the window switches build.
export interface OlderOwnerSaveCopy { whose: string; after: string; }
export const ASSISTANT_SAVE_COPY: OlderOwnerSaveCopy = { whose: "your assistant's changes", after: 'This window is taking over the folder.' };
export const OLDER_BUILD_SAVE_COPY: OlderOwnerSaveCopy = { whose: 'your changes', after: 'Semanticus is switching to the new engine.' };

/// Save the older owner's model through its agent door, then carry on the usual way. The usual way asks the owner
/// again and stops it only on a clean answer, so a save that did not take can never lose work. A failed or silent
/// save keeps the owner running and says why in one line.
export async function saveOlderOwnerThenProceed(
    owner: OlderOwnerDoor, proceed: () => Promise<void>, timeoutMs = 60_000, copy: OlderOwnerSaveCopy = ASSISTANT_SAVE_COPY,
): Promise<OlderOwnerSaveOutcome> {
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
        await Promise.race([
            owner.save(),
            new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('the older engine did not answer in time')), timeoutMs); }),
        ]);
    } catch (e: any) {
        const first = String(e?.message ?? e ?? '').split(/\r?\n/)[0].trim().replace(/\.+$/, '');
        const why = first || 'the older engine did not say why';
        return { saved: false, line: `Could not save ${copy.whose}: ${why}. The older engine keeps running.` };
    } finally {
        if (timer) clearTimeout(timer);
    }
    await proceed();
    return { saved: true, line: `Saved ${copy.whose}. ${copy.after}` };
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
    // The stable launcher (POSIX) is a Semanticus-written shape too, so an entry can move onto it and stay there.
    if (platform !== 'win32' && isLauncherCommand(candidate.command)) return true;
    const paths = platform === 'win32' ? path.win32 : path.posix;
    const binary = paths.basename(candidate.command).toLowerCase();
    const parent = paths.basename(paths.dirname(candidate.command)).toLowerCase();
    return parent === 'engine' && binary === (platform === 'win32' ? 'semanticus.engine.exe' : 'semanticus.engine');
}

/// Activation may silently refresh only the entry shape that Semanticus itself wrote (a bundled apphost, or the
/// stable launcher on POSIX), only when its command changed (an extension update moved the apphost, or the entry
/// moves onto the launcher), and only for the same workspace. Hand-authored,
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
    const commandChanged = normalizedOwnerPath(prior.command, platform) !== normalizedOwnerPath(desired.command, platform);
    const licenseStripped = argsContainLicenseToken(prior.args) && !argsContainLicenseToken(desired.args);
    return commandChanged || licenseStripped;
}

/// Which BUILD sits in an engine folder (F-241). An in-place reinstall keeps the version and the executable path
/// and changes only the bytes, so the path alone let a new window adopt the engine the previous build had started.
/// This is the SAME recipe the engine records in engine.json and answers over `buildIdentity`
/// (Semanticus.Engine/BuildIdentity.cs), and both sides pin one test vector: every Semanticus.*.dll in the folder,
/// lower-cased name, sorted ordinally, as "name\nsha256(bytes)\n", then sha256 of that text. Undefined when the
/// folder holds nothing of ours or cannot be read, which never matches anything.
export function engineBuildIdentity(directory: string): string | undefined {
    try {
        const names = fs.readdirSync(directory)
            .map((name) => ({ name: name.toLowerCase(), file: path.join(directory, name) }))
            .filter((f) => f.name.startsWith('semanticus.') && f.name.endsWith('.dll'))
            .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
        if (names.length === 0) return undefined;
        let text = '';
        for (const f of names) text += `${f.name}\n${crypto.createHash('sha256').update(fs.readFileSync(f.file)).digest('hex')}\n`;
        return crypto.createHash('sha256').update(text, 'utf8').digest('hex');
    } catch {
        return undefined;
    }
}

/// What a window does with a running engine whose executable already matched:
///   adopt             it is this window's build (or the build cannot be judged, or this window just started it);
///   replace           a different build with no unsaved work: stop it and start this build;
///   keep-for-unsaved  a different build that has unsaved work, or could not be asked. It keeps running, and the
///                     caller adopts it anyway so its grace cannot run out under the person, then offers Save.
///                     Losing someone's work is worse than running old code until they save.
export type BuildVerdict = 'adopt' | 'replace' | 'keep-for-unsaved';
export interface RunningEngineDoor {
    /// The running engine's answer. Undefined or '' from an engine older than the build identity.
    build(): Promise<string | undefined>;
    hasUnsavedChanges(): Promise<boolean | undefined>;
}
export interface BuildSettlement { verdict: BuildVerdict; reported?: string; unsaved?: boolean; }

export async function settleRunningBuild(
    expected: string | undefined, running: RunningEngineDoor, spawnedByThisWindow: boolean,
): Promise<BuildSettlement> {
    // Nothing to compare against: attach as every build before this one did.
    if (!expected) return { verdict: 'adopt' };
    const reported = (await running.build()) || undefined;
    if (reported === expected) return { verdict: 'adopt', reported };
    // Never replace the engine this window just started. If the two recipes ever disagreed, replacing it would
    // start the same build again and loop; the caller logs the disagreement instead.
    if (spawnedByThisWindow) return { verdict: 'adopt', reported };
    const unsaved = await running.hasUnsavedChanges();
    return { verdict: decideOwnerStop(unsaved) === 'stop' ? 'replace' : 'keep-for-unsaved', reported, unsaved };
}

/// F-244 (Sol 6, slice 5 review). Replacing a different build used to recheck nothing and kill whatever pid
/// engine.json named at that moment, and another window can have replaced the checked engine by then, with unsaved
/// work of its own. The rule now: the engine that was CHECKED is asked, over the connection it was checked on, to
/// stop itself, and it agrees only while it is clean (the engine's stopIfClean does the check and the stop as one
/// step). Nothing is killed from outside.
///
/// An engine from before that method cannot be asked. It is stopped only if it is still clean when asked again on
/// the same connection AND the record still names the very owner that was checked, by pid and start time. A record
/// naming anyone else means the folder changed hands, and nothing is stopped. Honest limit: on such an older
/// engine, an edit landing in the moment between the second check and the stop is not covered; it is covered on
/// every engine from this build on.
export interface StopIfCleanAnswer { stopping: boolean; pid?: number; reason?: string; }
export interface CheckedOwnerIdentity { pid?: number; startedUtc?: string; }
export interface ReplaceOwnerDoor {
    /// The checked engine's own answer. 'unsupported' from an engine older than the method; undefined when it could
    /// not answer in time, which is never read as clean.
    stopIfClean(): Promise<StopIfCleanAnswer | 'unsupported' | undefined>;
    /// The same connection, asked again. Only used for an engine too old to stop itself.
    hasUnsavedChanges(): Promise<boolean | undefined>;
    /// engine.json as it reads now. Compared against the checked owner, never used as a pid to stop.
    currentRecord(): CheckedOwnerIdentity | undefined;
    kill(pid: number): void;
}
export type ReplaceOutcome =
    | { kind: 'stopping'; pid?: number }
    | { kind: 'kept'; unsaved?: boolean }
    | { kind: 'changed-hands' };

export async function replaceCheckedOwner(checked: CheckedOwnerIdentity | undefined, door: ReplaceOwnerDoor): Promise<ReplaceOutcome> {
    const answer = await door.stopIfClean();
    if (answer === undefined) return { kind: 'kept', unsaved: undefined };
    if (answer !== 'unsupported') return answer.stopping ? { kind: 'stopping', pid: answer.pid } : { kind: 'kept', unsaved: true };
    const unsaved = await door.hasUnsavedChanges();
    if (decideOwnerStop(unsaved) !== 'stop') return { kind: 'kept', unsaved };
    if (!sameOwner(checked, door.currentRecord())) return { kind: 'changed-hands' };
    door.kill(checked!.pid!);
    return { kind: 'stopping', pid: checked!.pid };
}

function sameOwner(checked: CheckedOwnerIdentity | undefined, now: CheckedOwnerIdentity | undefined): boolean {
    return !!checked?.pid && !!now?.pid && checked.pid === now.pid && checked.startedUtc === now.startedUtc;
}

/// The notice when an older build is kept because of unsaved work. It carries the F-239 Save button
/// (OLDER_OWNER_ACTIONS); after a save the window switches to its own build.
export const OLDER_BUILD_UNSAVED = 'This folder is still running an older Semanticus engine, and it has unsaved changes. Save them and Semanticus will switch to the new engine.';
export const OLDER_BUILD_UNCHECKED = 'This folder is still running an older Semanticus engine, and Semanticus could not check it for unsaved changes, so it was kept. Save your work and Semanticus will switch to the new engine.';
