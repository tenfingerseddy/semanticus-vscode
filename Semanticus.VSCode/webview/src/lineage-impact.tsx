import { useEffect, useMemo, useRef, useState } from 'react';
import { rpc, revealInTree, pickReportPaths } from './bridge';
import { KIND_GLYPH, KIND_COLOR, KIND_LABEL, useFillHeight, type LineageResult } from './lineagetypes';
import { AccountPicker } from './accountpicker';
import * as M from './lineage-model';

// ===================================================================================================
// Model > Lineage > Impact. One page, one card, one report selection (Kane's option A, 15 September).
//
// What this replaced, and why, from Astra's UAT of the old tab:
//   - four buttons that sent the SAME request and drew the SAME card, one of which ("Create probes")
//     created nothing. They are gone. Picking a thing IS the question; the actions are Rename,
//     Propose removal and Add a check, and each one does what its verb says.
//   - a verdict chip that said "Broken" about a model nobody had touched yet. The card leads with the
//     answer instead: "Total Sales is used by 3 measures."
//   - an Interview tile for a feature that left the app. Gone from the engine result and from here.
//   - "Safe to remove" and "Published reports" as separate tabs drawing the same list twice. Cleanup is
//     a tick box on this page; the reports are chosen in one drawer the whole page shares.
//   - a yellow model-only banner that stayed up after you had analysed a report, because the page kept
//     that analysis to itself. The strip now says what WAS checked, and the engine owns the reading.
//
// Every count and sentence on screen comes from ./lineage-model, which is pure and tested. Nothing in
// this file invents a number.
// ===================================================================================================

// ---- wire shapes ---------------------------------------------------------------------------------
export interface ImpactNode { ref: string; name: string; kind: string; table?: string; depth: number; via: string; viaName?: string }
export interface ImpactResult {
  root: string; rootName: string; rootKind: string; impacted: ImpactNode[];
  measures: number; columns: number; tables: number; relationships: number; other: number; caveat?: string;
}
export interface UnusedItem { ref: string; name: string; kind: string; table?: string; isHidden?: boolean; verdict: string; refCount: number; blockedBy: string[]; reason: string }
export interface UnusedResult { items: UnusedItem[]; safeCount: number; usedByUnusedOnlyCount: number; cautionCount: number; caveat?: string }
/** Where in one visual the field shows up. Restored after the first build dropped every detail, which hid the
 * report's raw visual token by hiding the answer with it (Astra). */
export interface ImpactVisualHit {
  page: string; visual?: string | null; visualKind?: string; visualKindId?: string;
  usedRefs: string[]; viaDependent?: boolean;
}
export interface ImpactReportHit { path: string; name: string; visuals: number; usedRefs: string[]; details?: ImpactVisualHit[] }
export interface ImpactReplayCheck {
  id: string; kind: string; title: string; targetRef?: string; reason: string;
  /** When the engine has actually measured a last run, in words. Absent means nobody measured, and the row
   * says nothing rather than inventing "not run since the last change". */
  lastRunLabel?: string;
}
export interface ImpactAssessmentResult {
  objectRef: string; objectName: string; objectKind: string; modelName: string; intent: string; scope: string;
  verdict: string; modelImpact: ImpactResult; reportImpact: ImpactReportHit[]; reportsImpacted: number;
  visualsImpacted: number; replayChecks: ImpactReplayCheck[]; replayChecksOmitted: number;
  unknowns: string[]; summary: string; reportSummary?: string; checked?: { model: string; reports: M.ScopeReport[]; reportsChecked: number; reportsListed: number; checkedWhenUtc?: string; gaps: string[] };
}
export interface CloudReport { id: string; name: string; datasetId?: string; reportType?: string; webUrl?: string }
export interface FabricWorkspace { id: string; displayName: string }

// ---- shared bits ---------------------------------------------------------------------------------
/** One local clock for the whole page, so two rows can never disagree about when a report was read. */
export const clockTime = (iso: string): string => {
  try { return new Date(iso).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' }); }
  catch { return iso; }
};

const ctl = {
  boxSizing: 'border-box' as const, height: 'var(--sem-control-h-sm)', padding: 'var(--sem-control-pad-sm)',
  borderRadius: 'var(--sem-control-radius)', fontSize: 'var(--sem-control-font)',
  background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)', outline: 'none',
};

