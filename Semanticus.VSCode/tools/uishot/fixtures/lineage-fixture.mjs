// A REALISTIC-SCALE lineage fixture (~500 nodes) for the interaction driver (lineage-interact.mjs), shaped like a
// real finance semantic model (cf. Kane's 549-node "Finance" model) so the Graph/Tree views are exercised at true
// scale — not the 15-node mock in harness.html. Deterministic (index-based, no Math.random) so runs are reproducible.
//
// Emits three internally-consistent pieces the harness injects via window.__UISHOT__:
//   lineage        — getLineage: sources → tables → columns → measures (dependsOn chains) + relationships.
//   unused         — unusedObjects: a few safe / used-by-unused-only / caution verdicts referencing REAL refs.
//   reportAnalysis — analyzeReports/analyzeCloudReports: published-report usage WITH visuals[] (page/visual/usedRefs
//                    reference real model refs) so augmentGraphWithReports merges the field → visual → page → report leg.

const FACTS = ['Sales', 'GeneralLedger', 'Budget', 'Inventory'];
const DIMS = [
  'Date', 'Account', 'Customer', 'Product', 'Store', 'Department', 'Vendor', 'CostCentre', 'Currency', 'Scenario',
  'Employee', 'Project', 'Region', 'Channel', 'Entity', 'FiscalPeriod', 'PaymentTerm', 'TaxCode', 'Warehouse', 'Company',
];

// Numeric measure-source columns per fact, and descriptive attribute columns per dim.
const FACT_NUM = {
  Sales: ['SalesAmount', 'NetAmount', 'Quantity', 'DiscountAmount', 'TaxAmount', 'CostOfSales'],
  GeneralLedger: ['Amount', 'DebitAmount', 'CreditAmount', 'BudgetAmount', 'FxAmount'],
  Budget: ['BudgetAmount', 'ForecastAmount', 'PriorForecast'],
  Inventory: ['OnHandQty', 'OnHandValue', 'ReorderPoint', 'InTransitQty'],
};
const DIM_ATTR = {
  Date: ['Date', 'Year', 'Quarter', 'Month', 'MonthName', 'FiscalYear', 'FiscalPeriod'],
  Account: ['AccountName', 'AccountType', 'PnLLevel1', 'PnLLevel2', 'PnLLevel3'],
  Customer: ['CustomerName', 'Segment', 'Industry', 'City', 'Country'],
  Product: ['ProductName', 'Category', 'Subcategory', 'Brand'],
  Store: ['StoreName', 'Format', 'City'],
  Department: ['DepartmentName', 'Division'],
};

const nodes = [];
const edges = [];
const seen = new Set();
function node(ref, name, kind, table, extra) {
  if (seen.has(ref)) return ref;
  seen.add(ref);
  nodes.push({ ref, name, kind, table: table ?? null, isHidden: !!(extra && extra.isHidden), detail: (extra && extra.detail) ?? null });
  return ref;
}
function edge(from, to, kind) { edges.push({ from, to, kind }); }

// ---- sources ----------------------------------------------------------------------------------
const SOURCES = [
  ['source:connector/Sql.Database', 'Sql.Database', 'M connector'],
  ['source:connector/Fabric.Warehouse', 'Fabric.Warehouse', 'M connector'],
  ['source:expr/Staging GL', 'Staging GL', 'shared expression'],
  ['source:expr/Staging Sales', 'Staging Sales', 'shared expression'],
  ['source:expr/FX Rates', 'FX Rates', 'shared expression'],
  ['source:connector/SharePoint.Files', 'SharePoint.Files', 'M connector'],
];
for (const [ref, name, detail] of SOURCES) node(ref, name, 'source', null, { detail });

// ---- tables + columns -------------------------------------------------------------------------
const columnsByTable = {};
const allTables = [...FACTS, ...DIMS];
allTables.forEach((t, ti) => {
  node(`table:${t}`, t, 'table');
  // wire a couple of sources to each table (round-robin) so the "source" edge kind is present + walkable
  edge(SOURCES[ti % SOURCES.length][0], `table:${t}`, 'source');
  if (ti % 3 === 0) edge(SOURCES[(ti + 2) % SOURCES.length][0], `table:${t}`, 'source');

  const cols = [];
  // key column (hidden — the realistic "hide your keys" state)
  const keyRef = node(`column:${t}/${t}Key`, `${t}Key`, 'column', t, { isHidden: true });
  edge(`table:${t}`, keyRef, 'contains');
  cols.push(keyRef);

  const attrs = FACT_NUM[t] || DIM_ATTR[t] || ['Name', 'Code'];
  attrs.forEach((c) => {
    const ref = node(`column:${t}/${c}`, c, 'column', t);
    edge(`table:${t}`, ref, 'contains');
    cols.push(ref);
  });
  // facts carry FK columns to a handful of dims
  if (FACTS.includes(t)) {
    ['Date', 'Account', 'Customer', 'Product', 'Store', 'Department'].forEach((d) => {
      const ref = node(`column:${t}/${d}Key`, `${d}Key`, 'column', t, { isHidden: true });
      edge(`table:${t}`, ref, 'contains');
      cols.push(ref);
    });
  }
  columnsByTable[t] = cols;
});

