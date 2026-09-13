import { useEffect, useState, type CSSProperties, type ReactNode } from 'react';
import { rpc } from './bridge';

export type AuthorShape = 'measureValue' | 'measureReconcile' | 'interview';
export type RunScopeMode = 'everything' | 'selected' | 'section';

export interface TestDefinitionW {
  id: string; kind: string; title: string; targetTag?: string; targetIdentity?: string;
  targetRef?: string; paramsJson?: string; enabled: boolean; createdBy?: string; createdWhen?: string; budgetMs?: number;
}
export interface MeasureRowW { ref: string; name: string; table: string }
interface ReconcileParamsW {
  measureRef?: string; groupBy?: string[]; sql?: string; sqlGrandTotal?: string;
  toleranceAbsolute?: number; toleranceRelative?: number; blankPolicy?: string; maxRows?: number;
  server?: string; database?: string; authMode?: string; tenantId?: string;
}
interface MeasureValueParamsW {
  measureRef?: string; expectedValue?: string; filterColumn?: string; filterValue?: string;
  filterDax?: string; provenance?: string; toleranceAbsolute?: number; toleranceRelative?: number;
}
interface MappingReviewW {
  detectedServer?: string; detectedDatabase?: string; effectiveServer?: string; effectiveDatabase?: string;
  tested?: boolean; connected?: boolean; testError?: string; error?: string; note?: string;
}

const inp: CSSProperties = { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', borderColor: 'var(--sem-border)' };

export function scopeLabel(mode: RunScopeMode, selected: number, section: string) {
  if (mode === 'selected') return selected === 1 ? 'Will run: 1 saved test.' : `Will run: ${selected} saved tests.`;
  if (mode === 'section') return `Will run: ${section} only.`;
  return 'Will run: everything.';
}

export function AuthorDrawer({
  shape, editing, onClose, onSaved, onQuestionSaved,
}: {
  shape: AuthorShape; editing?: TestDefinitionW | null;
  onClose: () => void; onSaved: () => void; onQuestionSaved?: () => void;
}) {
  const [measures, setMeasures] = useState<MeasureRowW[]>([]);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [tryNote, setTryNote] = useState<string | null>(null);
  useEffect(() => { rpc<MeasureRowW[]>('listMeasures').then(setMeasures).catch(() => undefined); }, []);

  return (
    <aside className="fixed top-0 right-0 z-20 flex h-full w-[440px] max-w-[100vw] flex-col border-l" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-start justify-between gap-3 border-b px-4 py-3" style={{ borderColor: 'var(--sem-border)' }}>
        <div>
          <div className="text-[10px] font-semibold uppercase tracking-[0.07em]" style={{ color: 'var(--sem-muted)' }}>{editing ? 'Edit' : 'New test'}</div>
          <h2 className="m-0 mt-1 text-[14px] font-semibold">{shapeTitle(shape)}</h2>
        </div>
        <button type="button" onClick={onClose} className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Close</button>
      </div>
      <div className="min-h-0 flex-1 overflow-auto px-4 py-3">
        {err && <div className="mb-3 rounded-md border px-3 py-2 text-[12px]" style={{ color: 'var(--sem-bad)', borderColor: 'var(--sem-bad)' }}>{err}</div>}
        {tryNote && <div className="mb-3 rounded-md border px-3 py-2 text-[12px]" style={{ color: 'var(--sem-muted)', borderColor: 'var(--sem-border)' }}>{tryNote}</div>}
        {shape === 'measureValue' && <ExpectedTotalForm measures={measures} editing={editing} busy={busy} setBusy={setBusy} setErr={setErr} setTryNote={setTryNote} onSaved={onSaved} onClose={onClose} />}
        {shape === 'measureReconcile' && <SqlForm measures={measures} editing={editing} busy={busy} setBusy={setBusy} setErr={setErr} setTryNote={setTryNote} onSaved={onSaved} onClose={onClose} />}
        {shape === 'interview' && <QuestionForm busy={busy} setBusy={setBusy} setErr={setErr} setTryNote={setTryNote} onSaved={() => { onQuestionSaved?.(); onSaved(); }} onClose={onClose} />}
      </div>
    </aside>
  );
}

export function NewTestMenu({ onPick }: { onPick: (shape: AuthorShape) => void }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="relative">
      <button type="button" className="min-h-7 rounded-md border px-3 py-1 text-[12px] font-semibold" onClick={() => setOpen((v) => !v)}
        style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>New test</button>
      {open && (
        <div className="absolute right-0 z-10 mt-1 w-[280px] rounded-md border p-1.5" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
          <Pick label="Expected total" hint="Check that a measure gives a number you trust." onClick={() => { onPick('measureValue'); setOpen(false); }} />
          <Pick label="Match source SQL" hint="Compare a measure with SQL you accept as the truth." onClick={() => { onPick('measureReconcile'); setOpen(false); }} />
          <Pick label="Interview question" hint="Save a question your users ask and the answer to expect." onClick={() => { onPick('interview'); setOpen(false); }} />
        </div>
      )}
    </div>
  );
}

