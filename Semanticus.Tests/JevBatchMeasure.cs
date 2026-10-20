// The measurement run for m16/jev-batch: how many Jev calls, how many questions and tokens in each, and how long, for ONE Plan read and
// ONE "Run the plan" of a 30-table model, through the real engine paths and a fake relay that charges a realistic latency per call.
//
// It runs only when SEMANTICUS_JEV_MEASURE names an output file (otherwise xunit reports it skipped):
//
//   SEMANTICUS_JEV_MEASURE=/path/out.md [SEMANTICUS_JEV_TABLES=30] [SEMANTICUS_JEV_BASE_MS=1300] [SEMANTICUS_JEV_SCALE=1] \
//     dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevBatchMeasure"
//
// Latency: hero22 (14 Oct, real relay, 30-table model, 206 calls) measured 1.25 to 2.85 s a call with a median of about 1.4 s for up to 30
// questions; Jev's own guidance is about 1 ms a question. The default is 1300 ms a call plus 1 ms a question.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class JevBatchMeasureFactAttribute : FactAttribute
    {
        public JevBatchMeasureFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SEMANTICUS_JEV_MEASURE")))
                Skip = "The batching measurement run is on demand; set SEMANTICUS_JEV_MEASURE to an output file.";
        }
    }

    public sealed class JevBatchMeasure
    {
        private static double Env(string name, double fallback) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

        internal sealed class PhaseResult
        {
            public string Phase;
            public double WallSeconds;
            public int Presses;
            public int Waves;
            public string Sentence;
            public string Digest;   // what the model looks like when the phase ends, so two runs can be shown to have done the same work
            public List<string> Timeline = new List<string>();
            public List<RelayCall> Calls;
        }

        internal static async Task<List<PhaseResult>> RunAsync(int tables, double baseMs, double scale, string workDir)
        {
            Directory.CreateDirectory(workDir);
            var bim = Path.Combine(workDir, "many-tables.bim");
            ManyTablesModel.Write(bim, tables);
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.OpenAsync(bim);
            await f.SaveAsAsync("many-tables.bim");
            await f.ConsentAsync();
            var relay = new CountingRelay { BaseLatencyMs = baseMs, PerQuestionMs = 1, Scale = scale };
            f.Engine.UseDecisionsForTest(new DecisionService(relay, new DecisionBudget(), answers: new DecisionAnswerStore()));
            f.Engine.JevBatchedForTest = _ => true;

            var results = new List<PhaseResult>();

            // Phase 1: the Plan is built. Each read starts one background run; keep reading until a read asks nothing new.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var seen = -1;
            ModelPlanView plan = null;
            for (var read = 0; read < 20; read++)
            {
                plan = await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                var now = relay.Calls.Count;
                if (now == seen) break;
                seen = now;
            }
            plan = await f.Engine.GetModelPlanAsync();
            clock.Stop();
            results.Add(new PhaseResult { Phase = "Plan build", WallSeconds = clock.Elapsed.TotalSeconds / scale, Calls = relay.Calls.ToList(), Digest = PlanDigest(plan) });

            // Phase 2: Run the plan (press until nothing is ready), the assistant's rows set aside as a person with no assistant would.
            relay.Reset();
            var timeline = new List<string>();
            Action<PlanRunProgress> tap = v => { lock (timeline) timeline.Add("wave " + v.Wave + " " + v.Phase + " " + (v.ItemIds?.Length ?? 0) + " rows " + (v.Phase == "apply" || v.Phase == "done" ? string.Join(",", v.ItemIds ?? new string[0]) : "") + (v.WaitedSeconds.HasValue ? " waited " + v.WaitedSeconds + " s" : "") + " at " + v.Ms + " ms"); };
            f.Sessions.Bus.PlanRunProgressed += tap;
            clock.Restart();
            var presses = 0;
            var waves = 0;
            string sentence = null;
            for (var press = 0; press < 40; press++)
            {
                foreach (var asst in plan.Items.Where(i => i.Doer != PlanDoers.Semanticus && i.StateWord != PlanStateWords.Done
                                                           && i.Status != ModelPlanStatuses.Skipped && (i.Id.StartsWith("describe/", StringComparison.Ordinal) || i.Id.StartsWith("words/", StringComparison.Ordinal))))
                    await f.Engine.SkipModelPlanItemAsync(asst.Id, "No assistant here.", "human");
                await f.Engine.JevSettledForTest();
                plan = await f.Engine.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held)) break;
                var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                if (!ran.Performed) break;
                presses++;
                waves += ran.Waves;
                sentence = ran.Sentence;
                await f.Engine.JevSettledForTest();
            }
            clock.Stop();
            f.Sessions.Bus.PlanRunProgressed -= tap;
            results.Add(new PhaseResult { Phase = "Run the plan", WallSeconds = clock.Elapsed.TotalSeconds / scale, Presses = presses, Waves = waves, Sentence = sentence, Timeline = timeline.ToList(), Calls = relay.Calls.ToList(), Digest = await ModelDigestAsync(f) });
            return results;
        }

        /// <summary>A short hash of every line Jev put on the plan (row, kind, target, value), in a fixed order.</summary>
        private static string PlanDigest(ModelPlanView plan)
        {
            var lines = plan.Items.Where(i => i.Id.StartsWith("jev.", StringComparison.Ordinal))
                .SelectMany(i => (i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Select(l => i.Id + "|" + l.Op + "|" + l.Target + "|" + l.Property + "|" + l.After))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            return lines.Count.ToString(CultureInfo.InvariantCulture) + " lines, " + Hash(string.Join("\n", lines));
        }

        /// <summary>A short hash of what the model looks like: every column's and measure's name, formula, format, folder, summarize and hidden setting.</summary>
        private static async Task<string> ModelDigestAsync(DecisionFixture f)
        {
            var rows = await f.Sessions.Current.ReadAsync(m =>
                m.Tables.SelectMany(t => t.Columns.Select(c => "c|" + t.Name + "|" + c.Name + "|" + c.SummarizeBy + "|" + c.FormatString + "|" + c.IsHidden + "|" + c.DisplayFolder)
                    .Concat(t.Measures.Select(x => "m|" + t.Name + "|" + x.Name + "|" + x.Expression + "|" + x.FormatString + "|" + x.DisplayFolder + "|" + x.IsHidden)))
                  .OrderBy(x => x, StringComparer.Ordinal).ToList());
            return rows.Count.ToString(CultureInfo.InvariantCulture) + " objects, " + Hash(string.Join("\n", rows));
        }

        private static string Hash(string text)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Take(6).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        internal static string Table(string title, int tables, double baseMs, IReadOnlyList<PhaseResult> results)
        {
            const double UsdPerToken = 0.042 / 1_000_000;
            var sb = new StringBuilder();
            sb.AppendLine("### " + title + " (" + tables.ToString(CultureInfo.InvariantCulture) + " tables, latency " + baseMs.ToString("0", CultureInfo.InvariantCulture) + " ms + 1 ms a question)");
            foreach (var r in results)
            {
                sb.AppendLine();
                sb.AppendLine("**" + r.Phase + "**: " + (r.Presses > 0 ? r.Presses + " presses, " + r.Waves + " waves, " : "") + r.Calls.Count.ToString(CultureInfo.InvariantCulture) + " calls, "
                    + r.Calls.Sum(c => c.Questions).ToString(CultureInfo.InvariantCulture) + " questions, wall "
                    + r.WallSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s, "
                    + (r.Calls.Sum(c => (long)c.BodyBytes) / 4).ToString("N0", CultureInfo.InvariantCulture) + " input tokens (bytes / 4), USD "
                    + (r.Calls.Sum(c => (long)c.BodyBytes) / 4 * UsdPerToken).ToString("0.00000", CultureInfo.InvariantCulture));
                if (r.Sentence != null) { sb.AppendLine(); sb.AppendLine("Receipt: " + r.Sentence); }
                if (r.Digest != null) { sb.AppendLine(); sb.AppendLine((r.Phase == "Plan build" ? "Jev lines on the plan: " : "Model after the run: ") + r.Digest); }
                if (r.Timeline.Count > 0) { sb.AppendLine(); sb.AppendLine("Timeline:"); foreach (var line in r.Timeline) sb.AppendLine("- " + line); }
                sb.AppendLine();
                sb.AppendLine("| point | calls | questions | questions per call (avg / max) | body KB per call (avg / max) | input tokens (bytes/4) |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var g in r.Calls.GroupBy(c => c.Point).OrderBy(g => g.Key, StringComparer.Ordinal))
                    sb.AppendLine("| " + g.Key + " | " + g.Count() + " | " + g.Sum(c => c.Questions) + " | "
                        + g.Average(c => c.Questions).ToString("0", CultureInfo.InvariantCulture) + " / " + g.Max(c => c.Questions) + " | "
                        + (g.Average(c => c.BodyBytes) / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " / " + (g.Max(c => c.BodyBytes) / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " | "
                        + (g.Sum(c => (long)c.BodyBytes) / 4).ToString("N0", CultureInfo.InvariantCulture) + " |");
            }
            return sb.ToString();
        }

        [JevBatchMeasureFact]
        public async Task Measure_one_plan_read_and_one_run_of_a_many_table_model()
        {
            var output = Environment.GetEnvironmentVariable("SEMANTICUS_JEV_MEASURE");
            var tables = (int)Env("SEMANTICUS_JEV_TABLES", 30);
            var baseMs = Env("SEMANTICUS_JEV_BASE_MS", 1300);
            var scale = Env("SEMANTICUS_JEV_SCALE", 1);
            var results = await RunAsync(tables, baseMs, scale, Path.Combine(Path.GetTempPath(), "jev-measure-" + Guid.NewGuid().ToString("N")));
            var text = Table(Environment.GetEnvironmentVariable("SEMANTICUS_JEV_TITLE") ?? "Measured", tables, baseMs, results);
            File.WriteAllText(output, text);
            Assert.True(results[0].Calls.Count > 0, "Jev was never asked: the run proved nothing");
        }
    }
}
