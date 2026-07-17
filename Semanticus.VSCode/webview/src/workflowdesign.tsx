import { useEffect, useMemo, useRef, useState } from 'react';
import { rpc } from './bridge';
import {
  Panel, SectionTitle, Button, Banner, Pill, OpChip, SourceBadge, RailLine, NumberNode,
  type WorkflowDef, type WorkflowInfo, type WorkflowStep, type GateInput, type VerifySpec,
} from './workflows';
import { uiLabel } from './copy';

// ===================================================================================================
// Design mode — the workflow DESIGNER (docs/workflow-designer-plan.md §3/§4). The designer edits a
// structured Draft and EMITS the markdown deterministically; the file stays the artifact (git-diffable,
// hand-editable, readable by the AI assistant over MCP). Nothing here validates "for real" — save_workflow
// re-parses on the engine and a refusal comes back verbatim, so the designer can never write a file the
// parser refuses. Instruction text is preserved byte-for-byte; only the gate fence is generated.
// ===================================================================================================

interface OpInfo { name: string; description?: string | null }

const EVIDENCE_OP = 'export_workflow_evidence';
const actionPresentation = (op: OpInfo) => op.name === EVIDENCE_OP
  ? { label: 'Evidence report', description: 'After the workflow finishes, export its sealed HTML and JSON evidence report.' }
  : { label: op.name, description: op.description ?? '' };

interface DraftInput { name: string; question: string; type: string; required: string }
interface DraftVerify { kind: string; when: string; probe: string; scope: string; intent: string }
interface DraftStep { title: string; instructions: string; ops: string[]; strictness: string; inputs: DraftInput[]; verify: DraftVerify[] }
interface Draft {
  name: string; title: string; description: string; version: number;
  strictness: string;            // '' = none (engine defaults to hard)
  triggers: string[];
  steps: DraftStep[];
}

const INPUT_TYPES = ['text', 'verification', 'enum', 'number', 'objectRef'];
const REQUIREDS = ['answer-or-decline', 'required', 'optional'];
// Mirrors WorkflowParser.VerifyKinds minus workflow_admissible (the author-a-workflow meta-gate is
// deliberately agent-authored, not a designer pick). KIND_HINTS = what each engine check proves, in plain
// words, surfaced as the kind picker's hover hint.
const VERIFY_KINDS = ['dax_probe', 'dax_equivalence', 'readiness_rescan', 'bpa_clean', 'benchmark_delta', 'interview_replay',
  'baseline_captured', 'impact_assessment', 'baseline_exists', 'baseline_unchanged', 'tests_replay', 'plan_item_staged', 'plan_item_applied'];
const KIND_HINTS: Record<string, string> = {
  dax_probe: 'Checks the target measure against a known-good number collected by a gate question.',
  dax_equivalence: 'Proves the edited expression still returns the same values as the recorded original.',
  readiness_rescan: 'Re-scores AI readiness; the step must not make the grade worse.',
  bpa_clean: 'Checks the step introduced no new best-practice violations.',
  benchmark_delta: 'Times the measure against a recorded baseline; no speed regression past tolerance.',
  interview_replay: 'Replays the saved Model Interview pack; the questions your users ask must still come back right (no target or probe needed).',
  baseline_captured: 'Confirms a representative live-value baseline was captured for the target.',
  impact_assessment: 'Runs the engine-owned impact assessment for the declared target and intent.',
  baseline_exists: 'Confirms the submitted capture id belongs to a held representative-value baseline.',
  baseline_unchanged: 'Re-runs the submitted baseline and requires complete, unchanged values.',
  tests_replay: 'Runs the complete Tests suite and records the latest model-session result.',
  plan_item_staged: 'Binds the submitted item id to the exact proposed rename in Change Plan.',
  plan_item_applied: 'Confirms the exact reviewed rename item was applied and the new ref resolves.',
};
const STRICTNESS = ['hard', 'warn', 'off'];
const KEBAB = /^[a-z0-9]+(-[a-z0-9]+)*$/;

// ---- def ⇄ draft ----------------------------------------------------------------------------------

