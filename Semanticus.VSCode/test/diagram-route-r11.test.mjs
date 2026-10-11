// Review round 11 of m11/diagram-route (30 Sep), R10-1. While cards are open the flow store holds the spots the spread DREW, so the
// saved spots must be held somewhere the spread cannot reach. Round 10 kept them inside the spread's own state; a guard in its
// `draw` cleared that state when a late result found no card open, and the rebuild that followed took the drawn spots for the
// saved layout (the drive, tools/uishot/diagram-route-r11-race-drive.mjs, proves the behaviour live at 80 and 300 tables).
// Here: the holder's contract (diagramfit.mjs savedSpots), and a scan of diagram.tsx that keeps the structure: the spread's state
// carries no saved spots, `draw` releases nothing, and only endSpread empties the holder.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { savedSpots } from '../webview/src/diagramfit.mjs';

let passed = 0, failed = 0;
const ok = (name, fn) => {
  try { fn(); passed++; console.log('  [PASS] ' + name); }
  catch (e) { failed++; console.log('  [FAIL] ' + name + '\n    ' + String(e.message).split('\n').join('\n    ')); }
};

ok('nothing is held until a spread holds the saved spots', () => {
  assert.equal(savedSpots().peek(), null);
  assert.equal(savedSpots().release(), null);
});
ok('the first saved layout held is kept; a further spread over the same cards does not replace it', () => {
  const h = savedSpots();
  h.hold({ A: { x: 1, y: 2 }, B: { x: 3, y: 4 } });
  h.hold({ A: { x: 100, y: 200 }, B: { x: 300, y: 400 } });   // the DRAWN spots of a later picture
  assert.deepEqual(h.peek(), { A: { x: 1, y: 2 }, B: { x: 3, y: 4 } });
});
ok('a drag while spread amends only the cards it moved', () => {
  const h = savedSpots();
  h.hold({ A: { x: 1, y: 2 }, B: { x: 3, y: 4 } });
  h.amend({ B: { x: 30, y: 40 } });
  assert.deepEqual(h.peek(), { A: { x: 1, y: 2 }, B: { x: 30, y: 40 } });
});
ok('an amend with nothing held holds nothing (no spread, nothing to amend)', () => {
  const h = savedSpots();
  h.amend({ A: { x: 1, y: 1 } });
  assert.equal(h.peek(), null);
});
ok('release hands back every saved spot once and empties the holder', () => {
  const h = savedSpots();
  h.hold({ A: { x: 1, y: 2 } });
  assert.deepEqual(h.release(), { A: { x: 1, y: 2 } });
  assert.equal(h.peek(), null);
  assert.equal(h.release(), null);
});
ok('the holder keeps its own copy: changing the map it was given does not change the saved spots', () => {
  const h = savedSpots(), given = { A: { x: 1, y: 2 } };
  h.hold(given);
  given.A = { x: 9, y: 9 };
  assert.deepEqual(h.peek(), { A: { x: 1, y: 2 } });
});

// ---- the structure in diagram.tsx (the component itself is not loadable in node) ----
const src = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', 'webview', 'src', 'diagram.tsx'), 'utf8');
const between = (from, to) => { const i = src.indexOf(from); assert.ok(i >= 0, `${from} not found`); const j = src.indexOf(to, i + from.length); assert.ok(j > i, `${to} not found after ${from}`); return src.slice(i, j); };

ok('the spread state carries no saved spots (they are held apart)', () => {
  const t = between('type Spread = {', '};');
  assert.ok(!/saved/.test(t), t);
});
ok('the spread\'s draw never releases or clears anything', () => {
  const draw = between('const draw = (to', 'const { needed, grown }');
  assert.ok(!/release\(|endSpread\(|spread\.current\s*=\s*null|held\.amend/.test(draw), draw);
});
ok('only endSpread empties the holder, and nothing else sets the spread to null', () => {
  assert.equal((src.match(/held\.release\(/g) || []).length, 1);
  assert.equal((src.match(/spread\.current\s*=\s*null/g) || []).length, 1);   // inside endSpread
  assert.ok(/const endSpread = \(\) => \{ spread\.current = null; return held\.release\(\); \};/.test(src));
});
ok('nothing reads a saved spot from the spread any more', () => {
  assert.ok(!/\bsp\.saved\b|\.saved\[|spread\.current\??\.saved/.test(src));
});

console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
