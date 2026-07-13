// DAX language support for the Studio's CodeMirror editors: a StreamLanguage for highlighting + a model-aware
// completion source (DAX functions/keywords + the live model's tables/measures/columns). Mirrors the VS Code
// tree-side DAX language service (extension.ts) — the function/keyword data is static reference data.
import { StreamLanguage, LanguageSupport } from '@codemirror/language';
import type { CompletionContext, CompletionResult, Completion } from '@codemirror/autocomplete';

export interface DaxModel { tables: string[]; measures: { name: string; table: string }[]; columns: { table: string; name: string }[]; functions: string[]; }
export const EMPTY_MODEL: DaxModel = { tables: [], measures: [], columns: [], functions: [] };

export const DAX_KEYWORDS = ['VAR', 'RETURN', 'NOT', 'IN', 'DEFINE', 'EVALUATE', 'MEASURE', 'ORDER BY', 'START AT', 'ASC', 'DESC'];

// The full DAX function library (~270 functions, all categories). KEEP IN SYNC with src/extension.ts.
export const DAX_FUNCTIONS = [
  // Aggregation
  'APPROXIMATEDISTINCTCOUNT', 'AVERAGE', 'AVERAGEA', 'AVERAGEX', 'COUNT', 'COUNTA', 'COUNTAX', 'COUNTBLANK', 'COUNTROWS', 'COUNTX', 'DISTINCTCOUNT', 'DISTINCTCOUNTNOBLANK', 'MAX', 'MAXA', 'MAXX', 'MIN', 'MINA', 'MINX', 'PRODUCT', 'PRODUCTX', 'SUM', 'SUMX',
  // Date & time
  'CALENDAR', 'CALENDARAUTO', 'DATE', 'DATEDIFF', 'DATEVALUE', 'DAY', 'EDATE', 'EOMONTH', 'HOUR', 'MINUTE', 'MONTH', 'NETWORKDAYS', 'NOW', 'QUARTER', 'SECOND', 'TIME', 'TIMEVALUE', 'TODAY', 'UTCNOW', 'UTCTODAY', 'WEEKDAY', 'WEEKNUM', 'YEAR', 'YEARFRAC',
  // Time intelligence
  'CLOSINGBALANCEMONTH', 'CLOSINGBALANCEQUARTER', 'CLOSINGBALANCEYEAR', 'DATEADD', 'DATESBETWEEN', 'DATESINPERIOD', 'DATESMTD', 'DATESQTD', 'DATESYTD', 'ENDOFMONTH', 'ENDOFQUARTER', 'ENDOFYEAR', 'FIRSTDATE', 'FIRSTNONBLANK', 'LASTDATE', 'LASTNONBLANK', 'NEXTDAY', 'NEXTMONTH', 'NEXTQUARTER', 'NEXTYEAR', 'OPENINGBALANCEMONTH', 'OPENINGBALANCEQUARTER', 'OPENINGBALANCEYEAR', 'PARALLELPERIOD', 'PREVIOUSDAY', 'PREVIOUSMONTH', 'PREVIOUSQUARTER', 'PREVIOUSYEAR', 'SAMEPERIODLASTYEAR', 'STARTOFMONTH', 'STARTOFQUARTER', 'STARTOFYEAR', 'TOTALMTD', 'TOTALQTD', 'TOTALYTD',
  // Filter
  'ALL', 'ALLCROSSFILTERED', 'ALLEXCEPT', 'ALLNOBLANKROW', 'ALLSELECTED', 'CALCULATE', 'CALCULATETABLE', 'EARLIER', 'EARLIEST', 'FILTER', 'KEEPFILTERS', 'LOOKUPVALUE', 'REMOVEFILTERS', 'SELECTEDVALUE',
  // Financial
  'ACCRINT', 'ACCRINTM', 'AMORDEGRC', 'AMORLINC', 'COUPDAYBS', 'COUPDAYS', 'COUPDAYSNC', 'COUPNCD', 'COUPNUM', 'COUPPCD', 'CUMIPMT', 'CUMPRINC', 'DB', 'DDB', 'DISC', 'DOLLARDE', 'DOLLARFR', 'DURATION', 'EFFECT', 'FV', 'INTRATE', 'IPMT', 'ISPMT', 'MDURATION', 'NOMINAL', 'NPER', 'ODDFPRICE', 'ODDFYIELD', 'ODDLPRICE', 'ODDLYIELD', 'PDURATION', 'PMT', 'PPMT', 'PRICE', 'PRICEDISC', 'PRICEMAT', 'PV', 'RATE', 'RECEIVED', 'RRI', 'SLN', 'SYD', 'TBILLEQ', 'TBILLPRICE', 'TBILLYIELD', 'VDB', 'XIRR', 'XNPV', 'YIELD', 'YIELDDISC', 'YIELDMAT',
  // Information
  'COLUMNSTATISTICS', 'CONTAINS', 'CONTAINSROW', 'CONTAINSSTRING', 'CONTAINSSTRINGEXACT', 'CUSTOMDATA', 'HASONEFILTER', 'HASONEVALUE', 'ISAFTER', 'ISBLANK', 'ISCROSSFILTERED', 'ISEMPTY', 'ISERROR', 'ISEVEN', 'ISFILTERED', 'ISINSCOPE', 'ISLOGICAL', 'ISNONTEXT', 'ISNUMBER', 'ISODD', 'ISONORAFTER', 'ISSELECTEDMEASURE', 'ISSUBTOTAL', 'ISTEXT', 'SELECTEDMEASURE', 'SELECTEDMEASUREFORMATSTRING', 'SELECTEDMEASURENAME', 'USERCULTURE', 'USERNAME', 'USEROBJECTID', 'USERPRINCIPALNAME', 'INFO.VIEW.COLUMNS', 'INFO.VIEW.MEASURES', 'INFO.VIEW.RELATIONSHIPS', 'INFO.VIEW.TABLES',
  // Logical
  'AND', 'BITAND', 'BITLSHIFT', 'BITOR', 'BITRSHIFT', 'BITXOR', 'COALESCE', 'FALSE', 'IF', 'IF.EAGER', 'IFERROR', 'NOT', 'OR', 'SWITCH', 'TRUE',
  // Math & trig
  'ABS', 'ACOS', 'ACOSH', 'ACOT', 'ACOTH', 'ASIN', 'ASINH', 'ATAN', 'ATANH', 'CEILING', 'CONVERT', 'COS', 'COSH', 'COT', 'COTH', 'CURRENCY', 'DEGREES', 'DIVIDE', 'EVEN', 'EXP', 'FACT', 'FLOOR', 'GCD', 'INT', 'ISO.CEILING', 'LCM', 'LN', 'LOG', 'LOG10', 'MOD', 'MROUND', 'ODD', 'PI', 'POWER', 'QUOTIENT', 'RADIANS', 'RAND', 'RANDBETWEEN', 'ROUND', 'ROUNDDOWN', 'ROUNDUP', 'SIGN', 'SIN', 'SINH', 'SQRT', 'SQRTPI', 'TAN', 'TANH', 'TRUNC',
  // Parent & child
  'PATH', 'PATHCONTAINS', 'PATHITEM', 'PATHITEMREVERSE', 'PATHLENGTH',
  // Relationship
  'CROSSFILTER', 'RELATED', 'RELATEDTABLE', 'USERELATIONSHIP',
  // Statistical
  'BETA.DIST', 'BETA.INV', 'CHISQ.DIST', 'CHISQ.DIST.RT', 'CHISQ.INV', 'CHISQ.INV.RT', 'COMBIN', 'COMBINA', 'CONFIDENCE.NORM', 'CONFIDENCE.T', 'EXPON.DIST', 'GEOMEAN', 'GEOMEANX', 'LINEST', 'LINESTX', 'MEDIAN', 'MEDIANX', 'NORM.DIST', 'NORM.INV', 'NORM.S.DIST', 'NORM.S.INV', 'PERCENTILE.EXC', 'PERCENTILE.INC', 'PERCENTILEX.EXC', 'PERCENTILEX.INC', 'POISSON.DIST', 'RANK.EQ', 'RANKX', 'SAMPLE', 'STDEV.P', 'STDEV.S', 'STDEVX.P', 'STDEVX.S', 'T.DIST', 'T.DIST.2T', 'T.DIST.RT', 'T.INV', 'T.INV.2T', 'VAR.P', 'VAR.S', 'VARX.P', 'VARX.S',
  // Table manipulation
  'ADDCOLUMNS', 'ADDMISSINGITEMS', 'CROSSJOIN', 'CURRENTGROUP', 'DATATABLE', 'DETAILROWS', 'DISTINCT', 'EXCEPT', 'FILTERS', 'GENERATE', 'GENERATEALL', 'GENERATESERIES', 'GROUPBY', 'IGNORE', 'INTERSECT', 'NATURALINNERJOIN', 'NATURALLEFTOUTERJOIN', 'ROLLUP', 'ROLLUPADDISSUBTOTAL', 'ROLLUPGROUP', 'ROLLUPISSUBTOTAL', 'ROW', 'SELECTCOLUMNS', 'SUMMARIZE', 'SUMMARIZECOLUMNS', 'TOPN', 'TREATAS', 'UNION', 'VALUES',
  // Text
  'COMBINEVALUES', 'CONCATENATE', 'CONCATENATEX', 'EXACT', 'FIND', 'FIXED', 'FORMAT', 'LEFT', 'LEN', 'LOWER', 'MID', 'REPLACE', 'REPT', 'RIGHT', 'SEARCH', 'SUBSTITUTE', 'TRIM', 'UNICHAR', 'UNICODE', 'UPPER', 'VALUE',
  // Window
  'INDEX', 'MATCHBY', 'OFFSET', 'ORDERBY', 'PARTITIONBY', 'RANK', 'ROWNUMBER', 'WINDOW',
  // Other
  'BLANK', 'ERROR', 'EVALUATEANDLOG', 'TOCSV', 'TOJSON',
];

