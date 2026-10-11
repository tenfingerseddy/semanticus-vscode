// Plan statement check 5 fixes (verifier, 5 Oct): the start plan's "N links, M on" count and its drawing still disagree with the build once the
// person drops a guessed link or leaves a table out. Measured on ace10bf5 through the engine (StartModelFromSqlAsync on the keyless J8 shape with
// its primary keys, sample read, then drop_line on dim_project[CLNT_ID] to dim_client, or include_table dim_client off; then approve and build):
// the engine lists 9 link lines and the build writes 8 (dropped) or 7 (table left out), but linkGraph counts 9 both times and draws the dropped
// link and the two links to the left-out table as "kept off", which reads as built and switched off. A dropped guess is not built (status
// "rejected"); a line that ends "(not built: ...)" is not built either. The lines below are the engine's own words from that run. Left failing.
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

const GUESS = 'A guess from the names: the source declares no key for these two tables. Drop it if it is wrong.';
const NOT_BUILT = ' (not built: a table or column it needs is left out)';
const line = (n, from, fromCol, to, toCol, how, { kept = true, status = 'proposed', tail = '', why = GUESS } = {}) => ({
  item: { kind: 'create_relationship', objectRef: 'relationship:g' + n, objectName: 'g' + n, id: 'relationship:g' + n, ruleId: 'LINK-NAME-GUESS', source: 'deterministic', status,
    title: `Linked ${from}[${fromCol}] to ${to}[${toCol}]: ${how}${tail}` },
  decidedBy: status === 'rejected' ? 'person' : 'rule', kept, why, held: null,
});
const same = (c, t) => `same name, and ${c} is unique in ${t} (from the sample)`;
const ends = (c) => `${c} ends with DATE_KEY, and DATE_KEY is unique in dim_date (from the sample)`;
const rest = (clientTail, clientKept) => [
  line(2, 'fact_timesheet', 'EMP_NO', 'dim_staff', 'EMP_NO', same('EMP_NO', 'dim_staff')),
  line(3, 'fact_timesheet', 'PRJ_ID', 'dim_project', 'PRJ_ID', same('PRJ_ID', 'dim_project')),
  line(4, 'fact_timesheet', 'WORK_DT_KEY', 'dim_date', 'DATE_KEY', ends('WORK_DT_KEY')),
  line(5, 'fact_invoice', 'PRJ_ID', 'dim_project', 'PRJ_ID', same('PRJ_ID', 'dim_project')),
  line(6, 'fact_invoice', 'CLNT_ID', 'dim_client', 'CLNT_ID', same('CLNT_ID', 'dim_client'), { kept: clientKept, tail: clientTail }),
  line(7, 'fact_invoice', 'INV_DT_KEY', 'dim_date', 'DATE_KEY', ends('INV_DT_KEY')),
  line(8, 'fact_invoice', 'DUE_DT_KEY', 'dim_date', 'DATE_KEY', ends('DUE_DT_KEY'), { kept: false, tail: ' (off)' }),
  line(9, 'fact_invoice', 'PAID_DT_KEY', 'dim_date', 'DATE_KEY', ends('PAID_DT_KEY'), { kept: false, tail: ' (off)' }),
];
const withLinks = (lines) => {
  const view = JSON.parse(JSON.stringify(startPlanRealShape({})));
  const sec = view.sections.find((s) => s.id === 'links');
  const date = sec.groups[0].lines.find((l) => l.item.kind === 'mark_date_table');
  sec.groups[0].lines = [...lines, date];
  return view;
};

// The person dropped the guessed dim_project to dim_client link: 9 lines listed, 8 built (6 on).
{
  const view = withLinks([
    line(1, 'dim_project', 'CLNT_ID', 'dim_client', 'CLNT_ID', same('CLNT_ID', 'dim_client'),
      { kept: false, status: 'rejected', why: 'A guessed link, left out by you. It is not built. Keep it to bring it back.' }),
    ...rest('', true),
  ]);
  const g = M.linkGraph(view);
  assert.equal(g.edges.length, 8, `a dropped guess is not built, so it is not counted: the page says ${g.edges.length} links, the build writes 8`);
  assert.equal(g.edges.filter((e) => e.active).length, 6);
  assert.ok(!g.edges.some((e) => e.from === 'dim_project' && e.to === 'dim_client'), 'the dropped link is not drawn as a link that is kept off');
}

// The person left dim_client out: 9 lines listed, 7 built (5 on).
{
  const view = withLinks([
    line(1, 'dim_project', 'CLNT_ID', 'dim_client', 'CLNT_ID', same('CLNT_ID', 'dim_client'), { kept: false, tail: NOT_BUILT }),
    ...rest(NOT_BUILT, false),
  ]);
  const g = M.linkGraph(view);
  assert.equal(g.edges.length, 7, `a link to a table that is left out is not built, so it is not counted: the page says ${g.edges.length} links, the build writes 7`);
  assert.equal(g.edges.filter((e) => e.active).length, 5);
}
console.log('verify-psc5r-dropped-links: ok');
