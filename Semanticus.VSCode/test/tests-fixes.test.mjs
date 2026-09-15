// Astra's rejection of the Tests build, 2026-09-15. Every item she raised that lives in the webview is
// pinned here as BEHAVIOUR: the modules are transpiled and RUN, so a wrong rule fails here rather than in
// a screenshot. Source assertions appear only where the thing being pinned is the wiring itself.
//
//   1  opening an existing check must preserve its SQL source (P1)
//   2  Escape over two stacked surfaces dismisses only the top one (P1)
//   3  "Only this" sends the family the person picked
//   4  a partial run keeps each automatic family's last result and date, scoped to the model
//   5  unknown relationship checks are counted separately and Pass needs checked results
//   8  a could-not-check row says the reason instead of a lone dot
//  10  the tolerance sentence, and a tiny difference that is not rounded to a lying 0.00
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
const surfaces = await loadTypeScriptModule(resolve(root, 'webview/src/surface-stack.ts'));
const authoring = readFileSync(resolve(root, 'webview/src/tests-authoring.tsx'), 'utf8');
const page = readFileSync(resolve(root, 'webview/src/tests.tsx'), 'utf8');
const hub = readFileSync(resolve(root, 'webview/src/connectionshub.tsx'), 'utf8');

// ===================================================================================================
// 1 (P1). The source picker can never redirect an existing check.
//
// Astra's probe: a check carrying inline finance.example / Finance, and a second check naming a source
// that has since been removed, BOTH had their saved binding replaced by the only source in the list,
// because the drawer read its own first load as "a source appeared while Connections was open".
// ===================================================================================================
const WAREHOUSE = { id: 'sql-warehouse', name: 'Warehouse' };
const FINANCE = { id: 'sql-finance', name: 'Finance' };

{
  // (a) A check saved with INLINE coordinates and no named source. One source exists. Nothing is adopted,
  // the check keeps its own endpoint, and it can still be saved without naming a source.
  const inline = model.sqlSourceBinding({
    savedId: undefined, inlineServer: 'finance.example', inlineDatabase: 'Finance',
    available: [WAREHOUSE], loaded: true,
  });
  assert.equal(inline.select, '', 'an inline check binds to no named source, and never to the only one in the list');
  assert.equal(inline.keepsInline, true, 'the check keeps its own saved endpoint');
  assert.equal(inline.needsReplacement, false, 'a valid older inline binding survives editing');
  assert.equal(inline.note, 'This check uses its saved Finance source.', 'Astra’s wording, naming the saved database');
}

{
  // (b) A check naming a source that IS still saved keeps it and says so.
  const named = model.sqlSourceBinding({
    savedId: 'sql-finance', savedName: 'Finance', available: [WAREHOUSE, FINANCE], loaded: true,
  });
  assert.equal(named.select, 'sql-finance');
  assert.equal(named.note, 'This check uses its saved Finance source.');
  assert.equal(named.needsReplacement, false);
  assert.equal(named.keepsInline, false);
}

{
  // (c) A check naming a source that is GONE. The picker holds nothing, an explicit replacement is
  // required, and the missing source is named so the person knows what they are replacing.
  const gone = model.sqlSourceBinding({
    savedId: 'sql-finance', savedName: 'Finance', available: [WAREHOUSE], loaded: true,
  });
  assert.equal(gone.select, '', 'a missing named source is never silently swapped for the only one left');
  assert.equal(gone.needsReplacement, true, 'a replacement must be explicit');
  assert.match(gone.note, /Finance/, 'the source that went is named');
  assert.match(gone.note, /no longer saved/);
  assert.equal(gone.keepsInline, false, 'a removed named source is an error, never a fall back to the inline values');
}

