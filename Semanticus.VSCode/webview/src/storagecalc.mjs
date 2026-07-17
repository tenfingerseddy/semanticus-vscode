// Pure decisions for the Storage tab: snapshot persistence keys, comparison compatibility, and the
// deterministic finding gates. A plain ES module (no framework, no engine) so the node contract test
// (test/storage-logic.test.mjs) executes the SAME code the webview ships — the workflowruns.mjs pattern.

/** The persistence anchor: the most stable identity of the QUERY-TARGET model — the model vpaq_scan actually
 *  scans — or NULL when none exists. It MUST describe the query target (the attached live connection's
 *  endpoint+database), NOT the editing session: in attach mode a file-backed editing session can have two
 *  different query models swapped onto it, and anchoring on the editing identity would give them the SAME key
 *  and stage a clean-looking comparison between unrelated models. Live query connection: the endpoint+database
 *  PAIR — a database name alone repeats across endpoints, so it could chain a scan of one server's model onto
 *  another's; a database without a known endpoint gets no key at all rather than an ambiguous one. Disk: the
 *  source path (only reachable with no live connection, where no scan runs — a last-resort fallback). NEITHER →
 *  null: the caller keeps the comparison chain in memory for the session and never persists (the old 'local'
 *  fallback was one shared bucket every anonymous model wrote into). NEVER the model fingerprint (deleting a
 *  column — the very action the loop encourages — changes the shape fingerprint and would orphan the baseline). */
export function anchorOf(queryEndpoint, queryDatabase, source) {
  if (queryDatabase) return queryEndpoint ? queryEndpoint + '|' + queryDatabase : null;
  if (queryEndpoint) return null;   // a live target with a half-known identity must never fall back to the editing source
  return source || null;
}

/** Normalize the wire mode fail-closed. Missing and future values are unknown, never Import. */
export function normalizeStorageMode(mode) {
  return mode === 'import' || mode === 'directLake' ? mode : 'unknown';
}

/** Snapshot store key: mode first, then the anchor. Import, resident-only, and unknown scans measure
 *  potentially different things, so they can never share a comparison chain. The UI suppresses the
 *  unknown-mode chain entirely, but keeping the key function injective makes that contract safe for
 *  every caller. */
export function snapKey(anchor, storageMode) {
  const mode = normalizeStorageMode(storageMode);
  return `stats:scan:${mode === 'directLake' ? 'directlake' : mode}:${anchor}`;
}

/** A scan may only read or write the comparison bucket whose query-target anchor it captured when the
 *  scan began. This guard is intentionally independent of the async generation token: passive effects
 *  from the connection-swap commit still see the old ScanState alongside the new current anchor. */
export function scanMatchesAnchor(scanIdentity, currentAnchor) {
  return scanIdentity === currentAnchor;
}

/** Classify a scan while the host's asynchronous connection snapshot catches up with the engine-owned identity.
 *  A report with identity B may arrive while the host anchor is still null: retain it as pending, then call the
 *  null-to-B transition resolved so an in-memory pin can migrate into B's bucket. A previously established A anchor
 *  changing to B is still a genuine swap, even if a B report lands in the same render. */
export function scanIdentityTransition(scanIdentity, previousAnchor, currentAnchor) {
  if (previousAnchor != null && previousAnchor !== currentAnchor) return 'swap';
  if (scanIdentity === currentAnchor) {
    return previousAnchor == null && currentAnchor != null ? 'resolved' : 'current';
  }
  if (previousAnchor == null && currentAnchor == null && scanIdentity != null) return 'pending';
  return 'swap';
}

/** Can the scan remain in memory during this transition? Persistence still requires scanMatchesAnchor. */
export function scanUsableDuringTransition(scanIdentity, previousAnchor, currentAnchor) {
  return scanIdentityTransition(scanIdentity, previousAnchor, currentAnchor) !== 'swap';
}

/** Evidence level for joining query-copy storage rows to editing objects. Linked-copy relationships prove
 *  provenance only: either copy can be stale or structurally different, so their bytes remain read-only query-copy
 *  observations and never become measurements of same-named editing objects. */
export function storageEvidenceLevel(relationship) {
  if (relationship === 'sameInstance') return 'currentEditingModel';
  if (relationship === 'workingCopyAndPublished' || relationship === 'workingCopyAndLocalRuntime') return 'staleQueryCopy';
  return 'none';
}

/** Only sameInstance supports byte-grounded editing-object claims and direct editing actions. */
export function relationshipAllowsStorageEdits(relationship) {
  return storageEvidenceLevel(relationship) === 'currentEditingModel';
}

/** Linked copies may still create delete_if_unused plan items because apply re-verifies referential safety in the
 *  editing model. This permission never carries query-copy byte evidence or a reduction estimate. */
