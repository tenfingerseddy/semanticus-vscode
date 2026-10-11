// Plan statement check 5 (verifier, 5 Oct): the start plan's link drawing and its "N links, M on" count leave out every link the plan
// guessed from the names. On the J8 source with its declared keys the page read "9 links, 6 on" and drew 9 lines while the plan listed and
// the build wrote 13 links (4 guessed date roles: ONBRD, HIRE, START and END to dim_date). On the same source with no keys declared the
// page showed no link count and no drawing at all while the build wrote 9 links. The engine titles a guessed link
// "Linked A[x] to B[y]: how, and key" (LinkGuesser.Words, StartPlanViewBuilder.LinkGroups) and names it by its identity, and
// linkGraph (startplan-model.ts) reads only "A to B on Col" titles or an "A -> B" name. Left failing on purpose.
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

const view = JSON.parse(JSON.stringify(startPlanRealShape({})));
const links = view.sections.find((s) => s.id === 'links');
const line = (id, title, kept) => ({
  item: { kind: 'create_relationship', objectRef: 'relationship:' + id, objectName: id, title, source: 'deterministic', status: 'proposed', id: 'relationship:' + id, ruleId: 'LINK-NAME-GUESS' },
  decidedBy: 'rule', kept, why: 'A guess from the names: the source declares no key for these two tables. Drop it if it is wrong.', held: null,
});
// The engine's own words for a guessed link (LinkGuesser.Words), as the run's Studio showed them.
links.groups[0].lines.push(
  line('g-1', 'Linked Timesheets[WORK_DT_KEY] to Clients[DATE_KEY]: WORK_DT_KEY ends with DATE_KEY, and DATE_KEY is unique in Clients (from the sample)', true),
  line('g-2', 'Linked Projects[START_DT_KEY] to Clients[DATE_KEY]: START_DT_KEY ends with DATE_KEY, and DATE_KEY is unique in Clients (from the sample) (off)', false),
);
const kept = links.groups.flatMap((g) => g.lines).filter((l) => l.item.objectRef.startsWith('relationship:'));
const g = M.linkGraph(view);
assert.ok(g, 'the plan has links, so there is a drawing');
assert.equal(g.edges.length, kept.length, `every link the plan lists is drawn and counted: ${kept.length} listed, ${g.edges.length} drawn`);
assert.equal(g.edges.filter((e) => e.active).length, kept.filter((l) => l.kept).length, 'and the count of links that are on includes the guessed ones');
console.log('verify-psc5-guessed-links: ok');
