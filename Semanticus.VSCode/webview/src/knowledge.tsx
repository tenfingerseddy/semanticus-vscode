import { useEffect, useRef, useState, type ReactNode } from 'react';
import { onActivity, rpc } from './bridge';
import { mdToHtml } from './docrender';
import { DraftMark, EditMovedBanner, useEditBase } from './editbase';
import { heldDraft, pruneGone } from './drafts';
import { MenuPop } from './menupop';

interface PrimerDocument {
  modelName?: string | null; modelIdentity?: string | null; filePath?: string | null;
  markdown?: string | null; updatedUtc?: string | null; exists: boolean; note?: string | null;
}
interface PrimerSuggestion {
  id: string; section: string; markdown: string; capturedUtc?: string | null; origin?: string | null;
  sourceRunIds: string[]; evidenceCount: number; provenance: string; needsReview?: boolean;
}
interface PrimerSectionFreshness { section: string; suggestedAdditions: number; primerUpdatedUtc?: string | null; }
interface PrimerSuggestionList { isPro: boolean; suggestions: PrimerSuggestion[]; sections: PrimerSectionFreshness[]; note?: string | null; }
interface PrimerSuggestionDecision { changed: boolean; decision?: string | null; primer?: PrimerDocument | null; note?: string | null; }

interface InsightProvenance { when?: string; origin?: string; sessionId?: string; sourceRunIds?: string[]; schemaVersion?: number }
interface InsightRecord {
  id: string; text: string; kind: string; keys: string[]; fingerprint?: string | null;
  status: string; score: number; uses: number; retrievals: number; scope: string;
  lastUsedUtc?: string | null; provenance?: InsightProvenance | null; untrusted?: boolean; needsReview?: boolean;
}
interface InsightListResult { insights: InsightRecord[]; skippedCorruptLines: number; note?: string | null }
interface ModelFingerprint {
  tables: number; measures: number; columns: number; sourceTypes: string[];
  factTables: string[]; dimTables: string[]; namingHash: string; domainTokens: string[];
  grade?: number | null; fingerprintKey: string;
}
interface RecallCandidate { insight: InsightRecord; matchedKeys: string[]; fingerprintMatch: boolean; domainOverlap: number; rank: number; why: string }
interface RecallResult { candidates: RecallCandidate[]; fingerprint: ModelFingerprint; skippedCorruptLines: number; rankingNote?: string; note?: string | null }
interface PurgeResult { scope: string; liveCount: number; purged: boolean; note?: string }

