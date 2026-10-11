// VERIFY (m11/uatj8, U1 and U2): the words editor's draft and the per-table sample control, through the real Studio page against a REAL engine.
// Seeded from startplan-real-drive.mjs (the host, the pipes, the page, measure/settle/shoot). Added switches: window.__engineDown (every
// start-plan call answers 'Engine not connected.', the host's answer while no engine is attached), window.__sessId (the session id the
// page is told, so a 'reconnected' after a restart carries a NEW session as a real engine restart does), and holdRelay (live updates held).
//
// The start plan's Free journey against the REAL engine (the wiring step): the same built Studio bundle and harness page
// startplan-drive.mjs drives against its mock, but every start-plan verb the page calls goes to a real LocalEngine over a real
// named RPC pipe, and the engine's startPlan/didChange reaches the page as the extension relays it.
//
// THE HOST. A real engine cannot read a Fabric schema offline, and product code has no backdoor for it, so the engine runs
// inside the test assembly: Semanticus.Tests/StartPlanStudioHostTests.cs, run here as a long-lived child under semslot. It
// writes <host>/pipe.json and serves until <host>/stop exists. Build the tests first (Debug), then:
//
//   TMPDIR=/tmp/rdr OUTDIR=<dir> node tools/uishot/startplan-real-drive.mjs
//   SEMANTICUS_STUDIO_HOST_PIPE=<host dir>   attach to a host already running instead of starting one
//
// THE TWO PIPES. The human pipe is the Studio's (EngineRpcTarget as "human" plus HumanGovernanceRpcTarget, which alone has
// approveStartPlan). The agent pipe is the person's assistant: it writes the words (getStartWords, writeStartWords). The page
// never writes words. Everything else the page asks (entitlement, session info, the saved endpoints) keeps its fixture answer.
//
// WHAT IT ASSERTS. After each step it reads the engine's own view over the human pipe and checks the page against it: the
// sections, the lines in each section, the Needs you lines, the words count, the grade letter, the approval and built marks,
// and that approve and build were sent the digest the engine had when the page showed it.
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { connect } from 'node:net';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync, rmSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { checkScreen } from './screencheck.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const repoRoot = resolve(webRoot, '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'verify-uatj8-real');
mkdirSync(OUT, { recursive: true });
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) { console.error('media/studio/studio.js is missing. Run npm run build:webview in Semanticus.VSCode first.'); process.exit(1); }
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const t0 = Date.now();
const say = (s) => console.log(`[${((Date.now() - t0) / 1000).toFixed(1)}s] ${s}`);

// ---------------------------------------------------------------- the host

const attach = process.env.SEMANTICUS_STUDIO_HOST_PIPE;
const HOST = attach || process.env.HOST_DIR || join(process.env.HOME, '.cache', `sp-real-host-${process.pid}`);
let child = null;
if (!attach) {
  rmSync(HOST, { recursive: true, force: true });
  mkdirSync(HOST, { recursive: true });
  const tmp = process.env.TMPDIR || '/tmp/rdr';
  mkdirSync(tmp, { recursive: true });
  const xdg = join(tmp, `xrd-${process.pid}`); mkdirSync(xdg, { recursive: true });
  const env = { ...process.env, TMPDIR: tmp, XDG_RUNTIME_DIR: xdg, BROWSER: 'true', AZURE_CONFIG_DIR: '/tmp/no-az-login', MSBUILDDISABLENODEREUSE: '1',
    DOTNET_CLI_USE_MSBUILD_SERVER: '0', UseSharedCompilation: 'false', SEMANTICUS_STUDIO_HOST_DIR: HOST, SEMANTICUS_STUDIO_HOST_TIER: process.env.TIER || 'free',
    SEMANTICUS_STUDIO_HOST_MINUTES: process.env.HOST_MINUTES || '20' };
  // OUTDIR is this driver's, and MSBuild reads an OUTDIR variable as the build's output folder: dotnet test then looks for the dll there.
  for (const k of ['DBUS_SESSION_BUS_ADDRESS', 'DISPLAY', 'WAYLAND_DISPLAY', 'OUTDIR']) delete env[k];
  const slot = process.env.SEMSLOT || '/home/tenfingers/.local/bin/semslot';
  const args = ['dotnet', 'test', join(repoRoot, 'Semanticus.Tests', 'Semanticus.Tests.csproj'), '--no-build', '-c', 'Debug', '--filter', 'FullyQualifiedName~StartPlanStudioHost'];
  child = spawn(existsSync(slot) ? slot : args.shift(), existsSync(slot) ? args : args, { env, cwd: repoRoot, stdio: ['ignore', 'pipe', 'pipe'], detached: true });
  const hostLog = [];
  child.stdout.on('data', (d) => hostLog.push(String(d))); child.stderr.on('data', (d) => hostLog.push(String(d)));
  child.on('exit', (code) => { say(`host exited ${code}`); writeFileSync(join(OUT, 'host-stdout.txt'), hostLog.join('')); });
  say(`host started, pid ${child.pid} (semslot may queue until a slot frees)`);
}
const stopHost = async () => {
  if (!child) return;
  writeFileSync(join(HOST, 'stop'), '');
  for (let i = 0; i < 150 && child.exitCode === null && child.signalCode === null; i++) await wait(200);
  if (child.exitCode === null && child.signalCode === null) { say(`host did not stop by itself; killing its group, pid ${child.pid}`); try { process.kill(-child.pid, 'SIGTERM'); } catch { /* gone */ } }
};
const failHard = async (msg) => { console.error(msg); await stopHost(); process.exit(2); };