// ---- relationships (star: each fact → its dims via the FK column → the dim key column) ---------
FACTS.forEach((f) => {
  ['Date', 'Account', 'Customer', 'Product', 'Store', 'Department'].forEach((d) => {
    const fk = `column:${f}/${d}Key`;
    const pk = `column:${d}/${d}Key`;
    if (seen.has(fk) && seen.has(pk)) edge(fk, pk, 'relationship');
  });
});
// a couple of snowflake outriggers
edge('column:Customer/CustomerKey', 'column:Region/RegionKey', 'relationship');
edge('column:Store/StoreKey', 'column:Region/RegionKey', 'relationship');

// ---- measures (base → derived → time-intel → KPI), building real dependency chains ------------
const TARGET_MEASURES = 340;
const measureRefs = [];
function addMeasure(name, table, deps) {
  const ref = node(`measure:${table}/${name}`, name, 'measure', table);
  edge(`table:${table}`, ref, 'contains');                // home-table containment (engine emits this)
  for (const dep of deps) if (seen.has(dep)) edge(ref, dep, 'dependsOn');
  measureRefs.push(ref);
  return ref;
}

// 1) base measures — SUM of each numeric fact column
FACTS.forEach((f) => {
  (FACT_NUM[f] || []).forEach((c) => addMeasure(`Sum of ${c}`, f, [`column:${f}/${c}`]));
});
// a few DISTINCTCOUNT bases on dim keys
['Customer', 'Product', 'Store', 'Vendor'].forEach((d) => addMeasure(`${d} Count`, 'Sales', [`column:${d}/${d}Key`]));

// 2) grow the population with derived / time-intel / KPI measures that reference earlier measures (deep DAG)
const VERBS = ['Total', 'Net', 'Avg', 'YTD', 'QTD', 'MTD', 'PY', 'YoY', 'MoM', 'vs Budget', 'Margin', 'Variance', '% of Total', 'Rolling 3M', 'Rolling 12M'];
let vi = 0;
while (measureRefs.length < TARGET_MEASURES) {
  const idx = measureRefs.length;
  const table = FACTS[idx % FACTS.length];
  const verb = VERBS[vi % VERBS.length]; vi++;
  // Deterministic POWER-LAW dependency shape (like a real model): one big hub (~1 in 5 depend on the first base
  // measure, à la [Total Sales]), a dozen moderate hubs, and the rest deep chains off recent measures. Keeps the
  // max fan-in realistic (~65, not 320) so the views behave like a genuine 360-measure model.
  let d1;
  if (idx % 5 === 0) d1 = measureRefs[0];                                   // the one big hub
  else if (idx % 3 === 0) d1 = measureRefs[idx % 12];                        // a dozen moderate hubs
  else d1 = measureRefs[Math.max(0, idx - 1 - (idx % 25))];                  // chains off recent measures
  const d2 = (idx % 2 === 0) ? measureRefs[Math.max(0, idx - 2 - ((idx * 7) % 30))] : null;
  const deps = [d1];
  if (d2 && d2 !== d1) deps.push(d2);
  if (['YTD', 'QTD', 'MTD', 'PY', 'YoY', 'MoM', 'Rolling 3M', 'Rolling 12M'].includes(verb)) deps.push('column:Date/Date');
  const base = nodes.find((n) => n.ref === d1);
  const stem = base ? base.name.replace(/^Sum of /, '').replace(/^(Total|Net|Avg) /, '') : `Measure ${idx}`;
  addMeasure(`${verb} ${stem} ${idx}`, table, deps);
}

// ---- unused (safe-to-remove) — reference REAL refs so the tree's verdict dots line up ----------
const unused = {
  items: [
    { ref: 'column:Customer/Industry', name: 'Industry', kind: 'column', table: 'Customer', isHidden: false, verdict: 'safe', refCount: 0, blockedBy: [], reason: 'No measure/relationship/hierarchy/sort-by uses this column (model-only).' },
    { ref: 'column:Product/Brand', name: 'Brand', kind: 'column', table: 'Product', isHidden: false, verdict: 'safe', refCount: 0, blockedBy: [], reason: 'No measure/relationship/hierarchy/sort-by uses this column (model-only).' },
    { ref: 'column:Account/PnLLevel3', name: 'PnLLevel3', kind: 'column', table: 'Account', isHidden: false, verdict: 'caution', refCount: 1, blockedBy: ['GeneralLedger (partition coverage)'], reason: "Referenced by an object whose live-ness can't be determined offline — verify before removing." },
    { ref: measureRefs[measureRefs.length - 1], name: nodes.find((n) => n.ref === measureRefs[measureRefs.length - 1]).name, kind: 'measure', table: 'Inventory', isHidden: true, verdict: 'usedByUnusedOnly', refCount: 1, blockedBy: ['(a hidden staging measure)'], reason: 'Referenced only by objects that are themselves unused (hidden/dead).' },
  ],
  safeCount: 2, usedByUnusedOnlyCount: 1, cautionCount: 1,
  caveat: 'Model-only — published-report field usage is not yet included. Verify against report usage before deleting.',
};

