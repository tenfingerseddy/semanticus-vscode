using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The tool-result contract convention (docs/harness-engineering.md §1 + Appendix rule 1): "every failure
    /// teaches the recovery." A failure path must name (a) what happened, (b) the rule, and (c) the exact next
    /// step — an op name (+args) or a human action — never a bare verdict ("X not found", "invalid Y").
    ///
    /// THE TEST-ASSERTABLE BAR (the rule future gated/refusing ops are held to): the error text contains a real
    /// op name OR a decision the caller can act on — otherwise it is a defect. This suite spot-checks ~10
    /// representative failure paths across the highest-traffic op families swept in batch 1 (create / set /
    /// resolve / rename / apply_* / dry_run refusals). New gated or refusing ops SHOULD add a case here (or a
    /// sibling assertion) proving their failure text names the recovery — see <see cref="Convention_the_bar_every_refusing_op_is_held_to"/>.
    /// </summary>
    public sealed class ResultContractTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // The real MCP op surface a recovery message is allowed to point at. The assertion is "the failure text
        // names at least one of these" — the mechanical proxy for "teaches an actionable next call."
        private static readonly string[] KnownRecoveryOps =
        {
            "list_objects", "search_model", "get_object", "list_columns", "list_measures",
            "list_partitions", "list_roles", "list_calculation_groups", "get_perspectives",
            "get_model_summary", "get_properties", "get_plan", "set_plan_item", "propose_plan",
            "create_role", "create_relationship", "create_perspective", "create_calculation_group",
            "create_calculation_item", "set_compatibility_level", "bpa_scan", "load_bpa_rules",
            "set_column_data_type", "get_op_catalog", "run it directly",
            // batch-2 recovery ops (workflows / knowledge / daxlib / data-agent families)
            "list_workflows", "get_workflow", "save_workflow", "check_workflow", "open_model", "save_model",
            "list_insights", "daxlib_search", "daxlib_list_installed", "daxlib_versions",
            "list_workspaces", "list_data_agents", "list_deployment_pipelines", "get_pipeline_stages",
        };

        private static void AssertTeachesRecovery(string message)
        {
            Assert.False(string.IsNullOrWhiteSpace(message), "a failure message must exist");
            Assert.True(
                KnownRecoveryOps.Any(op => message.Contains(op, StringComparison.Ordinal)),
                $"failure text must name a recovery op / next step (contract §1) — bare verdict was: \"{message}\"");
        }

        private static async Task<LocalEngine> FreshAsync(bool pro = false, int cl = 1701)
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(pro));
            await engine.CreateModelAsync("ContractTest", cl);
            var t = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            await engine.CreateMeasureAsync(t, "Total", "SUM(Facts[Amount])", "human");
            return engine;
        }

        private static async Task<string> MessageOfAsync(Func<Task> act)
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(act);
            return ex.Message;
        }

        // 1) get_object on an unresolvable ref names the discovery ops.
        [Fact]
        public async Task Get_object_missing_ref_names_recovery()
        {
            var engine = await FreshAsync();
            AssertTeachesRecovery(await MessageOfAsync(() => engine.GetObjectAsync("measure:Facts/Nope")));
        }

        // 2) get_dax on a non-DAX object names the recovery (kinds + get_object).
        [Fact]
        public async Task Get_dax_on_non_dax_object_names_recovery()
        {
            var engine = await FreshAsync();
            AssertTeachesRecovery(await MessageOfAsync(() => engine.GetDaxAsync("column:Facts/Amount")));
        }

        // 3) create_measure against a non-table keeps its pinned "not a table" text AND names the recovery.
        [Fact]
        public async Task Create_measure_bad_table_teaches_recovery_and_keeps_pin()
        {
            var engine = await FreshAsync();
            var msg = await MessageOfAsync(() => engine.CreateMeasureAsync("table:NoSuch", "M", "1", "human"));
            Assert.Contains("not a table", msg);   // the pin DryRunTests depends on
            AssertTeachesRecovery(msg);
        }

        // 4) set_measure_format on a non-measure names list_measures.
        [Fact]
        public async Task Set_measure_format_on_non_measure_names_recovery()
        {
            var engine = await FreshAsync();
            AssertTeachesRecovery(await MessageOfAsync(() => engine.SetMeasureFormatAsync("column:Facts/Amount", "0.0", "human")));
        }

        // 5) rename of a missing/unrenameable ref names the discovery ops.
        [Fact]
        public async Task Rename_missing_ref_names_recovery()
        {
            var engine = await FreshAsync();
            AssertTeachesRecovery(await MessageOfAsync(() => engine.RenameObjectAsync("measure:Facts/Ghost", "New", "human")));
        }

        // 6) create_relationship with a non-column end names list_columns.
        [Fact]
        public async Task Create_relationship_bad_column_names_recovery()
        {
            var engine = await FreshAsync();
            AssertTeachesRecovery(await MessageOfAsync(
                () => engine.CreateRelationshipAsync("table:Facts", "column:Facts/Amount", "OneDirection", null, "human")));
        }

        // 7) set_role_member against a missing role names list_roles.
        [Fact]
        public async Task Set_role_member_missing_role_names_recovery()
        {
            var engine = await FreshAsync();
            AssertTeachesRecovery(await MessageOfAsync(() => engine.SetRoleMemberAsync("NoRole", "user@x.com", true, "human")));
        }

        // 8) create_calculation_item against a non-calc-group names the recovery.
        [Fact]
        public async Task Create_calc_item_bad_group_names_recovery()
        {
            var engine = await FreshAsync();
            AssertTeachesRecovery(await MessageOfAsync(() => engine.CreateCalculationItemAsync("table:Facts", "YTD", "1", "human")));
        }

        // 9) apply_dax_script with no @object blocks teaches the next step (script with format='dax' → get_op_catalog family).
        [Fact]
        public async Task Apply_dax_script_no_blocks_teaches_the_next_step()
        {
            var engine = await FreshAsync();
            var msg = await MessageOfAsync(() => engine.ApplyDaxScriptAsync("just some text, no headers", "human"));
            // Not a bare "nothing found": it names the authoring step (script with format='dax', then edit + apply).
            Assert.Contains("format='dax'", msg);
        }

        // 10) A dry_run refusal states the rule AND the alternative — the accountable-refusal tone.
        [Fact]
        public async Task Dry_run_refusal_names_the_alternative()
        {
            var engine = await FreshAsync();
            var save = await MessageOfAsync(() => engine.DryRunOpAsync("save_model", null));
            Assert.Contains("run it directly", save);
            var unknown = await MessageOfAsync(() => engine.DryRunOpAsync("frobnicate", null));
            Assert.Contains("get_op_catalog", unknown);   // even the unknown-op path routes to a discovery op
        }

        // ---- batch 2 (docs/harness-engineering.md §1 sweep, PR #35 follow-up): workflows / knowledge / daxlib /
        //      data-agent refusal paths. Each proves a genuine error path teaches the recovery op. ----

        // 11) get_workflow on a missing name names list_workflows (the library discovery op).
        [Fact]
        public async Task Get_workflow_missing_names_recovery()
        {
            var engine = await FreshAsync();
            AssertTeachesRecovery(await MessageOfAsync(() => engine.GetWorkflowAsync("no-such-workflow")));
        }

        // 12) save_workflow with un-parseable markdown keeps the pinned "does not parse" AND names save_workflow.
        [Fact]
        public async Task Save_workflow_unparseable_teaches_recovery_and_keeps_pin()
        {
            var engine = await FreshAsync();
            var msg = await MessageOfAsync(() => engine.SaveWorkflowAsync("my-flow", "not a workflow, no frontmatter", "human"));
            Assert.Contains("does not parse", msg);   // the pin WorkflowDesignerTests depends on
            AssertTeachesRecovery(msg);
        }

        // 13) approve_insight on an unknown id names list_insights (the live-set discovery op).
        [Fact]
        public async Task Knowledge_missing_insight_names_recovery()
        {
            var engine = await FreshAsync();
            AssertTeachesRecovery(await MessageOfAsync(() => engine.ApproveInsightAsync("does-not-exist", "human")));
        }

        // 14) daxlib_uninstall of a package not recorded in the model names daxlib_list_installed.
        [Fact]
        public async Task Daxlib_uninstall_unknown_package_names_recovery()
        {
            var engine = await FreshAsync();
            AssertTeachesRecovery(await MessageOfAsync(() => engine.DaxLibUninstallAsync("no.such.package", "human")));
        }

        // 15) daxlib_package_info with no id names daxlib_search (how to find the id) — validated before any network.
        [Fact]
        public async Task Daxlib_package_info_missing_id_names_recovery()
        {
            var engine = await FreshAsync();
            AssertTeachesRecovery(await MessageOfAsync(() => engine.DaxLibPackageInfoAsync("", null)));
        }

        // 16) data-agent reads validate ids and return a teaching DTO error (not a throw) naming the discovery ops.
        // The write half runs on a PRO engine: since 2026-07-07 every data-agent write gates at the top, so a
        // free engine gets the entitlement refusal (its own teaching message, pinned in DataAgentTests) before
        // the id validation this test pins.
        [Fact]
        public async Task Data_agent_missing_ids_teach_recovery()
        {
            var engine = await FreshAsync();
            var list = await engine.ListDataAgentsAsync("", "azcli", null);
            AssertTeachesRecovery(list.Error);                       // names list_workspaces
            var get = await engine.GetDataAgentAsync("ws", "", "azcli", null);
            AssertTeachesRecovery(get.Error);
            Assert.Contains("list_data_agents", get.Error);          // agent-id path additionally names list_data_agents
            var pro = await FreshAsync(pro: true);
            var create = await pro.CreateDataAgentAsync("", "Agent", null, false, "azcli", null, "human");
            Assert.Equal("error", create.Status);
            AssertTeachesRecovery(create.Message);                   // the DTO carries the recovery on .Message
        }

        // META: this documents the convention itself so it survives as the yardstick for future rules. It also
        // sanity-checks the helper: a bare verdict FAILS the bar, a recovery-teaching message PASSES it.
        [Fact]
        public void Convention_the_bar_every_refusing_op_is_held_to()
        {
            static bool Rejects(string m) { try { AssertTeachesRecovery(m); return false; } catch { return true; } }

            // A bare verdict is a DEFECT under the contract — the helper must reject it.
            Assert.True(Rejects("Object not found: measure:X/Y"), "a bare 'not found' must fail the bar");
            Assert.True(Rejects("invalid input"), "a bare 'invalid' must fail the bar");

            // A message that names the exact next call PASSES — cause -> rule -> the op to run.
            Assert.False(Rejects("measure:X/Y not found — run list_objects or search_model to find the exact ref."),
                "a recovery-teaching message must pass the bar");
        }
    }
}
