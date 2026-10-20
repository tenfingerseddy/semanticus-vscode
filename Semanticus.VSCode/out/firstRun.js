"use strict";
// The first-run choice (1.4, direction decision 10). The Studio asks "How do you want to work?" once per machine.
// This module is the host's half and is pure so a test can run it: the once-flag, the driver mode, and the
// one-press sequence (write the connection, then install the skill, then close the card for good).
//
// globalState, not workspaceState, for the same reason as the Plan tour: a person who chose once has chosen for
// every folder. A folder that is already wired is never asked, whatever the flag says.
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
exports.OPEN_HANDOFF_KEY = exports.OPEN_HANDOFF_MARKER = exports.PENDING_MARKER = exports.PENDING_CONNECT_KEY = exports.DRIVE_MODE_KEY = exports.FIRST_RUN_KEY = void 0;
exports.choiceFor = choiceFor;
exports.driveModeFor = driveModeFor;
exports.writeDriveMode = writeDriveMode;
exports.folderForDisplay = folderForDisplay;
exports.saveStartFolder = saveStartFolder;
exports.readFirstRun = readFirstRun;
exports.markFirstRun = markFirstRun;
exports.runFirstRunConnect = runFirstRunConnect;
exports.connectAssistant = connectAssistant;
exports.modeAfterConnect = modeAfterConnect;
exports.withoutTourDemo = withoutTourDemo;
exports.folderForConnect = folderForConnect;
exports.writePendingConnect = writePendingConnect;
exports.takePendingConnect = takePendingConnect;
exports.connectWithoutFolder = connectWithoutFolder;
exports.writeOpenHandoff = writeOpenHandoff;
exports.writeMarker = writeMarker;
exports.takeOpenHandoff = takeOpenHandoff;
const nodeFs = __importStar(require("fs"));
const nodePath = __importStar(require("path"));
const learn_1 = require("./learn");
exports.FIRST_RUN_KEY = 'semanticus.guide.firstRun';
function choiceFor(value) { return value === 'hand' || value === 'tour' ? value : 'assistant'; }
/** The driver mode, written the 1.4 contract's way (section 4): the workspace key the driver bar reads on studioReady. */
exports.DRIVE_MODE_KEY = 'semanticus.studio.driveMode';
function driveModeFor(choice) {
    return choice === 'assistant' ? 'claudeDrives' : choice === 'hand' ? 'iDrive' : undefined;
}
async function writeDriveMode(state, choice) {
    const mode = driveModeFor(choice);
    if (mode)
        await state.update(exports.DRIVE_MODE_KEY, mode);
}
/** The folder as a person reads it: a folder inside home is "~/...", home itself is whole. Shown on the card that says where to open Claude Code (r2 UAT N32). */
function folderForDisplay(folder, home) {
    const f = nodePath.resolve(folder), h = nodePath.resolve(home);
    if (f === h)
        return f; // "~" alone says nothing (r2 fix 4): the home folder is shown whole
    return f.startsWith(h + nodePath.sep) ? '~/' + f.slice(h.length + 1).split(nodePath.sep).join('/') : f;
}
/** Where a Save dialog starts when no folder is open: ~/Semanticus when it exists, else the home folder (never the root of the disk). */
function saveStartFolder(home, exists) {
    const lasting = nodePath.join(home, 'Semanticus');
    return exists(lasting) ? lasting : home;
}
function readFirstRun(state, connected) {
    const done = !!state.get(exports.FIRST_RUN_KEY)?.done;
    return { show: !done && !connected, connected };
}
async function markFirstRun(state, mode, now = () => new Date().toISOString()) {
    await state.update(exports.FIRST_RUN_KEY, { done: true, mode, at: now() });
}
/** One press: the connection first (it is the part that matters), then the skill, then the once-flag. A connection
 *  that was not written stops here and leaves the card open to try again. A skill that did not install is reported
 *  in a note and never undoes the connection. */
async function runFirstRunConnect(deps) {
    const wrote = await deps.writeConnection();
    if (!wrote.ok)
        return { ok: false, error: wrote.error ?? 'Semanticus could not write the connection.' };
    const skill = await deps.installSkill();
    await deps.markDone();
    return skill.ok ? { ok: true } : { ok: true, note: `The skill did not install: ${skill.error ?? 'unknown reason'}. Use Install or Update Assistant Skills to try again.` };
}
/** The same job from every door (the card, the header, the palette): the press above, then the say card. The card
 *  has its own say stage, so it calls runFirstRunConnect; the header and the palette come here. A connection that was
 *  not written shows nothing and changes no mode. */
