import { useState } from 'react';
import { uiLabel } from './copy';
import { Button } from './workflow-ui';
import { restrictionFor, type WorkflowEditAction, type WorkflowEditBlank, type WorkflowEditCheck, type WorkflowEditModel,
  type WorkflowEditOperation, type WorkflowEditQuestion, type WorkflowEditStep } from './workflowdraft.mjs';
import {
  blankValue, checkValue, nextName, phaseOfCheck, questionValue, RUNS_WHEN, WHO_WORDS, type Performer,
  checkName, argWords, argSource,
} from './workflow-v3model';

// ===================================================================================================
// Author's own fields for a format-3 workflow (composer contract 10.5, Plan-heart contract 21.5; lane author-gaps,
// B7). Before this, a v3 step's questions, blanks, Brief and checks, and the workflow's notes for the assistant and its
// inputs, could only be changed in Source. Each list here is written back whole with one set_field, in the shape the
// engine reads it (workflow-v3model.ts keeps that shape), so the file still parses after every change.
// ===================================================================================================

type Apply = (operation: WorkflowEditOperation, structural?: boolean) => void | boolean;

const setField = (apply: Apply, target: string, field: string, expect: unknown, value: unknown) =>
  apply({ op: 'set_field', target, field, expect: expect ?? null, value });

function lockOf(model: WorkflowEditModel, target: string, field: string) {
  return restrictionFor(model, target, field) ?? restrictionFor(model, target, null);
}

function Row({ label, children, wide }: { label: string; children: React.ReactNode; wide?: boolean }) {
  return <label className={`sem-wf-field ${wide ? 'sem-wf-field-wide' : ''}`}><span>{label}</span>{children}</label>;
}

