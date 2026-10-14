// Acceptance driver for the jev/hier-consent lane: permission to send this model's details for advice.
//
// Every shot is the REAL Studio and the REAL Connections hub, driven through the mock engine, not a
// drawing. What is being proven is the thing F-176 said did not exist: a person using VS Code can give
// permission, take it back, and see and change what Pro decides, without ever opening a terminal.
//
// WHAT IS PROVEN HERE: that the card is raised by a page whose Pro answer did not arrive, that OK sends
// the engine's OWN declared field list and asks the page's question again, that Not now records nothing
// and leaves the page exactly as it was, that the Connections line shows all three states, that the
// Settings switches are built from the registry and not from a list in the page, and that a free session
// sees none of it.
//
// WHAT IS NOT: that the ENGINE stores or refuses anything. This harness fakes the engine. The engine half
// is proven in Semanticus.Tests/DecisionConsentTests.cs, and test/rpc-name-parity.test.mjs proves the
// names on both sides are the same names.
import { createServer } from 'node:http';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { findBrowser, requireSupportedNode } from './browser.mjs';
import { checkScreen, screenFailures } from './screencheck.mjs';
import { applyTheme } from './themes.mjs';
requireSupportedNode();
import { createRequire } from 'node:module';
const require_ = createRequire(import.meta.url);
const puppeteer = require_('puppeteer-core').default ?? require_('puppeteer-core');

