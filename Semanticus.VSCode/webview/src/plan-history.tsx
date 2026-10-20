import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { copyText, onActivity, onDidChange, onPlanChange, onReconnect, rpc } from './bridge';
import { Evidence } from './evidence';
import * as M from './plan-history-model';
import './plan-history.css';
import { RunEntryActions } from './workflow-run';
import { HistoryMotion } from './personality/motion/history';

// ===================================================================================================
// History inside the Plan (Plan-heart contract 8, mockup plan-heart.html section 4).
//
// One time-ordered list, newest first, read from the engine's merged History (getPlanHistory), so the page and an
// assistant calling get_plan_history see the same rows. Who made each edit is a word and a mark; edits sit under
// the plan item, run, draft or task that caused them; Undo to here and Redo to here walk the one shared undo stack
// in ONE call (undo/redo with toEntry, contract 8.6), never N single steps from the page. Saved versions are flag
// rows with Restore and exactly one confirmation. The Verified word and its evidence live in each row's details.
//
// Every undo fact (undoable, laterCount, undone, redoable) is the engine's. This file only draws and words them;
// the words live in plan-history-model.ts, which test/plan-history.test.mjs executes.
// ===================================================================================================

interface UndoState { canUndo: boolean; canRedo: boolean }
interface HistoryCheckpointResult { committed: boolean; checkpoint?: { shortHash: string; label: string }; error?: string }
interface HistoryRestoreResult { restored: boolean; target?: { label: string; when: string }; note?: string; error?: string; sessionId?: string }

const PAGE = 50;

export interface PlanHistoryProps {
  /** The session this page is on. A restore's landing line is shown only on the session that restore reopened. */
  sessionId?: string | null;
  /** Open filtered to one object ("History for one object"): the Overview's object History link sends this. */
  objectRef?: string | null;
  /** Open with this entry's details showing (evidence "Show this edit", a Plan row's last change). */
  focusEntry?: string | null;
  /** Go to another Studio page (Publish > Published for published versions). */
  onGo?: (tab: string) => void;
  /** The page that does the work of the plan items with these ids, when the Plan knows one: a row's
   *  cause names item ids and only the Plan holds each item's page, so the Plan answers. */
  pageOf?: (ids: string[]) => { tool: string; label: string } | null;
}

