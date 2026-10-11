// The one comparison run for m11/jev-batch (Kane, 30 Sep 2026: "Yes, this release"): does asking Jev a table at a time
// move its answers, against asking it one object at a time? The protocol (labels, the harm rule, the 95% bar) is
// docs/redesign/jev-batch-comparison.md, committed before this ran.
//
// It runs ONLY when JEV_REPLAY_LIVE is exactly "1"; otherwise xunit reports it SKIPPED, never passed. It drives the
// product's own pieces: each family's Prepare builds the objects, the OLD arm sends each object's own request
// (Ask.Snapshot, Ask.Questions), the NEW arm sends the family's per-table requests (Family.Batches), both through the real
// JevDecisionOracle and DecisionService with the development key. Raw results go OUTSIDE the repository
// (~/.cache/jev-batch-comparison, overridable with JEV_REPLAY_OUT); tools/jev-replay/batch/score.mjs turns them into the
// per-point table and the verdict. The key is read from ~/.config/typesafe/env into this process only, for the clients below,
// and is never printed, returned or written.
//
//   JEV_REPLAY_LIVE=1 dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevBatchComparison"
//   node tools/jev-replay/batch/score.mjs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Decisions;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class JevBatchComparison
    {
        private static readonly JsonSerializerOptions Pretty = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>One answer of one arm: what the object's question came back with.</summary>
        private sealed class Row
        {
            public string Key { get; set; }
            public string Choice { get; set; }
            public double Confidence { get; set; }
            public string Note { get; set; }
        }

        private sealed class Arm
        {
            public int Requests { get; set; }
            public long InputTokens { get; set; }
            public double WallMs { get; set; }
            public List<Row> Rows { get; set; } = new List<Row>();
        }

        private static string OutDir()
        {
            var outDir = Environment.GetEnvironmentVariable("JEV_REPLAY_OUT")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "jev-batch-comparison");
            if (Path.GetFullPath(outDir).StartsWith(Path.GetFullPath(JevAiSchemaReplay.Repo()), StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to write results inside the repository.");
            Directory.CreateDirectory(outDir);
            return outDir;
        }

        /// <summary>Send every request of one arm through a client and a service of its own (so its requests and tokens are its
        /// own), six in flight as the product asks, and time the whole arm.</summary>
        private static async Task<Arm> RunArmAsync<T>(DecisionFixture f, DecisionPoint point, IReadOnlyList<T> requests,
            Func<T, (IReadOnlyDictionary<string, object> Snapshot, IReadOnlyList<DecisionQuestion> Questions)> shape, bool perQuestion,
            Func<T, DecisionOutcome, IEnumerable<Row>> rowsOf)
        {
            var budget = new DecisionBudget(8_000_000);
            using var jev = new JevDecisionOracle(JevAiSchemaReplay.Key(), budget);
            f.Engine.UseDecisionsForTest(new DecisionService(jev, budget));
            using var gate = new SemaphoreSlim(6, 6);
            var clock = Stopwatch.StartNew();
            var results = await Task.WhenAll(requests.Select(async r =>
            {
                await gate.WaitAsync();
                try
                {
                    var (snapshot, questions) = shape(r);
                    var o = await f.Engine.Decisions.AskAsync(point.Name, snapshot, questions, isPro: true, enabled: true, fence: null,
                        ct: CancellationToken.None, perQuestion: perQuestion);
                    return rowsOf(r, o).ToList();
                }
                catch (Exception ex) { return new List<Row> { new Row { Key = "?", Note = ex.GetType().Name } }; }
                finally { gate.Release(); }
            }));
            clock.Stop();
            return new Arm
            {
                Requests = (int)budget.Requests,
                InputTokens = budget.UsedInputTokens,
                WallMs = clock.Elapsed.TotalMilliseconds,
                Rows = results.SelectMany(x => x).ToList(),
            };
        }

        private static Row RowOf(string key, DecisionAnswers answers, string question)
        {
            var a = answers != null && answers.Decided ? answers.For(question) : null;
            return new Row { Key = key, Choice = a?.Choice, Confidence = a?.Confidence ?? 0, Note = a == null ? (answers?.NotDecidedReason ?? "no answer") : null };
        }

        private static Row Unanswered(string key, DecisionOutcome o) =>
            new Row { Key = key, Note = o == null ? "no outcome" : o.SkippedReason ?? o.Answers?.NotDecidedReason ?? "no answer" };

        /// <summary>The old arm's rows: one request per object, each answering the family's plain question names.</summary>
        private static IEnumerable<Row> OldRows(string key, IEnumerable<string> questions, DecisionOutcome o) =>
            questions.Select(q => o != null && o.Asked ? RowOf(key + "#" + q, o.Answers, q) : Unanswered(key + "#" + q, o));

        /// <summary>The new arm's rows: one request per table, each object reading its own answers under the plain names.</summary>
        private static IEnumerable<Row> NewRows<TAsk>(TableBatch<TAsk> batch, Func<TAsk, string> refOf, DecisionOutcome o) where TAsk : class =>
            batch.Asks.SelectMany(ask => batch.Names[ask].Select(n =>
                o != null && o.Asked ? RowOf(refOf(ask) + "#" + n.Value, o.Answers.Narrow(batch.Names[ask]), n.Value) : Unanswered(refOf(ask) + "#" + n.Value, o)));

        private static object Pair(Arm old, Arm now, Arm again = null) => new { old, @new = now, old2 = again };

        /// <summary>JEV_BATCH_REPEAT_OLD=1 asks the per-object requests a second time, so the comparison can say how much of any
        /// difference is the service answering the same request differently on another day (the noise floor), which is not the shape.</summary>
        private static bool RepeatOld => Environment.GetEnvironmentVariable("JEV_BATCH_REPEAT_OLD") == "1";

        [JevReplayFact]
        public async Task Compare_one_request_per_object_with_one_request_per_table()
        {
            var outDir = OutDir();
            using var labels = JevAiSchemaReplay.Labels();
            var models = new List<object>();
            var displayFolders = typeof(LocalEngine).GetMethod("DisplayFolderSuggestions", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(displayFolders);

            foreach (var model in labels.RootElement.GetProperty("models").EnumerateArray())
            {
                var id = model.GetProperty("id").GetString();
                using var f = await JevAiSchemaReplay.OpenAsync(model);
                if (f == null) throw new InvalidOperationException("The model " + id + " is not on this machine.");
                await f.ConsentAsync();
                var points = new Dictionary<string, object>(StringComparer.Ordinal);

                // ---- column.summarize ---------------------------------------------------------------------------------
                {
                    var point = DecisionRegistry.Require(DecisionRegistry.ColumnSummarize);
                    var prepared = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null));
                    var batches = ColumnSummarizeFamily.Batches(prepared);
                    var old = await RunArmAsync(f, point, prepared.Asks, a => (a.Snapshot, a.Questions), false,
                        (a, o) => OldRows(a.Ref, new[] { ColumnSummarizeFamily.Question }, o));
                    var again = !RepeatOld ? null : await RunArmAsync(f, point, prepared.Asks, a => (a.Snapshot, a.Questions), false,
                        (a, o) => OldRows(a.Ref, new[] { ColumnSummarizeFamily.Question }, o));
                    var now = await RunArmAsync(f, point, batches, b => (b.Snapshot, b.Questions), true,
                        (b, o) => NewRows(b, a => a.Ref, o));
                    points[point.Name] = Pair(old, now, again);
                }

                // ---- column.format ------------------------------------------------------------------------------------
                {
                    var point = DecisionRegistry.Require(DecisionRegistry.ColumnFormat);
                    var prepared = await f.Sessions.Current.ReadAsync(m => ColumnFormatFamily.Prepare(m));
                    var batches = ColumnFormatFamily.Batches(prepared);
                    var old = await RunArmAsync(f, point, prepared.Asks, a => (a.Snapshot, a.Questions), false,
                        (a, o) => OldRows(a.Ref, new[] { ColumnFormatFamily.Question }, o));
                    var again = !RepeatOld ? null : await RunArmAsync(f, point, prepared.Asks, a => (a.Snapshot, a.Questions), false,
                        (a, o) => OldRows(a.Ref, new[] { ColumnFormatFamily.Question }, o));
                    var now = await RunArmAsync(f, point, batches, b => (b.Snapshot, b.Questions), true,
                        (b, o) => NewRows(b, a => a.Ref, o));
                    points[point.Name] = Pair(old, now, again);
                }

                // ---- ai.schema ----------------------------------------------------------------------------------------
                {
                    var point = DecisionRegistry.Require(DecisionRegistry.AiSchema);
                    var prepared = await f.Sessions.Current.ReadAsync(m => AiSchemaFamily.Prepare(m, int.MaxValue));
                    var batches = AiSchemaFamily.Batches(prepared);
                    var old = await RunArmAsync(f, point, prepared.Asks, a => (a.Snapshot, a.Questions), false,
                        (a, o) => OldRows(a.Ref, new[] { AiSchemaFamily.Question }, o));
                    var again = !RepeatOld ? null : await RunArmAsync(f, point, prepared.Asks, a => (a.Snapshot, a.Questions), false,
                        (a, o) => OldRows(a.Ref, new[] { AiSchemaFamily.Question }, o));
                    var now = await RunArmAsync(f, point, batches, b => (b.Snapshot, b.Questions), true,
                        (b, o) => NewRows(b, a => a.Ref, o));
                    points[point.Name] = Pair(old, now, again);
                }

                // ---- measure.folder_and_format --------------------------------------------------------------------------
                {
                    var point = DecisionRegistry.Require(DecisionRegistry.MeasureFolderAndFormat);
                    var folders = (Func<Model, Table, string[]>)Delegate.CreateDelegate(typeof(Func<Model, Table, string[]>), displayFolders);
                    var prepared = await f.Sessions.Current.ReadAsync(m => MeasuresFamily.Prepare(m, folders));
                    if (prepared.Asks.Count > 0)
                    {
                        var batches = MeasuresFamily.Batches(prepared);
                        var old = await RunArmAsync(f, point, prepared.Asks, a => (a.Snapshot, a.Questions), false,
                            (a, o) => OldRows(a.Ref, a.Questions.Select(q => q.Name), o));
                    var again = !RepeatOld ? null : await RunArmAsync(f, point, prepared.Asks, a => (a.Snapshot, a.Questions), false,
                            (a, o) => OldRows(a.Ref, a.Questions.Select(q => q.Name), o));
                        var now = await RunArmAsync(f, point, batches, b => (b.Snapshot, b.Questions), true,
                            (b, o) => NewRows(b, a => a.Ref, o));
                        points[point.Name] = Pair(old, now, again);
                    }
                }

                models.Add(new { id, points });
            }

            var compose = await ComposeAsync();
            File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(new
            {
                ranUtc = DateTime.UtcNow.ToString("o"),
                confidenceBar = DecisionRegistry.Require(DecisionRegistry.ColumnSummarize).ConfidenceBar,
                models,
                compose,
            }, Pretty));
            Assert.True(models.Count > 0);
        }

        // ---- compose.draft, the last question ("wanted") ---------------------------------------------------------------

        /// <summary>The kept adds of the labelled compose sentences, and pairs and triples of them typed as one text, asked the old way
        /// (one request for each add) and the new way (one request for all the adds of a text).</summary>
        private static async Task<object> ComposeAsync()
        {
            var dir = Path.Combine(JevAiSchemaReplay.Repo(), "tools", "jev-replay", "compose");
            var words = new List<string>();
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in items.EnumerateArray())
                    if (item.TryGetProperty("words", out var w) && w.ValueKind == JsonValueKind.String) words.Add(w.GetString());
            }
            words = words.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct(StringComparer.Ordinal).ToList();
            var tables = new[] { "Sales", "Customer", "Product" };
            var objects = new[] { "Sales", "Customer", "Product", "Amount", "Order Count", "Total Sales" };

            // Texts: every sentence alone, then consecutive pairs and triples typed together, in a fixed order.
            var texts = new List<string>(words);
            for (var i = 0; i + 1 < words.Count; i += 2) texts.Add(words[i] + ". " + words[i + 1]);
            for (var i = 0; i + 2 < words.Count; i += 3) texts.Add(words[i] + ". " + words[i + 1] + ". " + words[i + 2]);

            var cases = new List<(string Text, List<(string Clause, DraftMenu.Template Template)> Adds)>();
            foreach (var text in texts.Distinct(StringComparer.Ordinal))
            {
                var clauses = DescribedSteps.ClausesOf(text, out _, out _);
                var rule = DescribedSteps.RuleOfWords(text, tables, objects);
                var adds = new List<(string, DraftMenu.Template)>();
                for (var i = 0; i < rule.Length && i < clauses.Count; i++)
                    if (rule[i].Tier == AddingRule.Tier.Kept && rule[i].Kind != null
                        && DraftMenu.All.FirstOrDefault(x => x.Op == rule[i].Kind) is DraftMenu.Template t)
                        adds.Add((clauses[i], t));
                if (adds.Count > 0 && text.Length <= JevDraftBuilder.MaxWantedChars) cases.Add((text, adds));
            }

            var point = DecisionRegistry.Require(DecisionRegistry.ComposeDraft);
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Compose", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.SaveAsAsync("Compose.bim");
            await f.ConsentAsync();

            // Old: one request per add. New: one request per text.
            var oldRequests = cases.SelectMany(c => c.Adds.Select((a, i) => (Case: c, Index: i))).ToList();
            var old = await RunArmAsync(f, point, oldRequests,
                r => { var (s, q) = JevDraftBuilder.BuildWanted(r.Case.Adds[r.Index].Clause, r.Case.Adds[r.Index].Template, r.Case.Text, tables); return (s, new[] { q }); },
                false, (r, o) => new[] { o != null && o.Asked ? RowOf(Key(r.Case.Text, r.Index), o.Answers, JevDraftBuilder.Q(r.Case.Adds[r.Index].Template.Id, JevDraftBuilder.Wanted)) : Unanswered(Key(r.Case.Text, r.Index), o) });
            var manyRequests = cases.Select(c => (Case: c, Built: JevDraftBuilder.BuildWantedMany(c.Adds, c.Text, tables))).ToList();
            var now = await RunArmAsync(f, point, manyRequests, r => (r.Built.Snapshot, r.Built.Questions), true,
                (r, o) => r.Built.Questions.Select((q, i) => o != null && o.Asked ? RowOf(Key(r.Case.Text, i), o.Answers, q.Name) : Unanswered(Key(r.Case.Text, i), o)));
            return new { texts = cases.Count, adds = oldRequests.Count, @old = old, @new = now };

            static string Key(string text, int i) => text + " [" + i + "]";
        }
    }
}
