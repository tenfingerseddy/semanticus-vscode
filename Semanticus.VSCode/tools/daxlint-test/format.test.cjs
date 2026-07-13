// Tests for the offline DAX formatter (src/daxFormat.ts → out/daxFormat.js). No deps; run after compile:
//   node tools/daxlint-test/format.test.cjs
const path = require('node:path');
const { formatDax } = require(path.join(__dirname, '..', '..', 'out', 'daxFormat.js'));

let pass = 0, fail = 0;
function eq(name, got, want) {
  if (got === want) { pass++; console.log('  PASS', name); }
  else { fail++; console.log('  FAIL', name); console.log('--- got ---\n' + got + '\n--- want ---\n' + want + '\n'); }
}
function ok(name, cond, detail) {
  if (cond) { pass++; console.log('  PASS', name); }
  else { fail++; console.log('  FAIL', name, detail || ''); }
}

// short call stays inline
eq('inline simple call', formatDax('SUM(Sales[Amount])'), 'SUM ( Sales[Amount] )');

// nested complex call expands; inner fitting call stays inline
eq('expand calculate',
  formatDax("CALCULATE(SUM(Sales[SalesAmount]),FILTER(ALL('Date'),'Date'[Year]=2026))"),
  "CALCULATE (\n    SUM ( Sales[SalesAmount] ),\n    FILTER ( ALL ( 'Date' ), 'Date'[Year] = 2026 )\n)");

// VAR / RETURN layout
eq('var/return',
  formatDax('VAR x=SUM(Sales[Amount]) VAR y=1 RETURN DIVIDE(x,y,0)'),
  'VAR x = SUM ( Sales[Amount] )\nVAR y = 1\nRETURN\n    DIVIDE ( x, y, 0 )');

// unary minus hugs its operand; binary keeps spaces
eq('unary minus', formatDax('-1 + ABS(-2) * 3'), '-1 + ABS ( -2 ) * 3');

// string literal with delimiters is preserved verbatim
ok('string preserved', formatDax('IF([M]>0,"a, b (c) [d]","x")').includes('"a, b (c) [d]"'));

// CORRECTNESS: a line comment must push the following token to a new line (never comment it out)
const withComment = formatDax('CALCULATE ( [Sales] , // keep\n ALL ( Sales ) )');
ok('line comment not on same line as next token', !/\/\/[^\n]*ALL/.test(withComment), withComment);

// idempotency: formatting formatted output is a no-op
const samples = [
  'SUM(Sales[Amount])',
  "CALCULATE(SUM(Sales[SalesAmount]),FILTER(ALL('Date'),'Date'[Year]=2026))",
  'VAR x=SUM(Sales[Amount]) VAR y=CALCULATE([Total],ALL(Sales)) RETURN DIVIDE(x,y,0)',
  'IF([Margin]>0,"pos","neg")',
  'CALCULATE([Sales], USERELATIONSHIP(Sales[ShipDateKey], \'Date\'[DateKey]))',
  '-1 + ABS(-2) * 3',
];
for (const s of samples) {
  const once = formatDax(s);
  const twice = formatDax(once);
  ok('idempotent: ' + s.slice(0, 28), once === twice, '\n--once--\n' + once + '\n--twice--\n' + twice);
}

// empty / whitespace input is safe
eq('empty', formatDax('   '), '');

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail ? 1 : 0);
