// Acceptance driver for the cp/git-cicd-account lane: Changes > Published > Advanced, both delivery panels,
// driven through the sign-in failure Kane hit on the Yoga (2026-09-15) and out the other side.
//
// The harness answers each Fabric call per sign-in mode the way a real token acquisition does: the Azure
// command line fails with Azure.Identity's own words, a browser sign-in succeeds. These failures come back
// on the DTO's own .error, not as a thrown call, because that is what LocalEngine really does for the Fabric
// Git reads and for cicd_publish. ?auth=azcli starts the page on a destination opened with the command line,
// so the failure, the plain line, the Sign in action and the recovery are all one run.
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
const webRoot = resolve(here, '..', '..'); // Semanticus.VSCode
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const url = req.url || '/';
  const p = normalize(join(webRoot, decodeURIComponent(url.split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const executablePath = process.env.SEMANTICUS_BROWSER || findBrowser();
const browser = await puppeteer.launch({ executablePath, headless: true, protocolTimeout: 180000, args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const LABEL = process.env.SHOTLABEL || 'after';

// A step is either a button to click, or a value to type into the nth input carrying a placeholder.
// Both panels label a box "workspace id", so the index says which panel: 0 = Fabric Git, 1 = the publish.
const GIT_WS = { fill: 'workspace id', nth: 0, value: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' };
const PUB_WS = { fill: 'workspace id', nth: 1, value: 'ws-1' };
const PUB_ITEM = { fill: 'semantic-model item id', nth: 0, value: 'model-1' };

const JOBS = [
  // Fabric Git: the defect, then the fix, then the recovered read.
  { slug: 'fabricgit-azcli-failure', q: 'auth=azcli', steps: ['Advanced', 'Delivery tools', GIT_WS, 'Status'] },
  { slug: 'fabricgit-signed-in', q: 'auth=azcli', steps: ['Advanced', 'Delivery tools', GIT_WS, 'Status', 'Sign in'] },
  // The starting choice with no flag: the way the publish destination was really opened.
  { slug: 'fabricgit-default-picker', steps: ['Advanced', 'Delivery tools', GIT_WS, 'Status'] },
  // CI/CD publish: the same three.
  { slug: 'cicd-azcli-failure', q: 'auth=azcli', steps: ['Advanced', 'Delivery tools', PUB_WS, PUB_ITEM, 'Plan'], scrollTo: 'CI·CD · Publish' },
  { slug: 'cicd-signed-in', q: 'auth=azcli', steps: ['Advanced', 'Delivery tools', PUB_WS, PUB_ITEM, 'Plan', 'Sign in'], scrollTo: 'CI·CD · Publish' },
];

const clickText = (t) => {
  const b = [...document.querySelectorAll('button')].find((x) => (x.textContent || '').trim() === t && x.getClientRects().length);
  if (b) b.click();
  return !!b;
};
const fillNth = (step) => {
  const boxes = [...document.querySelectorAll('input')].filter((i) => (i.placeholder || '') === step.fill && i.getClientRects().length);
  const box = boxes[step.nth];
  if (!box) return false;
  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
  setter.call(box, step.value);
  box.dispatchEvent(new Event('input', { bubbles: true }));
  return true;
};

const results = [];
for (const job of JOBS) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: 1366, height: 900, deviceScaleFactor: 2 });
  let note = '';
  try {
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html${job.q ? '?' + job.q : ''}`, { waitUntil: 'networkidle0', timeout: 30000 });
    await page.waitForSelector('nav button', { timeout: 15000 });
    await wait(1200);
    const okArea = await page.evaluate((a) => { const b = [...document.querySelectorAll('nav button')].find((x) => (x.textContent || '').trim() === a); if (b) b.click(); return !!b; }, 'Changes');
    if (!okArea) note += 'area "Changes" not found; ';
    await wait(900);
    const okTab = await page.evaluate(clickText, 'Published');
    if (!okTab) note += 'tab "Published" not found; ';
    await wait(1200);
    for (const step of job.steps) {
      const ok = typeof step === 'string'
        ? await page.evaluate(clickText, step)
        : await page.evaluate(fillNth, step);
      if (!ok) note += `step "${typeof step === 'string' ? step : step.fill + '#' + step.nth}" not found; `;
      await wait(typeof step === 'string' ? 1400 : 500);
    }
    // Both delivery panels render together and the page is taller than 900px, so a shot of the lower panel
    // has to scroll to it or it proves nothing about what a person sees.
    if (job.scrollTo) {
      const scrolled = await page.evaluate((title) => {
        const head = [...document.querySelectorAll('*')].find((e) => (e.textContent || '').trim() === title && e.children.length === 0);
        const panel = head && head.closest('div');
        if (panel) panel.scrollIntoView({ block: 'start' });
        return !!panel;
      }, job.scrollTo);
      if (!scrolled) note += `scrollTo "${job.scrollTo}" not found; `;
      await wait(700);
    }
    // What the page actually says, so a shot cannot be reported as proving something it does not show.
    const seen = await page.evaluate(() => {
      const t = (sel) => { const e = document.querySelector(sel); return e ? (e.textContent || '').trim() : null; };
      const pickers = [...document.querySelectorAll('[data-testid="account-picker"] select')];
      return {
        pickerCount: pickers.length,
        pickerValues: pickers.map((p) => p.value),
        pickerLabels: pickers.map((p) => (p.selectedOptions[0] || {}).textContent || null),
        fabricGitProblem: t('[data-testid="fabricgit-problem"]'),
        fabricGitFix: t('[data-testid="fabricgit-signin-fix"]'),
        cicdProblem: t('[data-testid="cicd-problem"]'),
        cicdFix: t('[data-testid="cicd-signin-fix"]'),
        // Proof the read really came back after the sign-in, not just that the red line went away.
        gitItems: [...document.querySelectorAll('div')].filter((d) => /Sales Overview$/.test((d.textContent || '').trim())).length,
        inSync: document.body.innerText.includes('In sync: no workspace/git differences'),
        gitBranch: document.body.innerText.includes('bi-models/main'),
        publishPlan: (document.body.innerText.match(/(DRY RUN[^\n]*)/) || [])[1] || null,
        rawAzLogin: document.body.innerText.includes("az login"),
      };
    });
    const out = join(OUT, `${LABEL}-${job.slug}-1366x900.png`);
    await page.screenshot({ path: out, fullPage: false });
    results.push({ job: job.slug, seen, errors, note, out });
  } catch (e) {
    results.push({ job: job.slug, errors, note: note + 'FATAL: ' + e.message });
  } finally {
    await page.close();
  }
}
await browser.close();
server.close();
writeFileSync(join(OUT, `results-gitcicd-${LABEL}.json`), JSON.stringify(results, null, 2));
console.log(JSON.stringify(results, null, 2));
