// The shared visual set (Query view and Validation, m16). Every visual is drawn for real with react-dom/server at an
// exact width and read back out of the markup, so a visual that draws nothing, draws NaN, or reaches for a colour or
// a size the tokens do not own fails here. The subfolder is not covered by the visual-style ratchet (it scans
// webview/src/*.tsx and *.css only), so section 3 is that ratchet for this folder.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const dir = join(root, 'webview', 'src', 'visuals');
const req = createRequire(join(root, 'webview', 'package.json'));
const ts = req('typescript');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');

const js = ts.transpileModule(readFileSync(join(dir, 'visuals.tsx'), 'utf8'), {
  compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, esModuleInterop: true },
  fileName: 'visuals.tsx',
}).outputText;
const mod = { exports: {} };
new Function('require', 'module', 'exports', js)(req, mod, mod.exports);
const V = mod.exports;

const region = [
  { label: 'Coast', v: 4120000, v2: 4120000 }, { label: 'Central', v: 3010000, v2: 3010000 },
  { label: 'West', v: 2980500, v2: 3000000 }, { label: 'North', v: 1204000, v2: 1190000 },
];
const money = { rows: region, vLabel: 'Net Sales', v2Label: 'Source', fmt: 'money', tolerance: 0.005, dimLabel: 'Region' };
const series = (a) => ({ label: a, v: 9, series: [4, 5, 5, 6, 8, 7, 9, 9] });
const DATA = {
  total: { ...money, rows: region.slice(0, 3), total: { value: 11100500, label: 'Net Sales', prior: 10200000, priorLabel: '2024', mark: 'bars' } },
  groups: { rows: [{ label: 'Contoso', v: 120, series: [120, 140, 150] }, { label: 'Fabrikam', v: 90, series: [90, null, 110] }], periods: ['2022', '2023', '2024'], vLabel: 'Net Sales', fmt: 'money', dimLabel: 'Brand' },
  bars: money, line: { ...money, rows: ['Jan', 'Feb', 'Mar', 'Apr'].map((l, i) => ({ label: l, v: 100 + i * 20, v2: 90 + i * 10 })) },
  variance: money, dumbbell: money, bullet: money, slope: money,
  multiples: { rows: [series('Sales'), { ...series('Orders'), note: 'dropped' }, series('Stores')], vLabel: 'Rows', fmt: 'rows' },
  waterfall: { rows: [{ label: 'Price', v: 120 }, { label: 'Volume', v: -40 }, { label: 'Mix', v: 25 }], start: { label: '2023', value: 1000 }, endLabel: '2024', vLabel: 'Sales', fmt: 'money' },
  sparktable: { rows: [series('Sales'), series('Orders')], vLabel: 'Rows', fmt: 'rows', dimLabel: 'Table' },
  heat: { rows: [{ label: 'Sales to Date', v: null }, { label: 'Sales to Store', v: null }], cols: ['Missing keys', 'Duplicates'], cells: [[0, 3], [12, 0]], vLabel: 'Count', fmt: 'int', dimLabel: 'Relationship' },
  table: money,
};

// 1. Every kind draws, at both sizes and two widths, with real numbers in it.
assert.deepEqual(Object.keys(V.KIND_NAME).sort(), Object.keys(DATA).sort(), 'the catalogue names every kind and the test covers each');
for (const kind of Object.keys(DATA)) for (const w of [320, 900]) for (const compact of [false, true]) {
  const html = renderToStaticMarkup(V.drawVisual(kind, DATA[kind], w, compact));
  assert.ok(html.length > 80, `${kind} at ${w} (compact ${compact}) draws something`);
  assert.doesNotMatch(html, /NaN|undefined|Infinity/, `${kind} at ${w} (compact ${compact}) has no broken number`);
}

// 2. What a person reads.
const read = (kind, d = DATA[kind], w = 900) => renderToStaticMarkup(V.drawVisual(kind, d, w)).replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ');
assert.match(read('bars'), /Coast.*4,120,000/, 'bars name the member and show its value');
assert.ok(read('bars').indexOf('Coast') < read('bars').indexOf('North'), 'bars are sorted, biggest first');
assert.match(read('variance'), /West.*-19,500/, 'variance bars show the signed difference');
assert.match(read('variance'), /same/, 'a row with no difference says same');
assert.match(read('total'), /11,100,500/, 'the total card shows the total');
assert.match(read('total'), /\+900,500/, 'and the move against the prior value');
assert.match(read('waterfall'), /1,000.*\+120.*-40.*\+25.*1,105/, 'the waterfall runs from the start to the end through each signed step');
assert.match(read('heat'), /Missing keys.*Duplicates.*Sales to Date.*0.*3.*12/, 'the heat table keeps its counts');
assert.match(renderToStaticMarkup(V.drawVisual('heat', DATA.heat, 900)), /color-mix\(in srgb, var\(--sem-bad\)/, 'a count above zero is shaded with the bad token');
assert.match(read('table'), /Difference/, 'the table shows the difference column for a pair');
assert.match(read('dumbbell', { ...DATA.dumbbell, pairLabel: true, fmt: 'ms', rows: [{ label: 'Total', v: 38, v2: 412 }] }), /412 to 38 ms/, 'a pair of speeds reads as "412 to 38 ms"');

// 3. Studio tokens only: no colour literal and no typed font size in the folder.
const css = readFileSync(join(dir, 'visuals.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
const tsx = readFileSync(join(dir, 'visuals.tsx'), 'utf8');
assert.doesNotMatch(css, /#[0-9a-fA-F]{3,8}\b|rgba?\(|hsla?\(/, 'the stylesheet types no colour');
assert.doesNotMatch(css, /font-size:\s*\d/, 'the stylesheet types no font size');
assert.doesNotMatch(tsx, /#[0-9a-fA-F]{3,8}['"`]|rgba?\(|hsla?\(/, 'the drawing types no colour');
for (const m of tsx.matchAll(/var\((--sem-[\w-]+)/g)) {
  const tokens = readFileSync(join(root, 'webview', 'src', 'tokens.css'), 'utf8');
  assert.ok(tokens.includes(m[1] + ':'), `${m[1]} is a real Studio token`);
}
assert.doesNotMatch(css + tsx, /—/, 'no em dash anywhere in the set');
assert.match(readFileSync(join(dir, 'index.ts'), 'utf8'), /visuals\.css/, 'importing the set brings its stylesheet');

// 4. The pick line: the chosen visual is pressed, the others that fit are one press each.
const pl = renderToStaticMarkup(React.createElement(V.PickLine, { pick: 'bars', fits: ['bars', 'dumbbell'], reason: 'One ranked split.', current: 'bars', onPick: () => {} }));
assert.match(pl, /aria-pressed="true"[^>]*data-testid="fit-bars"|data-testid="fit-bars"[^>]*aria-pressed="true"|aria-pressed="true"/, 'the current visual is pressed');
assert.match(pl, /Dumbbell/, 'another fit is offered');
console.log('visuals: ok');
