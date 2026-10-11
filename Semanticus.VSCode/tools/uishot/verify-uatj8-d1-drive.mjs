// VERIFY (m11/uatj8 fix round 2, D1 as a class): the start plan words draft through the real Studio page against a REAL engine.
// Built from verify-uatj8-real-drive.mjs (same host, pipes, page). Added: each page keeps its webview state in its own sessionStorage, as
// VS Code keeps a webview's state outside it (so a reload keeps it and a second window has its own), and a second page is a second window
// on the same engine. Scenes: Escape, two drafts on two lines, Save while the engine is down then back, a restart under a draft whose line the
// assistant changed (editor open, row shut), a second window, a reload, another model and a line that is gone, and the plan's own Discard.
//   TMPDIR=/tmp/rdr OUTDIR=<dir> node tools/uishot/verify-uatj8-d1-drive.mjs     (SCENES=escape,two,... for some)

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
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync, rmSync } from 'node:fs';
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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'verify-uatj8-d1');
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
      wrapped = Object.assign({}, api, {
        getState() { try { const s = sessionStorage.getItem('__vsstate'); if (s) return JSON.parse(s); } catch { /* none */ } return api.getState(); },
        setState(s) { try { sessionStorage.setItem('__vsstate', JSON.stringify(s)); } catch { /* none */ } return api.setState(s); },
        postMessage(msg) {
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

// Each page is one webview: its own window, its own persisted state, its own __engineDown. A second page is a second VS Code window on the
// same engine (the extension adopts one owner engine per model through EngineBroker, so two windows on one model share it).
const errors = [];
const pages = [];
let holdRelay = false; const held = [];
const tierQ = hostInfo.tier === 'pro' ? '&tier=pro' : '';
const PAGE_URL = () => `http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&live=1&spec=empty&coldNav=spec:start:sql${tierQ}`;
async function openPage(label) {
  const pg = await browser.newPage();
  pg.on('pageerror', (e) => errors.push(`${label} pageerror: ` + e.message));
  pg.on('console', (m) => { if (m.type() === 'error') errors.push(`${label} console: ` + m.text()); });
  await pg.exposeFunction('__realRpc', async (method, params) => {
    let before = null;
    try { before = (await human.call('getStartPlan', []))?.digest ?? null; } catch { /* no plan yet */ }
    const rec = { win: label, method, params, engineDigestBefore: before };
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
  await pg.setViewport({ width: 1280, height: 1100, deviceScaleFactor: 1 });
  await pg.evaluateOnNewDocument(WRAP);
  await pg.goto(PAGE_URL(), { waitUntil: 'networkidle0', timeout: 30000 });
  pages.push(pg);
  await wait(1500);
  return pg;
}
human.on('startPlan/didChange', (v) => {
  if (holdRelay) { held.push(v); return; }
  for (const pg of pages) void pg.evaluate((p) => { window.dispatchEvent(new MessageEvent('message', { data: { type: 'startPlanDidChange', payload: p } })); }, v).catch(() => {});
});
let page = await openPage('window1');


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

// ---------------------------------------------------------------- D1 class scenes (round 2 re-check)

const flagOf = (id) => page.evaluate((i) => document.querySelector(`[data-testid="sp-row-${i}"]`)?.dataset.draft ?? null, id);
// The words drafts as the page holds them in its persisted state: since join 11 the shared draft store ('studio.drafts', keyed [model, scope, field], scope 'startplan|<line>'),
// shown here in the old shape the checks read: key [model, line, kind], value { text, base }.
const heldDrafts = () => page.evaluate(() => {
  try {
    const all = (JSON.parse(sessionStorage.getItem('__vsstate') || '{}') || {})['studio.drafts'] || {}, out = {};
    for (const [k, v] of Object.entries(all)) { const [model, scope, kind] = JSON.parse(k); if (scope.startsWith('startplan|')) out[JSON.stringify([model, scope.slice('startplan|'.length), kind])] = { text: v.value, base: v.base }; }
    return out;
  } catch { return { unreadable: true }; }
});
const pageWrites = (n0) => calls.slice(n0).filter((c) => c.method === 'writeStartWords');
const escapeEditor = async () => { await page.focus('[data-testid="sp-words-input"]'); await page.keyboard.press('Escape'); await wait(300); };
const closeRow = async (id) => { const open = await page.evaluate((i) => !!document.querySelector(`[data-testid="sp-detail-${i}"]`), id); if (open) await clickId(`sp-row-${id}`); await wait(300); };
const saveAndWait = async (n0) => {
  has(await press('sp-words-save'), 'Save is there and on');
  await waitFor(async () => { const c = pageWrites(n0).at(-1); return c && (c.answer || c.error); }, 120000);
  await wait(500);
  return pageWrites(n0).at(-1);
};
const goDown = async () => { holdRelay = true; await page.evaluate(() => { window.__engineDown = true; window.dispatchEvent(new MessageEvent('message', { data: { type: 'engineState', connected: false } })); }); await wait(800); };
const comeBack = async (sess) => {
  held.splice(0);   // what the engine broadcast while this window had no engine never reached it; the page reads the plan again on the way back
  holdRelay = false;
  await page.evaluate((s) => { if (s) window.__sessId = s; window.__engineDown = false; window.dispatchEvent(new MessageEvent('message', { data: { type: 'reconnected' } })); window.dispatchEvent(new MessageEvent('message', { data: { type: 'engineState', connected: true } })); }, sess);
  await waitFor(async () => (await page.evaluate(() => !document.querySelector('[data-testid="sp-stale"]') && !!document.querySelector('[data-testid="sp-page"]'))), 20000);
  await wait(1200);
};
const keyOf = (v) => v?.destination?.modelPath || [v?.destination?.folder, v?.destination?.name].filter(Boolean).join('/') || v?.id || '';
const lineSummary = (x) => x ? { after: x.item?.after, word: x.word, by: x.decidedBy, kept: x.kept } : null;

async function sceneEscape(v) {
  const l = freshLines(v, used)[0]; used.add(l.item.id);
  const orig = l.word ?? l.item.after;
  await openWords(l);
  const draft = 'Draft E1 typed by the person.';
  await setText('[data-testid="sp-words-input"]', draft);
  const n0 = calls.length;
  await escapeEditor();
  const e = await ED(), flag = await flagOf(l.item.id), now = await lineNow(l.item.id);
  await shoot('d1-escape', null);
  verdict('D1 Escape closes the editor, keeps the draft, flags the row, saves nothing', !e.open && flag === 'yes' && pageWrites(n0).length === 0 && (now?.word ?? now?.item?.after) === orig,
    `editor ${JSON.stringify(e)}; row flag ${flag}; page writes ${pageWrites(n0).length}; engine ${JSON.stringify(lineSummary(now))}`);
  await press('sp-words'); await wait(300);
  const e2 = await ED();
  verdict('D1 after Escape, Edit words opens on the draft', e2.open && e2.value === draft, JSON.stringify(e2));
  await escapeEditor(); await closeRow(l.item.id); await openRowById(l.item.id);
  const e3 = await ED();
  verdict('D1 after Escape and the row shut, opening the row opens the editor on the draft', e3.open && e3.value === draft, JSON.stringify(e3));
  const label = await page.evaluate(() => document.querySelector('[data-testid="sp-words-cancel"]')?.textContent?.trim());
  has(await press('sp-words-cancel'), 'Discard my words');
  await wait(300);
  const flag2 = await flagOf(l.item.id), st = await heldDrafts();
  await press('sp-words'); await wait(300);
  const e4 = await ED();
  verdict('D1 Discard my words ends the draft (flag gone, state empty for the line, editor back on the line words)', label === 'Discard my words' && flag2 === 'no' && !Object.keys(st).some((k) => k.includes(l.item.id)) && e4.value === orig && pageWrites(n0).length === 0,
    `button "${label}"; flag ${flag2}; held keys ${JSON.stringify(Object.keys(st))}; editor ${JSON.stringify(e4)}`);
  await press('sp-words-cancel');
}

async function sceneTwo(v) {
  const [a, b] = freshLines(v, used); used.add(a.item.id); used.add(b.item.id);
  const oa = a.word ?? a.item.after, ob = b.word ?? b.item.after;
  await openWords(a); await setText('[data-testid="sp-words-input"]', 'Draft A2 for the first line.');
  await openWords(b); await setText('[data-testid="sp-words-input"]', 'Draft B2 for the second line.');
  const fa = await flagOf(a.item.id), fb = await flagOf(b.item.id);
  await openWords(a); const ea = await ED();
  await openWords(b); const eb = await ED();
  await shoot('d1-two-drafts', null);
  verdict('D1 two drafts on two lines both survive (flags, and each row reopens on its own draft)', fa === 'yes' && ea.value === 'Draft A2 for the first line.' && eb.value === 'Draft B2 for the second line.',
    `flag a ${fa} (b ${fb} while open); a reopened ${JSON.stringify(ea)}; b reopened ${JSON.stringify(eb)}`);
  const n0 = calls.length;
  const cb = await saveAndWait(n0);
  const nb = await lineNow(b.item.id), fb2 = await flagOf(b.item.id), fa2 = await flagOf(a.item.id);
  verdict('D1 saving the second draft writes only it, against its own start words; the first stays held', (nb?.word ?? nb?.item?.after) === 'Draft B2 for the second line.' && nb?.decidedBy === 'person'
    && cb?.params?.[0]?.length === 1 && cb.params[0][0].ref === b.item.objectRef && cb.params[0][0].expected === ob && fb2 === 'no' && fa2 === 'yes',
    `sent ${JSON.stringify(cb?.params)}; engine b ${JSON.stringify(lineSummary(nb))}; flags a ${fa2} b ${fb2}`);
  await openWords(a); const ea2 = await ED();
  const n1 = calls.length;
  const ca = await saveAndWait(n1);
  const na = await lineNow(a.item.id);
  verdict('D1 the first draft then saves into its own line', ea2.value === 'Draft A2 for the first line.' && (na?.word ?? na?.item?.after) === 'Draft A2 for the first line.' && ca?.params?.[0]?.[0]?.ref === a.item.objectRef && ca.params[0][0].expected === oa,
    `reopened ${JSON.stringify(ea2)}; sent ${JSON.stringify(ca?.params)}; engine a ${JSON.stringify(lineSummary(na))}`);
}

async function sceneDownSave(v) {
  const l = freshLines(v, used)[0]; used.add(l.item.id);
  const orig = l.word ?? l.item.after;
  await openWords(l);
  await setText('[data-testid="sp-words-input"]', 'Draft D5 typed by the person.');
  const n0 = calls.length;
  await goDown();
  const e = await ED();
  const stale = await page.evaluate(() => ({ t: document.querySelector('[data-testid="sp-stale"]')?.textContent?.trim() ?? null, away: document.querySelector('[data-testid="sp-words-away"]')?.textContent?.trim() ?? null }));
  await page.evaluate(() => document.querySelector('[data-testid="sp-words-save"]')?.click());
  await page.focus('[data-testid="sp-words-input"]'); await page.keyboard.press('End'); await page.keyboard.type(' More while down.', { delay: 2 });
  await wait(400);
  const e2 = await ED();
  await shoot('d1-down', null);
  verdict('D1 engine down: plan stays, says so, Save is off and sends nothing, typing still works', e.open && e.saveDisabled === true && /not answering/i.test(stale.t ?? '') && !!stale.away && e2.value === 'Draft D5 typed by the person. More while down.' && pageWrites(n0).length === 0,
    `editor ${JSON.stringify(e2)}; banner "${stale.t}"; away "${stale.away}"; writes ${pageWrites(n0).length}`);
  await comeBack('s-d5-restart');
  let e3 = await ED();
  if (!e3.open) { await openWords(l); e3 = await ED(); }
  const c = await saveAndWait(n0);
  const now = await lineNow(l.item.id);
  verdict('D1 back from the outage (new session) the draft saves, naming the words it started from', e3.value === 'Draft D5 typed by the person. More while down.' && (now?.word ?? now?.item?.after) === e3.value && c?.params?.[0]?.[0]?.expected === orig,
    `after back ${JSON.stringify(e3)}; sent ${JSON.stringify(c?.params)}; engine ${JSON.stringify(lineSummary(now))}`);
}

async function sceneRestartMoved(v, shut) {
  const l = freshLines(v, used)[0]; used.add(l.item.id);
  await openWords(l);
  const draft = `Draft M4 typed by the person (${shut ? 'row shut' : 'editor open'}).`;
  await setText('[data-testid="sp-words-input"]', draft);
  if (shut) { await escapeEditor(); await closeRow(l.item.id); }
  const n0 = calls.length;
  await goDown();
  const theirs = `Words N4 from the assistant while the engine was away (${shut ? 'shut' : 'open'}).`;
  const w = await agentWrite(l, theirs);
  await comeBack(`s-m4-${shut ? 'shut' : 'open'}`);
  let e = await ED();
  if (!e.open) { await openWords(l); e = await ED(); }
  await shoot(`d1-restart-moved-${shut ? 'shut' : 'open'}`, null, ['sp-words-editor']);
  verdict(`D1 after a restart, a draft whose line the assistant changed asks Keep mine / Use the new one (${shut ? 'row shut' : 'editor open'})`,
    w?.written === 1 && e.open && e.moved && e.value === draft && (e.theirs ?? '').includes(theirs) && e.saveDisabled === true && pageWrites(n0).length === 0,
    `agent written ${w?.written}; editor ${JSON.stringify(e)}; page writes ${pageWrites(n0).length}`);
  if (!shut) {
    has(await press('sp-words-keepmine'), 'Keep mine');
    await wait(300);
    const c = await saveAndWait(n0);
    const now = await lineNow(l.item.id);
    verdict('D1 after the restart, Keep mine then Save writes the draft over the words the person was shown', (now?.word ?? now?.item?.after) === draft && now?.decidedBy === 'person' && c?.params?.[0]?.[0]?.expected === theirs,
      `sent ${JSON.stringify(c?.params)}; engine ${JSON.stringify(lineSummary(now))}`);
  } else {
    has(await press('sp-words-usenew'), 'Use the new one');
    await wait(400);
    const e2 = await ED(), flag = await flagOf(l.item.id), now = await lineNow(l.item.id), st = await heldDrafts();
    verdict('D1 after the restart, Use the new one ends the draft and saves nothing', e2.value === theirs && flag === 'no' && pageWrites(n0).length === 0 && now?.decidedBy === 'assistant' && !Object.keys(st).some((k) => k.includes(l.item.id)),
      `editor ${JSON.stringify(e2)}; flag ${flag}; page writes ${pageWrites(n0).length}; engine ${JSON.stringify(lineSummary(now))}`);
    await press('sp-words-cancel');
  }
}

async function sceneSecondWindow(v) {
  const l = freshLines(v, used)[0]; used.add(l.item.id);
  const orig = l.word ?? l.item.after;
  await openWords(l);
  const draft = 'Draft W6 typed in window one.';
  await setText('[data-testid="sp-words-input"]', draft);
  const one = page;
  const two = await openPage('window2');
  page = two;
  await waitFor(async () => (await page.evaluate(MEASURE)).onPlan, 30000);
  await wait(800);
  await revealRow(l.item.id, 'words', l.group);
  const flag2 = await flagOf(l.item.id), st2 = await heldDrafts();
  await openWords(l);
  const e2 = await ED();
  await shoot('d1-window-two', null);
  verdict('D1 a second window on the same model does not show window one\'s draft (each webview keeps its own)', flag2 === 'no' && e2.value === orig && Object.keys(st2).length === 0,
    `window two: flag ${flag2}; held ${JSON.stringify(Object.keys(st2))}; editor ${JSON.stringify(e2)}`);
  const n0 = calls.length;
  await setText('[data-testid="sp-words-input"]', 'Words saved in window two.');
  const c2 = await saveAndWait(n0);
  page = one;
  await two.close().catch(() => {}); pages.splice(pages.indexOf(two), 1);
  await page.bringToFront();
  const e1 = await waitFor(async () => { const e = await ED(); return e.moved ? e : null; }, 10000) ?? await ED();
  await shoot('d1-window-one-after', null, ['sp-words-editor']);
  verdict('D1 window one\'s draft then asks beside window two\'s saved words and never saves over them unseen', c2?.answer?.written === 1 && e1.moved && e1.value === draft && (e1.theirs ?? '').includes('Words saved in window two.') && e1.saveDisabled === true,
    `window two saved ${JSON.stringify(c2?.answer)}; window one editor ${JSON.stringify(e1)}`);
  has(await press('sp-words-keepmine'), 'Keep mine in window one');
  const n1 = calls.length;
  const c1 = await saveAndWait(n1);
  const now = await lineNow(l.item.id);
  verdict('D1 window one Keep mine then Save names window two\'s words as the ones it replaces', c1?.params?.[0]?.[0]?.expected === 'Words saved in window two.' && (now?.word ?? now?.item?.after) === draft,
    `sent ${JSON.stringify(c1?.params)}; engine ${JSON.stringify(lineSummary(now))}`);
}

async function sceneReload(v) {
  const l = freshLines(v, used)[0]; used.add(l.item.id);
  const orig = l.word ?? l.item.after;
  await openWords(l);
  const draft = 'Draft R3 typed before the webview reloaded.';
  await setText('[data-testid="sp-words-input"]', draft);
  const before = await heldDrafts();
  await page.reload({ waitUntil: 'networkidle0', timeout: 60000 });
  await wait(1500);
  const where = await waitFor(async () => { const m = await page.evaluate(MEASURE); return m.onPlan ? m : null; }, 30000) ?? await page.evaluate(MEASURE);
  await shoot('d1-after-reload', null);
  if (!where.onPlan) { verdict('D1 a reload of the webview keeps the draft', false, `after the reload the page is not on the plan (form ${where.onForm}); held before ${JSON.stringify(Object.keys(before))}; held now ${JSON.stringify(Object.keys(await heldDrafts()))}`); return; }
  await revealRow(l.item.id, 'words', l.group);
  const flag = await flagOf(l.item.id);
  await openWords(l);
  const e = await ED();
  verdict('D1 a reload of the webview keeps the draft (row flagged, reopens on it)', flag === 'yes' && e.value === draft, `flag ${flag}; editor ${JSON.stringify(e)}`);
  const n0 = calls.length;
  const c = await saveAndWait(n0);
  const now = await lineNow(l.item.id);
  verdict('D1 after the reload the draft saves against its start words', (now?.word ?? now?.item?.after) === draft && c?.params?.[0]?.[0]?.expected === orig, `sent ${JSON.stringify(c?.params)}; engine ${JSON.stringify(lineSummary(now))}`);
}

async function writeAllWords() {
  const humanise = (r) => { let n = String(r ?? ''); n = n.slice(n.lastIndexOf('/') + 1); const c = n.indexOf(':'); if (c >= 0) n = n.slice(c + 1); return n.split(/[_ ]/).filter(Boolean).map((p) => p[0].toUpperCase() + p.slice(1).toLowerCase()).join(' '); };
  const wordFor = (i) => ({ ref: i.ref, kind: i.kind, text: i.kind === 'synonyms' ? 'alias one, alias two' : i.kind === 'display_name' ? humanise(i.ref) + ' Value' : i.kind === 'instructions' ? 'This model answers questions about consulting work.' : 'What ' + humanise(i.ref) + ' means for the business, in plain words.' });
  for (let pass = 0; pass < 60; pass++) {
    const pg = await agent.call('getStartWords', [40, 0]);
    if (!pg?.items?.length) break;
    const r = await agent.call('writeStartWords', [pg.items.map(wordFor), 'agent']);
    if (!r?.written) break;
  }
}

/** Open each assistant-written words line of the plan on screen and read its editor. */
async function sweepEditors(v, max = 12) {
  const out = [];
  for (const x of freshLines(v, new Set()).slice(0, max)) {
    await openWords(x);
    const e = await ED();
    out.push({ id: x.item.id, ref: x.item.objectRef, value: e.value, moved: e.moved });
    if (e.open) await press('sp-words-cancel');
  }
  return out;
}

async function sceneOtherModelAndGoneLine(v, startParams) {
  const l = freshLines(v, used)[0]; used.add(l.item.id);
  await openWords(l);
  const draft = 'Draft G7 typed for a line that will be gone.';
  await setText('[data-testid="sp-words-input"]', draft);
  await escapeEditor();
  const heldBefore = await heldDrafts();
  const n0 = calls.length;
  // The plan goes away elsewhere (the other window, or a reset), NOT by this page's Discard, which ends its drafts by design.
  await human.call('discardStartPlan', ['human']);
  await wait(1500);
  const other = { ...startParams, name: 'Contoso Other', folder: startParams.folder.replace(/Contoso Consulting$/, 'Contoso Other') };
  await human.call('startModelFromSql', [other, 'human']);
  await waitFor(async () => (await page.evaluate(MEASURE)).onPlan, 60000);
  let v2 = await settle('other-model', { ms: 120000 });
  await writeAllWords();
  v2 = await settle('other-model-words', { ms: 120000 });
  const sw = await sweepEditors(v2);
  const body = await page.evaluate(() => document.body.innerText);
  await shoot('d1-other-model', null);
  verdict('D1 another model (another file) never shows the draft', keyOf(v2) !== keyOf(v) && !sw.some((s) => s.value === draft) && !body.includes(draft) && !/Draft not saved/.test(body) && pageWrites(n0).length === 0,
    `dest ${keyOf(v2)} vs ${keyOf(v)}; editors ${JSON.stringify(sw.map((s) => s.value?.slice(0, 40)))}; page writes ${pageWrites(n0).length}; held keys ${JSON.stringify(Object.keys(heldBefore))}`);
  // Back to the first model's file with a NEW plan: the line the draft was typed for is gone (new line ids).
  await human.call('discardStartPlan', ['human']);
  await wait(1500);
  // The engine starts a new model only in an empty folder: what the first plan left there is listed, then the folder goes (it is this host's scratch).
  const left = existsSync(folder) ? readdirSync(folder, { recursive: true }) : [];
  say(`left in the first folder after the discard: ${JSON.stringify(left)}`);
  if (folder.startsWith(HOST + '/')) rmSync(folder, { recursive: true, force: true });
  await human.call('startModelFromSql', [startParams, 'human']);
  await waitFor(async () => (await page.evaluate(MEASURE)).onPlan, 60000);
  let v3 = await settle('same-file-new-plan', { ms: 120000 });
  await writeAllWords();
  v3 = await settle('same-file-new-plan-words', { ms: 120000 });
  const idGone = !linesIn(v3).some((x) => x.item.id === l.item.id);
  const sw3 = await sweepEditors(v3, 40);
  const body3 = await page.evaluate(() => document.body.innerText);
  const heldAfter = await heldDrafts();
  await shoot('d1-gone-line', null);
  verdict('D1 a draft for a line that no longer exists does not show on, or save into, another line', keyOf(v3) === keyOf(v) && idGone && !sw3.some((s) => s.value === draft) && !body3.includes(draft) && pageWrites(n0).length === 0,
    `same file ${keyOf(v3) === keyOf(v)}; old line id gone ${idGone}; ${sw3.length} editors opened, any with the draft ${sw3.some((s) => s.value === draft)}; page writes ${pageWrites(n0).length}; held keys now ${JSON.stringify(Object.keys(heldAfter))}`);
  return v3;
}

async function scenePlanDiscard(v) {
  const l = freshLines(v, used)[0]; used.add(l.item.id);
  await openWords(l);
  await setText('[data-testid="sp-words-input"]', 'Draft X8 typed before the plan is discarded.');
  await escapeEditor();
  const before = await heldDrafts();
  page.once('dialog', (d) => { void d.accept(); });
  const pressed = await press('sp-discard');
  await wait(2500);
  const maybeConfirm = await page.evaluate(() => [...document.querySelectorAll('button')].map((b) => b.textContent.trim()).filter((t) => /discard/i.test(t)));
  if (maybeConfirm.length) await clickText('body', maybeConfirm.find((t) => t !== 'Discard') ?? maybeConfirm[0]);
  await wait(2000);
  const after = await heldDrafts(), ev = await engineView().catch(() => null);
  verdict('D1 the plan\'s own Discard ends its drafts', pressed && Object.keys(before).length > 0 && Object.keys(after).filter((k) => k.includes(JSON.stringify(keyOf(v)).slice(1, -1) || '###')).length === 0,
    `pressed ${pressed}; confirm buttons ${JSON.stringify(maybeConfirm)}; held before ${Object.keys(before).length}, after ${JSON.stringify(Object.keys(after))}; engine plan exists ${!!ev?.exists}`);
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
  const startParams = calls.find((c) => c.method === 'startModelFromSql')?.params?.[0];
  await writeAllWords();
  v = await settle('words');
  say(`description lines the assistant owns: ${freshLines(v, used).length}; model key ${keyOf(v)}`);
  const only = (process.env.SCENES || 'escape,two,downsave,movedopen,movedshut,window,reload,gone,discard').split(',');
  const run = async (name, fn) => { if (!only.includes(name)) return; try { await fn(await settle(name, { ms: 60000 })); } catch (e) { verdict(`scene ${name}`, false, 'threw: ' + (e?.stack ?? e)); } };
  await run('escape', sceneEscape);
  await run('two', sceneTwo);
  await run('downsave', sceneDownSave);
  await run('movedopen', (vv) => sceneRestartMoved(vv, false));
  await run('movedshut', (vv) => sceneRestartMoved(vv, true));
  await run('window', sceneSecondWindow);
  await run('reload', sceneReload);
  await run('gone', (vv) => sceneOtherModelAndGoneLine(vv, startParams));
  await run('discard', scenePlanDiscard);
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
