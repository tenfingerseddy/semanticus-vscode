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
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'drafts');
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
  return { boxes, marks, banners, choices, writes, policyChecked, otherLine, otherRows, pageText: document.body.innerText };
};

const has = (cond, msg) => (cond ? [] : [msg]);
const box = (r, label) => r.boxes.find((b) => b.label.includes(label));

// ---- scenes --------------------------------------------------------------------------------------------------------
const INSTR = { present: true, instructions: 'Use net sales for revenue questions.', length: 36, limit: 2000, culture: 'en-US' };
const TYPED = ' Always show the currency.';
const SCENES = [];
const scene = (slug, desc, def) => SCENES.push({ slug, desc, ...def });

// AI instructions: the Instructions tab is its own page; leaving it unmounts the card.
const instructionsOpen = [click('[data-testid="overview-instructions"] button', 'Edit'), typeIn('#overview-instructions-editor', TYPED)];
scene('instructions-switch', 'AI instructions: type, go to the Plan, come back', {
  q: 'tab=instructions', ready: '[data-testid="overview-instructions"]',
  steps: [...instructionsOpen, probe('typed'), nav('plan'), nav('instructions'), probe('back')],
  expect: (p) => [
    ...has(box(p.typed, 'instructions-editor')?.value.endsWith(TYPED), `the words were not typed: ${JSON.stringify(p.typed.boxes)}`),
    ...has(p.back.boxes.some((b) => b.value.endsWith(TYPED)), `the typed words are not back: ${JSON.stringify(p.back.boxes)}`),
    ...has(p.back.marks.some((m) => /Draft not saved/.test(m)), `no "Draft not saved" mark: ${JSON.stringify(p.back.marks)}`),
    ...has(p.back.banners.length === 0, 'a choice is shown though nothing moved'),
    ...has(p.back.writes.length === 0, `something was written: ${p.back.writes}`),
  ],
});
scene('instructions-restart', 'AI instructions: type, the engine restarts, the words are still there', {
  q: 'tab=instructions', ready: '[data-testid="overview-instructions"]',
  steps: [...instructionsOpen, restart(), nav('instructions'), probe('back')],
  expect: (p) => [
    ...has(p.back.boxes.some((b) => b.value.endsWith(TYPED)), `the typed words are not back after the restart: ${JSON.stringify(p.back.boxes)}`),
    ...has(p.back.marks.some((m) => /Draft not saved/.test(m)), 'no mark after the restart'),
  ],
});
scene('instructions-moved', 'AI instructions: type, leave, the assistant rewrites them, come back and choose', {
  q: 'tab=instructions', ready: '[data-testid="overview-instructions"]',
  steps: [...instructionsOpen, nav('plan'), assistant('getAiInstructions', { ...INSTR, instructions: 'Use gross sales for revenue questions.' }), nav('instructions'), probe('back'),
    click('button', 'Use the new one'), probe('after')],
  expect: (p) => [
    ...has(p.back.boxes.some((b) => b.value.endsWith(TYPED)), 'the typed words were replaced by the assistant\'s'),
    ...has(p.back.choices.length === 2, `Keep mine / Use the new one are not both shown: ${JSON.stringify(p.back.choices)}`),
    ...has(p.back.banners.length === 1, 'the page does not say the instructions changed'),
    ...has(p.back.writes.length === 0, `something was written before the person chose: ${p.back.writes}`),
    ...has(p.after.boxes.some((b) => b.value === 'Use gross sales for revenue questions.'), `Use the new one did not load the assistant\'s words: ${JSON.stringify(p.after.boxes)}`),
    ...has(p.after.marks.length === 0, 'the mark stays after Use the new one'),
  ],
});

// Model notes (the primer) are the Notes tab of the same page; the whole page unmounts on a tab switch.
const notesOpen = [click('button', 'Edit notes'), typeIn('[aria-label="Model notes, written in Markdown"]', '\n\nMy note.')];
scene('notes-switch', 'Model notes: type, go to the Plan, come back', {
  q: 'tab=knowledge', ready: '[aria-label="AI instructions"]',
  steps: [...notesOpen, nav('plan'), nav('knowledge'), probe('back')],
  expect: (p) => [
    ...has(p.back.boxes.some((b) => b.value.endsWith('My note.')), `the typed notes are not back: ${JSON.stringify(p.back.boxes.map((b) => b.label))}`),
    ...has(p.back.marks.some((m) => /Draft not saved/.test(m)), 'no "Draft not saved" mark'),
  ],
});
scene('notes-restart', 'Model notes: type, the engine restarts', {
  q: 'tab=knowledge', ready: '[aria-label="AI instructions"]',
  steps: [...notesOpen, restart(), nav('knowledge'), probe('back')],
  expect: (p) => [...has(p.back.boxes.some((b) => b.value.endsWith('My note.')), 'the typed notes are not back after the restart')],
});

