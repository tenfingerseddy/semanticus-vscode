// Authoring-journey driver for the Workflows Author surface.
//
// Based on /tmp/semanticus-joint-review/integrated-acceptance/drive.mjs: it serves the built
// Semanticus.VSCode tree over http, opens tools/uishot/harness.html in headless Chromium, and
// drives it with REAL clicks and REAL typing, then saves PNGs and asserts against the live DOM.
//
// HONEST LIMIT: there is no .NET engine in this worktree, so every RPC is answered by the harness's
// in-page mocks (tools/uishot/harness.html). What this proves is the webview journey against a
// faithful mock of previewWorkflowEdit / editWorkflowDocument / saveWorkflow, not the engine's own
// patcher. Engine-side patching is covered by Semanticus.Tests, not by this file.
//
// Usage: OUTDIR=<dir> node tools/drive-authoring.mjs   (browser: SEMANTICUS_BROWSER, else chromium)
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..');                       // Semanticus.VSCode
const require_ = createRequire(resolve(here, 'uishot/shot.mjs'));
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const OUT = process.env.OUTDIR || resolve(webRoot, '..', 'shots');
mkdirSync(OUT, { recursive: true });
const BROWSER = process.env.SEMANTICUS_BROWSER || '/usr/bin/chromium';

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
  executablePath: BROWSER, headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'],
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const checks = [];
const shots = {};
const record = (journey, name, ok, detail = '') => { checks.push({ journey, name, ok: !!ok, detail: String(detail).slice(0, 240) }); };

// ---- in-page primitives: every interaction below is a real click on a real rendered element -----
const HELPERS = `
  window.__d = {
    all(sel) { return Array.prototype.slice.call(document.querySelectorAll(sel)); },
    btn(text, exact) {
      var t = String(text);
      return this.all('button').filter(function (b) {
        if (!b.getClientRects().length) return false;
        var s = (b.textContent || '').trim();
        return exact ? s === t : s.indexOf(t) >= 0;
      })[0] || null;
    },
    click(text, exact) { var b = this.btn(text, exact); if (b && !b.disabled) { b.click(); return true; } return false; },
    enabled(text, exact) { var b = this.btn(text, exact); return !!b && !b.disabled; },
    present(text, exact) { return !!this.btn(text, exact); },
    rail() { return this.all('.sem-wf-outline button').map(function (b) { return (b.textContent || '').trim(); }); },
    stepTitles() { return this.rail().filter(function (s) { return /^\\d+\\./.test(s); }); },
    selectedRail() { return this.all('.sem-wf-outline button.active').map(function (b) { return (b.textContent || '').trim(); }); },
    inserts() { return this.all('.sem-wf-outline button[aria-label^="Insert a step after"]').map(function (b) { return b.getAttribute('aria-label'); }); },
    clickInsert(n) { var b = document.querySelector('.sem-wf-outline button[aria-label="Insert a step after step ' + n + '"]'); if (b && !b.disabled) { b.click(); return true; } return false; },
    canvasSteps() { return this.all('[data-wf-canvas-step]').length; },
    canvasEmpty() { return !!document.querySelector('[data-wf-canvas-empty]'); },
    emptyCard() { return !!document.querySelector('[data-wf-empty-steps]'); },
    focusField() { var a = document.activeElement; return a ? (a.tagName + ':' + (a.getAttribute('data-wf-field') || '')) : 'none'; },
    formStep() { var e = document.querySelector('[data-wf-step-form]'); return e ? e.getAttribute('data-wf-step-form') : null; },
    formHeading() { var e = document.querySelector('.sem-wf-form-card h3'); return e ? e.textContent.trim() : null; },
    source() { var t = document.querySelector('textarea[aria-label="Workflow source"]'); return t ? t.value : ''; },
    text() { return document.body.innerText; },
  };
`;
const SNIFFER = `
  window.__rpc = { results: [] };
  window.addEventListener('message', function (e) {
    var d = e && e.data;
    if (!d || d.type !== 'rpcResult') return;
    var msgs = (window.__uishotHarness && window.__uishotHarness.messages) || [];
    var call = msgs.filter(function (m) { return m && m.id === d.id; })[0];
    window.__rpc.results.push({ method: call ? call.method : null, params: call ? call.params : null, result: d.result });
  });
`;

