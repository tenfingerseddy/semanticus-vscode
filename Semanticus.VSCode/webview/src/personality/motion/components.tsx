import { useEffect, useRef, type ReactNode } from 'react';
import { countAt, gradeFrames, isBigWin, settleChoices } from './state.mjs';
import { useMotionClock, useMotionCue, useMotionEffect, type MotionSound } from './runtime';
import './motion.css';

function useSound(eventKey: unknown, name: MotionSound, policy: ReturnType<typeof useMotionEffect>, enabled = true) {
  const handled = useRef<unknown>(Symbol());
  useEffect(() => {
    if (handled.current === eventKey) return;
    handled.current = eventKey;
    if (enabled) policy.play(name);
  }, [eventKey, enabled, name, policy]);
}

/** One board part for grades and numbers. The accessible value is always the confirmed final value. */
export function GradeFlip({ grade, from = 'D', eventKey = grade }: { grade: string; from?: string; eventKey?: unknown }) {
  const root = useRef<HTMLSpanElement>(null);
  const previous = useRef(from);
  const halves = useRef<HTMLSpanElement>(null);
  const values = gradeFrames(previous.current, grade);
  const last = useRef('');
  const animation = useRef<Animation[]>([]);
  const policy = useMotionClock(root, eventKey, k => {
    const value = values[Math.min(values.length - 1, Math.floor(k * values.length))];
    if (value === last.current) return;
    const host = halves.current;
    if (host) {
      animation.current.forEach(a => a.cancel());
      const old = last.current || values[0];
      const spans = host.querySelectorAll<HTMLElement>('[data-face]');
      spans.forEach(s => { s.textContent = s.dataset.face === 'old' ? old : value; });
      if (k > 0 && k < 1) {
        const top = host.querySelector('[data-moving="top"]');
        const bottom = host.querySelector('[data-moving="bottom"]');
        animation.current = [
          top?.animate([{ transform: 'rotateX(0deg)', opacity: 1 }, { transform: 'rotateX(-90deg)', opacity: 0 }], { duration: policy.quiet ? 40 : 120, fill: 'forwards' }),
          bottom?.animate([{ transform: 'rotateX(90deg)', opacity: 0 }, { transform: 'rotateX(0deg)', opacity: 1 }], { duration: policy.quiet ? 40 : 120, delay: policy.quiet ? 40 : 120, fill: 'both' }),
        ].filter((a): a is Animation => !!a);
      }
    }
    last.current = value;
    if (root.current) root.current.dataset.grade = value;
    if (k === 1) previous.current = grade;
  }, 1200);
  useEffect(() => () => animation.current.forEach(a => a.cancel()), []);
  useEffect(() => { if (!policy.moving) animation.current.forEach(a => a.cancel()); }, [policy.moving]);
  useMotionCue(root, eventKey, 'pop', 1200, grade === 'A');
  return <span className="sem-motion-flap" data-grade={grade} ref={root} role="img" aria-label={`Grade ${grade}`} data-testid="motion-grade">
    <span ref={halves} aria-hidden="true">
      <span className="sem-motion-half sem-motion-top"><span data-face="new">{grade}</span></span>
      <span className="sem-motion-half sem-motion-bottom"><span data-face="new">{grade}</span></span>
      <span className="sem-motion-half sem-motion-top sem-motion-turn" data-moving="top"><span data-face="old">{grade}</span></span>
      <span className="sem-motion-half sem-motion-bottom sem-motion-turn" data-moving="bottom"><span data-face="new">{grade}</span></span>
    </span>
    <BigWin kind={grade === 'A' ? 'grade-a' : undefined} eventKey={eventKey} delayMs={1200} />
  </span>;
}

