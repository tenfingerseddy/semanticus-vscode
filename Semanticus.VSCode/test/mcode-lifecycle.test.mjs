import assert from 'node:assert/strict';
import { performance } from 'node:perf_hooks';
import {
  createBusyOwnerGate,
  createContextBusyOwnerGate,
  createRequestGate,
  isMContextCurrent,
  mContextToken,
  pollingExpressionForSave,
  profileSubjectToken,
  reconcileExternalM,
  reconcileProfileSubject,
  reconcileSaveRevision,
  serverRevisionMatches,
  transformErrorForAction,
  transformErrorKey,
} from '../webview/src/mcodelifecycle.mjs';

// Finding 1: an async completion belongs to the exact table, query, and text revision captured at start.
// Switching either the query or revision before completion prevents the captured result from being applied.
{
  let current = mContextToken('Sales', 'partition:Sales/Main', 7);
  const captured = current;
  let applied = '';
  const complete = (value) => { if (isMContextCurrent(captured, current)) applied = value; };
  current = mContextToken('Inventory', 'partition:Sales/Main', 7);
  complete('table-switch result');
  assert.equal(applied, '', 'a completion from table A is discarded after switching to table B');

  current = mContextToken('Sales', 'partition:Sales/Archive', 7);
  complete('query-switch result');
  assert.equal(applied, '', 'a completion from query A is discarded after switching to query B');

  current = captured;
  current = mContextToken('Sales', 'partition:Sales/Main', 8);
  complete('old parse result');
  assert.equal(applied, '', 'a completion from an older text revision cannot overwrite a manual edit');

  current = captured;
  complete('current result');
  assert.equal(applied, 'current result', 'the unchanged context accepts its own completion');
}

// Finding 2: out-of-band M writes reload a clean editor, conflict with a dirty editor, invalidate Profile in
// both cases, and Save refuses a server revision that differs from the loaded revision.
{
  const clean = reconcileExternalM({ text: 'server v1', original: 'server v1' }, 'server v2');
  assert.deepEqual(clean, {
    kind: 'reloaded', text: 'server v2', original: 'server v2', serverText: null, profileInvalidated: true,
  });

  const dirty = reconcileExternalM({ text: 'local edit', original: 'server v1' }, 'server v2');
  assert.deepEqual(dirty, {
    kind: 'conflict', text: 'local edit', original: 'server v1', serverText: 'server v2', profileInvalidated: true,
  });
  assert.equal(serverRevisionMatches('server v1', 'server v2'), false, 'Save blocks a newer server-side M revision');
  assert.equal(serverRevisionMatches('server v1', 'server v1'), true, 'Save proceeds when the loaded revision is current');
  assert.equal(reconcileSaveRevision('server v1', 'local edit', 'server v1'), 'write');
  assert.equal(reconcileSaveRevision('server v1', 'local edit', 'local edit'), 'already-saved',
    'Save accepts the exact requested text when it is already on the server');
  assert.equal(reconcileSaveRevision('server v1', 'local edit', 'different server edit'), 'conflict',
    'Save keeps an honest conflict for different server text');
}

// Round 2 finding 1: profile invalidation follows the profiled table's M subject, not the editor selection. A
// range-filter write to Sales must invalidate Sales results even while an unchanged shared expression is selected.
{
  const selectedExpression = { text: 'shared v1', original: 'shared v1' };
  const editorResult = reconcileExternalM(selectedExpression, 'shared v1');
  assert.equal(editorResult.kind, 'unchanged', 'the selected shared expression itself did not change');

  const before = profileSubjectToken('Sales', [
    { name: 'Main', sourceType: 'M', source: 'let Source = 1 in Source' },
  ]);
  const subjectResult = reconcileProfileSubject(before, 'Sales', [
    { name: 'Main', sourceType: 'M', source: 'let Source = 1, Filtered = Table.SelectRows(Source, each true) in Filtered' },
  ]);
  assert.equal(subjectResult.profileInvalidated, true,
    "an out-of-band write to the profiled table invalidates results while a shared expression is selected");
}

