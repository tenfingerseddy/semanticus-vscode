// Acceptance driver for lane C2 (slice C, Connections): the one-page hub, driven through the REAL component in the
// harness, in three themes. Each scene is one step of the design's "Try it" walkthrough (connections-minimal.html),
// plus the lane's finish line as one long scene whose every engine call is recorded.
//
// The engine is the harness mock, made stateful here the way the engine is: a connect makes that model answer tests
// and queries, a publish-destination set or clear moves Publish to, disconnect lets go of tests and queries, and a SQL
// endpoint saved from a row shows on that row. The refusal sentences on the saved records are the engine's
// (Connect/ConnectionJobs.cs, the engine patch handed to C1). Every call the page makes is returned in the results.
//
//   OUTDIR=... node tools/uishot/c2-hub-drive.mjs          (THEMES=dark,light,hc by default)
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots');
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
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `c2-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({ executablePath: process.env.SEMANTICUS_BROWSER || findBrowser(), headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars', `--user-data-dir=${SCRATCH}`] });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const THEMES = (process.env.THEMES || 'dark,light,hc').split(',');

// The engine's sentences (Connect/ConnectionJobs.cs in the C1 engine patch).
const FILE_NO = 'A file on this computer cannot answer queries. Open it, then choose Power BI Desktop or a workspace model for tests and queries.';
const PUB_NO = 'Publish needs a workspace model. Power BI Desktop and files on this computer cannot be published to.';
const ago = (m) => new Date(Date.now() - m * 60000).toISOString();
const REC = {
  retail: { id: 'cn-retail', kind: 'file', endpoint: 'C:\\Models\\Contoso Retail', modelName: 'Contoso Retail', label: 'local', useCount: 9, lastUsedUtc: ago(5), testsRefusal: FILE_NO, publishRefusal: PUB_NO },
  sales: { id: 'cn-sales', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso Sales', database: 'Contoso Sales', modelName: 'Contoso Sales', authMode: 'interactive', label: 'uat', tenantId: 'contoso.com', useCount: 4, lastUsedUtc: ago(60) },
  fin: { id: 'cn-fin', kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso Finance', database: 'Contoso Finance', modelName: 'Contoso Finance', authMode: 'interactive', label: 'prod', tenantId: 'contoso.com', useCount: 3, lastUsedUtc: ago(90) },
  local: { id: 'cn-local', kind: 'localDesktop', endpoint: 'localhost:51000', database: 'Contoso', modelName: 'Contoso local test', label: 'local', useCount: 7, lastUsedUtc: ago(30), publishRefusal: PUB_NO },
};
const side = (r, extra = {}) => ({ available: true, modelName: r.modelName, source: r.endpoint, connectionId: r.id, kind: r.kind, endpoint: r.endpoint, database: r.database, live: r.kind !== 'file', ...extra });
const NONE = { available: false, live: false };
const PROFILES = [
  { id: 'pf1', username: 'avery@contoso.com', tenantId: 'contoso.com', family: 'interactive', isDefault: true, signedIn: true, lastUseUtc: ago(30) },
  { id: 'pf2', username: 'jordan@contoso.com', tenantId: 'contoso.com', family: 'interactive', isDefault: false, signedIn: true, lastUseUtc: ago(300) },
  { id: 'pf3', username: 'ops@contoso.com', tenantId: 'contoso.com', family: 'interactive', isDefault: false, signedIn: false, lastUseUtc: ago(3000) },
];
const PROBES = ['cn-sales', 'cn-fin'].map((id) => ({ id, account: 'avery@contoso.com', tenantId: 'contoso.com' }));
// A starting world: which records, and which model holds each job.
function world(rows, edit, tests, pub, extra = {}) {
  return {
    connections: rows.map((k) => ({ ...REC[k] })),
    connectionContext: { editing: edit ? { ...side(REC[edit]), connectionId: REC[edit].kind === 'file' ? undefined : REC[edit].id, sourceConnectionId: REC[edit].id } : NONE,
      querying: tests ? side(REC[tests]) : NONE, publishing: pub ? side(REC[pub]) : NONE, ...(extra.ctx || {}) },
    accountProfiles: PROFILES, accountProbes: PROBES,
    endpoints: extra.endpoints || { 'cn-retail': { sourceId: 'sql-retail', name: 'Contoso Retail warehouse', server: 'retail-sql.database.windows.net', database: 'Retail' } },
  };
}

// ---- the engine answers, installed after load --------------------------------------------------------------------
function install(mode) {
  const H = window.__uishotHarness;
  const cfg = window.__UISHOT__;
  const ctx = cfg.connectionContext;
  window.__c2 = { calls: [] };
  const log = (name, params) => window.__c2.calls.push([name, ...(params || [])]);
  const find = (id) => cfg.connections.find((r) => r.id === id);
  const sideOf = (r) => ({ available: true, modelName: r.modelName, source: r.endpoint, connectionId: r.id, kind: r.kind, endpoint: r.endpoint, database: r.database, live: true });
  const SOURCES = [{ id: 'sql-retail', name: 'Contoso Retail warehouse', server: 'retail-sql.database.windows.net', database: 'Retail', authMode: 'interactive', lastTestedUtc: new Date().toISOString(), lastTestOk: true },
    { id: 'sql-old', name: 'Old warehouse', server: 'old-sql.database.windows.net', database: 'Old', authMode: 'interactive' },
    { id: 'sql-closed', name: 'Contoso Finance warehouse', server: 'finance-sql.database.windows.net', database: 'Finance', authMode: 'interactive' }];
  // Usage counted across every saved model, open or not (F-265): a closed model's check still counts.
  const USE = { 'sql-closed': { checkTitles: ['revenue vs source'], mappedTables: [], usingModels: ['Contoso Finance'] } };
  const useOf = (id) => USE[id] || { checkTitles: [], mappedTables: [], usingModels: [] };
  const EP = cfg.endpoints || {};
  H.setResponse('listModelSqlEndpoints', (p) => { log('listModelSqlEndpoints', p); return {
    rows: cfg.connections.map((r) => ({ modelName: r.modelName, connectionId: r.id, sqlEndpoint: EP[r.id] || null })),
    notLinked: SOURCES.filter((s) => !Object.values(EP).some((e) => e.sourceId === s.id)).map((s) => ({ id: s.id, name: s.name, server: s.server, database: s.database,
      checksUsing: useOf(s.id).checkTitles.length, tableMappingsUsing: useOf(s.id).mappedTables.length, usageScope: 'savedModels', usingModels: useOf(s.id).usingModels })) }; });
  H.setResponse('deleteSqlSource', (p) => { log('deleteSqlSource', p); const s = SOURCES.find((x) => x.id === p[0]); const u = useOf(p[0]);
    if (!s) return { deleted: false, note: 'There is no saved SQL source with that id.' };
    if (u.checkTitles.length || u.mappedTables.length) return { deleted: false, checksUsing: u.checkTitles.length, tableMappingsUsing: u.mappedTables.length, checkTitles: u.checkTitles, mappedTables: u.mappedTables, usingModels: u.usingModels, note: `'${s.name}' is still in use, so it was not removed.` };
    SOURCES.splice(SOURCES.indexOf(s), 1); return { deleted: true, note: `'${s.name}' was removed. Nothing was using it.` }; });
  H.setResponse('listSqlSources', (p) => { log('listSqlSources', p); return SOURCES.slice(); });
  H.setResponse('saveSqlSource', (p) => { log('saveSqlSource', p);
    let s = p[0] && SOURCES.find((x) => x.id === p[0]);
    if (!s) { s = { id: 'sql-new-' + SOURCES.length }; SOURCES.push(s); }
    Object.assign(s, { name: p[1], server: p[2], database: p[3], authMode: p[4], tenantId: p[5] }); return { ...s }; });
  H.setResponse('testSqlSource', (p) => { log('testSqlSource', p); const s = SOURCES.find((x) => x.id === p[0]); if (s) { s.lastTestOk = true; s.lastTestedUtc = new Date().toISOString(); }
    return { id: p[0], ok: true, note: 'Connected. It answered in 0.4 seconds.', elapsedMs: 400 }; });
  H.setResponse('setModelSqlEndpoint', (p) => { log('setModelSqlEndpoint', p);
    const s = SOURCES.find((x) => x.id === p[0]); if (s) EP[p[1]] = { sourceId: s.id, name: s.name, server: s.server, database: s.database }; else delete EP[p[1]];
    return { connectionId: p[1], sqlEndpoint: EP[p[1]] || null, changed: true }; });
  H.setResponse('setPublishDestination', (p) => { log('setPublishDestination', p);
    if (p[2]) { const had = ctx.publishing.available; ctx.publishing = { available: false, live: false };
      return { ...ctx, note: had ? `${ctx.editing.modelName} no longer has a publish destination. Nothing was deployed or deleted.` : 'No publish destination was set, so nothing changed.' }; }
    const r = find(p[0]); if (r.publishRefusal) return { __uishotError: r.publishRefusal };
    ctx.publishing = sideOf(r); return { ...ctx }; });
  H.setResponse('disconnect', (p) => { log('disconnect', p); ctx.querying = { available: false, live: false }; return { connected: false }; });
  H.setResponse('connectLocal', (p) => { log('connectLocal', p);
    if (mode.localDown) return { connected: false, message: 'Power BI Desktop is not running Contoso local test. Open it, then try again.' };
    const r = cfg.connections.find((x) => x.endpoint === p[0]); if (r) ctx.querying = sideOf(r); return { connected: true, kind: 'local', dataSource: p[0] }; });
  H.setResponse('connectXmla', (p) => { log('connectXmla', p);
    let r = cfg.connections.find((x) => x.endpoint === p[0] && (x.database || '') === (p[1] || ''));
    if (!r) { r = { id: 'cn-new-' + cfg.connections.length, kind: 'xmla', endpoint: p[0], database: p[1], modelName: p[1], authMode: p[2], tenantId: 'contoso.com', useCount: 1, lastUsedUtc: new Date().toISOString() }; cfg.connections.unshift(r); }
    ctx.querying = sideOf(r); return { connected: true, kind: 'xmla', dataSource: p[0], database: p[1], account: 'avery@contoso.com' }; });
  const opened = (r) => { ctx.editing = { ...sideOf(r), connectionId: r.id, sourceConnectionId: r.id, live: r.kind !== 'file' };
    if (r.kind === 'xmla') { ctx.querying = sideOf(r); ctx.publishing = sideOf(r); } return {}; };
  H.setResponse('openLive', (p) => { log('openLive', p);
    if (mode.authFail) return { __uishotError: 'The live sign-in expired. Sign in again to use it.' };
    return opened(cfg.connections.find((x) => x.endpoint === p[0])); });
  H.setResponse('openLocal', (p) => { log('openLocal', p); if (mode.localDown) return { __uishotError: 'Power BI Desktop is not running Contoso local test. Open it, then try again.' };
    return opened(cfg.connections.find((x) => x.endpoint === p[0]) || { id: 'desk', kind: 'localDesktop', endpoint: p[0], modelName: 'Power BI Desktop report' }); });
  H.setResponse('open', (p) => { log('open', p); return opened(cfg.connections.find((x) => x.endpoint === p[0])); });
  H.setResponse('setDefaultAccountProfile', (p) => { log('setDefaultAccountProfile', p); cfg.accountProfiles.forEach((x) => { x.isDefault = x.id === p[0]; }); return {}; });
  H.setResponse('listAccountProfiles', (p) => cfg.accountProfiles);
  H.setResponse('listLocalInstances', (p) => { log('listLocalInstances', p); return [{ port: 51000, title: 'Contoso Sales', dataSource: 'localhost:51000' }, { port: 52214, title: 'Contoso Inventory', dataSource: 'localhost:52214' }]; });
  H.setResponse('listWorkspaces', (p) => { log('listWorkspaces', p); return [{ id: 'ws-sales', displayName: 'Contoso Sales' }, { id: 'ws-fin', displayName: 'Contoso Finance' }, { id: 'ws-ops', displayName: 'Contoso Ops' }]; });
  H.setResponse('listWorkspaceModels', (p) => { log('listWorkspaceModels', p); return p[0] === 'ws-fin' ? [{ id: 'm1', displayName: 'Contoso Budget' }, { id: 'm2', displayName: 'Contoso Finance' }] : [{ id: 'm3', displayName: 'Contoso Sales' }]; });
  H.setResponse('labelConnection', (p) => { log('labelConnection', p); const r = find(p[0]); if (r) r.label = p[1]; return r || {}; });
  window.dispatchEvent(new MessageEvent('message', { data: { type: 'connectionChanged' } }));
}

// ---- steps --------------------------------------------------------------------------------------------------------
async function act(page, [kind, a, b]) {
  return page.evaluate((kind, a, b) => {
    const vis = (e) => e && e.getClientRects().length;
    const rowOf = (name) => [...document.querySelectorAll('.hub-row')].find((x) => (x.querySelector('[data-testid="hub-model-row"]')?.textContent || '').startsWith(name));
    if (kind === 'id') { const e = [...document.querySelectorAll(`[data-testid="${a}"]`)].filter(vis)[b || 0]; if (e) e.click(); return !!e; }
    if (kind === 'menu') { const e = rowOf(a)?.querySelector('[data-testid="hub-row-overflow"]'); if (e) e.click(); return !!e; }
    if (kind === 'item') { const e = [...document.querySelectorAll('[data-hub-menu] [role="menuitem"]')].find((x) => (x.textContent || '').trim().startsWith(a)); if (e) e.click(); return !!e; }
    if (kind === 'open') { const e = rowOf(a)?.querySelector('[data-testid="hub-row-open"]'); if (e) e.click(); return !!e; }
    if (kind === 'option') { const e = [...document.querySelectorAll('[data-testid="hub-job-option"]')].find((x) => (x.textContent || '').startsWith(a)); if (e) e.click(); return !!e; }
    if (kind === 'text') { const e = [...document.querySelectorAll('button')].filter(vis).find((x) => (x.textContent || '').trim() === a); if (e) e.click(); return !!e; }
    if (kind === 'rm') { const row = [...document.querySelectorAll('[data-testid="hub-notlinked-row"]')].find((x) => (x.textContent || '').startsWith(a));
      const e = row?.querySelector('[data-testid="hub-notlinked-remove"]'); if (e && !e.disabled) e.click(); return !!e && !e.disabled; }
    if (kind === 'select') { const e = document.querySelector(`[data-testid="${a}"]`); if (!e) return false;
      Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(e, b); e.dispatchEvent(new Event('change', { bubbles: true })); return true; }
    return false;
  }, kind, a, b);
}

const W = (...a) => world(...a);
// Scenes follow the design's "Try it" walkthrough, scene by scene and step by step (s1-1 is scene 1, step 1).
const JOBS = [
  { slug: 's1-1-first-open', w: W([], null, null, null), desc: 'First open: one sentence and three ways in' },
  { slug: 's1-3-next-time', w: W(['retail'], 'retail', null, null), desc: 'Next time: the model is in the list and the Now strip says what is open' },
  { slug: 's2-1-reopen', w: W(['sales', 'retail', 'fin', 'local'], 'retail', null, null), desc: 'Recent models; each row has one button: Open' },
  { slug: 's2-2-opened-live', w: W(['sales', 'retail', 'fin', 'local'], 'retail', null, null), steps: [['open', 'Contoso Sales']], desc: 'Open picks the right way: live for a workspace model, and Connections closes' },
  { slug: 's2-3-row-menu', w: W(['sales', 'retail', 'fin', 'local'], 'sales', 'sales', 'sales'), steps: [['menu', 'Contoso Finance']], desc: 'Everything else sits in the row menu' },
  { slug: 's3-1-one-line', w: W(['sales', 'retail', 'fin', 'local'], 'sales', 'sales', 'sales'), desc: 'One model, all three jobs, one line' },
  { slug: 's3-2-change', w: W(['sales', 'retail', 'fin', 'local'], 'sales', 'sales', 'sales'), steps: [['id', 'hub-now-change']], desc: 'Change shows the three jobs' },
  { slug: 's3-3-tests-picker', w: W(['sales', 'retail', 'fin', 'local'], 'sales', 'sales', 'sales'), steps: [['id', 'hub-now-change'], ['id', 'hub-job-tests']], desc: 'The file is struck out with the engine reason' },
  { slug: 's3-4-tests-moved', w: W(['sales', 'retail', 'fin', 'local'], 'sales', 'sales', 'sales'), steps: [['id', 'hub-now-change'], ['id', 'hub-job-tests'], ['option', 'Contoso local test']], desc: 'Tests now run in Power BI Desktop; the strip splits' },
  { slug: 's3-5-publish-picker', w: W(['sales', 'retail', 'fin', 'local'], 'sales', 'local', 'sales'), steps: [['id', 'hub-now-change'], ['id', 'hub-job-publish']], desc: 'Publish to: Desktop and the file struck out, with the reason' },
  { slug: 's3-6-row-menu-greyed', w: W(['sales', 'local', 'fin', 'retail'], 'sales', 'local', 'fin'), steps: [['menu', 'Contoso local test']], desc: 'The row menu offers the same jobs, greyed with the reason' },
  { slug: 's4-1-workspace', w: W(['retail', 'sales', 'local'], 'retail', null, null), steps: [['id', 'hub-add-workspace']], desc: 'Workspace model lists the workspaces' },
  { slug: 's4-2-pick', w: W(['retail', 'sales', 'local'], 'retail', null, null), steps: [['id', 'hub-add-workspace'], ['id', 'pick-ws', 0]], desc: 'Pick the workspace, then the model' },
  { slug: 's4-3-env', w: W(['retail', 'sales', 'local'], 'retail', null, null), steps: [['id', 'hub-add-workspace'], ['id', 'pick-ws', 0], ['id', 'pick-model', 1], ['select', 'pick-env', 'prod']], desc: 'Say which environment it is' },
  { slug: 's4-4-connected', w: W(['retail', 'sales', 'local'], 'retail', null, null), steps: [['id', 'hub-add-workspace'], ['id', 'pick-ws', 0], ['id', 'pick-model', 1], ['select', 'pick-env', 'prod'], ['id', 'pick-connect']], desc: 'It connects and is remembered at the top of the list' },
  { slug: 's4-5-desktop', w: W(['retail', 'sales', 'local'], 'retail', null, null), steps: [['id', 'hub-add-desktop']], desc: 'Power BI Desktop lists the reports you have open' },
  { slug: 's5-1-sql-menu', w: W(['retail', 'sales', 'fin', 'local'], 'sales', 'sales', 'sales'), steps: [['menu', 'Contoso Sales']], desc: 'Contoso Retail has an endpoint on its row; Contoso Sales offers Add SQL endpoint' },
  { slug: 's5-2-sql-form', w: W(['retail', 'sales', 'fin', 'local'], 'sales', 'sales', 'sales', { ctx: { sqlEndpointSuggestion: { server: 'contoso-sales.datawarehouse.fabric.microsoft.com', database: 'SalesLakehouse' } } }), steps: [['menu', 'Contoso Sales'], ['item', 'Add SQL endpoint']], desc: "The form opens under the row; the model's own source is offered first" },
  { slug: 's5-3-sql-tested', w: W(['retail', 'sales', 'fin', 'local'], 'sales', 'sales', 'sales', { ctx: { sqlEndpointSuggestion: { server: 'contoso-sales.datawarehouse.fabric.microsoft.com', database: 'SalesLakehouse' } } }), steps: [['menu', 'Contoso Sales'], ['item', 'Add SQL endpoint'], ['text', 'Use this'], ['id', 'hub-sql-test']], desc: 'Use this fills it in; Test checks it answers' },
  { slug: 's5-4-sql-saved', w: W(['retail', 'sales', 'fin', 'local'], 'sales', 'sales', 'sales', { ctx: { sqlEndpointSuggestion: { server: 'contoso-sales.datawarehouse.fabric.microsoft.com', database: 'SalesLakehouse' } } }), steps: [['menu', 'Contoso Sales'], ['item', 'Add SQL endpoint'], ['text', 'Use this'], ['id', 'hub-sql-save']], desc: 'Saved: the row shows it' },
  { slug: 's6-1-account', w: W(['sales', 'retail', 'fin', 'local'], 'sales', 'sales', 'sales'), desc: 'The account is one button' },
  { slug: 's6-2-account-menu', w: W(['sales', 'retail', 'fin', 'local'], 'sales', 'sales', 'sales'), steps: [['id', 'hub-account-button']], desc: 'Saved accounts, the default, the signed out one' },
  { slug: 's6-3-make-default', w: W(['sales', 'retail', 'fin', 'local'], 'sales', 'sales', 'sales'), steps: [['id', 'hub-account-button'], ['id', 'hub-profile-make-default']], desc: 'Make default says how many models it moves first' },
  { slug: 's6-4-another-account', w: W(['sales', 'retail', 'fin', 'local'], 'sales', 'sales', 'sales'), steps: [['id', 'hub-account-button'], ['id', 'hub-profile-make-default'], ['id', 'hub-make-default-confirm'], ['id', 'hub-account-button'], ['menu', 'Contoso Finance']], desc: 'Done; another account for one model is in its row menu' },
  { slug: 's7-2-signin-expired', mode: { authFail: true }, w: W(['sales', 'retail', 'fin', 'local'], 'retail', null, null), steps: [['open', 'Contoso Finance']], desc: 'The sign-in has run out: the account choice, with the fix' },
  { slug: 's7-3-local-down', mode: { localDown: true }, w: W(['sales', 'retail', 'fin', 'local'], 'retail', null, null), steps: [['open', 'Contoso local test']], desc: 'A local model that is not running: said on its row, with Try again' },
  // Codex on pull request 394. F-266: a model connected for queries with no editing model open is named, with Change
  // and Disconnect. F-265: Remove is offered only where no saved model uses the source, and the confirmation says so.
  { slug: 's8-1-query-only', w: W(['sales', 'retail', 'fin', 'local'], null, 'sales', null), desc: 'Query only: the strip names the query model, with Change and Disconnect' },
  { slug: 's8-2-remove-confirm', w: W(['sales', 'retail', 'fin', 'local'], 'sales', 'sales', 'sales'), steps: [['rm', 'Old warehouse']], desc: 'Remove on a source no saved model uses: the confirmation says what was counted' },
  { slug: 's8-3-remove-closed-model', w: W(['sales', 'retail', 'fin', 'local'], 'sales', 'sales', 'sales'), desc: 'A source a closed model still uses: its row names the model and Remove is shut' },
  // The lane's finish line, one scene: switch Tests through the Change panel (a file struck out), clear Publish to,
  // Disconnect from the Now strip, add a SQL endpoint from a row. Every call is in the results.
  { slug: 'finish', finish: true, w: W(['sales', 'retail', 'fin', 'local'], 'sales', 'sales', 'sales'), steps: [
    ['id', 'hub-now-change'], ['id', 'hub-job-tests'], ['shot', 'finish-a-struck'], ['option', 'Contoso local test'], ['shot', 'finish-b-tests-moved'],
    ['id', 'hub-job-publish'], ['id', 'hub-job-publish-clear'], ['shot', 'finish-c-publish-cleared'],
    ['id', 'hub-now-disconnect'], ['shot', 'finish-d-disconnected'], ['id', 'hub-change-done'],
    ['menu', 'Contoso Finance'], ['item', 'Add SQL endpoint'], ['select', 'hub-sql-saved', 'sql-old'], ['id', 'hub-sql-save'], ['shot', 'finish-e-endpoint']], desc: 'FINISH LINE' },
];

const results = [];
for (const theme of THEMES) {
  for (const job of JOBS) {
    if (job.finish && theme !== THEMES[0] && !process.env.FINISH_ALL) continue;
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
    page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
    await page.setViewport({ width: 1280, height: 900, deviceScaleFactor: 2 });
    await page.evaluateOnNewDocument((cfg) => { window.__UISHOT__ = cfg; }, JSON.parse(JSON.stringify(job.w)));
    let note = '';
    const shots = [];
    try {
      await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?view=connections`, { waitUntil: 'networkidle0', timeout: 30000 });
      await page.waitForSelector('[data-testid="hub-now-change"]', { timeout: 15000 });
      await page.evaluate(install, job.mode || {});
      await applyTheme(page, theme);
      await wait(600);
      for (const step of job.steps || []) {
        if (step[0] === 'shot') { const o = join(OUT, `c2-${step[1]}-${theme}-1280x900.png`); await page.screenshot({ path: o }); shots.push(o); continue; }
        if (!(await act(page, step))) note += `${step[0]} "${step[1]}" not found; `;
        await wait(step[1] === 'pick-connect' ? 1200 : 600);
      }
      const seen = await page.evaluate(() => {
        const t = (s) => { const e = document.querySelector(s); return e ? (e.textContent || '').replace(/\s+/g, ' ').trim() : null; };
        return {
          now: t('[data-testid="hub-now"]'),
          rows: [...document.querySelectorAll('[data-testid="hub-model-row"]')].map((e) => e.textContent.replace(/\s+/g, ' ').trim()),
          struck: [...document.querySelectorAll('[data-testid="hub-job-option"][data-refused="true"]')].map((e) => e.textContent.replace(/\s+/g, ' ').trim()),
          greyed: [...document.querySelectorAll('[data-hub-menu] [data-refused="true"]')].map((e) => e.textContent.replace(/\s+/g, ' ').trim()),
          notice: t('[data-testid="hub-notice"]') || t('[data-testid="hub-change-note"]'),
          rowError: t('[data-testid="hub-row-error"]'),
          accountDialog: !!document.querySelector('[data-testid="hub-account-dialog"]'),
          confirm: t('[data-testid="hub-default-confirm"]'),
          notLinked: [...document.querySelectorAll('[data-testid="hub-notlinked-row"]')].map((e) => ({ text: e.textContent.replace(/\s+/g, ' ').trim(), removeOpen: !e.querySelector('[data-testid="hub-notlinked-remove"]')?.disabled })),
          removeConfirm: t('[data-testid="hub-sql-remove-confirm"]'),
          nowButtons: [...document.querySelectorAll('[data-testid="hub-now"] button')].map((e) => e.textContent.trim()),
          sqlSourcesSection: /SQL sources/.test(document.body.innerText),
          closed: (window.__uishotHarness.messages || []).some((m) => m && m.type === 'closePanel'),
          emDash: document.body.innerText.includes('\u2014'),
          engineWords: /\bXMLA\b|\bdataset\b/i.test(document.querySelector('.hub-frame')?.innerText || ''),
          calls: window.__c2.calls.filter((c) => !/^list/.test(c[0])),
        };
      });
      const out = join(OUT, `c2-${job.slug}-${theme}-1280x900.png`);
      await page.screenshot({ path: out });
      results.push({ theme, job: job.slug, desc: job.desc, seen, errors, note, out, shots });
    } catch (e) {
      results.push({ theme, job: job.slug, desc: job.desc, errors, note: note + 'FATAL: ' + e.message });
    } finally { await page.close(); }
  }
}
await browser.close();
server.close();
writeFileSync(join(OUT, 'results-c2-hub.json'), JSON.stringify(results, null, 2));
const bad = results.filter((r) => r.note || r.errors.length || r.seen?.emDash || r.seen?.engineWords || r.seen?.sqlSourcesSection);
console.log(`c2 hub: ${results.length} scenes, ${bad.length} with a problem`);
for (const r of bad) console.log(`  ${r.theme} ${r.job}: ${r.note} ${r.errors.join(' | ')}${r.seen?.emDash ? ' EM DASH' : ''}${r.seen?.engineWords ? ' ENGINE WORDS' : ''}${r.seen?.sqlSourcesSection ? ' SQL SOURCES SECTION' : ''}`);
const fin = results.find((r) => r.job === 'finish');
if (fin?.seen) { console.log('finish-line calls:'); for (const c of fin.seen.calls) console.log('  ' + JSON.stringify(c)); }
