// The grain a saved check or a Compare uses when a field the model sorts by another column is one of its groups (join 6, J4).
//
// The grid and Explain group by the field AND its sort-by column, so a month field sorted by a fiscal number is one row per month and each
// cell is that month. A saved check's groups and the Compare chips are exactly the fields the person sees, with no hidden sort column (the SQL
// side of a check has to be the same grain, and a hidden column would change it). For a measure that reads the sort order of the field, such
// as one using ALL on it, the number on those two surfaces can differ from the grid's. Those surfaces say so plainly instead of staying silent.

export interface SortColumn { table: string; name: string; sortByColumn?: string }

/** A field's table and column, from the engine's `column:Table/Name` or a DAX `'Table'[Name]` reference. Null for anything else. */
export function columnOfRef(ref: string): { table: string; name: string } | null {
  const text = (ref ?? '').trim();
  const engine = /^column:([^/]+)\/(.+)$/.exec(text);
  if (engine) return { table: engine[1], name: engine[2] };
  const dax = /^'((?:''|[^'])+)'\[(.+)\]$/.exec(text) ?? /^([A-Za-z_]\w*)\[(.+)\]$/.exec(text);
  if (dax) return { table: dax[1].replace(/''/g, "'"), name: dax[2].replace(/]]/g, ']') };
  return null;
}

/** The names of the grouped fields the model sorts by another column, in the order given. Empty when none is, or the model has not loaded. */
export function sortOrderedFields(refs: string[], columns: SortColumn[]): string[] {
  const out: string[] = [];
  for (const ref of refs ?? []) {
    const c = columnOfRef(ref);
    if (!c) continue;
    const col = (columns ?? []).find((x) => x.table.toLowerCase() === c.table.toLowerCase() && x.name.toLowerCase() === c.name.toLowerCase());
    if (col?.sortByColumn && col.sortByColumn.toLowerCase() !== col.name.toLowerCase() && !out.includes(col.name)) out.push(col.name);
  }
  return out;
}

const join = (names: string[]) => names.length <= 1 ? names.join('') : names.slice(0, -1).join(', ') + ' and ' + names[names.length - 1];

/** The plain sentence for a surface that groups by the fields only, or null when no grouped field has a sort-by column. */
export function grainWords(refs: string[], columns: SortColumn[]): string | null {
  const names = sortOrderedFields(refs, columns);
  if (!names.length) return null;
  return `Compared per ${join(names)}, without ${names.length === 1 ? 'its' : 'their'} sort order. A measure that reads that order (one that uses ALL on the field, say) can show a different number here than in the grid, which keeps it.`;
}
