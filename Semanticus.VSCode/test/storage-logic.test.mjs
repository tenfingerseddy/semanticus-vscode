// Storage tab decision contracts — executes the SAME module the webview ships (webview/src/storagecalc.mjs):
// snapshot persistence anchors/keys, comparison compatibility (fingerprint label + coverage gating), the
// newest-confirmed-wins last-snapshot policy, the full-coverage gate on "resolved" claims, the
// slash-ambiguity fail-closed gate, and the identifier-suffix token-boundary regexes. Pure node, no build
// step (the workflowruns reducer test pattern).
import assert from 'node:assert/strict';
import {
  anchorOf, normalizeStorageMode, snapKey, scanMatchesAnchor, scanIdentityTransition, scanUsableDuringTransition,
  storageEvidenceLevel, relationshipAllowsStorageEdits, relationshipAllowsReverifiedDeletePlan,
  coverageBasis, compareDecision, shouldStoreAsLast, resolvedClaimAllowed, introducedClaimAllowed,
  refIsAmbiguous, identifierLike,
} from '../webview/src/storagecalc.mjs';

let passed = 0;
const ok = (name, fn) => { fn(); passed++; console.log('  [PASS] ' + name); };

// ---- persistence anchor: the QUERY-TARGET endpoint|database when connected, source path on disk, NULL when anonymous ----
ok('anchorOf: query-target = the endpoint+database PAIR (a database name alone repeats across endpoints)', () => {
  assert.equal(anchorOf('powerbi://api/ws1', 'ContosoDb', null), 'powerbi://api/ws1|ContosoDb');
  assert.notEqual(anchorOf('powerbi://api/ws1', 'ContosoDb', null), anchorOf('powerbi://api/ws2', 'ContosoDb', null));
});
ok('anchorOf: two different query databases on one endpoint get DIFFERENT keys (no clean-looking compare between unrelated models)', () => {
  assert.notEqual(anchorOf('powerbi://api/ws1', 'ModelA', null), anchorOf('powerbi://api/ws1', 'ModelB', null));
});
ok('anchorOf: a database WITHOUT a known endpoint gets no key (ambiguous), not a bare-database one', () => {
  assert.equal(anchorOf(null, 'ContosoDb', null), null);
  assert.equal(anchorOf('', 'ContosoDb', 'C:/models/contoso'), null);   // live identity half-known → refuse, never guess a bucket
  assert.equal(anchorOf('powerbi://api/ws1', null, 'C:/models/contoso'), null); // endpoint-only live identity must not fall back to editing source
});
ok('anchorOf: on-disk models key on the source path', () => {
  assert.equal(anchorOf(null, null, 'C:/models/contoso'), 'C:/models/contoso');
});
ok('anchorOf: NO identity → null (never a shared fallback bucket — the old literal compared unrelated models)', () => {
  assert.equal(anchorOf(undefined, undefined, undefined), null);
  assert.equal(anchorOf('', '', ''), null);   // empty strings are not identities
});
ok('storage mode is an explicit tri-state and missing values fail closed as unknown', () => {
  assert.equal(normalizeStorageMode('import'), 'import');
  assert.equal(normalizeStorageMode('directLake'), 'directLake');
  assert.equal(normalizeStorageMode('unknown'), 'unknown');
  assert.equal(normalizeStorageMode(undefined), 'unknown');
  assert.equal(normalizeStorageMode('future-mode'), 'unknown');
});
ok('snapKey is mode-first and separates Import, Direct Lake, and unknown chains', () => {
  assert.equal(snapKey('ep|ContosoDb', 'import'), 'stats:scan:import:ep|ContosoDb');
  assert.equal(snapKey('ep|ContosoDb', 'directLake'), 'stats:scan:directlake:ep|ContosoDb');
  assert.equal(snapKey('ep|ContosoDb', 'unknown'), 'stats:scan:unknown:ep|ContosoDb');
  assert.notEqual(snapKey('a', 'directLake'), snapKey('a', 'import'));
  assert.notEqual(snapKey('a', 'unknown'), snapKey('a', 'import'));
});

