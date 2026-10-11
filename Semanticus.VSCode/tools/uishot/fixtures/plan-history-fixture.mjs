// History inside the Plan: the fixture and its engine stub, in one file so the webview test and the browser
// driver (tools/uishot/cp-plan-history-drive.mjs) can never disagree about what the engine said.
//
// IT IS THE WIRE SHAPE. It is shaped by Semanticus.Engine/PlanHistory.cs (PlanHistoryView, PlanHistoryRow) as the
// JSON-RPC door sends it, camelCase: string ids, ISO times, `by` as one of you | assistant | semanticus | workflow,
// a parent row per cause nesting its edits under `children`, and `undoable`, `laterCount`, `undone`, `redoable`,
// `beforeThisSession`, `beforeRestore` worked out by the engine, never by the page.
//
// The content is the approved mockup's (plan-heart.html section 4): six rows this session, a seam, two rows from
// an earlier one. The undo stub keeps ONE number, how many of the newest edits are undone, and derives every row
// flag from it the way EditLedger does (contract 8.5), so an Undo to here in the driver strikes through exactly
// the rows the engine would.
//
// No imports and no top-level await: the driver inlines this source into the page after stripping `export`.

/** The pinned instant, 10:50 on 24 September 2026 in the machine's own timezone, so clock times read the same. */
export const HISTORY_FIXED_NOW = new Date(2026, 8, 24, 10, 50, 0, 0).getTime();

const at = (h, m) => new Date(2026, 8, 24, h, m, 0, 0).toISOString();
const yesterday = (h, m) => new Date(2026, 8, 23, h, m, 0, 0).toISOString();

export const SESSION_NOW = 'sess-today';
export const SESSION_OLD = 'sess-friday';
export const SESSION_RESTORED = 'sess-restored';

const d = (ref, prop, before, after, kind = 'update') => ({ kind, ref, props: [prop], before, after });

// Leaf edits, NEWEST FIRST: this order is the undo stack (index 0 is the next step Undo takes back).
const LEAVES = [
  { id: 'ed-00000001', group: 'plan_item:safefix/promotion-keys', title: 'hide_columns', when: at(10, 46), by: 'semanticus', rev: 58,
    deltas: [d('column:Promotion/PromotionKey', 'IsHidden', 'False', 'True'), d('column:Promotion/ProductKey', 'IsHidden', 'False', 'True'),
      d('column:Promotion/StoreKey', 'IsHidden', 'False', 'True')] },
  { id: 'ed-00000002', group: 'workflow_run:run-7f3a', title: 'set_sort_by_column', when: at(10, 45), by: 'workflow', rev: 57,
    deltas: [d('column:Date/Month', 'SortByColumn', null, 'Date[Month Number]')] },
  { id: 'ed-00000003', group: 'workflow_run:run-7f3a', title: 'set_measure_format', when: at(10, 45), by: 'workflow', rev: 56,
    deltas: [d('measure:Sales/Sales Amount', 'FormatString', null, '$#,0'), d('measure:Sales/Cost', 'FormatString', null, '$#,0'),
      d('measure:Sales/Margin', 'FormatString', null, '0.0%'), d('measure:Sales/Units Sold', 'FormatString', null, '#,0')] },
  { id: 'ed-00000004', group: 'workflow_run:run-7f3a', title: 'set_column_hidden', when: at(10, 45), by: 'workflow', rev: 55,
    deltas: [d('column:Store/Fax', 'IsHidden', 'False', 'True'), d('column:Store/Legacy Code', 'IsHidden', 'False', 'True')] },
  { id: 'ed-00000005', group: null, title: 'Renamed Total Sales to Sales Amount', when: at(10, 44), by: 'you', rev: 54,
    deltas: [d('measure:Sales/Total Sales', 'Name', 'Total Sales', 'Sales Amount'),
      d('measure:Sales/Margin %', 'Expression', 'DIVIDE([Margin], [Total Sales])', 'DIVIDE([Margin], [Sales Amount])'),
      d('measure:Sales/Sales YoY', 'Expression', '[Total Sales] - [Sales LY]', '[Sales Amount] - [Sales LY]')] },
  { id: 'ed-00000006', group: 'plan_item:describe/customer', title: 'set_description', when: at(10, 41), by: 'assistant', rev: 53,
    deltas: [d('column:Customer/Tier', 'Description', null, 'Loyalty tier: Bronze, Silver or Gold.')] },
  { id: 'ed-00000007', group: 'plan_item:describe/customer', title: 'set_description', when: at(10, 41), by: 'assistant', rev: 52,
    deltas: [d('column:Customer/City', 'Description', null, 'City from the billing address.')] },
  { id: 'ed-00000008', group: 'plan_item:describe/customer', title: 'set_description', when: at(10, 41), by: 'assistant', rev: 51,
    deltas: [d('column:Customer/Region', 'Description', null, 'Sales region that owns the customer.')] },
  { id: 'ed-00000009', group: 'plan_item:describe/customer', title: 'set_description', when: at(10, 41), by: 'assistant', rev: 50,
    deltas: [d('column:Customer/Segment', 'Description', null, 'Consumer, Corporate or Home office.')] },
  { id: 'ed-0000000a', group: null, title: 'set_summarize_by', when: at(10, 22), by: 'you', rev: 49,
    deltas: [d('column:Sales/Quantity', 'SummarizeBy', 'None', 'Sum')] },
];

