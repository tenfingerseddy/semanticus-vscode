// The Plan page's fixture and its engine stub, in one file so the webview test and the browser driver
// can never drift apart about what the engine says.
//
// IT IS THE WIRE SHAPE, NOT A CONVENIENT ONE. The engine half landed on 56ed5144, so this file is
// shaped by Semanticus.Engine/Plan/ModelPlanView.cs rather than by an agreed sketch:
//
//   ids are STRINGS built from the subject      "safefix/Visibility", "check/measure:Sales/Margin %"
//   diff is an ARRAY of lines                   ModelPlanItem.Diff is string[]
//   refreshedAt and history[].when are ISO      DateTimeOffset on the wire, formatted by the page
//   orderedBy / ranked / note are on the plan   ModelPlanView.OrderedBy / .Ranked / .Note
//
// WHAT 21 SEPTEMBER ADDED, and why the map and the history were rebuilt around it:
//
//   item.tables is an array of OBJECTS          { name, count, what, diff } and no longer bare strings.
//                                               A bare string is a name with no count, which is why
//                                               "Remove 83 unused things" repeated unchanged on nine
//                                               cards and said nothing about any of them. With a count
//                                               per table the same item reads "Remove 31 unused things
//                                               here" on Sales and "Remove 12 unused things here" on
//                                               Customer. Both shapes stay legal on the wire and
//                                               plan-model's itemTables reads either.
//   tables[] carry their own facts              kind ("measure table", not "table"), columns, measures,
//                                               relationships, sizeBytes (null until a size scan runs)
//                                               and findings by category from the readiness scan the
//                                               plan already runs.
//   history rows are STRUCTURED                 kind, actor, itemId, title, revision, sentence, detail,
//                                               appeared, gone, completed, and `text` still, always
//                                               equal to `sentence`. ONE `detail` field carries three
//                                               different things by kind: the change lines on a done,
//                                               the person's reason on a skip, the refusal sentence on
//                                               a refusal. Oldest FIRST, up to 200. The old rows, a
//                                               time and a sentence, are still on this wire and two of
//                                               them are kept below on purpose.
//   worth.unchanged                             the engine saying the grade does not move, so the card
//                                               can say "Stays B" instead of "B to B".
//
// The five calls it plays, exactly as EngineRpcTarget.cs:404-409 declares them:
//
//   getModelPlan()                   -> the whole plan
//   skipModelPlanItem(id, reason)    -> the whole plan again
//   unskipModelPlanItem(id)          -> the whole plan again
//   modelPlanAction(id, 'start')     -> { itemId, action, performed, sentence, revision, plan }
//   routeAsk(text)                   -> AskRouteResult, the box on the Overview
//
// `start` is the only verb modelPlanAction accepts, and the stub REFUSES the others the way the engine
// does (LocalEngine.Plan.cs:98-101), so a page that started sending one would fail here rather than in
// front of a person. It also refuses an item that is not `auto` and one that is not a safe fix, with
// the engine's own sentences, because those refusals are a state the page has to draw.
//
// The keys, the types, the orders and the vocabularies above are the contract the engine lane published
// on 21 September (plan-map-shape.json), not a sketch: item.tables is sorted by name, `what` is counted
// BY KIND and never a reworded title, table.findings are readiness categories sorted by category, and
// the seven history kinds are drawn / redrawn / done / started / skipped / unskipped / refused.
//
// The content is Contoso Retail as the approved map-and-history mockup draws it: 13 tables,
// 5 items open, one set aside with a reason, three already done, and 83 unused things spread over six
// tables. Every per-table count below adds up to the item's own total, and the test executes that sum
// rather than trusting this comment.

/**
 * The instant this fixture is "now", and every relative timestamp in it is measured back from here.
 *
 * TWO READERS WANT TWO DIFFERENT THINGS, so this is a dial and not a constant.
 *
 * The DRIVER wants the real clock. A fixed moment sitting in the future of the browser's own clock made
 * the strip print a time of day ("20:42") where it should have said "2 min ago", so a shot taken on an
 * unknown day has to be measured back from that day. That is the default, read once at load.
 *
 * A TEST wants the same answer at every hour. Read off the real clock, an entry three and a half hours
 * back is "today" at 14:00 and "yesterday" at 00:20, so plan-page.test.mjs asserted four rows in today's
 * group and got six between midnight and about 02:00 local. A test that is right for twenty-two hours a
 * day is a test nobody can trust at the other two. `usePlanClock(PLAN_FIXED_NOW)` pins the dial before
 * the plan is built, and the page is handed the same instant as its `now`, which it already takes.
 */
let planNow = Date.now();

/**
 * The pinned instant: 16:20 on 21 September 2026, in the machine's OWN timezone.
 *
 * LOCAL and not a fixed offset, deliberately. Every derived time here is local (`setHours`) and the
 * page groups by local calendar day, so an instant fixed in absolute terms would only trade a clock
 * flake for a timezone flake: 16:20+10:00 is the small hours in the Americas, and the same entries
 * would fall on different days there. Anchored locally, the day groups are identical in every timezone.
 * Mid-afternoon, and three days clear of any daylight-saving change in September, so an hour of drift
 * cannot move a row onto another day.
 */
export const PLAN_FIXED_NOW = new Date(2026, 8, 21, 16, 20, 0, 0).getTime();

/** Pin the fixture's "now". Returns it, so a test can hand the page the same instant in one line. */
export function usePlanClock(instant) {
  planNow = instant;
  return planNow;
}

const ago = (minutes) => new Date(planNow - minutes * 60000).toISOString();
/** A time on an earlier day, so the timeline has real days to group by wherever it is opened. */
const daysBack = (days, hour, minute) => {
  const d = new Date(planNow - days * 86400000);
  d.setHours(hour, minute, 0, 0);
  return d.toISOString();
};

/** The nine items the mockup names, in the engine's order. The little number on a row is this order.
 *  A function, not an array: its one timestamp has to be stamped after the clock above is pinned. */
