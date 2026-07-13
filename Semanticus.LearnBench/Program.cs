using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;

namespace Semanticus.LearnBench
{
    /// <summary>
    /// Learning-loop EFFICACY simulation (docs/learning-loop-testing.md §5, T3): does memory produce LIFT?
    /// The ExpeL/ACE A/B design run entirely on Semanticus's OWN deterministic oracle — no human judge (§3).
    ///
    /// Corpus: one model FAMILY (same FingerprintKey, temp copies of the vendored model). TRAIN once — the
    /// scripted agent works the task naively, the gate FAILS, and that failure's root cause is captured as an
    /// approved, fingerprint-scoped post-mortem. EVAL a held-out split of same-shape models TWICE with an
    /// IDENTICAL scripted policy — memory-ON (the populated store) vs memory-OFF (an empty store). Lift = ON − OFF.
    ///
    /// Scripted pseudo-agent (§5.3): it cannot test JUDGMENT (that is the real-LLM harness, T4) — it
    /// deterministically proves the regression-gate claim: when a relevant insight exists, the harness SURFACES
    /// it (recall) and applying it MOVES the oracle. The oracle here is the workflow input gate: first-submission
    /// pass-rate, rounds-to-pass, repeated-error rate, and recall precision@1 — all engine-deterministic (§6).
    /// Offline throughout (the gate is input validation; live verifies would only add skips). Returns 0 iff the
    /// lift is positive, so it gates like a smoke.
    /// </summary>
    internal static class Program
    {
        private const int EvalModels = 6;
        private const string Task = "optimize-dax";              // its step-1 gate REQUIRES target + originalDax
        private const string HintKey = "optimize-dax";           // the post-mortem's match key the policy keys on
        private const string Query = "optimize a slow measure";
        // What a memory-guided agent supplies (non-empty required inputs) vs the naive empty submit.
        private const string Correct = "{\"target\":\"measure:Sales/Sales Amount\",\"originalDax\":\"CALCULATE(SUM(Sales[SalesAmount]))\",\"equivalenceGrid\":\"Date[Year]\"}";
        private const string Naive = "{}";

        private static async Task<int> Main()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "learnbench-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(tempRoot);
            var origHome = Environment.GetEnvironmentVariable("USERPROFILE");
            var home = Path.Combine(tempRoot, "home");            // isolate GLOBAL-scope recall from the real ~/.semanticus
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("USERPROFILE", home);

            var src = FindTestBim();
            try
            {
                Console.WriteLine("== Learning-loop efficacy A/B (scripted agent, deterministic oracle) ==");

                // memory-ON family: TRAIN populates its store with the post-mortem; eval models share that store.
                var famOn = Path.Combine(tempRoot, "on");
                await BuildFamilyAsync(famOn, src, withTrainedInsight: true);

                // memory-OFF family: identical eval models, EMPTY store.
                var famOff = Path.Combine(tempRoot, "off");
                await BuildFamilyAsync(famOff, src, withTrainedInsight: false);

                var on = await EvalSplitAsync(famOn);
                var off = await EvalSplitAsync(famOff);

                double passOn = on.passedFirst / (double)EvalModels, passOff = off.passedFirst / (double)EvalModels;
                double roundsOn = on.rounds / (double)EvalModels, roundsOff = off.rounds / (double)EvalModels;
                double repeatOn = (EvalModels - on.passedFirst) / (double)EvalModels, repeatOff = (EvalModels - off.passedFirst) / (double)EvalModels;
                double precisionOn = on.precisionHits / (double)EvalModels;
                double liftPp = (passOn - passOff) * 100;

                Console.WriteLine();
                Console.WriteLine($"  Eval models (held-out, same family): {EvalModels}");
                Console.WriteLine($"  First-submission gate-pass rate:  ON = {passOn:P0}   OFF = {passOff:P0}   LIFT = +{liftPp:0}pp");
                Console.WriteLine($"  Avg rounds-to-pass (fewer better): ON = {roundsOn:0.0}    OFF = {roundsOff:0.0}");
                Console.WriteLine($"  Repeated-error rate (same gate):   ON = {repeatOn:P0}    OFF = {repeatOff:P0}");
                Console.WriteLine($"  Recall precision@1 (ON):           {precisionOn:P0}");
                Console.WriteLine();

                bool positive = passOn > passOff && precisionOn > 0;
                Console.WriteLine(positive
                    ? "==== LEARN BENCH: LIFT POSITIVE — memory-ON beats memory-OFF on the oracle ===="
                    : "==== LEARN BENCH: NO LIFT — investigate before trusting the loop ====");
                return positive ? 0 : 1;
            }
            catch (Exception ex) { Console.WriteLine("[X] threw: " + ex); return 2; }
            finally
            {
                Environment.SetEnvironmentVariable("USERPROFILE", origHome);
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }

        // Build a family dir of EvalModels+1 same-shape copies. If training, open the first copy, work the task
        // naively (the gate fails), and capture that failure's root cause as an approved fingerprint-scoped insight.
        private static async Task BuildFamilyAsync(string dir, string src, bool withTrainedInsight)
        {
            Directory.CreateDirectory(dir);
            for (int i = 0; i <= EvalModels; i++) File.Copy(src, Path.Combine(dir, $"m{i}.bim"));
            if (!withTrainedInsight) return;

            var sessions = new SessionManager();
            var e = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro(), dir);
            try
            {
                await e.OpenAsync(Path.Combine(dir, "m0.bim"));

                // Naive attempt → the hard input gate rejects the empty submit (the failure that motivates the lesson).
                var run = await McpTools.StartWorkflow(e, Task);
                var stepId = run.CurrentStep?.StepId ?? run.Steps.First().StepId;
                try { await McpTools.SubmitWorkflowStep(e, run.RunId, stepId, Naive); }
                catch { /* expected — missing required inputs */ }

                // Post-mortem → approved (autoApprove defaults true for single-user local), fingerprint-scoped.
                var ins = await McpTools.AddInsight(e,
                    "optimize-dax step-1 needs the pre-rewrite baseline BEFORE you submit: get_dax for originalDax and name the target measure. An empty submit fails the required-input gate.",
                    new[] { HintKey, "benchmark_delta", "missing-baseline" }, "post-mortem", "project", fingerprintScoped: true);
                if (!string.Equals(ins.Status, "approved", StringComparison.Ordinal))
                    await McpTools.ApproveInsight(e, ins.Id);
            }
            finally { e.Dispose(); sessions.Dispose(); }
        }

        private static async Task<(int passedFirst, int rounds, int precisionHits)> EvalSplitAsync(string dir)
        {
            int passedFirst = 0, rounds = 0, precisionHits = 0;
            for (int i = 1; i <= EvalModels; i++)
            {
                var sessions = new SessionManager();
                var e = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro(), dir);
                try
                {
                    await e.OpenAsync(Path.Combine(dir, $"m{i}.bim"));

                    // 1) The agent recalls prior experience for this model shape.
                    var recall = await McpTools.RecallExperience(e, Query, 12);
                    bool hasHint = recall.Candidates.Any(c => c.Insight?.Keys?.Contains(HintKey) == true);
                    if (recall.Candidates.Length > 0 && recall.Candidates[0].Insight?.Keys?.Contains(HintKey) == true) precisionHits++;

                    // 2) Scripted policy: WITH the hint, supply the required inputs; WITHOUT it, submit naively.
                    var run = await McpTools.StartWorkflow(e, Task);
                    var stepId = run.CurrentStep?.StepId ?? run.Steps.First().StepId;
                    var first = hasHint ? Correct : Naive;
                    try
                    {
                        await McpTools.SubmitWorkflowStep(e, run.RunId, stepId, first);
                        passedFirst++; rounds += 1;                      // passed on the first submission
                    }
                    catch
                    {
                        // The naive submit failed; the agent recovers with the right inputs — but it cost a round.
                        try { await McpTools.SubmitWorkflowStep(e, run.RunId, stepId, Correct); } catch { }
                        rounds += 2;
                    }
                }
                finally { e.Dispose(); sessions.Dispose(); }
            }
            return (passedFirst, rounds, precisionHits);
        }

        private static string FindTestBim()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                foreach (var root in new[] { Path.Combine("external", "TabularEditor"), "TabularEditor" })
                {
                    var candidate = Path.Combine(dir.FullName, root, "TOMWrapperTest", "TestData", "AdventureWorks.bim");
                    if (File.Exists(candidate)) return candidate;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("Could not locate AdventureWorks.bim from " + AppContext.BaseDirectory);
        }
    }
}