const GROUPS = {
  'plan_item:safefix/promotion-keys': { kind: 'item', cause: { kind: 'plan_item', ids: ['safefix/promotion-keys'], title: 'Hide 3 Promotion key columns', approvedBy: null } },
  'workflow_run:run-7f3a': { kind: 'run', cause: { kind: 'workflow_run', ids: ['run-7f3a'], title: 'Monthly tidy, September', approvedBy: null },
    report: { runId: 'run-7f3a', workflow: 'monthly-tidy', title: 'Monthly tidy, September', status: 'completed',
      steps: [
        { stepId: 'scan', title: 'Scan the model', outcome: 'passed', submittedBy: 'semanticus', checks: [] },
        { stepId: 'sort', title: 'Sort months by number', outcome: 'passed', submittedBy: 'semanticus', checks: [{ kind: 'dax_probe', verdict: 'passed', detail: null }] },
        { stepId: 'formats', title: 'Format 4 measures', outcome: 'passed', submittedBy: 'assistant', checks: [] },
        { stepId: 'hide', title: 'Hide unused Store columns', outcome: 'passed', submittedBy: 'semanticus', checks: [] },
        { stepId: 'rescan', title: 'Scan again', outcome: 'passed', submittedBy: 'semanticus', checks: [{ kind: 'bpa_clean', verdict: 'passed', detail: null }] },
        { stepId: 'note', title: 'Write the change note', outcome: 'skipped', submittedBy: 'you', checks: [] },
        { stepId: 'done', title: 'Finish', outcome: 'passed', submittedBy: 'semanticus', checks: [] },
      ], evidence: '{"kind":"workflow_evidence","runId":"run-7f3a"}' } },
  'plan_item:describe/customer': { kind: 'item', cause: { kind: 'plan_item', ids: ['describe/customer'], title: 'Describe 4 Customer columns', approvedBy: 'you' } },
};

// Earlier session: a plan item and a saved version, both below the seam and never undoable.
const OLD_ROWS = () => [
  { id: 'plan_item:ai/instructions', kind: 'item', when: yesterday(16, 10), by: 'assistant', title: 'Add AI instructions',
    cause: { kind: 'plan_item', ids: ['ai/instructions'], title: 'Add AI instructions', approvedBy: null },
    deltas: [d('model:Model', 'AiInstructions', null, 'Answer in the sales team\'s words. Say when a question is outside this model.'),
      d('measure:Sales/Sales Amount', 'Synonyms', null, 'revenue, turnover, sales')],
    sessionId: SESSION_OLD, revision: 31, undoable: false, laterCount: 0, redoable: false, undone: false, verified: 'proven',
    beforeThisSession: true, beforeRestore: false, detail: null, report: null,
    children: [
      { id: 'ed-000000f1', kind: 'edit', when: yesterday(16, 10), by: 'assistant', title: 'set_ai_instructions', cause: null,
        deltas: [d('model:Model', 'AiInstructions', null, 'Answer in the sales team\'s words. Say when a question is outside this model.')],
        sessionId: SESSION_OLD, revision: 31, undoable: false, laterCount: 0, redoable: false, undone: false, verified: 'proven',
        beforeThisSession: true, beforeRestore: false, detail: null, report: null, children: null },
      { id: 'ed-000000f0', kind: 'edit', when: yesterday(16, 9), by: 'assistant', title: 'set_synonyms', cause: null,
        deltas: [d('measure:Sales/Sales Amount', 'Synonyms', null, 'revenue, turnover, sales')],
        sessionId: SESSION_OLD, revision: 30, undoable: false, laterCount: 0, redoable: false, undone: false, verified: null,
        beforeThisSession: true, beforeRestore: false, detail: null, report: null, children: null },
    ] },
  // An edit of an earlier session the file never held (the session ended before a save): not struck, because it was not taken back; it says it was not saved (join 8, K4).
  { id: 'ed-000000d0', kind: 'edit', when: yesterday(16, 0), by: 'you', title: 'set_description', cause: null,
    deltas: [d('measure:Sales/Sales Amount', 'Description', null, 'Total sales before tax.')],
    sessionId: SESSION_OLD, revision: 32, undoable: false, laterCount: 0, redoable: false, undone: false, verified: null, notSaved: true,
    beforeThisSession: true, beforeRestore: false, detail: null, report: null, children: null },
  { id: 'ed-000000e0', kind: 'saved', when: yesterday(15, 0), by: 'you', title: 'Saved a version: Friday handoff', cause: null, deltas: [],
    sessionId: SESSION_OLD, revision: 0, undoable: false, laterCount: 0, redoable: false, undone: false, verified: null,
    beforeThisSession: true, beforeRestore: false, detail: 'e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0', report: null, children: null },
];

