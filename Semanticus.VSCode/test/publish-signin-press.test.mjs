// Nothing automatic opens a sign-in window (hero22 H1). A page's button says so on its request (`pressed: true`), the host writer carries
// the word to the engine, and the engine opens a press for that request only (SignInPress). Every read a page starts by itself is a plain rpc.
// This pins the wiring as source, where the wiring is the thing being pinned.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (...p) => readFileSync(resolve(root, ...p), 'utf8');

const fence = read('src/modelFence.ts');
const engine = read('..', 'Semanticus.Engine', 'Connect', 'SignInPress.cs');
const wire = /PRESSED_MEMBER = '([a-zA-Z]+)'/.exec(fence)?.[1];
assert.equal(wire, 'pressed');
assert.ok(engine.includes(`WireName = "${wire}"`), 'the host and the engine name the same request member');
assert.ok(read('..', 'Semanticus.Engine', 'RpcServer.cs').includes('SignInPress.WireName'), 'the RPC door reads it');
assert.ok(/presses\.getStore\(\) === true\) m\[PRESSED_MEMBER\] = true/.test(fence), 'the writer stamps it only inside a press');
assert.ok(read('src/extension.ts').includes('withPress(pressed,'), 'the host relay runs a pressed page message as a press');
assert.ok(/postToHostStamped\(pressed \?/.test(read('webview/src/bridge.ts')), 'only rpcPressed sets it on the message');

// The new-model card reads workspaces by itself when it opens (plain rpc) and signs in only on Try again.
const card = read('webview/src/publish-changes.tsx');
assert.ok(card.includes("(pressed ? rpcPressed : rpc)<FabricWorkspace[]>('listWorkspaces'"), 'the card reads as a press only when pressed');
assert.ok(card.includes('useEffect(() => { void load(); }, [])'), 'opening the card is automatic');
assert.ok(card.includes('onClick={() => void load(true)}>'), 'the failure button (Sign in or Try again, below) is the person\'s press');
assert.ok(!/rpcPressed<[^>]*>\('listWorkspaceModels'/.test(card), 'the name suggestion never signs in');

// The Connections picker: a deep link opens it automatic, a button opens it as a press; choosing another way to sign in and picking a workspace are presses.
const pick = read('webview/src/connectpickers.tsx');
assert.ok(pick.includes('void loadWorkspaces(pressedFirst || !first)'), 'the first read is automatic for a deep link and a press when a button opened the picker');
assert.ok(pick.includes("rpcPressed<WorkspaceModel[]>('listWorkspaceModels'"), 'picking a workspace is a press');
// The failure line says "Press Sign in" when the workspaces could not be read for a sign-in reason, so the one button on that line is labelled Sign in
// (verifier v-signin-auto); any other failure keeps Try again. It is still the pressed read.
const cardLf = card.replace(/\r\n/g, '\n');
assert.ok(/workspaces\.kind === 'failed' && <button [^\n]*onClick=\{\(\) => void load\(true\)\}>\{isSignInError\(workspaces\.message\) \? 'Sign in' : 'Try again'\}<\/button>/.test(cardLf),
  'the new-model card labels its failure button Sign in for a sign-in error and Try again otherwise, and it reads as a press');
assert.ok(cardLf.includes("import { isSignInError } from './authcopy';"), 'the card uses the shared sign-in detector');
console.log('publish-signin-press: ok');
