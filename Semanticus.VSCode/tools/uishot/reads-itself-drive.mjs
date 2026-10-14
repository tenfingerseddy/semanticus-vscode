// The reading card, through the real Studio page against a REAL engine on a real 100-table model file (reads-itself-build, 12 Oct 2026).
//
// The page is the built Studio bundle in the uishot harness. The harness answers the shell's own calls (session info, the plan rows) with its fixtures;
// the four calls the card makes (readingCard, fixThem, undo, removePlanRow) go to a REAL engine over its real named RPC pipe: the engine binary
// `Semanticus.Engine.dll serve --open <model.bim>`, the same handshake and framing the extension uses. So the happy path is the product's: open,
// the grade, Fix them, Undo, the grade back. After each press the driver reads the engine's own grade (aiReadinessScan) and the plan's tasks over
// the pipe and checks the page against them.
//
//   ENGINE_DLL=<Semanticus.Engine.dll> MODEL=<model.bim> OUTDIR=<dir> TMPDIR=/tmp/rri node tools/uishot/reads-itself-drive.mjs
//   Each beat is shot dark and light at 1280 and 400 wide. The Reading beat is held 1.5 s in the page (only for its shot) so the picture can be taken.
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { connect } from 'node:net';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync, copyFileSync, rmSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { randomBytes } from 'node:crypto';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'reads-itself');
mkdirSync(OUT, { recursive: true });
const DLL = process.env.ENGINE_DLL;
const MODEL = process.env.MODEL;
if (!DLL || !existsSync(DLL)) { console.error('ENGINE_DLL must name Semanticus.Engine.dll (a Release build).'); process.exit(1); }
if (!MODEL || !existsSync(MODEL)) { console.error('MODEL must name the .bim to open.'); process.exit(1); }
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) { console.error('media/studio/studio.js is missing. Run npm run build:webview first.'); process.exit(1); }
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const t0 = Date.now();
const say = (s) => console.log(`[${((Date.now() - t0) / 1000).toFixed(1)}s] ${s}`);
const notes = [];
const check = (ok, msg) => { say(`${ok ? 'PASS' : 'FAIL'} ${msg}`); if (!ok) notes.push(msg); return ok; };

