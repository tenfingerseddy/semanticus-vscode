using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Composer contract section 2: the per-parameter classification beside OpTaxonomy (lane L1). The table
    /// is held to the real MCP signatures, so a parameter added to an executable op fails here by name until
    /// someone decides what it is, and a parameter renamed away leaves no stale row behind.
    /// </summary>
    public sealed class OpParameterKindsTests
    {
        // Contract 2.4, the first-build allowlist, verbatim.
        private static readonly string[] ChangeGroup =
        {
            "create_import_table", "create_column", "create_relationship", "set_relationship_crossfilter",
            "set_relationship_cardinality", "set_relationship_active", "set_column_data_type", "set_column_hidden",
            "set_summarize_by", "set_data_category", "set_sort_by_column", "create_measure", "update_measure",
            "set_measure_format", "set_display_folder", "set_description", "set_synonyms", "rename_object",
            "mark_date_table", "create_calculation_group", "create_calculation_item", "create_role", "set_table_permission",
            // the generators that are ops of their own (contract 2.3)
            "generate_time_intelligence", "generate_date_table",
        };
        private static readonly string[] AfterGroup =
        {
            "refresh_partition", "save_model", "run_tests", "export_test_report", "capture_baseline",
            "deploy_stage", "deploy_live", "ai_readiness_scan", "bpa_scan",
            // Lane l9-finish (a contract gap, decided and logged): the record of a draft's checked steps, which the
            // converted starters' last step saves. Its run is the draft's own, filled by Semanticus.
            "save_evidence",
        };
        private static readonly string[] PrepareReads =
        {
            "get_source_schema", "list_columns", "get_model_graph", "impact_assessment", "benchmark_dax", "profile_dax",
        };

        // Inventory section 6.1 (composer-design/inventory.md), every op and parameter that takes authored text
        // or code, spelled as the MCP door spells it. One row is left out on purpose: contract 2.1 classes
        // set_measure_format.formatString as pick:format_templates, and the contract is the later word.
        private static readonly (string Op, string Param)[] Authored =
        {
            ("create_measure", "name"), ("create_measure", "expression"), ("create_measure", "displayFolder"),
            ("update_measure", "expression"),
            ("create_calculated_column", "name"), ("create_calculated_column", "expression"),
            ("create_calculated_table", "name"), ("create_calculated_table", "expression"),
            ("create_calculation_group", "name"), ("create_calculation_item", "name"), ("create_calculation_item", "expression"),
            ("set_calc_item_format_string", "formatExpression"), ("set_measure_format_expression", "formatExpression"),
            ("create_function", "name"), ("create_function", "expression"),
            ("set_table_permission", "filterDax"),
            ("create_field_parameter", "name"), ("create_field_parameter", "items"),
            ("create_named_expression", "expression"), ("update_named_expression", "expression"),
            ("set_partition_m", "mExpression"), ("create_import_table", "mExpression"),
            ("set_incremental_refresh_policy", "pollingExpression"),
            ("create_table", "name"), ("create_column", "name"), ("create_hierarchy", "name"), ("create_perspective", "name"),
            ("create_role", "name"), ("create_data_source", "name"), ("create_directlake_table", "name"), ("duplicate_object", "newName"),
            ("rename_object", "newName"),
            ("set_display_folder", "folder"), ("rename_display_folder", "fromPath"), ("rename_display_folder", "toPath"),
            ("set_description", "text"), ("set_synonyms", "terms"), ("set_ai_instructions", "instructions"),
            ("create_data_agent", "aiInstructions"), ("update_data_agent", "aiInstructions"),
            ("update_data_agent", "datasourceJson"), ("update_data_agent", "fewshotsJson"),
            ("publish_data_agent", "description"),
            ("define_calendar", "name"), ("define_calendar", "description"),
            ("generate_date_table", "startExpr"), ("generate_date_table", "endExpr"),
            ("define_calendar_from_template", "startExpr"), ("define_calendar_from_template", "endExpr"),
            ("set_property", "value"), ("set_properties", "value"),
            ("replace_in_object", "find"), ("replace_in_object", "replace"), ("propose_replace", "find"), ("propose_replace", "replace"),
            ("apply_dax_script", "script"), ("apply_tmdl", "script"),
            ("set_plan_item", "after"), ("add_plan_item", "after"), ("add_plan_item", "title"),
            ("set_model_primer", "markdown"),
            ("add_insight", "text"), ("add_insight", "keys"), ("edit_insight", "text"), ("edit_insight", "keys"),
            ("set_doc_section", "markdown"), ("set_spec", "specJson"),
            ("add_interview_question", "question"), ("add_interview_question", "query"), ("add_interview_question", "scalarExpr"),
            ("add_interview_question", "paraphraseExpr"), ("add_interview_question", "filters"), ("add_interview_question", "expectedValue"),
            ("run_interview", "attemptDax"),
            ("try_test", "def"), ("save_test", "def"),
            ("save_workflow", "markdown"), ("save_workflow_template", "markdown"), ("edit_workflow_document", "exactText"),
            ("preview_workflow_edit", "draftText"),
            ("submit_workflow_step", "answers"),
            ("skip_workflow_step", "reason"), ("abort_workflow", "reason"), ("waive_finding", "reason"),
            ("skip_model_plan_item", "reason"), ("apply_plan", "overrideReason"), ("deploy_stage", "note"),
            ("git_commit", "message"), ("fabric_git_commit", "comment"), ("create_history_checkpoint", "label"),
            ("validate_dax", "expression"), ("lint_dax", "expression"), ("verify_dax_equivalence", "exprA"),
            ("verify_dax_equivalence", "exprB"), ("probe_measure", "expr"), ("run_dax", "query"), ("pivot_measure", "measureExpr"),
            ("benchmark_dax", "query"), ("optimize_measure", "candidates"), ("reconcile_measure", "sql"),
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        [Fact]
        public void The_allowlist_is_the_contracts_first_build_list_exactly()
        {
            Assert.Equal(ChangeGroup.OrderBy(x => x), OpParameterKinds.Executable.Values.Where(e => e.Group == OpParameterKinds.Change).Select(e => e.Op).OrderBy(x => x));
            Assert.Equal(AfterGroup.OrderBy(x => x), OpParameterKinds.Executable.Values.Where(e => e.Group == OpParameterKinds.After).Select(e => e.Op).OrderBy(x => x));
            Assert.Equal(PrepareReads.OrderBy(x => x), OpParameterKinds.Executable.Values.Where(e => e.Group == OpParameterKinds.Prepare).Select(e => e.Op).OrderBy(x => x));
            Assert.All(OpParameterKinds.Executable, kv => Assert.Equal(kv.Key, kv.Value.Op));
        }

        [Fact]
        public void Every_executable_op_is_a_real_op_with_a_ratified_home()
        {
            var surface = ProFeatureMapTests.CompiledOperationNames();
            var missing = OpParameterKinds.Executable.Keys.Where(op => !surface.Contains(op)).ToArray();
            Assert.True(missing.Length == 0, "Not on the MCP surface: " + string.Join(", ", missing));
            var unfiled = OpParameterKinds.Executable.Keys.Where(op => !OpTaxonomy.TryGet(op, out _, out _)).ToArray();
            Assert.True(unfiled.Length == 0, "Not in OpTaxonomy: " + string.Join(", ", unfiled));
        }

        [Fact]
        public void Every_parameter_of_every_executable_op_has_a_kind()
        {
            var unclassed = OpParameterKinds.Executable.Keys
                .SelectMany(op => OpParameterKinds.Parameters(op).Select(p => (op, p.Name)))
                .Where(x => OpParameterKinds.KindOf(x.op, x.Name) == null)
                .Select(x => x.op + "." + x.Name)
                .ToArray();
            Assert.True(unclassed.Length == 0, "Parameters with no kind: " + string.Join(", ", unclassed));
        }

        [Fact]
        public void Every_classified_parameter_exists_on_its_op()
        {
            var stale = OpParameterKinds.All
                .SelectMany(kv =>
                {
                    var real = OpParameterKinds.Parameters(kv.Key);
                    if (real == null) return new[] { kv.Key + " (no such op)" };
                    return kv.Value.Keys.Where(p => !real.Any(r => r.Name == p)).Select(p => kv.Key + "." + p).ToArray();
                })
                .ToArray();
            Assert.True(stale.Length == 0, "Classified but not on the op: " + string.Join(", ", stale));
        }

        [Fact]
        public void Every_pair_from_inventory_6_1_is_authored()
        {
            var wrong = Authored.Where(a => OpParameterKinds.AuthoredKind(a.Op, a.Param) == null)
                .Select(a => $"{a.Op}.{a.Param} is {OpParameterKinds.KindOf(a.Op, a.Param) ?? "unclassed"}")
                .ToArray();
            Assert.True(wrong.Length == 0, "Inventory 6.1 says these take authored text: " + string.Join(", ", wrong));
            Assert.Equal("pick:format_templates", OpParameterKinds.KindOf("set_measure_format", "formatString"));
        }

        [Fact]
        public void Every_kind_is_well_formed_and_an_executable_ops_authored_kind_is_a_blank_kind()
        {
            var bad = OpParameterKinds.All
                .SelectMany(kv => kv.Value.Select(p => (Op: kv.Key, Param: p.Key, Kind: p.Value)))
                .Where(x =>
                {
                    var k = x.Kind ?? "";
                    if (k == "flag" || k == "number" || k == "internal") return false;
                    if (k.StartsWith("enum:", StringComparison.Ordinal))
                        return k.Length == 5 || k.Substring(5).Split('|').Any(string.IsNullOrWhiteSpace);
                    if (k.StartsWith("pick:", StringComparison.Ordinal))
                        return !OpParameterKinds.Enumerators.ContainsKey(k.Substring(5));
                    if (k.StartsWith("authored:", StringComparison.Ordinal))
                    {
                        var blank = k.Substring(9);
                        return OpParameterKinds.IsExecutable(x.Op)
                            ? !OpParameterKinds.BlankKinds.Contains(blank)
                            : !OpParameterKinds.BlankKinds.Contains(blank) && !OpParameterKinds.OtherAuthoredKinds.Contains(blank);
                    }
                    return true;
                })
                .Select(x => $"{x.Op}.{x.Param} = {x.Kind}")
                .ToArray();
            Assert.True(bad.Length == 0, "Malformed kinds: " + string.Join(", ", bad));
            Assert.Equal(new[] { "dax", "description", "m", "name", "choice" }, OpParameterKinds.BlankKinds);
        }

        [Fact]
        public void The_contracts_enumerators_are_registered()
        {
            foreach (var name in new[] { "tables", "columns", "number_columns", "measures", "format_templates", "source_tables", "source_columns", "stages", "roles", "calendars" })
                Assert.True(OpParameterKinds.Enumerators.ContainsKey(name), name);
            Assert.True(OpParameterKinds.IsEnumerator("columns:{{inputs.table}}", out var n));
            Assert.Equal("columns", n);
            Assert.False(OpParameterKinds.IsEnumerator("measures:Sales", out _));   // measures takes no table
            Assert.False(OpParameterKinds.IsEnumerator("rows", out _));
        }

        [Fact]
        public void Every_generator_writes_authored_parameters_of_an_executable_op()
        {
            Assert.Equal(new[] { "date_table", "starter_measure", "time_intelligence" }, OpParameterKinds.Generators.Keys.OrderBy(x => x));
            foreach (var g in OpParameterKinds.Generators.Values)
            {
                Assert.True(OpParameterKinds.IsExecutable(g.Op), g.Id);
                Assert.All(g.Writes, p => Assert.NotNull(OpParameterKinds.AuthoredKind(g.Op, p)));
            }
        }

        [Fact]
        public void Every_change_op_has_an_applier_kind_checked_against_ApplyOneItem()
        {
            // A Windows checkout has CRLF endings (.gitattributes marks *.cs as text, and the runner converts), so the
            // end marker below never matched there and the slice length went negative. Read the source as LF.
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.Engine", "LocalEngine.cs")).Replace("\r\n", "\n");
            var start = src.IndexOf("internal void ApplyOneItem(Model m, ChangeItem it, Session s, IJevModelFacts facts = null)", StringComparison.Ordinal);
            Assert.True(start > 0, "ApplyOneItem moved; point this test at it.");
            var end = src.IndexOf("\n        }\n", start, StringComparison.Ordinal);
            Assert.True(end > start, "The end of ApplyOneItem was not found; point this test at it.");
            var kinds = Regex.Matches(src.Substring(start, end - start), "case \"([a-z_]+)\":").Select(m => m.Groups[1].Value).ToHashSet();

            foreach (var e in OpParameterKinds.Executable.Values)
            {
                if (e.Group != OpParameterKinds.Change)
                {
                    Assert.True(e.ApplierKind == null, $"{e.Op} runs outside the change and has no applier");
                    continue;
                }
                Assert.False(string.IsNullOrWhiteSpace(e.ApplierKind), $"{e.Op} has no applier kind name");
                // A pending kind that ApplyOneItem now has means lane L3 landed it: clear the flag.
                Assert.True(e.ApplierPending != kinds.Contains(e.ApplierKind),
                    e.ApplierPending ? $"{e.Op}: ApplyOneItem now has '{e.ApplierKind}', so it is no longer pending"
                                     : $"{e.Op}: ApplyOneItem has no case '{e.ApplierKind}'");
            }
        }

        [Fact]
        public void The_allowlist_and_the_composers_applier_list_name_the_same_kind_for_every_change_op()
        {
            // Lane L3's ComposedOps.Kinds is what a composed step compiles to; this allowlist is what check_workflow
            // admits. Two lists that disagree would admit a step the composer then cannot apply, or the reverse.
            foreach (var e in OpParameterKinds.Executable.Values.Where(x => x.Group == OpParameterKinds.Change))
            {
                Assert.True(Semanticus.Engine.Composer.ComposedOps.Kinds.TryGetValue(e.Op, out var kind), $"{e.Op} is not in ComposedOps.Kinds");
                Assert.Equal(e.ApplierKind, kind);
            }
            foreach (var op in Semanticus.Engine.Composer.ComposedOps.Kinds.Keys.Where(k => k != "starter_measure"))
                Assert.True(OpParameterKinds.IsExecutable(op), $"ComposedOps names {op}, which the allowlist does not admit");
        }

        [Fact]
        public void An_after_op_derives_the_after_phase_and_a_change_op_the_change_phase()
        {
            Assert.Equal(OpParameterKinds.After, OpParameterKinds.GroupOf("save_model"));
            Assert.Equal(OpParameterKinds.Change, OpParameterKinds.GroupOf("create_measure"));
            Assert.Equal(OpParameterKinds.Prepare, OpParameterKinds.GroupOf("get_source_schema"));
            Assert.Null(OpParameterKinds.GroupOf("apply_tmdl"));
        }
    }
}
