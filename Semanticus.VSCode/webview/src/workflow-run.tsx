import { useEffect, useRef, useState } from 'react';
import { rpc, onWorkflowChange } from './bridge';
import { uiLabel, checkTitle } from './copy';
import { submitStepArgs } from './workflowsubmit.mjs';
import { Caret } from './ui';
import {
  stepGateSkipped, runSections, frameProgress, frameIsCurrent, providedAnswerDraft, type RunFrameItem, type RunSection, type RunRow,
} from './workflowruns.mjs';
import { gateArg } from './workflowsubmit.mjs';
import { reusableCopy, reusableName, type WfInfo } from './workflows-model';
import { EvidenceArtifactDialog, type EvidenceArtifactW, type EvidenceSaveResultW } from './artifactdialog';
import {
  Banner, Button, Dot, OpChip, Panel, Pill, RailLine, ReasonButton, SectionTitle, StatusNode, AssistantInstructions, Markdown,
  isAnswered, isDeclined, stepStyle, verifyColor, strictnessWords, stepSummary, totalStepsText, runTally,
  type AnswerValue, type GateInput, type GateSpec, type WorkflowInfo, type CurrentStepView, type StepResult, type VerifyResult, type WorkflowRunView,
} from './workflow-ui';

// ===================================================================================================
// A guided workflow run, as the detail of its Plan row (lane W2 doing R3; Plan-heart contract 11.3).
//
// A run is a Plan row "run/<wfr id>" (Composer/RunRows.cs). Its steps, the current step's questions, Submit,
// Skip with reason and the one Abort are here, with the same RPCs the Runs list used; the Runs list and the
// live-run banner are gone from Workflows. When the run ends its row leaves the Plan and the run lives on as a
// History entry, where its report opens (plan-history.tsx) and "Make it reusable" turns it into a workflow.
// The detail reads the run itself and follows workflow/didChange, so a step the assistant submits on the
// other door moves here at once.
// ===================================================================================================

export const RUN_AGED_OUT = 'This run is no longer available for a report. Only the most recent runs are kept, and this one has aged out. Start the workflow again to produce a fresh run you can seal.';

/** currentOnly: only the step the run waits at, for a draft's gated step (lane l9-finish), where the draft's own rows
 *  already list every step. */
export function WorkflowRunDetail({ runId, onRunUpdate, currentOnly }: { runId: string; onRunUpdate?: (r: WorkflowRunView) => void; currentOnly?: boolean }) {
  const [run, setRun] = useState<WorkflowRunView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [report, setReport] = useState(false);
  useEffect(() => {
    let alive = true;
    setRun(null); setError(null);
    rpc<WorkflowRunView>('getWorkflowRun', runId).then((r) => { if (alive) setRun(r); })
      .catch((e) => { if (alive) setError(String((e as Error).message ?? e)); });
    const off = onWorkflowChange((v) => { const r = v as WorkflowRunView; if (r?.runId === runId) setRun(r); });
    return () => { alive = false; off(); };
  }, [runId]);
  const take = (r: WorkflowRunView) => { setRun(r); onRunUpdate?.(r); };
  const submit = async (runId: string, stepId: string, answersJson: string, callGate?: string) =>
    take(await rpc<WorkflowRunView>('submitWorkflowStep', ...submitStepArgs(runId, stepId, answersJson, 'human', callGate)));
  const skip = async (id: string, stepId: string, reason: string) => take(await rpc<WorkflowRunView>('skipWorkflowStep', id, stepId, reason, 'human'));
  const abort = async (id: string, reason: string) => take(await rpc<WorkflowRunView>('abortWorkflow', id, reason, 'human'));
  if (error) return <div className="sem-wf-run" data-testid="wf-run-detail"><Banner color="var(--sem-bad)">{/not found/i.test(error) ? RUN_AGED_OUT : error}</Banner></div>;
  if (!run) return <div className="sem-wf-run sem-wf-note" data-testid="wf-run-detail" role="status">Reading the run…</div>;
  const live = run.status === 'active';
  if (currentOnly) return <div className="sem-wf-run" data-testid="wf-run-detail" data-run={run.runId} data-status={run.status}>
    <RunRail run={run} onSubmit={submit} onSkip={skip} onAbort={abort} currentOnly />
  </div>;
  return <div className="sem-wf-run" data-testid="wf-run-detail" data-run={run.runId} data-status={run.status}>
    <div className="sem-wf-run-head">
      <span className="sem-wf-note">{live
        ? <>Step <span className="sem-num">{Math.min(run.stepIndex + 1, run.totalSteps)}</span> of {totalStepsText(run)}</>
        : run.status === 'completed' ? 'Finished. Its report is in History.' : `Stopped. ${run.abortReason ?? ''}`.trim()}</span>
      {!live && <button type="button" className="sem-btn sem-btn-sm" data-testid="wf-run-report" onClick={() => setReport(true)}>Workflow report</button>}
    </div>
    <RunRail run={run} onSubmit={submit} onSkip={skip} onAbort={abort} />
    {report && <RunReportDialog run={run} onClose={() => setReport(false)} />}
  </div>;
}