const ITEMS = () => [
  {
    // Round 5 (Kane, 30 Sep 2026): a suggestion list for a lean model, columns only. The row's title, diff and count are what STARTS
    // TICKED (57 columns); the 31 that start unticked are counted in the sentences and are each a line, with a reason, once the person
    // presses Work it out. 26 unused measures are not on it. The sentences are the engine's own (LocalEngine.Plan.cs:
    // CleanupOptionalReadSentence, CleanupPurposeSentence, CleanupCountSentence).
    id: 'cleanup/model', section: 'Structure', title: 'Remove 57 unused columns',
    sub: 'Optional: ETLBatch, LegacyDiscount, TaxCodeOld, SKU_X and 53 more',
    value: '1.8 MB back', who: 'you', status: 'ready', confidence: 0.89, optional: true,
    why: [
      'Optional. 2 reports\' use of this model has been read, and none of them shows these. Other reports may still use them. Nothing else on the plan waits for this.',
      'This list keeps the model lean. It is only a suggestion, so you look it over before anything is removed: untick any line you want to keep, or set the whole row aside. A column that nothing uses today may still be wanted for a report you build later, and only you know that.',
      '88 unused columns are listed. 57 start ticked. 31 start unticked, each with its reason on the line (for example a key, a date, a name or a number). Press Work it out to see them all.',
      'Nothing in the model uses them, and neither do the reports that were read.',
      'A version is saved first, so undo puts them all back in one step.',
      '26 unused measures are not on this list. A measure is removed on its own, when you are sure no report needs it.',
    ],
    diff: [
      '- 57 columns in Sales, Customer, Product, Promotion and Budget',
    ],
    touches: '57 objects in 5 tables',
    // 5 + 12 + 9 + 7 + 24 = 57, and the array is sorted by NAME, because that is what the engine
    // sends. The test does the sum; this comment does not count.
    tables: [
      {
        name: 'Budget', count: 5, what: '5 columns',
        diff: ['- Budget[ETLBatch], Budget[Scenario Code]', '- and 3 more in this table'],
      },
      {
        name: 'Customer', count: 12, what: '12 columns',
        diff: ['- Customer[MiddleName], Customer[CustKeyAlt], Customer[ETLBatch]', '- and 9 more in this table'],
      },
      {
        name: 'Product', count: 9, what: '9 columns',
        diff: ['- Product[SKU_X], Product[LegacyClass]', '- and 7 more in this table'],
      },
      {
        name: 'Promotion', count: 7, what: '7 columns',
        diff: ['- Promotion[LoadDate], Promotion[SourceRow]', '- and 5 more in this table'],
      },
      {
        name: 'Sales', count: 24, what: '24 columns',
        diff: [
          '- Sales[OrderKey], Sales[LineKey], Sales[ETLBatch]',
          '- Sales[LegacyDiscount], Sales[TaxCodeOld]',
          '- and 19 more in this table',
        ],
      },
    ],
    jump: { tool: 'lineage', label: 'Lineage' },
  },
  {
    id: 'safefix/Visibility', section: 'Trust', title: 'Fix 4 safe things',
    sub: 'hide 4 key columns that only relationships use',
    value: '+6 quality', who: 'auto', status: 'ready', confidence: 0.94,
    why: [
      'These 4 columns are keys used by relationships; report authors do not need them',
      'Hiding a key changes no number in any report',
    ],
    diff: [
      '+ hide Sales[CustomerKey], Sales[ProductKey]',
      '+ hide Customer[CustomerKey]',
      '+ hide Product[ProductKey]',
    ],
    touches: '4 columns in 3 tables',
    tables: [
      { name: 'Customer', count: 1, what: '1 column', diff: ['+ hide Customer[CustomerKey]'] },
      { name: 'Product', count: 1, what: '1 column', diff: ['+ hide Product[ProductKey]'] },
      {
        name: 'Sales', count: 2, what: '2 columns',
        diff: ['+ hide Sales[CustomerKey]', '+ hide Sales[ProductKey]'],
      },
    ],
    jump: { tool: 'bpa', label: 'Model quality' },
  },
  {
    id: 'describe/model', section: 'Meaning', title: 'Describe 12 measures',
    sub: 'Margin %, Net Sales, Customer Count and 9 more have no description',
    value: 'Copilot stops guessing', who: 'asst', status: 'ready', confidence: 0.91,
    why: [
      '12 of 71 measures have no description',
      'Descriptions are words, so your assistant writes them and you edit each one',
    ],
    diff: ['+ 12 descriptions (to be written)'],
    touches: '12 measures in 2 tables',
    tables: [
      {
        name: 'Key Measures', count: 9, what: '9 measures',
        diff: ['+ Margin %: description (to be written)', '+ Net Sales: description (to be written)', '+ 7 more in this table'],
      },
      {
        name: 'Time Intelligence', count: 3, what: '3 measures',
        diff: ['+ Sales YTD: description (to be written)', '+ Sales YoY %: description (to be written)', '+ Sales PY: description (to be written)'],
      },
    ],
    jump: { tool: 'knowledge', label: 'Model notes' },
  },
  {
    id: 'datetable/model', section: 'Structure', title: 'Mark Date as the date table',
    sub: 'time intelligence needs it',
    value: 'YTD and YoY work', who: 'you', status: 'ready', confidence: 0.97,
    why: ['Date has one row per day and every fact joins to it', 'YTD and YoY measures read this table'],
    diff: ['+ mark Date as the date table'],
    touches: 'Date',
    tables: [{
      name: 'Date', count: 1,
      what: '1 table',
      diff: ['+ mark Date as the date table'],
    }],
    jump: { tool: 'diagram', label: 'Diagram' },
  },
  {
    id: 'check/measure:Sales/Margin %', section: 'Trust', title: 'Add a check for Margin %',
    sub: 'it feeds 2 reports and has no check against the source',
    value: '2 reports safer', who: 'auto', status: 'ready', confidence: 0.86,
    why: [
      'Margin % is used by 5 visuals across Monthly report and Sales Overview',
      'It has a mapped SQL source, so a compare with source check can run',
    ],
    diff: ['+ check: Margin % against SQL source Finance, by month, blank means no value'],
    touches: '1 measure, 1 source',
    tables: [{
      name: 'Key Measures', count: 1,
      what: '1 measure',
      diff: ['+ check: Margin % against SQL source Finance, by month'],
    }],
    jump: { tool: 'tests', label: 'Validation' },
  },
  {
    id: 'relationship/Sales-Store', section: 'Structure', title: 'Relationship Sales to Store',
    sub: 'found from the data: StoreKey matches 100 percent',
    value: 'store slicing works', who: 'you', status: 'skipped', confidence: 0.96,
    skipReason: 'reporting by store is not needed yet',
    skippedUtc: ago(81),
    why: [
      'Every Sales[StoreKey] value exists in Store[StoreKey]; Store key is unique',
      'You set it aside today',
    ],
    diff: ['+ relationship Sales[StoreKey] to Store[StoreKey], many to one'],
    touches: 'Sales, Store',
    tables: [
      { name: 'Sales', count: 1, what: '1 relationship', diff: ['+ relationship Sales[StoreKey] to Store[StoreKey]'] },
      { name: 'Store', count: 1, what: '1 relationship', diff: ['+ relationship Sales[StoreKey] to Store[StoreKey]'] },
    ],
    jump: { tool: 'diagram', label: 'Diagram' },
  },
  {
    id: 'done/hide-keys', section: 'Trust', title: 'Hide 6 key columns',
    sub: 'done on Saturday', value: '+4 quality', who: 'auto', status: 'done', confidence: 0.94,
    why: ['Six key columns were visible to report authors who never need them'],
    diff: ['+ hide 6 key columns'],
    touches: '6 columns in 3 tables',
    tables: [
      { name: 'Customer', count: 2, what: '2 columns', diff: ['+ hide 2 key columns in Customer'] },
      { name: 'Product', count: 1, what: '1 column', diff: ['+ hide Product[SubcategoryKey]'] },
      { name: 'Sales', count: 3, what: '3 columns', diff: ['+ hide 3 key columns in Sales'] },
    ],
    jump: { tool: 'bpa', label: 'Model quality' },
  },
  {
    id: 'done/date-table', section: 'Structure', title: 'Add the Date table',
    sub: 'done on Saturday', value: 'time intelligence possible', who: 'asst', status: 'done', confidence: 0.95,
    why: ['The model had no date table, so no time intelligence could be written'],
    diff: ['+ table Date with 16 columns'],
    touches: 'Date',
    tables: [{ name: 'Date', count: 1, what: '1 table', diff: ['+ table Date with 16 columns'] }],
    jump: { tool: 'diagram', label: 'Diagram' },
  },
  {
    id: 'done/rel-sales-date', section: 'Structure', title: 'Create relationship Sales to Date',
    sub: 'done on Saturday', value: 'dates slice the facts', who: 'asst', status: 'done', confidence: 0.95,
    why: ['Sales[DateKey] matched Date[DateKey] on every row'],
    diff: ['+ relationship Sales[DateKey] to Date[DateKey], many to one'],
    touches: 'Sales, Date',
    tables: [
      { name: 'Date', count: 1, what: '1 relationship', diff: ['+ relationship Sales[DateKey] to Date[DateKey]'] },
      { name: 'Sales', count: 1, what: '1 relationship', diff: ['+ relationship Sales[DateKey] to Date[DateKey]'] },
    ],
    jump: { tool: 'diagram', label: 'Diagram' },
  },
];

/**
 * The thirteen tables, with the facts a card carries.
 *
 * `kind` is one of the engine's seven, most specific first: field parameter, date table, measure
 * table, bridge, fact, dimension, table. "Key Measures" and "Time Intelligence" hold measures and
 * nothing else, and a reader told they are plain tables has been told nothing.
 *
 * `sizeBytes` is null on every table no size scan has reached, which is all of them except the ones a
 * scan really touched: only a live vpaq_scan fills it, and the plan never goes to the network. Null
 * prints as SILENCE on the card, never as 0 MB.
 *
 * `findings` are the ACTIVE readiness findings on that table, grouped by the same category names the
 * findings page uses and sorted by category, which is exactly what the engine sends. Visibility and
 * Formatting are the two categories the rules fix deterministically, and those are the ones a card
 * paints as safe (Semanticus.Analysis/ReadinessRules.cs:1266, 1280, 1307, 1332).
 */
const TABLES = [
  {
    name: 'Sales', kind: 'fact', columns: 38, measures: 12, relationships: 5, sizeBytes: 2202010,
    findings: [{ category: 'Descriptions', count: 3 }, { category: 'Visibility', count: 2 }],
  },
  {
    name: 'Key Measures', kind: 'measure table', columns: 0, measures: 41, relationships: 0, sizeBytes: null,
    findings: [{ category: 'Descriptions', count: 9 }, { category: 'Formatting', count: 2 }],
  },
  {
    name: 'Customer', kind: 'dimension', columns: 16, measures: 0, relationships: 2, sizeBytes: 419430,
    findings: [{ category: 'Naming', count: 1 }, { category: 'Visibility', count: 1 }],
  },
  {
    name: 'Product', kind: 'dimension', columns: 14, measures: 0, relationships: 1, sizeBytes: 209715,
    findings: [{ category: 'Visibility', count: 1 }],
  },
  {
    name: 'Date', kind: 'date table', columns: 16, measures: 0, relationships: 1, sizeBytes: 104858,
    findings: [{ category: 'Formatting', count: 2 }],
  },
  {
    name: 'Promotion', kind: 'dimension', columns: 9, measures: 0, relationships: 1, sizeBytes: null,
    findings: [{ category: 'Descriptions', count: 4 }],
  },
  {
    name: 'Time Intelligence', kind: 'measure table', columns: 0, measures: 18, relationships: 0, sizeBytes: null,
    findings: [{ category: 'Descriptions', count: 3 }],
  },
  {
    name: 'Budget', kind: 'fact', columns: 7, measures: 2, relationships: 2, sizeBytes: null,
    findings: [{ category: 'Synonyms', count: 2 }],
  },
  { name: 'Store', kind: 'dimension', columns: 11, measures: 0, relationships: 1, sizeBytes: null, findings: [] },
  { name: 'Geography', kind: 'dimension', columns: 6, measures: 0, relationships: 1, sizeBytes: null, findings: [] },
  { name: 'Currency', kind: 'dimension', columns: 4, measures: 0, relationships: 0, sizeBytes: null, findings: [] },
  { name: 'Bridge Customer Store', kind: 'bridge', columns: 2, measures: 0, relationships: 2, sizeBytes: null, findings: [] },
  { name: 'Scenario', kind: 'dimension', columns: 3, measures: 0, relationships: 0, sizeBytes: null, findings: [] },
];

/**
 * The plan's story, OLDEST FIRST, which is the order the engine keeps it in. The page reverses it to
 * draw newest first; a fixture that was already newest-first would have hidden that entirely.
 *
 * Nine STRUCTURED rows across three days, and two rows from the OLD wire on a fourth. The old ones are
 * here because they are still in the plan memory of any model that was open before today, and the page
 * has to draw them honestly: no kind, no actor, no item, so they read as a redraw with their own
 * sentence and nothing to open.
 *
 * `text` is on every structured row too and is always the same string as `sentence`, exactly as the
 * engine sends it, so a reader that knows nothing else still works.
 *
 * `detail` carries three different things by kind, because one field does: the applied change lines on
 * a done or a started, the person's reason on a skip, and the refusal sentence on a refusal.
 */
