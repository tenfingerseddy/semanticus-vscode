// Pure documentation renderer: turns a getDocModel snapshot (DocModelDto) into a self-contained, branded
// HTML document and a GitHub-flavoured Markdown variant. NO React, NO DOM, NO engine calls — just strings —
// so it renders the live preview AND the exported file from one code path, and the uishot harness can drive it.
//
// SECURITY: every value that originates from the model (names, DAX, descriptions, narrative) is HTML-escaped
// before it reaches the output. Narrative is authored Markdown, so it goes through a conservative Markdown→HTML
// pass that escapes first and sanitises link URLs (no javascript:/data: links) — the model is untrusted input
// even though it's the user's own (a name could contain "</style><script>…").

import { staticDiagram, classifyRoles } from './diagramlayout.mjs';
import { pathOf } from './diagramroute.mjs';

// ---- wire types (camelCase mirror of Semanticus.Engine/Protocol.cs DocModelDto) --------------------

export interface GraphTable { ref: string; name: string; isHidden: boolean; isDateTable: boolean; isCalculated: boolean; isFieldParameter?: boolean; columns: number; measures: number; keyColumns: string[]; hasDescription: boolean; }
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
export interface VpaqReport { modelSize: number; columnCount: number; tables: VpaqTable[]; topColumns: VpaqColumn[]; storageMode?: 'import' | 'directLake' | 'unknown'; caveat?: string; error?: string; }
export interface PrepForAiConfig { hasLinguisticSchema: boolean; aiInstructions?: string; aiInstructionsLength: number; aiSchemaExcludedFields: number; sourceReadable: boolean; qnaEnabled?: boolean | null; verifiedAnswersPresent: boolean; verifiedAnswerCount: number; }
export interface DocPerspective { ref: string; name: string; description?: string; members: string[]; }

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
  perspectives?: DocPerspective[];
}

// ---- config + branding ----------------------------------------------------------------------------

/** Which sections / details to include. Each flag gates a block; the UI binds checkboxes to these. Hidden tables,
 *  columns and measures are always shown and marked "hidden": a hidden fact table is usual practice and every
 *  measure is built on it. A config saved by an older version may carry keys that no longer exist; they are ignored. */
export interface DocConfig {
  daxExpressions: boolean;    // include measure / column / calc-item formulas (folded on screen, open in print)
  perTableDetail: boolean;    // a card per table
  columnsDetail: boolean;     // the columns table inside each table card
  hierarchies: boolean;       // drill paths inside the table card
  diagram: boolean;           // the relationship diagram as inline SVG
  relationships: boolean;     // the relationships table
  measuresIndex: boolean;     // the measures, grouped by folder
  calcGroups: boolean;        // calculation groups + items
  kpis: boolean;              // KPIs
  rls: boolean;               // roles + row-level security
  perspectives: boolean;      // named model perspectives
  lineage: boolean;           // where each table's data comes from
  storageStats: boolean;      // VertiPaq storage (auto-suppressed when storage is unavailable)
  readinessScorecard: boolean;// the AI readiness grade, as one line
  narrative: boolean;         // authored Documentation narrative (model / table / measure)
}

/** Branding for the header and footer. Colours are not branding: every document uses the app's own look. */
export interface DocBranding {
  title: string;
  subtitle: string;
  companyName: string;
  author: string;
  date: string;             // display date (caller supplies, so the renderer stays pure and deterministic)
  logoDataUri: string;      // a data: URI for the company logo, or '' for the Semanticus logo
  footerText: string;
}

export const DEFAULT_DOC_CONFIG: DocConfig = {
  daxExpressions: true, perTableDetail: true, columnsDetail: true, hierarchies: true,
  diagram: true, relationships: true, measuresIndex: true, calcGroups: true, kpis: true, rls: true, perspectives: true,
  lineage: true, storageStats: true, readinessScorecard: true, narrative: true,
};

