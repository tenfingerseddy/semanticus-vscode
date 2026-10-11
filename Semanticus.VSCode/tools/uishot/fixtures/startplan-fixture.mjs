// A mock engine for the start plan (lane sf-studio, L12): the StartPlanView JSON the contract fixes
// (Semanticus.Engine/StartPlan/StartPlanContract.cs, camelCased on the wire), plus a small stateful mock of the verbs
// the page calls, so a driver and the model tests can press real buttons against it.
//
// WHAT IT MODELS, and nothing more: the digest moves on any edit or any assistant words (S1); an approval is bound to the
// digest it was made on and is void the moment the digest moves; Build runs only on an approved, current digest; the
// person-only approve exists apart from the build. It is a MOCK of the engine's rules, not the engine: lane sf-doors
// wires the real doors and the join re-runs everything against the real one.
//
// The data is the mockup's Contoso Consulting source (option-a.html): seven tables, 53 columns, the Rates question.
//
// THE WIRING STEP'S FIELDS (2 Oct): every line carries `held` (plain words while a hand-made line's object is out of the
// plan, else null); the view carries `built`, `sampleFacts`, `sample`, `jevDeclined` and the grades as letters with
// `afterBuildProvisional`. Leaving Codes out holds your assistant's words on it; putting it back releases them. "Not now"
// on the grant is an answer (target grant), and the plan goes on the way a free plan does until the person allows Jev.
//
// THE REAL ENGINE'S RUN (2 Oct, sf-wiring-real-drive): the grant needs the MODEL's own permission first and is refused
// without it, word for word as LocalEngine.GrantStartDisclosureAsync refuses (startPlanConsent / startPlanSetConsent play
// getConsent / setConsent); the Free plan holds no line Jev would have decided (the real one has none, so none is marked
// Default); the fiscal-year question is the engine's own shape (id and kind fiscal_year, options a year end and calendar).
//
// This file is also INLINED into the browser by the driver (module keywords stripped), so it must stay one plain
// script: no imports, no top-level await.

export const SOURCE = { server: 'contoso.datawarehouse.fabric.microsoft.com', database: 'ContosoOps', authMode: 'azcli', tenantId: '', storageMode: 'import' };
export const DEST = { folder: '/home/kane/models/Contoso Consulting', name: 'Contoso Consulting', modelPath: '/home/kane/models/Contoso Consulting/Contoso Consulting.SemanticModel' };

const TABLES = [
  ['Clients', 'dim_client'], ['Projects', 'dim_project'], ['Staff', 'dim_staff'], ['Timesheets', 'fact_timesheet'],
  ['Invoices', 'fact_invoice'], ['Date', 'dim_date'], ['Codes', 'ref_codes'],
];

function hash(text) {
  let h = 2166136261;
  for (let i = 0; i < text.length; i++) { h ^= text.charCodeAt(i); h = Math.imul(h, 16777619) >>> 0; }
  return h.toString(16).padStart(8, '0');
}

// One line of the plan: the ChangeItem fields the page reads, who decided it, and whether it is kept.
function line(id, section, group, by, title, why, o = {}) {
  return {
    section, group,
    item: { id, objectRef: o.ref || ('model:' + id), objectName: o.name || title, kind: o.kind || 'set_property', title, rationale: why, before: o.before ?? null, after: o.after ?? null, status: 'proposed', risk: 'safe' },
    decidedBy: by, point: o.point || null, word: o.word || null, round: o.round ?? 1, kept: o.kept !== false, why, held: null, confidence: o.confidence, ...(o.derived ? { derived: true } : {}), ...(o.nameFlag ? { nameFlag: o.nameFlag } : {}),
  };
}

// The plain words the engine gives for a name the naming rules would still flag (ReadinessRuleSet.NamingRuleWhy).
const FLAG_WHY = 'The name would still read as a code or an abbreviation: an underscore, words run together, a name that ends in Key, ID or Cd, or capital letters that are not a known acronym.';

