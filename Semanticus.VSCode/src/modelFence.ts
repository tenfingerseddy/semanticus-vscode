// The model a request was made for (drafts-sol round 2, C1; round 3, the model fence for the extension's own commands). A write names the model the person was looking at
// when they ASKED for it, and the ENGINE refuses it when another model is open (PersonFence, Semanticus.Engine/ModelFence.cs). The name travels as ONE extra top-level member of
// the JSON-RPC request, `modelIdentity`, so no method signature carries it and no call site can forget it. A second member, `namesModel: true`, says "this writer names a model
// on every request", so the engine can tell a request that named none (a command that could not say which model it meant) from an older client that never names one.
//
// WHERE the name is taken. Not where the request is sent: a command waits on the person first (an input box, a modal), and the assistant may open another model in that
// time. The name is taken where the action STARTS and carried by async context (AsyncLocalStorage) to every request the action sends, however many awaits later:
//   * a page names its model on each message it posts (bridge.ts), and the host relay wraps that send in withModelIdentity;
//   * a registered command runs through ONE wrapper (registerPersonCommand in extension.ts) that calls personAction() with the model the host showed at the moment of the click;
//   * a tree node carries the model it was fetched for (`TreeNode.model`), so a context menu left open while another model opens still names the one the person clicked on.
// The EARLIEST capture wins: a scope that is already open is never replaced (an inner withModelIdentity or personAction runs inside it). rebindAction() is the ONE explicit exit,
// for an action that opens a model on purpose and then writes into it. outsideAnyAction() is the other: a long-lived thing started from inside an action (the engine pipe, whose
// reader would otherwise carry that action's model for ever) starts outside every scope.
//
// vscode-jsonrpc builds a request and hands it to the writer in the same synchronous step of sendRequest (connection.js, `messageWriter.write(requestMessage)` inside the Promise
// executor, before its first await). The writer reads the scope at that moment, so a value set around the call is on that request and nothing else needs to be threaded.
import { AsyncLocalStorage } from 'node:async_hooks';
import type { Message, MessageWriter, Event } from 'vscode-jsonrpc/node';

/** The wire name of the member (the engine reads the same: RequestModelIdentity.WireName). */
export const MODEL_IDENTITY_MEMBER = 'modelIdentity';
/** The wire name of the second member (the engine reads the same: RequestModelIdentity.StrictWireName). */
export const NAMES_MODEL_MEMBER = 'namesModel';
/** The wire name of the third member (the engine reads the same: SignInPress.WireName): `pressed: true` says a person pressed a button for this request, so a sign-in window may open for it. */
export const PRESSED_MEMBER = 'pressed';

const presses = new AsyncLocalStorage<boolean>();
/** Run `send` (which must call `sendRequest` synchronously) as a person's press when `pressed` is true; the request it makes then carries `pressed: true`. Otherwise it is automatic. */
export function withPress<T>(pressed: boolean, send: () => T): T {
    return pressed ? presses.run(true, send) : send();
}

/** The sentence the engine refuses a write with when the model changed under it (ModelFence.Refusal): one text, so the person reads the same words wherever it is shown. */
export const MODEL_REFUSAL = 'This page was showing another model than the one open now, so nothing was changed. Anything you typed is kept.';

/** The shape an identity must have to be worth naming: the engine's own (`file:`, `live:`, `desktop:`, `unsaved:`). A fallback key a page built for itself is not one, and
 *  naming it would make the engine refuse every write, so it names nothing (an older engine that does not send the field). Kept equal to fenceFor in webview/src/drafts.ts. */
export const fenceIdentity = (model: unknown): string | undefined => (typeof model === 'string' && /^(file|live|desktop|unsaved):/.test(model) ? model : undefined);

/** What one action knows: the model it started on. `identity` is undefined when it started with no model open, or on a shape the engine does not name; the writer then names none. */
interface ActionScope { readonly identity: string | undefined; }
const scopes = new AsyncLocalStorage<ActionScope>();

/** The model the action this code runs in started on, or undefined (no action, or one that started on no model). */
export const actionModelIdentity = (): string | undefined => scopes.getStore()?.identity;
/** True while this code runs inside an action's scope. */
export const insideAnyAction = (): boolean => scopes.getStore() !== undefined;

/** Run `send` (which must call `sendRequest` synchronously) with `identity` named on the request it makes. Inside an action the action's earlier capture wins and `identity` is
 *  ignored: a page message relayed while a command is mid-flight cannot rewrite the model that command started on. Without an identity it names none. */
export function withModelIdentity<T>(identity: unknown, send: () => T): T {
    if (scopes.getStore()) return send();
    return scopes.run({ identity: fenceIdentity(identity) }, send);
}

/** The ONE explicit exit from the earliest-capture rule: run `fn` as an action on `identity`, even inside another action's scope. For an action that opens a model on purpose and
 *  then writes into it, so its writes name the model it opened and not the one it started on. */
export function rebindAction<T>(identity: unknown, fn: () => T): T {
    return scopes.run({ identity: fenceIdentity(identity) }, fn);
}

/** Run `fn` outside every action's scope: whatever it starts (a socket, a timer, a child process) carries no model. */
export function outsideAnyAction<T>(fn: () => T): T {
    return scopes.exit(fn);
}

/** The models the arguments of a command carry: a tree node, or a list of them, each stamped with the model it was fetched for. */
function modelsOf(args: readonly unknown[]): string[] {
    const found = new Set<string>();
    const look = (v: unknown, depth: number): void => {
        if (!v || typeof v !== 'object' || depth > 2) return;
        if (Array.isArray(v)) { for (const x of v) look(x, depth + 1); return; }
        const m = fenceIdentity((v as { model?: unknown }).model);
        if (m) found.add(m);
    };
    for (const a of args) look(a, 0);
    return [...found];
}

/** Open the scope of one person's action (a command the person ran). The model is, first, the one a node argument was fetched for; else the one the host was showing when the
 *  action started (`hostIdentity`). Nodes from two different models are refused before anything is sent. A scope that is already open is kept (earliest capture wins). */
export function personAction<T>(args: readonly unknown[], hostIdentity: unknown, run: () => T, refuse?: (message: string) => void): T | undefined {
    if (scopes.getStore()) return run();
    const nodes = modelsOf(args);
    if (nodes.length > 1) { refuse?.(MODEL_REFUSAL); return undefined; }
    return scopes.run({ identity: nodes[0] ?? fenceIdentity(hostIdentity) }, run);
}

const isRequest = (m: Message): boolean => typeof (m as { method?: unknown }).method === 'string' && (m as { id?: unknown }).id !== undefined;

/** A writer that stamps the model on a request as it goes out (and says it names one on every request), and passes everything else through untouched. A notification is not a write. */
export class FencedMessageWriter implements MessageWriter {
    constructor(private readonly inner: MessageWriter) {}
    get onError(): Event<[Error, Message | undefined, number | undefined]> { return this.inner.onError; }
    get onClose(): Event<void> { return this.inner.onClose; }
    write(msg: Message): Promise<void> {
        if (isRequest(msg)) {
            const m = msg as unknown as Record<string, unknown>;
            m[NAMES_MODEL_MEMBER] = true;
            const named = actionModelIdentity();
            if (named) m[MODEL_IDENTITY_MEMBER] = named;
            if (presses.getStore() === true) m[PRESSED_MEMBER] = true;
        }
        return this.inner.write(msg);
    }
    end(): void { this.inner.end(); }
    dispose(): void { this.inner.dispose(); }
}
