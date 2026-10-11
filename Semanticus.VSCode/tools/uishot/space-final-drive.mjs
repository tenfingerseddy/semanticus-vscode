// Proofs for m11/space-rule round 5, beside the verifier's space-r4v-drive.mjs (its cases A and B are the proofs for
// the first two faults of round 4 and are not repeated here).
//
// A. Backspace in a long text lays out no hidden twin. Round 4 measured the whole text on a twin for every key that
//    was not an insertion, so each Backspace into a 50,000-character Docs narrative took 17 to 21 ms in the harness
//    and 39 to 43 ms in a real VS Code window, against round 2's 3 and 13 ms (the verifier's N3, R6 half fixed). A
//    timing assertion on a shared machine is a coin toss, so this counts the twins, which is what the time was spent
//    on, and prints the times for the report. Run in both zoom models, for the Studio's box and the Properties one.
// B. Once the typing pauses, a box that Backspace left too tall is measured again and gives the slack back: its height
//    is its text's, with no line to spare and none hidden. Proved on a last line that wraps by a few words, which the
//    keys then take away. A cut of a selection is measured at once, with no pause.
// C. Plan's Next list grows into the page and never past it, at every Studio text size, counted again on each change
//    (100, 90, 80, 90, 100, 110, 120%) and when the page is opened at that size, in both zoom models. The fit took off
//    <main>'s bottom padding and not the Plan page's own (32 px), so in a real VS Code window at 2000 x 1200 and 90% it
//    grew Next by one row too many and the page scrolled 15 px, opened fresh or re-counted alike; its comment said it
//    could never push the page long. Checked by trial: a row's height added under the list must not fit while
//    Show all still hides rows, and the page must not scroll while Next shows more than its floor of 7.
//
// Serves THIS worktree's built bundle (cd webview && npm run build first). Exit 1 when any case fails.
//   node tools/uishot/space-final-drive.mjs            (CASES=B for one case)
import { createRequire } from 'node:module';
import { planSeed, start, wait } from './visual-scenes.mjs';
import { findBrowser } from './browser.mjs';

const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const { server, browser: harness, base } = await start();
const vscode = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser({ quiet: true }), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', '--disable-blink-features=StandardizedBrowserZoom'],
});
const MODELS = [['harness', harness], ['vscode', vscode]];
const want = (c) => !process.env.CASES || process.env.CASES.includes(c);
const failures = [];
const report = (ok, label, detail) => { console.log(`${ok ? 'PASS' : 'FAIL'}  ${label}: ${detail}`); if (!ok) failures.push(`${label}: ${detail}`); };
const DOCS = `${base}?tier=pro&conn=1&live=1&tab=docs`;
const PROPS = base.replace('harness.html', 'propgrid.html?s=measure&desc=long');

async function open(b, url, size) {
  const page = await b.newPage();
  await page.setViewport({ width: size[0], height: size[1], deviceScaleFactor: 1 });
  await page.goto(url, { waitUntil: 'networkidle0', timeout: 60000 });
  await wait(2600);
  return page;
}

// In the page: the box under test, a twin counter, and the two checks every case makes.
function probe(kind) {
  const ta = kind === 'docs'
    ? [...document.querySelectorAll('textarea[placeholder^="Markdown"]')].find((t) => t.getClientRects().length)
    : [...document.querySelectorAll('textarea')].filter((t) => t.getClientRects().length).sort((a, b) => b.value.length - a.value.length)[0];
  if (!ta) return false;
  window.__ta = ta;
  window.__twins = 0;
  new MutationObserver((recs) => { for (const r of recs) for (const n of r.addedNodes) if (n.nodeName === 'TEXTAREA' && n.getAttribute('aria-hidden') === 'true') window.__twins++; })
    .observe(document.body, { childList: true });
  // Hidden: how much text the box hides below its cap (at its cap it scrolls, which is what the cap is for). Slack:
  // whether a line less would still hold it all (a whole line to spare).
  window.__hidden = () => {
    const cap = parseFloat(getComputedStyle(ta).maxHeight);
    return Number.isFinite(cap) && ta.offsetHeight >= cap - 1 ? 0 : ta.scrollHeight - ta.clientHeight;
  };
  window.__slack = () => {
    const cs = getComputedStyle(ta);
    const line = parseFloat(cs.lineHeight);
    const floor = ta.rows * line + parseFloat(cs.paddingTop) + parseFloat(cs.paddingBottom) + parseFloat(cs.borderTopWidth) + parseFloat(cs.borderBottomWidth);
    if (ta.offsetHeight <= floor + 1) return false;
    const was = ta.style.height;
    ta.style.height = `${parseFloat(cs.height) - line}px`;
    const holds = ta.scrollHeight <= ta.clientHeight;
    ta.style.height = was;
    return holds;
  };
  window.__type = (s) => { ta.focus(); document.execCommand('insertText', false, s); };
  return true;
}