const newPage = async () => {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: 1440, height: 900, deviceScaleFactor: 2 });
  await page.evaluateOnNewDocument(SNIFFER);
  page.__errors = errors;
  return page;
};
const refresh = (page) => page.evaluate(HELPERS);
const open = async (page, query = '', settle = 2600) => {
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html${query}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button', { timeout: 15000 });
  await wait(1000);
  await page.evaluate(() => { const b = [...document.querySelectorAll('nav button')].find((x) => (x.textContent || '').trim() === 'Workflows'); if (b) b.click(); });
  await wait(700);
  await page.evaluate(() => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === 'Workflows' && x.getClientRects().length); if (b) b.click(); });
  await wait(settle);
  await refresh(page);
};
const section = async (page, id, settle = 1000) => {
  const ok = await page.evaluate((s) => { const b = document.querySelector(`[data-section="${s}"]`); if (b) { b.click(); return true; } return false; }, id);
  await wait(settle); await refresh(page); return ok;
};
const click = async (page, text, exact = true, settle = 900) => {
  const ok = await page.evaluate((t, e) => window.__d.click(t, e), text, exact);
  await wait(settle); await refresh(page); return ok;
};
const q = (page, fn) => page.evaluate(fn);
const view = async (page, label, settle = 1900) => {
  await page.evaluate((v) => { const b = [...document.querySelectorAll('[role="tab"]')].find((x) => (x.textContent || '').trim() === v); if (b) b.click(); }, label);
  await wait(settle); await refresh(page);
};
const openWorkflow = async (page, name) => {
  await section(page, 'library', 1400);
  // Library remembers the last opened playbook, so it can land on that page instead of the list.
  const onList = await page.evaluate((n) => !!document.querySelector(`[data-workflow="${n}"]`), name);
  if (!onList) { await click(page, 'Library', true, 1400); }
  const ok = await page.evaluate((n) => { const b = document.querySelector(`[data-workflow="${n}"] button`); if (b) { b.click(); return true; } return false; }, name);
  await wait(1600); await refresh(page);
  const edited = await click(page, 'Edit workflow', true, 2800);
  return ok && edited;
};
// The harness's previewWorkflowEdit does not re-emit markdown for structural edits; it echoes the
// operations it folded in as "<!-- workflow editor preview: ... -->". That echo is what proves the
// Steps/Canvas edits and the Source view are one draft here. Real markdown emission is the engine's
// WorkflowDocumentPatcher, covered by Semanticus.Tests, not reachable from this node-only worktree.
const sourceOps = (text) => {
  const m = /workflow editor preview: ([^>]*)-->/.exec(text || '');
  return m ? m[1].trim().split(',').map((x) => x.trim()).filter(Boolean) : [];
};
const shot = async (page, name) => { const out = join(OUT, `${name}.png`); await page.screenshot({ path: out, fullPage: false }); shots[name] = out; return out; };
const noErrors = (page, J) => record(J, 'no page or console errors', page.__errors.length === 0, page.__errors.slice(0, 3).join(' | '));

