// Action-picker driver for the Workflows Author surface.
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
// Usage: OUTDIR=<dir> node tools/drive-oppicker.mjs   (browser: SEMANTICUS_BROWSER, else chromium)
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
    picker() { return !!document.querySelector('[data-wf-op-picker]'); },
    questions() { return this.all('[data-wf-op-question]').map(function (e) { return e.getAttribute('data-wf-op-question'); }); },
    shelves() { return this.all('[data-wf-op-shelf]').map(function (e) { return e.getAttribute('data-wf-op-shelf'); }); },
    choices() { return this.all('[data-wf-op-choice]').map(function (e) { return e.getAttribute('data-wf-op-choice'); }); },
    described() { return this.all('[data-wf-op-choice] small').filter(function (e) { return (e.textContent || '').trim().length > 0; }).length; },
    shelvesOf(question) {
      var box = document.querySelector('[data-wf-op-question="' + question + '"]');
      return box ? Array.prototype.slice.call(box.querySelectorAll('[data-wf-op-shelf]')).map(function (e) { return e.getAttribute('data-wf-op-shelf'); }) : [];
    },
    openQuestion(question) {
      var box = document.querySelector('[data-wf-op-question="' + question + '"]');
      var head = box && box.querySelector('.sem-wf-op-head');
      if (head && head.getAttribute('aria-expanded') === 'false') { head.click(); return true; }
      return !!head;
    },
    tick(op) { var b = document.querySelector('[data-wf-op-choice="' + op + '"]'); if (b) { b.click(); return true; } return false; },
    ticked() { return this.all('[data-wf-op-choice][aria-pressed="true"]').map(function (e) { return e.getAttribute('data-wf-op-choice'); }); },
    chips() { return this.all('[data-wf-op-chip]').map(function (e) { return e.getAttribute('data-wf-op-chip'); }); },
    removeChip(op) { var b = document.querySelector('[aria-label="Remove ' + op + '"]'); if (b) { b.click(); return true; } return false; },
    budget() { var e = document.querySelector('[data-wf-word-budget]'); return e ? { words: e.getAttribute('data-wf-word-budget'), text: (e.textContent || '').trim() } : null; },
    scrollTo(sel) { var e = document.querySelector(sel); if (e) { e.scrollIntoView({ block: 'center' }); return true; } return false; },
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
// THE ONE JOURNEY: choose actions for a step by browsing, not by knowing the op name.
// 1.1.3 had a question > shelf > action picker; 1.2.0 replaced it with a flat datalist. This drives
// the restored picker in the BUILT bundle: open it, filter it, tick two actions from two different
// shelves, see them as chips, see them reach the draft, and take one away again.
// =================================================================================================
{
  const J = 'choose actions';
  const page = await newPage();
  await open(page, '');
  await section(page, 'author');
  await click(page, '+ New playbook', true, 1100);
  await page.type('input.sem-wf-input', 'picker-demo', { delay: 12 });
  await wait(300);
  await click(page, 'Create draft', true, 2400);
  await click(page, 'Add the first step', true, 1400);

  // The soft word budget under Instructions: a count always, a nudge only when the step runs long.
  const setText = (page_, value) => page_.evaluate((text) => {
    const box = document.querySelector('[data-wf-step-form] textarea.sem-wf-input');
    if (!box) return false;
    const setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
    setter.call(box, text);
    box.dispatchEvent(new Event('input', { bubbles: true }));
    return true;
  }, value);
  record(J, 'the new step form has an instructions field', await q(page, () => !!document.querySelector('[data-wf-step-form] textarea.sem-wf-input')));
  const typedOk = await setText(page, 'Walk the tables and note what is missing.');
  await wait(1000); await refresh(page);
  const typed = await q(page, () => window.__d.budget());
  record(J, 'the instructions field shows a word count', typedOk && !!typed && Number(typed.words) === 8, JSON.stringify(typed));
  record(J, 'a short step gets no nudge', !!typed && !/read best/.test(typed.text), typed ? typed.text : 'none');
  const longOk = await setText(page, Array.from({ length: 160 }, () => 'word').join(' '));
  await wait(1000); await refresh(page);
  const nudged = await q(page, () => window.__d.budget());
  record(J, 'a long step is nudged, and only nudged', longOk && !!nudged && Number(nudged.words) === 160 && /read best under about 150/.test(nudged.text),
    nudged ? nudged.text : 'none');
  record(J, 'the nudge never disables the field', await q(page, () => !document.querySelector('[data-wf-step-form] textarea.sem-wf-input').disabled));
  await q(page, () => window.__d.scrollTo('[data-wf-step-form]'));
  await wait(300);
  await shot(page, 'step-word-budget');
  await setText(page, 'Walk the tables and note what is missing.');
  await wait(900); await refresh(page);

  record(J, 'the step form offers "Choose actions"', await q(page, () => window.__d.enabled('Choose actions', true)));
  record(J, 'the flat quick path is still there for anyone who knows the name',
    await q(page, () => !!document.querySelector('input[list="sem-wf-op-list"]')));

  await click(page, 'Choose actions', true, 1200);
  await q(page, () => window.__d.scrollTo('[data-wf-op-picker]'));
  await wait(400);
  const opened = await q(page, () => ({
    open: window.__d.picker(),
    asks: /What should this step be allowed to do\?/.test(window.__d.text()),
    questions: window.__d.questions(),
    shelves: window.__d.shelvesOf('Open a model and connect'),
    choices: window.__d.choices(),
    described: window.__d.described(),
  }));
  record(J, 'the picker panel opens', opened.open);
  record(J, 'it opens with the question, not a bare list', opened.asks);
  record(J, 'actions are grouped by question', opened.questions.length >= 5, JSON.stringify(opened.questions));
  record(J, 'a question holds its shelves', opened.shelves.includes('Connections and targets'), JSON.stringify(opened.shelves));
  record(J, 'every visible action carries its plain description', opened.described === opened.choices.length && opened.described > 0,
    `described=${opened.described} choices=${opened.choices.length}`);
  await shot(page, 'oppicker-open');

  await page.type('[data-wf-op-filter]', 'measure', { delay: 14 });
  await wait(700); await refresh(page);
  const filtered = await q(page, () => ({ choices: window.__d.choices(), questions: window.__d.questions() }));
  record(J, 'the filter narrows the list to matching actions', filtered.choices.length > 0 && filtered.choices.every((op) => /measure/i.test(op)),
    JSON.stringify(filtered.choices));
  record(J, 'the filter drops groups that have no match left', filtered.questions.length < opened.questions.length,
    `${opened.questions.length} -> ${filtered.questions.length}`);
  await shot(page, 'oppicker-filtered');

  await page.evaluate(() => {
    const box = document.querySelector('[data-wf-op-filter]');
    const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
    setter.call(box, '');
    box.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await wait(800); await refresh(page);
  const cleared = await q(page, () => ({ choices: window.__d.choices().length, questions: window.__d.questions().length }));
  record(J, 'clearing the filter brings the whole list back', cleared.questions === opened.questions.length, JSON.stringify(cleared));

  const firstPick = await q(page, () => window.__d.tick('connect_local'));
  await wait(900); await refresh(page);
  await q(page, () => window.__d.openQuestion('Make it better'));
  await wait(600); await refresh(page);
  const secondPick = await q(page, () => window.__d.tick('bpa_scan'));
  await wait(1100); await refresh(page);
  const picked = await q(page, () => ({
    ticked: window.__d.ticked(), chips: window.__d.chips(),
    shelfOfFirst: window.__d.shelvesOf('Open a model and connect'),
    shelfOfSecond: window.__d.shelvesOf('Make it better'),
  }));
  record(J, 'ticking works from two different shelves', firstPick && secondPick);
  record(J, 'the two actions came from two different shelves',
    picked.shelfOfFirst.includes('Connections and targets') && picked.shelfOfSecond.includes('Findings and fixes'),
    `${JSON.stringify(picked.shelfOfFirst)} / ${JSON.stringify(picked.shelfOfSecond)}`);
  record(J, 'both ticks show as ticked in the panel', picked.ticked.includes('connect_local') && picked.ticked.includes('bpa_scan'), JSON.stringify(picked.ticked));
  record(J, 'both chosen actions appear as chips on the step', picked.chips.includes('connect_local') && picked.chips.includes('bpa_scan'), JSON.stringify(picked.chips));
  await shot(page, 'oppicker-ticked');

  await click(page, 'Close the action list', true, 900);
  const closed = await q(page, () => ({ open: window.__d.picker(), chips: window.__d.chips() }));
  record(J, 'the panel closes and the chips stay', !closed.open && closed.chips.length === 2, JSON.stringify(closed.chips));
  await q(page, () => window.__d.scrollTo('.sem-wf-op-editor'));
  await wait(400);
  await shot(page, 'oppicker-chips');

  const removed = await q(page, () => window.__d.removeChip('connect_local'));
  await wait(1100); await refresh(page);
  const after = await q(page, () => window.__d.chips());
  record(J, 'a chip can be taken off again', removed && after.length === 1 && after[0] === 'bpa_scan', JSON.stringify(after));

  // The chips and Source are ONE draft. Ticking writes nothing on its own (no engine call per tick);
  // the accumulated draft operations go out when the preview is asked for, which is what opening Source
  // does. So the proof is the operations THIS call carries, plus the harness's echo of them in the text.
  // HONEST LIMIT: the mock's reply for an unsaved new playbook does not carry the steps back, so this
  // journey reads the request and stops there. Real emission is the engine's patcher (Semanticus.Tests).
  await view(page, 'Source');
  const sent = await q(page, () => {
    const calls = (window.__uishotHarness.messages || []).filter((m) => m.method === 'previewWorkflowEdit');
    const latest = calls[calls.length - 1];
    let operations = [];
    try { operations = JSON.parse((latest && latest.params && latest.params[3]) || '[]'); } catch (error) { operations = []; }
    const ops = operations.filter((o) => o && o.op === 'set_field' && o.field === 'ops');
    return { ops, last: ops.length ? ops[ops.length - 1].value : null, kinds: operations.map((o) => o.op) };
  });
  record(J, 'no engine call fires per tick; the draft carries the edits', sent.ops.length === 3, JSON.stringify(sent.kinds));
  record(J, 'the draft sent to the engine holds the remaining action', JSON.stringify(sent.last) === JSON.stringify(['bpa_scan']), JSON.stringify(sent.last));
  const source = await q(page, () => window.__d.source());
  record(J, 'Source is fed by that same draft', sourceOps(source).filter((op) => op === 'set_field').length === sent.kinds.filter((op) => op === 'set_field').length,
    `${sourceOps(source).join(',')} vs ${sent.kinds.join(',')}`);
  noErrors(page, J);
  await page.close();
}

const failed = checks.filter((c) => !c.ok);
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ checks, shots }, null, 2));
for (const c of checks) console.log(`${c.ok ? 'PASS' : 'FAIL'}  [${c.journey}] ${c.name}${c.ok ? '' : '  -- ' + c.detail}`);
console.log(`\n${checks.length - failed.length}/${checks.length} action-picker checks passed.`);
await browser.close(); server.close();
process.exit(failed.length ? 1 : 0);
