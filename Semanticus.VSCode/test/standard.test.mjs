// Review > Your standard (Kane picked option B, 18 Oct 2026): the page's pure half (standard-model.ts), its wiring into the Studio (the route, the last place on the Review row,
// the places list, the activity follow), and the proof that the shipped page reads the engine and carries no typed numbers. The page against a mocked engine, with its grid and every
// control pressed, is tools/uishot/standard-drive.mjs.
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { makeLoader, makeHookRuntime, ambientReact, settle, webview } from './helpers/webview-load.mjs';

let failures = 0;
async function check(name, fn) {
  try { await fn(); console.log(`ok - ${name}`); } catch (e) { failures++; console.error(`not ok - ${name}\n${e.stack}`); }
}
const src = (f) => readFileSync(join(webview, 'src', f), 'utf8');
const M = makeLoader({})('standard-model.ts');
const R = makeLoader({})('route.ts');

const PARTS = ['checks', 'names', 'formats', 'folders', 'descriptions', 'synonyms', 'keys', 'instructions'];
const mkParts = () => PARTS.map((id) => ({ id, name: id[0].toUpperCase() + id.slice(1), ask: `Ask ${id}`, choices: [{ id: 'a', label: `${id} A` }, { id: 'b', label: `${id} B` }, { id: 'c', label: `${id} C` }] }));
const pick = (x) => Object.fromEntries(PARTS.map((p) => [p, x]));
const view = (picks = {}, startsFrom = 'balanced') => ({
  name: 'Contoso', origin: 'model', where: 'Clients / Contoso / contoso.standard.yaml', path: '/x/contoso.standard.yaml', use: '', userPath: '', userExists: false, notes: [], hasComments: false, startsFrom, picks: { ...pick('b'), ...picks },
  presets: [{ id: 'light', name: 'Light', line: 'l', picks: pick('a') }, { id: 'balanced', name: 'Balanced', line: 'b', picks: pick('b') }, { id: 'strict', name: 'Strict', line: 's', picks: pick('c') }],
  parts: mkParts(), yaml: 'standard: Contoso',
});

await check('the lines under the presets are exactly the parts that differ from the preset it starts from', () => {
  assert.deepEqual(M.overrides(view()).map((p) => p.id), []);
  assert.deepEqual(M.overrides(view({ formats: 'c', folders: 'a' })).map((p) => p.id), ['formats', 'folders']);
  assert.deepEqual(M.overrides(view({ formats: 'a' }, 'light')).map((p) => p.id), PARTS.filter((p) => p !== 'formats'), 'against Light every other part is a change');
});

await check('moving to another preset writes the person\'s own lines and resets every other part, so the new preset shows through', () => {
  const v = view({ formats: 'c' });
  const patch = M.presetPatch(v, 'strict');
  assert.equal(patch.startsFrom, 'strict');
  assert.equal(patch.picks.formats, 'c');
  assert.equal(patch.picks.checks, 'c', 'the check level cannot be reset in the engine, so it is written, and it equals the preset');
  assert.ok(patch.reset.includes('names') && !patch.reset.includes('formats') && !patch.reset.includes('checks'), 'a part that was not changed is reset to follow the new preset');
  assert.equal(M.presetPatch(v, 'nope'), null);
});

await check('a count says what the standard does here in a few words, and "No change" when it does nothing', () => {
  assert.equal(M.countWords({ now: 34, plan: 4 }), '34 now · 4 for the Plan');
  assert.equal(M.countWords({ now: 0, plan: 4 }), '4 for the Plan');
  assert.equal(M.countWords({ now: 7, plan: 0 }), '7 now');
  assert.equal(M.countWords({ now: 0, plan: 0 }), 'No change');
  assert.equal(M.effectWords({ now: 0, plan: 0, examples: [], say: '61 of 150 count' }), '61 of 150 count');
  assert.equal(M.effectWords({ now: 7, plan: 0, examples: [] }), '7 now');
  assert.equal(M.effectWords({ now: 0, plan: 5, examples: [] }), '5 on the Plan');
  assert.equal(M.effectWords(undefined), '');
});

