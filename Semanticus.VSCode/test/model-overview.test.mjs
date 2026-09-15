// The Model Overview landing page (Model > Overview). Two halves, on purpose:
//
//  1. The facts-to-copy layer is a plain ES module (webview/src/overviewfacts.mjs), so the sentence a person reads on
//     the landing page is checked by RUNNING the shipped code, not by matching the JSX source. Every clause of the
//     hero sentence exists only when its fact is real, and this file proves each one appears and disappears alone.
//  2. The states a card can be in (loading, error, empty, selected) are structural, so those are read from the
//     source: a landing page whose cards can render blank is the failure this half refuses.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  blockLabel, checksSummary, formatBytes, formatCount, heroClauses, heroSentence,
  instructionCount, memoryBand, tableKind, tableRows,
} from '../webview/src/overviewfacts.mjs';
// The newer builders are reached through the namespace on purpose: a missing export then fails the ONE
// check that needs it, instead of a module-level SyntaxError that takes the other forty checks with it.
import * as facts from '../webview/src/overviewfacts.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const page = readFileSync(resolve(root, 'webview', 'src', 'modelhome.tsx'), 'utf8');
const styles = readFileSync(resolve(root, 'webview', 'src', 'styles.css'), 'utf8');
const app = readFileSync(resolve(root, 'webview', 'src', 'App.tsx'), 'utf8');
const harness = readFileSync(resolve(root, 'tools', 'uishot', 'harness.html'), 'utf8');

const failures = [];
const check = (name, fn) => { try { fn(); } catch (e) { failures.push(`${name}: ${e.message}`); } };
const has = (src, re, msg) => { if (!re.test(src)) throw new Error(msg); };
const hasNot = (src, re, msg) => { if (re.test(src)) throw new Error(msg); };

// ---- the hero sentence: one clause per real fact -------------------------------------------------
const SHAPE = { tables: 11, measures: 12, relationships: 8 };

check('hero: the shape clause always leads', () => {
  const clauses = heroClauses(SHAPE);
  assert.equal(clauses[0], '11 tables, 12 measures and 8 relationships.');
  assert.equal(clauses.length, 1, 'with no scan, no instructions and no edits there is exactly one clause');
});

check('hero: singulars read as singulars', () => {
  assert.equal(heroClauses({ tables: 1, measures: 1, relationships: 1 })[0], '1 table, 1 measure and 1 relationship.');
});

check('hero: the memory clause appears only with a scan', () => {
  const withScan = heroSentence({ ...SHAPE, memoryBytes: 48500000, largestTable: 'Sales', largestShare: 66 });
  assert.match(withScan, /The model uses 46\.3 MB in memory and 66% of it sits in Sales\./);
  const noScan = heroSentence({ ...SHAPE, memoryBytes: null, largestTable: null, largestShare: 0 });
  assert.doesNotMatch(noScan, /memory/, 'no scan means no memory clause at all, never a placeholder');
  assert.doesNotMatch(noScan, /0 MB|unknown|not scanned/i);
});

check('hero: a scan with no single largest table still states the total', () => {
  const only = heroSentence({ ...SHAPE, memoryBytes: 48500000, largestTable: null, largestShare: 0 });
  assert.match(only, /The model uses 46\.3 MB in memory\./);
  assert.doesNotMatch(only, /sits in/);
});

check('hero: the instructions clause appears only when instructions exist', () => {
  const withText = heroSentence({ ...SHAPE, instructionCount: 5 });
  assert.match(withText, /Your assistant follows 5 instructions written for this model\./);
  assert.match(heroSentence({ ...SHAPE, instructionCount: 1 }), /follows 1 instruction written/);
  assert.doesNotMatch(heroSentence({ ...SHAPE, instructionCount: 0 }), /assistant/,
    'nothing written means the hero says nothing about instructions');
});

