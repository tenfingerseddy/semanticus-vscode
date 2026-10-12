// Explain This Number (feature #2) — the slide-over panel behind "Explain this number" on any value cell
// in DAX Lab (matrix / table / result grid). It calls the engine's explain_value once and renders the
// deterministic dossier: the value re-derived in the cell's exact filter context, the why-is-this-blank
// checklist, the top contributors (which the ENGINE refuses to show when the parts don't sum), what feeds
// the number, where the data comes from, and a row-security heads-up. The panel never narrates beyond the
// engine's deterministic sentences; "Give to your assistant" puts a ready prompt on the Plan (Copy beside it) so the
// user's own assistant does the interpreting (golden rule: the engine runs no inference).
// Copy rules (Kane's UX bar): plain language, jargon always glossed, no em-dashes, never "Claude".

import { useEffect, useState } from 'react';
import { rpc } from './bridge';
import { GiveToAssistant, explainTask } from './sendtask';
import { plainNumber } from './resultformat.mjs';
import { formatNumber } from './gridformat';
import type { CtxEntry } from './daxvisual';
import { uiLabel } from './copy';

// ---- wire shapes (camelCase of Semanticus.Engine Explain DTOs) ----------------------------------
export interface ExplainWireFilter { column: string; members: string[]; empty?: boolean }
interface ChainNode { ref: string; name: string; kind: string; depth: number }
interface LineageEntry { column: string; table?: string; source?: string }
interface ContributorRow { member: string; value: string; pct?: number | null }
interface Contributors { dimension?: string; additive: boolean; rows: ContributorRow[]; truncated: boolean; note?: string; spread?: ContributorRow[] }
interface BlankCheck { id: string; question: string; result: 'ok' | 'cause' | 'maybe' | 'skipped'; finding: string; detail?: string }
interface Blank { checks: BlankCheck[]; likelyCauseId?: string; summary?: string }
interface RlsRole { role: string; filters: { table: string; filterExpression?: string }[] }
interface Driver { name: string; value: string; format?: string | null }
interface Evidence { available: boolean; query?: string; totalMs?: number; feMs?: number; seMs?: number; seQueries?: number; note?: string }
export interface ExplainDossier {
  status: string; measure: string; name?: string; expression?: string;
  valueEvaluated: boolean; value?: string; isBlank?: boolean | null; summary?: string;
  format?: string | null; drivers?: Driver[];
  chain: ChainNode[]; chainTruncated?: boolean; lineage: LineageEntry[];
  contributors?: Contributors | null; blank?: Blank | null; rls: RlsRole[];
  evidence?: Evidence; note?: string; error?: string;
}

/** What a right-clicked cell hands the panel (built in daxvisual.tsx from the wells). */
export interface ExplainPayload {
  measureName: string;                 // display name; the engine resolves it (unique names work)
  value: string;                       // the cell's value as shown (display only)
  groupBy: string[];                   // the visual's AXIS columns — re-created as group-by scope by the engine (Finding A)
  filters: ExplainWireFilter[];        // pinned row/col keys as single-member filters
  extraPredicates: string[];           // the filter well's DAX predicate lines
  entries: CtxEntry[];                 // display chips for "your selection"
  format?: string | null;              // the measure's own format string, so the number reads as the model shows it (35.8%, not 0.3578)
}

/** A value the engine sent as text, shown in the measure's own format when it has one. */
/** A selection value arrives as the engine wrote it ("= 2025"); the "=" is drawn by the line itself. */
export const plainValue = (v: string): string => v.replace(/^=\s*/, '');
/** The selection as one plain line: "Year = 2025; City = Perth". */
export const selectionWords = (entries: readonly { field: string; value: string }[]): string => entries.map((e) => `${e.field} = ${plainValue(e.value)}`).join('; ');

export const fmtNum = (v: string | undefined, format?: string | null) => {
  if (v == null) return '';
  const n = Number(v);
  return Number.isFinite(n) ? (formatNumber(n, format) ?? plainNumber(n)) : v;
};

