// fx29: the measure card states the rc28 release tester found wrong (gaps 4, 5, 6 and 9), on the Checks page at 1440 and in
// Properties at a 290 px sidebar, light and dark, in headless Chromium with the mocked engine (checks-d1-fixture.js, then
// fx29-card-fixture.js). Each state asserts in the DOM; a failed state is still shot, and the drive exits 1 after all of them.
//   gap4: a place in a detail table wraps only between words; every number stays on one line.
//   gap5: the timing line reads "Timing. About <t>." with no run counts and no Cold or Warm words.
//   gap6: the Performance detail is one table: Where, Median, Spread, Cold.
//   gap9: after Keep, the card shows the kept Description at once, never "No Description yet." beside "Kept".
// FX29_PHASE names the shots (<gap>-<phase>-<width>-<theme>.png); OUTDIR is where they go.
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { THEMES } from './themes.mjs';

const here = dirname(fileURLToPath(import.meta.url)), root = resolve(here, '../..');
const out = resolve(process.env.OUTDIR || join(here, 'shots/fx29-card'));
const phase = process.env.FX29_PHASE || 'after';
const profile = resolve(process.env.FX29_PROFILE || join(out, 'browser-profile'));
mkdirSync(out, { recursive: true });
const fixture = readFileSync(join(here, 'checks-d1-fixture.js'), 'utf8') + '\n' + readFileSync(join(here, 'fx29-card-fixture.js'), 'utf8');
const only = process.env.FX29_ONLY ? new RegExp(process.env.FX29_ONLY) : null;
const server = createServer((req, res) => {
  const path = decodeURIComponent((req.url || '/').split('?')[0]);
  const file = resolve(root, '.' + path);
  if (!file.startsWith(root + '/') || !existsSync(file)) { res.writeHead(404); res.end(); return; }
  res.setHeader('content-type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json' })[extname(file)] || 'application/octet-stream');
  res.end(readFileSync(file));
});

