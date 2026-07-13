import { useEffect, useState } from 'react';
import { rpc, onDidChange, onActivity, copyText } from './bridge';
import { KIND_GLYPH, KIND_COLOR, resolveColor } from './lineagetypes';
import { Evidence, useRichEvidence, matchRich } from './evidence';
import { useTier, isEntitlementError, ProBadge } from './pro';

// ===================================================================================================
// Edit History — the co-authoring timeline. Every model change this session, by YOU (the VS Code UI) or the
// AI ASSISTANT (over MCP), on one shared, undoable timeline. This is the premium home for the dual-drive story:
// who changed what, when, and a one-click step back. Data is the model/didChange edit stream (collected at the
// App root so the full session is captured); undo/redo drive the engine's single shared undo timeline.
//
// Per-edit undo: the timeline is a LINEAR stack (TE2's UndoManager), so "undo just one middle edit" isn't a thing.
// What we offer instead is "Undo to here" — click any edit to roll back that edit AND everything after it (the
// Photoshop-history model), implemented by driving the engine's one-step undo N times. `undoneCount` (owned by App,
// driven by the undo/redo broadcasts) is how many of the NEWEST entries are currently rolled back: items[0..undoneCount-1]
// render as "undone" (dim), and a fresh edit clears that redo branch. Redo walks back the other way.
// ===================================================================================================

export interface EditEntry { revision: number; sessionId?: string; origin: string; label?: string; count: number; refs: string[]; ts: number }
interface UndoState { canUndo: boolean; canRedo: boolean; atCheckpoint: boolean }

// ---- Verified Edits (the Pro audit layer) --------------------------------------------------------
// The persistent, append-only "Verified Edits" trail the engine welds ONTO the model (it survives reload; the live
// timeline above does not). Every mutating op is hash-chained (bodyHash + prevHash → hash) so the record can prove it
// wasn't edited or removed after the fact — Semanticus as the independent referee of what the AI (or you) actually did.
// Wire shapes mirror the engine door's VerifiedEditsChain / VerifiedEditRecord (camelCase over the JSON-RPC, like every
// other DTO). A record welds to a live timeline entry when sessionId === current session AND revision === entry.revision.
export interface VerifiedEditRecord {
  seq: number; when: string; sessionId: string; revision: number; origin: string; op: string; objectRef?: string;
  verdict: string; summary?: string; overrideReason?: string; evidence?: string; bodyHash: string; prevHash: string; hash: string;
}
export interface VerifiedEditsChain { records: VerifiedEditRecord[]; chainIntact: boolean; firstBrokenSeq: number; note?: string }
interface HistoryCheckpoint { hash: string; shortHash: string; label: string; author: string; when: string; modelPath: string; sessionId?: string; revision: number; auditHead?: string }
interface HistoryCheckpointList { supported: boolean; branch?: string; modelDirty: boolean; checkpoints: HistoryCheckpoint[]; ownedPaths: string[]; note?: string }
interface HistoryCheckpointResult { preview: boolean; committed: boolean; checkpoint?: HistoryCheckpoint; files: string[]; savedModelFirst: boolean; note?: string; error?: string }
interface HistoryRestoreResult { preview: boolean; restored: boolean; target?: HistoryCheckpoint; rescueCheckpoint?: HistoryCheckpoint; restoredCheckpoint?: HistoryCheckpoint; paths: string[]; note?: string; error?: string }
interface RestorePointRecord { id: string; endpoint: string; database: string; capturedUtc: string; bimPath?: string | null; op?: string; reason?: string; pushed: string[]; deleted: string[] }

// Verdict → chip styling. Quiet, tinted-not-loud, on the tab's existing palette: proven/deployed lean good, validated
// is the accent, needs-review/overridden warn (override carries the reason), batch/info stay neutral. Unknown verdicts
// fall through to neutral so a new engine verdict never crashes the row.
const VERDICT_STYLE: Record<string, { label: string; color: string }> = {
  proven: { label: 'proven', color: 'var(--sem-good)' },
  validated: { label: 'validated', color: 'var(--sem-accent)' },
  'needs-review': { label: 'needs review', color: 'var(--sem-warn)' },
  overridden: { label: 'overridden', color: 'var(--sem-bad)' },
  deployed: { label: 'deployed', color: 'var(--sem-good)' },
  safe: { label: 'safe', color: 'var(--sem-good)' },        // compare_baseline: nothing downstream moved
  impact: { label: 'impact', color: 'var(--sem-bad)' },     // compare_baseline: numbers moved / measures missing
  batch: { label: 'batch', color: 'var(--sem-muted)' },
  info: { label: 'info', color: 'var(--sem-muted)' },
  // "What moved this number?" (feature #3) — plain labels, per the UX bar (no "blame"/"interval" words).
  attributed: { label: 'cause found', color: 'var(--sem-good)' },
  interval: { label: 'candidates ranked', color: 'var(--sem-warn)' },
  'data-suspected': { label: 'data change?', color: 'var(--sem-warn)' },
  inconclusive: { label: 'no answer yet', color: 'var(--sem-muted)' },
};
const verdictStyle = (v: string) => VERDICT_STYLE[v] ?? { label: v || 'info', color: 'var(--sem-muted)' };

