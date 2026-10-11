import { useEffect, useMemo, useRef, useState } from 'react';
import { rpc } from './bridge';
import { uiLabel } from './copy';
import { Button, OpChip, SectionTitle } from './workflow-ui';
import { checkTitle } from './copy';
import { checkWords, phaseWord, type Tag, type WfStep } from './workflows-model';
import { V3StepFields } from './workflow-v3fields';
import { isV3 } from './workflow-v3model';
import { countWords, groupOpCatalog, INSTRUCTIONS_WORD_BUDGET, newTempKey, restrictionFor, uniqueStepId, type OpCatalogEntry, type WorkflowEditModel, type WorkflowEditOperation, type WorkflowEditStep } from './workflowdraft.mjs';

type OpInfo = OpCatalogEntry;
// The catalog is the same for every step and every form, so one fetch per webview session serves them
// all; without this each selected step re-asked the engine for 300-odd rows.
let catalogCache: OpInfo[] | null = null;
type Apply = (operation: WorkflowEditOperation, structural?: boolean) => void | boolean;
const EVIDENCE_OP = 'export_workflow_evidence';
const actionPresentation = (op: OpInfo) => op.name === EVIDENCE_OP
  ? { label: 'Workflow report', description: 'After the workflow finishes, export its sealed HTML and JSON report.' }
  : { label: uiLabel(op.name), description: op.description ?? '' };

const textList = (value: string) => value.split(/[\n,]/).map((item) => item.trim()).filter(Boolean);
const shownList = (value?: string[] | null) => (value ?? []).join(', ');
const nextName = (basis: string, current: string[]) => {
  if (!current.includes(basis)) return basis;
  for (let i = 2; ; i++) if (!current.includes(`${basis}${i}`)) return `${basis}${i}`;
};
const tempKey = newTempKey;

function restriction(model: WorkflowEditModel, target: string, field: string) {
  return restrictionFor(model, target, field) ?? restrictionFor(model, target, null);
}

function EditField({ label, reason, children, wide }: { label: string; reason?: string | null; children: React.ReactNode; wide?: boolean }) {
  return <label className={`sem-wf-field ${wide ? 'sem-wf-field-wide' : ''}`}>
    <span>{label}</span>
    {children}
    {reason && <small className="sem-wf-restriction">{reason}</small>}
  </label>;
}

