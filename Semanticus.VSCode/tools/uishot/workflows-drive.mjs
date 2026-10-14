// Browser-driven proof of Workflows as one page (lane W2; Plan-heart contract 21), and of a run and a draft as
// Plan rows (R3 and Codex F-275), in three themes.
//
// It serves THIS worktree's built bundle (media/studio/studio.js), drives the real React page with real clicks
// and keys, and MEASURES what landed into results.json next to its PNGs: the rows on the list, the step marks,
// the Required mark, what the drawers say, and every call a click made, with its arguments. A scene that looks
// right while saying the wrong thing fails on its `expect`.
//
// The engine is fixtures/workflows-fixture.mjs, a stateful fake that answers as a function of each call and of
// the calls before it. The Plan's rows come from fixtures/plan-heart-fixture.mjs (E1's shape file) plus the run
// and draft rows the workflow engine adds, the way LocalEngine.Plan.cs appends RunPlanRows and DraftPlanRows.
// Both files are INLINED into the page, because their functions do not survive JSON.
//
// The last scene is the design's "Play the flow": Use a starter, run it, finish it on its Plan row, open its
// report from History, press Make it reusable, and see the new workflow on the list.
//
//   OUTDIR=<dir> TMPDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/workflows-drive.mjs
//   ONLY=list,author node tools/uishot/workflows-drive.mjs      just those scenes
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { THEMES, applyTheme } from './themes.mjs';
import { checkScreen, screenFailures } from './screencheck.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'workflows');
mkdirSync(OUT, { recursive: true });
const only = process.env.ONLY ? process.env.ONLY.split(',') : null;
if (!existsSync(join(webRoot, 'media', 'studio', 'studio.js'))) {
  console.error('media/studio/studio.js is missing. Run: cd webview && npm run build');
  process.exit(1);
}

const WF_SRC = readFileSync(join(here, 'fixtures', 'workflows-fixture.mjs'), 'utf8').replace(/^export /gm, '');
const HEART_SRC = readFileSync(join(here, 'fixtures', 'plan-heart-fixture.mjs'), 'utf8').replace(/^export /gm, '');

// The engine stub, installed before the harness reads its config and before studio.js makes its first call.
const seed = (opts) => `
window.__wf = (() => { ${WF_SRC}
  return makeWorkflowsEngine(${JSON.stringify(opts)}); })();
window.__heart = (() => { ${HEART_SRC}
  return { makeHeartPlan }; })();
window.__UISHOT__ = Object.assign(window.__UISHOT__ || {}, { workflows2: window.__wf.LIST, workflowDefs: window.__wf.DEFS });
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true,
  get: () => __h,
  set: (v) => {
    __h = v;
    for (const [name, fn] of Object.entries(window.__wf.handlers)) v.setResponse(name, fn);
    // The Plan: the heart fixture's rows, then one row per live run and per open draft, as the engine appends them.
    v.setResponse('getModelPlan', () => {
      const plan = window.__heart.makeHeartPlan({ mode: 'plain' });
      const extra = [];
      for (const r of Object.values(window.__wf.RUNS)) if (r.status === 'active') extra.push({
        id: 'run/' + r.runId, title: r.title, sub: r.currentStep ? r.currentStep.title : '', section: 'structure', source: 'workflow',
        doer: 'you', who: 'you', status: 'running', state: 'gap', stateWord: 'needs_you',
        stateNote: 'step ' + (r.stepIndex + 1) + ' of ' + r.totalSteps, why: ['The workflow ' + r.workflow + ' is running. It ends as an entry in History.'],
        doneText: "Done when the workflow's last step passes.", objects: [], originLink: { kind: 'run', ref: r.runId } });
      for (const d of Object.values(window.__wf.DRAFTS)) if (d.state !== 'done') extra.push({
        id: 'draft/' + d.draftId, title: d.title, sub: d.summary, section: 'structure', source: 'words', doer: 'you', status: 'open',
        stateWord: d.planWord === 'ready' ? 'ready' : 'needs_you', stateNote: d.planNote, why: [], objects: [], originLink: { kind: 'draft', ref: d.draftId } });
      plan.items = extra.concat(plan.items);
      return plan;
    });
  },
});
(() => {})()`;

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
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// What a scene reads off the page. Every field is a measurement of the DOM, not of the fixture.
const MEASURE = `(() => {
  const text = (e) => (e ? e.textContent.replace(/\\s+/g, ' ').trim() : '');
  const q = (s) => document.querySelector(s);
  const qa = (s) => [...document.querySelectorAll(s)];
  const page = q('[data-testid="wf-page"]');
  // The design's first-screen measure (inventory section 2): every word in the Workflows area, and every
  // button, field, link and switch whose top is inside the viewport.
  const area = page || q('[data-testid="plan-page"]') || document.body;
  const controls = qa('button, input, select, textarea, [role="switch"], a').filter((el) => {
    if (!area.contains(el)) return false;
    const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0 && r.top >= 0 && r.top < window.innerHeight;
  });
  return {
    view: page ? page.dataset.view : (q('[data-testid="plan-page"]') ? 'plan' : null),
    words: (area.innerText || '').split(/\\s+/).filter(Boolean).length,
    controls: controls.length,
    count: text(q('[data-testid="wf-count"]')),
    rows: qa('[data-testid^="wf-row-"]').map((r) => ({ name: r.dataset.workflow, state: r.dataset.state,
      title: text(r.querySelector('.sem-wf-row-name b')), sub: text(r.querySelector('.sem-wf-row-name .sem-wf-note')),
      marks: [...r.querySelectorAll('.sem-wf-strip svg')].map((s) => (s.getAttribute('class') || '').replace('pw-ico pw-doer-', '')),
      cut: !!r.querySelector('.sem-wf-strip-cut'), last: text(r.querySelector('.sem-wf-last')), required: text(r.querySelector('[data-testid="wf-required-mark"]')),
      stateWord: text(r.querySelector('[data-testid="wf-state-word"]')) })),
    newPanel: text(q('[data-testid="wf-new-panel"]')),
    starters: qa('[data-testid^="wf-starter-"]').map((c) => ({ name: c.dataset.testid.slice('wf-starter-'.length), button: text(c.querySelector('button.sem-btn')) })),
    menu: text(q('[data-testid="wf-describe-menu"]')),
    teamRules: text(q('[data-testid="wf-team-rules"]')),
    // UAT-S02-2-006: how many times the page shows one error sentence (the fixture's unavailable pin).
    pinWarnings: [...document.querySelectorAll('[role="alert"], .sem-wf-page *')]
      .filter((e) => e.children.length === 0 && /is pinned to stock revision/.test(e.textContent || '')).length,
    offered: (q('[data-testid="wf-offered"]') || { dataset: {} }).dataset.offered ?? null,
    offLine: text(q('[data-testid="wf-checks-off-line"]')),
    checks: text(q('[data-testid="wf-checks-switch"]')),
    author: text(q('[data-testid="wf-author"] .sem-wf-ahead')),
    saved: text(q('[data-testid="wf-saved"]')),
    summary: text(q('[data-testid="wf-summary"]')),
    outline: qa('[data-testid^="wf-ol-"]').map((b) => text(b)),
    stepForm: text(q('[data-wf-step-form]')),
    settings: text(q('[data-testid="wf-settings"]')),
    upgrade: text(q('[data-testid="wf-upgrade-notice"]')),
    preview: text(q('[data-testid="wf-save-preview"]')),
    useDrawer: text(q('[data-testid="wf-use-drawer"]')),
    runDetail: text(q('[data-testid="wf-run-detail"]')),
    runTally: text(q('[data-testid="wf-run-tally"]')) || text(q('.sem-wf-runrail .sem-wf-note, .sem-wf-runrail span')),
    reasonBox: (q('.sem-wf-run input[placeholder]') || { placeholder: null }).placeholder,
    assistantFills: text(q('[data-testid="wf-assistant-fills"]')),
    composer: text(q('.cd-draft, [data-testid="cd-summary"]')?.closest('.pw-detail, .sem-wf-draft-host, .cd-draft') || null),
    draftAuthor: text(q('[data-testid="wf-author-draft"]')),
    history: text(q('[data-testid="ph-run-actions"]')),
    reusable: text(q('[data-testid="ph-reusable-done"]')),
    proPreview: text(q('[data-testid="pro-preview"]')),
    tabs: qa('.studio-nav button, [role="tab"]').map((b) => text(b)),
    retired: ['Home', 'Library', 'Governance', 'Runs'].filter((w) => qa('button, [role="tab"], h1, h2').some((b) => text(b) === w)),
    alert: text(q('[role="alert"]')),
    notice: text(q('.sem-wf-notice')),
    held: text(q('[data-testid="wf-held"]')),
    preview: text(q('[data-testid="wf-save-preview"]')),
    emptySteps: !!q('[data-wf-empty-steps]'),
    list: text(q('[data-testid="wf-list"]')),
    source: (q('[data-testid="wf-author"] textarea') || { value: null }).value,
    chain: (() => { const out = []; for (let e = q('.sem-wf-rail'); e; e = e.parentElement) { const cs = getComputedStyle(e); out.push(e.tagName + '.' + [...e.classList].join('.') + ' bg=' + cs.backgroundColor + ' b=' + cs.borderTopWidth + cs.borderLeftWidth); if (out.length > 8) break; } return out; })(),
    calls: window.__wf ? window.__wf.calls : [],
    docWidth: document.documentElement.scrollWidth, viewportWidth: window.innerWidth,
  };
})()`;