// The audit rail's node glyph — one character per verdict so the circle itself tells the story at a glance
// (✓ pass-family, ! attention-family, ? needs eyes, ↑ shipped, ≡ a batch). Unknown verdicts get a neutral dot.
const VERDICT_GLYPH: Record<string, string> = {
  proven: '✓', safe: '✓', validated: '✓', deployed: '↑', overridden: '!', impact: '!', 'needs-review': '?', batch: '≡', info: 'i',
  attributed: '✓', interval: '?', 'data-suspected': '!', inconclusive: 'i',
};

// Ops whose raw id would leak engine language into the UI — rendered with a plain-English label instead
// (the raw op id stays available in the record's Details/raw JSON for anyone who needs it).
const OP_LABEL: Record<string, string> = {
  blame_value: 'What moved this number?',
};
const opLabel = (op: string) => OP_LABEL[op] ?? op;

// When several records share one revision (apply_plan: per-item verdicts + a "batch" summary), the badge that welds to
// the timeline row must be the most SIGNIFICANT one — a red "overridden" or amber "needs-review" must never be hidden by
// the neutral "batch". Higher number = higher priority; an unknown verdict sits just above the neutral floor.
const VERDICT_PRIORITY: Record<string, number> = {
  overridden: 6, impact: 6, 'needs-review': 5, proven: 4, safe: 4, validated: 3, deployed: 2, batch: 1, info: 0,
  attributed: 4, interval: 3, 'data-suspected': 3, inconclusive: 0,
};
const verdictPriority = (v: string) => VERDICT_PRIORITY[v] ?? 0.5;

const isAi = (origin: string) => origin === 'agent';
const actorLabel = (origin: string) => (origin === 'agent' ? 'AI Assistant' : origin === 'system' ? 'System' : 'You');

function ago(ts: number, now: number): string {
  const s = Math.max(0, Math.floor((now - ts) / 1000));
  if (s < 5) return 'just now';
  if (s < 60) return s + 's ago';
  const m = Math.floor(s / 60); if (m < 60) return m + 'm ago';
  const h = Math.floor(m / 60); if (h < 24) return h + 'h ago';
  return Math.floor(h / 24) + 'd ago';
}

// A short, absolute-ish label for a record's ISO-UTC `when` (the audit trail is persistent, so a live "just now" would
// mislead across reloads): "just now" / "5m ago" while recent, then a fixed local date-time once it's older than a day.
function shortWhen(iso: string, now: number): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return iso;
  const s = Math.max(0, Math.floor((now - t) / 1000));
  if (s < 60) return 'just now';
  const m = Math.floor(s / 60); if (m < 60) return m + 'm ago';
  const h = Math.floor(m / 60); if (h < 24) return h + 'h ago';
  return new Date(t).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

// Parse an ObjectRefs ref ('measure:Sales/Total Sales') into a glyph + name, for the per-change object chips.
function refLabel(ref: string): { kind: string; name: string } {
  const colon = ref.indexOf(':');
  const kind = colon > 0 ? ref.slice(0, colon) : 'unresolved';
  const rest = colon > 0 ? ref.slice(colon + 1) : ref;
  const slash = rest.lastIndexOf('/');
  return { kind, name: slash > 0 ? rest.slice(slash + 1) : rest };
}

