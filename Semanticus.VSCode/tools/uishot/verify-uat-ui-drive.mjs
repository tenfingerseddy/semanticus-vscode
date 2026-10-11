// VERIFY driver for m11/uat-ui (2 Oct): attacks the lane through the REAL Studio bundle (media/studio/studio.js) and the
// mock engine (harness.html), and records every call a click made, with its arguments.
//
//   policy   the incremental refresh policy form: the person changes one field, the assistant changes another field of
//            the same policy, Save policy is pressed. Lost work if the old field is written back without a word.
//   runone   Validation > a saved check > Run this one: the call must be runTests(persist, origin, only, sections).
//   insight  the Notes lesson editor, typed, the assistant rewords it and the page misses the live event: Save must not write.
//   insclean the Notes lesson editor, opened and left clean, the assistant rewords it: Save must write the assistant's words.
//   docdraft Docs narrative, typed, the assistant rewrites the section, the person switches away: nothing written, words offered.
//   docclean Docs narrative, typed, nothing moved, switched away: the words are saved on the way out.
//   docrace  Docs narrative, typed, the engine's section moved but the page was not told yet, switched away: the re-read
//            finds it, nothing written, words offered (round 3).
//   ctrls    Ctrl+S with a calculation group precedence typed and still focused: Studio commits it and answers after it lands.
//   ctrlsidle Ctrl+S with nothing focused: Studio answers at once and writes nothing.
//
//   OUTDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/verify-uat-ui-drive.mjs
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
requireSupportedNode();
import { createRequire } from 'node:module';
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'verify-uat-ui');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const p = normalize(join(webRoot, decodeURIComponent((req.url || '/').split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `verify-uat-ui-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' },
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const results = {};
const failures = [];
const must = (scene, ok, why) => { if (!ok) failures.push(`${scene}: ${why}`); };
const only = process.env.ONLY ? process.env.ONLY.split(',') : null;
const want = (s) => !only || only.includes(s);

async function open(tab, viewport = { width: 1200, height: 1000 }) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.setViewport({ ...viewport, deviceScaleFactor: 1 });
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?tab=${tab}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await wait(1500);
  return { page, errors };
}
const calls = (page, method) => page.evaluate((m) => window.__uishotHarness.messages.filter((x) => x && x.type === 'rpc' && x.method === m).map((x) => x.params), method);
const clickText = (page, text, within = 'body') => page.evaluate((t, w) => {
  const b = [...document.querySelectorAll(`${w} button`)].find((x) => (x.textContent || '').trim().startsWith(t) && !x.disabled);
  if (b) { b.scrollIntoView({ block: 'center' }); b.click(); }
  return !!b;
}, text, within);

// ---- policy / policyclean: the incremental refresh policy form ----------------------------------------------------------
// policy: the person changes "store", the assistant changes the refresh window of the same policy, Save policy is pressed.
// policyclean: nothing typed; the form must follow the assistant's change (this half already holds).
for (const scene of ['policy', 'policyclean']) {
  if (!want(scene)) continue;
  const { page, errors } = await open('mcode');
  // Every read answers a fresh copy, as the real host does (a message is cloned on its way in), so React sees a new document.
  await page.evaluate(() => {
    const h = window.__uishotHarness;
    const doc = h.response('getDocModel', [1]);
    h.setResponse('getDocModel', () => JSON.parse(JSON.stringify(doc)));
  });
  const before = await page.evaluate(() => window.__uishotHarness.response('getIncrementalRefreshPolicy', ['table:Sales']));
  results.policyBefore = before;
  must(scene, await clickText(page, 'Configure'), 'no Configure button on the Incremental refresh card');
  await wait(600);
  const boxes = () => page.evaluate(() => [...document.querySelectorAll('section[aria-label="Incremental refresh settings"] input[type=number]')].map((i) => i.value));
  const start = await boxes();
  if (scene === 'policy') {
    // The person changes "store" from 5 to 7 (React reads the value through the native setter and an input event).
    await page.evaluate(() => {
      const box = document.querySelector('section[aria-label="Incremental refresh settings"] input[type=number]');
      const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
      set.call(box, '7'); box.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await wait(300);
  }
  const typed = await boxes();
  // The assistant changes the refresh window of the same policy to 3 months, and the page hears the change.
  await page.evaluate((b) => {
    const h = window.__uishotHarness;
    h.setResponse('getIncrementalRefreshPolicy', Object.assign({}, b, { enabled: true, incrementalPeriods: 3, incrementalGranularity: 'Month' }));
    h.dispatch({ type: 'didChange', payload: { sessionId: 'uishot', revision: 99, origin: 'agent', label: 'set_incremental_refresh_policy', deltas: [{ kind: 'update', ref: 'table:Sales' }] } });
  }, before);
  await wait(1500);
  const after = await boxes();
  const banner = await page.evaluate(() => [...document.querySelectorAll('section[aria-label="Incremental refresh settings"] [role="alert"], section[aria-label="Incremental refresh settings"] .xe-msg')].map((a) => a.textContent.trim()).join(' | '));
  await page.evaluate(() => document.querySelector('section[aria-label="Incremental refresh settings"]')?.scrollIntoView({ block: 'start' }));
  await wait(200);
  await page.evaluate(() => document.querySelector('section[aria-label="Incremental refresh settings"] input[type=number]')?.scrollIntoView({ block: 'center' }));
  await wait(300);
  await page.screenshot({ path: join(OUT, `${scene}-before-save.png`) });
  must(scene, await clickText(page, 'Save policy'), 'no Save policy button');
  await wait(1500);
  const writes = await calls(page, 'setIncrementalRefreshPolicy');
  results[scene] = { start, typed, afterAssistant: after, banner, writes };
  if (writes.length) {
    const w = writes[writes.length - 1];   // (table, dateColumn, store, storeGran, refresh, refreshGran, ...)
    must(scene, w[4] === 3 && w[5] === 'Month', `Save policy wrote refresh ${w[4]} ${w[5]} over the assistant's 3 Month window; the form showed ${JSON.stringify(after)}; notice: "${banner}"`);
  }
  if (errors.length) failures.push(`${scene}: ` + errors.join(' | '));
  await page.close();
}

// ---- runone: Validation > a saved check > Run this one ---------------------------------------------------------------
if (want('runone')) {
  const { page, errors } = await open('tests');
  // The saved SQL check "Total Sales ties to the GL": open its row menu, then press Run this one.
  const opened = await page.evaluate(() => {
    const row = [...document.querySelectorAll('tr, [role="row"], div')].filter((r) => /^Total Sales ties to the GL/.test((r.textContent || '').trim()) && r.querySelector('button'))
      .sort((a, b) => a.textContent.length - b.textContent.length)[0];
    const menu = row && [...row.querySelectorAll('button')].pop();
    if (menu) menu.click();
    return !!menu;
  });
  must('runone', opened, 'no row menu on the saved SQL check');
  await wait(400);
  const hit = await page.evaluate(() => { const b = [...document.querySelectorAll('[role="menuitem"]')].find((x) => x.textContent.trim() === 'Run this one'); b?.click(); return !!b; });
  must('runone', hit, 'no Run this one button on the Validation page');
  await wait(1500);
  const runs = await calls(page, 'runTests');
  const alert = await page.evaluate(() => [...document.querySelectorAll('[role="alert"]')].map((a) => a.textContent.trim()).join(' | '));
  results.runone = { runs, alert };
  const last = runs[runs.length - 1];
  must('runone', !!last && typeof last[0] === 'boolean' && last[1] === 'human' && Array.isArray(last[2]) && last[2].length === 1,
    `runTests was sent as ${JSON.stringify(last)}; the engine's shape is (persist, origin, only, sections)`);
  await page.screenshot({ path: join(OUT, 'runone-after.png'), fullPage: false });
  if (errors.length) failures.push('runone: ' + errors.join(' | '));
  await page.close();
}

// ---- insight / insclean: the Notes lesson editor ------------------------------------------------------------------------
const lesson = (text) => ({
  insights: [{ id: 'ki-77b0e9a4', text, kind: 'insight', keys: ['FK-HIDDEN', 'naming'], status: 'approved', score: 4, uses: 1, retrievals: 3, scope: 'project', provenance: { origin: 'agent', when: '2026-10-02T07:00:00Z' } }],
  skippedCorruptLines: 0,
});
for (const scene of ['insight', 'insclean']) {
  if (!want(scene)) continue;
  const { page, errors } = await open('knowledge', { width: 1100, height: 900 });
  await page.waitForSelector('#knowledge-insights', { timeout: 20000 });
  await page.evaluate((l) => { window.__uishotHarness.setResponse('listInsights', l); window.__uishotHarness.dispatch({ type: 'activity', payload: { kind: 'insight', at: new Date().toISOString() } }); }, lesson('Hiding technical FK columns lifted Naming by 8 points.'));
  await wait(800);
  const pressIn = (label) => page.evaluate((t) => {
    const a = [...document.querySelectorAll('#knowledge-insights article')].find((x) => /Hiding technical FK|Assistant reworded/.test(x.textContent + (x.querySelector('textarea')?.value || '')));
    const b = [...(a?.querySelectorAll('button') ?? [])].find((x) => (x.textContent || '').trim() === t);
    if (b) b.click();
    return !!b;
  }, label);
  must(scene, await pressIn('Edit'), 'no Edit button');
  await wait(300);
  if (scene === 'insight') {
    await page.click('#knowledge-insights textarea[aria-label="Insight text"]');
    await page.keyboard.type(' My own sentence.');
    // the engine moves, and the page is NOT told (a missed live event)
    await page.evaluate((l) => window.__uishotHarness.setResponse('listInsights', l), lesson('Assistant reworded: hide key columns model-wide.'));
  } else {
    await page.evaluate((l) => { window.__uishotHarness.setResponse('listInsights', l); window.__uishotHarness.dispatch({ type: 'activity', payload: { kind: 'edit_insight', at: new Date().toISOString() } }); }, lesson('Assistant reworded: hide key columns model-wide.'));
    await wait(1000);
  }
  must(scene, await pressIn('Save'), 'no Save button');
  await wait(1000);
  const edits = await calls(page, 'editInsight');
  const seen = await page.evaluate(() => {
    const a = document.querySelector('#knowledge-insights article textarea')?.closest('article');
    return { banner: (a?.querySelector('[role="alert"]')?.textContent || '').trim(), typed: a?.querySelector('textarea')?.value || '' };
  });
  results[scene] = { edits, seen };
  if (scene === 'insight') {
    must(scene, edits.length === 0, `Save wrote ${JSON.stringify(edits)} over a lesson the assistant had reworded`);
    must(scene, /changed while you were editing/.test(seen.banner) && /My own sentence/.test(seen.typed), `no banner or the words were lost: ${JSON.stringify(seen)}`);
  } else {
    must(scene, edits.length === 1 && /Assistant reworded/.test(edits[0][1]), `a clean editor wrote ${JSON.stringify(edits)}`);
  }
  await page.screenshot({ path: join(OUT, `${scene}-after.png`) });
  if (errors.length) failures.push(`${scene}: ` + errors.join(' | '));
  await page.close();
}

// ---- docdraft / docclean: the Docs narrative section, switched away from while typed (round 2) --------------------------
// docdraft: the person types in Sales > businessContext, the assistant rewrites that section, the person switches to another
//           object. Nothing may be written, and the words must be offered when the section opens again.
// docclean: the person types, nothing moves, the person switches away: the words are saved on the way out (old behaviour).
for (const scene of ['docdraft', 'docclean', 'docrace']) {
  if (!want(scene)) continue;
  const { page, errors } = await open('docs', { width: 1400, height: 1100 });
  await page.evaluate(() => {
    const h = window.__uishotHarness;
    const doc = h.response('getDocModel', [50]);
    h.setResponse('getDocModel', () => JSON.parse(JSON.stringify(doc)));
    // Round 3: switching away re-reads the section itself (getDocSection) before it writes. The engine's copy follows the
    // getDocModel fixture unless a scene moves it on its own (docrace: the engine moved, the page was never told).
    window.__liveSection = {};
    h.setResponse('getDocSection', (p) => {
      const k = p[0] + '|' + p[1];
      if (Object.prototype.hasOwnProperty.call(window.__liveSection, k)) return window.__liveSection[k];
      const d = h.response('getDocModel', [50]);
      const o = (d.tables || []).find((x) => x.ref === p[0]);
      return o?.narrative?.sections?.find((x) => x.key === p[1])?.markdown ?? null;
    });
  });
  const pick = (ref) => page.evaluate((r) => {
    const sel = [...document.querySelectorAll('aside select')].find((s) => [...s.options].some((o) => o.value === r));
    if (!sel) return false;
    const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set;
    set.call(sel, r); sel.dispatchEvent(new Event('change', { bubbles: true }));
    return true;
  }, ref);
  must(scene, await pick('table:Sales'), 'no narrative object picker with table:Sales');
  await wait(600);
  const box = 'aside textarea';
  const grainBox = () => page.evaluate((b) => [...document.querySelectorAll(b)].findIndex((t) => /Grain is|Assistant rewrote|My own line/.test(t.value)), box);
  const idx = await grainBox();
  must(scene, idx >= 0, 'no businessContext section box for Sales');
  await page.evaluate((b, i) => { const t = document.querySelectorAll(b)[i]; t.focus(); t.setSelectionRange(t.value.length, t.value.length); }, box, idx);
  await page.keyboard.type(' My own line.');
  await wait(300);
  if (scene === 'docdraft') {
    await page.evaluate(() => {
      const h = window.__uishotHarness;
      const doc = h.response('getDocModel', [50]);
      const sales = doc.tables.find((t) => t.ref === 'table:Sales');
      sales.narrative = { author: 'agent', updatedUtc: '2026-10-02', sections: [{ key: 'businessContext', markdown: 'Assistant rewrote: one row per order line, shipped.' }] };
      h.setResponse('getDocModel', () => JSON.parse(JSON.stringify(doc)));
      h.dispatch({ type: 'didChange', payload: { sessionId: 'uishot', revision: 120, origin: 'agent', label: 'set_doc_section', deltas: [{ kind: 'update', ref: 'table:Sales' }] } });
    });
    await wait(1500);
  }
  if (scene === 'docrace') {
    // The assistant rewrote the section in the engine, and the page has not heard yet (inside its reload debounce).
    await page.evaluate(() => { window.__liveSection['table:Sales|businessContext'] = 'Assistant rewrote: one row per order line, shipped.'; });
  }
  const movedBanner = await page.evaluate(() => [...document.querySelectorAll('aside [role="alert"], aside .xe-msg')].map((a) => a.textContent.trim()).join(' | '));
  if (scene === 'docdraft') await page.screenshot({ path: join(OUT, 'docdraft-moved.png') });
  must(scene, await pick('model'), 'could not switch to the model');
  await wait(800);
  const writesAway = await calls(page, 'setDocSection');
  must(scene, await pick('table:Sales'), 'could not switch back to Sales');
  await wait(800);
  const back = await page.evaluate((b) => ({
    alert: [...document.querySelectorAll('aside [role="alert"]')].map((a) => a.textContent.trim()).join(' | '),
    boxes: [...document.querySelectorAll(b)].map((t) => t.value).filter(Boolean),
  }), box);
  let restored = null;
  if (scene === 'docdraft') {
    await page.screenshot({ path: join(OUT, 'docdraft-reopened.png') });
    must(scene, writesAway.length === 0, `switching away wrote ${JSON.stringify(writesAway)} over the section the assistant rewrote`);
    must(scene, /Your words were kept/.test(back.alert), `no kept-words notice on reopen: ${JSON.stringify(back)}`);
    must(scene, back.boxes.some((v) => /^Assistant rewrote/.test(v)), `the box does not show the assistant's section on reopen: ${JSON.stringify(back.boxes)}`);
    must(scene, await clickText(page, 'Use my words', 'aside'), 'no Use my words button');
    await wait(400);
    restored = await page.evaluate((b) => [...document.querySelectorAll(b)].map((t) => t.value).find((v) => /My own line/.test(v)) ?? null, box);
    must(scene, !!restored && /Grain is/.test(restored), `Use my words did not put the typed words back: ${restored}`);
    must(scene, (await calls(page, 'setDocSection')).length === 0, 'Use my words wrote something before Save');
    await page.screenshot({ path: join(OUT, 'docdraft-usemine.png') });
  } else if (scene === 'docrace') {
    must(scene, writesAway.length === 0, `switching away wrote ${JSON.stringify(writesAway)} over a section the engine had moved but the page had not heard of`);
    must(scene, (await calls(page, 'getDocSection')).length >= 1, 'switching away did not re-read the section');
    must(scene, /Your words were kept/.test(back.alert), `no kept-words notice on reopen after the race: ${JSON.stringify(back)}`);
    await page.screenshot({ path: join(OUT, 'docrace-reopened.png') });
  } else {
    must(scene, writesAway.length === 1 && writesAway[0][0] === 'table:Sales' && /My own line/.test(writesAway[0][2]), `a section that did not move was not saved on the way out: ${JSON.stringify(writesAway)}`);
  }
  results[scene] = { movedBanner, writesAway, back, restored };
  if (errors.length) failures.push(`${scene}: ` + errors.join(' | '));
  await page.close();
}

// ---- ctrls / ctrlsidle: Ctrl+S from the host while a Studio field commits on blur (round 2) ----------------------------
// ctrls: the person types a new calculation group precedence and, without leaving the box, presses Ctrl+S. The host sends
//        commitFocused; Studio must blur the box, send the write, and answer focusedCommitted only once that write has landed.
// ctrlsidle: nothing focused: Studio answers at once and writes nothing.
for (const scene of ['ctrls', 'ctrlsidle']) {
  if (!want(scene)) continue;
  const { page, errors } = await open('advmodels', { width: 1300, height: 1000 });
  const findPrec = () => page.evaluate(() => {
    const i = [...document.querySelectorAll('label input')].find((x) => /precedence/i.test(x.closest('label')?.textContent || ''));
    if (i) i.scrollIntoView({ block: 'center' });
    return !!i;
  });
  if (!(await findPrec())) { await clickText(page, 'Calculation groups'); await wait(800); }
  must(scene, await findPrec(), 'no calculation group precedence box');
  if (scene === 'ctrls') {
    await page.evaluate(() => {
      const i = [...document.querySelectorAll('label input')].find((x) => /precedence/i.test(x.closest('label')?.textContent || ''));
      i.focus(); i.select();
    });
    await page.keyboard.type('7');
    await page.evaluate(() => window.__uishotHarness.hold('setCalcGroupPrecedence'));
  }
  const n0 = await page.evaluate(() => window.__uishotHarness.messages.length);
  await page.evaluate(() => window.__uishotHarness.dispatch({ type: 'commitFocused', id: 41 }));
  await wait(500);
  const mid = await page.evaluate((n) => window.__uishotHarness.messages.slice(n).map((m) => m.type === 'rpc' ? 'rpc:' + m.method + ':' + JSON.stringify(m.params) : m.type + (m.id != null ? ':' + m.id : '')), n0);
  let after = mid;
  if (scene === 'ctrls') {
    must(scene, mid.some((m) => /^rpc:setCalcGroupPrecedence:.*7/.test(m)), `the typed precedence was not sent when the host asked for a commit: ${JSON.stringify(mid)}`);
    must(scene, !mid.includes('focusedCommitted:41'), `Studio answered focusedCommitted before its write landed: ${JSON.stringify(mid)}`);
    await page.evaluate(() => window.__uishotHarness.release('setCalcGroupPrecedence'));
    await wait(500);
    after = await page.evaluate((n) => window.__uishotHarness.messages.slice(n).map((m) => m.type === 'rpc' ? 'rpc:' + m.method + ':' + JSON.stringify(m.params) : m.type + (m.id != null ? ':' + m.id : '')), n0);
    must(scene, after.includes('focusedCommitted:41'), `Studio never answered focusedCommitted after the write landed: ${JSON.stringify(after)}`);
    const iw = after.findIndex((m) => /^rpc:setCalcGroupPrecedence/.test(m)), ia = after.indexOf('focusedCommitted:41');
    must(scene, iw >= 0 && ia > iw, `the answer came before the write: ${JSON.stringify(after)}`);
  } else {
    must(scene, mid.includes('focusedCommitted:41'), `with nothing focused Studio did not answer at once: ${JSON.stringify(mid)}`);
    must(scene, !mid.some((m) => m.startsWith('rpc:set')), `with nothing focused Studio wrote something: ${JSON.stringify(mid)}`);
  }
  await page.screenshot({ path: join(OUT, `${scene}-after.png`) });
  results[scene] = { mid, after };
  if (errors.length) failures.push(`${scene}: ` + errors.join(' | '));
  await page.close();
}

await browser.close();
server.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify({ results, failures }, null, 2));
console.log(JSON.stringify(results, null, 1));
if (failures.length) { console.error('FAILURES:\n  ' + failures.join('\n  ')); process.exit(1); }
console.log('every scene held');