// Documentation: a narrative section is a textarea; leaving it saves it when nothing moved, so the scene takes the engine down first (the restart case) and
// uses a moved section for the switch case.
scene('docs-restart', 'Documentation: type a narrative, the engine restarts, go back', {
  q: 'tab=docs', ready: '[data-testid="docs-page"], textarea, .sem-page',
  steps: [typeIn('textarea[placeholder^="Markdown"]', ' My narrative.'), probe('typed'),
    eval_(`(() => { const h = window.__uishotHarness; h.setResponse('setDocSection', () => ({ __uishotError: 'Engine not connected.' })); return true; })()`),
    restart(), nav('docs'), probe('back')],
  expect: (p) => [
    ...has(p.typed.boxes.some((b) => b.value.endsWith(' My narrative.')), `the words were not typed: ${JSON.stringify(p.typed.boxes.map((b) => b.label))}`),
    ...has(p.back.boxes.some((b) => b.value.endsWith(' My narrative.')), `the narrative is not back: ${JSON.stringify(p.back.boxes.map((b) => b.label))}`),
    ...has(p.back.marks.some((m) => /Draft not saved/.test(m)), 'no mark'),
  ],
});

// Calculation groups (Edit > Advanced modelling): an item's DAX is a code box; the area is chosen again after the switch, as a person would.
const CALC = [click('button', 'Calculation groups'), typeIn('[aria-label="YTD expression"] .cm-content', ' + 0')];
scene('calc-switch', 'Calculation item: type into its DAX, go to the Plan, come back', {
  q: 'tab=advmodels', ready: '.sem-page, .xe-under-bar',
  steps: [...CALC, probe('typed'), nav('plan'), nav('advmodels'), click('button', 'Calculation groups'), probe('back')],
  expect: (p) => [
    ...has(p.typed.boxes.some((b) => b.label.includes('YTD expression') && b.value.endsWith(' + 0')), `the DAX was not typed: ${JSON.stringify(p.typed.boxes.map((b) => b.label))}`),
    ...has(p.back.boxes.some((b) => b.label.includes('YTD expression') && b.value.endsWith(' + 0')), 'the typed DAX is not back'),
    ...has(p.back.marks.length >= 1, 'no mark'),
  ],
});

scene('calc-restart', 'Calculation item: type into its DAX, the engine restarts, go back', {
  q: 'tab=advmodels', ready: '.sem-page, .xe-under-bar',
  steps: [...CALC, restart(), nav('advmodels'), click('button', 'Calculation groups'), probe('back')],
  expect: (p) => [
    ...has(p.back.boxes.some((b) => b.label.includes('YTD expression') && b.value.endsWith(' + 0')), `the typed DAX is not back after the restart: ${JSON.stringify(p.back.boxes)}`),
    ...has(p.back.marks.length >= 1, 'no mark'),
  ],
});
const GROUPS_MOVED = [{ ref: 'table:Time Intelligence', name: 'Time Intelligence', precedence: 10, items: [
  { ref: 'calcitem:Time Intelligence/YTD', name: 'YTD', ordinal: 0, expression: 'SELECTEDMEASURE ()', formatStringExpression: '' },
  { ref: 'calcitem:Time Intelligence/PY', name: 'PY', ordinal: 1, expression: "CALCULATE ( SELECTEDMEASURE (), SAMEPERIODLASTYEAR ( 'Date'[Date] ) )", formatStringExpression: '' }] }];
