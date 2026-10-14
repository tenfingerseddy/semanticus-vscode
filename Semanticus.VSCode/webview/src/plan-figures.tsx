import { useEffect, useId, useRef, useState } from 'react';
import { GradeFlip } from './personality/motion';
import { usePersonalitySettings } from './personality/settings';
import { gradeBand, band } from './readinessband.mjs';
import { figuresOf, tipOf, spokenOf, type Figure, type GradeFigure, type PlanFigure } from './plan-figures-model';
import type { Grade } from './plan-calm-model';
import type { PlanStrip } from './plan-model';

// The three small figures beside the Plan's title (1.5, Kane's pick A): a grey label on top, the number under it, a thin rule between.
// Each is one tab stop with a tooltip that shows on hover and on keyboard focus (Escape hides it). The numbers come from the engine (plan-figures-model.ts).

/** A number that counts to its new value, so a change reads as a climb. The engine's value is the real one; the count is only the way it is shown. */
function useCountTo(target: number, reduced: boolean): number {
  const [shown, setShown] = useState(target);
  const from = useRef(target);
  useEffect(() => {
    if (reduced || from.current === target) { from.current = target; setShown(target); return undefined; }
    const start = from.current, t0 = performance.now();
    let frame = 0;
    const tick = (now: number) => {
      const k = Math.min(1, (now - t0) / 600);
      setShown(Math.round(start + (target - start) * (1 - Math.pow(1 - k, 3))));
      if (k < 1) frame = requestAnimationFrame(tick); else from.current = target;
    };
    frame = requestAnimationFrame(tick);
    return () => { cancelAnimationFrame(frame); from.current = target; };
  }, [target, reduced]);
  return shown;
}

function GradeValue({ f }: { f: GradeFigure }) {
  const tone = gradeBand(f.letter) ?? (f.score !== null ? band(f.score) : 'good');
  const reduced = usePersonalitySettings().reducedMotion;
  const score = useCountTo(f.score ?? 0, reduced);
  return <span className={`pc-fv pc-grade sem-rag-${tone}`}>
    <span className="pc-letter" data-testid={`fig-${f.key}-letter`}><GradeFlip grade={f.letter} from={f.key === 'ai' ? 'D' : f.letter} eventKey={`${f.key}:${f.letter}`} /></span>
    {f.score !== null && <span key={f.score} className="sem-num pc-score pc-bump" data-testid={`fig-${f.key}-score`} data-value={f.score}>{score}</span>}
  </span>;
}

function PlanValue({ f }: { f: PlanFigure }) {
  const reduced = usePersonalitySettings().reducedMotion;
  const percent = useCountTo(f.percent, reduced);
  return <span className="pc-fv">
    <span key={f.percent} className="sem-num pc-pct pc-bump" data-testid="fig-plan-percent" data-value={f.percent}>{percent}%</span>
    <span className="pc-minibar" aria-hidden="true"><i style={{ width: `${f.percent}%` }} /></span>
  </span>;
}

function FigureView({ f }: { f: Figure }) {
  const id = useId();
  const [hidden, setHidden] = useState(false);
  const tip = tipOf(f);
  return <span className="pc-fig" tabIndex={0} role="group" aria-label={spokenOf(f)} aria-describedby={id} data-testid={`fig-${f.key}`} data-hidden={hidden || undefined}
    onMouseEnter={() => setHidden(false)} onFocus={() => setHidden(false)} onKeyDown={(e) => { if (e.key === 'Escape') setHidden(true); }}>
    <span className="pc-fl">{f.label}</span>
    {f.key === 'plan' ? <PlanValue f={f} /> : <GradeValue f={f} />}
    <span className="pc-tip" role="tooltip" id={id} data-testid={`fig-${f.key}-tip`}><b>{tip.head}</b>{tip.what}<span className="pc-tip-src">{tip.src}</span></span>
  </span>;
}

export function PlanFigures({ grade, strip }: { grade: Grade | null; strip: PlanStrip | null | undefined }) {
  const figures = figuresOf(grade, strip);
  if (figures.length === 0) return null;
  return <span className="pc-figs" role="group" aria-label="How the model stands" data-testid="plan-figures">
    {figures.map((f) => <FigureView key={f.key} f={f} />)}
  </span>;
}
