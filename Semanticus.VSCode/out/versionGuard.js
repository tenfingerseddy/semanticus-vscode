"use strict";
/// A newer extension talking to an older engine, or the reverse (Kane, 25 September). An in-place reinstall of the
/// same version replaced the files under a running engine; the Studio page came up new, the engine stayed old, and
/// the page's first new call came back as the engine's raw "No method by the name 'describeWorkflowSteps' is found."
///
/// A missing method on our own engine has one honest reading: the two halves are different builds. So it never
/// reaches the person as an error text. It becomes one plain sentence, and the window restarts the engine itself
/// when it owns that engine and nothing unsaved is at stake. The connect-time check (F-241, engineResolution.ts)
/// catches most mismatches before any call is made; this is the safety net for the ones it keeps on purpose (an
/// older engine holding unsaved work) and for any it cannot see.
Object.defineProperty(exports, "__esModule", { value: true });
exports.SwitchNotices = exports.OLDER_BUILD_SAVE_COMMAND = exports.EngineUpdatedError = exports.METHOD_NOT_FOUND = exports.ENGINE_RESTARTED_TEXT = exports.ENGINE_UPDATED_RESTART = exports.ENGINE_UPDATED_TEXT = void 0;
exports.isMethodNotFound = isMethodNotFound;
exports.plainRpcError = plainRpcError;
exports.decideOutdatedEngine = decideOutdatedEngine;
exports.guardEngineRequests = guardEngineRequests;
exports.withSaveLink = withSaveLink;
exports.ENGINE_UPDATED_TEXT = 'Semanticus was updated. Close VS Code and your assistant, then open them again to finish.';
exports.ENGINE_UPDATED_RESTART = 'Restart';
/// What the page says instead when the window restarted the engine itself: the call that failed can simply be tried again.
exports.ENGINE_RESTARTED_TEXT = 'Semanticus was updated and is restarting to finish. Try that again in a moment.';
/// JSON-RPC's "method not found". The engine's StreamJsonRpc door answers it as
/// "No method by the name 'x' is found."; vscode-jsonrpc's own server says "Unhandled method x".
exports.METHOD_NOT_FOUND = -32601;
function messageOf(e) {
    if (typeof e === 'string')
        return e;
    const m = e?.message;
    return typeof m === 'string' ? m : '';
}
/// Did the other side say it has no such method? The code is the proof; the sentence covers a hop that dropped it.
function isMethodNotFound(e) {
    if (e === undefined || e === null)
        return false;
    if (e.code === exports.METHOD_NOT_FOUND)
        return true;
    return /No method by the name '[^']*' is found/.test(messageOf(e));
}
/// The text a person reads for a failed engine call: the plain sentence for a build mismatch (or the restart line
/// when the window is restarting the engine itself), the engine's own message for everything else.
function plainRpcError(e, action) {
    if (isMethodNotFound(e))
        return action === 'restart' ? exports.ENGINE_RESTARTED_TEXT : exports.ENGINE_UPDATED_TEXT;
    const m = messageOf(e);
    return m || String(e);
}
function decideOutdatedEngine(input) {
    if (!input.ownedByThisWindow)
        return 'tell';
    if (!input.expected)
        return 'offer-restart';
    if (input.reported === input.expected)
        return 'tell';
    return input.unsaved === false ? 'restart' : 'offer-restart';
}
class EngineUpdatedError extends Error {
    constructor(method) {
        super(exports.ENGINE_UPDATED_TEXT);
        this.method = method;
        this.code = exports.METHOD_NOT_FOUND;
    }
}
exports.EngineUpdatedError = EngineUpdatedError;
/// Patch every request on this connection so a missing method rejects with the plain sentence (keeping the
/// method-not-found code for callers that test it) and tells the window which call hit it. Patched in place: the
/// extension compares connections by identity (conn === connected), so a wrapper object would break those checks.
function guardEngineRequests(connection, onOutdated) {
    const send = connection.sendRequest.bind(connection);
    connection.sendRequest = async (...args) => {
        try {
            return await send(...args);
        }
        catch (e) {
            if (!isMethodNotFound(e))
                throw e;
            const method = typeof args[0] === 'string' ? args[0] : String(args[0]?.method ?? '');
            try {
                onOutdated(method);
            }
            catch { /* the notice is best-effort; the caller still gets the sentence */ }
            throw new EngineUpdatedError(method);
        }
    };
    return connection;
}
/// The command behind the Save link on the older-engine notice.
exports.OLDER_BUILD_SAVE_COMMAND = 'semanticus.saveOlderEngine';
/// A notice line with its Save as a link: a notification Semanticus can close carries no buttons, only links.
function withSaveLink(text) {
    return `${text} [Save](command:${exports.OLDER_BUILD_SAVE_COMMAND})`;
}
/// Notices that stay up until an engine switch finishes. hold() is the promise a progress notification waits on;
/// settle() closes every one of them at once. VS Code cannot close a showWarningMessage, so a notice that must go
/// away when the situation it describes is over has to be one of these.
class SwitchNotices {
    constructor() {
        this.waiting = [];
    }
    get open() { return this.waiting.length; }
    hold() { return new Promise((resolve) => { this.waiting.push(resolve); }); }
    settle() {
        const done = this.waiting;
        this.waiting = [];
        for (const resolve of done)
            resolve();
    }
}
exports.SwitchNotices = SwitchNotices;
//# sourceMappingURL=versionGuard.js.map