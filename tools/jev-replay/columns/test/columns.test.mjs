// Self-test for the column replay: the gather step against a fake engine, then build-calls and score against a
// synthetic Contoso-style facts file with synthetic Jev answers. No network, no key, no real model.
//   node --test tools/jev-replay/columns/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, readFileSync, mkdirSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { buildCalls, adjudicationFlags } from '../build-calls.mjs';
import { scoreAll, toMarkdown, mcnemarP, verdictOf, parseAnswer } from '../score.mjs';
import { formatFamily, baselineHidden, baselineSummarize, baselineSortby, baselineDatacat, baselineFormatTyped } from '../rules.mjs';
import { columnFactsQuery, deriveMeasured, shapeModel, countMeasureRefs } from '../derive.mjs';

const HERE = fileURLToPath(new URL('.', import.meta.url));
const DIR = join(HERE, '..');
const LABEL_KEYS = ['isHidden', 'summarizeBy', 'dataCategory', 'sortByColumn', 'formatString', 'isAvailableInMdx'];

// ------------------------------------------------------------------ synthetic facts (placeholder names only)
function measured(over = {}) {
  return {
    distinct: 10, blankShare: 0, wholeNumbers: false, uniqueOnEveryRow: false, looksLikeYear: false,
    looksLikeMonthName: false, looksLikeMonthNumber: false, looksLikeDayName: false, looksLikeQuarter: false,
    placeShare: { country: 0, region: 0, city: 0, postcode: 0 }, urlShare: 0, imageUrlShare: 0, emailShare: 0,
    oneToOneWith: [], dictionaryBytes: null, dataBytes: null, cardinality: 10, sampled: false, ...over,
  };
}
function col(name, dataType, over = {}, m = {}) {
  return {
    name, dataType, isCalculated: false, isHidden: false, summarizeBy: 'none', dataCategory: null, sortByColumn: null,
    formatString: null, isAvailableInMdx: true, isKey: false, relationshipRole: 'none', usedByMeasures: 0,
    usedInReports: null, measured: measured(m), ...over,
  };
}
export function syntheticModels(copies = 12) {
  const models = [];
  for (let i = 0; i < 2; i++) {
    const tables = [];
    for (let k = 0; k < copies; k++) {
      tables.push({ name: `Sales${k}`, rows: 1000, measures: 3, columns: [
        col('CustomerKey', 'int64', { isHidden: true, relationshipRole: 'many', summarizeBy: 'none', formatString: '0' }, { wholeNumbers: true }),
        col('ProductKey', 'int64', { isHidden: false, relationshipRole: 'many', summarizeBy: 'sum', formatString: '0' }, { wholeNumbers: true }),
        col('Amount', 'decimal', { summarizeBy: 'sum', formatString: '\\$#,0.00', usedByMeasures: 2 }),
        col('Quantity', 'int64', { summarizeBy: 'sum', formatString: '#,0' }, { wholeNumbers: true }),
      ] });
      tables.push({ name: `Calendar${k}`, rows: 365, measures: 0, columns: [
        col('Month', 'string', { sortByColumn: 'Month Number' }, { distinct: 12, looksLikeMonthName: true, oneToOneWith: ['Month Number'] }),
        col('Month Number', 'int64', { isHidden: true, formatString: '0' }, { distinct: 12, looksLikeMonthNumber: true, wholeNumbers: true, oneToOneWith: ['Month'] }),
        col('Year', 'int64', { summarizeBy: 'sum', formatString: '0' }, { distinct: 5, looksLikeYear: true, wholeNumbers: true }),
        col('Country', 'string', { dataCategory: 'Country' }, { distinct: 8, placeShare: { country: 1, region: 0, city: 0, postcode: 0 } }),
        col('Web', 'string', {}, { distinct: 8, urlShare: 1 }),
      ] });
    }
    models.push({ model: `m${i}`, data: 'live', tables });
  }
  return models;
}