await check('"and N more" counts the changes the examples do not show', () => {
  assert.equal(M.moreThanShown({ now: 12, plan: 0, examples: [1, 2, 3] }), 9);
  assert.equal(M.moreThanShown({ now: 2, plan: 0, examples: [1, 2, 3] }), 0);
});

await check('grades use the bands the rest of the product uses', () => {
  assert.deepEqual([95, 90, 89, 80, 70, 60, 59].map(M.gradeOf), ['A', 'A', 'B', 'B', 'C', 'D', 'F']);
});

await check('an engine that does not know the standard tools gets one plain line, not the raw text', () => {
  assert.equal(M.loadError(new Error('Method not found: getStandard')), M.OLD_ENGINE);
  assert.equal(M.loadError('something odd happened'), 'something odd happened');
  assert.equal(M.loadError(null), M.NO_ENGINE);
  for (const t of [M.OLD_ENGINE, M.NO_ENGINE]) assert.ok(!t.includes('—') && !/RPC|JSON|exception/i.test(t));
});

await check('"Copy as a page" writes one short sentence per part and names what differs from the preset', () => {
  const t = M.standardAsPage(view({ formats: 'c' }));
  assert.match(t, /^Contoso standard/);
  assert.match(t, /Starts from Balanced/);
  for (const p of M.PRESET_IDS) assert.ok(typeof p === 'string');
  assert.equal(t.split('\n').filter((l) => /^(Checks|Names|Formats|Folders|Descriptions|Synonyms|Keys|Instructions):/.test(l)).length, 8);
  assert.match(t, /Different from Balanced: formats\./);
  assert.ok(!M.standardAsPage(view()).includes('Different from'), 'nothing differs, nothing is said');
  assert.ok(!t.includes('—'));
});

// ---- the engine's real answers (shapes from Semanticus.Engine/Standard/*.cs, camelCase on the wire)
const spec = (o = {}) => ({ standard: 'Balanced', startsFrom: 'balanced', checks: { level: 'balanced', off: [], must: [] }, names: 'leave', formats: {}, folders: 'by-subject, over 10', descriptions: 'measures, tables', synonyms: '2, measures and tables', keysAndTotals: 'hide keys, no-sum ids', aiInstructions: 'none', ...o });
const presets = [spec({ standard: 'Light', startsFrom: 'light', checks: { level: 'light' }, folders: 'none', descriptions: 'none', synonyms: 'none' }), spec(), spec({ standard: 'Strict', startsFrom: 'strict', checks: { level: 'strict' }, names: 'title-case', formats: { percent: '0.0%', count: '#,0' }, folders: 'by-subject, always', descriptions: 'all-visible', synonyms: '2, all-visible', keysAndTotals: 'hide keys and technical, no-sum ids' })];
const state = (o = {}) => ({ name: 'Contoso', source: 'model-file', path: '/home/me/Contoso.SemanticModel/.semanticus/standard.yaml', userPath: '/home/me/.semanticus/standard.yaml', userExists: false, effective: spec(), written: spec(), fileText: '# mine\nstandard: Contoso\n', notes: [], ...o });

await check('getStandard becomes the page view: picks are the engine\'s own values, the presets keep their order, a hand-written value is still a choice', () => {
  const v = M.viewFromWire({ standard: state({ effective: spec({ folders: 'by-subject, over 25', formats: { percent: '0.0%', count: '#,0' } }) }), presets });
  assert.equal(v.origin, 'model');
  assert.equal(v.startsFrom, 'balanced');
  assert.equal(v.picks.folders, 'by-subject, over 25');
  assert.ok(v.parts.find((p) => p.id === 'folders').choices.some((c) => c.id === 'by-subject, over 25'), 'the value from the file is a choice, so the menu can show it');
  assert.equal(v.picks.formats, 'percent 0.0%, count #,0');
  assert.deepEqual(v.presets.map((p) => p.id), ['light', 'balanced', 'strict']);
  assert.equal(v.presets[2].picks.formats, 'percent 0.0%, count #,0', 'a preset\'s formats read as the same choice id');
  assert.deepEqual(M.overrides(v).map((p) => p.id), ['formats', 'folders']);
  assert.equal(v.hasComments, true);
  const own = M.viewFromWire({ standard: state({ fileText: '# A Semanticus standard. Every model that uses this file is held to it.\nstarts-from: "balanced"\n' }), presets });
  assert.equal(own.hasComments, false, 'the header the engine writes is not the person\'s comment');
  assert.equal(v.where, 'Contoso.SemanticModel / .semanticus / standard.yaml'.split(' / ').slice(-2).join(' / '));
});