/** The sealed report of a finished run (export_workflow_evidence), opened from its History entry or its row. */
export function RunReportDialog({ run, onClose }: { run: { runId: string; workflow?: string | null; title?: string | null; modelName?: string | null }; onClose: () => void }) {
  const workflow = run.workflow || 'workflow';
  return <EvidenceArtifactDialog
    title="Workflow report"
    subtitle={`${run.title || workflow} · run ${run.runId} · ${run.modelName || 'current model'}`}
    baseName={`${(run.modelName || 'semanticus').replace(/[^\w.-]+/g, '_')}-${workflow}-evidence`}
    stateKey="workflows.evidence.format"
    load={() => rpc<EvidenceArtifactW>('exportWorkflowEvidence', run.runId).then((a) => {
      if (a?.note && !a.html && !a.json && !a.markdown && /not found/i.test(a.note)) return { error: RUN_AGED_OUT };
      return a;
    }).catch((e) => {
      if (/not found/i.test(String((e as Error).message ?? e))) return { error: RUN_AGED_OUT };
      throw e;
    })}
    save={() => rpc<EvidenceSaveResultW>('saveEvidence', 'workflow', run.runId, 'human')}
    onClose={onClose} />;
}

// ---- run rail (a run is active or terminal) ------------------------------------------------------
// Actions carry THIS run's id up to the shell so a switch to another live run never targets the wrong run.
export function RunRail({ run, onSubmit, onSkip, onAbort, currentOnly }: {
  run: WorkflowRunView; currentOnly?: boolean;
  onSubmit: (runId: string, stepId: string, answersJson: string, callGate?: string) => Promise<void>;
  onSkip: (runId: string, stepId: string, reason: string) => Promise<void>; onAbort: (runId: string, reason: string) => Promise<void>;
}) {
  const rid = run.runId;
  const currentId = run.status === 'active' ? run.currentStep?.stepId : null;
  const renderStep = ({ result, n }: RunRow) => currentOnly && currentId !== result.stepId ? null : (
    <RunStep key={`${rid}:${result.stepId}`} n={n} result={result}
      current={currentId === result.stepId ? run.currentStep ?? null : null}
      onSubmit={(stepId, json, gate) => onSubmit(rid, stepId, json, gate)}
      onSkip={(stepId, reason) => onSkip(rid, stepId, reason)} onAbort={(reason) => onAbort(rid, reason)} />
  );
  // Flat, not a Panel: this rail sits inside the opened Plan row, which is already the card (One Visual Language).
  return (
    <div className="sem-wf-runrail">
      {!currentOnly && <SectionTitle>Run <span style={{ color: 'var(--sem-muted)' }} data-testid="wf-run-tally">· {runTally(run)}</span></SectionTitle>}
      <div className="relative mt-2">
        <RailLine />
        <div className="flex flex-col">
          <RunSections sections={runSections(run)} runId={rid} currentId={currentId} renderStep={renderStep} />
        </div>
      </div>
    </div>
  );
}