function Plain({ value, onChange, placeholder, mono, disabled, testId, multiline }: {
  /** Answer false when the edit was refused: the box keeps the words it sent (R-B). */
  value?: string | null; onChange: (v: string) => void | boolean; placeholder?: string; mono?: boolean; disabled?: boolean; testId?: string; multiline?: boolean;
}) {
  // Written on blur, so each keystroke is not its own undo step and a half-typed name never reaches the engine.
  const [text, setText] = useState<string | null>(null);
  const shown = text ?? value ?? '';
  const commit = () => { if (text != null && text !== (value ?? '') && onChange(text) === false) return; setText(null); };
  const props = { className: `sem-wf-input ${mono ? 'tnum' : ''}`, value: shown, placeholder, disabled, spellCheck: false, 'data-testid': testId,
    onChange: (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => setText(e.target.value), onBlur: commit };
  return multiline ? <textarea {...props} rows={3} /> : <input {...props} onKeyDown={(e) => { if (e.key === 'Enter') commit(); }} />;
}

function Pick({ value, choices, onChange, disabled, labels, testId }: {
  value?: string | null; choices: string[]; onChange: (v: string) => void; disabled?: boolean; labels?: Record<string, string>; testId?: string;
}) {
  return <select className="sem-wf-input" value={value ?? ''} disabled={disabled} data-testid={testId} onChange={(e) => onChange(e.target.value)}>
    {choices.map((c) => <option key={c} value={c}>{labels?.[c] ?? c}</option>)}
  </select>;
}

const listText = (v?: string[] | null) => (v ?? []).join(', ');
const textList = (v: string) => v.split(/[\n,]/).map((x) => x.trim()).filter(Boolean);

// ---- questions: a step's "You choose", and the workflow's inputs -------------------------------------------------

export function QuestionList({ model, target, field, questions, apply, empty, testId }: {
  model: WorkflowEditModel; target: string; field: 'questions' | 'inputs'; questions: WorkflowEditQuestion[]; apply: Apply; empty: string; testId: string;
}) {
  const lock = lockOf(model, target, field);
  const types = model.choices.questionTypes ?? ['text', 'enum', 'number', 'objectRef'];
  const bys = model.choices.questionBy ?? ['you', 'assistant', 'semanticus'];
  const write = (next: WorkflowEditQuestion[]) => setField(apply, target, field, questions, next.map(questionValue));
  const change = (i: number, patch: Partial<WorkflowEditQuestion>) => write(questions.map((q, j) => j === i ? { ...q, ...patch } : q));
  return <div className="sem-wf-v3list" data-testid={testId}>
    {questions.length === 0 && <span className="sem-wf-note">{empty}</span>}
    {questions.map((q, i) => <div key={`${q.name}:${i}`} className="sem-wf-nested sem-wf-field-grid" data-testid={`${testId}-${q.name}`}>
      <Row label="Question" wide><Plain value={q.question} disabled={!!lock} testId="wf-q-text" onChange={(v) => change(i, { question: v })} /></Row>
      <Row label="Answer name"><Plain value={q.name} mono disabled={!!lock} onChange={(v) => change(i, { name: v })} /></Row>
      <Row label="Kind of answer"><Pick value={q.type ?? 'text'} choices={types} disabled={!!lock} labels={{ text: 'Words', enum: 'A choice', number: 'A number', objectRef: 'An object' }}
        onChange={(v) => change(i, { type: v, values: v === 'enum' ? q.values : [] })} /></Row>
      <Row label="Who answers"><Pick value={q.by ?? 'you'} choices={bys} disabled={!!lock} labels={{ you: 'You', assistant: 'Your assistant', semanticus: 'Semanticus, from the model' }}
        onChange={(v) => change(i, { by: v })} /></Row>
      {(q.type ?? 'text') === 'enum' && <Row label="The choices" wide><Plain value={listText(q.values)} disabled={!!lock} placeholder="Sales, Returns"
        onChange={(v) => change(i, { values: textList(v) })} /></Row>}
      <Row label="Needed"><Pick value={q.required ?? 'required'} choices={['required', 'optional']} disabled={!!lock} labels={{ required: 'Needed', optional: 'Can be left out' }}
        onChange={(v) => change(i, { required: v })} /></Row>
      {!lock && <div className="sem-wf-item-actions"><button type="button" onClick={() => write(questions.filter((_, j) => j !== i))}>Remove</button></div>}
    </div>)}
    {lock ? <small className="sem-wf-restriction">{lock.message}</small>
      : <Button onClick={() => write([...questions, { name: nextName('answer', questions.map((q) => q.name)), question: 'What should be asked?', type: 'text',
          required: 'required', by: 'you', values: [], from: null, jev: null, default: null }])}>Add question</Button>}
  </div>;
}

// ---- one v3 step ---------------------------------------------------------------------------------------------

export function V3StepFields({ model, step, apply }: { model: WorkflowEditModel; step: WorkflowEditStep; apply: Apply }) {
  const key = step.key;
  const who = (step.performer ?? 'assistant') as Performer;
  const lock = (field: string) => lockOf(model, key, field);
  const ops = model.choices.executableOps ?? [];
  const blanks = step.blanks ?? [];
  const checks = step.checks ?? [];
  const action = step.action ?? null;
  const kinds = model.choices.checkKinds ?? {};
  const allKinds = Object.entries(kinds).flatMap(([phase, list]) => list.map((k) => ({ phase, kind: k })));

  const setAction = (next: WorkflowEditAction | null) => setField(apply, key, 'action', action, next);
  const writeBlanks = (next: WorkflowEditBlank[]) => setField(apply, key, 'blanks', blanks, next.map(blankValue));
  const writeChecks = (next: WorkflowEditCheck[]) => setField(apply, key, 'checks', checks, next.map(checkValue));
  const argNames = (action?.args ?? []).map((a) => a.name);

  return <div className="sem-wf-v3" data-testid="wf-v3-fields">
    <div className="sem-wf-field-grid">
      <Row label="Who does it">
        <Pick value={who} testId="wf-who" disabled={!!lock('performer')} choices={['semanticus', 'assistant', 'person']} labels={WHO_WORDS}
          onChange={(v) => {
            if (v === 'semanticus' && !action) { setAction({ op: ops[0] ?? 'create_measure', args: [] }); return; }
            setField(apply, key, 'performer', step.performer ?? null, v);
          }} />
      </Row>
      {who === 'semanticus' && <Row label="What Semanticus runs">
        <Pick value={action?.op ?? ''} testId="wf-action-op" disabled={!!lock('action')} choices={ops} labels={Object.fromEntries(ops.map((o) => [o, uiLabel(o)]))}
          onChange={(op) => setAction({ op, args: action?.op === op ? action.args : [] })} />
      </Row>}
    </div>
    {who === 'person' && <span className="sem-wf-note">The run stops here and waits for you to press Continue. Ask your questions below.</span>}
    {who === 'assistant' && <span className="sem-wf-note">Your assistant changes the model here. This step is its own undo step.</span>}

    {who === 'semanticus' && action && <div className="sem-wf-v3list" data-testid="wf-action-args">
      <span className="sem-wf-note">What it is given. A value can come from one of your answers, or from a part your assistant writes.</span>
      {action.args.map((a, i) => {
        const chip = a.values ? null : argSource(a.value, { inputs: model.definition.inputs, questions: step.questions, blanks });
        const change = (patch: { value: string | null; values: string[] | null }) => setAction({ ...action, args: action.args.map((x, j) => j === i ? { ...x, ...patch } : x) });
        return <div key={`${a.name}:${i}`} className="sem-wf-map-row" data-testid={`wf-arg-${a.name}`}>
          <span className="sem-wf-arg-label" title={a.name}>{argWords(a.name)}</span>
          {chip
            ? <span className={`sem-wf-chip sem-wf-chip-${chip.kind}`} data-testid="wf-arg-chip">{chip.text}
                <button type="button" aria-label={`Type a value for ${argWords(a.name)} instead`} title="Type a value instead" onClick={() => change({ value: '', values: null })}>Change</button></span>
            : <Plain value={a.values ? listText(a.values) : a.value} onChange={(v) => change(a.values ? { value: null, values: textList(v) } : { value: v, values: null })} />}
          <button type="button" onClick={() => setAction({ ...action, args: action.args.filter((_, j) => j !== i) })}>Remove</button>
        </div>;
      })}
      <AddArgument onAdd={(name, value) => setAction({ ...action, args: [...action.args, { name, value, values: null }] })} />
    </div>}

    <div className="sem-wf-grp" data-testid="wf-v3-questions">
      <div className="sem-wf-grp-h"><b>You choose</b><span className="sem-wf-note">{who === 'person' ? 'asked when the run stops here' : 'asked before it runs'}</span></div>
      <QuestionList model={model} target={key} field="questions" questions={step.questions ?? []} apply={apply} testId="wf-step-questions"
        empty="No questions on this step." />
    </div>

    {who === 'semanticus' && <div className="sem-wf-grp" data-testid="wf-v3-blanks">
      <div className="sem-wf-grp-h"><b>Your assistant writes</b><span className="sem-wf-note">{blanks.length === 1 ? '1 part' : `${blanks.length} parts`}</span></div>
      {blanks.map((b, i) => <div key={`${b.name}:${i}`} className="sem-wf-nested sem-wf-field-grid" data-testid={`wf-blank-${b.name}`}>
        <Row label="Part name"><Plain value={b.name} mono onChange={(v) => writeBlanks(blanks.map((x, j) => j === i ? { ...x, name: v } : x))} /></Row>
        <Row label="What it is"><Pick value={b.kind ?? 'dax'} choices={model.choices.blankKinds ?? ['dax']} labels={{ dax: 'A formula', description: 'A description', m: 'A query', name: 'A name', choice: 'A choice' }}
          onChange={(v) => writeBlanks(blanks.map((x, j) => j === i ? { ...x, kind: v } : x))} /></Row>
        <Row label="It becomes"><Pick value={b.fills ?? ''} choices={argNames.includes(b.fills ?? '') ? argNames : [b.fills ?? '', ...argNames]}
          onChange={(v) => writeBlanks(blanks.map((x, j) => j === i ? { ...x, fills: v } : x))} /></Row>
        <Row label="Asked first"><Pick value={b.by ?? 'assistant'} choices={model.choices.blankBy ?? ['assistant', 'you']} labels={{ assistant: 'Your assistant', you: 'You' }}
          onChange={(v) => writeBlanks(blanks.map((x, j) => j === i ? { ...x, by: v } : x))} /></Row>
        <div className="sem-wf-item-actions"><button type="button" onClick={() => writeBlanks(blanks.filter((_, j) => j !== i))}>Remove</button></div>
      </div>)}
      <Button onClick={() => writeBlanks([...blanks, { name: nextName('text', blanks.map((b) => b.name)), kind: 'description', fills: argNames[0] ?? 'text',
        by: 'assistant', dependsOn: [], maxChars: null, choices: null, generator: null }])}>Add a part to write</Button>
    </div>}

    {who !== 'person' && <div className="sem-wf-grp" data-testid="wf-v3-brief">
      <div className="sem-wf-grp-h"><b>Brief for your assistant</b><span /></div>
      <Plain multiline value={step.brief} testId="wf-brief" disabled={!!lock('brief')} placeholder="Advice for whoever writes this step's parts."
        onChange={(v) => setField(apply, key, 'brief', step.brief ?? null, v || null)} />
      <span className="sem-wf-note"><b>The brief is advice. The checks are law.</b> Anything that must be true goes in a check.</span>
    </div>}

    {who !== 'person' && <div className="sem-wf-grp" data-testid="wf-v3-checks">
      <div className="sem-wf-grp-h"><b>Checks</b><span className="sem-wf-note">{checks.length === 1 ? '1 check' : `${checks.length} checks`}</span></div>
      {checks.map((c, i) => {
        const set = (patch: Partial<WorkflowEditCheck>) => writeChecks(checks.map((x, j) => j === i ? { ...x, ...patch } : x));
        const ranged = c.kind === 'result_range' || c.kind === 'row_count';
        return <div key={`${c.kind}:${i}`} className="sem-wf-nested sem-wf-field-grid" data-testid={`wf-check-${c.kind}`}>
          <Row label="Check"><select className="sem-wf-input" value={c.kind} onChange={(e) => set({ kind: e.target.value, phase: phaseOfCheck(kinds, e.target.value) })}>
            {Object.entries(kinds).map(([phase, list]) => <optgroup key={phase} label={RUNS_WHEN[phase] ?? phase}>
              {list.map((k) => <option key={k} value={k}>{checkName(k)}</option>)}</optgroup>)}
          </select></Row>
          <Row label="Runs when"><span className="sem-wf-note" data-testid="wf-check-when">{RUNS_WHEN[c.phase ?? phaseOfCheck(kinds, c.kind)] ?? ''}</span></Row>
          {ranged && <><Row label="At least"><Plain value={c.min} mono onChange={(v) => set({ min: v || null })} /></Row>
            <Row label="At most"><Plain value={c.max} mono onChange={(v) => set({ max: v || null })} /></Row></>}
          {c.kind === 'max_chars' && <Row label="At most, characters"><Plain value={c.max} mono onChange={(v) => set({ max: v || null })} /></Row>}
          {(c.kind === 'refs_within' || c.kind === 'dax_probe') && <Row label={c.kind === 'refs_within' ? 'Tables it may use' : 'Question with the number'} wide>
            <Plain value={listText(c.of)} onChange={(v) => set({ of: textList(v) })} /></Row>}
          <div className="sem-wf-item-actions"><button type="button" onClick={() => writeChecks(checks.filter((_, j) => j !== i))}>Remove</button></div>
        </div>;
      })}
      {allKinds.length > 0 && <Button onClick={() => {
        const first = allKinds.find((k) => k.kind === (who === 'semanticus' && blanks.some((b) => b.kind === 'dax') ? 'valid_dax' : 'result_range')) ?? allKinds[0];
        writeChecks([...checks, { kind: first.kind, phase: first.phase, target: null, min: first.kind === 'result_range' ? '0' : null, max: null, of: [], when: null }]);
      }}>Add check</Button>}
    </div>}
  </div>;
}

// R-B: Add clears its two boxes only when the edit was taken (a refused edit keeps the typed name and value).
function AddArgument({ onAdd }: { onAdd: (name: string, value: string) => void | boolean }) {
  const [name, setName] = useState('');
  const [value, setValue] = useState('');
  return <div className="sem-wf-map-row">
    <input className="sem-wf-input tnum" value={name} placeholder="Setting" spellCheck={false} onChange={(e) => setName(e.target.value)} />
    <input className="sem-wf-input" value={value} placeholder="Value" spellCheck={false} onChange={(e) => setValue(e.target.value)} />
    <button type="button" disabled={!name.trim()} onClick={() => { if (onAdd(name.trim(), value) === false) return; setName(''); setValue(''); }}>Add</button>
  </div>;
}

// ---- the Settings drawer's two v3 fields -------------------------------------------------------------------------

export function V3SettingsFields({ model, apply }: { model: WorkflowEditModel; apply: Apply }) {
  const def = model.definition;
  return <>
    <section className="sem-wf-side-sec" data-testid="wf-notes-edit">
      <b>Notes for your assistant</b>
      <Plain multiline value={def.assistantNotes} testId="wf-notes" placeholder="House style for every part your assistant writes."
        disabled={!!lockOf(model, def.key, 'assistantNotes')} onChange={(v) => setField(apply, def.key, 'assistantNotes', def.assistantNotes ?? null, v || null)} />
      <span className="sem-wf-note">Semanticus adds the model facts your assistant needs by itself. Do not paste table or column lists here.</span>
    </section>
    <section className="sem-wf-side-sec" data-testid="wf-inputs">
      <b>Inputs asked each run</b>
      <QuestionList model={model} target={def.key} field="inputs" questions={def.inputs ?? []} apply={apply} testId="wf-inputs-list"
        empty="None. Each step asks what it needs as it goes." />
    </section>
  </>;
}
