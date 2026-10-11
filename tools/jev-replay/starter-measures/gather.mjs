// Gather starter measure labels and derived facts from live models, on the machine that holds the connections.
//
//   node gather.mjs [--connections <file>] [--out <file>] [--only <index>]
//
// Drives the INSTALLED engine headless through its MCP door (Semanticus.Engine mcp --workspace <scratch>), connects
// read-only to every xmla entry in the connections file with connect_xmla, reads metadata through run_dmv, and for
// each number column on a fact table works out two things ON THIS MACHINE:
//   the label   which plain aggregation measure the author already wrote over it, if any (starter.mjs, labelFor)
//   the facts   the engine's own request for that column: names, types, settings, and shares from one scalar
//               run_dax probe that returns counts only (rows, distinct values, blanks, values with a fraction)
// Measure formulas are read here and never written: the output carries an option id, not a formula.
//
// Hard rules this script keeps (the same as the columns replay's gather):
//   - Auth is the engine's own agent path. An agent-origin connect never opens a browser or a sign-in window; when
//     no silent token exists it fails, this script records the failure and moves on. It never retries interactively.
//   - Output is names, types, settings, derived shares and the label. Models are keyed anonymously (m0, m1, ...);
//     the endpoint, workspace and model names are never written. A model that is a structural copy of one already
//     gathered is counted and skipped.
//   - Console output carries counts only. Any error text is scrubbed of the connection's own strings first.
//   - Nothing this process starts can reach the desktop: the display variables are removed from its environment.
import { readFileSync, writeFileSync, mkdirSync, mkdtempSync, rmSync, existsSync, chmodSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { createHash } from 'node:crypto';
import { McpClient } from './mcp-client.mjs';
import { shapeModel, candidates, labelFor, ruleChoice, snapshot } from './starter.mjs';

const arg = (k, d) => { const i = process.argv.indexOf(k); return i > 0 ? process.argv[i + 1] : d; };
const H = homedir();
const CONNS = arg('--connections', join(H, '.semanticus', 'connections.json'));
const OUT = arg('--out', join(H, '.cache', 'jev-replay-starter', 'starter-facts.json'));
const WS = join(H, '.cache', 'jev-replay-starter', 'ws');
const ONLY = arg('--only', null);
const EXE = process.env.SEMANTICUS_ENGINE
  ?? join(H, '.vscode', 'extensions', 'semanticus-vscode.semanticus-vscode-1.2.1', 'engine', 'Semanticus.Engine');

const conns = JSON.parse(readFileSync(CONNS, 'utf8')).filter((e) => e.kind === 'xmla');
const secrets = conns.flatMap((c) => [c.endpoint, c.database, c.modelName, c.label, c.lastAccount]).filter(Boolean);
const scrub = (s) => {
  let t = String(s ?? '');
  for (const x of secrets) t = t.split(x).join('<redacted>');
  return t.replace(/[\w.+-]+@[\w.-]+/g, '<email>').replace(/'[^']{1,200}'/g, "'<name>'").replace(/\[[^\]]{1,200}\]/g, '[<name>]').slice(0, 200);
};

mkdirSync(dirname(OUT), { recursive: true });
mkdirSync(WS, { recursive: true });
// The scratch workspace is ours alone. If an engine already owns it, stop rather than attach.
if (existsSync(join(WS, '.semanticus', 'engine.json'))) {
  let pid = 0;
  try { const info = JSON.parse(readFileSync(join(WS, '.semanticus', 'engine.json'), 'utf8')); pid = Number(info.pid ?? info.Pid) || 0; } catch { /* stale */ }
  let alive = false;
  try { if (pid) { process.kill(pid, 0); alive = true; } } catch { /* not running */ }
  if (alive) { console.log('stopped: an engine already owns the scratch workspace; close it and run again'); process.exit(2); }
}

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
env.SEMANTICUS_HOME = mkdtempSync(join(tmpdir(), 'sem-home-jev-starter-'));
// The scratch home is this run's alone, so it goes when the run does, on every way out (a finished run, an early
// exit, an uncaught error). The engine has exited by then: close() waits for it on the normal path.
process.on('exit', () => { try { rmSync(env.SEMANTICUS_HOME, { recursive: true, force: true }); } catch { /* best effort */ } });
env.SEMANTICUS_SIGNIN_DIR = process.env.SEMANTICUS_SIGNIN_DIR || signInDir;

const models = [];
const summary = [];
const seen = new Set();
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
try {
  await m.init();
  for (let i = 0; i < conns.length; i++) {
    if (ONLY != null && String(i) !== String(ONLY)) continue;
    const c = conns[i];
    const rec = { key: `m${i}`, reached: false };
    summary.push(rec);
    if (!c.database) { rec.error = 'no database recorded for this connection'; continue; }
    try {
      const s = await m.call('connect_xmla', { endpoint: c.endpoint, database: c.database, authMode: c.authMode || 'interactive' }, 180_000);
      if (s?.connected === false) throw new Error(s.error || 'not connected');
      rec.reached = true;
      await gatherOne(rec);
    } catch (e) {
      rec.error = scrub(e.message);
    }
  }
} catch (e) {
  summary.push({ fatal: scrub(e.message), stderr: scrub(m.stderrTail.slice(-3).join(' | ')) });
}
await m.close();
writeFileSync(OUT, JSON.stringify({ kind: 'jev-starter-facts', version: 1, gatheredAt: new Date().toISOString(), summary, models }));
try { chmodSync(OUT, 0o600); } catch { /* best effort */ }
for (const r of summary) console.log(JSON.stringify(r));
console.log(`models gathered ${models.length} of ${conns.length}; candidate columns ${models.reduce((a, x) => a + x.columns.length, 0)}; wrote ${OUT}`);

async function dmv(query) {
  const rs = await m.call('run_dmv', { query, maxRows: 100000 }, 300_000);
  if (!rs || rs.error) throw new Error(rs?.error ? 'dmv query failed' : 'no result');
  const names = (rs.columns ?? []).map((x) => x.name);
  return (rs.rows ?? []).map((r) => Object.fromEntries(names.map((n, j) => [n, r[j]])));
}

async function dax(query) {
  const r = await m.call('run_dax', { query, maxRows: 5 }, 600_000);
  if (r.error) throw new Error(r.policyRefused ? 'refused by the agent policy' : `dax query failed: ${r.error}`);
  return (r.rows ?? [])[0] ?? [];
}

// Function declarations, not consts: they are hoisted, so the top-level run above can call them.
function qTable(t) { return `'${String(t).replace(/'/g, "''")}'`; }
function qCol(t, c) { return `${qTable(t)}[${String(c).replace(/]/g, ']]')}]`; }

/** One scalar probe per column: counts only, never a value, a minimum, a maximum or a sample. */
function probe(t, c) {
  const col = qCol(t.name, c.name);
  const nonWhole = c.dataType === 'Int64' ? '0'
    : `COUNTROWS(FILTER(FILTER(ALLNOBLANKROW(${col}), NOT ISBLANK(${col})), ${col} <> INT(${col})))`;
  return `EVALUATE ROW("rows", COUNTROWS(${qTable(t.name)}), "distinct", DISTINCTCOUNTNOBLANK(${col}), "blanks", COUNTBLANK(${col}), "nonWhole", ${nonWhole})`;
}

async function gatherOne(rec) {
  const meta = {
    tables: await dmv('SELECT [ID],[Name],[DataCategory] FROM $SYSTEM.TMSCHEMA_TABLES'),
    columns: await dmv('SELECT [ID],[TableID],[ExplicitName],[InferredName],[ExplicitDataType],[InferredDataType],[IsHidden],[IsKey],[SummarizeBy],[Type],[FormatString] FROM $SYSTEM.TMSCHEMA_COLUMNS'),
    relationships: await dmv('SELECT [FromTableID],[FromColumnID],[FromCardinality],[ToTableID],[ToColumnID],[ToCardinality] FROM $SYSTEM.TMSCHEMA_RELATIONSHIPS'),
  };
  // Formulas stay in this function's memory. Only labelFor reads them, and it returns an option id.
  const measures = await dmv('SELECT [TableID],[Expression] FROM $SYSTEM.TMSCHEMA_MEASURES');
  const shaped = shapeModel(meta);

  // A copy of a model already gathered (same tables, same columns) is counted, not measured twice.
  const fp = createHash('sha256').update(shaped.map((t) => t.name + ':' + t.columns.map((c) => c.name).join(',')).sort().join('|')).digest('hex');
  if (seen.has(fp)) { rec.copy = true; return; }
  seen.add(fp);

  const cands = candidates(shaped);
  rec.tables = shaped.length;
  rec.factTables = shaped.filter((t) => t.isFact).length;
  rec.candidates = cands.length;
  rec.measures = measures.length;
  let measured = 0, failed = 0;
  const reasons = {};
  const rowsByTable = new Map();
  const out = [];
  for (const { t, c } of cands) {
    let facts = null, rows = null;
    try {
      const r = await dax(probe(t, c));
      rows = Number(r[0]);
      const distinct = Number(r[1]), blanks = Number(r[2]), nonWhole = Number(r[3]);
      facts = {
        distinct,
        blankShare: rows > 0 ? blanks / rows : 0,
        wholeNumbers: nonWhole === 0,
        uniqueOnEveryRow: rows > 0 && blanks === 0 && distinct === rows,
      };
      rowsByTable.set(t.name, rows);
      measured++;
    } catch (e) {
      failed++;
      const k = scrub(e.message);
      reasons[k] = (reasons[k] ?? 0) + 1;
    }
    const label = labelFor(t.name, c.name, measures);
    out.push({
      table: t.name,
      column: c.name,
      label,
      rule: ruleChoice(c.isKey, c.relationshipRole, c.summarizeBy),
      state: snapshot(t, c, facts, rows ?? 0),
    });
  }
  rec.measured = measured; rec.failed = failed;
  if (failed) rec.failureReasons = reasons;
  rec.labels = out.reduce((a, x) => { const k = x.label.kind === 'plain' ? x.label.options.join('+') : x.label.kind; a[k] = (a[k] ?? 0) + 1; return a; }, {});
  models.push({ model: rec.key, columns: out });
}
