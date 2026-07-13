using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// Learning Loop L4 REPLAY CHECK (docs/learning-loop-plan.md §3.3 — "replay check where cheap"): the expensive
    /// half of the admission pipeline, extending check_workflow. Where check_workflow statically resolves a workflow
    /// against the op surface, replay_check_workflow REHEARSES each declared step op through the universal dry_run
    /// contract (guaranteed rollback — nothing changes) driven by the workflow's EXEMPLAR RUN answers.
    ///
    /// Why the exemplar: the L0 experience log records what happened (kinds/labels/deltas) but NOT op arguments, so
    /// replay args can't come from the log. A DISTILLED workflow instead carries its exemplar run's gate answers in
    /// frontmatter (`exemplar_answers:` — see /distill-workflow). The parser preserves only FLAT frontmatter keys as
    /// strings (WorkflowParser.ParseFrontmatter), so the block is a flat one-line-JSON convention, not a nested map.
    ///
    /// Per-op outcomes (all surfaced, none silently dropped): REHEARSED (dry_run ran — wouldSucceed + delta count, or
    /// the op's own teaching error), SKIPPED-DENIED (deny-listed / unknown — never a failure), SKIPPED-UNBINDABLE (a
    /// required param the exemplar can't supply — named, not a failure). DAX probe/equivalence verifies are marked
    /// REPLAYABLE (probe + target resolve) but NEVER executed here — live DAX needs a connection; mirrors the
    /// executors' honest offline-skip. Free, read-only (matches check_workflow's gating — no entitlement gate).
    /// </summary>
    public sealed partial class LocalEngine
    {
        public async Task<WorkflowReplayReport> ReplayCheckWorkflowAsync(string name)
        {
            // 1) Extend the parse/op-catalog admission — run check_workflow first and carry its findings in.
            var check = await CheckWorkflowAsync(name);   // throws instructively if the workflow does not exist
            var def = LoadWorkflowDefs().First(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));

            var report = new WorkflowReplayReport
            {
                Name = check.Name,
                ParseError = check.ParseError,
                AdmissionFindings = check.Findings,
            };
            if (check.ParseError != null)
            {
                report.ReplaySkipped = true;
                report.Admissible = false;
                report.Note = "NOT admissible — the file does not parse; fix it before a replay. " + check.ParseError;
                return report;
            }

            // 2) The exemplar block (flat convention). No exemplar → SKIPPED with an instructive note, never a failure.
            var answers = ParseExemplarAnswers(def, out var skipNote);
            if (answers == null)
            {
                report.ReplaySkipped = true;
                report.Admissible = true;   // parses clean and nothing was rehearsed that could fail
                report.Note = skipNote;
                return report;
            }
            report.ExemplarRun = def.Provenance != null && def.Provenance.TryGetValue("exemplar_run", out var runId) ? runId : null;

            var rows = new List<WorkflowReplayRow>();
            var available = new HashSet<string>(answers.Keys, StringComparer.OrdinalIgnoreCase);

            foreach (var step in def.Steps)
            {
                // 3) Rehearse each declared op via the SAME dry_run contract (real code path, guaranteed rollback).
                foreach (var op in step.Ops ?? Array.Empty<string>())
                    rows.Add(await RehearseOpAsync(step, op, answers, available, report));

                // 4) Verify blocks — DAX probe/equivalence replayability (surfaced, NOT executed here).
                foreach (var v in step.Gate?.Verify ?? Array.Empty<VerifySpec>())
                {
                    if (v.Kind != "dax_probe" && v.Kind != "dax_equivalence") continue;
                    var row = await ReplayableVerifyRowAsync(step, v, def, answers);
                    if (row.Outcome == "replayable") report.Replayable++;
                    rows.Add(row);
                }
            }

            report.Rows = rows.ToArray();
            // 5) Verdict: parse-clean AND no rehearsed op would fail. Denied/unbindable ops and DAX probes were NOT
            //    executed, so they never dock admissibility — they are surfaced for the reviewer, nothing more.
            report.Admissible = report.ParseError == null && report.RehearsedFailed == 0;
            report.Note = report.Admissible
                ? "Admissible — parses clean and every op the exemplar can drive would succeed (rehearsed then rolled back; nothing changed). Denied/unbindable ops and DAX probes are surfaced, not executed. Verdict rule: parse-clean AND no rehearsed op reported wouldSucceed=false."
                : $"NOT admissible — {report.RehearsedFailed} rehearsed op(s) would fail (see the rows). Verdict rule: parse-clean AND no rehearsed op reported wouldSucceed=false.";
            return report;
        }

        // ---- one op's rehearsal ------------------------------------------------------------------

        private async Task<WorkflowReplayRow> RehearseOpAsync(
            WorkflowStep step, string op, Dictionary<string, string> answers, ISet<string> available, WorkflowReplayReport report)
        {
            var row = new WorkflowReplayRow { Step = step.Id, Op = op };

            // Unknown op — cannot rehearse (check_workflow already flagged it warn; surface it here too, not as a failure).
            if (!OpSurface.Methods.TryGetValue(op, out var method))
            {
                row.Outcome = "skipped-denied";
                row.Detail = $"'{op}' is not a known op (get_op_catalog lists the real surface) — cannot rehearse.";
                report.SkippedDenied++;
                return row;
            }
            // Deny-listed — the SAME guard dry_run uses (I/O, composites, timeline, bookkeeping). Not a failure.
            if (LocalEngine.TryGuardDryRunnable(op, out var reason))
            {
                row.Outcome = "skipped-denied";
                row.Detail = $"dry_run does not rehearse '{op}': {reason}.";
                report.SkippedDenied++;
                return row;
            }
            var pars = method.GetParameters();
            // Unbindable — a required parameter the exemplar answers can't supply. Named, not a failure.
            var missing = FirstUnbindableRequired(pars, available);
            if (missing != null)
            {
                row.Outcome = "skipped-unbindable";
                row.Detail = $"required parameter '{missing}' has no matching exemplar answer — add an input named '{missing}' to the exemplar run (or answer it) so replay can drive this op.";
                report.SkippedUnbindable++;
                return row;
            }

            // Rehearse: build args from the name-matched exemplar answers and run the guaranteed-rollback dry_run.
            row.Outcome = "rehearsed";
            report.Rehearsed++;
            try
            {
                var dr = await DryRunOpAsync(op, BuildArgsJson(pars, answers));
                row.WouldSucceed = dr.WouldSucceed;
                row.DeltaCount = dr.Deltas?.Length ?? 0;
                row.Detail = dr.WouldSucceed
                    ? $"would succeed — {row.DeltaCount} would-be delta(s); nothing changed (rolled back)."
                    : "would FAIL — " + dr.Error;
                if (!dr.WouldSucceed) report.RehearsedFailed++;
            }
            catch (Exception ex)
            {
                // A bind/type mismatch surfaced by the shared binder (rare — gate answers are strings): honest as a
                // rehearsal that cannot succeed (the exemplar arg can't drive this op's typed parameter).
                row.WouldSucceed = false;
                row.Detail = "could not rehearse — " + ex.Message;
                report.RehearsedFailed++;
            }
            return row;
        }

        // ---- a verify block's replayability (never executed here) --------------------------------

        private async Task<WorkflowReplayRow> ReplayableVerifyRowAsync(
            WorkflowStep step, VerifySpec v, WorkflowDef def, IReadOnlyDictionary<string, string> answers)
        {
            var row = new WorkflowReplayRow { Step = step.Id, Op = "verify:" + v.Kind, Outcome = "verify-skipped" };

            var probeAnswered = !string.IsNullOrEmpty(v.Probe) && answers.TryGetValue(v.Probe, out var pv) && !string.IsNullOrEmpty(pv);
            // Target = the objectRef-typed input's exemplar answer (the LatestObjectRefAnswer convention, over the exemplar).
            string target = null;
            foreach (var st in def.Steps)
                foreach (var input in st.Gate?.Inputs ?? Array.Empty<GateInput>())
                    if (input.Type == "objectRef" && answers.TryGetValue(input.Name, out var a) && !string.IsNullOrEmpty(a))
                        target = a;

            if (!probeAnswered || target == null)
            {
                row.Detail = !probeAnswered
                    ? $"not replayable — the probe input '{v.Probe}' has no exemplar answer to compare against."
                    : "not replayable — no objectRef-typed input has an exemplar answer naming the target object.";
                return row;
            }

            // Does the target resolve to an existing object on the CURRENT model?
            var resolves = _sessions.Current != null
                && await _sessions.Current.ReadAsync(m => ObjectRefs.Resolve(m, target) != null);
            if (!resolves)
            {
                row.Detail = _sessions.Current == null
                    ? $"not replayable now — no model open, so '{target}' can't be resolved (open the model, then replay)."
                    : $"not replayable — '{target}' does not resolve to an existing object on the current model.";
                return row;
            }

            // Both sides present + the target resolves → REPLAYABLE. We do NOT execute live DAX here (a probe needs a
            // connection); mirror WorkflowDaxProbeAsync's honest offline-skip and point at the real-evidence path.
            row.Outcome = "replayable";
            row.Detail = _live == null
                ? $"replayable: needs a live/attached session — probe '{v.Probe}' + target '{target}' resolve, but offline now, so the DAX was NOT executed (open_live/open_local, then start_workflow for real evidence)."
                : $"replayable — probe '{v.Probe}' + target '{target}' resolve and a live session is attached; replay does not execute live DAX itself (run start_workflow for real evidence).";
            return row;
        }

        // ---- the exemplar block (flat convention) ------------------------------------------------

        /// <summary>Read the exemplar answers from provenance. Convention (flat — the only shape the parser round-trips):
        /// <c>exemplar_answers: {"inputName":"answer", ...}</c> one-line JSON, optionally with <c>exemplar_run: &lt;runId&gt;</c>.
        /// null = no/invalid exemplar (with an instructive <paramref name="skipNote"/>) — replay is SKIPPED, never failed.</summary>
        private static Dictionary<string, string> ParseExemplarAnswers(WorkflowDef def, out string skipNote)
        {
            skipNote = null;
            if (def.Provenance == null || !def.Provenance.TryGetValue("exemplar_answers", out var json) || string.IsNullOrWhiteSpace(json))
            {
                skipNote = "Replay SKIPPED: no exemplar block — /distill-workflow embeds one when distilling from a run; replay needs example args. "
                    + "Add `exemplar_answers: {\"inputName\":\"answer\", ...}` (one-line JSON of the exemplar run's gate answers) to the frontmatter.";
                return null;
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    skipNote = "Replay SKIPPED: `exemplar_answers` is not a JSON object of {inputName: answer}. Re-distill or fix the block by hand.";
                    return null;
                }
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in doc.RootElement.EnumerateObject())
                    map[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
                if (map.Count == 0)
                {
                    skipNote = "Replay SKIPPED: `exemplar_answers` is an empty object — no example args to drive any op.";
                    return null;
                }
                return map;
            }
            catch (Exception ex)
            {
                skipNote = "Replay SKIPPED: `exemplar_answers` is not valid one-line JSON (" + ex.Message + "). Re-distill or fix the block by hand.";
                return null;
            }
        }

        /// <summary>Build the dry_run args JSON from the exemplar answers, keyed by the op's parameter names
        /// (case-insensitive, the binder's own matching). Each value rides through as a JSON scalar/array/object when it
        /// already parses as one, else a JSON string — gate answers are text/objectRef → strings, exactly right for the
        /// string-typed op params that dominate the surface.</summary>
        private static string BuildArgsJson(ParameterInfo[] pars, Dictionary<string, string> answers)
        {
            var sb = new StringBuilder("{");
            var first = true;
            foreach (var p in pars)
            {
                if (typeof(IEngine).IsAssignableFrom(p.ParameterType)) continue;   // the receiver
                if (!answers.TryGetValue(p.Name, out var val)) continue;           // no matching answer → let its default apply
                if (!first) sb.Append(',');
                first = false;
                sb.Append(JsonSerializer.Serialize(p.Name)).Append(':').Append(EncodeJsonValue(val, p.ParameterType));
            }
            return sb.Append('}').ToString();
        }

        /// <summary>A string-typed parameter takes the answer verbatim as a JSON string — the right call for the DAX/
        /// name/ref gate answers that dominate (a value like "1" is a DAX expression, not the number 1). Only a
        /// non-string param gets smart-encoding (the answer rides through as a JSON scalar/array when it parses as one).</summary>
        private static string EncodeJsonValue(string raw, Type paramType)
        {
            if (raw == null) return "null";
            if (paramType == typeof(string)) return JsonSerializer.Serialize(raw);
            var t = raw.TrimStart();
            if (t.Length > 0 && (t[0] == '{' || t[0] == '[' || t[0] == '-' || char.IsDigit(t[0]) || t == "true" || t == "false" || t == "null"))
                try { using (JsonDocument.Parse(raw)) return raw; } catch { /* not JSON — fall through to a string */ }
            return JsonSerializer.Serialize(raw);
        }
    }

    // ---- wire DTOs (broadcast/returned as-is; camelCase over the wire, mirrored in knowledge.tsx) -------

    /// <summary>Learning Loop L4 (docs/learning-loop-plan.md §3.3): the REPLAY-CHECK report — the expensive half of the
    /// admission pipeline. Carries the parse/op-catalog findings (from check_workflow), a per-op/per-verify row for each
    /// step, roll-up counts, and the overall <see cref="Admissible"/> verdict. Verdict rule: <c>ParseError == null AND no
    /// rehearsed op reported wouldSucceed=false</c> — denied/unbindable ops and DAX probes are surfaced, not scored.</summary>
    public sealed class WorkflowReplayReport
    {
        public string Name { get; set; }
        public string ParseError { get; set; }                    // null = parsed clean
        public CheckFinding[] AdmissionFindings { get; set; } = Array.Empty<CheckFinding>();   // from CheckWorkflowAsync
        public string ExemplarRun { get; set; }                   // the exemplar run id, if the block named one
        public bool ReplaySkipped { get; set; }                   // true when there was no/invalid exemplar (or a parse error)
        public WorkflowReplayRow[] Rows { get; set; } = Array.Empty<WorkflowReplayRow>();
        public int Rehearsed { get; set; }
        public int RehearsedFailed { get; set; }                  // rehearsed ops whose dry_run reported wouldSucceed=false
        public int SkippedDenied { get; set; }                    // deny-listed or unknown ops (not failures)
        public int SkippedUnbindable { get; set; }                // ops the exemplar can't supply a required param for
        public int Replayable { get; set; }                       // DAX verifies that could replay (not executed here)
        public bool Admissible { get; set; }
        public string Note { get; set; }                          // explains the verdict / the skip reason
    }

    /// <summary>One replay row. <see cref="Outcome"/> ∈ rehearsed | skipped-denied | skipped-unbindable | replayable |
    /// verify-skipped. <see cref="Op"/> is the op name, or "verify:&lt;kind&gt;" for a verify row.</summary>
    public sealed class WorkflowReplayRow
    {
        public string Step { get; set; }
        public string Op { get; set; }
        public string Outcome { get; set; }
        public string Detail { get; set; }
        public bool? WouldSucceed { get; set; }                   // set only for a rehearsed op
        public int DeltaCount { get; set; }                       // would-be deltas for a rehearsed op
    }
}