await check('where a standard comes from: this model, Your default, a shared file, or the built in Balanced', () => {
  const o = (st) => M.viewFromWire({ standard: state(st), presets }).origin;
  assert.equal(o({}), 'model');
  assert.equal(o({ source: 'your-default' }), 'default');
  assert.equal(o({ use: '/home/me/.semanticus/standard.yaml' }), 'default', 'a model that points at Your default says so');
  assert.equal(o({ use: 'https://example.com/team.yaml' }), 'shared');
  const b = M.viewFromWire({ standard: state({ source: 'built-in', path: null, fileText: null }), presets });
  assert.equal(b.origin, 'builtin');
  assert.match(b.yaml, /^standard: Balanced/, 'the built in standard still shows as a file');
});

await check('previewStandard becomes counts, examples, and the Plan lines that need words', () => {
  const p = M.previewFromWire({ changesNow: 11, tasks: 2, scoreNow: 82, scoreAfter: 86, parts: [
    { part: 'formats', setting: 'percent 0.0%', changesNow: 4, tasks: 0, examples: ['Margin %: no format to 0.0%'] },
    { part: 'names', setting: 'title-case', changesNow: 0, tasks: 5, examples: ['sales_amt to Sales Amount'] },
    { part: 'keys-and-totals', setting: 'x', changesNow: 7, tasks: 0, examples: [] },
    { part: 'ai-instructions', setting: 'f.md', changesNow: 0, tasks: 1, examples: [] },
    { part: 'checks', setting: 'light', changesNow: 0, tasks: 0, examples: [], note: '61 of 150 open checks reach the Plan.' }] });
  assert.equal(p.now, 11); assert.equal(p.plan, 2);
  assert.equal(p.words, '4 formats · 7 keys and totals');
  assert.deepEqual(p.tasks.map((t) => t.part), ['names', 'instructions']);
  assert.equal(p.tasks[0].text, 'Rename 5 objects');
  assert.deepEqual(p.air, [82, 86, 86]);
  assert.equal(p.parts.formats.examples[0].what, 'Margin %: no format to 0.0%', 'an engine example line is shown whole, never split (names and formats can hold ": " and " to ")');
  assert.equal(p.parts.names.examples[0].what, 'sales_amt to Sales Amount');
  assert.equal(M.effectWords(p.parts.checks), '61 of 150 open checks reach the Plan.');
  assert.equal(M.effectWords(p.parts.keys), '7 now');
});

await check('applyStandard becomes what was done and how many Plan rows were added', () => {
  const a = M.appliedFromWire({ changesNow: 3, tasks: 1, planRows: ['p1'], parts: [{ part: 'folders', changesNow: 3, tasks: 0 }, { part: 'descriptions', changesNow: 0, tasks: 4 }] });
  assert.equal(a.changed, 3); assert.equal(a.planRows, 1); assert.equal(a.words, '3 folders');
  assert.equal(a.tasks[0].text, 'Write 4 descriptions');
});

