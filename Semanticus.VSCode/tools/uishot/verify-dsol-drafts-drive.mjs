// Browser-driven proof of the drafts class (join 11): typed, unsaved words outlive the editor.
//
// It serves THIS worktree's built Studio (media/studio) over the harness's mock engine and drives the REAL React page with real keys and clicks. Each scene types into
// one editor, then does one of the three things that used to lose the words, and READS the page afterwards:
//   switch   go to another tab and come back (every tab except Tests unmounts)
//   restart  the engine restarts: the session id changes and the page is told it reconnected (App.tsx remounts every tab on the new id and lands on the Plan, so the scene goes back to the editor)
//   moved    the assistant changes the saved value while the person is away; coming back must show Keep mine / Use the new one and write nothing
// Every scene states what it MUST show (`expect`) and the run fails when one does not, so an exit of 0 is worth quoting. The PNG of each scene is read by a person.
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=<chromium> node tools/uishot/drafts-drive.mjs
//   ONLY=instructions-switch,notes-restart node tools/uishot/drafts-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'vdsol');
mkdirSync(OUT, { recursive: true });
const ONLY = (process.env.ONLY || '').split(',').map((x) => x.trim()).filter(Boolean);
const WIDTH = parseInt(process.env.UISHOT_W || '1200', 10);
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) { console.error('media/studio/studio.js is missing. Run npm run build:webview in Semanticus.VSCode first.'); process.exit(1); }

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `drafts-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' },
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// ---- steps ---------------------------------------------------------------------------------------------------------
// One step is one thing a person (or the engine) does. Waits are short because the harness answers at once; `settle` is the longest a React re-render needs.
const SETTLE = 450;
const nav = (tab) => ({ nav: tab });
const click = (selector, text) => ({ click: selector, text });
const typeIn = (selector, value, opts = {}) => ({ type: selector, value, ...opts });
const restart = () => ({ restart: true });
const assistant = (method, value) => ({ assistant: method, value });
const probe = (label) => ({ probe: label });
const eval_ = (code) => ({ eval: code });
// The Studio panel is closed and opened again: a NEW webview with none of the old one's state. The host (extension.ts) kept the last `draftsHeld` the page handed it in workspaceState and gives
// it to the page it opens as window.__semanticusHeldDrafts, which this step does with the last such message the harness recorded.
const reopen = () => ({ reopen: true });

async function runStep(page, step, ctx) {
  if (step.nav) {
    await page.evaluate((tab) => window.__uishotHarness.dispatch({ type: 'navigate', tab }), step.nav);
    await wait(SETTLE + 300);
  } else if (step.click) {
    const ok = await page.evaluate((sel, text) => {
      const all = [...document.querySelectorAll(sel)].filter((e) => e.getClientRects().length && e.offsetHeight > 0);
      const label = (e) => (e.textContent || '').trim();
      const hit = text ? (all.find((e) => label(e) === text) ?? all.find((e) => label(e).startsWith(text))) : all[0];
      if (hit) hit.click();
      return !!hit;
    }, step.click, step.text ?? null);
    if (!ok) ctx.notes.push(`no ${step.click}${step.text ? ` "${step.text}"` : ''} to click`);
    await wait(SETTLE);
  } else if (step.type) {
    const handle = await page.$(step.type);
    if (!handle) { ctx.notes.push(`no ${step.type} to type into`); return; }
    await handle.focus();
    // End of the box, so typed words are appended to the saved words and the draft differs from where it started.
    await page.keyboard.down('Control'); await page.keyboard.press('End'); await page.keyboard.up('Control');
    await page.keyboard.type(step.value, { delay: 5 });
    await wait(SETTLE);
  } else if (step.restart) {
    await page.evaluate(() => {
      const h = window.__uishotHarness, s = h.response('sessionInfo');
      h.setResponse('sessionInfo', () => ({ ...s, sessionId: (s.sessionId || 'uishot') + '-r' }));
      h.dispatch({ type: 'reconnected' });
    });
    await wait(SETTLE + 1500);
  } else if (step.reopen) {
    await wait(400);   // the page's own throttle (drafts.ts) hands the host the last change within 250 ms
    const kept = await page.evaluate(() => { const m = (window.__uishotHarness.messages || []).filter((x) => x && x.type === 'draftsHeld').pop(); return m ? m.value : null; });
    if (!kept || !Object.keys(kept).length) ctx.notes.push('the page never handed the host its drafts (no draftsHeld message)');
    ctx.keptByHost = kept;
    await page.evaluateOnNewDocument((k) => { if (k) window.__semanticusHeldDrafts = k; }, kept);
    await page.goto(page.url(), { waitUntil: 'networkidle0', timeout: 30000 });   // a new page: the harness's persisted state is a fresh, empty object
    await page.waitForSelector(ctx.ready, { timeout: 20000 });
    await wait(1500);
  } else if (step.assistant) {
    await page.evaluate((method, value) => window.__uishotHarness.setResponse(method, () => value), step.assistant, step.value);
    await wait(100);
  } else if (step.eval) {
    await page.evaluate(step.eval);
    await wait(SETTLE);
  } else if (step.probe) {
    ctx.probes[step.probe] = await page.evaluate(ctx.read);
  }
}

// What the page says now: the open editors' text, the marks, the choice banners, and every engine write sent so far.
const READ = () => {
  const vis = (e) => e.getClientRects().length > 0;
  const boxes = [...document.querySelectorAll('textarea, input[type="text"], input:not([type])')].filter(vis).map((e) => ({ label: e.getAttribute('aria-label') || e.getAttribute('placeholder') || e.id || e.name || '', value: e.value }));
  // A DAX field is a CodeMirror box: its label sits on the wrapper, and its text is the box's own text.
  for (const c of document.querySelectorAll('.cm-content')) if (vis(c)) boxes.push({ label: c.closest('[aria-label]')?.getAttribute('aria-label') || 'dax', value: c.innerText.replace(/\n$/, '') });
  const otherLine = [...document.querySelectorAll('[data-testid="other-model-words"]')].filter(vis).map((e) => (e.textContent || '').trim());
  const otherRows = [...document.querySelectorAll('[data-testid="other-model-row"]')].filter(vis).map((e) => (e.textContent || '').trim());
  const marks = [...document.querySelectorAll('.sem-draft-mark, [data-testid="draft-mark"]')].filter(vis).map((e) => (e.textContent || '').trim());
  const banners = [...document.querySelectorAll('[role="alert"]')].filter(vis).map((e) => (e.textContent || '').trim()).filter((t) => /changed while you were editing/.test(t));
  const choices = [...document.querySelectorAll('button')].filter(vis).map((b) => (b.textContent || '').trim()).filter((t) => t === 'Keep mine' || t === 'Use the new one');
  const writes = (window.__uishotHarness.messages || []).filter((m) => m.method && /^(set|save|edit|write|update)/i.test(m.method)).map((m) => m.method);
  const policyChecked = [...document.querySelectorAll('[aria-label="Incremental refresh settings"] input[type="checkbox"]')].filter(vis).map((c) => c.checked);
  const lastMsg = (window.__uishotHarness.messages || []).filter((x) => x && x.method === 'setAiInstructions').pop(); return { boxes, marks, banners, choices, writes, policyChecked, otherLine, otherRows, pageText: document.body.innerText, lastSet: lastMsg ? JSON.stringify(lastMsg.params) : null };
};

const has = (cond, msg) => (cond ? [] : [msg]);
const box = (r, label) => r.boxes.find((b) => b.label.includes(label));

// ---- scenes --------------------------------------------------------------------------------------------------------

const INSTR = { present: true, instructions: 'Use net sales for revenue questions.', length: 36, limit: 2000, culture: 'en-US' };
const TYPED = ' Always show the currency.';
const NEWER = 'Use gross sales for revenue questions.';
const SCENES = [];
const scene = (slug, desc, def) => SCENES.push({ slug, desc, ...def });
const lastCall = (method) => `(() => { const m = (window.__uishotHarness.messages || []).filter((x) => x && x.method === ${JSON.stringify(method)}).pop(); return m ? JSON.stringify(m.params) : null; })()`;
const keepMineState = () => ({ eval: `window.__keep = [...document.querySelectorAll('[data-testid="moved-keep-mine"]')].map((b) => b.disabled)` });

// F11: the banner SHOWS the newer value, Keep mine is pressable only then, and the Save after Keep mine names that newer value.
const instructionsOpen = [click('[data-testid="overview-instructions"] button', 'Edit'), typeIn('#overview-instructions-editor', TYPED)];
scene('f11-instructions-keepmine', 'F11 AI instructions: the assistant rewrites them while the person is away; the banner shows the new text; Keep mine then Save names it', {
  q: 'tab=instructions', ready: '[data-testid="overview-instructions"]',
  steps: [...instructionsOpen, nav('plan'), assistant('getAiInstructions', { ...INSTR, instructions: NEWER }), nav('instructions'), probe('back'),
    eval_(`window.__theirs = document.querySelector('[data-testid="moved-theirs"]')?.textContent || null; window.__keep = [...document.querySelectorAll('[data-testid="moved-keep-mine"]')].map((b) => b.disabled)`),
    click('[data-testid="moved-keep-mine"]'), probe('kept'), click('button', 'Save'), probe('saved')],
  expect: (p) => [
    ...has(p.back.banners.length === 1, `no moved banner: ${JSON.stringify(p.back.banners)}`),
    ...has(p.back.banners.some((t) => t.includes(NEWER)), `the banner does not show the newer text: ${JSON.stringify(p.back.banners)}`),
    ...has(p.back.writes.length === 0, `something was written before the person chose: ${p.back.writes}`),
    ...has(p.kept.boxes.some((b) => b.value.endsWith(TYPED)), 'Keep mine lost the typed words'),
    ...has((p.saved.lastSet || '').includes(NEWER), `the Save after Keep mine does not name the newer text as the one it replaces: ${p.saved.lastSet}`),
  ],
});

// F12: Power Query / M words are held (tab switch, area switch, restart, panel reopen) and marked.
const MBOX = '[aria-labelledby="m-editor-heading"] .cm-content';
const mType = [typeIn(MBOX, '\n// note')];
const mExpect = (p) => [
  ...has(p.typed.boxes.some((b) => b.value.includes('// note')), `the M words were not typed: ${JSON.stringify(p.typed.boxes.map((b) => b.label))}`),
  ...has(p.typed.marks.some((m) => /Draft not saved/.test(m)), 'no Draft not saved mark while typing'),
  ...has(p.back.boxes.some((b) => b.value.includes('// note')), `the M words are not back: ${JSON.stringify(p.back.boxes.map((b) => b.value.slice(-40)))}`),
  ...has(p.back.marks.some((m) => /Draft not saved/.test(m)), 'no mark when back'),
  ...has(p.back.writes.length === 0, `something was written: ${p.back.writes}`),
];
scene('f12-mquery-switch', 'F12 Power Query: type M, go to the Plan, come back', { q: 'tab=mcode', ready: MBOX, height: 1400, steps: [...mType, probe('typed'), nav('plan'), nav('mcode'), probe('back')], expect: mExpect });
scene('f12-mquery-restart', 'F12 Power Query: type M, the engine restarts, go back', { q: 'tab=mcode', ready: MBOX, height: 1400, steps: [...mType, probe('typed'), restart(), nav('mcode'), probe('back')], expect: mExpect });
scene('f12-mquery-reopen', 'F12 Power Query: type M, close the Studio panel, open it again', { q: 'tab=mcode', ready: MBOX, height: 1400, steps: [...mType, probe('typed'), reopen(), nav('mcode'), probe('back')], expect: mExpect });

// F13: JSON typed, Hide JSON, a structured edit, leave the page, come back: both are held, JSON opens on its words, Apply asks.
scene('f13-spec-json-and-struct', 'F13 Spec: JSON words, Hide JSON, a structured edit, leave and come back', {
  q: 'tab=spec', ready: '.sem-page, .xe-under-bar, section',
  steps: [click('button', 'Edit JSON'), typeIn('textarea[spellcheck="false"]', ' '), probe('typed'), click('button', 'Hide JSON'),
    typeIn('input[aria-label="Model name"]', ' mine'), eval_('document.activeElement.blur()'), nav('plan'), nav('spec'), probe('json'), click('button', 'Hide JSON'), probe('back'), click('button', 'Edit JSON'), probe('json2'),
    click('button', 'Apply JSON'), probe('apply')],
  expect: (p) => [
    ...has(p.typed.boxes.some((b) => /\s$/.test(b.value)), 'precondition: the JSON words were typed'),
    ...has(p.back.boxes.some((b) => b.label === 'Model name' && b.value.endsWith(' mine')), `the structured edit is not back: ${JSON.stringify(p.back.boxes.map((b) => b.label + '=' + b.value.slice(0, 30)))}`),
    ...has(/Draft not saved|Unsaved changes/.test(p.back.pageText), 'no mark'),
    ...has(p.json.boxes.some((b) => /\s$/.test(b.value) && !/ mine"/.test(b.value)), `the JSON view did not come back on the person's JSON words: ${JSON.stringify(p.json.boxes.map((b) => b.label + '=' + b.value.slice(-40)))}`),
    ...has(p.json2.boxes.some((b) => /\s$/.test(b.value) && !/ mine"/.test(b.value)), `Edit JSON after Hide JSON did not open on the JSON words: ${JSON.stringify(p.json2.boxes.map((b) => b.value.slice(-40)))}`),
    ...has(/spec-both|Keep the JSON words|Use my structured edits/.test(p.apply.pageText) || /Keep the JSON words/.test(p.apply.pageText), `Apply JSON did not ask with both held: ${p.apply.pageText.slice(0, 300)}`),
    ...has(p.apply.writes.length === 0, `something was written: ${p.apply.writes}`),
  ],
});