const HASH_AI_PREP = 'a1b2c3d4e5f60718293a4b5c6d7e8f9012345678';
const HASH_FRIDAY = 'e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0e0';
const HASH_RESCUE = 'b7c8d9e0f1a2b3c4d5e6f708192a3b4c5d6e7f80';

/**
 * A fresh engine stand-in. Options:
 *   undone      how many of the newest edits start undone (the mockup's undo scene is 3)
 *   restored    start after a restore (the flag row, a rescue version, and a seam over everything older)
 *   broken      the verified record fails its hash check
 *   redCheck    add one publish that went ahead on a red check
 *   empty       a fresh model with no history at all
 *   noGit       checkpoints unsupported (the model is not in a git folder)
 */
export function makeHistoryEngine(options = {}) {
  const state = {
    undone: options.undone ?? 0,
    restored: !!options.restored,
    saved: [],   // versions saved in this session, newest first
    calls: [],
  };
  const sessionId = () => (state.restored ? SESSION_RESTORED : SESSION_NOW);

  function leafRow(leaf, index) {
    const mine = !state.restored;
    const undone = mine && index < state.undone;
    const undoable = mine && !undone;
    return {
      id: leaf.id, kind: 'edit', when: leaf.when, by: leaf.by, title: leaf.title,
      cause: leaf.group ? GROUPS[leaf.group].cause : null, deltas: leaf.deltas,
      sessionId: SESSION_NOW, revision: leaf.rev,
      undoable, laterCount: undoable ? index - state.undone : 0, redoable: undone, undone,
      verified: leaf.rev === 58 ? 'proven' : leaf.rev === 53 ? 'validated' : null,
      beforeThisSession: state.restored, beforeRestore: state.restored, detail: null, report: null, children: null,
    };
  }

  function currentRows() {
    if (options.empty) return [];
    const rows = [];
    const parents = new Map();
    LEAVES.forEach((leaf, index) => {
      const row = leafRow(leaf, index);
      if (!leaf.group) { rows.push(row); return; }
      let parent = parents.get(leaf.group);
      if (!parent) {
        const g = GROUPS[leaf.group];
        parent = { id: leaf.group, kind: g.kind, when: leaf.when, by: leaf.by, title: g.cause.title, cause: g.cause, deltas: [],
          sessionId: SESSION_NOW, revision: leaf.rev, undoable: false, laterCount: 0, redoable: false, undone: false, verified: null,
          beforeThisSession: state.restored, beforeRestore: state.restored, detail: null, report: g.report ?? null, children: [] };
        parents.set(leaf.group, parent);
        rows.push(parent);
      }
      parent.children.push(row);
    });
    for (const parent of parents.values()) {
      const kids = parent.children;
      const firstUndoable = [...kids].reverse().find((k) => k.undoable);
      parent.undoable = !!firstUndoable;
      parent.laterCount = firstUndoable ? firstUndoable.laterCount : 0;
      parent.redoable = kids.some((k) => k.redoable);
      parent.undone = kids.every((k) => k.undone);
      parent.verified = kids.map((k) => k.verified).find((v) => v) ?? null;
      parent.deltas = kids.flatMap((k) => k.deltas);
    }
    rows.push({ id: 'ed-000000c1', kind: 'saved', when: at(10, 30), by: 'you', title: 'Saved a version: Before AI prep', cause: null, deltas: [],
      sessionId: SESSION_NOW, revision: 0, undoable: false, laterCount: 0, redoable: false, undone: false, verified: null,
      beforeThisSession: state.restored, beforeRestore: state.restored, detail: HASH_AI_PREP, report: null, children: null });
    const old = OLD_ROWS();
    if (state.restored) old.forEach((r) => { r.beforeRestore = true; });
    rows.push(...old);
    if (state.restored) {
      rows.push({ id: 'ed-000000r1', kind: 'restored', when: at(10, 58), by: 'you', title: 'Restored Before AI prep', cause: null, deltas: [],
        sessionId: SESSION_RESTORED, revision: 0, undoable: false, laterCount: 0, redoable: false, undone: false, verified: null,
        beforeThisSession: false, beforeRestore: false, detail: 'restored a1b2c3d; the state before it is saved as b7c8d9e', report: null, children: null });
    }
    for (const s of state.saved) rows.push(s);
    return rows.sort((a, b) => Date.parse(b.when) - Date.parse(a.when));
  }

  const touches = (row, ref) => (row.deltas || []).some((x) => x.ref.toLowerCase() === ref.toLowerCase());

  // What the engine folds into its History (LocalEngine.History.cs, contract 8.8), played here: a verified record
  // with no edit to sit on, a version the ledger never flagged (the rescue a restore makes first), and a Verified word
  // past a broken record read as "broken". The restored-state commit is left out: the Restored row says it.
  const PUBLISHES = new Set(['deploy_live', 'deploy_stage']);
  const whoOfOrigin = (o) => (o === 'agent' ? 'assistant' : o === 'system' ? 'semanticus' : 'you');
  function engineExtras(rows) {
    const current = sessionId();
    const all = [...rows, ...rows.flatMap((r) => r.children ?? [])];
    const edits = new Set(all.filter((r) => r.sessionId && r.revision).map((r) => r.sessionId + '|' + r.revision));
    const flagged = new Set(all.filter((r) => r.kind === 'saved' && r.detail).map((r) => r.detail));
    const restoredAt = all.filter((r) => r.kind === 'restored').map((r) => Date.parse(r.when)).reduce((a, b) => Math.max(a, b), -Infinity);
    const mine = all.filter((r) => r.sessionId === current && !r.beforeThisSession).map((r) => Date.parse(r.when));
    const sessionStart = mine.length ? Math.min(...mine) : null;
    const olderEnd = all.filter((r) => r.beforeThisSession).map((r) => Date.parse(r.when)).reduce((a, b) => Math.max(a, b), -Infinity);
    const chain = listVerifiedEdits();
    const out = [...rows];
    for (const record of chain.records) {
      if (record.revision > 0 && edits.has(record.sessionId + '|' + record.revision)) continue;
      const red = !!record.overrideReason && PUBLISHES.has(record.op);
      out.push({ id: 'record:' + record.seq, kind: 'record', when: record.when, by: whoOfOrigin(record.origin), title: record.op,
        cause: null, deltas: record.objectRef ? [{ kind: 'check', ref: record.objectRef }] : [], sessionId: record.sessionId,
        revision: record.revision, undoable: false, laterCount: 0, redoable: false, undone: false, verified: record.verdict,
        beforeThisSession: record.sessionId !== current, beforeRestore: Date.parse(record.when) < restoredAt,
        detail: red ? record.overrideReason : record.summary ?? null, report: null, children: null, record });
    }
    const versions = listHistoryCheckpoints();
    for (const cp of versions.supported ? versions.checkpoints : []) {
      if (flagged.has(cp.hash) || /^Restored to /.test(cp.label)) continue;
      const t = Date.parse(cp.when);
      const rescue = /^Before restore to /.test(cp.label);
      out.push({ id: 'checkpoint:' + cp.hash, kind: 'saved', when: cp.when, by: rescue ? 'semanticus' : 'you',
        title: 'Saved a version: ' + (rescue ? 'Before the restore' : cp.label), cause: null, deltas: [], sessionId: cp.sessionId ?? null,
        revision: 0, undoable: false, laterCount: 0, redoable: false, undone: false, verified: null,
        beforeThisSession: cp.sessionId ? cp.sessionId !== current : sessionStart != null ? t < sessionStart : t <= olderEnd,
        beforeRestore: t < restoredAt, detail: cp.hash, report: null, children: null });
    }
    if (!chain.chainIntact) {
      const recordAt = new Map(chain.records.map((r) => [r.sessionId + '|' + r.revision, r]));
      const broken = (r) => chain.firstBrokenSeq <= 0 || (r != null && r.seq >= chain.firstBrokenSeq);
      const distrust = (row) => {
        for (const c of row.children ?? []) distrust(c);
        if (!row.verified) return;
        const rec = row.record ?? recordAt.get(row.sessionId + '|' + row.revision);
        if (broken(rec) || (row.children ?? []).some((c) => c.verified === 'broken')) row.verified = 'broken';
      };
      out.forEach(distrust);
    }
    return out.sort((a, b) => Date.parse(b.when) - Date.parse(a.when));
  }

  function getPlanHistory(by, objectRef, before, limit) {
    let rows = engineExtras(currentRows());
    if (by) rows = rows.filter((r) => r.by === by);
    if (objectRef) rows = rows.filter((r) => touches(r, objectRef)).map((r) => (r.children ? { ...r, children: r.children.filter((c) => touches(c, objectRef)) } : r));
    if (before) rows = rows.filter((r) => Date.parse(r.when) < Date.parse(before));
    const take = !limit || limit <= 0 ? 50 : Math.min(limit, 500);
    return { sessionId: sessionId(), rows: rows.slice(0, take), more: rows.length > take,
      seamSentence: 'Before this session. Undo is not available for these.', kept: true };
  }

  function stackIndexOf(entry) {
    const leaf = LEAVES.findIndex((l) => l.id === entry);
    if (leaf >= 0) return { first: leaf, last: leaf };
    const kids = LEAVES.map((l, i) => (l.group === entry ? i : -1)).filter((i) => i >= 0);
    if (kids.length === 0) return null;
    return { first: Math.min(...kids), last: Math.max(...kids) };
  }

  function undo(origin, toEntry) {
    state.calls.push({ call: 'undo', params: [origin, toEntry ?? null] });
    const pos = toEntry ? stackIndexOf(toEntry) : { first: state.undone, last: state.undone };
    if (!pos || state.restored || pos.last < state.undone) {
      return { __uishotError: 'That change is from before this session. Restore a saved version instead.' };
    }
    state.undone = pos.last + 1;
    return { canUndo: state.undone < LEAVES.length, canRedo: state.undone > 0, atCheckpoint: false };
  }

  function redo(origin, toEntry) {
    state.calls.push({ call: 'redo', params: [origin, toEntry ?? null] });
    const pos = toEntry ? stackIndexOf(toEntry) : { first: state.undone - 1, last: state.undone - 1 };
    if (!pos || pos.first >= state.undone) return { __uishotError: 'Nothing to redo there.' };
    state.undone = pos.first;
    return { canUndo: true, canRedo: state.undone > 0, atCheckpoint: false };
  }

  function checkpointOf(hash) {
    if (hash === HASH_AI_PREP) return { hash, shortHash: 'a1b2c3d', label: 'Before AI prep', author: 'you', when: at(10, 30), modelPath: 'model', revision: 48 };
    if (hash === HASH_FRIDAY) return { hash, shortHash: 'e0e0e0e', label: 'Friday handoff', author: 'you', when: yesterday(15, 0), modelPath: 'model', revision: 29 };
    if (hash === HASH_RESCUE) return { hash, shortHash: 'b7c8d9e', label: 'Before restore to a1b2c3d', author: 'you', when: at(10, 58), modelPath: 'model', revision: 0 };
    const mine = state.saved.find((s) => s.detail === hash);
    return mine ? { hash, shortHash: hash.slice(0, 7), label: mine.title.replace('Saved a version: ', ''), author: 'you', when: mine.when, modelPath: 'model', revision: 0 } : null;
  }

  function listHistoryCheckpoints() {
    if (options.noGit) return { supported: false, modelDirty: false, checkpoints: [], ownedPaths: [], note: 'This model is not in a git folder.' };
    const hashes = [...state.saved.map((s) => s.detail), ...(state.restored ? [HASH_RESCUE] : []), ...(options.empty ? [] : [HASH_AI_PREP, HASH_FRIDAY])];
    return { supported: true, branch: 'main', modelDirty: false, checkpoints: hashes.map(checkpointOf), ownedPaths: ['model'] };
  }

  function createHistoryCheckpoint(label, commit) {
    state.calls.push({ call: 'createHistoryCheckpoint', params: [label, commit] });
    if (!commit) return { preview: true, committed: false, files: [], savedModelFirst: false, note: 'Preview only.' };
    const hash = ('c' + String(state.saved.length + 1)).padEnd(40, '0');
    const name = label || 'Version ' + (state.saved.length + 1);
    state.saved.unshift({ id: 'ed-0000s' + state.saved.length, kind: 'saved', when: at(10, 49), by: 'you', title: 'Saved a version: ' + name, cause: null, deltas: [],
      sessionId: sessionId(), revision: 0, undoable: false, laterCount: 0, redoable: false, undone: false, verified: null,
      beforeThisSession: false, beforeRestore: false, detail: hash, report: null, children: null });
    return { preview: false, committed: true, checkpoint: checkpointOf(hash), files: ['model'], savedModelFirst: false };
  }

  function restoreHistoryCheckpoint(hash, restore) {
    state.calls.push({ call: 'restoreHistoryCheckpoint', params: [hash, restore] });
    const target = checkpointOf(hash);
    if (!target) return { preview: !restore, restored: false, paths: [], error: 'Checkpoint \'' + hash + '\' is not in this model\'s current branch history.' };
    if (!restore) return { preview: true, restored: false, target, paths: ['model'], note: 'Preview only.' };
    state.restored = true;
    state.undone = 0;
    // commitFails: the files were restored and reopened but the checkpoint commit after them failed. The engine
    // answers restored AND an error together (LocalEngine.HistoryCheckpoints.cs), and says no note.
    if (options.commitFails) return { preview: false, restored: true, target, rescueCheckpoint: checkpointOf(HASH_RESCUE), paths: ['model'],
      error: 'The model files were restored and reopened, but the restored state was not committed: git commit failed.' };
    return { preview: false, restored: true, target, rescueCheckpoint: checkpointOf(HASH_RESCUE), paths: ['model'], note: 'Restored.' };
  }

  function listVerifiedEdits() {
    if (options.empty) return { records: [], chainIntact: true, firstBrokenSeq: 0 };
    const records = [
      { seq: 38, when: yesterday(16, 10), sessionId: SESSION_OLD, revision: 31, origin: 'agent', op: 'apply_plan', verdict: 'proven',
        summary: 'The check passed: 12 of 12 test questions answered right.', bodyHash: 'aa11', prevHash: '0000', hash: 'bb22' },
      { seq: 40, when: at(10, 41), sessionId: SESSION_NOW, revision: 53, origin: 'agent', op: 'apply_plan', verdict: 'validated',
        summary: 'The check passed: all 4 columns now have a description.', bodyHash: 'cc33', prevHash: 'bb22', hash: 'dd44' },
      { seq: 41, when: at(10, 46), sessionId: SESSION_NOW, revision: 58, origin: 'system', op: 'apply_plan', verdict: 'proven',
        summary: 'The check passed: no report uses these 3 columns.', bodyHash: 'ee55', prevHash: 'dd44', hash: 'ff66' },
    ];
    if (options.redCheck) {
      records.push({ seq: 42, when: at(10, 47), sessionId: SESSION_NOW, revision: 0, origin: 'human', op: 'deploy_live', verdict: 'overridden',
        summary: 'Red safety check (2 blocking problems: Sales[Discount] has no format; Sales[Net] is hidden and unused). Published anyway with a written reason, to powerbi://api.powerbi.com/v1.0/myorg/Contoso Sales',
        overrideReason: 'The board pack goes out at noon and these two are cosmetic.', bodyHash: '1a1a', prevHash: 'ff66', hash: '2b2b' });
    }
    return {
      records, chainIntact: !options.broken, firstBrokenSeq: options.broken ? 40 : 0,
      note: options.broken ? 'Integrity broken at record #40: a record was edited or removed after it was written.' : undefined,
    };
  }

  function exportVerifiedEdits(format) {
    state.calls.push({ call: 'exportVerifiedEdits', params: [format] });
    return format === 'json' ? '{"records":[]}' : '# Verified edits\n';
  }

  return { state, getPlanHistory, undo, redo, listHistoryCheckpoints, createHistoryCheckpoint, restoreHistoryCheckpoint, listVerifiedEdits, exportVerifiedEdits };
}
