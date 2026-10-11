// UAT-S02-j7-003 and UAT-S11-1-003 (lost work): unsaved Author work is kept when the person leaves Author, by the
// Workflows link or by another page. The rules of the store (webview/src/workflowheld.mjs); the clicks are driven
// in a real browser by tools/uishot/workflows-drive.mjs (author-leave-kept, -reopen, -by-plan, -untouched).
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHeldDrafts, draftShape, heldForFile, heldKey, heldPlaces, heldStateFrom, worthHolding } from '../webview/src/workflowheld.mjs';

const draft = (sourceText, operations = []) => ({ sourceText, operations, base: { path: '/p/a.md' } });

// A new workflow and a saved one never share a key, even with the same file name.
assert.notEqual(heldKey('margin-review', true), heldKey('margin-review', false));

// Only real work is kept.
{
  const seed = draftShape(draft('---\ntitle: X\n---\n', [{ op: 'set_field' }]));
  assert.equal(worthHolding(draft('a'), { creating: false, dirty: false, seedShape: null }), false, 'a saved workflow with nothing unsaved');
  assert.equal(worthHolding(draft('a'), { creating: false, dirty: true, seedShape: null }), true, 'a saved workflow with an edit');
  assert.equal(worthHolding(draft('---\ntitle: X\n---\n', [{ op: 'set_field' }]), { creating: true, dirty: true, seedShape: seed }), false,
    'a Start blank nobody touched is not clutter on the list');
  assert.equal(worthHolding(draft('KEEP\n---\ntitle: X\n---\n'), { creating: true, dirty: true, seedShape: seed }), true, 'a new workflow with typed source');
  assert.equal(worthHolding(draft('anything'), { creating: true, dirty: true, seedShape: null }), true, 'a reopened new workflow stays kept');
  assert.equal(worthHolding(null, { creating: false, dirty: true, seedShape: null }), false);
}

// The store: kept across a reload of the webview (the injected state), dropped once saved or thrown away.
{
  let saved = {};
  let t = 0;
  const open = () => createHeldDrafts({ load: () => saved, save: (v) => { saved = v; }, now: () => ++t });
  const a = open();
  a.hold('wf:b', { name: 'b', title: 'B', creating: false, draft: draft('b') });
  a.hold('new:a', { name: 'a', title: 'A', creating: true, view: 'source', draft: draft('KEEP') });
  const b = open();   // a fresh webview reads what the last one kept
  assert.deepEqual(b.list().map((e) => e.key), ['wf:b', 'new:a'], 'oldest first');
  assert.equal(b.get('new:a').draft.sourceText, 'KEEP');
  assert.equal(b.get('new:a').view, 'source');
  b.drop('wf:b');
  assert.deepEqual(open().list().map((e) => e.key), ['new:a']);
  assert.equal(open().get('missing'), null);
}

// Unusable state never breaks Author: it starts empty, and a failing save still keeps the work in memory.
{
  const broken = createHeldDrafts({ load: () => { throw new Error('no state'); }, save: () => { throw new Error('no state'); } });
  broken.hold('wf:x', { name: 'x', title: 'X', creating: false, draft: draft('x') });
  assert.equal(broken.get('wf:x').name, 'x');
}

// The verifier on m11/sol-fixes-1 round 2: a same-named workflow in another model (Save As carries every workflow
// along) wrote its changes over the work kept for this model's, because a saved workflow was kept by its name alone.
// Kept by its file's path, the two never meet; work kept by an older Studio under the name still comes back for its file.
{
  const here = '/models/a/.semanticus/workflows/margin-review.md';
  const there = '/models/b/.semanticus/workflows/margin-review.md';
  assert.notEqual(heldKey('margin-review', false, here), heldKey('margin-review', false, there));
  let saved = {};
  const store = createHeldDrafts({ load: () => saved, save: (v) => { saved = v; } });
  store.hold(heldKey('margin-review', false, here), { name: 'margin-review', title: 'Mine', creating: false, draft: { ...draft('kept'), base: { path: here } } });
  store.hold(heldKey('margin-review', false, there), { name: 'margin-review', title: 'Theirs', creating: false, draft: { ...draft('other'), base: { path: there } } });
  assert.equal(heldForFile(store, 'margin-review', here).title, 'Mine', 'the other model\'s edit is kept apart');
  assert.equal(heldForFile(store, 'margin-review', there).title, 'Theirs');
  const older = createHeldDrafts({ load: () => ({ 'wf:margin-review': { name: 'margin-review', title: 'Older', creating: false, draft: { ...draft('x'), base: { path: here } } } }) });
  assert.equal(heldForFile(older, 'margin-review', here)?.title, 'Older', 'work an older Studio kept by name comes back for its own file');
  assert.equal(heldForFile(older, 'margin-review', there), null, 'and never for another file of that name');
}

