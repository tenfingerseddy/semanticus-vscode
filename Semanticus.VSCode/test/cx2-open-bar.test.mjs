// cx2 (Kane, 3 Nov, 1.7.3): "we shouldnt have to scroll to see connect", and the open "keeps freezing up or wont do it". Kane picked
// option A of the Connections mock-up at 11:44: one bar fixed at the bottom of the dialog. It holds the one Open (Connect on the
// Workspace model screen), the one account place with Change, and while a model opens, "Step n of 5 · <step>" and Cancel.
// This file proves the words and the rules (openbar-model.ts, executed), the bar drawn state by state (react-dom/server), and the
// wiring read off the source. The places on screen (in view with 29 saved models and 29 workspaces) are measured in a real browser by
// tools/uishot/cx2-bar-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader, req } from './helpers/webview-load.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (...p) => readFileSync(resolve(root, ...p), 'utf8');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const h = React.createElement;
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/&quot;/g, '"').replace(/\s+/g, ' ').trim();
const testids = (html) => [...html.matchAll(/data-testid="([^"]+)"/g)].map((m) => m[1]);

assert.ok(existsSync(join(root, 'webview', 'src', 'openbar-model.ts')), 'the bar keeps its rules in openbar-model.ts');
assert.ok(existsSync(join(root, 'webview', 'src', 'openbar.tsx')), 'the bar is one shared view, openbar.tsx');
const load = makeLoader({ './bridge': { rpc: async () => null, onProgress: () => () => {} } });
const M = load('openbar-model');
const B = load('openbar');

// ---- 1. The rules ---------------------------------------------------------------------------------------------------
{
  const t0 = 1_000_000;
  let run = M.startOpenRun('Sales', 'in Contoso Sales', 5, t0);
  assert.equal(run.step, 0);
  assert.equal(M.openStepLine(run, t0), 'Starting the open · 0 s', 'before the engine names a step, the bar says the open started');
  const ev = (done, note, extra = {}) => ({ opKey: 'open', runId: 'r1', done, total: 5, note, origin: 'human', cancellable: true, ...extra });
  run = M.foldOpenProgress(run, ev(1, 'Signing in'), t0 + 100);
  run = M.foldOpenProgress(run, ev(2, 'Connecting to the workspace'), t0 + 1400);
  assert.equal(run.runId, 'r1', 'the first event names the run Cancel stops');
  assert.equal(M.openStepLine(run, t0 + 3400), 'Step 2 of 5 · Connecting to the workspace · 3 s');
  assert.deepEqual(M.openPips(run), ['done', 'now', 'next', 'next', 'next'], 'five equal marks, never a percent');
  // A late event never moves the step back; another run's events and an assistant's open are not this bar's.
  assert.equal(M.foldOpenProgress(run, ev(1, 'Signing in'), t0 + 3500).step, 2);
  assert.equal(M.foldOpenProgress(run, ev(3, 'Reading the model definition', { runId: 'other' }), t0 + 3500).step, 2);
  const fresh = M.startOpenRun('Sales', 'in Contoso Sales', 5, t0);
  assert.equal(M.foldOpenProgress(fresh, ev(1, 'Signing in', { origin: 'agent', runId: 'a9' }), t0).runId, null, 'an assistant open is not the window\'s');
  assert.equal(M.foldOpenProgress(run, { opKey: 'deploy_live', runId: 'r1', done: 3, total: 0, note: 'x' }, t0).step, 2);
  // Once the model is open the engine says Cancel can no longer change nothing; the bar stops offering it.
  const late = M.foldOpenProgress(run, ev(5, 'Connecting for queries', { cancellable: false }), t0 + 8000);
  assert.equal(late.cancellable, false);
  // An event with no words ends the steps; the run stays until the open answers.
  assert.equal(M.foldOpenProgress(late, ev(5, null), t0 + 9000).ended, true);
  // The sign-in step that waits on the browser says so (tester note 4), only for a browser sign-in and after a moment.
  const signing = M.foldOpenProgress(M.startOpenRun('Sales', 'in Contoso Sales', 5, t0), ev(1, 'Signing in'), t0);
  assert.equal(M.browserWaitLine(signing, t0 + 500, true), null);
  assert.match(M.browserWaitLine(signing, t0 + 4000, true), /^Finish the sign-in in your browser\./);
  assert.equal(M.browserWaitLine(signing, t0 + 4000, false), null, 'a service identity has no browser');
  assert.equal(M.stoppedLine('Sales'), 'Stopped. Sales did not open, and nothing changed.');
  assert.equal(M.pendingLine('Gold'), 'Cancel this open to open Gold instead.');
  assert.match(M.tooLateLine('Sales'), /Sales was already open/);
  for (const s of [M.stoppedLine('A'), M.pendingLine('A'), M.tooLateLine('A'), M.BROWSER_WAIT]) assert.doesNotMatch(s, /\u2014|XMLA|endpoint|dataset/i);
}

