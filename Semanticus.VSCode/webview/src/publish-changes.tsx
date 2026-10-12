import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { rpc, onDidChange, onActivity } from './bridge';
import { useConnection } from './connection';
import { isSignInError } from './authcopy';
import type { ApplyDiffResult, CompareSeed, ModelDiff } from './compare';
import { KIND_ICON, ReviewCardBody } from './diffview';
import {
  checkingCopy, clockTime, failureReasons, groupRows, isRemoval, neededParent, notPublishedLine, nothingToPublishCopy,
  couldNotGoLine, freeNewModelName, heldReason, newModelLabel, newModelSignIn, publishableRows, publishCounts, publishedLine, publishLabel, rowNote, undoneLine, unreachableCopy,
} from './publishcopy';
import { engineSentence, sortWorkspaces, workspaceName, type FabricWorkspace, type Load } from './connectpickers-model';

// The Changes tab of the Publish page. It compares your copy with the destination, once, then answers every tick
// from the list it already holds: a tick never reads the destination again. One dry run of the whole list names the
// rows Power BI would refuse. One button, in the page header, writes the ticked rows. Nothing sits between the press
// and the live model, because the label names the count and the place, and the write keeps a restore point.

interface RollbackWire { applied: boolean; error?: string; note?: string }
type Line = { tone: 'good' | 'bad'; text: string; undo?: { id: string; when: string } };
interface NewModelResult { workspace: string; endpoint: string; database: string; loaded: boolean; loadError?: string; signInNeeded?: boolean; note: string }
// The line a new model's publish ends on. The page turns into the Changes view of the new destination as soon as the
// context names it, so the line is carried over to that view once.
let carried: { database: string; line: Line } | null = null;
const carriedFor = (database?: string | null) => (carried && carried.database === database ? carried.line : null);

// The one button lives in the page header, on the line that says where it goes. Each tab hands it over through this.
export function PageAction({ slot, children }: { slot: HTMLElement | null; children: ReactNode }) {
  return slot ? createPortal(children, slot) : null;
}

