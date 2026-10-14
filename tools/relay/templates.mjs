// The pinned question templates and the hard limits.
//
// This file is the server-side copy of Semanticus.Core's DecisionRegistry, and it is deliberately a COPY
// rather than an import: the engine's registry is what the shipped client believes it may send, and this is
// what the relay is willing to pay for. They are supposed to be compared, not shared. A point missing here
// cannot be forwarded whatever a client claims, and a field the template does not declare cannot leave the
// relay even if a client built a snapshot containing it.
//
// Keeping the two in step is a release step, not a runtime one: bump the schemaVersion here in the same
// change that bumps DecisionPoint.SchemaVersion in Semanticus.Core/Decisions/DecisionRegistry.cs, and
// deploy the relay before the client that needs it. A client ahead of the relay is refused with a plain
// message (schema_mismatch); a relay ahead of the client keeps serving the older version until it is
// removed here.

export const LIMITS = {
    /** The whole request body. The engine caps its snapshot at 64 KB (JevDecisionOracle.MaxSnapshotBytes).
     *  Kane, 13 Oct 2026: 30 questions per request. A question is a name plus a small schema, about 150 bytes
     *  (the largest declared shape is a choice with a few options), so 30 of them are under 5 KB; with the
     *  64 KB snapshot and the criteria and framing text that is about 75 KB. 192 KB leaves a 2.5x margin and
     *  is still a small body for a worker. */
    maxRequestBytes: 196_608,
    maxSnapshotBytes: 65_536,
    maxQuestions: 30,
    /** In-flight calls per licence: OFF (0). Kane, 13 Oct 2026: 1,000 Jev requests cost about 80 cents, so a
     *  limit on requests at once protects nothing and refused real work (a three-table model stopped with
     *  "too many advice requests"). The monthly token budget below is the only runaway guard. The ledger
     *  still honours a positive value, which is how a future limit would be turned back on. The engine's own
     *  gate (JevDecisionOracle.MaxConcurrent, lane jev-busy) is set on its side. */
    perSidConcurrency: 0,
    /** How long past the vendor deadline a hold may stay "running". The worker's own fetch is cut at
     *  upstreamTimeoutMs, so a hold older than that plus this grace belongs to a worker that died without
     *  settling it. It no longer blocks anything (not a concurrency limit when one is set, not a retry of
     *  its own key); it still holds its estimate for the budget until the sweep. */
    inFlightGraceMs: 10_000,
    /** Input tokens per licence per calendar month, on the server clock. Kane, 1 Oct 2026: USD 2 a month per user, which at Jev's
     *  USD 0.042 per million input tokens (output free) is 47,600,000 input tokens (USD 1.9992). */
    licenceMonthlyTokens: 47_600_000,
    /** How long a call that may have been charged waits before the sweep counts it as spent. */
    stuckAfterMs: 3_600_000,
    /** How long a finished ledger entry is kept so a late retry is still recognised as the same charge. */
    entryRetentionMs: 86_400_000,
    /** The largest answer the ledger will keep so a repeat of the same ask under the same reference can be
     *  served without buying it twice. A bigger answer is simply not kept, and the repeat pays again. */
    maxCachedReplyBytes: 32_768,
    /** The relay's own deadline on the vendor. Longer than the engine's four seconds on purpose: when the
     *  person has already given up, the relay still needs the real cost back to settle the reservation. */
    upstreamTimeoutMs: 15_000,
};

/**
 * Every point the relay will pay for, with what each one may send.
 *
 *   fields          the only snapshot keys that may leave the relay, in the registry's own order
 *   questions       the exact question names, for a point whose questions are fixed
 *   questionPrefix  for a point whose question NAMES are built from the model at ask time, the prefix
 *                   every one of them must carry. plan.rank asks one score per plan item, so the names are
 *                   item ids (PlanRankPoint.QuestionFor); compose.draft asks one include per step
 *                   template and one pick per listed argument, named by template (JevDraftBuilder.Q)
 *   maxQuestions    that point's own bound, when it differs from the general one
 *   batched         for a point whose fixed questions may also be asked ONE PER OBJECT OF A TABLE (m11/jev-batch): the
 *                   name is then "<question>:<object ref>" ("summarize_by:column:Sales/Amount"), each with something
 *                   after the colon, and maxQuestions is what one chunk of a table needs (the engine's ChunkSize times
 *                   the questions each object carries). The fixed names stay valid, so a per-object request still passes.
 *
 * `tools/relay/test/drift.test.mjs` reads DecisionRegistry.cs, PlanRankPoint.cs and AskRoutePoint.cs and
 * fails on any difference from the table below, so this copy cannot fall behind the engine in silence.
 */