export function RunScopePopover({
  mode, selectedCount, section, onMode, onRun, persistOff,
}: {
  mode: RunScopeMode; selectedCount: number; section: string;
  onMode: (mode: RunScopeMode) => void; onRun: (persist: boolean) => void; persistOff: boolean;
}) {
  const [open, setOpen] = useState(false);
  return (
    <div className="relative inline-flex">
      <button type="button" className="min-h-7 rounded-l-md border px-3 py-1 text-[12px] font-semibold" onClick={() => onRun(false)}
        style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Run tests</button>
      <button type="button" aria-label="What to run" className="min-h-7 rounded-r-md border border-l-0 px-2 text-[12px]" onClick={() => setOpen((v) => !v)}
        style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>▾</button>
      {open && (
        <div className="absolute right-0 z-10 mt-9 w-[260px] rounded-md border p-2" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
          <div className="mb-1.5 text-[10px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>What to run</div>
          <ScopeOpt current={mode} value="everything" label="Everything" hint="Saved tests, relationships, security, interview questions." onPick={onMode} />
          <ScopeOpt current={mode} value="selected" label="Only what I ticked" hint={`${selectedCount} saved test${selectedCount === 1 ? '' : 's'}.`} onPick={onMode} />
          <ScopeOpt current={mode} value="section" label="Only this section" hint={`The open section: ${section}.`} onPick={onMode} />
          <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>{scopeLabel(mode, selectedCount, section)}</div>
        </div>
      )}
      <span className="sr-only">{persistOff ? 'Only a full run can be recorded.' : ''}</span>
    </div>
  );
}

function ScopeOpt({ current, value, label, hint, onPick }: { current: RunScopeMode; value: RunScopeMode; label: string; hint: string; onPick: (m: RunScopeMode) => void }) {
  return (
    <button type="button" onClick={() => onPick(value)} className="mb-1 w-full rounded-md border px-2 py-1.5 text-left"
      style={{ background: current === value ? 'var(--sem-accent-soft)' : 'var(--sem-surface-2)', borderColor: current === value ? 'var(--sem-accent)' : 'var(--sem-border)' }}>
      <div className="text-[12px] font-semibold">{label}</div>
      <div className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>{hint}</div>
    </button>
  );
}

function Pick({ label, hint, onClick }: { label: string; hint: string; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick} className="mb-1 w-full rounded-md px-2 py-1.5 text-left hover:bg-[var(--sem-surface-2)]">
      <div className="text-[12px] font-semibold">{label}</div>
      <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{hint}</div>
    </button>
  );
}

function shapeTitle(shape: AuthorShape) {
  if (shape === 'measureValue') return 'Expected total';
  if (shape === 'measureReconcile') return 'Match source SQL';
  return 'Interview question';
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="mb-3 block">
      <div className="mb-1 text-[11px] font-semibold">{label}</div>
      {children}
      {hint && <div className="mt-1 text-[10px]" style={{ color: 'var(--sem-muted)' }}>{hint}</div>}
    </label>
  );
}

