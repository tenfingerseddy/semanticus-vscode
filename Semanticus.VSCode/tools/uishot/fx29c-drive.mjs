// fx29c: rc28 gap 1 at its cause. The engine wrote the calm "This score turns on when Jev scoring is released." line on Totals
// and the Description score for every unscored case, so the card and the Checks page (which hide that line) showed nothing,
// where they should say what stops scoring. This drive renders the real Studio (harness.html, mocked engine
// fx29-checks-fixture.js) on the Checks page with Total Quantity's card open, at 1440 and 290, light and dark, for each case
// with the engine's lines: old=1 sends what the base engine (j29) sent, without it what the fixed engine sends (both read live
// on pilot2, fx29c/live/out-*.txt):
//   A  no licence (or a Pro tier with no licence, the rc29 capture): "Scoring needs a Pro plan."
//   B  a licence with Pro advice off for the model: "Pro advice is off for this model." with Turn on
//   C  a model never saved: "Save the model or open it live to score it."
//   D  a licence, advice on, not scored yet: no line, and never the calm line
//   E  Total Quantity with no Description: the card says "No Description yet.", never the calm line
// FX29C_MODE=before records each failure (the failing-first proof); after (the default) fails on any. Shots:
// <item>-<mode>-<width>-<theme>.png in OUTDIR, plus results-<mode>.json. FX29C_ONLY=<regex> picks items.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { THEMES } from './themes.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const mode = process.env.FX29C_MODE === 'before' ? 'before' : 'after';
const root = resolve(here, '../..');
const only = process.env.FX29C_ONLY ? new RegExp(process.env.FX29C_ONLY) : null;
const out = resolve(process.env.OUTDIR || join(here, 'shots/fx29c'));
mkdirSync(out, { recursive: true });
const fixture = readFileSync(join(here, 'fx29-checks-fixture.js'), 'utf8');
const CALM = 'This score turns on when Jev scoring is released.';
const failures = [];
const check = (what, ok, detail) => { if (ok) return; if (mode === 'after') assert.fail(`${what}: ${detail}`); failures.push(`${what}: ${detail}`); };

