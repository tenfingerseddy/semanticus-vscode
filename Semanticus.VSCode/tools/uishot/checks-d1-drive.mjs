// The 1.7 Checks screens after the d1 design pass, in headless Chromium with a mocked engine (checks-d1-fixture.js): the
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

  // Each state: the surface, how to reach it, and what it must show.
  const states = [
    // The three mockup scenarios.
    ['01-sum-checks', 1440, async () => { await checks('sum'); await openRow('Total Quantity');
      const head = await text('checks-verdict'), plan = await text('plan-sentence'), card = await atRest();
      assert.match(card.verdict, /^✓ Verified by 3 checks/); assert.ok(card.card <= 30, 'card ' + card.card);
      assert.ok(words(head) <= 12, head); assert.ok(words(plan) <= 15, plan);
      counts.push({ state: '01-sum', header: head, headerWords: words(head), plan, planWords: words(plan), ...card }); }],
    ['01-sum-props', 290, async () => { await grid('sum', 'Total Quantity'); counts.push({ state: '01-sum props', ...await atRest() }); }],
    ['01-sum-tree', 290, async () => { tree = treePage('sum', 'Total Quantity').html; await send('Page.navigate', { url: base + '/tree.html' }); await wait("!!document.querySelector('.row')"); }],
    ['02-filters-checks', 1440, async () => { await checks('sum'); await openRow('Date New Customer');
      const card = await atRest(); await openMark('Filters'); assert.match(card.verdict, /^▲/); counts.push({ state: '02-filters', ...card, detail: words(await text('mk-card-Filters')) }); }],
    ['02-filters-props', 290, async () => { await grid('sum', 'Date New Customer', 'Filters'); }],
    ['02-filters-tree', 290, async () => { tree = treePage('sum', 'Date New Customer').html; await send('Page.navigate', { url: base + '/tree.html' }); await wait("!!document.querySelector('.row')"); }],
    ['03-edit-checks', 1440, async () => { await checks('edit'); await openRow('Total Quantity');
      const card = await atRest(); assert.match(card.verdict, /edit/i); counts.push({ state: '03-edit', ...card });
      await openMark('Equivalence'); assert.match(await text('mk-edit-diff'), /\* 2/); }],
    // Keep on an edit: the engine holds it (keepMeasureEdit) and pushes the state; the Equivalence mark stops applying (spec 7.5).
    ['03-kept-checks', 1440, async () => { await checks('edit'); await openRow('Total Quantity', 'Equivalence');
      await press('Keep'); await wait(`/^✓\\s*Verified by 3 checks/.test(${tid('mk-verdict')}.innerText.trim())`, 'the kept edit');
      assert.equal(await evaluate("Object.keys(window.marksEngine.kept).length"), 1);
      // v-fx23: one verdict for one measure. After Keep the open card reads Verified; its own row and the header must agree.
      await delay(1000);
      const row = await evaluate(`[...document.querySelectorAll('.cp-row')].find(r => /Total Quantity/.test(r.textContent))?.querySelector('.cp-glyph')?.textContent`);
      assert.equal(row, '✓', 'the Total Quantity row keeps ▲ under a card that reads ✓ Verified');
      assert.doesNotMatch(await text('checks-verdict'), /2 need review/, 'the header still counts the kept edit'); }],
    // v-fx24 N1: Keep ends when a measure it uses changes (the moved numbers show again), and Undo brings it back.
    ['13-kept-used-checks', 1440, async () => { await keepThenUsed(true);
      await wait(`/^▲\\s*Needs review/.test(${tid('mk-verdict')}.innerText.trim())`, 'the Keep ends');
      await delay(300); assert.equal(await rowGlyph('Total Quantity'), '▲', 'the row hides the moved numbers');
      counts.push({ state: '13-kept-used', verdict: await text('mk-verdict'), header: await text('checks-verdict') }); }],
    ['13-kept-undo-checks', 1440, async () => { await keepThenUsed(true);
      await wait(`/^▲\\s*Needs review/.test(${tid('mk-verdict')}.innerText.trim())`, 'the Keep ends');
      await evaluate('window.marksEngine.editUsed(false)'); await changed();
      await wait(`/^✓\\s*Verified by 3 checks/.test(${tid('mk-verdict')}.innerText.trim())`, 'Undo brings the Keep back');
      await delay(300); assert.equal(await rowGlyph('Total Quantity'), '✓'); }],
    ['03-edit-props', 290, async () => { await grid('edit', 'Total Quantity', 'Equivalence'); }],
    ['03-edit-tree', 290, async () => { tree = treePage('edit', 'Total Quantity').html; await send('Page.navigate', { url: base + '/tree.html' }); await wait("!!document.querySelector('.row')"); }],
    // HEAD's gaps. 1: Broken, red, with its reason and fix.
    ['04-broken-checks', 1440, async () => { await checks('broken'); await openRow('Sales Amount');
      const card = await atRest(); await openMark('Trusted number'); assert.match(card.verdict, /^✗ Broken/); assert.match(await text('mk-card-Trusted number'), /Fix the formula/);
      counts.push({ state: '04-broken', ...card, detail: words(await text('mk-card-Trusted number')) }); }],
    ['04-broken-props', 290, async () => { await grid('broken', 'Sales Amount', 'Trusted number'); }],
    ['04-broken-tree', 290, async () => { tree = treePage('broken', 'Sales Amount').html; await send('Page.navigate', { url: base + '/tree.html' }); await wait("!!document.querySelector('.row')"); }],
    // fx27: a number you trust that holds says it in the measure's format, as its table cell does.
    ['04-trusted-checks', 1440, async () => { await checks('trusted'); await openRow('Sales Amount');
      const card = await atRest(); await openMark('Trusted number');
      assert.match(await text('mk-card-Trusted number'), /Same as your number: \$628,370\.00\./);
      counts.push({ state: '04-trusted', ...card, detail: words(await text('mk-card-Trusted number')) }); }],
    ['04-trusted-props', 290, async () => { await grid('trusted', 'Sales Amount', 'Trusted number');
      assert.match(await text('mk-card-Trusted number'), /Same as your number: \$628,370\.00\./); }],
    // fx28: a gap between numbers of different signs reads in the measure's format ($1,005.00, not 1,005).
    ['04-gap-checks', 1440, async () => { await checks('gap'); await openRow('Sales Amount');
      const card = await atRest(); await openMark('Trusted number'); assert.match(card.verdict, /^✗ Broken: \$1,005\.00 lower than your number/);
      assert.match(await text('mk-card-Trusted number'), /\$1,005\.00 lower than your number\./);
      counts.push({ state: '04-gap', ...card, detail: words(await text('mk-card-Trusted number')) }); }],
    ['04-gap-props', 290, async () => { await grid('gap', 'Sales Amount', 'Trusted number');
      assert.match(await text('mk-card-Trusted number'), /\$1,005\.00 lower than your number\./); }],
    // 2: never in a run: the group opens, the card and the tree say why; the plan never claims 12 of 12 beside them.
    ['05-unchecked-checks', 1440, async () => { await checks('unchecked');
      await evaluate(`${tid('group-unchecked')}.querySelector('button').click()`); await wait(`${tid('group-unchecked')}.querySelectorAll('.cp-row').length === 2`, 'the group opens');
      await openRow('Quantity PY');
      const plan = await text('plan-sentence'), card = await atRest();
      assert.match(card.verdict, /^Not checked yet: /); assert.doesNotMatch(plan, /12 of 12/);
      counts.push({ state: '05-unchecked', plan, ...card }); }],
    ['05-unchecked-props', 290, async () => { await grid('unchecked', 'Quantity PY'); }],
    ['05-unchecked-tree', 290, async () => { const t = treePage('unchecked', 'Quantity PY'); tree = t.html;
      assert.match(t.rows.find(r => r.name === 'Quantity PY').tooltip, /^Not checked yet: no run has included it/);
      await send('Page.navigate', { url: base + '/tree.html' }); await wait("!!document.querySelector('.row')"); }],
    // 2 (after a run): nothing that applies could run, so it is Unknown with the reason, not "not checked".
    ['06-unknown-checks', 1440, async () => { await checks('sum'); await openRow('Quantity PY');
      const card = await atRest(); assert.match(card.verdict, /^○ Unknown: /); counts.push({ state: '06-unknown', ...card }); }],
    ['06-unknown-tree', 290, async () => { tree = treePage('sum', 'ShowValueForDates').html; await send('Page.navigate', { url: base + '/tree.html' }); await wait("!!document.querySelector('.row')"); }],
    // 3: the run moment.
    ['07-running-checks', 1440, async () => { await checks('running'); await press('Run');
      await evaluate('window.marksEngine.pushDone(5)'); await wait(`/Checking 5 of 12/.test(${tid('plan-sentence')}.textContent)`, 'the running line');
      assert.ok(await evaluate(`!!${tid('plan-stop')}`), 'Stop shows while the run runs');
      counts.push({ state: '07-running', plan: await text('plan-sentence') }); }],
    // ra1 item 25: Stop on the run line. The run ends at once; the rows it checked keep their marks.
    ['14-stop-checks', 1440, async () => { await checks('running'); await press('Run');
      await evaluate('window.marksEngine.pushDone(5)'); await wait(`/Checking 5 of 12/.test(${tid('plan-sentence')}.textContent)`, 'the running line');
      await press('Stop');
      await wait(`!/Checking 5 of 12\./.test(${tid('plan-sentence')}.textContent) && !${tid('plan-stop')}`, 'the run line at rest');
      // The five checked rows keep their marks: Date New Customer leads the header (R1), the other four read ✓.
      assert.match(await text('checks-verdict'), /^1 needs review/, 'the checked rows keep their marks');
      assert.equal(await rowGlyph('# Customers'), '✓');
      counts.push({ state: '14-stop', header: await text('checks-verdict'), plan: await text('plan-sentence'), note: await text('plan-note') }); }],
    ['08-finished-checks', 1440, async () => { await checks('running'); const before = await text('checks-verdict');
      await press('Run'); await evaluate('window.marksEngine.pushDone(5)');
      await wait(`/Checking 5 of 12\\./.test(${tid('plan-sentence')}.textContent)`, 'the running line'); await evaluate('window.marksEngine.release()');
      // Spec 4.2 step 4: when the run ends, the header verdict line changes. No "Checked 12 measures" note (R1).
      // fx24 (D5): the header names only what needs action when something does, so "verified" is not always in it.
      await wait(`${tid('checks-verdict')}.textContent !== ${JSON.stringify(before)} && !/Checking 5/.test(${tid('plan-sentence')}.textContent)`, 'the finished state');
      const after = await text('checks-verdict'); assert.notEqual(after, before);
      await openRow('Total Quantity');
      counts.push({ state: '08-finished', before, header: after, plan: await text('plan-sentence'), note: await text('plan-note') }); }],
    ['07-running-props', 290, async () => { await grid('running', 'Total Quantity'); }],
    // 4: one value per place. The Totals detail shows the same grand total as Filters.
    ['09-totals-checks', 1440, async () => { await checks('sum'); await openRow('Total Quantity', 'Totals');
      const totals = await text('mk-card-Totals'); assert.match(totals, /628,370/); assert.doesNotMatch(totals, /628370/); }],
    ['09-totals-date-checks', 1440, async () => { await checks('sum'); await openRow('Date New Customer', 'Totals');
      const totals = await text('mk-card-Totals'); assert.match(totals, /3 Jan 2018/); assert.doesNotMatch(totals, /2018-01-03/); }],
    // 5: the score says what it compares. Behind the click: Choose and the full Description.
    ['10-choose-checks', 1440, async () => { await checks('sum'); await evaluate(`${tid('plan-choose')}.click()`); await wait(`!!${tid('plan-choose-panel')}`);
      counts.push({ state: '10-choose', choose: words(await text('plan-choose-panel')) }); }],
    // fx24 D4: Check now on a Broken measure. The marks the run has not reached keep their last state, with a checking cue.
    ['12-checking-checks', 1440, async () => { await checks('broken'); await openRow('Sales Amount'); await press('Check now');
      await wait(`/^Checking now/.test(${tid('mk-verdict')}.innerText.trim())`, 'the re-check');
      assert.match(await evaluate(`${tid('mk-row-Trusted number')}.className`), /mk-s-broken mk-checking|mk-s-broken.*mk-checking/, 'Trusted number keeps ✗ while it waits');
      const row = await evaluate(`[...document.querySelectorAll('.cp-row')].find(r => /Sales Amount/.test(r.textContent))?.querySelector('.cp-glyph')?.textContent`);
      assert.equal(row, '✗', 'the row keeps its glyph during the run');
      counts.push({ state: '12-checking', verdict: await text('mk-verdict'), marks: await text('mk-list') }); }],
    ['12-checking-props', 290, async () => { await grid('broken', 'Sales Amount'); await press('Check now');
      await wait(`/^Checking now/.test(${tid('mk-verdict')}.innerText.trim())`, 'the re-check');
      assert.match(await evaluate(`${tid('mk-row-Trusted number')}.className`), /mk-checking/); }],
    ['11-description-props', 290, async () => { await grid('sum', 'Total Quantity'); await evaluate(`${tid('mk-desc-line')}.click()`); await delay(150);
      assert.match(await text('mk-description'), /Matches, 94%/); }],
  ].filter(([name]) => !only || only.test(name));

  for (const theme of ['light', 'dark']) for (const [name, width, reach] of states) {
    // Studio scrolls inside its own frame, so the Checks page is shot in a tall window to show the header and the card.
    await size(name.endsWith('-tree') ? 640 : width, name.endsWith('-tree') ? 460 : name.endsWith('-checks') ? 1500 : 900);
    await reach();
    await applyTheme(theme);
    await evaluate('document.fonts.ready'); await delay(150);
    const m = await evaluate(`({ w: document.documentElement.clientWidth, h: Math.max(document.documentElement.scrollHeight, 400),
      wide: [...document.querySelectorAll(${JSON.stringify(name.endsWith('-checks') ? '[data-testid=checks-page] *' : name.endsWith('-props') ? '[data-testid=marks-panel] *' : '.side *')})]
        .filter(e => e.getBoundingClientRect().right > innerWidth + 1 && e.children.length === 0).slice(0, 5).map(e => (e.className || e.tagName) + ':' + (e.textContent || '').slice(0, 30)) })`);
    assert.deepEqual(m.wide, [], `${name} ${theme}: nothing past the right edge`);
    if (name.endsWith('-props')) await size(width, Math.min(m.h, 3000));
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    const file = `${name}-${name.endsWith('-tree') ? 290 : width}-${theme}.png`;
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
