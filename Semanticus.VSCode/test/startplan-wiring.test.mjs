// The start plan page against the wiring step's contract (M11, 2 Oct 2026): what the engine now says (built, held lines,
// the sample, Not now, the grades as letters with a provisional flag) and the faults found on the page before the join.
//
// Each block names the fault it holds shut. They are executed against the real page code (startplan-model.ts, startplan.tsx)
// over the mock engine (tools/uishot/fixtures/startplan-fixture.mjs), rendered with react-dom/server and read as the words a
// person sees. What a test cannot see (a mark at 900 px, two links drawn apart) is also proven in a browser by
// tools/uishot/startplan-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader, req } from './helpers/webview-load.mjs';
import {
  makeStartPlanState, startPlanView, startPlanEdit, startPlanApprove, startPlanBuild, startPlanAssistantWords, startPlanGrant, startPlanRealShape,
  startPlanConsent, startPlanSetConsent,
} from '../tools/uishot/fixtures/startplan-fixture.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
const load = makeLoader({
  './startplan.css': {},
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls'); },
    pickWorkingCopyFolder: async () => null, onStartPlanChange: () => () => {}, runHostCommand: () => {}, copyText: async () => true,
    onDidChange: () => () => {}, onPlanChange: () => () => {}, navigateInStudio: () => {},
  },
  './pro': { useTier: () => 'pro', upgradeToPro: () => {}, PENDING_ACCESS: 'Checking your Pro access…' },
  './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} },
  './connection': { useConnection: () => ({ openConnections: () => {} }) },
});
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&')
  .replace(/&#x2F;/g, '/').replace(/&gt;/g, '>').replace(/&lt;/g, '<').replace(/\s+/g, ' ').trim();
const src = (f) => readFileSync(join(root, 'webview', 'src', f), 'utf8');
const M = load('startplan-model');
const P = load('startplan');
const asView = (state) => JSON.parse(JSON.stringify(startPlanView(state)));
const noop = () => {};
const handlers = { onEdit: noop, onNeed: noop, onApprove: noop, onBuild: noop, onApproveAndBuild: noop, onDiscard: noop, onRetry: noop };
const render = (view, pro = true, extra = {}) => renderToStaticMarkup(React.createElement(P.StartPlanBody, { view, pro, busy: null, error: null, sentence: null, changed: new Set(), ...handlers, ...extra }));
const PRO = { pro: true };
const lineOf = (v, id) => M.linesOf(v).find((l) => l.item.id === id);
const failed = [];
const check = (name, fn) => {
  try { fn(); console.log(`  [PASS] ${name}`); } catch (e) { failed.push(name); console.log(`  [FAIL] ${name}: ${e.message.split('\n')[0]}`); }
};
const checkAsync = async (name, fn) => {
  try { await fn(); console.log(`  [PASS] ${name}`); } catch (e) { failed.push(name); console.log(`  [FAIL] ${name}: ${e.message.split('\n')[0]}`); }
};
const noJargon = (s, what) => {
  assert.doesNotMatch(s, /[—–]/, `${what}: no em or en dash`);
  assert.doesNotMatch(s, /one[- ]click/i, `${what}: never "one click"`);
  assert.doesNotMatch(s, /\bengine\b/i, `${what}: the engine is not the actor in words a person reads`);
};

// ---- 3. "The engine made no plan." is plain words that say what happened and what to do ----------------------------------
check('3. "The engine made no plan." is plain words that say what happened and what to do', () => {
  assert.equal(M.startOutcome({ exists: true, digest: 'x' }).error, null, 'a plan that came back is the plan');
  const none = M.startOutcome({ exists: false });
  assert.ok(none.error, 'a start that made no plan says so');
  assert.match(none.error, /could not start a plan from that source/i, 'what happened');
  assert.match(none.error, /check the server and database, then try again/i, 'and what to do');
  noJargon(none.error, 'no plan');
  assert.equal(M.startOutcome(null).error, none.error, 'no answer at all reads the same way');
  assert.doesNotMatch(src('startplan.tsx'), /The engine made no plan/, 'the raw line is gone from the page');
  // Reading a source with a sign-in, and building a whole model, take longer than the 30 second guard: a slow start must not
  // turn into "no response to startModelFromSql within 30s. The engine may be busy" on the start form.
  const long = /const LONG_RPC = \/\^\(([^)]*)\)\//.exec(src('bridge.ts'));
  assert.ok(long, 'the long-call list is where it was');
  for (const n of ['startModelFromSql', 'buildStartPlan']) assert.ok(long[1].split('|').includes(n), `${n} gets the long timeout`);
});