function seedLines() {
  const L = [];
  // ---- Tables and columns
  for (const [name, src, cols] of [['Clients', 'dim_client', 6], ['Projects', 'dim_project', 9], ['Staff', 'dim_staff', 8], ['Timesheets', 'fact_timesheet', 8], ['Invoices', 'fact_invoice', 9], ['Date', 'dim_date', 8], ['Codes', 'ref_codes', 5]])
    L.push(line('t-in-' + name, 'tables', name, 'rule', `Bring in ${name} (${cols} of ${cols} columns)`, 'It is in the source.', { ref: 'table:' + name, kind: 'include_table', ...(name === 'Staff' ? { nameFlag: FLAG_WHY } : {}) }));
  L.push(line('t-all', 'tables', '', 'person', 'Build all 7 tables you read', 'Only you decide what goes in. Everything you read is in the draft until you leave something out.', { after: 'All 7 tables' }));
  L.push(line('t-cols', 'tables', '', 'person', 'Keep every column', 'Only you decide which columns go in. Best practice below names ones you may not want.', { after: 'Keep all 53' }));
  L.push(line('t-types', 'tables', '', 'rule', 'Data types come straight from SQL', 'Each column keeps the type SQL gave it: text, whole number, decimal or date.'));
  L.push(line('t-cats', 'tables', '', 'rule', 'No data categories needed', 'No column holds a place, a web link or an image, so none gets a category.'));
  L.push(line('t-hide-ts', 'tables', 'Timesheets', 'rule', 'Hide 3 key columns', 'A column at either end of a link is a key. It joins tables, never totals, and stays out of sight.', { after: 'ProjectKey, StaffKey, DateKey' }));
  L.push(line('t-hide-inv', 'tables', 'Invoices', 'rule', 'Hide 3 key columns', 'A column at either end of a link is a key. It joins tables, never totals, and stays out of sight.', { after: 'ProjectKey, ClientKey, DateKey' }));
  L.push(line('t-hide-oth', 'tables', 'Projects', 'rule', 'Hide 2 key columns', 'A column at either end of a link is a key. It joins tables, never totals, and stays out of sight.', { after: 'ProjectKey, ClientKey' }));
  L.push(line('t-sum-hours', 'tables', 'Timesheets', 'jev', 'Hours add up', 'Each row is one piece of work, so adding rows gives a real total.', { after: 'Add up', point: 'column.summarize', confidence: 91, ref: 'column:Timesheets/Hours', name: 'Hours' }));
  L.push(line('t-sum-rate', 'tables', 'Timesheets', 'jev', 'Rates never add up', 'Adding $270 and $140 an hour gives a number with no meaning. So the starter rule makes no Total rate.', { after: 'No total', point: 'column.summarize', confidence: 93, ref: 'column:Timesheets/Rate', name: 'Rate' }));
  L.push(line('t-sum-date', 'tables', 'Date', 'jev', 'Date and year numbers never add up', 'yyyymmdd and year numbers label time. Adding 2025 and 2026 means nothing.', { after: 'No total', point: 'column.summarize', confidence: 98 }));
  L.push(line('t-sum-budget', 'tables', 'Projects', 'jev', 'How project budgets total', 'Jev was not sure. Budgets add up across projects, but repeat next to timesheet lines.', { after: 'Add up', point: 'column.summarize', kept: false, confidence: 55 }));
  L.push(line('t-sum-money', 'tables', 'Invoices', 'jev', 'Money shows in Australian dollars', 'Every invoice says AUD. Money shows as $1,234.56.', { after: '$ AUD', point: 'column.format', confidence: 92 }));
  L.push(line('t-fmt-hours', 'tables', 'Timesheets', 'jev', 'Hours with one decimal, dates as 1 Jul 2024', 'Hours come in quarter and half hours. Dates read best as day, month, year.', { after: 'As shown', point: 'column.format', confidence: 90 }));
  // ---- Links and dates (the engine's shape: one group with no table, a link named by identity, titled "A to B on Col", Kept = on)
  const link = (id, title, why, on, extra = {}) => L.push(line(id, 'links', '', 'rule', title, why, { ref: 'relationship:' + id, name: id, kind: 'create_relationship', kept: on, ...extra }));
  link('r-ts-pr', 'Timesheets to Projects on ProjectKey', 'Every key finds its match: all 4,200 timesheet lines.', true);
  link('r-ts-st', 'Timesheets to Staff on StaffKey', 'Every key finds its match: all 4,200 timesheet lines.', true);
  link('r-ts-dt', 'Timesheets to Date on WorkDate', 'Timesheets have one date. That link can be live.', true);
  link('r-pr-cl', 'Projects to Clients on ClientKey', 'Every project finds its client.', true);
  link('r-inv-pr', 'Invoices to Projects on ProjectKey', 'Every key finds its match: all 615 invoices.', true);
  link('r-inv-dt', 'Invoices to Date on IssueDate', 'Invoices have three dates. Only one link to Date can be live.', true);
  link('r-inv-cl', 'Invoices to Clients on ClientKey (off)', 'Invoices already reach Clients through Projects. Two live paths would make totals unclear.', false);
  link('r-inv-dt2', 'Invoices to Date on DueDate (off)', 'Invoices have three dates. Only one link to Date can be live until a measure asks.', false);
  L.push(line('l-date', 'links', '', 'rule', 'Mark Date as the date table on Date', 'One row per day from 1 Jul 2024 to 30 Jun 2026, with no gaps.', { kind: 'mark_date_table', ref: 'table:Date' }));
  L.push(line('l-ytd', 'links', '', 'rule', 'Year to date counts from 1 January', 'The rule uses the calendar year unless you change it. This source has a fiscal year that ends on 30 June.', { after: 'calendar' }));
  L.push(line('l-codes', 'links', '', 'rule', 'Codes has no link', 'One table holds six code lists, so no single column matches it. It is built on its own.', { after: 'Not linked' }));
  // ---- Measures
  L.push(line('m-hours', 'measures', 'Timesheets', 'rule', 'Total hours: sum of Hours', 'A total people can use.', { ref: 'measure:Timesheets/Total hours', kind: 'create_measure' }));
  L.push(line('m-hours-ytd', 'measures', 'Timesheets', 'rule', 'Hours YTD, Hours last year', 'TOTALYTD([Total hours], \'Date\'[Date])', { kind: 'create_measure', derived: true, after: 'TOTALYTD([Total hours], \'Date\'[Date])' }));
  L.push(line('m-amount', 'measures', 'Invoices', 'rule', 'Total amount: sum of Amount', 'A total people can use.', { ref: 'measure:Invoices/Total amount', kind: 'create_measure' }));
  L.push(line('m-tax', 'measures', 'Invoices', 'rule', 'Total tax: sum of Tax', 'A total people can use.', { ref: 'measure:Invoices/Total tax', kind: 'create_measure', nameFlag: FLAG_WHY }));
  L.push(line('m-amount-ytd', 'measures', 'Invoices', 'rule', 'Amount YTD, Amount last year', 'TOTALYTD([Total amount], \'Date\'[Date])', { kind: 'create_measure', derived: true, after: 'TOTALYTD([Total amount], \'Date\'[Date])' }));
  L.push(line('m-bud-hours', 'measures', 'Projects', 'rule', 'Total budget hours: sum of Budget hours', 'A total people can use.', { ref: 'measure:Projects/Total budget hours', kind: 'create_measure' }));
  L.push(line('m-bud-amount', 'measures', 'Projects', 'rule', 'Total budget amount: sum of Budget amount', 'A total people can use.', { ref: 'measure:Projects/Total budget amount', kind: 'create_measure' }));
  L.push(line('m-folders', 'measures', '', 'jev', 'Measures in 3 folders: Hours, Money, Time', 'People look for hours, money and time measures in different places.', { after: '3 folders', point: 'measure.folder', confidence: 87 }));
  L.push(line('m-formats', 'measures', '', 'jev', 'Measure formats: hours #,0.0, money $ AUD', 'A measure shows like the column it totals.', { after: 'As shown', point: 'measure.format', confidence: 91 }));
  // ---- Words for Copilot
  L.push(line('w-names', 'words', '', 'assistant', 'Business names for 7 tables and 40 fields', 'Timesheets, Staff member, Billable. Raw names stay in the source.', { word: 'written' }));
  L.push(line('w-desc', 'words', '', 'assistant', 'Descriptions for every field and measure', '"Invoice amount before tax. Includes the 24 voided invoices." Copilot reads these to pick the right field.', { word: 'written' }));
  L.push(line('w-syn', 'words', '', 'assistant', '38 synonyms', 'Clients: customer, account. Staff: consultant, employee. Amount: revenue, billings.', { word: 'written' }));
  L.push(line('w-desc-staff', 'words', 'Staff', 'assistant', 'Description of Staff', 'Written by your assistant.', { word: 'People who work at the firm.', after: 'People who work at the firm.', kind: 'set_description', ref: 'table:Staff', name: 'Staff' }));
  L.push(line('w-codes', 'words', 'Codes', 'assistant', 'Descriptions for the 5 Codes columns', 'Each code list says what its codes mean, so Copilot can explain a code.', { word: 'written', ref: 'table:Codes', name: 'Codes' }));
  L.push(line('w-instr', 'words', '', 'assistant', 'Instructions for Copilot', 'Money is in Australian dollars. Leave voided invoices out of revenue. Code columns are explained in Codes.', { word: 'written' }));
  L.push(line('w-cop', 'words', '', 'jev', 'Copilot reads the fields people use, not IDs and date numbers', 'Line IDs, date numbers and a column with one value only confuse answers. Copilot reads the rest.', { after: 'As shown', point: 'copilot.visible', confidence: 89 }));
  L.push(line('w-cop-rate', 'words', 'Staff', 'jev', 'Staff rates and Copilot', 'Jev was not sure. Rates look like pay.', { after: 'Copilot can read them', point: 'copilot.visible', kept: false, confidence: 61 }));
  L.push(line('w-cop-left', 'words', 'Staff', 'jev', 'Who left the firm, and Copilot', 'Jev was not sure. Some firms treat it as private.', { after: 'Copilot can read it', point: 'copilot.visible', kept: false, confidence: 52 }));
  // ---- Best practice
  L.push(line('b-safe', 'bestpractice', '', 'rule', '9 safe fixes go in with the build', 'Keys never total, the date table is marked, each measure gets a format. Model quality counts these as fixed.', { after: '9 fixes' }));
  L.push(line('b-trail', 'bestpractice', 'Invoices', 'rule', 'The export trail differs on every row', 'Stays open. A text column with a new value on every row is the biggest thing in memory. Leave it out in Tables and columns, or accept it with a reason.', { kept: false, name: 'ExportTrail' }));
  L.push(line('b-codes', 'bestpractice', 'Codes', 'rule', 'Codes is not linked to anything', 'Stays open. A table with no links cannot filter anything. Your assistant described each code column instead. Leave it out, or accept it with a reason.', { kept: false }));
  return L;
}