export function HistoryView({ items, undoneCount, sessionId, onOpenRollback }: {
  items: EditEntry[]; undoneCount: number; sessionId?: string;
  onOpenRollback?: (point: RestorePointRecord) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const [chain, setChain] = useState<VerifiedEditsChain | null>(null);
  // "Show this edit" (the What-moved-this-number panel's one-click jump): scroll the timeline row for a
  // revision into view and flash it — the undo affordance already lives on that row.
  const [flashRev, setFlashRev] = useState<number | null>(null);
  // Rich op results off the live activity stream — full evidence (candidate DAX, mismatch grids,
  // benchmarks) for records written this session; older records degrade to their persisted digest.
  const richHits = useRichEvidence();

  // Tick the relative timestamps so "just now" ages without an interaction.
  useEffect(() => { const t = window.setInterval(() => setNow(Date.now()), 30000); return () => window.clearInterval(t); }, []);

  // Pull the persistent Verified Edits chain on mount and on every model change (throttled ~500ms so a burst of deltas
  // is one fetch). Failures are swallowed to empty — no model, a pre-Pro engine, or an older door that lacks the method
  // must all read as "nothing verified" (silence, never a scary error banner). onDidChange gives us the live re-pull.
  // But deploy overrides/outcomes MUTATE NOTHING, so they fire no didChange — the trail would go stale while the tab is
  // open. The engine now emits an ActivityEvent {kind:'verified_edit'} after every audit append; subscribe to that too
  // and re-pull on the same throttle, so a non-mutating verified edit still refreshes the trail live.
  useEffect(() => {
    let alive = true;
    let timer: number | undefined;
    const pull = () => { rpc<VerifiedEditsChain>('listVerifiedEdits').then((c) => { if (alive) setChain(c); }).catch(() => { if (alive) setChain(null); }); };
    const schedule = () => { window.clearTimeout(timer); timer = window.setTimeout(pull, 500); };
    pull();
    const offC = onDidChange(schedule);
    const offA = onActivity((e) => { if (e.kind === 'verified_edit') schedule(); });
    return () => { alive = false; window.clearTimeout(timer); offC(); offA(); };
  }, []);

  // Weld records to live timeline entries: a record proves an entry when it's on THIS session and the same revision.
  // Two hazards the naive last-wins Map got wrong:
  //  (a) revision 0 = a record that mutated nothing (deploy outcomes, "kept-the-original" optimize verdicts — the engine
  //      stamps these 0). No timeline row has revision 0, so these must NEVER weld — skip them (they live in the audit
  //      trail only).
  //  (b) MANY records can share one real revision — apply_plan writes per-item "overridden"/"needs-review" records AND a
  //      neutral "batch" record at the same rev. Last-wins let "batch" hide a red "overridden". So on a collision keep the
  //      HIGHER-priority verdict (a warning/override must win over a neutral batch/info badge).
  const recordByRevision = new Map<number, VerifiedEditRecord>();
  for (const r of chain?.records ?? []) {
    if (!sessionId || r.sessionId !== sessionId) continue;
    if (r.revision <= 0) continue;                                  // (a) non-mutating record — never badges a row
    const prev = recordByRevision.get(r.revision);
    if (!prev || verdictPriority(r.verdict) > verdictPriority(prev.verdict)) recordByRevision.set(r.revision, r);  // (b)
  }

  const live = items.length - undoneCount;      // entries currently in effect (not rolled back)
  const canUndo = live > 0;
  const canRedo = undoneCount > 0;

  // Drive the engine's one-step undo/redo `times` times (the timeline is linear, so a roll-back to a point is just
  // N single steps). undoneCount updates from the broadcasts as each step lands, which also corrects the buttons.
  const run = async (dir: 'undo' | 'redo', times: number) => {
    if (times <= 0 || busy) return;
    setBusy(true); setMsg(null);
    try {
      for (let k = 0; k < times; k++) {
        const s = await rpc<UndoState>(dir, 'human');
        if (dir === 'undo' && !s.canUndo) break;
        if (dir === 'redo' && !s.canRedo) break;
      }
    } catch (e) { setMsg(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };
  // "Undo to here" on a LIVE entry at index i → roll back it + everything newer (i + 1 − undoneCount steps).
  const undoTo = (i: number) => run('undo', i + 1 - undoneCount);
  // "Redo to here" on an UNDONE entry at index i → restore it + the older undone edits up to it (undoneCount − i steps).
  const redoTo = (i: number) => run('redo', undoneCount - i);

  const jumpToRevision = (rev: number) => {
    const el = document.getElementById('edit-rev-' + rev);
    if (!el) return;
    el.scrollIntoView({ behavior: 'smooth', block: 'center' });
    setFlashRev(rev);
    window.setTimeout(() => setFlashRev((f) => (f === rev ? null : f)), 1800);
  };

  const liveItems = items.slice(undoneCount);
  const aiCount = liveItems.filter((i) => isAi(i.origin)).length;
  const youCount = liveItems.length - aiCount;

  return (
    <div className="sem-evidence-page sem-centered-page flex flex-col gap-4 min-w-0">
      <Panel>
        <div className="flex items-center gap-4">
          <div className="flex flex-col items-center justify-center w-16 h-16 rounded-2xl shrink-0" style={{ background: 'var(--sem-surface-2)', boxShadow: 'inset 0 0 0 2px var(--sem-accent)' }}>
            <div className="text-2xl font-bold tnum" style={{ color: 'var(--sem-fg)' }}>{live}</div>
            <div className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>edits</div>
          </div>
          <div className="flex-1 min-w-0">
            <div className="text-[15px] font-semibold">Edit History</div>
            <div className="text-[12px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>
              Every change this session, yours and the AI Assistant’s, on one shared, undoable timeline.
            </div>
            <div className="flex items-center gap-3 mt-2">
              <Legend kind="you" n={youCount} />
              <Legend kind="ai" n={aiCount} />
              {undoneCount > 0 && <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>· {undoneCount} undone</span>}
            </div>
          </div>
          <div className="flex items-center gap-2 shrink-0">
            <Button onClick={() => run('undo', 1)} disabled={busy || !canUndo}>{busy ? 'Working…' : 'Undo last'}</Button>
            <Button onClick={() => run('redo', 1)} disabled={busy || !canRedo}>Redo</Button>
          </div>
        </div>
        {msg && <div className="text-[11px] mt-2" style={{ color: 'var(--sem-muted)' }}>{msg}</div>}
        <div className="text-[10.5px] mt-2" style={{ color: 'var(--sem-muted)' }}>
          Tip: hover any edit and choose <span style={{ color: 'var(--sem-fg)' }}>Undo to here</span> to roll back that edit and everything after it.
        </div>
      </Panel>

      <RecoveryPanel sessionId={sessionId} onOpenRollback={onOpenRollback} />

      {items.length === 0 ? (
        <Panel>
          <div className="text-[13px]" style={{ color: 'var(--sem-fg)' }}>No edits yet this session.</div>
          <div className="text-[12px] mt-1" style={{ color: 'var(--sem-muted)' }}>
            Changes you make in Studio and changes the AI Assistant makes both land here: live, attributed, and
            reversible from one place. Try “Apply safe fixes”, or ask the AI Assistant to improve the model.
          </div>
        </Panel>
      ) : (
        <Panel>
          <div className="relative">
            {/* vertical timeline rail */}
            <div className="absolute top-1 bottom-1 w-px" style={{ left: 15, background: 'var(--sem-border)' }} />
            <div className="flex flex-col">
              {items.map((it, i) => (
                <Entry key={it.revision + ':' + i} it={it} now={now} latest={i === undoneCount}
                  undone={i < undoneCount} busy={busy} flash={flashRev === it.revision}
                  record={it.sessionId === sessionId ? recordByRevision.get(it.revision) : undefined}
                  revertCount={i < undoneCount ? undoneCount - i : i + 1 - undoneCount}
                  onAction={() => (i < undoneCount ? redoTo(i) : undoTo(i))} />
              ))}
            </div>
          </div>
        </Panel>
      )}

      {/* render on records OR a broken chain: an unparseable blob is zero records + intact=false, and THAT
          state must show the loud warning — the one failure the audit section can never render as silence. */}
      {chain && (chain.records.length > 0 || !chain.chainIntact) && (
        <AuditTrail chain={chain} now={now} richHits={richHits} sessionId={sessionId} onJump={jumpToRevision} />
      )}
    </div>
  );
}

// Durable recovery belongs beside the session timeline, not in another source-control screen. The two columns keep
// the boundary explicit: local FILE history is git; a PUBLISHED target is restored from its pre-write metadata points.
function RecoveryPanel({ sessionId, onOpenRollback }: { sessionId?: string; onOpenRollback?: (point: RestorePointRecord) => void }) {
  const [local, setLocal] = useState<HistoryCheckpointList | null>(null);
  const [published, setPublished] = useState<RestorePointRecord[]>([]);
  const [label, setLabel] = useState('');
  const [createPreview, setCreatePreview] = useState<HistoryCheckpointResult | null>(null);
  const [restorePreview, setRestorePreview] = useState<HistoryRestoreResult | null>(null);
  const [result, setResult] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = () => Promise.all([
    rpc<HistoryCheckpointList>('listHistoryCheckpoints', 50).then(setLocal).catch(() => setLocal(null)),
    rpc<RestorePointRecord[]>('listRestorePoints', null, null).then((x) => setPublished(x ?? [])).catch(() => setPublished([])),
  ]);
  useEffect(() => { setCreatePreview(null); setRestorePreview(null); setResult(null); setError(null); void load(); }, [sessionId]); // eslint-disable-line react-hooks/exhaustive-deps

  const previewCreate = async () => {
    setBusy('create-preview'); setError(null); setResult(null);
    try { const r = await rpc<HistoryCheckpointResult>('createHistoryCheckpoint', label.trim() || null, false, 'human'); setCreatePreview(r); if (r.error) setError(r.error); }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  };
  const confirmCreate = async () => {
    setBusy('create'); setError(null);
    try {
      const r = await rpc<HistoryCheckpointResult>('createHistoryCheckpoint', label.trim() || null, true, 'human');
      if (r.error) setError(r.error); else { setResult('Checkpoint ' + (r.checkpoint?.shortHash ?? '') + ' created.'); setLabel(''); setCreatePreview(null); await load(); }
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  };
  const previewRestore = async (hash: string) => {
    setBusy('restore-preview'); setError(null); setResult(null);
    try { const r = await rpc<HistoryRestoreResult>('restoreHistoryCheckpoint', hash, false, 'human'); setRestorePreview(r); if (r.error) setError(r.error); }
    catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  };
  const confirmRestore = async () => {
    if (!restorePreview?.target) return;
    setBusy('restore'); setError(null);
    try {
      const r = await rpc<HistoryRestoreResult>('restoreHistoryCheckpoint', restorePreview.target.hash, true, 'human');
      if (r.error) setError(r.error);
      else { setResult(r.note || 'Checkpoint restored.'); setRestorePreview(null); await load(); }
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  };

  return (
    <Panel>
      <div className="flex items-start gap-3">
        <div className="min-w-0 flex-1">
          <div className="text-[13px] font-semibold">Durable recovery</div>
          <div className="text-[11px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>Session Undo is immediate. These anchors still exist after the model or app closes.</div>
        </div>
        {local?.branch && <span className="text-[10px] tnum px-2 py-0.5 rounded" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>branch {local.branch}</span>}
      </div>
      {error && <div className="mt-2 rounded-md px-3 py-2 text-[11px]" style={{ color: 'var(--sem-bad)', border: '1px solid var(--sem-bad)' }}>{error}</div>}
      {result && <div className="mt-2 rounded-md px-3 py-2 text-[11px]" style={{ color: 'var(--sem-good)', border: '1px solid var(--sem-good)' }}>{result}</div>}
      <div className="grid grid-cols-1 xl:grid-cols-2 gap-3 mt-3">
        <section className="rounded-lg border p-3 min-w-0" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-bg)' }}>
          <div className="text-[10px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-accent)' }}>Local file · source control</div>
          <div className="text-[11px] mt-1" style={{ color: 'var(--sem-muted)' }}>A checkpoint commits only this model and its tracked Semanticus evidence. It never pushes.</div>
          {local?.supported ? (
            <>
              <div className="flex gap-2 mt-2">
                <input value={label} onChange={(e) => { setLabel(e.target.value); setCreatePreview(null); }} placeholder="Checkpoint label (optional)" spellCheck={false}
                  className="flex-1 min-w-0 text-[11px] px-2 py-1 rounded-md outline-none" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }} />
                <MiniButton onClick={previewCreate} disabled={busy != null}>{busy === 'create-preview' ? 'Checking…' : 'Preview checkpoint'}</MiniButton>
              </div>
              {createPreview && !createPreview.error && (
                <div className="mt-2 rounded-md p-2" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
                  <div className="text-[11px]">{createPreview.note}</div>
                  <div className="text-[10px] tnum mt-1" style={{ color: 'var(--sem-muted)' }}>{createPreview.files.length ? createPreview.files.join(' · ') : 'No file delta; this will mark the accepted state.'}</div>
                  <div className="flex gap-2 mt-2"><MiniButton onClick={confirmCreate} disabled={busy != null}>{busy === 'create' ? 'Creating…' : 'Create checkpoint'}</MiniButton><MiniButton onClick={() => setCreatePreview(null)}>Cancel</MiniButton></div>
                </div>
              )}
              <div className="mt-3 flex flex-col gap-1.5">
                {local.checkpoints.slice(0, 6).map((c) => (
                  <div key={c.hash} className="flex items-center gap-2 rounded-md px-2 py-1.5" style={{ background: 'var(--sem-surface-2)' }}>
                    <span className="text-[10px] tnum font-semibold" style={{ color: 'var(--sem-accent)' }}>{c.shortHash}</span>
                    <span className="text-[11px] truncate">{c.label}</span>
                    <span className="ml-auto text-[9.5px] shrink-0" style={{ color: 'var(--sem-muted)' }}>{shortWhen(c.when, Date.now())}</span>
                    <MiniButton onClick={() => previewRestore(c.hash)} disabled={busy != null}>Restore</MiniButton>
                  </div>
                ))}
                {local.checkpoints.length === 0 && <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{local.note}</div>}
              </div>
              {restorePreview?.target && !restorePreview.error && (
                <div className="mt-2 rounded-md p-2" style={{ border: '1px solid var(--sem-warn)', color: 'var(--sem-fg)' }}>
                  <div className="text-[11px] font-semibold">Restore {restorePreview.target.shortHash} · {restorePreview.target.label}</div>
                  <div className="text-[10px] mt-1" style={{ color: 'var(--sem-muted)' }}>{restorePreview.note}</div>
                  <div className="flex gap-2 mt-2"><MiniButton onClick={confirmRestore} disabled={busy != null}>{busy === 'restore' ? 'Restoring…' : 'Restore checkpoint'}</MiniButton><MiniButton onClick={() => setRestorePreview(null)}>Cancel</MiniButton></div>
                </div>
              )}
            </>
          ) : (
            <div className="text-[11px] mt-2" style={{ color: 'var(--sem-muted)' }}>{local?.note || 'This model has no local git checkpoint history. Session Undo still works until the model closes.'}</div>
          )}
        </section>
        <section className="rounded-lg border p-3 min-w-0" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-bg)' }}>
          <div className="text-[10px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-warn)' }}>Published model · pre-write restore points</div>
          <div className="text-[11px] mt-1" style={{ color: 'var(--sem-muted)' }}>These snapshots protect an XMLA write. Rollback happens against the named published target, never the local file.</div>
          <div className="mt-2 flex flex-col gap-1.5">
            {published.slice(0, 6).map((p) => (
              <div key={p.id} className="rounded-md px-2 py-1.5" style={{ background: 'var(--sem-surface-2)' }}>
                <div className="flex items-center gap-2">
                  <span className="text-[11px] font-medium truncate">{p.database || 'Published model'}</span>
                  <span className="ml-auto text-[9.5px] shrink-0" style={{ color: 'var(--sem-muted)' }}>{shortWhen(p.capturedUtc, Date.now())}</span>
                  {onOpenRollback && <MiniButton onClick={() => onOpenRollback(p)}>Open Roll back</MiniButton>}
                </div>
                <div className="text-[9.5px] tnum truncate mt-0.5" title={p.endpoint} style={{ color: 'var(--sem-muted)' }}>{p.id} · {p.endpoint}</div>
                {p.reason && <div className="text-[10px] truncate mt-0.5" style={{ color: 'var(--sem-muted)' }}>{p.reason}</div>}
              </div>
            ))}
            {published.length === 0 && <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>No published restore points on this machine. One is created before a guarded published write.</div>}
          </div>
        </section>
      </div>
    </Panel>
  );
}

function Entry({ it, now, latest, undone, busy, record, revertCount, onAction, flash }: {
  it: EditEntry; now: number; latest: boolean; undone: boolean; busy: boolean; record?: VerifiedEditRecord; revertCount: number; onAction: () => void; flash?: boolean;
}) {
  const ai = isAi(it.origin);
  const accent = resolveColor('var(--sem-accent)', '#2ED47A');
  return (
    <div id={'edit-rev-' + it.revision} className="group relative flex items-start gap-3 py-2.5 rounded-lg"
      style={{ opacity: undone ? 0.5 : 1,
        background: flash ? 'color-mix(in srgb, var(--sem-accent) 14%, transparent)' : undefined,
        transition: 'background 600ms ease' }}>
      {/* author node on the rail */}
      <div className="relative z-10 shrink-0 w-8 h-8 rounded-full flex items-center justify-center text-[10px] font-bold"
        style={ai
          ? { background: accent, color: 'var(--sem-on-accent)' }
          : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
        {ai ? 'AI' : 'You'}
      </div>
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="text-[12px] font-medium" style={{ color: ai ? accent : 'var(--sem-fg)' }}>{ai ? 'AI Assistant' : 'You'}</span>
          <span className="text-[12px]" style={{ color: 'var(--sem-fg)', textDecoration: undone ? 'line-through' : 'none' }}>{it.label || 'edited the model'}</span>
          {/* verdict badge — ONLY when a persistent Verified Edits record welds to this row. No record = no badge:
              silence is honest here (an un-verified edit isn't "unproven"; it simply wasn't checked). */}
          {record && <VerdictBadge verdict={record.verdict} title={record.overrideReason ? `Override: ${record.overrideReason}` : record.summary} />}
          {latest && !undone && <span className="text-[9px] uppercase tracking-wide px-1.5 py-0.5 rounded-full" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>latest</span>}
          {undone && <span className="text-[9px] uppercase tracking-wide px-1.5 py-0.5 rounded-full" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>undone</span>}
          {/* per-edit action — appears on row hover (always visible for the boundary entries) */}
          <button onClick={onAction} disabled={busy}
            title={undone
              ? `Redo to here: restore this${revertCount > 1 ? ` and ${revertCount - 1} more` : ''}`
              : `Undo to here: roll back this${revertCount > 1 ? ` and the ${revertCount - 1} edit${revertCount - 1 > 1 ? 's' : ''} after it` : ''}`}
            className="text-[10px] px-1.5 py-0.5 rounded transition-opacity opacity-0 group-hover:opacity-100 focus:opacity-100 disabled:opacity-30"
            style={{ background: 'var(--sem-surface-2)', color: undone ? 'var(--sem-accent)' : 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
            {undone ? 'Redo to here' : 'Undo to here'}{!undone && revertCount > 1 ? ` (${revertCount})` : ''}
          </button>
          <span className="ml-auto text-[10px] tnum shrink-0" style={{ color: 'var(--sem-muted)' }} title={`revision ${it.revision}`}>{ago(it.ts, now)}</span>
        </div>
        {it.refs.length > 0 && (
          <div className="flex flex-wrap gap-1 mt-1">
            {it.refs.slice(0, 10).map((ref, j) => {
              const l = refLabel(ref);
              return (
                <span key={ref + j} className="flex items-center gap-1 text-[10px] px-1.5 py-0.5 rounded" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)' }}>
                  <span style={{ color: resolveColor(KIND_COLOR[l.kind], '#9aa0aa') }}>{KIND_GLYPH[l.kind] ?? '•'}</span>
                  {l.name}
                </span>
              );
            })}
            {it.count > 10 && <span className="text-[10px] px-1.5 py-0.5" style={{ color: 'var(--sem-muted)' }}>+{it.count - 10} more</span>}
          </div>
        )}
      </div>
    </div>
  );
}

// A tinted verdict chip. `color-mix` gives a quiet fill + border on the tab's palette (no new colours) so a wall of
// badges never shouts — the verdict word carries the meaning, the tint just nudges the eye (good/warn/bad).
function VerdictBadge({ verdict, title }: { verdict: string; title?: string }) {
  const s = verdictStyle(verdict);
  return (
    <span title={title} className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full"
      style={{ color: s.color, background: `color-mix(in srgb, ${s.color} 16%, transparent)`, border: `1px solid color-mix(in srgb, ${s.color} 34%, transparent)` }}>
      {s.label}
    </span>
  );
}

// The persistent audit trail — collapsible, below the live timeline. The header carries the chain-integrity indicator:
// a quiet "chain intact · N records" when the hash chain verifies end-to-end, a LOUD warning when it doesn't (a
// record was edited or removed after the fact — the whole point of the chain is to make that undeniable). Export buttons
// (MD / JSON) hand the trail to whoever needs it; the engine Pro-gates the export and its upsell surfaces inline.
function AuditTrail({ chain, now, richHits, sessionId, onJump }: {
  chain: VerifiedEditsChain; now: number; richHits: ReturnType<typeof useRichEvidence>; sessionId?: string; onJump?: (revision: number) => void;
}) {
  const [open, setOpen] = useState(true);
  const [exporting, setExporting] = useState<string | null>(null);
  const [copied, setCopied] = useState<string | null>(null);
  const [exportErr, setExportErr] = useState<string | null>(null);
  const tier = useTier();
  const intact = chain.chainIntact;
  // Newest first (mirrors the live timeline above), but keep the seq so the chain order is still readable.
  const records = [...chain.records].sort((a, b) => b.seq - a.seq);

  const doExport = async (format: 'md' | 'json') => {
    setExporting(format); setExportErr(null); setCopied(null);
    try {
      const text = await rpc<string>('exportVerifiedEdits', format);
      const ok = await copyText(text);
      if (ok) { setCopied(format); window.setTimeout(() => setCopied((c) => (c === format ? null : c)), 2000); }
      else setExportErr('Could not copy to the clipboard.');
    } catch (e) {
      // The buttons stay visible and clickable on the free tier (honest: the capability exists, it's Pro) —
      // a free click gets the plain invitation, not the raw engine exception.
      setExportErr(isEntitlementError(e)
        ? 'Reading the full trail here is always free. Pro packages it as a shareable Markdown or JSON report.'
        : String((e as Error).message ?? e));
    } finally { setExporting(null); }
  };

  return (
    <Panel>
      <div className="flex items-center gap-2">
        <button onClick={() => setOpen((o) => !o)} className="flex items-center gap-1.5 text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>
          <span className="inline-block transition-transform text-[9px]" style={{ transform: open ? 'rotate(90deg)' : 'none' }}>▶</span>
          Audit trail
          <span style={{ color: 'var(--sem-muted)' }}>({chain.records.length})</span>
        </button>
        <div className="ml-auto flex items-center gap-2">
          {/* chain-integrity indicator: quiet when intact, a loud tinted pill when broken */}
          {intact ? (
            <span className="flex items-center gap-1 text-[11px]" style={{ color: 'var(--sem-good)' }} title="Every record verifies end-to-end (the hash chain is unbroken).">
              intact · {chain.records.length} record{chain.records.length === 1 ? '' : 's'}
            </span>
          ) : (
            <span className="flex items-center gap-1 text-[11px] font-semibold px-2 py-0.5 rounded-md" style={{ color: 'var(--sem-bad)', background: 'color-mix(in srgb, var(--sem-bad) 14%, transparent)', border: '1px solid color-mix(in srgb, var(--sem-bad) 40%, transparent)' }}
              title={chain.note || 'A record was edited or removed after it was written.'}>
              {/* firstBrokenSeq<=0 means the blob itself wouldn't parse (there IS no seq #0) — say "unreadable", not "broken at #0". */}
              {chain.firstBrokenSeq > 0 ? `integrity broken at record #${chain.firstBrokenSeq}` : 'trail unreadable'}
            </span>
          )}
          <span className="w-px h-4" style={{ background: 'var(--sem-border)' }} />
          <MiniButton onClick={() => doExport('md')} disabled={!!exporting}
            title={tier === 'free' ? 'Pro packages the trail as a shareable report. Reading it here stays free.' : 'Copy the audit trail as Markdown.'}>
            {copied === 'md' ? 'Copied ✓' : exporting === 'md' ? '…' : 'Export MD'}<ProBadge show={tier === 'free'} />
          </MiniButton>
          <MiniButton onClick={() => doExport('json')} disabled={!!exporting}
            title={tier === 'free' ? 'Pro packages the trail as a shareable report. Reading it here stays free.' : 'Copy the audit trail as JSON.'}>
            {copied === 'json' ? 'Copied ✓' : exporting === 'json' ? '…' : 'Export JSON'}<ProBadge show={tier === 'free'} />
          </MiniButton>
        </div>
      </div>

      {/* the loud broken-chain explainer + any export/entitlement message live directly under the header, always shown */}
      {!intact && (
        <div className="mt-2 rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb, var(--sem-bad) 12%, transparent)', color: 'var(--sem-bad)', border: '1px solid color-mix(in srgb, var(--sem-bad) 34%, transparent)' }}>
          {chain.note || (chain.firstBrokenSeq > 0
            ? `Integrity broken at record #${chain.firstBrokenSeq}: a record was edited or removed after it was written. The trail below cannot be trusted from that point on.`
            : 'The Verified Edits trail could not be read (the stored record is damaged). None of it can be trusted.')}
        </div>
      )}
      {exportErr && <div className="mt-2 rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb, var(--sem-accent) 12%, transparent)', color: 'var(--sem-fg)', border: '1px solid color-mix(in srgb, var(--sem-accent) 34%, transparent)' }}>{exportErr}</div>}
      {copied && <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Copied the {copied === 'md' ? 'Markdown' : 'JSON'} audit trail to the clipboard.</div>}

      {open && (
        <div className="relative mt-2">
          {/* the same vertical rail as the live timeline above — one visual language for "events over time" */}
          <div className="absolute top-1 bottom-1 w-px" style={{ left: 15, background: 'var(--sem-border)' }} />
          <div className="flex flex-col">
            {records.map((r) => <AuditRow key={r.seq} r={r} now={now} broken={!intact && r.seq >= chain.firstBrokenSeq} rich={matchRich(richHits, r)} sessionId={sessionId} onJump={onJump} />)}
          </div>
        </div>
      )}
    </Panel>
  );
}

// One record row on the audit RAIL — the same visual language as the live timeline above (circle node + rail),
// so "events over time" reads the same way twice. The node carries the VERDICT (tinted ring + glyph) instead of
// a pill in the row, the actor/time/seq collapse into one quiet right-aligned run, and the Details toggle is
// hover-revealed like the timeline's per-edit action — one loud thing per row (the verdict), everything else calm.
// The expander renders TYPED evidence (candidate DAX, mismatch grids, benchmark bars — full when a live rich
// result welded, digest otherwise, raw JSON as the fallback) + the chain hashes.
function AuditRow({ r, now, broken, rich, sessionId, onJump }: {
  r: VerifiedEditRecord; now: number; broken: boolean; rich?: unknown; sessionId?: string; onJump?: (revision: number) => void;
}) {
  const [open, setOpen] = useState(false);
  const l = r.objectRef ? refLabel(r.objectRef) : null;
  const s = verdictStyle(r.verdict);
  return (
    <div className="group relative flex items-start gap-3 py-2.5" style={{ opacity: broken ? 0.55 : 1 }}>
      {/* verdict node on the rail */}
      <div className="relative z-10 shrink-0 w-8 h-8 rounded-full flex items-center justify-center text-[13px] font-bold"
        title={s.label + (r.summary ? ': ' + r.summary : '')}
        style={{ background: 'var(--sem-surface-2)', color: s.color, border: `2px solid color-mix(in srgb, ${s.color} 65%, transparent)` }}>
        {VERDICT_GLYPH[r.verdict] ?? '•'}
      </div>
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="text-[12px] font-medium" style={{ color: 'var(--sem-fg)' }}>{opLabel(r.op)}</span>
          {l && (
            <span className="flex items-center gap-1 text-[10px] px-1.5 py-0.5 rounded" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)' }}>
              <span style={{ color: resolveColor(KIND_COLOR[l.kind], '#9aa0aa') }}>{KIND_GLYPH[l.kind] ?? '•'}</span>
              {l.name}
            </span>
          )}
          <span className="text-[10px] uppercase tracking-wide font-semibold" style={{ color: s.color }}>{s.label}</span>
          <button onClick={() => setOpen((o) => !o)}
            className={'text-[10px] px-1.5 py-0.5 rounded transition-opacity focus:opacity-100 ' + (open ? '' : 'opacity-0 group-hover:opacity-100')}
            style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
            {open ? 'Hide details' : 'Details'}
          </button>
          <span className="ml-auto text-[10px] tnum shrink-0" style={{ color: 'var(--sem-muted)' }} title={r.when + ' · chain #' + r.seq}>
            {actorLabel(r.origin)} · {shortWhen(r.when, now)} · #{r.seq}
          </span>
        </div>
        {r.summary && <div className="text-[11px] mt-1" style={{ color: 'var(--sem-muted)' }}>{r.summary}</div>}
        {r.overrideReason && (
          <div className="text-[11px] mt-1 px-2 py-1 rounded" style={{ color: 'var(--sem-bad)', background: 'color-mix(in srgb, var(--sem-bad) 12%, transparent)' }}>
            <span className="font-semibold">Override:</span> {r.overrideReason}
          </div>
        )}
        {open && (
          <div className="mt-1.5 flex flex-col gap-1.5">
            <Evidence record={r} rich={rich} sessionId={sessionId} onJump={onJump} />
            <div className="flex flex-wrap gap-x-4 gap-y-0.5 text-[10px] tnum" style={{ color: 'var(--sem-muted)' }}>
              <span title={r.bodyHash}>body {short8(r.bodyHash)}</span>
              <span title={r.prevHash}>prev {short8(r.prevHash)}</span>
              <span title={r.hash}>hash {short8(r.hash)}</span>
              <span>rev {r.revision}</span>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

function short8(h: string): string { return h ? h.slice(0, 8) : 'Not recorded'; }

function Legend({ kind, n }: { kind: 'you' | 'ai'; n: number }) {
  const ai = kind === 'ai';
  const accent = resolveColor('var(--sem-accent)', '#2ED47A');
  return (
    <span className="flex items-center gap-1.5 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
      <span className="w-4 h-4 rounded-full flex items-center justify-center text-[8px] font-bold"
        style={ai ? { background: accent, color: 'var(--sem-on-accent)' } : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
        {ai ? 'AI' : 'Y'}
      </span>
      {n} by {ai ? 'AI Assistant' : 'you'}
    </span>
  );
}

// ---- local primitives -------------------------------------------------------------------------
function Panel({ children }: { children: React.ReactNode }) {
  return <div className="rounded-xl border p-4" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>{children}</div>;
}
function Button({ children, onClick, disabled }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled}
      className="text-[12px] px-3 py-1.5 rounded-lg font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
function MiniButton({ children, onClick, disabled, title }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className="text-[11px] px-2 py-0.5 rounded-md font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
