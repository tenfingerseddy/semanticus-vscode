import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import { onWorkflowLibraryChange, rpc } from './bridge';
import { Banner, Button, Panel, Pill, SourceBadge, type WorkflowDef, type WorkflowInfo, type WorkflowRunView } from './workflows';
// The shared overflow menu and the shared row measurement, so the Author header folds the way every other
// tool row in Studio folds instead of inventing a second rule.
import { RowMenu, useToolRow } from './toolrow';
import { WorkflowCanvas } from './workflowcanvas';
import { applyTextareaChange } from './workflowdocument.mjs';
import { WorkflowEmptySteps, WorkflowSettingsForm, WorkflowStepForm } from './workflowforms';
import {
  acceptWorkflowSave, applyWorkflowOperation, boxesFromEditModel, createWorkflowDraft, discardWorkflowDraft,
  editWorkflowSource, installWorkflowPreview, newTempKey, receiveWorkflowDocument, redoWorkflowDraft, setWorkflowPreview,
  structuredUnavailable, undoWorkflowDraft, uniqueStepId, workflowDraftDirty, workflowPreviewArgs,
  type WorkflowDefinitionDraft, type WorkflowDocument, type WorkflowEditModel, type WorkflowEditOperation, type WorkflowEditPreview,
} from './workflowdraft.mjs';

// Every add control in the editor reads the same way, and the rail's version has to survive the muted
// .sem-wf-outline button rule without a stylesheet change.
// .sem-wf-outline button has no :disabled rule and the stylesheet belongs to another lane, so the
// dimming that tells a reader "this is off right now" is carried inline.
const addStyle = (on: boolean) => ({ color: 'var(--sem-accent)', fontWeight: 650, opacity: on ? 1 : 0.4 });
const insertStyle = (on: boolean) => ({ color: 'var(--sem-accent)', fontSize: '10.5px', opacity: on ? 0.75 : 0.3, paddingTop: 2, paddingBottom: 2 });
const STOCK_EDIT_NOTE = 'Copy to this project to edit';
// These two sentences used to be rows of their own above the canvas. A reader needs each of them once, on
// the day they first meet it, and after that they were two rows of height on every screen. They are the
// tooltips on the control they are about now, which is where the question gets asked.
const STOCK_COPY_WHY = 'This built-in workflow stays unchanged. Copy its exact source to this project before editing.';
const DRAFT_WHY = 'Your changes are held here until you choose Save workflow. Saving checks whether the file changed since you opened it and reports a conflict instead of overwriting.';
const SAVED_WHY = 'Nothing is waiting to be saved.';

function newStepValue(model: WorkflowEditModel) {
  const title = 'New step';
  return { id: uniqueStepId(title, model), title, instructions: '', ops: [] };
}

type View = 'steps' | 'canvas' | 'source';
interface WorkflowDocumentEditResult { changed: boolean; reason?: string | null; document: WorkflowDocument }

const KEBAB = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
const errorMessage = (cause: unknown) => cause instanceof Error ? cause.message : String(cause);
const isDraftConflict = (message: string) => /changed on disk|model changed|resolves to a different file/i.test(message);

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

// The save state, as one word in the header. The explanation that used to be a three line block is its
// tooltip: it answers "what does unsaved mean here", which is a question asked once, not on every screen.
function DraftState({ dirty }: { dirty: boolean }) {
  return <span className={dirty ? 'sem-wf-author-state dirty' : 'sem-wf-author-state'} title={dirty ? DRAFT_WHY : SAVED_WHY}>
    {dirty ? 'Unsaved draft' : 'Saved'}
  </span>;
}

function ViewTabs({ view, unavailable, busy, onView }: { view: View; unavailable: string | null; busy: boolean; onView: (view: View) => void }) {
  return <div className="sem-seg" role="tablist" aria-label="Workflow editing view">
    {(['steps', 'canvas', 'source'] as const).map((item) => <button key={item} role="tab" className="sem-seg-item"
      aria-selected={view === item} aria-pressed={view === item}
      disabled={busy}
      title={item !== 'source' ? unavailable ?? undefined : undefined}
      onClick={() => onView(item)}>{item[0].toUpperCase() + item.slice(1)}</button>)}
  </div>;
}