// What the engine sends for each case, written into the mock engine before the page reads it. ?fx29c=<item>&old=1 sends what
// the base engine sent; without old, what the fixed engine sends.
const ITEMS = `
(function () {
  const q = new URLSearchParams(location.search), item = q.get('fx29c'), old = q.get('old') === '1', e = window.marksEngine;
  if (!item || !e) return;
  const CALM = ${JSON.stringify(CALM)};
  const lines = { A: 'Scoring needs a Pro plan.', B: 'Pro advice is off for this model.', C: 'Save the model or open it live to score it.',
    D: 'It is scored when this measure is checked.', E: 'There is no Description to score.' };
  const reason = old ? CALM : lines[item];
  for (const ref of e.call('getChecksPage', []).measures.map(m => m.ref)) {
    const bare = item === 'E' && ref === 'measure:Sales/Total Quantity';
    if (item !== 'E') for (const m of e.call('getMeasureMarks', [ref]).marks) if (m.name === 'Totals')
      Object.assign(m, { state: 'not run', sentence: 'Not scored yet. ' + reason, reasonCode: 'totals_off', applies: false, verdict: null, cells: [], total: null });
    const d = e.call('getMeasureDescription', [ref]);
    if (d.match && (item !== 'E' || bare)) Object.assign(d.match, { score: null, level: 'not scored yet', label: 'not scored yet', reason });
  }
  // E: Total Quantity has no Description, on the card and the Checks page.
  if (item === 'E') {
    const call = e.call, blank = x => Object.assign(x, { description: '', oneLine: '' });
    e.call = function (method, args) {
      const r = call.call(e, method, args);
      if (method === 'getMeasureDescription' && args[0] === 'measure:Sales/Total Quantity') blank(r);
      if (method === 'getChecksPage') r.measures.filter(m => m.ref === 'measure:Sales/Total Quantity').forEach(blank);
      return r;
    };
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

  const old = mode === 'before' ? '&old=1' : '';
  // The Checks page with Total Quantity's card open.
  const studio = async (item) => {
    await send('Page.navigate', { url: `${base}/tools/uishot/harness.html?conn=1&tier=pro&sc=sum&fx29c=${item}${old}` });
    await wait("[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Review')");
    await press('Review'); await press('Checks');
    await wait(`!!${tid('checks-measures')} && !!${tid('plan-sentence')} && !/Making/.test(${tid('plan-sentence')}.textContent)`, 'the Checks page');
    await evaluate(`document.querySelector('[data-testid="checks-row-measure:Sales/Total Quantity"] button').click()`);
    await wait(`!!${tid('marks-panel')} && !!${tid('mk-verdict')} && !/Reading/.test(${tid('mk-verdict')}.textContent)`, 'Total Quantity card');
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
  // What the card and the Checks page must say for each case (null: no line), and the calm line never.
  const LINES = { A: /^Scoring needs a Pro plan\.$/, B: /^Pro advice is off for this model\. Turn on$/, C: /^Save the model or open it live to score it\.$/, D: null, E: null };
  const items = ['A', 'B', 'C', 'D', 'E'].filter(item => !only || only.test(item));
  for (const item of items) for (const theme of ['light', 'dark']) for (const width of [1440, 290]) {
    await size(width, 900);
    await studio(item);
    await applyTheme(theme);
    await evaluate('document.fonts.ready'); await delay(200);
    const m = await evaluate(MEASURE);
    const where = `${item} ${width} ${theme}`;
    measured.push({ item, width, theme, ...m });
    check(item + ' no side scroll', m.scrolls.length === 0, `${where}: scrolls sideways: ${m.scrolls.join(', ')}`);
    check(item + ' nothing cut', m.clipped.length === 0, `${where}: cut at the right: ${m.clipped.join(', ')}`);
    const card = await text('mk-actionable'), page = await text('checks-actionable'), all = await evaluate('document.body.innerText');
    measured[measured.length - 1].lines = { card, page };
    const want = LINES[item];
    if (want) {
      check(item + ' line', want.test(card), `${where}: the card line reads "${card}"`);
      check(item + ' line', want.test(page), `${where}: the Checks page line reads "${page}"`);
      if (item === 'B') check('B Turn on', await evaluate(`!!${tid('mk-turn-on')} && !!${tid('checks-turn-on')}`), `${where}: no Turn on beside the line`);
    } else check(item + ' no line', card === '' && page === '', `${where}: a line shows: card "${card}", page "${page}"`);
    if (item === 'E') check('E no Description', /No Description yet\./.test(await text('marks-panel')), `${where}: the card does not say No Description yet.`);
    check(item + ' no calm line', !all.includes(CALM), `${where}: the page shows the calm line`);
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    const file = `${item}-${mode}-${width}-${theme}.png`;
    writeFileSync(join(out, file), Buffer.from(shot.data, 'base64'));
    shots.push(file);
    // The card itself, scrolled into view: at 1440 it opens below the fold of the Checks page.
    await evaluate(`${tid('marks-panel')}.scrollIntoView({ block: 'start' })`); await delay(150);
    const cardShot = await send('Page.captureScreenshot', { format: 'png' });
    const cardFile = `${item}-${mode}-${width}-${theme}-card.png`;
    writeFileSync(join(out, cardFile), Buffer.from(cardShot.data, 'base64'));
    shots.push(cardFile);
  }
  assert.deepEqual(errors, [], 'no page errors');
  const result = { mode, shots, failures: [...new Set(failures)], measured };
  writeFileSync(join(out, `results-${mode}.json`), JSON.stringify(result, null, 2));
  console.log(JSON.stringify({ mode, out, shots: shots.length, failures: result.failures,
    lines: measured.map(m => `${m.item} ${m.width} ${m.theme}: card "${m.lines.card}" page "${m.lines.page}"`) }, null, 1));
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