function needsOf(state) {
  const n = [];
  if (state.sample === 'reading') n.push({ id: 'sample', kind: 'sample_reading', text: 'Reading a sample of the rows in ContosoOps, so counts and patterns can guide the choices. Rows never leave your computer. The plan will update when it is done.', options: [], recommended: null });
  if (state.sample === 'refused') n.push({ id: 'sample', kind: 'sample_refused', text: 'A sample of the rows was not read: the sign-in for this source has expired. The plan is made from the table and column names alone, so some choices may be less sure.', options: [], recommended: null });
  if (!state.free && state.jevDeclined) {
    n.push({ id: 'grant', kind: 'grant_declined', text: 'You chose to carry on without Jev. The plan is made the way a free plan is, with your assistant\'s words. You can allow Jev later.', options: ['Allow'], recommended: 'Allow' });
  } else if (!state.free) {
    if (state.granted === false) {
      n.push({ id: 'grant', kind: 'grant', text: 'Jev needs your permission to see the tables of ContosoOps on contoso.datawarehouse.fabric.microsoft.com before it decides anything. It reads names and types, and counts and patterns from a sample of up to 20,000 rows a table. It never sees rows.', options: ['Allow', 'Not now'], recommended: 'Allow' });
    } else if (!state.answers['need-budgets']) {
      n.push({ id: 'need-budgets', kind: 'jev', text: 'Should project budgets add up? Budget hours and Budget amount sit on 180 project rows. Next to timesheets they repeat on every line. Adding a budget across projects is right; adding it across timesheet lines counts it many times. Your answer also decides whether the rules make Total budget hours and Total budget amount.', options: ['No total', 'Add up'], recommended: 'No total' });
    }
    if (state.granted !== false) {
      if (!state.answers['need-rates']) n.push({ id: 'need-rates', kind: 'jev', text: 'Can Copilot read out staff rates? Standard rate runs from $140 to $490 an hour across 80 people. Rates look like pay. They stay in the model either way; this only decides what Copilot can say.', options: ['Hide from Copilot', 'Copilot can read them'], recommended: 'Hide from Copilot' });
      if (!state.answers['need-left']) n.push({ id: 'need-left', kind: 'jev', text: 'Can Copilot say who has left the firm? 3 of 80 staff are marked as left. Useful for questions about headcount, but some firms treat it as private.', options: ['Copilot can read it', 'Hide from Copilot'], recommended: 'Copilot can read it' });
    }
  }
  // A pick the rule made because Jev was not sure (StartPlanDecide.NeedsYou): the plan USES it and the build makes it, so the band must not say it stays out (UAT-J7-S2-004).
  if (state.pickNeed && !state.free && state.granted !== false) n.push({ id: 'unsure_pick:b-rate', kind: 'unsure_pick', text: 'Jev was not sure how to add up Rate in Staff, so the plan uses average. Is that right?', options: ['average', 'none', 'sum'], recommended: 'average' });
  if (!state.answers.fiscal_year) n.push({ id: 'fiscal_year', kind: 'fiscal_year', text: 'The date data suggests a financial year that ends on 30 June. Year to date uses the calendar year until you say.', options: ['6/30', 'calendar'], recommended: '6/30' });
  n.push({ id: 'composite:staff-rates', kind: 'composite_key', text: 'Staff and Rates join on two columns together (StaffKey and RateYear). A link needs one column, so this link is not built. It would need a combined key.', options: [], recommended: null });
  return n;
}

