// The compose.draft replay (composer lane L5): does Jev draft the right steps from a person's words?
//
// It runs ONLY when JEV_REPLAY_LIVE is exactly "1"; otherwise xunit reports it SKIPPED, never passed. It drives the
// product's own path end to end: the real JevDraftBuilder builds each request, the real JevDecisionOracle sends it to
// the vendor (the development route, SEMANTICUS_JEV_KEY's), and the real engine reads the answer back. Nothing is
// mirrored, so what is scored is what the product would do.
//
// The labelled set is tools/jev-replay/compose/labels.json (reviewer-written: what each step of each sentence asks for).
// The bar is tools/jev-replay/compose/bar.json, committed before the first run. This writes raw results OUTSIDE the
// repository (~/.cache/jev-replay-compose/results.json, overridable with JEV_REPLAY_OUT); tools/jev-replay/compose/
// score.mjs turns them into aggregate numbers and the verdict. The key is read from ~/.config/typesafe/env into this
// process only, for one client, and is never printed, returned or written.
//
//   JEV_REPLAY_LIVE=1 dotnet test Semanticus.Tests --filter "FullyQualifiedName~JevComposeReplay"
//   node tools/jev-replay/compose/score.mjs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>A fact that runs only when JEV_REPLAY_LIVE is exactly "1", and says so when it is skipped.</summary>
    public sealed class JevReplayFactAttribute : FactAttribute
    {
        public JevReplayFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("JEV_REPLAY_LIVE") != "1")
                Skip = "The compose.draft replay calls the Jev service; set JEV_REPLAY_LIVE=1 to run it.";
        }
    }

    public sealed class JevComposeReplay
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

        /// <summary>Not live: the labels line up with the engine. Each description cuts into as many steps as it labels,
        /// and every labelled op is a template the chooser offers on Workflows, so a score never compares unlike things.</summary>
        [Theory]
        [InlineData("labels.json")]
        [InlineData("labels-fresh.json")]
        [InlineData("labels-plan2.json")]
        public void The_labels_line_up_with_the_splitter_and_the_templates(string file)
        {
            using var labels = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo(), "tools", "jev-replay", "compose", file)));
            var ops = JevDraftBuilder.Candidates(JevDraftBuilder.Scope.Describe).Select(t => t.Op).ToHashSet(StringComparer.Ordinal);
            var n = 0;
            foreach (var item in labels.RootElement.GetProperty("items").EnumerateArray())
            {
                n++;
                var id = item.GetProperty("id").GetString();
                var clauses = item.GetProperty("clauses").EnumerateArray().ToArray();
                Assert.True(DescribedSteps.From(item.GetProperty("words").GetString()).Steps.Length == clauses.Length, id + " cuts into a different number of steps than it labels");
                foreach (var op in clauses.SelectMany(c => c.EnumerateArray()).Select(e => e.GetString()))
                    Assert.True(ops.Contains(op), id + " labels " + op + ", which the chooser does not offer");
            }
            Assert.True(n >= 30, n + " labelled descriptions");
        }

        [JevReplayFact]
        public async Task Replay_the_labelled_words_through_the_product()
        {
            var repo = Repo();
            var outDir = Environment.GetEnvironmentVariable("JEV_REPLAY_OUT")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "jev-replay-compose");
            if (Path.GetFullPath(outDir).StartsWith(Path.GetFullPath(repo), StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to write results inside the repository.");
            Directory.CreateDirectory(outDir);
            // JEV_REPLAY_LABELS picks the set: labels.json (the tuning set) by default, labels-fresh.json for the bar run.
            var labelFile = Environment.GetEnvironmentVariable("JEV_REPLAY_LABELS") ?? "labels.json";
            using var labels = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "tools", "jev-replay", "compose", labelFile)));

            using var f = new DecisionFixture(TestEntitlements.Pro);
            // A Contoso-style model: the table names are the only model detail compose.draft sends.
            await f.Engine.CreateModelAsync("Contoso", 1604);
            foreach (var t in new[] { "Sales", "Product", "Customer", "Date", "Returns", "Budget" })
                await f.Engine.CreateTableAsync(t, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await f.SaveAsAsync("Contoso.bim");
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = true;
            var budget = new DecisionBudget(2_000_000);
            using var jev = new JevDecisionOracle(Key(), budget);
            f.Engine.UseDecisionsForTest(new DecisionService(jev, budget));

            var rows = new List<object>();
            foreach (var item in labels.RootElement.GetProperty("items").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                var words = item.GetProperty("words").GetString();
                var byWords = DescribedSteps.From(words);
                var started = DateTime.UtcNow;
                var described = await f.Engine.DescribeWorkflowStepsAsync(words);
                var describeMs = (DateTime.UtcNow - started).TotalMilliseconds;
                started = DateTime.UtcNow;
                DraftView composed = null;
                string composeError = null;
                try { composed = await f.Engine.ComposeTaskAsync(words, null, null, null, null, "human"); }
                catch (Exception ex) { composeError = ex.GetType().Name + ": " + ex.Message; }
                var composeMs = (DateTime.UtcNow - started).TotalMilliseconds;
                rows.Add(new
                {
                    id,
                    clauses = described.Steps.Select((s, i) => new
                    {
                        words = s.Words,
                        jev = s.ChosenBy == "jev" ? s.Ops : Array.Empty<string>(),
                        shown = s.Ops,
                        chosenBy = s.ChosenBy,
                        byWords = byWords.Steps.ElementAtOrDefault(i)?.Ops ?? Array.Empty<string>(),
                    }).ToArray(),
                    jevAsked = described.JevAsked,
                    jevNote = described.JevNote,
                    describeMs,
                    compose = composed == null ? null : new
                    {
                        // Drafted, not the menu: by Jev, by Jev and the words, or by the words (the per-clause rule).
                        jevDrafted = composed.MenuLine == DraftCopy.JevDraftedMenuLine || composed.MenuLine == DraftCopy.JevAndWordsMenuLine
                                     || composed.MenuLine == DraftCopy.WordsDraftedMenuLine,
                        kept = composed.Steps.Concat(composed.After).Where(s => s.Kept).Select(s => s.Id).ToArray(),
                        dropped = composed.Steps.Concat(composed.After).Where(s => !s.Kept).Select(s => s.Id).ToArray(),
                    },
                    composeError,
                    composeMs,
                });
            }

            var result = new
            {
                ranUtc = DateTime.UtcNow.ToString("o"),
                labels = labelFile,
                point = DecisionRegistry.ComposeDraft,
                requests = budget.Requests,
                inputTokens = budget.UsedInputTokens,
                templates = JevDraftBuilder.Candidates(JevDraftBuilder.Scope.Describe).Select(t => new { t.Id, t.Op }).ToArray(),
                rows,
            };
            File.WriteAllText(Path.Combine(outDir, Path.GetFileNameWithoutExtension(labelFile).Replace("labels", "results") + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Assert.True(rows.Count > 0);
        }
    }
}
