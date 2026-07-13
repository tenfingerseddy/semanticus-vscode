using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// The universal dry-run contract (docs/harness-engineering.md §4 — "plan mode for models"): rehearse ANY
    /// single model-mutating op through its REAL code path and return the exact would-be delta set + the op's own
    /// result, with a hard guarantee of no mutation / no undo entry / no broadcast / no audit record. The rehearsal
    /// itself lives at the <see cref="Session.MutateAsync"/> chokepoint (the vendored rollback path, driven by the
    /// ambient <see cref="DryRunScope"/>); this partial is the wrapper op that resolves an op name to its McpTools
    /// method, refuses the ops a rolled-back rehearsal would mislead on, binds the JSON args, and invokes under the
    /// scope. Ops that do external/system I/O, span multiple dependent mutations, touch the undo timeline, or write
    /// sidecar/bookkeeping state are DENIED with a teaching refusal that names the right preview path.
    /// </summary>
    public sealed partial class LocalEngine
    {
        // --- deny-list (the ops dry_run refuses, each family with the reason its refusal teaches) --------------
        // A rolled-back rehearsal of these would be misleading, not informative: I/O escapes the rollback,
        // composites diverge because later steps read earlier persisted writes, the undo timeline is already
        // reversible, and bookkeeping writes are sidecar/annotation state a model rollback doesn't cover.

        private const string IoReason =
            "it performs external/system I/O, which dry_run cannot rehearse — run it directly, or use its own preview path";
        private const string CloudReason =
            "it already defaults to dry-run internally (it will not write without a commit/consent flag) — run it directly";
        private const string TimelineReason =
            "dry_run cannot rehearse the undo timeline — undo is already reversible, so just run undo_change / redo_change";
        private const string CompositeReason =
            "its later steps read earlier writes, so a rolled-back rehearsal would diverge — use propose_plan / the op's own preview path / apply one typed op at a time";
        private const string BookkeepingReason =
            "it writes bookkeeping state (workflow / knowledge / waiver), not a model-definition edit — dry_run rehearses model edits and does not apply here";

        // Exact op-name → reason. Flattened from the families below so lookup is O(1) and every message is uniform.
        private static readonly Dictionary<string, string> DenyExact = BuildDenyExact();

        // Prefix families (a startsWith match). data-agent ops are matched by the "data_agent" SUBSTRING (their
        // names are list_/get_/create_/… _data_agent) rather than a prefix — handled in GuardDryRunnable.
        private static readonly (string prefix, string reason)[] DenyPrefixes =
        {
            ("benchmark_", IoReason),
            ("deploy", CloudReason),   // deploy_live / deploy_gate / deploy_stage / deployment_history
            ("fabric_", CloudReason),
            ("cicd_", CloudReason),
            ("git_", CloudReason),
            ("daxlib_", CloudReason),
        };

        private static Dictionary<string, string> BuildDenyExact()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(string reason, params string[] names) { foreach (var n in names) d[n] = reason; }

            // External I/O / lifecycle. run_interview executes live DAX + records its outcome — run it directly.
            Add(IoReason, "save_model", "open_model", "open_local", "open_live", "create_model", "connect_xmla",
                "connect_local", "disconnect", "refresh_partition", "export_vpax", "clear_cache", "evaluate_and_log",
                "run_interview");
            // Cloud/network reads not caught by a prefix.
            Add(CloudReason, "analyze_cloud_reports", "list_workspaces", "autogenerate_spec_from_fabric");
            // History checkpoints do a real git commit + model save (and restore reopens the model) when their
            // commit/restore flag is set, outside MutateAsync — a rehearsal cannot roll them back, so refuse like the
            // git_/deploy families rather than execute a real commit under a "nothing was changed" report.
            Add(CloudReason, "create_history_checkpoint", "restore_history_checkpoint");
            // Undo timeline.
            Add(TimelineReason, "undo_change", "redo_change");
            // Multi-mutate composites whose later steps read earlier persisted writes.
            Add(CompositeReason, "apply_plan", "apply_safe_fixes", "bpa_fix_all", "make_model_ai_ready",
                "apply_model_diff", "cherry_pick", "build_model_from_spec", "apply_dax_script", "apply_tmdl");
            // Workflow / knowledge / waiver bookkeeping (sidecar-file or annotation writers, not model-definition edits).
            // The settings-file + workflow-templates writers bypass MutateAsync, so a rehearsal would PERSIST them —
            // which would silently break the report's "nothing was changed" guarantee (same reason as the spec/plan writers below).
            Add(BookkeepingReason, "start_workflow", "submit_workflow_step", "skip_workflow_step", "abort_workflow",
                "save_workflow", "delete_workflow", "set_workflow_binding", "set_workflow_enabled", "set_workflow_enforcement", "activate_workflow_profile",
                "save_workflow_template", "delete_workflow_template", "instantiate_workflow_template",
                "add_insight", "approve_insight", "edit_insight", "upvote_insight",
                "downvote_insight", "delete_insight", "purge_knowledge", "accept_primer_suggestion", "reject_primer_suggestion",
                "set_model_primer", "save_evidence",   // sidecar-file writers (Primer .md, sealed evidence JSON/HTML) that bypass MutateAsync — a rehearsal would PERSIST + broadcast them
                "waive_finding", "unwaive_finding",
                "add_interview_question", "delete_interview_question",   // sidecar-JSONL writers — a rehearsal would PERSIST them
                // Spec/plan session-state writers bypass MutateAsync, so a rehearsal would PERSIST them — which
                // would silently break the report's "nothing was changed" guarantee. Denied for the same reason.
                "set_spec", "clear_spec", "load_spec", "save_spec", "propose_plan", "propose_replace", "add_plan_item", "set_plan_item",
                "clear_plan", "capture_baseline", "save_layout", "set_verified_mode", "load_bpa_rules", "reset_bpa_rules",
                "load_readiness_rules", "reset_readiness_rules");   // rule-annotation writers — same family as the BPA pair
            return d;
        }

        // ONE refusal builder so every teaching refusal reads the same shape.
        private static InvalidOperationException Refuse(string op, string reason) =>
            new InvalidOperationException($"dry_run cannot rehearse '{op}': {reason}.");

        private static void GuardDryRunnable(string op)
        {
            if (TryGuardDryRunnable(op, out var reason)) throw Refuse(op, reason);
        }

        /// <summary>The deny-list as a PREDICATE (no throw): true + the teaching <paramref name="reason"/> when the
        /// op is NOT dry-runnable. Shared with replay_check_workflow so both admission paths read the SAME deny-list
        /// (never a duplicated copy that could drift). Does not cover the unknown-op case — the caller resolves the
        /// op against <see cref="OpSurface"/> first.</summary>
        internal static bool TryGuardDryRunnable(string op, out string reason)
        {
            if (DenyExact.TryGetValue(op, out reason)) return true;
            foreach (var (prefix, r) in DenyPrefixes)
                if (op.StartsWith(prefix, StringComparison.Ordinal)) { reason = r; return true; }
            if (op.Contains("data_agent")) { reason = CloudReason; return true; }   // *_data_agent* — already commit-gated
            reason = null;
            return false;
        }

        /// <summary>The first REQUIRED parameter (skip the IEngine receiver + params with a C# default) whose name has
        /// no case-insensitive match in <paramref name="available"/>; null = every required param can be bound. Shares
        /// <see cref="BindArgs"/>'s exact parameter model so replay's "unbindable" verdict matches what a real bind would do.</summary>
        internal static string FirstUnbindableRequired(ParameterInfo[] pars, ISet<string> available)
        {
            foreach (var p in pars)
            {
                if (typeof(IEngine).IsAssignableFrom(p.ParameterType)) continue;   // the receiver, bound to `this`
                if (p.HasDefaultValue) continue;                                   // optional → its C# default
                if (!available.Contains(p.Name)) return p.Name;
            }
            return null;
        }

        // --- the wrapper op -----------------------------------------------------------------------------------

        private static readonly JsonSerializerOptions DryRunBindJson = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        private static readonly JsonSerializerOptions DryRunResultJson = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        public async Task<DryRunReport> DryRunOpAsync(string op, string argsJson)
        {
            op = (op ?? string.Empty).Trim();

            // 1) Resolve to the McpTools method (the SAME reflection GetOpCatalogAsync uses — one shared surface).
            if (!OpSurface.Methods.TryGetValue(op, out var method))
                throw new InvalidOperationException(
                    $"'{op}' is not a known op. get_op_catalog lists the real tool surface; dry_run rehearses any single MODEL-MUTATING op from it.");

            // 2) Deny-list — a teaching refusal that names the alternative.
            GuardDryRunnable(op);

            // 3) Bind the JSON args to the method's parameters (case-insensitive by name; missing optionals take
            //    their C# defaults; the IEngine param is `this`). A missing REQUIRED param teaches the signature.
            var pars = method.GetParameters();
            var args = BindArgs(op, pars, argsJson);

            // 4) Invoke under the ambient scope (always cleared) and await; a rehearsal that finds a failure is a
            //    SUCCESSFUL dry-run, so an op exception is captured into the report, not thrown.
            var report = new DryRunReport
            {
                Op = op,
                Note = "Rehearsal only — nothing was changed, no undo entry, no broadcast. Run the op itself to apply.",
            };
            var collector = new DryRunCollector();
            DryRunScope.Current = collector;
            try
            {
                object resultObj = null;
                try
                {
                    var task = (Task)method.Invoke(null, args);
                    await task.ConfigureAwait(false);
                    resultObj = UnwrapTaskResult(task);
                    report.WouldSucceed = true;
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    // A synchronous throw before the op returned its Task (rare — most ops are async).
                    report.WouldSucceed = false;
                    report.Error = tie.InnerException.Message;
                }
                catch (Exception ex)
                {
                    report.WouldSucceed = false;
                    report.Error = ex.Message;
                }
                report.Deltas = collector.Deltas.ToArray();
                report.Mutations = collector.MutationLabels.ToArray();
                if (resultObj != null)
                    report.Result = JsonSerializer.Serialize(resultObj, resultObj.GetType(), DryRunResultJson);
                return report;
            }
            finally { DryRunScope.Current = null; }
        }

        private object[] BindArgs(string op, ParameterInfo[] pars, string argsJson)
        {
            var bag = ParseArgsBag(op, argsJson);
            var args = new object[pars.Length];
            for (int i = 0; i < pars.Length; i++)
            {
                var p = pars[i];
                if (i == 0 && typeof(IEngine).IsAssignableFrom(p.ParameterType)) { args[i] = this; continue; }
                if (bag.TryGetValue(p.Name, out var el) && el.ValueKind != JsonValueKind.Null)
                {
                    try { args[i] = JsonSerializer.Deserialize(el.GetRawText(), p.ParameterType, DryRunBindJson); }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"dry_run '{op}': argument '{p.Name}' could not be read as {p.ParameterType.Name} — {ex.Message}. Signature: {op}({Signature(pars)}).");
                    }
                }
                else if (p.HasDefaultValue) args[i] = p.DefaultValue;
                else
                    throw new InvalidOperationException(
                        $"dry_run '{op}': missing required argument '{p.Name}'. Signature: {op}({Signature(pars)}). Supply it in the args JSON object.");
            }
            return args;
        }

        private static Dictionary<string, JsonElement> ParseArgsBag(string op, string argsJson)
        {
            var bag = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);   // case-insensitive by arg name
            if (string.IsNullOrWhiteSpace(argsJson)) return bag;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(argsJson); }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"dry_run '{op}': args must be a JSON object of {{argName: value}} — {ex.Message}.");
            }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException(
                        $"dry_run '{op}': args must be a JSON OBJECT keyed by argument name (e.g. {{\"objRef\":\"measure:Sales/Total\"}}).");
                foreach (var prop in doc.RootElement.EnumerateObject())
                    bag[prop.Name] = prop.Value.Clone();   // Clone so the elements outlive the document's disposal
            }
            return bag;
        }

        private static string Signature(ParameterInfo[] pars) =>
            string.Join(", ", pars.Where(p => !typeof(IEngine).IsAssignableFrom(p.ParameterType))
                .Select(p => p.HasDefaultValue ? p.Name + "?" : p.Name));

        /// <summary>Read <c>Task&lt;T&gt;.Result</c> after the task has completed; null for a non-generic Task or an
        /// <c>async Task</c> (whose Result is the internal VoidTaskResult).</summary>
        private static object UnwrapTaskResult(Task task)
        {
            var t = task.GetType();
            if (!t.IsGenericType) return null;
            var val = t.GetProperty("Result")?.GetValue(task);
            return val != null && val.GetType().FullName == "System.Threading.Tasks.VoidTaskResult" ? null : val;
        }
    }
}
