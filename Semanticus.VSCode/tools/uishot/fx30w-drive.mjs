// fx30w: the screen-side faults of the wr1 list, rendered in the real Studio (harness.html) and Properties (propgrid.html)
// on the mocked engine (fx29-checks-fixture.js), at 1440 and 290, light and dark. Scenes:
//   open    F1 and F8: a fresh open, nothing checked yet, Pro advice off for the model, one measure changed. The engine's
//           page read names what stops scoring (unscored); the Checks page and Total Quantity's card in both doors show
//           "Pro advice is off for this model." with Turn on, and the run plan line keeps 15 words with four reason groups.
//   filters F9: Total Quantity's Filters detail. "No ignored filter in <n> places." and "Where it looked" never show two
//           different counts for one word.
//   move    S2: Total Quantity's card is open in Verified; a push moves it to Needs review. The card stays in view.
// FX30W_MODE=before records each failure (the failing-first proof); after (the default) fails on any. Shots:
// <scene>-<door>-<mode>-<width>-<theme>.png in OUTDIR, plus results-<mode>.json. FX30W_ONLY=<regex> picks scenes.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { THEMES } from './themes.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const mode = process.env.FX30W_MODE === 'before' ? 'before' : 'after';
const root = resolve(here, '../..');
const only = process.env.FX30W_ONLY ? new RegExp(process.env.FX30W_ONLY) : null;
const out = resolve(process.env.OUTDIR || join(here, 'shots/fx30w'));
mkdirSync(out, { recursive: true });
const fixture = readFileSync(join(here, 'fx29-checks-fixture.js'), 'utf8');
const failures = [];
const check = (what, ok, detail) => { if (ok) return; if (mode === 'after') assert.fail(`${what}: ${detail}`); failures.push(`${what}: ${detail}`); };