export function defToDraft(def: WorkflowDef): Draft {
  return {
    name: def.name,
    title: def.title ?? '',
    description: def.description ?? '',
    version: def.version || 1,
    strictness: def.strictness ?? '',
    triggers: def.triggers ?? [],
    steps: def.steps.map((s: WorkflowStep) => ({
      title: s.title ?? '',
      instructions: s.instructions ?? '',
      ops: s.ops ?? [],
      strictness: s.gate?.strictness ?? '',
      inputs: (s.gate?.inputs ?? []).map((i: GateInput) => ({ name: i.name, question: i.question ?? '', type: i.type ?? 'text', required: i.required ?? 'answer-or-decline' })),
      verify: (s.gate?.verify ?? []).map((v: VerifySpec) => ({ kind: v.kind, when: v.when ?? '', probe: v.probe ?? '', scope: v.scope ?? '', intent: v.intent ?? '' })),
    })),
  };
}

export function emptyDraft(): Draft {
  return { name: '', title: '', description: '', version: 1, strictness: '', triggers: [], steps: [emptyStep()] };
}
function emptyStep(): DraftStep {
  return { title: 'New step', instructions: '', ops: [], strictness: '', inputs: [], verify: [] };
}

// Deterministic markdown emission (spec §4): stable key order, one gate fence per step (ops first),
// questions double-quoted. The parser can't escape quotes or see past " #" as a comment marker, so
// values are sanitised the one honest way: embedded double-quotes become singles inside quoted strings,
// and a frontmatter value containing " #" gets quoted.
const q = (s: string) => `"${(s || '').replace(/"/g, "'")}"`;
const fmVal = (s: string) => (s.includes(' #') || s.includes('"') ? q(s) : s);

export function emitMarkdown(d: Draft): string {
  const out: string[] = ['---', `name: ${d.name}`];
  if (d.title) out.push(`title: ${fmVal(d.title)}`);
  if (d.description) out.push(`description: ${fmVal(d.description)}`);
  out.push(`version: ${d.version}`);
  if (d.strictness) out.push(`strictness: ${d.strictness}`);
  if (d.triggers.length) out.push(`triggers: [${d.triggers.join(', ')}]`);
  out.push('---');
  d.steps.forEach((s, i) => {
    out.push('', `## Step ${i + 1}: ${s.title.trim() || 'Untitled'}`, '');
    if (s.instructions.trim()) out.push(s.instructions.trim(), '');
    const hasGate = s.ops.length || s.strictness || s.inputs.length || s.verify.length;
    if (hasGate) {
      out.push('```yaml gate');
      if (s.ops.length) out.push(`ops: [${s.ops.join(', ')}]`);
      if (s.strictness) out.push(`strictness: ${s.strictness}`);
      if (s.inputs.length) {
        out.push('inputs:');
        for (const inp of s.inputs) {
          out.push(`  - name: ${inp.name}`);
          out.push(`    question: ${q(inp.question)}`);
          if (inp.type && inp.type !== 'text') out.push(`    type: ${inp.type}`);
          if (inp.required && inp.required !== 'answer-or-decline') out.push(`    required: ${inp.required}`);
        }
      }
      if (s.verify.length) {
        out.push('verify:');
        for (const v of s.verify) {
          out.push(`  - kind: ${v.kind}`);
          if (v.when) out.push(`    when: ${v.when}`);
          if (v.probe) out.push(`    probe: ${v.probe}`);
          if (v.scope) out.push(`    scope: ${v.scope}`);
          if (v.intent) out.push(`    intent: ${v.intent}`);
        }
      }
      out.push('```', '');
    }
  });
  return out.join('\n').replace(/\n{3,}/g, '\n\n').trimEnd() + '\n';
}

