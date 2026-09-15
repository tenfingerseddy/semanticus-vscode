// Type surface for overviewfacts.mjs (the storagecalc.d.mts pattern: runtime in .mjs, types here).

export type TableKind = 'data' | 'date' | 'calculated' | 'fieldParameter';

export interface OverviewTable {
  name: string;
  ref?: string;
  measures?: number;
  columns?: number;
  isDateTable?: boolean;
  isCalculated?: boolean;
  isFieldParameter?: boolean;
}

export interface OverviewScanTable { name: string; size: number; rows?: number | null }
export interface OverviewScanColumn { table: string; dataSize?: number; dictionarySize?: number; hashIndexSize?: number }

export interface HeroFacts {
  /** How far the model graph has actually been read. Counts print only once it is 'ready' (or unstated, which
   *  means the caller already holds a real read): an unread model is not an empty one. */
  state?: 'loading' | 'error' | 'ready';
  tables: number;
  measures: number;
  relationships: number;
  memoryBytes?: number | null;
  largestTable?: string | null;
  largestShare?: number;
  instructionCount?: number;
  /** Edits this webview has SEEN since the model opened. Not a count of unpublished work. */
  sessionEdits?: number;
}

export interface BandBlock {
  name: string;
  kind: TableKind;
  bytes: number | null;
  rows: number | null;
  columns: number;
  weight: number;
  share: number;
}

export interface BandResult { basis: 'memory' | 'columns'; total: number; blocks: BandBlock[] }

export interface RowSegment { part: 'data' | 'dict' | 'hash' | 'rest' | 'columns'; width: number }

export interface OverviewRow {
  name: string;
  ref: string | null;
  kind: TableKind;
  measures: number;
  columns: number;
  bytes: number | null;
  rows: number | null;
  share: number;
  segments: RowSegment[];
}

export interface ChecksHealth {
  checked?: number; passed?: number; failed?: number; suspect?: number; notVerifiable?: number; missing?: number;
}
export interface ChecksHistory { runs?: { runId?: string; when?: string; live?: boolean; health?: ChecksHealth }[]; note?: string }
export interface ChecksSummary { state: 'none' | 'ok' | 'warn' | 'unknown'; headline: string; detail: string }
/** One run as it comes back from runTests (Semanticus.Engine/LocalEngine.TestSuite.cs TestSuiteRunResult,
 *  camelCased). `persisted` is false unless the caller asked for persistence AND the tier allows it. */
export interface ChecksRun {
  runId?: string; when?: string; live?: boolean; persisted?: boolean; note?: string; error?: string;
  health?: ChecksHealth;
}
export interface ChecksCard extends ChecksSummary {
  /** Which run the card is showing: "Just now, not recorded", "Just now, recorded", or "Last recorded run, …".
   *  Null only when there is no run at all. */
  source: string | null;
  recorded: boolean;
  note: string | null;
  error: string | null;
}
export interface ChangesCard { state: 'none' | 'info'; headline: string; detail: string }
export interface BandBlockGeometry { name: string; share: number; px: number; label: 'full' | 'name' | 'none' }

export const KIND_LABEL: Record<TableKind, string>;

export function tableKind(table: OverviewTable | null | undefined): TableKind;
export function formatBytes(bytes: number | null | undefined): string;
export function formatCount(n: number | null | undefined): string;
export function instructionCount(text: string | null | undefined): number;
export function heroClauses(facts: HeroFacts): string[];
export function heroSentence(facts: HeroFacts): string;
export function memoryBand(input: { tables: OverviewTable[]; scanTables?: OverviewScanTable[] | null }): BandResult;
export const BAND_GAP_PX: number;
export const BAND_SLIVER_PX: number;
export function bandGeometry(
  blocks: { name: string; share: number }[], bandPx: number,
  options?: { gapPx?: number; sliverPx?: number },
): BandBlockGeometry[];
export function blockLabel(name: string, widthPx: number): 'full' | 'name' | 'none';
export function tableRows(input: {
  tables: OverviewTable[]; scanTables?: OverviewScanTable[] | null; scanColumns?: OverviewScanColumn[] | null;
}): OverviewRow[];
export function checksSummary(history: ChecksHistory | null | undefined): ChecksSummary;
export function recordedWhen(iso: string | null | undefined, now?: Date | number | string): string | null;
export function checksCard(input: { history?: ChecksHistory | null; latest?: ChecksRun | null } | null | undefined): ChecksCard;
export function changesCard(input: { edits: number; capped?: boolean } | null | undefined): ChangesCard;
