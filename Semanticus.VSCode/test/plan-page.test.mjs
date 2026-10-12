// The older half of Model > Plan's checks: the wire shape of the plan items the Overview still reads, the tier gate,
// the engine stub's refusals, and the review road (keep, drop, approve, cancel, propose again) over plan-fixture.mjs.
//
// The Plan page itself was rebuilt as the Plan at the heart (slice 9, lane U1). Its checks are
// test/plan-heart.test.mjs, over fixtures/plan-heart-fixture.mjs (E1's shape file), and its clicks are driven by
// tools/uishot/cp-plan-drive.mjs. The sections of this file that tested the retired page (its rows, map, tiles,
// target header, filters, run walk and page source) went with it; those that test plan-model functions the
// Overview still uses stay here until R1 retires the Overview.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import {
  makePlan, planAction, planSkip, planUnskip, PLAN_FIXED_NOW, usePlanClock,
  planReviewAction, planSetLine, planFindLine, planChangeView, planAddTarget, planRemoveTarget,
} from '../tools/uishot/fixtures/plan-fixture.mjs';

// THE CLOCK IS PINNED BEFORE ANYTHING IS BUILT, and this line is the whole reason this suite is not a
// different suite at midnight. The fixture defaults to the real clock, which is right for the driver's
// shots and wrong here: an entry three and a half hours back is in today's group at 14:00 and in
// yesterday's at 00:20, so the day-grouping assertion below read [6, 3, 2] instead of [4, 2, 3, 2]
// between midnight and about 02:00 local. Pinned, every hour of the day gives one answer, and the page
// is handed the same instant as its `now` everywhere it is asked for one. Proved at two faked clocks:
//   FAKE_NOW_MS=<00:30> node --require ./test/helpers/fake-clock.cjs test/plan-page.test.mjs
//   FAKE_NOW_MS=<14:00> node --require ./test/helpers/fake-clock.cjs test/plan-page.test.mjs
const PLAN_NOW = usePlanClock(PLAN_FIXED_NOW);

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

