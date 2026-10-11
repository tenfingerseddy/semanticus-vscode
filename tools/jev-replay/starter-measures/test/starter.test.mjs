// Self-test for the starter measures replay. No network, no key, no engine.
//
//   node --test tools/jev-replay/starter-measures/test/starter.test.mjs
//
// It proves four things: the replay asks the engine's exact question (read from the C#, so a change there fails
// here); the labels read plain aggregations and nothing else; the request carries the engine's fields and derived
// shares only; and the score is aggregate, with no table or column name in it.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, writeFileSync, mkdtempSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  CRITERIA, INSTRUCTIONS, OPTIONS, QUESTION_NAME, ruleChoice, factSideRule, shapeModel, candidates, plainAggregation, labelFor, snapshot, mcnemar,
} from '../starter.mjs';
import { score } from '../score.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = join(HERE, '..', '..', '..', '..');
const read = (p) => readFileSync(join(ROOT, p), 'utf8');
const FAMILY = read('Semanticus.Engine/Plan/JevFamilies/StarterMeasuresFamily.cs');
const TEMPLATES = read('Semanticus.Engine/Plan/JevFamilies/StarterMeasureTemplates.cs');
const REGISTRY = read('Semanticus.Core/Decisions/DecisionRegistry.cs');

test('the replay asks the engine question word for word', () => {
  const criteria = {};
  for (const m of FAMILY.matchAll(/\{\s*StarterMeasureTemplates\.(\w+),\s*"([^"]+)"\s*\}/g)) criteria[m[1]] = m[2];
  const ids = { None: 'none', Sum: 'sum', Average: 'average', Min: 'min', Max: 'max', Count: 'count', DistinctCount: 'distinctcount' };
  assert.equal(Object.keys(criteria).length, 7, 'the parser did not find the seven criteria in StarterMeasuresFamily.cs');
  for (const [k, v] of Object.entries(criteria)) assert.equal(CRITERIA[ids[k]], v, 'criterion differs for ' + ids[k]);
  const instructions = /new DecisionQuestion\(Question, DecisionQuestionKind\.Choice,\s*"([^"]+)"/.exec(FAMILY);
  assert.ok(instructions, 'the parser did not find the question in StarterMeasuresFamily.cs');
  assert.equal(INSTRUCTIONS, instructions[1]);
  assert.match(FAMILY, new RegExp(`internal const string Question = "${QUESTION_NAME}";`));
  const options = /StarterAggregations = new List<string>\s*\{([^}]*)\}/.exec(REGISTRY);
  assert.deepEqual([...options[1].matchAll(/"([^"]+)"/g)].map((m) => m[1]), OPTIONS);
});

test('the rule matches the engine rule', () => {
  // The C# switch, read as text: every summarize-by the engine maps to an option maps the same way here.
  const body = /RuleChoice\(bool isKey[\s\S]*?\n        \}/.exec(TEMPLATES)[0];
  for (const m of body.matchAll(/case "(\w+)": return (\w+);/g)) {
    const expect = m[2] === 'Sum' ? 'sum' : m[2] === 'Average' ? 'average' : m[2] === 'Min' ? 'min' : m[2] === 'Max' ? 'max'
      : m[2] === 'Count' ? 'count' : m[2] === 'DistinctCount' ? 'distinctcount' : 'none';
    assert.equal(ruleChoice(false, 'none', m[1]), expect, 'summarize-by ' + m[1]);
  }
  assert.equal(ruleChoice(false, 'none', 'Default'), 'sum');
  assert.equal(ruleChoice(false, 'none', 'None'), 'none');
  assert.equal(ruleChoice(true, 'none', 'Sum'), 'none');
  assert.equal(ruleChoice(false, 'many', 'Sum'), 'none');
  // The scored alternative ignores summarize-by entirely.
  assert.equal(factSideRule(false, 'none'), 'sum');
  assert.equal(factSideRule(false, 'many'), 'sum');
  assert.equal(factSideRule(false, 'one'), 'none');
  assert.equal(factSideRule(true, 'none'), 'none');
});

