import * as P from './plan-model';
import { DoerMark } from './plan-row';
import { gradeBand } from './readinessband.mjs';

// ===================================================================================================
// The strip at the top of the Plan (Kane's pick A, 24 September; contract 9; colour level B).
//
// Three tracks, one per doer, each on its own soft tint: a segment per row up to twelve, then a count, and the three
// counts at the end. The counts are the ENGINE's (ModelPlanView.Strip), so the page, the fold line and the summary
// read one answer. Selecting a track filters the list to that doer, which is page state and costs no call. The
// Latest mark opens History.
// ===================================================================================================

// The AI readiness grade takes its band colour (Kane, 8 Oct): the same green, amber or red as the AI readiness page, from one
// band function. The best practice letter is a different grade and stays plain.
const bandOf = (letter: string) => { const b = gradeBand(letter); return b ? `pw-grade sem-rag-${b} sem-rag-text` : 'pw-grade'; };

export function PlanStrip({ plan, track, onTrack, onLatest }: {
  plan: P.Plan; track: P.PlanDoer | null; onTrack: (doer: P.PlanDoer | null) => void; onLatest: () => void;
}) {
  const grade = P.gradeLine(plan.strip);
  const latest = P.latestLine(plan.strip);
  return <section className="pw-strip" data-testid="plan-strip" data-tour="strip" aria-label="The plan at a glance">
    <div className="pw-strip-h">
      {grade
        ? <span data-testid="plan-grade">AI readiness <b className={bandOf(grade.now)}>{grade.now}</b> now
          {grade.after && <> <span className="plan-muted">to</span> <b className={bandOf(grade.after)}>{grade.after}</b> when the plan is done</>}</span>
        : <span className="plan-muted">No grade yet</span>}
      {plan.strip?.bestPractice && <span data-testid="plan-best-practice" title={plan.strip.bestPracticeWhy ?? undefined}>
        <span className="plan-muted">Best practice</span> <b className="pw-grade">{plan.strip.bestPractice}</b></span>}
      {latest && <button type="button" className="pw-latest" data-testid="plan-latest" onClick={onLatest}>
        <span className="pw-latest-dot" aria-hidden="true" />
        <span className="plan-muted">Latest</span> <span className="sem-num">{latest.time}</span>
        <span className="pw-trunc">{latest.sentence}</span>
        <span className="plan-link">History</span>
      </button>}
    </div>
    {P.stripTracks(plan).map((t) => {
      const { segs, more } = P.stripSegments(t);
      const on = track === t.doer;
      return <button key={t.doer} type="button" className={`pw-track pw-track-${t.doer}`} aria-pressed={on}
        data-testid={`plan-track-${t.doer}`} onClick={() => onTrack(on ? null : t.doer)}>
        <span className="pw-track-who"><DoerMark doer={t.doer} />{P.DOER_LABEL[t.doer]}</span>
        <span className="pw-bar" aria-hidden="true">
          {segs.map((s, i) => <i key={i} className={`pw-seg pw-seg-${s}`} />)}
          {more > 0 && <span className="pw-bar-more sem-num" title={`${more} more tasks than the bar shows`}>+{more} more</span>}
        </span>
        <span className="pw-counts" data-testid={`plan-counts-${t.doer}`}>
          {P.trackCounts(t).map((c) => <span key={c.key} className={c.warn ? 'pw-count-warn' : undefined}>{c.text}</span>)}
        </span>
      </button>;
    })}
    {track && <div className="pw-legend plan-muted">
      <span />
      <span />
      <span>Choose the track again to see every task</span>
    </div>}
  </section>;
}
