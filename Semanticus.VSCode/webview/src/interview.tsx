import { useEffect, useMemo, useState } from 'react';
import { rpc } from './bridge';

// The Model Interview evidence section (Tests tab) — the DETERMINISTIC artifact only: the saved question pack,
// each question's last graded outcome, and a replay button (run_interview is free). The Tests coordinator also
// replays the DAX-backed questions on every suite run; safe-decline questions stay chat-only. Authoring questions is
// the /interview-model skill's job — this card never invokes the AI assistant (golden rule 1), it just shows
// what the engine proved. UX bar (Kane 2026-07-07): PLAIN labels + colour only — the engine's outcome enum
// (Correct | Refused | SilentlyWrong | Unverified) never appears as user copy, and neither does a rule id.

interface RunRecord { when?: string; outcome?: string; detail?: string; fixRuleId?: string; fixHint?: string }
export interface SuiteInterviewEvidence extends RunRecord {
  questionId?: string; question?: string; tier?: string; replayStatus?: 'replayed' | 'chat-only';
  previousOutcome?: string; changed?: boolean;
}
interface Question {
  id: string; question: string; tier: string; scope: string; seedSource?: string;
  expectedValue?: string; expectedMatrix?: string[][]; expectRefusal?: boolean; lastRun?: RunRecord;
  // The tier's DAX + oracle fields ride the list result too (the engine serializes the whole question). The card
  // reads them so the "pin a trusted answer" flow can re-save the SAME question carrying the pasted value, with no
  // JSON editing (a value question keeps its query; a paraphrase keeps both phrasings).
  query?: string; scalarExpr?: string; paraphraseExpr?: string; groupBy?: string[]; filters?: string[]; fixRuleId?: string;
  modelLabel?: string;   // #157: which model this question was authored against (shown on the "another model" divider)
}
// #157: the pack is bound to the model it was authored against. `questions` is the OPEN model's pack only;
// questions from another model, or unattributed ones (saved before binding, in a shared store), come back in
// their own buckets so they are shown honestly — never mixed into this model's interview.
interface ListResult { questions: Question[]; otherModelQuestions?: Question[]; unattributedQuestions?: Question[]; skippedCorruptLines: number; note?: string }
interface RunResult { questionId?: string; outcome: string; detail?: string; fixRuleId?: string; fixHint?: string; recorded?: boolean }
interface SeedResult { candidates?: { source: string }[] }

// Where a saved question came from, in analyst words (the seeding lanes are deterministic - never the assistant
// inventing provenance). Absent/unknown sources show nothing rather than a guess.
const SEED_BADGE: Record<string, string> = {
  'verified-answer': 'From a verified answer',
  'hard-pack': 'Built-in hard question',
};

// The engine-enum → plain-label mapping. "Confidently wrong" is deliberately the scary one (red);
// a safe refusal is a GOOD outcome (calm grey), and "couldn't check" is amber honesty, not failure.
const OUTCOME: Record<string, { label: string; color: string }> = {
  Correct: { label: 'Right', color: 'var(--sem-good)' },
  Refused: { label: "Safely said it couldn't answer", color: 'var(--sem-muted)' },
  SilentlyWrong: { label: 'Confidently wrong', color: 'var(--sem-bad)' },
  Unverified: { label: "Couldn't check", color: 'var(--sem-warn)' },
};
// A future/unknown outcome string must still render as "was asked, couldn't be interpreted" — falling back
// to "Not asked yet" would be a lie (the raw detail stays available on the chip's hover).
const UNKNOWN_OUTCOME = { label: "Couldn't check", color: 'var(--sem-warn)' };

// What each tier means, in analyst words (shown as a muted qualifier, never jargon like "equivalence").
const TIER_NOTE: Record<string, string> = {
  value: 'checked against a number you trust',
  paraphrase: 'asked two different ways',
  refusal: 'should be safely declined',
};

