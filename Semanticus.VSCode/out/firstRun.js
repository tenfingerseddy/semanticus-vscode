"use strict";
// The first-run choice (1.4, direction decision 10). The Studio asks "How do you want to work?" once per machine.
// This module is the host's half and is pure so a test can run it: the once-flag, the driver mode, and the
// one-press sequence (write the connection, then install the skill, then close the card for good).
//
// globalState, not workspaceState, for the same reason as the Plan tour: a person who chose once has chosen for
// every folder. A folder that is already wired is never asked, whatever the flag says.
Object.defineProperty(exports, "__esModule", { value: true });
exports.DRIVE_MODE_KEY = exports.FIRST_RUN_KEY = void 0;
exports.choiceFor = choiceFor;
exports.driveModeFor = driveModeFor;
exports.writeDriveMode = writeDriveMode;
exports.readFirstRun = readFirstRun;
exports.markFirstRun = markFirstRun;
exports.runFirstRunConnect = runFirstRunConnect;
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
//# sourceMappingURL=firstRun.js.map