export const TEMPLATES = {
    'measure.folder_and_format': {
        // 2 (F-229): the formula text became facts worked out from it on the person's computer.
        schemaVersion: 2,
        fields: ['measure_name', 'measure_formula_facts', 'references_measures', 'table_name', 'table_columns', 'sibling_measures', 'measures'],
        questions: ['display_folder', 'format_family'],
        // m11/jev-batch: the Plan asks a table's measures in one request, up to MeasuresFamily.ChunkSize (15) measures, a folder
        // and a format question for each. propose_measure_defaults still asks one measure (two questions).
        batched: true,
        maxQuestions: 30,
    },
    'plan.rank': {
        schemaVersion: 1,
        fields: ['model_shape', 'plan_items'],
        questions: [],
        questionPrefix: 'value:',
        // One question per plan item, all in a single call, and the engine takes at most this many
        // (PlanRankPoint.MaxScoredItems, and LocalEngine.Plan.cs takes exactly that from the open list).
        // The general bound of eight is a measure-point bound; holding plan.rank to it would 413 every
        // real plan, which is Astra's finding 13.
        maxQuestions: 40,
    },
    'ask.route': {
        schemaVersion: 1,
        fields: ['ask_text'],
        // model_intent is the second question, asked only when the words cannot say whether a person wants a new model made
        // (AskRoutePoint.IntentQuestion, added by 0ad1c1be). The relay copy had fallen behind the registry: found by the drift test
        // failing on the join branch, and fixed here because the relay is deployed with this release.
        questions: ['tool', 'model_intent'],
    },
    'measure.starter': {
        // T268: one number column per call, one question. The name, formula and format are the engine's.
        schemaVersion: 1,
        fields: ['table_name', 'column_name', 'column_facts', 'table_columns'],
        questions: ['aggregation'],
    },
    'compose.draft': {
        // Composer lane L5: Jev drafts steps from words. One noul per candidate step template
        // ("step:add-measure:include") and one choice per argument the engine lists from the model
        // ("step:refresh:partitionRef"), all in one call, so the point is prefixed like plan.rank and the
        // bound is JevDraftBuilder.MaxQuestions.
        schemaVersion: 1,
        fields: ['draft_words', 'table_names'],
        questions: [],
        questionPrefix: 'step:',
        maxQuestions: 64,
    },
    'query.summarize': {
        // Jev question-to-DAX (Kane, 26 Sep): the parts of a data question. The names carry the part they settle
        // ("q:measure:1", "q:splits", "q:filter:f3", "q:top"), one filter question per place the words matched a
        // value, so the point is prefixed like compose.draft and the bound is JevQueryReader.MaxQuestions. The split
        // redesign asks the split in a second call ("q:group:1" over the engine's shortlist, and "q:group_table:1") and,
        // only when the shortlist missed, a third ("q:group_in:1"); same point, same fields, value_matches empty.
        // value_matches carries a column name and the question's own words, never a value.
        // The check that comes last (29 Sep, "Jev data questions v2") is a fourth call: "q:check:gap", with the reading
        // told in the question's own words in stated_reading (no value, no date); when its answer is not clear, up to two more calls,
        // "q:check:real", one per gap, over the same fields.
        schemaVersion: 1,
        fields: ['question_words', 'model_fields', 'value_matches', 'stated_reading'],
        questions: [],
        questionPrefix: 'q:',
        maxQuestions: 24,
    },
    'queries.chart_kind': {
        // D6: Jev's chart pick for a Queries result, OFF until its replay clears docs/queries-chart-pick-bar.md.
        // The words and the result's shape (split names, date or not, distinct counts, number and row counts), never rows.
        schemaVersion: 1,
        fields: ['question_words', 'result_shape'],
        questions: ['chart_kind'],
    },
    'next.step': {
        // 1.5, the Next line: which of the rules' eligible next steps to show. Counts, yes-or-no facts and step ids only;
        // every word on the line is the engine's (Semanticus.Engine/NextStepRules.cs).
        schemaVersion: 1,
        fields: ['journey_stage', 'journey_counts', 'journey_flags', 'candidate_kinds', 'plan_top_kind'],
        questions: ['next_kind'],
    },
    'column.format': {
        // T268, columns first: one number or date column per call, names and types only. The format string is the engine's.
        schemaVersion: 1,
        fields: ['table_name', 'column_name', 'column_facts', 'table_columns', 'columns'],
        questions: ['format_family'],
        // m11/jev-batch: a table's columns in one request, up to ColumnFormatFamily.ChunkSize (30), one question each.
        batched: true,
        maxQuestions: 30,
    },
    'relationship.join': {
        // T268, M4 point 2: one column per call, the key columns it may join listed by the engine. The engine
        // builds the relationship, checks the keys and decides which relationships can be active.
        schemaVersion: 1,
        fields: ['table_name', 'column_name', 'column_facts', 'table_columns', 'candidate_keys'],
        questions: ['join_to', 'filter_direction', 'cardinality', 'date_role'],
    },
    'ai.schema': {
        // T268, what Copilot sees: one table, column or measure per call, names, types and structure facts only.
        // The write is the engine's own set_ai_data_schema, after its dependency check.
        schemaVersion: 1,
        fields: ['table_name', 'object_kind', 'object_name', 'object_facts', 'table_fields', 'objects'],
        questions: ['ai_schema'],
        // m11/jev-batch: a table's objects in one request, up to AiSchemaFamily.ChunkSize (30), one question each.
        batched: true,
        maxQuestions: 30,
    },
    'column.category': {
        // T268, the fair replay: one visible text or number column per call, names, types and derived shares only.
        schemaVersion: 1,
        fields: ['table_name', 'column_name', 'column_facts', 'table_columns'],
        questions: ['data_category'],
    },
    'column.summarize': {
        // T268, Kane's decision of 26 Sep: one number column per call, the same four fields as column.category.
        schemaVersion: 1,
        fields: ['table_name', 'column_name', 'column_facts', 'table_columns', 'model_tables', 'table_facts', 'columns'],
        questions: ['summarize_by'],
        // m11/jev-batch: a table's columns in one request, up to ColumnSummarizeFamily.ChunkSize (30), one question each.
        batched: true,
        maxQuestions: 30,
    },
    'cleanup.suggest': {
        // m11/plan-harm round 5: the Plan's cleanup row. One question per unused column that would start ticked, all in a
        // few calls, each named "cleanup:<column ref>" and each answered suggest or leave_out. The names, types and table
        // shapes only. Jev can only untick, and the engine writes every line and every reason (Plan/CleanupSuggest.cs).
        schemaVersion: 1,
        fields: ['cleanup_tables', 'cleanup_columns'],
        questions: [],
        questionPrefix: 'cleanup:',
        // The engine sends at most this many columns per call (CleanupSuggest.MaxPerCall).
        maxQuestions: 40,
    },
    'date.table': {
        // T268, M4 point 3: once per model with no marked date table. Table names, sides, column names and types, and
        // derived yes-or-no facts about each date column. The mark and the generated table are the engine's.
        schemaVersion: 1,
        fields: ['model_tables', 'model_facts'],
        questions: ['date_table'],
    },
};

