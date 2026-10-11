// m11/sol-fixes-1 round 6: THE ADDING RULE version 4 (the coordinator, 29 Sep 05:10) on both doors, with Jev saying no and Jev
// sure of every add, and the verifier's round 5 low items L2 (a note's words are true of its clause) and L3 (the menu the
// driver shows). The rule on its own is AddingRuleTests; the verifier's own failing tests are
// VerifySolFixes1Round5DefectTests.

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
    public sealed class SolFixes1Round6Tests
    {
        /// <summary>Answers compose.draft with one confidence for every include of a template that adds (<paramref name="adds"/>)
        /// and one for every other (<paramref name="others"/>); 0.02 is a sure no.</summary>
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
                return new RecordedDecisionOracle(sb.ToString(), "jev-sf1r6").AskAsync(request, ct);
            }
        }

        private static readonly Jev SaysNo = new Jev(0.02, 0.02);
        private static readonly Jev SureOfEveryAdd = new Jev(0.99, 0.02);

        private static async Task<DecisionFixture> ProAsync(string name, IDecisionOracle jev, bool consent = true)
        {
            var file = name + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim";
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync(file);
            await f.SaveAsAsync(file);
            if (consent) await f.ConsentAsync();
            f.Engine.UseDecisionsForTest(new DecisionService(jev, new DecisionBudget()));
            return f;
        }

        private static Task<DraftView> ComposeOnDoor(LocalEngine engine, string door, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).composeTask(words: words) : McpTools.ComposeTask(engine, words: words);

        private static Task<DraftView> AddMoreWordsOnDoor(LocalEngine engine, string door, string draftId, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).editDraft(draftId, "refine", text: words) : McpTools.EditDraft(engine, draftId, "refine", text: words);

        private static readonly string[] AddIds = { "add-measure", "relate", "import", "add-column", "calc-group", "role" };

        private static string[] Adds(DraftView v) =>
            v.Steps.Concat(v.After ?? Array.Empty<DraftStepView>())
                .Where(s => AddIds.Any(a => s.Id == a || s.Id.StartsWith(a + "-", StringComparison.Ordinal) && s.Id.Substring(a.Length + 1).All(char.IsDigit)))
                .Select(s => s.Title + (s.Kept ? " (kept)" : " (dropped)")).ToArray();

        private static string[] KeptAdds(DraftView v) => Adds(v).Where(a => a.EndsWith("(kept)", StringComparison.Ordinal)).ToArray();

        public static IEnumerable<object[]> DoorsAndJev() => new[]
        {
            new object[] { "studio", "no" }, new object[] { "agent", "no" }, new object[] { "studio", "sure" }, new object[] { "agent", "sure" },
        };

        /// <summary>The words each version 4 letter reads as asking for no add, and the verifier's round 5 Q1, Q2 and Q4.</summary>
        private static readonly string[] NoAdd =
        {
            // f: a take-away sign anywhere in the sentence, at any distance (VR9 and VR10's family).
            "create a job that cleans the old roles up", "set up a task to clean all the old measures out",
            "build a new step that throws every one of the old roles away", "add a step that takes all of the old roles out of the model",
            "create a task to take Total Sales and Total Cost out", "set up a step to throw the old roles and the old measures away",
            "set up a job for the old roles, they should all go", "add the Managers role to the roles that aren't needed",
            "add Total Sales to the measures we no longer really need", "add a measure for margin and, after a long while and some thought, delete the old measures",
            // Q1: e.g. and i.e. do not end a sentence.
            "Create a step for the old roles, e.g. deleting them", "add a measure for margin, e.g. a ratio, and the old role can go",
            "add a role for managers, i.e. the finance people; the old role has to go",
            // Q2: a take-away in a later sentence of the same words.
            "Add a measure for margin. Then delete the old measures.", "Add a role for managers. The old ones can go.",
            "Create a measure for margin; remove the old measures", "Add a column for the year? Delete the old columns.",
            // g, h, i, l, and Q4.
            "make a list of measures", "add a note for each role", "set up a reminder for the roles", "create a step for the new measures",
            "we need one less role", "I'd like two fewer measures", "add some more of the measures",
            "add no new roles", "create zero new measures", "add none of the roles", "don't add a role",
            "give the old role a new name", "give the measure a new description", "give the column a new display folder",
        };

        /// <summary>v4 f to l on both doors: none of these words drafts a KEPT add, with Jev saying no (the Plan's word match
        /// decides) and with Jev SURE of every add (its picks go through the same rule). Version 5 offers an add unticked
        /// (dropped) for words that hold an adding verb, so this counts kept adds only: rewritten, rule line version 5 item 2.</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        public async Task No_words_of_version_four_draft_an_add(string door, string jev)
        {
            using var f = await ProAsync("R6No" + door + jev, jev == "sure" ? SureOfEveryAdd : SaysNo);
            var wrong = new List<string>();
            foreach (var words in NoAdd)
            {
                var adds = KeptAdds(await ComposeOnDoor(f.Engine, door, words));
                if (adds.Length > 0) wrong.Add(words + " => " + string.Join(", ", adds));
            }
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        /// <summary>The adds version 4 must keep (its controls, and the two lead-ins the verifier's fixture keeps, p4 and p9):
        /// each still drafts its add, kept, on both doors.</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        public async Task Version_four_keeps_the_adds_it_has_no_reason_to_lose(string door, string jev)
        {
            using var f = await ProAsync("R6Keep" + door + jev, jev == "sure" ? SureOfEveryAdd : SaysNo);
            var lost = new List<string>();
            foreach (var (words, add) in new[]
            {
                ("I would like you to add a measure for margin", "Add a measure"), ("I think we need a new measure for margin", "Add a measure"),
                ("please go ahead and add a measure for margin", "Add a measure"), ("add a measure for margin with no filter", "Add a measure"),
                ("we need a role for managers", "Add a role"),   // v6 D (moved here from the no-add list): a bare want of a new thing asks
                ("Add a role for managers. Then hide the Margin measure.", "Add a role"),
                ("add a Units measure, e.g. for the last year", "Add a measure"),
                ("give Total Sales a new home: create a role for managers", "Add a role"),
            })
            {
                var kept = KeptAdds(await ComposeOnDoor(f.Engine, door, words));
                if (!kept.Contains(add + " (kept)")) lost.Add(words + " => " + string.Join(", ", kept));
            }
            Assert.True(lost.Count == 0, string.Join("\n", lost));
            // v6 B (moved out of the list above, relabelled from kept): "Remove" anywhere in the text demotes the add.
            var removed = await ComposeOnDoor(f.Engine, door, "Remove the old role; add a measure for margin");
            Assert.Empty(KeptAdds(removed));
            Assert.Contains("Add a measure (dropped)", Adds(removed));
            // The replace journey is UNTICKED under version 5 (K4, the coordinator's call of 29 Sep 19:05): the add is offered, not ticked.
            foreach (var words in new[] { "Add a role for managers. Then delete the old measures.", "Add a measure for margin. Then delete the old measures." })
            {
                var v = await ComposeOnDoor(f.Engine, door, words);
                Assert.Empty(KeptAdds(v));
                Assert.Contains(v.Notes, n => n.StartsWith("Not ticked", StringComparison.Ordinal));
            }
        }

        /// <summary>j: words kept before permission are read again with the sentence they were typed in, with Jev saying no
        /// and with Jev sure of every add (VR11), and an add in kept words with nothing that takes away in their sentence
        /// stays.</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        public async Task Kept_words_keep_their_sentences_when_they_are_read_again(string door, string jev)
        {
            using var f = await ProAsync("R6Kept" + door + jev, jev == "sure" ? SureOfEveryAdd : SaysNo);
            await f.Engine.WithdrawConsentAsync("human");
            var v = await ComposeOnDoor(f.Engine, door, "the old roles have to go, and set up the same for the old measures");
            await f.ConsentAsync();
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "and hide the Margin measure");
            Assert.True(KeptAdds(v).Length == 0, "kept before permission => " + string.Join(", ", Adds(v)));

            await f.Engine.WithdrawConsentAsync("human");
            v = await ComposeOnDoor(f.Engine, door, "add a measure for margin and the old role should be removed");
            await f.ConsentAsync();
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "and hide the Margin measure");
            Assert.True(KeptAdds(v).Length == 0, "vetoed before permission => " + string.Join(", ", Adds(v)));

            await f.Engine.WithdrawConsentAsync("human");
            v = await ComposeOnDoor(f.Engine, door, "add a role for managers");
            await f.ConsentAsync();
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "and hide the Margin measure");
            Assert.Contains("Add a role (kept)", Adds(v));
        }

        /// <summary>Add more words after a closed take-away sentence (version 5; k is retired): the rule reads the new words as
        /// typed. A NEW add of a named kind is kept, whatever the draft's last sentence took away; an add that puts the thing
        /// on that list ("to that list too") is unticked. Rewritten from "the draft's last closed sentence vetoes an add of the
        /// same kind", rule line version 5 item 4 (and K3 for the second).</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        public async Task Add_more_words_reads_the_new_words_as_typed(string door, string jev)
        {
            using var f = await ProAsync("R6More" + door + jev, jev == "sure" ? SureOfEveryAdd : SaysNo);
            // v6 B (relabelled from kept): a sign of the frozen list in ANY typing demotes a kept add, so an add typed after
            // "delete all the old measures." is offered unticked whatever it says; the add that puts the thing on the list is too.
            var v = await ComposeOnDoor(f.Engine, door, "delete all the old measures.");
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "Add a measure for margin");
            Assert.Empty(KeptAdds(v));
            Assert.Contains("Add a measure (dropped)", Adds(v));
            v = await ComposeOnDoor(f.Engine, door, "delete all the old measures.");
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "Add the Rebate measure to that list too");
            Assert.Empty(KeptAdds(v));
            Assert.Contains("Add a measure (dropped)", Adds(v));
            v = await ComposeOnDoor(f.Engine, door, "delete all the old measures.");
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "Add a role for managers");
            Assert.Empty(KeptAdds(v));
            Assert.Contains("Add a role (dropped)", Adds(v));
        }

        /// <summary>L2: the note kept with a clause says what is true of it. "Total Cost out" of a take-away is not called
        /// words that "do not ask to add anything", "e.g." is never a clause of its own, and no note names "e" or "g".</summary>
        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        public async Task A_note_says_what_is_true_of_its_clause(string door)
        {
            using var f = await ProAsync("R6Note" + door, SaysNo);
            var v = await ComposeOnDoor(f.Engine, door, "create a task to take Total Sales and Total Cost out");
            Assert.Empty(KeptAdds(v));
            Assert.DoesNotContain(v.Notes, n => n.Contains(AddingRule.NoAddReason, StringComparison.Ordinal));
            Assert.Contains(v.Notes, n => n.Contains("\"Total Cost out\"", StringComparison.Ordinal) && n.Contains(AddingRule.TakeAwayReason, StringComparison.Ordinal));
            v = await ComposeOnDoor(f.Engine, door, "Create a step for the old roles, e.g. deleting them");
            Assert.Empty(KeptAdds(v));
            Assert.DoesNotContain(v.Notes, n => n.Contains("\"g\"", StringComparison.Ordinal) || n.Contains("\"e\"", StringComparison.Ordinal) || n.Contains("\"e.g.\"", StringComparison.Ordinal));
            Assert.Contains(v.Notes, n => n.Contains("\"e.g. deleting them\"", StringComparison.Ordinal) && n.Contains(AddingRule.TakeAwayReason, StringComparison.Ordinal));
            v = await ComposeOnDoor(f.Engine, door, "Add a measure for margin. Then delete the old measures.");
            Assert.Empty(KeptAdds(v));
            Assert.Contains(v.Notes, n => n.StartsWith("Not ticked", StringComparison.Ordinal) && n.Contains("\"Add a measure for margin\"", StringComparison.Ordinal) && n.Contains(AddingRule.UnreadReason, StringComparison.Ordinal));
        }
    }
}
