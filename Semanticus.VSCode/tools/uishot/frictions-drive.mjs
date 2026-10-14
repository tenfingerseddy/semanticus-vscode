// Driven proof for friction D6 (launch video, 12 Oct): while the assistant publishes a NEW model, the Publish page shows the target the
// assistant publishes to, and not "Choose a workspace" and the open model's name. The engine says it with two activity events (kind
// publish_target, phase start and end); this drive sends them the way the host relays them and reads the page.
//
//   OUTDIR=<dir> node tools/uishot/frictions-drive.mjs      -> PNGs + results.json in $OUTDIR
//   EXPECT_OLD=1 ...                                         the old bundle: records what it shows without failing the run
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';

requireSupportedNode();
const puppeteer = (await import('puppeteer-core')).default;
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'frictions');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: findBrowser(), headless: true, protocolTimeout: 180000, args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const failures = [];
const expect = (ok, what) => { if (!ok) failures.push(what); };
const results = {};

const page = await browser.newPage();
const errors = [];
page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
await page.setViewport({ width: 1280, height: 760, deviceScaleFactor: 1 });
await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?pub=nodest&tab=deploy`, { waitUntil: 'networkidle0', timeout: 30000 });
await page.waitForSelector('nav button', { timeout: 15000 });
await wait(3500);
const read = () => page.evaluate(() => {
  const sel = document.querySelector('[data-testid="publish-new-ws"]');
  const name = document.querySelector('[data-testid="publish-new-name"]');
  const t = (s) => { const e = document.querySelector(s); return e ? (e.textContent || '').replace(/\s+/g, ' ').trim() : null; };
  return {
    workspaceShown: sel ? (sel.options[sel.selectedIndex]?.textContent ?? null) : t('[data-testid="publish-new-ws-target"]'),
    workspaceDisabled: sel ? sel.disabled : (document.querySelector('[data-testid="publish-new-ws-target"]') ? true : null), name: name ? name.value : null, nameDisabled: name ? name.disabled : null,
    assistantLine: t('[data-testid="publish-assistant-target"]'), button: t('[data-testid="publish-new-go"]'),
  };
});
const send = (phase) => page.evaluate((ph) => window.dispatchEvent(new MessageEvent('message', { data: { type: 'activity', payload: {
  seq: Date.now(), kind: 'publish_target', origin: 'agent', ok: true, target: 'Semanticus Demo', query: 'launch-video-test',
  label: ph === 'start' ? 'Publishing launch-video-test to Semanticus Demo' : 'Finished with launch-video-test in Semanticus Demo',
  result: { phase: ph, workspace: 'Semanticus Demo', name: 'launch-video-test' } } } })), phase);

results.idle = await read();
await page.screenshot({ path: join(OUT, 'd6-1-idle.png') });
await send('start'); await wait(900);
results.assistantPublishing = await read();
await page.screenshot({ path: join(OUT, 'd6-2-assistant-publishing.png') });
await send('end'); await wait(700);
results.afterEnd = await read();
await page.screenshot({ path: join(OUT, 'd6-3-after-end.png') });

if (!process.env.EXPECT_OLD) {
  const a = results.assistantPublishing;
  expect(a.workspaceShown === 'Semanticus Demo', `the workspace shown while publishing: ${JSON.stringify(a.workspaceShown)}`);
  expect(a.name === 'launch-video-test', `the name shown while publishing: ${JSON.stringify(a.name)}`);
  expect(/Your assistant is publishing launch-video-test to Semanticus Demo\./.test(a.assistantLine || ''), `the line: ${JSON.stringify(a.assistantLine)}`);
  expect(a.workspaceDisabled === true && a.nameDisabled === true, 'the form is held while the assistant publishes');
  expect(results.afterEnd.assistantLine === null && results.afterEnd.nameDisabled === false, 'the form is free again at the end');
  expect(!/[–—]/.test(JSON.stringify(a)), 'no dash on screen');
  expect(errors.length === 0, `page errors: ${errors.join(' | ')}`);
}
results.errors = errors; results.failures = failures;
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
await browser.close(); server.close();
console.log(JSON.stringify(results, null, 1));
if (failures.length) { console.error('FAILED: ' + failures.join(' | ')); process.exit(1); }
console.log('frictions-drive: ok');
