// (slot-calm) The header slot across a model change and a run: no blank frame. Based on calm-drive.mjs.
// Browser-driven screens of the calm Plan page (1.5, pick C "Header does it all"), headless, engine mocked. It plays a 12-row Contoso plan
// (and a 120-row one) through the real bundle: at rest, a run, the end, one row on its own, the filter and a folded group.
//
//   ROOT=<worktree with a built media/studio> OUT=<dir> LABEL=after|before [SCALE=0.4] node tools/uishot/calm-drive.mjs
//   LABEL=before skips the scenes the old page does not have (rows' own Run, the filter, groups).
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const here = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(process.env.ROOT || join(here, '..', '..'));
const OUT = process.env.OUT || join(ROOT, 'shots', 'calm');
const LABEL = process.env.LABEL || 'after';
const SCALE = Number(process.env.SCALE || 1);
mkdirSync(OUT, { recursive: true });
if (!existsSync(join(ROOT, 'media', 'studio', 'studio.js'))) { console.error('media/studio/studio.js is missing in ' + ROOT); process.exit(1); }

// ---- the plans ----
const why = ['Semanticus found this on the model.'];
const row = (id, title, doer, stateWord, extra = {}) => ({
  id, section: 'Meaning', title, sub: extra.sub ?? '', who: doer === 'you' ? 'you' : doer === 'assistant' ? 'asst' : 'auto', status: 'proposed', state: 'gap', confidence: 0.8, why,
  source: 'found', doer, stateWord, goal: extra.goal ?? 'quality', goals: [extra.goal ?? 'quality'], rules: [], objects: extra.objects ?? [], waitsFor: extra.waitsFor ?? [],
  jump: extra.jump ?? { tool: 'readiness', label: 'AI readiness' }, doneText: 'The model check passes.', stateNote: extra.stateNote ?? null, order: extra.order ?? null, ...(extra.rest ?? {}),
});
const plan12 = (stage) => {
  const done = stage === 1;
  const w = (id, title) => ({ id, title });
  const items = [
    row('words/descriptions', 'Rewrite 3 descriptions', 'assistant', 'ready', { goal: 'ai', order: 1, jump: { tool: 'instructions', label: 'Model notes' } }),
    row('words/measures', 'Describe 2 measures', 'assistant', 'ready', { goal: 'ai', order: 2 }),
    row('safefix/names', 'Make 9 names easier to read', 'assistant', 'ready', { goal: 'ai', order: 3 }),
    row('starter/Customer', 'Add 3 measures for the numbers in Customer', 'semanticus', done ? 'done' : 'ready', { goal: 'ai', jump: { tool: 'advmodels', label: 'Advanced modelling' } }),
    row('starter/Product', 'Add 3 measures for the numbers in Product', 'semanticus', done ? 'done' : 'waiting', { goal: 'ai', waitsFor: done ? [] : [w('starter/Customer', 'Add 3 measures for the numbers in Customer')], objects: ['table:Product'] }),
    row('starter/Discount', 'Add 1 measure for the numbers in Discount Scenario', 'semanticus', done ? 'done' : 'waiting', { goal: 'ai', waitsFor: done ? [] : [w('starter/Customer', 'Add 3 measures for the numbers in Customer')] }),
    row('rel/sales-date', 'Join Sales to Date on Order Date', 'semanticus', done ? 'done' : 'ready', { jump: { tool: 'relationships', label: 'Relationships' } }),
    row('summ/spend', "Set 3 columns to Don't summarize in Customer Spend Band", 'you', 'needs_you', { sub: 'could not be checked this time' }),
    row('summ/metrics', "Set 1 column to Don't summarize in Metrics", 'you', 'needs_you', { sub: 'could not be checked this time' }),
    row('fmt/sales', 'Add folders and formats to 2 measures in Sales', 'semanticus', done ? 'done' : 'waiting', { waitsFor: done ? [] : [w('rel/sales-date', 'Join Sales to Date on Order Date')] }),
    row('pt-1', 'Pick a sort order for Fiscal Month', 'you', 'needs_you', { source: 'words', jump: null, rest: { id: 'pt-1', doneWhen: 'you_mark' } }),
  ];
  return {
    modelName: 'Contoso', revision: done ? 2 : 1, items: done ? items.filter((i) => i.stateWord !== 'done') : items, history: [], tables: [], readyDigest: done ? null : 'rd1', assistantQueue: 'on',
    note: '2 tasks on the Plan could not be checked this time, so Semanticus cannot tell if they are finished. They stay on the list, marked as not checked.',
    strip: { gradeNow: done ? 'A' : 'A', gradeAfter: null, bestPractice: 'D', tracks: [] },
  };
};
const TABLES = ['Sales', 'Customer', 'Product', 'Date', 'Store', 'Orders', 'Returns', 'Budget', 'Geography', 'Employee'];
const plan120 = () => {
  const items = [];
  for (let i = 0; i < 120; i++) {
    const t = TABLES[i % TABLES.length], k = i % 6;
    if (k < 2) items.push(row(`w/${i}`, `Write a description for ${t} column ${i}`, 'assistant', 'ready', { goal: 'ai', order: i, objects: [`column:${t}/C${i}`] }));
    else if (k < 5) items.push(row(`s/${i}`, `Hide the key column ${i} in ${t}`, 'semanticus', i % 4 === 0 ? 'waiting' : 'ready', { goal: i % 2 ? 'ai' : 'quality', objects: [`column:${t}/K${i}`], waitsFor: i % 4 === 0 ? [{ id: 's/2', title: 'x' }] : [] }));
    else items.push(row(`y/${i}`, `Choose a sort order for ${t} month ${i}`, 'you', 'needs_you', { objects: [`table:${t}`] }));
  }
  return { modelName: 'Contoso Big', revision: 1, items, history: [], tables: [], readyDigest: 'rd1', assistantQueue: 'on', strip: { gradeNow: 'B', bestPractice: 'C', tracks: [] } };
};
const withChangeSet = (plan, id) => ({ ...plan, items: plan.items.map((i) => (i.id === id ? { ...i, changeSet: { state: 'proposed', digest: 'dg-' + id, lines: [{ id: 'l1', line: 'x', kept: true }], sentence: 'ok' } } : i)) });

