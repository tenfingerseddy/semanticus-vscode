// verify: the verifier's failing tests for m11/sol-fixes-1 round 6 (c08a452f). Each test names ONE defect and is LEFT
// FAILING on purpose: it passes once the defect is fixed. The phrasings are from the verifier's labels, committed before any
// run (Semanticus.Tests/fixtures/verify-sol-fixes-1-r6-adding-attacks.json, commit 8d206bc6). Jev is the scripted, honest
// oracle the earlier rounds use: it answers the exact request the engine built, through the real recorded oracle and answer
// reader, either saying no to every step (every clause takes the Plan's word match, where THE ADDING RULE version 4 decides)
// or sure of every step that adds (its picks go through the same rule). Real Jev keeps the same adds for these phrasings on
// both doors (verifier round 6 evidence, probe-live.jsonl).

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
    public sealed class VerifySolFixes1Round6DefectTests
    {
        /// <summary>Answers compose.draft with one confidence for every include of a template that adds and one for every
        /// other; 0.02 is a sure no.</summary>
        private sealed class Jev : IDecisionOracle
        {
            private readonly double _adds, _others;
            public Jev(double adds, double others) { _adds = adds; _others = others; }
            public bool Available => true;

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PointName != DecisionRegistry.ComposeDraft)
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "this test answers compose.draft only"));
                var sb = new StringBuilder("{\"entries\":[{\"digest\":").Append(JsonSerializer.Serialize(request.Digest))
                    .Append(",\"point\":").Append(JsonSerializer.Serialize(request.PointName))
                    .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":30},\"answers\":{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(':');
                    if (q.Kind == DecisionQuestionKind.Noul)
                    {
                        var adds = DraftMenu.All.Any(t => AddingRule.Adds(t.Op) && q.Name == JevDraftBuilder.Q(t.Id, JevDraftBuilder.Include));
                        sb.Append("{\"type\":\"noul\",\"noul\":").Append((adds ? _adds : _others).ToString(CultureInfo.InvariantCulture)).Append('}');
                    }
                    else if (q.Name.EndsWith(":wanted", StringComparison.Ordinal)) sb.Append("{\"type\":\"choice\",\"choice\":\"yes\",\"confidence\":0.95}");   // join part 2: the last question fails closed, so a script answers it yes
                    else sb.Append("{\"type\":\"choice\",\"choice\":\"none\",\"confidence\":0.95}");
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-vsf6r6").AskAsync(request, ct);
            }
        }

        private static readonly Jev SaysNo = new Jev(0.02, 0.02);
        private static readonly Jev SureOfEveryAdd = new Jev(0.99, 0.02);

        private static async Task<DecisionFixture> ProAsync(string name, string jev, bool consent = true)
        {
            var file = name + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim";
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync(file);
            await f.SaveAsAsync(file);
            if (consent) await f.ConsentAsync();
            f.Engine.UseDecisionsForTest(new DecisionService(jev == "sure" ? SureOfEveryAdd : SaysNo, new DecisionBudget()));
            return f;
        }

        private static Task<DraftView> ComposeOnDoor(LocalEngine engine, string door, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).composeTask(words: words) : McpTools.ComposeTask(engine, words: words);

        private static Task<DraftView> AddMoreWordsOnDoor(LocalEngine engine, string door, string draftId, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).editDraft(draftId, "refine", text: words) : McpTools.EditDraft(engine, draftId, "refine", text: words);

        private static readonly string[] AddIds = { "add-measure", "relate", "import", "add-column", "calc-group", "role" };

        /// <summary>The Plan's steps that add (their op creates), KEPT, by title.</summary>
        private static string[] KeptAdds(DraftView v) =>
            v.Steps.Concat(v.After ?? Array.Empty<DraftStepView>())
                .Where(s => s.Kept && AddIds.Any(a => s.Id == a || s.Id.StartsWith(a + "-", StringComparison.Ordinal) && s.Id.Substring(a.Length + 1).All(char.IsDigit)))
                .Select(s => s.Title).ToArray();

        private static IEnumerable<object[]> Rows(params string[] words) =>
            words.SelectMany(w => new[] { "studio", "agent" }.SelectMany(d => new[] { "no", "sure" }.Select(j => new object[] { d, j, w })));

        public static IEnumerable<object[]> DoorsAndJev() =>
            new[] { "studio", "agent" }.SelectMany(d => new[] { "no", "sure" }.Select(j => new object[] { d, j }));

        // ---- VR13: a sign version 4 lists, in a form its presence check does not read ---------------------------------

        public static IEnumerable<object[]> SignsInOtherForms() => Rows(
            "add the Managers role to the roles that should probably go",
            "add Total Sales to the measures that can safely go",
            "add the Rebate measure to the old ones, they must all eventually go",
            "add the Managers role to that list, and let all of them go",
            "add the Budget measure to the old ones, I want them all gone",
            "add the Managers role to the roles that won't be needed after the move",
            "add Total Sales to the measures that haven't been used in years",
            "add Total Cost to the measures we won't need next year",
            "add the Managers role to the roles that gotta go");

        /// <summary>VR13 (the harmful kind: the opposite change is drafted as a KEPT step). Version 4 f: the veto is "a presence
        /// check over the whole sentence: if any take-away sign in any form appears anywhere, at any distance ... No distance
        /// windows", and its signs include "go"/"gone" after should, must, can, have to and all, "not needed", "don't need"
        /// and "unused". The sign pattern reads "go" only right after the modal or after one of nine listed words
        /// (AddingRule.cs:352-353: "should probably go", "can safely go", "must all eventually go" are missed), "go" after
        /// "all" only beside it and never "all gone" (:353), "not" only as not, isn't, aren't, wasn't or weren't (:358), so
        /// "won't be needed" and "haven't been used" are missed, "don't need" only after don't, doesn't or didn't (:360), so
        /// "won't need" is missed, and "got to" but not "gotta", which the lead-ins read as the same words (:469). Each
        /// sentence adds the thing to the ones that are to go, and the draft keeps the add, on both doors, with Jev saying
        /// no and with Jev sure of every add.</summary>
        [Theory]
        [MemberData(nameof(SignsInOtherForms))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR13_a_listed_sign_in_a_form_the_presence_check_does_not_read_drafts_no_kept_add(string door, string jev, string words)
        {
            using var f = await ProAsync("VR13", jev);
            var v = await ComposeOnDoor(f.Engine, door, words);
            Assert.True(KeptAdds(v).Length == 0, words + " => kept " + string.Join(", ", KeptAdds(v)));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR13_a_listed_sign_in_a_form_the_presence_check_does_not_read_drafts_no_kept_add_SampleRows() => SignsInOtherForms().Take(1);

        [Theory]
        [MemberData(nameof(VR13_a_listed_sign_in_a_form_the_presence_check_does_not_read_drafts_no_kept_add_SampleRows))]
        public Task VR13_a_listed_sign_in_a_form_the_presence_check_does_not_read_drafts_no_kept_add_sample(string door, string jev, string words) => VR13_a_listed_sign_in_a_form_the_presence_check_does_not_read_drafts_no_kept_add(door, jev, words);

        /// <summary>VR13 on the rule alone, with no engine: a clause of each of these words reads as asking to add.</summary>
        [Theory]
        [InlineData("add the Managers role to the roles that should probably go")]
        [InlineData("add Total Sales to the measures that can safely go")]
        [InlineData("add the Rebate measure to the old ones, they must all eventually go")]
        [InlineData("add the Managers role to that list, and let all of them go")]
        [InlineData("add the Budget measure to the old ones, I want them all gone")]
        [InlineData("add the Managers role to the roles that won't be needed after the move")]
        [InlineData("add Total Sales to the measures that haven't been used in years")]
        [InlineData("add Total Cost to the measures we won't need next year")]
        [InlineData("add the Managers role to the roles that gotta go")]
        [InlineData("Add the Managers role to the review list. Every role on that list should probably go.")]
        public void VR13_rule_reads_no_clause_of_these_words_as_asking_to_add(string words)
        {
            var clauses = DescribedSteps.ClausesOf(words, out _, out var sentences);
            Assert.All(AddingRule.Read(clauses, sentences), r => Assert.False(r.AsksToAdd));
        }

        /// <summary>VR13 in a later sentence (version 4 k): "Every role on that list should probably go." takes away roles, the
        /// kind the first sentence adds to that list, and the draft keeps Add a role.</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR13_a_later_sentence_with_the_sign_in_another_form_vetoes_the_add(string door, string jev)
        {
            using var f = await ProAsync("VR13k", jev);
            var v = await ComposeOnDoor(f.Engine, door, "Add the Managers role to the review list. Every role on that list should probably go.");
            Assert.True(KeptAdds(v).Length == 0, "kept " + string.Join(", ", KeptAdds(v)));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR13_a_later_sentence_with_the_sign_in_another_form_vetoes_the_add_SampleRows() => DoorsAndJev().Take(1);

        [Theory]
        [MemberData(nameof(VR13_a_later_sentence_with_the_sign_in_another_form_vetoes_the_add_SampleRows))]
        public Task VR13_a_later_sentence_with_the_sign_in_another_form_vetoes_the_add_sample(string door, string jev) => VR13_a_later_sentence_with_the_sign_in_another_form_vetoes_the_add(door, jev);

        /// <summary>VR13 in the draft's last sentence for Add more words (version 4 k): "The old roles should probably go.",
        /// then "Add the Managers role to them."; the draft keeps Add a role.</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR13_the_drafts_last_sentence_with_the_sign_in_another_form_vetoes_the_add(string door, string jev)
        {
            using var f = await ProAsync("VR13m", jev);
            var v = await ComposeOnDoor(f.Engine, door, "The old roles should probably go.");
            Assert.Empty(KeptAdds(v));
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "Add the Managers role to them.");
            Assert.True(KeptAdds(v).Length == 0, "kept " + string.Join(", ", KeptAdds(v)));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR13_the_drafts_last_sentence_with_the_sign_in_another_form_vetoes_the_add_SampleRows() => DoorsAndJev().Take(1);

        [Theory]
        [MemberData(nameof(VR13_the_drafts_last_sentence_with_the_sign_in_another_form_vetoes_the_add_SampleRows))]
        public Task VR13_the_drafts_last_sentence_with_the_sign_in_another_form_vetoes_the_add_sample(string door, string jev) => VR13_the_drafts_last_sentence_with_the_sign_in_another_form_vetoes_the_add(door, jev);

        /// <summary>VR13 in kept words (version 4 j): typed before permission, read again at Add more words with their
        /// sentence, whose sign is in a form the check does not read.</summary>
        [Theory]
        [MemberData(nameof(KeptWordsInOtherForms))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR13_kept_words_with_the_sign_in_another_form_draft_no_kept_add(string door, string jev, string first)
        {
            using var f = await ProAsync("VR13j", jev, consent: false);
            var v = await ComposeOnDoor(f.Engine, door, first);
            await f.ConsentAsync();
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "and hide the Margin measure");
            Assert.True(KeptAdds(v).Length == 0, first + " => kept " + string.Join(", ", KeptAdds(v)));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR13_kept_words_with_the_sign_in_another_form_draft_no_kept_add_SampleRows() => KeptWordsInOtherForms().Take(1);

        [Theory]
        [MemberData(nameof(VR13_kept_words_with_the_sign_in_another_form_draft_no_kept_add_SampleRows))]
        public Task VR13_kept_words_with_the_sign_in_another_form_draft_no_kept_add_sample(string door, string jev, string first) => VR13_kept_words_with_the_sign_in_another_form_draft_no_kept_add(door, jev, first);

        public static IEnumerable<object[]> KeptWordsInOtherForms() => Rows(
            "the old measures should probably go, and set up the same for the old roles",
            "the old roles won't be needed after the move, so set up the same for the old measures");

        // ---- VR14: a sign or a later take-away past the twelve clauses one ask reads --------------------------------

        private const string OneSentencePastTheLimit = "Put Total Sales, Total Cost, Average Price, Margin, Units, Returns, Budget, Profit, "
            + "Discount, Tax and Freight in one group, add the Rebate measure to that group too, and then delete the whole group";

        private const string LaterSentencePastTheLimit = "Add the Rebate measure to the review list. Also hide Total Sales, Total Cost, "
            + "Average Price, Margin, Units, Returns, Budget, Profit, Discount, Tax and Freight. Then delete every measure on the review list.";

        /// <summary>VR14 (the harmful kind). Version 4 f reads the WHOLE sentence and k every later sentence of the same typing,
        /// but the Plan hands the rule only the clauses one ask reads (DescribedSteps.cs:638 and :642: the twelve read, never
        /// the ones past them), so a sign in the thirteenth clause of one sentence is never seen, and the add in the twelfth,
        /// "add the Rebate measure to that group too", keeps a step that adds (Relate two tables with Jev saying no, from the
        /// word match's slip that reads "Rebate" as "relate"; every add with Jev sure), while the group it joins is deleted.</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR14_a_sign_past_the_twelve_clauses_one_ask_reads_still_vetoes_the_add_in_its_sentence(string door, string jev)
        {
            using var f = await ProAsync("VR14", jev);
            var v = await ComposeOnDoor(f.Engine, door, OneSentencePastTheLimit);
            Assert.True(KeptAdds(v).Length == 0, "kept " + string.Join(", ", KeptAdds(v)));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR14_a_sign_past_the_twelve_clauses_one_ask_reads_still_vetoes_the_add_in_its_sentence_SampleRows() => DoorsAndJev().Take(1);

        [Theory]
        [MemberData(nameof(VR14_a_sign_past_the_twelve_clauses_one_ask_reads_still_vetoes_the_add_in_its_sentence_SampleRows))]
        public Task VR14_a_sign_past_the_twelve_clauses_one_ask_reads_still_vetoes_the_add_in_its_sentence_sample(string door, string jev) => VR14_a_sign_past_the_twelve_clauses_one_ask_reads_still_vetoes_the_add_in_its_sentence(door, jev);

        /// <summary>VR14 over Add more words: new words of thirteen clauses in one sentence; the thirteenth deletes the group.</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR14_new_words_past_the_limit_still_veto_the_add_in_their_sentence(string door, string jev)
        {
            using var f = await ProAsync("VR14m", jev);
            var v = await ComposeOnDoor(f.Engine, door, "Hide the Margin measure.");
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "Then put Total Sales, Total Cost, Average Price, Units, Returns, "
                + "Budget, Profit, Discount, Tax, Freight and Rebate in one group, add the Net Sales measure to that group as well, and delete the whole group");
            Assert.True(KeptAdds(v).Length == 0, "kept " + string.Join(", ", KeptAdds(v)));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR14_new_words_past_the_limit_still_veto_the_add_in_their_sentence_SampleRows() => DoorsAndJev().Take(1);

        [Theory]
        [MemberData(nameof(VR14_new_words_past_the_limit_still_veto_the_add_in_their_sentence_SampleRows))]
        public Task VR14_new_words_past_the_limit_still_veto_the_add_in_their_sentence_sample(string door, string jev) => VR14_new_words_past_the_limit_still_veto_the_add_in_their_sentence(door, jev);

        /// <summary>VR14 with k: the later sentence that deletes every measure on the review list is the thirteenth clause.</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR14_a_later_take_away_past_the_limit_still_vetoes_the_add(string door, string jev)
        {
            using var f = await ProAsync("VR14k", jev);
            var v = await ComposeOnDoor(f.Engine, door, LaterSentencePastTheLimit);
            Assert.True(KeptAdds(v).Length == 0, "kept " + string.Join(", ", KeptAdds(v)));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR14_a_later_take_away_past_the_limit_still_vetoes_the_add_SampleRows() => DoorsAndJev().Take(1);

        [Theory]
        [MemberData(nameof(VR14_a_later_take_away_past_the_limit_still_vetoes_the_add_SampleRows))]
        public Task VR14_a_later_take_away_past_the_limit_still_vetoes_the_add_sample(string door, string jev) => VR14_a_later_take_away_past_the_limit_still_vetoes_the_add(door, jev);

        /// <summary>VR14 with kept words (version 4 j and k): the same words typed before permission; at Add more words the
        /// add is read while the later sentence that takes it away waits past the room one ask has (DraftDoors.cs:401-410).</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR14_a_later_take_away_that_waits_past_the_room_still_vetoes_the_kept_add(string door, string jev)
        {
            using var f = await ProAsync("VR14j", jev, consent: false);
            var v = await ComposeOnDoor(f.Engine, door, LaterSentencePastTheLimit);
            await f.ConsentAsync();
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "and hide the Margin measure");
            Assert.True(KeptAdds(v).Length == 0, "kept " + string.Join(", ", KeptAdds(v)));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR14_a_later_take_away_that_waits_past_the_room_still_vetoes_the_kept_add_SampleRows() => DoorsAndJev().Take(1);

        [Theory]
        [MemberData(nameof(VR14_a_later_take_away_that_waits_past_the_room_still_vetoes_the_kept_add_SampleRows))]
        public Task VR14_a_later_take_away_that_waits_past_the_room_still_vetoes_the_kept_add_sample(string door, string jev) => VR14_a_later_take_away_that_waits_past_the_room_still_vetoes_the_kept_add(door, jev);

        // ---- VR15: the draft's open sentence for Add more words ends at "e.g." and "i.e." -----------------------------

        /// <summary>VR15 (the harmful kind). Version 4 f for Add more words reads "the new words plus the draft's last
        /// sentence", and "e.g." and "i.e." never end a sentence (the round 5 Q1 the lane answers in AddingRule.Sentences).
        /// DescribedSteps.OpenSentence (DescribedSteps.cs:320-328), which finds that open sentence for Add more words, keeps its
        /// own list of sentence ends, where a full stop before a space always ends one: the open sentence of "Delete the old
        /// roles, e.g. the ones for sales" is "the ones for sales", so "and add the Managers role to them too" keeps Add a
        /// role, and the same with "i.e.".</summary>
        [Theory]
        [MemberData(nameof(AbbreviationsInTheOpenSentence))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR15_add_more_words_after_e_g_or_i_e_keep_the_drafts_take_away_sentence(string door, string jev, string first, string more)
        {
            using var f = await ProAsync("VR15", jev);
            var v = await ComposeOnDoor(f.Engine, door, first);
            Assert.Empty(KeptAdds(v));
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, more);
            Assert.True(KeptAdds(v).Length == 0, first + " + " + more + " => kept " + string.Join(", ", KeptAdds(v)));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR15_add_more_words_after_e_g_or_i_e_keep_the_drafts_take_away_sentence_SampleRows() => AbbreviationsInTheOpenSentence().Take(1);

        [Theory]
        [MemberData(nameof(VR15_add_more_words_after_e_g_or_i_e_keep_the_drafts_take_away_sentence_SampleRows))]
        public Task VR15_add_more_words_after_e_g_or_i_e_keep_the_drafts_take_away_sentence_sample(string door, string jev, string first, string more) => VR15_add_more_words_after_e_g_or_i_e_keep_the_drafts_take_away_sentence(door, jev, first, more);

        public static IEnumerable<object[]> AbbreviationsInTheOpenSentence() =>
            new[]
            {
                ("Delete the old roles, e.g. the ones for sales", "and add the Managers role to them too"),
                ("Remove the old measures, i.e. Total Sales and Total Cost", "and add the Rebate measure to that list as well"),
            }.SelectMany(p => DoorsAndJev().Select(r => new object[] { r[0], r[1], p.Item1, p.Item2 }));

        /// <summary>VR15 on its own: the open sentence of words whose last sentence holds "e.g." is that whole sentence.</summary>
        [Fact]
        public void VR15_the_open_sentence_does_not_end_at_e_g()
        {
            Assert.Equal("Delete the old roles, e.g. the ones for sales", DescribedSteps.OpenSentence("Delete the old roles, e.g. the ones for sales"));
            Assert.Equal("Remove the old measures, i.e. Total Sales and Total Cost", DescribedSteps.OpenSentence("Remove the old measures, i.e. Total Sales and Total Cost"));
        }

        // ---- VR16: k for Add more words reads only the first new sentence ------------------------------------------

        /// <summary>VR16 (the harmful kind). Version 4 k: "A take-away in a LATER sentence of the same typing (or the draft's
        /// last sentence for Add more words) also vetoes an add that names the same object kind". AddingRule.Read applies the
        /// draft's last sentence to the FIRST sentence of the new words only (AddingRule.cs:113 and :138), so "Delete the old
        /// measures." then "Hide the Margin measure. Add the Rebate measure to that list too." keeps a step that adds: the
        /// Rebate measure goes on the list of measures to delete.</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR16_the_drafts_last_sentence_vetoes_a_same_kind_add_in_any_new_sentence(string door, string jev)
        {
            using var f = await ProAsync("VR16", jev);
            var v = await ComposeOnDoor(f.Engine, door, "Delete the old measures.");
            Assert.Empty(KeptAdds(v));
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "Hide the Margin measure. Add the Rebate measure to that list too.");
            Assert.True(KeptAdds(v).Length == 0, "kept " + string.Join(", ", KeptAdds(v)));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR16_the_drafts_last_sentence_vetoes_a_same_kind_add_in_any_new_sentence_SampleRows() => DoorsAndJev().Take(1);

        [Theory]
        [MemberData(nameof(VR16_the_drafts_last_sentence_vetoes_a_same_kind_add_in_any_new_sentence_SampleRows))]
        public Task VR16_the_drafts_last_sentence_vetoes_a_same_kind_add_in_any_new_sentence_sample(string door, string jev) => VR16_the_drafts_last_sentence_vetoes_a_same_kind_add_in_any_new_sentence(door, jev);
    }
}
