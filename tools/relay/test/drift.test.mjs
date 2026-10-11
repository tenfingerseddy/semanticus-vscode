// Drift guard: the relay's pinned templates against the engine's own declarations.
//
// templates.mjs is a deliberate COPY of Semanticus.Core/Decisions/DecisionRegistry.cs, not an import: the
// registry is what the shipped client believes it may send, and the template is what the relay is willing
// to pay for. A copy that nobody compares is just a second source of truth, and Astra's finding 13 is what
// that costs: two points shipped in the engine, both answered 404 unknown_point by the relay, and a
// question bound of eight against an engine that sends forty.
//
// So this file reads the C# and fails on the difference. It parses rather than importing a generated JSON
// export on purpose: an export is a file somebody has to remember to regenerate, and the thing being
// guarded here is exactly the class of mistake where somebody did not.
//
// The parser is small and strict. Every match it needs is asserted to exist, so a refactor that moves the
// declarations out of reach fails this suite loudly instead of quietly passing over nothing.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { TEMPLATES } from '../templates.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const read = (relative) => readFileSync(join(root, relative), 'utf8');

const REGISTRY_PATH = 'Semanticus.Core/Decisions/DecisionRegistry.cs';
const PLAN_RANK_PATH = 'Semanticus.Engine/Plan/PlanRankPoint.cs';
const ASK_ROUTE_PATH = 'Semanticus.Engine/Plan/AskRoutePoint.cs';
const DRAFT_BUILDER_PATH = 'Semanticus.Engine/Composer/JevDraftBuilder.cs';
const QUERY_READER_PATH = 'Semanticus.Engine/Query/JevQueryReader.cs';
const CLEANUP_PATH = 'Semanticus.Engine/Plan/CleanupSuggest.cs';

/** "public const string PlanRank = "plan.rank";" and friends, so a point's name is read the way the C#
 *  reads it rather than by trusting the literal to appear at the use site. */
function stringConstants(source) {
    const out = new Map();
    for (const m of source.matchAll(/public\s+const\s+string\s+(\w+)\s*=\s*"([^"]*)"\s*;/g)) out.set(m[1], m[2]);
    return out;
}

function parsePoints(source) {
    const constants = stringConstants(source);
    const points = [];

    for (const chunk of source.split('new DecisionPoint(').slice(1)) {
        const end = chunk.indexOf('proOnly:');
        assert.ok(end > 0, 'every DecisionPoint ends with proOnly; the parser needs that boundary');
        const block = chunk.slice(0, end);

        const nameRef = /name:\s*([A-Za-z_]\w*)/.exec(block);
        assert.ok(nameRef, 'a DecisionPoint with no name argument the parser can see');
        const name = constants.get(nameRef[1]);
        assert.ok(name, 'the point name constant ' + nameRef[1] + ' was not found in the registry');

        const schema = /schemaVersion:\s*(\d+)/.exec(block);
        assert.ok(schema, 'point ' + name + ' declares no schemaVersion the parser can see');

        const fieldsBlock = /snapshotFields:\s*new\[\]\s*\{([^}]*)\}/.exec(block);
        assert.ok(fieldsBlock, 'point ' + name + ' declares no snapshotFields the parser can see');
        const fields = [...fieldsBlock[1].matchAll(/"([^"]+)"/g)].map((m) => m[1]);
        assert.ok(fields.length > 0, 'point ' + name + ' parsed to an empty field list');

        const questionShapes = [...block.matchAll(/new DecisionQuestionShape\(\s*"([^"]+)"/g)].map((m) => m[1]);
        assert.ok(questionShapes.length > 0, 'point ' + name + ' parsed to an empty question list');

        points.push({ name, schemaVersion: Number(schema[1]), fields, questionShapes });
    }

    return points;
}

const registrySource = read(REGISTRY_PATH);
const POINTS = parsePoints(registrySource);
const byName = new Map(POINTS.map((p) => [p.name, p]));