// Client-side guards for the two structural mistakes the ENGINE parse would mis-read rather than refuse:
// a step-heading line inside an instruction body silently becomes a new step, and a stray gate fence
// opens a second gate. Refuse locally with a pointed message instead of saving something that shifts shape.
function structuralProblems(d: Draft): string[] {
  const problems: string[] = [];
  if (!KEBAB.test(d.name)) problems.push(`'${d.name || '(empty)'}' is not a valid kebab-case name (e.g. my-workflow).`);
  if (d.steps.length === 0) problems.push('A workflow needs at least one step.');
  d.steps.forEach((s, i) => {
    if (/^##\s*Step\s+\d+\s*:/m.test(s.instructions)) problems.push(`Step ${i + 1}: the instruction text contains a '## Step N:' line, which would become a new step. Reword it.`);
    if (/^```\s*yaml\s+gate/m.test(s.instructions)) problems.push(`Step ${i + 1}: the instruction text contains a 'yaml gate' fence; the gate is edited below, not inline.`);
    s.inputs.forEach((inp) => { if (!inp.name.trim()) problems.push(`Step ${i + 1}: a gate input is missing its name.`); });
  });
  return problems;
}

// ---- the design surface ----------------------------------------------------------------------------

export function DesignMode({ info, def, creating, onSaved, onDeleted, layout = 'stack' }: {
  info: WorkflowInfo | null;
  def: WorkflowDef | null;
  creating: boolean;
  onSaved: (name: string) => void;
  onDeleted: () => void;
  // 'outline' = the ratified Author screen: a step outline on the left, one focused pane on the right
  // (Workflow settings, or a single step). 'stack' = the legacy full-chain scroll.
  layout?: 'stack' | 'outline';
}) {
  const readOnlyStock = !creating && info?.source === 'stock';
  const [editing, setEditing] = useState(creating);          // stock opens read-only; Customise unlocks
  const [draft, setDraft] = useState<Draft | null>(creating ? emptyDraft() : def ? defToDraft(def) : null);
  const [dirty, setDirty] = useState(creating);
  const [showRaw, setShowRaw] = useState(false);
  const [saving, setSaving] = useState(false);
  const [attempted, setAttempted] = useState(false);   // structural-problem banners only nag after a save attempt
  const [saveErr, setSaveErr] = useState<string | null>(null);
  const [savedTick, setSavedTick] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  // Outline focus: -1 = the Workflow settings pane, 0..n-1 = a step. Clamped when steps are added/removed.
  const [activeIdx, setActiveIdx] = useState(-1);

  // Re-seed the draft when the selected workflow changes underneath us (only when not mid-edit).
  useEffect(() => {
    if (creating) return;
    if (!dirty) { setDraft(def ? defToDraft(def) : null); setEditing(false); setSaveErr(null); }
  }, [def, creating]); // eslint-disable-line react-hooks/exhaustive-deps

  if (!creating && def?.error) {
    return (
      <Panel>
        <div className="text-[13px] font-semibold" style={{ color: 'var(--sem-bad)' }}>This file doesn't parse, so it can't be edited structurally</div>
        <div className="text-[12px] mt-1" style={{ color: 'var(--sem-muted)' }}>Fix the file in <span className="tnum">.semanticus/workflows</span> (the parse error is shown in Run view), or delete it and rebuild it here.</div>
      </Panel>
    );
  }
  if (!draft) return <Panel><div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading workflow…</div></Panel>;

  const set = (patch: Partial<Draft>) => { setDraft({ ...draft, ...patch }); setDirty(true); };
  const setStep = (i: number, patch: Partial<DraftStep>) => {
    const steps = draft.steps.slice(); steps[i] = { ...steps[i], ...patch }; set({ steps });
  };
  const insertStep = (at: number) => { const steps = draft.steps.slice(); steps.splice(at, 0, emptyStep()); set({ steps }); };
  const removeStep = (i: number) => { if (draft.steps.length <= 1) return; const steps = draft.steps.slice(); steps.splice(i, 1); set({ steps }); };
  const moveStep = (i: number, dir: -1 | 1) => {
    const j = i + dir; if (j < 0 || j >= draft.steps.length) return;
    const steps = draft.steps.slice(); [steps[i], steps[j]] = [steps[j], steps[i]]; set({ steps });
  };

  const problems = structuralProblems(draft);
  const save = async () => {
    setAttempted(true);
    if (problems.length) { setSaveErr(problems.join(' ')); return; }
    setSaving(true); setSaveErr(null);
    try {
      await rpc('saveWorkflow', draft.name, emitMarkdown(draft), 'human');
      setDirty(false); setSavedTick(true); setTimeout(() => setSavedTick(false), 2500);
      onSaved(draft.name);
    } catch (e) {
      setSaveErr(String((e as Error).message ?? e));   // the engine's parse refusal, verbatim — it IS the fix hint
    } finally { setSaving(false); }
  };
  const del = async () => {
    try { await rpc('deleteWorkflow', draft.name, 'human'); onDeleted(); }
    catch (e) { setSaveErr(String((e as Error).message ?? e)); }
  };

  const inputNames = draft.steps.flatMap((s) => s.inputs.map((i) => i.name)).filter(Boolean);
  const idx = Math.min(activeIdx, draft.steps.length - 1);   // clamp against removals
  const addStepAndFocus = () => { const at = draft.steps.length; insertStep(at); setActiveIdx(at); };

  // The frontmatter editor — the workflow's title/version/strictness/description/triggers. Shared by both layouts.
  const frontmatter = (
    <Panel>
      <SectionTitle>Workflow</SectionTitle>
      <div className="grid grid-cols-2 gap-3 mt-2">
        <Field label="Title"><TextInput value={draft.title} disabled={!isEditable()} onChange={(v) => set({ title: v })} placeholder="What this playbook does" /></Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Version"><TextInput value={String(draft.version)} disabled={!isEditable()} onChange={(v) => set({ version: Math.max(1, parseInt(v, 10) || 1) })} /></Field>
          <Field label="Default strictness">
            <Select value={draft.strictness} disabled={!isEditable()} onChange={(v) => set({ strictness: v })}
              options={[['', 'hard (default)'], ...STRICTNESS.map((s) => [s, s] as [string, string])]} />
          </Field>
        </div>
        <div className="col-span-2">
          <Field label="Description"><TextInput value={draft.description} disabled={!isEditable()} onChange={(v) => set({ description: v })} placeholder="Shown in the library and to the AI Assistant" /></Field>
        </div>
        <div className="col-span-2">
          <Field label="Triggers: ops that suggest this workflow (advisory)">
            <OpChipEditor ops={draft.triggers} disabled={!isEditable()} onChange={(triggers) => set({ triggers })} />
          </Field>
        </div>
      </div>
    </Panel>
  );

  // The top action bar (name/save/view-file/delete/customise) and the raw-file view — shared by both layouts.
  const actionBar = (
    <Panel>
      <div className="flex items-center gap-3 flex-wrap">
        {creating ? (
          <label className="flex items-center gap-2 text-[12px]">
            <span style={{ color: 'var(--sem-muted)' }}>Name</span>
            <input value={draft.name} onChange={(e) => set({ name: e.target.value })} placeholder="my-workflow" spellCheck={false} autoFocus
              className="tnum text-[12px] px-2 py-1 rounded-md outline-none w-56"
              style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: `1px solid ${draft.name && !KEBAB.test(draft.name) ? 'var(--sem-bad)' : 'var(--sem-border)'}` }} />
          </label>
        ) : (
          <div className="text-[12px] flex items-center gap-2">
            <span className="tnum font-semibold">{draft.name}.md</span>
            {info && <SourceBadge source={editing && info.source === 'stock' ? 'user' : info.source} />}
            {readOnlyStock && !editing && <Pill>read-only</Pill>}
          </div>
        )}
        <div className="flex-1" />
        {readOnlyStock && !editing ? (
          <Button primary onClick={() => { setEditing(true); setDirty(true); }}
            title="Stock workflows are read-only. Customising saves YOUR copy to .semanticus/workflows, which replaces the built-in one until you delete your copy">
            Customise…
          </Button>
        ) : (
          <>
            <Button onClick={() => setShowRaw(!showRaw)} title="The markdown this designer writes: the file is the artifact">{showRaw ? 'Hide file' : 'View file'}</Button>
            {!creating && info?.source === 'user' && (
              confirmDelete
                ? <Button onClick={del} title="Really delete. Deleting your copy of a stock workflow reverts to the built-in one"><span style={{ color: 'var(--sem-bad)' }}>Confirm delete</span></Button>
                : <Button onClick={() => setConfirmDelete(true)}>Delete…</Button>
            )}
            <Button primary disabled={saving || !dirty} onClick={save}
              title="The file is checked again before saving. A file that fails the check is never saved">{saving ? 'Saving…' : savedTick ? 'Saved ✓' : 'Save'}</Button>
          </>
        )}
      </div>
      {readOnlyStock && !editing && (
        <div className="text-[11.5px] mt-2" style={{ color: 'var(--sem-muted)' }}>
          This is a stock playbook shipped with the engine. You can read everything below; Customise creates your project's editable copy.
        </div>
      )}
      {saveErr && <div className="mt-2"><Banner color="var(--sem-bad)">{saveErr}</Banner></div>}
      {!saveErr && problems.length > 0 && dirty && attempted && <div className="mt-2"><Banner color="var(--sem-accent)">{problems[0]}</Banner></div>}
    </Panel>
  );
  const rawView = (
    <Panel>
      <SectionTitle>The file (deterministic emission)</SectionTitle>
      <pre className="mt-2 rounded-lg px-3 py-2 text-[11.5px] whitespace-pre-wrap overflow-x-auto"
        style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', font: '11.5px/1.55 ui-monospace,SFMono-Regular,Consolas,monospace' }}>
        {emitMarkdown(draft)}
      </pre>
    </Panel>
  );

  if (layout === 'outline') {
    const active = draft.steps[idx];
    return (
      <div className="flex flex-col gap-3">
        {actionBar}
        {showRaw && rawView}
        <div className="grid gap-3" style={{ gridTemplateColumns: '210px minmax(0, 1fr)' }}>
          <div className="rounded-xl border p-2 self-start" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
            <div className="text-[10px] uppercase tracking-wide font-semibold px-2 pt-1 pb-2" style={{ color: 'var(--sem-muted)' }}>Outline</div>
            <div className="flex flex-col gap-0.5 text-[12px]">
              <button onClick={() => setActiveIdx(-1)} className="text-left px-2 py-1.5 rounded-md"
                style={idx < 0 ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)', fontWeight: 600 } : { color: 'var(--sem-muted)' }}>◇ Workflow settings</button>
              {draft.steps.map((s, i) => (
                <button key={i} onClick={() => setActiveIdx(i)} className="text-left px-2 py-1.5 rounded-md truncate"
                  style={i === idx ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)', fontWeight: 600 } : { color: 'var(--sem-muted)' }}>{i + 1} · {s.title.trim() || 'Untitled'}</button>
              ))}
              {isEditable() && (
                <button onClick={addStepAndFocus} className="text-left px-2 py-1.5 rounded-md font-semibold" style={{ color: 'var(--sem-accent)' }}>+ Add step</button>
              )}
            </div>
          </div>
          <div className="min-w-0">
            {idx < 0 || !active ? frontmatter : (
              <StepCard step={active} index={idx} count={draft.steps.length} editable={isEditable()} inputNames={inputNames}
                onChange={(patch) => setStep(idx, patch)}
                onMove={(dir) => { moveStep(idx, dir); setActiveIdx(Math.max(0, Math.min(draft.steps.length - 1, idx + dir))); }}
                onRemove={() => { removeStep(idx); setActiveIdx(Math.max(-1, idx - 1)); }} />
            )}
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-3">
      {actionBar}
      {showRaw && rawView}
      {frontmatter}

      {/* the chain */}
      <div className="relative flex flex-col gap-2">
        <RailLine />
        {draft.steps.map((s, i) => (
          <div key={i}>
            <StepCard
              step={s} index={i} count={draft.steps.length} editable={isEditable()} inputNames={inputNames}
              onChange={(patch) => setStep(i, patch)}
              onMove={(dir) => moveStep(i, dir)}
              onRemove={() => removeStep(i)}
            />
            {isEditable() && <InsertStep onClick={() => insertStep(i + 1)} />}
          </div>
        ))}
      </div>
    </div>
  );

  function isEditable() { return creating || editing || info?.source === 'user'; }
}

// ---- one step card ---------------------------------------------------------------------------------

function StepCard({ step, index, count, editable, inputNames, onChange, onMove, onRemove }: {
  step: DraftStep; index: number; count: number; editable: boolean; inputNames: string[];
  onChange: (patch: Partial<DraftStep>) => void; onMove: (dir: -1 | 1) => void; onRemove: () => void;
}) {
  const setInput = (i: number, patch: Partial<DraftInput>) => {
    const inputs = step.inputs.slice(); inputs[i] = { ...inputs[i], ...patch }; onChange({ inputs });
  };
  const setVerify = (i: number, patch: Partial<DraftVerify>) => {
    const verify = step.verify.slice(); verify[i] = { ...verify[i], ...patch }; onChange({ verify });
  };
  const gateActive = step.inputs.length > 0 || step.verify.length > 0 || !!step.strictness;

  return (
    <div className="flex gap-3 relative">
      <NumberNode n={index + 1} />
      <div className="flex-1 min-w-0 rounded-xl border p-3" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
        <div className="flex items-center gap-2">
          <input value={step.title} disabled={!editable} onChange={(e) => onChange({ title: e.target.value })} placeholder="Step title" spellCheck={false}
            className="flex-1 min-w-0 text-[13px] font-semibold px-2 py-1 rounded-md outline-none disabled:opacity-100"
            style={{ background: editable ? 'var(--sem-surface-2)' : 'transparent', color: 'var(--sem-fg)', border: editable ? '1px solid var(--sem-border)' : '1px solid transparent' }} />
          {editable && (
            <div className="flex items-center gap-1 shrink-0">
              <IconBtn label="↑" title="Move up" disabled={index === 0} onClick={() => onMove(-1)} />
              <IconBtn label="↓" title="Move down" disabled={index === count - 1} onClick={() => onMove(1)} />
              <IconBtn label="✕" title={count <= 1 ? 'A workflow needs at least one step' : 'Remove step'} disabled={count <= 1} onClick={onRemove} danger />
            </div>
          )}
        </div>

        {/* actions — the chained MCP primitives */}
        <div className="mt-2.5">
          <SectionTitle>Actions</SectionTitle>
          <div className="mt-1">
            <OpChipEditor ops={step.ops} disabled={!editable} evidenceQuickAdd onChange={(ops) => onChange({ ops })} />
            {step.ops.includes(EVIDENCE_OP) && (
              <div className="mt-1 text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>
                Evidence exports only after the run is completed or aborted, so an active run is never presented as final.
              </div>
            )}
          </div>
        </div>

        {/* instructions — verbatim, the skill half */}
        <div className="mt-2.5">
          <SectionTitle>Instructions</SectionTitle>
          <textarea value={step.instructions} disabled={!editable} onChange={(e) => onChange({ instructions: e.target.value })} spellCheck={false}
            placeholder="What the AI Assistant (or a human) is told at this step. Use direct instructions and name the actions to take. Preserved verbatim."
            rows={Math.min(10, Math.max(3, step.instructions.split('\n').length + 1))}
            className="mt-1 w-full text-[12px] px-2.5 py-2 rounded-lg outline-none resize-y disabled:opacity-100"
            style={{ background: editable ? 'var(--sem-surface-2)' : 'transparent', color: 'var(--sem-fg)', border: `1px solid ${editable ? 'var(--sem-border)' : 'transparent'}`, font: '12px/1.6 ui-monospace,SFMono-Regular,Consolas,monospace' }} />
          <WordBudget text={step.instructions} />
        </div>

        {/* the gate — constraints */}
        <div className="mt-2.5">
          <div className="flex items-center gap-2">
            <SectionTitle>Gate</SectionTitle>
            {!gateActive && <span className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>none; this step only teaches</span>}
            <div className="flex-1" />
            {editable && (
              <div className="flex items-center gap-1.5">
                <MiniBtn onClick={() => onChange({ inputs: [...step.inputs, { name: '', question: '', type: 'text', required: 'answer-or-decline' }] })}>+ question</MiniBtn>
                <MiniBtn onClick={() => onChange({ verify: [...step.verify, { kind: 'bpa_clean', when: '', probe: '', scope: '', intent: '' }] })}>+ verification check</MiniBtn>
                <Select value={step.strictness} disabled={false} small onChange={(v) => onChange({ strictness: v })}
                  options={[['', 'strictness: inherit'], ...STRICTNESS.map((s) => [s, `strictness: ${s}`] as [string, string])]} />
              </div>
            )}
          </div>

          {step.inputs.length > 0 && (
            <div className="mt-1.5 flex flex-col gap-1.5">
              {step.inputs.map((inp, i) => (
                <div key={i} className="grid gap-1.5 items-center" style={{ gridTemplateColumns: '150px 1fr 110px 150px 24px' }}>
                  <TextInput mono value={inp.name} disabled={!editable} onChange={(v) => setInput(i, { name: v })} placeholder="inputName" />
                  <TextInput value={inp.question} disabled={!editable} onChange={(v) => setInput(i, { question: v })} placeholder="The question the agent must ask the user" />
                  <Select value={inp.type} disabled={!editable} small onChange={(v) => setInput(i, { type: v })} options={INPUT_TYPES.map((t) => [t, uiLabel(t)] as [string, string])} />
                  <Select value={inp.required} disabled={!editable} small onChange={(v) => setInput(i, { required: v })} options={REQUIREDS.map((r) => [r, r] as [string, string])} />
                  {editable ? <IconBtn label="✕" title="Remove" onClick={() => { const inputs = step.inputs.slice(); inputs.splice(i, 1); onChange({ inputs }); }} danger /> : <span />}
                </div>
              ))}
            </div>
          )}

          {step.verify.length > 0 && (
            <div className="mt-1.5 flex flex-col gap-1.5">
              {step.verify.map((v, i) => (
                <div key={i} className="grid gap-1.5 items-center" style={{ gridTemplateColumns: '160px 1fr 140px 100px 110px 24px' }}>
                  <Select value={v.kind} disabled={!editable} small onChange={(k) => setVerify(i, { kind: k })} options={VERIFY_KINDS.map((k) => [k, uiLabel(k)] as [string, string])} title={KIND_HINTS[v.kind]} />
                  <Select value={v.when} disabled={!editable} small onChange={(w) => setVerify(i, { when: w })}
                    options={[['', 'when: always'], ...inputNames.map((n) => [`inputs.${n}.answered`, `when: ${n} answered`] as [string, string])]} />
                  <Select value={v.probe} disabled={!editable} small onChange={(p) => setVerify(i, { probe: p })}
                    options={[['', 'Probe: none'], ...inputNames.map((n) => [n, `Probe: ${n}`] as [string, string])]} />
                  <Select value={v.scope} disabled={!editable} small onChange={(sc) => setVerify(i, { scope: sc })}
                    options={[['', 'Scope: none'], ['object', 'Object'], ['model', 'Whole model']]} />
                  <Select value={v.intent} disabled={!editable || v.kind !== 'impact_assessment'} small onChange={(intent) => setVerify(i, { intent })}
                    options={[['', 'Intent: none'], ['change', 'Change'], ['rename', 'Rename'], ['remove', 'Remove'], ['restructure', 'Restructure']]} />
                  {editable ? <IconBtn label="✕" title="Remove" onClick={() => { const verify = step.verify.slice(); verify.splice(i, 1); onChange({ verify }); }} danger /> : <span />}
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

// ---- op chips + picker (the "chain an action" affordance, fed by the live tool catalog) --------------

function OpChipEditor({ ops, disabled, evidenceQuickAdd = false, onChange }: {
  ops: string[]; disabled: boolean; evidenceQuickAdd?: boolean; onChange: (ops: string[]) => void;
}) {
  const [open, setOpen] = useState(false);
  return (
    <div className="flex items-center gap-1.5 flex-wrap">
      {ops.map((op) => (
        <span key={op} className="flex items-center">
          <OpChip op={op} />
          {!disabled && (
            <button onClick={() => onChange(ops.filter((o) => o !== op))} title="Remove"
              className="text-[10px] ml-0.5 px-0.5" style={{ color: 'var(--sem-muted)' }}>✕</button>
          )}
        </span>
      ))}
      {ops.length === 0 && disabled && <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>None</span>}
      {!disabled && evidenceQuickAdd && !ops.includes(EVIDENCE_OP) && (
        <MiniBtn title="Add a sealed evidence report after this workflow finishes" onClick={() => onChange([...ops, EVIDENCE_OP])}>+ evidence report</MiniBtn>
      )}
      {!disabled && (open
        ? <OpPicker exclude={ops} onPick={(op) => { onChange([...ops, op]); setOpen(false); }} onClose={() => setOpen(false)} />
        : <MiniBtn onClick={() => setOpen(true)}>+ action</MiniBtn>)}
    </div>
  );
}

// The searchable picker over get_op_catalog — reflected from the engine's real tool surface, so the
// designer can only chain ops that actually exist. Catalog is fetched once per webview session.
let catalogCache: OpInfo[] | null = null;
function OpPicker({ exclude, onPick, onClose }: { exclude: string[]; onPick: (op: string) => void; onClose: () => void }) {
  const [catalog, setCatalog] = useState<OpInfo[] | null>(catalogCache);
  const [filter, setFilter] = useState('');
  const boxRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!catalogCache) rpc<OpInfo[]>('getOpCatalog').then((c) => { catalogCache = c; setCatalog(c); }).catch(() => setCatalog([]));
    const onDoc = (e: MouseEvent) => { if (boxRef.current && !boxRef.current.contains(e.target as Node)) onClose(); };
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, [onClose]);
  const hits = useMemo(() => {
    const ex = new Set(exclude);
    const f = filter.trim().toLowerCase();
    return (catalog ?? []).filter((o) => {
      const p = actionPresentation(o);
      return !ex.has(o.name) && (!f || o.name.includes(f) || p.label.toLowerCase().includes(f) || p.description.toLowerCase().includes(f));
    }).sort((a, b) => Number(b.name === EVIDENCE_OP) - Number(a.name === EVIDENCE_OP)).slice(0, 8);
  }, [catalog, exclude, filter]);
  return (
    <div ref={boxRef} className="relative">
      <input autoFocus value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Search ops…" spellCheck={false}
        onKeyDown={(e) => { if (e.key === 'Escape') onClose(); if (e.key === 'Enter' && hits[0]) onPick(hits[0].name); }}
        className="tnum text-[11px] px-2 py-1 rounded-md outline-none w-44"
        style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid color-mix(in srgb, var(--sem-accent) 45%, transparent)' }} />
      <div className="absolute z-30 mt-1 w-[360px] rounded-lg border overflow-hidden shadow-lg"
        style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
        {catalog == null ? (
          <div className="px-3 py-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Loading the op catalog…</div>
        ) : hits.length === 0 ? (
          <div className="px-3 py-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>No matching ops.</div>
        ) : hits.map((o) => {
          const p = actionPresentation(o);
          return <button key={o.name} onClick={() => onPick(o.name)}
            className="w-full text-left px-3 py-1.5 hover:opacity-80"
            style={{ background: 'transparent' }}>
            <div className="text-[11.5px] font-semibold" style={{ color: 'var(--sem-fg)' }}>{p.label}</div>
            {p.label !== o.name && <div className="tnum text-[9.5px]" style={{ color: 'var(--sem-muted)' }}>{o.name}</div>}
            {p.description && <div className="text-[10.5px] truncate" style={{ color: 'var(--sem-muted)' }}>{p.description}</div>}
          </button>
        })}
      </div>
    </div>
  );
}

// ---- micro-primitives --------------------------------------------------------------------------------

function InsertStep({ onClick }: { onClick: () => void }) {
  return (
    <div className="flex justify-center py-0.5 pl-11">
      <button onClick={onClick} className="text-[10.5px] px-2 py-0.5 rounded-full opacity-50 hover:opacity-100 transition-opacity"
        style={{ color: 'var(--sem-accent)', border: '1px dashed color-mix(in srgb, var(--sem-accent) 50%, transparent)' }}>
        + add step
      </button>
    </div>
  );
}
function WordBudget({ text }: { text: string }) {
  const words = text.trim() ? text.trim().split(/\s+/).length : 0;
  if (words <= 150) return null;   // soft guidance, not a limit (context economy per the authoring rules)
  return <div className="text-[10px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>{words} words. Steps read best under ~150 (the agent re-reads this every run).</div>;
}
function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="flex flex-col gap-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
      {label}
      {children}
    </label>
  );
}
function TextInput({ value, onChange, placeholder, disabled, mono }: { value: string; onChange: (v: string) => void; placeholder?: string; disabled?: boolean; mono?: boolean }) {
  return (
    <input value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)} placeholder={placeholder} spellCheck={false}
      className={`text-[12px] px-2 py-1 rounded-md outline-none w-full disabled:opacity-100 ${mono ? 'tnum' : ''}`}
      style={{ background: disabled ? 'transparent' : 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: `1px solid ${disabled ? 'transparent' : 'var(--sem-border)'}` }} />
  );
}
function Select({ value, onChange, options, disabled, small, title }: { value: string; onChange: (v: string) => void; options: [string, string][]; disabled?: boolean; small?: boolean; title?: string }) {
  return (
    <select value={value} disabled={disabled} title={title} onChange={(e) => onChange(e.target.value)}
      className={`${small ? 'text-[11px]' : 'text-[12px]'} px-1.5 py-1 rounded-md outline-none disabled:opacity-100`}
      style={{ background: disabled ? 'transparent' : 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: `1px solid ${disabled ? 'transparent' : 'var(--sem-border)'}` }}>
      {options.map(([v, label]) => <option key={v} value={v}>{label}</option>)}
    </select>
  );
}
function IconBtn({ label, title, onClick, disabled, danger }: { label: string; title?: string; onClick: () => void; disabled?: boolean; danger?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className="text-[11px] w-6 h-6 rounded-md disabled:opacity-30"
      style={{ background: 'var(--sem-surface-2)', color: danger ? 'var(--sem-bad)' : 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {label}
    </button>
  );
}
function MiniBtn({ children, onClick, title }: { children: React.ReactNode; onClick: () => void; title?: string }) {
  return (
    <button onClick={onClick} title={title} className="text-[10.5px] px-2 py-0.5 rounded-md font-medium"
      style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-accent)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