function Text({ value, onChange, disabled, placeholder, mono, multiline, inputRef, field }: {
  value?: string | number | null; onChange: (value: string) => void; disabled?: boolean; placeholder?: string; mono?: boolean; multiline?: boolean;
  inputRef?: React.RefObject<HTMLInputElement | null>; field?: string;
}) {
  const props = {
    value: value == null ? '' : String(value), disabled, placeholder, spellCheck: false,
    onChange: (event: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => onChange(event.target.value),
    className: `sem-wf-input ${mono ? 'tnum' : ''}`,
    'data-wf-field': field,
  };
  return multiline ? <textarea {...props} rows={5} /> : <input {...props} ref={inputRef} />;
}

function Select({ value, choices, onChange, disabled, empty = 'Not set', label }: {
  value?: string | null; choices: string[]; onChange: (value: string) => void; disabled?: boolean; empty?: string;
  /** The words a choice shows as, when its value is an engine id (lane l9-finish: ids never reach the screen). */
  label?: (choice: string) => string;
}) {
  return <select className="sem-wf-input" value={value ?? ''} disabled={disabled} onChange={(event) => onChange(event.target.value)}>
    <option value="">{empty}</option>
    {choices.map((choice) => <option key={choice} value={choice}>{label ? label(choice) : choice}</option>)}
  </select>;
}

function ToggleSection({ title, active, disabled, reason, onToggle, children }: {
  title: string; active: boolean; disabled?: boolean; reason?: string | null; onToggle: () => void; children: React.ReactNode;
}) {
  return <section className="sem-wf-option">
    <div className="sem-wf-option-head">
      <button type="button" role="switch" aria-checked={active} disabled={disabled} className="sem-wf-switch" onClick={onToggle}>
        <span />
      </button>
      <b>{title}</b>
      {reason && <small className="sem-wf-restriction">{reason}</small>}
    </div>
    {active && <div className="sem-wf-option-body">{children}</div>}
  </section>;
}

function setField(apply: Apply, target: string, field: string, expect: unknown, value: unknown) {
  apply({ op: 'set_field', target, field, expect: expect ?? null, value });
}

function StringListField({ model, target, field, label, value, apply, placeholder }: {
  model: WorkflowEditModel; target: string; field: string; label: string; value?: string[] | null; apply: Apply; placeholder?: string;
}) {
  const lock = restriction(model, target, field);
  return <EditField label={label} reason={lock?.message} wide>
    <Text value={shownList(value)} disabled={!!lock} placeholder={placeholder} onChange={(next) => setField(apply, target, field, value ?? [], textList(next))} />
  </EditField>;
}

function KeyValueEditor({ model, target, field, entries, apply }: {
  model: WorkflowEditModel; target: string; field: 'provenance' | 'call.with'; entries: { key: string; name: string; value: string }[]; apply: Apply;
}) {
  const lock = restriction(model, target, field);
  const [name, setName] = useState('');
  const [value, setValue] = useState('');
  return <div className="sem-wf-nested">
    {entries.map((entry) => {
      const itemLock = restrictionFor(model, entry.key, null);
      return <div className="sem-wf-map-row" key={entry.key}>
        <code>{entry.name}</code>
        <Text value={entry.value} disabled={!!lock || !!itemLock} onChange={(next) => apply({ op: 'set_map_entry', target, field, name: entry.name, expect: entry.value, value: next })} />
        <button type="button" disabled={!!lock || !!itemLock} onClick={() => apply({ op: 'remove_map_entry', target, field, name: entry.name, expect: entry.value })}>Remove</button>
        {itemLock && <small className="sem-wf-restriction">{itemLock.message}</small>}
      </div>;
    })}
    {!lock && <div className="sem-wf-map-row">
      <Text value={name} mono placeholder="name" onChange={setName} />
      <Text value={value} placeholder="value" onChange={setValue} />
      <button type="button" disabled={!name.trim() || entries.some((entry) => entry.name === name.trim())} onClick={() => {
        if (apply({ op: 'set_map_entry', target, field, name: name.trim(), expect: null, value }) === false) return;   // R-B: a refused edit keeps the typed name and value
        setName(''); setValue('');
      }}>Add</button>
    </div>}
    {lock && <small className="sem-wf-restriction">{lock.message}</small>}
  </div>;
}

function ItemActions({ index, items, item, apply }: { index: number; items: { key: string; fingerprint: string }[]; item: { key: string; fingerprint: string }; apply: Apply }) {
  const order = items.map((entry) => entry.key);
  const move = (direction: -1 | 1) => {
    if (direction < 0) apply({ op: 'move_item', target: item.key, after: index > 1 ? items[index - 2].key : null, expectOrder: order });
    else apply({ op: 'move_item', target: item.key, after: items[index + 1].key, expectOrder: order });
  };
  return <div className="sem-wf-item-actions">
    <button type="button" disabled={index === 0} onClick={() => move(-1)}>↑</button>
    <button type="button" disabled={index === items.length - 1} onClick={() => move(1)}>↓</button>
    <button type="button" onClick={() => apply({ op: 'remove_item', target: item.key, expectFingerprint: item.fingerprint })}>Remove</button>
  </div>;
}

// The form area when a draft has no steps at all. Before this existed, a new workflow opened on the
// settings form with no add control anywhere, so there was no way to author a first step (Kane, 1.2.0).
export function WorkflowEmptySteps({ disabled, note, onAdd }: { disabled?: boolean; note?: string | null; onAdd: () => void }) {
  return <div className="sem-wf-form-card" data-wf-empty-steps="true">
    <div className="sem-wf-form-title"><div>
      <SectionTitle>Steps</SectionTitle>
      <h3>No steps yet</h3>
      <p>A step is one instruction plus the questions and checks that go with it. Every workflow needs at least one.</p>
    </div></div>
    <Button primary disabled={disabled} onClick={onAdd}>Add the first step</Button>
    {note && <p className="sem-wf-id-help">{note}</p>}
  </div>;
}

/** The two tags as small icons (contract 21.1 row 61): a person for "You choose", a pen for "Your assistant writes". */
export function TagMark({ tag, label = false }: { tag: Tag; label?: boolean }) {
  const words = tag === 'you' ? 'You choose' : 'Your assistant writes';
  return <span className={`sem-wf-tag sem-wf-tag-${tag}`} title={words}>
    <svg viewBox="0 0 12 12" aria-hidden="true">{tag === 'you'
      ? <><circle cx="6" cy="4" r="2.2" fill="none" stroke="currentColor" strokeWidth="1.1" /><path d="M2 11c.6-2.3 2.1-3.4 4-3.4s3.4 1.1 4 3.4" fill="none" stroke="currentColor" strokeWidth="1.1" /></>
      : <path d="M2.2 9.8l.6-2.4 5.6-5.6 1.8 1.8-5.6 5.6zM7.6 2.6l1.8 1.8" fill="none" stroke="currentColor" strokeWidth="1.1" />}</svg>
    {label ? words : <span className="sem-sr">{words}</span>}
  </span>;
}

/** The Settings drawer's "About it": the title, what it is for and when to use it. Everything else about the file
 *  (its version, check strength, suggested actions, provenance and format) is in Source (contract 21.1 row 62). */
export function WorkflowSettingsFields({ model, apply }: { model: WorkflowEditModel; apply: Apply }) {
  const def = model.definition;
  const field = (name: string) => restriction(model, def.key, name);
  return <div className="sem-wf-field-grid sem-wf-field-grid-one" data-testid="wf-about">
    <EditField label="Title" reason={field('title')?.message} wide><Text value={def.title} disabled={!!field('title')} onChange={(value) => setField(apply, def.key, 'title', def.title, value)} /></EditField>
    <EditField label="What it is for" reason={field('description')?.message} wide><Text value={def.description} disabled={!!field('description')} multiline onChange={(value) => setField(apply, def.key, 'description', def.description, value)} /></EditField>
    <EditField label="When to use it" reason={field('whenToUse')?.message} wide><Text value={def.whenToUse} disabled={!!field('whenToUse')} multiline onChange={(value) => setField(apply, def.key, 'whenToUse', def.whenToUse ?? null, value || null)} /></EditField>
    <StringListField model={model} target={def.key} field="tags" label="Tags" value={def.tags} apply={apply} />
  </div>;
}

// The browsable picker, restored from 1.1.3: a question, then the shelves under it, then the actions
// with their plain titles. The flat datalist below stays as the quick path for anyone who already knows
// the name. Both write the same ops list through the same set_field operation on the draft.
function OpPicker({ catalog, chosen, onToggle, onClose }: { catalog: OpInfo[]; chosen: string[]; onToggle: (op: string) => void; onClose: () => void }) {
  const [filter, setFilter] = useState('');
  const [shut, setShut] = useState<Record<string, boolean>>({});
  const searching = filter.trim().length > 0;
  const grouped = useMemo(() => groupOpCatalog(catalog, { filter, present: actionPresentation }), [catalog, filter]);
  const isOpen = (key: string, fallback: boolean) => searching || (shut[key] == null ? fallback : !shut[key]);
  const flip = (key: string, fallback: boolean) => setShut((current) => ({ ...current, [key]: current[key] == null ? fallback : !current[key] }));
  return <section className="sem-wf-op-picker" data-wf-op-picker>
    <div className="sem-wf-op-picker-head">
      <div><b>What should this step be allowed to do?</b><p>Actions are grouped by the question they answer. Tick one to add it to this step.</p></div>
      <Button onClick={onClose}>Done</Button>
    </div>
    <input className="sem-wf-input" value={filter} aria-label="Filter actions" data-wf-op-filter placeholder="Filter actions" spellCheck={false}
      onChange={(event) => setFilter(event.target.value)} onKeyDown={(event) => { if (event.key === 'Escape') onClose(); }} />
    <div className="sem-wf-op-tree">
      {catalog.length === 0
        ? <p className="sem-wf-op-empty">The action list has not arrived yet.</p>
        : grouped.total === 0
          ? <p className="sem-wf-op-empty">No actions match that filter.</p>
          : grouped.questions.map((question, order) => {
            const open = isOpen(question.question, order === 0);
            return <div key={question.question} className="sem-wf-op-question" data-wf-op-question={question.question}>
              <button type="button" className="sem-wf-op-head" aria-expanded={open} disabled={searching} onClick={() => flip(question.question, order === 0)}>
                <span aria-hidden="true">{open ? '▾' : '▸'}</span><b>{question.question}</b><span className="tnum">{question.count}</span>
              </button>
              {open && question.shelves.map((shelf) => {
                const shelfKey = `${question.question} > ${shelf.shelf}`;
                const shelfOpen = isOpen(shelfKey, true);
                return <div key={shelfKey} className="sem-wf-op-shelf" data-wf-op-shelf={shelf.shelf}>
                  <button type="button" className="sem-wf-op-head sem-wf-op-shelf-head" aria-expanded={shelfOpen} disabled={searching} onClick={() => flip(shelfKey, true)}>
                    <span aria-hidden="true">{shelfOpen ? '▾' : '▸'}</span><span>{shelf.shelf}</span><span className="tnum">{shelf.ops.length}</span>
                  </button>
                  {shelfOpen && shelf.ops.map((op) => {
                    const ticked = chosen.includes(op.name);
                    return <button key={op.name} type="button" className="sem-wf-op-choice" data-wf-op-choice={op.name} aria-pressed={ticked} onClick={() => onToggle(op.name)}>
                      <span className="sem-wf-op-tick" aria-hidden="true">{ticked ? '✓' : ''}</span>
                      <span className="sem-wf-op-choice-text">
                        <b>{op.label}</b>
                        {op.label !== op.name && <code className="tnum">{op.name}</code>}
                        {op.description && <small>{op.description}</small>}
                      </span>
                    </button>;
                  })}
                </div>;
              })}
            </div>;
          })}
    </div>
  </section>;
}

/** The Model interview is hidden from the whole app (Kane, 8 Oct). Its engine part stays, so a saved workflow that already
 *  uses an interview action or the replay check still shows it: only the pickers stop offering them. */
const isInterviewOp = (name: string) => /interview/i.test(name);
const isInterviewCheck = (kind: string) => kind === 'interview_replay';

function OpEditor({ model, step, apply }: { model: WorkflowEditModel; step: WorkflowEditStep; apply: Apply }) {
  const [catalog, setCatalog] = useState<OpInfo[]>(catalogCache ?? []);
  const [picking, setPicking] = useState(false);
  const lock = restriction(model, step.key, 'ops');
  useEffect(() => {
    if (catalogCache) return;
    rpc<OpInfo[]>('getOpCatalog').then((list) => { catalogCache = list ?? []; setCatalog(catalogCache); }).catch(() => setCatalog([]));
  }, []);
  const toggle = (op: string) => setField(apply, step.key, 'ops', step.ops, step.ops.includes(op) ? step.ops.filter((item) => item !== op) : [...step.ops, op]);
  return <div className="sem-wf-op-editor">
    {/* OpChip is a display:flex box, so a bare inline wrapper gave it its own line and orphaned the × underneath it.
        The wrapper is the flex row that keeps a chip and its remove control together. */}
    <div className="flex gap-1 flex-wrap">{step.ops.map((op) => <span key={op} className="inline-flex items-center gap-1" data-wf-op-chip={op}><OpChip op={op} />{!lock && <button type="button" aria-label={`Remove ${op}`} onClick={() => setField(apply, step.key, 'ops', step.ops, step.ops.filter((item) => item !== op))}>×</button>}</span>)}</div>
    {!lock && <div className="flex gap-2 items-center"><Button onClick={() => setPicking(!picking)}>{picking ? 'Close the action list' : 'Choose actions'}</Button><small>Browse every action by the question it answers, or filter by name.</small></div>}
    {/* The Model interview is not offered in the picker any more; a step that already uses one still shows it. */}
    {!lock && picking && <OpPicker catalog={catalog.filter((o) => !isInterviewOp(o.name) || step.ops.includes(o.name))} chosen={step.ops} onToggle={toggle} onClose={() => setPicking(false)} />}
    {!lock && !step.ops.includes(EVIDENCE_OP) && <div><Button onClick={() => setField(apply, step.key, 'ops', step.ops, [...step.ops, EVIDENCE_OP])}>Add a workflow report</Button></div>}
    {step.ops.includes(EVIDENCE_OP) && <small>The report exports only after the run is completed or aborted, so an active run is never presented as final.</small>}
    {lock && <small className="sem-wf-restriction">{lock.message}</small>}
  </div>;
}

export function GateEditor({ model, step, apply, inputNames }: { model: WorkflowEditModel; step: WorkflowEditStep; apply: Apply; inputNames: string[] }) {
  const gate = step.gate ?? { strictness: null, inputs: [], verify: [] };
  const addInput = () => {
    const name = nextName('answer', gate.inputs.map((input) => input.name));
    apply({ op: 'insert_item', target: step.key, field: 'gate.inputs', after: gate.inputs.length ? gate.inputs[gate.inputs.length - 1].key : null, tempKey: tempKey(), value: {
      name, question: 'What should be recorded?', type: 'text', required: 'answer-or-decline', scope: 'iteration',
    } });
  };
  const addVerify = () => apply({ op: 'insert_item', target: step.key, field: 'gate.verify', after: gate.verify.length ? gate.verify[gate.verify.length - 1].key : null, tempKey: tempKey(), value: {
    kind: model.choices.verifyKinds.find((kind) => !isInterviewCheck(kind)) ?? 'dax_probe', pinnedShapes: [], openShapes: [],
  } });
  return <section className="sem-wf-gate">
    <div className="sem-wf-form-title"><div><SectionTitle>Questions and checks</SectionTitle><p>Ask for needed facts, then check the work before moving on.</p></div><div className="flex gap-2"><Button disabled={!!restriction(model, step.key, 'gate.inputs')} onClick={addInput}>Add question</Button><Button disabled={!!restriction(model, step.key, 'gate.verify')} onClick={addVerify}>Add check</Button></div></div>
    <EditField label="Checks for this step" reason={restriction(model, step.key, 'gate.strictness')?.message}>
      <Select value={gate.strictness} choices={model.choices.strictness} empty="Use workflow setting" disabled={!!restriction(model, step.key, 'gate.strictness')} onChange={(value) => setField(apply, step.key, 'gate.strictness', gate.strictness ?? null, value || null)} />
    </EditField>
    {gate.inputs.map((input, index) => {
      const itemLock = restrictionFor(model, input.key, null);
      const locked = (field: string) => restriction(model, input.key, field) ?? itemLock;
      return <div className="sem-wf-nested sem-wf-field-grid" key={input.key}>
        <EditField label="Answer name" reason={locked('name')?.message}><Text mono value={input.name} disabled={!!locked('name')} onChange={(value) => setField(apply, input.key, 'name', input.name, value)} /></EditField>
        <EditField label="Question" reason={locked('question')?.message} wide><Text value={input.question} disabled={!!locked('question')} onChange={(value) => setField(apply, input.key, 'question', input.question, value)} /></EditField>
        <EditField label="Type" reason={locked('type')?.message}><Select value={input.type} choices={model.choices.inputTypes} disabled={!!locked('type')} onChange={(value) => setField(apply, input.key, 'type', input.type, value)} /></EditField>
        <EditField label="Answer rule" reason={locked('required')?.message}><Select value={input.required} choices={model.choices.inputRequired} disabled={!!locked('required')} onChange={(value) => setField(apply, input.key, 'required', input.required, value)} /></EditField>
        <EditField label="Repeat scope" reason={locked('scope')?.message}><Select value={input.scope} choices={model.choices.inputScopes} disabled={!!locked('scope')} onChange={(value) => setField(apply, input.key, 'scope', input.scope ?? null, value || null)} /></EditField>
        <EditField label="DAX rule" reason={locked('daxPurity')?.message}><Select value={input.daxPurity} choices={model.choices.daxPurity} disabled={!!locked('daxPurity')} onChange={(value) => setField(apply, input.key, 'daxPurity', input.daxPurity ?? null, value || null)} /></EditField>
        {!itemLock && <ItemActions index={index} items={gate.inputs} item={input} apply={apply} />}
      </div>;
    })}
    {gate.verify.map((verify, index) => {
      const itemLock = restrictionFor(model, verify.key, null);
      const locked = (field: string) => restriction(model, verify.key, field) ?? itemLock;
      return <div className="sem-wf-nested sem-wf-field-grid" key={verify.key}>
        <EditField label="Check" reason={locked('kind')?.message}><Select value={verify.kind} choices={model.choices.verifyKinds.filter((kind) => !isInterviewCheck(kind) || kind === verify.kind)} label={checkTitle} disabled={!!locked('kind')} onChange={(value) => setField(apply, verify.key, 'kind', verify.kind, value)} /></EditField>
        <EditField label="Runs when" reason={locked('when')?.message}><Text value={verify.when} mono disabled={!!locked('when')} placeholder="Always" onChange={(value) => setField(apply, verify.key, 'when', verify.when ?? null, value || null)} /></EditField>
        <EditField label="Probe answer" reason={locked('probe')?.message}><select className="sem-wf-input" value={verify.probe ?? ''} disabled={!!locked('probe')} onChange={(event) => setField(apply, verify.key, 'probe', verify.probe ?? null, event.target.value || null)}><option value="">None</option>{inputNames.map((name) => <option key={name}>{name}</option>)}</select></EditField>
        <EditField label="Scope" reason={locked('scope')?.message}><Select value={verify.scope} choices={model.choices.verifyScopes} disabled={!!locked('scope')} onChange={(value) => setField(apply, verify.key, 'scope', verify.scope ?? null, value || null)} /></EditField>
        <EditField label="Change intent" reason={locked('intent')?.message}><Select value={verify.intent} choices={model.choices.verifyIntents} disabled={!!locked('intent')} onChange={(value) => setField(apply, verify.key, 'intent', verify.intent ?? null, value || null)} /></EditField>
        <StringListField model={model} target={verify.key} field="pinnedShapes" label="Pinned shapes" value={verify.pinnedShapes} apply={apply} placeholder="Enter shape IDs" />
        <StringListField model={model} target={verify.key} field="openShapes" label="Open shapes" value={verify.openShapes} apply={apply} />
        <EditField label="Open shapes from" reason={locked('openShapesFrom')?.message}><Text mono value={verify.openShapesFrom} disabled={!!locked('openShapesFrom')} onChange={(value) => setField(apply, verify.key, 'openShapesFrom', verify.openShapesFrom ?? null, value || null)} /></EditField>
        <EditField label="Open mismatch" reason={locked('openMismatch')?.message}><Select value={verify.openMismatch} choices={model.choices.openMismatch} disabled={!!locked('openMismatch')} onChange={(value) => setField(apply, verify.key, 'openMismatch', verify.openMismatch ?? null, value || null)} /></EditField>
        <EditField label="Named fields answer" reason={locked('anchors')?.message}><Text mono value={verify.anchors} disabled={!!locked('anchors')} onChange={(value) => setField(apply, verify.key, 'anchors', verify.anchors ?? null, value || null)} /></EditField>
        {!itemLock && <ItemActions index={index} items={gate.verify} item={verify} apply={apply} />}
      </div>;
    })}
  </section>;
}

// A soft budget, restored from 1.1.3: the count is always there, the nudge only once the step is long.
// It never blocks a save; the agent re-reads these instructions on every run, so long steps cost context.
function WordBudget({ text }: { text: string }) {
  const words = countWords(text);
  return <small className="sem-wf-word-budget" data-wf-word-budget={words}>
    {words === 1 ? '1 word' : `${words} words`}
    {words > INSTRUCTIONS_WORD_BUDGET && `. Steps read best under about ${INSTRUCTIONS_WORD_BUDGET} words. The agent reads this again on every run.`}
  </small>;
}


// The step form in the composer's order (composer contract 10.5, Plan-heart contract 21.1 rows 63 to 68): what the
// step does; what you choose; what your assistant writes, with its Brief beside the blank; the checks. The step's
// id, its raw conditions and the fields that are engine detail stay in Source. Everything else a step can do
// (its actions, "Runs only when", "Repeats for each item", "Calls another workflow", the questions and checks of a
// guided step) is under "More for this step", unchanged in what it writes.
export function WorkflowStepForm({ model, step, index, workflows, apply, onSelect, onFocusStep, focusKey = null, readOnlyNote, movement = 'vertical', v3 = null, onSource }: {
  model: WorkflowEditModel; step: WorkflowEditStep; index: number; workflows: { name: string; title: string }[]; apply: Apply; onSelect?: (index: number) => void;
  onFocusStep?: (key: string) => void; focusKey?: string | null; readOnlyNote?: string | null;
  movement?: 'vertical' | 'horizontal';
  /** The same step as the engine reads it (getWorkflow): who does it, its questions, blanks, Brief and checks. */
  v3?: WfStep | null;
  onSource?: () => void;
}) {
  const steps = model.definition.steps;
  const titleRef = useRef<HTMLInputElement | null>(null);
  const [more, setMore] = useState(false);
  useEffect(() => {
    if (!focusKey || focusKey !== step.key || !titleRef.current || titleRef.current.disabled) return;
    titleRef.current.focus();
    titleRef.current.select();
  }, [focusKey, step.key]);
  const field = (name: string) => restriction(model, step.key, name);
  const earlierInputNames = useMemo(() => steps.slice(0, index + 1).flatMap((item) => item.gate?.inputs?.map((input) => input.name) ?? []).filter(Boolean), [steps, index]);
  const structure = (operation: WorkflowEditOperation) => apply(operation, true);
  const move = (direction: -1 | 1) => {
    const after = direction < 0 ? (index > 1 ? steps[index - 2].key : null) : steps[index + 1].key;
    structure({ op: 'move_step', target: step.key, after, expectOrder: steps.map((item) => item.key) });
    onSelect?.(index + direction);
  };
  const addAfter = () => {
    const title = 'New step';
    const key = tempKey();
    structure({ op: 'add_step', after: step.key, tempKey: key, value: { id: uniqueStepId(title, model), title, instructions: '', ops: [], ...(editable ? { performer: 'assistant' } : {}) } });
    onSelect?.(index + 1); onFocusStep?.(key);
  };
  const copy = () => {
    const key = tempKey();
    structure({ op: 'copy_step', target: step.key, after: step.key, tempKey: key, newId: uniqueStepId(`${step.title} copy`, model) });
    onSelect?.(index + 1); onFocusStep?.(key);
  };
  const remove = () => { structure({ op: 'remove_step', target: step.key, expectFingerprint: step.fingerprint }); onSelect?.(Math.max(0, index - 1)); };
  const stepLock = restrictionFor(model, step.key, null);
  const loop = step.forEach;
  const loopSourceLock = field('forEach.source');
  const loopAsLock = field('forEach.as');
  const loopMaximumLock = field('forEach.maxIterations');
  const call = step.call;
  // A format-3 workflow is edited from the edit model itself (lane author-gaps, B7): who does the step, its action,
  // questions, parts, Brief and checks are all fields here, and change as the person types, before anything is saved.
  const editable = isV3(model.definition.schemaVersion);
  const questions = editable ? (step.questions ?? []).filter((q) => (q.by ?? 'you') === 'you') : (v3?.questions ?? []).filter((q) => (q.by ?? 'you') === 'you');
  const blanks = editable ? (step.blanks ?? []).filter((b) => (b.by ?? 'assistant') !== 'you') : (v3?.blanks ?? []).filter((b) => (b.by ?? 'assistant') !== 'you');
  const performer = editable ? step.performer : v3?.performer;
  const composed = editable || (!!v3 && (v3.performer === 'semanticus' || v3.performer === 'person' || !!v3.action || blanks.length > 0 || questions.length > 0));
  const checks = v3?.checks ?? [];
  const gateChecks = step.gate?.verify ?? [];
  return <div className="sem-wf-form-card sem-wf-sf" data-wf-step-form={step.id}>
    <div className="sem-wf-sf-h">
      <span className="sem-wf-note">Step {index + 1} of {steps.length}</span>
      <input ref={titleRef} className="sem-wf-sf-title" value={step.title} aria-label="Step title" data-wf-field="step-title"
        disabled={!!field('title')} onChange={(event) => setField(apply, step.key, 'title', step.title, event.target.value)} />
      {readOnlyNote && <small className="sem-wf-restriction">{readOnlyNote}</small>}
    </div>

    <div className="sem-wf-what">
      <span className="sem-wf-note">What it does</span>
      <Text value={step.instructions} multiline disabled={!!field('instructions')} onChange={(value) => setField(apply, step.key, 'instructions', step.instructions, value)} />
      <WordBudget text={step.instructions} />
    </div>

    {editable && <V3StepFields model={model} step={step} apply={apply} />}

    {!editable && (questions.length > 0 || (!composed && (step.gate?.inputs?.length ?? 0) > 0)) && <div className="sem-wf-grp" data-testid="wf-grp-you">
      <div className="sem-wf-grp-h"><TagMark tag="you" /><b>You choose</b><span className="sem-wf-note">asked each run</span></div>
      {questions.map((q) => <div key={q.name} className="sem-wf-q"><span>{q.question}</span>
        <span className="sem-wf-note">{q.values?.length ? q.values.join(', ') : q.type === 'number' ? 'a number' : 'words'}</span></div>)}
      {!composed && (step.gate?.inputs ?? []).map((q) => <div key={q.key} className="sem-wf-q"><span>{q.question}</span>
        <span className="sem-wf-note">{q.required === 'optional' ? 'optional' : 'needed'}</span></div>)}
    </div>}

    {!editable && (blanks.length > 0 || !composed || v3?.performer === 'assistant') && <div className="sem-wf-grp" data-testid="wf-grp-asst">
      <div className="sem-wf-grp-h"><TagMark tag="asst" /><b>Your assistant writes</b>
        <span className="sem-wf-note">{blanks.length ? (blanks.length === 1 ? '1 blank' : `${blanks.length} blanks`) : composed ? 'nothing on this step' : 'the whole step'}</span></div>
      {blanks.length > 0 ? blanks.map((b) => <div key={b.name} className="sem-wf-blank-row" data-testid={`wf-blank-${b.name}`}>
        <div><span className="sem-wf-note">{b.name[0].toUpperCase() + b.name.slice(1)}</span>
          <div className="sem-wf-area-wait">Written by your assistant when this runs, or type it yourself.</div></div>
        <div><span className="sem-wf-note">Brief for your assistant</span>
          <div className="sem-wf-area-brief" data-testid="wf-brief">{v3?.brief || 'No brief yet. Write one in Source.'}</div>
          <span className="sem-wf-note"><b>The brief is advice. The checks are law.</b> Anything that must be true goes in a check.</span></div>
      </div>)
        : !composed ? <span className="sem-wf-note">Your assistant does this step and submits it. {step.ops.length ? `It may use: ${step.ops.map((op) => uiLabel(op)).join(', ')}.` : ''}</span>
          : <span className="sem-wf-note">Your assistant does this step and submits it.</span>}
    </div>}
    {!editable && composed && v3?.performer === 'semanticus' && blanks.length === 0 && questions.length === 0 && <span className="sem-wf-note">Semanticus does this step with no words from anyone.</span>}

    {!editable && (checks.length > 0 || gateChecks.length > 0) && <div className="sem-wf-grp" data-testid="wf-grp-checks">
      <div className="sem-wf-grp-h"><b>Checks</b><span className="sem-wf-note">{checks.length + gateChecks.length === 1 ? '1 check' : `${checks.length + gateChecks.length} checks`}</span></div>
      {checks.map((c, i) => <div key={i} className="sem-wf-chk"><span aria-hidden="true">✓</span><span>{checkWords(c)}</span><span className="sem-wf-note">{phaseWord(c.phase)}</span></div>)}
      {gateChecks.map((c) => <div key={c.key} className="sem-wf-chk"><span aria-hidden="true">✓</span><span>{checkTitle(c.kind)}</span><span className="sem-wf-note">before the step counts as done</span></div>)}
    </div>}
    {!editable && composed && onSource && <span className="sem-wf-note">Questions, blanks, the Brief and checks change in <button type="button" className="sem-wf-quiet-link" onClick={onSource}>Source</button>.</span>}

    <button type="button" className="sem-wf-disclosure" aria-expanded={more} data-testid="wf-step-more" onClick={() => setMore(!more)}>{more ? '▾' : '▸'} More for this step</button>
    {more && <div className="sem-wf-more-step">
      <div className="sem-wf-form-actions"><Button disabled={!!stepLock || index === 0} onClick={() => move(-1)}>{movement === 'horizontal' ? 'Move left' : 'Move up'}</Button><Button disabled={!!stepLock || index === steps.length - 1} onClick={() => move(1)}>{movement === 'horizontal' ? 'Move right' : 'Move down'}</Button><Button disabled={!!stepLock} onClick={addAfter}>Add step after</Button><Button disabled={!!stepLock} onClick={copy}>Copy step</Button><Button disabled={!!stepLock || steps.length <= 1} onClick={remove} title={steps.length <= 1 ? 'A workflow needs at least one step.' : undefined}>Delete step</Button></div>
      <div className="sem-wf-field-wide"><SectionTitle>Actions it may use</SectionTitle><OpEditor model={model} step={step} apply={apply} /></div>
      <ToggleSection title="Runs only when" active={step.when != null} disabled={!!field('when')} reason={field('when')?.message}
        onToggle={() => setField(apply, step.key, 'when', step.when ?? null, step.when == null ? 'model.tableCount >= 0' : null)}>
        <small>This step runs only while its condition holds; otherwise it is skipped and the run moves on. The condition itself is in Source.</small>
        {onSource && <button type="button" className="sem-wf-quiet-link" onClick={onSource}>Open Source</button>}
      </ToggleSection>
      <ToggleSection title="Repeats for each item" active={!!loop} disabled={!!field('forEach')} reason={field('forEach')?.message}
        onToggle={() => setField(apply, step.key, 'forEach', loop ?? null, loop ? null : { source: { kind: 'literal', values: [] }, as: 'item', maxIterations: model.choices.defaultMaxIterations })}>
        {loop && <div className="sem-wf-field-grid">
          <EditField label="The items come from" reason={loopSourceLock?.message}><select className="sem-wf-input" value={loop.source.kind} disabled={!!loopSourceLock} onChange={(event) => setField(apply, step.key, 'forEach.source', loop.source, event.target.value === 'input' ? { kind: 'input', name: earlierInputNames[0] ?? '' } : { kind: 'literal', values: [] })}><option value="literal">A list you type</option><option value="input">An earlier answer</option></select></EditField>
          {loop.source.kind === 'input'
            ? <EditField label="Answer" reason={loopSourceLock?.message}><select className="sem-wf-input" value={loop.source.name ?? ''} disabled={!!loopSourceLock} onChange={(event) => setField(apply, step.key, 'forEach.source', loop.source, { kind: 'input', name: event.target.value })}><option value="">Choose an answer</option>{earlierInputNames.map((name) => <option key={name}>{name}</option>)}</select></EditField>
            : <EditField label="Items" reason={loopSourceLock?.message} wide><Text value={shownList(loop.source.values)} disabled={!!loopSourceLock} placeholder="Sales, Product" onChange={(value) => setField(apply, step.key, 'forEach.source', loop.source, { kind: 'literal', values: textList(value) })} /></EditField>}
          <EditField label="Each item is called" reason={loopAsLock?.message}><Text mono value={loop.as} disabled={!!loopAsLock} onChange={(value) => setField(apply, step.key, 'forEach.as', loop.as, value)} /></EditField>
          <EditField label="At most this many items" reason={loopMaximumLock?.message}><Text value={loop.maxIterations} disabled={!!loopMaximumLock} onChange={(value) => setField(apply, step.key, 'forEach.maxIterations', loop.maxIterations, Math.min(model.choices.maxIterations, Math.max(1, Number.parseInt(value, 10) || 1)))} /></EditField>
        </div>}
      </ToggleSection>
      <ToggleSection title="Calls another workflow" active={!!call} disabled={!!field('call')} reason={field('call')?.message}
        onToggle={() => setField(apply, step.key, 'call', call ?? null, call ? null : { workflow: workflows[0]?.name ?? '', with: [], returns: [] })}>
        {call && <div className="sem-wf-field-grid">
          <EditField label="Workflow" reason={field('call.workflow')?.message}><select className="sem-wf-input" value={call.workflow} disabled={!!field('call.workflow')} onChange={(event) => setField(apply, step.key, 'call.workflow', call.workflow, event.target.value)}><option value="">Choose a workflow</option>{workflows.map((item) => <option key={item.name} value={item.name}>{item.title || item.name}</option>)}</select></EditField>
          <StringListField model={model} target={step.key} field="call.returns" label="Answers it hands back" value={call.returns} apply={apply} />
          <div className="sem-wf-field-wide"><SectionTitle>What it is given</SectionTitle><KeyValueEditor model={model} target={step.key} field="call.with" entries={call.with} apply={apply} /></div>
        </div>}
      </ToggleSection>
      {/* A guided step's gate. In a format-3 workflow a step's questions and checks are the fields above, so a gate is
          shown only on a step that already carries one (lane author-gaps, B7: Add question and Add check wrote gate
          fields there before). */}
      {(!composed || (editable && performer !== 'semanticus' && !!step.gate)) && <GateEditor model={model} step={step} apply={apply} inputNames={earlierInputNames} />}
    </div>}
  </div>;
}
