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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'startplan-real');
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
  let orig = null, wrapped = null;
  Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true,
    get: () => function () {
      if (wrapped) return wrapped;
      const api = orig();
      wrapped = Object.assign({}, api, { postMessage(msg) {
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
    if (method === 'writeStartWords' && result?.view) served = result.view;
    if (method === 'getStartPlan' || method === 'startModelFromSql' || method === 'editStartPlan' || method === 'approveStartPlan' || method === 'grantStartDisclosure') served = result;
    if (method === 'buildStartPlan' && result?.view) served = result.view;
    return { result: result ?? null };
  } catch (e) {
    rec.error = e.message;
    return { error: e.message };
  }
});
human.on('startPlan/didChange', (v) => {
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
  // The links count is the links the build writes (view.links), after every drop and leave-out: not the link lines, which also hold a dropped guess and a link to a left-out table.
  if (m.onPlan && Array.isArray(v.links)) {
    const shown = /(\d+) links, (\d+) on/.exec(m.facts ?? '');
    if (v.links.length) ck(shown && +shown[1] === v.links.length && +shown[2] === v.links.filter((l) => l.active).length, `links: engine builds ${v.links.length} (${v.links.filter((l) => l.active).length} on), page "${shown?.[0] ?? 'no count'}"`);
    else ck(!shown, `links: engine builds none, page "${shown?.[0]}"`);
  }
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
// Press a control once it is on the page and enabled (a Pro plan is busy for a while after each edit).
const press = async (id) => { for (let i = 0; i < 80; i++) { const ok = await page.evaluate((s) => { const el = document.querySelector(`[data-testid="${s}"]`); if (!el || el.disabled) return false; el.scrollIntoView({ block: 'center' }); el.click(); return true; }, id); if (ok) return true; await wait(500); } return false; };
const setText = async (sel, value) => { await page.evaluate((q) => { const e = document.querySelector(q); e.focus(); e.select(); }, sel); await page.keyboard.type(value, { delay: 3 }); };

async function dialogScene() {
  const opened = await page.waitForSelector('[data-testid="consent-card-start"]', { timeout: 20000 }).then(() => true, () => false);
  has(opened, 'Allow opens the permission card with the tables on it, even before any permission is given');
  if (!opened) return;
  await wait(500);
  const rows = await pageq(() => [...document.querySelectorAll('[data-testid^="consent-table-"][data-included]')].map((r) => ({ name: r.dataset.testid.replace('consent-table-', ''), included: r.dataset.included === 'yes' })));
  has(rows.length >= 3, `the card lists every table with its two choices: ${JSON.stringify(rows)}`);
  const text = await pageq(() => document.querySelector('[data-testid="consent-card-start"]').innerText);
  has(/counts and patterns/.test(text) && /never see/i.test(text), `the card says what the sample is and that rows never go: "${text.replace(/\s+/g, ' ').slice(0, 300)}"`);
  await shoot('dialog-open', null);
  const keep = rows[1].name, out = rows[rows.length - 1].name;
  // refuse one table's sample
  has(await pageq((n) => { const r = document.querySelector(`[data-testid="consent-table-${n}"] [data-testid="consent-table-sample"]`); if (!r) return false; r.click(); return true; }, keep), `the sample box of ${keep} is there`);
  await wait(900);
  let ev = await engineView();
  has(ev.sections.find((x) => x.id === 'tables').groups.find((g) => g.table === keep)?.sampleOff === true, `the engine holds the refused sample of ${keep}`);
  has(ev.sections.find((x) => x.id === 'tables').groups.find((g) => g.table === keep)?.lines.some((l) => l.kept), `${keep} is still in the plan`);
  // leave one table out, then put it back
  has(await pageq((n) => { const r = document.querySelector(`[data-testid="consent-table-${n}"] [data-testid="consent-table-toggle"]`); if (!r) return false; r.click(); return true; }, out), `the leave-out button of ${out} is there`);
  await wait(900);
  ev = await engineView();
  has(!ev.sections.find((x) => x.id === 'tables').groups.find((g) => g.table === out)?.lines.some((l) => l.kept), `the engine has ${out} left out`);
  has(await pageq((n) => document.querySelector(`[data-testid="consent-table-${n}"]`)?.dataset.included === 'no', out), `the card shows ${out} left out`);
  await shoot('dialog-choices', null);
  has(await pageq((n) => { const r = document.querySelector(`[data-testid="consent-table-${n}"] [data-testid="consent-table-toggle"]`); if (!r) return false; r.click(); return true; }, out), `the put-back button of ${out}`);
  await wait(900);
  ev = await engineView();
  has(ev.sections.find((x) => x.id === 'tables').groups.find((g) => g.table === out)?.lines.some((l) => l.kept), `the engine has ${out} back`);
  const before = (await engineView()).digest;
  has(await clickId('consent-ok'), 'the OK button of the card');
  await wait(1500);
  const g = calls.filter((c) => c.method === 'grantStartDisclosure' && !c.error).at(-1);
  has(g && g.params[0] === before, `the grant carries the digest of the plan after the person's table choices (${g?.params?.[0]} vs ${before})`);
  ev = await engineView();
  has(ev.sections.find((x) => x.id === 'tables').groups.find((t) => t.table === keep)?.sampleOff === true, `the refused sample of ${keep} survives the grant`);
}

async function controlsScene(tag) {
  let v = await settle(`${tag} controls start`, { ms: 180000 });
  // 1. one row opens at a time
  const ids = await pageq(() => [...document.querySelectorAll('.sp-row')].slice(0, 4).map((r) => r.dataset.testid));
  has(ids.length >= 2 && new Set(ids).size === ids.length, `the rows have their own ids: ${JSON.stringify(ids)}`);
  for (const id of ids.slice(0, 3)) { await clickId(id); await wait(200); }
  has((await expandedRows()) === 1, `only the row pressed last is open (${await expandedRows()} open)`);
  await shoot(`${tag}-one-row-open`, null);
  // 2. drop and keep a table line by its id
  const tables = linesIn(v, 'tables').filter((l) => l.item.kind === 'include_table' && l.kept);
  const target = tables[tables.length - 1];
  if (target) {
    await revealRow(target.item.id, 'tables', target.group);
    await openRowById(target.item.id);
    has(await press('sp-drop'), `Drop on ${target.item.objectName}`);
    await wait(900);
    v = await engineView();
    has(linesIn(v, 'tables').find((l) => l.item.id === target.item.id)?.kept === false, `the engine dropped ${target.item.objectName}, the line the page pressed (id ${target.item.id})`);
    await shoot(`${tag}-dropped-table`, v);
    await revealRow(target.item.id, 'tables', target.group);
    await openRowById(target.item.id);
    has(await press('sp-keep'), `Keep on ${target.item.objectName}`);
    await wait(900);
    v = await engineView();
    has(linesIn(v, 'tables').find((l) => l.item.id === target.item.id)?.kept === true, `the engine has ${target.item.objectName} back`);
  } else notes.push(`${tag}: no kept table line to press`);
  // 3. rename a table, and back
  const t2 = linesIn(v, 'tables').find((l) => l.item.kind === 'include_table' && l.kept);
  if (t2) {
    const old = t2.item.objectName;
    await revealRow(t2.item.id, 'tables', t2.group);
    await openRowById(t2.item.id);
    has(await press('sp-rename'), `Rename on ${old}`);
    await wait(300);
    await setText('[data-testid="sp-rename-input"]', old + ' Renamed');
    await shoot(`${tag}-rename-editor`, null);
    has(await press('sp-rename-save'), 'the Save of the rename');
    await wait(1200);
    v = await settle(`${tag} rename`, { ms: 120000 });
    has(linesIn(v, 'tables').some((l) => l.item.kind === 'include_table' && l.item.objectName === old + ' Renamed'), `the engine renamed ${old}`);
    const back = linesIn(v, 'tables').find((l) => l.item.objectName === old + ' Renamed');
    await revealRow(back.item.id, 'tables', back.group);
    await openRowById(back.item.id);
    await press('sp-rename'); await wait(300);
    await setText('[data-testid="sp-rename-input"]', old);
    await press('sp-rename-save'); await wait(1200);
    v = await settle(`${tag} rename back`, { ms: 120000 });
    has(linesIn(v, 'tables').some((l) => l.item.objectName === old), `the engine has ${old} again`);
  }
  // 4. the columns: folded until opened, then a rename and a leave-out
  await pageq(() => { document.querySelector('[data-testid="sp-columns-toggle"]')?.scrollIntoView({ block: 'center' }); });
  const folded = await pageq(() => [...document.querySelectorAll('[data-testid="sp-columns"]')].every((c) => c.getAttribute('aria-expanded') === 'false') && document.querySelectorAll('.sp-col').length === 0);
  has(folded, 'every column list is folded by default');
  has(await press('sp-columns-toggle'), 'the column list opens');
  await wait(300);
  const cols = await pageq(() => [...document.querySelectorAll('.sp-col')].map((c) => c.dataset.testid.replace('sp-col-', '')));
  has(cols.length > 1, `the table's column names are listed: ${JSON.stringify(cols)}`);
  await shoot(`${tag}-columns-open`, null);
  const colName = cols[cols.length - 1];
  has(await pageq((n) => { const b = document.querySelector(`[data-testid="sp-col-${n}"] [data-testid="sp-col-include"]`); if (!b) return false; b.click(); return true; }, colName), `Leave out on ${colName}`);
  await wait(1000);
  v = await settle(`${tag} column out`, { ms: 120000 });
  has(await pageq((n) => document.querySelector(`[data-testid="sp-col-${n}"]`)?.dataset.included === 'no', colName), `${colName} shows as left out`);
  await pageq((n) => document.querySelector(`[data-testid="sp-col-${n}"] [data-testid="sp-col-include"]`).click(), colName);
  await wait(1000);
  v = await settle(`${tag} column back`, { ms: 120000 });
  has(await pageq((n) => document.querySelector(`[data-testid="sp-col-${n}"]`)?.dataset.included === 'yes', colName), `${colName} is back`);
  // 5. edit the words on a line the assistant wrote
  const wl = linesIn(v, 'words').find((l) => l.word && l.decidedBy === 'assistant' && l.item.kind === 'set_description');
  if (wl) {
    await revealRow(wl.item.id, 'words', wl.group);
    await openRowById(wl.item.id);
    has(await press('sp-words'), 'Edit the words on a description the assistant wrote');
    await wait(300);
    await setText('[data-testid="sp-words-input"]', 'Written by the person in Studio.');
    await shoot(`${tag}-words-editor`, null);
    has(await press('sp-words-save'), 'the Save of the words');
    await wait(1200);
    v = await settle(`${tag} words`, { ms: 120000 });
    const now = linesIn(v, 'words').find((l) => l.item.id === wl.item.id);
    has(now?.decidedBy === 'person' && now?.item?.after === 'Written by the person in Studio.', `the line is the person's now: ${now?.decidedBy} "${now?.item?.after}"`);
    await shoot(`${tag}-words-edited`, v);
  } else notes.push(`${tag}: no description line the assistant wrote to edit`);
  // 6. keep this value (a value a rule or Jev chose)
  const pinnable = linesIn(v).find((l) => l.kept && l.item.after && !l.derived && (l.decidedBy === 'rule' || l.decidedBy === 'jev') && !['include_table', 'create_relationship', 'mark_date_table', 'create_measure'].includes(l.item.kind));
  if (pinnable) {
    await revealRow(pinnable.item.id, sectionOfLine(v, pinnable), pinnable.group);
    await openRowById(pinnable.item.id);
    has(await press('sp-pin'), `Keep this value on ${pinnable.item.title}`);
    await wait(900);
    v = await engineView();
    has(linesIn(v).find((l) => l.item.id === pinnable.item.id)?.decidedBy === 'person', 'the pinned line is the person\'s');
  } else say(`${tag}: no rule or Jev line with a value to keep (nothing to pin here)`);
  return v;
}
let words = { batches: 0, written: 0, refused: [] };

try {
  // 1. the start form: type an endpoint (the J8 source the host serves), a name, an empty folder; Start.
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
  m = await page.evaluate(MEASURE);
  has(m.onPlan, `the start plan opened after the source was read (form error: ${m.formError})`);
  const start = calls.find((c) => c.method === 'startModelFromSql');
  has(start && start.params[0]?.server === hostInfo.server && start.params[0]?.database === hostInfo.database && start.params[0]?.folder === folder && start.params[0]?.name === 'Contoso Consulting',
    `startModelFromSql was sent the typed source, name and folder: ${JSON.stringify(start?.params)}`);
  // The plan as it lands, before the sample has necessarily finished: shot at once, then settled.
  const vFirst = await engineView();
  await shoot('started-landing', null);
  results.at(-1).engine = { digest: vFirst.digest, sample: vFirst.sample, needsYou: (vFirst.needsYou ?? []).map((n) => n.kind), provisional: vFirst.grades?.afterBuildProvisional };
  let v = await settle('started');
  m = await shoot('started', v, ['sp-section-links']);

  // Pro: the grant first (the person's Allow on the Needs you line), then Jev decides; then every unsure line it asks about is answered.
  if (hostInfo.tier === 'pro') {
    has(m.needs.some((n) => n.kind === 'grant'), `Pro asks for the grant first: ${JSON.stringify(m.needs.map((n) => n.kind))}`);
    // The grant line names the tables and the sample before anything is pressed (UAT-J7-S2-002).
    const grantNeed = m.needs.find((n) => n.kind === 'grant');
    has(/names, columns and settings of/.test(grantNeed?.text ?? '') && /sample/.test(grantNeed?.text ?? '') && /never the rows/.test(grantNeed?.text ?? ''), `the Needs you grant names the tables and the sample: "${grantNeed?.text}"`);
    has(await clickText('[data-testid="sp-need-grant"]', 'Allow'), 'the grant line offers Allow');
    await dialogScene();
    await wait(1500);
    m = await page.evaluate(MEASURE);
    if (/Give permission for this model first/.test(m.error ?? '')) {
      // THE DEAD WIRE (found by this driver, 2 Oct): the engine refuses a grant until the model's own permission is current
      // (LocalEngine.Decisions.cs GrantStartDisclosureAsync), the Plan page keeps the generic card aside while a start plan is
      // open (plan.tsx useConsentCard(... && !startPlanActive)), and the start plan's own card (consent.tsx openStartPlanConsentCard,
      // which gives the model's permission and then the grant) has no caller: the page's Allow calls grantStartDisclosure alone.
      // So Allow can never succeed from the start plan on a fresh Pro start. Shot, reported, then WORKED AROUND below so the rest of
      // the Pro journey can be seen: the person's model permission is given over the human pipe exactly as that card would give it.
      notes.push(`DEFECT: Pro Allow on the start plan's grant is refused: "${m.error}"`);
      await shoot('grant-refused', v);
      const c = await human.call('getConsent', []);
      const given = await human.call('setConsent', [c?.fieldsInForce ?? [], c?.schemaVersionInForce ?? 0, 'human', c?.appliesTo ?? null]);
      say(`WORKAROUND: the model's permission given over the human pipe (current ${given?.current}); Allow pressed again`);
      results.push({ step: 'workaround-model-permission', given: { current: given?.current, fields: given?.fields?.length } });
      await wait(800);
      has(await clickText('[data-testid="sp-need-grant"]', 'Allow'), 'the grant line still offers Allow');
      await wait(800);
    }
    v = await settle('decided', { ms: 180000 });
    m = await shoot('decided', v, ['sp-needs']);
    const grant = calls.filter((c) => c.method === 'grantStartDisclosure' && !c.error).at(-1);
    has(grant && grant.params[1] === 'human' && grant.params[0] === grant.engineDigestBefore, `the grant was sent the digest the engine had, as the person: ${JSON.stringify(grant?.params)} vs ${grant?.engineDigestBefore}`);
    // Pro asks one format question per column or measure Jev is unsure of: 13 before, 17 now that the four dimension date-key columns are no longer hidden as link keys (plan statement check 5). The bound is a safety stop, not the count.
    for (let k = 0; k < 60; k++) {
      m = await page.evaluate(MEASURE);
      const n = m.needs.find((x) => x.options.length > 0 && x.kind !== 'grant' && x.kind !== 'grant_declined');
      if (!n) break;
      has(await clickText(`[data-testid="sp-need-${n.id}"]`, n.options[0]), `answer ${n.id} with ${n.options[0]}`);
      await wait(600);
      v = await settle(`answer ${n.id}`, { ms: 120000 });
      say(`answered ${n.kind} ${n.id}: ${n.options[0]}`);
    }
    m = await shoot('answered', v);
  }

  // 2. the assistant writes the words on the agent pipe, page by page, until none are open (the Studio writes none).
  const humanise = (r) => { let n = String(r ?? ''); n = n.slice(n.lastIndexOf('/') + 1); const c = n.indexOf(':'); if (c >= 0) n = n.slice(c + 1); return n.split(/[_ ]/).filter(Boolean).map((p) => p[0].toUpperCase() + p.slice(1).toLowerCase()).join(' '); };
  const newName = (r) => { const raw = String(r ?? '').slice(String(r ?? '').lastIndexOf('/') + 1); const h = humanise(r); return h.toLowerCase() === raw.toLowerCase() ? h + ' Value' : h; };
  const wordFor = (i) => ({ ref: i.ref, kind: i.kind, text: i.kind === 'synonyms' ? 'alias one, alias two' : i.kind === 'display_name' ? newName(i.ref) : i.kind === 'instructions' ? 'This model answers questions about consulting work: hours, projects, clients and invoices.' : 'What ' + humanise(i.ref) + ' means for the business, in plain words.' });
  const openBefore = (await agent.call('getStartWords', [40, 0]))?.total ?? null;
  for (let pass = 0; pass < 60; pass++) {
    const pg = await agent.call('getStartWords', [40, 0]);
    if (!pg?.items?.length) break;
    const r = await agent.call('writeStartWords', [pg.items.map(wordFor), 'agent']);
    words.batches++; words.written += r?.written ?? 0; words.refused.push(...(r?.refused ?? []));
    // A careful assistant: a name the naming rules would still flag is refused with its reason, and the refusal says to send keep_name for the name the object has (join 8).
    const refusedNames = new Set((r?.refused ?? []).map((x) => String(x).split(': ')[0]));
    const keeps = pg.items.filter((i) => i.kind === 'display_name' && refusedNames.has(i.ref)).map((i) => ({ ref: i.ref, kind: 'keep_name', text: '' }));
    if (keeps.length) { const k = await agent.call('writeStartWords', [keeps, 'agent']); words.written += k?.written ?? 0; words.keeps = (words.keeps ?? 0) + (k?.written ?? 0); r.written = (r?.written ?? 0) + (k?.written ?? 0); }
    if (!r?.written) { notes.push(`no word landed on pass ${pass}: ${(r?.refused ?? []).slice(0, 3).join('; ')}`); break; }
  }
  words.openBefore = openBefore;
  words.openAfter = (await agent.call('getStartWords', [40, 0]))?.total ?? null;
  say(`assistant wrote ${words.written} words in ${words.batches} batches (${words.refused.length} refused), open before ${openBefore}, after ${words.openAfter}`);
  v = await settle('words');
  m = await shoot('words', v, ['sp-section-words']);
  has(v.words?.total > 0 && v.words?.open === 0, `every word is written once the plan settles (the assistant left none open, ${words.openAfter} after its last batch): ${JSON.stringify(v.words)}`);

  // 3. answer what the plan asks the person that has options (Free: the fiscal year, if asked), so the plan is the person's.
  for (const n of m.needs.filter((x) => x.options.length > 0 && x.kind !== 'grant' && x.kind !== 'grant_declined')) {
    const opt = n.options[0];
    has(await clickText(`[data-testid="sp-need-${n.id}"]`, opt), `answer ${n.id} with ${opt}`);
    await wait(500);
    v = await settle(`answer ${n.id}`);
    say(`answered ${n.kind} ${n.id}: ${opt}`);
  }

  // 3b. join 7: the person's own line controls, through the real page against the real engine.
  v = await controlsScene(hostInfo.tier);
  v = await settle('controls', { ms: 120000 });
  m = await shoot('after-controls', v);

  // 4. approve (the person; the human pipe's HumanGovernanceRpcTarget) for the digest the page shows.
  const shownBefore = served?.digest;
  has(await clickId('sp-approve-head'), 'the Approve button');
  await wait(800);
  v = await settle('approved', { ms: 20000 });
  m = await shoot('approved', v);
  const approve = calls.filter((c) => c.method === 'approveStartPlan').at(-1);
  has(approve && approve.params[0] === shownBefore && approve.params[0] === approve.engineDigestBefore, `approve was sent the digest the page showed (${approve?.params[0]}), which was the engine's (${approve?.engineDigestBefore})`);
  has(v.approval?.digest === v.digest && v.canBuild, `the engine holds a current approval and can build: ${v.blockedBecause}`);
  has(m.approval === 'current' && m.buttons.some((b) => /^Build$/.test(b.text) && !b.disabled), `the page shows the approval and a live Build: ${JSON.stringify(m.buttons)}`);

  // 5. build.
  has(await clickId('sp-build-head'), 'the Build button');
  for (let i = 0; i < 120 && (await page.evaluate(MEASURE)).built !== 'yes'; i++) await wait(500);
  v = await settle('built', { needSettled: false, ms: 30000 });
  m = await shoot('built', v);
  const build = calls.filter((c) => c.method === 'buildStartPlan').at(-1);
  has(build && build.params[0] === approve?.params[0] && build.params[1] === 'human', `build was sent the approved digest as the person: ${JSON.stringify(build?.params)}`);
  has(v.built && m.built === 'yes', 'the engine and the page both say built');
  has(/One undo takes it back/.test(m.said ?? ''), `the page says how to take it back: "${m.said}"`);
  has(m.buttons.length === 0 && m.discard, `Discard is the one thing offered after the build: ${JSON.stringify(m.buttons)}`);
  has(existsSync(folder), `the model folder was made: ${folder}`);
} catch (e) {
  notes.push('the journey threw: ' + (e?.stack ?? e));
} finally {
  writeFileSync(join(OUT, 'results.json'), JSON.stringify({ host: hostInfo, words, calls, results, notes, errors }, null, 2));
  await browser.close().catch(() => {});
  server.close();
  human.close(); agent.close();
  await stopHost();
}
console.log(`\n${results.length} steps, ${calls.length} engine calls from the page (${[...new Set(calls.map((c) => c.method))].join(', ')}).`);
for (const n of notes) console.log(`  ! ${n}`);
for (const e of errors.slice(0, 10)) console.log(`  ! page ${e}`);
console.log(`results: ${join(OUT, 'results.json')}`);
if (notes.length || errors.length) process.exitCode = 1;
