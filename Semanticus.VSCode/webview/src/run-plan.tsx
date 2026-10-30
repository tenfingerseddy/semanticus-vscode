import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useMarksHost } from './marks-host';
import { aboutTime, planGroups, planSeconds, planSummary, runReason, selectionRequest, type CheckCache, type RunPlanView } from './marks-model';

export interface PlanMeasure { ref: string; name: string; table?: string; folder?: string }
type RunResult = { planId: string; stoppedOnEdit: boolean; stopped?: boolean; results: CheckCache[] };
const errorText = (error: unknown, fallback: string) => error instanceof Error && error.message ? error.message : fallback;
const tableOf = (m: PlanMeasure) => m.table || m.ref.replace(/^measure:/, '').split('/')[0];
const folderOf = (m: PlanMeasure) => (m.folder ?? '').split(';')[0].trim();

/** A finished run says nothing: the verdict line on the Checks page changes. Only a run that stopped needs words. */
function ranWords(result: RunResult): string {
  return result?.stoppedOnEdit ? 'The run stopped because the model changed; run it again to finish.' : '';
}

/** The run plan at rest: one line, Choose and Run. While it runs, the line shows the progress and Stop. A test counts these words. */
export function PlanLine({ sentence, running, done, total, choosing, canRun, onChoose, onRun, onStop }: {
  sentence: string; running?: boolean; done?: number; total?: number; choosing?: boolean; canRun?: boolean; onChoose: () => void; onRun: () => void; onStop?: () => void;
}) {
  return <div className="rp-line">
    <p className="rp-sentence" data-testid="plan-sentence" role={running ? 'status' : undefined}>{running ? `Checking ${done ?? 0} of ${total ?? 0}.` : sentence}</p>
    {running && <progress max={total || 1} value={done ?? 0} aria-label="Checks done" />}
    {running && onStop && <button type="button" className="mk-link" data-testid="plan-stop" onClick={onStop}>Stop</button>}
    <div className="mk-acts">
      <button type="button" className="sem-btn sem-btn-sm" aria-expanded={!!choosing} disabled={running} data-testid="plan-choose" onClick={onChoose}>Choose</button>
      <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={!canRun || running} onClick={onRun}>Run</button>
    </div>
  </div>;
}

/** Choose: Include performance, the reasons for each group, and Pick your own. A test counts these words. */
export function ChooseBody({ plan, include, added, name, more, onInclude, onMore, onPick, running }: {
  plan: RunPlanView | null; include: boolean; added: string; name: (ref: string) => string | undefined; more: Set<string>;
  onInclude: (on: boolean) => void; onMore: (group: string) => void; onPick: () => void; running?: boolean;
}) {
  return <>
    <label className="rp-switch">
      <input type="checkbox" role="switch" data-testid="plan-performance" checked={include} onChange={e => onInclude(e.target.checked)} />
      <span>Include performance{added && <span className="mk-faint"> (adds {added})</span>}</span>
    </label>
    {plan && <div className="rp-groups" data-testid="plan-reasons">{planGroups(plan).map(g => {
      const all = more.has(g.group), shown = all ? g.picks : g.picks.slice(0, 2);
      return <div key={g.group} className="rp-group"><p className="rp-glabel">{g.label} ({g.picks.length})</p>
        <ul>{shown.map(p => <li key={p.measureRef}><b>{name(p.measureRef)}</b> {runReason(p.reason)}</li>)}</ul>
        {g.picks.length > 2 && <button type="button" className="mk-link" onClick={() => onMore(g.group)}>{all ? 'Show fewer' : `and ${g.picks.length - 2} more`}</button>}
      </div>;
    })}</div>}
    <div className="mk-acts"><button type="button" className="sem-btn sem-btn-sm" disabled={running} onClick={onPick}>Pick your own</button></div>
  </>;
}