const results = [];
const click = (page, sel) => page.evaluate((s) => {
  const el = document.querySelector(s);
  if (!el) return false;
  el.scrollIntoView({ block: 'center' }); el.click(); return true;
}, sel);
const clickText = (page, text, within = 'body') => page.evaluate((t, w) => {
  const root = document.querySelector(w) || document.body;
  const el = [...root.querySelectorAll('button, [role="menuitem"]')].find((b) => b.textContent.replace(/\s+/g, ' ').trim() === t);
  if (!el) return false;
  el.scrollIntoView({ block: 'center' }); el.click(); return true;
}, text, within);

async function scene(name, { opts = {}, tab = 'workflows', free = false, steps = [], expect = null, width = 1440, height = 1000, screenRoot = null, screen = true,
  prelude = '', capture = null } = {}) {
  if (only && !only.includes(name)) return;
  if (process.env.WIDTH) width = Number(process.env.WIDTH);   // 1.5 visuals: the same scene at another width
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width, height, deviceScaleFactor: 1 });
  await page.emulateTimezone('UTC');
  await page.evaluateOnNewDocument(seed(opts) + prelude);
  const query = `conn=1&live=1${free ? '' : '&tier=pro'}&tab=${tab}`;
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${query}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await wait(1500);
  const notes = [];
  for (const step of steps) {
    let ok = true;
    if (step.sel) ok = await click(page, step.sel);
    else if (step.text) ok = await clickText(page, step.text, step.within);
    else if (step.type) {
      ok = !!(await page.$(step.type));
      if (ok) { await page.focus(step.type); await page.keyboard.type(step.value, { delay: 5 }); }
    } else if (step.select) {
      ok = !!(await page.$(step.select));
      if (ok) await page.select(step.select, step.value);
    } else if (step.key) await page.keyboard.press(step.key);
    else if (step.dispatch) await page.evaluate((d) => window.dispatchEvent(new MessageEvent('message', { data: d })), step.dispatch);
    else if (step.evaluate) await page.evaluate(step.evaluate);   // verify (round 2): a change the page does not make itself
    if (!ok) notes.push(`step not found: ${JSON.stringify(step)}`);
    await wait(step.settle ?? 500);
  }
  if (capture) await capture(page);
  // The One Visual Language screen check (gap, margin, padding, grid, nesting, contrast in three themes), scored
  // against tools/uishot/visual-baseline.json like every other driver's states.
  // A drawer is scored as its own page (its root), the way the Pro page and the Connections hub are; the shared
  // report dialog (artifactdialog.tsx) and the shared menu popover (toolrow.tsx RowMenu), open on purpose in two
  // scenes, are other lanes' components and are not scored here.
  if (screen) await checkScreen(page, `workflows-drive:${name}`, { root: screenRoot });
  await page.setViewport({ width, height, deviceScaleFactor: 1 });
  await wait(300);
  const measured = await page.evaluate(MEASURE);
  const shots = {};
  for (const theme of Object.keys(THEMES)) {
    if (process.env.THEME_ONLY && theme !== process.env.THEME_ONLY) continue;
    await applyTheme(page, theme); await wait(250);
    const file = join(OUT, `${process.env.SHOT_TAG ? process.env.SHOT_TAG + '-' : ''}${name}-${theme}.png`);
    await page.screenshot({ path: file, fullPage: false });
    shots[theme] = file;
  }
  if (expect) for (const failure of expect(measured)) notes.push(failure);
  if (measured.docWidth > measured.viewportWidth + 1) notes.push(`the page scrolls sideways: ${measured.docWidth} > ${measured.viewportWidth}`);
  results.push({ scene: name, measured, shots, notes, errors });
  console.log(`${notes.length || errors.length ? '!' : 'ok'} ${name}${notes.length ? ': ' + notes.join(' | ') : ''}${errors.length ? ' ERR ' + errors.slice(0, 2).join(' | ') : ''}`);
  await page.close();
}

const has = (cond, message) => (cond ? [] : [message]);
const callsOf = (m, name) => m.calls.filter((c) => c.call === name);
const row = (m, name) => m.rows.find((r) => r.name === name);