function PreviewPanel({ preview, operations, busy, onApply, onClose, onReload, onReview }: {
  preview: WorkflowEditPreview; operations: WorkflowEditOperation[]; busy: boolean;
  onApply: () => void; onClose: () => void; onReload: () => void; onReview: () => void;
}) {
  const conflict = preview.outcome === 'conflict';
  const lead = preview.outcome === 'ready'
    ? 'Ready to save. Only the lines below change.'
    : conflict
      ? 'Choose Reload saved file to drop your draft, or Review current file to keep it and check it against the newer file.'
      : preview.reason;
  return <section className="sem-wf-preview" aria-label="Save workflow preview">
    <div className="sem-wf-preview-head"><div><b>Save workflow preview</b><p>{lead}</p></div><div className="flex gap-2">
      {preview.canApply && <Button primary disabled={busy} onClick={onApply}>{busy ? 'Applying…' : 'Apply'}</Button>}
      {conflict && <><Button disabled={busy} onClick={onReload}>Reload saved file</Button><Button disabled={busy} onClick={onReview}>Review current file</Button></>}
      <Button disabled={busy} onClick={onClose}>Close</Button>
    </div></div>
    {(operations.some((operation) => operation.op === 'stabilize_step_ids') || (preview.keyChanges ?? []).some((change) => change.oldStepId !== change.newStepId))
      && <div className="sem-wf-preview-note">Step ids are made stable so steps can move safely.</div>}
    {(preview.issues ?? []).filter((issue) => conflict || issue.message !== preview.reason || issue.target !== 'workflow').map((issue, index) => <Banner key={`${issue.code}:${index}`} color={issue.severity === 'error' ? 'var(--sem-bad)' : 'var(--sem-warn)'}>
      {issue.target && issue.target !== 'workflow' ? <b>{issue.target}{issue.field ? ` · ${issue.field}` : ''}: </b> : null}{issue.message}
    </Banner>)}
    {preview.diff && <pre className="sem-wf-diff" aria-label="Proposed source diff">{preview.diff.split('\n').map((line, index) => <span key={index} className={line.startsWith('+') && !line.startsWith('+++') ? 'add' : line.startsWith('-') && !line.startsWith('---') ? 'del' : ''}>{index ? '\n' : ''}{line}</span>)}</pre>}
  </section>;
}

function SourceEditor({ draft, stock, busy, onChange }: {
  draft: WorkflowDefinitionDraft; stock: boolean; busy: boolean; onChange: (text: string) => void;
}) {
  const source = draft.sourceText.charCodeAt(0) === 0xfeff ? draft.sourceText.slice(1) : draft.sourceText;
  return <Panel>
    <div className="sem-wf-form-title"><div><b>Source</b><p>Edit the same unsaved draft as Steps and Canvas.</p></div><code>{draft.base.path || `${draft.base.name}.md`}</code></div>
    <textarea className="sem-wf-source" aria-label="Workflow source" value={source} readOnly={stock || busy} spellCheck={false}
      onChange={(event) => onChange(applyTextareaChange(draft.sourceText, event.target.value))} />
  </Panel>;
}

