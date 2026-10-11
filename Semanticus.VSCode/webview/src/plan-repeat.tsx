import { useEffect, useRef, useState } from 'react';
import * as R from './plan-repeat-model';

// ===================================================================================================
// The repeat editor in a Plan row's detail (lane RP; contract section 1.7). U1 owns plan-row.tsx and renders this
// as one field row of the detail's right column; everything it shows comes from the plan the engine sent, and
// every button is one of the three person-only calls (addPlanRepeat, setPlanRepeat, deletePlanRepeat) answered
// with the whole plan.
//
// Four faces: nothing (a row that cannot repeat), the offer ("Repeat this", Pro), the Pro line (free), and a saved
// repeat (its cadence, when it comes due next, Change, Pause or Resume, Delete). Nothing runs by itself, and the
// copy says so where a person could think otherwise.
// ===================================================================================================

export interface RepeatEditorProps {
  row: R.RepeatRow;
  repeats: R.PlanRepeat[] | null | undefined;
  isPro: boolean;
  busy: string | null;
  /** Answer true only when the engine took the write: the form closes and its held cadence ends on that answer, and only while it still holds the cadence that was sent (R-B). */
  onAdd: (request: R.RepeatRequest) => void | Promise<boolean>;
  onChange: (id: string, request: R.RepeatRequest) => void | Promise<boolean>;
  onDelete: (id: string) => void;
  /** The clock the default cadence reads; the page's own. */
  now?: Date;
  /** Where a cadence being set is held (the Plan row's own scope in the draft store), so the form opens on it again when the row is drawn anew (join 11 solui, sweep fix 1, R-B).
   *  Handed in by the row, so this editor reads and writes no store of its own. */
  hold?: CadenceHold;
}

/** The held cadence of one row: what is held now, hold a cadence over the one the form started from, and end it. */
export interface CadenceHold { read: () => R.RepeatCadence | undefined; write: (c: R.RepeatCadence, start: R.RepeatCadence) => void; clear: () => void }

function CadenceForm({ start, busy, onSave, onCancel, saveLabel, now, hold }: {
  start: R.RepeatCadence; busy: string | null; saveLabel: string;
  /** The same clock the start cadence read, so switching to months picks the same day of the month. */
  now?: Date;
  onSave: (c: R.RepeatCadence) => void; onCancel: () => void;
  hold?: CadenceHold;
}) {
  const [c, setC] = useState<R.RepeatCadence>(() => hold?.read() ?? start);
  // What the person set is held until they save or cancel; a cadence equal to where the form started holds nothing.
  useEffect(() => { hold?.write(c, start); }, [c]);   // eslint-disable-line react-hooks/exhaustive-deps
  const problem = R.cadenceProblem(c);
  const unit = c.kind === 'daily' ? (c.every === 1 ? 'day' : 'days') : c.kind === 'weekly' ? (c.every === 1 ? 'week' : 'weeks') : (c.every === 1 ? 'month' : 'months');
  return <form className="rp-form" data-testid="plan-repeat-form"
    onSubmit={(e) => { e.preventDefault(); if (!problem) onSave(c); }}>
    <span className="plan-muted">Every</span>
    <input className="sem-field sem-field-sm rp-num" type="number" min={1} max={12} value={c.every}
      aria-label="How many" data-testid="plan-repeat-every"
      onChange={(e) => setC({ ...c, every: Number(e.target.value) })} />
    <select className="sem-field sem-field-sm" aria-label="Days, weeks or months" data-testid="plan-repeat-kind" value={c.kind}
      onChange={(e) => setC(R.withKind(c, e.target.value as R.CadenceKind, now))}>
      <option value="daily">{c.every === 1 ? 'day' : 'days'}</option>
      <option value="weekly">{c.every === 1 ? 'week' : 'weeks'}</option>
      <option value="monthly">{c.every === 1 ? 'month' : 'months'}</option>
    </select>
    {c.kind === 'weekly' && <select className="sem-field sem-field-sm" aria-label="On which day" data-testid="plan-repeat-day"
      value={c.day ?? ''} onChange={(e) => setC({ ...c, day: e.target.value })}>
      {R.DAYS.map((d) => <option key={d} value={d}>on {d[0].toUpperCase() + d.slice(1)}</option>)}
    </select>}
    {c.kind === 'monthly' && <input className="sem-field sem-field-sm rp-num" type="number" min={1} max={31}
      value={c.date ?? ''} aria-label="On which date" data-testid="plan-repeat-date"
      onChange={(e) => setC({ ...c, date: Number(e.target.value) })} />}
    <span className="plan-muted rp-meta" data-testid="plan-repeat-preview">{problem ?? R.cadenceText(c)}</span>
    <button type="submit" className="sem-btn sem-btn-sm sem-btn-primary" data-testid="plan-repeat-save"
      disabled={!!busy || !!problem} aria-label={`${saveLabel}, ${unit}`}>{saveLabel}</button>
    <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={onCancel}>Cancel</button>
  </form>;
}

