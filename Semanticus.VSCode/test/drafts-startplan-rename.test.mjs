// The start plan's Rename editors keep a typed new name (join 11, the drafts class; the words editor did since join 8).
//
// A new name typed into a table or measure row, or into a column of the folded column list, and not saved, is held above the editor by the model's FILE,
// the line (or `col:<ref>`) and the kind 'rename', over the name it was typed over. The row opens on it again after another row, a remount on a restarted
// engine and a reload; the row and the column fold say "Draft not saved"; a name that changed meanwhile asks Keep mine / Use the new one; a save, Discard
// and Use the new one end it; Escape closes the editor but keeps it. A draft never appears on another model.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader, req } from './helpers/webview-load.mjs';
import { makeStartPlanState, startPlanView } from '../tools/uishot/fixtures/startplan-fixture.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const persisted = {};
const stubs = {
  './startplan.css': {}, './plan-history.css': {}, './sparkline': { ValueSparkline: () => null },
  './bridge': {
    rpc: async () => { throw new Error('no engine here'); }, onEngineState: () => () => {}, pickWorkingCopyFolder: async () => null, onStartPlanChange: () => () => {}, runHostCommand: () => {},
    copyText: async () => true, onDidChange: () => () => {}, onActivity: () => () => {}, onPlanChange: () => () => {}, onReconnect: () => () => {}, navigateInStudio: () => {}, claimConsentAsk: async () => true, revealInTree: () => {},
    loadState: (k, f) => (k in persisted ? JSON.parse(JSON.stringify(persisted[k])) : f), saveState: (k, v) => { persisted[k] = JSON.parse(JSON.stringify(v)); },
  },
  './pro': { useTier: () => 'pro', upgradeToPro: () => {}, PENDING_ACCESS: 'Checking your Pro access…' },
  './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} },
  './connection': { useConnection: () => ({ openConnections: () => {} }) },
};
const load = makeLoader(stubs);
const M = load('startplan-model');
const P = load('startplan');
const D = load('startplan-drafts');
const src = (f) => readFileSync(join(root, 'webview', 'src', f), 'utf8');
const view = JSON.parse(JSON.stringify(startPlanView(makeStartPlanState({}))));
const MODEL = view.destination.modelPath;
const line = M.linesOf(view).find((l) => l.item?.kind === 'include_table' && M.lineControls(l).rename);
const lineId = line.item.id, current = M.lineControls(line).rename.current;
const tier = { pro: false };
const detail = (l = line, modelKey = MODEL) => renderToStaticMarkup(React.createElement(P.LineDetail, { line: l, view, tier, busy: false, onEdit() {}, onWords: async () => ({ saved: true, moved: false, said: null }), modelKey }));
const body = (v = view) => renderToStaticMarkup(React.createElement(P.StartPlanBody, { view: v, pro: false, busy: null, error: null, sentence: null, changed: new Set(), onEdit() {}, onNeed() {}, onApprove() {}, onBuild() {}, onApproveAndBuild() {}, onDiscard() {}, onRetry() {} }));

test('rename: a typed new name is held by file, line and kind, and the row opens on it', () => {
  D.draftStore.clearModel(MODEL);
  assert.ok(!/data-testid="sp-rename-editor"/.test(detail()), 'with no draft the row opens without a rename editor');
  D.draftStore.set(MODEL, lineId, 'rename', { text: 'Employees', base: current });
  const opened = detail();
  assert.match(opened, /data-testid="sp-rename-editor"/, 'a row with a held name opens on its rename editor');
  assert.match(opened, /value="Employees"/, 'with the typed name in the box');
  assert.ok(!/data-testid="sp-rename-moved"/.test(opened), 'the name did not move: no choice to make');
  assert.ok(!/sp-rename-editor/.test(detail(line, '/m/Other.SemanticModel')), 'another model does not see it');
  D.draftStore.clearModel(MODEL);
});

