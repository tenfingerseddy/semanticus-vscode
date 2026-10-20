// Shared wire shapes returned by the engine (camelCase JSON). Declared ONCE so an engine field
// addition lands in a single place instead of drifting across each tab's private copy.

export interface ConnectionStatus {
  connected: boolean;
  kind?: string;       // "xmla" | "local"
  dataSource?: string;
  // The QUERY TARGET identity (Semanticus.Engine ConnectionStatus, camelCased) — the database actually
  // attached (distinct from the editing-origin liveDatabase on SessionInfo), the registry connection id,
  // and the account this connection authenticated as (null = honestly unknown). Evidence provenance keys
  // on these: switching database A→B on one endpoint, or re-authenticating under another account, is a
  // context change even though the endpoint string is identical.
  database?: string;
  connectionId?: string;
  account?: string;
  message?: string;
}

export interface ColumnDef {
  name: string;
  type?: string;
  /** The model's own format string for a measure column, sent with an answer so a percent reads as a percent wherever
   *  the answer is drawn (a table, a card, a chart). Absent for a column that is not a measure, and on a result the
   *  engine ran from typed DAX, which carries no formats. */
  format?: string;
}

export interface ResultSet {
  columns: ColumnDef[];
  rows: unknown[][];
  rowCount: number;
  truncated: boolean;
  elapsedMs: number;
  error?: string;
  query?: string;
  cancelled?: boolean;
  /** Set by foldSortColumns: for each group-by position, the model's sort-by value of every label (daxvisual.tsx). */
  sortRank?: (Map<string, number | string | null> | null)[];
}

export function rowAnnouncement(rowCount: number, truncated: boolean): string {
  if (rowCount === 1 && !truncated) return '1 row';
  const rows = `${rowCount.toLocaleString()} row${rowCount === 1 ? '' : 's'}`;
  return truncated ? `${rows} shown. More rows exist.` : rows;
}

export function timingAnnouncement(ms: number): string {
  if (ms < 1) return 'under 1 ms';
  // A second or more is written in seconds, as the row header does ("6.8 s"), never "6374.2 ms" (v-batch1).
  if (ms >= 1000) return `${(ms / 1000).toFixed(ms >= 10000 ? 0 : 1)} s`;
  return Number.isInteger(ms) ? `${ms} ms` : `${ms.toFixed(1)} ms`;
}

export function queryIdentity(query?: string | null): string | null {
  if (!query || !query.trim()) return null;
  const one = query.trim().replace(/\s+/g, ' ');
  return one.length <= 120 ? one : `${one.slice(0, 117)}...`;
}

// A column row from list_columns (Semanticus.Engine/Protocol.cs ColumnRow). Shared by the Columns audit grid
// and the Diagram canvas (which lists columns to drag-create relationships).
export interface ColumnRow {
  ref: string; name: string; table: string; dataType: string; displayFolder: string; formatString: string;
  summarizeBy: string; dataCategory: string; isKey: boolean; isHidden: boolean; isCalculated: boolean;
  hasDescription: boolean; description: string; expression: string; sortByColumn?: string;
  sourceColumn?: string | null;   // the partition-output name M operates on (can differ from name; null for calculated)
}