// =================================================================================================
// JOURNEY 1 — create a workflow, add the first step, save, reopen from the Library.
// =================================================================================================
{
  const J = 'J1 create a workflow';
  const page = await newPage();
  await open(page, '');
  await section(page, 'author');
  record(J, 'Author pane offers "+ New workflow"', await q(page, () => window.__d.present('+ New workflow', true)));
  await click(page, '+ New workflow', true, 1100);
  await page.type('input.sem-wf-input', 'demo-playbook', { delay: 12 });
  await wait(300);
  await click(page, 'Create draft', true, 2400);

  const empty = await q(page, () => ({
    rail: window.__d.rail(), steps: window.__d.stepTitles().length,
    railAdd: window.__d.enabled('Add the first step', true),
    card: window.__d.emptyCard(), heading: window.__d.formHeading(),
  }));
  record(J, 'a new draft opens with zero steps', empty.steps === 0, `rail=${JSON.stringify(empty.rail)}`);
  record(J, 'the Steps rail carries an enabled "Add the first step"', empty.railAdd, `rail=${JSON.stringify(empty.rail)}`);
  record(J, 'the empty form area shows the add card, not a dead end', empty.card && empty.heading === 'No steps yet', `heading=${empty.heading}`);
  await shot(page, 'j1-empty-draft');

  await view(page, 'Canvas');
  const emptyCanvas = await q(page, () => ({ empty: window.__d.canvasEmpty(), add: window.__d.enabled('Add the first step', true), toolbar: window.__d.present('+ Add step', true) }));
  record(J, 'Canvas shows its own empty state with zero steps', emptyCanvas.empty);
  record(J, 'Canvas offers an enabled add action with zero steps', emptyCanvas.add);
  record(J, 'Canvas toolbar add control exists (no selection needed)', emptyCanvas.toolbar);
  await shot(page, 'j1-empty-canvas');
  await view(page, 'Steps');

  await click(page, 'Add the first step', true, 1300);
  const first = await q(page, () => ({ steps: window.__d.stepTitles(), selected: window.__d.selectedRail(), focus: window.__d.focusField(), form: window.__d.formStep() }));
  record(J, 'adding creates exactly one step', first.steps.length === 1, JSON.stringify(first.steps));
  record(J, 'the new step is selected in the rail and its form is open', /^1\./.test(first.selected[0] || '') && !!first.form, `selected=${JSON.stringify(first.selected)} form=${first.form}`);
  record(J, 'focus lands in the new step title field', first.focus === 'INPUT:step-title', first.focus);

  record(J, 'the rail keeps "+ Add step" at one step', await q(page, () => window.__d.enabled('+ Add step', true)));
  await click(page, '+ Add step', true, 1100);
  await click(page, '+ Add step', true, 1100);
  const three = await q(page, () => ({ steps: window.__d.stepTitles(), railAdd: window.__d.enabled('+ Add step', true), inserts: window.__d.inserts() }));
  record(J, 'the rail add reaches three steps', three.steps.length === 3, JSON.stringify(three.steps));
  record(J, 'the rail keeps "+ Add step" at three steps', three.railAdd);
  record(J, 'the rail carries insert controls between steps', three.inserts.length === 2, JSON.stringify(three.inserts));
  await shot(page, 'j1-three-steps');

  await view(page, 'Canvas');
  const canvasThree = await q(page, () => ({ boxes: window.__d.canvasSteps(), add: window.__d.enabled('+ Add step', true) }));
  record(J, 'Canvas shows three boxes with no save in between', canvasThree.boxes === 3, `boxes=${canvasThree.boxes}`);
  record(J, 'Canvas keeps its selection-independent add control at three steps', canvasThree.add);
  await shot(page, 'j1-three-canvas');

  await view(page, 'Steps', 2200);

  // Persistence fixture, seeded from the create preview the app itself just received.
  const seeded = await q(page, () => {
    const made = window.__rpc.results.filter((r) => r.method === 'previewWorkflowEdit' && r.result && r.result.editModel)[0];
    if (!made) return { ok: false, reason: 'no previewWorkflowEdit response captured' };
    const blank = structuredClone(made.result.editModel);
    blank.definition.steps = [];
    let revision = 0;
    window.__workflowDocument = {
      calls: [], deferred: false, refuseNext: false, createRace: false, pendingEditModel: null,
      document: { name: 'demo-playbook', library: 'stock', path: '/engine/workflows/demo-playbook.md',
        exactText: made.result.proposedText || '', byteHash: 'sha256:seed', editModel: blank,
        metadata: { schemaVersion: 2, title: 'demo-playbook', version: 1, stepIds: [], explicitIds: true, parses: true, parseError: null } },
      set(text, library = 'user') {
        this.document = { ...this.document, library, path: '/project/.semanticus/workflows/demo-playbook.md',
          exactText: text, byteHash: 'sha256:' + String(++revision).padStart(10, '0') };
        if (this.pendingEditModel) { this.document.editModel = this.pendingEditModel; this.pendingEditModel = null; }
        this.document.metadata = { ...this.document.metadata, stepIds: this.document.editModel.definition.steps.map((s) => s.id) };
      },
    };
    return { ok: true };
  });
  record(J, 'save fixture seeded from the real create preview', seeded.ok, seeded.reason || '');

  await click(page, 'Save workflow', true, 2400);
  record(J, 'Save shows the guarded preview with Apply', await q(page, () => window.__d.present('Apply', true)));
  await click(page, 'Apply', true, 2600);
  const saved = await q(page, () => ({ lib: window.__workflowDocument.document.library, ids: window.__workflowDocument.document.metadata.stepIds }));
  record(J, 'Apply writes the project copy with three steps', saved.lib === 'user' && saved.ids.length === 3, `library=${saved.lib} stepIds=${JSON.stringify(saved.ids)}`);

  // Publish it into the library the way the engine's workflowLibraryDidChange broadcast would.
  await q(page, () => {
    const last = window.__rpc.results.filter((r) => r.method === 'listWorkflows' && Array.isArray(r.result)).slice(-1)[0];
    const next = (last ? last.result.slice() : []).concat([{ name: 'demo-playbook', title: 'demo-playbook', description: '', version: 1,
      source: 'user', stepCount: 3, error: null, enabled: true, active: true, activeReason: null, requiredForOps: [], triggers: [], tags: [], gated: true, filePath: '/project/.semanticus/workflows/demo-playbook.md' }]);
    window.__uishotHarness.setResponse('listWorkflows', next);
    window.__uishotHarness.dispatch({ type: 'workflowLibraryDidChange', payload: next });
  });
  const reopenedOk = await openWorkflow(page, 'demo-playbook');
  const reopened = await q(page, () => ({ steps: window.__d.stepTitles(), form: window.__d.formStep() }));
  record(J, 'the saved workflow reopens from the Library', reopenedOk);
  record(J, 'the reopened workflow still has its three steps', reopened.steps.length === 3, JSON.stringify(reopened.steps));
  await shot(page, 'j1-reopened');
  noErrors(page, J);
  await page.close();
}

