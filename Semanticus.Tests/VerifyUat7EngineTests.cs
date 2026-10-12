using System;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier, lane m11/uat7-engine, item 2 (every held question names the model, the object and the new value). Two rows of the registry
    /// write their words with a choice template ("{add?add|remove}", "{include?add|take out}") that <see cref="GateWords.Fill"/> has never
    /// understood, so under Locked the person reads the braces as they are and cannot tell an add from a remove. For a role this is who may
    /// read the model's rows. The lane's registry-wide sweep passed both rows because the member's name alone counts as "an object".
    /// These tests ask through the assistant's door, as the person meets the question, and fail until the question says which way it goes.
    /// </summary>
    [Collection("restore-root")]
    public sealed class VerifyUat7EngineTests : GateFixtureBase
    {
        public VerifyUat7EngineTests(RestoreRootFixture fixture) : base(fixture) { }

        [Theory]
        [InlineData(true, "add")]
        [InlineData(false, "remove")]
        public async Task A_role_member_question_says_whether_the_member_is_added_or_removed(bool add, string verb)
        {
            var (engine, _, _) = await OpenAsync("rolemember-" + verb);
            using (engine)
            {
                await engine.CreateRoleAsync("Readers", "read", "human");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await Assert.ThrowsAnyAsync<Exception>(() => Gate(engine).SetRoleMemberAsync("Readers", "reader@example.com", add, "agent"));
                var asked = (await OnlyPendingAsync(engine)).Summary;
                Assert.DoesNotContain("{", asked);
                Assert.StartsWith("Your assistant wants to " + verb + " reader@example.com", asked);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(true, "add")]
        [InlineData(false, "take out")]
        public async Task A_perspective_member_question_says_whether_the_object_is_added_or_taken_out(bool include, string verb)
        {
            var (engine, _, _) = await OpenAsync("perspmember-" + (include ? "in" : "out"));
            using (engine)
            {
                var perspective = await engine.CreatePerspectiveAsync("Short", "human");
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                await Assert.ThrowsAnyAsync<Exception>(() => Gate(engine).SetPerspectiveMemberAsync(perspective, "measure:Sales/Total", include, "agent"));
                var asked = (await OnlyPendingAsync(engine)).Summary;
                Assert.DoesNotContain("{", asked);
                Assert.StartsWith("Your assistant wants to " + verb + " [Total]", asked);
            }
        }
    }
}
