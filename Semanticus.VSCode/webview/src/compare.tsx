import { useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onConnectionChange } from './bridge';
import { useTier, isEntitlementError, ProBadge, UpsellNotice } from './pro';
import { useConnection } from './connection';
import { DiffView } from './diffview';
import { isSignInError } from './authcopy';

// Wire shapes mirror Semanticus.Engine/Alm/AlmProtocol.cs (camelCased). The Compare tab is the general
// any-two-models differ/merger over compareModels + applyDiff; the Deploy tab's Source-Control diff is a
// preset (Source=session, Target=gitref:HEAD) rendered with the SAME <CompareGrid>. Phase 1: read-only
// drill-down (summary → object → property → code) + apply-into-a-FILE target (the today-supported applyDiff
// target). Merge-into-the-open-session and copy/paste land in Phases 2–3.
export interface ModelRef { kind: string; path?: string; gitRef?: string; endpoint?: string; database?: string; authMode?: string; tenantId?: string; label?: string; }
export interface ModelDiffItem { ref: string; objectType: string; name: string; table?: string; action: string; leftText?: string; rightText?: string; matchedByName?: boolean; }
export interface ModelDiff { leftLabel?: string; rightLabel?: string; created: number; updated: number; deleted: number; equal: number; items: ModelDiffItem[]; error?: string; }
export interface ApplyDiffResult { applied: boolean; count: number; appliedRefs: string[]; failedRefs: string[]; target?: string; note?: string; error?: string; confirmToken?: string; }

const ACTION_COLOR: Record<string, string> = { Create: 'var(--sem-good)', Update: 'var(--sem-warn)', Delete: 'var(--sem-bad)', Equal: 'var(--sem-muted)' };
const TYPE_ORDER = ['Table', 'Column', 'Measure', 'Hierarchy', 'Partition', 'Relationship', 'Role', 'Perspective', 'Culture', 'DataSource', 'Expression'];

