// Browser-driven proof of the repeat editor in a Plan row's detail (lane RP; contract section 1.7).
//
// U1 owns the Plan page and the row detail (plan-row.tsx); until its rewrite lands, the editor is mounted here on
// its own, inside a detail column as wide as the row detail's right half, on the REAL built stylesheet
// (media/studio/tokens.css and studio.css, built by `cd webview && npm run build`) and in the three VS Code themes.
// When U1 has merged and the patch in the RP lane report is applied, the same scenes belong in cp-plan-drive.mjs.
//
// A measuring instrument like the other drivers: each scene records the text a person reads and every call a click
// made, in results.json beside the PNGs. The fake engine answers the three person-only calls the way the real one
// does (a new repeat is saved with the engine's cadence sentence; pause flips held; stop removes it), so a click's
// effect on the face is measured, not assumed.
//
//   OUTDIR=<dir> TMPDIR=<dir> SEMANTICUS_BROWSER=/usr/bin/chromium node tools/uishot/rp-repeat-drive.mjs
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { THEMES, applyTheme } from './themes.mjs';
requireSupportedNode();

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'rp');
mkdirSync(OUT, { recursive: true });
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const esbuild = createRequire(join(webRoot, 'webview', 'package.json'))('esbuild');

for (const f of ['tokens.css', 'studio.css'])
  if (!existsSync(join(webRoot, 'media', 'studio', f))) { console.error(`media/studio/${f} is missing. Run: cd webview && npm run build`); process.exit(1); }
const CSS = ['tokens.css', 'studio.css'].map((f) => readFileSync(join(webRoot, 'media', 'studio', f), 'utf8')).join('\n');

// The scenes, as a tiny app: one editor per scene, each over its own fake plan, and every call written down.
const ENTRY = `
import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { RepeatEditor } from './plan-repeat';
import * as R from './plan-repeat-model';

const NOW = new Date(2026, 8, 25, 10);
const found = { id: 'safefix/Visibility', title: 'Hide the key columns', source: 'found', doer: 'semanticus', stateWord: 'ready' };
const typed = { id: 'pt-5a5b5c5d', title: 'Review Total Sales every week', source: 'words', doer: 'you', stateWord: 'needs_you' };
const card = { id: 'pt-7c01d4e9', title: 'Update model notes', source: 'repeat', doer: 'assistant', stateWord: 'ready' };
const notes = { id: 'rp-0a0b0c0d', words: 'Update model notes', doer: 'assistant', stock: 'model-notes',
  cadence: { kind: 'weekly', every: 2, day: 'friday' }, cadenceText: 'Every 2 weeks, on Friday',
  nextDueUtc: '2026-10-09T00:00:00Z', held: false, openItemId: card.id };
const SCENES = {
  offer: { row: found, repeats: [], isPro: true },
  task: { row: typed, repeats: [], isPro: true },
  resting: { row: typed, repeats: [{ id: 'rp-55556666', words: typed.title, doer: 'you', cadence: { kind: 'weekly', every: 1, day: 'monday' },
    cadenceText: 'Every week, on Monday', nextDueUtc: '2026-09-28T00:00:00Z', held: false, openItemId: null, fromItemId: typed.id,
    resting: 'Its task is set aside, so it does not come due. Put the task back, or stop repeating.' }], isPro: true },
  proline: { row: found, repeats: [], isPro: false },
  saved: { row: card, repeats: [notes], isPro: true },
  paused: { row: card, repeats: [{ ...notes, held: true }], isPro: true },
  fixidle: { row: found, repeats: [{ id: 'rp-11112222', words: found.title, fix: found.id, doer: 'semanticus',
    cadence: { kind: 'weekly', every: 1, day: 'monday' }, cadenceText: 'Every week, on Monday',
    nextDueUtc: '2026-09-28T00:00:00Z', held: false, openItemId: null }], isPro: true },
  savedfree: { row: card, repeats: [notes], isPro: false },
};
window.__calls = [];
function Scene({ name }) {
  const s = SCENES[name];
  const [repeats, setRepeats] = useState(s.repeats);
  const note = (call, args) => window.__calls.push({ scene: name, call, args });
  return <div className="rp-host" data-scene={name}>
    <RepeatEditor row={s.row} repeats={repeats} isPro={s.isPro} busy={null} now={NOW}
      onAdd={(req) => { note('addPlanRepeat', [req]); setRepeats([...repeats, { id: 'rp-33334444', words: s.row.title,
        fix: req.fix ?? null, doer: req.fix ? 'semanticus' : (req.doer ?? 'assistant'), cadence: req.cadence,
        cadenceText: R.cadenceText(req.cadence), nextDueUtc: '2026-10-02T00:00:00Z', held: false, openItemId: null,
        fromItemId: req.fromTask ?? null }]); return true; }}   // answers true like the Plan's controlOk: the form ends its words on the engine's OK
      onChange={(id, req) => { note('setPlanRepeat', [id, req]); setRepeats(repeats.map((r) => r.id !== id ? r
        : { ...r, ...(req.held !== undefined ? { held: req.held } : {}),
            ...(req.cadence ? { cadence: req.cadence, cadenceText: R.cadenceText(req.cadence) } : {}) })); return true; }}
      onDelete={(id) => { note('deletePlanRepeat', [id]); setRepeats(repeats.filter((r) => r.id !== id)); }} />
  </div>;
}
createRoot(document.getElementById('root')).render(<Scene name={window.__scene} />);
`;