const SEC = {
  tables: ['Tables and columns', 'What is built, keys, totals, formats'],
  links: ['Links and dates', 'Each link on or off, the date table, year to date'],
  measures: ['Measures', 'Starter totals, time measures, folders and formats'],
  words: ['Words for Copilot', 'Names, descriptions, synonyms, instructions, what Copilot sees'],
  bestpractice: ['Best practice', 'Fixes made in the build, and what stays open'],
};

export function makeStartPlanState(o = {}) {
  const free = !!o.free;
  const state = {
    free, lines: seedLines(), answers: {}, pins: {}, words: { total: 144, open: o.wordsOpen ?? 0 },
    granted: free ? true : o.granted !== false, decide: o.decide || (free ? 'off' : 'stable'), round: 2,
    jevDeclined: false, sample: o.sample || 'read', provisional: o.provisional ?? null, pickNeed: !!o.pickNeed,
    // The MODEL's own permission (getConsent): a new Pro model has none until the person gives it.
    consent: !!o.consent,
    approval: null, built: false, edits: 0, revision: 0, parked: false, kept: [],
    progress: o.progress || [], reason: o.reason || null,
  };
  // Links guessed from names (uat7-links): the lines the engine gives a source that declares no key. The same ids the engine gives them: "relationship:" and the link's identity.
  if (o.guessedLinks) state.lines.push(...guessedLinkLines());
  // Free: the real engine gives no line Jev would have decided (sf-wiring-real-drive: "0 on plain defaults"), so none is here.
  if (free) state.lines = state.lines.filter((l) => l.decidedBy !== 'jev');
  return state;
}