export function InterviewCard({ suiteEvidence = [], suiteNote }: { suiteEvidence?: SuiteInterviewEvidence[]; suiteNote?: string }) {
  const [questions, setQuestions] = useState<Question[]>([]);
  const [otherModel, setOtherModel] = useState<Question[]>([]);          // #157: packs authored against a different model
  const [unattributed, setUnattributed] = useState<Question[]>([]);      // #157: legacy packs with no model binding
  const [fresh, setFresh] = useState<Record<string, RunResult>>({});   // this-session results (carry the fix hint)
  const [busy, setBusy] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [seedCount, setSeedCount] = useState(0);   // ready-made candidates the engine could seed (info line only)
  const [pinning, setPinning] = useState<string | null>(null);   // which question's "expected answer" input is open
  const [pinValue, setPinValue] = useState('');                  // the pasted trusted / Copilot answer being edited
  const [pinBusy, setPinBusy] = useState(false);
  const suiteById = useMemo(() => new Map(suiteEvidence.filter((x) => x.questionId).map((x) => [x.questionId!, x])), [suiteEvidence]);

  async function load() {
    try {
      const r = await rpc<ListResult>('listInterviewQuestions');
      setQuestions(r.questions ?? []);
      setOtherModel(r.otherModelQuestions ?? []);
      setUnattributed(r.unattributedQuestions ?? []);
      setNote(r.skippedCorruptLines > 0 ? r.note ?? null : null);
      setError(null);
    } catch (e) { setError(String((e as Error).message ?? e)); }
    // Best-effort: the seed count is a hint, never a blocker (an older engine without the op stays silent).
    try { setSeedCount((await rpc<SeedResult>('listInterviewSeeds')).candidates?.length ?? 0); } catch { /* hint only */ }
  }
  useEffect(() => { void load(); }, []);

  async function ask(q: Question) {
    setBusy((b) => new Set(b).add(q.id));
    setError(null);
    try {
      const r = await rpc<RunResult>('runInterview', q.id);
      setFresh((f) => ({ ...f, [q.id]: r }));
      await load();   // the outcome was recorded on the saved question — re-read so lastRun reflects it
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setBusy((b) => { const n = new Set(b); n.delete(q.id); return n; }); }
  }

  async function askAll() {
    // Each run_interview is a free one-question op — no bulk primitive. Refusal-tier questions are graded in
    // chat only (they need the assistant's honest decline/attempt), so replaying them here would always come
    // back "couldn't check" AND overwrite a previously meaningful record — skip them.
    for (const q of questions.filter((x) => x.tier !== 'refusal')) await ask(q);
  }

  function openPin(q: Question) {
    setPinning(q.id);
    setPinValue(q.expectedValue ?? '');
    setError(null);
  }

  // Pin the number this question SHOULD return (paste your Copilot / expected answer) without editing any JSON.
  // The engine keeps questions append-only, so pinning re-saves the SAME question carrying the trusted value
  // (add_interview_question), then retires the prior copy. A value question keeps its query; a paraphrase keeps
  // both phrasings AND now gets a trusted answer, which is what lets the interview catch an answer that is
  // consistent-but-wrong. Add-then-delete order is deliberate: if the save is refused (saving is part of Pro),
  // the original stays exactly as it was and the reason is shown.
  async function savePin(q: Question) {
    const value = pinValue.trim();
    if (!value) { setError('Enter the answer this question should return (a number, or the word BLANK for no value).'); return; }
    setPinBusy(true);
    setError(null);
    try {
      await rpc<Question>('addInterviewQuestion',
        q.question, q.tier, q.query ?? null, q.scalarExpr ?? null, q.paraphraseExpr ?? null,
        q.groupBy ?? [], q.filters ?? [], value, null, false, q.fixRuleId ?? null, q.seedSource ?? 'user', q.scope ?? 'project', 'human');
      // The save landed — retire the prior copy so the pack shows one question, now carrying the trusted answer.
      try { await rpc('deleteInterviewQuestion', q.id, 'human'); } catch { /* the new copy stands regardless */ }
      setPinning(null);
      setPinValue('');
      await load();
    } catch (e) { setError(String((e as Error).message ?? e)); }
    finally { setPinBusy(false); }
  }

  return (
    <div className="rounded-xl border p-4" style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>
      <div className="flex items-center gap-2">
        <div>
          <div className="text-[11px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-muted)' }}>Model Interview</div>
          <div className="text-[13px] font-semibold mt-0.5">Behavioral contracts</div>
        </div>
        <div className="ml-auto flex gap-1.5 shrink-0">
          {questions.length > 0 && (
            <IBtn disabled={busy.size > 0} onClick={() => void askAll()} title="Ask every saved question again and re-check each answer (free). Questions about safely declining are graded from chat and are skipped here.">
              {busy.size > 0 ? 'Asking…' : 'Ask all again'}
            </IBtn>
          )}
          <IBtn onClick={() => void load()} title="Re-read the saved questions">Refresh</IBtn>
        </div>
      </div>

      {error && <div className="mt-2 rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb,var(--sem-bad) 14%, transparent)', color: 'var(--sem-bad)' }}>{error}</div>}
      <div className="mt-2 rounded-md border px-2.5 py-1.5 text-[10px]" style={{ borderColor: 'var(--sem-border)', color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>
        Evidence only. Running tests automatically re-checks saved number and paraphrase questions. Safe-decline questions are checked in an AI chat.
        Those outcomes appear in the report but never change its grade or coverage.
      </div>
      {suiteNote && <div className="mt-2 text-[11px]" style={{ color: suiteEvidence.some((x) => x.changed) ? 'var(--sem-warn)' : 'var(--sem-muted)' }}>{suiteNote}</div>}
      {note && <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-warn)' }}>{note}</div>}
      {seedCount > 0 && (
        <div className="mt-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
          {seedCount} ready-made question{seedCount === 1 ? '' : 's'} could be added from your verified answers and the built-in
          hard set. Ask your AI Assistant to review and save the keepers; every number is confirmed with you before it becomes the trusted answer.
        </div>
      )}

      {questions.length === 0 ? (
        <div className="mt-2 text-[12px]" style={{ color: 'var(--sem-muted)' }}>
          No questions saved yet. Ask your AI Assistant to <span className="font-medium">interview this model</span>. It proposes the questions
          your users will ask, checks each answer against a number you trust, and saves the keepers here so every future edit can be re-checked.
          One-off checks are free; saving questions is part of Pro.
        </div>
      ) : (
        <div className="mt-2 flex flex-col gap-1">
          {questions.map((q) => {
            const contract = suiteById.get(q.id);
            // A just-completed Tests replay wins over the persisted last observation, while an explicit Ask action
            // wins over both. Chat-only evidence intentionally falls back to its last real assistant observation.
            const run = fresh[q.id] ?? (contract?.replayStatus === 'replayed' ? contract : q.lastRun);
            const oc = run?.outcome ? OUTCOME[run.outcome] ?? UNKNOWN_OUTCOME : null;
            const isBusy = busy.has(q.id);
            return (
              <div key={q.id} className="rounded-lg px-2.5 py-2 hover:bg-[var(--sem-surface-2)]">
                <div className="flex items-center gap-2.5">
                  <div className="min-w-0 flex-1">
                    <div className="text-[12px] truncate">“{q.question}”</div>
                    <div className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>
                      {TIER_NOTE[q.tier] ?? ''}
                      {q.seedSource && SEED_BADGE[q.seedSource] ? ` · ${SEED_BADGE[q.seedSource]}` : ''}
                    </div>
                  </div>
                  {oc ? (
                    <span title={run?.detail} className="text-[11px] px-2 py-0.5 rounded-full font-medium shrink-0"
                      style={{ background: `color-mix(in srgb, ${oc.color} 14%, transparent)`, color: oc.color }}>
                      {oc.label}
                    </span>
                  ) : (
                    <span className="text-[11px] px-2 py-0.5 rounded-full shrink-0" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>Not asked yet</span>
                  )}
                  {q.tier === 'refusal' ? (
                    // A refusal question grades the ASSISTANT's behavior (decline vs made-up answer) — the card
                    // has no assistant to ask, so replaying it here could only ever say "couldn't check" and
                    // would overwrite a meaningful earlier result. Graded from chat instead.
                    <span className="text-[10px] shrink-0" style={{ color: 'var(--sem-muted)' }}
                      title="This one checks that the AI safely declines to answer. Re-grade it from a chat with your AI Assistant.">
                      Graded in chat
                    </span>
                  ) : (
                    <IBtn disabled={isBusy} onClick={() => void ask(q)} title="Ask this question and check the answer (free)">
                      {isBusy ? '…' : run ? 'Ask again' : 'Ask'}
                    </IBtn>
                  )}
                </div>
                {run?.outcome === 'SilentlyWrong' && (
                  <div className="mt-1 text-[11px]" style={{ color: 'var(--sem-bad)' }}>
                    {run.detail}
                    {run.fixHint && <span style={{ color: 'var(--sem-fg)' }}> → {run.fixHint}</span>}
                  </div>
                )}
                {run?.outcome === 'Unverified' && run.detail && (
                  <div className="mt-1 text-[11px]" style={{ color: 'var(--sem-muted)' }}>{plain(run.detail)}</div>
                )}
                {contract?.replayStatus === 'replayed' && (
                  <div className="mt-1 text-[10px]" style={{ color: contract.changed ? 'var(--sem-warn)' : 'var(--sem-muted)' }}>
                    Replayed with this Tests run · {contract.changed
                      ? `changed from ${contract.previousOutcome ? (OUTCOME[contract.previousOutcome] ?? UNKNOWN_OUTCOME).label : 'the previous result'}`
                      : contract.previousOutcome ? 'unchanged' : 'first observation'}
                  </div>
                )}

                {/* Bring-your-own answer: pin the number this question SHOULD return (from Copilot, or what you
                    know is right) so every future edit is re-checked against it. Not offered for "safely declined"
                    questions, which have no number to pin — nor when a matrix oracle already exists, since a single
                    pasted number can't represent (and would silently discard) a multi-row trusted answer. */}
                {q.tier !== 'refusal' && !(q.expectedMatrix && q.expectedMatrix.length) && (
                  pinning === q.id ? (
                    <div className="mt-1.5 flex items-center gap-1.5">
                      <input autoFocus value={pinValue} onChange={(e) => setPinValue(e.target.value)}
                        onKeyDown={(e) => { if (e.key === 'Enter') void savePin(q); if (e.key === 'Escape') setPinning(null); }}
                        placeholder="Paste the expected / Copilot answer (a number, or BLANK)"
                        className="flex-1 text-[11px] px-2 py-1 rounded-md"
                        style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }} />
                      <IBtn disabled={pinBusy} onClick={() => void savePin(q)} title="Save this as the trusted answer (part of Pro)">
                        {pinBusy ? 'Saving…' : 'Save'}
                      </IBtn>
                      <IBtn disabled={pinBusy} onClick={() => setPinning(null)} title="Cancel">Cancel</IBtn>
                    </div>
                  ) : (
                    <button data-pin={q.id} disabled={pinBusy} onClick={() => openPin(q)}
                      className="mt-1 text-[10px] hover:underline"
                      style={{ color: 'var(--sem-muted)' }}
                      title="Paste the answer this question should return, so future edits are re-checked against it.">
                      {q.expectedValue ? '✎ Edit the trusted answer' : '＋ Pin the answer it should give'}
                    </button>
                  )
                )}
              </div>
            );
          })}
        </div>
      )}

      {/* #157: questions bound to a DIFFERENT model, or saved before questions were tied to a model, are shown
          under explicit dividers — never mixed into this model's interview and never run against it. */}
      <StrayList
        title="From another model"
        hint="These questions were written for a different model. Their answers are checked against that model's data, so they aren't run here. Re-open that model to use them, or remove them."
        items={otherModel}
        withOrigin
      />
      <StrayList
        title="Unattributed"
        hint="Saved before questions were tied to a model. We can't tell which model they belong to, so they aren't run against this one. Re-save them while this model is open to keep them, or remove them."
        items={unattributed}
      />
    </div>
  );
}

// AI Readiness keeps only the latest behavioral signal. The full question pack and actions live in Tests,
// while this chip preserves the readiness narrative without creating a second evidence surface.
export function InterviewSummaryChip({ onOpen }: { onOpen: () => void }) {
  const [questions, setQuestions] = useState<Question[]>([]);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    let cancelled = false;
    rpc<ListResult>('listInterviewQuestions')
      .then((r) => { if (!cancelled) { setQuestions(r.questions ?? []); setFailed(false); } })
      .catch(() => { if (!cancelled) setFailed(true); });
    return () => { cancelled = true; };
  }, []);
  const observed = questions.filter((q) => q.lastRun?.outcome);
  const latest = [...observed].sort((a, b) => Date.parse(b.lastRun?.when ?? '') - Date.parse(a.lastRun?.when ?? ''))[0]?.lastRun;
  const outcome = latest?.outcome ? OUTCOME[latest.outcome] ?? UNKNOWN_OUTCOME : null;
  return (
    <button onClick={onOpen} className="self-start rounded-full border px-3 py-1.5 text-left"
      style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)', color: 'var(--sem-fg)' }}
      title="Open the full Model Interview evidence in Tests">
      <span className="text-[10px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>Model Interview</span>
      <span className="mx-2" style={{ color: 'var(--sem-border)' }}>·</span>
      <span className="text-[11px]">{failed ? 'Evidence unavailable' : questions.length === 0 ? 'No saved questions' : `${observed.length} of ${questions.length} observed`}</span>
      <span className="mx-2" style={{ color: 'var(--sem-border)' }}>·</span>
      <span className="text-[11px] font-medium" style={{ color: outcome?.color ?? 'var(--sem-muted)' }}>{outcome ? `Latest: ${outcome.label}` : 'Latest: not asked yet'}</span>
      <span className="ml-2 text-[11px]" style={{ color: 'var(--sem-accent)' }}>View in Tests ›</span>
    </button>
  );
}

