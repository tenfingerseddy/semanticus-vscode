// The first-run choice (1.4, direction decision 10): "How do you want to work?" in the Studio, once per machine.
// It replaces the old "Connect your assistant?" toast. One press of Connect Claude Code writes the connection
// file AND installs the skill, then says what to type next.
//
//   1. The HOST half (src/firstRun.ts) is pure and runs here against a fake globalState and fake steps: the
//      once-flag, "never again after a connect", the two steps run in order, and a failed step names itself.
//   2. The CARD (webview/src/firstrun.tsx) is rendered with react-dom/server and the words are asserted.
//   3. The Plan tour gains one step, "Connect your assistant", only while not connected.
//   4. The wiring in extension.ts: the toast is gone, the messages are answered, the setting exists.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const h = React.createElement;

const STUBS = {
  './bridge': {
    rpc: async () => ({ rows: [] }),
    requestGuideFlags: async () => ({ planTour: false, whatChanged: false }),
    setGuideFlag: () => {},
    runHostCommand: () => {},
    requestFirstRun: async () => ({ show: true, connected: false }),
    firstRunConnect: async () => ({ ok: true }),
    firstRunClose: () => {},
  },
  './pro': { useTier: () => 'pro', upgradeToPro: () => {} },
  './plan-guide.css': {}, './firstrun.css': {},
};
const cache = new Map();
function load(file, stubs = STUBS) {
  const full = /\.(ts|tsx|mjs|js)$/.test(file) ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (stubs === STUBS && cache.has(full)) return cache.get(full);
  const module = { exports: {} };
  if (stubs === STUBS) cache.set(full, module.exports);
  const localRequire = (name) => stubs[name] ?? (name.startsWith('.') ? load(resolve(dirname(full), name), stubs) : req(name));
  const out = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true }, fileName: full,
  }).outputText;
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', out)(localRequire, module, module.exports);
  if (stubs === STUBS) cache.set(full, module.exports);
  return module.exports;
}
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();

// ---- 1. the host half ------------------------------------------------------------------------------------
{
  const F = load(join(root, 'src', 'firstRun.ts'), {});
  const store = new Map();
  const state = { get: (k) => store.get(k), update: async (k, v) => { store.set(k, v); } };
  assert.equal(F.FIRST_RUN_KEY, 'semanticus.guide.firstRun', 'one once-flag, in the same family as the other guide flags');
  assert.deepEqual(F.readFirstRun(state, false), { show: true, connected: false }, 'a fresh machine with no connection is asked');
  assert.deepEqual(F.readFirstRun(state, true), { show: false, connected: true }, 'a folder that is already wired is never asked');
  await F.markFirstRun(state, 'hand', () => '2026-10-10T10:00:00.000Z');
  assert.deepEqual(store.get('semanticus.guide.firstRun'), { done: true, mode: 'hand', at: '2026-10-10T10:00:00.000Z' });
  assert.deepEqual(F.readFirstRun(state, false), { show: false, connected: false }, 'By hand closes it for good on this machine');

  const order = [];
  const ok = (name) => async () => { order.push(name); return { ok: true }; };
  const done = await F.runFirstRunConnect({ writeConnection: ok('connection'), installSkill: ok('skill'), markDone: async () => { order.push('flag'); } });
  assert.deepEqual(order, ['connection', 'skill', 'flag'], 'one press does the connection, then the skill, then closes the card for good');
  assert.deepEqual(done, { ok: true });

  const order2 = [];
  const failed = await F.runFirstRunConnect({
    writeConnection: async () => { order2.push('connection'); return { ok: false, error: 'The connection file is not valid JSON.' }; },
    installSkill: ok('skill'), markDone: async () => { order2.push('flag'); },
  });
  assert.deepEqual(order2, ['connection'], 'a connection that was not written stops there: no skill, and the card stays open to try again');
  assert.deepEqual(failed, { ok: false, error: 'The connection file is not valid JSON.' });

  const skillFail = await F.runFirstRunConnect({
    writeConnection: ok('c'), installSkill: async () => ({ ok: false, error: 'No write access to the skills folder.' }), markDone: async () => {},
  });
  assert.equal(skillFail.ok, true, 'the connection is the part that matters: a skill that did not install does not undo it');
  assert.match(skillFail.note, /skill/i, 'but the card says so');

  // The driver mode is written the contract's way (section 4): the workspace key the driver bar reads.
  assert.equal(F.DRIVE_MODE_KEY, 'semanticus.studio.driveMode');
  assert.equal(F.driveModeFor('assistant'), 'claudeDrives', 'Connect writes claudeDrives');
  assert.equal(F.driveModeFor('hand'), 'iDrive', 'By hand writes iDrive');
  assert.equal(F.driveModeFor('tour'), undefined, 'a look around chooses nothing: the bar decides from the connection, as before');
  assert.equal(F.choiceFor('assistant'), 'assistant'); assert.equal(F.choiceFor('hand'), 'hand'); assert.equal(F.choiceFor('tour'), 'tour');
  assert.equal(F.choiceFor('x'), 'assistant', 'anything else is the default');
  const ws = new Map();
  await F.writeDriveMode({ get: (k) => ws.get(k), update: async (k, v) => { ws.set(k, v); } }, 'assistant');
  assert.equal(ws.get('semanticus.studio.driveMode'), 'claudeDrives');
  await F.writeDriveMode({ get: (k) => ws.get(k), update: async (k, v) => { ws.set(k, v); } }, 'hand');
  assert.equal(ws.get('semanticus.studio.driveMode'), 'iDrive');
  await F.writeDriveMode({ get: (k) => ws.get(k), update: async (k, v) => { ws.set(k, v); } }, 'tour');
  assert.equal(ws.get('semanticus.studio.driveMode'), 'iDrive', 'and a look around leaves what is there alone');
}

