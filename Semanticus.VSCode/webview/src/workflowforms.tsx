import { useEffect, useMemo, useRef, useState } from 'react';
import { rpc } from './bridge';
import { uiLabel } from './copy';
import { Button, OpChip, SectionTitle } from './workflows';
import { countWords, groupOpCatalog, INSTRUCTIONS_WORD_BUDGET, newTempKey, restrictionFor, uniqueStepId, type OpCatalogEntry, type WorkflowEditModel, type WorkflowEditOperation, type WorkflowEditStep } from './workflowdraft.mjs';

type OpInfo = OpCatalogEntry;
// The catalog is the same for every step and every form, so one fetch per webview session serves them
// all; without this each selected step re-asked the engine for 300-odd rows.
let catalogCache: OpInfo[] | null = null;
type Apply = (operation: WorkflowEditOperation, structural?: boolean) => void;
const EVIDENCE_OP = 'export_workflow_evidence';
const actionPresentation = (op: OpInfo) => op.name === EVIDENCE_OP
  ? { label: 'Evidence report', description: 'After the workflow finishes, export its sealed HTML and JSON evidence report.' }
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

function Select({ value, choices, onChange, disabled, empty = 'Not set' }: {
  value?: string | null; choices: string[]; onChange: (value: string) => void; disabled?: boolean; empty?: string;
}) {
  return <select className="sem-wf-input" value={value ?? ''} disabled={disabled} onChange={(event) => onChange(event.target.value)}>
    <option value="">{empty}</option>
    {choices.map((choice) => <option key={choice} value={choice}>{choice}</option>)}
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
        apply({ op: 'set_map_entry', target, field, name: name.trim(), expect: null, value }); setName(''); setValue('');
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

export function WorkflowSettingsForm({ model, apply, readOnlyNote }: { model: WorkflowEditModel; apply: Apply; readOnlyNote?: string | null }) {
  const def = model.definition;
  const field = (name: string) => restriction(model, def.key, name);
  const [advanced, setAdvanced] = useState(false);
  const addSlot = () => {
    const names = def.slots.map((slot) => slot.name);
    const name = nextName('input', names);
    apply({ op: 'insert_item', target: def.key, field: 'slots', after: def.slots.length ? def.slots[def.slots.length - 1].key : null, tempKey: tempKey(), value: {
      name, question: 'What should this workflow receive?', type: model.choices.slotTypes[0] ?? 'text', required: model.choices.slotRequired[0] ?? 'required', values: [],
    } });
  };
  return <div className="sem-wf-form-card">
    <div className="sem-wf-form-title"><div><SectionTitle>Saved definition</SectionTitle><h3>Workflow settings</h3></div><div className="sem-wf-form-actions">{readOnlyNote && <small className="sem-wf-restriction">{readOnlyNote}</small>}<code>{def.name}</code></div></div>
    <div className="sem-wf-field-grid">
      <EditField label="Title" reason={field('title')?.message}><Text value={def.title} disabled={!!field('title')} onChange={(value) => setField(apply, def.key, 'title', def.title, value)} /></EditField>
      <EditField label="Version" reason={field('version')?.message}><Text value={def.version} disabled={!!field('version')} onChange={(value) => setField(apply, def.key, 'version', def.version, Math.max(1, Number.parseInt(value, 10) || 1))} /></EditField>
      <EditField label="Description" reason={field('description')?.message} wide><Text value={def.description} disabled={!!field('description')} multiline onChange={(value) => setField(apply, def.key, 'description', def.description, value)} /></EditField>
      <EditField label="When to use it" reason={field('whenToUse')?.message} wide><Text value={def.whenToUse} disabled={!!field('whenToUse')} multiline onChange={(value) => setField(apply, def.key, 'whenToUse', def.whenToUse ?? null, value || null)} /></EditField>
      <EditField label="Checks" reason={field('strictness')?.message}><Select value={def.strictness} choices={model.choices.strictness} disabled={!!field('strictness')} empty="must pass (default)" onChange={(value) => setField(apply, def.key, 'strictness', def.strictness ?? null, value || null)} /></EditField>
      <div className="sem-wf-readonly"><span>Format</span><b>{model.format} · {def.kind}</b></div>
      <StringListField model={model} target={def.key} field="triggers" label="Suggested actions" value={def.triggers} apply={apply} />
      <StringListField model={model} target={def.key} field="tags" label="Tags" value={def.tags} apply={apply} />
    </div>
    <button type="button" className="sem-wf-disclosure" aria-expanded={advanced} onClick={() => setAdvanced(!advanced)}>{advanced ? '▾' : '▸'} Advanced fields</button>
    {advanced && <div className="sem-wf-advanced">
      <div><SectionTitle>Provenance</SectionTitle><KeyValueEditor model={model} target={def.key} field="provenance" entries={def.provenance} apply={apply} /></div>
      <div className="sem-wf-form-title"><SectionTitle>Template inputs</SectionTitle><Button disabled={!!restriction(model, def.key, 'slots')} onClick={addSlot}>Add input</Button></div>
      {def.slots.map((slot, index) => {
        const itemLock = restrictionFor(model, slot.key, null);
        const slotField = (name: string) => restriction(model, slot.key, name) ?? itemLock;
        return <div className="sem-wf-nested sem-wf-field-grid" key={slot.key}>
          <EditField label="Name" reason={slotField('name')?.message}><Text mono value={slot.name} disabled={!!slotField('name')} onChange={(value) => setField(apply, slot.key, 'name', slot.name, value)} /></EditField>
          <EditField label="Type" reason={slotField('type')?.message}><Select value={slot.type} choices={model.choices.slotTypes} disabled={!!slotField('type')} onChange={(value) => setField(apply, slot.key, 'type', slot.type, value)} /></EditField>
          <EditField label="Question" reason={slotField('question')?.message} wide><Text value={slot.question} disabled={!!slotField('question')} onChange={(value) => setField(apply, slot.key, 'question', slot.question, value)} /></EditField>
          <EditField label="Required" reason={slotField('required')?.message}><Select value={slot.required} choices={model.choices.slotRequired} disabled={!!slotField('required')} onChange={(value) => setField(apply, slot.key, 'required', slot.required, value)} /></EditField>
          <EditField label="Default" reason={slotField('default')?.message}><Text value={slot.default} disabled={!!slotField('default')} onChange={(value) => setField(apply, slot.key, 'default', slot.default ?? null, value || null)} /></EditField>
          <EditField label="Example" reason={slotField('example')?.message}><Text value={slot.example} disabled={!!slotField('example')} onChange={(value) => setField(apply, slot.key, 'example', slot.example ?? null, value || null)} /></EditField>
          <EditField label="Hint" reason={slotField('hint')?.message}><Text value={slot.hint} disabled={!!slotField('hint')} onChange={(value) => setField(apply, slot.key, 'hint', slot.hint ?? null, value || null)} /></EditField>
          <StringListField model={model} target={slot.key} field="values" label="Allowed values" value={slot.values} apply={apply} />
          {!itemLock && <ItemActions index={index} items={def.slots} item={slot} apply={apply} />}
        </div>;
      })}
    </div>}
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
      <div><b>What should this step be allowed to do?</b><p>Actions are grouped by the job they belong to. Tick one to add it to this step.</p></div>
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

function OpEditor({ model, step, apply }: { model: WorkflowEditModel; step: WorkflowEditStep; apply: Apply }) {
  const [catalog, setCatalog] = useState<OpInfo[]>(catalogCache ?? []);
  const [choice, setChoice] = useState('');
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
    {!lock && <div className="flex gap-2 items-center"><Button onClick={() => setPicking(!picking)}>{picking ? 'Close the action list' : 'Choose actions'}</Button><small>Browse every action by the job it does.</small></div>}
    {!lock && picking && <OpPicker catalog={catalog} chosen={step.ops} onToggle={toggle} onClose={() => setPicking(false)} />}
    {!lock && <div className="flex gap-2"><input className="sem-wf-input" list="sem-wf-op-list" value={choice} placeholder="Find an action" onChange={(event) => setChoice(event.target.value)} /><datalist id="sem-wf-op-list">{catalog.map((op) => <option key={op.name} value={op.name}>{op.description}</option>)}</datalist><Button disabled={!choice || step.ops.includes(choice)} onClick={() => { setField(apply, step.key, 'ops', step.ops, [...step.ops, choice]); setChoice(''); }}>Add</Button></div>}
    {!lock && !step.ops.includes(EVIDENCE_OP) && <div><Button onClick={() => setField(apply, step.key, 'ops', step.ops, [...step.ops, EVIDENCE_OP])}>Add evidence report</Button></div>}
    {step.ops.includes(EVIDENCE_OP) && <small>Evidence exports only after the run is completed or aborted, so an active run is never presented as final.</small>}
    {lock && <small className="sem-wf-restriction">{lock.message}</small>}
  </div>;
}

function GateEditor({ model, step, apply, inputNames }: { model: WorkflowEditModel; step: WorkflowEditStep; apply: Apply; inputNames: string[] }) {
  const gate = step.gate ?? { strictness: null, inputs: [], verify: [] };
  const addInput = () => {
    const name = nextName('answer', gate.inputs.map((input) => input.name));
    apply({ op: 'insert_item', target: step.key, field: 'gate.inputs', after: gate.inputs.length ? gate.inputs[gate.inputs.length - 1].key : null, tempKey: tempKey(), value: {
      name, question: 'What should be recorded?', type: 'text', required: 'answer-or-decline', scope: 'iteration',
    } });
  };
  const addVerify = () => apply({ op: 'insert_item', target: step.key, field: 'gate.verify', after: gate.verify.length ? gate.verify[gate.verify.length - 1].key : null, tempKey: tempKey(), value: {
    kind: model.choices.verifyKinds[0] ?? 'dax_probe', pinnedShapes: [], openShapes: [],
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
        <EditField label="Check" reason={locked('kind')?.message}><Select value={verify.kind} choices={model.choices.verifyKinds} disabled={!!locked('kind')} onChange={(value) => setField(apply, verify.key, 'kind', verify.kind, value)} /></EditField>
        <EditField label="Runs when" reason={locked('when')?.message}><Text value={verify.when} mono disabled={!!locked('when')} placeholder="Always" onChange={(value) => setField(apply, verify.key, 'when', verify.when ?? null, value || null)} /></EditField>
        <EditField label="Probe answer" reason={locked('probe')?.message}><select className="sem-wf-input" value={verify.probe ?? ''} disabled={!!locked('probe')} onChange={(event) => setField(apply, verify.key, 'probe', verify.probe ?? null, event.target.value || null)}><option value="">None</option>{inputNames.map((name) => <option key={name}>{name}</option>)}</select></EditField>
        <EditField label="Scope" reason={locked('scope')?.message}><Select value={verify.scope} choices={model.choices.verifyScopes} disabled={!!locked('scope')} onChange={(value) => setField(apply, verify.key, 'scope', verify.scope ?? null, value || null)} /></EditField>
        <EditField label="Change intent" reason={locked('intent')?.message}><Select value={verify.intent} choices={model.choices.verifyIntents} disabled={!!locked('intent')} onChange={(value) => setField(apply, verify.key, 'intent', verify.intent ?? null, value || null)} /></EditField>
        <StringListField model={model} target={verify.key} field="pinnedShapes" label="Pinned shapes" value={verify.pinnedShapes} apply={apply} placeholder="Enter shape IDs" />
        <StringListField model={model} target={verify.key} field="openShapes" label="Open shapes" value={verify.openShapes} apply={apply} />
        <EditField label="Open shapes from" reason={locked('openShapesFrom')?.message}><Text mono value={verify.openShapesFrom} disabled={!!locked('openShapesFrom')} onChange={(value) => setField(apply, verify.key, 'openShapesFrom', verify.openShapesFrom ?? null, value || null)} /></EditField>
        <EditField label="Open mismatch" reason={locked('openMismatch')?.message}><Select value={verify.openMismatch} choices={model.choices.openMismatch} disabled={!!locked('openMismatch')} onChange={(value) => setField(apply, verify.key, 'openMismatch', verify.openMismatch ?? null, value || null)} /></EditField>
        <EditField label="Anchors answer" reason={locked('anchors')?.message}><Text mono value={verify.anchors} disabled={!!locked('anchors')} onChange={(value) => setField(apply, verify.key, 'anchors', verify.anchors ?? null, value || null)} /></EditField>
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

export function WorkflowStepForm({ model, step, index, workflows, apply, onSelect, onFocusStep, focusKey = null, readOnlyNote, movement = 'vertical' }: {
  model: WorkflowEditModel; step: WorkflowEditStep; index: number; workflows: { name: string; title: string }[]; apply: Apply; onSelect?: (index: number) => void;
  onFocusStep?: (key: string) => void; focusKey?: string | null; readOnlyNote?: string | null;
  movement?: 'vertical' | 'horizontal';
}) {
  const steps = model.definition.steps;
  const titleRef = useRef<HTMLInputElement | null>(null);
  // A step added from anywhere (the rail, the canvas, the empty state, Add step after) lands here with the
  // caret already in its title, so the very next keystroke names it instead of being swallowed.
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
    structure({ op: 'add_step', after: step.key, tempKey: key, value: { id: uniqueStepId(title, model), title, instructions: '', ops: [] } });
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
  return <div className="sem-wf-form-card" data-wf-step-form={step.id}>
    <div className="sem-wf-form-title">
      <div><SectionTitle>Step {index + 1}</SectionTitle><h3>{step.title || 'Untitled'}</h3><small>id: {step.id}</small></div>
      <div className="sem-wf-form-actions"><Button disabled={!!stepLock || index === 0} onClick={() => move(-1)}>{movement === 'horizontal' ? 'Move left' : 'Move up'}</Button><Button disabled={!!stepLock || index === steps.length - 1} onClick={() => move(1)}>{movement === 'horizontal' ? 'Move right' : 'Move down'}</Button><Button disabled={!!stepLock} onClick={addAfter}>Add step after</Button><Button disabled={!!stepLock} onClick={copy}>Copy step</Button><Button disabled={!!stepLock || steps.length <= 1} onClick={remove} title={steps.length <= 1 ? 'A workflow needs at least one step.' : undefined}>Delete step</Button>{readOnlyNote && <small className="sem-wf-restriction">{readOnlyNote}</small>}</div>
    </div>
    <p className="sem-wf-id-help">Step ids stay fixed after saving. Rename this id in Source.</p>
    <div className="sem-wf-field-grid">
      <EditField label="Title" reason={field('title')?.message} wide><Text value={step.title} field="step-title" inputRef={titleRef} disabled={!!field('title')} onChange={(value) => setField(apply, step.key, 'title', step.title, value)} /></EditField>
      <EditField label="Instructions" reason={field('instructions')?.message} wide><Text value={step.instructions} multiline disabled={!!field('instructions')} onChange={(value) => setField(apply, step.key, 'instructions', step.instructions, value)} /><WordBudget text={step.instructions} /></EditField>
      <div className="sem-wf-field-wide"><SectionTitle>Actions</SectionTitle><OpEditor model={model} step={step} apply={apply} /></div>
    </div>
    <ToggleSection title="Runs only when" active={step.when != null} disabled={!!field('when')} reason={field('when')?.message}
      onToggle={() => setField(apply, step.key, 'when', step.when ?? null, step.when == null ? 'model.tableCount >= 0' : null)}>
      <Text mono value={step.when} placeholder="inputs.ready.answered" disabled={!!field('when')} onChange={(value) => setField(apply, step.key, 'when', step.when ?? '', value)} />
      <small>The step runs or is skipped according to this condition. Steps still run in order.</small>
    </ToggleSection>
    <ToggleSection title="Repeats for each item of" active={!!loop} disabled={!!field('forEach')} reason={field('forEach')?.message}
      onToggle={() => setField(apply, step.key, 'forEach', loop ?? null, loop ? null : { source: { kind: 'literal', values: [] }, as: 'item', maxIterations: model.choices.defaultMaxIterations })}>
      {loop && <div className="sem-wf-field-grid">
        <EditField label="Source" reason={loopSourceLock?.message}><select className="sem-wf-input" value={loop.source.kind} disabled={!!loopSourceLock} onChange={(event) => setField(apply, step.key, 'forEach.source', loop.source, event.target.value === 'input' ? { kind: 'input', name: earlierInputNames[0] ?? '' } : { kind: 'literal', values: [] })}><option value="literal">Literal list</option><option value="input">Earlier answer</option></select></EditField>
        {loop.source.kind === 'input'
          ? <EditField label="Answer" reason={loopSourceLock?.message}><select className="sem-wf-input" value={loop.source.name ?? ''} disabled={!!loopSourceLock} onChange={(event) => setField(apply, step.key, 'forEach.source', loop.source, { kind: 'input', name: event.target.value })}><option value="">Choose an answer</option>{earlierInputNames.map((name) => <option key={name}>{name}</option>)}</select></EditField>
          : <EditField label="Items" reason={loopSourceLock?.message} wide><Text value={shownList(loop.source.values)} disabled={!!loopSourceLock} placeholder="Sales, Product" onChange={(value) => setField(apply, step.key, 'forEach.source', loop.source, { kind: 'literal', values: textList(value) })} /></EditField>}
        <EditField label="As (name used by later steps)" reason={loopAsLock?.message}><Text mono value={loop.as} disabled={!!loopAsLock} onChange={(value) => setField(apply, step.key, 'forEach.as', loop.as, value)} /></EditField>
        <EditField label="Maximum items (optional)" reason={loopMaximumLock?.message}><Text value={loop.maxIterations} disabled={!!loopMaximumLock} onChange={(value) => setField(apply, step.key, 'forEach.maxIterations', loop.maxIterations, Math.min(model.choices.maxIterations, Math.max(1, Number.parseInt(value, 10) || 1)))} /></EditField>
      </div>}
    </ToggleSection>
    <ToggleSection title="Calls another workflow" active={!!call} disabled={!!field('call')} reason={field('call')?.message}
      onToggle={() => setField(apply, step.key, 'call', call ?? null, call ? null : { workflow: workflows[0]?.name ?? '', with: [], returns: [] })}>
      {call && <div className="sem-wf-field-grid">
        <EditField label="Workflow" reason={field('call.workflow')?.message}><select className="sem-wf-input" value={call.workflow} disabled={!!field('call.workflow')} onChange={(event) => setField(apply, step.key, 'call.workflow', call.workflow, event.target.value)}><option value="">Choose a workflow</option>{workflows.map((item) => <option key={item.name} value={item.name}>{item.title || item.name}</option>)}</select></EditField>
        <StringListField model={model} target={step.key} field="call.returns" label="Returns" value={call.returns} apply={apply} />
        <div className="sem-wf-field-wide"><SectionTitle>Named inputs</SectionTitle><KeyValueEditor model={model} target={step.key} field="call.with" entries={call.with} apply={apply} /></div>
      </div>}
    </ToggleSection>
    <GateEditor model={model} step={step} apply={apply} inputNames={earlierInputNames} />
  </div>;
}
