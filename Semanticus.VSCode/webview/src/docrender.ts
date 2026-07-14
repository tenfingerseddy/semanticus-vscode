// Pure documentation renderer: turns a getDocModel snapshot (DocModelDto) into a self-contained, branded
// HTML document and a GitHub-flavoured Markdown variant. NO React, NO DOM, NO engine calls — just strings —
// so it renders the live preview AND the exported file from one code path, and the uishot harness can drive it.
//
// SECURITY: every value that originates from the model (names, DAX, descriptions, narrative) is HTML-escaped
// before it reaches the output. Narrative is authored Markdown, so it goes through a conservative Markdown→HTML
// pass that escapes first and sanitises link URLs (no javascript:/data: links) — the model is untrusted input
// even though it's the user's own (a name could contain "</style><script>…").

import dagre from '@dagrejs/dagre';

// ---- wire types (camelCase mirror of Semanticus.Engine/Protocol.cs DocModelDto) --------------------

export interface GraphTable { ref: string; name: string; isHidden: boolean; isDateTable: boolean; isCalculated: boolean; columns: number; measures: number; keyColumns: string[]; hasDescription: boolean; }
export interface GraphRelationship { name: string; fromTable: string; fromColumn: string; toTable: string; toColumn: string; fromCardinality: string; toCardinality: string; crossFilter: string; isActive: boolean; }
export interface ModelGraph { tables: GraphTable[]; relationships: GraphRelationship[]; }

export interface DocSection { key: string; markdown: string; }
export interface DocNarrative { author?: string; updatedUtc?: string; sections: DocSection[]; }
export interface DocLevel { name: string; ordinal: number; column?: string; }
export interface DocHierarchy { name: string; description?: string; isHidden: boolean; levels: DocLevel[]; }
export interface DocPartition { name: string; mode?: string; sourceType?: string; source?: string; entityName?: string; schemaName?: string; dataSource?: string; refreshedTime?: string; }
export interface DocCalcItem { name: string; ordinal: number; expression?: string; formatStringExpression?: string; description?: string; }
export interface DocKpi { measureRef?: string; measure?: string; targetExpression?: string; statusExpression?: string; statusGraphic?: string; trendExpression?: string; trendGraphic?: string; }
export interface DocDataSource { name: string; type?: string; }
export interface DocExpression { name: string; kind?: string; expression?: string; description?: string; }
export interface MeasureRow { ref: string; name: string; table: string; displayFolder?: string; formatString?: string; isHidden: boolean; hasDescription: boolean; description?: string; expression?: string; narrative?: DocNarrative | null; }
export interface ColumnRow { ref: string; name: string; table: string; dataType?: string; displayFolder?: string; formatString?: string; summarizeBy?: string; dataCategory?: string; isKey: boolean; isHidden: boolean; isCalculated: boolean; hasDescription: boolean; description?: string; expression?: string; sortByColumn?: string; }
export interface DocTable { ref: string; name: string; description?: string; isHidden: boolean; isCalculated: boolean; isDateTable: boolean; isCalculationGroup: boolean; calcGroupPrecedence: number; dataCategory?: string; rowCount?: number | null; hierarchies: DocHierarchy[]; partitions: DocPartition[]; calcItems: DocCalcItem[]; narrative?: DocNarrative | null; }
export interface DocModelHeader { name?: string; description?: string; culture?: string; defaultMode?: string; compatibilityLevel: number; source?: string; tableCount: number; measureCount: number; columnCount: number; relationshipCount: number; liveConnected: boolean; liveKind?: string; generatedUtc?: string; }
export interface TablePermissionInfo { table: string; filterExpression: string; }
export interface RoleInfo { name: string; description?: string; modelPermission?: string; tableFilters: TablePermissionInfo[]; members: string[]; }
export interface CategoryScore { category: string; score: number; weight: number; applicable: number; violations: number; hasRules: boolean; }
export interface Scorecard { overall: number; rawOverall: number; grade: string; gatedBy: string[]; categories: CategoryScore[]; safeFixCount: number; }
export interface BpaScorecard { ruleCount: number; violationCount: number; autoFixable: number; }
export interface VpaqTable { name: string; size: number; rows: number | null; pctOfModel: number; columns?: number; }
export interface VpaqColumn { table: string; column: string; totalSize: number; encoding: string; pctOfModel: number; }
export interface VpaqReport { modelSize: number; columnCount: number; tables: VpaqTable[]; topColumns: VpaqColumn[]; isDirectLake?: boolean; caveat?: string; error?: string; }
export interface PrepForAiConfig { hasLinguisticSchema: boolean; aiInstructions?: string; aiInstructionsLength: number; aiSchemaExcludedFields: number; sourceReadable: boolean; qnaEnabled?: boolean | null; verifiedAnswersPresent: boolean; verifiedAnswerCount: number; }

export interface DocModelDto {
  header: DocModelHeader;
  graph: ModelGraph;
  tables: DocTable[];
  measures: MeasureRow[];
  columns: ColumnRow[];
  roles: RoleInfo[];
  kpis: DocKpi[];
  dataSources: DocDataSource[];
  expressions: DocExpression[];
  modelNarrative?: DocNarrative | null;
  readiness?: Scorecard | null;
  bpa?: BpaScorecard | null;
  prepForAi?: PrepForAiConfig | null;
  storage?: VpaqReport | null;
  storageAvailable: boolean;
}

// ---- config + branding ----------------------------------------------------------------------------

/** Which sections / details to include. Each flag gates a block; the UI binds checkboxes to these. */
export interface DocConfig {
  hiddenObjects: boolean;     // include hidden tables / columns / measures
  daxExpressions: boolean;    // include measure / column / calc-item DAX
  perTableDetail: boolean;    // a detail section per table
  columnsDetail: boolean;     // the columns table inside each table's detail
  hierarchies: boolean;       // hierarchies inside table detail + the hierarchies summary
  diagram: boolean;           // the relationship (ER) diagram as inline SVG
  relationships: boolean;     // the relationships table
  measuresIndex: boolean;     // the global measures index
  calcGroups: boolean;        // calculation groups + items
  kpis: boolean;              // KPIs
  rls: boolean;               // roles + row-level security
  lineage: boolean;           // partitions / M source / data sources / shared expressions
  storageStats: boolean;      // VertiPaq storage (auto-suppressed when storage is unavailable)
  readinessScorecard: boolean;// AI-readiness scorecard
  bpaScorecard: boolean;      // Best-Practice-Analyzer summary
  prepForAi: boolean;         // Prep-for-AI surface (Q&A / AI instructions)
  narrative: boolean;         // authored Documentation narrative (model / table / measure)
}

/** Branding for the cover + theme. Injected as CSS custom properties and cover content. */
export interface DocBranding {
  title: string;
  subtitle: string;
  companyName: string;
  author: string;
  date: string;             // display date (caller supplies — the renderer stays pure/deterministic)
  logoDataUri: string;      // a data: URI for the cover logo, or '' for none
  accentColor: string;      // hex, e.g. '#4c6ef5'
  footerText: string;
  theme: 'light' | 'dark';  // doc theme (default light — print-friendly)
}

export const DEFAULT_DOC_CONFIG: DocConfig = {
  hiddenObjects: false, daxExpressions: true, perTableDetail: true, columnsDetail: true, hierarchies: true,
  diagram: true, relationships: true, measuresIndex: true, calcGroups: true, kpis: true, rls: true,
  lineage: true, storageStats: true, readinessScorecard: true, bpaScorecard: true, prepForAi: true, narrative: true,
};

