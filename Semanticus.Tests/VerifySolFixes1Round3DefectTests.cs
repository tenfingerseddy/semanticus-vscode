// verify: the verifier's failing tests for m11/sol-fixes-1 round 3 (bd6222e3). Each test names ONE defect and is LEFT
// FAILING on purpose: it passes once the defect is fixed. The Jev oracle is the scripted, honest oracle the earlier
// rounds use: it answers the exact request the engine built, through the real recorded oracle and answer reader. Here
// Jev says no to every step, as a good Jev does for a delete when Semanticus has no delete step, so every clause takes
// the Plan's fallback, which is where the coordinator's adding rule (28 Sep) applies.

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
    public sealed class VerifySolFixes1Round3DefectTests
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
                    // JOIN, part 2: the last question (":wanted") now fails closed on Pro, so this scripted Jev answers it yes: the test is about the steps, not about that question.
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(':').Append(q.Kind == DecisionQuestionKind.Noul
                        ? "{\"type\":\"noul\",\"noul\":0.02}"
                        : q.Name.EndsWith(":wanted", StringComparison.Ordinal)
                            ? "{\"type\":\"choice\",\"choice\":\"yes\",\"confidence\":0.95}"
                            : "{\"type\":\"choice\",\"choice\":\"none\",\"confidence\":0.95}");
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-vsf3r3").AskAsync(request, ct);
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

        private static Task<DraftView> ComposeOnDoor(LocalEngine engine, string door, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).composeTask(words: words)
                             : McpTools.ComposeTask(engine, words: words);

        private static string[] KeptAdds(DraftView v) =>
            v.Steps.Concat(v.After ?? Array.Empty<DraftStepView>())
                .Where(s => s.Kept && (s.Title ?? "").StartsWith("Add a", StringComparison.Ordinal)).Select(s => s.Title).ToArray();

        public static IEnumerable<object[]> Deletes() => new[]
        {
            // "make" leads the clause, but "make sure" is not an adding action: the clause asks to remove.
            new object[] { "make sure the old role is removed", new string[0] },
            new object[] { "make sure the old measures are deleted", new string[0] },
            new object[] { "make sure the old measures and the old role are deleted", new string[0] },
            // The second clause has an action of its own ("removed"); it is not a clause the cut left with no action.
            // THE ADDING RULE version 3, b (the coordinator, 29 Sep 00:55) changed this row's label: the take-away veto covers
            // the whole sentence, so the add in it is lost on purpose (the safe side). It was { "Add a measure" }.
            new object[] { "add a measure for margin and the old role should be removed", new string[0] },
            // A slip of the fingers the ruling never asked for: "refine" is read as "define", an adding action.
            new object[] { "refine the old measures", new string[0] },
        }.SelectMany(c => new[] { new object[] { "studio", c[0], c[1] }, new object[] { "agent", c[0], c[1] } });

        /// <summary>VR5 (medium, the harmful kind: the opposite change is drafted as a kept step; the same kind as the
        /// completeness verifier's round 3 F1 this rule was adopted for). The adding rule reads a clause's action as its
        /// FIRST word (DescribedSteps.AsksToAdd), and a clause whose first word is not an action as having none, taking the
        /// action of the clause before it. So "make sure the old role is removed" asks to add ("make"), "the old role should
        /// be removed" after an add asks to add (the clause before it adds), and "refine" is "define" with a slip of the
        /// fingers. Each drafts Add a role or Add a measure as a KEPT Plan step for words that ask to take something away,
        /// on both doors, with Jev on and saying no to every step. The ruling: "a template that adds ONLY when the clause's
        /// own action is an adding one", and "the one thing that must never happen is an add nobody asked for".</summary>
        [Theory]
        [MemberData(nameof(Deletes))]
        public async Task VR5_words_that_ask_to_take_something_away_draft_no_kept_add(string door, string words, string[] asked)
        {
            using var f = await ProAsync("VR5" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim");
            var v = await ComposeOnDoor(f.Engine, door, words);
            Assert.Equal(asked, KeptAdds(v));
        }

        public static IEnumerable<object[]> Adds() => new[]
        {
            new object[] { "can you add a measure for margin", "Add a measure" },
            new object[] { "now add a measure for margin", "Add a measure" },
            new object[] { "let's create a role for managers", "Add a role" },
            new object[] { "we need a new measure for margin", "Add a measure" },
        };

        /// <summary>VR6 (low, a step lost): the same first-word reading misses an adding action that does not lead its clause.
        /// "can you add a measure for margin" asks to add, and before the rule the fallback gave it Add a measure; now the
        /// clause takes no step and the draft opens as the menu. The ruling: every pick stands where the clause's own action
        /// adds.</summary>
        [Theory]
        [MemberData(nameof(Adds))]
        public async Task VR6_words_that_ask_to_add_keep_their_add_when_the_action_is_not_the_first_word(string words, string add)
        {
            using var f = await ProAsync("VR6" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim");
            var v = await ComposeOnDoor(f.Engine, "agent", words);
            Assert.Contains(add, KeptAdds(v));
        }
        /// <summary>Author's edits, one per key, as it sends them for a field typed from <paramref name="from"/> to
        /// <paramref name="to"/>: every value in between, each expecting the one before it.</summary>
        private static object[] Typed(string target, string field, string from, string to)
        {
            var ops = new List<object>();
            var was = from;
            for (var i = from.Length + 1; i <= to.Length; i++)
            {
                var now = to.Substring(0, i);
                ops.Add(new { op = "set_field", target, field, expect = was, value = now });
                was = now;
            }
            return ops.ToArray();
        }

        /// <summary>VR7 (medium; older than this lane, the same class as the step title round 2 found and this round fixed
        /// for the title only). Author sends a step's "What it does" one edit per key, so typing "reconcile the stock
        /// ledger" passes through "reconcile " with a space at its end. The patcher reads a step's text back from the file
        /// without that space and refuses the whole save: "The source spelling cannot represent the requested 'instructions'
        /// value without changing its meaning". Seen live on bd6222e3: a new blank workflow with its step text typed cannot
        /// be saved, and the name-taken way out ("Save as month-end-check-2") is refused the same way, so that kept work is
        /// stranded again. The fix for the title (trim at both ends, WorkflowDocumentPatcher.cs:1019) does not cover it.</summary>
        [Fact]
        public void VR7_a_step_text_typed_one_key_at_a_time_through_spaces_saves()
        {
            const string source = "---\nname: typed-text\ntitle: Typed text\nversion: 1\n---\n## Step 1: Check\n";
            var opened = WorkflowDocumentPatcher.Apply("typed-text", source, "[]");
            Assert.Null(opened.Error);
            var key = opened.Model.Definition.Steps.Single().Key;
            var saved = WorkflowDocumentPatcher.Apply("typed-text", source,
                JsonSerializer.Serialize(Typed(key, "instructions", "", "reconcile the stock ledger")));
            Assert.True(saved.Error == null, saved.ErrorCode + ": " + saved.Error);
            Assert.Contains("reconcile the stock ledger", saved.Text, StringComparison.Ordinal);
        }
    }
}