const said = (row) => ({ ...row, text: row.sentence });
const HISTORY = () => [
  { when: daysBack(3, 8, 30), text: '2 items done, nothing new.' },
  { when: daysBack(3, 16, 40), text: '1 item done, 1 new.' },
  said({
    when: daysBack(2, 11, 20), kind: 'drawn', actor: 'engine', title: 'The plan was drawn from the model',
    revision: 128, sentence: '7 items, in the order the rules found them.',
    appeared: [
      'Remove 83 unused things', 'Add the Date table', 'Create relationship Sales to Date',
      'Mark Date as the date table', 'Describe 12 measures', 'Rename 3 cryptic columns',
      'Fix 4 safe things',
    ],
    gone: [],
    completed: [],
  }),
  said({
    when: daysBack(2, 11, 38), kind: 'done', actor: 'assistant', itemId: 'done/date-table',
    title: 'Add the Date table', revision: 129,
    sentence: 'A table marked as a date table appeared with 16 columns.',
    detail: ['+ table Date with 16 columns'],
  }),
  said({
    when: daysBack(2, 11, 41), kind: 'done', actor: 'assistant', itemId: 'done/rel-sales-date',
    title: 'Create relationship Sales to Date', revision: 131,
    sentence: 'The plan saw the relationship appear and closed the item.',
    detail: ['+ relationship Sales[DateKey] to Date[DateKey], many to one'],
  }),
  said({
    when: daysBack(1, 9, 2), kind: 'started', actor: 'plan', itemId: 'safefix/Visibility',
    title: 'Fix 4 safe things', revision: 141,
    sentence: 'The plan started this one and nothing in the model changed.',
    detail: [],
  }),
  said({
    when: daysBack(1, 9, 15), kind: 'unskipped', actor: 'you', itemId: 'describe/model',
    title: 'Describe 12 measures', sentence: 'It is back on the plan, open again.',
  }),
  said({
    when: ago(208), kind: 'redrawn', actor: 'engine', title: 'The plan was redrawn', revision: 147,
    sentence: 'You saved your changes, so the plan read the model again.',
    appeared: ['Add a check for Margin %'],
    // completed is a SUBSET of gone, which is how the engine sends it. The page shows the two apart.
    gone: ['Rename 3 cryptic columns'],
    completed: ['Rename 3 cryptic columns'],
  }),
  said({
    when: ago(82), kind: 'refused', actor: 'engine', itemId: 'check/measure:Sales/Margin %',
    title: 'Add a check for Margin %',
    sentence: 'Nothing ran. A check is written by you or your assistant in Validation.',
    detail: ['A check is written by you or your assistant in Validation. It does not run on its own.'],
  }),
  said({
    when: ago(81), kind: 'skipped', actor: 'you', itemId: 'relationship/Sales-Store',
    title: 'Relationship Sales to Store',
    sentence: 'It stays on the plan, marked as set aside.',
    detail: ['reporting by store is not needed yet'],
  }),
  said({
    when: ago(79), kind: 'done', actor: 'you', itemId: 'safefix/Visibility', title: 'Fix 4 safe things',
    revision: 148, sentence: '4 changes applied in Sales, Customer and Product.',
    detail: [
      '+ hide Sales[CustomerKey], Sales[ProductKey]',
      '+ hide Customer[CustomerKey]',
      '+ hide Product[ProductKey]',
    ],
  }),
  // ---- the review road, the Fix milestone's three kinds ------------------------------------------
  // Proposing changes NOTHING, dropping changes nothing, and approving is the one that wrote. All three
  // carry their lines in `detail`, the same field a done carries its change lines in.
  said({
    when: ago(44), kind: 'proposed', actor: 'assistant', itemId: 'describe/model',
    title: 'Describe 12 measures',
    sentence: '4 changes proposed, 3 kept. Nothing has been written.',
    detail: [
      '~ Margin %: description "Gross margin as a share of net sales"',
      '~ Net Sales: description "Sales after returns and discounts, in USD"',
      '~ Customer Count: description "Distinct customers with a sale in the period"',
      '~ Sales YTD: description "Net sales from the start of the fiscal year"',
    ],
  }),
  said({
    when: ago(33), kind: 'cancelled', actor: 'you', itemId: 'cleanup/model',
    title: 'Remove 83 unused things',
    sentence: 'The changes were dropped. Nothing was written.',
    detail: [
      '- 57 columns in Sales, Customer, Product, Promotion and Budget',
      '- 26 measures in Sales and Key Measures',
    ],
  }),
  said({
    when: ago(14), kind: 'approved', actor: 'you', itemId: 'done/hide-keys', title: 'Hide 6 key columns',
    revision: 149, sentence: '6 changes written as one step. Undo puts them all back.',
    detail: ['+ hide 2 key columns in Customer', '+ hide Product[SubcategoryKey]', '+ hide 3 key columns in Sales'],
  }),
];

/**
 * The five rule aims, as the targets lane derives them: one per section, from that section's own why
 * line turned into a statement, never stored and never removable (m3-item-shape.json, targets._twoKinds).
 * The wire sends `when`, `doneUtc` and `itemId` as null on every one of them, and they are here as null
 * rather than absent because null is what the page has to read.
 */
const RULE_AIMS = [
  { id: 'rules/structure', text: 'The model is built right.', section: 'Structure' },
  { id: 'rules/meaning', text: 'The model makes sense to people and to AI.', section: 'Meaning' },
  { id: 'rules/numbers', text: 'The measures are right and tidy.', section: 'Numbers' },
  { id: 'rules/trust', text: 'The numbers can be checked.', section: 'Trust' },
  { id: 'rules/ship', text: 'It can go to the reports.', section: 'Ship' },
].map((aim) => ({ ...aim, source: 'rules', when: null, doneUtc: null, itemId: null }));

/** The typed target the mockup draws, with the engine's own id shape (the words, slugged). */
const TYPED_TARGET = () => ({
  id: 'add-the-fiscal-calendar-as-a-second-calendar',
  text: 'Add the Fiscal Calendar as a second calendar',
  section: 'Structure',
  source: 'you',
  when: ago(26),
  doneUtc: null,
  itemId: 'target/add-the-fiscal-calendar-as-a-second-calendar',
});

/**
 * The item a typed target becomes.
 *
 * `diff` is ALWAYS empty and that is not "nothing to do": nothing in the model matches the target yet,
 * so the plan cannot work the change out (m3-item-shape.json, targets._itemRules.emptyDiff). `status` is
 * the engine's own 'target' and `state` is deliberately ABSENT, because the targets lane says in as many
 * words that it does not set the change-set lane's state field. The page has to read either, so the
 * fixture sends what the engine really sends today.
 */
const TARGET_ITEM = () => ({
  id: 'target/add-the-fiscal-calendar-as-a-second-calendar',
  section: 'Structure',
  title: 'Add the Fiscal Calendar as a second calendar',
  sub: 'You added this to the target. Nothing exists yet.',
  value: '', who: 'you', status: 'target', confidence: 0,
  why: [
    'You added this to the target today.',
    'Nothing in the model matches it yet, so the plan cannot work out the change. It is done by hand on '
      + 'Advanced modelling.',
  ],
  diff: [],
  touches: 'nothing yet',
  tables: [],
  jump: { tool: 'advmodels', label: 'Advanced modelling' },
});

/**
 * The change set the mockup draws, hanging off the item it is for.
 *
 * Four lines from the person's assistant, three kept, one dropped, and one line the engine left out with
 * its reason. The DIGEST is a fixed string here: the engine computes the real one over the lines and the
 * two versions and the page never computes one, so what matters in a fixture is that the page carries
 * back exactly the string it was handed.
 */
/**
 * The four lines the mockup draws.
 *
 * TWO IDS PER LINE, and the difference is the one that does not fail. `id` names the line, built from
 * the rule and the thing. `planItemId` is the short handle the change store keeps, and it is the one
 * `setPlanItem` takes. Both are strings, so a page that sent the wrong one would decide nothing, or
 * decide another line, with no error anywhere.
 */
const CHANGE_LINES = () => [
  {
    id: 'DESC-M/measure:Key Measures/Margin %',
    planItemId: 'ci-0001',
    line: '~ Margin %: description "Gross margin as a share of net sales"',
    op: 'setProperty', target: 'measure:Key Measures/Margin %', property: 'Description',
    before: '', after: 'Gross margin as a share of net sales', kept: true,
    provenance: { kind: 'assistant', point: 'describe', rule: null, confidence: 0.91 },
  },
  {
    id: 'DESC-M/measure:Key Measures/Net Sales',
    planItemId: 'ci-0002',
    line: '~ Net Sales: description "Sales after returns and discounts, in USD"',
    op: 'setProperty', target: 'measure:Key Measures/Net Sales', property: 'Description',
    before: '', after: 'Sales after returns and discounts, in USD', kept: true,
    provenance: { kind: 'assistant', point: 'describe', rule: null, confidence: 0.9 },
  },
  {
    id: 'DESC-M/measure:Key Measures/Customer Count',
    planItemId: 'ci-0003',
    line: '~ Customer Count: description "Distinct customers with a sale in the period"',
    op: 'setProperty', target: 'measure:Key Measures/Customer Count', property: 'Description',
    before: '', after: 'Distinct customers with a sale in the period', kept: false,
    provenance: { kind: 'assistant', point: 'describe', rule: null, confidence: 0.83 },
  },
  {
    // A FORMULA, so the review has one to draw as code. A DAX replacement read "Margin %: update DAX"
    // and nothing else, with Keep and Approve over it, so a person could approve a formula they had
    // never seen (Astra finding 11). Both sides are real DAX and the page draws them as blocks.
    id: 'DAX-M/measure:Key Measures/Margin %',
    planItemId: 'ci-0005',
    line: '~ Margin %: update DAX',
    op: 'setExpression', target: 'measure:Key Measures/Margin %', property: 'Expression',
    before: 'DIVIDE ( [Gross Margin], [Sales] )',
    after: 'DIVIDE ( [Gross Margin], [Net Sales] )',
    kept: true,
    provenance: { kind: 'assistant', point: 'describe', rule: null, confidence: 0.87 },
  },
  {
    id: 'DESC-M/measure:Time Intelligence/Sales YTD',
    planItemId: 'ci-0004',
    line: '~ Sales YTD: description "Net sales from the start of the fiscal year"',
    op: 'setProperty', target: 'measure:Time Intelligence/Sales YTD', property: 'Description',
    before: '', after: 'Net sales from the start of the fiscal year', kept: true,
    provenance: { kind: 'assistant', point: 'describe', rule: null, confidence: 0.88 },
  },
];