export function relationshipAllowsReverifiedDeletePlan(relationship) {
  return relationship === 'sameInstance'
    || relationship === 'workingCopyAndPublished'
    || relationship === 'workingCopyAndLocalRuntime';
}

/** The coverage basis of a snapshot: which population its component split (data/dict/hash) was summed
 *  over. 'full' when every byte of the known total is attributed (exact equality — no rounding); else
 *  the count of scanned columns, because two partial scans are only component-comparable when they
 *  covered the same top-N population. The engine computes the known total over ALL columns before the
 *  top-N cut, so the TOTAL stays comparable across any two bases — only the component split is gated. */
export function coverageBasis(snap) {
  if (!snap) return null;
  return snap.attributed === snap.known ? 'full' : 'top:' + (snap.scannedColumns ?? 0);
}

/**
 * Decide what a comparison between the current scan and a stored snapshot may honestly claim.
 * Returns:
 *  - { available: false, reason: 'none' | 'fingerprint' }  — nothing to compare / identity unconfirmed
 *  - { available: true, structureChanged, componentsComparable } — total delta is always valid;
 *    component + finding deltas only when componentsComparable; structureChanged flags that the model
 *    shape differs (label caveat — the comparison still shows, because the did-it-help loop must
 *    survive the structural fixes it recommends).
 */
export function compareDecision(cur, prev) {
  if (!cur || !prev) return { available: false, reason: 'none' };
  // Identity unconfirmed on EITHER side → no comparison, honest note. Never guess.
  if (!cur.fingerprintKey || !prev.fingerprintKey) return { available: false, reason: 'fingerprint' };
  return {
    available: true,
    structureChanged: cur.fingerprintKey !== prev.fingerprintKey,
    componentsComparable: coverageBasis(cur) === coverageBasis(prev),
  };
}

/** May this snapshot become the stored "last scan"? The newest identity-CONFIRMED scan (fingerprint
 *  captured, any origin, any coverage) ALWAYS wins and stores its coverage — whether two scans'
 *  components are comparable is decided at COMPARE time (compareDecision), never by refusing to store
 *  (a coverage-based refusal let one narrow agent scan starve every later scan out of the chain). An
 *  identity-unconfirmed snapshot never replaces anything: compareDecision suppresses on a null
 *  fingerprint, so storing it could only evict a usable comparison point for a transient hiccup. */
export function shouldStoreAsLast(next) {
  return next != null && next.fingerprintKey != null;
}

/** May a comparison claim a finding "resolved"? ONLY between two FULL-coverage snapshots: with a top-N
 *  cut on either side, a finding can leave the visible population by RANK SHIFT alone — absent is not fixed. */
export function resolvedClaimAllowed(cur, prev) {
  return coverageBasis(cur) === 'full' && coverageBasis(prev) === 'full';
}

/** May a comparison claim a finding "Introduced" (it appeared SINCE the comparison)? ONLY when the PRIOR scan
 *  had FULL coverage. Under a top-N prior scan the whole population was never observed, so a finding now visible
 *  may have been present-but-out-of-window before — it is "newly observed", not introduced. Symmetric with
 *  resolvedClaimAllowed (which gates the other direction): both guard against a rank shift masquerading as a
 *  change. The finding is demonstrably present NOW regardless of the CURRENT scan's coverage, so only the prior
 *  scan's coverage decides whether "introduced since" is honest. */
export function introducedClaimAllowed(prev) {
  return coverageBasis(prev) === 'full';
}

/** '/' is legal in BOTH a table and a column name, and the engine's ref grammar splits on the FIRST
 *  slash — so 'column:Sales/EU/Amount' is ambiguous (Sales/EU[Amount] vs Sales[EU/Amount]). Destructive
 *  actions fail CLOSED on such names; the fix is a rename or the Model tree, never a guessed delete. */
export function refIsAmbiguous(table, column) {
  return String(table ?? '').includes('/') || String(column ?? '').includes('/');
}

// Identifier-suffix detection with a token boundary: the suffix must start its own word. The camelCase
// branch is case-SENSITIVE (the lowercase→capital hump IS the boundary — AmountPaid/Valid/Casino/Episode
// never match); the separator branch is case-INSENSITIVE (the separator is the boundary, so CUSTOMER_ID,
// ORDER ID and STORE-CODE match the same as their lowercase forms).
const ID_SUFFIX_CAMEL = /(?:^|[a-z0-9])(?:Key|Id|ID|Code|No|Number)$/;
const ID_SUFFIX_SEP = /(?:^|[_\s.-])(?:key|id|code|no|number)$/i;

/** Does this column look like an identifier (for the summarize-by cleanup)? An explicit key flag always
 *  qualifies; otherwise the name must END with an identifier token on a real token boundary. */
export function identifierLike(name, isKey) {
  const n = String(name ?? '');
  return !!isKey || ID_SUFFIX_CAMEL.test(n) || ID_SUFFIX_SEP.test(n);
}
