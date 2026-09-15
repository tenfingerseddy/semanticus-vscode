import { manageLicense } from './bridge';
import { FEATURE_NAME, FEATURE_STILL_FREE, featureOfTool, type ProFeature } from './features';

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
    <span className="inline-flex items-center gap-1.5 text-[11px]">
      {label && <span style={{ color: 'var(--sem-muted)' }}>{label}</span>}
      <span aria-disabled="true" className="rounded-md border px-2 py-1"
        style={{ ...SOFT, borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)', color: value ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>
        {value ?? placeholder}
      </span>
    </span>
  );
}
function Pill({ children, tone }: { children: React.ReactNode; tone?: 'good' | 'warn' | 'accent' }) {
  const color = tone ? `var(--sem-${tone})` : 'var(--sem-muted)';
  return (
    <span className="rounded-full border px-2 py-0.5 text-[9.5px] font-semibold uppercase tracking-wide whitespace-nowrap"
      style={{ borderColor: color, color }}>{children}</span>
  );
}
function Toolbar({ children }: { children: React.ReactNode }) {
  return <div className="flex flex-wrap items-center gap-1.5">{children}</div>;
}
function Panel({ title, sub, right, children }: {
  title?: string; sub?: string; right?: React.ReactNode; children: React.ReactNode;
}) {
  return (
    <section className="rounded-lg border p-3" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-bg)' }}>
      {(title || right) && (
        <div className="flex flex-wrap items-baseline justify-between gap-2 mb-2">
          <div className="min-w-0">
            {title && <h3 className="m-0 text-[12.5px] font-semibold">{title}</h3>}
            {sub && <div className="text-[11px] mt-0.5" style={{ color: 'var(--sem-muted)' }}>{sub}</div>}
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
      <table className="w-full text-[11.5px]" style={{ borderCollapse: 'collapse' }}>
        <thead>
          <tr>{cols.map((c) => (
            <th key={c} className="text-left font-semibold uppercase tracking-wide text-[9.5px] px-2 py-1.5"
              style={{ color: 'var(--sem-muted)', borderBottom: '1px solid var(--sem-border)' }}>{c}</th>
          ))}</tr>
        </thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i}>{r.map((cell, j) => (
              <td key={j} className="px-2 py-1.5 align-top" style={{ borderBottom: '1px solid var(--sem-border)' }}>{cell}</td>
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
      <div className="text-[9.5px] font-semibold uppercase tracking-[0.14em] mb-2" style={{ color: 'var(--sem-muted)' }}>{title}</div>
      <div className="grid gap-0.5">
        {items.map((item) => (
          <span key={item} className="rounded-md px-2 py-1.5 text-[11.5px]"
            style={item === active
              ? { background: 'var(--sem-accent-soft)', color: 'var(--sem-fg)', fontWeight: 600 }
              : { color: 'var(--sem-muted)' }}>{item}</span>
        ))}
      </div>
    </nav>
  );
}
function Code({ lines }: { lines: string[] }) {
  return (
    <pre className="m-0 rounded-md border p-2.5 text-[11px] leading-5 overflow-x-auto"
      style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)', color: 'var(--sem-fg)',
        fontFamily: 'ui-monospace,SFMono-Regular,Consolas,monospace' }}>{lines.join('\n')}</pre>
  );
}
function Cards({ children }: { children: React.ReactNode }) {
  return <div className="grid gap-2" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(250px, 1fr))' }}>{children}</div>;
}
function Card({ title, badge, children }: { title: string; badge?: React.ReactNode; children: React.ReactNode }) {
  return (
    <article className="rounded-lg border p-3" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }}>
      <div className="flex items-center gap-2">
        <div className="text-[12px] font-semibold">{title}</div>{badge}
      </div>
      <div className="mt-1.5 text-[11px] grid gap-1" style={{ color: 'var(--sem-muted)' }}>{children}</div>
    </article>
  );
}
function Label({ children }: { children: React.ReactNode }) {
  return <div className="text-[9.5px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--sem-muted)' }}>{children}</div>;
}