// ---- connection-swap persistence: old ScanState must never write into the new anchor's bucket ----
ok('connection swap A to B persists nothing under B and does not migrate A pin', () => {
  const anchorA = anchorOf('endpoint', 'ModelA', null);
  const anchorB = anchorOf('endpoint', 'ModelB', null);
  const completedA = { identity: anchorA, fingerprintKey: 'fp-a', known: 100, attributed: 100, scannedColumns: 1 };
  const pinA = { fingerprintKey: 'pin-a', known: 120, attributed: 120, scannedColumns: 1 };
  const stores = new Map([[snapKey(anchorA, 'import'), { last: completedA, pinned: pinA }]]);

  // Mirrors the App persistence effect: React has committed B's anchor, but clearScan has not rendered yet,
  // so the completed ScanState and in-memory pin are still A's.
  const persist = (scan, currentAnchor, inMemoryPin) => {
    if (!scanMatchesAnchor(scan.identity, currentAnchor)) return;
    const key = snapKey(currentAnchor, 'import');
    const stored = stores.get(key) ?? { last: null, pinned: null };
    stores.set(key, { last: scan, pinned: inMemoryPin ?? stored.pinned });
  };
  persist(completedA, anchorB, pinA);

  assert.equal(stores.has(snapKey(anchorB, 'import')), false, 'A snapshot must not become B last');
  assert.equal(stores.get(snapKey(anchorA, 'import')).pinned, pinA, 'A pin stays only in A bucket');
});

ok('engine switch before connection refresh: a B report is tagged B and refused under A key', () => {
  const anchorA = anchorOf('endpoint', 'ModelA', null);
  const anchorB = anchorOf('endpoint', 'ModelB', null);
  const engineReportB = { queryIdentity: anchorB };
  const completedB = { identity: engineReportB.queryIdentity, fingerprintKey: 'fp-b', known: 90, attributed: 90, scannedColumns: 1 };
  const stores = new Map();

  // The host connection snapshot still says A. ScanState takes the identity from the engine report, not that stale
  // host anchor, and the strict persistence guard refuses to place B's bytes in A's bucket.
  assert.equal(completedB.identity, anchorB);
  assert.equal(scanIdentityTransition(completedB.identity, anchorA, anchorA), 'swap');
  if (scanMatchesAnchor(completedB.identity, anchorA)) {
    stores.set(snapKey(anchorA, 'import'), { last: completedB, pinned: null });
  }
  assert.equal(stores.has(snapKey(anchorA, 'import')), false);
});

ok('same-render A to B swap refuses B persistence, A pin migration, and B-to-A comparison pairing', () => {
  const anchorA = anchorOf('endpoint', 'ModelA', null);
  const anchorB = anchorOf('endpoint', 'ModelB', null);
  const completedA = { identity: anchorA, fingerprintKey: 'fp-a', known: 100, attributed: 100, scannedColumns: 1 };
  const completedB = { identity: anchorB, fingerprintKey: 'fp-b', known: 90, attributed: 90, scannedColumns: 1 };
  const pinA = { fingerprintKey: 'pin-a', known: 120, attributed: 120, scannedColumns: 1 };
  const stores = new Map();

  // B's anchor and report arrive in one commit while the previous render's established anchor and pin are still A's.
  // Identity equality alone is insufficient: the A-to-B transition is a swap until the scheduled clear renders.
  const scanUsable = scanUsableDuringTransition(completedB.identity, anchorA, anchorB);
  const scanCurrent = scanUsable && scanMatchesAnchor(completedB.identity, anchorB);
  assert.equal(scanIdentityTransition(completedB.identity, anchorA, anchorB), 'swap');
  assert.equal(scanUsable, false);
  assert.equal(scanMatchesAnchor(completedB.identity, anchorB), true, 'the adversarial B report matches B by identity');

  const comparison = scanCurrent ? completedA : null;
  const evidenceAndMutationEligible = scanCurrent;
  if (scanCurrent) stores.set(snapKey(anchorB, 'import'), { last: completedB, pinned: pinA });

  assert.equal(stores.has(snapKey(anchorB, 'import')), false, 'B must not persist with A\'s in-memory pin');
  assert.equal(comparison, null, 'B must not compare against A\'s mounted snapshot');
  assert.equal(evidenceAndMutationEligible, false, 'B must not expose evidence or mutations during the swap commit');
  assert.notEqual(completedA.identity, completedB.identity);
});

ok('null to engine-resolved same target retains the scan and migrates its in-memory pin', () => {
  const anchorB = anchorOf('endpoint', 'ModelB', null);
  const completedB = { identity: anchorB, fingerprintKey: 'fp-b', known: 90, attributed: 90, scannedColumns: 1 };
  const pin = { fingerprintKey: 'pin-b', known: 100, attributed: 100, scannedColumns: 1 };
  const stores = new Map();

  assert.equal(scanIdentityTransition(completedB.identity, null, null), 'pending');
  assert.equal(scanUsableDuringTransition(completedB.identity, null, null), true, 'pending engine identity stays in memory');
  assert.equal(scanMatchesAnchor(completedB.identity, null), false, 'pending identity cannot write an anonymous bucket');

  assert.equal(scanIdentityTransition(completedB.identity, null, anchorB), 'resolved');
  assert.equal(scanUsableDuringTransition(completedB.identity, null, anchorB), true, 'resolution is not a swap');
  const scanCurrent = scanUsableDuringTransition(completedB.identity, null, anchorB)
    && scanMatchesAnchor(completedB.identity, anchorB);
  if (scanCurrent) {
    stores.set(snapKey(anchorB, 'import'), { last: completedB, pinned: pin });
  }
  assert.equal(stores.get(snapKey(anchorB, 'import')).pinned, pin, 'the pending pin migrates to the resolved target');
});

