// Join 11 solui, fix round 3: a box inventory, so the lost-words class stops being found one box at a time.
//
// Two static assertions over webview/src, against test/box-inventory.json:
//   1. Every text box (input, textarea, DaxField, DaxEditor, MEditor) has a class: held, search, parent, transient or outage-deferred. A box with no class fails, and so does a class with no box.
//   2. Every draft scope prefix (a `word|` that starts a string or a template) has a lister: OrphanDrafts, a named lister, an id-keyed allowlist with its reason, a singleton, or an `unlisted`
//      entry that says so. A prefix with no entry fails.
// `outage-deferred` is the debt Kane deferred (plain state that dies when a box is drawn anew: the outage, restart and licence class). It stays listed so that it stays visible; this test
// does not convert it. A new box or prefix needs one line in the manifest, written by a person who looked at it (`node test/helpers/box-inventory.mjs` prints a seed for the new boxes).
import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { BOX_CLASSES, LISTERS, MANIFEST, SRC, orphanListers, scanBoxes, scanScopePrefixes } from './helpers/box-inventory.mjs';

const manifest = () => JSON.parse(readFileSync(MANIFEST, 'utf8'));

/** The problems with a manifest's boxes: a box with no class, a class that is not one of the five, a held box with no `how`, a line with no why, and a line with no box. */
export function boxProblems(boxes, m) {
  const problems = [];
  const keys = new Set(boxes.map((b) => b.key));
  for (const b of boxes) {
    const e = m.boxes[b.key];
    if (!e) { problems.push(`no class for the box ${b.key} (${b.file}:${b.line})`); continue; }
    if (!BOX_CLASSES.includes(e.class)) problems.push(`${b.key}: "${e.class}" is not one of ${BOX_CLASSES.join(', ')}`);
    if (!e.why || e.why.length < 12) problems.push(`${b.key}: a class needs its reason`);
    if (e.class === 'held' && !['draft-store', 'persisted-state'].includes(e.how)) problems.push(`${b.key}: a held box says how it is held (draft-store or persisted-state)`);
  }
  for (const k of Object.keys(m.boxes)) if (!keys.has(k)) problems.push(`the manifest line ${k} has no box in webview/src any more`);
  return problems;
}

/** The problems with a manifest's scope prefixes: one with no lister, a lister that is not there, a reason that is missing, and a line with no prefix. */
export function scopeProblems(prefixes, listers, m) {
  const problems = [];
  for (const prefix of prefixes.keys()) {
    const e = m.scopes[prefix];
    if (!e) { problems.push(`no lister for the draft scope prefix ${prefix} (used in ${[...prefixes.get(prefix)].join(', ')})`); continue; }
    if (!LISTERS.includes(e.lister)) problems.push(`${prefix}: "${e.lister}" is not one of ${LISTERS.join(', ')}`);
    if (!e.why || e.why.length < 20) problems.push(`${prefix}: a lister needs its reason`);
    if (e.lister === 'orphan-drafts') {
      const drawn = [...listers.literal.keys()].some((p) => p.startsWith(prefix)) || listers.computed.some((c) => c.file === e.where.split(' ')[0]);
      if (!drawn) problems.push(`${prefix}: the manifest says OrphanDrafts lists it, but no <OrphanDrafts prefix="${prefix}..."> is drawn`);
    }
    if (e.lister === 'named-lister') {
      const [file, symbol] = e.where.split(' ');
      const src = readFileSync(join(SRC, file), 'utf8');
      if (symbol && !src.includes(symbol.replace(/[()]/g, ''))) problems.push(`${prefix}: the named lister ${symbol} is not in ${file}`);
    }
  }
  for (const k of Object.keys(m.scopes)) if (!prefixes.has(k)) problems.push(`the manifest line ${k} has no prefix in webview/src any more`);
  return problems;
}

test('every text box in webview/src has a class in the manifest, and the manifest has no line for a box that is gone', () => {
  const boxes = scanBoxes();
  assert.ok(boxes.length > 120, `the scan found only ${boxes.length} boxes: the scanner is broken, not the page`);
  assert.deepEqual(boxProblems(boxes, manifest()), []);
});

