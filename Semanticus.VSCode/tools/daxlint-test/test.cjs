// Unit tests for the pure DAX analyzer (src/daxLint.ts → out/daxLint.js). No deps; run after `npm run compile`:
//   node tools/daxlint-test/test.cjs
const path = require('node:path');
const { analyzeDax, collectVars, extractDaxSymbols, varDefinition } = require(path.join(__dirname, '..', '..', 'out', 'daxLint.js'));

const idx = {
  tables: new Set(['sales', 'date', 'customer']),
  columnsByTable: new Map([
    ['sales', new Set(['salesamount', 'customerkey', 'orderdatekey'])],
    ['date', new Set(['datekey', 'year'])],
    ['customer', new Set(['customerkey', 'customername'])],
  ]),
  measures: new Set(['total sales', 'margin']),
  allColumns: new Set(['salesamount', 'customerkey', 'orderdatekey', 'datekey', 'year', 'customername']),
};

let pass = 0, fail = 0;
function check(name, text, useIdx, expect) {
  const d = analyzeDax(text, useIdx ? idx : null);
  const msgs = d.map((x) => x.severity + ':' + x.message);
  if (expect(d, msgs)) { pass++; console.log('  PASS', name); }
  else { fail++; console.log('  FAIL', name, '=>', JSON.stringify(msgs)); }
}
const none = (d) => d.length === 0;
const has = (sub) => (_d, m) => m.some((x) => x.includes(sub));

check('valid measure', 'CALCULATE ( [Total Sales], FILTER ( Sales, Sales[SalesAmount] > 0 ) )', true, none);
check('unbalanced paren', 'CALCULATE ( [Total Sales]', true, has("Unclosed '('"));
check('extra close bracket', 'SUM ( Sales[SalesAmount] ] )', true, has("Unmatched ']'"));
check('unknown quoted table', "SUM ( 'Foo'[Bar] )", true, has("Unknown table 'Foo'"));
check('unknown column on known table', "SUM ( 'Sales'[Nope] )", true, has("has no column 'Nope'"));
check('unknown bare ref', 'CALCULATE ( [Nonexistent] )', true, has("Unknown measure or column '[Nonexistent]'"));
check('valid bare measure', 'CALCULATE ( [Margin] )', true, none);
check('valid contextual column', 'SUMX ( Sales, [SalesAmount] * 2 )', true, none);
check('unquoted known col valid', 'SUM ( Sales[OrderDateKey] )', true, none);
check('unquoted bad col', 'SUM ( Sales[Bogus] )', true, has("has no column 'Bogus'"));
check('brackets inside string literal ignored', 'IF ( [Margin] > 0, "a[b](c", "x" )', true, none);
check('refs in line comment ignored', "// 'Foo'[x] and [Bad]\nCALCULATE([Margin])", true, none);
check('refs in block comment ignored', "/* 'Foo'[x] */ [Margin]", true, none);
check('VAR identifier no false positive', 'VAR x = SUM ( Sales[SalesAmount] ) RETURN x', true, none);
check('no index = balance only', "SUM ( 'Foo'[Bar] )", false, none);
check('no index still catches imbalance', 'SUM ( [x]', false, has("Unclosed '('"));
check('dax string with escaped quotes', 'IF ( [Margin] > 0, "say ""hi""", "no" )', true, none);
check('empty', '', true, none);

// collectVars (for VAR-name completion)
function vcheck(name, text, expect) {
  const got = collectVars(text).sort();
  const ok = JSON.stringify(got) === JSON.stringify(expect.sort());
  if (ok) { pass++; console.log('  PASS', name); }
  else { fail++; console.log('  FAIL', name, '=> got', JSON.stringify(got), 'want', JSON.stringify(expect)); }
}
vcheck('vars basic', 'VAR a = 1 VAR _b = SUM(Sales[X]) RETURN a + _b', ['a', '_b']);
vcheck('vars none', 'CALCULATE([M], ALL(Sales))', []);
vcheck('vars not in string', 'VAR a = "VAR b = 2" RETURN a', ['a']);
vcheck('vars not in comment', '// VAR x = 1\nVAR y = 2 RETURN y', ['y']);
vcheck('VARIANCE not a var', 'VAR x = VARIANCE.P(Sales[X]) RETURN x', ['x']);

// extractDaxSymbols (outline)
function symcheck(name, text, expect) {
  const got = extractDaxSymbols(text);
  const simple = got.map((s) => s.kind + ':' + s.name);
  const okNames = JSON.stringify(simple) === JSON.stringify(expect);
  // structural invariants on every symbol
  const okRanges = got.every((s) => s.nameStart < s.nameEnd && s.fullStart <= s.nameStart && s.nameEnd <= s.fullEnd
    && text.slice(s.nameStart, s.nameEnd) === (s.kind === 'var' ? s.name : 'RETURN'));
  if (okNames && okRanges) { pass++; console.log('  PASS', name); }
  else { fail++; console.log('  FAIL', name, '=> got', JSON.stringify(simple), 'ranges-ok', okRanges); }
}
symcheck('outline var/return', 'VAR a = 1\nVAR _b = SUM(Sales[X])\nRETURN a + _b', ['var:a', 'var:_b', 'return:RETURN']);
symcheck('outline flat expr = none', 'CALCULATE([M], ALL(Sales))', []);
symcheck('outline ignores VAR in string', 'VAR a = "VAR z = 9" RETURN a', ['var:a', 'return:RETURN']);
{
  const text = 'VAR first = 1\nVAR second = 2\nRETURN first';
  const s = extractDaxSymbols(text);
  // the first VAR's full range should end exactly where the second VAR begins
  const ok = s.length === 3 && s[0].fullEnd === s[1].fullStart && s[1].fullEnd === s[2].fullStart;
  if (ok) { pass++; console.log('  PASS outline contiguous ranges'); } else { fail++; console.log('  FAIL outline contiguous ranges'); }
}

// varDefinition (VAR hover)
function dcheck(name, text, varName, expect) {
  const got = varDefinition(text, varName);
  if (got === expect) { pass++; console.log('  PASS', name); }
  else { fail++; console.log('  FAIL', name, '=> got', JSON.stringify(got), 'want', JSON.stringify(expect)); }
}
dcheck('vardef basic', 'VAR a = SUM(Sales[X]) RETURN a', 'a', 'SUM(Sales[X])');
dcheck('vardef case-insensitive', 'VAR Total = 1 RETURN Total', 'total', '1');
dcheck('vardef second of two', 'VAR a = 1 VAR b = DIVIDE(a, 2) RETURN b', 'b', 'DIVIDE(a, 2)');
dcheck('vardef not a var', 'CALCULATE([M], ALL(Sales))', 'M', null);
dcheck('vardef rhs with equality', 'VAR f = [a] = [b] RETURN f', 'f', '[a] = [b]');

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