// ---- run -----------------------------------------------------------------------------------------------------------
const results = [];
for (const sc of SCENES) {
  if (ONLY.length && !ONLY.includes(sc.slug)) continue;
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.setViewport({ width: WIDTH, height: sc.height ?? 900, deviceScaleFactor: 1 });
  const ctx = { notes: [], probes: {}, read: READ, ready: sc.ready };
  try {
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${sc.q}`, { waitUntil: 'networkidle0', timeout: 30000 });
    await page.waitForSelector(sc.ready, { timeout: 20000 });
    await wait(1500);
    for (const step of sc.steps) await runStep(page, step, ctx);
    // The shot shows the mark: scroll it into the middle of the window first.
    await page.evaluate(() => { const m = [...document.querySelectorAll('.sem-draft-mark')].find((e) => e.getClientRects().length); if (m) m.scrollIntoView({ block: 'center' }); });
    await wait(200);
    const out = join(OUT, `${sc.slug}.png`);
    if (sc.shot) { await page.evaluate((sel) => document.querySelector(sel)?.scrollIntoView({ block: 'start' }), sc.shot); await wait(200); }
    await page.screenshot({ path: out });
    if (process.env.DEBUGSEL) console.log(await page.evaluate((sel) => { const e = document.querySelector(sel); if (!e) return 'none'; const r = e.getBoundingClientRect(); return JSON.stringify({ r, display: getComputedStyle(e).display, parent: e.parentElement?.className, scroll: document.scrollingElement.scrollTop }); }, process.env.DEBUGSEL));
    const failures = [...sc.expect(ctx.probes), ...ctx.notes, ...errors];
    results.push({ scene: sc.slug, desc: sc.desc, out, failures });
  } catch (e) {
    results.push({ scene: sc.slug, desc: sc.desc, failures: ['FATAL: ' + e.message, ...ctx.notes] });
  } finally { await page.close(); }
}
await browser.close();
server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
for (const r of results) console.log(`${r.failures.length ? 'FAIL' : 'ok  '} ${r.scene}${r.failures.length ? ': ' + r.failures.join(' | ') : ''}`);
const broken = results.filter((r) => r.failures.length);
if (broken.length) { console.error(`\n${broken.length} of ${results.length} scenes did not show what they promised`); process.exit(1); }
console.log(`\nall ${results.length} scenes showed what they promised`);