// Round 3 finding 1: a table profile also depends on every named M document reached from its partitions. Repairing
// a referenced parameter must invalidate results even when the already-filtered partition text is unchanged.
{
  const partitions = [{
    name: 'Main', sourceType: 'M',
    source: 'let Source = #"Shared Source", Filtered = Table.SelectRows(Source, each [Date] >= RangeStart and [Date] < RangeEnd) in Filtered',
  }];
  const expressions = [
    { name: 'RangeStart', kind: 'M', expression: '#datetime(2020, 1, 1, 0, 0, 0)' },
    { name: 'RangeEnd', kind: 'M', expression: '#datetime(2021, 1, 1, 0, 0, 0)' },
    { name: 'Shared Source', kind: 'M', expression: 'Endpoint' },
    { name: 'Endpoint', kind: 'M', expression: '"server-v1"' },
    { name: 'Unrelated', kind: 'M', expression: '1' },
  ];
  const before = profileSubjectToken('Sales', partitions, expressions);
  const repairedParameters = expressions.map((expression) => expression.name === 'RangeStart'
    ? { ...expression, expression: '#datetime(2020, 1, 1, 0, 0, 0) meta [IsParameterQuery=true]' }
    : expression);
  assert.equal(reconcileProfileSubject(before, 'Sales', partitions, repairedParameters).profileInvalidated, true,
    'a parameter-only M repair invalidates the profile while the partition stays unchanged');

  const changedTransitiveDependency = expressions.map((expression) => expression.name === 'Endpoint'
    ? { ...expression, expression: '"server-v2"' }
    : expression);
  assert.equal(reconcileProfileSubject(before, 'Sales', partitions, changedTransitiveDependency).profileInvalidated, true,
    'a transitive named-expression write invalidates the dependent table profile');

  const changedUnrelatedExpression = expressions.map((expression) => expression.name === 'Unrelated'
    ? { ...expression, expression: '2' }
    : expression);
  assert.equal(reconcileProfileSubject(before, 'Sales', partitions, changedUnrelatedExpression).profileInvalidated, false,
    'an unrelated named-expression write does not invalidate the table profile');
}

// Round 4 finding: dependency references use the full M quoted-identifier escape grammar and Unicode identifier
// characters. Missing either makes a changed named expression look falsely current to an existing table profile.
{
  const escapedNamePartitions = [{
    name: 'Main', sourceType: 'M', source: 'let Source = #"A#(0020)B" in Source',
  }];
  const escapedNameExpressions = [{ name: 'A B', kind: 'M', expression: '1' }];
  const escapedNameBefore = profileSubjectToken('Sales', escapedNamePartitions, escapedNameExpressions);
  assert.equal(reconcileProfileSubject(escapedNameBefore, 'Sales', escapedNamePartitions, [
    { ...escapedNameExpressions[0], expression: '2' },
  ]).profileInvalidated, true,
  'a quoted partition reference decodes its M hex escape before matching the named expression');

  const fullEscapeName = 'A\r\n\t#\u{1F600}"B';
  const fullEscapePartitions = [{
    name: 'Main', sourceType: 'M',
    source: 'let Source = #"A#(cr)#(lf)#(tab)#(#)#(0001F600)""B" in Source',
  }];
  const fullEscapeExpressions = [{ name: fullEscapeName, kind: 'M', expression: '1' }];
  const fullEscapeBefore = profileSubjectToken('Sales', fullEscapePartitions, fullEscapeExpressions);
  assert.equal(reconcileProfileSubject(fullEscapeBefore, 'Sales', fullEscapePartitions, [
    { ...fullEscapeExpressions[0], expression: '2' },
  ]).profileInvalidated, true,
  'control, hash, long Unicode, and doubled-quote escapes share the M quoted-identifier decoder');

  const combiningName = 'Cafe\u0301';
  const combiningPartitions = [{
    name: 'Main', sourceType: 'M', source: `let Source = ${combiningName} in Source`,
  }];
  const combiningExpressions = [{ name: combiningName, kind: 'M', expression: '1' }];
  const combiningBefore = profileSubjectToken('Sales', combiningPartitions, combiningExpressions);
  assert.equal(reconcileProfileSubject(combiningBefore, 'Sales', combiningPartitions, [
    { ...combiningExpressions[0], expression: '2' },
  ]).profileInvalidated, true,
  'a combining mark remains part of its bare M identifier dependency');

  const invalidEscapeName = 'A#(invalid#(cr))B\n';
  const invalidEscapePartitions = [{
    name: 'Main', sourceType: 'M', source: 'let Source = #"A#(invalid#(cr))B#(lf)" in Source',
  }];
  const invalidEscapeExpressions = [{ name: invalidEscapeName, kind: 'M', expression: '1' }];
  const invalidEscapeBefore = profileSubjectToken('Sales', invalidEscapePartitions, invalidEscapeExpressions);
  assert.equal(reconcileProfileSubject(invalidEscapeBefore, 'Sales', invalidEscapePartitions, [
    { ...invalidEscapeExpressions[0], expression: '2' },
  ]).profileInvalidated, true,
  'an invalid escape stays literal through its first close while a following valid escape still decodes');

  const unmatchedEscapeName = 'A#(invalid"B';
  const unmatchedEscapePartitions = [{
    name: 'Main', sourceType: 'M', source: 'let Source = #"A#(invalid""B" in Source',
  }];
  const unmatchedEscapeExpressions = [{ name: unmatchedEscapeName, kind: 'M', expression: '1' }];
  const unmatchedEscapeBefore = profileSubjectToken('Sales', unmatchedEscapePartitions, unmatchedEscapeExpressions);
  assert.equal(reconcileProfileSubject(unmatchedEscapeBefore, 'Sales', unmatchedEscapePartitions, [
    { ...unmatchedEscapeExpressions[0], expression: '2' },
  ]).profileInvalidated, true,
  'an unmatched escape stays literal without suppressing later doubled-quote decoding');
}

