import assert from 'node:assert/strict';
import { test } from 'node:test';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { existsSync, readFileSync, mkdtempSync, rmSync, writeFileSync, openSync, closeSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { req, makeLoader } from './helpers/webview-load.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const browserPath = process.env.SEMANTICUS_BROWSER || '/usr/bin/chromium';

// Exercise the registered tree command, rather than manufacturing a different navigation for the test.
function commandNavigation(ref) {
  const source = readFileSync(join(root, 'src/extension.ts'), 'utf8');
  const registration = source.split('\n').find((line) => line.includes("registerPersonCommand('semanticus.tryCalculation'"));
  assert.ok(registration, 'Try calculation is registered');
  const functions = source.slice(source.indexOf('function navigateStudio('), source.indexOf('// Model-tree "Add to Studio Diagram"'));
  const js = req('typescript').transpileModule(`
    let command, nav, pendingNav;
    const currentSessionId = 'uishot';
    let studioReady = false;
    const studioPanel = { webview: { postMessage(message) { nav = message; } } };
    function openStudio() {}
    function registerPersonCommand(_id, handler) { command = handler; }
    const context = {}, treeView = null;
    ${functions}
    ${registration.trim().replace(/,$/, '')}
    command({ ref: ${JSON.stringify(ref)} });
    if (nav) throw new Error('A cold Studio must wait for studioReady');
    studioReady = true;
    flushNav();
    return nav;
  `, {
    compilerOptions: { target: req('typescript').ScriptTarget.ES2022 },
  }).outputText;
  return new Function(js)();
}

test('the default grid follows scope tests in the measure and its dependencies', () => {
  const { tryCalculationQuery: query } = makeLoader()('try-calculation.ts');
  const measures = [
    { ref: 'measure:Sales/Scoped', table: 'Sales', name: 'Scoped', expression: '[Base] + IF(ISINSCOPE(\'Date\'[Year]), 1)' },
    { ref: 'measure:Sales/Base', table: 'Sales', name: 'Base', expression: 'SELECTEDVALUE(Product[Category]) + [Scoped]' },
  ];
  const columns = [{ table: 'Date', name: 'Year' }, { table: 'Product', name: 'Category' }];
  const dax = query(measures[0].ref, measures, columns);
  assert.match(dax, /^DEFINE\nMEASURE /);
  assert.match(dax, /EVALUATE TOPN\(100, SUMMARIZECOLUMNS\(/);
  assert.match(dax, /'Date'\[Year\]/);
  assert.match(dax, /'Product'\[Category\]/);
  assert.match(dax, /"Scoped", \[Scoped\]/);
  assert.equal(query('measure:Sales/A"B]', [], []), 'EVALUATE ROW("A""B]", [A"B]]])');
  const literal = [{ ...measures[0], expression: '"ISINSCOPE(\'Date\'[Year])" // SELECTEDVALUE(Product[Category])' }];
  const literalDax = query(literal[0].ref, literal, columns);
  assert.match(literalDax, /EVALUATE ROW\("Scoped", \[Scoped\]\)$/);
  assert.ok(!literalDax.includes('SUMMARIZECOLUMNS'));
});

test('the webview bridge preserves the explicit run request and ordinary navigation stays passive', () => {
  const previousWindow = globalThis.window, previousApi = globalThis.acquireVsCodeApi;
  let receive;
  try {
    globalThis.window = { addEventListener: (type, handler) => { if (type === 'message') receive = handler; } };
    globalThis.acquireVsCodeApi = () => ({ postMessage() {}, getState() { return {}; }, setState() {} });
    const bridge = makeLoader()('bridge.ts');
    const seen = [];
    bridge.onNavigate((message) => seen.push(message));
    receive({ data: commandNavigation('measure:Sales/Total Sales') });
    receive({ data: { type: 'navigate', tab: 'query', target: 'measure:Sales/Total Sales' } });
    assert.equal(seen[0].run, true);
    assert.equal(seen[1].run, false);
    const rpcDoor = readFileSync(join(root, '../Semanticus.Engine/EngineRpcTarget.cs'), 'utf8');
    const mcpDoor = readFileSync(join(root, '../Semanticus.Engine/McpTools.cs'), 'utf8');
    assert.match(rpcDoor, /runDax\([^\n]+=> _engine\.RunDaxAsync\(query, maxRows/);
    assert.match(mcpDoor, /Name = "run_dax"[\s\S]*?engine\.RunDaxAsync\(query, maxRows/);
  } finally {
    globalThis.window = previousWindow; globalThis.acquireVsCodeApi = previousApi;
  }
});

async function chrome() {
  const profile = mkdtempSync(join(tmpdir(), 'try-calculation-'));
  // Chromium's Unix socket needs a short spelling of the same scratch directory.
  const tempFd = process.platform === 'linux' ? openSync(tmpdir(), 'r') : null;
  const env = { ...process.env, ...(tempFd === null ? {} : { TMPDIR: `/proc/${process.pid}/fd/${tempFd}` }) };
  const child = spawn(browserPath, ['--headless', '--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage',
    '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { env, stdio: ['ignore', 'ignore', 'pipe'] });
  let stderr = '';
  const endpoint = await new Promise((ok, no) => {
    child.once('error', no);
    child.stderr.on('data', (chunk) => {
      stderr += chunk;
      const match = stderr.match(/DevTools listening on (ws:\/\/[^\s]+)/);
      if (match) ok(match[1]);
    });
    child.once('exit', (code) => no(new Error(`Chromium exited ${code}: ${stderr}`)));
  });
  const socket = new WebSocket(endpoint);
  await new Promise((ok, no) => { socket.onopen = ok; socket.onerror = no; });
  let id = 0;
  const pending = new Map();
  const errors = [];
  socket.onmessage = ({ data }) => {
    const msg = JSON.parse(data);
    if (!msg.id) {
      if (msg.method === 'Runtime.exceptionThrown' || msg.method === 'Runtime.consoleAPICalled' && msg.params?.type === 'error') errors.push(msg.params);
      return;
    }
    const waiter = pending.get(msg.id);
    pending.delete(msg.id);
    if (msg.error) waiter?.no(new Error(JSON.stringify(msg.error)));
    else waiter?.ok(msg.result);
  };
  const send = (method, params = {}, sessionId) => new Promise((ok, no) => {
    const next = ++id;
    pending.set(next, { ok, no });
    socket.send(JSON.stringify({ id: next, method, params, ...(sessionId ? { sessionId } : {}) }));
  });
  const { targetId } = await send('Target.createTarget', { url: 'about:blank' });
  const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true });
  const page = (method, params) => send(method, params, sessionId);
  await page('Page.enable');
  await page('Runtime.enable');
  await page('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });
  const evaluate = async (expression) => {
    const r = await page('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error(JSON.stringify(r.exceptionDetails));
    return r.result.value;
  };
  return { page, evaluate, errors, close: async () => {
    const exited = new Promise((ok) => child.once('exit', ok));
    await send('Browser.close').catch(() => {});
    await exited;
    socket.close();
    if (tempFd !== null) closeSync(tempFd);
    rmSync(profile, { recursive: true, force: true });
  } };
}

async function until(check, message) {
  const end = Date.now() + 8000;
  do {
    if (await check()) return;
    await new Promise((ok) => setTimeout(ok, 80));
  } while (Date.now() < end);
  assert.fail(message);
}

test('Try calculation runs once and shows a number, including a cold Studio', {
  skip: !existsSync(browserPath) || typeof WebSocket === 'undefined', timeout: 60000,
}, async () => {
  const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.woff2': 'font/woff2' };
  const server = createServer((request, response) => {
    const path = resolve(root, '.' + new URL(request.url, 'http://localhost').pathname);
    if (!path.startsWith(root + '/') || !existsSync(path)) { response.writeHead(404).end(); return; }
    response.writeHead(200, { 'content-type': types[extname(path)] || 'application/octet-stream' });
    response.end(readFileSync(path));
  });
  await new Promise((ok) => server.listen(0, '127.0.0.1', ok));
  let browser;
  try { browser = await chrome(); } catch (error) { server.close(); throw error; }
  const nav = commandNavigation('measure:Sales/Total Sales');
  const runs = () => browser.evaluate("window.__uishotHarness.messages.filter(m => m.method === 'runDax').map(m => m.params)");
  try {
    for (const cold of [false, true]) {
      const fixture = {
        session: { sessionId: 'uishot', modelName: 'Measure fixture', tables: 1, measures: 1, hasUnsavedChanges: false, liveConnected: false },
        connectionContext: {
          editing: { available: true, modelName: 'Measure fixture', kind: 'file', source: '/fixture/model.bim', live: false },
          querying: { available: true, modelName: 'Measure fixture', kind: 'localDesktop', endpoint: 'localhost:51000', database: 'Fixture', live: true },
          publishing: { available: false }, reference: { available: false },
          relationship: 'workingCopyAndLocalRuntime', twoModelsInPlay: false,
          summary: 'Queries run on the fixture model.',
        },
        pivot: { columns: [{ name: 'Total Sales', type: 'Int64' }], rows: [[42]], rowCount: 1, truncated: false, elapsedMs: 3 },
      };
      const { identifier } = await browser.page('Page.addScriptToEvaluateOnNewDocument', { source: `
        window.__UISHOT__ = ${JSON.stringify(fixture)};
        // The generic harness predates getChecksPage. Supply its real response shape before Studio mounts.
        Object.defineProperty(window, '__uishotHarness', { configurable: true, set(harness) {
          harness.setResponse('getChecksPage', {
            sessionId: 'uishot', revision: 0, measures: [],
            summary: {checked:0,total:0,wrong:0,partly:0,notChecked:0,checking:0,numbersDiffer:0,keyProblems:0,tableProblems:0},
            numbers: {definitions:[]}, results: {}, relationships: null, tables: {rows:[]}, when: null, resultTimes: {}, relationshipsWhen: null,
          });
          Object.defineProperty(window, '__uishotHarness', { configurable: true, value: harness });
        } });
        ${cold ? `Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true, set(factory) {
          Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true, value: () => {
            const api = factory(); const post = api.postMessage;
            api.postMessage = function(message) {
              post.call(api, message);
              if (message.type === 'studioReady') setTimeout(() => window.dispatchEvent(new MessageEvent('message', { data: ${JSON.stringify(nav)} })), 0);
            }; return api;
          } });
        } });` : ''}
      ` });
      await browser.page('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?conn=1&sessionDelay=${cold ? 250 : 0}` });
      await until(() => browser.evaluate("!!document.querySelector('#root > *') && window.__uishotHarness?.messages.some(m => m.type === 'studioReady')"), 'Studio mounted');
      if (!cold) await browser.evaluate(`window.__uishotHarness.dispatch(${JSON.stringify(nav)})`);
      await until(async () => (await runs()).length > 0, 'Try calculation must send runDax; opening Query alone is not enough');
      assert.equal((await runs()).length, 1, 'one deliberate action runs once');
      assert.ok((await runs())[0][0].endsWith('EVALUATE ROW("Total Sales", [Total Sales])'));
      assert.match((await runs())[0][0], /^DEFINE\nMEASURE 'Sales'\[Total Sales\] = /);
      await until(() => browser.evaluate("[...document.querySelectorAll('[data-testid=query-page] [data-testid=figure]')].some(figure => figure.getClientRects().length && figure.textContent.includes('42'))"), 'the notebook shows the returned number');
      await new Promise((ok) => setTimeout(ok, 350));
      assert.equal((await runs()).length, 1, 'acknowledging the route cannot replay the run');
      if (cold && process.env.TRY_CALCULATION_SHOT) {
        const shot = await browser.page('Page.captureScreenshot', { format: 'png' });
        writeFileSync(process.env.TRY_CALCULATION_SHOT, Buffer.from(shot.data, 'base64'));
      }
      await browser.page('Page.removeScriptToEvaluateOnNewDocument', { identifier });
    }
    // A measure whose number needs a column context gets a small grid and still runs once.
    await browser.evaluate(`window.__uishotHarness.setResponse('listMeasures', [{ref:'measure:Sales/Scoped',table:'Sales',name:'Scoped',expression:"IF(ISINSCOPE('Date'[Year]), 42)"}]);
      window.__uishotHarness.setResponse('listColumns', [{table:'Date',name:'Year'}]);
      window.__uishotHarness.setResponse('runDax', {columns:[{name:'Year'},{name:'Scoped'}],rows:[[2026,42]],rowCount:1,truncated:false,elapsedMs:3});`);
    await browser.evaluate(`window.__uishotHarness.dispatch(${JSON.stringify(commandNavigation('measure:Sales/Scoped'))})`);
    await until(async () => (await runs()).length === 2, 'the scoped measure runs once');
    assert.match((await runs())[1][0], /SUMMARIZECOLUMNS\('Date'\[Year\], "Scoped", \[Scoped\]\)/);
    await until(() => browser.evaluate("document.querySelector('[data-testid=query-page]').textContent.includes('42')"), 'the scoped result shows a number');
    // An unavailable query connection must produce the same visible, plain error as pressing Run.
    await browser.evaluate("window.__uishotHarness.setResponse('runDax', {error: 'Connect a model that holds data to run this calculation.'})");
    await browser.evaluate(`window.__uishotHarness.dispatch(${JSON.stringify(nav)})`);
    await until(() => browser.evaluate("document.querySelector('[data-testid=query-page]').textContent.includes('Connect a model that holds data')"), 'the run error is visible');
    assert.equal((await runs()).length, 3);
    // A measure just added by the assistant has no published definition. Its working copy closure travels with the query.
    await browser.evaluate(`window.__uishotHarness.setResponse('listMeasures', [
      {ref:'measure:Sales/Sales YTD (test)',table:'Sales',name:'Sales YTD (test)',expression:"TOTALYTD([New Sales], 'Date'[Date])"},
      {ref:'measure:Sales/New Sales',table:'Sales',name:'New Sales',expression:'SUM(Sales[Amount])'}]);
      window.__uishotHarness.setResponse('runDax', {columns:[{name:'Sales YTD (test)'}],rows:[[42]],rowCount:1,truncated:false,elapsedMs:3});`);
    const newMeasure = commandNavigation('measure:Sales/Sales YTD (test)');
    await browser.evaluate(`window.__uishotHarness.dispatch(${JSON.stringify(newMeasure)})`);
    await until(async () => (await runs()).length === 4, 'the new measure runs once');
    assert.match((await runs())[3][0], /^DEFINE/);
    assert.ok((await runs())[3][0].includes("MEASURE 'Sales'[New Sales] = SUM(Sales[Amount])"));
    assert.ok((await runs())[3][0].includes("MEASURE 'Sales'[Sales YTD (test)] = TOTALYTD([New Sales], 'Date'[Date])"));
    await until(() => browser.evaluate("[...document.querySelectorAll('[data-testid=query-page] [data-testid=figure]')].some(f => f.getClientRects().length && f.textContent.includes('42'))"), 'the new measure shows its number');
    if (process.env.TRY_CALCULATION_SHOT) {
      const shot = await browser.page('Page.captureScreenshot', { format: 'png' });
      writeFileSync(process.env.TRY_CALCULATION_SHOT, Buffer.from(shot.data, 'base64'));
    }
    await browser.evaluate("window.__uishotHarness.setResponse('runDax', {error: 'Query (1, 34) The value cannot be determined. RootActivityId: contoso-id'})");
    await browser.evaluate(`window.__uishotHarness.dispatch(${JSON.stringify(newMeasure)})`);
    await until(() => browser.evaluate("document.querySelector('[data-testid=query-page]').textContent.includes('Ask your assistant to check the formula.')"), 'the new measure gets a plain calculation error');
    const errorText = await browser.evaluate("document.querySelector('[data-testid=query-page]').textContent");
    assert.ok(!errorText.includes('Query (1, 34)'));
    assert.ok(!errorText.includes('RootActivityId'));
  } catch (error) {
    console.error(JSON.stringify(browser.errors.map(e => e.args?.map(a => a.description || a.value) ?? e.exceptionDetails)));
    console.error(await browser.evaluate("JSON.stringify({page: document.querySelector('[data-testid=query-page]')?.textContent, root: document.querySelector('#root')?.innerText, calls: window.__uishotHarness?.messages.filter(m => m.method === 'runDax' || m.method === 'listMeasures')})"));
    if (process.env.TRY_CALCULATION_SHOT) {
      const shot = await browser.page('Page.captureScreenshot', { format: 'png' });
      writeFileSync(process.env.TRY_CALCULATION_SHOT.replace(/\.png$/, '-failure.png'), Buffer.from(shot.data, 'base64'));
    }
    throw error;
  } finally {
    await browser.close();
    await new Promise((ok) => server.close(ok));
  }
});