const CHANGE_SET = () => ({
  id: 'modelplan/describe~model',
  // The engine hashes the lines and the two versions; this stub counts, which is enough to prove the
  // page carries back whatever string it was last handed and never computes one of its own. The last
  // four characters are the kept count, so deciding a line and undeciding it comes back to this value.
  digest: '9c1b7e40a2d53f6188b0c4e7a915d326f8e0b41d7c96a2358be1470fd2c80004',
  state: 'proposed',
  revision: 41,
  sourceVersion: 'disk:637:2026-09-21T06:11:07.1234567Z',
  staleAtProposal: false,
  // COMPUTED on every read, and the ordinary way a set goes bad: fine when it was worked out, not fine
  // now. Always true when staleAtProposal is (m3-item-shape.json, `_changeSetFields`).
  stale: false,
  staleReason: '',
  sessionId: 'sess-contoso-1',
  modelIdentity: 'disk:9a1c4f77',
  // The PREVIOUS run for this item, beside the current proposal and never folded into it.
  lastAppliedAt: null,
  lastAppliedCount: 0,
  proposedAt: ago(8),
  appliedAt: null,
  keptCount: 3,
  sentence: '4 changes proposed, 3 kept. Nothing has been written.',
  checkpoint: '',
  lines: CHANGE_LINES(),
  refused: [
    { target: 'measure:Key Measures/Margin LY draft', reason: 'This one is waived, so it was left out.' },
  ],
});

const clone = (value) => JSON.parse(JSON.stringify(value));

// ---- M4: a Jev item, exactly as the engine composes one (Plan/JevFamilies/JevFamily.cs) ------------
//
// One item per table per family, in Meaning, owned by Jev, already proposed. Every line is a decision
// line: the point, the confidence as a number AND as the word the engine worked out, a reason the ENGINE
// built, and the engine's check. The unsure line arrives dropped. Only a Pro plan ever carries one.
const jevLine = (id, planItemId, measure, op, property, before, after, confidence, word, reason, check) => ({
  id: `${op}/measure:Sales/${measure}#${property}`,
  planItemId,
  line: `~ ${measure}: ${op === 'set_display_folder' ? `put in the ${after} folder` : `format as ${after}`}`,
  op,
  target: `measure:Sales/${measure}`,
  property,
  before,
  after,
  kept: word !== 'unsure',
  provenance: { kind: 'decision', point: 'measure.folder_and_format', rule: null, confidence, confidenceWord: word },
  reason,
  check: { exists: true, text: check },
});

const JEV_LINES = () => [
  jevLine(1, 'ci-0101', 'Total Sales', 'set_display_folder', 'displayFolder', '(none)', 'Money', 0.93, 'sure',
    'Money already holds 6 items in this model, and this measure belongs with them.',
    'Money is a folder this model already uses.'),
  jevLine(2, 'ci-0102', 'Total Sales', 'set_measure_format', 'formatString', '(none)', '$#,##0.00', 0.95, 'sure',
    'It reads as an amount of money, and $#,##0.00 is the money format this model already uses.',
    '$#,##0.00 is already used by 4 measures in this model.'),
  jevLine(3, 'ci-0103', 'Margin Pct', 'set_display_folder', 'displayFolder', '(none)', 'Money', 0.72, 'likely',
    'Money already holds 6 items in this model, and this measure belongs with them.',
    'Money is a folder this model already uses.'),
  jevLine(4, 'ci-0104', 'Margin Pct', 'set_measure_format', 'formatString', '(none)', '0%', 0.41, 'unsure',
    'It reads as a share or a rate, and its formula divides one number by another, so it shows out of 100.',
    '0% is the standard percentages format in the format library, and the model accepts it.'),
];

const JEV_ITEM = () => ({
  id: 'jev.measures.table:Sales',
  section: 'Meaning',
  title: 'Tidy 2 measures in Sales',
  sub: 'Total Sales, Margin Pct',
  value: '',
  who: 'jev',
  status: 'proposed',
  state: 'proposed',
  confidence: 0.9,
  why: [
    '2 measures in Sales have no display folder or no number format yet, so they read as raw numbers in a flat list.',
    'Jev chose each setting from a fixed list using the names, types and settings on this computer. '
      + 'Semanticus wrote every line and checked it before showing it.',
    '1 line arrives dropped because Jev was unsure. Keep it to include it.',
  ],
  diff: ['~ Total Sales: put in the Money folder', '~ Total Sales: format as $#,##0.00',
    '~ Margin Pct: put in the Money folder', '~ Margin Pct: format as 0%'],
  touches: '2 measures in Sales',
  tables: [{ name: 'Sales', count: 2, what: '2 measures', diff: [] }],
  jump: { tool: 'optimize', label: 'Pending changes' },
  changeSet: {
    id: 'modelplan/jev.measures.table:Sales',
    digest: 'b7d20e4c51a93f6088e1c4d7a215f326e8f0c41d7a96b2358ce1470fd2c80003',
    state: 'proposed',
    revision: 41,
    sourceVersion: 'disk:637:2026-09-21T06:11:07.1234567Z',
    staleAtProposal: false,
    stale: false,
    staleReason: '',
    sessionId: 'sess-contoso-1',
    modelIdentity: 'disk:9a1c4f77',
    lastAppliedAt: null,
    lastAppliedCount: 0,
    proposedAt: ago(1),
    appliedAt: null,
    keptCount: 3,
    sentence: '4 changes worked out, 3 kept. Nothing has been written yet.',
    checkpoint: '',
    lines: JEV_LINES(),
    refused: [
      { target: 'measure:Sales/Unit Cost', reason: 'It reads as money, but this model has no money format yet, so no currency was picked for it. Choose one by hand.' },
    ],
  },
});

// ---- M6: a starter measures item (Plan/JevFamilies/StarterMeasuresFamily.cs) -----------------------------
//
// One item per fact table, in Meaning. The shipped state is the RULE's item ("starter/table:Sales", owned by
// auto, every line provenance rule STARTER-MEASURE): the replay has not met the bar, so Jev does not decide yet.
// The Jev shape is the same item under "jev.starter.table:Sales", owned by Jev, with decision lines, and it is
// what a Pro session sees once the bar is met. Lines: create the measure (After is the engine's formula), then
// its format when it has one.
const starterLine = (n, planItemId, measure, op, after, jev, confidence, word, reason, check) => ({
  id: `${jev ? op : 'STARTER-MEASURE'}/measure:Sales/${measure}#${op === 'create_measure' ? 'expression' : 'formatString'}`,
  planItemId,
  line: op === 'create_measure' ? `+ ${measure}: ${reason.title}` : `+ ${measure}: format as ${after}`,
  op,
  target: `measure:Sales/${measure}`,
  property: op === 'create_measure' ? 'expression' : 'formatString',
  before: '(none)',
  after,
  kept: word !== 'unsure',
  provenance: jev
    ? { kind: 'decision', point: 'measure.starter', rule: null, confidence, confidenceWord: word }
    : { kind: 'rule', point: null, rule: 'STARTER-MEASURE', confidence: null, confidenceWord: null },
  reason: jev ? reason.text : null,
  check: { exists: true, text: check },
});

const STARTER_LINES = (jev) => {
  const ok = (name) => `${name} is a new name in this model, and its formula passed the model's DAX check.`;
  const amount = { title: 'add as the total of Amount', text: 'Amount is a number column in Sales that no measure uses yet. Its values add up across rows, so its total is the first thing people ask of it. Its own default summary says the same.' };
  const qty = { title: 'add as the total of Quantity', text: 'Quantity is a number column in Sales that no measure uses yet. Its values add up across rows, so its total is the first thing people ask of it. Its own default summary says the same.' };
  const price = { title: 'add as the average of UnitPrice', text: 'UnitPrice is a number column in Sales that no measure uses yet. A total of it means little, so its average is what people ask for. Its own default summary says the same.' };
  return [
    starterLine(1, 'ci-0201', 'Total Amount', 'create_measure', "SUM ( 'Sales'[Amount] )", jev, 0.95, 'sure', amount, ok('Total Amount')),
    starterLine(2, 'ci-0202', 'Total Amount', 'set_measure_format', '#,0.00', jev, 0.95, 'sure', amount, 'Amount already shows as #,0.00, and the new measure takes the same format.'),
    starterLine(3, 'ci-0203', 'Total Quantity', 'create_measure', "SUM ( 'Sales'[Quantity] )", jev, 0.7, 'likely', qty, ok('Total Quantity')),
    starterLine(4, 'ci-0204', 'Average UnitPrice', 'create_measure', "AVERAGE ( 'Sales'[UnitPrice] )", jev, 0.41, jev ? 'unsure' : 'sure', price, ok('Average UnitPrice')),
  ];
};

const STARTER_ITEM = (jev) => ({
  id: jev ? 'jev.starter.table:Sales' : 'starter/table:Sales',
  section: 'Meaning',
  title: 'Add 3 starter measures in Sales',
  sub: 'Total Amount, Total Quantity, Average UnitPrice',
  value: jev ? '' : 'one answer per number',
  who: jev ? 'jev' : 'auto',
  status: 'proposed',
  state: 'proposed',
  confidence: 0.8,
  why: [
    'Sales has number columns that no measure uses yet, so a report that shows one adds it up however the column happens to default to. 3 starter measures would give each one a single written answer.',
    jev
      ? 'Jev chose each setting from a fixed list using the names, types and settings on this computer. Semanticus wrote every line and checked it before showing it.'
      : "Each one follows the column's own default summary, and key columns get none. Undo takes them all back in one step.",
    ...(jev ? ['1 line arrives dropped because Jev was unsure. Keep it to include it.'] : []),
  ],
  diff: ['+ Total Amount: add as the total of Amount', '+ Total Amount: format as #,0.00',
    '+ Total Quantity: add as the total of Quantity', '+ Average UnitPrice: add as the average of UnitPrice'],
  touches: '3 measures in Sales',
  tables: [{ name: 'Sales', count: 3, what: '3 starter measures', diff: [] }],
  jump: { tool: 'optimize', label: 'Pending changes' },
  changeSet: {
    id: `modelplan/${jev ? 'jev.starter.table:Sales' : 'starter/table:Sales'}`,
    digest: 'c1e30f5d62b04a7199f2d5e8b326f437f9a1d52e8ba7c3469df2581ae3d91114',
    state: 'proposed',
    revision: 41,
    sourceVersion: 'disk:637:2026-09-21T06:11:07.1234567Z',
    staleAtProposal: false,
    stale: false,
    staleReason: '',
    sessionId: 'sess-contoso-1',
    modelIdentity: 'disk:9a1c4f77',
    lastAppliedAt: null,
    lastAppliedCount: 0,
    proposedAt: ago(1),
    appliedAt: null,
    keptCount: jev ? 3 : 4,
    sentence: jev ? '4 changes worked out, 3 kept. Nothing has been written yet.' : '4 changes worked out, 4 kept. Nothing has been written yet.',
    checkpoint: '',
    lines: STARTER_LINES(jev),
    refused: [],
  },
});