// A Contoso star, as the TMSCHEMA DMVs return it.
const META = {
  tables: [{ ID: 1, Name: 'Customer' }, { ID: 2, Name: 'Sales' }, { ID: 3, Name: 'Date', DataCategory: 'Time' }, { ID: 4, Name: 'LocalDateTable_x' }],
  columns: [
    { ID: 10, TableID: 1, ExplicitName: 'CustomerKey', ExplicitDataType: 6, IsKey: true, SummarizeBy: 2, Type: 1 },
    { ID: 20, TableID: 2, ExplicitName: 'CustomerKey', ExplicitDataType: 6, SummarizeBy: 1, Type: 1 },
    { ID: 21, TableID: 2, ExplicitName: 'Amount', ExplicitDataType: 8, SummarizeBy: 3, Type: 1, FormatString: '#,0.00' },
    { ID: 22, TableID: 2, ExplicitName: 'UnitPrice', ExplicitDataType: 8, SummarizeBy: 7, Type: 1 },
    { ID: 23, TableID: 2, ExplicitName: 'Region', ExplicitDataType: 2, SummarizeBy: 2, Type: 1 },
    { ID: 24, TableID: 2, ExplicitName: 'Margin', ExplicitDataType: 8, SummarizeBy: 2, Type: 2 },
    { ID: 25, TableID: 2, ExplicitName: 'RowNumber-x', ExplicitDataType: 6, Type: 3 },
    { ID: 30, TableID: 3, ExplicitName: 'Year', ExplicitDataType: 6, SummarizeBy: 1, Type: 1 },
  ],
  relationships: [
    { FromTableID: 2, FromColumnID: 20, FromCardinality: 2, ToTableID: 1, ToColumnID: 10, ToCardinality: 1 },
  ],
};

test('fact tables and candidates follow the engine reading', () => {
  const shaped = shapeModel(META);
  assert.deepEqual(shaped.filter((t) => t.isFact).map((t) => t.name), ['Sales']);
  assert.ok(!shaped.some((t) => t.name.startsWith('LocalDateTable_')), 'an automatic date table is not a model table');
  const cands = candidates(shaped).map(({ c }) => c.name);
  assert.deepEqual(cands, ['Amount', 'CustomerKey', 'Margin', 'UnitPrice'], 'number columns on the fact, no row number, no text');
  const key = shaped.find((t) => t.name === 'Sales').columns.find((c) => c.name === 'CustomerKey');
  assert.equal(key.relationshipRole, 'many');
});

test('labels read plain aggregations and nothing else', () => {
  assert.deepEqual(plainAggregation("SUM ( 'Sales'[Amount] )"), { option: 'sum', table: 'Sales', column: 'Amount' });
  assert.deepEqual(plainAggregation('sum(Sales[Amount])'), { option: 'sum', table: 'Sales', column: 'Amount' });
  assert.equal(plainAggregation('SUMX ( Sales, Sales[Amount] )').option, 'sum');
  assert.equal(plainAggregation('CALCULATE ( AVERAGE ( Sales[UnitPrice] ) )').option, 'average');
  assert.equal(plainAggregation('DISTINCTCOUNTNOBLANK ( Sales[CustomerKey] ) // how many buyers').option, 'distinctcount');
  assert.equal(plainAggregation('COUNTA ( Sales[Amount] )').option, 'count');
  assert.equal(plainAggregation("CALCULATE ( SUM ( Sales[Amount] ), Sales[Region] = \"West\" )"), null);
  assert.equal(plainAggregation('SUMX ( Sales, Sales[Amount] * Sales[UnitPrice] )'), null);
  assert.equal(plainAggregation('SUMX ( Customer, Sales[Amount] )'), null, 'an X over a different table is not a plain sum');
  assert.equal(plainAggregation('DIVIDE ( SUM ( Sales[Amount] ), 2 )'), null);

  const measures = [
    { Expression: 'SUM ( Sales[Amount] )' },
    { Expression: 'AVERAGE ( Sales[Amount] )' },
    { Expression: "CALCULATE ( SUM ( Sales[Margin] ), Sales[Region] = \"West\" )" },
  ];
  assert.deepEqual(labelFor('Sales', 'Amount', measures), { kind: 'plain', options: ['average', 'sum'] });
  assert.deepEqual(labelFor('Sales', 'Margin', measures), { kind: 'other' });
  assert.deepEqual(labelFor('Sales', 'UnitPrice', measures), { kind: 'none' });
});

