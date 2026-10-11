import { useState } from 'react';
import { rpc } from './bridge';
import { messageOf, useAutoGrow, useRemote, type Remote } from './hooks';
import { KnowledgeView } from './knowledge';
import { DraftMark, EditMovedBanner, useEditBase } from './editbase';
import { heldDraft } from './drafts';

// ===================================================================================================
// AI instructions.
//
// The instructions saved IN the model: the standing background every answer your assistant gives starts
// from. They are read and written through the same engine ops the assistant's own door uses
// (getAiInstructions / setAiInstructions), so the two doors cannot drift apart.
//
// One component, two homes, because Kane made this a page of its own on 22 September 2026 without taking
// it off the Overview: `AiInstructionsPage` is the Edit > AI instructions page and owns its own read;
// `AiInstructionsCard` is the same card the Overview has always shown and is fed by the Overview's read.
// The card is the only thing that knows how instructions look, so the two can never disagree.
//
// TWO TABS, from 23 September 2026 (Kane: "Model notes - agreed implement recommendation"). Model notes
// stopped being a page of its own and became this page's second tab, because both tabs are the written
// background for one model: Instructions is what the assistant must start from, Notes is what a person
// should know. The Notes tab mounts `KnowledgeView` exactly as the page did, unchanged.
//
// `knowledge` stays a real route id. It is how the Notes tab is reached from a deep link, the ask box,
// the Plan and old saved state, the same way `evidence` opens Validation on its Reports tab. Which tab
// is open is therefore the ROUTE and not local state, so the strip, a deep link and the link at the foot
// of the Instructions tab can never disagree about where you are. App.tsx owns that mapping.
//
// The Notes tab stays MOUNTED while the Instructions tab is showing, hidden rather than thrown away, so
// switching tabs cannot lose a half-written note. It mounts only once this page is open, so a person who
// never opens AI instructions never pays for its reads.
// ===================================================================================================

export interface AiInstructions { present: boolean; instructions?: string | null; length: number; limit: number; culture?: string | null; note?: string | null }

/** A suggested addition to the written background, waiting for a person to accept or reject it. The
 *  suggestions themselves come from the assistant; the decision is always a human one, which is why the
 *  Accept and Reject controls follow this card wherever it is rendered. */
export interface PrimerSuggestion {
  id: string; section: string; markdown: string; capturedUtc?: string | null; provenance?: string;
}
export interface PrimerSuggestionList { suggestions?: PrimerSuggestion[]; note?: string | null }

/** Which of the two tabs is showing. `notes` is the `knowledge` route; `instructions` is the `instructions`
 *  route. The page never holds this in state, so there is one answer and the strip already knows it. */
export type InstructionsTab = 'instructions' | 'notes';

/** The page's two tabs. Both are real routes, so a deep link, the ask box and the link at the foot of the
 *  Instructions tab all agree about which one is open. Exported and free of hooks on purpose: a test can
 *  then call it and press a tab for real instead of matching this file's text. */
export function InstructionsTabs({ tab, onTab, notesWaiting = 0 }: { tab: InstructionsTab; onTab: (tab: InstructionsTab) => void; notesWaiting?: number }) {
  return <div className="sem-seg" aria-label="AI instructions">
    <button type="button" className="sem-seg-item" data-page-tab="instructions" aria-pressed={tab === 'instructions'}
      onClick={() => onTab('instructions')}>Instructions</button>
    <button type="button" className="sem-seg-item" data-page-tab="notes" aria-pressed={tab === 'notes'}
      onClick={() => onTab('notes')}>Notes{notesWaiting > 0 && <span className="sem-num ml-1" title={notesWaiting === 1 ? 'One suggested note is waiting.' : `${notesWaiting} suggested notes are waiting.`}>{notesWaiting}</span>}</button>
  </div>;
}

/** Edit > AI instructions. The page owns its own read of the two engine facts the card needs, keyed on the
 *  session and on every change this webview has been told about, so an edit made through the other door
 *  shows up here without a reload. */
