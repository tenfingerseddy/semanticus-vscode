// The Query page's engine half against a REAL model file, over the real MCP door (engine --mcp, stdio). It opens a real offline model
// (the AdventureWorks .bim the tests ship), reads the fields the Fields card and the wells list, and shows what the page does when
// a query is run with no model attached: the engine's own plain refusal, not an empty result. A number needs an Analysis Services
// endpoint (Power BI Desktop or XMLA), and this laptop has none, so the number and the speed come from the live gate (see the report).
//   node tools/query-real/engine-smoke.mjs <path to Semanticus.Engine.dll> <path to a .bim or TMDL folder>
import { spawn } from 'node:child_process';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const [dll, model] = process.argv.slice(2);
if (!dll || !model) { console.error('usage: engine-smoke.mjs <Semanticus.Engine.dll> <model>'); process.exit(2); }
const home = mkdtempSync(join(tmpdir(), 'qreal-'));
const child = spawn('dotnet', [dll, '--mcp'], { env: { ...process.env, HOME: home, SEMANTICUS_HOME: join(home, 'sem'), ANTHROPIC_API_KEY: '' }, stdio: ['pipe', 'pipe', 'ignore'], cwd: home });
let buf = '', id = 0;
const waiting = new Map();
child.stdout.on('data', (d) => {
  buf += d;
  let i;
  while ((i = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, i).trim(); buf = buf.slice(i + 1);
    if (!line) continue;
    try { const m = JSON.parse(line); if (m.id !== undefined && waiting.has(m.id)) { waiting.get(m.id)(m); waiting.delete(m.id); } } catch { /* a log line */ }
  }
});
const rpc = (method, params) => new Promise((res) => { const n = ++id; waiting.set(n, res); child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: n, method, params }) + '\n'); });
const call = async (name, args) => { const r = await rpc('tools/call', { name, arguments: args }); const t = r.result?.content?.[0]?.text ?? JSON.stringify(r.error ?? r); try { return JSON.parse(t); } catch { return t; } };
const out = [];
const say = (k, v) => { out.push(`${k}: ${typeof v === 'string' ? v : JSON.stringify(v)}`); console.log(out[out.length - 1]); };

await rpc('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'query-real', version: '1' } });
child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
const opened = await call('open_model', { path: model });
say('open_model', typeof opened === 'string' ? opened.slice(0, 160) : { name: opened.name ?? opened.modelName, tables: opened.tableCount ?? opened.tables });
const measures = await call('list_measures', {});
const list = Array.isArray(measures) ? measures : measures.measures ?? [];
say('list_measures', `${list.length} measures, first: ${list.slice(0, 3).map((m) => m.name).join(', ')}`);
const cols = await call('list_columns', {});
const cl = Array.isArray(cols) ? cols : cols.columns ?? [];
say('list_columns', `${cl.length} columns, first: ${cl.slice(0, 3).map((c) => `${c.table}[${c.name}]`).join(', ')}`);
const ran = await call('run_dax', { query: 'EVALUATE ROW("x", 1)' });
say('run_dax with no live model', (ran.error ?? JSON.stringify(ran)).slice(0, 200));
const who = await call('get_entitlement', {});
say('get_entitlement', { tier: who.tier ?? who.Tier });
child.kill();
process.exit(0);