function RunSections({ sections, runId, currentId, renderStep }: {
  sections: RunSection[]; runId: string; currentId?: string | null; renderStep: (row: RunRow) => React.ReactNode;
}) {
  return <>{sections.map((section) => section.kind === 'step' ? renderStep(section.row)
    : section.frameKind === 'call' ? section.items.map((item) => (
      <RunFrameCard key={`${runId}:frame-${item.frameIndex}`} item={item} total={1} runId={runId} currentId={currentId} renderStep={renderStep} />
    )) : (
      <div key={`${runId}:${section.key}`} className="relative my-2 sem-wf-step-now">
        <div className="flex items-center gap-2 flex-wrap mb-2">
          <b className="text-(length:--sem-text-meta)">Repeat · {section.items[0].rows[0]?.result.title ?? uiLabel(section.stepId)}</b>
          <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>
            {section.items.filter((item) => !['pending', 'in_progress'].includes(frameProgress(item, currentId))).length} of {section.items.length} items finished
          </span>
        </div>
        {section.items.map((item) => (
          <RunFrameCard key={`${runId}:frame-${item.frameIndex}`} item={item} total={section.items.length} runId={runId} currentId={currentId} renderStep={renderStep} />
        ))}
      </div>
    ))}</>;
}

function RunFrameCard({ item, total, runId, currentId, renderStep }: {
  item: RunFrameItem; total: number; runId: string; currentId?: string | null; renderStep: (row: RunRow) => React.ReactNode;
}) {
  const status = frameProgress(item, currentId);
  const current = frameIsCurrent(item, currentId);
  const [expanded, setExpanded] = useState(false);
  const open = current || expanded;
  const style = stepStyle(status);
  const frame = item.frame;
  return (
    <div className="mt-2 pt-2 sem-wf-frame" data-current={current ? 'true' : undefined}>
      <button type="button" className="w-full text-left flex items-center gap-2 min-w-0" aria-expanded={open}
        onClick={() => setExpanded((value) => !value)} disabled={current}>
        <span className="text-(length:--sem-text-label)" style={{ color: style.color }}>{style.glyph}</span>
        {frame.kind === 'iteration' ? <>
          <span className="text-(length:--sem-text-label) shrink-0" style={{ color: 'var(--sem-muted)' }}>Item {frame.iterationIndex + 1} of {total}</span>
          <span className="text-(length:--sem-text-meta) font-semibold break-words min-w-0">{frame.loopValue}</span>
        </> : <span className="text-(length:--sem-text-meta) font-semibold">Run workflow · {uiLabel(frame.workflow)}</span>}
        <span className="text-(length:--sem-text-label) ml-auto shrink-0" style={{ color: style.color }}>{style.label}</span>
        {!current && <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{open ? '▾' : '▸'}</span>}
      </button>
      {frame.kind === 'call' && <div className="text-(length:--sem-text-label) mt-1 flex flex-wrap gap-x-4 gap-y-1" style={{ color: 'var(--sem-muted)' }}>
        <span>{item.rows.filter(({ result }) => !['pending', 'in_progress'].includes(result.status)).length} of {item.rows.length} planned steps finished</span>
        <span>Inputs received: {frame.passed.length ? frame.passed.map((name) => uiLabel(name)).join(', ') : 'none'}</span>
        <span>{frame.returnNote || `Outputs returned: ${frame.returned.length ? frame.returned.map((name) => uiLabel(name)).join(', ') : status === 'pending' || current ? 'none yet' : 'none'}`}</span>
      </div>}
      {open && <div className="mt-1"><RunSections sections={item.sections} runId={runId} currentId={currentId} renderStep={renderStep} /></div>}
    </div>
  );
}