export function BulkCount({ count, elapsedMs, noun = 'changes', eventKey = count }: { count: number; elapsedMs: number; noun?: string; eventKey?: unknown }) {
  const root = useRef<HTMLDivElement>(null), number = useRef<HTMLSpanElement>(null), bar = useRef<HTMLElement>(null);
  const lastTick = useRef(0);
  const policy = useMotionClock(root, eventKey, k => {
    if (number.current) number.current.textContent = countAt(0, count, k).toLocaleString('en-GB');
    if (bar.current) bar.current.style.transform = `scaleX(${k})`;
    const now = performance.now();
    if (k > 0 && k < 1 && now - lastTick.current >= 40) { lastTick.current = now; policy.play('tick', { k }); }
  });
  const time = `${(elapsedMs / 1000).toFixed(1)} s`;
  return <div className="sem-motion-bulk" ref={root} data-testid="motion-bulk">
    <span className="sem-motion-count" ref={number} aria-hidden="true">{count.toLocaleString('en-GB')}</span>
    <span className="sem-motion-bar" aria-hidden="true"><i ref={bar} /></span>
    <span role="status">{count.toLocaleString('en-GB')} {noun} in {time}. One undo step.</span>
  </div>;
}

/** Wrap the Diagram's existing path and markers. Geometry stays fixed during the snap. */
export function RelationshipSnap(props: { children: ReactNode; eventKey: unknown; enabled?: boolean; labels?: ReactNode }) {
  if (props.enabled === false) return <g>{props.children}{props.labels}</g>;
  return <AnimatedRelationshipSnap {...props} />;
}

function AnimatedRelationshipSnap({ children, eventKey, labels }: { children: ReactNode; eventKey: unknown; labels?: ReactNode }) {
  const root = useRef<SVGGElement>(null), line = useRef<SVGGElement>(null), ends = useRef<SVGGElement>(null);
  const policy = useMotionEffect(root, eventKey, animate => {
    animate(line.current, [{ transform: 'scale(0.94)', opacity: 0 }, { transform: 'scale(1.015)', opacity: 1, offset: 0.72 }, { transform: 'scale(1)', opacity: 1 }], { duration: 520 });
    animate(ends.current, [{ transform: 'translateY(6px)', opacity: 0 }, { transform: 'translateY(0)', opacity: 1 }], { delay: 360, duration: 260 });
  });
  useMotionCue(root, eventKey, 'snap', 360);
  return <g ref={root} data-testid="motion-relationship"><g ref={line} className="sem-motion-rel-line">{children}</g><g ref={ends}>{labels}</g></g>;
}

// The design's S, split into fixed pieces. Only their opacity changes, never a path or dash offset per frame.
const S_PARTS = [
  'M19 5.5H15.4', 'M15.4 5.5H11.8', 'M11.8 5.5H8.25',
  'M8.25 5.5A3.25 3.25 0 0 0 5.952 6.452', 'M5.952 6.452A3.25 3.25 0 0 0 5 8.75',
  'M5 8.75A3.25 3.25 0 0 0 5.952 11.048', 'M5.952 11.048A3.25 3.25 0 0 0 8.25 12',
  'M8.25 12H12', 'M12 12H15.75',
  'M15.75 12A3.25 3.25 0 0 1 18.048 12.952', 'M18.048 12.952A3.25 3.25 0 0 1 19 15.25',
  'M19 15.25A3.25 3.25 0 0 1 18.048 17.548', 'M18.048 17.548A3.25 3.25 0 0 1 15.75 18.5',
  'M15.75 18.5H12.15', 'M12.15 18.5H8.55', 'M8.55 18.5H5',
];