/** The engine runs a `start` only on an item whose id names a safe-fix family (IsSafeFixItem). */
const isSafeFix = (id) => String(id).startsWith('safefix/');

/**
 * The whole plan, as the engine answers getModelPlan.
 *
 * Options, each one a state a first read cannot otherwise reach:
 *   jev              M4: a Jev item on the Plan ("Tidy 2 measures in Sales"), proposed, with sure,
 *                    likely and unsure lines, reasons and checks. Ignored on free, as the engine does.
 *   free             the free session and the null-oracle session, which are the SAME answer: the
 *                    complete plan in rule order, every confidence 0, ranked false, and orderedBy
 *                    saying why. The engine's own wording, from DecisionService.cs:156 through Lower().
 *   midRun           a run is already going when the page opens.
 *   noAuto           nothing on the plan runs on its own, so Run the plan is live and disabled.
 *   lowRisk          one automatic item is only proposed. Nothing the engine can run is proposed in the
 *                    real plan, so this option exists to prove the Approve button stays ABSENT when it
 *                    would have nothing to reach.
 *   worthUnchanged   the engine worked the grade out and it does not move. "B to B" read as a typo, so
 *                    the card says "Stays B" and still lists what the work is worth.
 *   legacyTables     an engine older than 21 September: item.tables is an array of bare NAMES and the
 *                    tables carry no facts and no findings. The map must still draw.
 *   proposed         the Fix milestone's whole point: one item carries its own exact changes, four of
 *                    them, three kept, waiting on one decision. `next` points at it, so the Next up tile
 *                    and the Overview card both have to say the decision rather than the item's title.
 *   stale            the same set, worked out against a model that had ALREADY moved: staleAtProposal.
 *   ranBefore        a receipt of a PREVIOUS run beside the current proposal. The pending lines are
 *                    still pending, and the item is still proposed, not applied.
 *   detached         the snapshot's change plan is gone, so every handle is cleared. Nothing on it can
 *                    be decided, and the page must say so rather than leaving dead buttons.
 *   staleAfter       the set was fine when it was worked out and the model moved afterwards, which is
 *                    the ordinary way a set goes bad. staleAtProposal stays false and the engine
 *                    recomputes `stale` true on the read. Approve must be gone in both cases.
 *   appliedSet       the set landed. The item is `applied`, not `done`: the kept lines were written and
 *                    the plan has not yet worked out whether the gap really went.
 *   targets          a target a person typed, plus the item it became, at the HEAD of its section. It
 *                    shifts every position by one, which is why it is an option and not the default.
 *   proposedAuto     the same set, on the item that RUNS ON ITS OWN. Approving that one is Start by
 *                    another name, so it is the one review action Pro gates, and this is the shape that
 *                    proves free meets the pill instead of a dead button.
 */
export function makePlan(options = {}) {
  const plan = {
    modelName: 'Contoso Retail',
    refreshedAt: ago(2),
    refreshedWhy: 'after you saved: 1 item done, 1 new (a check for Margin %)',
    worth: {
      from: 'B',
      to: 'A',
      lines: ['83 unused things gone', '1.8 MB back', 'every measure described'],
      // The engine's own target line: the grade now, the grade the safe fixes reach, and whether
      // anything a person asked for is still open (m3-item-shape.json, targets['worth.targetLine']).
      targetLine: 'B, then A.',
    },
    next: { itemId: 'cleanup/model', line: 'the biggest single win on the plan, and it is yours to approve' },
    // The five rule aims are on EVERY plan and cannot be removed, so they are here with no option
    // guarding them. A typed one arrives only with `targets`.
    targets: clone(RULE_AIMS),
    items: clone(ITEMS()),
    history: clone(HISTORY()),
    tables: clone(TABLES),
    ranked: true,
    orderedBy: 'In value order, highest first.',
    note: null,
  };

  // The engine stamps a `state` on every item. Nine literal 'gap's would say nothing this line does not,
  // so the two that are really something else are read off the status they already carry and the rest
  // are the ordinary state of a plan item.
  plan.items.forEach((item) => {
    item.state = item.status === 'done' ? 'done' : item.status === 'skipped' ? 'skipped' : 'gap';
    item.changeSet = null;
  });

  if (options.free) {
    // Exactly what the engine sends with no key, on free, or below the bar: the SAME plan, complete, in
    // the order the rules found it, with no number on anything. Nothing is removed and nothing is hidden.
    plan.ranked = false;
    plan.orderedBy = 'In the order the rules found them, because suggestions like this one are part of '
      + 'Semanticus Pro.';
    plan.items.forEach((item) => { item.confidence = 0; });
  }

  // ---- M4: a Jev item. Pro only, exactly as the engine: a free plan never carries one, whatever the
  // option says, because a free session never asks Jev.
  if (options.jev && !options.free) plan.items = [JEV_ITEM(), ...plan.items];
  // M6: the starter measures item. 'rule' is the rule's item, which every tier sees; 'jev' is Jev's, Pro only.
  if (options.starter === 'rule') plan.items = [STARTER_ITEM(false), ...plan.items];
  if (options.starter === 'jev' && !options.free) plan.items = [STARTER_ITEM(true), ...plan.items];
  // Jev still looking at this revision in the background (ModelPlanView.JevPending). Never on free.
  if (options.jevPending && !options.free) plan.jevPending = true;

  // ---- the Fix milestone: a plan item carrying its own exact changes ------------------------------
  if (options.proposedAuto) {
    // Approving an item that runs on its own is the product doing the work in one press, which is what
    // Start is, so it is the one action in a review that Pro gates.
    const auto = plan.items.find((row) => row.id === 'safefix/Visibility');
    auto.state = 'proposed';
    auto.status = 'proposed';
    auto.changeSet = CHANGE_SET();
    plan.next = { itemId: 'safefix/Visibility', line: 'the rules worked these out from the model' };
  }

  if (options.proposed || options.stale || options.staleAfter || options.appliedSet || options.detached
    || options.ranBefore) {
    const item = plan.items.find((row) => row.id === 'describe/model');
    const set = CHANGE_SET();
    if (options.ranBefore) {
      // A RECEIPT beside a proposal. An item id names a family, so four descriptions approved today and
      // a fifth added tomorrow arrive under the same id. The receipt must never make the fifth look
      // applied (Astra finding 10).
      set.lastAppliedCount = 4;
      set.lastAppliedAt = ago(190);
    }

    if (options.detached) {
      // A DETACHED snapshot: its change plan is gone, so the engine clears every handle. The ids it used
      // to hold have been handed out again to whatever is on the change plan now, which is why a page
      // that kept the buttons live decided somebody else's change (Astra finding 7).
      item.state = 'proposed';
      item.status = 'proposed';
      set.stale = true;
      set.staleReason = 'The list these came from is gone, so they cannot be kept or dropped. Work them '
        + 'out again.';
      set.lines.forEach((line) => { line.planItemId = ''; });
    }

    if (options.stale || options.staleAfter) {
      item.state = 'proposed';
      item.status = 'proposed';
      // `stale` is true in BOTH cases, because it answers "can this be applied right now". Only the
      // stored flag tells them apart, and only the engine sets it.
      set.stale = true;
      set.staleAtProposal = !!options.stale;
      set.staleReason = options.stale
        ? 'These were already out of date when they were worked out, so they cannot be applied. Work '
          + 'them out again.'
        : 'You changed the model after these were worked out, so they cannot be applied to it. Work '
          + 'them out again.';
      set.sentence = 'These changes are out of date. Work them out again.';
    } else if (options.appliedSet) {
      // Applied is NOT done. The lines were written; whether the gap has really gone is worked out on
      // the next compose, and until then the item honestly says the changes landed and nothing more.
      item.state = 'applied';
      set.state = 'applied';
      set.appliedAt = ago(3);
      set.checkpoint = 'undo:42';
      set.sentence = '3 changes written as one step. Undo puts them all back.';
      set.lines.forEach((line) => { line.kept = line.kept === true; });
    } else {
      item.state = 'proposed';
      item.status = 'proposed';
    }
    item.changeSet = set;
    // The Next up tile has to say the DECISION when the item it points at is one waiting on a decision.
    // The item keeps its place in the engine's order: nothing about carrying changes reorders a plan,
    // and moving it would have changed every position number on the page for no reason.
    plan.next = { itemId: 'describe/model', line: 'your assistant worked these out from the model notes' };
  }

  // ---- a target a person typed, and the item it became --------------------------------------------
  if (options.targets) {
    plan.targets = [...plan.targets, TYPED_TARGET()];
    // An OPEN target item goes at the HEAD of its section (m3-item-shape.json,
    // targets._itemRules.placement). Structure's first item is the first item on this plan, so it lands
    // at the front and every position below it moves down one.
    plan.items = [TARGET_ITEM(), ...plan.items];
    plan.worth.targetLine = 'B, then A, then what you asked for.';
  }

  if (options.lowRisk) {
    const item = plan.items.find((row) => row.id === 'check/measure:Sales/Margin %');
    item.status = 'proposed';
    item.sub = 'proposed: it feeds 2 reports and has no check against the source';
  }

  if (options.noAuto) {
    // Nothing on the plan runs on its own. The real state Kane hit on 23 September: Run the plan is a
    // live Pro control with nothing to reach, so it is disabled and the page owes him the reason. The
    // two `auto` items become work for a person and for their assistant, which is what the reason line
    // then counts. Everything else about the plan is untouched.
    plan.items.filter((item) => item.who === 'auto' && item.status === 'ready').forEach((item, index) => {
      item.who = index === 0 ? 'you' : 'asst';
      item.sub = index === 0 ? 'you approve this one' : 'this one needs words, so your assistant writes it';
    });
  }

  if (options.midRun) {
    const running = plan.items.find((item) => item.id === 'safefix/Visibility');
    running.status = 'running';
    running.sub = 'running now';
  }

  if (options.worthUnchanged) {
    // `unchanged` is true only when BOTH grades are known and equal, so `to` is here too. The card is
    // what turns that into "Stays B"; the engine does not send the words.
    plan.worth = { from: 'B', to: 'B', unchanged: true, pending: false, lines: ['83 unused things gone', '1.8 MB back'] };
  }

  if (options.legacyTables) {
    // An engine older than 21 September: bare table NAMES, no per-table facts, no findings, and a
    // history of one sentence a row. The map and the timeline still have to draw.
    plan.items.forEach((item) => {
      item.tables = (item.tables ?? []).map((t) => (typeof t === 'string' ? t : t.name));
    });
    plan.tables = plan.tables.map((t) => ({ name: t.name, kind: t.kind === 'measure table' ? 'table' : t.kind }));
    plan.history = plan.history.map((row) => ({ when: row.when, text: row.text ?? row.sentence ?? 'the plan changed' }));
  }
  return plan;
}