// ---- 2. the card -----------------------------------------------------------------------------------------
{
  const R = load(join(webview, 'src', 'firstrun.tsx'));
  const props = { mode: 'assistant', onMode: () => {}, onConnect: () => {}, onClose: () => {}, onAround: () => {}, error: null, note: null };
  const choose = readable(renderToStaticMarkup(h(R.FirstRunCard, { ...props, stage: 'choose' })));
  for (const w of ['How do you want to work?', 'With your assistant', 'recommended', 'By hand', 'Connect Claude Code']) assert.ok(choose.includes(w), `choose stage says "${w}": ${choose}`);
  const raw = renderToStaticMarkup(h(R.FirstRunCard, { ...props, stage: 'choose' }));
  assert.ok(choose.includes('Show me around (3 minutes)'), 'the offer to be shown around is on the choice');
  assert.ok(/aria-checked="true"[^>]*>[^]*?With your assistant/.test(raw), 'With your assistant is the selected choice');
  assert.ok(!/aria-checked="true"[^>]*>[^]*?By hand/.test(raw.split('With your assistant')[0]), 'and By hand is not');
  const hand = readable(renderToStaticMarkup(h(R.FirstRunCard, { ...props, mode: 'hand', stage: 'choose' })));
  assert.ok(hand.includes('Start working') && !hand.includes('Connect Claude Code'), `choosing By hand swaps the button for a plain way out: ${hand}`);
  const working = readable(renderToStaticMarkup(h(R.FirstRunCard, { ...props, stage: 'working' })));
  assert.ok(working.includes('Connecting'), working);
  const connected = readable(renderToStaticMarkup(h(R.FirstRunCard, { ...props, stage: 'done' })));
  assert.ok(connected.includes('Open Claude Code in this folder and say:'), connected);
  assert.ok(connected.includes(R.FIRST_SAY), 'it shows the first thing to say');
  assert.ok(/restart/i.test(connected) && /Claude Code/.test(connected), 'and says plainly that Claude Code needs a restart');
  const withNote = readable(renderToStaticMarkup(h(R.FirstRunCard, { ...props, stage: 'done', note: 'The skill did not install.' })));
  assert.ok(withNote.includes('The skill did not install.'));
  const err = readable(renderToStaticMarkup(h(R.FirstRunCard, { ...props, stage: 'choose', error: 'The connection file is not valid JSON.' })));
  assert.ok(err.includes('The connection file is not valid JSON.'), 'a failure says what happened and the card stays open');
  for (const text of [choose, hand, working, connected, err, R.FIRST_SAY]) {
    assert.ok(!/—/.test(text), 'no em dash'); assert.ok(!/one[- ]click/i.test(text), 'never "one click"'); assert.ok(!/\.mcp\.json/.test(text), 'no dotfile in the words a person reads');
  }
}

