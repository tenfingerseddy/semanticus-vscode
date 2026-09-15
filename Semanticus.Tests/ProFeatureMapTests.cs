using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The map has no third state. Every name either belongs to one of the four Pro features or is on the
    /// explicit free list, on BOTH doors and on the IEngine methods the gate keys on. This is the guard that stops a
    /// future operation landing ungated: add a tool or an RPC entry and this test names it until someone decides.</summary>
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

        /// <summary>The count Kane's matrix settled: 142 Pro operations, 69 / 20 / 23 / 30. A change to any of these
        /// is a change to what is sold, so it fails here before it ships.</summary>
        [Fact]
        public void The_Pro_surface_is_142_operations_in_four_features()
        {
            Assert.Equal(69, FeatureMap.OperationsFor(ProFeature.ModelCreate).Count);
            Assert.Equal(20, FeatureMap.OperationsFor(ProFeature.Tests).Count);
            Assert.Equal(23, FeatureMap.OperationsFor(ProFeature.PublishedAdvanced).Count);
            Assert.Equal(30, FeatureMap.OperationsFor(ProFeature.Workflows).Count);
            Assert.Equal(142, FeatureMap.ProOperations.Count);
            Assert.Equal(142, FeatureMap.ProRpcMethods.Count);
            Assert.Equal(142, FeatureMap.ProEngineMethods.Count);
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
        [InlineData("verify_dax_equivalence")] [InlineData("deploy_gate")] [InlineData("bpa_fix_all")]
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

        /// <summary>The five RPC entries with no MCP twin. None belongs to a Pro feature, and each is listed free by
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

        /// <summary>Astra's example of why the gate cannot key on the wire name: one implementation, three names.</summary>
        [Fact]
        public void A_differently_named_RPC_entry_still_resolves_to_its_feature()
        {
            Assert.True(FeatureMap.TryOperation("list_table_mappings", out var a, out _));
            Assert.True(FeatureMap.TryRpcMethod("listTableMappings", out var b, out _));
            Assert.True(FeatureMap.TryEngineMethod("ListTableSourceMappingsAsync", out var c, out _));
            Assert.Equal(ProFeature.Tests, a);
            Assert.Equal(ProFeature.Tests, b);
            Assert.Equal(ProFeature.Tests, c);
        }

        /// <summary>The tab a person would open, not the enum name.</summary>
        [Theory]
        [InlineData("get_partition_m", "Power Query")]
        [InlineData("fabric_git_status", "Fabric Git")]
        [InlineData("git_push", "Source control")]
        [InlineData("create_data_agent", "Data agent")]
        [InlineData("run_tests", "Tests")]
        [InlineData("save_evidence", "Saved reports")]
        [InlineData("get_spec", "Model Spec")]
        [InlineData("list_calendars", "Advanced Modelling")]
        [InlineData("get_doc_outline", "Docs")]
        [InlineData("list_insights", "Model notes")]
        [InlineData("start_workflow", "Workflows")]
        [InlineData("cicd_publish", "CI and CD publish")]
        public void Each_operation_names_the_tab_a_person_would_open(string operation, string tab)
        {
            Assert.True(FeatureMap.TryOperation(operation, out _, out var actual));
            Assert.Equal(tab, actual);
        }
    }
}