// One guessed link per state it can be in: on with the rows read, on with names alone, off because another table answers to the same column, and left out by the person.
function guessedLinkLines() {
  const guess = 'A guess from the names: the source declares no key for these two tables. Drop it if it is wrong.';
  const g = (id, title, why, o = {}) => ({ ...line('relationship:' + id, 'links', '', o.by || 'rule', title, why, { ref: 'relationship:' + id, name: id, kind: 'create_relationship', kept: o.kept !== false }), guessed: true });
  return [
    g('g-staff', 'Linked Timesheets[StaffKey] to Staff[StaffKey]: same name, and StaffKey is unique in Staff (from the sample)', guess),
    g('g-client', 'Linked Invoices[ClientKey] to Clients[ClientKey]: same name, and ClientKey is named like the key of Clients (rows not read)', guess + ' Invoices[ClientKey] also matches ClientArchive, which is off.'),
    g('g-archive', 'Linked Invoices[ClientKey] to ClientArchive[ClientKey]: same name, and ClientKey is unique in ClientArchive (from the sample) (off)',
      guess + ' Off because Invoices[ClientKey] also matches Clients, and the link to Clients was kept on instead (ClientKey is named like the key of Clients). Turn this one on instead if it is the right one. A measure can still use it with USERELATIONSHIP.', { kept: false }),
    g('g-codes', 'Linked Projects[CodeKey] to Codes[CodeKey]: same name, and CodeKey is unique in Codes (from the sample)', 'A guessed link, left out by you. It is not built. Keep it to bring it back.', { by: 'person', kept: false }),
  ];
}

function digestOf(state) {
  return hash(JSON.stringify({ l: liveLines(state).map((l) => [l.item.id, l.kept, l.item.after, l.decidedBy, l.word, l.held]), a: state.answers, p: state.pins, w: state.words, g: state.granted, d: state.jevDeclined, s: state.sample === 'read', r: state.sampleRefused ?? [] }));
}

function countsOf(lines, free) {
  const c = { rule: 0, jev: 0, assistant: 0, you: 0 };
  for (const l of lines) { if (!l.kept && l.decidedBy === 'jev') continue; if (l.decidedBy === 'person') c.you++; else c[l.decidedBy]++; }
  void free; return c;
}

// Before the person lets Jev look (or after they said Not now), Jev has decided nothing: its lines do not exist yet.
const liveLines = (state) => state.lines.filter((l) => (state.granted !== false && !state.jevDeclined) || l.decidedBy !== 'jev');

// The grades the preview made: letters, and "after the build" provisional until the decide has settled.
function gradesOf(state) {
  const settled = state.decide === 'stable' || state.decide === 'off';
  return {
    now: 35, nowGrade: 'D', nowBestPractice: null,
    afterBuild: state.free || state.jevDeclined ? 78 : 92, afterBuildGrade: state.free || state.jevDeclined ? 'C' : 'A', afterBuildBestPractice: 'B', afterBuildBestPracticeOpen: 2,
    afterBuildProvisional: state.provisional ?? !settled,
  };
}