// ---- the same tiny CommonJS loader the Impact page's test uses, with the host seams stubbed ---------
// `stubTier` is mutable so the same loaded module can be rendered as a free session and as a Pro one.
// The page reads the tier through pro.tsx's useTier, which is the one seam the licence comes in by.
let stubTier = 'pro';
const STUBS = {
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls; drive the app for that'); },
    onDidChange: () => () => {}, onProgress: () => () => {}, revealInTree: () => {},
    focusSelectInProperties: () => {}, selectInProperties: () => {}, runHostCommand: () => {},
    // The Plan page folded in the pending changes and the draft on 23 September, so it subscribes to
    // both of their broadcasts. Neither fires here; the seams only have to exist.
    onPlanChange: () => () => {}, onSpecChange: () => () => {}, copyText: async () => {},
    pickSpecFile: async () => null,
  },
  './connection': { useConnection: () => ({ session: { sessionId: 'test' }, context: null }) },
  './pro': {
    useTier: () => stubTier,
    useFeature: () => (stubTier === 'pro' ? 'granted' : 'denied'),
    isEntitlementError: () => false, ProBadge: () => null, UpsellNotice: () => null,
    PENDING_ACCESS: 'Checking your Pro access…', upgradeToPro: () => {},
  },
};
const cache = new Map();
function load(file) {
  // .mjs is in the list because a plain ES module this page imports can be loaded here too (the Overview's
  // overviewfacts.mjs was the first; it retired with the Overview in slice 11).
  const full = /\.(ts|tsx|mjs)$/.test(file) ? file
    : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const source = readFileSync(full, 'utf8');
  const js = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    // TypeScript decides the module kind from the EXTENSION and forces .mjs to stay ESM whatever
    // `module` says, so a .mjs is transpiled under a .ts name. Without this the loader hands back a
    // file that still says `export` and Node throws on it.
    fileName: full.endsWith('.mjs') ? `${full.slice(0, -4)}.ts` : full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => {
    if (STUBS[name]) return STUBS[name];
    if (name.startsWith('.') && name.endsWith('.css')) return {};
    if (name.startsWith('.')) return load(resolve(dirname(full), name));
    return req(name);
  };
  // eslint-disable-next-line no-new-func
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
// Read as LF: the Windows CI checkout has CRLF endings (*.tsx is text under `* text=auto`), and the source
// patterns below are written with \n. The Keep or Drop pattern failed there first (PR 396).
const srcOf = (f) => readFileSync(join(webview, 'src', f), 'utf8').replace(/\r\n/gu, '\n');

/** What a person can actually read or hear: the text, plus the title a tooltip speaks. */
const readable = (html) => html
  .replace(/\s(?:class|style|data-[\w-]+|value|type|id|for|role|checked|disabled|href|width|maxWidth|aria-controls|aria-expanded|aria-pressed)="[^"]*"/g, '')
  .replace(/<[^>]+>/g, ' ')
  .replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&').replace(/&#x2F;/g, '/')
  .replace(/\s+/g, ' ');

const P = load(join(webview, 'src', 'plan-model.ts'));
const F = load(join(webview, 'src', 'features.ts'));
const plan = makePlan();
const freePlan = makePlan({ free: true });
const byId = (p, id) => p.items.find((i) => i.id === id);
const SAFEFIX = 'safefix/Visibility';
const DESCRIBE = 'describe/model';
const CLEANUP = 'cleanup/model';
const CHECK = 'check/measure:Sales/Margin %';
const RELATIONSHIP = 'relationship/Sales-Store';
const DATETABLE = 'datetable/model';
const DONE_KEYS = 'done/hide-keys';
const DONE_DATE = 'done/date-table';

// ===================================================================================================
// 1. The fixture is the WIRE shape, not a convenient one. If this block drifts the page is being built
//    against something the engine does not send.
// ===================================================================================================
assert.equal(plan.items.length, 9, 'the mockup plan is 9 items: 5 open, 1 set aside, 3 already done');
assert.equal(plan.tables.length, 13, 'on a model of 13 tables');
for (const item of plan.items) {
  assert.equal(typeof item.id, 'string', `item ${item.id} must carry a string id, as ModelPlanItem.Id does`);
  assert.ok(/^[a-z]+\//.test(item.id), `an id is family/subject, never a number: ${item.id}`);
  assert.ok(Array.isArray(item.diff), `item ${item.id} must carry diff as an array, as ModelPlanItem.Diff is`);
  assert.ok(Array.isArray(item.why));
}
assert.equal(typeof plan.next.itemId, 'string', 'and the next item is named by that same string id');
assert.ok(Number.isFinite(Date.parse(plan.refreshedAt)), 'refreshedAt is a real timestamp, not a label');
assert.ok(plan.history.every((row) => Number.isFinite(Date.parse(row.when))), 'and so is every history row');
assert.equal(plan.ranked, true);
assert.equal(typeof plan.orderedBy, 'string');
// The ids are unique, which is what lets a skip stick to the thing it was about across a refresh.
assert.equal(new Set(plan.items.map((i) => i.id)).size, 9, 'every id names one thing');

// --- what 21 September put on the wire, and the arithmetic behind it.
//
// An item's per-table counts have to ADD UP to the item's own total, because the card prints
// "24 of the plan's 57" and that sentence is only true if the 57 is the sum of the parts. This is
// executed rather than written down: a fixture edit that breaks the sum fails here and not in a shot.
const cleanupItem = byId(plan, CLEANUP);
assert.equal(cleanupItem.tables.length, 5, 'the 57 unused columns that start ticked land on five tables');
assert.equal(cleanupItem.tables.reduce((n, t) => n + t.count, 0), 57,
  "the per-table counts add up to the item's own total, which is what makes \"24 of the plan's 57\" true");
assert.deepEqual(cleanupItem.tables.map((t) => t.name),
  ['Budget', 'Customer', 'Product', 'Promotion', 'Sales']);
assert.equal(byId(plan, SAFEFIX).tables.reduce((n, t) => n + t.count, 0), 4);
// The engine sorts an item's tables by name, case-insensitive, one entry each and never a repeat.
for (const item of plan.items) {
  const names = item.tables.map((t) => t.name);
  assert.deepEqual(names, [...names].sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase())),
    `item ${item.id} sends its tables out of the order the engine sends them in`);
  assert.equal(new Set(names).size, names.length, `item ${item.id} names a table twice`);
}
for (const item of plan.items) {
  for (const at of item.tables) {
    assert.equal(typeof at.name, 'string', `item ${item.id} names a table without a name`);
    assert.ok(Number.isFinite(at.count), `item ${item.id} on ${at.name} carries no count`);
    assert.ok(plan.tables.some((t) => t.name === at.name),
      `item ${item.id} names ${at.name}, which is not a table on this model`);
  }
}
// A measure table says so. It used to come through as a plain "table", which told a reader nothing.
assert.equal(plan.tables.find((t) => t.name === 'Key Measures').kind, 'measure table');
assert.equal(plan.tables.find((t) => t.name === 'Key Measures').sizeBytes, null,
  'and a table no size scan has reached carries null, which is not the same as 0');