function Glyph({ kind }: { kind: string }) {
  return <span className="shrink-0 text-[12px] w-4 text-center" style={{ color: KIND_COLOR[kind] ?? 'var(--sem-muted)' }}
    aria-label={KIND_LABEL[kind] ?? kind}>{KIND_GLYPH[kind] ?? '•'}</span>;
}
function Panel({ children, className = '', testId }: { children: React.ReactNode; className?: string; testId?: string }) {
  return <div data-testid={testId} className={`rounded-xl border p-4 ${className}`}
    style={{ background: 'var(--sem-surface)', borderColor: 'var(--sem-border)' }}>{children}</div>;
}
function RowLabel({ children }: { children: React.ReactNode }) {
  return <div className="text-[11px] uppercase tracking-wide font-semibold shrink-0" style={{ color: 'var(--sem-muted)', width: 96 }}>{children}</div>;
}
function Muted({ children, className = '', title }: { children: React.ReactNode; className?: string; title?: string }) {
  return <span className={`text-[11px] ${className}`} title={title} style={{ color: 'var(--sem-muted)' }}>{children}</span>;
}

// ===================================================================================================
// The checked strip. It sits in the tool row, beside the Graph / Tree / Impact switch, and says what
// this page's answers were checked against. It replaces the yellow "model only" banner, which said what
// the answer did NOT cover and stayed up even after a report had been read.
// ===================================================================================================
export function CheckedStrip({ scope, onChoose, timeFormat = clockTime }: {
  scope: M.ReportScope; onChoose: () => void; timeFormat?: (iso: string) => string;
}) {
  const strip = M.checkedStrip(scope, { time: timeFormat });
  return (
    <span className="text-[11px] flex items-center gap-1.5 flex-wrap" data-testid="impact-checked-strip" style={{ color: 'var(--sem-muted)' }}>
      <span>{strip.model}</span>
      <span aria-hidden>·</span>
      <span data-testid="impact-checked-reports">{strip.reports}</span>
      {strip.when && <span className="tnum">{strip.when}</span>}
      <span aria-hidden>·</span>
      <button onClick={onChoose} data-testid="impact-choose-reports" className="underline" style={{ color: 'var(--sem-accent)' }}>{strip.action}</button>
    </span>
  );
}