// Round 5 finding: malformed quoted-identifier escapes must be scanned once. The former global regex retried at
// every nested "#(" and took seconds for this reviewer-shaped 128 KB identifier.
{
  const unmatchedEscapeCount = 64_000;
  const malformedName = '#('.repeat(unmatchedEscapeCount);
  const started = performance.now();
  const token = profileSubjectToken('Sales', [
    { name: 'Main', sourceType: 'M', source: `#"${malformedName}"` },
  ], [
    { name: malformedName, kind: 'M', expression: '1' },
  ]);
  const elapsedMs = performance.now() - started;
  assert.ok(elapsedMs < 100,
    `64,000 unmatched quoted-identifier escapes must complete under 100 ms (measured ${elapsedMs.toFixed(3)} ms)`);
  assert.deepEqual(JSON.parse(token)[2], [[malformedName, '1']],
    'an unmatched escape remains literal so invalid M can still be diagnosed safely');
  console.log(`M Code malformed quoted-identifier perf: ${elapsedMs.toFixed(3)} ms for ${unmatchedEscapeCount} unmatched escapes`);
}

// Round 2 finding 2: an abandoned A operation cannot release or act as the owner of a newer A operation after
// A -> B -> A. The monotonically increasing owner id distinguishes otherwise identical resource/action pairs.
{
  const gate = createBusyOwnerGate();
  const olderA = gate.begin({ resource: 'preview:Sales', action: 'keep-top' });
  assert.ok(olderA);
  gate.reset(); // switch A -> B
  const operationB = gate.begin({ resource: 'preview:Inventory', action: 'keep-top' });
  assert.ok(operationB);
  gate.reset(); // switch B -> A
  const newerA = gate.begin({ resource: 'preview:Sales', action: 'keep-top' });
  assert.ok(newerA);
  assert.notEqual(olderA.ownerId, newerA.ownerId);
  assert.equal(gate.isOwner(olderA), false, 'the discarded A completion no longer owns the lock');
  assert.equal(gate.release(olderA), false, 'the discarded A completion cannot release the newer A lock');
  assert.equal(gate.isOwner(newerA), true, 'the newer A operation keeps its lock');
  assert.equal(gate.release(newerA), true, 'only the current owner can release the lock');
}

