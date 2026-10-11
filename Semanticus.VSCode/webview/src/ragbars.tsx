import { band } from './readinessband.mjs';

export interface RagRow { name: string; score: number; detail: string }

// One bar per category, in the band colour of its score (readinessband.mjs), with a mark at 60 and at 80 where the colour
// changes. The number is always shown, so the colour is never the only way to read the score. AI readiness and Model quality
// both draw their categories here, so the two pages cannot drift apart.
export function RagBars({ rows, testId }: { rows: RagRow[]; testId?: string }) {
  return (
    <div className="flex flex-col gap-2 mt-2" data-testid={testId}>
      {rows.map((c) => (
        <div key={c.name} className={`flex items-center gap-3 sem-rag-${band(c.score)}`} title={`${c.name}: ${c.score.toFixed(0)} of 100`}>
          <div className="w-48 shrink-0">{c.name}</div>
          <div className="sem-rag-bar">
            <i className="sem-rag-fill" style={{ width: `${c.score}%` }} />
            <s className="sem-rag-mark" style={{ left: '60%' }} aria-hidden="true" />
            <s className="sem-rag-mark" style={{ left: '80%' }} aria-hidden="true" />
          </div>
          <div className="w-12 text-right sem-num sem-rag-score-cell">{c.score.toFixed(0)}</div>
          <div className="w-40 text-right sem-num sem-text-meta">{c.detail}</div>
        </div>
      ))}
    </div>
  );
}
