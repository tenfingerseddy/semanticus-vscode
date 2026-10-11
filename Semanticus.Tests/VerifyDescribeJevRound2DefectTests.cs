// verify: the verifier's failing tests for round 2 of lane m11/describe-jev (84ac2227, the fix for the first FAIL).
// Each test names ONE defect and is LEFT FAILING on purpose: it passes once the defect is fixed. The oracle is the same
// scripted, honest oracle the round 1 tests use: it answers the exact request the engine built, through the real
// recorded oracle and answer reader, and it can be made to act like a service that is away. No test here sets
// JevDraftsForTest: the committed per-door switches decide, as they do in the product.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VerifyDescribeJevRound2DefectTests
    {
        private sealed class ScriptedOracle : IDecisionOracle
        {
            private readonly Func<string, string, (string Choice, double P)?> _script;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            /// <summary>True while the service is away: every ask comes back not decided, as in the 26 Sep outage.</summary>
            public volatile bool Away;
            public ScriptedOracle(Func<string, string, (string, double)?> script) { _script = script; }
            public bool Available => true;
            public IReadOnlyList<DecisionRequest> Seen { get { lock (_seen) return _seen.Where(r => r.PointName == DecisionRegistry.ComposeDraft).ToList(); } }

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (_seen) _seen.Add(request);
                if (Away) return Task.FromResult(DecisionAnswers.NotDecided(request, "the advice service could not be reached"));
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
                    {
                        var p = a?.P ?? 0.02;
                        sb.Append("{\"type\":\"noul\",\"noul\":").Append(p.ToString(CultureInfo.InvariantCulture)).Append('}');
                    }
                    else
                    {
                        var (choice, confidence) = a ?? (q.Name.EndsWith(":wanted", StringComparison.Ordinal) ? ("yes", 0.95) : ("none", 0.95));   // join part 2: the last question fails closed, so a script answers it yes
                        sb.Append("{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                          .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append('}');
                    }
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct);
            }
        }

        private static async Task<(DecisionFixture F, ScriptedOracle Jev)> ProAsync(string file, Func<string, string, (string, double)?> script)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync(file);
            await f.Engine.CreateTableAsync("Returns", "human");
            await f.Engine.CreateColumnAsync("table:Returns", "Qty", "Int64", null, "human");
            await f.SaveAsAsync(file);
            await f.ConsentAsync();
            var jev = new ScriptedOracle(script);
            f.Engine.UseDecisionsForTest(new DecisionService(jev, new DecisionBudget()));
            return (f, jev);
        }

        private static IEnumerable<DraftStepView> AllSteps(DraftView v) => v.Steps.Concat(v.After);

        // ---- D6b, not closed: a second Add more words on a draft that is still the menu ------------------------------

        /// <summary>DEFECT R2-D6b (D6b is fixed for the first Add more words only). RefineAsync asks the old words and the
        /// new ones cut apart by a line break when the draft is still an empty menu, but it STORES the words joined with a
        /// space (d.Words). So when an earlier Add more words did not draft (the service was away, as in the 26 Sep outage,
        /// or permission was not yet given), the next Add more words re-asks "refresh Sales save the model" as ONE clause:
        /// the word match names one step for it, and the other step the person typed is lost with no word.
        /// Steps: Ctrl J "refresh Sales" while the service is away (the menu opens), Add more words "save the model" (still
        /// away, the menu again), then Add more words "describe the Sales table" once it is back.</summary>
        [Fact]
        public async Task R2_D6b_a_second_add_more_words_on_a_menu_draft_keeps_the_earlier_new_words_as_their_own_step()
        {
            var (f, jev) = await ProAsync("R2RefineTwice.bim", (w, q) => null);   // Jev sure of nothing once back: the words decide
            using (f)
            {
                jev.Away = true;
                var v = await f.Engine.ComposeTaskAsync("refresh Sales", null, null, null, null, "human");
                Assert.Empty(AllSteps(v));
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "save the model", "human");
                Assert.Empty(AllSteps(v));
                jev.Away = false;
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "describe the Sales table", "human");
                var kept = AllSteps(v).Where(s => s.Kept).Select(s => s.Id).ToArray();
                var asked = jev.Seen.Skip(jev.Seen.Count - 2).Select(r => (string)r.Snapshot["draft_words"]).ToArray();
                Assert.True(kept.Contains("refresh") && kept.Contains("save") && kept.Contains("describe"),
                    "the person typed refresh Sales, save the model and describe the Sales table; the draft kept only ["
                    + string.Join(", ", kept) + "]. The last Add more words asked Jev about: " + string.Join(" | ", asked)
                    + ". Sentence: " + v.Sentence);
            }
        }

        // ---- D7, not closed for a clause only Jev's leaning can stand for -------------------------------------------

        /// <summary>DEFECT R2-D7 (D7 is fixed for sure and word-matched steps only). The per-clause rule promises "a
        /// clause neither can decide keeps what Jev leaned to, dropped" and "never drop a clause". MergeClauses folds a
        /// leaning step whose kind is already on the draft, which is right for an EXTRA leaning step in a clause Jev was
        /// sure of something else, but here the leaning step is the clause's ONLY step: "also Qty please" names no step the
        /// word match knows, and Jev leaned to hiding Qty. Because "hide Amount" already drafted a hide step, the second
        /// clause and its object vanish with no word, exactly the round 1 D7 symptom.
        /// Steps: Ctrl J "hide Amount, then also Qty please" with Jev sure of the first clause and leaning on the second;
        /// and the same two clauses the other way round, where the sure step takes the leaning one's place.</summary>
        [Fact]
        public async Task R2_D7_a_clause_only_Jev_leaned_to_is_not_lost_when_another_clause_has_its_kind()
        {
            var (f, jev) = await ProAsync("R2LeanFold.bim", (w, q) =>
                w != null && w.Contains("Amount") && q == "step:hide:include" ? ("yes", 0.97)
                : w != null && w.Contains("Amount") && q == "step:hide:objRef" ? ("column:Sales/Amount", 0.95)
                : w != null && w.Contains("Qty") && q == "step:hide:include" ? ("yes", 0.6)     // leans: arrives dropped
                : w != null && w.Contains("Qty") && q == "step:hide:objRef" ? ("column:Returns/Qty", 0.95)
                : ((string, double)?)null);
            using (f)
            {
                var lost = new List<string>();
                foreach (var words in new[] { "hide Amount, then also Qty please", "also Qty please, then hide Amount" })
                {
                    var clauses = DescribedSteps.From(words).Steps;
                    Assert.Equal(2, clauses.Length);
                    // The word match names nothing for "Qty please": only Jev's leaning can stand for that clause.
                    Assert.Null(clauses.Single(c => c.Words.Contains("Qty")).Op);
                    var v = await f.Engine.ComposeTaskAsync(words, null, null, null, null, "human");
                    var hides = AllSteps(v).Where(s => s.Id.StartsWith("hide", StringComparison.Ordinal)).ToArray();
                    var picks = v.Questions.Where(q => q.Name.StartsWith("hide", StringComparison.Ordinal) && q.Name.EndsWith("-objRef", StringComparison.Ordinal))
                        .Select(q => q.Name + "=" + q.Answer).ToArray();
                    if (hides.Length != 2 || !picks.Any(p => p.EndsWith("column:Returns/Qty", StringComparison.Ordinal)))
                        lost.Add("\"" + words + "\" drafted " + hides.Length + " hide step(s) ["
                            + string.Join(", ", hides.Select(s => s.Id + (s.Kept ? "" : " (dropped)"))) + "] with picks [" + string.Join(", ", picks)
                            + "]; sentence: " + v.Sentence);
                }
                Assert.True(jev.Seen.Count >= 2);   // the second order's clauses may be served from the decision cache
                Assert.True(lost.Count == 0, "two clauses each asked to hide something, and the clause only Jev's leaning stands for "
                    + "is gone with no word: " + string.Join(" | ", lost));
            }
        }

        // ---- D6a's sentence on a draft that opened as the menu --------------------------------------------------------

        /// <summary>DEFECT R2-D6a (low). ComposeTaskAsync adds DraftCopy.StepsLeftOut whenever the words name more than 12
        /// steps, even when Jev was sure of nothing and the words matched nothing, so the draft opened as the menu with no
        /// step on it. The sentence then says "only the first 12 are here" about a draft that holds none.</summary>
        [Fact]
        public async Task R2_D6a_a_menu_draft_with_no_steps_does_not_say_twelve_are_here()
        {
            var (f, jev) = await ProAsync("R2MenuThirteen.bim", (w, q) => null);   // Jev sure of nothing, and no word match
            using (f)
            {
                var words = string.Join(", ", Enumerable.Range(1, 13).Select(i => "zzqx wibble " + i.ToString(CultureInfo.InvariantCulture)));
                var v = await f.Engine.ComposeTaskAsync(words, null, null, null, null, "human");
                Assert.Empty(AllSteps(v));
                Assert.True(!(v.Sentence ?? "").Contains("are here", StringComparison.Ordinal),
                    "the draft holds no step, and its sentence reads: " + v.Sentence);
            }
        }
    }
}