/** The plain lead: what the number is, for which selection. No engine words, no raw figure. */
export function leadSentence(measure: string, d: ExplainDossier, format: string | null | undefined, entries: CtxEntry[]): string {
  if (!d.valueEvaluated || d.isBlank) return d.summary ?? '';
  const where = entries.length ? ` for ${entries.map((e) => plainValue(e.value)).join(' and ')}` : '';
  return `${measure} is ${fmtNum(d.value, format)}${where}.`;
}

/** The measures this number is built from, each worked out in the same selection. */
function Drivers({ drivers }: { drivers: Driver[] }) {
  return (
    <div className="flex flex-col gap-1">
      {drivers.map((r, i) => (
        <div key={i} className="flex items-baseline gap-2 text-xs">
          <span className="truncate" style={{ color: 'var(--sem-fg)' }}>{r.name}</span>
          <span className="ml-auto tnum font-semibold shrink-0" style={{ color: 'var(--sem-fg)' }}>{fmtNum(r.value, r.format)}</span>
        </div>
      ))}
    </div>
  );
}

/** The column a dimension ref names: 'Product'[Brand] is Brand. */
const dimensionName = (dim?: string) => (dim ? (/\[([^\]]+)\]\s*$/.exec(dim)?.[1] ?? dim) : 'each part');

/** For a number whose parts do not add up (a ratio, an average): the value in each member, highest first then lowest. */
function Spread({ c, format }: { c: Contributors; format?: string | null }) {
  const rows = c.spread ?? [];
  if (rows.length < 2) return null;
  return (
    <Section title={`How it differs by ${dimensionName(c.dimension)}`} always>
      <div className="flex flex-col gap-1">
        {rows.map((r, i) => (
          <div key={i} className="flex items-baseline gap-2 text-xs">
            <span className="truncate" title={r.member} style={{ color: 'var(--sem-fg)' }}>{r.member}</span>
            <span className="ml-auto tnum font-semibold shrink-0" style={{ color: 'var(--sem-fg)' }}>{fmtNum(r.value, format)}</span>
          </div>
        ))}
      </div>
    </Section>
  );
}

/** The explanation of one number, inline: used by Ask your data when the question asked why. The same dossier as the panel. */
export function ExplainInline({ d, measureName, format }: { d: ExplainDossier; measureName: string; format?: string | null }) {
  const fmt = d.format ?? format;
  return (
    <div data-explain-inline className="flex flex-col gap-3">
      <div className="text-(length:--sem-text-body) font-semibold" style={{ color: 'var(--sem-fg)' }}>{leadSentence(measureName, d, fmt, [])}</div>
      {d.drivers && d.drivers.length > 0 && (
        <Section title="What it is built from" always><Drivers drivers={d.drivers} /></Section>
      )}
      {d.contributors && d.contributors.additive && d.contributors.rows.length > 0 && (
        <Section title="What makes it up" always>
          <ContributorBars rows={d.contributors.rows} format={fmt} />
          {d.contributors.note && <div className="text-(length:--sem-text-label) mt-2" style={{ color: 'var(--sem-muted)' }}>{d.contributors.note}</div>}
        </Section>
      )}
      {d.contributors && !d.contributors.additive && <Spread c={d.contributors} format={fmt} />}
      {d.contributors && !d.contributors.additive && d.contributors.note && (
        <div className="text-(length:--sem-text-label) leading-5" style={{ color: 'var(--sem-muted)' }}>{d.contributors.note}</div>
      )}
    </div>
  );
}

