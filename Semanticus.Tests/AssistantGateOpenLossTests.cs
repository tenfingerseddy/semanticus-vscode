using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The one definition of what an open would lose (LocalEngine.OpenLoss), as the person meets it: the question names the loss, a yes is for the spec
    /// and the plan lines it was asked about, an open that loses nothing is not asked, and below Locked nothing is asked at all (lane m11/sol6-gate, round 3).</summary>
    [Collection("restore-root")]
    public sealed class AssistantGateOpenLossTests : GateFixtureBase
    {
        public AssistantGateOpenLossTests(RestoreRootFixture fixture) : base(fixture) { }

        private const string Spec = "{\"name\":\"PersonSpec\",\"tables\":[],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}";

        private static async Task ApprovedLineAsync(LocalEngine engine, string words)
        {
            var plan = await engine.AddPlanItemAsync("measure:Sales/Total", "set_description", words, null, null, null, "human");
            await engine.SetPlanItemAsync(plan.Items.Last().Id, null, true, "human");
        }

        [Fact]
        public async Task The_question_names_the_spec_and_the_plan_lines_that_would_be_lost()
        {
            var (engine, _, _) = await OpenAsync("q");
            using (engine)
            {
                await engine.SetSpecAsync(Spec, "human");
                await ApprovedLineAsync(engine, "one");
                await ApprovedLineAsync(engine, "two");
                await ApprovedLineAsync(engine, "three");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await RefusedAsync(() => Gate(engine).OpenAsync(AssistantGateKit.WriteModel(Root, "q-other"), false));
                var asked = (await OnlyPendingAsync(engine)).Summary;
                Assert.Contains("your unsaved spec (\"PersonSpec\"", asked);
                Assert.Contains("and 3 lines on your plan (", asked);
                Assert.Contains("one", asked);   // the plan lines are shown with their words, not only counted (UAT-J7-S3-007)
                Assert.DoesNotContain("unsaved changes to the open model", asked);   // the model itself is saved: only the memory is named
            }
        }

        // Join 7 (K3): lines the assistant proposed start approved, so they count as plan work, but the person did not approve them. The words say only what is true.
        [Fact]
        public async Task The_question_does_not_say_the_person_approved_lines_the_assistant_proposed()
        {
            var (engine, _, _) = await OpenAsync("qp");
            using (engine)
            {
                var proposed = await engine.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, origin: "agent");
                var held = proposed.Items.Count(i => i.Status == "approved");   // the lines a swap would lose: the ones that start approved
                Assert.True(held > 0, "the fixture model gives the proposal at least one line that starts approved");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await RefusedAsync(() => Gate(engine).OpenAsync(AssistantGateKit.WriteModel(Root, "qp-other"), false));
                var asked = (await OnlyPendingAsync(engine)).Summary;
                Assert.DoesNotContain("you approved", asked);
                Assert.Contains(held + (held == 1 ? " line on your plan" : " lines on your plan"), asked);
            }
        }

        [Fact]
        public async Task The_question_adds_the_model_edits_when_they_would_go_too()
        {
            var (engine, _, _) = await OpenAsync("qe");
            using (engine)
            {
                await engine.SetSpecAsync(Spec, "human");
                await engine.SetDescriptionAsync("measure:Sales/Total", "unsaved words", "human");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await RefusedAsync(() => Gate(engine).CreateModelAsync("Fresh", 1604, true));
                Assert.Contains("your unsaved changes to the open model and your unsaved spec (", (await OnlyPendingAsync(engine)).Summary);
            }
        }

        [Fact]
        public async Task A_spec_written_after_the_yes_is_a_new_question_and_the_old_yes_does_not_cover_it()
        {
            var (engine, _, _) = await OpenAsync("stale");
            using (engine)
            {
                await ApprovedLineAsync(engine, "reviewed");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var other = AssistantGateKit.WriteModel(Root, "stale-other");
                await RefusedAsync(() => Gate(engine).OpenAsync(other, false));
                await ApproveAsync(engine);
                await engine.SetSpecAsync(Spec, "human");   // the person writes a spec after the yes: it does not move the revision
                await RefusedAsync(() => Gate(engine).OpenAsync(other, false));
                var asked = (await engine.ListPendingApprovalsAsync()).Single(p => p.GrantedUtc == null);   // the earlier yes is spent on the earlier question
                Assert.Contains("your unsaved spec (", asked.Summary);   // asked again, about the spec as it is now
                Assert.Contains("and 1 line on your plan (", asked.Summary);
                Assert.Equal("PersonSpec", (await engine.GetSpecAsync()).Spec?.Name);
                Assert.Contains((await engine.GetPlanAsync()).Items, i => i.After == "reviewed");
            }
        }

        [Fact]
        public async Task A_spec_written_while_the_approved_open_loads_stops_the_swap_and_keeps_the_spec()
        {
            var (engine, sessions, path) = await OpenAsync("race");
            using (engine)
            {
                await ApprovedLineAsync(engine, "reviewed");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var other = AssistantGateKit.WriteModel(Root, "race-other");
                await RefusedAsync(() => Gate(engine).OpenAsync(other, false));
                await ApproveAsync(engine);
                // the swap has drained the old model and is about to replace it: the person's spec arrives now
                engine.BetweenInvalidateAndCommitForTest = () => engine.SetSpecAsync(Spec, "human").GetAwaiter().GetResult();
                var ex = await RefusedAsync(() => Gate(engine).OpenAsync(other, false));
                Assert.Contains("changed after you approved", ex.Message);
                Assert.Equal("PersonSpec", (await engine.GetSpecAsync()).Spec?.Name);
                Assert.Equal(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(sessions.Current.SourcePath));   // the old model is still the open one
                Assert.Contains((await engine.GetPlanAsync()).Items, i => i.After == "reviewed");
            }
        }

        // Join 7 (K4, the sol6-gate round 3 note b): an open that lost nothing when it was asked carries no approval to compare, so a spec that arrived while the model loaded
        // was dropped with no question. The swap now compares what is at stake with what the open saw when it started.
        [Theory]
        [InlineData("locked")]
        [InlineData("standard")]
        public async Task A_spec_written_while_an_open_that_lost_nothing_loads_stops_the_swap_and_keeps_the_spec(string preset)
        {
            var (engine, sessions, path) = await OpenAsync("late" + preset[..2]);
            using (engine)
            {
                AgentPolicyStore.SetPreset(preset, "human");
                var other = AssistantGateKit.WriteModel(Root, "late-other" + preset[..2]);
                engine.BetweenInvalidateAndCommitForTest = () => engine.SetSpecAsync(Spec, "human").GetAwaiter().GetResult();   // the person's spec arrives while the model loads
                var ex = await RefusedAsync(() => Gate(engine).OpenAsync(other, false));
                Assert.Contains("changed while the model was opening", ex.Message);
                Assert.Equal("PersonSpec", (await engine.GetSpecAsync()).Spec?.Name);
                Assert.Equal(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(sessions.Current.SourcePath));   // the old model is still the open one
                Assert.Empty(await engine.ListPendingApprovalsAsync());   // a refusal, not a new question
            }
        }

        [Fact]
        public async Task A_yes_for_the_spec_and_the_plan_opens_the_model_and_the_person_chose_that()
        {
            var (engine, sessions, _) = await OpenAsync("yes");
            using (engine)
            {
                await engine.SetSpecAsync(Spec, "human");
                await ApprovedLineAsync(engine, "reviewed");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var other = AssistantGateKit.WriteModel(Root, "yes-other");
                await RefusedAsync(() => Gate(engine).OpenAsync(other, false));
                await ApproveAsync(engine);
                await Gate(engine).OpenAsync(other, false);
                Assert.Equal(System.IO.Path.GetFullPath(other), System.IO.Path.GetFullPath(sessions.Current.SourcePath));
            }
        }

        [Fact]
        public async Task A_saved_model_with_no_spec_and_no_plan_loses_nothing_so_the_open_is_not_asked()
        {
            var (engine, sessions, _) = await OpenAsync("none");
            using (engine)
            {
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var other = AssistantGateKit.WriteModel(Root, "none-other");
                await Gate(engine).OpenAsync(other, false);
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                Assert.Equal(System.IO.Path.GetFullPath(other), System.IO.Path.GetFullPath(sessions.Current.SourcePath));
            }
        }

        [Theory]
        [InlineData("standard")]
        [InlineData("open")]
        public async Task Below_Locked_an_open_that_drops_the_spec_and_plan_asks_nothing_new(string preset)
        {
            var (engine, sessions, _) = await OpenAsync("below" + preset);
            using (engine)
            {
                await engine.SetSpecAsync(Spec, "human");
                await ApprovedLineAsync(engine, "reviewed");
                AgentPolicyStore.SetPreset(preset, "human");
                var other = AssistantGateKit.WriteModel(Root, "below-other-" + preset);
                await Gate(engine).OpenAsync(other, false);
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                Assert.Equal(System.IO.Path.GetFullPath(other), System.IO.Path.GetFullPath(sessions.Current.SourcePath));
            }
        }
    }
}
