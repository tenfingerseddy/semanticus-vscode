export interface TryMeasure { ref: string; name: string; table: string; expression?: string }
export interface TryColumn { name: string; table: string }

/** A narrow, empirical explanation: an aggregate over an empty table or time period returns blank.
 * Other formula shapes need other evidence. Do not infer a cause from the name or the saved meaning. */
export function blankCalculationProbe(ref: string, measures: TryMeasure[], query: string): { query: string; emptyReason: string } | null {
  const tokens = (body: string) => (body.match(/\/\/[^\n]*|--[^\n]*|\/\*[\s\S]*?\*\/|"(?:[^"]|"")*"|'(?:[^']|'')*'|\[(?:[^\]]|\]\])*\]|[\p{L}_][\p{L}\p{N}_]*|[^\s]/gu) ?? [])
    .filter(t => !t.startsWith('//') && !t.startsWith('--') && !t.startsWith('/*'));
  const unname = (s: string) => s.startsWith("'") ? s.slice(1, -1).replace(/''/g, "'")
    : s.startsWith('[') ? s.slice(1, -1).replace(/]]/g, ']') : s;
  const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();
  const tableToken = (s: string) => /^'(?:[^']|'')*'$|^[\p{L}_][\p{L}\p{N}_]*$/u.test(s);
  const table = (s: string) => "'" + s.replace(/'/g, "''") + "'";
  const visited = new Set<string>();
  const resolve = (code: string[]): string[] => {
    if (!(code.length === 1 && code[0].startsWith('[') || code.length === 2 && tableToken(code[0]) && code[1].startsWith('['))) return code;
    const name = unname(code.at(-1)!);
    const target = measures.find(m => same(m.name, name) && (code.length === 1 || same(m.table, unname(code[0]))));
    if (!target || visited.has(target.ref)) return [];
    visited.add(target.ref);
    return resolve(tokens(target.expression ?? ''));
  };
  const call = (code: string[]) => {
    if (code[1] !== '(' || code.at(-1) !== ')') return null;
    const args: string[][] = [[]];
    let depth = 0;
    for (const token of code.slice(2, -1)) {
      if (token === '(') depth++;
      if (token === ')' && --depth < 0) return null;
      if (token === ',' && depth === 0) args.push([]); else args.at(-1)!.push(token);
    }
    return depth === 0 ? { name: code[0].toUpperCase(), args } : null;
  };
  const target = measures.find(m => m.ref === ref);
  if (!target) return null;
  let aggregate = call(resolve(tokens(target.expression ?? '')));
  let dates: string | null = null;
  const time = { TOTALYTD: 'DATESYTD', TOTALQTD: 'DATESQTD', TOTALMTD: 'DATESMTD' };
  if (aggregate && aggregate.name in time) {
    const date = aggregate.args[1];
    if (aggregate.args.length !== 2 || date.length !== 2 || !tableToken(date[0]) || !date[1].startsWith('[')) return null;
    dates = `${time[aggregate.name as keyof typeof time]}(${date.join('')})`;
    aggregate = call(resolve(aggregate.args[0]));
  }
  if (!aggregate) return null;
  const first = aggregate.args[0];
  const rowAggregate = aggregate.name === 'SUMX' && aggregate.args.length === 2
    || aggregate.name === 'COUNTROWS' && aggregate.args.length === 1;
  const columnAggregate = ['SUM', 'AVERAGE', 'MIN', 'MAX', 'COUNT', 'COUNTA'].includes(aggregate.name) && aggregate.args.length === 1;
  if (!first?.length || !tableToken(first[0]) || !(rowAggregate && first.length === 1
    || columnAggregate && first.length === 2 && first[1].startsWith('['))) return null;
  const fact = unname(first[0]);
  const count = `COALESCE(COUNTROWS(${table(fact)}), 0)`;
  const name = `[${target.name.replace(/]/g, ']]')}]`;
  const define = query.startsWith('DEFINE\n') ? query.slice(0, query.lastIndexOf('\nEVALUATE ') + 1) : '';
  return { query: `${define}EVALUATE ROW("__value", ${name}, "__rows", ${dates ? `CALCULATE(${count}, ${dates})` : count})`,
    emptyReason: `${fact} has no rows${dates ? ' in the period used by this calculation' : ''}.` };
}

