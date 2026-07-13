// Custom rule authoring — the shared "Custom rules" panel used by BOTH the BPA tab (kind='bpa') and the
// AI Readiness tab (kind='readiness'). Progressive disclosure per the analyst-to-pro-dev bar: templates are
// the front door (start from a working example), the expression language is the advanced reveal. Everything
// routes through the engine's real ops: getCustomRules (list), validateRule (LIVE compile + an honest
// test-run preview against the open model, clearly labeled as not saved), loadBpaRules / loadReadinessRules
// (save, merge by id), resetBpaRules / resetReadinessRules (clear). Authoring is FREE (content, like
// save_workflow); the enforcement machinery stays where it already is.
import { useEffect, useRef, useState } from 'react';
import { rpc, onDidChange, copyText } from './bridge';

type Kind = 'bpa' | 'readiness';

// Wire types (camelCase from the engine). Rule objects round-trip: what getCustomRules returns is what the
// load ops accept (the engine parses property names case-insensitively).
interface RuleCheck { id?: string; valid: boolean; errors: string[]; applicable: number; violations: number; dormant: boolean; sample: string[]; note?: string }
interface RuleValidationResult { kind: string; ruleCount: number; allValid: boolean; rules: RuleCheck[]; note?: string }
interface CustomRulesInfo { bpa: Record<string, unknown>[]; readiness: Record<string, unknown>[]; readinessCategories: string[]; scopes: string[]; note?: string }

interface Draft {
  id: string; name: string; category: string; severity: string; scope: string;
  appliesTo: string; expression: string; message: string; description: string;
  fixKind: string;        // readiness only
  fixExpression: string;  // bpa only
}
const BLANK: Draft = { id: '', name: '', category: '', severity: '', scope: 'Measure', appliesTo: '', expression: '', message: '', description: '', fixKind: 'None', fixExpression: '' };

// Starter templates — working examples an analyst can save as-is, then tweak. One vocabulary across kinds.
const TEMPLATES: Record<Kind, { label: string; blurb: string; draft: Partial<Draft> }[]> = {
  readiness: [
    { label: 'Measures missing a description', blurb: 'Flag visible measures with no description.', draft: { id: 'ORG-DESC-MEASURE-FOLDER', name: 'Measures need descriptions', category: 'Descriptions', severity: 'Medium', scope: 'Measure', appliesTo: 'not IsHidden', expression: 'string.IsNullOrEmpty(Description)', message: '%object% has no description. AI features ground their answers on descriptions.', fixKind: 'AiContent' } },
    { label: 'Naming pattern', blurb: 'Flag names that contain an underscore.', draft: { id: 'ORG-NAME-UNDERSCORE', name: 'No underscores in measure names', category: 'Naming', severity: 'Medium', scope: 'Measure', expression: 'Name.Contains("_")', message: '%object% has an underscore in its name. Use plain words so people and AI read it.', fixKind: 'AiContent' } },
    { label: 'Format strings in a folder', blurb: 'Measures in one folder must have a format string.', draft: { id: 'ORG-FMT-KPI', name: 'KPI measures need format strings', category: 'Formatting', severity: 'Medium', scope: 'Measure', appliesTo: 'DisplayFolder.StartsWith("KPI")', expression: 'string.IsNullOrEmpty(FormatString)', message: '%object% has no format string, so its numbers render raw.' } },
    { label: 'Key columns left visible', blurb: 'Columns named like keys should be hidden.', draft: { id: 'ORG-VIS-KEY', name: 'Hide key columns', category: 'Visibility', severity: 'Medium', scope: 'Column', appliesTo: 'Name.EndsWith("Key")', expression: 'not IsHidden', message: '%object% looks like a key column and is visible. Hide it so AI features skip it.', fixKind: 'Proposal' } },
  ],
  bpa: [
    { label: 'Measures missing a description', blurb: 'Flag measures with no description.', draft: { id: 'ORG_MEASURE_DESC', name: 'Measures need descriptions', category: 'Metadata', severity: '2', scope: 'Measure', expression: 'string.IsNullOrEmpty(Description)', description: '%object% has no description.' } },
    { label: 'Naming pattern', blurb: 'Flag column names that contain an underscore.', draft: { id: 'ORG_COLUMN_UNDERSCORE', name: 'No underscores in column names', category: 'Naming Conventions', severity: '1', scope: 'Column', expression: 'Name.Contains("_")', description: '%object% has an underscore in its name. Use plain words.' } },
    { label: 'Format strings in a folder', blurb: 'Measures in one folder must have a format string.', draft: { id: 'ORG_FMT_KPI', name: 'KPI measures need format strings', category: 'Formatting', severity: '2', scope: 'Measure', expression: 'string.IsNullOrEmpty(FormatString) and DisplayFolder.StartsWith("KPI")', description: '%object% has no format string.' } },
    { label: 'Auto-fix: hide key columns', blurb: 'Flags visible key columns, with a one-click fix.', draft: { id: 'ORG_HIDE_KEYS', name: 'Hide key columns', category: 'Model Layout', severity: '2', scope: 'Column', expression: 'Name.EndsWith("Key") and not IsHidden', fixExpression: 'IsHidden = true', description: '%object% looks like a key column and is visible.' } },
  ],
};

