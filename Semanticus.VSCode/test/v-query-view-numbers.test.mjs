// Verifier (m16 v-query-view): the numbers a Query visual prints, held against the Result grid. Each case is a defect
// the verifier found by reading query-model.ts and visuals.tsx at 403ec978; each assertion fails on that commit.
//   1. A single number that is BLANK: the rules offer the total card, the card has no total, and drawing it throws.
//   2. A blank in a line is drawn on the zero line: the chart shows 0 where the grid shows nothing.
//   3. A blank in bars prints no value and draws a stub bar: it reads as zero, never as "blank".
//   4. A change from zero is printed as +100.0%: a percent that is not a number shown as one.
//   5. Two numbers with no format string at all are taken as one unit, so a count is drawn against an amount.
//   6. Bars draw a negative as long as a positive of the same size, in the same colour and direction.
//   7. Speed: 59% reading data is said as "Most of this run is calculation (41%)"; the split bar rounds 12.4 ms to "12 ms" beside
//      a headline of "12.4 ms", so 12.4 and 11.6 print the same.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const webview = join(root, 'webview');
const req = createRequire(join(webview, 'package.json'));
const ts = req('typescript');
const { renderToStaticMarkup } = req('react-dom/server');
const cache = new Map();
function load(file) {
  const full = file.endsWith('.ts') || file.endsWith('.tsx') || file.endsWith('.mjs') ? file : existsSync(file + '.tsx') ? file + '.tsx' : existsSync(file + '.ts') ? file + '.ts' : join(file, 'index.ts');
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
    fileName: full.replace(/\.mjs$/, '.js'),
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (name) => (name.endsWith('.css') ? {} : name.startsWith('.') ? load(resolve(dirname(full), name)) : req(name));
  new Function('require', 'module', 'exports', js)(localRequire, module, module.exports);
  cache.set(full, module.exports);
  return module.exports;
}
const M = load(join(webview, 'src', 'query-model.ts'));
const V = load(join(webview, 'src', 'visuals', 'visuals.tsx'));
const text = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&amp;/g, '&').replace(/\s+/g, ' ').trim();
const draw = (kind, d, w = 700) => renderToStaticMarkup(V.drawVisual(kind, d, w));
const f = (name, kind = 'column') => ({ kind, ref: kind === 'measure' ? `[${name}]` : `'T'[${name}]`, name, table: 'T' });
const config = (rows, values) => ({ rows, cols: [], values, filters: [] });
const money = '\\$#,0';
const fits = (res, cfg, dims) => {
  const base = M.queryVisualFits(M.queryShape(res, cfg, dims));
  return base.fits.filter((k) => k === 'table' || M.visualData(k, res, cfg) !== null);
};
const failures = [];
const check = (name, fn) => { try { fn(); } catch (e) { failures.push(`${name}: ${e.message.split('\n')[0]}`); } };

// 1. "Sales in 2031": one number, blank. The page offers the total card (rule.fits keeps it) and draws it.
check('blank total', () => {
  const res = { columns: [{ name: 'Net Sales', format: money }], rows: [[null]], rowCount: 1, truncated: false, elapsedMs: 2 };
  const cfg = config([], [f('Net Sales', 'measure')]);
  const offered = fits(res, cfg, []);
  if (!offered.includes('total')) return;                       // not offered: fine
  const d = M.visualData('total', res, cfg);
  let html = '';
  assert.doesNotThrow(() => { html = draw('total', d); }, 'the total card throws on a blank number the rules offered it');
  assert.match(text(html), /blank/i, 'a blank total says blank');
});

// 2. A month with no sales: the grid cell is empty, the line puts a dot on the zero line.
check('blank in a line', () => {
  const res = { columns: [{ name: 'Month' }, { name: 'Net Sales', format: money }], rows: [['Jan', 500], ['Feb', null], ['Mar', 700]], rowCount: 3, truncated: false, elapsedMs: 2 };
  const cfg = config([f('Month')], [f('Net Sales', 'measure')]);
  const d = M.visualData('line', res, cfg);
  if (d === null) return;                                       // not drawn: fine
  const html = draw('line', d);
  const ys = [...html.matchAll(/<circle cx="[^"]+" cy="([^"]+)"/g)].map((m) => Number(m[1]));
  const zeroY = Number(/<line x1="52" x2="694" y1="([^"]+)"/.exec(html)?.[1]);
  assert.ok(!ys.includes(zeroY), `the blank February is drawn on the zero line (dot at y=${zeroY})`);
});

