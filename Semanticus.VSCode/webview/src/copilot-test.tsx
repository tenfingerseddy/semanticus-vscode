// Review > Copilot test (Kane, 15 Oct 2026, option A). The model interview as a test loop: the person's own assistant writes a bank of questions with proven
// answers, a small student model in that assistant answers them seeing only what Copilot sees, the engine grades by number, the assistant changes the AI
// instructions, descriptions and other names, and the bank runs again. This page shows the loop. It runs no AI: starting a round asks the assistant
// (a task on the Plan), and everything else is read from the same engine calls the assistant uses (interviewRounds, listInterviewQuestions, getCopilotView).
// One 12-column grid, every part open by default, tokens only.
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { navigateInStudio, onActivity, onDidChange, onProgress, rpc } from './bridge';
import { GiveToAssistant, clampWords, giveToAssistant, type PlanTaskRequest } from './sendtask';
import { PublishThenTest } from './copilot-test-publish';
import { useDriver } from './studio/driver-store';
import {
  HELD_NOTE, CHANGES_NOTE, assistantNote, KIND_HINT, KIND_LABEL, MISSES, MISS_LABEL, MISS_HINT, MISS_SHORT, WHO_SEES,
  bankRows, bankTask, changeOutcome, headSummary, headline, headlineNote, heatCells, kindSeries, latestOf, missesByKind, pct, progressShare, roundState, roundTask,
  scoreSeries, share, studentLabel,
  type BankQuestion, type CopilotChangeOut, type CopilotMissOut, type CopilotRound, type CopilotRoundsView, type CopilotView, type Miss, type QKind,
} from './copilot-test-model';
import './copilot-test.css';

/** The score the person is aiming for. The page says it; the assistant is told it in the task. */
const AIM = 0.9;
const SHOWN_PER_GROUP = 9;