// The view as it arrives over the wire: a fresh copy every time. Handing out the state's own line objects let a later change to
// the state rewrite a view the page already holds, so a diff between two reads saw nothing move (the Changed filter, 2 Oct).
export function startPlanView(state) {
  const v = JSON.parse(JSON.stringify(viewOf(state)));
  // A kept name is not flagged any more (StartPlanViewBuilder.NameFlagOf), and the Staff table lists its columns, one of them with a name the rules flag.
  for (const sec of v.sections) for (const g of sec.groups) {
    for (const l of g.lines) if (l.nameFlag && state.kept.includes(l.item.objectRef)) l.nameFlag = null;
    if (sec.id === 'tables' && g.table === 'Staff') g.columns = ['EMP_NM', 'Grade', 'Rate'].map((n) => ({ ref: 'column:Staff/' + n, name: n, dataType: n === 'Rate' ? 'Decimal' : 'String', included: true, hidden: false, key: false, ...(n === 'EMP_NM' && !state.kept.includes('column:Staff/EMP_NM') ? { nameFlag: FLAG_WHY } : {}) }));
  }
  return v;
}

// set_table_sample off: the person (or the assistant) refused one table's sample. The engine puts it on that table's group (StartPlanViewBuilder.TableGroups).
const sec_sampleOff = (state, sectionId, group) => sectionId === 'tables' && !!group && (state.sampleRefused ?? []).includes(group);

function viewOf(state) {
  const sections = Object.keys(SEC).map((id) => {
    const inSection = liveLines(state).filter((l) => l.section === id);
    const names = [...new Set(inSection.map((l) => l.group))];
    return {
      id, title: SEC[id][0], summary: SEC[id][1], counts: countsOf(inSection, state.free),
      groups: names.map((g) => ({ table: g || null, sourceName: (TABLES.find((t) => t[0] === g) || [])[1] || null, ...(sec_sampleOff(state, id, g) ? { sampleOff: true } : {}), lines: inSection.filter((l) => l.group === g).map(({ section, group, ...rest }) => rest) })),
    };
  });
  const digest = digestOf(state);
  const deciding = state.decide === 'deciding' || (state.decide === 'waiting_grant' && !state.jevDeclined);
  return {
    exists: !state.discarded && !state.parked, id: 'sp-1', destination: DEST, source: SOURCE, sections, built: !!state.built,
    sampleFacts: state.sample === 'read', jevDeclined: !!state.jevDeclined,
    sample: { state: state.sample, reason: state.sample === 'refused' ? 'the sign-in for this source has expired' : null, measured: state.sample === 'read' ? 7 : 0, notMeasured: 0 },
    needsYou: needsOf(state),
    decide: { state: state.decide, round: state.round, reason: state.reason, progress: state.progress },
    words: { total: state.words.total, open: state.words.open },
    grades: gradesOf(state),
    // As StartPlanViewBuilder.Gate: CanBuild is true only once the approval matches the digest and nothing else blocks the build.
    digest, approval: state.approval, canBuild: !state.built && !deciding && !!state.approval && state.approval.digest === digest,
    blockedBecause: state.built ? 'This plan was built into Contoso Consulting. Undo the build to change the plan and build it again.'
      : state.decide === 'deciding' ? 'Jev is still working on the plan. It will be ready to approve when it stops.'
        : state.decide === 'waiting_grant' ? 'Jev is waiting for your answer to the first question.'
          : !state.approval ? 'You have not approved this plan yet.'
            : state.approval.digest !== digest ? 'The plan changed after you approved it. Look it over and approve it again.' : null,
  };
}