export interface DaxSig { params: string[]; doc: string; }
export const DAX_SIGNATURES: Record<string, DaxSig> = {
  CALCULATE: { params: ['Expression', '[Filter1]', '…'], doc: 'Evaluates an expression in a context modified by the given filters.' },
  CALCULATETABLE: { params: ['Table', '[Filter1]', '…'], doc: 'Evaluates a table expression in a modified filter context.' },
  FILTER: { params: ['Table', 'FilterExpression'], doc: 'Returns the rows of Table where FilterExpression is true.' },
  SUM: { params: ['ColumnName'], doc: 'Adds all the numbers in a column.' },
  SUMX: { params: ['Table', 'Expression'], doc: 'Sums Expression evaluated for each row of Table.' },
  AVERAGE: { params: ['ColumnName'], doc: 'Averages all the numbers in a column.' },
  AVERAGEX: { params: ['Table', 'Expression'], doc: 'Averages Expression over each row of Table.' },
  COUNTROWS: { params: ['[Table]'], doc: 'Counts the rows of Table (or the current context).' },
  DISTINCTCOUNT: { params: ['ColumnName'], doc: 'Counts distinct values in a column.' },
  DIVIDE: { params: ['Numerator', 'Denominator', '[AlternateResult]'], doc: 'Safe division; returns AlternateResult (default BLANK) on divide-by-zero.' },
  IF: { params: ['LogicalTest', 'ResultIfTrue', '[ResultIfFalse]'], doc: 'Returns one value if the test is true, another if false.' },
  SWITCH: { params: ['Expression', 'Value1', 'Result1', '…', '[Else]'], doc: 'Matches Expression against values, returning the matching result.' },
  COALESCE: { params: ['Expression1', 'Expression2', '…'], doc: 'Returns the first non-blank expression.' },
  RELATED: { params: ['ColumnName'], doc: 'Returns a related value from another table (many→one).' },
  RELATEDTABLE: { params: ['Table'], doc: 'Returns the related rows of Table (one→many).' },
  USERELATIONSHIP: { params: ['Column1', 'Column2'], doc: 'Activates an inactive relationship for the calculation.' },
  ALL: { params: ['[TableOrColumn]', '…'], doc: 'Removes filters from a table/columns.' },
  ALLEXCEPT: { params: ['Table', 'Column1', '…'], doc: 'Removes filters from Table except the listed columns.' },
  REMOVEFILTERS: { params: ['[TableOrColumn]', '…'], doc: 'Clears filters from the given tables/columns.' },
  KEEPFILTERS: { params: ['Expression'], doc: 'Adds filters without overriding existing ones.' },
  VALUES: { params: ['TableOrColumn'], doc: 'Distinct values of a column (incl. a blank row if any).' },
  SELECTEDVALUE: { params: ['ColumnName', '[AlternateResult]'], doc: 'The single value in scope, else AlternateResult.' },
  TREATAS: { params: ['Table', 'Column1', '…'], doc: 'Applies a table expression as filters on the given columns.' },
  SUMMARIZECOLUMNS: { params: ['GroupBy1', '…', '[Filter]', '[Name]', '[Expr]'], doc: 'Groups and aggregates across columns and filters.' },
  ADDCOLUMNS: { params: ['Table', 'Name1', 'Expr1', '…'], doc: 'Adds calculated columns to Table.' },
  TOPN: { params: ['N', 'Table', '[OrderBy]', '[Order]'], doc: 'Returns the top N rows of Table.' },
  TOTALYTD: { params: ['Expression', 'Dates', '[Filter]', '[YearEndDate]'], doc: 'Year-to-date evaluation of Expression.' },
  SAMEPERIODLASTYEAR: { params: ['Dates'], doc: 'Shifts the date column back one year.' },
  DATEADD: { params: ['Dates', 'NumberOfIntervals', 'Interval'], doc: 'Shifts dates by day/month/quarter/year intervals.' },
  FORMAT: { params: ['Value', 'FormatString'], doc: 'Formats a value as text using a format string.' },
  LOOKUPVALUE: { params: ['ResultColumn', 'SearchColumn', 'SearchValue', '…'], doc: 'Returns the result value for matching search columns.' },
};