// ---- 3. the tour step ------------------------------------------------------------------------------------
{
  const G = load(join(webview, 'src', 'plan-guide.tsx'));
  assert.equal(G.PLAN_TOUR.length, 6, 'the tour a connected person sees is unchanged');
  assert.equal(G.tourSteps(true).length, 6);
  const t = G.tourSteps(false);
  assert.equal(t.length, 7, 'a person who has not connected gets one more step');
  const i = t.findIndex((s) => s.title === 'Connect your assistant');
  assert.ok(i > 0, 'it is there'); assert.equal(t[i + 1].anchor[0], 'drive-switch', 'just before the step about its tasks');
  assert.deepEqual(t[i].anchor, ['assistant-chip'], 'anchored to the header Your assistant button');
  assert.ok(t[i].body && !/—/.test(t[i].body) && !/one[- ]click/i.test(t[i].body));
  assert.deepEqual(t.map((_, k) => G.tourView(k, t)).slice(-1), ['history'], 'the last step still opens History');
}

// ---- 3b. the tour waits for the choice ------------------------------------------------------------------
{
  const calls = { n: 0 };
  const stubs = { ...STUBS, './bridge': { ...STUBS['./bridge'], requestFirstRun: async () => { calls.n += 1; return { show: true, connected: false }; } } };
  const Gate = load(join(webview, 'src', 'firstrun-gate.ts'), stubs);
  let released = false;
  const waiting = Gate.whenFirstRunClosed().then(() => { released = true; });
  await Promise.resolve(); await new Promise((r) => setTimeout(r, 10));
  assert.equal(released, false, 'the tour is held while the first-run choice is open');
  await Gate.firstRunOnce(); assert.equal(calls.n, 1, 'one question to the host per page, shared');
  Gate.markFirstRunClosed(); await waiting;
  assert.equal(released, true, 'and released when it closes');
  const free = load(join(webview, 'src', 'firstrun-gate.ts'), { ...STUBS, './bridge': { ...STUBS['./bridge'], requestFirstRun: async () => ({ show: false, connected: true }) } });
  await free.whenFirstRunClosed();
}