// A. Twenty Backspaces at the end and twenty in the middle of a 50,000-character text, a frame apart.
async function backspaces(page) {
  return page.evaluate(async () => {
    const ta = window.__ta;
    const run = async (at) => {
      const keys = [];
      for (let i = 0; i < 20; i++) {
        const pos = at === 'end' ? ta.value.length : Math.floor(ta.value.length / 2);
        ta.setSelectionRange(pos, pos);
        window.__twins = 0;
        const t0 = performance.now();
        document.execCommand('delete');
        const ms = performance.now() - t0;
        await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 10)));
        keys.push({ ms, twins: window.__twins, hidden: window.__hidden() });
      }
      const ms = keys.map((k) => k.ms).sort((a, b) => a - b);
      return { twins: keys.reduce((s, k) => s + k.twins, 0), hidden: Math.max(...keys.map((k) => k.hidden)), median: Math.round(ms[10] * 10) / 10, max: Math.round(ms[19] * 10) / 10 };
    };
    const end = await run('end');
    await new Promise((r) => setTimeout(r, 700));
    const mid = await run('middle');
    return { chars: ta.value.length, end, mid };
  });
}

if (want('A')) for (const [model, b] of MODELS) for (const kind of ['docs', 'props']) {
  const tag = `A ${model} model, ${kind === 'docs' ? 'a Docs narrative' : 'a Properties description'} of 50,000 characters`;
  const page = await open(b, kind === 'docs' ? DOCS : PROPS, kind === 'docs' ? [1440, 900] : [420, 800]);
  try {
    if (!await page.evaluate(probe, kind)) throw new Error(`${tag}: no text box on the page, so nothing was measured`);
    await page.evaluate(() => {
      const line = 'Revenue is recognised at ship date and returns are netted off in the month they are processed. ';
      let v = ''; while (v.length < 50000) v += line + (v.length % 7 === 0 ? '\n' : '');
      window.__ta.focus(); window.__ta.select(); document.execCommand('insertText', false, v.slice(0, 50000));
    });
    await wait(1200);
    const m = await backspaces(page);
    if (m.chars < 49000) throw new Error(`${tag}: the box holds ${m.chars} characters, so the long text never went in`);
    report(m.end.twins === 0 && m.mid.twins === 0, `${tag}: Backspace lays out no twin`,
      `${m.end.twins} twins over 20 keys at the end, ${m.mid.twins} over 20 in the middle; median ${m.end.median} and ${m.mid.median} ms a key, max ${m.end.max} and ${m.mid.max} (printed, not asserted)`);
    report(m.end.hidden <= 0 && m.mid.hidden <= 0, `${tag}: no Backspace leaves text hidden`,
      `at most ${Math.max(m.end.hidden, m.mid.hidden)} px hidden after a key`);
  } finally { await page.close(); }
}

// B. A wrapped last line taken away by Backspace, then a cut.
if (want('B')) for (const [model, b] of MODELS) for (const kind of ['docs', 'props']) {
  const tag = `B ${model} model, ${kind === 'docs' ? 'a Docs narrative' : 'a Properties description'}`;
  const page = await open(b, kind === 'docs' ? DOCS : PROPS, kind === 'docs' ? [1440, 900] : [420, 800]);
  try {
    if (!await page.evaluate(probe, kind)) throw new Error(`${tag}: no text box on the page, so nothing was measured`);
    const m = await page.evaluate(async () => {
      const ta = window.__ta;
      const settle = () => new Promise((r) => requestAnimationFrame(() => setTimeout(r, 10)));
      ta.focus(); ta.select();   // over whatever the box held, so it starts well under its cap
      window.__type(Array.from({ length: 8 }, (_, i) => `Line ${i + 1}: returns are netted off in the month they are processed.`).join('\n') + '\n');
      await new Promise((r) => setTimeout(r, 700));
      // Words until the last line wraps onto a new one, then two more: about a dozen Backspaces take that line away.
      const h0 = ta.offsetHeight;
      let words = 0;
      while (ta.offsetHeight === h0 && words < 400) { window.__type('word '); words++; await settle(); }
      if (ta.offsetHeight === h0) return null;
      window.__type('ab '); await settle();
      const grown = ta.offsetHeight;
      let keys = 0;
      window.__twins = 0;
      while (keys < 16) { ta.setSelectionRange(ta.value.length, ta.value.length); document.execCommand('delete'); keys++; await settle(); }
      const during = { twins: window.__twins, hidden: window.__hidden(), h: ta.offsetHeight };
      await new Promise((r) => setTimeout(r, 700));
      const after = { twins: window.__twins, hidden: window.__hidden(), slack: window.__slack(), h: ta.offsetHeight };
      // A cut: half the text selected and deleted in one go, measured on the next frame with no pause.
      ta.setSelectionRange(Math.floor(ta.value.length / 3), ta.value.length);
      document.execCommand('delete');
      await settle();
      const cut = { hidden: window.__hidden(), slack: window.__slack(), h: ta.offsetHeight };
      return { h0, grown, keys, during, after, cut };
    });
    if (!m) throw new Error(`${tag}: the last line never wrapped, so no Backspace could take a line away`);
    if (m.grown <= m.h0) throw new Error(`${tag}: the box did not grow for the wrapped line, so the case would prove nothing`);
    report(m.after.hidden <= 0 && !m.after.slack, `${tag}: after a pause the box is its text's height again`,
      `${m.keys} Backspaces took away a wrapped line: ${m.grown} px while it wrapped, ${m.during.h} px during the keys (${m.during.hidden} px hidden), ${m.after.h} px after a pause, `
        + `${m.after.hidden > 0 ? `${m.after.hidden} px hidden` : m.after.slack ? 'a whole line to spare' : 'nothing hidden and no line to spare'} (${m.h0} px before the line wrapped)`);
    report(m.cut.hidden <= 0 && !m.cut.slack, `${tag}: a cut is measured at once`,
      `${m.cut.h} px on the next frame, ${m.cut.hidden > 0 ? `${m.cut.hidden} px hidden` : m.cut.slack ? 'a whole line to spare' : 'nothing hidden and no line to spare'}`);
  } finally { await page.close(); }
}

