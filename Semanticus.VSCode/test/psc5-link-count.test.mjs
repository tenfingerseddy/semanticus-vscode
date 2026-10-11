// Plan statement check 5, N1 (builder): the start plan's "N links, M on" count and its drawing equal the links the plan lists, which are the links the build writes.
// The engine lists every link the build can make (a declared one as "A to B on Col", a guessed one as "Linked A[x] to B[y]: how, and key", either with " (off)" when it is
// written switched off); verify-psc5-guessed-links.test.mjs holds the one-shape proof, this one holds the count on the two shapes the check measured: a source that declares its
// keys with some links guessed beside them, and a source that declares none (every link guessed, which used to draw nothing and show no count at all).
import assert from 'node:assert/strict';
import { makeLoader, req } from './helpers/webview-load.mjs';
import { startPlanRealShape } from '../tools/uishot/fixtures/startplan-fixture.mjs';

req('react');
const load = makeLoader({
  './startplan.css': {},
  './bridge': {
    rpc: async () => { throw new Error('the test loader answers no calls'); },
    pickWorkingCopyFolder: async () => null, onStartPlanChange: () => () => {}, runHostCommand: () => {},
    onDidChange: () => () => {}, onPlanChange: () => () => {}, navigateInStudio: () => {},
  },
  './pro': { useTier: () => 'free', upgradeToPro: () => {}, PENDING_ACCESS: 'Checking your Pro access…' },
  './connectionshub': { listSqlSources: async () => [], openSqlSources: () => {} },
  './connection': { useConnection: () => ({ openConnections: () => {} }) },
});
const M = load('startplan-model');

const WHY = 'A guess from the names: the source declares no key for these two tables. Drop it if it is wrong.';
const guessed = (id, from, fromCol, to, toCol, kept, tail = '') => ({
  item: { kind: 'create_relationship', objectRef: 'relationship:' + id, objectName: id, title: `Linked ${from}[${fromCol}] to ${to}[${toCol}]: ${fromCol} ends with ${toCol}, and ${toCol} is unique in ${to} (from the sample)${tail}`,
    source: 'deterministic', status: 'proposed', id: 'relationship:' + id, ruleId: 'LINK-NAME-GUESS' },
  decidedBy: 'rule', kept, why: WHY, held: null,
});
const declared = (id, from, to, col, kept) => ({
  item: { kind: 'create_relationship', objectRef: 'relationship:' + id, objectName: id, title: `${from} to ${to} on ${col}${kept ? '' : ' (off)'}`, source: 'deterministic', status: 'proposed', id: 'relationship:' + id },
  decidedBy: 'rule', kept, why: 'The source declares this link.', held: null,
});

const withLinks = (lines) => {
  const view = JSON.parse(JSON.stringify(startPlanRealShape({})));
  const sec = view.sections.find((s) => s.id === 'links');
  const date = sec.groups[0].lines.find((l) => l.item.kind === 'mark_date_table');
  sec.groups[0].lines = [...lines, date];
  return view;
};
const listed = (view) => view.sections.find((s) => s.id === 'links').groups.flatMap((g) => g.lines).filter((l) => l.item.kind === 'create_relationship');

const notBuilt = (l) => l.item.status === 'rejected' || /\(not built\b[^)]*\)\s*$/.test(l.item.title);

// A source that declares its keys, with guessed links beside the declared ones (the J8 keyed shape: 13 listed, 13 built, 6 on). The last guessed line below is "(not built: ...)":
// the build does not write it, so the count and the drawing leave it out (check 5, round 2: it used to be counted as a link that is off).
{
  const view = withLinks([
    declared('d-1', 'Timesheets', 'Projects', 'ProjectKey', true), declared('d-2', 'Projects', 'Clients', 'ClientKey', true), declared('d-3', 'Timesheets', 'Clients', 'ClientKey', false),
    guessed('g-1', 'Clients', 'ONBRD_DT_KEY', 'Date', 'DATE_KEY', false, ' (off)'), guessed('g-2', 'Projects', 'START_DT_KEY', 'Date', 'DATE_KEY', true),
    guessed('g-3', 'Projects', 'END_DT_KEY', 'Date', 'DATE_KEY', false, ' (not built: a table or column it needs is left out)'),
  ]);
  const g = M.linkGraph(view);
  const built = listed(view).filter((l) => !notBuilt(l));
  assert.equal(built.length, listed(view).length - 1, 'one line of the six is not built');
  assert.equal(g.edges.length, built.length, 'the count is every link the build writes, the guessed ones too, and not a line that is not built');
  assert.equal(g.edges.filter((e) => e.active).length, built.filter((l) => l.kept).length, 'and the links on are the links kept');
  assert.ok(!g.edges.some((e) => e.from === 'Projects' && e.to === 'Date' && !e.active && /END/.test(e.note ?? '')), 'the not-built END_DT_KEY line is not drawn');
  assert.deepEqual(g.edges.filter((e) => e.from === 'Projects' && e.to === 'Date').map((e) => e.active), [true], 'the date role that is built is drawn, the one that is not built is not');
}

// A source with two date roles of one table, both built, one on and one off, are both drawn.
{
  const view = withLinks([guessed('g-1', 'Projects', 'START_DT_KEY', 'Date', 'DATE_KEY', true), guessed('g-2', 'Projects', 'END_DT_KEY', 'Date', 'DATE_KEY', false, ' (off)')]);
  assert.deepEqual(M.linkGraph(view).edges.map((e) => e.active), [true, false], 'both date roles of one table are drawn, one on and one off');
}

// A dropped guess (status "rejected") is a line the person sees and the build does not write.
{
  const dropped = guessed('g-3', 'Projects', 'ClientKey', 'Clients', 'ClientKey', false);
  dropped.item.status = 'rejected'; dropped.decidedBy = 'person';
  const view = withLinks([guessed('g-1', 'Timesheets', 'ProjectKey', 'Projects', 'ProjectKey', true), dropped]);
  assert.equal(M.linkGraph(view).edges.length, 1);
}

// A source that declares no keys: every link is a guess. The page used to show no count and draw nothing.
{
  const view = withLinks([
    guessed('g-1', 'Timesheets', 'WORK_DT_KEY', 'Date', 'DATE_KEY', true), guessed('g-2', 'Timesheets', 'ProjectKey', 'Projects', 'ProjectKey', true),
    guessed('g-3', 'Projects', 'ClientKey', 'Clients', 'ClientKey', false, ' (off)'),
  ]);
  const g = M.linkGraph(view);
  assert.ok(g, 'a plan whose links are all guessed still has a drawing and a count');
  assert.equal(g.edges.length, 3);
  assert.equal(g.edges.filter((e) => e.active).length, 2);
}

// The structured list the engine sends (the links the build writes) wins over the lines, an empty list draws nothing, and a plan with no link line still draws nothing.
assert.deepEqual(M.linkGraph({ ...withLinks([guessed('g-1', 'A', 'x', 'B', 'y', true)]), links: [{ from: 'A', to: 'B', active: true }, { from: 'B', to: 'C', active: false }] }).edges.length, 2);
assert.equal(M.linkGraph({ ...withLinks([guessed('g-1', 'A', 'x', 'B', 'y', true)]), links: [] }), null, 'the build writes no link, so none is drawn and no count is shown');
assert.deepEqual(M.linkGraph({ ...withLinks([]), links: [{ from: 'A', to: 'B', active: true, id: 'relationship:r1' }] }).edges.map((e) => e.id), ['relationship:r1'], 'a drawn link keeps the id of its line');
assert.equal(M.linkGraph(withLinks([])), null);
console.log('psc5-link-count: ok');
