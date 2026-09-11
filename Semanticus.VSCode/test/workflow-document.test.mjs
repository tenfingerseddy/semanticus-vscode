import assert from 'node:assert/strict';
import { test } from 'node:test';
import * as documentState from '../webview/src/workflowdocument.mjs';
import { acceptDocumentSave, applyTextareaChange, canApplyUpgrade, canPreviewUpgrade, createDocumentLoader, receiveDocument } from '../webview/src/workflowdocument.mjs';

const document = (exactText, byteHash = exactText, path = '/project/.semanticus/workflows/review.md') => ({
  name: 'review', library: 'user', path, exactText, byteHash,
  metadata: { parses: true, parseError: null, schemaVersion: 2, title: 'Review', version: 1, stepIds: ['review'], explicitIds: true },
});

test('reviewed saved source can explicitly become the base without losing the draft', () => {
  const original = document('original', 'hash-1');
  const latest = document('agent update', 'hash-2');
  const draft = { ...receiveDocument(null, original), text: 'my reconciled edit' };
  const stale = receiveDocument(draft, latest);
  assert.equal(stale.base.byteHash, 'hash-1', 'refresh cannot authorize an overwrite');
  const rebased = documentState.rebaseDocument(stale, latest);
  assert.equal(rebased.text, draft.text);
  assert.equal(rebased.base.byteHash, 'hash-2', 'the next explicit save uses the reviewed hash');
  assert.equal(rebased.base.path, original.path);
  assert.deepEqual(rebased.latest, latest);
  assert.deepEqual(receiveDocument(JSON.parse(JSON.stringify(rebased)), latest), rebased, 'the rebased draft survives navigation');
  assert.equal(canPreviewUpgrade(rebased), false, 'rebasing does not consume the dirty draft for an upgrade');
  const next = receiveDocument(rebased, document('another outside edit', 'hash-3'));
  assert.notEqual(next.base.byteHash, next.latest.byteHash, 'another edit disables saving again');
  assert.equal(next.text, draft.text);
});

test('rebasing refuses a changed review, another path, and stock source without changing the draft', () => {
  const original = document('original', 'hash-1');
  const reviewed = document('reviewed', 'hash-2');
  const stale = receiveDocument({ ...receiveDocument(null, original), text: 'keep this draft' }, reviewed);
  const snapshot = JSON.stringify(stale);
  for (const incoming of [document('newer', 'hash-3'), document('reviewed', 'hash-2', '/other/review.md'),
    { ...reviewed, library: 'stock' }]) {
    assert.throws(() => documentState.rebaseDocument({ ...stale, latest: incoming }, reviewed), /no longer matches this review/);
  }
  const other = document('reviewed', 'hash-2', '/other/review.md');
  assert.throws(() => documentState.rebaseDocument({ ...stale, latest: other }, other), /no longer matches this review/, 'even a reviewed other-project file cannot become the base');
  const stock = { ...reviewed, library: 'stock' };
  assert.throws(() => documentState.rebaseDocument({ ...stale, latest: stock }, stock), /no longer matches this review/);
  assert.throws(() => documentState.rebaseDocument({ ...stale, base: { ...original, library: 'stock' } }, reviewed), /no longer matches this review/);
  assert.equal(JSON.stringify(stale), snapshot);
});

test('clean buffers follow changes; dirty buffers keep their original write fence and text', () => {
  const first = document('original', 'hash-1');
  const newer = document('agent update', 'hash-2');
  const clean = receiveDocument(null, first);
  assert.equal(receiveDocument(clean, newer).text, 'agent update');
  const dirty = { ...clean, text: 'my unsaved edit' };
  const stale = receiveDocument(dirty, newer);
  assert.equal(stale.text, 'my unsaved edit');
  assert.equal(stale.base.byteHash, 'hash-1');
  assert.equal(stale.latest.byteHash, 'hash-2');
  assert.deepEqual(receiveDocument(JSON.parse(JSON.stringify(stale)), newer), stale, 'a retained draft survives leaving and reopening the editor');
  assert.equal(receiveDocument(dirty, document('other project', 'hash-3', '/other/review.md')).text, 'other project', 'drafts cannot cross document paths');
});

test('refused saves retain invalid or conflicting drafts and the original base hash', () => {
  const original = receiveDocument(null, document('original', 'hash-1'));
  for (const saved of [original.base, document('external edit', 'hash-2')]) {
    const draft = { ...original, text: 'invalid or conflicting draft' };
    const refused = acceptDocumentSave(draft, saved, draft.text, false);
    assert.equal(refused.text, draft.text);
    assert.equal(refused.base.byteHash, 'hash-1');
    assert.equal(refused.base.exactText, 'original');
    assert.deepEqual(refused.latest, saved);
  }
});

test('save results acknowledge exactly the submitted draft, including no-op results', () => {
  const first = receiveDocument(null, document('old', 'hash-1'));
  const saved = document('submitted', 'hash-2');
  const clean = acceptDocumentSave({ ...first, text: 'submitted' }, saved, 'submitted', true);
  assert.equal(clean.text, clean.base.exactText);
  assert.equal(clean.base.byteHash, 'hash-2');
  const stillTyping = acceptDocumentSave({ ...first, text: 'a newer edit' }, saved, 'submitted', true);
  assert.equal(stillTyping.text, 'a newer edit');
  assert.equal(stillTyping.base.byteHash, 'hash-2');
  assert.deepEqual(acceptDocumentSave(first, first.base, 'old', false), first);
  const equalBytes = acceptDocumentSave({ ...first, text: 'submitted' }, saved, 'submitted', false);
  assert.equal(equalBytes.text, equalBytes.base.exactText);
  assert.equal(equalBytes.base.byteHash, 'hash-2');
});

