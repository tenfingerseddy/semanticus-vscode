// fx29: the rc28 release tester's gaps 1, 2, 3, 7 and 8 on the Checks page (1440), Properties (290) and the Model tree row
// stand-in (290), light and dark, in headless Chromium with a mocked engine (fx29-checks-fixture.js). FX29_MODE=before serves
// FX29_ROOT (the rc28 build) and the fixture's rc28 engine answers, and records each check instead of failing on it (the
// failing-first proof); FX29_MODE=after (the default) serves this tree and fails on any check. Shots: <gap>-<mode>-<width>-<theme>.png.
// Based on checks-d1-drive.mjs: the 1.7 Checks screens after the d1 design pass, in headless Chromium with a mocked engine (checks-d1-fixture.js): the
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

const here = dirname(fileURLToPath(import.meta.url));
const mode = process.env.FX29_MODE === 'before' ? 'before' : 'after';
const root = resolve(process.env.FX29_ROOT || resolve(here, '../..'));
const out = resolve(process.env.OUTDIR || join(here, 'shots/fx29'));
mkdirSync(out, { recursive: true });
const fixture = readFileSync(join(here, 'fx29-checks-fixture.js'), 'utf8');
const q = mode === 'before' ? '&rc28=1' : '';
const failures = [];
// A check: in after mode it fails the run; in before mode it is recorded (the gap, shown failing on the rc28 build).
const check = (gap, ok, what) => { if (ok) return; if (mode === 'after') assert.fail(`${gap}: ${what}`); failures.push(`${gap}: ${what}`); };
const only = process.env.D1_ONLY ? new RegExp(process.env.D1_ONLY) : null;
const { treeRowItem } = createRequire(import.meta.url)(join(root, 'out/marksModel.js'));
const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;');
const words = s => (String(s).match(/[A-Za-z0-9][\w'%.,:-]*/g) || []).length;

// The tree stand-in for one scenario: the native row is an icon, the label, then the description (the verdict glyph).
const treePage = (sc, hover, pushed = null) => {
  const sandbox = { window: {}, location: { search: '?sc=' + sc + q }, URLSearchParams, Date, setTimeout, Promise, MessageEvent: class {}, };
  sandbox.window.dispatchEvent = () => {};
  vm.runInNewContext(fixture, sandbox);
  const engine = sandbox.window.marksEngine;
  if (pushed != null) { engine.dispatched = []; sandbox.window.dispatchEvent = e => engine.dispatched.push(e); }
  const page = engine.call('getChecksPage', []);
  let cache = engine.call('getMeasureCheckCache', []);
  if (pushed != null) { sandbox.MessageEvent = class { constructor(t, i) { this.data = i.data; } }; engine.pushDone(pushed); cache = engine.dispatched.at(-1)?.data.payload.states ?? cache; }
  const rows = page.measures.map(m => { const c = cache.find(x => x.measureRef === m.ref);
    return { name: m.name, ...treeRowItem(m.name, c?.marks?.length ? c.marks : c?.cheap, { oneLine: m.oneLine, match: m.descriptionMatch, checked: !!c?.lastChecked }) }; });
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
    await send('Page.navigate', { url: `${base}/tools/uishot/propgrid.html?s=empty&sc=${sc}${q}` });
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
    await send('Page.navigate', { url: `${base}/tools/uishot/harness.html?conn=1&tier=pro&sc=${sc}${q}` });
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

  const tidOk = t => evaluate(`!!${tid(t)}`);
  const allRowGlyphs = () => evaluate(`Object.fromEntries([...document.querySelectorAll('.cp-row')].map(r => [r.querySelector('.cp-name')?.textContent, r.querySelector('.cp-glyph')?.textContent || '']))`);
  const tree290 = async (sc, hover, pushed) => { const t = treePage(sc, hover, pushed); tree = t.html; await send('Page.navigate', { url: base + '/tree.html' }); await wait("!!document.querySelector('.row')"); return t.rows; };
  const states = [
    // Gap 1: Pro advice off for this model reads one plain line with Turn on, under the Checks verdict line and on the card.
    ['gap1', 1440, async () => { await checks('advice'); await openRow('Total Quantity');
      const line = await text('checks-actionable');
      check('gap1', /^Pro advice is off for this model\. Turn on$/.test(line), `Checks page line reads "${line}"`);
      check('gap1', await tidOk('checks-turn-on'), 'no Turn on on the Checks page');
      check('gap1', await tidOk('mk-turn-on'), 'no Turn on on the card');
      counts.push({ gap: 'gap1', header: await text('checks-verdict'), line, card: await text('mk-actionable') }); }],
    ['gap1', 290, async () => { await grid('advice', 'Total Quantity');
      const line = await text('mk-actionable');
      check('gap1', /^Pro advice is off for this model\. Turn on$/.test(line), `Properties card line reads "${line}"`);
      counts.push({ gap: 'gap1 props', line, verdict: await text('mk-verdict') }); }],
    // Gap 2: a run has checked 4 of 12; the other 8 are only waiting (cheap pass done). They keep no glyph, never Unknown.
    ['gap2', 1440, async () => { await checks('running'); await press('Run');
      await evaluate('window.marksEngine.pushDone(4)'); await wait(`/Checking 4 of 12/.test(${tid('plan-sentence')}.textContent)`, 'the running line');
      await delay(200);
      // Open Not checked yet (collapsed when another group exists), so the shot and the check see the waiting rows.
      if (await tidOk('group-unchecked')) { await evaluate(`${tid('group-unchecked')}.querySelector('button').click()`); await delay(150); }
      const head = await text('checks-verdict'), glyphs = await allRowGlyphs();
      check('gap2', !/unknown/i.test(head), `the header reads "${head}" while 8 measures only wait`);
      check('gap2', !(await tidOk('group-unknown')), 'an Unknown group shows while measures only wait');
      check('gap2', glyphs['Gross Profit'] === '', `a waiting row shows "${glyphs['Gross Profit']}"`);
      counts.push({ gap: 'gap2', header: head, plan: await text('plan-sentence') }); }],
    ['gap2', 290, async () => { const rows = await tree290('running', 'Gross Profit', 4);
      const waiting = rows.find(r => r.name === 'Gross Profit');
      check('gap2', waiting.description === '' && !/Unknown/.test(waiting.label), `the waiting tree row reads "${waiting.description}" "${waiting.label}"`);
      counts.push({ gap: 'gap2 tree', row: waiting.label }); }],
    // Gap 3: the run ends. The header lists every non-zero part, verified included.
    ['gap3', 1440, async () => { await checks('running'); await press('Run'); await evaluate('window.marksEngine.pushDone(5)');
      await wait(`/Checking 5 of 12\\./.test(${tid('plan-sentence')}.textContent)`, 'the running line'); await evaluate('window.marksEngine.release()');
      await wait(`!/Checking 5/.test(${tid('plan-sentence')}.textContent) && /unknown/.test(${tid('checks-verdict')}.textContent)`, 'the finished state');
      await delay(300);
      const head = await text('checks-verdict');
      check('gap3', head === '1 needs review · 2 unknown · 9 verified', `the header reads "${head}"`);
      check('gap3', words('Checks ' + head) <= 12, 'the header is over 12 words');
      counts.push({ gap: 'gap3', header: head }); }],
    ['gap3', 290, async () => { await checks('sum'); const head = await text('checks-verdict');
      check('gap3', head === '1 needs review · 2 unknown · 9 verified', `the header at 290 reads "${head}"`); }],
    // Gap 7: a measure just added has no glyph until its checks run (no Equivalence: it has no before).
    ['gap7', 1440, async () => { await checks('added');
      const glyphs = await allRowGlyphs();
      check('gap7', glyphs['New Measure'] === undefined || glyphs['New Measure'] === '', `the new measure's row shows "${glyphs['New Measure']}"`);
      const head = await text('checks-verdict');
      check('gap7', head === '1 needs review · 2 unknown · 9 verified · 1 not checked yet', `the header reads "${head}"`);
      // The new measure sits in Not checked yet (collapsed when other groups exist): open it so the shot shows the row.
      if (await tidOk('group-unchecked')) { await evaluate(`${tid('group-unchecked')}.querySelector('button').click()`); await delay(100); await openRow('New Measure'); }
      else await openRow('New Measure');
      const verdict = await text('mk-verdict');
      check('gap7', /^Not checked yet/.test(verdict), `the new measure's card reads "${verdict}"`);
      counts.push({ gap: 'gap7', header: head, verdict }); }],
    ['gap7', 290, async () => { const rows = await tree290('added', 'New Measure');
      const added = rows.find(r => r.name === 'New Measure');
      check('gap7', added.description === '' && !/Unknown/.test(added.label), `the new measure's tree row reads "${added.description}" "${added.label}"`);
      counts.push({ gap: 'gap7 tree', row: added.label }); }],
    // Gap 8: Date New Customer (format yyyy-mm-dd) reads 2016-09-12 in its sentence and its cells.
    ['gap8', 1440, async () => { await checks('sum'); await openRow('Date New Customer', 'Filters');
      const detail = await text('mk-card-Filters');
      check('gap8', /2016-09-12/.test(detail) && !/Sep 2016/.test(detail), `the Filters detail reads "${detail}"`);
      counts.push({ gap: 'gap8', detail }); }],
    ['gap8', 290, async () => { await grid('sum', 'Date New Customer', 'Filters');
      const detail = await text('mk-card-Filters');
      check('gap8', /2016-09-12/.test(detail) && !/Sep 2016/.test(detail), `the Properties Filters detail reads "${detail}"`); }],
  ].filter(([name]) => !only || only.test(name));

  for (const theme of ['light', 'dark']) for (const [gap, width, reach] of states) {
    const name = gap + (width === 290 && (gap === 'gap2' || gap === 'gap7') ? '-tree' : width === 290 && gap !== 'gap3' ? '-props' : '-checks');
    // Studio scrolls inside its own frame, so the Checks page is shot in a tall window to show the header and the card.
    await size(name.endsWith('-tree') ? 640 : width, name.endsWith('-tree') ? 460 : name.endsWith('-checks') ? 1500 : 900);
    await reach();
    await applyTheme(theme);
    await evaluate('document.fonts.ready'); await delay(150);
    const m = await evaluate(`({ w: document.documentElement.clientWidth, h: Math.max(document.documentElement.scrollHeight, 400),
      wide: [...document.querySelectorAll(${JSON.stringify(name.endsWith('-checks') ? '[data-testid=checks-page] *' : name.endsWith('-props') ? '[data-testid=marks-panel] *' : '.side *')})]
        .filter(e => e.getBoundingClientRect().right > innerWidth + 1 && e.children.length === 0).slice(0, 5).map(e => (e.className || e.tagName) + ':' + (e.textContent || '').slice(0, 30)) })`);
    check(gap, m.wide.length === 0, `${name} ${theme}: past the right edge: ${m.wide.join(', ')}`);
    if (name.endsWith('-props')) await size(width, Math.min(m.h, 3000));
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    const file = `${gap}-${mode}-${width}-${theme}.png`;
    writeFileSync(join(out, file), Buffer.from(shot.data, 'base64'));
    shots.push(file);
  }
  assert.deepEqual(errors, [], 'no page errors');
  writeFileSync(join(out, `results-${mode}.json`), JSON.stringify({ mode, shots, counts, errors, failures: [...new Set(failures)] }, null, 2));
  console.log(JSON.stringify({ mode, out, shots: shots.length, failures: [...new Set(failures)], counts }, null, 1));
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
