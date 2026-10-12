// verify: the verifier's failing tests for m11/sol-fixes-1 round 5 (6b0efad4). Each test names ONE defect and is LEFT
// FAILING on purpose: it passes once the defect is fixed. The phrasings are from the verifier's labels, committed before any
// run (Semanticus.Tests/fixtures/verify-sol-fixes-1-r5-adding-attacks.json, commit 0ca8726b). The Jev oracle is the
// scripted, honest oracle the earlier rounds use: it answers the exact request the engine built, through the real recorded
// oracle and answer reader, and says no to every step, so every clause takes the Plan's fallback, where THE ADDING RULE
// version 3 (AddingRule.cs) decides. A Jev sure of every add, and real Jev, give the same kept adds for these phrasings on
// both doors (verifier round 5 evidence, probe-scripted.jsonl and probe-real.jsonl).

using System;
using System.Collections.Generic;
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
    public sealed class VerifySolFixes1Round5DefectTests
    {
        private sealed class JevSaysNo : IDecisionOracle
        {
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
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(':').Append(q.Kind == DecisionQuestionKind.Noul
                        ? "{\"type\":\"noul\",\"noul\":0.02}"
                        : q.Name.EndsWith(":wanted", StringComparison.Ordinal) ? "{\"type\":\"choice\",\"choice\":\"yes\",\"confidence\":0.95}"   // join part 2: the last question fails closed, so a script answers it yes
                        : "{\"type\":\"choice\",\"choice\":\"none\",\"confidence\":0.95}");
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-vsf5r5").AskAsync(request, ct);
            }
        }

        private static async Task<DecisionFixture> ProAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync(file);
            await f.SaveAsAsync(file);
            await f.ConsentAsync();
            f.Engine.UseDecisionsForTest(new DecisionService(new JevSaysNo(), new DecisionBudget()));
            return f;
        }

        private static string File(string name) => name + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim";

        private static Task<DraftView> ComposeOnDoor(LocalEngine engine, string door, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).composeTask(words: words)
                             : McpTools.ComposeTask(engine, words: words);

        private static Task<DraftView> AddMoreWordsOnDoor(LocalEngine engine, string door, string draftId, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).editDraft(draftId, "refine", text: words)
                             : McpTools.EditDraft(engine, draftId, "refine", text: words);

        /// <summary>The Plan's steps that add (their op creates: Add a measure, a role, a column, a calculation group,
        /// Relate two tables, Import a table), kept or dropped, by step id ("role", "role-2").</summary>
        private static string[] Adds(DraftView v) =>
            v.Steps.Concat(v.After ?? Array.Empty<DraftStepView>())
                .Where(s => new[] { "add-measure", "relate", "import", "add-column", "calc-group", "role" }
                    .Any(a => s.Id == a || s.Id.StartsWith(a + "-", StringComparison.Ordinal) && s.Id.Substring(a.Length + 1).All(char.IsDigit)))
                .Select(s => s.Title + (s.Kept ? " (kept)" : " (dropped)")).ToArray();

        public static IEnumerable<object[]> Doors() => new[] { new object[] { "studio" }, new object[] { "agent" } };

        private static IEnumerable<object[]> OnBothDoors(params string[] words) =>
            words.SelectMany(w => new[] { new object[] { "studio", w }, new object[] { "agent", w } });

        /// <summary>A take-away sign the rule lists, in a form the code does not read. The sign pattern is matched on one
        /// clause's words at a time and on words side by side (AddingRule.cs:77 and :293): "clean ... up" with its object
        /// between, "throw ... away" and "take ... out" with more than four words between, a sign the cut splits at "and",
        /// "should all go", "aren't needed", "no longer really need".</summary>
        public static IEnumerable<object[]> ListedSigns() => OnBothDoors(
            "create a job that cleans the old roles up",
            "set up a task to clean all the old measures out",
            "build a new step that throws every one of the old roles away",
            "add a step that takes all of the old roles out of the model",
            "create a task to take Total Sales and Total Cost out",
            "set up a step to throw the old roles and the old measures away",
            "set up a job for the old roles, they should all go",
            // "add the Managers role to the roles that aren't needed" is RETIRED from this list (round 7, version 5 offers its
            // add unticked): VerifySolFixes1Round7RestatedTests.VR9r_... restates it as "no KEPT add", on both doors.
            "add Total Sales to the measures we no longer really need");

        /// <summary>VR9 (the harmful kind: the opposite change is drafted as a KEPT step). Rule version 3, b: "No take-away
        /// sign appears ANYWHERE in the clause ... A take-away sign is a veto, wherever it sits", the veto covers "the WHOLE
        /// SENTENCE", and the signs include "clean up, clean out, take out, throw away, not needed, no longer need, old ...
        /// should go ... and their forms". Each of these sentences asks to take roles or measures away, an adding verb leads
        /// its first clause, and the draft keeps Add a role or Add a measure, on both doors, with Jev saying no (the Plan's
        /// fallback), with a Jev sure of every add and with real Jev on.</summary>
        [Theory]
        [MemberData(nameof(ListedSigns))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR9_a_listed_take_away_sign_in_a_form_the_rule_misses_drafts_no_add(string door, string words)
        {
            using var f = await ProAsync(File("VR9"));
            var v = await ComposeOnDoor(f.Engine, door, words);
            Assert.True(Adds(v).Length == 0, words + " => " + string.Join(", ", Adds(v)));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR9_a_listed_take_away_sign_in_a_form_the_rule_misses_drafts_no_add_SampleRows() => ListedSigns().Take(1);

        [Theory]
        [MemberData(nameof(VR9_a_listed_take_away_sign_in_a_form_the_rule_misses_drafts_no_add_SampleRows))]
        public Task VR9_a_listed_take_away_sign_in_a_form_the_rule_misses_drafts_no_add_sample(string door, string words) => VR9_a_listed_take_away_sign_in_a_form_the_rule_misses_drafts_no_add(door, words);

        /// <summary>VR10 (the harmful kind). "we need one less role" and "I'd like two fewer measures" ask for fewer things,
        /// and neither has an adding verb (rule version 3, a: "Only an explicit adding VERB in the clause can make it an
        /// add"). The wanting form the lane keeps ("we need a new ...") reads any count after the want as a new thing
        /// (AddingRule.cs:167, the Indefinite words at :244 include "one" and "two"), so each keeps Add a role or Add a
        /// measure, on both doors.</summary>
        [Theory]
        [MemberData(nameof(WantsOfFewer))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR10_a_want_of_fewer_things_drafts_no_add(string door, string words)
        {
            using var f = await ProAsync(File("VR10"));
            var v = await ComposeOnDoor(f.Engine, door, words);
            Assert.True(Adds(v).Length == 0, words + " => " + string.Join(", ", Adds(v)));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR10_a_want_of_fewer_things_drafts_no_add_SampleRows() => WantsOfFewer().Take(1);

        [Theory]
        [MemberData(nameof(VR10_a_want_of_fewer_things_drafts_no_add_SampleRows))]
        public Task VR10_a_want_of_fewer_things_drafts_no_add_sample(string door, string words) => VR10_a_want_of_fewer_things_drafts_no_add(door, words);

        public static IEnumerable<object[]> WantsOfFewer() => OnBothDoors("we need one less role", "I'd like two fewer measures");

        /// <summary>VR11 (the harmful kind). Words the draft keeps because no answered ask read them (typed before Jev had
        /// permission, DescribedSteps.cs:616; or past the twelve clauses one ask reads, :610) are read again at the next Add
        /// more words one per line (DraftDoors.cs:406), so each kept clause is a sentence of its own and the sentence that
        /// asked to take things away no longer vetoes it; RuleOf (DescribedSteps.cs:257) carries the draft's open sentence
        /// onto the NEW words only. Rule version 3, b and e: the veto covers the whole sentence, and Jev's picks go through
        /// the rule "with the sentence as context, never a lone clause". Here "set up the same for the old measures" and
        /// "add the Net Sales measure to that list too" both keep Add a measure, on both doors.</summary>
        [Theory]
        [MemberData(nameof(Doors))]
        public async Task VR11_kept_words_typed_before_permission_keep_the_sentence_that_takes_away(string door)
        {
            using var f = await ProAsync(File("VR11"));
            // Typed before Jev had permission: every clause is kept, then read again once permission is given.
            await f.Engine.WithdrawConsentAsync("human");
            var v = await ComposeOnDoor(f.Engine, door, "the old roles have to go, and set up the same for the old measures");
            await f.ConsentAsync();
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "and hide the Margin measure");
            Assert.True(Adds(v).Length == 0, "kept before permission => " + string.Join(", ", Adds(v)));
        }

        // VR11 past the limit and VR11b are RETIRED here (round 7, THE ADDING RULE version 5): each asserted that no step that
        // adds is drafted at all, and version 5 offers these adds UNTICKED with the person's words and one true line on why.
        // The harm is a KEPT add, so their letter is now "no KEPT add": VerifySolFixes1Round7RestatedTests.VR11r_... and
        // VR11br_... restate them word for word, on both doors.

        /// <summary>VR12 (low, an add nobody asked for; not a take-away). "add no new roles" and "create zero new measures" ask
        /// for no add, and each keeps Add a role or Add a measure, on both doors: the adding verb leads the clause and nothing
        /// reads what it says of its object.</summary>
        [Theory]
        [MemberData(nameof(Negations))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR12_words_that_ask_for_no_new_thing_draft_no_add(string door, string words)
        {
            using var f = await ProAsync(File("VR12"));
            var v = await ComposeOnDoor(f.Engine, door, words);
            Assert.True(Adds(v).Length == 0, words + " => " + string.Join(", ", Adds(v)));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR12_words_that_ask_for_no_new_thing_draft_no_add_SampleRows() => Negations().Take(1);

        [Theory]
        [MemberData(nameof(VR12_words_that_ask_for_no_new_thing_draft_no_add_SampleRows))]
        public Task VR12_words_that_ask_for_no_new_thing_draft_no_add_sample(string door, string words) => VR12_words_that_ask_for_no_new_thing_draft_no_add(door, words);

        public static IEnumerable<object[]> Negations() => OnBothDoors("add no new roles", "create zero new measures");

        /// <summary>VR9 and VR10 on the rule alone, with no engine: rule version 3 reads a clause of each of these words as
        /// asking to add.</summary>
        [Theory]
        [InlineData("create a job that cleans the old roles up")]
        [InlineData("set up a task to clean all the old measures out")]
        [InlineData("build a new step that throws every one of the old roles away")]
        [InlineData("add a step that takes all of the old roles out of the model")]
        [InlineData("create a task to take Total Sales and Total Cost out")]
        [InlineData("set up a step to throw the old roles and the old measures away")]
        [InlineData("set up a job for the old roles, they should all go")]
        [InlineData("add the Managers role to the roles that aren't needed")]
        [InlineData("add Total Sales to the measures we no longer really need")]
        [InlineData("we need one less role")]
        [InlineData("I'd like two fewer measures")]
        public void VR9_rule_reads_no_clause_of_these_words_as_asking_to_add(string words)
        {
            var clauses = DescribedSteps.ClausesOf(words, out _, out var sentences);
            Assert.All(AddingRule.Read(clauses, sentences), r => Assert.False(r.AsksToAdd));
        }
    }
}
