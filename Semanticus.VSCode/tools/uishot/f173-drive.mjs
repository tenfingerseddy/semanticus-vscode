// f173 (copied from checks-d1-drive.mjs): a Totals Jev typed but did not judge, before (rc2) and after (rc3). The 1.7 Checks screens after the d1 design pass, in headless Chromium with a mocked engine (checks-d1-fixture.js): the
// Studio Checks page at 1440, Properties at a 290 px sidebar, and a stand-in for the Model tree row built from the real
// row strings (the tree is native VS Code). The d1 mockup scenarios (simple SUM, a Filters finding, after an edit) and
// the tester's gaps (Broken, not checked yet, the run moment) are each shot light and dark. Words at rest are counted.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
import { THEMES } from './themes.mjs';

const here = dirname(fileURLToPath(import.meta.url)), root = resolve(here, '../..');
const out = resolve(process.env.OUTDIR || join(here, 'shots/checks-d1'));
mkdirSync(out, { recursive: true });
const fixture = readFileSync(join(here, 'checks-d1-fixture.js'), 'utf8');
const only = process.env.D1_ONLY ? new RegExp(process.env.D1_ONLY) : null;
const { treeRowItem } = createRequire(import.meta.url)(join(root, 'out/marksModel.js'));
const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;');
const words = s => (String(s).match(/[A-Za-z0-9][\w'%.,:-]*/g) || []).length;

// The tree stand-in for one scenario: the native row is an icon, the label, then the description (the verdict glyph).
const treePage = (sc, hover) => {
  const sandbox = { window: {}, location: { search: '?sc=' + sc }, URLSearchParams, Date, setTimeout, Promise };
  vm.runInNewContext(fixture, sandbox);
  const engine = sandbox.window.marksEngine;
  const page = engine.call('getChecksPage', []), cache = engine.call('getMeasureCheckCache', []);
  const rows = page.measures.map(m => { const c = cache.find(x => x.measureRef === m.ref);
    return { name: m.name, ...treeRowItem(m.name, c?.marks, { oneLine: m.oneLine, match: m.descriptionMatch, checked: !!c?.lastChecked }) }; });
  const tip = rows.find(r => r.name === hover);
  return { rows, html: `<!doctype html><html><head><meta charset="utf-8"><title>Model tree</title><style>
body { margin: 0; font: 13px var(--vscode-font-family, system-ui); background: var(--vscode-editor-background); color: var(--vscode-foreground); }
.side { width: 290px; border-right: 1px solid var(--vscode-panel-border, #8884); min-height: 100vh; box-sizing: border-box; }
.title { padding: 8px 20px; font-size: 11px; font-weight: 600; letter-spacing: .04em; }
.row { display: flex; align-items: center; height: 22px; padding-left: var(--indent, 20px); white-space: nowrap; overflow: hidden; }
.row.sel { background: color-mix(in srgb, var(--vscode-foreground) 12%, transparent); }
.icon { width: 16px; flex: none; margin-right: 6px; text-align: center; opacity: .8; }
.desc { margin-left: 6px; opacity: .75; font-size: 12px; }
.tip { position: absolute; left: 230px; width: 340px; white-space: pre-wrap; font-size: 12px; line-height: 18px; background: var(--vscode-editor-background);
  border: 1px solid var(--vscode-focusBorder, #999); padding: 8px 10px; box-shadow: 0 2px 8px #0004; }
</style></head><body><div class="side"><div class="title">SEMANTICUS</div><div class="row" style="--indent:8px"><span class="icon">▾</span>Model</div>
<div class="row" style="--indent:20px"><span class="icon">▾</span>Sales</div>
${rows.map((r, i) => `<div class="row${r.name === hover ? ' sel' : ''}" style="--indent:44px" data-row="${esc(r.name)}"><span class="icon">∑</span><span>${esc(r.name)}</span><span class="desc">${esc(r.description)}</span></div>`).join('\n')}
</div>${tip ? `<div class="tip" data-testid="tree-tip" style="top:${88 + rows.indexOf(tip) * 22 + 26}px">${esc(tip.tooltip)}</div>` : ''}</body></html>` };
};
let tree = '';
const server = createServer((req, res) => {
  const path = decodeURIComponent((req.url || '/').split('?')[0]);
  if (path === '/tree.html') { res.setHeader('content-type', 'text/html'); res.end(tree); return; }
  const file = resolve(root, '.' + path);
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
const errors = [], shots = [], counts = [];
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
  const press = (label, scope = 'document') => evaluate(`(() => { const b = [...${scope}.querySelectorAll('button')].find(b => b.textContent.trim() === ${JSON.stringify(label)} && !b.disabled); if (!b) throw new Error('Missing button: ' + ${JSON.stringify(label)}); b.click(); })()`);
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
        for (const m of ['getChecksPage', 'getMeasureCheckCache', 'getMeasureMarks', 'getMeasureDescription',
          'planMeasureRun', 'runMeasurePlan', 'stopMeasureRun', 'keepMeasureEdit', 'listSqlSources', 'cleanupMeaningAnnotations'])
          h.setResponse(m, params => window.marksEngine.call(m, Array.isArray(params) ? params : [params]));
      } });
    })();` });
  const applyTheme = name => evaluate(`(() => { const t = ${JSON.stringify(THEMES[name])}; for (const [k, v] of Object.entries(t.vars)) document.documentElement.style.setProperty(k, v);
    document.body.classList.remove('vscode-dark', 'vscode-light'); document.body.classList.add(t.bodyClass); document.body.dataset.vscodeThemeId = t.label;
    document.body.style.background = t.vars['--vscode-editor-background']; document.body.style.color = t.vars['--vscode-foreground']; })()`);
  const size = (width, height = 900) => send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
  const ref = n => 'measure:Sales/' + n;

  // Properties for one measure, as the provider loads it.
  const P = (name, displayName, category, value) => ({ name, displayName, category, kind: 'string', value, description: displayName, options: [], readOnly: false, varies: false });
  const grid = async (sc, n, mark) => {
    await send('Page.navigate', { url: `${base}/tools/uishot/propgrid.html?s=empty&sc=${sc}` });
    await wait("document.readyState === 'complete' && !!window.semanticusGrid");
    const formula = await evaluate(`window.marksEngine.formulas[${JSON.stringify(n)}] || ''`);
    const props = [P('Name', 'Name', 'Basic', n), P('Expression', 'Expression', 'Advanced', formula), P('FormatString', 'Format String', 'Formatting', '#,0')];
    await evaluate(`window.dispatchEvent(new MessageEvent('message', { data: ${JSON.stringify({ type: 'load', key: JSON.stringify([ref(n)]), model: 'Contoso.pbip', name: n, multi: false, props })} }))`);
    await wait(`!!${tid('mk-verdict')} && !/Reading/.test(${tid('mk-verdict')}.textContent)`, 'the card verdict');
    await delay(150);
    if (mark) await openMark(mark);
  };
  const openMark = async mark => { await evaluate(`${tid('mk-row-' + mark)}.click()`); await wait(`!!${tid('mk-card-' + mark)}`, 'the ' + mark + ' detail'); };
  // The Studio Checks page.
  const checks = async sc => {
    await send('Page.navigate', { url: `${base}/tools/uishot/harness.html?conn=1&tier=pro&sc=${sc}` });
    await wait("[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Review')");
    await press('Review'); await press('Checks');
    await wait(`!!${tid('checks-measures')} && !!${tid('plan-sentence')} && !/Making/.test(${tid('plan-sentence')}.textContent)`, 'the Checks page');
    await delay(150);
  };
  const openRow = async (n, mark) => {
    await evaluate(`document.querySelector('[data-testid="checks-row-${ref(n)}"] button').click()`);
    await wait(`!!${tid('marks-panel')} && !!${tid('mk-verdict')} && !/Reading/.test(${tid('mk-verdict')}.textContent)`, n + ' card');
    await delay(150);
    if (mark) await openMark(mark);
    await evaluate(`document.querySelector('[data-testid="checks-row-${ref(n)}"]').scrollIntoView({ block: 'start' })`);
  };
  const rowGlyph = n => evaluate(`[...document.querySelectorAll('.cp-row')].find(r => r.textContent.includes(${JSON.stringify(n)}))?.querySelector('.cp-glyph')?.textContent`);
  const changed = () => evaluate(`window.dispatchEvent(new MessageEvent('message', { data: { type: 'didChange', payload: { sessionId: 'uishot', revision: 8, origin: 'user', label: 'edit', deltas: [] } } }))`);
  const keepThenUsed = async on => { await checks('edit'); await openRow('Total Quantity', 'Equivalence');
    await press('Keep'); await wait(`/^✓\\s*Verified by 3 checks/.test(${tid('mk-verdict')}.innerText.trim())`, 'the kept edit');
    await evaluate(`window.marksEngine.editUsed(${on})`); await changed(); };
  const atRest = async () => ({ verdict: await text('mk-verdict'), card: words(await text('marks-panel')) });

  // f173: SC=f173 (rc3) or SC=f173rc2 (rc2 engine data, shot with the rc2 webview build). Both widths, both measures.
  const SC = process.env.SC || 'f173';
  const totals = async () => { if (await evaluate(`!!${tid('mk-row-Totals')}`)) await openMark('Totals'); };
  const tag = SC === 'f173' ? 'after' : 'before';
  const states = [];
  for (const [key, n] of [['distinct', '# Customers'], ['ratio', 'Margin %']]) for (const width of [1280, 390]) {
    states.push([`${tag}-${key}-checks-${width}`, width, async () => { await checks(SC); await openRow(n); await totals();
      counts.push({ state: `${tag}-${key}-checks-${width}`, verdict: await text('mk-verdict'), marks: await text('mk-list'), header: await text('checks-verdict'),
        detail: await evaluate(`${tid('mk-card-Totals')}?.querySelector('.mk-say')?.innerText || ''`) }); }]);
    states.push([`${tag}-${key}-props-${width}`, width, async () => { await grid(SC, n); await totals();
      counts.push({ state: `${tag}-${key}-props-${width}`, verdict: await text('mk-verdict'), marks: await text('mk-list') }); }]);
  }
  for (const [key, n] of [['distinct', '# Customers'], ['ratio', 'Margin %']])
    states.push([`${tag}-${key}-tree`, 290, async () => { const t = treePage(SC, n); tree = t.html;
      counts.push({ state: `${tag}-${key}-tree`, tooltip: t.rows.find(r => r.name === n).tooltip });
      await send('Page.navigate', { url: base + '/tree.html' }); await wait("!!document.querySelector('.row')"); }]);
  states.splice(0, states.length, ...states.filter(([name]) => !only || only.test(name)));

  for (const theme of ['light', 'dark']) for (const [name, width, reach] of states) {
    // Studio scrolls inside its own frame, so the Checks page is shot in a tall window to show the header and the card.
    await size(name.endsWith('-tree') ? 640 : width, name.endsWith('-tree') ? 460 : name.includes('-checks') ? 1500 : 900);
    await reach();
    await applyTheme(theme);
    await evaluate('document.fonts.ready'); await delay(150);
    const m = await evaluate(`({ w: document.documentElement.clientWidth, h: Math.max(document.documentElement.scrollHeight, 400),
      wide: [...document.querySelectorAll(${JSON.stringify(name.includes('-checks') ? '[data-testid=checks-page] *' : name.includes('-props') ? '[data-testid=marks-panel] *' : '.side *')})]
        .filter(e => e.getBoundingClientRect().right > innerWidth + 1 && e.children.length === 0).slice(0, 5).map(e => (e.className || e.tagName) + ':' + (e.textContent || '').slice(0, 30)) })`);
    assert.deepEqual(m.wide, [], `${name} ${theme}: nothing past the right edge`);
    if (name.includes('-props')) await size(width, Math.min(m.h, 3000));
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    const file = `${name}-${theme}.png`;
    writeFileSync(join(out, file), Buffer.from(shot.data, 'base64'));
    shots.push(file);
  }
  assert.deepEqual(errors, [], 'no page errors');
  writeFileSync(join(out, 'results.json'), JSON.stringify({ shots, counts, errors }, null, 2));
  console.log(JSON.stringify({ out, shots: shots.length, counts }, null, 1));
} catch (error) {
  writeFileSync(join(out, 'failure.json'), JSON.stringify({ error: String(error?.stack || error), errors, shots, counts }, null, 2));
  throw error;
} finally {
  if (closeBrowser) await Promise.race([closeBrowser().catch(() => {}), delay(2000)]);
  socket?.close();
  if (browser.exitCode === null && browser.signalCode === null) browser.kill('SIGTERM');
  server.closeAllConnections();
  await new Promise(r => server.close(r));
}
