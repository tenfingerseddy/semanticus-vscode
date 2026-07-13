// Explain This Number (feature #2) — the slide-over panel behind "Explain this number" on any value cell
// in DAX Lab (matrix / table / result grid). It calls the engine's explain_value once and renders the
// deterministic dossier: the value re-derived in the cell's exact filter context, the why-is-this-blank
// checklist, the top contributors (which the ENGINE refuses to show when the parts don't sum), what feeds
// the number, where the data comes from, and a row-security heads-up. The panel never narrates beyond the
// engine's deterministic sentences; the "Ask the AI Assistant" button copies a ready-to-paste prompt so the
// user's own assistant does the interpreting (golden rule: the engine runs no inference).
// Copy rules (Kane's UX bar): plain language, jargon always glossed, no em-dashes, never "Claude".

import { useEffect, useState } from 'react';
import { rpc, copyText } from './bridge';
import type { CtxEntry } from './daxvisual';
import { uiLabel } from './copy';

// ---- wire shapes (camelCase of Semanticus.Engine Explain DTOs) ----------------------------------
export interface ExplainWireFilter { column: string; members: string[]; empty?: boolean }
interface ChainNode { ref: string; name: string; kind: string; depth: number }
interface LineageEntry { column: string; table?: string; source?: string }
interface ContributorRow { member: string; value: string; pct?: number | null }
interface Contributors { dimension?: string; additive: boolean; rows: ContributorRow[]; truncated: boolean; note?: string }
interface BlankCheck { id: string; question: string; result: 'ok' | 'cause' | 'maybe' | 'skipped'; finding: string; detail?: string }
interface Blank { checks: BlankCheck[]; likelyCauseId?: string; summary?: string }
interface RlsRole { role: string; filters: { table: string; filterExpression?: string }[] }
interface Evidence { available: boolean; query?: string; totalMs?: number; feMs?: number; seMs?: number; seQueries?: number; note?: string }
export interface ExplainDossier {
  status: string; measure: string; name?: string; expression?: string;
  valueEvaluated: boolean; value?: string; isBlank?: boolean | null; summary?: string;
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
}

const fmtNum = (v: string | undefined) => {
  if (v == null) return '';
  const n = Number(v);
  return Number.isFinite(n) ? n.toLocaleString(undefined, { maximumFractionDigits: 2 }) : v;
};

