// The Learn tour (1.4, lane tour; contract section 9).
//
// What is executed here, one line each:
//   every chapter script has the contract's shape, 4 to 8 steps, plain words, and every place and press id exists in the place map and the press map
//   a step's press sits on the step's own place, so the cursor can find the control it shows
//   the recorded Ask answers have rows that match their columns, a chart the Ask page draws, and numbers that agree with each other
//   the player plays each step as a "tour" note, waits the read time, a set time or the person's press, and ends with the right closing line
//   taking the wheel stops the tour, and Continue gives it back
//   the scratch copy: its path, the age rule that deletes old copies (and only copies this feature made), the copy itself, a second start in the same second
//   the demo model's TMDL is tab-indented and carries the five tables and six measures the tour names
//   the Studio side is wired: Help's Learn list, the Ask example block with its tag and line, the host commands and their relay
//   no em dash and no "one click" anywhere in the tour's words
import assert from 'node:assert/strict';
import { readFileSync, existsSync, readdirSync, mkdtempSync, mkdirSync, writeFileSync, utimesSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const studio = join(root, 'webview', 'src', 'studio');
const learnDir = join(root, 'assets', 'learn');
const demoDir = join(learnDir, 'contoso-shop');
const req = createRequire(join(root, 'webview', 'package.json'));
const ts = req('typescript');

const cache = new Map();
function load(file) {
  const full = /\.(ts|tsx)$/.test(file) ? file : existsSync(file + '.ts') ? file + '.ts' : file + '.tsx';
  if (cache.has(full)) return cache.get(full);
  const js = ts.transpileModule(readFileSync(full, 'utf8'), {
    compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, esModuleInterop: true }, fileName: full,
  }).outputText;
  const module = { exports: {} };
  cache.set(full, module.exports);
  const localRequire = (spec) => (spec.startsWith('.') ? load(resolve(dirname(full), spec)) : req(spec));
  new Function('exports', 'require', 'module', js)(module.exports, localRequire, module);
  return module.exports;
}
const readJson = (file) => JSON.parse(readFileSync(file, 'utf8'));
const M = load(join(studio, 'tour-model.ts'));
const L = load(join(root, 'src', 'learn.ts'));
const notes = load(join(studio, 'notes-store.ts'));

const places = readJson(join(studio, 'places.json')).places;
// The press map belongs to lane cursor. Until it has joined, the press ids are not checked (the join runs this test again).
const presses = existsSync(join(studio, 'presses.json')) ? readJson(join(studio, 'presses.json')).presses : null;
const index = readJson(join(learnDir, 'index.json'));
const CHAPTER_IDS = ['plan', 'ask', 'edit', 'review', 'publish'];
const answers = readJson(join(demoDir, 'answers.json'));

const BANNED = [/—/, /–/, /one[- ]click/i, /\b(MCP|XMLA|TOM|engine)\b/, /\p{Extended_Pictographic}/u];
function cleanWords(label, text) {
  for (const rule of BANNED) assert.ok(!rule.test(text), `${label} breaks the copy rules (${rule}): ${text}`);
}