// ===================================================================================================
// The card: one verdict sentence, then three plain rows, then the actions.
// ===================================================================================================
export function ImpactCard({
  impact, scope, reportHits, savedChecks, summary, reportSummary, timeFormat = clockTime,
  onPick, onChooseReports, onRename, onProposeRemoval, onAddCheck, onOpenCheck, onOpenPlan,
  busy, proposed, error, addCheckOpen, onCloseAddCheck, expandedRow, onExpandRow,
}: {
  impact: ImpactResult; scope: M.ReportScope; reportHits: ImpactReportHit[]; savedChecks: ImpactReplayCheck[];
  summary: string; reportSummary?: string; timeFormat?: (iso: string) => string;
  onPick?: (ref: string) => void; onChooseReports?: () => void; onRename?: () => void;
  onProposeRemoval?: () => void; onAddCheck?: (ref: string) => void; onOpenCheck?: (checkIds: string[]) => void; onOpenPlan?: () => void;
  busy?: string | null; proposed?: boolean; error?: string | null;
  addCheckOpen?: boolean; onCloseAddCheck?: () => void;
  expandedRow?: string | null; onExpandRow?: (row: string | null) => void;
}) {
  const [localExpanded, setLocalExpanded] = useState<string | null>(null);
  const expanded = expandedRow !== undefined ? expandedRow : localExpanded;
  const setExpanded = onExpandRow ?? setLocalExpanded;
  const [filter, setFilter] = useState('');
  const [pages, setPages] = useState<Record<string, number>>({});
  const block = M.removalBlock(impact);
  const reportsLine = M.reportsRowLine(scope, { time: timeFormat });
  const plan = M.addCheckPlan(impact);
  const relevant = savedChecks.filter((c) => c.kind !== 'ambient-suite');

  return (
    <Panel className="min-w-0 flex flex-col overflow-hidden lg:h-full" testId="impact-card">
      <div className="flex items-baseline gap-2 flex-wrap shrink-0">
        <Glyph kind={impact.rootKind} />
        <span className="text-[15px] font-semibold" data-testid="impact-verdict">{summary}</span>
        <Muted className="ml-auto">{KIND_LABEL[impact.rootKind] ?? impact.rootKind}{impact.impacted[0]?.table ? '' : ''}</Muted>
      </div>
      {reportSummary && <div className="text-[12px] mt-1 shrink-0" data-testid="impact-report-summary" style={{ color: 'var(--sem-muted)' }}>{reportSummary}</div>}
      {error && <div className="text-[11px] mt-2 rounded-md px-2.5 py-2 shrink-0" data-testid="impact-error" style={{ color: 'var(--sem-bad)', border: '1px solid var(--sem-bad)' }}>{error}</div>}

      <div className="mt-3 flex flex-col gap-2 flex-1 min-h-0 overflow-auto">
        {/* ---- In the model ---------------------------------------------------------------- */}
        <div className="flex items-start gap-2" data-testid="impact-row-model">
          <RowLabel>In the model</RowLabel>
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2 flex-wrap">
              <span className="text-[12px] font-medium tnum">{M.modelRowCounts(impact)}</span>
              {M.previewNames(impact, 5).map((n) => (
                <button key={n.ref} onClick={() => onPick?.(n.ref)} data-testid="impact-preview-name"
                  className="inline-flex items-center gap-1 text-[11px] px-1.5 py-0.5 rounded"
                  style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>
                  <Glyph kind={n.kind} />{n.name}
                </button>
              ))}
              {impact.impacted.length > 0 && (
                <button onClick={() => setExpanded(expanded === 'model' ? null : 'model')} data-testid="impact-show-all"
                  aria-expanded={expanded === 'model'} className="text-[11px] underline" style={{ color: 'var(--sem-accent)' }}>
                  {expanded === 'model' ? 'Collapse' : M.showAllLabel(impact)}
                </button>
              )}
            </div>
            {expanded === 'model' && (
              // Bounded on purpose: a hundred and twenty-three dependants must not push "In reports" and
              // "Saved checks" off the card. The full list scrolls inside its own box; the card keeps its shape.
              <div className="mt-2 rounded-lg border p-2 overflow-auto" data-testid="impact-show-all-list"
                // Scales with the window, never a fixed 320px: on a short card that number left the saved-check
                // instruction under it clipped (Astra, screen 04). The rows below always keep their room.
                style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-bg)', maxHeight: 'min(320px, 34vh)' }}>
                <div className="flex items-center gap-2 flex-wrap">
                  <span className="text-[11px] font-semibold">Everything that depends on {impact.rootName}</span>
                  <Muted>Sorted by how directly they use it</Muted>
                  <input value={filter} onChange={(e) => setFilter(e.target.value)} data-testid="impact-show-all-filter"
                    aria-label="Filter this list" placeholder="Filter this list" spellCheck={false}
                    style={{ ...ctl, marginLeft: 'auto', width: 200 }} />
                </div>
                {M.dependantGroups({ ...impact, impacted: impact.impacted.filter((n) => !filter.trim() || (n.name + ' ' + (n.table ?? '')).toLowerCase().includes(filter.trim().toLowerCase())) })
                  .map((g) => {
                  // Kane's amendment: every dependant is reachable. The first build stopped each group at 200 and
                  // printed "and N more", which is a number you cannot open. This pages instead, and the control
                  // says how many pressing it gets you.
                  const page = pages[g.id] ?? 0;
                  const shown = M.pageOf(g.items, page);
                  const left = M.remaining(g.items, page);
                  return (
                  <div key={g.id} className="mt-2">
                    <div className="text-[10px] uppercase tracking-wide font-semibold" data-testid="impact-group-label" style={{ color: 'var(--sem-muted)' }}>{g.label}</div>
                    {shown.map((n) => (
                      <div key={n.ref} className="flex items-center gap-2 px-1 py-0.5 text-[12px]" data-testid="impact-dependant">
                        <Glyph kind={n.kind} />
                        <span className="truncate">{n.name}</span>
                        <Muted>{n.table}</Muted>
                        <Muted className="ml-auto">{M.directness(n)}</Muted>
                        <button onClick={() => onPick?.(n.ref)} data-testid="impact-open-dependant"
                          className="text-[11px] px-1.5 py-0.5 rounded shrink-0"
                          style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>Open</button>
                      </div>
                    ))}
                    {left > 0 && (
                      <button data-testid="impact-show-more" onClick={() => setPages((p) => ({ ...p, [g.id]: page + 1 }))}
                        className="text-[11px] underline mt-1" style={{ color: 'var(--sem-accent)' }}>
                        Show {left} more
                      </button>
                    )}
                  </div>
                ); })}
              </div>
            )}
          </div>
        </div>

        {/* ---- In reports ----------------------------------------------------------------- */}
        <div className="flex items-start gap-2" data-testid="impact-row-reports">
          <RowLabel>In reports</RowLabel>
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2 flex-wrap">
              {/* The headline already carries the completion fact. Repeating it as a count beside itself said the
                  same thing twice in one row (Astra, ruling 1), on six of the seventeen screens. */}
              <span className="text-[12px] font-medium" data-testid="impact-reports-headline">{reportsLine.headline}</span>
              <button onClick={onChooseReports} data-testid="impact-reports-choose" className="text-[11px] underline" style={{ color: 'var(--sem-accent)' }}>{reportsLine.action}</button>
            </div>
            <div className="text-[11px] mt-0.5" data-testid="impact-reports-detail" style={{ color: 'var(--sem-muted)' }}>{reportsLine.detail}</div>
            {reportHits.length > 0 && (
              <div className="mt-1">
                <button onClick={() => setExpanded(expanded === 'reports' ? null : 'reports')} data-testid="impact-reports-show-all"
                  aria-expanded={expanded === 'reports'} className="text-[11px] underline" style={{ color: 'var(--sem-accent)' }}>
                  {expanded === 'reports' ? 'Collapse' : 'Show pages and visuals'}
                </button>
                {expanded === 'reports' && (
                  <div className="mt-1 rounded-lg border p-2" data-testid="impact-reports-list" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-bg)' }}>
                    {reportHits.map((h) => (
                      <div key={h.path} className="text-[12px] py-0.5" data-testid="impact-report-hit">
                        <div><b>{h.name}</b> <Muted>{h.visuals === 1 ? '1 visual' : `${h.visuals} visuals`}</Muted></div>
                        {/* Report, page, visual, and whether the visual shows the field itself or only something
                            that depends on it. That last distinction is why the detail is worth showing at all. */}
                        {(h.details ?? []).map((d, i) => (
                          <div key={(d.page ?? '') + i} className="flex items-center gap-2 pl-3 py-0.5 text-[11px]" data-testid="impact-report-visual">
                            <Muted>{d.page}</Muted>
                            <span>{d.visual || d.visualKind || 'Visual'}</span>
                            {d.visual && d.visualKind && <Muted>{d.visualKind}</Muted>}
                            {d.viaDependent && <Muted>through a measure that uses it</Muted>}
                            {d.visualKindId && d.visualKindId !== d.visualKind && (
                              <Muted className="ml-auto" title={`This report calls it ${d.visualKindId}.`}>details</Muted>
                            )}
                          </div>
                        ))}
                      </div>
                    ))}
                  </div>
                )}
              </div>
            )}
          </div>
        </div>

        {/* ---- Saved checks ---------------------------------------------------------------- */}
        <div className="flex items-start gap-2" data-testid="impact-row-checks">
          <RowLabel>Saved checks</RowLabel>
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2 flex-wrap">
              <span className="text-[12px] font-medium">{relevant.length === 0 ? 'No saved check is bound to this' : relevant.length === 1 ? '1 relevant saved check' : `${relevant.length} relevant saved checks`}</span>
              {/* "Run it" opened a page and ran nothing, and "not run since the last change" was printed whether
                  or not anything had been measured. Both were claims the code did not keep (Astra). */}
              {relevant.length > 0 && onOpenCheck && (
                <button onClick={() => onOpenCheck(relevant.map((c) => c.id))} data-testid="impact-open-checks"
                  className="text-[11px] underline" style={{ color: 'var(--sem-accent)' }}>Open saved checks</button>
              )}
            </div>
            {/* All of them, not the first two: a check you cannot see is a check you will not run. */}
            {relevant.length > 0 && (
              <div className="flex flex-col gap-0.5 mt-0.5">
                {relevant.map((c) => (
                  <div key={c.id} className="text-[11px] flex items-center gap-2" data-testid="impact-saved-check" style={{ color: 'var(--sem-muted)' }}>
                    <span className="truncate">{c.title}</span>
                    {c.lastRunLabel && <span className="ml-auto shrink-0">{c.lastRunLabel}</span>}
                  </div>
                ))}
              </div>
            )}
            <Muted>Checks to run after changing this field. Nothing here runs on its own.</Muted>
          </div>
        </div>
      </div>

      {/* ---- Actions ---------------------------------------------------------------------- */}
      <div className="flex items-center gap-2 mt-3 flex-wrap shrink-0" data-testid="impact-actions">
        <button onClick={onRename} data-testid="impact-rename" disabled={busy != null}
          title="Opens this field in the model list, with its name ready to change."
          className="text-[12px] px-3 py-1 rounded-md font-medium"
          style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>Rename...</button>
        <button onClick={() => { if (!block) onProposeRemoval?.(); }} data-testid="impact-propose-removal"
          disabled={busy != null || !!block || proposed}
          title={block ?? M.PROPOSE_NOTE}
          className="text-[12px] px-3 py-1 rounded-md font-medium"
          style={{
            background: block ? 'var(--sem-surface-2)' : 'var(--sem-accent)',
            color: block ? 'var(--sem-muted)' : 'var(--sem-on-accent)', opacity: proposed ? 0.6 : 1,
          }}>{proposed ? 'Proposed' : 'Propose removal'}</button>
        <button onClick={() => (plan.kind === 'measure' ? onAddCheck?.(plan.ref) : setExpanded('addCheck'))}
          data-testid="impact-add-check" className="text-[12px] px-3 py-1 rounded-md font-medium"
          style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>+ Add a check</button>
        {block && <span className="text-[11px]" data-testid="impact-removal-blocked" style={{ color: 'var(--sem-warn)' }}>{block}</span>}
        {!block && <Muted data-testid="impact-propose-note">{M.PROPOSE_NOTE}</Muted>}
        {proposed && onOpenPlan && (
          <button onClick={onOpenPlan} data-testid="impact-open-plan" className="text-[11px] underline" style={{ color: 'var(--sem-accent)' }}>Open Changes</button>
        )}
      </div>

      {/* A column has no measure of its own, so it asks instead of being pushed into a measure-only form. */}
      {(expanded === 'addCheck' || addCheckOpen) && plan.kind === 'choose' && (
        <div className="mt-2 rounded-lg border p-2 shrink-0" data-testid="impact-add-check-choose" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-bg)' }}>
          <div className="text-[12px] font-medium">{plan.prompt}</div>
          <div className="flex items-center gap-1.5 mt-1 flex-wrap">
            {plan.options.slice(0, 12).map((o) => (
              <button key={o.ref} onClick={() => { onAddCheck?.(o.ref); onCloseAddCheck?.(); setExpanded(null); }}
                data-testid="impact-add-check-option" className="text-[11px] px-2 py-0.5 rounded"
                style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>{o.name}</button>
            ))}
            {plan.options.length === 0 && <Muted>No measure uses this field yet.</Muted>}
            {plan.tableRowCount && (
              <button onClick={() => { onAddCheck?.(`table:${plan.tableRowCount}`); onCloseAddCheck?.(); setExpanded(null); }}
                data-testid="impact-add-check-table" className="text-[11px] px-2 py-0.5 rounded"
                style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>
                Count the rows in {plan.tableRowCount} instead
              </button>
            )}
          </div>
        </div>
      )}
    </Panel>
  );
}