/** What a run will check and why, before it runs. Include performance changes the estimate in every place it shows. */
export function RunPlan({ measures, onRan, progress }: { measures: PlanMeasure[]; onRan?: (results: CheckCache[]) => void; progress?: { done: number; total: number } | null }) {
  const host = useMarksHost();
  const [include, setInclude] = useState(false);
  const [plan, setPlan] = useState<RunPlanView | null>(null);
  const [selectedRun, setSelectedRun] = useState<RunPlanView | null>(null);
  const [choosing, setChoosing] = useState(false);
  const [done, setDone] = useState(0);
  const [more, setMore] = useState<Set<string>>(new Set());
  const [picking, setPicking] = useState(false);
  const [ticked, setTicked] = useState<string[]>([]);
  const [tickedPlan, setTickedPlan] = useState<RunPlanView | null>(null);
  const [openTables, setOpenTables] = useState<Set<string>>(new Set());
  const [note, setNote] = useState('');
  const [running, setRunning] = useState(false);
  const asked = useRef(0);
  const replan = (perf: boolean) => {
    const mine = ++asked.current;
    host.rpc<RunPlanView>('planMeasureRun', { cap: 40, includePerformance: perf }).then(p => { if (mine === asked.current) setPlan(Array.isArray(p?.picks) ? p : null); })
      .catch(error => { if (mine === asked.current) setNote(errorText(error, 'The run plan could not be made.')); });
  };
  useEffect(() => { replan(include); const off = host.onChange(() => replan(include)); return off; }, [include]);
  const tickedAsk = useRef(0);
  useEffect(() => {
    if (!ticked.length) { setTickedPlan(null); return; }
    const mine = ++tickedAsk.current;
    host.rpc<RunPlanView>('planMeasureRun', selectionRequest({ kind: 'measures', measures: ticked }, include))
      .then(p => { if (mine === tickedAsk.current) setTickedPlan(Array.isArray(p?.picks) ? p : null); }).catch(() => { if (mine === tickedAsk.current) setTickedPlan(null); });
  }, [ticked, include]);
  const shownPlan = selectedRun ?? (picking && tickedPlan ? tickedPlan : plan);
  const summary = shownPlan ? planSummary(shownPlan, include) : null;
  // The run is one call that answers at the end, so the line counts the measures whose check state the engine pushed as done
  // since it began. Nothing polls the state: both hosts carry the push (rc27 run hang).
  const watch = (refs: string[]) => {
    const started = Date.now(), seen = new Set<string>(); setDone(0);
    const count = (incoming: unknown) => {
      const list = Array.isArray(incoming) ? incoming : (incoming as { states?: CheckCache[] })?.states ?? [];
      for (const c of list as CheckCache[]) if (refs.includes(c.measureRef) && !c.running && c.lastChecked && Date.parse(c.lastChecked) >= started) seen.add(c.measureRef);
      setDone(seen.size);
    };
    return host.onCheckState?.(count) ?? (() => {});
  };
  const stop = () => { void host.rpc('stopMeasureRun').catch(error => setNote(errorText(error, 'The run could not be stopped.'))); };
  const run = async (ask: RunPlanView | null) => {
    if (!ask) return;
    setSelectedRun(null); setChoosing(false);
    setRunning(true); setNote('');
    const unwatch = watch(ask.picks.map(p => p.measureRef));
    // The rows take the run's results before the done line shows (rc24 release 2, gap 3).
    try { const result = await host.rpc<RunResult>('runMeasurePlan', ask.id); onRan?.(result?.results ?? []); setNote(ranWords(result)); }
    catch (error) { setNote(errorText(error, 'The run did not finish.')); }
    finally { unwatch(); setRunning(false); }
  };
  const checkSelected = async () => {
    // A fresh plan of exactly the ticked set, so the run cannot fall back to the default plan.
    setRunning(true); setNote(''); setChoosing(false); setPicking(false);
    let unwatch = () => {};
    try {
      const exact = await host.rpc<RunPlanView>('planMeasureRun', selectionRequest({ kind: 'measures', measures: ticked }, include));
      setSelectedRun(exact); unwatch = watch(exact.picks.map(p => p.measureRef));
      const result = await host.rpc<RunResult>('runMeasurePlan', exact.id);
      onRan?.(result?.results ?? []); setNote(ranWords(result));
    } catch (error) { setNote(errorText(error, 'The run did not finish.')); }
    finally { unwatch(); setRunning(false); }
  };
  const tickChanged = async () => {
    setNote('');
    try {
      const changed = await host.rpc<RunPlanView>('planMeasureRun', selectionRequest({ kind: 'changed' }, include));
      const refs = (changed?.picks ?? []).map(p => p.measureRef);
      setTicked(prev => [...new Set([...prev, ...refs])]);
      setNote(refs.length ? '' : 'You have not changed a measure since the last run.');
    } catch (error) { setNote(errorText(error, 'The changed measures could not be found.')); }
  };
  const toggle = (refs: string[], on: boolean) => { setSelectedRun(null); setTicked(prev => on ? [...new Set([...prev, ...refs])] : prev.filter(r => !refs.includes(r))); };
  const tables = [...new Set(measures.map(tableOf))].sort((a, b) => a.localeCompare(b));
  const name = (ref: string) => measures.find(m => m.ref === ref)?.name ?? ref.split('/').pop();
  const box = (refs: string[], label: string, testid: string) => {
    const on = refs.length > 0 && refs.every(r => ticked.includes(r)), some = !on && refs.some(r => ticked.includes(r));
    return <input type="checkbox" aria-label={label} data-testid={testid} checked={on} ref={el => { if (el) el.indeterminate = some; }} onChange={() => toggle(refs, !on)} />;
  };
  const tickedTime = tickedPlan ? planSummary(tickedPlan, include).time : null;
  const added = plan ? aboutTime(planSeconds(plan, true) - planSeconds(plan, false)).replace(/^About/, 'about') : '';
  const shown = running && progress ? progress : { done, total: shownPlan?.picks.length ?? 0 };
  const choose: ReactNode = <div className="rp-choose" data-testid="plan-choose-panel">
    {!picking && <ChooseBody plan={plan} include={include} added={added} name={name} more={more}
      onInclude={on => { setSelectedRun(null); setInclude(on); }} onMore={g => setMore(s => { const n = new Set(s); n.has(g) ? n.delete(g) : n.add(g); return n; })}
      onPick={() => setPicking(true)} running={running} />}
    {picking && <div className="rp-pick" data-testid="plan-pick">
      <div className="mk-acts"><span data-testid="pick-count">{ticked.length} ticked</span><button type="button" className="mk-link" onClick={() => void tickChanged()}>Tick the ones I changed</button>
        {ticked.length > 0 && <button type="button" className="mk-link" onClick={() => { setSelectedRun(null); setTicked([]); }}>Clear</button>}</div>
      <ul className="rp-tree">{tables.map(t => {
        const inTable = measures.filter(m => tableOf(m) === t), open = openTables.has(t);
        const folders = [...new Set(inTable.map(folderOf).filter(Boolean))].sort((a, b) => a.localeCompare(b));
        const loose = inTable.filter(m => !folderOf(m));
        const row = (m: PlanMeasure) => <li key={m.ref} className="rp-m"><label>{box([m.ref], `Tick ${m.name}`, `tick-${m.ref}`)}<span>{m.name}</span></label></li>;
        return <li key={t}>
          <div className="rp-t"><button type="button" className="rp-twist" aria-expanded={open} aria-label={`${open ? 'Close' : 'Open'} ${t}`} onClick={() => setOpenTables(s => { const n = new Set(s); open ? n.delete(t) : n.add(t); return n; })}>{open ? '▾' : '▸'}</button>
            <label>{box(inTable.map(m => m.ref), `Tick table ${t}`, `tick-table-${t}`)}<span>{t}</span><span className="mk-faint"> {inTable.length}</span></label></div>
          {open && <ul>{folders.map(f => {
            const inFolder = inTable.filter(m => folderOf(m) === f || folderOf(m).startsWith(f + '\\'));
            return <li key={f}><div className="rp-f"><label>{box(inFolder.map(m => m.ref), `Tick folder ${f}`, `tick-folder-${t}/${f}`)}<span>{f}</span></label></div>
              <ul>{inFolder.filter(m => folderOf(m) === f).map(row)}</ul></li>;
          })}{loose.map(row)}</ul>}
        </li>;
      })}</ul>
      <div className="rp-selected" data-testid="pick-summary">
        <p>{ticked.length ? <><b>{ticked.length} ticked:</b> {ticked.slice(0, 4).map(name).join(', ')}{ticked.length > 4 ? ` and ${ticked.length - 4} more` : ''}.</> : 'Nothing is ticked yet.'}</p>
        {tickedTime && <p className="mk-faint" data-testid="pick-estimate">{tickedTime}.</p>}
      </div>
      <div className="mk-acts">
        <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={!ticked.length || running} onClick={() => void checkSelected()}>Check selected ({ticked.length})</button>
        <button type="button" className="sem-btn sem-btn-sm" disabled={running} onClick={() => { setSelectedRun(null); setPicking(false); }}>Cancel</button>
      </div>
    </div>}
  </div>;
  return <section className="rp" data-testid="run-plan" aria-label="Run plan">
    <PlanLine sentence={summary ? summary.sentence : 'Making the plan.'} running={running} done={shown.done} total={shown.total} choosing={choosing} canRun={!!plan}
      onChoose={() => setChoosing(!choosing)} onRun={() => void run(plan)} onStop={stop} />
    {choosing && !running && choose}
    {note && <p className="mk-note" role="status" data-testid="plan-note">{note}</p>}
  </section>;
}