// ---- the scripts ----------------------------------------------------------------------------------------------------
{
  assert.equal(index.version, 1);
  assert.deepEqual(index.chapters.map((c) => c.id), CHAPTER_IDS, 'the five chapters, in order');
  for (const entry of index.chapters) {
    assert.ok(entry.title && entry.line && Number.isInteger(entry.minutes), `index entry ${entry.id}`);
    cleanWords(`index ${entry.id}`, `${entry.title} ${entry.line}`);
  }
  assert.deepEqual(readdirSync(learnDir).filter((f) => f.endsWith('.json')).sort(), [...CHAPTER_IDS.map((id) => `${id}.json`), 'index.json'].sort(), 'one file per chapter, no stray scripts');

  const placeIds = new Set(places.map((p) => p.id));
  const pressById = presses ? new Map(presses.map((p) => [p.id, p])) : null;
  for (const id of CHAPTER_IDS) {
    const chapter = readJson(join(learnDir, `${id}.json`));
    assert.equal(chapter.version, 1, `${id}: version`);
    assert.equal(chapter.id, id);
    assert.ok(chapter.title.length >= 3 && chapter.title.length <= 30, `${id}: title`);
    assert.ok(Number.isInteger(chapter.minutes) && chapter.minutes >= 1 && chapter.minutes <= 5);
    assert.match(chapter.tryWithClaude, /^Ask Claude: .+\?$|^Ask Claude: .+\.$/, `${id}: the closing line starts "Ask Claude:"`);
    cleanWords(`${id} tryWithClaude`, chapter.tryWithClaude);
    assert.ok(chapter.steps.length >= 4 && chapter.steps.length <= 8, `${id}: 4 to 8 steps, has ${chapter.steps.length}`);
    chapter.steps.forEach((step, n) => {
      const where = `${id} step ${n + 1}`;
      assert.ok(placeIds.has(step.place), `${where}: place "${step.place}" is not in places.json`);
      assert.ok(typeof step.say === 'string' && step.say.trim().length >= 10 && step.say.length <= M.SAY_MAX, `${where}: say is 10 to ${M.SAY_MAX} characters`);
      cleanWords(where, step.say);
      if (step.how) { assert.match(step.how, /^Open /, `${where}: how starts "Open"`); assert.ok(step.how.length <= 120); cleanWords(`${where} how`, step.how); }
      if (step.focus) assert.match(step.focus, /^[a-z]+:\S/, `${where}: focus is an object ref`);
      if (step.press) {
        if (pressById) {
          const press = pressById.get(step.press);
          assert.ok(press, `${where}: press "${step.press}" is not in presses.json`);
          assert.equal(press.place, step.place, `${where}: the press sits on ${press.place}, so the step must be there too`);
        }
      }
      const wait = step.wait;
      assert.ok(wait === 'read' || (wait && (Number.isInteger(wait.ms) && wait.ms > 0 && wait.ms <= 15000 || wait.press === true)), `${where}: wait`);
      if (wait && wait.press) assert.ok(step.press, `${where}: a press wait needs a press`);
      if (step.example !== undefined) assert.ok(Number.isInteger(step.example) && step.example >= 0 && step.example < answers.answers.length, `${where}: example index`);
      assert.ok(!step.do, `${where}: this release never performs a press`);
    });
  }
  assert.ok(readJson(join(learnDir, 'ask.json')).steps.some((s) => s.example !== undefined), 'the Ask chapter shows an example answer');
}

// ---- the recorded answers -------------------------------------------------------------------------------------------
{
  assert.equal(answers.version, 1);
  assert.equal(answers.answers.length, 4, 'four recorded answers');
  const CHARTS = ['bar', 'line', 'area'];
  const totals = {};
  for (const a of answers.answers) {
    assert.ok(a.question.endsWith('?') && a.question.length <= 120, a.question);
    assert.ok(a.columns.some((c) => c.name === a.measure), `${a.question}: the measure is one of the columns`);
    assert.ok(a.rows.length >= 3, 'rows');
    for (const row of a.rows) {
      assert.equal(row.length, a.columns.length, `${a.question}: a row has one value for each column`);
      row.slice(1).forEach((v) => assert.equal(typeof v, 'number'));
    }
    assert.match(a.dax, /^EVALUATE/, 'the DAX is a query');
    for (const c of a.columns.slice(1)) assert.ok(a.dax.includes(`[${c.name}]`) || a.dax.includes(`"${c.name}"`), `${a.question}: the DAX names ${c.name}`);
    assert.ok(CHARTS.includes(a.chart), `${a.question}: chart ${a.chart}`);
    cleanWords(a.question, a.question);
    a.columns.slice(1).forEach((c, k) => { (totals[c.name] ??= []).push(a.rows.reduce((sum, row) => sum + row[k + 1], 0)); });
  }
  // The same year of the same shop: every Net Sales answer adds up to the one total, and every month of orders to the one order count.
  assert.deepEqual([...new Set(totals['Net Sales'])], [12_000_000], 'every Net Sales answer totals 12,000,000');
  assert.deepEqual(totals.Orders, [48_000], 'the monthly orders total 48,000');
  const quarters = answers.answers[1];
  const weighted = quarters.rows.reduce((sum, r) => sum + r[1] * r[2], 0) / 12_000_000;
  assert.ok(weighted > 0.38 && weighted < 0.41, `the year's margin ${weighted} sits between the quarters' margins`);

  // The answer as the Ask page reads it.
  for (const a of answers.answers) {
    const shown = M.answerOf(a);
    assert.equal(shown.answer.outcome, 'answered');
    assert.equal(shown.answer.rowCount, a.rows.length);
    assert.equal(shown.answer.columns.length, a.columns.length);
    assert.equal(shown.answer.formats.length, a.columns.length);
    assert.deepEqual(shown.parts.groupBy, [a.columns[0].name]);
    assert.ok(shown.parts.measures.includes(a.measure));
    assert.equal(shown.viz, a.chart);
  }
}

