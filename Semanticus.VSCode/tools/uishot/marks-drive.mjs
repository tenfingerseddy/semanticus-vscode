// The marks screens in headless Chromium with a mocked engine (marks-fixture.js): Properties for a measure and for the
// model (run plan), the mark cards, the Checks page, and a stand-in for the Model tree row built from the real row
// strings (the tree is native VS Code and cannot be captured). Every state is shot light and dark, at 1280 and at a
// 300 px sidebar. Then every button in every state is pressed and must show a visible result within two seconds.
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
const out = resolve(process.env.OUTDIR || join(here, 'shots/marks'));
mkdirSync(out, { recursive: true });
const fixture = readFileSync(join(here, 'marks-fixture.js'), 'utf8');
const only = process.env.MARKS_ONLY ? new RegExp(process.env.MARKS_ONLY) : null;
const server = createServer((req, res) => {
  const path = decodeURIComponent((req.url || '/').split('?')[0]);
  if (path === '/tree-row.html') { res.setHeader('content-type', 'text/html'); res.end(treePage); return; }
  const file = resolve(root, '.' + path);
  if (!file.startsWith(root + '/') || !existsSync(file)) { res.writeHead(404); res.end(); return; }
  res.setHeader('content-type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json' })[extname(file)] || 'application/octet-stream');
  res.end(readFileSync(file));
});

// The tree row stand-in: the native row is an icon, the label, then the description that VS Code cuts with an ellipsis.
const { treeRowItem } = createRequire(import.meta.url)(join(root, 'out/marksModel.js'));
const sandbox = { window: {}, location: { search: '' }, URLSearchParams };
vm.runInNewContext(fixture, sandbox);
const fx = sandbox.window.marksEngine.measures;
const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;');
const infoIcon = '<svg data-testid="native-info-standin" viewBox="0 0 16 16" width="16" height="16" fill="none" aria-hidden="true"><circle cx="8" cy="8" r="6.25" stroke="currentColor" stroke-width="1.5"/><path d="M8 7v4" stroke="currentColor" stroke-width="1.5"/><circle cx="8" cy="4.5" r=".8" fill="currentColor"/></svg>';
const rows = Object.entries(fx).map(([ref, m]) => ({ ref, name: ref.split('/').slice(1).join('/'), table: ref.split(':')[1].split('/')[0],
  ...treeRowItem(ref.split('/').slice(1).join('/'), m.marks, { oneLine: m.description, match: m.match }) }));
const treePage = `<!doctype html><html><head><meta charset="utf-8"><title>Model tree row A</title><style>
body { margin: 0; font: 13px var(--vscode-font-family, system-ui); background: var(--vscode-editor-background, #fff); color: var(--vscode-foreground, #333); }
.side { width: var(--w, 300px); border-right: 1px solid var(--vscode-panel-border, #ddd); min-height: 100vh; box-sizing: border-box; }
.title { padding: 8px 20px; font-size: 11px; font-weight: 600; letter-spacing: .04em; }
.row { display: flex; align-items: center; height: 22px; padding-left: var(--indent, 20px); white-space: nowrap; overflow: hidden; cursor: default; }
.row:hover, .row.sel { background: color-mix(in srgb, var(--vscode-foreground, #333) 10%, transparent); }
.icon { width: 16px; flex: none; margin-right: 6px; text-align: center; opacity: .8; }
.label { flex: none; }
.desc { margin-left: 6px; opacity: .7; overflow: hidden; text-overflow: ellipsis; font-size: 12px; min-width: 0; }
.tip { position: absolute; left: calc(var(--w, 300px) - 60px); top: 120px; width: 360px; white-space: pre-wrap; font-size: 12px; line-height: 18px;
  background: var(--vscode-editor-background, #fff); border: 1px solid var(--vscode-focusBorder, #999); padding: 8px 10px; box-shadow: 0 2px 8px #0004; display: none; }
body.hover .tip { display: block; }
</style></head><body><div class="side"><div class="title">SEMANTICUS</div><div class="row" style="--indent:8px"><span class="icon">▾</span><span class="label">Model</span></div>
${[...new Set(rows.map(r => r.table))].map(t => `<div class="row" style="--indent:20px"><span class="icon">▾</span><span class="label">${esc(t)}</span></div>
${rows.filter(r => r.table === t).map((r, i) => `<div class="row${r.name === 'Sales Tailspin Toys' ? ' sel' : ''}" style="--indent:44px" data-row="${esc(r.name)}" data-state="${r.state}" title=""><span class="icon">${r.icon === 'info' ? infoIcon : '∑'}</span><span class="label">${esc(r.name)}</span><span class="desc">${esc(r.description)}</span></div>`).join('\n')}`).join('\n')}
</div><div class="tip" data-testid="tree-tip">${esc(rows.find(r => r.name === 'Sales Tailspin Toys').tooltip)}</div></body></html>`;

