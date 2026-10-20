// The biggest cost on the Size page (UAT P16 and F21: "Nothing to fix" beside one text column that is 54% of the model).
// A column above a clear share of the model is always a finding, whatever the byte floor of the other rules says: on a small
// model every column is under 1 MB and the dictionary rule never fires. Pure, so the page and its test ask the same question.
export const SHARE_PCT = 25;
export const SHARE_FLOOR_BYTES = 64 * 1024;

export interface ShareColumn { table: string; column: string; totalSize: number; pctOfModel: number }
export interface ShareFinding { table: string; column: string; totalSize: number; pct: number }

/** The one column that costs the most, when it is at least SHARE_PCT of the model and not trivially small; otherwise null. */
export function biggestShare(columns: readonly ShareColumn[] | null | undefined): ShareFinding | null {
  let top: ShareColumn | null = null;
  for (const c of columns ?? []) if (!top || c.totalSize > top.totalSize) top = c;
  if (!top || top.pctOfModel < SHARE_PCT || top.totalSize < SHARE_FLOOR_BYTES) return null;
  return { table: top.table, column: top.column, totalSize: top.totalSize, pct: Math.round(top.pctOfModel) };
}

/** The finding's headline: the column and its share. The list shows it before anyone opens a row. */
export function shareWords(f: ShareFinding): string {
  return `${f.column} is ${f.pct}% of the model`;
}

/** What to do about it. */
export function shareAdvice(f: ShareFinding): string {
  return `It takes ${fmt(f.totalSize)}. Ask your assistant to shrink it, or check that the model needs all of it.`;
}
/** The same advice with no assistant to ask: the press that needs one is not offered in words, the page offers Connect beside it. */
export function adviceFor(message: string, assistantConnected: boolean): string {
  const asked = assistantConnected ? message : message.replace(/ Ask your assistant to shrink it, or check that/u, ' Check that');
  return sizeWords(asked);
}
/** The Size words a person reads: the engine's storage terms (dictionary, hash index, cardinality) in plain words (UAT round 2, N19). The engine keeps its own wording for the assistant. */
export function sizeWords(message: string): string {
  return message
    .replace('Dictionary is the largest component:', 'The list of different values is the biggest part:')
    .replace('Often indicates a high-cardinality or wide text column.', 'This often means a column with many different values, or long text.')
    .replace('Hash index is', 'Lookup help is');
}
const fmt = (bytes: number): string => (bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.round(bytes / 1024)} KB`);

/** The words where the page says there is nothing to fix. Until the checks have loaded it says so, never "Nothing to fix". */
export function verdictWords(state: 'loading' | 'failed' | 'done', count: number): string | null {
  if (state === 'loading') return 'Looking for savings...';
  if (state === 'failed') return 'Could not check for savings.';
  return count === 0 ? 'Nothing to fix.' : null;
}
