// Verifier, lane m11/uat-numbers round 2 (N1). LEFT FAILING ON PURPOSE: it proves a defect, it does not fix one.
//
// Round 2 orders a field by the model's sort-by column by putting that column INTO the query's group-by
// (buildQuery: SUMMARIZECOLUMNS('Date'[Fiscal Month], 'Date'[FiscalMonthNo], "Sales", [Sales]) ORDER BY ...).
// That is not only an order: a grouped column is in every cell's filter context. A measure that depends on it reads a
// different number than it did (the common one: DIVIDE([Sales], CALCULATE([Sales], ALL('Date'[Fiscal Month]))) keeps the
// FiscalMonthNo filter, so every month reads 100%, the way Power BI shows it).
//
// "Explain this number" exists to recompute the clicked cell in the SAME context the visual used: the engine
// re-creates the visual's axis columns as group-by (LocalEngine.Explain.cs, Finding A). The payload Queries hands it
// (daxvisual.tsx explainPayloadFromRow) still lists only the wells' fields, not the sort-by column the query grouped by.
// So the panel's header shows a recomputed number (8.3%) for a cell that shows 100%, and explains the wrong one.
//
// The invariant: Explain groups by every column the drawn query grouped by.
import assert from 'node:assert/strict';
import test from 'node:test';
import { makeLoader, req } from './helpers/webview-load.mjs';

const React = req('react');
globalThis.document = { documentElement: { style: {} } };
globalThis.getComputedStyle = () => ({ getPropertyValue: () => '' });

const load = makeLoader({ './echart': { EChart: () => React.createElement('div'), tokenPx: () => 11 }, echarts: {} });
const { explainPayloadFromRow } = load('daxvisual.tsx');
const { buildQuery } = load('daxwells.tsx');
const { foldSortColumns } = load('axisorder.ts');

const measure = { kind: 'measure', ref: '[Share of Year]', name: 'Share of Year' };
const col = (table, name, sortBy) => ({ kind: 'column', ref: `'${table}'[${name}]`, name, table, ...(sortBy ? { sortBy: { ref: `'${table}'[${sortBy}]`, name: sortBy } } : {}) });
const groupedBy = (q) => /SUMMARIZECOLUMNS\(([\s\S]*?)\n\s*\)/.exec(q)[1].split(',').map((s) => s.trim()).filter((s) => /^'[^']+'\[[^\]]+\]$/.test(s));

test('VN2 Explain recomputes a sort-by field\'s cell grouped by the same columns the drawn query grouped by', () => {
  const config = { rows: [col('Date', 'Fiscal Month', 'FiscalMonthNo')], cols: [], values: [measure], filters: [] };
  const q = buildQuery(config);
  assert.deepEqual(groupedBy(q), ["'Date'[Fiscal Month]", "'Date'[FiscalMonthNo]"], q);
  const res = foldSortColumns({ columns: [{ name: 'Fiscal Month' }, { name: 'FiscalMonthNo' }, { name: 'Share of Year' }], rows: [['Jan', 7, 1], ['Jul', 1, 1]], rowCount: 2, truncated: false, elapsedMs: 1 }, config);
  const p = explainPayloadFromRow(config, res.rows[0], 1);
  assert.ok(p, 'a value cell has an explanation');
  for (const c of groupedBy(q)) assert.ok(p.groupBy.includes(c), `Explain must group by ${c}, the visual did; it groups by [${p.groupBy.join(', ')}]`);
});

test('VN2 the same holds for a sort-by field on the matrix columns', () => {
  const config = { rows: [col('Product', 'Category')], cols: [col('Date', 'Fiscal Month', 'FiscalMonthNo')], values: [measure], filters: [] };
  const q = buildQuery(config);
  const p = explainPayloadFromRow(config, ['Bikes', 'Jan', 1], 2);
  for (const c of groupedBy(q)) assert.ok(p.groupBy.includes(c), `Explain must group by ${c}; it groups by [${p.groupBy.join(', ')}]`);
});
