import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import { rpc, onPlanChange, loadState, saveState, postToHost, keptByTheExtension } from './bridge';
import { Banner, Panel, type WorkflowDef, type WorkflowInfo, type WorkflowPolicy, type WorkflowRunView } from './workflow-ui';
import { RowMenu } from './toolrow';
import { WorkflowCanvas } from './workflowcanvas';
import { canvasCounts } from './workflowboxes.mjs';
import { applyTextareaChange, type WorkflowUpgradeResult } from './workflowdocument.mjs';
import { WorkflowEmptySteps, WorkflowSettingsFields, WorkflowStepForm, TagMark } from './workflowforms';
import { LockMark, OfferedControl, RequiredForControl, SideDrawer, requiredWords } from './workflow-rules';
import { RepeatEditor } from './plan-repeat';
import type { PlanRepeat, RepeatRequest } from './plan-repeat-model';
import * as M from './workflows-model';
import {
  acceptWorkflowSave, applyWorkflowOperation, boxesFromEditModel, createWorkflowDraft, discardWorkflowDraft,
  editWorkflowSource, installWorkflowPreview, newTempKey, receiveWorkflowDocument, redoWorkflowDraft, setWorkflowPreview,
  structuredUnavailable, undoWorkflowDraft, uniqueStepId, workflowDraftDirty, workflowPreviewArgs,
  type WorkflowDefinitionDraft, type WorkflowDocument, type WorkflowEditModel, type WorkflowEditOperation, type WorkflowEditPreview,
} from './workflowdraft.mjs';
import { createHeldDrafts, draftShape, heldForFile, heldKey, heldStateFrom, worthHolding, type HeldEntry } from './workflowheld.mjs';
import { V3SettingsFields } from './workflow-v3fields';
import { isV3, runOnceKind } from './workflow-v3model';

// ===================================================================================================
// Author: a workflow, open (lane W2; contract 21.5 and 21.6, composer contract 10.5 as L7 folded into W2).
//
// Steps is the face. Canvas and Source sit behind the View switch in the menu (Kane's decision 5, 24 Sep: Steps
// is the face until the canvas can branch). The header is the title, the Required mark, Saved or Unsaved, Undo,
// Run, Save and one menu (Redo, Discard, Duplicate, Delete, Check, Check against past runs, View). Save is one
// click: the diff shows only when the file changed somewhere else or something needs a warning, and the conflict
// guard that has always protected the file stays exactly as it was. Under the title, one sentence built from
// the steps says who does what. The outline carries the two tags; a step with no tag is done by Semanticus.
// A workflow's own settings (notes for your assistant, inputs asked each run, its rules, Repeat on the Plan)
// live in its Settings drawer. A workflow in the oldest format carries one notice with its Upgrade.
// ===================================================================================================

/** How Author was opened: a new workflow's title in words (Start blank), a description's steps (Describe it in words,
 *  Kane's UAT on 25 Sep: describing is authoring, so it opens here and puts nothing on the Plan), straight into its
 *  Settings drawer (Edit rules from a team rule), or a kept new workflow to reopen (its held key, UAT-S02-j7-003). */
export interface AuthorSeed { title?: string; words?: string | null; settings?: boolean; steps?: SeedStep[] | null; note?: string | null; resume?: string;
  /** A composer draft written as a format-3 workflow (edit_draft open_in_author; lane author-gaps, B8): Author opens it
   *  as a new, unsaved workflow with exactly this text, under the free name the engine chose. */
  document?: { name: string; text: string } | null }

/** One step read off a description by the engine (describeWorkflowSteps). No op means the words named no step
 *  Semanticus knows: it is still a step, a named placeholder to fill in, never a broken row. `ops` are the actions the
 *  step may use (Jev's choice on Pro once its replay clears the bar, else the word match), `target` what the engine says
 *  it works on, and `reason` why a placeholder has no action, in plain words. Every string is the engine's. */
export interface SeedStep {
  title: string; op?: string | null; matched?: boolean;
  ops?: string[] | null; target?: string | null; reason?: string | null;
  performer?: string | null; chosenBy?: string | null;
}

/** What a placeholder step asks for, in its instructions field. */
export const PLACEHOLDER_STEP = 'Say what this step does, then choose what it may use.';

/** The add_step values for a description's steps, each id unique against the model as it grows. */
export function seedStepValues(model: WorkflowEditModel, steps: SeedStep[]) {
  const taken = { ...model, definition: { ...model.definition, steps: [...model.definition.steps] } } as WorkflowEditModel;
  return steps.map((s) => {
    const title = s.title.trim() || 'New step';
    const id = uniqueStepId(title, taken);
    (taken.definition.steps as unknown as { id: string }[]).push({ id });
    const ops = s.ops?.length ? s.ops : s.op ? [s.op] : [];
    // A format 2 step has no place for an action's arguments, and the assistant reads the step's body, so what the
    // engine says the step works on goes there.
    const instructions = !ops.length ? (s.reason || PLACEHOLDER_STEP) : s.target ? `Works on ${s.target}.` : '';
    return { id, title, instructions, ops };
  });
}

/** Unsaved Author work, kept when the person leaves Author (workflowheld.mjs; UAT-S02-j7-003). One store for the
 *  Studio webview, on its own state so it outlives the panel being hidden, and handed to the extension on every change
 *  so it outlives the Studio tab being closed too. The list reads it for "kept here". */
const HELD_STATE = 'workflowHeldDrafts';
type HeldState = Record<string, HeldEntry<WorkflowDefinitionDraft>>;
export const heldDrafts = createHeldDrafts<WorkflowDefinitionDraft>({
  load: () => heldStateFrom(loadState<HeldState | null>(HELD_STATE, null), keptByTheExtension<HeldState>()) as HeldState,
  save: (value) => { saveState(HELD_STATE, value); postToHost({ type: 'workflowHeld', value }); },
});
const KEPT_NOTICE = 'Your unsaved changes are back. Save them, or choose Discard changes.';

function newStepValue(model: WorkflowEditModel) {
  const title = 'New step';
  // A format-3 step says who does it; a new one is the assistant's until the author gives it an action (lane author-gaps, B7).
  return { id: uniqueStepId(title, model), title, instructions: '', ops: [], ...(isV3(model.definition.schemaVersion) ? { performer: 'assistant' } : {}) };
}

type View = 'steps' | 'canvas' | 'source';
interface WorkflowDocumentEditResult { changed: boolean; reason?: string | null; document: WorkflowDocument }
interface CheckFinding { severity: string; message: string }
interface CheckReport { parseError?: string | null; findings: CheckFinding[]; ok: boolean }
interface ReplayReport { parseError?: string | null; admissible: boolean; note?: string | null; rehearsed: number; rehearsedFailed: number; rows: { step: string; outcome: string; detail: string }[] }