export function PlanHistory({ sessionId = null, objectRef: objectRefProp = null, focusEntry = null, onGo, pageOf }: PlanHistoryProps) {
  const [who, setWho] = useState<M.WhoFilter>('all');
  const [objectRef, setObjectRef] = useState<string | null>(objectRefProp);
  const [view, setView] = useState<M.HistoryView | null>(null);
  const [older, setOlder] = useState<M.HistoryRow[]>([]);
  const [more, setMore] = useState(false);
  const [chain, setChain] = useState<M.VerifiedEditsChain | null>(null);
  const [checkpoints, setCheckpoints] = useState<M.HistoryCheckpointList | null>(null);
  const [open, setOpen] = useState<Set<string>>(new Set());
  const [selected, setSelected] = useState<string | null>(focusEntry);
  const [ask, setAsk] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // A restore this page started reopened the model, and the page was remounted on the new session: say here what
  // the restore said, since the page that asked is gone.
  const [landing] = useState(() => M.takeRestoreLanding(sessionId));
  const [msg, setMsgText] = useState<string | null>(landing?.msg ?? null);
  const [msgTone, setMsgTone] = useState<'warn' | 'bad' | null>(landing?.tone ?? null);
  // Every other line on this page is plain; only a restore's answer sets a tone, right after it says the line.
  const setMsg = (text: string | null) => { setMsgText(text); setMsgTone(null); };
  const [menu, setMenu] = useState(false);
  const [saving, setSaving] = useState<string | null>(null);   // the name being typed; null when the form is shut
  const savingNow = useRef<string | null>(null); savingNow.current = saving;   // what the box holds when the engine answers
  const [failed, setFailed] = useState<string | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => { setObjectRef(objectRefProp); }, [objectRefProp]);
  useEffect(() => { if (focusEntry) setSelected(focusEntry); }, [focusEntry]);
  useEffect(() => { const t = window.setInterval(() => setNow(Date.now()), 60000); return () => window.clearInterval(t); }, []);

  // One pull reads all three answers: the History's rows, and two side facts the rows do not carry (whether the
  // audit record is intact and how long it is, and whether versions can be saved here). A failed side read is silence
  // (no model, an older engine): the list still draws. A failed History read is said once, plainly.
  const seq = useRef(0);
  const pull = useCallback(async () => {
    const mine = ++seq.current;
    const [h, c, k] = await Promise.allSettled([
      rpc<M.HistoryView>('getPlanHistory', M.whoParam(who), objectRef, null, PAGE),
      rpc<M.VerifiedEditsChain>('listVerifiedEdits'),
      rpc<M.HistoryCheckpointList>('listHistoryCheckpoints', 50),
    ]);
    if (mine !== seq.current) return;
    if (h.status === 'fulfilled') { setView(h.value); setOlder([]); setMore(!!h.value?.more); setFailed(null); }
    else setFailed('History could not be opened. ' + String((h.reason as Error)?.message ?? h.reason));
    setChain(c.status === 'fulfilled' ? c.value : null);
    setCheckpoints(k.status === 'fulfilled' ? k.value : null);
  }, [who, objectRef]);

  useEffect(() => {
    let timer: number | undefined;
    const schedule = () => { window.clearTimeout(timer); timer = window.setTimeout(() => { void pull(); }, 300); };
    void pull();
    const offs = [onDidChange(schedule), onPlanChange(schedule), onReconnect(schedule),
      onActivity((e) => { if (e.kind === 'verified_edit') schedule(); })];
    return () => { window.clearTimeout(timer); offs.forEach((off) => off()); };
  }, [pull]);

  // The menu closes on any click outside it.
  useEffect(() => {
    if (!menu) return;
    const close = (e: MouseEvent) => { if (!menuRef.current?.contains(e.target as Node)) setMenu(false); };
    window.addEventListener('mousedown', close);
    return () => window.removeEventListener('mousedown', close);
  }, [menu]);

  // The rows are the engine's answer as it gave them (contract 8.8): both doors read one History.
  const rows = useMemo(() => (view ? [...view.rows, ...older] : []), [view, older]);
  const lines = useMemo(() => M.timelineLines(rows, open), [rows, open]);
  const found = useMemo(() => findRow(rows, selected), [rows, selected]);
  const undone = M.undoneCount(rows);
  const supported = checkpoints?.supported ?? false;
  const records = chain?.records.length ?? 0;
  const broken = chain != null && !chain.chainIntact;

  // Selecting an edit inside a closed parent opens the parent, so the selected row is always on screen.
  useEffect(() => { if (found?.parent && !open.has(found.parent.id)) setOpen((o) => new Set(o).add(found.parent!.id)); }, [found]); // eslint-disable-line react-hooks/exhaustive-deps

  const act = async (row: M.HistoryRow) => {
    const a = M.rowAction(row);
    if (a.kind === 'restore') {
      if (ask === row.id) { setAsk(null); return; }
      // The one confirmation is the engine's dry run (contract 8.7): a version that cannot be restored says so here,
      // before the question is asked, never after the person has said yes.
      setBusy(true); setMsg(null);
      try {
        const preview = await rpc<HistoryRestoreResult>('restoreHistoryCheckpoint', a.hash, false, 'human');
        if (preview.error) setMsg(preview.error); else setAsk((cur) => M.askRestore(cur, row.id));
      } catch (e) { setMsg(String((e as Error).message ?? e)); }
      finally { setBusy(false); }
      return;
    }
    if (a.kind !== 'undo' && a.kind !== 'redo') return;
    setBusy(true); setMsg(null); setAsk(null);
    try {
      // ONE call with toEntry (contract 8.6): the engine walks every step in one commit and one broadcast.
      if (a.kind === 'undo') await rpc<UndoState>('undo', 'human', row.id);
      else await rpc<UndoState>('redo', 'human', row.id);
      await pull();
    }
    catch (e) { setMsg(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };

  const redoAll = async () => {
    const target = M.redoAllTarget(rows);
    if (!target) return;
    setBusy(true); setMsg(null);
    try { await rpc<UndoState>('redo', 'human', target); await pull(); }
    catch (e) { setMsg(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };

  const restore = async (row: M.HistoryRow) => {
    if (!row.detail) return;
    setBusy(true); setMsg(null);
    try {
      const r = await rpc<HistoryRestoreResult>('restoreHistoryCheckpoint', row.detail, true, 'human');
      // Restored first: files restored with a failed commit is still a restore, so the question closes and the
      // error is a warning. On a clean restore the Restored row on top says what happened.
      const o = M.restoreOutcome(r);
      if (r.restored) M.noteRestoreLanding(o, r.sessionId);
      if (o.settled) { setAsk(null); setSelected(null); }
      if (o.msg) { setMsg(o.msg); setMsgTone(o.tone); }
      await pull();
    } catch (e) { setMsg(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };

  const save = async () => {
    setBusy(true); setMsg(null);
    const sent = saving;
    try {
      const r = await rpc<HistoryCheckpointResult>('createHistoryCheckpoint', saving?.trim() || null, true, 'human');
      // The form ends only when the box still holds the name that was sent; a name typed while the write was on its way stays.
      if (r.error) setMsg(r.error); else { if (savingNow.current === sent) setSaving(null); setMsg('Saved.'); }
      await pull();
    } catch (e) { setMsg(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  };

  const exportAs = async (format: 'md' | 'json') => {
    setMenu(false); setMsg(null);
    try {
      const text = await rpc<string>('exportVerifiedEdits', format);
      setMsg((await copyText(text)) ? `Copied the record as ${format === 'md' ? 'Markdown' : 'JSON'}.` : 'Could not copy to the clipboard.');
    } catch (e) { setMsg(String((e as Error).message ?? e)); }
  };

  const showOlder = async () => {
    const last = rows.filter((r) => view?.rows.includes(r) || older.includes(r)).pop();
    if (!last) return;
    try {
      const page = await rpc<M.HistoryView>('getPlanHistory', M.whoParam(who), objectRef, last.when, PAGE);
      setOlder((o) => [...o, ...(page.rows ?? [])]); setMore(!!page.more);
    } catch (e) { setMsg(String((e as Error).message ?? e)); }
  };

  const jump = (revision: number) => {
    const hit = M.rowForRevision(rows, view?.sessionId, revision);
    if (hit) setSelected(hit.row.id);
  };

  const restoreFor = (row: M.HistoryRow) => M.restoreQuestion(M.rowTitle(row).replace(/^Saved version: /, ''), row.when, now);

  return (
    <div className="ph" data-testid="plan-history">
      <HistoryMotion rows={rows} sessionId={view?.sessionId} partial={more || !!objectRef || who !== 'all'} />
      <div className="ph-top">
        <div className="sem-seg" role="group" aria-label="Who made the change">
          {M.WHO_FILTERS.map((f) => (
            <button key={f.id} type="button" className="sem-seg-item" aria-pressed={who === f.id}
              data-testid={`ph-who-${f.id}`} onClick={() => { setWho(f.id); setSelected(null); setAsk(null); }}>{f.label}</button>
          ))}
        </div>
        <div className="ph-top-r">
          {records > 0 && !broken && <span className="ph-intact" data-testid="ph-intact"><Check />Verified record intact</span>}
          {supported && <button type="button" className="sem-btn sem-btn-sm" data-testid="ph-save"
            onClick={() => setSaving((s) => (s == null ? '' : null))} aria-expanded={saving != null}>Save a version</button>}
          <div className="ph-menu-wrap" ref={menuRef}>
            <button type="button" className="sem-btn sem-btn-sm" aria-label="More" aria-haspopup="menu" aria-expanded={menu}
              data-testid="ph-more" onClick={() => setMenu((m) => !m)}>···</button>
            {menu && (
              <div className="compact-menu ph-menu" role="menu" data-testid="ph-menu">
                <button type="button" role="menuitem" onClick={() => void exportAs('md')}>Export as Markdown</button>
                <button type="button" role="menuitem" onClick={() => void exportAs('json')}>Export as JSON</button>
                <span className="ph-menu-sep" role="separator" />
                <button type="button" role="menuitem" className="ph-menu-quiet" onClick={() => { setMenu(false); onGo?.('deploy'); }}>
                  Published versions are in Publish
                </button>
              </div>
            )}
          </div>
        </div>
      </div>

      {broken && (
        <div className="ph-broken" role="alert" data-testid="ph-broken">
          <b>{chain!.firstBrokenSeq > 0 ? `The verified record is broken at record ${chain!.firstBrokenSeq}.` : 'The verified record could not be opened.'}</b>{' '}
          {chain!.firstBrokenSeq > 0
            ? 'A record was edited or removed after it was written. Nothing from that point on can be trusted.'
            : 'The stored record is damaged. None of it can be trusted.'}
        </div>
      )}
      {view && view.kept === false && <div className="ph-note">This model is not saved yet, so this history lasts until it closes.</div>}
      {saving != null && (
        <form className="ph-save" onSubmit={(e) => { e.preventDefault(); void save(); }} data-testid="ph-save-form">
          <input className="sem-field sem-field-sm" value={saving} autoFocus spellCheck={false} aria-label="Version name"
            placeholder="Name this version (optional)" onChange={(e) => setSaving(e.target.value)} />
          <button type="submit" className="sem-btn sem-btn-sm sem-btn-primary" disabled={busy}>Save</button>
          <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => setSaving(null)}>Cancel</button>
        </form>
      )}
      {objectRef && (
        <div className="ph-note ph-filterline" data-testid="ph-object">
          <span>Showing changes to <b>{M.objectName(objectRef)}</b></span>
          <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => setObjectRef(null)}>Show every change</button>
        </div>
      )}
      {undone > 0 && (
        <div className="ph-bar" data-testid="ph-undone-bar">
          <span>{M.undoneBarText(undone)}</span>
          <button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={() => void redoAll()} data-testid="ph-redo-all">Redo all</button>
        </div>
      )}
      {(msg || failed) && (() => {
        const tone = failed ? null : msgTone;
        return <div className={'ph-note' + (tone ? ` ph-note-${tone}` : '')} role={tone ? 'alert' : 'status'} data-tone={tone ?? undefined}
          data-testid="ph-msg">{failed ?? msg}</div>;
      })()}

      <div className={'ph-body' + (found ? ' ph-split' : '')}>
        <div className="ph-list">
          {view && rows.length === 0 ? (
            <div className="ph-empty" data-testid="ph-empty">{objectRef ? 'No changes to this object yet.' : M.EMPTY_SENTENCE}</div>
          ) : (
            <HistoryTimeline lines={lines} now={now} selected={selected} ask={ask} busy={busy} restoreFor={restoreFor}
              onSelect={(id) => { setSelected((s) => (s === id ? null : id)); }}
              onToggle={(id) => setOpen((o) => { const n = new Set(o); if (n.has(id)) n.delete(id); else n.add(id); return n; })}
              onAction={(row) => void act(row)}
              onConfirmRestore={(row) => void restore(row)} onCancelRestore={() => setAsk(null)} />
          )}
          {more && <button type="button" className="sem-btn sem-btn-sm ph-older" onClick={() => void showOlder()}>Show older changes</button>}
        </div>
        {found && (
          <HistoryDetail row={found.row} parent={found.parent} chain={chain} now={now} busy={busy} sessionId={view?.sessionId}
            onClose={() => setSelected(null)} onAction={(row) => void act(row)}
            onObject={(ref) => { setObjectRef(ref); setSelected(null); }} onJump={jump} pageOf={pageOf} onGo={onGo} />
        )}
      </div>
    </div>
  );
}

function findRow(rows: M.HistoryRow[], id: string | null): { row: M.HistoryRow; parent?: M.HistoryRow } | null {
  if (!id) return null;
  for (const r of rows) {
    if (r.id === id) return { row: r };
    for (const c of r.children ?? []) if (c.id === id) return { row: c, parent: r };
  }
  return null;
}

// ---- the list -------------------------------------------------------------------------------------------

export interface TimelineProps {
  lines: M.Line[];
  now: number;
  selected: string | null;
  ask: string | null;
  busy: boolean;
  restoreFor: (row: M.HistoryRow) => { question: string; text: string } | null;
  onSelect: (id: string) => void;
  onToggle: (id: string) => void;
  onAction: (row: M.HistoryRow) => void;
  onConfirmRestore: (row: M.HistoryRow) => void;
  onCancelRestore: () => void;
}

export function HistoryTimeline({ lines, now, selected, ask, busy, restoreFor, onSelect, onToggle, onAction, onConfirmRestore, onCancelRestore }: TimelineProps) {
  const firstRow = lines.findIndex((l) => l.type === 'row');
  return (
    <div className="ph-rows" role="list">
      {lines.map((line, i) => {
        if (line.type === 'seam') {
          const s = line.which === 'restore' ? M.RESTORE_SEAM : M.SESSION_SEAM;
          return <div key={'seam-' + line.which} className="ph-seam" role="separator" data-testid={`ph-seam-${line.which}`}><b>{s.title}</b>{s.text}</div>;
        }
        const { row, parent } = line;
        const q = ask === row.id ? restoreFor(row) : null;
        return (
          <div key={row.id + ':' + i} role="listitem" data-tour={i === firstRow ? 'history-row' : undefined}>
            <HistoryRowView row={row} parent={parent} now={now} selected={selected === row.id} busy={busy}
              open={!!(row.children && row.children.length) && lines.some((l) => l.type === 'row' && l.parent?.id === row.id)}
              onSelect={onSelect} onToggle={onToggle} onAction={onAction} />
            {q && (
              <div className="ph-confirm" data-testid="ph-restore-confirm">
                <span><b>{q.question}</b> {q.text}</span>
                <span className="ph-confirm-acts">
                  <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={busy} onClick={() => onConfirmRestore(row)}>Restore this version</button>
                  <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={onCancelRestore}>Cancel</button>
                </span>
              </div>
            )}
          </div>
        );
      })}
    </div>
  );
}

function HistoryRowView({ row, parent, now, selected, busy, open, onSelect, onToggle, onAction }: {
  row: M.HistoryRow; parent?: M.HistoryRow; now: number; selected: boolean; busy: boolean; open: boolean;
  onSelect: (id: string) => void; onToggle: (id: string) => void; onAction: (row: M.HistoryRow) => void;
}) {
  const kid = !!parent;
  const who = M.whoOf(row.by);
  const many = !!(row.children && row.children.length);
  const ver = kid ? null : M.verifiedWord(row.verified);
  const a = M.rowAction(row);
  const cls = 'ph-row' + (kid ? ' ph-kid' : '') + (row.kind === 'saved' ? ' ph-saved' : '') + (row.undone ? ' ph-undone' : '') + (row.notSaved ? ' ph-notsaved' : '') + (selected ? ' ph-sel' : '');
  return (
    <div className={cls} data-hid={row.id} data-kind={row.kind} data-undone={row.undone ? 'true' : undefined} data-notsaved={row.notSaved ? 'true' : undefined}
      onClick={() => onSelect(row.id)}>
      <span className="ph-time">{kid ? '' : M.clock(row.when, now)}</span>
      <span className="ph-who">
        {kid ? null : row.kind === 'saved' ? <span className="ph-flag"><Flag />Saved</span>
          : <span className={M.doerClass(who)}>{M.WHO_WORD[who]}</span>}
      </span>
      <span className="ph-what">
        {kid ? <span className="ph-kidtitle">{M.rowTitle(row, parent)}</span> : (
          <>
            {many
              ? <button type="button" className="ph-caret" aria-expanded={open} aria-label={open ? 'Hide the changes' : 'Show the changes'}
                  onClick={(e) => { e.stopPropagation(); onToggle(row.id); }}><Caret open={open} /></button>
              : <span className="ph-caret-gap" />}
            <b className="ph-title">{M.rowTitle(row)}</b>
            <span className="ph-meta">{M.metaLine(row)}</span>
          </>
        )}
      </span>
      <span className="ph-ver">{ver && <span className={'ph-word ph-' + ver.tone}>{ver.tone === 'good' ? <Check /> : <Dot />}{ver.word}</span>}</span>
      <span className="ph-act" onClick={(e) => e.stopPropagation()}>
        {a.kind === 'undo' && <>
          <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" disabled={busy} onClick={() => onAction(row)} data-testid={`ph-undo-${row.id}`}>Undo to here</button>
          {a.later > 0 && <span className="ph-later">{M.laterTag(a.later)}</span>}
        </>}
        {a.kind === 'redo' && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" disabled={busy} onClick={() => onAction(row)} data-testid={`ph-redo-${row.id}`}>Redo to here</button>}
        {a.kind === 'restore' && <button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={() => onAction(row)} data-testid={`ph-restore-${row.id}`}>Restore</button>}
      </span>
    </div>
  );
}

// ---- the detail panel -----------------------------------------------------------------------------------

export function HistoryDetail({ row, parent, chain, now, busy, sessionId, onClose, onAction, onObject, onJump, pageOf, onGo }: {
  row: M.HistoryRow; parent?: M.HistoryRow; chain: M.VerifiedEditsChain | null; now: number; busy: boolean; sessionId?: string | null;
  onClose: () => void; onAction: (row: M.HistoryRow) => void; onObject: (ref: string) => void; onJump: (revision: number) => void;
  pageOf?: PlanHistoryProps['pageOf']; onGo?: (tab: string) => void;
}) {
  const [evidence, setEvidence] = useState(false);
  useEffect(() => { setEvidence(false); }, [row.id]);
  const head = parent ?? row;
  const who = M.whoOf(head.by);
  const record = M.recordFor(row, chain) ?? (parent ? M.recordFor(parent, chain) : undefined);
  const ver = M.verifiedWord(record?.verdict ?? row.verified ?? head.verified);
  const table = M.changeRows(row);
  const a = M.rowAction(row);
  const foot = M.footLine(row, record, chain);
  const steps = row.report?.steps ?? [];
  const redLines = row.record && M.isRedCheck(row.record) ? M.redCheckLines(row.record) : [];
  const causeIds = (row.cause ?? parent?.cause)?.ids;
  const page = pageOf && causeIds?.length ? pageOf(causeIds) : null;
  return (
    <aside className="ph-detail" data-testid="ph-detail" aria-label="Change details">
      <div className="ph-dhead">
        <span className={M.doerClass(who)}>{M.WHO_WORD[who]}</span>
        <span className="ph-num">{M.clock(head.when, now)}</span>
        <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet ph-close" onClick={onClose}>Close</button>
      </div>
      <h4 className="ph-dtitle">{M.rowTitle(row, parent)}</h4>
      {redLines.length > 0
        ? redLines.map((l) => <div key={l} className="ph-dmeta ph-wrap">{l}</div>)
        : <div className="ph-dmeta">{parent ? 'Part of: ' + M.rowTitle(parent) + ' · ' + M.metaLine(parent) : M.metaLine(row)}</div>}
      {row.kind === 'decision' && row.detail && <div className="ph-dmeta">{row.detail}</div>}
      {page && (
        <div className="ph-dmeta">
          <button type="button" className="plan-link" data-testid="ph-detail-page" onClick={() => onGo?.(page.tool)}>Open in {page.label}</button>
        </div>
      )}

      {table.length > 0 && (
        <table className="ph-chg">
          <colgroup><col className="ph-c-obj" /><col className="ph-c-chg" /><col className="ph-c-val" /><col className="ph-c-val" /></colgroup>
          <thead><tr><th>Object</th><th>Change</th><th>Before</th><th>After</th></tr></thead>
          <tbody>
            {table.map((t, i) => (
              <tr key={t.ref + i}>
                <td><button type="button" className="ph-obj" title="Show only the changes to this object" onClick={() => onObject(t.ref)}>{t.object}</button></td>
                <td>{t.change}</td>
                <td className="ph-v ph-before">{t.before}</td>
                <td className="ph-v">{t.after}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {steps.length > 0 && (
        <ol className="ph-steps" data-testid="ph-run-steps">
          {steps.map((s) => (
            <li key={s.stepId}>
              <span>{s.title || s.stepId}</span>
              <span className="ph-dmeta">{[s.outcome, s.submittedBy ? M.WHO_WORD[M.whoOf(s.submittedBy)] : null,
                ...(s.checks ?? []).map((c) => (M.verifiedWord(c.verdict)?.word ?? c.verdict))].filter(Boolean).join(' · ')}</span>
            </li>
          ))}
        </ol>
      )}

      {ver && (
        <div className="ph-verline">
          {ver.tone === 'good' ? <Check /> : <Dot tone={ver.tone} />}
          <span><b>{ver.word}.</b> {record?.overrideReason ? '"' + record.overrideReason + '"' : M.recordWords(record)}{' '}
            {record && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet ph-inline" aria-expanded={evidence}
              onClick={() => setEvidence((v) => !v)}>Evidence</button>}</span>
        </div>
      )}
      {evidence && record && <div className="ph-evidence"><Evidence record={record} sessionId={sessionId ?? undefined} onJump={onJump} /></div>}
      {row.report?.evidence && (
        <div className="ph-acts"><button type="button" className="sem-btn sem-btn-sm" onClick={() => void copyText(row.report!.evidence!)}>Copy what the run did</button></div>
      )}
      {/* A finished workflow run (lane W2, contract 11.3): its sealed report and "Make it reusable" live here now. */}
      {row.kind === 'run' && row.report?.runId && <RunEntryActions report={row.report} />}

      <div className="ph-acts">
        {a.kind === 'undo' && <><button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={() => onAction(row)} data-testid="ph-detail-undo">Undo to here</button>
          <span className="ph-conseq">{M.undoSentence(a.later)}</span></>}
        {a.kind === 'redo' && <><button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={() => onAction(row)}>Redo to here</button>
          <span className="ph-conseq">{M.REDO_SENTENCE}</span></>}
        {a.kind === 'none' && a.why === 'session' && <span className="ph-conseq" data-testid={row.notSaved ? 'ph-notsaved-sentence' : undefined}>{row.notSaved ? M.NOT_SAVED_SENTENCE : M.BEFORE_SESSION_SENTENCE}</span>}
        {a.kind === 'none' && a.why === 'restore' && <span className="ph-conseq">{M.BEFORE_RESTORE_SENTENCE}</span>}
      </div>
      {foot && <div className="ph-foot">{foot}</div>}
    </aside>
  );
}

// ---- marks ----------------------------------------------------------------------------------------------

function Check() {
  return <svg className="ph-ico" viewBox="0 0 12 12" aria-hidden="true"><path d="M2.5 6.2l2.3 2.3 4.7-5" fill="none" stroke="currentColor" strokeWidth="1.5" /></svg>;
}
function Dot({ tone }: { tone?: M.Tone }) {
  return <span className={'ph-dot' + (tone ? ' ph-' + tone : '')} aria-hidden="true" />;
}
function Flag() {
  return <svg className="ph-ico" viewBox="0 0 12 12" aria-hidden="true"><path d="M3 11V1.5M3 2h6l-1.5 2.5L9 7H3" fill="none" stroke="currentColor" strokeWidth="1.3" /></svg>;
}
function Caret({ open }: { open: boolean }) {
  return open
    ? <svg className="ph-ico" viewBox="0 0 12 12" aria-hidden="true"><path d="M3 4.5l3 3 3-3" fill="none" stroke="currentColor" strokeWidth="1.4" /></svg>
    : <svg className="ph-ico" viewBox="0 0 12 12" aria-hidden="true"><path d="M4.5 3l3 3-3 3" fill="none" stroke="currentColor" strokeWidth="1.4" /></svg>;
}