// ===================================================================================================
// The cleanup view. Same page, picker swapped for the candidate list: reasons, three groups, tick
// boxes and one Propose button. Nothing here is called safe.
// ===================================================================================================
export function CleanupList({
  items, scope, selected, timeFormat = clockTime, onToggle, onOpen, onPropose, busy, filter, onFilter, active, updating, caveat, onCleanupOff,
}: {
  items: UnusedItem[]; scope: M.ReportScope; selected: Set<string>; timeFormat?: (iso: string) => string;
  onToggle?: (ref: string) => void; onOpen?: (ref: string) => void; onPropose?: () => void;
  busy?: boolean; filter?: string; onFilter?: (v: string) => void; active?: string | null;
  /** The ENGINE's own caveat for this list, when it has one. A standing sentence printed over it is the same
   * fault as the polished fixture reasons: the page sounding steadier than the engine did (Astra, P5). */
  caveat?: string | null;
  /** P1: true while these candidates are being recomputed against a scope that has just moved. A list measured
   * against a reading that no longer exists must say so rather than wearing the new reading's label. */
  updating?: boolean;
  /** The same tick that opened this view, so there is always a way back to the picker. */
  onCleanupOff?: () => void;
}) {
  const needle = (filter ?? '').trim().toLowerCase();
  const shown = needle ? items.filter((i) => (i.table + ' ' + i.name).toLowerCase().includes(needle)) : items;
  const groups = M.cleanupGroups(shown);
  return (
    <Panel className="min-w-0 flex flex-col overflow-hidden lg:h-full" testId="cleanup-list">
      <div className="flex items-center gap-2 flex-wrap shrink-0">
        <span className="text-[13px] font-semibold">Cleanup candidates</span>
        {onCleanupOff && (
          <label className="flex items-center gap-2 text-[12px]" data-testid="cleanup-toggle-row">
            <input type="checkbox" checked data-testid="impact-cleanup-toggle" onChange={onCleanupOff} />
            <span>Showing cleanup candidates</span>
          </label>
        )}
        <button onClick={onPropose} disabled={busy || selected.size === 0} data-testid="cleanup-propose"
          className="text-[12px] px-3 py-1 rounded-md font-medium ml-auto"
          style={{ background: selected.size === 0 ? 'var(--sem-surface-2)' : 'var(--sem-accent)', color: selected.size === 0 ? 'var(--sem-muted)' : 'var(--sem-on-accent)' }}>
          {M.proposeSelectedLabel(selected.size)}
        </button>
      </div>
      <div className="text-[12px] mt-1 shrink-0" data-testid="cleanup-headline" style={{ color: 'var(--sem-muted)' }}>
        {updating ? 'Updating cleanup results...' : M.cleanupHeadline(items, scope)}
      </div>
      <div className="text-[11px] mt-1 shrink-0" data-testid="cleanup-caveat" style={{ color: 'var(--sem-warn)' }}>{caveat || M.CLEANUP_CAVEAT}</div>
      {onFilter && (
        <input value={filter ?? ''} onChange={(e) => onFilter(e.target.value)} data-testid="cleanup-filter"
          aria-label="Filter the candidates" placeholder="Filter the candidates" spellCheck={false}
          style={{ ...ctl, marginTop: 8, width: '100%' }} />
      )}
      <div className="mt-2 overflow-auto flex-1 min-h-0">
        {groups.length === 0 && <div className="text-[12px] py-6 text-center" style={{ color: 'var(--sem-muted)' }}>Nothing matches.</div>}
        {groups.map((g) => (
          <div key={g.id} className="mt-2">
            <div className="text-[11px] font-semibold mb-1" data-testid="cleanup-group" style={{ color: 'var(--sem-fg)' }}>{g.label} · {g.items.length}</div>
            {g.items.map((i) => (
              <div key={i.ref} data-testid="cleanup-row" onClick={() => onOpen?.(i.ref)}
                className="flex items-start gap-2 px-2 py-1 rounded-md cursor-pointer hover:bg-[var(--sem-surface-2)]"
                style={i.ref === active ? { background: 'var(--sem-accent-soft)' } : undefined}>
                <input type="checkbox" data-testid="cleanup-tick" checked={selected.has(i.ref)} aria-label={`Select ${i.name}`}
                  onClick={(e) => e.stopPropagation()} onChange={() => onToggle?.(i.ref)} className="mt-1" />
                <Glyph kind={i.kind} />
                <div className="min-w-0">
                  <div className="text-[12px] truncate"><span className="font-medium">{i.name}</span> <Muted>{i.table}</Muted></div>
                  <div className="text-[11px]" data-testid="cleanup-reason" style={{ color: 'var(--sem-muted)' }}>{i.reason}</div>
                </div>
                <button onClick={(e) => { e.stopPropagation(); onOpen?.(i.ref); }} data-testid="cleanup-open"
                  className="text-[11px] px-1.5 py-0.5 rounded ml-auto shrink-0"
                  style={{ background: 'var(--sem-surface-2)', border: '1px solid var(--sem-border)', color: 'var(--sem-fg)' }}>Open</button>
              </div>
            ))}
          </div>
        ))}
      </div>
    </Panel>
  );
}

