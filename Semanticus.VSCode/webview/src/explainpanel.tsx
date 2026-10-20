// Explain This Number (feature #2) — the slide-over panel behind "Explain this number" on any value cell
// in DAX Lab (matrix / table / result grid). It calls the engine's explain_value once and renders the
// deterministic dossier: the value re-derived in the cell's exact filter context, the why-is-this-blank
// checklist, the top contributors (which the ENGINE refuses to show when the parts don't sum), what feeds
// the number, where the data comes from, and a row-security heads-up. The panel never narrates beyond the
// engine's deterministic sentences; "Give to your assistant" puts a ready prompt on the Plan (Copy beside it) so the
// user's own assistant does the interpreting (golden rule: the engine runs no inference).
// Copy rules (Kane's UX bar): plain language, jargon always glossed, no em-dashes, never "Claude".

import { useEffect, useRef, useState } from 'react';
import { rpc } from './bridge';
import { explainKey, explainOnce, splitOptions } from './explainguard';
import { useSessionIdentity } from './connection';
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
interface Contributors { dimension?: string; additive: boolean; rows: ContributorRow[]; truncated: boolean; note?: string; spread?: ContributorRow[]; alternatives?: string[] }
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
/** A field the engine names as a DAX reference ('Customer'[City]) in the person's words: the column, City. */
/** An engine note with its DAX references said as the column ("split by 'Customer'[City]" is "split by City"). */
export const plainNote = (note: string | null | undefined): string | undefined =>
  note ? note.replace(/(?:'[^']+'|[A-Za-z0-9_]+)\[([^\]]+)\]/g, '$1') : undefined;
export const plainField = (field: string): string => /\[([^\]]+)\]\s*$/.exec(field)?.[1] ?? field;
/** A column in words, with its table when the name alone is ambiguous: 'Customer'[City] is "Customer City" beside 'Store'[City], "City" alone. */
export const columnLabel = (ref: string, among: readonly string[] = [], always = false): string => {
  const name = plainField(ref);
  const table = /^'?((?:[^'\[]|'')+?)'?\s*\[/.exec(ref)?.[1]?.replace(/''/g, "'");
  if (!table) return name;
  const same = among.filter((o) => plainField(o) === name).length;
  return always || same > 1 ? `${table} ${name}` : name;
};
export const selectionWords = (entries: readonly { field: string; value: string }[]): string => entries.map((e) => `${plainField(e.field)} = ${plainValue(e.value)}`).join('; ');

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

/** The engine's note on the split, with its column said as "Customer City" (the table says which City). */
const splitNote = (c: Contributors): string | undefined => plainNote(c.note && c.dimension ? c.note.split(c.dimension).join(columnLabel(c.dimension, [], true)) : c.note);

/** Where a table's data comes from, in words. The engine writes "Import (M) · Source name · the query text": the text goes behind its own disclosure. */
function sourceWords(source: string): { words: string; code: string } {
  const [head = '', ...rest] = source.split(' · ');
  const isCode = (x: string) => /[=()]/.test(x) || x.length > 40;
  const from = rest.length > 0 && !isCode(rest[0]) ? rest[0] : '';
  const code = (from ? rest.slice(1) : rest).join(' · ');
  const kind = /^Import/i.test(head) ? 'Loaded from its source' : /^DirectQuery/i.test(head) ? 'Read live from its source' : /^DirectLake/i.test(head) ? 'Read live from the lake' : /^Calculated/i.test(head) ? 'Worked out in the model' : head.replace(/\s*\(.*?\)/g, '').trim() || 'Source not stated';
  return { words: from ? `${kind}, ${from}` : kind, code };
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
    <Section title={`How it differs by ${c.dimension ? columnLabel(c.dimension, [], true) : dimensionName(c.dimension)}`} always>
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
          {splitNote(d.contributors) && <div className="text-(length:--sem-text-label) mt-2" style={{ color: 'var(--sem-muted)' }}>{splitNote(d.contributors)}</div>}
        </Section>
      )}
      {d.contributors && !d.contributors.additive && <Spread c={d.contributors} format={fmt} />}
      {d.contributors && !d.contributors.additive && splitNote(d.contributors) && (
        <div className="text-(length:--sem-text-label) leading-5" style={{ color: 'var(--sem-muted)' }}>{splitNote(d.contributors)}</div>
      )}
    </div>
  );
}

