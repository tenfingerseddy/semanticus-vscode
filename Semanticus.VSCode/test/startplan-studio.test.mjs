// The start plan's page (lane sf-studio, L12): option A of the spec-first start, on the Plan page.
//
// What each part proves, because they are not equally strong:
//   1. THE MODEL (startplan-model.ts), executed: who decided a line and what it says, the filters, Free's plain
//      defaults, the approval states, which buttons are live, the digest the person LOOKED at is the one sent.
//   2. THE PAGE, rendered for real with react-dom/server over the mock engine's JSON (fixtures/startplan-fixture.mjs),
//      Pro and Free, and read back as the words a person sees.
//   3. THE WIRE NAMES: the verbs the page calls are the contract's IStartPlanEngine verbs, camelCased with Async
//      dropped. Until lane sf-doors puts them on the RPC target they are the pending names in rpc-name-parity.
//   4. THE COPY: no em dashes, never "one click", no colourful emoji, in the model's words or the page's.
//   5. THE WIRING: the Plan mounts the page, the SQL wizard hands an empty model to it, the host forwards its
//      broadcast. The real clicks are driven in a browser by tools/uishot/startplan-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeLoader, req } from './helpers/webview-load.mjs';
import {
  makeStartPlanState, startPlanView, startPlanEdit, startPlanApprove, startPlanBuild, startPlanAssistantWords, startPlanGrant, startPlanRealShape,
} from '../tools/uishot/fixtures/startplan-fixture.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repo = resolve(root, '..');
const React = req('react');
const { renderToStaticMarkup } = req('react-dom/server');
let tier = 'pro';
const load = makeLoader({
  './startplan.css': {},
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls'); },
    pickWorkingCopyFolder: async () => null, onStartPlanChange: () => () => {}, runHostCommand: () => {},
    onDidChange: () => () => {}, onPlanChange: () => () => {}, navigateInStudio: () => {},
  },
  './pro': { useTier: () => tier, upgradeToPro: () => {}, PENDING_ACCESS: 'Checking your Pro access…' },
  './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} },
  './connection': { useConnection: () => ({ openConnections: () => {} }) },
});
const readable = (html) => html.replace(/<[^>]+>/g, ' ').replace(/&quot;/g, '"').replace(/&#x27;/g, "'").replace(/&amp;/g, '&')
  .replace(/&#x2F;/g, '/').replace(/&gt;/g, '>').replace(/&lt;/g, '<').replace(/\s+/g, ' ').trim();
const src = (f) => readFileSync(join(root, 'webview', 'src', f), 'utf8');

const M = load('startplan-model');
const proState = () => makeStartPlanState({});
const freeState = () => makeStartPlanState({ free: true });
const asView = (state) => JSON.parse(JSON.stringify(startPlanView(state)));   // what the wire hands the page

// ===================================================================================================
// 1. The model.
// ===================================================================================================

// ---- the approval: bound to the digest the person looked at (S1) --------------------------------------
{
  const s = proState(); const v = asView(s);
  assert.equal(M.approvalOf(v).state, 'none', 'a plan nobody approved says so');
  assert.deepEqual(M.approveArgs(v), [v.digest], 'Approve sends the digest the person LOOKED at, nothing else');
  assert.deepEqual(M.buildArgs(v), [v.digest, 'human'], 'Build sends that same digest, and says a person pressed it');

  const approved = asView((startPlanApprove(s, v.digest), s));
  assert.equal(M.approvalOf(approved).state, 'current', 'an approval on the current digest is current');
  assert.match(M.approvalOf(approved).text, /you approved/i, 'and it says the person did it');
  assert.match(M.approvalOf(approved).text, /you or your assistant can build/i, 'K3, truthfully: after the person approves, either may press Build');

  // The assistant writes words on the other door: the digest moves and the approval stops matching.
  const moved = asView((startPlanAssistantWords(s, 3), s));
  assert.notEqual(moved.digest, approved.digest, 'assistant words move the digest');
  assert.equal(M.approvalOf(moved).state, 'stale', 'so the approval made before is stale, never shown as standing');
  assert.match(M.approvalOf(moved).text, /no longer matches|changed/i, 'and the page says it changed');
  assert.equal(M.actionsOf(moved, { pro: true }).build.enabled, false, 'a stale approval cannot build');
  assert.equal(M.actionsOf(moved, { pro: true }).approve.enabled, true, 'it can be approved again, on the new digest');
  assert.deepEqual(M.approveArgs(moved), [moved.digest], 'and the new approval is for the new digest');
}

// ---- which buttons are live ---------------------------------------------------------------------------
{
  const s = proState(); const v = asView(s);
  const a = M.actionsOf(v, { pro: true });
  assert.equal(a.approveAndBuild.enabled, true, 'a ready plan can be approved and built');
  assert.equal(a.build.enabled, false, 'Build alone needs an approval first');
  assert.equal(M.primaryAction(v, { pro: true }), 'approve_and_build');
  const ok = asView((startPlanApprove(s, v.digest), s));
  assert.equal(M.primaryAction(ok, { pro: true }), 'build', 'once approved, the button is Build');
  assert.equal(M.actionsOf(ok, { pro: true }).build.enabled, true);

  for (const state of ['deciding', 'waiting_grant']) {
    const d = makeStartPlanState({ decide: state, granted: state !== 'waiting_grant' });
    const dv = asView(d);
    const da = M.actionsOf(dv, { pro: true });
    assert.equal(da.approve.enabled, false, `${state}: nothing is approved while the plan is still moving`);
    assert.equal(da.approveAndBuild.enabled, false);
    assert.ok(da.approve.why && da.approve.why.length > 10, `${state}: the page says why, in words`);
  }
  const inc = makeStartPlanState({ decide: 'incomplete', reason: 'Jev stopped after 6 rounds.' });
  assert.equal(M.actionsOf(asView(inc), { pro: true }).approve.enabled, true, 'Incomplete may be approved: the rest becomes Plan rows, said honestly');
  const built = asView((startPlanApprove(s, v.digest), startPlanBuild(s, v.digest), s));
  assert.equal(M.actionsOf(built, { pro: true }).approveAndBuild.enabled, false, 'a built plan builds nothing again');
  assert.equal(M.actionsOf({ exists: false }, { pro: true }).approve.enabled, false, 'no plan, no button');
  assert.equal(M.actionsOf({ ...v, digest: '' }, { pro: true }).approve.enabled, false, 'no digest, nothing to approve');
}

// ---- Free is honest -----------------------------------------------------------------------------------
{
  const v = asView(freeState());
  const free = { pro: false };
  const lines = v.sections.flatMap((s) => s.groups.flatMap((g) => g.lines));
  assert.ok(lines.every((l) => l.decidedBy !== 'jev'), 'the mock gives Free no Jev line (the engine sends none)');
  const tabs = M.filterTabs(v, free).map((t) => t.label);
  assert.ok(!tabs.includes('Jev'), 'Free never filters by Jev');
  // The real Free plan holds no line Jev would have decided (sf-wiring-real-drive, 2 Oct), so the mock holds none and there is no
  // Default filter. A line that DOES carry a Jev decision point on Free is still marked Default, never Jev: built here by hand.
  assert.ok(!tabs.includes('Default'), 'no Default filter when nothing is on a plain default');
  const defaulted = asView(proState()).sections.flatMap((s) => s.groups.flatMap((g) => g.lines)).filter((l) => l.point)
    .map((l) => ({ ...l, decidedBy: 'rule', kept: true }));
  assert.ok(defaulted.length > 0);
  const withDefaults = { ...v, sections: [...v.sections.filter((s) => s.id !== 'words'), { id: 'words', groups: [{ table: null, lines: defaulted }] }] };
  assert.ok(M.filterTabs(withDefaults, free).some((t) => t.label === 'Default'), 'with such lines the Default filter is back');
  for (const l of defaulted) {
    assert.equal(M.lineMark(l, free).label, 'Default', 'a line Jev would have decided is marked Default on Free');
    assert.match(M.lineState(l, withDefaults, free).text, /Default, no Jev/);
  }
  assert.equal(M.lineMark(lines.find((l) => l.decidedBy === 'assistant'), free).label, 'Assistant', 'the assistant is still the assistant');
  assert.equal(M.lineMark(lines.find((l) => l.decidedBy === 'rule' && !l.point), free).label, 'Rule', 'a rule is still a rule');
  assert.match(M.COPY.freeNotice, /Jev is Pro/);
  assert.match(M.COPY.freeNotice, /one at a time/, 'Free says best practice fixes are one at a time');
  assert.equal(M.decideNote(v, free), null, 'Free shows no Jev progress at all');
  assert.equal(v.needsYou.filter((n) => n.kind === 'jev' || n.kind === 'grant').length, 0, 'and no Jev question');

  const pro = asView(proState());
  const proLine = pro.sections.flatMap((s) => s.groups.flatMap((g) => g.lines)).find((l) => l.decidedBy === 'jev');
  assert.equal(M.lineMark(proLine, { pro: true }).label, 'Jev', 'on Pro the same kind of line says Jev');
}

// ---- the lines: who, what, state ----------------------------------------------------------------------
{
  const v = asView(proState());
  const lines = v.sections.flatMap((s) => s.groups.flatMap((g) => g.lines));
  const dropped = lines.find((l) => l.item.id === 't-sum-budget');
  assert.equal(dropped.kept, false);
  assert.match(M.lineState(dropped, v, { pro: true }).text, /Left out until you answer/, 'an unsure Jev line is left out until answered');
  assert.equal(M.lineState(dropped, v, { pro: true }).tone, 'need');
  const written = lines.find((l) => l.item.id === 'w-desc');
  assert.equal(M.lineState(written, v, { pro: true }).text, 'Written');
  const decided = lines.find((l) => l.item.id === 'r-ts-pr');
  assert.equal(M.lineState(decided, v, { pro: true }).text, 'Decided');
  const stays = lines.find((l) => l.item.id === 'b-trail');
  assert.equal(M.lineState(stays, v, { pro: true }).text, 'Stays open', 'a best practice finding the build does not fix says it stays open');
  assert.equal(M.lineMark(lines.find((l) => l.decidedBy === 'person'), { pro: true }).label, 'You');
  assert.equal(M.lineTitle(decided), 'Timesheets to Projects on ProjectKey');
  assert.equal(M.lineValue(lines.find((l) => l.item.id === 'm-hours')), null, 'a measure shows its formula, not a made-up value');
}

// ---- filters and totals -------------------------------------------------------------------------------
{
  const v = asView(proState());
  const tabs = M.filterTabs(v, { pro: true });
  assert.deepEqual(tabs.map((t) => t.id), ['all', 'jev', 'rule', 'assistant', 'you', 'changed']);
  const all = tabs.find((t) => t.id === 'all').count;
  const lines = v.sections.flatMap((s) => s.groups.flatMap((g) => g.lines));
  assert.equal(all, lines.length, 'All counts every line in the plan');
  const sum = ['jev', 'rule', 'assistant', 'you'].map((id) => tabs.find((t) => t.id === id).count).reduce((a, b) => a + b, 0);
  assert.equal(sum, all, 'the four marks add up to the whole, so nothing is counted twice or lost');
  assert.equal(M.lineMatches('jev', lines.find((l) => l.decidedBy === 'jev'), { pro: true }, new Set()), true);
  assert.equal(M.lineMatches('rule', lines.find((l) => l.decidedBy === 'jev'), { pro: true }, new Set()), false);
  assert.equal(M.lineMatches('changed', lines[0], { pro: true }, new Set([lines[0].item.id])), true);
  assert.equal(M.lineMatches('changed', lines[1], { pro: true }, new Set([lines[0].item.id])), false);
}

// ---- a change moves only its part: what changed between two reads -------------------------------------
{
  const s = proState(); const before = asView(s);
  startPlanEdit(s, { op: 'answer', target: 'need-budgets', value: 'No total' });
  const after = asView(s);
  const changed = M.changedLineIds(before, after);
  assert.ok(changed.has('t-sum-budget'), 'the budgets line is marked changed');
  assert.ok(changed.has('m-bud-hours') && changed.has('m-bud-amount'), 'and the two measures the rules no longer make');
  assert.ok(!changed.has('m-hours') && !changed.has('r-ts-pr'), 'nothing else moves');
  assert.equal(M.changedLineIds(null, after).size, 0, 'the first read has nothing to compare, so nothing is "changed"');
}

// ---- Needs you: a question becomes the call the engine understands --------------------------------------
{
  const v = asView(proState());
  const grant = makeStartPlanState({ granted: false });
  const gv = asView(grant);
  const gneed = gv.needsYou.find((n) => n.kind === 'grant');
  assert.ok(gneed, 'before the grant, the band carries the grant');
  assert.deepEqual(M.needAction(gv, gneed, 'Allow'), { kind: 'grant', args: [gv.digest, 'human'] }, 'the grant is the person pressing it, for the digest they saw');
  // Not now is an answer since the wiring step: the plan goes on without Jev, and the band then offers Allow (startplan-wiring.test.mjs).
  const notNow = { op: 'answer', target: 'grant', value: 'Not now' };
  assert.deepEqual(M.needAction(gv, gneed, 'Not now'), { kind: 'edit', edit: notNow, args: [notNow, 'human'] }, 'Not now is the person\'s answer to the grant');
  const q = v.needsYou.find((n) => n.id === 'need-budgets');
  assert.deepEqual(M.needAction(v, q, 'No total'), { kind: 'edit', edit: { op: 'answer', target: 'need-budgets', value: 'No total' }, args: [{ op: 'answer', target: 'need-budgets', value: 'No total' }, 'human'] });
  assert.equal(M.needLean(q), 'No total', 'the recommended option is the one Jev leans to');
  const comp = v.needsYou.find((n) => n.kind === 'composite_key');
  assert.deepEqual(comp.options, [], 'a composite key has no options');
  assert.equal(M.needTitle(q), 'Should project budgets add up?', 'the question is the first sentence of its text');
  assert.match(M.needBody(q), /Budget hours and Budget amount/, 'the rest is the evidence');
  assert.deepEqual(M.editLine({ item: { id: 't-sum-rate' } }, false), { op: 'drop_line', target: 't-sum-rate' });
  assert.deepEqual(M.editLine({ item: { id: 't-sum-rate' } }, true), { op: 'keep_line', target: 't-sum-rate' });
  assert.deepEqual(M.pinLine({ item: { id: 't-sum-rate', after: 'No total' } }), { op: 'pin', target: 't-sum-rate', value: 'No total' });
}

// ---- Jev's and the words' progress: Incomplete never reads as done --------------------------------------
{
  const f = { pro: true };
  assert.equal(M.decideNote(asView(proState()), f).tone, 'good', 'Stable is said');
  const deciding = asView(makeStartPlanState({ decide: 'deciding', progress: [
    { family: 'measures', asked: 41, answered: 38, unusable: 3, remaining: 34 }, { family: 'columnFormat', asked: 20, answered: 20, unusable: 0, remaining: 50 }] }));
  const d = M.decideNote(deciding, f);
  assert.equal(d.tone, 'busy');
  assert.match(d.text, /Jev is deciding/);
  assert.match(d.text, /61 of 145|61 of/, 'the count is asked of all it has to look at');
  const inc = M.decideNote(asView(makeStartPlanState({ decide: 'incomplete', reason: 'Jev stopped after 6 rounds.' })), f);
  assert.equal(inc.tone, 'warn');
  assert.match(inc.text, /not finished/i, 'Incomplete says it is not finished');
  assert.match(inc.text, /Jev stopped after 6 rounds/, 'and why');
  assert.doesNotMatch(inc.text, /\bdone\b/i, 'and never says done');
  const wait = M.decideNote(asView(makeStartPlanState({ decide: 'waiting_grant', granted: false })), f);
  assert.match(wait.text, /waiting for you/i);
  const w = M.wordsLine(asView(makeStartPlanState({ wordsOpen: 20 })));
  assert.deepEqual([w.done, w.total, w.open], [124, 144, 20], 'words written are the total less the ones still open');
  assert.match(w.text, /124 of 144/);
}

// ---- the link drawing (mockup B), from the plan's own Links lines ---------------------------------------
{
  const v = asView(proState());
  const g = M.linkGraph(v);
  assert.ok(g, 'the plan has links, so there is a drawing');
  assert.deepEqual(g.nodes.map((n) => n.name).sort(), ['Clients', 'Date', 'Invoices', 'Projects', 'Staff', 'Timesheets'].sort(), 'it draws the tables the links touch');
  assert.equal(g.edges.length, 8, 'one line per link');
  assert.equal(g.edges.filter((e) => e.active).length, 6, '6 on');
  assert.equal(g.edges.filter((e) => !e.active).length, 2, 'and 2 kept off, drawn differently');
  assert.ok(g.nodes.find((n) => n.name === 'Timesheets').sourceName === 'fact_timesheet', 'the source name rides along (mockup C)');
  // Layout: every box inside the drawing, none on another, and a link that skips a column goes round, not behind a table.
  for (const n of g.nodes) assert.ok(n.x >= 0 && n.y >= 0 && n.x + n.w <= g.width && n.y + n.h <= g.height, `${n.name} is inside the drawing`);
  for (const a of g.nodes) for (const b of g.nodes) if (a !== b) assert.ok(a.x + a.w <= b.x || b.x + b.w <= a.x || a.y + a.h <= b.y || b.y + b.h <= a.y, `${a.name} and ${b.name} do not overlap`);
  const col = (name) => g.nodes.find((n) => n.name === name).x;
  for (const e of g.edges) {
    const skips = Math.abs(col(e.to) - col(e.from)) > 220;
    assert.equal(!!e.lane, skips, `${e.from} to ${e.to}: ${skips ? 'skips a column, so it runs round the boxes' : 'goes straight'}`);
  }
  assert.equal(M.linkGraph({ ...v, sections: v.sections.filter((s) => s.id !== 'links') }), null, 'no links, no drawing: nothing is drawn that the plan does not say');
  // An engine that sends the structured links (the contract has none yet) wins over reading the titles.
  const own = M.linkGraph({ ...v, links: [{ from: 'A', to: 'B', active: true }] });
  assert.deepEqual(own.edges.map((e) => [e.from, e.to]), [['A', 'B']]);
}

// ---- the start form's request -------------------------------------------------------------------------
{
  const rec = { id: 'sq-1', name: 'Contoso warehouse', server: ' a.example.com ', database: ' ContosoOps ', authMode: 'interactive', tenantId: 'contoso.com' };
  assert.deepEqual(M.startRequest(rec, ' Contoso Consulting ', ' /home/k/models ', 'import'),
    { server: 'a.example.com', database: 'ContosoOps', authMode: 'interactive', tenantId: 'contoso.com', storageMode: 'import', folder: '/home/k/models', name: 'Contoso Consulting' },
    'the saved record, trimmed, plus the model name and the folder');
  assert.equal(M.startRequest({ ...rec, authMode: '', tenantId: '' }, 'X', '/f', 'import').authMode, 'azcli', 'no sign-in named means the default');
  assert.equal(M.startRequest({ ...rec, tenantId: '' }, 'X', '/f', 'import').tenantId, undefined, 'no tenant is not sent');
  assert.equal(M.startRequest(rec, 'X', '/f', 'import').discardUnsaved, undefined, 'discarding unsaved work is never sent by default');
  assert.equal(M.startRequest(rec, 'X', '/f', 'import', true).discardUnsaved, true, 'only when the person said so');
  assert.equal(M.startProblem({ server: '', database: 'd' }, 'N', '/f'), 'Choose a SQL endpoint first.');
  assert.equal(M.startProblem({ server: 's', database: 'd' }, '  ', '/f'), 'Give the model a name.');
  assert.equal(M.startProblem({ server: 's', database: 'd' }, 'N', ' '), 'Choose an empty folder to keep it in.');
  assert.equal(M.startProblem({ server: 's', database: 'd' }, 'a/b', '/f'), 'A model name cannot hold a slash.', 'a name that would escape the folder is refused here too');
  assert.equal(M.startProblem({ server: 's', database: 'd' }, 'N', '/f'), null);
}

// ---- the engine's own shape (StartPlanViewBuilder): the page reads what sf-core really sends -----------------------------
{
  const real = (o) => JSON.parse(JSON.stringify(startPlanRealShape(o)));
  // CanBuild is true ONLY once the approval matches (StartPlanViewBuilder.Gate), so a plan nobody approved has CanBuild false
  // and the page must still let the person approve it.
  const fresh = real({});
  assert.equal(fresh.canBuild, false); assert.match(fresh.blockedBecause, /not approved/);
  const a = M.actionsOf(fresh, { pro: false });
  assert.equal(a.approve.enabled, true, 'an unapproved plan can be approved even though CanBuild is false');
  assert.equal(a.approveAndBuild.enabled, true);
  assert.equal(a.build.enabled, false, 'but not built: nothing is approved');
  const ok = real({ approved: true });
  assert.equal(M.approvalOf(ok).state, 'current');
  assert.equal(M.actionsOf(ok, { pro: false }).build.enabled, true, 'approved and CanBuild: it can be built');
  assert.equal(M.actionsOf({ ...ok, canBuild: false, blockedBecause: 'Another model is open, so this plan is parked.' }, { pro: false }).build.enabled, false, 'a parked plan cannot be built');
  assert.equal(M.actionsOf({ ...ok, canBuild: false, blockedBecause: 'Another model is open, so this plan is parked.' }, { pro: false }).build.why, 'Another model is open, so this plan is parked.', 'and says why in the engine\'s words');
  const deciding = real({ decide: 'deciding' });
  assert.equal(M.actionsOf(deciding, { pro: true }).approve.enabled, false, 'while Jev decides nothing is approved');
  assert.match(M.actionsOf(deciding, { pro: true }).approve.why, /still working/, 'in the engine\'s words');

  // Links: named by identity, titled "A to B on Col", Kept = on. The drawing reads the title and the kept flag.
  const g = M.linkGraph(fresh);
  assert.ok(g, 'a plan whose links carry only identities and titles still has a drawing');
  assert.deepEqual(g.nodes.map((n) => n.name).sort(), ['Clients', 'Projects', 'Timesheets']);
  assert.equal(g.edges.length, 3); assert.equal(g.edges.filter((e) => e.active).length, 2, '"(off)" and Kept=false draw as kept off');
  const lines = M.linesOf(fresh);
  const off = lines.find((l) => l.item.objectRef === 'relationship:r-3');
  assert.equal(M.lineState(off, fresh, { pro: false }).text, 'Kept off', 'a link that is off says so, not "Left out"');
  // A base measure carries no formula: the second line is the reason, never a blank.
  const m1 = lines.find((l) => l.item.kind === 'create_measure');
  assert.equal(M.lineSub(m1), 'A total people can use.');
  // Grades.Now is null until the engine measures the raw tables; the page says so rather than showing a dash.
  const html = renderToStaticMarkup(React.createElement(load('startplan').StartPlanBody, { view: fresh, pro: false, busy: null, error: null, sentence: null, changed: new Set(), onEdit() {}, onNeed() {}, onApprove() {}, onBuild() {}, onApproveAndBuild() {}, onDiscard() {}, onRetry() {} }));
  const text = readable(html);
  assert.match(text, /71/, 'the grade after the build is shown');
  assert.doesNotMatch(text, /AI readiness - now/, 'and an unknown grade now is not drawn as a dash');
  assert.match(text, /not scored yet|not measured yet/i, 'it says the raw tables are not scored yet');
  // The engine's Summary ("3 changes.") does not replace the section's plain blurb.
  assert.match(text, /What is built, keys, totals, formats/);
  // The engine's source names show only where they differ (sf-core sends null when equal).
  assert.match(text, /fact_timesheet/);
  // Sentence splitting must not cut a question at a dot inside a server name.
  const grant = real({ decide: 'waiting_grant' }).needsYou[0];
  assert.equal(M.needTitle(grant), 'Jev needs your permission to see the tables of ContosoOps on contoso.datawarehouse.fabric.microsoft.com before it decides anything.');
  assert.deepEqual(M.needAction(real({ decide: 'waiting_grant' }), grant, 'Allow'), { kind: 'grant', args: ['abc123', 'human'] });
  assert.deepEqual(M.needAction(real({ decide: 'waiting_grant' }), grant, 'Not now').edit, { op: 'answer', target: 'grant', value: 'Not now' });
  assert.equal(M.needLeanLabel(grant), 'Suggested', 'a grant is suggested, not something Jev leans to');
}

// ===================================================================================================
// 2. The page, rendered.
// ===================================================================================================
const P = load('startplan');
assert.equal(typeof P.StartPlanBody, 'function', 'the body is presentational and exported, so it is rendered here');
assert.equal(typeof P.StartForm, 'function', 'the start form is exported too');
const noop = () => {};
const handlers = { onEdit: noop, onNeed: noop, onApprove: noop, onBuild: noop, onApproveAndBuild: noop, onDiscard: noop, onRetry: noop };
const render = (view, pro, extra = {}) => renderToStaticMarkup(React.createElement(P.StartPlanBody, { view, pro, busy: null, error: null, sentence: null, changed: new Set(), ...handlers, ...extra }));

{
  const html = render(asView(proState()), true); const text = readable(html);
  for (const t of ['Tables and columns', 'Links and dates', 'Measures', 'Words for Copilot', 'Best practice']) assert.match(text, new RegExp(t), `the section ${t} is on the page`);
  const order = ['tables', 'links', 'measures', 'words', 'bestpractice'].map((id) => html.indexOf(`data-testid="sp-section-${id}"`));
  assert.ok(order.every((n) => n > 0), 'all five sections are drawn');
  assert.deepEqual([...order].sort((a, b) => a - b), order, 'in the order a person reads them');
  assert.match(text, /Needs you/, 'the Needs you band is above the sections');
  assert.ok(html.indexOf('data-testid="sp-needs"') > 0 && html.indexOf('data-testid="sp-needs"') < order[0], 'above them');
  assert.match(text, /Should project budgets add up\?/);
  assert.match(text, /Jev leans/, 'the lean is marked on the option Jev leans to');
  assert.match(text, /Approve and build/);
  assert.match(text, /Contoso Consulting/);
  assert.match(text, /ContosoOps/, 'the source is named');
  assert.match(text, /fact_timesheet/, 'the source name stands beside the model name (mockup C)');
  assert.match(text, /AI readiness/); assert.match(text, /Model quality|Best practice/);
  assert.match(text, /35/, 'the grade now');
  assert.match(text, /92/, 'and after the build');
  assert.match(text, /data-testid="sp-link-drawing"|Links/, 'the page has the links');
  assert.match(html, /data-testid="sp-link-drawing"/, 'the small link drawing sits at the top of Links and dates (mockup B)');
  assert.doesNotMatch(text, /Free plan\./, 'Pro does not read the Free notice');
  assert.match(text, /One undo takes the whole build back/, 'the build is one step and says so');
}
{
  const text = readable(render(asView(freeState()), false));
  assert.match(text, /Free plan\./, 'Free says it is Free');
  assert.match(text, /Jev is Pro/);
  assert.doesNotMatch(text, /Default, no Jev|plain defaults/, 'the real Free plan has no plain-default line, and the page claims none');
  assert.doesNotMatch(text, /Jev leans|Jev asks|Jev is deciding|Jev needs your permission/, 'Free is never offered a Jev question or Jev progress');
  assert.match(text, /Approve and build/, 'Free builds too');
  assert.match(text, /Best practice fixes come after the build, one at a time/);
  assert.doesNotMatch(text, /Pro decides|Unlock/, 'and it is not sold anything in the middle of the plan');
}
{
  // An approval made stale by the assistant's words: shown as stale, with Build gone and Approve back.
  const s = proState(); const v0 = asView(s);
  startPlanApprove(s, v0.digest); startPlanAssistantWords(s, 2);
  const text = readable(render(asView(s), true));
  assert.match(text, /no longer matches/i, 'the stale approval is told, not hidden');
  assert.match(text, /Approve and build/, 'and Approve is offered again');
  // A standing approval: the button is Build and it says who may press it.
  const s2 = proState(); const v1 = asView(s2); startPlanApprove(s2, v1.digest);
  const t2 = readable(render(asView(s2), true));
  assert.match(t2, /You approved this plan/i);
  assert.match(t2, /you or your assistant can build it/i);
  assert.match(t2, /(^|\s)Build(\s|$)/, 'the button is now Build');
  assert.doesNotMatch(t2, /Approve and build/, 'and Approve and build is gone');
}
{
  const waiting = readable(render(asView(makeStartPlanState({ decide: 'waiting_grant', granted: false })), true));
  assert.match(waiting, /Jev needs your permission to see the tables of ContosoOps/, 'the first question is the grant, and it names the source');
  assert.match(waiting, /never sees rows/i, 'with what it will not see');
  assert.match(waiting, /waiting for you/i);
  const inc = readable(render(asView(makeStartPlanState({ decide: 'incomplete', reason: 'Jev stopped after 6 rounds.' })), true));
  assert.match(inc, /not finished/i);
  assert.match(inc, /Jev stopped after 6 rounds/);
}
{
  // The form: a saved endpoint first, a name, a folder, and one honest line about what happens.
  const rec = { id: 'sq-1', name: 'Contoso warehouse', server: 'contoso.datawarehouse.fabric.microsoft.com', database: 'ContosoOps', authMode: 'interactive', tenantId: 'contoso.com' };
  const text = readable(renderToStaticMarkup(React.createElement(P.StartForm, { sources: [rec], busy: false, error: null, onStart: noop, onAddOnConnections: noop, pro: true })));
  assert.match(text, /Contoso warehouse/); assert.match(text, /Model name/); assert.match(text, /Folder/);
  assert.match(text, /empty folder/i, 'the folder must be empty, and it says so');
  assert.match(text, /No rows are copied|never rows/i, 'and what is read');
  assert.match(text, /Read the source|Start/, 'one button to start');
  const dirty = readable(renderToStaticMarkup(React.createElement(P.StartForm, { sources: [rec], busy: false, error: null, onStart: noop, onAddOnConnections: noop, pro: true, needsDiscard: true })));
  assert.match(dirty, /changes that are not saved/, 'an open model with unsaved changes is said out loud before it is replaced');
  assert.match(dirty, /Discard those changes and read the source/, 'and the button says what it will do');
  assert.doesNotMatch(text, /not saved/, 'a clean start says none of that');
  const none = readable(renderToStaticMarkup(React.createElement(P.StartForm, { sources: [], busy: false, error: null, onStart: noop, onAddOnConnections: noop, pro: false })));
  assert.match(none, /Connections/, 'with none saved it points at Connections');
}
{
  // Hero run 5 (9 Oct), B3: a start that took a free name ("Gold 2") says so, in one calm line on the plan page. The engine sends the sentence in the start's answer only,
  // so the page holds it for that plan. Another plan, or none, shows nothing.
  const said = 'A model called Gold is already in this folder, so this one is called Gold 2, in a new folder of that name.';
  assert.equal(typeof P.holdStartNotice, 'function', 'the start form keeps the sentence the engine sent');
  assert.equal(typeof P.StartNoticeLine, 'function', 'and the plan page draws it');
  P.holdStartNotice({ exists: true, id: 'plan-1', notice: said, sections: [], needsYou: [] });
  const line = renderToStaticMarkup(React.createElement(P.StartNoticeLine, { planId: 'plan-1' }));
  assert.match(readable(line), /Gold 2/, 'the line shows the new name');
  assert.match(line, /data-testid="sp-start-notice"/);
  assert.equal(renderToStaticMarkup(React.createElement(P.StartNoticeLine, { planId: 'plan-2' })), '', 'a different plan shows none');
  P.holdStartNotice({ exists: true, id: 'plan-3', notice: null, sections: [], needsYou: [] });
  assert.equal(renderToStaticMarkup(React.createElement(P.StartNoticeLine, { planId: 'plan-1' })), '', 'a start with nothing to say clears the line');
  // Item 2: while the source is read the form offers Cancel, and not otherwise.
  const rec = { id: 'sq-1', name: 'Contoso warehouse', server: 'contoso.datawarehouse.fabric.microsoft.com', database: 'ContosoOps', authMode: 'interactive', tenantId: 'contoso.com' };
  const reading = renderToStaticMarkup(React.createElement(P.StartForm, { sources: [rec], busy: true, error: null, onStart: noop, onCancel: noop, onAddOnConnections: noop, pro: true }));
  assert.match(reading, /data-testid="sp-form-cancel"/, 'a read in progress can be cancelled');
  const idle = renderToStaticMarkup(React.createElement(P.StartForm, { sources: [rec], busy: false, error: null, onStart: noop, onCancel: noop, onAddOnConnections: noop, pro: true }));
  assert.doesNotMatch(idle, /sp-form-cancel/, 'and an idle form has no Cancel');
}

// ===================================================================================================
// 3. The wire names are the contract's verbs.
// ===================================================================================================
{
  const contract = readFileSync(join(repo, 'Semanticus.Engine/StartPlan/StartPlanContract.cs'), 'utf8');
  const verbs = [...contract.matchAll(/Task<[\w<>]+>\s+(\w+)Async\(/g)].map((m) => m[1].charAt(0).toLowerCase() + m[1].slice(1));
  assert.ok(verbs.length >= 9 && verbs.includes('approveStartPlan') && verbs.includes('buildStartPlan'), `the contract's verbs were found: ${verbs.join(', ')}`);
  const calls = new Set();
  for (const f of ['startplan.tsx', 'plan.tsx', 'spec.tsx']) for (const m of src(f).matchAll(/\brpc\s*(?:<[^()]*?>)?\s*\(\s*(['"])([^'"]+)\1/g)) calls.add(m[2]);
  const mine = [...calls].filter((n) => /StartPlan|StartModel|StartDisclosure|startPlan/.test(n));
  assert.ok(mine.length >= 6, `the page calls the start plan verbs: ${mine.join(', ')}`);
  for (const n of mine) assert.ok(verbs.includes(n), `${n} is a verb of IStartPlanEngine`);
  for (const n of ['getStartPlan', 'editStartPlan', 'grantStartDisclosure', 'approveStartPlan', 'buildStartPlan', 'startModelFromSql']) assert.ok(calls.has(n), `${n} is called by name, as a literal, so the name parity test can see it`);
  // Join 7 (UAT-J7-S1-002): the person edits the words on a line with the very write the assistant uses, from the person's own door, so the line becomes theirs. Finding what is
  // still open to write stays the assistant's: the page never reads that list.
  assert.ok(calls.has('writeStartWords') && !calls.has('getStartWords'), 'the person edits words with write_start_words; the open list is the assistant\'s');
}

// ===================================================================================================
// 4. The copy.
// ===================================================================================================
{
  const strings = [];
  const walk = (o) => { if (typeof o === 'string') strings.push(o); else if (o && typeof o === 'object') for (const v of Object.values(o)) walk(v); };
  walk(M.COPY);
  const rendered = [render(asView(proState()), true), render(asView(freeState()), false), render(asView(makeStartPlanState({ decide: 'incomplete', reason: 'x' })), true)].map(readable);
  const all = [...strings, ...rendered, readable(renderToStaticMarkup(React.createElement(P.StartForm, { sources: [], busy: false, error: null, onStart: noop, onAddOnConnections: noop, pro: true })))];
  for (const s of all) {
    assert.doesNotMatch(s, /[—–]/, 'no em or en dashes in anything a person reads');
    assert.doesNotMatch(s, /one[- ]click/i, 'never "one click"');
    assert.doesNotMatch(s, /[\u{1F300}-\u{1FAFF}✅❌]/u, 'no colourful emoji');
  }
  assert.ok(strings.length > 20, 'the copy table is large enough to mean something');
}

// ===================================================================================================
// 5. The wiring.
// ===================================================================================================
{
  const plan = src('plan.tsx');
  assert.match(plan, /from '\.\/startplan'/, 'the Plan mounts the start plan');
  assert.match(plan, /useStartPlan\(sessionId, revision, changeNonce\)/, 'and reads it again whenever the model or the engine says it moved');
  assert.match(src('startplan.tsx'), /rpc<[^>]*>\('getStartPlan'\)/, 'from the engine');
  assert.match(plan, /useConsentCard\([^)]*!startPlan|useConsentCard\([^)]*startPlanActive/, 'the generic Pro permission card stands aside while a start plan is open: the start plan asks its own, naming the source (S2)');
  const spec = src('spec.tsx');
  assert.match(spec, /onStartPlan/, 'the SQL wizard hands an empty model to the start plan');
  const bridge = src('bridge.ts');
  assert.match(bridge, /startPlanDidChange/, 'the page hears the engine\'s startPlan/didChange');
  const ext = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
  assert.match(ext, /onNotification\('startPlan\/didChange'/, 'and the host forwards it');
  assert.match(ext, /'semanticus\.draftFromSqlSource'/);
}

// A time measure is a line the view makes from its base measure (join 6): Drop works on it, and a formula cannot be pinned on it, so no Pin button.
{
  const detail = (l) => renderToStaticMarkup(React.createElement(P.LineDetail, { line: l, view: { sections: [] }, tier: { pro: true }, busy: false, onEdit: noop }));
  const base = { item: { id: 'tm', kind: 'create_measure', after: 'TOTALYTD([Total], \'Date\'[Date])' }, decidedBy: 'rule', kept: true };
  assert.match(detail(base), /data-testid="sp-pin"/, 'a formula the plan wrote on a stored line can still be pinned');
  const derived = detail({ ...base, derived: true });
  assert.match(derived, /data-testid="sp-drop"/, 'a time measure can be dropped');
  assert.doesNotMatch(derived, /data-testid="sp-pin"/, 'and has no Pin the engine would refuse');
  assert.match(detail({ ...base, derived: true, kept: false }), /data-testid="sp-keep"/, 'a dropped time measure can be kept again');
}

console.log('startplan-studio: ok');