// Source is the third door on the same draft: added steps must reach it with no save in between.
{
  const J = 'J1 create a workflow';
  const page = await newPage();
  await open(page, '');
  await section(page, 'author');
  await click(page, '+ New workflow', true, 1100);
  await page.type('input.sem-wf-input', 'demo-playbook', { delay: 12 });
  await click(page, 'Create draft', true, 2400);
  await click(page, 'Add the first step', true, 1300);
  await click(page, '+ Add step', true, 1100);
  await click(page, '+ Add step', true, 1100);
  const before = await q(page, () => window.__d.stepTitles().length);
  await view(page, 'Source', 2800);
  const ops = sourceOps(await q(page, () => window.__d.source()));
  record(J, 'Source opens on the same draft and carries the three adds, no save in between',
    before === 3 && ops.filter((o) => o === 'add_step').length === 3, `steps=${before} ops=${JSON.stringify(ops)}`);
  noErrors(page, J);
  await page.close();
}

// =================================================================================================
// JOURNEY 2 — add later steps, insert between, reorder, delete, all live in Canvas and Source.
// =================================================================================================
{
  const J = 'J2 add later steps';
  const page = await newPage();
  await open(page, '');
  const opened = await openWorkflow(page, 'loop-handoff');
  record(J, 'an existing two-step project workflow opens in the editor', opened);
  const start = await q(page, () => ({ steps: window.__d.stepTitles(), form: window.__d.formStep() }));
  record(J, 'it starts with two steps', start.steps.length === 2, JSON.stringify(start.steps));

  await click(page, 'Add step after', true, 1200);
  const after = await q(page, () => ({ steps: window.__d.stepTitles(), selected: window.__d.selectedRail(), focus: window.__d.focusField() }));
  record(J, '"Add step after" inserts directly after the selected step', after.steps.length === 3 && /^2\. New step/.test(after.steps[1] || ''), JSON.stringify(after.steps));
  record(J, '"Add step after" selects and focuses the new step', /^2\./.test(after.selected[0] || '') && after.focus === 'INPUT:step-title', `${JSON.stringify(after.selected)} ${after.focus}`);

  await click(page, '+ Add step', true, 1200);
  const appended = await q(page, () => window.__d.stepTitles());
  record(J, 'the rail "+ Add step" appends at the end', appended.length === 4 && /^4\. New step/.test(appended[3] || ''), JSON.stringify(appended));

  const insertOk = await page.evaluate(() => window.__d.clickInsert(1));
  await wait(1200); await refresh(page);
  const inserted = await q(page, () => window.__d.stepTitles());
  record(J, 'the rail insert control adds a step between two steps', insertOk && inserted.length === 5 && /^2\. New step/.test(inserted[1] || ''), `${insertOk} ${JSON.stringify(inserted)}`);
  await shot(page, 'j2-five-steps');

  // The inserted step is selected and its title focused, so typing names it. Three sibling steps are
  // all called "New step", and a reorder between two of them is invisible unless one is named.
  await page.keyboard.type('Reconcile', { delay: 14 });
  await wait(900); await refresh(page);
  const named = await q(page, () => window.__d.stepTitles());
  record(J, 'typing straight after an insert names the new step', /^2\. Reconcile/.test(named[1] || ''), JSON.stringify(named));

  const at = (list) => list.findIndex((t) => /Reconcile/.test(t)) + 1;
  await click(page, 'Move down', true, 1300);
  const down = await q(page, () => window.__d.stepTitles());
  record(J, 'Move down moves the step one place later', at(down) === at(named) + 1, `${at(named)} -> ${at(down)}: ${JSON.stringify(down)}`);
  await click(page, 'Move up', true, 1300);
  const up = await q(page, () => window.__d.stepTitles());
  record(J, 'Move up restores the previous order', JSON.stringify(up) === JSON.stringify(named), JSON.stringify(up));

  await view(page, 'Canvas');
  const canvas = await q(page, () => ({ boxes: window.__d.canvasSteps(), titles: window.__d.all('[data-wf-canvas-step]').map((e) => e.innerText.replace(/\n/g, ' ')) }));
  record(J, 'Canvas shows all five steps with no save in between', canvas.boxes === 5, `boxes=${canvas.boxes}`);
  record(J, 'Canvas carries the renamed step with no save in between', canvas.titles.some((t) => /Reconcile/.test(t)), JSON.stringify(canvas.titles).slice(0, 200));
  await shot(page, 'j2-canvas');
  await view(page, 'Source', 3000);
  const ops = sourceOps(await q(page, () => window.__d.source()));
  record(J, 'Source carries the three adds and both moves, no save in between',
    ops.filter((o) => o === 'add_step').length === 3 && ops.filter((o) => o === 'move_step').length === 2, JSON.stringify(ops));
  await view(page, 'Steps', 3000);

  // Delete down to one and prove the minimum-one rule holds.
  let count = (await q(page, () => window.__d.stepTitles())).length;
  let guard = 0;
  while (count > 1 && guard++ < 10) {
    const did = await click(page, 'Delete step', true, 1100);
    if (!did) break;
    count = (await q(page, () => window.__d.stepTitles())).length;
  }
  const last = await q(page, () => ({ steps: window.__d.stepTitles(), present: window.__d.present('Delete step', true), enabled: window.__d.enabled('Delete step', true) }));
  record(J, 'steps delete down to the last one', last.steps.length === 1, JSON.stringify(last.steps));
  record(J, 'the minimum-one rule disables Delete step at one step', last.present && !last.enabled, `present=${last.present} enabled=${last.enabled}`);
  await shot(page, 'j2-one-step-delete-locked');
  noErrors(page, J);
  await page.close();
}