// ---- the list ---------------------------------------------------------------------------------------------
await scene('list', {
  opts: { rules: true },
  expect: (m) => [
    ...has(m.view === 'list', `view ${m.view}`),
    ...has(JSON.stringify(m.rows.map((r) => r.name)) === JSON.stringify(['test-a-complex-measure', 'import-returns', 'publish-to-test', 'old-hygiene']), `rows ${m.rows.map((r) => r.name)}`),
    ...has(row(m, 'test-a-complex-measure')?.required === 'Required for new measures', `required mark "${row(m, 'test-a-complex-measure')?.required}"`),
    ...has(JSON.stringify(row(m, 'test-a-complex-measure')?.marks) === '["you","assistant","semanticus","semanticus"]', `marks ${row(m, 'test-a-complex-measure')?.marks}`),
    ...has(row(m, 'test-a-complex-measure')?.cut === true, 'the after-the-change line is drawn'),
    ...has(/From a run/.test(row(m, 'import-returns')?.sub ?? ''), `From a run mark "${row(m, 'import-returns')?.sub}"`),
    ...has(/Yesterday · passed/.test(row(m, 'import-returns')?.last ?? ''), `last run "${row(m, 'import-returns')?.last}"`),
    // UAT N4: a run whose report says Unknown is listed as Unknown, never as passed.
    ...has(/· Unknown/.test(row(m, 'test-a-complex-measure')?.last ?? '') && !/passed/.test(row(m, 'test-a-complex-measure')?.last ?? ''), `last run "${row(m, 'test-a-complex-measure')?.last}"`),
    ...has(m.retired.length === 0, `retired sections still shown: ${m.retired}`),
  ],
});
await scene('list-no-rules', {
  expect: (m) => [
    ...has(m.rows.every((r) => !r.required), 'a person with no rules sees no Required mark'),
    ...has(!/Required|required|Checks in runs|team rule/.test(m.rows.map((r) => r.sub + r.last + r.required).join(' ')), 'no policy words on the rows'),
  ],
});
await scene('empty', {
  opts: { empty: true },
  expect: (m) => [
    ...has(/Describe it in words/.test(m.newPanel), 'the New panel is the page'),
    ...has(JSON.stringify(m.starters.map((s) => s.name)) === JSON.stringify(['verified-measure', 'optimize-dax', 'deploy-to-production', 'governed-rename', 'secure-with-rls', 'month-end-close']), `starters ${m.starters.map((s) => s.name)}`),
    ...has(m.starters.every((s) => s.button === 'Use'), 'every starter says Use'),
  ],
});
await scene('new-required-starter', {
  opts: { rules: true }, steps: [{ sel: '[data-testid="wf-new"]' }],
  expect: (m) => has(m.starters.length === 6, `${m.starters.length} starters`),
});
// Kane's UAT on build ee57ba84 (25 Sep): "describe a workflow tries to add it to the plan to author and it becomes a
// mess". Describing is authoring: Author opens with the described steps, and nothing is drafted onto the Plan.
await scene('describe-authors', {
  steps: [{ sel: '[data-testid="wf-new"]' }, { type: '[data-testid="wf-describe"]', value: 'Every month, refresh Sales, run the saved checks and email finance.' }, { sel: '[data-testid="wf-draft-it"]', settle: 1800 }],
  expect: (m) => [
    ...has(callsOf(m, 'composeTask').length === 0, `a draft was made on the Plan: ${JSON.stringify(callsOf(m, 'composeTask'))}`),
    ...has(JSON.stringify(callsOf(m, 'describeWorkflowSteps').map((c) => c.params)) === '[["Every month, refresh Sales, run the saved checks and email finance."]]', `describe calls ${JSON.stringify(callsOf(m, 'describeWorkflowSteps'))}`),
    ...has(m.view === 'author', `landed on ${m.view}`),
    ...has(m.saved === 'Unsaved', `saved "${m.saved}"`),
    ...has(JSON.stringify(m.outline.map((o) => o.replace(/^\d+\s*/u, ''))) === '["Refresh Sales","Run the saved checks","Email finance"]', `outline ${JSON.stringify(m.outline)}`),
    ...has(/1 to fill in/.test(m.alert + ' ' + m.notice), `notice "${m.notice}"`),
  ],
});
await scene('describe-placeholder', {
  steps: [{ sel: '[data-testid="wf-new"]' }, { type: '[data-testid="wf-describe"]', value: 'Every month, refresh Sales, run the saved checks and email finance.' }, { sel: '[data-testid="wf-draft-it"]', settle: 1800 },
    { sel: '[data-testid^="wf-ol-email"]', settle: 900 }],
  expect: (m) => [
    ...has(m.view === 'author', `landed on ${m.view}`),
    ...has(/Say what this step does, then choose what it may use\./.test(m.stepForm), `the placeholder step "${m.stepForm.slice(0, 200)}"`),
    ...has(/Your assistant does 3 steps/.test(m.summary), `summary "${m.summary}"`),
    ...has(/^Every month, refresh Sales, run the saved checks and email\s*Unsaved/.test(m.author), `title "${m.author}"`),
  ],
});
// Kane's UAT on the Yoga (25 Sep): "when creating a workflow from text, Jev doesnt appear to choose MCP operations just
// give free text for agent steps". On Pro once Jev drafts (lane L5), each step arrives with the actions Jev chose and
// the engine proved, and what it works on.
await scene('describe-jev-ops', {
  steps: [{ sel: '[data-testid="wf-new"]' }, { type: '[data-testid="wf-describe"]', value: 'create a meaure and test its performance' }, { sel: '[data-testid="wf-draft-it"]', settle: 1800 }],
  expect: (m) => [
    ...has(m.view === 'author', `landed on ${m.view}`),
    ...has(JSON.stringify(m.outline.map((o) => o.replace(/^\d+\s*/u, ''))) === '["Create a meaure","Test its performance"]', `outline ${JSON.stringify(m.outline)}`),
    ...has(/It may use: Create measure\./.test(m.stepForm), `step 1 "${m.stepForm.slice(0, 300)}"`),
    ...has(/Works on Sales\./.test(m.stepForm), `step 1 target "${m.stepForm.slice(0, 300)}"`),
    ...has(!/Say what this step does/.test(m.stepForm), `step 1 is still a placeholder "${m.stepForm.slice(0, 200)}"`),
    ...has(/Jev chose what each step may use\./.test(m.alert + ' ' + m.notice), `notice "${m.notice}"`),
  ],
});
await scene('describe-jev-ops-step-2', {
  steps: [{ sel: '[data-testid="wf-new"]' }, { type: '[data-testid="wf-describe"]', value: 'create a meaure and test its performance' }, { sel: '[data-testid="wf-draft-it"]', settle: 1800 },
    { sel: '[data-testid^="wf-ol-test"]', settle: 900 }],
  expect: (m) => [
    ...has(/It may use: Benchmark dax, Profile dax\./.test(m.stepForm), `step 2 "${m.stepForm.slice(0, 300)}"`),
    ...has(!/Say what this step does/.test(m.stepForm), `step 2 is still a placeholder "${m.stepForm.slice(0, 200)}"`),
  ],
});
await scene('describe-jev-refresh-check', {
  steps: [{ sel: '[data-testid="wf-new"]' }, { type: '[data-testid="wf-describe"]', value: 'refresh the sales model and check its correct' }, { sel: '[data-testid="wf-draft-it"]', settle: 1800 }],
  expect: (m) => [
    ...has(/Jev chose what each step may use\./.test(m.alert + ' ' + m.notice) && !/cannot hand a step to Semanticus/.test(m.alert + ' ' + m.notice), `notice "${m.notice}"`),
    ...has(/It may use: Refresh partition\./.test(m.stepForm), `step 1 "${m.stepForm.slice(0, 300)}"`),
    ...has(/Works on Sales\./.test(m.stepForm), `step 1 target "${m.stepForm.slice(0, 300)}"`),
  ],
});
await scene('start-blank', {
  steps: [{ sel: '[data-testid="wf-new"]' }, { sel: '[data-testid="wf-start-blank"]' }, { type: '[data-testid="wf-blank-title"]', value: 'Publish to the test workspace every Friday' }, { text: 'Start', settle: 1200 }],
  expect: (m) => [
    ...has(m.view === 'author', `view ${m.view}`),
    ...has(/Publish to the test workspace every Friday/.test(m.author), `author head "${m.author}"`),
    ...has(m.saved === 'Unsaved', `saved "${m.saved}"`),
  ],
});
await scene('use-template', { screenRoot: '[data-testid="wf-use-drawer"]',
  steps: [{ sel: '[data-testid="wf-new"]' }, { sel: '[data-testid="wf-use-month-end-close"]', settle: 800 }],
  expect: (m) => has(/Which period is this for\?/.test(m.useDrawer), `use drawer "${m.useDrawer}"`),
});
await scene('filter-off', {
  steps: [{ sel: '[data-testid="wf-filter-off"]' }],
  opts: {},
  expect: (m) => has(m.rows.length === 0, `${m.rows.length} rows are off`),
});

