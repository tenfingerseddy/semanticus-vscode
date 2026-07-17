import { useEffect, useMemo, useState } from 'react';
import { rpc } from './bridge';
import { Panel, Button, Banner, type WorkflowInfo } from './workflows';
import { uiLabel } from './copy';

// ===================================================================================================
// Profiles + ready-made workflows: the analyst-facing entry point (pro-mode-spec §9.16 / §10.6 profiles-v2
// / §10.12 "Layer 1: Pick & go"). A profile is one engine-owned policy bundle; a template is a workflow with
// plain-language blanks to fill in. Both stay inside Workflows so users never learn another product surface.
//
// This is deliberately NOT a new tab (doctrine Rule 1): it sits at the top of Workflows. Two paths share the
// existing engine state: a profile applies one atomic policy bundle; a ready-made workflow fills a template's
// plain-language questions and then uses instantiate_workflow_template.
// The UI never speaks engine (§10.12 translation table): "instantiate" → "Add this workflow", "binding" →
// "Required for…", "strictness" → "how strict its checks are". Engine terms live only in tooltips.
//
// Free/Pro (§9.8 / §10.7 / brief §4): browsing and preview are free. A profile that makes workflows required
// is Pro. Filling a template is free authoring; running the enforced workflow it creates remains the Pro gate.
// ===================================================================================================

// ---- wire shapes for templates (camelCase, mirror Semanticus.Engine/Workflow.cs SlotDef / WorkflowTemplate) ----
export interface SlotDef {
  name: string; question: string; type: string; required: string;
  default?: string | null; example?: string | null; hint?: string | null; values?: string[];
}
export interface WorkflowTemplateInfo {
  name: string; title: string; whenToUse?: string | null; version: number; source: string;
  slotCount: number; slots: string[]; error?: string | null;
}
export interface WorkflowTemplate {
  name: string; title: string; description: string; whenToUse?: string | null; version: number;
  source: string; error?: string | null; slots: SlotDef[]; markdown: string;
}
interface WorkflowProfileInfo {
  name: string; title: string; description: string; effects: string[]; pro: boolean; selected: boolean;
}
interface WorkflowProfileResult { activeProfile: string; workflows: WorkflowInfo[]; note?: string | null; }

// The six bindable authoring chokepoints, in plain lowercase for use inside a sentence ("every new measure …").
// Only these can be bound (the EnforceBindingAsync call sites), so a scenario only ever binds one of them.
const OP_LABELS: Record<string, string> = {
  create_measure: 'new measure', update_measure: 'edited measure', create_table: 'new table',
  create_calculated_column: 'new calculated column', create_relationship: 'new relationship',
  create_calculation_item: 'new calculation item',
};

// ---- the scenario catalog -------------------------------------------------------------------------
type PreviewTone = 'add' | 'require' | 'set' | 'note';
interface ScenarioBinding { op: string; require: string[]; mode: 'hard' | 'warn' }
interface Scenario {
  id: string;
  title: string;
  tag: string;                 // a one-word category chip (no engine vocabulary)
  blurb: string;               // one sentence: what it enforces, in plain words
  kind: 'template' | 'settings';
  hero?: boolean;               // the two top-billed jobs; depth examples remain below profiles
  profile?: string;            // kind:'settings' — one engine-owned atomic profile bundle
  template?: string;           // kind:'template' — the stock template whose slots become the form
  enforcement?: 'default' | 'off';         // kind:'settings'
  bindings?: ScenarioBinding[];            // kind:'settings' — an enforced requirement is Pro
  recommend?: string[];        // workflow names to run by hand before/after (advisory, never enforced)
  runNote?: string;            // template scenarios: a plain note about running the result (Pro to run)
  appliedNote?: string;        // signposts can guide without pretending they changed policy
  undo: string;                // plain-language "how to turn this back off"
  isDefault?: boolean;         // Standard is the safe zero-config default (§10.12)
}