scene('calc-moved', 'Calculation item: type, leave, the assistant rewrites the item, come back and choose', {
  q: 'tab=advmodels', ready: '.sem-page, .xe-under-bar',
  steps: [...CALC, nav('plan'), assistant('listCalculationGroups', GROUPS_MOVED), assistant('getDax', 'SELECTEDMEASURE ()'), nav('advmodels'), click('button', 'Calculation groups'), probe('back')],
  expect: (p) => [
    ...has(p.back.boxes.some((b) => b.label.includes('YTD expression') && b.value.endsWith(' + 0')), 'the typed DAX was replaced by the assistant\'s'),
    ...has(p.back.choices.length === 2, `Keep mine / Use the new one are not both shown: ${JSON.stringify(p.back.choices)}`),
    ...has(p.back.writes.length === 0, `something was written before the person chose: ${p.back.writes}`),
  ],
});

// W3 (fix round 2): the assistant renames the item while its DAX is typed. The words are kept, the page says so, and Keep mine moves them onto the renamed item.
const GROUPS_RENAMED = [{ ref: 'table:Time Intelligence', name: 'Time Intelligence', precedence: 10, items: [
  { ref: 'calcitem:Time Intelligence/Year to date', name: 'Year to date', ordinal: 0, expression: 'SELECTEDMEASURE ()', formatStringExpression: '' },
  GROUPS_MOVED[0].items[1]] }];
const pickRenamed = `(() => { const s = document.querySelector('[data-testid="orphan-pick"]'); if (!s) return false; Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(s, 'calcitem:Time Intelligence/Year to date'); s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`;
scene('calc-renamed', 'Calculation item: type its DAX, the assistant renames the item, come back: the words are kept and asked about, Keep mine moves them', {
  q: 'tab=advmodels', ready: '.sem-page, .xe-under-bar',
  steps: [...CALC, nav('plan'), assistant('listCalculationGroups', GROUPS_RENAMED), nav('advmodels'), click('button', 'Calculation groups'), probe('asked'),
    eval_(pickRenamed), click('button', 'Keep mine on it'), probe('moved')],
  expect: (p) => [
    ...has(/Words you typed for a calculation item that is not here now are kept/.test(p.asked.pageText), `the page does not say the words are kept: ${p.asked.pageText.slice(0, 200)}`),
    ...has(/Time Intelligence \/ YTD/.test(p.asked.pageText), 'the page does not name the item'),
    ...has(p.asked.writes.length === 0, `something was written: ${p.asked.writes}`),
    ...has(p.moved.boxes.some((b) => b.label.includes('Year to date expression') && b.value.endsWith(' + 0')), `the words are not on the renamed item: ${JSON.stringify(p.moved.boxes.map((b) => b.label + '=' + b.value))}`),
    ...has(!/Words you typed for a calculation item/.test(p.moved.pageText), 'the ask stays after Keep mine'),
    ...has(p.moved.writes.length === 0, `Keep mine wrote something: ${p.moved.writes}`),
  ],
});

scene('calc-renamed-asked', 'Calculation item: the assistant renamed it; the page shows the kept words and the choice (screenshot of the ask)', {
  q: 'tab=advmodels', ready: '.sem-page, .xe-under-bar',
  steps: [...CALC, nav('plan'), assistant('listCalculationGroups', GROUPS_RENAMED), nav('advmodels'), click('button', 'Calculation groups'), probe('asked')],
  expect: (p) => [
    ...has(/Keep mine on it/.test(p.asked.pageText) && /Discard/.test(p.asked.pageText), 'the ask does not offer Keep mine on it and Discard'),
    ...has(p.asked.writes.length === 0, `something was written: ${p.asked.writes}`),
  ],
});

// Model notes: a lesson (insight) being edited.
scene('insight-switch', 'Insight card: edit a lesson, go to the Plan, come back', {
  q: 'tab=knowledge', ready: '[aria-label="AI instructions"]',
  steps: [click('article button', 'Edit'), typeIn('textarea[aria-label="Insight text"]', ' Reworded.'), probe('typed'), nav('plan'), nav('knowledge'), probe('back')],
  expect: (p) => [
    ...has(p.typed.boxes.some((b) => b.label === 'Insight text' && b.value.endsWith(' Reworded.')), 'the lesson words were not typed'),
    ...has(p.back.boxes.some((b) => b.label === 'Insight text' && b.value.endsWith(' Reworded.')), `the lesson is not open on the typed words: ${JSON.stringify(p.back.boxes.map((b) => b.label))}`),
    ...has(p.back.marks.some((m) => /Draft not saved/.test(m)), 'no mark'),
  ],
});