// ---- Author ---------------------------------------------------------------------------------------------
const openTcm = [{ sel: '[data-testid="wf-row-test-a-complex-measure"] .sem-wf-row-name', settle: 1200 }];
await scene('author', {
  opts: { rules: true }, steps: [...openTcm, { sel: '[data-testid="wf-ol-write"]' }],
  expect: (m) => [
    ...has(m.view === 'author', `view ${m.view}`),
    ...has(/Required for new measures/.test(m.author) && /Saved/.test(m.saved), `head "${m.author}"`),
    ...has(/You answer 5 questions and your assistant writes 1 part\. Then Semanticus makes the change only if every check passes\./.test(m.summary), `summary "${m.summary}"`),
    ...has(m.outline.length === 4, `outline ${m.outline}`),
    ...has(/You choose/.test(m.stepForm) && /Your assistant writes/.test(m.stepForm) && /Brief for your assistant/.test(m.stepForm) && /The brief is advice\. The checks are law\./.test(m.stepForm), `step form "${m.stepForm.slice(0, 200)}"`),
  ],
});
await scene('author-dirty-save', {
  opts: { rules: true }, steps: [...openTcm, { type: '.sem-wf-sf-title', value: ' now' }, { sel: '[data-testid="wf-save"]', settle: 1200 }],
  expect: (m) => [
    ...has(callsOf(m, 'editWorkflowDocument').length === 1, `one save call, got ${callsOf(m, 'editWorkflowDocument').length}`),
    ...has(!m.preview, 'no preview on a clean save'),
  ],
});
await scene('author-settings', { screenRoot: '[data-testid="wf-settings"]',
  opts: { rules: true }, steps: [...openTcm, { sel: '[data-testid="wf-settings-open"]', settle: 900 }],
  expect: (m) => ['Notes for your assistant', 'Inputs asked each run', 'Rules', 'Offered', 'Repeat on the Plan', 'Required for new measures']
    .flatMap((w) => has(m.settings.includes(w), `settings lacks "${w}"`)),
});
// Sol on slice 10 (F-285): Team rules opened from a workflow's Settings keeps the workflow's unsaved edits.
await scene('author-dirty-team-rules', { screenRoot: '[data-testid="wf-team-rules"]',
  opts: { rules: true }, steps: [...openTcm, { type: '.sem-wf-sf-title', value: ' now' }, { sel: '[data-testid="wf-settings-open"]', settle: 900 },
    { text: 'Team rules', within: '[data-testid="wf-settings"]', settle: 900 }],
  expect: (m) => [
    ...has(m.view === 'author', `view ${m.view}`),
    ...has(m.saved === 'Unsaved', `the edit is gone: saved "${m.saved}"`),
    ...has(/Team rules/.test(m.teamRules), 'the Team rules drawer is open'),
  ],
});
// Sol on slice 10 (F-287): Always on a workflow that is off, with a saved Only when rule, leaves it offered always.
await scene('author-offered-always', { screenRoot: '[data-testid="wf-settings"]',
  opts: { rules: true, offWithRule: true }, steps: [{ sel: '[data-testid="wf-filter-off"]', settle: 600 }, ...openTcm,
    { sel: '[data-testid="wf-settings-open"]', settle: 900 }, { text: 'Always', within: '[data-testid="wf-offered"]', settle: 1200 }],
  expect: (m) => [
    ...has(m.offered === 'always', `offered "${m.offered}" after Always`),
    ...has(callsOf(m, 'setWorkflowEnabled').length === 1 && callsOf(m, 'setWorkflowActivation').some((c) => !c.params[1]), `calls ${JSON.stringify(m.calls.map((c) => [c.call, c.params]))}`),
  ],
});
await scene('author-menu', { screen: false,
  opts: { rules: true }, steps: [...openTcm, { text: 'More▾', settle: 400 }],
  expect: () => [],
});
await scene('author-canvas', { opts: { rules: true }, steps: [...openTcm, { text: 'More▾' }, { sel: '[data-testid="wf-view-canvas"]', settle: 1500 }] });
// 1.5 visuals: the Plan's source column must hold "Found by Semanticus" (it read "Found by Sem..." at about 1490 px).
await scene('plan-source', {
  tab: 'plan', steps: [{ settle: 1500 }],
  capture: async (page) => {
    const found = await page.evaluate(() => [...document.querySelectorAll('.pw-src .pw-trunc')].map((el) => ({ text: el.textContent, clipped: el.scrollWidth > el.clientWidth })));
    console.log('plan sources', JSON.stringify(found));
  },
});
await scene('author-source', { opts: { rules: true }, steps: [...openTcm, { text: 'More▾' }, { sel: '[data-testid="wf-view-source"]', settle: 1200 }] });
await scene('author-check', {
  opts: { rules: true }, steps: [...openTcm, { text: 'More▾' }, { sel: '[data-testid="wf-check"]', settle: 800 }],
  expect: (m) => has(callsOf(m, 'checkWorkflow').length === 1, 'Check calls checkWorkflow'),
});
await scene('author-upgrade', {
  steps: [{ sel: '[data-testid="wf-row-old-hygiene"] .sem-wf-row-name', settle: 1200 }, { sel: '[data-testid="wf-upgrade-look"]', settle: 800 }],
  expect: (m) => [
    ...has(/This workflow uses an older format\./.test(m.upgrade), `upgrade "${m.upgrade}"`),
    ...has(callsOf(m, 'upgradeWorkflow').length === 1 && callsOf(m, 'upgradeWorkflow')[0].params[1] === true, 'the notice previews first'),
  ],
});
await scene('author-starter', {
  steps: [{ sel: '[data-testid="wf-new"]' }, { sel: '[data-testid="wf-starter-optimize-dax"] .sem-wf-card-title', settle: 1200 }],
  expect: (m) => has(/A starter\. Use it to make a copy you can change\./.test(m.author), `starter head "${m.author}"`),
});

// ---- Team rules ------------------------------------------------------------------------------------------
await scene('team-rules', { screenRoot: '[data-testid="wf-team-rules"]',
  opts: { rules: true }, steps: [{ sel: '[data-testid="wf-team-rules-link"]', settle: 700 }],
  expect: (m) => [
    ...has(/New measures go through Test a complex measure/.test(m.teamRules), `rules "${m.teamRules.slice(0, 160)}"`),
    ...has(/Checks in runs: on/.test(m.checks), `checks "${m.checks}"`),
  ],
});
await scene('team-rules-off', { screenRoot: '[data-testid="wf-team-rules"]',
  opts: { rules: true, checksOff: true }, steps: [{ sel: '[data-testid="wf-team-rules-link"]', settle: 700 }],
  expect: (m) => [
    ...has(/Checks inside runs are off\. Actions a team rule requires still go through their workflow\./.test(m.checks), `checks "${m.checks}"`),
    ...has(!/not checked against required workflows/i.test(m.teamRules), 'the false copy is gone'),
  ],
});
await scene('team-rules-none', { screenRoot: '[data-testid="wf-team-rules"]',
  steps: [{ sel: '[data-testid="wf-team-rules-link"]', settle: 700 }],
  expect: (m) => [
    ...has(/None\. Nothing has to go through a workflow\./.test(m.teamRules), 'no rule, said plainly'),
    ...has(!m.offLine, 'the off line only when a rule exists'),
  ],
});

