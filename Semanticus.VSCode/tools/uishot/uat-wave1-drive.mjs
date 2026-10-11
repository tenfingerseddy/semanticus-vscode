// Acceptance driver for two UI harm findings from Sol's UAT wave 1 (lane m11/uat-fix-2), run on the real Studio
// against the mock engine.
//
// UAT-S07b-2-001: Model tree > Sales > Edit M Code opened Power Query on Customer. The tree jump mounts the
// Power Query tab before its model document has loaded; the heal step then saw no tables, cleared the jump's
// table, and when the document arrived it picked the first table. The cold scenes below are that journey: the
// navigation is queued while Studio mounts and flushed on studioReady, exactly as extension.ts flushNav does.
//
// UAT-S06a-1-002: a lesson the assistant wrote reads "Not reviewed" in Model notes, in the insight list and on its
// suggested Primer update, and the insight carries a one-click Approve even once it is recallable.
//
// EVERY SCENE STATES WHAT IT MUST SHOW and the run fails when one does not.
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
const webRoot = resolve(here, '..', '..');            // Semanticus.VSCode
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'uat-wave1');
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `uat-wave1-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath, headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars',
    `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' },
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const LABEL = process.env.SHOTLABEL || 'after';
const ONLY = (process.env.SCENES || '').split(',').map((x) => x.trim()).filter(Boolean);
const WIDTH = parseInt(process.env.UISHOT_W || '1440', 10);

// What the Power Query tab is showing: the table selector and the query selector, read from their own titles.
const readPowerQuery = () => {
  const table = document.querySelector('select[title="Choose a table"]');
  const query = document.querySelector('select[title="Choose an M query"]');
  return { table: table ? table.value : null, query: query ? query.value : null };
};
const readNotes = () => {
  const cards = [...document.querySelectorAll('#knowledge-insights article')];
  const card = (id) => cards.find((a) => (a.textContent || '').includes(id));
  const text = (el) => (el?.textContent || '').replace(/\s+/g, ' ').trim();
  const suggestion = document.querySelector('article.xe-rule-item');
  const agentCard = cards.find((a) => /DIVIDE\(\[x\]/.test(a.textContent || ''));
  const humanCard = cards.find((a) => /Hiding technical FK columns/.test(a.textContent || ''));
  const buttons = (el) => [...(el?.querySelectorAll('button') || [])].map((b) => text(b));
  return {
    suggestion: text(suggestion),
    agentCard: text(agentCard), agentButtons: buttons(agentCard),
    humanCard: text(humanCard), humanButtons: buttons(humanCard),
    found: !!card,
  };
};

const JOBS = [
  {
    slug: 'mcode-cold-jump-sales', q: 'coldNav=mcode:table:Sales&sessionDelay=600', kind: 'pq', settle: 3200,
    desc: 'Model tree > Sales > Edit M Code, Studio cold: Power Query opens on Sales and its own query',
    must: (s) => [
      s.table !== 'Sales' && `the jump named Sales but the table selector reads ${JSON.stringify(s.table)}`,
      !(s.query || '').startsWith('partition:Sales/') && `the M query should be a Sales partition; it reads ${JSON.stringify(s.query)}`,
    ],
  },
  {
    // The UAT journey: Power Query was used earlier in the session, so the tab remembers a table (here Customer).
    // Studio is on another page; the tree jump remounts Power Query, and the heal step, running before the model
    // document had loaded, wiped the jump's table in favour of an empty one. The document then picked Customer.
    slug: 'mcode-return-jump-sales', q: 'tab=mcode', kind: 'pq', settle: 2600,
    steps: async (page) => {
      await page.evaluate(() => {
        const sel = document.querySelector('select[title="Choose a table"]');
        const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set;
        set.call(sel, 'Customer');
        sel.dispatchEvent(new Event('change', { bubbles: true }));
      });
      await wait(900);
      await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'plan' } })));
      await wait(900);
      await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'mcode', target: 'table:Sales' } })));
      await wait(2600);
    },
    desc: 'Power Query used earlier on Customer, then Model tree > Sales > Edit M Code from another page: it opens on Sales',
    must: (s) => [
      s.table !== 'Sales' && `the jump named Sales but the table selector reads ${JSON.stringify(s.table)}`,
      !(s.query || '').startsWith('partition:Sales/') && `the M query should be a Sales partition; it reads ${JSON.stringify(s.query)}`,
    ],
  },
  {
    // The same journey to a table that is not first in the model, so landing on the first table cannot pass by luck.
    slug: 'mcode-return-jump-product', q: 'tab=mcode', kind: 'pq', settle: 2600,
    steps: async (page) => {
      await page.evaluate(() => {
        const sel = document.querySelector('select[title="Choose a table"]');
        const set = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set;
        set.call(sel, 'Customer');
        sel.dispatchEvent(new Event('change', { bubbles: true }));
      });
      await wait(900);
      await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'plan' } })));
      await wait(900);
      await page.evaluate(() => window.dispatchEvent(new MessageEvent('message', { data: { type: 'navigate', tab: 'mcode', target: 'table:Product' } })));
      await wait(2600);
    },
    desc: 'Power Query used earlier on Customer, then Model tree > Product > Edit M Code from another page: it opens on Product',
    must: (s) => [
      s.table !== 'Product' && `the jump named Product but the table selector reads ${JSON.stringify(s.table)}`,
      !(s.query || '').startsWith('partition:Product/') && `the M query should be a Product partition; it reads ${JSON.stringify(s.query)}`,
    ],
  },
  {
    slug: 'mcode-cold-jump-product', q: 'coldNav=mcode:table:Product', kind: 'pq', settle: 3200,
    desc: 'the same jump to another table with no session delay: it lands on Product, not the first table',
    must: (s) => [
      s.table !== 'Product' && `the jump named Product but the table selector reads ${JSON.stringify(s.table)}`,
    ],
  },
  {
    slug: 'notes-not-reviewed', q: 'tab=knowledge', kind: 'notes', settle: 2600,
    desc: 'Model notes: a lesson the assistant wrote says Not reviewed and offers Approve; a person\'s own lesson does not',
    must: (s) => [
      !/Not reviewed/.test(s.suggestion) && `the assistant's suggested update does not say Not reviewed: ${JSON.stringify(s.suggestion.slice(0, 160))}`,
      !/Not reviewed/.test(s.agentCard) && 'the assistant\'s approved lesson does not say Not reviewed',
      !s.agentButtons.includes('Approve') && `the assistant's lesson offers no Approve; its buttons are ${JSON.stringify(s.agentButtons)}`,
      /Not reviewed/.test(s.humanCard) && 'a person\'s own lesson is marked Not reviewed',
      s.humanButtons.includes('Approve') && 'a person\'s own lesson offers Approve',
    ],
  },
];