// ---- 4. the foot says the right thing for each state ---------------------------------------------------------------------
check('4. the foot says the right thing for each state', () => {
  const s = makeStartPlanState({}); const v = asView(s);
  const waiting = M.footOf(v, PRO);
  assert.match(waiting.lead, /5 questions wait on you/, `a plan with questions says so: ${waiting.lead}`);
  const approved = asView((startPlanApprove(s, v.digest), s));
  const a = M.footOf(approved, PRO);
  assert.match(a.lead, /This plan is approved/, `an approved plan says the person approved it: ${a.lead}`);
  assert.match(a.rest, /Build/, 'and what happens next');
  assert.doesNotMatch(a.lead + a.rest, /Ready to approve|questions? waits?/i, 'never "ready to approve" once approved');
  const stale = asView((startPlanAssistantWords(s, 2), s));
  const st = M.footOf(stale, PRO);
  assert.match(st.lead, /changed after you approved/i, `an approval the plan moved past says so: ${st.lead}`);
  assert.match(st.rest, /approve it again/i);
  const s2 = makeStartPlanState({}); const v2 = asView(s2);
  startPlanApprove(s2, v2.digest); startPlanBuild(s2, v2.digest);
  const b = M.footOf(asView(s2), PRO);
  assert.match(b.lead, /built/i, `a built plan says it was built: ${b.lead}`);
  assert.match(b.rest, /Undo/, 'and that Undo takes it back');
  const deciding = M.footOf(asView(makeStartPlanState({ decide: 'deciding' })), PRO);
  assert.doesNotMatch(deciding.lead, /Ready to approve/, 'a plan Jev is still deciding is not "ready to approve"');
  const ready = M.footOf(asView((() => { const r = makeStartPlanState({ free: true }); startPlanEdit(r, { op: 'answer', target: 'fiscal_year', value: '6/30' }); return r; })()), { pro: false });
  assert.match(ready.lead, /question waits on you|Ready to approve/, `a free plan with only the composite key left: ${ready.lead}`);
  for (const f of [waiting, a, st, b, deciding, ready]) noJargon(f.lead + ' ' + f.rest, 'foot');
  const text = readable(render(approved));
  assert.match(text, /This plan is approved\./, 'the rendered foot says it too');
});

