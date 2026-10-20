// Verifier attacks on m16/jev-batch (lane v-jev-batch, 15 Oct 2026). Each test tries to break one claim of the lane: the packing stays
// inside every limit at the edges, an answer goes back to the object it was asked about (same column names in many tables), a bad or extra
// answer loses only itself, the base-measure rows' footprint still meets every row it must, and a new client on the old relay still gets
// its answers. A test that fails here proves a defect; it is not a fix.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifyJevBatchAttackTests
    {
        // ---- a relay that can spoil, add and refuse ----------------------------------------------------------------------

        private sealed class TamperRelay : IDecisionOracle
        {
            public Func<DecisionQuestion, string> Choose;
            public Func<DecisionQuestion, bool> Spoil;
            public Func<DecisionRequest, IEnumerable<KeyValuePair<string, string>>> Extra;
            public int RefuseOver;
            public int Refused;
            /// <summary>Behave like the live relay b2d99092: its per-point question bounds, its 64 KB state and 192 KB body limits, in its own words.</summary>
            public bool Old;
            /// <summary>Refuse every request as a state that is too big (a refusal that fewer questions can never cure).</summary>
            public bool RefuseAllAsState;
            public static readonly Dictionary<string, int> OldBounds = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["measure.folder_and_format"] = 30, ["plan.rank"] = 40, ["compose.draft"] = 64, ["query.summarize"] = 24, ["column.format"] = 30,
                ["ai.schema"] = 30, ["column.summarize"] = 30, ["cleanup.suggest"] = 40,
            };
            public string DumpDir;
            private int _dumped;
            public readonly List<(string Point, int Questions, int Bytes, List<string> Names)> Answered = new List<(string, int, int, List<string>)>();
            public bool Available => true;

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                var snapshotJson = DecisionDigest.CanonicalSnapshotJson(request.Snapshot);
                if (DumpDir != null)
                {
                    var n = Interlocked.Increment(ref _dumped);
                    File.WriteAllText(Path.Combine(DumpDir, n.ToString("0000") + ".json"), JevDecisionOracle.BuildRelayBody(request, snapshotJson, "k" + n), new UTF8Encoding(false));
                }
                string oldWhy = null;
                if (RefuseAllAsState) oldWhy = "That model detail is too big to send.";
                else if (Old)
                {
                    var bound = OldBounds.TryGetValue(request.PointName, out var ob) ? ob : 30;
                    if (Encoding.UTF8.GetByteCount(JevDecisionOracle.BuildRelayBody(request, snapshotJson, "k")) > 196_608) oldWhy = "That request is too big to send.";
                    else if (request.Questions.Count > bound) oldWhy = "That request asks too much at once.";
                    else if (Encoding.UTF8.GetByteCount(snapshotJson) > 65_536) oldWhy = "That model detail is too big to send.";
                }
                if (oldWhy != null)
                {
                    Interlocked.Increment(ref Refused);
                    return Task.FromResult(DecisionAnswers.NotDecided(request, oldWhy));
                }
                if (RefuseOver > 0 && request.Questions.Count > RefuseOver)
                {
                    Interlocked.Increment(ref Refused);
                    return Task.FromResult(DecisionAnswers.NotDecided(request, JevDecisionOracle.TooBigSentences[0]));
                }
                lock (Answered) Answered.Add((request.PointName, request.Questions.Count, Encoding.UTF8.GetByteCount(JevDecisionOracle.BuildBody(request, snapshotJson)), request.Questions.Select(q => q.Name).ToList()));
                var parts = new List<string>();
                foreach (var q in request.Questions)
                {
                    string v;
                    if (Spoil != null && Spoil(q)) v = "{\"type\":\"choice\",\"choice\":\"bogus-option\",\"confidence\":0.95}";
                    else if (q.Kind == DecisionQuestionKind.Choice) v = "{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(Choose?.Invoke(q) ?? q.Options[0].Id) + ",\"confidence\":0.95}";
                    else if (q.Kind == DecisionQuestionKind.Noul) v = "{\"type\":\"noul\",\"noul\":0.9}";
                    else
                    {
                        var legend = string.Join(",", q.Options.Select((o, i) => "\"" + i + "\":" + JsonSerializer.Serialize(o.Criterion)));
                        var probs = string.Join(",", q.Options.Select((o, i) => "\"" + i + "\":" + (i == 2 ? "0.9" : "0.025")));
                        v = "{\"type\":\"score\",\"score\":2.0,\"confidence\":0.9,\"legend\":{" + legend + "},\"probabilities\":{" + probs + "}}";
                    }
                    parts.Add(JsonSerializer.Serialize(q.Name) + ":" + v);
                }
                if (Extra != null) foreach (var kv in Extra(request)) parts.Add(JsonSerializer.Serialize(kv.Key) + ":" + kv.Value);
                using var doc = JsonDocument.Parse("{" + string.Join(",", parts) + "}");
                return Task.FromResult(DecisionAnswerReader.Read(request, doc.RootElement.Clone(), "jev-test-1", 100, 5));
            }
        }

        private static async Task<DecisionFixture> OpenAsync(int tables, IDecisionOracle relay, Func<string, string> rewrite = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), "vjb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "many.bim");
            ManyTablesModel.Write(bim, tables);
            if (rewrite != null) File.WriteAllText(bim, rewrite(File.ReadAllText(bim)), new UTF8Encoding(false));
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.OpenAsync(bim);
            await f.SaveAsAsync("many.bim");
            await f.ConsentAsync();
            f.Engine.UseDecisionsForTest(new DecisionService(relay, new DecisionBudget(), answers: new DecisionAnswerStore()));
            f.Engine.JevBatchedForTest = _ => true;
            return f;
        }

        private static async Task<ModelPlanView> ReadUntilQuietAsync(DecisionFixture f, Func<int> calls)
        {
            var seen = -1;
            for (var read = 0; read < 12; read++)
            {
                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();
                if (calls() == seen) break;
                seen = calls();
            }
            return await f.Engine.GetModelPlanAsync();
        }

        private static List<ModelPlanChangeLine> Lines(ModelPlanView plan, string family) =>
            plan.Items.Where(i => i.Id.StartsWith("jev." + family + ".", StringComparison.Ordinal))
                .SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).ToList();

        private static int Hash(string s) { var h = 17; foreach (var c in s) h = unchecked(h * 31 + c); return h & 0x7fffffff; }

        // The ref an object question is about: "summarize_by:column:Sales/Amount" -> "column:Sales/Amount".
        private static string RefOf(string questionName) => questionName.Substring(questionName.IndexOf(':') + 1);

        // ---- 1. the packing at the edges ------------------------------------------------------------------------------------

        private static TableBatch<string> Build(IReadOnlyList<string> chunk, Func<string, int> bytesOf, int questionsEach = 1)
        {
            var pt = DecisionRegistry.Require(DecisionRegistry.ColumnSummarize);
            var b = new TableBatch<string> { TableRef = "table:" + chunk[0].Split('/')[0], Asks = chunk.ToList() };
            var questions = new List<DecisionQuestion>();
            for (var i = 0; i < chunk.Count; i++)
            {
                var names = new List<KeyValuePair<string, string>>();
                for (var k = 0; k < questionsEach; k++)
                {
                    var name = "summarize_by" + (k == 0 ? "" : k.ToString()) + ":column:" + chunk[i];
                    questions.Add(new DecisionQuestion(name, DecisionQuestionKind.Choice, "For the column `columns[" + i + "]`: how?",
                        new[] { new DecisionOption("sum", "totals mean something"), new DecisionOption("none", "totals mean nothing") }));
                    names.Add(new KeyValuePair<string, string>(name, "summarize_by"));
                }
                b.Names[chunk[i]] = names;
            }
            b.Questions = questions;
            b.Snapshot = DecisionSnapshot.Build(pt, new[]
            {
                new KeyValuePair<string, object>("columns", chunk.Select(a => (object)new Dictionary<string, object> { { "name", a }, { "text", new string('x', bytesOf(a)) } }).ToList()),
            });
            return b;
        }

        private static void AssertInsideEveryLimit(IReadOnlyList<TableBatch<string>> batches, IReadOnlyList<string> asks, bool allowLoneOversize = false)
        {
            var point = DecisionRegistry.Require(DecisionRegistry.ColumnSummarize);
            Assert.Equal(asks.OrderBy(a => a, StringComparer.Ordinal), batches.SelectMany(b => b.Asks).OrderBy(a => a, StringComparer.Ordinal));   // every ask exactly once
            foreach (var b in batches)
            {
                var json = DecisionDigest.CanonicalSnapshotJson(b.Snapshot);
                var relayBody = JevDecisionOracle.BuildRelayBody(b.RequestFor(point), json, Guid.NewGuid().ToString("N"));
                var lone = b.Asks.Count == 1 && allowLoneOversize;
                if (lone) continue;
                Assert.True(Encoding.UTF8.GetByteCount(json) <= JevTableBatch.MaxSnapshotBytes, "state " + Encoding.UTF8.GetByteCount(json));
                Assert.True(Encoding.UTF8.GetByteCount(JevDecisionOracle.BuildBody(b.RequestFor(point), json)) <= JevTableBatch.MaxBodyBytes);
                Assert.True(Encoding.UTF8.GetByteCount(relayBody) <= 196_608, "relay body " + Encoding.UTF8.GetByteCount(relayBody));
                Assert.True(b.Questions.Count <= 400, "questions " + b.Questions.Count);
            }
        }

        [Fact]
        public void Attack_pack_one_huge_table()
        {
            var asks = Enumerable.Range(0, 3000).Select(i => "Huge/c" + i).ToList();
            var batches = JevTableBatch.Pack(asks, a => a.Split('/')[0], c => Build(c, _ => 150), DecisionRegistry.Require(DecisionRegistry.ColumnSummarize));
            AssertInsideEveryLimit(batches, asks);
            Assert.True(batches.Count < 40, "one huge table split into " + batches.Count);
        }

        [Fact]
        public void Attack_pack_many_tiny_tables_and_more_than_400_short_questions()
        {
            var asks = Enumerable.Range(0, 2500).Select(i => "T" + i + "/c").ToList();
            var batches = JevTableBatch.Pack(asks, a => a.Split('/')[0], c => Build(c, _ => 0), DecisionRegistry.Require(DecisionRegistry.ColumnSummarize));
            AssertInsideEveryLimit(batches, asks);
            Assert.Contains(batches, b => b.Questions.Count == 400);   // the question bound bites, exactly
            // two questions an object: 450 objects is 900 questions, never more than 400 in one request
            var two = Enumerable.Range(0, 450).Select(i => "T" + i + "/c").ToList();
            var b2 = JevTableBatch.Pack(two, a => a.Split('/')[0], c => Build(c, _ => 0, 2), DecisionRegistry.Require(DecisionRegistry.ColumnSummarize));
            AssertInsideEveryLimit(b2, two);
        }

        [Theory]
        [InlineData(-40)]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(40)]
        public void Attack_pack_a_state_just_under_and_just_over_60_KB(int delta)
        {
            // find the text size at which ONE ask's state is exactly MaxSnapshotBytes, then move it by delta
            int StateOf(int n) => Encoding.UTF8.GetByteCount(DecisionDigest.CanonicalSnapshotJson(Build(new[] { "A/1" }, _ => n).Snapshot));
            var at = JevTableBatch.MaxSnapshotBytes - StateOf(0);
            Assert.Equal(JevTableBatch.MaxSnapshotBytes, StateOf(at));
            var asks = new[] { "A/0", "A/1", "B/2" };
            var batches = JevTableBatch.Pack(asks, a => a.Split('/')[0], c => Build(c, a => a == "A/1" ? at + delta : 10), DecisionRegistry.Require(DecisionRegistry.ColumnSummarize));
            AssertInsideEveryLimit(batches, asks, allowLoneOversize: delta > 0);
            if (delta >= -1) Assert.Contains(batches, b => b.Asks.SequenceEqual(new[] { "A/1" }));   // at or over the line, the big one never shares a request
            // and two halves of 30 KB share one request only while together they stay inside
            var halves = JevTableBatch.Pack(new[] { "A/0", "A/1" }, a => a.Split('/')[0], c => Build(c, _ => at / 2 + delta), DecisionRegistry.Require(DecisionRegistry.ColumnSummarize));
            AssertInsideEveryLimit(halves, new[] { "A/0", "A/1" });
        }

        [Fact]
        public void Attack_pack_unicode_and_quotes_in_names_stays_inside_the_relay_measure()
        {
            // the relay measures new TextEncoder().encode(JSON.stringify(snapshot)); an escape in the engine's JSON must never make the engine read less
            var weird = new[] { "Ventes \"Année\"", "日本語の表", "Emoji 😀🚀", "Back\\slash", "Tab\tNew\nLine", "Ünïcødé «q»", "<&'>" };
            var asks = Enumerable.Range(0, 600).Select(i => weird[i % weird.Length] + i / 50 + "/" + weird[(i * 3) % weird.Length] + " " + i).ToList();
            var batches = JevTableBatch.Pack(asks, a => a.Split('/')[0], c => Build(c, _ => 120), DecisionRegistry.Require(DecisionRegistry.ColumnSummarize));
            AssertInsideEveryLimit(batches, asks);
            // the relay's own measure (JSON.stringify) is checked in node on these bodies: the file name carries the engine's state bytes
            var dump = Environment.GetEnvironmentVariable("VJB_DUMP");
            if (!string.IsNullOrEmpty(dump))
            {
                Directory.CreateDirectory(dump);
                var point = DecisionRegistry.Require(DecisionRegistry.ColumnSummarize);
                for (var i = 0; i < batches.Count; i++)
                {
                    var json = DecisionDigest.CanonicalSnapshotJson(batches[i].Snapshot);
                    File.WriteAllText(Path.Combine(dump, "pack-" + i + "-" + Encoding.UTF8.GetByteCount(json) + ".json"),
                        JevDecisionOracle.BuildRelayBody(batches[i].RequestFor(point), json, "p" + i), new UTF8Encoding(false));
                }
            }
        }

        // ---- 2. answers go back to the right object ---------------------------------------------------------------------

        private static readonly string[] Aggs = { "max", "min", "count", "distinctcount" };
        private static string AggFor(string colRef) => Aggs[Hash(colRef) % Aggs.Length];
        private static string FormatFor(string colRef) => Hash(colRef) % 2 == 0 ? "percent" : "whole_number";

        [Fact]
        public async Task Attack_same_column_names_in_many_tables_each_answer_lands_on_its_own_column()
        {
            var relay = new TamperRelay
            {
                Choose = q =>
                {
                    if (q.Name.StartsWith("summarize_by:", StringComparison.Ordinal)) return AggFor(RefOf(q.Name));
                    if (q.Name.StartsWith("format_family:column:", StringComparison.Ordinal)) return FormatFor(RefOf(q.Name));
                    return q.Options[0].Id;
                },
            };
            using var f = await OpenAsync(30, relay);
            var plan = await ReadUntilQuietAsync(f, () => relay.Answered.Count);

            // the three fact tables share every number column name ("Duration Ms", "Cost", ...): all in ONE request
            var summarizeCalls = relay.Answered.Where(a => a.Point == "column.summarize").ToList();
            var asked = string.Join(" | ", summarizeCalls.SelectMany(c => c.Names));
            Assert.True(summarizeCalls.Count == 1, summarizeCalls.Count + " summarize calls: " + asked);
            var twins = summarizeCalls[0].Names.Select(RefOf).GroupBy(r => r.Substring(r.IndexOf('/') + 1)).Where(g => g.Count() > 1).ToList();
            Assert.True(twins.Count >= 3, "fewer than three column names asked in more than one table: " + asked);

            var creates = Lines(plan, ColumnSummarizeFamily.Name).Where(l => l.Op == "create_measure").ToList();
            Assert.True(creates.Count >= 20, "too few base measures to judge: " + creates.Count + " lines: " + string.Join(" ; ", Lines(plan, ColumnSummarizeFamily.Name).Select(l => l.Op + " " + l.Target + " " + l.After).Take(12)));
            var fn = new Dictionary<string, string> { ["average"] = "AVERAGE", ["max"] = "MAX", ["min"] = "MIN", ["count"] = "COUNT", ["distinctcount"] = "DISTINCTCOUNT" };
            var wrong = new List<string>();
            foreach (var l in creates)
            {
                var m = Regex.Match(l.After ?? "", @"^\s*([A-Z]+)\s*\(\s*'([^']+)'\[([^\]]+)\]");
                if (!m.Success) { wrong.Add("formula? " + l.Target + " = " + l.After); continue; }
                var col = "column:" + m.Groups[2].Value + "/" + m.Groups[3].Value;
                if (fn[AggFor(col)] != m.Groups[1].Value) wrong.Add(col + " wanted " + fn[AggFor(col)] + " got " + l.After);   // Jev's answer for THIS column, not its twin's
                if (!(l.Target ?? "").StartsWith("measure:" + m.Groups[2].Value + "/", StringComparison.Ordinal)) wrong.Add("table? " + l.Target + " = " + l.After);
            }
            Assert.True(wrong.Count == 0, wrong.Count + " of " + creates.Count + " base measures do not match: " + string.Join(" ; ", wrong.Take(8)));

            var formats = Lines(plan, ColumnFormatFamily.Name).Where(l => l.Op == "set_format_string").ToList();
            Assert.True(formats.Count >= 12, "too few format lines to judge: " + formats.Count);
            foreach (var l in formats)
                Assert.True((l.After ?? "").Contains('%') == (FormatFor(l.Target) == "percent"), l.Target + " got " + l.After);
        }

        [Fact]
        public async Task Attack_a_spoiled_answer_and_extra_answers_lose_only_themselves_and_write_nothing()
        {
            const string spoiled = "column:Capacity Operation/Capacity Key";
            var relay = new TamperRelay
            {
                Choose = q => q.Name.StartsWith("summarize_by:", StringComparison.Ordinal) ? "max" : q.Options[0].Id,
                Spoil = q => q.Name == "summarize_by:" + spoiled,
                Extra = r => r.PointName == "column.summarize"
                    ? new[]
                    {
                        new KeyValuePair<string, string>("summarize_by:column:Capacity Operation/Ghost", "{\"type\":\"choice\",\"choice\":\"min\",\"confidence\":0.99}"),
                        new KeyValuePair<string, string>("summarize_by:column:Workspace/Category", "{\"type\":\"choice\",\"choice\":\"min\",\"confidence\":0.99}"),
                        new KeyValuePair<string, string>("summarize_by", "{\"type\":\"choice\",\"choice\":\"min\",\"confidence\":0.99}"),
                    }
                    : Array.Empty<KeyValuePair<string, string>>(),
            };
            using var f = await OpenAsync(30, relay);
            var plan = await ReadUntilQuietAsync(f, () => relay.Answered.Count);

            var lines = Lines(plan, ColumnSummarizeFamily.Name);
            var all = string.Join(" ; ", lines.Select(l => l.Op + " " + l.Target + " " + l.After));
            // the spoiled answer is not Jev's: that column falls to the rule (by design), so it never gets Jev's MAX
            Assert.False(lines.Any(l => Regex.IsMatch(l.After ?? "", @"^MAX\s*\(\s*'Capacity Operation'\[Capacity Key\]")), "spoiled answer used: " + all);
            // extra answers for objects never asked write nothing
            Assert.False(lines.Any(l => (l.Target ?? "").Contains("Ghost") || (l.After ?? "").Contains("Ghost")), "ghost: " + all);
            Assert.False(lines.Any(l => (l.Target ?? "").Contains("Workspace/Category") || (l.After ?? "").Contains("'Workspace'[Category]")), "workspace: " + all);
            Assert.False(lines.Any(l => (l.After ?? "").StartsWith("MIN", StringComparison.Ordinal)), "an extra answer was used: " + all);
            // the rest of the same request is used: the twin column in another table and a sibling in the same table
            Assert.True(lines.Any(l => Regex.IsMatch(l.After ?? "", @"^MAX\s*\(\s*'Activity Event'\[Capacity Key\]")) && lines.Any(l => Regex.IsMatch(l.After ?? "", @"^MAX\s*\(\s*'Capacity'\[Capacity Key\]")), "twin lost: " + all);
            Assert.True(lines.Any(l => Regex.IsMatch(l.After ?? "", @"^MAX\s*\(\s*'Capacity Operation'\[Capacity Operation Key\]")), "sibling lost: " + all);
            // the spoiled question was really asked (so the check above is not empty)
            Assert.Contains(relay.Answered.Where(a => a.Point == "column.summarize").SelectMany(a => a.Names), n => n == "summarize_by:" + spoiled);
        }

        // ---- 4. a new client on the old relay (30 questions a request) -------------------------------------------------------

        private static async Task<HashSet<string>> MeasuresAnsweredAsync(int refuseOver, bool withAFolder = false)
        {
            var relay = new TamperRelay { RefuseOver = refuseOver };
            // one measure in a display folder: every other measure is then asked a folder AND a format question (MeasureDefaultsPoint.Questions)
            using var f = await OpenAsync(10, relay, withAFolder ? json => json.Replace("\"name\": \"KPI 01\",", "\"name\": \"KPI 01\", \"displayFolder\": \"Costs\",") : null);
            await ReadUntilQuietAsync(f, () => relay.Answered.Count + relay.Refused);
            return new HashSet<string>(relay.Answered.Where(a => a.Point == "measure.folder_and_format").SelectMany(a => a.Names).Select(RefOf), StringComparer.Ordinal);
        }

        [Fact]
        public async Task Control_on_the_new_relay_every_fact_measure_is_asked()
        {
            var answered = await MeasuresAnsweredAsync(0);
            Assert.True(answered.Count(r => r.StartsWith("measure:Activity Event/", StringComparison.Ordinal)) >= 20, string.Join(", ", answered));
        }

        [Fact]
        public async Task Attack_old_relay_fallback_still_gets_the_measures_of_a_table_with_more_than_15_measures()
        {
            // The old relay serves measure.folder_and_format at 30 questions a request (two a measure, so 15 measures, as main did).
            var control = await MeasuresAnsweredAsync(0);
            var answered = await MeasuresAnsweredAsync(30);
            var lost = control.Where(r => !answered.Contains(r)).ToList();
            Assert.True(lost.Count == 0, lost.Count + " measures never got an answer on the old relay, e.g. " + string.Join(", ", lost.Take(5)));
        }

        [Fact]
        public async Task Attack_old_relay_fallback_when_the_model_has_a_folder_two_questions_a_measure()
        {
            var control = await MeasuresAnsweredAsync(0, withAFolder: true);
            Assert.True(control.Count(r => r.StartsWith("measure:Activity Event/", StringComparison.Ordinal)) >= 20, "control: " + string.Join(", ", control));
            var answered = await MeasuresAnsweredAsync(30, withAFolder: true);
            var lost = control.Where(r => !answered.Contains(r)).ToList();
            Assert.True(lost.Count == 0, lost.Count + " of " + control.Count + " measures never got an answer on the old relay (30 questions a request), e.g. " + string.Join(", ", lost.Take(5)));
        }

        // ---- re-check of the fix (f5c50277): a faithful old relay drops nothing, end to end ------------------------------------

        private static async Task<(Dictionary<string, HashSet<string>> Asked, string Model, int Refused, int Over)> PlanAndRunAsync(bool old)
        {
            var relay = new TamperRelay { Old = old };
            using var f = await OpenAsync(30, relay, json => json.Replace("\"name\": \"KPI 01\",", "\"name\": \"KPI 01\", \"displayFolder\": \"Costs\","));
            var plan = await ReadUntilQuietAsync(f, () => relay.Answered.Count + relay.Refused);
            for (var press = 0; press < 40; press++)
            {
                foreach (var asst in plan.Items.Where(i => i.Doer != PlanDoers.Semanticus && i.StateWord != PlanStateWords.Done && i.Status != ModelPlanStatuses.Skipped
                                                           && (i.Id.StartsWith("describe/", StringComparison.Ordinal) || i.Id.StartsWith("words/", StringComparison.Ordinal))))
                    await f.Engine.SkipModelPlanItemAsync(asst.Id, "No assistant here.", "human");
                await f.Engine.JevSettledForTest();
                plan = await f.Engine.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held)) break;
                var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                if (!ran.Performed) break;
                await f.Engine.JevSettledForTest();
            }
            var rows = await f.Sessions.Current.ReadAsync(m =>
                m.Tables.SelectMany(t => t.Columns.Select(c => "c|" + t.Name + "|" + c.Name + "|" + c.SummarizeBy + "|" + c.FormatString + "|" + c.IsHidden + "|" + c.DisplayFolder)
                    .Concat(t.Measures.Select(x => "m|" + t.Name + "|" + x.Name + "|" + x.Expression + "|" + x.FormatString + "|" + x.DisplayFolder + "|" + x.IsHidden)))
                  .OrderBy(x => x, StringComparer.Ordinal).ToList());
            List<(string Point, int Questions, int Bytes, List<string> Names)> answered;
            lock (relay.Answered) answered = relay.Answered.ToList();
            var asked = answered.GroupBy(a => a.Point).ToDictionary(g => g.Key, g => new HashSet<string>(g.SelectMany(a => a.Names), StringComparer.Ordinal));
            var over = answered.Count(a => a.Questions > (TamperRelay.OldBounds.TryGetValue(a.Point, out var b) ? b : 30));
            return (asked, rows.Count + " objects, " + string.Join("\n", rows).GetHashCode().ToString("x"), relay.Refused, over);
        }

        [Fact]
        public async Task Recheck_a_faithful_old_relay_answers_every_question_of_every_family_and_the_run_ends_in_the_same_model()
        {
            var fresh = await PlanAndRunAsync(old: false);
            var old = await PlanAndRunAsync(old: true);
            Assert.True(old.Refused > 0, "the old relay never refused: the fallback was not exercised");
            Assert.True(old.Over == 0, old.Over + " answered requests were over the old relay's bound");
            var lost = new List<string>();
            foreach (var (point, names) in fresh.Asked)
            {
                old.Asked.TryGetValue(point, out var got);
                var missing = names.Where(n => got == null || !got.Contains(n)).ToList();
                if (missing.Count > 0) lost.Add(point + " lost " + missing.Count + " of " + names.Count + " (e.g. " + missing[0] + ")");
            }
            Assert.True(lost.Count == 0, string.Join("; ", lost));
            Assert.True(fresh.Asked.ContainsKey("measure.folder_and_format") && fresh.Asked["measure.folder_and_format"].Any(n => n.StartsWith("display_folder", StringComparison.Ordinal)), "the folder question was never asked: the two-question case was not exercised");
            Assert.Equal(fresh.Model, old.Model);
        }

        [Fact]
        public async Task Recheck_a_refusal_that_fewer_questions_cannot_cure_ends_and_says_not_decided()
        {
            var relay = new TamperRelay { RefuseAllAsState = true };
            using var f = await OpenAsync(2, relay);
            var point = DecisionRegistry.Require("cleanup.suggest");
            var snapshot = DecisionSnapshot.Build(point, point.SnapshotFields.Select(n => new KeyValuePair<string, object>(n, (object)new List<object>())));
            var option = new[] { new DecisionOption("suggest", "a leftover"), new DecisionOption("leave_out", "anything else") };
            var qs = Enumerable.Range(0, 100).Select(i => new DecisionQuestion("cleanup:" + i, DecisionQuestionKind.Choice, "Is item " + i + " a leftover?", option)).ToList();
            var ask = f.Engine.Splitter.AskAsync(f.Engine.Decisions, point.Name, snapshot, qs, true, true, null, CancellationToken.None);
            Assert.True(await Task.WhenAny(ask, Task.Delay(30_000)) == ask, "the splitter did not finish");
            var outcome = await ask;
            Assert.False(outcome.Asked && outcome.Answers.Decided, "a request nothing could cure came back decided");
            Assert.True(relay.Refused <= 2 * qs.Count + 1, relay.Refused + " refused calls for " + qs.Count + " questions");
        }

        [Fact]
        public async Task Recheck_a_split_whole_ask_keeps_every_answer_the_parts_gave()
        {
            var relay = new TamperRelay { Old = true };
            using var f = await OpenAsync(2, relay);
            var point = DecisionRegistry.Require("query.summarize");
            var snapshot = DecisionSnapshot.Build(point, point.SnapshotFields.Select(n => new KeyValuePair<string, object>(n, (object)new List<object>())));
            var qs = Enumerable.Range(0, 60).Select(i => new DecisionQuestion("q:" + i, DecisionQuestionKind.Choice, "Part " + i + "?",
                new[] { new DecisionOption("a" + i, "first"), new DecisionOption("b" + i, "second") })).ToList();
            relay.Choose = q => q.Options[1].Id;   // a distinct, recognisable answer per question
            var outcome = await f.Engine.Splitter.AskAsync(f.Engine.Decisions, point.Name, snapshot, qs, true, true, null, CancellationToken.None);
            Assert.True(outcome.Asked && outcome.Answers.Decided, outcome.Answers?.NotDecidedReason + outcome.SkippedReason);
            foreach (var q in qs) Assert.Equal("b" + q.Name.Substring(2), outcome.Answers.For(q.Name)?.Choice);
            Assert.True(relay.Answered.All(a => a.Questions <= 24));
        }

        // ---- the requests a 30-table model with unicode and quotes sends, dumped for the real relay checks (node) ---------------

        [Fact]
        public async Task Attack_unicode_model_requests_dump_for_the_relay_check()
        {
            var dump = Environment.GetEnvironmentVariable("VJB_DUMP");
            if (string.IsNullOrEmpty(dump)) return;
            Directory.CreateDirectory(dump);
            var relay = new TamperRelay { DumpDir = dump };
            using var f = await OpenAsync(30, relay, json => json
                .Replace("Activity Event", "Activit\\u00e9 \\\"\\u00c9v\\u00e9nement\\\" \\u65e5\\u672c \\ud83d\\ude00")
                .Replace("Duration Ms", "Dur\\u00e9e \\u00abms\\u00bb \\\"q\\\"")
                .Replace("Workspace", "Espace de travail \\u00e9\\u00e8 \\u4e2d\\u6587"));
            var plan = await ReadUntilQuietAsync(f, () => relay.Answered.Count);
            Assert.True(relay.Answered.Count > 0);
            Assert.Contains(Lines(plan, ColumnSummarizeFamily.Name), l => (l.After ?? "").Contains("日本"));
        }

        // ---- 3. the base-measure rows' footprint -------------------------------------------------------------------------------

        private static FootprintInputs Inputs() => new FootprintInputs { Model = new FootprintSnapshot
        {
            MeasureRefs = new[] { "measure:Sales/Existing", "measure:Measure Table/KPI" },
            ColumnsByTable = { ["Sales"] = new[] { "column:Sales/Amount", "column:Sales/Key" }, ["Returns"] = new[] { "column:Returns/Qty" }, ["Measure Table"] = new[] { "column:Measure Table/Value" } },
            MeasuresByTable = { ["Sales"] = new[] { "measure:Sales/Existing" }, ["Returns"] = Array.Empty<string>(), ["Measure Table"] = new[] { "measure:Measure Table/KPI" } },
            RelationshipTables = { ["Sales"] = new[] { "Returns" }, ["Returns"] = new[] { "Sales" } },
        } };

        private static Footprint Summarize(FootprintInputs x, string table, string column, string measure) =>
            PlanFootprint.Of("jev.column-summarize.table:" + table, new[]
            {
                new ModelPlanChangeLine { Op = "set_summarize_by", Target = "column:" + table + "/" + column, After = "none" },
                new ModelPlanChangeLine { Op = "create_measure", Target = "measure:" + table + "/" + measure, After = "SUM ( '" + table + "'[" + column + "] )" },
            }, x);

        private static Footprint Row(FootprintInputs x, string id, string op, string target, string after = null, params (string K, string V)[] args) =>
            PlanFootprint.Of(id, new[] { new ModelPlanChangeLine { Op = op, Target = target, After = after,
                Args = args.Length == 0 ? null : args.ToDictionary(a => a.K, a => a.V, StringComparer.Ordinal) } }, x);

        [Fact]
        public void Attack_footprint_every_row_that_must_meet_a_base_measure_row_still_does()
        {
            var x = Inputs();
            var sales = Summarize(x, "Sales", "Amount", "Total Amount");
            Assert.True(sales.Known);
            // the same measure name in another table, any case: a measure name is unique across the model
            Assert.False(PlanFootprint.Independent(sales, Summarize(x, "Returns", "Qty", "total amount")), "two rows taking one measure name in different case");
            // a starter row on the same table (it reads the table's columns)
            Assert.False(PlanFootprint.Independent(sales, Row(x, "jev.starter.table:Sales", "create_measure", "measure:Sales/Revenue", "SUM ( 'Sales'[Amount] )")));
            // a rule row in another table whose new measure adds up this column
            Assert.False(PlanFootprint.Independent(sales, Row(x, "rule/kpi", "create_measure", "measure:Measure Table/Amount KPI", "SUM ( 'Sales'[Amount] )")));
            // a rule row that makes a measure of the same name somewhere else
            Assert.False(PlanFootprint.Independent(sales, Row(x, "rule/kpi2", "create_measure", "measure:Measure Table/Total Amount", "1")));
            // a new column with the measure's name on the same table
            Assert.False(PlanFootprint.Independent(sales, Row(x, "rule/col", "create_column", "column:Sales/Total Amount", "1")));
            // rename, delete of the column, of the table, rename of the table
            Assert.False(PlanFootprint.Independent(sales, Row(x, "rule/r1", "rename", "column:Sales/Amount", "Net")));
            Assert.False(PlanFootprint.Independent(sales, Row(x, "rule/d1", "delete", "column:Sales/Amount")));
            Assert.False(PlanFootprint.Independent(sales, Row(x, "rule/d2", "delete", "table:Sales")));
            Assert.False(PlanFootprint.Independent(sales, Row(x, "rule/r2", "rename", "table:Sales", "Orders")));
            // a rename of an existing measure INTO the new measure's name
            Assert.False(PlanFootprint.Independent(sales, Row(x, "rule/r3", "rename", "measure:Measure Table/KPI", "Total Amount")));
            // two tables' rows with different names stay independent (the point of the commit)
            Assert.True(PlanFootprint.Independent(sales, Summarize(x, "Returns", "Qty", "Total Qty")));
        }
    }
}