// Custom rules (BPA): a NEW rule left half written.
scene('rules-switch', 'Custom rules: start a new rule, type its id, go to the Plan, come back', {
  q: 'tab=bpa', ready: 'section.sem-card',
  steps: [click('button', 'New rule'), typeIn('input[placeholder^="ORG"], input[placeholder*="id" i]', 'ORG_MY_RULE'), probe('typed'), nav('plan'), nav('bpa'), probe('back')],
  expect: (p) => [
    ...has(p.typed.boxes.some((b) => b.value.includes('ORG_MY_RULE')), `the id was not typed: ${JSON.stringify(p.typed.boxes.map((b) => b.label + '=' + b.value))}`),
    ...has(p.back.boxes.some((b) => b.value.includes('ORG_MY_RULE')), 'the new-rule form is not open on the typed id'),
    ...has(p.back.marks.some((m) => /Draft not saved/.test(m)), 'no mark'),
  ],
});

// Spec: the Edit JSON box. The text is the whole spec; the person's edit is appended at the end of it.
scene('spec-json-switch', 'Spec JSON: edit it, go to the Plan, come back', {
  q: 'tab=spec', ready: '.sem-page, .xe-under-bar, section',
  steps: [click('button', 'Edit JSON'), typeIn('textarea[spellcheck="false"]', '\n/* mine */'), probe('typed'), nav('plan'), nav('spec'), probe('back')],
  expect: (p) => [
    ...has(p.typed.boxes.some((b) => b.value.endsWith('/* mine */')), 'the JSON edit was not typed'),
    ...has(p.back.boxes.some((b) => b.value.endsWith('/* mine */')), `the JSON editor is not open on the typed text: ${JSON.stringify(p.back.boxes.map((b) => b.label))}`),
    ...has(p.back.marks.some((m) => /Draft not saved/.test(m)), 'no mark'),
  ],
});
scene('spec-json-restart', 'Spec JSON: edit it, the engine restarts, go back', {
  q: 'tab=spec', ready: '.sem-page, .xe-under-bar, section',
  steps: [click('button', 'Edit JSON'), typeIn('textarea[spellcheck="false"]', '\n/* mine */'), restart(), nav('spec'), probe('back')],
  expect: (p) => [...has(p.back.boxes.some((b) => b.value.endsWith('/* mine */')), 'the JSON edit is not back after the restart')],
});

// W2 (fix round 2): the structured view's inline edits (here the model name) are held like Edit JSON.
const nameEdit = [typeIn('input[aria-label="Model name"]', ' mine'), eval_('document.activeElement.blur()')];
scene('spec-struct-switch', 'Spec structured view: edit the model name, go to the Plan, come back', {
  q: 'tab=spec', ready: '.sem-page, .xe-under-bar, section',
  steps: [...nameEdit, probe('typed'), nav('plan'), nav('spec'), probe('back')],
  expect: (p) => [
    ...has(p.typed.boxes.some((b) => b.label === 'Model name' && b.value.endsWith(' mine')), `the name was not typed: ${JSON.stringify(p.typed.boxes.map((b) => b.label + '=' + b.value))}`),
    ...has(p.back.boxes.some((b) => b.label === 'Model name' && b.value.endsWith(' mine')), `the typed name is not back: ${JSON.stringify(p.back.boxes.map((b) => b.label + '=' + b.value))}`),
    ...has(/Unsaved changes/.test(p.back.pageText), 'the tab does not say it holds unsaved changes'),
    ...has(p.back.writes.length === 0, `something was written: ${p.back.writes}`),
  ],
});
scene('spec-struct-restart', 'Spec structured view: edit the model name, the engine restarts, go back', {
  q: 'tab=spec', ready: '.sem-page, .xe-under-bar, section',
  steps: [...nameEdit, restart(), nav('spec'), probe('back')],
  expect: (p) => [...has(p.back.boxes.some((b) => b.label === 'Model name' && b.value.endsWith(' mine')), `the typed name is not back after the restart: ${JSON.stringify(p.back.boxes.map((b) => b.label + '=' + b.value))}`),
    ...has(/Unsaved changes/.test(p.back.pageText), 'no unsaved mark after the restart')],
});