// A menu item that did its work closes the menu, the way a menu does: the shared RowMenu closes on Escape. Delete…
// is the one item that keeps it open, because its confirmation appears in its place.
const closeMenuAfter = (event: React.MouseEvent) => {
  const hit = (event.target as HTMLElement).closest('button');
  if (!hit || hit.disabled || hit.getAttribute('data-testid') === 'wf-delete') return;
  setTimeout(() => document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' })), 0);
};
const errorMessage = (cause: unknown) => cause instanceof Error ? cause.message : String(cause);
const isDraftConflict = (message: string) => /changed on disk|model changed|resolves to a different file/i.test(message);
const DRAFT_WHY = 'Your changes are held here until you save. Saving checks whether the file changed since you opened it and shows you the difference instead of overwriting.';

function refusedPreview(draft: WorkflowDefinitionDraft, document: WorkflowDocument, reason: string): WorkflowEditPreview {
  return {
    name: draft.base.name, outcome: 'conflict', canApply: false, requiresReview: true, reason, document,
    proposedText: draft.sourceText, proposedByteHash: null, diff: '', editModel: draft.editModel,
    issues: [{ code: 'stale_draft', severity: 'error', target: 'workflow', relatedTargets: [], message: reason }], keyChanges: [],
  };
}

function emptyCreateDocument(name: string, preview: WorkflowEditPreview): WorkflowDocument {
  return {
    name, library: 'user', path: '', exactText: '', byteHash: '', editModel: preview.editModel ?? null,
    metadata: { schemaVersion: 2, title: name, version: 1, stepIds: [], explicitIds: true, parses: true },
  };
}

/** Does a preview carry anything a person should read before it is written? A conflict, or a warning. */
export function previewNeedsReading(preview: WorkflowEditPreview | null | undefined): boolean {
  if (!preview) return false;
  if (preview.outcome !== 'ready') return preview.outcome !== 'no_change';
  return (preview.issues ?? []).some((i) => i.severity === 'error' || i.severity === 'warning');
}

function PreviewPanel({ preview, busy, onApply, onClose, onReload, onReview, freeName = null, onSaveAs }: {
  preview: WorkflowEditPreview; busy: boolean;
  onApply: () => void; onClose: () => void; onReload: () => void; onReview: () => void;
  freeName?: string | null; onSaveAs?: () => void;
}) {
  // A new workflow whose file name was taken while its changes were kept (the assistant saved one of that name, say):
  // there is no saved file of its own to reload or read, so the way out is its own name, never only a drop (verifier on
  // m11/sol-fixes-1 round 2).
  const taken = !!freeName && !!onSaveAs && preview.outcome === 'conflict' && (preview.issues ?? []).some((i) => i.code === 'create_collision');
  const conflict = preview.outcome === 'conflict' && !taken;
  const lead = taken ? M.nameTakenLine(preview.name, freeName!)
    : conflict
    ? 'The file changed somewhere else since you opened it. Reload it to drop your changes, or read it and keep yours.'
    : preview.outcome === 'ready' ? 'Read this first. Only the lines below change.' : preview.reason;
  return <section className="sem-wf-preview" aria-label="Before you save" data-testid="wf-save-preview">
    <div className="sem-wf-preview-head"><div><b>Before you save</b><p>{lead}</p></div><div className="sem-wf-acts">
      {preview.canApply && <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={busy} onClick={onApply}>{busy ? 'Saving…' : 'Save anyway'}</button>}
      {taken && <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={busy} data-testid="wf-save-as-free" onClick={onSaveAs}>{`Save as ${freeName}`}</button>}
      {conflict && <><button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={onReload}>Reload saved file</button>
        <button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={onReview}>Read it and keep mine</button></>}
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" disabled={busy} onClick={onClose}>Close</button>
    </div></div>
    {(preview.issues ?? []).filter((issue) => !taken && (conflict || issue.message !== preview.reason || issue.target !== 'workflow')).map((issue, index) =>
      <Banner key={`${issue.code}:${index}`} color={issue.severity === 'error' ? 'var(--sem-bad)' : 'var(--sem-warn)'}>{issue.message}</Banner>)}
    {preview.diff && <pre className="sem-wf-diff" aria-label="Source changes">{preview.diff.split('\n').map((line, index) => <span key={index} className={line.startsWith('+') && !line.startsWith('+++') ? 'add' : line.startsWith('-') && !line.startsWith('---') ? 'del' : ''}>{index ? '\n' : ''}{line}</span>)}</pre>}
  </section>;
}

function SourceEditor({ draft, readOnly, busy, onChange }: { draft: WorkflowDefinitionDraft; readOnly: boolean; busy: boolean; onChange: (text: string) => void }) {
  const source = draft.sourceText.charCodeAt(0) === 0xfeff ? draft.sourceText.slice(1) : draft.sourceText;
  return <Panel>
    <div className="sem-wf-form-title"><div><b>Source</b><p>The file itself. Edits here are the same unsaved changes as Steps.</p></div><code>{draft.base.path || `${draft.base.name}.md`}</code></div>
    <textarea className="sem-wf-source" aria-label="Workflow source" value={source} readOnly={readOnly || busy} spellCheck={false}
      onChange={(event) => onChange(applyTextareaChange(draft.sourceText, event.target.value))} />
  </Panel>;
}

/** The older-format notice (contract 21.6): the only live door to upgrade_workflow, with its preview first. */
function UpgradeNotice({ name, document, onDone }: { name: string; document: WorkflowDocument; onDone: () => void }) {
  const [preview, setPreview] = useState<WorkflowUpgradeResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const look = async () => {
    setBusy(true); setError(null);
    try { setPreview(await rpc<WorkflowUpgradeResult>('upgradeWorkflow', name, true, document.byteHash, document.path, 'human')); }
    catch (e) { setError(errorMessage(e)); } finally { setBusy(false); }
  };
  const apply = async () => {
    if (!preview) return;
    setBusy(true); setError(null);
    try {
      const r = await rpc<WorkflowUpgradeResult>('upgradeWorkflow', name, false, preview.document.byteHash, preview.document.path, 'human');
      if (!r.changed) { setError(r.reason || r.parseError || 'The upgrade was not applied. Look at it again.'); return; }
      setPreview(null); onDone();
    } catch (e) { setError(errorMessage(e)); } finally { setBusy(false); }
  };
  return <div className="sem-wf-notice-line" data-testid="wf-upgrade-notice">
    <span>{M.UPGRADE_LINE.replace(/ Upgrade it$/, '')}</span>
    {!preview && <button type="button" className="sem-btn sem-btn-sm" disabled={busy} data-testid="wf-upgrade-look" onClick={() => void look()}>{busy ? 'Reading…' : 'Upgrade it'}</button>}
    {preview && <>
      <span className="sem-wf-note">{preview.canApply ? `${preview.addedLines === 1 ? '1 line changes' : `${preview.addedLines} lines change`}. Nothing else does.` : preview.reason}</span>
      {preview.canApply && <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={busy} data-testid="wf-upgrade-apply" onClick={() => void apply()}>Upgrade</button>}
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => setPreview(null)}>Not now</button>
      {preview.diff && <pre className="sem-wf-diff">{preview.diff}</pre>}
    </>}
    {error && <Banner color="var(--sem-bad)">{error}</Banner>}
  </div>;
}

function CheckResult({ check, replay, onClose }: { check: CheckReport | null; replay: ReplayReport | null; onClose: () => void }) {
  return <section className="sem-wf-checkres" data-testid="wf-check-result">
    <div className="sem-wf-kv"><b>{check ? 'Check this workflow' : 'Check against past runs'}</b>
      <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={onClose}>Close</button></div>
    {check && (check.parseError ? <Banner color="var(--sem-bad)">{check.parseError}</Banner>
      : check.findings.length === 0 ? <span className="sem-wf-note">It reads correctly, and every action it names is real.</span>
      : check.findings.map((f, i) => <span key={i} className="sem-wf-note">{f.severity === 'warn' ? 'Warning: ' : ''}{f.message}</span>))}
    {replay && <>
      {replay.parseError && <Banner color="var(--sem-bad)">{replay.parseError}</Banner>}
      {replay.note && <span className="sem-wf-note">{replay.note}</span>}
      {!replay.parseError && <span className="sem-wf-note">{replay.rehearsed === 1 ? '1 step rehearsed' : `${replay.rehearsed} steps rehearsed`}{replay.rehearsedFailed ? `, ${replay.rehearsedFailed} would fail` : ''}. Nothing in the model changed.</span>}
      {replay.rows.map((r, i) => <span key={i} className="sem-wf-note">{r.step}: {r.detail}</span>)}
    </>}
  </section>;
}