await new Promise(r => server.listen(0, '127.0.0.1', r));
const base = `http://127.0.0.1:${server.address().port}`;
const browser = spawn(process.env.SEMANTICUS_BROWSER || '/usr/bin/chromium', [
  '--headless=new', '--no-sandbox', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  '--disable-background-networking', '--remote-debugging-port=0', '--user-data-dir=' + join(out, 'browser-profile'), 'about:blank',
], { stdio: ['ignore', 'ignore', 'pipe'] });
const delay = ms => new Promise(r => setTimeout(r, ms));
const errors = [], shots = [], presses = [], checks = [];
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
    const exited = new Promise(resolve => {
      if (browser.exitCode !== null || browser.signalCode !== null) resolve();
      else browser.once('exit', resolve);
    });
    await send('Browser.close');
    await exited;
  };
  const evaluate = async expression => {
    const value = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (value.exceptionDetails) throw new Error(value.exceptionDetails.exception?.description || value.exceptionDetails.text);
    return value.result?.value;
  };
  const wait = async (expression, what = expression) => {
    for (let i = 0; i < 100; i++) { if (await evaluate(expression)) return; await delay(50); }
    throw new Error('The page did not reach: ' + what);
  };
  const press = (label, scope = 'document') => evaluate(`(() => { const b = [...${scope}.querySelectorAll('button')].find(b => b.textContent.trim() === ${JSON.stringify(label)} && !b.disabled); if (!b) throw new Error('Missing button: ' + ${JSON.stringify(label)}); b.click(); })()`);
  const tid = t => `document.querySelector('[data-testid="${t}"]')`;
  await send('Page.enable'); await send('Runtime.enable');
  await send('Page.addScriptToEvaluateOnNewDocument', { source: fixture + `
    (function () {
      Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => window.__h, set: h => {
        window.__h = h;
        h.setResponse('getOrientation', { activeWork: { workflows: [] } });
        h.setResponse('getWorkflowRun', null);
        h.setResponse('listTests', { definitions: [] });
        h.setResponse('listTableMappings', { rows: [] });
        for (const m of ['getChecksPage', 'getMeasureCheckCache', 'getMeasureMarks', 'getMeasureDescription', 'setDescription', 'draftMeasureDescription',
          'proposeMeasureDescription', 'keepMeasureDescription', 'planMeasureRun', 'runMeasurePlan', 'setMeasurePerformance', 'setMeasureTotals', 'listSqlSources', 'saveTest', 'cleanupMeaningAnnotations'])
          h.setResponse(m, params => window.marksEngine.call(m, params));
      } });
    })();` });

  const applyTheme = name => evaluate(`(() => { const t = ${JSON.stringify(THEMES[name])}; for (const [k, v] of Object.entries(t.vars)) document.documentElement.style.setProperty(k, v);
    document.body.classList.remove('vscode-dark', 'vscode-light'); document.body.classList.add(t.bodyClass); document.body.dataset.vscodeThemeId = t.label;
    document.body.style.background = t.vars['--vscode-editor-background']; document.body.style.color = t.vars['--vscode-foreground']; })()`);
  const size = (width, height = 900) => send('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });

  // The Properties grid with one object loaded, as the provider loads it.
  const P = (name, displayName, category, kind, value) => ({ name, displayName, category, kind, value, description: displayName, options: [], readOnly: false, varies: false });
  const gridLoad = async (ref, query = '', markRequest) => {
    await send('Page.navigate', { url: `${base}/tools/uishot/propgrid.html?s=empty${query}` });
    await wait("document.readyState === 'complete' && !!window.semanticusGrid");
    const name = ref === 'model:' ? 'Contoso' : ref.split('/').slice(1).join('/');
    const m = window_measure(ref);
    const props = ref === 'model:' ? [P('Name', 'Name', 'Basic', 'string', 'Contoso'), P('Culture', 'Culture', 'Basic', 'string', 'en-AU')]
      : [P('Name', 'Name', 'Basic', 'string', name), P('DisplayFolder', 'Display Folder', 'Basic', 'string', m.folder), P('Expression', 'Expression', 'Advanced', 'string', m.expression), P('FormatString', 'Format String', 'Formatting', 'string', '#,0')];
    await evaluate(`window.__marksLoad = ${JSON.stringify({ type: 'load', key: JSON.stringify([ref]), model: 'Contoso.pbip', name, multi: false, props, markRequest })}; window.dispatchEvent(new MessageEvent('message', { data: window.__marksLoad }))`);
    await wait(ref === 'model:' ? `!!${tid('plan-sentence')} && !/Making/.test(${tid('plan-sentence')}.textContent)` : `!!${tid('mk-summary')} && !/Reading/.test(${tid('mk-summary')}.textContent) && !!${tid('mk-score')}`);
  };
  const expressions = { 'Sales Tailspin Toys': 'CALCULATE ( [Sales Amount], \'Product\'[Manufacturer] = "Tailspin Toys" )', 'Total Quantity': 'SUM ( Sales[Quantity] )',
    'Sales YTD': 'TOTALYTD ( [Sales Amount], \'Date\'[Date], "6-30" )', 'Total Cost': 'SUMX ( Sales, Sales[Quantity] * Sales[Unit Cost] )',
    'Gross Margin %': 'SUMX ( VALUES ( Channel[Channel Name] ), DIVIDE ( [Sales Amount] - [Total Cost], [Sales Amount] ) )',
    'Inventory Balance': 'CALCULATE ( SUM ( Inventory[OnHandQuantity] ), LASTNONBLANK ( \'Date\'[Date], CALCULATE ( COUNTROWS ( Inventory ) ) ) )' };
  function window_measure(ref) { const m = fx[ref] || {}; return { folder: m.folder || '', expression: expressions[ref.split('/').slice(1).join('/')] || '' }; }
  const openMark = async name => { await evaluate(`${tid('mk-row-' + name)}.click()`); await wait(`!!${tid('mk-card-' + name)}`); };
  const checksPage = async () => {
    await send('Page.navigate', { url: `${base}/tools/uishot/harness.html?conn=1&tier=pro` });
    await wait("[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Review')");
    await press('Review'); await press('Checks');
    await wait(`!!${tid('checks-measures')} && ${tid('checks-measures')}.children.length > 3 && !!${tid('plan-sentence')} && !/Making/.test(${tid('plan-sentence')}.textContent)`);
    assert.equal(await evaluate(`[...${tid('checks-page')}.querySelectorAll('button')].filter(b => /^(Run|Run checks)$/.test(b.textContent.trim())).length`), 1);
  };

  // Each state: how to reach it, and the part of the page whose buttons the press test presses.
  const T = 'Sales Tailspin Toys', Q = 'Total Quantity', Y = 'Sales YTD', C = 'Total Cost', G = 'Gross Margin %', I = 'Inventory Balance';
  const ref = n => (n === I ? 'measure:Inventory/' : 'measure:Sales/') + n;
  const descriptionState = async state => {
    await gridLoad(ref(Q), '&descwait=1'); await press('Propose a better one');
    await wait(`/Waiting for your assistant/.test(${tid('mk-desc-note')}?.textContent || '')`);
    if (state === 'waiting') return;
    await evaluate(`Object.assign(window.marksEngine.measures[${JSON.stringify(ref(Q))}].proposal, { state: 'ready', text: 'Adds Contoso quantities after returns.', message: 'Your draft is ready.' })`);
    await wait(`!!${tid('mk-proposal')} && !/Waiting|60 seconds/.test(${tid('mk-description')}.textContent)`);
    if (state === 'ready') return;
    await press('Keep this one');
    await wait(`/Kept/.test(${tid('mk-desc-note')}?.textContent || '') && !${tid('mk-proposal')}`);
    if (state === 'kept') return;
    // Undo is a host operation. Its model/didChange reload carries the restored Description back to the grid.
    await evaluate(`window.marksEngine.measures[${JSON.stringify(ref(Q))}].description = 'Number of units sold.'; window.dispatchEvent(new MessageEvent('message', { data: window.__marksLoad }))`);
    await wait(`!${tid('mk-desc-note')} && ${tid('mk-desc-text')}.textContent === 'Number of units sold.'`);
  };
  const states = [
    ...['waiting', 'ready', 'kept', 'undone'].map(state => ['f9-description-' + state, () => descriptionState(state), 'mk-description']),
    ...[['notrun', 'Gross Profit'], ['passed', 'Unit Price'], ['info', 'Customers'], ['suspect', T], ['description', G]].map(([state, n]) => [
      'f9-primary-' + state, async () => {
        const row = rows.find(r => r.name === n);
        await gridLoad(ref(n), '', { name: row.mark, id: 1 });
        assert.ok(row.description.length, 'every tree row has a mark');
        if (row.mark !== 'Description') await wait(`!!${tid('mk-card-' + row.mark)}`);
        checks.push({ check: 'tree mark opens its Properties card', measure: n, mark: row.mark, state });
      }, 'grid-marks',
    ]),
    ['f9-scoring-unreleased', async () => {
      await gridLoad(ref(Q), '&unreleased=1'); await openMark('Totals');
      const text = await evaluate(`${tid('grid-marks')}.innerText`);
      assert.match(text, /This score turns on when Jev scoring is released/);
      assert.doesNotMatch(text, /unavailable right now|outage/);
    }, 'grid-marks'],
    ['f9-native-properties', async () => {
      await gridLoad(ref(Y));
      // VS Code supplies this inset before the grid stylesheet. The old harness omitted it.
      await evaluate("(() => { const s = document.createElement('style'); s.textContent = 'body { padding: 0 20px; } html { scrollbar-gutter: stable; }'; document.head.prepend(s); })()");
      await evaluate(`window.marksEngine.measures[${JSON.stringify(ref(Y))}].marks.find(m => m.name === 'Equivalence').cells[0].context = 'ContosoRetailStoreIdentifierWithNoSpaces=GrandTotal'`);
      await evaluate("window.dispatchEvent(new MessageEvent('message', { data: window.__marksLoad }))");
      await wait("document.querySelector('.mk-banner').textContent.includes('ContosoRetailStoreIdentifier')");
      await openMark('Equivalence');
    }, 'grid-marks'],
    ['fx4-not-scored', async () => {
      await gridLoad(ref(Q), '&descscore=unavailable&totals=unavailable');
      await openMark('Totals');
      const text = await evaluate(`${tid('grid-marks')}.innerText`);
      assert.match(text, /Not scored yet/);
      assert.match(text, /This score turns on when Jev scoring is released/);
      assert.doesNotMatch(text, /judge|Check this measure again|relay|engine|question id/i);
      checks.push({ check: 'Totals and Description use plain not-scored words', measure: Q });
    }, 'grid-marks'],
    ...[70, 69, 50, 49, 'unavailable'].map(score => ['d-description-' + score, async () => {
      await gridLoad(ref(Q), '&descscore=' + score);
      const text = await evaluate(`${tid('mk-score')}.textContent`);
      const label = score === 'unavailable' ? 'Not scored yet' : score >= 70 ? 'Matches' : score >= 50 ? 'Could be improved' : 'Does not match';
      assert.ok(text.includes(label), text);
      assert.doesNotMatch(text, /not measured yet/);
      assert.equal(await evaluate(`document.querySelectorAll('[data-testid^="mk-row-"]').length`), 7);
      checks.push({ check: 'Description band with seven marks', score, label });
    }, 'mk-description']),
    ...[
      ['sum-passed', Q, 'passed'], ['sum-warning', Q, 'warning'],
      ['recalc-passed', G, 'passed'], ['recalc-warning', G, 'warning'],
      ['semi-passed', I, 'passed'], ['semi-warning', I, 'warning'],
      ['distinct-info', 'Customers', 'info'], ['type-unclear', Q, 'unclear'], ['waived', Q, 'waived'],
      ['average-passed', Q, 'passed', 'average'], ['average-warning', Q, 'warning', 'average'],
      ['blank-passed', Q, 'passed', 'blank'], ['blank-warning', Q, 'warning', 'blank'],
      ['first-passed', I, 'passed', 'first'], ['first-warning', I, 'warning', 'first'],
      ['weak-sum-info', Q, 'info'], ['unavailable', Q, 'unavailable'],
    ].map(([id, measure, state, rule]) => ['r-totals-' + id, async () => {
      await gridLoad(ref(measure), '&totals=' + state + (rule ? '&totalrule=' + rule : '')); await openMark('Totals');
      if (state !== 'unavailable') await wait(`!!${tid('mk-total-type')}`);
    }, 'mk-card-Totals']),
    ['g1-properties-suspect', async () => { await gridLoad(ref(T)); }, 'grid-marks'],
    ['g2-card-filters', async () => { await gridLoad(ref(T)); await openMark('Filters'); }, 'grid-marks'],
    ['g3-card-contexts', async () => { await gridLoad(ref(I)); await openMark('Contexts'); }, 'grid-marks'],
    ['g4-card-performance', async () => { await gridLoad(ref(I)); await openMark('Performance'); }, 'grid-marks'],
    ['g5-sql-no-source', async () => { await gridLoad(ref(Q)); await openMark('SQL'); await wait(`!!${tid('mk-no-sql')}`); }, 'grid-marks'],
    ['g6-sql-with-source', async () => { await gridLoad(ref(Q), '&sql=1'); await openMark('SQL'); await wait("[...document.querySelectorAll('button')].some(b => b.textContent === 'Set up a SQL check')"); }, 'grid-marks'],
    ['g7-trusted-number', async () => { await gridLoad(ref(Q)); await openMark('Trusted number'); }, 'grid-marks'],
    ['g8-moved', async () => { await gridLoad(ref(Y)); await openMark('Equivalence'); }, 'grid-marks'],
    ['g-fx17-equivalence', async () => {
      await gridLoad(ref(Q), '&fx17ratio=1'); await openMark('Equivalence');
      const text = await evaluate(`${tid('mk-card-Equivalence')}.textContent`);
      assert.match(text, /628,370/); assert.match(text, /0\.0097%/); assert.doesNotMatch(text, /E-05/);
    }, 'grid-marks'],
    ['g9-nothing-moved', async () => { await gridLoad(ref(C)); }, 'grid-marks'],
    ['g10-totals-passed', async () => { await gridLoad(ref(G)); await openMark('Totals'); }, 'grid-marks'],
    ['g11-edit-description', async () => { await gridLoad(ref(Q)); await press('Edit'); await wait(`!!${tid('mk-desc-input')}`); }, 'grid-marks'],
    ['g12-proposal', async () => { await gridLoad(ref(Q)); await press('Propose a better one'); await wait(`!!${tid('mk-proposal')}`); }, 'grid-marks'],
    ['g13-run-plan', async () => { await gridLoad('model:'); await press('Show the reasons'); await wait(`!!${tid('plan-reasons')}`); }, 'grid-marks'],
    ['g14-run-plan-performance', async () => { await gridLoad('model:'); await evaluate(`${tid('plan-performance')}.click()`); await wait(`/with performance/.test(${tid('plan-sentence')}.textContent)`); }, 'grid-marks'],
    ['g15-pick', async () => { await gridLoad('model:'); await press('Choose instead'); await wait(`!!${tid('plan-pick')}`);
      await evaluate(`document.querySelector('[aria-label="Open Sales"]').click()`); await delay(50);
      for (const n of [T, Y, G]) await evaluate(`document.querySelector('[data-testid="tick-${ref(n)}"]').click()`);
      await wait(`!!${tid('pick-estimate')}`); }, 'grid-marks'],
    ['c1-checks-page', checksPage, 'checks-page'],
    ['c-fx17-checks-layout', async () => {
      await checksPage();
      await evaluate(`(() => { const row = document.querySelector('[data-testid="checks-row-${ref('ShowValueForDates')}"]');
        // A 1440 px VS Code window can leave 680 px for Checks beside its native sidebars.
        row.closest('.cp-page').style.maxWidth = '680px';
      })()`);
    }, 'checks-page'],
    ['c2-checks-open', async () => { await checksPage(); await evaluate(`document.querySelector('[data-testid="checks-row-${ref(T)}"] button').click()`); await wait(`!!${tid('marks-panel')} && !/Reading/.test(${tid('mk-summary')}.textContent)`);
      await evaluate(`document.querySelector('[data-testid="checks-row-${ref(T)}"]').scrollIntoView({ block: 'start' })`); }, 'checks-page'],
    ['c3-checks-reasons', async () => { await checksPage(); await press('Show the reasons'); await wait(`!!${tid('plan-reasons')}`);
      const text = await evaluate(`${tid('plan-reasons')}.textContent`); assert.match(text, /Last checked/); assert.doesNotMatch(text, /01:02:04Z|Oldest check:/); }, 'checks-page'],
    ['c4-checks-performance', async () => { await checksPage(); await evaluate(`${tid('plan-performance')}.click()`); await wait(`/with performance/.test(${tid('plan-sentence')}.textContent)`); }, 'checks-page'],
    ['c5-checks-ran', async () => { await checksPage(); await press('Run'); await wait(`/Checked 34 measures/.test(${tid('plan-note')}?.textContent || '')`); }, 'checks-page'],
    ['t1-tree-row', async () => { await send('Page.navigate', { url: `${base}/tree-row.html` }); await wait("!!document.querySelector('.row')"); }, null],
    ['t2-tree-hover', async () => { await send('Page.navigate', { url: `${base}/tree-row.html` }); await wait("!!document.querySelector('.row')"); await evaluate("document.body.classList.add('hover')"); }, null],
  ].filter(([name]) => !only || only.test(name));

  // 1. Shots: every state, light and dark, at 1280 and at a 300 px sidebar.
  for (const theme of ['light', 'dark']) for (const sidebar of (process.env.MARKS_WIDTHS || '1280,300').split(',').map(Number)) for (const [name, reach] of states) {
    // The tree stand-in is a window with a sidebar: its hover floats over the editor beside a 300 px sidebar.
    const width = name.startsWith('t') && sidebar === 300 ? 700 : sidebar;
    await size(width);
    await reach();
    await applyTheme(theme);
    if (name.startsWith('t')) await evaluate(`document.querySelector('.side').style.setProperty('--w', '${sidebar === 300 ? 300 : 520}px')`);
    await evaluate('document.fonts.ready'); await delay(120);
    if (name === 'c-fx17-checks-layout') {
      const gap = await evaluate(`(() => { const row = document.querySelector('[data-testid="checks-row-${ref('ShowValueForDates')}"]');
        const name = row.querySelector('.cp-name'), summary = row.querySelector('.cp-reason');
        const text = document.createRange(); text.selectNodeContents(name);
        const a = text.getBoundingClientRect(), b = summary.getBoundingClientRect();
        return { horizontal: b.left - a.right, wrapped: b.top >= a.bottom, width: innerWidth };
      })()`);
      checks.push({ check: 'ShowValueForDates has a gap or a wrap', ...gap });
      assert.ok(gap.wrapped || gap.horizontal >= 8, JSON.stringify(gap));
    }
    const m = await evaluate(`({ w: document.documentElement.clientWidth, sw: document.documentElement.scrollWidth, h: Math.max(document.documentElement.scrollHeight, 400),
      wide: [...document.querySelectorAll(${JSON.stringify(name.startsWith('c') ? '[data-testid=checks-page] *' : 'body *')})].filter(e => e.getBoundingClientRect().right > innerWidth + 1 && e.children.length === 0).slice(0, 5).map(e => (e.className || e.tagName) + ':' + (e.textContent || '').slice(0, 30)) })`);
    // Studio's own tab bar is wider than 300 px (an editor tab, not a sidebar); the Checks page content must still fit.
    if (name.startsWith('c')) m.page = await evaluate(`(() => { const r = document.querySelector('[data-testid=checks-page]').getBoundingClientRect(); return [r.left, r.right, document.querySelector('[data-testid=checks-page]').scrollWidth]; })()`);
    assert.ok(name.startsWith('c') ? m.wide.length === 0 : m.sw <= m.w, `${name} ${theme} ${width}: no sideways scroll (${m.sw} > ${m.w}) ${JSON.stringify(m.wide)} ${JSON.stringify(m.page)}`);
    await size(width, Math.min(m.h, 4000));
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    const file = `${name}-${sidebar}-${theme}.png`;
    writeFileSync(join(out, file), Buffer.from(shot.data, 'base64'));
    shots.push(file);
    if (name.startsWith('d-description-')) {
      const clip = await evaluate(`(() => { const r = ${tid('mk-description')}.getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width, height: r.height, scale: 1 }; })()`);
      const card = await send('Page.captureScreenshot', { format: 'png', clip });
      writeFileSync(join(out, `${name}-${sidebar}-${theme}-description.png`), Buffer.from(card.data, 'base64'));
    }
    if (name.startsWith('r-totals-')) {
      const clip = await evaluate(`(() => { const r = ${tid('mk-card-Totals')}.getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width, height: r.height, scale: 1 }; })()`);
      const card = await send('Page.captureScreenshot', { format: 'png', clip });
      writeFileSync(join(out, `${name}-${sidebar}-${theme}-card.png`), Buffer.from(card.data, 'base64'));
    }
  }

  // 2. Behaviour the brief names, checked on the real pages.
  await size(1280);
  if (!only || only.test('r-totals-sum-warning')) {
    await states.find(s => s[0] === 'r-totals-sum-warning')[1]();
    const before = await evaluate(`${tid('mk-card-Totals')}.innerText`);
    assert.match(before, /Confidence 94%/);
    assert.match(before, /Jev reading the name, the Description and the formula/);
    assert.equal(await evaluate(`${tid('mk-card-Totals')}.querySelectorAll('tbody tr').length`), 4);
    await press('This is expected', `document.querySelector('[data-testid="mk-card-Totals"]')`);
    await wait(`!/Suspect/.test(${tid('mk-card-Totals')}.innerText) && /This is expected/.test(${tid('mk-card-Totals')}.innerText)`);
    assert.equal(await evaluate("window.marksEngine.calls.filter(c => c.method === 'setMeasureTotals').length"), 1);
    assert.equal(await evaluate(`${tid('mk-card-Totals')}.querySelectorAll('.mk-acts button').length`), 0);
    checks.push({ check: 'Totals waiver saves once and stops the warning' });
  }
  if (!only || only.test('g15-pick')) {
    await states.find(s => s[0] === 'g15-pick')[1]();
    const before = await evaluate(`${tid('pick-estimate')}.textContent + '|' + ${tid('plan-sentence')}.textContent`);
    await evaluate(`${tid('plan-performance')}.click()`);
    await wait(`/with performance/.test(${tid('pick-estimate')}.textContent) && /with performance/.test(${tid('plan-sentence')}.textContent)`, 'both estimates change with Include performance');
    const after = await evaluate(`${tid('pick-estimate')}.textContent + '|' + ${tid('plan-sentence')}.textContent`);
    assert.notEqual(after, before);
    await press('Check selected (3)');
    await wait(`/Checked 3 measures/.test(${tid('plan-note')}?.textContent || '')`, 'Check selected reports the 3 it ran');
    const ran = await evaluate('window.marksEngine.lastRun');
    assert.equal(ran, 'plan-' + [T, Y, G].map(ref).join('|') + '+perf', 'Check selected runs exactly the ticked set');
    checks.push({ check: 'Include performance changes both estimates', before, after }, { check: 'Check selected runs exactly the ticked set', ran });
  }
  if (!only || only.test('g11-edit')) {
    await states.find(s => s[0] === 'g11-edit-description')[1]();
    await evaluate(`(() => { const t = ${tid('mk-desc-input')}; const set = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set; set.call(t, 'Number of units sold, after returns.'); t.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    await press('Save');
    await wait(`/after returns/.test(${tid('mk-desc-text')}?.textContent || '') && /Written by a person/.test(${tid('mk-provenance')}.textContent)`, 'Edit saves the Description');
    const writes = await evaluate("window.marksEngine.calls.filter(c => c.method === 'setDescription').length");
    assert.equal(writes, 1, 'one Save is one write');
    checks.push({ check: 'Edit saves with one write and shows the person as the source', writes });
    // A change made while the person types is not written over: Save keeps the words and asks for a second press.
    await states.find(s => s[0] === 'g11-edit-description')[1]();
    await evaluate(`(() => { const t = ${tid('mk-desc-input')}; const set = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set; set.call(t, 'My words.'); t.dispatchEvent(new Event('input', { bubbles: true })); })()`);
    await evaluate(`window.marksEngine.measures['measure:Sales/Total Quantity'].description = 'Changed by your assistant.'`);
    await press('Save');
    await wait(`/changed while you typed/.test(${tid('mk-desc-note')}?.textContent || '') && ${tid('mk-desc-input')}.value === 'My words.'`, 'Save keeps the words when the Description changed');
    const guarded = await evaluate("window.marksEngine.calls.filter(c => c.method === 'setDescription').length");
    assert.equal(guarded, 0, 'nothing is written over a newer Description');
    checks.push({ check: 'Save does not write over a Description changed while typing', writes: guarded });
  }
  if (!only || only.test('g2-card')) {
    await states.find(s => s[0] === 'g2-card-filters')[1]();
    await press('Close', `document.querySelector('[data-testid="mk-card-Filters"]')`);
    await wait(`!${tid('mk-card-Filters')}`, 'Close closes the card');
    checks.push({ check: 'Close closes the card' });
  }

  // 3. Every button in every state shows a visible result within two seconds.
  await size(1280);
  for (const [name, reach, scope] of states) {
    if (!scope) continue;
    await reach();
    const labels = await evaluate(`[...new Set([...${tid(scope)}.querySelectorAll('button')].filter(b => !b.disabled && b.offsetParent).map(b => b.textContent.trim() || b.getAttribute('aria-label')))]`);
    for (const label of labels) {
      await reach();
      const ms = await evaluate(`new Promise((resolve) => {
        const scope = ${tid(scope)}; const b = [...scope.querySelectorAll('button')].find(b => !b.disabled && b.offsetParent && (b.textContent.trim() || b.getAttribute('aria-label')) === ${JSON.stringify(label)});
        if (!b) { resolve(-1); return; }
        const snap = () => document.body.innerText + '|' + [...document.querySelectorAll('[aria-expanded],[aria-pressed],button')].map(x => x.getAttribute('aria-expanded') + x.disabled).join('');
        const before = snap(), start = performance.now();
        b.click();
        const tick = () => { if (snap() !== before) resolve(performance.now() - start); else if (performance.now() - start > 2000) resolve(9999); else setTimeout(tick, 10); };
        tick();
      })`);
      presses.push({ state: name, button: label, ms: Math.round(ms) });
    }
  }
  const slow = presses.filter(p => p.ms < 0 || p.ms > 2000);
  assert.deepEqual(errors, [], 'no page errors');
  writeFileSync(join(out, 'results.json'), JSON.stringify({ shots, presses, checks, slow, errors }, null, 2));
  assert.deepEqual(slow, [], 'every button shows a visible result within two seconds');
  console.log(JSON.stringify({ out, shots: shots.length, presses: presses.length, within2s: presses.length - slow.length, checks: checks.length, errors: errors.length }));
} catch (error) {
  writeFileSync(join(out, 'failure.json'), JSON.stringify({ error: String(error?.stack || error), errors, shots, presses }, null, 2));
  throw error;
} finally {
  if (closeBrowser) await Promise.race([closeBrowser().catch(() => {}), delay(2000)]);
  socket?.close();
  if (browser.exitCode === null && browser.signalCode === null) browser.kill('SIGTERM');
  server.closeAllConnections();
  await new Promise(r => server.close(r));
}
