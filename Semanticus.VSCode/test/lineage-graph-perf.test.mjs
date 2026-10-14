// Lineage > Graph on a big model (Kane, 12 Oct: "crash and freeze", "buggy and different"). The picture is laid out by our
// own seeded, sliced layout (webview/src/lineagelayout.mjs), not by ECharts' random-start single-pass force layout, and
// the Filter menu opens inside the view.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createLayout, runLayoutSliced } from '../webview/src/lineagelayout.mjs';
import { makeMonitoringLineage } from '../tools/uishot/fixtures/lineage-monitoring-fixture.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const graph = readFileSync(resolve(root, 'webview/src/lineagegraph.tsx'), 'utf8');
const slicer = readFileSync(resolve(root, 'webview/src/lineageslicer.tsx'), 'utf8');

// ---- the graph view uses the sliced layout, and tells the person it is drawing ---------------------
assert.match(graph, /from '\.\/lineagelayout\.mjs'/, 'the graph must lay itself out with lineagelayout.mjs');
assert.doesNotMatch(graph, /layoutAnimation/, 'ECharts force layout (random start, one blocking pass) must not come back');
assert.match(graph, /layout: layout === 'force' \? 'none' : layout/, 'force is drawn from our positions, so ECharts runs no layout of its own');
assert.match(graph, /Drawing/, 'a visible Drawing state while the layout runs');
assert.match(graph, /linkWork\.hold\('lineage-graph'/, 'the Link works while the picture is drawn');
assert.doesNotMatch(graph, /length <= 150\) return;\s*autoFocused/, 'large models must pick their first view during render, not after a whole-model draw');

// ---- the same model draws the same way, however the slices fall -----------------------------------
const fx = makeMonitoringLineage();
const nodes = fx.lineage.nodes.map((n) => ({ id: n.ref, group: n.table ?? n.name }));
const links = fx.lineage.edges.filter((e) => e.kind !== 'contains' || !e.to.startsWith('measure:')).map((e) => ({ source: e.from, target: e.to }));
assert.ok(nodes.length >= 800 && links.length >= 900, 'the fixture is a big monitoring model');
const finish = (budget) => { const l = createLayout({ nodes, links }); while (!l.step(budget)) { /* slices */ } return Array.from(l.positions, (v) => Math.round(v * 1000)); };
assert.deepEqual(finish(0), finish(1000), 'one step per slice and one slice for all give the same picture');
const a = finish(8), b = finish(8);
assert.deepEqual(a, b, 'two draws of the same model are identical');
assert.ok(a.every(Number.isFinite), 'every position is a number');

// ---- no slice holds the thread for long, and the whole draw is cheap ------------------------------
{
  // A wall clock on a shared CI runner has stalls (a paused process, a garbage collection) that no code can avoid: a Windows runner once measured 50 ms in one slice
  // of a draw whose slices run 8 to 17 ms here. A real regression (a heavy step, a layout in one block) makes EVERY draw slow, so the draw is timed up to three
  // times and the quietest run is the one judged. A stall hits one run; a regression hits all of them.
  const draw = () => {
    const l = createLayout({ nodes, links });
    let worst = 0, slices = 0; const t0 = performance.now();
    while (true) { const s = performance.now(); const done = l.step(8); worst = Math.max(worst, performance.now() - s); slices++; if (done) break; }
    return { worst, slices, total: performance.now() - t0 };
  };
  const badness = (r) => Math.max(r.worst / 40, r.total / 4000);
  let best = draw();
  for (let n = 1; n < 3 && badness(best) >= 1; n++) { const r = draw(); if (badness(r) < badness(best)) best = r; }
  assert.ok(best.worst < 40, `the longest slice was ${best.worst.toFixed(1)} ms in the quietest of three draws; it must stay under 40`);
  assert.ok(best.total < 4000, `the whole draw took ${best.total.toFixed(0)} ms`);
  assert.ok(best.slices > 1, 'the draw is cut into slices');
}

// ---- the picture is not a pile: nodes spread out and linked ones sit closer than strangers ---------
{
  const l = createLayout({ nodes, links }); while (!l.step(1000)) { /* run */ }
  const idx = new Map(nodes.map((n, i) => [n.id, i]));
  const d = (i, j) => Math.hypot(l.positions[2 * i] - l.positions[2 * j], l.positions[2 * i + 1] - l.positions[2 * j + 1]);
  const linked = links.slice(0, 300).map((e) => d(idx.get(e.source), idx.get(e.target)));
  const strangers = []; for (let i = 0; i < 300; i++) strangers.push(d((i * 37) % nodes.length, (i * 91 + 5) % nodes.length));
  const mean = (x) => x.reduce((s, v) => s + v, 0) / x.length;
  assert.ok(mean(linked) < mean(strangers) * 0.7, `linked ${mean(linked).toFixed(0)} vs strangers ${mean(strangers).toFixed(0)}`);
  const stacked = Array.from({ length: 300 }, (_, i) => d(i, i + 1)).filter((v) => v < 1).length;
  assert.ok(stacked < 5, 'no two neighbours in the list sit on the same point');
}

// ---- runLayoutSliced gives the thread back, paints the seed at once and ends with done=true -------
{
  const l = createLayout({ nodes: nodes.slice(0, 200), links: [] });
  const frames = []; let yields = 0;
  await new Promise((done) => runLayoutSliced(l, (p, fin) => { frames.push(fin); if (fin) done(); }, { sliceMs: 0, yieldFn: (fn) => { yields++; setTimeout(fn, 0); } }));
  assert.equal(frames[0], false, 'the first frame is the seeded picture');
  assert.equal(frames[frames.length - 1], true, 'the last frame says done');
  assert.ok(yields > 5, 'the layout yielded between slices');
}

// ---- the Filter menu holds its lists inside itself (Kane, 12 Oct 20:42) ---------------------------
assert.match(slicer, /inline\?: boolean/, 'MultiSelect has an in-flow mode for use inside a menu');
assert.match(graph, /<MultiSelect[^>]*inline/, 'the graph Filter menu uses the in-flow lists');
console.log('lineage-graph-perf: ok');