// A deterministic stand-in for Jev: the facts arm reads the facts, the names arm guesses from the name.
function fakeJev(calls, arm) {
  return calls.map((c) => {
    const s = c.arms[arm], f = s.derived_facts?.measured, answers = {};
    const name = s.column.name;
    for (const [q, spec] of Object.entries(c.questions)) {
      if (q === 'hidden') answers[q] = { type: 'noul', noul: arm === 'facts' && s.derived_facts.relationship_role === 'many' ? 0.95 : /key$/i.test(name) ? 0.7 : 0.3 };
      else if (q === 'summarize_by') answers[q] = { type: 'choice', choice: arm === 'facts' && (f.looksLikeYear || s.derived_facts.relationship_role !== 'none') ? 'none' : 'sum', confidence: 0.9 };
      else if (q === 'data_category') answers[q] = { type: 'choice', choice: arm === 'facts' && f.placeShare.country > 0.5 ? 'Country' : 'none', confidence: 0.8 };
      else if (q === 'sort_by_column') answers[q] = { type: 'choice', choice: Object.keys(spec.criteria).find((k) => k !== 'none' && arm === 'facts') ?? 'none', confidence: 0.7 };
      else if (q === 'format_family') answers[q] = { type: 'choice', choice: s.column.data_type === 'decimal' ? 'currency' : 'whole_number', confidence: 0.99 };
    }
    return { id: c.id, answers };
  });
}

// ------------------------------------------------------------------ tests
test('rules match the pilot definitions on its own examples', () => {
  assert.equal(formatFamily('\\$#,0.00'), 'currency');
  assert.equal(formatFamily('0.0%'), 'percent');
  assert.equal(formatFamily('#,0'), 'whole_number');
  assert.equal(formatFamily('Long Date'), 'date_or_time');
  assert.equal(formatFamily(null), 'text_or_none');
  assert.equal(baselineHidden('CustomerKey'), true);
  assert.equal(baselineHidden('Name'), false);
  assert.equal(baselineSummarize('Calendar Year', 'int64'), 'none');
  assert.equal(baselineSummarize('Quantity', 'int64'), 'sum');
  assert.equal(baselineSortby('Month', ['Month Number', 'Year']), 'Month Number');
  assert.equal(baselineDatacat('Zip Code'), 'PostalCode');
  assert.equal(baselineFormatTyped('Quantity', 'int64'), 'whole_number');
});

