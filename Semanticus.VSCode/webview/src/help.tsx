import { useEffect, useMemo, useState } from 'react';

// ===================================================================================================
// Per-tab in-context help — the USER GUIDE (Studio v2). A '?' in the header opens a slide-over with
// two views: "This tab" (a detailed guide for the ACTIVE tab: concepts, tasks with the real button
// names, gotchas, Pro notes) and "Where do I…?" (a searchable task → location index, because plenty
// of authoring lives OUTSIDE Studio — the Model tree, the Properties view, the command palette — and
// users look here first). Content is written against the actual components; keep it that way: when a
// tab's controls change, its entry here changes in the same PR.
// ===================================================================================================

interface HelpSection { h: string; body?: string; bullets?: string[] }
interface SeeAlso { label: string; tab?: string; hint?: string }
interface TabHelp {
  title: string;
  lead: string;
  sections: HelpSection[];
  pro?: string;           // what's Pro-gated on this tab (omit when nothing is)
  tip?: string;
  seeAlso?: SeeAlso[];    // entries with a tab id render as clickable jumps
}

const HELP: Record<string, TabHelp> = {
  // ---------------------------------------------------------------- Build --------
  diagram: {
    title: 'Diagram',
    lead: 'An interactive map of your tables and relationships: arrange, audit, and edit the relationships themselves. Toggle "◈ Canvas / ▤ Relationships" in the header for the sortable grid view.',
    sections: [
      {
        h: 'Diagrams & layouts',
        bullets: [
          '"All tables" always shows every table; "＋ New" creates an empty custom diagram you curate with "＋ Add table…", the Add-tables palette (drag a tile onto the canvas), or a selected table\'s "＋ related" button.',
          'Layout buttons: "Bus matrix" (facts left, dimensions on top; the default), "Layered", "Vertical". Applying a layout collapses tables and saves positions.',
          'All-tables positions are saved beside the model, so they follow the model across machines and the AI Assistant sees them too. Custom-diagram layouts stay on this machine.',
        ],
      },
      {
        h: 'Create a relationship',
        bullets: [
          'Expand a table (▸ chevron or "Expand all"), then drag a column row onto a column of another table.',
          'A key-ish column (IsKey, or named *Key/*Id) is taken as the ONE side; the toast offers "Swap direction" if the guess was wrong.',
          'Dropping on an already-related pair offers "Add inactive": the way to add a second (inactive) path like a Delivery Date.',
        ],
      },
      {
        h: 'Edit or remove a relationship',
        bullets: [
          'Single-click an edge to focus it; press Delete/Backspace to remove it.',
          'Double-click an edge for the properties panel: cardinality, cross-filter (Single/Both), Active checkbox, and Delete.',
          'The audit strip above the canvas counts bidirectional / inactive / isolated; amber numbers deserve a look.',
        ],
      },
      {
        h: 'What this tab does NOT do',
        body: 'The canvas edits relationships only. Tables, columns and measures are created in the Model tree (right-click a table), and their properties live in the Properties view.',
      },
    ],
    tip: 'Dragging from the native Model tree into the canvas is blocked by VS Code itself. Use the in-canvas "Add tables" palette, or the tree right-click "Add to Studio Diagram".',
    seeAlso: [
      { label: 'Trace a field through the model', tab: 'lineage' },
      { label: 'Create a measure or table', hint: 'Model tree → right-click a table → "New Measure…" / view "…" menu → "New Table…"' },
    ],
  },

  advmodels: {
    title: 'Advanced Modelling',
    lead: 'Guided builders for the constructs that otherwise need the tree + property grid + raw DAX. Six areas: Perspectives · Field parameters · Calc groups · Calendars · RLS / OLS · DaxLib.',
    sections: [
      {
        h: 'Perspectives',
        body: 'An objects × perspectives include/exclude checkbox matrix. "+ Perspective" adds one; checking a table cascades to its fields; expand a table row to toggle individual measures and columns.',
      },
      {
        h: 'Field parameters',
        body: 'Pick measures/columns in order ("Add a measure or column" list → reorder with ↑/↓) and "Create". You get a Power-BI-identical field parameter (the slicer that swaps what a visual shows).',
      },
      {
        h: 'Calc groups',
        bullets: [
          '"+ Group" creates the group; "+ Item" adds an item whose DAX wraps SELECTEDMEASURE() (seeded for you).',
          'Set precedence on the group card (higher applies first) and a per-item format string (blank inherits the base measure\'s).',
        ],
      },
      {
        h: 'Calendars (the modern time-intelligence, CL 1701+)',
        bullets: [
          'Templates: Gregorian · Fiscal (pick the FY start month) · ISO · 4-4-5 · 13-Period. "Create calendar" generates missing columns + the TimeUnit mappings in one undoable step.',
          'Below CL 1701 the panel offers "Upgrade to 1701", a ONE-WAY upgrade (older tooling may not open the model). The classic generated date table remains available in the collapsible section below.',
          'Calendar-aware DAX takes the calendar name, e.g. TOTALYTD ( [Sales], \'Fiscal\' ).',
        ],
      },
      {
        h: 'RLS / OLS',
        bullets: [
          '"+ Role" → set the model permission (Read etc.), add members (UPN/group).',
          'Per table: a row-filter DAX input (RLS; blank = all rows) and an OLS select (Default/Read/None); expand a table for column-level OLS.',
          'Row filters are saved as typed. Test them with a live connection before shipping.',
        ],
      },
      {
        h: 'DaxLib',
        body: 'Search daxlib.org (the "app store" for DAX functions), preview a package\'s functions, and Install: one undoable batch that pulls dependencies first. Browse is anonymous and read-only. All free.',
      },
    ],
    seeAlso: [
      { label: 'Author the DAX inside a calc item', tab: 'daxlab', hint: 'prototype in DAX Lab, then paste' },
      { label: 'Create a plain role from the tree', hint: 'Model view "…" menu → "New Role…"' },
    ],
  },

  mcode: {
    title: 'M Code',
    lead: 'Edit the model\'s M (the query language) with a real editor, configure incremental refresh, and apply spreadsheet-style column operations that WRITE M. Two lanes: "Incremental Refresh" and "M query".',
    sections: [
      {
        h: 'M query lane',
        bullets: [
          'Pick a table, then a target: its M partitions or any shared expression/parameter.',
          'The editor autocompletes the M standard library (Ctrl+Space), hovers inferred types, and squiggles problems live. "Format" pretty-prints offline; "Save" writes the metadata (never runs a refresh).',
          '"+ New" creates a shared expression; "Duplicate" and "Reference…" build on an existing one.',
          '"Applied steps" (right rail) reads the let…in… chain: click to jump, ✎ rename, ✕ delete (references re-point automatically).',
        ],
      },
      {
        h: 'Column operations on the preview',
        bullets: [
          'The sample grid is a read-only top-N preview of LOADED data (needs a live connection). Column ops work offline too.',
          'Click a header\'s ⌄ (or right-click it): Remove · Rename… · Change type… · Filter rows… · Replace values… · Sort · Trim & Clean. Table-level: "Remove duplicates", "Keep top N…".',
          'Every op writes a deterministic M step into the editor. It applies at the NEXT refresh; the sample keeps showing loaded data.',
          '"Profile" (live) docks distinct/null counts under each header (one read-only DAX query).',
        ],
      },
      {
        h: 'Incremental refresh',
        bullets: [
          'The Prerequisites checklist verifies CL ≥ 1450, the RangeStart/RangeEnd parameters, and a partition filter on them. Create or update the parameters and add the range filter directly, or let Save policy wire both automatically.',
          'Configure the archive window ("Store rows from the past"), the refresh window, offset, and Import vs Hybrid mode (Hybrid needs CL ≥ 1565). "Save policy" validates the prerequisites first.',
          'This is pure metadata: it configures the policy; it never runs a data refresh.',
        ],
      },
      {
        h: 'Why there is no per-step data preview',
        body: 'There is no redistributable cross-platform M engine, so M is never evaluated step-by-step here. The sample always shows loaded data; your steps take effect at the next refresh.',
      },
    ],
    seeAlso: [
      { label: 'Preview table rows', tab: 'data' },
      { label: 'Refresh a partition', hint: 'Model tree → the partition → "Refresh Partition…" (dry-run, then confirm)' },
    ],
  },

  daxlab: {
    title: 'DAX Lab',
    lead: 'A visual + filter-context lab for understanding, tuning and PROVING DAX against a live engine. Build a visual from field wells, flip it into an editable query, then benchmark, trace and verify below.',
    sections: [
      {
        h: 'Visual mode',
        bullets: [
          'Drag fields from the left rail into Rows / Columns / Values / Filters (or click a field to insert into the editor). The visual auto-runs ~¼s after a change.',
          'Hover any cell/point for its FILTER CONTEXT: every field=value in force, the measure value, and the equivalent CALCULATE(…) expression.',
          'Viz types: Matrix, Table, Clustered bar, Line, Area, Card, Scatter. A filter chip opens a popover (date range/relative, numeric, text, boolean).',
        ],
      },
      {
        h: 'Query mode',
        body: 'Switching Visual → Query copies the generated query in; a visual IS a query. Full DAX editor with model-aware autocomplete; paste long queries from Desktop; "Run" executes (50k row cap).',
      },
      {
        h: 'The workbench (bottom tabs)',
        bullets: [
          'Result: the row grid.',
          'Performance: "Profile" (formula-engine / storage-engine server timings), "Quick" (wall-clock), "Cold / Warm" (cache-cleared vs warm, the honest before/after), "Clear cache". On a shared endpoint you must tick the confirmation; clearing affects all users.',
          'Plan: capture the logical + physical query plan (often unavailable on the Power BI XMLA endpoint; Profile and Cold/Warm are the reliable signals there).',
          'Debug: EVALUATEANDLOG traces; "Log each measure" auto-wraps the Values measures.',
          'Verify: prove a rewrite (Original vs Candidate across the visual\'s row × column × filter matrix). "✓ Equivalent: safe to apply" or a per-context mismatch table.',
        ],
      },
      {
        h: 'What this tab does NOT do',
        body: 'DAX Lab never writes to the model. To apply a verified rewrite, edit the measure (Model tree → click it → Ctrl+S) or route it through a Change Plan.',
      },
    ],
    tip: 'Everything that executes needs a live engine (the header Connect bar). The wells, editor and autocomplete work offline.',
    seeAlso: [
      { label: 'Create / edit the measure itself', hint: 'Model tree → right-click a table → "New Measure…"; click any measure to open its DAX editor' },
      { label: 'Apply fixes as one reviewed batch', tab: 'optimize' },
    ],
  },

  spec: {
    title: 'Model Spec',
    lead: 'The shared, versionable draft behind a new or existing model. Start through the wizard, review it with AI Assistant, edit it by hand, then build when it is ready.',
    sections: [
      {
        h: 'Start with the wizard',
        bullets: [
          'Create a new model from Open Model, then choose a blank draft, a read-only SQL schema draft or a saved Model Spec.',
          'For an existing model, "Autogenerate from model" drafts its current structure without changing it.',
        ],
      },
      {
        h: 'Refine and build',
        bullets: [
          'Inline edits, Edit JSON and AI Assistant all update the same engine-owned draft.',
          'Open spec and Save spec move the JSON artifact in or out of a project or source-control repository.',
          '"Build into model" adds the reviewed objects as one undoable change. It never publishes.',
        ],
      },
      {
        h: 'Reading the spec',
        body: 'Table cards carry role badges (fact/dimension/date/calculated/isolated), key/hidden column badges and summarize-by; relationships show bidi/inactive; the time-intelligence block lists the date table and variants.',
      },
    ],
    seeAlso: [
      { label: 'Review changes before applying them', tab: 'optimize' },
      { label: 'Compare the built model to another', tab: 'compare' },
    ],
  },

  // ---------------------------------------------------------------- Inspect ------
  lineage: {
    title: 'Lineage & Impact',
    lead: 'Trace where a field comes from and what depends on it, then find what\'s genuinely safe to remove. Five modes: Graph · Tree · Impact · Safe to remove · Published reports.',
    sections: [
      {
        h: 'Graph: the force view',
        bullets: [
          'Click a node to PIN it: it lights up with its neighbours and the rest fades; keep clicking connected nodes to walk the chain. "Clear pins" resets.',
          'Search focuses a node; big models open in Focused scope seeded at the busiest measure (depth slider 1–4).',
          'The kind chips (right) are both the colour key and a filter; edge colours: depends-on (green), relationship (yellow), shown-in-a-report (orange).',
        ],
      },
      {
        h: 'Tree: the upstream/downstream view',
        bullets: [
          'Pick a root ("Start from" search, or a node\'s "root" button), then walk ↑ Upstream (what it\'s built from) or ↓ Downstream (what depends on it; the impact direction).',
          'Node cards carry a safe-to-remove verdict dot; fan-out is capped with a "+N more…" expander.',
        ],
      },
      {
        h: 'Impact & Safe to remove',
        bullets: [
          'Impact: pick a measure/column → everything that depends on it, depth-indented, with via-relationship badges. "Nothing depends on this" is qualified "(model-only)" until reports are analyzed.',
          'Safe to remove is a TRI-STATE: green = safe, yellow = used only by an unused object, red = referenced by something we can\'t evaluate offline. Respect the reds.',
        ],
      },
      {
        h: 'Published reports (make the verdicts report-aware)',
        bullets: [
          'Analyze local PBIR folders offline (no sign-in), or discover a cloud workspace\'s reports (choose an auth mode; nothing is called until you click).',
          'Cloud report definitions need a write-capable scope + Contributor even for reading. The consent checkbox spells this out before any call.',
          'After analysis, report-used fields stop being "safe to remove" and the graph/tree gain report → page → visual drill.',
        ],
      },
    ],
    tip: 'The caveat banner tells you when verdicts are model-only. A field can look unused in the model and still drive a service-side report you haven\'t analyzed.',
    seeAlso: [
      { label: 'Delete carefully with a verified playbook', tab: 'workflows', hint: 'the model-hygiene workflow gates every delete' },
      { label: 'See a table\'s size before dropping columns', tab: 'stats' },
    ],
  },

  data: {
    title: 'Data',
    lead: 'Preview live rows of any table. The table list works offline; the row preview itself runs a live top-N query.',
    sections: [
      {
        h: 'Using it',
        bullets: [
          'Connect in the header (a running Power BI Desktop, or "Attach XMLA…"), or use "Open Model…" to open a Desktop/XMLA model that\'s editable and queryable in one step.',
          'Pick a table on the left (colour dots: yellow = date table, green = calculated; dimmed = hidden); choose 200 / 1,000 / 5,000 / 20,000 rows.',
          'The Model tree\'s "Preview Data" right-click lands here with the table pre-selected.',
        ],
      },
      {
        h: 'Caveat',
        body: 'Attaching connects a live engine for QUERIES only. It does not change which model is open in the tree, so make sure the running instance is the same model.',
      },
    ],
    seeAlso: [
      { label: 'Profile distinct/null counts per column', tab: 'mcode', hint: 'M Code → the sample grid\'s "Profile" toggle' },
      { label: 'Run an arbitrary DAX query', tab: 'daxlab' },
    ],
  },

  stats: {
    title: 'Statistics: storage & memory',
    lead: 'Where the model spends memory (the VertiPaq storage view). Offline you get the metadata overview; "Scan storage" against a live engine adds sizes, row counts and encodings.',
    sections: [
      {
        h: 'Reading a scan',
        bullets: [
          'The summary strip: model size, largest table, and the encoding mix (Hash / Value / RLE).',
          'The Storage map treemap: tables → columns, tiles coloured by encoding; click to zoom in, breadcrumb to zoom out. An "(other columns)" tile keeps table areas honest.',
          'The Tables and Columns grids sort by any header; clicking a row reveals the object in the Model tree.',
        ],
      },
      {
        h: 'What to look for',
        bullets: [
          'A huge Hash-encoded column is the classic candidate: high cardinality, expensive dictionary. Consider dropping, splitting, or reducing precision.',
          'On Direct Lake the numbers are RESIDENT-ONLY (what\'s currently paged in). The banner says so; don\'t read them as totals.',
        ],
      },
    ],
    seeAlso: [
      { label: 'Check nothing depends on a column before dropping it', tab: 'lineage' },
      { label: 'Benchmark before/after a change', tab: 'daxlab', hint: 'Performance → Cold / Warm' },
    ],
  },

  // ---------------------------------------------------------------- Improve ------
  readiness: {
    title: 'AI Readiness',
    lead: 'Score how ready the model is for Copilot / Q&A / data agents (A–F, per-category), then fix the gaps: one click for the safe fixes, ready-made AI prompts for the rest.',
    sections: [
      {
        h: 'The score',
        bullets: [
          'Categories only count when they have applicable rules (a dormant category never pads the score).',
          'Hard gates (red banner) cap the grade on the RAW findings (e.g. scale limits), and waivers never lift them, so the grade can\'t be inflated.',
          'The yellow caveat banner flags an incomplete signal (e.g. Direct Lake cardinality is resident-only).',
        ],
      },
      {
        h: 'Fixing findings',
        bullets: [
          '"Apply N safe fixes" applies the deterministic ones in one click (bulk = Pro; each is also applied singly per finding with "Apply").',
          '"Ask AI" on an AI-content finding copies a ready prompt, with the object\'s real context, for your AI Assistant.',
          '"Review as a plan →" moves everything into a Change Plan for a reviewed batch applied in one step. It never overwrites a plan you\'re already building.',
          'Right-click any finding: "Reveal in Model tree" / "Copy reference".',
        ],
      },
      {
        h: 'Waivers: accept a finding honestly',
        bullets: [
          '"Waive" on a finding asks for a REQUIRED reason; it stops docking the score but stays listed under "⊘ Waived (accepted)" with the reason, never hidden.',
          '"Waive rule" accepts every instance model-wide (Pro). "Un-waive" reverses either.',
        ],
      },
      {
        h: 'Prep-for-AI',
        body: 'The Prep-for-AI category reads the model\'s Q&A enablement, verified answers, AI instructions and AI data schema. Your AI Assistant can update the supported settings through the shared session; findings tell you which are missing.',
      },
      {
        h: 'Custom rules: your own checks',
        bullets: [
          'The "Custom rules" panel at the bottom authors your own readiness rules: pick a template, pick a real category, and the form checks the rule against the open model as you type (a test run shows what it would flag, before anything is saved).',
          'Saved rules travel with the model, run with every scan, and their findings are tagged "(custom rule)"; waivers work on them like any finding.',
          'A rule that matches nothing on this model stays dormant (it never pads a category), and a custom rule can never override a built-in one or lift a gate.',
          'Your AI Assistant can preview, save and reset custom readiness rules through the shared session.',
        ],
      },
    ],
    pro: 'Bulk "Apply safe fixes" and rule-level waives are Pro; single-finding fixes and per-instance waives are free.',
    seeAlso: [
      { label: 'Ship the payoff: a Data Agent on this model', tab: 'dataagent' },
      { label: 'Review all fixes as one batch', tab: 'optimize' },
    ],
  },

  bpa: {
    title: 'Best Practice Analyzer',
    lead: 'Tabular-Editor-compatible best-practice rules over the model: auto-fix what\'s deterministic, hand the rest to the AI Assistant, waive what you\'ve decided to accept.',
    sections: [
      {
        h: 'Scanning & fixing',
        bullets: [
          'Scans on open and re-scans automatically on every model change (from either door).',
          'Per finding: "Fix" (deterministic) or "Ask AI" (copies a grounded prompt). "Fix all N auto-fixable" is the Pro bulk batch.',
          '"Review as a plan →" routes the fixes into a Change Plan instead: reviewed, then applied in one step.',
        ],
      },
      {
        h: 'Waivers & interop',
        bullets: [
          'Waive a finding with a required reason (kept visible under "⊘ Waived (accepted)"); "Waive rule" is the model-wide Pro lever.',
          'Rules ignored in Tabular Editor (its ignore annotations) are honoured here, and per-instance waives mirror back so TE3 respects them too.',
          'Waived findings never block the deploy gate; they\'re audited acceptances, and the gate says how many it excluded.',
        ],
      },
      {
        h: 'Custom rules: your own checks',
        bullets: [
          'The "Custom rules" panel at the bottom authors your own rules: pick a template (naming pattern, missing property, an auto-fix), and the form checks the expression against the open model as you type; a test run shows what it would flag before anything is saved.',
          'Saved rules layer on top of the standard set, travel with the model, and their violations are tagged "(custom rule)". Re-using a standard rule id overrides that rule (the preview warns you).',
          'Your AI Assistant can preview, save and reset custom best-practice rules from a file, URL or inline JSON.',
        ],
      },
    ],
    pro: '"Fix all" (bulk) and rule-level waives are Pro; single fixes and per-instance waives are free.',
    seeAlso: [
      { label: 'See the gate these findings feed', tab: 'deploy', hint: 'Advanced → Source Control → "Readiness gate"' },
    ],
  },

  optimize: {
    title: 'Change Plan',
    lead: 'Review a batch of changes before they touch the model, like a pull request: every change is a before → after diff, and the approved set applies as ONE undoable step.',
    sections: [
      {
        h: 'Build a plan',
        bullets: [
          '"Analyse model" proposes a plan (deterministic fixes + an AI-content queue), or arrive via "Review as a plan →" from AI Readiness / BPA.',
          'The AI Assistant can build and edit the same plan; it syncs here live.',
        ],
      },
      {
        h: 'Review every item',
        bullets: [
          'Each row: approve checkbox, risk badge (safe / AI / rename / structural), and the before → after diff (monospace for DAX).',
          'DAX rewrites can carry "verified equivalent ✓", proven against live data before you approve.',
          '"Needs content" items wait for an authored value: type it and "Save", or "Ask AI" copies a grounded authoring prompt.',
          '✕ rejects an item (it dims and won\'t apply).',
        ],
      },
      {
        h: 'Apply',
        bullets: [
          '"Apply approved (N)" runs the whole approved set as one undoable step; one undo reverts it all.',
          '"Apply safe only (N)" restricts to the deterministic items.',
          'The report shows applied / skipped / failed plus the BPA / grade / score movement.',
        ],
      },
    ],
    pro: 'The one-step bulk apply is THE Pro feature. Free applies items one at a time; proposing, reviewing and authoring are all free.',
    seeAlso: [
      { label: 'Where the fixes come from', tab: 'readiness' },
      { label: 'Undo an applied batch', tab: 'history', hint: 'one entry, one undo' },
    ],
  },

  // ---------------------------------------------------------------- Ship ---------
  compare: {
    title: 'Deploy: Review changes',
    lead: 'Review any two models inside Deploy, drill from summary to code, then validate and merge selected changes into the open model or a file.',
    sections: [
      {
        h: 'Diffing',
        bullets: [
          'Open Deploy → Push changes, pick Source and Target (⇄ swaps), then "Review".',
          'Rows group by object type with Create/Update/Delete badges; click through to property-level or side-by-side code diffs.',
          'Most objects are name-matched, so a rename reads as delete + create. Relationships are matched structurally by endpoints.',
        ],
      },
      {
        h: 'Merging (two-step, validated)',
        bullets: [
          'Select changes (tri-state group checkboxes) → "Validate selection" rehearses the apply and flags anything that would fail (usually a missing parent; select it too).',
          '"Merge N → open model" is UNDOABLE (one undo reverts the whole merge). "Apply N → file" writes disk with no in-app undo; git is the safety net.',
          'Only a file or the working copy can be a merge target.',
        ],
      },
    ],
    seeAlso: [
      { label: 'Copy single objects between models', hint: 'the Reference Model view (side bar): Set Reference Model… → right-click "Copy into Open Model" (or Ctrl+C / Ctrl+V)' },
      { label: 'Promote between governed stages instead', tab: 'deploy' },
    ],
  },

  deploy: {
    title: 'Deploy',
    lead: 'One release decision surface: Push changes, Roll back, Promote, or open Advanced delivery and Data Agent tools. Every live write is previewed before its separate confirmation.',
    sections: [
      {
        h: 'Push changes',
        bullets: [
          'The state line names the editing model, target, working-copy change count, drift state and latest restore point. Unknowns stay explicit.',
          'Review changes compares the working copy to the current target by default. Any two supported model sources can still be selected.',
          'Validate selection rehearses the merge. The live write is a separate confirmation over the same engine operation.',
        ],
      },
      {
        h: 'Roll back and Promote',
        bullets: [
          'Roll back reads engine-owned restore points, previews exactly what will be restored, removed or left untouched, then requires Confirm.',
          'Promote: pick source and target stages → Preview (item changes plus readiness gate) → Deploy. A production target requires the human confirmation token from that preview.',
          'A failing gate offers "deploy anyway (override the failing gate)" with a REQUIRED reason, recorded in the Verified Edits audit trail.',
        ],
      },
      {
        h: 'Advanced',
        bullets: [
          'Delivery tools keep local source control, remote git sync, automation scaffolds and direct publish available without crowding the three release decisions.',
          'Data Agent is nested here as an advanced Ship capability. Existing Data Agent links route to this same workspace.',
        ],
      },
      {
        h: 'Deploying the open model to XMLA',
        body: 'The direct metadata deploy lives on the Model view toolbar: the cloud icon "Save to Live Model… (deploy metadata)". A preview change list, then commit; a red gate offers an audited override. Deploy is metadata-only: it never refreshes data.',
      },
    ],
    seeAlso: [
      { label: 'Fix the gate\'s blockers', tab: 'bpa' },
      { label: 'See recorded overrides', tab: 'history', hint: 'the Audit trail panel' },
    ],
  },

  dataagent: {
    title: 'Data Agent',
    lead: 'Deploy a Fabric Data Agent scoped to this model: the payoff of making it AI-ready. Three panels: Scope → Teach → Ship. Every cloud write shows a preview first; "Apply (writes to Fabric)" is always the explicit second step.',
    sections: [
      {
        h: 'Scope',
        bullets: [
          '"+ Add this model" assembles a semantic-model data source from the open model, element by element, honouring the Prep-for-AI data-schema exclusions (✓ included · ✕ excluded).',
          'It builds the config for review; nothing is written until you apply it into the draft.',
        ],
      },
      {
        h: 'Teach',
        bullets: [
          'AI instructions (15,000-char cap, counted live): how the agent should interpret this model, e.g. preferred measures, glossary, qualification rules.',
          'Example question + DAX pairs attach per source. The Fabric portal doesn\'t support them for semantic-model sources yet, so pairs saved here still do not change how the agent answers; the definition format allows them, so they\'re kept ready.',
        ],
      },
      {
        h: 'Ship',
        bullets: [
          '"Publish" creates the read-only copy consumers query; the draft keeps iterating independently.',
          'A published agent IS an MCP server; the "Connect your AI Assistant" card gives you the endpoint to copy.',
          '"Delete agent" removes the Fabric item entirely (dry-run + apply, not undoable).',
        ],
      },
    ],
    pro: 'The one-click "+ Add this model" source generation is Pro; hand-assembling the same source stays free.',
    tip: 'The header\'s sign-in picker chooses who signs in to Fabric; Azure CLI can be logged into a different tenant than the model connection. Pick "Entra (interactive)" and the tenant if the workspaces look wrong. Semanticus never queries the agent or runs inference; connect your own AI Assistant to the published endpoint instead.',
    seeAlso: [
      { label: 'Raise the model\'s readiness first', tab: 'readiness' },
    ],
  },

  docs: {
    title: 'Docs',
    lead: 'Auto-generated, brandable documentation of the model, plus an authored narrative layer that merges in, live over the shared session.',
    sections: [
      {
        h: 'Compose the document',
        bullets: [
          'The Include panel toggles every section: per-table detail, columns, DAX, measures index, relationships (+ diagram), hierarchies, calc groups, KPIs, roles & RLS, sources & lineage, storage, the AI-readiness scorecard, best-practices summary, Prep-for-AI surface, authored narrative, hidden objects.',
          'Branding: title/subtitle/company/author/footer, accent colour, light/dark, a logo (≤512 KB). All choices persist.',
          'Preview as HTML or Markdown; "Print / PDF" opens the print dialog; "Export…" saves to a file.',
        ],
      },
      {
        h: 'The narrative layer',
        bullets: [
          'Pick the model, a table or a measure and write Markdown sections (overview, business context, glossary, …), stored WITH the model, separate from object Descriptions.',
          '"Ask AI" copies a ready documentation prompt; the AI Assistant\'s additions appear live with an attribution chip.',
          'Unsaved drafts auto-save when you switch objects; typing is never silently discarded.',
        ],
      },
    ],
    seeAlso: [
      { label: 'Edit an object\'s Description instead', hint: 'select it in the Model tree → the Properties view (Description gets a multiline editor)' },
    ],
  },

  // ---------------------------------------------------------------- Standalone ---
  history: {
    title: 'Edit History',
    lead: 'Every change this session, yours and the AI Assistant\'s, on one shared, undoable timeline; below it, the persistent, tamper-evident audit trail of verified edits.',
    sections: [
      {
        h: 'The timeline',
        bullets: [
          'Each entry is attributed (You / AI Assistant) with its label and the objects it touched.',
          '"Undo last" / "Redo" step the shared timeline. Hover an entry for "Undo to here (N)": rolls back that edit and everything after it (a linear, Photoshop-style history; no cherry-picking a middle edit).',
          'A batch (a change plan, a fix-all) is ONE entry; one undo reverts it all.',
          'A verdict badge appears only when a Verified Edits record matches the row. No badge just means "wasn\'t checked"; silence is honest.',
        ],
      },
      {
        h: 'The Audit trail (persistent)',
        bullets: [
          'A tamper-evident, append-only record that survives reload: verified edits, overrides (with their recorded reasons), deploys.',
          'The header proves integrity: "intact · N records", or a loud warning naming the exact record where integrity breaks.',
          'Hover a row\'s "Details" for the typed evidence and hashes. "Export MD/JSON" copies the trail (Pro).',
        ],
      },
    ],
    pro: 'Audit-trail export is Pro. The timeline, undo/redo and the trail itself are free.',
    seeAlso: [
      { label: 'Undo from the command palette', hint: '"Semanticus: Undo" / "Semanticus: Redo"' },
      { label: 'Where overrides come from', tab: 'deploy' },
    ],
  },

  workflows: {
    title: 'Workflows',
    lead: 'Playbooks that chain real actions with verified gates: instructions the AI Assistant, or you, follows step by step, where each gate is checked with evidence before a step passes. Built-in playbooks ship with Semanticus; yours are saved beside the model.',
    sections: [
      {
        h: 'The library (left rail)',
        bullets: [
          'Grouped by journey stage: Design · Build · Data · Quality · Security · Ship · Custom. Filter with the search box.',
          '"stock" = built-in and read-only; "Customise…" saves YOUR editable copy, which replaces the built-in one until you delete it (deleting the copy reverts to stock).',
          'An accent dot on a card = it has an enforced gate (starting a run is Pro; READING any playbook is free).',
        ],
      },
      {
        h: 'Running (Run mode)',
        bullets: [
          '"Start run" walks the steps. Each step shows its instructions, the ops to use, and its Gate: questions to answer (some required, declines need a reason and are recorded) plus "Engine will verify" checks.',
          'Verify kinds: dax_probe, dax_equivalence, readiness_rescan, bpa_clean, benchmark_delta. Evidence chips show passed ✓ / failed ✕ / skipped ⤼ (never a silent pass).',
          '"Submit step" enforces the gate: a rejection is shown verbatim and the run stays put. "Skip" and "Abort run" both require recorded reasons.',
          'Runs started by the AI Assistant appear here live: the same run, whether started here or by the assistant.',
        ],
      },
      {
        h: 'Designing (Design mode)',
        bullets: [
          'The designer edits a structured draft and writes clean markdown; the file is the artifact (git-diffable). The file is checked again before saving; a file that fails the check is never saved.',
          'Steps have Actions (op chips picked from the LIVE op catalog, so only real ops can be chained), Instructions (imperative, ~150 words reads best), and an optional Gate (questions + verified checks).',
          'Strictness: hard / warn / off (a workflow default plus per-step overrides); the default is hard.',
        ],
      },
      {
        h: 'The Enforcement toggle (the kill-switch)',
        body: 'The "Enforcement" switch in the library rail turns gate enforcement off MODEL-WIDE, for quick tasks where a full gated run is overkill. Off means every gate is skipped (an amber banner reminds you), runs record no verified evidence, and gated runs start without Pro. It overrides every other strictness setting and is saved beside the model; turn it back on for accountable work.',
      },
      {
        h: 'Why gates matter',
        body: 'A workflow is a set of instructions with teeth: they tell the assistant what to do, and the gate makes it PROVE it did it. Answers are recorded, checks run against the actual model, and the whole run lands on the audit trail.',
      },
    ],
    pro: 'Starting an ENFORCED (gated) workflow run is Pro. Reading playbooks, following them manually, and designing your own are free.',
    seeAlso: [
      { label: 'Learned workflows distilled from your runs', tab: 'knowledge' },
      { label: 'See a run\'s edits on the timeline', tab: 'history' },
    ],
  },

  knowledge: {
    title: 'Knowledge',
    lead: 'Lessons the tool has learned: insights and post-mortems distilled from real sessions, learned workflows, and recall for the open model. Your own data stays in a plain file beside the model; Semanticus only counts and retrieves, while your AI Assistant does the judging.',
    sections: [
      {
        h: 'Insights',
        bullets: [
          'Cards carry kind (insight / post-mortem), scope (this model vs all your models), a "shape" pill when scoped to this model\'s fingerprint, and counters: score (importance; deleted at 0), retrievals, uses.',
          '▲ Upvote / ▼ Downvote tune how important an insight is; Edit fixes its text and match keys; Delete removes it.',
          'A "Pending approval" section appears when auto-approve is off: an entry is saved but held out of recall until you approve it.',
        ],
      },
      {
        h: 'Learned workflows',
        bullets: [
          'After a verified success, the /distill-workflow skill turns the run into a repeatable workflow with provenance.',
          '"Check" verifies the workflow reads correctly and every action it names is real; "Replay check" REHEARSES each step against the model using the saved example answers, with nothing changed: per-step outcomes, and "not admissible" when a rehearsal would fail.',
        ],
      },
      {
        h: 'Recall',
        bullets: [
          'Type what you\'re about to do and "Recall": deterministic retrieval by key overlap + same-shape fingerprint + score + recency; the "why" line explains each hit.',
          'The Model fingerprint card shows the shape recall matches against (tables/measures/grade, fact/dim mix, domain).',
        ],
      },
      {
        h: 'Purge',
        body: 'The safety valve: wipe a whole scope (project or global). A preview count first, then an explicit confirm. A purge marker is appended; the JSONL is never rewritten.',
      },
    ],
    seeAlso: [
      { label: 'Run a learned workflow', tab: 'workflows' },
    ],
  },
};

