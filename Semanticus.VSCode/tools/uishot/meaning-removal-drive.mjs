// Render the retained shells in local headless Chromium. No model or account is used.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url)), root = resolve(here, '../..');
const out = resolve(process.env.OUTDIR || join(here, 'shots/meaning-removal'));
mkdirSync(out, { recursive: true });
const server = createServer((req, res) => {
  const file = resolve(root, '.' + decodeURIComponent((req.url || '/').split('?')[0]));
  if (!file.startsWith(root + '/') || !existsSync(file)) { res.writeHead(404); res.end(); return; }
  res.setHeader('content-type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json' })[extname(file)] || 'application/octet-stream');
  res.end(readFileSync(file));
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const browser = spawn(process.env.SEMANTICUS_BROWSER || '/usr/bin/chromium', [
  '--headless=new', '--no-sandbox', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
  '--disable-background-networking', '--remote-debugging-port=0', '--user-data-dir=' + join(out, 'browser-profile'), 'about:blank',
], { stdio: ['ignore', 'ignore', 'pipe'] });
const delay = ms => new Promise(r => setTimeout(r, ms));
const errors = [], results = [];
let socket;
try {
  const endpoint = await new Promise((resolve, reject) => {
    let buffer = '';
    const timer = setTimeout(() => reject(new Error('Chromium did not start its local debug endpoint.')), 15000);
    browser.stderr.on('data', chunk => {
      buffer += chunk;
      const found = buffer.match(/DevTools listening on (ws:\/\/[^\s]+)/);
      if (found) { clearTimeout(timer); resolve(found[1]); }
    });
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
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const seq = ++id; pending.set(seq, { resolve, reject }); socket.send(JSON.stringify({ id: seq, method, params }));
  });
  const evaluate = async expression => {
    const value = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (value.exceptionDetails) throw new Error(value.exceptionDetails.exception?.description || value.exceptionDetails.text);
    return value.result?.value;
  };
  const wait = async expression => {
    for (let attempt = 0; attempt < 300; attempt++) {
      if (await evaluate(expression)) return;
      await delay(100);
    }
    throw new Error('The page did not reach: ' + expression);
  };
  const click = label => evaluate(`(() => { const b = [...document.querySelectorAll('button')].find(b => b.textContent.trim() === ${JSON.stringify(label)}); if (!b) throw new Error('Missing button: ' + ${JSON.stringify(label)}); b.click(); })()`);
  await send('Page.enable'); await send('Runtime.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 900, deviceScaleFactor: 1, mobile: false });
  await send('Page.addScriptToEvaluateOnNewDocument', { source: `
    let harness;
    const data = { sessionId: 'Contoso', revision: 1,
      measures: ['Sales Tailspin Toys', 'Total Cost', 'Sales YTD'].map(name => ({ ref: 'measure:Sales/' + name, name, description: 'Contoso sales after discounts.' })),
      summary: { total: 3, numbersDiffer: 0, keyProblems: 0, tableProblems: 0 },
      numbers: { definitions: [] }, results: {}, tables: { rows: [] }, relationships: null };
    Object.defineProperty(window, '__uishotHarness', { configurable: true, get: () => harness, set: h => {
      harness = h;
      h.setResponse('getOrientation', { activeWork: { workflows: [] } });
      h.setResponse('getWorkflowRun', null);
      h.setResponse('getChecksPage', () => structuredClone(data));
      h.setResponse('cleanupMeaningAnnotations', () => ({ measures: 3, annotations: 6, note: 'Removed 6 old meaning annotations from 3 measures. Undo restores them. Description stays the same.' }));
      h.setResponse('listTests', { definitions: [] });
      h.setResponse('listTableMappings', { rows: [] });
    } });` });
  await send('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/tools/uishot/harness.html?conn=1&tier=pro` });
  await wait("[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Review')");
  await click('Review'); await click('Checks');
  await wait("!!document.querySelector('[data-testid=checks-page]')");
  const capture = async name => {
    await evaluate('document.fonts.ready'); await delay(100);
    const state = await evaluate(`({ text: document.querySelector('[data-testid=checks-page]')?.innerText || document.body.innerText,
      width: innerWidth, docWidth: document.documentElement.scrollWidth,
      names: [...document.querySelectorAll('.cp-name')].map(n => ({ text: n.textContent, width: n.getBoundingClientRect().width })) })`);
    assert.ok(state.docWidth <= state.width, name + ': no sideways scroll');
    assert.ok(state.names.every(n => n.width >= 100), name + ': measure names have readable space');
    const shot = await send('Page.captureScreenshot', { format: 'png' });
    writeFileSync(join(out, name + '.png'), Buffer.from(shot.data, 'base64'));
    results.push({ name, ...state });
  };
  const pageText = await evaluate("document.querySelector('[data-testid=checks-page]').innerText");
  assert.doesNotMatch(pageText, /Write its meaning|Partly checked|\bChecked\b/);
  await capture('checks-1440');
  await evaluate("document.querySelector('[data-testid=\"checks-row-measure:Sales/Sales Tailspin Toys\"] button').click()");
  await wait("!!document.querySelector('.ck-card')");
  assert.match(await evaluate("document.querySelector('.ck-card').innerText"), /Contoso sales after discounts/);
  await capture('card-1440');
  await click('Remove old annotations');
  await wait("document.querySelector('[data-testid=checks-page]').innerText.includes('Undo restores them')");
  const calls = await evaluate("window.__uishotHarness.messages.filter(m => m.method === 'cleanupMeaningAnnotations')");
  assert.equal(calls.length, 1);
  await capture('cleanup-1440');
  await send('Emulation.setDeviceMetricsOverride', { width: 390, height: 900, deviceScaleFactor: 1, mobile: false });
  await capture('checks-390');
  await send('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/tools/uishot/propgrid.html?s=measure` });
  await wait("!!document.querySelector('.hdr')");
  await capture('properties-390');
  assert.deepEqual(errors, []);
  writeFileSync(join(out, 'results.json'), JSON.stringify({ errors, results, cleanupCalls: calls.length }, null, 2));
  console.log(JSON.stringify({ out, states: results.map(r => r.name), errors, cleanupCalls: calls.length }));
} finally {
  socket?.close(); browser.kill('SIGTERM');
  await new Promise(r => server.close(r));
}
