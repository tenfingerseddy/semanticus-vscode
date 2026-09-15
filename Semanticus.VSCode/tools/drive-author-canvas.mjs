// Author canvas layout driver: measures the Workflows > Author canvas in the BUILT bundle and shoots it.
//
// Same rig as tools/drive-authoring.mjs: serve the built Semanticus.VSCode tree over http, open
// tools/uishot/harness.html in headless Chromium, drive it with real clicks, then measure the live DOM.
//
// HONEST LIMIT: there is no .NET engine here, so every RPC is answered by the harness's in-page mocks
// (tools/uishot/harness.html). This proves the webview LAYOUT, not the engine's markdown patcher.
//
// Usage: OUTDIR=<dir> node tools/drive-author-canvas.mjs   (browser: SEMANTICUS_BROWSER, else chromium)
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..');
const require_ = createRequire(resolve(here, 'uishot/shot.mjs'));
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');
const OUT = process.env.OUTDIR || resolve(webRoot, '..', 'shots-author-canvas');
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
const out = { viewports: {}, shots: [], notes: [] };

const MEASURE = `
  window.__m = {
    box(sel) {
      var e = document.querySelector(sel);
      if (!e) return null;
      var r = e.getBoundingClientRect();
      return { w: Math.round(r.width), h: Math.round(r.height), top: Math.round(r.top), left: Math.round(r.left) };
    },
    text(sel) { var e = document.querySelector(sel); return e ? (e.textContent || '').trim() : null; },
    all(sel) { return Array.prototype.slice.call(document.querySelectorAll(sel)); },
    btn(t, exact) {
      return this.all('button').filter(function (b) {
        if (!b.getClientRects().length) return false;
        var s = (b.textContent || '').trim();
        return exact ? s === t : s.indexOf(t) >= 0;
      })[0] || null;
    },
    click(t, exact) { var b = this.btn(t, exact); if (b && !b.disabled) { b.click(); return true; } return false; },
    // The canvas host is whatever carries data-wf-canvas, before and after the change.
    canvas() { return this.box('[data-wf-canvas]'); },
    // Editor width: the drawer after the change, the right column of the old grid before it.
    editor() { return this.box('.sem-wf-drawer') || this.box('.sem-wf-canvas-layout > div:last-child'); },
    header() { return this.box('.sem-wf-author-head') || this.box('.sem-wf-editor-bar'); },
    tools() { return this.all('.sem-wf-canvas-tools button').map(function (b) { return (b.textContent || '').trim(); }); },
    counts() { return this.text('[data-wf-canvas-counts]'); },
    clickStep(n) {
      var e = document.querySelector('[data-wf-canvas-step="' + (n - 1) + '"]');
      if (!e) return false;
      var r = e.getBoundingClientRect();
      var ev = function (type) { return new MouseEvent(type, { bubbles: true, cancelable: true, clientX: r.left + r.width / 2, clientY: r.top + r.height / 2 }); };
      e.dispatchEvent(ev('mousedown')); e.dispatchEvent(ev('mouseup')); e.dispatchEvent(ev('click'));
      return true;
    },
    fullscreenAttr() { return document.body.getAttribute('data-studio-fullscreen'); },
    visible(sel) { var e = document.querySelector(sel); return !!e && e.getClientRects().length > 0; },
  };
`;

const newPage = async (width, height) => {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width, height, deviceScaleFactor: 2 });
  page.__errors = errors;
  return page;
};
const refresh = (page) => page.evaluate(MEASURE);
const shot = async (page, name) => {
  const file = join(OUT, `${name}.png`);
  await page.screenshot({ path: file });
  out.shots.push(file);
  return file;
};

// Workflows tab -> Library -> new-measure -> Edit workflow -> Canvas.
const openAuthorCanvas = async (page) => {
  await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.waitForSelector('nav button', { timeout: 15000 });
  await wait(1000);
  await page.evaluate(() => { const b = [...document.querySelectorAll('nav button')].find((x) => (x.textContent || '').trim() === 'Workflows'); if (b) b.click(); });
  await wait(700);
  await page.evaluate(() => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === 'Workflows' && x.getClientRects().length); if (b) b.click(); });
  await wait(2200);
  await page.evaluate(() => { const b = document.querySelector('[data-section="library"]'); if (b) b.click(); });
  await wait(1400);
  await page.evaluate(() => { const b = document.querySelector('[data-workflow="new-measure"] button'); if (b) b.click(); });
  await wait(1600);
  await page.evaluate(() => { const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === 'Edit workflow'); if (b) b.click(); });
  await wait(2800);
  await page.evaluate(() => { const b = [...document.querySelectorAll('[role="tab"]')].find((x) => (x.textContent || '').trim() === 'Canvas'); if (b) b.click(); });
  await wait(2200);
  await refresh(page);
};

