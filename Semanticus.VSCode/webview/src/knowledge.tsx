import { useEffect, useRef, useState, type ReactNode } from 'react';
import { onActivity, rpc } from './bridge';
import { PathDisclosure } from './ui';
import { mdToHtml } from './docrender';
import type { WorkflowDef, WorkflowInfo } from './workflows';

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

interface InsightProvenance { when?: string; origin?: string; sessionId?: string; sourceRunIds?: string[]; schemaVersion?: number }
interface InsightRecord {
  id: string; text: string; kind: string; keys: string[]; fingerprint?: string | null;
  status: string; score: number; uses: number; retrievals: number; scope: string;
  lastUsedUtc?: string | null; provenance?: InsightProvenance | null;
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
interface CheckFinding { severity: string; message: string }
interface WorkflowCheckReport { name: string; parseError?: string | null; findings: CheckFinding[]; ok: boolean }
interface WorkflowReplayRow { step: string; op: string; outcome: string; detail: string; wouldSucceed?: boolean | null; deltaCount: number }
interface WorkflowReplayReport {
  name: string; parseError?: string | null; admissionFindings: CheckFinding[]; exemplarRun?: string | null;
  replaySkipped: boolean; rows: WorkflowReplayRow[]; rehearsed: number; rehearsedFailed: number;
  skippedDenied: number; skippedUnbindable: number; replayable: number; admissible: boolean; note?: string | null;
}

const SECTIONS = ['Overview', 'Business context', 'Gotchas', 'Patterns', 'Known issues', 'History'];
const KIND_TINT: Record<string, string> = { insight: 'var(--sem-accent)', 'post-mortem': 'var(--sem-bad)' };
const REPLAY_TINT: Record<string, string> = {
  rehearsed: 'var(--sem-good)', 'skipped-denied': 'var(--sem-muted)',
  'skipped-unbindable': 'var(--sem-warn)', replayable: 'var(--sem-accent)', 'verify-skipped': 'var(--sem-muted)',
};
const errMsg = (e: unknown) => String((e as Error).message ?? e);
const fmtWhen = (iso?: string | null) => {
  if (!iso) return '';
  const d = new Date(iso);
  return isNaN(d.getTime()) ? iso : d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
};
const scopeLabel = (scope: string) => scope === 'global' ? 'all models' : 'this model';

export function KnowledgeView({ onOpenWorkflows }: { onOpenWorkflows?: () => void }) {
  const [doc, setDoc] = useState<PrimerDocument | null>(null);
  const [draft, setDraft] = useState('');
  const [editing, setEditing] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [suggestions, setSuggestions] = useState<PrimerSuggestionList | null>(null);
  const [deciding, setDeciding] = useState<string | null>(null);
  const [list, setList] = useState<InsightListResult | null>(null);
  const [listErr, setListErr] = useState<string | null>(null);
  const dirty = !!doc && draft !== (doc.markdown ?? '');
  const dirtyRef = useRef(dirty); dirtyRef.current = dirty;

  const loadPrimer = () => Promise.all([rpc<PrimerDocument>('getPrimer'), rpc<PrimerSuggestionList>('listPrimerSuggestions')]).then(([next, proposed]) => {
    setDoc(next); setDraft(next.markdown ?? ''); setSuggestions(proposed); setError(null);
  }).catch((e) => setError(errMsg(e)));
  const loadInsights = () => rpc<InsightListResult>('listInsights').then((r) => { setList(r); setListErr(null); }).catch((e) => setListErr(errMsg(e)));
  useEffect(() => { void loadPrimer(); void loadInsights(); }, []);
  useEffect(() => onActivity((e) => {
    const kind = typeof e.kind === 'string' ? e.kind : '';
    if (/primer/i.test(kind) && !dirtyRef.current) void loadPrimer();
    if (/insight|knowledge/i.test(kind)) void loadInsights();
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
      await loadPrimer();
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
  const insights = list?.insights ?? [];
  const approved = insights.filter((i) => i.status === 'approved');
  const pending = insights.filter((i) => i.status === 'pending');

  return (
    <div className="h-full overflow-auto">
      <div className="sem-evidence-page flex min-w-0 flex-col gap-4">
        <header className="rounded-xl border p-5" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
          <div className="flex items-start gap-4">
            <div className="min-w-0 flex-1">
              <div className="text-[10px] font-semibold uppercase tracking-[0.16em]" style={{ color: 'var(--sem-accent)' }}>Model orientation</div>
              <h1 className="m-0 mt-1 text-[22px] font-semibold">{doc?.modelName ? `${doc.modelName} notes` : 'Model notes'}</h1>
              <div className="mt-3 flex flex-wrap gap-1.5">
                {SECTIONS.map((section) => <span key={section} className="rounded-full border px-2 py-0.5 text-[10px]" style={{ borderColor: pendingFor(section) ? 'var(--sem-warn)' : 'var(--sem-border)', color: pendingFor(section) ? 'var(--sem-warn)' : 'var(--sem-muted)' }}>{section}{pendingFor(section) ? ` · ${pendingFor(section)} suggested` : ''}</span>)}
              </div>
              <nav className="mt-3 flex flex-wrap gap-2 text-[11px]">
                <Jump href="#knowledge-insights">Insights{approved.length || pending.length ? ` · ${approved.length}` : ''}</Jump>
                <Jump href="#knowledge-learned">Learned workflows</Jump>
                <Jump href="#knowledge-recall">Recall</Jump>
                <Jump href="#knowledge-purge" destructive>Delete saved knowledge…</Jump>
              </nav>
            </div>
            <div className="flex shrink-0 items-center gap-2">
              {editing ? <>
                <button onClick={() => { setDraft(doc?.markdown ?? ''); setEditing(false); setError(null); }} disabled={saving} className="rounded-md border px-3 py-1.5 text-[11px]" style={{ borderColor: 'var(--sem-border)' }}>Cancel</button>
                <button onClick={() => void save()} disabled={saving || !dirty} className="rounded-md px-3 py-1.5 text-[11px] font-semibold disabled:opacity-45" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{saving ? 'Saving…' : 'Save notes'}</button>
              </> : <button onClick={() => setEditing(true)} disabled={!doc?.markdown} className="rounded-md px-3 py-1.5 text-[11px] font-semibold disabled:opacity-45" style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Edit notes</button>}
            </div>
          </div>
          <div className="mt-4 flex flex-wrap items-center gap-x-4 gap-y-1 border-t pt-3 text-[10.5px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>
            <span>{doc?.exists ? 'Saved beside this model' : 'Starter document, not saved yet'}</span>
            {updated && !isNaN(updated.getTime()) && <span>Updated {updated.toLocaleString()}</span>}
            <PathDisclosure path={doc?.filePath} />
          </div>
        </header>

        {error && <Message color="var(--sem-bad)">{error}</Message>}
        {doc?.note && <Message color="var(--sem-warn)">{doc.note}</Message>}
        {suggestions?.note && <Message color={suggestions.isPro ? 'var(--sem-muted)' : 'var(--sem-warn)'}>{suggestions.note}</Message>}
        {listErr && <Message color="var(--sem-bad)">{listErr}</Message>}
        {list && list.skippedCorruptLines > 0 && (
          <Message color="var(--sem-warn)">
            {list.skippedCorruptLines} stored line{list.skippedCorruptLines === 1 ? '' : 's'} could not be read. The rest still loaded.
          </Message>
        )}
        {!editing && !!suggestions?.suggestions?.length && <section className="rounded-xl border p-4" style={{ borderColor: 'var(--sem-warn)', background: 'var(--sem-surface)' }}>
          <div className="mb-3">
            <div className="text-[10px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--sem-warn)' }}>Suggested updates</div>
            <p className="m-0 mt-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Review each suggested addition. It is added to these notes only when you select Accept.</p>
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
        {!doc ? <div className="rounded-xl border p-8 text-[12px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)' }}>Loading the model notes…</div>
          : editing ? <section className="grid min-h-[420px] gap-3 lg:grid-cols-[minmax(0,1fr)_minmax(360px,0.8fr)]">
              <textarea value={draft} onChange={(e) => setDraft(e.target.value)} spellCheck={false} aria-label="Model notes, written in Markdown"
                className="min-h-[420px] w-full resize-y rounded-xl border p-5 text-[12px] outline-none"
                style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)', color: 'var(--sem-fg)', fontFamily: 'ui-monospace,SFMono-Regular,Consolas,monospace' }} />
              <article className="sem-primer rounded-xl border p-6" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }} dangerouslySetInnerHTML={{ __html: mdToHtml(draft) }} />
            </section>
          : <article className="sem-primer min-h-[320px] rounded-xl border px-[clamp(24px,5vw,76px)] py-10" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }} dangerouslySetInnerHTML={{ __html: mdToHtml(doc.markdown ?? '') }} />}

        <InsightsSection approved={approved} pending={pending} loaded={!!list} onChanged={loadInsights} />
        <LearnedWorkflowsSection onOpenWorkflows={onOpenWorkflows} />
        <RecallSection />
        <PurgeSection onPurged={loadInsights} />
      </div>
    </div>
  );
}

