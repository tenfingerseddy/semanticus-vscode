// The Connections bar (cx2, option A of the Connect mock-up, Kane 3 Nov 11:44): "we shouldnt have to scroll to see connect".
//
// One bar at the bottom of the dialog, on the saved list and on the Workspace model screen. It never scrolls. It holds:
//   - the pick (a saved row, or a workspace and a model) in words,
//   - the one account place ("Open as <account>", "Connect as <account>") with Change,
//   - the one Open (Connect on the Workspace model screen),
//   - while a model opens: "Opening <model>", "Step n of 5 · <step> · <s> s", five step marks and Cancel,
//   - and above its line, when one is asked, the account choice or the unsaved-changes question, so it is never out of view.
// No hooks: a page calls it as a function, so a page's own tests can read its parts in one tree.
import type { ReactNode } from 'react';
import { browserWaitLine, LATE_STEP, openPips, openStepLine, type OpenRun } from './openbar-model';

export interface OpenBarMessage { kind: 'stop' | 'bad' | 'ok'; text: string; onRetry?: () => void; onDismiss?: () => void }

export interface OpenBarProps {
  /** The picked model's name, or null when nothing is picked. */
  pickName?: string | null;
  /** "in Contoso Sales", or the end of a file's path. */
  pickWhere?: string | null;
  /** What the bar says when nothing is picked. */
  emptyText: string;
  /** Who the open signs in as. Null for a model that needs no account (localNote says so). */
  account?: string | null;
  /** "used last time", "for this open": why this account. */
  accountWhy?: string | null;
  /** Shown instead of the account for a model with none (a file, Power BI Desktop). */
  localNote?: string | null;
  /** Change: the account choice. Absent when there is nothing to choose. */
  onChange?: () => void;
  changeOpen?: boolean;
  /** Replaces "Open as <account>": the Workspace model screen's own line (who the list is read as, a sign-in that asks). */
  accountText?: string | null;
  action: 'Open' | 'Connect';
  /** The button's words when they say more than the action ("Connect and remember" on the pasted-address screen, cx5). */
  actLabel?: string;
  actTestId: string;
  /** No action button (the Workspace model screen with no list to connect from). */
  hideAct?: boolean;
  canAct: boolean;
  onAct: () => void;
  /** The open that runs, or null. */
  run: OpenRun | null;
  now: number;
  onCancel: () => void;
  /** A browser sign-in: the first step may wait on the person in their browser. */
  interactive?: boolean;
  /** A pick made while an open runs: "Cancel this open to open X instead." */
  pending?: string | null;
  message?: OpenBarMessage | null;
  /** Drawn left of the action (the Workspace model screen's environment question). */
  extra?: ReactNode;
  /** Drawn above the bar's line: the account choice, the unsaved-changes question. */
  pop?: ReactNode;
}

export function OpenBar(p: OpenBarProps) {
  const run = p.run;
  const verb = p.action === 'Connect' ? 'Connect as' : 'Open as';
  const msg = p.message && !run ? (
    <div className={`hub-bar-msg note-line ${p.message.kind === 'bad' ? 'note-bad' : p.message.kind === 'ok' ? 'note-ok' : 'note-warn'}`} data-testid="hub-bar-msg" role={p.message.kind === 'bad' ? 'alert' : 'status'}>
      <span className="min-w-0 flex-1 break-words">{p.message.text}</span>
      {p.message.onRetry && <button type="button" className="sem-btn sem-btn-sm" onClick={p.message.onRetry}>Try again</button>}
      {p.message.onDismiss && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={p.message.onDismiss}>Dismiss</button>}
    </div>
  ) : null;
  if (run) {
    const browser = browserWaitLine(run, p.now, !!p.interactive);
    return (
      <div className="hub-bar" data-testid="hub-bar" role="region" aria-label="Opening">
        {p.pop && <div className="hub-bar-pop">{p.pop}</div>}
        <div className="hub-bar-line">
          <div className="hub-bar-what">
            <div className="hub-bar-name"><b>Opening {run.target}</b> {run.where && <span className="sem-text-meta">{run.where}</span>}</div>
            <div className="hub-bar-step sem-text-meta" data-testid="hub-bar-step" aria-live="polite">{openStepLine(run, p.now)}</div>
            {browser && <div className="hub-bar-hint note-line note-warn" data-testid="hub-bar-browser">{browser}</div>}
            {!run.cancellable && <div className="hub-bar-hint sem-text-meta" data-testid="hub-bar-late">{LATE_STEP}</div>}
            {run.cancelling && run.cancellable && <div className="hub-bar-hint sem-text-meta">Stopping the open...</div>}
          </div>
          {run.cancellable && <button type="button" className="sem-btn" data-testid="hub-bar-cancel" disabled={run.cancelling} onClick={p.onCancel}>Cancel</button>}
        </div>
        <div className="hub-bar-pips" data-testid="hub-bar-pips" aria-hidden>
          {openPips(run).map((k, i) => <i key={i} data-pip={k} className={`hub-pip hub-pip-${k}`} />)}
        </div>
        {p.pending && <div className="hub-bar-hint sem-text-meta" data-testid="hub-bar-pending">{p.pending}</div>}
      </div>
    );
  }
  return (
    <div className="hub-bar" data-testid="hub-bar" role="region" aria-label={p.action}>
      {p.pop && <div className="hub-bar-pop">{p.pop}</div>}
      {msg}
      <div className="hub-bar-line">
        <div className="hub-bar-what">
          {p.pickName
            ? <div className="hub-bar-name" data-testid="hub-bar-name"><b>{p.pickName}</b> {p.pickWhere && <span className="sem-text-meta">{p.pickWhere}</span>}</div>
            : <div className="hub-bar-name sem-text-meta" data-testid="hub-bar-name">{p.emptyText}</div>}
          {p.pickName && p.localNote && <div className="hub-bar-acct sem-text-meta" data-testid="hub-bar-account">{p.localNote}</div>}
          {(p.accountText || (p.pickName && !p.localNote && (p.account || p.onChange))) && (
            <div className="hub-bar-acct sem-text-meta" data-testid="hub-bar-account">
              {p.accountText
                ? <span data-testid="pick-as">{p.accountText}</span>
                : <span data-testid="pick-as">{verb} <b className="hub-bar-who">{p.account || 'the account you choose'}</b>{p.accountWhy ? ` (${p.accountWhy})` : ''}</span>}
              {p.onChange && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet hub-bar-change" data-testid="hub-bar-change" aria-expanded={!!p.changeOpen} onClick={p.onChange}>Change</button>}
            </div>
          )}
        </div>
        {p.extra}
        {!p.hideAct && <button type="button" className="sem-btn sem-btn-primary" data-testid={p.actTestId} disabled={!p.canAct} onClick={p.onAct}>{p.actLabel ?? p.action}</button>}
      </div>
    </div>
  );
}