export function DesignMode({ info, def, creating, seed = null, library, libraryReady = true, policy, isPro, running = null, sessionId = null, onBack, onRun, onRunOnce, runError = null,
  onSaved, onDeleted, onSetEnabled, onSetActivation, onSetBinding, onOpenTeamRules, titleOf }: {
  info: WorkflowInfo | null; def: WorkflowDef | null; creating: boolean; seed?: AuthorSeed | null;
  library: WorkflowInfo[]; libraryReady?: boolean; policy: WorkflowPolicy | null; isPro: boolean; running?: WorkflowRunView | null; sessionId?: string | null;
  onBack: () => void; onRun: (name: string, def: WorkflowDef | null) => void; runError?: string | null;
  /** Run once (Plan-heart contract 21.5): the unsaved text as it stands, drafted or started as a guided run. */
  onRunOnce?: (name: string, text: string, kind: 'draft' | 'guided', title: string) => void;
  onSaved: (name: string, note?: string) => void; onDeleted: () => void;
  onSetEnabled: (on: boolean, clearRule?: boolean) => void; onSetActivation: (when: string, set: string) => void;
  onSetBinding: (op: string, require: string[], mode: string) => void; onOpenTeamRules: () => void;
  titleOf: (name: string) => string;
}) {
  const [draft, setDraft] = useState<WorkflowDefinitionDraft | null>(null);
  const current = useRef<WorkflowDefinitionDraft | null>(null);
  const [view, setView] = useState<View>('steps');
  const [selected, setSelected] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [reviewCurrent, setReviewCurrent] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [settings, setSettings] = useState(!!seed?.settings);
  const [check, setCheck] = useState<CheckReport | null>(null);
  const [replay, setReplay] = useState<ReplayReport | null>(null);
  const [layoutChanges, setLayoutChanges] = useState<WorkflowEditPreview['keyChanges']>([]);
  const [focusStepKey, setFocusStepKey] = useState<string | null>(null);
  // Canvas state (behind View). `pick` is null until a step is chosen, because on Canvas "nothing selected" is a
  // real state: the canvas has the whole page and there is no drawer. Steps keeps its own `selected`.
  const [pick, setPick] = useState<number | null>(null);
  const [pinned, setPinned] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);
  const [fitSignal, setFitSignal] = useState(0);
  const lastPick = useRef(0);
  const stage = useRef<HTMLDivElement | null>(null);
  const [repeats, setRepeats] = useState<PlanRepeat[] | null>(null);
  const request = useRef(0);
  const createdName = useRef<string | null>(null);

  // Keeping unsaved work: every install writes the draft to the held store, or drops it once nothing is unsaved.
  // `quiet` is true while a draft is being prepared or reopened, so those steps never overwrite what is kept.
  const quiet = useRef(false);
  const seedShape = useRef<string | null>(null);
  const viewRef = useRef<View>('steps');
  viewRef.current = view;
  const keep = (next: WorkflowDefinitionDraft) => {
    if (quiet.current) return;
    const isNew = creating && !info;
    const fileName = isNew ? createdName.current : info?.name;
    if (!fileName || (!isNew && next.base.library === 'stock')) return;
    const key = heldKey(fileName, isNew, isNew ? null : next.base.path);
    if (worthHolding(next, { creating: isNew, dirty: workflowDraftDirty(next), seedShape: seedShape.current })) {
      // In Source the structured title is not read until the text is checked, so the last title known stands.
      const title = next.editModel?.definition.title || heldDrafts.get(key)?.title || (isNew ? seed?.title : next.base.metadata?.title) || fileName;
      heldDrafts.hold(key, { name: fileName, creating: isNew, view: viewRef.current, draft: next, title });
    } else heldDrafts.drop(key);
  };
  const install = (next: WorkflowDefinitionDraft) => { current.current = next; setDraft(next); keep(next); };
  const name = info?.name ?? createdName.current;
  const load = async (keepDirty = true) => {
    if (!info?.name) return null;
    const ticket = ++request.current;
    try {
      const document = await rpc<WorkflowDocument>('getWorkflowDocument', info.name, sessionId);
      if (ticket !== request.current) return null;
      install(keepDirty ? receiveWorkflowDocument(current.current, document, sessionId) : createWorkflowDraft(document, sessionId));
      setError(null); return document;
    } catch (cause) { if (ticket === request.current) setError(errorMessage(cause)); return null; }
  };

  useEffect(() => {
    current.current = null; setDraft(null); setView('steps'); setSelected(0); setPick(null); setFocusStepKey(null);
    setError(null); setNotice(null); setCheck(null); setReplay(null); setConfirmDelete(false);
    const kept = seed?.resume ? heldDrafts.get(seed.resume) : null;
    if (creating && kept) resumeCreate(kept, !kept.creating);
    else if (!creating) void openSaved(kept);
    else void beginCreate(seed?.title || 'New workflow');
    return () => { ++request.current; };
  }, [creating, info?.name, libraryReady]); // eslint-disable-line react-hooks/exhaustive-deps

  // Reopen an existing workflow. Kept unsaved work comes back on top of the file as it is now, so the save guard
  // still sees any change made elsewhere. It is found by the file it was made on, which names its model: work kept for
  // a same-named workflow in another model stays kept, untouched, and says where it belongs when that is what was
  // opened (verifier on m11/sol-fixes-1 round 2). Quiet while the file loads, so the clean copy never drops it.
  async function openSaved(asked: HeldEntry<WorkflowDefinitionDraft> | null) {
    // A workflow this model no longer has (deleted while it was open, say) has no file to wait for: it says so, and
    // what was kept for it stays on the list, which opens it as a new workflow.
    if (!info?.name) { if (libraryReady) setError(M.WORKFLOW_GONE); return; }
    quiet.current = true;
    try {
      const document = await load(false);
      if (!document || !info?.name) return;
      const kept = heldForFile(heldDrafts, info.name, document.path);
      const elsewhere = !!asked && asked.draft.base.path !== document.path;
      if (!kept) {
        if (elsewhere) setNotice(M.keptElsewhere(asked.title || asked.name));
        else if (seed?.note) setNotice(seed.note);   // said by the save that opened it (a new workflow saved under a free name)
        return;
      }
      quiet.current = false;
      install(receiveWorkflowDocument({ ...kept.draft, sessionId }, document, sessionId));
      // Work an older Studio kept under the name alone now sits under its file (install kept it there).
      if (heldDrafts.get(heldKey(info.name, false))?.draft?.base?.path === document.path) heldDrafts.drop(heldKey(info.name, false));
      if (kept.view === 'source' || kept.view === 'canvas') setView(kept.view as View);
      // "Open it" on another model's line opens this model's own kept work when it has some, and says so: it used to
      // open it as if it were the one asked for (verifier on m11/sol-fixes-1 round 3).
      setNotice(elsewhere ? M.keptElsewhereOwnOpen(asked.title || asked.name) : KEPT_NOTICE);
    } finally { quiet.current = false; }
  }

  // Reopen a new workflow that was never saved, exactly as it was left. `gone` is kept work for a saved workflow this
  // model no longer has: it opens as a new workflow under a free file name, and is kept from now on as that new one.
  function resumeCreate(kept: HeldEntry<WorkflowDefinitionDraft>, gone = false) {
    let work = kept.draft;
    let fileName = kept.name;
    if (gone) {
      const taken = [...(library ?? []), ...heldDrafts.list().filter((h) => h.creating)];
      fileName = M.freeName(kept.name, taken);
      const base = { ...work.base, name: fileName, path: '', byteHash: '', exactText: '' };
      work = { ...work, base, latest: base, preview: null,
        sourceText: fileName === kept.name ? work.sourceText : M.withWorkflowName(work.sourceText, fileName) };
    }
    createdName.current = fileName;
    seedShape.current = null;
    install({ ...work, sessionId });
    if (gone && seed?.resume && seed.resume !== heldKey(fileName, true)) heldDrafts.drop(seed.resume);
    if (kept.view === 'source' || kept.view === 'canvas') setView(kept.view as View);
    // Its first step, when it has one: nothing selected draws "No steps yet", which beside a held draft's steps is false.
    setSelected(kept.draft.editModel?.definition.steps.length ? 0 : -1);
    setNotice(gone ? M.keptForGone(kept.title || kept.name) : KEPT_NOTICE);
  }

  // The saved repeats, so "Repeat on the Plan" shows this workflow's own (lane RP's control, contract 21.5).
  const readRepeats = () => rpc<{ repeats?: PlanRepeat[] }>('getModelPlan').then((p) => setRepeats(p?.repeats ?? [])).catch(() => setRepeats([]));
  // true only when the engine took the repeat: the form ends its cadence on that answer (R-B).
  const repeatWrite = async (write: () => Promise<unknown>): Promise<boolean> => {
    try { await write(); } catch (e) { setError(errorMessage(e)); return false; }
    void readRepeats();
    return true;
  };
  useEffect(() => { if (!settings) return; void readRepeats(); return onPlanChange(() => { void readRepeats(); }); }, [settings]);  // eslint-disable-line react-hooks/exhaustive-deps

  // Start blank asks a title in words and derives the file name from it (contract 21.1 row 55).
  async function beginCreate(title: string) {
    // Free among the saved workflows AND the new ones whose unsaved work is kept, which are kept by file name: a second
    // new workflow with the same title must not land on the first one's key (verifier on m11/sol-fixes-1, D2).
    const taken = [...(library ?? []), ...heldDrafts.list().filter((h) => h.creating)];
    const derived = seed?.document ? M.freeName(seed.document.name, taken) : M.nameFromTitle(title, taken) ?? M.nameFromTitle('new workflow', taken)!;
    setBusy(true); setError(null);
    quiet.current = true;
    try {
      const preview = await rpc<WorkflowEditPreview>('previewWorkflowEdit', derived, null, null, '[]', null, true, sessionId);
      if (!preview.editModel) { setError(preview.reason || 'The new workflow could not be prepared.'); return; }
      const next = createWorkflowDraft(emptyCreateDocument(derived, preview), sessionId);
      next.sourceText = preview.proposedText ?? '';
      next.editModel = preview.editModel;
      install(next);
      createdName.current = derived;
      if (seed?.document) {
        // The draft's own file: its text as the source, then read back through the engine so Steps shows it.
        install(editWorkflowSource(current.current!, derived === seed.document.name ? seed.document.text : M.withWorkflowName(seed.document.text, derived)));
        const read = await rpc<WorkflowEditPreview>('previewWorkflowEdit', ...workflowPreviewArgs(current.current!, true));
        if (read.editModel) install(setWorkflowPreview(installWorkflowPreview(current.current!, read, false), null));
        else setError(read.reason || 'The draft could not be opened as a workflow.');
        if (seed.note) setNotice(seed.note);
        setSelected(0);
        seedShape.current = null;   // the draft's steps are the person's work: leaving Author keeps them
        return;
      }
      const def0 = preview.editModel.definition;
      if (title && def0.title !== title) install(applyWorkflowOperation(next, { op: 'set_field', target: def0.key, field: 'title', expect: def0.title, value: title }, false));
      if (seed?.words) { const d1 = current.current!.editModel!.definition;
        install(applyWorkflowOperation(current.current!, { op: 'set_field', target: d1.key, field: 'description', expect: d1.description, value: seed.words }, false)); }
      if (seed?.steps?.length) {
        let after: string | null = null;
        for (const value of seedStepValues(current.current!.editModel!, seed.steps)) {
          const key = newTempKey();
          install(applyWorkflowOperation(current.current!, { op: 'add_step', after, tempKey: key, value }, true));
          after = key;
        }
        if (seed.note) setNotice(seed.note);
        setSelected(0);
        seedShape.current = null;   // described steps are the person's work: leaving Author keeps them
        return;
      }
      setSelected(-1);
      seedShape.current = draftShape(current.current);   // what Author prepared; only a change to it is kept
    } catch (cause) { setError(errorMessage(cause)); }
    finally {
      // Every step above ran quiet, so what it built is kept once, now: a described draft is the person's words and
      // is kept even untouched, while an untouched blank matches its seed shape and stays out (Sol, finding 1).
      quiet.current = false;
      if (current.current) keep(current.current);
      setBusy(false);
    }
  }

  // false when the edit was refused (its target is gone after a reload): a box that sent words keeps them (R-B).
  const mutate = (operation: WorkflowEditOperation, structural = false): boolean => {
    if (!current.current) return false;
    try { install(applyWorkflowOperation(current.current, operation, structural)); setError(null); setNotice(null); return true; }
    catch (cause) { setError(errorMessage(cause)); return false; }
  };

  const preview = async (subject = current.current) => {
    if (!subject) return null;
    const revision = JSON.stringify([subject.base.path, subject.base.byteHash, subject.sourceText, subject.operations]);
    const result = await rpc<WorkflowEditPreview>('previewWorkflowEdit', ...workflowPreviewArgs(subject, creating));
    const live = current.current;
    if (!live || JSON.stringify([live.base.path, live.base.byteHash, live.sourceText, live.operations]) !== revision)
      throw new Error('The workflow changed while it was being checked. Save again.');
    const next = setWorkflowPreview(live, result);
    install(result.document && result.outcome === 'conflict' ? { ...next, latest: result.document } : next);
    return result;
  };

  const goView = async (next: View) => {
    const held = current.current;
    if (!held || next === view) return;
    setBusy(true); setError(null); setNotice(null);
    try {
      if (next === 'source') {
        if (held.operations.length) {
          const result = await preview(held);
          if (result) {
            const pending = current.current ?? held;
            if (result.outcome === 'ready' || result.outcome === 'no_change') install(setWorkflowPreview(installWorkflowPreview(pending, result, false), null));
            else { install(setWorkflowPreview(pending, null)); setError(result.reason || 'Your changes could not be shown as source. They are still held.'); }
          }
        }
        setView('source'); return;
      }
      if (view === 'source' || !held.editModel) {
        const result = await preview(held);
        if (!result?.editModel || structuredUnavailable(result.editModel)) {
          if (current.current) install(setWorkflowPreview(current.current, null));
          setError(result?.reason || structuredUnavailable(result?.editModel ?? null)); return;
        }
        install(setWorkflowPreview(installWorkflowPreview(current.current ?? held, result, false), null));
      }
      setView(next);
    } catch (cause) {
      const message = errorMessage(cause);
      setError(message);
      if (isDraftConflict(message)) {
        const document = await load(true);
        const live = current.current;
        if (document && live) install({ ...live, latest: document, preview: refusedPreview(live, document, message) });
      }
    } finally { setBusy(false); }
  };

  // ONE CLICK. The preview is asked for, and when it carries nothing to read (no conflict, no warning) the save
  // goes straight through; otherwise the preview is shown and nothing is written until "Save anyway".
  const save = async () => {
    if (!current.current) return;
    setBusy(true); setError(null); setNotice(null); setReviewCurrent(false);
    try {
      const result = await preview(current.current);
      if (result?.outcome === 'no_change') { setNotice('Nothing to save.'); install(setWorkflowPreview(current.current!, null)); return; }
      if (result && !previewNeedsReading(result)) await write();
    } catch (cause) {
      const message = errorMessage(cause);
      setError(message);
      if (isDraftConflict(message)) {
        const document = await load(true);
        const live = current.current;
        if (document && live) install({ ...live, latest: document, preview: refusedPreview(live, document, message) });
      }
    } finally { setBusy(false); }
  };

  // Run once (Plan-heart contract 21.5; lane author-gaps, B6): the text as it stands here, checked by the engine the way
  // Save checks it, then drafted (a step Semanticus runs) or started as a guided run. Nothing is saved.
  const runOnce = async () => {
    const held = current.current;
    if (!held || !name || !onRunOnce) return;
    setBusy(true); setError(null); setNotice(null);
    try {
      const result = held.operations.length ? await preview(held) : null;
      const text = result ? result.proposedText : held.sourceText;
      const model = result?.editModel ?? held.editModel;
      if (!text) { setError(result?.reason || 'This workflow does not run as it stands.'); return; }
      onRunOnce(name, text, runOnceKind(model?.definition),
        model?.definition.title || workflowTitle);
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  };

  const write = async (saved?: string) => {
    const held = current.current;
    const ready = held?.preview;
    if (!held || !ready?.canApply || !ready.proposedText) return;
    try {
      let document: WorkflowDocument;
      if (creating && !info) {
        await rpc('saveWorkflow', held.base.name, ready.proposedText, 'human', true, held.sessionId);
        document = await rpc<WorkflowDocument>('getWorkflowDocument', held.base.name, held.sessionId);
      } else {
        const result = await rpc<WorkflowDocumentEditResult>('editWorkflowDocument', held.base.name, held.base.byteHash, ready.proposedText, held.base.path, 'human', held.sessionId);
        if (!result.changed) {
          install({ ...held, latest: result.document, preview: refusedPreview(held, result.document, result.reason || 'The saved file changed. Your changes are kept.') });
          return;
        }
        document = result.document;
      }
      setLayoutChanges(ready.keyChanges ?? []);
      install(acceptWorkflowSave(held, document));
      setNotice(running ? 'Saved for the next run. The run going now keeps the version it started with.' : saved ?? 'Saved.');
      onSaved(held.base.name, saved);
    } catch (cause) {
      const detail = errorMessage(cause);
      const conflict = isDraftConflict(detail);
      setError(conflict ? `Your changes are kept. ${detail}` : detail);
      const document = await load(true);
      const live = current.current;
      if (conflict && document && live) install({ ...live, latest: document, preview: refusedPreview(live, document, detail) });
    }
  };
  const saveAnyway = async () => { setBusy(true); try { await write(); } finally { setBusy(false); } };

  // A new workflow's file name, taken while its changes were kept: the next name free of every workflow, kept one, and
  // name already refused, then the same save under it. Only the file name changes; the title the person gave stays.
  const refusedNames = useRef(new Set<string>());
  const freeFileName = (held: WorkflowDefinitionDraft | null) => {
    if (!held || !creating || info) return null;
    const taken = [...(library ?? []), ...heldDrafts.list().filter((h) => h.creating && h.name !== held.base.name),
      ...[held.base.name, ...refusedNames.current].map((name) => ({ name }))];
    return M.freeName(held.base.name, taken);
  };
  const saveAsFree = async () => {
    const held = current.current;
    const fresh = freeFileName(held);
    if (!held || !fresh) return;
    const old = held.base.name;
    setBusy(true); setError(null); setNotice(null);
    try {
      const renamed: WorkflowDefinitionDraft = { ...held, base: { ...held.base, name: fresh }, sourceText: M.withWorkflowName(held.sourceText, fresh), preview: null };
      const result = await rpc<WorkflowEditPreview>('previewWorkflowEdit', ...workflowPreviewArgs(renamed, true));
      // Refused too (that name went meanwhile): shown again with the next free one; the work is kept under its old key.
      if (result.outcome === 'conflict') { refusedNames.current.add(fresh); install(setWorkflowPreview(held, result)); return; }
      heldDrafts.drop(heldKey(old, true));
      createdName.current = fresh;
      install(setWorkflowPreview(renamed, result));
      if (!previewNeedsReading(result)) await write(M.savedUnderFreeName(fresh, old));
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  };

  const discard = async () => {
    setBusy(true); setError(null); setNotice(null); setReviewCurrent(false);
    if (creating && !info) {
      if (createdName.current) heldDrafts.drop(heldKey(createdName.current, true));
      setDraft(null); current.current = null; await beginCreate(seed?.title || 'New workflow');
    }
    else { const document = await load(false); if (document && current.current) install(discardWorkflowDraft(current.current, document)); }
    setBusy(false);
  };

  // Use a starter from inside Author (a shipped workflow opened to read): the same one op as the list's Use.
  const useIt = async () => {
    if (!info) return;
    setBusy(true); setError(null);
    try {
      const made = await rpc<WorkflowInfo[]>('instantiateWorkflowTemplate', info.name, M.copyName(info.name, library as M.WfInfo[]), null, 'human');
      onSaved(made?.[0]?.name ?? info.name);
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  };
  const duplicate = async () => {
    if (!draft) return;
    const copy = M.nameFromTitle(`${workflowTitle} copy`, library as M.WfInfo[]);
    if (!copy) return;
    setBusy(true); setError(null);
    try {
      const text = M.reusableCopy(draft.base.exactText, copy, `${workflowTitle} copy`, null);
      await rpc('saveWorkflow', copy, text, 'human', true, draft.sessionId);
      onSaved(copy);
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  };
  const runCheck = async (replayIt: boolean) => {
    if (!name) return;
    setBusy(true); setError(null); setCheck(null); setReplay(null);
    try {
      if (replayIt) setReplay(await rpc<ReplayReport>('replayCheckWorkflow', name));
      else setCheck(await rpc<CheckReport>('checkWorkflow', name));
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  };

  const reloadSaved = async () => { setReviewCurrent(false); await load(false); };
  const rebaseReviewed = async () => {
    const held = current.current;
    if (!held) return;
    setBusy(true); setError(null);
    try {
      if (held.operations.length) {
        const rebased = { ...held, base: held.latest, sourceText: held.latest.exactText, editModel: held.latest.editModel ?? null, preview: null };
        const result = await rpc<WorkflowEditPreview>('previewWorkflowEdit', ...workflowPreviewArgs(rebased, false));
        if (!result.editModel || result.outcome !== 'ready') { setError(result.reason || 'Your changes no longer fit the current file.'); return; }
        install(setWorkflowPreview(installWorkflowPreview(rebased, result, false), null));
      } else install({ ...held, base: held.latest, preview: null });
      setReviewCurrent(false); setNotice('You read the newer file. Your changes are still unsaved.');
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  };

  const travelDraft = async (direction: 'undo' | 'redo') => {
    const held = current.current;
    if (!held) return;
    const next = direction === 'undo' ? undoWorkflowDraft(held) : redoWorkflowDraft(held);
    if (next === held) return;
    setBusy(true); setError(null); setNotice(null);
    try {
      const needsSource = view === 'source' && next.operations.length > 0;
      const needsStructured = view !== 'source' && !next.editModel;
      if (!needsSource && !needsStructured) { install(next); return; }
      const result = await rpc<WorkflowEditPreview>('previewWorkflowEdit', ...workflowPreviewArgs(next, creating));
      if ((needsSource && result.proposedText == null) || (needsStructured && (!result.editModel || structuredUnavailable(result.editModel)))) {
        setError(result.reason || 'That point cannot open in this view. Your changes are unchanged.'); return;
      }
      install({ ...setWorkflowPreview(installWorkflowPreview(next, result, false), null), undo: next.undo, redo: next.redo });
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  };

  const del = async () => {
    if (!info) return;
    setBusy(true);
    try {
      await rpc('deleteWorkflow', info.name, 'human');
      const path = current.current?.base.path;
      if (path) heldDrafts.drop(heldKey(info.name, false, path));
      if (heldDrafts.get(heldKey(info.name, false))?.draft?.base?.path === path) heldDrafts.drop(heldKey(info.name, false));
      onDeleted();
    }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  };

  const boxes = useMemo(() => boxesFromEditModel(draft?.editModel ?? null,
    (n) => library.find((workflow) => workflow.name === n)?.title || n).map((box) => {
      const d = (def?.steps ?? []).find((s) => s.id === box.id) as unknown as M.WfStep | undefined;
      return d ? { ...box, tags: M.tagsOf(d) } : box;
    }), [draft?.editModel, library, def]);
  // ---- the canvas drawer, decided once (read before the early returns, so every hook runs on every render) ----
  const stepList = draft?.editModel?.definition.steps ?? [];
  if (pick != null) lastPick.current = pick;
  const wanted = pick != null ? pick : (pinned ? lastPick.current : null);
  const drawerIndex = wanted == null || stepList.length === 0 ? null : Math.min(Math.max(0, wanted), stepList.length - 1);
  const drawerOpen = view === 'canvas' && drawerIndex != null;
  // Full screen is a class on the document body: the parts it hides (the Studio header, the area row, this page's
  // own head) belong to other components, and the stylesheet reaches all of them from one attribute.
  useEffect(() => {
    if (!fullscreen) return;
    document.body.setAttribute('data-studio-fullscreen', 'workflow-canvas');
    return () => document.body.removeAttribute('data-studio-fullscreen');
  }, [fullscreen]);
  useEffect(() => { if (view !== 'canvas') setFullscreen(false); }, [view]);
  useEffect(() => { if (current.current) keep(current.current); }, [view]);   // eslint-disable-line react-hooks/exhaustive-deps
  // The canvas is a different width once the drawer is beside it, so the steps are refitted into the space it has.
  useEffect(() => { setFitSignal((n) => n + 1); }, [drawerOpen, fullscreen]);
  useEffect(() => {
    if (view !== 'canvas') return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented) return;
      if (document.querySelector('.sem-rowmenu-pop, [role="menu"], [role="dialog"]')) return;
      if (fullscreen) { event.preventDefault(); setFullscreen(false); return; }
      if (!drawerOpen) return;
      // Anywhere on this page counts: after clicking a step the focus is on the canvas, and on a starter every
      // control in the drawer is disabled, so requiring focus inside the drawer left Escape doing nothing.
      const target = event.target as Node | null;
      const here = !target || target === document.body || (!!stage.current && stage.current.contains(target));
      if (!here) return;
      event.preventDefault(); setPick(null); setPinned(false);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [view, fullscreen, drawerOpen]);

  if (!draft) return <div className="sem-wf-author" data-testid="wf-author">
    <div className="sem-wf-author-top"><button type="button" className="sem-wf-back" onClick={onBack}>Workflows</button></div>
    {error ? <Banner color="var(--sem-bad)">{error}</Banner> : <span className="sem-wf-note" role="status">{creating ? 'Preparing a new workflow…' : 'Opening the workflow…'}</span>}
  </div>;

  const stock = !creating && draft.base.library === 'stock';
  const mine = !stock;
  const unavailable = structuredUnavailable(draft.editModel);
  const dirty = (creating && !info) || workflowDraftDirty(draft);
  const model = draft.editModel;
  const steps = model?.definition.steps ?? [];
  const selectedIndex = Math.min(selected, Math.max(0, steps.length - 1));
  const step = steps[selectedIndex];
  const callable = library.filter((workflow) => workflow.name !== draft.base.name && !workflow.error);
  const workflowTitle = model?.definition.title || draft.base.metadata.title || draft.base.name;
  const readOnlyNote = stock ? (info?.required ? 'A team rule requires this workflow, so it is used as it is.' : 'A starter. Use it to make a copy you can change.') : null;
  const canAdd = !stock && !busy && !!model && !unavailable;
  const required = info ? requiredWords(info.name, policy) ?? (info.required ? 'Required by a team rule' : null) : null;
  // A workflow not saved yet has no definition to read, so its sentence is read off the steps held here: a described
  // workflow opens with its steps, and must not say "No steps yet" over them.
  // A format-3 workflow is summed up from the steps held here, so the sentence follows unsaved edits (who does a step,
  // its questions, parts and checks) and a new workflow opened from a draft reads right before it is saved.
  const summary = M.summaryOf((model && isV3(model.definition.schemaVersion)
    ? { name: draft.base.name, title: workflowTitle, inputs: model.definition.inputs ?? [], steps }
    : def ?? { name: draft.base.name, title: workflowTitle, steps: steps.map((s) => ({ id: s.id, title: s.title })) }) as unknown as M.WfDef, policy?.enforcement);
  const defStep = (id: string) => (def?.steps ?? []).find((s) => s.id === id) as unknown as M.WfStep | undefined;
  const afterAt = steps.findIndex((s) => defStep(s.id)?.phase === 'after');
  const choose = (index: number) => { setPick(index); setSelected(index); };
  const addStepAfter = (after: string | null, index: number) => {
    const live = current.current?.editModel;
    if (!live) return;
    const key = newTempKey();
    mutate({ op: 'add_step', after, tempKey: key, value: newStepValue(live) }, true);
    choose(index); setFocusStepKey(key);
  };
  const structure = (operation: WorkflowEditOperation) => mutate(operation, true);
  const moveStep = (index: number, direction: -1 | 1) => {
    const item = steps[index];
    if (!item) return;
    const after = direction < 0 ? (index > 1 ? steps[index - 2].key : null) : steps[index + 1].key;
    structure({ op: 'move_step', target: item.key, after, expectOrder: steps.map((entry) => entry.key) });
    choose(index + direction);
  };
  const copyStep = (index: number) => {
    const item = steps[index];
    if (!item || !model) return;
    const key = newTempKey();
    structure({ op: 'copy_step', target: item.key, after: item.key, tempKey: key, newId: uniqueStepId(`${item.title} copy`, model) });
    choose(index + 1); setFocusStepKey(key);
  };
  const removeStep = (index: number) => {
    const item = steps[index];
    if (!item) return;
    structure({ op: 'remove_step', target: item.key, expectFingerprint: item.fingerprint });
    choose(Math.max(0, index - 1));
  };
  const drawerStep = drawerIndex == null ? null : steps[drawerIndex];
  const addFirstStep = () => addStepAfter(null, 0);
  const addStepAtEnd = () => addStepAfter(steps.length ? steps[steps.length - 1].key : null, steps.length);
  const counts = canvasCounts(boxes);
  const savedDoc = draft.base;
  const repeatRow = { id: `workflow:${draft.base.name}`, title: workflowTitle, source: 'workflow-def', doer: 'you', stateWord: 'ready' };

  return <div className="sem-wf-author sem-wf-editor" data-testid="wf-author" data-wf-view={view} data-dirty={dirty ? 'yes' : 'no'}>
    <div className="sem-wf-author-top">
      <button type="button" className="sem-wf-back" data-testid="wf-back" onClick={onBack}>Workflows</button>
    </div>
    <div className="sem-wf-ahead">
      <h1 className="sem-page-title sem-wf-atitle" title={`${workflowTitle} · ${draft.base.name}`}>{workflowTitle}</h1>
      {required && <span className="sem-wf-req" data-testid="wf-required-mark"><LockMark />{required}</span>}
      {mine && <span className={dirty ? 'sem-wf-saved sem-wf-saved-dirty' : 'sem-wf-saved'} data-testid="wf-saved" title={dirty ? DRAFT_WHY : 'Nothing is waiting to be saved.'}>
        {dirty ? 'Unsaved' : 'Saved'}</span>}
      {stock && <span className="sem-wf-note">{readOnlyNote}</span>}
      {view !== 'steps' && <span className="sem-wf-note">{view === 'canvas' ? 'Canvas view' : 'Source view'} · <button type="button" className="sem-wf-quiet-link" onClick={() => void goView('steps')}>Back to steps</button></span>}
      <span className="sem-wf-author-acts">
        {mine && <button type="button" className="sem-btn sem-btn-sm sem-wf-icon" title="Undo your last change here" aria-label="Undo"
          disabled={busy || !draft.undo.length} data-testid="wf-undo" onClick={() => void travelDraft('undo')}>
          <svg viewBox="0 0 12 12" aria-hidden="true"><path d="M4.5 3L2 5.5 4.5 8M2.3 5.5H7a3 3 0 0 1 0 6H5" fill="none" stroke="currentColor" strokeWidth="1.2" /></svg></button>}
        {dirty
          ? <button type="button" className="sem-btn sem-btn-sm" data-testid="wf-author-run" disabled={!isPro || busy || !name || !onRunOnce || info?.enabled === false}
              title="Runs what is here now, not saved. It runs as a row on the Plan, and saving stays your choice."
              onClick={() => void runOnce()}>Run once</button>
          : <button type="button" className="sem-btn sem-btn-sm" data-testid="wf-author-run" disabled={!isPro || busy || !name || !info || info.enabled === false}
              title="Runs as a row on the Plan" onClick={() => name && onRun(name, def)}>Run</button>}
        {mine && <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" data-testid="wf-save" disabled={busy || !dirty} onClick={() => void save()}>{busy ? 'Saving…' : 'Save'}</button>}
        {stock && !info?.required && <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" data-testid="wf-author-use" disabled={busy || !isPro} onClick={() => void useIt()}>Use</button>}
        <button type="button" className="sem-btn sem-btn-sm" data-testid="wf-settings-open" onClick={() => setSettings(true)}>Settings</button>
        <RowMenu label="More" title="Redo, discard, duplicate, delete, check and the view"><span className="sem-wf-menu-items" onClickCapture={closeMenuAfter}>
          {mine && <button type="button" className="sem-btn sem-btn-sm" disabled={busy || !draft.redo.length} onClick={() => void travelDraft('redo')}>Redo</button>}
          {mine && <button type="button" className="sem-btn sem-btn-sm" disabled={busy || !dirty} onClick={() => void discard()}>Discard changes</button>}
          <button type="button" className="sem-btn sem-btn-sm" disabled={busy || !isPro || creating} onClick={() => void duplicate()}>Duplicate</button>
          <button type="button" className="sem-btn sem-btn-sm" disabled={busy || !name || creating} data-testid="wf-check" onClick={() => void runCheck(false)}>Check this workflow</button>
          <button type="button" className="sem-btn sem-btn-sm" disabled={busy || !name || creating} data-testid="wf-replay" onClick={() => void runCheck(true)}>Check against past runs</button>
          <span className="sem-wf-menu-view"><span className="sem-wf-note">View</span>
            <span className="sem-seg" role="group" aria-label="View">
              {(['steps', 'canvas', 'source'] as const).map((v) => <button key={v} type="button" className="sem-seg-item" aria-pressed={view === v}
                data-testid={`wf-view-${v}`} disabled={busy || (v !== 'source' && !!unavailable)} onClick={() => void goView(v)}>{v[0].toUpperCase() + v.slice(1)}</button>)}
            </span></span>
          {mine && !creating && info && (confirmDelete
            ? <button type="button" className="sem-btn sem-btn-sm" disabled={busy} data-testid="wf-delete-confirm" onClick={() => void del()}>Delete it, for good</button>
            : <button type="button" className="sem-btn sem-btn-sm" disabled={busy} data-testid="wf-delete" onClick={() => setConfirmDelete(true)}>Delete…</button>)}
        </span></RowMenu>
      </span>
    </div>
    <p className="sem-wf-summary" data-testid="wf-summary">{summary.map((s, i) => s.hue
      ? <span key={i} className={`sem-wf-hue sem-wf-hue-${s.hue}`}>{s.text}</span> : <Fragment key={i}>{s.text}</Fragment>)}</p>

    {info && M.canUpgrade(def as unknown as M.WfDef, mine) && <UpgradeNotice name={info.name} document={savedDoc} onDone={() => { void load(false); onSaved(info.name); }} />}
    {info?.enabled === false && <div className="sem-wf-notice-line">Turned off. It is not offered and cannot run until you turn it on in Settings.</div>}
    {info?.enabled !== false && info?.active === false && <div className="sem-wf-notice-line">Hidden today: {info.activeReason}. It can still run.</div>}
    {(error || notice || runError || (unavailable && view !== 'source')) && <div className="sem-wf-author-notices">
      {error && <Banner color="var(--sem-bad)">{error}</Banner>}
      {runError && <Banner color="var(--sem-warn)">{runError}</Banner>}
      {notice && <div role="status" className="sem-wf-notice">{notice}</div>}
      {unavailable && view !== 'source' && <Banner color="var(--sem-warn)">{unavailable} Open Source from the menu to change the file.</Banner>}
    </div>}
    {(check || replay) && <CheckResult check={check} replay={replay} onClose={() => { setCheck(null); setReplay(null); }} />}

    {draft.preview && previewNeedsReading(draft.preview) && <PreviewPanel preview={draft.preview} busy={busy} onApply={() => void saveAnyway()}
      onClose={() => install(setWorkflowPreview(draft, null))} onReload={() => void reloadSaved()} onReview={() => setReviewCurrent(true)}
      freeName={creating && !info ? freeFileName(draft) : null}
      onSaveAs={() => void saveAsFree()} />}
    {reviewCurrent && <Panel><div className="sem-wf-form-title"><div><b>The newer file</b><p>Keep mine checks your changes against it.</p></div>
      <button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={() => void rebaseReviewed()}>Keep mine</button></div>
      <pre className="sem-wf-current-source">{draft.latest.exactText}</pre></Panel>}

    {view === 'source' && <SourceEditor draft={draft} readOnly={stock} busy={busy}
      onChange={(text) => { install(editWorkflowSource(draft, text)); setNotice(null); setError(null); }} />}

    {view === 'steps' && model && !unavailable && <div className="sem-wf-ab">
      <nav className="sem-wf-ol" aria-label="Steps" data-testid="wf-outline">
        <button type="button" className="sem-wf-ol-i sem-wf-ol-set" onClick={() => setSettings(true)}><span /><span>Settings</span><span /></button>
        {steps.map((item, index) => { const d = isV3(model.definition.schemaVersion) ? item as unknown as M.WfStep : defStep(item.id); const tags = d ? M.tagsOf(d) : [];
          return <Fragment key={item.key}>
            {index === afterAt && <div className="sem-wf-ol-div">After the change</div>}
            <button type="button" className={index === selectedIndex && selected >= 0 ? 'sem-wf-ol-i sem-wf-ol-on' : 'sem-wf-ol-i'}
              aria-current={index === selectedIndex && selected >= 0 ? 'step' : undefined} data-testid={`wf-ol-${item.id}`} onClick={() => setSelected(index)}>
              <span className="sem-num">{index + 1}</span><span className="sem-wf-ol-t">{item.title || 'Untitled'}</span>
              <span className="sem-wf-ol-tags">{tags.map((t) => <TagMark key={t} tag={t} />)}</span>
            </button>
          </Fragment>; })}
        {steps.length === 0
          ? <button type="button" className="sem-wf-ol-add" disabled={!canAdd} onClick={addFirstStep}>+ Add the first step</button>
          : <button type="button" className="sem-wf-ol-add" disabled={!canAdd} data-testid="wf-add-step" onClick={addStepAtEnd}>+ Add step</button>}
      </nav>
      <div className="min-w-0"><fieldset disabled={stock || busy} className="sem-wf-form-lock">
        {selected < 0 || !step
          ? <WorkflowEmptySteps disabled={!canAdd} note={readOnlyNote} onAdd={addFirstStep} />
          : <WorkflowStepForm model={model} step={step} index={selectedIndex} workflows={callable} apply={mutate} onSelect={choose}
              onFocusStep={setFocusStepKey} focusKey={focusStepKey} readOnlyNote={null} v3={defStep(step.id) ?? null}
              onSource={() => void goView('source')} />}
      </fieldset></div>
    </div>}

    {/* THE CANVAS IS THE PAGE (behind View). Nothing selected means no drawer at all. */}
    {view === 'canvas' && model && !unavailable && <div className="sem-wf-canvas-stage" ref={stage}>
      <WorkflowCanvas workflowName={creating ? undefined : draft.base.name} workflowTitle={workflowTitle}
        steps={boxes} storageKey={`workflow-canvas:${draft.base.name}`} selected={pick ?? -1} onSelect={choose} onClear={() => setPick(null)} keyChanges={layoutChanges}
        onAddStep={steps.length ? addStepAtEnd : addFirstStep} addDisabled={!canAdd} addNote={readOnlyNote}
        counts={counts} fullscreen={fullscreen} onFullscreen={() => setFullscreen((on) => !on)} fitSignal={fitSignal} />
      {drawerOpen && drawerIndex != null && drawerStep && <aside className="sem-wf-drawer" aria-label={`Step ${drawerIndex + 1} editor`}>
        <div className="sem-wf-drawer-head">
          <span className="sem-wf-kicker">{`Step ${drawerIndex + 1} of ${steps.length}`}</span>
          <div className="sem-wf-drawer-actions">
            <button type="button" className="sem-btn sem-btn-sm sem-wf-drawer-icon" title="Move this step left" aria-label="Move left"
              disabled={stock || busy || drawerIndex === 0} onClick={() => moveStep(drawerIndex, -1)}>‹</button>
            <button type="button" className="sem-btn sem-btn-sm sem-wf-drawer-icon" title="Move this step right" aria-label="Move right"
              disabled={stock || busy || drawerIndex === steps.length - 1} onClick={() => moveStep(drawerIndex, 1)}>›</button>
            <span className="sem-wf-drawer-menu"><RowMenu label="⋯" title="More things to do with this step" align="end">
              <button type="button" className="sem-btn sem-btn-sm" disabled={stock || busy} onClick={() => addStepAfter(drawerStep.key, drawerIndex + 1)}>Add step after</button>
              <button type="button" className="sem-btn sem-btn-sm" disabled={stock || busy} onClick={() => copyStep(drawerIndex)}>Copy step</button>
              <button type="button" className="sem-btn sem-btn-sm" disabled={stock || busy || steps.length <= 1}
                title={steps.length <= 1 ? 'A workflow needs at least one step.' : undefined} onClick={() => removeStep(drawerIndex)}>Delete step</button>
            </RowMenu></span>
            <button type="button" className="sem-btn sem-btn-sm sem-wf-drawer-icon" aria-pressed={pinned}
              title={pinned ? 'Unpin. The editor closes when you clear the selection.' : 'Keep this open when you clear the selection.'}
              aria-label={pinned ? 'Unpin' : 'Keep this open'} onClick={() => setPinned((on) => !on)}>{pinned ? '◉' : '○'}</button>
            <button type="button" className="sem-btn sem-btn-sm sem-wf-drawer-icon" title="Close. Escape does the same." aria-label="Close"
              onClick={() => { setPick(null); setPinned(false); }}>✕</button>
          </div>
        </div>
        <div className="sem-wf-drawer-body">
          <fieldset disabled={stock || busy} className="sem-wf-form-lock">
            <WorkflowStepForm model={model} step={drawerStep} index={drawerIndex} workflows={callable} apply={mutate} onSelect={choose}
              onFocusStep={setFocusStepKey} focusKey={focusStepKey} readOnlyNote={readOnlyNote} movement="horizontal" v3={defStep(drawerStep.id) ?? null}
              onSource={() => void goView('source')} />
          </fieldset>
        </div>
      </aside>}
    </div>}
    {view === 'canvas' && <span className="sem-wf-note">The picture follows the step order. Branches are not drawn yet, so the order is changed in Steps or here.</span>}

    {settings && <SideDrawer title="Settings" sub={workflowTitle} testId="wf-settings" onClose={() => setSettings(false)}>
      {/* A format-3 workflow of yours: its notes and inputs are fields (lane author-gaps, B7). Otherwise they read as
          before: a starter's are its own, and an older format has neither until it is upgraded. */}
      {model && mine && isV3(model.definition.schemaVersion) ? <fieldset disabled={busy} className="sem-wf-form-lock"><V3SettingsFields model={model} apply={mutate} /></fieldset> : <>
      <section className="sem-wf-side-sec">
        <b>Notes for your assistant</b>
        {def?.assistantNotes ? <p className="sem-wf-notes-text" data-testid="wf-notes">{def.assistantNotes}</p>
          : <span className="sem-wf-note" data-testid="wf-notes">No notes yet. House style for every part your assistant writes goes here.</span>}
        <span className="sem-wf-note">Semanticus adds the model facts your assistant needs by itself. Change the notes in Source{mine ? '' : ' of your copy'}.</span>
      </section>
      <section className="sem-wf-side-sec" data-testid="wf-inputs">
        <b>Inputs asked each run</b>
        {(def?.inputs ?? []).length === 0 && (def?.slots ?? []).length === 0 && <span className="sem-wf-note">None. Each step asks what it needs as it goes.</span>}
        {(def?.inputs ?? []).map((q) => <div key={q.name} className="sem-wf-kv"><span>{q.question || q.name}</span><span className="sem-wf-note">{q.type === 'enum' ? 'a choice' : q.type === 'number' ? 'a number' : 'words'}</span></div>)}
        {(def?.slots ?? []).length > 0 && <span className="sem-wf-note">Asked once when it was set up: {(def?.slots ?? []).map((s) => s.question || s.name).join('; ')}.</span>}
      </section></>}
      <section className="sem-wf-side-sec" data-testid="wf-rules">
        <b>Rules</b>
        {info ? <>
          <OfferedControl info={info} isPro={isPro} required={!!required} onEnabled={onSetEnabled} onActivation={onSetActivation} />
          <RequiredForControl name={info.name} policy={policy} isPro={isPro} titleOf={titleOf} onSetBinding={onSetBinding} />
          <button type="button" className="sem-wf-quiet-link sem-wf-start" onClick={onOpenTeamRules}>Team rules</button>
        </> : <span className="sem-wf-note">Save it first, then choose where it is offered.</span>}
      </section>
      <section className="sem-wf-side-sec" data-testid="wf-repeat">
        <b>Repeat on the Plan</b>
        <span className="sem-wf-note">Adds this workflow to the Plan as a row, on a schedule. Nothing runs by itself.</span>
        {info && !dirty
          ? <RepeatEditor row={repeatRow} repeats={repeats} isPro={isPro} busy={busy ? 'busy' : null}
              onAdd={(r: RepeatRequest) => repeatWrite(() => rpc('addPlanRepeat', { ...r, workflow: info.name, doer: 'you' }))}
              onChange={(id: string, r: RepeatRequest) => repeatWrite(() => rpc('setPlanRepeat', id, r))}
              onDelete={(id: string) => void rpc('deletePlanRepeat', id).then(readRepeats).catch((e) => setError(errorMessage(e)))} />
          : <span className="sem-wf-note">Save it first.</span>}
      </section>
      {model && mine && <section className="sem-wf-side-sec">
        <b>About it</b>
        <WorkflowSettingsFields model={model} apply={mutate} />
      </section>}
      <section className="sem-wf-side-sec">
        <b>Details</b>
        <div className="sem-wf-kv"><span>File</span><code>{draft.base.path || `${draft.base.name}.md`}</code></div>
        <div className="sem-wf-kv"><span>Version</span><span className="sem-num">{model?.definition.version ?? def?.version ?? 1}</span></div>
        <div className="sem-wf-kv"><span>Where it came from</span><span>{stock ? 'Comes with Semanticus' : def?.provenance?.derived_from ? 'From a run' : 'Yours'}</span></div>
      </section>
    </SideDrawer>}
  </div>;
}