// Astra spot review 2, finding 2: the count this page holds is the edits THIS WEBVIEW SAW since the model
// opened. It is not a destination comparison, so the hero may name the number and must not name its fate.
check('hero: the edits clause counts this session and never claims a publication state', () => {
  assert.match(heroSentence({ ...SHAPE, sessionEdits: 1 }), /1 edit this session\./);
  assert.match(heroSentence({ ...SHAPE, sessionEdits: 3 }), /3 edits this session\./);
  assert.doesNotMatch(heroSentence({ ...SHAPE, sessionEdits: 0 }), /session|publish/i);
  assert.doesNotMatch(heroSentence({ ...SHAPE, sessionEdits: 3 }), /publish/i,
    'the hero must not say whether those edits are published: this page has no way to know');
});

check('hero: every clause together reads as one paragraph', () => {
  const all = heroSentence({ ...SHAPE, memoryBytes: 48500000, largestTable: 'Sales', largestShare: 66, instructionCount: 5, sessionEdits: 1 });
  assert.equal(all, '11 tables, 12 measures and 8 relationships. The model uses 46.3 MB in memory and 66% of it sits in Sales. Your assistant follows 5 instructions written for this model. 1 edit this session.');
  assert.doesNotMatch(all, /—/, 'no em dashes in any user-facing copy');
});

// Astra spot review 2, follow-up: the loading and failed scenes both printed "0 tables, 0 measures and 0
// relationships" above their status line. A model nobody has read yet has an UNKNOWN shape, and an empty
// model is the only thing allowed to read as zero.
check('hero: a model that has not been read yet states no counts at all', () => {
  const loading = heroClauses({ state: 'loading', ...SHAPE, instructionCount: 5, sessionEdits: 4 });
  assert.deepEqual(loading, ['Reading the model…'], 'while the graph is loading the hero says only that');
  assert.doesNotMatch(loading.join(' '), /[0-9]/, 'no number may appear before the model has been read');

  const failed = heroClauses({ state: 'error', ...SHAPE, instructionCount: 5, sessionEdits: 4 });
  assert.deepEqual(failed, [], 'a failed read leaves the error state to speak for itself');

  // A model that really was read and really is empty keeps its zeros: that is a measurement.
  assert.equal(heroClauses({ state: 'ready', tables: 0, measures: 0, relationships: 0 })[0],
    '0 tables, 0 measures and 0 relationships.');
  assert.equal(heroClauses({ tables: 0, measures: 0, relationships: 0 })[0],
    '0 tables, 0 measures and 0 relationships.', 'an unstated state still means a real read');
});

check('instructionCount counts written lines, not blank ones', () => {
  assert.equal(instructionCount('one\n\ntwo\n   \nthree'), 3);
  assert.equal(instructionCount(''), 0);
  assert.equal(instructionCount(null), 0);
});

check('sizes and counts read the way Size by table reads them', () => {
  assert.equal(formatBytes(48500000), '46.3 MB');
  assert.equal(formatBytes(4096), '4 KB');
  assert.equal(formatCount(2325450), '2.3 M');
  assert.equal(formatCount(3650), '3,650');
});

// ---- the composition band ------------------------------------------------------------------------
const GRAPH_TABLES = [
  { name: 'Sales', ref: 'table:Sales', measures: 12, columns: 14 },
  { name: 'Date', ref: 'table:Date', measures: 0, columns: 16, isDateTable: true },
  { name: 'Customer', ref: 'table:Customer', measures: 0, columns: 12 },
  { name: 'Budget', ref: 'table:Budget', measures: 0, columns: 5, isCalculated: true },
  { name: 'Fields by Measure', ref: 'table:Fields by Measure', measures: 0, columns: 3, isCalculated: true, isFieldParameter: true },
];
const SCAN_TABLES = [
  { name: 'Sales', size: 32000000, rows: 2300000 },
  { name: 'Customer', size: 8200000, rows: 18500 },
  { name: 'Date', size: 2000000, rows: 3650 },
];

const sum = (blocks) => blocks.reduce((n, b) => n + b.share, 0);

check('band: block shares add up to 100 with a scan', () => {
  const band = memoryBand({ tables: GRAPH_TABLES, scanTables: SCAN_TABLES });
  assert.equal(band.basis, 'memory');
  assert.ok(Math.abs(sum(band.blocks) - 100) < 1e-9, `shares summed to ${sum(band.blocks)}`);
  assert.equal(band.blocks.length, GRAPH_TABLES.length, 'every table is a block, including the ones the scan missed');
  assert.equal(band.blocks[0].name, 'Sales');
  assert.ok(Math.abs(band.blocks[0].share - 75.8) < 0.1, 'the biggest table takes its measured share');
});

