// The registry: every place in the product that is allowed to ask a decision service anything.
//
// This list IS the privacy disclosure and the audit surface. A point that is not here cannot be asked (the
// service refuses an unregistered name), and a field that a point has not declared cannot be sent (the
// snapshot builder refuses it). Both refusals are hard, not warnings, because the alternative is a feature
// that quietly grows what it uploads.
//
// Two versions live here and they mean different things:
//   DecisionPoint.SchemaVersion   what THIS point sends. Part of its request digest, so bumping it retires
//                                 every cached and recorded answer for that point, which is correct.
//   ConsentSchemaVersion          what the WHOLE product may send. Consent is granted against this number,
//                                 so adding a field anywhere means the person is asked again.
// DeclaredFieldFingerprint exists so the second of those cannot be forgotten: a test pins it, and adding or
// renaming any declared field on any point fails that test until the consent version is bumped too.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Semanticus.Decisions
{
    /// <summary>What one question looks like before the model fills in its options. A point declares the
    /// shape; the caller builds the real options from the open model at ask time.</summary>
    public sealed class DecisionQuestionShape
    {
        public DecisionQuestionShape(string name, DecisionQuestionKind kind, string optionSource, IEnumerable<string> fixedOptions = null)
        {
            Name = name;
            Kind = kind;
            OptionSource = optionSource;
            FixedOptions = fixedOptions == null ? null : new List<string>(fixedOptions).AsReadOnly();
        }

        public string Name { get; }
        public DecisionQuestionKind Kind { get; }

        /// <summary>Plain words for where the options come from, shown in the settings list. Either a fixed set
        /// or something read from the open model.</summary>
        public string OptionSource { get; }

        /// <summary>The option ids when they are fixed, null when they come from the model.</summary>
        public IReadOnlyList<string> FixedOptions { get; }
    }

    /// <summary>One registered decision point.</summary>
    public sealed class DecisionPoint
    {
        public DecisionPoint(string name, string caller, string purpose, int schemaVersion,
                             IEnumerable<string> snapshotFields, IEnumerable<DecisionQuestionShape> questions,
                             double confidenceBar, string lowConfidenceBehaviour, bool proOnly)
        {
            Name = name;
            Caller = caller;
            Purpose = purpose;
            SchemaVersion = schemaVersion;
            SnapshotFields = new List<string>(snapshotFields).AsReadOnly();
            Questions = new List<DecisionQuestionShape>(questions).AsReadOnly();
            ConfidenceBar = confidenceBar;
            LowConfidenceBehaviour = lowConfidenceBehaviour;
            ProOnly = proOnly;
        }

        /// <summary>The registered name, for example "measure.folder_and_format". Stable: it is in the request
        /// digest, in the settings file and in the replay records.</summary>
        public string Name { get; }

        /// <summary>The operation that asks. One point, one caller.</summary>
        public string Caller { get; }

        /// <summary>What this point is for, in the product's own words. This is the sentence the settings list
        /// shows, so it says what leaves the machine and why.</summary>
        public string Purpose { get; }

        public int SchemaVersion { get; }

        /// <summary>The only snapshot fields this point may send. The snapshot builder refuses anything else.</summary>
        public IReadOnlyList<string> SnapshotFields { get; }

        public IReadOnlyList<DecisionQuestionShape> Questions { get; }

        /// <summary>How sure an answer must be before the product says anything new.</summary>
        public double ConfidenceBar { get; }

        /// <summary>What happens below the bar, in plain words.</summary>
        public string LowConfidenceBehaviour { get; }

        /// <summary>Pro only. A free session skips the point and says so; it is never a refusal of the
        /// operation itself.</summary>
        public bool ProOnly { get; }
    }

    public static class DecisionRegistry
    {
        /// <summary>The proof point for M0a: a folder and a format family proposed for a new measure that was
        /// created without either. It proposes; it never writes.</summary>
        public const string MeasureFolderAndFormat = "measure.folder_and_format";

        /// <summary>The six format families. Fixed, ordered for display, and deliberately coarse: this point
        /// proposes a FAMILY, and turning a family into a real format string stays deterministic.</summary>
        public static readonly IReadOnlyList<string> FormatFamilies = new List<string>
        {
            "currency", "percent", "whole_number", "decimal", "date_or_time", "text_or_none",
        }.AsReadOnly();

        /// <summary>The option id that means "leave it where it is". A RESERVED id: no model-derived option id
        /// can equal it, because those are encoded with a prefix (MeasureDefaultsPoint.FolderIdPrefix) that a
        /// reserved id never carries. A folder in the model literally named "none" used to produce this id a
        /// second time and the duplicate threw.</summary>
        public const string NoFolder = "none";

        /// <summary>M2: the order of the living plan. It scores each candidate gap on one value ladder, in one
        /// call, and the plan is presented in score order when the scores clear the bar. It proposes an ORDER
        /// and nothing else: the items, their reasons and their exact changes are all computed by the rules
        /// before this point is asked, and they are identical whether it is asked or not.</summary>
        public const string PlanRank = "plan.rank";

        /// <summary>M2: the box. One ask in a person's own words, one of the twenty places in the product, or
        /// none. It navigates; it never acts.</summary>
        public const string AskRoute = "ask.route";

        /// <summary>M4 (T268): the starter measures family. For a number column on a fact table that no measure
        /// uses yet, whether it gets a base measure and which aggregation. It picks one option id; the engine builds
        /// the name, the formula, the format and the reason (Plan/JevFamilies/StarterMeasureTemplates.cs).</summary>
        public const string MeasureStarter = "measure.starter";

        /// <summary>The seven starter options, in the order a person reads them. Fixed: the ids are
        /// StarterMeasureTemplates' own, and the order is part of the request digest.</summary>
        public static readonly IReadOnlyList<string> StarterAggregations = new List<string>
        {
            "none", "sum", "average", "min", "max", "count", "distinctcount",
        }.AsReadOnly();

        /// <summary>Composer lane L5 (composer contract section 8): Jev picks the steps of a task or a workflow from a
        /// person's words, on each door whose replay cleared its bar (JevDraftBuilder.JevDraftsOnWorkflows and
        /// JevDraftsOnPlan, both on since 26 Sep). It is asked once per step the words name. The engine lists candidate
        /// step templates from the executable allowlist; for each, Jev answers whether the words ask for it and, for each
        /// argument the engine can list from the model, which option. The engine orders the steps, keeps the word match
        /// where Jev is not sure, and turns unsure picks into questions. It chooses; it never writes. Asked by
        /// compose_task and Add more words (the Plan field, Ctrl J) and describe_workflow_steps (Workflows).</summary>
        public const string ComposeDraft = "compose.draft";

        /// <summary>M4 (T268, columns first): the column format family. For a visible number or date column with no
        /// format yet, which of the six format families it shows as. The one column setting the replay shipped
        /// (docs/redesign/jev-columns-replay.md: names only 98%, the stronger rule 81%), and the replay's best arm
        /// sent names and types only, so this point sends nothing else. The engine turns the family into a real
        /// format string from the format library, or the money format the model already uses
        /// (Plan/JevFamilies/ColumnFormatFamily.cs).</summary>
        public const string ColumnFormat = "column.format";

        /// <summary>M4 point 2 (T268, relationships; Kane, 26 September: relationships are in this release): the
        /// relationship family. For a column that no relationship uses yet, which key column on another table it
        /// points at, if any, and how the relationship behaves: which way a filter passes, many to one or one to
        /// one, and whether it is the table's main date when several of its dates point at the same date table.
        /// The engine lists the keys a column may join (same type, and no repeats on the one side, measured by
        /// derived facts when data is connected), checks every pick, and decides which relationships can be
        /// active so two tables are never joined by two filter paths (Plan/JevFamilies/RelationshipFamily.cs).
        /// It goes live only once its replay clears its bar (RelationshipFamily.JevDecides).</summary>
        public const string RelationshipJoin = "relationship.join";

        /// <summary>The two filter directions a relationship line may take, in the order a person reads them.</summary>
        public static readonly IReadOnlyList<string> RelationshipDirections = new List<string>
        {
            "one_way", "both_ways",
        }.AsReadOnly();

        /// <summary>The two cardinalities a relationship line may take. Many to many is never offered: it is the
        /// shape the readiness rules warn about (REL-M2M), and nothing measured on this computer can prove it.</summary>
        public static readonly IReadOnlyList<string> RelationshipCardinalities = new List<string>
        {
            "many_to_one", "one_to_one",
        }.AsReadOnly();

        /// <summary>A date column's role when several of a table's dates point at the same date table.</summary>
        public static readonly IReadOnlyList<string> RelationshipDateRoles = new List<string>
        {
            "main", "other",
        }.AsReadOnly();

        /// <summary>M4 point 5 (T268, what Copilot sees): the AI data schema family. For each visible table, column and
        /// measure, whether Copilot and data agents should see it (include) or not (exclude). It picks one of two
        /// option ids; the engine checks the pick against the schema's own rule (nothing a measure Copilot still sees
        /// uses is left out) and writes the change through set_ai_data_schema (Plan/JevFamilies/AiSchemaFamily.cs).</summary>
        public const string AiSchema = "ai.schema";

        /// <summary>The two AI data schema choices, in the order a person reads them. Part of the request digest.</summary>
        public static readonly IReadOnlyList<string> AiSchemaChoices = new List<string>
        {
            "include", "exclude",
        }.AsReadOnly();

        /// <summary>M4 (T268, the fair replay of 26 September): the column data category. For a visible text or
        /// number column with no data category yet, which kind of real-world thing its values hold, or none. Shipped
        /// on the fair replay (docs/redesign/jev-columns-replay.md, "Third run"): against a key checked from each
        /// column's own data, Jev when sure, else the rule, beat the rule on both the key and the blind hand labels.
        /// Below the bar it proposes nothing and the geographic rule decides (Plan/JevFamilies/ColumnCategoryFamily.cs).</summary>
        public const string ColumnCategory = "column.category";

        /// <summary>M4 (T268, Kane's redesign of 26 September): a number column's explicit base measure. Every number column
        /// the family hears back on is set not to add up by itself, and this point's pick (the aggregation, or none)
        /// chooses the measure that does the adding instead (Plan/JevFamilies/ColumnSummarizeFamily.cs). The name stays
        /// "column.summarize" so a permission already given keeps covering it; measure.starter is superseded.</summary>
        public const string ColumnSummarize = "column.summarize";

        /// <summary>The summarize options, in the fair replay's order. Fixed: the order is part of the request digest.</summary>
        public static readonly IReadOnlyList<string> SummarizeOptions = new List<string>
        {
            "sum", "none", "average", "count", "distinctcount", "min", "max",
        }.AsReadOnly();

        /// <summary>The data categories the category point offers, in the replay's order (rules.mjs DATACAT_CRITERIA).
        /// Fixed: the order is part of the request digest.</summary>
        public static readonly IReadOnlyList<string> DataCategories = new List<string>
        {
            "none", "Country", "StateOrProvince", "County", "City", "PostalCode", "Address", "Place", "Continent",
            "Latitude", "Longitude", "ImageUrl", "WebUrl", "Barcode",
        }.AsReadOnly();

        /// <summary>M4 point 3 (T268, Kane 26 September: in this release): the date table. Asked once per model that has
        /// no marked date table and whose formulas use time intelligence (the rule decides it is needed): which table it
        /// is (one of the tables the engine lists, each with a date column the engine checked for repeats and missing
        /// days where it could measure them) or a new generated one. The engine builds the mark or the generated table
        /// and always marks (Plan/JevFamilies/DateTableFamily.cs, docs/redesign/jev-datetable-replay.md).</summary>
        public const string DateTable = "date.table";

        /// <summary>Jev question-to-DAX (Kane, 26 September: "add to this release"): the parts of a data question. From
        /// the words a person or their assistant typed, Jev picks the measures, the columns to split by, how each value
        /// the words matched is used, and any top N, all from what the engine lists. The engine matches values on this
        /// computer and sends only "a value in this column matches these words"; it builds the one query shape, checks
        /// it and runs it (Query/JevQueryReader.cs, Query/QueryTemplate.cs). Asked by ask_data.</summary>
        public const string QuerySummarize = "query.summarize";

        /// <summary>Queries (D6, docs/queries-chart-pick-bar.md): which chart a Queries result opens as. The chart rules
        /// list the kinds that can show the result's shape; Jev picks one of those, or says the rules' pick is fine. It
        /// sends the question's words and the result's shape, never a row or a value. OFF until its replay clears the
        /// bar (Query/JevChartPick.cs, JevPicks). Asked by pick_chart_kind.</summary>
        public const string QueriesChartKind = "queries.chart_kind";

        /// <summary>Every chart kind Queries can draw, as the chart rules name them, plus the neutral "rules". The
        /// options asked are the kinds the rules leave for one result, in a seeded order, then "rules".</summary>
        public static readonly IReadOnlyList<string> ChartKinds = new List<string>
        {
            "matrix", "line", "bar", "area", "table", "card", "scatter", "rules",
        }.AsReadOnly();

        /// <summary>Round 5 of m11/plan-harm (Kane, 30 Sep 2026: "i would think Jev could choose what to exclude"): the cleanup row.
        /// For each unused column the rule would tick as a suggested removal, whether to suggest it or leave it out of
        /// the suggestion. It can only leave one out: Jev never ticks a column the rule left unticked, never adds one and
        /// never removes anything (Plan/CleanupSuggest.cs). Asked when a person works the cleanup row out.</summary>
        public const string CleanupSuggest = "cleanup.suggest";

        /// <summary>The two options of the cleanup point, fixed. The ids are the wire words.</summary>
        public static readonly IReadOnlyList<string> CleanupChoices = new List<string> { "suggest", "leave_out" }.AsReadOnly();

        /// <summary>The value ladder the plan is ranked on, lowest rung first. Ordered by meaning, because a
        /// score question's option order is part of the request digest and part of what is being asked.</summary>
        public static readonly IReadOnlyList<string> PlanValueRungs = new List<string>
        {
            "none", "small", "useful", "large", "urgent",
        }.AsReadOnly();

        private static readonly List<DecisionPoint> All = new List<DecisionPoint>
        {
            new DecisionPoint(
                name: MeasureFolderAndFormat,
                caller: "propose_measure_defaults",
                purpose: "Propose a display folder and a number format for a measure that was created without either. "
                       + "It sends the measure's name, facts worked out from its formula on this computer (which "
                       + "functions it uses, whether it divides, counts or adds up an amount of money, and whether it "
                       + "uses a date column or compares time periods), the names of the measures it uses, its table's name and column names and types, "
                       + "and the names, folders and formats of the other measures on that table. It never sends the "
                       + "formula or any value written in it. It never writes anything.",
                // Version 2 (F-229): the formula text was replaced by facts worked out from it, because a formula
                // can carry a value and the consent card promises values never go.
                schemaVersion: 2,
                snapshotFields: new[]
                {
                    "measure_name",
                    "measure_formula_facts",
                    "references_measures",
                    "table_name",
                    "table_columns",
                    "sibling_measures",
                    "measures",
                },
                questions: new[]
                {
                    new DecisionQuestionShape("display_folder", DecisionQuestionKind.Choice,
                        "the display folders already used in this model, plus none"),
                    new DecisionQuestionShape("format_family", DecisionQuestionKind.Choice,
                        "a fixed list of six number formats", FormatFamilies),
                },
                confidenceBar: 0.85,
                lowConfidenceBehaviour: "the proposal is left out and the measure is returned exactly as it is today",
                proOnly: true),

            new DecisionPoint(
                name: PlanRank,
                caller: "get_model_plan",
                purpose: "Put the plan for this model in value order. It sends the shape of the model (how many "
                       + "tables, columns and measures it has and whether it is connected) and, for each thing on "
                       + "the plan, its heading, its one line, which goal it serves, who would do it, how many "
                       + "objects it touches and the plain reasons the rules gave for it. It sends no formulas, no "
                       + "rows and no values. It changes the ORDER and nothing else: the same things are on the "
                       + "plan, with the same reasons and the same exact changes, whether it is asked or not.",
                schemaVersion: 1,
                snapshotFields: new[]
                {
                    "model_shape",
                    "plan_items",
                },
                questions: new[]
                {
                    // Asked once per item, all in ONE call, so the question NAMES are the item ids and cannot be
                    // listed here. The shape is the same for every one of them: the ladder below.
                    new DecisionQuestionShape("item_value", DecisionQuestionKind.Score,
                        "a fixed ladder of five value rungs, asked once for each thing on the plan in a single call",
                        PlanValueRungs),
                },
                confidenceBar: 0.85,
                lowConfidenceBehaviour: "the plan is shown in rule order with no numbers on it, which is the same plan",
                proOnly: true),

            new DecisionPoint(
                name: AskRoute,
                caller: "route_ask",
                purpose: "Work out which part of the product answers what a person typed into the box. It sends "
                       + "the words they typed and nothing else: no model, no names, no formulas, no values. It "
                       + "picks one of the twenty places in the product, or none of them. When the words cannot "
                       + "say whether a person is asking for a new model to be made, it also asks whether they are: "
                       + "a new model, something to do with a model that exists, or neither. It only moves a person "
                       + "to a page; it never changes anything.",
                schemaVersion: 1,
                snapshotFields: new[]
                {
                    "ask_text",
                },
                questions: new[]
                {
                    new DecisionQuestionShape("tool", DecisionQuestionKind.Choice,
                        "the twenty places in the product, described by their own help lines, plus none"),
                    new DecisionQuestionShape("model_intent", DecisionQuestionKind.Choice,
                        "a new model, something to do with a model that exists, or neither",
                        new[] { "new_model", "this_model", "neither" }),
                },
                confidenceBar: 0.85,
                lowConfidenceBehaviour: "the menu of every place is shown instead, and nobody is sent anywhere",
                proOnly: true),

            new DecisionPoint(
                name: MeasureStarter,
                caller: "get_model_plan",
                purpose: "Choose whether a number column that no measure uses yet gets a starter measure, and which "
                       + "one: none, a total, an average, the lowest, the highest, a count, or a count of different "
                       + "values. It sends the column's name, its table's name, the names and types of the table's "
                       + "other columns, and facts about the column: its data type, whether it is a key, which end "
                       + "of a relationship it is on, its summarize-by setting, its format and whether it is hidden, "
                       + "plus shares worked out on this computer (how many of its values are different, how many "
                       + "are blank, whether they are all whole numbers). It never sends a value, a minimum, a "
                       + "maximum or a sample. The name, the formula and the format of the measure are the "
                       + "engine's own. It never writes anything.",
                schemaVersion: 1,
                snapshotFields: new[]
                {
                    "table_name",
                    "column_name",
                    "column_facts",
                    "table_columns",
                },
                questions: new[]
                {
                    new DecisionQuestionShape("aggregation", DecisionQuestionKind.Choice,
                        "a fixed list of seven starter measures, including none", StarterAggregations),
                },
                confidenceBar: 0.85,
                lowConfidenceBehaviour: "the line arrives dropped, so nothing is added unless a person keeps it",
                proOnly: true),

            new DecisionPoint(
                name: ComposeDraft,
                caller: "compose_task",
                // What is sent is read off the engine's own lists (JevDraftBuilder.Build, LocalEngine.OptionsOf): a guard
                // test names every kind of name those lists can hold, so this text cannot drift from the code again (D3a).
                purpose: "Draft the steps of a task or a workflow from what a person typed. It is asked once for each "
                       + "step the words name. It sends that step's words, the names of the model's tables, and, where "
                       + "a step works on something Semanticus lists from the model and the list holds 40 names or "
                       + "fewer, those names: table names, column names with their table, measure names and "
                       + "partition names with their table. It sends no formulas, no descriptions, no rows and no "
                       + "values. For each step Semanticus knows how to run, it asks whether the words ask for it "
                       + "and, where Semanticus listed the choices, which one. Semanticus puts the steps in order and "
                       + "asks the person where a choice was not sure. For a step that would add something new and "
                       + "that the words plainly ask for, it is asked once more, with everything the person typed for "
                       + "this draft (the same words, together), whether they still want the new thing. That answer can "
                       + "only stop the step being ticked. It never writes anything and never changes "
                       + "the model.",
                schemaVersion: 1,
                snapshotFields: new[]
                {
                    "draft_words",
                    "table_names",
                },
                questions: new[]
                {
                    // Asked for every candidate step in ONE call, so the question NAMES carry the step template's id
                    // ("step:add-measure:include", "step:refresh:partitionRef") and cannot be listed here. "include" is a
                    // noul; an argument is a choice over the options the engine listed from the model, plus none. The adding
                    // rule's last question (version 6, C) is a call of its own with one choice, "step:role:wanted": yes, no or
                    // unsure, about one add, over the same two fields.
                    new DecisionQuestionShape("template_step", DecisionQuestionKind.Noul,
                        "for each step template Semanticus can run: whether the words ask for it, and for each argument it can list from the model, one of those options or none"),
                },
                confidenceBar: 0.85,
                lowConfidenceBehaviour: "a step the words name that Jev is not sure of takes the step Semanticus's own word "
                                      + "match finds for those words, kept, and one that adds only when the words ask to "
                                      + "add; with no match, a step Jev leaned to arrives "
                                      + "dropped for the person to keep, and in Workflows the step is left for the person "
                                      + "to fill in. An unsure choice becomes a question for the person. Only when Jev is "
                                      + "sure of nothing and the words match no step does the Plan list the steps for the "
                                      + "person to pick",
                proOnly: true),

            new DecisionPoint(
                name: ColumnFormat,
                caller: "get_model_plan",
                purpose: "Choose how a number or date column with no format yet is shown: as money, a percent, a "
                       + "whole number, a decimal, a date, or left as it is. It sends the column's name, its "
                       + "table's name, the column's data type and whether it is calculated, and the names and "
                       + "types of the table's other columns. It never sends a value, a count or a sample. The "
                       + "format itself is the engine's own, taken from the format library or from a money format "
                       + "this model already uses. It never writes anything.",
                schemaVersion: 1,
                snapshotFields: new[]
                {
                    "table_name",
                    "column_name",
                    "column_facts",
                    "table_columns",
                    "columns",
                },
                questions: new[]
                {
                    new DecisionQuestionShape("format_family", DecisionQuestionKind.Choice,
                        "a fixed list of six number formats", FormatFamilies),
                },
                confidenceBar: 0.85,
                lowConfidenceBehaviour: "the line arrives dropped, so the column keeps no format unless a person keeps it",
                proOnly: true),

            new DecisionPoint(
                name: RelationshipJoin,
                caller: "get_model_plan",
                purpose: "Choose whether a column that no relationship uses yet points at a key column on another "
                       + "table, and how the relationship behaves: which way a filter passes, many to one or one to "
                       + "one, and which of a table's dates is its main one. It sends the column's name, its table's "
                       + "name, the names and types of the table's other columns, the column's data type, whether it "
                       + "is hidden or a key, and the key columns on other tables that have the same type, each with "
                       + "its table's name, whether it has no repeats, whether it is its table's own key and how the "
                       + "column's name matches it, all worked out on this computer. It never sends a value, a count, "
                       + "a minimum, a maximum or a sample. It never writes anything.",
                schemaVersion: 1,
                snapshotFields: new[]
                {
                    "table_name",
                    "column_name",
                    "column_facts",
                    "table_columns",
                    "candidate_keys",
                },
                questions: new[]
                {
                    new DecisionQuestionShape("join_to", DecisionQuestionKind.Choice,
                        "the key columns on other tables with the same type as the column, plus none"),
                    new DecisionQuestionShape("filter_direction", DecisionQuestionKind.Choice,
                        "one way or both ways", RelationshipDirections),
                    new DecisionQuestionShape("cardinality", DecisionQuestionKind.Choice,
                        "many to one or one to one, asked only when the column has no repeats either", RelationshipCardinalities),
                    new DecisionQuestionShape("date_role", DecisionQuestionKind.Choice,
                        "the main date or another date, asked only when several of the table's dates point at a date table", RelationshipDateRoles),
                },
                confidenceBar: 0.85,
                lowConfidenceBehaviour: "Semanticus's own name rule answers instead, and a column the rule cannot place gets no relationship",
                proOnly: true),

            new DecisionPoint(
                name: AiSchema,
                caller: "get_model_plan",
                purpose: "Choose which tables, columns and measures Copilot and data agents see when they answer "
                       + "questions about this model. For each visible one it sends its name, its table's name, "
                       + "whether it is a table, a column or a measure, and facts read from the model's structure on "
                       + "this computer: a column's data type, whether it is a key, which end of a relationship it is "
                       + "on, whether it sets another column's order and how many measures use it; the names of the "
                       + "columns and measures a measure uses; and the names and types of the other fields on its "
                       + "table. It never sends a formula, a value, a count of rows or a sample. Semanticus checks "
                       + "every choice and writes the change itself. It never writes anything.",
                schemaVersion: 1,
                snapshotFields: new[]
                {
                    "table_name",
                    "object_kind",
                    "object_name",
                    "object_facts",
                    "table_fields",
                    "objects",
                },
                questions: new[]
                {
                    new DecisionQuestionShape("ai_schema", DecisionQuestionKind.Choice,
                        "include or exclude", AiSchemaChoices),
                },
                confidenceBar: 0.85,
                // What the Pro path really does below the bar (AiSchemaFamily.ProPick and Resolve), in the words the
                // agent door hands the assistant.
                lowConfidenceBehaviour: "Semanticus's own rules decide that object instead, and the line says it is the rule's; "
                                      + "the rules never put back something a person left out, and a put-back Jev is sure of "
                                      + "waits, dropped, until a person keeps it",
                proOnly: true),

            new DecisionPoint(
                name: ColumnCategory,
                caller: "get_model_plan",
                purpose: "Choose what kind of thing a text or number column with no data category holds: a country, "
                       + "a state, a county, a city, a postal code, an address, a place, a continent, a latitude, "
                       + "a longitude, an image link, a web link, a barcode, or none of these. It sends the column's "
                       + "name, its table's name, the names of the table's other columns, the model's table names with "
                       + "how many columns and measures each has, and facts about the column: its data type, whether it "
                       + "is a key or calculated, which end of a relationship it is on, how many measures name it, "
                       + "whether another column sorts by it, which columns pair one to one with it, roughly how many "
                       + "rows its table has (two figures, never the exact count), and shares worked out on this "
                       + "computer (how many of its values are different or blank, and what share of them look like "
                       + "years, months, days, periods, countries, regions, cities, postal codes, links or email "
                       + "addresses). It never sends a value, a minimum, a maximum or a sample. It never writes anything.",
                schemaVersion: 1,
                snapshotFields: new[]
                {
                    "table_name",
                    "column_name",
                    "column_facts",
                    "table_columns",
                },
                questions: new[]
                {
                    new DecisionQuestionShape("data_category", DecisionQuestionKind.Choice,
                        "a fixed list of thirteen data categories, plus none", DataCategories),
                },
                confidenceBar: 0.85,
                lowConfidenceBehaviour: "nothing is proposed by Jev; a measured column is decided by the rule that reads its value shares, and any other column by the geographic rule",
                proOnly: true),

            new DecisionPoint(
                name: ColumnSummarize,
                caller: "get_model_plan",
                purpose: "Choose the explicit measure a number column gets instead of adding up by itself: its total, its "
                       + "average, a count, a count of different values, the smallest, the largest, or no measure. The "
                       + "column itself is set not to add up, and Semanticus writes the measure. It sends the same "
                       + "facts as the data category choice: the column's and table's names, the names of the table's "
                       + "other columns, the model's table names with their column and measure counts, how the model uses "
                       + "the column, roughly how many rows its table has (two figures), and shares worked out on this "
                       + "computer. It never sends a value, a minimum, a maximum, a sample or any column's current "
                       + "setting. It never writes anything.",
                schemaVersion: 1,
                snapshotFields: new[]
                {
                    "table_name",
                    "column_name",
                    "column_facts",
                    "table_columns",
                    "model_tables",
                    "table_facts",
                    "columns",
                },
                questions: new[]
                {
                    new DecisionQuestionShape("summarize_by", DecisionQuestionKind.Choice,
                        "a fixed list of seven ways to combine values", SummarizeOptions),
                },
                confidenceBar: 0.85,
                lowConfidenceBehaviour: "Semanticus's own rule chooses the measure instead and says so on its line; a flag or a line number never gets a total",
                proOnly: true),

            new DecisionPoint(
                name: DateTable,
                caller: "get_model_plan",
                purpose: "Choose which table is the date table of a model whose formulas use time intelligence and that has "
                       + "no marked date table, or whether to add a new one. It sends the names of the model's tables, whether "
                       + "each is on the one side or the many side of its relationships, which tables relate to each "
                       + "table, whether each table is built by a formula, the names and types of their "
                       + "columns, and facts worked out on this computer about each date column: whether it is the "
                       + "table's key, whether its dates repeat, whether any day is missing between the first and the "
                       + "last, whether the table is built by a calendar formula, and whether the model's formulas read "
                       + "that column. It also says whether the model's "
                       + "formulas use time intelligence, and how many tables and relationships the model has. It never "
                       + "sends a date, a first or last day, a count of rows, or any value out of your data. "
                       + "The mark and the new table are the engine's own. It never writes anything.",
                schemaVersion: 1,
                snapshotFields: new[]
                {
                    "model_tables",
                    "model_facts",
                },
                questions: new[]
                {
                    new DecisionQuestionShape("date_table", DecisionQuestionKind.Choice,
                        "the tables this model could use as its date table, plus a new one"),
                },
                confidenceBar: 0.85,
                lowConfidenceBehaviour: "nothing is proposed, and the plan's own Mark the date table row stays as it was",
                proOnly: true),

            new DecisionPoint(
                name: QuerySummarize,
                caller: "ask_data",
                purpose: "Work out what a question about this model's numbers asks for: which measures, split by which "
                       + "columns, narrowed how, and whether only a top few rows. It sends the words of the question, the "
                       + "names, types, descriptions and folders of the measures and columns a person can see, and, "
                       + "where the question's words match a value in a column, only that column's name and the "
                       + "question's own words. When a word of the question is one of the model's own other names for a "
                       + "measure (a synonym set in the model), it says which measure that word names, and only when the "
                       + "question uses that word. The values are matched on this computer; no value, row or count is "
                       + "sent. Before a number is shown, it is also asked what, if anything, the reading Semanticus built "
                       + "leaves out of the question (and, when that is not clear, up to two short follow-up questions about "
                       + "one thing it may leave out): it sends the question's words, the names, types, descriptions and folders of only the "
                       + "measures and columns that reading uses, and the reading told in the question's own words, with "
                       + "no value, date, row or count (it names what a ratio measure divides, by the measures and columns it reads). Semanticus builds the query itself, checks it and runs it. It never changes the model.",
                schemaVersion: 1,
                // stated_reading is the reading Semanticus built, told in the question's own words (StatedReading.ForJev), for the
                // check that runs just before a number is shown. Added on 29 Sep with the check (the ruling Jev data questions v2);
                // the integrator's one release bump of ConsentSchemaVersion covers it. The comment lives here, not inside the list:
                // tools/relay/test/drift.test.mjs reads every quoted string inside the list as a field.
                snapshotFields: new[]
                {
                    "question_words",
                    "model_fields",
                    "value_matches",
                    "stated_reading",
                },
                questions: new[]
                {
                    // Asked in one call for the parts and a second (third only if the shortlist missed) for the split,
                    // so the question NAMES carry the part they settle ("q:measure:1", "q:splits", "q:group:1",
                    // "q:filter:f3", "q:top") and cannot all be listed here: there is one filter question per place the
                    // words matched a value. Every one picks one option the engine listed, or none. The check that comes
                    // last is one more ("q:check:gap"), and when its answer is not clear, up to two ("q:check:real", one per gap).
                    new DecisionQuestionShape("query_part", DecisionQuestionKind.Choice,
                        "for each part of the question: one of the measures, columns, filter uses or row counts Semanticus lists, or none"),
                },
                confidenceBar: 0.85,
                lowConfidenceBehaviour: "nothing runs, and the answer says plainly which part was not sure, so a person can settle it; a reading the check is not sure of is not run either, and is offered as it is",
                proOnly: true),

            new DecisionPoint(
                name: QueriesChartKind,
                caller: "pick_chart_kind",
                purpose: "Choose which chart shows the answer to a data question best. It sends the words of the question, "
                       + "when there are any, and the shape of the answer: the name of each column it is split by, whether "
                       + "that column is a date and how many different values it has, how many numbers it shows and how "
                       + "many rows came back. It never sends a row or a value. It picks only among the charts the chart "
                       + "rules say can show that shape, or keeps the rules' pick. The reason shown is Semanticus's own. "
                       + "It never changes the model.",
                schemaVersion: 1,
                snapshotFields: new[]
                {
                    "question_words",
                    "result_shape",
                },
                questions: new[]
                {
                    new DecisionQuestionShape("chart_kind", DecisionQuestionKind.Choice,
                        "the charts the chart rules leave for this result, in a seeded order, plus the rules' pick is fine",
                        ChartKinds),
                },
                confidenceBar: 0.85,
                lowConfidenceBehaviour: "the chart rules' pick is shown, labelled as the rules' pick",
                proOnly: true),

            new DecisionPoint(
                name: CleanupSuggest,
                caller: "model_plan_action",
                purpose: "Choose which unused columns start ticked on the Plan's cleanup row, the list that keeps a model lean. "
                       + "For each unused column that would start ticked, it is asked whether to suggest removing it or to leave "
                       + "it out of the suggestion. It can only leave one out: it never ticks a column, never adds one and never "
                       + "removes anything, and every unused column stays listed for you to tick. It sends each column's name and "
                       + "data type, whether it is calculated, its table's name, which end of its table's relationships the table "
                       + "sits on, whether the table holds number columns, and the names and types of the table's other columns. "
                       + "It never sends a value, a count or a sample. It never writes anything.",
                schemaVersion: 1,
                snapshotFields: new[]
                {
                    "cleanup_tables",
                    "cleanup_columns",
                },
                questions: new[]
                {
                    // Asked for a batch of columns in ONE call, so the question NAMES carry the column's ref
                    // ("cleanup:column:Stock Item/Barcode") and cannot be listed here. Every one is the same choice.
                    new DecisionQuestionShape("suggest_removal", DecisionQuestionKind.Choice,
                        "for each unused column that would start ticked: suggest removing it, or leave it out of the suggestion",
                        CleanupChoices),
                },
                // The bar is how sure Jev must be that a column SHOULD go before it stays ticked: a "suggest" below it, like any
                // "leave it out", unticks the line. The bar is the "likely" floor and not the 0.85 the other points use because this
                // point can only make the list more careful: a wrongly unticked line costs a person one click, and the person
                // reviews every line before anything is removed. A call that is not answered at all changes nothing.
                confidenceBar: 0.6,
                lowConfidenceBehaviour: "the line is unticked with a note that Jev was not sure, and you decide when you review the list; with no answer at all, it stays as the rule first ticked it",
                proOnly: true),
        };

        public static IReadOnlyList<DecisionPoint> Points { get { return All.AsReadOnly(); } }

        public static bool TryGet(string name, out DecisionPoint point)
        {
            point = name == null ? null : All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
            return point != null;
        }

        public static DecisionPoint Require(string name)
        {
            DecisionPoint point;
            if (!TryGet(name, out point))
                throw new InvalidOperationException(
                    $"'{name}' is not a registered decision point. Every ask names a point in DecisionRegistry; add it there, with its declared fields, before asking.");
            return point;
        }

        /// <summary>The consent schema version for the WHOLE product. Consent is granted against this number, so
        /// adding or renaming a declared field on any point means bumping this and asking the person again.
        ///
        /// Bump this together with <see cref="DeclaredFieldFingerprint"/>'s frozen value in the tests.
        ///
        /// Version 2 (M2): the plan's ranking and the box were added, and they send two kinds of detail the
        /// product could not send before, so every permission granted against version 1 stops counting and the
        /// person is asked again. That is the cost of the rule and it is the right cost.
        ///
        /// Version 3 (F-229, F-231, 23 September): the measure point stopped sending formula text and sends
        /// facts worked out from it instead, and the card's "What goes" now names the questions a person types
        /// into the ask box. The list and the card both changed, so a permission given under version 2 stops
        /// counting and the card is shown once more. references_measures (the names of the measures a measure's
        /// formula uses) joined the same version before release, so it did not need a version of its own. So did
        /// measure.starter (T268, 24 September): no release has shipped a decision registry at all (v1.2.0 carries
        /// none), so nobody holds a version 3 permission its four fields could fall outside of. The model's marker is not
        /// touched: the new answer is kept for the same model, so permission stays per model across the bump.
        ///
        /// Version 4 (composer lane L5, 25 September): compose.draft sends the words a person types to describe a task
        /// or a workflow, and the card's "What goes" gained a line saying so. Joining version 3 quietly was not enough:
        /// get_consent reads a version 3 permission as current, so a person who agreed before would never have been
        /// asked, and every draft would have been skipped for a permission that did not cover it. column.format (T268,
        /// 25 September, columns first) joined the same version 4 set: a permission given before it does not cover its
        /// four fields, so the family stays off for that model until the card is agreed again. ai.schema (T268, 26 September,
        /// what Copilot sees) joined version 4 the same way: names, types and structure facts the card already names.
        /// column.category (T268,
        /// the fair replay, 26 September) joined version 4 too, on Kane's instruction not to bump it: no release
        /// carries version 4, and its facts are the kinds the card already names ("counts"). As with column.format, a
        /// permission given on an earlier build does not list its four fields (DecisionConsent.Covers checks each
        /// one), so the family stays off for that model until the card is agreed again. column.summarize
        /// (the same day, Kane's decision) joined version 4 on the same terms; the integrator bumps the version once.
        /// date.table (T268, M4 point 3, 26 September) joined the same version 4 set for the same reason: still no release
        /// carries the registry, and a version 4 permission given before it does not cover its two fields (Covers checks the
        /// point's fields, not only the number), so the card is asked again once for that model.
        /// query.summarize (26 September, Jev reads data questions) joined the list with its own card line, "the words
        /// of your data questions": a permission given before it does not cover its three fields, so get_consent reads it
        /// as not current and the card is shown again. cleanup.suggest (m11/plan-harm round 5, 30 September) joined the same
        /// version 4 set for the same reason: still no release carries the registry, and the card's "What goes" already names
        /// table and column names, data types and relationships, which is all it sends.
        ///
        /// Version 5 (the M11 join, 30 September 2026: THE ONE RELEASE BUMP). Every point that joined version 4 after the composer
        /// lane (column.format, ai.schema, column.category, column.summarize, date.table, relationship.join, query.summarize,
        /// queries.chart_kind, cleanup.suggest) and the wider list of what the card says goes (the words of a data question, the
        /// other names a model gives a measure) is one release, and no release ever carried version 4. So a permission given
        /// under any earlier build stops counting once and the card is shown again, once per model, with the whole list.
        /// m11/jev-batch (30 September) joined the same version 5 set: measure.folder_and_format gained `measures`, column.format and
        /// column.summarize gained `columns` (and column.summarize `model_tables` and `table_facts`), ai.schema gained `objects`.
        /// They carry the SAME names, types, counts, shares and yes-or-no facts the per-object requests carried, one request per
        /// table instead of one per object (Plan/JevFamilies/JevTableBatch.cs), so no release holds a version 5 permission they
        /// could fall outside of and the version is not bumped again.</summary>
        public const int ConsentSchemaVersion = 5;

        /// <summary>Every declared field across every point, as "point:field", sorted. This is the exact list a
        /// person consents to, and the thing the settings page shows.</summary>
        public static IReadOnlyList<string> DeclaredFields =>
            All.SelectMany(p => p.SnapshotFields.Select(f => p.Name + ":" + f))
               .OrderBy(s => s, StringComparer.Ordinal)
               .ToList()
               .AsReadOnly();

        /// <summary>A digest of the declared field list. A test pins this, so a new field cannot slip in without
        /// the consent version being bumped in the same change.</summary>
        public static string DeclaredFieldFingerprint =>
            DecisionDigest.Sha256Hex(string.Join("\n", DeclaredFields));
    }

    /// <summary>Building the one thing that leaves the machine.</summary>
    public static class DecisionSnapshot
    {
        /// <summary>Build a snapshot for a point, refusing any field the point has not declared. The refusal is an
        /// exception on purpose: a caller sending an undeclared field is a bug in the caller, and turning it into
        /// a quiet drop would mean the settings list stops describing what actually goes out.</summary>
        public static IReadOnlyDictionary<string, object> Build(DecisionPoint point, IEnumerable<KeyValuePair<string, object>> fields)
        {
            if (point == null) throw new ArgumentNullException(nameof(point));

            var declared = new HashSet<string>(point.SnapshotFields, StringComparer.Ordinal);
            var snapshot = new Dictionary<string, object>(StringComparer.Ordinal);

            foreach (var field in fields ?? Enumerable.Empty<KeyValuePair<string, object>>())
            {
                if (!declared.Contains(field.Key))
                    throw new InvalidOperationException(
                        $"Point '{point.Name}' tried to send the field '{field.Key}', which it has not declared. "
                      + $"Declared fields: {string.Join(", ", point.SnapshotFields)}. Declare it and bump DecisionRegistry.ConsentSchemaVersion, or do not send it.");

                if (snapshot.ContainsKey(field.Key))
                    throw new InvalidOperationException($"Point '{point.Name}' set the field '{field.Key}' twice.");

                snapshot[field.Key] = field.Value;
            }

            // Prove the whole thing is writable as canonical JSON here, at the point of construction, rather than
            // later inside the client where a throw would read as a network failure.
            DecisionDigest.CanonicalSnapshotJson(snapshot);
            return snapshot;
        }

        /// <summary>A size fence. An oversized field is REFUSED, never truncated: a silently shortened snapshot is
        /// a different piece of evidence wearing the same digest inputs, and the plan says oversized fields are
        /// rejected rather than redacted.</summary>
        public static void RequireWithin(IReadOnlyDictionary<string, object> snapshot, int maxBytes)
        {
            var json = DecisionDigest.CanonicalSnapshotJson(snapshot);
            var bytes = System.Text.Encoding.UTF8.GetByteCount(json);
            if (bytes > maxBytes)
                throw new InvalidOperationException(
                    $"The snapshot is {bytes.ToString(CultureInfo.InvariantCulture)} bytes, over the {maxBytes.ToString(CultureInfo.InvariantCulture)} byte limit. Send fewer siblings or columns; it is never truncated.");
        }
    }
}