// ---- UAT 25 Sep (lane uat-fix-5): the page tells the truth when the engine fails, and keeps unsaved work ------
// UAT-S02-j3-004: an unreadable library said "Reading your workflows" for ever and "No workflow matches that search".
await scene('list-unreadable', {
  opts: { listFails: true },
  expect: (m) => [
    ...has(!/Reading your workflows/.test(m.count), `still reading: "${m.count}"`),
    ...has(/could not be read/.test(m.count), `count line "${m.count}"`),
    ...has(!/No workflow matches that search/.test(m.list), `a search that nobody typed: "${m.list}"`),
  ],
});
// UAT-S02-2-005: Team rules said "None" when it could not read the rules at all.
await scene('team-rules-unreadable', { screenRoot: '[data-testid="wf-team-rules"]',
  opts: { rules: true, policyFails: true }, steps: [{ sel: '[data-testid="wf-team-rules-link"]', settle: 700 }],
  expect: (m) => [
    ...has(!/None\. Nothing has to go through a workflow\./.test(m.teamRules), 'Team rules says None while the rules could not be read'),
    ...has(/could not be read/.test(m.teamRules), `rules "${m.teamRules.slice(0, 200)}"`),
  ],
});
// UAT-S02-2-004 / j7-004: the switch said on after the engine had saved off and then reported an error.
await scene('team-rules-switch-error', { screenRoot: '[data-testid="wf-team-rules"]',
  opts: { rules: true, enforcementFailsAfterWrite: true },
  steps: [{ sel: '[data-testid="wf-team-rules-link"]', settle: 700 }, { sel: '[data-testid="wf-checks-toggle"]', settle: 900 }],
  expect: (m) => [
    ...has(callsOf(m, 'setWorkflowEnforcement').length === 1, `calls ${JSON.stringify(callsOf(m, 'setWorkflowEnforcement'))}`),
    ...has(/Checks in runs: off/.test(m.checks), `the switch does not show what the engine saved: "${m.checks}"`),
    // UAT-S02-2-006: the same warning was shown twice, over the list and again in the drawer. Once, in the drawer.
    ...has(m.pinWarnings === 1, `the error is shown ${m.pinWarnings} times`),
  ],
});
// UAT-S02-j7-003 and UAT-S11-1-003: leaving Author with unsaved work lost it, by the Workflows link and by the Plan.
const blankWithSource = [{ sel: '[data-testid="wf-new"]' }, { sel: '[data-testid="wf-start-blank"]' },
  { type: '[data-testid="wf-blank-title"]', value: 'Month end check' }, { text: 'Start', settle: 1200 },
  { text: 'More▾' }, { sel: '[data-testid="wf-view-source"]', settle: 1200 },
  { type: '[data-testid="wf-author"] textarea', value: 'KEEP-THIS-LINE' }];
await scene('author-leave-kept', {
  steps: [...blankWithSource, { sel: '[data-testid="wf-back"]', settle: 900 }],
  expect: (m) => [
    ...has(m.view === 'list', `view ${m.view}`),
    // The harness's own previewWorkflowEdit answers a new file's title as its file name, so Author (and the line
    // naming it) reads month-end-check after Source opens; the real engine keeps the title typed at Start blank.
    ...has(/(Month end check|month-end-check) are kept here/.test(m.held) && /Open it/.test(m.held) && /Throw it away/.test(m.held), `the list does not say the work is kept: "${m.held}"`),
  ],
});
await scene('author-leave-reopen', {
  steps: [...blankWithSource, { sel: '[data-testid="wf-back"]', settle: 900 }, { text: 'Open it', within: '[data-testid="wf-held"]', settle: 1400 }],
  expect: (m) => [
    ...has(m.view === 'author', `view ${m.view}`),
    ...has(m.saved === 'Unsaved', `saved "${m.saved}"`),
    ...has((m.source ?? '').includes('KEEP-THIS-LINE'), `the source came back without the edit: ${JSON.stringify((m.source ?? '').slice(-120))}`),
  ],
});
await scene('author-leave-by-plan', {
  opts: { rules: true },
  steps: [...openTcm, { type: '.sem-wf-sf-title', value: ' kept' }, { dispatch: { type: 'navigate', tab: 'plan' }, settle: 1200 },
    { dispatch: { type: 'navigate', tab: 'workflows' }, settle: 1200 }, { text: 'Open it', within: '[data-testid="wf-held"]', settle: 1500 }],
  expect: (m) => [
    ...has(m.view === 'author', `view ${m.view}`),
    ...has(m.saved === 'Unsaved' && /Define the measure kept/.test(m.outline.join(' ')), `the edit did not come back: saved "${m.saved}", outline ${JSON.stringify(m.outline)}`),
  ],
});
await scene('author-leave-untouched', {
  steps: [{ sel: '[data-testid="wf-new"]' }, { sel: '[data-testid="wf-start-blank"]' }, { type: '[data-testid="wf-blank-title"]', value: 'Nothing typed' },
    { text: 'Start', settle: 1200 }, { sel: '[data-testid="wf-back"]', settle: 900 }],
  expect: (m) => has(!m.held, `an untouched blank is kept as work: "${m.held}"`),
});

// Sol on m11/integration 568435d3, finding 1: a workflow made by Describe it in words was never kept, because every
// step that built it ran while keeping was quiet and nothing kept it once quiet ended. Opened and left untouched, it
// is the person's words, so leaving Author keeps it; an untouched blank still is not (author-leave-untouched).
const describeThenBack = [{ sel: '[data-testid="wf-new"]' }, { type: '[data-testid="wf-describe"]', value: 'Every month, refresh Sales, run the saved checks and email finance.' },
  { sel: '[data-testid="wf-draft-it"]', settle: 1800 }, { sel: '[data-testid="wf-back"]', settle: 900 }];
await scene('describe-leave-kept', {
  steps: describeThenBack,
  expect: (m) => [
    ...has(m.view === 'list', `view ${m.view}`),
    ...has(/kept here/.test(m.held) && /Open it/.test(m.held) && /Throw it away/.test(m.held), `the described workflow was not kept: "${m.held}"`),
  ],
});
await scene('describe-leave-reopen', {
  steps: [...describeThenBack, { text: 'Open it', within: '[data-testid="wf-held"]', settle: 1400 }],
  expect: (m) => [
    ...has(m.view === 'author', `view ${m.view}`),
    ...has(m.saved === 'Unsaved', `saved "${m.saved}"`),
    ...has(/Refresh Sales/.test(m.outline.join(' ')) && /Email finance/.test(m.outline.join(' ')), `the described steps did not come back: ${JSON.stringify(m.outline)}`),
    // The coordinator, 27 Sep (my own also-found): a kept new workflow reopened with its steps said "No steps yet" beside them.
    ...has(!m.emptySteps, 'the right pane says "No steps yet" beside the steps in the outline'),
  ],
});