const scene = async (width, height, label) => {
  const page = await newPage(width, height);
  await openAuthorCanvas(page);
  const nothing = await page.evaluate(() => ({
    canvas: window.__m.canvas(), editor: window.__m.editor(), header: window.__m.header(),
    tools: window.__m.tools(), counts: window.__m.counts(),
    hintRow: /Select a step to edit it\. Steps run in this order\./.test(document.body.innerText),
  }));
  await shot(page, `${label}-nothing-selected`);
  const picked = await page.evaluate(() => { const ok = window.__m.clickStep(3); return ok; });
  await wait(1400); await refresh(page);
  const selected = await page.evaluate(() => ({
    canvas: window.__m.canvas(), editor: window.__m.editor(),
    drawerHead: window.__m.text('.sem-wf-drawer-head'),
    overlay: (() => { const d = document.querySelector('.sem-wf-drawer'); return d ? getComputedStyle(d).position : null; })(),
  }));
  await shot(page, `${label}-step-3-selected`);
  let full = null;
  if (await page.evaluate(() => window.__m.click('Full screen', true))) {
    await wait(1200); await refresh(page);
    full = await page.evaluate(() => ({
      canvas: window.__m.canvas(), attr: window.__m.fullscreenAttr(),
      chrome: window.__m.visible('.studio-chrome'), arearow: window.__m.visible('.studio-arearow'),
      contextbar: window.__m.visible('.studio-contextbar'), rail: window.__m.visible('aside:has(button[data-section])'),
      authorHead: window.__m.visible('.sem-wf-author-head'),
    }));
    await shot(page, `${label}-full-screen`);
    await page.keyboard.press('Escape');
    await wait(800); await refresh(page);
    full.afterEscape = await page.evaluate(() => window.__m.fullscreenAttr());
  }
  out.viewports[label] = { width, height, picked, nothing, selected, full, errors: page.__errors.slice(0, 5) };
  await page.close();
};

// The keyboard and pin paths, driven for real at the laptop size.
const behaviour = async () => {
  const page = await newPage(1366, 768);
  await openAuthorCanvas(page);
  const checks = {};
  // Shift+F on the canvas turns full screen on, Escape turns it off.
  await page.evaluate(() => document.querySelector('[data-wf-canvas]').focus());
  await page.keyboard.down('Shift'); await page.keyboard.press('KeyF'); await page.keyboard.up('Shift');
  await wait(900);
  checks.shiftFEnters = await page.evaluate(() => document.body.getAttribute('data-studio-fullscreen'));
  await page.keyboard.press('Escape'); await wait(700);
  checks.escapeLeaves = await page.evaluate(() => document.body.getAttribute('data-studio-fullscreen'));
  await refresh(page);
  // Pick step 2, then Escape from inside the drawer closes it.
  await page.evaluate(() => window.__m.clickStep(2));
  await wait(1200); await refresh(page);
  checks.drawerAfterPick = await page.evaluate(() => !!document.querySelector('.sem-wf-drawer'));
  // Focus an ENABLED control in the drawer. On a read-only built-in only Pin and Close are enabled, so
  // picking the first button would focus a disabled one and the key would go to the body instead.
  checks.focusedInDrawer = await page.evaluate(() => {
    const live = [...document.querySelectorAll('.sem-wf-drawer button, .sem-wf-drawer input')].filter((b) => !b.disabled);
    if (!live.length) return null;
    live[0].focus();
    return document.activeElement === live[0] ? (live[0].getAttribute('aria-label') || live[0].tagName) : null;
  });
  await page.keyboard.press('Escape'); await wait(700);
  checks.drawerAfterEscape = await page.evaluate(() => !!document.querySelector('.sem-wf-drawer'));
  // Pin holds the drawer open when the selection is cleared.
  await page.evaluate(() => window.__m.clickStep(2));
  await wait(1200); await refresh(page);
  await page.evaluate(() => {
    const pin = [...document.querySelectorAll('.sem-wf-drawer-actions button')].find((b) => /Keep this open|Unpin/.test(b.getAttribute('aria-label') || ''));
    if (pin) pin.click();
  });
  await wait(500);
  // Clearing the selection by clicking the canvas background. Pinned, the drawer must stay.
  await page.evaluate(() => {
    const pane = document.querySelector('.sem-wf-canvas .react-flow__pane');
    const r = pane.getBoundingClientRect();
    const ev = (t) => new MouseEvent(t, { bubbles: true, cancelable: true, clientX: r.left + 30, clientY: r.top + r.height - 40 });
    pane.dispatchEvent(ev('mousedown')); pane.dispatchEvent(ev('mouseup')); pane.dispatchEvent(ev('click'));
  });
  await wait(800);
  checks.drawerAfterClearWhilePinned = await page.evaluate(() => !!document.querySelector('.sem-wf-drawer'));
  // Close is the explicit dismiss: it unpins and closes.
  await page.evaluate(() => {
    const close = [...document.querySelectorAll('.sem-wf-drawer-actions button')].find((b) => (b.getAttribute('aria-label') || '') === 'Close');
    if (close) close.click();
  });
  await wait(700);
  checks.drawerAfterClose = await page.evaluate(() => !!document.querySelector('.sem-wf-drawer'));
  out.behaviour = checks;
  out.behaviourErrors = page.__errors.slice(0, 5);
  await page.close();
};

await scene(1366, 768, '1366x768');
await behaviour();
await scene(1440, 900, '1440x900');
await scene(1000, 700, '1000x700');

writeFileSync(join(OUT, 'measurements.json'), JSON.stringify(out, null, 2));
console.log(JSON.stringify(out, null, 2));
await browser.close(); server.close();
