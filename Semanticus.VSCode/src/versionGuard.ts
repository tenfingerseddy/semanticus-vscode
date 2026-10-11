/// A newer extension talking to an older engine, or the reverse (Kane, 25 September). An in-place reinstall of the
/// same version replaced the files under a running engine; the Studio page came up new, the engine stayed old, and
/// the page's first new call came back as the engine's raw "No method by the name 'describeWorkflowSteps' is found."
///
/// A missing method on our own engine has one honest reading: the two halves are different builds. So it never
/// reaches the person as an error text. It becomes one plain sentence, and the window restarts the engine itself
/// when it owns that engine and nothing unsaved is at stake. The connect-time check (F-241, engineResolution.ts)
/// catches most mismatches before any call is made; this is the safety net for the ones it keeps on purpose (an
/// older engine holding unsaved work) and for any it cannot see.

export const ENGINE_UPDATED_TEXT = 'Semanticus was updated. Close VS Code and your assistant, then open them again to finish.';
export const ENGINE_UPDATED_RESTART = 'Restart';
/// What the page says instead when the window restarted the engine itself: the call that failed can simply be tried again.
export const ENGINE_RESTARTED_TEXT = 'Semanticus was updated and is restarting to finish. Try that again in a moment.';

/// JSON-RPC's "method not found". The engine's StreamJsonRpc door answers it as
/// "No method by the name 'x' is found."; vscode-jsonrpc's own server says "Unhandled method x".
export const METHOD_NOT_FOUND = -32601;

function messageOf(e: unknown): string {
    if (typeof e === 'string') return e;
    const m = (e as { message?: unknown } | undefined)?.message;
    return typeof m === 'string' ? m : '';
}

/// Did the other side say it has no such method? The code is the proof; the sentence covers a hop that dropped it.
export function isMethodNotFound(e: unknown): boolean {
    if (e === undefined || e === null) return false;
    if ((e as { code?: unknown }).code === METHOD_NOT_FOUND) return true;
    return /No method by the name '[^']*' is found/.test(messageOf(e));
}

/// The text a person reads for a failed engine call: the plain sentence for a build mismatch (or the restart line
/// when the window is restarting the engine itself), the engine's own message for everything else.
export function plainRpcError(e: unknown, action?: OutdatedAction): string {
    if (isMethodNotFound(e)) return action === 'restart' ? ENGINE_RESTARTED_TEXT : ENGINE_UPDATED_TEXT;
    const m = messageOf(e);
    return m || String(e);
}

/// What the window does once it knows the engine answered as another build.
///   restart        it owns the engine, the engine is another build, and nothing unsaved is at stake: restart now.
///   offer-restart  it owns the engine but a restart could cost unsaved work (or the builds could not be compared):
///                  the sentence with a Restart button, which goes through the restart that asks about saving.
///   tell           another process owns the engine, or the engine already matches the installed files (so this
///                  window is the stale half and restarting the engine would not help): the sentence only.
export type OutdatedAction = 'restart' | 'offer-restart' | 'tell';
export interface OutdatedInput {
    /// The build installed on disk now, which a fresh start would run. Undefined when it cannot be read.
    expected?: string;
    /// The build the running engine reports. '' or undefined from an engine too old to say.
    reported?: string;
    ownedByThisWindow: boolean;
    /// The engine's answer about unsaved work. Undefined (could not ask) counts as yes.
    unsaved?: boolean;
}

export function decideOutdatedEngine(input: OutdatedInput): OutdatedAction {
    if (!input.ownedByThisWindow) return 'tell';
    if (!input.expected) return 'offer-restart';
    if (input.reported === input.expected) return 'tell';
    return input.unsaved === false ? 'restart' : 'offer-restart';
}

export interface RequestSender { sendRequest(...args: any[]): Promise<any>; }
export class EngineUpdatedError extends Error {
    readonly code = METHOD_NOT_FOUND;
    constructor(readonly method: string) { super(ENGINE_UPDATED_TEXT); }
}

/// Patch every request on this connection so a missing method rejects with the plain sentence (keeping the
/// method-not-found code for callers that test it) and tells the window which call hit it. Patched in place: the
/// extension compares connections by identity (conn === connected), so a wrapper object would break those checks.
export function guardEngineRequests<T extends RequestSender>(connection: T, onOutdated: (method: string) => void): T {
    const send = connection.sendRequest.bind(connection);
    connection.sendRequest = async (...args: any[]) => {
        try { return await send(...args); }
        catch (e) {
            if (!isMethodNotFound(e)) throw e;
            const method = typeof args[0] === 'string' ? args[0] : String((args[0] as { method?: string })?.method ?? '');
            try { onOutdated(method); } catch { /* the notice is best-effort; the caller still gets the sentence */ }
            throw new EngineUpdatedError(method);
        }
    };
    return connection;
}

/// The command behind the Save link on the older-engine notice.
export const OLDER_BUILD_SAVE_COMMAND = 'semanticus.saveOlderEngine';

/// A notice line with its Save as a link: a notification Semanticus can close carries no buttons, only links.
export function withSaveLink(text: string): string {
    return `${text} [Save](command:${OLDER_BUILD_SAVE_COMMAND})`;
}

/// Notices that stay up until an engine switch finishes. hold() is the promise a progress notification waits on;
/// settle() closes every one of them at once. VS Code cannot close a showWarningMessage, so a notice that must go
/// away when the situation it describes is over has to be one of these.
export class SwitchNotices {
    private waiting: Array<() => void> = [];
    get open(): number { return this.waiting.length; }
    hold(): Promise<void> { return new Promise<void>((resolve) => { this.waiting.push(resolve); }); }
    settle(): void {
        const done = this.waiting;
        this.waiting = [];
        for (const resolve of done) resolve();
    }
}