export function ExplainPanel({ payload, onClose }: { payload: ExplainPayload; onClose: () => void }) {
  const [d, setD] = useState<ExplainDossier | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    setD(null); setErr(null);
    rpc<ExplainDossier>('explainValue', payload.measureName,
      { groupBy: payload.groupBy, filters: payload.filters, extraPredicates: payload.extraPredicates }, true, null, 5)
      .then((r) => { if (alive) setD(r); })
      .catch((e) => { if (alive) setErr(String((e as Error).message ?? e)); });
    return () => { alive = false; };
  }, [payload]);

  const fmt = d?.format ?? payload.format;
  const prompt = buildAssistantPrompt(payload, d);
  const failed = err || (d && d.status !== 'ok');

  return (
    <div data-explain-panel data-testid="explain-panel" className="fixed inset-y-0 right-0 flex flex-col" style={{
      width: 400, zIndex: 70, background: 'var(--sem-surface)', borderLeft: '1px solid var(--sem-border)',
      boxShadow: '-18px 0 44px -24px rgba(0,0,0,.7)',
    }}>
      {/* header */}
      <div className="flex items-center gap-2 px-3 py-2 shrink-0" style={{ borderBottom: '1px solid var(--sem-border)' }}>
        <div className="min-w-0">
          <div className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Explain this number</div>
          <div className="text-(length:--sem-text-body) font-semibold truncate" style={{ color: 'var(--sem-fg)' }}>{payload.measureName}</div>
        </div>
        <div className="ml-auto text-(length:--sem-text-page) font-bold tnum shrink-0">
          {d?.valueEvaluated ? (d.isBlank ? '(blank)' : fmtNum(d.value, fmt)) : fmtNum(payload.value)}
        </div>
        <button onClick={onClose} title="Close" className="shrink-0 grid place-items-center rounded"
          style={{ width: 'var(--sem-control-h-sm)', height: 'var(--sem-control-h-sm)', background: 'none', border: 'none', color: 'var(--sem-muted)', cursor: 'pointer' }}>✕</button>
      </div>

      <div className="flex-1 min-h-0 overflow-auto px-3 py-2 flex flex-col gap-3">
        {/* loading / error */}
        {!d && !err && <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>Working it out…</div>}
        {failed && (
          <div className="xe-error" role="alert">
            {err ?? d?.error ?? 'Something went wrong.'}
          </div>
        )}

        {d && d.status === 'ok' && (
          <>
            {/* the plain-language lead */}
            {(d.summary || d.valueEvaluated) && <div className="text-xs leading-5" style={{ color: 'var(--sem-fg)' }}>{leadSentence(payload.measureName, d, fmt, payload.entries) || d.summary}</div>}

            {/* your selection */}
            <Section title="Your selection" always>
              {payload.entries.length === 0
                ? <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>No filters. This is the number for everything.</div>
                : (
                  <div className="flex flex-col gap-1">
                    {payload.entries.map((e, i) => (
                      <div key={i} className="flex items-baseline gap-2 text-xs">
                        <span style={{ color: '#9cdcfe', fontFamily: 'var(--mono, monospace)', fontSize: 'var(--sem-text-label)' }}>{e.field}</span>
                        <span style={{ color: 'var(--sem-muted)' }}>=</span>
                        <span className="font-semibold" style={{ color: 'var(--sem-fg)' }}>{plainValue(e.value)}</span>
                        <span className="ml-auto text-(length:--sem-text-label) px-2 rounded" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>
                          {e.src === 'filter' ? 'filter' : e.src === 'rows' ? 'row' : 'column'}
                        </span>
                      </div>
                    ))}
                  </div>
                )}
            </Section>

            {/* why is this blank? — the ship-first slice */}
            {d.blank && (
              <Section title="Why is this blank?" always>
                {d.blank.summary && <div className="text-xs mb-2 font-medium xe-dot xe-dot-warn">{d.blank.summary}</div>}
                <div className="flex flex-col gap-2">
                  {d.blank.checks.map((c, i) => <CheckRow key={i} c={c} likely={d.blank!.likelyCauseId === c.id && c.result === 'cause'} />)}
                </div>
              </Section>
            )}

            {/* what it is built from: the measures it uses, worked out in this selection */}
            {d.drivers && d.drivers.length > 0 && (
              <Section title="What it is built from" always><Drivers drivers={d.drivers} /></Section>
            )}

            {/* what makes it up */}
            {d.contributors && (
              <Section title="What makes it up" always>
                {d.contributors.additive && d.contributors.rows.length > 0 ? (
                  <>
                    <ContributorBars rows={d.contributors.rows} format={fmt} />
                    {d.contributors.note && <div className="text-(length:--sem-text-label) mt-2" style={{ color: 'var(--sem-muted)' }}>{d.contributors.note}</div>}
                  </>
                ) : (
                  <>
                    <Spread c={d.contributors} format={fmt} />
                    <div className="text-xs leading-5" style={{ color: 'var(--sem-muted)' }}>{d.contributors.note ?? 'No breakdown is available here.'}</div>
                  </>
                )}
              </Section>
            )}

            {/* what feeds it */}
            {d.chain.length > 0 && (
              <Section title={`What feeds it (${d.chain.length})`}>
                <div className="flex flex-col gap-1">
                  {d.chain.map((n, i) => (
                    <div key={i} className="flex items-center gap-2 text-xs" style={{ paddingLeft: (n.depth - 1) * 12 }}>
                      <span className="text-(length:--sem-text-label) px-1 rounded shrink-0" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>
                        {n.kind === 'calccolumn' ? 'Calculated column' : uiLabel(n.kind)}
                      </span>
                      <span className="truncate" style={{ color: 'var(--sem-fg)' }}>{n.name}</span>
                    </div>
                  ))}
                  {d.chainTruncated && <div className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>…and more (the walk stops at a sensible depth).</div>}
                </div>
              </Section>
            )}

            {/* where the data comes from */}
            {d.lineage.length > 0 && (
              <Section title="Where the data comes from">
                <div className="flex flex-col gap-1">
                  {d.lineage.map((l, i) => (
                    <div key={i} className="text-(length:--sem-text-label) leading-5">
                      <span style={{ color: 'var(--sem-fg)' }}>{(l.table ? l.table + ' · ' : '') + l.column.slice(l.column.lastIndexOf('/') + 1)}</span>
                      {l.source && <span style={{ color: 'var(--sem-muted)' }}> ({l.source})</span>}
                    </div>
                  ))}
                </div>
              </Section>
            )}

            {/* row security */}
            {d.rls.length > 0 && (
              <Section title="Row security that could affect it">
                <div className="text-(length:--sem-text-label) mb-1" style={{ color: 'var(--sem-muted)' }}>
                  These roles limit which rows people see (row-level security). This view reads the model without a role, so a secured user's number can differ.
                </div>
                {d.rls.map((r, i) => (
                  <div key={i} className="text-(length:--sem-text-label)">
                    <span className="font-semibold" style={{ color: 'var(--sem-fg)' }}>{r.role}</span>
                    <span style={{ color: 'var(--sem-muted)' }}> filters {r.filters.map((f) => f.table).join(', ')}</span>
                  </div>
                ))}
              </Section>
            )}

            {/* evidence */}
            <Section title="How this was checked">
              {d.evidence?.available ? (
                <div className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>
                  The number was recomputed live with the exact filters of this cell.
                  {d.evidence.totalMs != null && <> Took {d.evidence.totalMs} ms.</>}
                </div>
              ) : (
                <div className="text-(length:--sem-text-label) xe-dot xe-dot-warn">
                  {d.evidence?.note ?? 'No live connection: the number itself was not recomputed.'}
                </div>
              )}
              {d.evidence?.query && (
                <details className="mt-1">
                  <summary className="text-(length:--sem-text-label) cursor-pointer select-none" style={{ color: 'var(--sem-muted)' }}>the exact query used</summary>
                  <pre className="text-(length:--sem-text-label) leading-5 px-2 py-1 rounded overflow-auto max-h-44 mt-1 whitespace-pre-wrap"
                    style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', fontFamily: 'var(--mono, monospace)' }}>{d.evidence.query}</pre>
                </details>
              )}
            </Section>
          </>
        )}
      </div>

      {/* footer: hand the dossier to the user's assistant — the UI never narrates */}
      <div className="px-3 py-2 shrink-0 flex items-center gap-2" style={{ borderTop: '1px solid var(--sem-border)' }}>
        <GiveToAssistant size="md" primary sendKey={`explain|${payload.measureName}|${JSON.stringify(payload.entries)}|${payload.extraPredicates.join(';')}`}
          build={() => explainTask(payload.measureName, d?.valueEvaluated ? (d.isBlank ? 'a blank' : fmtNum(d.value, fmt)) : fmtNum(payload.value), prompt)} />
      </div>
    </div>
  );
}