// =================================================================================================
// JOURNEY 3 — edit an existing project workflow from the Library.
// =================================================================================================
{
  const J = 'J3 edit from the Library';
  const page = await newPage();
  await open(page, '');
  await openWorkflow(page, 'loop-handoff');
  const landed = await q(page, () => ({
    tab: [...document.querySelectorAll('[role="tab"]')].filter((b) => b.getAttribute('aria-selected') === 'true').map((b) => b.textContent.trim())[0] || null,
    selected: window.__d.selectedRail(), form: window.__d.formStep(), heading: window.__d.formHeading(),
    actions: ['Move up', 'Move down', 'Add step after', 'Copy step', 'Delete step'].filter((t) => window.__d.present(t, true)),
  }));
  record(J, 'the editor opens on the Steps view', landed.tab === 'Steps', `tab=${landed.tab}`);
  record(J, 'the first step is selected, not Workflow settings', /^1\./.test(landed.selected[0] || '') && !!landed.form, `selected=${JSON.stringify(landed.selected)} form=${landed.form}`);
  record(J, 'the step form and all five step actions are visible immediately', landed.actions.length === 5, JSON.stringify(landed.actions));
  await shot(page, 'j3-project-workflow');

  // A project workflow with no steps at all is the case that used to have no add control anywhere.
  await openWorkflow(page, 'month-end-close');
  const zero = await q(page, () => ({ steps: window.__d.stepTitles(), railAdd: window.__d.enabled('Add the first step', true), card: window.__d.emptyCard(), rail: window.__d.rail() }));
  record(J, 'a zero-step project workflow still offers an add action', zero.steps.length === 0 && zero.railAdd && zero.card, `rail=${JSON.stringify(zero.rail)}`);
  // Workflow settings selected: the rail add must still be reachable (Fable's second gap).
  await page.evaluate(() => { const b = window.__d.btn('Workflow settings', true); if (b) b.click(); });
  await wait(1000); await refresh(page);
  const onSettings = await q(page, () => ({ heading: window.__d.formHeading(), railAdd: window.__d.enabled('Add the first step', true), card: window.__d.emptyCard() }));
  record(J, 'with Workflow settings selected the add action is still reachable', onSettings.railAdd && onSettings.card, `heading=${onSettings.heading}`);
  await shot(page, 'j3-zero-step-project-workflow');
  noErrors(page, J);
  await page.close();
}