const SECTIONS = ['Overview', 'Business context', 'Gotchas', 'Patterns', 'Known issues', 'History'];
const KIND_TINT: Record<string, string> = { insight: 'var(--sem-accent)', 'post-mortem': 'var(--sem-bad)' };
const errMsg = (e: unknown) => String((e as Error).message ?? e);
const fmtWhen = (iso?: string | null) => {
  if (!iso) return '';
  const d = new Date(iso);
  return isNaN(d.getTime()) ? iso : d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
};
// Why a lesson carries the Not reviewed tag (UAT-S06a-1-002): the assistant wrote it and no person has approved it.
const NOT_REVIEWED_TITLE = 'Your assistant wrote this. It is treated as a note, not as guidance, until you approve it.';
// The engine starter puts the model's name as a first title in the notes, and the page already has that title.
const withoutTitle = (md: string) => md.replace(/^\s*#[ \t][^\r\n]*(\r?\n)*/, '');
// With the title gone the first section heading leads the card, so it keeps no rule or gap above it (every later one does).
const firstHeadingFlush = (html: string) => html.replace(/^\s*<h2(?=[\s>])/, '<h2 style="margin-top:0;padding-top:0;border-top:0"');
const scopeLabel = (scope: string) => scope === 'global' ? 'all models' : 'this model';

export function KnowledgeView({ onSuggestionsChanged }: { onSuggestionsChanged?: () => void } = {}) {
  const [doc, setDoc] = useState<PrimerDocument | null>(null);
  // Notes typed and not saved outlive this page (a tab switch, a restarted engine, a reload): it opens on them (the drafts class, drafts.ts).
  const held = heldDraft<string>('notes|primer');
  const [draft, setDraft] = useState(held?.value ?? '');
  const [editing, setEditing] = useState(!!held);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [suggestions, setSuggestions] = useState<PrimerSuggestionList | null>(null);
  const [deciding, setDeciding] = useState<string | null>(null);
  const [list, setList] = useState<InsightListResult | null>(null);
  const [listErr, setListErr] = useState<string | null>(null);
  const dirty = !!doc && draft !== (doc.markdown ?? '');

  const editingRef = useRef(editing); editingRef.current = editing;
  // While the notes are open for editing the draft belongs to the person; the guard below decides what a newer
  // version of the notes does to it (follow it, or ask), so a reload here only replaces the draft when not editing.
  const loadPrimer = () => Promise.all([rpc<PrimerDocument>('getPrimer'), rpc<PrimerSuggestionList>('listPrimerSuggestions')]).then(([next, proposed]) => {
    setDoc(next); if (!editingRef.current) setDraft(next.markdown ?? ''); setSuggestions(proposed); setError(null);
  }).catch((e) => setError(errMsg(e)));
  const primerGuard = useEditBase<string>({
    current: doc ? (doc.markdown ?? '') : undefined, draft, editing, reseed: setDraft, persist: { scope: 'notes|primer' },
    readNow: async () => (await rpc<PrimerDocument>('getPrimer')).markdown ?? '',
  });
  const loadInsights = () => rpc<InsightListResult>('listInsights').then((r) => { setList(r); setListErr(null); }).catch((e) => setListErr(errMsg(e)));
  useEffect(() => { void loadPrimer(); void loadInsights(); }, []);
  // A lesson that is gone takes the words typed for it (the drafts class; a draft is never shown on another lesson).
  useEffect(() => { if (list) pruneGone('insight|', list.insights.map((i) => i.id)); }, [list]);
  useEffect(() => onActivity((e) => {
    const kind = typeof e.kind === 'string' ? e.kind : '';
    if (/primer/i.test(kind)) void loadPrimer();
    if (/insight|knowledge/i.test(kind)) void loadInsights();
  }), []);

  const save = async () => {
    setSaving(true); setError(null);
    try {
      if (!(await primerGuard.beforeSave())) return;
      // The write names the notes the Save read as the ones it replaces; the engine compares them inside its own write.
      let next: PrimerDocument;
      try { next = await rpc<PrimerDocument>('setPrimer', draft, 'human', primerGuard.confirmed() ?? ''); }
      catch (e) { if (await primerGuard.refused(e)) return; throw e; }
      setDoc(next);
      // R-B: the notes close, and the draft ends, only when the words typed are still exactly the ones the engine took; words typed while the Save was on its way stay held, over the saved notes.
      if (primerGuard.saved(draft, next.markdown ?? draft)) { setDraft(next.markdown ?? draft); setEditing(false); }
    }
    catch (e) { setError(errMsg(e)); }
    finally { setSaving(false); }
  };
  const decide = async (suggestion: PrimerSuggestion, accept: boolean) => {
    setDeciding(suggestion.id); setError(null);
    try {
      const result = await rpc<PrimerSuggestionDecision>(accept ? 'acceptPrimerSuggestion' : 'rejectPrimerSuggestion', suggestion.id, 'human');
      if (result.primer) { setDoc(result.primer); if (!editingRef.current) setDraft(result.primer.markdown ?? ''); }   // notes open for editing keep the person's words (R-B)
      await loadPrimer();
      onSuggestionsChanged?.();   // the count on the Notes tab follows the decision
    } catch (e) { setError(errMsg(e)); }
    finally { setDeciding(null); }
  };
  const updated = doc?.updatedUtc ? new Date(doc.updatedUtc) : null;
  const provenanceFor = (suggestion: PrimerSuggestion) => {
    const captured = suggestion.capturedUtc ? new Date(suggestion.capturedUtc) : null;
    const source = suggestion.sourceRunIds.length ? `Observed in ${suggestion.sourceRunIds.length} source run${suggestion.sourceRunIds.length === 1 ? '' : 's'}` : 'Captured learning';
    return captured && !isNaN(captured.getTime()) ? `${source} · ${captured.toLocaleString()}` : source;
  };
  const insights = list?.insights ?? [];
  // 'unreviewed' lessons are in recall (as untrusted notes), so they list with the approved ones, tagged Not reviewed.
  const approved = insights.filter((i) => i.status === 'approved' || i.status === 'unreviewed');
  const pending = insights.filter((i) => i.status === 'pending');

  return (
    <div className="h-full overflow-auto">
      <div className="sem-page xe-under-bar">
        <header className="sem-card">
          <div className="flex items-start gap-4">
            <div className="min-w-0 flex-1">
              <h1 className="m-0 text-(length:--sem-text-page) font-semibold">{doc?.modelName ? `${doc.modelName} notes` : 'Model notes'}</h1>
              {/* One quiet line: the date. Where the file lives is in the tooltip, not on the page. */}
              <div className="mt-1 sem-text-meta" title={`Saved beside this model${doc?.filePath ? ': ' + doc.filePath : ''}`}>
                {!doc?.exists ? 'Starter notes, not saved yet' : updated && !isNaN(updated.getTime()) ? `Updated ${updated.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })}` : 'Saved beside this model'}
              </div>
            </div>
            <div className="flex shrink-0 items-center gap-2">
              {editing ? <>
                <DraftMark show={primerGuard.drafted} />
                <button onClick={() => { setDraft(doc?.markdown ?? ''); setEditing(false); setError(null); }} disabled={saving} className="sem-btn sem-btn-sm">Cancel</button>
                <button onClick={() => void save()} disabled={saving || !dirty} className="sem-btn sem-btn-sm sem-btn-primary">{saving ? 'Saving…' : 'Save notes'}</button>
              </> : <button onClick={() => { setDraft(doc?.markdown ?? ''); setEditing(true); }} disabled={!doc?.markdown} className="sem-btn sem-btn-sm sem-btn-primary">Edit notes</button>}
            </div>
          </div>
        </header>

        {error && <Message color="var(--sem-bad)">{error}</Message>}
        {doc?.note && <Message color="var(--sem-warn)">{doc.note}</Message>}
        {suggestions?.note && <Message color={suggestions.isPro ? 'var(--sem-muted)' : 'var(--sem-warn)'}>{suggestions.note}</Message>}
        {!editing && !!suggestions?.suggestions?.length && <section className="sem-card">
          <div className="sem-card-title xe-dot xe-dot-warn mb-3">{suggestions.suggestions.length === 1 ? 'Suggested update' : 'Suggested updates'}</div>
          <div className="grid">
            {suggestions.suggestions.map((suggestion) => <article key={suggestion.id} className="xe-rule-item" title={provenanceFor(suggestion)}>
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-2 text-(length:--sem-text-label) font-semibold">{suggestion.section}{suggestion.needsReview && <Pill tint="var(--sem-warn)" title={NOT_REVIEWED_TITLE}>Not reviewed</Pill>}</div>
                  <div className="mt-1 whitespace-pre-wrap text-xs leading-5">{suggestion.markdown.split('\n')[0].replace(/^[-*]\s*/, '')}</div>
                </div>
                <div className="flex shrink-0 gap-2">
                  <button onClick={() => void decide(suggestion, false)} disabled={!!deciding} className="sem-btn sem-btn-sm">Reject</button>
                  <button onClick={() => void decide(suggestion, true)} disabled={!!deciding} className="sem-btn sem-btn-sm sem-btn-primary">{deciding === suggestion.id ? 'Saving…' : 'Accept'}</button>
                </div>
              </div>
            </article>)}
          </div>
        </section>}
        {editing && primerGuard.moved && <EditMovedBanner what="note" theirs={primerGuard.theirs} onKeepMine={primerGuard.keepMine} onUseNew={primerGuard.useNew} disabled={saving} />}
        {!doc ? <div className="sem-card sem-text-meta">Loading the model notes…</div>
          : editing ? <section className="grid min-h-[420px] gap-3 lg:grid-cols-2">
              <textarea value={draft} onChange={(e) => setDraft(e.target.value)} spellCheck={false} aria-label="Model notes, written in Markdown"
                rows={24} className="sem-field w-full resize-y sem-num" />
              <article className="sem-primer sem-card" dangerouslySetInnerHTML={{ __html: mdToHtml(withoutTitle(draft)) }} />
            </section>
          : <article className="sem-primer sem-card min-h-[320px]" dangerouslySetInnerHTML={{ __html: firstHeadingFlush(mdToHtml(withoutTitle(doc.markdown ?? ''))) }} />}

        <LessonsFold approved={approved} pending={pending} loaded={!!list} error={listErr} unreadable={list?.skippedCorruptLines ?? 0} onChanged={loadInsights} />
      </div>
    </div>
  );
}

// The assistant writes, recalls and clears these lessons itself (over MCP), so the person's controls sit in ONE
// closed line below the notes. Everything still works inside it. The one thing that must not hide is a loss
// signal: stored lines that could not be read keep a warning on the closed line.
function LessonsFold({ approved, pending, loaded, error, unreadable, onChanged }: {
  approved: InsightRecord[]; pending: InsightRecord[]; loaded: boolean; error: string | null; unreadable: number; onChanged: () => void;
}) {
  const [open, setOpen] = useState(false);
  const count = approved.length + pending.length;
  return (
    <section id="knowledge-learned" className="sem-card">
      <button type="button" aria-expanded={open} onClick={() => setOpen((o) => !o)} className="flex w-full items-center gap-2 text-left">
        <span aria-hidden className="inline-block w-3 text-(length:--sem-text-label) transition-transform" style={{ color: 'var(--sem-muted)', transform: open ? 'rotate(90deg)' : 'none' }}>▶</span>
        <span className="sem-card-title">What your assistant learned{loaded ? <span className="sem-num sem-text-meta"> ({count})</span> : null}</span>
        {!open && (error || unreadable > 0) && <span className="xe-dot xe-dot-warn sem-text-meta">{error ? 'Could not be read' : `${unreadable} stored line${unreadable === 1 ? '' : 's'} could not be read`}</span>}
      </button>
      {open && (
        <div className="mt-3 sem-stack">
          {error && <Message color="var(--sem-bad)">{error}</Message>}
          {unreadable > 0 && <Message color="var(--sem-warn)">{unreadable} stored line{unreadable === 1 ? '' : 's'} could not be read. The rest still loaded.</Message>}
          <div id="knowledge-insights">
            {!loaded ? (
              <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>Loading…</div>
            ) : count === 0 ? (
              <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>No lessons are saved yet. Ask your assistant to save a useful business rule or lesson from your work.</div>
            ) : (
              <div className="flex flex-col">
                {approved.map((i) => <InsightCard key={i.id} rec={i} onChanged={onChanged} />)}
                {pending.map((i) => <InsightCard key={i.id} rec={i} pending onChanged={onChanged} />)}
              </div>
            )}
          </div>
          <RecallSection />
          <PurgeSection onPurged={onChanged} />
        </div>
      )}
    </section>
  );
}

export function InsightCard({ rec, pending, onChanged }: { rec: InsightRecord; pending?: boolean; onChanged: () => void }) {
  // A lesson being edited and not saved is held above this card (drafts.ts): the card opens on it again after a tab switch, a restart or a reload.
  const heldWords = heldDraft<{ text: string; keys: string }>(`insight|${rec.id}`);
  const [editing, setEditing] = useState(!!heldWords);
  const [text, setText] = useState(heldWords?.value.text ?? rec.text);
  const [keys, setKeys] = useState(heldWords?.value.keys ?? (rec.keys ?? []).join(', '));
  const [showProv, setShowProv] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const run = async (p: Promise<unknown>): Promise<boolean> => {
    setBusy(true); setErr(null);
    try { await p; onChanged(); return true; } catch (e) { setErr(errMsg(e)); return false; } finally { setBusy(false); }
  };
  // The editor starts from what is live when Edit is pressed, follows it while nothing is typed, and Save reads the
  // lesson again first: the assistant can reword it (edit_insight) while this card is open (UAT-F-S06a-1-002).
  const fields = (r: Pick<InsightRecord, 'text' | 'keys'>) => ({ text: r.text, keys: (r.keys ?? []).join(', ') });
  const guard = useEditBase({
    current: fields(rec), draft: { text, keys }, editing, persist: { scope: `insight|${rec.id}` },
    reseed: (v) => { setText(v.text); setKeys(v.keys); },
    readNow: async () => { const found = (await rpc<InsightListResult>('listInsights')).insights.find((i) => i.id === rec.id); return found ? fields(found) : null; },
  });
  const openEditor = () => { const live = fields(rec); setText(live.text); setKeys(live.keys); setEditing(true); };
  const saveEdit = async () => {
    if (!(await guard.beforeSave())) return;
    const newKeys = keys.split(',').map((k) => k.trim()).filter(Boolean);
    // A failed save keeps the editor open with what was typed; closing it would throw the words away.
    // The write names the lesson the Save read as the one it replaces; the engine compares it inside its own write.
    const was = guard.confirmed();
    const expected = JSON.stringify({ text: was?.text ?? '', keys: (was?.keys ?? '').split(',').map((k) => k.trim()).filter(Boolean) });
    let asked = false;   // the engine refused because the lesson moved: the banner asks, and the editor stays open on the words
    const submitted = { text, keys };
    const ok = await run(rpc('editInsight', rec.id, text.trim(), newKeys, 'human', expected).catch(async (e) => { if (await guard.refused(e)) { asked = true; return null; } throw e; }));
    // R-B: the card closes, and the draft ends, only when the words it holds are still exactly the ones the engine took.
    if (ok && !asked && guard.saved(submitted)) setEditing(false);
  };

  const prov = rec.provenance;
  const [menu, setMenu] = useState(false);
  // The engine tags, the counters and the match keys are not on the row face. They sit in the tooltip.
  const facts = [rec.kind, scopeLabel(rec.scope), rec.fingerprint ? 'this model shape' : '', `score ${rec.score}`, `${rec.retrievals} retrievals`, `${rec.uses} uses`,
    (rec.keys ?? []).length ? 'keys: ' + rec.keys.join(', ') : ''].filter(Boolean).join(' · ');
  const notReviewed = !!pending || !!rec.needsReview || rec.status === 'unreviewed';
  return (
    <article className="xe-rule-item">
      <div className="flex items-start gap-2">
        <div className="min-w-0 flex-1">
          {editing ? (
            <textarea value={text} onChange={(e) => setText(e.target.value)} rows={3} spellCheck={false} aria-label="Insight text"
              className="w-full resize-y rounded-(--sem-radius-control) px-2 py-1 text-xs outline-none"
              style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
          ) : (
            <div className="text-xs" title={facts} style={{ color: 'var(--sem-fg)' }}>{rec.text}</div>
          )}
        </div>
        {!editing && notReviewed && <Pill tint="var(--sem-warn)" title={pending ? 'Pending approval: saved, but held out of recall until you approve it.' : NOT_REVIEWED_TITLE}>Not reviewed</Pill>}
      </div>
      {editing && (
        <input value={keys} onChange={(e) => setKeys(e.target.value)} placeholder="match keys, comma-separated" spellCheck={false} aria-label="Match keys"
          className="mt-2 w-full rounded-(--sem-radius-control) px-2 py-1 text-(length:--sem-text-label) outline-none tnum"
          style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
      )}
      {showProv && prov && (
        <div className="mt-2 sem-inset sem-text-meta">
          <span>by <b style={{ color: 'var(--sem-fg)' }}>{prov.origin === 'human' ? 'You' : 'Your assistant'}</b></span>
          {prov.when && <span> · {fmtWhen(prov.when)}</span>}
          {prov.sourceRunIds && prov.sourceRunIds.length > 0 && <span> · from {prov.sourceRunIds.length} run{prov.sourceRunIds.length === 1 ? '' : 's'}</span>}
        </div>
      )}
      {err && <div className="mt-2 text-(length:--sem-text-label) xe-dot xe-dot-bad">{err}</div>}
      {editing && guard.moved && <EditMovedBanner what="lesson" theirs={guard.theirs} onKeepMine={guard.keepMine} onUseNew={guard.useNew} disabled={busy} />}
      <div className="mt-2 flex flex-wrap items-center gap-2">
        {editing ? (
          <>
            <DraftMark show={guard.drafted} />
            <MiniBtn primary disabled={busy || !text.trim()} onClick={() => void saveEdit()}>Save</MiniBtn>
            <MiniBtn disabled={busy} onClick={() => { setEditing(false); setText(rec.text); setKeys((rec.keys ?? []).join(', ')); }}>Cancel</MiniBtn>
          </>
        ) : (
          <>
            {notReviewed && <MiniBtn primary disabled={busy} title="Trust this lesson" onClick={() => void run(rpc('approveInsight', rec.id, 'human'))}>Approve</MiniBtn>}
            {!pending && <MiniBtn disabled={busy} onClick={openEditor}>Edit</MiniBtn>}
            <span className="relative">
              <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" aria-label="More for this lesson" aria-haspopup="menu" aria-expanded={menu} disabled={busy} onClick={() => setMenu((o) => !o)}>⋯</button>
              {menu && <MenuPop onClose={() => setMenu(false)} items={[
                ...(pending ? [] : [
                  { label: 'Upvote', detail: 'Raises its importance.', act: () => void run(rpc('upvoteInsight', rec.id, 'human')) },
                  { label: 'Downvote', detail: 'Lowers it. At score 0 it drops out.', act: () => void run(rpc('downvoteInsight', rec.id, 'human')) },
                ]),
                ...(prov ? [{ label: showProv ? 'Hide source' : 'Show source', act: () => setShowProv((o) => !o) }] : []),
                { label: 'Delete', detail: 'Removes this lesson from use.', act: () => void run(rpc('deleteInsight', rec.id, 'human')) },
              ]} />}
            </span>
          </>
        )}
      </div>
    </article>
  );
}

function RecallSection() {
  const [query, setQuery] = useState('');
  const [result, setResult] = useState<RecallResult | null>(null);
  const [fp, setFp] = useState<ModelFingerprint | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => { rpc<ModelFingerprint>('getModelFingerprint').then(setFp).catch(() => setFp(null)); }, []);

  const recall = async () => {
    setBusy(true); setErr(null);
    try { const r = await rpc<RecallResult>('recallExperience', query.trim() || null, 12); setResult(r); if (r.fingerprint) setFp(r.fingerprint); }
    catch (e) { setErr(errMsg(e)); } finally { setBusy(false); }
  };
  // The model's shape is what recall matches against. It is a hint, not a block of figures, so it sits in a tooltip.
  const shape = fp ? `Model shape: ${fp.tables} tables, ${fp.measures} measures, ${fp.columns} columns. Recall matches lessons against it.` : 'Find lessons that fit your next task.';

  return (
    <div id="knowledge-recall" className="border-t pt-3" style={{ borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2" title={shape}>
        <input value={query} onChange={(e) => setQuery(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') void recall(); }}
          placeholder="Type what you're about to do" spellCheck={false} aria-label="Recall query"
          className="sem-field flex-1" />
        <button onClick={() => void recall()} disabled={busy} className="sem-btn">{busy ? 'Recalling…' : 'Recall'}</button>
      </div>
      {err && <div className="mt-2"><Message color="var(--sem-bad)">{err}</Message></div>}
      {result && (
        <div className="mt-3">
          {result.candidates.length === 0 ? (
            <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>No matching prior lessons for this model shape.</div>
          ) : (
            <div className="flex flex-col">{result.candidates.map((c) => <CandidateCard key={c.insight.id} c={c} />)}</div>
          )}
        </div>
      )}
    </div>
  );
}

function CandidateCard({ c }: { c: RecallCandidate }) {
  const i = c.insight;
  return (
    <article className="xe-rule-item">
      <div className="flex items-start gap-2">
        <div className="min-w-0 flex-1 text-xs" style={{ color: 'var(--sem-fg)' }}>{i.text}</div>
        <div className="flex shrink-0 items-center gap-2">
          <Pill tint={KIND_TINT[i.kind]}>{i.kind}</Pill>
          {c.fingerprintMatch && <Pill tint="var(--sem-accent)" title="Same model shape">shape</Pill>}
        </div>
      </div>
      {c.matchedKeys.length > 0 && (
        <div className="mt-2 flex flex-wrap items-center gap-1">
          {c.matchedKeys.map((k) => (
            <span key={k} className="sem-chip sem-chip-outline sem-num">{k}</span>
          ))}
        </div>
      )}
      <div className="mt-2 text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>{c.why}</div>
    </article>
  );
}

function PurgeSection({ onPurged }: { onPurged: () => void }) {
  const [scope, setScope] = useState('project');
  const [report, setReport] = useState<PurgeResult | null>(null);
  const [phase, setPhase] = useState<'idle' | 'review' | 'done'>('idle');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const call = async (confirm: boolean) => {
    setBusy(true); setErr(null);
    try {
      const r = await rpc<PurgeResult>('purgeKnowledge', scope, confirm, 'human');
      setReport(r); setPhase(confirm ? 'done' : 'review');
      if (confirm) onPurged();
    } catch (e) { setErr(errMsg(e)); } finally { setBusy(false); }
  };
  const reset = () => { setPhase('idle'); setReport(null); setErr(null); };

  return (
    <div id="knowledge-purge" className="border-t pt-3" style={{ borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2">
        <span className="text-xs font-semibold">Delete saved lessons</span>
        <select value={scope} onChange={(e) => { setScope(e.target.value); reset(); }} disabled={phase !== 'idle'} aria-label="Delete scope"
          className="rounded-(--sem-radius-control) px-2 py-1 text-xs outline-none"
          style={{ background: 'var(--sem-ground)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
          <option value="project">this model</option>
          <option value="global">all models</option>
        </select>
        {phase === 'idle' && <MiniBtn disabled={busy} onClick={() => void call(false)}>{busy ? 'Checking…' : 'Delete…'}</MiniBtn>}
      </div>
      {err && <div className="mt-2"><Message color="var(--sem-bad)">{err}</Message></div>}
      {phase === 'review' && report && (
        <div className="mt-2 sem-inset">
          {/* The count is shown before anything is removed. The storage history stays, so this is honest about what "delete" means. */}
          <div className="text-xs" style={{ color: 'var(--sem-fg)' }}>
            This would delete {report.liveCount} lesson{report.liveCount === 1 ? '' : 's'} for {scopeLabel(scope)}. The storage history is kept. Confirm to continue.
          </div>
          <div className="mt-2 flex items-center gap-2">
            <button onClick={() => void call(true)} disabled={busy}
              className="rounded-(--sem-radius-card) px-3 py-1 text-xs font-medium disabled:opacity-40" style={{ background: 'var(--sem-bad)', color: '#fff' }}>
              {busy ? 'Deleting…' : `Delete ${report.liveCount} lesson${report.liveCount === 1 ? '' : 's'}`}
            </button>
            <MiniBtn disabled={busy} onClick={reset}>Cancel</MiniBtn>
          </div>
        </div>
      )}
      {phase === 'done' && report && (
        <div className="mt-2 sem-inset">
          <div className="text-xs xe-dot xe-dot-good">Deleted {report.liveCount} lesson{report.liveCount === 1 ? '' : 's'} for {scopeLabel(scope)}.</div>
          <div className="mt-2"><MiniBtn onClick={reset}>Done</MiniBtn></div>
        </div>
      )}
    </div>
  );
}

// A kind or a state: a word with a small dot in its colour, never a coloured pill (Instrument).
function Pill({ children, tint, title }: { children: ReactNode; tint?: string; title?: string }) {
  return (
    <span title={title} className={'sem-chip' + (tint ? ' sem-chip-dot' : '')} style={tint ? { '--xe-dot': tint } as React.CSSProperties : undefined}>
      {children}
    </span>
  );
}
function MiniBtn({ children, onClick, disabled, primary, title }: { children: ReactNode; onClick?: () => void; disabled?: boolean; primary?: boolean; title?: string }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className={primary ? 'sem-btn sem-btn-sm sem-btn-primary' : 'sem-btn sem-btn-sm'}>
      {children}
    </button>
  );
}
// A message is a sentence with a coloured dot, not coloured text in a tinted box (Instrument).
function Message({ children, color }: { children: ReactNode; color: string }) {
  return <div className="xe-msg" style={{ '--xe-dot': color } as React.CSSProperties}>{children}</div>;
}
