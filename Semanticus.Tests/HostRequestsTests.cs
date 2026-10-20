// The personality settings and the trial start get an agent door (1.5.1, F-292 and F-293). The window owns both, so the engine
// holds the last settings it reported and asks the window by notification. The promises tested here: the assistant can read
// and set the three settings through one engine path, the window is told, the report is the person's alone, and a trial
// is never started without the person's yes in chat.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public class HostRequestsTests
    {
        [Fact]
        public async Task The_assistant_reads_the_defaults_until_a_window_reports()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var v = await McpTools.GetPersonalitySettings(f.Engine);
            Assert.False(v.Reported);
            Assert.Equal("on", v.Personality); Assert.False(v.Sounds); Assert.Equal(40, v.SoundVolume);

            await f.Engine.ReportHostSettingsAsync("quiet", true, 70);
            v = await McpTools.GetPersonalitySettings(f.Engine);
            Assert.True(v.Reported);
            Assert.Equal("quiet", v.Personality); Assert.True(v.Sounds); Assert.Equal(70, v.SoundVolume);
        }

        [Fact]
        public async Task Setting_one_value_keeps_the_others_and_tells_the_window()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            f.Sessions.Bus.HumanClientAttached = () => true;
            var told = new List<HostSettingsView>();
            f.Sessions.Bus.HostSettings += told.Add;
            await f.Engine.ReportHostSettingsAsync("on", true, 55);

            var r = await McpTools.SetPersonalitySettings(f.Engine, personality: "quiet");
            Assert.True(r.Applied);
            Assert.Equal("quiet", r.Personality); Assert.True(r.Sounds); Assert.Equal(55, r.SoundVolume);
            var sent = Assert.Single(told);
            Assert.Equal("quiet", sent.Personality); Assert.Equal(55, sent.SoundVolume);
            Assert.Equal("quiet", (await McpTools.GetPersonalitySettings(f.Engine)).Personality);
        }

        [Fact]
        public async Task With_no_window_the_value_is_kept_and_the_answer_says_it_was_not_written()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            f.Sessions.Bus.HumanClientAttached = () => false;
            var told = 0;
            f.Sessions.Bus.HostSettings += _ => told++;
            var r = await McpTools.SetPersonalitySettings(f.Engine, sounds: true, soundVolume: 20);
            Assert.False(r.Applied);
            Assert.Equal(0, told);
            Assert.Contains("No VS Code window", r.Note);
            Assert.Equal(20, (await McpTools.GetPersonalitySettings(f.Engine)).SoundVolume);
        }

        [Theory]
        [InlineData("loud", null, null)]
        [InlineData(null, null, 101)]
        [InlineData(null, null, -1)]
        [InlineData(null, null, null)]
        public async Task A_bad_or_empty_request_is_refused_with_a_plain_sentence(string personality, bool? sounds, int? volume)
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.SetPersonalitySettings(f.Engine, personality, sounds, volume));
            Assert.DoesNotContain("—", ex.Message);
        }

        [Fact]
        public void The_window_report_is_the_persons_and_an_agent_connection_has_no_such_method()
        {
            Assert.Null(typeof(EngineRpcTarget).GetMethod("reportHostSettings", BindingFlags.Public | BindingFlags.Instance));
            Assert.NotNull(typeof(HumanGovernanceRpcTarget).GetMethod("reportHostSettings", BindingFlags.Public | BindingFlags.Instance));
            // both doors read and set through the same engine path
            Assert.NotNull(typeof(EngineRpcTarget).GetMethod("getHostSettings"));
            Assert.NotNull(typeof(EngineRpcTarget).GetMethod("setHostSettings"));
            Assert.NotNull(typeof(EngineRpcTarget).GetMethod("startTrial"));
        }

        [Fact]
        public async Task A_trial_is_not_started_without_the_persons_yes_in_chat()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            f.Sessions.Bus.HumanClientAttached = () => true;
            var asked = new List<string>();
            f.Sessions.Bus.HostStartTrial += asked.Add;
            var r = await McpTools.StartTrial(f.Engine, "ana@contoso.com");
            Assert.Equal("needsYes", r.State);
            Assert.Contains("ana@contoso.com", r.Message);
            Assert.Empty(asked);
        }

        [Fact]
        public async Task With_the_persons_yes_the_window_is_asked_to_send_the_email()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            f.Sessions.Bus.HumanClientAttached = () => true;
            var asked = new List<string>();
            f.Sessions.Bus.HostStartTrial += asked.Add;
            var r = await McpTools.StartTrial(f.Engine, "  ana@contoso.com ", personSaidYes: true);
            Assert.Equal("requested", r.State);
            Assert.Equal("ana@contoso.com", Assert.Single(asked));
        }

        [Fact]
        public async Task A_trial_with_no_window_a_bad_address_or_pro_already_on_sends_nothing()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var asked = new List<string>();
            f.Sessions.Bus.HostStartTrial += asked.Add;
            f.Sessions.Bus.HumanClientAttached = () => false;
            Assert.Equal("noWindow", (await McpTools.StartTrial(f.Engine, "ana@contoso.com", true)).State);
            f.Sessions.Bus.HumanClientAttached = () => true;
            Assert.Equal("badEmail", (await McpTools.StartTrial(f.Engine, "ana", true)).State);
            Assert.Empty(asked);

            using var pro = new DecisionFixture(TestEntitlements.Pro);
            pro.Sessions.Bus.HumanClientAttached = () => true;
            pro.Sessions.Bus.HostStartTrial += asked.Add;
            Assert.Equal("alreadyPro", (await McpTools.StartTrial(pro.Engine, "ana@contoso.com", true)).State);
            Assert.Empty(asked);
        }
    }
}