async function connectAssistant(deps) {
    const result = await runFirstRunConnect(deps);
    if (result.ok)
        deps.showSay(result);
    return result;
}
/** Claude drives by default (Kane, 10 Oct). A folder that was already wired keeps the mode the person saved, so a
 *  second press of Connect does not undo their choice. */
function modeAfterConnect(wasWired, saved) {
    return wasWired && (saved === 'iDrive' || saved === 'claudeDrives') ? saved : 'claudeDrives';
}
// ---- Connect with no folder open (1.6) ---------------------------------------------------------------------
// The connection file lives in a folder, and VS Code with no folder open has none. The press must not stop there:
// it picks a folder (the model's own, or one in the home folder), writes the file, remembers what was open, and opens
// the folder. VS Code reloads the window when it goes from no folder to one, so the rest finishes after the reload.
exports.PENDING_CONNECT_KEY = 'semanticus.guide.pendingConnect';
const PENDING_CONNECT_MAX_AGE_MS = 10 * 60 * 1000;
const unsafeName = /[\\/:*?"<>|\u0000-\u001f]/g;
/** A marker file the window that opens the folder leaves in it, so the extension wakes up there (package.json lists it as an
 *  activation event). Nothing else ever creates it, so no other folder wakes the extension for this. */
exports.PENDING_MARKER = '.semanticus/connect-pending';
/** The tour's demo copy is throwaway and lives in the extension's internal storage: it is never a model to connect, to carry over a reload, or to name a folder after. */
function withoutTourDemo(model) {
    return model && (0, learn_1.isScratchCopyPath)(model.path) ? undefined : model;
}
/** The folder the connection file goes in. A model from a file: that file's own folder. A model folder (Name.SemanticModel,
 *  or its definition folder) is never written into: the connection goes beside it. A live model (its path is a temporary
 *  copy) or a model with no path: ~/Semanticus/<model name>, so it always has a stable home. */
function folderForConnect(model, home) {
    model = withoutTourDemo(model);
    if (model?.kind === 'file' && model.path) {
        for (let p = model.path; p !== nodePath.dirname(p); p = nodePath.dirname(p)) {
            if (/\.SemanticModel$/i.test(nodePath.basename(p)))
                return nodePath.dirname(p);
        }
        if (nodePath.basename(model.path).toLowerCase() === 'definition')
            return nodePath.dirname(nodePath.dirname(model.path));
        return nodePath.dirname(model.path);
    }
    const name = (model?.modelName ?? '').replace(unsafeName, ' ').replace(/\s+/g, ' ').trim().replace(/^\.+/, '') || 'Model';
    return nodePath.join(home, 'Semanticus', name);
}
async function writePendingConnect(state, pending) {
    await state.update(exports.PENDING_CONNECT_KEY, pending);
}
function sameFolder(a, b, caseInsensitive) {
    const x = nodePath.resolve(a), y = nodePath.resolve(b);
    return caseInsensitive ? x.toLowerCase() === y.toLowerCase() : x === y;
}
/** After the reload: the record for the folder that is now open, once. A stale record (older than ten minutes) and a
 *  record for another folder are never used; a stale one is dropped so it cannot surprise a later start. */
async function takePendingConnect(state, openFolder, now = Date.now, caseInsensitive = process.platform === 'win32' || process.platform === 'darwin') {
    const rec = state.get(exports.PENDING_CONNECT_KEY);
    if (!rec?.folder)
        return undefined;
    const age = now() - Date.parse(rec.at);
    const stale = !(age >= 0 && age < PENDING_CONNECT_MAX_AGE_MS);
    if (stale) {
        await state.update(exports.PENDING_CONNECT_KEY, undefined);
        return undefined;
    }
    if (!openFolder || !sameFolder(rec.folder, openFolder, caseInsensitive))
        return undefined;
    await state.update(exports.PENDING_CONNECT_KEY, undefined);
    return rec;
}
/** The press with no folder open. Order matters: the file is written first (a failure here opens nothing and leaves the
 *  card a plain line plus a button that asks for a folder), then what was open is remembered, then the folder opens. */
async function connectWithoutFolder(deps) {
    const folder = await deps.folder();
    if (!folder)
        return { ok: false, needsFolder: true, error: 'Semanticus needs a folder to keep the connection in. Choose one.' };
    const wrote = await deps.writeConnection(folder);
    if (!wrote.ok)
        return { ok: false, needsFolder: true, error: `${wrote.error ?? 'Semanticus could not write the connection there.'} Choose another folder.` };
    await deps.remember(folder);
    await deps.openFolder(folder);
    return { ok: true };
}
// ---- A folder Semanticus opens (1.6) -----------------------------------------------------------------------------
// When Semanticus itself opens a folder (Open this model's folder, Connect Claude Code), VS Code reloads the window and
// the new folder knows nothing of the model that was open. The model, the Studio and the Pro card's memory ride in THIS
// machine's globalState keyed by the folder (the same way a pending connect does). The press also leaves a marker file in
// that folder's .semanticus/, and package.json wakes the extension on it. The marker carries no model: a folder a person
// cloned or was sent can hold any file, so a marker with no record here hands over nothing (threat check 16b).
// A folder the person opens by hand gets nothing: no marker, no wake-up, and no engine in a window that has no reason.
exports.OPEN_HANDOFF_MARKER = '.semanticus/open-handoff';
exports.OPEN_HANDOFF_KEY = 'semanticus.guide.openHandoffs';
const OPEN_HANDOFF_MAX_AGE_MS = 10 * 60 * 1000;
const handoffKey = (folder, caseInsensitive) => caseInsensitive ? nodePath.resolve(folder).toLowerCase() : nodePath.resolve(folder);
const osCaseInsensitive = process.platform === 'win32' || process.platform === 'darwin';
/** Records the handoff in this machine's store, then leaves the wake-up marker in the folder about to open. A failure only means the
 *  model is not brought along. Records older than ten minutes are dropped on the way, so the store does not grow. */
async function writeOpenHandoff(state, folder, handoff, now = () => new Date().toISOString(), caseInsensitive = osCaseInsensitive) {
    try {
        const at = now();
        const records = {};
        for (const [k, v] of Object.entries(state.get(exports.OPEN_HANDOFF_KEY) ?? {})) {
            const age = Date.parse(at) - Date.parse(v?.at ?? '');
            if (age >= 0 && age < OPEN_HANDOFF_MAX_AGE_MS)
                records[k] = v;
        }
        records[handoffKey(folder, caseInsensitive)] = { ...handoff, at };
        await state.update(exports.OPEN_HANDOFF_KEY, records);
        const file = nodePath.join(folder, exports.OPEN_HANDOFF_MARKER);
        writeMarker(file, 'opening\n');
    }
    catch { /* the folder still opens; the model is one press away in Recent models */ }
}
/** Writes a wake-up marker as a new plain file. Whatever sits at that path is removed first, so a link a repo shipped there is replaced and
 *  never written through to a file of the person's (tc2-open-handoff). */
function writeMarker(file, text) {
    nodeFs.mkdirSync(nodePath.dirname(file), { recursive: true });
    nodeFs.rmSync(file, { force: true });
    nodeFs.writeFileSync(file, text, { encoding: 'utf8', flag: 'wx' });
}
/** The handoff for the folder that is open, once. The marker only wakes the extension: it is deleted whether or not it is used, and the model
 *  comes from this machine's own record for this folder (none, no handoff). A record that is stale is never used. */
function takeOpenHandoff(openFolder, now = Date.now, state, caseInsensitive = osCaseInsensitive) {
    if (!openFolder)
        return undefined;
    const file = nodePath.join(openFolder, exports.OPEN_HANDOFF_MARKER);
    let marked = true;
    try {
        nodeFs.unlinkSync(file);
    }
    catch {
        marked = false;
    }
    if (!marked || !state)
        return undefined;
    const records = state.get(exports.OPEN_HANDOFF_KEY);
    const key = handoffKey(openFolder, caseInsensitive);
    const rec = records?.[key];
    if (!rec)
        return undefined;
    const { [key]: _used, ...rest } = records;
    void Promise.resolve(state.update(exports.OPEN_HANDOFF_KEY, Object.keys(rest).length ? rest : undefined)).catch(() => { });
    if (!rec.model?.kind)
        return undefined;
    const age = now() - Date.parse(rec.at ?? '');
    return age >= 0 && age < OPEN_HANDOFF_MAX_AGE_MS ? rec : undefined;
}
//# sourceMappingURL=firstRun.js.map