// Round 3 finding 2: changing the M context abandons an applied-step owner immediately. The same context boundary
// also drives the panel's draft/error reset, so a same-named step in the next query starts clean and unlocked.
{
  const queryA = mContextToken('Sales', 'partition:Sales/Main', 3);
  const queryB = mContextToken('Sales', 'expr:Shared', 4);
  const gate = createContextBusyOwnerGate(queryA);
  let editing = 'Source';
  let stepError = { name: 'Source', text: 'rename failed' };
  const renameA = gate.begin({ resource: 'step:Source', action: 'rename' }, queryA);
  assert.ok(renameA);

  if (gate.resetContext(queryB)) { editing = null; stepError = null; }
  assert.equal(editing, null, 'the rename draft is reset on query switch');
  assert.equal(stepError, null, 'the prior query step error is reset on query switch');
  assert.equal(gate.isOwner(renameA), false, 'the abandoned parse no longer owns the applied-step lock');

  const deleteB = gate.begin({ resource: 'step:Source', action: 'delete' }, queryB);
  assert.ok(deleteB, 'the new query can start a same-named step action before the old parse finishes');
  assert.equal(gate.release(renameA), false, 'the old completion cannot release the new query owner');
  assert.equal(gate.isOwner(deleteB), true);
  assert.equal(gate.release(deleteB), true);
}

// Finding 3: disclosure visibility is not an input to persistence. The loaded expression round-trips while the
// disclosure is closed, and only explicit empty text clears it.
{
  const stored = 'DateTime.LocalNow()';
  for (const advancedOpen of [false, true]) {
    assert.equal(pollingExpressionForSave(stored), stored, `stored polling survives when advancedOpen=${advancedOpen}`);
  }
  assert.equal(pollingExpressionForSave('   '), null, 'explicitly clearing the field persists null');
}

// Finding 4: failures cannot migrate across a table, column, or action when a different form is shown.
{
  const errors = new Map();
  errors.set(transformErrorKey('Sales', 'Amount', 'rename-column'), 'rename failed');
  assert.equal(errors.get(transformErrorKey('Sales', 'Amount', 'rename-column')), 'rename failed');
  assert.equal(errors.get(transformErrorKey('Sales', 'Amount', 'change-type')), undefined);
  assert.equal(errors.get(transformErrorKey('Sales', 'Date', 'rename-column')), undefined);
  assert.equal(errors.get(transformErrorKey('Inventory', 'Amount', 'rename-column')), undefined);
}

// Round 2 finding 4: render lookup is action-specific. An older failure cannot mask the newer failure beside a
// different control on the same target.
{
  const errors = {};
  errors[transformErrorKey('Sales', undefined, 'remove-duplicates')] = 'older distinct failure';
  errors[transformErrorKey('Sales', undefined, 'keep-top')] = 'newer top failure';
  assert.equal(transformErrorForAction(errors, 'Sales', undefined, 'remove-duplicates'), 'older distinct failure');
  assert.equal(transformErrorForAction(errors, 'Sales', undefined, 'keep-top'), 'newer top failure');

  errors[transformErrorKey('Sales', 'Amount', 'remove-column')] = 'older remove failure';
  errors[transformErrorKey('Sales', 'Amount', 'sort-ascending')] = 'newer sort failure';
  assert.equal(transformErrorForAction(errors, 'Sales', 'Amount', 'remove-column'), 'older remove failure');
  assert.equal(transformErrorForAction(errors, 'Sales', 'Amount', 'sort-ascending'), 'newer sort failure');
}

// Finding 5: unmount cancellation makes the abandoned request stale before another probe can issue. A remounted
// instance has a separate gate, so its first run cannot overlap through shared request identity.
{
  const abandoned = createRequestGate();
  const oldRequest = abandoned.begin();
  assert.equal(abandoned.isCurrent(oldRequest), true);
  abandoned.cancel();
  assert.equal(abandoned.isCurrent(oldRequest), false, 'unmount cancellation invalidates the abandoned run');

  const remounted = createRequestGate();
  const freshRequest = remounted.begin();
  assert.equal(remounted.isCurrent(freshRequest), true);
  assert.equal(abandoned.isCurrent(oldRequest), false, 'the old instance stays cancelled after remount');
}

console.log('M Code revision, persistence, error ownership, and cancellation behavior tests passed');