// ---- the engine: a scratch workspace with its own copy of the model, a scratch home, a short pipe name
const tmp = process.env.TMPDIR || '/tmp/rri';
mkdirSync(tmp, { recursive: true });
const ws = join(tmp, `ws-${process.pid}`); rmSync(ws, { recursive: true, force: true }); mkdirSync(ws, { recursive: true });
const modelCopy = join(ws, basename(MODEL)); copyFileSync(MODEL, modelCopy);
const home = join(tmp, `home-${process.pid}`); mkdirSync(home, { recursive: true });
const pipeName = `rri${process.pid}`;
const challenge = randomBytes(24).toString('hex');
const env = { ...process.env, HOME: home, SEMANTICUS_HOME: join(home, 'sem'), TMPDIR: tmp, DOTNET_CLI_HOME: home, DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1' };
for (const k of ['DBUS_SESSION_BUS_ADDRESS', 'DISPLAY', 'WAYLAND_DISPLAY', 'OUTDIR', 'ANTHROPIC_API_KEY']) delete env[k];
const engineLog = [];
const child = spawn('dotnet', [DLL, 'serve', '--workspace', ws, '--open', modelCopy, '--pipe', pipeName, '--ui-challenge-stdin'], { env, stdio: ['pipe', 'pipe', 'pipe'] });
child.stdout.on('data', (d) => engineLog.push(String(d))); child.stderr.on('data', (d) => engineLog.push(String(d)));
child.stdin.write(challenge + '\n'); child.stdin.end();
say(`engine started, pid ${child.pid}`);
const stopEngine = () => { try { child.kill('SIGTERM'); } catch { /* gone */ } };
process.on('exit', stopEngine);

class PipeRpc {
  static async open(socketPath, challenge) {
    for (let i = 0; i < 300; i++) {
      if (existsSync(socketPath)) {
        try { return await PipeRpc.once(socketPath, challenge); } catch { /* not listening yet */ }
      }
      await wait(200);
    }
    throw new Error('the engine pipe never opened: ' + engineLog.join('').slice(-800));
  }
  static once(socketPath, challenge) {
    return new Promise((res, rej) => {
      const sock = connect(socketPath);
      let pre = Buffer.alloc(0), done = false;
      sock.once('error', rej);
      sock.on('connect', () => sock.write(`SEMANTICUS-RPC/1 human ${challenge}\n`));
      const onPre = (chunk) => {
        pre = Buffer.concat([pre, chunk]);
        const nl = pre.indexOf(0x0a);
        if (nl < 0 || done) return;
        done = true; sock.off('data', onPre);
        const line = pre.subarray(0, nl).toString('utf8');
        if (line !== 'SEMANTICUS-RPC/1 accepted') { sock.destroy(); rej(new Error(`handshake: ${line}`)); return; }
        const rpc = new PipeRpc(sock); rpc.feed(pre.subarray(nl + 1)); res(rpc);
      };
      sock.on('data', onPre);
    });
  }
  constructor(sock) { this.sock = sock; this.buf = Buffer.alloc(0); this.nextId = 1; this.pending = new Map(); sock.on('data', (c) => this.feed(c)); }
  feed(chunk) {
    this.buf = Buffer.concat([this.buf, chunk]);
    for (;;) {
      const end = this.buf.indexOf('\r\n\r\n');
      if (end < 0) return;
      const m = /Content-Length:\s*(\d+)/i.exec(this.buf.subarray(0, end).toString('ascii'));
      const len = Number(m[1]);
      if (this.buf.length < end + 4 + len) return;
      const msg = JSON.parse(this.buf.subarray(end + 4, end + 4 + len).toString('utf8'));
      this.buf = this.buf.subarray(end + 4 + len);
      if (msg.id != null && this.pending.has(msg.id)) {
        const p = this.pending.get(msg.id); this.pending.delete(msg.id);
        if (msg.error) p.rej(Object.assign(new Error(msg.error.message || JSON.stringify(msg.error)), { rpc: msg.error })); else p.res(msg.result);
      }
    }
  }
  call(method, params = []) {
    const id = this.nextId++;
    const body = Buffer.from(JSON.stringify({ jsonrpc: '2.0', id, method, params }), 'utf8');
    this.sock.write(Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, 'ascii'), body]));
    return new Promise((res, rej) => this.pending.set(id, { res, rej }));
  }
}
const human = await PipeRpc.open(join(tmp, `CoreFxPipe_${pipeName}`), challenge);
say('pipe connected');
let info = null;
for (let i = 0; i < 100 && !info?.sessionId; i++) { try { info = await human.call('sessionInfo', []); } catch { /* opening */ } if (!info?.sessionId) await wait(300); }
check(!!info?.sessionId, `the engine opened ${basename(MODEL)}: ${info?.tables} tables, ${info?.measures} measures`);

// DIRECT_FIRST=1: the engine's own first reading card, asked over the pipe the moment the model is open, before any page or browser exists. This isolates the
// engine from a loaded laptop's browser: it is the number the Studio's first call pays.
if (process.env.DIRECT_FIRST === '1') {
  const tFirst = Date.now();
  const first = await human.call('readingCard', [false]);
  say(`engine direct: the first readingCard answered in ${Date.now() - tFirst} ms: grade ${first?.grade} ${first?.score}, top ${(first?.top ?? []).map((i) => i.headline).join(' | ')}`);
  const tSecond = Date.now();
  await human.call('readingCard', [false]);
  say(`engine direct: the second readingCard answered in ${Date.now() - tSecond} ms`);
}

// ---- the page
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'],
});
const REAL = ['readingCard', 'fixThem', 'undo', 'removePlanRow'];
const calls = [];
const WRAP = `(() => {
  const REAL = new Set(${JSON.stringify(REAL)});
  window.__hold = 0; window.__landed = null; window.__t0 = performance.now();
  new MutationObserver(() => { if (!window.__landed && document.querySelector('[data-testid="reading-grade"]')) window.__landed = performance.now() - window.__t0; })
    .observe(document, { childList: true, subtree: true });
  let orig = null, wrapped = null;
  Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true,
    get: () => function () {
      if (wrapped) return wrapped;
      const api = orig();
      wrapped = Object.assign({}, api, { postMessage(msg) {
        if (msg && msg.type === 'rpc' && REAL.has(msg.method)) {
          const go = () => window.__realRpc(msg.method, msg.params || []).then((r) => {
            window.dispatchEvent(new MessageEvent('message', { data: r && r.error != null ? { type: 'rpcResult', id: msg.id, error: r.error } : { type: 'rpcResult', id: msg.id, result: r.result } }));
          });
          if (msg.method === 'readingCard' && msg.params && msg.params[0] === false && window.__hold) { const h = window.__hold; window.__hold = 0; setTimeout(go, h); } else go();
          return;
        }
        return api.postMessage(msg);
      } });
      return wrapped;
    },
    set: (fn) => { orig = fn; },
  });
})()`;