// =================================================================================================
// JOURNEY 4 — copy a built-in, then edit the copy.
// =================================================================================================
{
  const J = 'J4 copy a built-in';
  const page = await newPage();
  await open(page, '?wf=editor-stock', 5200);
  await wait(2500); await refresh(page);
  const ro = await q(page, () => ({
    pill: /read only/i.test(window.__d.text()),
    copyBtn: window.__d.enabled('Copy to this project', true),
    note: (window.__d.text().match(/Copy to this project to edit/g) || []).length,
    railAdd: window.__d.present('+ Add step', true) || window.__d.present('Add the first step', true),
    railAddEnabled: window.__d.enabled('+ Add step', true) || window.__d.enabled('Add the first step', true),
    steps: window.__d.stepTitles(),
  }));
  record(J, 'the built-in opens read only', ro.pill && ro.copyBtn, `pill=${ro.pill} copy=${ro.copyBtn}`);
  record(J, 'the add controls are PRESENT but disabled, not absent', ro.railAdd && !ro.railAddEnabled, `present=${ro.railAdd} enabled=${ro.railAddEnabled}`);
  record(J, '"Copy to this project to edit" is shown beside the disabled controls', ro.note >= 1, `occurrences=${ro.note}`);
  await shot(page, 'j4-builtin-read-only');

  await click(page, 'Copy to this project', true, 3000);
  const copied = await q(page, () => ({
    notice: /Project copy created/i.test(window.__d.text()),
    railAdd: window.__d.enabled('+ Add step', true),
    inserts: window.__d.inserts().length,
    present: ['Move up', 'Move down', 'Add step after', 'Copy step', 'Delete step'].filter((t) => window.__d.present(t, true)),
    // Move up is correctly disabled while step 1 is the selected step; the other four must be live.
    enabled: ['Move down', 'Add step after', 'Copy step', 'Delete step'].filter((t) => window.__d.enabled(t, true)),
    stillReadOnly: /Copy to this project to edit/.test(window.__d.text()),
    steps: window.__d.stepTitles(),
  }));
  record(J, 'the copy opens editable', copied.notice && !copied.stillReadOnly, `notice=${copied.notice} note=${copied.stillReadOnly}`);
  record(J, 'the copy has the same add controls, now enabled', copied.railAdd && copied.inserts >= 1, `railAdd=${copied.railAdd} inserts=${copied.inserts}`);
  record(J, 'the copy shows the full step action set', copied.present.length === 5, JSON.stringify(copied.present));
  record(J, 'every step action that applies at step 1 is enabled on the copy', copied.enabled.length === 4, JSON.stringify(copied.enabled));
  await shot(page, 'j4-copy-editable');

  await click(page, '+ Add step', true, 1300);
  const grew = await q(page, () => window.__d.stepTitles());
  record(J, 'a step can be added to the copy', grew.length === copied.steps.length + 1, `${copied.steps.length} -> ${grew.length}`);
  noErrors(page, J);
  await page.close();
}

const failed = checks.filter((c) => !c.ok);
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ checks, shots }, null, 2));
for (const c of checks) console.log(`${c.ok ? 'PASS' : 'FAIL'}  [${c.journey}] ${c.name}${c.ok ? '' : '  -- ' + c.detail}`);
console.log(`\n${checks.length - failed.length}/${checks.length} authoring-journey checks passed.`);
await browser.close(); server.close();
process.exit(failed.length ? 1 : 0);