export function ExplainPanel({ payload, onClose, inline = false }: { payload: ExplainPayload; onClose?: () => void; inline?: boolean }) {
  const [d, setD] = useState<ExplainDossier | null>(null);
  const [err, setErr] = useState<string | null>(null);
  // The column the person chose to split by (null: the engine picks), and the columns offered, kept from the engine's own pick while they switch.
  // The choice belongs to the number it was made on: a new number is never first asked with the old choice.
  const [choice, setChoice] = useState<{ cell: string; by: string | null }>({ cell: '', by: null });
  const [opts, setOpts] = useState<string[]>([]);

  // Keyed by what is asked, never by the payload object: a parent that builds a new, equal payload on each render must not ask again.
  const model = useSessionIdentity();
  const cellKey = explainKey(payload, model);
  const splitBy = choice.cell === cellKey ? choice.by : null;
  const setSplitBy = (by: string | null) => setChoice({ cell: cellKey, by });
  const key = explainKey({ ...payload, splitBy }, model);
  const payloadRef = useRef(payload);
  payloadRef.current = payload;
  // Another number is asked again from the engine's own pick.
  useEffect(() => { setOpts([]); }, [cellKey]);
  useEffect(() => {
    let alive = true;
    const p = payloadRef.current;
    setD(null); setErr(null);
    explainOnce(key, () => rpc<ExplainDossier>('explainValue', p.measureName,
      { groupBy: p.groupBy, filters: p.filters, extraPredicates: p.extraPredicates }, true, splitBy, 5))
      .then((r) => { if (alive) { setD(r); setOpts((kept) => splitOptions(kept, r.contributors)); } })
      .catch((e) => { if (alive) setErr(String((e as Error).message ?? e)); });
    return () => { alive = false; };
  }, [key]);

  const fmt = d?.format ?? payload.format;
  const prompt = buildAssistantPrompt(payload, d);
  const failed = err || (d && d.status !== 'ok');

  return (
    <div data-explain-panel data-testid="explain-panel" className={inline ? 'flex flex-col min-h-0' : 'fixed inset-y-0 right-0 flex flex-col'} style={inline ? undefined : {
      width: 400, zIndex: 70, background: 'var(--sem-surface)', borderLeft: '1px solid var(--sem-border)',
      boxShadow: '-18px 0 44px -24px rgba(0,0,0,.7)',
    }}>
      {/* header: inside the Query view the row above already names the number and its selection, and the first line says its value */}
      {!inline && <div className="flex items-center gap-2 px-3 py-2 shrink-0" style={{ borderBottom: '1px solid var(--sem-border)' }}>
        <div className="min-w-0">
          <div className="text-(length:--sem-text-label)" style={{ color: 'var(--sem-muted)' }}>Explain this number</div>
          <div className="text-(length:--sem-text-body) font-semibold truncate" style={{ color: 'var(--sem-fg)' }}>{payload.measureName}</div>
        </div>
        <div className="ml-auto text-(length:--sem-text-page) font-bold tnum shrink-0">
          {d?.valueEvaluated ? (d.isBlank ? '(blank)' : fmtNum(d.value, fmt)) : fmtNum(payload.value)}
        </div>
        {onClose && <button onClick={onClose} title="Close" className="shrink-0 grid place-items-center rounded"
          style={{ width: 'var(--sem-control-h-sm)', height: 'var(--sem-control-h-sm)', background: 'none', border: 'none', color: 'var(--sem-muted)', cursor: 'pointer' }}>✕</button>}
      </div>}

      <div className={(inline ? 'px-0 py-2' : 'flex-1 min-h-0 overflow-auto px-3 py-2') + ' flex flex-col gap-3'}>
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

            {/* your selection (the Query view shows it on the line above) */}
            {!inline && <Section title="Your selection" always>
              {payload.entries.length === 0
                ? <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>No filters. This is the number for everything.</div>
                : (
                  <div className="flex flex-col gap-1">
                    {payload.entries.map((e, i) => (
                      <div key={i} className="flex items-baseline gap-2 text-xs">
                        <span style={{ color: 'var(--sem-kind-column)', fontFamily: 'var(--mono, monospace)', fontSize: 'var(--sem-text-label)' }}>{plainField(e.field)}</span>
                        <span style={{ color: 'var(--sem-muted)' }}>=</span>
                        <span className="font-semibold" style={{ color: 'var(--sem-fg)' }}>{plainValue(e.value)}</span>
                        <span className="ml-auto text-(length:--sem-text-label) px-2 rounded" style={{ color: 'var(--sem-muted)', background: 'var(--sem-surface-2)' }}>
                          {e.src === 'filter' ? 'filter' : e.src === 'rows' ? 'row' : 'column'}
                        </span>
                      </div>
                    ))}
                  </div>
                )}
            </Section>}

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
                {opts.length > 1 && <SplitBy options={opts} chosen={d.contributors.dimension ?? splitBy} onPick={setSplitBy} />}
                {d.contributors.additive && d.contributors.rows.length > 0 ? (
                  <>
                    <ContributorBars rows={d.contributors.rows} format={fmt} />
                    {splitNote(d.contributors) && <div className="text-(length:--sem-text-label) mt-2" style={{ color: 'var(--sem-muted)' }}>{splitNote(d.contributors)}</div>}
                  </>
                ) : (
                  <>
                    <Spread c={d.contributors} format={fmt} />
                    <div className="text-xs leading-5" style={{ color: 'var(--sem-muted)' }}>{splitNote(d.contributors) ?? 'No breakdown is available here.'}</div>
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
                  {d.lineage.map((l, i) => {
                    const src = l.source ? sourceWords(l.source) : null;
                    return (
                      <div key={i} className="text-(length:--sem-text-label) leading-5" data-testid="explain-lineage-row">
                        <span style={{ color: 'var(--sem-fg)' }}>{(l.table ? l.table + ' · ' : '') + l.column.slice(l.column.lastIndexOf('/') + 1)}</span>
                        {src && <span style={{ color: 'var(--sem-muted)' }}>{' · '}{src.words}</span>}
                        {src?.code && (
                          <details className="ex-code">
                            <summary>Show the query text</summary>
                            <pre>{src.code}</pre>
                          </details>
                        )}
                      </div>
                    );
                  })}
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
                  <pre className="text-(length:--sem-text-label) leading-5 px-2 py-1 rounded mt-1 whitespace-pre-wrap [overflow-wrap:anywhere]"
                    style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', fontFamily: 'var(--mono, monospace)' }}>{d.evidence.query}</pre>
                </details>
              )}
            </Section>
          </>
        )}
      </div>

      {/* footer: hand the dossier to the user's assistant — the UI never narrates */}
      <div className={inline ? 'py-2 shrink-0 flex items-center gap-2' : 'px-3 py-2 shrink-0 flex items-center gap-2'} style={inline ? undefined : { borderTop: '1px solid var(--sem-border)' }}>
        <GiveToAssistant size="md" primary sendKey={`explain|${payload.measureName}|${JSON.stringify(payload.entries)}|${payload.extraPredicates.join(';')}`}
          build={() => explainTask(payload.measureName, d?.valueEvaluated ? (d.isBlank ? 'a blank' : fmtNum(d.value, fmt)) : fmtNum(payload.value), prompt)} />
      </div>
    </div>
  );
}

