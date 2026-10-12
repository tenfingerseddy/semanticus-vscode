// review-fix tests for the verifier's round on m11/sol-fixes-1 (c4793fbd): the cases around its eight defect tests that
// those do not pin. Each names the defect it guards. The Jev oracle answers the exact request the engine built, through
// the real recorded oracle and answer reader, as the verifier's tests do.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class SolFixes1Round2Tests
    {
        private sealed class ScriptedOracle : IDecisionOracle
        {
            private readonly Func<string, string, (string Choice, double P)?> _script;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            public ScriptedOracle(Func<string, string, (string, double)?> script) { _script = script; }
            public bool Available => true;
            public IReadOnlyList<string> Asked { get { lock (_seen) return _seen.Where(r => r.PointName == DecisionRegistry.ComposeDraft).Select(r => (string)r.Snapshot["draft_words"]).ToList(); } }

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (_seen) _seen.Add(request);
                if (request.PointName != DecisionRegistry.ComposeDraft)
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "this test answers compose.draft only"));
                var words = request.Snapshot.TryGetValue("draft_words", out var w) ? w as string : null;
                var sb = new StringBuilder("{\"entries\":[{\"digest\":").Append(JsonSerializer.Serialize(request.Digest))
                    .Append(",\"point\":").Append(JsonSerializer.Serialize(request.PointName))
                    .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":30},\"answers\":{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    var a = _script(words, q.Name);
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(':');
                    if (q.Kind == DecisionQuestionKind.Noul)
                        sb.Append("{\"type\":\"noul\",\"noul\":").Append((a?.P ?? 0.02).ToString(CultureInfo.InvariantCulture)).Append('}');
                    else
                    {
                        var (choice, confidence) = a ?? (q.Name.EndsWith(":wanted", StringComparison.Ordinal) ? ("yes", 0.95) : ("none", 0.95));   // join part 2: the last question fails closed, so a script answers it yes
                        sb.Append("{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                          .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append('}');
                    }
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-sf1-round2").AskAsync(request, ct);
            }
        }

        private static async Task<(DecisionFixture F, ScriptedOracle Jev)> ProAsync(string file, Func<string, string, (string, double)?> script)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync(file);
            await f.SaveAsAsync(file);
            await f.ConsentAsync();
            var jev = new ScriptedOracle(script);
            f.Engine.UseDecisionsForTest(new DecisionService(jev, new DecisionBudget()));
            return (f, jev);
        }

        private static string Clauses(int n, string what) =>
            string.Join(", ", Enumerable.Range(1, n).Select(i => what + " " + i.ToString(CultureInfo.InvariantCulture)));

        /// <summary>The test gap the verifier named (round 3's, still open): Add more words whose own words name more than
        /// twelve steps says the rest were not read, and keeps them for the next one.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Add_more_words_past_the_limit_says_the_rest_were_not_read()
        {
            var (f, jev) = await ProAsync("R2LeftOut.bim", (w, q) => null);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("hide the keys", null, null, null, null, "human");
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, Clauses(14, "zzqx wibble"), "human");
                Assert.Contains(DraftCopy.PlanStepsLeftOut(2, DescribedSteps.MaxSteps), v.Sentence);
                Assert.Contains(v.Notes, n => n.StartsWith("Not read yet: \"zzqx wibble 13\", \"zzqx wibble 14\".", StringComparison.Ordinal));
            }
        }

        /// <summary>D8, the other side: when the new words leave no room for every word kept from before, the ones that wait
        /// are said, and the next Add more words reads them, oldest first.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Add_more_words_says_the_earlier_words_that_wait_and_reads_them_next()
        {
            var (f, jev) = await ProAsync("R2Waiting.bim", (w, q) => null);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync(Clauses(20, "zzqx wibble"), null, null, null, null, "human");   // 8 kept
                var before = jev.Asked.Count;
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, Clauses(10, "qqzv flonk"), "human");
                var asked = jev.Asked.Skip(before).ToList();
                Assert.Equal(12, asked.Count);
                Assert.Equal(new[] { "zzqx wibble 13", "zzqx wibble 14" }, asked.Where(a => a.StartsWith("zzqx", StringComparison.Ordinal)).OrderBy(a => a, StringComparer.Ordinal));
                Assert.Contains(DraftCopy.EarlierWordsStillWaiting(6), v.Sentence);
                Assert.Contains(v.Notes, n => n.StartsWith("Not read yet: \"zzqx wibble 15\"", StringComparison.Ordinal));

                before = jev.Asked.Count;
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "qqzv flonk 11", "human");
                asked = jev.Asked.Skip(before).ToList();
                Assert.Equal(Enumerable.Range(15, 6).Select(i => "zzqx wibble " + i.ToString(CultureInfo.InvariantCulture)).Append("qqzv flonk 11"), asked.OrderBy(a => a.StartsWith("qqzv", StringComparison.Ordinal) ? 1 : 0).ThenBy(a => a.Length).ThenBy(a => a, StringComparer.Ordinal));
                Assert.DoesNotContain("not read yet", v.Sentence, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>D3 on the Workflows door, the shape at a length a person might type: twelve steps read, twelve kept as
        /// steps of their own, and every clause after them kept together in one step that holds their words.</summary>
        [Fact]
        public async Task A_long_description_keeps_the_clauses_past_the_limit_bounded_and_whole()
        {
            var (f, _) = await ProAsync("R2Bounded.bim", (w, q) => null);
            using (f)
            {
                var d = await McpTools.DescribeWorkflowSteps(f.Engine, Clauses(40, "zzqx wibble"));
                Assert.Equal(DescribedSteps.MaxSteps + DescribedSteps.MaxNotReadSteps + 1, d.Steps.Length);
                Assert.Equal(28, d.LeftOut);
                var rest = d.Steps.Last();
                Assert.True(rest.NotRead);
                Assert.Equal(DraftCopy.RestNotReadTitle(16), rest.Title);
                Assert.Equal(string.Join(", ", Enumerable.Range(25, 16).Select(i => "zzqx wibble " + i.ToString(CultureInfo.InvariantCulture))), rest.Words);
                var file = WorkflowParser.Parse(d.Document.Text);
                Assert.Null(file.Error);
                Assert.Equal(d.Steps.Length, file.Steps.Length);
                Assert.Contains("zzqx wibble 40", d.Document.Text);   // no clause lost
                Assert.StartsWith("25 steps from your words", d.Sentence);
                Assert.Contains("\"zzqx wibble 13\", \"zzqx wibble 14\", \"zzqx wibble 15\", \"zzqx wibble 16\", \"zzqx wibble 17\" and 23 more", d.Sentence);
            }
        }

        /// <summary>D4 across folders: Save As to another folder copies the Primer to the new model's name there, and the
        /// session's undo takes back the Primer the session now has, leaving the old model's saved Primer as it was.</summary>
        [Fact]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Save_as_to_another_folder_undoes_the_new_models_Primer_and_leaves_the_old_one()
        {
            var root = Path.Combine(Path.GetTempPath(), "sem-sf1-r2", Guid.NewGuid().ToString("N"));
            var a = Path.Combine(root, "a");
            var b = Path.Combine(root, "b");
            Directory.CreateDirectory(a);
            Directory.CreateDirectory(b);
            File.Copy(TestModels.FindBim(), Path.Combine(a, "Model.bim"));
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(Path.Combine(a, "Model.bim"));
                var starter = (await engine.GetPrimerAsync()).Markdown.Replace("\r\n", "\n");
                var saved = await McpTools.SetModelPrimer(engine, starter.Replace("## Gotchas\n\n_Add what people and the AI Assistant should know._", "## Gotchas\n\nFiscal year starts in July."));
                var oldText = await File.ReadAllTextAsync(saved.FilePath);
                await engine.SaveAsync(Path.Combine(b, "Model2.bim"), "bim", false, "human");
                var carried = Directory.GetFiles(Path.Combine(b, Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(saved.FilePath))), "primers"), "*.md").Single();
                Assert.Equal(oldText, await File.ReadAllTextAsync(carried));

                await McpTools.UndoChange(engine);
                Assert.False(File.Exists(carried));                                  // the Primer save, taken back on the copy
                Assert.Equal(oldText, await File.ReadAllTextAsync(saved.FilePath));   // the old model's own Primer untouched
                await McpTools.RedoChange(engine);
                Assert.Equal(oldText, await File.ReadAllTextAsync(carried));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