// ---- 2. The bar, state by state -------------------------------------------------------------------------------------
{
  const noop = () => {};
  const base = { pickName: 'Returns', pickWhere: 'in Fabrikam Retail', account: 'megan@fabrikam.com', accountWhy: 'used last time', onChange: noop,
    action: 'Open', actTestId: 'hub-open', canAct: true, onAct: noop, run: null, now: 0, onCancel: noop, emptyText: 'Pick a model' };
  const draw = (p) => renderToStaticMarkup(h(B.OpenBar, { ...base, ...p }));

  const idle = draw({});
  assert.match(readable(idle), /Returns in Fabrikam Retail/);
  assert.match(readable(idle), /Open as megan@fabrikam\.com \(used last time\) Change/, 'the one account place, with Change');
  assert.equal(testids(idle).filter((t) => t === 'hub-open').length, 1, 'the bar holds the one Open');
  assert.ok(testids(idle).includes('hub-bar-change'));
  // The Workspace model screen says Connect, and "Connect as" (tester note 6: one word for the action).
  const ws = draw({ action: 'Connect', actTestId: 'pick-connect', accountWhy: null, account: 'avery@contoso.com' });
  assert.match(readable(ws), /Connect as avery@contoso\.com Change/);
  assert.ok(testids(ws).includes('pick-connect'));
  // A local file needs no account and says so.
  assert.match(readable(draw({ account: null, localNote: 'Opens from this computer. No account is needed.' })), /Opens from this computer\. No account is needed\./);
  // Nothing picked: the bar says what to do, and Open waits.
  const none = draw({ pickName: null, canAct: false });
  assert.match(readable(none), /Pick a model/);
  assert.match(none, /data-testid="hub-open"[^>]*disabled|disabled[^>]*data-testid="hub-open"/);

  // While a model opens: its name, the step, five marks and Cancel. No Open beside it.
  const t0 = 5_000;
  let run = M.startOpenRun('Sales', 'in Contoso Sales', 5, t0);
  run = M.foldOpenProgress(run, { opKey: 'open', runId: 'r1', done: 3, total: 5, note: 'Reading the model definition', origin: 'human', cancellable: true }, t0 + 4000);
  const opening = draw({ run, now: t0 + 6000, pending: M.pendingLine('Gold') });
  const words = readable(opening);
  assert.match(words, /Opening Sales in Contoso Sales/);
  assert.match(words, /Step 3 of 5 · Reading the model definition · 6 s/);
  assert.match(words, /Cancel this open to open Gold instead\./);
  assert.ok(testids(opening).includes('hub-bar-cancel'));
  assert.ok(!testids(opening).includes('hub-open'), 'Open goes while an open runs');
  assert.equal((opening.match(/data-pip=/g) || []).length, 5);
  assert.doesNotMatch(opening, /%|width:\s*\d/, 'never a percent or a filled width');
  assert.match(opening, /aria-live="polite"/, 'the step is read out as it changes');
  // Once it cannot be stopped, Cancel goes and the bar says why.
  const late = draw({ run: { ...run, cancellable: false }, now: t0 + 7000 });
  assert.ok(!testids(late).includes('hub-bar-cancel'));
  assert.match(readable(late), /It can no longer be stopped/);
  // After a Cancel: the stopped line, and Open is back.
  const stopped = draw({ message: { kind: 'stop', text: M.stoppedLine('Sales') } });
  assert.match(readable(stopped), /Stopped\. Sales did not open, and nothing changed\./);
  assert.ok(testids(stopped).includes('hub-open'));
  // The account choice opens INSIDE the bar, above its line, so it is never out of view (cx1: it opened 426 px above the window).
  const pop = draw({ pop: h('div', { 'data-testid': 'hub-account-dialog' }, 'Which account') });
  assert.ok(testids(pop).indexOf('hub-account-dialog') < testids(pop).indexOf('hub-open'), 'the choice sits in the bar, above the Open line');
}

// ---- 3. The wiring --------------------------------------------------------------------------------------------------
{
  const hub = read('webview', 'src', 'connectionshub.tsx');
  const pickers = read('webview', 'src', 'connectpickers.tsx');
  const ext = read('src', 'extension.ts');
  const css = read('webview', 'src', 'styles.css');
  // The rows have no Open of their own: a press picks the row, a double-click opens it, and the bar holds the one Open.
  assert.doesNotMatch(hub, /data-testid="hub-row-open"/, 'one Open, in the bar');
  assert.match(hub, /onDoubleClick=/, 'a double-click on a row opens it at once');
  assert.match(hub, /OpenBar\(/, 'the list draws the shared bar');
  assert.match(pickers, /OpenBar\(/, 'the Workspace model screen draws the shared bar');
  // The account choice and the unsaved-changes question are drawn in the bar, not at the top of the scrolled list.
  assert.match(hub, /pop: switchTarget \?/, 'the account choice opens in the bar');
  // The live step: the hub listens to the engine's open steps, and Cancel asks the engine to stop the open.
  assert.match(hub, /onProgress\(/);
  assert.match(hub, /rpc<[^>]*>\('cancelOpen'|rpc\('cancelOpen'/);
  // The standalone Connections panel gets the steps too (the event used to go to Studio only).
  assert.match(ext, /connectionsPanel\?\.webview\.postMessage\(\{ type: 'progressDidChange'/);
  // Change on the Workspace model screen reads that account's workspaces again (tester note 1).
  assert.match(pickers, /const useAccount = \(/);
  assert.match(pickers, /useAccount[\s\S]{0,400}loadWorkspaces\(/);
  // A "Reading models" line while the model list loads (tester note 5).
  assert.match(read('webview', 'src', 'connectpickers-model.ts'), /modelsLooking: 'Reading models\.\.\.'/);
  // The page between the header and the bar scrolls; the bar does not.
  assert.match(css, /\.hub-bar\s*\{[^}]*flex:\s*none/);
  assert.match(css, /\.hub-frame \{[^}]*grid-template-rows: auto minmax\(0, 1fr\) auto;/, 'the frame has a row of its own for the bar');
  assert.match(css, /\.hub-pick-scroll \{[^}]*overflow-y: auto/, 'each workspace list scrolls in its own box');
}
console.log('cx2 open bar: ok');