// ---------------------------------------------------------------------------------------------------
// "Where do I…?" — the task → location index. Much of authoring is NATIVE VS Code (the Model tree,
// the Properties view, the palette), and users look in Studio first — this is the map. Keep entries
// verified against package.json/extension.ts.
// ---------------------------------------------------------------------------------------------------

interface WhereEntry { q: string; a: string; tab?: string }
const WHERE: { group: string; items: WhereEntry[] }[] = [
  {
    group: 'Author (the Model tree: the side bar, not Studio)',
    items: [
      { q: 'Create a measure', a: 'Model tree → right-click a table → "New Measure…". Its DAX editor opens immediately.' },
      { q: 'Edit a measure\'s DAX', a: 'Click the measure in the Model tree; a real DAX editor opens (autocomplete, hover, F12, format). Ctrl+S saves back to the model.' },
      { q: 'Create a table / calculated column / hierarchy', a: 'Model tree → right-click a table ("New Calculated Column…", "New Hierarchy…"); new tables via the Model view "…" menu → "New Table…". Data columns come from the source/M, not the tree.' },
      { q: 'Set a description or display folder', a: 'Select the object in the Model tree → edit it in the Properties view below (Description gets a multiline editor). There is no tree menu item for descriptions.' },
      { q: 'Set a format string', a: 'Right-click the measure → "Set Format String…" (presets + custom), or the FormatString property in the Properties view.' },
      { q: 'Rename or delete', a: 'Right-click → "Rename…" (DAX references are auto-rewritten) / "Delete" (references are NOT rewritten, so check dependents first).' },
      { q: 'Create a relationship', a: 'Diagram tab: drag column → column. Or Model tree: right-click the many-side column → "New Relationship from Column…".', tab: 'diagram' },
      { q: 'Bulk-edit DAX or TMDL as a script', a: 'Right-click object(s) → "Script ▸ DAX/TMDL (editable)": edit the script, then the ▶ "Apply" button in the editor title writes it back as one undoable batch.' },
      { q: 'Edit several objects at once', a: 'Multi-select in the Model tree; the Properties view switches to multi-edit (shared properties; one change applies to all).' },
    ],
  },
  {
    group: 'Build & analyze (Studio)',
    items: [
      { q: 'Field parameters, calc groups, calendars, perspectives, RLS/OLS, DaxLib', a: 'Advanced Modelling: six guided builders (also reachable from the Model view "…" → Advanced Modelling submenu).', tab: 'advmodels' },
      { q: 'Run a DAX query / build a visual / hover filter context', a: 'DAX Lab: Visual or Query mode against a live engine.', tab: 'daxlab' },
      { q: 'Prove a DAX rewrite is equivalent', a: 'DAX Lab → the Verify workbench tab (row × column × filter matrix).', tab: 'daxlab' },
      { q: 'Benchmark cold vs warm', a: 'DAX Lab → Performance → "Cold / Warm".', tab: 'daxlab' },
      { q: 'Edit M / applied steps / incremental refresh', a: 'M Code (or right-click a table → "Edit M Code").', tab: 'mcode' },
      { q: 'Preview table rows', a: 'Data tab (or right-click a table → "Preview Data").', tab: 'data' },
      { q: 'Find what depends on a field / what\'s safe to remove', a: 'Lineage & Impact (or right-click → "Show Lineage & Impact").', tab: 'lineage' },
      { q: 'See where memory goes', a: 'Statistics: "Scan storage" against a live engine.', tab: 'stats' },
      { q: 'Refresh a partition', a: 'Model tree → expand the table → right-click the partition → "Refresh Partition…" (pick a refresh type, dry-run, confirm).' },
    ],
  },
  {
    group: 'Improve & ship',
    items: [
      { q: 'Score & fix AI readiness', a: 'AI Readiness: one-click safe fixes, ready AI prompts for the rest.', tab: 'readiness' },
      { q: 'Best-practice violations', a: 'BPA: fix singly, fix-all (Pro), or route into a Change Plan.', tab: 'bpa' },
      { q: 'Review a batch of changes before applying', a: 'Change Plan: approve per item, apply as one undoable step.', tab: 'optimize' },
      { q: 'Deploy the open model to a live workspace', a: 'Model view toolbar → the cloud icon "Save to Live Model… (deploy metadata)": preview first; a red gate needs an audited override. Pipelines/Git/CI-CD live in the Deploy tab.', tab: 'deploy' },
      { q: 'Diff two models / merge changes', a: 'Deploy → Push changes can review any two supported model sources.', tab: 'deploy' },
      { q: 'Copy objects from another model', a: 'The Reference Model view (side bar): "Set Reference Model…", then right-click → "Copy into Open Model" (or Ctrl+C there, Ctrl+V in the Model tree).' },
      { q: 'Generate documentation', a: 'Docs: compose, brand, print to PDF, plus the authored narrative layer.', tab: 'docs' },
      { q: 'Ship a Fabric Data Agent', a: 'Deploy → Advanced → Data Agent: scope from this model, teach it, publish.', tab: 'dataagent' },
    ],
  },
  {
    group: 'AI Assistant & safety',
    items: [
      { q: 'Connect the AI Assistant to this model', a: 'Command palette → "Semanticus: Connect AI Assistant": writes the workspace connection; the AI Assistant then operates the same live session.' },
      { q: 'See what the AI Assistant changed', a: 'Edit History: every change attributed on one timeline; "Undo to here" rolls back.', tab: 'history' },
      { q: 'Undo / redo', a: 'Edit History buttons, or the palette: "Semanticus: Undo" / "Semanticus: Redo". A batch undoes as one step.', tab: 'history' },
      { q: 'Run a verified playbook', a: 'Workflows: gated steps verified with evidence; free to read, Pro to run enforced.', tab: 'workflows' },
      { q: 'Turn workflow enforcement off for a quick task', a: 'Workflows → the "Enforcement" switch in the library rail. Off means gates are skipped model-wide, with a banner; flip it back for accountable runs.', tab: 'workflows' },
      { q: 'Teach the tool from past sessions', a: 'Knowledge: insights, learned workflows, recall for this model\'s shape.', tab: 'knowledge' },
      { q: 'Accept a finding without hiding it', a: 'Waive it (reason required) on AI Readiness or BPA; it stays surfaced forever under "⊘ Waived (accepted)".', tab: 'readiness' },
    ],
  },
  {
    group: 'Setup & housekeeping',
    items: [
      { q: 'Open or connect a model', a: '"Semanticus: Open Model…" (tree toolbar or palette): a file (.bim/TMDL/PBIP), a running Power BI Desktop, or an XMLA endpoint (recent connections remembered).' },
      { q: 'Search the whole model', a: '"Semanticus: Find in Model" (the search icon on the Model view): names, descriptions and DAX.' },
      { q: 'Save the model to disk', a: 'The Save icon on the Model view (TMDL beside the model).' },
      { q: 'Restart the engine', a: '"Restart Engine (rebuild)" on the Model view toolbar. The MCP door re-attaches automatically on its next call.' },
      { q: 'Activate / inspect a Pro license', a: 'Palette: "Semanticus: Activate License" / "Semanticus: Show License".' },
      { q: 'Format DAX', a: 'In any DAX editor: Format Document (offline), or right-click → "Semanticus: Format DAX with DAX Formatter (online)".' },
    ],
  },
];