export const DEFAULT_DOC_BRANDING: DocBranding = {
  title: '', subtitle: 'Semantic Model Documentation', companyName: '', author: '', date: '',
  logoDataUri: '', accentColor: '#4c6ef5', footerText: '', theme: 'light',
};

// ---- small helpers --------------------------------------------------------------------------------

/** HTML-escape. The single chokepoint for untrusted model text → markup. */
export function esc(s: unknown): string {
  if (s === null || s === undefined) return '';
  return String(s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

const NL = (s: unknown) => (s === null || s === undefined ? '' : String(s));
const has = (s: unknown): s is string => typeof s === 'string' && s.trim().length > 0;

function fmtBytes(n: number): string {
  if (!n || n < 0) return '0 B';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0, v = n;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return `${v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)} ${u[i]}`;
}
function fmtNum(n: number | null | undefined): string {
  if (n === null || n === undefined) return '';
  return n.toLocaleString('en-US');
}

// Only allow safe link schemes in authored Markdown (block javascript:, data:, vbscript:, etc.).
function sanitizeUrl(url: string): string {
  const u = NL(url).trim();
  if (/^(https?:\/\/|mailto:|tel:|#|\/|\.{0,2}\/)/i.test(u)) return u;
  if (/^[a-z0-9.\-_~%]+$/i.test(u)) return u;     // a bare relative path / fragment, no scheme
  return '#';
}

// Minimal, conservative Markdown → safe HTML. Input is escaped FIRST, then a small set of inline + block
// transforms is applied to the escaped text — so no raw markup from the model can ever survive.
export function mdToHtml(md: string): string {
  if (!has(md)) return '';
  const src = esc(md).replace(/\r\n/g, '\n').replace(/\r/g, '\n');
  const lines = src.split('\n');
  const out: string[] = [];
  let i = 0;
  let para: string[] = [];
  const flushPara = () => { if (para.length) { out.push(`<p>${inline(para.join(' '))}</p>`); para = []; } };
  let list: { type: 'ul' | 'ol'; items: string[] } | null = null;
  const flushList = () => { if (list) { out.push(`<${list.type}>${list.items.map((x) => `<li>${inline(x)}</li>`).join('')}</${list.type}>`); list = null; } };

  while (i < lines.length) {
    const line = lines[i];
    // fenced code block ``` … ```
    const fence = line.match(/^\s*```(.*)$/);
    if (fence) {
      flushPara(); flushList();
      const body: string[] = [];
      i++;
      while (i < lines.length && !/^\s*```/.test(lines[i])) { body.push(lines[i]); i++; }
      i++; // skip closing fence
      out.push(`<pre class="doc-code"><code>${body.join('\n')}</code></pre>`);
      continue;
    }
    const heading = line.match(/^(#{1,6})\s+(.*)$/);
    if (heading) { flushPara(); flushList(); const lvl = Math.min(6, heading[1].length); out.push(`<h${lvl} class="doc-mdh">${inline(heading[2])}</h${lvl}>`); i++; continue; }
    const uli = line.match(/^\s*[-*+]\s+(.*)$/);
    const oli = line.match(/^\s*\d+[.)]\s+(.*)$/);
    if (uli) { flushPara(); if (!list || list.type !== 'ul') { flushList(); list = { type: 'ul', items: [] }; } list.items.push(uli[1]); i++; continue; }
    if (oli) { flushPara(); if (!list || list.type !== 'ol') { flushList(); list = { type: 'ol', items: [] }; } list.items.push(oli[1]); i++; continue; }
    if (/^\s*$/.test(line)) { flushPara(); flushList(); i++; continue; }
    if (/^\s*&gt;\s?/.test(line)) { flushPara(); flushList(); out.push(`<blockquote>${inline(line.replace(/^\s*&gt;\s?/, ''))}</blockquote>`); i++; continue; }
    para.push(line.trim());
    i++;
  }
  flushPara(); flushList();
  return out.join('\n');

  // inline transforms over already-escaped text
  function inline(s: string): string {
    return s
      .replace(/`([^`]+)`/g, (_m, c) => `<code>${c}</code>`)
      .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
      .replace(/__([^_]+)__/g, '<strong>$1</strong>')
      .replace(/(^|[^*])\*([^*\n]+)\*/g, '$1<em>$2</em>')
      .replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_m, t, url) => `<a href="${esc(sanitizeUrl(url))}" target="_blank" rel="noopener noreferrer">${t}</a>`);
  }
}

// ---- ER diagram → inline SVG ----------------------------------------------------------------------

const NODE_W = 190, NODE_H = 84;
const COMPONENT_GAP_X = 48, COMPONENT_GAP_Y = 56, DIAGRAM_ROW_WIDTH = 1180;
// Crow's-foot marker path data (mirrors diagram.tsx): "many" → fork, "one" → bar; start/end variants.
const SVG_MARKERS: { id: string; refX: number; d: string }[] = [
  { id: 'many-end', refX: 11, d: 'M1,6 L11,1 M1,6 L11,11' },
  { id: 'one-end', refX: 11, d: 'M8,1.5 L8,10.5' },
  { id: 'many-start', refX: 1, d: 'M11,6 L1,1 M11,6 L1,11' },
  { id: 'one-start', refX: 1, d: 'M4,1.5 L4,10.5' },
];

interface DiagramPoint { x: number; y: number; }
interface DiagramComponent {
  tables: GraphTable[];
  relationships: { relationship: GraphRelationship; index: number }[];
}
interface DiagramComponentLayout extends DiagramComponent {
  width: number;
  height: number;
  nodes: Map<string, DiagramPoint>;
  edges: Map<number, DiagramPoint[]>;
  offsetX: number;
  offsetY: number;
}

/** Build a self-contained ER diagram as an inline <svg> string (dagre layout + crow's-foot markers).
 *  Pure: no React Flow, no DOM. Colours come from CSS custom properties so the doc theme drives them.
 *
 *  Dagre stacks disconnected nodes in one narrow column. A responsive width then magnifies that column into
 *  giant cards and a multi-screen-tall pseudo-diagram. Lay out each connected component independently, put the
 *  relationship-bearing components first, and tile isolated tables below them so preview and export stay useful. */
export function graphToSvg(graph: ModelGraph, opts?: { includeHidden?: boolean }): string {
  const tables = (graph?.tables ?? []).filter((t) => opts?.includeHidden || !t.isHidden);
  if (tables.length === 0) return '<p class="doc-muted">No tables to diagram.</p>';
  const names = new Set(tables.map((t) => t.name));
  const rels = (graph?.relationships ?? []).filter((r) => names.has(r.fromTable) && names.has(r.toTable));
  const components = relationshipComponents(tables, rels).map(layoutDiagramComponent);
  const connected = components.filter((c) => c.relationships.length > 0)
    .sort((a, b) => b.tables.length - a.tables.length || a.tables[0].name.localeCompare(b.tables[0].name));
  const isolated = components.filter((c) => c.relationships.length === 0)
    .sort((a, b) => a.tables[0].name.localeCompare(b.tables[0].name));
  const layouts = [...connected, ...isolated];
  const { width: W, height: H } = packDiagramComponents(connected, isolated);
  const pos = new Map<string, DiagramPoint>();
  const edgePoints = new Map<number, DiagramPoint[]>();
  layouts.forEach((c) => {
    c.nodes.forEach((p, name) => pos.set(name, { x: p.x + c.offsetX, y: p.y + c.offsetY }));
    c.edges.forEach((points, index) => edgePoints.set(index, points.map((p) => ({ x: p.x + c.offsetX, y: p.y + c.offsetY }))));
  });

  const defs = `<defs>${SVG_MARKERS.map((s) =>
    `<marker id="doc-${s.id}" viewBox="0 0 12 12" markerWidth="15" markerHeight="15" refX="${s.refX}" refY="6" orient="auto" markerUnits="userSpaceOnUse"><path d="${s.d}" fill="none" stroke="var(--doc-accent)" stroke-width="1.3"/></marker>`).join('')}</defs>`;

  const edges = rels.map((r, index) => {
    const points = edgePoints.get(index) ?? [];
    if (points.length < 2) return '';
    const path = points.map((p, i) => `${i ? 'L' : 'M'}${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' ');
    const ms = `doc-${r.fromCardinality === 'Many' ? 'many' : 'one'}-start`;
    const me = `doc-${r.toCardinality === 'Many' ? 'many' : 'one'}-end`;
    const dash = r.isActive ? '' : ' stroke-dasharray="5 4"';
    const op = r.isActive ? '0.85' : '0.5';
    return `<path d="${path}" fill="none" stroke="var(--doc-accent)" stroke-width="1.3" opacity="${op}"${dash} marker-start="url(#${ms})" marker-end="url(#${me})"/>`;
  }).join('');

  const nodes = tables.map((t) => {
    const p = pos.get(t.name)!;
    const sub = `${t.columns} cols · ${t.measures} msr`;
    const tag = t.isDateTable ? 'DATE' : t.isCalculated ? 'CALC' : '';
    return `<g transform="translate(${p.x.toFixed(1)},${p.y.toFixed(1)})">`
      + `<rect width="${NODE_W}" height="${NODE_H}" rx="7" fill="var(--doc-surface)" stroke="var(--doc-border)" stroke-width="1"${t.isHidden ? ' opacity="0.6"' : ''}/>`
      + `<rect width="${NODE_W}" height="24" rx="7" fill="var(--doc-accent)" opacity="0.14"/>`
      + `<text x="10" y="16" font-size="12" font-weight="600" fill="var(--doc-fg)">${esc(trunc(t.name, 26))}</text>`
      + (tag ? `<text x="${NODE_W - 8}" y="16" font-size="8.5" text-anchor="end" fill="var(--doc-muted)">${tag}</text>` : '')
      + `<text x="10" y="42" font-size="10.5" fill="var(--doc-muted)">${esc(sub)}</text>`
      + (t.keyColumns.length ? `<text x="10" y="60" font-size="10" fill="var(--doc-accent)">key ${esc(trunc(t.keyColumns.join(', '), 28))}</text>` : '')
      + `</g>`;
  }).join('');

  return `<svg class="doc-svg" viewBox="0 0 ${W} ${H}" width="100%" style="max-width:${W}px" preserveAspectRatio="xMidYMin meet" role="img" aria-label="Relationship diagram" data-components="${layouts.length}" data-isolated="${isolated.length}">${defs}${edges}${nodes}</svg>`;
}

function relationshipComponents(tables: GraphTable[], rels: GraphRelationship[]): DiagramComponent[] {
  const byName = new Map(tables.map((t) => [t.name, t]));
  const adjacent = new Map(tables.map((t) => [t.name, new Set<string>()]));
  rels.forEach((r) => { adjacent.get(r.fromTable)?.add(r.toTable); adjacent.get(r.toTable)?.add(r.fromTable); });
  const seen = new Set<string>();
  const out: DiagramComponent[] = [];
  for (const table of tables) {
    if (seen.has(table.name)) continue;
    const queue = [table.name], componentNames = new Set<string>();
    seen.add(table.name);
    while (queue.length) {
      const name = queue.shift()!;
      componentNames.add(name);
      for (const next of adjacent.get(name) ?? []) if (!seen.has(next)) { seen.add(next); queue.push(next); }
    }
    out.push({
      tables: [...componentNames].map((name) => byName.get(name)!),
      relationships: rels.map((relationship, index) => ({ relationship, index }))
        .filter(({ relationship }) => componentNames.has(relationship.fromTable) && componentNames.has(relationship.toTable)),
    });
  }
  return out;
}

function layoutDiagramComponent(component: DiagramComponent): DiagramComponentLayout {
  if (component.tables.length === 1) return {
    ...component, width: NODE_W + 32, height: NODE_H + 32,
    nodes: new Map([[component.tables[0].name, { x: 16, y: 16 }]]), edges: new Map(), offsetX: 0, offsetY: 0,
  };
  const g = new dagre.graphlib.Graph({ multigraph: true });
  g.setDefaultEdgeLabel(() => ({}));
  g.setGraph({ rankdir: 'LR', nodesep: 30, ranksep: 90, marginx: 16, marginy: 16 });
  component.tables.forEach((t) => g.setNode(t.name, { width: NODE_W, height: NODE_H }));
  component.relationships.forEach(({ relationship: r, index }) => g.setEdge(r.fromTable, r.toTable, {}, String(index)));
  dagre.layout(g);
  const nodes = new Map<string, DiagramPoint>();
  component.tables.forEach((t) => {
    const n = g.node(t.name);
    nodes.set(t.name, { x: (n?.x ?? NODE_W / 2) - NODE_W / 2, y: (n?.y ?? NODE_H / 2) - NODE_H / 2 });
  });
  const edges = new Map<number, DiagramPoint[]>();
  component.relationships.forEach(({ relationship: r, index }) => {
    const edge = g.edge({ v: r.fromTable, w: r.toTable, name: String(index) }) as { points?: DiagramPoint[] } | undefined;
    edges.set(index, edge?.points ?? []);
  });
  const graphSize = g.graph() as { width?: number; height?: number };
  return {
    ...component, width: Math.ceil(graphSize.width ?? NODE_W + 32), height: Math.ceil(graphSize.height ?? NODE_H + 32),
    nodes, edges, offsetX: 0, offsetY: 0,
  };
}

function packDiagramComponents(connected: DiagramComponentLayout[], isolated: DiagramComponentLayout[]): { width: number; height: number } {
  let x = 0, y = 0, rowHeight = 0, maxWidth = 0;
  const place = (component: DiagramComponentLayout) => {
    if (x > 0 && x + component.width > DIAGRAM_ROW_WIDTH) {
      y += rowHeight + COMPONENT_GAP_Y; x = 0; rowHeight = 0;
    }
    component.offsetX = x; component.offsetY = y;
    x += component.width + COMPONENT_GAP_X;
    rowHeight = Math.max(rowHeight, component.height);
    maxWidth = Math.max(maxWidth, x - COMPONENT_GAP_X);
  };
  connected.forEach(place);
  // Keep the isolated-table grid below the actual relationship graph. Otherwise short isolated cards beside a
  // tall star component create a large blank trench before the next row and hide the useful graph among noise.
  if (connected.length && isolated.length) { y += rowHeight + COMPONENT_GAP_Y; x = 0; rowHeight = 0; }
  isolated.forEach(place);
  return { width: Math.max(1, Math.ceil(maxWidth)), height: Math.max(1, Math.ceil(y + rowHeight)) };
}

const trunc = (s: string, n: number) => (s.length > n ? s.slice(0, n - 1) + '…' : s);

// ---- HTML rendering -------------------------------------------------------------------------------

interface Toc { id: string; title: string; }

export function renderDoc(dto: DocModelDto, config: DocConfig, branding: DocBranding): { html: string; markdown: string } {
  return { html: renderHtml(dto, config, branding), markdown: renderMarkdown(dto, config) };
}

function renderHtml(dto: DocModelDto, cfg: DocConfig, b: DocBranding): string {
  const toc: Toc[] = [];
  const sections: string[] = [];
  const visTables = dto.tables.filter((t) => cfg.hiddenObjects || !t.isHidden);
  const visMeasures = dto.measures.filter((m) => cfg.hiddenObjects || !m.isHidden);
  const visColumns = dto.columns.filter((c) => cfg.hiddenObjects || !c.isHidden);
  const title = has(b.title) ? b.title : (dto.header.name || 'Semantic Model');

  const add = (id: string, tocTitle: string, body: string) => { toc.push({ id, title: tocTitle }); sections.push(`<section id="${id}" class="doc-section"><h2 class="doc-h2">${esc(tocTitle)}</h2>${body}</section>`); };

  // Overview
  add('overview', 'Overview', overviewHtml(dto, cfg));
  // Diagram
  if (cfg.diagram) add('diagram', 'Relationship Diagram', `<div class="doc-diagram">${graphToSvg(dto.graph, { includeHidden: cfg.hiddenObjects })}</div>`);
  // Per-table detail
  if (cfg.perTableDetail && visTables.length) {
    const body = visTables.map((t) => tableDetailHtml(t, dto, cfg)).join('\n');
    add('tables', 'Tables', body);
  }
  // Measures index
  if (cfg.measuresIndex && visMeasures.length) add('measures', 'Measures', measuresHtml(visMeasures, cfg));
  // Relationships
  if (cfg.relationships && dto.graph.relationships.length) add('relationships', 'Relationships', relationshipsHtml(dto.graph));
  // Calc groups
  const calcTables = visTables.filter((t) => t.isCalculationGroup);
  if (cfg.calcGroups && calcTables.length) add('calcgroups', 'Calculation Groups', calcGroupsHtml(calcTables, cfg));
  // KPIs
  if (cfg.kpis && dto.kpis.length) add('kpis', 'KPIs', kpisHtml(dto.kpis, cfg));
  // Roles / RLS
  if (cfg.rls && dto.roles.length) add('roles', 'Roles & Security', rolesHtml(dto.roles));
  // Lineage (partitions / sources / expressions)
  if (cfg.lineage && (dto.dataSources.length || dto.expressions.length)) add('lineage', 'Data Sources & Lineage', lineageHtml(dto));
  // Storage
  if (cfg.storageStats && dto.storageAvailable && dto.storage && !dto.storage.error) add('storage', 'Storage (VertiPaq)', storageHtml(dto.storage));
  // Prep for AI
  if (cfg.prepForAi && dto.prepForAi) add('prepforai', 'Prep for AI', prepForAiHtml(dto.prepForAi));
  // Glossary + Methodology (model narrative sections)
  if (cfg.narrative && dto.modelNarrative) {
    const glossary = dto.modelNarrative.sections.find((s) => s.key === 'glossary');
    const methodology = dto.modelNarrative.sections.find((s) => s.key === 'methodology');
    if (glossary && has(glossary.markdown)) add('glossary', 'Glossary', `<div class="doc-narr">${mdToHtml(glossary.markdown)}</div>`);
    if (methodology && has(methodology.markdown)) add('methodology', 'Methodology', `<div class="doc-narr">${mdToHtml(methodology.markdown)}</div>`);
  }

  const tocHtml = toc.map((t) => `<a href="#${t.id}" class="doc-toc-link">${esc(t.title)}</a>`).join('');
  const cover = coverHtml(dto, cfg, b, title);

  return `<!doctype html><html lang="en"><head><meta charset="utf-8"/><meta name="viewport" content="width=device-width, initial-scale=1"/>`
    + `<title>${esc(title)}: Documentation</title><style>${css(b)}</style></head><body class="doc-theme-${b.theme}">`
    + `<div class="doc-layout">`
    + `<aside class="doc-sidebar"><div class="doc-side-title">${esc(title)}</div>`
    + `<input id="doc-search" class="doc-search" type="search" placeholder="Filter…" oninput="docFilter(this.value)"/>`
    + `<nav class="doc-toc">${tocHtml}</nav></aside>`
    + `<main class="doc-main">${cover}${sections.join('\n')}<footer class="doc-footer">${esc(b.footerText) || esc(b.companyName)}${b.companyName && b.footerText ? ' · ' : ''}${b.companyName && b.footerText ? esc(b.companyName) : ''}<div class="doc-gen">Generated by Semanticus${dto.header.generatedUtc ? ' · ' + esc(dto.header.generatedUtc.slice(0, 10)) : ''}</div></footer></main>`
    + `</div>${searchScript()}</body></html>`;
}

function coverHtml(dto: DocModelDto, cfg: DocConfig, b: DocBranding, title: string): string {
  const grade = cfg.readinessScorecard && dto.readiness ? dto.readiness.grade : '';
  const badge = grade ? `<div class="doc-grade doc-grade-${esc(grade)}">AI-Readiness <b>${esc(grade)}</b></div>` : '';
  return `<header class="doc-cover">`
    + (has(b.logoDataUri) ? `<img class="doc-logo" src="${esc(b.logoDataUri)}" alt="logo"/>` : '')
    + `<div class="doc-cover-title">${esc(title)}</div>`
    + (has(b.subtitle) ? `<div class="doc-cover-sub">${esc(b.subtitle)}</div>` : '')
    + `<div class="doc-cover-meta">`
    + (has(b.companyName) ? `<span>${esc(b.companyName)}</span>` : '')
    + (has(b.author) ? `<span>${esc(b.author)}</span>` : '')
    + (has(b.date) ? `<span>${esc(b.date)}</span>` : '')
    + `<span>${esc(dto.header.name || '')}</span>`
    + `</div>${badge}</header>`;
}

function overviewHtml(dto: DocModelDto, cfg: DocConfig): string {
  const h = dto.header;
  const stats: [string, string][] = [
    ['Tables', fmtNum(h.tableCount)], ['Measures', fmtNum(h.measureCount)], ['Columns', fmtNum(h.columnCount)],
    ['Relationships', fmtNum(h.relationshipCount)], ['Compatibility', String(h.compatibilityLevel || '')],
    ['Culture', NL(h.culture)], ['Default mode', NL(h.defaultMode)],
  ];
  if (cfg.storageStats && dto.storageAvailable && dto.storage && !dto.storage.error) stats.push(['Model size', fmtBytes(dto.storage.modelSize)]);
  const statCards = stats.filter(([, v]) => has(v)).map(([k, v]) => `<div class="doc-stat"><div class="doc-stat-v">${esc(v)}</div><div class="doc-stat-k">${esc(k)}</div></div>`).join('');

  let scorecards = '';
  if (cfg.readinessScorecard && dto.readiness) {
    const r = dto.readiness;
    scorecards += `<div class="doc-scorecard"><span class="doc-grade doc-grade-${esc(r.grade)}">AI-Readiness <b>${esc(r.grade)}</b></span>`
      + `<span class="doc-muted">${Math.round(r.overall)} / 100${r.gatedBy && r.gatedBy.length ? ' · gated by ' + esc(r.gatedBy.join(', ')) : ''}</span></div>`;
    const cats = r.categories.filter((c) => c.hasRules).map((c) => `<tr><td>${esc(c.category)}</td><td class="num">${Math.round(c.score)}</td><td class="num">${fmtNum(c.violations)}</td><td class="num">${fmtNum(c.applicable)}</td></tr>`).join('');
    if (cats) scorecards += `<table class="doc-table"><thead><tr><th>Category</th><th class="num">Score</th><th class="num">Issues</th><th class="num">Checked</th></tr></thead><tbody>${cats}</tbody></table>`;
  }
  if (cfg.bpaScorecard && dto.bpa) {
    scorecards += `<div class="doc-scorecard"><b>Best practices:</b> <span class="doc-muted">${fmtNum(dto.bpa.violationCount)} issue(s) across ${fmtNum(dto.bpa.ruleCount)} rules · ${fmtNum(dto.bpa.autoFixable)} auto-fixable</span></div>`;
  }

  const narr = cfg.narrative && dto.modelNarrative ? sectionMd(dto.modelNarrative, 'overview') : '';
  const desc = has(dto.header.description) ? `<p class="doc-desc">${esc(dto.header.description)}</p>` : '';
  return (narr ? `<div class="doc-narr">${narr}</div>` : '') + desc + `<div class="doc-stats">${statCards}</div>` + scorecards;
}

function tableDetailHtml(t: DocTable, dto: DocModelDto, cfg: DocConfig): string {
  const cols = dto.columns.filter((c) => c.table === t.name && (cfg.hiddenObjects || !c.isHidden));
  const msrs = dto.measures.filter((m) => m.table === t.name && (cfg.hiddenObjects || !m.isHidden));
  const tags = [t.isCalculationGroup ? 'Calc group' : '', t.isCalculated ? 'Calculated' : '', t.isDateTable ? 'Date' : '', t.isHidden ? 'Hidden' : '']
    .filter(Boolean).map((x) => `<span class="doc-tag">${esc(x)}</span>`).join('');
  const meta = [t.rowCount != null ? `${fmtNum(t.rowCount)} rows` : '', has(t.dataCategory) ? `category: ${t.dataCategory}` : '', `${cols.length} cols`, `${msrs.length} measures`]
    .filter(Boolean).map((x) => esc(x)).join(' · ');

  let body = `<div class="doc-table-meta">${tags}<span class="doc-muted">${meta}</span></div>`;
  if (has(t.description)) body += `<p class="doc-desc">${esc(t.description)}</p>`;
  if (cfg.narrative && t.narrative) body += narrativeHtml(t.narrative);

  if (cfg.columnsDetail && cols.length) {
    const rows = cols.map((c) => `<tr><td>${esc(c.name)}${c.isKey ? ' (key)' : ''}</td><td>${esc(c.dataType)}</td><td>${esc(c.summarizeBy)}</td><td>${esc(c.formatString)}</td><td>${has(c.sortByColumn) ? esc(c.sortByColumn) : ''}</td><td>${esc(c.description)}</td></tr>`
      + (cfg.daxExpressions && has(c.expression) ? `<tr class="doc-expr-row"><td colspan="6"><pre class="doc-code"><code>${esc(c.expression)}</code></pre></td></tr>` : '')).join('');
    body += `<h4 class="doc-h4">Columns</h4><table class="doc-table"><thead><tr><th>Name</th><th>Type</th><th>Summarize</th><th>Format</th><th>Sort by</th><th>Description</th></tr></thead><tbody>${rows}</tbody></table>`;
  }
  if (msrs.length) {
    const rows = msrs.map((m) => `<tr><td>${esc(m.name)}</td><td>${esc(m.formatString)}</td><td>${esc(m.description)}</td></tr>`
      + (cfg.daxExpressions && has(m.expression) ? `<tr class="doc-expr-row"><td colspan="3"><pre class="doc-code"><code>${esc(m.expression)}</code></pre></td></tr>` : '')
      + (cfg.narrative && m.narrative ? `<tr><td colspan="3">${narrativeHtml(m.narrative)}</td></tr>` : '')).join('');
    body += `<h4 class="doc-h4">Measures</h4><table class="doc-table"><thead><tr><th>Name</th><th>Format</th><th>Description</th></tr></thead><tbody>${rows}</tbody></table>`;
  }
  if (cfg.hierarchies && t.hierarchies.length) {
    body += `<h4 class="doc-h4">Hierarchies</h4>` + t.hierarchies.map((h) =>
      `<div class="doc-hier"><b>${esc(h.name)}</b>${has(h.description) ? ` <span class="doc-muted">(${esc(h.description)})</span>` : ''}<div class="doc-levels">${h.levels.map((l) => `<span class="doc-level">${esc(l.name)}${has(l.column) && l.column !== l.name ? ` <span class="doc-muted">(${esc(l.column)})</span>` : ''}</span>`).join('<span class="doc-arrow">→</span>')}</div></div>`).join('');
  }
  if (cfg.calcGroups && t.isCalculationGroup && t.calcItems.length) {
    body += `<h4 class="doc-h4">Calculation Items</h4>` + calcItemsHtml(t.calcItems, cfg);
  }
  if (cfg.lineage && t.partitions.length) {
    body += `<h4 class="doc-h4">Partitions</h4>` + t.partitions.map((p) => {
      // A Direct Lake (Entity) partition has no M/SQL/DAX text — its `source` is the "Entity: schema.table"
      // binding, so show it inline in the muted meta rather than as a code block.
      const isEntity = (p.sourceType || '').toLowerCase() === 'entity';
      const meta = `${esc(prettyKey(p.mode ?? ''))}${has(p.sourceType) ? ' · ' + esc(prettyKey(p.sourceType)) : ''}${has(p.dataSource) ? ' · ' + esc(p.dataSource) : ''}${isEntity && has(p.source) ? ' · ' + esc(p.source) : ''}${has(p.refreshedTime) ? ' · refreshed ' + esc((p.refreshedTime ?? '').slice(0, 10)) : ''}`;
      const code = (cfg.daxExpressions && !isEntity && has(p.source)) ? `<pre class="doc-code"><code>${esc(p.source)}</code></pre>` : '';
      return `<div class="doc-part"><b>${esc(p.name)}</b> <span class="doc-muted">${meta}</span>${code}</div>`;
    }).join('');
  }
  return `<details open class="doc-table-detail" data-name="${esc(t.name.toLowerCase())}"><summary class="doc-h3">${esc(t.name)}</summary>${body}</details>`;
}

function measuresHtml(measures: MeasureRow[], cfg: DocConfig): string {
  const rows = measures.map((m) => `<tr><td>${esc(m.name)}</td><td>${esc(m.table)}</td><td>${esc(m.displayFolder)}</td><td>${esc(m.formatString)}</td><td>${esc(m.description)}</td></tr>`
    + (cfg.daxExpressions && has(m.expression) ? `<tr class="doc-expr-row"><td colspan="5"><pre class="doc-code"><code>${esc(m.expression)}</code></pre></td></tr>` : '')
    + (cfg.narrative && m.narrative ? `<tr><td colspan="5">${narrativeHtml(m.narrative)}</td></tr>` : '')).join('');
  return `<table class="doc-table"><thead><tr><th>Measure</th><th>Table</th><th>Folder</th><th>Format</th><th>Description</th></tr></thead><tbody>${rows}</tbody></table>`;
}

function relationshipsHtml(graph: ModelGraph): string {
  const rows = graph.relationships.map((r) => `<tr><td>${esc(r.fromTable)}[${esc(r.fromColumn)}]</td><td>${esc(r.toTable)}[${esc(r.toColumn)}]</td><td>${esc(prettyKey(r.fromCardinality))}→${esc(prettyKey(r.toCardinality))}</td><td>${esc(prettyKey(r.crossFilter))}</td><td>${r.isActive ? 'Yes' : 'No'}</td></tr>`).join('');
  return `<table class="doc-table"><thead><tr><th>From</th><th>To</th><th>Cardinality</th><th>Cross-filter</th><th>Active</th></tr></thead><tbody>${rows}</tbody></table>`;
}

function calcGroupsHtml(tables: DocTable[], cfg: DocConfig): string {
  return tables.map((t) => `<div class="doc-cg"><h3 class="doc-h3">${esc(t.name)}<span class="doc-muted"> · precedence ${t.calcGroupPrecedence}</span></h3>${calcItemsHtml(t.calcItems, cfg)}</div>`).join('');
}
function calcItemsHtml(items: DocCalcItem[], cfg: DocConfig): string {
  return [...items].sort((a, b) => a.ordinal - b.ordinal).map((ci) =>
    `<div class="doc-ci"><b>${esc(ci.name)}</b>${has(ci.description) ? ` <span class="doc-muted">(${esc(ci.description)})</span>` : ''}`
    + (cfg.daxExpressions && has(ci.expression) ? `<pre class="doc-code"><code>${esc(ci.expression)}</code></pre>` : '')
    + (cfg.daxExpressions && has(ci.formatStringExpression) ? `<div class="doc-muted doc-fmt">Format: <code>${esc(ci.formatStringExpression)}</code></div>` : '') + `</div>`).join('');
}

function kpisHtml(kpis: DocKpi[], cfg: DocConfig): string {
  return kpis.map((k) => `<div class="doc-kpi"><b>${esc(k.measure)}</b>`
    + (cfg.daxExpressions && has(k.targetExpression) ? `<div class="doc-muted">Target</div><pre class="doc-code"><code>${esc(k.targetExpression)}</code></pre>` : '')
    + (cfg.daxExpressions && has(k.statusExpression) ? `<div class="doc-muted">Status</div><pre class="doc-code"><code>${esc(k.statusExpression)}</code></pre>` : '')
    + (cfg.daxExpressions && has(k.trendExpression) ? `<div class="doc-muted">Trend</div><pre class="doc-code"><code>${esc(k.trendExpression)}</code></pre>` : '')
    + (has(k.statusGraphic) ? `<div class="doc-muted doc-fmt">Status graphic: ${esc(k.statusGraphic)}</div>` : '')
    + (has(k.trendGraphic) ? `<div class="doc-muted doc-fmt">Trend graphic: ${esc(k.trendGraphic)}</div>` : '')
    + `</div>`).join('');
}

function rolesHtml(roles: RoleInfo[]): string {
  return roles.map((r) => {
    const filters = r.tableFilters.map((f) => `<tr><td>${esc(f.table)}</td><td><pre class="doc-code doc-inline"><code>${esc(f.filterExpression)}</code></pre></td></tr>`).join('');
    return `<div class="doc-role"><h3 class="doc-h3">${esc(r.name)} <span class="doc-muted">· ${esc(r.modelPermission)}</span></h3>`
      + (has(r.description) ? `<p class="doc-desc">${esc(r.description)}</p>` : '')
      + (r.members.length ? `<div class="doc-muted">Members: ${esc(r.members.join(', '))}</div>` : '')
      + (filters ? `<table class="doc-table"><thead><tr><th>Table</th><th>Row filter (RLS)</th></tr></thead><tbody>${filters}</tbody></table>` : '<div class="doc-muted">No row-level filters.</div>')
      + `</div>`;
  }).join('');
}

function lineageHtml(dto: DocModelDto): string {
  let out = '';
  if (dto.dataSources.length) out += `<h3 class="doc-h3">Data Sources</h3><table class="doc-table"><thead><tr><th>Name</th><th>Type</th></tr></thead><tbody>${dto.dataSources.map((d) => `<tr><td>${esc(d.name)}</td><td>${esc(prettyKey(d.type ?? ''))}</td></tr>`).join('')}</tbody></table>`;
  if (dto.expressions.length) out += `<h3 class="doc-h3">Shared Expressions</h3>` + dto.expressions.map((e) => `<div class="doc-expr"><b>${esc(e.name)}</b> <span class="doc-muted">${esc(prettyKey(e.kind ?? ''))}</span>${has(e.description) ? ` <span class="doc-muted">(${esc(e.description)})</span>` : ''}${has(e.expression) ? `<pre class="doc-code"><code>${esc(e.expression)}</code></pre>` : ''}</div>`).join('');
  return out;
}

function storageHtml(vp: VpaqReport): string {
  // PctOfModel is ALREADY a 0–100 percentage from VertiPaq.Compute (matches the Storage tab in App.tsx) — don't ×100 again.
  const tables = [...vp.tables].sort((a, b) => b.size - a.size).slice(0, 30).map((t) => `<tr><td>${esc(t.name)}</td><td class="num">${t.rows == null ? 'Not available' : fmtNum(t.rows)}</td><td class="num">${fmtBytes(t.size)}</td><td class="num">${(t.pctOfModel ?? 0).toFixed(1)}%</td></tr>`).join('');
  const cols = vp.topColumns.slice(0, 25).map((c) => `<tr><td>${esc(c.table)}[${esc(c.column)}]</td><td>${esc(c.encoding)}</td><td class="num">${fmtBytes(c.totalSize)}</td><td class="num">${(c.pctOfModel ?? 0).toFixed(1)}%</td></tr>`).join('');
  const caveat = vp.isDirectLake && vp.caveat ? `<div class="doc-callout">${esc(vp.caveat)}</div>` : '';
  return `<div class="doc-muted">Total model size: <b>${fmtBytes(vp.modelSize)}</b> across ${fmtNum(vp.columnCount)} columns.</div>` + caveat
    + `<h3 class="doc-h3">Tables by size</h3><table class="doc-table"><thead><tr><th>Table</th><th class="num">Rows</th><th class="num">Size</th><th class="num">% model</th></tr></thead><tbody>${tables}</tbody></table>`
    + `<h3 class="doc-h3">Largest columns</h3><table class="doc-table"><thead><tr><th>Column</th><th>Encoding</th><th class="num">Size</th><th class="num">% model</th></tr></thead><tbody>${cols}</tbody></table>`;
}

function prepForAiHtml(p: PrepForAiConfig): string {
  const rows: [string, string][] = [
    ['Q&A enabled', p.qnaEnabled == null ? 'unknown' : p.qnaEnabled ? 'yes' : 'no'],
    ['Linguistic schema', p.hasLinguisticSchema ? 'present' : 'none'],
    ['AI instructions', p.aiInstructionsLength > 0 ? `${fmtNum(p.aiInstructionsLength)} chars` : 'none'],
    ['Fields excluded from AI schema', fmtNum(p.aiSchemaExcludedFields)],
    ['Verified answers', p.verifiedAnswersPresent ? fmtNum(p.verifiedAnswerCount) : 'none'],
  ];
  let out = `<table class="doc-table"><tbody>${rows.map(([k, v]) => `<tr><td>${esc(k)}</td><td>${esc(v)}</td></tr>`).join('')}</tbody></table>`;
  if (has(p.aiInstructions)) out += `<h4 class="doc-h4">AI Instructions</h4><pre class="doc-code"><code>${esc(p.aiInstructions)}</code></pre>`;
  return out;
}

// Authored-narrative block (per object): every section under a soft-tinted card with an author attribution.
function narrativeHtml(n: DocNarrative): string {
  const who = n.author === 'agent' ? 'AI Assistant' : n.author === 'human' ? 'You' : '';
  const body = n.sections.filter((s) => has(s.markdown)).map((s) => `<div class="doc-narr-sec"><div class="doc-narr-key">${esc(prettyKey(s.key))}</div><div class="doc-narr">${mdToHtml(s.markdown)}</div></div>`).join('');
  if (!body) return '';
  return `<div class="doc-narr-card">${who ? `<div class="doc-narr-by">${esc(who)}</div>` : ''}${body}</div>`;
}
function sectionMd(n: DocNarrative, key: string): string {
  const s = n.sections.find((x) => x.key === key);
  return s && has(s.markdown) ? mdToHtml(s.markdown) : '';
}
function prettyKey(k: string): string {
  return String(k).replace(/[_-]+/g, ' ').replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/^./, (c) => c.toUpperCase());
}

// ---- styles + search script -----------------------------------------------------------------------

function css(b: DocBranding): string {
  const accent = /^#[0-9a-f]{3,8}$/i.test(b.accentColor) ? b.accentColor : '#4c6ef5';   // guard the only colour we inject
  return `
:root{--doc-accent:${accent};}
.doc-theme-light{--doc-fg:#1d2330;--doc-muted:#646c7e;--doc-bg:#ffffff;--doc-surface:#f6f8fb;--doc-border:#e4e8ef;--doc-code-bg:#f3f5f9;}
.doc-theme-dark{--doc-fg:#e7ebf3;--doc-muted:#9aa3b5;--doc-bg:#12151c;--doc-surface:#1a1f29;--doc-border:#2a313f;--doc-code-bg:#1d232f;}
*{box-sizing:border-box;}
body{margin:0;font:14px/1.55 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:var(--doc-fg);background:var(--doc-bg);}
.doc-layout{display:flex;align-items:flex-start;}
.doc-sidebar{position:sticky;top:0;align-self:flex-start;height:100vh;width:230px;flex:0 0 230px;overflow:auto;padding:18px 14px;border-right:1px solid var(--doc-border);background:var(--doc-surface);}
.doc-side-title{font-weight:700;font-size:13px;margin-bottom:10px;color:var(--doc-fg);}
.doc-search{width:100%;padding:6px 8px;margin-bottom:10px;border:1px solid var(--doc-border);border-radius:6px;background:var(--doc-bg);color:var(--doc-fg);font-size:12px;}
.doc-toc{display:flex;flex-direction:column;gap:2px;}
.doc-toc-link{color:var(--doc-muted);text-decoration:none;font-size:12.5px;padding:4px 8px;border-radius:6px;}
.doc-toc-link:hover{color:var(--doc-fg);background:var(--doc-bg);}
.doc-main{flex:1;min-width:0;max-width:980px;margin:0 auto;padding:0 32px 64px;}
.doc-cover{padding:64px 0 40px;border-bottom:2px solid var(--doc-accent);margin-bottom:32px;text-align:center;}
.doc-logo{max-height:72px;max-width:240px;margin-bottom:20px;}
.doc-cover-title{font-size:34px;font-weight:800;letter-spacing:-0.5px;}
.doc-cover-sub{font-size:16px;color:var(--doc-muted);margin-top:6px;}
.doc-cover-meta{display:flex;gap:14px;justify-content:center;flex-wrap:wrap;margin-top:18px;color:var(--doc-muted);font-size:13px;}
.doc-cover-meta span:not(:first-child)::before{content:"·";margin-right:14px;color:var(--doc-border);}
.doc-section{margin:34px 0;scroll-margin-top:12px;}
.doc-h2{font-size:21px;font-weight:700;border-bottom:1px solid var(--doc-border);padding-bottom:6px;margin:0 0 14px;}
.doc-h3{font-size:16px;font-weight:650;margin:18px 0 8px;cursor:default;}
.doc-h4{font-size:13px;font-weight:650;color:var(--doc-muted);text-transform:uppercase;letter-spacing:0.4px;margin:16px 0 6px;}
.doc-muted{color:var(--doc-muted);}
.doc-desc{margin:6px 0 10px;}
.doc-stats{display:flex;flex-wrap:wrap;gap:12px;margin:14px 0;}
.doc-stat{background:var(--doc-surface);border:1px solid var(--doc-border);border-radius:10px;padding:12px 16px;min-width:96px;}
.doc-stat-v{font-size:22px;font-weight:700;}
.doc-stat-k{font-size:11px;color:var(--doc-muted);text-transform:uppercase;letter-spacing:0.4px;}
.doc-table{width:100%;border-collapse:collapse;font-size:12.5px;margin:8px 0 14px;}
.doc-table th,.doc-table td{text-align:left;padding:6px 9px;border-bottom:1px solid var(--doc-border);vertical-align:top;}
.doc-table th{color:var(--doc-muted);font-weight:600;font-size:11px;text-transform:uppercase;letter-spacing:0.3px;}
.doc-table td.num,.doc-table th.num{text-align:right;font-variant-numeric:tabular-nums;}
.doc-code{background:var(--doc-code-bg);border:1px solid var(--doc-border);border-radius:6px;padding:8px 10px;overflow:auto;font:12px/1.5 ui-monospace,SFMono-Regular,Consolas,monospace;white-space:pre-wrap;word-break:break-word;margin:4px 0;}
.doc-code.doc-inline{padding:3px 6px;display:inline-block;margin:0;}
code{font:12px ui-monospace,SFMono-Regular,Consolas,monospace;background:var(--doc-code-bg);border-radius:4px;padding:0 4px;}
pre.doc-code code{background:none;padding:0;}
.doc-expr-row td{padding-top:0;border-bottom:1px solid var(--doc-border);}
.doc-tag{display:inline-block;font-size:10px;font-weight:600;background:var(--doc-accent);color:#fff;border-radius:4px;padding:1px 6px;margin-right:6px;}
.doc-table-meta{display:flex;align-items:center;gap:4px;flex-wrap:wrap;margin-bottom:6px;}
.doc-table-detail{border:1px solid var(--doc-border);border-radius:10px;padding:10px 14px;margin:10px 0;background:var(--doc-surface);}
.doc-table-detail>summary{font-size:16px;font-weight:650;cursor:pointer;list-style:none;}
.doc-table-detail>summary::-webkit-details-marker{display:none;}
.doc-table-detail>summary::before{content:"▸ ";color:var(--doc-accent);}
.doc-table-detail[open]>summary::before{content:"▾ ";}
.doc-levels{display:flex;align-items:center;gap:6px;flex-wrap:wrap;margin:4px 0;}
.doc-level{background:var(--doc-bg);border:1px solid var(--doc-border);border-radius:5px;padding:2px 7px;font-size:12px;}
.doc-arrow{color:var(--doc-muted);}
.doc-hier,.doc-part,.doc-ci,.doc-expr,.doc-kpi,.doc-role,.doc-cg{margin:8px 0;}
.doc-grade{display:inline-block;font-weight:600;border-radius:6px;padding:3px 10px;font-size:13px;}
.doc-grade b{font-size:15px;}
.doc-grade-A,.doc-grade-B{background:#10893e22;color:#10893e;}
.doc-grade-C,.doc-grade-D{background:#c98a0022;color:#c98a00;}
.doc-grade-F{background:#d13a3a22;color:#d13a3a;}
.doc-scorecard{display:flex;align-items:center;gap:10px;margin:10px 0;flex-wrap:wrap;}
.doc-callout{border-left:3px solid #c98a00;background:#c98a0014;border-radius:0 8px 8px 0;padding:8px 12px;margin:10px 0;font-size:12.5px;color:var(--doc-fg);}
.doc-narr-card{border-left:3px solid var(--doc-accent);background:color-mix(in srgb,var(--doc-accent) 6%,transparent);border-radius:0 8px 8px 0;padding:8px 12px;margin:8px 0;}
.doc-narr-by{font-size:11px;color:var(--doc-muted);margin-bottom:4px;}
.doc-narr-key{font-size:11px;font-weight:650;color:var(--doc-accent);text-transform:uppercase;letter-spacing:0.4px;margin-top:6px;}
.doc-narr p{margin:4px 0;} .doc-narr ul,.doc-narr ol{margin:4px 0 4px 18px;}
.doc-footer{margin-top:48px;padding-top:14px;border-top:1px solid var(--doc-border);color:var(--doc-muted);font-size:12px;}
.doc-gen{font-size:11px;margin-top:4px;opacity:0.8;}
.doc-diagram{overflow:auto;}
.doc-svg{display:block;height:auto;margin:0 auto;background:var(--doc-bg);border:1px solid var(--doc-border);border-radius:10px;}
.doc-hidden{display:none !important;}
@media print{
  .doc-sidebar,.doc-search{display:none !important;}
  .doc-main{max-width:none;margin:0;padding:0;}
  .doc-table-detail{break-inside:avoid;border-color:#ccc;background:#fff;}
  .doc-section{break-inside:avoid-page;}
  details{display:block;} details>summary{list-style:none;}
  body{background:#fff;color:#000;}
}`;
}

// A tiny inline filter script (only meaningful in the EXPORTED file the user opens; the live preview iframe may
// block inline scripts via CSP, in which case it's a graceful no-op — content still renders).
function searchScript(): string {
  return `<script>function docFilter(q){q=(q||'').trim().toLowerCase();var ds=document.querySelectorAll('.doc-table-detail');ds.forEach(function(d){var hit=!q||(d.getAttribute('data-name')||'').indexOf(q)>=0||(d.textContent||'').toLowerCase().indexOf(q)>=0;d.classList.toggle('doc-hidden',!hit);if(hit&&q)d.setAttribute('open','');});}</script>`;
}

// ---- Markdown rendering ---------------------------------------------------------------------------

function renderMarkdown(dto: DocModelDto, cfg: DocConfig): string {
  const h = dto.header;
  const out: string[] = [];
  const p = (s = '') => out.push(s);
  const code = (lang: string, body: string) => { p('```' + lang); p(body); p('```'); p(); };
  const mdcell = (s: unknown) => esc2md(NL(s));

  p(`# ${NL(h.name) || 'Semantic Model'}: Documentation`);
  p();
  if (cfg.narrative && dto.modelNarrative) { const o = dto.modelNarrative.sections.find((x) => x.key === 'overview'); if (o && has(o.markdown)) { p(o.markdown.trim()); p(); } }
  if (has(h.description)) { p(h.description!.trim()); p(); }
  p('## Overview'); p();
  const ov: [string, string][] = [['Tables', fmtNum(h.tableCount)], ['Measures', fmtNum(h.measureCount)], ['Columns', fmtNum(h.columnCount)], ['Relationships', fmtNum(h.relationshipCount)], ['Compatibility level', String(h.compatibilityLevel || '')], ['Culture', NL(h.culture)], ['Default mode', NL(h.defaultMode)]];
  if (cfg.storageStats && dto.storageAvailable && dto.storage && !dto.storage.error) ov.push(['Model size', fmtBytes(dto.storage.modelSize)]);
  p('| Property | Value |'); p('| --- | --- |');
  ov.filter(([, v]) => has(v)).forEach(([k, v]) => p(`| ${k} | ${mdcell(v)} |`)); p();
  if (cfg.readinessScorecard && dto.readiness) { p(`**AI-Readiness:** ${dto.readiness.grade} (${Math.round(dto.readiness.overall)}/100)`); p(); }
  if (cfg.bpaScorecard && dto.bpa) { p(`**Best practices:** ${fmtNum(dto.bpa.violationCount)} issue(s), ${fmtNum(dto.bpa.autoFixable)} auto-fixable.`); p(); }
  // Model-level glossary / methodology — parallel the HTML export (renderHtml surfaces these as top-level sections).
  if (cfg.narrative && dto.modelNarrative) for (const k of ['glossary', 'methodology']) {
    const s = dto.modelNarrative.sections.find((x) => x.key === k);
    if (s && has(s.markdown)) { p(`## ${prettyKey(k)}`); p(); p(s.markdown.trim()); p(); }
  }

  const visTables = dto.tables.filter((t) => cfg.hiddenObjects || !t.isHidden);
  if (cfg.perTableDetail) {
    p('## Tables'); p();
    for (const t of visTables) {
      p(`### ${NL(t.name)}`); p();
      if (has(t.description)) { p(t.description!.trim()); p(); }
      if (cfg.narrative && t.narrative) for (const s of t.narrative.sections) if (has(s.markdown)) { p(`> **${prettyKey(s.key)}:** ${s.markdown.replace(/\n/g, ' ').trim()}`); p(); }
      const cols = dto.columns.filter((c) => c.table === t.name && (cfg.hiddenObjects || !c.isHidden));
      if (cfg.columnsDetail && cols.length) {
        p('| Column | Type | Summarize | Format | Description |'); p('| --- | --- | --- | --- | --- |');
        cols.forEach((c) => p(`| ${mdcell(c.name)}${c.isKey ? ' (key)' : ''} | ${mdcell(c.dataType)} | ${mdcell(c.summarizeBy)} | ${mdcell(c.formatString)} | ${mdcell(c.description)} |`)); p();
      }
      const msrs = dto.measures.filter((m) => m.table === t.name && (cfg.hiddenObjects || !m.isHidden));
      if (msrs.length) {
        for (const m of msrs) {
          p(`- **${NL(m.name)}**${has(m.description) ? `: ${m.description!.replace(/\n/g, ' ')}` : ''}`);
          if (cfg.narrative && m.narrative) for (const s of m.narrative.sections) if (has(s.markdown)) p(`  > **${prettyKey(s.key)}:** ${s.markdown.replace(/\n/g, ' ').trim()}`);
          if (cfg.daxExpressions && has(m.expression)) { p(); code('dax', m.expression!.trim()); }
        }
        p();
      }
    }
  }
  if (cfg.relationships && dto.graph.relationships.length) {
    p('## Relationships'); p(); p('| From | To | Cardinality | Cross-filter | Active |'); p('| --- | --- | --- | --- | --- |');
    dto.graph.relationships.forEach((r) => p(`| ${mdcell(r.fromTable)}[${mdcell(r.fromColumn)}] | ${mdcell(r.toTable)}[${mdcell(r.toColumn)}] | ${mdcell(prettyKey(r.fromCardinality))}→${mdcell(prettyKey(r.toCardinality))} | ${mdcell(prettyKey(r.crossFilter))} | ${r.isActive ? 'Yes' : 'No'} |`)); p();
  }
  if (cfg.rls && dto.roles.length) {
    p('## Roles & Security'); p();
    for (const r of dto.roles) {
      p(`### ${NL(r.name)} (${NL(r.modelPermission)})`); p();
      if (r.tableFilters.length) { for (const f of r.tableFilters) { p(`- **${NL(f.table)}**`); code('dax', NL(f.filterExpression).trim()); } } else { p('_No row-level filters._'); p(); }
    }
  }
  if (cfg.storageStats && dto.storageAvailable && dto.storage && !dto.storage.error) {
    p('## Storage (VertiPaq)'); p(); p(`Total model size: **${fmtBytes(dto.storage.modelSize)}**.`); p();
    if (dto.storage.isDirectLake && dto.storage.caveat) { p(`> ${dto.storage.caveat}`); p(); }
    p('| Table | Rows | Size | % model |'); p('| --- | ---: | ---: | ---: |');
    [...dto.storage.tables].sort((a, b) => b.size - a.size).slice(0, 30).forEach((t) => p(`| ${mdcell(t.name)} | ${t.rows == null ? 'Not available' : fmtNum(t.rows)} | ${fmtBytes(t.size)} | ${(t.pctOfModel ?? 0).toFixed(1)}% |`)); p();
  }
  return out.join('\n');
}

// Escape Markdown table-breaking characters in a cell.
function esc2md(s: string): string { return NL(s).replace(/\|/g, '\\|').replace(/\n/g, ' '); }