function Section({ title, always, children }: { title: string; always?: boolean; children: React.ReactNode }) {
  if (always) {
    return (
      <div>
        <div className="text-(length:--sem-text-label) font-semibold mb-1" style={{ color: 'var(--sem-muted)' }}>{title}</div>
        {children}
      </div>
    );
  }
  return (
    <details>
      <summary className="text-(length:--sem-text-label) font-semibold cursor-pointer select-none" style={{ color: 'var(--sem-muted)' }}>{title}</summary>
      <div className="mt-1">{children}</div>
    </details>
  );
}

function CheckRow({ c, likely }: { c: BlankCheck; likely: boolean }) {
  const icon = c.result === 'cause' ? '✗' : c.result === 'ok' ? '✓' : c.result === 'maybe' ? '?' : '·';
  const color = c.result === 'cause' ? 'var(--sem-bad)' : c.result === 'ok' ? 'var(--sem-good)' : c.result === 'maybe' ? 'var(--sem-warn)' : 'var(--sem-muted)';
  return (
    <div className={likely ? 'sem-inset' : 'px-3 py-1'}>
      <div className="flex items-start gap-2">
        <span className="shrink-0 font-bold text-xs xe-dot" style={{ '--xe-dot': color } as React.CSSProperties}>{icon}</span>
        <div className="min-w-0">
          <div className="text-(length:--sem-text-label) font-medium" style={{ color: 'var(--sem-fg)' }}>{c.question}{likely && <span className="ml-2 text-(length:--sem-text-label) font-semibold xe-dot xe-dot-bad">likely cause</span>}</div>
          <div className="text-(length:--sem-text-label) leading-5" style={{ color: 'var(--sem-muted)' }}>{c.finding}</div>
          {c.detail && <div className="text-(length:--sem-text-label) mt-1 font-mono truncate" title={c.detail} style={{ color: 'var(--sem-muted)', opacity: 0.8 }}>{c.detail}</div>}
        </div>
      </div>
    </div>
  );
}

