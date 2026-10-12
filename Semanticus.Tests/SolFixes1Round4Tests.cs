// review-fix tests for the verifier's round 3 on m11/sol-fixes-1 (bd6222e3): the adding rule, version 2 (the coordinator,
// 28 Sep 17:30), around the verifier's VR5 and VR6 cases; the half of the rule the verifier found untested; a step's text
// typed with a space at its end; and permission withdrawn under an open draft. The Jev oracle answers the exact request the
// engine built, through the real recorded oracle and answer reader, as the verifier's tests do.

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
    public sealed class SolFixes1Round4Tests
    {
        // ---- on both doors, Jev on (saying no, sure of an add, or not answering) and off -----------------------------

        /// <summary>Answers compose.draft as the script says for each question (null: "no" with 0.02 for a yes or no, "none"
        /// for a pick), and does not decide at all for words the script names.</summary>
        private sealed class Jev : IDecisionOracle
        {
            private readonly Func<string, string, (string, double)?> _script;
            private readonly Func<string, bool> _silent;
            public Jev(Func<string, string, (string, double)?> script, Func<string, bool> silent = null) { _script = script; _silent = silent ?? (_ => false); }
            public bool Available => true;

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                var words = request.Snapshot.TryGetValue("draft_words", out var w) ? w as string : null;
                if (request.PointName != DecisionRegistry.ComposeDraft || _silent(words ?? ""))
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "this test does not answer these words"));
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
                        sb.Append("{\"type\":\"noul\",\"noul\":").Append((a?.Item2 ?? 0.02).ToString(CultureInfo.InvariantCulture)).Append('}');
                    else
                    {
                        var (choice, confidence) = a ?? (q.Name.EndsWith(":wanted", StringComparison.Ordinal) ? ("yes", 0.95) : ("none", 0.95));   // join part 2: the last question fails closed, so a script answers it yes
                        sb.Append("{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                          .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append('}');
                    }
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-sf1r4").AskAsync(request, ct);
            }
        }

        private static async Task<DecisionFixture> ProAsync(string file, IDecisionOracle jev, bool consent = true)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync(file);
            await f.SaveAsAsync(file);
            if (consent) await f.ConsentAsync();
            f.Engine.UseDecisionsForTest(new DecisionService(jev, new DecisionBudget()));
            return f;
        }

        private static string File(string name) => name + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim";

        private static Task<DraftView> ComposeOnDoor(LocalEngine engine, string door, string words) =>
            door == "studio" ? new EngineRpcTarget(engine).composeTask(words: words)
                             : McpTools.ComposeTask(engine, words: words);

        private static IEnumerable<DraftStepView> AllSteps(DraftView v) => v.Steps.Concat(v.After ?? Array.Empty<DraftStepView>());

        private static string[] Adds(DraftView v) =>
            AllSteps(v).Where(s => (s.Title ?? "").StartsWith("Add a", StringComparison.Ordinal)).Select(s => s.Title).ToArray();

        private static string NoStepNote(DraftView v) => v.Notes.SingleOrDefault(n => n.StartsWith("No step for", StringComparison.Ordinal));

        public static IEnumerable<object[]> Doors() => new[] { new object[] { "studio" }, new object[] { "agent" } };

        /// <summary>Jev SURE of an add for words that ask to take something away: the rule holds for Jev's picks too, so the
        /// draft has no add at all, sure or dropped, and the words are kept with the take-away reason.</summary>
        [Theory]
        [MemberData(nameof(Doors))]
        public async Task Jev_sure_of_an_add_for_words_that_take_away_drafts_no_add(string door)
        {
            var sure = new Jev((w, q) => q == JevDraftBuilder.Q("role", JevDraftBuilder.Include) || q == JevDraftBuilder.Q("add-measure", JevDraftBuilder.Include) ? ("yes", 0.99) : null);
            using var f = await ProAsync(File("R4Sure"), sure);
            var v = await ComposeOnDoor(f.Engine, door, "make sure the old role is removed");
            Assert.Empty(Adds(v));
            Assert.Contains(AddingRule.TakeAwayReason, NoStepNote(v) ?? "", StringComparison.Ordinal);

            // The same Jev, for words that ask to add: its pick stands.
            v = await ComposeOnDoor(f.Engine, door, "can you add a role for managers");
            Assert.Contains("Add a role", Adds(v));
        }

        /// <summary>Jev saying no to every step: a clause that asks to take something away is a placeholder with the true
        /// reason, named in the draft's notes, and never "not read yet" (it was read, and no step here does it).</summary>
        [Theory]
        [MemberData(nameof(Doors))]
        public async Task Words_that_take_away_are_kept_with_the_true_reason_and_not_as_unread(string door)
        {
            using var f = await ProAsync(File("R4No"), new Jev((w, q) => null));
            var v = await ComposeOnDoor(f.Engine, door, "hide the keys, then make sure the old measures and the old role are deleted");
            Assert.Empty(Adds(v));
            Assert.Contains(AllSteps(v), s => s.Title == "Hide an object");
            var note = NoStepNote(v);
            Assert.NotNull(note);
            Assert.Contains("\"make sure the old measures\"", note, StringComparison.Ordinal);
            Assert.Contains("\"the old role are deleted\"", note, StringComparison.Ordinal);
            Assert.EndsWith(AddingRule.TakeAwayReason, note, StringComparison.Ordinal);
            Assert.DoesNotContain(v.Notes, n => n.StartsWith("Not read yet:", StringComparison.Ordinal));
        }

        /// <summary>The half of the rule the verifier found untested (round 3, ADDU): a clause whose ask was NOT answered,
        /// whose word match is an add it may not take. It used to be kept as "Not read yet ... Add more words asks Jev about
        /// it again", which is not why it has no step. Now it is kept with the rule's reason.</summary>
        [Theory]
        [MemberData(nameof(Doors))]
        public async Task An_unanswered_clause_whose_only_step_would_add_is_kept_with_the_rules_reason(string door)
        {
            using var f = await ProAsync(File("R4Silent"), new Jev((w, q) => null, silent: w => w.Contains("removed", StringComparison.Ordinal)));
            var v = await ComposeOnDoor(f.Engine, door, "hide the keys, then make sure the old role is removed");
            Assert.Empty(Adds(v));
            Assert.Contains(AddingRule.TakeAwayReason, NoStepNote(v) ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(v.Notes, n => n.StartsWith("Not read yet:", StringComparison.Ordinal) && n.Contains("removed", StringComparison.Ordinal));
        }

        /// <summary>Jev off: no permission given, and the Plan's Jev switch off. The draft opens as the menu, which keeps no
        /// step, so no add is drafted either way.</summary>
        [Theory]
        [MemberData(nameof(Doors))]
        [Trait(TestCost.Key, TestCost.Sweep)]
        public async Task With_jev_off_words_that_take_away_draft_no_add(string door)
        {
            using (var f = await ProAsync(File("R4Off"), new Jev((w, q) => ("yes", 0.99)), consent: false))
                Assert.DoesNotContain(AllSteps(await ComposeOnDoor(f.Engine, door, "make sure the old role is removed")), s => s.Kept && (s.Title ?? "").StartsWith("Add a", StringComparison.Ordinal));
            using (var f = await ProAsync(File("R4Switch"), new Jev((w, q) => ("yes", 0.99))))
            {
                f.Engine.JevDraftsForTest = false;
                Assert.DoesNotContain(AllSteps(await ComposeOnDoor(f.Engine, door, "make sure the old role is removed")), s => s.Kept && (s.Title ?? "").StartsWith("Add a", StringComparison.Ordinal));
            }
        }

        // Default tier: the first row of the sweep form, through the same body (lean suite, lane A).
        public static IEnumerable<object[]> With_jev_off_words_that_take_away_draft_no_add_SampleRows() => Doors().Take(1);

        [Theory]
        [MemberData(nameof(With_jev_off_words_that_take_away_draft_no_add_SampleRows))]
        public Task With_jev_off_words_that_take_away_draft_no_add_sample(string door) => With_jev_off_words_that_take_away_draft_no_add(door);

        /// <summary>Every other pick stands (the rule's own examples): steps that do not add are never taken away.</summary>
        [Theory]
        [MemberData(nameof(Doors))]
        public async Task Words_that_take_away_keep_a_step_that_does_not_add(string door)
        {
            using var f = await ProAsync(File("R4Stand"), new Jev((w, q) => null));
            var v = await ComposeOnDoor(f.Engine, door, "Deletion Flag should be hidden");
            Assert.Contains(AllSteps(v), s => s.Kept && s.Title == "Hide an object");
            v = await ComposeOnDoor(f.Engine, door, "remove the format from the Margin measure");
            Assert.Contains(AllSteps(v), s => s.Kept && s.Title == "Format a measure");
            Assert.Null(NoStepNote(v));
        }

        /// <summary>Add more words reads new words by the same rule, and the words kept with no step are not asked again.</summary>
        [Fact]
        public async Task Add_more_words_keeps_words_that_take_away_with_their_reason()
        {
            using var f = await ProAsync(File("R4More"), new Jev((w, q) => null));
            var v = await f.Engine.ComposeTaskAsync("hide the keys", null, null, null, null, "human");
            v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "and make sure the old role is removed", "human");
            Assert.Empty(Adds(v));
            Assert.Contains("\"make sure the old role is removed\"", NoStepNote(v) ?? "", StringComparison.Ordinal);
        }

        /// <summary>A draft opened in Author brings the words with no step along as steps to fill in, each saying why.</summary>
        [Fact]
        public async Task Open_in_author_brings_the_words_with_no_step_as_steps_to_fill_in()
        {
            using var f = await ProAsync(File("R4Author"), new Jev((w, q) => null));
            var v = await McpTools.ComposeTask(f.Engine, words: "hide the keys, then make sure the old role is removed");
            var opened = await McpTools.EditDraft(f.Engine, v.DraftId, "open_in_author");
            Assert.Contains("make sure the old role is removed", opened.Document.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(AddingRule.TakeAwayReason, opened.Document.Text, StringComparison.Ordinal);
        }

        // ---- VR4w: permission withdrawn under an open draft ---------------------------------------------------------

        /// <summary>The page reads an open draft again when the Plan moves. Giving permission moved the Plan (its marker is a
        /// model edit), withdrawing did not, so the open draft kept promising "Add more words asks Jev" (verifier on
        /// m11/sol-fixes-1 round 3, VR4w). Withdrawing now tells the Plan it moved, and the draft read then says why not.</summary>
        [Fact]
        public async Task Withdrawing_permission_tells_the_plan_it_moved()
        {
            using var f = await ProAsync(File("R4Withdraw"), new Jev((w, q) => null));
            var words = string.Join(", ", Enumerable.Range(1, 14).Select(i => "zzqx wibble " + i.ToString(CultureInfo.InvariantCulture)));
            var v = await f.Engine.ComposeTaskAsync(words, null, null, null, null, "human");
            var moved = 0;
            void Count(ChangePlanView _) => Interlocked.Increment(ref moved);
            f.Sessions.Bus.PlanChanged += Count;
            try { await f.Engine.WithdrawConsentAsync("human"); }
            finally { f.Sessions.Bus.PlanChanged -= Count; }
            Assert.True(moved > 0, "withdrawing permission did not tell the Plan it moved, so an open draft is not read again");
            v = await f.Engine.GetDraftAsync(v.DraftId);
            Assert.DoesNotContain(v.Notes, n => n.Contains("Add more words asks Jev", StringComparison.Ordinal));
        }

        // ---- VR7: a step's text typed with a space at its end ---------------------------------------------------------

        /// <summary>Typed text always saves. A step's "What it does" left ending in a space, or typed through a line break,
        /// saves; the file keeps the text without the spaces at its ends, which it cannot hold (verifier on m11/sol-fixes-1
        /// round 3, VR7). Before, the save was refused with "The source spelling cannot represent the requested
        /// 'instructions' value".</summary>
        [Theory]
        [InlineData("reconcile the stock ledger ")]
        [InlineData(" reconcile the stock ledger")]
        [InlineData("reconcile the stock ledger\n")]
        [InlineData("reconcile\n\nthe stock ledger ")]
        public void A_step_text_ending_in_a_space_saves(string typed)
        {
            const string source = "---\nname: typed-end\ntitle: Typed end\nversion: 1\n---\n## Step 1: Check\nOld text.\n";
            var opened = WorkflowDocumentPatcher.Apply("typed-end", source, "[]");
            Assert.Null(opened.Error);
            var key = opened.Model.Definition.Steps.Single().Key;
            var saved = WorkflowDocumentPatcher.Apply("typed-end", source, JsonSerializer.Serialize(new object[]
            {
                new { op = "set_field", target = key, field = "instructions", expect = "Old text.", value = typed },
            }));
            Assert.True(saved.Error == null, saved.ErrorCode + ": " + saved.Error);
            Assert.Contains(typed.Trim() + "\n", saved.Text, StringComparison.Ordinal);
            // What it held a moment ago, with its space, is still what the next edit may expect.
            var again = WorkflowDocumentPatcher.Apply("typed-end", source, JsonSerializer.Serialize(new object[]
            {
                new { op = "set_field", target = key, field = "instructions", expect = "Old text.", value = typed },
                new { op = "set_field", target = key, field = "instructions", expect = typed, value = typed + "x" },
            }));
            Assert.True(again.Error == null, again.ErrorCode + ": " + again.Error);
        }
    }
}
