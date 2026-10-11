// The ai.schema replay (T268, M4 point 5: what Copilot sees). Does Jev choose what Copilot sees better than today's rules?
//
// The labelled set is tools/jev-replay/ai-schema/labels.json: every eligible table, column and measure of three
// placeholder-named models (the UAT Northstar and ContosoMini fixtures, and the vendored Tabular Editor AdventureWorks
// test model), each labelled include, exclude or either by a written guide. The bar is tools/jev-replay/ai-schema/bar.json.
// Both were committed before the first live run.
//
// The live fact runs ONLY when JEV_REPLAY_LIVE is exactly "1"; otherwise xunit reports it SKIPPED, never passed. It
// drives the product's own pieces: AiSchemaFamily.Prepare builds each request (uncapped, so every labelled object is
// asked), the real JevDecisionOracle sends it through DecisionService with the development key, and
// AiSchemaFamily.Resolve applies the schema's own checks to Jev's picks and, separately, to the rule's
// (ReadinessRuleSet.AiSchemaRuleExcludes). Raw results go OUTSIDE the repository (~/.cache/jev-replay-ai-schema,
// overridable with JEV_REPLAY_OUT); tools/jev-replay/ai-schema/score.mjs turns them into aggregate numbers and the
// verdict. The key is read from ~/.config/typesafe/env into this process only, for one client, and is never printed.
//
//   JEV_REPLAY_LIVE=1 dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevAiSchemaReplay"
//   node tools/jev-replay/ai-schema/score.mjs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class JevAiSchemaReplay
    {
        internal static string Repo()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Semanticus.sln"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);
        }

        internal static string Key()
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "typesafe", "env");
            var m = Regex.Match(File.ReadAllText(file), @"^\s*(?:export\s+)?[A-Z0-9_]*(?:KEY|TOKEN)[A-Z0-9_]*\s*=\s*[""']?([^""'\s]+)", RegexOptions.Multiline);
            if (!m.Success) throw new InvalidOperationException("No key in the key file.");
            return m.Groups[1].Value;
        }

        internal static JsonDocument Labels() =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo(), "tools", "jev-replay", "ai-schema", "labels.json")));

        /// <summary>Where one labelled model's files come from. A UAT fixture is an archive on the Inspiron (overridable
        /// with JEV_REPLAY_FIXTURES); the AdventureWorks test model is in the vendored submodule.</summary>
        private static string Source(JsonElement model)
        {
            var src = model.GetProperty("source").GetString();
            if (src.StartsWith("repo:", StringComparison.Ordinal)) return Path.Combine(Repo(), src.Substring("repo:".Length));
            var root = Environment.GetEnvironmentVariable("JEV_REPLAY_FIXTURES")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "uat-sol", "fixtures");
            return Path.Combine(root, src.Substring("fixture:".Length));
        }

        /// <summary>Open one labelled model in a fresh engine, from a copy, and save it into the fixture's workspace so
        /// permission has a model to bind to. Null when the source is not on this machine.</summary>
        internal static async Task<DecisionFixture> OpenAsync(JsonElement model)
        {
            var source = Source(model);
            if (!File.Exists(source)) return null;
            var f = new DecisionFixture(TestEntitlements.Pro);
            string open;
            if (source.EndsWith(".tgz", StringComparison.Ordinal))
            {
                var dir = Path.Combine(f.Home, "src");
                Directory.CreateDirectory(dir);
                using (var p = Process.Start(new ProcessStartInfo("tar", $"xzf \"{source}\" -C \"{dir}\"") { UseShellExecute = false }))
                    p.WaitForExit();
                open = Path.Combine(dir, model.GetProperty("open").GetString());
            }
            else
            {
                open = Path.Combine(f.Home, Path.GetFileName(source));
                File.Copy(source, open);
            }
            await f.Engine.OpenAsync(open);
            await f.SaveAsAsync(model.GetProperty("id").GetString() + ".bim");
            return f;
        }

        private static Task<AiSchemaFamily.Prepared> PrepareAsync(DecisionFixture f) =>
            f.Sessions.Current.ReadAsync(m => AiSchemaFamily.Prepare(m, int.MaxValue));

        /// <summary>Not live: every eligible object of every labelled model carries a label, no label names an object
        /// that is not eligible, and the labels obey the schema's own rule (nothing an included measure uses is labelled
        /// exclude), so no label asks for something the product would refuse. Skipped per model whose files are not here.</summary>
        [Fact]
        public async Task The_labels_cover_every_eligible_object_and_obey_the_schema_rule()
        {
            using var labels = Labels();
            var dump = Environment.GetEnvironmentVariable("JEV_REPLAY_DUMP");
            var dumped = new Dictionary<string, object>();
            var checkedModels = 0;
            foreach (var model in labels.RootElement.GetProperty("models").EnumerateArray())
            {
                using var f = await OpenAsync(model);
                if (f == null) continue;
                checkedModels++;
                var id = model.GetProperty("id").GetString();
                var prepared = await PrepareAsync(f);
                var eligible = prepared.Asks.Select(a => a.Ref).ToList();
                if (dump != null)
                    dumped[id] = prepared.Asks.Select(a => new
                    {
                        a.Ref, a.Kind, a.TableName, a.ObjectName,
                        facts = a.Snapshot["object_facts"],
                        rule = ruleOf(f, a.Ref),
                    }).ToArray();
                if (dump != null) continue;   // a dump is for writing labels, before there are any to check

                var byRef = model.GetProperty("labels").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.Ordinal);
                Assert.True(byRef.Values.All(v => v == "include" || v == "exclude" || v == "either"), id + " has a label that is not include, exclude or either");
                var missing = eligible.Where(r => !byRef.ContainsKey(r)).ToList();
                var extra = byRef.Keys.Where(r => !eligible.Contains(r, StringComparer.Ordinal)).ToList();
                Assert.True(missing.Count == 0, id + " has no label for " + string.Join(", ", missing.Take(20)));
                Assert.True(extra.Count == 0, id + " labels objects that are not eligible: " + string.Join(", ", extra.Take(20)));

                foreach (var kv in prepared.Measures.Where(kv => byRef.TryGetValue(kv.Key, out var l) && l == "include"))
                    foreach (var used in kv.Value.Uses.Where(u => byRef.TryGetValue(u, out var l) && l == "exclude"))
                        Assert.Fail(id + ": " + kv.Key + " is labelled include but uses " + used + ", labelled exclude.");
            }
            if (dump != null)
                File.WriteAllText(dump, JsonSerializer.Serialize(dumped, new JsonSerializerOptions { WriteIndented = true }));
            Assert.True(checkedModels > 0, "No labelled model was found on this machine.");

            static bool ruleOf(DecisionFixture f, string r) =>
                f.Sessions.Current.ReadAsync(m => ReadinessRuleSet.AiSchemaRuleExcludes(m, (TabularNamedObject)ObjectRefs.Resolve(m, r))).GetAwaiter().GetResult();
        }

        [JevReplayFact]
        public async Task Replay_the_labelled_models_through_the_product()
        {
            var repo = Repo();
            var outDir = Environment.GetEnvironmentVariable("JEV_REPLAY_OUT")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "jev-replay-ai-schema");
            if (Path.GetFullPath(outDir).StartsWith(Path.GetFullPath(repo), StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to write results inside the repository.");
            Directory.CreateDirectory(outDir);
            using var labels = Labels();
            var point = DecisionRegistry.Require(DecisionRegistry.AiSchema);
            var budget = new DecisionBudget(3_000_000);
            using var jev = new JevDecisionOracle(Key(), budget);

            var models = new List<object>();
            foreach (var model in labels.RootElement.GetProperty("models").EnumerateArray())
            {
                var id = model.GetProperty("id").GetString();
                using var f = await OpenAsync(model);
                if (f == null) throw new InvalidOperationException("The labelled model " + id + " is not on this machine.");
                await f.ConsentAsync();
                var service = new DecisionService(jev, budget);
                f.Engine.UseDecisionsForTest(service);
                var prepared = await PrepareAsync(f);
                var rule = await f.Sessions.Current.ReadAsync(m => prepared.Asks.ToDictionary(a => a.Ref,
                    a => ReadinessRuleSet.AiSchemaRuleExcludes(m, (TabularNamedObject)ObjectRefs.Resolve(m, a.Ref)), StringComparer.Ordinal));

                using var gate = new SemaphoreSlim(6, 6);
                var started = DateTime.UtcNow;
                var outcomes = await Task.WhenAll(prepared.Asks.Select(async ask =>
                {
                    await gate.WaitAsync();
                    var t0 = DateTime.UtcNow;
                    try
                    {
                        var o = await f.Engine.Decisions.AskAsync(point.Name, ask.Snapshot, ask.Questions, isPro: true, enabled: true, fence: null, ct: CancellationToken.None);
                        var a = o != null && o.Asked && o.Answers.Decided ? o.Answers.For(AiSchemaFamily.Question) : null;
                        return (Ask: ask, Choice: a?.Choice, Confidence: a?.Confidence ?? 0, Ms: (DateTime.UtcNow - t0).TotalMilliseconds,
                                Note: a == null ? (o?.SkippedReason ?? o?.Answers?.NotDecidedReason ?? "no outcome") : null);
                    }
                    catch (Exception ex) { return (Ask: ask, Choice: (string)null, Confidence: 0.0, Ms: (DateTime.UtcNow - t0).TotalMilliseconds, Note: ex.GetType().Name); }
                    finally { gate.Release(); }
                }));

                // The product's own checks, over Jev's picks and over the rule's, the same way.
                var jevLines = new List<ChangeItem>();
                AiSchemaFamily.Resolve(prepared, outcomes.Where(o => o.Choice != null).Select(o => new AiSchemaFamily.Pick(o.Ask, o.Choice, o.Confidence)),
                                       point.ConfidenceBar, jevLines, new List<ModelPlanChangeRefusal>());
                // The Pro path the product runs: Jev when sure, else the rule (AiSchemaFamily.ProPick).
                var proLines = new List<ChangeItem>();
                AiSchemaFamily.Resolve(prepared, outcomes.Where(o => o.Choice != null).Select(o => AiSchemaFamily.ProPick(o.Ask, o.Choice, o.Confidence, point.ConfidenceBar)),
                                       point.ConfidenceBar, proLines, new List<ModelPlanChangeRefusal>());
                var ruleLines = new List<ChangeItem>();
                AiSchemaFamily.Resolve(prepared, prepared.Asks.Select(a => new AiSchemaFamily.Pick(a, rule[a.Ref] ? "exclude" : "include", 1.0)),
                                       point.ConfidenceBar, ruleLines, new List<ModelPlanChangeRefusal>());

                bool OutAfter(List<ChangeItem> lines, AiSchemaFamily.Ask a, bool keptOnly)
                {
                    bool Own(string r)
                    {
                        var line = lines.FirstOrDefault(l => string.Equals(l.ObjectRef, r, StringComparison.OrdinalIgnoreCase)
                                                             && (!keptOnly || l.Status != "rejected"));
                        return line != null ? line.After == "Excluded" : prepared.OwnExcluded.TryGetValue(r, out var x) && x;
                    }
                    return Own(a.Ref) || (a.Kind != AiSchemaFamily.KindTable && Own(a.TableRef));
                }

                models.Add(new
                {
                    id,
                    wallMs = (DateTime.UtcNow - started).TotalMilliseconds,
                    rows = outcomes.Select(o => new
                    {
                        @ref = o.Ask.Ref,
                        kind = o.Ask.Kind,
                        jevChoice = o.Choice,
                        jevConfidence = o.Confidence,
                        jevNote = o.Note,
                        ms = o.Ms,
                        ruleRaw = rule[o.Ask.Ref] ? "exclude" : "include",
                        // What the Plan would apply if a person approved it as it arrives (kept lines only), and what
                        // it would apply if every line Jev proposed were kept.
                        pro = OutAfter(proLines, o.Ask, keptOnly: true) ? "exclude" : "include",
                        jev = OutAfter(jevLines, o.Ask, keptOnly: true) ? "exclude" : "include",
                        jevAllLines = OutAfter(jevLines, o.Ask, keptOnly: false) ? "exclude" : "include",
                        rule = OutAfter(ruleLines, o.Ask, keptOnly: true) ? "exclude" : "include",
                    }).ToArray(),
                });
            }

            var result = new
            {
                ranUtc = DateTime.UtcNow.ToString("o"),
                point = point.Name,
                schemaVersion = point.SchemaVersion,
                confidenceBar = point.ConfidenceBar,
                requests = budget.Requests,
                inputTokens = budget.UsedInputTokens,
                models,
            };
            File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Assert.True(models.Count > 0);
        }
    }
}