// A remembered live-model record from the engine connection registry — the SAME `listConnections` op that backs the
// Connections manager. Only the fields the Workspace picker needs (a workspace ModelRef is built straight from these);
// each webview module declares its own registry subset, as deploy.tsx / permissions.tsx already do.
interface ConnRecord { id: string; kind: string; endpoint: string; database?: string; modelName?: string; authMode?: string; tenantId?: string; label?: string; }
const shortEndpoint = (value?: string) => {
  const v = (value || '').replace(/[\\/]+$/, '');
  const i = Math.max(v.lastIndexOf('/'), v.lastIndexOf('\\'));
  return i >= 0 ? v.slice(i + 1) : v;
};
const recordName = (r: ConnRecord) => r.modelName || r.database || shortEndpoint(r.endpoint) || 'Model';
// An XMLA record's environment chip: its declared label (uat / prod / local), else the fail-closed "Production
// safeguards" wording for an unlabelled cloud model — the SAME pattern the Connections manager uses (do not reword).
const envLabel = (r: ConnRecord) => r.label || 'Production safeguards';
// Build a workspace ModelRef straight from a registry record: no endpoint is ever typed here, and the record threads
// its own tenant so a cross-tenant compare targets ITS tenant, not az login's default (which the old free-text form lost).
const workspaceRef = (r: ConnRecord): ModelRef => ({
  kind: 'workspace', endpoint: r.endpoint, database: r.database ?? '',
  authMode: r.authMode || 'interactive', tenantId: r.tenantId ?? '', label: recordName(r),
});
const PICKER_INPUT = { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', borderRadius: 4, padding: '2px 6px', fontSize: 12 } as const;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// The shared diff grid: groups by object type, drills each row down to a property-level (or code-level)
// left↔right comparison. Selectable when `selected`/`onSelectedChange` are supplied (the checked refs ARE
// the applyDiff selectedRefs). `failed` badges refs a dry-run validate said would fail.
export function CompareGrid({ diff, leftLabel, rightLabel, selected, onSelectedChange, failed }: {
  diff: ModelDiff;
  leftLabel?: string; rightLabel?: string;
  selected?: Set<string>;
  onSelectedChange?: (next: Set<string>) => void;
  failed?: Set<string>;
}) {
  const selectable = !!selected && !!onSelectedChange;
  const [expanded, setExpanded] = useState<string | null>(null);
  const [collapsed, setCollapsed] = useState<Set<string>>(() => new Set());   // all groups expanded (relationships now match structurally — no false-diff to hide)
  const left = leftLabel ?? diff.leftLabel ?? 'source';
  const right = rightLabel ?? diff.rightLabel ?? 'target';

  // group items by objectType, in a stable, sensible order
  const groups = useMemo(() => {
    const m = new Map<string, ModelDiffItem[]>();
    for (const it of diff.items) { const a = m.get(it.objectType); if (a) a.push(it); else m.set(it.objectType, [it]); }
    return [...m.entries()].sort((a, b) => {
      const ia = TYPE_ORDER.indexOf(a[0]), ib = TYPE_ORDER.indexOf(b[0]);
      return (ia < 0 ? 99 : ia) - (ib < 0 ? 99 : ib) || a[0].localeCompare(b[0]);
    });
  }, [diff.items]);

  const toggle = (refs: string[], on: boolean) => {
    if (!selected || !onSelectedChange) return;
    const next = new Set(selected);
    for (const r of refs) { if (on) next.add(r); else next.delete(r); }
    onSelectedChange(next);
  };

  if ((diff.created + diff.updated + diff.deleted) === 0) {
    return <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No differences. {left} and {right} are identical.</div>;
  }

  return (
    <div>
      <div className="text-[11px] font-semibold uppercase tracking-wide mb-1 flex items-center gap-2" style={{ color: 'var(--sem-muted)' }}>
        <span>{left}</span><span style={{ color: 'var(--sem-accent)' }}>→</span><span>{right}</span>
        <span className="ml-1"><span style={{ color: 'var(--sem-good)' }}>+{diff.created}</span> <span style={{ color: 'var(--sem-warn)' }}>~{diff.updated}</span> <span style={{ color: 'var(--sem-bad)' }}>−{diff.deleted}</span></span>
        <span style={{ color: 'var(--sem-muted)', opacity: 0.7 }}>· {diff.equal ?? 0} unchanged</span>
      </div>
      <div className="rounded" style={{ border: '1px solid var(--sem-border)', maxHeight: 420, overflow: 'auto' }}>
        {groups.map(([type, items]) => {
          const isCollapsed = collapsed.has(type);
          const refs = items.map((i) => i.ref);
          const selCount = selectable ? refs.filter((r) => selected!.has(r)).length : 0;
          const nameMatched = items.some((i) => i.matchedByName);   // role/perspective/etc. — keyed by name (a rename reads as delete+create)
          return (
            <div key={type}>
              <div className="flex items-center gap-2 px-2 py-1 text-[11px] sticky top-0 z-10" style={{ background: 'var(--sem-surface-2)', borderBottom: '1px solid var(--sem-border)' }}>
                {selectable && <GroupCheck total={items.length} selected={selCount} onChange={(on) => toggle(refs, on)} />}
                <button onClick={() => setCollapsed((c) => { const n = new Set(c); if (n.has(type)) n.delete(type); else n.add(type); return n; })}
                  className="flex items-center gap-1" style={{ background: 'transparent', border: 'none', color: 'var(--sem-fg)', cursor: 'pointer' }}>
                  <span style={{ color: 'var(--sem-muted)' }}>{isCollapsed ? '▸' : '▾'}</span>
                  <span className="font-semibold uppercase tracking-wide">{type}</span>
                  <span style={{ color: 'var(--sem-muted)' }}>({items.length})</span>
                </button>
                {nameMatched && <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }} title="Identified by name (a rename reads as delete+create). Relationships are matched structurally by their endpoints, so they're exempt. This applies to roles, perspectives, cultures, data sources, expressions and partitions.">ⓘ matched by name</span>}
              </div>
              {!isCollapsed && items.map((it) => (
                <div key={it.ref + ':' + it.action}>
                  <div className="flex items-center gap-2 px-2 py-1 text-[12px]" style={{ borderBottom: '1px solid var(--sem-border)', background: failed?.has(it.ref) ? 'color-mix(in srgb,var(--sem-bad) 10%, transparent)' : undefined }}>
                    {selectable && <input type="checkbox" checked={selected!.has(it.ref)} onChange={(e) => toggle([it.ref], e.target.checked)} />}
                    <Badge color={ACTION_COLOR[it.action] ?? 'var(--sem-muted)'}>{it.action}</Badge>
                    <span className="font-medium cursor-pointer flex-1 min-w-0 truncate" onClick={() => setExpanded(expanded === it.ref ? null : it.ref)}>
                      <span style={{ color: 'var(--sem-muted)' }}>{expanded === it.ref ? '▾ ' : '▸ '}</span>
                      {it.table ? <span><span style={{ color: 'var(--sem-muted)' }}>{it.table}[</span>{it.name}<span style={{ color: 'var(--sem-muted)' }}>]</span></span> : it.name}
                    </span>
                    {failed?.has(it.ref) && <span className="text-[10px]" style={{ color: 'var(--sem-bad)' }} title="The dry-run reported this would fail to apply (often a missing dependency; select its parent too).">would fail</span>}
                  </div>
                  {expanded === it.ref && (
                    <div className="px-3 py-2" style={{ background: 'var(--sem-surface-2)', borderBottom: '1px solid var(--sem-border)' }}>
                      <ObjectDelta item={it} leftLabel={left} rightLabel={right} />
                    </div>
                  )}
                </div>
              ))}
            </div>
          );
        })}
      </div>
    </div>
  );
}