// ---- edit/query relationship: cross-model joins and mutations fail closed ----
ok('byte-grounded editing claims and direct storage edits require same-instance evidence', () => {
  assert.equal(storageEvidenceLevel('sameInstance'), 'currentEditingModel');
  assert.equal(relationshipAllowsStorageEdits('sameInstance'), true);
  assert.equal(relationshipAllowsStorageEdits('workingCopyAndPublished'), false);
  assert.equal(relationshipAllowsStorageEdits('workingCopyAndLocalRuntime'), false);
  assert.equal(relationshipAllowsStorageEdits('twoModels'), false);
  assert.equal(relationshipAllowsStorageEdits('queryingOnly'), false);
  assert.equal(relationshipAllowsStorageEdits(undefined), false);
});
ok('linked-copy provenance downgrades bytes to stale observations but keeps reverified delete plans eligible', () => {
  for (const relationship of ['workingCopyAndPublished', 'workingCopyAndLocalRuntime']) {
    assert.equal(storageEvidenceLevel(relationship), 'staleQueryCopy');
    assert.equal(relationshipAllowsStorageEdits(relationship), false, relationship + ' cannot attach bytes to editing objects');
    assert.equal(relationshipAllowsReverifiedDeletePlan(relationship), true, relationship + ' may use apply-time re-verification');
  }
  assert.equal(relationshipAllowsReverifiedDeletePlan('sameInstance'), true);
  assert.equal(storageEvidenceLevel('twoModels'), 'none');
  assert.equal(relationshipAllowsReverifiedDeletePlan('twoModels'), false);
});
ok('twoModels keeps a same-named scanned and editing column ineligible for mutation and byte evidence', () => {
  const scannedRef = 'column:Sales/LegacyId';
  const editingRefs = new Set(['column:Sales/LegacyId']);
  const textuallyReachable = editingRefs.has(scannedRef);
  const evidenceAndMutationEligible = relationshipAllowsStorageEdits('twoModels') && textuallyReachable;

  assert.equal(textuallyReachable, true, 'the adversarial names intentionally collide');
  assert.equal(evidenceAndMutationEligible, false, 'relationship proof is required in addition to a name match');
});

// ---- coverage basis: exact attributed==known equality, never rounded ----
const full = { known: 100, attributed: 100, scannedColumns: 64, fingerprintKey: 'fp1' };
const partial13 = { known: 100, attributed: 78, scannedColumns: 13, fingerprintKey: 'fp1' };
const partial25 = { known: 100, attributed: 90, scannedColumns: 25, fingerprintKey: 'fp1' };
ok('coverageBasis: full only on EXACT attributed==known (99.5% is still partial)', () => {
  assert.equal(coverageBasis(full), 'full');
  assert.equal(coverageBasis({ known: 1000, attributed: 995, scannedColumns: 20 }), 'top:20');
  assert.equal(coverageBasis(partial13), 'top:13');
  assert.equal(coverageBasis(null), null);
});

// ---- comparison decision: availability, structure label, component gating ----
ok('compareDecision: no prior snapshot means no comparison', () => {
  assert.deepEqual(compareDecision(full, null), { available: false, reason: 'none' });
  assert.deepEqual(compareDecision(null, full), { available: false, reason: 'none' });
});
ok('compareDecision: a missing fingerprint on EITHER side suppresses the comparison (honest note, never a guess)', () => {
  assert.deepEqual(compareDecision({ ...full, fingerprintKey: null }, partial13), { available: false, reason: 'fingerprint' });
  assert.deepEqual(compareDecision(full, { ...partial13, fingerprintKey: null }), { available: false, reason: 'fingerprint' });
});
ok('compareDecision: same fingerprint + same basis is a clean, fully comparable delta', () => {
  assert.deepEqual(compareDecision(full, { ...full }), { available: true, structureChanged: false, componentsComparable: true });
  assert.deepEqual(compareDecision(partial13, { ...partial13, attributed: 60 }), { available: true, structureChanged: false, componentsComparable: true });
});
ok('compareDecision: a DIFFERENT fingerprint keeps the comparison but labels it structure-changed (the loop must survive structural fixes)', () => {
  const d = compareDecision(full, { ...full, fingerprintKey: 'fp2' });
  assert.deepEqual(d, { available: true, structureChanged: true, componentsComparable: true });
});
ok('compareDecision: differing coverage bases keep only the total comparable (components gated off)', () => {
  assert.deepEqual(compareDecision(partial13, partial25), { available: true, structureChanged: false, componentsComparable: false });
  assert.deepEqual(compareDecision(full, partial13), { available: true, structureChanged: false, componentsComparable: false });
});