function ContributorBars({ rows, format }: { rows: ContributorRow[]; format?: string | null }) {
  const vals = rows.map((r) => Math.abs(Number(r.value) || 0));
  const max = Math.max(...vals, 1e-9);
  return (
    <div className="flex flex-col gap-1">
      {rows.map((r, i) => (
        <div key={i} className="flex items-center gap-2">
          <span className="text-(length:--sem-text-label) w-32 shrink-0 truncate" title={r.member} style={{ color: 'var(--sem-fg)' }}>{r.member}</span>
          <div className="flex-1 h-2 rounded" style={{ background: 'var(--sem-surface-2)' }}>
            <div className="h-2 rounded" style={{ width: `${Math.max(2, (Math.abs(Number(r.value) || 0) / max) * 100)}%`, background: 'var(--sem-muted)', opacity: r.member.startsWith('(everything else') ? 0.4 : 0.9 }} />
          </div>
          <span className="text-(length:--sem-text-label) tnum w-16 text-right shrink-0" style={{ color: 'var(--sem-fg)' }}>{fmtNum(r.value, format)}</span>
          <span className="text-(length:--sem-text-label) tnum w-10 text-right shrink-0" style={{ color: 'var(--sem-muted)' }}>{r.pct != null ? r.pct + '%' : ''}</span>
        </div>
      ))}
    </div>
  );
}

// The copy-paste prompt for the user's own assistant: it names the MCP tool + the exact arguments so the
// assistant re-derives the SAME dossier and interprets it — the engine's evidence, the assistant's words.
function buildAssistantPrompt(p: ExplainPayload, d: ExplainDossier | null): string {
  const args = {
    measureRef: d?.measure ?? p.measureName,
    filterContext: { groupBy: p.groupBy, filters: p.filters, extraPredicates: p.extraPredicates },
  };
  const lines = [
    `Explain why the measure "${p.measureName}" shows ${d?.valueEvaluated ? (d.isBlank ? 'a blank' : d.value) : p.value || 'this value'} for this selection.`,
    p.entries.length ? 'Selection: ' + selectionWords(p.entries) + '.' : 'Selection: none (grand total).',
    'Use the Semanticus MCP tool explain_value with these arguments:',
    JSON.stringify(args, null, 2),
    'Then walk me through the dossier in plain language: what the number is made of, what feeds it, and anything I should double-check.',
  ];
  return lines.join('\n');
}