// A group's tri-state checkbox (checked / indeterminate / unchecked) that selects/deselects the whole group.
function GroupCheck({ total, selected, onChange }: { total: number; selected: number; onChange: (on: boolean) => void }) {
  const ref = useRef<HTMLInputElement>(null);
  useEffect(() => { if (ref.current) ref.current.indeterminate = selected > 0 && selected < total; }, [selected, total]);
  return <input ref={ref} type="checkbox" checked={selected === total && total > 0} onChange={(e) => onChange(e.target.checked)} title={`${selected}/${total} selected`} />;
}

// The drill-down for one object: a PROPERTY-level table when both sides are JSON (the common case — TOM JSON),
// else a side-by-side code view with changed-line highlight. Parsing is best-effort and never throws.
function ObjectDelta({ item, leftLabel, rightLabel }: { item: ModelDiffItem; leftLabel: string; rightLabel: string }) {
  const props = useMemo(() => propertyDelta(item.leftText, item.rightText), [item.leftText, item.rightText]);
  if (props) {
    return (
      <table className="w-full text-[11px] border-collapse">
        <thead><tr>
          <th className="text-left px-1 py-0.5" style={{ color: 'var(--sem-muted)', width: '22%' }}>property</th>
          <th className="text-left px-1 py-0.5" style={{ color: 'var(--sem-good)' }}>{leftLabel}</th>
          <th className="text-left px-1 py-0.5" style={{ color: 'var(--sem-muted)' }}>{rightLabel}</th>
        </tr></thead>
        <tbody>
          {props.map((p) => (
            <tr key={p.key} style={{ background: p.changed ? 'color-mix(in srgb,var(--sem-warn) 12%, transparent)' : undefined }}>
              <td className="px-1 py-0.5 align-top font-mono" style={{ color: p.changed ? 'var(--sem-warn)' : 'var(--sem-muted)' }}>{p.changed ? '~ ' : ''}{p.key}</td>
              <td className="px-1 py-0.5 align-top font-mono whitespace-pre-wrap" style={{ color: p.left == null ? 'var(--sem-muted)' : 'var(--sem-fg)' }}>{p.left ?? 'Not present'}</td>
              <td className="px-1 py-0.5 align-top font-mono whitespace-pre-wrap" style={{ color: p.right == null ? 'var(--sem-muted)' : 'var(--sem-fg)' }}>{p.right ?? 'Not present'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    );
  }
  // fallback: raw side-by-side with changed-line highlight
  const ld = lineHighlight(item.leftText, item.rightText);
  return (
    <div className="grid grid-cols-2 gap-3">
      <CodePane label={leftLabel} color="var(--sem-good)" lines={ld.left} />
      <CodePane label={rightLabel} color="var(--sem-muted)" lines={ld.right} />
    </div>
  );
}

function CodePane({ label, color, lines }: { label: string; color: string; lines: { text: string; changed: boolean }[] }) {
  return (
    <div className="min-w-0">
      <div className="text-[10px] mb-0.5" style={{ color }}>{label}</div>
      <pre className="text-[11px] m-0" style={{ maxHeight: 200, overflow: 'auto' }}>{lines.length === 0 ? '(absent)' : lines.map((l, i) => (
        <div key={i} style={{ background: l.changed ? 'color-mix(in srgb,var(--sem-warn) 14%, transparent)' : undefined, whiteSpace: 'pre-wrap' }}>{l.text || ' '}</div>
      ))}</pre>
    </div>
  );
}

// best-effort: if both texts parse to JSON objects, return a per-key left/right/changed comparison.
export function propertyDelta(leftText?: string, rightText?: string): { key: string; left: string | null; right: string | null; changed: boolean }[] | null {
  const lo = tryParseObject(leftText), ro = tryParseObject(rightText);
  if (lo === undefined && ro === undefined) return null;            // neither is JSON → fall back to text
  const lf = lo ? flatten(lo) : {}, rf = ro ? flatten(ro) : {};
  const keys = [...new Set([...Object.keys(lf), ...Object.keys(rf)])].sort();
  if (keys.length === 0) return null;
  return keys.map((key) => {
    const left = key in lf ? lf[key] : null, right = key in rf ? rf[key] : null;
    return { key, left, right, changed: left !== right };
  });
}
function tryParseObject(s?: string): Record<string, unknown> | undefined {
  if (!s) return undefined;
  const t = s.trim();
  if (!t.startsWith('{')) return undefined;
  try { const v = JSON.parse(t); return v && typeof v === 'object' && !Array.isArray(v) ? v as Record<string, unknown> : undefined; }
  catch { return undefined; }
}
// one level deep; deeper values stringified compactly so a property row stays readable.
function flatten(o: Record<string, unknown>, prefix = ''): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(o)) {
    const key = prefix ? prefix + '.' + k : k;
    if (v && typeof v === 'object' && !Array.isArray(v) && Object.keys(v).length <= 6) Object.assign(out, flatten(v as Record<string, unknown>, key));
    else out[key] = typeof v === 'string' ? v : JSON.stringify(v);
  }
  return out;
}
// cheap line-level highlight (not a true LCS diff): a line is "changed" if absent from the other side's line set.
function lineHighlight(leftText?: string, rightText?: string): { left: { text: string; changed: boolean }[]; right: { text: string; changed: boolean }[] } {
  const ll = (leftText ?? '').split('\n'), rl = (rightText ?? '').split('\n');
  const ls = new Set(ll), rs = new Set(rl);
  return { left: ll.map((t) => ({ text: t, changed: !rs.has(t) })), right: rl.map((t) => ({ text: t, changed: !ls.has(t) })) };
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// The Compare tab: pick a Source and a Target model, diff them, drill in, and (when the target is a FILE)
// selectively apply Source→Target (a gated two-step write — the only applyDiff target supported today).
export type CompareSeed = { left: ModelRef; right: ModelRef | null; note?: string; nonce: number };

export function CompareView({ seed, embedded = false }: { seed?: CompareSeed | null; embedded?: boolean }) {
  const [left, setLeft] = useState<ModelRef>({ kind: 'session' });
  const [right, setRight] = useState<ModelRef>({ kind: 'gitref', gitRef: 'HEAD' });
  // A note from a seed that could only fill the Source: e.g. an attached XMLA engine whose dataset can't be named, so
  // we ask the user to pick the Target rather than diff an arbitrary dataset. Cleared once they touch the comparison.
  const [seedNote, setSeedNote] = useState<string | null>(null);
  const [diff, setDiff] = useState<ModelDiff | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [failed, setFailed] = useState<Set<string> | undefined>(undefined);
  // "Only differences" defaults ON — identical objects are hidden (shown as a count). Turning it OFF fetches the
  // full set (includeEqual) ONCE and keeps the selection; the engine omits identical bodies by default (smaller payload).
  const [onlyDiffs, setOnlyDiffs] = useState(true);
  const [equalsLoaded, setEqualsLoaded] = useState(false);   // has the current diff been fetched WITH identical objects?
  const [equalsLoading, setEqualsLoading] = useState(false);
  const [forceReviewNonce, setForceReviewNonce] = useState(0);   // a footer/status-bar seed lands on Review
  const [busy, setBusy] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [upsell, setUpsell] = useState<string | null>(null);   // a free click on a bulk merge teaches, never errors
  const tier = useTier();
  const { connectXmla, busy: connBusy, openConnections } = useConnection();   // sign-in-and-retry on an auth error (#156) + opening the shared Connections manager to add a model
  // Remembered live models from the engine connection registry — the Workspace side of the picker SELECTS one of these
  // instead of typing an endpoint (Add connection is the only place an endpoint is typed; docs/design-records-2026-07-12.md
  // T13/R8). Reloads on onConnectionChange, which fires for every registry mutation: the manager's own "Add a published
  // model" broadcasts it in-webview (connect_xmla emits no activity), and the host relays remember/label/forget from
  // either door — so a model added or forgotten in the Connections manager appears/disappears here without a remount.
  const [xmlaRecords, setXmlaRecords] = useState<ConnRecord[]>([]);
  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      try { const recs = (await rpc<ConnRecord[]>('listConnections')) ?? []; if (!cancelled) setXmlaRecords(recs.filter((r) => r.kind === 'xmla')); }
      catch { /* the registry is supporting context — a fetch failure still leaves file / git-ref / working-copy compares usable */ }
    };
    void load();
    const off = onConnectionChange(() => { void load(); });
    return () => { cancelled = true; off(); };
  }, []);
  const [pending, setPending] = useState<ApplyDiffResult | null>(null);   // apply dry-run awaiting Confirm
  const [result, setResult] = useState<ApplyDiffResult | null>(null);
  // A context-bar click seeds Source/Target (editing vs querying). We adopt a seed once per nonce, and never once the
  // user has set up their own comparison by hand — mirroring how the Change Plan seed only acts on an untouched plan.
  const consumedNonce = useRef<number | undefined>(undefined);
  const gatedWhyRef = useRef<HTMLDivElement>(null);
  const [touched, setTouched] = useState(false);
  const touch = () => { setTouched(true); setSeedNote(null); };

  const targetIsFile = right.kind === 'file';

  // Auto-diff on open with the default Source → Target (working copy → HEAD), mirroring the Deploy tab; after that
  // a diff runs only on an explicit Compare click (so typing a file path doesn't trigger surprise loads). When a seed
  // is steering us, the seed effect below owns the first diff instead.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { if (!seed) void compare(); }, []);

  // Adopt the seed: point Source/Target at what you're editing vs querying and run the diff. Guarded so it fires once
  // per nonce and never clobbers a comparison the user already configured.
  useEffect(() => {
    if (!seed || seed.nonce === consumedNonce.current) return;
    consumedNonce.current = seed.nonce;
    setForceReviewNonce((n) => n + 1);   // every seeded entry (footer / status bar) lands on Review
    if (touched) return;   // respect a hand-built comparison; the nonce is consumed so it won't reapply later
    setLeft(seed.left);
    // A seed with no Target (the attached dataset couldn't be named) fills only the Source and surfaces its note —
    // we do NOT auto-diff, so the user picks the Target rather than compare against an arbitrary dataset.
    if (seed.right) { setRight(seed.right); setSeedNote(null); void compare(seed.left, seed.right); }
    else { setSeedNote(seed.note ?? null); }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [seed?.nonce]);

  async function compare(l: ModelRef = left, r: ModelRef = right) {
    setBusy('compare'); setErr(null); setFailed(undefined); setPending(null); setResult(null);
    const includeEqual = !onlyDiffs;   // honour the current toggle so a fresh diff already carries identical objects if wanted
    try {
      const d = await rpc<ModelDiff>('compareModels', l, r, includeEqual);
      if (d.error) { setErr(d.error); setDiff(null); return; }
      setDiff(d); setEqualsLoaded(includeEqual);
      // default: every CHANGE selected (ALM-style), except Delete against a published model starts unticked.
      // Identical objects are never selectable — they carry no action.
      const changes = d.items.filter((i) => i.action !== 'Equal');
      const preselected = r.kind === 'workspace' ? changes.filter((i) => i.action !== 'Delete') : changes;
      setSelected(new Set(preselected.map((i) => i.ref)));
    } catch (e) { setErr(String((e as Error).message ?? e)); setDiff(null); }
    finally { setBusy(null); }
  }
  // #156: the human sign-in-and-retry for an XMLA target that had no live token. Runs the SAME interactive sign-in
  // connect_xmla uses (a browser pops), then re-runs the compare — which now reuses the freshly-cached token silently.
  async function signInAndRetry() {
    if (right.kind !== 'workspace' || !right.endpoint) return;
    setErr(null);
    const { ok, message } = await connectXmla(right.endpoint, right.database ?? '', 'interactive', right.tenantId ?? null);
    if (ok) void compare();
    else setErr(message || 'Sign-in did not complete. Try again, or open the Connections manager to sign in with an account that has access to this workspace.');
  }
  // Toggle "Only differences". OFF the first time fetches the identical objects (includeEqual) once, preserving the
  // current selection (identical refs are never added to it). ON just filters them out client-side — no round-trip.
  async function toggleOnlyDiffs(next: boolean) {
    // Turning it ON (filter to changes) is pure client-side; likewise OFF when the identical objects are already
    // loaded or there's no diff yet — commit immediately, no round-trip.
    if (next || equalsLoaded || !diff) { setOnlyDiffs(next); return; }
    // Turning it OFF for the first time must FETCH the identical objects. Commit onlyDiffs=false ONLY after that
    // fetch succeeds — flipping it early leaves diff.items holding only changes while the header claims
    // "0 identical shown" while no identical rows exist. On failure keep the toggle ON and show why.
    setEqualsLoading(true);
    try {
      const d = await rpc<ModelDiff>('compareModels', left, right, true);
      if (d.error) { setErr(d.error); return; }   // toggle stays ON (onlyDiffs unchanged)
      setDiff(d); setEqualsLoaded(true); setOnlyDiffs(false);   // selection preserved: it only ever held changed refs
    } catch (e) { setErr(String((e as Error).message ?? e)); }   // toggle stays ON
    finally { setEqualsLoading(false); }
  }
  // Validate (dry-run apply) — reports the count that would apply + any FailedRefs (e.g. a child whose parent
  // wasn't also selected). Mutates nothing.
  async function validate() {
    setBusy('validate'); setErr(null); setResult(null);
    try {
      const r = await rpc<ApplyDiffResult>('applyDiff', left, right, [...selected], false, 'human');
      if (r.error) { setErr(r.error); return; }
      setFailed(new Set(r.failedRefs ?? [])); setPending(r);
    } catch (e) { setErr(String((e as Error).message ?? e)); }
    finally { setBusy(null); }
  }
  // Confirm the apply — into a FILE (writes disk, no in-app undo) or the open MODEL (undoable merge).
  const clickWhy = 'Merging one object at a time is free. Pro merges everything you selected in one step.';
  async function applyConfirm() {
    const gated = tier === 'free' && selected.size > 1;
    setBusy('apply'); setErr(null); setUpsell(gated ? clickWhy : null);
    try {
      const r = await rpc<ApplyDiffResult>('applyDiff', left, right, [...selected], true, 'human', undefined, pending?.confirmToken);
      setPending(null); setResult(r); setFailed(new Set(r.failedRefs ?? []));
      if (r.error) { setErr(r.error); return; }
      setUpsell(null);
      // refresh the diff so the applied changes drop out of the view (keeping the result line)
      const d = await rpc<ModelDiff>('compareModels', left, right, !onlyDiffs);
      if (!d.error) { setDiff(d); setEqualsLoaded(!onlyDiffs); setSelected(new Set(d.items.filter((i) => i.action !== 'Equal').map((i) => i.ref))); }
    } catch (e) {
      // A free click on a bulk merge (>1 object, file or open-model target) gets the plain invitation at the click.
      if (isEntitlementError(e)) {
        setUpsell(clickWhy);
        queueMicrotask(() => gatedWhyRef.current?.scrollIntoView({ block: 'nearest' }));
      }
      else setErr(String((e as Error).message ?? e));
    }
    finally { setBusy(null); }
  }

  const changeCount = diff ? diff.created + diff.updated + diff.deleted : 0;
  // What Confirm actually SUBMITS is [...selected] — and the engine's Pro gates count the SELECTED diff refs
  // (items.Count > 1 in ApplyDiffIntoSessionAsync; applicable.Length > 1 on the file path), NOT the preview's
  // applyable count. When would-fail refs stay selected, pending.count undercounts the gate basis — so the badge
  // and tooltip must follow selected.size or a free "Merge 1" shows unbadged and then gets the Pro refusal.
  // (diff.items carry only non-Equal actions and `selected` only ever holds item refs, so selected.size IS the basis.)
  const submitCount = selected.size;
  const targetIsSession = right.kind === 'session';
  const targetIsWorkspace = right.kind === 'workspace';
  const targetIsApplyable = targetIsFile || targetIsSession || targetIsWorkspace;
  const targetLabel = targetIsSession ? 'the open model' : targetIsWorkspace ? (right.database || right.label || 'the published model') : (right.path || 'the target file');

  return (
    <div className={embedded ? '' : 'h-full overflow-auto'} style={{ color: 'var(--sem-fg)' }}>
      <div className={`${embedded ? '' : 'm-3'} rounded-lg p-3`} style={{ background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
        <div className="flex items-baseline gap-2 mb-2">
          <span className="text-[13px] font-semibold">{embedded ? 'Choose what to publish' : 'Compare'}</span>
          <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{embedded ? 'review the exact model diff, validate the selection, then confirm the write' : 'diff any two models · drill summary → object → property → code · merge selected changes into the open model or a file'}</span>
        </div>
        <div className="flex items-center gap-2 flex-wrap text-[12px] mb-2">
          <span style={{ color: 'var(--sem-muted)' }}>Source</span>
          <ModelRefPicker value={left} records={xmlaRecords} onAddConnection={openConnections} onChange={(r) => { touch(); setLeft(r); setDiff(null); }} />
          <span style={{ color: 'var(--sem-accent)' }}>→</span>
          <span style={{ color: 'var(--sem-muted)' }}>Target</span>
          <ModelRefPicker value={right} records={xmlaRecords} onAddConnection={openConnections} onChange={(r) => { touch(); setRight(r); setDiff(null); }} />
          <button onClick={() => { touch(); const a = left, b = right; setLeft(b); setRight(a); setDiff(null); }} title="swap source/target" className="px-1.5 py-0.5 rounded text-[12px]" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>⇄</button>
          <Btn primary onClick={() => { touch(); void compare(); }} busy={busy === 'compare'}>{embedded ? 'Review' : 'Compare'}</Btn>
        </div>

        {seedNote && <div className="text-[12px] mb-2 rounded px-2 py-1" style={{ background: 'color-mix(in srgb,var(--sem-warn) 12%, transparent)', color: 'var(--sem-warn)', border: '1px solid color-mix(in srgb,var(--sem-warn) 40%, transparent)' }}>{seedNote}</div>}
        {err && (
          <div className="text-[12px] mb-2 rounded px-2 py-1" style={{ background: 'color-mix(in srgb,var(--sem-bad) 12%, transparent)', color: 'var(--sem-bad)' }}>
            <div>{err}</div>
            {/* #156: a sign-in error is not a dead end — offer the one-click sign-in (or name the Connections manager). */}
            {isSignInError(err) && (
              <div className="mt-1.5 flex items-center gap-2">
                {right.kind === 'workspace' && right.endpoint
                  ? <Btn primary onClick={signInAndRetry} busy={connBusy}>Sign in and retry</Btn>
                  : <span style={{ color: 'var(--sem-muted)' }}>Open the <b>Connections</b> manager to sign in, then run the compare again.</span>}
              </div>
            )}
          </div>
        )}
        {upsell && !pending && <div className="mb-2"><UpsellNotice onDismiss={() => setUpsell(null)}>{upsell}</UpsellNotice></div>}

        {diff && (
          <>
            {/* The Model Diff view — one view, two modes (Review / Side by side). Changing the selection
                invalidates any pending preview — its counts (and the gate they predict) describe the validated
                selection, so Confirm can never submit a different set. */}
            <DiffView diff={diff} leftLabel={left.label ?? diff.leftLabel ?? 'source'} rightLabel={right.label ?? diff.rightLabel ?? 'target'}
              selected={selected} onSelectedChange={(next) => { setSelected(next); setPending(null); }} failed={failed}
              onlyDiffs={onlyDiffs} onToggleOnlyDiffs={toggleOnlyDiffs} equalsLoading={equalsLoading} forceReviewNonce={forceReviewNonce} />
            {changeCount > 0 && (
              <div className="flex items-center gap-2 mt-2 text-[12px] flex-wrap">
                <div className="ml-auto flex items-center gap-2">
                  {!targetIsApplyable && <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>To apply, set the Target to a <b>file</b>, the <b>working copy</b>, or a <b>published model</b>.</span>}
                  <Btn onClick={validate} busy={busy === 'validate'} disabled={!targetIsApplyable || selected.size === 0}>Validate selection</Btn>
                </div>
              </div>
            )}
            {pending && (
              <div className="mt-2 rounded p-2 text-[12px]" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-warn)' }}>
                <div style={{ color: 'var(--sem-muted)' }}>{pending.note || `${pending.count} change(s) would apply into ${targetLabel}.`}</div>
                {(pending.failedRefs?.length ?? 0) > 0 && <div className="mt-0.5" style={{ color: 'var(--sem-bad)' }}>{pending.failedRefs.length} cannot apply (highlighted above): a missing parent, or an unsupported type. They still count as selected changes, so deselect them to merge only what applies.</div>}
                {targetIsSession
                  ? <div className="mt-1" style={{ color: 'var(--sem-muted)' }}>Merges into the open model; <b>undoable</b> (one Ctrl+Z reverts the whole merge).</div>
                  : targetIsWorkspace
                    ? <div className="mt-1" style={{ color: 'var(--sem-warn)' }}>Writes to the published model. A restore point is saved first. Nothing is removed unless you selected a Delete.</div>
                    : <div className="mt-1" style={{ color: 'var(--sem-warn)' }}>Writes <span className="font-mono">{right.path}</span> on disk; there is no in-app undo (git is the safety net).</div>}
                {upsell && (
                  <div className="mt-1.5" data-testid="compare-pro-gated-why" ref={gatedWhyRef}>
                    <UpsellNotice onDismiss={() => setUpsell(null)}>{upsell}</UpsellNotice>
                  </div>
                )}
                <div className="flex items-center gap-2 mt-1.5">
                  <Btn primary onClick={applyConfirm} busy={busy === 'apply'} disabled={pending.count === 0}
                    title={tier === 'free' && submitCount > 1
                      ? 'Pro merges everything you selected in one step. Merging one object at a time stays free.'
                      : undefined}>
                    {targetIsSession
                      ? (submitCount > pending.count ? `Merge ${pending.count} of ${submitCount} selected → open model` : `Merge ${pending.count} → open model`)
                      : targetIsWorkspace
                        ? `Push ${pending.count} to the published model`
                        : `Apply ${pending.count} → file`}
                    <ProBadge show={tier === 'free' && submitCount > 1} variant="onAccent" />
                  </Btn>
                  <button className="text-[11px] underline" onClick={() => { setPending(null); setFailed(undefined); }} style={{ color: 'var(--sem-muted)' }}>Cancel</button>
                </div>
              </div>
            )}
            {result && !pending && (
              <div className="mt-2 text-[12px] rounded px-2 py-1" style={{ background: 'color-mix(in srgb,var(--sem-' + (result.error ? 'bad' : 'good') + ') 12%, transparent)', color: result.error ? 'var(--sem-bad)' : 'var(--sem-good)' }}>
                {result.error || `✓ applied ${result.count} change(s) into ${result.target || 'the target'}${(result.failedRefs?.length ?? 0) > 0 ? ` · ${result.failedRefs.length} failed` : ''}.`}
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}

// A source/target model picker: the open model, a file/PBIP path, a git ref of the open model's repo, or a remembered
// published model chosen from the connection registry. An endpoint is NEVER typed here — the Workspace side SELECTS a
// record (Add connection is the only place an endpoint is typed; docs/design-records-2026-07-12.md T13/R8).
function ModelRefPicker({ value, onChange, records, onAddConnection }: {
  value: ModelRef; onChange: (r: ModelRef) => void; records: ConnRecord[]; onAddConnection: () => void;
}) {
  const setKind = (kind: string) => onChange(
    kind === 'session' ? { kind: 'session' }
    : kind === 'file' ? { kind: 'file', path: value.path ?? '' }
    // Preselect the first remembered model so the picker is immediately usable; no records yet ⇒ an endpoint-less
    // workspace ref whose picker offers "Add a published model" (an endpoint is never seeded from free text).
    : kind === 'workspace' ? (records[0] ? workspaceRef(records[0]) : { kind: 'workspace', endpoint: '' })
    : { kind: 'gitref', gitRef: value.gitRef ?? 'HEAD' });
  const inp = PICKER_INPUT;
  return (
    <span className="inline-flex items-center gap-1">
      <select value={value.kind} onChange={(e) => setKind(e.target.value)}
        style={{ ...inp, padding: '2px 4px' }}>
        <option value="session">● working copy</option>
        <option value="file">File…</option>
        <option value="gitref">Git ref…</option>
        <option value="workspace">Published model…</option>
      </select>
      {value.kind === 'file' && (
        <input value={value.path ?? ''} onChange={(e) => onChange({ kind: 'file', path: e.target.value })} placeholder=".bim / .pbip / TMDL folder" spellCheck={false} style={{ ...inp, width: 220 }} />
      )}
      {value.kind === 'gitref' && (
        <input value={value.gitRef ?? ''} onChange={(e) => onChange({ kind: 'gitref', gitRef: e.target.value })} placeholder="HEAD / branch / commit" spellCheck={false} style={{ ...inp, width: 150 }} />
      )}
      {value.kind === 'workspace' && (
        <WorkspacePicker value={value} records={records} onChange={onChange} onAddConnection={onAddConnection} />
      )}
    </span>
  );
}

// The Published-model side of the picker: SELECTS a remembered model from the connection registry — no endpoint is ever
// typed. Each row shows the model's environment chip (uat / prod / local, else the fail-closed "Production safeguards"),
// so you see what you are about to diff against before you diff it. If the model you want is not remembered yet, "Add a
// published model" opens the shared Connections manager; adding it there makes it appear in this list.
function WorkspacePicker({ value, records, onChange, onAddConnection }: {
  value: ModelRef; records: ConnRecord[]; onChange: (r: ModelRef) => void; onAddConnection: () => void;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLSpanElement>(null);
  const selected = records.find((r) => r.endpoint === value.endpoint && (r.database ?? '') === (value.database ?? ''));
  // A value carried in from a seed (a publish/live destination) may not be in the fetched list yet; show its own label so
  // the selection reads honestly, with the fail-closed "Production safeguards" chip for an unlabelled cloud model.
  const heading = selected ? recordName(selected) : (value.endpoint ? (value.label || shortEndpoint(value.endpoint)) : 'Choose a published model');
  const headingChip = selected ? envLabel(selected) : (value.endpoint ? 'Production safeguards' : null);
  // A lightweight menu (no focus trap — the surrounding selects stay usable): close on Escape or a click outside it.
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', onDown); document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('mousedown', onDown); document.removeEventListener('keydown', onKey); };
  }, [open]);
  const chip = (text: string) => <span className="rounded px-1 py-0.5 text-[9px] whitespace-nowrap" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>{text}</span>;
  return (
    <span ref={ref} className="relative inline-flex items-center gap-1">
      <button type="button" data-testid="compare-workspace-picker" onClick={() => setOpen((v) => !v)} aria-haspopup="listbox" aria-expanded={open}
        className="inline-flex items-center gap-1.5" style={{ ...PICKER_INPUT, minWidth: 200, maxWidth: 300 }}>
        <span className="min-w-0 flex-1 truncate text-left">{heading}</span>
        {headingChip && chip(headingChip)}
        <span style={{ color: 'var(--sem-muted)' }}>▾</span>
      </button>
      {open && (
        <div role="listbox" className="absolute z-50 rounded" style={{ top: '100%', left: 0, marginTop: 4, minWidth: 260, maxWidth: 360, background: 'var(--sem-surface)', border: '1px solid var(--sem-border)', boxShadow: '0 6px 20px rgba(0,0,0,0.35)' }}>
          <div style={{ maxHeight: 240, overflow: 'auto' }}>
            {records.length === 0 && <div className="px-2 py-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>No published models are remembered yet.</div>}
            {records.map((r) => (
              <button type="button" key={r.id} role="option" aria-selected={selected?.id === r.id}
                onClick={() => { onChange(workspaceRef(r)); setOpen(false); }}
                className="flex w-full items-center gap-2 px-2 py-1.5 text-left text-[12px]"
                style={{ background: selected?.id === r.id ? 'var(--sem-accent-soft)' : 'transparent', border: 'none', color: 'var(--sem-fg)', cursor: 'pointer' }}>
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-medium">{recordName(r)}</span>
                  <span className="block truncate text-[10px]" style={{ color: 'var(--sem-muted)' }}>{r.database || shortEndpoint(r.endpoint)}</span>
                </span>
                {chip(envLabel(r))}
              </button>
            ))}
          </div>
          <button type="button" onClick={() => { setOpen(false); onAddConnection(); }}
            className="flex w-full items-center px-2 py-1.5 text-left text-[11px] font-semibold"
            style={{ borderTop: '1px solid var(--sem-border)', background: 'transparent', color: 'var(--sem-accent)', cursor: 'pointer' }}>
            + Add a published model
          </button>
        </div>
      )}
    </span>
  );
}

function Btn({ children, onClick, primary, busy, disabled, title }: { children: React.ReactNode; onClick: () => void; primary?: boolean; busy?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={busy || disabled} title={title}
      className="px-2.5 py-1 rounded text-[12px] font-medium disabled:opacity-50"
      style={{ background: primary ? 'var(--sem-accent)' : 'var(--sem-surface-2)', color: primary ? '#000' : 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {busy ? '…' : children}
    </button>
  );
}
function Badge({ color, children }: { color: string; children: React.ReactNode }) {
  return <span className="text-[9px] px-1 rounded align-middle" style={{ background: color, color: '#000' }}>{children}</span>;
}