const BPA_CATEGORY_SUGGESTIONS = ['Performance', 'DAX Expressions', 'Naming Conventions', 'Formatting', 'Error Prevention', 'Maintenance', 'Metadata', 'Model Layout'];

function draftToRule(kind: Kind, d: Draft): Record<string, unknown> {
  const r: Record<string, unknown> = { ID: d.id.trim(), Name: d.name.trim() || undefined, Category: d.category.trim(), Scope: d.scope.trim(), Expression: d.expression };
  if (d.description.trim()) r.Description = d.description.trim();
  if (kind === 'readiness') {
    if (d.severity) r.Severity = d.severity;
    if (d.appliesTo.trim()) r.AppliesTo = d.appliesTo;
    if (d.message.trim()) r.Message = d.message.trim();
    if (d.fixKind && d.fixKind !== 'None') r.FixKind = d.fixKind;
  } else {
    r.Severity = parseInt(d.severity, 10) || 2;
    if (d.fixExpression.trim()) r.FixExpression = d.fixExpression.trim();
  }
  return r;
}

function ruleToDraft(kind: Kind, r: Record<string, unknown>): Draft {
  const s = (k: string) => { const v = r[k]; return v == null ? '' : String(v); };
  return {
    id: s('id'), name: s('name'), category: s('category'),
    severity: kind === 'bpa' ? (s('severity') || '2') : (s('severity') || 'Medium'),
    scope: s('scope') || 'Measure', appliesTo: s('appliesTo'), expression: s('expression'),
    message: s('message'), description: s('description'),
    fixKind: s('fixKind') || 'None', fixExpression: s('fixExpression'),
  };
}