export function DesignMode({ info, def: _def, creating, onSaved, onDeleted, sessionId = null, activeRun = null }: {
  info: WorkflowInfo | null; def: WorkflowDef | null; creating: boolean; onSaved: (name: string) => void; onDeleted: () => void;
  layout?: 'stack' | 'outline'; sessionId?: string | null; activeRun?: WorkflowRunView | null;
}) {
  const [draft, setDraft] = useState<WorkflowDefinitionDraft | null>(null);
  const current = useRef<WorkflowDefinitionDraft | null>(null);
  const [view, setView] = useState<View>('steps');
  const [selected, setSelected] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [createName, setCreateName] = useState('');
  const [reviewCurrent, setReviewCurrent] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [workflows, setWorkflows] = useState<WorkflowInfo[]>([]);
  const [layoutChanges, setLayoutChanges] = useState<WorkflowEditPreview['keyChanges']>([]);
  const [focusStepKey, setFocusStepKey] = useState<string | null>(null);
  // Canvas state. `pick` is null until a step is chosen, because on Canvas "nothing selected" is a real
  // state: the canvas has the whole page and there is no drawer. Steps keeps its own `selected`, where -1
  // still means Workflow settings, so that surface is unchanged.
  const [pick, setPick] = useState<number | null>(null);
  const [pinned, setPinned] = useState(false);
  const [fullscreen, setFullscreen] = useState(false);
  const [fitSignal, setFitSignal] = useState(0);
  const lastPick = useRef(0);
  const stage = useRef<HTMLDivElement | null>(null);
  const request = useRef(0);
  const head = useToolRow();

  const install = (next: WorkflowDefinitionDraft) => { current.current = next; setDraft(next); };
  const load = async (keepDirty = true) => {
    if (!info?.name) return null;
    const ticket = ++request.current;
    try {
      const document = await rpc<WorkflowDocument>('getWorkflowDocument', info.name, sessionId);
      if (ticket !== request.current) return null;
      const next = keepDirty ? receiveWorkflowDocument(current.current, document, sessionId) : createWorkflowDraft(document, sessionId);
      install(next); setError(null); return document;
    } catch (cause) { if (ticket === request.current) setError(errorMessage(cause)); return null; }
  };

  useEffect(() => {
    current.current = null; setDraft(null); setView('steps'); setSelected(0); setPick(null); setPinned(false); setFullscreen(false); setFocusStepKey(null); setError(null); setNotice(null); setCreateName('');
    if (!creating) void load(false);
    rpc<WorkflowInfo[]>('listWorkflows').then(setWorkflows).catch(() => setWorkflows([]));
    const off = onWorkflowLibraryChange((items) => { if (Array.isArray(items)) setWorkflows(items as WorkflowInfo[]); if (!creating) void load(true); });
    return () => { ++request.current; off(); };
  }, [creating, info?.name]); // eslint-disable-line react-hooks/exhaustive-deps

  const beginCreate = async () => {
    const name = createName.trim();
    if (!KEBAB.test(name)) { setError('Use a name such as my-workflow. Start with a letter and use lower-case words joined by hyphens.'); return; }
    setBusy(true); setError(null);
    try {
      const preview = await rpc<WorkflowEditPreview>('previewWorkflowEdit', name, null, null, '[]', null, true, sessionId);
      if (!preview.editModel) { setError(preview.reason || 'The new workflow could not be prepared.'); return; }
      const base = emptyCreateDocument(name, preview);
      const next = createWorkflowDraft(base, sessionId);
      next.sourceText = preview.proposedText ?? '';
      next.editModel = preview.editModel;
      install(next);
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  };

  const mutate = (operation: WorkflowEditOperation, structural = false) => {
    if (!current.current) return;
    try { install(applyWorkflowOperation(current.current, operation, structural)); setError(null); setNotice(null); }
    catch (cause) { setError(errorMessage(cause)); }
  };

  const preview = async (subject = current.current) => {
    if (!subject) return null;
    const revision = JSON.stringify([subject.base.path, subject.base.byteHash, subject.sourceText, subject.operations]);
    const result = await rpc<WorkflowEditPreview>('previewWorkflowEdit', ...workflowPreviewArgs(subject, creating));
    const live = current.current;
    if (!live || JSON.stringify([live.base.path, live.base.byteHash, live.sourceText, live.operations]) !== revision)
      throw new Error('The draft changed while it was being checked. Check it again.');
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
            if (result.outcome === 'ready' || result.outcome === 'no_change')
              install(setWorkflowPreview(installWorkflowPreview(pending, result, false), null));
            else {
              install(setWorkflowPreview(pending, null));
              setError(result.reason || 'The draft could not be projected into source. Your structured changes are still held.');
            }
          }
        }
        setView('source');
        return;
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
    }
    finally { setBusy(false); }
  };

  const requestSave = async () => {
    if (!current.current) return;
    setBusy(true); setError(null); setNotice(null); setReviewCurrent(false);
    try {
      const result = await preview(current.current);
      if (result?.outcome === 'no_change') setNotice('Nothing to save.');
    } catch (cause) {
      const message = errorMessage(cause);
      setError(message);
      if (isDraftConflict(message)) {
        const document = await load(true);
        const live = current.current;
        if (document && live) install({ ...live, latest: document, preview: refusedPreview(live, document, message) });
      }
    }
    finally { setBusy(false); }
  };

  const applySave = async () => {
    const held = current.current;
    const ready = held?.preview;
    if (!held || !ready?.canApply || !ready.proposedText) return;
    setBusy(true); setError(null);
    try {
      let document: WorkflowDocument;
      if (creating) {
        await rpc('saveWorkflow', held.base.name, ready.proposedText, 'human', true, held.sessionId);
        document = await rpc<WorkflowDocument>('getWorkflowDocument', held.base.name, held.sessionId);
      } else {
        const result = await rpc<WorkflowDocumentEditResult>('editWorkflowDocument', held.base.name, held.base.byteHash, ready.proposedText, held.base.path, 'human', held.sessionId);
        if (!result.changed) {
          const reason = result.reason || 'The saved file changed. Your draft is kept.';
          install({ ...held, latest: result.document, preview: refusedPreview(held, result.document, reason) });
          return;
        }
        document = result.document;
      }
      setLayoutChanges(ready.keyChanges ?? []);
      install(acceptWorkflowSave(held, document));
      setNotice(activeRun ? 'Saved for future runs. This run keeps the version captured when it started.' : 'Workflow saved.');
      onSaved(held.base.name);
    } catch (cause) {
      const detail = errorMessage(cause);
      const conflict = isDraftConflict(detail);
      const message = conflict ? `Your draft is kept. ${detail}` : detail;
      setError(message);
      const document = await load(true);
      const live = current.current;
      if (conflict && document && live) install({ ...live, latest: document, preview: refusedPreview(live, document, message) });
    } finally { setBusy(false); }
  };

  const discard = async () => {
    setBusy(true); setError(null); setNotice(null); setReviewCurrent(false);
    if (creating) { setDraft(null); current.current = null; await beginCreate(); }
    else { const document = await load(false); if (document && current.current) install(discardWorkflowDraft(current.current, document)); }
    setBusy(false);
  };

  const copyStock = async () => {
    if (!draft || draft.base.library !== 'stock') return;
    setBusy(true); setError(null);
    try {
      await rpc('saveWorkflow', draft.base.name, draft.base.exactText, 'human', true, draft.sessionId);
      const document = await rpc<WorkflowDocument>('getWorkflowDocument', draft.base.name, draft.sessionId);
      install(createWorkflowDraft(document, draft.sessionId)); onSaved(draft.base.name); setNotice('Project copy created. You can edit it now.');
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
        if (!result.editModel || result.outcome !== 'ready') { setError(result.reason || 'The draft no longer matches the current file.'); return; }
        install(setWorkflowPreview(installWorkflowPreview(rebased, result, false), null));
      } else install({ ...held, base: held.latest, preview: null });
      setReviewCurrent(false); setNotice('The current saved file was reviewed. Your draft is still unsaved.');
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
        setError(result.reason || 'That draft history point cannot open in this view. Your current draft is unchanged.'); return;
      }
      const projected = setWorkflowPreview(installWorkflowPreview(next, result, false), null);
      install({ ...projected, undo: next.undo, redo: next.redo });
    } catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  };

  const del = async () => {
    if (!info) return;
    setBusy(true);
    try { await rpc('deleteWorkflow', info.name, 'human'); onDeleted(); }
    catch (cause) { setError(errorMessage(cause)); }
    finally { setBusy(false); }
  };

  const boxes = useMemo(() => boxesFromEditModel(draft?.editModel ?? null,
    (name) => workflows.find((workflow) => workflow.name === name)?.title || name), [draft?.editModel, workflows]);

  // ---- the drawer, decided once ---------------------------------------------------------------------
  // Read before the early returns below, so every hook still runs on every render.
  const stepList = draft?.editModel?.definition.steps ?? [];
  if (pick != null) lastPick.current = pick;
  const wanted = pick != null ? pick : (pinned ? lastPick.current : null);
  const drawerIndex = wanted == null || stepList.length === 0 ? null : Math.min(Math.max(0, wanted), stepList.length - 1);
  const drawerOpen = view === 'canvas' && drawerIndex != null;

  // Full screen is a class on the document body, not a prop threaded through App.tsx: the parts it hides
  // (the Studio header, the area row, the Workflows rail, this page's own header and the bottom bar) belong
  // to four components in three other lanes, and the stylesheet can reach all of them from one attribute.
  // It is deliberately not remembered: leaving the page leaves full screen.
  useEffect(() => {
    if (!fullscreen) return;
    document.body.setAttribute('data-studio-fullscreen', 'workflow-canvas');
    return () => document.body.removeAttribute('data-studio-fullscreen');
  }, [fullscreen]);
  useEffect(() => { if (view !== 'canvas') setFullscreen(false); }, [view]);
  // The canvas is a different width once the drawer is beside it, so the steps are refitted into the space
  // it actually has rather than left sitting off to one side.
  useEffect(() => { setFitSignal((n) => n + 1); }, [drawerOpen, fullscreen]);

  useEffect(() => {
    if (view !== 'canvas') return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape' || event.defaultPrevented) return;
      // A menu or a popover is the nearer thing to close, and each one closes itself on Escape.
      if (document.querySelector('.sem-rowmenu-pop, [role="menu"], [role="dialog"]')) return;
      if (fullscreen) { event.preventDefault(); setFullscreen(false); return; }
      if (!drawerOpen) return;
      // "With the drawer focused" in practice means anywhere on this page: after clicking a step, focus is
      // on the canvas pane, and on a read-only built-in the drawer's own controls are all disabled, so
      // requiring focus INSIDE the drawer left Escape doing nothing in the two commonest cases (measured
      // 2026-09-15). Focus on nothing at all counts too, because then no other tool owns the key.
      const target = event.target as Node | null;
      const here = !target || target === document.body || (!!stage.current && stage.current.contains(target));
      if (!here) return;
      event.preventDefault(); setPick(null); setPinned(false);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [view, fullscreen, drawerOpen]);

  if (creating && !draft) return <Panel><div className="sem-wf-create">
    <div><div className="sem-wf-kicker">New workflow</div><h2>Name this workflow</h2><p>The file name is fixed after creation. You can change its title at any time.</p></div>
    <label>Name<input autoFocus className="sem-wf-input tnum" value={createName} placeholder="my-workflow" onChange={(event) => setCreateName(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter') void beginCreate(); }} /></label>
    {error && <Banner color="var(--sem-bad)">{error}</Banner>}
    <Button primary disabled={busy || !KEBAB.test(createName.trim())} onClick={beginCreate}>{busy ? 'Preparing…' : 'Create draft'}</Button>
  </div></Panel>;

  if (!draft) return <Panel>{error ? <Banner color="var(--sem-bad)">{error}</Banner> : 'Loading saved definition…'}</Panel>;
  const stock = !creating && draft.base.library === 'stock';
  const unavailable = structuredUnavailable(draft.editModel);
  const dirty = creating || workflowDraftDirty(draft);
  const model = draft.editModel;
  const steps = model?.definition.steps ?? [];
  const selectedIndex = Math.min(selected, Math.max(0, steps.length - 1));
  const step = steps[selectedIndex];
  const callable = workflows.filter((workflow) => workflow.name !== draft.base.name && !workflow.error);
  const workflowTitle = model?.definition.title || draft.base.metadata.title || draft.base.name;
  const readOnlyNote = stock ? STOCK_EDIT_NOTE : null;
  const canAdd = !stock && !busy && !!model && !unavailable;

  const drawerStep = drawerIndex == null ? null : steps[drawerIndex];
  const counts = { steps: steps.length, actions: steps.reduce((sum, item) => sum + (item.ops?.length ?? 0), 0),
    checks: steps.reduce((sum, item) => sum + (item.gate?.verify?.length ?? 0), 0) };

  // Choosing a step chooses it for BOTH surfaces, so moving between Steps and Canvas keeps your place.
  const choose = (index: number) => { setPick(index); setSelected(index); };

  // One add path for every control (rail, insert-between, canvas, empty state). It mints the draft key,
  // selects the step it just made and asks the form to put the caret in its title.
  const addStepAfter = (after: string | null, index: number) => {
    const live = current.current?.editModel;
    if (!live) return;
    const key = newTempKey();
    mutate({ op: 'add_step', after, tempKey: key, value: newStepValue(live) }, true);
    choose(index); setFocusStepKey(key);
  };

  // The drawer header's step actions. These are the same four operations the step form builds
  // (workflowforms.tsx, WorkflowStepForm): the form's own title row is hidden inside the drawer, because
  // the drawer header is where a reader looks for them, and two copies of Delete step in one 440px column
  // is worse than one. The op shapes must stay in step with that file.
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
  const addFirstStep = () => addStepAfter(null, 0);
  const addStepAtEnd = () => addStepAfter(steps.length ? steps[steps.length - 1].key : null, steps.length);
  const stepFormProps = {
    model: model as WorkflowEditModel, workflows: callable, apply: mutate, onSelect: choose,
    onFocusStep: setFocusStepKey, focusKey: focusStepKey, readOnlyNote,
  };

  return <div className="sem-wf-editor" data-wf-view={view}>
    {/* ONE 40px ROW. It used to be a Panel of four stacked rows (title, save state, running snapshot, the
        built-in explanation), which cost about 190px of page before the canvas even started. Every
        sentence that left is a tooltip on the control it is about. */}
    <div className="sem-wf-author-head" ref={head.ref as React.Ref<HTMLDivElement>}>
      <b className="sem-wf-author-title" title={`${workflowTitle} · ${draft.base.name}`}>{workflowTitle}</b>
      <SourceBadge name={draft.base.name} source={stock ? 'stock' : creating ? 'user' : draft.base.library} />
      {stock && <Pill>read only</Pill>}
      <DraftState dirty={dirty} />
      {activeRun && <span className="sem-wf-author-run" title={`${activeRun.title || workflowTitle} · ${activeRun.runId} · started ${activeRun.startedUtc ? new Date(activeRun.startedUtc).toLocaleString() : 'earlier'} · version ${activeRun.workflowVersion}. A run keeps the version captured when it started.`}>Running now</span>}
      <div className="sem-wf-author-actions">
        <ViewTabs view={view} unavailable={unavailable} busy={busy} onView={(next) => void goView(next)} />
        {!stock && (head.narrow
          ? <RowMenu label="More" title="Undo, discard or delete this draft">
            <button type="button" className="sem-btn sem-btn-sm" disabled={busy || !draft.undo.length} onClick={() => void travelDraft('undo')}>Undo draft</button>
            <button type="button" className="sem-btn sem-btn-sm" disabled={busy || !draft.redo.length} onClick={() => void travelDraft('redo')}>Redo</button>
            <button type="button" className="sem-btn sem-btn-sm" disabled={busy || !dirty} onClick={() => void discard()}>Discard changes</button>
            {!creating && info?.source === 'user' && (confirmDelete
              ? <button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={() => void del()}>Confirm delete</button>
              : <button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={() => setConfirmDelete(true)}>Delete…</button>)}
          </RowMenu>
          : <>
            <button type="button" className="sem-btn sem-btn-sm" disabled={busy || !draft.undo.length} onClick={() => void travelDraft('undo')}>Undo draft</button>
            <button type="button" className="sem-btn sem-btn-sm" disabled={busy || !draft.redo.length} onClick={() => void travelDraft('redo')}>Redo</button>
            <button type="button" className="sem-btn sem-btn-sm" disabled={busy || !dirty} onClick={() => void discard()}>Discard changes</button>
            {!creating && info?.source === 'user' && (confirmDelete
              ? <button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={() => void del()}>Confirm delete</button>
              : <button type="button" className="sem-btn sem-btn-sm" disabled={busy} onClick={() => setConfirmDelete(true)}>Delete…</button>)}
          </>)}
        {!stock && <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={busy || !dirty} onClick={() => void requestSave()}>{busy ? 'Checking…' : 'Save workflow'}</button>}
        {stock && <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" title={STOCK_COPY_WHY} disabled={busy} onClick={() => void copyStock()}>{busy ? 'Copying…' : 'Copy to this project'}</button>}
      </div>
    </div>
    {(error || notice || (unavailable && view !== 'source')) && <div className="sem-wf-author-notices">
      {error && <Banner color="var(--sem-bad)">{error}</Banner>}
      {notice && <div role="status" className="sem-wf-notice">{notice}</div>}
      {unavailable && view !== 'source' && <Banner color="var(--sem-warn)">{unavailable} Open Source to edit the file.</Banner>}
    </div>}

    {draft.preview && draft.preview.outcome !== 'no_change' && <PreviewPanel preview={draft.preview} operations={draft.operations} busy={busy} onApply={() => void applySave()}
      onClose={() => install(setWorkflowPreview(draft, null))} onReload={() => void reloadSaved()} onReview={() => setReviewCurrent(true)} />}
    {reviewCurrent && <Panel><div className="sem-wf-form-title"><div><b>Review current file</b><p>Read the newer saved file. Keep my draft then checks your draft against it.</p></div><Button disabled={busy} onClick={() => void rebaseReviewed()}>Keep my draft</Button></div><pre className="sem-wf-current-source">{draft.latest.exactText}</pre></Panel>}

    {view === 'source' && <SourceEditor draft={draft} stock={stock} busy={busy}
      onChange={(text) => { install(editWorkflowSource(draft, text)); setNotice(null); setError(null); }} />}

    {view === 'steps' && model && !unavailable && <div className="sem-wf-steps-layout">
      <aside className="sem-wf-outline">
        <div className="sem-wf-kicker">Steps</div>
        <button className={selected < 0 ? 'active' : ''} onClick={() => setSelected(-1)}>Workflow settings</button>
        {steps.length === 0 && <button type="button" disabled={!canAdd} style={addStyle(canAdd)} onClick={addFirstStep}>Add the first step</button>}
        {steps.map((item, index) => <Fragment key={item.key}>
          <button className={index === selectedIndex && selected >= 0 ? 'active' : ''} onClick={() => setSelected(index)}>{index + 1}. {item.title || 'Untitled'}</button>
          {index < steps.length - 1 && <button type="button" disabled={!canAdd} style={insertStyle(canAdd)}
            aria-label={`Insert a step after step ${index + 1}`} onClick={() => addStepAfter(item.key, index + 1)}>+ Insert step</button>}
        </Fragment>)}
        {steps.length > 0 && <button type="button" disabled={!canAdd} style={addStyle(canAdd)} onClick={addStepAtEnd}>+ Add step</button>}
        {stock && <small className="sem-wf-restriction">{STOCK_EDIT_NOTE}</small>}
      </aside>
      <div className="min-w-0"><fieldset disabled={stock || busy} className="sem-wf-form-lock">
        {selected < 0 ? <>
          <WorkflowSettingsForm model={model} apply={mutate} readOnlyNote={readOnlyNote} />
          {steps.length === 0 && <WorkflowEmptySteps disabled={!canAdd} note={readOnlyNote} onAdd={addFirstStep} />}
        </> : step
          ? <WorkflowStepForm {...stepFormProps} step={step} index={selectedIndex} />
          : <WorkflowEmptySteps disabled={!canAdd} note={readOnlyNote} onAdd={addFirstStep} />}
      </fieldset></div>
    </div>}

    {/* THE CANVAS IS THE PAGE. Nothing selected means no drawer at all, so the picture Kane came to read
        has the whole width and the whole remaining height. */}
    {view === 'canvas' && model && !unavailable && <div className="sem-wf-canvas-stage" ref={stage}>
      <WorkflowCanvas workflowName={creating ? undefined : draft.base.name} workflowTitle={workflowTitle}
        steps={boxes} storageKey={`workflow-canvas:${draft.base.name}`} selected={pick ?? -1} onSelect={choose} onClear={() => setPick(null)} keyChanges={layoutChanges}
        onAddStep={steps.length ? addStepAtEnd : addFirstStep} addDisabled={!canAdd} addNote={readOnlyNote}
        counts={counts} fullscreen={fullscreen} onFullscreen={() => setFullscreen((on) => !on)} fitSignal={fitSignal} />
      {drawerOpen && drawerIndex != null && drawerStep && <aside className="sem-wf-drawer" aria-label={`Step ${drawerIndex + 1} editor`}>
        <div className="sem-wf-drawer-head">
          <span className="sem-wf-kicker">{`Step ${drawerIndex + 1} of ${steps.length}`}</span>
          <span className="sem-wf-drawer-id tnum" title={`Step id: ${drawerStep.id}. Step ids stay fixed after saving.`}>{drawerStep.id}</span>
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
          <h3 className="sem-wf-drawer-title">{drawerStep.title || 'Untitled'}</h3>
          <fieldset disabled={stock || busy} className="sem-wf-form-lock">
            <WorkflowStepForm {...stepFormProps} step={drawerStep} index={drawerIndex} movement="horizontal" />
          </fieldset>
        </div>
      </aside>}
    </div>}
  </div>;
}
