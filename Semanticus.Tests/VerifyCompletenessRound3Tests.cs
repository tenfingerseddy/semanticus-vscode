// verify: m11/completeness round 3 (28 Sep), each test failing on purpose at 01ec7063.
//
// D5 says a described delete never drafts an add. Round 3 made what decides a delete the clause's ACTION, the word a
// step leads with (DescribedSteps.Removes), and that fixed the exact words of round 2. The clause cut still runs FIRST
// and splits at "and", a comma and a colon (DescribedSteps.Cut), so the word that asks for the delete stays in the first
// clause and every clause after it names only a thing, which the word match turns into the step that adds that thing:
// "delete the old measures and the old role" drafts Add a role, "remove the Sales and Date relationship" drafts Relate
// two tables. A delete word that is not the lead ("clean up the model by removing the old measures", "start by deleting
// the unused measures", "Unused measures - delete") is no longer seen at all: round 2 (a delete word anywhere) made those
// placeholders, round 3 drafts Add a measure again. A lead the list leaves out on purpose ("clear") and a delete asked
// with no removing verb ("get the old measures out of the model", "we no longer need the old measures") draft Add a
// measure too. On the Plan the same words offer the add as the menu's match, some of them beside the new sentence that
// says a delete is not a step, and once Jev drafts there (the release's switch, JevDraftsForTest = true) a clause after
// the cut keeps the add Jev is sure of. Measured on the lane's Release engine over MCP as well: 26 of 32 delete phrasings
// drafted an add on Workflows and 27 of 32 offered one first on the Plan's menu.
//
// Every phrasing in the first three tests asks to take something away and nothing else, so no step template that adds
// is ever its step. The fourth is round 2's R4 in other shapes: a name led by a removal noun or verb ("Deletion Flag
// should be hidden", "Drop Off Location should be a geography column") and "remove" a setting from an object ("remove
// the format from the Margin measure") ask for an action Semanticus has, and the lane's base drafted it.