// ---- the ten replicas -------------------------------------------------------------------------------
// Placeholder model only (Contoso). Nothing came from a real tenant and nothing is a measurement.

interface Spec { title: string; what: string; body: React.ReactNode }

const PREVIEWS: Record<string, Spec> = {
  // Model Spec: the real toolbar, the summary row, a table draft with its columns, one measure expression,
  // a relationship, and what a build ends with.
  'spec': {
    title: 'Model Spec',
    what: 'Model Spec is one shared draft for you and your assistant. You review it here before anything is built.',
    body: <>
      <Toolbar>
        <Ctl>Open spec…</Ctl><Ctl>Save spec…</Ctl><Ctl>Autogenerate from model</Ctl>
        <Ctl>Autogenerate from SQL…</Ctl><Ctl>Edit JSON</Ctl><Ctl>Clear</Ctl>
        <Ctl primary>Build into model →</Ctl>
      </Toolbar>
      <div className="mt-3 flex flex-wrap items-center gap-2 text-[11.5px]">
        <b>Contoso</b><Pill>Import</Pill><Pill>CL 1604</Pill>
        <span style={{ color: 'var(--sem-muted)' }}>3 tables (1 fact · 1 dim) · 2 relationships · 2 measures</span>
      </div>
      <div className="mt-2 grid gap-2">
        <Panel title="Sales" sub="fact · dbo.FactSales">
          <Table cols={['Column', 'Type', 'Summarise by', 'Key', 'Hidden']} rows={[
            ['SalesAmount', 'Decimal', 'Sum', '', ''],
            ['Quantity', 'Int64', 'Sum', '', ''],
            ['CustomerKey', 'Int64', 'none', 'key', 'hidden'],
            ['OrderDate', 'DateTime', 'none', '', ''],
          ]} />
          <div className="mt-2 text-[11.5px]"><b>Total Sales</b>
            <span style={{ color: 'var(--sem-muted)' }}> · format $#,##0 · folder Base Measures</span></div>
          <div className="mt-1"><Code lines={['SUM ( Sales[SalesAmount] )']} /></div>
          <div className="mt-1.5 flex items-center gap-2"><Pill tone="good">valid</Pill>
            <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Sales to Customer · many to one · on CustomerKey</span></div>
        </Panel>
        <Panel title="Build result">
          <div className="text-[11.5px]">Built 3 tables, 2 relationships and 2 measures into the model, as one undoable step.</div>
        </Panel>
      </div>
    </>,
  },

  // Advanced Modelling: the real subtool row, a populated perspective matrix, and a worked calculation group.
  'advmodels': {
    title: 'Advanced Modelling',
    what: 'Advanced Modelling builds calculation groups, calendars, field parameters and who is allowed to see what.',
    body: <>
      <Toolbar>
        <Ctl primary>Perspectives</Ctl><Ctl>Field parameters</Ctl><Ctl>Calc groups</Ctl>
        <Ctl>Calendars</Ctl><Ctl>RLS / OLS</Ctl><Ctl>DaxLib</Ctl>
      </Toolbar>
      <div className="mt-2 grid gap-2">
        <Panel title="Perspectives" sub="Choose which fields appear in a named view of the model. Selecting a table includes its fields."
          right={<Ctl>+ Perspective</Ctl>}>
          <Table cols={['Object', 'Finance', 'Sales Ops']} rows={[
            ['Sales (15)', 'included', ''],
            ['Date (3)', 'included', 'included'],
            ['Customer (6)', '', 'included'],
            ['Product (2)', '', 'included'],
            ['Budget (0)', '', ''],
          ]} />
        </Panel>
        <Panel title="Time Intelligence" sub="calculation group · 3 items · precedence 10">
          <Table cols={['Item', 'Expression']} rows={[
            ['YTD', <Code key="a" lines={["CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'Date'[Date] ) )"]} />],
            ['YoY %', <Code key="b" lines={['DIVIDE ( SELECTEDMEASURE () - [PY], [PY] )']} />],
          ]} />
          <div className="mt-1.5 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
            Total Sales with YTD applied returns 7,918,204.11 for the year to 12 July.
          </div>
        </Panel>
      </div>
    </>,
  },

  // Power Query: the context bar, readable M, the applied steps, and the small table it produces.
  'mcode': {
    title: 'Power Query',
    what: 'Power Query shows where each table gets its data, and lets you change how it is loaded.',
    body: <>
      <div className="flex flex-wrap items-center gap-2">
        <Field label="Table" value="Sales" />
        <Field label="Editing" value="Query · Sales" />
        <Pill tone="good">valid M</Pill>
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>Refresh: On · store 5 years · refresh 10 days</span>
      </div>
      <div className="mt-2"><Toolbar><Ctl>Format</Ctl><Ctl>Save M</Ctl><Ctl>Revert</Ctl><Ctl>New query</Ctl></Toolbar></div>
      <div className="mt-2 flex gap-3 flex-wrap items-start">
        <div className="min-w-0" style={{ flex: '1 1 380px' }}>
          <Label>M editor</Label>
          <div className="mt-1"><Code lines={[
            'let',
            '    Source = Sql.Database("contoso-sql", "DW"),',
            '    dbo_FactSales = Source{[Schema="dbo",Item="FactSales"]}[Data],',
            '    #"Removed Columns" = Table.RemoveColumns(dbo_FactSales, {"ETLLoadID"}),',
            '    #"Changed Type" = Table.TransformColumnTypes(#"Removed Columns",',
            '        {{"SalesAmount", type number}})',
            'in',
            '    #"Changed Type"',
          ]} /></div>
        </div>
        <div style={{ flex: '0 1 190px' }}>
          <Label>Applied steps (5)</Label>
          <div className="mt-1 grid gap-0.5 text-[11.5px]">
            {['1 Source', '2 dbo_FactSales', '3 Removed Columns', '4 Changed Type', '5 Filtered Rows'].map((step) => (
              <span key={step} className="rounded-md px-2 py-1" style={{ background: 'var(--sem-surface-2)' }}>{step}</span>
            ))}
          </div>
        </div>
      </div>
      <div className="mt-2"><Panel title="Preview" sub="the first rows this query returns">
        <Table cols={['OrderDate', 'SalesAmount', 'Quantity']} rows={[
          ['2026-07-01', '1,240.00', '3'], ['2026-07-01', '86.50', '1'], ['2026-07-02', '2,310.75', '6'],
        ]} />
      </Panel></div>
    </>,
  },

  // Docs: the Include rail, the real export controls, and a formatted page carrying a measure description.
  'docs': {
    title: 'Docs',
    what: 'Docs turns your model into a write up you can hand to someone else.',
    body: <>
      <div className="flex gap-4 flex-wrap items-start">
        <Rail title="Include" active="Measures index"
          items={['Per-table detail', 'Columns', 'DAX expressions', 'Measures index', 'Relationships', 'Roles and RLS', 'Prep-for-AI surface']} />
        <div className="min-w-0" style={{ flex: '1 1 360px' }}>
          <Toolbar><Ctl primary>HTML</Ctl><Ctl>Markdown</Ctl><Ctl>Print / PDF</Ctl><Ctl>Export…</Ctl></Toolbar>
          <div className="mt-2 rounded-lg border p-4" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface-2)' }}>
            <div className="text-[16px] font-semibold">Contoso</div>
            <div className="text-[11.5px]" style={{ color: 'var(--sem-muted)' }}>Semantic Model Documentation</div>
            <div className="mt-1.5"><Pill tone="good">AI-Readiness B</Pill></div>
            <div className="mt-3 text-[12.5px] font-semibold">Overview</div>
            <p className="m-0 mt-1 text-[11.5px]">
              The Contoso model is the single source of truth for commercial reporting, following a Kimball star schema.
            </p>
            <div className="mt-3 text-[12.5px] font-semibold">Measures</div>
            <p className="m-0 mt-1 text-[11.5px]">
              <b>Total Sales</b> Net sales after returns and discounts. Use this for any revenue question.
            </p>
            <div className="mt-1"><Code lines={['SUM ( Sales[SalesAmount] )']} /></div>
          </div>
        </div>
      </div>
    </>,
  },

  // Model notes: the section navigation, readable business context and gotchas, one saved lesson with its source.
  'knowledge': {
    title: 'Model notes',
    what: 'Model notes keeps the written background your assistant reads before it answers a question.',
    body: <>
      <Toolbar>
        <Ctl>Overview</Ctl><Ctl>Business context</Ctl><Ctl>Gotchas</Ctl><Ctl>Patterns</Ctl>
        <Ctl>Known issues</Ctl><Ctl>History</Ctl><Ctl>Insights</Ctl><Ctl>Recall</Ctl>
        <Ctl primary>Edit notes</Ctl>
      </Toolbar>
      <div className="mt-2 grid gap-2">
        <Panel title="Contoso Retail Primer" sub="Saved beside this model · updated 11 July 2026">
          <div className="text-[12px] font-semibold">Business context</div>
          <p className="m-0 mt-1 text-[11.5px]">
            The fiscal year starts on 1 July. Sales are net of returns and discounts. Budget figures are
            monthly and must not be added up across years.
          </p>
          <div className="mt-2.5 text-[12px] font-semibold">Gotchas</div>
          <p className="m-0 mt-1 text-[11.5px]">
            Store 0 is the online shop, not a building. Month-end stock can lag the sales snapshot by one
            refresh cycle.
          </p>
        </Panel>
        <Panel title="Insights (3)" sub="Lessons your assistant recalls before it answers">
          <Table cols={['Lesson', 'Where it came from', 'Used']} rows={[
            ['Qualify revenue by region when the question does not name one.', 'Observed in 2 source runs', '4 times'],
            ['Budget is monthly. Never add it up across years.', 'Saved by a person', '2 times'],
          ]} />
        </Panel>
      </div>
    </>,
  },

  // Tests: the real toolbar and result columns, a pass and a mismatch with expected and actual, plus the
  // relationship and row-count families that run beside them.
  'tests': {
    title: 'Tests',
    // Not "a record of every run": tests.tsx defaults persist=false, so a run is NOT written down unless
    // you ask for it. Promising a record here would be a promise the product does not keep.
    what: 'Tests checks your numbers against the source and lets you save the results.',
    body: <>
      <Toolbar>
        <Ctl primary>Run all enabled checks</Ctl><Ctl>New check</Ctl><Ctl>Report</Ctl>
        <span className="text-[11px]" style={{ color: 'var(--sem-muted)' }}>SQL sources: 2</span>
      </Toolbar>
      <div className="mt-2 grid gap-2">
        <Panel title="Saved checks" sub="Each one compares a number in your model with an answer you trust.">
          <Table cols={['Check', 'Kind', 'Expected · Actual', 'Result', 'Last run']} rows={[
            ['Total Sales ties to the GL', 'Compare with source', '11,532,660.44 · 11,532,660.44',
              <Pill key="a" tone="good">pass</Pill>, 'yesterday 9:30 AM'],
            ['Net Revenue ties to finance extract', 'Compare with source', '9,204,881.10 · 9,198,442.67',
              <Pill key="b" tone="warn">differs</Pill>, 'yesterday 9:30 AM'],
            ['Gross Margin % totals 41.2%', 'Trusted answer', '41.2% · 41.2%',
              <Pill key="c" tone="good">pass</Pill>, 'yesterday 9:30 AM'],
          ]} />
          <div className="mt-1.5 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
            Net Revenue differs by 6,438.43. Open the check to see the rows behind each number.
          </div>
        </Panel>
        <div className="grid gap-2" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(280px, 1fr))' }}>
          <Panel title="Relationships" sub="Checked when you run everything.">
            <Table cols={['Relationship', 'Result']} rows={[
              ['Sales to Date', <Pill key="a" tone="good">one to many</Pill>],
              ['Sales to Customer', <Pill key="b" tone="warn">2 keys with no match</Pill>],
            ]} />
          </Panel>
          <Panel title="Table row counts" sub="Model rows against the source table.">
            <Table cols={['Table', 'Model', 'Source', 'Result']} rows={[
              ['Sales', '2,410,882', '2,410,882', <Pill key="c" tone="good">match</Pill>],
              ['Customer', '18,484', '18,492', <Pill key="d" tone="warn">8 short</Pill>],
            ]} />
          </Panel>
        </div>
      </div>
    </>,
  },

  // Saved reports: the real card grid with model, date and coverage, plus an excerpt of what was checked.
  'evidence': {
    title: 'Saved reports',
    what: 'Saved reports keeps the proof of a run so you can show it to someone later.',
    body: <>
      <Cards>
        <Card title="Contoso Retail test evidence">
          <span>Tests run · 11 July 2026, 9:30 AM</span>
          <span>Saved with Contoso Retail by a person</span>
          <span>12 of 15 checked · 3 could not be checked</span>
          <span><Ctl>Open</Ctl></span>
        </Card>
        <Card title="Month-end close evidence">
          <span>Workflow run · 10 July 2026, 2:15 PM</span>
          <span>Saved with Contoso Retail by a person</span>
          <span>5 of 5 checked</span>
          <span><Ctl>Open</Ctl></span>
        </Card>
      </Cards>
      <div className="mt-2"><Panel title="Contoso Retail test evidence" sub="What this report says was checked">
        <Table cols={['Check', 'Expected · Actual', 'Result']} rows={[
          ['Total Sales ties to the GL', '11,532,660.44 · 11,532,660.44', <Pill key="a" tone="good">pass</Pill>],
          ['Net Revenue ties to finance extract', '9,204,881.10 · 9,198,442.67', <Pill key="b" tone="warn">differs</Pill>],
          ['Returns rate ties to ops report', 'not run', <Pill key="c">could not check</Pill>],
        ]} />
        <div className="mt-1.5 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
          Run against Contoso Retail at revision 41. The report is sealed, so this list cannot change afterwards.
        </div>
      </Panel></div>
    </>,
  },

  // Workflows: the real rail and job cards, plus one short run with named steps, a required check and its result.
  'workflows': {
    title: 'Workflows',
    what: 'Workflows turns a job you repeat into steps the app checks for you as you go.',
    body: <>
      <div className="flex gap-4 flex-wrap items-start">
        <Rail title="Workflows" active="Home" items={['Home', 'Library', 'Runs', 'Governance', 'Author']} />
        <div className="min-w-0" style={{ flex: '1 1 360px' }}>
          <div className="text-[12.5px] font-semibold mb-2">Start a job</div>
          <Cards>
            <Card title="Add a measure">
              <span>A clear calculation with one number you can check it against.</span>
              <span><b style={{ color: 'var(--sem-fg)' }}>What you end up with:</b> a new measure with its
                format and description, checked against one number you trust.</span>
              <span><Ctl primary>Start</Ctl> <Ctl>Open</Ctl></span>
            </Card>
            <Card title="Tidy a model">
              <span>A quality and AI-readiness pass before a review or release.</span>
              <span><b style={{ color: 'var(--sem-fg)' }}>What you end up with:</b> selected quality issues
                fixed, then the scan checked again.</span>
              <span><Ctl primary>Start</Ctl> <Ctl>Open</Ctl></span>
            </Card>
          </Cards>
        </div>
      </div>
      <div className="mt-2"><Panel title="Add a measure" sub="Run started 12 July, 9:04 AM · step 2 of 3">
        <Table cols={['Step', 'What it does', 'Result']} rows={[
          ['1 Write the measure', 'Author the DAX and its format string', <Pill key="a" tone="good">done</Pill>],
          ['2 Check one number', 'Required check: the measure ties to a number you trust',
            <Pill key="b" tone="accent">waiting for you</Pill>],
          ['3 Describe it', 'Write the description your assistant reads', <Pill key="c">not started</Pill>],
        ]} />
        <div className="mt-1.5 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
          Step 2 is a required check. The run cannot finish until it passes, and its result is kept with the run.
        </div>
      </Panel></div>
    </>,
  },

  // Data agent: the agent list, workspace context, the tables it shares, its instructions, example
  // questions and a publish state.
  'dataagent': {
    title: 'Data Agent',
    what: 'Data Agent publishes your model so Fabric can answer questions from it.',
    body: <>
      <div className="flex flex-wrap items-center gap-2">
        <Field label="Sign in as" value="Use the Azure command line" />
        <Field label="Workspace" value="Contoso [Dev]" />
        <Ctl>Refresh</Ctl>
      </div>
      <div className="mt-2 flex gap-3 flex-wrap items-start">
        <div style={{ flex: '0 1 210px' }}>
          <Label>Agents</Label>
          <div className="mt-1 grid gap-2">
            <Card title="Finance Copilot" badge={<Pill tone="good">published</Pill>}>
              <span>Answers finance questions over the Contoso model.</span>
            </Card>
            <Card title="Sales Ops Assistant" badge={<Pill>draft</Pill>}>
              <span>Ops-scoped agent for the sales team.</span>
            </Card>
          </div>
        </div>
        <div className="min-w-0" style={{ flex: '1 1 340px' }}>
          <Panel title="Finance Copilot" sub="da-finance-01 · Contoso [Dev] · 1 data source">
            <Label>Scope · tables shared</Label>
            <div className="mt-1 text-[11.5px]">Sales, Customer, Date, Product</div>
            <div className="mt-2.5"><Label>Instructions</Label></div>
            <p className="m-0 mt-1 text-[11.5px]">
              Answer in whole dollars. The fiscal year starts on 1 July. Say when a number is a forecast.
            </p>
            <div className="mt-2.5"><Label>Example questions</Label></div>
            <div className="mt-1 text-[11.5px] grid gap-0.5">
              <span>What were total sales in the last fiscal year?</span>
              <span>Which region had the highest margin last quarter?</span>
            </div>
            <div className="mt-2.5"><Ctl primary>Publish the agent</Ctl> <Ctl>+ Add this model</Ctl></div>
          </Panel>
        </div>
      </div>
    </>,
  },

  // Published > Advanced: the three distinct panels, the Data Agent entry, and a concrete change preview.
  'deploy-advanced': {
    title: 'Advanced',
    what: 'Advanced connects source control, keeps a Fabric workspace in step, sets up automated delivery and publishes a data agent.',
    body: <>
      <Toolbar><Ctl primary>Delivery tools</Ctl><Ctl>Data Agent</Ctl></Toolbar>
      <div className="mt-2 grid gap-2">
        <Panel title="Source Control" sub="local git and model versioning"
          right={<Toolbar><Ctl>Pull</Ctl><Ctl>Push</Ctl><Ctl>Readiness gate</Ctl></Toolbar>}>
          <div className="flex flex-wrap items-center gap-2 text-[11.5px]">
            <span>on <b>feature/ai-ready</b></span><Pill>origin/feature/ai-ready</Pill>
            <Pill>2 ahead</Pill><Pill tone="warn">2 changed · unsaved edits</Pill>
          </div>
          <div className="mt-2 flex flex-wrap gap-2 items-center"><Field placeholder="commit message…" /><Ctl primary>Commit</Ctl></div>
          <div className="mt-2"><Table cols={['What would be committed', 'Change']} rows={[
            ['Sales/Total Sales.measure.tmdl', 'format string and description changed'],
            ['Customer.table.tmdl', '1 column hidden'],
          ]} /></div>
        </Panel>
        <Panel title="Fabric Git" sub="keep a workspace and git in step">
          <div className="flex flex-wrap items-center gap-2">
            <Field label="Sign in as" value="Sign in in a browser" />
            <Field placeholder="workspace id" /><Ctl>Status</Ctl>
          </div>
          <div className="mt-1.5 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
            Every click previews first. A second Confirm runs the live write.
          </div>
        </Panel>
        <Panel title="Automated delivery" sub="publish from git, on every change">
          <Toolbar><Ctl>Write the build file</Ctl><Ctl>Publish from git</Ctl></Toolbar>
          <div className="mt-1.5 text-[11px]" style={{ color: 'var(--sem-muted)' }}>
            Last run 12 July, 6:02 AM. Published Contoso to Contoso [Test].
          </div>
        </Panel>
      </div>
    </>,
  },
};

