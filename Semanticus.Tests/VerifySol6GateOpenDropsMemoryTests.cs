using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Verifier for m11/sol6-gate, round 2. The lane holds clearing the person's spec or change plan under Locked (ClearSpecAsync, ClearPlanAsync,
    /// SetSpecAsync and the rest ask when there is content, because "the spec is not in the model, so Undo does not bring it back"). The sweep then leaves
    /// session memory out of the trace for the opens, on the claim that "the opens are held when there is unsaved work to throw away". But the open rows
    /// count only the MODEL's unsaved edits (GateRegistry: GateHasUnsavedAsync; RefuseUnheldDiscard; RefuseStaleDiscard): an open or a new model on a saved
    /// model passes with no question under Locked and replaces the session, so the person's spec and every plan line they approved are gone, the same loss
    /// clear_spec and clear_plan ask about. Each test must ask (or refuse) and keep the person's spec and plan.</summary>
    [Collection("restore-root")]
    public sealed class VerifySol6GateOpenDropsMemoryTests : GateFixtureBase
    {
        public VerifySol6GateOpenDropsMemoryTests(RestoreRootFixture fixture) : base(fixture) { }

        private const string Spec = "{\"name\":\"PersonSpec\",\"tables\":[],\"relationships\":[],\"measures\":[],\"timeIntelligence\":[]}";

        /// <summary>A saved model (no unsaved edits) with the person's spec and one plan line the person approved, both in the session's memory.</summary>
        private async Task<(LocalEngine engine, SessionManager sessions)> PersonWorkAsync(string tag)
        {
            var (engine, sessions, _) = await OpenAsync(tag);
            await engine.SetSpecAsync(Spec, "human");
            var line = (await engine.AddPlanItemAsync("measure:Sales/Total", "set_description", "the person's reviewed words", null, null, null, "human")).Items.Single().Id;
            await engine.SetPlanItemAsync(line, null, true, "human");
            Assert.False(sessions.Current.HasUnsavedEdits);   // plan and spec live beside the model: nothing in the model is unsaved
            AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
            return (engine, sessions);
        }

        private static async Task KeptAsync(LocalEngine engine)
        {
            Assert.Equal("PersonSpec", (await engine.GetSpecAsync()).Spec?.Name);
            Assert.Contains((await engine.GetPlanAsync()).Items, i => i.After == "the person's reviewed words");
        }

        [Fact]
        public async Task Open_model_under_Locked_does_not_throw_away_the_persons_spec_and_approved_plan_with_no_question()
        {
            var (engine, _) = await PersonWorkAsync("om");
            using (engine)
            {
                var other = AssistantGateKit.WriteModel(Root, "om-other");
                try { await Gate(engine).OpenAsync(other, true); } catch (InvalidOperationException) { }
                await KeptAsync(engine);
                Assert.Equal("PersonSpec", (await engine.GetSpecAsync()).Spec?.Name);   // the assertion the hygiene rule wants in the test's own body
            }
        }

        [Fact]
        public async Task Create_model_under_Locked_does_not_throw_away_the_persons_spec_and_approved_plan_with_no_question()
        {
            var (engine, _) = await PersonWorkAsync("cm");
            using (engine)
            {
                try { await Gate(engine).CreateModelAsync("Fresh", 1604, true); } catch (InvalidOperationException) { }
                await KeptAsync(engine);
                Assert.Equal("PersonSpec", (await engine.GetSpecAsync()).Spec?.Name);   // the assertion the hygiene rule wants in the test's own body
            }
        }

        [Fact]
        public async Task Open_model_under_Locked_without_discard_does_not_throw_away_the_persons_spec_and_approved_plan_with_no_question()
        {
            var (engine, _) = await PersonWorkAsync("on");
            using (engine)
            {
                var other = AssistantGateKit.WriteModel(Root, "on-other");
                try { await Gate(engine).OpenAsync(other, false); } catch (InvalidOperationException) { }
                await KeptAsync(engine);
                Assert.Equal("PersonSpec", (await engine.GetSpecAsync()).Spec?.Name);   // the assertion the hygiene rule wants in the test's own body
            }
        }
    }
}
