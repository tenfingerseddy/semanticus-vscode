import { useCallback, useEffect, useRef, useState } from 'react';
import { rpc } from './bridge';
import './connect-assistants.css';
import { NOTHING_LISTED, PANEL_LEAD, PANEL_TITLE, resultLines, rowView, type ConnectableAssistant, type ConnectAssistantResult } from './connect-assistants-model';

/** The "Connect another assistant" view in the header's assistant menu (1.5). It lists the assistants Semanticus can write a
 *  connection for, says which are on this computer, and one press per assistant writes it. The engine does the writing, so the
 *  assistant's own connect_assistant tool lands in the same place. */
export function ConnectAssistantsPanel({ onBack }: { onBack: () => void }) {
  const [list, setList] = useState<ConnectableAssistant[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [result, setResult] = useState<{ id: string; r: ConnectAssistantResult } | null>(null);
  const first = useRef<HTMLButtonElement>(null);

  const load = useCallback(() => {
    void rpc<ConnectableAssistant[]>('listAssistants').then((l) => { setList(Array.isArray(l) ? l : []); setFailed(false); }, () => setFailed(true));
  }, []);
  useEffect(load, [load]);
  useEffect(() => { if (list) first.current?.focus({ preventScroll: true }); }, [list === null]); // eslint-disable-line react-hooks/exhaustive-deps

  const press = (id: string) => {
    setBusy(id); setResult(null);
    void rpc<ConnectAssistantResult>('connectAssistant', id).then(
      (r) => { setResult({ id, r }); load(); },
      (e) => setResult({ id, r: { assistant: id, name: id, ok: false, changed: false, message: e instanceof Error ? e.message : 'Semanticus could not connect that assistant.' } }),
    ).finally(() => setBusy(null));
  };
  return <ConnectAssistantsView list={list} failed={failed} busy={busy} result={result} onPress={press} onBack={onBack} firstRef={first} />;
}

/** The picker itself, with nothing to fetch, so a test can draw every state. */
export function ConnectAssistantsView({ list, failed, busy, result, onPress, onBack, firstRef }: {
  list: ConnectableAssistant[] | null; failed: boolean; busy: string | null; result: { id: string; r: ConnectAssistantResult } | null;
  onPress: (id: string) => void; onBack: () => void; firstRef?: React.Ref<HTMLButtonElement>;
}) {
  return (
    <div className="connect-any" role="group" aria-label={PANEL_TITLE}
      onKeyDown={(e) => { if (e.key === 'Escape') { e.preventDefault(); onBack(); } }}>
      <div className="connect-any-head">
        <span>{PANEL_TITLE}</span>
        <button type="button" onClick={onBack} className="sem-btn sem-btn-sm sem-btn-quiet ml-auto" title="Back to the assistant menu">Back</button>
      </div>
      <p className="connect-any-lead">{PANEL_LEAD}</p>
      {failed && <p className="connect-any-note" role="alert">{NOTHING_LISTED}</p>}
      {!list && !failed && <p className="connect-any-note">Looking</p>}
      <ul className="connect-any-list">
        {(list ?? []).map((a, i) => {
          const v = rowView(a);
          const mine = result && result.id === a.id ? result.r : null;
          return (
            <li key={a.id} className="connect-any-row" data-assistant={a.id} data-state={a.state}>
              <div className="connect-any-main">
                <span className="connect-any-name">{v.name}</span>
                <span className="connect-any-status" data-muted={v.muted ? '' : undefined}>{v.status}</span>
              </div>
              {/* aria-disabled, never disabled: a button that turns disabled under the pointer drops focus, and the menu closes when focus leaves it. */}
              <button ref={i === 0 ? firstRef : undefined} type="button" aria-disabled={v.action === 'done' || busy !== null ? true : undefined}
                className={`sem-btn sem-btn-sm ${v.action === 'connect' && !v.muted ? 'sem-btn-primary' : 'sem-btn-quiet'}`}
                aria-label={`${v.actionLabel} ${v.name}`} onClick={() => { if (v.action !== 'done' && busy === null) onPress(a.id); }}>
                {busy === a.id ? 'Connecting' : v.actionLabel}
              </button>
              {mine && (
                <div className="connect-any-result" role="status" data-ok={mine.ok ? '' : undefined}>
                  {resultLines(mine).map((l, k) => <p key={k}>{l}</p>)}
                </div>
              )}
            </li>
          );
        })}
      </ul>
    </div>
  );
}
