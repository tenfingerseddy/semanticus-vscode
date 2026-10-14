// A monitoring-style lineage fixture shaped like a large Fabric/Power BI monitoring model: about 40 tables
// (8 facts that share 5 to 8 lookup tables, a few fact-to-fact links, hidden calculated tables), 60+ relationships
// and 300+ measures, many of which depend on other measures. Deterministic (index based, no Math.random) so a timing
// run is reproducible. Same wire shape as lineage-fixture.mjs: { lineage, stats }.
//
// Names are generic monitoring words (no real names). Used by lineage-perf-drive.mjs.

const FACTS = ['Activity Event', 'Capacity Operation', 'Refresh History', 'Query Log', 'Item Usage', 'Alert Event', 'Job Run', 'Storage Snapshot'];
const LOOKUPS = [
  'Workspace', 'Item', 'Capacity', 'Date', 'Hour', 'Principal', 'Domain', 'Operation Type', 'Region', 'Status',
  'Item Type', 'SKU', 'Owner', 'Tenant', 'Environment', 'Tag', 'Service', 'Severity', 'Gateway', 'Dataset Tier',
  'Country', 'Department', 'Cost Centre', 'Licence', 'Client App', 'Device', 'Time Zone', 'Retention Class',
];
const CALC_TABLES = ['Measure Table', 'Rank Helper', 'Window Helper', 'Parameter Selector'];
// Each fact links to a deterministic 5 to 8 lookups (Date, Workspace, Item, Capacity always shared).
const SHARED = ['Date', 'Workspace', 'Item', 'Capacity'];