await check('a change the page asks for becomes the JSON patch set_standard takes: part names, null resets, formats by kind', () => {
  const v = M.viewFromWire({ standard: state(), presets });
  assert.deepEqual(JSON.parse(M.wirePatch(v, { picks: { keys: 'leave' } })), { 'keys-and-totals': 'leave' });
  assert.deepEqual(JSON.parse(M.wirePatch(v, { picks: { checks: 'strict' } })), { checks: { level: 'strict' } });
  assert.deepEqual(JSON.parse(M.wirePatch(v, { picks: { formats: 'leave' } })).formats, { money: 'leave', percent: 'leave', count: 'leave' }, 'leave is written for every kind, so it also beats a preset that sets one');
  assert.deepEqual(JSON.parse(M.wirePatch(v, { picks: { formats: 'money $#,0.00, percent 0.0%' } })).formats, { money: '$#,0.00', percent: '0.0%', count: 'leave' });
  const preset = JSON.parse(M.wirePatch(v, M.presetPatch(v, 'strict')));
  assert.equal(preset.preset, 'strict'); assert.equal(preset.names, null); assert.equal(preset.checks.level, 'strict');
  const shared = JSON.parse(M.wirePatch(v, { use: 'https://example.com/team.yaml' }));
  assert.equal(shared.use, 'https://example.com/team.yaml'); assert.equal(shared.names, null, 'a shared file fills the parts the model does not write');
  assert.equal(JSON.parse(M.wirePatch(v, { use: 'default' })).use, '/home/me/.semanticus/standard.yaml');
  assert.deepEqual(JSON.parse(M.wirePatch(v, { use: null })), { use: null });
  assert.equal(M.wirePatch(v, {}), null);
});

await check('the formats choice ids round-trip, whatever order the engine lists the kinds in', () => {
  assert.equal(M.formatsId({ count: '#,0', percent: '0.0%' }), 'percent 0.0%, count #,0');
  assert.equal(M.formatsId({ money: 'leave' }), 'leave');
  assert.equal(M.formatsId(null), 'leave');
  assert.deepEqual(M.formatsOf('money $#,0.00, count #,0'), { money: '$#,0.00', percent: 'leave', count: '#,0' });
});

await check('Your standard is the last place on the Review row and a real route', () => {
  assert.equal(R.DESTINATION_OF_TOOL.standard, 'review');
  assert.deepEqual(R.landingOf('standard'), { tool: 'standard' });
  const app = src('App.tsx');
  const review = app.match(/\{ id: 'review', label: 'Review', tabs: \[([\s\S]*?)\] \}/)[1];
  const ids = [...review.matchAll(/id: '([a-z]+)'/g)].map((m) => m[1]);
  assert.equal(ids[ids.length - 1], 'standard', 'the page sits last on the Review row');
  assert.match(review, /label: 'Your standard'/);
  assert.match(app, /tab === 'standard'/);
});

await check('the places list and the host menu know the page, and the tools are the ones the assistant uses', () => {
  const map = JSON.parse(src('studio/places.json'));
  const p = map.places.find((x) => x.id === 'review/standard');
  assert.ok(p, 'review/standard is a place');
  assert.equal(p.tool, 'standard');
  assert.deepEqual([...p.tools].sort(), ['apply_standard', 'get_standard', 'preview_standard', 'set_standard']);
  assert.ok(readFileSync(join(webview, '..', 'src', 'extension.ts'), 'utf8').includes("label: 'Your standard', key: 'standard'"), 'the Review quick pick lists it');
});

