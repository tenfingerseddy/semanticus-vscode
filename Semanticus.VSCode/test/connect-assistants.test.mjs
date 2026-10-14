// Connect another assistant (1.5, lane connect-any): the picker in the header's assistant menu, run against the real model and view.
//   each state of an assistant reads in plain words: On this computer, Not found here, Connected, Needs an update
//   Connect is the accent button only for an assistant that is on this computer; Connected has nothing left to press
//   a press shows what happened, the one thing to do next, the copy note and the licence note, in that order
//   a refusal shows the engine's sentence and nothing else; every string is free of em dashes and "one click"
//   the Studio and the engine agree on the two wire names, and the menu item sits in the assistant chip
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { makeLoader, req, webview } from './helpers/webview-load.mjs';

const bridge = { rpc: async () => null };
const load = makeLoader({ './bridge': bridge, './connect-assistants.css': {} });
const M = load('connect-assistants-model.ts');
const V = load('connect-assistants.tsx');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

let failures = 0;
async function check(name, fn) {
  try { await fn(); console.log(`ok - ${name}`); } catch (e) { failures++; console.error(`not ok - ${name}\n${e.stack}`); }
}

const LIST = [
  { id: 'claude-desktop', name: 'Claude Desktop', found: true, state: 'notConnected' },
  { id: 'codex', name: 'Codex', found: true, state: 'connected' },
  { id: 'cursor', name: 'Cursor', found: false, state: 'notFound' },
  { id: 'claude-code', name: 'Claude Code (terminal)', found: true, state: 'outdated' },
];
const draw = (props) => renderToStaticMarkup(React.createElement(V.ConnectAssistantsView, { list: LIST, failed: false, busy: null, result: null, onPress: () => {}, onBack: () => {}, ...props }));
const tag = (html, label) => (html.match(new RegExp(`<button[^>]*aria-label="${label.replace(/[()]/g, '\\$&')}"[^>]*>`)) ?? [''])[0];
const text = (html) => html.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();

await check('each state reads in plain words with the right button', () => {
  const rows = LIST.map((a) => M.rowView(a));
  assert.deepEqual(rows.map((r) => [r.status, r.actionLabel, r.action]), [
    ['On this computer', 'Connect', 'connect'], ['On this computer', 'Connected', 'done'],
    ['Not found here', 'Connect', 'connect'], ['Needs an update', 'Update', 'update'],
  ]);
  assert.equal(rows[2].muted, true);
  assert.equal(rows[0].muted, false);
});

await check('the panel draws all four assistants, the lead line and a Back button', () => {
  const html = draw({});
  const t = text(html);
  for (const name of ['Claude Desktop', 'Codex', 'Cursor', 'Claude Code (terminal)']) assert.ok(t.includes(name), name);
  assert.ok(t.includes('Connect another assistant'));
  assert.ok(t.includes('changes nothing else in them'));
  assert.match(html, /aria-label="Connect Claude Desktop"/);
  assert.match(html, /aria-label="Update Claude Code \(terminal\)"/);
  // Connected has nothing left to press, and the word Connected shows once on its row: on the button, never in the line under the name (copy21)
  assert.match(tag(html, 'Connected Codex'), /aria-disabled="true"/);
  assert.equal((text(html).match(/Connected/g) ?? []).length, 1);
  assert.doesNotMatch(tag(html, 'Connect Claude Desktop'), /aria-disabled/);
  // only an assistant that is on this computer gets the accent button
  assert.match(tag(html, 'Connect Claude Desktop'), /sem-btn-primary/);
  assert.doesNotMatch(tag(html, 'Connect Cursor'), /sem-btn-primary/);
});

await check('a press shows the sentence, the next step, the copy note and the licence note in that order', () => {
  const r = { assistant: 'claude-desktop', name: 'Claude Desktop', ok: true, changed: true, message: 'Connected Claude Desktop.',
    nextStep: 'Quit Claude Desktop fully and open it again.', backupPath: '/x.semanticus-bak', licenceNote: 'Your Pro licence is saved in /h/.semanticus/license so Claude Desktop runs as Pro too.' };
  // The row's own button says Connected, so the sentence that only repeats it is not shown (copy21)
  assert.deepEqual(M.resultLines(r), [
    'Quit Claude Desktop fully and open it again.', 'A copy of the old file is next to it.', r.licenceNote]);
  const t = text(draw({ result: { id: 'claude-desktop', r } }));
  assert.ok(!t.includes('Connected Claude Desktop.'));
  assert.deepEqual(M.resultLines({ ...r, nextStep: undefined }), ['Connected Claude Desktop.', 'A copy of the old file is next to it.', r.licenceNote]);
  assert.ok(t.indexOf('A copy of the old file') < t.indexOf('runs as Pro too'));
});

await check('a refusal shows only the engine sentence', () => {
  const r = { assistant: 'cursor', name: 'Cursor', ok: false, changed: false, message: 'The config file of Cursor is not valid JSON, so Semanticus left it alone. Fix the file, then press Connect again.', nextStep: 'ignored', backupPath: 'ignored' };
  assert.deepEqual(M.resultLines(r), [r.message]);
  assert.ok(!draw({ result: { id: 'cursor', r } }).includes('data-ok'));
});

await check('a busy row says Connecting and every button waits', () => {
  const html = draw({ busy: 'claude-desktop' });
  assert.ok(text(html).includes('Connecting'));
  assert.equal((html.match(/<button[^>]*aria-disabled="true"/g) ?? []).length, 4);   // all four row buttons wait; Back stays pressable
});

await check('a list that failed to load says so in a sentence', () => {
  const t = text(draw({ list: null, failed: true }));
  assert.ok(t.includes(M.NOTHING_LISTED));
});

await check('no string has an em dash or "one click"', () => {
  const all = [M.PANEL_TITLE, M.PANEL_LEAD, M.NOTHING_LISTED, ...LIST.flatMap((a) => { const v = M.rowView(a); return [v.status, v.actionLabel]; })].join('\n');
  assert.ok(!/—|–/.test(all), 'dash');
  assert.ok(!/one[- ]click/i.test(all));
  for (const f of ['connect-assistants.tsx', 'connect-assistants-model.ts']) assert.ok(!/—/.test(readFileSync(join(webview, 'src', f), 'utf8')), f);
});

await check('the wire names match the engine and the item sits in the assistant chip', () => {
  const src = readFileSync(join(webview, 'src', 'connect-assistants.tsx'), 'utf8');
  assert.match(src, /rpc<[^>]*>\('listAssistants'\)/);
  assert.match(src, /rpc<[^>]*>\('connectAssistant', id\)/);
  const engine = readFileSync(join(webview, '..', '..', 'Semanticus.Engine', 'EngineRpcTarget.cs'), 'utf8');
  assert.match(engine, /listAssistants\(\)/);
  assert.match(engine, /connectAssistant\(string assistant/);
  const chip = readFileSync(join(webview, 'src', 'activity.tsx'), 'utf8');
  assert.match(chip, /id: 'connectOther', label: PANEL_TITLE/);
  assert.match(chip, /view === 'connect'/);
});

if (failures) { console.error(`${failures} failed`); process.exit(1); }
