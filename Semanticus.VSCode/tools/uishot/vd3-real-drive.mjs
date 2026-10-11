// VERIFY (m11/drafts round 3): held words against a REAL engine whose session has a real model source (never the harness's name: fallback).
// The host is Semanticus.Tests/VerifyDraftIdentityRound3Tests.cs VerifyDrafts3StudioHostTests: a real LocalEngine and RpcServer on a pipe; open_live uses the
// assembly's snapshot seam (a new folder per open, as the real export does). An ENGINE RESTART is a new host process on the same snapshot root.
// The page is this worktree's built Studio on the harness; sessionInfo, the AI instructions verbs and the Spec verbs go to the real engine, nothing else.
//
//   TMPDIR=/tmp/vdrz OUTDIR=<dir> node tools/uishot/vd3-real-drive.mjs          (build Semanticus.Tests Debug first)
//   VD3_FALLBACK=1   the page was built with REATTACH_WORDS_AFTER_RESTART = false: a host-copy restore must be read-only
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { connect } from 'node:net';
import { copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const repoRoot = resolve(webRoot, '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'vd3-real');
mkdirSync(OUT, { recursive: true });
const FALLBACK = process.env.VD3_FALLBACK === '1';
const BASE = join(process.env.HOME, '.cache', `vd3-real-${process.pid}`);
const SNAP = join(BASE, 'snapshots');
rmSync(BASE, { recursive: true, force: true }); mkdirSync(SNAP, { recursive: true });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const t0 = Date.now();
const say = (s) => console.log(`[${((Date.now() - t0) / 1000).toFixed(1)}s] ${s}`);
const results = [];
const check = (name, cond, detail = '') => { results.push({ name, ok: !!cond, detail }); say(`${cond ? 'PASS' : 'FAIL'} ${name}${cond ? '' : ' :: ' + detail}`); };

// ---------------------------------------------------------------- the pipe client (handshake, Content-Length framing)
class PipeRpc {
  static open(socketPath, challenge) {
    return new Promise((res, rej) => {
      const sock = connect(socketPath); let pre = Buffer.alloc(0), done = false;
      sock.once('error', rej);
      sock.on('connect', () => sock.write(`SEMANTICUS-RPC/1 human ${challenge}\n`));
      const onPre = (chunk) => {
        pre = Buffer.concat([pre, chunk]); const nl = pre.indexOf(0x0a); if (nl < 0 || done) return; done = true; sock.off('data', onPre);
        const line = pre.subarray(0, nl).toString('utf8');
        if (line !== 'SEMANTICUS-RPC/1 accepted') { sock.destroy(); rej(new Error('handshake: ' + line)); return; }
        const rpc = new PipeRpc(sock); rpc.feed(pre.subarray(nl + 1)); res(rpc);
      };
      sock.on('data', onPre);
    });
  }
  constructor(sock) { this.sock = sock; this.buf = Buffer.alloc(0); this.nextId = 1; this.pending = new Map(); sock.on('data', (c) => this.feed(c)); sock.on('close', () => { for (const p of this.pending.values()) p.rej(new Error('pipe closed')); this.pending.clear(); }); }
  feed(chunk) {
    this.buf = Buffer.concat([this.buf, chunk]);
    for (;;) {
      const end = this.buf.indexOf('\r\n\r\n'); if (end < 0) return;
      const len = Number(/Content-Length:\s*(\d+)/i.exec(this.buf.subarray(0, end).toString('ascii'))[1]);
      if (this.buf.length < end + 4 + len) return;
      const msg = JSON.parse(this.buf.subarray(end + 4, end + 4 + len).toString('utf8')); this.buf = this.buf.subarray(end + 4 + len);
      if (msg.id != null && this.pending.has(msg.id)) { const p = this.pending.get(msg.id); this.pending.delete(msg.id); if (msg.error) p.rej(new Error(msg.error.message || JSON.stringify(msg.error))); else p.res(msg.result); }
    }
  }
  call(method, params = []) {
    const id = this.nextId++; const body = Buffer.from(JSON.stringify({ jsonrpc: '2.0', id, method, params }), 'utf8');
    this.sock.write(Buffer.concat([Buffer.from(`Content-Length: ${body.length}\r\n\r\n`, 'ascii'), body]));
    return new Promise((res, rej) => this.pending.set(id, { res, rej }));
  }
  close() { try { this.sock.destroy(); } catch { /* gone */ } }
}

// ---------------------------------------------------------------- hosts: one per engine process
let host = null, hostN = 0;
async function startHost() {
  const dir = join(BASE, `host${++hostN}`); mkdirSync(dir, { recursive: true });
  const env = { ...process.env, VD3_HOST_DIR: dir, VD3_SNAP_ROOT: SNAP, BROWSER: 'true', AZURE_CONFIG_DIR: '/tmp/no-az-login', MSBUILDDISABLENODEREUSE: '1', DOTNET_CLI_USE_MSBUILD_SERVER: '0' };
  delete env.OUTDIR;   // MSBuild reads an OUTDIR variable as the build's output folder
  const child = spawn('dotnet', ['test', join(repoRoot, 'Semanticus.Tests', 'Semanticus.Tests.csproj'), '--no-build', '-c', 'Debug', '--filter', 'FullyQualifiedName~VerifyDrafts3StudioHost'], { env, cwd: repoRoot, stdio: ['ignore', 'pipe', 'pipe'], detached: true });
  const log = []; child.stdout.on('data', (d) => log.push(String(d))); child.stderr.on('data', (d) => log.push(String(d)));
  child.on('exit', () => writeFileSync(join(OUT, `host${hostN}-stdout.txt`), log.join('')));
  const pj = join(dir, 'pipe.json');
  for (let i = 0; !existsSync(pj); i++) { if (child.exitCode !== null) throw new Error('host exited early: ' + log.join('')); if (i > 1500) throw new Error('no pipe.json'); await wait(200); }
  const info = JSON.parse(readFileSync(pj, 'utf8'));
  const rpc = await PipeRpc.open(info.socket, info.challenge);
  say(`engine process ${hostN} up (test host pid ${child.pid})`);
  return { child, dir, info, rpc };
}
async function stopHost(hard = false) {
  if (!host) return;
  host.rpc.close();
  // hard: the engine dies without its clean exit (VS Code killed), so it leaves its snapshot folder behind, as a cold start then finds it
  if (hard) try { process.kill(-host.child.pid, 'SIGKILL'); } catch { /* gone */ }
  writeFileSync(join(host.dir, 'stop'), '');
  for (let i = 0; i < 150 && host.child.exitCode === null && host.child.signalCode === null; i++) await wait(200);
  if (host.child.exitCode === null && host.child.signalCode === null) try { process.kill(-host.child.pid, 'SIGTERM'); } catch { /* gone */ }
  say(`engine process ${hostN} stopped`); host = null;
}
const writes = [];
async function realRpc(method, params) {
  if (!host) return { error: 'Engine not connected.' };
  try { const r = await host.rpc.call(method, params); if (/^set/.test(method)) writes.push({ method, ok: true }); return { result: r ?? null }; }
  catch (e) { if (/^set/.test(method)) writes.push({ method, ok: false, error: e.message }); return { error: e.message }; }
}

// ---------------------------------------------------------------- the page
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' }); res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const REAL = ['sessionInfo', 'getAiInstructions', 'setAiInstructions', 'getSpec', 'setSpec'];
const WRAP = `(() => {
  const REAL = new Set(${JSON.stringify(REAL)});
  let orig = null, wrapped = null;
  Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true,
    get: () => function () {
      if (wrapped) return wrapped;
      const api = orig();
      window.__vsapi = api;
      wrapped = Object.assign({}, api, { postMessage(msg) {
        if (msg && msg.type === 'rpc' && REAL.has(msg.method)) {
          window.__realRpc(msg.method, msg.params || []).then((r) => window.dispatchEvent(new MessageEvent('message', { data: r && r.error != null ? { type: 'rpcResult', id: msg.id, error: r.error } : { type: 'rpcResult', id: msg.id, result: r.result } })));
          return;
        }
        return api.postMessage(msg);
      } });
      return wrapped;
    }, set: (fn) => { orig = fn; } });
})()`;
let page = null;
async function newPage({ state = null, hostCopy = null, tab = 'instructions' } = {}) {
  if (page) await page.close();
  page = await browser.newPage();
  page.on('pageerror', (e) => say('pageerror: ' + e.message));
  await page.exposeFunction('__realRpc', realRpc);
  await page.setViewport({ width: 1200, height: 1000 });
  await page.evaluateOnNewDocument(WRAP);
  if (hostCopy) await page.evaluateOnNewDocument((k) => { window.__semanticusHeldDrafts = k; }, hostCopy);
  const q = `tab=${tab}&tier=pro` + (state ? `&state=${encodeURIComponent(JSON.stringify(state))}` : '');
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${q}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await wait(1500);
}
const nav = async (tab) => { await page.evaluate((t) => window.__uishotHarness.dispatch({ type: 'navigate', tab: t }), tab); await wait(900); };
const reconnected = async () => { await page.evaluate(() => window.__uishotHarness.dispatch({ type: 'reconnected' })); await wait(2000); };
async function clickText(sel, text) {
  const ok = await page.evaluate((s, t) => { const all = [...document.querySelectorAll(s)].filter((e) => e.getClientRects().length); const hit = all.find((e) => (e.textContent || '').trim() === t) ?? all.find((e) => (e.textContent || '').trim().startsWith(t)); if (hit) hit.click(); return !!hit; }, sel, text);
  await wait(500); return ok;
}
async function typeInto(sel, words) {
  const h = await page.$(sel); if (!h) return false;
  await h.focus(); await page.keyboard.down('Control'); await page.keyboard.press('End'); await page.keyboard.up('Control');
  await page.keyboard.type(words, { delay: 5 }); await wait(500); return true;
}
const read = () => page.evaluate(() => {
  const vis = (e) => e.getClientRects().length > 0;
  const boxes = [...document.querySelectorAll('textarea, input[type="text"], input:not([type])')].filter(vis).map((e) => ({ label: e.getAttribute('aria-label') || e.id || '', value: e.value }));
  const txt = (s) => [...document.querySelectorAll(s)].filter(vis).map((e) => (e.textContent || '').trim());
  const st = window.__vsapi ? window.__vsapi.getState() : null;
  const posted = (window.__uishotHarness.messages || []).filter((m) => m && m.type === 'draftsHeld');
  return { boxes, marks: txt('.sem-draft-mark, [data-testid="draft-mark"]'), other: txt('[data-testid="other-model-words"]'), otherRows: txt('[data-testid="other-model-row"]'),
    keys: Object.keys((st && st['studio.drafts']) || {}), state: st, hostCopy: posted.length ? posted[posted.length - 1].value : null, text: document.body.innerText };
});
const shot = (name) => page.screenshot({ path: join(OUT, `${name}.png`) });
const modelOf = (k) => JSON.parse(k)[0];
const instr = '#overview-instructions-editor';
const openInstr = async () => (await clickText('[data-testid="overview-instructions"] button', 'Edit')) || (await clickText('[data-testid="overview-instructions"] button', 'Write instructions'));
const EP = 'powerbi://api.powerbi.com/v1.0/myorg/Finance';

try {
  // ======================== 1. LOCAL FILE: type, engine restarts, words back on the same key
  host = await startHost();
  const local = join(BASE, 'models', 'Local.bim'); mkdirSync(dirname(local), { recursive: true }); copyFileSync(host.info.bim, local);
  await host.rpc.call('open', [local, true]);
  const a = await host.rpc.call('sessionInfo');
  check('local: the engine names a local file file:<path>', a.modelIdentity === 'file:' + local, JSON.stringify(a.modelIdentity));
  await newPage();
  check('instructions editor opened', await openInstr());
  check('local: typed', await typeInto(instr, ' LOCAL WORDS.'));
  let r = await read();
  check('local: held under the engine identity verbatim', r.keys.length === 1 && modelOf(r.keys[0]) === a.modelIdentity, JSON.stringify(r.keys));
  await stopHost(); host = await startHost();
  await host.rpc.call('open', [local, true]);
  const a2 = await host.rpc.call('sessionInfo');
  check('local restart: same identity from a new engine process', a2.modelIdentity === a.modelIdentity, `${a.modelIdentity} vs ${a2.modelIdentity}`);
  await reconnected(); await nav('plan'); await nav('instructions');
  r = await read(); await shot('1-local-restart');
  check('local restart: the words are back in the editor', r.boxes.some((b) => b.value.endsWith(' LOCAL WORDS.')), JSON.stringify(r.boxes));
  check('local restart: marked as a draft', r.marks.some((m) => /Draft not saved/.test(m)), JSON.stringify(r.marks));
  check('local restart: no other-models line', r.other.length === 0, JSON.stringify(r.other));

  // ======================== 1b. SPEC on the same file: content base over a real engine restart
  await host.rpc.call('setSpec', [JSON.stringify({ name: 'VD3 Spec' }), 'agent', null]);
  await nav('spec'); await wait(800);
  check('spec: typed into the structured view', await typeInto('input[aria-label="Model name"]', ' mine'));
  await page.evaluate(() => document.activeElement && document.activeElement.blur()); await wait(500);
  const v1 = await host.rpc.call('getSpec');
  await stopHost(); host = await startHost(); await host.rpc.call('open', [local, true]);
  await reconnected(); await nav('plan'); await nav('spec'); await wait(800);
  r = await read(); await shot('1b-spec-after-restart-no-spec');
  check('spec restart: the typed name is back', r.boxes.some((b) => b.label === 'Model name' && b.value === 'VD3 Spec mine'), JSON.stringify(r.boxes.filter((b) => b.label === 'Model name')));
  check('spec restart: the banner says the model holds no spec (not "changed elsewhere (now v0)")', /holds no spec now/.test(r.text) && !/now v0/.test(r.text), r.text.slice(0, 400));
  // The assistant sets a DIFFERENT spec on the restarted engine: its version number collides with the one the draft was typed over (V4).
  const other = await host.rpc.call('setSpec', [JSON.stringify({ name: 'Other Spec' }), 'agent', null]);
  check('spec: precondition, the restarted engine reuses the version number', other.version === v1.version, `${v1.version} vs ${other.version}`);
  await nav('plan'); await nav('spec'); await wait(800);
  r = await read();
  check('spec different content: the page asks', /The spec changed elsewhere/.test(r.text), r.text.slice(0, 400));
  const wBefore = writes.length;
  await clickText('button', 'Save'); await wait(1200);
  const afterSave = await host.rpc.call('getSpec');
  r = await read(); await shot('1b-spec-different-content-save-refused');
  check('spec different content: Save writes nothing', afterSave.spec?.name === 'Other Spec' && writes.slice(wBefore).filter((w) => w.method === 'setSpec').length === 0, JSON.stringify({ name: afterSave.spec?.name, w: writes.slice(wBefore) }));
  // The assistant puts the SAME content back: no ask, and Save sends the version of the read whose content matched.
  await host.rpc.call('setSpec', [JSON.stringify({ name: 'VD3 Spec' }), 'agent', null]);
  await nav('plan'); await nav('spec'); await wait(800);
  r = await read();
  check('spec equal content: no ask', !/The spec changed elsewhere/.test(r.text) && !/holds no spec now/.test(r.text), r.text.slice(0, 400));
  await clickText('button', 'Save'); await wait(1200);
  const saved = await host.rpc.call('getSpec');
  check('spec equal content: Save lands on the restarted engine', saved.spec?.name === 'VD3 Spec mine', JSON.stringify(saved.spec?.name));

  // ======================== 2. PUBLISHED: open live, type, engine restarts (NEW snapshot folder), words back
  await host.rpc.call('openLive', [EP, 'Sales', 'token', 'not-a-jwt', null, false, null, null, null, 'human', true]);
  const p1 = await host.rpc.call('sessionInfo');
  check('published: live-bound with a live identity', p1.liveBound && p1.modelIdentity === 'live:' + EP.toLowerCase() + '|sales', JSON.stringify({ lb: p1.liveBound, id: p1.modelIdentity }));
  await reconnected(); await nav('plan'); await nav('instructions');
  r = await read();
  check('published: the local file\'s words are NOT attached here', !r.boxes.some((b) => b.value.includes('LOCAL WORDS')), JSON.stringify(r.boxes));
  check('published: the other-models line names one model', r.other.some((t) => /Words kept for 1 other model/.test(t)), JSON.stringify(r.other));
  check('instructions editor opened', await openInstr());
  check('published: typed', await typeInto(instr, ' LIVE WORDS.'));
  await stopHost(); host = await startHost();
  await host.rpc.call('openLive', [EP, 'Sales', 'token', 'not-a-jwt', null, false, null, null, null, 'human', true]);
  const p2 = await host.rpc.call('sessionInfo');
  check('published restart: precondition, a NEW snapshot folder', p2.source !== p1.source, `${p1.source} / ${p2.source}`);
  check('published restart: same identity', p2.modelIdentity === p1.modelIdentity, `${p1.modelIdentity} vs ${p2.modelIdentity}`);
  await reconnected(); await nav('plan'); await nav('instructions');
  r = await read(); await shot('2-published-restart');
  check('published restart: the words are back', r.boxes.some((b) => b.value.endsWith(' LIVE WORDS.')), JSON.stringify(r.boxes));
  check('published restart: marked', r.marks.some((m) => /Draft not saved/.test(m)), JSON.stringify(r.marks));

  // ======================== 3. COLD START: VS Code closed and opened; the extension reopens the cached snapshot as a FILE
  const pageState = r.state; const hostCopy = r.hostCopy;
  check('cold: the page handed the host its words (workspaceState mirror)', hostCopy && Object.keys(hostCopy).length === 2, JSON.stringify(Object.keys(hostCopy || {})));
  await stopHost(true); check('cold: precondition, the snapshot folder is still there', existsSync(p2.source), p2.source); host = await startHost();
  await host.rpc.call('open', [p2.source, true]);
  const c = await host.rpc.call('sessionInfo');
  check('cold: the snapshot opened as a plain file is NOT live-bound', c.liveBound === false || c.liveBound == null, JSON.stringify(c.liveBound));
  check('cold: same identity as while it was open live', c.modelIdentity === p1.modelIdentity, `${p1.modelIdentity} vs ${c.modelIdentity}`);
  // 3a. Studio restored with its own state (the panel was open; VS Code serialises its state)
  await newPage({ state: pageState });
  r = await read(); await shot('3a-cold-own-state');
  check('cold, own state: the words are back in the editor', r.boxes.some((b) => b.value.endsWith(' LIVE WORDS.')), JSON.stringify(r.boxes));
  // 3b. Studio panel was closed: a NEW page with no state, the host's copy only
  writes.length = 0;
  await newPage({ hostCopy });
  r = await read(); await shot('3b-cold-host-copy');
  if (!FALLBACK) {
    check('cold, host copy: the words are back in the editor (redesign)', r.boxes.some((b) => b.value.endsWith(' LIVE WORDS.')), JSON.stringify(r.boxes));
    check('cold, host copy: one other model (the local file)', r.other.some((t) => /Words kept for 1 other model/.test(t)), JSON.stringify(r.other));
  } else {
    check('FALLBACK cold, host copy: nothing attached to the editor', !r.boxes.some((b) => /LIVE WORDS|LOCAL WORDS/.test(b.value)), JSON.stringify(r.boxes));
    check('FALLBACK cold, host copy: both models listed read-only', r.other.some((t) => /Words kept for 2 other models/.test(t)), JSON.stringify(r.other));
    await clickText('[data-testid="other-model-words-toggle"]', 'Words kept'); r = await read(); await shot('3b-fallback-list');
    check('FALLBACK: rows say "from before VS Code restarted" with Copy and Discard', r.otherRows.length === 2 && r.otherRows.every((t) => /from before VS Code restarted/.test(t) && /Copy/.test(t) && /Discard/.test(t)), JSON.stringify(r.otherRows));
    await clickText('[data-testid="other-model-discard"]', 'Discard'); r = await read();
    check('FALLBACK: Discard drops one', r.other.some((t) => /Words kept for 1 other model/.test(t)), JSON.stringify(r.other));
  }
  check('cold: nothing was written to the engine', writes.filter((w) => w.ok).length === 0, JSON.stringify(writes));
  const engineInstr = await host.rpc.call('getAiInstructions');
  check('cold: the engine still holds its own instructions (no typed words in it)', !/LIVE WORDS|LOCAL WORDS/.test(engineInstr.instructions || ''), JSON.stringify(engineInstr.instructions));
} catch (e) {
  check('drive ran to the end', false, String(e && e.stack || e));
} finally {
  await stopHost().catch(() => {});
  await browser.close().catch(() => {}); server.close();
  rmSync(BASE, { recursive: true, force: true });
}
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
const failed = results.filter((x) => !x.ok);
say(`${results.length - failed.length} PASS ${failed.length} FAIL (${FALLBACK ? 'fallback build' : 'redesign build'})`);
process.exit(failed.length ? 1 : 0);
