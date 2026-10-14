// ONE INVARIANT for the Plan list (fix-coverage3, Kane 13 Oct: "Plan done must show the right number", strike three).
// For ANY plan the engine returns:
//   (a) no job is listed twice: no two rows share a finding (Claims), and no two rows share a title and the same objects;
//   (b) the strip counts come from the list: done == rows that read Done (+ the rows Refresh plan cleared, which a test passes in; and the applied tasks that left the plan, which the engine counts),
//       total == done + the rows still to do (a set-aside row is not a task to do);
//   (c) every open scored finding (Model quality severity 2 and up, AI readiness scored) is on an open row, or on the task that holds it.
// A test calls CheckAsync after EVERY step it drives, not only at the end.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    internal sealed class PlanInvariant
    {
        private static readonly Dictionary<string, string> Twins = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AVOID_USING_THE_IFERROR_FUNCTION"] = "BP-DAX-IFERROR", ["PROVIDE_FORMAT_STRING_FOR_MEASURES"] = "FMT-MEASURE", ["SEM_COLUMN_NEEDS_FORMAT_STRING"] = "FMT-COLUMN",
        };

        private readonly LocalEngine _e;
        private readonly ITestOutputHelper _out;
        /// <summary>The rules and objects each derived row named when it was last seen, so a task handed over from it covers what the row covered.</summary>
        private readonly Dictionary<string, (string[] Rules, string[] Objects, string[] Claims)> _rowsSeen = new(StringComparer.Ordinal);

        public PlanInvariant(LocalEngine e, ITestOutputHelper output) { _e = e; _out = output; }

        private static string[] Arr(string[] a) => a ?? Array.Empty<string>();

        /// <summary>The problems the plan has against the invariant; empty when it holds. Writes the list and the strip to the test output.</summary>
        public async Task<List<string>> ProblemsAsync(ModelPlanView plan, string label, int cleared = 0, bool findings = true)
        {
            var problems = new List<string>();
            var items = plan.Items ?? Array.Empty<ModelPlanItem>();
            _out?.WriteLine($"---- {label}: strip {plan.Strip?.TasksDone}/{plan.Strip?.TasksAll}, {items.Length} rows");
            foreach (var i in items) _out?.WriteLine($"     {i.Id} | {i.StateWord} | {i.Title} | objs={Arr(i.Objects).Length} claims={Arr(i.Claims).Length} rules={string.Join(",", Arr(i.Rules))} o={string.Join(",", Arr(i.Objects).Take(3))}");
            foreach (var i in items.Where(i => i.Id != null && !i.Id.StartsWith("pt-", StringComparison.Ordinal)))
                _rowsSeen[i.Id] = (Arr(i.Rules), Arr(i.Objects), Arr(i.Claims));

            // (a) no job twice
            var live = items.Where(i => i.StateWord != PlanStateWords.SetAside).ToArray();
            for (var x = 0; x < live.Length; x++)
                for (var y = x + 1; y < live.Length; y++)
                {
                    var a = live[x]; var b = live[y];
                    var shared = Arr(a.Claims).Intersect(Arr(b.Claims), StringComparer.OrdinalIgnoreCase).Any();
                    // Two jobs that each carry their own findings are told apart by those findings: a later finding of the same kind is a new job and may share a title.
                    var bothHold = Arr(a.Claims).Length > 0 && Arr(b.Claims).Length > 0;
                    var same = !bothHold && a.Title == b.Title && Arr(a.Objects).OrderBy(o => o, StringComparer.Ordinal).SequenceEqual(Arr(b.Objects).OrderBy(o => o, StringComparer.Ordinal));
                    if (shared || same)
                        problems.Add($"LISTED-TWICE ({(shared ? "same findings" : "same title and objects")}): {a.Id} {a.StateWord} / {b.Id} {b.StateWord} :: {a.Title}");
                }

            // (b) the strip is the list
            // Applied tasks that left the live plan count as done the moment they leave (hero run 21, N9), so Refresh plan changes no figure.
            cleared += await _e.VanishedAppliedCountForTest();
            var done = live.Count(i => i.StateWord == PlanStateWords.Done) + cleared;
            var total = live.Length + cleared;
            if (plan.Strip == null) problems.Add("NO STRIP");
            else
            {
                if (plan.Strip.TasksDone != done) problems.Add($"STRIP-DONE {plan.Strip.TasksDone} but the list has {done} done");
                if (plan.Strip.TasksAll != total) problems.Add($"STRIP-TOTAL {plan.Strip.TasksAll} but the list has {total} jobs");
            }

            // (c) no scored finding on no open row
            if (findings)
            {
                var open = items.Where(i => PlanStateWords.IsOpen(i.StateWord)).ToArray();
                var bpa = await _e.BpaScanAsync();
                var air = await _e.AiReadinessScanAsync();
                bool Covers(ModelPlanItem i, string rule, string obj)
                {
                    var key = LocalEngine.ClaimKey(rule, obj);
                    var rules = Arr(i.Rules); var objs = Arr(i.Objects); var claims = Arr(i.Claims);
                    if (i.OriginLink?.Kind == PlanOriginKinds.Item && i.OriginLink.Ref != null && _rowsSeen.TryGetValue(i.OriginLink.Ref, out var was))
                    { rules = rules.Concat(was.Rules).ToArray(); objs = objs.Concat(was.Objects).ToArray(); claims = claims.Concat(was.Claims).ToArray(); }
                    if (claims.Contains(key, StringComparer.OrdinalIgnoreCase)) return true;
                    // A task handed over from a row names the objects it works, and carries no rule list of its own.
                    if (PlanTasks.IsTaskId(i.Id) && Arr(i.Rules).Length == 0 && objs.Contains(obj, StringComparer.OrdinalIgnoreCase)) return true;
                    if (i.Id == "cleanup/model" || i.OriginLink?.Ref == "cleanup/model") { if (objs.Contains(obj, StringComparer.OrdinalIgnoreCase)) return true; }
                    var twin = PairedAcceptances.AirTwinOf(rule) ?? (Twins.TryGetValue(rule ?? "", out var t) ? t : null);
                    var ruleOk = rules.Contains(rule, StringComparer.OrdinalIgnoreCase) || (twin != null && rules.Contains(twin, StringComparer.OrdinalIgnoreCase));
                    return ruleOk && (objs.Length == 0 || objs.Contains(obj, StringComparer.OrdinalIgnoreCase));
                }
                foreach (var v in bpa.Violations.Where(v => !v.Waived && v.Severity >= 2))
                    if (!open.Any(i => Covers(i, v.RuleId, v.ObjectRef))) problems.Add("LEFT-ON-NO-ROW bpa " + v.RuleId + " " + v.ObjectRef);
                foreach (var x in air.Findings.Where(x => !x.Waived && (air.ScoredRuleIds.Contains(x.RuleId ?? "") || x.RuleId is "LIMIT-SCALE" or "DAX-REFERENCE" or "DESC-MEASURE")))
                    if (!open.Any(i => Covers(i, x.RuleId, x.ObjectRef))) problems.Add("LEFT-ON-NO-ROW air " + x.RuleId + " " + x.ObjectRef);
            }
            foreach (var p in problems) _out?.WriteLine("     INVARIANT: " + p);
            return problems;
        }

        /// <summary>Reads the plan and asserts the invariant. Returns the plan so a drive can carry on with it.</summary>
        public async Task<ModelPlanView> CheckAsync(string label, int cleared = 0, bool findings = true)
        {
            var plan = await _e.GetModelPlanAsync();
            Check(plan, label, await ProblemsAsync(plan, label, cleared, findings));
            return plan;
        }

        public static void Check(ModelPlanView plan, string label, List<string> problems) =>
            Xunit.Assert.True(problems.Count == 0, label + " breaks the Plan invariant:\n" + string.Join("\n", problems));
    }
}