const FN_UPPER = new Set(DAX_FUNCTIONS.map((f) => f.toUpperCase()));
const KW_UPPER = new Set(['VAR', 'RETURN', 'NOT', 'IN', 'DEFINE', 'EVALUATE', 'MEASURE', 'ORDER', 'BY', 'START', 'AT', 'ASC', 'DESC', 'TRUE', 'FALSE']);

// StreamLanguage tokenizer for DAX → standard token names that CodeMirror's default highlight style colours.
const daxStream = StreamLanguage.define<{ block: boolean }>({
  startState: () => ({ block: false }),
  token(stream, state) {
    if (state.block) { if (stream.skipTo('*/')) { stream.match('*/'); state.block = false; } else stream.skipToEnd(); return 'comment'; }
    if (stream.eatSpace()) return null;
    if (stream.match('//') || stream.match('--')) { stream.skipToEnd(); return 'comment'; }
    if (stream.match('/*')) { state.block = true; return 'comment'; }
    const c = stream.peek()!;
    if (c === '"') { stream.next(); while (!stream.eol()) { if (stream.next() === '"') { if (stream.peek() === '"') stream.next(); else break; } } return 'string'; }
    if (c === "'") { stream.next(); while (!stream.eol()) { if (stream.next() === "'") { if (stream.peek() === "'") stream.next(); else break; } } return 'typeName'; }   // 'Table'
    if (c === '[') { stream.next(); while (!stream.eol() && stream.next() !== ']') { /* column/measure */ } return 'propertyName'; }
    if (/[0-9]/.test(c) || (c === '.' && /[0-9]/.test(stream.string[stream.pos + 1] || ''))) { stream.match(/^[0-9]*\.?[0-9]+([eE][+-]?[0-9]+)?/); return 'number'; }
    if (/[A-Za-z_]/.test(c)) { const w = stream.match(/^[A-Za-z_][A-Za-z0-9_.]*/) as RegExpMatchArray | null; const word = (w && w[0]) || ''; const u = word.toUpperCase(); return KW_UPPER.has(u) ? 'keyword' : FN_UPPER.has(u) ? 'keyword' : 'variableName'; }
    if ('()[]{}'.includes(c)) { stream.next(); return 'bracket'; }
    if ('+-*/=<>&^|,.:'.includes(c)) { stream.next(); return 'operator'; }
    stream.next(); return null;
  },
});
export const daxLanguage = new LanguageSupport(daxStream);

