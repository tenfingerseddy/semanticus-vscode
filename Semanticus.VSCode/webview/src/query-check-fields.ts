// A check opened from a Query row reads its measure and groups from the row's own DAX (UAT 18 Oct, round 3, R2).
// The wells hold whatever was built last; an opened row puts its DAX in the editor and leaves the wells alone, so the
// query on screen is the only thing that says what the row is about. Only the query shape the field builder writes is
// read (SUMMARIZECOLUMNS or ROW, optionally inside CALCULATETABLE); anything else returns null and the check opens
// empty, because a guess about a hand-written query is a wrong check that looks right.
interface ModelLike { measures: { name: string; table: string }[]; columns: { table: string; name: string; sortByColumn?: string }[] }
export interface QueryCheckFields { measureRef: string; groupBy: string[]; filterDax: string }

/** The text between the bracket that opens at `open` and its match, skipping strings and quoted names. */
function balanced(text: string, open: number): { inner: string; end: number } | null {
  let depth = 0;
  for (let i = open; i < text.length; i++) {
    const c = text[i];
    if (c === '"' || c === "'") { const q = c; for (i++; i < text.length && !(text[i] === q && text[i + 1] !== q); i++) if (text[i] === q) i++; continue; }
    if (c === '(') depth++;
    else if (c === ')' && --depth === 0) return { inner: text.slice(open + 1, i), end: i };
  }
  return null;
}
/** Top-level comma split (strings, quoted names, brackets and parentheses respected). */
function splitTop(text: string): string[] {
  const out: string[] = []; let depth = 0, start = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"' || c === "'") { const q = c; for (i++; i < text.length && !(text[i] === q && text[i + 1] !== q); i++) if (text[i] === q) i++; continue; }
    if (c === '[') { for (i++; i < text.length && !(text[i] === ']' && text[i + 1] !== ']'); i++) if (text[i] === ']') i++; continue; }
    if (c === '(') depth++; else if (c === ')') depth--;
    else if (c === ',' && depth === 0) { out.push(text.slice(start, i).trim()); start = i + 1; }
  }
  out.push(text.slice(start).trim());
  return out.filter(Boolean);
}
const COLUMN = /^(?:'((?:[^']|'')+)'|([A-Za-z_]\w*))\[((?:[^\]]|\]\])+)\]$/;
const MEASURE = /^\[((?:[^\]]|\]\])+)\]$/;
const NAME = /^"(?:[^"]|"")*"$/;

export function checkFieldsOfDax(dax: string | null | undefined, model: ModelLike): QueryCheckFields | null {
  const text = (dax ?? '').replace(/\r\n/g, '\n').trim();
  const evaluate = /^EVALUATE\s+/i.exec(text);
  if (!evaluate) return null;
  let body = text.slice(evaluate[0].length).replace(/\n\s*ORDER BY[\s\S]*$/i, '').trim();
  let filters: string[] = [];
  const wrap = /^CALCULATETABLE\s*\(/i.exec(body);
  if (wrap) {
    const b = balanced(body, wrap[0].length - 1);
    if (!b || body.slice(b.end + 1).trim()) return null;
    const [table, ...rest] = splitTop(b.inner);
    if (!table) return null;
    body = table; filters = rest;
  }
  const head = /^(SUMMARIZECOLUMNS|ROW)\s*\(/i.exec(body);
  if (!head) return null;
  const call = balanced(body, head[0].length - 1);
  if (!call || body.slice(call.end + 1).trim()) return null;
  const args = splitTop(call.inner);
  const isRow = head[1].toUpperCase() === 'ROW';
  const columns: { table: string; name: string }[] = [];
  const measures: string[] = [];
  for (let i = 0; i < args.length; i++) {
    const m = COLUMN.exec(args[i]);
    if (m && !isRow) { columns.push({ table: (m[1] ?? m[2]).replace(/''/g, "'"), name: m[3].replace(/\]\]/g, ']') }); continue; }
    if (NAME.test(args[i]) && args[i + 1] && MEASURE.test(args[i + 1])) { measures.push(MEASURE.exec(args[++i])![1].replace(/\]\]/g, ']')); continue; }
    return null;
  }
  const measure = model.measures.find((x) => x.name === measures[0]);
  if (!measure) return null;
  const known = columns.map((c) => model.columns.find((x) => x.table === c.table && x.name === c.name));
  if (known.some((c) => !c)) return null;
  // A column a field builder adds only to order another one is not a group the person chose.
  const ordering = new Set(known.map((c) => c!.sortByColumn).filter(Boolean));
  const groupBy = known.filter((c) => !(ordering.has(c!.name) && known.some((o) => o!.sortByColumn === c!.name && o!.table === c!.table))).map((c) => `column:${c!.table}/${c!.name}`);
  return { measureRef: `measure:${measure.table}/${measure.name}`, groupBy, filterDax: filters.join(', ') };
}