export const DEFAULT_DOC_BRANDING: DocBranding = {
  title: '', subtitle: '', companyName: '', author: '', date: '', logoDataUri: '', footerText: '',
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

// ---- entry point ----------------------------------------------------------------------------------

interface Toc { id: string; title: string; }

/** Fonts the page embeds, as data: URIs (woff2). Optional: without them the page falls back to system fonts, so the
 *  renderer stays pure and the node tests need no font files. */
export interface DocFonts { sans?: string; mono?: string; }
export interface DocOptions {
  fonts?: DocFonts;
  /** The editor's theme, for the Docs tab preview only. The exported file never carries it: it follows the reader. */
  theme?: 'light' | 'dark';
}

// The exported FILE keeps its filter script; the in-app preview must not carry one. That iframe is sandboxed
// without allow-scripts (deliberately), so every open of Model > Docs logged two "Blocked script execution in
// about:srcdoc" console errors for a script that could never run (M21, walkthrough 2026-09-14). Built by
// removing the exact strings this file emitted, so there is no second render pass and nothing to drift.
export function renderDoc(dto: DocModelDto, config: DocConfig, branding: DocBranding, opts?: DocOptions): { html: string; markdown: string; previewHtml: string } {
  const html = renderHtml(dto, config, branding, opts);
  const previewHtml = html.split(searchScript()).join('').split(SEARCH_ONINPUT).join('')
    .replace('<html lang="en">', opts?.theme === 'dark' || opts?.theme === 'light' ? `<html lang="en" data-theme="${opts.theme}">` : '<html lang="en">');
  return { html, previewHtml, markdown: renderMarkdown(dto, config, branding) };
}

// ---- shared small pieces --------------------------------------------------------------------------

const TYPE_WORDS: Record<string, string> = { int64: 'whole number', string: 'text', datetime: 'date', boolean: 'true or false', decimal: 'decimal', double: 'decimal' };
const typeWord = (t: unknown) => TYPE_WORDS[NL(t).toLowerCase()] ?? NL(t);

/** A format string as one plain word. The raw pattern is code, not something a reader needs. */
function formatWord(f: unknown): string {
  const s = NL(f);
  if (!s) return '';
  if (s.includes('%')) return 'percent';
  if (s.includes('$')) return 'currency';
  if (/0\.0/.test(s)) return 'decimal';
  return 'whole number';
}

const relWord = (r: GraphRelationship) => `${r.fromCardinality === 'Many' ? 'Many' : 'One'} to ${r.toCardinality === 'Many' ? 'many' : 'one'}`;
const filterWord = (r: GraphRelationship) => (r.crossFilter === 'BothDirections' ? 'Both ways' : 'One way');

/** The reader's word for what a table is, or '' for a plain data table. */
function kindWord(t: DocTable, g: GraphTable | undefined, cols: ColumnRow[]): string {
  if (t.isCalculationGroup) return 'calculation group';
  if (g?.isFieldParameter) return 'field parameter';
  if (t.isDateTable || g?.isDateTable) return 'date';
  if ((g?.measures ?? 0) > 0 && cols.every((c) => c.isHidden)) return 'measures only';
  if (t.isCalculated) return 'calculated';
  return '';
}

/** Measures grouped by display folder. A folder starting with an underscore (helpers) goes last. */
function measureFolders(measures: MeasureRow[]): [string, MeasureRow[]][] {
  const map = new Map<string, MeasureRow[]>();
  for (const m of measures) {
    const f = has(m.displayFolder) ? m.displayFolder! : 'Other';
    const list = map.get(f);
    if (list) list.push(m); else map.set(f, [m]);
  }
  return [...map.entries()].sort((a, b) => (Number(a[0].startsWith('_')) - Number(b[0].startsWith('_'))) || a[0].localeCompare(b[0]));
}
/** "02 Retail\Assortment" is shown as "Retail / Assortment": the number is only a sort key. */
const folderLabel = (f: string) => f.replace(/(^|\\)\d+\s+/g, '$1').split('\\').join(' / ');

/** Tables in reading order: the ones that take part in a relationship (facts first), then the rest by name. */
function tableOrder(dto: DocModelDto): DocTable[] {
  const { facts, dims } = classifyRoles(dto.graph.tables, dto.graph.relationships);
  const rank = new Map<string, number>();
  [...[...facts].sort(), ...[...dims].sort()].forEach((n, i) => rank.set(n, i));
  const rest = (n: string) => rank.get(n) ?? Number.MAX_SAFE_INTEGER;
  return [...dto.tables].sort((a, b) => (rest(a.name) - rest(b.name)) || a.name.localeCompare(b.name));
}

const gradeTone = (g: string) => (g === 'A' || g === 'B' ? 'var(--sem-good)' : g === 'C' || g === 'D' ? 'var(--sem-warn)' : 'var(--sem-bad)');
const findKey = (...parts: unknown[]) => esc(parts.map((p) => NL(p).toLowerCase()).join(' '));

// ---- the relationship diagram: the app's own layout and router, drawn as a static SVG --------------

const OUTWARD: Record<string, number> = { top: -90, bottom: 90, left: 180, right: 0 };
// One cardinality marker at a line end (same shapes as diagramedges.tsx endMarker): a fork for many, a bar for one.
function endMarker(p: { x: number; y: number; side: string }, many: boolean, color: string): string {
  const d = many ? 'M9,0 L0,-5 M9,0 L0,5' : 'M7,-4.5 L7,4.5';
  return `<path d="${d}" transform="translate(${p.x},${p.y}) rotate(${OUTWARD[p.side] ?? 0})" style="stroke:${color};stroke-width:1.4;fill:none"/>`;
}

/** Group the tables that have no relationship by what they are, for the one line under the diagram. */
function isolatedLine(dto: DocModelDto, isolated: GraphTable[]): string {
  if (!isolated.length) return '';
  const byName = new Map(dto.tables.map((t) => [t.name, t]));
  const colsOf = (n: string) => dto.columns.filter((c) => c.table === n);
  const groups = new Map<string, string[]>();
  for (const g of isolated) {
    const t = byName.get(g.name);
    const k = (t ? kindWord(t, g, colsOf(g.name)) : '') || 'helper table';
    const list = groups.get(k);
    if (list) list.push(g.name); else groups.set(k, [g.name]);
  }
  const parts = [...groups.entries()].map(([k, names]) => `${names.map((n) => esc(n)).join(', ')} <span class="muted">(${esc(k)})</span>`);
  return `<p class="muted not-linked">Not linked to other tables: <span class="fg">${parts.join('; ')}</span></p>`;
}

/** The diagram as an inline SVG string, or '' when no table takes part in a relationship. Hidden tables are drawn,
 *  marked "hidden", like the app. The `--sem-*` tokens come from the page, so the page's ground drives the colours. */
export function diagramSvg(graph: ModelGraph, opts?: { aspect?: number }): { svg: string; isolated: GraphTable[]; lines: number } {
  const { cards, lines, isolated } = staticDiagram(graph, { aspect: opts?.aspect ?? 1.6 });
  if (!cards.length) return { svg: '', isolated, lines: 0 };
  const pad = 16;
  const minX = Math.min(...cards.map((c) => c.x)) - pad, minY = Math.min(...cards.map((c) => c.y)) - pad;
  let maxX = Math.max(...cards.map((c) => c.x + c.w)), maxY = Math.max(...cards.map((c) => c.y + c.h));
  for (const l of lines) for (const p of l.pts ?? []) { maxX = Math.max(maxX, p.x); maxY = Math.max(maxY, p.y); }
  maxX += pad; maxY += pad;
  const W = Math.ceil(maxX - minX), H = Math.ceil(maxY - minY);
  const edge = lines.map((l) => {
    const r = l.r, both = r.crossFilter === 'BothDirections';
    const color = !r.isActive ? 'var(--sem-muted)' : both ? 'var(--sem-warn)' : 'var(--sem-accent)';
    const path = l.pts && l.pts.length > 1 ? pathOf(l.pts) : `M${l.src.x},${l.src.y} L${l.dst.x},${l.dst.y}`;
    const title = `${r.fromTable}[${r.fromColumn}] to ${r.toTable}[${r.toColumn}]${r.isActive ? '' : ', not active'}`;
    return `<g class="rel" data-rel="${esc(r.fromTable)}-${esc(r.toTable)}"><title>${esc(title)}</title>`
      + `<path d="${path}" fill="none" style="stroke:${color};stroke-width:1.5${r.isActive ? '' : ';stroke-dasharray:5 4'}"/>`
      + endMarker(l.src, r.fromCardinality === 'Many', color) + endMarker(l.dst, r.toCardinality === 'Many', color) + `</g>`;
  }).join('');
  const node = cards.map((c) => {
    const t = c.t;
    const role = t.isDateTable ? 'date' : t.isFieldParameter ? 'FP' : t.isCalculated ? 'calculated' : '';
    // The same chart steps as the Diagram tab (tokens.css): field parameter purple, calculated light green, date light blue.
    const dot = t.isDateTable ? 'var(--sem-series-5)' : t.isFieldParameter ? 'var(--sem-series-3)' : t.isCalculated ? 'var(--sem-series-4)' : '';
    const words = [t.isHidden ? 'hidden' : '', role].filter(Boolean);
    let rx = c.w - 12, tags = '';
    for (const word of words.reverse()) {
      tags += `<text x="${rx}" y="17" text-anchor="end" class="dg-muted">${esc(word)}</text>`;
      rx -= word.length * 6.2 + 6;
      if (word === role && dot) { tags += `<circle cx="${rx + 1}" cy="13" r="2" style="fill:${dot}"/>`; rx -= 8; }
    }
    const nameMax = Math.max(4, Math.floor((rx - 12) / 6.8));
    const name = t.name.length > nameMax ? t.name.slice(0, nameMax - 1) + '…' : t.name;
    return `<g data-card="${esc(t.name)}" transform="translate(${c.x},${c.y})">`
      + `<rect width="${c.w}" height="${c.h}" rx="4" class="dg-card"/><line x1="0" y1="25" x2="${c.w}" y2="25" class="dg-rule"/>`
      + `<text x="12" y="17" class="dg-name">${esc(name)}</text>${tags}`
      + `<text x="12" y="44" class="dg-muted"><tspan class="dg-num">${t.columns}</tspan> cols<tspan dx="12" class="dg-num">${t.measures}</tspan> msr</text>`
      + (t.keyColumns?.length ? `<text x="12" y="64" class="dg-muted">key: <tspan class="dg-fg">${esc(trunc(t.keyColumns.join(', '), Math.floor((c.w - 40) / 6.2)))}</tspan></text>` : '')
      + `</g>`;
  }).join('');
  const svg = `<svg class="dg" viewBox="${minX} ${minY} ${W} ${H}" width="${W}" style="--dgw:${W};width:100%;max-width:${W}px;min-width:calc(var(--dgw)*0.77px);height:auto;margin:0 auto" role="img" aria-label="How the tables connect" data-lines="${lines.length}">${edge}${node}</svg>`;
  return { svg, isolated, lines: lines.length };
}

const trunc = (s: string, n: number) => (s.length > n ? s.slice(0, Math.max(1, n - 1)) + '…' : s);

const LEGEND = `<div class="legend">`
  + `<span><svg width="28" height="10" aria-hidden="true"><path d="M2,5 H26" style="stroke:var(--sem-accent);stroke-width:1.5"/></svg>In use</span>`
  + `<span><svg width="28" height="10" aria-hidden="true"><path d="M2,5 H26" style="stroke:var(--sem-muted);stroke-width:1.5;stroke-dasharray:5 4"/></svg>Not active (used by a formula only)</span>`
  + `<span><svg width="28" height="10" aria-hidden="true"><path d="M2,5 H26" style="stroke:var(--sem-warn);stroke-width:1.5"/></svg>Filters both ways</span>`
  + `<span><svg width="20" height="12" aria-hidden="true"><path d="M2,6 H18 M18,6 L9,1 M18,6 L9,11" style="stroke:var(--sem-muted);stroke-width:1.4;fill:none"/></svg>Many rows</span>`
  + `<span><svg width="20" height="12" aria-hidden="true"><path d="M2,6 H18 M15,1.5 V10.5" style="stroke:var(--sem-muted);stroke-width:1.4;fill:none"/></svg>One row</span>`
  + `</div>`;

// ---- HTML rendering -------------------------------------------------------------------------------

/** Logo A (path S with end dots), drawn with the page tokens. */
const LOGO = (size: number) => `<svg class="logo" width="${size}" height="${size}" viewBox="0 0 24 24" aria-hidden="true"><path fill="none" style="stroke:var(--sem-fg-strong)" d="M19 5.5H8.25a3.25 3.25 0 0 0 0 6.5h7.5a3.25 3.25 0 0 1 0 6.5H5" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/><path fill="none" style="stroke:var(--sem-accent)" d="M8.25 12h7.5" stroke-width="1.5" stroke-linecap="round"/><circle style="fill:var(--sem-accent)" cx="19" cy="5.5" r="1.9"/><circle style="fill:var(--sem-accent)" cx="5" cy="18.5" r="1.9"/></svg>`;

/** Who wrote a narrative, as a quiet word. Empty when the engine did not say. */
const writtenBy = (n: DocNarrative) => (n.author === 'agent' ? 'Written by your assistant' : n.author === 'human' ? 'Written by you' : '');

function narrativeHtml(n: DocNarrative, only?: string[]): string {
  const sections = n.sections.filter((s) => has(s.markdown) && (!only || only.includes(s.key)));
  if (!sections.length) return '';
  const by = writtenBy(n);
  const body = sections.map((s) => `<div class="nar-sec">${only && only.length === 1 && s.key === 'overview' ? '' : `<div class="nar-key">${esc(prettyKey(s.key))}</div>`}<div class="nar-body">${mdToHtml(s.markdown)}</div></div>`).join('');
  return `<div class="nar">${by ? `<div class="word">${esc(by)}</div>` : ''}${body}</div>`;
}

const formulaHtml = (code: string, cls = 'f') => `<details class="${cls === 'src' ? 'f src' : 'f'}"><summary>${cls === 'src' ? 'Query' : 'Formula'}</summary><pre class="dax">${esc(code.trim())}</pre></details>`;

function renderHtml(dto: DocModelDto, cfg: DocConfig, b: DocBranding, opts?: DocOptions): string {
  const toc: Toc[] = [];
  const sections: string[] = [];
  const title = has(b.title) ? b.title : (dto.header.name || 'Semantic model');
  const add = (id: string, tocTitle: string, body: string) => { toc.push({ id, title: tocTitle }); sections.push(`<section id="${id}" class="sec"><h2>${esc(tocTitle)}</h2>${body}</section>`); };
  const graphByName = new Map(dto.graph.tables.map((t) => [t.name, t]));
  const colsOf = (n: string) => dto.columns.filter((c) => c.table === n);

  // Diagram
  if (cfg.diagram) {
    const d = diagramSvg(dto.graph);
    const body = d.svg
      ? `<div class="diagram">${d.svg}</div>${LEGEND}${isolatedLine(dto, d.isolated)}`
      : `<p class="muted">No table is linked to another table.</p>${isolatedLine(dto, d.isolated)}`;
    add('diagram', 'How the tables connect', body);
  }

  // Tables: hidden ones are always listed and marked
  if (cfg.perTableDetail && dto.tables.length) {
    add('tables', 'Tables', `<div class="stack">${tableOrder(dto).map((t) => tableCardHtml(t, dto, cfg, graphByName.get(t.name), colsOf(t.name))).join('')}</div>`);
  }

  // Measures: once, grouped by folder
  if (cfg.measuresIndex && dto.measures.length) add('measures', 'Measures', measuresHtml(dto.measures, cfg));

  // Relationships
  if (cfg.relationships && dto.graph.relationships.length) add('relationships', 'Relationships', `<div class="scroll">${relationshipsHtml(dto.graph)}</div>`);

  // Calculation groups
  const calcTables = dto.tables.filter((t) => t.isCalculationGroup);
  if (cfg.calcGroups && calcTables.length) add('calc', 'Calculation groups', calcGroupsHtml(calcTables, cfg));

  if (cfg.kpis && dto.kpis.length) add('kpis', 'KPIs', kpisHtml(dto.kpis, cfg));
  if (cfg.rls && dto.roles.length) add('roles', 'Roles and security', rolesHtml(dto.roles));
  if (cfg.perspectives && dto.perspectives && dto.perspectives.length) add('perspectives', 'Perspectives', perspectivesHtml(dto.perspectives));
  if (cfg.lineage && (dto.tables.some((t) => t.partitions.length) || dto.dataSources.length || dto.expressions.length)) add('sources', 'Where the data comes from', sourcesHtml(dto, cfg));
  if (cfg.storageStats && dto.storageAvailable && dto.storage && !dto.storage.error) add('storage', 'Storage', storageHtml(dto.storage));

  if (cfg.narrative && dto.modelNarrative) {
    const glossary = dto.modelNarrative.sections.find((s) => s.key === 'glossary');
    const methodology = dto.modelNarrative.sections.find((s) => s.key === 'methodology');
    if (glossary && has(glossary.markdown)) add('glossary', 'Glossary', `<div class="nar-body">${mdToHtml(glossary.markdown)}</div>`);
    if (methodology && has(methodology.markdown)) add('methodology', 'Method', `<div class="nar-body">${mdToHtml(methodology.markdown)}</div>`);
  }

  const nav = [{ id: 'summary', title: 'Summary' }, ...toc].map((t, i) => `<a href="#${t.id}"${i === 0 ? ' class="on"' : ''}>${esc(t.title)}</a>`).join('');
  const date = has(b.date) ? b.date : '';
  const footerBits = [has(b.footerText) ? b.footerText : '', has(b.companyName) ? b.companyName : ''].filter(Boolean).map(esc);

  return `<!doctype html><html lang="en"><head><meta charset="utf-8"/><meta name="viewport" content="width=device-width, initial-scale=1"/>`
    + `<title>${esc(title)}: documentation</title><style>${css(opts?.fonts)}</style></head><body>`
    + `<div class="wrap">`
    + `<aside class="side"><div class="brand">${LOGO(20)}<span>${esc(title)}</span></div>`
    + `<nav aria-label="Contents">${nav}</nav>`
    + `<input id="doc-search" class="find" type="search" placeholder="Find a table or measure" aria-label="Find a table or measure"${SEARCH_ONINPUT}/></aside>`
    + `<main>${summaryHtml(dto, cfg, b, title, date)}${sections.join('\n')}`
    + `<footer class="foot">${LOGO(16)}<span>${footerBits.length ? footerBits.join(' · ') + ' · ' : ''}Made with Semanticus${date ? ' · ' + esc(date) : ''}</span></footer></main>`
    + `</div>${searchScript()}</body></html>`;
}

function summaryHtml(dto: DocModelDto, cfg: DocConfig, b: DocBranding, title: string, date: string): string {
  const h = dto.header;
  const logo = /^data:image\/(png|jpe?g|gif|webp|svg\+xml);base64,[a-z0-9+/=\s]+$/i.test(b.logoDataUri ?? '')
    ? `<img class="co-logo" src="${esc(b.logoDataUri)}" alt="${esc(b.companyName || 'Company logo')}"/>` : LOGO(32);
  const meta = [date, has(b.author) ? b.author : '', has(b.companyName) ? b.companyName : ''].filter(Boolean).map(esc).join(' · ');
  const narr = cfg.narrative && dto.modelNarrative ? narrativeHtml(dto.modelNarrative, ['overview']) : '';
  const lead = has(h.description) ? `<p class="lead">${esc(h.description)}</p>` : '';
  const counts: [string, number][] = [['Tables', h.tableCount], ['Measures', h.measureCount], ['Columns', h.columnCount], ['Relationships', h.relationshipCount]];
  const stats = counts.map(([k, v]) => `<div class="card stat"><div class="num big">${fmtNum(v)}</div><div class="muted">${k}</div></div>`).join('');
  const ready = cfg.readinessScorecard && dto.readiness
    ? `<p class="muted ready">AI readiness <span class="grade" style="color:${gradeTone(dto.readiness.grade)}">${esc(dto.readiness.grade)}</span> <span class="num">${Math.round(dto.readiness.overall)}</span> of 100</p>` : '';
  return `<header id="summary" class="sec">${logo}<p class="word eyebrow">${esc(has(b.subtitle) ? b.subtitle : 'Model documentation')}</p><h1>${esc(title)}</h1>`
    + (meta ? `<p class="muted meta">${meta}</p>` : '') + narr + lead + `<div class="stats">${stats}</div>${ready}</header>`;
}

function tableCardHtml(t: DocTable, dto: DocModelDto, cfg: DocConfig, g: GraphTable | undefined, cols: ColumnRow[]): string {
  const kind = kindWord(t, g, cols);
  const words = [t.isHidden ? 'hidden' : '', kind].filter(Boolean).join(' · ');
  const nMeasures = g?.measures ?? dto.measures.filter((m) => m.table === t.name).length;
  const nCols = g?.columns ?? cols.length;
  const head = `<div class="thead"><h3>${esc(t.name)}</h3>${words ? `<span class="word">${esc(words)}</span>` : ''}`
    + `<span class="word counts"><span class="num">${nCols}</span> columns · <span class="num">${nMeasures}</span> measures${t.rowCount != null ? ` · <span class="num">${fmtNum(t.rowCount)}</span> rows` : ''}</span></div>`;
  const mine = dto.graph.relationships.filter((r) => r.fromTable === t.name || r.toTable === t.name);
  const links = mine.length
    ? `<p class="muted line">Linked to: ${mine.map((r) => { const other = r.fromTable === t.name ? r.toTable : r.fromTable; return `<span class="fg">${esc(other)}</span> on ${esc(r.fromTable === t.name ? r.fromColumn : r.toColumn)}${r.isActive ? '' : ' (not active)'}`; }).join(', ')}</p>` : '';
  const drill = cfg.hierarchies ? t.hierarchies.map((h) => `<p class="muted line">Drill path <span class="fg">${esc(h.name)}</span>${h.isHidden ? ' (hidden)' : ''}: ${h.levels.map((l) => esc(l.name)).join(' <span aria-hidden="true">&#8250;</span> ')}</p>`).join('') : '';
  const calcNote = t.isCalculationGroup ? `<p class="muted line">Its items are listed under Calculation groups.</p>` : '';
  const shown = cfg.columnsDetail ? cols : [];
  const rows = shown.map((c) => `<tr><td${c.isHidden ? ' class="hid"' : ''}><span class="${c.isHidden ? 'muted' : 'fg'}">${esc(c.name)}</span>${c.isKey ? ' <span class="word">key</span>' : ''}${c.isHidden && !t.isHidden ? ' <span class="word">hidden</span>' : ''}</td>`
    + `<td class="muted">${esc(typeWord(c.dataType))}</td><td>${esc(c.description)}${cfg.daxExpressions && c.isCalculated && has(c.expression) ? formulaHtml(c.expression) : ''}</td></tr>`).join('');
  const colTable = shown.length ? `<div class="scroll tblwrap"><table class="t tbl"><thead><tr><th style="width:30%">Column</th><th style="width:16%">Type</th><th>What it holds</th></tr></thead><tbody>${rows}</tbody></table></div>` : '';
  const narr = cfg.narrative && t.narrative ? narrativeHtml(t.narrative) : '';
  return `<div class="card tcard" data-find="${findKey(t.name, t.description)}">${head}`
    + (has(t.description) ? `<p class="desc">${esc(t.description)}</p>` : '') + narr + links + drill + calcNote + colTable + `</div>`;
}

function measuresHtml(measures: MeasureRow[], cfg: DocConfig): string {
  const folders = measureFolders(measures);
  const body = folders.map(([f, ms]) => `<div class="fold"><h4>${esc(folderLabel(f))} <span class="num">${ms.length}</span></h4>${ms.map((m) => {
    const fmt = formatWord(m.formatString);
    const words = [m.isHidden ? 'hidden' : '', fmt].filter(Boolean).join(' · ');
    return `<div class="measure" data-find="${findKey(m.name, m.description)}"><div class="mhead"><span class="mname">${esc(m.name)}</span><span class="word">${esc(words)}</span></div>`
      + (has(m.description) ? `<p>${esc(m.description)}</p>` : '')
      + (cfg.narrative && m.narrative ? narrativeHtml(m.narrative) : '')
      + (cfg.daxExpressions && has(m.expression) ? formulaHtml(m.expression) : '') + `</div>`;
  }).join('')}</div>`).join('');
  return `<p class="muted">Grouped by folder, as report builders see them.</p>${body}`;
}

function relationshipsHtml(graph: ModelGraph): string {
  const rows = graph.relationships.map((r) => `<tr><td>${esc(r.fromTable)} <span class="muted">${esc(r.fromColumn)}</span></td><td>${esc(r.toTable)} <span class="muted">${esc(r.toColumn)}</span></td><td>${relWord(r)}</td><td>${filterWord(r)}</td><td>${r.isActive ? 'Yes' : '<span class="muted">No</span>'}</td></tr>`).join('');
  return `<table class="t tbl"><thead><tr><th>From</th><th>To</th><th>Rows</th><th>Filter</th><th>Active</th></tr></thead><tbody>${rows}</tbody></table>`;
}

function calcGroupsHtml(tables: DocTable[], cfg: DocConfig): string {
  return `<div class="stack">${tables.map((t) => {
    const items = [...t.calcItems].sort((a, b) => a.ordinal - b.ordinal);
    const formulas = cfg.daxExpressions && items.some((c) => has(c.expression) || has(c.formatStringExpression))
      ? `<details class="f"><summary>Formulas</summary>${items.map((c) => `<p class="ci">${esc(c.name)}</p>`
        + (has(c.expression) ? `<pre class="dax">${esc(c.expression!.trim())}</pre>` : '')
        + (has(c.formatStringExpression) ? `<p class="word">Format</p><pre class="dax">${esc(c.formatStringExpression!.trim())}</pre>` : '')).join('')}</details>` : '';
    return `<div class="card" data-find="${findKey(t.name, t.description)}"><div class="thead"><h3>${esc(t.name)}</h3><span class="word">calculation group${t.isHidden ? ' · hidden' : ''}</span></div>`
      + (has(t.description) ? `<p class="desc">${esc(t.description)}</p>` : '')
      + `<p class="chips">${items.map((c) => `<span class="chip">${esc(c.name)}</span>`).join('')}</p>${formulas}</div>`;
  }).join('')}</div>`;
}

function kpisHtml(kpis: DocKpi[], cfg: DocConfig): string {
  return `<div class="stack">${kpis.map((k) => {
    const part = (label: string, code?: string) => (has(code) ? `<p class="word kpi-k">${label}</p><pre class="dax">${esc(code!.trim())}</pre>` : '');
    const inner = cfg.daxExpressions ? part('Target', k.targetExpression) + part('Status', k.statusExpression) + part('Trend', k.trendExpression) : '';
    const graphics = [has(k.statusGraphic) ? `status graphic ${k.statusGraphic}` : '', has(k.trendGraphic) ? `trend graphic ${k.trendGraphic}` : ''].filter(Boolean).join(' · ');
    return `<div class="card"><div class="thead"><h3>${esc(k.measure)}</h3>${graphics ? `<span class="word">${esc(graphics)}</span>` : ''}</div>`
      + (inner ? `<details class="f"><summary>Formulas</summary>${inner}</details>` : '') + `</div>`;
  }).join('')}</div>`;
}

function prettyMember(ref: string): string {
  return String(ref).replace(/^[a-z]+:/i, '');
}
function perspectivesHtml(ps: DocPerspective[]): string {
  return `<div class="stack">${ps.map((p) => {
    const members = (p.members ?? []).map((m) => `<span class="chip">${esc(prettyMember(m))}</span>`).join('');
    return `<div class="card"><div class="thead"><h3>${esc(p.name)}</h3></div>`
      + (has(p.description) ? `<p class="desc">${esc(p.description)}</p>` : '')
      + (members ? `<p class="chips">${members}</p>` : '<p class="muted">No members.</p>') + `</div>`;
  }).join('')}</div>`;
}

function rolesHtml(roles: RoleInfo[]): string {
  return `<div class="stack">${roles.map((r) => {
    const filters = r.tableFilters.map((f) => `<tr><td>${esc(f.table)}</td><td><pre class="dax inline">${esc(f.filterExpression)}</pre></td></tr>`).join('');
    return `<div class="card"><div class="thead"><h3>${esc(r.name)}</h3>${has(r.modelPermission) ? `<span class="word">${esc(prettyKey(r.modelPermission!).toLowerCase())}</span>` : ''}</div>`
      + (has(r.description) ? `<p class="desc">${esc(r.description)}</p>` : '')
      + (r.members.length ? `<p class="muted line">Members: ${esc(r.members.join(', '))}</p>` : '')
      + (filters ? `<div class="scroll tblwrap"><table class="t tbl"><thead><tr><th style="width:30%">Table</th><th>Row filter</th></tr></thead><tbody>${filters}</tbody></table></div>` : '<p class="muted line">No row filters.</p>')
      + `</div>`;
  }).join('')}</div>`;
}

const SOURCE_KIND: Record<string, string> = { m: 'Power Query', calculated: 'DAX table', calculationgroup: 'calculation group', entity: 'Direct Lake table', query: 'SQL query' };
const MODE_WORD: Record<string, string> = { import: 'imported', directquery: 'direct query', directlake: 'Direct Lake', dual: 'dual' };

function sourcesHtml(dto: DocModelDto, cfg: DocConfig): string {
  const rows = dto.tables.map((t) => {
    const p = t.partitions[0];
    if (!p) return '';
    const kind = SOURCE_KIND[(p.sourceType ?? '').toLowerCase()] ?? prettyKey(p.sourceType ?? '').toLowerCase();
    const mode = MODE_WORD[(p.mode ?? '').toLowerCase()] ?? '';
    const entity = (p.sourceType ?? '').toLowerCase() === 'entity';
    const code = cfg.daxExpressions && !entity && has(p.source) ? formulaHtml(p.source!, 'src') : '';
    const where = [entity && has(p.source) ? p.source!.replace(/^Entity:\s*/i, '') : '', has(p.dataSource) ? p.dataSource! : ''].filter(Boolean).map(esc).join(' · ');
    const more = t.partitions.length > 1 ? ` <span class="word">${t.partitions.length} partitions</span>` : '';
    return `<tr><td>${esc(t.name)}${more}${code}</td><td class="muted">${esc(kind)}</td><td class="muted">${esc([mode, where].filter(Boolean).join(' · '))}</td></tr>`;
  }).join('');
  let out = rows ? `<div class="scroll tblwrap"><table class="t tbl"><thead><tr><th>Table</th><th>Made by</th><th>Data</th></tr></thead><tbody>${rows}</tbody></table></div>` : '';
  if (dto.dataSources.length) out += `<h3 class="sub">Data sources</h3><div class="scroll tblwrap"><table class="t tbl"><thead><tr><th>Name</th><th>Type</th></tr></thead><tbody>${dto.dataSources.map((d) => `<tr><td>${esc(d.name)}</td><td class="muted">${esc(prettyKey(d.type ?? '').toLowerCase())}</td></tr>`).join('')}</tbody></table></div>`;
  if (dto.expressions.length) out += `<h3 class="sub">Shared queries</h3>` + dto.expressions.map((e) =>
    `<div class="expr"><span class="mname">${esc(e.name)}</span> <span class="word">${esc(prettyKey(e.kind ?? '').toLowerCase())}</span>${has(e.description) ? `<p class="muted">${esc(e.description)}</p>` : ''}${cfg.daxExpressions && has(e.expression) ? formulaHtml(e.expression!, 'src') : ''}</div>`).join('');
  return out;
}

function storageHtml(vp: VpaqReport): string {
  // PctOfModel is ALREADY a 0-100 percentage from VertiPaq.Compute (matches the Storage tab in App.tsx): do not multiply again.
  const mode = vp.storageMode === 'import' || vp.storageMode === 'directLake' ? vp.storageMode : 'unknown';
  const shareLabel = mode === 'import' ? '% model' : mode === 'directLake' ? '% resident' : '% observed';
  const tables = [...vp.tables].sort((a, b) => b.size - a.size).slice(0, 30).map((t) => `<tr><td>${esc(t.name)}</td><td class="num">${t.rows == null ? 'Not available' : fmtNum(t.rows)}</td><td class="num">${fmtBytes(t.size)}</td><td class="num">${(t.pctOfModel ?? 0).toFixed(1)}%</td></tr>`).join('');
  const cols = vp.topColumns.slice(0, 25).map((c) => `<tr><td>${esc(c.table)}[${esc(c.column)}]</td><td>${esc(c.encoding)}</td><td class="num">${fmtBytes(c.totalSize)}</td><td class="num">${(c.pctOfModel ?? 0).toFixed(1)}%</td></tr>`).join('');
  const fallback = mode === 'unknown' ? 'Storage mode could not be confirmed. Storage sizes and row counts may reflect only data currently in memory, so they are not treated as full model totals.' : '';
  const caveat = mode !== 'import' && (vp.caveat || fallback) ? `<p class="callout">${esc(vp.caveat || fallback)}</p>` : '';
  const totalLabel = mode === 'import' ? 'Total model size' : mode === 'directLake' ? 'Resident storage estimate' : 'Observed column storage';
  return `<p class="muted">${totalLabel}: <b class="fg">${fmtBytes(vp.modelSize)}</b> across ${fmtNum(vp.columnCount)} columns.</p>${caveat}`
    + `<h3 class="sub">Tables by size</h3><div class="scroll tblwrap"><table class="t tbl"><thead><tr><th>Table</th><th class="num">Rows</th><th class="num">Size</th><th class="num">${shareLabel}</th></tr></thead><tbody>${tables}</tbody></table></div>`
    + `<h3 class="sub">Largest columns</h3><div class="scroll tblwrap"><table class="t tbl"><thead><tr><th>Column</th><th>Encoding</th><th class="num">Size</th><th class="num">${shareLabel}</th></tr></thead><tbody>${cols}</tbody></table></div>`;
}

function prettyKey(k: string): string {
  return String(k).replace(/[_-]+/g, ' ').replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/^./, (c) => c.toUpperCase());
}