export function PublishChanges({ seed, destination, slot, onWrote, onReach, onChooseDestination }: {
  seed: CompareSeed | null; destination: string; slot: HTMLElement | null;
  onWrote?: () => void;                       // the destination just changed (a publish or an undo)
  onReach?: (ok: boolean) => void;            // whether the last read reached the destination
  onChooseDestination: () => void;
}) {
  const { connectXmla, busy: signingIn } = useConnection();
  const left = seed?.left ?? null;
  const right = seed?.right && seed.right.kind === 'workspace' ? seed.right : null;
  const [diff, setDiff] = useState<ModelDiff | null>(null);
  const [readErr, setReadErr] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [wish, setWish] = useState<Set<string>>(new Set());
  const [refused, setRefused] = useState<Map<string, string>>(new Map());
  const [busy, setBusy] = useState<'publish' | 'undo' | null>(null);
  const [line, setLine] = useState<Line | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const seq = useRef(0);
  const known = useRef<Set<string>>(new Set());   // refs of the last list, so a re-read keeps the person's ticks
  const wishNow = useRef(wish); wishNow.current = wish;

  const items = useMemo(() => publishableRows((diff?.items ?? []).filter((i) => i.action !== 'Equal')), [diff]);

  async function read() {
    if (!left || !right) return;
    const mine = ++seq.current;
    setLoading(true);
    try {
      const d = await rpc<ModelDiff>('compareModels', left, right);
      if (mine !== seq.current) return;
      if (d.error) { setDiff(null); setReadErr(d.error); onReach?.(false); return; }
      onReach?.(true);
      const list = publishableRows(d.items.filter((i) => i.action !== 'Equal'));
      // Ticks the person made stay. A row that is new to the list starts ticked, except a removal: that is the one tick that stays a choice.
      const next = new Set<string>();
      for (const it of list) if (known.current.has(it.ref) ? wishNow.current.has(it.ref) : it.action !== 'Delete') next.add(it.ref);
      known.current = new Set(list.map((i) => i.ref));
      setWish(next); setDiff(d); setReadErr(null);
      // One dry run of the whole list. It names what Power BI would refuse; ticking never asks again.
      let reasons = new Map<string, string>();
      if (list.length > 0) {
        try {
          const dry = await rpc<ApplyDiffResult>('applyDiff', left, right, list.map((i) => i.ref), false, 'human');
          if (mine !== seq.current) return;
          reasons = failureReasons(dry.failedRefs, list);
        } catch { /* the write itself says no in plain words; a failed rehearsal must not hide the list */ }
      }
      setRefused(reasons);
    } catch (e) {
      if (mine !== seq.current) return;
      setDiff(null); setReadErr(String((e as Error).message ?? e)); onReach?.(false);
    } finally { if (mine === seq.current) setLoading(false); }
  }

  const identity = `${right?.endpoint ?? ''}|${right?.database ?? ''}|${seed?.nonce ?? 0}`;
  useEffect(() => { known.current = new Set(); setLine(carriedFor(right?.database)); void read(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [identity]);
  useEffect(() => {
    let timer: number | undefined;
    const again = () => { window.clearTimeout(timer); timer = window.setTimeout(() => void read(), 500); };
    const offC = onDidChange(again);
    const offA = onActivity((e) => { if (e.kind === 'apply_model_diff' || e.kind === 'deploy_live' || e.kind === 'rollback_push') again(); });
    return () => { offC(); offA(); window.clearTimeout(timer); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [identity]);

  // What each tick means, worked out here from the list and the ticks.
  const needs = useMemo(() => {
    const m = new Map<string, { name: string; ref: string }>();
    for (const it of items) { if (!wish.has(it.ref)) continue; const p = neededParent(it, items, wish); if (p) m.set(it.ref, p); }
    return m;
  }, [items, wish]);
  const effective = useMemo(() => new Set(items.filter((i) => wish.has(i.ref) && !needs.has(i.ref) && !refused.has(i.ref) && !heldReason(i)).map((i) => i.ref)), [items, wish, needs, refused]);
  const counts = useMemo(() => publishCounts(items, effective), [items, effective]);
  const groups = useMemo(() => groupRows(items), [items]);

  const flip = (ref: string, on: boolean) => setWish((w) => { const n = new Set(w); if (on) n.add(ref); else n.delete(ref); return n; });
  const tickAll = () => setWish(new Set(items.filter((i) => !isRemoval(i)).map((i) => i.ref)));   // removals stay a choice

  async function publish() {
    if (!left || !right || effective.size === 0 || busy) return;
    const when = clockTime(new Date());
    carried = null;
    setBusy('publish'); setLine(null);
    try {
      const r = await rpc<ApplyDiffResult>('applyDiff', left, right, [...effective], true, 'human');
      if (r.error) setLine({ tone: 'bad', text: r.error });
      else if (!r.applied) {
        const why = [...failureReasons(r.failedRefs, items).values()][0];
        setLine({ tone: 'bad', text: `${notPublishedLine(destination)}${why ? ` ${why}` : ''}` });
      } else {
        const failed = failureReasons(r.failedRefs, items);
        const extra = failed.size > 0 ? ` ${couldNotGoLine(failed, items)} ${[...failed.values()][0]}` : '';
        setLine({ tone: 'good', text: publishedLine(r.count, destination) + extra, undo: r.restorePointId && r.count > 0 ? { id: r.restorePointId, when } : undefined });
      }
      onWrote?.();
    } catch (e) { setLine({ tone: 'bad', text: String((e as Error).message ?? e) }); }
    finally { setBusy(null); }
    await read();   // a refused write usually means the destination moved: compare again by itself and relabel
  }

  async function undo(point: { id: string; when: string }) {
    setBusy('undo');
    try {
      const r = await rpc<RollbackWire>('rollbackPush', point.id, true, null, 'human');
      setLine(r.error ? { tone: 'bad', text: r.error } : { tone: 'good', text: undoneLine(destination, point.when) });
      onWrote?.();
    } catch (e) { setLine({ tone: 'bad', text: String((e as Error).message ?? e) }); }
    finally { setBusy(null); }
    await read();
  }

  async function signInAndRetry() {
    if (!right?.endpoint) return;
    const { ok, message } = await connectXmla(right.endpoint, right.database ?? '', 'interactive', right.tenantId ?? null);
    if (ok) void read(); else setReadErr(message || readErr);
  }

  if (!right || !left) return <NewModel slot={slot} note={seed?.note} onChooseDestination={onChooseDestination} onWrote={onWrote} />;

  const checking = loading && !diff;
  const empty = !!diff && items.length === 0;
  const label = publishLabel({ changes: counts.changes, removals: counts.removals, target: destination, busy: busy === 'publish', checking, empty });
  const canPress = !!diff && !busy && effective.size > 0;

  return (
    <>
      {diff && <PageAction slot={slot}>
        <button type="button" className="sem-btn sem-btn-primary" data-testid="publish-go" disabled={!canPress} onClick={() => void publish()}>{label}</button>
      </PageAction>}
      {checking && <PageAction slot={slot}><button type="button" className="sem-btn sem-btn-primary" data-testid="publish-go" disabled>{label}</button></PageAction>}

      {line && (
        <div className="sem-inset sem-inline" data-testid="publish-result" role="status">
          <span className={line.tone === 'good' ? 'sem-chip sem-chip-ok' : 'sem-chip sem-chip-bad'} aria-hidden="true" />
          <span className="min-w-0 flex-1 text-(length:--sem-text-body)">{line.text}</span>
          {line.undo && <button type="button" className="sem-btn sem-btn-sm" data-testid="publish-undo" disabled={!!busy} onClick={() => void undo(line.undo!)}>{busy === 'undo' ? 'Undoing...' : 'Undo publish'}</button>}
        </div>
      )}

      {readErr && !diff && (
        <div className="sem-inset sem-stack" data-testid="publish-unreachable">
          <div className="sem-inline text-(length:--sem-text-body)"><span className="sem-chip sem-chip-bad" aria-hidden="true" />{unreachableCopy(destination)}</div>
          <div className="sem-text-meta">{readErr}</div>
          <div>{isSignInError(readErr)
            ? <button type="button" className="sem-btn" data-testid="publish-signin" disabled={signingIn} onClick={() => void signInAndRetry()}>Sign in</button>
            : <button type="button" className="sem-btn" onClick={() => void read()}>Try again</button>}</div>
        </div>
      )}

      {checking && !readErr && <div className="sem-text-meta" data-testid="publish-checking">{checkingCopy(destination)}</div>}
      {empty && <div className="sem-text-body" data-testid="publish-empty">{nothingToPublishCopy(destination)}</div>}

      {diff && !empty && (
        <section className="sem-card sem-card-flush" data-testid="publish-list">
          <div className="sem-inline px-4 py-3" style={{ borderBottom: '1px solid var(--sem-rule)' }}>
            <span className="sem-text-meta flex-1">Ticked changes go to {destination}. Untick any you want to keep back.</span>
            <button type="button" className="sem-btn sem-btn-quiet sem-btn-sm" onClick={tickAll}>Tick all</button>
            <button type="button" className="sem-btn sem-btn-quiet sem-btn-sm" onClick={() => setWish(new Set())}>Untick all</button>
          </div>
          {groups.map((g) => (
            <div key={g.key || '(model)'}>
              <div className="sem-inline px-4" style={{ minHeight: 'var(--sem-row)', borderBottom: '1px solid var(--sem-rule)' }}>
                <span className="font-semibold flex-1 min-w-0 truncate">{g.label}</span>
                <span className="sem-text-meta">{g.rows.filter((r) => effective.has(r.ref)).length} of {g.rows.length} ticked</span>
              </div>
              {g.rows.map((row) => {
                const parent = needs.get(row.ref);
                const reason = refused.get(row.ref) ?? heldReason(row);
                const blocked = !!parent || !!reason;
                return (
                  <div key={row.ref + ':' + row.action} data-testid="publish-row" data-ref={row.ref} className="px-4"
                    style={{ display: 'grid', gridTemplateColumns: 'var(--sem-space-4) var(--sem-space-4) minmax(0, 1fr)', columnGap: 'var(--sem-space-2)', alignItems: 'center',
                      borderBottom: '1px solid var(--sem-rule)', background: blocked ? 'var(--sem-wash-bad)' : undefined }}>
                    <input type="checkbox" checked={effective.has(row.ref)} disabled={blocked} aria-label={`Include ${row.name}`}
                      onChange={(e) => flip(row.ref, e.target.checked)} style={{ accentColor: 'var(--sem-accent)', margin: 0 }} />
                    <span className="text-center" style={{ color: 'var(--sem-muted)' }} aria-hidden="true">{KIND_ICON[row.objectType] ?? '•'}</span>
                    <button type="button" className="text-left min-w-0 truncate" title="Show what changed" aria-expanded={open === row.ref}
                      style={{ background: 'transparent', border: 'none', color: 'var(--sem-fg)', cursor: 'pointer', padding: 0, font: 'inherit', minHeight: 'var(--sem-row)' }}
                      onClick={() => setOpen(open === row.ref ? null : row.ref)}>
                      {row.name}
                      <span className="sem-text-meta"> · {rowNote(row, destination)}</span>
                    </button>
                    {parent && (
                      <div className="sem-inline text-(length:--sem-text-meta) pb-2" style={{ gridColumn: 3 }} data-testid="publish-needs">
                        <span className="sem-chip sem-chip-bad" aria-hidden="true" />
                        <span>Needs the {parent.name} table, which is not ticked. <button type="button" className="underline" data-testid="publish-tick-parent" onClick={() => flip(parent.ref, true)}>Tick {parent.name} too</button></span>
                      </div>
                    )}
                    {!parent && reason && (
                      <div className="sem-inline text-(length:--sem-text-meta) pb-2" style={{ gridColumn: 3 }} data-testid="publish-refused">
                        <span className="sem-chip sem-chip-bad" aria-hidden="true" /><span>{reason}</span>
                      </div>
                    )}
                    {open === row.ref && <div className="sem-inset" style={{ gridColumn: '1 / -1', margin: '0 0 var(--sem-space-2)' }}><ReviewCardBody item={row} leftLabel="Your copy" rightLabel={destination} /></div>}
                  </div>
                );
              })}
            </div>
          ))}
        </section>
      )}
    </>
  );
}

// No destination yet: publish the whole model as a new model in a workspace. Pick the workspace, keep or change the
// name, and one press creates it, loads its data and makes it this model's destination.
function NewModel({ slot, note, onChooseDestination, onWrote }: {
  slot: HTMLElement | null; note?: string; onChooseDestination: () => void; onWrote?: () => void;
}) {
  const { context, session, refresh } = useConnection();
  const [signIn, setSignIn] = useState<{ authMode: string; tenantId: string | null } | null>(null);
  const [workspaces, setWorkspaces] = useState<Load<FabricWorkspace>>({ kind: 'loading' });
  const [wsId, setWsId] = useState('');
  const [name, setName] = useState(session?.modelName ?? '');
  const typedName = useRef(false);   // the person changed the name: it is theirs, and no default replaces it
  const [busy, setBusy] = useState(false);
  const [line, setLine] = useState<Line | null>(null);
  useEffect(() => { if (!name && session?.modelName) setName(session.modelName); }, [session?.modelName]);   // eslint-disable-line react-hooks/exhaustive-deps

  async function load() {
    setWorkspaces({ kind: 'loading' });
    // The sign-in the model's SQL source was read with comes first: it is the one the person just used.
    const plan = await rpc<{ source?: { authMode?: string; tenantId?: string } }>('getStartPlan').catch(() => null);
    const saved = (await rpc<{ kind?: string; authMode?: string; tenantId?: string }[]>('listConnections').catch(() => [])) ?? [];
    const si = newModelSignIn([plan?.source, context?.publishing, context?.editing, context?.querying, ...saved.filter((r) => r.kind === 'xmla')]);
    setSignIn(si);
    try {
      const list = sortWorkspaces((await rpc<FabricWorkspace[]>('listWorkspaces', si.authMode, si.tenantId)) ?? []);
      setWorkspaces({ kind: 'ready', items: list });
      if (list.length === 1) setWsId(list[0].id);
    } catch (e) { setWorkspaces({ kind: 'failed', message: engineSentence(e) }); }
  }
  useEffect(() => { void load(); }, []);   // eslint-disable-line react-hooks/exhaustive-deps
  // The workspace already holds a model with this model's name ("Gold"): the card offers the next free one ("Gold 2"), so the first press does not meet a refusal.
  useEffect(() => {
    const base = session?.modelName;
    if (!wsId || !signIn || !base) return;
    let live = true;
    void rpc<{ displayName: string }[]>('listWorkspaceModels', wsId, signIn.authMode, signIn.tenantId).then(
      (list) => { if (live && !typedName.current) setName(freeNewModelName(base, (list ?? []).map((m) => m.displayName))); },
      () => undefined,
    );
    return () => { live = false; };
  }, [wsId, signIn, session?.modelName]);   // eslint-disable-line react-hooks/exhaustive-deps

  const ws = workspaces.kind === 'ready' ? workspaces.items.find((w) => w.id === wsId) ?? null : null;
  async function publish() {
    if (!ws || !signIn || busy || !name.trim()) return;
    setBusy(true); setLine(null);
    try {
      const r = await rpc<NewModelResult>('publishNewModel', workspaceName(ws), name.trim(), signIn.authMode, signIn.tenantId, 'human', ws.id);
      // An import model Power BI has no source sign-in for is published; its line is the next step, not an error.
      carried = { database: r.database, line: { tone: r.loaded || r.signInNeeded ? 'good' : 'bad', text: r.note } };
      setLine(carried.line);
      onWrote?.();
      await refresh();
    } catch (e) { setLine({ tone: 'bad', text: engineSentence(e) }); }
    finally { setBusy(false); }
  }

  return (
    <>
      <PageAction slot={slot}>
        <button type="button" className="sem-btn sem-btn-quiet" data-testid="publish-choose" onClick={onChooseDestination}>Choose destination</button>
        <button type="button" className="sem-btn sem-btn-primary" data-testid="publish-new-go" disabled={!ws || !name.trim() || busy} onClick={() => void publish()}>
          {newModelLabel({ busy, workspace: ws ? workspaceName(ws) : null })}</button>
      </PageAction>
      {note && <div className="sem-text-meta">{note}</div>}
      {line && (
        <div className="sem-inset sem-inline" data-testid="publish-result" role="status">
          <span className={line.tone === 'good' ? 'sem-chip sem-chip-ok' : 'sem-chip sem-chip-bad'} aria-hidden="true" />
          <span className="min-w-0 flex-1 text-(length:--sem-text-body)">{line.text}</span>
        </div>
      )}
      <section className="sem-card sem-stack" data-testid="publish-new-model">
        <h3 className="sem-card-title">New model in a workspace</h3>
        <div className="sem-inline flex-wrap">
          <label className="sem-field-label">Workspace
            {workspaces.kind === 'ready' ? (
              <select className="sem-field" data-testid="publish-new-ws" value={wsId} disabled={busy} onChange={(e) => setWsId(e.target.value)}>
                <option value="">Choose a workspace</option>
                {workspaces.items.map((w) => <option key={w.id} value={w.id}>{workspaceName(w)}</option>)}
              </select>
            ) : <span className="sem-text-meta">{workspaces.kind === 'loading' ? 'Looking for your workspaces...' : workspaces.message}</span>}
          </label>
          <label className="sem-field-label">Name
            <input className="sem-field" data-testid="publish-new-name" value={name} disabled={busy} autoComplete="off" onChange={(e) => { typedName.current = true; setName(e.target.value); }} />
          </label>
          {workspaces.kind === 'failed' && <button type="button" className="sem-btn sem-btn-sm" onClick={() => void load()}>Try again</button>}
        </div>
        <span className="sem-text-meta">Publishes the whole model and loads its data. Later publishes update it.</span>
      </section>
    </>
  );
}