assert.equal(plan.tables.find((t) => t.name === 'Sales').sizeBytes, 2202010);
// The Fix milestone put TWO new members on every item (m3-item-shape.json: `state` and `changeSet`),
// and the ordinary answer for both is the ordinary state of a plan item and no changes worked out.
for (const item of plan.items) {
  assert.equal(typeof item.state, 'string', `item ${item.id} carries no state`);
  assert.ok(['target', 'gap', 'proposed', 'applied', 'done', 'skipped'].includes(item.state),
    `item ${item.id} is in a state the engine does not send: ${item.state}`);
  assert.equal(item.changeSet, null, 'no item on the ordinary plan has changes worked out for it');
}
assert.equal(plan.items.filter((i) => i.state === 'gap').length, 5, 'five gaps');
assert.equal(plan.items.filter((i) => i.state === 'done').length, 3);
assert.equal(plan.items.filter((i) => i.state === 'skipped').length, 1);
// And the five rule aims are on EVERY plan, derived, one per section, owned by nobody.
assert.equal(plan.targets.length, 5, 'the rule aims are one per section and are always there');
assert.deepEqual(plan.targets.map((t) => t.section), ['Structure', 'Meaning', 'Numbers', 'Trust', 'Ship']);
assert.ok(plan.targets.every((t) => t.source === 'rules' && t.when === null && t.itemId === null),
  'a rule aim was added by nobody and became no item, and the wire says so with nulls');
assert.equal(typeof plan.worth.targetLine, 'string', 'and worth carries the engine own target line');

// The history is the new wire AND the old one, on purpose: both have to draw.
assert.equal(plan.history.length, 14);
// OLDEST FIRST on the wire, which is the one thing about this list the page has to undo.
const stamps = plan.history.map((row) => Date.parse(row.when));
assert.deepEqual(stamps, [...stamps].sort((a, b) => a - b), 'the engine keeps its history oldest first');
// And `text` is on every structured row too, always the same string as `sentence`, so a reader that
// knows nothing about the new fields still has one sentence to print.
for (const row of plan.history.filter((r) => r.kind)) assert.equal(row.text, row.sentence);
assert.equal(plan.history.filter((row) => row.kind).length, 12, 'twelve structured rows');
assert.equal(plan.history.filter((row) => !row.kind && row.text).length, 2,
  'and two from the old wire, which carried a time and a sentence and nothing else');
assert.deepEqual([...new Set(plan.history.filter((r) => r.kind).map((r) => r.kind))].sort(),
  ['done', 'drawn', 'redrawn', 'refused', 'skipped', 'started', 'unskipped',
    'proposed', 'approved', 'cancelled'].sort(),
  'all TEN of the engine kinds appear, so no kind is drawn only in a test');

// ===================================================================================================
// 6. The gate is the TIER, not a fifth feature, because the engine gates it on IsProTier and
//    get_entitlement.features only ever carries the four tab features.
// ===================================================================================================
assert.equal(F.planRankingOf('pro'), 'granted');
assert.equal(F.planRankingOf('free'), 'denied');
assert.equal(F.planRankingOf('unknown'), 'unknown',
  'before the licence has been read, neither the ranked page nor the upsell is honest');
assert.equal(F.planRankingOf(''), 'denied', 'anything that is not the paid tier is not the paid tier');
assert.deepEqual(F.PRO_FEATURES, ['workflows', 'askData'],
  'the Pro line is two tab features (Workflows, and Ask your data from 26 September 2026), and no third one was invented to carry the plan');
assert.equal(F.featureOfTool('plan'), null, 'the Plan page itself stays free, and is not in the map');
assert.ok(!Object.keys(F.FEATURE_OF_TOOL).includes('plan'));