// plan.rank is the one point whose wire question NAMES are not its declared shape name: it asks one score
// per plan item, so the names are item ids and the registry declares the shape once under a placeholder.
// The exemption is written down here, with the registry's own words quoted, so it cannot be widened by
// accident to a point that simply forgot to declare something.
const PER_ITEM_POINT = 'plan.rank';
const PER_ITEM_SHAPE = 'item_value';
// compose.draft is the second: one include per step template and one pick per listed argument, all in one call,
// named by template id.
const PER_STEP_POINT = 'compose.draft';
const PER_STEP_SHAPE = 'template_step';
// query.summarize is the third: one question per part of a data question, and one per place the words matched a value.
const PER_PART_POINT = 'query.summarize';
const PER_PART_SHAPE = 'query_part';
// cleanup.suggest is the third: one choice per unused column, named by the column's ref (m11/plan-harm round 5).
const PER_COLUMN_POINT = 'cleanup.suggest';
const PER_COLUMN_SHAPE = 'suggest_removal';

test('the parser really read the engine declarations', () => {
    assert.ok(registrySource.includes('DecisionRegistry'), 'the registry source was not read');
    assert.ok(POINTS.length >= 3, 'expected at least the three shipped points, parsed ' + POINTS.length);
});

test('the relay serves exactly the points the engine registers', () => {
    assert.deepEqual([...byName.keys()].sort(), Object.keys(TEMPLATES).sort());
});

test('each template declares exactly the snapshot fields the engine declares', () => {
    for (const point of POINTS) {
        assert.deepEqual(TEMPLATES[point.name].fields, point.fields, 'declared fields differ for ' + point.name);
    }
});

test('each template pins the schema version the engine sends', () => {
    for (const point of POINTS) {
        assert.equal(TEMPLATES[point.name].schemaVersion, point.schemaVersion, 'schema version differs for ' + point.name);
    }
});

test('the fixed-question points accept exactly the questions the engine asks', () => {
    const fixed = POINTS.filter((p) => p.name !== PER_ITEM_POINT && p.name !== PER_STEP_POINT && p.name !== PER_PART_POINT && p.name !== PER_COLUMN_POINT);
    assert.ok(fixed.length >= 2, 'expected more than one fixed-question point');
    for (const point of fixed) {
        assert.equal(TEMPLATES[point.name].questionPrefix, undefined,
            point.name + ' asks a fixed set of questions, so it must not carry a prefix');
        assert.deepEqual(TEMPLATES[point.name].questions, point.questionShapes,
            'declared questions differ for ' + point.name);
    }
});

test('plan.rank accepts the per-item score names the engine builds, and nothing else', () => {
    const point = byName.get(PER_ITEM_POINT);
    assert.deepEqual(point.questionShapes, [PER_ITEM_SHAPE],
        'plan.rank stopped declaring one placeholder shape; re-read the registry before trusting the prefix');
    assert.ok(registrySource.includes('the question NAMES are the item ids'),
        'the registry no longer says plan.rank question names are item ids');

    const prefix = /QuestionFor\(string itemId\)\s*=>\s*"([^"]+)"\s*\+\s*itemId/.exec(read(PLAN_RANK_PATH));
    assert.ok(prefix, 'PlanRankPoint.QuestionFor no longer builds a name the parser can read');
    assert.equal(TEMPLATES[PER_ITEM_POINT].questionPrefix, prefix[1]);
    assert.deepEqual(TEMPLATES[PER_ITEM_POINT].questions, []);
});

test('plan.rank takes as many questions as the engine will send in one call', () => {
    const max = /public const int MaxScoredItems\s*=\s*(\d+)\s*;/.exec(read(PLAN_RANK_PATH));
    assert.ok(max, 'PlanRankPoint.MaxScoredItems is no longer a plain constant');
    assert.equal(TEMPLATES[PER_ITEM_POINT].maxQuestions, Number(max[1]),
        'the relay question bound must match PlanRankPoint.MaxScoredItems exactly');
});

