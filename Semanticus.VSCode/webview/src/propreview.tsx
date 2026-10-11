import { upgradeToPro } from './pro';
import { FEATURE_NAME, featureOfTool, type ProFeature } from './features';

// What a Pro tool looks like on a plan that does not reach it.
//
// THREE rules decide everything in this file.
//
// 1. A faithful replica, not a summary. A person should be able to see what the tool IS before deciding
//    whether to pay for it, so each preview keeps the real page's shape: its own layout (a table, a card
//    grid, a rail and a document, an editor and its applied steps), the real control names in their real
//    order, and one complete example outcome. The first version rendered every tool as the same short list
//    of rows, which showed that a page exists and nothing about what it does.
// 2. NEVER A GATED READ. The rejected design loaded all the paid read data and only softened the writes,
//    which rebuilt the operation split Kane threw out. So this file calls nothing. It has no rpc import, no
//    effects and no state. Every figure is example data on a placeholder model, said out loud, never the
//    open one.
// 3. DIM THE CONTROLS, NOT THE CONTENT. A control is soft because pressing it would do nothing; the content
//    is the whole point of showing the page, so it stays at full contrast and it is NOT aria-hidden. A
//    screen reader gets the same preview a sighted reader gets.
//
// The pill and the one Unlock Pro action are the only live things in the file.

// ---- the soft kit -----------------------------------------------------------------------------------
// `aria-disabled` plus `pointer-events: none` is the pair: the first tells assistive tech, the second stops
// a stray click reading as a broken button.
const SOFT = { pointerEvents: 'none' as const, opacity: 0.5 };

function Ctl({ children, primary }: { children: React.ReactNode; primary?: boolean }) {
  return (
    <span className={`sem-btn sem-btn-sm${primary ? ' sem-btn-primary' : ''}`} aria-disabled="true" style={SOFT}>{children}</span>
  );
}
function Field({ label, value, placeholder }: { label?: string; value?: string; placeholder?: string }) {
  return (
    <span className="inline-flex items-center gap-2 text-xs">
      {label && <span style={{ color: 'var(--sem-muted)' }}>{label}</span>}
      <span aria-disabled="true" className="rounded-(--sem-radius-control) border px-2 py-1"
        style={{ ...SOFT, borderColor: 'var(--sem-border)', background: 'transparent', color: value ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>
        {value ?? placeholder}
      </span>
    </span>
  );
}
// Instrument: a state is a word with a dot in its colour, never a coloured pill.
function Pill({ children, tone }: { children: React.ReactNode; tone?: 'good' | 'warn' | 'accent' }) {
  const dot = tone === 'good' ? ' sem-chip-ok' : tone === 'warn' ? ' sem-chip-warn' : tone === 'accent' ? ' sem-chip-dot' : '';
  return <span className={`sem-chip${dot}`} style={tone ? { color: 'var(--sem-fg)' } : undefined}>{children}</span>;
}
function Toolbar({ children }: { children: React.ReactNode }) {
  return <div className="flex flex-wrap items-center gap-2">{children}</div>;
}
function Panel({ title, sub, right, children }: {
  title?: string; sub?: string; right?: React.ReactNode; children: React.ReactNode;
}) {
  return (
    <section className="sem-inset">
      {(title || right) && (
        <div className="flex flex-wrap items-baseline justify-between gap-2 mb-2">
          <div className="min-w-0">
            {title && <h3 className="m-0 text-(length:--sem-text-body) font-semibold">{title}</h3>}
            {sub && <div className="text-xs mt-1" style={{ color: 'var(--sem-muted)' }}>{sub}</div>}
          </div>
          {right}
        </div>
      )}
      {children}
    </section>
  );
}
/** A real data table with the real column headers. Content is readable; nothing here is a control. */
function Table({ cols, rows }: { cols: string[]; rows: React.ReactNode[][] }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-xs" style={{ borderCollapse: 'collapse' }}>
        <thead>
          <tr>{cols.map((c) => (
            <th key={c} className="text-left font-normal text-xs px-2 py-2"
              style={{ color: 'var(--sem-muted)', borderBottom: '1px solid var(--sem-rule)' }}>{c}</th>
          ))}</tr>
        </thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i}>{r.map((cell, j) => (
              <td key={j} className="px-2 py-2 align-top" style={{ borderBottom: '1px solid var(--sem-rule)' }}>{cell}</td>
            ))}</tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
/** The left rail several of these pages carry, with its current item marked the way the real one marks it. */
function Rail({ title, items, active }: { title: string; items: string[]; active: string }) {
  return (
    <nav className="shrink-0" aria-label={title} style={{ width: 164 }}>
      <div className="sem-label mb-2">{title}</div>
      <div className="grid gap-1">
        {items.map((item) => (
          <span key={item} className="px-2 py-1 text-xs"
            style={item === active
              ? { boxShadow: 'inset 2px 0 0 var(--sem-fg)', color: 'var(--sem-fg)', fontWeight: 600 }
              : { color: 'var(--sem-muted)' }}>{item}</span>
        ))}
      </div>
    </nav>
  );
}
function Code({ lines }: { lines: string[] }) {
  return (
    <pre className="m-0 rounded-(--sem-radius-control) border p-3 text-xs leading-5 overflow-x-auto"
      style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)', color: 'var(--sem-fg)',
        fontFamily: 'ui-monospace,SFMono-Regular,Consolas,monospace' }}>{lines.join('\n')}</pre>
  );
}
function Cards({ children }: { children: React.ReactNode }) {
  // flex-1: the cards fill the row down to the rail's foot, so the next inset sits one gap below both.
  return <div className="grid flex-1 gap-3" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(250px, 1fr))' }}>{children}</div>;
}
function Card({ title, badge, children }: { title: string; badge?: React.ReactNode; children: React.ReactNode }) {
  return (
    <article className="sem-inset">
      <div className="flex items-center gap-2">
        <div className="text-xs font-semibold">{title}</div>{badge}
      </div>
      <div className="mt-2 text-xs grid gap-1" style={{ color: 'var(--sem-muted)' }}>{children}</div>
    </article>
  );
}
function Label({ children }: { children: React.ReactNode }) {
  return <div className="sem-label">{children}</div>;
}