// ---- styles + search script -----------------------------------------------------------------------

const FONT_URI = /^data:font\/woff2?;base64,[A-Za-z0-9+/=]+$/;

// Light tokens sit on :root. Dark follows the reader's setting (screen only, so a printed page is always light),
// unless the page is pinned to light. data-theme="dark" is what the Docs tab preview passes from the editor theme.
const DARK_TOKENS = 'color-scheme:dark;--sem-bg:#1F1F1F;--sem-fg:#CCCCCC;--sem-fg-strong:#E8E8E8;--sem-accent:#3CC08F;--sem-link:#4DAAFC;--sem-chart-green:#89D185;--sem-chart-yellow:#CCA700;--sem-chart-red:#F14C4C;--sem-chart-blue:#59A4F9;--sem-chart-purple:#B180D7;--sem-series-shift:.13;--sem-hue-mix:70%';

function css(fonts?: DocFonts): string {
  const face = (family: string, uri: string | undefined, weight: string) => (uri && FONT_URI.test(uri)
    ? `@font-face{font-family:"${family}";src:url(${uri}) format("woff2");font-weight:${weight};font-display:swap}` : '');
  return `
${face('Instrument Sans', fonts?.sans, '400 700')}${face('JetBrains Mono', fonts?.mono, '400')}
:root{color-scheme:light;--sem-bg:#FFFFFF;--sem-fg:#3B3B3B;--sem-fg-strong:#1F1F1F;--sem-accent:#0A7A55;--sem-link:#005FB8;
  --sem-chart-green:#388A34;--sem-chart-yellow:#BF8803;--sem-chart-red:#E51400;--sem-chart-blue:#0063D3;--sem-chart-purple:#652D90;--sem-series-shift:-.13;--sem-hue-mix:55%;
  --doc-font:"Instrument Sans",system-ui,-apple-system,"Segoe UI",sans-serif;--doc-mono:"JetBrains Mono",ui-monospace,SFMono-Regular,Consolas,monospace;
  --s1:4px;--s2:8px;--s3:12px;--s4:16px;--s6:24px;--s8:32px;--s12:48px;--s16:64px}
@media screen and (prefers-color-scheme: dark){:root:not([data-theme="light"]){${DARK_TOKENS}}}
@media screen{:root[data-theme="dark"]{${DARK_TOKENS}}}
:root{--sem-surface:color-mix(in srgb,var(--sem-fg) 8%,var(--sem-bg));--sem-muted:color-mix(in srgb,var(--sem-fg) 74%,var(--sem-surface));
  --sem-border:color-mix(in srgb,var(--sem-fg) 16%,var(--sem-bg));--sem-rule:color-mix(in srgb,var(--sem-fg) 11%,var(--sem-bg));
  --sem-good:color-mix(in srgb,var(--sem-chart-green) var(--sem-hue-mix),var(--sem-fg));--sem-warn:color-mix(in srgb,var(--sem-chart-yellow) var(--sem-hue-mix),var(--sem-fg));
  --sem-bad:color-mix(in srgb,var(--sem-chart-red) var(--sem-hue-mix),var(--sem-fg));
  --sem-series-3:color-mix(in srgb,var(--sem-chart-purple) var(--sem-hue-mix),var(--sem-fg));
  --sem-series-4:oklab(from var(--sem-accent) calc(l + var(--sem-series-shift)) a b);
  --sem-series-5:oklab(from color-mix(in srgb,var(--sem-chart-blue) var(--sem-hue-mix),var(--sem-fg)) calc(l + var(--sem-series-shift)) a b)}
*{box-sizing:border-box}
html{background:var(--sem-bg)}
body{margin:0;background:var(--sem-bg);color:var(--sem-fg);font:400 14px/20px var(--doc-font);-webkit-font-smoothing:antialiased}
h1,h2,h3,h4{font-family:var(--doc-font);color:var(--sem-fg-strong);font-weight:600;margin:0}
h1{font-size:32px;line-height:40px;letter-spacing:-.01em}
h2{font-size:22px;line-height:28px}
h3{font-size:16px;line-height:24px}
h4{font-size:12px;line-height:16px;color:var(--sem-muted)}
p{margin:0}
a{color:inherit}
code,pre,.num{font-family:var(--doc-mono);font-variant-numeric:tabular-nums}
code{font-size:12px;background:var(--sem-surface);border-radius:2px;padding:0 var(--s1)}
.muted{color:var(--sem-muted)}.fg{color:var(--sem-fg)}
.card{background:var(--sem-surface);border:1px solid var(--sem-border);border-radius:4px;padding:var(--s4)}
.word{color:var(--sem-muted);font-size:12px;line-height:16px}
.wrap{display:grid;grid-template-columns:repeat(12,minmax(0,1fr));gap:var(--s3);max-width:1200px;margin:0 auto;padding:0 var(--s6)}
.side{grid-column:span 3;position:sticky;top:0;align-self:start;height:100vh;padding:var(--s6) var(--s3) var(--s6) 0;display:flex;flex-direction:column;gap:var(--s4);border-right:1px solid var(--sem-rule)}
.brand{display:flex;align-items:center;gap:var(--s2);font-weight:600;color:var(--sem-fg-strong);min-width:0}
.brand span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.logo{flex:none}
.side nav{display:flex;flex-direction:column}
.side nav a{text-decoration:none;color:var(--sem-muted);padding:var(--s1) var(--s3);border-left:2px solid transparent;font-size:13px;line-height:24px}
.side nav a.on,.side nav a:hover{color:var(--sem-fg-strong)}
.side nav a.on{border-left-color:var(--sem-accent)}
.find{font:13px/20px var(--doc-font);padding:var(--s1) var(--s2);border:1px solid var(--sem-border);border-radius:3px;background:var(--sem-bg);color:var(--sem-fg);width:100%}
main{grid-column:span 9;padding:var(--s12) 0 var(--s16) var(--s6);min-width:0}
.sec{margin-bottom:var(--s12);scroll-margin-top:var(--s6)}
.sec>h2{margin-bottom:var(--s3)}
.co-logo{display:block;max-height:48px;max-width:200px;margin-bottom:var(--s4)}
header .logo{display:block;margin-bottom:var(--s4)}
.eyebrow{margin-bottom:var(--s2)}
.meta{margin-top:var(--s2)}
.lead{font-size:16px;line-height:24px;margin-top:var(--s4);max-width:64ch;color:var(--sem-fg)}
.stats{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:var(--s3);margin-top:var(--s6)}
.stat{padding:var(--s3) var(--s4)}.big{font-size:22px;line-height:28px;color:var(--sem-fg-strong)}
.ready{margin-top:var(--s3)}.grade{font-weight:700}
.stack{display:flex;flex-direction:column;gap:var(--s3)}
.thead{display:flex;align-items:baseline;gap:var(--s3);flex-wrap:wrap}
.thead .counts{margin-left:auto}
.desc{margin-top:var(--s2)}
.line{margin-top:var(--s2)}
.sub{margin:var(--s6) 0 var(--s2)}
.chips{margin-top:var(--s3)}
.chip{display:inline-block;font-size:12px;line-height:16px;padding:2px var(--s2);border:1px solid var(--sem-border);border-radius:2px;color:var(--sem-fg);margin:0 var(--s1) var(--s1) 0;background:var(--sem-bg)}
.tblwrap{margin-top:var(--s3)}
.scroll{overflow-x:auto}
table.t{width:100%;border-collapse:collapse;font-size:13px;line-height:20px}
table.t th{text-align:left;font-weight:600;font-size:12px;line-height:16px;color:var(--sem-muted);padding:var(--s2) var(--s3) var(--s2) 0;border-bottom:1px solid var(--sem-border)}
table.t td{padding:var(--s2) var(--s3) var(--s2) 0;border-bottom:1px solid var(--sem-rule);vertical-align:top}
table.t th.num,table.t td.num{text-align:right}
.fold h4{margin-top:var(--s6)}.fold h4 .num{font-weight:400}
.measure{padding:var(--s3) 0;border-bottom:1px solid var(--sem-rule)}
.mhead{display:flex;gap:var(--s3);align-items:baseline}
.mname{font-weight:600;color:var(--sem-fg-strong)}
.mhead .word{margin-left:auto}
.measure p{margin-top:var(--s1)}
.ci{margin-top:var(--s3);font-weight:600}
.kpi-k{margin-top:var(--s3)}
.expr{margin-top:var(--s3)}
details.f{margin-top:var(--s1)}
details.f>summary{cursor:pointer;color:var(--sem-muted);font-size:12px;line-height:16px;list-style:none;display:inline-flex;gap:var(--s1);align-items:center}
details.f>summary::before{content:"";width:0;height:0;border-left:4px solid currentColor;border-top:3px solid transparent;border-bottom:3px solid transparent}
details.f[open]>summary::before{transform:rotate(90deg)}
details.f>summary::-webkit-details-marker{display:none}
pre.dax,pre.doc-code{margin:var(--s2) 0 0;padding:var(--s3);background:var(--sem-bg);border:1px solid var(--sem-rule);border-radius:3px;font-size:12px;line-height:16px;white-space:pre-wrap;word-break:break-word;color:var(--sem-fg)}
pre.dax.inline{margin:0;padding:var(--s1) var(--s2)}
pre code{background:none;padding:0}
.nar{border-left:2px solid var(--sem-accent);padding-left:var(--s3);margin-top:var(--s3);max-width:72ch}
.nar-key{font-size:12px;line-height:16px;font-weight:600;color:var(--sem-muted);margin-top:var(--s2)}
.nar-body p{margin-top:var(--s1)}.nar-body ul,.nar-body ol{margin:var(--s1) 0 var(--s1) var(--s4);padding:0}
.nar-body blockquote{margin:var(--s2) 0;padding-left:var(--s3);border-left:2px solid var(--sem-border);color:var(--sem-muted)}
.nar-body .doc-mdh{font-size:14px;line-height:20px;margin-top:var(--s3)}
.callout{border-left:2px solid var(--sem-warn);padding-left:var(--s3);margin:var(--s3) 0;color:var(--sem-fg)}
.diagram{overflow:auto;display:flex;justify-content:flex-start;padding:var(--s4);background-color:var(--sem-bg);background-image:radial-gradient(color-mix(in srgb,var(--sem-fg) 14%,transparent) 1px,transparent 1px);background-size:20px 20px;border:1px solid var(--sem-rule);border-radius:3px}
.dg text{font:11px/16px var(--doc-font)}.dg .dg-name{font-weight:600;fill:var(--sem-fg-strong)}.dg .dg-muted{fill:var(--sem-muted)}.dg .dg-fg{fill:var(--sem-fg)}
.dg .dg-num{font-family:var(--doc-mono);font-variant-numeric:tabular-nums}.dg .dg-card{fill:var(--sem-surface);stroke:var(--sem-border)}.dg .dg-rule{stroke:var(--sem-rule)}
.legend{display:flex;flex-wrap:wrap;gap:var(--s4);font-size:12px;line-height:16px;color:var(--sem-muted);margin-top:var(--s3)}
.legend svg{vertical-align:middle;margin-right:var(--s1)}
.not-linked{margin-top:var(--s3)}
.foot{display:flex;gap:var(--s2);align-items:center;color:var(--sem-muted);font-size:12px;border-top:1px solid var(--sem-rule);padding-top:var(--s4)}
.doc-hidden{display:none !important}
@media (max-width:760px){.side{display:none}main{grid-column:1/-1;padding-left:0}.stats{grid-template-columns:repeat(2,minmax(0,1fr))}.wrap{padding:0 var(--s4)}.thead .counts{margin-left:0}.diagram .dg{flex:none}}
@page{size:A4;margin:14mm}
@media print{
  :root{color-scheme:light}
  .wrap{display:block;padding:0}main{padding:0}.side,.no-print{display:none!important}
  .sec{margin-bottom:var(--s8)}#tables,#measures{break-before:page}
  .card,.tbl tr,.measure,.diagram{break-inside:avoid}
  .diagram{background-image:none}.diagram .dg{min-width:0}
  h2,h4{break-after:avoid}
  a{text-decoration:none}
}`;
}