/** One field row: "Repeats", then the face this row gets. Renders nothing for a row that cannot repeat. */
export function RepeatEditor({ row, repeats, isPro, busy, onAdd, onChange, onDelete, now, hold }: RepeatEditorProps) {
  // A cadence held for this row opens the form again (the row was drawn anew: it moved to another section of the Plan).
  const [editing, setEditingNow] = useState(() => !!hold?.read());
  const close = () => { hold?.clear(); setEditingNow(false); };
  const setEditing = (on: boolean) => { if (on) setEditingNow(true); else close(); };
  const [sending, setSending] = useState(false);
  // A cadence goes to the engine and the form stays, with its words, until the engine says it took them. A refusal (the repeat was deleted from another window) leaves the form on what the person set.
  const send = async (cadence: R.RepeatCadence, write: () => void | Promise<boolean>) => {
    if (sending) return;
    setSending(true);
    let ok = false;
    try { ok = (await write()) === true; } catch { /* refused: the cadence stays */ }
    setSending(false);
    if (!ok) return;
    const held = hold?.read();
    if (!held || R.sameCadence(held, cadence)) close();   // words typed while the answer was on its way are not the write's to end
  };
  const rowWas = useRef(row.id);
  useEffect(() => { if (rowWas.current !== row.id) { rowWas.current = row.id; setEditingNow(false); } }, [row.id]);
  const face = R.repeatFace(row, repeats, isPro);
  if (face === 'none') return null;
  const saved = R.repeatForRow(row.id, repeats);

  return <div className="rp-field" data-testid="plan-repeat" data-face={face}>
    <span className="plan-muted">Repeats</span>
    {face === 'pro' && <span className="plan-muted rp-meta" data-testid="plan-repeat-pro">
      Repeating this is part of Semanticus Pro.</span>}

    {face === 'offer' && !editing && <span className="rp-acts">
      <button type="button" className="sem-btn sem-btn-sm" data-testid="plan-repeat-add" disabled={!!busy}
        onClick={() => setEditing(true)}>Repeat this</button>
      <span className="plan-muted rp-meta">It comes back on a date you choose. Nothing runs by itself.</span>
    </span>}
    {face === 'offer' && editing && <CadenceForm start={R.defaultCadence(now)} now={now} busy={busy ?? (sending ? 'saving' : null)} saveLabel="Save repeat" hold={hold}
      onCancel={() => setEditing(false)}
      onSave={(cadence) => { const req = R.repeatRequestFor(row, repeats); if (req) void send(cadence, () => onAdd({ ...req, cadence })); }} />}

    {face === 'saved' && saved && !editing && <span className="rp-saved">
      <span data-testid="plan-repeat-cadence"><b>{saved.cadenceText}</b></span>
      {isPro
        ? <span className="plan-muted rp-meta" data-testid="plan-repeat-due">{R.dueLine(saved, isPro)}</span>
        : <span className="plan-muted rp-meta" data-testid="plan-repeat-pro">{R.PRO_LINE}</span>}
      <span className="rp-acts">
        {isPro && <button type="button" className="sem-btn sem-btn-sm" data-testid="plan-repeat-change" disabled={!!busy}
          onClick={() => setEditing(true)}>Change</button>}
        {isPro && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="plan-repeat-hold" disabled={!!busy}
          onClick={() => onChange(saved.id, { held: !saved.held })}>{saved.held ? 'Resume' : 'Pause'}</button>}
        <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" data-testid="plan-repeat-delete" disabled={!!busy}
          title="Stops the repeat. This row stays." onClick={() => onDelete(saved.id)}>Stop repeating</button>
      </span>
    </span>}
    {face === 'saved' && saved && editing && <CadenceForm start={saved.cadence} now={now} busy={busy ?? (sending ? 'saving' : null)} saveLabel="Save" hold={hold}
      onCancel={() => setEditing(false)}
      onSave={(cadence) => void send(cadence, () => onChange(saved.id, { cadence }))} />}
  </div>;
}