// verify (m11/sol-fixes-1 at c4793fbd): finding 1's fix keeps whatever Author just prepared, once, as it opens. A new
// workflow is kept under its file name, which comes from its title and is free only among SAVED workflows, so a second
// new workflow with the same title lands on the same key: an untouched blank now DROPS the unsaved work kept there, and
// a described one OVERWRITES it, the moment it opens and without a change. Before the fix neither touched the kept work
// until the person changed something. Both scenes pass on 46193fcf and fail on c4793fbd.
await scene('verify-second-blank-same-title-keeps-the-first', {
  steps: [...blankWithSource, { sel: '[data-testid="wf-back"]', settle: 900 },
    { sel: '[data-testid="wf-new"]' }, { sel: '[data-testid="wf-start-blank"]' }, { type: '[data-testid="wf-blank-title"]', value: 'Month end check' },
    { text: 'Start', settle: 1200 }, { sel: '[data-testid="wf-back"]', settle: 900 },
    { text: 'Open it', within: '[data-testid="wf-held"]', settle: 1400 }],
  expect: (m) => [
    ...has(m.view === 'author', `the kept work is gone from the list: view ${m.view}, held "${m.held}"`),
    ...has((m.source ?? '').includes('KEEP-THIS-LINE'), `the first blank's unsaved edit was lost: ${JSON.stringify((m.source ?? '').slice(-120))}`),
  ],
});
await scene('verify-second-description-same-first-sentence-keeps-the-first', {
  steps: [{ sel: '[data-testid="wf-new"]' }, { type: '[data-testid="wf-describe"]', value: 'Every month, refresh Sales, run the saved checks and email finance.' },
    { sel: '[data-testid="wf-draft-it"]', settle: 1800 }, { type: '.sem-wf-sf-title', value: ' kept' }, { sel: '[data-testid="wf-back"]', settle: 900 },
    { sel: '[data-testid="wf-new"]' }, { type: '[data-testid="wf-describe"]', value: 'Every month, refresh Sales, run the saved checks and email finance. Then publish it.' },
    { sel: '[data-testid="wf-draft-it"]', settle: 1800 }, { sel: '[data-testid="wf-back"]', settle: 900 },
    { text: 'Open it', within: '[data-testid="wf-held"]', settle: 1400 }],
  expect: (m) => [
    ...has(m.view === 'author', `view ${m.view}, held "${m.held}"`),
    ...has(/kept/.test(m.outline.join(' ')), `the first description's unsaved edit was overwritten: ${JSON.stringify(m.outline)}`),
  ],
});

// verify (m11/sol-fixes-1 round 2, 4d81c149): unsaved work kept for a SAVED workflow is kept under its name alone
// (heldKey(name, false)), not under its model. Open another model that has a workflow of the same name (Save As into
// another folder carries every workflow along) and change it: the change is kept under the same key, over the work kept
// for the first model's file. Back in the first model, the kept work is gone. Here the model swap is the one thing the
// page cannot do itself: the documents the engine sends start to name another model's folder, then the first again.
// OLDER THAN THIS LANE: the scene fails on 30f7e62b (c4793fbd plus round 1's tests) as well, so it is not charged to
// round 2's fixes; it is here because the brief names a model swap among the ways kept work must never be lost.
const swapModel = (to) => ({ evaluate: `(() => {
  window.__vsfModelDir = ${JSON.stringify(to)};
  if (window.__vsfSwapHook) return;
  window.__vsfSwapHook = true;
  const rewrite = (o) => { if (o && typeof o === 'object') { for (const k of Object.keys(o)) {
    if (typeof o[k] === 'string' && o[k].startsWith('/project/') && window.__vsfModelDir) o[k] = window.__vsfModelDir + o[k].slice('/project/'.length);
    else if (o[k] && typeof o[k] === 'object') rewrite(o[k]); } } return o; };
  window.addEventListener('message', (e) => {
    const d = e.data; if (!d || d.type !== 'rpcResult' || !d.result) return;
    const asked = (window.__uishotHarness.messages || []).find((m) => m && m.id === d.id);
    if (asked && (asked.method === 'getWorkflowDocument' || asked.method === 'previewWorkflowEdit')) rewrite(d.result);
  }, true);
})()`, settle: 300 });
await scene('verify-r2-kept-edit-survives-a-same-named-workflow-in-another-model', {
  opts: { rules: true },
  steps: [...openTcm, { type: '.sem-wf-sf-title', value: ' kept' }, { sel: '[data-testid="wf-back"]', settle: 900 },
    swapModel('/other-model/'), ...openTcm, { type: '.sem-wf-sf-title', value: ' other' }, { sel: '[data-testid="wf-back"]', settle: 900 },
    swapModel(null), { text: 'Open it', within: '[data-testid="wf-held"]', settle: 1500 }],
  expect: (m) => [
    ...has(m.view === 'author', `view ${m.view}, held "${m.held}"`),
    ...has(/Define the measure kept/.test(m.outline.join(' ')) && m.saved === 'Unsaved',
      `the work kept for this model's workflow is gone (the other model's edit was kept over it): outline ${JSON.stringify(m.outline)}, saved "${m.saved}", notice "${m.notice}"`),
  ],
});

// m11/sol-fixes-1 round 3, the older kept-work losses the verifier's round 2 names in the harm area.
// (1) The assistant saves a workflow under the file name a kept new workflow was going to use: every Save was refused
// and the only other way out, Reload saved file, dropped the work. Now the refusal offers the next free name.
const assistantTakesTheName = { evaluate: `window.__wf.handlers.saveWorkflow(['month-end-check', '---\\nname: month-end-check\\ntitle: Month end check\\n---\\n', 'agent', true])`, settle: 600 };
const keptThenTaken = [...blankWithSource, { sel: '[data-testid="wf-back"]', settle: 900 }, assistantTakesTheName,
  { text: 'Open it', within: '[data-testid="wf-held"]', settle: 1400 }, { sel: '[data-testid="wf-save"]', settle: 1500 }];
await scene('author-name-taken-offers-its-own-name', {
  steps: keptThenTaken,
  expect: (m) => [
    ...has(/Save as month-end-check-2/.test(m.preview) && /A workflow named month-end-check was saved while your changes were kept/.test(m.preview),
      `the refusal offers no name of its own: "${m.preview}"`),
    ...has(!/Reload saved file/.test(m.preview), `the only other way out still drops the work: "${m.preview}"`),
    ...has((m.source ?? '').includes('KEEP-THIS-LINE') || m.saved === 'Unsaved', `the kept work is gone: saved "${m.saved}"`),
  ],
});
await scene('author-name-taken-saves-under-its-own-name', {
  steps: [...keptThenTaken, { sel: '[data-testid="wf-save-as-free"]', settle: 1800 }],
  expect: (m) => {
    const saves = callsOf(m, 'saveWorkflow').map((c) => c.params ?? c.p ?? c.args ?? c);
    const mine = saves.find((p) => JSON.stringify(p).includes('month-end-check-2'));
    return [
      ...has(!!mine && JSON.stringify(mine).includes('KEEP-THIS-LINE') && JSON.stringify(mine).includes('name: month-end-check-2'),
        `the work was not saved under its own name: ${JSON.stringify(saves).slice(0, 400)}`),
      ...has(!m.held, `it is still listed as kept: "${m.held}"`),
      ...has(/Saved as month-end-check-2, because a workflow named month-end-check was saved while your changes were kept\./.test(m.notice),
        `the person is not told the name it was saved under: "${m.notice}"`),
    ];
  },
});

// (2) Closing the Studio tab dropped the webview's own state and every kept workflow with it. The page now hands each
// change to the extension, which gives it to the next Studio page it opens: here, a second page that starts with only
// what the first one handed over, the way a Studio tab opened again does.
let handedToTheExtension = null;
await scene('author-close-studio-kept', {
  steps: [...blankWithSource, { sel: '[data-testid="wf-back"]', settle: 900 }],
  capture: async (page) => { handedToTheExtension = await page.evaluate(() => {
    const sent = (window.__uishotHarness.messages || []).filter((x) => x && x.type === 'workflowHeld');
    return sent.length ? sent[sent.length - 1].value : null;
  }); },
  expect: () => has(!!handedToTheExtension && Object.keys(handedToTheExtension).some((k) => k.startsWith('new:month-end-check')),
    `the kept work was not handed to the extension: ${JSON.stringify(handedToTheExtension)?.slice(0, 200)}`),
});
await scene('author-close-studio-reopen', {
  prelude: `;window.__semanticusHeldWorkflows = ${JSON.stringify(handedToTheExtension)};`,
  steps: [{ text: 'Open it', within: '[data-testid="wf-held"]', settle: 1400 }],
  expect: (m) => [
    ...has(m.view === 'author', `the reopened Studio does not list the kept work: view ${m.view}, held "${m.held}"`),
    ...has((m.source ?? '').includes('KEEP-THIS-LINE'), `the kept edit did not come back: ${JSON.stringify((m.source ?? '').slice(-120))}`),
  ],
});

