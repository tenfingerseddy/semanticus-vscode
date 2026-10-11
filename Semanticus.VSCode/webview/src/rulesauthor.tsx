// Custom rule authoring — the shared "Custom rules" panel used by BOTH the BPA tab (kind='bpa') and the
// AI Readiness tab (kind='readiness'). Progressive disclosure per the analyst-to-pro-dev bar: templates are
// the front door (start from a working example), the expression language is the advanced reveal. Everything
// routes through the engine's real ops: getCustomRules (list), validateRule (LIVE compile + an honest
// test-run preview against the open model, clearly labeled as not saved), loadBpaRules / loadReadinessRules
// (save, merge by id), resetBpaRules / resetReadinessRules (clear). Authoring is FREE (content, like
// save_workflow); the enforcement machinery stays where it already is.
import { useEffect, useRef, useState } from 'react';
import { rpc, onDidChange } from './bridge';
import { RuleCheckBox } from './sendtask';
import { DraftMark, EditMovedBanner, sameValue, useEditBase, useHoldDraft } from './editbase';
import { draftModel, draftStore, heldDraft, heldId, pruneGone, useHeldDrafts } from './drafts';

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
    { label: 'Auto-fix: hide key columns', blurb: 'Flags visible key columns, with a Fix button.', draft: { id: 'ORG_HIDE_KEYS', name: 'Hide key columns', category: 'Model Layout', severity: '2', scope: 'Column', expression: 'Name.EndsWith("Key") and not IsHidden', fixExpression: 'IsHidden = true', description: '%object% looks like a key column and is visible.' } },
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
  // A rule typed and not saved is held above this panel (drafts.ts): a tab switch, a restart and a reload keep it, a rule per id and one for a new rule.
  const scopeOf = (id: string | null) => `rules|${kind}|${id ?? 'new'}`;
  const heldRules = useHeldDrafts(`rules|${kind}|`);
  const [info, setInfo] = useState<CustomRulesInfo | null>(null);
  const [form, setForm] = useState<{ draft: Draft; editingId: string | null } | null>(() => {
    // The panel opens on the form that was left half written: the new rule first, else the first rule being edited.
    const m = draftModel(), ids = m ? [...draftStore.held(m, `rules|${kind}|`)].map((h) => (JSON.parse(h) as [string, string])[0].slice(`rules|${kind}|`.length)) : [];
    const id = ids.includes('new') ? 'new' : ids[0];
    const h = id === undefined ? undefined : heldDraft<Draft>(scopeOf(id === 'new' ? null : id));
    return h ? { draft: h.value, editingId: id === 'new' ? null : id } : null;
  });
  const formRef = useRef(form); formRef.current = form;   // the form as it is NOW, for a Save that finishes after more was typed
  const [open, setOpen] = useState(!!form);
  const newBase = useRef<Draft | undefined>(form && !form.editingId ? heldDraft<Draft>(scopeOf(null))?.base : undefined);   // what a NEW rule's form started from (blank or a template)
  const [err, setErr] = useState<string | null>(null);
  const [check, setCheck] = useState<RuleCheck | null>(null);
  const [checking, setChecking] = useState(false);
  const [busy, setBusy] = useState(false);
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
  function closeForm(modelKey = draftModel()) {
    invalidateChecks();
    if (form && modelKey) draftStore.clear(modelKey, scopeOf(form.editingId));   // Cancel and a save the engine took end the draft, under the model it belongs to
    setForm(null); setCheck(null); setChecking(false);
  }

  const rules = (kind === 'bpa' ? info?.bpa : info?.readiness) ?? [];

  const liveRules = async () => { const now = await rpc<CustomRulesInfo>('getCustomRules'); return (kind === 'bpa' ? now.bpa : now.readiness) ?? []; };

  async function refresh() {
    try {
      const read = await rpc<CustomRulesInfo>('getCustomRules');
      pruneGone(`rules|${kind}|`, ((kind === 'bpa' ? read.bpa : read.readiness) ?? []).map((r) => String(r.id)), ['new']);   // a rule that is gone takes its draft; a new rule's stays
      setInfo(read); setErr(null);
    }
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
    const start = { ...BLANK, ...(kind === 'bpa' ? { severity: '2' } : { severity: 'Medium' }), ...seed };
    // Words left on this rule earlier open again (for an edit, over the rule they were typed over: the guard below asks if it moved).
    const held = heldDraft<Draft>(scopeOf(editingId));
    const draft = held ? held.value : start;
    newBase.current = editingId ? undefined : (held ? held.base : start);
    setForm({ draft, editingId });
    setCheck(null); setOpen(true);
    if (draft.id && draft.expression) scheduleCheck(draft);
  }

  // Editing a rule saves the whole rule from the form (merge by id), so the form must know which version of the rule it
  // started from: the assistant can rewrite the same rule (load_rules) while the form is open. The guard follows a live
  // change while nothing is typed, asks (Keep mine / Use the new one) when words are typed, and Save re-reads first.
  const editedLive = form?.editingId ? rules.find((r) => String(r.id) === form.editingId) : undefined;
  const readRule = useRef<Record<string, unknown> | null>(null);   // the rule the last Save read, as the engine sent it: the write names it as the rule it replaces
  const guard = useEditBase<Draft>({
    current: editedLive ? ruleToDraft(kind, editedLive) : undefined, scope: form?.editingId ?? undefined,
    draft: form?.draft, editing: !!form?.editingId, persist: form?.editingId ? { scope: scopeOf(form.editingId) } : undefined,
    reseed: (v) => { setForm((f) => f && { ...f, draft: v }); scheduleCheck(v); },
    readNow: async () => {
      const live = (await liveRules()).find((r) => String(r.id) === form?.editingId);
      if (!live) throw new Error('This rule was removed while you were editing it. Cancel, then make it again with New rule if you still want it.');
      readRule.current = live;
      return ruleToDraft(kind, live);
    },
  });

  const newDrafted = useHoldDraft<Draft>({ persist: form && !form.editingId ? { scope: scopeOf(null) } : undefined, draft: form?.draft, base: newBase.current, active: !!form && !form.editingId });

  async function save() {
    if (!form) return;
    const sent = form.draft, startedOn = draftModel();   // what this Save carries, and the model it was pressed on
    setBusy(true); setErr(null);
    try {
      const id = form.draft.id.trim();
      if (!form.editingId && rules.some((r) => String(r.id).toLowerCase() === id.toLowerCase())) {
        setErr('This id is already used. Pick a new one, or edit the rule that has it.');
        return;
      }
      // The same check against the live set: the assistant may have added a rule with this id since the page drew its list.
      // A rename counts too, since replacing the set would otherwise leave two rules with one id.
      if ((!form.editingId || form.editingId !== id) && (await liveRules()).some((r) => String(r.id).toLowerCase() === id.toLowerCase() && String(r.id) !== form.editingId)) {
        setErr('This id is already used. Pick a new one, or edit the rule that has it.');
        return;
      }
      if (form.editingId && !(await guard.beforeSave())) return;
      const rule = draftToRule(kind, form.draft);
      // An edit writes naming the rule the Save read; the engine compares it inside its own write. A new rule has nothing to name.
      const expected = form.editingId && readRule.current ? JSON.stringify(readRule.current) : null;
      if (form.editingId && form.editingId !== form.draft.id.trim()) {
        // A rename while editing: replace the whole set with the old rule swapped out, so the old id never lingers.
        // The set is read again first: replacing it from the list this page drew earlier would delete any rule the
        // assistant loaded (load_custom_rules) since (UAT stale-copy class).
        const remaining = (await liveRules()).filter((r) => String(r.id) !== form.editingId).map((r) => ({ ...r }));
        await rpc(loadOp, JSON.stringify([...remaining, rule]), true, 'human', expected);
      } else {
        await rpc(loadOp, JSON.stringify([rule]), false, 'human', expected);   // merge by id (also how edits overwrite in place)
      }
      // R-B: the form closes, and the draft ends, only when the words it holds are still exactly the ones the engine took. Words typed while the Save was on its way stay in the form,
      // over the rule just saved.
      if (sameValue(formRef.current?.draft, sent)) closeForm(startedOn);
      else if (form.editingId) guard.saved(sent, ruleToDraft(kind, rule));
      await refresh();
      onChanged?.();
    } catch (e) { if (!(await guard.refused(e))) setErr(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  }

  async function removeOne(id: string) {
    setBusy(true); setErr(null);
    try {
      const remaining = (await liveRules()).filter((r) => String(r.id) !== id);   // from the live set, never the page's older copy
      if (remaining.length === 0) await rpc(resetOp);
      else await rpc(loadOp, JSON.stringify(remaining), true);
      await refresh();
      onChanged?.();
    } catch (e) { setErr(String((e as Error).message ?? e)); }
    finally { setBusy(false); }
  }

  // The prompt the old help button copied, with the person's own words in place of the gap it
  // left for them to fill in the chat. It is the brief of the Plan task RuleCheckBox gives (Copy stays beside it).
  function assistantPrompt(check: string): string {
    if (!form) return '';
    const what = kind === 'bpa' ? 'Model quality (BPA)' : 'AI-readiness';
    const prompt =
      `Using the Semanticus MCP tools, help me write a custom ${what} rule for the open model.\n` +
      `What I want to check: ${check}\n` +
      `My draft so far: ${JSON.stringify(draftToRule(kind, form.draft))}\n` +
      `Steps: preview it with validate_rule (kind='${kind}') until it compiles and the trial run flags the right objects, ` +
      `then save it with load_rules (system='${kind === 'bpa' ? 'bpa' : 'air'}', merge). ` +
      `get_custom_rules lists the valid categories and scopes.`;
    return prompt;
  }

  const d = form?.draft;
  const canSave = !!form && !!d?.id.trim() && !!d?.category.trim() && !!d?.expression.trim() && check?.valid === true && !checking;

  return (
    <section className="sem-card">
      <div className="flex items-center gap-2">
        <button onClick={() => setOpen(!open)} className="sem-card-title flex items-center gap-2 text-left" title={open ? 'Collapse' : 'Expand'}>
          <span className="inline-flex w-3 justify-center font-normal" style={{ color: 'var(--sem-muted)' }}>{open ? '▾' : '▸'}</span>
          <span title="Your own checks. They run with every scan and travel with the model.">Custom rules</span> <span className="sem-num font-normal" style={{ color: 'var(--sem-muted)' }}>{rules.length}</span>
        </button>
        <span className="ml-auto" />
        <DraftMark show={!form && heldRules.has(heldId(scopeOf(null), ''))} />
        {!form && <Btn onClick={() => openForm({}, null)}>New rule</Btn>}
      </div>

      {err && <div role="alert" className="mt-2 flex items-start gap-2 sem-text-body whitespace-pre-wrap"><span className="sem-chip sem-chip-bad shrink-0" aria-hidden="true" />{err}</div>}

      {open && !form && (
        rules.length === 0
          ? <div className="mt-3 sem-text-meta">None yet. Start from a template with New rule, or ask your assistant to author one for you.</div>
          : (
            <div className="mt-3 flex flex-col" style={{ borderTop: '1px solid var(--sem-rule)' }}>
              {rules.map((r) => (
                <div key={String(r.id)} className="sem-list-row" style={{ paddingInline: 0 }}>
                  <span className="sem-num sem-text-meta">{String(r.id)}</span>
                  <span className="truncate">{String(r.name ?? '')}</span>
                  <span className="sem-text-meta">{String(r.scope ?? '')}{r.category ? ` · ${String(r.category)}` : ''}</span>
                  <span className="ml-auto flex items-center gap-1">
                    <DraftMark show={heldRules.has(heldId(scopeOf(String(r.id)), ''))} />
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
          {/* The assistant first (D4): say what the rule should check and give it away. The form below is for doing it by hand. */}
          <div className="flex items-center gap-2">
            <RuleCheckBox kind={kind === 'bpa' ? 'bpa' : 'air'} prompt={assistantPrompt} />
          </div>
          {form.editingId === null && !d.expression && (
            <div>
              <div className="sem-text-meta mb-2">Start from a working example (you can change everything):</div>
              <div className="grid grid-cols-2 gap-3" style={{ maxWidth: 720 }}>
                {TEMPLATES[kind].map((t) => (
                  <button key={t.label} onClick={() => openForm(t.draft, null)}
                    className="sem-inset text-left">
                    <div className="font-medium">{t.label}</div>
                    <div className="sem-text-meta">{t.blurb}</div>
                  </button>
                ))}
              </div>
            </div>
          )}

          <div className="grid gap-2" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', maxWidth: 900 }}>
            <Field label="Rule id" hint="Short and stable, e.g. ORG-DESC-KPI">
              <input className="sem-field w-full" value={d.id} onChange={(e) => setDraft({ id: e.target.value })} placeholder="ORG-MY-RULE" />
            </Field>
            <Field label="Name" hint="What the rule checks, in plain words">
              <input className="sem-field w-full" value={d.name} onChange={(e) => setDraft({ name: e.target.value })} placeholder="KPI measures need descriptions" />
            </Field>
            <Field label="Category">
              {kind === 'readiness' ? (
                <select className="sem-field w-full" value={d.category} onChange={(e) => setDraft({ category: e.target.value })}>
                  <option value="">Pick a category…</option>
                  {(info?.readinessCategories ?? []).map((c) => <option key={c} value={c}>{c}</option>)}
                </select>
              ) : (
                <>
                  <input className="sem-field w-full" list="ra-bpa-cats" value={d.category} onChange={(e) => setDraft({ category: e.target.value })} placeholder="Metadata" />
                  <datalist id="ra-bpa-cats">{BPA_CATEGORY_SUGGESTIONS.map((c) => <option key={c} value={c} />)}</datalist>
                </>
              )}
            </Field>
            <Field label="Severity">
              <select className="sem-field w-full" value={d.severity} onChange={(e) => setDraft({ severity: e.target.value })}>
                {kind === 'readiness'
                  ? ['Info', 'Medium', 'High', 'Critical'].map((s) => <option key={s} value={s}>{s}</option>)
                  : [['1', 'Info'], ['2', 'Warning'], ['3', 'Error']].map(([v, l]) => <option key={v} value={v}>{l}</option>)}
              </select>
            </Field>
            <Field label="Checks" hint="Which objects the rule looks at">
              <select className="sem-field w-full" value={d.scope} onChange={(e) => setDraft({ scope: e.target.value })}>
                {(info?.scopes ?? ['Measure', 'Column', 'Table']).map((s) => <option key={s} value={s}>{s}</option>)}
              </select>
            </Field>
            {kind === 'readiness' && (
              <Field label="Only where (optional)" hint={'Limits which objects are scored, e.g. DisplayFolder.StartsWith("KPI")'}>
                <input className="sem-field w-full" value={d.appliesTo} onChange={(e) => setDraft({ appliesTo: e.target.value })} placeholder="not IsHidden" spellCheck={false} />
              </Field>
            )}
          </div>

          <Field label="Flag an object when" hint="The condition, e.g. string.IsNullOrEmpty(Description). Properties come from the object type you picked under Checks.">
            <textarea className="sem-field w-full" rows={2} value={d.expression} onChange={(e) => setDraft({ expression: e.target.value })} placeholder='string.IsNullOrEmpty(Description)' spellCheck={false} style={{ fontFamily: 'var(--vscode-editor-font-family, monospace)' }} />
          </Field>

          {guard.moved && <EditMovedBanner what="rule" theirs={guard.theirs} onKeepMine={guard.keepMine} onUseNew={guard.useNew} disabled={busy} />}

          <div className="grid gap-2" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(260px, 1fr))', maxWidth: 900 }}>
            {kind === 'readiness' && (
              <Field label="Finding message (optional)" hint="%object% inserts the object name">
                <input className="sem-field w-full" value={d.message} onChange={(e) => setDraft({ message: e.target.value })} placeholder="%object% needs a description" />
              </Field>
            )}
            <Field label="Why this matters (optional)">
              <input className="sem-field w-full" value={d.description} onChange={(e) => setDraft({ description: e.target.value })} />
            </Field>
            {kind === 'readiness' ? (
              <Field label="How it gets fixed" hint="Advisory: who acts on a finding">
                <select className="sem-field w-full" value={d.fixKind} onChange={(e) => setDraft({ fixKind: e.target.value })}>
                  <option value="None">Just report it</option>
                  <option value="AiContent">Your assistant writes the fix</option>
                  <option value="Proposal">A person reviews it</option>
                </select>
              </Field>
            ) : (
              <Field label="Auto-fix (optional)" hint="e.g. IsHidden = true. Lets Fix change it.">
                <input className="sem-field w-full" value={d.fixExpression} onChange={(e) => setDraft({ fixExpression: e.target.value })} placeholder="IsHidden = true" spellCheck={false} />
              </Field>
            )}
          </div>

          {(check || checking) && (
            <div className="sem-inset sem-text-meta" style={{ color: checking ? 'var(--sem-muted)' : 'var(--sem-fg)' }}>
              {checking ? 'Checking against the open model…' : check!.valid ? (
                <span>
                  <b className="sem-chip sem-chip-ok" style={{ color: 'var(--sem-fg)' }}>Works.</b>{' '}
                  {check!.dormant
                    ? 'Nothing on this model matches, so it never changes the score.'
                    : <>Trial run: would flag <b>{check!.violations}</b> of <b>{check!.applicable}</b> object{check!.applicable === 1 ? '' : 's'}{check!.sample.length > 0 ? <> (first: {check!.sample.slice(0, 3).join(', ')})</> : null}.</>}
                  {' '}<span style={{ color: 'var(--sem-muted)' }}>Preview only. Nothing is saved until you click Save rule.</span>
                  {check!.note && !check!.dormant && check!.note.includes('OVERRIDE') ? <div className="mt-1 flex items-start gap-2"><span className="sem-chip sem-chip-warn shrink-0" aria-hidden="true" />{check!.note}</div> : null}
                </span>
              ) : (
                <div className="flex flex-col gap-1">{check!.errors.map((e, i) => <div key={i} className="flex items-start gap-2"><span className="sem-chip sem-chip-bad shrink-0" aria-hidden="true" />{e}</div>)}</div>
              )}
            </div>
          )}

          <div className="flex items-center gap-2">
            <Btn primary disabled={!canSave || busy} onClick={() => void save()}
              title={canSave ? 'Save onto the model (undoable). It runs with every scan from now on.' : 'Fill in id, category and the condition, and wait for a passing check.'}>
              {busy ? 'Saving…' : form.editingId ? 'Save changes' : 'Save rule'}
            </Btn>
            <Btn onClick={closeForm}>Cancel</Btn>
            <DraftMark show={guard.drafted || newDrafted} />
          </div>
        </div>
      )}

    </section>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) {
  return (
    <label className="flex flex-col gap-1 min-w-0">
      <span className="sem-text-meta">{label}{hint && <span> · {hint}</span>}</span>
      {children}
    </label>
  );
}
function Btn({ children, onClick, primary, disabled, title }: { children: React.ReactNode; onClick?: () => void; primary?: boolean; disabled?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className={primary ? 'sem-btn sem-btn-primary' : 'sem-btn'}>
      {children}
    </button>
  );
}
function MiniBtn({ children, onClick, disabled }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled}
      className="sem-btn sem-btn-sm">
      {children}
    </button>
  );
}