const pipeFile = join(HOST, 'pipe.json');
for (let i = 0; !existsSync(pipeFile); i++) {
  if (child && child.exitCode !== null) await failHard(`the host exited (${child.exitCode}) before it wrote ${pipeFile}`);
  if (i > 30 * 60 * 5) await failHard('no pipe.json after 30 minutes');
  if (i % 150 === 0 && i) say('waiting for the host ...');
  await wait(200);
}
const hostInfo = JSON.parse(readFileSync(pipeFile, 'utf8'));
say(`host up: ${hostInfo.tier} on ${hostInfo.socket}`);

// ---------------------------------------------------------------- a JSON-RPC client over the pipe (the handshake, then Content-Length framing)

class PipeRpc {
  static open(socketPath, role, challenge) {
    return new Promise((res, rej) => {
      const sock = connect(socketPath);
      let pre = Buffer.alloc(0), done = false;
      sock.once('error', rej);
      sock.on('connect', () => sock.write(role === 'human' ? `SEMANTICUS-RPC/1 human ${challenge}\n` : 'SEMANTICUS-RPC/1 agent\n'));
      const onPre = (chunk) => {
        pre = Buffer.concat([pre, chunk]);
        const nl = pre.indexOf(0x0a);
        if (nl < 0 || done) return;
        done = true;
        sock.off('data', onPre);
        const line = pre.subarray(0, nl).toString('utf8');
        if (line !== 'SEMANTICUS-RPC/1 accepted') { sock.destroy(); rej(new Error(`handshake ${role}: ${line}`)); return; }
        const rpc = new PipeRpc(sock, role);
        rpc.feed(pre.subarray(nl + 1));
        res(rpc);
      };
      sock.on('data', onPre);
    });
  }
  constructor(sock, role) {
    this.sock = sock; this.role = role; this.buf = Buffer.alloc(0); this.nextId = 1; this.pending = new Map(); this.listeners = new Map();
    sock.on('data', (c) => this.feed(c));
    sock.on('close', () => { for (const p of this.pending.values()) p.rej(new Error('pipe closed')); this.pending.clear(); });
  }
  feed(chunk) {
    this.buf = Buffer.concat([this.buf, chunk]);
    for (;;) {
      const end = this.buf.indexOf('\r\n\r\n');
      if (end < 0) return;
      const m = /Content-Length:\s*(\d+)/i.exec(this.buf.subarray(0, end).toString('ascii'));
      if (!m) throw new Error('no Content-Length in ' + this.buf.subarray(0, end).toString('ascii'));
      const len = Number(m[1]);
      if (this.buf.length < end + 4 + len) return;
      const msg = JSON.parse(this.buf.subarray(end + 4, end + 4 + len).toString('utf8'));
      this.buf = this.buf.subarray(end + 4 + len);
      if (msg.id != null && this.pending.has(msg.id)) {
        const p = this.pending.get(msg.id); this.pending.delete(msg.id);
        if (msg.error) p.rej(Object.assign(new Error(msg.error.message || JSON.stringify(msg.error)), { rpc: msg.error })); else p.res(msg.result);
      } else if (msg.method) for (const fn of this.listeners.get(msg.method) || []) fn(msg.params);
    }
  }
  call(method, params = []) {
    const id = this.nextId++;
    const body = Buffer.from(JSON.stringify({ jsonrpc: '2.0', id, method, params }), 'utf8');
    this.sock.write(Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, 'ascii'), body]));
    return new Promise((res, rej) => this.pending.set(id, { res, rej }));
  }
  on(method, fn) { this.listeners.set(method, [...(this.listeners.get(method) || []), fn]); }
  close() { try { this.sock.destroy(); } catch { /* gone */ } }
}

const human = await PipeRpc.open(hostInfo.socket, 'human', hostInfo.challenge);
const agent = await PipeRpc.open(hostInfo.socket, 'agent');
say('both pipes connected');

// ---------------------------------------------------------------- the page

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

// The verbs that go to the real engine. approveStartPlan is on the human pipe only (HumanGovernanceRpcTarget).
const REAL = ['startModelFromSql', 'getStartPlan', 'editStartPlan', 'grantStartDisclosure', 'approveStartPlan', 'buildStartPlan', 'discardStartPlan', 'getConsent', 'setConsent', 'writeStartWords'];
const calls = [];          // every call the page made to the engine: method, params, the engine's digest when it was sent, the answer's digest
let served = null;         // the last view the page was answered with
const digestOf = (r) => (r && typeof r === 'object' ? (r.digest ?? r.view?.digest ?? null) : null);

// The webview's acquireVsCodeApi is wrapped before the harness defines it: a start-plan rpc is answered by the real engine,
// asynchronously, as the extension relays it; anything else goes to the harness's fixture answers unchanged.
const WRAP = `(() => {
  const REAL = new Set(${JSON.stringify(REAL)});
  window.__sessIds = new Set(); window.__engineDown = false; window.__sessId = null;
  // A restarted engine opens the model under a NEW session id: the page is told that id on its next getSessionInfo.
  window.addEventListener('message', (e) => { const d = e.data; if (d && d.type === 'rpcResult' && window.__sessIds.has(d.id) && window.__sessId && d.result && typeof d.result === 'object') d.result.sessionId = window.__sessId; }, true);
  let orig = null, wrapped = null;
  Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true,
    get: () => function () {
      if (wrapped) return wrapped;
      const api = orig();
      wrapped = Object.assign({}, api, { postMessage(msg) {
        if (msg && msg.type === 'rpc' && msg.method === 'getSessionInfo') window.__sessIds.add(msg.id);
        if (msg && msg.type === 'rpc' && window.__engineDown) {
          setTimeout(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'rpcResult', id: msg.id, error: 'Engine not connected.' } })), 5);
          return;
        }
        if (msg && msg.type === 'rpc' && REAL.has(msg.method)) {
          window.__realRpc(msg.method, msg.params || []).then((r) => {
            window.dispatchEvent(new MessageEvent('message', { data: r && r.error != null ? { type: 'rpcResult', id: msg.id, error: r.error } : { type: 'rpcResult', id: msg.id, result: r.result } }));
          });
          return;
        }
        return api.postMessage(msg);
      } });
      return wrapped;
    },
    set: (fn) => { orig = fn; },
  });
})()`;

