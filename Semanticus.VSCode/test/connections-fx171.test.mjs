// fx171, from the connections tester round cx1.
// Gap 8: a failed History row names the model, the account and the tenant when they are known.
// Gap 11: Escape closes an open Jobs model picker (Editing, Tests and queries).
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader } from './helpers/webview-load.mjs';
import { makePage, text, findAll, settleAll } from './helpers/drafts-mount.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const hub = readFileSync(resolve(root, 'webview', 'src', 'connectionshub.tsx'), 'utf8');
const load = makeLoader({});

test('FX174: the workspace service identity setup says Sign-in failed after authentication fails', async () => {
  const pg = makePage({ './bridge': { rpc: async () => ({ ready: true, requirements: [] }) } });
  const { SignInCheck } = pg.load('connectpickers.tsx');
  const h = pg.mount(() => SignInCheck({ authMode: 'serviceprincipal', tenant: 'wrong.example', failure: 'The service identity could not sign in.' }));
  await new Promise((resolve) => setTimeout(resolve, 300)); await settleAll();
  assert.ok(findAll(h.result, (el) => text(el) === 'Sign-in failed').length > 0);
  assert.equal(findAll(h.result, (el) => text(el) === 'Ready').length, 0);
  h.unmount();
});

test('a failed History row names the model, the account and the tenant', () => {
  const { historySubLine, historyKindLabel } = load('historyrow');
  const e = { kind: 'open', ok: false, account: 'alice@contoso.com', tenantId: '11111111-aaaa-4aaa-8aaa-111111111111',
    database: 'Contoso Sales', modelName: 'Contoso Sales', endpoint: 'powerbi://x/ws', detail: 'signing in or opening the model failed; the saved account was left unchanged' };
  assert.equal(historyKindLabel(e), 'Open failed');
  const line = historySubLine(e);
  assert.match(line, /Contoso Sales/, 'the model');
  assert.match(line, /alice@contoso\.com/, 'the account');
  assert.match(line, /11111111-aaaa-4aaa-8aaa-111111111111/, 'the tenant');
  assert.match(line, /signing in or opening the model failed/, 'the reason stays');
});

test('a failed row with no account still names the model and the tenant', () => {
  const { historySubLine } = load('historyrow');
  const line = historySubLine({ kind: 'signin', ok: false, tenantId: '11111111-aaaa-4aaa-8aaa-111111111111', database: 'Contoso Sales', detail: 'sign-in failed before an account was chosen (the sign-in did not complete).' });
  assert.match(line, /Contoso Sales/);
  assert.match(line, /11111111-aaaa-4aaa-8aaa-111111111111/);
});

test('the hub History view uses the shared row text', () => {
  assert.match(hub, /from '\.\/historyrow'/);
});

test('Escape on an open Jobs picker closes it and stops there', () => {
  const { JobPicker } = load('connectionshub-parts');
  let toggled = 0, stopped = 0;
  const tree = JobPicker({ job: 'tests', current: 'Contoso Sales', options: [], open: true, onToggle: () => { toggled++; }, onChoose: () => undefined });
  assert.equal(typeof tree.props.onKeyDown, 'function', 'the picker wrapper listens for keys');
  tree.props.onKeyDown({ key: 'Enter', stopPropagation: () => { stopped++; }, nativeEvent: { stopPropagation: () => { stopped++; } } });
  assert.equal(toggled, 0);
  tree.props.onKeyDown({ key: 'Escape', stopPropagation: () => { stopped++; }, nativeEvent: { stopPropagation: () => { stopped++; } } });
  assert.equal(toggled, 1, 'Escape closes the open picker');
  assert.ok(stopped > 0, 'the key does not reach the hub, which would otherwise treat the focused open picker as its own list');
  const closed = JobPicker({ job: 'tests', current: 'x', options: [], open: false, onToggle: () => { toggled++; }, onChoose: () => undefined });
  closed.props.onKeyDown({ key: 'Escape', stopPropagation: () => undefined, nativeEvent: { stopPropagation: () => undefined } });
  assert.equal(toggled, 1, 'a closed picker ignores Escape so the hub can close');
});

test('the sign-in setup chip does not read Ready after a failed sign-in', () => {
  assert.match(hub, /signInFailedNow = !!error && /, 'the preflight knows when the sign-in just failed');
  assert.match(hub, /!prereq\.ready \? 'Not ready' : signInFailedNow \? 'Sign-in failed' : 'Ready'/);
});