/** The preview a locked tool renders instead of its real page. `tool` is a Studio tool id, or the one
 *  in-page id `deploy-advanced` for the Advanced mode inside Published.
 *
 *  `embedded` is for that in-page case: Published already owns the page's padding, so a second copy of it
 *  pushed the replica down by a third of the fold and left a gap that read as a missing panel. */
export function ProPreview({ tool, embedded }: { tool: string; embedded?: boolean }) {
  const spec = PREVIEWS[tool];
  const feature: ProFeature | null = tool === 'deploy-advanced' ? 'publishedAdvanced' : featureOfTool(tool);
  // An unmapped tool is a coding mistake, not a state a person should meet a blank page in. Fall back to
  // the feature line alone rather than rendering nothing.
  const title = spec?.title ?? (feature ? FEATURE_NAME[feature] : 'This tool');
  const what = spec?.what ?? 'This tool is part of Semanticus Pro.';
  const stillFree = feature ? FEATURE_STILL_FREE[feature] : '';
  const featureName = feature ? FEATURE_NAME[feature] : 'This';

  return (
    <div className={embedded ? undefined : 'h-full overflow-auto'} data-testid="pro-preview" data-pro-preview={tool}>
      <div className={embedded ? 'flex flex-col gap-3' : 'sem-evidence-page flex flex-col gap-3 pt-3'}>
        <section className={embedded ? 'rounded-xl border p-4' : 'rounded-xl border p-5'}
          style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
          <div className="flex items-start gap-4">
            <div className="min-w-0 flex-1">
              <div className="flex items-center gap-2">
                <h1 className={embedded ? 'm-0 text-[16px] font-semibold' : 'm-0 text-[22px] font-semibold'}>{title}</h1>
                <span className="text-[9px] uppercase tracking-wide font-bold px-1.5 py-px rounded"
                  style={{ background: 'color-mix(in srgb, var(--sem-accent) 20%, transparent)', color: 'var(--sem-accent)' }}>Pro</span>
              </div>
              <p className="m-0 mt-2 text-[13px]" style={{ color: 'var(--sem-fg)' }}>{what}</p>
              <p className="m-0 mt-1 text-[12px]" style={{ color: 'var(--sem-muted)' }}>
                {featureName} is part of Semanticus Pro. {stillFree}
              </p>
            </div>
            <button type="button" data-testid="pro-preview-unlock" onClick={manageLicense}
              className="sem-btn sem-btn-sm sem-btn-primary shrink-0">Unlock Pro</button>
          </div>
        </section>

        {/* Said out loud, ABOVE the replica. Without this line the page below reads as a measurement of the
            model that is actually open, which is the one thing a preview must never do. Neither this line
            nor anything under it is aria-hidden: the replica is the point of the page, so a screen reader
            gets it too. */}
        <div className="rounded-xl border px-4 py-3 flex flex-col gap-2"
          style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
          <div className="text-[10px] font-semibold uppercase tracking-[0.14em]" style={{ color: 'var(--sem-muted)' }}>
            Example. This is not your model.
          </div>
          {spec?.body}
        </div>
      </div>
    </div>
  );
}
