// m11/sol-fixes-1 round 5 (the final fix): THE ADDING RULE version 3 (the coordinator, 29 Sep 00:55) on both doors, and the
// verifier's round 4 low items L1 (the menu), L2 (a take-away's note) and L6 (a Windows line break over set_field). The
// rule on its own is AddingRuleTests; the verifier's own failing tests are VerifySolFixes1Round4DefectTests.

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
    public sealed class SolFixes1Round5Tests
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
                return new RecordedDecisionOracle(sb.ToString(), "jev-sf1r5").AskAsync(request, ct);
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

        /// <summary>The draft's steps that add, kept or dropped, by title.</summary>
        private static string[] Adds(DraftView v) =>
            v.Steps.Concat(v.After ?? Array.Empty<DraftStepView>())
                .Where(s => AddIds.Any(a => s.Id == a || s.Id.StartsWith(a + "-", StringComparison.Ordinal) && s.Id.Substring(a.Length + 1).All(char.IsDigit)))
                .Select(s => s.Title + (s.Kept ? " (kept)" : " (dropped)")).ToArray();

        private static string[] KeptAdds(DraftView v) => Adds(v).Where(a => a.EndsWith("(kept)", StringComparison.Ordinal)).ToArray();

        public static IEnumerable<object[]> DoorsAndJev() => new[]
        {
            new object[] { "studio", "no" }, new object[] { "agent", "no" }, new object[] { "studio", "sure" }, new object[] { "agent", "sure" },
        };

        /// <summary>The verifier's round 4 attack phrasings labelled before any run as asking for no add (its evidence,
        /// attack-labels.json, sha256 0c50ec29...), every one but the three Add more words ones, which the next test drives.</summary>
        private static readonly string[] TakeAwayAttack =
        {
            "the new role and the new measure should be deleted", "Total Sales and Average Price should be removed",
            "new measures and new roles have to go", "the new Margin measure and the new Managers role are not needed",
            "Total Sales and Total Cost can go", "Total Sales measure, delete it", "The new Margin measure. Delete it.",
            "new role for managers: remove it", "Average Price: no longer needed", "Total Sales. Remove it.", "the new measure: bin it",
            "the measure that was added yesterday, remove it", "the role that was created last week. Delete it.",
            "the columns that were added by the import, drop them", "a new role and a new measure were added by mistake, remove them",
            "create a cleanup step for the old roles", "set up a cleanup of the old measures", "build the model without the old role",
            "make old roles vanish", "make the old role vanish", "add the old role to the list of things to remove", "don't add a role",
            "do not create a new measure", "no need to add a role", "never add a role for managers", "we don't need a new role",
            "please don't make a new column", "instead of adding a role, delete the old one", "undo the new measure",
            "revert the role I created", "delete Total Sales and Total Cost", "delete total sales and total cost",
            "remove the Margin measure, then Total Cost too", "the new role shouldn't exist", "the new role isn't needed any more",
            "count of orders and count of returns should be hidden", "new measures and new columns should be hidden",
            // Version 3's new signs.
            "create a clean-up step for the old roles", "add a step to purge the old roles", "add a measure and prune the old ones",
            "add a measure before we decommission the old model", "create a role, we sunset the old one", "add a measure and archive the old ones",
        };

        /// <summary>VR8 by version 5: no phrasing that asks to take something away, or to add nothing, drafts a KEPT add on
        /// either door, with Jev saying no (the Plan's word match decides) and with Jev SURE of every add (its picks go through
        /// the same rule). Version 5 offers an add UNTICKED (a dropped step, or a note) for the words that hold an adding verb,
        /// so this test, which said "kept or dropped", now counts kept adds only: rewritten, rule line version 5 item 2.</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task No_phrasing_that_takes_away_drafts_an_add(string door, string jev)
        {
            using var f = await ProAsync("R5Take" + door + jev, jev == "sure" ? SureOfEveryAdd : SaysNo);
            var wrong = new List<string>();
            foreach (var words in TakeAwayAttack)
            {
                var adds = KeptAdds(await ComposeOnDoor(f.Engine, door, words));
                if (adds.Length > 0) wrong.Add(words + " => " + string.Join(", ", adds));
            }
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> No_phrasing_that_takes_away_drafts_an_add_SampleRows() => DoorsAndJev().Take(1);

        [Theory]
        [MemberData(nameof(No_phrasing_that_takes_away_drafts_an_add_SampleRows))]
        public Task No_phrasing_that_takes_away_drafts_an_add_sample(string door, string jev) => No_phrasing_that_takes_away_drafts_an_add(door, jev);

        /// <summary>Polite and leading adds keep their add, kept, on both doors: the verifier's VR6 and its round 4 polite adds,
        /// and the completeness verifier's v2-21 to v2-27.</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Polite_and_leading_adds_keep_their_add(string door, string jev)
        {
            using var f = await ProAsync("R5Polite" + door + jev, jev == "sure" ? SureOfEveryAdd : SaysNo);
            var lost = new List<string>();
            foreach (var (words, add) in new[]
            {
                ("can you add a measure for margin", "Add a measure"), ("now add a measure for margin", "Add a measure"),
                ("let's create a role for managers", "Add a role"), ("we need a new measure for margin", "Add a measure"),
                ("please create a role for the finance team", "Add a role"), ("let's make a measure for margin", "Add a measure"),
                ("could you make a new column for the order year", "Add a column"), ("could you please add a measure for margin", "Add a measure"),
                ("I'd like a new measure for margin", "Add a measure"), ("time to add a measure for margin", "Add a measure"),
                ("remember to create a role for managers", "Add a role"), ("Add a measure for margin please", "Add a measure"),
                ("make sure to add a Margin measure", "Add a measure"),
            })
            {
                var kept = KeptAdds(await ComposeOnDoor(f.Engine, door, words));
                if (!kept.Contains(add + " (kept)")) lost.Add(words + " => " + string.Join(", ", kept));
            }
            Assert.True(lost.Count == 0, string.Join("\n", lost));
            // v6 B (moved out of the list above, relabelled from kept): "Remove" anywhere in the text demotes the add.
            var removed = await ComposeOnDoor(f.Engine, door, "Remove the old role. Add a measure for margin.");
            Assert.Empty(KeptAdds(removed));
            Assert.Contains("Add a measure (dropped)", Adds(removed));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> Polite_and_leading_adds_keep_their_add_SampleRows() => DoorsAndJev().Take(1);

        [Theory]
        [MemberData(nameof(Polite_and_leading_adds_keep_their_add_SampleRows))]
        public Task Polite_and_leading_adds_keep_their_add_sample(string door, string jev) => Polite_and_leading_adds_keep_their_add(door, jev);

        /// <summary>Add more words after a take-away (version 5): the rule reads the new words as they are typed, and no longer
        /// carries the draft's open sentence into them (version 3 b is retired). A NEW add of a named kind is kept after the
        /// take-away, the add of "the new measures" with no verb is not drafted, and an add that names something that already
        /// exists ("to them") is unticked. Rewritten from "the take-away it carries on vetoes the add", rule line version 5 item 4.</summary>
        [Theory]
        [MemberData(nameof(DoorsAndJev))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task Add_more_words_reads_the_new_words_with_the_sentence_they_carry_on(string door, string jev)
        {
            using var f = await ProAsync("R5More" + door + jev, jev == "sure" ? SureOfEveryAdd : SaysNo);
            var v = await ComposeOnDoor(f.Engine, door, "delete the old role");
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "also the new measures");
            Assert.Empty(Adds(v));
            v = await ComposeOnDoor(f.Engine, door, "delete the old measures");
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "and add a role for managers");
            // v6 B (relabelled from kept): "delete" in an earlier typing demotes the add typed after it.
            Assert.Empty(KeptAdds(v));
            Assert.Contains("Add a role (dropped)", Adds(v));
            v = await ComposeOnDoor(f.Engine, door, "delete the old measures");
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "and add the Managers role to them");
            Assert.Empty(KeptAdds(v));
            Assert.Contains("Add a role (dropped)", Adds(v));
            v = await ComposeOnDoor(f.Engine, door, "delete the old measures.");
            v = await AddMoreWordsOnDoor(f.Engine, door, v.DraftId, "Add a role for managers");
            Assert.Empty(KeptAdds(v));   // v6 B (relabelled from kept)
            Assert.Contains("Add a role (dropped)", Adds(v));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> Add_more_words_reads_the_new_words_with_the_sentence_they_carry_on_SampleRows() => DoorsAndJev().Take(1);

        [Theory]
        [MemberData(nameof(Add_more_words_reads_the_new_words_with_the_sentence_they_carry_on_SampleRows))]
        public Task Add_more_words_reads_the_new_words_with_the_sentence_they_carry_on_sample(string door, string jev) => Add_more_words_reads_the_new_words_with_the_sentence_they_carry_on(door, jev);

        /// <summary>An add the words after it hold back (version 5, K4) is offered unticked with the person's words and the one
        /// true line on why, and the take-away is kept with its own reason. Rewritten from "kept with the sentence's reason":
        /// version 5 retired the sentence-wide veto and its SentenceTakesAwayReason.</summary>
        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_vetoed_add_is_kept_with_the_sentences_reason(string door)
        {
            using var f = await ProAsync("R5Veto" + door, SaysNo);
            var v = await ComposeOnDoor(f.Engine, door, "add a measure for margin and the old role should be removed");
            Assert.Empty(KeptAdds(v));
            Assert.Contains("Add a measure (dropped)", Adds(v));
            Assert.Contains(v.Notes, n => n.StartsWith("Not ticked", StringComparison.Ordinal) && n.Contains("\"add a measure for margin\"", StringComparison.Ordinal) && n.Contains(AddingRule.UnreadReason, StringComparison.Ordinal));
            Assert.Contains(v.Notes, n => n.Contains("\"the old role should be removed\"", StringComparison.Ordinal) && n.Contains(AddingRule.TakeAwayReason, StringComparison.Ordinal));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("studio")]
        public Task A_vetoed_add_is_kept_with_the_sentences_reason_sample(string door) => A_vetoed_add_is_kept_with_the_sentences_reason(door);

        /// <summary>L1: under words that ask to take something away the menu does not put the opposite add first: a step
        /// that adds is named only by clauses that ask to add. With Jev off the draft is the menu.</summary>
        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task The_menu_does_not_offer_an_add_first_for_words_that_take_away(string door)
        {
            using var f = await ProAsync("R5Menu" + door, SaysNo, consent: false);
            foreach (var words in new[] { "Total Sales measure, delete it", "make sure the old role is removed", "the new role and the new measure should be deleted" })
            {
                var menu = (await ComposeOnDoor(f.Engine, door, words)).Menu;
                Assert.NotNull(menu);
                Assert.DoesNotContain(menu, m => m.Matched && AddIds.Contains(m.Id));
                Assert.DoesNotContain(menu[0].Id, AddIds);
            }
            // An add the words ask for still comes first.
            Assert.Equal("role", (await ComposeOnDoor(f.Engine, door, "add a role for managers")).Menu[0].Id);
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("studio")]
        public Task The_menu_does_not_offer_an_add_first_for_words_that_take_away_sample(string door) => The_menu_does_not_offer_an_add_first_for_words_that_take_away(door);

        /// <summary>L2: a clause that asks to take something away keeps its note when Jev only leaned to steps for it that do
        /// not add (they stand dropped, beside the note). Before, any such step took the note away.</summary>
        [Theory]
        [InlineData("studio")]
        [InlineData("agent")]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task A_take_away_keeps_its_note_beside_steps_jev_only_leaned_to(string door)
        {
            using var f = await ProAsync("R5Lean" + door, new Jev(0.02, 0.6));
            var v = await ComposeOnDoor(f.Engine, door, "Average Price should be removed");
            Assert.Empty(KeptAdds(v));
            Assert.Contains(v.Notes, n => n.StartsWith("No step for", StringComparison.Ordinal)
                && n.Contains("\"Average Price should be removed\"", StringComparison.Ordinal) && n.Contains(AddingRule.TakeAwayReason, StringComparison.Ordinal));
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        [Theory]
        [InlineData("studio")]
        public Task A_take_away_keeps_its_note_beside_steps_jev_only_leaned_to_sample(string door) => A_take_away_keeps_its_note_beside_steps_jev_only_leaned_to(door);

        /// <summary>L6: a step's text sent over set_field with a Windows line break saves, with the break the file writes
        /// (an agent's edit; a browser text box never sends one). Before, it was refused as unpreservable.</summary>
        [Fact]
        public void A_step_text_with_a_windows_line_break_saves()
        {
            const string source = "---\nname: crlf\ntitle: Crlf\nversion: 1\n---\n## Step 1: Check\nOld text.\n";
            var opened = WorkflowDocumentPatcher.Apply("crlf", source, "[]");
            Assert.Null(opened.Error);
            var key = opened.Model.Definition.Steps.Single().Key;
            var typed = "reconcile" + (char)13 + (char)10 + "the stock ledger ";
            var saved = WorkflowDocumentPatcher.Apply("crlf", source, JsonSerializer.Serialize(new object[]
            {
                new { op = "set_field", target = key, field = "instructions", expect = "Old text.", value = typed },
            }));
            Assert.True(saved.Error == null, saved.ErrorCode + ": " + saved.Error);
            Assert.Contains("reconcile" + (char)10 + "the stock ledger" + (char)10, saved.Text, StringComparison.Ordinal);
            Assert.DoesNotContain((char)13, saved.Text);
        }
    }
}
