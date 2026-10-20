// The one live batching run for m16/jev-live (Kane, 15 Oct: one live batching run against real Jev). The same 30-table model as the
// measure harness (ManyTablesModel), through the REAL relay on a test licence, two arms each on a fresh engine, service and answer store:
//
//   NEW  the shipped whole-model shape: one Plan build, then Run the plan until nothing is ready.
//   OLD  one request a table (JevPerTableForTest), the Plan build only, so its answers can be set beside the new arm's.
//
// It runs ONLY when SEMANTICUS_JEV_LIVE_LICENCE_FILE names a file holding the licence token; otherwise xunit reports it skipped. The
// token is read into this process for the relay client only and is never printed, returned or written. Results go to
// SEMANTICUS_JEV_LIVE_OUT (a folder outside the repository): calls.json (one row a call), answers.json (one row a question), summary.json.
//
//   SEMANTICUS_JEV_LIVE_LICENCE_FILE=/scratch/lic SEMANTICUS_JEV_LIVE_OUT=/scratch/out \
//     dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevBatchLive"

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class JevBatchLiveFactAttribute : FactAttribute
    {
        public JevBatchLiveFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SEMANTICUS_JEV_LIVE_LICENCE_FILE")))
                Skip = "The live batching run is on demand; set SEMANTICUS_JEV_LIVE_LICENCE_FILE.";
        }
    }

    public sealed class JevBatchLive
    {
        private static readonly JsonSerializerOptions Pretty = new JsonSerializerOptions { WriteIndented = true };

        internal sealed class LiveCall
        {
            public string Arm { get; set; }
            public string Phase { get; set; }
            public string Point { get; set; }
            public int Questions { get; set; }
            public int BodyBytes { get; set; }
            public double StartedAtSeconds { get; set; }
            public double Ms { get; set; }
            public bool Decided { get; set; }
            public string NotDecidedReason { get; set; }
            public int Answered { get; set; }
            public int Rejected { get; set; }
            public int InputTokens { get; set; }
            public string Served { get; set; }
        }

        internal sealed class LiveAnswer
        {
            public string Arm { get; set; }
            public string Phase { get; set; }
            public string Point { get; set; }
            public string Question { get; set; }
            public string Kind { get; set; }
            public string Options { get; set; }
            public string Choice { get; set; }
            public double Confidence { get; set; }
            public string Why { get; set; }   // null when answered; the words when refused or not decided
        }

        /// <summary>The real relay client, wrapped so every call and every question is recorded with its time.</summary>
        private sealed class Recorder : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            public readonly List<LiveCall> Calls = new List<LiveCall>();
            public readonly List<LiveAnswer> Answers = new List<LiveAnswer>();
            public string Arm;
            public string Phase;
            public Recorder(IDecisionOracle inner) { _inner = inner; }
            public bool Available => true;

            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                var snapshotJson = DecisionDigest.CanonicalSnapshotJson(request.Snapshot);
                var call = new LiveCall
                {
                    Arm = Arm, Phase = Phase, Point = request.PointName, Questions = request.Questions.Count,
                    BodyBytes = Encoding.UTF8.GetByteCount(JevDecisionOracle.BuildBody(request, snapshotJson)), StartedAtSeconds = _clock.Elapsed.TotalSeconds,
                };
                var sw = Stopwatch.StartNew();
                DecisionAnswers a = null;
                try { a = await _inner.AskAsync(request, ct).ConfigureAwait(false); return a; }
                finally
                {
                    sw.Stop();
                    call.Ms = sw.Elapsed.TotalMilliseconds;
                    call.Decided = a?.Decided ?? false;
                    call.NotDecidedReason = a == null ? "threw" : a.NotDecidedReason;
                    call.Answered = a?.Answers.Count ?? 0;
                    call.Rejected = a?.Rejected.Count ?? 0;
                    call.InputTokens = a?.InputTokens ?? 0;
                    call.Served = a?.ServedModelVersion;
                    lock (Calls)
                    {
                        Calls.Add(call);
                        foreach (var q in request.Questions)
                        {
                            DecisionAnswer ans = null;
                            var got = a != null && a.Decided && a.Answers.TryGetValue(q.Name, out ans);
                            string why = null;
                            if (!got) why = a == null ? "threw" : a.Rejected.TryGetValue(q.Name, out var r) ? r : (a.NotDecidedReason ?? "no answer and no words");
                            Answers.Add(new LiveAnswer
                            {
                                Arm = Arm, Phase = Phase, Point = request.PointName, Question = q.Name, Kind = q.Kind.ToString(),
                                Options = string.Join("|", q.Options.Select(o => o.Id)),
                                Choice = got ? ans.OptionId : null, Confidence = got ? ans.Confidence : 0, Why = why,
                            });
                        }
                    }
                }
            }
        }

        // jev114: an arm whose name ends in "-de" names the fact tables, their columns and measures in German (multilingual check, Jev 1.14).
        private static readonly Dictionary<string, string> GermanWords = new Dictionary<string, string>
        {
            ["Activity Event"] = "Aktivitätsereignis", ["Capacity Operation"] = "Kapazitätsvorgang", ["Refresh History"] = "Aktualisierungsverlauf",
            ["Duration Ms"] = "Dauer Ms", ["Row Count"] = "Zeilenanzahl", ["CU Seconds"] = "CU Sekunden", ["Bytes"] = "Bytes",
            ["Error Count"] = "Fehleranzahl", ["Throttle Ms"] = "Drosselung Ms", ["Queue Ms"] = "Warteschlange Ms", ["Cost"] = "Kosten",
            ["Timestamp"] = "Zeitstempel", ["Event Id"] = "Ereignis-ID", ["Correlation Id"] = "Korrelations-ID", ["Status Code"] = "Statuscode",
            ["Category"] = "Kategorie", ["Group"] = "Gruppe", ["Label"] = "Bezeichnung", ["Owner"] = "Besitzer", ["Notes"] = "Notizen",
            ["Tier"] = "Stufe", ["Kind"] = "Art", ["Description"] = "Beschreibung",
            [" Avg "] = " Durchschnitt ", [" Count"] = " Anzahl", [" per Row"] = " pro Zeile",
        };
        private static string German(string s) => GermanWords.TryGetValue(s, out var d) ? d : s;

        private static string PlanLines(ModelPlanView plan) => string.Join("\n", plan.Items.Where(i => i.Id.StartsWith("jev.", StringComparison.Ordinal))
            .SelectMany(i => (i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Select(l => i.Id + "|" + l.Op + "|" + l.Target + "|" + l.Property + "|" + l.After))
            .OrderBy(x => x, StringComparer.Ordinal));

        private static async Task<object> ArmAsync(string arm, bool perTable, bool run, int? objects, string licence, string outDir, List<LiveCall> allCalls, List<LiveAnswer> allAnswers)
        {
            var work = Path.Combine(outDir, "work-" + arm);
            Directory.CreateDirectory(work);
            var bim = Path.Combine(work, "many-tables.bim");
            ManyTablesModel.Write(bim, 30, arm.EndsWith("-de", StringComparison.Ordinal) ? German : null);
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.OpenAsync(bim);
            await f.SaveAsAsync("many-tables.bim");
            await f.ConsentAsync();
            var budget = new DecisionBudget();   // the app's own: no ceiling, a counter
            using var relay = JevDecisionOracle.ForRelay(licence, JevDecisionOracle.RelayBaseFromEnvironment(), budget);
            var before = await relay.ReadBudgetAsync(CancellationToken.None);
            var rec = new Recorder(relay) { Arm = arm, Phase = "plan" };
            f.Engine.UseDecisionsForTest(new DecisionService(rec, budget, answers: new DecisionAnswerStore()));
            f.Engine.JevBatchedForTest = _ => true;
            f.Engine.JevPerTableForTest = perTable;
            f.Engine.JevRequestObjectsForTest = objects;

            var clock = Stopwatch.StartNew();
            var seen = -1;
            ModelPlanView plan = null;
            var reads = 0;
            for (var read = 0; read < 20; read++)
            {
                plan = await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                reads++;
                int now; lock (rec.Calls) now = rec.Calls.Count;
                if (now == seen) break;
                seen = now;
            }
            plan = await f.Engine.GetModelPlanAsync();
            clock.Stop();
            var planSeconds = clock.Elapsed.TotalSeconds;
            if (!run)
            {
                // Let the ranking of the settled list land (a plan-only arm makes no change after this, so nothing cancels it).
                for (var i = 0; i < 3; i++) { await f.Engine.PlanRankSettledForTest(); plan = await f.Engine.GetModelPlanAsync(); await f.Engine.JevSettledForTest(); }
                await f.Engine.PlanRankSettledForTest();
            }
            var planLines = PlanLines(plan);
            File.WriteAllText(Path.Combine(outDir, arm + "-plan-lines.txt"), planLines);
            File.WriteAllText(Path.Combine(outDir, arm + "-plan-rows.txt"), string.Join("\n", plan.Items.Select(i => i.Id + " | " + i.Doer + " | " + i.StateWord + " | " + i.Status + " | " + i.Title)));

            double runSeconds = 0; int presses = 0, waves = 0; string sentence = null; string after = null; string leftRows = null;
            if (run)
            {
                rec.Phase = "run";
                clock.Restart();
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
                await f.Engine.JevSettledForTest();
                plan = await f.Engine.GetModelPlanAsync();
                clock.Stop();
                runSeconds = clock.Elapsed.TotalSeconds;
                leftRows = string.Join("\n", plan.Items.Where(i => i.StateWord != PlanStateWords.Done && i.Status != ModelPlanStatuses.Skipped)
                    .Select(i => i.Id + " | " + i.Doer + " | " + i.StateWord + " | " + i.Status + " | held " + i.Held + " | " + i.Title));
                File.WriteAllText(Path.Combine(outDir, arm + "-left-rows.txt"), leftRows);
                var rows = await f.Sessions.Current.ReadAsync(m =>
                    m.Tables.SelectMany(t => t.Columns.Select(c => "c|" + t.Name + "|" + c.Name + "|" + c.DataType + "|sum=" + c.SummarizeBy + "|fmt=" + c.FormatString + "|hidden=" + c.IsHidden + "|folder=" + c.DisplayFolder)
                        .Concat(t.Measures.Select(x => "m|" + t.Name + "|" + x.Name + "|" + x.Expression + "|fmt=" + x.FormatString + "|folder=" + x.DisplayFolder + "|hidden=" + x.IsHidden)))
                      .OrderBy(x => x, StringComparer.Ordinal).ToList());
                after = string.Join("\n", rows);
                File.WriteAllText(Path.Combine(outDir, arm + "-model-after.txt"), after);
                File.WriteAllText(Path.Combine(outDir, arm + "-model-annotations.txt"), await f.Sessions.Current.ReadAsync(m =>
                    string.Join("\n", m.GetAnnotations().Select(n => n + " = " + m.GetAnnotation(n)))));
            }
            var afterBudget = await relay.ReadBudgetAsync(CancellationToken.None);
            lock (rec.Calls) { allCalls.AddRange(rec.Calls); allAnswers.AddRange(rec.Answers); }
            return new
            {
                arm, perTable, reads, planSeconds, planLines = planLines.Split('\n').Length, runSeconds, presses, waves, sentence,
                sessionInputTokens = budget.UsedInputTokens, sessionRequests = budget.Requests,
                relayUsedBefore = before?.UsedInputTokens, relayUsedAfter = afterBudget?.UsedInputTokens,
                relayBeforeReason = before?.Reason, relayAfterReason = afterBudget?.Reason,
            };
        }

        [JevBatchLiveFact]
        public async Task Live_plan_and_run_on_the_real_relay_with_the_per_table_arm_beside_it()
        {
            var licence = File.ReadAllText(Environment.GetEnvironmentVariable("SEMANTICUS_JEV_LIVE_LICENCE_FILE")).Trim();
            var outDir = Environment.GetEnvironmentVariable("SEMANTICUS_JEV_LIVE_OUT") ?? Path.Combine(Path.GetTempPath(), "jev-live");
            Directory.CreateDirectory(outDir);
            var summaries = new List<object>();
            var calls = new List<LiveCall>();
            var answers = new List<LiveAnswer>();
            var armSpec = Environment.GetEnvironmentVariable("SEMANTICUS_JEV_LIVE_ARMS") ?? "new:0:1,old:1:0";
            foreach (var (arm, perTable, run, objects) in armSpec.Split(',').Select(x => x.Split(':')).Select(x => (x[0], x[1] == "1", x[2] == "1", x.Length > 3 ? int.Parse(x[3]) : (int?)null)))
            {
                summaries.Add(await ArmAsync(arm, perTable, run, objects, licence, outDir, calls, answers));
                File.WriteAllText(Path.Combine(outDir, "calls.json"), JsonSerializer.Serialize(calls, Pretty));
                File.WriteAllText(Path.Combine(outDir, "answers.json"), JsonSerializer.Serialize(answers, Pretty));
                File.WriteAllText(Path.Combine(outDir, "summary.json"), JsonSerializer.Serialize(summaries, Pretty));
            }
            Assert.True(calls.Count > 0, "Jev was never asked: the run proved nothing");
        }
    }
}