const page = await browser.newPage();
const errors = [];
page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
await page.exposeFunction('__realRpc', async (method, params) => {
  let before = null;
  try { before = (await human.call('getStartPlan', []))?.digest ?? null; } catch { /* no plan yet */ }
  const rec = { method, params, engineDigestBefore: before };
  calls.push(rec);
  try {
    const result = await human.call(method, params);
    rec.answerDigest = digestOf(result);
    if (method === 'writeStartWords') rec.answer = { written: result?.written, refused: result?.refused };
    if (method === 'writeStartWords' && result?.view) served = result.view;
    if (method === 'getStartPlan' || method === 'startModelFromSql' || method === 'editStartPlan' || method === 'approveStartPlan' || method === 'grantStartDisclosure') served = result;
    if (method === 'buildStartPlan' && result?.view) served = result.view;
    return { result: result ?? null };
  } catch (e) {
    rec.error = e.message;
    return { error: e.message };
  }
});
let holdRelay = false; const held = [];
human.on('startPlan/didChange', (v) => {
  if (holdRelay) { held.push(v); return; }
  void page.evaluate((p) => { window.dispatchEvent(new MessageEvent('message', { data: { type: 'startPlanDidChange', payload: p } })); }, v).catch(() => {});
});
await page.setViewport({ width: 1280, height: 1100, deviceScaleFactor: 1 });
await page.evaluateOnNewDocument(WRAP);
const tierQ = hostInfo.tier === 'pro' ? '&tier=pro' : '';
await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&spec=empty&coldNav=spec:start:sql${tierQ}`, { waitUntil: 'networkidle0', timeout: 30000 });
await wait(1500);

// ---------------------------------------------------------------- measuring: the page, and the page against the engine

const MEASURE = `(() => {
  const q = (s) => document.querySelector(s);
  const text = (e) => (e ? e.textContent.replace(/\\s+/g, ' ').trim() : null);
  const all = (s) => [...document.querySelectorAll(s)];
  const pg = q('[data-testid="sp-page"]');
  return {
    onPlan: !!pg, onForm: !!q('[data-testid="sp-form"]'), formError: text(q('[data-testid="sp-form-error"]')),
    approval: pg?.dataset.approval ?? null, decide: pg?.dataset.decide ?? null, built: pg?.dataset.built ?? null,
    error: text(q('[data-testid="sp-error"]')), said: text(q('[data-testid="sp-said"]')), foot: text(q('[data-testid="sp-foot"]')),
    strip: text(q('[data-testid="sp-strip"]')), grade: text(q('[data-testid="sp-grade"]')), gradeProvisional: q('[data-testid="sp-grade"]')?.dataset.provisional ?? null,
    facts: text(q('[data-testid="sp-facts"]')), head: text(q('.sp-head')),
    needs: all('[data-testid^="sp-need-"][data-kind]').map((e) => ({ id: e.dataset.testid.replace('sp-need-', ''), kind: e.dataset.kind, text: text(e), options: [...e.querySelectorAll('button')].map((b) => b.textContent.trim()) })),
    sections: all('[data-section]').map((s) => ({ id: s.dataset.section, title: text(s.querySelector('.sp-sec-h b')),
      lines: [...s.querySelectorAll('[data-group]')].reduce((n, g) => { const h = g.querySelector('.sp-grp-r'); return n + (h ? parseInt(h.textContent, 10) : g.querySelectorAll('.sp-row').length); }, 0) })),
    rows: all('.sp-row').length, rowMarks: all('.sp-row').map((r) => [...r.querySelectorAll('.sp-who')].some((w) => w.getBoundingClientRect().width > 0)),
    buttons: all('[data-testid^="sp-approve"],[data-testid^="sp-build"]').map((b) => ({ id: b.dataset.testid, text: b.textContent.trim(), disabled: b.disabled })),
    discard: !!q('[data-testid="sp-discard"]'), filters: all('[data-testid^="sp-filter-"]').map((b) => b.textContent.trim()),
    allText: document.body.innerText, docWidth: document.documentElement.scrollWidth, viewportWidth: window.innerWidth,
  };
})()`;

const notes = [];
const has = (cond, msg) => { if (!cond) notes.push(msg); return !!cond; };
const results = [];
const engineView = () => human.call('getStartPlan', []);
const linesOf = (sec) => (sec.groups ?? []).reduce((n, g) => n + (g.lines ?? []).length, 0);

/** The page against the engine's own view: every count the page shows must be the view's. */
function compare(step, m, v) {
  const out = [];
  const ck = (c, msg) => { if (!c) out.push(`${step}: ${msg}`); };
  const vs = (v.sections ?? []).map((s) => ({ id: s.id, title: s.title, lines: linesOf(s) }));
  ck(JSON.stringify(m.sections.map((s) => s.id)) === JSON.stringify(vs.map((s) => s.id)), `sections page ${m.sections.map((s) => s.id)} engine ${vs.map((s) => s.id)}`);
  for (const s of vs) {
    const p = m.sections.find((x) => x.id === s.id);
    ck(p && p.lines === s.lines, `section ${s.id}: page shows ${p?.lines} lines, engine has ${s.lines}`);
    ck(p && (!s.title || p.title === s.title), `section ${s.id} title page "${p?.title}" engine "${s.title}"`);
  }
  // A built plan asks nothing: the band is gone by design (NeedsBand returns null once view.built), whatever the view still lists.
  if (v.built) ck(m.needs.length === 0, `needs: a built plan shows ${m.needs.length}`);
  else ck(m.needs.length === (v.needsYou ?? []).length, `needs: page ${m.needs.length} [${m.needs.map((n) => n.kind)}] engine ${(v.needsYou ?? []).length} [${(v.needsYou ?? []).map((n) => n.kind)}]`);
  const w = v.words ?? { total: 0, open: 0 };
  if (w.total) ck(new RegExp(`${w.total - w.open}\\s*of ${w.total}`).test(m.strip ?? ''), `words: engine ${w.total - w.open} of ${w.total}, page strip "${m.strip}"`);
  else ck(/None yet/.test(m.strip ?? ''), `words: engine has none, page strip "${m.strip}"`);
  if (v.grades?.afterBuildGrade) ck((m.grade ?? '').includes(v.grades.afterBuildGrade), `grade: engine ${v.grades.afterBuildGrade}, page "${m.grade}"`);
  ck(m.gradeProvisional === (v.grades?.afterBuildProvisional ? 'yes' : 'no'), `provisional: engine ${v.grades?.afterBuildProvisional}, page ${m.gradeProvisional}`);
  ck(m.built === (v.built ? 'yes' : 'no'), `built: engine ${v.built}, page ${m.built}`);
  ck(m.decide === (v.decide?.state ?? 'off'), `decide: engine ${v.decide?.state}, page ${m.decide}`);
  const approvedNow = !!v.approval && v.approval.digest === v.digest;
  if (!v.built) ck((m.approval === 'current') === approvedNow, `approval: engine ${approvedNow ? 'current' : v.approval ? 'stale' : 'none'}, page ${m.approval}`);
  ck(served?.digest === v.digest, `the page was last answered with digest ${served?.digest}, the engine is at ${v.digest}`);
  ck(m.docWidth <= m.viewportWidth + 1, `the page scrolls sideways: ${m.docWidth} > ${m.viewportWidth}`);
  ck(!(m.allText.match(/[–—]/g) || []).length, 'an em or en dash on screen');
  return out;
}

/** Settle: the engine has stopped moving (the sample read, the preview's grades) and the page has been answered with what it holds. */
async function settle(label, { needSettled = true, ms = 90000 } = {}) {
  const until = Date.now() + ms;
  let v = await engineView(), last = null, same = 0;
  for (;;) {
    v = await engineView();
    // Waiting for the grant, the grades stay provisional until Jev has looked: that is the settled state of a plan nobody has allowed yet.
    const moving = v?.sample?.state === 'reading' || v?.decide?.state === 'deciding' || (needSettled && v?.grades?.afterBuildProvisional && !v?.built && v?.decide?.state !== 'waiting_grant');
    same = last === v?.digest ? same + 1 : 0; last = v?.digest;
    if (!moving && same >= 2 && served?.digest === v?.digest) break;
    if (Date.now() > until) { notes.push(`${label}: did not settle in ${ms / 1000}s (sample ${v?.sample?.state}, decide ${v?.decide?.state}, provisional ${v?.grades?.afterBuildProvisional}, page at ${served?.digest}, engine at ${v?.digest})`); break; }
    // A page that missed a broadcast is not this driver's to fix: nudge nothing, just wait and report.
    await wait(500);
  }
  await wait(600);
  return v;
}

async function shoot(name, v, views = []) {
  await page.evaluate(() => { document.querySelectorAll('main, .studio-root').forEach((e) => { e.scrollTop = 0; }); window.scrollTo(0, 0); });
  await wait(250);
  const m = await page.evaluate(MEASURE);
  const cmp = v ? compare(name, m, v) : [];
  notes.push(...cmp);
  const shots = [];
  for (const view of [null, ...views]) {
    if (view) {
      const found = await page.evaluate((id) => { const el = document.querySelector(`[data-testid="${id}"]`); if (!el) return false; el.scrollIntoView({ block: 'start' }); return true; }, view);
      if (!found) { notes.push(`${name}: nothing to scroll to at ${view}`); continue; }
      await wait(250);
    }
    for (const theme of ['light', 'dark']) {
      await applyTheme(page, theme); await wait(300);
      const file = join(OUT, `real-${hostInfo.tier}-${name}${view ? '-' + view.replace(/^sp-(section-)?/, '') : ''}-1280-${theme}.png`);
      await page.screenshot({ path: file, fullPage: false });
      shots.push(file);
    }
  }
  const screen = await checkScreen(page, `startplan-real-drive:${name}`).catch((e) => ({ failures: [String(e)] }));
  results.push({ step: name, measured: { ...m, allText: undefined }, engine: v ? { digest: v.digest, sections: (v.sections ?? []).map((s) => [s.id, linesOf(s)]), needsYou: (v.needsYou ?? []).map((n) => n.kind), words: v.words, grades: v.grades, decide: v.decide?.state, sample: v.sample, approval: v.approval, canBuild: v.canBuild, blockedBecause: v.blockedBecause, built: v.built } : null,
    mismatches: cmp, shots, screen: screen?.counts ?? null });
  say(`${cmp.length ? '!' : 'ok'} ${name}${cmp.length ? ': ' + cmp.join(' | ') : ''}`);
  return m;
}

// ---------------------------------------------------------------- the journey (Free)

const typeIn = async (sel, value) => { await page.focus(sel); await page.keyboard.type(value, { delay: 3 }); };
const clickId = (id) => page.evaluate((s) => { const el = document.querySelector(`[data-testid="${s}"]`); if (!el) return false; el.scrollIntoView({ block: 'center' }); el.click(); return true; }, id);
const clickText = (scope, label) => page.evaluate((sc, t) => {
  const el = [...document.querySelectorAll(`${sc} button`)].find((b) => b.textContent.replace(/\s+/g, ' ').trim().startsWith(t));
  if (!el) return false; el.scrollIntoView({ block: 'center' }); el.click(); return true;
}, scope, label);
const folder = join(HOST, 'models', 'Contoso Consulting');

// ---------------------------------------------------------------- join 7: the person's line controls and the per-table permission dialog (UAT-J7-S1-002/003, S2-001/002/003/005/006/007)

const pageq = (fn, ...args) => page.evaluate(fn, ...args);
const sectionOfLine = (v, line) => (v.sections ?? []).find((x) => (x.groups ?? []).some((g) => (g.lines ?? []).some((l) => l.item.id === line.item.id)))?.id;
const linesIn = (v, sectionId) => (v.sections ?? []).filter((x) => !sectionId || x.id === sectionId).flatMap((x) => (x.groups ?? []).flatMap((g) => (g.lines ?? []).map((l) => ({ ...l, group: g.table }))));
const expandedRows = () => pageq(() => document.querySelectorAll('.sp-row[aria-expanded="true"]').length);
// Open a row only if its detail is not already open (a second press would close it).
const openRowById = async (id) => { const open = await page.evaluate((i) => !!document.querySelector(`[data-testid="sp-detail-${i}"]`), id); if (!open) await clickId(`sp-row-${id}`); await wait(300); };
// A wide section opens only the tables that need the person: open the table's group when its row is not on the page yet.
const revealRow = async (id, section, group) => { const there = await page.evaluate((i) => !!document.querySelector(`[data-testid="sp-row-${i}"]`), id); if (!there && group) { await clickId(`sp-group-${section}-${group}`); await wait(300); } };

// ---------------------------------------------------------------- VERIFY scenes

const setText = async (sel, value) => { await page.evaluate((q) => { const e = document.querySelector(q); e.focus(); e.select(); }, sel); await page.keyboard.type(value, { delay: 2 }); };
const press = async (id) => { for (let i = 0; i < 60; i++) { const ok = await page.evaluate((s) => { const el = document.querySelector(`[data-testid="${s}"]`); if (!el || el.disabled) return false; el.scrollIntoView({ block: 'center' }); el.click(); return true; }, id); if (ok) return true; await wait(250); } return false; };
const ED = () => page.evaluate(() => {
  const q = (s) => document.querySelector(s);
  const ed = q('[data-testid="sp-words-editor"]');
  return { open: !!ed, value: q('[data-testid="sp-words-input"]')?.value ?? null, moved: !!q('[data-testid="sp-words-moved"]'),
    theirs: q('[data-testid="sp-words-theirs"]')?.textContent ?? null, said: q('[data-testid="sp-words-said"]')?.textContent ?? null,
    saveDisabled: q('[data-testid="sp-words-save"]')?.disabled ?? null, onPlan: !!q('[data-testid="sp-page"]'), topError: q('[data-testid="sp-error"]')?.textContent ?? null };
});
const verdicts = [];
const verdict = (scene, ok, detail) => { verdicts.push({ scene, ok, detail }); say(`${ok ? 'PASS' : 'FAIL'} ${scene}: ${detail}`); if (!ok) notes.push(`${scene}: ${detail}`); };
const freshLines = (v, used) => linesIn(v, 'words').filter((l) => l.kept && l.word != null && l.decidedBy === 'assistant' && l.item.kind === 'set_description' && l.item.objectRef && !used.has(l.item.id));
const used = new Set();
async function openWords(l) {
  await revealRow(l.item.id, 'words', l.group);
  await openRowById(l.item.id);
  const ok = await press('sp-words');
  await wait(300);
  return ok;
}
const lineNow = async (id) => linesIn(await engineView()).find((x) => x.item.id === id);
const agentWrite = (l, text) => agent.call('writeStartWords', [[{ ref: l.item.objectRef, kind: 'description', text }], 'agent']);
const waitFor = async (fn, ms = 8000) => { const until = Date.now() + ms; for (;;) { const r = await fn(); if (r || Date.now() > until) return r; await wait(200); } };

async function sceneConflictKeepMine(v) {
  const l = freshLines(v, used)[0]; used.add(l.item.id);
  await openWords(l);
  await setText('[data-testid="sp-words-input"]', 'Draft C typed by the person.');
  const w = await agentWrite(l, 'Words B from the assistant.');
  const e1 = await waitFor(async () => { const e = await ED(); return e.moved ? e : null; });
  const e = e1 ?? await ED();
  await shoot('u1-conflict-live', null, ['sp-words-editor']);
  verdict('U1 conflict: live update under a typed draft', !!e1 && e.value === 'Draft C typed by the person.' && /Words B/.test(e.theirs ?? '') && e.saveDisabled === true,
    `agent written=${w?.written}; editor ${JSON.stringify(e)}`);
  has(await press('sp-words-keepmine'), 'Keep mine is there');
  await wait(300);
  const e2 = await ED();
  verdict('U1 Keep mine keeps the draft and opens Save', e2.open && !e2.moved && e2.value === 'Draft C typed by the person.' && e2.saveDisabled === false, JSON.stringify(e2));
  has(await press('sp-words-save'), 'Save after Keep mine');
  await wait(1500);
  const now = await lineNow(l.item.id); const e3 = await ED();
  const call = calls.filter((c) => c.method === 'writeStartWords').at(-1);
  verdict('U1 Keep mine then Save writes the draft against the newer words', now?.item?.after === 'Draft C typed by the person.' && now?.decidedBy === 'person' && !e3.open && call?.params?.[0]?.[0]?.expected === 'Words B from the assistant.',
    `engine "${now?.item?.after}" by ${now?.decidedBy}; expected sent "${call?.params?.[0]?.[0]?.expected}"; editor open ${e3.open}`);
}

async function sceneConflictUseNew(v) {
  const l = freshLines(v, used)[0]; used.add(l.item.id);
  await openWords(l);
  await setText('[data-testid="sp-words-input"]', 'Draft D typed by the person.');
  await agentWrite(l, 'Words E from the assistant.');
  const e1 = await waitFor(async () => { const e = await ED(); return e.moved ? e : null; });
  verdict('U1 Use the new one: the choice is shown', !!e1 && e1.value === 'Draft D typed by the person.', JSON.stringify(e1));
  // The draft is not dropped by anything but the person's choice: wait, and the draft is still there.
  await wait(2500);
  const still = await ED();
  verdict('U1 the draft waits for the choice', still.value === 'Draft D typed by the person.' && still.moved, JSON.stringify(still));
  has(await press('sp-words-usenew'), 'Use the new one is there');
  await wait(300);
  const e2 = await ED();
  await shoot('u1-usenew', null);
  const now = await lineNow(l.item.id);
  verdict('U1 Use the new one drops the draft and loads the new words, nothing saved', e2.value === 'Words E from the assistant.' && e2.saveDisabled === true && now?.item?.after === 'Words E from the assistant.' && now?.decidedBy === 'assistant',
    `editor ${JSON.stringify(e2)}; engine "${now?.item?.after}" by ${now?.decidedBy}`);
  await press('sp-words-cancel');
}

async function sceneConflictSaveRace(v) {
  // The assistant writes, and the person presses Save BEFORE the live update reaches the page (Sol's exact step 4, with the relay late).
  const l = freshLines(v, used)[0]; used.add(l.item.id);
  await openWords(l);
  await setText('[data-testid="sp-words-input"]', 'Draft F typed by the person.');
  holdRelay = true;
  await agentWrite(l, 'Words G from the assistant.');
  await wait(400);
  has(await press('sp-words-save'), 'Save while the update is late');
  await wait(1500);
  const e = await ED();
  await shoot('u1-save-race', null, ['sp-words-editor']);
  const now = await lineNow(l.item.id);
  verdict('U1 Save refused over newer words keeps the editor and the draft', e.open && e.value === 'Draft F typed by the person.' && now?.item?.after === 'Words G from the assistant.',
    `editor ${JSON.stringify(e)}; engine "${now?.item?.after}"`);
  holdRelay = false;
  for (const h of held.splice(0)) await page.evaluate((p) => { window.dispatchEvent(new MessageEvent('message', { data: { type: 'startPlanDidChange', payload: p } })); }, h);
  await wait(800);
  const e2 = await ED();
  verdict('U1 after the late update the choice is shown beside the draft', e2.moved && e2.value === 'Draft F typed by the person.' && /Words G/.test(e2.theirs ?? ''), JSON.stringify(e2));
  await press('sp-words-usenew'); await press('sp-words-cancel');
}

async function sceneRefusal(v) {
  const l = freshLines(v, used)[0]; used.add(l.item.id);
  await openWords(l);
  const draft = 'The total is SUM ( Sales[Amount] ) for the year.';
  await setText('[data-testid="sp-words-input"]', draft);
  has(await press('sp-words-save'), 'Save of words the engine refuses');
  await wait(1500);
  const e = await ED();
  await shoot('u1-refused', null, ['sp-words-editor']);
  const now = await lineNow(l.item.id);
  verdict('U1 engine refusal keeps the editor, the draft and says the reason inside it', e.open && e.value === draft && !!e.said && now?.item?.after !== draft,
    `editor ${JSON.stringify(e)}; engine "${now?.item?.after}"`);
  await press('sp-words-cancel');
}

async function sceneOtherLine(v) {
  const fl = freshLines(v, used);
  const l = (process.env.TARGET && fl.find((x) => x.item.objectRef === process.env.TARGET)) || fl[0];
  const other = (process.env.OTHER && fl.find((x) => x.item.objectRef === process.env.OTHER)) || fl.find((x) => x !== l);
  used.add(l.item.id); used.add(other.item.id);
  await openWords(l);
  await setText('[data-testid="sp-words-input"]', 'Draft H typed by the person.');
  const w = await agentWrite(other, 'Words for another line from the assistant.');
  await wait(2500);
  const e = await ED();
  verdict('U1 a live update of ANOTHER line leaves the draft alone', e.open && e.value === 'Draft H typed by the person.' && !e.moved, `agent written ${w?.written}; editor ${JSON.stringify(e)}`);
  const nBefore = calls.length;
  has(await press('sp-words-save'), 'Save after another line moved');
  const t1 = Date.now();
  await waitFor(async () => { const c = calls.slice(nBefore).find((x) => x.method === 'writeStartWords'); return c && (c.answer || c.error); }, 120000);
  say(`the save answered in ${Date.now() - t1} ms`);
  await wait(500);
  const ev = await engineView();
  const now = linesIn(ev).find((x) => x.item.id === l.item.id);
  const same = linesIn(ev).filter((x) => x.item.objectRef === l.item.objectRef).map((x) => ({ id: x.item.id, kind: x.item.kind, after: x.item.after, word: x.word, by: x.decidedBy, kept: x.kept }));
  const last = calls.filter((c) => c.method === 'writeStartWords').at(-1);
  verdict('U1 the draft saves after another line moved', now?.item?.after === 'Draft H typed by the person.' || same.some((x) => x.after === 'Draft H typed by the person.' || x.word === 'Draft H typed by the person.'),
    `engine "${now?.item?.after}"; lines on ${l.item.objectRef}: ${JSON.stringify(same)}; other line ${other.item.objectRef}; last save ${JSON.stringify(last?.params)}`);
}

async function sceneSecondEditor(v) {
  // (a) the Rename editor on the same line, (b) the words editor on another line. Both are a second editor opened while a draft is typed.
  const cands = freshLines(v, used);
  const l = cands.find((x) => true); used.add(l.item.id);
  await openWords(l);
  await setText('[data-testid="sp-words-input"]', 'Draft J typed by the person.');
  const canRename = await page.evaluate(() => !!document.querySelector('[data-testid="sp-rename"]'));
  if (canRename) {
    await press('sp-rename'); await wait(300);
    const e = await ED();
    await shoot('u1-second-rename', null);
    verdict('U1 second editor (Rename on the same line) keeps the typed draft', e.open && e.value === 'Draft J typed by the person.', `after pressing Rename: ${JSON.stringify(e)}`);
    await press('sp-rename-cancel').catch(() => {});
    await press('sp-words'); await wait(300);
    const back = await ED();
    verdict('U1 reopening the words editor brings the draft back', back.value === 'Draft J typed by the person.', JSON.stringify(back));
    if (back.value !== 'Draft J typed by the person.') await setText('[data-testid="sp-words-input"]', 'Draft J typed by the person.');
  } else say('no Rename on this words line; (a) not applicable');
  const o = freshLines(await engineView(), used)[0]; used.add(o.item.id);
  await openWords(o);
  const e2 = await ED();
  await openWords(l);
  const back2 = await ED();
  await shoot('u1-second-other-row', null);
  verdict('U1 second editor (another line opened) then back: the first draft is still there', back2.value === 'Draft J typed by the person.', `other editor ${JSON.stringify(e2)}; back on the first: ${JSON.stringify(back2)}`);
  await press('sp-words-cancel');
}

async function sceneReconnectSame(v) {
  const l = freshLines(v, used)[0]; used.add(l.item.id);
  await openWords(l);
  const draft = 'Draft S typed by the person (same session).';
  await setText('[data-testid="sp-words-input"]', draft);
  await page.evaluate(() => { window.dispatchEvent(new MessageEvent('message', { data: { type: 'reconnected' } })); });
  await wait(3000);
  const e = await ED();
  verdict('U1 control: a reconnect to the SAME session keeps the draft', e.open && e.value === draft, JSON.stringify(e));
  if (e.open) await press('sp-words-cancel');
}

async function sceneRestart(v, mode) {
  const l = freshLines(v, used)[0]; used.add(l.item.id);
  await openWords(l);
  const draft = `Draft K typed by the person (${mode}).`;
  await setText('[data-testid="sp-words-input"]', draft);
  // The engine goes away: the host posts engineState false, and every call answers 'Engine not connected.'.
  await page.evaluate(() => { window.__engineDown = true; window.dispatchEvent(new MessageEvent('message', { data: { type: 'engineState', connected: false } })); });
  await wait(800);
  if (mode === 'save-while-down') {
    has(await press('sp-words-save'), 'Save while the engine is down');
    await wait(1500);
    const e = await ED();
    await shoot('u1-save-while-down', null);
    verdict('U1 Save while the engine is down keeps the draft', e.open && e.value === draft, JSON.stringify(e));
  }
  // The engine is back as a NEW process: the model reopens under a new session id, the host posts 'reconnected' and engineState true.
  await page.evaluate((s) => { window.__sessId = s; window.__engineDown = false; window.dispatchEvent(new MessageEvent('message', { data: { type: 'reconnected' } })); window.dispatchEvent(new MessageEvent('message', { data: { type: 'engineState', connected: true } })); }, `s-restart-${mode}`);
  await wait(3000);
  let e = await ED();
  if (!e.open && e.onPlan) { await revealRow(l.item.id, 'words', l.group); await openRowById(l.item.id); e = await ED(); }
  const reopened = e.open ? e : (await press('sp-words').then(() => wait(300)).then(() => ED()));
  await shoot(`u1-restart-${mode}`, null);
  verdict(`U1 engine restart while the editor is open (${mode}) keeps the typed draft`, reopened.value === draft, `after the restart: ${JSON.stringify(e)}; reopened: ${JSON.stringify(reopened)}`);
  if (reopened.open) await press('sp-words-cancel');
}

const SAMPLE = () => page.evaluate(() => [...document.querySelectorAll('[data-testid="sp-sample"]')].map((e) => ({ table: e.dataset.table, allowed: e.dataset.allowed, checked: e.querySelector('input')?.checked, text: e.textContent.trim() })));
const groupOf = (v, t) => (v.sections ?? []).flatMap((s) => s.groups ?? []).find((g) => g.table === t && g.columns);
async function proSampleScene(v) {
  // The person's grant, given on the person's own pipe (the model's permission, then the plan's grant), as the start plan's own card gives it.
  const c = await human.call('getConsent', []);
  await human.call('setConsent', [c?.fieldsInForce ?? [], c?.schemaVersionInForce ?? 0, 'human', c?.appliesTo ?? null]);
  v = await engineView();
  await human.call('grantStartDisclosure', [v.digest, 'human']);
  v = await settle('granted', { ms: 180000 });
  const before = await SAMPLE();
  await shoot('u2-controls', v, ['sp-section-tables']);
  verdict('U2 after the grant each included table has the sample choice, on', before.length > 0 && before.every((x) => x.allowed === 'yes' && x.checked), JSON.stringify(before));
  const T = before.at(-1)?.table;
  if (!T) return;
  const n0 = calls.length;
  await page.evaluate((t) => { const e = document.querySelector(`[data-testid="sp-sample"][data-table="${t}"] input`); e.scrollIntoView({ block: 'center' }); e.click(); }, T);
  await waitFor(async () => calls.slice(n0).some((x) => x.method === 'editStartPlan' && x.answerDigest), 30000);
  v = await settle('refused', { ms: 180000 });
  const call = calls.slice(n0).find((x) => x.method === 'editStartPlan');
  const after = await SAMPLE();
  await shoot('u2-refused', v, ['sp-section-tables']);
  verdict('U2 the control sends the same edit MCP set_table_sample sends, from the person', JSON.stringify(call?.params) === JSON.stringify([{ op: 'set_table_sample', target: T, value: 'off' }, 'human']), JSON.stringify(call?.params));
  verdict('U2 the engine holds the refusal and the page shows it', groupOf(v, T)?.sampleOff === true && after.find((x) => x.table === T)?.allowed === 'no' && after.filter((x) => x.table !== T).every((x) => x.allowed === 'yes'),
    `engine sampleOff ${groupOf(v, T)?.sampleOff}; page ${JSON.stringify(after)}; decide ${v.decide?.state}`);
  const n1 = calls.length;
  await page.evaluate((t) => { const e = document.querySelector(`[data-testid="sp-sample"][data-table="${t}"] input`); e.scrollIntoView({ block: 'center' }); e.click(); }, T);
  await waitFor(async () => calls.slice(n1).some((x) => x.method === 'editStartPlan' && x.answerDigest), 30000);
  v = await settle('back on', { ms: 180000 });
  const m = await page.evaluate(MEASURE);
  const again = await SAMPLE();
  await shoot('u2-back-on', v, ['sp-needs']);
  verdict('U2 turning it back on asks for the grant again (never widens silently)', groupOf(v, T)?.sampleOff === false && (v.decide?.state === 'waiting_grant' || (v.needsYou ?? []).some((n) => n.kind === 'grant')) && m.needs.some((n) => n.kind === 'grant'),
    `engine decide ${v.decide?.state}, needs ${(v.needsYou ?? []).map((n) => n.kind)}; page needs ${m.needs.map((n) => n.kind)}; controls now ${JSON.stringify(again)}`);
}

let words = { batches: 0, written: 0, refused: [] };
try {
  let m = await page.evaluate(MEASURE);
  has(m.onForm, 'the start form is on the page');
  has(await clickText('[data-testid="sp-form"]', 'Type an endpoint instead'), 'the form offers to type an endpoint');
  await wait(300);
  await typeIn('input[aria-label="SQL endpoint"]', hostInfo.server);
  await typeIn('input[aria-label="Database"]', hostInfo.database);
  await typeIn('[data-testid="sp-form-name"]', 'Contoso Consulting');
  await typeIn('[data-testid="sp-form-folder"]', folder);
  has(await clickId('sp-form-start'), 'the Start button');
  for (let i = 0; i < 120 && !(await page.evaluate(MEASURE)).onPlan; i++) await wait(500);
  let v = await settle('started');
  if (hostInfo.tier === 'pro') { await proSampleScene(v); throw Object.assign(new Error('pro scenes done'), { done: true }); }
  // the assistant writes every open word (agent pipe)
  const humanise = (r) => { let n = String(r ?? ''); n = n.slice(n.lastIndexOf('/') + 1); const c = n.indexOf(':'); if (c >= 0) n = n.slice(c + 1); return n.split(/[_ ]/).filter(Boolean).map((p) => p[0].toUpperCase() + p.slice(1).toLowerCase()).join(' '); };
  const newName = (r) => { const raw = String(r ?? '').slice(String(r ?? '').lastIndexOf('/') + 1); const h = humanise(r); return h.toLowerCase() === raw.toLowerCase() ? h + ' Value' : h; };
  const wordFor = (i) => ({ ref: i.ref, kind: i.kind, text: i.kind === 'synonyms' ? 'alias one, alias two' : i.kind === 'display_name' ? newName(i.ref) : i.kind === 'instructions' ? 'This model answers questions about consulting work.' : 'What ' + humanise(i.ref) + ' means for the business, in plain words.' });
  for (let pass = 0; pass < 60; pass++) {
    const pg = await agent.call('getStartWords', [40, 0]);
    if (!pg?.items?.length) break;
    const r = await agent.call('writeStartWords', [pg.items.map(wordFor), 'agent']);
    words.batches++; words.written += r?.written ?? 0;
    if (!r?.written) break;
  }
  v = await settle('words');
  say(`assistant wrote ${words.written}; description lines the assistant owns: ${freshLines(v, used).length}`);
  const only = (process.env.SCENES || 'keepmine,usenew,race,refusal,other,second,restart,restartsave').split(',');
  const run = async (name, fn) => { if (!only.includes(name)) return; try { await fn(await settle(name, { ms: 60000 })); } catch (e) { verdict(`scene ${name}`, false, 'threw: ' + (e?.stack ?? e)); } };
  await run('keepmine', sceneConflictKeepMine);
  await run('usenew', sceneConflictUseNew);
  await run('race', sceneConflictSaveRace);
  await run('refusal', sceneRefusal);
  await run('other', sceneOtherLine);
  await run('second', sceneSecondEditor);
  await run('same', sceneReconnectSame);
  await run('restart', (vv) => sceneRestart(vv, 'idle'));
  await run('restartsave', (vv) => sceneRestart(vv, 'save-while-down'));
} catch (e) {
  if (!e?.done) notes.push('the journey threw: ' + (e?.stack ?? e));
} finally {
  writeFileSync(join(OUT, 'results.json'), JSON.stringify({ host: hostInfo, words, verdicts, calls, results, notes, errors }, null, 2));
  await browser.close().catch(() => {});
  server.close();
  human.close(); agent.close();
  await stopHost();
}
console.log('\nVERDICTS');
for (const x of verdicts) console.log(`${x.ok ? 'PASS' : 'FAIL'}  ${x.scene}`);
for (const n of notes) console.log(`  ! ${n}`);
for (const e of errors.slice(0, 10)) console.log(`  ! page ${e}`);
console.log(`results: ${join(OUT, 'results.json')}`);
if (verdicts.some((x) => !x.ok)) process.exitCode = 1;