test('rename: a name that changed while the editor was away is asked about, and nothing typed is replaced', () => {
  D.draftStore.set(MODEL, lineId, 'rename', { text: 'Employees', base: 'An older name' });
  const asked = detail();
  assert.match(asked, /data-testid="sp-rename-moved"/, 'the name moved under the typed one: Keep mine / Use the new one');
  assert.match(asked, /value="Employees"/, 'the typed name stays');
  assert.match(asked, /data-testid="sp-rename-keepmine"/); assert.match(asked, /data-testid="sp-rename-usenew"/);
  assert.match(asked, /disabled=""[^>]*data-testid="sp-rename-save"/, 'Save waits for the choice');
  D.draftStore.clearModel(MODEL);
});

test('rename: the row says Draft not saved, once, and the plan of another file does not', () => {
  D.draftStore.clearModel(MODEL);
  D.draftStore.set(MODEL, lineId, 'rename', { text: 'Employees', base: current });
  const flagged = body();
  assert.match(flagged, new RegExp(`data-testid="sp-row-${lineId}"[^>]*data-draft="yes"`), 'the row with a held name is marked');
  assert.equal((flagged.match(/data-testid="sp-draft-flag"/g) ?? []).length, 1, 'once');
  assert.ok(!/data-draft="yes"/.test(body({ ...view, destination: { ...view.destination, modelPath: '/m/Other.SemanticModel' } })));
  D.draftStore.clearModel(MODEL);
});

test('rename: a column\'s typed name is held under its ref, the column list opens on it and its header says so', () => {
  const group = (view.sections ?? []).flatMap((s) => s.groups ?? []).find((g) => (g.columns ?? []).length);
  const col = group.columns[0];
  D.draftStore.clearModel(MODEL);
  const list = (open = true, modelKey = MODEL) => renderToStaticMarkup(React.createElement(P.ColumnList, { columns: group.columns, open, busy: false, modelKey, onToggle() {}, onEdit() {} }));
  assert.ok(!/sp-col-rename-editor/.test(list()), 'no draft: no editor');
  D.draftStore.set(MODEL, `col:${col.ref}`, 'rename', { text: 'A better name', base: col.name });
  const opened = list();
  assert.match(opened, /data-testid="sp-col-rename-editor"/, 'the column opens on its rename editor');
  assert.match(opened, /value="A better name"/);
  assert.match(list(false), /data-testid="sp-draft-flag"/, 'a shut list still says a name is not saved');
  assert.ok(!/sp-col-rename-editor/.test(list(true, '/m/Other.SemanticModel')), 'another model does not see it');
  D.draftStore.clearModel(MODEL);
});

test('rename: a save, Discard and Use the new one end the draft; Escape closes the editor and keeps it', () => {
  const s = src('startplan.tsx');
  const editor = s.slice(s.indexOf('function Editor('), s.indexOf('/** The editor for the words on a line.'));
  assert.match(editor, /onDraft\??\.\(typed \? \{ text, base \} : null\)/, 'the editor reports every change upward, so the draft outlives it');
  // R-B (drafts-sol F6): a save ends the draft only when the engine took exactly the words it sent. It used to end it before the write, so a refusal took the typed name with it.
  assert.match(editor, /took = \(await onSave\(sent\)\) !== false/, 'the save reads whether the engine took it');
  assert.match(editor, /if \(!took\) return;\s*onTook\?\.\(sent\);/, 'a save the engine took ends the draft (and only the words it sent), a refused one keeps it');
  assert.ok(!/const save = [^]*?onDraft\?\.\(null\)[^]*?await onSave/.test(editor), 'the draft is never ended before the write is answered');
  assert.match(editor, /data-testid=\{`\$\{testid\}-cancel`\} onClick=\{\(\) => \{ onDraft\?\.\(null\); onCancel\(\); \}\}/, 'Discard ends it');
  assert.match(editor, /onClick=\{\(\) => \{ setText\(value\); setBase\(value\); \}\}/, 'Use the new one loads the live name, which leaves nothing typed, which holds no draft');
  const esc = editor.slice(editor.indexOf('const keys ='), editor.indexOf('const save ='));
  assert.ok(!/onDraft/.test(esc), 'Escape closes the editor but never throws the draft away');
  assert.match(s, /draftStore\.clearModel\(/, 'the plan\'s own discard clears every draft of the model, renames included');
});