using System;
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
    public sealed class VerifyCompletenessRound3Tests
    {
        private static bool Adds(string op, string stepName) =>
            (op ?? "").StartsWith("create_", StringComparison.Ordinal) || (stepName ?? "").StartsWith("Add ", StringComparison.Ordinal);

        private static bool MenuAdds(string id) => DraftMenu.All.Any(t => t.Id == id && DescribedSteps.Adds(t));

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("delete the old measures and the old role")]
        [InlineData("remove the Sales Manager and Region Manager roles")]
        [InlineData("remove the Sales and Date relationship")]
        [InlineData("retire the old role and its measures")]
        [InlineData("Delete: the old measures")]
        [InlineData("clean up the model by removing the old measures")]
        [InlineData("start by deleting the unused measures")]
        [InlineData("Unused measures - delete")]
        [InlineData("clear the old measures")]
        [InlineData("get the old measures out of the model")]
        [InlineData("we no longer need the old measures")]
        public async Task A_described_delete_never_drafts_an_add_whatever_the_words_on_either_door(string words)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("DeleteWordsR3.bim");
            f.Engine.JevDraftsForTest = false;   // Jev off: the word match decides, as it does whenever Jev is off, not asked or not sure
            var studio = await new EngineRpcTarget(f.Engine).describeWorkflowSteps(words);
            var agent = await McpTools.DescribeWorkflowSteps(f.Engine, words);
            foreach (var (door, d) in new[] { ("Studio", studio), ("agent", agent) })
                foreach (var step in d.Steps)
                    Assert.False(step.Ops.Any(op => Adds(op, step.StepName)),
                        $"{door} door: '{words}' drafted its step '{step.Words}' as {step.StepName} ({string.Join(",", step.Ops)}, chosen by {step.ChosenBy}): a delete became an add");
        }

        /// <summary>The other way round (round 2's R4, other shapes of the same words): a name led by a removal noun or a
        /// removing verb ("Deletion Flag", "Drop Off Location"), and "remove" a setting FROM an object, ask for an action
        /// Semanticus has. The lane's base (568435d3, its Release engine over MCP, Jev off) drafted each of these ops; round 3
        /// makes every one a placeholder with the reason "Semanticus has no action that deletes or removes yet".</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("Deletion Flag should be hidden", "set_column_hidden")]
        [InlineData("Drop Off Location should be a geography column", "set_data_category")]
        [InlineData("remove the summarization from the Year column", "set_summarize_by")]
        [InlineData("remove the format from the Margin measure", "set_measure_format")]
        [InlineData("remove the display folder from the Sales measures", "set_display_folder")]
        public async Task A_step_with_a_delete_word_that_asks_for_an_action_Semanticus_has_keeps_it_on_either_door(string words, string op)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("KeepsItsStepR3.bim");
            f.Engine.JevDraftsForTest = false;
            var studio = await new EngineRpcTarget(f.Engine).describeWorkflowSteps(words);
            var agent = await McpTools.DescribeWorkflowSteps(f.Engine, words);
            foreach (var (door, d) in new[] { ("Studio", studio), ("agent", agent) })
            {
                var step = Assert.Single(d.Steps);
                Assert.True(step.Op == op,
                    $"{door} door: '{words}' asks for {op} (the lane's base drafted it) but drafted {step.Op ?? "a placeholder"} with the reason \"{step.Reason}\"");
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("delete the old measures and the old role")]
        [InlineData("remove the Sales and Date relationship")]
        [InlineData("clean up the model by removing the old measures")]
        [InlineData("Unused measures - delete")]
        [InlineData("clear the old measures")]
        [InlineData("we no longer need the old measures")]
        public async Task The_Plan_menu_never_offers_an_add_as_the_match_for_a_delete_whatever_the_words(string words)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("PlanMenuDeleteR3.bim");
            f.Engine.JevDraftsForTest = false;   // this branch's Plan door, and the release's whenever Jev is not sure
            var studio = await new EngineRpcTarget(f.Engine).composeTask(words);
            var agent = await McpTools.ComposeTask(f.Engine, words: words);
            foreach (var (door, v) in new[] { ("Studio", studio), ("agent", agent) })
            {
                var offered = v.Menu.Where(m => m.Matched && MenuAdds(m.Id)).Select(m => m.Title).ToArray();
                Assert.True(offered.Length == 0 && !v.Steps.Any(s => MenuAdds(s.Id)),
                    $"{door} door: '{words}' on the Plan offers {string.Join(", ", offered)} as its match (menu line: \"{v.MenuLine}\"): a delete became an add");
            }
        }

        [Theory]
        [InlineData("delete the old measures and the old role")]
        [InlineData("clean up the model by removing the old measures")]
        public async Task Once_Jev_drafts_on_the_Plan_a_delete_in_other_words_drafts_no_add(string words)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("PlanJevDeleteR3.bim");
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = true;   // the release's JevDraftsOnPlan (m11/sol-fixes-1)
            f.Engine.UseDecisionsForTest(new DecisionService(new SureOfEveryAddOracle(), new DecisionBudget()));
            var studio = await new EngineRpcTarget(f.Engine).composeTask(words);
            var agent = await McpTools.ComposeTask(f.Engine, words: words);
            foreach (var (door, v) in new[] { ("Studio", studio), ("agent", agent) })
                Assert.False(v.Steps.Any(s => MenuAdds(s.Id)),
                    $"{door} door drafted '{words}' on the Plan as {string.Join(", ", v.Steps.Select(s => s.Title))}: a delete became an add");
        }

        /// <summary>Jev sure (0.97) of every template that adds, a sure no (0.02) to every other include, none to every
        /// choice: the shape of the lane's own SureOfEveryAdd (DescribedStepsTests) and round 2's SureOfAddOracle.</summary>
        private sealed class SureOfEveryAddOracle : IDecisionOracle
        {
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PointName != DecisionRegistry.ComposeDraft)
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "this test answers compose.draft only"));
                var adds = DraftMenu.All.Where(DescribedSteps.Adds).Select(t => "step:" + t.Id + ":include").ToHashSet();
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
                        sb.Append("{\"type\":\"noul\",\"noul\":").Append((adds.Contains(q.Name) ? 0.97 : 0.02).ToString(CultureInfo.InvariantCulture)).Append('}');
                    else
                        sb.Append("{\"type\":\"choice\",\"choice\":\"none\",\"confidence\":0.95}");
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct);
            }
        }
    }
}