// ===================================================================================================
// 7. A timestamp is formatted, never printed raw (the Overview's plan card still reads whenLabel).
// ===================================================================================================
{
  const NOW = Date.parse('2026-09-21T10:44:00.000Z');
  assert.equal(P.whenLabel('2026-09-21T10:42:00.000Z', NOW), '2 min ago');
  assert.equal(P.whenLabel('2026-09-21T10:43:40.000Z', NOW), 'just now');
  assert.equal(P.whenLabel('2026-09-21T07:44:00.000Z', NOW), '3 hours ago');
  assert.equal(P.whenLabel('2026-09-19T10:44:00.000Z', NOW), '2 days ago');
  assert.equal(P.whenLabel('2026-08-01T10:44:00.000Z', NOW), '2026-08-01', 'anything old enough is a date');
  assert.equal(P.whenLabel(undefined, NOW), '');
  assert.equal(P.whenLabel('whenever', NOW), 'whenever', 'a value that is not a time is printed, not "Invalid Date"');
}

// ===================================================================================================
// 12. The engine stub, which REFUSES what the engine refuses. These are the answers the browser driver
//     then proves on screen.
// ===================================================================================================
const started = planAction(plan, SAFEFIX, 'start');
assert.equal(started.performed, true);
assert.equal(byId(started.plan, SAFEFIX).status, 'done');
assert.match(started.sentence, /^9 things were fixed\./);
// performed false is NOT an error: it is an answer with a sentence a person can act on.
const refusedAsst = planAction(plan, DESCRIBE, 'start');
assert.equal(refusedAsst.performed, false);
assert.match(refusedAsst.sentence, /your assistant writes it/);
assert.equal(byId(refusedAsst.plan, DESCRIBE).status, 'ready', 'and nothing changed');
const refusedYou = planAction(plan, CLEANUP, 'start');
assert.equal(refusedYou.performed, false);
assert.match(refusedYou.sentence, /only once you approve it\. Work out its changes, check them and approve them on the Plan, or open Lineage and do it there\./);
const refusedNotSafe = planAction(plan, CHECK, 'start');
assert.equal(refusedNotSafe.performed, false);
assert.match(refusedNotSafe.sentence, /^Nothing in this release runs this one for you yet\. Open Validation/);
const refusedSkipped = planAction(plan, RELATIONSHIP, 'start');
assert.equal(refusedSkipped.performed, false);
assert.match(refusedSkipped.sentence, /^This item is set aside\./);
assert.equal(planAction(plan, 'no/such-item', 'start').performed, false);
// The engine performs FIVE verbs now and throws on the rest, so the stub must too: a page that started
// sending one it does not perform has to fail here and not quietly in front of a person. `run-all` and
// `cancel` are still the PAGE's own vocabulary for walking the list and stopping the walk, and neither
// of those ever reaches the engine; the engine `cancel` is a different thing with the same word, and it
// drops a set of changes.
assert.throws(() => planAction(plan, SAFEFIX, 'run-all'), /This release performs start, propose, approve, cancel, done/);
assert.throws(() => planAction(plan, SAFEFIX, 'change'), /This release performs start, propose, approve, cancel, done/);

const skipped = planSkip(plan, CLEANUP, 'the old key is still read by finance');
assert.equal(byId(skipped, CLEANUP).status, 'skipped');
assert.equal(byId(skipped, CLEANUP).skipReason, 'the old key is still read by finance');
assert.ok(byId(skipped, CLEANUP).skippedUtc, 'and the plan records when');
assert.equal(byId(planSkip(plan, CLEANUP, '   '), CLEANUP).skipReason, 'not now',
  'a blank reason is still a reason on the row, never an empty quote');
assert.equal(byId(planUnskip(skipped, CLEANUP), CLEANUP).status, 'ready');
assert.equal(byId(planUnskip(skipped, CLEANUP), CLEANUP).skipReason, undefined);