// A step's mark in the doer hues, the same three shapes the Plan and the Workflows list draw.
function Mark({ doer }: { doer: 'semanticus' | 'you' | 'assistant' }) {
  return <svg className={`pw-ico pw-doer-${doer}`} viewBox="0 0 12 12" role="img" aria-label={doer === 'semanticus' ? 'Semanticus' : doer === 'you' ? 'You choose' : 'Your assistant writes'}>
    {doer === 'semanticus' && <rect x="2" y="2" width="8" height="8" fill="currentColor" />}
    {doer === 'you' && <circle cx="6" cy="6" r="4.3" fill="currentColor" />}
    {doer === 'assistant' && <path d="M6 1L11 6 6 11 1 6z" fill="currentColor" />}
  </svg>;
}

// ---- the replica ------------------------------------------------------------------------------------
// One, because one whole tab is paid from 22 September 2026. The other nine replicas went with the gates
// they explained: Model Spec, Advanced modelling, Power Query, Docs, Model notes, Validation, Reports,
// Data Agent and Published Advanced are free and render their real pages.
// Placeholder model only (Contoso). Nothing came from a real tenant and nothing is a measurement.

/** `line` is the one quiet sentence above the example; `free` says what still works without Pro, when that is worth saying. */
interface Spec { line: string; free?: string; body: React.ReactNode }