// m11/sol-fixes-1 round 4, the kept-work items the verifier's round 3 names. (1) Two same-named kept entries, one per
// model, read the same, and the verifier threw away the wrong one: each line now names the folder of its model.
const keptInTwoModels = [...openTcm, { type: '.sem-wf-sf-title', value: ' kept' }, { sel: '[data-testid="wf-back"]', settle: 900 },
  swapModel('/other-model/'), ...openTcm, { type: '.sem-wf-sf-title', value: ' other' }, { sel: '[data-testid="wf-back"]', settle: 900 },
  swapModel(null)];
await scene('author-kept-two-models-told-apart', {
  opts: { rules: true },
  steps: keptInTwoModels,
  expect: (m) => [
    ...has(/Unsaved changes to .+ in the model in project are kept here\./.test(m.held) && /Unsaved changes to .+ in the model in other-model are kept here\./.test(m.held),
      `the two kept lines are not told apart by their model: "${m.held}"`),
  ],
});
// (2) "Open it" on the other model's line opened this model's own kept work as if it were the one asked for. It still
// opens this model's own (the other model's stays kept), and now says so.
await scene('author-kept-other-model-says-which-opened', {
  opts: { rules: true },
  steps: [...keptInTwoModels, { evaluate: `(() => { const b = [...document.querySelectorAll('[data-testid="wf-held"] button')].filter((x) => x.textContent.trim() === 'Open it'); b[b.length - 1].click(); })()`, settle: 1500 }],
  expect: (m) => [
    ...has(m.view === 'author' && /Define the measure kept/.test(m.outline.join(' ')), `this model's own kept work did not open: view ${m.view}, outline ${JSON.stringify(m.outline)}`),
    ...has(/were made to .+ in another model\. They are still kept\./.test(m.notice) && /These are the unsaved changes kept for this model's own/.test(m.notice),
      `the page does not say the work asked for is another model's: "${m.notice}"`),
  ],
});
// (3) Kept work for a workflow the assistant deleted waited at "Opening the workflow..." for good. It opens as a new
// workflow with the work, so it can be saved again.
await scene('author-kept-for-a-deleted-workflow-opens-as-new', {
  opts: { rules: true },
  steps: [...openTcm, { type: '.sem-wf-sf-title', value: ' kept' }, { sel: '[data-testid="wf-back"]', settle: 900 },
    { evaluate: `(() => { window.__wf.handlers.deleteWorkflow(['test-a-complex-measure', 'agent']);
      window.dispatchEvent(new MessageEvent('message', { data: { type: 'workflowLibraryDidChange', payload: window.__wf.LIST.slice() } })); })()`, settle: 900 },
    { text: 'Open it', within: '[data-testid="wf-held"]', settle: 1800 }],
  expect: (m) => [
    ...has(!/Opening the workflow/.test(m.author + ' ' + m.alert) && m.view === 'author', `it still waits for the deleted file: view ${m.view}`),
    ...has(/Define the measure kept/.test(m.outline.join(' ')), `the kept edit did not come back: outline ${JSON.stringify(m.outline)}`),
    ...has(/There is no workflow named .+ in this model now, so your kept changes open here as a new workflow\./.test(m.notice), `nothing says why it opens as new: "${m.notice}"`),
  ],
});
await scene('author-kept-for-a-deleted-workflow-saves-again', {
  opts: { rules: true },
  steps: [...openTcm, { type: '.sem-wf-sf-title', value: ' kept' }, { sel: '[data-testid="wf-back"]', settle: 900 },
    // The fixture's delete takes the row off the list; the file goes too (the harness reads its definitions for a save).
    { evaluate: `(() => { window.__wf.handlers.deleteWorkflow(['test-a-complex-measure', 'agent']); delete window.__wf.DEFS['test-a-complex-measure'];
      window.dispatchEvent(new MessageEvent('message', { data: { type: 'workflowLibraryDidChange', payload: window.__wf.LIST.slice() } })); })()`, settle: 900 },
    { text: 'Open it', within: '[data-testid="wf-held"]', settle: 1800 }, { sel: '[data-testid="wf-save"]', settle: 1800 }],
  expect: (m) => {
    // Saved as a new file (createOnly) of its own name, with the kept edit: the harness writes an edit as a marker line.
    const saves = callsOf(m, 'saveWorkflow').map((c) => c.params ?? []);
    return [
      ...has(saves.some((p) => p[0] === 'test-a-complex-measure' && p[3] === true && String(p[1]).includes('workflow editor preview: set_field')),
        `the kept work was not saved again: ${JSON.stringify(saves).slice(0, 400)}; preview "${m.preview}", notice "${m.notice}", alert "${m.alert}"`),
      ...has(!m.held, `it is still listed as kept: "${m.held}"`),
    ];
  },
});
// (4) Author sent "What it does" one edit per key, and a value on the way ending in a space was refused by the engine
// with the whole save (VR7). Typed through spaces, it is now ONE edit from what the field held to what it holds.
let typedEdits = null;
await scene('author-step-text-typed-is-one-edit', {
  opts: { rules: true },
  steps: [...openTcm, { type: '.sem-wf-what textarea', value: ' Then check it.' }, { sel: '[data-testid="wf-save"]', settle: 1500 }],
  capture: async (page) => { typedEdits = await page.evaluate(() => {
    const sent = (window.__uishotHarness.messages || []).filter((x) => x && x.method === 'previewWorkflowEdit');
    return sent.length ? JSON.parse(sent[sent.length - 1].params?.[3] ?? '[]') : null;
  }); },
  expect: () => {
    const text = (typedEdits ?? []).filter((e) => e.op === 'set_field' && e.field === 'instructions');
    return [
      // The browser types where the box puts the caret (its start), so each key's value on the way begins with a space.
      ...has(text.length === 1 && text[0].value.startsWith(' Then check it.') && text[0].expect === 'Name the measure and say which numbers you already trust.',
        `the step text was not sent as one edit: ${JSON.stringify(typedEdits).slice(0, 400)}`),
    ];
  },
});

// ---- free ------------------------------------------------------------------------------------------------
await scene('free', { free: true, expect: (m) => has(/Workflows is part of Semanticus Pro/.test(m.proPreview) || /Pro/.test(m.proPreview), 'the Pro line') });

// ---- runs and drafts on the Plan (R3, F-275) -------------------------------------------------------------
await scene('run-row', {
  steps: [{ sel: '[data-testid="wf-new"]' }, { sel: '[data-testid="wf-row-publish-to-test"] button.sem-btn', settle: 300 }],
  expect: () => [],
});
await scene('draft-row', {
  steps: [{ sel: '[data-testid="wf-run-test-a-complex-measure"]', settle: 1600 }],
  expect: (m) => [
    ...has(m.view === 'plan', `landed on ${m.view}`),
    ...has(callsOf(m, 'composeTask').length === 1 && callsOf(m, 'composeTask')[0].params[1] === 'test-a-complex-measure', `compose ${JSON.stringify(callsOf(m, 'composeTask'))}`),
    ...has(/You choose/.test(m.composer) && /What should the measure be called\?/.test(m.composer), `draft "${m.composer.slice(0, 160)}"`),
  ],
});