test('the request carries the engine fields and derived shares, never a value', () => {
  const shaped = shapeModel(META);
  const sales = shaped.find((t) => t.name === 'Sales');
  const amount = sales.columns.find((c) => c.name === 'Amount');
  const s = snapshot(sales, amount, { distinct: 2031, blankShare: 0.0125, wholeNumbers: false, uniqueOnEveryRow: false }, 8123);

  // The engine's four declared fields, read from the registry.
  const block = /snapshotFields: new\[\]\s*\{\s*"table_name",[^}]*\}/.exec(REGISTRY)[0];
  assert.deepEqual(Object.keys(s).sort(), [...block.matchAll(/"([^"]+)"/g)].map((m) => m[1]).sort());
  // The engine's column facts, read from its Snapshot.
  const cf = /var columnFacts = new Dictionary<string, object>\(StringComparer\.Ordinal\)\s*\{([\s\S]*?)\};/.exec(FAMILY)[1];
  assert.deepEqual(Object.keys(s.column_facts).sort(), [...cf.matchAll(/\{ "(\w+)",/g)].map((m) => m[1]).sort());
  const mf = /measuredFacts = new Dictionary<string, object>\(StringComparer\.Ordinal\)\s*\{([\s\S]*?)\};/.exec(FAMILY)[1];
  assert.deepEqual(Object.keys(s.column_facts.measured).sort(), [...mf.matchAll(/\{ "(\w+)",/g)].map((m) => m[1]).sort());

  assert.equal(s.column_facts.measured.distinct_share, 0.25);
  const json = JSON.stringify(s);
  assert.ok(!json.includes('8123') && !json.includes('2031'), 'a raw count went out where a share should');
  for (const word of ['"min', '"max', '"sample"', '"values', '"rows']) assert.ok(!json.toLowerCase().includes(word), word);
});

test('the gather writes option ids, never a formula, and probes with scalar counts only', () => {
  const gather = readFileSync(join(HERE, '..', 'gather.mjs'), 'utf8');
  const uses = [...gather.matchAll(/Expression/g)].length;
  assert.equal(uses, 1, 'Expression may appear once: in the DMV query that reads it into memory');
  const probe = /return `(EVALUATE[^`]*)`;/.exec(gather)[1];
  assert.match(probe, /^EVALUATE ROW\(/);
  assert.doesNotMatch(probe, /\b(MIN|MAX|SAMPLE|TOPN|VALUES|SELECTCOLUMNS|FIRSTNONBLANK|CONCATENATEX)\s*\(/);
});

test('the score is aggregate and applies the verdict bar', () => {
  const cols = [];
  for (let i = 0; i < 60; i++) cols.push({ table: 'Sales', column: 'Contoso Col ' + i, label: { kind: 'plain', options: ['sum'] }, rule: i < 20 ? 'sum' : 'none', state: { column_facts: { is_key: false, relationship_role: 'none' } } });
  for (let i = 0; i < 20; i++) cols.push({ table: 'Sales', column: 'Contoso Key ' + i, label: { kind: 'none' }, rule: 'none', state: { column_facts: { is_key: true, relationship_role: 'none' } } });
  cols.push({ table: 'Sales', column: 'Contoso Other', label: { kind: 'other' }, rule: 'sum' });
  const facts = { models: [{ model: 'm0', columns: cols }] };
  const raw = cols.map((c, i) => ({
    id: `m0#${i}`,
    answers: { aggregation: { choice: c.label.kind === 'none' ? 'none' : 'sum', confidence: i % 2 ? 0.9 : 0.7 } },
  }));
  const s = score(facts, raw);
  assert.equal(s.counts.scored, 80);
  assert.equal(s.counts.other, 1);
  assert.equal(s.accuracy.jev, 100);
  assert.equal(s.accuracy.rule, 50);
  assert.equal(s.verdict, 'SHIP');
  assert.equal(s.ruleOnly.factSide, 100, 'the fact-side rule totals the 60 plain columns and leaves the 20 keys');
  const text = JSON.stringify(s);
  assert.ok(!/Contoso|Sales/.test(text), 'a name reached the score');

  // One label over 95% of the rows tests no judgement.
  const flat = { models: [{ model: 'm0', columns: cols.slice(0, 60) }] };
  assert.equal(score(flat, raw.slice(0, 60)).verdict, 'MORE DATA');
  assert.ok(mcnemar(40, 0) < 0.001 && mcnemar(3, 3) === 1);
});

test('the gather runs the engine on a scratch state folder and removes it when done', () => {
  const home = mkdtempSync(join(tmpdir(), 'jrs-home-'));
  const conns = join(home, 'connections.json');
  writeFileSync(conns, JSON.stringify([{ kind: 'file', path: '/nowhere' }]));
  const envLog = join(home, 'engine-env.json');
  const env = { ...process.env, HOME: home, SEMANTICUS_ENGINE: join(HERE, '..', '..', 'columns', 'test', 'fake-engine.mjs'), FAKE_ENGINE_ENV_LOG: envLog };
  delete env.SEMANTICUS_HOME;
  delete env.SEMANTICUS_SIGNIN_DIR;
  const stdout = execFileSync(process.execPath, [join(HERE, '..', 'gather.mjs'), '--connections', conns, '--out', join(home, 'out.json')], { env, encoding: 'utf8' });
  assert.match(stdout, /models gathered 0 of 0/);
  const seen = JSON.parse(readFileSync(envLog, 'utf8'));
  assert.ok(seen.home && !seen.home.startsWith(join(home, '.semanticus')), 'the engine gets a scratch SEMANTICUS_HOME');
  assert.match(seen.signIn ?? '', /Semanticus[\\/]auth$/);
  assert.ok(!existsSync(seen.home), 'gather.mjs removes its scratch home when it is done');
  rmSync(home, { recursive: true, force: true });
});