// 3. Bars with a blank member: no value printed, a stub bar drawn.
check('blank in bars', () => {
  const res = { columns: [{ name: 'Region' }, { name: 'Net Sales', format: money }], rows: [['Coast', 500], ['North', null], ['West', 700]], rowCount: 3, truncated: false, elapsedMs: 2 };
  const d = M.visualData('bars', res, config([f('Region')], [f('Net Sales', 'measure')]));
  if (d === null) return;
  assert.match(text(draw('bars', d)), /North\s+blank/i, 'the blank member reads "blank", not an empty label beside a bar');
});

// 4. Last year was 0, this year 500: the change is not +100%.
check('change from zero', () => {
  const res = { columns: [{ name: 'Net Sales', format: money }, { name: 'Last Year', format: money }], rows: [[500, 0]], rowCount: 1, truncated: false, elapsedMs: 2 };
  const d = M.visualData('total', res, config([], [f('Net Sales', 'measure'), f('Last Year', 'measure')]));
  assert.doesNotMatch(text(draw('total', d, 900)), /\+100\.0%/, 'a change from zero is printed as +100.0%');
  const pair = { columns: [{ name: 'Region' }, { name: 'Net Sales', format: money }, { name: 'Last Year', format: money }],
    rows: [['Coast', 500, 0], ['North', 300, 280], ['West', 200, 210]], rowCount: 3, truncated: false, elapsedMs: 2 };
  const v = M.visualData('variance', pair, config([f('Region')], [f('Net Sales', 'measure'), f('Last Year', 'measure')]));
  assert.doesNotMatch(text(draw('variance', v)), /\+100\.0%/, 'variance bars print a change from zero as +100.0%');
});

// 5. Order Count and Sales Amount, neither with a format string (common in an imported model): drawn against each other.
check('mixed units, no format', () => {
  const res = { columns: [{ name: 'Region' }, { name: 'Sales Amount' }, { name: 'Order Count' }],
    rows: [['Coast', 412000, 310], ['North', 220000, 190], ['West', 198000, 160]], rowCount: 3, truncated: false, elapsedMs: 2 };
  const offered = fits(res, config([f('Region')], [f('Sales Amount', 'measure'), f('Order Count', 'measure')]), [{ name: 'Region', isDate: false, distinct: 3 }]);
  assert.deepEqual(offered, ['table'], `a count and an amount with no format are drawn against each other (${offered.join(', ')})`);
});

// 6. Profit by region, one region at a loss: the -900 bar is the longest bar, drawn like a gain.
check('negative bars', () => {
  const res = { columns: [{ name: 'Region' }, { name: 'Profit', format: money }], rows: [['Coast', 900], ['North', 300], ['West', -900]], rowCount: 3, truncated: false, elapsedMs: 2 };
  const html = draw('bars', M.visualData('bars', res, config([f('Region')], [f('Profit', 'measure')])));
  const rects = [...html.matchAll(/<rect x="([^"]+)" y="[^"]+" width="([^"]+)"[^>]*fill="([^"]+)"/g)].map((m) => m.slice(1).join('|'));
  assert.notEqual(rects[0], rects[2], 'a loss of 900 and a gain of 900 draw the same bar (same start, length and colour)');
});

// 7. The speed sentence and the split bar.
const P = load(join(webview, 'src', 'query-parts.tsx'));
check('speed words', () => {
  const said = P.speedInsight(null, { traceAvailable: true, seQueries: 3, totalMs: 100, seMs: 59 });
  assert.ok(!/Most of this run is calculation \(41%\)/.test(said ?? ''), `59% reading data is said as: "${said}"`);
});
check('speed rounding', () => {
  const a = text(renderToStaticMarkup(P.SplitBar({ label: 'First run', total: 12.4, se: 6, max: 20 })));
  const b = text(renderToStaticMarkup(P.SplitBar({ label: 'First run', total: 11.6, se: 6, max: 20 })));
  assert.ok(/12\.4 ms/.test(a) && a !== b, `the split bar prints 12.4 ms and 11.6 ms the same: "${a}" / "${b}"`);
});

assert.deepEqual(failures, [], '\n  ' + failures.join('\n  '));
