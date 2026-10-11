// verify: m11/completeness round 1 (27 Sep), each test failing on purpose at d0452195.
//
// 1. get_decision_points' new text says 'live' is whether this build asks Jev for a point, and that a point that is not
//    live shows as Not on yet with its rows from the rules. ai.schema is not asked in this build (AiSchemaFamily.JevDecides
//    is false, so the Plan has no ai.schema item at all), yet both doors report it live, and What Pro decides shows its
//    switch as on. The audit's F3, which the lane's H5 text now states as true.
// 2. The word match drafts "delete the replaced measures" as Add a measure (create_measure). The lane found it in its
//    own W4 run (docs/redesign/w4-describe-it.md, "Also found") and did not fix it. Workflows > Describe keeps the word
//    match whenever Jev is off, not asked, not sure, or sure that no step fits, so a person who asks to delete gets a
//    step that creates, on Pro with Jev asked as well as without.
// 3. The W4 verdict (JevStarterDescribeIt.Mismatch) counts a check kind carried by ANY drafted step, including a wrong
//    one. In the lane's own live run the wrong Add a measure steps carried dax_probe and bpa_clean, so a draft that adds
//    a measure the starter never adds would park metric-certification (today, if Jev picks Describe for the stamp) and
//    refactor-to-calculation-group (once the chooser offers delete_object, the doc's own "What would change the verdict").

using System;
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
    public sealed class VerifyCompletenessRound1Tests
    {
        [Fact]
        public async Task Ai_schema_is_not_live_on_either_door_while_this_build_never_asks_Jev_for_it()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("Live.bim");
            var agent = await McpTools.GetDecisionPoints(f.Engine);
            var studio = await new EngineRpcTarget(f.Engine).getDecisionPoints();
            foreach (var (door, points) in new[] { ("agent", agent), ("Studio", studio) })
            {
                var ai = points.Single(p => p.Name == DecisionRegistry.AiSchema);
                Assert.True(ai.Live == AiSchemaFamily.JevDecides,
                    $"{door} door: ai.schema live={ai.Live} while AiSchemaFamily.JevDecides={AiSchemaFamily.JevDecides}, so 'live' does not say whether this build asks Jev");
            }
        }

        [Theory]
        [InlineData("delete the replaced measures before saving the model")]
        [InlineData("delete the replaced measures")]
        [InlineData("remove the old measures")]
        public async Task Describing_a_delete_never_drafts_an_add_on_either_door(string words)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("Delete.bim");
            f.Engine.JevDraftsForTest = false;   // the word match decides, as it does whenever Jev is off, not asked or not sure
            var studio = await new EngineRpcTarget(f.Engine).describeWorkflowSteps(words);
            var agent = await McpTools.DescribeWorkflowSteps(f.Engine, words);
            foreach (var (door, d) in new[] { ("Studio", studio), ("agent", agent) })
                foreach (var step in d.Steps)
                    Assert.False(step.Ops.Contains("create_measure"),
                        $"{door} door drafted '{step.Words}' as {step.StepName} ({string.Join(",", step.Ops)}): a step that says delete became one that creates");
        }

        /// <summary>The Pro path of the same defect. describe_workflow_steps' new text says the word match is used "when Jev is
        /// off or not sure", but when Jev is asked and says a sure no to every step (SaidNone), DescribedSteps.ApplyJev keeps
        /// the word match's Add a measure. Seen live on the lane's engine too: jevAsked true, chosenBy words, create_measure.</summary>
        [Theory]
        [InlineData("delete the replaced measures before saving the model")]
        [InlineData("remove the old measures")]
        public async Task With_Jev_saying_no_to_every_step_a_delete_still_never_drafts_an_add(string words)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync("SaysNo.bim");
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = true;
            f.Engine.UseDecisionsForTest(new DecisionService(new SaysNoOracle(), new DecisionBudget()));
            var d = await McpTools.DescribeWorkflowSteps(f.Engine, words);
            Assert.True(d.JevAsked, "Jev was asked");
            foreach (var step in d.Steps)
                Assert.False(step.Ops.Contains("create_measure"),
                    $"Jev said no to every step, yet '{step.Words}' drafted as {step.StepName} (chosen by {step.ChosenBy})");
        }

        /// <summary>Answers every compose.draft question with a sure no (a noul of 0.02, and "none" for a choice).</summary>
        private sealed class SaysNoOracle : IDecisionOracle
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
                    sb.Append(q.Kind == DecisionQuestionKind.Noul
                        ? "{\"type\":\"noul\",\"noul\":0.02}"
                        : "{\"type\":\"choice\",\"choice\":\"none\",\"confidence\":0.95}");
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct);
            }
        }

        [Fact]
        public void The_W4_verdict_does_not_park_a_starter_on_a_draft_that_adds_a_measure_the_starter_never_adds()
        {
            var s = JevStarterDescribeIt.Starters.Single(x => x.Name == "metric-certification");
            DescribedStep Step(string[] ops, params string[] checks) => new DescribedStep { Ops = ops, Checks = checks };
            // The lane's own live run drafted the first clause ("confirm the KPI dictionary still holds") as Add a measure
            // by the word match, with that template's four checks. The last clause needs only Jev being sure of Describe
            // as well as Save (a step holds every sure template, DescribedSteps.ApplyJev), and the rule calls it a match.
            var drafted = new[]
            {
                Step(new[] { "create_measure" }, "valid_dax", "dax_lint", "dax_probe", "bpa_clean"),
                Step(new[] { "run_tests" }),
                Step(Array.Empty<string>()),
                Step(new[] { "set_description", "save_model" }),
            };
            Assert.True(JevStarterDescribeIt.Mismatch(s, drafted) != null,
                "the verdict parks metric-certification on a draft that adds a measure the starter never adds, and whose dax_probe and bpa_clean come from that wrong step");
        }
    }
}
