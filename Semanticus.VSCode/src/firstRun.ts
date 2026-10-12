// The first-run choice (1.4, direction decision 10). The Studio asks "How do you want to work?" once per machine.
// This module is the host's half and is pure so a test can run it: the once-flag, the driver mode, and the
// one-press sequence (write the connection, then install the skill, then close the card for good).
//
// globalState, not workspaceState, for the same reason as the Plan tour: a person who chose once has chosen for
// every folder. A folder that is already wired is never asked, whatever the flag says.

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