export function CustomRulesPanel({ kind, onChanged }: { kind: Kind; onChanged?: () => void }) {
  const loadOp = kind === 'bpa' ? 'loadBpaRules' : 'loadReadinessRules';
  const resetOp = kind === 'bpa' ? 'resetBpaRules' : 'resetReadinessRules';
  const [info, setInfo] = useState<CustomRulesInfo | null>(null);
  const [open, setOpen] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [form, setForm] = useState<{ draft: Draft; editingId: string | null } | null>(null);
  const [check, setCheck] = useState<RuleCheck | null>(null);
  const [checking, setChecking] = useState(false);
  const [busy, setBusy] = useState(false);
  const [copied, setCopied] = useState(false);
  const timer = useRef<number | undefined>(undefined);
  const checkTimer = useRef<number | undefined>(undefined);
  const checkSeq = useRef(0);

  // One lifecycle rule: closing the form (Cancel or a successful save) or unmounting the panel invalidates
  // the check sequence and clears any scheduled timer, so no validation callback — debounced or already
  // in flight — can land afterwards (no state updates on a closed form/unmounted panel, no wasted RPC).
  function invalidateChecks() {
    checkSeq.current++;
    window.clearTimeout(checkTimer.current);
  }
  function closeForm() {
    invalidateChecks();
    setForm(null); setCheck(null); setChecking(false);
  }

  const rules = (kind === 'bpa' ? info?.bpa : info?.readiness) ?? [];

  async function refresh() {
    try { setInfo(await rpc<CustomRulesInfo>('getCustomRules')); setErr(null); }
    catch (e) { setErr(String((e as Error).message ?? e)); }
  }
  useEffect(() => {
    void refresh();
    // Dual-drive: the AI assistant can load rules over MCP mid-session; the panel follows the same broadcast.
    const off = onDidChange(() => { window.clearTimeout(timer.current); timer.current = window.setTimeout(() => void refresh(), 400); });
    return () => { off(); window.clearTimeout(timer.current); invalidateChecks(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Live validation: compile via the engine's real parser + an honest test run, debounced as the user types.
  function scheduleCheck(d: Draft) {
    setChecking(true);
    window.clearTimeout(checkTimer.current);   // supersede any earlier pending check outright
    const seq = ++checkSeq.current;
    checkTimer.current = window.setTimeout(async () => {
      if (seq !== checkSeq.current) return;
      if (!d.id.trim() || !d.expression.trim()) { setCheck(null); setChecking(false); return; }
      try {
        const r = await rpc<RuleValidationResult>('validateRule', kind, JSON.stringify(draftToRule(kind, d)));
        if (seq === checkSeq.current) { setCheck(r.rules[0] ?? null); setChecking(false); }
      } catch (e) {
        if (seq === checkSeq.current) { setCheck({ valid: false, errors: [String((e as Error).message ?? e)], applicable: 0, violations: 0, dormant: false, sample: [] }); setChecking(false); }
      }
    }, 500);
  }
  const setDraft = (patch: Partial<Draft>) => setForm((f) => {
    if (!f) return f;
    const draft = { ...f.draft, ...patch };
    scheduleCheck(draft);
    return { ...f, draft };
  });

  function openForm(seed: Partial<Draft>, editingId: string | null) {
    const draft = { ...BLANK, ...(kind === 'bpa' ? { severity: '2' } : { severity: 'Medium' }), ...seed };
    setForm({ draft, editingId });
    setCheck(null); setCopied(false); setOpen(true);
    if (draft.id && draft.expression) scheduleCheck(draft);
  }

  async function save() {
    if (!form) return;
    setBusy(true); setErr(null);
    try {
      const rule = draftToRule(kind, form.draft);
      if (form.editingId && form.editingId !== form.draft.id.trim()) {
        // A rename while editing: replace the whole set with the old rule swapped out, so the old id never lingers.
        const remaining = rules.filter((r) => String(r.id) !== form.editingId).map((r) => ({ ...r }));
        await rpc(loadOp, JSON.stringify([...remaining, rule]), true);
      } else {
        await rpc(loadOp, JSON.stringify([rule]), false);   // merge by id (also how edits overwrite in place)
      }
      closeForm();
      await refresh();
      onChanged?.();
    } catch (e) { setErr(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  }

  async function removeOne(id: string) {
    setBusy(true); setErr(null);
    try {
      const remaining = rules.filter((r) => String(r.id) !== id);
      if (remaining.length === 0) await rpc(resetOp);
      else await rpc(loadOp, JSON.stringify(remaining), true);
      await refresh();
      onChanged?.();
    } catch (e) { setErr(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  }

  async function askAssistant() {
    if (!form) return;
    const what = kind === 'bpa' ? 'Best Practice (BPA)' : 'AI-readiness';
    const prompt =
      `Using the Semanticus MCP tools, help me write a custom ${what} rule for the open model.\n` +
      `What I want to check: <describe the practice in plain words>\n` +
      `My draft so far: ${JSON.stringify(draftToRule(kind, form.draft))}\n` +
      `Steps: preview it with validate_rule (kind='${kind}') until it compiles and the test run flags the right objects, ` +
      `then save it with ${kind === 'bpa' ? 'load_bpa_rules' : 'load_readiness_rules'} (merge). ` +
      `get_custom_rules lists the valid categories and scopes.`;
    if (await copyText(prompt)) { setCopied(true); window.setTimeout(() => setCopied(false), 2500); }
  }

  const d = form?.draft;
  const canSave = !!form && !!d?.id.trim() && !!d?.category.trim() && !!d?.expression.trim() && check?.valid === true && !checking;

  return (
    <div className="rounded-xl border p-4" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2">
        <button onClick={() => setOpen(!open)} className="flex items-center gap-2 text-left" title={open ? 'Collapse' : 'Expand'}>
          <span className="text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>
            Custom rules <span style={{ color: 'var(--sem-fg)' }}>{rules.length}</span>
          </span>
          <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>{open ? '▾' : '▸'}</span>
        </button>
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Your own checks. They run with every scan and travel with the model.</span>
        <span className="ml-auto" />
        {!form && <Btn primary onClick={() => openForm({}, null)}>New rule</Btn>}
      </div>

      {err && <div className="mt-2 rounded-lg px-3 py-2 text-[12px] whitespace-pre-wrap" style={{ background: 'color-mix(in srgb,var(--sem-bad) 14%, transparent)', color: 'var(--sem-bad)' }}>{err}</div>}

      {open && !form && (
        rules.length === 0
          ? <div className="mt-3 text-[12px]" style={{ color: 'var(--sem-muted)' }}>None yet. Start from a template with New rule, or ask the AI Assistant to author one for you.</div>
          : (
            <div className="mt-3 flex flex-col gap-1.5">
              {rules.map((r) => (
                <div key={String(r.id)} className="flex items-center gap-2 rounded-lg px-2.5 py-1.5" style={{ background: 'var(--sem-surface-2)' }}>
                  <span className="text-[11px] font-mono px-1.5 py-0.5 rounded" style={{ background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)' }}>{String(r.id)}</span>
                  <span className="text-[12px] truncate">{String(r.name ?? '')}</span>
                  <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{String(r.scope ?? '')}{r.category ? ` · ${String(r.category)}` : ''}</span>
                  <span className="ml-auto flex items-center gap-1">
                    <MiniBtn disabled={busy} onClick={() => openForm(ruleToDraft(kind, r), String(r.id))}>Edit</MiniBtn>
                    <MiniBtn disabled={busy} onClick={() => void removeOne(String(r.id))}>Remove</MiniBtn>
                  </span>
                </div>
              ))}
            </div>
          )
      )}

      {form && d && (
        <div className="mt-3 flex flex-col gap-3">
          {form.editingId === null && !d.expression && (
            <div>
              <div className="text-[11px] mb-1.5" style={{ color: 'var(--sem-muted)' }}>Start from a working example (you can change everything):</div>
              <div className="grid grid-cols-2 gap-1.5" style={{ maxWidth: 720 }}>
                {TEMPLATES[kind].map((t) => (
                  <button key={t.label} onClick={() => openForm(t.draft, null)}
                    className="text-left rounded-lg px-2.5 py-2 border" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
                    <div className="text-[12px] font-medium">{t.label}</div>
                    <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{t.blurb}</div>
                  </button>
                ))}
              </div>
            </div>
          )}

          <div className="grid gap-2" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', maxWidth: 900 }}>
            <Field label="Rule id" hint="Short and stable, e.g. ORG-DESC-KPI">
              <input className="ra-in" value={d.id} onChange={(e) => setDraft({ id: e.target.value })} placeholder="ORG-MY-RULE" />
            </Field>
            <Field label="Name" hint="What the rule checks, in plain words">
              <input className="ra-in" value={d.name} onChange={(e) => setDraft({ name: e.target.value })} placeholder="KPI measures need descriptions" />
            </Field>
            <Field label="Category">
              {kind === 'readiness' ? (
                <select className="ra-in" value={d.category} onChange={(e) => setDraft({ category: e.target.value })}>
                  <option value="">Pick a category…</option>
                  {(info?.readinessCategories ?? []).map((c) => <option key={c} value={c}>{c}</option>)}
                </select>
              ) : (
                <>
                  <input className="ra-in" list="ra-bpa-cats" value={d.category} onChange={(e) => setDraft({ category: e.target.value })} placeholder="Metadata" />
                  <datalist id="ra-bpa-cats">{BPA_CATEGORY_SUGGESTIONS.map((c) => <option key={c} value={c} />)}</datalist>
                </>
              )}
            </Field>
            <Field label="Severity">
              <select className="ra-in" value={d.severity} onChange={(e) => setDraft({ severity: e.target.value })}>
                {kind === 'readiness'
                  ? ['Info', 'Medium', 'High', 'Critical'].map((s) => <option key={s} value={s}>{s}</option>)
                  : [['1', 'Info'], ['2', 'Warning'], ['3', 'Error']].map(([v, l]) => <option key={v} value={v}>{l}</option>)}
              </select>
            </Field>
            <Field label="Checks" hint="Which objects the rule looks at">
              <select className="ra-in" value={d.scope} onChange={(e) => setDraft({ scope: e.target.value })}>
                {(info?.scopes ?? ['Measure', 'Column', 'Table']).map((s) => <option key={s} value={s}>{s}</option>)}
              </select>
            </Field>
            {kind === 'readiness' && (
              <Field label="Only where (optional)" hint={'Narrows the population that is scored, e.g. DisplayFolder.StartsWith("KPI")'}>
                <input className="ra-in" value={d.appliesTo} onChange={(e) => setDraft({ appliesTo: e.target.value })} placeholder="not IsHidden" spellCheck={false} />
              </Field>
            )}
          </div>

          <Field label="Flag an object when" hint="The condition, e.g. string.IsNullOrEmpty(Description). Properties come from the object type you picked under Checks.">
            <textarea className="ra-in" rows={2} value={d.expression} onChange={(e) => setDraft({ expression: e.target.value })} placeholder='string.IsNullOrEmpty(Description)' spellCheck={false} style={{ fontFamily: 'var(--vscode-editor-font-family, monospace)' }} />
          </Field>

          <div className="grid gap-2" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(260px, 1fr))', maxWidth: 900 }}>
            {kind === 'readiness' && (
              <Field label="Finding message (optional)" hint="%object% inserts the object name">
                <input className="ra-in" value={d.message} onChange={(e) => setDraft({ message: e.target.value })} placeholder="%object% needs a description" />
              </Field>
            )}
            <Field label="Why this matters (optional)">
              <input className="ra-in" value={d.description} onChange={(e) => setDraft({ description: e.target.value })} />
            </Field>
            {kind === 'readiness' ? (
              <Field label="How it gets fixed" hint="Advisory: who acts on a finding">
                <select className="ra-in" value={d.fixKind} onChange={(e) => setDraft({ fixKind: e.target.value })}>
                  <option value="None">Just report it</option>
                  <option value="AiContent">The AI Assistant writes the fix</option>
                  <option value="Proposal">A person reviews it</option>
                </select>
              </Field>
            ) : (
              <Field label="Auto-fix (optional)" hint="Property = value, e.g. IsHidden = true. Enables one-click Fix.">
                <input className="ra-in" value={d.fixExpression} onChange={(e) => setDraft({ fixExpression: e.target.value })} placeholder="IsHidden = true" spellCheck={false} />
              </Field>
            )}
          </div>

          {(check || checking) && (
            <div className="rounded-lg px-3 py-2 text-[12px]"
              style={checking
                ? { background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }
                : check!.valid
                  ? { background: 'color-mix(in srgb,var(--sem-good) 12%, transparent)', color: 'var(--sem-fg)' }
                  : { background: 'color-mix(in srgb,var(--sem-bad) 12%, transparent)', color: 'var(--sem-bad)' }}>
              {checking ? 'Checking against the open model…' : check!.valid ? (
                <span>
                  <b style={{ color: 'var(--sem-good)' }}>Compiles.</b>{' '}
                  {check!.dormant
                    ? 'No objects match its population on this model, so it stays inactive and never moves the score.'
                    : <>Test run: would flag <b>{check!.violations}</b> of <b>{check!.applicable}</b> object{check!.applicable === 1 ? '' : 's'}{check!.sample.length > 0 ? <> (first: {check!.sample.slice(0, 3).join(', ')})</> : null}.</>}
                  {' '}<span style={{ color: 'var(--sem-muted)' }}>Preview only. Nothing is saved until you click Save rule.</span>
                  {check!.note && !check!.dormant && check!.note.includes('OVERRIDE') ? <div className="mt-1" style={{ color: 'var(--sem-warn)' }}>{check!.note}</div> : null}
                </span>
              ) : (
                <div className="flex flex-col gap-1">{check!.errors.map((e, i) => <div key={i}>{e}</div>)}</div>
              )}
            </div>
          )}

          <div className="flex items-center gap-2">
            <Btn primary disabled={!canSave || busy} onClick={() => void save()}
              title={canSave ? 'Save onto the model (undoable). It runs with every scan from now on.' : 'Fill in id, category and the condition, and wait for a passing check.'}>
              {busy ? 'Saving…' : form.editingId ? 'Save changes' : 'Save rule'}
            </Btn>
            <Btn onClick={closeForm}>Cancel</Btn>
            <span className="ml-auto" />
            <Btn onClick={() => void askAssistant()} title="Copies a ready-to-paste prompt. Paste it to the AI Assistant and it will draft, test and save the rule with you.">
              {copied ? 'Copied ✓' : 'Ask the AI Assistant for help'}
            </Btn>
          </div>
        </div>
      )}

      <style>{`.ra-in{width:100%;font-size:12px;padding:5px 8px;border-radius:8px;background:var(--sem-surface-2);color:var(--sem-fg);border:1px solid var(--sem-border);outline:none}
.ra-in:focus{border-color:var(--sem-accent)}`}</style>
    </div>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <label className="flex flex-col gap-1 min-w-0">
      <span className="text-[11px] font-medium" style={{ color: 'var(--sem-muted)' }}>{label}{hint && <span className="font-normal"> · {hint}</span>}</span>
      {children}
    </label>
  );
}
function Btn({ children, onClick, primary, disabled, title }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className="text-[12px] px-3 py-1.5 rounded-lg font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={primary ? { background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' } : { background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
function MiniBtn({ children, onClick, disabled }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled}
      className="text-[11px] px-2 py-0.5 rounded-md font-medium transition-opacity disabled:opacity-40 whitespace-nowrap"
      style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
