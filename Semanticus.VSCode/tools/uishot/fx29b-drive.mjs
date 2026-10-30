// fx29b item D: Studio at a 290 px panel scrolled sideways (the top bar clipped), and the grey "Unknown" word read at about
// 3:1. This drive renders the real Studio (harness.html, mocked engine fx29-checks-fixture.js) on the Checks page with its
// Unknown group open and an Unknown measure's card open, at 1440 and 290, light and dark, and asserts in the DOM:
//   1. nothing scrolls sideways: the document, and every element that can scroll on x, has scrollWidth <= clientWidth;
//   2. nothing is cut at the right edge: no visible leaf element ends past the window or past an ancestor that clips it;
//   3. every Unknown word and glyph (the header count, the group heading, the row glyph, the card verdict) reads at 4.5:1
//      or more against the colour actually painted behind it.
// The addendum adds, at 1440 and 290, light and dark:
//   E  a never-saved model: the card and the Checks page say what to do ("Save the model or open it live to score it.");
//   F  right after Turn on: no "Pro advice is off" line is left (the engine's line says advice is on and the measure is rechecked);
//   G  permission moved mid-score: the line says so, not "Pro advice is off";
//   H  a Trusted number with two trillion-scale currency numbers: no number is cut, in Studio and in Properties at 290.
// FX29B_MODE=before records each failure instead of throwing (the failing-first proof), and FX29B_ROOT serves an older tree
// (the fx29 build) for it; after (the default) serves this tree and fails on any. Shots: <item>-<mode>-<width>-<theme>.png in
// OUTDIR, plus results-<mode>.json with every measurement. FX29B_ONLY=<regex> picks items.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { THEMES } from './themes.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const mode = process.env.FX29B_MODE === 'before' ? 'before' : 'after';
const root = resolve(process.env.FX29B_ROOT || resolve(here, '../..'));
const only = process.env.FX29B_ONLY ? new RegExp(process.env.FX29B_ONLY) : null;
const out = resolve(process.env.OUTDIR || join(here, 'shots/fx29b'));
mkdirSync(out, { recursive: true });
const fixture = readFileSync(join(here, 'fx29-checks-fixture.js'), 'utf8');
const MIN = 4.5;
const failures = [];
const check = (what, ok, detail) => { if (ok) return; if (mode === 'after') assert.fail(`${what}: ${detail}`); failures.push(`${what}: ${detail}`); };

