// The one live sanity run for M11 join part 4 (K3, Kane 1 Oct): with the four Plan points asking a TABLE at a time, how many requests,
// how many input tokens and how much money does the first Plan read of a real 15-table model cost, against one request per object?
//
// It runs ONLY when JEV_REPLAY_LIVE is exactly "1"; otherwise xunit reports it SKIPPED, never passed. The model is the vendored Tabular
// Editor AdventureWorks (15 tables), opened in Pro with consent, and each arm is a FRESH engine, service and budget (the cache of the first
// arm cannot answer the second). The budget has no ceiling, as the app's own (K1). The key is read from ~/.config/typesafe/env into this
// process only and is never printed or written. Results go outside the repository (~/.cache/jev-plan-sanity).
//
//   JEV_REPLAY_LIVE=1 dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevPlanSanityRun"

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class JevPlanSanityRun
    {
        private readonly ITestOutputHelper _out;
        public JevPlanSanityRun(ITestOutputHelper output) { _out = output; }

        private sealed class Arm
        {
            public string Mode { get; set; }
            public int Tables { get; set; }
            public long Requests { get; set; }
            public long InputTokens { get; set; }
            public double Usd { get; set; }
            public double WallSeconds { get; set; }
            public int Reads { get; set; }
            public int PlanItems { get; set; }
            public int JevLines { get; set; }
            public int Presses { get; set; }
            public int PressesStoppedOnStale { get; set; }
            public int ChangesApplied { get; set; }
            public List<string> StaleNotes { get; set; } = new List<string>();
        }

        private static async Task<Arm> RunArmAsync(JsonElement model, bool batched)
        {
            using var f = await JevAiSchemaReplay.OpenAsync(model);
            if (f == null) throw new InvalidOperationException("The AdventureWorks test model is not in the submodule on this machine.");
            await f.ConsentAsync();
            var budget = new DecisionBudget();   // the app's own: no ceiling, a counter
            using var jev = new JevDecisionOracle(JevAiSchemaReplay.Key(), budget);
            f.Engine.UseDecisionsForTest(new DecisionService(jev, budget, answers: new DecisionAnswerStore()));
            f.Engine.JevBatchedForTest = _ => batched;

            var arm = new Arm { Mode = batched ? "per table" : "per object" };
            var clock = Stopwatch.StartNew();
            ModelPlanView plan = null;
            long last = -1;
            // Each read starts one background run that asks up to its per-run caps; the plan keeps asking on the next read until
            // everything is heard, as a person pressing refresh would. Stop when a read adds no request.
            for (var read = 0; read < 12; read++)
            {
                plan = await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                arm.Reads = read + 1;
                if (budget.Requests == last) break;
                last = budget.Requests;
            }
            plan = await f.Engine.GetModelPlanAsync();
            clock.Stop();

            // E2's evidence: press Run the plan until nothing is ready and count the presses that stopped on a row "worked out before the model
            // last changed". The assistant's rows are set aside, as a person with no assistant would.
            for (var press = 0; press < 12; press++)
            {
                foreach (var asst in plan.Items.Where(i => i.Doer != PlanDoers.Semanticus && i.StateWord != PlanStateWords.Done
                                                           && i.Status != ModelPlanStatuses.Skipped && (i.Id.StartsWith("describe/", StringComparison.Ordinal) || i.Id.StartsWith("words/", StringComparison.Ordinal))))
                    await f.Engine.SkipModelPlanItemAsync(asst.Id, "No assistant here.", "human");
                await f.Engine.JevSettledForTest();
                plan = await f.Engine.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held)) break;
                var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                arm.Presses++;
                if (ran.Performed) arm.ChangesApplied += ran.Applied;
                else if (StoppedOnStale(ran))
                {
                    arm.PressesStoppedOnStale++;
                    // What a person does next: work the stale Semanticus rows out again, then read the plan once it has settled.
                    foreach (var r in plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.ChangeSet?.Stale == true).ToList())
                    {
                        arm.StaleNotes.Add("press " + press + " stale " + r.Id + ": " + r.ChangeSet.StaleReason + " (set at revision " + r.ChangeSet.Revision + ", model at " + f.Sessions.Require().Revision + ")");
                        if (r.Id.StartsWith("jev.", StringComparison.Ordinal)) continue;   // Jev looks again by itself; cancelling its row only empties it
                        await f.Engine.ModelPlanActionAsync(r.Id, "cancel", "human", null);
                        await f.Engine.ModelPlanActionAsync(r.Id, "propose", "human", null);
                        await f.Engine.JevSettledForTest();
                        var again = (await f.Engine.GetModelPlanAsync()).Items.FirstOrDefault(i => i.Id == r.Id);
                        arm.StaleNotes.Add("   after rework: stale=" + again?.ChangeSet?.Stale + " why=" + again?.ChangeSet?.StaleReason + " revision " + again?.ChangeSet?.Revision + ", model at " + f.Sessions.Require().Revision);
                    }
                }
                else break;
            }

            arm.Tables = await f.Sessions.Current.ReadAsync(m => m.Tables.Count);
            arm.Requests = budget.Requests;
            arm.InputTokens = budget.UsedInputTokens;
            arm.Usd = budget.UsedUsd;
            arm.WallSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
            arm.PlanItems = plan.Items.Length;
            arm.JevLines = plan.Items.Where(i => i.Id.StartsWith("jev.", StringComparison.Ordinal)).Sum(i => i.ChangeSet?.Lines?.Length ?? 0);
            return arm;
        }

        private static bool StoppedOnStale(ApplyPlanItemsResult ran) =>
            new[] { ran.Sentence }.Concat((ran.Skipped ?? Array.Empty<ApplyPlanSkip>()).Select(k => k.Reason))
                .Any(t => (t ?? "").Contains("before the model last changed", StringComparison.Ordinal) || (t ?? "").Contains("Jev is looking again", StringComparison.Ordinal));

        [JevReplayFact]
        public async Task A_first_Plan_read_of_a_15_table_model_before_and_after_batching()
        {
            using var labels = JevAiSchemaReplay.Labels();
            var model = labels.RootElement.GetProperty("models").EnumerateArray().First(m => m.GetProperty("id").GetString() == "adventureworks");

            var before = await RunArmAsync(model, batched: false);
            var after = await RunArmAsync(model, batched: true);

            var outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "jev-plan-sanity");
            Directory.CreateDirectory(outDir);
            var json = JsonSerializer.Serialize(new { before, after }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(outDir, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json"), json);
            _out.WriteLine(json);

            Assert.True(before.Requests > 0, "Jev was never asked: the run proved nothing");
            Assert.True(after.Requests < before.Requests, "asking a table at a time did not cut the requests");
        }

        /// <summary>E2 (M11 join part 4): on a real 15-table model with no Jev, press Run the plan until it is quiet; whenever a press stops
        /// on a row "worked out before the model last changed", work that row out again the way a person does. A row worked out again
        /// must read current, and the next press must take it.</summary>
        [Fact]
        public async Task A_Semanticus_row_worked_out_again_after_a_press_is_current_and_the_next_press_takes_it()
        {
            using var labels = JevAiSchemaReplay.Labels();
            var model = labels.RootElement.GetProperty("models").EnumerateArray().First(m => m.GetProperty("id").GetString() == "adventureworks");
            using var f = await JevAiSchemaReplay.OpenAsync(model);
            Assert.NotNull(f);
            f.UseNull();
            var trail = new System.Text.StringBuilder();
            ModelPlanView plan = await f.Engine.GetModelPlanAsync();
            for (var press = 0; press < 14; press++)
            {
                foreach (var asst in plan.Items.Where(i => i.Doer != PlanDoers.Semanticus && i.StateWord != PlanStateWords.Done
                                                           && i.Status != ModelPlanStatuses.Skipped && (i.Id.StartsWith("describe/", StringComparison.Ordinal) || i.Id.StartsWith("words/", StringComparison.Ordinal))))
                    await f.Engine.SkipModelPlanItemAsync(asst.Id, "No assistant here.", "human");
                plan = await f.Engine.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held)) break;
                var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                trail.AppendLine("press " + press + ": " + ran.Performed + " " + ran.Sentence);
                if (ran.Performed) continue;
                var stale = plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && i.ChangeSet?.Stale == true).ToList();
                Assert.True(stale.Count > 0, "a press that did nothing named no stale row:\n" + trail);
                foreach (var r in stale)
                {
                    await f.Engine.ModelPlanActionAsync(r.Id, "cancel", "human", null);
                    var again = await f.Engine.ModelPlanActionAsync(r.Id, "propose", "human", null);
                    var now = again.Plan?.Items.FirstOrDefault(i => i.Id == r.Id);
                    trail.AppendLine("  reworked " + r.Id + ": stale=" + now?.ChangeSet?.Stale + " why=" + now?.ChangeSet?.StaleReason + " (set at " + now?.ChangeSet?.Revision + ", model at " + f.Sessions.Require().Revision + ")");
                    Assert.True(now?.ChangeSet?.Stale != true, "a row worked out again still reads stale:\n" + trail);
                }
                plan = await f.Engine.GetModelPlanAsync();
            }
            _out.WriteLine(trail.ToString());
        }
    }
}
