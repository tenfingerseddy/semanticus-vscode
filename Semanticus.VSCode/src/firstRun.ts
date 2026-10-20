// The first-run choice (1.4, direction decision 10). The Studio asks "How do you want to work?" once per machine.
// This module is the host's half and is pure so a test can run it: the once-flag, the driver mode, and the
// one-press sequence (write the connection, then install the skill, then close the card for good).
//
// globalState, not workspaceState, for the same reason as the Plan tour: a person who chose once has chosen for
// every folder. A folder that is already wired is never asked, whatever the flag says.

import * as nodeFs from 'fs';
import * as nodePath from 'path';
import { isScratchCopyPath } from './learn';

export const FIRST_RUN_KEY = 'semanticus.guide.firstRun';

/** What the person picked on the card. "tour" is Show me around: nothing chosen yet, so no mode is written. */
export type FirstRunChoice = 'assistant' | 'hand' | 'tour';
export function choiceFor(value: unknown): FirstRunChoice { return value === 'hand' || value === 'tour' ? value : 'assistant'; }

/** The driver mode, written the 1.4 contract's way (section 4): the workspace key the driver bar reads on studioReady. */
export const DRIVE_MODE_KEY = 'semanticus.studio.driveMode';
export type DriveMode = 'claudeDrives' | 'iDrive';
export function driveModeFor(choice: FirstRunChoice): DriveMode | undefined {
    return choice === 'assistant' ? 'claudeDrives' : choice === 'hand' ? 'iDrive' : undefined;
}
export async function writeDriveMode(state: FirstRunStore, choice: FirstRunChoice): Promise<void> {
    const mode = driveModeFor(choice);
    if (mode) await state.update(DRIVE_MODE_KEY, mode);
}

export interface FirstRunStore {
    get<T>(key: string): T | undefined;
    update(key: string, value: unknown): Thenable<void> | Promise<void>;
}

export interface FirstRunState { show: boolean; connected: boolean }

/** The folder as a person reads it: a folder inside home is "~/...", home itself is whole. Shown on the card that says where to open Claude Code (r2 UAT N32). */
export function folderForDisplay(folder: string, home: string): string {
    const f = nodePath.resolve(folder), h = nodePath.resolve(home);
    if (f === h) return f;   // "~" alone says nothing (r2 fix 4): the home folder is shown whole
    return f.startsWith(h + nodePath.sep) ? '~/' + f.slice(h.length + 1).split(nodePath.sep).join('/') : f;
}

/** Where a Save dialog starts when no folder is open: ~/Semanticus when it exists, else the home folder (never the root of the disk). */
export function saveStartFolder(home: string, exists: (p: string) => boolean): string {
    const lasting = nodePath.join(home, 'Semanticus');
    return exists(lasting) ? lasting : home;
}

export function readFirstRun(state: FirstRunStore, connected: boolean): FirstRunState {
    const done = !!state.get<{ done?: boolean }>(FIRST_RUN_KEY)?.done;
    return { show: !done && !connected, connected };
}

export async function markFirstRun(state: FirstRunStore, mode: FirstRunChoice, now: () => string = () => new Date().toISOString()): Promise<void> {
    await state.update(FIRST_RUN_KEY, { done: true, mode, at: now() });
}

export interface StepResult { ok: boolean; error?: string }
export interface FirstRunResult { ok: boolean; error?: string; note?: string }

/** One press: the connection first (it is the part that matters), then the skill, then the once-flag. A connection
 *  that was not written stops here and leaves the card open to try again. A skill that did not install is reported
 *  in a note and never undoes the connection. */
export async function runFirstRunConnect(deps: {
    writeConnection: () => Promise<StepResult>; installSkill: () => Promise<StepResult>; markDone: () => Promise<void>;
}): Promise<FirstRunResult> {
    const wrote = await deps.writeConnection();
    if (!wrote.ok) return { ok: false, error: wrote.error ?? 'Semanticus could not write the connection.' };
    const skill = await deps.installSkill();
    await deps.markDone();
    return skill.ok ? { ok: true } : { ok: true, note: `The skill did not install: ${skill.error ?? 'unknown reason'}. Use Install or Update Assistant Skills to try again.` };
}

/** The same job from every door (the card, the header, the palette): the press above, then the say card. The card
 *  has its own say stage, so it calls runFirstRunConnect; the header and the palette come here. A connection that was
 *  not written shows nothing and changes no mode. */
export async function connectAssistant(deps: {
    writeConnection: () => Promise<StepResult>; installSkill: () => Promise<StepResult>; markDone: () => Promise<void>;
    showSay: (result: FirstRunResult) => void;
}): Promise<FirstRunResult> {
    const result = await runFirstRunConnect(deps);
    if (result.ok) deps.showSay(result);
    return result;
}