test('textarea edits preserve BOM and untouched CRLF, LF, CR and final-newline bytes', () => {
  const bom = String.fromCharCode(0xfeff);
  const exact = bom + 'one\r\ntwo\nthree\r';
  assert.equal(applyTextareaChange(exact, 'one\ntwo\nthree\n'), exact, 'an untouched textarea does not normalize the file');
  assert.equal(applyTextareaChange(exact, 'one\nTWO\nthree\n'), bom + 'one\r\nTWO\nthree\r');
  assert.equal(applyTextareaChange('one\r\ntwo', 'one\nnew\ntwo'), 'one\r\nnew\r\ntwo');
  assert.equal(applyTextareaChange('one\r\ntwo\r\n', 'one\n'), 'one\r\n');
  assert.equal(applyTextareaChange('one', ''), '');
  assert.equal(applyTextareaChange(bom + 'one', ''), bom, 'editing content keeps its encoding marker');
});

test('late document reads and late failures cannot replace a newer selection or update', async () => {
  const requests = [];
  const received = [];
  const errors = [];
  const loader = createDocumentLoader({
    read: () => new Promise((resolve, reject) => requests.push({ resolve, reject })),
    onDocument: (value) => received.push(value), onError: (error) => errors.push(error),
  });
  const old = loader.load('review');
  const latest = loader.load('review');
  requests[1].resolve(document('latest'));
  await latest;
  requests[0].resolve(document('stale'));
  assert.equal(await old, null);
  assert.deepEqual(received.map((value) => value.exactText), ['latest']);
  const abandoned = loader.load('review');
  loader.invalidate();
  requests[2].reject(new Error('old read failed'));
  await abandoned;
  assert.deepEqual(errors, []);
});

test('upgrade previews require clean saved source and never consume a local draft', () => {
  const saved = document('v1 source', 'hash-1');
  saved.metadata.schemaVersion = 1;
  const clean = receiveDocument(null, saved);
  assert.equal(canPreviewUpgrade(clean), true);
  const dirty = { ...clean, text: 'local draft' };
  assert.equal(canPreviewUpgrade(dirty), false);
  assert.equal(dirty.text, 'local draft');
  assert.equal(canPreviewUpgrade({ ...clean, latest: document('external source', 'hash-2') }), false);
  assert.equal(canPreviewUpgrade({ ...clean, latest: document('v1 source', 'hash-1', '/other/review.md') }), false);
  assert.equal(canPreviewUpgrade(receiveDocument(null, { ...saved, metadata: { ...saved.metadata, parses: false } })), false);
  assert.equal(canPreviewUpgrade(null), false);
});

test('apply uses a matching reviewed source, not the preview changed flag', () => {
  const saved = document('v1 source', 'hash-1');
  const clean = receiveDocument(null, saved);
  const preview = { name: saved.name, dryRun: true, changed: false, canApply: true, document: saved,
    proposedText: 'v2 source', diff: '-v1 source +v2 source', parseError: null };
  assert.equal(canApplyUpgrade(clean, preview), true, 'safe previews always have changed:false');
  assert.equal(canApplyUpgrade({ ...clean, text: 'local draft' }, preview), false);
  assert.equal(canApplyUpgrade(receiveDocument(clean, document('new source', 'hash-2')), preview), false);
  assert.equal(canApplyUpgrade(receiveDocument(clean, document('v1 source', 'hash-1', '/other/review.md')), preview), false);
  assert.equal(canApplyUpgrade(clean, { ...preview, canApply: false }), false);
  assert.equal(canApplyUpgrade(clean, { ...preview, dryRun: false }), false);
  assert.equal(canApplyUpgrade(clean, { ...preview, parseError: 'Invalid format' }), false);
  assert.equal(canApplyUpgrade(clean, { ...preview, proposedText: saved.exactText }), false, 'v2 no-ops cannot apply');
  assert.equal(canApplyUpgrade(clean, { ...preview, proposedText: null }), false);
  assert.equal(canApplyUpgrade(clean, null), false);
});

test('stock upgrades can preview but need a fresh project-copy preview to apply', () => {
  const stock = { ...document('v1 source', 'same-hash', '/stock/review.md'), library: 'stock' };
  const preview = { dryRun: true, changed: false, canApply: false, document: stock, proposedText: 'v2 source', diff: 'diff' };
  const clean = receiveDocument(null, stock);
  assert.equal(canPreviewUpgrade(clean), true);
  assert.equal(canApplyUpgrade(clean, preview), false);
  const copy = receiveDocument(clean, document(stock.exactText, stock.byteHash));
  assert.equal(canApplyUpgrade(copy, preview), false, 'matching bytes at a new path still need a new preview');
  assert.equal(canApplyUpgrade(copy, { ...preview, canApply: true, document: copy.base }), true);
});
