// The Checks > Tests page turns engine results into words. These are the WORDS, tested as behaviour:
// the module is transpiled and run, so a wrong sentence fails here rather than in a screenshot.
//
// Written against the approved redesign (tests-redesign.html), Astra's UAT and her ten-point critique.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');

async function loadTypeScriptModule(file) {
  const source = readFileSync(file, 'utf8');
  const output = ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
    fileName: file,
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(output, 'utf8').toString('base64')}`);
}

const model = await loadTypeScriptModule(resolve(root, 'webview/src/tests-model.ts'));

// ---------------------------------------------------------------------------------------------------
// 3. The result chip. Five states, and an unknown is never dressed as a failure.
// ---------------------------------------------------------------------------------------------------
assert.equal(model.resultChip(false, { verdict: 'Pass' }), 'Off', 'a turned-off check reads Off whatever its last verdict was');
assert.equal(model.resultChip(true, null), 'Not run', 'a check with no outcome has not run');
assert.equal(model.resultChip(true, undefined), 'Not run', 'an absent outcome is Not run');
assert.equal(model.resultChip(true, { verdict: 'Pass' }), 'Pass');
assert.equal(model.resultChip(true, { verdict: 'Fail' }), 'Differs');
assert.equal(model.resultChip(true, { verdict: 'Suspect' }), 'Differs', 'a suspect check did see a difference; its root cause is named in the row');
assert.equal(model.resultChip(true, { verdict: 'NotVerifiable' }), 'Could not check');
assert.equal(model.resultChip(true, { verdict: 'Pass', missing: true }), 'Could not check', 'a missing target cannot pass');

// ---------------------------------------------------------------------------------------------------
// 1. The Run scope menu lists exactly what "all" includes, and never offers a group with nothing in it.
// ---------------------------------------------------------------------------------------------------
{
  const all = model.runScopeGroups(5, 8, 6);
  assert.deepEqual(all.map((g) => g.id), ['checks', 'relationships', 'tableCounts']);
  assert.deepEqual(all.map((g) => g.label), ['Saved checks', 'Relationships', 'Table row counts']);
  assert.deepEqual(all.map((g) => g.count), [5, 8, 6]);

  const noTables = model.runScopeGroups(5, 8, 0);
  assert.deepEqual(noTables.map((g) => g.id), ['checks', 'relationships'], 'a group with nothing to run is not offered');

  assert.deepEqual(model.runScopeGroups(0, 0, 0), [], 'nothing to run offers nothing');

  // Found by driving the built page: before the first run nobody has counted the relationships yet.
  // An UNKNOWN count is not zero, and hiding the group made "all" look like it left relationships out.
  const beforeAnyRun = model.runScopeGroups(5, null, 2);
  assert.deepEqual(beforeAnyRun.map((g) => g.id), ['checks', 'relationships', 'tableCounts'],
    'a group whose size is not known yet is still part of what "all" runs');
  assert.equal(beforeAnyRun[1].count, null, 'and it says so by having no number, never a zero');
}

assert.equal(model.tickedRunRefusal(0), 'Tick at least one check first. Nothing is ticked, so there is nothing to run.',
  'an empty tick list is refused in one plain line, never sent as "everything"');
assert.equal(model.tickedRunRefusal(1), null);
assert.equal(model.tickedRunRefusal(4), null);

// ---------------------------------------------------------------------------------------------------
// 3. An older row keeps its own date after a partial rerun. Only what ran is replaced.
// ---------------------------------------------------------------------------------------------------
{
  const before = {
    a: { outcome: { defId: 'a', verdict: 'Pass' }, whenIso: '2026-09-10T09:00:00Z' },
    b: { outcome: { defId: 'b', verdict: 'Fail' }, whenIso: '2026-09-10T09:00:00Z' },
  };
  const after = model.foldRunResults(before, [{ defId: 'b', verdict: 'Pass' }], '2026-09-15T11:30:00Z');
  assert.equal(after.a.whenIso, '2026-09-10T09:00:00Z', 'a check that did not run keeps its own older date');
  assert.equal(after.a.outcome.verdict, 'Pass', 'a check that did not run keeps its own older result');
  assert.equal(after.b.whenIso, '2026-09-15T11:30:00Z', 'a check that ran takes the new date');
  assert.equal(after.b.outcome.verdict, 'Pass', 'a check that ran takes the new result');
  assert.notEqual(after, before, 'the fold returns a new map rather than mutating the old one');
  assert.equal(before.b.outcome.verdict, 'Fail', 'the previous map is left alone');
}

// ---------------------------------------------------------------------------------------------------
// 6 (Astra). "N comparisons differ; M match", with any cap disclosed and never implied away.
// ---------------------------------------------------------------------------------------------------
assert.equal(model.comparisonSummary(3, 45, 48, 48), '3 comparisons differ; 45 match.');
assert.equal(model.comparisonSummary(1, 45, 46, 46), '1 comparison differs; 45 match.');
assert.equal(model.comparisonSummary(0, 48, 48, 48), 'Every comparison matches; 48 of 48.');
{
  const capped = model.comparisonSummary(3, 17, 20, 48);
  assert.match(capped, /3 comparisons differ; 17 match\./);
  assert.match(capped, /20 of 48/, 'a capped set must say how many of the whole it is showing');
  assert.doesNotMatch(capped, /^3 comparisons differ; 17 match\.$/, 'a cap must never be left unsaid');
}

// ---------------------------------------------------------------------------------------------------
// 3 + 10a. The one sentence of what differs. It names a number, never a SQL column.
// ---------------------------------------------------------------------------------------------------
assert.equal(model.differenceSentence('12,984,201', '12,941,883', -42318),
  'The model is 42,318 lower than the answer you trust.');
assert.equal(model.differenceSentence('12,941,883', '12,984,201', 42318, 'the warehouse'),
  'The model is 42,318 higher than the warehouse.');
assert.equal(model.differenceSentence('1,204,112', '1,204,112', 0),
  'The model matches the answer you trust.');
{
  const noNumber = model.differenceSentence('318.40', 'blank', null);
  assert.match(noNumber, /blank/, 'a blank answer is said as blank, not as an empty gap');
  assert.match(noNumber, /318\.40/, 'the answer you trust is still shown when there is no difference to compute');
  assert.match(noNumber, /cannot say by how much/, 'an uncomputable difference says so instead of showing zero');
  assert.doesNotMatch(noNumber, /SQL|NULL/, 'a trusted-answer check never speaks SQL');
}

// ---------------------------------------------------------------------------------------------------
// 2. A part run earns a real letter for WHAT IT RAN. The page must say what that letter covers, name
// what did not run, and never present it as the model's overall grade. The engine stopped saying
// "Partial" (cp/tests-engine-a (b)), so neither does the page.
// ---------------------------------------------------------------------------------------------------
{
  const one = model.partRunSummary('the 1 check you picked', ['relationships', 'table counts']);
  assert.match(one, /the 1 check you picked/, 'the letter must say exactly what it covers');
  assert.match(one, /not for the whole model/, 'and must never read as the model overall');
  assert.match(one, /Relationships and table counts did not run/,
    'what did not run is named in the engine words, and starts its sentence like a sentence');
  assert.match(one, /keep their own earlier result and date/, 'and the untouched rows are accounted for');
  assert.doesNotMatch(one, /Partial/i, 'the word Partial told a person nothing and is gone');

  const nothingSkipped = model.partRunSummary('relationships', []);
  assert.match(nothingSkipped, /relationships/);
  assert.doesNotMatch(nothingSkipped, /did not run/, 'nothing skipped means no second sentence');

  const noCover = model.partRunSummary(undefined, ['table counts']);
  assert.match(noCover, /only the part you ran/, 'an absent gradeCovers still gets an honest sentence');
}

// ---------------------------------------------------------------------------------------------------
// 4 + 6 (Astra). Relationships lead with meaning: attention, not a vague "1 differs".
// ---------------------------------------------------------------------------------------------------
assert.equal(model.relationshipAttention(1), '1 relationship needs attention');
assert.equal(model.relationshipAttention(3), '3 relationships need attention');
assert.equal(model.relationshipAttention(0), 'Nothing needs attention');

// ---------------------------------------------------------------------------------------------------
// 7 (UAT 1.5.104). A total-only comparison can pass; the one line says what it leaves out and offers a field, it does not warn.
// ---------------------------------------------------------------------------------------------------
{
  const note = model.limitedEvidenceNote(0);
  assert.ok(note, 'a total-only comparison must carry a note');
  assert.equal(note, 'This compares the overall total only. Add a field to compare each group as well.');
  assert.doesNotMatch(note, /never pass|proves very little/, 'a total that ties passes, so the note must not say it cannot');
  assert.equal(model.limitedEvidenceNote(2), null, 'a grouped comparison needs no such warning');
}

// ---------------------------------------------------------------------------------------------------
// 5. The table-count footer, word for word.
// ---------------------------------------------------------------------------------------------------
assert.equal(model.TABLE_COUNT_FOOTER,
  '"Counts differ" means matching snapshots were not confirmed, so this alone does not prove missing rows.',
  'the footer is the exact approved wording; it must never imply timing explains the difference');

// No em dash anywhere in the reading rules.
assert.doesNotMatch(readFileSync(resolve(root, 'webview/src/tests-model.ts'), 'utf8'), /—/, 'no em dashes in user-facing copy');

// ---------------------------------------------------------------------------------------------------
// UAT 1.5.104 (17 Oct): "Table row counts" read "Not run" and "2 not counted" while all 5 countable tables matched, because two
// calculated tables can never be counted. Only a table with a source to count against belongs to the row, and a row nobody made
// (no table mapped, nothing counted) is not a check.
// ---------------------------------------------------------------------------------------------------
{
  const counted = (n) => ({ canCount: true, source: 'mapping', lastVerdict: 'Pass', lastModelCount: n, lastSourceCount: n });
  const calculated = { canCount: false, source: 'none', lastVerdict: null, lastModelCount: null, lastSourceCount: null };
  const five = [counted(1848), counted(731), counted(52), counted(60145), counted(8)];
  assert.equal(model.countsFamilyChip([...five, calculated, calculated]), 'Pass', 'tables that cannot be counted never hold the row back');
  assert.deepEqual(model.countsFamilyFinding([...five, calculated, calculated]), 'All 5 match');
  const differ = { canCount: true, source: 'mapping', lastVerdict: 'Fail', lastModelCount: 10, lastSourceCount: 12 };
  assert.equal(model.countsFamilyChip([...five, differ]), 'Differs');
  assert.equal(model.countsFamilyFinding([...five, differ]), '1 differs');
  const unreadable = { canCount: true, source: 'mapping', lastVerdict: 'NotVerifiable', lastModelCount: null, lastSourceCount: null };
  assert.equal(model.countsFamilyChip([...five, unreadable]), 'Could not check', 'a table that was tried and could not be counted is not "not run"');
  const mappedNotRun = { canCount: true, source: 'mapping', lastVerdict: null, lastModelCount: null, lastSourceCount: null };
  assert.equal(model.countsFamilyChip([mappedNotRun, mappedNotRun]), 'Not run');
  assert.equal(model.countsFamilyFinding([mappedNotRun, mappedNotRun]), '2 not counted yet');
  const found = { canCount: true, source: 'endpoint', lastVerdict: null, lastModelCount: null, lastSourceCount: null };
  assert.equal(model.countsFamilyChip([found, found, found]), null, 'a fresh model shows no row counts nobody made');
  assert.equal(model.countsFamilyChip([calculated]), null, 'nothing countable: no row');
  assert.equal(model.countsFamilyChip([]), null);
}

// ---------------------------------------------------------------------------------------------------
// UAT 1.5.104 (17 Oct): the check form started empty. The model's own source is chosen for the person, and a plain SUM of a column
// whose table maps to that source starts the SQL as a guess they can change.
// ---------------------------------------------------------------------------------------------------
{
  assert.equal(model.defaultSqlSourceId('b', [{ id: 'a' }, { id: 'b' }]), 'b', 'the model\'s linked source wins');
  assert.equal(model.defaultSqlSourceId('gone', [{ id: 'a' }, { id: 'b' }]), '', 'a link to a source that is no longer saved chooses nothing');
  assert.equal(model.defaultSqlSourceId(undefined, [{ id: 'only' }]), 'only', 'the only source is the obvious one');
  assert.equal(model.defaultSqlSourceId(undefined, [{ id: 'a' }, { id: 'b' }]), '', 'two sources and no link: the person chooses');
  assert.equal(model.defaultSqlSourceId(undefined, []), '');

  const maps = [{ modelTable: 'Sales', sqlSourceId: 's1', schema: 'dbo', entity: 'Sales', canCount: true }, { modelTable: 'Date', sqlSourceId: 's1', schema: 'dbo', entity: 'DimDate', canCount: true }];
  assert.equal(model.guessSumSql('SUM(Sales[NetSalesAmount])', maps, 's1'), 'SELECT SUM(NetSalesAmount) FROM dbo.Sales');
  assert.equal(model.guessSumSql("SUM('Sales'[NetSalesAmount])", maps, 's1'), 'SELECT SUM(NetSalesAmount) FROM dbo.Sales');
  assert.equal(model.guessSumSql(' sum ( Sales[Net Sales Amount] ) ', maps, 's1'), 'SELECT SUM([Net Sales Amount]) FROM dbo.Sales', 'a name with spaces is bracketed');
  assert.equal(model.guessSumSql('SUM(Sales[NetSalesAmount])', maps, 's2'), null, 'the table maps to another source: no guess');
  assert.equal(model.guessSumSql('SUM(Sales[A]) + SUM(Sales[B])', maps, 's1'), null, 'only a plain SUM is guessed');
  assert.equal(model.guessSumSql('DIVIDE([Net Sales],[Orders])', maps, 's1'), null);
  assert.equal(model.guessSumSql('SUM(Product[Cost])', maps, 's1'), null, 'a table with no mapping is not guessed');
  assert.equal(model.guessSumSql('SUM(Sales[NetSalesAmount])', [{ ...maps[0], canCount: false }], 's1'), null);
{
  // Round 2 N10: with groups chosen the guess selects them first, in order, then the number, so the defaults run.
  const star = [
    { modelTable: 'Sales', sqlSourceId: 's1', schema: 'dbo', entity: 'Sales', canCount: true },
    { modelTable: 'Product', sqlSourceId: 's1', schema: 'dbo', entity: 'Product', canCount: true },
    { modelTable: 'Date', sqlSourceId: 's1', schema: 'dbo', entity: 'Date', canCount: true },
  ];
  const links = [
    { fromTable: 'Sales', fromColumn: 'ReturnDateKey', toTable: 'Date', toColumn: 'DateKey', isActive: false },
    { fromTable: 'Sales', fromColumn: 'ProductKey', toTable: 'Product', toColumn: 'ProductKey', isActive: true },
    { fromTable: 'Sales', fromColumn: 'OrderDateKey', toTable: 'Date', toColumn: 'DateKey', isActive: true },
  ];
  const grouped = model.guessSumSql('SUM(Sales[NetSalesAmount])', star, 's1', ['column:Product/Brand', 'column:Date/Year'], links);
  assert.equal(grouped, [
    'SELECT t1.Brand, t2.Year, SUM(t0.NetSalesAmount)', 'FROM dbo.Sales t0',
    'JOIN dbo.Product t1 ON t0.ProductKey = t1.ProductKey', 'JOIN dbo.Date t2 ON t0.OrderDateKey = t2.DateKey',
    'GROUP BY t1.Brand, t2.Year'].join('\n'), 'groups first in the order chosen, an active link wins');
  assert.equal(model.guessSumSql('SUM(Sales[NetSalesAmount])', star, 's1', ["'Sales'[Region]"], links), 'SELECT t0.Region, SUM(t0.NetSalesAmount)\nFROM dbo.Sales t0\nGROUP BY t0.Region', 'a group in the same table needs no join');
  assert.equal(model.guessSumSql('SUM(Sales[NetSalesAmount])', star, 's1', ['column:Customer/Region'], links), null, 'a group it cannot reach is no guess, not a SQL that fails');
  assert.equal(model.guessSumSql('SUM(Sales[NetSalesAmount])', star, 's1', [], links), 'SELECT SUM(NetSalesAmount) FROM dbo.Sales', 'no groups: the plain total, as before');
  // Round 2 N11: a ratio is shown the way its measure shows it; anything else is left as it was.
  assert.equal(model.shownNumber('0.5812049141182254', '0.0%'), '58.1%');
  assert.equal(model.shownNumber('0.5812049141182254', '0%'), '58%');
  assert.equal(model.shownNumber('0.5812049141182254', '0.00%;-0.00%'), '58.12%');
  assert.equal(model.shownNumber('36251380.48', '#,##0.00'), '36251380.48', 'no percent in the format: as it was');
  assert.equal(model.shownNumber('58.1%', '0.0%'), '58.1%', 'text a person typed is left alone');
  assert.equal(model.shownNumber('BLANK', '0.0%'), 'BLANK');
  assert.equal(model.shownNumber('0.58', undefined), '0.58');
  // Round 2 N15: a new check's name is distinct from the ones already saved.
  assert.equal(model.distinctTitle('Net Sales ties to source SQL', []), 'Net Sales ties to source SQL');
  assert.equal(model.distinctTitle('Net Sales ties to source SQL', ['Net Sales ties to source SQL']), 'Net Sales ties to source SQL 2');
  assert.equal(model.distinctTitle('Net Sales ties to source SQL', ['net sales ties to source sql', 'Net Sales ties to source SQL 2']), 'Net Sales ties to source SQL 3');
}
  assert.equal(model.guessSumSql('SUM(Sales[NetSalesAmount])', [{ modelTable: 'Sales', sqlSourceId: 's1', entity: 'Sales', canCount: true }], 's1'), 'SELECT SUM(NetSalesAmount) FROM dbo.Sales', 'no schema: dbo');
}

// A source chosen after the last count makes that count old: the row waits for a run instead of showing the older failure.
{
  const old = { canCount: true, source: 'mapping', setWhenUtc: '2026-10-17T22:40:00Z', lastRunUtc: '2026-10-17T22:38:00Z', lastVerdict: 'NotVerifiable', lastModelCount: null, lastSourceCount: null };
  assert.equal(model.countsFamilyChip([old]), 'Not run', 'a mapping saved after the last count: not counted yet');
  assert.equal(model.countsFamilyFinding([old]), '1 not counted yet');
  const fresh = { ...old, lastRunUtc: '2026-10-17T22:41:00Z', lastVerdict: 'Pass', lastModelCount: 5, lastSourceCount: 5 };
  assert.equal(model.countsFamilyChip([fresh]), 'Pass', 'a count taken after the mapping stands');
}

// UAT recheck 19 Oct (D3): Ctrl+S told Validation "The model changed. These results may be out of date" when nothing had changed. A save (or a publish
// that landed) tells the window with a notice that moves no object; only a notice that carries a change makes the results old.
{
  assert.equal(model.makesResultsOld({ label: 'save', deltas: [] }), false, 'a save moves nothing');
  assert.equal(model.makesResultsOld({ label: 'publish', deltas: [] }), false, 'a publish that landed moves nothing in the copy');
  assert.equal(model.makesResultsOld({ label: 'set_property', deltas: [{ kind: 'update', ref: 'measure:Sales/Net Sales' }] }), true, 'an edit makes the results old');
  assert.equal(model.makesResultsOld({ label: 'undo', replay: 'undo', deltas: [] }), true, 'an undo walks the timeline: old');
  assert.equal(model.makesResultsOld({ label: 'save', deltas: [{ kind: 'update', ref: 'measure:Sales/Net Sales' }] }), true, 'a notice that does carry a change is a change, whatever its label');
  assert.equal(model.makesResultsOld({}), true, 'an unlabelled notice is treated as a change, never silently ignored');
}

console.log('Tests page reading rules passed');