// ===================================================================================================
// 17. The review, through the real operations: keep, drop, approve, cancel, propose again.
//     ONE of the five writes. The digest is the engine's and the page carries it back untouched.
// ===================================================================================================
{
  const start = makePlan({ proposed: true });
  const digestBefore = P.changeSetOf(byId(start, DESCRIBE)).digest;

  // ---- keep and drop write NOTHING and MOVE THE DIGEST --------------------------------------------
  // THE HANDLE, not the line's name and not the plan item's id. The engine looks a line up by this
  // alone, and every other id of the right type is accepted by the same method and decides something
  // else, with nothing thrown anywhere.
  const lineId = 'ci-0001';
  assert.equal(planFindLine(start, lineId).line.id, 'DESC-M/measure:Key Measures/Margin %',
    'the handle really does reach that line');
  assert.equal(planFindLine(start, 'DESC-M/measure:Key Measures/Margin %'), null,
    'and the line NAME reaches nothing, which is what a page sending the wrong one would quietly get');
  const afterDrop = planSetLine(start, lineId, false);
  const droppedSet = P.changeSetOf(byId(afterDrop, DESCRIBE));
  assert.equal(P.keptCount(droppedSet), 3, 'the kept count falls with the line');
  assert.equal(P.approveLabel(droppedSet), 'Approve 3 kept', 'and the button follows the list above it');
  assert.notEqual(droppedSet.digest, digestBefore,
    'the digest is over the lines, so deciding one moves it: that is what makes an old approve refusable');
  assert.equal(byId(afterDrop, DESCRIBE).status, 'proposed', 'and nothing was written');
  const backAgain = planSetLine(afterDrop, lineId, true);
  assert.equal(P.keptCount(P.changeSetOf(byId(backAgain, DESCRIBE))), 4, 'a decision can be changed');
  assert.equal(P.changeSetOf(byId(backAgain, DESCRIBE)).digest, digestBefore,
    'and the digest comes back with it, because it describes the lines and nothing else');

  // ---- approve is the one that WRITES -------------------------------------------------------------
  const approved = planReviewAction(start, DESCRIBE, 'approve', digestBefore);
  assert.equal(approved.performed, true, 'this is the verb that changes the model');
  assert.equal(approved.revision, 42);
  assert.equal(P.itemState(byId(approved.plan, DESCRIBE)), 'applied',
    'and the item is APPLIED, not done: whether the gap went is worked out on the next compose');
  assert.equal(P.changeSetOf(byId(approved.plan, DESCRIBE)).checkpoint, 'undo:42',
    'with one undo entry, which is the promise the page prints under the button');
  assert.match(approved.sentence, /^4 changes written as one step\./);

  // A digest that is not the one on screen is REFUSED. This is the whole reason the page carries the
  // engine's string back untouched instead of computing one.
  const wrong = planReviewAction(start, DESCRIBE, 'approve', 'not-the-digest-you-were-shown');
  assert.equal(wrong.performed, false);
  assert.match(wrong.sentence, /^These are not the changes you were shown/);
  assert.equal(P.itemState(byId(wrong.plan, DESCRIBE)), 'proposed', 'and nothing was written');

  // A STALE set is refused even with the right digest: the model moved under it.
  const staleStart = makePlan({ stale: true });
  const staleDigest = P.changeSetOf(byId(staleStart, DESCRIBE)).digest;
  const refusedStale = planReviewAction(staleStart, DESCRIBE, 'approve', staleDigest);
  assert.equal(refusedStale.performed, false);
  assert.match(refusedStale.sentence, /^The model changed after these changes were worked out/);

  // ---- cancel drops the lot and writes nothing ----------------------------------------------------
  const cancelled = planReviewAction(start, DESCRIBE, 'cancel');
  assert.equal(cancelled.performed, false, 'dropping a set changes no model, so performed is false');
  assert.equal(P.changeSetOf(byId(cancelled.plan, DESCRIBE)), null, 'the set is gone');
  assert.equal(P.itemState(byId(cancelled.plan, DESCRIBE)), 'gap', 'and the item is a gap again');

  // ---- propose works them out again, and writes nothing -------------------------------------------
  const again = planReviewAction(cancelled.plan, DESCRIBE, 'propose');
  assert.equal(again.performed, false);
  assert.equal(P.itemState(byId(again.plan, DESCRIBE)), 'proposed');
  assert.equal(P.changeLines(P.changeSetOf(byId(again.plan, DESCRIBE))).length, 5);
}

