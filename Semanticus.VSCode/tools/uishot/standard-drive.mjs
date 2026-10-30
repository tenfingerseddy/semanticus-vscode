// Browser-driven proof of Review > Your standard (Kane picked option B, 18 Oct 2026). The real built Studio on a mocked engine: the engine's four tools (getStandard, previewStandard,
// setStandard, applyStandard) answer from a typed table below, and undo is recorded. It checks the page against the design lens: the answer fits without scrolling at 1090x800, every part
// sits on the 12-column grid (standard-align.js), nothing spills sideways, no console error, and every control works and changes what the engine was asked.
// The engine is mocked here, in its real wire shapes: the counts are typed in, not computed. The real run (a clean home, Northstar, the real engine) is a separate check.
//
//   OUTDIR=<dir> TMPDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium [THEMES=dark,light] [WIDTHS=1440,1090] node tools/uishot/standard-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'standard');
mkdirSync(OUT, { recursive: true });
const THEMES = (process.env.THEMES || 'dark').split(',');
const WIDTHS = (process.env.WIDTHS || '1440,1090').split(',').map(Number);
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) { console.error('Build the webview first.'); process.exit(1); }
const ALIGN = readFileSync(join(here, 'standard-align.js'), 'utf8').replace(/^\/\/.*$/mg, '').trim();
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'],
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// ---- the mocked engine: what the four tools answer, in the engine's own wire shapes (camelCase; typed in, the counts are not computed) ----------
const seed = (state) => `
window.__calls = [];
const SPEC = (name, level, names, formats, folders, descriptions, synonyms, keys) => ({ standard: name, startsFrom: name.toLowerCase(), checks: { level, off: [], must: [] }, names, formats, folders, descriptions, synonyms, keysAndTotals: keys, aiInstructions: 'none' });
const PRESETS = [
  SPEC('Light', 'light', 'leave', {}, 'none', 'none', 'none', 'hide keys, no-sum ids'),
  SPEC('Balanced', 'balanced', 'leave', {}, 'by-subject, over 10', 'measures, tables', '2, measures and tables', 'hide keys, no-sum ids'),
  SPEC('Strict', 'strict', 'title-case', { percent: '0.0%', count: '#,0' }, 'by-subject, always', 'all-visible', '2, all-visible', 'hide keys and technical, no-sum ids'),
];
const KEY = { checks: 'checks', names: 'names', formats: 'formats', folders: 'folders', descriptions: 'descriptions', synonyms: 'synonyms', keys: 'keys-and-totals', instructions: 'ai-instructions' };
const valueOf = (spec, part) => part === 'checks' ? spec.checks.level : part === 'formats' ? Object.entries(spec.formats || {}).filter(([, v]) => v !== 'leave').map(([k, v]) => k + ' ' + v).join(', ') || 'leave' : part === 'keys' ? spec.keysAndTotals : part === 'instructions' ? spec.aiInstructions : spec[part];
// what a choice does on this model: [changes now, tasks, examples as the engine words them]
const EFFECT = {
  'names|title-case': [0, 5, ['Measure sales_amt: sales_amt to Sales Amount', 'Measure NPAT ACT YTD: NPAT ACT YTD to Net Profit After Tax YTD', 'Column cust_seg: cust_seg to Customer Segment']],
  'names|title-case-no-prefix': [0, 7, ['Measure sales_amt: sales_amt to Sales Amount', 'Table DimPromotion: DimPromotion to Promotion', 'Table FactBudget: FactBudget to Budget']],
  'formats|percent 0.0%, count #,0': [4, 0, ['Margin %: no format to 0.0%', 'Order Count: 0 to #,0']],
  'formats|money $#,0.00, percent 0.0%, count #,0': [7, 0, ['Total Sales: no format to $#,0.00', 'Margin %: no format to 0.0%', 'Order Count: 0 to #,0']],
  'folders|by-subject, over 10': [12, 0, ['Total Sales: Sales to Sales / Revenue', 'Margin %: Sales to Sales / Margin']],
  'folders|by-subject, always': [15, 0, ['Total Sales: Sales to Sales / Revenue', 'Budget Amount: Budget to Budget / Plan', 'Order Count: Sales to Sales / Orders']],
  'descriptions|measures': [0, 7, ['Total Sales: no description']],
  'descriptions|measures, tables': [0, 11, ['Total Sales: no description', 'Promotion: no description']],
  'descriptions|all-visible': [0, 52, ['Total Sales: no description', 'Sales[Store Name]: no description']],
  'synonyms|2, measures and tables': [0, 8, ['Total Sales: needs 2 other names', 'Customer: needs 2 other names']],
  'synonyms|2, all-visible': [0, 63, ['Total Sales: needs 2 other names', 'Store: needs 2 other names']],
  'keys|hide keys, no-sum ids': [15, 0, ['Sales[CustomerKey]: shown to hidden', 'Date[Year]: Sum to Do not add up']],
  'keys|hide keys and technical, no-sum ids': [19, 0, ['Sales[CustomerKey]: shown to hidden', 'Product[etl_batch_id]: shown to hidden']],
};
const NOTE = { 'checks|light': '61 of 150 open checks reach the Plan, 89 are left off by this standard.', 'checks|balanced': '150 of 150 open checks reach the Plan.', 'checks|strict': '150 of 150 open checks reach the Plan.' };
const st = { spec: JSON.parse(JSON.stringify(PRESETS[1])), source: ${JSON.stringify(state === 'builtin' ? 'built-in' : 'model-file')}, use: null, applied: false, userExists: false };
if (${JSON.stringify(state)} === 'changed' || ${JSON.stringify(state)} === 'changed2') st.spec.formats = { money: '$#,0.00', percent: '0.0%', count: '#,0' };
if (${JSON.stringify(state)} === 'changed2') { st.spec.folders = 'by-subject, always'; st.spec.synonyms = 'none'; }
const FILE = '/home/me/Clients/Contoso/Contoso.SemanticModel/.semanticus/standard.yaml';
const USER = '/home/me/.semanticus/standard.yaml';
const apply = (spec, patch) => {
  const s = JSON.parse(JSON.stringify(spec)); const p = patch || {};
  for (const [k, v] of Object.entries(p)) {
    if (k === 'preset') { if (v) { const base = PRESETS.find((x) => x.startsFrom === v); s.startsFrom = v; s.__base = base; } continue; }
    if (k === 'use') { s.use = v; continue; }
    if (k === 'checks') { if (v === null) { s.checks = { level: 'balanced', off: [], must: [] }; } else Object.assign(s.checks, v); continue; }
    if (k === 'formats') { s.formats = v === null ? {} : { ...v }; continue; }
    const field = k === 'keys-and-totals' ? 'keysAndTotals' : k === 'ai-instructions' ? 'aiInstructions' : k;
    s[field] = v === null ? (s.__base || PRESETS.find((x) => x.startsFrom === s.startsFrom) || PRESETS[1])[field] : v;
  }
  delete s.__base; return s;
};
const yamlOf = () => ['# Contoso standard. Every model that uses this file is held to it.', 'standard: Contoso', 'starts-from: ' + st.spec.startsFrom, '', 'checks:', '  level: ' + st.spec.checks.level, 'names: ' + st.spec.names, 'folders: "' + st.spec.folders + '"', 'descriptions: "' + st.spec.descriptions + '"', 'synonyms: "' + st.spec.synonyms + '"', 'keys-and-totals: "' + st.spec.keysAndTotals + '"', 'ai-instructions: ' + st.spec.aiInstructions].join('\\n');
const stateOf = (spec) => ({ name: st.source === 'built-in' ? 'Balanced' : 'Contoso', source: st.source, path: st.source === 'built-in' ? null : FILE, writePath: FILE, userPath: USER, userExists: st.userExists, use: st.use, hash: 'abcd1234', effective: spec, written: st.source === 'built-in' ? null : spec, fileText: st.source === 'built-in' ? null : yamlOf(), notes: [] });
function previewOf(spec) {
  const parts = []; let now = 0, tasks = 0;
  for (const part of Object.keys(KEY)) {
    const v = valueOf(spec, part); const e = EFFECT[part + '|' + v] || [0, 0, []];
    if (!st.applied) { now += e[0]; tasks += e[1]; }
    parts.push({ part: KEY[part], setting: v, changesNow: st.applied ? 0 : e[0], tasks: e[1], examples: st.applied ? [] : e[2], note: NOTE[part + '|' + v] || null });
  }
  return { standard: stateOf(spec), parts, changesNow: now, tasks, gradeNow: 'B', gradeAfter: 'B', scoreNow: 82, scoreAfter: Math.min(100, 82 + (now > 20 ? 4 : 2)), sentence: 'x' };
}
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => __h, set: (v) => {
  __h = v;
  v.setResponse('getStandard', () => { window.__calls.push(['getStandard']); ${state === 'old' ? "throw new Error('Method not found: getStandard');" : ''} return { standard: stateOf(st.spec), presets: PRESETS }; });
  v.setResponse('previewStandard', (params) => { window.__calls.push(['previewStandard', params[0] ?? null]); return previewOf(apply(st.spec, params[0] ? JSON.parse(params[0]) : null)); });
  v.setResponse('setStandard', (params) => {
    window.__calls.push(['setStandard', params[0], params[1] ?? null]);
    if (params[1] === 'your-default') { st.userExists = true; return { changed: ['x'], standard: stateOf(st.spec) }; }
    const p = JSON.parse(params[0]); st.spec = apply(st.spec, p); st.source = 'model-file'; st.applied = false;
    if ('use' in p) st.use = p.use;
    return { changed: ['x'], standard: stateOf(st.spec) };
  });
  v.setResponse('applyStandard', () => {
    window.__calls.push(['applyStandard']); const p = previewOf(st.spec); st.applied = true;
    return { revision: 5, applied: [], parts: p.parts, changesNow: p.changesNow, tasks: p.tasks, planRows: Array.from({ length: p.parts.filter((x) => x.tasks > 0).length }, (_, i) => 'plan-' + i), gradeBefore: 'B', gradeAfter: 'B', sentence: 'x' };
  });
  v.setResponse('undo', (params) => { window.__calls.push(['undo', params[0], params[1] ?? null]); st.applied = false; return { canUndo: false }; });
} });
`;
// ---- the drive ----------------------------------------------------------------------------------------------------------------------------
let failed = 0;
const check = (ok, what) => { if (!ok) { failed += 1; console.error('  FAIL ' + what); } else console.log('  ok   ' + what); };
const count = (a) => a ? Object.fromEntries(['offGrid', 'ragged', 'titleLines', 'gaps', 'inner'].map((k) => [k, a[k].length])) : null;
const sideways = (p) => p.evaluate(() => {
  const pg = document.querySelector('.st-page'); let e = pg, bad = document.documentElement.scrollWidth > innerWidth + 1;
  while (e && e !== document.body) { if (e.scrollWidth > e.clientWidth + 1 && /(auto|scroll)/.test(getComputedStyle(e).overflowX)) bad = true; e = e.parentElement; }
  for (const c of document.querySelectorAll('.st-page .sem-card')) { if (c.scrollWidth > c.clientWidth + 1) bad = true; }
  return bad;
});
const pageScroll = (p) => p.evaluate(() => { let e = document.querySelector('.st-page'); while (e && e !== document.body) { const s = getComputedStyle(e); if (/(auto|scroll)/.test(s.overflowY) && e.scrollHeight > e.clientHeight + 1) return e.scrollHeight - e.clientHeight; e = e.parentElement; } return 0; });
const fullHeight = (p) => p.evaluate(() => { let e = document.querySelector('.st-page'); while (e && e !== document.body) { const s = getComputedStyle(e); if (/(auto|scroll)/.test(s.overflowY) && e.scrollHeight > e.clientHeight) return e.scrollHeight + 120; e = e.parentElement; } return innerHeight; });
const calls = (page, name) => page.evaluate((n) => window.__calls.filter((c) => c[0] === n), name);
const results = { runs: [] };
for (const theme of THEMES) for (const width of WIDTHS) for (const state of ['balanced', 'changed', 'changed2', 'builtin', 'old']) {
  console.log(`== ${theme} ${width} ${state}`);
  const ctx = await browser.createBrowserContext();
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', (e) => { failed += 1; errors.push(String(e)); console.error('pageerror: ' + e.message); });
  page.on('console', (m) => { if (m.type() === 'error' && state !== 'old') errors.push(m.text()); });
  await page.setViewport({ width, height: width === 1090 ? 800 : 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(seed(state));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=standard`, { waitUntil: 'networkidle0', timeout: 30000 });
  await applyTheme(page, theme);
  await wait(900);
  const dir = join(OUT, `${theme}-${width}-${state}`); mkdirSync(dir, { recursive: true });
  let n = 0; const run = { theme, width, state, align: [], sideways: [], missing: [], shots: [] };
  const measure = async (step) => { const a = await page.evaluate(`(${ALIGN})()`); const c = count(a); run.align.push({ step, ...c }); if (c && Object.values(c).some((v) => v)) run.align[run.align.length - 1].detail = a; if (await sideways(page)) run.sideways.push(step); };
  const shot = async (name, full = false) => { n += 1; const f = join(dir, `${String(n).padStart(2, '0')}-${name}.png`); if (full) { await page.setViewport({ width, height: Math.min(9000, await fullHeight(page)), deviceScaleFactor: 1 }); await wait(400); } await page.screenshot({ path: f }); if (full) await page.setViewport({ width, height: width === 1090 ? 800 : 900, deviceScaleFactor: 1 }); run.shots.push(f); };
  const text = () => page.evaluate(() => document.querySelector('.st-page')?.innerText ?? '');
  const press = async (id) => { const el = await page.$(`[data-testid="${id}"]`); if (!el) { run.missing.push(id); return false; } await el.evaluate((e) => e.scrollIntoView({ block: 'center' })); await el.click(); await wait(300); return true; };

  check(!!(await page.$('.st-page')), 'the Your standard page is on screen');
  if (state === 'old') {
    const body = await text();
    check(/does not know about standards yet/.test(body) && !!(await page.$('[data-testid=st-retry]')), 'an engine without the tools gets one plain line and Try again, not raw text');
    check(!/Method not found/.test(body), 'the raw engine text is never shown');
    await shot('old', true);
    await ctx.close(); results.runs.push(run); continue;
  }
  await measure('load'); await shot('load');
  // The first view fits without scrolling at 1090x800.
  if (width === 1090 && state === 'balanced') check((await pageScroll(page)) === 0, 'the first view needs no scrolling at 1090x800 (' + (await pageScroll(page)) + ' px over)');
  const body = await text();
  check(/Your standard/.test(body) && /Light/.test(body) && /Balanced/.test(body) && /Strict/.test(body) && /Change one thing/.test(body) && /Apply to this model/.test(body), 'the page has its parts: presets, your changes, change one thing, apply');
  check(!body.includes('—') && !/Q&A|\bMap\b/.test(body), 'no em dash, no "Q&A", no "Map" in the words on the page');
  check(await page.$eval('[data-testid=st-preset-balanced]', (e) => e.getAttribute('aria-checked')) === 'true', 'Balanced is the chosen preset');
  // each preset carries its counts on this model, read from the engine
  const countsOf = await page.evaluate(() => ['light', 'balanced', 'strict'].map((p) => document.querySelector('[data-testid="st-preset-n-' + p + '"]')?.textContent));
  check(countsOf.every((t) => /\d/.test(t || '')), 'every preset shows its counts: ' + countsOf.join(' | '));
  check((await calls(page, 'previewStandard')).length >= 4, 'the counts came from previewStandard (current standard and three presets)');
  if (state === 'balanced') check(/None\. Balanced as it comes\./.test(body), 'with no changes the changes card says so');
  if (state === 'builtin') check(/Balanced, built in/.test(await page.$eval('[data-testid=st-where]', (e) => e.textContent)), 'the built in standard says where it comes from');

  // compare presets: press a count, see the changes before and after
  check(await press('st-preset-n-strict'), 'the Strict count can be pressed');
  const ex = await page.$('[data-testid=st-preset-examples]');
  check(!!ex && /Strict on this model/.test(await ex.evaluate((e) => e.textContent)) && / to /.test(await ex.evaluate((e) => e.textContent)), 'a preset count opens the changes before and after');
  await measure('preset-examples'); await shot('preset-examples');
  await press('st-preset-n-strict');
  check(!(await page.$('[data-testid=st-preset-examples]')), 'pressing the count again closes it');

  // change one thing
  check(await press('st-change-one'), 'Change one thing opens');
  check(await press('st-c1-formats'), 'a part can be chosen');
  await measure('change-one-choices'); await shot('change-one');
  check(await press('st-c1-formats-money $#,0.00, percent 0.0%, count #,0'), 'a choice can be made');
  await wait(300);
  const setCalls = await calls(page, 'setStandard');
  const sentPatch = setCalls.length ? JSON.parse(setCalls[setCalls.length - 1][1]) : {};
  check(sentPatch.formats?.money === '$#,0.00' && Object.keys(sentPatch).length === 1, 'Change one thing sends a patch of that one part to setStandard (a JSON string)');
  check(!!(await page.$('[data-testid=st-over-formats]')), 'the change is now a line under the presets');
  check(/was Leave/.test(await page.$eval('[data-testid=st-over-formats]', (e) => e.textContent)), 'the line says what the preset had');
  // a change line's count opens its examples
  check(await press('st-n-formats'), 'the change line count can be pressed');
  check(!!(await page.$('[data-testid=st-examples]')) && /Total Sales/.test(await page.$eval('[data-testid=st-examples]', (e) => e.textContent)), 'a count press opens the examples for that part');
  await measure('change-line-examples'); await shot('change-line-examples');
  await press('st-n-formats');
  // moving to another preset keeps the line
  await press('st-preset-light'); await wait(300);
  const lastSet = (await calls(page, 'setStandard')).pop();
  const toLight = JSON.parse(lastSet[1]);
  check(toLight.preset === 'light' && toLight.formats?.money === '$#,0.00' && toLight.names === null, 'moving to Light keeps the person\'s own money format and resets the parts that were not changed');
  await press('st-preset-balanced'); await wait(300);
  check(await press('st-reset-formats'), 'Put back is there for a changed line'); await wait(300);
  check(!(await page.$('[data-testid=st-over-formats]')), 'Put back removes the line');

  // File: the standard as its file, Copy as a page
  check(await press('st-file'), 'File opens');
  check(!!(await page.$('[data-testid=st-fileview] pre')) && /standard: Contoso|# Contoso standard/.test(await page.$eval('[data-testid=st-fileview] pre', (e) => e.textContent)), 'File shows the standard as its file');
  check(!!(await page.$('[data-testid=st-copy-page]')) && !!(await page.$('[data-testid=st-copy-file]')), 'File offers Copy and Copy as a page');
  if (state !== 'builtin') check(!!(await page.$('[data-testid=st-open-file]')) && !!(await page.$('[data-testid=st-comments]')), 'File offers Open in the editor and says the comments are dropped when the page changes the file');
  await measure('file'); await shot('file');
  await press('st-copy-page');
  await press('st-file');

  // picker
  check(await press('st-picker'), 'the standard picker opens');
  await page.waitForSelector('[data-testid=st-use-default]', { timeout: 4000 }).catch(() => undefined);
  check(!!(await page.$('[data-testid=st-use-default]')) && !!(await page.$('[data-testid=st-shared-path]')), 'the picker offers Your default and a shared file');
  await measure('picker'); await shot('picker');
  check(/Save this as your default/.test(await page.$eval('[data-testid=st-use-default]', (e) => e.textContent)), 'with no default file yet, the picker offers to save this standard as your default');
  await page.keyboard.press('Escape'); await wait(200);
  // Your default: the default file is saved first, then this model points at it; the file path comes from the engine
  await press('st-picker'); await page.waitForSelector('[data-testid=st-use-default]', { timeout: 4000 }).catch(() => undefined); check(await press('st-use-default'), 'Your default can be chosen'); await wait(500);
  const toDefault = await calls(page, 'setStandard');
  check(toDefault.some((c) => c[2] === 'your-default') && JSON.parse(toDefault[toDefault.length - 1][1]).use === '/home/me/.semanticus/standard.yaml', 'Your default saves the default file, then points this model at it');
  check(/Your default, on this computer/.test(await page.$eval('[data-testid=st-where]', (e) => e.textContent)), 'the page says the standard now comes from Your default');
  // A shared file: the model\'s own lines are cleared so the shared file fills every part
  await press('st-picker'); await page.waitForSelector('[data-testid=st-shared-path]', { timeout: 4000 }).catch(() => undefined);
  await page.type('[data-testid=st-shared-path]', 'https://example.com/team.standard.yaml'); await press('st-shared-use'); await wait(500);
  const toShared = JSON.parse((await calls(page, 'setStandard')).pop()[1]);
  check(toShared.use === 'https://example.com/team.standard.yaml' && toShared.names === null && toShared.formats === null, 'a shared file is set with use, and the model\'s own lines are cleared');
  check(/example\.com/.test(await page.$eval('[data-testid=st-where]', (e) => e.textContent)), 'the page names the shared file');
  await press('st-picker'); await page.waitForSelector('[data-testid=st-use-model]', { timeout: 4000 }).catch(() => undefined); await press('st-use-model'); await wait(500);
  check(JSON.parse((await calls(page, 'setStandard')).pop()[1]).use === null, 'This model\'s own file sends use null');

  // Apply: one press, no confirm, one call, then what was done and the Plan rows
  const before = (await calls(page, 'applyStandard')).length;
  check(await press('st-apply'), 'Apply to this model can be pressed'); await wait(400);
  check((await calls(page, 'applyStandard')).length === before + 1, 'one press makes exactly one applyStandard call, with no confirm in between');
  const ap = await text();
  check(!!(await page.$('[data-testid=st-applied]')) && /changes made/.test(ap) && /Undo puts (them all|it) back/.test(ap), 'after Apply the card says what was done and that Undo puts it back');
  check(!!(await page.$('[data-testid=st-tasks]')) && /Your assistant/.test(ap), 'the renames and words are on the Plan as tasks, with who does them');
  await measure('applied'); await shot('applied');
  check(await press('st-undo'), 'Undo can be pressed'); await wait(400);
  const u = (await calls(page, 'undo')).pop();
  check(!!u && u[1] === 'human', 'Undo goes through the shared undo timeline');
  check(!!(await page.$('[data-testid=st-apply]')) && !(await page.$('[data-testid=st-applied]')), 'after Undo the page is back to what Apply would do');
  await press('st-apply'); await wait(300);
  check(await press('st-open-plan'), 'Open the Plan can be pressed'); await wait(600);
  check(/Plan/.test(await page.evaluate(() => document.body.innerText.slice(0, 2000))) && !(await page.$('.st-page')), 'Open the Plan leaves for the Plan');

  await measure('end');
  check(errors.length === 0, `no page or console error (${errors.length})`);
  check(run.missing.length === 0, 'no control went missing (' + run.missing.join(', ') + ')');
  check(run.sideways.length === 0, 'nothing spills sideways (' + run.sideways.join(', ') + ')');
  const bad = run.align.filter((a) => Object.entries(a).some(([k, v]) => k !== 'step' && k !== 'detail' && v));
  check(bad.length === 0, `every step sits on the grid (bad steps ${bad.length} of ${run.align.length})`);
  if (bad.length) console.error('  first bad: ' + JSON.stringify(bad[0]).slice(0, 700));
  run.errors = errors; results.runs.push(run);
  await ctx.close();
}
writeFileSync(join(OUT, 'drive.json'), JSON.stringify(results, null, 1));
await browser.close(); server.close();
console.log(failed ? `\n${failed} FAILED` : '\nall ok');
process.exit(failed ? 1 : 0);