const results = [];
for (const job of JOBS) {
  if (ONLY.length && !ONLY.includes(job.slug)) continue;
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: WIDTH, height: 1000, deviceScaleFactor: 2 });
  const out = join(OUT, `${job.slug}-${LABEL}.png`);
  try {
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${job.q}`, { waitUntil: 'networkidle0', timeout: 30000 });
    await wait(job.settle);
    if (job.steps) await job.steps(page);
    if (job.kind === 'notes') await page.evaluate(() => document.querySelector('#knowledge-insights')?.scrollIntoView());
    const seen = await page.evaluate(job.kind === 'pq' ? readPowerQuery : readNotes);
    await page.screenshot({ path: out });
    const failures = job.must(seen).filter(Boolean);
    if (errors.length) failures.push(`the page logged errors: ${errors.join(' | ')}`);
    results.push({ job: job.slug, desc: job.desc, seen, errors, failures, out });
  } catch (e) {
    results.push({ job: job.slug, desc: job.desc, errors, failures: ['FATAL: ' + e.message] });
  } finally {
    await page.close();
  }
}
await browser.close();
server.close();
writeFileSync(join(OUT, `results-uat-wave1-${LABEL}.json`), JSON.stringify(results, null, 2));
console.log(JSON.stringify(results.map((r) => ({ job: r.job, seen: r.seen, failures: r.failures })), null, 2));

const broken = results.filter((r) => r.failures?.length);
if (broken.length) {
  console.error(`\n${broken.length} of ${results.length} scenes did not show what they promised:`);
  for (const r of broken) console.error(`  ${r.job}: ${r.failures.join(' | ')}`);
  process.exit(1);
}
console.log(`\nall ${results.length} scenes showed what they promised, at ${WIDTH} wide`);
