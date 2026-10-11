// Sol join 12 (A8), the CLASS: a person action ends its words only when the engine accepted exactly those words ("end only on OK, same words", rule R-B).
//
// Static, over webview/src/*.tsx, against test/action-inventory.json:
//   1. No handler sends a write and clears a box, a form, an open flag or a hold in the SAME run (`onSave(x); setText('')`, `void rpc(...); setOpen(false)`). Such a clear runs before the
//      answer, so a refusal loses the person's words. A hit is allowed only when the manifest names it with the reason it holds no words (a menu, a local-only edit).
//   2. Every action the audit found losing words is listed with the line of code that now guards it, and that line must still be there.
//   3. A manifest line with no hit in the source fails too, so the list never keeps a fix that was removed.
// The scan is for the same-run shape only. It cannot see a clear that follows an `await` of an answer nobody checks; that shape is why the audited list (`fixed`, `notLosing`) exists.
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const SRC = join(here, '..', 'webview', 'src');
const INVENTORY = join(here, 'action-inventory.json');

const SEND = String.raw`(?:\bon[A-Z]\w*|\brpc(?:<[^>()]*>)?|\bsave\w*|\bsend\w*|\bsubmit\w*|\bapply\w*|\bwrite\w*|\bcommit\w*)`;
const CLEAR = String.raw`(?:set\w+\(\s*(?:''|"{2}|false|null|\[\]|\{\})\s*\)|\bclear\w*\(|\bclose\w*\(|\bend\w*Draft\w*\(|draftStore\.clear\w*\()`;
const SAME_RUN = () => new RegExp(String.raw`(?<![\w.])(?<!await )(?<!return )(` + SEND + String.raw`\((?:[^;(){}]|\([^;(){}]*\)|\{(?:[^;(){}]|\([^;(){}]*\))*\})*\))\s*;\s*(` + CLEAR + ')', 'g');

/** Every place a send is followed by a clear in the same run: `file::send::clear` with its line. */
export function sameRunHits(file, text) {
  const out = [];
  for (const m of text.matchAll(SAME_RUN())) out.push({ key: `${file}::${m[1].replace(/\s+/g, ' ')}::${m[2]}`, line: text.slice(0, m.index).split('\n').length });
  return out;
}

const sources = () => readdirSync(SRC).filter((n) => n.endsWith('.tsx')).sort().map((n) => ({ file: n, text: readFileSync(join(SRC, n), 'utf8') }));
const inventory = () => JSON.parse(readFileSync(INVENTORY, 'utf8'));

test('the scan sees the shape it exists to forbid (the four handlers the audit fixed, as they were)', () => {
  const before = [
    ["const commit = () => { const t = text.trim(); if (t) { onWaive(t); setEditing(false); setText(''); } };", 'onWaive(t)'],
    ["onSave={(cadence) => { onChange(saved.id, { cadence }); setEditing(false); }}", 'onChange(saved.id, { cadence })'],
    ["onClick={() => { onAdd(name.trim(), value); setName(''); setValue(''); }}", 'onAdd(name.trim(), value)'],
    ["apply({ op: 'set_map_entry', target, field, name: name.trim(), expect: null, value }); setName(''); setValue('');", "apply({ op: 'set_map_entry', target, field, name: name.trim(), expect: null, value })"],
    ["void rpc('createX', n); setNewName('');", "rpc('createX', n)"],
  ];
  for (const [code, send] of before) {
    const hits = sameRunHits('x.tsx', code);
    assert.equal(hits.length, 1, `not seen: ${code}`);
    assert.ok(hits[0].key.includes(send), hits[0].key);
  }
  // The guarded shapes are not hits: an answer awaited, or a clear inside a then.
  assert.equal(sameRunHits('x.tsx', "const ok = await onWaive(t); if (ok) setText('');").length, 0);
  assert.equal(sameRunHits('x.tsx', "void rpc('createX', n).then(() => { setNewName(''); });").length, 0);
});

test('no action clears words in the same run as the write it sends, unless the manifest says it holds none', () => {
  const m = inventory();
  const found = new Map();
  for (const { file, text } of sources()) for (const h of sameRunHits(file, text)) found.set(h.key, h.line);
  const problems = [];
  for (const [key, line] of found) {
    const e = m.noWords[key];
    if (!e) problems.push(`${key} (line ${line}) sends a write and clears in the same run: end it only on the engine's OK, and only if the box holds the words that were sent; or list it in test/action-inventory.json with why it holds no words`);
    else if (!e.why || e.why.length < 20) problems.push(`${key}: an allowed line needs its reason`);
  }
  for (const key of Object.keys(m.noWords)) if (!found.has(key)) problems.push(`the manifest line ${key} has no such action in webview/src any more`);
  assert.deepEqual(problems, [], problems.join('\n'));
});

test('every action the audit found losing words is guarded, and the guard is still in the source', () => {
  const m = inventory();
  const text = new Map(sources().map((s) => [s.file, s.text]));
  const problems = [];
  for (const a of m.fixed) {
    const t = text.get(a.file);
    if (!t) { problems.push(`${a.id}: ${a.file} is not in webview/src`); continue; }
    if (!a.trigger || a.trigger.length < 20) problems.push(`${a.id}: a fixed action says what refuses it`);
    for (const re of a.guards) if (!new RegExp(re).test(t)) problems.push(`${a.id}: ${a.file} no longer has the guard /${re}/`);
    for (const re of a.never ?? []) if (new RegExp(re).test(t)) problems.push(`${a.id}: ${a.file} has the old clear-before-answer shape /${re}/ again`);
  }
  assert.deepEqual(problems, [], problems.join('\n'));
  assert.ok(m.fixed.length >= 12, 'the audited list has gone missing');
});