await check('the shipped page reads the engine: four real calls, no typed numbers, no fixture, no prototype box', () => {
  const page = src('standard.tsx');
  for (const m of ['getStandard', 'previewStandard', 'setStandard', 'applyStandard']) assert.ok(page.includes(`'${m}'`), `${m} is called`);
  assert.ok(!/sproto|fixture|mock/i.test(page), 'no prototype or fixture in the shipped page');
  assert.ok(!/SPROTO|data-opt|dashed/.test(page));
  assert.ok(!existsSync(join(webview, 'src', 'sproto')), 'the prototype folder does not ship');
  // Apply is one press with no confirm; Undo goes through the same undo timeline as every other page.
  assert.ok(!/confirm\(|window\.confirm|Are you sure/.test(page), 'Apply does not ask');
  assert.match(page, /rpc\('undo', 'human'/);
  assert.match(page, /openFileInEditor/, 'File has Open in the editor');
});

await check('the words on the page: no em dash, no "Map", no "one click", no colourful emoji', () => {
  for (const f of ['standard-model.ts', 'standard.tsx', 'standard.css']) {
    const s = src(f);
    assert.ok(!s.includes('—'), `${f} has an em dash`);
    assert.ok(!/\bMap\b/.test(s.replace(/\.map\(|new Map|Map</g, '')), `${f} says Map`);
    assert.ok(!/one click|one-click/i.test(s), `${f} says one click`);
    assert.ok(!/[\u{1F300}-\u{1FAFF}]/u.test(s), `${f} has an emoji`);
  }
});

await check('the page uses tokens only: no raw colour and no pixel gap outside the token scale', () => {
  const css = src('standard.css');
  assert.ok(!/#[0-9a-fA-F]{3,8}\b/.test(css), 'a raw colour in standard.css');
  assert.ok(!/rgba?\(/.test(css), 'an rgb() colour in standard.css');
  assert.ok(!/(?:^|[\s:;])(?:gap|padding|margin)[a-z-]*:\s*[^;{}]*\b(?!1px)\d+px/m.test(css.replace(/calc\([^)]*\)/g, '')), 'a raw pixel spacing in standard.css');
});

await check('a shared standard keeps its address on failure and clears only the accepted address', async () => {
  const oldWindow = globalThis.window, oldDocument = globalThis.document;
  globalThis.window = { clearTimeout, setTimeout };
  globalThis.document = { addEventListener() {}, removeEventListener() {} };
  const pending = [];
  const pageRuntime = makeHookRuntime(), headRuntime = makeHookRuntime();
  const react = new Proxy(ambientReact, { get: (target, key) => key === 'useCallback' ? (fn, deps) => {
    const slot = target.useRef(null);
    if (!slot.current || deps.some((dep, i) => !Object.is(dep, slot.current.deps[i]))) slot.current = { fn, deps };
    return slot.current.fn;
  } : target[key] });
  const nodes = (node) => !node || typeof node !== 'object' ? [] : [node, ...[node.props?.children].flat(Infinity).flatMap(nodes)];
  try {
    const Page = makeLoader({
      './bridge': {
        onActivity: () => () => {}, onDidChange: () => () => {},
        rpc: (method, ...args) => method === 'setStandard'
          ? new Promise((resolve, reject) => pending.push({ resolve, reject, args })) : Promise.resolve({}),
      },
      './standard-model': { ...M, viewFromWire: () => view(), previewFromWire: () => ({ now: 0, tasks: [], parts: {} }) },
    }, react)('standard.tsx').StandardView;
    const page = pageRuntime.renderHook(() => Page());
    await settle();
    const header = () => nodes(page.result).find((n) => n.type?.name === 'Head');
    const head = headRuntime.renderHook(() => { const h = header(); return h.type(h.props); });
    const at = (id) => nodes(head.result).find((n) => n.props?.['data-testid'] === id);
    const type = (value) => at('st-shared-path').props.onChange({ target: { value } });
    const submit = () => nodes(head.result).find((n) => n.type === 'form').props.onSubmit({ preventDefault() {} });
    at('st-picker').props.onClick();
    type('/shared/first.yaml');
    submit();
    assert.equal(at('st-shared-path')?.props.value, '/shared/first.yaml', 'the pending request keeps the address visible');
    pending.shift().reject(new Error('Shared standard unavailable'));
    await settle();
    assert.equal(at('st-shared-path')?.props.value, '/shared/first.yaml', 'a refusal keeps the address for retry');
    submit();
    type('/shared/newer.yaml');
    pending.shift().resolve({});
    await settle();
    assert.equal(at('st-shared-path')?.props.value, '/shared/newer.yaml', 'an older success keeps newer input');
    submit();
    const accepted = pending.shift();
    assert.equal(JSON.parse(accepted.args[0]).use, '/shared/newer.yaml');
    accepted.resolve({});
    await settle();
    assert.equal(at('st-picker').props['aria-expanded'], false, 'success closes the picker');
    at('st-picker').props.onClick();
    assert.equal(at('st-shared-path').props.value, '', 'success clears the accepted address');
  } finally {
    pageRuntime.unmount(); headRuntime.unmount();
    globalThis.window = oldWindow; globalThis.document = oldDocument;
  }
});

if (failures) { console.error(`${failures} failed`); process.exit(1); }
console.log('all ok');
