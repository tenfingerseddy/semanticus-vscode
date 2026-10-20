// Verifier tests for Validation C (m16/v-validation-view, 15 Oct). Each one is a wrong number shown as right, on the
// engine's real result shapes: the check's table is the shared DataTable drawn from the same data as its visual, so
// what the table marks and prints is what the person reads. These failed on 3cfcfa44 when they were written.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const req = createRequire(join(root, 'webview', 'package.json'));
const ts = req('typescript');
const { renderToStaticMarkup } = req('react-dom/server');
const React = req('react');

const cjs = (file) => {
  const js = ts.transpileModule(readFileSync(join(root, 'webview', 'src', file), 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: file,
  }).outputText;
  const mod = { exports: {} };
  new Function('require', 'module', 'exports', js)((name) => (name === './visuals' ? V : req(name)), mod, mod.exports);
  return mod.exports;
};
const V = cjs('visuals/visuals.tsx');
const M = cjs('validation-model.ts');

/** The table rows as the person reads them: the label, the printed cells, and whether the row is marked as differing. */
const tableRows = (data) => {
  const html = renderToStaticMarkup(React.createElement(V.DataTable, { d: data }));
  return [...html.matchAll(/<tr( class="([^"]*)")?><td>([^<]*)<\/td>(.*?)<\/tr>/g)]
    .filter((m) => !/<th/.test(m[0]))
    .map((m) => ({ label: m[3], bad: /qv-row-bad/.test(m[2] ?? ''), cells: [...m[4].matchAll(/<td[^>]*>(.*?)<\/td>/g)].map((c) => c[1].replace(/<[^>]+>/g, '')) }));
};

test('a comparison the engine judged a mismatch is marked as differing, even when the check allows an absolute difference', () => {
  // Allowed difference 100 (absolute). A small region 90 off is a match; a large region 500 off is a mismatch, though
  // 500 is a smaller share of its value than 90 is of 200.
  const outcome = {
    verdict: 'Fail', rowsTotal: 2, mismatches: 1, matches: 1,
    rows: [
      { context: 'Islands', sql: 200, dax: 290, delta: 90, verdict: 'Pass' },
      { context: 'Mainland', sql: 1000000, dax: 1000500, delta: 500, verdict: 'Fail' },
    ],
  };
  const cv = M.checkVisual(outcome, { title: 'Net Revenue', sourceLabel: 'the source', trusted: false });
  const rows = tableRows(cv.data);
  const mainland = rows.find((r) => r.label === 'Mainland');
  assert.ok(mainland, 'the mismatched row is in the table');
  assert.equal(mainland.bad, true, 'the engine said Mainland differs; the table must not draw it as a match');
  assert.equal(rows.find((r) => r.label === 'Islands').bad, false, 'the engine said Islands matches');
});

test('two values that are not whole and differ are not printed as the same number', () => {
  const outcome = { verdict: 'Fail', expected: '1.004', actual: '1.001', difference: -0.003 };
  const cv = M.checkVisual(outcome, { title: 'Units per order', sourceLabel: 'the answer you trust', trusted: true });
  const [row] = tableRows(cv.data);
  assert.notEqual(row.cells[0], row.cells[1], `model ${row.cells[0]} and trusted answer ${row.cells[1]} read as equal`);
});

test('two percents that differ are not printed as the same percent, and the difference is not "0.0 pts"', () => {
  const outcome = { verdict: 'Fail', expected: '41.83%', actual: '41.81%', difference: -0.0002 };
  const cv = M.checkVisual(outcome, { title: 'Return rate', sourceLabel: 'the answer you trust', trusted: true });
  const [row] = tableRows(cv.data);
  assert.notEqual(row.cells[0], row.cells[1], `model ${row.cells[0]} and trusted answer ${row.cells[1]} read as equal`);
  assert.doesNotMatch(row.cells[2], /^[+-]?0\.0 pts$/, `the difference reads ${row.cells[2]}`);
});

test('the run history does not call a check 100% off when the engine kept only its worst comparisons', () => {
  // The engine keeps the grand total and the 20 worst comparisons, and says how many there were in all. 30 of 500 differ
  // in the newest run and 25 of 500 in the run before: that moved, and neither run is all wrong.
  const run = (id, when, bad) => ({
    runId: id, when, checks: [{
      kind: 'savedCheck', name: 'Net Revenue', verdict: 'Fail', contextsTotal: 500,
      contexts: Array.from({ length: 20 }, () => ({ verdict: 'Fail' })), mismatches: bad,
    }],
  });
  const runs = [run('b', '2026-10-15T10:00:00Z', 30), run('a', '2026-10-14T10:00:00Z', 25)];
  const share = M.howFarOff(runs[0].checks[0]);
  assert.ok(share !== null && share < 0.5, `the newest run is drawn as ${share * 100}% off; 30 of 500 differ`);
  assert.ok(M.movedCount(M.changesVisual(runs)) > 0, '"Nothing moved" is said about a check whose differences went from 25 to 30');
});

test('a relationship with orphan rows is not drawn as 100.0% matching', () => {
  // 5 orphan rows in a 2,000,000-row fact table: the engine says Fail.
  const cv = M.relationshipVisual([{ name: 'Sales to Customer', manyTable: 'Sales', oneTable: 'Customer', manyRowCount: 2000000, refVerdict: 'Fail', refCount: 5 }]);
  const [row] = tableRows(cv.data);
  assert.ok(row.bad || row.cells[0] !== '100.0%', `the failing relationship reads ${row.cells[0]} of rows matching and is not marked`);
});

test('a blank model value reads "blank", not an empty cell', () => {
  const outcome = {
    verdict: 'Fail', rowsTotal: 2, mismatches: 1, matches: 1,
    rows: [{ context: 'Islands', sql: 200, dax: null, delta: null, verdict: 'Fail' }, { context: 'Mainland', sql: 5, dax: 5, delta: 0, verdict: 'Pass' }],
  };
  const rows = tableRows(M.checkVisual(outcome, { title: 'Net Revenue', sourceLabel: 'the source', trusted: false }).data);
  assert.equal(rows.find((r) => r.label === 'Islands').cells[0], 'blank');
});

test('numbers keep the places the old page kept and never read "-0.0"', () => {
  assert.equal(V.fmt(1.004, 'dec'), '1.004');
  assert.equal(V.fmt(12.5, 'dec'), '12.50');
  assert.equal(V.fmt(-0.0000001, 'dec'), '0');
  assert.equal(V.fmtDiff(-0.0000001, 'pctx'), '0 pts');
  assert.equal(V.fmt(0.99999999, 'pctx'), '99.9999%', 'a share under one whole never rounds up to 100%');
  assert.equal(V.fmt(0.4183, 'pctx'), '41.83%');
});

test('a relationship that could not be checked is not drawn as a number in any visual that fits', () => {
  // Re-check of 14adb650: once one relationship fails, the rules pick a dumbbell, and a row with no share was drawn at 0,
  // labelled "-100.0 pts". The harness's own fixture shows it: Returns to Product (Suspect) and Budget to Scenario (could not check).
  const cv = M.relationshipVisual([
    { name: 'a', manyTable: 'Sales', oneTable: 'Customer', manyRowCount: 2000000, refVerdict: 'Fail', refCount: 1847 },
    { name: 'b', manyTable: 'Returns', oneTable: 'Product', refVerdict: 'Suspect' },
    { name: 'c', manyTable: 'Budget', oneTable: 'Scenario', refVerdict: 'NotVerifiable' },
    { name: 'd', manyTable: 'Sales', oneTable: 'Date', refVerdict: 'Pass' },
  ]);
  for (const kind of cv.fits.filter((k) => k !== 'table')) {
    const svg = renderToStaticMarkup(React.createElement('div', null, V.drawVisual(kind, cv.data, 640)));
    const labels = [...svg.matchAll(/<text[^>]*>([^<]*)<\/text>/g)].map((m) => m[1]);
    for (const name of ['Returns to Product', 'Budget to Scenario']) {
      const after = labels[labels.indexOf(name) + 1] ?? '';
      assert.doesNotMatch(after, /\d/, `${kind}: ${name} is drawn as "${after}", but it has no share to show`);
    }
  }
});

test('a run recorded before mismatches were kept claims no share, and Before and after does not invent one', () => {
  const old = (when) => ({ runId: when, when, checks: [{ kind: 'savedCheck', name: 'Net Revenue', verdict: 'Fail', contextsTotal: 500,
    contexts: Array.from({ length: 21 }, () => ({ verdict: 'Fail' })) }] });
  assert.equal(M.howFarOff(old('b').checks[0]), null, 'no count on an old run: no share');
  assert.equal(M.changesVisual([old('b'), old('a')]).data, null);
  assert.equal(M.historyVisual([old('b'), old('a')]).data, null);
});

test('an unknown row with two numbers says could not check, and a gap too small to print says so in words', () => {
  const outcome = { rows: [
    { context: 'East', sql: 100, dax: 100, verdict: 'UnverifiableBlank' },
    { context: 'West', sql: 1.0000004, dax: 1, verdict: 'Mismatch' },
    { context: 'North', sql: 50, dax: 50, verdict: 'Match' },
  ] };
  const cv = M.checkVisual(outcome, { title: 'Net Revenue', sourceLabel: 'the source', trusted: false });
  const rows = tableRows(cv.data);
  assert.match(rows.find((r) => r.label === 'East').cells.join(' '), /could not check/);
  assert.match(rows.find((r) => r.label === 'West').cells.join(' '), /differs by less than 0\.000001/);
  assert.doesNotMatch(rows.find((r) => r.label === 'North').cells.join(' '), /could not check|differs by/);
  const svg = renderToStaticMarkup(React.createElement('div', null, V.drawVisual('dumbbell', cv.data, 640)));
  assert.match(svg, /differs by less than 0\.000001/);
});

test('the total card says "differs by less than" for a gap under the printed step, not a difference of 0', () => {
  // Re-check of 5e85a763: the table and the dumbbell say it in words, but the total card (the rules' pick for every trusted
  // answer) printed "1 · 0 (+0.00%) vs Trusted answer" for a check the engine failed.
  const cv = M.checkVisual({ verdict: 'Fail', expected: '1', actual: '1.0000004' }, { title: 'Units per order', sourceLabel: 'the answer you trust', trusted: true });
  assert.equal(cv.rulesPick, 'total');
  const html = renderToStaticMarkup(React.createElement('div', null, V.drawVisual('total', cv.data, 640)));
  const delta = (html.match(/qv-total-delta[^>]*>(.*?)<\/div>/) ?? [])[1]?.replace(/<[^>]+>/g, '') ?? '';
  assert.match(delta, /differs by less than/, `the total card says "${delta}"`);
});