export function PublishDraw({ eventKey, state = 'success', first = false, production = true, children }: {
  eventKey: unknown; state?: 'working' | 'success' | 'error'; first?: boolean; production?: boolean; children?: ReactNode;
}) {
  const root = useRef<HTMLDivElement>(null), drawing = useRef<SVGSVGElement>(null);
  const policy = useMotionEffect(root, eventKey, animate => {
    if (state === 'error') return;
    drawing.current?.querySelectorAll('[data-piece]').forEach((piece, index) => animate(piece, [{ opacity: 0 }, { opacity: 1 }], { delay: index * 100, duration: 160 }));
    if (state === 'success') animate(drawing.current?.querySelector('[data-end]') ?? null,
      [{ transform: 'scale(0)', opacity: 0 }, { transform: 'scale(1.4)', opacity: 1, offset: 0.65 }, { transform: 'scale(1)', opacity: 1 }], { delay: 1600, duration: 380 });
  });
  useMotionCue(root, eventKey, 'chime', 1980, state === 'success');
  return <div ref={root} className={`sem-motion-publish sem-motion-publish-${state}`} data-testid="motion-publish">
    <span className="sem-motion-publish-mark">
      <svg ref={drawing} viewBox="0 0 24 24" aria-hidden="true">
        <path className="sem-motion-ghost" d="M19 5.5H8.25a3.25 3.25 0 0 0 0 6.5h7.5a3.25 3.25 0 0 1 0 6.5H5" />
        {S_PARTS.map((d, i) => <path key={i} data-piece={i} d={d} className={i === 7 || i === 8 ? 'sem-motion-jade' : ''} />)}
        <circle cx="19" cy="5.5" r="1.9" className="sem-motion-dot" />
        {state === 'success' && <circle data-end cx="5" cy="18.5" r="1.9" className="sem-motion-dot sem-motion-end" />}
      </svg>
      <BigWin kind={first && state === 'success' ? 'first-publish' : undefined} production={production} eventKey={eventKey} delayMs={1980} />
    </span>
    {children}
  </div>;
}

export interface RewindChange { id: string; text: string }
export function UndoRewind({ changes, fromRevision, toRevision, direction = 'undo', eventKey, partial = false }: {
  changes: RewindChange[]; fromRevision?: number; toRevision?: number; direction?: 'undo' | 'redo'; eventKey: unknown; partial?: boolean;
}) {
  const root = useRef<HTMLDivElement>(null), revision = useRef<HTMLSpanElement>(null), rows = useRef<HTMLUListElement>(null);
  const policy = useMotionEffect(root, eventKey, animate => {
    const elements = Array.from(rows.current?.children ?? []);
    (direction === 'undo' ? elements.reverse() : elements).forEach((row, i) => animate(row, direction === 'undo'
      ? [{ transform: 'translateX(0)', opacity: 1 }, { transform: 'translateX(24px)', opacity: 0 }]
      : [{ transform: 'translateX(24px)', opacity: 0 }, { transform: 'translateX(0)', opacity: 1 }], { delay: i * Math.min(120, 800 / changes.length), duration: 180 }));
  });
  useMotionClock(root, eventKey, k => {
    if (revision.current && fromRevision != null && toRevision != null) revision.current.textContent = String(fromRevision + Math.round((toRevision - fromRevision) * k));
    if (root.current) root.current.dataset.complete = k === 1 ? 'true' : 'false';
  }, Math.min(800, changes.length * 120) + 180);
  useSound(eventKey, 'rewind', policy, direction === 'undo' && changes.length >= 3);
  return <div ref={root} className="sem-motion-undo" data-testid="motion-undo">
    <div className="sem-inline"><span role="status">{changes.length} {partial ? 'shown changes' : 'changes'} {direction === 'undo' ? 'undone' : 'redone'}.</span>
      {toRevision != null && <span className="sem-text-meta" title="The original change number in History.">History change <span ref={revision} aria-hidden="true">{toRevision}</span><span className="sem-motion-sr">{toRevision}</span></span>}</div>
    <ul ref={rows} aria-hidden="true" className={direction === 'undo' ? 'sem-motion-remove' : ''}>{changes.map(c => <li key={c.id}>{c.text}</li>)}</ul>
  </div>;
}

