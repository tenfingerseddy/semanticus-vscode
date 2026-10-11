// The pieces of the one-page Connections hub that only draw what they are handed (slice C, lane C2). They take
// props and call back, and hold no engine state, so a node test renders each one for real with react-dom/server
// and reads it the way a person would. The page (connectionshub.tsx) owns every engine call.

import type { ReactNode } from 'react';
import { JOBS, type Job, type JobOption, type NotLinkedSource, notLinkedRemovable, notLinkedUses } from './connectionshub-model';

const BTN = 'sem-btn';
const BTN_PRIMARY = 'sem-btn sem-btn-primary';
const BTN_COMPACT = 'sem-btn sem-btn-sm';
const BTN_QUIET = 'sem-btn sem-btn-sm sem-btn-quiet';

/**
 * One job's picker. Every saved model is listed; one that cannot do this job is struck out with the engine's
 * reason beside it and cannot be chosen, but it is never left out, so nobody wonders where their model went.
 */
export function JobPicker({ job, current, options, open, onToggle, onChoose, extra, busy }:
  { job: Job; current: string; options: JobOption[]; open: boolean; onToggle: () => void; onChoose: (id: string) => void; extra?: ReactNode; busy?: boolean }) {
  return (
    <div className="hub-job-pick">
      <button type="button" className="sem-field hub-job-current" data-testid={`hub-job-${job}`} aria-haspopup="listbox" aria-expanded={open} disabled={busy} onClick={onToggle}>
        <span className="min-w-0 truncate">{current}</span><span aria-hidden className="hub-job-caret">{'▾'}</span>
      </button>
      {open && (
        <div role="listbox" aria-label={JOBS.find((j) => j.job === job)?.label} className="hub-job-opts" data-testid={`hub-job-opts-${job}`}>
          {options.map((o) => (
            <button type="button" role="option" key={o.id} aria-selected={o.current} aria-disabled={!!o.refusal}
              data-testid="hub-job-option" data-refused={o.refusal ? 'true' : undefined}
              className="hub-job-opt" disabled={!!o.refusal || busy} onClick={() => { if (!o.refusal) onChoose(o.id); }}>
              <span className={o.refusal ? 'hub-job-struck' : undefined}>{o.name}</span>
              <em className="hub-job-why">{o.detail}</em>
            </button>
          ))}
          {extra}
        </div>
      )}
    </div>
  );
}

/** The Change panel: the three jobs, each with its one-line meaning and a picker over the saved models. */
export function ChangePanel({ rows, onDone, note }:
  { rows: Array<{ job: Job; current: string; picker: ReactNode }>; onDone: () => void; note?: string | null }) {
  return (
    <section className="hub-change" data-testid="hub-change" aria-label="Which model does which job">
      <div className="hub-change-head"><h2 className="sem-card-title">Which model does which job</h2>
        <button type="button" className={`${BTN_QUIET} ml-auto`} data-testid="hub-change-done" onClick={onDone}>Done</button></div>
      {rows.map(({ job, picker }) => {
        const info = JOBS.find((j) => j.job === job)!;
        return (
          <div className="hub-job" key={job} data-job={job}>
            <div className="hub-job-text"><b>{info.label}</b><span className="sem-text-meta">{info.help}</span></div>
            {picker}
          </div>
        );
      })}
      {note && <div className="sem-text-meta note-line note-ok" data-testid="hub-change-note">{note}</div>}
    </section>
  );
}

/** The make-default confirmation. It says how many models move BEFORE anything moves (contract 19.1). */
export function DefaultConfirm({ username, sentence, onCancel, onConfirm, busy }:
  { username: string; sentence: string; onCancel: () => void; onConfirm: () => void; busy?: boolean }) {
  return (
    <div className="hub-confirm" data-testid="hub-default-confirm">
      <span>Make {username} the default? {sentence} Each switch is kept in History.</span>
      <div className="hub-confirm-btns">
        <button type="button" className={BTN_COMPACT} onClick={onCancel}>Cancel</button>
        <button type="button" className={`${BTN_COMPACT} sem-btn-primary`} data-testid="hub-make-default-confirm" disabled={busy} onClick={onConfirm}>Make default</button>
      </div>
    </div>
  );
}

/**
 * Saved SQL sources no model is linked to, listed once (contract 19.3). Link attaches one to a model; Remove is
 * offered only when the engine counted nothing using it across every saved model, and the engine still refuses a
 * source that is in use.
 */
export function NotLinkedList({ items, linkTargets, onLink, onRemove, busy }:
  { items: NotLinkedSource[]; linkTargets: Array<{ id: string; name: string }>; onLink: (sourceId: string, connectionId: string) => void; onRemove: (s: NotLinkedSource) => void; busy?: boolean }) {
  if (!items.length) return null;
  return (
    <section className="hub-notlinked" data-testid="hub-notlinked">
      <h3 className="sem-label">Not linked to a model</h3>
      {items.map((s) => {
        const removable = notLinkedRemovable(s);
        const uses = notLinkedUses(s);
        return (
          <div className="sem-list-row hub-notlinked-row" key={s.id} data-testid="hub-notlinked-row">
            <span className="flex min-w-0 flex-1 flex-col">
              <span className="hub-row-name truncate">{s.name}</span>
              <span className="sem-text-meta truncate">{[s.server, s.database, uses].filter(Boolean).join(' · ')}</span>
            </span>
            <select className="sem-field sem-field-sm hub-link-select" data-testid="hub-notlinked-link" aria-label={`Link ${s.name} to a model`} value="" disabled={busy || linkTargets.length === 0}
              onChange={(e) => { if (e.target.value) onLink(s.id, e.target.value); }}>
              <option value="">Link</option>
              {linkTargets.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
            </select>
            <button type="button" className={BTN_COMPACT} data-testid="hub-notlinked-remove" disabled={busy || !removable}
              title={removable ? undefined : (typeof s.checksUsing === 'number' && s.usageScope ? 'Something still uses this source' : 'What uses this source was not counted')} onClick={() => onRemove(s)}>Remove</button>
          </div>
        );
      })}
    </section>
  );
}

export { BTN, BTN_PRIMARY, BTN_COMPACT, BTN_QUIET };