// C. Plan's Next at every text size, both models.
const zoomTo = async (page, target) => {
  for (let i = 0; i < 8; i++) {
    const z = await page.evaluate(() => parseInt(document.querySelector('.studio-root')?.style.zoom || '100', 10));
    if (z === target) return;
    await page.evaluate((dy) => window.dispatchEvent(new WheelEvent('wheel', { deltaY: dy, ctrlKey: true, cancelable: true })), z < target ? -100 : 100);
    await wait(450);
  }
  throw new Error(`the Studio text size never reached ${target}%`);
};
const readPlan = (page) => page.evaluate(() => {
  const main = document.querySelector('main');
  const list = document.querySelector('[data-testid="plan-list"][data-primary]');
  if (!main || !list) return null;
  const isRow = (r) => r.getClientRects().length && !/^plan-row-(open|page|more)/.test(r.dataset.testid);
  const rows = [...list.querySelectorAll('div[data-testid^="plan-row-"]')].filter(isRow);
  const needs = [...list.querySelectorAll('[data-testid="plan-needs"] div[data-testid^="plan-row-"]')].filter(isRow).length;
  const more = document.querySelector('[data-testid="plan-show-all"]');
  const scrolls = main.scrollHeight > main.clientHeight + 1;
  // One more row's worth of height under the list: does the page then scroll?
  let fitsOneMore = false;
  if (!scrolls && rows.length) {
    const pad = document.createElement('div');
    pad.style.height = `${rows[rows.length - 1].offsetHeight}px`;
    list.appendChild(pad);
    fitsOneMore = main.scrollHeight <= main.clientHeight + 1;
    pad.remove();
  }
  return { next: rows.length - needs, more: more ? more.textContent.trim() : null, scrolls, fitsOneMore, over: main.scrollHeight - main.clientHeight };
});
if (want('C')) for (const [model, b] of MODELS) for (const size of [[1652, 1076], [1092, 876]]) {
  const page = await b.newPage();
  try {
    await page.setViewport({ width: size[0], height: size[1], deviceScaleFactor: 1 });
    await page.evaluateOnNewDocument(planSeed());
    await page.goto(`${base}?tier=pro&conn=1&live=1&tab=plan`, { waitUntil: 'networkidle0', timeout: 60000 });
    await wait(2600);
    let grew = 0;
    for (const zoom of [100, 90, 80, 90, 100, 110, 120]) for (const how of ['counted again', 'opened']) {
      const tag = `C ${model} model, Plan, a Studio ${size[0]}x${size[1]} at ${zoom}%, ${how}`;
      await zoomTo(page, zoom);
      await wait(900);
      if (how === 'opened') {
        const tab = (name) => page.evaluate((n) => { const t = [...document.querySelectorAll('button')].find((x) => x.textContent.trim() === n && x.getClientRects().length); if (t) t.click(); return !!t; }, name);
        if (!await tab('Explore')) throw new Error(`${tag}: no Explore tab to leave Plan by`);
        await wait(900);
        if (!await tab('Plan')) throw new Error(`${tag}: no Plan tab to come back by`);
        await wait(1600);
      }
      const m = await readPlan(page);
      if (!m) throw new Error(`${tag}: no Plan list on the page, so nothing was measured`);
      if (m.next > 7) grew++;
      const bad = (m.scrolls && m.next > 7) ? `the page scrolls ${m.over} px with ${m.next} Next rows, past the floor of 7`
        : (m.more && m.fitsOneMore) ? `another row fits under the list and ${m.more} still hides rows` : null;
      report(!bad, `${tag}: Next fills the page and never pushes it long`, bad || `${m.next} Next rows${m.more ? `, "${m.more}"` : ', all of them'}; the page ${m.scrolls ? `scrolls ${m.over} px (Next at its floor)` : 'does not scroll'}`);
    }
    if (!grew) throw new Error(`C ${model} model, ${size[0]}x${size[1]}: Next never grew past its floor at any size, so the case proved nothing`);
  } finally { await page.close(); }
}

await harness.close();
await vscode.close();
server.close();
if (failures.length) { console.error(`\n${failures.length} failed:\n  ${failures.join('\n  ')}`); process.exit(1); }
console.log('\nall passed');