// The open scene: what the engine sends at a fresh open with Pro advice off. The page read carries the scoring state
// (unscored); no mark has run, so no mark carries the line. The plan has four reason groups, as on pilot2 after an edit.
const ITEMS = `
(function () {
  const q = new URLSearchParams(location.search), scene = q.get('fx30w'), e = window.marksEngine;
  if (!scene || !e) return;
  const call = e.call;
  e.call = function (method, args) {
    const r = call.call(e, method, args);
    if (scene === 'open' && method === 'getChecksPage') r.unscored = 'Pro advice is off for this model.';
    if (scene === 'open' && method === 'planMeasureRun' && r && r.picks) r.picks = r.picks.map((p, i) => Object.assign({}, p,
      { group: i < 1 ? 'changed' : i < 4 ? 'core' : i < 9 ? 'risky' : 'oldest' }));
    if (method === 'getConsent') return { current: false };
    return r;
  };
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

    const text = t => evaluate(`(${tid(t)}?.innerText || '').replace(/[ \\n\\t]+/g, ' ').trim()`);
  const WORDS = /[A-Za-z0-9][A-Za-z0-9%:.,'’()-]*/g;
  const count = s => (s.match(WORDS) ?? []).length;
  const sc = scene => scene === 'open' ? 'fresh' : 'sum';
  const TQ = 'measure:Sales/Total Quantity';
  const openFilters = async () => {
    await wait(`!!${tid('mk-row-Filters')}`, 'the Filters mark'); await evaluate(`${tid('mk-row-Filters')}.click()`);
    await wait(`!!${tid('mk-card-Filters')}`, 'the Filters detail');
    await evaluate(`${tid('mk-card-Filters')}.scrollIntoView({ block: 'center' })`);
  };
  // The Checks page with Total Quantity's card open.
  const studio = async scene => {
    await send('Page.navigate', { url: `${base}/tools/uishot/harness.html?conn=1&tier=pro&sc=${sc(scene)}&fx30w=${scene}` });
    await wait("[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Review')");
    await press('Review'); await press('Checks');
    await wait(`!!${tid('checks-measures')} && !!${tid('plan-sentence')} && !/Making/.test(${tid('plan-sentence')}.textContent)`, 'the Checks page');
    if (scene === 'open') {
      // Nothing checked yet: Not checked yet is the only group, open.
      await wait(`!!document.querySelector('[data-testid="checks-row-${TQ}"] button')`, 'the Total Quantity row');
    }
    await evaluate(`document.querySelector('[data-testid="checks-row-${TQ}"] button').click()`);
    await wait(`!!${tid('marks-panel')} && !!${tid('mk-verdict')} && !/Reading/.test(${tid('mk-verdict')}.textContent)`, 'Total Quantity card');
    await delay(250);
    if (scene === 'filters') await openFilters();
    if (scene === 'move') {
      await evaluate(`${tid('marks-panel')}.scrollIntoView({ block: 'center' })`); await delay(150);
      // The engine's push: Total Quantity's Contexts now finds blanks, so the measure moves from Verified to Needs review.
      await evaluate(`(() => { const e = window.marksEngine, r = e.call('getMeasureMarks', [${JSON.stringify(TQ)}]);
        const marks = r.marks.map(m => m.name === 'Contexts' ? Object.assign({}, m, { state: 'suspect', verdict: 'review', sentence: 'Blank in 3 places.' }) : m);
        window.dispatchEvent(new MessageEvent('message', { data: { type: 'checkState', payload: { version: 9999, states: [
          { measureRef: ${JSON.stringify(TQ)}, version: 9999, running: false, lastChecked: r.lastChecked, marks }] } } })); })()`);
      await wait(`!!document.querySelector('[data-testid="group-review"] [data-testid="checks-row-${TQ}"]')`, 'Total Quantity in Needs review');
      await delay(400);
    }
  };
  // Properties: the same measure in the grid, as the provider loads it.
  const props = async scene => {
    await send('Page.navigate', { url: `${base}/tools/uishot/propgrid.html?s=empty&sc=${sc(scene)}&fx30w=${scene}` });
    await wait("document.readyState === 'complete' && !!window.semanticusGrid");
    const P = (name, displayName, category, value) => ({ name, displayName, category, kind: 'string', value, description: displayName, options: [], readOnly: false, varies: false });
    const fields = [P('Name', 'Name', 'Basic', 'Total Quantity'), P('Expression', 'Expression', 'Advanced', 'SUM ( Sales[Quantity] )'), P('FormatString', 'Format String', 'Formatting', '#,0')];
    const load = { type: 'load', key: JSON.stringify([TQ]), model: 'Contoso.pbip', name: 'Total Quantity', multi: false, props: fields };
    await evaluate(`window.dispatchEvent(new MessageEvent('message', { data: ${JSON.stringify(load)} }))`);
    await wait(`!!${tid('mk-verdict')} && !/Reading/.test(${tid('mk-verdict')}.textContent)`, 'the Properties card');
    await delay(400);
    if (scene === 'filters') await openFilters();
  };

  const scenes = [['open', ['studio', 'props']], ['filters', ['studio', 'props']], ['move', ['studio']]].filter(([s]) => !only || only.test(s));
  for (const [scene, doors] of scenes) for (const door of doors) for (const theme of ['light', 'dark']) for (const width of [1440, 290]) {
    await size(width, 900);
    await (door === 'studio' ? studio(scene) : props(scene));
    await applyTheme(theme);
    await evaluate('document.fonts.ready'); await delay(200);
    const m = await evaluate(MEASURE);
    const where = `${scene} ${door} ${width} ${theme}`;
    const row = { scene, door, width, theme, ...m };
    measured.push(row);
    check(scene + ' no side scroll', m.scrolls.length === 0, `${where}: scrolls sideways: ${m.scrolls.join(', ')}`);
    check(scene + ' nothing cut', m.clipped.length === 0, `${where}: cut at the right: ${m.clipped.join(', ')}`);
    if (scene === 'open') {
      // F1: the line from the scoring state, with Turn on, at open, before any run.
      const card = await text('mk-actionable'), page = door === 'studio' ? await text('checks-actionable') : null;
      row.lines = { card, page };
      check('F1 card line', /^Pro advice is off for this model\. Turn on$/.test(card), `${where}: the card line reads "${card}"`);
      if (door === 'studio') {
        check('F1 page line', /^Pro advice is off for this model\. Turn on$/.test(page), `${where}: the Checks page line reads "${page}"`);
        // F8: the run plan line, buttons included, is 15 words or fewer with four reason groups.
        const plan = await evaluate(`(${tid('plan-sentence')}.closest('.rp-line, [data-testid="run-plan"]') || ${tid('plan-sentence')}.parentElement).innerText.replace(/[ \\n\\t]+/g, ' ').trim()`);
        row.plan = plan;
        check('F8 plan words', count(plan) <= 15, `${where}: the plan line is ${count(plan)} words: "${plan}"`);
      }
    }
    if (scene === 'filters') {
      // F9: one count for one word. The sentence counts places (cells checked); the button never shows another number.
      const say = await evaluate(`${tid('mk-card-Filters')}.querySelector('.mk-say').innerText`);
      const button = await evaluate(`([...${tid('mk-card-Filters')}.querySelectorAll('button')].find(b => /Where it looked/.test(b.textContent)) || {}).textContent || ''`);
      row.lines = { say, button };
      const places = /in ([0-9,]+) places/.exec(say)?.[1], looked = /\(([0-9,]+)\)/.exec(button)?.[1];
      check('F9 one count', !looked || looked === places, `${where}: "${say}" beside "${button}"`);
    }
    if (scene === 'move') {
      // S2: the open card's verdict line is in view after its measure changed group: inside the box that scrolls, not under
      // the sticky header above it.
      const view = await evaluate(`(() => { const v = ${tid('mk-verdict')}, r = v.getBoundingClientRect();
        let box = v.parentElement; while (box && !(/auto|scroll/.test(getComputedStyle(box).overflowY) && box.scrollHeight > box.clientHeight)) box = box.parentElement;
        const b = box ? box.getBoundingClientRect() : { top: 0, bottom: innerHeight };
        return { top: Math.round(r.top), bottom: Math.round(r.bottom), boxTop: Math.round(b.top), boxBottom: Math.round(b.bottom) }; })()`);
      row.card = view;
      check('S2 card in view', view.top >= view.boxTop && view.bottom <= view.boxBottom, `${where}: the card's verdict line is at ${view.top} to ${view.bottom}, the view is ${view.boxTop} to ${view.boxBottom}`);
    }
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    const file = `${scene}-${door}-${mode}-${width}-${theme}.png`;
    writeFileSync(join(out, file), Buffer.from(shot.data, 'base64'));
    shots.push(file);
    if (door === 'studio' && scene === 'open') {
      // The card itself, scrolled into view: at 290 it opens below the fold of the Checks page.
      await evaluate(`${tid('marks-panel')}.scrollIntoView({ block: 'start' })`); await delay(150);
      const cardShot = await send('Page.captureScreenshot', { format: 'png' });
      const cardFile = `${scene}-${door}-${mode}-${width}-${theme}-card.png`;
      writeFileSync(join(out, cardFile), Buffer.from(cardShot.data, 'base64'));
      shots.push(cardFile);
    }
  }
  assert.deepEqual(errors, [], 'no page errors');
  const result = { mode, shots, failures: [...new Set(failures)], measured };
  writeFileSync(join(out, `results-${mode}.json`), JSON.stringify(result, null, 2));
  console.log(JSON.stringify({ mode, out, shots: shots.length, failures: result.failures }, null, 1));
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