// ===================================================================================================
// FINDINGS THAT OUTLIVED THE OLD PAGE (slice 9 integration). The Plan at the heart cut this file to
// what the new page still has, and five FIXED rows in docs/findings.md named checks that went with the
// cut. Four guard code the new page still runs (panelFor, the Keep or Drop call, isStale, ChangeList),
// so their checks are back here. The fifth, F-192, guarded the retired map; its defect (a table shows
// nothing open because the engine named a COLUMN of it, not the table) came back in the new page's
// object filter, so its check is written against that filter.
// ===================================================================================================
{
  // ---- F-213 (Codex, PR 388): which panel a route asks for, including the route that asks for none.
  assert.equal(P.panelFor('changes'), 'changes', 'an optimize route asks for the review panel');
  assert.equal(P.panelFor('draft'), 'draft', 'a spec route asks for the draft editor');
  assert.equal(P.panelFor(undefined), 'none', 'and a plain plan route asks for NO panel, which closes one');
  assert.equal(P.panelFor(null), 'none');
  assert.equal(P.panelFor('something else'), 'none',
    'anything this page does not know is the plan, never whatever panel happened to be open');
}
{
  // ---- F-221 (Astra fix-review 8): a Keep or Drop reply is a ChangePlanView, never the plan.
  const reply = planChangeView(makePlan({ proposed: true }), 'ci-0001');
  assert.ok(Array.isArray(reply.items) && reply.items.length > 0, 'a ChangePlanView carries items, which is what fooled the page');
  for (const row of reply.items) {
    assert.equal(row.section, undefined, 'a change line has no section: it is not a plan item');
    assert.equal(row.changeSet, undefined, 'and no change set of its own');
  }
  const planPage = srcOf('plan.tsx');
  const decide = planPage.slice(planPage.indexOf('const decideLine'), planPage.indexOf('const runPlan'));
  assert.match(decide, /await rpc\('setPlanItem', lineId, null, kept\);\n\s*setPlan\(normalize\(await rpc<P\.Plan>\('getModelPlan'\)\)\);/,
    'a Keep or Drop reply is never drawn as the plan: the page reads the plan again from getModelPlan');
  assert.doesNotMatch(decide, /=\s*await rpc[^(]*\('setPlanItem'/, 'and the reply is not kept at all');
}
{
  // ---- F-222 (Astra fix-review 9): the engine sends two stale flags and the page reads both.
  const after = byId(makePlan({ staleAfter: true }), DESCRIBE);
  const set = P.changeSetOf(after);
  assert.equal(set.staleAtProposal, false, 'it was fine when it was worked out');
  assert.equal(set.stale, true, 'and it is not fine now, which is the flag the engine recomputes');
  assert.equal(P.isStale(set), true, 'so the page reads it as stale');
  assert.equal(P.reviewActions(after, true).includes('approve'), false, 'no Approve, because the engine would refuse it');
  assert.equal(P.isStale({ lines: [], staleAtProposal: true }), true, 'the stored flag alone is stale');
  assert.equal(P.isStale({ lines: [], stale: true }), true, 'and so is the live flag alone');
  assert.equal(P.isStale({ lines: [] }), false, 'a set that says neither is not stale');
  assert.equal(P.isStale(null), false);
}
{
  // ---- F-224 (Astra fix-review 11): the approval view shows the values being approved.
  const Page = load(join(webview, 'src', 'plan.tsx'));
  const set = P.changeSetOf(byId(makePlan({ proposed: true }), DESCRIBE));
  const list = renderToStaticMarkup(React.createElement(Page.ChangeList, { set, position: 3, busy: null, onDecideLine: () => {} }));
  const shown = readable(list);
  assert.match(shown, /Gross margin as a share of net sales/, 'the description being approved is readable');
  assert.match(shown, /Net sales from the start of the fiscal year/, 'every line, not just the first');
  assert.match(shown, /Description/, 'and which property each one sets');
  assert.match(shown, /empty/, 'and a value that is empty now says so, rather than drawing a blank cell');
}
{
  // ---- F-192 (Astra slice 2 review 15), on the Plan at the heart. Choosing a table in the tree filters
  // the Plan to that table. The engine names the COLUMNS a row touches ("column:Sales/CustomerKey"), not
  // the table, so a filter that matched only "table:Sales" and its "table:Sales/..." children showed
  // Sales with nothing open while Sales had an open row: the defect the old map had, on the new filter.
  const rows = [
    { id: 'safefix/Visibility', doer: 'semanticus', state: 'ready', objects: ['column:Sales/CustomerKey'] },
    { id: 'describe/m', doer: 'assistant', state: 'ready', objects: ['measure:Sales/Margin %'] },
    { id: 'other', doer: 'you', state: 'ready', objects: ['column:Sales Targets/Amount', 'column:Store/City'] },
    { id: 'whole', doer: 'you', state: 'ready', objects: ['table:Sales'] },
  ];
  const shownFor = (ref) => P.workView(rows, { object: ref }).rows.map((r) => r.id);
  assert.deepEqual(shownFor('table:Sales'), ['safefix/Visibility', 'describe/m', 'whole'],
    'Astra finding 15 on the Plan at the heart: a table shows the rows the engine names on its columns and measures');
  assert.deepEqual(shownFor('table:sales'), ['safefix/Visibility', 'describe/m', 'whole'], 'whatever the case');
  assert.deepEqual(shownFor('column:Sales/CustomerKey'), ['safefix/Visibility'], 'a column shows only its own rows');
  assert.deepEqual(shownFor('table:Store'), ['other'], 'and a table whose name starts another table\'s name is not confused with it');
}
{
  // ---- THE FORMAT FILL RULE, addendum 2 item 11. A Jev format line the engine could not prove is a SUGGESTION: its plain note is
  // words on the row (not only a hover), and it carries no check mark and no "sure", because both would vouch for it.
  const Page = load(join(webview, 'src', 'plan.tsx'));
  const NOTE = 'Jev suggests this format; check it fits before you run it.';
  const proved = {
    id: 'set_measure_format/measure:Sales/Orders#formatString', planItemId: 'ci-0201', line: '+ Orders: format as #,##0',
    op: 'set_measure_format', target: 'measure:Sales/Orders', property: 'formatString', before: '(none)', after: '#,##0', kept: true,
    provenance: { kind: 'decision', point: 'measure.folder_and_format', rule: null, confidence: 0.97, confidenceWord: 'sure' },
    reason: 'It counts whole things, so it shows no decimal places.', check: { exists: true, text: '#,##0 is the standard whole numbers format in the format library.' },
    suggestion: false, note: null,
  };
  const suggestion = {
    id: 'set_measure_format/measure:Sales/Order Share#formatString', planItemId: 'ci-0202', line: '+ Order Share: format as #,##0',
    op: 'set_measure_format', target: 'measure:Sales/Order Share', property: 'formatString', before: '(none)', after: '#,##0', kept: false,
    provenance: { kind: 'decision', point: 'measure.folder_and_format', rule: null, confidence: 0.97, confidenceWord: null },
    reason: "Jev's reading, not proved: it counts whole things, so it shows no decimal places.", check: null,
    suggestion: true, note: NOTE,
  };
  assert.equal(P.suggestionNote(suggestion), NOTE, 'the note is the words the page prints');
  assert.equal(P.suggestionNote({ ...suggestion, note: null }), NOTE, 'a suggestion with no note sent still says so in the same words');
  assert.equal(P.suggestionNote(proved), null, 'a proved line has no note');
  assert.equal(P.jevWord(proved), 'sure');
  assert.equal(P.jevWord(suggestion), null, 'no confidence word on a suggestion');
  assert.equal(P.jevWord({ ...suggestion, provenance: proved.provenance }), null, 'not even when an engine still sends the word');
  assert.ok(P.checkTick(proved)?.startsWith('Checked: '), 'a proved line keeps its check mark');
  assert.equal(P.checkTick(suggestion), null, 'a suggestion has no check mark');
  assert.equal(P.checkTick({ ...suggestion, check: proved.check }), null, 'not even when an engine still sends a check');

  const set = { id: 's', digest: 'd', state: 'proposed', lines: [proved, suggestion], refused: [], staleAtProposal: false, stale: false };
  const html = renderToStaticMarkup(React.createElement(Page.ChangeList, { set, position: 1, busy: null, onDecideLine: () => {} }));
  const rows = html.split('data-line="').slice(1);
  assert.equal(rows.length, 2, 'both lines are drawn');
  const [provedRow, suggestionRow] = rows;
  assert.match(readable(provedRow), /Jev chose this/, 'the recorded Jev decision is visible beside its value');
  for (const [word, kept, label] of [
    ['unsure', false, 'Jev asks you'],
    ['unsure', true, 'Jev chose this'],
    ['likely', false, 'Jev chose this'],
    ['sure', false, 'Jev chose this'],
  ]) {
    const line = { ...proved, kept, provenance: { ...proved.provenance, confidenceWord: word } };
    const row = readable(renderToStaticMarkup(React.createElement(Page.ChangeList, {
      set: { ...set, lines: [line] }, position: 1, busy: null, onDecideLine: () => {},
    })));
    assert.ok(row.includes(label), `${word}, kept=${kept}: ${label}`);
    if (label === 'Jev asks you') assert.doesNotMatch(row, /Jev chose this/, 'an unsure dropped line asks the person');
  }
  assert.doesNotMatch(readable(suggestionRow), /Jev chose this/, 'a suggestion does not claim a choice was applied');
  assert.match(readable(suggestionRow), /Jev suggests this format; check it fits before you run it\./, 'the note is visible text on the suggestion row');
  assert.doesNotMatch(suggestionRow, /plan-tick|plan-jevword|data-jev-word/, 'and the row has no check mark and no confidence word');
  assert.doesNotMatch(readable(suggestionRow), /\bsure\b/, 'so the word "sure" is not on it');
  assert.match(readable(suggestionRow), /Jev's reading, not proved/, 'and its reason is put as Jev\'s reading');
  assert.match(provedRow, /plan-tick/, 'the proved row keeps its check mark');
  assert.match(provedRow, /data-jev-word="sure"/, 'and its word');
  assert.doesNotMatch(readable(provedRow), /Jev suggests this format/, 'and carries no suggestion note');
}
{
  // ---- THE FORMAT FILL RULE, addendum 3 items 18 and 19.
  // 18: blank characters a person typed are a VALUE, so the page never calls them "empty" (a tick given on that word would write over them).
  const spaced = P.changeValues({ property: 'formatString', before: '   ', after: '#,##0' });
  assert.notEqual(spaced.beforeLabel, 'empty', 'typed spaces are not empty');
  assert.match(spaced.beforeLabel, /3 blank characters you typed/, 'the page says what is there');
  assert.equal(P.changeValues({ property: 'formatString', before: '\u00a0', after: '#,##0' }).beforeLabel, '1 blank character you typed', 'a no-break space too');
  assert.equal(P.changeValues({ property: 'formatString', before: '', after: '#,##0' }).beforeLabel, 'empty', 'nothing at all is still empty');

  // 19: the Pending changes panel shows a Jev suggestion as a suggestion, with its note as text, never as "safe".
  const Optimize = load(join(webview, 'src', 'optimize.tsx'));
  const NOTE = 'Jev suggests this format; check it fits before you run it.';
  const base = { id: 'ci-0301', objectRef: 'measure:Sales/Share', objectName: 'Share', kind: 'set_measure_format', source: 'jev', category: 'Formatting',
    severity: 'Info', risk: 'safe', target: 'formatString', title: 'Format as #,##0', rationale: "Jev's reading, not proved: it counts whole things.",
    before: '(none)', after: '#,##0', status: 'proposed' };
  const draw = (it) => renderToStaticMarkup(React.createElement(Optimize.Row, { it, draft: '', onDraft: () => {}, onToggle: () => {}, onSave: () => {}, onReject: () => {}, onGive: () => ({}), onApply: () => {} }));
  const asSuggestion = readable(draw({ ...base, suggestion: true, checkText: NOTE }));
  assert.match(asSuggestion, /suggestion/, 'the chip says suggestion');
  assert.doesNotMatch(asSuggestion, /\bsafe\b/, 'and never "safe"');
  assert.match(asSuggestion, /Jev suggests this format; check it fits before you run it\./, 'the note is visible text on the row');
  const asProved = readable(draw({ ...base, suggestion: false, status: 'approved', checkText: '#,##0 is the standard whole numbers format.' }));
  assert.match(asProved, /\bsafe\b/, 'control: a proved line keeps its chip');
  assert.doesNotMatch(asProved, /Jev suggests this format/, 'and carries no suggestion note');
  assert.match(srcOf('optimize.tsx'), /from Jev/, 'the summary counts Jev lines in a category of their own');
}
