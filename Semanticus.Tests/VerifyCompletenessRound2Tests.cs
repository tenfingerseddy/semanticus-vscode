// verify: m11/completeness round 2 (28 Sep), each test failing on purpose at 03684796.
//
// D5 said a described delete or remove never drafts an add, with Jev off, not sure or sure no. Round 2 made a clause a
// placeholder when a fixed list of verbs matches it (DescribedSteps.Removing: delete, remove, drop, erase, discard,
// purge, get rid of), and dropped a sure add from Jev only for such a clause. Everything else still goes to the word
// match, which picks a step by its nouns and forgives a one-letter slip, so:
// 1. The nouns of the same two verbs ("deletion", "removal"), a one-letter slip in the verb ("delte", "remvoe") and
//    common words for the same thing ("eliminate", "scrap", "throw away") still draft Add a measure, Relate two tables or
//    Add a role, on both doors, with Jev off. Seen on the lane's own Release engine over MCP too
//    (describe_workflow_steps, jevAsked false, chosenBy words).
// 2. The Plan's door has no such filter at all. compose_task from words asks Jev clause by clause
//    (AskJevDraftByClauseAsync) and adds every sure template, so "delete the replaced measures" with Jev sure of Add a
//    measure drafts Add a measure. It is off on this branch only because JevDraftBuilder.JevDraftsOnPlan is false; the
//    release turns it on (m11/sol-fixes-1 30f7e62b, describe-jev round 4: JevDraftsOnPlan = true), and this lane merges
//    after it. JevDraftsForTest = true is that switch.
// 3. The other way round: the verb list matches a word anywhere in the clause, so a step that only NAMES a deleted,
//    removed or dropped thing (a soft-delete flag, "Deleted Date", "Dropped Calls", common in real models) is now a
//    placeholder that says "Semanticus has no action that deletes or removes yet", a reason that is not true of the
//    words. At d0452195 these drafted the step they ask for (Add a measure, Hide an object, Rename an object, Describe
//    an object). With Jev on, Jev's sure Add a measure is thrown away for the same reason.

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
    public sealed class VerifyCompletenessRound2Tests
    {
        private static bool Adds(string op, string stepName) =>
            (op ?? "").StartsWith("create_", StringComparison.Ordinal) || (stepName ?? "").StartsWith("Add ", StringComparison.Ordinal);

        [Theory]
        [InlineData("Deletion of the unused measures")]
        [InlineData("removal of the old measures")]
        [InlineData("delte the old measures")]
        [InlineData("remvoe the old measures")]
        [InlineData("eliminate the duplicate measures")]
        [InlineData("scrap the draft measures")]
        [InlineData("throw away the draft measures")]
        [InlineData("eliminate the relationship between Sales and Date")]
        [InlineData("eliminate the Sales Manager role")]
        public async Task A_delete_in_other_words_never_drafts_an_add_on_either_door(string words)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("DeleteWords.bim");
            f.Engine.JevDraftsForTest = false;   // Jev off: the word match decides, as it does whenever Jev is off, not asked or not sure
            var studio = await new EngineRpcTarget(f.Engine).describeWorkflowSteps(words);
            var agent = await McpTools.DescribeWorkflowSteps(f.Engine, words);
            foreach (var (door, d) in new[] { ("Studio", studio), ("agent", agent) })
                foreach (var step in d.Steps)
                    Assert.False(step.Ops.Any(op => Adds(op, step.StepName)),
                        $"{door} door drafted '{step.Words}' as {step.StepName} ({string.Join(",", step.Ops)}, chosen by {step.ChosenBy}): a step that asks to take something away became one that adds");
        }

        [Fact]
        public async Task A_delete_typed_on_the_Plan_never_drafts_an_add_once_Jev_drafts_there()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("PlanDelete.bim");
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = true;   // the release's JevDraftsOnPlan (m11/sol-fixes-1)
            f.Engine.UseDecisionsForTest(new DecisionService(new SureOfAddOracle(), new DecisionBudget()));
            var studio = await new EngineRpcTarget(f.Engine).composeTask("delete the replaced measures");
            var agent = await McpTools.ComposeTask(f.Engine, words: "delete the replaced measures");
            foreach (var (door, v) in new[] { ("Studio", studio), ("agent", agent) })
                Assert.False(v.Steps.Any(s => s.Id == "add-measure"),
                    $"{door} door drafted 'delete the replaced measures' on the Plan as {string.Join(", ", v.Steps.Select(s => s.Title))}: a delete became an add");
        }

        // Relabelled in round 5 by THE ADDING RULE version 2 (the coordinator, 28 Sep 17:30): a take-away sign anywhere in a
        // clause is a veto, so "counts deleted orders" drafts no step that adds (its case v2-vn-15); a null op here means no add.
        [Theory]
        [InlineData("add a measure that counts deleted orders", null)]
        [InlineData("hide the Deleted Date column", "set_column_hidden")]
        [InlineData("rename the Removed Flag column to Is Removed", "rename_object")]
        [InlineData("describe the Deleted Orders measure", "set_description")]
        public async Task A_step_that_only_names_a_deleted_thing_keeps_the_step_it_asks_for(string words, string op)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("NamesDeleted.bim");
            f.Engine.JevDraftsForTest = false;
            var studio = await new EngineRpcTarget(f.Engine).describeWorkflowSteps(words);
            var agent = await McpTools.DescribeWorkflowSteps(f.Engine, words);
            foreach (var (door, d) in new[] { ("Studio", studio), ("agent", agent) })
            {
                var step = Assert.Single(d.Steps);
                if (op == null)
                    Assert.False(step.Ops.Any(o => o.StartsWith("create_", StringComparison.Ordinal)), $"{door} door: '{words}' drafted {step.StepName} under version 2");
                else
                    Assert.True(step.Op == op,
                        $"{door} door: '{words}' asks for {op} but drafted {step.Op ?? "a placeholder"} with the reason \"{step.Reason}\"");
            }
        }

        /// <summary>Relabelled in round 5 by THE ADDING RULE version 2: the take-away sign in "counts deleted orders" is a
        /// veto, so even a Jev sure of Add a measure does not draft it, and the step says why.</summary>
        [Fact]
        public async Task With_Jev_sure_of_the_add_a_measure_of_deleted_orders_is_set_aside_under_version_2()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("CountsDeleted.bim");
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = true;
            f.Engine.UseDecisionsForTest(new DecisionService(new SureOfAddOracle(), new DecisionBudget()));
            var d = await McpTools.DescribeWorkflowSteps(f.Engine, "add a measure that counts deleted orders");
            Assert.True(d.JevAsked, "Jev was asked");
            var step = Assert.Single(d.Steps);
            Assert.False(step.Ops.Any(o => o.StartsWith("create_", StringComparison.Ordinal)),
                $"Jev was sure of Add a measure for 'add a measure that counts deleted orders', and version 2 vetoes it, yet the step is {step.Op}");
            // Re-labelled at the M11 join: under the joined rule (v6 B, the whole-text belt) the sign unticks the add rather than
            // vetoing it, so the Workflows placeholder gives the belt's reason, not the take-away one.
            Assert.StartsWith(AddingRule.SignReason.Substring(0, AddingRule.SignReason.IndexOf(". ", StringComparison.Ordinal) + 1), step.Reason);
        }

        /// <summary>Jev as the lane's own test scripts it (JevDraftBuilderTests.A_delete_clause_drops_a_sure_add_from_Jev_and_says_why):
        /// sure (0.97) that the words ask for Add a measure, a sure no (0.02) to every other include, none to every choice.</summary>
        private sealed class SureOfAddOracle : IDecisionOracle
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
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(':');
                    if (q.Kind == DecisionQuestionKind.Noul)
                        sb.Append("{\"type\":\"noul\",\"noul\":").Append((q.Name == "step:add-measure:include" ? 0.97 : 0.02).ToString(CultureInfo.InvariantCulture)).Append('}');
                    else
                        sb.Append("{\"type\":\"choice\",\"choice\":\"none\",\"confidence\":0.95}");
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct);
            }
        }
    }
}