{
  // (d) Before the list has loaded, nothing is decided. A slow list must not read as a missing source.
  const early = model.sqlSourceBinding({ savedId: 'sql-finance', savedName: 'Finance', available: [], loaded: false });
  assert.equal(early.select, 'sql-finance', 'the saved binding is held while the list is still loading');
  assert.equal(early.needsReplacement, false);
  assert.equal(early.note, null, 'nothing is said until there is something to say');
}

{
  // (e) A brand new check says nothing and requires nothing beyond picking a source at save time.
  const fresh = model.sqlSourceBinding({ available: [WAREHOUSE], loaded: true });
  assert.equal(fresh.select, '');
  assert.equal(fresh.note, null);
  assert.equal(fresh.needsReplacement, false);
  assert.equal(fresh.keepsInline, false);
}

{
  // (f) Adoption is keyed on a baseline captured when Add was INVOKED, never on the drawer's own first load.
  assert.equal(model.sourceAddedSinceAdd(null, ['sql-warehouse']), null,
    'a first load with no Add in flight adopts nothing: this is the P1 defect');
  assert.equal(model.sourceAddedSinceAdd([], ['sql-warehouse']), 'sql-warehouse',
    'Add was invoked on an empty list and exactly one source came back');
  assert.equal(model.sourceAddedSinceAdd(['sql-warehouse'], ['sql-warehouse', 'sql-finance']), 'sql-finance');
  assert.equal(model.sourceAddedSinceAdd(['sql-warehouse'], ['sql-warehouse', 'sql-finance', 'sql-a']), null,
    'two new sources are ambiguous, so the drawer does not guess');
  assert.equal(model.sourceAddedSinceAdd(['sql-warehouse'], ['sql-warehouse']), null,
    'the hub was opened and nothing was added');
}

// The wiring: the baseline is taken in the Add handler, and the effect refuses to adopt without one.
assert.match(authoring, /addBaseline\s*=\s*useRef<string\[\]\s*\|\s*null>\(null\)/,
  'the drawer holds a baseline captured when Add is invoked');
assert.match(authoring, /beginAddSqlSource/, 'Add a SQL source goes through the handler that takes the baseline');
assert.doesNotMatch(authoring, /sourceAddedWhileWaiting\(current, latestSources\)/,
  'the old mount-time comparison is gone');