// The verifier on m11/sol-fixes-1 round 2: closing the Studio tab dropped the webview's own state and every kept
// workflow with it. A Studio opened afresh starts from the copy the extension keeps; a reloaded one from its own state.
{
  const kept = { 'new:month-end-check': { name: 'month-end-check', title: 'Month end check', creating: true, draft: draft('KEEP') } };
  assert.deepEqual(heldStateFrom(undefined, kept), kept, 'a Studio opened afresh reads the extension\'s copy');
  assert.deepEqual(heldStateFrom({ a: 1 }, kept), { a: 1 }, 'a reloaded Studio keeps its own');
  assert.deepEqual(heldStateFrom(null, null), {});
  const reopened = createHeldDrafts({ load: () => heldStateFrom(undefined, kept) });
  assert.equal(reopened.get('new:month-end-check')?.draft.sourceText, 'KEEP');

  // The two ends of that copy: the page hands every change to the extension, the extension keeps it for the workspace
  // and gives it to the next Studio page it opens, written so that no kept text can end the script it sits in.
  const design = readFileSync(new URL('../webview/src/workflowdesign.tsx', import.meta.url), 'utf8');
  assert.match(design, /heldStateFrom\(loadState[^)]*\), keptByTheExtension(<\w+>)?\(\)\)/, 'the store starts from the extension\'s copy when its own state is empty');
  assert.match(design, /postToHost\(\{ type: 'workflowHeld', value \}\)/, 'every change is handed to the extension');
  const ext = readFileSync(new URL('../src/extension.ts', import.meta.url), 'utf8');
  assert.match(ext, /msg\?\.type === 'workflowHeld'[^\n]*workspaceState\.update\(WORKFLOW_HELD_KEY/, 'the extension keeps the copy for the workspace');
  assert.match(ext, /window\.__semanticusHeldWorkflows=\$\{scriptJson\(/, 'and hands it to the Studio page it opens');
  assert.ok(ext.includes(".replace(/</g, '\\\\u003c')"), 'as JSON no text can close the script with');
}


// Two kept entries of one title are told apart by where they belong: the folder of each one's model, or a new one's file
// name. The verifier on m11/sol-fixes-1 round 3 threw away the wrong one of two lines that read the same.
{
  const entry = (key, title, creating, path, name = 'margin-review') => ({ key, name, title, creating, draft: { base: { path } } });
  const list = [
    entry('wf:a', 'Margin review', false, '/w/workspace/.semanticus/workflows/margin-review.md'),
    entry('wf:b', 'Margin review', false, '/w/workspace/other/.semanticus/workflows/margin-review.md'),
    entry('new:month-end-check', 'Month end check', true, null, 'month-end-check'),
    entry('new:month-end-check-2', 'Month end check', true, null, 'month-end-check-2'),
    entry('wf:c', 'Only one', false, '/w/x/.semanticus/workflows/only-one.md', 'only-one'),
  ];
  const places = heldPlaces(list);
  assert.deepEqual(places.get('wf:a'), { kind: 'model', folder: 'workspace' });
  assert.deepEqual(places.get('wf:b'), { kind: 'model', folder: 'other' });
  assert.deepEqual(places.get('new:month-end-check-2'), { kind: 'new', file: 'month-end-check-2' });
  assert.equal(places.get('wf:c'), null, 'a title no other kept entry shares says nothing more');
  // Two models in folders of one name are told apart by the folders above them.
  const deep = heldPlaces([
    entry('wf:d', 'Margin review', false, '/w/one/model/.semanticus/workflows/margin-review.md'),
    entry('wf:e', 'Margin review', false, 'C:' + String.fromCharCode(92) + 'two' + String.fromCharCode(92) + 'model' + String.fromCharCode(92) + '.semanticus' + String.fromCharCode(92) + 'workflows' + String.fromCharCode(92) + 'margin-review.md'),
  ]);
  assert.deepEqual([deep.get('wf:d'), deep.get('wf:e')], [{ kind: 'model', folder: 'one/model' }, { kind: 'model', folder: 'two/model' }]);
}

console.log('workflow-held: ok');
