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
    /// method, binds the JSON args, and invokes it under the scope.
    ///
    /// THE GUARD IS THE GATE, not a list of ops. The tool runs on the gated engine (<see cref="AssistantEngine"/>), so every engine
    /// call it makes meets the registry: a model edit runs (the rehearsal rolls it back), and any call whose row says it cannot be
    /// previewed (<see cref="GateEntry.CanPreview"/>: a write to a file, a setting, a sidecar, the session, the undo timeline) is
    /// refused with one plain line, whatever the op is called. A new op therefore needs no entry here to be safe. The name list below
    /// is only the TEACHING layer: it turns a refusal the rehearsal would reach late into a sentence that names the right path.
    /// </summary>
    public sealed partial class LocalEngine
    {
        // --- teaching list (the ops dry_run turns away early, each family with the reason its refusal teaches) ----
        // NOT a guard: the gate refuses whatever the rollback does not cover (see the class comment), so a name missing from here is
        // never a way round. A rolled-back rehearsal of these would be misleading, not informative: I/O escapes the rollback,
        // composites diverge because later steps read earlier persisted writes, the undo timeline is already
        // reversible, and bookkeeping writes are sidecar/annotation state a model rollback doesn't cover.

        private const string IoReason =
            "it performs external/system I/O, which dry_run cannot rehearse: run it directly, or use its own preview path";
        private const string CloudReason =
            "it already defaults to dry-run internally (it will not write without a commit/consent flag): run it directly";
        private const string TimelineReason =
            "dry_run cannot rehearse the undo timeline: undo is already reversible, so just run undo_change / redo_change";
        private const string CompositeReason =
            "its later steps read earlier writes, so a rolled-back rehearsal would diverge: use propose_plan / the op's own preview path / apply one typed op at a time";
        private const string StartPlanReason =
            "it creates and keeps a start plan (a folder, a saved model and the plan's own file) that a rolled-back rehearsal does not undo: read the plan with get_start_plan, or run it directly";
        private const string BookkeepingReason =
            "it writes bookkeeping state (workflow / knowledge / waiver), not a model-definition edit: dry_run rehearses model edits and does not apply here";

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
            Add(CloudReason, "analyze_cloud_reports", "list_workspaces", "list_workspace_models", "list_workspace_sql_endpoints", "autogenerate_spec_from_fabric");
            // History checkpoints do a real git commit + model save (and restore reopens the model) when their
            // commit/restore flag is set, outside MutateAsync — a rehearsal cannot roll them back, so refuse like the
            // git_/deploy families rather than execute a real commit under a "nothing was changed" report.
            Add(CloudReason, "create_history_checkpoint", "restore_history_checkpoint");
            // The spec-first start (M11) creates a folder and a saved model, keeps its plan in a sidecar and builds into the
            // person's model with a plan the person approved: none of that rolls back with a rehearsal, which would then
            // report that nothing changed. The two reads (get_start_plan, get_start_words) change nothing and stay rehearsable.
            Add(StartPlanReason, "start_model_from_sql", "edit_start_plan", "edit_start_plans", "grant_start_disclosure", "write_start_words",
                "build_start_plan", "discard_start_plan");
            // Undo timeline.
            Add(TimelineReason, "undo_change", "redo_change");
            Add(BookkeepingReason, "set_plan_items", "add_plan_items", "plan_rows", "unwaive_findings");
            // Multi-mutate composites whose later steps read earlier persisted writes.
            Add(CompositeReason, "apply_plan", "apply_safe_fixes", "make_model_ai_ready",
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
                // The Model Plan's memory (.semanticus/plan-memory.json: skips, completions, the refresh log) is a
                // sidecar written by ComposeAsync OUTSIDE the model batch, so a rehearsal kept the skip for real
                // while the report said nothing changed (Astra 11 on PR 385). model_plan_action is denied with
                // them: it writes that same memory AND runs the safe fixes, and apply_safe_fixes is already denied
                // just above as a composite.
                "skip_model_plan_item", "unskip_model_plan_item", "model_plan_action", "apply_plan_items",
                // The target is kept in that same sidecar, so a rehearsal would write one for real while the
                // report said nothing changed. Same reason, same denial.
                "add_plan_target", "remove_plan_target",
                // Spec/plan session-state writers bypass MutateAsync, so a rehearsal would PERSIST them — which
                // would silently break the report's "nothing was changed" guarantee. Denied for the same reason.
                "set_spec", "clear_spec", "load_spec", "save_spec", "propose_plan", "propose_replace", "add_plan_item", "set_plan_item",
                "clear_plan", "capture_baseline", "save_layout", "set_verified_mode",
                "load_rules", "reset_rules",   // rule-annotation writers (both systems)
                "set_publish_destination", "remember_xmla_connection", "forget_connection");   // connection-registry writers: a rehearsal would persist them outside model rollback
            return d;
        }

        // ONE refusal builder so every teaching refusal reads the same shape.
        private static InvalidOperationException Refuse(string op, string reason) =>
            new InvalidOperationException($"dry_run cannot rehearse '{op}': {reason}.");

        private static void GuardDryRunnable(string op)
        {
            if (TryGuardDryRunnable(op, out var reason)) throw Refuse(op, reason);
        }

        /// <summary>The teaching list as a PREDICATE (no throw): true + the <paramref name="reason"/> when the
        /// op is one dry_run turns away early. Shared with replay_check_workflow so both admission paths read the SAME deny-list
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

            // 2) Teaching list — a refusal that names the alternative (the gate refuses the rest, below).
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
                Note = "Rehearsal only: nothing was changed, no undo entry, no broadcast. Run the op itself to apply.",
            };
            var collector = new DryRunCollector { FromTool = true };
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
                // The tool runs on the GATED engine, so every write it makes meets the gate: under a rehearsal the gate lets a model edit
                // through (rolled back) and refuses a write that lands anywhere else. Never on `this`: that is a road around the gate.
                if (i == 0 && typeof(IEngine).IsAssignableFrom(p.ParameterType)) { args[i] = GateContext.Gated ?? AssistantEngine.For(this); continue; }
                if (bag.TryGetValue(p.Name, out var el) && el.ValueKind != JsonValueKind.Null)
                {
                    try { args[i] = JsonSerializer.Deserialize(el.GetRawText(), p.ParameterType, DryRunBindJson); }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"dry_run '{op}': argument '{p.Name}' could not be read as {p.ParameterType.Name}: {ex.Message}. Signature: {op}({Signature(pars)}).");
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
                throw new InvalidOperationException($"dry_run '{op}': args must be a JSON object of {{argName: value}}: {ex.Message}.");
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
