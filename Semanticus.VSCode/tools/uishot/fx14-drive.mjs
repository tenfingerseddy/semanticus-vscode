// FX14: real Properties and Checks bundles, one mocked engine, no live model or sign-in.
// TMPDIR=<job>/t OUTDIR=<job>/screens node tools/uishot/fx14-drive.mjs
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const out = resolve(process.env.OUTDIR || join(root, 'tools/uishot/shots/fx14'));
mkdirSync(out, { recursive: true });
const server = createServer((req, res) => {
  const path = (req.url || '/').split('?')[0];
  if (path === '/fx14.html') {
    res.setHeader('content-type', 'text/html');
    res.end('<html><body style="margin:0;background:#1e1e1e;display:grid;grid-template-columns:420px 1fr;height:100vh;gap:1px"><iframe id="props" title="Properties" style="width:100%;height:100%;border:0" src="/tools/uishot/propgrid.html?s=empty"></iframe><iframe id="checks" title="Checks" style="width:100%;height:100%;border:0" src="/tools/uishot/harness.html?conn=1&tier=pro"></iframe></body></html>');
    return;
  }
  const file = resolve(root, '.' + decodeURIComponent(path));
  if (!file.startsWith(root + '/') || !existsSync(file)) { res.writeHead(404); res.end(); return; }
  res.setHeader('content-type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json' })[extname(file)] || 'application/octet-stream');
  res.end(readFileSync(file));
});
await new Promise(done => server.listen(0, '127.0.0.1', done));
const browser = spawn(process.env.SEMANTICUS_BROWSER || '/usr/bin/chromium', [
  '--headless=new', '--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--no-first-run',
  '--disable-background-networking', '--remote-debugging-port=0', '--user-data-dir=' + join(out, 'browser-profile'), 'about:blank',
], { stdio: ['ignore', 'ignore', 'pipe'] });
let socket;
const errors = [];
const delay = ms => new Promise(done => setTimeout(done, ms));
try {
  const endpoint = await new Promise((done, reject) => {
    let buffer = '';
    const timer = setTimeout(() => reject(new Error('Chromium did not start.')), 15000);
    browser.stderr.on('data', chunk => {
      buffer += chunk;
      const match = buffer.match(/DevTools listening on (ws:\/\/[^\s]+)/);
      if (match) { clearTimeout(timer); done(match[1]); }
    });
    browser.once('exit', code => { clearTimeout(timer); reject(new Error('Chromium exited: ' + code)); });
  });
  const targets = await (await fetch(new URL(endpoint).origin.replace('ws:', 'http:') + '/json/list')).json();
  socket = new WebSocket(targets.find(t => t.type === 'page').webSocketDebuggerUrl);
  await new Promise((done, reject) => { socket.onopen = done; socket.onerror = reject; });
  let next = 0;
  const pending = new Map();
  socket.onmessage = event => {
    const msg = JSON.parse(event.data);
    if (msg.method === 'Runtime.exceptionThrown') errors.push(msg.params.exceptionDetails.exception?.description || msg.params.exceptionDetails.text);
    const call = pending.get(msg.id);
    if (call) { pending.delete(msg.id); msg.error ? call.reject(new Error(msg.error.message)) : call.resolve(msg.result); }
  };
  const send = (method, params = {}) => new Promise((done, reject) => {
    const id = ++next; pending.set(id, { resolve: done, reject }); socket.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async expression => {
    const result = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    return result.result?.value;
  };
  const wait = async expression => {
    for (let i = 0; i < 150; i++) { if (await evaluate(expression)) return; await delay(50); }
    throw new Error('The page did not reach: ' + expression);
  };
  await send('Page.enable'); await send('Runtime.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1120, deviceScaleFactor: 1, mobile: false });
  await send('Page.addScriptToEvaluateOnNewDocument', { source: `
    if (window === top) {
      const names = ['Amount', 'ShowValueForDates', ...Array.from({length:10}, (_, i) => 'Amount ' + (i + 2))];
      const order = ['Filters','Contexts','SQL','Trusted number','Equivalence','Totals','Alignment','Performance'];
      const refs = names.map(n => 'measure:Contoso Sales/' + n);
      const state = { checked: false, proposal: null, calls: [] };
      const marks = ref => order.map(name => ({ name, state: state.checked && ref === refs[0] && ['Filters','Contexts'].includes(name) ? 'passed' : 'not run',
        sentence: state.checked && ['Filters','Contexts'].includes(name) ? 'The sampled Contoso values match.' : 'Not run yet.',
        lastRan: state.checked && ref === refs[0] && ['Filters','Contexts'].includes(name) ? state.when : null, cells: [], timings: [], chosenContexts: [] }));
      window.marksEngine = { state, refs, call(method, args) {
        state.calls.push({method,args});
        if (method === 'getMeasureDescription') return { ref: args[0], name: args[0].split('/').pop(), description: '', oneLine: '', provenance: 'person', proposal: state.proposal };
        if (method === 'draftMeasureDescription') return state.proposal = { id: 'contoso', state: 'waiting', message: 'Waiting for your assistant to draft a Description. The wait ends after 60 seconds.' };
        if (method === 'getMeasureMarks') {
          if (args[2]) { state.checked = true; state.when = new Date().toISOString(); }
          return { measureRef: args[0], revision: 1, running: false, marks: marks(args[0]) };
        }
        if (method === 'getMeasureCheckCache') return refs.map(measureRef => ({ measureRef, marks: marks(measureRef), lastChecked: measureRef === refs[0] ? state.when : null }));
        if (method === 'getChecksPage') return { sessionId: 'contoso', revision: 1,
          measures: refs.map((ref, i) => ({ ref, name: names[i], table: 'Contoso Sales', folder: '', description: '', oneLine: '' })),
          summary: { total:12, numbersDiffer:0, keyProblems:0, tableProblems:0 }, numbers: { definitions:[] }, results: {}, resultTimes: {}, tables: { rows:[] } };
        if (method === 'planMeasureRun') return { id:'contoso-plan', totalMeasures:12, batchSize:40, estimatedSeconds:228, firstRun:true,
          picks: refs.map(measureRef => ({ measureRef, group:'oldest', reason:'Never checked.' })) };
        if (method === 'listSqlSources') return [];
        throw new Error('Unexpected marks request: ' + method);
      } };
    } else window.marksEngine = top.marksEngine;
    Object.defineProperty(window, '__uishotHarness', { configurable:true, get:() => window.__h, set:h => {
      window.__h = h;
      h.setResponse('getOrientation', { activeWork:{ workflows:[] } });
      h.setResponse('getWorkflowRun', null);
      h.setResponse('listTests', { definitions:[] });
      h.setResponse('listTableMappings', { rows:[] });
      for (const method of ['getChecksPage','getMeasureCheckCache','getMeasureMarks','getMeasureDescription','draftMeasureDescription','planMeasureRun','listSqlSources'])
        h.setResponse(method, args => window.marksEngine.call(method, args));
    } });
  ` });
  await send('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/fx14.html` });
  await wait("document.querySelector('#props')?.contentWindow.semanticusGrid && [...document.querySelector('#checks')?.contentDocument.querySelectorAll('button') || []].some(b => b.textContent.trim() === 'Review')");
  await evaluate(`window.p = document.querySelector('#props').contentWindow; window.c = document.querySelector('#checks').contentWindow;
    window.load = { type:'load', key:JSON.stringify([marksEngine.refs[0]]), model:'Contoso.bim', name:'Amount', multi:false,
      props:[{name:'Name',displayName:'Name',category:'Basic',kind:'string',value:'Amount',description:'Name',options:[],readOnly:false,varies:false}] };
    p.postMessage(load, '*');
    [...c.document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Review').click();`);
  await wait("[...c.document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Checks')");
  await evaluate("[...c.document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Checks').click()");
  await wait("c.document.querySelector('[data-testid=checks-measures]')?.children.length === 12 && p.document.querySelector('[data-testid=mk-summary]')?.textContent.includes('7 not run')");
  await evaluate("[...p.document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Draft a Description').click()");
  await wait("p.document.querySelector('[data-testid=mk-desc-note]')?.textContent.includes('Waiting')");
  const ended = 'Your assistant did not answer in time. No Description was changed. Request it again when your assistant is ready.';
  await evaluate(`marksEngine.state.proposal = { id:'contoso',state:'timedOut',message:${JSON.stringify(ended)} }; p.postMessage(load, '*')`);
  await wait(`p.document.querySelector('[data-testid=mk-desc-note]')?.textContent === ${JSON.stringify(ended)}`);
  writeFileSync(join(out, 'properties-wait-ended.png'), Buffer.from((await send('Page.captureScreenshot', { format:'png', clip:{ x:0,y:0,width:420,height:1120,scale:1 } })).data, 'base64'));
  await evaluate("[...p.document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Check it now').click()");
  await wait("p.document.querySelector('[data-testid=mk-summary]')?.textContent === '2 passed, 5 not run'");
  await evaluate("c.postMessage({type:'didChange',payload:{sessionId:'contoso',revision:1,origin:'system',deltas:[]}}, '*')");
  await wait("c.document.querySelector('[data-testid=\"checks-row-measure:Contoso Sales/Amount\"]')?.textContent.includes('2 passed, 5 not run')");
  const details = await evaluate(`(() => {
    const row = c.document.querySelector('[data-testid="checks-row-measure:Contoso Sales/Amount"]');
    const signs = [...c.document.querySelectorAll('.cp-open')].map(b => ({ name:b.querySelector('.cp-name').textContent,
      sign:b.querySelector('.cp-glyph')?.textContent, width:b.querySelector('.cp-glyph')?.getBoundingClientRect().width,
      height:b.getBoundingClientRect().height }));
    return { row:row.textContent, when:p.document.querySelector('[data-testid=mk-last]').textContent,
      plan:c.document.querySelector('[data-testid=plan-sentence]').textContent, signs };
  })()`);
  assert.ok(details.row.includes(details.when.replace(/[.]$/, '')), JSON.stringify(details));
  assert.match(details.plan, /About 4 minutes.*first run/i);
  for (const sign of details.signs) assert.ok(sign.sign && sign.width > 0, JSON.stringify(sign));
  for (const sign of details.signs) assert.ok(sign.height <= 72, 'A row must keep its result readable: ' + JSON.stringify(sign));
  assert.ok(details.signs.some(s => s.name === 'ShowValueForDates' && s.sign === '–'));
  writeFileSync(join(out, 'checks-beside-properties.png'), Buffer.from((await send('Page.captureScreenshot', { format:'png' })).data, 'base64'));
  assert.deepEqual(errors, []);
  writeFileSync(join(out, 'evidence.json'), JSON.stringify(details, null, 2));
  console.log('FX14 headless checks passed. Screenshots: ' + out);
  await send('Browser.close');
} finally {
  socket?.close();
  if (browser.exitCode === null && browser.signalCode === null) browser.kill();
  server.close();
}