const here = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(here, '..', '..');            // Semanticus.VSCode
const OUT = process.env.OUTDIR || resolve(webRoot, 'shots', 'consent');
mkdirSync(OUT, { recursive: true });
const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.map': 'application/json' };
const server = createServer((req, res) => {
  const url = req.url || '/';
  const p = normalize(join(webRoot, decodeURIComponent(url.split('?')[0].split('#')[0])));
  if (!p.startsWith(webRoot) || !existsSync(p) || statSync(p).isDirectory()) { res.writeHead(404); res.end('nf'); return; }
  res.writeHead(200, { 'content-type': TYPES[extname(p).toLowerCase()] || 'application/octet-stream' });
  res.end(readFileSync(p));
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;
const executablePath = process.env.SEMANTICUS_BROWSER || findBrowser();
// Chromium's own scratch, on real disk. /tmp on this machine is a tmpfs other work fills, and a failed
// write there reads exactly like a component fault and is not one.
const SCRATCH = resolve(process.env.HOME ?? '/tmp', '.cache', 'semanticus-uishot', `consent-${process.pid}`);
mkdirSync(SCRATCH, { recursive: true });
const browser = await puppeteer.launch({
  executablePath, headless: true, protocolTimeout: 180000,
  args: ['--no-sandbox', '--disable-gpu', '--disable-dev-shm-usage', '--force-color-profile=srgb', '--hide-scrollbars',
    `--user-data-dir=${SCRATCH}`],
  env: { ...process.env, TZ: 'UTC' },
});
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const LABEL = process.env.SHOTLABEL || 'after';
// SHOTTHEME=dark|light|hc repaints the page in that VS Code theme before the picture is taken.
const SHOTTHEME = process.env.SHOTTHEME || null;
const ONLY = (process.env.SCENES || '').split(',').map((x) => x.trim()).filter(Boolean);

// ---- the engine, as this lane needs it ------------------------------------------------------------
// Five registered points, copied from Semanticus.Core/Decisions/DecisionRegistry.cs rather than invented, because the
// page's whole claim is that it says what the registry says: a made-up purpose here would let the page print a label of
// its own and still look right. Only measure.folder_and_format is word for word. The others are SHORTENED, so a shot
// shows less than the product does for them: plan.rank and ask.route leave off the purpose's last sentence, date.table
// is a shorter version of its purpose, and compose.draft carries an older, shorter purpose and only the first of its two
// fields. No scene checks a purpose's words; they check the page's own lines and that the switches are read from the
// registry (getDecisionPoints).
const POINTS = `[
  {
    name: 'measure.folder_and_format', caller: 'propose_measure_defaults', enabled: true, proOnly: true,
    purpose: 'Propose a display folder and a number format for a measure that was created without either. '
      + "It sends the measure's name, facts worked out from its formula on this computer (which "
      + 'functions it uses, whether it divides, counts or adds up an amount of money, and whether it '
      + "uses a date column or compares time periods), the names of the measures it uses, its table's name and column names and types, "
      + 'and the names, folders and formats of the other measures on that table. It never sends the '
      + 'formula or any value written in it. It never writes anything.',
    fields: ['measure.folder_and_format:measure_name', 'measure.folder_and_format:measure_formula_facts',
      'measure.folder_and_format:references_measures',
      'measure.folder_and_format:table_name', 'measure.folder_and_format:table_columns',
      'measure.folder_and_format:sibling_measures'],
  },
  {
    name: 'plan.rank', caller: 'get_model_plan', enabled: true, proOnly: true,
    purpose: 'Put the plan for this model in value order. It sends the shape of the model (how many '
      + 'tables, columns and measures it has and whether it is connected) and, for each thing on '
      + 'the plan, its heading, its one line, which goal it serves, who would do it, how many '
      + 'objects it touches and the plain reasons the rules gave for it. It sends no formulas, no '
      + 'rows and no values. It changes the ORDER and nothing else.',
    fields: ['plan.rank:model_shape', 'plan.rank:plan_items'],
  },
  {
    name: 'ask.route', caller: 'route_ask', enabled: false, proOnly: true,
    purpose: 'Work out which part of the product answers what a person typed into the box. It sends '
      + 'the words they typed and nothing else: no model, no names, no formulas, no values. It '
      + 'picks one of the twenty places in the product, or none of them.',
    fields: ['ask.route:ask_text'],
  },
  {
    // Switched on and never asked: its replay bar is unmet, so the engine reports live: false (N1). The switch
    // must not read On over a point that does nothing.
    name: 'compose.draft', caller: 'compose_task', enabled: true, live: false, proOnly: true,
    purpose: 'Choose the steps for a task or a workflow from the words a person typed. It sends those words, '
      + 'the step templates Semanticus knows and the names of the tables and partitions they could work on.',
    fields: ['compose.draft:draft_words'],
  },
  {
    // A point this build never asks: its replay bar is unmet, so the engine reports live false and the switch is
    // dead however the person left "enabled". date.table is the live example (T268, M4 point 3, switched off on
    // 27 Sep); the fixture keeps enabled true on purpose, because live false alone has to hold the switch off.
    name: 'date.table', caller: 'get_model_plan', enabled: true, live: false, proOnly: true,
    purpose: 'Choose which table is the date table of a model whose formulas use time intelligence and that has '
      + "no marked date table, or whether to add a new one. It sends the names of the model's tables and the "
      + 'names and types of their columns. It never sends a value out of your data.',
    fields: ['date.table:model_tables', 'date.table:model_facts'],
  }
]`;

// A plan that came back UNRANKED, which is what a Pro session without permission really gets: the whole
// plan, in rule order, with the engine's own line saying why. Nothing is hidden and nothing is invented.
const UNRANKED_PLAN = `{
  modelName: 'Contoso Retail', items: [], history: [], tables: [], ranked: false,
  orderedBy: 'In the order the rules found them, because you have not given permission to send this '
    + "model's details for advice.",
}`;
// A saved model's plan after its first edits: the Save row, and the cleanup, which is always optional and waits for the
// rows it conflicts with (the state the engine produces; its collapsed line still says Optional, L1).
const SAVE_PLAN = `{
  modelName: 'Contoso Consulting', history: [], tables: [], ranked: false,
  orderedBy: 'In the order the rules found them, because you have not given permission to send this '
    + "model's details for advice.",
  items: [
    { id: 'ship/unsaved', section: 'Ship', title: 'Save the changes you have made', sub: 'they are only in this session',
      value: 'nothing is lost', who: 'you', status: 'ready', why: ['This model has changes that are not saved yet.'],
      diff: [], touches: 'Contoso Consulting', jump: { tool: 'history', label: 'History' },
      source: 'found', doer: 'you', stateWord: 'needs_you', state: 'gap' },
    { id: 'cleanup/model', section: 'Structure', title: 'Remove 2 unused things', sub: 'Optional. Waits for: Save the changes you have made',
      waitsFor: [{ id: 'ship/unsaved', title: 'Save the changes you have made' }],
      value: 'a smaller, clearer model', who: 'you', status: 'ready', optional: true,
      why: ["Optional. No report's use of this model has been read yet, so Semanticus cannot tell whether a report shows these. Nothing else on the plan waits for this."],
      diff: ['- column dim_client[INDUSTRY_CD]', '- column dim_client[REGION_CD]'], touches: '2 objects',
      jump: { tool: 'lineage', label: 'Lineage' }, source: 'found', doer: 'you', stateWord: 'waiting', state: 'gap' },
  ],
}`;
const RANKED_PLAN = `{
  modelName: 'Contoso Retail', items: [], history: [], tables: [], ranked: true,
  orderedBy: 'In value order, highest first.',
}`;

/**
 * The seed. `state` is the permission this session starts with: 'none', 'on', or 'unsaved' (a model that
 * has never been saved, which the engine reports as a permission that applies to nothing).
 */
const seed = (state) => `
window.__consentCalls = [];
// The harness does not answer the host's first-run and guide questions, so the page waited its 5 s for them before it asked
// about permission and every scene read the page too early. Answered here: a connected person, no tour.
{
  let realApi;
  Object.defineProperty(window, 'acquireVsCodeApi', { configurable: true,
    get: () => function () {
      const api = realApi(); const post = api.postMessage;
      const reply = (m) => setTimeout(() => window.dispatchEvent(new MessageEvent('message', { data: m })), 0);
      api.postMessage = function (msg) {
        const t = msg && msg.type;
        if (t === 'getFirstRun') { reply({ type: 'firstRun', id: msg.id, show: false, connected: true }); return; }
        if (t === 'getGuideFlags') { reply({ type: 'guideFlags', id: msg.id, planTour: true, whatChanged: true }); return; }
        return post.call(this, msg);
      };
      return api;
    },
    set: (v) => { realApi = v; } });
}
const __note = (name, params) => window.__consentCalls.push({ call: name, params: params || [] });
const __fieldsInForce = ${POINTS}.flatMap((p) => p.fields).sort();
let __consent = ${JSON.stringify(state)} === 'on'
  ? { granted: true, current: true, schemaVersion: 6, schemaVersionInForce: 6,
      fields: __fieldsInForce, fieldsInForce: __fieldsInForce,
      grantedUtc: '2026-09-21T09:15:00Z', withdrawnUtc: null, appliesTo: 'disk:4f2a91bc#8e07d3aa', reason: null }
  : ${JSON.stringify(state)} === 'unsaved'
    ? { granted: false, current: false, schemaVersion: 0, schemaVersionInForce: 6,
        fields: [], fieldsInForce: __fieldsInForce, grantedUtc: null, withdrawnUtc: null, appliesTo: null,
        reason: 'Save this model, or open it live, before agreeing to send its details for advice.' }
  : ${JSON.stringify(state)} === 'old'
    // Agreed under the version 3 card, which did not name the words a person types to describe a task or a workflow.
    // The engine reports that as granted but not current, for the same model, and the card comes back once.
    ? { granted: true, current: false, schemaVersion: 3, schemaVersionInForce: 6,
        fields: ['measure.folder_and_format:measure_expression'], fieldsInForce: __fieldsInForce,
        grantedUtc: '2026-09-21T09:15:00Z', withdrawnUtc: null, appliesTo: 'disk:4f2a91bc#8e07d3aa',
        reason: 'the list of details that would be sent has changed since you agreed, so permission is needed again' }
    : { granted: false, current: false, schemaVersion: 0, schemaVersionInForce: 6,
        fields: [], fieldsInForce: __fieldsInForce, grantedUtc: null, withdrawnUtc: null,
        appliesTo: 'disk:4f2a91bc#8e07d3aa',
        reason: "you have not given permission to send this model's details for advice" };
let __points = ${POINTS};
// A scene can change what the engine answers mid-scene: another model opened by the other door, or a switch the
// engine says was decided by something other than this model's own switch.
window.__mapPoints = (fn) => { __points = __points.map(fn); };
// Which model is open, as the engine would say. A scene opens another one with __openModel, which changes what
// sessionInfo answers, and a switch sent for the model that is no longer open is refused as the engine refuses it.
let __openSession = null;
window.__openModel = (id, name) => {
  const h = window.__uishotHarness;
  const base = h.response('sessionInfo');
  __openSession = id;
  h.setResponse('sessionInfo', Object.assign({}, base, { sessionId: id, modelName: name }));
};
let __h; Object.defineProperty(window, '__uishotHarness', { configurable: true,
  get: () => __h,
  set: (v) => {
    __h = v;
    v.setResponse('getConsent', () => { __note('getConsent'); return __consent; });
    v.setResponse('setConsent', (params) => {
      __note('setConsent', params);
      // Astra 2: the answer names the model it is an answer ABOUT. A grant that arrives naming a
      // different model than the one open is refused by the engine, and the fixture refuses it the same
      // way so a card that stopped carrying the scope fails here instead of looking fine.
      if (params[3] && params[3] !== __consent.appliesTo) {
        return { __uishotError: 'That answer was about a model that is not the model open now, so nothing was changed.' };
      }
      __consent = Object.assign({}, __consent, {
        granted: true, current: true, schemaVersion: params[1], fields: params[0],
        grantedUtc: '2026-09-22T10:00:00Z', withdrawnUtc: null, reason: null,
      });
      return __consent;
    });
    v.setResponse('withdrawConsent', () => {
      __note('withdrawConsent');
      __consent = Object.assign({}, __consent, {
        granted: false, current: false, withdrawnUtc: '2026-09-22T10:05:00Z',
        reason: "you withdrew permission to send this model's details for advice",
      });
      return __consent;
    });
    v.setResponse('getDecisionPoints', () => { __note('getDecisionPoints'); return __points; });
    v.setResponse('setDecisionPointEnabled', (params) => {
      __note('setDecisionPointEnabled', params);
      if (__openSession && params[3] && params[3] !== __openSession) {
        return { __uishotError: 'That switch was for a model that is no longer open, so nothing was changed. Look at the switches again for the model open now.' };
      }
      __points = __points.map((p) => (p.name === params[0] ? Object.assign({}, p, { enabled: params[1] }) : p));
      return __points;
    });
    // The month is the relay's, in the relay's own units. 41 thousand of 30 million is the mockup's
    // number, so a shot can be held beside the mockup and compared word for word.
    v.setResponse('getDecisionBudget', () => {
      __note('getDecisionBudget');
      return { monthlyUsedInputTokens: 41000, monthlyCapInputTokens: 30000000, monthlyReason: null };
    });
    // The plan answers according to the permission that is CURRENT when it is asked, so pressing OK and
    // the page asking again is a real before-and-after rather than two fixed answers.
    v.setResponse('getModelPlan', () => {
      __note('getModelPlan');
      if (window.__savePlan) return ${SAVE_PLAN};
      return __consent.current ? ${RANKED_PLAN} : ${UNRANKED_PLAN};
    });
  } });`;

// ---- the scenes -----------------------------------------------------------------------------------
//
// EVERY SCENE STATES WHAT IT MUST SHOW, and the run fails when one does not (Astra 6, 2026-09-22). The
// first version of this driver recorded what it saw and exited zero whatever that was, so a card that
// never appeared, a setConsent that failed, or a scene that threw all came back as a successful run
// with an interesting-looking results file. A shot nobody has to justify cannot verify anything.
//
// `must` is given the measurement the page produced and returns the list of things that are wrong with
// it. Empty means the scene proved what it promised. A scene with no `must` is refused before the
// browser starts, so a scene cannot be added without saying what it is for.
const JOBS = [
  { slug: 'card', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'none',
    desc: 'the card, raised over the Plan page whose value order did not run',
    must: (s) => [
      !s.card && 'the card was not raised over a Plan whose value order did not run',
      s.cardTitle !== 'Let Pro decide for this model?' && `the title read ${JSON.stringify(s.cardTitle)}`,
      s.cardLists.length !== 12 && `the two lists should carry twelve lines between them (nine that go, three that never go), saw ${s.cardLists.length}`,
      !s.cardLists.includes('how a data question was read, in the words of the question, with no values or dates') && 'the card does not say how a data question was read goes (the stated reading, Jev data questions v2)',
      !s.cardLists.includes('the questions you type into the ask box') && 'the card does not say typed questions go (F-231)',
      !s.cardLists.includes('the words you type to describe a task or a workflow') && 'the card does not say described words go (F-231, lane L5)',
      !s.cardLists.includes('the words of your data questions') && 'the card does not say the words of data questions go (query.summarize, verify-askdata defect 7)',
      !s.cardLists.includes('measure and column descriptions, when you ask about your data') && 'the card does not say descriptions go with a data question (query.summarize, verify-askdata-r2 R3)',
      !s.calls.includes('getConsent') && 'the page never asked the engine whether permission was missing',
    ],
  },
  { slug: 'after-ok', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'none', press: ['consent-ok'],
    desc: 'after OK: the card is gone, permission is given, and the page asked its question again',
    must: (s) => [
      s.card && 'the card is still standing after OK',
      !s.setConsentFields && 'OK did not grant permission',
      // Twelve: the three live points' nine, compose.draft's one and date.table's two. Permission covers every declared field,
      // live or not, so a point that turns live later is not asked about again.
      s.setConsentFields?.length !== 12 && `OK sent ${s.setConsentFields?.length} fields, not the twelve the points declare`,
      s.setConsentVersion !== 6 && `OK sent version ${s.setConsentVersion}`,
      !s.setConsentScope && 'the answer did not name the model it was an answer about (Astra 2)',
      s.planReads < 2 && 'the page did not ask its question again after permission was given',
    ],
  },
  { slug: 'card-reshown', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'old', press: ['consent-ok'],
    desc: 'agreed under the version 3 card: the new card is shown once more, and OK agrees at version 6 for the same model',
    must: (s) => [
      !s.calls.includes('setConsent') && 'the card did not come back for a permission given under the old card',
      s.setConsentVersion !== 6 && `OK sent version ${s.setConsentVersion}`,
      s.setConsentScope !== 'disk:4f2a91bc#8e07d3aa' && `the new answer was not kept for the same model: ${s.setConsentScope}`,
      s.card && 'the card is still standing after OK',
    ],
  },
  { slug: 'after-not-now', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'none', press: ['consent-not-now'],
    desc: 'after Not now: nothing recorded, the plan still in rule order with its quiet line',
    must: (s) => [
      s.card && 'the card is still standing after Not now',
      s.setConsentFields && 'Not now recorded something, and it must record nothing',
      s.calls.includes('setConsent') && 'Not now called setConsent',
    ],
  },
  // A NEW MODEL'S FIRST SAVE (UAT plan statement check, 28 Sep). Before the save there is nothing to keep permission
  // under, so no card; the save gives the model an identity and rides the change bus, and the card must come then.
  // It did not: the page's check ran once, stopped at "never saved", and was never run again.
  // Hero 20 D10: the assistant opens a model and nobody has decided about Jev for it. No page is waiting on a Pro answer (this is
  // Review), and the card comes anyway, once, without turning anything on.
  { slug: 'card-on-open', q: 'tab=bpa&tier=pro&conn=1&live=1', state: 'none',
    desc: 'Studio open on Review with a model nobody has decided about: the card is up, and nothing was granted',
    must: (s) => [
      !s.card && 'the card was not raised for an undecided model on a page that waits for no Pro answer',
      s.cardTitle !== 'Let Pro decide for this model?' && `the title read ${JSON.stringify(s.cardTitle)}`,
      s.calls.includes('setConsent') && 'permission was turned on without a press',
    ],
  },
  { slug: 'card-after-assistant-open', q: 'tab=bpa&tier=pro&conn=1&live=1', state: 'on', act: 'assistantOpens',
    desc: 'the assistant opens another model while Studio is on Review: the card comes for it, and no grant is made',
    must: (s) => [
      s.cardBefore && 'the card was up before the assistant opened the model, so this proves nothing',
      !s.card && 'the card did not come after the assistant opened a model nobody had decided about',
      s.calls.includes('setConsent') && 'permission was turned on without a press',
    ],
  },
  { slug: 'card-after-first-save', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'unsaved', act: 'firstSave',
    desc: 'a new model: no card before its first save, the card right after it',
    must: (s) => [
      s.cardBefore && 'the card was raised for a model that had never been saved',
      !s.card && 'the card was not offered after the model was first saved',
      s.cardTitle !== 'Let Pro decide for this model?' && `the card that appeared reads ${JSON.stringify(s.cardTitle)}`,
    ],
  },
  { slug: 'line-after-first-save', q: 'tab=plan&tier=pro&conn=1&live=1', menu: true, state: 'unsaved', act: 'firstSave',
    desc: 'What Pro decides after a first save: the line offers Turn on instead of "save this model first"',
    must: (s) => [
      s.neverSaved && 'the line still says the model has never been saved, after it was saved',
      !s.turnOn && 'there is no way to turn permission on after the first save',
      // Verify, 28 Sep, L6: the card the save raised opened on top of this panel, two dialogs at once.
      s.card && s.panel && 'the permission card opened on top of the What Pro decides panel: two dialogs at once',
    ],
  },
  // L6, the other half: the card that waited for the panel is offered once the person closes the panel, so a new
  // model's first save still gets its card (the bug card-after-first-save fixed must not come back by the side door).
  { slug: 'card-after-panel-closes', q: 'tab=plan&tier=pro&conn=1&live=1', menu: true, state: 'unsaved', act: 'firstSave',
    press: ['decisions-close'],
    desc: 'a first save while What Pro decides is open: the card waits, and comes once the panel is closed',
    must: (s) => [
      s.twoDialogs && 'the permission card opened on top of the What Pro decides panel: two dialogs at once',
      s.panel && 'Close did not close the What Pro decides panel',
      !s.card && 'the card that waited for the panel was never offered after the panel closed',
    ],
  },
  // Verify, 28 Sep, L8: permission given on the OTHER door while the card is up. The card's question is answered, so
  // it closes by itself and the page asks its question again; it used to stay, and OK then refused.
  { slug: 'card-granted-elsewhere', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'none', act: 'grantElsewhere',
    desc: 'permission given by the assistant while the card is up: the card closes itself and the Plan asks again',
    must: (s) => [
      !s.cardBefore && 'the card was not up before the other door gave permission, so this proves nothing',
      s.card && 'the card is still asking for a permission the other door already gave',
      s.calls.includes('setConsent') && 'the card granted permission itself; the other door had already done it',
      s.planReads < 2 && 'the Plan did not ask its question again once permission was there',
    ],
  },
  // Verify, 28 Sep, L7: check_reports and set_report_scope move no revision and carry no deltas; the engine still
  // publishes a change (LocalEngine.ReportScope.cs, PublishScopeChanged). The Plan must read again on it.
  { slug: 'plan-reads-on-scope-change', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'on', act: 'scopeChange',
    desc: 'reports read on the other door: a change with no deltas and the same revision, and the Plan reads again',
    must: (s) => [
      s.planReads < 2 && `the Plan did not read again on a report reading from the other door (reads: ${s.planReads})`,
    ],
  },
  // The Plan's Save row SAVES (it was Choose, which opened History and saved nothing). The cleanup beside it on a new
  // model says Optional.
  { slug: 'plan-save-row', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'on', savePlan: true, press: ['plan-act-save'],
    desc: 'the Save row: its button is Save, and pressing it asks the host to save the model',
    must: (s) => [
      s.saveLabel !== 'Save' && `the Save row's button reads ${JSON.stringify(s.saveLabel)}`,
      s.saveMessages !== 1 && `pressing Save should ask the host to save once, saw ${s.saveMessages}`,
      s.planGo && 'pressing Save opened another page',
      !/^Optional/.test(s.cleanupSub || '') && `the optional cleanup does not say so: ${JSON.stringify(s.cleanupSub)}`,
    ],
  },
  { slug: 'card-free', q: 'tab=plan&conn=1&live=1', state: 'none',
    desc: 'a free session: no card at all, because the Pro pill already stands there',
    must: (s) => [
      s.card && 'a free session was shown the permission card',
      s.calls.includes('getConsent') && 'a free session asked the engine about permission at all',
    ],
  },
  // The permission line lived under Connections > Current setup until slice C, which cut it from Connections.
  // It now sits at the top of Your assistant > What Pro decides, reached the way a person reaches it: the chip,
  // then the Settings row. The old conn-free scene is folded into settings-free below.
  { slug: 'line-on', q: 'tab=plan&tier=pro&conn=1&live=1', menu: true, state: 'on',
    desc: 'What Pro decides: permission on, with the date and a way to take it back',
    must: (s) => [
      !s.line && 'there is no permission line in What Pro decides',
      s.lineState !== 'Pro advice for this model: on since Sep 21' && `the line read ${JSON.stringify(s.lineState)}`,
      !s.withdraw && 'there is no way to take permission back',
    ],
  },
  { slug: 'line-off', q: 'tab=plan&tier=pro&conn=1&live=1', menu: true, state: 'none',
    desc: 'What Pro decides: permission off, with a way to raise the card',
    must: (s) => [
      !s.line && 'there is no permission line in What Pro decides',
      s.lineState !== 'Pro advice for this model: off' && `the line read ${JSON.stringify(s.lineState)}`,
      !s.turnOn && 'there is no way to turn permission on',
    ],
  },
  { slug: 'line-turn-on', q: 'tab=plan&tier=pro&conn=1&live=1', menu: true, state: 'none', press: ['consent-turn-on'],
    desc: 'Turn on, from What Pro decides, really raises the card above the panel',
    must: (s) => [
      !s.card && 'Turn on did not raise the permission card',
      s.cardTitle !== 'Let Pro decide for this model?' && `the card that appeared reads ${JSON.stringify(s.cardTitle)}`,
    ],
  },
  { slug: 'line-withdrawn', q: 'tab=plan&tier=pro&conn=1&live=1', menu: true, state: 'on', press: ['consent-withdraw'],
    desc: 'Withdraw pressed: the same line, now off',
    must: (s) => [
      !s.calls.includes('withdrawConsent') && 'Withdraw did not reach the engine',
      s.lineState !== 'Pro advice for this model: off' && `the line still reads ${JSON.stringify(s.lineState)}`,
      !s.turnOn && 'after withdrawing there is no way back',
    ],
  },
  { slug: 'line-unsaved', q: 'tab=plan&tier=pro&conn=1&live=1', menu: true, state: 'unsaved',
    desc: 'a model that has never been saved: the sentence instead of a control',
    must: (s) => [
      !s.neverSaved && 'a model that has never been saved was not told why it cannot be granted',
      s.withdraw && 'a never-saved model was offered Withdraw',
      s.turnOn && 'a never-saved model was offered Turn on, which could do nothing',
    ],
  },
  { slug: 'settings', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'on', menu: true,
    desc: 'Your assistant, Settings: one switch per registered point, and the month',
    must: (s) => [
      !s.panel && 'the Your assistant menu did not open the settings panel',
      s.switches.length !== 5 && `expected one switch per registered point, saw ${s.switches.length}`,
      s.switches.some((t) => /_/.test(t)) && 'a switch is labelled with an id instead of plain words',
      // N1: a point this build never asks reads as not on yet, its switch off and disabled, however it is set.
      s.switchStates[3] !== 'false' && `a point that is not live showed its switch as ${s.switchStates[3]}`,
      s.switchDisabled[3] !== true && 'a point that is not live could still be switched',
      !/Not on yet in this version\./.test(s.switches[3] || '') && 'a point that is not live did not say so',
      s.switchDisabled[0] !== false && 'a live point could not be switched',
      s.month !== 'This month: 41 thousand of 30 million' && `the month read ${JSON.stringify(s.month)}`,
      !s.calls.includes('getDecisionPoints') && 'the switches were not read from the registry',
    ],
  },
  { slug: 'settings-sent', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'on', menu: true, press: ['decisions-fold'],
    desc: 'What is sent, opened: each point says in its own words what it sends, and a point that is off says so',
    must: (s) => [
      !s.sentOpen && 'What is sent did not open',
      s.switches.length !== 5 && `expected five points in the fold, saw ${s.switches.length}`,
      // The rest of a row is written in the present tense, so a point that is not on has to say nothing is sent
      // for it. Without this the fold claimed a switched-off point sends things (round 3, DT5).
      s.sentNotLive.length !== 2
        && `expected exactly two points in the fold to say nothing is sent (compose.draft and date.table), saw ${s.sentNotLive.length}`,
      s.sentNotLive[0] && !/nothing is sent/.test(s.sentNotLive[0])
        && `the fold's not-on line reads ${JSON.stringify(s.sentNotLive[0])}`,
    ],
  },
  { slug: 'settings-not-live', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'on', menu: true,
    desc: 'a point this build never asks: said plainly, switch off and unpressable however it was left',
    must: (s) => [
      s.notLive.length !== 2 && `expected exactly two not-on points, saw ${s.notLive.length}`,
      s.notLive[0] !== 'Not on yet in this version.' && `the not-on line reads ${JSON.stringify(s.notLive[0])}`,
      // The fixture leaves this point enabled, so an off switch here is live false doing the work.
      [3, 4].some((i) => s.switchStates[i] !== 'false') && `a not-on switch reads ${s.switchStates[3]} and ${s.switchStates[4]}, not off`,
      [3, 4].some((i) => !s.switchDisabled[i]) && 'a not-on switch can be pressed',
    ],
  },
  { slug: 'settings-toggled', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'on', menu: true, press: [{ testid: 'decisions-switch', nth: 1 }],
    desc: 'one switch turned off, which is one call to the engine and no local state',
    must: (s) => [
      s.enabledCalls.length !== 1 && `a toggle should be exactly one call, saw ${s.enabledCalls.length}`,
      s.switchStates[1] !== 'false' && `the switch did not turn off, it reads ${s.switchStates[1]}`,
      s.switchStates[0] !== 'true' && 'toggling one switch moved another',
    ],
  },
  // The switch-scope verifier, 28 Sep (LOW 9, and the notes of round 2): a row says so when something other than this
  // model's own switch decided it, in the engine's own words.
  { slug: 'settings-note', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'on', menu: true,
    setup: () => window.__mapPoints((p, i) => (i === 0 ? Object.assign({}, p, { enabled: false,
      note: 'Off because it was switched off for a Power BI Desktop file with the same name. Desktop does not say whether that was this file.' }) : p)),
    desc: 'a switch decided by another Desktop file with the same name says so under its label',
    must: (s) => [
      s.notes.length !== 1 && `expected one note, saw ${s.notes.length}`,
      s.notes[0] !== 'Off because it was switched off for a Power BI Desktop file with the same name. Desktop does not say whether that was this file.'
        && `the note read ${JSON.stringify(s.notes[0])}`,
      s.switchStates[0] !== 'false' && 'the switch the note explains does not read off',
    ],
  },
  // LOW 9: the panel read the switches once. When the other door opened another model while it was up, a click set the
  // new model's switch from the last model's rows. Model B has every point on, so ask.route (off for model A) reads on
  // again, and switching it is switching it OFF.
  { slug: 'settings-model-swap', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'on', menu: true,
    // The didChange below is the one the engine sends when the assistant opens a model (LocalEngine.PublishModelSwapped,
    // pinned over the real pipe by DecisionSwitchScopeVerifyR2Tests and DecisionSwitchScopeTests): the new model's id,
    // origin system, no deltas. Before round 3 the engine sent nothing, and this scene dispatched a message it never sent.
    after: () => {
      window.__openModel('uishot-model-b', 'Model B');
      window.__mapPoints((p) => Object.assign({}, p, { enabled: true }));
      window.__uishotHarness.dispatch({ type: 'didChange', payload: { sessionId: 'uishot-model-b', revision: 0, origin: 'system', label: 'Opened Model B', deltas: [], undo: { canUndo: false, canRedo: false, steps: 0, redoSteps: 0 } } });
    },
    pressAfter: [{ testid: 'decisions-switch', nth: 2 }],
    desc: 'another model opened while the panel is up: the rows are read again, and a click sets the new model from them',
    must: (s) => [
      s.pointReads < 2 && 'the panel did not read the switches again when another model was opened',
      JSON.stringify(s.enabledCalls) !== JSON.stringify([['ask.route', false, 'human', 'uishot-model-b']])
        && `the click after the swap sent ${JSON.stringify(s.enabledCalls)}, not ask.route off for the model now open`,
    ],
  },
  // The click that beats the news: another model is open, but the change saying so has not reached the panel yet. The
  // click names the model its rows were read for, the engine refuses it, and the rows are read again.
  { slug: 'settings-model-swap-unheard', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'on', menu: true,
    after: () => {
      window.__openModel('uishot-model-b', 'Model B');
      window.__mapPoints((p) => Object.assign({}, p, { enabled: true }));
    },
    pressAfter: [{ testid: 'decisions-switch', nth: 2 }],
    desc: 'a click on the rows of the last model after another model opened is refused, said plainly, and the rows are read again',
    must: (s) => [
      s.enabledCalls.length !== 1 && `expected one switch call, saw ${s.enabledCalls.length}`,
      s.enabledCalls[0] && s.enabledCalls[0][3] !== 'uishot' && `the click did not name the model its rows were read for: ${JSON.stringify(s.enabledCalls[0])}`,
      !/no longer open, so nothing was changed/.test(s.panelError || '') && `the refusal was not shown, the panel said ${JSON.stringify(s.panelError)}`,
      s.pointReads < 2 && 'the rows were not read again after the refusal',
      s.switchStates[2] !== 'true' && `after the refusal ask.route should read Model B's on, it reads ${s.switchStates[2]}`,
    ],
  },
  // Switch-scope round 4 (LOW 7, M1): the panel names the model its rows are for, and a row says in the engine's words
  // why it is off or what an on really did. The notes are the engine's sentences (DecisionSwitchScopeJoinTests pins the on note).
  { slug: 'settings-model-and-reasons', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'on', menu: true,
    setup: () => {
      window.__openModel('uishot-sales', 'Sales');
      window.__mapPoints((p, i) => (i === 0 ? Object.assign({}, p, { enabled: false, note: 'Off because it was switched off for a Power BI Desktop file with the same name. Desktop does not say whether that was this file.' })
        : i === 1 ? Object.assign({}, p, { enabled: true, note: 'On for every Power BI Desktop file with this name, such as a copy in another folder, because Desktop does not say which file this is. Other Power BI Desktop files that were switched off together with it stay off until you switch them on there.' }) : p));
    },
    desc: 'the panel names the model, and the reason lines read as the engine says them',
    must: (s) => [
      s.modelLine !== 'Switches for this model: Sales' && `the model line read ${JSON.stringify(s.modelLine)}`,
      s.notes.length !== 2 && `expected two reason lines, saw ${s.notes.length}`,
      s.notes[0] !== 'Off because it was switched off for a Power BI Desktop file with the same name. Desktop does not say whether that was this file.' && `the first reason read ${JSON.stringify(s.notes[0])}`,
      s.notes[1] !== 'On for every Power BI Desktop file with this name, such as a copy in another folder, because Desktop does not say which file this is. Other Power BI Desktop files that were switched off together with it stay off until you switch them on there.'
        && `the second reason read ${JSON.stringify(s.notes[1])}`,
      s.notes.some((n) => /\u2014/.test(n)) && 'a reason line has an em dash',
    ],
  },
  // LOW D: sessionInfo failed, so the rows cannot say which model they are for. A click changes nothing and says why.
  { slug: 'settings-no-model-id', q: 'tab=plan&tier=pro&conn=1&live=1', state: 'on', menu: true,
    setup: () => window.__uishotHarness.setResponse('sessionInfo', () => ({ __uishotError: 'the engine did not answer' })),
    pressAfter: [{ testid: 'decisions-switch', nth: 0 }],
    desc: 'a click on rows that cannot name their model sends nothing and says so',
    must: (s) => [
      s.enabledCalls.length !== 0 && `the click reached the engine: ${JSON.stringify(s.enabledCalls)}`,
      !/nothing was changed/.test(s.panelError || '') && `the refusal was not shown, the panel said ${JSON.stringify(s.panelError)}`,
    ],
  },
  { slug: 'settings-free', q: 'tab=plan&conn=1&live=1', state: 'none', menu: true,
    desc: 'a free session: the Pro line instead of switches',
    must: (s) => [
      !s.proPill && 'a free session was not shown the Pro line',
      s.switches.length > 0 && 'a free session was shown switches over nothing',
      s.line && 'a free session was shown a permission line it cannot use',
      s.calls.includes('getConsent') && 'a free session asked the engine about permission at all',
    ],
  },
];

const clickTestid = (spec) => {
  const testid = typeof spec === 'string' ? spec : spec.testid;
  const nth = typeof spec === 'string' ? 0 : (spec.nth || 0);
  const all = [...document.querySelectorAll(`[data-testid="${testid}"]`)].filter((x) => x.getClientRects().length);
  const el = all[nth];
  if (el) el.click();
  return !!el;
};

const noPromise = JOBS.filter((j) => typeof j.must !== 'function').map((j) => j.slug);
if (noPromise.length) {
  console.error(`these scenes say nothing about what they must show, so they cannot prove anything: ${noPromise.join(', ')}`);
  await browser.close();
  server.close();
  process.exit(1);
}

const results = [];
for (const job of JOBS) {
  if (ONLY.length && !ONLY.includes(job.slug)) continue;
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error') errors.push('console: ' + m.text()); });
  await page.setViewport({ width: 1366, height: 900, deviceScaleFactor: 2 });
  let note = '';
  try {
    await page.evaluateOnNewDocument(seed(job.state));
    if (job.savePlan) await page.evaluateOnNewDocument(() => { window.__savePlan = true; });
    await page.goto(`http://127.0.0.1:${port}/tools/uishot/harness.html?${job.q}`, { waitUntil: 'networkidle0', timeout: 30000 });
    await wait(1600);
    if (job.setup) await page.evaluate(job.setup);

    // The menu is the way in that a person uses: the chip in the header, then the named row. Clicking a
    // function on window would prove the panel renders and nothing about whether anybody can reach it.
    if (job.menu) {
      const opened = await page.evaluate(() => {
        const chip = [...document.querySelectorAll('button[aria-haspopup="menu"]')]
          .find((b) => /Your assistant/.test(b.textContent || ''));
        if (chip) chip.click();
        return !!chip;
      });
      if (!opened) note += 'the Your assistant chip was not found; ';
      await wait(500);
      const picked = await page.evaluate(() => {
        const row = [...document.querySelectorAll('[role="menuitem"]')]
          .find((b) => /what Pro decides/i.test(b.textContent || ''));
        if (row) row.click();
        return !!row;
      });
      if (!picked) note += 'the Settings row was not in the menu; ';
      await wait(900);
    }

    // The first save of a new model, as the engine tells it: permission now has a model to belong to, and the save
    // rides the change bus like every other change.
    let cardBefore = false;
    if (job.act === 'firstSave') {
      cardBefore = await page.evaluate(() => !!document.querySelector('[data-testid="consent-card"]'));
      await page.evaluate(() => {
        __consent = Object.assign({}, __consent, { appliesTo: 'disk:9c1e22d0#51ab7f03',
          reason: "you have not given permission to send this model's details for advice" });
        window.dispatchEvent(new MessageEvent('message', { data: { type: 'didChange',
          payload: { sessionId: 'uishot', revision: 7, origin: 'human', label: 'Save', deltas: [] } } }));
      });
      await wait(1200);
    }
    // Were two dialogs up at once at any point after the save? Read before any press, which could close one.
    const twoDialogs = await page.evaluate(() => !!document.querySelector('[data-testid="consent-card"]')
      && !!document.querySelector('[data-testid="decisions-panel"]'));

    // The other door gives permission while the card is up. In the engine a first grant stamps the model, which is an
    // ordinary change on the bus (LocalEngine.Decisions.cs, EnsureConsentIdAsync), so the scope the card was raised
    // about gains its marker and a change notification follows.
    if (job.act === 'grantElsewhere') {
      cardBefore = await page.evaluate(() => !!document.querySelector('[data-testid="consent-card"]'));
      await page.evaluate(() => {
        __consent = Object.assign({}, __consent, {
          granted: true, current: true, schemaVersion: 6, fields: __fieldsInForce, appliesTo: 'disk:4f2a91bc#77aa01ce',
          grantedUtc: '2026-09-22T10:00:00Z', withdrawnUtc: null, reason: null,
        });
        window.dispatchEvent(new MessageEvent('message', { data: { type: 'didChange',
          payload: { sessionId: 'uishot', revision: 8, origin: 'agent', label: 'mark this model so permission can be kept for it',
            deltas: [{ kind: 'update', ref: 'model', property: 'Annotations' }] } } }));
      });
      await wait(1200);
    }

    // The assistant opens another model: a different identity with no permission, and the host's 'reconnected' word for another door.
    if (job.act === 'assistantOpens') {
      cardBefore = await page.evaluate(() => !!document.querySelector('[data-testid="consent-card"]'));
      await page.evaluate(() => {
        __consent = { granted: false, current: false, schemaVersion: 0, schemaVersionInForce: 6, fields: [], fieldsInForce: __fieldsInForce,
          grantedUtc: null, withdrawnUtc: null, appliesTo: 'disk:5d3b17aa#02c4e9f1',
          reason: "you have not given permission to send this model's details for advice" };
        window.__openModel('uishot-assistant-model', 'SmallShop');
        window.dispatchEvent(new MessageEvent('message', { data: { type: 'reconnected', by: 'otherDoor' } }));
      });
      await wait(1500);
    }

    // A report reading on the other door: the engine's PublishScopeChanged, no deltas, the revision unmoved.
    if (job.act === 'scopeChange') {
      await page.evaluate(() => {
        window.dispatchEvent(new MessageEvent('message', { data: { type: 'didChange',
          payload: { sessionId: 'uishot', revision: 0, origin: 'agent', label: 'reports checked', deltas: [] } } }));
      });
      await wait(1200);
    }

    for (const spec of job.press || []) {
      const ok = await page.evaluate(clickTestid, spec);
      if (!ok) note += `action "${JSON.stringify(spec)}" not found; `;
      await wait(900);
    }
    if (job.after) { await page.evaluate(job.after); await wait(900); }
    for (const spec of job.pressAfter || []) {
      const ok = await page.evaluate(clickTestid, spec);
      if (!ok) note += `action "${JSON.stringify(spec)}" not found; `;
      await wait(900);
    }

    // What the page actually SAYS and what it actually CALLED. No scene may be reported as proving
    // something these two do not show.
    const seen = await page.evaluate(() => {
      const txt = (sel) => { const e = document.querySelector(sel); return e ? (e.textContent || '').replace(/\s+/g, ' ').trim() : null; };
      const all = (sel) => [...document.querySelectorAll(sel)].map((e) => (e.textContent || '').replace(/\s+/g, ' ').trim());
      return {
        card: !!document.querySelector('[data-testid="consent-card"]'),
        cardTitle: txt('[data-testid="consent-card"] h2'),
        cardLists: all('[data-testid="consent-card"] li'),
        cardError: txt('[data-testid="consent-error"]'),
        line: !!document.querySelector('[data-testid="consent-line"]'),
        lineState: txt('[data-testid="consent-state"]'),
        neverSaved: txt('[data-testid="consent-never-saved"]'),
        withdraw: !!document.querySelector('[data-testid="consent-withdraw"]'),
        turnOn: !!document.querySelector('[data-testid="consent-turn-on"]'),
        panel: !!document.querySelector('[data-testid="decisions-panel"]'),
        switches: all('[data-testid="decisions-row"]'),
        switchStates: [...document.querySelectorAll('[data-testid="decisions-switch"]')].map((b) => b.getAttribute('aria-checked')),
        switchDisabled: [...document.querySelectorAll('[data-testid="decisions-switch"]')].map((b) => b.disabled),
        month: txt('[data-testid="decisions-month"]'),
        sentOpen: !!document.querySelector('[data-testid="decisions-sent"]'),
        notLive: all('[data-testid="decisions-not-live"]'),
        sentNotLive: all('[data-testid="decisions-sent-not-live"]'),
        proPill: !!document.querySelector('[data-testid="decisions-pro-pill"]'),
        planOrder: all('[data-testid="plan-ordered-by"]')[0] ?? null,
        calls: (window.__consentCalls || []).map((c) => c.call),
        setConsentFields: ((window.__consentCalls || []).find((c) => c.call === 'setConsent')?.params?.[0]) ?? null,
        setConsentVersion: ((window.__consentCalls || []).find((c) => c.call === 'setConsent')?.params?.[1]) ?? null,
        setConsentScope: ((window.__consentCalls || []).find((c) => c.call === 'setConsent')?.params?.[3]) ?? null,
        enabledCalls: (window.__consentCalls || []).filter((c) => c.call === 'setDecisionPointEnabled').map((c) => c.params),
        planReads: (window.__consentCalls || []).filter((c) => c.call === 'getModelPlan').length,
        saveLabel: txt('[data-testid="plan-row-ship/unsaved"] [data-testid="plan-act-save"]'),
        saveMessages: (window.__uishotHarness?.messages || []).filter((m) => m && m.type === 'saveModel').length,
        planGo: (window.__uishotHarness?.messages || []).some((m) => m && m.type === 'navigate'),
        cleanupSub: txt('[data-testid="plan-row-cleanup/model"] .pw-sub'),
        pointReads: (window.__consentCalls || []).filter((c) => c.call === 'getDecisionPoints').length,
        notes: all('[data-testid="decisions-note"]'),
        panelError: txt('[data-testid="decisions-error"]'),
        modelLine: txt('[data-testid="decisions-model"]'),
      };
    });
    seen.cardBefore = cardBefore;
    seen.twoDialogs = twoDialogs;
    const out = join(OUT, `${LABEL}-${job.slug}${SHOTTHEME ? '-' + SHOTTHEME : ''}-1366x900.png`);
    if (SHOTTHEME) await applyTheme(page, SHOTTHEME);
    await page.screenshot({ path: out, fullPage: false });
    // One Visual Language: the page this scene shows, measured at 1440x900 against its screen baseline. It
    // runs after the call trace above is read, so a relayout cannot add to the evidence.
    const screen = await checkScreen(page, `consent:${job.slug}`);
    // The scene is judged against what it promised. A missing control that the scene needed is a
    // failure too, not a note: a press that found nothing proves nothing about what the press does.
    const broken = (job.must(seen) || []).filter(Boolean)
      .concat(note ? [`something the scene needed was not on the page: ${note}`] : [])
      .concat(errors.length ? [`the page errored: ${errors.join(' | ')}`] : [])
      .concat(screen.failures);
    results.push({ job: job.slug, desc: job.desc, seen, errors, note, out, broken });
  } catch (e) {
    results.push({
      job: job.slug, desc: job.desc, errors: errors.concat('threw: ' + String(e && e.message)),
      note, out: null, broken: ['the scene threw: ' + String(e && e.message)],
    });
  }
  await page.close();
}

await browser.close();
server.close();
const reportPath = join(OUT, `${LABEL}-results.json`);
writeFileSync(reportPath, JSON.stringify(results, null, 2));
for (const r of results) {
  console.log(`${r.broken?.length ? 'FAIL' : 'ok  '} ${r.job}: ${r.desc}`);
  console.log(`  seen: ${JSON.stringify(r.seen)}`);
  for (const line of r.broken ?? []) console.log(`  BROKEN: ${line}`);
}
const failed = results.filter((r) => r.broken?.length);
console.log(`\n${results.length} scene(s) -> ${OUT}\n${reportPath}`);
if (failed.length) {
  console.error(`\n${failed.length} of ${results.length} scene(s) did not show what they promised: ${failed.map((r) => r.job).join(', ')}`);
  console.error('This driver is acceptance evidence for F-176. A run that fails here has not verified anything.');
  process.exitCode = 1;
} else {
  console.log(`all ${results.length} scenes showed what they promised`);
}