function useWidth<T extends HTMLElement>(): [React.RefObject<T | null>, number] {
  const ref = useRef<T | null>(null);
  const [w, setW] = useState(0);
  useLayoutEffect(() => { if (ref.current) setW(ref.current.clientWidth); }, []);
  useEffect(() => {
    const el = ref.current; if (!el) return;
    const ro = new ResizeObserver(() => setW(el.clientWidth));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  return [ref, w];
}

const task = (page: string, t: { words: string; brief: string }): PlanTaskRequest => ({
  words: clampWords(t.words), brief: t.brief, sourcePage: page, objects: [], doneWhen: 'assistant_says', originLink: { kind: 'page', ref: page },
});

// ---------------------------------------------------------------- what the page reads
interface Loaded { rounds: CopilotRoundsView | null; detail: CopilotRound | null; bank: BankQuestion[]; view: CopilotView | null }

function useCopilotTest() {
  const [loaded, setLoaded] = useState<Loaded>({ rounds: null, detail: null, bank: [], view: null });
  const [error, setError] = useState<string | null>(null);
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const alive = useRef(true);
  const seq = useRef(0);
  const load = useCallback(async () => {
    const mine = ++seq.current;
    try {
      const rounds = await rpc<CopilotRoundsView>('interviewRounds', null);
      const last = latestOf(rounds.rounds);
      // The misses of the latest round that has finished (the one the changes come from); a round still running shows the one before it.
      const doneRound = [...rounds.rounds].reverse().find((r) => r.complete) ?? null;
      const detail = doneRound ? (await rpc<CopilotRoundsView>('interviewRounds', String(doneRound.number))).rounds.find((r) => r.number === doneRound.number) ?? null : null;
      const list = await rpc<{ questions: BankQuestion[] }>('listInterviewQuestions', 'project');
      const view = await rpc<CopilotView>('getCopilotView');
      if (!alive.current || mine !== seq.current) return;
      setLoaded({ rounds, detail, bank: (list.questions ?? []).filter((q) => !!q.kind), view });
      setError(null);
      if (last?.complete) setProgress(null);
    } catch (e) {
      if (alive.current && mine === seq.current) setError(String((e as Error)?.message ?? e));
    }
  }, []);
  useEffect(() => {
    alive.current = true;
    void load();
    const offActivity = onActivity((e) => { if (e.kind === 'run_interview_batch' || e.kind === 'add_interview_questions') void load(); });
    const offProgress = onProgress((p) => { if (p.opKey === 'copilot_round') setProgress({ done: p.done, total: p.total }); });
    // A change to the model moves what Copilot sees (a description, an instruction), so the counts are read again.
    let timer: number | undefined;
    const offChange = onDidChange(() => { window.clearTimeout(timer); timer = window.setTimeout(() => void load(), 400); });
    return () => { alive.current = false; offActivity(); offProgress(); offChange(); window.clearTimeout(timer); };
  }, [load]);
  return { ...loaded, error, progress };
}

// ---------------------------------------------------------------- shared bits
function Fold({ open, onToggle, testid }: { open: boolean; onToggle: () => void; testid: string }) {
  return <button type="button" className="ct-fold" aria-expanded={open} onClick={onToggle} data-testid={testid} title={open ? 'Fold' : 'Open'}>
    <svg viewBox="0 0 12 12" width="12" height="12" aria-hidden="true"><path d={open ? 'M2.5 4.5L6 8l3.5-3.5' : 'M4.5 2.5L8 6l-3.5 3.5'} fill="none" stroke="currentColor" strokeWidth="1.4" /></svg>
  </button>;
}

function Title({ children, extra, open, onToggle, testid }: { children: React.ReactNode; extra?: React.ReactNode; open: boolean; onToggle: () => void; testid: string }) {
  return <div className="ct-title"><span className="sem-card-title">{children}</span><span className="ct-push" />{extra}<Fold open={open} onToggle={onToggle} testid={`fold-${testid}`} /></div>;
}

function JevMark({ title }: { title: string }) {
  return <span className="ct-jev" title={title}><svg viewBox="0 0 12 12" width="12" height="12" aria-hidden="true"><path d="M6 .8l1.4 3.8L11.2 6 7.4 7.4 6 11.2 4.6 7.4.8 6l3.8-1.4z" fill="currentColor" /></svg>Jev</span>;
}

// ---------------------------------------------------------------- the score over the rounds
function ScoreChart({ rounds, running }: { rounds: CopilotRound[]; running: boolean }) {
  const [ref, w] = useWidth<HTMLDivElement>();
  const h = 168, l = 40, r = 40, top = 18, b = 24;
  const pts = scoreSeries(rounds);
  const n = pts.length;
  const px = (i: number) => (n <= 1 ? l + (w - l - r) / 2 : l + (i * (w - l - r)) / (n - 1));
  const py = (v: number) => top + (h - top - b) * (1 - v);
  const line = (get: (p: (typeof pts)[number]) => number | null) => pts.map((p, i) => ({ i, v: get(p) })).filter((x): x is { i: number; v: number } => x.v != null);
  const practice = line((p) => p.all), held = line((p) => p.held);
  const path = (xs: { i: number; v: number }[]) => xs.map((x, k) => `${k ? 'L' : 'M'}${px(x.i)} ${py(x.v)}`).join(' ');
  return <div ref={ref} className="ct-chart" data-testid="score-chart">{w > 0 && <svg width={w} height={h} role="img" aria-label="Right answers over rounds">
    {[0, 0.5, 1].map((t) => <g key={t}><line x1={l} x2={w - r} y1={py(t)} y2={py(t)} stroke="var(--sem-rule)" />
      <text x={l - 8} y={py(t)} textAnchor="end" dominantBaseline="middle" className="ct-axis">{pct(t)}</text></g>)}
    <line x1={l} x2={w - r} y1={py(AIM)} y2={py(AIM)} stroke="var(--sem-muted)" strokeDasharray="4 4" />
    <text x={l + 6} y={py(AIM) - 7} className="ct-axis">Aim {pct(AIM)}</text>
    {held.length > 1 && <path d={path(held)} fill="none" stroke="var(--sem-series-neutral)" strokeWidth={1.5} strokeDasharray="2 3" />}
    {held.map((x) => <circle key={`h${x.i}`} cx={px(x.i)} cy={py(x.v)} r={2.5} fill="var(--sem-series-neutral)" />)}
    {practice.length > 1 && <path d={path(practice)} fill="none" stroke="var(--sem-accent)" strokeWidth={2} />}
    {practice.map((x) => {
      const live = running && x.i === n - 1;
      return <g key={x.i}>
        <circle cx={px(x.i)} cy={py(x.v)} r={4} fill={live ? 'var(--sem-surface)' : 'var(--sem-accent)'} stroke="var(--sem-accent)" strokeWidth={2} className={live ? 'ct-pulse' : ''} />
        <text x={px(x.i)} y={py(x.v) - 12} textAnchor="middle" className="ct-ptlabel">{pct(x.v)}{live ? ' so far' : ''}</text>
      </g>;
    })}
    {pts.map((p, i) => <text key={`x${i}`} x={px(i)} y={h - 8} textAnchor="middle" className="ct-axis">Round {p.number}</text>)}
  </svg>}</div>;
}

function ScoreCard({ rounds }: { rounds: CopilotRound[] }) {
  const [open, setOpen] = useState(true);
  const last = latestOf(rounds);
  const hl = headline(rounds);
  return <section className="sem-card ct-card sem-span-7" data-testid="card-score">
    <Title testid="score" open={open} onToggle={() => setOpen(!open)} extra={<span className="sem-text-meta">How many the student got right</span>}>Score over rounds</Title>
    {open && <>
      {rounds.length === 0
        ? <div className="sem-text-meta" data-testid="score-empty">No round has run yet. When one has, its score and the score of the held back questions show here.</div>
        : <>
          <div className="ct-bignum"><span className="ct-big ct-num" data-testid="score-now">{pct(hl.value)}</span><span className="sem-text-meta">{headlineNote(rounds, AIM)}</span></div>
          <ScoreChart rounds={rounds} running={hl.running} />
          <div className="ct-legend sem-text-meta" data-testid="score-legend">
            <span className="ct-lg"><span className="ct-sw ct-sw-right" />All {last?.all.total ?? 0} questions</span>
            <span className="ct-lg"><span className="ct-sw ct-sw-held" />{last?.heldBack.total ?? 0} held back: {pct(share(last?.heldBack))}{last && !last.complete ? ' so far' : ''}</span>
          </div>
          <div className="sem-text-meta">{HELD_NOTE}</div>
        </>}
    </>}
  </section>;
}

// ---------------------------------------------------------------- the round
const STEPS = ['Questions', 'Student answers', 'Grade by number', 'Group misses', 'Propose changes', 'Try them'];

function RoundCard({ rounds, progress, hasBank }: { rounds: CopilotRound[]; progress: { done: number; total: number } | null; hasBank: boolean }) {
  const [open, setOpen] = useState(true);
  const state = roundState(rounds, progress);
  const last = latestOf(rounds);
  const done = state === 'done';
  const next = (last?.number ?? 0) + 1;
  const total = last?.total ?? progress?.total ?? 0;
  const answered = last?.answered ?? progress?.done ?? 0;
  // Steps: the engine sees the questions and grading; grouping happens when the last answer is in; the changes are the assistant's.
  const current = state === 'none' ? 0 : done ? (last && last.changes.length > 0 ? 5 : 4) : 2;
  return <section className="sem-card ct-card sem-span-5" data-testid="card-round">
    <Title testid="round" open={open} onToggle={() => setOpen(!open)}
      extra={state === 'none' ? null : <span className={`sem-chip ${done ? 'sem-chip-ok' : 'sem-chip-warn'}`} data-testid="round-state">{done ? 'Done' : 'Running'}</span>}>
      {last ? `Round ${last.number}` : 'Next round'}
    </Title>
    {open && <>
      <ol className="ct-steps" aria-label="Steps of a round">{STEPS.map((t, i) => <li key={t} className={i < current ? 'ct-step-done' : i === current ? 'ct-step-now' : ''}>{t}</li>)}</ol>
      <div className="ct-progress" aria-label={`${answered} of ${total} graded`}><span className="ct-prog-graded" style={{ width: `${progressShare(last, progress) * 100}%` }} /></div>
      <div className="ct-kv">
        <span>Graded</span><b className="ct-num">{answered} of {total}</b>
        <span>Right so far</span><b className="ct-num">{last ? `${last.all.right} (${pct(share(last.all))})` : 'none yet'}</b>
        <span>Student</span><b>{studentLabel(last)}, in your assistant</b>
        <span>Started</span><b>{last ? new Date(last.when).toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit' }) : 'not yet'}</b>
      </div>
      <div className="ct-row ct-foot">
        {hasBank
          ? <><GiveToAssistant sendKey={`copilot-test:round:${next}`} build={() => task('copilottest', roundTask(next))} primary bare label={`Run round ${next}`} />
            <span className="sem-text-meta">{done || state === 'none' ? 'Your assistant runs it. You can keep working.' : 'Runs in the background.'}</span></>
          : <span className="sem-text-meta">A round needs questions first. Your assistant writes them below.</span>}
      </div>
    </>}
  </section>;
}

// ---------------------------------------------------------------- the changes
function ChangeRow({ c, n, done }: { c: CopilotChangeOut; n: number; done: boolean }) {
  const [open, setOpen] = useState(true);
  return <div className="ct-change" data-testid={`change-${n}`}>
    <div className="ct-change-line">
      <span className="ct-change-n ct-num">{n}</span>
      <span className="ct-change-title">{c.title}</span>
      <span className="ct-push" />
      <span className="sem-chip sem-chip-outline">{c.where}</span>
      <span className="ct-change-out sem-text-meta ct-num">{changeOutcome(c, done)}</span>
      <Fold open={open} onToggle={() => setOpen(!open)} testid={`fold-change-${n}`} />
    </div>
    {open && <div className="ct-change-body">
      <div className="ct-diff"><span className="sem-text-meta">{c.on}, now</span><span className="ct-before">{c.before || '(nothing)'}</span></div>
      <div className="ct-diff"><span className="sem-text-meta">After</span><span className="ct-after">{c.after}</span></div>
    </div>}
  </div>;
}

function ChangesCard({ rounds }: { rounds: CopilotRound[] }) {
  const [open, setOpen] = useState(true);
  const last = latestOf(rounds);
  const tried = rounds.filter((r) => r.changes.length > 0);
  const round = last && last.changes.length > 0 ? last : tried.length ? tried[tried.length - 1] : null;
  const prevNumber = round ? round.number - 1 : 0;
  return <section className="sem-card ct-card sem-span-12" data-testid="card-changes">
    <Title testid="changes" open={open} onToggle={() => setOpen(!open)}
      extra={round ? <span className="sem-text-meta">{round.complete ? `Tried in round ${round.number}` : `Being tried in round ${round.number}`}</span> : null}>
      {round ? `Changes from round ${prevNumber}` : 'Changes'}
    </Title>
    {open && (round
      ? <>
        <div className="ct-changes">{round.changes.map((c, i) => <ChangeRow key={i} c={c} n={i + 1} done={round.complete} />)}</div>
        {round.newMisses > 0 && <div className="sem-text-meta" data-testid="new-misses"><span className="sem-chip sem-chip-bad">New</span> {round.newMisses} {round.newMisses === 1 ? 'question that was right is now a miss' : 'questions that were right are now misses'}.</div>}
        <div className="ct-row ct-foot">
          <button type="button" className="sem-btn sem-btn-sm" data-testid="open-history" onClick={() => navigateInStudio('history')}>Open History</button>
          <span className="sem-text-meta">{CHANGES_NOTE}</span>
        </div>
      </>
      : <div className="sem-text-meta" data-testid="changes-empty">After a round, your assistant proposes changes to the AI instructions, the descriptions and the other names. They show here with what each one fixed.</div>)}
  </section>;
}

// ---------------------------------------------------------------- the misses
function MissItem({ m }: { m: CopilotMissOut }) {
  return <div className="ct-miss ct-miss-sub ct-sub" data-testid={`miss-${m.n}`}>
    <div className="ct-miss-q"><span className="ct-q">“{m.question}”</span><span className="sem-chip sem-chip-outline">{KIND_LABEL[m.kind as QKind] ?? m.kind}</span></div>
    <div className="ct-side ct-side-bad"><div className="ct-side-head"><span>The student wrote</span><b className="ct-num">{m.studentAnswer ?? 'no answer'}</b></div><pre className="ct-dax">{m.studentDax ?? '(no DAX)'}</pre></div>
    <div className="ct-side"><div className="ct-side-head"><span>The right answer</span><b className="ct-num">{m.referenceAnswer ?? ''}</b></div><pre className="ct-dax">{m.referenceDax ?? '(none: the model has no data for this)'}</pre></div>
    {m.cause && <div className="ct-cause"><span>{m.cause}</span></div>}
  </div>;
}

function MissesCard({ round }: { round: CopilotRound | null }) {
  const [open, setOpen] = useState(true);
  const [only, setOnly] = useState<Miss | null>(null);
  const [all, setAll] = useState(false);
  const misses = round?.misses ?? [];
  const groups = missesByKind(misses).filter((g) => !only || g.miss === only);
  const by = round?.groups.some((g) => g.by === 'jev') ? 'jev' : 'rules';
  return <section className="sem-card ct-card ct-sub sem-span-12" data-testid="card-misses">
    <Title testid="misses" open={open} onToggle={() => setOpen(!open)}
      extra={round ? <span className="sem-text-meta ct-num">{`Round ${round.number}, the practice questions`}</span> : null}>
      {round ? `${misses.length} ${misses.length === 1 ? 'miss' : 'misses'}, grouped` : 'Misses'}
    </Title>
    {open && (!round
      ? <div className="sem-text-meta" data-testid="misses-empty">The misses of a finished round show here, with the student's DAX beside the right DAX.</div>
      : misses.length === 0
        ? <div className="sem-text-meta" data-testid="misses-none">No practice question was missed in round {round.number}.</div>
        : <>
          <div className="ct-filters" role="group" aria-label="Kind of miss">
            <button type="button" className="ct-fit" aria-pressed={!only} onClick={() => setOnly(null)} data-testid="miss-all">All {misses.length}</button>
            {MISSES.filter((m) => (round.missCounts[m] ?? 0) > 0).map((m) => <button key={m} type="button" className="ct-fit" aria-pressed={only === m} onClick={() => setOnly(only === m ? null : m)} data-testid={`miss-only-${m}`}>{MISS_LABEL[m]} {round.missCounts[m]}</button>)}
          </div>
          {round.groups.length > 0 && <div className="ct-causes" data-testid="causes">
            <div className="ct-jevline">{by === 'jev' ? <JevMark title="Jev grouped these misses in one call" /> : null}<span>{by === 'jev' ? `grouped all ${misses.length} misses into ${round.groups.length} ${round.groups.length === 1 ? 'cause' : 'causes'} in one call, so your assistant reads causes, not every miss.` : `The misses fall into ${round.groups.length} ${round.groups.length === 1 ? 'cause' : 'causes'}, found by plain rules.`}</span></div>
            {round.groups.map((g) => <div key={`${g.cause}:${g.fix}`} className="ct-cause-row"><span className="ct-num ct-cause-n">{g.count}</span><span>{g.label}</span><span className="sem-text-meta">{g.fixWords}</span></div>)}
          </div>}
          {groups.map((g) => {
            const rows = all ? g.items : g.items.slice(0, SHOWN_PER_GROUP);
            return <div key={g.miss} className="ct-group ct-sub" data-testid={`missgroup-${g.miss}`}>
              <div className="ct-group-head"><b>{MISS_LABEL[g.miss]}</b><span className="sem-text-meta ct-num">{g.items.length}</span><span className="sem-text-meta">{MISS_HINT[g.miss]}</span>
                {rows.length < g.items.length && <span className="sem-text-meta ct-push ct-num">{rows.length} of {g.items.length} shown</span>}</div>
              {rows.map((m) => <MissItem key={m.n} m={m} />)}
            </div>;
          })}
          {groups.some((g) => g.items.length > SHOWN_PER_GROUP) && <div className="ct-row ct-foot"><button type="button" className="sem-btn sem-btn-sm" data-testid="miss-more" onClick={() => setAll(!all)}>{all ? 'Show fewer' : 'Show all'}</button></div>}
        </>)}
  </section>;
}

// ---------------------------------------------------------------- where the misses sit, and each kind over the rounds
function HeatCard({ round }: { round: CopilotRound | null }) {
  const [open, setOpen] = useState(true);
  const cells = heatCells(round?.misses ?? []);
  const max = Math.max(1, ...cells.cells.flat());
  return <section className="sem-card ct-card sem-span-7" data-testid="card-heat">
    <Title testid="heat" open={open} onToggle={() => setOpen(!open)} extra={round ? <span className="sem-text-meta">Round {round.number}, kind of question by kind of miss</span> : null}>Where the misses sit</Title>
    {open && (!round || round.misses.length === 0
      ? <div className="sem-text-meta">Nothing to place yet.</div>
      : <table className="ct-table ct-heat"><thead><tr><th>Question kind</th>{MISSES.map((m) => <th key={m} className="ct-r">{MISS_SHORT[m]}</th>)}</tr></thead>
        <tbody>{cells.rows.map((k, i) => <tr key={k}><td>{KIND_LABEL[k]}</td>{cells.cells[i].map((v, j) => <td key={j} className="ct-r ct-num ct-heatcell"
          style={{ background: v ? `color-mix(in srgb, var(--sem-bad) ${14 + Math.round((v / max) * 46)}%, transparent)` : undefined }}>{v}</td>)}</tr>)}</tbody></table>)}
  </section>;
}

function KindMultiples({ rounds }: { rounds: CopilotRound[] }) {
  const [ref, w] = useWidth<HTMLDivElement>();
  const series = kindSeries(rounds);
  const across = w >= 520 ? Math.min(4, Math.max(1, series.length)) : 2, gap = 12;
  const cw = Math.max(40, (w - gap * (across - 1)) / across), ch = 46;
  return <div ref={ref} className="ct-multiples" style={{ gridTemplateColumns: `repeat(${across}, minmax(0, 1fr))`, gap }}>
    {w > 0 && series.map((k) => {
      const px = (i: number) => 2 + (i * (cw - 4)) / Math.max(1, k.series.length - 1);
      const py = (v: number) => ch - 2 - v * (ch - 6);
      return <div key={k.kind} className="ct-multiple">
        <div className="ct-multiple-head"><span className="ct-trunc">{KIND_LABEL[k.kind]}</span><span className="ct-num">{pct(k.now)}</span></div>
        <svg width={cw} height={ch} role="img" aria-label={KIND_LABEL[k.kind]}>
          <line x1={0} x2={cw} y1={ch - 2} y2={ch - 2} stroke="var(--sem-rule)" />
          {k.series.length > 1 && <path d={k.series.map((v, i) => `${i ? 'L' : 'M'}${px(i).toFixed(1)} ${py(v).toFixed(1)}`).join(' ')} fill="none" stroke="var(--sem-accent)" strokeWidth={1.6} />}
          <circle cx={px(k.series.length - 1)} cy={py(k.series[k.series.length - 1])} r={2.6} fill="var(--sem-accent)" />
        </svg>
      </div>;
    })}
  </div>;
}

function KindsCard({ rounds }: { rounds: CopilotRound[] }) {
  const [open, setOpen] = useState(true);
  const has = kindSeries(rounds).length > 0;
  return <section className="sem-card ct-card sem-span-5" data-testid="card-kinds">
    <Title testid="kinds" open={open} onToggle={() => setOpen(!open)} extra={rounds.length ? <span className="sem-text-meta">Share right, rounds 1 to {rounds[rounds.length - 1].number}</span> : null}>Each kind over the rounds</Title>
    {open && (has
      ? <KindMultiples rounds={rounds} />
      : <div className="sem-text-meta">Each kind of question gets its own small line once a round has run.</div>)}
  </section>;
}

// ---------------------------------------------------------------- the bank, by kind
function answerText(q: BankQuestion): string {
  if (q.tier === 'refusal' || q.expectRefusal) return 'Say no';
  if (q.expectedMatrix && q.expectedMatrix.length) return `${q.expectedMatrix.length} rows`;
  return q.expectedValue === 'BLANK' ? 'blank' : q.expectedValue ?? '';
}

function BankCard({ view, bank, latest }: { view: CopilotRoundsView | null; bank: BankQuestion[]; latest: CopilotRound | null }) {
  const [open, setOpen] = useState(true);
  const rows = bankRows(view, latest);
  const first = rows.length ? rows[0].kind : 'totals';
  const [kind, setKind] = useState<QKind>(first);
  const shown = rows.some((r) => r.kind === kind) ? kind : first;
  const max = Math.max(1, ...rows.map((r) => r.n));
  const questions = bank.filter((q) => q.kind === shown).slice(0, 12);
  return <section className="sem-card ct-card sem-span-7" data-testid="card-bank">
    <Title testid="bank" open={open} onToggle={() => setOpen(!open)} extra={<span className="sem-text-meta ct-num">{view ? `${view.bankSize} questions${view.heldBack ? `, ${view.heldBack} held back` : ''}` : ''}</span>}>Question bank</Title>
    {open && (rows.length === 0
      ? <>
        <div className="sem-text-meta" data-testid="bank-empty">There is no question bank yet. Your assistant writes it: 100 or more questions in the words a person would use, each with an answer the engine proved by running it.</div>
        <div className="ct-row ct-foot"><GiveToAssistant sendKey="copilot-test:bank" build={() => task('copilottest', bankTask())} primary bare label="Write the questions" /></div>
      </>
      : <>
        <div className="ct-kinds" role="list">{rows.map((r) => <button key={r.kind} type="button" role="listitem" className="ct-kind" aria-pressed={shown === r.kind} onClick={() => setKind(r.kind)} data-testid={`kind-${r.kind}`}>
          <span className="ct-kind-name">{KIND_LABEL[r.kind]}</span>
          <span className="ct-kind-bar" data-graded={r.right != null ? 'yes' : 'no'} style={{ width: `${(r.n / max) * 100}%` }}>
            {r.right != null && <span className="ct-kind-right" style={{ width: `${Math.min(100, (r.right / r.n) * 100)}%` }} />}
            {r.missed != null && r.missed > 0 && <span className="ct-kind-miss" style={{ width: `${Math.min(100, (r.missed / r.n) * 100)}%` }} />}
          </span>
          <span className="ct-kind-n ct-num">{r.right != null ? `${r.right} of ${r.n}` : r.n}</span>
        </button>)}</div>
        {latest && <div className="ct-legend sem-text-meta"><span className="ct-lg"><span className="ct-sw ct-sw-right" />Right in round {latest.number}</span><span className="ct-lg"><span className="ct-sw ct-sw-miss" />Missed</span></div>}
        <div className="ct-kind-detail" data-testid="kind-detail">
          <div className="sem-text-meta">{KIND_HINT[shown]}</div>
          <table className="ct-table ct-qtable"><thead><tr><th>Question</th><th>Right answer</th><th>Proven</th></tr></thead>
            <tbody>{questions.map((q) => <tr key={q.id}><td>{q.heldBack ? <><span className="sem-chip sem-chip-outline">Held back</span> {q.question}</> : q.question}</td><td className="ct-num">{answerText(q)}</td>
              <td><span className="sem-chip sem-chip-ok" title={q.query || 'No DAX: the right answer is to say no'}>{q.query ? 'Ran the DAX' : 'Checked: no data'}</span></td></tr>)}</tbody></table>
        </div>
        <div className="ct-row ct-foot">
          <GiveToAssistant sendKey={`copilot-test:more:${view?.bankSize ?? 0}`} build={() => task('copilottest', { words: 'Add more Copilot test questions', brief: 'Use the interview-model skill. Add questions to the Copilot test bank, balanced across the kinds, the words a person would use, each with a reference DAX, in one call to add_interview_questions.' })} bare label="Ask for more" />
          <span className="sem-text-meta ct-push">Kept with this model</span>
        </div>
      </>)}
  </section>;
}

// ---------------------------------------------------------------- who answers, and what it sees
function StudentCard({ latest, view }: { latest: CopilotRound | null; view: CopilotView | null }) {
  const [open, setOpen] = useState(true);
  return <section className="sem-card ct-card sem-span-5" data-testid="card-student">
    <Title testid="student" open={open} onToggle={() => setOpen(!open)}>Who answers</Title>
    {open && <>
      <div className="ct-student"><b>{studentLabel(latest)}</b><span className="sem-text-meta">A small model, close to what Copilot uses. It is the stand-in: a Copilot score only comes from Copilot.</span></div>
      <div className="ct-sees">{(view?.sees ?? []).map((x) => <div key={x.what} className={`ct-see ${x.on ? '' : 'ct-see-off'}`}>
        <span className="ct-mark" aria-hidden="true">{x.on ? '✓' : '×'}</span><span>{x.what}</span><span className="sem-text-meta ct-num">{x.count}</span></div>)}</div>
      <div className="sem-text-meta ct-foot">{WHO_SEES}</div>
    </>}
  </section>;
}

// ---------------------------------------------------------------- the page
export function CopilotTestView() {
  const { rounds, detail, bank, view, error, progress } = useCopilotTest();
  const list = rounds?.rounds ?? [];
  const last = latestOf(list);
  const next = (last?.number ?? 0) + 1;
  const hasBank = (rounds?.bankSize ?? 0) > 0;
  const driver = useDriver();
  return <div className="sem-page ct-page" data-testid="copilot-test">
    <div className="ct-head" data-testid="ct-head">
      <div className="ct-head-row"><h1 className="sem-page-title">Copilot test</h1><span className="ct-push" />
        <GiveToAssistant sendKey="copilot-test:bank" build={() => task('copilottest', bankTask())} bare size="md" primary={!hasBank} label="New question bank" />
        {hasBank && <GiveToAssistant sendKey={`copilot-test:round:${next}`} build={() => task('copilottest', roundTask(next))} primary bare size="md" label={`Run round ${next}`} />}
      </div>
      <div className="ct-sum sem-text-meta">{headSummary(rounds, last).map((s) => <span key={s} className="ct-num">{s}</span>)}</div>
      <div className="sem-text-meta" data-testid="ct-assistant-note">{assistantNote(driver.assistantConnected)}</div>
      <PublishThenTest onPublished={async () => { await giveToAssistant(hasBank ? `copilot-test:round:${next}` : 'copilot-test:bank', () => task('copilottest', hasBank ? roundTask(next) : bankTask())); }} />
      {error && <div className="sem-text-meta" role="alert" data-testid="ct-error"><span className="sem-chip sem-chip-bad" aria-hidden="true" /> {error}</div>}
    </div>
    <ScoreCard rounds={list} /><RoundCard rounds={list} progress={progress} hasBank={hasBank} />
    {list.length > 0 && <>
      <ChangesCard rounds={list} />
      <MissesCard round={detail} />
      <HeatCard round={detail} /><KindsCard rounds={list} />
    </>}
    <BankCard view={rounds} bank={bank} latest={last} /><StudentCard latest={last} view={view} />
  </div>;
}
