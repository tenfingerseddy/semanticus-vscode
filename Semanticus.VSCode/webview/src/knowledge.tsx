import { useEffect, useRef, useState } from 'react';
import { onActivity, rpc } from './bridge';
import { mdToHtml } from './docrender';

interface PrimerDocument {
  modelName?: string | null; modelIdentity?: string | null; filePath?: string | null;
  markdown?: string | null; updatedUtc?: string | null; exists: boolean; note?: string | null;
}
interface PrimerSuggestion {
  id: string; section: string; markdown: string; capturedUtc?: string | null; origin?: string | null;
  sourceRunIds: string[]; evidenceCount: number; provenance: string;
}
interface PrimerSectionFreshness { section: string; suggestedAdditions: number; primerUpdatedUtc?: string | null; }
interface PrimerSuggestionList { isPro: boolean; suggestions: PrimerSuggestion[]; sections: PrimerSectionFreshness[]; note?: string | null; }
interface PrimerSuggestionDecision { changed: boolean; decision?: string | null; primer?: PrimerDocument | null; note?: string | null; }

const SECTIONS = ['Overview', 'Business context', 'Gotchas', 'Patterns', 'Known issues', 'History'];
const errMsg = (e: unknown) => String((e as Error).message ?? e);

export function KnowledgeView(_props: { onOpenWorkflows?: () => void }) {
  const [doc, setDoc] = useState<PrimerDocument | null>(null);
  const [draft, setDraft] = useState('');
  const [editing, setEditing] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [suggestions, setSuggestions] = useState<PrimerSuggestionList | null>(null);
  const [deciding, setDeciding] = useState<string | null>(null);
  const dirty = !!doc && draft !== (doc.markdown ?? '');
  const dirtyRef = useRef(dirty); dirtyRef.current = dirty;

  const load = () => Promise.all([rpc<PrimerDocument>('getPrimer'), rpc<PrimerSuggestionList>('listPrimerSuggestions')]).then(([next, proposed]) => {
    setDoc(next); setDraft(next.markdown ?? ''); setSuggestions(proposed); setError(null);
  }).catch((e) => setError(errMsg(e)));
  useEffect(() => { void load(); }, []);
  useEffect(() => onActivity((e) => {
    if ((e.kind === 'set_model_primer' || e.kind === 'accept_primer_suggestion' || e.kind === 'reject_primer_suggestion') && !dirtyRef.current) void load();
  }), []);

  const save = async () => {
    setSaving(true); setError(null);
    try { const next = await rpc<PrimerDocument>('setPrimer', draft, 'human'); setDoc(next); setDraft(next.markdown ?? draft); setEditing(false); }
    catch (e) { setError(errMsg(e)); }
    finally { setSaving(false); }
  };
  const decide = async (suggestion: PrimerSuggestion, accept: boolean) => {
    setDeciding(suggestion.id); setError(null);
    try {
      const result = await rpc<PrimerSuggestionDecision>(accept ? 'acceptPrimerSuggestion' : 'rejectPrimerSuggestion', suggestion.id, 'human');
      if (result.primer) { setDoc(result.primer); setDraft(result.primer.markdown ?? ''); }
      await load();
    } catch (e) { setError(errMsg(e)); }
    finally { setDeciding(null); }
  };
  const updated = doc?.updatedUtc ? new Date(doc.updatedUtc) : null;
  const pendingFor = (section: string) => suggestions?.sections?.find((x) => x.section === section)?.suggestedAdditions ?? 0;
  const provenanceFor = (suggestion: PrimerSuggestion) => {
    const captured = suggestion.capturedUtc ? new Date(suggestion.capturedUtc) : null;
    const source = suggestion.sourceRunIds.length ? `Observed in ${suggestion.sourceRunIds.length} source run${suggestion.sourceRunIds.length === 1 ? '' : 's'}` : 'Captured learning';
    return captured && !isNaN(captured.getTime()) ? `${source} · ${captured.toLocaleString()}` : source;
  };

  return (
    <div className="h-full overflow-auto">
      <div className="sem-evidence-page flex min-w-0 flex-col gap-4">
        <header className="rounded-xl border p-5" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
          <div className="flex items-start gap-4">
            <div className="min-w-0 flex-1">
              <div className="text-[10px] font-semibold uppercase tracking-[0.16em]" style={{ color: 'var(--sem-accent)' }}>Model orientation</div>
              <h1 className="m-0 mt-1 text-[22px] font-semibold">{doc?.modelName ? `${doc.modelName} Primer` : 'Model Primer'}</h1>
              <p className="m-0 mt-1 max-w-[820px] text-[12px]" style={{ color: 'var(--sem-muted)' }}>
                The one declared guide for people and the AI Assistant: what this model means, how to work with it, and what to watch.
              </p>
              <div className="mt-3 flex flex-wrap gap-1.5">
                {SECTIONS.map((section) => <span key={section} className="rounded-full border px-2 py-0.5 text-[10px]" style={{ borderColor: pendingFor(section) ? 'var(--sem-warn)' : 'var(--sem-border)', color: pendingFor(section) ? 'var(--sem-warn)' : 'var(--sem-muted)' }}>{section}{pendingFor(section) ? ` · ${pendingFor(section)} suggested` : ''}</span>)}
              </div>
            </div>
            <div className="flex shrink-0 items-center gap-2">
              {editing ? <>
                <button onClick={() => { setDraft(doc?.markdown ?? ''); setEditing(false); setError(null); }} disabled={saving} className="rounded-md border px-3 py-1.5 text-[11px]" style={{ borderColor: 'var(--sem-border)' }}>Cancel</button>
                <button onClick={() => void save()} disabled={saving || !dirty} className="rounded-md px-3 py-1.5 text-[11px] font-semibold disabled:opacity-45" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{saving ? 'Saving…' : 'Save Primer'}</button>
              </> : <button onClick={() => setEditing(true)} disabled={!doc?.markdown} className="rounded-md px-3 py-1.5 text-[11px] font-semibold disabled:opacity-45" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Edit Markdown</button>}
            </div>
          </div>
          <div className="mt-4 flex flex-wrap items-center gap-x-4 gap-y-1 border-t pt-3 text-[10.5px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
            <span>{doc?.exists ? 'Saved beside this model' : 'Starter document, not saved yet'}</span>
            {updated && !isNaN(updated.getTime()) && <span>Updated {updated.toLocaleString()}</span>}
            {doc?.filePath && <span className="min-w-0 truncate font-mono" title={doc.filePath}>{doc.filePath}</span>}
          </div>
        </header>

        {error && <Message color="var(--sem-bad)">{error}</Message>}
        {doc?.note && <Message color="var(--sem-warn)">{doc.note}</Message>}
        {suggestions?.note && <Message color={suggestions.isPro ? 'var(--sem-muted)' : 'var(--sem-warn)'}>{suggestions.note}</Message>}
        {!editing && !!suggestions?.suggestions?.length && <section className="rounded-xl border p-4" style={{ borderColor: 'var(--sem-warn)', background: 'var(--sem-surface)' }}>
          <div className="mb-3">
            <div className="text-[10px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--sem-warn)' }}>Suggested updates</div>
            <p className="m-0 mt-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Captured learning never changes the Primer until you accept the exact addition.</p>
          </div>
          <div className="grid gap-2">
            {suggestions.suggestions.map((suggestion) => <article key={suggestion.id} className="rounded-lg border p-3" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-bg)' }}>
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="text-[10px] font-semibold" style={{ color: 'var(--sem-accent)' }}>{suggestion.section}</div>
                  <div className="mt-1 whitespace-pre-wrap text-[11.5px] leading-5">{suggestion.markdown.split('\n')[0].replace(/^[-*]\s*/, '')}</div>
                  <div className="mt-2 text-[9.5px]" style={{ color: 'var(--sem-muted)' }}>{provenanceFor(suggestion)}</div>
                </div>
                <div className="flex shrink-0 gap-1.5">
                  <button onClick={() => void decide(suggestion, false)} disabled={!!deciding} className="rounded-md border px-2 py-1 text-[10px] disabled:opacity-45" style={{ borderColor: 'var(--sem-border)' }}>Reject</button>
                  <button onClick={() => void decide(suggestion, true)} disabled={!!deciding} className="rounded-md px-2 py-1 text-[10px] font-semibold disabled:opacity-45" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{deciding === suggestion.id ? 'Saving…' : 'Accept'}</button>
                </div>
              </div>
            </article>)}
          </div>
        </section>}
        {!doc ? <div className="rounded-xl border p-8 text-[12px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>Loading the model Primer…</div>
          : editing ? <section className="grid min-h-[620px] gap-3 lg:grid-cols-[minmax(0,1fr)_minmax(360px,0.8fr)]">
              <textarea value={draft} onChange={(e) => setDraft(e.target.value)} spellCheck={false} aria-label="Primer Markdown"
                className="min-h-[620px] w-full resize-y rounded-xl border p-5 text-[12px] outline-none"
                style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)', color: 'var(--sem-fg)', fontFamily: 'ui-monospace,SFMono-Regular,Consolas,monospace' }} />
              <article className="sem-primer rounded-xl border p-6" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }} dangerouslySetInnerHTML={{ __html: mdToHtml(draft) }} />
            </section>
          : <article className="sem-primer min-h-[620px] rounded-xl border px-[clamp(24px,5vw,76px)] py-10" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }} dangerouslySetInnerHTML={{ __html: mdToHtml(doc.markdown ?? '') }} />}
      </div>
    </div>
  );
}

function Message({ children, color }: { children: React.ReactNode; color: string }) {
  return <div className="rounded-lg border px-3 py-2 text-[12px]" style={{ color, borderColor: `color-mix(in srgb, ${color} 45%, transparent)`, background: `color-mix(in srgb, ${color} 10%, transparent)` }}>{children}</div>;
}