// A scope-sensitive measure needs the columns it tests, including tests in its measure dependencies.
// Only use existing model columns. Strings and comments cannot suggest a grid.
export function tryCalculationQuery(ref: string, measures: TryMeasure[], columns: TryColumn[]): string {
  const name = ref.slice(ref.indexOf('/') + 1);
  const bracket = (s: string) => `[${s.replace(/]/g, ']]')}]`;
  const quote = (s: string) => `"${s.replace(/"/g, '""')}"`;
  const table = (s: string) => `'${s.replace(/'/g, "''")}'`;
  const unbracket = (s: string) => s.slice(1, -1).replace(/]]/g, ']');
  const same = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();
  const groups = new Set<string>();
  const visited = new Set<string>();
  const definitions: TryMeasure[] = [];
  function visit(measure: TryMeasure) {
    if (visited.has(measure.ref)) return;
    visited.add(measure.ref);
    const tokens = (measure.expression ?? '').match(/\/\/[^\n]*|--[^\n]*|\/\*[\s\S]*?\*\/|"(?:[^"]|"")*"|'(?:[^']|'')*'|\[(?:[^\]]|\]\])*\]|[\p{L}_][\p{L}\p{N}_]*|[^\s]/gu) ?? [];
    const code = tokens.filter((t) => !t.startsWith('//') && !t.startsWith('--') && !t.startsWith('/*'));
    for (let i = 0; i < code.length; i++) {
      if (/^(ISINSCOPE|HASONEVALUE|HASONEFILTER|SELECTEDVALUE)$/i.test(code[i]) && code[i + 1] === '(') {
        let home = measure.table, field = code[i + 2];
        if (code[i + 3]?.startsWith('[')) {
          home = field.startsWith("'") ? field.slice(1, -1).replace(/''/g, "'") : field;
          field = code[i + 3];
        }
        if (field?.startsWith('[')) {
          const column = columns.find((c) => same(c.table, home) && same(c.name, unbracket(field)));
          if (column) groups.add(`${table(column.table)}${bracket(column.name)}`);
        }
      }
      if (code[i].startsWith('[')) {
        const previous = code[i - 1];
        const candidateHome = previous && (/^'/.test(previous) || /^[\p{L}_][\p{L}\p{N}_]*$/u.test(previous))
          ? previous.startsWith("'") ? previous.slice(1, -1).replace(/''/g, "'") : previous : undefined;
        const home = candidateHome && (measures.some(m => same(m.table, candidateHome)) || columns.some(c => same(c.table, candidateHome)))
          ? candidateHome : undefined;
        const field = unbracket(code[i]);
        if (columns.some(c => same(c.name, field) && same(c.table, home ?? measure.table))) continue;
        const dependency = measures.find((m) => same(m.name, field) && (!home || same(m.table, home)));
        if (dependency) visit(dependency);
      }
    }
    if (measure.expression?.trim()) definitions.push(measure);
  }
  const measure = measures.find((m) => m.ref === ref);
  if (measure) visit(measure);
  // Query-scoped definitions include the whole working copy closure. Nothing is deployed to the runtime model.
  const define = definitions.length ? 'DEFINE\n' + definitions.map(m => `MEASURE ${table(m.table)}${bracket(m.name)} = ${m.expression}`).join('\n') + '\n' : '';
  const value = `${quote(name)}, ${bracket(name)}`;
  if (!groups.size) return `${define}EVALUATE ROW(${value})`;
  // A small, stable default grid. The query remains visible and can be run unchanged through run_dax.
  const grid = [...groups].join(', ');
  return `${define}EVALUATE TOPN(100, SUMMARIZECOLUMNS(${grid}, ${value}), ${[...groups].map((g) => `${g}, ASC`).join(', ')})\nORDER BY ${grid}`;
}

/** The assistant can read the full run_dax error. A calculation action shows only the next useful step. */
export function calculationFailureWord(error: unknown, context = true): string {
  const text = error instanceof Error ? error.message : typeof error === 'string' ? error : '';
  if (/^The query was stopped\.?$/i.test(text)) return 'The query was stopped.';
  if (/^(Connect a model|No live connection|Not connected|Open a live model)/i.test(text))
    return 'Connect a model that holds data to run this calculation.';
  return (context ? 'The calculation could not run. ' : '') + 'Ask your assistant to check the formula.';
}