const PREVIEWS: Record<string, Spec> = {

  // Ask your data (26 September 2026): the page as it is, a question built from parts, its answer and the list of
  // questions asked before. Example data on a placeholder model; no call is made.
  'askdata': {
    line: 'Ask a question about your numbers. Semanticus builds and checks the query.',
    free: 'Part of Pro. Queries and your assistant still run any query for free.',
    body: <div className="flex gap-3 flex-wrap">
      <Rail title="Asked before" items={['Net Sales by Store Region', 'Units by Category, top 10', 'Margin by Segment']} active="Net Sales by Store Region" />
      <div className="grid gap-3 flex-1 min-w-0">
        <Toolbar><Ctl>Net Sales</Ctl><span className="text-xs" style={{ color: 'var(--sem-muted)' }}>by</span><Ctl>Store Region</Ctl><Ctl>Year = 2025</Ctl><Ctl>Add</Ctl></Toolbar>
        <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>Built from your choices. Every column reaches Net Sales. The query checks out. 3 rows.</div>
        <Table cols={['Store Region', 'Net Sales']} rows={[['Coast', '1,204,310'], ['Inland', '802,115'], ['North', '640,480']]} />
        <Toolbar><Ctl>Show DAX</Ctl><Ctl>Open in Queries</Ctl><Ctl>Save as a check</Ctl></Toolbar>
      </div>
    </div>,
  },

  // Workflows (lane W2, contract 21.1 row 8): the one page as it really is, your workflows as rows with a mark
  // per step in the doer hues, the last run, a Required mark and Run, drawn with the page's own classes.
  'workflows': {
    line: 'Workflows is part of Pro.',
    body: <div className="sem-wf-list-page">
      <div className="sem-wf-head">
        <span className="text-(length:--sem-text-title) font-semibold">Workflows</span>
        <span className="sem-wf-note">3 workflows in this model</span>
        <span className="sem-wf-head-r"><Field placeholder="Search your workflows" /><Ctl primary>New workflow</Ctl></span>
      </div>
      <div className="sem-wf-list">
        <div className="sem-wf-lhead"><span>Name</span><span>Steps</span><span>Last run</span><span /><span /></div>
        {([
  { title: 'Test a complex measure', sub: '4 steps · from a starter', marks: ['you', 'assistant', 'semanticus', 'semanticus'], last: 'Tuesday · passed', required: 'Required for new measures' },
  { title: 'Month-end close for Contoso Retail', sub: '5 steps · from a starter', marks: ['semanticus', 'semanticus', 'you', 'semanticus', 'semanticus'], last: '1 September · passed' },
  { title: 'Publish to Test with checks', sub: '3 steps', marks: ['semanticus', 'you', 'semanticus'], last: 'Never run' },
] as { title: string; sub: string; marks: ('semanticus' | 'you' | 'assistant')[]; last: string; required?: string }[]).map((r) => <div key={r.title} className="sem-wf-row">
          <span className="sem-wf-row-name"><b>{r.title}</b><span className="sem-wf-note">{r.sub}</span></span>
          <span className="sem-wf-strip">{r.marks.map((m, i) => <Mark key={i} doer={m} />)}</span>
          <span className="sem-wf-last">{r.last}</span>
          <span className="sem-wf-marks">{r.required && <span className="sem-wf-req">{r.required}</span>}</span>
          <span className="sem-wf-row-r"><Ctl>Run</Ctl></span>
        </div>)}
      </div>
      <div className="sem-wf-foot"><span className="sem-wf-note">Team rules</span>
        <span className="sem-wf-legend"><span><Mark doer="semanticus" />Semanticus</span><span><Mark doer="you" />You choose</span><span><Mark doer="assistant" />Your assistant writes</span></span></div>
    </div>,
  },
};

/** The preview a locked tool renders instead of its real page. `tool` is a Studio tool id.
 *
 *  `embedded` is kept for an in-page caller: a page that already owns its padding would otherwise get a
 *  second copy of it, which pushed the replica down by a third of the fold and read as a missing panel. */
export function ProPreview({ tool, embedded }: { tool: string; embedded?: boolean }) {
  const spec = PREVIEWS[tool];
  const feature: ProFeature | null = featureOfTool(tool);
  // An unmapped tool is a coding mistake, not a state a person should meet a blank page in. Fall back to
  // the feature line alone rather than rendering nothing.
  const line = spec?.line ?? `${feature ? FEATURE_NAME[feature] : 'This tool'} is part of Pro.`;

  return (
    <div className={embedded ? undefined : 'h-full overflow-auto'} data-testid="pro-preview" data-pro-preview={tool}>
      <div className={embedded ? 'flex flex-col gap-3' : 'sem-evidence-page flex flex-col gap-3 pt-3'}>
        {/* The page title is the area row's, so this is one quiet line and the one way in. */}
        <div className="flex items-start gap-4">
          <div className="min-w-0 flex-1">
            <p className="m-0 text-(length:--sem-text-body)" style={{ color: 'var(--sem-fg)' }}>{line}</p>
            {spec?.free && <p className="m-0 mt-1 text-xs" style={{ color: 'var(--sem-muted)' }}>{spec.free}</p>}
          </div>
          <button type="button" data-testid="pro-preview-unlock" onClick={upgradeToPro}
            className="sem-btn sem-btn-sm sem-btn-primary shrink-0">Unlock Pro</button>
        </div>

        {/* Said out loud, ABOVE the replica. Without this line the page below reads as a measurement of the
            model that is actually open, which is the one thing a preview must never do. Neither this line
            nor anything under it is aria-hidden: the replica is the point of the page, so a screen reader
            gets it too. */}
        <div className="flex flex-col gap-3">
          <div className="sem-label">
            Example. This is not your model.
          </div>
          {spec?.body}
        </div>
      </div>
    </div>
  );
}