// The verbs. Each answers with the view (or the build result) the way the contract's verbs do.
export function startPlanEdit(state, edit) {
  const { op, target, value } = edit;
  state.edits++;
  const find = (id) => state.lines.find((l) => l.item.id === id);
  if (op === 'answer' && target === 'grant') {
    // "Not now": the person carries on without Jev. Nothing is asked of Jev, and the plan goes on the way a free plan does.
    if (value === 'Not now') { state.jevDeclined = true; state.decide = 'off'; }
  } else if (op === 'answer') {
    state.answers[target] = value;
    if (target === 'need-budgets') {
      const j = find('t-sum-budget'); j.kept = true; j.item.after = value; j.confidence = undefined;
      const addUp = value === 'Add up';
      for (const id of ['m-bud-hours', 'm-bud-amount']) { const m = find(id); if (m) m.kept = addUp; if (m && !addUp) m.why = 'Left out: you said project budgets do not add up.'; }
    }
    if (target === 'need-rates') { const j = find('w-cop-rate'); j.kept = true; j.decidedBy = 'person'; j.item.after = value; }
    if (target === 'need-left') { const j = find('w-cop-left'); j.kept = true; j.decidedBy = 'person'; j.item.after = value; }
    if (target === 'fiscal_year') { const j = find('l-ytd'); j.decidedBy = 'person'; j.item.after = value; j.item.title = value === 'calendar' ? 'Year to date counts from 1 January' : 'Year to date counts from the day after ' + value; }
  } else if (op === 'drop_line') { const l = find(target); if (l) l.kept = false; if (l?.guessed) l.decidedBy = 'person'; holdFor(state, l, true); }
  else if (op === 'keep_line') { const l = find(target); if (l) l.kept = true; if (l?.guessed) l.decidedBy = 'rule'; holdFor(state, l, false); }
  else if (op === 'pin') { state.pins[target] = value; const l = find(target); if (l) l.decidedBy = 'person'; }
  else if (op === 'set_table_sample') {
    state.sampleRefused ??= [];
    const has = state.sampleRefused.includes(target);
    if (value === 'off' && !has) state.sampleRefused.push(target);
    if (value === 'on' && has) state.sampleRefused = state.sampleRefused.filter((t) => t !== target);
  }
  return startPlanView(state);
}
// Leaving a table out HOLDS the lines the person or their assistant made about it (StartPlanHold): kept, shown, not built.
function holdFor(state, line, out) {
  if (!line || line.item.kind !== 'include_table') return;
  const table = line.item.objectRef.slice('table:'.length);
  for (const l of state.lines) {
    if (l === line || l.group !== table || (l.decidedBy !== 'assistant' && l.decidedBy !== 'person')) continue;
    l.held = out ? `Held, because the table ${table} was left out by you. It comes back if you put that back in.` : null;
  }
}
// getConsent and setConsent for the model the plan builds. The scope is the model's identity, as the engine reports it.
export const CONSENT_SCOPE = 'model:contoso-consulting';
export function startPlanConsent(state) {
  return state.consent
    ? { granted: true, current: true, appliesTo: CONSENT_SCOPE, fieldsInForce: ['model.names', 'model.types'], schemaVersionInForce: 1, reason: null }
    : { granted: false, current: false, appliesTo: CONSENT_SCOPE, fieldsInForce: ['model.names', 'model.types'], schemaVersionInForce: 1,
      reason: 'you have not given permission to send this model\'s details for advice' };
}
export function startPlanSetConsent(state) { state.consent = true; return startPlanConsent(state); }
export function startPlanGrant(state, digest) {
  if (digest !== digestOf(state)) throw new Error('The plan changed since you read it. Look at it again, then answer.');
  // As LocalEngine.GrantStartDisclosureAsync: the model's own permission must be current first.
  if (!state.consent) throw new Error('Give permission for this model first, then say which tables Pro may see. ' + startPlanConsent(state).reason);
  state.granted = true; state.jevDeclined = false; state.decide = 'stable'; return startPlanView(state);
}
export function startPlanApprove(state, digest) {
  if (digest !== digestOf(state)) throw new Error('The plan changed since you read it, so nothing was approved. Look at it again, then approve.');
  state.approval = { digest, utc: '2026-10-01T10:00:00Z' }; return startPlanView(state);
}
export function startPlanBuild(state, digest) {
  const cur = digestOf(state);
  if (!state.approval || state.approval.digest !== cur) return { built: false, refused: 'Nothing was built. The plan has no approval that matches what it says now. Approve it first.', revision: state.revision, view: startPlanView(state) };
  if (digest !== cur) return { built: false, refused: 'Nothing was built. The plan changed since you read it.', revision: state.revision, view: startPlanView(state) };
  state.built = true; state.revision = 12; return { built: true, refused: null, revision: 12, view: startPlanView(state) };
}
// write_start_words: the person's keep of a flagged name (keep_name) and the person's edit of words. A keep is a line of the person's own; an edit makes the line the person's.
export function startPlanWriteWords(state, words, origin) {
  let written = 0; const refused = [];
  for (const w of words) {
    if (w.kind === 'keep_name') {
      state.kept.push(w.ref); written++;
      const group = w.ref.startsWith('column:') ? w.ref.slice(7).split('/')[0] : w.ref.startsWith('table:') ? w.ref.slice(6) : w.ref.slice(8).split('/')[0];
      state.lines.push(line('w-keep-' + w.ref, 'words', group, origin === 'human' ? 'person' : 'assistant', 'Keep the name ' + w.ref.split(/[:/]/).pop(), 'You kept this name.', { kind: 'keep_name', ref: w.ref, name: w.ref.split(/[:/]/).pop(), word: '' }));
      continue;
    }
    if (/\[refuse\]/.test(w.text ?? '')) { refused.push(w.ref + ': These words cannot be saved: a description may not say [refuse].'); continue; }   // any refusal the engine makes other than a conflict (a name that clashes, a rule on the words)
    const l = state.lines.find((x) => x.item.objectRef === w.ref && x.word != null && x.item.kind === 'set_description');
    if (!l) { refused.push(w.ref + ': It is not in the plan right now.'); continue; }
    if (w.expected != null && (l.word ?? '').trim() !== w.expected.trim()) { refused.push(w.ref + ': These words changed while you were editing them, so yours were not saved. Look at the new words and edit again.'); continue; }
    l.word = w.text; l.item.after = w.text; l.decidedBy = origin === 'human' ? 'person' : 'assistant'; written++;
  }
  state.edits++;
  return { written, refused, view: startPlanView(state) };
}
// The assistant replaces the words on the Staff description from the other door while the person has the editor open.
export function startPlanExternalWords(state, text) {
  const l = state.lines.find((x) => x.item.id === 'w-desc-staff'); l.word = text; l.item.after = text; l.decidedBy = 'assistant';
  state.words.total += 1;
  return startPlanView(state);
}
// The assistant writes words on the other door: the digest moves and an approval made before is void.
export function startPlanAssistantWords(state, n = 1) {
  state.words.total += n;
  state.lines.find((l) => l.item.id === 'w-syn').item.title = (38 + n) + ' synonyms';
  return startPlanView(state);
}