// ---- report analysis (with visuals[]) — usedRefs reference real measures/columns ---------------
const M = (i) => measureRefs[i % measureRefs.length];
const reportAnalysis = {
  reports: [
    {
      path: 'C:/pbip/Executive.Report', name: 'Executive Summary', read: true, fieldCount: 9, unresolved: 0,
      usedRefs: [M(30), M(31), M(50), 'column:Date/Year', 'column:Account/PnLLevel1'], extensionMeasures: ['Report Rank'],
      visuals: [
        { page: 'Overview', visual: 'Revenue KPI', visualType: 'card', usedRefs: [M(30), M(0)] },
        { page: 'Overview', visual: 'Revenue by Month', visualType: 'columnChart', usedRefs: [M(31), 'column:Date/Year'] },
        { page: 'Overview', visual: 'P&L by Level', visualType: 'matrix', usedRefs: [M(50), 'column:Account/PnLLevel1'] },
        { page: 'Drivers', visual: 'Margin trend', visualType: 'lineChart', usedRefs: [M(12), M(13)] },
      ],
    },
    {
      path: 'C:/pbip/SalesOps.Report', name: 'Sales Operations', read: true, fieldCount: 7, unresolved: 1,
      usedRefs: [M(0), M(1), M(2), 'column:Customer/Segment'], extensionMeasures: [],
      visuals: [
        { page: 'Sales', visual: 'Sales by Segment', visualType: 'barChart', usedRefs: [M(0), 'column:Customer/Segment'] },
        { page: 'Sales', visual: 'Top products', visualType: 'table', usedRefs: [M(1), 'column:Product/Category'] },
        { page: 'Detail', visual: 'Order detail', visualType: 'table', usedRefs: [M(2)] },
      ],
    },
    {
      path: 'C:/pbip/Ops.Report', name: 'Ops (paginated)', read: false, fieldCount: 0, unresolved: 0,
      usedRefs: [], extensionMeasures: [], error: 'Paginated (RDL) report — its field usage is not parsed.',
    },
  ],
  reportsRead: 2, reportsUnreadable: 1,
  modelFieldsUsed: [M(0), M(1), M(2), M(30), M(31), M(50), M(12), M(13), 'column:Date/Year', 'column:Account/PnLLevel1', 'column:Customer/Segment', 'column:Product/Category'],
  unused: { items: unused.items.slice(0, 3), safeCount: 2, usedByUnusedOnlyCount: 0, cautionCount: 1, caveat: 'Includes field usage from 2 report(s); 1 report(s) could not be read (paginated/RDL).' },
  caveat: 'Includes field usage from 2 report(s); 1 report(s) could not be read (paginated/RDL, blocked by a sensitivity label, or unreadable).',
};

const lineage = { nodes, edges, caveat: unused.caveat };

// A deterministic low-degree measure + one neighbour — a stable, unambiguous target for the driver's walk test
// (searchable by its unique name, then walk to the named neighbour in the focused view).
const _deg = new Map();
for (const e of edges) { _deg.set(e.from, (_deg.get(e.from) || 0) + 1); _deg.set(e.to, (_deg.get(e.to) || 0) + 1); }
const _nbr = new Map();
for (const e of edges) { (_nbr.get(e.from) || _nbr.set(e.from, []).get(e.from)).push(e.to); (_nbr.get(e.to) || _nbr.set(e.to, []).get(e.to)).push(e.from); }
const _leafRef = measureRefs.find((r) => (_deg.get(r) || 0) >= 2 && (_deg.get(r) || 0) <= 3) || measureRefs[measureRefs.length - 1];
const _leafNbr = (_nbr.get(_leafRef) || []).find((n) => n.startsWith('measure:') || n.startsWith('column:')) || (_nbr.get(_leafRef) || [])[0];
const _nameOf = (ref) => nodes.find((n) => n.ref === ref)?.name;
const leaf = { ref: _leafRef, name: _nameOf(_leafRef), neighborRef: _leafNbr, neighborName: _nameOf(_leafNbr) };

export function makeFinanceLineage() {
  return {
    lineage,
    unused,
    reportAnalysis,
    stats: {
      nodes: nodes.length, edges: edges.length,
      measures: measureRefs.length, tables: allTables.length,
      columns: nodes.filter((n) => n.kind === 'column').length, sources: SOURCES.length,
    },
    // handy refs for the driver to drive deterministic clicks
    refs: {
      // a high-degree base measure (many dependants) — good pin / tree-root target
      hubMeasure: 'measure:Sales/Sum of SalesAmount',
      factTable: 'table:Sales',
      dimTable: 'table:Date',
      measureSample: measureRefs.slice(0, 12),
      leaf,   // { ref, name, neighborRef, neighborName } — a stable walk target
    },
  };
}

// Allow `node lineage-fixture.mjs` to print the stats (sanity check).
if (import.meta.url === `file://${process.argv[1]}` || process.argv[1]?.endsWith('lineage-fixture.mjs')) {
  const f = makeFinanceLineage();
  console.log(JSON.stringify(f.stats, null, 2));
}