const built = await esbuild.build({
  stdin: { contents: ENTRY, resolveDir: join(webRoot, 'webview', 'src'), loader: 'tsx', sourcefile: 'rp-entry.tsx' },
  bundle: true, write: false, format: 'iife', jsx: 'automatic', minify: false, logLevel: 'silent',
  nodePaths: [join(webRoot, 'webview', 'node_modules')], define: { 'process.env.NODE_ENV': '"production"' },
});
const JS = built.outputFiles[0].text;

// A page shaped like the row detail's right column: half a 960 px Studio, the width the detail gives the field.
const html = (scene) => `<!doctype html><html><head><meta charset="utf-8"><style>${CSS}
body { margin: 0; background: var(--sem-bg); color: var(--sem-fg); font-family: var(--sem-font-ui); font-size: var(--sem-text-body); }
.rp-frame { width: 440px; padding: var(--sem-space-3); }
</style></head><body><div class="rp-frame"><div id="root"></div></div>
<script>window.__scene = ${JSON.stringify(scene)};</script><script>${JS.replace(/<\/script/g, '<\\/script')}</script></body></html>`;

const browser = await puppeteer.launch({
  executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'],
});
const results = [];
const text = (page) => page.$eval('#root', (el) => el.innerText.replace(/\s+/g, ' ').trim());
const shot = async (page, name) => {
  const el = await page.$('.rp-frame');
  const file = join(OUT, `${name}.png`);
  await el.screenshot({ path: file });
  return file;
};

// [scene, clicks after load, what the scene is]
const PLAN = [
  ['offer', [], 'Pro, a row Semanticus does: the offer'],
  ['offer', ['plan-repeat-add'], 'Pro: Repeat this opens the cadence form'],
  ['offer', ['plan-repeat-add', 'kind:monthly', 'plan-repeat-save'], 'Pro: saved monthly, the row now shows its repeat'],
  ['task', ['plan-repeat-add', 'plan-repeat-save'], 'Pro, a typed task: Save shows the schedule and Stop repeating on the same row (UAT S12-6-003)'],
  ['resting', [], 'Pro, a task set aside: its repeat rests and says why, with Stop repeating'],
  ['proline', [], 'Free: the Pro line, no button'],
  ['saved', [], 'Pro, a repeat card: cadence, still open, Change, Pause, Stop repeating'],
  ['saved', ['plan-repeat-hold'], 'Pro: Pause flips to Resume'],
  ['saved', ['plan-repeat-delete'], 'Pro: Stop repeating removes the repeat, the row stays'],
  ['paused', [], 'Pro, paused'],
  ['fixidle', [], 'Pro, a found row whose fix repeats, not due yet'],
  ['savedfree', [], 'Free, a saved repeat: the Pro line and only Stop repeating'],
];