check('band: without a scan the blocks fall back to columns', () => {
  const band = memoryBand({ tables: GRAPH_TABLES, scanTables: null });
  assert.equal(band.basis, 'columns');
  assert.ok(Math.abs(sum(band.blocks) - 100) < 1e-9, `shares summed to ${sum(band.blocks)}`);
  assert.equal(band.blocks[0].name, 'Date', 'the widest table by columns leads the fallback band');
  assert.equal(band.blocks[0].bytes, null, 'the fallback never invents a size');
});

check('band: an empty model produces no blocks and no divide by zero', () => {
  const band = memoryBand({ tables: [], scanTables: null });
  assert.deepEqual(band.blocks, []);
  assert.equal(band.total, 0);
});

check('band: table kinds carry through to the block colour', () => {
  const band = memoryBand({ tables: GRAPH_TABLES, scanTables: SCAN_TABLES });
  const kind = (name) => band.blocks.find((b) => b.name === name).kind;
  assert.equal(kind('Sales'), 'data');
  assert.equal(kind('Date'), 'date');
  assert.equal(kind('Budget'), 'calculated');
  assert.equal(kind('Fields by Measure'), 'fieldParameter', 'a field parameter is not just a calculated table');
  assert.equal(tableKind({ isCalculated: true, isDateTable: true }), 'calculated');
});

check('band: a label is dropped, never clipped, when its block is too narrow', () => {
  // The label decides on the block's OWN drawn width in pixels, not on a share of the band. It used to take
  // a share and re-derive the width, which is the same arithmetic the renderer got wrong (finding 3).
  assert.equal(blockLabel('Sales', 860), 'full');
  assert.equal(blockLabel('Customer', 80), 'name');
  assert.equal(blockLabel('Geography', 6), 'none');
  assert.equal(blockLabel('Geography', 2), 'none', 'a sliver never carries a label');
});

// ---- the band's GEOMETRY: what is drawn must be what is claimed -----------------------------------
// Astra spot review 2, finding 3. Sales was labelled 66% and drew 762px of a 1330px band (57.3%) at 1440,
// and 496.20px of 926px (53.6%) at 1000; each zero-byte table drew about 16.5px instead of the 2px sliver
// the comment in styles.css claimed. The cause was 16px of horizontal padding on every flex item, which
// flex-basis: 0 does not remove. The fix moves the padding inside the block, so this function models the
// SAME resolution the browser performs: the band minus its gaps, minus a sliver for each block too small
// to draw, divided by share.
const SCANNED = () => memoryBand({ tables: GRAPH_TABLES, scanTables: SCAN_TABLES }).blocks;

check('band geometry: every block draws its stated share of the band, inside one pixel', () => {
  for (const bandPx of [1330, 926]) {
    const laid = facts.bandGeometry(SCANNED(), bandPx);
    const gaps = facts.BAND_GAP_PX * (laid.length - 1);
    const slivers = laid.filter((b) => b.px <= facts.BAND_SLIVER_PX + 1e-9).length * facts.BAND_SLIVER_PX;
    const drawable = bandPx - gaps - slivers;
    for (const block of laid) {
      if (block.px <= facts.BAND_SLIVER_PX + 1e-9) continue;
      const expected = (block.share / 100) * drawable;
      assert.ok(Math.abs(block.px - expected) < 1,
        `at ${bandPx}px ${block.name} claims ${block.share.toFixed(2)}% but draws ${block.px.toFixed(2)}px, wanted ${expected.toFixed(2)}px`);
    }
    const drawn = laid.reduce((n, b) => n + b.px, 0);
    assert.ok(Math.abs(drawn - (bandPx - gaps)) < 0.5, `the blocks plus the gaps must fill the band exactly (drew ${drawn} of ${bandPx - gaps})`);
  }
});