export function AiInstructionsPage({ sessionId, revision, changeNonce, tab, onTab }: {
  sessionId: string; revision?: number; changeNonce: number;
  tab: InstructionsTab; onTab: (tab: InstructionsTab) => void;
  /** The Notes tab no longer lists saved workflows (they live in Workflows), so nothing here opens one. Kept so the caller need not change. */
  onOpenWorkflows?: (name?: string) => void;
}) {
  const keys = [sessionId, revision, changeNonce];
  const [instructions, reloadInstructions] = useRemote<AiInstructions>(() => rpc<AiInstructions>('getAiInstructions'), keys);
  const [suggestions, reloadSuggestions] = useRemote<PrimerSuggestionList>(() => rpc<PrimerSuggestionList>('listPrimerSuggestions'), keys);
  // One frame for both tabs: the strip sits on the page margin, and the Instructions pane is the page grid
  // under it, one --sem-gap below, so the strip and the card share a left edge (Instrument, 23 September).
  return <div className="flex h-full min-h-0 flex-col">
    <div className="xe-tabbar"><InstructionsTabs tab={tab} onTab={onTab} notesWaiting={suggestions.status === 'ready' ? (suggestions.value.suggestions ?? []).length : 0} /></div>
    <div className={tab === 'instructions' ? 'min-h-0 flex-1 overflow-auto' : 'hidden'}>
      <main className="sem-page sem-instructions-page xe-under-bar">
        <AiInstructionsCard state={instructions} onReload={reloadInstructions} />
      </main>
    </div>
    {/* Hidden, not unmounted: see the header note. A half-written note survives a look at the other tab. */}
    <div className={tab === 'notes' ? 'min-h-0 flex-1' : 'hidden'} data-testid="instructions-notes-pane">
      <KnowledgeView onSuggestionsChanged={reloadSuggestions} />
    </div>
  </div>;
}

/** AI Instructions (Kane's name, 15 Sep). The instructions saved in the model, read and written through the same engine
 *  path the assistant's own instructions op uses, so the two doors cannot drift apart.
 *
 *  One card on its own page. The Overview used to draw a compact copy of it across its foot; since the Overview
 *  redesign (23 September) the Overview shows a tile that opens this page instead, so the compact mode went.
 *  Instrument: one hairline card, the text in a flat inset. A waiting note suggestion is counted on the Notes tab. */
