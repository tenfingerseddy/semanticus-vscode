using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The map has no third state. Every name either belongs to a Pro feature or is on the explicit free
    /// list, on BOTH doors and on the IEngine methods the gate keys on. This is the guard that stops a future
    /// operation landing ungated: add a tool or an RPC entry and this test names it until someone decides.</summary>
    public sealed class ProFeatureMapTests
    {
        internal static string[] CompiledOperationNames() =>
            typeof(McpTools).Assembly.GetTypes()
                .Where(t => t.IsAbstract && t.IsSealed)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Select(m => m.GetCustomAttributes(true).FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute"))
                .Where(a => a != null)
                .Select(a => (string)a.GetType().GetProperty("Name")?.GetValue(a))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

        internal static MethodInfo[] RpcEntries() =>
            typeof(EngineRpcTarget).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToArray();

        internal static MethodInfo[] EngineMethods() =>
            typeof(IEngine).GetMethods()
                .Where(m => !m.IsSpecialName)
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToArray();

        [Fact]
        public void Every_MCP_operation_is_classified_exactly_once()
        {
            var unclassified = CompiledOperationNames().Where(n => !FeatureMap.IsOperationClassified(n)).ToArray();
            Assert.True(unclassified.Length == 0,
                "These MCP operations are neither mapped to a Pro feature nor on the explicit free list: "
                + string.Join(", ", unclassified));
        }

        [Fact]
        public void Every_RPC_entry_is_classified_exactly_once()
        {
            var unclassified = RpcEntries().Select(m => m.Name).Distinct(StringComparer.Ordinal)
                .Where(n => !FeatureMap.IsRpcMethodClassified(n)).ToArray();
            Assert.True(unclassified.Length == 0,
                "These RPC entries are neither mapped to a Pro feature nor on the explicit free list: "
                + string.Join(", ", unclassified));
        }

        [Fact]
        public void Every_IEngine_method_is_classified_exactly_once()
        {
            var unclassified = EngineMethods().Select(m => m.Name).Distinct(StringComparer.Ordinal)
                .Where(n => !FeatureMap.IsEngineMethodClassified(n)).ToArray();
            Assert.True(unclassified.Length == 0,
                "These IEngine methods are neither mapped to a Pro feature nor on the explicit free list: "
                + string.Join(", ", unclassified));
        }

        [Fact]
        public void The_map_names_nothing_that_is_not_on_a_door()
        {
            var ops = new HashSet<string>(CompiledOperationNames(), StringComparer.Ordinal);
            var rpc = new HashSet<string>(RpcEntries().Select(m => m.Name), StringComparer.Ordinal);
            var eng = new HashSet<string>(EngineMethods().Select(m => m.Name), StringComparer.Ordinal);

            Assert.Empty(FeatureMap.ProOperations.Concat(FeatureMap.FreeOperations).Where(n => !ops.Contains(n)));
            Assert.Empty(FeatureMap.ProRpcMethods.Concat(FeatureMap.FreeRpcMethods).Where(n => !rpc.Contains(n)));
            Assert.Empty(FeatureMap.ProEngineMethods.Concat(FeatureMap.FreeEngineMethods).Where(n => !eng.Contains(n)));
        }

        /// <summary>THE COUNT CHANGED ON PURPOSE. Kane, 22 September 2026: "free is the tool, Pro is the tool
        /// that works for you." Everything a person can do by hand, or with their own assistant driving the agent
        /// door, is free; what is paid is the product deciding and doing.
        ///
        /// So eight tabs left the paid line in one decision: Advanced modelling, Validation, Reports, Model Spec,
        /// Docs, Model notes, Power Query and the advanced half of Published. That is 112 operations, and it took
        /// the modelCreate, tests and publishedAdvanced features with it rather than leaving three empty ones
        /// behind. 143 becomes 31: Workflows (30) and the ask box (1).
        ///
        /// The history, so the next person can see the shape of the argument rather than just the number: 142 in
        /// Kane's matrix as 69 / 20 / 23 / 30, then 143 when route_ask became Pro on 22 September, then 31 later
        /// the same day when the free line was reset. A change to any of these is a change to what is sold, so it
        /// fails here before it ships.</summary>
        [Fact]
        public void The_Pro_surface_is_56_operations_in_four_features()
        {
            // 35 on 24 September: the composer's four draft ops joined Workflows (lane L4; composer contract 5.1 and
            // Plan-heart contract 14, "draft ops stay Pro", ratified by Kane). The person's Approve and run on a held
            // draft is one more engine method with no op and no EngineRpcTarget entry: it is on the human-only target.
            // 36 on 25 September: describe_workflow_steps joined Workflows (Kane's UAT: describing a workflow on the
            // Workflows page is authoring, and opens Author with the steps). It is part of New workflow, which was
            // already Pro as a whole, so nothing that was free became paid.
            // 39 on 10 Oct 2026 (lane agent-controls): approve_draft, continue_draft, add_plan_repeat and set_plan_repeat became MCP tools, still Pro.
            // 38 on 12 Oct 2026: abort_workflow became free, so a run active when a trial or Pro ended can be stopped.
            Assert.Equal(38, FeatureMap.OperationsFor(ProFeature.Workflows).Count);
            Assert.Equal(1, FeatureMap.OperationsFor(ProFeature.Ask).Count);
            // 38 on 26 September: ask_data and list_data_questions, Ask your data (Kane: "Anything involving Jev is always
            // Pro only"). Both are new operations, so nothing that was free became paid.
            // 39 on 28 September: pick_chart_kind (D6, Jev's chart pick for a Queries result) joined Ask your data. A new
            // operation, so nothing that was free became paid.
            Assert.Equal(new[] { "ask_data", "list_data_questions", "pick_chart_kind" }, FeatureMap.OperationsFor(ProFeature.AskData).ToArray());
            Assert.Equal(new[] { "add_plan_items", "apply_fixes", "create_measures", "duplicate_objects", "edit_objects", "edit_start_plans", "plan_rows", "set_doc_sections", "set_object_permissions", "set_perspective_members", "set_plan_items", "unwaive_findings", "waive_findings", "write_words" }, FeatureMap.OperationsFor(ProFeature.Batch).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.Equal(56, FeatureMap.ProOperations.Count);
            Assert.Equal(56, FeatureMap.ProRpcMethods.Count);
            // 40, not 36: four Pro engine methods are person-only, with no MCP operation and no EngineRpcTarget entry.
            // Approve and run on a held draft (lane L4), Continue past a stop in a draft (lane author-gaps, B9) and the
            // repeat cards' add and change (lane RP, contract 1.7, Kane 24 Sep). What is sold on the two doors is 36
            // names; the gate keys on these 40. 42 with Ask your data's two, 43 with pick_chart_kind.
            Assert.Equal(56, FeatureMap.ProEngineMethods.Count);
            Assert.Equal(new[] { "route_ask" }, FeatureMap.OperationsFor(ProFeature.Ask).ToArray());
        }

        /// <summary>The eight tabs that became free, asserted one operation at a time rather than trusting the
        /// count above. Each of these refused a free session the day before and does not now.</summary>
        [Theory]
        [InlineData("get_spec")] [InlineData("build_model_from_spec")]            // Model Spec
        [InlineData("list_calendars")] [InlineData("create_role")]                // Advanced modelling
        [InlineData("get_partition_m")] [InlineData("set_partition_m")]           // Power Query
        [InlineData("get_doc_outline")] [InlineData("set_doc_section")]           // Docs
        [InlineData("list_insights")] [InlineData("get_model_primer")]            // Model notes
        [InlineData("run_tests")] [InlineData("save_test")] [InlineData("list_tests")]  // Validation
        [InlineData("save_evidence")] [InlineData("list_evidence")]               // Reports
        [InlineData("git_push")] [InlineData("fabric_git_status")]                // Published, advanced
        [InlineData("cicd_publish")] [InlineData("create_data_agent")]            // Published, advanced
        public void The_eight_tabs_Kane_freed_are_free_on_the_map(string operation)
        {
            Assert.False(FeatureMap.TryOperation(operation, out _, out var tab),
                operation + " is still Pro, under the tab " + tab + ".");
            Assert.Contains(operation, FeatureMap.FreeOperations);
        }

        /// <summary>What stays paid, named rather than counted. Workflows as a whole, and the ask box.</summary>
        [Theory]
        [InlineData("start_workflow", ProFeature.Workflows, "Workflows")]
        [InlineData("list_workflows", ProFeature.Workflows, "Workflows")]
        [InlineData("route_ask", ProFeature.Ask, "Ask about this model")]
        public void What_stays_Pro_stays_Pro(string operation, ProFeature feature, string tab)
        {
            Assert.True(FeatureMap.TryOperation(operation, out var actual, out var actualTab));
            Assert.Equal(feature, actual);
            Assert.Equal(tab, actualTab);
        }

        /// <summary>The shared free reads named in the brief, verbatim. Gating any of these locks a free surface
        /// shut, so each is asserted by name rather than left to the free list's bulk.</summary>
        [Theory]
        // "compare_models" in the brief is the RPC name; the MCP door calls the same implementation "model_diff".
        // Both are asserted, one here and one in the RPC theory below.
        [InlineData("git_status")] [InlineData("model_diff")] [InlineData("list_workspaces")]
        [InlineData("get_workflow_run")] [InlineData("get_entitlement")] [InlineData("get_op_catalog")]
        [InlineData("harness_report")] [InlineData("list_primer_suggestions")] [InlineData("accept_primer_suggestion")]
        [InlineData("reject_primer_suggestion")] [InlineData("set_compatibility_level")] [InlineData("get_ai_instructions")]
        [InlineData("set_ai_instructions")] [InlineData("check_reports")] [InlineData("set_report_scope")]
        [InlineData("list_report_scope")] [InlineData("analyze_reports")] [InlineData("analyze_cloud_reports")]
        [InlineData("list_reports")] [InlineData("list_pending_approvals")] [InlineData("get_agent_policy")]
        [InlineData("get_model_fingerprint")] [InlineData("list_sql_source_usage")] [InlineData("probe_measure")]
        [InlineData("verify_dax_equivalence")] [InlineData("deploy_gate")]
        [InlineData("cancel_query")] [InlineData("benchmark_cold_warm")]
        [InlineData("label_connection")] [InlineData("set_default_account_profile")]
        [InlineData("apply_safe_fixes")] [InlineData("make_model_ai_ready")] [InlineData("apply_plan")]
        [InlineData("set_verified_mode")] [InlineData("export_verified_edits")] [InlineData("add_interview_question")]
        [InlineData("blame_value")] [InlineData("list_value_history")] [InlineData("waive_finding")]
        [InlineData("optimize_measure")] [InlineData("cherry_pick")] [InlineData("apply_tmdl")]
        [InlineData("apply_dax_script")] [InlineData("apply_model_diff")] [InlineData("remove_safe_objects")]
        public void Named_free_operations_stay_free(string operation)
        {
            Assert.True(FeatureMap.FreeOperations.Contains(operation),
                operation + " must stay free, and the map says it is Pro.");
        }

        /// <summary>The named plumbing and settings RPC entries. Each remains free with or without an MCP twin, and is listed by
        /// name so the completeness walk over the RPC door can pass honestly rather than by omission.</summary>
        [Theory]
        [InlineData("cancelDax")] [InlineData("clearReferenceBinding")] [InlineData("pullAgentHealth")]
        [InlineData("setConnectionWorkingFolder")] [InlineData("setDefaultAccountProfile")]
        [InlineData("compareModels")] [InlineData("gitStatus")]
        public void The_RPC_only_entries_are_explicitly_free(string rpcMethod)
        {
            Assert.Contains(rpcMethod, FeatureMap.FreeRpcMethods);
            Assert.True(FeatureMap.IsRpcMethodClassified(rpcMethod));
        }

        /// <summary>Astra's example of why the gate cannot key on the wire name: one implementation, three names.
        /// The example used to be list_table_mappings, which is free now, so it is made on a Pro one that still
        /// has three different names for one method.</summary>
        [Fact]
        public void A_differently_named_RPC_entry_still_resolves_to_its_feature()
        {
            Assert.True(FeatureMap.TryOperation("route_ask", out var a, out _));
            Assert.True(FeatureMap.TryRpcMethod("routeAsk", out var b, out _));
            Assert.True(FeatureMap.TryEngineMethod("RouteAskAsync", out var c, out _));
            Assert.Equal(ProFeature.Ask, a);
            Assert.Equal(ProFeature.Ask, b);
            Assert.Equal(ProFeature.Ask, c);

            // The free side of the same point: one implementation, three names, all three classified free.
            Assert.True(FeatureMap.IsOperationClassified("list_table_mappings"));
            Assert.True(FeatureMap.IsRpcMethodClassified("listTableMappings"));
            Assert.True(FeatureMap.IsEngineMethodClassified("ListTableSourceMappingsAsync"));
            Assert.False(FeatureMap.TryEngineMethod("ListTableSourceMappingsAsync", out _, out _));
        }

        /// <summary>The tab a person would open, not the enum name. Ten of the twelve rows this test used to
        /// carry named tabs that are free now, so the list is the two that are left.</summary>
        [Theory]
        [InlineData("start_workflow", "Workflows")]
        [InlineData("route_ask", "Ask about this model")]
        public void Each_operation_names_the_tab_a_person_would_open(string operation, string tab)
        {
            Assert.True(FeatureMap.TryOperation(operation, out _, out var actual));
            Assert.Equal(tab, actual);
        }
    }
}