const seed = (cfg) => `
const __cfg = ${JSON.stringify(cfg)};
window.__calls = [];
let __stage = 0;
const __plans = __cfg.plans;
const __cur = () => __plans[Math.min(__stage, __plans.length - 1)];
const __note = (name, params) => window.__calls.push({ call: name, params });
const __fire = (payload) => window.dispatchEvent(new MessageEvent('message', { data: { type: 'planRunProgress', payload } }));
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true,
  get: () => __h,
  set: (v) => {
    __h = v;
    v.setResponse('getModelPlan', () => { __note('getModelPlan'); return __cur(); });
    v.setResponse('readingCard', () => ({ show: true, grade: __cfg.grade[Math.min(__stage, 1)][0], score: __cfg.grade[Math.min(__stage, 1)][1], gradeAfter: null, scoreAfter: null, heldBy: [], safeFixCount: 3, writingCount: 5, writingJobs: 2, openFindings: 14, revision: 1, next: '3 writing tasks go to your assistant.',
      top: __stage > 0 ? [] : [{ key: 'd1', count: 3, headline: '3 descriptions need a rewrite', why: 'A weak description helps no one.', who: 'assistant', holdsGrade: false, state: 'open', ruleIds: [] }, { key: 'n1', count: 9, headline: '9 names are hard to read', why: 'People and Copilot both guess wrong.', who: 'assistant', holdsGrade: false, state: 'open', ruleIds: [] }, { key: 'm1', count: 2, headline: '2 measures have no description', why: 'Copilot has to guess what they mean.', who: 'assistant', holdsGrade: false, state: 'open', ruleIds: [] }] }));
    v.setResponse('getNextStep', () => ({ id: 's1', on: true, stage: 2, stageName: 'Get it AI ready', step: { kind: 'run_plan', ask: 'Ask your assistant to run the plan', why: '3 tasks are ready. They run in Plan order.', source: 'From your Plan', prompt: 'Work through my Semanticus plan.' } }));
    v.setResponse('applyPlanItems', (params) => {
      __note('applyPlanItems', params);
      const ids = (params && params[0] && params[0].length) ? params[0].map((x) => x.id) : __cfg.runIds;
      ids.forEach((id, k) => {
        setTimeout(() => __fire({ runId: 'r1', itemIds: [id], phase: 'work', wave: 1, ms: k * __cfg.step }), k * __cfg.step);
        setTimeout(() => __fire({ runId: 'r1', itemIds: [id], phase: 'apply', wave: 1, changes: 4, ms: k * __cfg.step + 300 }), k * __cfg.step + __cfg.step * 0.4);
        setTimeout(() => __fire({ runId: 'r1', itemIds: [id], phase: 'done', wave: 1, changes: 4, ms: k * __cfg.step + 600 }), k * __cfg.step + __cfg.step * 0.9);
      });
      return new Promise((resolve) => setTimeout(() => { __stage = 1; resolve({ performed: true, applied: ids.length * 4, itemIds: ids, revision: 2, entryId: 'ed-1', canSave: true, plan: __cur() }); }, ids.length * __cfg.step + 200));
    });
    v.setResponse('modelPlanAction', (params) => { __note('modelPlanAction', params); const p = __cur(); return { itemId: params[0], action: params[1], performed: false, plan: __cfg.proposeFor ? Object.assign({}, p, { items: p.items.map((i) => (i.id === params[0] ? Object.assign({}, i, { changeSet: { state: 'proposed', digest: 'dg-' + i.id, lines: [{ id: 'l1', line: 'x', kept: true }] } }) : i)) }) : p }; });
    v.setResponse('undo', (params) => { __note('undo', params); __stage = 0; return { canUndo: false, canRedo: true }; });
    v.setResponse('addPlanTask', (params) => { __note('addPlanTask', params); return { itemId: 'pt-9', plan: __cur() }; });
    v.setResponse('setAssistantQueue', (params) => { __note('setAssistantQueue', params); return Object.assign({}, __cur(), { assistantQueue: params[0] }); });
    v.setResponse('setCompatibilityLevel', (params) => { __note('setCompatibilityLevel', params); return {}; });
    for (const name of ['setPlanItemDoer', 'claimPlanTask', 'skipModelPlanItem', 'movePlanTaskUp', 'completePlanTask']) v.setResponse(name, (params) => { __note(name, params); return __cur(); });
  },
});
let __acquire; Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true, get: () => __acquire, set: (original) => { __acquire = () => { const api = original(); const post = api.postMessage;
  api.postMessage = (m) => { post(m); if (m.type === 'studioReady') setTimeout(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'driveMode', mode: 'claudeDrives', assistantConnected: true } })), 30); }; return api; }; } });`;

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(ROOT, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(ROOT) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 600000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const text = `(sel) => { const e = document.querySelector(sel); return e ? e.textContent.replace(/\\s+/g, ' ').trim() : null; }`;
const notes = [];
const say = (m) => { console.log(m); notes.push(m); };

async function open(theme, width, cfg) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width, height: width < 700 ? 900 : 960, deviceScaleFactor: 1 });
  await page.emulateTimezone('UTC');
  await page.evaluateOnNewDocument(seed(cfg));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&tier=pro&tab=plan&compat=1606&dirty=0`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('[data-testid="plan-page"]', { timeout: 20000 });
  await applyTheme(page, theme);
  await wait(1800);
  return { page, errors };
}
const CFG12 = { plans: [plan12(0), plan12(1)], grade: [['A', 91], ['A', 94]], runIds: ['starter/Customer', 'rel/sales-date', 'starter/Product', 'starter/Discount', 'fmt/sales'], step: Math.round(1100 * SCALE), proposeFor: true };
const CFG120 = { plans: [plan120(), plan120()], grade: [['B', 84], ['B', 84]], runIds: [], step: 100, proposeFor: true };
const shot = async (page, name) => { await page.screenshot({ path: join(OUT, `${name}.png`), fullPage: false }); };
const overflow = (page) => page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
const offenders = (page) => page.evaluate(() => [...document.querySelectorAll('body *')].filter((e) => { const r = e.getBoundingClientRect(); return r.width > 0 && r.right > window.innerWidth + 1; })
  .slice(0, 6).map((e) => `${e.tagName.toLowerCase()}.${String(e.className).split(' ').slice(0, 2).join('.')} right=${Math.round(e.getBoundingClientRect().right)}`));


// ---- slot-calm: frames across a model change and a run ----------------------------------------------------------------------------
const stepView = (id, ask, by, kind = 'describe') => ({ id, on: true, stage: 2, stageName: 'Get it AI ready', hidden: false,
  step: { kind, ask, why: 'Jev looked at your Plan and your model.', source: by === 'jev' ? 'Jev chose this' : 'From your Plan', chosenBy: by, prompt: ask, doIt: null } });
const push = (page, v) => page.evaluate((view) => window.dispatchEvent(new MessageEvent('message', { data: { type: 'studioNextStep', payload: view } })), v);
const sampler = (page) => page.evaluate(() => {
  window.__frames = [];
  const t0 = performance.now();
  window.__sample = setInterval(() => {
    const slot = document.querySelector('[data-testid="plan-slot"]');
    const words = document.querySelector('[data-testid="plan-next-words"]');
    const out = document.querySelector('[data-testid="plan-next-out"]');
    window.__frames.push({ t: Math.round(performance.now() - t0), kind: slot?.getAttribute('data-slot') ?? 'MISSING',
      text: (slot?.textContent ?? '').replace(/\s+/g, ' ').trim(), wordsOpacity: words ? Number(getComputedStyle(words).opacity) : null, fading: !!out });
  }, 40);
});
const frames = (page) => page.evaluate(() => { clearInterval(window.__sample); return window.__frames; });
const compress = (fr) => { const out = []; for (const f of fr) { const last = out[out.length - 1]; const key = f.kind + '|' + f.text; if (last && last.key === key) { last.to = f.t; } else out.push({ key, kind: f.kind, text: f.text, from: f.t, to: f.t }); } return out; };

for (const theme of ['dark', 'light']) {
  const tag = `${LABEL}-${theme}-1440`;
  const { page, errors } = await open(theme, 1440, CFG12);
  const A = stepView('ns-aaaaaaaa001', 'Ask your assistant to describe the 5 new measures', 'jev');
  const B = stepView('ns-bbbbbbbb002', 'Ask your assistant to run the plan', 'rules', 'run-plan');
  const C = stepView('ns-cccccccc003', 'Ask your assistant to check the new measures', 'jev', 'check');
  await push(page, A);
  await wait(400);
  await shot(page, `${tag}-1-rest`);
  await sampler(page);
  // a model change: what the engine really sends while it works the step out again
  await push(page, B); await wait(300);                                  // the rules' answer, before Jev has chosen
  await push(page, { ...B, id: 'ns-dddddddd004', step: null }); await wait(300);   // a recompute with no step yet
  await page.evaluate(() => { window.__uishotHarness.setResponse('getNextStep', () => { throw new Error('engine busy'); }); });
  await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'reconnected' } })));
  await wait(700);
  await shot(page, `${tag}-2-recomputing`);
  await push(page, C);                                                    // Jev lands: the one real change
  await wait(120);
  await shot(page, `${tag}-3-crossfade`);
  await wait(900);
  await shot(page, `${tag}-4-new-step`);
  // a run: the run words take the slot, then the Done line, then the step returns
  await page.evaluate((s) => document.querySelector(s)?.click(), '[data-testid="plan-run"]');
  await wait(CFG12.step * 2.4);
  await shot(page, `${tag}-5-running`);
  await wait(CFG12.step * 4 + 1500);
  await shot(page, `${tag}-6-done`);
  await wait(9500);
  await shot(page, `${tag}-7-step-back`);
  const fr = await frames(page);
  const timeline = compress(fr);
  const blank = fr.filter((f) => f.text === '' || f.kind === 'MISSING');
  const quietDuringChange = fr.filter((f) => f.kind === 'quiet' && f.t < 3000);
  say(`${tag}: ${fr.length} frames, blank frames ${blank.length}, quiet-line frames during the model change ${quietDuringChange.length}, errors ${JSON.stringify(errors)}`);
  for (const seg of timeline) say(`  ${String(seg.from).padStart(6)}-${String(seg.to).padStart(6)} ms  [${seg.kind}] ${seg.text.slice(0, 90)}`);
  await page.close();
}
// the Next bar is gone from the other pages: a plain page and the Plan both have no bar
{
  const page = await browser.newPage();
  await page.setViewport({ width: 1440, height: 960, deviceScaleFactor: 1 });
  await page.evaluateOnNewDocument(seed(CFG12));
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?conn=1&tier=pro&tab=diagram&dirty=0`, { waitUntil: 'networkidle0', timeout: 30000 });
  await wait(2000);
  const bar = await page.evaluate(() => document.querySelectorAll('[data-testid="next-line"], .nx-line, [data-studio-next-line]').length);
  await shot(page, `${LABEL}-diagram-no-next-bar`);
  say(`${LABEL}: Next bar elements on the Diagram page: ${bar}`);
  await page.close();
}
await browser.close(); server.close();
import('node:fs').then((fs) => fs.writeFileSync(join(OUT, 'frames.txt'), notes.join('\n') + '\n'));