// ---------------------------------------------------------------------------------------------------

function SectionBlock({ s }: { s: HelpSection }) {
  return (
    <div className="flex flex-col gap-1">
      <div className="text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-accent)' }}>{s.h}</div>
      {s.body && <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>{s.body}</div>}
      {s.bullets && (
        <ul className="flex flex-col gap-1">
          {s.bullets.map((p, i) => (
            <li key={i} className="text-[12px] flex gap-2" style={{ color: 'var(--sem-muted)' }}>
              <span style={{ color: 'var(--sem-accent)' }}>•</span><span>{p}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function TabGuide({ h, onGo }: { h: TabHelp; onGo?: (tab: string) => void }) {
  return (
    <div className="flex flex-col gap-3.5">
      <div className="text-[12.5px]" style={{ color: 'var(--sem-fg)' }}>{h.lead}</div>
      {h.sections.map((s, i) => <SectionBlock key={i} s={s} />)}
      {h.pro && (
        <div className="text-[11px] rounded-md px-2.5 py-2" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>
          <span className="font-semibold" style={{ color: 'var(--sem-accent)' }}>Pro · </span>{h.pro}
        </div>
      )}
      {h.tip && <div className="text-[11px] rounded-md px-2.5 py-2" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>{h.tip}</div>}
      {h.seeAlso && h.seeAlso.length > 0 && (
        <div className="flex flex-col gap-1">
          <div className="text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>Looking for…</div>
          {h.seeAlso.map((s, i) => (
            <div key={i} className="text-[12px] flex gap-2" style={{ color: 'var(--sem-muted)' }}>
              <span style={{ color: 'var(--sem-accent)' }}>→</span>
              <span>
                {s.tab && onGo
                  ? <button className="underline" style={{ color: 'var(--sem-accent)' }} onClick={() => onGo(s.tab!)}>{s.label}</button>
                  : <span style={{ color: 'var(--sem-fg)' }}>{s.label}</span>}
                {s.hint ? <span>: {s.hint}</span> : null}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function WhereIndex({ onGo }: { onGo?: (tab: string) => void }) {
  const [q, setQ] = useState('');
  const groups = useMemo(() => {
    const needle = q.trim().toLowerCase();
    if (!needle) return WHERE;
    return WHERE
      .map((g) => ({ ...g, items: g.items.filter((it) => (it.q + ' ' + it.a).toLowerCase().includes(needle)) }))
      .filter((g) => g.items.length > 0);
  }, [q]);
  return (
    <div className="flex flex-col gap-3">
      <input
        value={q} onChange={(e) => setQ(e.target.value)} placeholder="Filter tasks… e.g. measure, deploy, undo"
        className="w-full rounded-md px-2.5 py-1.5 text-[12px] outline-none"
        style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}
      />
      {groups.length === 0 && <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No tasks match “{q}”.</div>}
      {groups.map((g) => (
        <div key={g.group} className="flex flex-col gap-1.5">
          <div className="text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-accent)' }}>{g.group}</div>
          {g.items.map((it, i) => (
            <div key={i} className="flex flex-col gap-0.5 rounded-md px-2 py-1.5" style={{ background: 'var(--sem-surface-2)' }}>
              <div className="text-[12px] font-medium" style={{ color: 'var(--sem-fg)' }}>
                {it.tab && onGo
                  ? <button className="underline text-left" style={{ color: 'var(--sem-fg)' }} onClick={() => onGo(it.tab!)}>{it.q}</button>
                  : it.q}
              </div>
              <div className="text-[11.5px]" style={{ color: 'var(--sem-muted)' }}>{it.a}</div>
            </div>
          ))}
        </div>
      ))}
    </div>
  );
}

export function HelpButton({ tab, onGo, onShortcuts }: { tab: string; onGo?: (tab: string) => void; onShortcuts?: () => void }) {
  const [open, setOpen] = useState(false);
  const [view, setView] = useState<'tab' | 'where'>('tab');
  const h = HELP[tab];
  const go = onGo ? (t: string) => { setOpen(false); onGo(t); } : undefined;
  // Esc closes the slide-over (part of the keyboard suite — every Studio overlay closes on Escape).
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); setOpen(false); } };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [open]);
  return (
    <>
      <button onClick={() => { setView('tab'); setOpen(true); }} title={h ? `Guide: the ${h.title} tab (and “Where do I…?”)` : 'Help'} aria-label="Help"
        className="w-6 h-6 rounded-full flex items-center justify-center text-[12px] font-semibold shrink-0"
        style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)', border: '1px solid var(--sem-border)' }}>?</button>
      {open && (
        <div className="fixed inset-0 z-50" onClick={() => setOpen(false)} style={{ background: 'rgba(0,0,0,0.35)' }}>
          <div onClick={(e) => e.stopPropagation()} className="absolute top-0 right-0 h-full flex flex-col"
            style={{ width: 460, maxWidth: '92vw', background: 'var(--sem-surface)', borderLeft: '1px solid var(--sem-border)', boxShadow: '-8px 0 24px rgba(0,0,0,0.4)' }}>
            <div className="flex items-center gap-2 px-4 py-3 border-b" style={{ borderColor: 'var(--sem-border)' }}>
              <span className="text-[10px] uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>guide</span>
              <span className="text-[14px] font-semibold flex-1">{view === 'tab' ? (h?.title ?? 'Help') : 'Where do I…?'}</span>
              <div className="flex rounded-md overflow-hidden" style={{ border: '1px solid var(--sem-border)' }}>
                <button onClick={() => setView('tab')} className="px-2 py-0.5 text-[11px]"
                  style={{ background: view === 'tab' ? 'var(--sem-surface-2)' : 'transparent', color: view === 'tab' ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>This tab</button>
                <button onClick={() => setView('where')} className="px-2 py-0.5 text-[11px]"
                  style={{ background: view === 'where' ? 'var(--sem-surface-2)' : 'transparent', color: view === 'where' ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>Where do I…?</button>
              </div>
              <button onClick={() => setOpen(false)} aria-label="Close help" className="text-[14px]" style={{ color: 'var(--sem-muted)' }}>✕</button>
            </div>
            <div className="flex-1 overflow-auto px-4 py-3">
              {view === 'tab'
                ? (h ? <TabGuide h={h} onGo={go} /> : <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No guide for this view yet. Try “Where do I…?”.</div>)
                : <WhereIndex onGo={go} />}
              <div className="text-[10px] mt-3 pt-2 border-t flex items-center gap-2" style={{ color: 'var(--sem-muted)', borderColor: 'var(--sem-border)' }}>
                <span>Your AI Assistant can do everything here too, on the same live model.</span>
                {onShortcuts && (
                  <button className="ml-auto underline whitespace-nowrap" style={{ color: 'var(--sem-accent)' }}
                    onClick={() => { setOpen(false); onShortcuts(); }}>Keyboard shortcuts (?)</button>
                )}
              </div>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
