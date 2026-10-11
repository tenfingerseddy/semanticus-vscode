using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Semanticus.Engine
{
    // ============================================================================================
    // Workflow composer contract section 2: the per-parameter classification, beside OpTaxonomy.
    //
    // OpTaxonomy says where an op LIVES. This file says what each of an op's parameters IS, so a
    // workflow step Semanticus runs knows which arguments the engine can list (pick), which are closed
    // scalars (flag, number, enum), which only a writer can supply (authored: those become blanks), and
    // which Semanticus fills itself (internal). It also holds the three registries the composer reads the
    // same way on every door: the enumerators (option lists), the generators (engine-written text from a
    // fixed template, no inference), and the executable allowlist (the ops a `performer: semanticus`
    // step may name).
    //
    // Completeness is required only for the executable ops (contract 2.4), so the table is the size of the
    // composer, not of the whole tool surface. The authored rows of every other op are here too, because
    // inventory section 6.1 is the list of what nobody but a writer can supply, and a test pins it.
    // OpParameterKindsTests holds the table to the real MCP signatures in both directions.
    // ============================================================================================
    public static class OpParameterKinds
    {
        public const string Change = "change";
        public const string After = "after";
        public const string Prepare = "prepare";

        /// <summary>The blank kinds a workflow may name (contract 1.5). An executable op's authored parameter
        /// uses one of these, because that is what its blank will be.</summary>
        public static readonly string[] BlankKinds = { "dax", "description", "m", "name", "choice" };

        /// <summary>Authored kinds only a non-executable op uses. Such an op never appears on a step Semanticus
        /// runs, so its text never becomes a blank and needs no blank kind.</summary>
        public static readonly string[] OtherAuthoredKinds = { "text", "json", "markdown", "script" };

        public sealed class EnumeratorSpec
        {
            public string Name { get; init; }
            public bool TakesTable { get; init; }
            public string Source { get; init; }       // model | source | service
            public string Lists { get; init; }
        }

        public sealed class GeneratorSpec
        {
            public string Id { get; init; }
            public string Op { get; init; }
            public string[] Writes { get; init; } = Array.Empty<string>();
            public string Describes { get; init; }
        }

        public sealed class ExecutableOp
        {
            public string Op { get; init; }
            public string Group { get; init; }        // change | after | prepare
            public string ApplierKind { get; init; }  // the ChangeItem kind ApplyOneItem applies; change group only
            public bool ApplierPending { get; init; } // true until lane L3 adds the kind to ApplyOneItem
        }

        // ---- enumerators (contract 2.2) -----------------------------------------------------------
        // One registry, read by the draft builder, the Studio picker and Jev's option sets, so all three see
        // the same list. The first ten are the contract's; the rest are the smallest additions that let
        // every executable op's reference parameters be a pick rather than free text (logged in the L1 report).
        // Running them at prepare time belongs to the run path, not to this file.
        public static readonly IReadOnlyDictionary<string, EnumeratorSpec> Enumerators = new[]
        {
            E("tables", false, "model", "The model's tables."),
            E("columns", true, "model", "The columns of the model, or of one table."),
            E("number_columns", true, "model", "The numeric columns of the model, or of one table."),
            E("measures", false, "model", "The model's measures."),
            E("format_templates", false, "model", "The format strings Semanticus offers."),
            E("source_tables", false, "source", "The tables the connected source holds."),
            E("source_columns", true, "source", "The columns of one source table."),
            E("stages", false, "service", "The stages of the deployment pipeline."),
            E("roles", false, "model", "The model's security roles."),
            E("calendars", false, "model", "The model's calendars."),
            E("objects", false, "model", "Any model object: a table, a column, a measure or a hierarchy."),
            E("relationships", false, "model", "The model's relationships."),
            E("partitions", true, "model", "The partitions of the model, or of one table."),
            E("calculation_groups", false, "model", "The model's calculation groups."),
            E("pipelines", false, "service", "The deployment pipelines the account can reach."),
            E("tests", false, "model", "The saved tests beside the model."),
            E("data_categories", false, "model", "The data categories set_data_category accepts."),
        }.ToDictionary(e => e.Name, StringComparer.Ordinal);

        // ---- generators (contract 2.3) ------------------------------------------------------------
        public static readonly IReadOnlyDictionary<string, GeneratorSpec> Generators = new[]
        {
            new GeneratorSpec { Id = "starter_measure", Op = "create_measure", Writes = new[] { "expression", "name" },
                Describes = "A starter measure from StarterMeasureTemplates, for example SUM ( Returns[RefundAmount] )." },
            new GeneratorSpec { Id = "time_intelligence", Op = "generate_time_intelligence", Writes = new[] { "displayFolder" },
                Describes = "The time intelligence suite generate_time_intelligence writes, in its own folder." },
            new GeneratorSpec { Id = "date_table", Op = "generate_date_table", Writes = new[] { "startExpr", "endExpr" },
                Describes = "The date range generate_date_table works out from the model's date columns." },
        }.ToDictionary(g => g.Id, StringComparer.Ordinal);

        // ---- the executable allowlist (contract 2.4) ------------------------------------------------
        // Change group: one applier per op, the kind ApplyOneItem's switch names (contract 3.2). A pending
        // kind is one lane L3 adds; the test fails when a kind lands without the flag being cleared, so the
        // flag cannot outlive the thing it waits for.
        public static readonly IReadOnlyDictionary<string, ExecutableOp> Executable = new[]
        {
            X("create_import_table", Change, "create_import_table"),
            X("create_column", Change, "create_column"),
            X("create_relationship", Change, "create_relationship"),
            X("set_relationship_crossfilter", Change, "set_relationship_crossfilter"),
            X("set_relationship_cardinality", Change, "set_relationship_cardinality"),
            X("set_relationship_active", Change, "set_relationship_active"),
            X("set_column_data_type", Change, "set_data_type"),
            X("set_column_hidden", Change, "set_column_hidden"),
            X("set_summarize_by", Change, "set_summarize_by"),
            X("set_data_category", Change, "set_data_category"),
            X("set_sort_by_column", Change, "set_sort_by_column"),
            X("create_measure", Change, "create_measure"),
            X("update_measure", Change, "set_dax"),
            X("set_measure_format", Change, "set_measure_format"),
            X("set_display_folder", Change, "set_display_folder"),
            X("set_description", Change, "set_description"),
            X("set_synonyms", Change, "set_synonyms"),
            X("rename_object", Change, "rename"),
            X("mark_date_table", Change, "mark_date_table"),
            X("create_calculation_group", Change, "create_calculation_group"),
            X("create_calculation_item", Change, "create_calculation_item"),
            X("create_role", Change, "create_role"),
            X("set_table_permission", Change, "set_table_permission"),
            X("generate_time_intelligence", Change, "generate_time_intelligence"),
            X("generate_date_table", Change, "generate_date_table"),

            // After the change: each leaves the model, so Undo does not reach it (contract 1.6).
            X("refresh_partition", After), X("save_model", After), X("run_tests", After), X("export_test_report", After),
            X("capture_baseline", After), X("deploy_stage", After), X("deploy_live", After),
            X("ai_readiness_scan", After), X("bpa_scan", After),
            // The record of a draft's checked steps (lane l9-finish): the draft's own guided sub-run, which Semanticus names.
            X("save_evidence", After),

            // Prepare: reads that return facts and resolved options before anything changes.
            X("get_source_schema", Prepare), X("list_columns", Prepare), X("get_model_graph", Prepare),
            X("impact_assessment", Prepare), X("benchmark_dax", Prepare), X("profile_dax", Prepare),
        }.ToDictionary(x => x.Op, StringComparer.Ordinal);

        // ---- the table: op -> parameter -> kind ---------------------------------------------------------
        // Connection settings (endpoint, database, authMode, tenantId), the two-step commit and its token,
        // output shape (detail, maxFindings...) and the session fence are internal: Semanticus fills them from
        // the session and the run, and a workflow never asks for them.
        private const string DataTypes = "enum:String|Int64|Decimal|Double|DateTime|Boolean";
        private const string CrossFilter = "enum:OneDirection|BothDirections";
        private const string Cardinality = "enum:One|Many";

        private static readonly Dictionary<string, Dictionary<string, string>> Table = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            // change group
            ["create_import_table"] = P(("name", "authored:name"), ("mExpression", "authored:m")),
            ["create_column"] = P(("tableRef", "pick:tables"), ("name", "authored:name"), ("dataType", DataTypes), ("sourceColumn", "pick:source_columns")),
            ["create_relationship"] = P(("fromColumnRef", "pick:columns"), ("toColumnRef", "pick:columns"), ("crossFilter", CrossFilter), ("isActive", "flag")),
            ["set_relationship_crossfilter"] = P(("relationshipName", "pick:relationships"), ("crossFilteringBehavior", CrossFilter)),
            ["set_relationship_cardinality"] = P(("relationshipName", "pick:relationships"), ("fromCardinality", Cardinality), ("toCardinality", Cardinality)),
            ["set_relationship_active"] = P(("relationshipName", "pick:relationships"), ("isActive", "flag")),
            ["set_column_data_type"] = P(("columnRef", "pick:columns"), ("dataType", DataTypes)),
            ["set_column_hidden"] = P(("objRef", "pick:objects"), ("hidden", "flag")),
            ["set_summarize_by"] = P(("objRef", "pick:columns"), ("summarizeBy", "enum:None|Sum|Average|Min|Max|Count|DistinctCount")),
            ["set_data_category"] = P(("objRef", "pick:columns"), ("dataCategory", "pick:data_categories")),
            ["set_sort_by_column"] = P(("objRef", "pick:columns"), ("sortByColumnName", "pick:columns")),
            ["create_measure"] = P(("tableRef", "pick:tables"), ("name", "authored:name"), ("expression", "authored:dax"), ("displayFolder", "authored:name")),
            ["update_measure"] = P(("objRef", "pick:measures"), ("expression", "authored:dax"), ("sessionId", "internal")),
            ["set_measure_format"] = P(("objRef", "pick:measures"), ("formatString", "pick:format_templates")),
            ["set_display_folder"] = P(("refs", "pick:objects"), ("folder", "authored:name")),
            ["set_description"] = P(("objRef", "pick:objects"), ("text", "authored:description")),
            ["set_synonyms"] = P(("objRef", "pick:objects"), ("terms", "authored:name"), ("culture", "internal")),
            ["rename_object"] = P(("objRef", "pick:objects"), ("newName", "authored:name"), ("sessionId", "internal")),
            ["mark_date_table"] = P(("tableRef", "pick:tables"), ("dateColumn", "pick:columns")),
            ["create_calculation_group"] = P(("name", "authored:name")),
            ["create_calculation_item"] = P(("calcGroupRef", "pick:calculation_groups"), ("name", "authored:name"), ("expression", "authored:dax")),
            ["create_role"] = P(("name", "authored:name"), ("modelPermission", "enum:None|Read|ReadRefresh|Refresh|Administrator")),
            ["set_table_permission"] = P(("roleName", "pick:roles"), ("tableRef", "pick:tables"), ("filterDax", "authored:dax")),
            ["generate_time_intelligence"] = P(("baseMeasureRef", "pick:measures"), ("dateColumn", "pick:columns"),
                ("variants", "enum:YTD|QTD|MTD|PY|YoY|YoYPct|ROLL12|R3M|R6M|SPLY|PYTD|PM|MoM|MoM%"), ("displayFolder", "authored:name")),
            ["generate_date_table"] = P(("tableName", "authored:name"), ("startExpr", "authored:dax"), ("endExpr", "authored:dax"), ("markAsDate", "flag")),

            // after the change
            ["refresh_partition"] = P(("partitionRef", "pick:partitions"), ("refreshType", "enum:Full|DataOnly|Calculate|ClearValues|Automatic|Add"),
                ("endpoint", "internal"), ("database", "internal"), ("authMode", "internal"), ("tenantId", "internal"), ("commit", "internal")),
            ["save_model"] = P(("path", "internal"), ("format", "enum:TMDL|bim|folder"), ("overwrite", "flag")),
            ["run_tests"] = P(("persist", "flag"), ("only", "pick:tests"), ("sections", "enum:measures|relationships|tableCounts")),
            ["export_test_report"] = P(),
            ["capture_baseline"] = P(("objRef", "pick:measures"), ("groupBy", "pick:columns"), ("filters", "authored:dax"),
                ("includeDependents", "flag"), ("maxMeasures", "number"), ("label", "authored:name")),
            ["deploy_stage"] = P(("pipelineId", "pick:pipelines"), ("sourceStageId", "pick:stages"), ("targetStageId", "pick:stages"),
                ("items", "internal"), ("note", "authored:description"), ("commit", "internal"), ("confirmToken", "internal"),
                ("forceOverride", "flag"), ("authMode", "internal"), ("overrideReason", "authored:description")),
            ["deploy_live"] = P(("endpoint", "internal"), ("database", "internal"), ("authMode", "internal"), ("tenantId", "internal"),
                ("commit", "internal"), ("overrideReason", "authored:description"), ("confirmToken", "internal"), ("deleteRefs", "pick:objects")),
            ["ai_readiness_scan"] = P(("detail", "internal"), ("live", "flag"), ("category", "internal"), ("severityMin", "internal"), ("maxFindings", "internal")),
            ["bpa_scan"] = P(("detail", "internal"), ("category", "internal"), ("ruleId", "internal"), ("severityMin", "internal"),
                ("autoFixableOnly", "internal"), ("maxViolations", "internal")),
            // sourceId is the draft's guided sub-run, filled by Semanticus (DraftRunner.RunAfterAsync); a workflow never names a run.
            ["save_evidence"] = P(("source", "enum:workflow|tests"), ("sourceId", "internal")),

            // prepare-phase reads
            ["get_source_schema"] = P(("tableRef", "pick:tables"), ("authMode", "internal"), ("tenantId", "internal")),
            ["list_columns"] = P(),
            ["get_model_graph"] = P(("detail", "internal")),
            ["impact_assessment"] = P(("objectRef", "pick:objects"), ("intent", "enum:change|rename|remove|restructure"),
                ("scope", "enum:modelAndReports|model"), ("reportPaths", "internal")),
            ["benchmark_dax"] = P(("query", "authored:dax"), ("runs", "number"), ("mode", "enum:warm|coldWarm"), ("clearForCold", "flag"), ("confirm", "internal")),
            ["profile_dax"] = P(("query", "authored:dax")),

            // Inventory 6.1, the authored parameters of ops a Semanticus step may not name. Only the authored
            // parameters are classed here: completeness is the executable ops' rule, not these.
            ["create_calculated_column"] = P(("name", "authored:name"), ("expression", "authored:dax")),
            ["create_calculated_table"] = P(("name", "authored:name"), ("expression", "authored:dax")),
            ["set_calc_item_format_string"] = P(("formatExpression", "authored:dax")),
            ["set_measure_format_expression"] = P(("formatExpression", "authored:dax")),
            ["create_function"] = P(("name", "authored:name"), ("expression", "authored:dax")),
            ["create_field_parameter"] = P(("name", "authored:name"), ("items", "authored:text")),
            ["create_named_expression"] = P(("expression", "authored:m")),
            ["update_named_expression"] = P(("expression", "authored:m")),
            ["set_partition_m"] = P(("mExpression", "authored:m")),
            ["set_incremental_refresh_policy"] = P(("pollingExpression", "authored:m")),
            ["create_table"] = P(("name", "authored:name")),
            ["create_hierarchy"] = P(("name", "authored:name")),
            ["create_perspective"] = P(("name", "authored:name")),
            ["create_data_source"] = P(("name", "authored:name")),
            ["create_directlake_table"] = P(("name", "authored:name")),
            ["duplicate_object"] = P(("newName", "authored:name")),
            ["rename_display_folder"] = P(("fromPath", "authored:name"), ("toPath", "authored:name")),
            ["set_ai_instructions"] = P(("instructions", "authored:text")),
            ["create_data_agent"] = P(("aiInstructions", "authored:text")),
            ["update_data_agent"] = P(("aiInstructions", "authored:text"), ("datasourceJson", "authored:json"), ("fewshotsJson", "authored:json")),
            ["publish_data_agent"] = P(("description", "authored:description")),
            ["define_calendar"] = P(("name", "authored:name"), ("description", "authored:description")),
            ["define_calendar_from_template"] = P(("startExpr", "authored:dax"), ("endExpr", "authored:dax")),
            ["set_property"] = P(("value", "authored:text")),
            ["set_properties"] = P(("value", "authored:text")),
            ["replace_in_object"] = P(("find", "authored:text"), ("replace", "authored:text")),
            ["propose_replace"] = P(("find", "authored:text"), ("replace", "authored:text")),
            ["apply_dax_script"] = P(("script", "authored:script")),
            ["apply_tmdl"] = P(("script", "authored:script")),
            ["set_plan_item"] = P(("after", "authored:text")),
            ["add_plan_item"] = P(("after", "authored:text"), ("title", "authored:name")),
            ["set_model_primer"] = P(("markdown", "authored:markdown")),
            ["add_insight"] = P(("text", "authored:text"), ("keys", "authored:name")),
            ["edit_insight"] = P(("text", "authored:text"), ("keys", "authored:name")),
            ["set_doc_section"] = P(("markdown", "authored:markdown")),
            ["set_spec"] = P(("specJson", "authored:json")),
            ["add_interview_question"] = P(("question", "authored:text"), ("query", "authored:dax"), ("scalarExpr", "authored:dax"),
                ("paraphraseExpr", "authored:dax"), ("filters", "authored:dax"), ("expectedValue", "authored:text")),
            ["run_interview"] = P(("attemptDax", "authored:dax")),
            ["try_test"] = P(("def", "authored:json")),
            ["save_test"] = P(("def", "authored:json")),
            ["save_workflow"] = P(("markdown", "authored:markdown")),
            ["save_workflow_template"] = P(("markdown", "authored:markdown")),
            ["edit_workflow_document"] = P(("exactText", "authored:markdown")),
            ["preview_workflow_edit"] = P(("draftText", "authored:markdown")),
            ["submit_workflow_step"] = P(("answers", "authored:json")),
            ["skip_workflow_step"] = P(("reason", "authored:description")),
            ["abort_workflow"] = P(("reason", "authored:description")),
            ["waive_finding"] = P(("reason", "authored:description")),
            ["skip_model_plan_item"] = P(("reason", "authored:description")),
            ["apply_plan"] = P(("overrideReason", "authored:description")),
            ["git_commit"] = P(("message", "authored:description")),
            ["fabric_git_commit"] = P(("comment", "authored:description")),
            ["create_history_checkpoint"] = P(("label", "authored:name")),
            ["validate_dax"] = P(("expression", "authored:dax")),
            ["lint_dax"] = P(("expression", "authored:dax")),
            ["verify_dax_equivalence"] = P(("exprA", "authored:dax"), ("exprB", "authored:dax")),
            ["probe_measure"] = P(("expr", "authored:dax")),
            ["run_dax"] = P(("query", "authored:dax")),
            ["pivot_measure"] = P(("measureExpr", "authored:dax")),
            ["optimize_measure"] = P(("candidates", "authored:dax")),
            ["reconcile_measure"] = P(("sql", "authored:text")),
        };

        /// <summary>Every classified op, and its parameters with their kinds.</summary>
        public static IReadOnlyDictionary<string, Dictionary<string, string>> All => Table;

        public static string KindOf(string op, string parameter) =>
            op != null && parameter != null && Table.TryGetValue(op, out var ps) && ps.TryGetValue(parameter, out var k) ? k : null;

        /// <summary>The blank kind of an authored parameter ("dax", "name"...), or null when it is not authored.</summary>
        public static string AuthoredKind(string op, string parameter)
        {
            var k = KindOf(op, parameter);
            return k != null && k.StartsWith("authored:", StringComparison.Ordinal) ? k.Substring("authored:".Length) : null;
        }

        public static bool IsExecutable(string op) => op != null && Executable.ContainsKey(op);

        /// <summary>change, after or prepare for an executable op; null otherwise.</summary>
        public static string GroupOf(string op) => op != null && Executable.TryGetValue(op, out var e) ? e.Group : null;

        /// <summary>One parameter of an op as the MCP door declares it. The door is the one source: this reads
        /// the same reflected method map get_op_catalog and dry_run use, so it cannot drift from the surface.</summary>
        public sealed class OpParameter
        {
            public string Name { get; init; }
            public bool Required { get; init; }
            public bool IsList { get; init; }
        }

        public static IReadOnlyList<OpParameter> Parameters(string op)
        {
            if (op == null || !LocalEngine.OpSurface.Methods.TryGetValue(op, out var m)) return null;
            return m.GetParameters()
                .Where(p => p.ParameterType != typeof(IEngine) && p.ParameterType != typeof(CancellationToken))
                .Select(p => new OpParameter
                {
                    Name = p.Name,
                    Required = !p.HasDefaultValue,
                    IsList = p.ParameterType.IsArray,
                })
                .ToArray();
        }

        /// <summary>True when <paramref name="from"/> names a registered enumerator, alone or as
        /// <c>name:argument</c> for one that takes a table.</summary>
        public static bool IsEnumerator(string from, out string name)
        {
            name = null;
            if (string.IsNullOrWhiteSpace(from)) return false;
            var colon = from.IndexOf(':');
            name = colon < 0 ? from : from.Substring(0, colon);
            if (!Enumerators.TryGetValue(name, out var spec)) return false;
            return colon < 0 || (spec.TakesTable && colon < from.Length - 1);
        }

        private static EnumeratorSpec E(string name, bool takesTable, string source, string lists) =>
            new EnumeratorSpec { Name = name, TakesTable = takesTable, Source = source, Lists = lists };

        private static ExecutableOp X(string op, string group, string applier = null, bool pending = false) =>
            new ExecutableOp { Op = op, Group = group, ApplierKind = applier, ApplierPending = pending };

        private static Dictionary<string, string> P(params (string Param, string Kind)[] kinds) =>
            kinds.ToDictionary(k => k.Param, k => k.Kind, StringComparer.Ordinal);
    }
}