// The handler attribute is a named constant because the preview strips it by exact match, not by pattern.
const SEARCH_ONINPUT = ' oninput="docFilter(this.value)"';

// A tiny inline script for the EXPORTED file the reader opens. The in-app preview strips it (see renderDoc): its
// iframe has no allow-scripts, so leaving it in only produced console errors. It filters tables and measures as the
// reader types, and opens every formula before printing so the paper shows what the screen folds.
function searchScript(): string {
  return `<script>function docFilter(q){q=(q||'').trim().toLowerCase();document.querySelectorAll('[data-find]').forEach(function(e){e.classList.toggle('doc-hidden',!!q&&e.getAttribute('data-find').indexOf(q)<0);});document.querySelectorAll('.fold').forEach(function(f){f.classList.toggle('doc-hidden',!!q&&!f.querySelector('[data-find]:not(.doc-hidden)'));});}`
    + `window.addEventListener('beforeprint',function(){document.querySelectorAll('details.f:not(.src)').forEach(function(d){d.setAttribute('open','');});});</script>`;
}

// ---- Markdown rendering ---------------------------------------------------------------------------

/** Mermaid text for the relationship diagram: GitHub, Azure DevOps and most wikis draw it. */
function mermaidEr(graph: ModelGraph): string {
  const q = (s: string) => `"${NL(s).replace(/"/g, "'").replace(/[\r\n]+/g, ' ')}"`;
  const lines = graph.relationships.map((r) => {
    const left = r.fromCardinality === 'Many' ? '}o' : '||';
    const right = r.toCardinality === 'Many' ? 'o{' : '||';
    return `    ${q(r.fromTable)} ${left}${r.isActive ? '--' : '..'}${right} ${q(r.toTable)} : ${q(r.fromColumn)}`;
  });
  return ['erDiagram', ...lines].join('\n');
}

