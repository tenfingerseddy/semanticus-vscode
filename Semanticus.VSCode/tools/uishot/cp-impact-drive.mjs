// Acceptance driver for the cp/impact-one-page lane: Model > Lineage > Impact, option A.
//
// Every shot is the REAL component driven through the mock engine at 1366x768, not a drawing. The harness's
// report scope MUTATES, so "before a check" and "after a check" are the same page reacting to a new reading
// rather than two fixtures: ticking a report leaves it not checked, and pressing Check is what changes the
// card, the strip and the cleanup reasons.
//
// What is NOT proven here, and is not claimed anywhere: no XMLA endpoint, no Fabric sign-in, no real report
// download, no real deletion. The published-report leg is a mock; the local-folder leg is a mock.
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
// findBrowser prefers a cached chrome-headless-shell. A fallback to a full browser can hand the launch to a
// running chromium and close the session mid-drive, which reads as a component failure and is not one.
const executablePath = process.env.SEMANTICUS_BROWSER || findBrowser();
const browser = await puppeteer.launch({
  executablePath, headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars'],
  // The times on screen are formatted in the viewer's own zone. Pin the browser to UTC so a shot taken on
  // one machine reads the same as a shot taken on another, and matches the fixture's stamp.
  env: { ...process.env, TZ: 'UTC' },
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

const clickTestid = (spec) => {
  const testid = typeof spec === 'string' ? spec : spec.testid;
  const nth = typeof spec === 'string' ? 0 : (spec.nth || 0);
  const all = [...document.querySelectorAll(`[data-testid="${testid}"]`)].filter((x) => x.getClientRects().length);
  const el = all[nth];
  if (el) el.click();
  return !!el;
};
const clickText = (spec) => {
  const [sel, text] = spec;
  const el = [...document.querySelectorAll(sel)].find((x) => (x.textContent || '').trim().startsWith(text) && x.getClientRects().length);
  if (el) el.click();
  return !!el;
};

// `pick` selects a measure or column in the left list by name.
const pickByName = (name) => {
  // The row text starts with the kind glyph, so match the NAME span, not the row.
  const rows = [...document.querySelectorAll('[data-testid="lineage-impact-object"]')];
  const el = rows.find((r) => {
    const span = r.querySelector('span.truncate');
    return span && (span.textContent || '').trim() === name;
  });
  if (el) el.click();
  return !!el;
};

const JOBS = [
  { slug: '01-impact-empty', q: 'scope=one', desc: 'Impact with nothing picked yet' },
  { slug: '02-picked-few', q: 'scope=one', pick: 'Total Sales', desc: 'a measure with a few dependants' },
  { slug: '03-picked-many', q: 'scope=all&impact=big', pick: 'Date', desc: 'a column 123 things depend on' },
  { slug: '04-show-all-open', q: 'scope=all&impact=big', pick: 'Date', press: ['impact-show-all'], desc: 'Show all open, grouped and sorted' },
  { slug: '05-show-all-filtered', q: 'scope=all&impact=big', pick: 'Date', press: ['impact-show-all'], type: { 'impact-show-all-filter': 'margin' }, desc: 'the full list with a filter typed' },
  { slug: '06-cleanup-two-ticked', q: 'scope=one', press: ['impact-cleanup-toggle'], tick: [0, 1], desc: 'the cleanup view with two ticked' },
  { slug: '07-drawer-nothing-chosen', q: 'scope=empty', press: ['impact-choose-reports'], desc: 'Choose reports on the first visit' },
  { slug: '08-drawer-one-checked', q: 'scope=one', pick: 'Total Sales', press: ['impact-choose-reports'], desc: 'the drawer with one report already read' },
  { slug: '09-drawer-permission', q: 'scope=one', press: ['impact-choose-reports', 'choose-reports-permission-details'], desc: 'the permission detail, before any sign-in' },
  { slug: '10-drawer-partly', q: 'scope=partly', pick: 'Total Sales', press: ['impact-choose-reports'], desc: 'a report that could not be fully checked' },
  { slug: '11-drawer-stale', q: 'scope=stale', pick: 'Total Sales', press: ['impact-choose-reports'], desc: 'a reading the model has moved past' },
  { slug: '12-escape-closed-drawer', q: 'scope=one', pick: 'Total Sales', press: ['impact-choose-reports'], escape: true, desc: 'Escape closes only the drawer' },
  { slug: '13-after-a-check', q: 'scope=one', pick: 'Total Sales', press: ['impact-choose-reports', 'choose-reports-check'], settleAfter: 900, escape: true, desc: 'the card after the reports were read' },
  { slug: '14-propose-blocked', q: 'scope=one', pick: 'Total Sales', desc: 'Propose removal blocked, with the reason' },
  { slug: '15-propose-allowed', q: 'scope=one', press: ['impact-cleanup-toggle'], pickCleanup: '_Base Rate', desc: 'a candidate nothing uses: removal is offered, with the list still beside it' },
  // A fully READ scope, because with an unread report chosen the candidate is demoted and a removal must not
  // be proposable at all. That is the point of R1, and this job is about what happens when it is satisfied.
  { slug: '16-proposed-in-changes', q: 'scope=all', press: ['impact-cleanup-toggle'], pickCleanup: '_Base Rate', then: ['impact-propose-removal'], goPlan: true, desc: 'the proposal under Changes > Proposed',
    expect: ['_Base Rate'] },
  { slug: '17-stale-strip', q: 'scope=stale', desc: 'the strip when a reading has gone stale',
    expect: ['1 listed report needs checking again'] },

  // ---- Round two. Astra rejected the first build on journeys these seventeen jobs never walked. ----------
  // Each of these is a claim the old shots could not have disproved, because nothing pressed the control.
  { slug: '18-discover-and-tick', q: 'scope=empty', press: ['impact-choose-reports', 'choose-reports-load-workspaces'],
    select: { 'choose-reports-workspace': 'ws-a' }, then: ['choose-reports-load-reports'], tickReport: 'Warehouse ops',
    desc: 'a newly discovered report is chosen once, and reads as not checked',
    expect: ['Warehouse ops', 'Not checked'], absent: ['Nothing chosen yet.'] },

  { slug: '19-change-workspace-keeps-the-other', q: 'scope=twoworkspaces',
    press: ['impact-choose-reports', 'choose-reports-load-workspaces'],
    select: { 'choose-reports-workspace': 'ws-a' }, then: ['choose-reports-load-reports'], tickReport: 'Warehouse ops',
    desc: 'adding from one workspace leaves the report from the other where it was',
    expect: ['Old report', 'Workspace B', 'Warehouse ops', 'Workspace A'] },

  { slug: '20-propose-then-scope-changes', q: 'scope=one', press: ['impact-cleanup-toggle'], pickCleanup: '_Base Rate',
    then: ['impact-propose-removal'], afterPropose: true,
    desc: 'the scope changes after a proposal, and the page says the selection needs checking again',
    expect: ['needs checking'] },

  { slug: '21-report-hit-details', q: 'scope=all&impact=big', pick: 'Date', settleAfter: 900, press: ['impact-reports-show-all'],
    desc: 'a report hit down to the page and the visual, including use through a measure',
    expect: ['Overview', 'Sales by month', 'Bar chart', 'through a measure that uses it'] },

  { slug: '22-unchecked-scope-cleanup', q: 'scope=unchecked', press: ['impact-cleanup-toggle'],
    desc: 'a chosen but unread report leaves nothing on this list callable unused',
    expect: ['1 chosen report still needs checking', 'Check them, then look again', 'Needs checking · 3'],
    absent: ['No use found in these checks'] },

  { slug: '23-reopen-not-checked', q: 'scope=reopen', press: ['impact-choose-reports'],
    desc: 'reopening a model brings the choices back, unread',
    expect: ['Warehouse ops', 'Old report', 'Not checked', 'Your changed selection needs checking'],
    absent: ['Checked 9:41 AM'] },

  { slug: '24-rename-journey', q: 'scope=one', pick: 'Total Sales', then: ['impact-rename'],
    desc: 'Rename carries the field to where its name is edited',
    expect: ['Total Sales'] },
];

// One job at a time when a scene is being worked on: JOBFILTER=21 runs only the jobs whose slug contains 21.
const only = process.env.JOBFILTER;
const results = [];
for (const job of (only ? JOBS.filter((j) => j.slug.includes(only)) : JOBS)) {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: 1366, height: 768, deviceScaleFactor: 2 });
  let note = '';
  try {
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${job.q}`, { waitUntil: 'networkidle0', timeout: 30000 });
    await page.waitForSelector('[data-sem-tab]', { timeout: 15000 }).catch(() => {});
    await wait(700);
    // Model > Lineage, then the Impact sub-tab.
    const okTab = await page.evaluate(clickText, ['button', 'Lineage']);
    if (!okTab) note += 'Lineage tab not found; ';
    await wait(600);
    const okImpact = await page.evaluate(clickText, ['button', 'Impact']);
    if (!okImpact) note += 'Impact sub-tab not found; ';
    await wait(700);

    // Pick first, then act: a drawer opened before the field was picked has no field to return to.
    if (job.pick) {
      const ok = await page.evaluate(pickByName, job.pick);
      if (!ok) note += `pick "${job.pick}" not found; `;
      await wait(900);
    }
    for (const spec of job.press ?? []) {
      const ok = await page.evaluate(clickTestid, spec);
      if (!ok) note += `action "${JSON.stringify(spec)}" not found; `;
      await wait(600);
    }
    if (job.pickCleanup) {
      const ok = await page.evaluate((name) => {
        const rows = [...document.querySelectorAll('[data-testid="cleanup-row"]')];
        const el = rows.find((r) => (r.textContent || '').includes(name));
        if (el) el.click();
        return !!el;
      }, job.pickCleanup);
      if (!ok) note += `cleanup row "${job.pickCleanup}" not found; `;
      await wait(900);
    }
    for (const n of job.tick ?? []) {
      const ok = await page.evaluate((i) => {
        const boxes = [...document.querySelectorAll('[data-testid="cleanup-tick"]')];
        if (!boxes[i]) return false;
        boxes[i].click();
        return true;
      }, n);
      if (!ok) note += `tick ${n} not found; `;
      await wait(250);
    }
    for (const [testid, value] of Object.entries(job.select ?? {})) {
      const ok = await page.evaluate(([t, v]) => {
        const el = document.querySelector(`[data-testid="${t}"]`);
        if (!el) return false;
        el.value = v;
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
      }, [testid, value]);
      if (!ok) note += `select "${testid}" not found; `;
      await wait(600);
    }
    for (const [testid, value] of Object.entries(job.type ?? {})) {
      const ok = await page.evaluate((t) => { const e = document.querySelector(`[data-testid="${t}"]`); if (e) e.focus(); return !!e; }, testid);
      if (!ok) { note += `field "${testid}" not found; `; continue; }
      await page.keyboard.type(value, { delay: 10 });
      await wait(400);
    }
    if (job.settleAfter) await wait(job.settleAfter);
    if (job.escape) {
      await page.keyboard.press('Escape');
      await wait(600);
    }
    for (const spec of job.then ?? []) {
      const ok = await page.evaluate(clickTestid, spec);
      if (!ok) note += `then "${JSON.stringify(spec)}" not found; `;
      await wait(800);
    }
    if (job.tickReport) {
      const ok = await page.evaluate((name) => {
        const rows = [...document.querySelectorAll('[data-testid="choose-reports-report"]')];
        const row = rows.find((r) => (r.textContent || '').includes(name));
        const box = row && row.querySelector('input[type="checkbox"]');
        if (box) box.click();
        return !!box;
      }, job.tickReport);
      if (!ok) note += `discovered report "${job.tickReport}" not found; `;
      await wait(1100);
    }
    if (job.afterPropose) {
      // The basis moves under the proposal: the person changes the selection before applying it.
      const ok = await page.evaluate(() => {
        const el = document.querySelector('[data-testid="impact-choose-reports"]');
        if (el) el.click();
        return !!el;
      });
      if (!ok) note += 'Choose reports not found after the proposal; ';
      await wait(700);
      const excluded = await page.evaluate(clickTestid, 'choose-reports-remove');
      if (!excluded) note += 'Exclude from checks not found; ';
      await wait(1100);
    }
    if (job.goPlan) {
      const ok = await page.evaluate(clickText, ['button', 'Changes']);
      if (!ok) note += 'Changes tab not found; ';
      await wait(1100);
      // Bring the proposal itself into the shot, not just the count that went up.
      const scrolled = await page.evaluate(() => {
        const hit = [...document.querySelectorAll('*')].filter((e) => {
          if (!(e.textContent || '').includes('_Base Rate')) return false;
          return ![...e.children].some((c) => (c.textContent || '').includes('_Base Rate'));
        })[0];
        if (hit) hit.scrollIntoView({ block: 'center' });
        return !!hit;
      });
      if (!scrolled) note += 'the proposal row was not on the page; ';
      await wait(600);
    }

    // What the page actually SAYS, so no shot is reported as proving something it does not show.
    const seen = await page.evaluate(() => {
      const txt = (sel) => { const e = document.querySelector(sel); return e ? (e.textContent || '').trim().replace(/\s+/g, ' ') : null; };
      const all = (sel) => [...document.querySelectorAll(sel)].map((e) => (e.textContent || '').trim().replace(/\s+/g, ' '));
      const body = (document.body.innerText || '').replace(/\s+/g, ' ');
      return {
        verdict: txt('[data-testid="impact-verdict"]'),
        reportSummary: txt('[data-testid="impact-report-summary"]'),
        strip: txt('[data-testid="impact-checked-strip"]'),
        reportsHeadline: txt('[data-testid="impact-reports-headline"]'),
        reportsDetail: txt('[data-testid="impact-reports-detail"]'),
        showAll: txt('[data-testid="impact-show-all"]'),
        showAllOpen: !!document.querySelector('[data-testid="impact-show-all-list"]'),
        groups: all('[data-testid="impact-group-label"]'),
        dependants: document.querySelectorAll('[data-testid="impact-dependant"]').length,
        cleanupGroups: all('[data-testid="cleanup-group"]'),
        cleanupStillOpen: !!document.querySelector('[data-testid="cleanup-list"]'),
        cleanupHeadline: txt('[data-testid="cleanup-headline"]'),
        cleanupPropose: txt('[data-testid="cleanup-propose"]'),
        cleanupReasons: all('[data-testid="cleanup-reason"]'),
        removalBlocked: txt('[data-testid="impact-removal-blocked"]'),
        proposeDisabled: (() => { const b = document.querySelector('[data-testid="impact-propose-removal"]'); return b ? b.disabled : null; })(),
        proposeLabel: txt('[data-testid="impact-propose-removal"]'),
        drawerOpen: !!document.querySelector('[data-testid="choose-reports-drawer"]'),
        reportsShowAll: !!document.querySelector('[data-testid="impact-reports-show-all"]'),
        reportVisuals: document.querySelectorAll('[data-testid="impact-report-visual"]').length,
        drawerStates: all('[data-testid="choose-reports-state"]'),
        permission: txt('[data-testid="choose-reports-permission"]'),
        escapeNote: txt('[data-testid="choose-reports-escape-note"]'),
        // The banned vocabulary, checked against what the page actually renders.
        banned: ['Broken', 'Check blast radius', 'Create probes', 'Stage removal', 'Interview', 'Coverage gaps',
          'reportPaths', 'PBIR', 'Report-aware', 'Model-only', 'azcli', 'barChart', 'Safe to remove']
          .filter((w) => body.includes(w)),
        // The Changes view has no test hook of its own (another lane owns it), so this reads the page text:
        // the proposal is there when the field it names is on screen under a proposed row.
        planHasProposal: body.includes('_Base Rate'),
        planSaysProposed: /Proposed/i.test(body),
      };
    });
    const png = join(OUT, `${job.slug}.png`);
    await page.screenshot({ path: png, fullPage: false });
    // What this job claims its screen shows. Checked against the page text, so a caption can never outrun it.
    const body = await page.evaluate(() => (document.body.innerText || '').replace(/\s+/g, ' '));
    const expectFailed = (job.expect ?? []).filter((t) => !body.includes(t));
    const absentFailed = (job.absent ?? []).filter((t) => body.includes(t)).map((t) => 'still on screen: ' + t);
    results.push({ slug: job.slug, desc: job.desc, png, note: note.trim(), errors, seen, expectFailed: [...expectFailed, ...absentFailed] });
  } catch (e) {
    results.push({ slug: job.slug, desc: job.desc, note: (note + 'FAILED: ' + (e?.message ?? e)).trim(), errors, seen: null });
  } finally {
    await page.close();
  }
}

await browser.close();
server.close();
const report = join(OUT, 'cp-impact-drive.json');
writeFileSync(report, JSON.stringify(results, null, 2));
for (const r of results) {
  const flags = [r.note ? 'NOTE: ' + r.note : '', r.errors.length ? `${r.errors.length} console error(s)` : ''].filter(Boolean).join(' | ');
  console.log(`${r.slug.padEnd(26)} ${r.desc}${flags ? '  [' + flags + ']' : ''}`);
  if (r.seen?.banned?.length) console.log(`${''.padEnd(26)} BANNED WORDS ON SCREEN: ${r.seen.banned.join(', ')}`);
  for (const e of r.expectFailed ?? []) console.log(`${''.padEnd(26)} EXPECTED BUT NOT SEEN: ${e}`);
}
console.log('\nwrote ' + report);
// ANY note fails the run, not only a thrown FAILED. A "control not found" note meant the driver had walked
// past the thing the job existed to show and still reported a pass (Astra, ruling 6). A job that could not
// press what it set out to press has not proved anything.
const failed = results.filter((r) => r.note || r.errors.length || (r.seen?.banned?.length ?? 0) > 0 || (r.expectFailed?.length ?? 0) > 0);
if (failed.length) {
  console.error(`\n${failed.length} job(s) failed, errored, could not reach a control, or showed a banned word.`);
  for (const r of failed) console.error(`  ${r.slug}: ${[r.note, r.errors.join('; '), (r.expectFailed ?? []).join('; ')].filter(Boolean).join(' | ')}`);
  process.exit(1);
}
