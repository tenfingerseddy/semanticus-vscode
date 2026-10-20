// The Query speed view for a FAST query (Kane's hands-on of 1.5.104, 17 Oct): a 75 ms query that took 16 ms on the server and 0 ms in storage
// was headed "Most of this run is calculation (100%), not reading data", drew five bars that did not show the times, a "First run" column of
// "Not run", a "Scan 1, 0 ms, 0 rows" row, and the same "under 1 ms" on every row of a table of runs.
//   1. THE WORDS: a fast query leads with the answer; the reading/calculation split is said only when the query is slow enough to matter,
//      from server time, and never as a percent made of a 0 ms storage reading.
//   2. THE BARS: scaled to the run times from a zero baseline; runs that agree say so in one quiet line instead of a chart.
//   3. THE DETAIL: no first-run column when no first run happened, no scan under 1 ms, every run behind its own button, one labelled
//      "round trip" beside the server time.
// The real clicks and the screenshots are tools/uishot/speed-view-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const src = (f) => readFileSync(join(webview, 'src', f), 'utf8').replace(/\r\n/g, '\n');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const { renderToStaticMarkup } = req('react-dom/server');
const cache = new Map();
function load(file) {
  const full = file.endsWith('.ts') || file.endsWith('.tsx') ? file : existsSync(file + '.tsx') ? file + '.tsx' : file + '.ts';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => (name.endsWith('.css') ? {} : name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const P = load(join(webview, 'src', 'query-parts.tsx'));
const text = (html) => html.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
const failures = [];
const check = (name, fn) => { try { fn(); } catch (e) { failures.push(`${name}: ${e.message.split('\n')[0]}`); } };

const st = (avg, runs) => ({ n: runs.length, avgMs: avg, stdDevMs: 4.3, minMs: Math.min(...runs, avg), maxMs: Math.max(...runs, avg), runsMs: runs });
const none = st(0, []);
// Kane's shot: five repeat runs, no first run, 16 ms on the server, 72 ms round trip, one storage query served from memory.
const kane = {
  cw: { runs: 5, cacheClearAvailable: true, traceAvailable: true, coldTotal: none, coldSe: none, warmTotal: st(75.4, [79, 76, 77, 78, 67]), warmSe: st(0, [0, 0, 0, 0, 0]),
    detail: [79, 76, 77, 78, 67].map((ms, i) => ({ index: i + 1, cold: false, totalMs: ms, seMs: 0, seQueries: 1 })), rowCount: 4 },
  t: { totalMs: 16, wallMs: 72, feMs: 16, seMs: 0, seCpuMs: 0, seQueries: 1, seCacheHits: 1, seParallelism: 0, rowCount: 4, traceAvailable: true, scans: [{ kind: 'VertiPaqScan', durationMs: 0, cpuMs: 0, rows: 0 }] },
};
const slowT = (totalMs, seMs) => ({ totalMs, wallMs: totalMs + 50, feMs: totalMs - seMs, seMs, seCpuMs: seMs, seQueries: 2, seParallelism: 1, rowCount: 10, traceAvailable: true, scans: [] });

// 1. THE WORDS
check('fast leads with the answer', () => {
  assert.equal(P.speedInsight(kane.cw, kane.t), 'Fast: 77 ms. Nothing to fix.', 'the middle repeat run, the same number the figure and the row header say (18 Oct, N12)');
});
check('fast says no split', () => {
  const said = P.speedInsight(kane.cw, kane.t) ?? '';
  assert.ok(!/calculation|reading|%/i.test(said), `a fast query is told its split: "${said}"`);
});
check('fast on the server numbers alone', () => {
  assert.equal(P.speedInsight(null, kane.t), 'Fast: 72 ms. Nothing to fix.', 'the number is what the person waits for, the round trip');
});
check('fast on a quick measure alone', () => {
  assert.equal(P.speedInsight(null, null, { runs: 5, firstMs: 90, warmMinMs: 40, warmMedianMs: 50, runsMs: [90, 50, 40, 45, 50], rowCount: 4 }), 'Fast: 90 ms. Nothing to fix.');
});
check('a slow first run is not fast', () => {
  const said = P.speedInsight({ ...kane.cw, coldTotal: st(412, [436, 388]), coldSe: st(301, [319, 283]), warmTotal: st(60, [60, 60, 60]) }, kane.t) ?? '';
  assert.ok(!/^Fast/.test(said), `a 412 ms first run is called fast: "${said}"`);
});
check('slow with a 0 ms storage reading makes no percent', () => {
  const said = P.speedInsight(null, slowT(800, 0)) ?? '';
  assert.ok(!/%/.test(said) && !/100/.test(said), `a 0 ms storage reading is turned into a share: "${said}"`);
  assert.ok(/calculation/.test(said), `a slow query with no storage time still says where the time went: "${said}"`);
});
check('slow with real reading says it from server time', () => {
  const said = P.speedInsight(null, slowT(800, 650)) ?? '';
  assert.ok(/reading data/.test(said) && /81%/.test(said) && /server/.test(said), `the split is not said from server time: "${said}"`);
});
check('the line between fast and slow is 100 ms', () => {
  assert.equal(P.FAST_MS, 100);
  assert.ok(!/^Fast/.test(P.speedInsight(null, slowT(100, 70)) ?? ''), 'a 150 ms round trip is called fast');
});

// 2. THE BARS
check('bars are scaled to the time from zero', () => {
  const html = renderToStaticMarkup(P.RunsStrip({ runs: [79, 76, 77, 78, 67] }));
  const h = [...html.matchAll(/qv-run-bar[^"]*" style="height:([\d.]+)%/g)].map((m) => Number(m[1]));
  assert.equal(h.length, 5, 'five bars');
  assert.ok(Math.abs(h[4] / h[0] - 67 / 79) < 0.01, `the 67 ms bar is ${h[4]}% beside a 79 ms bar of ${h[0]}%`);
});
check('bars sit on a baseline', () => {
  assert.match(readFileSync(join(webview, 'src', 'query.css'), 'utf8').replace(/\r\n/g, '\n'), /\.qv-run-track \{[^}]*border-bottom:/, 'no baseline under the bars');
});
check('runs that agree are one quiet line, not a chart', () => {
  assert.equal(P.runsAgree([78, 79, 78, 79, 78]), true);
  assert.equal(P.runsAgree([79, 76, 77, 78, 67]), false, 'a 15% spread is a real difference');
  const html = renderToStaticMarkup(P.RunsStrip({ runs: [78, 79, 78, 79, 78] }));
  assert.ok(!/qv-run-bar/.test(html) && /about the same/.test(text(html)), `runs of 78 to 79 ms still draw a chart: "${text(html)}"`);
});

// 3. THE DETAIL
const page = src('query.tsx');
check('scans under 1 ms are not listed', () => {
  const kept = P.visibleScans([{ durationMs: 0, rows: 0 }, { durationMs: 0.4, rows: 3 }, { durationMs: 12, rows: 0 }, { durationMs: 5, rows: 10 }]);
  assert.deepEqual(kept.map((s) => s.durationMs), [12, 5]);
  assert.match(page, /visibleScans\(/, 'the time card still lists every scan');
});
check('no first-run column when there was no first run', () => {
  assert.ok(!/<th className="qv-r">First run<\/th>/.test(page), 'the First run heading is always drawn');
  assert.match(page, /Repeat runs only/, 'no quiet note says only repeat runs were timed');
  assert.ok(!/'Not run'/.test(page.slice(page.indexOf('function TimeDetail'), page.indexOf('// ---------------------------------------------------------------- explain'))), 'a cell still says Not run');
});
check('every run waits behind its own button', () => {
  assert.match(page, /data-testid="fold-every-run"/, 'no Show every run button');
  assert.match(page, /Show every run/);
  assert.match(page, /const \[everyRun, setEveryRun\] = useState\(false\)/, 'the list of runs does not start closed');
});
check('the two times are labelled so they differ on purpose', () => {
  assert.match(page, /on the server/, 'the server time is not labelled');
  assert.match(page, /round trip/, 'the round trip is not labelled');
  assert.match(page, /one profiled run/i, 'nothing says why 72 ms is not the 75 ms average');
});
check('a fast query hides the split bars and the legend', () => {
  assert.match(page, /!fast && /, 'the split and its legend are drawn for a fast query');
});

assert.deepEqual(failures, [], '\n  ' + failures.join('\n  '));
console.log('speed-view: all checks passed');