check('band geometry: a table measured at zero bytes is a sliver, not a block', () => {
  const blocks = memoryBand({
    tables: GRAPH_TABLES,
    scanTables: [{ name: 'Sales', size: 32000000, rows: 2300000 }],
  }).blocks;
  const laid = facts.bandGeometry(blocks, 1330);
  const zero = laid.filter((b) => b.share === 0);
  assert.equal(zero.length, 4, 'the four tables the scan measured at nothing');
  for (const block of zero) assert.equal(block.px, 2, `${block.name} must draw the 2px sliver`);
  assert.ok(laid.find((b) => b.name === 'Sales').px > 1300, 'the one measured table takes the rest of the band');
});

check('band geometry: the drawn percentage a person could measure matches the printed one', () => {
  for (const bandPx of [1330, 926]) {
    const laid = facts.bandGeometry(SCANNED(), bandPx);
    const drawn = laid.reduce((n, b) => n + b.px, 0);
    for (const block of laid) {
      if (block.px <= facts.BAND_SLIVER_PX + 1e-9) continue;
      const asDrawn = (100 * block.px) / drawn;
      assert.ok(Math.abs(asDrawn - block.share) < 1,
        `at ${bandPx}px ${block.name} prints ${Math.round(block.share)}% and measures ${asDrawn.toFixed(2)}%`);
    }
  }
});

check('band geometry: the shipped CSS is the geometry this function models', () => {
  const rule = /\.overview-comp-block\s*\{([^}]*)\}/.exec(styles);
  if (!rule) throw new Error('the band block rule is gone from styles.css');
  has(rule[1], /padding:\s*0/, 'the block itself must carry NO padding: that padding is what distorted every width');
  has(rule[1], /min-width:\s*2px/, 'the sliver floor must stay 2px, the width a zero-sized table draws');
  has(rule[1], /flex-basis:\s*0/, 'width must follow share alone');
  has(styles, /\.overview-comp\s*\{[^}]*gap:\s*2px/, 'the 2px gap the geometry deducts must stay 2px');
  has(styles, /\.overview-comp-label\s*\{[^}]*padding:\s*6px 8px/, 'the label inside the block carries the padding instead');
  hasNot(rule[1], /padding:\s*6px 8px/, 'the block must not take the label padding back');
});

// ---- the tables card ------------------------------------------------------------------------------
const SCAN_COLUMNS = [
  { table: 'Sales', column: 'SalesAmount', dataSize: 9000000, dictionarySize: 200000, hashIndexSize: 100000 },
  { table: 'Customer', column: 'CustomerName', dataSize: 600000, dictionarySize: 2400000, hashIndexSize: 1300000 },
];

check('tables: each row carries the three measured parts plus the unmeasured rest', () => {
  const rows = tableRows({ tables: GRAPH_TABLES, scanTables: SCAN_TABLES, scanColumns: SCAN_COLUMNS });
  const sales = rows.find((r) => r.name === 'Sales');
  assert.deepEqual(sales.segments.map((s) => s.part), ['data', 'dict', 'hash', 'rest']);
  const width = sales.segments.reduce((n, s) => n + s.width, 0);
  assert.ok(Math.abs(width - sales.share) < 1e-9, 'a row bar is the table share, split into its parts');
  assert.equal(sales.measures, 12);
  assert.equal(sales.columns, 14);
  assert.equal(sales.ref, 'table:Sales');
});

check('tables: without a scan a row is one bar by column share', () => {
  const rows = tableRows({ tables: GRAPH_TABLES, scanTables: null });
  assert.deepEqual(rows[0].segments.map((s) => s.part), ['columns']);
  assert.ok(Math.abs(rows.reduce((n, r) => n + r.segments[0].width, 0) - 100) < 1e-9);
});

// ---- the health cards ------------------------------------------------------------------------------
const health = (over) => ({ runs: [{ runId: 'r1', health: { checked: 5, passed: 4, failed: 0, notVerifiable: 1, missing: 0, ...over } }] });

check('checks: the headline counts passes and the unknowns get their own line', () => {
  const s = checksSummary(health());
  assert.equal(s.headline, '4 of 5 pass');
  assert.match(s.detail, /1 check could not be checked/);
  assert.equal(s.state, 'unknown');
});

check('checks: a failure reads as a failure, not as an unknown', () => {
  const s = checksSummary(health({ passed: 3, failed: 1, notVerifiable: 0 }));
  assert.equal(s.state, 'warn');
  assert.match(s.detail, /1 check did not pass/);
});

