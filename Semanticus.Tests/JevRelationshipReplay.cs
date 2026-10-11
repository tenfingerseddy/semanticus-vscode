// The relationship.join replay (M4 point 2): does Jev join the right columns, the right way, with the right one active?
//
// The live fact runs ONLY when JEV_REPLAY_LIVE is exactly "1"; otherwise xunit reports it SKIPPED, never passed. It
// drives the product's own path end to end: each labelled model is opened with every relationship removed, the real
// family run builds each request (RelationshipFamily.Prepare), the real JevDecisionOracle sends it (the development
// route), and the real resolution and lines come back on the Plan. The name rule is run over the same stripped model
// through the same resolution, so both arms are scored on the same columns.
//
// The labelled set is tools/jev-replay/relationships/labels.json and the bar is bar.json beside it, both committed
// before the first run. Raw results go OUTSIDE the repository (~/.cache/jev-replay-relationships/results.json,
// JEV_REPLAY_OUT overrides); tools/jev-replay/relationships/score.mjs turns them into aggregate numbers and the
// verdict. The key is read from ~/.config/typesafe/env into this process only, for one client, and is never printed,
// returned or written.
//
//   JEV_REPLAY_LIVE=1 dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevRelationshipReplay"
//   node tools/jev-replay/relationships/score.mjs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class JevRelationshipReplay
    {
        private static string Repo()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Semanticus.sln"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
        }

        private static string Key()
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "typesafe", "env");
            var m = Regex.Match(File.ReadAllText(file), @"^\s*(?:export\s+)?[A-Z0-9_]*(?:KEY|TOKEN)[A-Z0-9_]*\s*=\s*[""']?([^""'\s]+)", RegexOptions.Multiline);
            if (!m.Success) throw new InvalidOperationException("No key in the key file.");
            return m.Groups[1].Value;
        }

        /// <summary>The labelled set: labels.json, or the file JEV_REPLAY_LABELS names beside it (labels-fresh.json for
        /// run 2).</summary>
        private static JsonDocument Labels(string file = null) =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo(), "tools", "jev-replay", "relationships",
                file ?? Environment.GetEnvironmentVariable("JEV_REPLAY_LABELS") ?? "labels.json")));

        private static readonly Dictionary<string, string> BimTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Int64"] = "int64", ["String"] = "string", ["DateTime"] = "dateTime", ["Decimal"] = "decimal", ["Double"] = "double", ["Boolean"] = "boolean",
        };

        /// <summary>The model as a .bim with NO relationships: the vendored file with its relationships taken out, or the
        /// labelled tables written as plain import tables.</summary>
        internal static string StrippedBim(JsonElement model)
        {
            if (model.TryGetProperty("bim", out var bim))
            {
                var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo(), bim.GetString())));
                doc["model"].AsObject().Remove("relationships");
                return doc.ToJsonString();
            }
            var tables = new JsonArray();
            foreach (var t in model.GetProperty("tables").EnumerateArray())
            {
                var name = t.GetProperty("name").GetString();
                var columns = new JsonArray();
                foreach (var c in t.GetProperty("columns").EnumerateArray())
                {
                    var col = new JsonObject
                    {
                        ["name"] = c.GetProperty("name").GetString(),
                        ["dataType"] = BimTypes[c.GetProperty("dataType").GetString()],
                        ["sourceColumn"] = c.GetProperty("name").GetString(),
                    };
                    if (c.TryGetProperty("isKey", out var k) && k.GetBoolean()) col["isKey"] = true;
                    columns.Add(col);
                }
                var table = new JsonObject
                {
                    ["name"] = name,
                    ["columns"] = columns,
                    ["partitions"] = new JsonArray(new JsonObject
                    {
                        ["name"] = name,
                        ["source"] = new JsonObject { ["type"] = "m", ["expression"] = "let Source = #table({}, {}) in Source" },
                    }),
                };
                if (t.TryGetProperty("dataCategory", out var cat)) table["dataCategory"] = cat.GetString();
                tables.Add(table);
            }
            return new JsonObject
            {
                ["name"] = "SemanticModel",
                ["compatibilityLevel"] = 1604,
                ["model"] = new JsonObject { ["culture"] = "en-US", ["tables"] = tables },
            }.ToJsonString();
        }

        private static async Task<DecisionFixture> OpenStrippedAsync(JsonElement model)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var path = Path.Combine(f.Workspace, model.GetProperty("id").GetString() + ".bim");
            File.WriteAllText(path, StrippedBim(model));
            await f.Engine.OpenAsync(path);
            return f;
        }

        private static object RelationOf(RelationshipFamily.Pick p) => new
        {
            from = p.Ask.TableName + "/" + p.Ask.ColumnName,
            to = p.Key.Table + "/" + p.Key.Column,
            active = p.Active,
            direction = p.Direction,
            cardinality = p.Cardinality,
        };

        /// <summary>Not live: the labels line up with the models. The vendored sample's labels are still exactly its own
        /// relationships, every model opens with no relationship, and every labelled column exists in it.</summary>
        [Theory]
        [InlineData("labels.json", 60)]
        [InlineData("labels-fresh.json", 20)]
        public async Task The_labels_line_up_with_the_models(string file, int atLeast)
        {
            using var labels = Labels(file);
            var n = 0;
            foreach (var model in labels.RootElement.GetProperty("models").EnumerateArray())
            {
                var id = model.GetProperty("id").GetString();
                var labelled = model.GetProperty("labels").EnumerateArray()
                    .Select(l => l.GetProperty("from").GetString() + ">" + l.GetProperty("to").GetString()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                n += labelled.Length;
                if (model.TryGetProperty("bim", out var bim))
                {
                    using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo(), bim.GetString())));
                    var own = source.RootElement.GetProperty("model").GetProperty("relationships").EnumerateArray()
                        .Select(r => r.GetProperty("fromTable").GetString() + "/" + r.GetProperty("fromColumn").GetString() + ">"
                                   + r.GetProperty("toTable").GetString() + "/" + r.GetProperty("toColumn").GetString())
                        .OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    Assert.True(own.SequenceEqual(labelled), id + ": the labels no longer match the file's own relationships");
                }
                using var f = await OpenStrippedAsync(model);
                var columns = await f.Sessions.Current.ReadAsync(m =>
                {
                    Assert.Empty(m.Relationships);
                    return m.Tables.SelectMany(t => t.Columns.Select(c => t.Name + "/" + c.Name)).ToHashSet(StringComparer.Ordinal);
                });
                foreach (var pair in labelled)
                    foreach (var end in pair.Split('>'))
                        Assert.True(columns.Contains(end), id + " has no column " + end);
            }
            Assert.True(n >= atLeast, n + " labelled relationships");
        }

        /// <summary>Every answer the oracle gave, keyed by the column it was about, so each decision can be scored on
        /// its own and the Pro path ("Jev when sure, else the rule") can be rebuilt from the same answers.</summary>
        private sealed class RecordingOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            public RecordingOracle(IDecisionOracle inner) { _inner = inner; }
            public bool Available => _inner.Available;
            public Dictionary<string, DecisionAnswers> ByColumn { get; } = new Dictionary<string, DecisionAnswers>(StringComparer.Ordinal);
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, System.Threading.CancellationToken ct)
            {
                var answers = await _inner.AskAsync(request, ct).ConfigureAwait(false);
                if (request.PointName == DecisionRegistry.RelationshipJoin)
                    lock (ByColumn) ByColumn[request.Snapshot["table_name"] + "/" + request.Snapshot["column_name"]] = answers;
                return answers;
            }
        }

        /// <summary>One answer, or the rule's value when the answer is not sure (the Pro path).</summary>
        private static (string Choice, double Confidence) Sure(DecisionAnswers a, string question, double bar)
        {
            var x = a != null && a.Decided ? a.For(question) : null;
            return x != null && x.Confidence >= bar ? (x.Choice, x.Confidence) : (null, 0);
        }

        [JevReplayFact]
        public async Task Replay_the_labelled_models_through_the_product()
        {
            var repo = Repo();
            var outDir = Environment.GetEnvironmentVariable("JEV_REPLAY_OUT")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "jev-replay-relationships");
            if (Path.GetFullPath(outDir).StartsWith(Path.GetFullPath(repo), StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to write results inside the repository.");
            Directory.CreateDirectory(outDir);

            var budget = new DecisionBudget(2_000_000);
            // JEV_REPLAY_DRY=1 asks nobody: it lists the columns and the rule's answers, which is what the answer key
            // is hand-checked against before any Jev answer exists.
            var dry = Environment.GetEnvironmentVariable("JEV_REPLAY_DRY") == "1";
            using var live = dry ? null : new JevDecisionOracle(Key(), budget);
            IDecisionOracle jev = dry ? NullDecisionOracle.Instance : live;
            var point = DecisionRegistry.Require(DecisionRegistry.RelationshipJoin);
            var bar = point.ConfidenceBar;
            using var labels = Labels();
            var rows = new List<object>();
            foreach (var model in labels.RootElement.GetProperty("models").EnumerateArray())
            {
                using var f = await OpenStrippedAsync(model);
                await f.ConsentAsync();
                foreach (var other in DecisionRegistry.Points.Where(p => p.Name != point.Name))
                    await f.Engine.SetDecisionPointEnabledAsync(other.Name, false, "human");
                f.Engine.RelationshipJevDecidesForTest = true;
                var recording = new RecordingOracle(jev);
                f.Engine.UseDecisionsForTest(new DecisionService(recording, budget));

                // The product's own run: the requests it builds are the ones asked.
                await f.Engine.GetModelPlanAsync();
                await f.Engine.JevSettledForTest();

                var row = await f.Sessions.Current.ReadAsync(m =>
                {
                    var prepared = RelationshipFamily.Prepare(m, null);
                    List<object> Arm(Func<RelationshipFamily.Ask, RelationshipFamily.Pick> pick) =>
                        RelationshipFamily.Resolve(prepared.Asks.Select(pick).Where(p => p != null).ToList(),
                                                   prepared.Existing, prepared.ActivePairs, new List<ModelPlanChangeRefusal>())
                            .Select(RelationOf).ToList();
                    DecisionAnswers AnswersFor(RelationshipFamily.Ask a) =>
                        recording.ByColumn.TryGetValue(a.TableName + "/" + a.ColumnName, out var x) ? x : null;

                    // What the product proposes today: lines that arrive kept (sure or likely).
                    var kept = Arm(a =>
                    {
                        var p = RelationshipFamily.Map(a, AnswersFor(a));
                        return p != null && p.Confidence >= JevFamily.LikelyFloor ? p : null;
                    });
                    var sure = Arm(a =>
                    {
                        var p = RelationshipFamily.Map(a, AnswersFor(a));
                        return p != null && p.Confidence >= bar ? p : null;
                    });
                    // The Pro path: per QUESTION, Jev's answer when it is sure, otherwise the rule's.
                    var blend = Arm(a =>
                    {
                        var answers = AnswersFor(a);
                        var rule = RelationshipFamily.RulePick(a);
                        var join = Sure(answers, RelationshipFamily.JoinTo, bar);
                        var keyId = join.Choice ?? rule?.Key.Id;
                        if (keyId == null || keyId == RelationshipFamily.None) return null;
                        var dir = Sure(answers, RelationshipFamily.Direction, bar).Choice ?? RelationshipFamily.OneWay;
                        var card = Sure(answers, RelationshipFamily.Cardinality, bar).Choice ?? RelationshipFamily.ManyToOne;
                        var role = Sure(answers, RelationshipFamily.DateRole, bar);
                        var ruleMain = rule != null && rule.Key.Id == keyId && rule.Main;
                        var p = RelationshipFamily.Map(a, keyId, join.Choice != null ? join.Confidence : 1.0, dir, card,
                                                       role.Choice ?? (ruleMain ? RelationshipFamily.Main : RelationshipFamily.Other),
                                                       role.Choice != null ? role.Confidence : 1.0);
                        return p;
                    });
                    var rule = Arm(RelationshipFamily.RulePick);

                    var asks = prepared.Asks.Select(a =>
                    {
                        var x = AnswersFor(a);
                        object Q(string q)
                        {
                            var ans = x != null && x.Decided ? x.For(q) : null;
                            return ans == null ? null : new { choice = ans.Choice, confidence = ans.Confidence };
                        }
                        return (object)new
                        {
                            column = a.TableName + "/" + a.ColumnName,
                            keys = a.Keys.Select(k => k.Table + "/" + k.Column).ToArray(),
                            ruleKey = RelationshipFamily.RuleTarget(a) is RelationshipFamily.Key rk ? rk.Table + "/" + rk.Column : null,
                            decided = x?.Decided ?? false,
                            join = Q(RelationshipFamily.JoinTo),
                            direction = Q(RelationshipFamily.Direction),
                            cardinality = Q(RelationshipFamily.Cardinality),
                            dateRole = Q(RelationshipFamily.DateRole),
                        };
                    }).ToArray();

                    return new
                    {
                        id = model.GetProperty("id").GetString(),
                        asked = prepared.Asks.Count,
                        candidates = prepared.Asks.SelectMany(a => a.Keys.Select(k => (object)new { from = a.TableName + "/" + a.ColumnName, to = k.Table + "/" + k.Column })).ToArray(),
                        asks,
                        jev = kept,
                        jevSure = sure,
                        blend,
                        rule,
                    };
                });
                rows.Add(row);
            }

            var result = new
            {
                ranUtc = DateTime.UtcNow.ToString("o"),
                point = point.Name,
                labels = Environment.GetEnvironmentVariable("JEV_REPLAY_LABELS") ?? "labels.json",
                requests = budget.Requests,
                inputTokens = budget.UsedInputTokens,
                models = rows,
            };
            File.WriteAllText(Path.Combine(outDir, Environment.GetEnvironmentVariable("JEV_REPLAY_RESULTS") ?? "results.json"),
                              JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Assert.True(rows.Count > 0);
        }
    }
}
