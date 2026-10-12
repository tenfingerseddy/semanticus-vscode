import { useEffect, useRef, useState } from 'react';
import { rpc, onActivity, type ChangeNotification } from '../bridge';
import { useActivity } from '../activity';
import type { HistoryView } from '../plan-history-model';
import { SaidModel, saidCopy, undoEntry, canUndoGroup, type SaidGroup } from './said-model';

const CHANGE = 'semanticus:assistant-edit';
export function noteAssistantChange(n: ChangeNotification) {
  window.dispatchEvent(new CustomEvent(CHANGE, { detail: n }));
}

// The notification follows the same call boundary as the driver. No second bridge listener is needed.
export function SaidLine({ onHistory }: { onHistory: (entry: string | undefined, sessionId: string) => void }) {
  const { addEdit } = useActivity();
  const [line, setLine] = useState<SaidGroup | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const latest = useRef<{ sessionId: string; revision: number } | null>(null);
  const alive = useRef(true);
  const shown = useRef<SaidGroup | null>(null);
  const archived = useRef(new Map<string, number>());
  useEffect(() => {
    alive.current = true;
    const model = new SaidModel();
    let quiet: number | undefined;
    let expiry: number | undefined;
    const flashes = new Map<Element, number>();
    const archive = () => {
      const group = shown.current;
      if (group && archived.current.get(group.id) !== group.lastRevision) {
        archived.current.set(group.id, group.lastRevision);
        addEdit(group);
      }
    };
    const call = (event: MessageEvent) => {
      if (event.data?.type !== 'assistantCall') return;
      const { sessionId, call, tool } = event.data.payload ?? {};
      if (typeof sessionId !== 'string' || typeof call !== 'string') return;
      model.startCall(sessionId, call, tool);
      window.clearTimeout(quiet);
    };
    const change = (event: Event) => {
      const n = (event as CustomEvent<ChangeNotification>).detail;
      latest.current = { sessionId: n.sessionId, revision: n.revision };
      model.change(n);
      if (!model.current) return;
      const group = model.current;
      if (shown.current && shown.current.id !== group.id) archive();
      shown.current = group;
      setLine(group); setError(null);
      window.clearTimeout(quiet);
      if (!group.plan && !group.words) quiet = window.setTimeout(() => model.endCall(), 4000);
      window.clearTimeout(expiry);
      expiry = window.setTimeout(() => { archive(); shown.current = null; setLine(null); }, 8000);
      const refs = new Set((n.deltas ?? []).map((d) => d.ref));
      // Only rows already painted can flash. This does not scroll, select, or open a page.
      document.querySelectorAll('[data-ref], [data-object-ref]').forEach((el) => {
        if (!refs.has(el.getAttribute('data-ref') ?? el.getAttribute('data-object-ref') ?? '')) return;
        window.clearTimeout(flashes.get(el));
        el.classList.remove('sem-flash');
        void (el as HTMLElement).offsetWidth;
        el.classList.add('sem-flash');
        flashes.set(el, window.setTimeout(() => { el.classList.remove('sem-flash'); flashes.delete(el); }, 3000));
      });
    };
    const offActivity = onActivity((e) => {
      const before = model.current;
      model.activity(e);
      if (!model.current || before === model.current) return;
      shown.current = model.current; setLine(model.current);
      window.clearTimeout(expiry);
      expiry = window.setTimeout(() => { archive(); shown.current = null; setLine(null); }, 8000);
    });
    window.addEventListener('message', call);
    window.addEventListener(CHANGE, change);
    return () => {
      alive.current = false;
      window.removeEventListener('message', call); window.removeEventListener(CHANGE, change);
      offActivity();
      window.clearTimeout(quiet); window.clearTimeout(expiry);
      flashes.forEach((timer, el) => { window.clearTimeout(timer); el.classList.remove('sem-flash'); });
    };
  }, [addEdit]);

  const undo = async () => {
    if (!line || busy) return;
    const group = line;
    setBusy(true); setError(null);
    try {
      const history = await rpc<HistoryView>('getPlanHistory', null, null, null, 200);
      if (!alive.current) return;
      const entry = undoEntry(history.rows, group);
      if (!entry || latest.current?.sessionId !== group.sessionId || !canUndoGroup(history.rows, group)) {
        onHistory(entry, group.sessionId);
        return;
      }
      const result = await rpc<{ historyRequired?: boolean }>('undo', 'human', entry, group.lastRevision);
      if (result.historyRequired) { onHistory(entry, group.sessionId); return; }
      if (alive.current && shown.current?.id === group.id) {
        addEdit(group); shown.current = null; setLine(null);
      }
    } catch (e) { if (alive.current) setError(String((e as Error).message ?? e)); }
    finally { if (alive.current) setBusy(false); }
  };
  if (!line) return null;
  return <div className="said-line" data-testid="said-line">
    <span role="status">{error || saidCopy(line)}</span>
    {!line.saved && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="said-undo"
      disabled={busy} onClick={() => void undo()}>Undo</button>}
  </div>;
}