/** One press changes the split: the columns that also explain most of the number, the one shown on. */
function SplitBy({ options, chosen, onPick }: { options: string[]; chosen?: string | null; onPick: (column: string) => void }) {
  return (
    <div className="ex-by" role="group" aria-label="Split by" data-testid="explain-split-by">
      <span className="ex-by-label">Split by</span>
      {options.map((o) => (
        <button key={o} type="button" className="ex-by-opt" aria-pressed={o === chosen} data-column={o} onClick={() => onPick(o)}>{columnLabel(o, options)}</button>
      ))}
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
  // One grid for every row, so the bars start and end on the same lines and the amount and the share each keep their own column ("$4,901,153.49" and
  // "12.5%" cannot run together, hero 25 H8). The bars share one scale; "everything else" is drawn quieter. A member name wraps rather than being cut off.
  return (
    <div className="ex-parts" data-testid="explain-contributors">
      {rows.map((r, i) => {
        const rest = r.member.startsWith('(everything else');
        return (
          <div key={i} className={rest ? 'ex-part ex-part-rest' : 'ex-part'}>
            <span className="ex-part-name" title={r.member}>{r.member}</span>
            <div className="ex-part-track"><div className="ex-part-fill" style={{ width: `${Math.max(2, (Math.abs(Number(r.value) || 0) / max) * 100)}%` }} /></div>
            <span className="ex-part-amt tnum">{fmtNum(r.value, format)}</span>
            <span className="ex-part-pct tnum">{r.pct != null ? r.pct.toFixed(1) + '%' : ''}</span>
          </div>
        );
      })}
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