// ---- 4. the wiring ---------------------------------------------------------------------------------------
{
  const ext = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
  assert.ok(!/Connect your assistant to this Semanticus session\?/.test(ext), 'the old toast is gone');
  assert.ok(!/maybeOfferConnectAiAssistant/.test(ext), 'and its function with it');
  assert.ok(/msg\?\.type === 'getFirstRun'[\s\S]{0,500}readFirstRun\(extCtx\.globalState/.test(ext), 'the host answers the once-flag from globalState');
  assert.ok(/msg\?\.type === 'firstRunConnect'[\s\S]{0,900}runFirstRunConnect\(/.test(ext), 'one press runs the connect-and-skill sequence');
  assert.ok(/msg\?\.type === 'firstRunClose'[\s\S]{0,200}finishFirstRun\(/.test(ext) && /async function finishFirstRun[\s\S]{0,200}markFirstRun\(/.test(ext), 'By hand writes the flag');
  const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));
  assert.equal(pkg.contributes.configuration.properties['semanticus.workMode'], undefined, 'no separate setting: the mode is the contract key');
  assert.ok(!/semanticus\.workMode|WORK_MODE_SETTING/.test(ext), 'and nothing in the host writes one');
  assert.ok(/async function finishFirstRun[\s\S]{0,500}writeDriveMode\(extCtx\.workspaceState/.test(ext), 'finishing writes the driver mode to workspaceState');
  const b = readFileSync(join(webview, 'src', 'bridge.ts'), 'utf8');
  assert.ok(/export function requestFirstRun/.test(b) && /export function firstRunConnect/.test(b) && /export function firstRunClose/.test(b));
  const act = readFileSync(join(webview, 'src', 'activity.tsx'), 'utf8');
  assert.ok(/data-tour="assistant-chip"/.test(act), 'the header chip carries the tour anchor');
  const app = readFileSync(join(webview, 'src', 'App.tsx'), 'utf8');
  assert.ok(/<FirstRun\b/.test(app), 'the Studio mounts the choice');
  const fr = readFileSync(join(webview, 'src', 'firstrun.tsx'), 'utf8');
  assert.ok(/import \{ startTour, useLearn \} from '\.\/studio\/learn-state'/.test(fr) && /startTour\('plan'\)/.test(fr), 'Show me around starts the Plan chapter of the Learn tour');
  assert.ok(!existsSync(join(webview, 'src', 'studio-tour-shim.ts')), 'the stand-in for the tour is gone: the real startTour is used');
}

// ---- 5. N2 (hero16): Connect does the same full job from every door -------------------------------------
{
  const F = load(join(root, 'src', 'firstRun.ts'), {});
  const order = [];
  const step = (name) => async () => { order.push(name); return { ok: true }; };
  const said = [];
  const res = await F.connectAssistant({ writeConnection: step('connection'), installSkill: step('skill'),
    markDone: async () => { order.push('mode'); }, showSay: (r) => { order.push('say'); said.push(r); } });
  assert.deepEqual(order, ['connection', 'skill', 'mode', 'say'], 'the header press writes the file, installs the skills, sets the mode and shows the say card, all in one go');
  assert.equal(res.ok, true); assert.equal(said.length, 1);
  const order2 = [];
  const bad = await F.connectAssistant({ writeConnection: async () => ({ ok: false, error: 'No folder.' }), installSkill: step('skill'),
    markDone: async () => { order2.push('mode'); }, showSay: () => { order2.push('say'); } });
  assert.equal(bad.ok, false); assert.deepEqual(order2, [], 'a connection that was not written shows no say card and changes no mode');
  const note = await F.connectAssistant({ writeConnection: step('c'), installSkill: async () => ({ ok: false, error: 'No access.' }), markDone: async () => {}, showSay: (r) => said.push(r) });
  assert.match(said.at(-1).note, /skill/i, 'a skill that did not install still shows the say card, with the note');
  assert.equal(note.ok, true);
  // The default mode after Connect is Claude drives; a person who is already wired and chose a mode keeps it.
  assert.equal(F.modeAfterConnect(false, undefined), 'claudeDrives', 'first connect: Claude drives (Kane 10 Oct)');
  assert.equal(F.modeAfterConnect(false, 'iDrive'), 'claudeDrives', 'By hand earlier, then Connect from the header: Claude drives');
  assert.equal(F.modeAfterConnect(true, 'iDrive'), 'iDrive', 'pressing Connect again on a wired folder does not undo the person\'s choice');
  assert.equal(F.modeAfterConnect(true, undefined), 'claudeDrives');

  // The host: the header and the palette command run the same sequence, with no second toast.
  const ext = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
  const cmd = ext.slice(ext.indexOf('async function connectClaudeCodeCmd'), ext.indexOf('/// The first-run choice is finished'));
  assert.ok(/connectAssistant\(/.test(cmd) && /installClaudeCodeSkill/.test(cmd), 'the command runs the shared connect sequence with the skills');
  assert.ok(/postFirstRunSay|firstRunSay/.test(ext), 'the host tells the Studio to show the say card');
  assert.ok(!/Install assistant skills/.test(cmd) && !/Nothing is connected yet/.test(cmd), 'the second-press toast is gone');
  assert.ok(/modeAfterConnect\(/.test(ext), 'the mode is set by the shared rule');
  const b = readFileSync(join(webview, 'src', 'bridge.ts'), 'utf8');
  assert.ok(/export function onFirstRunSay/.test(b) && /msg\.type === 'firstRunSay'/.test(b), 'the page hears the say message');

  // Leave from the tour returns to the card while the person has not connected.
  const G = load(join(webview, 'src', 'firstrun-gate.ts'));
  assert.equal(G.returnsToCard({ fromCard: true, phase: 'idle', connected: false, open: false }), true, 'Leave after Show me around: the card comes back');
  assert.equal(G.returnsToCard({ fromCard: true, phase: 'running', connected: false, open: false }), false, 'not while the tour plays');
  assert.equal(G.returnsToCard({ fromCard: true, phase: 'idle', connected: true, open: false }), false, 'not once connected');
  assert.equal(G.returnsToCard({ fromCard: false, phase: 'idle', connected: false, open: false }), false, 'a tour started from Help does not raise the card');
  assert.equal(G.returnsToCard({ fromCard: true, phase: 'idle', connected: false, open: true }), false, 'not over a card that is already open');
  const fr = readFileSync(join(webview, 'src', 'firstrun.tsx'), 'utf8');
  assert.ok(/returnsToCard\(/.test(fr) && /onFirstRunSay\(/.test(fr), 'the card component uses both');
}

console.log('first-run: choice, one-press connect, tour step and wiring checks passed');