export interface JevChoice { id: string; label: string; confidence: number }
export function JevSettle({ choices, chosenId, eventKey }: { choices: JevChoice[]; chosenId?: string | null; eventKey: unknown }) {
  const root = useRef<HTMLUListElement>(null);
  const sorted = settleChoices(choices);
  const policy = useMotionEffect(root, eventKey, animate => {
    sorted.forEach((choice, index) => {
      const row = root.current?.children[index] as HTMLElement | undefined;
      animate(row ?? null, [{ transform: `translateY(calc(var(--sem-row-2) * ${choice.index - index}))`, opacity: 0.6 }, { transform: 'translateY(0)', opacity: 1 }], { delay: 350, duration: 480 });
      animate(row?.querySelector('i') ?? null, [{ transform: 'scaleX(0)' }, { transform: `scaleX(${choice.confidence})` }], { duration: 700 });
    });
  });
  useSound(eventKey, 'settle', policy);
  return <ul ref={root} className="sem-motion-choices" data-testid="motion-jev">{sorted.map(choice => <li key={choice.id} data-chosen={choice.id === chosenId || undefined}>
    <span className="sem-motion-choice-name">{choice.label}{choice.id === chosenId && <small>Jev chose this</small>}</span>
    <span className="sem-motion-meter" aria-hidden="true"><i style={{ transform: `scaleX(${choice.confidence})` }} /></span>
    <span className="sem-num">{Math.round(choice.confidence * 100)}%</span>
  </li>)}</ul>;
}

export function BigWin({ kind, eventKey, production = false, ok = true, destructive = false, delayMs = 0 }: {
  kind?: 'grade-a' | 'zero-findings' | 'first-publish'; eventKey: unknown; production?: boolean; ok?: boolean; destructive?: boolean; delayMs?: number;
}) {
  const root = useRef<SVGSVGElement>(null);
  const allowed = isBigWin(kind, { ok, production, destructive });
  const policy = useMotionEffect(root, eventKey, animate => {
    if (!allowed || !policy.sparkle) return;
    root.current?.querySelectorAll('path').forEach((path, i) => animate(path,
      [{ transform: 'scale(0)', opacity: 0 }, { transform: 'scale(1)', opacity: 1, offset: 0.3 }, { transform: 'scale(1.25)', opacity: 0 }], { duration: 700, delay: delayMs + i * 45, fill: 'both' }));
  });
  useMotionCue(root, eventKey, 'pop', delayMs, allowed && kind === 'zero-findings');
  return <svg ref={root} className="sem-motion-sparkle" viewBox="0 0 100 100" aria-hidden="true" data-testid="motion-sparkle" style={{ display: allowed && policy.sparkle ? undefined : 'none' }}>
    {[[-3, 22], [76, 8], [93, 58], [48, 92], [12, 72]].map(([x, y], i) => <path key={i} d={`M${x} ${y - 5}Q${x} ${y} ${x + 5} ${y}Q${x} ${y} ${x} ${y + 5}Q${x} ${y} ${x - 5} ${y}Q${x} ${y} ${x} ${y - 5}`} style={{ transformOrigin: `${x}px ${y}px` }} />)}
  </svg>;
}

/** The next-step lane calls this wrapper when a confirmed next step changes. */
export function NextGlide({ children, eventKey }: { children: ReactNode; eventKey: unknown }) {
  const root = useRef<HTMLDivElement>(null);
  useMotionEffect(root, eventKey, animate => animate(root.current,
    [{ transform: 'translateY(14px)', opacity: 0 }, { transform: 'translateY(-2px)', opacity: 1, offset: 0.7 }, { transform: 'translateY(0)', opacity: 1 }]));
  return <div ref={root} className="sem-motion-next" data-testid="motion-next">{children}</div>;
}

export function NextMotionStub({ text, eventKey }: { text: string; eventKey: unknown }) {
  return <NextGlide eventKey={eventKey}><span><b>Next:</b> {text}</span></NextGlide>;
}

export function SmallWin({ children, eventKey }: { children: ReactNode; eventKey: unknown }) {
  const root = useRef<HTMLSpanElement>(null);
  useMotionEffect(root, eventKey, animate => animate(root.current, [{ transform: 'scale(1)' }, { transform: 'scale(1.06)', offset: 0.4 }, { transform: 'scale(1)' }]));
  return <span ref={root} className="sem-motion-small">{children}</span>;
}
