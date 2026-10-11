"use strict";
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
exports.OLDER_BUILD_UNCHECKED = exports.OLDER_BUILD_UNSAVED = exports.OLDER_BUILD_SAVE_COPY = exports.ASSISTANT_SAVE_COPY = exports.ASSISTANT_UNSAVED_SAME = exports.OLDER_OWNER_ACTIONS = exports.OLDER_OWNER_SAVE = exports.ASSISTANT_UNSAVED_OLDER = void 0;
exports.resolveEngineCandidate = resolveEngineCandidate;
exports.engineOwnerMatches = engineOwnerMatches;
exports.decideEngineOwner = decideEngineOwner;
exports.engineExtensionFolder = engineExtensionFolder;
exports.decideStaleEngine = decideStaleEngine;
exports.decideOwnerStop = decideOwnerStop;
exports.saveOlderOwnerThenProceed = saveOlderOwnerThenProceed;
exports.shouldAutoHealMcpEntry = shouldAutoHealMcpEntry;
exports.engineBuildIdentity = engineBuildIdentity;
exports.settleRunningBuild = settleRunningBuild;
exports.replaceCheckedOwner = replaceCheckedOwner;
const crypto = __importStar(require("crypto"));
const fs = __importStar(require("fs"));
const path = __importStar(require("path"));
const licenseDelivery_1 = require("./licenseDelivery");
const mcpLauncher_1 = require("./mcpLauncher");
/// Keep the F5 override inside the Extension Development Host. A Marketplace install is a verified pair of
/// extension + bundled engine; silently substituting a global debug DLL makes its displayed version meaningless.
function resolveEngineCandidate(input) {
    if (input.mode === 'development' && input.overrideDll && input.exists(input.overrideDll))
        return { kind: 'dll', path: input.overrideDll };
    if (input.exists(input.bundledExecutable))
        return { kind: 'exe', path: input.bundledExecutable };
    if (input.mode === 'production')
        throw new Error(`The bundled Semanticus engine was not found at ${input.bundledExecutable}. Reinstall the extension.`);
    throw new Error(`Semanticus engine not found. Set "semanticus.engineDll" to a built Semanticus.Engine.dll, ` +
        `or reinstall the extension so ${input.bundledExecutable} is present.`);
}
function normalizedOwnerPath(value, platform) {
    // Tests exercise every target from any host, so choose path semantics from the TARGET instead of Node's host.
    const resolved = (platform === 'win32' ? path.win32 : path.posix).resolve(value);
    return platform === 'win32' ? resolved.toLowerCase() : resolved;
}
/// The workspace rendezvous is shared across VS Code and MCP, but a live pipe is not proof that the owner is the
/// binary this host selected. Packaged owners must match the bundled apphost exactly. A dev DLL is dotnet-hosted,
/// so the broker's executable provenance can distinguish a dev owner from a packaged one (the exact DLL remains a
/// developer-controlled concern within Development mode).
function engineOwnerMatches(expected, recordedExecutable, platform) {
    if (!recordedExecutable)
        return false;
    if (expected.kind === 'exe')
        return normalizedOwnerPath(expected.path, platform) === normalizedOwnerPath(recordedExecutable, platform);
    const ownerName = (platform === 'win32' ? path.win32 : path.posix).basename(recordedExecutable).toLowerCase();
    return ownerName === (platform === 'win32' ? 'dotnet.exe' : 'dotnet');
}
/// Missing provenance is a legacy state, not proof of a mismatch. Production still needs a resolvable bundled
/// candidate before it can compare; a Development Host may attach to an already-live owner when its checkout is not
/// currently buildable. The caller owns the one-time warnings for the two unverified attach decisions.
function decideEngineOwner(expected, recordedExecutable, mode, platform) {
    if (!expected)
        return mode === 'development' ? 'development-fallback' : 'unresolved';
    if (!recordedExecutable)
        return 'legacy';
    return engineOwnerMatches(expected, recordedExecutable, platform) ? 'match' : 'mismatch';
}
/// The extension install folder a bundled engine belongs to. Every packaged engine lives at
/// <extensions>/<publisher>.<name>-<version>/engine/Semanticus.Engine[.exe], so the grandparent of the
/// executable IS the version folder. Undefined for anything that is not that shape: a development owner
/// records dotnet, which belongs to no extension and must never be judged by this rule.
function engineExtensionFolder(executable, platform) {
    if (!executable)
        return undefined;
    const paths = platform === 'win32' ? path.win32 : path.posix;
    const binary = paths.basename(executable).toLowerCase();
    if (binary !== 'semanticus.engine' && binary !== 'semanticus.engine.exe')
        return undefined;
    const engineDir = paths.dirname(executable);
    if (paths.basename(engineDir).toLowerCase() !== 'engine')
        return undefined;
    const folder = paths.dirname(engineDir);
    return folder && folder !== engineDir ? folder : undefined;
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
/// An engine whose recorded parent is dead is inside its own twenty-second grace (ParentLifetime) or already
/// leaving. It cannot be rescued by adoption, because the only window that would come back is this newer
/// version and adoption never crosses install folders, so the grace can be cut short rather than waited out.
///
/// An OLD-FORMAT record (no recordedParentPid at all) comes from a build with no parent watch and therefore
/// no grace to consult. The only evidence left is the engine pid: gone means stale, answering means we
/// cannot prove it is unowned, so it is left alone. That is deliberately conservative, and it means an
/// orphan from before this change is never swept automatically. That is the right way round: failing to
/// reap a leftover costs memory, stopping a live session costs someone's work.
function decideStaleEngine(input) {
    const recorded = engineExtensionFolder(input.recordedExecutable, input.platform);
    if (!recorded || !input.extensionPath)
        return 'keep';
    if (normalizedOwnerPath(recorded, input.platform) === normalizedOwnerPath(input.extensionPath, input.platform))
        return 'keep';
    if (!input.enginePidAlive)
        return 'stop-stale';
    // An owner the assistant started (VS Code was closed) records the assistant's connection as its parent, which
    // stays alive, so the parent rule would read it as an open window and lock the updated window out of the
    // folder. No window is using it, and the old connection tells the assistant to reconnect once it is gone.
    if (input.recordedStartedBy === 'assistant')
        return 'stop-stale';
    if (input.recordedParentPid === undefined)
        return 'leave-foreign';
    return input.recordedParentAlive ? 'leave-foreign' : 'stop-stale';
}
/// What VS Code says, and does, when an owner it would stop still holds the assistant's unsaved work.
/// The older-engine line carries a Save button (F-239): after an update the assistant's new connection cannot reach
/// the older owner, so "ask it to save" was a dead end. Save goes through that owner's own agent door instead.
exports.ASSISTANT_UNSAVED_OLDER = 'Your assistant has unsaved changes in an older engine. Save them and this window will carry on.';
exports.OLDER_OWNER_SAVE = 'Save';
exports.OLDER_OWNER_ACTIONS = [exports.OLDER_OWNER_SAVE];
exports.ASSISTANT_UNSAVED_SAME = 'Your assistant has unsaved changes in this folder. Ask it to save them, then reload the window.';
/// May VS Code stop this owner? Only on a definite "no unsaved changes". An owner that has them, or that could not
/// be asked, keeps running: stopping it would throw the person's work away in silence, and waiting costs nothing.
function decideOwnerStop(hasUnsavedChanges) {
    return hasUnsavedChanges === false ? 'stop' : 'leave-running';
}
exports.ASSISTANT_SAVE_COPY = { whose: "your assistant's changes", after: 'This window is taking over the folder.' };
exports.OLDER_BUILD_SAVE_COPY = { whose: 'your changes', after: 'Semanticus is switching to the new engine.' };
/// Save the older owner's model through its agent door, then carry on the usual way. The usual way asks the owner
/// again and stops it only on a clean answer, so a save that did not take can never lose work. A failed or silent
/// save keeps the owner running and says why in one line.
async function saveOlderOwnerThenProceed(owner, proceed, timeoutMs = 60_000, copy = exports.ASSISTANT_SAVE_COPY) {
    let timer;
    try {
        await Promise.race([
            owner.save(),
            new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('the older engine did not answer in time')), timeoutMs); }),
        ]);
    }
    catch (e) {
        const first = String(e?.message ?? e ?? '').split(/\r?\n/)[0].trim().replace(/\.+$/, '');
        const why = first || 'the older engine did not say why';
        return { saved: false, line: `Could not save ${copy.whose}: ${why}. The older engine keeps running.` };
    }
    finally {
        if (timer)
            clearTimeout(timer);
    }
    await proceed();
    return { saved: true, line: `Saved ${copy.whose}. ${copy.after}` };
}
function normalizedWorkspaceFromArgs(args, platform) {
    if (!Array.isArray(args) || args.some((x) => typeof x !== 'string'))
        return undefined;
    const values = args;
    if (values.length !== 3 && values.length !== 5)
        return undefined;
    if (values[0] !== 'mcp' || values[1] !== '--workspace' || !values[2])
        return undefined;
    if (values.length === 5 && (values[3] !== '--license' || !values[4]))
        return undefined;
    const index = values.indexOf('--workspace');
    return normalizedOwnerPath(values[index + 1], platform);
}
function looksLikeGeneratedBundledEntry(value, platform) {
    if (!value || typeof value !== 'object' || Array.isArray(value))
        return false;
    const candidate = value;
    if (Object.keys(candidate).some((key) => key !== 'command' && key !== 'args'))
        return false;
    if (typeof candidate.command !== 'string' || !Array.isArray(candidate.args))
        return false;
    if (candidate.args[0] !== 'mcp' || normalizedWorkspaceFromArgs(candidate.args, platform) === undefined)
        return false;
    // The stable launcher (POSIX) is a Semanticus-written shape too, so an entry can move onto it and stay there.
    if (platform !== 'win32' && (0, mcpLauncher_1.isLauncherCommand)(candidate.command))
        return true;
    const paths = platform === 'win32' ? path.win32 : path.posix;
    const binary = paths.basename(candidate.command).toLowerCase();
    const parent = paths.basename(paths.dirname(candidate.command)).toLowerCase();
    return parent === 'engine' && binary === (platform === 'win32' ? 'semanticus.engine.exe' : 'semanticus.engine');
}
/// Activation may silently refresh only the entry shape that Semanticus itself wrote (a bundled apphost, or the
/// stable launcher on POSIX), only when its command changed (an extension update moved the apphost, or the entry
/// moves onto the launcher), and only for the same workspace. Hand-authored,
/// DLL-hosted, cross-workspace, or augmented entries still require the explicit Connect AI Assistant confirmation.
function shouldAutoHealMcpEntry(prior, desired, platform) {
    if (!looksLikeGeneratedBundledEntry(prior, platform) || !looksLikeGeneratedBundledEntry(desired, platform))
        return false;
    const oldWorkspace = normalizedWorkspaceFromArgs(prior.args, platform);
    const newWorkspace = normalizedWorkspaceFromArgs(desired.args, platform);
    if (oldWorkspace !== newWorkspace)
        return false;
    const commandChanged = normalizedOwnerPath(prior.command, platform) !== normalizedOwnerPath(desired.command, platform);
    const licenseStripped = (0, licenseDelivery_1.argsContainLicenseToken)(prior.args) && !(0, licenseDelivery_1.argsContainLicenseToken)(desired.args);
    return commandChanged || licenseStripped;
}
/// Which BUILD sits in an engine folder (F-241). An in-place reinstall keeps the version and the executable path
/// and changes only the bytes, so the path alone let a new window adopt the engine the previous build had started.
/// This is the SAME recipe the engine records in engine.json and answers over `buildIdentity`
/// (Semanticus.Engine/BuildIdentity.cs), and both sides pin one test vector: every Semanticus.*.dll in the folder,
/// lower-cased name, sorted ordinally, as "name\nsha256(bytes)\n", then sha256 of that text. Undefined when the
/// folder holds nothing of ours or cannot be read, which never matches anything.
function engineBuildIdentity(directory) {
    try {
        const names = fs.readdirSync(directory)
            .map((name) => ({ name: name.toLowerCase(), file: path.join(directory, name) }))
            .filter((f) => f.name.startsWith('semanticus.') && f.name.endsWith('.dll'))
            .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
        if (names.length === 0)
            return undefined;
        let text = '';
        for (const f of names)
            text += `${f.name}\n${crypto.createHash('sha256').update(fs.readFileSync(f.file)).digest('hex')}\n`;
        return crypto.createHash('sha256').update(text, 'utf8').digest('hex');
    }
    catch {
        return undefined;
    }
}
async function settleRunningBuild(expected, running, spawnedByThisWindow) {
    // Nothing to compare against: attach as every build before this one did.
    if (!expected)
        return { verdict: 'adopt' };
    const reported = (await running.build()) || undefined;
    if (reported === expected)
        return { verdict: 'adopt', reported };
    // Never replace the engine this window just started. If the two recipes ever disagreed, replacing it would
    // start the same build again and loop; the caller logs the disagreement instead.
    if (spawnedByThisWindow)
        return { verdict: 'adopt', reported };
    const unsaved = await running.hasUnsavedChanges();
    return { verdict: decideOwnerStop(unsaved) === 'stop' ? 'replace' : 'keep-for-unsaved', reported, unsaved };
}
async function replaceCheckedOwner(checked, door) {
    const answer = await door.stopIfClean();
    if (answer === undefined)
        return { kind: 'kept', unsaved: undefined };
    if (answer !== 'unsupported')
        return answer.stopping ? { kind: 'stopping', pid: answer.pid } : { kind: 'kept', unsaved: true };
    const unsaved = await door.hasUnsavedChanges();
    if (decideOwnerStop(unsaved) !== 'stop')
        return { kind: 'kept', unsaved };
    if (!sameOwner(checked, door.currentRecord()))
        return { kind: 'changed-hands' };
    door.kill(checked.pid);
    return { kind: 'stopping', pid: checked.pid };
}
function sameOwner(checked, now) {
    return !!checked?.pid && !!now?.pid && checked.pid === now.pid && checked.startedUtc === now.startedUtc;
}
/// The notice when an older build is kept because of unsaved work. It carries the F-239 Save button
/// (OLDER_OWNER_ACTIONS); after a save the window switches to its own build.
exports.OLDER_BUILD_UNSAVED = 'This folder is still running an older Semanticus engine, and it has unsaved changes. Save them and Semanticus will switch to the new engine.';
exports.OLDER_BUILD_UNCHECKED = 'This folder is still running an older Semanticus engine, and Semanticus could not check it for unsaved changes, so it was kept. Save your work and Semanticus will switch to the new engine.';
//# sourceMappingURL=engineResolution.js.map