// What the engine sends for each item, written into the mock engine's marks before the page reads them. ?fx29b=<item>&old=1
// sends what the fx29 engine sent (the before shots); without old, what this engine sends.
const ITEMS = `
(function () {
  const q = new URLSearchParams(location.search), item = q.get('fx29b'), old = q.get('old') === '1', e = window.marksEngine;
  if (!item || !e) return;
  const lines = {
    E: old ? 'Not scored yet. This score turns on when Jev scoring is released.' : 'Not scored yet. Save the model or open it live to score it.',
    F: old ? 'Not scored yet. Pro advice is off for this model.' : 'Not scored yet. Pro advice is on now. Check again to score it.',
    G: old ? 'Not scored yet. Pro advice is off for this model.' : 'Not scored yet. Pro advice changed while this was being scored. Check again to score it.',
  };
  const all = e.call('getChecksPage', []).measures.map(m => m.ref);
  for (const ref of all) {
    const marks = e.call('getMeasureMarks', [ref]).marks;
    if (lines[item]) for (const m of marks) if (m.name === 'Totals')
      Object.assign(m, { state: 'not run', sentence: lines[item], reasonCode: 'totals_off', applies: false, verdict: null, cells: [], total: null });
    if (item === 'H' && ref === 'measure:Sales/Sales Amount') for (const m of marks) if (m.name === 'Trusted number')
      m.cells = [
        { context: 'Customer[City]: Wasseralfingen-Hüttlingen Industriegebiet Nord', before: '1234567890123.45', after: '-9876543210987.65', beforeText: '$1,234,567,890,123.45', afterText: '-$9,876,543,210,987.65', gapText: '$11,111,111,101,110.10' },
        { context: 'Grand total', before: '9999999999999.99', after: '-9999999999999.99', beforeText: '$9,999,999,999,999.99', afterText: '-$9,999,999,999,999.99', gapText: '$19,999,999,999,999.98' }];
  }
})();`;
const server = createServer((req, res) => {
  const file = resolve(root, '.' + decodeURIComponent((req.url || '/').split('?')[0]));
  if (!file.startsWith(root + '/') || !existsSync(file)) { res.writeHead(404); res.end(); return; }
  res.setHeader('content-type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json' })[extname(file)] || 'application/octet-stream');
  res.end(readFileSync(file));
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const base = `http://127.0.0.1:${server.address().port}`;
const browser = spawn(process.env.SEMANTICUS_BROWSER || '/usr/bin/chromium', [
  '--headless=new', '--no-sandbox', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  '--disable-background-networking', '--remote-debugging-port=0', '--user-data-dir=' + join(out, 'browser-profile'), 'about:blank',
], { stdio: ['ignore', 'ignore', 'pipe'] });
const delay = ms => new Promise(r => setTimeout(r, ms));
const errors = [], shots = [], measured = [];
let socket, closeBrowser;
try {
  const endpoint = await new Promise((resolve, reject) => {
    let buffer = '';
    const timer = setTimeout(() => reject(new Error('Chromium did not start its local debug endpoint.')), 15000);
    browser.stderr.on('data', chunk => { buffer += chunk; const f = buffer.match(/DevTools listening on (ws:\/\/[^\s]+)/); if (f) { clearTimeout(timer); resolve(f[1]); } });
    browser.once('exit', code => { clearTimeout(timer); reject(new Error('Chromium exited: ' + code)); });
  });
  const origin = new URL(endpoint).origin.replace('ws:', 'http:');
  const targets = await (await fetch(origin + '/json/list')).json();
  socket = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  let id = 0;
  const pending = new Map();
  socket.onmessage = event => {
    const msg = JSON.parse(event.data);
    if (msg.method === 'Runtime.exceptionThrown') errors.push(msg.params.exceptionDetails.exception?.description || msg.params.exceptionDetails.text);
    const call = pending.get(msg.id);
    if (call) { pending.delete(msg.id); msg.error ? call.reject(new Error(msg.error.message)) : call.resolve(msg.result); }
  };
  const send = (method, params = {}) => new Promise((resolve, reject) => { const seq = ++id; pending.set(seq, { resolve, reject }); socket.send(JSON.stringify({ id: seq, method, params })); });
  closeBrowser = async () => {
    const exited = new Promise(resolve => { if (browser.exitCode !== null || browser.signalCode !== null) resolve(); else browser.once('exit', resolve); });
    await send('Browser.close');
    await exited;
  };
  const evaluate = async expression => {
    const value = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (value.exceptionDetails) throw new Error(value.exceptionDetails.exception?.description || value.exceptionDetails.text);
    return value.result?.value;
  };
  const wait = async (expression, what = expression) => {
    for (let i = 0; i < 120; i++) { if (await evaluate(expression)) return; await delay(50); }
    throw new Error('The page did not reach: ' + what);
  };
  const press = label => evaluate(`(() => { const b = [...document.querySelectorAll('button')].find(b => b.textContent.trim() === ${JSON.stringify(label)} && !b.disabled); if (!b) throw new Error('Missing button: ' + ${JSON.stringify(label)}); b.click(); })()`);
  const tid = t => `document.querySelector('[data-testid="${t}"]')`;
  await send('Page.enable'); await send('Runtime.enable');
  await send('Page.addScriptToEvaluateOnNewDocument', { source: fixture + ITEMS + `
    (function () {
      Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => window.__h, set: h => {
        window.__h = h;
        h.setResponse('getOrientation', { activeWork: { workflows: [] } });
        h.setResponse('getWorkflowRun', null);
        h.setResponse('listTests', { definitions: [] });
        h.setResponse('listTableMappings', { rows: [] });
        for (const m of ['getChecksPage', 'getMeasureCheckCache', 'getMeasureMarks', 'getMeasureDescription',
          'planMeasureRun', 'runMeasurePlan', 'stopMeasureRun', 'keepMeasureEdit', 'listSqlSources', 'cleanupMeaningAnnotations'])
          h.setResponse(m, params => window.marksEngine.call(m, Array.isArray(params) ? params : [params]));
      } });
    })();` });
  const applyTheme = name => evaluate(`(() => { const t = ${JSON.stringify(THEMES[name])}; for (const [k, v] of Object.entries(t.vars)) document.documentElement.style.setProperty(k, v);
    document.body.classList.remove('vscode-dark', 'vscode-light'); document.body.classList.add(t.bodyClass); document.body.dataset.vscodeThemeId = t.label;
    document.body.style.background = t.vars['--vscode-editor-background']; document.body.style.color = t.vars['--vscode-foreground']; })()`);
  const size = (width, height) => send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });

  const old = mode === 'before' && process.env.FX29B_ROOT ? '&old=1' : '';
  // The Checks page with one measure's card open: the first Unknown measure (D), Total Quantity (E, F, G), or Sales Amount
  // with its Trusted number detail open (H).
  const studio = async (item) => {
    const sc = item === 'H' ? 'broken' : 'sum';
    await send('Page.navigate', { url: `${base}/tools/uishot/harness.html?conn=1&tier=pro&sc=${sc}&fx29b=${item}${old}` });
    await wait("[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Review')");
    await press('Review'); await press('Checks');
    await wait(`!!${tid('checks-measures')} && !!${tid('plan-sentence')} && !/Making/.test(${tid('plan-sentence')}.textContent)`, 'the Checks page');
    if (item === 'D') return reach();
    const name = item === 'H' ? 'Sales Amount' : 'Total Quantity';
    await evaluate(`document.querySelector('[data-testid="checks-row-measure:Sales/${name}"] button').click()`);
    await wait(`!!${tid('marks-panel')} && !!${tid('mk-verdict')} && !/Reading/.test(${tid('mk-verdict')}.textContent)`, name + ' card');
    if (item === 'H') { await wait(`!!${tid('mk-row-Trusted number')}`, 'the Trusted number mark'); await evaluate(`${tid('mk-row-Trusted number')}.click()`); await wait(`!!${tid('mk-card-Trusted number')}`, 'the Trusted number detail');
      await evaluate(`${tid('mk-card-Trusted number')}.scrollIntoView({ block: 'end' })`); }
    await delay(200);
  };
  // Properties at 290: the same measure in the grid, as the provider loads it.
  const props = async (item) => {
    await send('Page.navigate', { url: `${base}/tools/uishot/propgrid.html?s=empty&sc=${item === 'H' ? 'broken' : 'sum'}&fx29b=${item}${old}` });
    await wait("document.readyState === 'complete' && !!window.semanticusGrid");
    const n = item === 'H' ? 'Sales Amount' : 'Total Quantity';
    const P = (name, displayName, category, value) => ({ name, displayName, category, kind: 'string', value, description: displayName, options: [], readOnly: false, varies: false });
    const props = [P('Name', 'Name', 'Basic', n), P('Expression', 'Expression', 'Advanced', 'SUM ( Sales[Quantity] )'), P('FormatString', 'Format String', 'Formatting', '#,0')];
    const load = { type: 'load', key: JSON.stringify(['measure:Sales/' + n]), model: 'Contoso.pbip', name: n, multi: false, props };
    await evaluate(`window.dispatchEvent(new MessageEvent('message', { data: ${JSON.stringify(load)} }))`);
    await wait(`!!${tid('mk-verdict')} && !/Reading/.test(${tid('mk-verdict')}.textContent)`, 'the Properties card');
    if (item === 'H') { await wait(`!!${tid('mk-row-Trusted number')}`, 'the Trusted number mark'); await evaluate(`${tid('mk-row-Trusted number')}.click()`); await wait(`!!${tid('mk-card-Trusted number')}`, 'the Trusted number detail'); }
    await delay(200);
  };
  // The Checks page with the Unknown group open and the first Unknown measure's card open.
  const reach = async () => {
    await wait("[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Review')");
    await press('Review'); await press('Checks');
    await wait(`!!${tid('checks-measures')} && !!${tid('plan-sentence')} && !/Making/.test(${tid('plan-sentence')}.textContent)`, 'the Checks page');
    await wait(`!!${tid('group-unknown')}`, 'the Unknown group');
    const head = `${tid('group-unknown')}.querySelector('button.cp-ghead')`;
    if (await evaluate(`!!${head}`) && !(await evaluate(`!!${tid('group-unknown')}.querySelector('.cp-row')`))) await evaluate(`${head}.click()`);
    await wait(`!!${tid('group-unknown')}.querySelector('.cp-row button')`, 'an Unknown row');
    await evaluate(`${tid('group-unknown')}.querySelector('.cp-row button').click()`);
    await wait(`!!${tid('marks-panel')} && !!${tid('mk-verdict')} && !/Reading/.test(${tid('mk-verdict')}.textContent)`, 'the Unknown card');
    await delay(200);
  };

  // Every measurement runs in the page: overflow, clipping and contrast (painted colours read back through a canvas).
  const MEASURE = `(() => {
    const canvas = document.createElement('canvas'); canvas.width = canvas.height = 1;
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    const rgba = c => { ctx.clearRect(0, 0, 1, 1); ctx.fillStyle = '#000'; ctx.fillStyle = c; ctx.fillRect(0, 0, 1, 1); const d = ctx.getImageData(0, 0, 1, 1).data; return [d[0], d[1], d[2], d[3] / 255]; };
    const over = (top, under) => top.slice(0, 3).map((v, i) => v * top[3] + under[i] * (1 - top[3]));
    const ground = el => { const layers = []; for (let e = el; e; e = e.parentElement) { const b = rgba(getComputedStyle(e).backgroundColor); if (b[3] > 0) layers.push(b); if (b[3] >= 1) break; }
      let c = [255, 255, 255]; for (const l of layers.reverse()) c = over(l, c); return c; };
    const lum = c => { const [r, g, b] = c.map(v => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
    const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
    const visible = e => { const s = getComputedStyle(e); const r = e.getBoundingClientRect(); return s.visibility !== 'hidden' && s.display !== 'none' && r.width > 0 && r.height > 0; };
    const name = e => (e.getAttribute('data-testid') || (typeof e.className === 'string' && e.className.split(' ')[0]) || e.tagName) + ':' + (e.textContent || '').trim().replace(/\\s+/g, ' ').slice(0, 30);
    const W = document.documentElement.clientWidth;
    const scrolls = [document.scrollingElement, ...document.querySelectorAll('*')].filter(e => e && visible(e)
      && (e === document.scrollingElement || /auto|scroll/.test(getComputedStyle(e).overflowX)) && e.scrollWidth > e.clientWidth + 1)
      .map(e => name(e) + ' ' + e.scrollWidth + '>' + e.clientWidth);
    // A leaf that ends past the window, or past an ancestor that clips its overflow, is cut off.
    const clipped = [];
    for (const e of document.querySelectorAll('body *')) {
      if (e.children.length || !visible(e) || !(e.textContent || '').trim() || e.closest('.sr-only')) continue;
      const r = e.getBoundingClientRect();
      if (r.bottom < 0 || r.top > innerHeight) continue;
      let edge = W;
      for (let a = e.parentElement; a; a = a.parentElement) if (getComputedStyle(a).overflowX !== 'visible') edge = Math.min(edge, a.getBoundingClientRect().right);
      if (r.right > edge + 1 && getComputedStyle(e).textOverflow !== 'ellipsis') clipped.push(name(e) + ' ' + Math.round(r.right) + '>' + Math.round(edge));
      else if (getComputedStyle(e).overflowX !== 'visible' && e.scrollWidth > e.clientWidth + 1 && getComputedStyle(e).textOverflow !== 'ellipsis') clipped.push(name(e) + ' clips its own text ' + e.scrollWidth + '>' + e.clientWidth);
    }
    const words = [...document.querySelectorAll('.cp-s-unknown, .mk-v-unknown .mk-vglyph, .mk-v-unknown b, .mk-s-unknown .mk-glyph, [data-testid="group-unknown"] .cp-ghead, [data-testid="checks-verdict"] *, [data-testid="checks-verdict"]')]
      .filter(e => visible(e) && (e.matches('.cp-s-unknown, .mk-v-unknown .mk-vglyph, .mk-v-unknown b, .mk-s-unknown .mk-glyph') || [...e.childNodes].some(n => n.nodeType === 3 && /unknown/i.test(n.textContent))))
      .map(e => { const fg = rgba(getComputedStyle(e).color); const bg = ground(e); return { el: name(e), ratio: Math.round(ratio(over(fg, bg), bg) * 100) / 100 }; });
    const bar = document.querySelector('header, [data-testid="studio-header"], .studio-header');
    return { width: W, scrolls, clipped: clipped.slice(0, 12), words, bar: bar ? name(bar).slice(0, 40) : null };
  })()`;

  const text = t => evaluate(`(${tid(t)}?.innerText || '').replace(/\\s+/g, ' ').trim()`);
  // What each item's card (and, in Studio, the Checks page) must say, and must not say.
  const LINES = {
    E: [/^Save the model or open it live to score it\.$/, null],
    F: [/^Pro advice is on now\. Check again to score it\.$/, /Pro advice is off/],
    G: [/^Pro advice changed while this was being scored\. Check again to score it\.$/, /Pro advice is off/],
  };
  const items = [['D', 'studio'], ['E', 'studio'], ['F', 'studio'], ['G', 'studio'], ['H', 'studio'], ['E', 'props'], ['F', 'props'], ['H', 'props']]
    .filter(([item]) => !only || only.test(item));
  for (const [item, place] of items) for (const theme of ['light', 'dark']) for (const width of place === 'props' ? [290] : [1440, 290]) {
    await size(width, 900);
    await (place === 'props' ? props(item) : studio(item));
    await applyTheme(theme);
    await evaluate('document.fonts.ready'); await delay(200);
    const m = await evaluate(MEASURE);
    const tag = item + (place === 'props' ? '-props' : '');
    const where = `${tag} ${width} ${theme}`;
    measured.push({ item: tag, width, theme, ...m });
    check(item + ' no side scroll', m.scrolls.length === 0, `${where}: scrolls sideways: ${m.scrolls.join(', ')}`);
    check(item + ' nothing cut', m.clipped.length === 0, `${where}: cut at the right: ${m.clipped.join(', ')}`);
    if (item === 'D') {
      check('Unknown read', m.words.length >= 3, `${where}: only ${m.words.length} Unknown words and glyphs were found`);
      for (const w of m.words) check('Unknown contrast', w.ratio >= MIN, `${where}: ${w.el} reads at ${w.ratio}:1`);
    }
    if (LINES[item]) {
      const [want, never] = LINES[item];
      const card = await text('mk-actionable'), page = place === 'studio' ? await text('checks-actionable') : null, panel = await text('marks-panel');
      m.lines = { card, page };
      check(item + ' line', want.test(card), `${where}: the card line reads "${card}"`);
      if (page != null) check(item + ' line', want.test(page), `${where}: the Checks page line reads "${page}"`);
      if (never) check(item + ' line', !never.test(panel + ' ' + (page ?? '')), `${where}: still says ${never}`);
    }
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    const file = `${tag}-${mode}-${width}-${theme}.png`;
    writeFileSync(join(out, file), Buffer.from(shot.data, 'base64'));
    shots.push(file);
  }
  assert.deepEqual(errors, [], 'no page errors');
  const result = { mode, shots, failures: [...new Set(failures)], measured };
  writeFileSync(join(out, `results-${mode}.json`), JSON.stringify(result, null, 2));
  console.log(JSON.stringify({ mode, out, shots: shots.length, failures: result.failures,
    contrast: measured.filter(m => m.item === 'D').map(m => `${m.width} ${m.theme}: ` + Math.min(...m.words.map(w => w.ratio))) }, null, 1));
} catch (error) {
  writeFileSync(join(out, 'failure.json'), JSON.stringify({ error: String(error?.stack || error), errors, shots, measured }, null, 2));
  throw error;
} finally {
  if (closeBrowser) await Promise.race([closeBrowser().catch(() => {}), delay(2000)]);
  socket?.close();
  if (browser.exitCode === null && browser.signalCode === null) browser.kill('SIGTERM');
  server.closeAllConnections();
  await new Promise(r => server.close(r));
}
