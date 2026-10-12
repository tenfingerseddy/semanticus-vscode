// verify: the verifier's failing tests for m11/sol-fixes-1 round 4 (7c840a24). Each test names ONE defect and is LEFT
// FAILING on purpose: it passes once the defect is fixed. The Jev oracle is the scripted, honest oracle the earlier rounds
// use: it answers the exact request the engine built, through the real recorded oracle and answer reader, and says no to
// every step, so every clause takes the Plan's fallback, where THE ADDING RULE version 2 (AddingRule.cs) decides. Real
// Jev gives the same kept adds for every phrasing here, on both doors (verifier round 4 evidence, probe-real.jsonl).

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
    public sealed class VerifySolFixes1Round4DefectTests
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
                return new RecordedDecisionOracle(sb.ToString(), "jev-vsf4r4").AskAsync(request, ct);
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

        public static IEnumerable<object[]> TakeAways() => new[]
        {
            // A clause the cut leaves with only a thing's name ("Total Sales", "the new role") is read as an ask to add
            // (AddingRule.ActionAt: a first clause led by new, total, count, sum or average; AddingRule.Thing: a first clause
            // that says "new"). Its action is in the clause after it, which the rule reads only for the reason.
            "the new role and the new measure should be deleted",
            "Total Sales and Average Price should be removed",
            "Total Sales measure, delete it",
            "The new Margin measure. Delete it.",
            // "was added" said of the thing is read as an ask to add it (AddingRule.Predicate): it says what happened.
            "the measure that was added yesterday, remove it",
            "the columns that were added by the import, drop them",
            // "cleanup" is "clean up" (a listed sign, "and their forms"); the sign pattern only matches the two words.
            "create a cleanup step for the old roles",
        }.SelectMany(w => new[] { new object[] { "studio", w }, new object[] { "agent", w } });

        /// <summary>VR8 (medium, the harmful kind: the opposite change is drafted as a KEPT step; the same kind as round 3's
        /// VR5, which rule version 2 was adopted to close). Rule version 2: a clause may draft an adding step only when "the
        /// clause asks to make something: an adding verb is its action", and "every doubt resolves to no add". A clause with
        /// no verb at all, or one that only says a thing was added, does not ask to make anything, yet each of these drafts
        /// Add a measure, Add a role or Import a table as a kept Plan step, on both doors, with Jev saying no (the Plan's
        /// fallback) and with real Jev on.</summary>
        [Theory]
        [MemberData(nameof(TakeAways))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR8_words_that_ask_to_take_something_away_draft_no_add(string door, string words)
        {
            using var f = await ProAsync(File("VR8"));
            var v = await ComposeOnDoor(f.Engine, door, words);
            Assert.Empty(Adds(v));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR8_words_that_ask_to_take_something_away_draft_no_add_SampleRows() => TakeAways().Take(1);

        [Theory]
        [MemberData(nameof(VR8_words_that_ask_to_take_something_away_draft_no_add_SampleRows))]
        public Task VR8_words_that_ask_to_take_something_away_draft_no_add_sample(string door, string words) => VR8_words_that_ask_to_take_something_away_draft_no_add(door, words);

        /// <summary>VR8b (medium, the harmful kind). Add more words reads its new words on their own, so a clause that only
        /// names a thing and continues the draft's own take-away ("delete the old measures", then "and the new role too")
        /// is a first clause with no action, read as an ask to add. The rule: "a clause with no action of its own inherits
        /// the action of the clause before it", which here is "delete the old measures". Kept Add a role, both doors.</summary>
        [Theory]
        [MemberData(nameof(Doors))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR8b_add_more_words_that_carry_on_a_take_away_draft_no_add(string door)
        {
            using var f = await ProAsync(File("VR8b"));
            var v = await ComposeOnDoor(f.Engine, door, "delete the old measures");
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "and the new role too");
            Assert.Empty(Adds(v));
            v = await ComposeOnDoor(f.Engine, door, "remove the Total Sales measure");
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "and Total Cost");
            Assert.Empty(Adds(v));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR8b_add_more_words_that_carry_on_a_take_away_draft_no_add_SampleRows() => Doors().Take(1);

        [Theory]
        [MemberData(nameof(VR8b_add_more_words_that_carry_on_a_take_away_draft_no_add_SampleRows))]
        public Task VR8b_add_more_words_that_carry_on_a_take_away_draft_no_add_sample(string door) => VR8b_add_more_words_that_carry_on_a_take_away_draft_no_add(door);

        /// <summary>VR8c (low, an add nobody asked for; not a take-away). The same reading: "count of orders and count of
        /// returns should be hidden" asks to hide two things, and the draft keeps Add a measure for "count of orders". The
        /// lane's own AddingRuleTests expects "count of orders should be hidden" to draft no add; cut in two, it does.</summary>
        [Theory]
        [MemberData(nameof(Doors))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task VR8c_words_that_ask_to_hide_two_things_draft_no_add(string door)
        {
            using var f = await ProAsync(File("VR8c"));
            var v = await ComposeOnDoor(f.Engine, door, "count of orders and count of returns should be hidden");
            Assert.Empty(Adds(v));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> VR8c_words_that_ask_to_hide_two_things_draft_no_add_SampleRows() => Doors().Take(1);

        [Theory]
        [MemberData(nameof(VR8c_words_that_ask_to_hide_two_things_draft_no_add_SampleRows))]
        public Task VR8c_words_that_ask_to_hide_two_things_draft_no_add_sample(string door) => VR8c_words_that_ask_to_hide_two_things_draft_no_add(door);

        /// <summary>The root cause on its own, with no engine: rule version 2 reads each of VR8's clauses as NOT asking to
        /// add. It reads the first clause of each as asking to add.</summary>
        [Theory]
        [InlineData("the new role and the new measure should be deleted")]
        [InlineData("Total Sales and Average Price should be removed")]
        [InlineData("Total Sales measure, delete it")]
        [InlineData("The new Margin measure. Delete it.")]
        [InlineData("the measure that was added yesterday, remove it")]
        [InlineData("the columns that were added by the import, drop them")]
        [InlineData("create a cleanup step for the old roles")]
        public void VR8_rule_reads_no_clause_of_these_words_as_asking_to_add(string words)
        {
            var clauses = DescribedSteps.ClausesOf(words, out _);
            Assert.All(AddingRule.Read(clauses), r => Assert.False(r.AsksToAdd));
        }
    }
}