test('ask.route asks the two questions the engine names', () => {
    const tool = /public const string ToolQuestion\s*=\s*"([^"]+)"\s*;/.exec(read(ASK_ROUTE_PATH));
    assert.ok(tool, 'AskRoutePoint.ToolQuestion is no longer a plain constant');
    // The second, asked only when the words cannot say whether a person wants a new model made. The relay copy had lost it.
    const intent = /public const string IntentQuestion\s*=\s*"([^"]+)"\s*;/.exec(read(ASK_ROUTE_PATH));
    assert.ok(intent, 'AskRoutePoint.IntentQuestion is no longer a plain constant');
    assert.deepEqual(TEMPLATES['ask.route'].questions, [tool[1], intent[1]]);
});

test('compose.draft accepts the per-template names the engine builds, as many as it sends', () => {
    const point = byName.get(PER_STEP_POINT);
    assert.deepEqual(point.questionShapes, [PER_STEP_SHAPE],
        'compose.draft stopped declaring one placeholder shape; re-read the registry before trusting the prefix');
    assert.ok(registrySource.includes('the question NAMES carry the step template'),
        'the registry no longer says compose.draft question names carry the step template');

    const source = read(DRAFT_BUILDER_PATH);
    const prefix = /public const string Prefix\s*=\s*"([^"]+)"\s*;/.exec(source);
    assert.ok(prefix, 'JevDraftBuilder.Prefix is no longer a plain constant');
    assert.ok(/public static string Q\(string templateId, string what\)\s*=>\s*Prefix\s*\+/.test(source),
        'JevDraftBuilder.Q no longer starts every name with Prefix');
    assert.equal(TEMPLATES[PER_STEP_POINT].questionPrefix, prefix[1]);
    assert.deepEqual(TEMPLATES[PER_STEP_POINT].questions, []);

    const max = /public const int MaxQuestions\s*=\s*(\d+)\s*;/.exec(source);
    assert.ok(max, 'JevDraftBuilder.MaxQuestions is no longer a plain constant');
    assert.equal(TEMPLATES[PER_STEP_POINT].maxQuestions, Number(max[1]),
        'the relay question bound must match JevDraftBuilder.MaxQuestions exactly');
});

test('query.summarize accepts the per-part names the engine builds, as many as it sends', () => {
    const point = byName.get(PER_PART_POINT);
    assert.deepEqual(point.questionShapes, [PER_PART_SHAPE],
        'query.summarize stopped declaring one placeholder shape; re-read the registry before trusting the prefix');
    assert.ok(registrySource.includes('the question NAMES carry the part they settle'),
        'the registry no longer says query.summarize question names carry the part they settle');

    const source = read(QUERY_READER_PATH);
    const prefix = /public const string Prefix\s*=\s*"([^"]+)"\s*;/.exec(source);
    assert.ok(prefix, 'JevQueryReader.Prefix is no longer a plain constant');
    for (const builder of ['Fits', 'MeasureQ', 'GroupQ', 'SplitsQ', 'GroupTableQ', 'GroupInQ', 'FilterQ', 'TopQ', 'TopByQ', 'TopEndQ', 'SortQ']) {
        assert.ok(new RegExp('public (?:const string|static string) ' + builder + '\\b[^;]*=>?\\s*Prefix\\s*\\+').test(source)
            || new RegExp('public const string ' + builder + '\\s*=\\s*Prefix\\s*\\+').test(source),
            'JevQueryReader.' + builder + ' no longer starts its name with Prefix');
    }
    assert.equal(TEMPLATES[PER_PART_POINT].questionPrefix, prefix[1]);
    assert.deepEqual(TEMPLATES[PER_PART_POINT].questions, []);

    const max = /public const int MaxQuestions\s*=\s*(\d+)\s*;/.exec(source);
    assert.ok(max, 'JevQueryReader.MaxQuestions is no longer a plain constant');
    assert.equal(TEMPLATES[PER_PART_POINT].maxQuestions, Number(max[1]),
        'the relay question bound must match JevQueryReader.MaxQuestions exactly');
});