// ---- 6. two links between one pair of tables are drawn apart ---------------------------------------------------------------
check('6. two links between one pair of tables are drawn apart', () => {
  const v = asView(makeStartPlanState({}));
  const g = M.linkGraph(v);
  const pair = g.edges.filter((e) => e.from === 'Invoices' && e.to === 'Date');
  assert.equal(pair.length, 2, 'the fixture has a fact with two dates linked to the date table');
  assert.notEqual(pair[0].offset, pair[1].offset, 'the two links get different offsets');
  assert.ok(Math.abs(pair[0].offset - pair[1].offset) >= 6, 'far enough apart to read as two lines');
  for (const e of g.edges.filter((x) => !(x.from === 'Invoices' && x.to === 'Date'))) assert.equal(e.offset ?? 0, 0, `${e.from} to ${e.to} is alone, so it stays centred`);
  const html = renderToStaticMarkup(React.createElement(P.LinkDrawing, { graph: g }));
  const ds = [...html.matchAll(/<path[^>]*class="sp-edge[^"]*"[^>]*\sd="([^"]+)"/g)].map((m) => m[1]);
  assert.equal(ds.length, g.edges.length, 'every link is drawn');
  assert.equal(new Set(ds).size, ds.length, 'no two link lines are drawn on the same path');
});

// ---- 7. the open row names the source column or table as the source calls it, never an internal ref ------------------------
check('7. the open row names the source column or table as the source calls it, never an internal ref', () => {
  const v = asView(makeStartPlanState({}));
  const rate = lineOf(v, 't-sum-rate');
  const group = v.sections.flatMap((s) => s.groups).find((g) => g.lines.some((l) => l.item.id === 't-sum-rate'));
  assert.equal(M.lineSource(rate, group), 'fact_timesheet, column Rate', 'the source table and the column');
  const table = lineOf(v, 't-in-Clients');
  assert.equal(M.lineSource(table, v.sections[0].groups.find((g) => g.table === 'Clients')), 'dim_client', 'a table line names the source table');
  const model = lineOf(v, 't-types');
  assert.equal(M.lineSource(model, v.sections[0].groups.find((g) => !g.table)), null, 'a model-wide line has no source name, and none is made up');
  const html = renderToStaticMarkup(React.createElement(P.LineDetail, { line: rate, view: v, tier: PRO, busy: false, onEdit: noop }));
  const text = readable(html);
  assert.match(text, /fact_timesheet, column Rate/, `the detail shows it: ${text}`);
  assert.doesNotMatch(text, /column:Timesheets|model:|relationship:|measure:/, 'and never an internal ref');
  for (const l of M.linesOf(v)) {
    const g = v.sections.flatMap((s) => s.groups).find((x) => x.lines.includes(l));
    const t = readable(renderToStaticMarkup(React.createElement(P.LineDetail, { line: l, view: v, tier: PRO, busy: false, onEdit: noop })));
    assert.doesNotMatch(t, /\b(model|column|table|measure|relationship):[^\s]/, `${l.item.id}: no internal ref in the open row (${M.lineSource(l, g)})`);
  }
});

// ---- 8. the grades after the build are letters, and a provisional one says so -------------------------------------------
check('8. the grades after the build are letters, and a provisional one says so', () => {
  const settled = asView(makeStartPlanState({}));
  const g = M.gradeOf(settled, PRO);
  assert.equal(g.after, 'A', 'the letter after the build');
  assert.equal(g.provisional, false);
  assert.equal(g.bestPractice, 'B', 'the best practice letter after the build');
  const t = readable(render(settled));
  assert.match(t, /\bA\b/, 'the letter is on the page');
  assert.doesNotMatch(t, /may still change/i, 'a settled grade is not called provisional');
  const moving = asView(makeStartPlanState({ decide: 'deciding' }));
  const gm = M.gradeOf(moving, PRO);
  assert.equal(gm.provisional, true);
  assert.match(gm.note, /may still change while Jev works/, 'a provisional grade says so in plain words');
  const tm = readable(render(moving));
  assert.match(tm, /may still change while Jev works/, 'next to the letter on the page');
  assert.match(render(moving), /data-provisional="yes"/, 'and the grade is marked provisional, never shown as final');
  const free = asView(makeStartPlanState({ free: true, provisional: true }));
  assert.doesNotMatch(M.gradeOf(free, { pro: false }).note, /Jev/, 'Free never says Jev is working');
  assert.match(M.gradeOf(free, { pro: false }).note, /may still change/);
  const real = JSON.parse(JSON.stringify(startPlanRealShape({})));
  assert.equal(M.gradeOf(real, { pro: false }).after, 'B', 'the engine shape: the letter from afterBuildGrade');
  assert.equal(M.gradeOf(real, { pro: false }).now, null, 'and nothing made up for now');
  // D7: a plan whose build was undone says the model has no grade now, beside the forecast, so the person sees the grade fall.
  assert.equal(M.gradeOf(real, { pro: false }).undone, false, 'a plan never built is not undone');
  const undone = JSON.parse(JSON.stringify(startPlanRealShape({})));
  undone.grades = { ...undone.grades, nowUndone: true };
  assert.equal(M.gradeOf(undone, { pro: false }).undone, true, 'built, then undone');
  const undoneText = readable(render(undone));
  assert.match(undoneText, /No tables now/, 'the card says there is no grade now');
  assert.match(undoneText, /The build is undone, so the model has nothing to grade/, 'and why');
  assert.match(undoneText, /after the build/, 'the forecast stays, named as a forecast');
  undone.built = true;
  assert.equal(M.gradeOf(undone, { pro: false }).undone, false, 'redone: the plan is built again and the card reads as built');
});

// ---- 9. a held line is marked, and not counted as a change that will be built; Built is its own state ----------------------
check('9. a held line is marked, and not counted as a change that will be built; Built is its own state', () => {
  const s = makeStartPlanState({});
  const before = asView(s);
  startPlanEdit(s, { op: 'drop_line', target: 't-in-Codes' });
  const v = asView(s);
  const held = lineOf(v, 'w-codes');
  assert.ok(held.held, 'leaving Codes out holds your assistant\'s words on it');
  assert.equal(M.lineState(held, v, PRO).text, M.COPY.stateHeld, 'the row says it is held');
  assert.equal(M.lineSub(held), held.held, 'and why, in the engine\'s plain words');
  assert.equal(M.buildCountOf(v), M.buildCountOf(before) - 2, 'the held line and the left-out table are not counted as changes that will be built');
  assert.equal(M.heldCountOf(v), 1);
  const html = render(v); const text = readable(html);
  assert.match(html, /data-held="yes"/, 'the row is marked held');
  assert.match(text, /1 waiting, not built/, 'the facts count it apart');
  assert.match(text, /Held, because the table Codes was left out by you/, 'and the words are on the page');
  startPlanEdit(s, { op: 'keep_line', target: 't-in-Codes' });
  assert.equal(lineOf(asView(s), 'w-codes').held, null, 'putting Codes back releases it');

  // Built: the page says so, shows the grades, offers Discard only.
  const b = makeStartPlanState({}); const bv0 = asView(b);
  startPlanApprove(b, bv0.digest); const r = startPlanBuild(b, bv0.digest);
  assert.equal(r.built, true); assert.equal(r.view.built, true, 'the build answers with the built plan');
  const bv = asView(b);
  const acts = M.actionsOf(bv, PRO);
  assert.ok(!acts.approve.enabled && !acts.build.enabled && !acts.approveAndBuild.enabled, 'nothing to approve or build on a built plan');
  const bh = render(bv, true, { onAskAssistant: noop }); const bt = readable(bh);
  assert.doesNotMatch(bh, /data-testid="sp-(approve|build)/, 'no approve or build buttons');
  assert.match(bh, /data-testid="sp-discard"/, 'Discard the plan is the one thing offered');
  assert.match(bt, /Built/, 'the page says it was built');
  assert.match(bt, /\bA\b/, 'with the grades');
  assert.match(bh, /data-built="yes"/);
  assert.doesNotMatch(bt, /Model not built yet/, 'never "not built yet" on a built plan');
  assert.doesNotMatch(bt, /Needs you/, 'a built plan asks nothing');
  // hero16 N5: a plan that was built while Jev still waited for its permission must not go on saying "Jev is waiting".
  const waitedThenBuilt = JSON.parse(JSON.stringify(asView(makeStartPlanState({ granted: false, decide: 'waiting_grant' }))));
  assert.notEqual(M.decideNote(waitedThenBuilt, PRO), null, 'before the build the waiting note is said');
  waitedThenBuilt.built = true;
  assert.equal(M.decideNote(waitedThenBuilt, PRO), null, 'after the build no Jev note is said');
  // hero16 section 10: the person reads changes, not "lines", and who chose them is named.
  assert.doesNotMatch([M.COPY.builtNote('X'), M.builtBandWords(bv), M.COPY.needsLead(1), M.COPY.needsLead(2), M.COPY.keepNote, M.COPY.pinNote, M.COPY.orphanLine].join(' '), /\blines?\b/i, 'the start plan says changes, never lines');
  assert.match(M.builtBandWords(bv), /\d+ changes? built\. One undo takes it back\./, 'the band counts changes');
  assert.match(bt, /Chosen by/, 'the counts under Changes in the plan say who chose them');
  assert.doesNotMatch(bt, /Left out until you answer|needs? you\b/, 'and no row on it waits on the person: what the build left out says Not built');
  assert.match(bt, /Not built/);
  assert.doesNotMatch(bh, /data-testid="sp-ask"/, 'nothing to ask the assistant to write for a plan already built');
  const rb = JSON.parse(JSON.stringify(startPlanRealShape({ built: true })));
  assert.equal(M.actionsOf(rb, { pro: false }).approve.enabled, false, 'the engine shape too');

  // The sample lines: text only, no buttons.
  for (const kind of ['reading', 'refused']) {
    const sv = asView(makeStartPlanState({ sample: kind }));
    const need = sv.needsYou.find((n) => n.kind === 'sample_' + kind);
    assert.ok(need, `the sample ${kind} line is in the band`);
    const sh = render(sv);
    const block = new RegExp(`data-testid="sp-need-sample"[^>]*data-kind="sample_${kind}"[\\s\\S]*?</div></div>`).exec(sh);
    assert.ok(block, `it is drawn (${kind})`);
    assert.doesNotMatch(block[0], /<button/, 'with no buttons');
    assert.match(readable(sh), kind === 'reading' ? /Reading a sample of the rows/ : /A sample of the rows was not read/, 'in plain words');
    assert.equal(M.needMark(need), null, 'it asks nothing, so nobody is marked as asking');
    assert.equal(M.questionsOf(sv).some((n) => n.kind === need.kind), false, 'and it is not a question that waits on the person');
  }
  // The engine's own Jev question kinds are marked Jev and counted as Jev's.
  for (const kind of ['unsure_pick', 'unsure_line', 'oscillation', 'jev', 'grant']) assert.equal(M.needMark({ kind, options: ['a', 'b'] }).key, 'jev', `${kind} is Jev asking`);
  for (const kind of ['grant_declined', 'fiscal_year']) assert.equal(M.needMark({ kind, options: ['a'] }).key, 'rule', `${kind} is Semanticus asking`);
});

// ---- 10. Not now is an answer; after it the band offers Allow --------------------------------------------------------------
check('10. Not now is an answer; after it the band offers Allow', () => {
  const s = makeStartPlanState({ granted: false, decide: 'waiting_grant' });
  const v = asView(s);
  const grant = v.needsYou.find((n) => n.kind === 'grant');
  const not = M.needAction(v, grant, 'Not now');
  assert.deepEqual(not, { kind: 'edit', edit: { op: 'answer', target: 'grant', value: 'Not now' }, args: [{ op: 'answer', target: 'grant', value: 'Not now' }, 'human'] }, 'Not now is the answer op, from the person');
  const after = asView((startPlanEdit(s, not.edit), s));
  assert.equal(after.jevDeclined, true);
  const declined = after.needsYou.find((n) => n.kind === 'grant_declined');
  assert.ok(declined, 'the band now carries the declined line');
  assert.deepEqual(declined.options, ['Allow'], 'with Allow');
  assert.deepEqual(M.needAction(after, declined, 'Allow'), { kind: 'grant', args: [after.digest, 'human'] }, 'Allow is the grant, for the digest the person saw');
  assert.equal(M.actionsOf(after, PRO).approve.enabled, true, 'a person who said Not now can still approve the plan they have');
  assert.equal(M.decideNote(after, PRO), null, 'no "Jev is waiting for you" once the person said Not now');
  const t = readable(render(after));
  assert.match(t, /You chose to carry on without Jev/);
  assert.doesNotMatch(t, /Jev is waiting for you/);
  const granted = asView((startPlanSetConsent(s), startPlanGrant(s, after.digest), s));
  assert.equal(granted.jevDeclined, false, 'Allow later lets Jev look');
  assert.ok(!granted.needsYou.some((n) => n.kind === 'grant_declined'));
});

// ---- 2. Connections' From a SQL source opens the start form with no name step first ---------------------------------------
check("2. Connections' From a SQL source opens the start form with no name step first", () => {
  const hub = src('connectionshub.tsx');
  assert.match(hub, /data-testid="hub-add-sql"[^>]*onClick=\{\(\) => openStartFromSql\(\)\}/, 'From a SQL source opens the start form');
  assert.doesNotMatch(hub, /openCreate\(true\)/, 'never the blank model\'s name step');
  assert.doesNotMatch(hub, /createFromSql/, 'the two-step create from a SQL source is gone');
  assert.match(hub, /<StartFromSqlForm\b/, 'the hub hosts the start plan\'s own form');
  assert.match(hub, /initialView === 'createsql'/, 'the host command still opens it with no model open');
  const ext = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
  assert.match(ext, /registerPersonCommand\('semanticus\.draftFromSqlSource'/);
  assert.doesNotMatch(ext, /makes the blank model and then lands on the start form/, 'the host no longer describes the two-step path');
});

// ---- 5. the marks stay at 900 px: no container rule hides a mark ---------------------------------------------------------
check('5. the marks stay at 900 px: no container rule hides a mark', () => {
  const css = src('startplan.css');
  const narrow = [...css.matchAll(/@container sp \(max-width: 1000px\) \{([\s\S]*?)\n\}/g)].map((m) => m[1]).join('\n');
  assert.ok(narrow.length > 0, 'the narrow rules were found');
  assert.doesNotMatch(narrow, /\.sp-need > \.sp-who\s*\{\s*display:\s*none/, 'the Jev asks and Semanticus asks marks stay on the Needs you lines');
  assert.match(narrow, /\.sp-row-t \.sp-who2\s*\{\s*display:\s*inline-flex/, 'a row\'s mark moves into its title, it does not vanish');
});


// ===================================================================================================
// The real engine's run (sf-wiring-real-drive, 2 Oct): defects the mock hid.
// ===================================================================================================

// ---- A. Allow on Pro gives the model's own permission first, through the start plan's card, then the grant -------------
await checkAsync('A. Allow makes sure the model permission is current, shows the card when it is not, then grants', async () => {
  // The mock refuses a grant without the model's permission, as LocalEngine.GrantStartDisclosureAsync does.
  const s = makeStartPlanState({ granted: false, decide: 'waiting_grant' });
  const v = asView(s);
  assert.equal(startPlanConsent(s).current, false, 'a new Pro model has no permission yet');
  assert.throws(() => startPlanGrant(s, v.digest), /Give permission for this model first/, 'the mock refuses the grant alone, as the engine does');
  // The flow: permission not current, so the card opens and NOTHING is granted until the person presses its OK.
  const calls = [];
  let card = null;
  const deps = {
    getConsent: async () => { calls.push('getConsent'); return startPlanConsent(s); },
    grant: async (digest) => { calls.push(['grant', digest]); return startPlanGrant(s, digest); },
    openCard: (plan, scope) => { calls.push(['openCard', scope]); card = plan; },
  };
  const grantNeed = v.needsYou.find((n) => n.kind === 'grant');
  const r = await M.allowJev(v, grantNeed, deps);
  assert.equal(r, 'card', 'permission was not current, so the card is shown');
  assert.deepEqual(calls, ['getConsent', ['openCard', startPlanConsent(s).appliesTo]], 'and nothing is granted yet');
  assert.match(card.source, /ContosoOps/, 'the card names the source');
  assert.ok(card.tables.includes('Timesheets') && card.tables.length === 7, `and the tables Pro will see: ${card.tables}`);
  assert.equal(card.sample, true, 'and whether a sample of rows is used');
  // The card's OK gives the model permission (setConsent), then calls the plan's grant with the digest the person looked at.
  startPlanSetConsent(s);
  const after = await card.grant();
  assert.deepEqual(calls[calls.length - 1], ['grant', v.digest], 'the grant carries the digest the person looked at');
  assert.notEqual(after.decide.state, 'waiting_grant', 'and Jev is no longer waiting');
  // Permission already current: the card is STILL shown (join 7, UAT-J7-S2-002/003): it is where the person sees which tables and samples Jev would see and
  // says no to any of them, so a grant never goes with no look at the scope. Nothing is granted until its OK.
  const s2 = makeStartPlanState({ granted: false, decide: 'waiting_grant', consent: true });
  const v2 = asView(s2); const c2 = [];
  const r2 = await M.allowJev(v2, v2.needsYou.find((n) => n.kind === 'grant'), {
    getConsent: async () => startPlanConsent(s2), grant: async (d) => { c2.push(d); return startPlanGrant(s2, d); }, openCard: () => c2.push('card'),
  });
  assert.equal(r2, 'card', 'a current permission still shows the card with the tables on it');
  assert.deepEqual(c2, ['card'], 'and grants nothing until the person presses its OK');
  // Allow after Not now goes the same way.
  const s3 = makeStartPlanState({ granted: false, decide: 'waiting_grant' });
  startPlanEdit(s3, { op: 'answer', target: 'grant', value: 'Not now' });
  const v3 = asView(s3); let opened = false;
  await M.allowJev(v3, v3.needsYou.find((n) => n.kind === 'grant_declined'), { getConsent: async () => startPlanConsent(s3), grant: async () => { throw new Error('granted with no permission'); }, openCard: () => { opened = true; } });
  assert.ok(opened, 'Allow after Not now opens the card too');
  // The page wires it: Allow goes through allowJev and the start plan's own card, never grantStartDisclosure alone.
  const page = src('startplan.tsx');
  assert.match(page, /M\.allowJev\(/, 'the page presses Allow through allowJev');
  assert.match(page, /openStartPlanConsentCard\(/, 'and raises the start plan card');
  assert.match(page, /rpc<[^>]*>\('getConsent'\)/, 'reading the model permission from the engine');
});

// ---- B. the Free note says what a Free plan really holds -----------------------------------------------------------------
check('B. the Free note promises nothing the real Free plan does not have', () => {
  assert.doesNotMatch(M.COPY.freeNotice, /marked Default|plain defaults/i, 'no "marked Default" promise: the real Free plan has no Default line');
  noJargon(M.COPY.freeNotice, 'free note');
  assert.match(M.COPY.freeNotice, /your assistant writes the words/i, 'it says who writes the words');
  assert.match(M.COPY.freeNotice, /Jev is Pro/);
  const v = asView(makeStartPlanState({ free: true }));
  assert.equal(M.linesOf(v).filter((l) => M.lineMark(l, { pro: false }).key === 'default').length, 0, 'the mock Free plan is the real one: no Default lines');
  assert.ok(!M.filterTabs(v, { pro: false }).some((t) => t.id === 'default'), 'so there is no Default filter with nothing in it');
  const t = readable(render(v, false));
  assert.doesNotMatch(t, /on plain defaults|Default, no Jev/, 'and nothing on the page talks about defaults that are not there');
});

// ---- C. never say how many findings stay open when the plan does not know --------------------------------------------
check('C. "findings stay open" only when the preview scan gave a count; else the letter', () => {
  const real = JSON.parse(JSON.stringify(startPlanRealShape({})));
  const t = readable(render(real, false));
  assert.doesNotMatch(t, /\d+ findings? stays? open/i, 'the best practice section is empty, so no count of open findings is claimed');
  assert.match(t, /Best practice C/, 'what is known is said: the letter');
  const pro = readable(render(asView(makeStartPlanState({})), true));
  assert.match(pro, /2 best practice problems stay open/, 'a plan whose preview scan gave a count says it (the count is the scan\'s: see psc3-fixes.test.mjs)');
});

// ---- D. a line that asks nothing carries no "asks" mark -----------------------------------------------------------------
check('D. a needs-you line with no options is not marked as asking', () => {
  assert.equal(M.needMark({ kind: 'sample_refused', options: [] }), null);
  assert.equal(M.needMark({ kind: 'composite_key', options: [] }), null);
  const html = render(asView(makeStartPlanState({ sample: 'refused' })));
  const block = /data-testid="sp-need-sample"[\s\S]*?<\/div><\/div>/.exec(html)[0];
  assert.doesNotMatch(readable(block), /asks/, 'the sample line says what happened, not who asks');
});

// ---- E. the answers a person reads are capitalised; the values sent stay as the engine named them ----------------------
check('E. option labels are capitalised, the values sent are not', () => {
  const need = { id: 'unsure_line:x', kind: 'unsure_line', text: 'Jev was not sure. Keep it?', options: ['keep', 'drop'], recommended: 'keep' };
  assert.equal(M.optionLabel('keep'), 'Keep'); assert.equal(M.optionLabel('drop'), 'Drop');
  const v = { ...asView(makeStartPlanState({})), needsYou: [need] };
  const t = readable(render(v));
  assert.match(t, /\bKeep\b[\s\S]*\bDrop\b/, 'the buttons read Keep and Drop');
  assert.doesNotMatch(render(v), />keep<|>drop</, 'never lowercase on a button');
  assert.equal(M.needAction(v, need, 'keep').edit.value, 'keep', 'the engine still gets keep');
});

// ---- F. Changed counts every change since the person's own last change -------------------------------------------------
check('F. the Changed filter keeps every line that moved since the person last changed something', () => {
  const a = new Set(['w-1']), b = new Set(['w-2']);
  assert.deepEqual([...M.mergeChanged(a, b, false)].sort(), ['w-1', 'w-2'], 'a second batch of words adds to the first');
  assert.deepEqual([...M.mergeChanged(a, b, true)], ['w-2'], 'the person\'s own change starts the count again, with what it moved');
  assert.match(M.COPY.changedTip, /since your last edit/i, 'the filter says what it counts');
  assert.match(src('startplan.tsx'), /M\.mergeChanged\(/, 'the page uses it');
});

// ---- G. the fiscal-year question ---------------------------------------------------------------------------------------
check('G. the fiscal-year need renders as a question and answers with op answer, target fiscal_year', () => {
  const v = asView(makeStartPlanState({}));
  const fy = v.needsYou.find((n) => n.kind === 'fiscal_year');
  assert.ok(fy && fy.id === 'fiscal_year', 'the need is fiscal_year by id and kind, as the engine sends it');
  assert.deepEqual(fy.options, ['6/30', 'calendar']);
  assert.deepEqual(M.needAction(v, fy, '6/30').edit, { op: 'answer', target: 'fiscal_year', value: '6/30' });
  assert.equal(M.optionLabel('6/30'), 'Year ends 6/30'); assert.equal(M.optionLabel('calendar'), 'Calendar year');
  const t = readable(render(v));
  assert.match(t, /Year ends 6\/30/); assert.match(t, /Calendar year/);
  assert.equal(M.needMark(fy).key, 'rule', 'Semanticus asks it');
  const s = makeStartPlanState({}); startPlanEdit(s, { op: 'answer', target: 'fiscal_year', value: '6/30' });
  assert.ok(!asView(s).needsYou.some((n) => n.kind === 'fiscal_year'), 'answered, it leaves the band');
});

// ---- H. a typed endpoint signs in the way a saved one does (hero journey 5, 8 Oct) ------------------------------------------
check('H. a typed endpoint takes the sign-in of the saved endpoints, then of the open model, never the Azure command line by itself', () => {
  const typed = { server: 'x.datawarehouse.fabric.microsoft.com', database: 'Sales' };
  const saved = [{ id: 'a', name: 'Source', server: 's', database: 'd', authMode: 'serviceprincipal', tenantId: 'tenant-1' }];
  const withSaved = M.typedEndpoint(typed, saved, saved[0], { authMode: 'devicecode', tenantId: 'tenant-2' });
  assert.deepEqual([withSaved.authMode, withSaved.tenantId], ['serviceprincipal', 'tenant-1'], 'the endpoint the person picked in the list signs in first');
  const firstSaved = M.typedEndpoint(typed, saved, undefined, undefined);
  assert.deepEqual([firstSaved.authMode, firstSaved.tenantId], ['serviceprincipal', 'tenant-1'], 'with none picked, the first saved endpoint');
  const fromModel = M.typedEndpoint(typed, [], undefined, { authMode: 'serviceprincipal', tenantId: 'tenant-2' });
  assert.deepEqual([fromModel.authMode, fromModel.tenantId], ['serviceprincipal', 'tenant-2'], 'with none saved, the way the open model was opened');
  const nothing = M.typedEndpoint(typed, [], undefined, undefined);
  assert.equal(nothing.authMode, 'interactive', 'with nothing known, the browser sign-in the rest of the product starts on');
  assert.equal(nothing.server + '|' + nothing.database, typed.server + '|' + typed.database, 'the typed words are kept');
  assert.equal(M.startRequest(fromModel, 'X', '/f', 'import').authMode, 'serviceprincipal', 'and the start sends it');
  assert.match(src('startplan.tsx'), /M\.typedEndpoint\(/, 'the form uses it');
});

// ---- I. the build shows it is working, and a built plan opened from the Plan's band has a way back (K25, 9 Oct) ---------------
check('I. Approve and build says it is building while the call runs, and a built plan opened from the band says how to go back', () => {
  const v = asView(makeStartPlanState({}));
  assert.doesNotMatch(readable(render(v)), /Building /, 'no building line while nothing builds');
  const t = readable(render(v, true, { building: true, busy: 'approve' }));
  assert.match(t, /Building .+\. This takes a few seconds\. One undo takes it back\./, 'the page says the build is running and that undo takes it back');
  noJargon(t, 'the building line');
  assert.match(render(v, true, { building: true }), /data-testid="sp-building"/);
  const s2 = makeStartPlanState({}); const d2 = asView(s2).digest; startPlanApprove(s2, d2); startPlanBuild(s2, d2);
  const built = asView(s2);
  assert.ok(built.built, 'the build answers with the built plan');
  assert.doesNotMatch(render(built), /Back to the Plan/, 'a built plan the page was not asked to close has no way back');
  assert.match(readable(render(built, true, { onClose: noop })), /Back to the Plan/, 'opened from the band, it has one');
});

if (failed.length) { console.error(`startplan-wiring: ${failed.length} failed: ${failed.join(' | ')}`); process.exit(1); }
console.log('startplan-wiring: ok');