test('the box inventory is red when a manifest line is deleted, and when a line names no box', () => {
  const boxes = scanBoxes();
  const m = manifest();
  const key = Object.keys(m.boxes).find((k) => k.startsWith('plan-row.tsx::PlanRowDetail::input::note'));
  assert.ok(key, 'the Plan row note box is in the inventory');
  const without = { ...m, boxes: Object.fromEntries(Object.entries(m.boxes).filter(([k]) => k !== key)) };
  const problems = boxProblems(boxes, without);
  assert.equal(problems.length, 1);
  assert.match(problems[0], /no class for the box plan-row\.tsx::PlanRowDetail::input::note/);
  const extra = { ...m, boxes: { ...m.boxes, 'nowhere.tsx::Gone::input::x': { class: 'search', why: 'a box that is not there' } } };
  assert.match(boxProblems(boxes, extra).join('\n'), /nowhere\.tsx::Gone::input::x has no box/);
  const bad = { ...m, boxes: { ...m.boxes, [key]: { class: 'wishful', why: 'not a class at all' } } };
  assert.match(boxProblems(boxes, bad).join('\n'), /not one of held, search, parent, transient, outage-deferred/);
});

test('every draft scope prefix has a lister (OrphanDrafts, a named lister, an id-keyed allowlist with its reason, a singleton or an honest unlisted)', () => {
  const m = manifest();
  const prefixes = scanScopePrefixes();
  assert.ok(prefixes.size > 20, `the scan found only ${prefixes.size} prefixes: the scanner is broken, not the page`);
  assert.deepEqual(scopeProblems(prefixes, orphanListers(), m), []);
});

test('the scope inventory is red when a prefix has no lister, when OrphanDrafts is claimed but not drawn, and when a named lister is not there', () => {
  const m = manifest();
  const prefixes = scanScopePrefixes(), listers = orphanListers();
  const without = { ...m, scopes: Object.fromEntries(Object.entries(m.scopes).filter(([k]) => k !== 'calcgroup|')) };
  assert.match(scopeProblems(prefixes, listers, without).join('\n'), /no lister for the draft scope prefix calcgroup\|/);
  const lying = { ...m, scopes: { ...m.scopes, 'rolemember|': { lister: 'orphan-drafts', where: 'nowhere.tsx', why: 'claimed, but nothing draws it here' } } };
  const noLister = { ...listers, literal: new Map([...listers.literal].filter(([p]) => !p.startsWith('rolemember|'))) };
  assert.match(scopeProblems(prefixes, noLister, lying).join('\n'), /no <OrphanDrafts prefix="rolemember\|/);
  const ghost = { ...m, scopes: { ...m.scopes, 'planrow|': { lister: 'named-lister', where: 'plan-row.tsx NoSuchLister', why: 'a lister that does not exist in that file' } } };
  assert.match(scopeProblems(prefixes, listers, ghost).join('\n'), /named lister NoSuchLister is not in plan-row\.tsx/);
});

test('the debt stays visible: the unlisted prefixes and the outage-deferred boxes are counted, and a named class for each is on record', () => {
  const m = manifest();
  const unlisted = Object.entries(m.scopes).filter(([, e]) => e.lister === 'unlisted').map(([p]) => p).sort();
  assert.deepEqual(unlisted, ['tests-authoring|'], 'a new unlisted prefix is a decision: add it here on purpose');
  const deferred = Object.values(m.boxes).filter((b) => b.class === 'outage-deferred').length;
  console.log(`# inventory: ${Object.keys(m.boxes).length} boxes (${['held', 'search', 'parent', 'transient', 'outage-deferred'].map((c) => `${c} ${Object.values(m.boxes).filter((b) => b.class === c).length}`).join(', ')}); ${Object.keys(m.scopes).length} scope prefixes, ${unlisted.length} unlisted`);
  assert.ok(deferred > 0 && deferred < 60, `the deferred class should be the 30-odd plain boxes, found ${deferred}`);
});