export function ExplainPanel({ payload, onClose }: { payload: ExplainPayload; onClose: () => void }) {
  const [d, setD] = useState<ExplainDossier | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    let alive = true;
    setD(null); setErr(null);
    rpc<ExplainDossier>('explainValue', payload.measureName,
      { groupBy: payload.groupBy, filters: payload.filters, extraPredicates: payload.extraPredicates }, true, null, 5)
      .then((r) => { if (alive) setD(r); })
      .catch((e) => { if (alive) setErr(String((e as Error).message ?? e)); });
    return () => { alive = false; };
  }, [payload]);

  const prompt = buildAssistantPrompt(payload, d);
  const failed = err || (d && d.status !== 'ok');

  return (
    <div data-explain-panel className="fixed inset-y-0 right-0 flex flex-col" style={{
      width: 400, zIndex: 70, background: 'var(--sem-surface)', borderLeft: '1px solid var(--sem-border)',
      boxShadow: '-18px 0 44px -24px rgba(0,0,0,.7)',
    }}>
      {/* header */}
      <div className="flex items-center gap-2 px-3 py-2 shrink-0" style={{ borderBottom: '1px solid var(--sem-border)' }}>
        <div className="min-w-0">
          <div className="text-[10px] uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>Explain this number</div>
          <div className="text-[13px] font-semibold truncate" style={{ color: 'var(--sem-fg)' }}>{payload.measureName}</div>
        </div>
        <div className="ml-auto text-[18px] font-bold tnum shrink-0" style={{ color: 'var(--sem-accent)' }}>
          {d?.valueEvaluated ? (d.isBlank ? '(blank)' : fmtNum(d.value)) : fmtNum(payload.value)}
        </div>
        <button onClick={onClose} title="Close" className="shrink-0 grid place-items-center rounded"
          style={{ width: 22, height: 22, background: 'none', border: 'none', color: 'var(--sem-muted)', cursor: 'pointer' }}>✕</button>
      </div>

      <div className="flex-1 min-h-0 overflow-auto px-3 py-2.5 flex flex-col gap-3">
        {/* loading / error */}
        {!d && !err && <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>Working it out…</div>}
        {failed && (
          <div className="rounded-lg px-3 py-2 text-[12px]" style={{ background: 'color-mix(in srgb, var(--sem-bad) 12%, transparent)', color: 'var(--sem-bad)', border: '1px solid color-mix(in srgb, var(--sem-bad) 35%, transparent)' }}>
            {err ?? d?.error ?? 'Something went wrong.'}
          </div>
        )}

        {d && d.status === 'ok' && (
          <>
            {/* the plain-language lead */}
            {d.summary && <div className="text-[12.5px] leading-snug" style={{ color: 'var(--sem-fg)' }}>{d.summary}</div>}

            {/* your selection */}
            <Section title="Your selection" always>
              {payload.entries.length === 0
                ? <div className="text-[11.5px]" style={{ color: 'var(--sem-muted)' }}>No filters. This is the number for everything.</div>
                : (
                  <div className="flex flex-col gap-1">
                    {payload.entries.map((e, i) => (
                      <div key={i} className="flex items-baseline gap-2 text-[11.5px]">
                        <span style={{ color: '#9cdcfe', fontFamily: 'var(--mono, monospace)', fontSize: 11 }}>{e.field}</span>
                        <span style={{ color: 'var(--sem-muted)' }}>=</span>
                        <span className="font-semibold" style={{ color: 'var(--sem-fg)' }}>{e.value}</span>
                        <span className="ml-auto text-[9px] px-1.5 rounded" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>
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
                {d.blank.summary && <div className="text-[12px] mb-1.5 font-medium" style={{ color: 'var(--sem-warn)' }}>{d.blank.summary}</div>}
                <div className="flex flex-col gap-1.5">
                  {d.blank.checks.map((c, i) => <CheckRow key={i} c={c} likely={d.blank!.likelyCauseId === c.id && c.result === 'cause'} />)}
                </div>
              </Section>
            )}

            {/* what makes it up */}
            {d.contributors && (
              <Section title="What makes it up" always>
                {d.contributors.additive && d.contributors.rows.length > 0 ? (
                  <>
                    <ContributorBars rows={d.contributors.rows} />
                    {d.contributors.note && <div className="text-[10.5px] mt-1.5" style={{ color: 'var(--sem-muted)' }}>{d.contributors.note}</div>}
                  </>
                ) : (
                  <div className="text-[11.5px] leading-snug" style={{ color: 'var(--sem-muted)' }}>{d.contributors.note ?? 'No breakdown is available here.'}</div>
                )}
              </Section>
            )}

            {/* what feeds it */}
            {d.chain.length > 0 && (
              <Section title={`What feeds it (${d.chain.length})`}>
                <div className="flex flex-col gap-0.5">
                  {d.chain.map((n, i) => (
                    <div key={i} className="flex items-center gap-1.5 text-[11.5px]" style={{ paddingLeft: (n.depth - 1) * 12 }}>
                      <span className="text-[9px] px-1 rounded shrink-0" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>
                        {n.kind === 'calccolumn' ? 'Calculated column' : uiLabel(n.kind)}
                      </span>
                      <span className="truncate" style={{ color: 'var(--sem-fg)' }}>{n.name}</span>
                    </div>
                  ))}
                  {d.chainTruncated && <div className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>…and more (the walk stops at a sensible depth).</div>}
                </div>
              </Section>
            )}

            {/* where the data comes from */}
            {d.lineage.length > 0 && (
              <Section title="Where the data comes from">
                <div className="flex flex-col gap-1">
                  {d.lineage.map((l, i) => (
                    <div key={i} className="text-[11px] leading-snug">
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
                <div className="text-[11px] mb-1" style={{ color: 'var(--sem-muted)' }}>
                  These roles limit which rows people see (row-level security). This view reads the model without a role, so a secured user's number can differ.
                </div>
                {d.rls.map((r, i) => (
                  <div key={i} className="text-[11px]">
                    <span className="font-semibold" style={{ color: 'var(--sem-fg)' }}>{r.role}</span>
                    <span style={{ color: 'var(--sem-muted)' }}> filters {r.filters.map((f) => f.table).join(', ')}</span>
                  </div>
                ))}
              </Section>
            )}

            {/* evidence */}
            <Section title="How this was checked">
              {d.evidence?.available ? (
                <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>
                  The number was recomputed live with the exact filters of this cell.
                  {d.evidence.totalMs != null && <> Took {d.evidence.totalMs} ms.</>}
                </div>
              ) : (
                <div className="text-[11px]" style={{ color: 'var(--sem-warn)' }}>
                  {d.evidence?.note ?? 'No live connection: the number itself was not recomputed.'}
                </div>
              )}
              {d.evidence?.query && (
                <details className="mt-1">
                  <summary className="text-[10.5px] cursor-pointer select-none" style={{ color: 'var(--sem-muted)' }}>the exact query used</summary>
                  <pre className="text-[10px] leading-snug px-2 py-1.5 rounded overflow-auto max-h-44 mt-1 whitespace-pre-wrap"
                    style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', fontFamily: 'var(--mono, monospace)' }}>{d.evidence.query}</pre>
                </details>
              )}
            </Section>
          </>
        )}
      </div>

      {/* footer: hand the dossier to the user's assistant — the UI never narrates */}
      <div className="px-3 py-2 shrink-0 flex items-center gap-2" style={{ borderTop: '1px solid var(--sem-border)' }}>
        <button onClick={async () => { if (await copyText(prompt)) { setCopied(true); setTimeout(() => setCopied(false), 1600); } }}
          className="text-[12px] px-3 py-1.5 rounded-lg font-medium"
          style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', border: 'none', cursor: 'pointer' }}>
          {copied ? 'Copied' : 'Ask the AI Assistant to explain'}
        </button>
        <span className="text-[10px]" style={{ color: 'var(--sem-muted)' }}>Copies a ready-to-paste prompt.</span>
      </div>
    </div>
  );
}

function Section({ title, always, children }: { title: string; always?: boolean; children: React.ReactNode }) {
  if (always) {
    return (
      <div>
        <div className="text-[10px] uppercase tracking-wide font-semibold mb-1" style={{ color: 'var(--sem-muted)' }}>{title}</div>
        {children}
      </div>
    );
  }
  return (
    <details>
      <summary className="text-[10px] uppercase tracking-wide font-semibold cursor-pointer select-none" style={{ color: 'var(--sem-muted)' }}>{title}</summary>
      <div className="mt-1">{children}</div>
    </details>
  );
}

function CheckRow({ c, likely }: { c: BlankCheck; likely: boolean }) {
  const icon = c.result === 'cause' ? '✗' : c.result === 'ok' ? '✓' : c.result === 'maybe' ? '?' : '·';
  const color = c.result === 'cause' ? 'var(--sem-bad)' : c.result === 'ok' ? 'var(--sem-good)' : c.result === 'maybe' ? 'var(--sem-warn)' : 'var(--sem-muted)';
  return (
    <div className="rounded-lg px-2 py-1.5" style={{ border: likely ? '1px solid color-mix(in srgb, var(--sem-bad) 45%, transparent)' : '1px solid var(--sem-border)' }}>
      <div className="flex items-start gap-2">
        <span className="shrink-0 font-bold text-[12px]" style={{ color }}>{icon}</span>
        <div className="min-w-0">
          <div className="text-[11px] font-medium" style={{ color: 'var(--sem-fg)' }}>{c.question}{likely && <span className="ml-1.5 text-[9px] uppercase tracking-wide font-semibold" style={{ color: 'var(--sem-bad)' }}>likely cause</span>}</div>
          <div className="text-[11px] leading-snug" style={{ color: 'var(--sem-muted)' }}>{c.finding}</div>
          {c.detail && <div className="text-[10px] mt-0.5 font-mono truncate" title={c.detail} style={{ color: 'var(--sem-muted)', opacity: 0.8 }}>{c.detail}</div>}
        </div>
      </div>
    </div>
  );
}

function ContributorBars({ rows }: { rows: ContributorRow[] }) {
  const vals = rows.map((r) => Math.abs(Number(r.value) || 0));
  const max = Math.max(...vals, 1e-9);
  return (
    <div className="flex flex-col gap-1">
      {rows.map((r, i) => (
        <div key={i} className="flex items-center gap-2">
          <span className="text-[11px] w-32 shrink-0 truncate" title={r.member} style={{ color: 'var(--sem-fg)' }}>{r.member}</span>
          <div className="flex-1 h-2 rounded" style={{ background: 'var(--sem-surface-2)' }}>
            <div className="h-2 rounded" style={{ width: `${Math.max(2, (Math.abs(Number(r.value) || 0) / max) * 100)}%`, background: 'var(--sem-accent)', opacity: r.member.startsWith('(everything else') ? 0.4 : 0.9 }} />
          </div>
          <span className="text-[11px] tnum w-16 text-right shrink-0" style={{ color: 'var(--sem-fg)' }}>{fmtNum(r.value)}</span>
          <span className="text-[10px] tnum w-10 text-right shrink-0" style={{ color: 'var(--sem-muted)' }}>{r.pct != null ? r.pct + '%' : ''}</span>
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
    p.entries.length ? 'Selection: ' + p.entries.map((e) => `${e.field} = ${e.value}`).join('; ') + '.' : 'Selection: none (grand total).',
    'Use the Semanticus MCP tool explain_value with these arguments:',
    JSON.stringify(args, null, 2),
    'Then walk me through the dossier in plain language: what the number is made of, what feeds it, and anything I should double-check.',
  ];
  return lines.join('\n');
}