/**
 * Check one decide request against the pinned template.
 * Returns null when it passes, or { status, error, reason } naming the first thing wrong.
 *
 * The order is not arbitrary. Cheap identity checks come before shape checks so a client that is simply on
 * the wrong version is told that, rather than being told its fields are wrong.
 */
export function checkTemplate(point, schemaVersion, snapshot, questions) {
    if (typeof point !== 'string' || !Object.prototype.hasOwnProperty.call(TEMPLATES, point)) {
        return { status: 404, error: 'unknown_point', reason: 'This kind of advice is not offered.' };
    }
    const template = TEMPLATES[point];

    // The engine may not send schemaVersion yet (the body contract is point, snapshot, questions,
    // idempotencyKey). When it is absent the pinned version is assumed and the log says so; when it is
    // present it must match, which is how a client that has moved ahead is caught.
    if (schemaVersion !== undefined && schemaVersion !== null && Number(schemaVersion) !== template.schemaVersion) {
        return {
            status: 409,
            error: 'schema_mismatch',
            reason: 'This version of Semanticus asks for something the service does not serve yet. Update Semanticus.',
        };
    }

    if (snapshot === null || typeof snapshot !== 'object' || Array.isArray(snapshot)) {
        return { status: 400, error: 'bad_request', reason: 'The request was not in the expected shape.' };
    }
    if (questions === null || typeof questions !== 'object' || Array.isArray(questions)) {
        return { status: 400, error: 'bad_request', reason: 'The request was not in the expected shape.' };
    }

    const questionNames = Object.keys(questions);
    if (questionNames.length === 0) {
        return { status: 400, error: 'bad_request', reason: 'The request asked nothing.' };
    }
    if (questionNames.length > (template.maxQuestions ?? LIMITS.maxQuestions)) {
        return { status: 413, error: 'too_many_questions', reason: 'That request asks too much at once.' };
    }

    for (const name of Object.keys(snapshot)) {
        if (!template.fields.includes(name)) {
            return {
                status: 400,
                error: 'undeclared_field',
                reason: 'The request tried to send something this kind of advice does not use.',
            };
        }
    }
    for (const name of questionNames) {
        // A prefixed point still has to name something after the prefix, so a bare "value:" is as much a
        // refusal as a question the point never declared.
        // A batched point's fixed questions may also carry an object after a colon ("summarize_by:column:Sales/Amount"); the
        // part before the colon must be one of the fixed names and something must follow it.
        const allowed = template.questionPrefix
            ? name.startsWith(template.questionPrefix) && name.length > template.questionPrefix.length
            : template.questions.includes(name)
              || (template.batched === true && template.questions.some((q) => name.startsWith(q + ':') && name.length > q.length + 1));
        if (!allowed) {
            return {
                status: 400,
                error: 'undeclared_question',
                reason: 'The request asked something this kind of advice does not answer.',
            };
        }
    }

    const snapshotBytes = new TextEncoder().encode(JSON.stringify(snapshot)).length;
    if (snapshotBytes > LIMITS.maxSnapshotBytes) {
        return { status: 413, error: 'too_large', reason: 'That model detail is too big to send.' };
    }

    return null;
}