function MeasureSelect({ measures, value, onChange }: { measures: MeasureRowW[]; value: string; onChange: (v: string) => void }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} className="w-full rounded-md border px-2 py-1.5 text-[12px]" style={inp}>
      <option value="">Pick a measure</option>
      {measures.map((m) => <option key={m.ref} value={m.ref}>{m.name}</option>)}
    </select>
  );
}

function Footer({ busy, onTry, onSave, tryLabel = 'Try it now' }: { busy: boolean; onTry: () => void; onSave: () => void; tryLabel?: string }) {
  return (
    <div className="mt-4 flex flex-wrap items-center gap-2">
      <button type="button" disabled={busy} onClick={onTry} className="min-h-7 rounded-md border px-3 py-1 text-[12px]" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>{tryLabel}</button>
      <button type="button" disabled={busy} onClick={onSave} className="min-h-7 rounded-md border px-3 py-1 text-[12px] font-semibold" style={{ background: 'var(--sem-accent)', borderColor: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Save <span style={{ color: 'var(--sem-on-accent)', opacity: 0.8 }}>Pro</span></button>
    </div>
  );
}

function ExpectedTotalForm({ measures, editing, busy, setBusy, setErr, setTryNote, onSaved, onClose }: {
  measures: MeasureRowW[]; editing?: TestDefinitionW | null; busy: boolean;
  setBusy: (v: boolean) => void; setErr: (v: string | null) => void; setTryNote: (v: string | null) => void;
  onSaved: () => void; onClose: () => void;
}) {
  const initial = parseValue(editing);
  const [measureRef, setMeasureRef] = useState(editing?.targetRef || initial.measureRef || '');
  const [filterColumn, setFilterColumn] = useState(initial.filterColumn || '');
  const [filterValue, setFilterValue] = useState(initial.filterValue || '');
  const [expected, setExpected] = useState(initial.expectedValue || '');
  const [provenance, setProvenance] = useState(initial.provenance || '');
  const [budget, setBudget] = useState(editing?.budgetMs?.toString() || '');
  const def = (): TestDefinitionW => ({
    id: editing?.id, kind: 'measureValue', title: titleFor(measureRef, expected),
    targetRef: measureRef, enabled: true,
    budgetMs: budget.trim() ? Number(budget) : undefined,
    paramsJson: JSON.stringify({ measureRef, expectedValue: expected.trim(), filterColumn: filterColumn.trim() || undefined, filterValue: filterValue.trim() || undefined, provenance: provenance.trim() || undefined } satisfies MeasureValueParamsW),
  } as TestDefinitionW);

  return (
    <>
      <Field label="Measure"><MeasureSelect measures={measures} value={measureRef} onChange={setMeasureRef} /></Field>
      <Field label="Only where (optional)" hint="Example: Year is 2024.">
        <div className="flex gap-2">
          <input value={filterColumn} onChange={(e) => setFilterColumn(e.target.value)} placeholder="Column" className="min-h-7 flex-1 rounded-md border px-2 text-[12px]" style={inp} />
          <input value={filterValue} onChange={(e) => setFilterValue(e.target.value)} placeholder="Value" className="min-h-7 flex-1 rounded-md border px-2 text-[12px]" style={inp} />
        </div>
      </Field>
      <Field label="The number you trust" hint="Type BLANK if the right answer is no value.">
        <input value={expected} onChange={(e) => setExpected(e.target.value)} className="min-h-7 w-full rounded-md border px-2 text-[12px]" style={inp} />
      </Field>
      <Field label="Where this number came from (optional)">
        <input value={provenance} onChange={(e) => setProvenance(e.target.value)} className="min-h-7 w-full rounded-md border px-2 text-[12px]" style={inp} />
      </Field>
      <Field label="Timing budget (optional)" hint="Milliseconds. Blank means the test is never timed.">
        <input value={budget} onChange={(e) => setBudget(e.target.value)} className="min-h-7 w-full rounded-md border px-2 text-[12px]" style={inp} />
      </Field>
      <Footer busy={busy} onTry={() => void tryDef(def(), setBusy, setErr, setTryNote)} onSave={() => void saveDef(def(), setBusy, setErr, onSaved, onClose)} />
    </>
  );
}

function SqlForm({ measures, editing, busy, setBusy, setErr, setTryNote, onSaved, onClose }: {
  measures: MeasureRowW[]; editing?: TestDefinitionW | null; busy: boolean;
  setBusy: (v: boolean) => void; setErr: (v: string | null) => void; setTryNote: (v: string | null) => void;
  onSaved: () => void; onClose: () => void;
}) {
  const initial = parseReconcile(editing);
  const [measureRef, setMeasureRef] = useState(editing?.targetRef || initial.measureRef || '');
  const [groupBy, setGroupBy] = useState((initial.groupBy || []).join(', '));
  const [sql, setSql] = useState(initial.sql || '');
  const [sqlGrandTotal, setSqlGrandTotal] = useState(initial.sqlGrandTotal || '');
  const [blankPolicy, setBlankPolicy] = useState(initial.blankPolicy || '');
  const [abs, setAbs] = useState(String(initial.toleranceAbsolute ?? 0));
  const [rel, setRel] = useState(String(initial.toleranceRelative ?? 0));
  const [showMap, setShowMap] = useState(false);
  const [server, setServer] = useState(initial.server || '');
  const [database, setDatabase] = useState(initial.database || '');
  const [review, setReview] = useState<MappingReviewW | null>(null);

  const params = (): ReconcileParamsW => ({
    measureRef, sql: sql.trim(), sqlGrandTotal: sqlGrandTotal.trim() || undefined,
    groupBy: groupBy.split(',').map((s) => s.trim()).filter(Boolean),
    blankPolicy, toleranceAbsolute: Number(abs) || 0, toleranceRelative: Number(rel) || 0,
    server: server.trim() || undefined, database: database.trim() || undefined,
  });
  const def = (): TestDefinitionW => ({
    id: editing?.id, kind: 'measureReconcile', title: titleFor(measureRef, 'SQL'),
    targetRef: measureRef, enabled: true, paramsJson: JSON.stringify(params()),
  } as TestDefinitionW);

  return (
    <>
      <Field label="Measure"><MeasureSelect measures={measures} value={measureRef} onChange={setMeasureRef} /></Field>
      <Field label="Group by" hint="Choose fields such as customer region and product category to compare different cases. With no fields, only the overall total is checked, which can hide missing or unmatched rows.">
        <input value={groupBy} onChange={(e) => setGroupBy(e.target.value)} placeholder="Columns to compare, separated by commas" className="min-h-7 w-full rounded-md border px-2 text-[12px]" style={inp} />
      </Field>
      <Field label="Source SQL you accept as the truth">
        <textarea value={sql} onChange={(e) => setSql(e.target.value)} rows={5} spellCheck={false} className="w-full rounded-md border px-2 py-1 font-mono text-[11px]" style={inp} />
      </Field>
      <Field label="Grand total SQL (optional)">
        <textarea value={sqlGrandTotal} onChange={(e) => setSqlGrandTotal(e.target.value)} rows={2} spellCheck={false} className="w-full rounded-md border px-2 py-1 font-mono text-[11px]" style={inp} />
      </Field>
      <div className="mb-3">
        <div className="mb-1 text-[11px] font-semibold">A blank in the model means</div>
        {([['zero', 'Zero'], ['null', 'No value'], ['distinct', 'Its own group']] as const).map(([v, label]) => (
          <label key={v} className="mr-3 text-[12px]"><input type="radio" name="blank" checked={blankPolicy === v} onChange={() => setBlankPolicy(v)} /> {label}</label>
        ))}
        {!blankPolicy && <div className="mt-1 text-[11px]" style={{ color: 'var(--sem-bad)' }}>Pick one before you save.</div>}
      </div>
      <Field label="Tolerance">
        <div className="flex gap-2">
          <input value={abs} onChange={(e) => setAbs(e.target.value)} placeholder="Absolute" className="min-h-7 flex-1 rounded-md border px-2 text-[12px]" style={inp} />
          <input value={rel} onChange={(e) => setRel(e.target.value)} placeholder="Relative" className="min-h-7 flex-1 rounded-md border px-2 text-[12px]" style={inp} />
        </div>
      </Field>
      <div className="mb-3 rounded-md border p-2.5" style={{ borderColor: 'var(--sem-border)' }}>
        <div className="flex items-center justify-between">
          <strong className="text-[12px]">Where the SQL runs</strong>
          <button type="button" className="text-[11px]" style={{ color: 'var(--sem-accent)' }} onClick={() => {
            setShowMap((v) => !v);
            if (!review) rpc<MappingReviewW>('reviewReconcileMapping', { measureRef, server: server || null, database: database || null, testConnection: false })
              .then(setReview).catch((e: unknown) => setErr(e instanceof Error ? e.message : String(e)));
          }}>{showMap ? 'Hide' : 'Change'}</button>
        </div>
        <div className="mt-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>{review?.effectiveServer || review?.detectedServer || 'Detected from the measure, or not found yet.'}</div>
        {showMap && (
          <div className="mt-2">
            <input value={server} onChange={(e) => setServer(e.target.value)} placeholder="Endpoint" className="mb-1 min-h-7 w-full rounded-md border px-2 text-[12px]" style={inp} />
            <input value={database} onChange={(e) => setDatabase(e.target.value)} placeholder="Database" className="min-h-7 w-full rounded-md border px-2 text-[12px]" style={inp} />
            <button type="button" className="mt-2 text-[11px]" style={{ color: 'var(--sem-accent)' }} onClick={() => { setServer(''); setDatabase(''); }}>Use detected again</button>
          </div>
        )}
      </div>
      <Footer busy={busy} onTry={() => void tryDef(def(), setBusy, setErr, setTryNote)} onSave={() => {
        if (!blankPolicy) { setErr('Pick what a blank in the model means.'); return; }
        void saveDef(def(), setBusy, setErr, onSaved, onClose);
      }} />
    </>
  );
}

function QuestionForm({ busy, setBusy, setErr, setTryNote, onSaved, onClose }: {
  busy: boolean; setBusy: (v: boolean) => void; setErr: (v: string | null) => void; setTryNote: (v: string | null) => void;
  onSaved: () => void; onClose: () => void;
}) {
  const [question, setQuestion] = useState('');
  const [tier, setTier] = useState<'value' | 'paraphrase' | 'refusal'>('value');
  const [expected, setExpected] = useState('');
  const [query, setQuery] = useState('');
  const [scalar, setScalar] = useState('');
  const [paraphrase, setParaphrase] = useState('');
  const save = async () => {
    if (!question.trim()) { setErr('Type the question a user would say.'); return; }
    setBusy(true); setErr(null);
    try {
      await rpc('addInterviewQuestion', question.trim(), tier, query.trim() || null, scalar.trim() || null, paraphrase.trim() || null,
        [], [], expected.trim() || null, null, tier === 'refusal', null, 'user', 'project', 'human');
      onSaved(); onClose();
    } catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };
  const tryNow = async () => {
    setBusy(true); setErr(null); setTryNote(null);
    try {
      const r = await rpc<{ outcome?: string; detail?: string }>('runInterview', null, JSON.stringify({
        question, tier, query: query.trim() || undefined, scalarExpr: scalar.trim() || undefined,
        paraphraseExpr: paraphrase.trim() || undefined, expectedValue: expected.trim() || undefined, expectRefusal: tier === 'refusal',
      }));
      setTryNote(r.detail || r.outcome || 'Tried once. Nothing was saved.');
    } catch (e) {
      const msg = e instanceof Error ? e.message : String(e);
      setTryNote(msg.toLowerCase().includes('live') || msg.toLowerCase().includes('connect')
        ? "Can't try this now: no live model to ask." : msg);
    } finally { setBusy(false); }
  };
  return (
    <>
      <Field label="Question"><textarea value={question} onChange={(e) => setQuestion(e.target.value)} rows={2} className="w-full rounded-md border px-2 py-1 text-[12px]" style={inp} /></Field>
      <div className="mb-3 text-[11px] font-semibold">What to check</div>
      <label className="mb-1 block text-[12px]"><input type="radio" checked={tier === 'value'} onChange={() => setTier('value')} /> A number I trust</label>
      <label className="mb-1 block text-[12px]"><input type="radio" checked={tier === 'paraphrase'} onChange={() => setTier('paraphrase')} /> Two ways to ask must agree</label>
      <label className="mb-3 block text-[12px]"><input type="radio" checked={tier === 'refusal'} onChange={() => setTier('refusal')} /> The model should not answer this</label>
      {tier === 'value' && (
        <>
          <Field label="The answer to expect"><input value={expected} onChange={(e) => setExpected(e.target.value)} className="min-h-7 w-full rounded-md border px-2 text-[12px]" style={inp} /></Field>
          <Field label="Edit the query (optional)" hint="Leave blank and we will write one from the measure when we can.">
            <textarea value={query} onChange={(e) => setQuery(e.target.value)} rows={3} spellCheck={false} className="w-full rounded-md border px-2 py-1 font-mono text-[11px]" style={inp} />
          </Field>
        </>
      )}
      {tier === 'paraphrase' && (
        <>
          <Field label="First way" hint="This option compares two DAX queries. Use a known-answer test if you prefer to enter the expected number directly.">
            <textarea value={scalar} onChange={(e) => setScalar(e.target.value)} rows={2} spellCheck={false} className="w-full rounded-md border px-2 py-1 font-mono text-[11px]" style={inp} />
          </Field>
          <Field label="Second way">
            <textarea value={paraphrase} onChange={(e) => setParaphrase(e.target.value)} rows={2} spellCheck={false} className="w-full rounded-md border px-2 py-1 font-mono text-[11px]" style={inp} />
          </Field>
        </>
      )}
      <Footer busy={busy} onTry={() => void tryNow()} onSave={() => void save()} />
    </>
  );
}

async function tryDef(def: TestDefinitionW, setBusy: (v: boolean) => void, setErr: (v: string | null) => void, setTryNote: (v: string | null) => void) {
  setBusy(true); setErr(null); setTryNote(null);
  try {
    const r = await rpc<{ verdict?: string; message?: string }>('tryTest', def);
    setTryNote(r.message || r.verdict || 'Tried once. Nothing was saved.');
  } catch (e) {
    const msg = e instanceof Error ? e.message : String(e);
    setTryNote(msg.toLowerCase().includes('live') || msg.toLowerCase().includes('connect')
      ? "Can't try this now: no live model to ask." : msg);
  } finally { setBusy(false); }
}

async function saveDef(def: TestDefinitionW, setBusy: (v: boolean) => void, setErr: (v: string | null) => void, onSaved: () => void, onClose: () => void) {
  if (!def.targetRef) { setErr('Pick a measure.'); return; }
  setBusy(true); setErr(null);
  try {
    await rpc('saveTest', def);
    onSaved(); onClose();
  } catch (e) { setErr(e instanceof Error ? e.message : String(e)); }
  finally { setBusy(false); }
}

function parseValue(def?: TestDefinitionW | null): MeasureValueParamsW {
  if (!def?.paramsJson) return {};
  try { return JSON.parse(def.paramsJson) as MeasureValueParamsW; } catch { return {}; }
}
function parseReconcile(def?: TestDefinitionW | null): ReconcileParamsW {
  if (!def?.paramsJson) return {};
  try { return JSON.parse(def.paramsJson) as ReconcileParamsW; } catch { return {}; }
}
function titleFor(measureRef: string, expected: string) {
  const name = (measureRef || '').split('/').pop()?.replace(/^measure:/, '') || 'Measure';
  return expected && expected !== 'SQL' ? `${name} totals ${expected}` : `${name} ties to source SQL`;
}