// The canonical set. Names + wording follow the spec's analyst-facing Layer-1 scenarios (§10.12) with each
// card's EFFECT defined by §9.16's policy semantics (available / bound / strictness) and the shipped stock
// templates. The two showcase jobs lead; profiles set policy; the broader template shelf remains available below.
const SCENARIOS: Scenario[] = [
  {
    // This is a signpost into the shipped workflow, not a hidden policy write. The assistant and Studio use the
    // same workflow once the analyst opens it; choosing the card changes no settings and remains free.
    id: 'make-model-ai-ready',
    title: 'Make the model AI-ready',
    tag: 'AI-ready',
    hero: true,
    blurb: 'Get the model ready for Copilot and Q&A. The AI Assistant fills the gaps those tools rely on, and the readiness grade shows the improvement without claiming the model is perfect.',
    kind: 'settings',
    appliedNote: 'Nothing was changed here. Open "Make the model AI-ready" in your Workflows list, or ask the AI Assistant to make the model AI-ready, to begin.',
    undo: 'This changes no settings. If the assistant later applies model edits, undo that batch from Edit History.',
  },
  {
    id: 'hard-measure',
    title: 'Author a hard measure',
    tag: 'Measure',
    hero: true,
    blurb: 'Pin what the requirement actually says, lock the expected values from raw rows first, then author one candidate and prove it against an independent raw-row witness before any optional speed work.',
    kind: 'template',
    template: 'hard-measure',
    runNote: 'Running the workflow is a Pro capability because it includes enforced equality checks. Setting it up here is free.',
    undo: 'Delete the created workflow from the Workflows list.',
  },
  {
    id: 'standard',
    title: 'Solo analyst',
    tag: 'Everyday',
    blurb: 'Every playbook is on the menu and nothing is required. The AI Assistant can follow a workflow, but nothing blocks your work. The safe default.',
    kind: 'settings',
    profile: 'standard',
    enforcement: 'default',
    isDefault: true,
    undo: 'This is the default. Pick another profile, or use "Required for" on a workflow, to tighten things later.',
  },
  {
    id: 'month-end-close',
    title: 'Month-end close',
    tag: 'Close',
    blurb: 'Before the numbers go out for the month, check every total against the figures finance signed off, prove the headline, record who approved the close, and capture the certified figures so any later refresh or edit that moves them is caught. It reports drift against what was certified; it cannot stop a refresh.',
    kind: 'template',
    template: 'month-end-close',
    runNote: 'Running the checklist is a Pro capability (it has enforced checks). Setting it up here is free.',
    undo: 'Delete the created workflow from the Workflows list.',
  },
  {
    id: 'team-standard',
    title: 'Team standard',
    tag: 'Team',
    blurb: 'Guide every new or edited measure through the Verified measure workflow. Checks warn during rollout, so the team sees gaps without blocking work.',
    kind: 'settings',
    profile: 'team-standard',
    bindings: [
      { op: 'create_measure', require: ['verified-measure'], mode: 'warn' },
      { op: 'update_measure', require: ['verified-measure'], mode: 'warn' },
    ],
    undo: 'Pick Solo analyst, or change individual workflow rules below. A Studio or AI Assistant policy change marks the profile Custom.',
  },
  {
    id: 'consulting-delivery',
    title: 'Consulting delivery',
    tag: 'Delivery',
    blurb: 'Require evidence before new measures, edited measures or relationships are handed over to a client.',
    kind: 'settings',
    profile: 'consulting-delivery',
    bindings: [
      { op: 'create_measure', require: ['verified-measure'], mode: 'hard' },
      { op: 'update_measure', require: ['verified-measure'], mode: 'hard' },
      { op: 'create_relationship', require: ['add-relationship'], mode: 'hard' },
    ],
    undo: 'Pick Solo analyst, or change individual workflow rules below. A Studio or AI Assistant policy change marks the profile Custom.',
  },
  {
    id: 'production-deployment',
    title: 'Production deployment',
    tag: 'Release',
    blurb: 'Before a release goes to production, check the change is allowed, prove the model did not get worse, and record who approved it.',
    kind: 'template',
    template: 'deploy-freeze-guard',
    runNote: 'Running these checks is a Pro capability. Heads up: deploys are not forced through them automatically yet, so run it yourself before you deploy.',
    undo: 'Delete the created workflow from the Workflows list.',
  },
];