// ===================================================================================================
// 2 (P1). Escape is handled by the TOPMOST surface only.
// ===================================================================================================
{
  surfaces.resetSurfaces();
  assert.equal(surfaces.surfaceCount(), 0);
  const drawer = surfaces.pushSurface();
  assert.equal(surfaces.isTopSurface(drawer), true, 'one surface is its own top');
  const connections = surfaces.pushSurface();
  assert.equal(surfaces.isTopSurface(connections), true, 'the hub opened over the drawer is the top');
  assert.equal(surfaces.isTopSurface(drawer), false,
    'the unfinished check must NOT act on the same Escape: this is Astra finding 2');
  surfaces.popSurface(connections);
  assert.equal(surfaces.isTopSurface(drawer), true, 'closing the hub gives the drawer the key back');
  surfaces.popSurface(drawer);
  assert.equal(surfaces.surfaceCount(), 0, 'a closed surface leaves nothing behind');
  // Popping twice, or popping out of order, must never strand the stack.
  surfaces.popSurface(drawer);
  assert.equal(surfaces.surfaceCount(), 0);
  const a = surfaces.pushSurface(); const b = surfaces.pushSurface();
  surfaces.popSurface(a);
  assert.equal(surfaces.isTopSurface(b), true, 'an out-of-order close leaves the remaining surface on top');
  surfaces.resetSurfaces();
}
assert.match(authoring, /useSurfaceEscape\(onClose\)/, 'the drawer only closes itself when it is the top surface');
assert.match(hub, /useSurfaceEscape\(\(\) => \{ if \(menuIdRef\.current\)/, 'and the hub only when it is');
// FOUND BY DRIVING IT: registering inside an effect that depends on the close handler re-orders the stack
// on every render, because the handler is a new function each time. Opening the hub re-rendered the Tests
// page, the drawer re-registered LAST, and Escape then closed the drawer and left the hub open: the same
// bug with the two surfaces swapped. The registration must depend on nothing that changes while open.
const escapeHook = readFileSync(resolve(root, 'webview/src/surface-escape.ts'), 'utf8');
assert.match(escapeHook, /\}, \[active\]\);/, 'the surface registers once, on open, and not again on every render');
assert.match(escapeHook, /handler\.current = onEscape;/, 'the close handler is read through a ref instead of a dependency');
assert.doesNotMatch(escapeHook, /\[active, onEscape\]|\[onEscape/, 'the handler must never be a dependency of the registration');

// ===================================================================================================
// 3. "Only this" means only this: each group sends its OWN section name.
// ===================================================================================================
assert.equal(model.sectionsForGroup('checks').join(','), 'measures');
assert.equal(model.sectionsForGroup('relationships').join(','), 'relationships');
assert.equal(model.sectionsForGroup('tableCounts').join(','), 'tableCounts',
  'table row counts had no execution scope of their own, so "Only this" ran relationships too');
// And the person is told which one they are asking for.
assert.equal(model.runOnlyLabel({ id: 'tableCounts', label: 'Table row counts', count: 6 }), 'Run table row counts only');
assert.equal(model.runOnlyLabel({ id: 'relationships', label: 'Relationships', count: 8 }), 'Run relationships only');
assert.doesNotMatch(page, /Checked on every run/,
  'a selected saved-check run skips both automatic families, so nothing may promise they always run');

// ===================================================================================================
// 4. A partial run keeps each automatic family's last result and date, scoped to the model.
// ===================================================================================================
assert.equal(model.familyRan(undefined, 'relationships'), true, 'a run with no scope at all is a full run');
assert.equal(model.familyRan({ ran: ['saved checks'], skipped: ['relationships', 'table counts'] }, 'relationships'), false);
assert.equal(model.familyRan({ ran: ['relationships', 'table counts'], skipped: ['saved checks'] }, 'relationships'), true);
assert.equal(model.familyRan({ ran: [], skipped: [] }, 'relationships'), true,
  'an engine that named neither list is read as a full run, never as a skip');

{
  const first = model.foldFamilyResult(null, { ran: true, value: { relationships: [1, 2] }, whenIso: '2026-09-10T09:00:00Z', modelKey: 'Contoso' });
  assert.deepEqual(first.value, { relationships: [1, 2] });
  assert.equal(first.whenIso, '2026-09-10T09:00:00Z');

  const skipped = model.foldFamilyResult(first, { ran: false, value: { relationships: [] }, whenIso: '2026-09-15T11:30:00Z', modelKey: 'Contoso' });
  assert.equal(skipped, first, 'a run that skipped the family leaves the earlier result and its own date alone');

  const otherModel = model.foldFamilyResult(first, { ran: false, value: undefined, whenIso: '2026-09-15T11:30:00Z', modelKey: 'Northwind' });
  assert.equal(otherModel, null, 'another model’s relationships are never shown as this one’s');

  const rerun = model.foldFamilyResult(first, { ran: true, value: { relationships: [1] }, whenIso: '2026-09-15T11:30:00Z', modelKey: 'Contoso' });
  assert.deepEqual(rerun.value, { relationships: [1] });
  assert.equal(rerun.whenIso, '2026-09-15T11:30:00Z');
}
{
  const note = model.skippedFamilyNote('relationships', '2026-09-10T09:00:00Z');
  assert.match(note, /^These relationships were last checked on /);
  assert.match(note, /they did not run this time\.$/);
  assert.doesNotMatch(note, /no relationships to check/,
    'the false empty sentence Astra saw in part-run-1440.png must never be reachable from a skip');
  assert.equal(model.skippedFamilyNote('table counts', '2026-09-10T09:00:00Z').startsWith('These table counts were last checked on '), true);
  assert.equal(model.skippedFamilyNote('relationships', undefined),
    'Relationships did not run this time, and have not been checked yet.',
    'a skip with nothing retained says so, rather than claiming the model has none');
}

// ===================================================================================================
// 5. Unknown relationship checks are their own count, and Pass requires checked results.
// ===================================================================================================
const REL_PASS = ['Pass', 'Pass', 'Pass'];
const REL_FAIL = ['Fail', 'Pass', 'Pass'];
const REL_UNKNOWN = ['NotVerifiable', 'Pass', 'Pass'];
assert.equal(model.relationshipVerdict(REL_PASS), 'pass');
assert.equal(model.relationshipVerdict(REL_FAIL), 'attention');
assert.equal(model.relationshipVerdict(['Suspect', 'Pass', 'Pass']), 'attention');
assert.equal(model.relationshipVerdict(REL_UNKNOWN), 'unknown',
  'Budget → Scenario had unknown checks and was counted as fine');
assert.equal(model.relationshipVerdict(['NotVerifiable', 'NotVerifiable', 'NotVerifiable']), 'unknown');
assert.equal(model.relationshipVerdict([]), 'unknown', 'a relationship with no checks at all was not checked');

{
  const tally = model.relationshipTally([REL_FAIL, REL_FAIL, REL_PASS, REL_PASS, REL_UNKNOWN]);
  assert.deepEqual(tally, { attention: 2, passed: 2, unknown: 1 });
  assert.equal(model.relationshipSummary(tally), '2 need attention; 2 passed; 1 could not be checked.',
    'Astra’s sentence, word for word');
  assert.equal(model.relationshipSummary({ attention: 1, passed: 1, unknown: 0 }), '1 needs attention; 1 passed.');
  assert.equal(model.relationshipSummary({ attention: 0, passed: 7, unknown: 0 }), '7 passed.');
  assert.equal(model.relationshipSummary({ attention: 0, passed: 0, unknown: 3 }), '3 could not be checked.');
}
{
  assert.equal(model.relationshipChip({ attention: 0, passed: 3, unknown: 0 }), 'Pass');
  assert.equal(model.relationshipChip({ attention: 1, passed: 2, unknown: 0 }), 'Differs');
  assert.equal(model.relationshipChip({ attention: 0, passed: 0, unknown: 3 }), 'Could not check',
    'a card with every check unknown produced a Pass header');
  assert.equal(model.relationshipChip({ attention: 0, passed: 2, unknown: 1 }), 'Could not check',
    'Pass requires CHECKED results, so one unknown withholds it');
}

// ===================================================================================================
// 8. A could-not-check row says why, instead of a lone dot.
// ===================================================================================================
assert.equal(
  model.unknownCellText({ missing: true, message: 'the measure this test was bound to no longer exists (or was recreated with a new identity). Re-bind or delete the test' }),
  'Calculation missing. Edit this check.', 'Astra’s wording for the fixture whose calculation is gone');
assert.equal(
  model.unknownCellText({ message: 'Not run: no live connection, so the measure could not be asked.' }),
  'No live model to ask.');
assert.equal(
  model.unknownCellText({ message: 'reconciliation needs a live connection. Connect a live model in Connections and re-run' }),
  'No live model to ask.');
assert.equal(
  model.unknownCellText({ message: 'the SQL source this was set to use is no longer saved. Open Connections, pick a SQL source, then save this again' }),
  'Its SQL source is no longer saved.');
{
  const shape = model.unknownCellText({ message: 'Could not check: that expression returned a table of 4 rows by 2 columns. A measure check compares one number, so there is nothing here to compare with the number you trust.' });
  assert.equal(shape, 'That query returns a table, not one number.');
}
assert.equal(model.unknownCellText({ message: undefined }), 'Could not check. Open this row for why.',
  'an outcome with nothing to say still says something, never a dot');
assert.equal(model.unknownCellText(undefined), null, 'a row with no outcome is not a could-not-check row');
assert.equal(model.unknownCellText({ actual: '1,204,112' }), null, 'a row with a number shows the number');

// ===================================================================================================
// 13. The run menu never offers a choice nobody can make. (Kane, live on the Yoga, 2026-09-15.)
//
// Tick boxes live only on saved-check rows, so on a model with no saved checks "Only what I ticked" was a
// dead option and "Tick to run a subset" described a control that was not on the page.
// ===================================================================================================
assert.equal(model.tickingIsPossible(0), false, 'with no saved checks there is nothing to tick');
assert.equal(model.tickingIsPossible(2), true);
assert.equal(model.savedChecksHint(0), 'Save a check to run checks one at a time.',
  'the header line must describe something the page actually has');
assert.equal(model.savedChecksHint(1), 'Tick to run a subset.');
assert.equal(model.tickedMenuNote(0, 3), 'Tick at least one check first. Nothing is ticked, so there is nothing to run.');
assert.equal(model.tickedMenuNote(1, 3), '1 check ticked.', 'the menu item states the count');
assert.equal(model.tickedMenuNote(2, 3), '2 checks ticked.');

// The wiring: the option is conditional, the tick box is a labelled control, and a key pressed inside the
// row's own controls does not reach the row.
assert.match(page, /\{canTick && <button type="button" role="menuitem"/, 'the ticked option is offered only when ticking is possible');
assert.match(page, /aria-label=\{`Tick \$\{def\.title\} to run on its own`\}/, 'every tick box is labelled by the check it ticks');
assert.match(page, /if \(event\.target !== event\.currentTarget\) return;/,
  'Space on the tick box must tick it, not expand the row and cancel the tick');

// ===================================================================================================
// 10. The tolerance sentence keeps its precision, and a tiny difference is never a lying 0.00.
// ===================================================================================================
assert.equal(model.toleranceSentence(0.000000001, 0.0000001),
  'Allowed difference: 0.000000001 or 0.00001%, whichever is larger.', 'Astra’s sentence, precision preserved');
assert.equal(model.toleranceSentence(0.000001, 0), 'Allowed difference: 0.000001.');
assert.equal(model.toleranceSentence(0, 0.00001), 'Allowed difference: 0.001%.');
assert.equal(model.toleranceSentence(0, 0), 'Allowed difference: none. The numbers must match exactly.');
assert.equal(model.toleranceSentence(undefined, undefined), 'Allowed difference: none. The numbers must match exactly.');
assert.doesNotMatch(model.toleranceSentence(0.000000001, 0.0000001), /e-/,
  'a tolerance is a number a person reads, never scientific notation');

assert.equal(model.differenceSentence('1.000', '1.001', 0.001),
  'The model is 0.001 higher than the answer you trust.',
  'Astra’s probe read this as "0.00 higher", which says the check found nothing');
assert.equal(model.differenceSentence('12,984,201', '12,941,883', -42318),
  'The model is 42,318 lower than the answer you trust.', 'a whole number keeps reading as a whole number');
assert.equal(model.differenceSentence('1,204,112', '1,204,112', 0), 'The model matches the answer you trust.');
assert.match(model.differenceSentence('1.00', '1.50', 0.5), /0\.50 higher/, 'money keeps its cents');
assert.match(model.differenceSentence('a', 'b', 0.0000001), /0\.0000001 higher/);

// The page must read those rules rather than keeping its own copy of them.
assert.match(page, /toleranceSentence/, 'the opened row reads the shared tolerance sentence');
assert.match(page, /unknownCellText/, 'the Expected · actual cell reads the shared reason');
assert.match(page, /relationshipSummary/, 'the relationship card reads the shared tally');
assert.doesNotMatch(page, /const fine = rels\.length - needing/, 'unknowns are no longer subtracted into "fine"');

// ===================================================================================================
// ROUND TWO (Astra, 2026-09-15, second pass).
//
// F1 (P1). "Try it now" bypassed the guard Save uses. With a missing named source the picker holds
// nothing, so the payload dropped sqlSourceId while KEEPING the inline address, and the engine read the
// absent id as permission to use it: the check silently asked the old database. Astra's replay caught
// tryTest carrying finance.example / Finance with sql-finance already removed.
// ===================================================================================================
const GONE = { needsReplacement: true, keepsInline: false };
const INLINE = { needsReplacement: false, keepsInline: true };
const NEITHER = { needsReplacement: false, keepsInline: false };

assert.equal(
  model.authoringRefusal({ blankPolicy: 'zero', selectedSourceId: '', binding: GONE }),
  'This check points at a SQL source that is no longer saved. Pick the source it should use now.',
  'the missing-source refusal is one sentence, and Try and Save both read it');
assert.equal(model.authoringRefusal({ blankPolicy: 'zero', selectedSourceId: '', binding: INLINE }), null,
  'a check with its own saved endpoint can still be tried and saved');
assert.equal(model.authoringRefusal({ blankPolicy: 'zero', selectedSourceId: '', binding: NEITHER }),
  'Pick the SQL source this check asks, or add one.');
assert.equal(model.authoringRefusal({ blankPolicy: '', selectedSourceId: 'sql-a', binding: NEITHER }),
  'Pick what a blank in the model means.', 'the blank meaning is checked first, as it always was');
assert.equal(model.authoringRefusal({ blankPolicy: 'zero', selectedSourceId: 'sql-a', binding: GONE }), null,
  'once a replacement is picked there is nothing left to refuse');

// And the payload carries the binding, so a call that somehow escaped the guard is refused by the ENGINE
// as a dangling source rather than run against the inline address. The bad payload is now impossible.
assert.deepEqual(model.reconcileSourceFields('', 'sql-finance', 'Finance', []),
  { sqlSourceId: 'sql-finance', sqlSourceName: 'Finance' },
  'a missing named source stays ON the payload; dropping it is what let the inline address win');
assert.deepEqual(model.reconcileSourceFields('', undefined, undefined, [WAREHOUSE]), {},
  'an inline check names no source, exactly as before');
assert.deepEqual(model.reconcileSourceFields('sql-warehouse', 'sql-finance', 'Finance', [WAREHOUSE]),
  { sqlSourceId: 'sql-warehouse', sqlSourceName: 'Warehouse' }, 'a picked replacement wins over both');

// The wiring: ONE validation, read by both buttons, and the fields built by the shared helper.
assert.match(authoring, /authoringRefusal\(\{ blankPolicy, selectedSourceId: sqlSourceId, binding \}\)/,
  'the drawer has one validation, reading the binding it already computed');
assert.match(authoring, /onTry=\{\(\) => \{[\s\S]{0,200}?refusal\(\)/,
  'Try must run it: trying a check the engine would refuse asks a question the check does not describe');
assert.match(authoring, /onSave=\{\(\) => \{[\s\S]{0,200}?refusal\(\)/, 'and so must Save');
assert.match(authoring, /\.\.\.reconcileSourceFields\(/, 'and both send the same source fields');

// ===================================================================================================
// F2. A saved-check-only run must not delete "Run relationships only" from the menu.
//
// The count came from the NEWEST run, whose skipped report is empty, and a group counted at zero is not
// offered. So running the saved checks removed the way back to running the relationships.
// ===================================================================================================
{
  const full = model.foldFamilyResult(null, {
    ran: true, value: { summary: { relationships: 5 } }, whenIso: '2026-09-10T09:00:00Z', modelKey: 'Contoso',
  });
  assert.equal(model.relationshipMenuCount(full), 5);

  const afterSavedChecksOnly = model.foldFamilyResult(full, {
    ran: false, value: { summary: { relationships: 0 } }, whenIso: '2026-09-15T11:30:00Z', modelKey: 'Contoso',
  });
  assert.equal(model.relationshipMenuCount(afterSavedChecksOnly), 5,
    'the retained count, never the skipped run’s empty report');
  const groups = model.runScopeGroups(5, model.relationshipMenuCount(afterSavedChecksOnly), 2);
  assert.ok(groups.some((g) => g.id === 'relationships'), 'so the choice is still on the menu');
  assert.equal(groups.find((g) => g.id === 'relationships').count, 5, 'with the count it last measured');

  assert.equal(model.relationshipMenuCount(null), null,
    'before any run the size is UNKNOWN, which is not zero, so the group is still offered');
  assert.ok(model.runScopeGroups(5, model.relationshipMenuCount(null), 2).some((g) => g.id === 'relationships'));

  // A model that genuinely has none is the one case that withholds the choice.
  const none = model.foldFamilyResult(null, {
    ran: true, value: { summary: { relationships: 0 } }, whenIso: '2026-09-10T09:00:00Z', modelKey: 'Contoso',
  });
  assert.equal(model.relationshipMenuCount(none), 0);
  assert.ok(!model.runScopeGroups(5, model.relationshipMenuCount(none), 2).some((g) => g.id === 'relationships'));
}
assert.doesNotMatch(page, /run\?\.relationships\?\.summary\.relationships/,
  'the menu count must not come from the newest run, which may have skipped the family');
assert.match(page, /relationshipMenuCount\(relationships\)/, 'it comes from what was retained');

// The table-mapping drawer promised the same thing the relationship card stopped promising.
assert.doesNotMatch(authoring, /This runs with every other check\./, 'a saved-check run does not count tables');
assert.match(authoring, /This runs when you run everything or choose table row counts\./);

// ===================================================================================================
// ROUND THREE (Astra, 2026-09-15, third pass). A limit the engine accepts must survive being written
// down, and the History row must show what was written down rather than reformat it.
// ===================================================================================================
assert.equal(model.toleranceSentence(1e-21, 1e-7),
  'Allowed difference: 0.000000000000000000001 or 0.00001%, whichever is larger.',
  'a tiny absolute limit recorded as 0 is a limit the person never set');
assert.equal(model.toleranceSentence(0.12345678901234566, 1e-7),
  'Allowed difference: 0.12345678901234566 or 0.00001%, whichever is larger.',
  'and a long one keeps its tail');
assert.equal(model.toleranceSentence(0, 1e-21), 'Allowed difference: 0.0000000000000000001%.');
assert.equal(model.toleranceSentence(0.000001234567, 0.0000001),
  'Allowed difference: 0.000001234567 or 0.00001%, whichever is larger.', 'the earlier case still holds');
for (const [value, shown] of [[0.5, '0.5'], [1, '1'], [100, '100'], [0.05, '0.05']]) {
  assert.equal(model.toleranceSentence(value, 0), `Allowed difference: ${shown}.`,
    'an ordinary number still reads like one');
}
assert.doesNotMatch(model.toleranceSentence(1e-21, 1e-7), /e-/, 'and never in scientific notation');

// The History row shows the sentence the RUN recorded, verbatim. Re-deriving it from these rules would
// describe the check as it stands today, which is the whole thing finding 6 was about.
assert.match(page, /settings\.push\(\['Tolerance', check\.tolerance\]\)/,
  'the recorded tolerance is rendered as stored, never reformatted by the page');
assert.doesNotMatch(page, /toleranceSentence\([^)]*check\./,
  'and the page must not recompute a recorded run’s tolerance from its own numbers');

// No em dash anywhere in either module.
for (const file of ['webview/src/tests-model.ts', 'webview/src/surface-stack.ts']) {
  assert.doesNotMatch(readFileSync(resolve(root, file), 'utf8'), /—/, `no em dashes in ${file}`);
}

console.log('Tests page fixes (Astra 2026-09-15) passed');
