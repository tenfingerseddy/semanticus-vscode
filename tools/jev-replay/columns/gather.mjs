// Gather column labels and derived facts from live models, on the machine that holds the connections.
//
//   node gather.mjs [--connections <file>] [--out <file>] [--only <index>]
//
// Run it in a terminal INSIDE the desktop session of the machine that holds the connections: the engine's saved
// XMLA sign-in lives in that session's keyring, so the agent-origin connect is silent there and fails over SSH.
//
// Drives the INSTALLED engine headless through its MCP door (Semanticus.Engine mcp --workspace <scratch>), connects
// read-only to every xmla entry in the connections file with connect_xmla, reads metadata through run_dmv and derives
// the contract's ColumnFacts `measured` block from scalar run_dax probes (see derive.mjs for the privacy shape).
//
// Hard rules this script keeps:
//   - Auth is the engine's own agent path. An agent-origin connect never opens a browser or a sign-in window; when
//     no silent token exists it fails, this script records the failure and moves on. It never retries interactively.
//   - Output is names, types, the five labelled settings and derived facts. The model is written under an
//     anonymous key (m0, m1, ...); the endpoint, workspace and model names are never written.
//   - Console output carries counts only. Any error text is scrubbed of the connection's own strings first.
import { readFileSync, writeFileSync, mkdirSync, mkdtempSync, rmSync, existsSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { McpClient } from './mcp-client.mjs';
import { rowsOf, shapeModel, columnFactsQuery, deriveMeasured, oneToOnePlan, pairQuery, SAMPLE_ABOVE, qCol } from './derive.mjs';

const arg = (k, d) => { const i = process.argv.indexOf(k); return i > 0 ? process.argv[i + 1] : d; };
const H = homedir();
const CONNS = arg('--connections', join(H, '.semanticus', 'connections.json'));
const OUT = arg('--out', join(H, 'Downloads', 'jev-columns-facts.json'));
const WS = join(H, '.cache', 'jev-replay-columns', 'ws');
const ONLY = arg('--only', null);
const EXE = process.env.SEMANTICUS_ENGINE
  ?? join(H, '.vscode', 'extensions', 'semanticus-vscode.semanticus-vscode-1.2.1', 'engine', 'Semanticus.Engine');

const conns = JSON.parse(readFileSync(CONNS, 'utf8')).filter((e) => e.kind === 'xmla');
const secrets = conns.flatMap((c) => [c.endpoint, c.database, c.modelName, c.label, c.lastAccount]).filter(Boolean);
const scrub = (s) => {
  let t = String(s ?? '');
  for (const x of secrets) t = t.split(x).join('<redacted>');
  return t.replace(/[\w.+-]+@[\w.-]+/g, '<email>').replace(/'[^']{1,200}'/g, "'<name>'").slice(0, 240);
};

mkdirSync(dirname(OUT), { recursive: true });
mkdirSync(WS, { recursive: true });
// The scratch workspace is ours alone. If an engine already owns it, stop rather than attach: attaching to an engine
// that a VS Code window drives would switch the model open in that window.
if (existsSync(join(WS, '.semanticus', 'engine.json'))) {
  let pid = 0;
  try { const info = JSON.parse(readFileSync(join(WS, '.semanticus', 'engine.json'), 'utf8')); pid = Number(info.pid ?? info.Pid) || 0; } catch { /* unreadable: treat as stale */ }
  let alive = false;
  try { if (pid) { process.kill(pid, 0); alive = true; } } catch { /* not running */ }
  if (alive) { console.log('stopped: an engine already owns the scratch workspace; close it and run again'); process.exit(2); }
}
const models = [];
const env = { ...process.env };
for (const k of ['DISPLAY', 'WAYLAND_DISPLAY', 'BROWSER', 'ANTHROPIC_API_KEY']) delete env[k];
// No display and no BROWSER, so the engine this starts cannot put a window on the screen. It DOES keep the desktop
// session bus, on purpose: on Linux the person's saved sign-in (the token cache named by SEMANTICUS_SIGNIN_DIR below)
// lives in the desktop keyring, which is reached over that bus, and reaching the person's models with it is what this
// script is for. The engine runs only its assistant door here, which never opens a browser or starts a sign-in: an
// expired sign-in makes connect_xmla fail with a message. Test runs, smokes and probes drop the bus; this script keeps it.
// The engine keeps its per-user state in a scratch folder, never the person's ~/.semanticus: every connect_xmla below
// would otherwise rewrite the saved list and add history rows (27 Sep). The saved list is read from the file above,
// so the scratch home costs nothing. The sign-in store moves with a scratch home too, and this script exists to reach
// the person's models with the person's own saved sign-in, so it names that store on purpose (SEMANTICUS_SIGNIN_DIR).
const signInDir = join(process.env.LOCALAPPDATA
  ?? (process.platform === 'darwin' ? join(H, 'Library', 'Application Support') : (process.env.XDG_DATA_HOME || join(H, '.local', 'share'))),
  'Semanticus', 'auth');
env.SEMANTICUS_HOME = mkdtempSync(join(tmpdir(), 'sem-home-jev-columns-'));
// The scratch home is this run's alone, so it goes when the run does, on every way out (a finished run, an early
// exit, an uncaught error). The engine has exited by then: close() waits for it on the normal path.
process.on('exit', () => { try { rmSync(env.SEMANTICUS_HOME, { recursive: true, force: true }); } catch { /* best effort */ } });
env.SEMANTICUS_SIGNIN_DIR = process.env.SEMANTICUS_SIGNIN_DIR || signInDir;

const m = new McpClient(EXE, ['mcp', '--workspace', WS], env);
// Ctrl+C, SIGTERM or a closed terminal: Node skips 'exit' handlers when a signal ends the process, so leave through
// process.exit instead, once the engine has let go of the scratch home, and the handler above removes it.
// Handled with process.on and a flag, not once: with once, a second Ctrl+C met Node's default handler, which skips
// the exit handler and left the scratch home behind (round 4).
let closing = false;
for (const [signal, code] of [['SIGINT', 130], ['SIGTERM', 143], ['SIGHUP', 129]]) {
  process.on(signal, () => {
    if (closing) return;   // a second Ctrl+C while the engine closes must not end the run before the cleanup
    closing = true;
    m.close().finally(() => process.exit(code));
  });
}
const summary = [];
try {
  await m.init();
  for (let i = 0; i < conns.length; i++) {
    if (ONLY != null && String(i) !== String(ONLY)) continue;
    const c = conns[i];
    const key = `m${i}`;
    const rec = { key, reached: false };
    summary.push(rec);
    if (!c.database) { rec.error = 'no database recorded for this connection'; continue; }
    try {
      const s = await m.call('connect_xmla', { endpoint: c.endpoint, database: c.database, authMode: c.authMode || 'interactive' }, 180_000);
      if (s?.connected === false) throw new Error(s.error || 'not connected');
      rec.reached = true;
      await gatherOne(key, rec);
    } catch (e) {
      rec.error = scrub(e.message);
    }
  }
} catch (e) {
  summary.push({ fatal: scrub(e.message), stderr: scrub(m.stderrTail.slice(-3).join(' | ')) });
}
await m.close();
writeFileSync(OUT, JSON.stringify({ kind: 'jev-columns-facts', version: 1, gatheredAt: new Date().toISOString(), summary, models }));
for (const r of summary) console.log(JSON.stringify(r));
const got = summary.filter((r) => r.reached).length;
console.log(`models reached ${got} of ${conns.length}; columns measured ${summary.reduce((a, r) => a + (r.measured ?? 0), 0)}; wrote ${OUT}`);

async function dmv(query) { return rowsOf(await m.call('run_dmv', { query, maxRows: 100000 }, 300_000)); }
async function dax(query) {
  const r = await m.call('run_dax', { query, maxRows: 5 }, 600_000);
  if (r.error) throw new Error(r.policyRefused ? 'refused by the agent policy' : `dax query failed: ${r.error}`);
  const names = (r.columns ?? []).map((x) => x.name);
  const row = (r.rows ?? [])[0] ?? [];
  return { names, row };
}

async function gatherOne(key, rec) {
  const tables = await dmv('SELECT [ID],[Name],[IsHidden],[DataCategory] FROM $SYSTEM.TMSCHEMA_TABLES');
  const columns = await dmv('SELECT [ID],[TableID],[ExplicitName],[InferredName],[ExplicitDataType],[InferredDataType],[DataCategory],[IsHidden],[IsKey],[SummarizeBy],[Type],[FormatString],[SortByColumnID],[IsAvailableInMDX] FROM $SYSTEM.TMSCHEMA_COLUMNS');
  const relationships = await dmv('SELECT [FromTableID],[FromColumnID],[FromCardinality],[ToTableID],[ToColumnID],[ToCardinality],[IsActive] FROM $SYSTEM.TMSCHEMA_RELATIONSHIPS');
  const measures = await dmv('SELECT [TableID],[Name],[Expression] FROM $SYSTEM.TMSCHEMA_MEASURES');
  const shaped = shapeModel({ tables, columns, relationships, measures });

  // Storage sizes, when the service answers the storage DMVs. Absent sizes stay null, never zero.
  const storage = new Map();
  try {
    for (const r of await dmv('SELECT [DIMENSION_NAME],[ATTRIBUTE_NAME],[DICTIONARY_SIZE],[COLUMN_TYPE] FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMNS')) {
      if (r.COLUMN_TYPE !== 'BASIC_DATA') continue;
      storage.set(`${r.DIMENSION_NAME}\u0001${r.ATTRIBUTE_NAME}`, { dictionaryBytes: Number(r.DICTIONARY_SIZE) || 0, dataBytes: 0 });
    }
    for (const r of await dmv('SELECT [DIMENSION_NAME],[COLUMN_ID],[USED_SIZE] FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS')) {
      const attr = String(r.COLUMN_ID ?? '').replace(/ \(\d+\)$/, '');
      const s = storage.get(`${r.DIMENSION_NAME}\u0001${attr}`);
      if (s) s.dataBytes += Number(r.USED_SIZE) || 0;
    }
    rec.storage = 'measured';
  } catch { rec.storage = 'unavailable'; }

  let measured = 0, failed = 0, sampledCols = 0;
  // Why columns failed, scrubbed and bucketed, so a model whose every column fails says why (e.g. an unprocessed
  // table or a DirectQuery source the service cannot reach) without carrying a name out.
  const reasons = {};
  for (const t of shaped) {
    let rows = null;
    for (const col of t.columns) {
      if (col.dataType === 'binary' || col.dataType === 'variant' || col.dataType === 'unknown') continue;
      try {
        // Size first, so a large column is measured on a bounded sample.
        const pre = await dax(`EVALUATE ROW("nb", COUNTROWS(FILTER(ALLNOBLANKROW(${qCol(t.name, col.name)}), NOT ISBLANK(${qCol(t.name, col.name)}))))`);
        const sample = Number(pre.row[0] ?? 0) > SAMPLE_ABOVE;
        const q = columnFactsQuery(t.name, col.name, col.dataType, sample);
        const r = await dax(q.query);
        const counts = Object.fromEntries(q.fields.map((f, i) => [f, r.row[i]]));
        rows = Number(counts.rows);
        col.measured = deriveMeasured(counts, col.dataType, sample, storage.get(`${t.name}\u0001${col.name}`));
        measured++; if (sample) sampledCols++;
      } catch (e) {
        failed++;
        const k = scrub(e.message).replace(/\[[^\]]{1,200}\]/g, '[<name>]').slice(0, 160);
        reasons[k] = (reasons[k] ?? 0) + 1;
      }
    }
    t.rows = rows;
    // oneToOneWith, same table: distinct(pair) == distinct(each).
    const plan = oneToOnePlan(t.columns.filter((x) => x.measured));
    const link = (a, b) => { a.measured.oneToOneWith.push(b.name); b.measured.oneToOneWith.push(a.name); };
    for (const [a, b] of plan.known) link(a, b);
    for (let i = 0; i < plan.toTest.length; i += 20) {
      const batch = plan.toTest.slice(i, i + 20);
      try {
        const r = await dax(pairQuery(t.name, batch));
        batch.forEach(([a, b], j) => { if (Number(r.row[j]) === a.measured.distinct) link(a, b); });
      } catch { /* a failed batch leaves those pairs unlinked */ }
    }
    if (plan.capped) t.oneToOneCapped = true;
  }
  rec.tables = shaped.length;
  rec.columns = shaped.reduce((a, t) => a + t.columns.length, 0);
  rec.measured = measured; rec.failed = failed; rec.sampled = sampledCols;
  if (failed) rec.failureReasons = reasons;
  for (const t of shaped) for (const col of t.columns) delete col.id;
  models.push({ model: key, data: 'live', tables: shaped });
}