export function makeMonitoringLineage({ tables: tableLimit } = {}) {
  const nodes = [], edges = [], seen = new Set();
  const node = (ref, name, kind, table, extra) => {
    if (seen.has(ref)) return ref; seen.add(ref);
    nodes.push({ ref, name, kind, table: table ?? null, isHidden: !!(extra && extra.isHidden), detail: (extra && extra.detail) ?? null });
    return ref;
  };
  const edge = (from, to, kind) => edges.push({ from, to, kind });

  const SOURCES = ['Fabric.Warehouse', 'Sql.Database', 'Staging Admin API', 'Staging Capacity Metrics'];
  SOURCES.forEach((s, i) => node(`source:${i}/${s}`, s, 'source', null, { detail: 'shared expression' }));

  const facts = FACTS.slice(0, tableLimit ? Math.min(FACTS.length, tableLimit) : FACTS.length);
  const lookups = LOOKUPS.slice(0, tableLimit ? Math.max(0, tableLimit - facts.length) : LOOKUPS.length);
  const calcs = tableLimit ? [] : CALC_TABLES;
  const all = [...facts, ...lookups, ...calcs];
  const cols = {};
  const NUM = ['Duration Ms', 'Row Count', 'CU Seconds', 'Bytes', 'Error Count', 'Throttle Ms', 'Queue Ms', 'Cost'];
  const TXT = ['Name', 'Code', 'Category', 'Group', 'Label', 'Owner', 'Notes', 'Tier', 'Kind', 'Description'];

  all.forEach((t, ti) => {
    node(`table:${t}`, t, calcs.includes(t) ? 'calcTable' : 'table');
    edge(`source:${ti % SOURCES.length}/${SOURCES[ti % SOURCES.length]}`, `table:${t}`, 'source');
    const list = [];
    const add = (c, hidden, kind = 'column') => { const r = node(`column:${t}/${c}`, c, kind, t, { isHidden: hidden }); edge(`table:${t}`, r, 'contains'); list.push(r); };
    add(`${t} Key`, true);
    if (facts.includes(t)) {
      NUM.forEach((c) => add(c, false));
      ['Timestamp', 'Event Id', 'Correlation Id', 'Status Code'].forEach((c) => add(c, false));
      add('Is Failure', false, 'calcColumn');
    } else if (lookups.includes(t)) {
      for (let i = 0; i < 7 + (ti % 6); i++) add(TXT[i % TXT.length] + (i >= TXT.length ? ' ' + i : ''), false);
    } else {
      add('Value', false, 'calcColumn');
    }
    cols[t] = list;
  });

  // Relationships: each fact has its own FK column to 5 to 8 lookups (4 shared + 1 to 4 own), as column > column.
  let relCount = 0;
  facts.forEach((f, fi) => {
    const own = lookups.filter((l) => !SHARED.includes(l));
    const picks = [...SHARED.filter((s) => lookups.includes(s)), ...own.slice((fi * 3) % Math.max(1, own.length), ((fi * 3) % Math.max(1, own.length)) + 1 + (fi % 4))];
    [...new Set(picks)].forEach((l) => {
      const fk = node(`column:${f}/${l} Key`, `${l} Key`, 'column', f, { isHidden: true });
      if (!edges.some((e) => e.from === `table:${f}` && e.to === fk)) edge(`table:${f}`, fk, 'contains');
      edge(fk, `column:${l}/${l} Key`, 'relationship'); relCount++;
    });
  });
  // fact-to-fact links and a few snowflake outriggers
  for (let i = 0; i + 1 < facts.length; i += 2) {
    const a = node(`column:${facts[i]}/Correlation Id`, 'Correlation Id', 'column', facts[i]);
    const b = node(`column:${facts[i + 1]}/Correlation Id`, 'Correlation Id', 'column', facts[i + 1]);
    edge(a, b, 'relationship'); relCount++;
  }
  [['Item', 'Workspace'], ['Workspace', 'Capacity'], ['Principal', 'Department'], ['Item', 'Item Type'], ['Capacity', 'SKU'], ['Workspace', 'Domain'], ['Owner', 'Department']].forEach(([a, b]) => {
    if (!lookups.includes(a) || !lookups.includes(b)) return;
    const fk = node(`column:${a}/${b} Key`, `${b} Key`, 'column', a, { isHidden: true });
    edge(`table:${a}`, fk, 'contains'); edge(fk, `column:${b}/${b} Key`, 'relationship'); relCount++;
  });

  // Measures: bases off the numeric fact columns, then derived measures that depend on measures (deep DAG, hubs).
  const TARGET = tableLimit ? Math.max(12, tableLimit * 6) : 330;
  const measureRefs = [];
  const home = (i) => (calcs.length ? calcs[0] : facts[i % facts.length]);
  const addMeasure = (name, table, deps) => {
    const ref = node(`measure:${table}/${name}`, name, 'measure', table);
    edge(`table:${table}`, ref, 'contains');
    for (const d of deps) if (seen.has(d) && d !== ref) edge(ref, d, 'dependsOn');
    measureRefs.push(ref); return ref;
  };
  facts.forEach((f) => NUM.forEach((c) => addMeasure(`Total ${c} ${f}`, home(measureRefs.length), [`column:${f}/${c}`])));
  const VERBS = ['Avg', 'Max', 'P95', 'YTD', 'MTD', 'PY', 'YoY', 'Share', 'Rolling 7D', 'Rolling 28D', 'Rate', 'Per Hour', 'Per User', 'Failure %', 'Throttle %'];
  let vi = 0;
  while (measureRefs.length < TARGET) {
    const idx = measureRefs.length;
    const verb = VERBS[vi++ % VERBS.length];
    let d1;
    if (idx % 5 === 0) d1 = measureRefs[0];
    else if (idx % 3 === 0) d1 = measureRefs[idx % 14];
    else d1 = measureRefs[Math.max(0, idx - 1 - (idx % 25))];
    const deps = [d1];
    if (idx % 2 === 0) deps.push(measureRefs[Math.max(0, idx - 2 - ((idx * 7) % 30))]);
    if (['YTD', 'MTD', 'PY', 'YoY', 'Rolling 7D', 'Rolling 28D'].includes(verb) && lookups.includes('Date')) deps.push('column:Date/Date Key');
    const base = nodes.find((n) => n.ref === d1);
    addMeasure(`${verb} ${base ? base.name.replace(/^Total /, '') : 'Metric'} ${idx}`, home(idx), [...new Set(deps)]);
  }
  return {
    lineage: { nodes, edges, caveat: null },
    unused: { items: [], safeCount: 0, usedByUnusedOnlyCount: 0, cautionCount: 0 },
    reportAnalysis: null,
    stats: { nodes: nodes.length, edges: edges.length, measures: measureRefs.length, tables: all.length, relationships: relCount },
    refs: { hubMeasure: measureRefs[0], measureSample: measureRefs.slice(0, 12) },
  };
}

if (process.argv[1]?.endsWith('lineage-monitoring-fixture.mjs')) {
  console.log(JSON.stringify(makeMonitoringLineage().stats), JSON.stringify(makeMonitoringLineage({ tables: 5 }).stats));
}