function InsightsSection({ approved, pending, loaded, onChanged }: {
  approved: InsightRecord[]; pending: InsightRecord[]; loaded: boolean; onChanged: () => void;
}) {
  return (
    <section id="knowledge-insights" className="rounded-xl border p-4" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      <div className="text-[10px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--sem-accent)' }}>Insights</div>
      <p className="m-0 mt-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
        Save useful lessons from earlier work. Upvote and downvote change their importance. Edit changes the text and search terms. Delete removes a lesson from use.
      </p>
      {!loaded ? (
        <div className="mt-3 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading insights…</div>
      ) : approved.length === 0 && pending.length === 0 ? (
        <div className="mt-3 text-[12px]" style={{ color: 'var(--sem-muted)' }}>No lessons are saved yet. Ask your assistant to save a useful business rule or lesson from your work.</div>
      ) : (
        <div className="mt-3 flex flex-col gap-2">{approved.map((i) => <InsightCard key={i.id} rec={i} onChanged={onChanged} />)}</div>
      )}
      {pending.length > 0 && (
        <div className="mt-4">
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--sem-warn)' }}>
            Pending approval <span className="tnum">({pending.length})</span>
          </div>
          <p className="m-0 mt-1 mb-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
            These are saved but held out of recall until you approve them.
          </p>
          <div className="flex flex-col gap-2">{pending.map((i) => <InsightCard key={i.id} rec={i} pending onChanged={onChanged} />)}</div>
        </div>
      )}
    </section>
  );
}