function renderMarkdown(dto: DocModelDto, cfg: DocConfig, b: DocBranding): string {
  const h = dto.header;
  const out: string[] = [];
  const p = (s = '') => out.push(s);
  const code = (lang: string, body: string) => { p('```' + lang); p(body); p('```'); p(); };
  const mdcell = (s: unknown) => esc2md(NL(s));
  const title = has(b.title) ? b.title : (NL(h.name) || 'Semantic model');
  const graphByName = new Map(dto.graph.tables.map((t) => [t.name, t]));
  const colsOf = (n: string) => dto.columns.filter((c) => c.table === n);

  p(`# ${title}: documentation`);
  p();
  const meta = [has(b.date) ? b.date : '', has(b.author) ? b.author : '', has(b.companyName) ? b.companyName : ''].filter(Boolean).join(' · ');
  if (meta) { p(meta); p(); }
  if (cfg.narrative && dto.modelNarrative) { const block = authoredMarkdown(dto.modelNarrative, 'overview'); if (block) { p(block); p(); } }
  if (has(h.description)) { p(h.description!.trim()); p(); }
  p(`**${fmtNum(h.tableCount)}** tables · **${fmtNum(h.measureCount)}** measures · **${fmtNum(h.columnCount)}** columns · **${fmtNum(h.relationshipCount)}** relationships`); p();
  if (cfg.readinessScorecard && dto.readiness) { p(`AI readiness **${dto.readiness.grade}** (${Math.round(dto.readiness.overall)} of 100)`); p(); }

  if (cfg.diagram) {
    p('## How the tables connect'); p();
    if (dto.graph.relationships.length) {
      code('mermaid', mermaidEr(dto.graph));
      p('A dotted line is a link that is not active.'); p();
    } else { p('No table is linked to another table.'); p(); }
    const linked = new Set(dto.graph.relationships.flatMap((r) => [r.fromTable, r.toTable]));
    const isolated = dto.graph.tables.filter((t) => !linked.has(t.name));
    if (isolated.length) {
      const byName = new Map(dto.tables.map((t) => [t.name, t]));
      const groups = new Map<string, string[]>();
      for (const g of isolated) {
        const t = byName.get(g.name);
        const k = (t ? kindWord(t, g, colsOf(g.name)) : '') || 'helper table';
        const list = groups.get(k);
        if (list) list.push(g.name); else groups.set(k, [g.name]);
      }
      p(`Not linked to other tables: ${[...groups.entries()].map(([k, ns]) => `${ns.map(mdcell).join(', ')} (${k})`).join('; ')}`); p();
    }
  }

  if (cfg.perTableDetail && dto.tables.length) {
    p('## Tables'); p();
    for (const t of tableOrder(dto)) {
      const g = graphByName.get(t.name);
      const cols = colsOf(t.name);
      const words = [t.isHidden ? 'hidden' : '', kindWord(t, g, cols)].filter(Boolean).join(', ');
      p(`### ${NL(t.name)}`); p();
      if (words) { p(`_${words}_`); p(); }
      if (has(t.description)) { p(t.description!.trim()); p(); }
      if (cfg.narrative && t.narrative) for (const s of t.narrative.sections) if (has(s.markdown)) { p(`> **${prettyKey(s.key)}:** ${s.markdown.replace(/\n/g, ' ').trim()}`); p(); }
      const mine = dto.graph.relationships.filter((r) => r.fromTable === t.name || r.toTable === t.name);
      if (mine.length) { p(`Linked to: ${mine.map((r) => { const other = r.fromTable === t.name ? r.toTable : r.fromTable; return `${mdcell(other)} on ${mdcell(r.fromTable === t.name ? r.fromColumn : r.toColumn)}${r.isActive ? '' : ' (not active)'}`; }).join(', ')}`); p(); }
      if (cfg.hierarchies) for (const hi of t.hierarchies) { p(`Drill path ${mdcell(hi.name)}: ${hi.levels.map((l) => mdcell(l.name)).join(' > ')}`); p(); }
      if (cfg.columnsDetail && cols.length) {
        p('| Column | Type | What it holds |'); p('| --- | --- | --- |');
        cols.forEach((c) => p(`| ${mdcell(c.name)}${c.isKey ? ' (key)' : ''}${c.isHidden && !t.isHidden ? ' (hidden)' : ''} | ${mdcell(typeWord(c.dataType))} | ${mdcell(c.description)} |`)); p();
      }
    }
  }

  if (cfg.measuresIndex && dto.measures.length) {
    p('## Measures'); p();
    for (const [folder, ms] of measureFolders(dto.measures)) {
      p(`### ${folderLabel(folder)}`); p();
      for (const m of ms) {
        const words = [m.isHidden ? 'hidden' : '', formatWord(m.formatString)].filter(Boolean).join(', ');
        p(`- **${NL(m.name)}**${words ? ` (${words})` : ''}${has(m.description) ? `: ${m.description!.replace(/\n/g, ' ')}` : ''}`);
        if (cfg.narrative && m.narrative) for (const s of m.narrative.sections) if (has(s.markdown)) p(`  > **${prettyKey(s.key)}:** ${s.markdown.replace(/\n/g, ' ').trim()}`);
        if (cfg.daxExpressions && has(m.expression)) { p(); code('dax', m.expression!.trim()); }
      }
      p();
    }
  }

  if (cfg.relationships && dto.graph.relationships.length) {
    p('## Relationships'); p(); p('| From | To | Rows | Filter | Active |'); p('| --- | --- | --- | --- | --- |');
    dto.graph.relationships.forEach((r) => p(`| ${mdcell(r.fromTable)} ${mdcell(r.fromColumn)} | ${mdcell(r.toTable)} ${mdcell(r.toColumn)} | ${relWord(r)} | ${filterWord(r)} | ${r.isActive ? 'Yes' : 'No'} |`)); p();
  }

  const calcTables = dto.tables.filter((t) => t.isCalculationGroup);
  if (cfg.calcGroups && calcTables.length) {
    p('## Calculation groups'); p();
    for (const t of calcTables) {
      p(`### ${NL(t.name)}`); p();
      if (has(t.description)) { p(t.description!.trim()); p(); }
      for (const ci of [...t.calcItems].sort((a, c) => a.ordinal - c.ordinal)) {
        p(`- **${NL(ci.name)}**`);
        if (cfg.daxExpressions && has(ci.expression)) { p(); code('dax', ci.expression!.trim()); }
      }
      p();
    }
  }
  if (cfg.kpis && dto.kpis.length) {
    p('## KPIs'); p();
    for (const k of dto.kpis) {
      p(`### ${NL(k.measure)}`); p();
      if (cfg.daxExpressions) for (const [label, expr] of [['Target', k.targetExpression], ['Status', k.statusExpression], ['Trend', k.trendExpression]] as const) if (has(expr)) { p(`${label}:`); p(); code('dax', expr!.trim()); }
    }
  }
  if (cfg.rls && dto.roles.length) {
    p('## Roles and security'); p();
    for (const r of dto.roles) {
      p(`### ${NL(r.name)} (${prettyKey(NL(r.modelPermission)).toLowerCase()})`); p();
      if (r.tableFilters.length) { for (const f of r.tableFilters) { p(`- **${NL(f.table)}**`); p(); code('dax', NL(f.filterExpression).trim()); } } else { p('_No row filters._'); p(); }
    }
  }
  if (cfg.perspectives && dto.perspectives && dto.perspectives.length) {
    p('## Perspectives'); p();
    for (const persp of dto.perspectives) {
      p(`### ${NL(persp.name)}`); p();
      if (has(persp.description)) { p(persp.description!.trim()); p(); }
      const members = persp.members ?? [];
      if (members.length) { for (const m of members) p(`- ${prettyMember(m)}`); p(); }
      else { p('_No members._'); p(); }
    }
  }
  if (cfg.lineage && (dto.tables.some((t) => t.partitions.length) || dto.dataSources.length || dto.expressions.length)) {
    p('## Where the data comes from'); p();
    const rows = dto.tables.filter((t) => t.partitions.length);
    if (rows.length) {
      p('| Table | Made by | Data |'); p('| --- | --- | --- |');
      rows.forEach((t) => {
        const pt = t.partitions[0];
        const kind = SOURCE_KIND[(pt.sourceType ?? '').toLowerCase()] ?? prettyKey(pt.sourceType ?? '').toLowerCase();
        p(`| ${mdcell(t.name)} | ${mdcell(kind)} | ${mdcell(MODE_WORD[(pt.mode ?? '').toLowerCase()] ?? '')} |`);
      }); p();
    }
    if (dto.dataSources.length) { p('| Data source | Type |'); p('| --- | --- |'); dto.dataSources.forEach((d) => p(`| ${mdcell(d.name)} | ${mdcell(prettyKey(d.type ?? '').toLowerCase())} |`)); p(); }
    for (const e of dto.expressions) {
      p(`### ${NL(e.name)} (${prettyKey(e.kind ?? '').toLowerCase()})`); p();
      if (has(e.description)) { p(e.description!.trim()); p(); }
      if (cfg.daxExpressions && has(e.expression)) code('m', e.expression!.trim());
    }
  }
  if (cfg.storageStats && dto.storageAvailable && dto.storage && !dto.storage.error) {
    const storageMode = dto.storage.storageMode === 'import' || dto.storage.storageMode === 'directLake' ? dto.storage.storageMode : 'unknown';
    const totalLabel = storageMode === 'import' ? 'Total model size' : storageMode === 'directLake' ? 'Resident storage estimate' : 'Observed column storage';
    const shareLabel = storageMode === 'import' ? '% model' : storageMode === 'directLake' ? '% resident' : '% observed';
    const fallback = storageMode === 'unknown' ? 'Storage mode could not be confirmed. Storage sizes and row counts may reflect only data currently in memory, so they are not treated as full model totals.' : '';
    p('## Storage'); p(); p(`${totalLabel}: **${fmtBytes(dto.storage.modelSize)}**.`); p();
    if (storageMode !== 'import' && (dto.storage.caveat || fallback)) { p(`> ${dto.storage.caveat || fallback}`); p(); }
    p(`| Table | Rows | Size | ${shareLabel} |`); p('| --- | ---: | ---: | ---: |');
    [...dto.storage.tables].sort((a, c) => c.size - a.size).slice(0, 30).forEach((t) => p(`| ${mdcell(t.name)} | ${t.rows == null ? 'Not available' : fmtNum(t.rows)} | ${fmtBytes(t.size)} | ${(t.pctOfModel ?? 0).toFixed(1)}% |`)); p();
  }
  if (cfg.narrative && dto.modelNarrative) for (const [k, label] of [['glossary', 'Glossary'], ['methodology', 'Method']] as const) {
    const s = dto.modelNarrative.sections.find((x) => x.key === k);
    if (s && has(s.markdown)) { p(`## ${label}`); p(); p(s.markdown.trim()); p(); }
  }
  p('Made with Semanticus');
  return out.join('\n');
}

function authoredMarkdown(n: DocNarrative, key: string): string {
  const s = n.sections.find((x) => x.key === key);
  if (!s || !has(s.markdown)) return '';
  const by = writtenBy(n);
  const quoted = s.markdown.trim().split('\n').map((line) => `> ${line}`).join('\n');
  return by ? `> _${by}_\n>\n${quoted}` : quoted;
}

// Escape Markdown table-breaking characters in a cell.
function esc2md(s: string): string { return NL(s).replace(/\|/g, '\\|').replace(/\n/g, ' '); }
