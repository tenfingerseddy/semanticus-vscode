using System;
using System.Collections.Generic;

namespace Semanticus.Engine
{
    /// <summary>
    /// The op → capability map. **ADVISORY ONLY.** Enforcement does NOT route through this map — the guard is invoked
    /// at each live-write call site (deploy_live / apply_model_diff→workspace / rollback_push) with an EXPLICIT
    /// capability, so an op missing from this map cannot silently become an ungated action at the gate. This map exists
    /// so the agent (via get_agent_policy) can see what an op WOULD need, and for the permissions UI to label ops. A
    /// new live-touching op must be wired to <c>GuardAgent</c> explicitly with its capability — the map is not a
    /// substitute for that, and its "unknown ⇒ Read" default is an advisory default, not an authorisation decision.
    ///
    /// QueryData enforcement state (keep honest): preview_table IS wired (session-grant semantics — a granted approval
    /// covers reads from that target until it expires, never one-shot). Row-returning run_dax is wired too (#129): it
    /// still maps to <see cref="AgentCapability.QueryCalc"/> here (a scalar evaluation is a calculation allowed
    /// everywhere), but the call site classifies the query shape (<see cref="DaxQueryClassifier"/>) and routes a
    /// row-returning `EVALUATE &lt;table&gt;` through the SAME GuardAgent(QueryData, …, consumeGrant:false) call,
    /// sharing preview_table's grant (intentBasis "querydata"); a genuinely scalar `EVALUATE ROW(...)` /
    /// `EVALUATE { … }` stays ungated. pivot_measure AND evaluate_and_log are row-returners BY CONSTRUCTION so they
    /// map to QueryData outright — pivot_measure emits SUMMARIZECOLUMNS rows, and evaluate_and_log's whole purpose is
    /// the EVALUATEANDLOG log channel, which carries capped row samples of an arbitrary table regardless of the outer
    /// query shape (a per-event cap doesn't bound multi-call pagination), so shape classification is the wrong tool
    /// for it. run_interview threads its origin to the caller-supplied DAX it executes (attempts / inline queries)
    /// through the same run_dax gate; its own constructed oracle probes are engine-authored and stay ungated.
    /// reconcile_measure (the Tests-tab reconciler runner) IS wired: it returns source-row aggregates by group, so it
    /// calls GuardAgent(QueryData, …, consumeGrant:false, intentBasis "querydata") before executing — on BOTH targets
    /// it reads, the XMLA model AND the source SQL endpoint (server+database) — the same target-scoped session grant as
    /// preview_table (it runs the read-only ground-truth SQL internally via FabricSqlQuery, so there is no standalone
    /// run_sql tool to gate separately yet). run_dmv returns
    /// metadata/statistics, not source rows — Read. ADJUDICATED not-gated (bounded
    /// diagnostic context of calculation ops, same class as the accepted scalar-aggregation limitation — any measure
    /// can serialize data through QueryCalc): verify_dax_equivalence Mismatch context (&lt;=50 members) and
    /// explain_value decomposeBy contributor labels.
    ///
    /// deploy_stage keeps its own, STAGE-aware gate (prod stage ⇒ agent hard-refusal + human confirm token) — the
    /// pipeline's stage metadata is a better prod signal than an unlabelled registry record, and re-gating non-prod
    /// promotions here would regress the documented "agent may deploy to non-production stages" workflow.
    /// </summary>
    public static class CapabilityMap
    {
        private static readonly Dictionary<string, AgentCapability> Map = new(StringComparer.OrdinalIgnoreCase)
        {
            // --- the live-touching, label-gated ops (the whole point) ---
            ["deploy_live"] = AgentCapability.DeployLive,
            ["apply_model_diff"] = AgentCapability.DeployLive,   // when the target is an endpoint; a file target is re-classified at the call site
            ["deploy_stage"] = AgentCapability.DeployLive,
            ["cicd_publish"] = AgentCapability.DeployLive,
            ["publish_data_agent"] = AgentCapability.DeployLive,
            ["fabric_git_commit"] = AgentCapability.DeployLive,
            ["fabric_git_update"] = AgentCapability.DeployLive,

            ["rollback_push"] = AgentCapability.Rollback,
            ["refresh_partition"] = AgentCapability.Refresh,

            // --- data exfiltration surface: previewing ROWS of source/live data ---
            ["preview_table"] = AgentCapability.QueryData,
            ["pivot_measure"] = AgentCapability.QueryData,      // SUMMARIZECOLUMNS rows by construction — always a data read
            ["evaluate_and_log"] = AgentCapability.QueryData,   // the EVALUATEANDLOG log channel samples arbitrary table rows regardless of the outer query shape
            ["reconcile_measure"] = AgentCapability.QueryData,  // Tests-tab reconciler runner — returns source-row aggregates by group (wired at the call site, consumeGrant:false)
            ["run_sql"] = AgentCapability.QueryData,            // planned standalone op — forward-declared (the reconciler runs SQL internally via FabricSqlQuery)
            ["get_source_schema"] = AgentCapability.Read,        // column names/types over TDS — metadata, not rows
            ["run_dmv"] = AgentCapability.Read,                  // DMV metadata/statistics, not source rows

            // --- a calculation (scalar/measure) is explicitly NOT QueryData: allowed everywhere ---
            ["run_dax"] = AgentCapability.QueryCalc,       // note: run_dax can return rows; the call site re-classifies a row query
            ["benchmark_dax"] = AgentCapability.QueryCalc,
            ["probe_measure"] = AgentCapability.QueryCalc,

            // --- governance: never the agent's ---
            ["set_agent_policy"] = AgentCapability.Governance,
            ["label_connection"] = AgentCapability.Governance,
            ["approve_agent_action"] = AgentCapability.Governance,

            // --- writing a model to disk ---
            ["save_model"] = AgentCapability.DeployFile,

            // --- local model edits: the generic property-setter pair, named explicitly so the advisory surface
            // (get_agent_policy / the permissions UI) labels them without leaning on the isKnownMutation fallback ---
            ["set_property"] = AgentCapability.EditLocal,
            ["set_properties"] = AgentCapability.EditLocal,
        };

        /// <summary>The capability an op maps to. An unknown op is <see cref="AgentCapability.Read"/> ONLY if it is not
        /// a mutation the engine already knows about; the caller passes <paramref name="isKnownMutation"/> from the
        /// existing dry-run/mutation classification so an unlisted mutating op floors at EditLocal, never Read.</summary>
        public static AgentCapability For(string op, bool isKnownMutation = false)
        {
            if (op != null && Map.TryGetValue(op, out var cap)) return cap;
            return isKnownMutation ? AgentCapability.EditLocal : AgentCapability.Read;
        }

        public static bool IsGated(AgentCapability cap) =>
            cap != AgentCapability.Read && cap != AgentCapability.QueryCalc;
    }
}