export function AiInstructionsCard({ state, onReload }: {
  state: Remote<AiInstructions>; onReload: () => void;
}) {
  // Words typed and not saved outlive this card (a tab switch, a restarted engine, a reload): it opens on them (the drafts class, drafts.ts).
  const held = heldDraft<string>('instructions|text');
  const [editing, setEditing] = useState(!!held);
  const [draft, setDraft] = useState(held?.value ?? '');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const value = state.status === 'ready' ? state.value : null;
  const limit = value?.limit || 2000;
  const text = value?.present ? (value.instructions || '') : '';

  const open = () => { setDraft(text); setSaveError(null); setEditing(true); };
  // The editor grows with its text (twelve rows is the floor), so the whole draft shows and the page scrolls.
  const editorRef = useAutoGrow(draft, editing);
  // The instructions can be rewritten by the assistant while this editor is open; Save must not write the copy taken at open over that.
  const guard = useEditBase<string>({
    current: state.status === 'ready' ? text : undefined, draft, editing, reseed: setDraft, persist: { scope: 'instructions|text' },
    readNow: async () => { const r = await rpc<AiInstructions>('getAiInstructions'); return r.present ? (r.instructions || '') : ''; },
  });
  const save = async () => {
    setSaving(true); setSaveError(null);
    try {
      if (!(await guard.beforeSave())) return;
      // The write names the instructions the Save read as the ones it replaces; the engine compares them inside its own write.
      try { await rpc('setAiInstructions', draft, null, 'human', guard.confirmed() ?? ''); }
      catch (e) { if (await guard.refused(e)) return; throw e; }
      // R-B: the editor closes, and the draft ends, only when the words it holds are still exactly the ones the engine took. Words typed while the Save was on its way stay held and the editor stays open over the saved text.
      if (guard.saved(draft)) setEditing(false);
      onReload();
    }
    catch (error) { setSaveError(messageOf(error)); }
    finally { setSaving(false); }
  };

  // The card is the reading column, not a box around one: it takes the row and stops at the width of its own 80ch
  // text (see .xe-instr). data-readable says so, which is how the space check knows a cap on a reading column is
  // the point and not a page held narrow.
  return <section className="sem-card xe-instr" data-readable data-testid="overview-instructions">
    <div className="xe-instr-head">
      <div className="xe-shrink">
        {/* No title: the strip above already says AI instructions. The subtitle is the honest one: Copilot reads these
            (the model's saved custom instructions), your own assistant does not. */}
        <div className="sem-text-meta">Copilot reads these before it answers questions about this model.</div>
      </div>
      {state.status === 'ready' && !editing && value?.present
        && <button type="button" className="sem-btn sem-btn-sm" onClick={open}>Edit</button>}
    </div>

    <div className="xe-instr-body">
    {state.status === 'loading' && <div className="sem-inline sem-text-meta" role="status"><span className="sem-spin" /> Reading the instructions…</div>}
    {state.status === 'error' && <div className="xe-error" role="alert">
      <span>The instructions could not be read. {state.message}</span>
      <button type="button" className="sem-btn sem-btn-sm" onClick={onReload}>Try again</button>
    </div>}

    {editing && <div className="sem-stack">
      <label className="sr-only" htmlFor="overview-instructions-editor">Instructions for Copilot</label>
      <textarea id="overview-instructions-editor" ref={editorRef} data-readable className="sem-field xe-instr-editor" rows={12} value={draft} autoFocus spellCheck
        onChange={(event) => setDraft(event.target.value)} />
      <div className="sem-inline sem-text-meta">
        <span className={draft.length > limit ? 'sem-num xe-grow xe-bad' : 'sem-num xe-grow'}>{draft.length.toLocaleString('en-US')} of {limit.toLocaleString('en-US')} characters</span>
        <DraftMark show={guard.drafted} />
        <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" disabled={saving || draft.length > limit} onClick={() => void save()}>{saving ? 'Saving…' : 'Save'}</button>
        <button type="button" className="sem-btn sem-btn-sm" disabled={saving} onClick={() => setEditing(false)}>Cancel</button>
      </div>
      {guard.moved && <EditMovedBanner what="text" theirs={guard.theirs} onKeepMine={guard.keepMine} onUseNew={guard.useNew} disabled={saving} />}
      {draft.length > limit && <div className="xe-error">That is longer than this model accepts. Shorten it to {limit.toLocaleString('en-US')} characters.</div>}
      {saveError && <div className="xe-error" id="overview-instructions-error" role="alert">The instructions were not saved. {saveError}</div>}
    </div>}

    {!editing && state.status === 'ready' && value?.present && <div className="sem-stack">
      <div className="sem-inset xe-instr-text" data-readable>{text}</div>
      <div className="sem-inline sem-text-meta">
        <span title={value.culture ? `Written for ${value.culture}` : undefined}>{(value.length || text.length).toLocaleString('en-US')} of {limit.toLocaleString('en-US')} characters</span>
        <div className="xe-meter"><i style={{ width: `${Math.min(100, (100 * (value.length || text.length)) / limit)}%` }} /></div>
      </div>
      {value.note && <div className="sem-text-meta">{value.note}</div>}
    </div>}

    {!editing && state.status === 'ready' && !value?.present && <div className="sem-inset sem-stack xe-start">
      <strong>Nothing written yet.</strong>
      <span className="sem-text-meta">Copilot answers from the model alone. Write the first instructions.</span>
      <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" onClick={open}>Write instructions</button>
    </div>}
    </div>

  </section>;
}