// ---- the player -----------------------------------------------------------------------------------------------------
function rig({ canMove = true } = {}) {
  let t = 0; let next = 1;
  const timers = new Map();
  const pushed = [];
  const state = { canMove };
  const player = M.createTourPlayer({
    setTimer: (fn, ms) => { const id = next++; timers.set(id, { fn, at: t + ms }); return id; },
    clearTimer: (id) => { timers.delete(id); },
    push: (note) => pushed.push(note),
    canMove: () => state.canMove,
    places,
    now: () => new Date(Date.UTC(2026, 9, 10, 9, 0, 0)),
  });
  const advance = (ms) => {
    const end = t + ms;
    for (;;) {
      const due = [...timers.entries()].filter(([, v]) => v.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
      if (!due) break;
      timers.delete(due[0]); t = due[1].at; due[1].fn();
    }
    t = end;
  };
  return { player, pushed, advance, state, timers };
}
const chapterOf = (id) => readJson(join(learnDir, `${id}.json`));

{
  const r = rig();
  const chapter = chapterOf('plan');
  r.player.start(chapter, 'sess-1');
  assert.equal(r.pushed.length, 1, 'the first step plays at once');
  const first = r.pushed[0];
  assert.equal(first.id, 'tour-plan-1');
  assert.equal(first.by, 'tour');
  assert.equal(first.sessionId, 'sess-1');
  assert.equal(first.place, 'plan');
  assert.equal(first.how, places.find((p) => p.id === 'plan').how, 'how comes from the place map when the script says null');
  assert.equal(first.at, '2026-10-10T09:00:00.000Z');
  // "read": the next step waits the note's read time, not a tick less.
  const read = notes.readTimeMs(first.say);
  r.advance(read - 1); assert.equal(r.pushed.length, 1);
  r.advance(1); assert.equal(r.pushed.length, 2, 'the second step plays after the read time');
  // a press step with a set time waits that time, which is never shorter than the read time.
  const pressStep = chapter.steps[2];
  assert.equal(r.pushed[2] ?? null, null);
  r.advance(notes.readTimeMs(chapter.steps[1].say));
  assert.equal(r.pushed.length, 3);
  assert.equal(r.pushed[2].press, 'run-plan');
  assert.equal(M.waitMs(pressStep), Math.max(pressStep.wait.ms, notes.readTimeMs(pressStep.say)));
  // run to the end.
  r.advance(60_000);
  assert.equal(r.pushed.length, chapter.steps.length, 'every step played once');
  assert.deepEqual(r.pushed.map((n) => n.id), chapter.steps.map((_, i) => `tour-plan-${i + 1}`));
  const done = r.player.getSnapshot();
  assert.equal(done.status, 'done');
  assert.equal(r.timers.size, 0, 'no timer is left running');
}

{
  // The Ask chapter: a step that names an example sets it, and a later step without one keeps it.
  const r = rig();
  const chapter = chapterOf('ask');
  r.player.start(chapter, 's');
  assert.equal(r.player.getSnapshot().example, null);
  r.advance(notes.readTimeMs(chapter.steps[0].say));
  assert.equal(r.player.getSnapshot().example, 0);
  r.advance(60_000);
  assert.equal(r.player.getSnapshot().example, 2);
  r.player.stop();
  assert.equal(r.player.getSnapshot().example, null, 'leaving clears the example');
  assert.equal(r.player.getSnapshot().status, 'idle');
}

{
  // A press wait ends on the person's press and not before; a press at any other moment changes nothing.
  const r = rig();
  const chapter = { version: 1, id: 'ask', title: 'T', minutes: 1, tryWithClaude: 'Ask Claude: hi?', steps: [
    { place: 'explore/ask', press: 'ask', say: 'Press Ask to see the answer.', wait: { press: true } },
    { place: 'explore/ask', say: 'That is the answer.', wait: 'read' },
  ] };
  r.player.start(chapter, 's');
  assert.equal(r.player.getSnapshot().waitingForPress, true);
  r.advance(120_000);
  assert.equal(r.pushed.length, 1, 'it waits for the person, however long');
  r.player.pressed();
  assert.equal(r.pushed.length, 2);
  assert.equal(r.player.getSnapshot().waitingForPress, false);
  r.player.pressed();
  assert.equal(r.pushed.length, 2, 'a press with nothing to press does nothing');
}

{
  // The person takes the wheel: the tour stops where it is, and Continue plays the NEXT step (the shown one is not shown again).
  const r = rig();
  const chapter = chapterOf('review');
  r.player.start(chapter, 's');
  r.advance(500);
  r.state.canMove = false;
  r.player.interrupt();
  assert.equal(r.player.getSnapshot().status, 'paused');
  r.advance(120_000);
  assert.equal(r.pushed.length, 1, 'nothing plays while the person has the wheel');
  assert.equal(r.timers.size, 0);
  r.player.resume();
  assert.equal(r.pushed.length, 1, 'still paused: the driver has not given the wheel back');
  r.state.canMove = true;
  r.player.resume();
  assert.equal(r.pushed.length, 2);
  assert.equal(r.player.getSnapshot().status, 'playing');

  // A tour that starts while the person has the wheel waits at step 1, then plays it (it was never shown).
  const w = rig({ canMove: false });
  w.player.start(chapter, 's');
  assert.equal(w.player.getSnapshot().status, 'paused');
  assert.equal(w.pushed.length, 0);
  w.state.canMove = true;
  w.player.resume();
  assert.equal(w.pushed.length, 1);
  assert.equal(w.pushed[0].id, 'tour-review-1');

  // Next skips the wait; on the last step it ends the chapter.
  const n = rig();
  n.player.start(chapter, 's');
  for (let i = 1; i < chapter.steps.length; i++) n.player.next();
  assert.equal(n.pushed.length, chapter.steps.length);
  n.player.next();
  assert.equal(n.player.getSnapshot().status, 'done');
}

{
  // The closing line.
  const chapter = chapterOf('plan');
  assert.equal(M.endLine(chapter, true), chapter.tryWithClaude);
  assert.equal(M.endLine(chapter, false), 'Connect your assistant to try this live.');
  // The read time is the notes store's own.
  assert.equal(M.waitMs({ say: 'x'.repeat(200), wait: 'read' }), 8000);
  assert.equal(M.waitMs({ say: 'short one here', wait: { ms: 100 } }), notes.readTimeMs('short one here'));
  assert.equal(M.waitMs({ say: 'short one here', wait: { press: true } }), null);
}

// ---- the scratch copy -----------------------------------------------------------------------------------------------
{
  const now = new Date(Date.UTC(2026, 9, 10, 3, 4, 5));
  assert.equal(L.scratchName('plan', now), 'plan-20261010-030405');
  assert.equal(L.scratchPath('/store', 'ask', now), join('/store', 'learn', 'ask-20261010-030405'), 'globalStorage/learn/<chapter>-<yyyyMMdd-HHmmss>');
  assert.ok(L.isLearnChapter('edit'));
  for (const bad of ['', 'Plan', '../plan', 'plan/x', 'contoso-shop', null, 3]) assert.ok(!L.isLearnChapter(bad), `${bad} is not a chapter`);

  const DAY = 24 * 60 * 60 * 1000;
  const nowMs = Date.UTC(2026, 9, 10);
  assert.deepEqual(L.staleCopies([
    { name: 'plan-20261001-000000', mtimeMs: nowMs - 8 * DAY },
    { name: 'ask-20261005-000000', mtimeMs: nowMs - 6 * DAY },
    { name: 'edit-20261001-000000-2', mtimeMs: nowMs - 9 * DAY },
    { name: 'my-notes', mtimeMs: nowMs - 30 * DAY },
    { name: 'plan-2026', mtimeMs: nowMs - 30 * DAY },
    { name: '..', mtimeMs: 0 },
  ], nowMs), ['plan-20261001-000000', 'edit-20261001-000000-2'], 'only copies this feature made, only when older than seven days');

  const store = mkdtempSync(join(tmpdir(), 'lrn-'));
  try {
    const learn = join(store, 'learn');
    mkdirSync(join(learn, 'plan-20260101-000000'), { recursive: true });
    mkdirSync(join(learn, 'ask-20261008-000000'), { recursive: true });
    mkdirSync(join(learn, 'my-notes'), { recursive: true });
    writeFileSync(join(learn, 'my-notes', 'keep.txt'), 'x');
    const old = new Date(Date.UTC(2026, 9, 1)); const recent = new Date(Date.UTC(2026, 9, 8));
    utimesSync(join(learn, 'plan-20260101-000000'), old, old);
    utimesSync(join(learn, 'ask-20261008-000000'), recent, recent);
    utimesSync(join(learn, 'my-notes'), old, old);
    const when = new Date(Date.UTC(2026, 9, 10, 12, 0, 0));
    const copy = L.makeScratchCopy(join(root, 'assets'), store, 'plan', when);
    assert.equal(copy, join(learn, 'plan-20261010-120000'));
    assert.ok(existsSync(join(copy, 'database.tmdl')) && existsSync(join(copy, 'tables', 'Sales.tmdl')), 'the demo model was copied whole');
    assert.ok(!existsSync(join(learn, 'plan-20260101-000000')), 'a copy older than seven days went');
    assert.ok(existsSync(join(learn, 'ask-20261008-000000')), 'a newer copy stayed');
    assert.ok(existsSync(join(learn, 'my-notes', 'keep.txt')), 'a folder this feature did not make was never touched');
    const again = L.makeScratchCopy(join(root, 'assets'), store, 'plan', when);
    assert.equal(again, join(learn, 'plan-20261010-120000-2'), 'a second start in the same second gets its own folder');
    assert.ok(existsSync(join(copy, 'database.tmdl')), 'the first copy is intact');
    assert.throws(() => L.makeScratchCopy(join(store, 'nowhere'), store, 'plan', when), /demo model is missing/);
  } finally { rmSync(store, { recursive: true, force: true }); }
}

// ---- the demo model's files -----------------------------------------------------------------------------------------
{
  const tmdl = [];
  const walk = (dir) => { for (const e of readdirSync(dir, { withFileTypes: true })) { const p = join(dir, e.name); if (e.isDirectory()) walk(p); else tmdl.push(p); } };
  walk(demoDir);
  const tmdlFiles = tmdl.filter((f) => f.endsWith('.tmdl'));
  assert.deepEqual(tmdlFiles.map((f) => f.slice(demoDir.length + 1).replaceAll('\\', '/')).sort(),
    ['database.tmdl', 'model.tmdl', 'relationships.tmdl', 'tables/Customer.tmdl', 'tables/Date.tmdl', 'tables/Product.tmdl', 'tables/Sales.tmdl', 'tables/Store.tmdl']);
  for (const file of tmdlFiles) {
    const text = readFileSync(file, 'utf8');
    assert.ok(!/^ +\S/m.test(text), `${file}: TMDL is indented with tabs`);
    assert.ok(!text.includes('\r'), `${file}: LF line ends`);
  }
  const sales = readFileSync(join(demoDir, 'tables', 'Sales.tmdl'), 'utf8');
  assert.equal([...sales.matchAll(/^\tmeasure /gm)].length, 6, 'six measures');
  // The gaps are on purpose: the Plan has rows. The C# test (LearnDemoModelTests) proves the engine lists them.
  assert.ok(!readFileSync(join(demoDir, 'tables', 'Date.tmdl'), 'utf8').includes('dataCategory'), 'the date table is not marked');
  assert.ok(/summarizeBy: sum/.test(sales), 'a column totals by itself (the implicit measure)');
  const customer = readFileSync(join(demoDir, 'tables', 'Customer.tmdl'), 'utf8');
  assert.ok(/column 'Customer Key'\n\t\tisKey\n\t\tdataType/.test(customer), 'a dimension key is marked as a key and left visible');
  cleanWords('the demo model', tmdlFiles.map((f) => readFileSync(f, 'utf8')).join('\n').replace(/\bengine\b/g, ''));
}

// ---- the Studio and host wiring -------------------------------------------------------------------------------------
{
  const help = readFileSync(join(root, 'webview', 'src', 'help.tsx'), 'utf8');
  assert.match(help, /import \{ learnChapters, startTour \} from '\.\/studio\/learn-state'/, 'Help reads the lean state file, not the whole tour');
  assert.match(help, /section: i === 0 \? 'Learn Semanticus' : undefined/, 'Help lists the chapters under one heading');
  assert.match(help, /run: \(\) => startTour\(c\.id\)/);

  const ask = readFileSync(join(root, 'webview', 'src', 'askdata.tsx'), 'utf8');
  assert.match(ask, /data-testid="askdata-example">Example</, 'the Example tag');
  assert.ok(ask.includes('Example answer. Live answers need your own model.'), 'the line under the tag');
  assert.equal((ask.match(/<ExampleAnswers /g) || []).length, 1, 'one block on the Ask page');
  assert.ok(!/\brpc\b|\bsend\(/.test(ask.slice(ask.indexOf('function ExampleAnswers'), ask.indexOf('export function OtherReadings'))), 'the example never calls the engine');

  const tour = readFileSync(join(studio, 'tour.tsx'), 'utf8');
  assert.match(tour, /export \{ startTour \} from '\.\/learn-state'/, 'firstrun calls startTour(chapterId)');
  const lean = readFileSync(join(studio, 'learn-state.ts'), 'utf8');
  assert.match(lean, /export function startTour\(chapterId: string\)/);
  assert.ok(!/from '\.\.?\/(bridge|notes-runtime|driver-store)'|\.json'|\.css'/.test(lean), 'learn-state.ts stays light: no bridge, no scripts, no stylesheet');
  assert.match(readFileSync(join(root, 'webview', 'src', 'main.tsx'), 'utf8'), /import '\.\/studio\/tour';/, 'the tour loads with the Studio');
  assert.match(tour, /runHostCommand\('semanticus\.learn\.start', chapterId\)/);
  assert.match(tour, /runHostCommand\('semanticus\.learn\.end'\)/);
  assert.ok(!/\brpc\b/.test(tour), 'the tour calls no engine operation');

  const ext = readFileSync(join(root, 'src', 'extension.ts'), 'utf8');
  assert.match(ext, /registerPersonCommand\('semanticus\.learn\.start'/);
  assert.match(ext, /registerPersonCommand\('semanticus\.learn\.end'/);
  assert.match(ext, /command === 'semanticus\.learn\.start' \|\| command === 'semanticus\.learn\.end'/, 'the relay runs the two commands');
  assert.ok(!/WEBVIEW_HOST_COMMANDS = new Set\([^\]]*learn/.test(ext), 'the pinned allowlist is unchanged');
  const host = readFileSync(join(root, 'src', 'learnHost.ts'), 'utf8');
  assert.ok(host.includes('learnReady') && host.includes('learnFailed') && host.includes('learnCancelled') && host.includes('learnEnded'));
  assert.ok(host.includes('Your own model is unchanged.'));
  const pkg = readFileSync(join(root, '.vscodeignore'), 'utf8');
  assert.ok(!/^assets/m.test(pkg), 'the demo model ships in the package');

  // The tour's own copy, as the Studio shows it.
  cleanWords('tour.tsx', tour.replace(/\/\/.*$/gm, ''));
}

console.log('learn tour: scripts, answers, player, scratch copy, demo files and wiring all hold');