await scene('draft-in-author', {
  steps: [{ sel: '[data-testid="wf-run-test-a-complex-measure"]', settle: 1600 }, { sel: '[data-testid="cd-edit"]', settle: 1500 }],
  expect: (m) => [
    ...has(m.view === 'draft', `view ${m.view}`),
    ...has(/A draft, not saved as a workflow/.test(m.draftAuthor) && /Drafted from one of your workflows/.test(m.draftAuthor), `author draft "${m.draftAuthor.slice(0, 200)}"`),
    ...has(/What should the measure be called\?/.test(m.draftAuthor) && /Run with your assistant/.test(m.draftAuthor), 'the draft body is drawn in Author'),
  ],
});
await scene('draft-reusable', {
  steps: [{ sel: '[data-testid="wf-run-test-a-complex-measure"]', settle: 1600 }, { sel: '[data-testid="cd-edit"]', settle: 1500 }, { sel: '[data-testid="wf-draft-reusable"]', settle: 1500 }],
  expect: (m) => [
    ...has(callsOf(m, 'saveWorkflow').length === 1 && /derived_from: dr-0a1b2c3d/.test(callsOf(m, 'saveWorkflow')[0].params[1]), `save ${JSON.stringify(callsOf(m, 'saveWorkflow').map((c) => c.params[0]))}`),
    ...has(m.view === 'author', `view ${m.view}`),
  ],
});

// ---- Play the flow: Use a starter, run it, finish it on its Plan row, report and Make it reusable --------
await scene('flow-1-use', {
  opts: { empty: true }, steps: [{ sel: '[data-testid="wf-use-optimize-dax"]', settle: 1400 }],
  expect: (m) => [
    ...has(m.view === 'author', `view ${m.view}`),
    ...has(JSON.stringify(callsOf(m, 'instantiateWorkflowTemplate').map((c) => c.params.slice(0, 2))) === '[["optimize-dax",null]]', `use ${JSON.stringify(callsOf(m, 'instantiateWorkflowTemplate'))}`),
  ],
});
const flowRun = [{ sel: '[data-testid="wf-use-optimize-dax"]', settle: 1400 }, { sel: '[data-testid="wf-author-run"]', settle: 1600 }];
await scene('flow-2-run', {
  opts: { empty: true }, tab: 'workflows', steps: flowRun,
  expect: (m) => [
    ...has(m.view === 'plan', `landed on ${m.view}`),
    ...has(callsOf(m, 'startWorkflow').length === 1, 'one start'),
    ...has(/Step 1 of 4 steps/.test(m.runDetail) && /Submit step/.test(m.runDetail), `run detail "${m.runDetail.slice(0, 200)}"`),
  ],
});
const answer = { type: '.sem-wf-run input, .sem-wf-run textarea', value: 'Sales YTD' };
// ---- pre-handover journey polish (lane polish-b): F12, F13, F14 ----------------------------------------------
await scene('run-assistant-fills', {
  opts: { empty: true }, steps: flowRun,
  expect: (m) => [
    ...has(!/Machine field/.test(m.runDetail), `the person is asked for machine input: "${m.runDetail.slice(0, 240)}"`),
    ...has(/Your assistant fills these in/.test(m.assistantFills) && /Equivalence grid/.test(m.assistantFills), `assistant fills "${m.assistantFills}"`),
  ],
});
await scene('run-skip-box', {
  opts: { empty: true }, steps: [...flowRun, { text: 'Skip with reason', settle: 400 }],
  expect: (m) => has(m.reasonBox === 'Why skip this step?', `skip box placeholder "${m.reasonBox}"`),
});
await scene('run-tally', {
  opts: { empty: true }, steps: [...flowRun, answer, { text: 'Submit step', settle: 900 }, { text: 'Skip with reason', settle: 300 },
    { type: '.sem-wf-run input:not([disabled])', value: 'No benchmark server today' }, { text: 'Skip with reason', settle: 900 }],
  expect: (m) => has(/1 passed · 1 skipped · 2 to go/.test(m.runTally), `run tally "${m.runTally}"`),
});
await scene('flow-3-steps', {
  opts: { empty: true }, steps: [...flowRun, answer, { text: 'Submit step', settle: 900 }, { text: 'Skip with reason', settle: 300 },
    { type: '.sem-wf-run input:not([disabled])', value: 'No benchmark server today' }, { text: 'Skip with reason', settle: 900 }],
  expect: (m) => [
    ...has(callsOf(m, 'submitWorkflowStep').length === 1, `submits ${callsOf(m, 'submitWorkflowStep').length}`),
    ...has(callsOf(m, 'skipWorkflowStep').length <= 1, 'at most one skip'),
  ],
});
await scene('flow-4-history', {
  opts: { empty: true }, steps: [...flowRun, answer, { text: 'Submit step', settle: 900 }, { text: 'Submit step', settle: 900 }, { text: 'Submit step', settle: 900 }, { text: 'Submit step', settle: 1200 },
    { sel: '[data-testid="plan-view-hist"]', settle: 1200 }],
  expect: (m) => has(callsOf(m, 'submitWorkflowStep').length === 4, `submits ${callsOf(m, 'submitWorkflowStep').length}`),
});
const finish = [...flowRun, answer, { text: 'Submit step', settle: 900 }, { text: 'Submit step', settle: 900 }, { text: 'Submit step', settle: 900 }, { text: 'Submit step', settle: 1200 },
  { sel: '[data-testid="plan-view-hist"]', settle: 1200 }, { sel: '[data-hid="h-wfr-7"]', settle: 800 }];
await scene('flow-5-report', { screen: false,
  opts: { empty: true }, steps: [...finish, { sel: '[data-testid="ph-run-report"]', settle: 1200 }],
  expect: (m) => has(callsOf(m, 'exportWorkflowEvidence').length === 1 && callsOf(m, 'exportWorkflowEvidence')[0].params[0] === 'wfr-7', `report ${JSON.stringify(callsOf(m, 'exportWorkflowEvidence'))}`),
});
await scene('flow-6-reusable', {
  opts: { empty: true }, steps: [...finish, { sel: '[data-testid="ph-make-reusable"]', settle: 1200 }],
  expect: (m) => [
    ...has(callsOf(m, 'saveWorkflow').length === 1 && callsOf(m, 'saveWorkflow')[0].params[0] === 'optimize-dax-from-a-run', `save ${JSON.stringify(callsOf(m, 'saveWorkflow').map((c) => c.params[0]))}`),
    ...has(/derived_from: wfr-7/.test(callsOf(m, 'saveWorkflow')[0]?.params[1] ?? ''), 'the copy carries the run it came from'),
    ...has(/Saved as Fix a slow measure, from a run in your workflows\./.test(m.reusable), `done "${m.reusable}"`),
  ],
});
await scene('flow-7-list', {
  opts: { empty: true }, steps: [...finish, { sel: '[data-testid="ph-make-reusable"]', settle: 1200 }, { text: 'Open Workflows', settle: 1500 }],
  expect: (m) => [
    ...has(m.view === 'list', `view ${m.view}`),
    ...has(m.rows.some((r) => r.name === 'optimize-dax-from-a-run' && /From a run/.test(r.sub)), `rows ${JSON.stringify(m.rows.map((r) => [r.name, r.sub]))}`),
    ...has(m.retired.length === 0, `retired sections still shown: ${m.retired}`),
  ],
});

await browser.close();
server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));
const bad = results.filter((r) => r.errors.length > 0 || r.notes.length > 0);
console.log(`\n${results.length} scenes, ${results.length - bad.length} clean.`);
for (const r of bad) console.log(`  ! ${r.scene}: ${[...r.notes, ...r.errors].join(' | ')}`);
const screenBad = screenFailures();
for (const f of screenBad) console.log(`  ! screen: ${f}`);
console.log(`results: ${join(OUT, 'results.json')}`);
if (bad.length > 0 || screenBad.length > 0) process.exitCode = 1;