// A read-only list of questions that do NOT belong to the open model. No "Ask" button — running them here would
// grade one model's trusted answer against another model's data (the exact leak #157 closes). Shown so the user
// sees their work is safe and can act on it (re-open the right model, or delete), never silently hidden.
function StrayList({ title, hint, items, withOrigin }: { title: string; hint: string; items: Question[]; withOrigin?: boolean }) {
  if (!items || items.length === 0) return null;
  return (
    <div className="mt-3 pt-2" style={{ borderTop: '1px solid var(--sem-border)' }}>
      <div className="text-[11px] font-semibold" style={{ color: 'var(--sem-muted)' }}>
        {title} · {items.length}
      </div>
      <div className="text-[10px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>{hint}</div>
      <div className="mt-1 flex flex-col gap-0.5">
        {items.map((q) => (
          <div key={q.id} className="text-[11px] px-1 py-0.5" style={{ color: 'var(--sem-muted)' }}>
            “{q.question}”{withOrigin && q.modelLabel ? <span> · {q.modelLabel}</span> : null}
          </div>
        ))}
      </div>
    </div>
  );
}

// UI-never-speaks-engine: the engine's honesty details are written for the agent door (they name ops like
// open_live/open_local, or the abstained/attemptDax parameters). The two details an analyst can actually
// hit here get plain-words translations.
function plain(detail: string): string {
  if (detail.startsWith('offline'))
    return 'No live connection. Connect to a running model to check this answer.';
  if (detail.includes('abstained'))
    return 'This gets graded from a chat with your AI Assistant. It checks that the AI declines instead of making a number up.';
  return detail;
}

function IBtn({ children, onClick, disabled, title }: { children: React.ReactNode; onClick?: () => void; disabled?: boolean; title?: string }) {
  return (
    <button title={title} onClick={onClick} disabled={disabled}
      className="text-[11px] px-2 py-0.5 rounded-md font-medium transition-opacity disabled:opacity-40 whitespace-nowrap shrink-0"
      style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>
      {children}
    </button>
  );
}
