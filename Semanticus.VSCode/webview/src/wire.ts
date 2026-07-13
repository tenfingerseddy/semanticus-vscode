// Shared wire shapes returned by the engine (camelCase JSON). Declared ONCE so an engine field
// addition lands in a single place instead of drifting across each tab's private copy.

export interface ConnectionStatus {
  connected: boolean;
  kind?: string;       // "xmla" | "local"
  dataSource?: string;
  message?: string;
}

export interface ColumnDef {
  name: string;
  type?: string;
}

export interface ResultSet {
  columns: ColumnDef[];
  rows: unknown[][];
  rowCount: number;
  truncated: boolean;
  elapsedMs: number;
  error?: string;
}

// A column row from list_columns (Semanticus.Engine/Protocol.cs ColumnRow). Shared by the Columns audit grid
// and the Diagram canvas (which lists columns to drag-create relationships).
export interface ColumnRow {
  ref: string; name: string; table: string; dataType: string; displayFolder: string; formatString: string;
  summarizeBy: string; dataCategory: string; isKey: boolean; isHidden: boolean; isCalculated: boolean;
  hasDescription: boolean; description: string; expression: string; sortByColumn?: string;
}
