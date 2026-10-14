using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>hero19 D7: while the assistant waits on the person about a red check, the Studio says so on Publish. The engine sends one
    /// activity event with the line when the assistant is refused, and a clearing event once a publish gets past the check.</summary>
    [Collection("restore-root")]
    public sealed class PublishAskTests : IDisposable
    {
        private const string LocalEndpoint = "localhost:2";
        private readonly string _root;
        private readonly string _safeRoot;

        public PublishAskTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-pubask-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            AgentPolicyStore.RootOverride = _root; ApprovalLedger.RootOverride = _root; ConnectionRegistry.RootOverride = _root;
        }
        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot; ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; } = true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        // Hero 20 D14: the gate names a rule the way the rule file does ("[DAX Expressions] " category, DAX brackets round the measure). The
        // line the person reads says the rule in plain words and names the thing it is about.
        [Theory]
        [InlineData("[DAX Expressions] Avoid using the IFERROR function ([Return Rate])", "Avoid using the IFERROR function on Return Rate")]
        [InlineData("[Performance] Remove unused columns ([Sales Amount] and 4 more)", "Remove unused columns on Sales Amount and 4 more")]
        [InlineData("Hide foreign key columns ('Date')", "Hide foreign key columns on Date")]
        [InlineData("Add a description", "Add a description")]
        public void The_line_says_the_rule_in_plain_words(string named, string plain)
        {
            var blocker = "2 errors to fix before this model can ship. Start with: " + named + "; Another rule (X). Fix each one, or record a waiver saying why you accept it.";
            var line = LocalEngine.PublishAskWords(new[] { blocker }, 2);
            Assert.Equal("Your assistant is asking you about 2 red checks: " + plain, line);
            Assert.DoesNotContain("[", line);
        }

        [Fact]
        public void The_line_counts_the_red_checks_and_names_the_first()
        {
            var blocker = "1 error to fix before this model can ship. Start with: Avoid IFERROR (Return Rate). Fix each one, or record a waiver saying why you accept it.";
            Assert.Equal("Your assistant is asking you about 1 red check: Avoid IFERROR on Return Rate", LocalEngine.PublishAskWords(new[] { blocker }, 1));
            Assert.Equal("Your assistant is asking you about 3 red checks: Avoid IFERROR on Return Rate", LocalEngine.PublishAskWords(new[] { blocker }, 3));
            // A blocker with no names still gives the count, and never an em dash.
            var plain = LocalEngine.PublishAskWords(new[] { "some other blocker" }, 0);
            Assert.Equal("Your assistant is asking you about 1 red check", plain);
            Assert.DoesNotContain("—", plain);
        }

        [Fact]
        public async Task A_refused_assistant_publish_raises_the_line_and_the_persons_yes_clears_it()
        {
            var sessions = new SessionManager();
            var events = new List<ActivityEvent>();
            sessions.Bus.Activity += e => { if (e.Kind == "publish_asking") events.Add(e); };
            using var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("Blocked", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateMeasureAsync(t, "Total Sales", "1", "human");   // visible, undescribed: the deploy gate is RED
            var token = await ReviewFenceTest.TokenAsync(engine, LocalEndpoint, "Blocked", "agent");

            await Assert.ThrowsAnyAsync<Exception>(() => engine.DeployLiveAsync(LocalEndpoint, "Blocked", "serviceprincipal", null, null,
                commit: true, origin: "agent", overrideReason: null, confirmToken: token));
            var asked = Assert.Single(events);
            Assert.True(asked.Ok);
            Assert.StartsWith("Your assistant is asking you about ", asked.Label);

            // The person says yes in chat: the assistant publishes again with their words. The line goes.
            await Assert.ThrowsAnyAsync<Exception>(() => engine.DeployLiveAsync(LocalEndpoint, "Blocked", "serviceprincipal", null, null,
                commit: true, origin: "agent", overrideReason: "yes, publish it", confirmToken: token));
            Assert.Equal(2, events.Count);
            Assert.False(events[1].Ok);
        }

        private static async Task<(LocalEngine Engine, List<ActivityEvent> Events, string Measure)> AskedAsync()
        {
            var sessions = new SessionManager();
            var events = new List<ActivityEvent>();
            sessions.Bus.Activity += e => { if (e.Kind == "publish_asking") lock (events) events.Add(e); };
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("Blocked", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            var m = await engine.CreateMeasureAsync(t, "Total Sales", "1", "human");
            var token = await ReviewFenceTest.TokenAsync(engine, LocalEndpoint, "Blocked", "agent");
            await Assert.ThrowsAnyAsync<Exception>(() => engine.DeployLiveAsync(LocalEndpoint, "Blocked", "serviceprincipal", null, null,
                commit: true, origin: "agent", overrideReason: null, confirmToken: token));
            return (engine, events, m);
        }

        private static async Task<bool> WaitForAsync(Func<bool> done)
        {
            for (var i = 0; i < 100 && !done(); i++) await Task.Delay(50);
            return done();
        }

        [Fact]
        public async Task The_line_goes_when_the_assistant_works_again_because_the_person_said_no()
        {
            var (engine, events, m) = await AskedAsync();
            using (engine)
            {
                Assert.Single(events);
                await engine.SetDescriptionAsync(m, "Total sales in reporting currency.", "agent");   // moved on after the person's no
                Assert.True(await WaitForAsync(() => { lock (events) return events.Count == 2; }));
                Assert.False(events[1].Ok);
            }
        }

        [Fact]
        public async Task The_line_stays_when_the_person_edits_but_the_check_is_still_red()
        {
            var (engine, events, m) = await AskedAsync();
            using (engine)
            {
                await engine.SetMeasureFormatAsync(m, "#,0", "human");   // an edit, but the description and the date table are still missing
                await Task.Delay(1500);
                lock (events) Assert.Single(events);
            }
        }

        [Fact]
        public async Task The_line_goes_when_the_person_fixes_the_check()
        {
            var (engine, events, m) = await AskedAsync();
            using (engine)
            {
                await engine.SetDescriptionAsync(m, "The total sales amount in reporting currency.", "human");
                await engine.SetMeasureFormatAsync(m, "#,0", "human");
                var dt = await engine.CreateTableAsync("Date", "human");
                var dkey = await engine.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
                await engine.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
                await engine.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
                await engine.MarkDateTableAsync(dt, "Date", "human");
                Assert.True(await WaitForAsync(() => { lock (events) return events.Count >= 2; }));
                Assert.False(events[^1].Ok);
            }
        }
    }
}