/**
 * The engine's half of modelPlanAction. Deterministic, and it REFUSES what the engine refuses, because
 * a refusal with a sentence is a state the page has to draw and a stub that granted everything would
 * have hidden it.
 */
export function planAction(previous, itemId, action, digest) {
  const verb = String(action ?? 'start').trim().toLowerCase();
  // The five verbs the Fix milestone's engine really performs (m3-item-shape.json `_verbs`, plus the
  // targets lane's `done`). Anything else throws, the way the engine throws on an unknown verb, so a
  // page that started sending one fails in a test rather than quietly doing nothing in front of a person.
  const KNOWN = ['start', 'propose', 'approve', 'cancel', 'done'];
  if (!KNOWN.includes(verb)) {
    throw new Error(`This release performs ${KNOWN.join(', ')} and nothing else. Use skip_model_plan_item `
      + 'to set an item aside and unskip_model_plan_item to put it back.');
  }
  if (verb !== 'start') return planReviewAction(previous, itemId, verb, digest);
  const plan = clone(previous);
  const item = plan.items.find((row) => row.id === itemId);
  const refuse = (sentence) => ({ itemId, action: verb, performed: false, sentence, revision: 41, plan });

  if (!item) {
    return refuse('Nothing on the plan has that id. It may have been finished or set aside since the plan '
      + 'was last read.');
  }
  if (item.status === 'skipped') return refuse('This item is set aside. Put it back first, then start it.');
  if (item.who === 'asst') {
    return refuse('This one is words, so your assistant writes it. Ask it to, and it will use the same plan.');
  }
  if (item.who !== 'auto') {
    return refuse('This one changes the model only once you approve it. Work out its changes, check them and '
      + 'approve them on the Plan, or open ' + (item.jump ? item.jump.label : 'the page it points at') + ' and do it there.');
  }
  if (!isSafeFix(item.id)) {
    return refuse('Nothing in this release runs this one for you yet. Open '
      + (item.jump ? item.jump.label : 'the page it points at') + ' and do it there.');
  }

  item.status = 'done';
  item.sub = 'done just now';
  plan.history.unshift({ when: new Date(planNow).toISOString(), text: 'Ran ' + item.title.toLowerCase() + '. 9 changes applied.' });
  return {
    itemId, action: verb, performed: true, revision: 42, plan,
    sentence: '9 things were fixed. The AI readiness grade is now A minus. Undo puts it all back in one step.',
  };
}

/**
 * The four verbs that are not `start`: propose, approve, cancel, done.
 *
 * Each one REFUSES what the engine refuses, because a refusal with a sentence is a state the page has to
 * draw. The one that matters most is approve on a STALE digest: the model moved, so the lines describe
 * something that is not there, and the engine says no rather than writing yesterday's answer.
 *
 * `done` answers performed FALSE on success, exactly as the targets lane says it does
 * (m3-item-shape.json, targets._verbs.done): performed means the MODEL changed, and marking a target
 * reached changes the sidecar and nothing else. A page that read the flag as "did it work" would call a
 * success a failure, which is the whole reason this stub answers that way.
 */
export function planReviewAction(previous, itemId, verb, digest) {
  const plan = clone(previous);
  const item = plan.items.find((row) => row.id === itemId);
  const answer = (performed, sentence) => ({ itemId, action: verb, performed, sentence, revision: 41, plan });
  if (!item) return answer(false, 'Nothing on the plan has that id. It may have been finished or set aside since.');

  if (verb === 'done') {
    if (item.status !== 'target' && item.state !== 'target') {
      return answer(false, 'Only a target you added can be marked done. Everything else on the plan is '
        + 'worked out from the model.');
    }
    item.status = 'done';
    item.state = 'done';
    item.sub = 'You added this to the target. You marked it done.';
    item.touches = 'nothing to do';
    item.why = [...item.why, 'You marked it done today.'];
    const target = (plan.targets ?? []).find((t) => t.itemId === itemId || t.id === itemId);
    if (target) target.doneUtc = new Date(planNow).toISOString();
    retargetLine(plan);
    // performed is FALSE and the call succeeded. The model did not change; the sidecar did.
    return answer(false, 'Marked done. Nothing in the model changed, because this one is yours to do by hand.');
  }

  if (verb === 'propose') {
    const set = CHANGE_SET();
    set.proposedAt = new Date(planNow).toISOString();
    item.state = 'proposed';
    item.status = 'proposed';
    item.changeSet = set;
    return answer(false, '4 changes proposed, 3 kept. Nothing has been written.');
  }

  const set = item.changeSet;
  if (!set) return answer(false, 'There are no changes worked out for this one.');

  if (verb === 'cancel') {
    item.changeSet = null;
    item.state = 'gap';
    item.status = 'ready';
    return answer(false, 'The changes were dropped. Nothing was written.');
  }

  // approve
  if (set.staleAtProposal) {
    return answer(false, 'The model changed after these changes were worked out, so they cannot be '
      + 'applied. Work them out again.');
  }
  if (digest !== undefined && digest !== set.digest) {
    return answer(false, 'These are not the changes you were shown. Read them again and approve from '
      + 'what is on screen.');
  }
  const kept = set.lines.filter((line) => line.kept === true);
  set.state = 'applied';
  set.appliedAt = new Date(planNow).toISOString();
  set.checkpoint = 'undo:42';
  set.keptCount = kept.length;
  set.sentence = `${kept.length} change${kept.length === 1 ? '' : 's'} written as one step. `
    + 'Undo puts them all back.';
  item.state = 'applied';
  item.status = 'ready';
  return { ...answer(true, set.sentence), revision: 42 };
}

/**
 * The engine's half of applyPlanItems (groundwork contract section 3): every listed item, each with the
 * digest the page was shown, applied as ONE step, or nothing. It refuses the whole list on the first item
 * that approve would refuse, and names that item, the way the engine does
 * (Semanticus.Engine/Plan/LocalEngine.PlanVerbs.cs, ApplyPlanItemsAsync).
 */
export function planApplyItems(previous, items) {
  const plan = clone(previous);
  const refuse = (item, reason) => ({
    performed: false, applied: 0, itemIds: [], failedItemId: item ? item.id : null, revision: 41, plan: previous,
    sentence: item ? `Nothing was changed, because of ${item.title.charAt(0).toLowerCase()}${item.title.slice(1)}. ${reason}`
      : `Nothing was changed. ${reason}`,
  });
  const ready = [];
  for (const asked of items ?? []) {
    const item = plan.items.find((row) => row.id === asked.id);
    if (!item) return refuse(null, `Nothing on the plan has the id ${asked.id}.`);
    const set = item.changeSet;
    if (!set || set.state !== 'proposed') {
      return refuse(item, 'No changes have been worked out for this one, so nothing was applied. Work them out first.');
    }
    if (set.staleAtProposal) {
      return refuse(item, 'The model changed after these changes were worked out, so they are out of date. '
        + 'Work them out again. Nothing was applied.');
    }
    if (asked.digest !== set.digest) {
      return refuse(item, 'The changes on the plan are not the ones you approved, so nothing was applied. '
        + 'Read them again and approve what you see.');
    }
    ready.push(item);
  }
  let total = 0;
  for (const item of ready) {
    const set = item.changeSet;
    const kept = set.lines.filter((line) => line.kept === true);
    total += kept.length;
    set.state = 'applied';
    set.appliedAt = new Date(planNow).toISOString();
    set.checkpoint = 'undo:42';
    set.keptCount = kept.length;
    item.state = 'applied';
    item.status = 'ready';
  }
  return {
    performed: total > 0, applied: total, itemIds: ready.map((item) => item.id), revision: 42, plan,
    sentence: `Applied ${total} change${total === 1 ? '' : 's'} as one step you can undo.`,
  };
}

/** Which item on this plan owns the line with this handle, or null. The engine looks a line up by its
 *  handle alone, across the whole store, so the stub does too: a caller that had to know the item as
 *  well could not be wrong in the way the real one can. */
export function planFindLine(plan, planItemId) {
  for (const item of plan.items ?? []) {
    const line = (item.changeSet?.lines ?? []).find((row) => row.planItemId === planItemId);
    if (line) return { item, line };
  }
  return null;
}

/**
 * What setPlanItem REALLY answers: a ChangePlanView (Semanticus.Engine/Plan.cs:72).
 *
 * A planId, a scope, a revision, a summary and an `items` array of individual CHANGE LINES. No sections,
 * no owners, no targets, no history, no tables. It is not a plan and it never was, and a mock that
 * answered with a whole plan proved the page against a wire that does not exist: one press of Keep
 * replaced the plan on screen with a list of change lines and lost everything else until a refresh
 * (Astra finding 8). This answers what the engine answers, so a page that reads it as a plan fails here.
 */
