// n171 (rc34 and rc35 tester Next lists): the three layout faults, rendered in headless Chromium with the mocked engine of
// checks-d1-fixture.js, and the check each must pass. ROOT names the extension folder to serve, so the same drive shoots the
// build before the fix and the build after it.
//   rc35 Next 1: Properties while the marks load after a restart (the reads have not answered): never "Not checked yet".
//   rc34 Next 2: the More menu of a trusted number row on the Checks page: Turn off and Delete can be pressed (not clipped).
//   rc34 Next 5: Properties on the model while a run another screen started is going on: no active Run.
// Exit code 1 when a check fails. Usage: ROOT=<Semanticus.VSCode> OUTDIR=<folder> LABEL=before|after node n171-drive.mjs
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { THEMES } from './themes.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(process.env.ROOT || resolve(here, '../..'));
const out = resolve(process.env.OUTDIR || join(here, 'shots/n171'));
const label = process.env.LABEL || 'after';
mkdirSync(out, { recursive: true });
const fixture = readFileSync(join(here, 'checks-d1-fixture.js'), 'utf8');
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
  '--disable-background-networking', '--remote-debugging-port=0', '--user-data-dir=' + join(out, 'browser-profile-' + label), 'about:blank',
], { stdio: ['ignore', 'ignore', 'pipe'] });
const delay = ms => new Promise(r => setTimeout(r, ms));
const results = [];
let socket;
try {
  const endpoint = await new Promise((resolve, reject) => {
    let buffer = '';
    const timer = setTimeout(() => reject(new Error('Chromium did not start.')), 15000);
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
    const call = pending.get(msg.id);
    if (call) { pending.delete(msg.id); msg.error ? call.reject(new Error(msg.error.message)) : call.resolve(msg.result); }
  };
  const send = (method, params = {}) => new Promise((resolve, reject) => { const seq = ++id; pending.set(seq, { resolve, reject }); socket.send(JSON.stringify({ id: seq, method, params })); });
  const evaluate = async expression => {
    const value = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (value.exceptionDetails) throw new Error(value.exceptionDetails.exception?.description || value.exceptionDetails.text);
    return value.result?.value;
  };
  const wait = async (expression, what = expression) => {
    for (let i = 0; i < 160; i++) { if (await evaluate(expression)) return; await delay(50); }
    throw new Error('The page did not reach: ' + what);
  };
  const tid = t => `document.querySelector('[data-testid="${t}"]')`;
  await send('Page.enable'); await send('Runtime.enable');
  // The fixture engine; `hold=1` in the address keeps the marks reads unanswered, as after a restart.
  await send('Page.addScriptToEvaluateOnNewDocument', { source: fixture + `
    (function () {
      const hold = /[?&]hold=1/.test(location.search);
      if (hold && window.marksEngine) { const call = window.marksEngine.call.bind(window.marksEngine);
        window.marksEngine.call = (m, a) => m === 'getMeasureMarks' || m === 'getMeasureCheckCache' ? new Promise(() => {}) : call(m, a); }
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
  const dark = THEMES.dark;
  const theme = () => evaluate(`(() => { const t = ${JSON.stringify(dark)}; for (const [k, v] of Object.entries(t.vars)) document.documentElement.style.setProperty(k, v);
    document.body.classList.remove('vscode-dark', 'vscode-light'); document.body.classList.add(t.bodyClass);
    document.body.style.background = t.vars['--vscode-editor-background']; document.body.style.color = t.vars['--vscode-foreground']; })()`);
  const size = (width, height = 900) => send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
  const shoot = async name => {
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    writeFileSync(join(out, `${name}-${label}.png`), Buffer.from(shot.data, 'base64'));
  };
  const check = (name, ok, detail) => { results.push({ name, ok, detail }); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}: ${detail}`); };
  const P = (name, displayName, category, value) => ({ name, displayName, category, kind: 'string', value, description: displayName, options: [], readOnly: false, varies: false });
  const grid = async (query, key, name) => {
    await send('Page.navigate', { url: `${base}/tools/uishot/propgrid.html?s=empty&${query}` });
    await wait("document.readyState === 'complete' && !!window.semanticusGrid");
    await evaluate(`window.dispatchEvent(new MessageEvent('message', { data: ${JSON.stringify({ type: 'load', key: JSON.stringify([key]), model: 'Contoso.pbip', name, multi: false, props: [P('Name', 'Name', 'Basic', name)] })} }))`);
    await theme();
  };

  // rc35 Next 1: the card while its marks load.
  await size(290);
  await grid('sc=sum&hold=1', 'measure:Sales/Gross Profit', 'Gross Profit');
  await wait(`!!${tid('mk-verdict')}`, 'the card');
  await delay(1500);
  const verdict = await evaluate(`${tid('mk-verdict')}.innerText.trim()`);
  check('rc35-1 card while the marks load', !/Not checked yet/.test(verdict), JSON.stringify(verdict));
  await shoot('rc35-1-card-loading-290-dark');

  // rc34 Next 2: the trusted number row's More menu on the Checks page.
  await size(1440);
  await send('Page.navigate', { url: `${base}/tools/uishot/harness.html?conn=1&tier=pro&sc=broken` });
  await wait("[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Review')");
  await theme();
  await evaluate("[...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Review').click()");
  await evaluate("[...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Checks').click()");
  await wait(`!!document.querySelector('[data-testid="checks-row-number:sales-all"] button')`, 'the trusted number row');
  await evaluate(`document.querySelector('[data-testid="checks-row-number:sales-all"] button').click()`);
  await wait(`!!document.querySelector('[aria-label="More for Sales Amount, all rows"]')`, 'the row More');
  await evaluate(`document.querySelector('[data-testid="checks-row-number:sales-all"]').scrollIntoView({ block: 'center' })`);
  await evaluate(`document.querySelector('[aria-label="More for Sales Amount, all rows"]').click()`);
  await wait(`!!document.querySelector('[role="menu"][aria-label="More for Sales Amount, all rows"]')`, 'the open menu');
  // A person scrolls to a menu that opens below the fold; a clipped item stays hidden however far they scroll.
  // Only a scroll bar the person has moves: the page's own scroll area, never the list (a script can scroll a clipping box).
  await evaluate(`(() => { const menu = document.querySelector('[role="menu"][aria-label="More for Sales Amount, all rows"]');
    let box = menu.parentElement; while (box && !/(auto|scroll)/.test(getComputedStyle(box).overflowY)) box = box.parentElement;
    const scroller = box || document.scrollingElement; const r = menu.getBoundingClientRect();
    const view = box ? box.getBoundingClientRect().bottom : innerHeight; scroller.scrollTop += Math.max(0, r.bottom - view + 24); })()`);
  await delay(200);
  const pressable = await evaluate(`(() => {
    const menu = document.querySelector('[role="menu"][aria-label="More for Sales Amount, all rows"]');
    return [...menu.querySelectorAll('[role="menuitem"]')].map(item => { const r = item.getBoundingClientRect();
      const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
      return { item: item.textContent.trim(), pressable: !!hit && (hit === item || item.contains(hit)) }; }); })()`);
  check('rc34-2 every item of the row More menu can be pressed', pressable.length >= 3 && pressable.every(p => p.pressable), JSON.stringify(pressable));
  await shoot('rc34-2-trusted-more-menu-1440-dark');

  // rc34 Next 5: the model's run plan in Properties while a run another screen started is going on.
  await size(290);
  await grid('sc=sum', 'model:', 'Contoso');
  await wait(`!!${tid('plan-sentence')} && !/Making/.test(${tid('plan-sentence')}.textContent)`, 'the model run plan');
  await evaluate(`window.dispatchEvent(new MessageEvent('message', { data: { type: 'checkState', payload: { sessionId: 'Contoso', version: 90, states: [], run: { active: true, done: 5, total: 12 } } } }))`);
  await delay(400);
  const plan = await evaluate(`(() => { const run = [...document.querySelectorAll('[data-testid="run-plan"] button')].find(b => b.textContent.trim() === 'Run');
    return { line: ${tid('plan-sentence')}.innerText.trim(), run: run ? (run.disabled ? 'disabled' : 'active') : 'none' }; })()`);
  check('rc34-5 no active Run in Properties during a run', plan.run !== 'active', JSON.stringify(plan));
  await shoot('rc34-5-model-plan-during-run-290-dark');
} finally {
  socket?.close();
  browser.kill();
  server.close();
}
writeFileSync(join(out, `results-${label}.json`), JSON.stringify(results, null, 2));
process.exit(results.every(r => r.ok) ? 0 : 1);