check('checks: no run yet is an empty state, never a zero score', () => {
  const s = checksSummary({ runs: [] });
  assert.equal(s.state, 'none');
  assert.match(s.headline, /No tests run yet/);
  assert.doesNotMatch(s.headline, /0 of 0/);
});

// ---- the Checks card: the run you just triggered, or the last recorded one, never a blend -----------
// Astra spot review 2, finding 1. Run tests awaited runTests, THREW THE RESULT AWAY and re-read the saved
// history. runTests defaults to persist = false (EngineRpcTarget.cs:61) and the engine only appends history
// inside the persist branch (LocalEngine.TestSuite.cs:350), so the card kept showing the old saved result:
// 23 of 23 pass, immediately after a run of 23 failures. Running checks is free and must stay free, so the
// fix is to show the RETURNED run, labelled as unrecorded, not to start asking for paid persistence.
const SAVED_ALL_PASS = { runs: [{ runId: 'saved-1', when: '2026-09-12T06:10:00Z', live: true, health: { checked: 23, passed: 23, failed: 0, notVerifiable: 0, missing: 0 } }] };
const FRESH_ALL_FAIL = { runId: 'fresh-1', when: '2026-09-14T10:15:00Z', live: true, persisted: false, health: { checked: 23, passed: 0, failed: 23, notVerifiable: 0, missing: 0 } };

check('checks card: a fresh failing run wins over a saved passing history', () => {
  const card = facts.checksCard({ history: SAVED_ALL_PASS, latest: FRESH_ALL_FAIL });
  assert.equal(card.headline, '0 of 23 pass', `the card showed ${JSON.stringify(card.headline)}`);
  assert.equal(card.state, 'warn');
  assert.match(card.detail, /23 checks did not pass/);
  assert.match(card.source, /not recorded/i, 'an unrecorded run must say so rather than pass as history');
  assert.doesNotMatch(card.headline + ' ' + card.detail, /23 of 23 pass|Everything that was checked passed/,
    'the saved history must not leak into a card that is showing the fresh run');
});

check('checks card: a recorded run says so, and saved history is labelled with its own time', () => {
  const recorded = facts.checksCard({ history: SAVED_ALL_PASS, latest: { ...FRESH_ALL_FAIL, persisted: true } });
  assert.match(recorded.source, /recorded/i);
  assert.doesNotMatch(recorded.source, /not recorded/i);

  const saved = facts.checksCard({ history: SAVED_ALL_PASS, latest: null });
  assert.equal(saved.headline, '23 of 23 pass');
  assert.match(saved.source, /^Last recorded run, /, `saved history must name when it was recorded, got ${JSON.stringify(saved.source)}`);
  const when = new Date('2026-09-12T06:10:00Z');
  assert.ok(saved.source.includes(when.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })),
    'the recorded time is the run’s own time, read in the reader’s clock');
});

check('checks card: nothing run and nothing saved is the empty state', () => {
  const empty = facts.checksCard({ history: { runs: [] }, latest: null });
  assert.equal(empty.state, 'none');
  assert.match(empty.headline, /No tests run yet/);
  assert.equal(empty.source, null, 'an empty state has no run to date-stamp');
  assert.equal(facts.checksCard({}).state, 'none', 'no history object at all is the same empty state');
});

check('checks card: the run’s own note and error travel with it', () => {
  const noted = facts.checksCard({ history: SAVED_ALL_PASS, latest: { ...FRESH_ALL_FAIL, note: 'Offline: relationship probes are not verifiable.' } });
  assert.equal(noted.note, 'Offline: relationship probes are not verifiable.');
  const failed = facts.checksCard({ history: SAVED_ALL_PASS, latest: { ...FRESH_ALL_FAIL, error: 'The suite could not be read.' } });
  assert.equal(failed.error, 'The suite could not be read.');
});

// ---- the Changes card: only what this webview actually saw ------------------------------------------
// Astra spot review 2, finding 2. The card read "Nothing to publish / Every edit you have made is already
// published" from a list that starts EMPTY and only ever holds this webview's own change notifications.
check('changes card: zero edits never claims anything is published', () => {
  const card = facts.changesCard({ edits: 0 });
  assert.match(card.headline, /No edits this session/);
  assert.doesNotMatch(card.headline + ' ' + card.detail, /already published|Nothing to publish/i,
    'an empty local list is not evidence that the destination matches');
  assert.doesNotMatch(card.headline, /publish/i);
});