function RunStep({ n, result, current, onSubmit, onSkip, onAbort }: {
  n: number; result: StepResult; current: CurrentStepView | null;
  onSubmit: (stepId: string, answersJson: string, callGate?: string) => Promise<void>;
  onSkip: (stepId: string, reason: string) => Promise<void>; onAbort: (reason: string) => Promise<void>;
}) {
  const st = stepStyle(result.status);
  const done = result.status === 'passed' || result.status === 'done' || result.status === 'not_applicable' || result.status === 'skipped' || result.status === 'failed';
  const notable = !!result.note
    || Object.values(result.answers).some((a) => a.declined)
    || result.verifyResults.some((v) => v.status !== 'passed');
  const [open, setOpen] = useState(notable);
  const hasRecord = !!result.note || Object.keys(result.answers).length > 0 || result.verifyResults.length > 0;
  return (
    <div className="relative flex items-start gap-3 py-3">
      <StatusNode status={result.status} />
      <div className="min-w-0 flex-1">
        <div className="flex items-center gap-2 flex-wrap">
          <span className="text-(length:--sem-text-label) tnum" style={{ color: 'var(--sem-muted)' }}>{n}</span>
          <span className="text-(length:--sem-text-meta) font-semibold" style={{ color: current || done ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>{result.title}</span>
          <Pill tint={st.color}>{st.label}</Pill>
          {result.effectiveStrictness && done && (
            <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }} title="Whether this step stopped on failure, warned, or had its checks turned off">Checks {stepGateSkipped(result.effectiveStrictness) ? 'were turned off' : strictnessWords(result.effectiveStrictness)}</span>
          )}
        </div>

        {/* every completed step is reopenable, a clean pass must still be inspectable, not just the notable ones */}
        {done && (
          <>
            <button onClick={() => setOpen((o) => !o)} aria-expanded={open} className="mt-1 flex items-center gap-1 text-(length:--sem-text-label) font-medium" style={{ color: 'var(--sem-muted)' }}>
              <Caret open={open} />
              {open ? 'Hide record' : 'Record'}
            </button>
            {open && (
              <div className="mt-2 flex flex-col gap-2">
                {result.note && <div className="sem-text-meta">{result.note}</div>}
                {Object.entries(result.answers).map(([nm, a]) => <AnswerRow key={nm} name={nm} a={a} />)}
                {result.verifyResults.length > 0 && (
                  <div className="flex flex-wrap gap-2">{result.verifyResults.map((v, i) => <VerifyChip key={v.kind + i} v={v} />)}</div>
                )}
                {!hasRecord && <div className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>No questions or checks were recorded for this step.</div>}
              </div>
            )}
          </>
        )}

        {current && (
          <CurrentStepCard current={current}
            onSubmit={(json, gate) => onSubmit(current.stepId, json, gate)}
            onSkip={(reason) => onSkip(current.stepId, reason)} onAbort={onAbort} />
        )}
      </div>
    </div>
  );
}