test('the facts query is one scalar probe that returns counts only', () => {
  for (const [dt, sample] of [['string', false], ['int64', true], ['double', false], ['dateTime', false]]) {
    const { query, fields } = columnFactsQuery("Cust'omer", 'Na]me', dt, sample);
    assert.match(query, /^DEFINE VAR __v = [\s\S]*\nEVALUATE ROW\(/);
    assert.equal((query.match(/\bEVALUATE\b/g) ?? []).length, 1);
    assert.doesNotMatch(query, /\b(MINX?|MAXX?|CONCATENATEX|TOPN|SELECTEDVALUE|FIRSTNONBLANK|LASTNONBLANK)\s*\(/i);
    assert.ok(query.includes("'Cust''omer'[Na]]me]"));
    assert.equal(fields[0], 'rows');
    if (sample) assert.match(query, /SAMPLE\(5000,/);
  }
  const m = deriveMeasured({ rows: 100, distinct: 12, blanks: 0, nb: 12, month12N: 12, monthNameN: 0 }, 'int64', false, null);
  assert.equal(m.looksLikeMonthNumber, true);
  assert.equal(m.looksLikeYear, false);
  assert.equal(m.wholeNumbers, true);
  assert.equal(m.dictionaryBytes, null);
});

test('shapeModel drops system tables and row-number columns and counts measure references', () => {
  const shaped = shapeModel({
    tables: [{ ID: 1, Name: 'Sales' }, { ID: 2, Name: 'LocalDateTable_abc' }],
    columns: [
      { ID: 5, TableID: 1, ExplicitName: 'Amount', ExplicitDataType: 10, SummarizeBy: 3, Type: 1, IsHidden: false },
      { ID: 6, TableID: 1, ExplicitName: 'RowNumber', ExplicitDataType: 6, Type: 3 },
      { ID: 7, TableID: 2, ExplicitName: 'Date', ExplicitDataType: 9, Type: 1 },
    ],
    relationships: [], measures: [{ TableID: 1, Expression: 'SUM ( Sales [Amount] )' }],
  });
  assert.equal(shaped.length, 1);
  assert.deepEqual(shaped[0].columns.map((c) => [c.name, c.dataType, c.summarizeBy, c.usedByMeasures]), [['Amount', 'decimal', 'sum', 1]]);
  assert.equal(countMeasureRefs('Sales Table', 'Amount', [{ Expression: "SUM('Sales Table'[Amount])" }]), 1);
});

test('gather drives an engine over MCP and writes one file of names plus derived facts', () => {
  const home = mkdtempSync(join(tmpdir(), 'jrc-home-'));
  const conns = join(home, 'connections.json');
  writeFileSync(conns, JSON.stringify([
    { kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso Workspace', database: 'Contoso Sales', authMode: 'interactive' },
    { kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso Workspace', database: 'Missing', authMode: 'interactive' },
    { kind: 'xmla', endpoint: 'powerbi://api.powerbi.com/v1.0/myorg/Contoso Workspace', database: '' },
    { kind: 'file', path: '/nowhere' },
  ]));
  const out = join(home, 'Downloads', 'facts.json');
  const log = join(home, 'dax.log');
  const envLog = join(home, 'engine-env.json');
  const env = { ...process.env, HOME: home, SEMANTICUS_ENGINE: join(HERE, 'fake-engine.mjs'), FAKE_ENGINE_LOG: log, FAKE_ENGINE_ENV_LOG: envLog };
  delete env.SEMANTICUS_HOME;
  delete env.SEMANTICUS_SIGNIN_DIR;
  const stdout = execFileSync(process.execPath, [join(DIR, 'gather.mjs'), '--connections', conns, '--out', out], { env, encoding: 'utf8' });
  assert.match(stdout, /models reached 1 of 3; columns measured 6;/);
  // The engine runs on a scratch state folder, never ~/.semanticus, and uses the person's sign-in store on purpose.
  const seen = JSON.parse(readFileSync(envLog, 'utf8'));
  assert.ok(seen.home, 'gather.mjs gives the engine a scratch SEMANTICUS_HOME');
  assert.ok(!seen.home.startsWith(join(home, '.semanticus')), 'the scratch home is not the person\'s ~/.semanticus');
  assert.ok(!existsSync(seen.home), 'gather.mjs removes its scratch home when it is done');
  assert.match(seen.signIn ?? '', /Semanticus[\\/]auth$/);
  assert.doesNotMatch(stdout, /Contoso/, 'console output must carry no connection strings');
  const facts = JSON.parse(readFileSync(out, 'utf8'));
  assert.equal(facts.models.length, 1);
  assert.equal(facts.summary[1].reached, false);
  assert.equal(facts.summary[2].error, 'no database recorded for this connection');
  assert.doesNotMatch(JSON.stringify(facts), /Contoso|powerbi:/, 'the facts file carries no endpoint, workspace or model name');
  const [sales, date] = facts.models[0].tables;
  assert.equal(facts.models[0].tables.length, 2, 'the automatic date table is skipped');
  assert.deepEqual(sales.columns.map((c) => c.name), ['OrderDateKey', 'Amount']);
  assert.equal(sales.columns[0].relationshipRole, 'many');
  assert.equal(sales.columns[1].usedByMeasures, 2);
  assert.equal(sales.columns[1].measured.wholeNumbers, false);
  const month = date.columns.find((c) => c.name === 'Month Name');
  assert.equal(month.sortByColumn, 'Month Number');
  assert.equal(month.measured.looksLikeMonthName, true);
  assert.deepEqual(month.measured.oneToOneWith, ['Month Number']);
  assert.equal(date.columns.find((c) => c.name === 'Country').measured.placeShare.country, 1);
  assert.equal(date.columns.find((c) => c.name === 'DateKey').relationshipRole, 'one');
  for (const q of readFileSync(log, 'utf8').trim().split('\n')) assert.match(q, /EVALUATE ROW\(/, 'every DAX query is a scalar probe');

  rmSync(home, { recursive: true, force: true });
});

test('call states never carry a labelled setting, and sort-by options are the one-to-one candidates', () => {
  const { calls } = buildCalls(syntheticModels(1));
  for (const c of calls) for (const arm of ['names', 'facts']) {
    const s = JSON.stringify(c.arms[arm]);
    for (const k of LABEL_KEYS) assert.ok(!s.includes(`"${k}"`), `${arm} state carries ${k}`);
    assert.ok(!s.includes('\\\\$#'), 'no format string in the state');
  }
  const month = calls.find((c) => c.id.endsWith('|Month'));
  assert.deepEqual(Object.keys(month.questions.sort_by_column.criteria), ['Month Number', 'none']);
  assert.deepEqual(month.questions.sort_by_column, calls.find((c) => c.id.endsWith('|Month')).questions.sort_by_column);
  assert.ok(month.arms.facts.derived_facts.measured.looksLikeMonthName);
  assert.equal(month.arms.names.derived_facts, undefined);
});

test('adjudication flags plainly wrong labels from facts', () => {
  const pk = col('ProductKey', 'int64', { relationshipRole: 'many' });
  assert.equal(adjudicationFlags(pk, { hidden: false }).hidden, 'visible foreign key');
  const yr = col('Year', 'int64', {}, { looksLikeYear: true });
  assert.ok(adjudicationFlags(yr, { summarize_by: 'sum' }).summarize_by);
  const web = col('Web', 'string', {}, { urlShare: 1 });
  assert.ok(adjudicationFlags(web, { data_category: 'none' }).data_category);
  const amt = col('Amount', 'decimal');
  assert.deepEqual(adjudicationFlags(amt, { hidden: false, summarize_by: 'sum' }), {});
});

test('McNemar and the verdict bar', () => {
  assert.equal(mcnemarP(0, 0), 1);
  assert.ok(Math.abs(mcnemarP(10, 0) - 0.001953125) < 1e-9);
  assert.ok(mcnemarP(5, 5) > 0.99);
  const rows = (b, c, same) => [
    ...Array.from({ length: b }, () => ({ facts: { ok: true }, rule: { ok: false } })),
    ...Array.from({ length: c }, () => ({ facts: { ok: false }, rule: { ok: true } })),
    ...Array.from({ length: same }, () => ({ facts: { ok: true }, rule: { ok: true } })),
  ].map((r, i) => ({ ...r, truth: i % 2 === 0 }));
  assert.equal(verdictOf(rows(20, 2, 60), 'rule').verdict, 'SHIP');
  assert.equal(verdictOf(rows(2, 20, 60), 'rule').verdict, 'RULE');
  assert.equal(verdictOf(rows(5, 3, 60), 'rule').verdict, 'MORE DATA');
  assert.equal(verdictOf(rows(5, 0, 10), 'rule').verdict, 'MORE DATA');
  assert.deepEqual(parseAnswer({ type: 'noul', noul: 0.9 }).pred, true);
  // Labels that are one value are no test of judgement: a constant rule would score near 100%.
  const oneValue = rows(20, 2, 60).map((r) => ({ ...r, truth: 'none' }));
  const v = verdictOf(oneValue, 'rule');
  assert.equal(v.verdict, 'MORE DATA');
  assert.match(v.why, /one value/);
});

test('end to end on the synthetic file: scores, verdicts, and no name in anything written', () => {
  const models = syntheticModels(12);
  const { calls, diag } = buildCalls(models);
  const s = scoreAll(calls, fakeJev(calls, 'facts'), fakeJev(calls, 'names'), diag);
  assert.equal(s.models, 2);
  assert.equal(s.unpaired, 0);
  const sec = Object.fromEntries(s.sections.map((x) => [x.name, x]));
  const dc = sec['data category, every text or number column'];
  assert.ok(dc.facts > dc.names, 'facts arm reads the country and url facts');
  const hid = sec['hidden, every column'];
  assert.equal(hid.adjudicated.dropped, 24, 'one visible foreign key per sales table, 12 tables in each of 2 models');
  assert.ok(['SHIP', 'RULE', 'MORE DATA'].includes(sec['summarize by, numeric columns'].verdict));
  const fmt = sec['format, numeric and date columns that carry a format string'];
  assert.deepEqual(Object.keys(fmt.rules), ['rule', 'typed']);
  const written = JSON.stringify(s) + toMarkdown(s);
  for (const n of ['Sales', 'Calendar', 'CustomerKey', 'Month Number', 'Amount', 'Quantity']) assert.ok(!written.includes(n), `aggregate output names ${n}`);
  const sortConf = sec['sort by, every column asked'].confusions.map((c) => c.pair).join(' ');
  assert.doesNotMatch(sortConf, /Month/);
});

test('the scorer runs from the command line on a cache dir', () => {
  const dir = mkdtempSync(join(tmpdir(), 'jrc-cache-'));
  mkdirSync(dir, { recursive: true });
  const factsFile = join(dir, 'jev-columns-facts.json');
  writeFileSync(factsFile, JSON.stringify({ kind: 'jev-columns-facts', version: 1, models: syntheticModels(12) }));
  execFileSync(process.execPath, [join(DIR, 'build-calls.mjs'), '--in', factsFile, '--out', join(dir, 'calls.json')]);
  const calls = JSON.parse(readFileSync(join(dir, 'calls.json'), 'utf8'));
  writeFileSync(join(dir, 'raw-facts.json'), JSON.stringify(fakeJev(calls, 'facts')));
  writeFileSync(join(dir, 'raw-names.json'), JSON.stringify(fakeJev(calls, 'names')));
  const md = execFileSync(process.execPath, [join(DIR, 'score.mjs'), '--dir', dir], { encoding: 'utf8' });
  assert.match(md, /\| hidden, every column \| 216 \|/);
  assert.ok(JSON.parse(readFileSync(join(dir, 'score.json'), 'utf8')).sections.length >= 10);

  rmSync(dir, { recursive: true, force: true });
});