check('changes card: edits are counted and named as this session’s', () => {
  assert.equal(facts.changesCard({ edits: 4 }).headline, '4 edits this session');
  assert.equal(facts.changesCard({ edits: 1 }).headline, '1 edit this session');
  assert.doesNotMatch(facts.changesCard({ edits: 4 }).headline, /not published|unpublished/i,
    'the count is of edits seen here, not of edits known to be unpublished');
});

check('changes card: a capped count says it is a floor, not a total', () => {
  // App.tsx keeps at most 200 entries, so the 200th is "at least 200", never "exactly 200".
  assert.match(facts.changesCard({ edits: 200, capped: true }).headline, /200 or more edits this session/);
});

// ---- the page: every card has a loading, error and empty state --------------------------------------
check('page: the hero sentence comes from the shared builder, not from inline JSX', () => {
  has(page, /from '\.\/overviewfacts\.mjs'/, 'the page must import the tested facts-to-copy module');
  has(page, /heroClauses\(/, 'the hero paragraph must be built by the tested function');
});

check('page: the instructions card has a written and an unwritten state', () => {
  has(page, /Nothing written yet\./, 'no instructions must show the plain empty state, not a blank card');
  has(page, /Your assistant answers from the model alone\./, 'the empty state must say what happens instead');
  has(page, /\.present/, 'the empty state must be driven by Present from the engine');
  has(page, /Notes for people are separate\./, 'the card must point at Model notes for human notes');
  has(page, /of \$\{[^}]*limit[^}]*\} characters|characters/, 'the card must meter length against the limit');
});