// Join 11 solui, U2 (R-B): the assistant saves a new model name while the person is still typing in the name box. The words stay, the newer name is shown, a blur is not a decision.
const specMoves = `(() => { const h = window.__uishotHarness, s = h.response('getSpec'); h.setResponse('getSpec', () => ({ ...s, version: (s.version || 1) + 1, spec: { ...s.spec, name: 'Assistant name' } })); h.dispatch({ type: 'specDidChange' }); })()`;
scene('spec-name-moved', 'Spec model name: type, the assistant saves another name, leave the box', {
  q: 'tab=spec', ready: '.sem-page, .xe-under-bar, section', shot: '[data-testid="spec-words-moved"]',
  steps: [typeIn('input[aria-label="Model name"]', ' mine'), eval_(specMoves), eval_('new Promise((r) => setTimeout(r, 700))'), probe('moved'), eval_('document.activeElement.blur()'), probe('blurred')],
  expect: (p) => [
    ...has(p.moved.boxes.some((b) => b.label === 'Model name' && b.value.endsWith(' mine')), `the typed name was replaced: ${JSON.stringify(p.moved.boxes.map((b) => b.label + '=' + b.value))}`),
    ...has(/Assistant name/.test(p.moved.pageText) && /Your words are kept/.test(p.moved.pageText), 'the page does not show the newer name and say the words are kept'),
    ...has(p.blurred.boxes.some((b) => b.label === 'Model name' && b.value.endsWith(' mine')), 'a blur dropped the typed name'),
    ...has(p.blurred.choices.length === 2 || /Keep mine/.test(p.blurred.pageText), 'the choice went away on blur'),
    ...has(p.blurred.writes.length === 0, `something was written before the person chose: ${p.blurred.writes}`),
  ],
});
scene('spec-name-use-new', 'Spec model name: type, the assistant saves another name, choose Use the new one', {
  q: 'tab=spec', ready: '.sem-page, .xe-under-bar, section',
  steps: [typeIn('input[aria-label="Model name"]', ' mine'), eval_(specMoves), eval_('new Promise((r) => setTimeout(r, 700))'), eval_('document.activeElement.blur()'), click('button', 'Use the new one'), probe('after')],
  expect: (p) => [
    ...has(p.after.boxes.some((b) => b.label === 'Model name' && b.value === 'Assistant name'), `Use the new one did not show the newer name: ${JSON.stringify(p.after.boxes.map((b) => b.label + '=' + b.value))}`),
    ...has(!/Your words are kept/.test(p.after.pageText), 'the choice stays after Use the new one'),
  ],
});

// Validation: a NEW check (trusted answer). The Tests tab stays mounted on a tab switch but is rebuilt on a restart.
const setInput = `(() => { const span = [...document.querySelectorAll('aside label span')].find((s) => s.textContent.trim() === 'The number you trust'); const input = span && span.parentElement.querySelector('input'); if (!input) return false; Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input, '125049'); input.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`;
const newCheck = [click('button', 'New check'), click('[role="menuitem"]', 'Trusted answer'), eval_(setInput)];
scene('tests-new-check-restart', 'Validation: start a new Trusted answer check, type the number, close, the engine restarts, open it again', {
  q: 'conn=1&live=1&tier=pro&tab=tests', ready: 'nav button',
  steps: [...newCheck, probe('typed'), click('aside button', 'Close'), restart(), nav('tests'), click('button', 'New check'), probe('menu'), click('[role="menuitem"]', 'Trusted answer'), probe('back')],
  expect: (p) => [
    ...has(p.typed.boxes.some((b) => b.value === '125049'), `the number was not typed: ${JSON.stringify(p.typed.boxes)}`),
    ...has(p.back.boxes.some((b) => b.value === '125049'), `the drawer is not open on the typed number: ${JSON.stringify(p.back.boxes)}`),
    ...has(p.back.marks.some((m) => /Draft not saved/.test(m)), 'no mark in the drawer'),
    ...has(p.back.writes.length === 0, `something was saved: ${p.back.writes}`),
  ],
});