function personGateError(raw: string) {
  if (/Ask the user/i.test(raw) || /\{"declined"/.test(raw) || /gate input/.test(raw))
    return raw.includes('may not be declined') ? 'This one cannot be declined. Type an answer.'
      : raw.includes('without a reason') ? 'Say why you are declining.'
      : 'This question still needs an answer.';
  return raw;
}

export function CurrentStepCard({ current, onSubmit, onSkip, onAbort }: {
  current: CurrentStepView; onSubmit: (answersJson: string, callGate?: string) => Promise<void>;
  onSkip: (reason: string) => Promise<void>; onAbort: (reason: string) => Promise<void>;
}) {
  const [draft, setDraft] = useState(() => providedAnswerDraft(current.providedAnswers));
  const { vals, declined, reasons } = draft;
  const edited = useRef(new Set<string>());
  useEffect(() => {
    if (current.providedAnswers) setDraft((previous) => providedAnswerDraft(current.providedAnswers, previous, edited.current));
  }, [current.providedAnswers]);
  const [err, setErr] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [showInstructions, setShowInstructions] = useState(false);
  // The run view carries the gate flattened into questions + verifyKinds, not a GateSpec. Rebuild the shape
  // stepSummary needs so Runs and the Library detail say exactly the same sentence about the same step.
  // F12: what Semanticus reads as data (a check's probe, its anchors, the grid) the assistant fills, so the person is
  // never asked for a fenced JSON array or a "Machine field". Only the questions answered in words are theirs.
  const fills = new Set(current.assistantFills ?? []);
  const yours = current.questions.filter((q) => !fills.has(q.name));
  const theirs = current.questions.filter((q) => fills.has(q.name));
  const [selfFill, setSelfFill] = useState<Record<string, boolean>>({});
  const currentGate: GateSpec = {
    strictness: current.effectiveStrictness ?? null,
    inputs: yours,
    verify: current.verifyKinds.map((kind) => ({ kind })),
  };

  const declineIncomplete = current.questions.some((q) => q.required !== 'required' && declined[q.name] && !(reasons[q.name] || '').trim());
  const [fieldErr, setFieldErr] = useState<Record<string, string>>({});
  const [callGate, setCallGate] = useState<string | null>(null);

  const doSubmit = async (gateOverride?: string) => {
    const nextErr: Record<string, string> = {};
    if (!gateSkipped) {
      for (const q of current.questions) {
        if (q.required === 'optional') continue;
        if (q.required === 'required' && declined[q.name]) nextErr[q.name] = 'This one cannot be declined. Type an answer.';
        else if (declined[q.name] && !(reasons[q.name] || '').trim()) nextErr[q.name] = 'Say why you are declining.';
        else if (!declined[q.name] && !(vals[q.name] || '').trim() && !edited.current.has(q.name))
          nextErr[q.name] = fills.has(q.name) ? 'Your assistant has not filled this in yet.' : 'This question still needs an answer.';
      }
    }
    if (Object.keys(nextErr).length > 0) {
      setFieldErr(nextErr);
      setErr(Object.keys(nextErr).length === 1
        ? Object.values(nextErr)[0]
        : `${Object.keys(nextErr).length} questions still need an answer before this step can be submitted.`);
      return;
    }
    if (declineIncomplete) { setErr('Say why you are declining.'); return; }
    setBusy(true); setErr(null); setFieldErr({});
    try {
      const payload: Record<string, unknown> = {};
      for (const q of current.questions) {
        if (q.required !== 'required' && declined[q.name]) payload[q.name] = { declined: true, reason: (reasons[q.name] || '').trim() };
        else if (edited.current.has(q.name) || (vals[q.name] || '').trim() !== '') payload[q.name] = vals[q.name] ?? '';
      }
      // gateArg drops anything that is not a gate string. A handler bound straight to onClick is called with
      // the DOM click event, which is not a gate and cannot be cloned onto the wire (D-171).
      await onSubmit(JSON.stringify(payload), gateArg(gateOverride) ?? callGate ?? undefined);
    } catch (e) {
      const raw = String((e as Error).message ?? e);
      setErr(personGateError(raw));
    } finally { setBusy(false); }
  };

  // Render from THIS run's frozen strictness snapshot (effectiveStrictness), never the current model-wide toggle:
  // a mid-run enforcement flip can't tear a run, so the UI must show what the engine will actually do.
  const gateSkipped = stepGateSkipped(current.effectiveStrictness);
  const strictnessLabel = current.effectiveStrictness && !gateSkipped
    ? (current.effectiveStrictness === 'hard' ? 'Checks must pass' : current.effectiveStrictness === 'warn' ? 'Checks warn only' : `Checks ${strictnessWords(current.effectiveStrictness)}`)
    : null;

  const field = (q: GateInput, nested = false) => (
    <GateField key={q.name} q={q} nested={nested}
      value={vals[q.name] ?? ''} declined={!!declined[q.name]} reason={reasons[q.name] ?? ''} error={fieldErr[q.name]}
      onValue={(v) => { edited.current.add(q.name); setDraft((m) => ({ ...m, vals: { ...m.vals, [q.name]: v } })); }}
      onDecline={(d) => { edited.current.add(q.name); setDraft((m) => ({ ...m, declined: { ...m.declined, [q.name]: d } })); }}
      onReason={(r) => { edited.current.add(q.name); setDraft((m) => ({ ...m, reasons: { ...m.reasons, [q.name]: r } })); }} />
  );

  return (
    <div className="mt-2 sem-wf-step-now">
      {strictnessLabel && (
        <div className="mb-2"><Pill>{strictnessLabel}</Pill></div>
      )}
      <div className="text-(length:--sem-text-meta)" style={{ color: 'var(--sem-fg)' }}>{stepSummary(current.ops, currentGate)}</div>
      <AssistantInstructions md={current.instructions} open={showInstructions} onToggle={() => setShowInstructions((o) => !o)} />

      {current.handOff?.gateOff && (
        <div className="mt-3 flex flex-col gap-2">
          <Banner color="var(--sem-warn)"><b>This step hands off to {current.handOff.calleeTitle || 'another workflow'}.</b>
          <div className="sem-text-meta">Its checks are turned off, so it asks no questions and hands nothing back.{current.handOff.nextStepTitle ? ` ${current.handOff.nextStepTitle} needs those values.` : ''}</div></Banner>
          <div className="mt-2 flex gap-2">
            <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" onClick={() => { setCallGate('on'); void doSubmit('on'); }}>Turn its checks on for this run</button>
            <button type="button" className="sem-btn sem-btn-sm" onClick={() => { setCallGate('off'); void doSubmit('off'); }}>Continue without them</button>
          </div>
        </div>
      )}
      {current.ops.length > 0 && (
        <div className="flex items-center gap-1 mt-3 flex-wrap">
          <span className="text-(length:--sem-text-label) font-semibold mr-1" style={{ color: 'var(--sem-muted)' }}>Actions</span>
          {current.ops.map((op) => <OpChip key={op} op={op} onStage={(id) => { const planQ = current.questions.find((q) => q.type === 'planItem'); if (planQ) { edited.current.add(planQ.name); setDraft((m) => ({ ...m, vals: { ...m.vals, [planQ.name]: id } })); } }} />)}
        </div>
      )}

      {/* enforcement off (for THIS run) REPLACES the gate form with the honest skipped state, no half-shown
          questions with a notice tacked on. Enforced: the gate form (questions + the checks the engine will run). */}
      {gateSkipped ? (
        <div className="mt-3">
          <Banner color="var(--sem-warn)"><b>Checks are turned off for this step</b>
          <div className="sem-text-meta">It asks you nothing and records nothing as checked. Submit to move the run on.</div></Banner>
        </div>
      ) : (
        <>
          {yours.length > 0 && (
            <div className="mt-3 flex flex-col gap-3">
              <div className="text-(length:--sem-text-label) font-semibold" style={{ color: 'var(--sem-muted)' }}>What this step needs from you</div>
              {yours.map((q) => field(q))}
            </div>
          )}
          {theirs.length > 0 && (
            <div className="mt-3 flex flex-col gap-2" data-testid="wf-assistant-fills">
              <div className="text-(length:--sem-text-label) font-semibold" style={{ color: 'var(--sem-muted)' }}>Your assistant fills these in</div>
              <div className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Semanticus reads them as data, so their shape has to be exact.</div>
              {theirs.map((q) => {
                const filled = !declined[q.name] && (vals[q.name] ?? '').trim() !== '';
                return <div key={q.name} className="sem-wf-gate-field">
                  <div className="flex items-center gap-2">
                    <span className="text-(length:--sem-text-meta) flex-1" style={{ color: 'var(--sem-fg)' }}>{uiLabel(q.name)}</span>
                    <span className="sem-chip">{filled ? 'Filled' : declined[q.name] ? 'Declined' : 'Waiting for your assistant'}</span>
                    <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" aria-expanded={!!selfFill[q.name]}
                      onClick={() => setSelfFill((m) => ({ ...m, [q.name]: !m[q.name] }))}>{selfFill[q.name] ? 'Hide' : 'Fill it yourself'}</button>
                  </div>
                  {selfFill[q.name] && field(q, true)}
                  {!selfFill[q.name] && fieldErr[q.name] && <div className="mt-2 text-(length:--sem-text-label)" style={{ color: 'var(--sem-bad)' }}>{fieldErr[q.name]}</div>}
                </div>;
              })}
            </div>
          )}
          {current.verifyKinds.length > 0 && (
            <div className="mt-3">
              <div className="text-(length:--sem-text-label) font-semibold mb-1" style={{ color: 'var(--sem-muted)' }}>Semanticus checks, after you submit</div>
              <div className="flex flex-wrap gap-2">
                {current.verifyKinds.map((k) => (
                  <span key={k} className="sem-chip">
                    <span style={{ color: 'var(--sem-muted)' }}>○</span> {checkTitle(k)}
                  </span>
                ))}
              </div>
            </div>
          )}
        </>
      )}

      {err && (
        <div className="mt-3"><Banner color="var(--sem-bad)">{err}</Banner></div>
      )}

      <div className="mt-3 flex items-center gap-2">
        <Button primary disabled={busy} onClick={() => { void doSubmit(); }}>{busy ? 'Submitting…' : 'Submit step'}</Button>
        <ReasonButton label="Skip with reason" placeholder="Why skip this step?" title="Skip this step (a reason is required)." onConfirm={onSkip} />
        <ReasonButton label="Abort" placeholder="Why stop this run?" title="Abort the whole run (a reason is required)." onConfirm={onAbort} danger />
      </div>
    </div>
  );
}

function GateField({ q, value, declined, reason, error, nested, onValue, onDecline, onReason }: {
  q: GateInput; value: string; declined: boolean; reason: string; error?: string; nested?: boolean;
  onValue: (v: string) => void; onDecline: (d: boolean) => void; onReason: (r: string) => void;
}) {
  const required = q.required === 'required';
  return (
    <div className={nested ? 'mt-2' : 'sem-wf-gate-field'}>
      <div className="flex items-start gap-2">
        <label className="text-(length:--sem-text-meta) flex-1" style={{ color: 'var(--sem-fg)' }}>{q.question || uiLabel(q.name)}</label>
        <span className="sem-chip">{q.type === 'planItem' ? 'Pending rename' : uiLabel(q.type)}</span>
        {required && <span title="This one cannot be declined." className="sem-chip sem-chip-bad">Required</span>}
        {q.required === 'optional' && <span className="shrink-0 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Optional</span>}
      </div>
      {!declined && q.type === 'planItem' && <PlanItemPicker value={value} onValue={onValue} />}
      {!declined && q.type !== 'planItem' && (
        <textarea value={value} onChange={(e) => onValue(e.target.value)} rows={q.type === 'text' ? 2 : 1} spellCheck={false} placeholder={uiLabel(q.name)}
          className="sem-field mt-2 w-full resize-y"
          />
      )}
      {q.required !== 'required' && (
      <div className="mt-2 flex items-center gap-2">
        <label className="flex items-center gap-2 text-(length:--sem-text-label) cursor-pointer" style={{ color: 'var(--sem-muted)' }}>
          <input type="checkbox" checked={declined} onChange={(e) => onDecline(e.target.checked)} />
          I can't answer this. Record why instead.
        </label>
        {declined && <span className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Recorded as an explicit decline.</span>}
      </div>
      )}
      {declined && q.required !== 'required' && (
        <textarea value={reason} onChange={(e) => onReason(e.target.value)} rows={2} spellCheck={false} placeholder="Say why you are declining."
          className="sem-field mt-2 w-full resize-y"
          />
      )}
      {error && <div className="mt-2 text-(length:--sem-text-label)" style={{ color: 'var(--sem-bad)' }}>{error}</div>}
    </div>
  );
}

function PlanItemPicker({ value, onValue }: { value: string; onValue: (v: string) => void }) {
  const [items, setItems] = useState<{ id: string; title?: string; objRef?: string; after?: string; status?: string }[]>([]);
  useEffect(() => {
    rpc<{ items?: { id: string; title?: string; objRef?: string; after?: string; status?: string }[] }>('getPlan')
      .then((p) => setItems(p.items ?? [])).catch(() => undefined);
  }, []);
  const stage = async () => {
    const target = items[0]?.objRef;
    if (!target) return;
    const plan = await rpc<{ items?: { id: string }[] }>('addPlanItem', target, 'rename', items[0]?.after ?? '', 'Pending rename', null, null);
    const id = plan.items?.[plan.items.length - 1]?.id;
    if (id) onValue(id);
  };
  return (
    <div className="mt-2">
      {items.length === 0 && <div className="mb-2 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>No pending rename yet. Add it to the changes from the answers already given.</div>}
      <select value={value} onChange={(e) => onValue(e.target.value)} className="sem-field w-full">
        <option value="">Pick a pending rename</option>
        {items.map((it) => <option key={it.id} value={it.id}>{it.title || it.objRef || it.id} {it.after ? `→ ${it.after}` : ''} ({it.status || 'pending'})</option>)}
      </select>
      <button type="button" className="mt-2 text-(length:--sem-text-label) font-semibold sem-wf-link" onClick={() => void stage()}>Add it to the changes</button>
    </div>
  );
}

function AnswerRow({ name, a }: { name: string; a: AnswerValue }) {
  if (isDeclined(a)) {
    return (
      <div className="sem-inline sem-text-meta" style={{ color: 'var(--sem-fg)' }}>
        <Dot color="var(--sem-warn)" /><span className="font-semibold">{uiLabel(name)} · declined</span>
        {a.declineReason && <span style={{ color: 'var(--sem-fg)' }}>: {a.declineReason}</span>}
      </div>
    );
  }
  return (
    <div className="sem-text-meta" style={{ color: 'var(--sem-fg)' }}>
      <span className="font-semibold" style={{ color: 'var(--sem-fg)' }}>{uiLabel(name)}</span>
      <span style={{ color: 'var(--sem-muted)' }}> · {isAnswered(a) ? a.value : '(no value)'}</span>
    </div>
  );
}

function VerifyChip({ v }: { v: VerifyResult }) {
  const color = verifyColor(v.status);
  // unavailable is BLOCKED-with-a-reason (amber '!'), distinct from the muted not-run glyph, the two must never read alike.
  const glyph = v.status === 'passed' ? '✓' : v.status === 'failed' ? '✕' : v.status === 'unavailable' ? '!' : '⤼';
  const tip = v.status === 'unavailable' && v.missing ? `Missing: ${v.missing}${v.detail ? `
${v.detail}` : ''}` : v.detail;
  return (
    <span title={tip} className="sem-chip" style={{ color: 'var(--sem-fg)' }}>
      <Dot color={color} />
      <span aria-hidden>{glyph}</span> {checkTitle(v.kind)}{v.status === 'unavailable' ? ' (blocked)' : ''}
    </span>
  );
}

// ---- a finished run, on its History entry (contract 11.3 rows 38 and 46; the "Make it reusable" door) ----------

/** "Make it reusable": the run's workflow, copied into your workflows under a new name, carrying the run it came
 *  from (derived_from), so the list marks it "From a run". The copy is written create-only through save_workflow,
 *  the op the agent door's distill skill uses, so a name that is taken or shadows a required workflow is refused
 *  by the engine with its own sentence. Returns the new workflow's title. */
export async function makeReusable(run: { runId: string; workflow: string; title?: string | null }): Promise<{ name: string; title: string }> {
  const library = await rpc<WorkflowInfo[]>('listWorkflows');
  const doc = await rpc<{ exactText: string }>('getWorkflowDocument', run.workflow);
  const name = reusableName(run.workflow, library as WfInfo[]);
  const title = `${run.title || run.workflow}, from a run`;
  await rpc('saveWorkflow', name, reusableCopy(doc.exactText, name, title, run.runId), 'human', true);
  return { name, title };
}

export function RunEntryActions({ report, onOpenWorkflows }: {
  report: { runId: string; workflow?: string | null; title?: string | null; status?: string | null };
  onOpenWorkflows?: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const workflow = report.workflow;
  return <div className="ph-acts" data-testid="ph-run-actions">
    <button type="button" className="sem-btn sem-btn-sm" data-testid="ph-run-report" onClick={() => setOpen(true)}>Workflow report</button>
    {workflow && !done && <button type="button" className="sem-btn sem-btn-sm" disabled={busy} data-testid="ph-make-reusable"
      title="Saves this run's workflow as one of your own, marked From a run"
      onClick={() => { setBusy(true); setError(null); void makeReusable({ runId: report.runId, workflow, title: report.title })
        .then((r) => setDone(r.title)).catch((e) => setError(String((e as Error).message ?? e))).finally(() => setBusy(false)); }}>
      {busy ? 'Saving…' : 'Make it reusable'}</button>}
    {done && <span className="ph-conseq" role="status" data-testid="ph-reusable-done">Saved as {done} in your workflows.{' '}
      {onOpenWorkflows && <button type="button" className="plan-link" onClick={onOpenWorkflows}>Open Workflows</button>}</span>}
    {error && <span className="ph-conseq" role="alert">{error}</span>}
    {open && <RunReportDialog run={{ runId: report.runId, workflow, title: report.title }} onClose={() => setOpen(false)} />}
  </div>;
}
