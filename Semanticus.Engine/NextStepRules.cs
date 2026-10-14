// The Next line's rules and words (1.5, option A; Kane picked it on 11 Oct 2026).
//
// PURE. The inputs are counts and yes-or-no facts the engine already keeps; the output is the list of eligible steps in rule
// order, FIRST MATCH WINS on Free. Pro may let Jev (next.step) choose among these same eligible kinds, and nothing else: every
// word on the line, the ask, the why, the source and the Do it button, comes from the table below, never from Jev.
//
// The words of the five moments are the mock's, word for word (next-mock/index.html, MOMENTS). The kinds the mock does not
// show (publish-test, publish-production, keep-healthy) are written the same way: short, plain, no em dashes.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Semanticus.Engine
{
    /// <summary>What the rules read. No names reach Jev from here except through <see cref="TopTaskTitle"/>, which only the
    /// engine's own Pro sentence uses; the snapshot sends <see cref="TopTaskKind"/> (a family id) instead.</summary>
    public sealed class NextStepInputs
    {
        public bool ModelOpen { get; set; }
        public int Unsaved { get; set; }              // objects changed since the last save; 0 = nothing unsaved
        public int RepeatSteps { get; set; }          // how many steps the repeated run has
        public int RepeatTimes { get; set; }          // on how many objects the same steps were done; 3 or more counts
        public string RepeatNoun { get; set; }        // "tables", "measures", "columns", "objects"
        public int TasksReady { get; set; }
        public int TasksDone { get; set; }
        public string TopTaskTitle { get; set; }
        public string TopTaskKind { get; set; }
        public bool PlanJustFinished { get; set; }    // the last ready task closed since the last check run
        public bool SavedSinceChange { get; set; }    // a save happened in this session and nothing changed after it
        public bool ChecksRunSinceSave { get; set; }
        public int ChecksFailed { get; set; }
        public int ChecksCount { get; set; }          // saved checks for this model (the "12 checks" of the line)
        public bool PublishedTest { get; set; }
        public bool PublishedProduction { get; set; } // the model is live (opened from a published model, or pushed there)
    }

    public static class NextStepRules
    {
        public const string Save = "save", Workflow = "workflow", RunPlan = "run-plan", RunChecks = "run-checks",
            CheckThenPublish = "check-then-publish", PublishTest = "publish-test", PublishProduction = "publish-production",
            KeepHealthy = "keep-healthy", Assistant = "assistant";

        public const string ByRules = "rules", ByJev = "jev", ByAssistant = "assistant";
        public const string JevSource = "Jev chose this";
        public const string AssistantSource = "Your assistant suggested this";

        public static readonly string[] StageNames =
        {
            "Open the model", "Get it AI ready", "Check the numbers", "Save", "Publish to test", "Publish to production", "Keep it healthy",
        };

        /// <summary>Where the person is, 1 to 7, from the same facts.</summary>
        public static int StageOf(NextStepInputs i)
        {
            if (i == null || !i.ModelOpen) return 1;
            if (i.PublishedProduction) return 7;
            if (i.TasksReady > 0) return 2;
            var checksPassed = i.ChecksRunSinceSave && i.ChecksFailed == 0;
            if (!checksPassed) return 3;
            if (i.Unsaved > 0) return 4;
            return i.PublishedTest ? 6 : 5;
        }

        /// <summary>Every rule that matches, in rule order. The first is the Free answer; Pro's Jev may pick another of them.</summary>
        public static List<string> Eligible(NextStepInputs i)
        {
            var kinds = new List<string>();
            if (i == null || !i.ModelOpen) return kinds;
            if (i.Unsaved > 0) kinds.Add(Save);
            if (i.RepeatTimes >= 3) kinds.Add(Workflow);
            if (i.TasksReady > 0) kinds.Add(RunPlan);
            var planDone = i.TasksReady == 0 && i.TasksDone > 0;
            if (!i.ChecksRunSinceSave && i.Unsaved == 0 && (planDone || i.SavedSinceChange))
                kinds.Add(planDone && i.PlanJustFinished ? CheckThenPublish : RunChecks);
            if (i.ChecksRunSinceSave && i.ChecksFailed == 0 && i.Unsaved == 0)
                kinds.Add(i.PublishedProduction ? KeepHealthy : i.PublishedTest ? PublishProduction : PublishTest);
            return kinds;
        }

        /// <summary>The fixed words for a kind. <paramref name="pro"/> picks the Pro why (shown when Jev chose it).</summary>
        public static NextStepStep Words(string kind, NextStepInputs i, bool pro)
        {
            string Plural(int n, string one, string many) => n == 1 ? one : many;
            var checks = i.ChecksCount;
            string checksLead(string tail) => checks > 0 ? $"{checks} {Plural(checks, "check tests", "checks test")} {tail}" : null;
            switch (kind)
            {
                case Save:
                {
                    var n = Math.Max(1, i.Unsaved);
                    var free = $"{n} {Plural(n, "change is", "changes are")} not saved yet.";
                    return Step(kind, null, "Save your changes", pro ? free + " Save before the next task." : free, "Not saved yet",
                        "Save the model.", new NextStepDoIt { Op = "save", Label = "Save", Done = "Saved." });
                }
                case Workflow:
                {
                    var free = $"You did the same {Math.Max(1, i.RepeatSteps)} {Plural(i.RepeatSteps, "step", "steps")} on {i.RepeatTimes} {i.RepeatNoun ?? "objects"}.";
                    return Step(kind, null, "Save these steps as a workflow", free, "Same steps 3 times",
                        "Save the steps I just repeated as a workflow.",
                        new NextStepDoIt { Op = "draft_workflow", Label = "Do it", Done = "Draft workflow made. Open Workflows to see it." });
                }
                case RunPlan:
                {
                    var n = i.TasksReady;
                    var free = $"{n} {Plural(n, "task is", "tasks are")} ready. They run in Plan order.";
                    var proWhy = string.IsNullOrWhiteSpace(i.TopTaskTitle) ? free
                        : $"{n} {Plural(n, "task is", "tasks are")} ready. Jev put \"{i.TopTaskTitle}\" first.";
                    return Step(kind, null, "Ask your assistant to run the plan", pro ? proWhy : free, "From your Plan", "Run the plan.", null);
                }
                case CheckThenPublish:
                {
                    var free = checksLead("the measures the plan changed.") ?? "Your checks have not run since the plan finished.";
                    return Step(kind, "Your plan is done.", "Check the numbers, then publish to test", pro ? free + " Then test is the next stop." : free,
                        "Plan done", "Run the checks, then publish the model to test.", RunChecksDoIt());
                }
                case RunChecks:
                {
                    var free = checks > 0 ? checksLead("the measures you changed.") + " They have not run since your save." : "Your checks have not run since your save.";
                    var proWhy = checks > 0 ? checksLead("the measures you changed.") + " Run them before you publish." : "Run your checks before you publish.";
                    return Step(kind, null, "Run your checks", pro ? proWhy : free, "Saved, checks not run", "Run the checks.", RunChecksDoIt());
                }
                case PublishTest:
                    return Step(kind, null, "Publish to test", pro ? "Your checks passed. Test is the next stop before production." : "Your checks passed. Test is the next stop.",
                        "Checks passed", "Publish the model to the test workspace.", null);
                case PublishProduction:
                    return Step(kind, null, "Publish to production", pro ? "Test looks good. Production is the last stop, so people can use it." : "Test looks good. Production is the last stop.",
                        "Live in test", "Publish the model to production.", null);
                case KeepHealthy:
                    return Step(kind, null, "Check the model's health", pro ? "The model is live. A check now and then keeps the numbers right." : "The model is live. A check now and then keeps it right.",
                        "Live", "Run the checks on the live model.", RunChecksDoIt());
                default:
                    return null;
            }
        }

        private static NextStepDoIt RunChecksDoIt() => new NextStepDoIt { Op = "run_checks", Label = "Do it", Done = "Checks running." };

        private static NextStepStep Step(string kind, string lead, string ask, string why, string source, string prompt, NextStepDoIt doIt) =>
            new NextStepStep { Kind = kind, Lead = lead, Ask = ask, Why = why, Source = source, ChosenBy = ByRules, Prompt = prompt, DoIt = doIt };

        /// <summary>"ns-" + 12 hex of (kind, ask, stage): the same state gives the same id, so a hidden step stays hidden until it changes.</summary>
        public static string IdOf(string kind, string ask, int stage)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes((kind ?? "") + "\n" + (ask ?? "") + "\n" + stage));
            return "ns-" + string.Concat(hash.Take(6).Select(b => b.ToString("x2")));
        }
    }
}