test('cleanup.suggest accepts the per-column names the engine builds, as many as it sends in one call', () => {
    const point = byName.get(PER_COLUMN_POINT);
    assert.deepEqual(point.questionShapes, [PER_COLUMN_SHAPE],
        'cleanup.suggest stopped declaring one placeholder shape; re-read the registry before trusting the prefix');
    assert.ok(registrySource.includes('the question NAMES carry the column'),
        'the registry no longer says cleanup.suggest question names carry the column');

    const source = read(CLEANUP_PATH);
    const prefix = /public const string Prefix\s*=\s*"([^"]+)"\s*;/.exec(source);
    assert.ok(prefix, 'CleanupSuggest.Prefix is no longer a plain constant');
    assert.ok(/public static string Q\(string columnRef\)\s*=>\s*Prefix\s*\+/.test(source),
        'CleanupSuggest.Q no longer starts every name with Prefix');
    assert.equal(TEMPLATES[PER_COLUMN_POINT].questionPrefix, prefix[1]);
    assert.deepEqual(TEMPLATES[PER_COLUMN_POINT].questions, []);

    const max = /public const int MaxPerCall\s*=\s*(\d+)\s*;/.exec(source);
    assert.ok(max, 'CleanupSuggest.MaxPerCall is no longer a plain constant');
    assert.equal(TEMPLATES[PER_COLUMN_POINT].maxQuestions, Number(max[1]),
        'the relay question bound must match CleanupSuggest.MaxPerCall exactly');
});

// m11/jev-batch: the four per-object families ask a table's objects in one request, one question per object, named
// "<question>:<object ref>". The relay may only take as many as the engine sends in one chunk, so the bound is read off the
// engine's own ChunkSize and question names rather than typed twice.
const BATCHED_FAMILIES = [
    { point: 'measure.folder_and_format', path: 'Semanticus.Engine/Plan/JevFamilies/MeasuresFamily.cs', perObject: 2 },
    { point: 'column.format', path: 'Semanticus.Engine/Plan/JevFamilies/ColumnFormatFamily.cs', perObject: 1 },
    { point: 'column.summarize', path: 'Semanticus.Engine/Plan/JevFamilies/ColumnSummarizeFamily.cs', perObject: 1 },
    { point: 'ai.schema', path: 'Semanticus.Engine/Plan/JevFamilies/AiSchemaFamily.cs', perObject: 1 },
];

test('the batched points take exactly the questions a chunk of a table sends, and no other point takes a batched name', () => {
    for (const family of BATCHED_FAMILIES) {
        const template = TEMPLATES[family.point];
        assert.equal(template.batched, true, family.point + ' must be marked batched');
        const chunk = /internal const int ChunkSize\s*=\s*(\d+)\s*;/.exec(read(family.path));
        assert.ok(chunk, family.path + ' no longer declares ChunkSize as a plain constant');
        assert.equal(template.maxQuestions, Number(chunk[1]) * family.perObject,
            family.point + ': the relay question bound must be the engine chunk size times the questions each object carries');
        assert.equal(template.questions.length, family.perObject, family.point + ' declares a different number of fixed questions');
    }
    const batched = new Set(BATCHED_FAMILIES.map((f) => f.point));
    for (const name of Object.keys(TEMPLATES)) {
        if (!batched.has(name)) assert.notEqual(TEMPLATES[name].batched, true, name + ' is not batched by the engine');
    }
});

test('each batched family builds its question names as "<question>:<object ref>"', () => {
    for (const family of BATCHED_FAMILIES) {
        const source = read(family.path);
        assert.ok(/JevTableBatch\.QuestionName\(/.test(source), family.path + ' no longer names its questions through JevTableBatch.QuestionName');
    }
    const helper = read('Semanticus.Engine/Plan/JevFamilies/JevTableBatch.cs');
    assert.ok(/public static string QuestionName\(string plain, string objectRef\) => plain \+ ":" \+ objectRef;/.test(helper),
        'JevTableBatch.QuestionName no longer builds "<question>:<ref>"; the relay accepts exactly that');
});