function InsightCard({ rec, pending, onChanged }: { rec: InsightRecord; pending?: boolean; onChanged: () => void }) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(rec.text);
  const [keys, setKeys] = useState((rec.keys ?? []).join(', '));
  const [showProv, setShowProv] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const run = async (p: Promise<unknown>) => {
    setBusy(true); setErr(null);
    try { await p; onChanged(); } catch (e) { setErr(errMsg(e)); } finally { setBusy(false); }
  };
  const saveEdit = async () => {
    const newKeys = keys.split(',').map((k) => k.trim()).filter(Boolean);
    await run(rpc('editInsight', rec.id, text.trim(), newKeys, 'human'));
    setEditing(false);
  };

  const prov = rec.provenance;
  return (
    <article className="rounded-lg border p-3" style={{ background: 'var(--sem-bg)', borderColor: pending ? 'color-mix(in srgb, var(--sem-warn) 40%, transparent)' : 'var(--sem-border)' }}>
      <div className="flex items-start gap-2">
        <div className="min-w-0 flex-1">
          {editing ? (
            <textarea value={text} onChange={(e) => setText(e.target.value)} rows={3} spellCheck={false} aria-label="Insight text"
              className="w-full resize-y rounded-md px-2 py-1.5 text-[12.5px] outline-none"
              style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
          ) : (
            <div className="text-[12.5px]" style={{ color: 'var(--sem-fg)' }}>{rec.text}</div>
          )}
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          <Pill tint={KIND_TINT[rec.kind]}>{rec.kind}</Pill>
          <Pill title={rec.scope === 'global' ? 'Travels across all your models' : 'Beside this model'}>{scopeLabel(rec.scope)}</Pill>
          {rec.fingerprint && <Pill tint="var(--sem-accent)" title="Scoped to this model shape">shape</Pill>}
        </div>
      </div>
      {editing ? (
        <input value={keys} onChange={(e) => setKeys(e.target.value)} placeholder="match keys, comma-separated" spellCheck={false} aria-label="Match keys"
          className="mt-2 w-full rounded-md px-2 py-1 text-[11px] outline-none tnum"
          style={{ background: 'var(--sem-surface)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
      ) : (rec.keys ?? []).length > 0 ? (
        <div className="mt-1.5 flex flex-wrap items-center gap-1">
          {rec.keys.map((k) => (
            <span key={k} className="rounded px-1.5 py-0.5 text-[9.5px] tnum" style={{ background: 'var(--sem-surface)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>{k}</span>
          ))}
        </div>
      ) : null}
      <div className="mt-2 flex items-center gap-3 text-[10.5px] tnum" style={{ color: 'var(--sem-muted)' }}>
        <span title="Importance. A lesson drops out at 0.">score <b style={{ color: 'var(--sem-fg)' }}>{rec.score}</b></span>
        <span title="Times recall returned this lesson">retrievals {rec.retrievals}</span>
        <span title="Times this lesson was used on a run">uses {rec.uses}</span>
        {prov && (
          <button onClick={() => setShowProv((o) => !o)} className="flex items-center gap-1" style={{ color: 'var(--sem-muted)' }}>
            <span className="inline-block text-[9px] transition-transform" style={{ transform: showProv ? 'rotate(90deg)' : 'none' }}>▶</span>
            source
          </button>
        )}
      </div>
      {showProv && prov && (
        <div className="mt-1.5 rounded-md px-2 py-1.5 text-[11px]" style={{ background: 'var(--sem-surface)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>
          <span>by <b style={{ color: 'var(--sem-fg)' }}>{prov.origin === 'human' ? 'You' : 'Your assistant'}</b></span>
          {prov.when && <span> · {fmtWhen(prov.when)}</span>}
          {prov.sourceRunIds && prov.sourceRunIds.length > 0 && <span> · from {prov.sourceRunIds.length} run{prov.sourceRunIds.length === 1 ? '' : 's'}</span>}
        </div>
      )}
      {err && <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-bad)' }}>{err}</div>}
      <div className="mt-2.5 flex flex-wrap items-center gap-1.5">
        {editing ? (
          <>
            <MiniBtn primary disabled={busy || !text.trim()} onClick={() => void saveEdit()}>Save</MiniBtn>
            <MiniBtn disabled={busy} onClick={() => { setEditing(false); setText(rec.text); setKeys((rec.keys ?? []).join(', ')); }}>Cancel</MiniBtn>
          </>
        ) : pending ? (
          <>
            <MiniBtn primary disabled={busy} onClick={() => void run(rpc('approveInsight', rec.id, 'human'))}>Approve</MiniBtn>
            <MiniBtn disabled={busy} onClick={() => void run(rpc('deleteInsight', rec.id, 'human'))}>Delete</MiniBtn>
          </>
        ) : (
          <>
            <MiniBtn disabled={busy} title="Upvote: raises importance" onClick={() => void run(rpc('upvoteInsight', rec.id, 'human'))}>▲ Upvote</MiniBtn>
            <MiniBtn disabled={busy} title="Downvote: at score 0 it drops out" onClick={() => void run(rpc('downvoteInsight', rec.id, 'human'))}>▼ Downvote</MiniBtn>
            <MiniBtn disabled={busy} onClick={() => setEditing(true)}>Edit</MiniBtn>
            <MiniBtn disabled={busy} title="Remove this lesson" onClick={() => void run(rpc('deleteInsight', rec.id, 'human'))}>Delete</MiniBtn>
          </>
        )}
      </div>
    </article>
  );
}

function LearnedWorkflowsSection({ onOpenWorkflows }: { onOpenWorkflows?: () => void }) {
  const [learned, setLearned] = useState<WorkflowDef[] | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    (async () => {
      try {
        const lib = await rpc<WorkflowInfo[]>('listWorkflows');
        const defs = await Promise.all(lib.filter((w) => !w.error).map((w) => rpc<WorkflowDef>('getWorkflow', w.name).catch(() => null)));
        if (!alive) return;
        setLearned(defs.filter((d): d is WorkflowDef => !!d && !!d.provenance && Object.keys(d.provenance).length > 0));
      } catch (e) { if (alive) setErr(errMsg(e)); }
    })();
    return () => { alive = false; };
  }, []);

  return (
    <section id="knowledge-learned" className="rounded-xl border p-4" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      <div className="text-[10px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--sem-accent)' }}>
        Learned workflows <span style={{ color: 'var(--sem-muted)' }}>{learned ? `(${learned.length})` : ''}</span>
      </div>
      <p className="m-0 mt-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
        Playbooks distilled from your own verified runs. Check reads the playbook and confirms every action it names is real. Replay check rehearses each step with the saved example answers and changes nothing.
      </p>
      {err && <div className="mt-2"><Message color="var(--sem-bad)">{err}</Message></div>}
      {!learned ? (
        <div className="mt-3 text-[12px]" style={{ color: 'var(--sem-muted)' }}>Loading…</div>
      ) : learned.length === 0 ? (
        <div className="mt-3 text-[12px]" style={{ color: 'var(--sem-muted)' }}>No learned workflows yet. Ask your assistant to turn a successful process into a reusable workflow.</div>
      ) : (
        <div className="mt-3 flex flex-col gap-2">{learned.map((d) => <LearnedWorkflowCard key={d.name} def={d} onOpenWorkflows={onOpenWorkflows} />)}</div>
      )}
    </section>
  );
}

function LearnedWorkflowCard({ def, onOpenWorkflows }: { def: WorkflowDef; onOpenWorkflows?: () => void }) {
  const [report, setReport] = useState<WorkflowCheckReport | null>(null);
  const [replay, setReplay] = useState<WorkflowReplayReport | null>(null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const check = async () => {
    setBusy(true); setErr(null); setReplay(null);
    try { setReport(await rpc<WorkflowCheckReport>('checkWorkflow', def.name)); }
    catch (e) { setErr(errMsg(e)); } finally { setBusy(false); }
  };
  const replayCheck = async () => {
    setBusy(true); setErr(null); setReport(null);
    try { setReplay(await rpc<WorkflowReplayReport>('replayCheckWorkflow', def.name)); }
    catch (e) { setErr(errMsg(e)); } finally { setBusy(false); }
  };
  const prov = def.provenance ?? {};

  return (
    <article className="rounded-lg border p-3" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)' }}>
      <div className="flex flex-wrap items-start gap-2">
        <div className="min-w-0 flex-1">
          <div className="text-[12.5px] font-semibold">{def.title || def.name}</div>
          <div className="mt-0.5 text-[10.5px] tnum" style={{ color: 'var(--sem-muted)' }}>{def.name} · v{def.version}</div>
        </div>
        <div className="flex items-center gap-1.5">
          <MiniBtn disabled={busy} onClick={() => void check()}>{busy && !replay ? 'Checking…' : 'Check'}</MiniBtn>
          <MiniBtn disabled={busy} onClick={() => void replayCheck()}>{busy && !report ? 'Replaying…' : 'Replay check'}</MiniBtn>
          {onOpenWorkflows && <MiniBtn onClick={onOpenWorkflows}>Open in Workflows</MiniBtn>}
        </div>
      </div>
      <div className="mt-2 flex flex-wrap items-center gap-1.5">
        {Object.entries(prov).map(([k, v]) => (
          <span key={k} className="rounded px-1.5 py-0.5 text-[9.5px] tnum" style={{ background: 'var(--sem-surface)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>
            <b style={{ color: 'var(--sem-fg)' }}>{k}</b>: {v}
          </span>
        ))}
      </div>
      {err && <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-bad)' }}>{err}</div>}
      {report && (
        <div className="mt-2.5">
          <div className="mb-1.5 flex items-center gap-2">
            <span className="rounded-full px-1.5 py-0.5 text-[9px] font-semibold uppercase tracking-wide"
              style={report.ok
                ? { color: 'var(--sem-good)', background: 'color-mix(in srgb, var(--sem-good) 16%, transparent)' }
                : { color: 'var(--sem-bad)', background: 'color-mix(in srgb, var(--sem-bad) 16%, transparent)' }}>
              {report.parseError ? 'could not read' : report.ok ? 'ready' : 'has warnings'}
            </span>
          </div>
          {report.parseError && <div className="text-[11px]" style={{ color: 'var(--sem-bad)' }}>{report.parseError}</div>}
          <div className="flex flex-col gap-1">
            {report.findings.map((f, i) => (
              <div key={i} className="flex items-start gap-1.5 rounded px-2 py-1 text-[11px]"
                style={{ background: 'var(--sem-surface)', border: `1px solid color-mix(in srgb, ${f.severity === 'warn' ? 'var(--sem-warn)' : 'var(--sem-muted)'} 30%, transparent)` }}>
                <span className="shrink-0 text-[9px] font-semibold uppercase tracking-wide" style={{ color: f.severity === 'warn' ? 'var(--sem-warn)' : 'var(--sem-muted)' }}>{f.severity}</span>
                <span style={{ color: 'var(--sem-fg)' }}>{f.message}</span>
              </div>
            ))}
            {report.findings.length === 0 && !report.parseError && <div className="text-[11px]" style={{ color: 'var(--sem-good)' }}>Reads correctly. Every named action is real.</div>}
          </div>
        </div>
      )}
      {replay && <ReplayReport r={replay} />}
    </article>
  );
}

function ReplayReport({ r }: { r: WorkflowReplayReport }) {
  return (
    <div className="mt-2.5">
      <div className="mb-1.5 flex flex-wrap items-center gap-2">
        <span className="rounded-full px-1.5 py-0.5 text-[9px] font-semibold uppercase tracking-wide"
          style={r.parseError || !r.admissible
            ? { color: 'var(--sem-bad)', background: 'color-mix(in srgb, var(--sem-bad) 16%, transparent)' }
            : { color: 'var(--sem-good)', background: 'color-mix(in srgb, var(--sem-good) 16%, transparent)' }}>
          {r.parseError ? 'could not read' : r.admissible ? 'ready' : 'not admissible'}
        </span>
        {r.exemplarRun && <span className="text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>using saved example answers</span>}
      </div>
      {!r.replaySkipped && (
        <div className="mb-1.5 flex items-center gap-2.5 text-[10px] tnum" style={{ color: 'var(--sem-muted)' }}>
          {r.rehearsed > 0 && <span>rehearsed <b style={{ color: 'var(--sem-fg)' }}>{r.rehearsed}</b>{r.rehearsedFailed > 0 ? <span style={{ color: 'var(--sem-bad)' }}> ({r.rehearsedFailed} would fail)</span> : null}</span>}
          {r.skippedDenied > 0 && <span>skipped {r.skippedDenied}</span>}
          {r.skippedUnbindable > 0 && <span>missing answers {r.skippedUnbindable}</span>}
          {r.replayable > 0 && <span>needs a live model {r.replayable}</span>}
        </div>
      )}
      {r.note && <div className="mb-1.5 text-[11px]" style={{ color: r.replaySkipped ? 'var(--sem-muted)' : 'var(--sem-fg)' }}>{r.note}</div>}
      <div className="flex flex-col gap-1">
        {r.rows.map((row, i) => {
          const tint = row.outcome === 'rehearsed' && row.wouldSucceed === false ? 'var(--sem-bad)' : (REPLAY_TINT[row.outcome] ?? 'var(--sem-muted)');
          return (
            <div key={i} className="flex items-start gap-1.5 rounded px-2 py-1 text-[11px]"
              style={{ background: 'var(--sem-surface)', border: `1px solid color-mix(in srgb, ${tint} 30%, transparent)` }}>
              <span className="shrink-0 text-[9px] font-semibold uppercase tracking-wide" style={{ color: tint }}>{row.outcome}</span>
              <span className="shrink-0 tnum" style={{ color: 'var(--sem-muted)' }}>{row.step}</span>
              <span style={{ color: 'var(--sem-fg)' }}>{row.detail}</span>
            </div>
          );
        })}
      </div>
    </div>
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

  return (
    <section id="knowledge-recall" className="rounded-xl border p-4" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      <div className="text-[10px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--sem-accent)' }}>Recall</div>
      <p className="m-0 mt-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
        Describe your next task to find relevant saved lessons. Results consider matching words, similar models, importance and recent use. Each result explains why it was suggested.
      </p>
      {fp && <FingerprintCard fp={fp} />}
      <div className="mt-3 flex items-center gap-2">
        <input value={query} onChange={(e) => setQuery(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') void recall(); }}
          placeholder="Type what you're about to do" spellCheck={false} aria-label="Recall query"
          className="flex-1 rounded-md px-2.5 py-1.5 text-[12px] outline-none"
          style={{ background: 'var(--sem-bg)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
        <button onClick={() => void recall()} disabled={busy} className="rounded-md px-3 py-1.5 text-[11px] font-semibold disabled:opacity-45"
          style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>{busy ? 'Recalling…' : 'Recall'}</button>
      </div>
      {err && <div className="mt-2"><Message color="var(--sem-bad)">{err}</Message></div>}
      {result && (
        <div className="mt-3">
          {result.candidates.length === 0 ? (
            <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No matching prior lessons for this model shape.</div>
          ) : (
            <div className="flex flex-col gap-2">{result.candidates.map((c) => <CandidateCard key={c.insight.id} c={c} />)}</div>
          )}
        </div>
      )}
    </section>
  );
}

function FingerprintCard({ fp }: { fp: ModelFingerprint }) {
  return (
    <div className="mt-2.5 rounded-lg border p-3" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)' }}>
      <div className="flex flex-wrap items-center gap-3">
        <div className="text-[10px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>Model fingerprint</div>
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>The shape recall matches against.</span>
      </div>
      <div className="mt-1.5 flex flex-wrap items-baseline gap-5">
        <FpStat n={fp.tables} label="tables" />
        <FpStat n={fp.measures} label="measures" />
        <FpStat n={fp.columns} label="columns" />
        {fp.grade != null && <FpStat n={Math.round(fp.grade)} label="grade" />}
      </div>
      {fp.factTables?.length > 0 && <ChipRow label="facts" items={fp.factTables} tint="var(--sem-accent)" />}
      {fp.dimTables?.length > 0 && <ChipRow label="dims" items={fp.dimTables} />}
      {fp.domainTokens?.length > 0 && <ChipRow label="domain" items={fp.domainTokens} />}
    </div>
  );
}
function FpStat({ n, label }: { n: number; label: string }) {
  return <div><div className="text-lg font-semibold tnum">{n}</div><div className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>{label}</div></div>;
}
function ChipRow({ label, items, tint }: { label: string; items: string[]; tint?: string }) {
  return (
    <div className="mt-1.5 flex flex-wrap items-center gap-1.5">
      <span className="text-[9.5px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>{label}</span>
      {items.map((t) => (
        <span key={t} className="rounded px-1.5 py-0.5 text-[9.5px] tnum" style={tint
          ? { background: `color-mix(in srgb, ${tint} 14%, transparent)`, color: tint }
          : { background: 'var(--sem-surface)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>{t}</span>
      ))}
    </div>
  );
}

function CandidateCard({ c }: { c: RecallCandidate }) {
  const i = c.insight;
  return (
    <article className="rounded-lg border p-3" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-start gap-2">
        <div className="min-w-0 flex-1 text-[12.5px]" style={{ color: 'var(--sem-fg)' }}>{i.text}</div>
        <div className="flex shrink-0 items-center gap-1.5">
          <Pill tint={KIND_TINT[i.kind]}>{i.kind}</Pill>
          {c.fingerprintMatch && <Pill tint="var(--sem-accent)" title="Same model shape">shape</Pill>}
        </div>
      </div>
      {c.matchedKeys.length > 0 && (
        <div className="mt-1.5 flex flex-wrap items-center gap-1">
          {c.matchedKeys.map((k) => (
            <span key={k} className="rounded px-1.5 py-0.5 text-[9.5px] tnum" style={{ background: 'color-mix(in srgb, var(--sem-accent) 14%, transparent)', color: 'var(--sem-accent)' }}>{k}</span>
          ))}
        </div>
      )}
      <div className="mt-1.5 text-[10.5px]" style={{ color: 'var(--sem-muted)' }}>{c.why}</div>
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
    <section id="knowledge-purge" className="rounded-xl border p-4" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
      <div className="text-[10px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--sem-accent)' }}>Delete saved knowledge</div>
      <p className="m-0 mt-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
        Remove saved lessons from use for this model or across all models. Review the count before confirming. The storage history is kept; this does not erase the original records.
      </p>
      <div className="mt-3 flex items-center gap-2">
        <select value={scope} onChange={(e) => { setScope(e.target.value); reset(); }} disabled={phase !== 'idle'} aria-label="Purge scope"
          className="rounded-md px-2 py-1 text-[12px] outline-none"
          style={{ background: 'var(--sem-bg)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
          <option value="project">this model</option>
          <option value="global">all models</option>
        </select>
        {phase === 'idle' && <MiniBtn disabled={busy} onClick={() => void call(false)}>{busy ? 'Checking…' : 'Purge…'}</MiniBtn>}
      </div>
      {err && <div className="mt-2"><Message color="var(--sem-bad)">{err}</Message></div>}
      {phase === 'review' && report && (
        <div className="mt-2.5 rounded-lg border p-3" style={{ background: 'var(--sem-bg)', borderColor: 'color-mix(in srgb, var(--sem-bad) 34%, transparent)' }}>
          <div className="text-[12px]" style={{ color: 'var(--sem-fg)' }}>
            This would wipe {report.liveCount} lesson{report.liveCount === 1 ? '' : 's'} for {scopeLabel(scope)}. Confirm to continue.
          </div>
          <div className="mt-2.5 flex items-center gap-2">
            <button onClick={() => void call(true)} disabled={busy}
              className="rounded-lg px-3 py-1.5 text-[12px] font-medium disabled:opacity-40" style={{ background: 'var(--sem-bad)', color: '#fff' }}>
              {busy ? 'Purging…' : `Purge ${report.liveCount} lesson${report.liveCount === 1 ? '' : 's'}`}
            </button>
            <MiniBtn disabled={busy} onClick={reset}>Cancel</MiniBtn>
          </div>
        </div>
      )}
      {phase === 'done' && report && (
        <div className="mt-2.5 rounded-lg border p-3" style={{ background: 'var(--sem-bg)', borderColor: 'var(--sem-border)' }}>
          <div className="text-[12px]" style={{ color: 'var(--sem-good)' }}>Wiped {report.liveCount} lesson{report.liveCount === 1 ? '' : 's'} for {scopeLabel(scope)}.</div>
          <div className="mt-2"><MiniBtn onClick={reset}>Done</MiniBtn></div>
        </div>
      )}
    </section>
  );
}

function Jump({ href, children, destructive }: { href: string; children: ReactNode; destructive?: boolean }) {
  const id = href.startsWith('#') ? href.slice(1) : href;
  return (
    <a href={href} onClick={(e) => { e.preventDefault(); document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' }); }}
      title={destructive ? 'Go to the section that removes saved lessons. Nothing is removed until you confirm there.' : undefined}
      className={destructive ? 'rounded-md border px-2 py-0.5 no-underline font-medium' : 'rounded-full border px-2 py-0.5 no-underline'}
      style={destructive
        ? { borderColor: 'color-mix(in srgb, var(--sem-bad) 55%, transparent)', color: 'var(--sem-bad)', background: 'color-mix(in srgb, var(--sem-bad) 10%, transparent)' }
        : { borderColor: 'var(--sem-border)', color: 'var(--sem-fg)' }}>
      {children}
    </a>
  );
}
function Pill({ children, tint, title }: { children: ReactNode; tint?: string; title?: string }) {
  return (
    <span title={title} className="rounded-full px-1.5 py-0.5 text-[9.5px] font-semibold"
      style={tint
        ? { color: tint, background: `color-mix(in srgb, ${tint} 16%, transparent)` }
        : { color: 'var(--sem-muted)', background: 'var(--sem-surface)', border: '1px solid var(--sem-border)' }}>
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
function Message({ children, color }: { children: ReactNode; color: string }) {
  return <div className="rounded-lg border px-3 py-2 text-[12px]" style={{ color, borderColor: `color-mix(in srgb, ${color} 45%, transparent)`, background: `color-mix(in srgb, ${color} 10%, transparent)` }}>{children}</div>;
}