/** Claude drives by default (Kane, 10 Oct). A folder that was already wired keeps the mode the person saved, so a
 *  second press of Connect does not undo their choice. */
export function modeAfterConnect(wasWired: boolean, saved: unknown): DriveMode {
    return wasWired && (saved === 'iDrive' || saved === 'claudeDrives') ? saved : 'claudeDrives';
}

// ---- Connect with no folder open (1.6) ---------------------------------------------------------------------
// The connection file lives in a folder, and VS Code with no folder open has none. The press must not stop there:
// it picks a folder (the model's own, or one in the home folder), writes the file, remembers what was open, and opens
// the folder. VS Code reloads the window when it goes from no folder to one, so the rest finishes after the reload.

export const PENDING_CONNECT_KEY = 'semanticus.guide.pendingConnect';
const PENDING_CONNECT_MAX_AGE_MS = 10 * 60 * 1000;

/** What was open, in the shape the host remembers a model (extension.ts LastModel). Carried over the reload. */
export interface PendingModel { kind: 'file' | 'live' | 'localDesktop'; path?: string; endpoint?: string; database?: string; tenantId?: string; authMode?: string; modelName?: string }
export interface PendingConnect { folder: string; model?: PendingModel; at: string; asked?: string[] }

const unsafeName = /[\\/:*?"<>|\u0000-\u001f]/g;

/** A marker file the window that opens the folder leaves in it, so the extension wakes up there (package.json lists it as an
 *  activation event). Nothing else ever creates it, so no other folder wakes the extension for this. */
export const PENDING_MARKER = '.semanticus/connect-pending';

/** The tour's demo copy is throwaway and lives in the extension's internal storage: it is never a model to connect, to carry over a reload, or to name a folder after. */
export function withoutTourDemo<T extends { path?: string }>(model: T | undefined): T | undefined {
    return model && isScratchCopyPath(model.path) ? undefined : model;
}

/** The folder the connection file goes in. A model from a file: that file's own folder. A model folder (Name.SemanticModel,
 *  or its definition folder) is never written into: the connection goes beside it. A live model (its path is a temporary
 *  copy) or a model with no path: ~/Semanticus/<model name>, so it always has a stable home. */
export function folderForConnect(model: { kind?: string; path?: string; modelName?: string } | undefined, home: string): string {
    model = withoutTourDemo(model);
    if (model?.kind === 'file' && model.path) {
        for (let p = model.path; p !== nodePath.dirname(p); p = nodePath.dirname(p)) {
            if (/\.SemanticModel$/i.test(nodePath.basename(p))) return nodePath.dirname(p);
        }
        if (nodePath.basename(model.path).toLowerCase() === 'definition') return nodePath.dirname(nodePath.dirname(model.path));
        return nodePath.dirname(model.path);
    }
    const name = (model?.modelName ?? '').replace(unsafeName, ' ').replace(/\s+/g, ' ').trim().replace(/^\.+/, '') || 'Model';
    return nodePath.join(home, 'Semanticus', name);
}

export async function writePendingConnect(state: FirstRunStore, pending: PendingConnect): Promise<void> {
    await state.update(PENDING_CONNECT_KEY, pending);
}

function sameFolder(a: string, b: string, caseInsensitive: boolean): boolean {
    const x = nodePath.resolve(a), y = nodePath.resolve(b);
    return caseInsensitive ? x.toLowerCase() === y.toLowerCase() : x === y;
}

/** After the reload: the record for the folder that is now open, once. A stale record (older than ten minutes) and a
 *  record for another folder are never used; a stale one is dropped so it cannot surprise a later start. */
export async function takePendingConnect(state: FirstRunStore, openFolder: string | undefined, now: () => number = Date.now,
                                         caseInsensitive: boolean = process.platform === 'win32' || process.platform === 'darwin'): Promise<PendingConnect | undefined> {
    const rec = state.get<PendingConnect>(PENDING_CONNECT_KEY);
    if (!rec?.folder) return undefined;
    const age = now() - Date.parse(rec.at);
    const stale = !(age >= 0 && age < PENDING_CONNECT_MAX_AGE_MS);
    if (stale) { await state.update(PENDING_CONNECT_KEY, undefined); return undefined; }
    if (!openFolder || !sameFolder(rec.folder, openFolder, caseInsensitive)) return undefined;
    await state.update(PENDING_CONNECT_KEY, undefined);
    return rec;
}

export interface NoFolderResult extends FirstRunResult { needsFolder?: boolean }

/** The press with no folder open. Order matters: the file is written first (a failure here opens nothing and leaves the
 *  card a plain line plus a button that asks for a folder), then what was open is remembered, then the folder opens. */
export async function connectWithoutFolder(deps: {
    folder: () => Promise<string | undefined>;
    writeConnection: (folder: string) => Promise<StepResult>;
    remember: (folder: string) => Promise<void>;
    openFolder: (folder: string) => Promise<void>;
}): Promise<NoFolderResult> {
    const folder = await deps.folder();
    if (!folder) return { ok: false, needsFolder: true, error: 'Semanticus needs a folder to keep the connection in. Choose one.' };
    const wrote = await deps.writeConnection(folder);
    if (!wrote.ok) return { ok: false, needsFolder: true, error: `${wrote.error ?? 'Semanticus could not write the connection there.'} Choose another folder.` };
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

export const OPEN_HANDOFF_MARKER = '.semanticus/open-handoff';
export const OPEN_HANDOFF_KEY = 'semanticus.guide.openHandoffs';
const OPEN_HANDOFF_MAX_AGE_MS = 10 * 60 * 1000;

/** What the record carries: the model that was open, whether the Studio was open, and the scopes the Pro card had already asked about
 *  (so a reload Semanticus caused does not ask the same question again). */
export interface OpenHandoff { model?: PendingModel; studio: boolean; asked: string[]; at?: string }

const handoffKey = (folder: string, caseInsensitive: boolean) => caseInsensitive ? nodePath.resolve(folder).toLowerCase() : nodePath.resolve(folder);
const osCaseInsensitive = process.platform === 'win32' || process.platform === 'darwin';

/** Records the handoff in this machine's store, then leaves the wake-up marker in the folder about to open. A failure only means the
 *  model is not brought along. Records older than ten minutes are dropped on the way, so the store does not grow. */
export async function writeOpenHandoff(state: FirstRunStore, folder: string, handoff: OpenHandoff, now: () => string = () => new Date().toISOString(),
                                       caseInsensitive: boolean = osCaseInsensitive): Promise<void> {
    try {
        const at = now();
        const records: Record<string, OpenHandoff> = {};
        for (const [k, v] of Object.entries(state.get<Record<string, OpenHandoff>>(OPEN_HANDOFF_KEY) ?? {})) {
            const age = Date.parse(at) - Date.parse(v?.at ?? '');
            if (age >= 0 && age < OPEN_HANDOFF_MAX_AGE_MS) records[k] = v;
        }
        records[handoffKey(folder, caseInsensitive)] = { ...handoff, at };
        await state.update(OPEN_HANDOFF_KEY, records);
        const file = nodePath.join(folder, OPEN_HANDOFF_MARKER);
        writeMarker(file, 'opening\n');
    } catch { /* the folder still opens; the model is one press away in Recent models */ }
}

/** Writes a wake-up marker as a new plain file. Whatever sits at that path is removed first, so a link a repo shipped there is replaced and
 *  never written through to a file of the person's (tc2-open-handoff). */
export function writeMarker(file: string, text: string): void {
    nodeFs.mkdirSync(nodePath.dirname(file), { recursive: true });
    nodeFs.rmSync(file, { force: true });
    nodeFs.writeFileSync(file, text, { encoding: 'utf8', flag: 'wx' });
}

/** The handoff for the folder that is open, once. The marker only wakes the extension: it is deleted whether or not it is used, and the model
 *  comes from this machine's own record for this folder (none, no handoff). A record that is stale is never used. */
export function takeOpenHandoff(openFolder: string | undefined, now: () => number = Date.now, state?: FirstRunStore,
                                caseInsensitive: boolean = osCaseInsensitive): OpenHandoff | undefined {
    if (!openFolder) return undefined;
    const file = nodePath.join(openFolder, OPEN_HANDOFF_MARKER);
    let marked = true;
    try { nodeFs.unlinkSync(file); } catch { marked = false; }
    if (!marked || !state) return undefined;
    const records = state.get<Record<string, OpenHandoff>>(OPEN_HANDOFF_KEY);
    const key = handoffKey(openFolder, caseInsensitive);
    const rec = records?.[key];
    if (!rec) return undefined;
    const { [key]: _used, ...rest } = records!;
    void Promise.resolve(state.update(OPEN_HANDOFF_KEY, Object.keys(rest).length ? rest : undefined)).catch(() => { /* a record that stays is only stale in ten minutes */ });
    if (!rec.model?.kind) return undefined;
    const age = now() - Date.parse(rec.at ?? '');
    return age >= 0 && age < OPEN_HANDOFF_MAX_AGE_MS ? rec : undefined;
}
