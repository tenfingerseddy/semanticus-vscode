#!/usr/bin/env node
// A stand-in for `Semanticus.Engine mcp` in the self-test. It answers the handful of tools gather.mjs calls with a
// tiny Contoso-style model, and records every DAX query it is sent so the test can check they are all scalar probes.
import { createInterface } from 'node:readline';
import { appendFileSync } from 'node:fs';

const LOG = process.env.FAKE_ENGINE_LOG;
// Where a real engine would keep its per-user state and sign-ins, so the test can check gather.mjs chose them.
if (process.env.FAKE_ENGINE_ENV_LOG) {
  appendFileSync(process.env.FAKE_ENGINE_ENV_LOG, JSON.stringify({ home: process.env.SEMANTICUS_HOME ?? null, signIn: process.env.SEMANTICUS_SIGNIN_DIR ?? null }));
}
const rs = (columns, rows) => ({ columns: columns.map((name) => ({ name })), rows, rowCount: rows.length });
const DMV = {
  TMSCHEMA_TABLES: rs(['ID', 'Name', 'IsHidden', 'DataCategory'], [[1, 'Sales', false, null], [2, 'Date', false, 'Time'], [3, 'LocalDateTable_x', true, null]]),
  TMSCHEMA_COLUMNS: rs(['ID', 'TableID', 'ExplicitName', 'InferredName', 'ExplicitDataType', 'InferredDataType', 'DataCategory', 'IsHidden', 'IsKey', 'SummarizeBy', 'Type', 'FormatString', 'SortByColumnID', 'IsAvailableInMDX'], [
    [10, 1, 'OrderDateKey', null, 6, 6, null, false, false, 3, 1, '0', null, true],
    [11, 1, 'Amount', null, 10, 10, null, false, false, 3, 1, '\\$#,0.00', null, true],
    [12, 1, 'RowNumber-x', null, 6, 6, null, true, false, 1, 3, null, null, true],
    [20, 2, 'DateKey', null, 6, 6, null, true, true, 2, 1, '0', null, true],
    [21, 2, 'Month Name', null, 2, 2, null, false, false, 2, 1, null, 22, true],
    [22, 2, 'Month Number', null, 6, 6, null, false, false, 2, 1, '0', null, true],
    [23, 2, 'Country', null, 2, 2, 'Country', false, false, 2, 1, null, null, true],
    [30, 3, 'Date', null, 9, 9, null, true, false, 2, 1, null, null, true],
  ]),
  TMSCHEMA_RELATIONSHIPS: rs(['FromTableID', 'FromColumnID', 'FromCardinality', 'ToTableID', 'ToColumnID', 'ToCardinality', 'IsActive'], [[1, 10, 2, 2, 20, 1, true]]),
  TMSCHEMA_MEASURES: rs(['TableID', 'Name', 'Expression'], [[1, 'Total', "SUM ( Sales[Amount] )"], [1, 'Avg', "AVERAGE('Sales'[Amount])"]]),
};
// Per-column counts in columnFactsQuery field order. Only counts, as a real engine would return for a scalar probe.
const COUNTS = {
  "'Sales'[OrderDateKey]": { rows: 1000, distinct: 365, blanks: 0, nb: 365 },
  "'Sales'[Amount]": { rows: 1000, distinct: 900, blanks: 0, nb: 900, nonWhole: 850 },
  "'Date'[DateKey]": { rows: 365, distinct: 365, blanks: 0, nb: 365 },
  "'Date'[Month Name]": { rows: 365, distinct: 12, blanks: 0, nb: 12, monthNameN: 12 },
  "'Date'[Month Number]": { rows: 365, distinct: 12, blanks: 0, nb: 12, month12N: 12, quarter4N: 4 },
  "'Date'[Country]": { rows: 365, distinct: 3, blanks: 0, nb: 3, countryN: 3 },
};
const reply = (id, result) => process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id, result }) + '\n');
const text = (o) => ({ content: [{ type: 'text', text: JSON.stringify(o) }, { type: 'text', text: 'health: ok' }] });

createInterface({ input: process.stdin }).on('line', (line) => {
  const msg = JSON.parse(line);
  if (msg.id == null) return;
  if (msg.method === 'initialize') return reply(msg.id, { protocolVersion: '2025-06-18', capabilities: {}, serverInfo: { name: 'fake' } });
  if (msg.method !== 'tools/call') return reply(msg.id, {});
  const { name, arguments: a } = msg.params;
  if (name === 'connect_xmla') return reply(msg.id, a.database === 'Missing' ? { isError: true, content: [{ type: 'text', text: `could not open '${a.endpoint}'` }] } : text({ connected: true }));
  if (name === 'run_dmv') {
    const k = Object.keys(DMV).find((x) => a.query.includes(x));
    return reply(msg.id, text(k ? DMV[k] : { error: 'unsupported DMV' }));
  }
  if (name === 'run_dax') {
    if (LOG) appendFileSync(LOG, a.query.replace(/\n/g, ' ') + '\n');
    // Field names open each line of the ROW body; the single-line size probe names one field right after ROW(.
    let fields = [...a.query.matchAll(/\n\s+"([A-Za-z0-9]+)", /g)].map((m) => m[1]);
    if (!fields.length) fields = [...a.query.matchAll(/ROW\("([A-Za-z0-9]+)",/g)].map((m) => m[1]);
    const col = Object.keys(COUNTS).find((c) => a.query.includes(c));
    if (fields.every((f) => /^p\d+$/.test(f))) {
      // pair test: Month Name with Month Number is one to one (12 pairs)
      return reply(msg.id, text(rs(fields, [fields.map(() => 12)])));
    }
    const c = COUNTS[col] ?? {};
    return reply(msg.id, text(rs(fields, [fields.map((f) => c[f] ?? 0)])));
  }
  return reply(msg.id, text({ error: 'unknown tool' }));
});