for (const theme of Object.keys(THEMES)) {
  for (const [scene, clicks, what] of PLAN) {
    const page = await browser.newPage();
    await page.setViewport({ width: 480, height: 300, deviceScaleFactor: 1 });
    await page.setContent(html(scene), { waitUntil: 'load' });
    await applyTheme(page, theme);
    for (const c of clicks) {
      if (c.startsWith('kind:')) await page.select('[data-testid="plan-repeat-kind"]', c.slice(5));
      else await page.click(`[data-testid="${c}"]`);
    }
    const name = `${theme}-${scene}${clicks.length ? '-' + clicks.map((c) => c.replace(/^plan-repeat-/, '').replace(':', '-')).join('-') : ''}`;
    const face = await page.$eval('[data-testid="plan-repeat"]', (el) => el.getAttribute('data-face')).catch(() => null);
    const overflow = await page.$eval('.rp-frame', (el) => [...el.querySelectorAll('*')].some((n) => n.scrollWidth > n.clientWidth + 1 && getComputedStyle(n).overflow !== 'visible'));
    results.push({ theme, scene, clicks, what, face, text: await text(page), calls: await page.evaluate(() => window.__calls), overflow, png: await shot(page, name) });
    await page.close();
  }
}
await browser.close();
writeFileSync(join(OUT, 'results.json'), JSON.stringify(results, null, 2));

// The measured facts this driver stands behind, checked here so a wrong face fails the run.
const fail = [];
const at = (scene, clicks) => results.filter((r) => r.scene === scene && r.clicks.join() === clicks.join());
for (const r of at('offer', [])) if (!/Repeat this/.test(r.text) || r.face !== 'offer') fail.push(`${r.theme} offer: ${r.text}`);
for (const r of at('offer', ['plan-repeat-add', 'kind:monthly', 'plan-repeat-save'])) {
  const call = r.calls.find((c) => c.call === 'addPlanRepeat');
  if (!call || call.args[0].fix !== 'safefix/Visibility' || call.args[0].cadence.kind !== 'monthly' || call.args[0].cadence.date !== 25)
    fail.push(`${r.theme} add call: ${JSON.stringify(r.calls)}`);
  if (!/Every month, on the 25th/.test(r.text)) fail.push(`${r.theme} after add: ${r.text}`);
}
for (const r of at('proline', [])) if (!/part of Semanticus Pro/.test(r.text) || /Repeat this/.test(r.text)) fail.push(`${r.theme} proline: ${r.text}`);
for (const r of at('saved', ['plan-repeat-hold'])) {
  if (!r.calls.some((c) => c.call === 'setPlanRepeat' && c.args[1].held === true)) fail.push(`${r.theme} hold call: ${JSON.stringify(r.calls)}`);
  if (!/Resume/.test(r.text) || !/^Repeats .*Paused\./.test(r.text)) fail.push(`${r.theme} after hold: ${r.text}`);
}
for (const r of at('saved', ['plan-repeat-delete'])) {
  if (!r.calls.some((c) => c.call === 'deletePlanRepeat' && c.args[0] === 'rp-0a0b0c0d')) fail.push(`${r.theme} delete call: ${JSON.stringify(r.calls)}`);
  if (r.face !== null) fail.push(`${r.theme} after delete still shows ${r.face}`);
}
for (const r of at('task', ['plan-repeat-add', 'plan-repeat-save'])) {
  const call = r.calls.find((c) => c.call === 'addPlanRepeat');
  if (!call || call.args[0].fromTask !== 'pt-5a5b5c5d') fail.push(`${r.theme} task add call: ${JSON.stringify(r.calls)}`);
  if (r.face !== 'saved' || !/Every week/.test(r.text) || !/Stop repeating/.test(r.text) || /Repeat this/.test(r.text))
    fail.push(`${r.theme} task after save: ${r.face} ${r.text}`);
}
for (const r of at('resting', [])) if (!/Its task is set aside/.test(r.text) || !/Stop repeating/.test(r.text)) fail.push(`${r.theme} resting: ${r.text}`);
for (const r of at('savedfree', [])) if (/Change|Pause/.test(r.text) || !/Stop repeating/.test(r.text)) fail.push(`${r.theme} savedfree: ${r.text}`);
for (const r of results) {
  if (r.overflow) fail.push(`${r.theme} ${r.scene}: something overflows`);
  if (r.text.includes('\u2014')) fail.push(`${r.theme} ${r.scene}: an em dash`);
}
console.log(`rp-repeat-drive: ${results.length} shots in ${OUT}`);
if (fail.length) { console.error(fail.join('\n')); process.exit(1); }
console.log('rp-repeat-drive: every measured face and call as expected');