// ===================================================================================================
// Choose reports. One drawer, shared by the card and the cleanup view.
//
// The old page put the raw sign-in mode ("azcli") in a dropdown and the permission fact in a checkbox
// under the report list, after the choice was already made. Here the shared account picker names the
// account, and the permission sentence stands BEFORE the sign-in, because the person needs it before
// they agree rather than after.
// ===================================================================================================
export const PERMISSION_SENTENCE = 'Sign-in asks for permission that can also edit reports. Semanticus uses it only to read.';
export const PERMISSION_DETAIL =
  'Reading a report definition uses the Fabric Get Report Definition call. It needs a permission that can also write '
  + '(Item.ReadWrite.All or Report.ReadWrite.All) and the Contributor role on the workspace. Semanticus only reads the '
  + 'definition and never changes the report.';
export const LOCAL_FOLDER_HINT =
  'A Power BI project (.pbip) or its .Report folder. PBIX files are not supported. Adding a folder chooses it; '
  + 'nothing is read until you check it.';

export function ChooseReportsDrawer({
  scope, selected, onClose, timeFormat = clockTime, returningTo,
  workspaces, reports, workspaceId, signInMode = 'azcli', tenantId = '',
  onWorkspaceId, onSignInMode, onTenantId, onLoadWorkspaces, onLoadReports, onToggle, onCheck,
  folderPath = '', onFolderPath, onBrowse, onAddFolder, onRemove, busy, error, consent, onConsent,
}: {
  scope: M.ReportScope; selected: Set<string>; onClose: () => void; timeFormat?: (iso: string) => string;
  returningTo?: string | null;
  workspaces?: FabricWorkspace[] | null; reports?: CloudReport[] | null; workspaceId?: string;
  signInMode?: string; tenantId?: string;
  onWorkspaceId?: (v: string) => void; onSignInMode?: (v: string) => void; onTenantId?: (v: string) => void;
  onLoadWorkspaces?: () => void; onLoadReports?: () => void; onToggle?: (id: string) => void; onCheck?: () => void;
  folderPath?: string; onFolderPath?: (v: string) => void; onBrowse?: () => void; onAddFolder?: () => void;
  onRemove?: (id: string) => void; busy?: string | null; error?: string | null;
  consent?: boolean; onConsent?: (v: boolean) => void;
}) {
  const [showDetail, setShowDetail] = useState(false);
  // Anything chosen and not yet read. The Done/Escape sentence has to be about the state the person is in.
  const pendingSelection = scope.reports.some((r) => r.state === 'notChecked' || r.state === 'needsChecking');
  const stateWords = (r: M.ScopeReport) =>
    r.state === 'checked' ? `Checked ${r.checkedWhenUtc ? timeFormat(r.checkedWhenUtc) : ''}`.trim()
      : r.state === 'couldNotBeFullyChecked' ? 'Could not be fully checked'
      : r.state === 'needsChecking' ? 'Needs checking again'
      : 'Not checked';

  return (
    <aside role="dialog" aria-label="Choose reports" data-testid="choose-reports-drawer"
      className="flex flex-col gap-3 p-4 overflow-auto"
      style={{ background: 'var(--sem-surface)', borderLeft: '1px solid var(--sem-border)', width: 440, maxWidth: '100%' }}>
      <div className="flex items-center gap-2">
        <span className="text-[15px] font-semibold">Choose reports</span>
        <button onClick={onClose} data-testid="choose-reports-done" className="text-[12px] px-3 py-1 rounded-md font-medium ml-auto"
          style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)' }}>Done</button>
      </div>
      <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>
        Impact counts only the reports checked here. Your selection is kept for this model. Check them again when you reopen it.
      </div>
      {error && <div className="text-[11px] rounded-md px-2.5 py-2" data-testid="choose-reports-error" style={{ color: 'var(--sem-bad)', border: '1px solid var(--sem-bad)' }}>{error}</div>}

      <div className="rounded-lg border p-3 flex flex-col gap-2" style={{ borderColor: 'var(--sem-border)' }}>
        <div className="text-[12px] font-semibold">Published reports</div>
        {/* The permission fact stands before the sign-in, not after the choice. */}
        <div className="text-[11px] rounded-md px-2 py-1.5" data-testid="choose-reports-permission"
          style={{ color: 'var(--sem-warn)', border: '1px solid color-mix(in srgb, var(--sem-warn) 40%, transparent)' }}>
          {PERMISSION_SENTENCE}{' '}
          <button onClick={() => setShowDetail((v) => !v)} data-testid="choose-reports-permission-details" className="underline" style={{ color: 'inherit' }}>Details</button>
          {showDetail && <div className="mt-1" data-testid="choose-reports-permission-detail">{PERMISSION_DETAIL}</div>}
        </div>
        <AccountPicker mode={signInMode} onMode={(v) => onSignInMode?.(v)} tenantId={tenantId}
          onTenantId={(v) => onTenantId?.(v)} tenantLabel="where the reports live (optional)" />
        <div className="flex items-center gap-2 flex-wrap">
          <button onClick={onLoadWorkspaces} disabled={busy != null} data-testid="choose-reports-load-workspaces"
            className="text-[12px] px-3 py-1 rounded-md" style={{ ...ctl }}>
            {busy === 'workspaces' ? 'Loading...' : workspaces ? 'Reload workspaces' : 'Find workspaces'}
          </button>
          {workspaces && (
            <select value={workspaceId ?? ''} onChange={(e) => onWorkspaceId?.(e.target.value)} data-testid="choose-reports-workspace"
              aria-label="Workspace" style={{ ...ctl, minWidth: 190 }}>
              <option value="">{workspaces.length === 0 ? 'No workspaces you can see' : 'Choose a workspace'}</option>
              {workspaces.map((w) => <option key={w.id} value={w.id}>{w.displayName}</option>)}
            </select>
          )}
          {workspaces && (
            <button onClick={onLoadReports} disabled={busy != null || !workspaceId} data-testid="choose-reports-load-reports"
              className="text-[12px] px-3 py-1 rounded-md font-medium"
              style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', opacity: workspaceId ? 1 : 0.5 }}>
              {busy === 'reports' ? 'Loading...' : 'List reports'}
            </button>
          )}
        </div>
        {reports && reports.map((r) => (
          <label key={r.id} data-testid="choose-reports-report" className="flex items-center gap-2 text-[12px]">
            <input type="checkbox" checked={selected.has(r.id)} onChange={() => onToggle?.(r.id)} aria-label={`Choose ${r.name}`} />
            <span className="truncate">{r.name}</span>
            <Muted className="ml-auto">{r.reportType === 'PaginatedReport' ? 'This kind of report cannot be read' : 'Can be read'}</Muted>
          </label>
        ))}
        {reports && reports.length === 0 && <Muted>No reports in that workspace, or you cannot see them.</Muted>}
        {onConsent && (
          <label className="flex items-start gap-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
            <input type="checkbox" checked={!!consent} onChange={(e) => onConsent(e.target.checked)} data-testid="choose-reports-consent" className="mt-0.5" />
            <span>Yes, sign in with that permission. Semanticus uses it only to read.</span>
          </label>
        )}
      </div>

      <div className="rounded-lg border p-3 flex flex-col gap-2" style={{ borderColor: 'var(--sem-border)' }}>
        <div className="text-[12px] font-semibold">Report files on this computer</div>
        <Muted>No sign-in needed.</Muted>
        <div className="flex items-center gap-2 flex-wrap">
          <input value={folderPath} onChange={(e) => onFolderPath?.(e.target.value)} data-testid="choose-reports-folder"
            aria-label="Report folder" placeholder="Path to a report folder" spellCheck={false} style={{ ...ctl, flex: 1, minWidth: 170 }} />
          <button onClick={onBrowse} disabled={busy != null} data-testid="choose-reports-browse" className="text-[12px] px-3 py-1 rounded-md" style={{ ...ctl }}>Browse...</button>
          <button onClick={onAddFolder} disabled={busy != null || !folderPath.trim()} data-testid="choose-reports-add-folder"
            className="text-[12px] px-3 py-1 rounded-md font-medium"
            style={{ background: 'var(--sem-accent)', color: 'var(--sem-on-accent)', opacity: folderPath.trim() ? 1 : 0.5 }}>Add this report folder</button>
        </div>
        <Muted>{LOCAL_FOLDER_HINT}</Muted>
      </div>

      <div className="rounded-lg border p-3 flex flex-col gap-1" style={{ borderColor: 'var(--sem-border)' }}>
        <div className="text-[12px] font-semibold">Chosen for this model</div>
        {scope.reports.length === 0 && <Muted>Nothing chosen yet.</Muted>}
        {scope.reports.map((r) => (
          <div key={r.id} data-testid="choose-reports-chosen" className="flex items-start gap-2 text-[12px] py-1">
            <input type="checkbox" className="mt-1" checked={selected.has(r.id)} onChange={() => onToggle?.(r.id)} aria-label={`Check ${r.name}`} />
            <div className="min-w-0 flex-1">
              <div className="truncate" title={r.name}>{r.name}</div>
              <div className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>
                {r.kind === 'published' ? r.source : 'on this computer'} · <span data-testid="choose-reports-state">{stateWords(r)}</span>
              </div>
            </div>
            {onRemove && (
              <button onClick={() => onRemove(r.id)} data-testid="choose-reports-remove" className="text-[11px] underline shrink-0 mt-0.5" style={{ color: 'var(--sem-muted)' }}>Exclude from checks</button>
            )}
          </div>
        ))}
        {scope.reports.length > 0 && <Muted>Excluding a report from checks leaves the report itself alone.</Muted>}
      </div>

      <div className="flex items-center gap-2 flex-wrap">
        <button onClick={onCheck} disabled={busy != null || selected.size === 0} data-testid="choose-reports-check"
          className="text-[12px] px-3 py-1 rounded-md font-medium"
          style={{ background: selected.size === 0 ? 'var(--sem-surface-2)' : 'var(--sem-accent)', color: selected.size === 0 ? 'var(--sem-muted)' : 'var(--sem-on-accent)' }}>
          {busy === 'check' ? 'Checking...' : `Check ${selected.size} selected report${selected.size === 1 ? '' : 's'}`}
        </button>
        {/* A save clears the reading it was made against, so "keeps the last completed check" was false the
            moment anything was added or excluded. Say which of the two states you are actually in. */}
        <Muted data-testid="choose-reports-escape-note">
          {pendingSelection
            ? `Your changed selection needs checking. Done and Escape keep the selection${returningTo ? ` and bring you back to ${returningTo}` : ''}; nothing is read until you check it.`
            : `Done and Escape keep the last completed check${returningTo ? ` and bring you back to ${returningTo}` : ''}.`}
        </Muted>
      </div>
    </aside>
  );
}