export function planChangeView(plan, planItemId) {
  const found = planFindLine(plan, planItemId);
  const set = found ? found.item.changeSet : null;
  const lines = set ? set.lines : [];
  return {
    planId: set ? set.id : 'modelplan/none',
    scope: 'whole model',
    revision: set ? (set.revision ?? 41) : 41,
    items: lines.map((line) => ({
      id: line.planItemId,
      objectRef: line.target,
      objectName: line.target,
      kind: line.op,
      source: line.provenance ? line.provenance.kind : 'rule',
      category: 'Descriptions',
      title: line.line,
      status: line.kept ? 'approved' : 'rejected',
      before: line.before,
      after: line.after,
    })),
    summary: { total: lines.length, approved: lines.filter((l) => l.kept).length,
      rejected: lines.filter((l) => !l.kept).length, applied: 0, needsContent: 0 },
    note: undefined,
  };
}

/**
 * setPlanItem(itemId, after, approved, origin), where the itemId is the LINE's handle: keep or drop one
 * line of one item's change set. It writes nothing to the model and it MOVES THE DIGEST, which is what
 * makes an approve against an old digest refusable.
 *
 * It takes the handle ALONE, exactly as the engine does. A stub that also took the plan item would have
 * hidden the mistake this signature invites: the same method, with the same argument types, decides a
 * change set row when it is given one of those ids instead.
 *
 * A line of a set that is already applied cannot be changed, the way an applied row could not be before.
 */
export function planSetLine(previous, planItemId, kept) {
  const plan = clone(previous);
  const found = planFindLine(plan, planItemId);
  if (!found) return plan;
  const { item, line } = found;
  const set = item.changeSet;
  if (!set || set.state !== 'proposed') return plan;
  line.kept = kept === true;
  set.keptCount = set.lines.filter((row) => row.kept === true).length;
  // The digest is over the lines, so deciding one moves it. The engine hashes; this counts, which is
  // enough to prove the page carries back whatever it was last handed.
  set.digest = `${set.digest.slice(0, 60)}${String(set.keptCount).padStart(4, '0')}`;
  set.sentence = `${set.lines.length} changes proposed, ${set.keptCount} kept. Nothing has been written.`;
  return plan;
}

/**
 * The engine's target line, worked out again after a target is added, taken away or marked done.
 *
 * The clause ", then what you asked for" counts ONLY targets a person typed and has not marked done
 * (m3-item-shape.json, targets['worth.targetLine']._countedAs). Recomputing it here matters: a stub that
 * left the clause standing after the last typed target went would have put a sentence on the Target tile
 * that the engine would never send, and a driver shot of it would have looked right.
 */
function retargetLine(plan) {
  const open = (plan.targets ?? []).filter((t) => t.source === 'you' && !t.doneUtc).length;
  const base = 'B, then A';
  plan.worth.targetLine = open > 0 ? `${base}, then what you asked for.` : `${base}.`;
  return plan;
}

/** addPlanTarget(text, section). Free, refuses a blank, a text over 200 characters and a section that is
 *  not one of the five (m3-item-shape.json, targets._verbs.add). The same words twice are ONE target. */
export function planAddTarget(previous, text, section) {
  const plan = clone(previous);
  const words = String(text ?? '').trim();
  if (!words) throw new Error('A target needs some words. Say what this model should have.');
  if (words.length > 200) throw new Error('A target has to fit in 200 characters. Say the shorter version.');
  const named = ['Structure', 'Meaning', 'Numbers', 'Trust', 'Ship']
    .find((name) => name.toLowerCase() === String(section ?? '').trim().toLowerCase());
  if (!named) throw new Error('A target belongs to one of Structure, Meaning, Numbers, Trust or Ship.');
  const id = words.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
  const already = plan.targets.find((t) => t.id === id);
  if (already) { already.section = named; already.doneUtc = null; return retargetLine(plan); }
  plan.targets.push({
    id, text: words, section: named, source: 'you', when: new Date(planNow).toISOString(),
    doneUtc: null, itemId: `target/${id}`,
  });
  plan.items = [{
    ...TARGET_ITEM(), id: `target/${id}`, section: named, title: words,
    why: ['You added this to the target today.',
      'Nothing in the model matches it yet, so the plan cannot work out the change.'],
  }, ...plan.items];
  return retargetLine(plan);
}

/** removePlanTarget(id). Takes the target id or the item id, and REFUSES a rule aim: one is derived on
 *  every call and belongs to nobody. */
export function planRemoveTarget(previous, id) {
  const plan = clone(previous);
  if (String(id ?? '').startsWith('rules/')) {
    throw new Error('That one comes from the rules, so it is not yours to take away.');
  }
  const target = plan.targets.find((t) => t.id === id || t.itemId === id);
  if (!target) return plan;
  plan.targets = plan.targets.filter((t) => t !== target);
  plan.items = plan.items.filter((row) => row.id !== target.itemId);
  return retargetLine(plan);
}

/** skipModelPlanItem: the whole plan back, with that item set aside and the reason kept. */
export function planSkip(previous, itemId, reason) {
  const plan = clone(previous);
  const item = plan.items.find((row) => row.id === itemId);
  if (item) {
    item.status = 'skipped';
    item.skipReason = (reason && String(reason).trim()) || 'not now';
    item.skippedUtc = new Date(planNow).toISOString();
  }
  return plan;
}

/** unskipModelPlanItem: the whole plan back, with that item open again. */
export function planUnskip(previous, itemId) {
  const plan = clone(previous);
  const item = plan.items.find((row) => row.id === itemId);
  if (item) {
    item.status = 'ready';
    delete item.skipReason;
    delete item.skippedUtc;
  }
  return plan;
}

/** What the driver injects: the four responses, each reading the params the page really sent. */
export function responsesFor(plan) {
  let current = clone(plan);
  return {
    get: () => clone(current),
    set: (next) => { current = clone(next); },
    start: (params) => {
      const result = planAction(current, params[0], params[1]);
      current = clone(result.plan);
      return result;
    },
    skip: (params) => { current = planSkip(current, params[0], params[1]); return clone(current); },
    unskip: (params) => { current = planUnskip(current, params[0]); return clone(current); },
  };
}


// ---- the box -------------------------------------------------------------------------------------
//
// routeAsk, as RouteAskAsync really answers it (Semanticus.Engine/LocalEngine.Plan.cs and
// Plan/AskRoutePoint.cs). Four shapes, because that is what the engine has since 22 September:
//
//   ANSWERED FROM STATE   the engine worked it out from its own numbers. A sentence, with confidence 1
//                         and asked false, since nothing was asked of anybody.
//   MATCHED ON WORDS      a place, no sentence, asked false. The ordinary answer now: the words a page
//                         is about are matched locally before any service is considered.
//   MATCHED WITH A THING  the same, plus `create` naming what is being made, which the page turns into
//                         a door to that thing's own form where one exists.
//   NOTHING               no place, fallback "menu", the plain SENTENCE a person reads, and the gate's
//                         own words in `reason`, which the page never shows. Kane read a consent
//                         refusal as an answer on 21 September; the two fields are separate for that
//                         reason and the box only draws the first.
//
// FREE NEVER GETS HERE. route_ask is Pro on both doors (Kane, 22 Sep), the page draws the quiet pill
// instead of an input on a free session, and nothing calls this.
//
// The sentences below are the shapes AskRoutePoint's own builders make (Shape, SizeSentence and
// ChangeSentence), filled with the numbers the OVERVIEW is showing while the box is on screen: the
// harness model Contoso (mock), 11 tables, 12 measures, 4 edits this session and no size scan taken. A
// driver shot in which the box contradicts the page above it would be a picture of a bug that is not
// there, so the two agree on purpose.

/** The bar the box routes at, from the AskRoute decision point. */
export const ASK_BAR = 0.85;

const ASK_ANSWERS = [
  {
    match: ['how big is this model', 'how big', 'model size', 'how much memory'],
    result: {
      tool: 'stats', area: 'model', confidence: 1, confidenceBar: ASK_BAR, asked: false,
      // No size scan has been taken on this session, and SizeSentence says exactly that rather than
      // printing a zero. The Overview's own band says the same thing two cards down.
      sentence: 'Contoso (mock) has 11 tables and 12 measures. Its size in memory has not been measured '
        + 'yet. Open the Size page to measure it.',
    },
  },
  {
    match: ['what changed', 'what did i change', 'any unsaved changes'],
    result: {
      tool: 'history', area: 'model', confidence: 1, confidenceBar: ASK_BAR, asked: false,
      sentence: '4 changes have been made and not saved yet.',
    },
  },
  {
    // Matched on words: the place is the whole answer, and nothing was asked of anybody.
    match: ['what breaks if i rename total sales', 'what uses margin', 'what depends on net sales'],
    result: { tool: 'lineage', area: 'model', confidence: 1, confidenceBar: ASK_BAR, asked: false },
  },
  {
    match: ['run the best practice rules', 'run bpa'],
    result: { tool: 'bpa', area: 'checks', confidence: 1, confidenceBar: ASK_BAR, asked: false },
  },
  {
    // A CREATE with a form behind it. Advanced Modelling has a calendars panel, so the page opens it.
    match: ['create a calendar', 'add a calendar', 'mark the date table'],
    result: {
      tool: 'advmodels', area: 'model', confidence: 1, confidenceBar: ASK_BAR, asked: false,
      create: 'calendar',
    },
  },
  {
    // A CREATE the MODEL TREE makes. Studio has no measure form, and the product does have a way to make
    // one: the tree's New Measure command. The engine names the place that owns the thing (DAX Lab, the
    // Calculations area) and the thing itself; the page turns that into the command and runs it.
    match: ['create a measure', 'add a new measure', 'create a kpi'],
    result: {
      tool: 'query', area: 'explore', confidence: 1, confidenceBar: ASK_BAR, asked: false,
      create: 'measure',
    },
  },
  {
    // The Plan page joined the catalogue on 22 September. It was in the area strip and in the help page
    // and in no routing catalogue, so the box could not send anybody to the page that says what is next.
    match: ['what should i do next', 'what should i do', 'what is left to do', 'show me the plan'],
    result: { tool: 'plan', area: 'model', confidence: 1, confidenceBar: ASK_BAR, asked: false },
  },
];

