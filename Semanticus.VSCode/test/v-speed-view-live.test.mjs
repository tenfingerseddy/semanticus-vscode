// v-batch1 verifier (17 Oct): the Speed view on the LIVE Semanticus Demo model, which is a service model, so Measure speed
// skips the cold runs (warm only) and records one profiled run. Two things the fix-speed-view brief asked for do not
// hold there, though both pass on the local cold+warm mock the lane screenshotted:
//   1. A slow query gets the split. Live, a 6.4 s query showed the "Reading data / Calculating" legend and NO split bar:
//      the cold/warm bars need a cold run, and the one-run bar is drawn only when there is no cold/warm result at all.
//   2. Numbers agree with their labels. The headline says "Repeat runs, middle of 5" but shows the AVERAGE: live runs of
//      104, 79, 77, 66, 69 ms were headed "middle of 5: 79 ms"; the middle run is 77 ms.
// Evidence: /tmp/semanticus-joint-review/m16/v-batch1/shots-p/b16-fast-where.png, b17-fast-every-run.png, b37-slow-where.png.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const page = readFileSync(join(root, 'webview', 'src', 'query.tsx'), 'utf8').replace(/\r\n/g, '\n');
const body = page.slice(page.indexOf('function SpeedBody'), page.indexOf('function TimeDetail'));
const failures = [];
const check = (name, fn) => { try { fn(); } catch (e) { failures.push(`${name}: ${e.message.split('\n')[0]}`); } };

// The live shape, numbers as measured on the Demo model: five warm runs, no cold run, one profiled run of 6159 ms on the server.
const st = (runs) => ({ n: runs.length, avgMs: runs.length ? runs.reduce((a, b) => a + b, 0) / runs.length : 0, stdDevMs: 76.2, minMs: Math.min(...runs, 0), maxMs: Math.max(...runs, 0), runsMs: runs });
const cw = { runs: 5, cacheClearAvailable: false, traceAvailable: true, coldTotal: st([]), coldSe: st([]), warmTotal: st([6282, 6340, 6380, 6355, 6514]), warmSe: st([0, 0, 0, 0, 0]), detail: [] };
const t = { totalMs: 6159, wallMs: 6619, seMs: 0, seQueries: 2, seCacheHits: 2, traceAvailable: true, scans: [] };
const fast = false, measured = true;

// Every JSX guard in SpeedBody that draws a SplitBar, and the one that draws the legend, evaluated on the live shape.
const guards = [...body.matchAll(/\{(!fast && [^{}]*?) && <div className="qv-splits">/g)].map((m) => m[1]);
const legend = (body.match(/\{(!fast && measured && [^{}]*?) && <div className="qv-legend">/) || [])[1];
const run = (expr) => new Function('fast', 'measured', 'cw', 't', `return !!(${expr});`)(fast, measured, cw, t);

check('the SplitBar guards were found', () => assert.ok(guards.length >= 2 && legend, `guards ${guards.length}, legend ${!!legend}`));
check('a slow warm-only live query draws a split', () => {
  assert.ok(guards.some(run), 'no SplitBar is drawn for a slow query measured on a service model (warm runs plus one profiled run)');
});
check('the legend is never drawn without a bar to explain', () => {
  if (legend && run(legend)) assert.ok(guards.some(run), 'the Reading data / Calculating legend is drawn with no split bar under it');
});
check('the headline number is what its label says', () => {
  const label = /Repeat runs, middle of/.test(body);
  const isAverage = /warmAvg = cw\?\.warmTotal\?\.n \? cw\.warmTotal\.avgMs/.test(body);
  assert.ok(!(label && isAverage), 'the headline says "middle of N" but shows the average (live: runs 104, 79, 77, 66, 69 ms headed 79 ms; the middle run is 77 ms)');
});

assert.deepEqual(failures, [], '\n  ' + failures.join('\n  '));
console.log('v-speed-view-live: all checks passed');