check('page: the instructions editor saves through the engine relay', () => {
  has(page, /rpc[^\n]*'setAiInstructions'/, 'Save must call the same setter the assistant calls');
  has(page, /rpc<AiInstructions>\('getAiInstructions'/, 'the card must read the instructions back from the engine');
  has(page, /overview-instructions-error/, 'a failed save must show a plain error line');
});

check('page: the selected table row keeps aria-current (M18)', () => {
  has(page, /aria-current=\{isSelected \? 'true' : undefined\}/, 'the selected row must stay marked for a screen reader');
  has(page, /is-selected/, 'the selected row must stay visibly marked');
});

check('page: the band offers the scan when there is none, and never starts one by itself', () => {
  has(page, /No size scan yet\. Blocks show columns\./, 'the fallback caption must say what the blocks mean');
  has(page, /Scan sizes/, 'the fallback must offer the same scan Size by table runs');
  hasNot(page, /useEffect\([^)]*\)\s*=>\s*\{\s*void scan\(\)/, 'landing on Overview must never start a scan on its own');
});

check('page: every remote card states loading, error and empty', () => {
  for (const state of ['Checking…', 'Try again']) has(page, new RegExp(state), `the page must be able to show "${state}"`);
  has(page, /sem-spin/, 'a loading card must show the shared quiet spinner');
  has(page, /role="alert"/, 'an error must be announced');
});

check('page: the page renders when only getModelGraph succeeds', () => {
  has(page, /catch/, 'every best-effort call must swallow its own failure');
  const bestEffort = ['getAiInstructions', 'aiReadinessScan', 'listTestRuns'];
  for (const call of bestEffort) {
    has(page, new RegExp(`'${call}'`), `${call} must be called`);
  }
  hasNot(page, /await Promise\.all\(\[[^\]]*getModelGraph/, 'one failed side call must not take the page down with it');
});

check('page: Run tests renders the run it got back, and never asks for paid persistence', () => {
  has(page, /const run = await rpc<[^>]*>\('runTests'\)|=\s*await rpc<[^>]*>\('runTests'\)/,
    'the button must KEEP the run it awaited; discarding it is how a stale green survived a failing run');
  hasNot(page, /rpc\(\s*'runTests'\s*,\s*true/, 'running checks is free: the page must never request persistence');
  has(page, /checksCard\(/, 'the card state must come from the tested builder');
  hasNot(page, /checksSummary\(state\.value\)/, 'the card must not read saved history as though it were the latest run');
});

check('page: the Changes card describes local activity and claims no publication state', () => {
  hasNot(page, /Nothing to publish/, 'that headline claimed a destination comparison this page never made');
  hasNot(page, /Every edit you have made is already published/, 'the same claim in its detail line');
  hasNot(page, /not published/, 'the page cannot know that an edit is unpublished');
  has(page, /changesCard\(/, 'the card copy must come from the tested builder');
});

check('page: the hero says it is reading, rather than printing zeros it has not measured', () => {
  has(page, /state: load\.status/, 'the hero must be told whether the graph has actually been read');
});

check('page: the band is laid out by the tested geometry, not by raw flex growth', () => {
  has(page, /bandGeometry\(/, 'the block widths must come from the tested function');
  has(page, /overview-comp-label/, 'the label must sit in its own padded element inside the block');
});

check('page: the copy stays plain', () => {
  hasNot(page, /—/, 'no em dashes in the page source copy');
  hasNot(page, />\s*AI Assistant is told/, 'the card speaks of "your assistant"');
  hasNot(page, /What your assistant is told/, 'the card is no longer called What your assistant is told (Kane, 15 Sep)');
  has(page, /<h2>AI Instructions<\/h2>/, 'the instructions card is called AI Instructions');
});

// ---- the layout and the mount ----------------------------------------------------------------------
check('styles: the Overview has its own section and stacks below 980px', () => {
  has(styles, /\.overview-hero/, 'the hero must be styled from the shared sheet');
  has(styles, /\.overview-band/, 'the composition band must be styled from the shared sheet');
  has(styles, /max-width:\s*980px/, 'the two columns must stack on a narrow viewport');
  has(styles, /prefers-reduced-motion[\s\S]*overview|overview[\s\S]*prefers-reduced-motion/,
    'the Overview must respect reduced motion');
});

check('mount: App hands Overview the facts it already holds', () => {
  has(app, /<ModelHome sessionId=\{session\.sessionId\}/, 'Model must still open on the session-fenced model home');
  // The prop is named for what the number IS. It was called unpublishedEdits, which is what made the page
  // describe a destination comparison that nothing in this webview performs (Astra spot review 2, finding 2).
  has(app, /<ModelHome[\s\S]{0,600}sessionEdits=\{activity\.length - undoneCount\}/,
    'the Overview must be told how many edits this webview has seen, under that name');
  hasNot(app, /<ModelHome[\s\S]{0,600}unpublishedEdits=/, 'the old name asserted a fact the value does not carry');
  has(app, /StorageTabStateContext\.Consumer/, 'the Overview must read the scan the Size by table page already ran');
});

check('harness: the Overview states can be driven headlessly', () => {
  has(harness, /getAiInstructions:/, 'the harness must answer the instructions read');
  has(harness, /setAiInstructions:/, 'the harness must acknowledge a save');
  has(harness, /home === 'noinstr'|noinstr/, 'the harness must offer a no-instructions state');
  has(harness, /home === 'noscan'|noscan/, 'the harness must offer a no-scan state');
});

if (failures.length > 0) {
  console.error(`Model Overview: ${failures.length} check(s) failed.`);
  for (const f of failures) console.error(`  - ${f}`);
  process.exit(1);
}
console.log('Model Overview: all checks passed.');

// ---- Kane, 15 Sep: the AI Instructions card is as tall as the Tables card and scrolls inside ----------------
check('page and styles: the AI Instructions card matches the Tables card height and scrolls inside', () => {
  has(page, /overview-instructions-body/, 'the read-only text and the editor sit in a body that fills the card');
  has(page, /overview-instructions-scroll/, 'the body content lives in its own scroll container');
  has(styles, /\.overview-cols \{[^}]*align-items: stretch/, 'the two columns stretch to one height, so the card takes the Tables card height');
  has(styles, /\.overview-instructions-scroll \{[^}]*position: absolute[^}]*overflow-y: auto/, 'the body content scrolls inside the card instead of growing it');
  has(styles, /\.overview-instructions-body \{[^}]*position: relative[^}]*flex: 1 1 0/, 'the body takes the remaining height of the card');
});