// M code: the incremental refresh policy form. Turning the policy on is the change; it is only applied by Save.
scene('mcode-policy-restart', 'M code: switch the refresh policy on, the engine restarts, go back', {
  q: 'tab=mcode', ready: '[aria-label="Incremental refresh settings"]', shot: '[aria-label="Incremental refresh settings"]', height: 1500,
  steps: [click('button, a', 'Configure'), probe('before'), click('[aria-label="Incremental refresh settings"] input[type="checkbox"]'), probe('typed'), restart(), nav('mcode'), probe('back')],
  expect: (p) => [
    ...has(p.typed.policyChecked[0] === !p.before.policyChecked[0], `the switch did not change: ${JSON.stringify([p.before.policyChecked, p.typed.policyChecked])}`),
    ...has(p.back.policyChecked[0] === p.typed.policyChecked[0], `the policy switch is not as the person left it: ${JSON.stringify(p.back.policyChecked)}`),
    ...has(p.back.marks.some((m) => /Draft not saved/.test(m)), 'no mark'),
    ...has(p.back.writes.length === 0, `something was saved: ${p.back.writes}`),
  ],
});

// The Studio panel is closed (VS Code drops the webview's state) and opened again: the words typed before are back (round 3, the host keeps a copy in workspaceState).
scene('panel-close-reopen', 'AI instructions: type, close the Studio panel, open it again: the words are back, marked as a draft', {
  q: 'tab=instructions', ready: '[data-testid="overview-instructions"]',
  steps: [...instructionsOpen, probe('typed'), reopen(), nav('instructions'), probe('back')],
  expect: (p) => [
    ...has(p.typed.boxes.some((b) => b.value.endsWith(TYPED)), 'precondition: the words were typed'),
    ...has(p.back.boxes.some((b) => b.value.endsWith(TYPED)), `the typed words are not back after the panel was closed and opened again: ${JSON.stringify(p.back.boxes)}`),
    ...has(p.back.marks.some((m) => /Draft not saved/.test(m)), 'no mark after reopening'),
    ...has(p.back.writes.length === 0, `something was saved: ${p.back.writes}`),
  ],
});

// Words held for ANOTHER model: one quiet line, Copy and Discard, never attached, never written (round 3).
const OTHERS = encodeURIComponent(JSON.stringify({ 'studio.drafts': {
  [JSON.stringify(['desktop:contoso sales', 'instructions', ''])]: { value: 'Words typed for the Contoso Sales model.', base: 'Old.', modelLabel: 'Contoso Sales' },
  [JSON.stringify(['live:powerbi://x/finance|budget', 'knowledge', ''])]: { value: 'Budget notes.', base: '', modelLabel: 'Budget' },
} }));
scene('other-models-line', 'Words kept for other models: the quiet line, the list, Discard clears one', {
  q: `tab=instructions&state=${OTHERS}`, ready: '[data-testid="overview-instructions"]',
  steps: [probe('line'), click('[data-testid="other-model-words-toggle"]'), probe('open'), click('[data-testid="other-model-discard"]'), probe('after')],
  expect: (p) => [
    ...has(p.line.otherLine.some((t) => /Words kept for 2 other models/.test(t)), `no line for the two other models: ${JSON.stringify(p.line.otherLine)}`),
    ...has(p.line.boxes.every((b) => !/Contoso Sales model/.test(b.value)), 'the other model\'s words were attached to an editor'),
    ...has(p.open.otherRows.length === 2 && p.open.otherRows.some((t) => /Contoso Sales/.test(t)) && p.open.otherRows.some((t) => /Budget/.test(t)), `the list does not name both models: ${JSON.stringify(p.open.otherRows)}`),
    ...has(p.after.otherRows.length === 1, `Discard did not clear exactly one model: ${JSON.stringify(p.after.otherRows)}`),
    ...has(p.after.writes.length === 0, `something was written: ${p.after.writes}`),
  ],
});

// A draft left behind for ANOTHER model must never hold the Plan up with no way to restart the engine (A1).
const ORPHAN = encodeURIComponent(JSON.stringify({ 'studio.drafts': { [JSON.stringify(['/models/Gone.SemanticModel', 'startplan|w-desc-staff', 'description'])]: { value: 'Typed long ago.', base: 'Old words.' } } }));
scene('orphan-draft-engine-down', 'An orphan draft of another model, the engine is down: the page offers Restart engine', {
  q: `engine=down&coldNav=plan&state=${ORPHAN}`, ready: 'body',
  steps: [probe('page')],
  expect: (p) => [
    ...has(/Restart engine/.test(p.page.pageText), `no Restart engine on the page: ${JSON.stringify(p.page.pageText.slice(0, 200))}`),
    ...has(!/Open a model to begin/.test(p.page.pageText), 'the page says "Open a model to begin" with no way to restart'),
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