// A settings scenario is Pro to APPLY only when it sets an enforced requirement (a binding). Everything else
// (curating the menu, plain strictness) and every template scenario (authoring) is free to apply.
const scenarioProToApply = (s: Scenario) => s.kind === 'settings' && (s.bindings?.length ?? 0) > 0;

// Pure {{slot}} substitution for the preview title — the same deterministic render the engine does (no
// inference, golden rule 1): fall back to a slot's default, then leave the token if truly unfilled.
const renderTemplate = (text: string, values: Record<string, string>, slots: SlotDef[]) =>
  (text || '').replace(/\{\{(\w+)\}\}/g, (m, k) => {
    const v = values[k];
    if (v != null && v.trim() !== '') return v.trim();
    const d = slots.find((s) => s.name === k)?.default;
    return d != null && d !== '' ? d : m;
  });

// kebab-case slug for the new workflow's id, derived from the first required slot (so the file name is
// self-describing). The engine re-validates + refuses a duplicate name; that refusal shows verbatim.
const slug = (s: string) => (s || '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40);
const countSteps = (md: string) => (md.match(/^##\s+Step\b/gim) || []).length;

const PRO_APPLY_REASON =
  'Applying a profile that requires an action is a Pro capability. Browsing profiles, filling ready-made workflows and previewing changes are free.';

interface PreviewLine { tone: PreviewTone; text: string }
const TONE_GLYPH: Record<PreviewTone, string> = { add: '+', require: '●', set: '·', note: 'i' };
const TONE_COLOR: Record<PreviewTone, string> = {
  add: 'var(--sem-good)', require: 'var(--sem-accent)', set: 'var(--sem-muted)', note: 'var(--sem-muted)',
};

// ---- the panel ------------------------------------------------------------------------------------
export function ScenariosPanel({ tier, library, onApplied, onActiveChange, variant = 'full' }: {
  tier: string;
  library: WorkflowInfo[] | null;
  onApplied: (freshLibrary?: WorkflowInfo[]) => void;
  onActiveChange?: (active: boolean) => void;
  // 'templates' hides the profile tier — profiles live only in the Governance section now, so the Home
  // guided-setup overlay offers just the ready-made-workflow shelf (one home per concept).
  variant?: 'full' | 'templates';
}) {
  const isPro = tier === 'pro';
  const showProfiles = variant === 'full';
  const [open, setOpen] = useState(true);              // the whole picker collapses (calm once configured)
  const [templates, setTemplates] = useState<WorkflowTemplateInfo[] | null>(null);
  const [profiles, setProfiles] = useState<WorkflowProfileInfo[]>([]);
  const [scnId, setScnId] = useState<string | null>(null);   // a wizard is active when non-null

  // Load the template shelf once — a template-backed scenario is only offered when its template is installed.
  useEffect(() => {
    rpc<WorkflowTemplateInfo[]>('listWorkflowTemplates').then(setTemplates).catch(() => setTemplates([]));
    rpc<WorkflowProfileInfo[]>('listWorkflowProfiles').then(setProfiles).catch(() => setProfiles([]));
  }, []);

  // Tell the parent to hide the per-workflow detail while a scenario wizard is open (keep the screen focused).
  useEffect(() => { onActiveChange?.(scnId != null); }, [scnId, onActiveChange]);

  const titleOf = (name: string) => library?.find((w) => w.name === name)?.title || name;
  const templateInstalled = (name?: string) => !name || !!templates?.some((t) => t.name === name && !t.error);
  const scenario = SCENARIOS.find((s) => s.id === scnId) ?? null;
  const activeProfile = profiles.find((p) => p.selected)?.name;
  const afterApplied = (freshLibrary?: WorkflowInfo[]) => {
    rpc<WorkflowProfileInfo[]>('listWorkflowProfiles').then(setProfiles).catch(() => undefined);
    onApplied(freshLibrary);
  };

  return (
    <div className="rounded-xl border" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
      <button onClick={() => setOpen((o) => !o)}
        className="w-full flex items-center gap-2 px-4 py-3 text-left"
        title={open ? 'Hide workflow setup' : 'Show workflow setup'}>
        <span className="inline-block transition-transform text-[9px]" style={{ transform: open ? 'rotate(90deg)' : 'none', color: 'var(--sem-muted)' }}>▶</span>
        <span className="text-[13px] font-semibold flex-1">Start with a workflow</span>
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>{activeProfile ? `Current: ${profiles.find((p) => p.name === activeProfile)?.title ?? activeProfile}` : 'Current: Custom'}</span>
      </button>

      {open && (
        <div className="px-4 pb-4">
          {scenario ? (
            <ScenarioWizard
              key={scenario.id} scenario={scenario} isPro={isPro} library={library} titleOf={titleOf}
              onBack={() => setScnId(null)} onApplied={afterApplied} />
          ) : (
            <div>
              <div className="mb-2">
                <div className="text-[11px] font-semibold">Featured workflows</div>
                <div className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>Start with the two jobs that best show the AI Assistant and proof working together.</div>
              </div>
              <div className="grid gap-2.5" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))' }}>
                {SCENARIOS.filter((s) => s.hero).map((s) => (
                  <ScenarioCard key={s.id} scenario={s} isPro={isPro} selected={false}
                    unavailable={templates != null && !templateInstalled(s.template)} onPick={() => setScnId(s.id)} />
                ))}
              </div>
              {showProfiles && (<>
              <div className="mt-4 mb-2 border-t pt-3" style={{ borderColor: 'var(--sem-border)' }}>
                <div className="text-[11px] font-semibold">Workflow profiles</div>
                <div className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>One choice sets what is available, what is required, and how strongly checks apply. The policy is saved with the project.</div>
              </div>
              <div className="grid gap-2.5" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))' }}>
                {SCENARIOS.filter((s) => !!s.profile && !s.hero).map((s) => (
                  <ScenarioCard key={s.id} scenario={s} isPro={isPro} selected={activeProfile === s.profile}
                    unavailable={false} onPick={() => setScnId(s.id)} />
                ))}
              </div>
              </>)}
              <div className="mt-4 mb-2 border-t pt-3" style={{ borderColor: 'var(--sem-border)' }}>
                <div className="text-[11px] font-semibold">More ready-made workflows</div>
                <div className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>Fill in your organisation's details once. The questions become a reusable workflow for people and the AI Assistant.</div>
              </div>
              <div className="grid gap-2.5" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))' }}>
                {SCENARIOS.filter((s) => s.kind === 'template' && !s.hero).map((s) => (
                  <ScenarioCard key={s.id} scenario={s} isPro={isPro} selected={false}
                    unavailable={templates != null && !templateInstalled(s.template)} onPick={() => setScnId(s.id)} />
                ))}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ---- a scenario card ------------------------------------------------------------------------------
function ScenarioCard({ scenario, isPro, unavailable, selected, onPick }: {
  scenario: Scenario; isPro: boolean; unavailable: boolean; selected: boolean; onPick: () => void;
}) {
  const pro = scenarioProToApply(scenario);
  return (
    <button type="button" onClick={onPick} disabled={unavailable}
      title={unavailable ? `This scenario needs the "${scenario.template}" template, which is not installed.` : 'Set up this scenario'}
      className="text-left rounded-lg border p-3 flex flex-col gap-1.5 transition-colors disabled:opacity-50 disabled:cursor-default"
      style={{ background: 'var(--sem-surface-2)', borderColor: scenario.hero || scenario.isDefault ? 'color-mix(in srgb, var(--sem-accent) 48%, transparent)' : 'var(--sem-border)' }}>
      <div className="flex items-center gap-2">
        <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded"
          style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>{scenario.tag}</span>
        <div className="flex-1" />
        {scenario.isDefault && (
          <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full"
            style={{ color: 'var(--sem-accent)', background: 'var(--sem-accent-soft)' }} title="Ships selected. Nothing to set up.">Default</span>
        )}
        {selected && <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full" style={{ color: 'var(--sem-good)', background: 'color-mix(in srgb, var(--sem-good) 14%, transparent)' }}>Current</span>}
        {pro && (
          <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full"
            style={{ color: isPro ? 'var(--sem-accent)' : 'var(--sem-muted)', background: 'var(--sem-accent-soft)' }}
            title={PRO_APPLY_REASON}>Pro</span>
        )}
      </div>
      <div className="text-[13px] font-semibold" style={{ color: 'var(--sem-fg)' }}>{scenario.title}</div>
      <div className="text-[11.5px] leading-snug" style={{ color: 'var(--sem-muted)' }}>{scenario.blurb}</div>
      <div className="mt-1 text-[11px] font-semibold" style={{ color: unavailable ? 'var(--sem-muted)' : 'var(--sem-accent)' }}>
        {unavailable ? 'Not installed' : selected ? 'Review →' : scenario.profile ? 'Choose →' : 'Set up →'}
      </div>
    </button>
  );
}

// ---- the wizard (form → preview → applied) --------------------------------------------------------
function ScenarioWizard({ scenario, isPro, library, titleOf, onBack, onApplied }: {
  scenario: Scenario; isPro: boolean; library: WorkflowInfo[] | null;
  titleOf: (name: string) => string; onBack: () => void; onApplied: (freshLibrary?: WorkflowInfo[]) => void;
}) {
  // A template scenario starts on the fill form; a settings scenario has no form, so it opens on the preview.
  const [phase, setPhase] = useState<'form' | 'preview' | 'applied'>(scenario.kind === 'template' ? 'form' : 'preview');
  const [tmpl, setTmpl] = useState<WorkflowTemplate | null>(null);
  const [tmplErr, setTmplErr] = useState<string | null>(null);
  const [values, setValues] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [applyErr, setApplyErr] = useState<string | null>(null);
  const [appliedNote, setAppliedNote] = useState<string | null>(null);

  // Fetch the full template (slots + body) so the form and the rendered-title preview can render.
  useEffect(() => {
    if (scenario.kind !== 'template' || !scenario.template) return;
    let alive = true;
    rpc<WorkflowTemplate>('getWorkflowTemplate', scenario.template)
      .then((t) => { if (alive) { setTmpl(t); setTmplErr(t?.error ?? null); } })
      .catch((e) => { if (alive) setTmplErr(String((e as Error).message ?? e)); });
    return () => { alive = false; };
  }, [scenario]);

  const slots = tmpl?.slots ?? [];
  const requiredUnfilled = slots.filter((s) => s.required === 'required' && !(values[s.name] || '').trim());
  const canPreview = scenario.kind !== 'template' || (tmpl != null && requiredUnfilled.length === 0);

  // Plain-language preview lines: what THIS will do, in words an analyst reads without a spec.
  const previewLines = useMemo<PreviewLine[]>(() => {
    if (scenario.kind === 'template') {
      if (!tmpl) return [];
      const renderedTitle = renderTemplate(tmpl.title, values, slots);
      const steps = countSteps(tmpl.markdown);
      const lines: PreviewLine[] = [
        { tone: 'add', text: `Adds a new workflow: "${renderedTitle}"${steps ? ` (${steps} step${steps === 1 ? '' : 's'})` : ''}.` },
        { tone: 'note', text: tmpl.description },
      ];
      if (scenario.runNote) lines.push({ tone: 'note', text: scenario.runNote });
      lines.push({ tone: 'set', text: 'This adds a workflow to your list. It changes none of your existing settings.' });
      return lines;
    }
    // settings bundle
    const lines: PreviewLine[] = [];
    if (scenario.id === 'make-model-ai-ready') {
      lines.push({ tone: 'note', text: 'Opens the shipped AI-ready workflow, where the AI Assistant scans the model, fills grounding gaps, and rescans to show the grade moving.' });
      lines.push({ tone: 'set', text: 'Choosing this card changes no workflow policy or model setting.' });
      return lines;
    }
    for (const b of scenario.bindings ?? []) {
      const wf = titleOf(b.require[0]);
      lines.push({ tone: 'require', text: `Requires every ${OP_LABELS[b.op] ?? uiLabel(b.op)} to go through "${wf}"${b.mode === 'hard' ? ' (blocks until it passes)' : ' (warns, but lets you continue)'}.` });
    }
    if (scenario.recommend?.length) {
      lines.push({ tone: 'note', text: `Recommended before you hand over: run ${scenario.recommend.map((n) => `"${titleOf(n)}"`).join(' and ')}.` });
    }
    if (scenario.id === 'standard') {
      lines.push({ tone: 'set', text: 'Every workflow stays available on the menu.' });
      lines.push({ tone: 'set', text: 'No action is required to go through a workflow.' });
      lines.push({ tone: 'set', text: "Checks run at each workflow's own strictness." });
    }
    return lines;
  }, [scenario, tmpl, values, slots, titleOf]);

  const proToApply = scenarioProToApply(scenario);
  const applyBlocked = proToApply && !isPro;

  const apply = async () => {
    if (applyBlocked) return;
    setBusy(true); setApplyErr(null);
    try {
      if (scenario.kind === 'template' && tmpl) {
        // fill in defaults for blank optional slots, then instantiate (authoring — free).
        const payload: Record<string, string> = {};
        for (const s of slots) {
          const v = (values[s.name] || '').trim();
          if (v) payload[s.name] = v;
          else if (s.required === 'optional' && s.default) payload[s.name] = s.default;
        }
        const primary = slots.find((s) => s.required === 'required');
        const name = `${tmpl.name}-${slug(primary ? values[primary.name] || '' : '')}`.replace(/-+$/, '') || tmpl.name;
        const lib = await rpc<WorkflowInfo[]>('instantiateWorkflowTemplate', tmpl.name, name, JSON.stringify(payload), 'human');
        setAppliedNote(`Created the workflow "${renderTemplate(tmpl.title, values, slots)}". Find it in your Workflows list.`);
        onApplied(lib);
      } else if (scenario.profile) {
        // The engine owns the bundle and writes it atomically. Standard therefore clears every prior simple
        // requirement and visibility rule instead of leaving a half-reset policy behind.
        const result = await rpc<WorkflowProfileResult>('activateWorkflowProfile', scenario.profile, 'human');
        setAppliedNote(result.note ?? 'Workflow profile applied.');
        onApplied(result.workflows);
      } else {
        // A hero signpost is deliberately non-mutating. It leads to an existing dual-drive workflow without
        // manufacturing a profile write or claiming that the readiness work has already happened.
        setAppliedNote(scenario.appliedNote ?? 'Nothing changed. Open the workflow from your Workflows list to begin.');
        onApplied();
      }
      setPhase('applied');
    } catch (e) {
      // The engine's refusal IS the teaching content (Pro upsell / duplicate name / locked policy) — verbatim.
      setApplyErr(String((e as Error).message ?? e));
    } finally { setBusy(false); }
  };

  return (
    <Panel>
      <div className="flex items-center gap-2">
        <button onClick={onBack} className="text-[11px] font-medium" style={{ color: 'var(--sem-muted)' }}>← All workflows and profiles</button>
        <div className="flex-1" />
        <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded"
          style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>{scenario.tag}</span>
      </div>
      <div className="text-[15px] font-semibold mt-1">{scenario.title}</div>
      <div className="text-[12px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>{scenario.blurb}</div>

      {/* ---- FORM (template scenarios) ---- */}
      {phase === 'form' && (
        <div className="mt-3">
          {tmplErr ? (
            <Banner color="var(--sem-bad)">{tmplErr}</Banner>
          ) : !tmpl ? (
            <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading the questions…</div>
          ) : (
            <>
              <div className="text-[10px] uppercase tracking-wide font-semibold mb-2" style={{ color: 'var(--sem-muted)' }}>Fill in your details</div>
              <div className="flex flex-col gap-3">
                {slots.map((s) => (
                  <SlotField key={s.name} slot={s} value={values[s.name] ?? ''}
                    onChange={(v) => setValues((m) => ({ ...m, [s.name]: v }))} />
                ))}
              </div>
              <div className="mt-3 flex items-center gap-2">
                <Button primary disabled={!canPreview}
                  title={canPreview ? 'See what this will do' : 'Answer the required questions first.'}
                  onClick={() => setPhase('preview')}>Preview</Button>
                {requiredUnfilled.length > 0 && (
                  <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>
                    {requiredUnfilled.length} required question{requiredUnfilled.length === 1 ? '' : 's'} still to answer.
                  </span>
                )}
              </div>
            </>
          )}
        </div>
      )}

      {/* ---- PREVIEW ---- */}
      {phase === 'preview' && (
        <div className="mt-3">
          <div className="text-[10px] uppercase tracking-wide font-semibold mb-2" style={{ color: 'var(--sem-muted)' }}>What this will do</div>
          <div className="rounded-lg border p-3 flex flex-col gap-2" style={{ background: 'var(--sem-surface-2)', borderColor: 'var(--sem-border)' }}>
            {previewLines.length === 0 ? (
              <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Nothing to change.</div>
            ) : previewLines.map((l, i) => (
              <div key={i} className="flex items-start gap-2 text-[12px]" style={{ color: l.tone === 'note' ? 'var(--sem-muted)' : 'var(--sem-fg)' }}>
                <span className="shrink-0 mt-0.5 w-4 h-4 rounded-full flex items-center justify-center text-[9px] font-bold"
                  style={{ color: TONE_COLOR[l.tone], background: `color-mix(in srgb, ${TONE_COLOR[l.tone]} 16%, transparent)` }}>{TONE_GLYPH[l.tone]}</span>
                <span className="flex-1">{l.text}</span>
              </div>
            ))}
          </div>

          {applyBlocked && (
            <div className="mt-3 rounded-md px-3 py-2 text-[11.5px] flex items-center gap-2"
              style={{ color: 'var(--sem-fg)', background: 'var(--sem-accent-soft)', border: '1px solid color-mix(in srgb, var(--sem-accent) 34%, transparent)' }}>
              <span className="text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded-full shrink-0" style={{ color: 'var(--sem-accent)', background: 'var(--sem-surface)' }}>Pro</span>
              <span>{PRO_APPLY_REASON}</span>
            </div>
          )}

          {applyErr && <div className="mt-3"><Banner color="var(--sem-warn, #d7a54a)">{applyErr}</Banner></div>}

          <div className="mt-3 flex items-center gap-2">
            <Button primary disabled={busy || applyBlocked}
              title={applyBlocked ? PRO_APPLY_REASON : scenario.profile ? 'Apply this workflow profile' : scenario.kind === 'template' ? 'Add this ready-made workflow' : 'Continue to the existing workflow'}
              onClick={apply}>{busy ? 'Applying…' : applyBlocked ? 'Apply · Pro' : scenario.profile ? 'Apply profile' : scenario.kind === 'template' ? 'Add workflow' : 'Continue'}</Button>
            {scenario.kind === 'template' ? (
              <Button onClick={() => setPhase('form')}>Back to questions</Button>
            ) : (
              <Button onClick={onBack}>Cancel</Button>
            )}
          </div>
        </div>
      )}

      {/* ---- APPLIED ---- */}
      {phase === 'applied' && (
        <div className="mt-3">
          <div className="rounded-lg border p-3" style={{ background: 'color-mix(in srgb, var(--sem-good) 10%, transparent)', borderColor: 'color-mix(in srgb, var(--sem-good) 34%, transparent)' }}>
            <div className="text-[12.5px] font-semibold flex items-center gap-1.5" style={{ color: 'var(--sem-good)' }}>
              <span>✓</span> {appliedNote || 'Scenario applied.'}
            </div>
            <div className="text-[11.5px] mt-2" style={{ color: 'var(--sem-fg)' }}>
              <span className="font-semibold">To undo: </span>{scenario.undo}
            </div>
          </div>
          <div className="mt-3 flex items-center gap-2">
            <Button primary onClick={onBack}>Done</Button>
          </div>
        </div>
      )}
    </Panel>
  );
}

// ---- one slot field, rendered by its type with a plain label + help line (no engine vocabulary) -----
function SlotField({ slot, value, onChange }: { slot: SlotDef; value: string; onChange: (v: string) => void }) {
  const required = slot.required === 'required';
  const help = slot.hint || (slot.example ? `e.g. ${slot.example}` : '');
  const isEnum = slot.type === 'enum' && (slot.values?.length ?? 0) > 0;
  // A long, list-shaped answer (checklist, dictionary) wants a textarea; short facts get an input.
  const multiline = slot.type === 'text';
  return (
    <div className="rounded-lg p-2.5" style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)' }}>
      <div className="flex items-start gap-2">
        <label className="text-[12px] font-medium flex-1" style={{ color: 'var(--sem-fg)' }}>{slot.question || slot.name}</label>
        {required
          ? <span className="shrink-0 text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded" style={{ color: 'var(--sem-bad)', background: 'color-mix(in srgb, var(--sem-bad) 14%, transparent)' }}>required</span>
          : <span className="shrink-0 text-[9px] uppercase tracking-wide font-semibold px-1.5 py-0.5 rounded" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface)' }}>optional</span>}
      </div>
      {help && <div className="text-[10.5px] mt-1" style={{ color: 'var(--sem-muted)' }}>{help}</div>}
      {isEnum ? (
        <select value={value} onChange={(e) => onChange(e.target.value)} data-example={slot.example ?? ''}
          className="mt-1.5 w-full text-[12px] px-2 py-1 rounded-md outline-none"
          style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
          <option value="">Choose…</option>
          {slot.values!.map((v) => <option key={v} value={v}>{v}</option>)}
        </select>
      ) : multiline ? (
        <textarea value={value} onChange={(e) => onChange(e.target.value)} rows={2} spellCheck={false} data-example={slot.example ?? ''}
          className="mt-1.5 w-full text-[12px] px-2 py-1 rounded-md outline-none resize-y"
          style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
      ) : (
        <input value={value} onChange={(e) => onChange(e.target.value)} spellCheck={false} data-example={slot.example ?? ''}
          type={slot.type === 'number' ? 'number' : 'text'}
          className="mt-1.5 w-full text-[12px] px-2 py-1 rounded-md outline-none"
          style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
      )}
      {!required && slot.default && (
        <div className="text-[10px] mt-1" style={{ color: 'var(--sem-muted)' }}>Leave blank to use: {slot.default}</div>
      )}
    </div>
  );
}