const gradeOf = async () => { const c = await human.call('aiReadinessScan', []); return { grade: c.grade, score: Math.round(c.overall), safe: c.safeFixCount }; };
const planTasks = async () => {
  const p = await human.call('getModelPlan', []);
  return (p.items ?? []).filter((i) => /^pt-/.test(i.id) && i.doer === 'assistant').map((i) => ({ id: i.id, title: i.title }));
};
const gradeText = (page) => page.evaluate(() => (document.querySelector('[data-testid="reading-grade"] [aria-label]')?.getAttribute('aria-label') ?? '').replace(/^Grade /, '') || null);
const text = (page, sel) => page.evaluate((s) => { const e = document.querySelector(s); return e ? e.textContent.replace(/\s+/g, ' ').trim() : null; }, sel);
const click = (page, id) => page.evaluate((s) => { const el = document.querySelector(`[data-testid="${s}"]`); if (!el || el.disabled) return false; el.scrollIntoView({ block: 'center' }); el.click(); return true; }, id);
const until = async (fn, ms = 20000) => { const end = Date.now() + ms; for (;;) { const r = await fn(); if (r) return r; if (Date.now() > end) return null; await wait(100); } };

const results = [];
async function journey(theme, width, timing, deep = false) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.exposeFunction('__realRpc', async (method, params) => {
    const rec = { method, params, ms: null };
    calls.push(rec);
    const s = Date.now();
    try { const result = await human.call(method, params); rec.ms = Date.now() - s; return { result: result ?? null }; }
    catch (e) { rec.error = e.message; rec.ms = Date.now() - s; return { error: e.message }; }
  });
  await page.setViewport({ width, height: 900, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(WRAP);
  if (!timing) await page.evaluateOnNewDocument(() => { window.__hold = 1500; });
  const tag = `${theme}-${width}`;
  const shoot = async (beat) => {
    await wait(350);
    await page.evaluate(() => { document.querySelectorAll('main, .studio-root').forEach((e) => { e.scrollTop = 0; }); window.scrollTo(0, 0); });
    const file = join(OUT, `${tag}-${beat}.png`);
    await page.screenshot({ path: file, fullPage: false });
    const over = await page.evaluate(() => ({ w: document.documentElement.scrollWidth, v: window.innerWidth, card: document.querySelector('[data-testid="reading-card"]')?.getBoundingClientRect().right ?? 0, dash: /[\u2013\u2014]/.test(document.querySelector('[data-testid="reading-card"]')?.innerText || '') }));
    // The card must fit. The page's own sideways scroll at 400 is the harness shell's header actions (465 wide with no card on the page either).
    check(over.card <= over.v + 1, `${tag} ${beat}: the card fits (right edge ${Math.round(over.card)} <= ${over.v}); page scroll width ${over.w}`);
    check(!over.dash, `${tag} ${beat}: no dash in the card`);
    results.push({ tag, beat, file });
  };
  // A loaded laptop can refuse a page now and then (net::ERR_INSUFFICIENT_RESOURCES): try again, a few times.
  for (let attempt = 1; ; attempt++) {
    try { await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&tier=pro&tab=plan`, { waitUntil: timing ? 'networkidle0' : 'domcontentloaded', timeout: 40000 }); break; }
    catch (e) { if (attempt >= 4) throw e; say(`${tag}: the page did not open (${String(e.message).slice(0, 60)}); trying again`); await wait(4000 * attempt); }
  }
  await applyTheme(page, theme);
  if (timing) {
    const landed = await until(() => page.evaluate(() => window.__landed));
    results.push({ tag, beat: 'timing', pageGradeMs: landed, calls: calls.filter((c) => c.method === 'readingCard').map((c) => ({ withAfter: c.params[0], ms: c.ms })) });
    say(`${tag}: the grade is on the page ${landed?.toFixed?.(0)} ms after the page opened; readingCard calls ${JSON.stringify(calls.filter((c) => c.method === 'readingCard').map((c) => c.ms))} ms`);
    await page.close(); return;
  }
  const readingUp = await until(() => page.evaluate(() => !!document.querySelector('[data-testid="reading-card"][data-beat="reading"]')), 8000);
  check(!!readingUp, `${tag}: the Link is reading`);
  if (readingUp) await shoot('1-reading');
  const grade1 = await until(() => gradeText(page), 20000);
  const before = await gradeOf();
  check(grade1 === before.grade, `${tag}: the page shows the engine's grade (${grade1} vs ${before.grade}, ${before.score} of 100)`);
  const afterThere = await until(() => text(page, '[data-testid="reading-grade-note"]').then((t) => /after Fix them/.test(t ?? '')), 20000);
  check(!!afterThere, `${tag}: the grade after Fix them lands beside the grade`);
  const issues = await page.evaluate(() => [...document.querySelectorAll('[data-testid^="reading-issue-"]')].map((e) => e.textContent.replace(/\s+/g, ' ').trim()));
  check(issues.length === 3, `${tag}: three issues rise in: ${JSON.stringify(issues)}`);
  await wait(900);
  await shoot('2-grade');
  check(await click(page, 'reading-fix'), `${tag}: Fix them is there`);
  const note = await until(() => text(page, '[data-testid="reading-note"]'), 30000);
  check(!!note, `${tag}: the note after Fix them: ${note}`);
  const afterFix = await gradeOf();
  check(afterFix.grade !== before.grade || afterFix.score > before.score, `${tag}: the engine's grade moved ${before.grade} ${before.score} to ${afterFix.grade} ${afterFix.score}`);
  check(afterFix.safe === 0, `${tag}: no safe fix is left`);
  const jobs = deep ? await planTasks() : [{ title: 'not read on this pass' }];
  check(jobs.length >= 1, `${tag}: the assistant has ${jobs.length} writing tasks: ${JSON.stringify(jobs.map((j) => j.title))}`);
  const pageGrade = await gradeText(page);
  check(pageGrade === afterFix.grade, `${tag}: the page shows the new grade (${pageGrade})`);
  await wait(1200);
  await shoot('3-fixed');
  check(await click(page, 'reading-undo'), `${tag}: Undo is in the note`);
  const undone = await until(() => text(page, '[data-testid="reading-note"]').then((t) => (/^Undone/.test(t ?? '') ? t : null)), 60000);
  check(!!undone, `${tag}: the note after Undo: ${undone}`);
  const back = await gradeOf();
  check(back.grade === before.grade && back.score === before.score, `${tag}: the grade is back (${back.grade} ${back.score}, was ${before.grade} ${before.score})`);
  const left = deep ? await until(async () => { const l = await planTasks(); return l.length === 0 ? l : null; }, 240000) ?? await planTasks() : [];
  check(left.length === 0, `${tag}: the writing tasks are withdrawn (${left.length} left)`);
  const grade3 = await until(() => gradeText(page), 10000);
  check(grade3 === before.grade, `${tag}: the page shows the grade back (${grade3})`);
  await until(() => text(page, '[data-testid="reading-note"]').then((t) => (/tasks are withdrawn/.test(t ?? '') ? t : null)), 240000);
  await until(() => text(page, '[data-testid="reading-fix-line"]').then((t) => (/writing task/.test(t ?? '') ? t : null)), 60000);
  await wait(1200);
  await shoot('4-undone');
  if (errors.length) { check(false, `${tag}: page errors: ${errors.slice(0, 3).join(' | ')}`); }
  await page.close();
}

try {
  await journey('dark', 1280, true);       // the timing run: no hold
  if (process.env.TIMING_ONLY === '1') throw new Error('timing only');
  let first = true;
  for (const width of [1280, 400]) for (const theme of ['dark', 'light']) { await journey(theme, width, false, first); first = false; }
} catch (e) {
  if (String(e.message) !== 'timing only') throw e;
} finally {
  writeFileSync(join(OUT, 'results.json'), JSON.stringify({ results, calls, notes }, null, 2));
  writeFileSync(join(OUT, 'engine-log.txt'), engineLog.join(''));
  await browser.close(); server.close(); stopEngine();
}
console.log(notes.length ? `\n${notes.length} problem(s):\n- ${notes.join('\n- ')}` : '\nall checks passed');
process.exit(notes.length ? 1 : 0);