/**
 * One ask, answered the way the engine answers it.
 *
 * `free` matters here and is not a decoration: on a free session nothing is asked of a service at all,
 * so a question the engine cannot answer from its own numbers comes back as a skip with the engine's
 * reason, in the same wording DecisionService gives the plan's order line.
 */
/** The plain line a person reads when nothing placed them. AskRoutePoint.NoMatchSentence, verbatim. */
export const ASK_NO_MATCH =
  'Nothing here matches those words yet. Try a page name, or the name of a thing in the model.';

export function askRoute(text) {
  const asked = String(text ?? '');
  const t = asked.toLowerCase().replace(/[^a-z0-9 ]+/g, ' ').replace(/\s+/g, ' ').trim();

  if (!t) {
    const line = 'Type what you are trying to do and this will find the place for it.';
    return {
      tool: null, area: null, confidence: 0, confidenceBar: ASK_BAR, fallback: 'menu', asked: false,
      sentence: line, reason: line,
    };
  }

  const hit = ASK_ANSWERS.find((entry) => entry.match.some((phrase) => t === phrase || t.includes(phrase)));
  if (hit) return clone(hit.result);

  // The words placed nobody and the service is gated on this fixture's session, which is the state
  // Kane's real session was in. The SENTENCE is what the box draws; the REASON is the gate's own words
  // and the box must not draw it. A driver shot showing the reason is the bug coming back.
  return {
    tool: null, area: null, confidence: 0, confidenceBar: ASK_BAR, fallback: 'menu', asked: false,
    sentence: ASK_NO_MATCH,
    reason: 'You have not given permission to send this model\u2019s details for advice.',
  };
}

// ---- the two surfaces folded onto the Plan page, 23 September ---------------------------------------
//
// Kane, fifth Yoga build: "Model spec should be replaced by plan and pending changes should also be. No
// point in three similar pages, it makes it very confusing for users."
//
// So the page now also reads TWO other engine answers, and this file plays both of them, in their own
// wire shapes rather than in a shape convenient for the plan:
//
//   getPlan()                                -> ChangePlanView (Semanticus.Engine/Plan.cs). A set EXISTS
//                                               whenever it carries a planId, even with no rows.
//   setPlanItem(id, after, approved, origin) -> the whole set again, that row kept or dropped
//   applyPlan(ids | null, origin)            -> ApplyPlanReport, then getPlan answers with the set again
//   clearPlan(origin)                        -> the set emptied
//   getSpec()                                -> SpecSnapshot { version, source, spec | null }
//
// `status` is the engine's vocabulary and not the page's: proposed, approved, rejected, needs_content,
// applied. The page's own words (kept, dropped, still to be written) are made from it by plan-model, so
// a fixture that spoke the page's words would have proved nothing.
//
// The content is the same Contoso Retail the plan is drawn from, and the tables named in `grounding`
// are real tables of that plan, so a change lands on a card that exists.

const CHANGES = () => [
  {
    id: 'ch-1',
    objectRef: 'Sales[Discount Amount]',
    objectName: 'Sales[Discount Amount]',
    kind: 'set_description',
    source: 'ai',
    category: 'Descriptions',
    risk: 'ai',
    severity: 'medium',
    target: 'description',
    title: 'Describe Discount Amount',
    rationale: 'A described column is one an assistant can answer about.',
    before: 'no description',
    after: 'The money taken off the line before tax.',
    status: 'proposed',
    grounding: { table: 'Sales' },
  },
  {
    id: 'ch-2',
    objectRef: 'Customer[CustomerKey]',
    objectName: 'Customer[CustomerKey]',
    kind: 'set_hidden',
    source: 'bpa',
    category: 'Visibility',
    risk: 'safe',
    severity: 'low',
    target: 'isHidden',
    title: 'Hide the key column',
    rationale: 'A key nobody reports on is noise in the field list.',
    before: 'shown',
    after: 'hidden',
    status: 'proposed',
    grounding: { table: 'Customer' },
  },
  {
    id: 'ch-3',
    objectRef: 'Sales/Margin %',
    objectName: 'Margin %',
    kind: 'set_format',
    source: 'deterministic',
    category: 'Formatting',
    risk: 'safe',
    severity: 'low',
    target: 'formatString',
    title: 'Format Margin % as a percentage',
    rationale: 'It reads as 0.184 in every report today.',
    before: 'General',
    after: '0.0%',
    status: 'proposed',
    grounding: { table: 'Sales' },
  },
];

/**
 * The change set, as getPlan answers it.
 *
 * Options, each a state the page has to draw:
 *   none        no set at all. The section and the chip are both absent, which is the ordinary model.
 *   dropped     one row rejected, so "Apply what is kept" counts two and the dropped row is dimmed.
 *   applied     every kept row written. The rows stay as a record and none of them is work any more.
 *   towrite     one row the engine says still needs a value. It cannot be kept and says why.
 *   empty       a real set with no rows, which happens when nothing could be included; the note is the
 *               only thing that explains it, so the section still draws.
 */
export function makeChanges(options = {}) {
  if (options.none) return { revision: 41, items: [], summary: emptySummary() };
  const items = clone(CHANGES());
  if (options.dropped) items[1].status = 'rejected';
  if (options.towrite) { items[0].status = 'needs_content'; items[0].after = null; }
  if (options.applied) items.forEach((row) => { row.status = 'applied'; });
  const set = {
    planId: 'cp-contoso-1',
    scope: 'whole model',
    revision: 41,
    items: options.empty ? [] : items,
    note: options.empty ? 'Nothing could be included: every match is read only in this model.' : undefined,
  };
  set.summary = summaryOf(set.items);
  return set;
}

const emptySummary = () => ({
  total: 0, deterministic: 0, bpa: 0, ai: 0, renames: 0, needsContent: 0, approved: 0, rejected: 0,
  applied: 0, unverified: 0,
});

function summaryOf(items) {
  const count = (fn) => items.filter(fn).length;
  return {
    total: items.length,
    deterministic: count((i) => i.source === 'deterministic'),
    bpa: count((i) => i.source === 'bpa'),
    ai: count((i) => i.source === 'ai'),
    renames: count((i) => i.kind === 'rename'),
    needsContent: count((i) => i.status === 'needs_content'),
    approved: count((i) => i.status === 'approved'),
    rejected: count((i) => i.status === 'rejected'),
    applied: count((i) => i.status === 'applied'),
    unverified: 0,
  };
}

/** setPlanItem. `approved` true keeps a row, false drops it. A row the engine says still needs a value
 *  is REFUSED the way the engine refuses it, because the page must not offer Keep on one of those. */
export function changeDecide(previous, id, after, approved) {
  const set = clone(previous);
  const row = set.items.find((r) => r.id === id);
  if (!row) return set;
  if (row.status === 'applied') return set;
  if (row.status === 'needs_content' && after == null) return set;
  if (after != null) row.after = after;
  if (approved === true) row.status = 'approved';
  else if (approved === false) row.status = 'rejected';
  set.summary = summaryOf(set.items);
  return set;
}

/** applyPlan. Writes the kept rows and answers with the engine's own three counts. */
export function changeApply(previous, ids) {
  const set = clone(previous);
  const wanted = Array.isArray(ids) && ids.length > 0
    ? set.items.filter((r) => ids.includes(r.id) && r.status === 'approved')
    : set.items.filter((r) => r.status === 'approved');
  wanted.forEach((row) => { row.status = 'applied'; });
  set.summary = summaryOf(set.items);
  return {
    set,
    report: {
      revision: 42,
      appliedCount: wanted.length,
      skippedCount: 0,
      failedCount: 0,
      items: clone(wanted),
      bpaViolationsBefore: 12,
      bpaViolationsAfter: 12 - wanted.length,
      gradeBefore: 'B',
      gradeAfter: 'A',
      overallBefore: 78,
      overallAfter: 84,
      note: 'Applied as one undoable step.',
    },
  };
}

/** clearPlan: the set is gone, and getPlan answers with one that has no id. */
export function changeClear() {
  return { revision: 43, items: [], summary: emptySummary() };
}

/**
 * getSpec, as the Model Spec's own snapshot. `spec: null` is a model with no draft, which is what keeps
 * the banner off the page; a draft is a real ModelSpec, so the banner's counts are counted and not told.
 */
export function makeSpec(options = {}) {
  if (options.none) return { version: 0, source: 'manual', spec: null };
  return {
    version: 3,
    source: 'autogenerate-model',
    spec: {
      name: 'Contoso Retail (next)',
      compatibilityLevel: 1604,
      storageMode: 'import',
      tables: [
        { name: 'Sales', role: 'fact', columns: [{ name: 'Order Number', dataType: 'String' }, { name: 'Amount', dataType: 'Decimal' }] },
        { name: 'Customer', role: 'dimension', columns: [{ name: 'CustomerKey', dataType: 'Int64', isKey: true }, { name: 'Name', dataType: 'String' }] },
        { name: 'Product', role: 'dimension', columns: [{ name: 'ProductKey', dataType: 'Int64', isKey: true }] },
        { name: 'Date', role: 'date', columns: [{ name: 'Date', dataType: 'DateTime' }] },
      ],
      relationships: [
        { fromTable: 'Sales', fromColumn: 'CustomerKey', toTable: 'Customer', toColumn: 'CustomerKey' },
        { fromTable: 'Sales', fromColumn: 'ProductKey', toTable: 'Product', toColumn: 'ProductKey' },
      ],
      measures: [
        { table: 'Sales', name: 'Total Sales', dax: 'SUM ( Sales[Amount] )' },
        { table: 'Sales', name: 'Order Count', dax: 'DISTINCTCOUNT ( Sales[Order Number] )' },
      ],
      timeIntelligence: [],
    },
  };
}