await new Promise(r => server.listen(0, '127.0.0.1', r));
const base = `http://127.0.0.1:${server.address().port}`;
const browser = spawn(process.env.SEMANTICUS_BROWSER || '/usr/bin/chromium', [
  '--headless=new', '--no-sandbox', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  '--disable-background-networking', '--remote-debugging-port=0', '--user-data-dir=' + profile, 'about:blank',
], { stdio: ['ignore', 'ignore', 'pipe'] });
const delay = ms => new Promise(r => setTimeout(r, ms));
const errors = [], shots = [], failures = [];
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
  const text = t => evaluate(`(${tid(t)}?.innerText || '').replace(/\\s+/g, ' ').trim()`);
  await send('Page.enable'); await send('Runtime.enable');
  await send('Page.addScriptToEvaluateOnNewDocument', { source: fixture + `
    (function () {
      Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => window.__h, set: h => {
        window.__h = h;
        h.setResponse('getOrientation', { activeWork: { workflows: [] } });
        h.setResponse('getWorkflowRun', null);
        h.setResponse('listTests', { definitions: [] });
        h.setResponse('listTableMappings', { rows: [] });
        for (const m of ['getChecksPage', 'getMeasureCheckCache', 'getMeasureMarks', 'getMeasureDescription', 'draftMeasureDescription',
          'proposeMeasureDescription', 'keepMeasureDescription', 'planMeasureRun', 'runMeasurePlan', 'stopMeasureRun', 'keepMeasureEdit', 'listSqlSources'])
          h.setResponse(m, params => window.marksEngine.call(m, Array.isArray(params) ? params : [params]));
      } });
    })();` });
  const applyTheme = name => evaluate(`(() => { const t = ${JSON.stringify(THEMES[name])}; for (const [k, v] of Object.entries(t.vars)) document.documentElement.style.setProperty(k, v);
    document.body.classList.remove('vscode-dark', 'vscode-light'); document.body.classList.add(t.bodyClass); document.body.dataset.vscodeThemeId = t.label;
    document.body.style.background = t.vars['--vscode-editor-background']; document.body.style.color = t.vars['--vscode-foreground']; })()`);
  // VS Code on Linux, as the tester saw it: a webview gets the Linux font stack (system-ui first, wider than the Segoe UI that
  // THEMES carries) and VS Code's own 10 px scrollbar, which Properties shows beside its long grid. Without both the card is
  // about 10 px wider than in the product and the tester's split place names do not happen.
  const vscodeLinux = () => evaluate(`(() => { document.documentElement.style.setProperty('--vscode-font-family', 'system-ui, "Ubuntu", "Droid Sans", sans-serif');
    if (!document.getElementById('fx29-vscode')) { const s = document.createElement('style'); s.id = 'fx29-vscode';
      s.textContent = 'html { overflow-y: scroll; } ::-webkit-scrollbar { width: 10px; height: 10px; } ::-webkit-scrollbar-thumb { background: rgba(121,121,121,.4); }';
      document.head.appendChild(s); } })()`);
  const size = (width, height = 900) => send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
  const ref = n => 'measure:Sales/' + n;
  const query = (sc, fx) => `sc=${sc}&fx=${fx}`;

  const P = (name, displayName, category, value) => ({ name, displayName, category, kind: 'string', value, description: displayName, options: [], readOnly: false, varies: false });
  const openMark = async mark => { await evaluate(`${tid('mk-row-' + mark)}.click()`); await wait(`!!${tid('mk-card-' + mark)}`, 'the ' + mark + ' detail'); };
  const grid = async (sc, fx, n, mark) => {
    await send('Page.navigate', { url: `${base}/tools/uishot/propgrid.html?s=empty&${query(sc, fx)}` });
    await wait("document.readyState === 'complete' && !!window.semanticusGrid");
    const props = [P('Name', 'Name', 'Basic', n), P('FormatString', 'Format String', 'Formatting', '#,0')];
    await evaluate(`window.dispatchEvent(new MessageEvent('message', { data: ${JSON.stringify({ type: 'load', key: JSON.stringify([ref(n)]), model: 'Contoso.pbip', name: n, multi: false, props })} }))`);
    await wait(`!!${tid('mk-verdict')} && !/Reading/.test(${tid('mk-verdict')}.textContent)`, 'the card verdict');
    await delay(150);
    if (mark) await openMark(mark);
  };
  const checks = async (sc, fx, n, mark) => {
    await send('Page.navigate', { url: `${base}/tools/uishot/harness.html?conn=1&tier=pro&${query(sc, fx)}` });
    await wait("[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Review')");
    await press('Review'); await press('Checks');
    await wait(`!!${tid('checks-measures')} && !!${tid('plan-sentence')} && !/Making/.test(${tid('plan-sentence')}.textContent)`, 'the Checks page');
    await delay(150);
    await evaluate(`(() => { const g = ${tid('group-unknown')}; const b = g && g.querySelector('button[aria-expanded="false"]'); if (b) b.click(); })()`);
    await evaluate(`document.querySelector('[data-testid="checks-row-${ref(n)}"] button').click()`);
    await wait(`!!${tid('marks-panel')} && !!${tid('mk-verdict')} && !/Reading/.test(${tid('mk-verdict')}.textContent)`, n + ' card');
    await delay(150);
    if (mark) await openMark(mark);
    await evaluate(`document.querySelector('[data-testid="checks-row-${ref(n)}"]').scrollIntoView({ block: 'start' })`);
  };
  const reach = (sc, fx, n, mark) => width => width === 1440 ? checks(sc, fx, n, mark) : grid(sc, fx, n, mark);

  // Words split across line boxes in the place cells, and numbers on more than one line, in every detail table.
  const splitWords = () => evaluate(`(() => {
    const lines = (node, from, to) => { const r = document.createRange(); r.setStart(node, from); r.setEnd(node, to);
      return new Set([...r.getClientRects()].filter(b => b.width > 0).map(b => Math.round(b.top))).size; };
    const texts = cell => { const w = document.createTreeWalker(cell, NodeFilter.SHOW_TEXT); const list = []; while (w.nextNode()) list.push(w.currentNode); return list; };
    const bad = [];
    for (const table of document.querySelectorAll('.mk-cells')) for (const row of table.rows) [...row.cells].forEach((cell, i) => {
      for (const node of texts(cell)) {
        if (i === 0) { for (const m of node.data.matchAll(/\\S+/g)) if (lines(node, m.index, m.index + m[0].length) > 1) bad.push('place word "' + m[0] + '"'); }
        else if (node.data.trim() && lines(node, 0, node.data.length) > 1) bad.push('number "' + node.data.trim() + '"');
      }
    });
    return bad; })()`);
  const pastEdge = () => evaluate(`[...document.querySelectorAll('[data-testid=marks-panel] *')].filter(e => e.getBoundingClientRect().right > innerWidth + 1 && e.children.length === 0)
    .slice(0, 5).map(e => (e.className || e.tagName) + ':' + (e.textContent || '').slice(0, 30))`);
  const check = (ok, why) => { if (!ok) throw new Error(why); };

  const states = [
    ['gap4', reach('broken', 'wrap', 'Sales Amount', 'Trusted number'), async () => {
      const bad = await splitWords(); check(bad.length === 0, 'a word or number is split across lines: ' + bad.join(', '));
      check(/Total Cost: All/.test(await text('mk-card-Trusted number')), 'the place is not shown'); }],
    ['gap4eq', reach('edit', 'none', 'Total Quantity', 'Equivalence'), async () => {
      const bad = await splitWords(); check(bad.length === 0, 'a word or number is split across lines: ' + bad.join(', ')); }],
    ['gap5', reach('sum', 'timing', 'Total Cost'), async () => {
      const line = await text('mk-timing');
      check(line === 'Timing. About 2 seconds.', `the timing line reads "${line}"`); }],
    ['gap6', reach('sum', 'perf', 'Total Cost', 'Performance'), async () => {
      const heads = await evaluate(`[...${tid('mk-card-Performance')}.querySelectorAll('table')].map(t => [...t.querySelectorAll('th')].map(th => th.textContent.trim()).join(' / '))`);
      check(heads.length === 1 && heads[0] === 'Where / Median / Spread / Cold', 'the Performance detail shows ' + heads.length + ' tables: ' + heads.join(' | '));
      const bad = await splitWords(); check(bad.length === 0, 'a word or number is split across lines: ' + bad.join(', ')); }],
    ['gap9', reach('sum', 'keep', 'ShowValueForDates'), async () => {
      await press('Draft a Description');
      await wait(`!!${tid('mk-proposal')}`, 'the drafted Description');
      await press('Keep this one');
      await wait(`/Kept\\./.test(${tid('mk-description')}.innerText)`, 'the Kept note');
      await delay(300);
      const card = await text('mk-description');
      check(!/No Description yet/.test(card), 'the card reads "' + card + '" right after Keep');
      check(/Shows the value only for dates/.test(card), 'the kept text is not shown: "' + card + '"'); }],
  ].filter(([name]) => !only || only.test(name));

  for (const theme of ['light', 'dark']) for (const [name, go, assertState] of states) for (const width of [1440, 290]) {
    await size(width, width === 1440 ? 1500 : 900);
    let failed = null;
    try { await go(width); await applyTheme(theme); await vscodeLinux(); await evaluate('document.fonts.ready'); await delay(150); await assertState(); }
    catch (error) { failed = String(error?.message || error); }
    if (!failed) { const wide = await pastEdge(); if (wide.length) failed = 'past the right edge: ' + wide.join(', '); }
    await applyTheme(theme); await vscodeLinux(); await delay(100);
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    const file = `${name}-${phase}-${width}-${theme}.png`;
    writeFileSync(join(out, file), Buffer.from(shot.data, 'base64'));
    shots.push(file);
    if (failed) failures.push(`${name} ${width} ${theme}: ${failed}`);
    console.log(`${failed ? 'FAIL' : 'ok  '} ${name} ${width} ${theme}${failed ? ': ' + failed : ''}`);
  }
  if (errors.length) failures.push('page errors: ' + errors.join(' | '));
  writeFileSync(join(out, `fx29-card-${phase}.json`), JSON.stringify({ shots, failures, errors }, null, 2));
  console.log(`${shots.length} shots, ${failures.length} failures`);
  if (failures.length) process.exitCode = 1;
} finally {
  if (closeBrowser) await Promise.race([closeBrowser().catch(() => {}), delay(2000)]);
  socket?.close();
  if (browser.exitCode === null && browser.signalCode === null) browser.kill('SIGTERM');
  server.closeAllConnections();
  await new Promise(r => server.close(r));
}