const fnInfo = (fn: string): string | undefined => {
  const s = DAX_SIGNATURES[fn]; return s ? `${fn}(${s.params.join(', ')})\n\n${s.doc}` : undefined;
};

// Each DAX-editor surface biases completion toward the functions it needs — ADDITIVELY (the whole library still
// completes; these just sort to the top via `boost`). calc-item bodies wrap SELECTEDMEASURE(); a format-string
// expression centres on SELECTEDMEASUREFORMATSTRING; an RLS row-filter leans on the dynamic-security helpers.
export type DaxScope = 'calcitem' | 'formatstring' | 'rls';
const SCOPE_BOOST: Record<DaxScope, string[]> = {
  calcitem: ['SELECTEDMEASURE', 'SELECTEDMEASURENAME', 'SELECTEDMEASUREFORMATSTRING', 'ISSELECTEDMEASURE', 'CALCULATE'],
  formatstring: ['SELECTEDMEASUREFORMATSTRING', 'SELECTEDMEASURE', 'FORMAT'],
  rls: ['USERPRINCIPALNAME', 'USERNAME', 'USEROBJECTID', 'LOOKUPVALUE', 'PATH', 'PATHCONTAINS', 'PATHITEM'],
};
export interface DaxCompletionOpts { scope?: DaxScope; table?: string }