// The view EXACTLY as the engine's own builder (StartPlanViewBuilder, lane sf-core) shapes it for a plan made from a source
// with no Jev yet: a group per table with one "Bring in" line, ONE group with no table holding every link by its identity
// and the date table, base measures with no formula, Grades.Now null, a section Summary of "N changes.", and CanBuild true
// only once the approval matches. The page must read this as it reads the richer mock above.
export function startPlanRealShape(o = {}) {
  const by = 'rule';
  const shown = (kind, ref, name, title, kept, why) => ({ item: { kind, objectRef: ref, objectName: name, title, source: 'deterministic', status: 'proposed', id: null }, decidedBy: by, kept, why, held: null });
  const tables = [['Clients', 'dim_client', 6], ['Projects', 'dim_project', 9], ['Timesheets', 'fact_timesheet', 8]];
  const secs = {
    tables: tables.map(([n, src, c]) => ({ table: n, sourceName: src, lines: [shown('include_table', 'table:' + n, n, `Bring in ${n} (${c} of ${c} columns)`, true, 'It is in the source.')] })),
    links: [{ table: null, sourceName: null, lines: [
      shown('create_relationship', 'relationship:r-1', 'r-1', 'Timesheets to Projects on ProjectKey', true, 'The source declares this link.'),
      shown('create_relationship', 'relationship:r-2', 'r-2', 'Projects to Clients on ClientKey', true, 'The source declares this link.'),
      shown('create_relationship', 'relationship:r-3', 'r-3', 'Timesheets to Clients on ClientKey (off)', false, 'Two live paths would make totals unclear.'),
      shown('mark_date_table', 'table:Clients', 'Clients', 'Mark Clients as the date table on Date', true, 'Time maths needs one.')] }],
    measures: [{ table: 'Timesheets', sourceName: null, lines: [shown('create_measure', 'measure:Timesheets/Total hours', 'Total hours', 'Total hours: sum of Hours', true, 'A total people can use.')] }],
    words: [], bestpractice: [],
  };
  const approved = !!o.approved, digest = 'abc123';
  const decide = o.decide || 'off';
  const blocked = decide === 'deciding' ? 'Jev is still working on the plan. It will be ready to approve when it stops.'
    : !approved ? 'You have not approved this plan yet.' : null;
  return {
    exists: true, id: 'sp-real', destination: DEST, source: SOURCE,
    sections: Object.keys(SEC).map((id) => ({ id, title: SEC[id][0], summary: secs[id].flatMap((g) => g.lines).length ? secs[id].flatMap((g) => g.lines).length + ' changes.' : 'Nothing here yet.',
      counts: { rule: secs[id].flatMap((g) => g.lines).length, jev: 0, assistant: 0, you: 0 }, groups: secs[id] })),
    needsYou: decide === 'waiting_grant' ? [{ id: 'grant', kind: 'grant', text: 'Jev needs your permission to see the tables of ContosoOps on contoso.datawarehouse.fabric.microsoft.com before it decides anything.', options: ['Allow', 'Not now'], recommended: 'Allow' }] : [],
    decide: { state: decide, round: 0, reason: null, progress: [] }, words: { total: 0, open: 0 },
    grades: { now: null, afterBuild: 71, nowGrade: null, afterBuildGrade: 'B', nowBestPractice: null, afterBuildBestPractice: 'C', afterBuildProvisional: !!o.provisional },
    digest, approval: approved || o.built ? { digest, utc: '2026-10-01T10:00:00Z' } : null,
    canBuild: approved && !o.built && decide !== 'deciding',
    blockedBecause: o.built ? 'This plan was built into Contoso Consulting. Undo the build to change the plan and build it again.' : blocked,
    built: !!o.built, sampleFacts: false, sample: { state: 'none', reason: null, measured: 0, notMeasured: 0 }, jevDeclined: false,
  };
}
