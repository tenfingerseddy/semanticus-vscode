// verify: the verifier's failing tests for round 3 of lane m11/describe-jev (6b411326, the fix for the round 2 FAIL).
// Each test names ONE defect and is LEFT FAILING on purpose: it passes once the defect is fixed. The oracle is the same
// scripted, honest oracle rounds 1 and 2 use: it answers the exact request the engine built, through the real recorded
// oracle and answer reader, and it can act like a service that is away. No test here sets JevDraftsForTest: the
// committed per-door switches decide, as they do in the product.

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
    public sealed class VerifyDescribeJevRound3DefectTests
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
        private static string Layout(DraftView v) => string.Join(", ", AllSteps(v).Select(s => s.Id + (s.Kept ? "" : " (dropped)")));

        /// <summary>Jev sure of hiding Amount; on "Qty" it only leans (the step arrives dropped); sure of nothing else.</summary>
        private static (string, double)? HideAmountLeanQty(string w, string q) =>
            w != null && w.Contains("Amount") && q == "step:hide:include" ? ("yes", 0.97)
            : w != null && w.Contains("Amount") && q == "step:hide:objRef" ? ("column:Sales/Amount", 0.95)
            : w != null && w.Contains("Qty") && q == "step:hide:include" ? ("yes", 0.6)
            : w != null && w.Contains("Qty") && q == "step:hide:objRef" ? ("column:Returns/Qty", 0.95)
            : ((string, double)?)null;

        // ---- R3-D6b: the R2-D6b class on a draft that already has steps (the builder's own open item, proven) ----------

        /// <summary>DEFECT R3-D6b (medium-low). RefineAsync asks only the NEW words once a draft has a step
        /// (DraftDoors.cs:348, "the old words' steps are already there"). That premise is false for words from an Add more
        /// words that could not draft (the service away, no permission yet, the point switched off, the budget spent): they
        /// were never drafted, and no later Add more words reads them again. A step the Plan's menu does not list at all
        /// (Save the model, Refresh a table, Publish, Run the saved checks: DraftMenu.For lists DraftMenu.All only) can then
        /// never reach the draft, while the sentence at the time said "Pick the steps. The ones your words name come first."
        /// Steps: Ctrl J "hide Amount" (drafted), Add more words "save the model" while the service is away, then Add more
        /// words "describe the Sales table" once it is back. The draft holds hide and describe; save is gone, with no word.</summary>
        [Fact]
        public async Task R3_D6b_words_added_while_Jev_could_not_draft_are_not_lost_on_a_draft_with_steps()
        {
            var (f, jev) = await ProAsync("R3AwayWithSteps.bim", HideAmountLeanQty);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("hide Amount", null, null, null, null, "human");
                Assert.Equal("hide", Layout(v));
                jev.Away = true;
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "save the model", "human");
                var away = v.Sentence;
                var menu = string.Join(", ", (v.Menu ?? Array.Empty<DraftMenuItem>()).Select(m => m.Id));
                jev.Away = false;
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "describe the Sales table", "human");
                Assert.True(AllSteps(v).Any(s => s.Id == "save"),
                    "the person typed hide Amount, then save the model (while the service was away), then describe the Sales table; "
                    + "the draft holds [" + Layout(v) + "]. While away it said: " + away + " The menu it offered: [" + menu
                    + "]. Once back it said: " + v.Sentence);
            }
        }

        // ---- R3-D6a: R2-D6a's new sentence gives an instruction that does not work on the draft it was written for -----

        /// <summary>DEFECT R3-D6a (low). R2-D6a was a menu draft whose words name 13 steps saying "only the first 12 are
        /// here". The fix says "only the first 12 were read. Add the other 1 with Add more words." On that same draft the
        /// instruction does not work: a draft still on the menu is re-asked from all its words (R2-D6b), the first 13
        /// clauses first, so the added words land past the twelfth again and are never read (DescribedSteps.From stops at
        /// MaxSteps). Steps: 12 clauses nothing recognises plus "save the model", Jev sure of nothing; then Add more words
        /// "save the model", as told.</summary>
        [Fact]
        public async Task R3_D6a_add_the_other_one_with_add_more_words_works_on_the_menu_draft_that_says_it()
        {
            var (f, jev) = await ProAsync("R3MenuThirteen.bim", (w, q) => null);
            using (f)
            {
                var words = string.Join(", ", Enumerable.Range(1, 12).Select(i => "zzqx wibble " + i.ToString(CultureInfo.InvariantCulture))) + ", save the model";
                var v = await f.Engine.ComposeTaskAsync(words, null, null, null, null, "human");
                Assert.Empty(AllSteps(v));
                Assert.Contains("Add the other 1 with Add more words", v.Sentence);
                var told = v.Sentence;
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "save the model", "human");
                var asked = jev.Seen.Skip(jev.Seen.Count - 12).Select(r => (string)r.Snapshot["draft_words"]).ToArray();
                Assert.True(AllSteps(v).Any(s => s.Id == "save"),
                    "the draft said: " + told + " After Add more words \"save the model\" it holds [" + Layout(v) + "] and says: "
                    + v.Sentence + " The last asks were: " + string.Join(" / ", asked));
            }
        }

        // ---- R3-D7s: the creation sentence leaves out a dropped step when the words named another ---------------------

        /// <summary>DEFECT R3-D7s (low). With R2-D7 fixed, a clause only Jev leaned to arrives dropped beside a step of
        /// its kind. When the word match named any step (d.WordSteps &gt; 0), ComposeTaskAsync says JevDraftedWithWords or
        /// WordsDrafted, neither of which says a step arrives dropped (DraftRunner.cs:196-203); only the all-Jev branch
        /// does (JevDraftedWithUnsure). Add more words always says it (MoreWordsAdded, D4). Steps: "hide Amount and Qty"
        /// with Jev leaning on both clauses: the word match keeps "hide Amount", "Qty" arrives dropped with its pick, and
        /// the sentence reads "Your words name these steps."</summary>
        [Fact]
        public async Task R3_D7s_the_draft_sentence_says_a_step_arrives_dropped_when_the_words_named_another()
        {
            var (f, _) = await ProAsync("R3LeanBoth.bim", (w, q) =>
                (w != null && (w.Contains("Amount") || w.Contains("Qty"))) && q == "step:hide:include" ? ("yes", 0.6)
                : w != null && w.Contains("Qty") && q == "step:hide:objRef" ? ("column:Returns/Qty", 0.95)
                : ((string, double)?)null);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("hide Amount and Qty", null, null, null, null, "human");
                Assert.Equal("hide, hide-2 (dropped)", Layout(v));
                Assert.True((v.Sentence ?? "").Contains("not sure", StringComparison.Ordinal),
                    "the draft holds [" + Layout(v) + "], one step Jev only leaned to, and its sentence reads: " + v.Sentence);
            }
        }

        // ---- R3-D6b-copy: the R2-D6b re-ask credits every step to the newest words -----------------------------------

        /// <summary>DEFECT R3-D6b-copy (low). On a draft still on the menu, the R2-D6b fix drafts ALL the words typed so far,
        /// and the sentence then says MoreWordsAdded's "Your new words name 3 more steps." when the new words name one of
        /// them: the other two came from words typed while the service was away. Steps: Ctrl J "refresh Sales" while the
        /// service is away, Add more words "save the model" (still away), then Add more words "describe the Sales table".</summary>
        [Fact]
        public async Task R3_D6b_copy_the_sentence_does_not_credit_earlier_words_to_the_new_ones()
        {
            var (f, jev) = await ProAsync("R3MenuCredit.bim", (w, q) => null);
            using (f)
            {
                jev.Away = true;
                var v = await f.Engine.ComposeTaskAsync("refresh Sales", null, null, null, null, "human");
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "save the model", "human");
                jev.Away = false;
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "describe the Sales table", "human");
                Assert.Equal(3, AllSteps(v).Count(s => s.Kept));
                Assert.True(!(v.Sentence ?? "").Contains("new words name 3", StringComparison.Ordinal),
                    "the new words were \"describe the Sales table\", one step; the draft holds [" + Layout(v) + "] and says: " + v.Sentence);
            }
        }
    }
}