// ---- last-snapshot policy: the newest CONFIRMED scan always wins; compatibility is a COMPARE-time call ----
ok('shouldStoreAsLast: a confirmed scan replaces, whatever its coverage (an agent top-N seed must never starve a later scan, and a grown model must never be locked out)', () => {
  assert.equal(shouldStoreAsLast(partial13), true);   // narrow agent scan → still becomes last
  assert.equal(shouldStoreAsLast(partial25), true);   // different basis → still becomes last (compare-time gates components)
  assert.equal(shouldStoreAsLast(full), true);
});
ok('shouldStoreAsLast: an identity-unconfirmed snapshot never becomes the comparison point', () => {
  assert.equal(shouldStoreAsLast({ ...full, fingerprintKey: null }), false);
  assert.equal(shouldStoreAsLast(null), false);
});

// ---- "resolved" claims: only between two FULL-coverage scans (rank shift is not a fix) ----
ok('resolvedClaimAllowed: true only when BOTH sides have full coverage', () => {
  assert.equal(resolvedClaimAllowed(full, { ...full }), true);
  assert.equal(resolvedClaimAllowed(full, partial13), false);      // prior scan was top-N → absence could be rank shift
  assert.equal(resolvedClaimAllowed(partial13, full), false);      // current scan is top-N → same
  assert.equal(resolvedClaimAllowed(partial13, partial13), false); // same top-N is NOT the same population
});

// ---- "Introduced" claims: only when the PRIOR scan had full coverage (else a finding may be newly OBSERVED, not new) ----
ok('introducedClaimAllowed: true only when the PRIOR scan had full coverage', () => {
  assert.equal(introducedClaimAllowed(full), true);
  assert.equal(introducedClaimAllowed(partial13), false);   // prior top-N never saw the whole population → could be newly observed
  assert.equal(introducedClaimAllowed(partial25), false);
});
ok('introducedClaimAllowed: does NOT depend on the current scan — the finding is present NOW regardless of its coverage', () => {
  // Same prior (full) → introduced claimable whether the current scan is full or partial.
  assert.equal(introducedClaimAllowed(full), true);
});

// ---- slash ambiguity: destructive refs fail closed ----
ok('refIsAmbiguous flags a slash in either the table or the column name', () => {
  assert.equal(refIsAmbiguous('Sales', 'Amount'), false);
  assert.equal(refIsAmbiguous('Sales/EU', 'Amount'), true);      // 'column:Sales/EU/Amount' parses as Sales[EU/Amount]
  assert.equal(refIsAmbiguous('Sales', 'EU/Amount'), true);
  assert.equal(refIsAmbiguous(null, undefined), false);
});

// ---- identifier suffix: token boundary required, bare containment never matches ----
ok('identifierLike matches real identifier names (camelCase hump or separator before the suffix)', () => {
  for (const name of ['CustomerKey', 'OrderDateKey', 'ProductCode', 'OrderNo', 'InvoiceNumber', 'order_id', 'order key', 'store-code', 'ID', 'RowId']) {
    assert.equal(identifierLike(name, false), true, name + ' should match');
  }
});
ok('identifierLike: the separator branch is case-insensitive (UPPERCASE column names are common in warehouses)', () => {
  for (const name of ['CUSTOMER_ID', 'ORDER ID', 'STORE-CODE', 'ORDER KEY', 'INVOICE_NUMBER', 'Order_Id']) {
    assert.equal(identifierLike(name, false), true, name + ' should match');
  }
});
ok('identifierLike rejects bare-containment false positives (the camelCase boundary stays case-sensitive)', () => {
  for (const name of ['AmountPaid', 'Valid', 'Period', 'Casino', 'Episode', 'Ratio', 'Tornado', 'Grid', 'Placebo', 'Piano', 'VALID', 'CASINO']) {
    assert.equal(identifierLike(name, false), false, name + ' should NOT match');
  }
});
ok('identifierLike: the explicit key flag always qualifies, whatever the name', () => {
  assert.equal(identifierLike('TotallyOrdinaryName', true), true);
});

console.log(`\nstorage-logic.test.mjs: ${passed} passed`);