// Model-aware DAX completion. getModel returns the current tables/measures/columns/functions; opts biases ranking
// per editor surface (scope) and, for RLS, ranks the target table's own columns first.
export function daxCompletionSource(getModel: () => DaxModel, opts: DaxCompletionOpts = {}) {
  const boosted = opts.scope ? new Set(SCOPE_BOOST[opts.scope]) : null;
  const scopeTable = opts.table?.toLowerCase();
  const boost = (fn: string) => (boosted?.has(fn) ? 99 : undefined);
  return (ctx: CompletionContext): CompletionResult | null => {
    const m = getModel();
    const line = ctx.state.doc.lineAt(ctx.pos);
    const before = line.text.slice(0, ctx.pos - line.from);

    // 1) 'Table'[partial  OR  Table[partial  → that table's columns (bare name).
    const tbl = /'([^']+)'\[([^\]]*)$|(?:^|[^\w'\]])([A-Za-z_]\w*)\[([^\]]*)$/.exec(before);
    if (tbl) {
      const table = (tbl[1] ?? tbl[3] ?? '').trim().toLowerCase();
      const typed = tbl[2] ?? tbl[4] ?? '';
      const cols = m.columns.filter((c) => c.table.toLowerCase() === table);
      if (cols.length) return { from: ctx.pos - typed.length, options: cols.map((c) => ({ label: c.name, type: 'property', apply: c.name + ']' })), validFor: /^[^\]]*$/ };
    }

    // 2) bare [partial  (not preceded by a name/quote) → measures + columns by bare name. In RLS scope the target
    //    table's own columns rank first (the row filter is almost always about this table).
    const bare = /(?:^|[^\w'\]])\[([^\]]*)$/.exec(before);
    if (bare) {
      const typed = bare[1];
      const opts2: Completion[] = [
        ...m.measures.map((x) => ({ label: x.name, type: 'variable', detail: 'measure', apply: x.name + ']' })),
        ...dedupeColNames(m.columns, scopeTable).map((x) => ({ label: x.name, type: 'property', detail: 'column', apply: x.name + ']', boost: x.own ? 99 : undefined })),
      ];
      return { from: ctx.pos - typed.length, options: opts2, validFor: /^[^\]]*$/ };
    }

    // 3) a word → functions / keywords / tables / measures / columns (scope-boosted).
    const word = ctx.matchBefore(/[A-Za-z_][\w.]*/);
    if (!word && !ctx.explicit) return null;
    const opts3: Completion[] = [
      ...DAX_FUNCTIONS.map((fn) => ({ label: fn, type: 'function', detail: 'DAX', info: fnInfo(fn), apply: fn + '(', boost: boost(fn) })),
      ...m.functions.map((fn) => ({ label: fn, type: 'function', detail: 'model function', apply: fn + '(' })),
      ...DAX_KEYWORDS.map((kw) => ({ label: kw, type: 'keyword', apply: kw + ' ' })),
      ...m.tables.map((t) => ({ label: `'${t}'`, type: 'class', detail: 'table', boost: scopeTable && t.toLowerCase() === scopeTable ? 99 : undefined })),
      ...m.measures.map((x) => ({ label: `[${x.name}]`, type: 'variable', detail: 'measure' })),
      ...m.columns.map((c) => ({ label: `'${c.table}'[${c.name}]`, type: 'property', detail: 'column · ' + c.table, boost: scopeTable && c.table.toLowerCase() === scopeTable ? 99 : undefined })),
    ];
    return { from: word ? word.from : ctx.pos, options: opts3, validFor: /^[\w'[\].]*$/ };
  };
}

// Distinct bare column names; `own` marks a name that belongs to the scope table (RLS) so it can rank first.
function dedupeColNames(cols: { table: string; name: string }[], scopeTable?: string): { name: string; own: boolean }[] {
  const seen = new Set<string>();
  const out: { name: string; own: boolean }[] = [];
  for (const c of cols) {
    if (seen.has(c.name)) continue;
    seen.add(c.name);
    out.push({ name: c.name, own: !!scopeTable && c.table.toLowerCase() === scopeTable });
  }
  return out;
}
