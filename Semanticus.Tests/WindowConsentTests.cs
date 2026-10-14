using System.Threading.Tasks;
using System.Linq;
using Semanticus.Decisions;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>hero19 D11 (P13): the person's OK on the Pro box in this window also covers the live model the assistant opens next,
    /// so Ask does not say Allow a second time. Nothing wider: no OK in the window, a person's own open, or a file stay as they were.</summary>
    public sealed class WindowConsentTests
    {
        private static void LiveNow(DecisionFixture f) =>
            f.Sessions.Current.LiveOrigin = new LiveOrigin("localhost:2", "Northstar Live", null);

        [Fact]
        public async Task The_OK_in_this_window_carries_to_a_live_model_the_assistant_opens()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync();
            Assert.True((await f.ConsentAsync()).Current);                 // the person pressed OK for the model they had open
            LiveNow(f);                                                      // the assistant opens the live model: another model, another address
            Assert.False((await f.Engine.GetConsentAsync()).Current);        // before: Ask would say Allow
            await f.Engine.ExtendWindowConsentAsync("agent");
            var after = await f.Engine.GetConsentAsync();
            Assert.True(after.Current);                                      // after: it answers
            Assert.Equal("human", after.GrantedBy);                          // it is the person's OK, not the assistant's
        }

        [Fact]
        public async Task Without_an_OK_in_this_window_nothing_is_granted()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync();
            LiveNow(f);
            await f.Engine.ExtendWindowConsentAsync("agent");
            Assert.False((await f.Engine.GetConsentAsync()).Current);
        }

        [Fact]
        public async Task A_live_model_the_person_opens_and_an_assistants_own_yes_do_not_extend_it()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync();
            await f.ConsentAsync();
            LiveNow(f);
            await f.Engine.ExtendWindowConsentAsync("human");               // the person opened it: they see the card for it
            Assert.False((await f.Engine.GetConsentAsync()).Current);
        }

        [Fact]
        public async Task The_assistant_turning_it_on_for_one_model_is_not_a_window_OK()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync();
            await f.Engine.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "agent");
            LiveNow(f);
            await f.Engine.ExtendWindowConsentAsync("agent");
            Assert.False((await f.Engine.GetConsentAsync()).Current);
        }
        // ---- verifier probes (v-publish-gate): the carried OK must never overrule the person's own "no" ----
        [Fact]
        public async Task Probe_a_person_who_turned_it_off_in_this_window_is_not_turned_back_on_for_the_next_live_model()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync();
            await f.ConsentAsync();                                          // the person said OK for the file model
            await f.Engine.WithdrawConsentAsync("human");                    // then the person turned it off again, same window
            LiveNow(f);
            await f.Engine.ExtendWindowConsentAsync("agent");
            Assert.False((await f.Engine.GetConsentAsync()).Current);        // the last word in this window was no
        }

        [Fact]
        public async Task Probe_a_live_model_the_person_said_no_to_is_not_turned_on_by_an_OK_for_another_model()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync();
            LiveNow(f);
            await f.ConsentAsync();                                          // the live model: the person said OK...
            await f.Engine.WithdrawConsentAsync("human");                    // ...and then no, for THIS live model
            f.Sessions.Current.LiveOrigin = null;                            // back to the file model
            await f.ConsentAsync();                                          // the person says OK for the file model
            LiveNow(f);                                                      // the assistant opens the live model again
            await f.Engine.ExtendWindowConsentAsync("agent");
            Assert.False((await f.Engine.GetConsentAsync()).Current);        // the person's no for this model stands
        }
        [Fact]
        public async Task Probe_an_OK_in_one_window_never_reaches_another_window_or_a_restarted_engine()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync();
            await f.ConsentAsync();                                          // the person said OK in window one
            // A second window (or the same window after a restart) is another engine on the same machine and the same permission file.
            var sessions2 = new SessionManager();
            using var other = new LocalEngine(sessions2, TestEntitlements.Pro, f.Workspace);
            await other.CreateModelAsync("OtherWindow", 1604);
            await other.CreateTableAsync("Sales", "human");
            await other.SaveAsync(System.IO.Path.Combine(f.Workspace, "OtherWindow.bim"), "bim", false, "human", true);
            sessions2.Current.LiveOrigin = new LiveOrigin("localhost:3", "Westwind Live", null);   // the assistant opens a live model there
            Assert.False((await other.GetConsentAsync()).Current);
            await other.ExtendWindowConsentAsync("agent");
            Assert.False((await other.GetConsentAsync()).Current);           // window one's OK does not carry
        }
    }
}
