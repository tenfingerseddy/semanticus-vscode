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
    /// <summary>
    /// Kane, 13 Oct: a live publish read the live model twice (preview, then commit) and took minutes with the engine idle, and the page said
    /// only "deploy live". The preview now keeps its connection for the commit, one cheap check guards the write, every step is timed, and the
    /// page is told each step in plain words. Run against the recorded live double: no real endpoint.
    /// </summary>
    [Collection("restore-root")]
    public sealed class DeploySpeedTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public DeploySpeedTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-deploy-speed-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private sealed class Rig : IDisposable
        {
            public LocalEngine Engine;
            public SessionManager Sessions;
            public RecordedLiveTarget Target;
            public string Path;
            public readonly List<OperationProgress> Events = new List<OperationProgress>();
            public void Dispose() { Engine.Dispose(); try { File.Delete(Path); } catch { } }

            public Task<DeployReport> Preview() =>
                Engine.DeployLiveAsync(RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, "azcli", null, null, commit: false, origin: "human");
            public Task<DeployReport> Commit(string token) =>
                Engine.DeployLiveAsync(RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, "azcli", null, null, commit: true, origin: "human", confirmToken: token);
        }

        private static async Task<Rig> OpenRigAsync()
        {
            var rig = new Rig { Target = RecordedLiveTarget.FiveTable(), Sessions = new SessionManager() };
            rig.Path = rig.Target.WriteBim(rig.Target.Live);
            rig.Engine = new LocalEngine(rig.Sessions, new Pro());
            await rig.Engine.OpenAsync(rig.Path);
            rig.Target.AttachHeld(rig.Engine);
            rig.Sessions.Bus.Progress += rig.Events.Add;
            await rig.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "The sum of the sales amount, in the shop's own currency.", "human");
            return rig;
        }

        private static async Task Until(Func<bool> done)
        {
            for (var i = 0; i < 200 && !done(); i++) await Task.Delay(25);
            Assert.True(done(), "the closing of the connection did not happen in time");
        }

        [Fact]
        public async Task Preview_then_commit_reads_the_live_model_once_and_checks_once()
        {
            using var rig = await OpenRigAsync();
            var preview = await rig.Preview();
            Assert.True(string.IsNullOrEmpty(preview.Error), preview.Error);
            Assert.Equal(1, rig.Target.FullReads);

            var commit = await rig.Commit(preview.ConfirmToken);
            Assert.True(commit.Committed, commit.Error);
            Assert.Equal(1, rig.Target.FullReads);       // the commit did not read the live model again
            Assert.Equal(1, rig.Target.VersionChecks);   // it asked one cheap question instead
            Assert.Equal(1, rig.Target.SaveChangesCount);
            Assert.Equal("The sum of the sales amount, in the shop's own currency.",
                rig.Target.Live.Model.Tables["Sales"].Measures["Total Sales"].Description);
            await Until(() => rig.Target.Closed == 1);   // the connection is closed after the commit, once
            Assert.False(rig.Engine.HasHeldLive);
        }

        [Fact]
        public async Task A_live_change_between_preview_and_commit_reads_again_and_writes_nothing_blind()
        {
            using var rig = await OpenRigAsync();
            var preview = await rig.Preview();
            rig.Target.ChangeUnderPreview(m => m.Tables["Sales"].Measures["Total Sales"].Description = "Edited by a colleague while you looked.");

            // The held read is stale, so it is dropped and the live model is read again. The new read shows a different before value,
            // so the review token no longer matches and nothing is written.
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Commit(preview.ConfirmToken));
            Assert.Equal(2, rig.Target.FullReads);
            Assert.Equal(1, rig.Target.VersionChecks);
            Assert.Equal(0, rig.Target.SaveChangesCount);
            Assert.Equal("Edited by a colleague while you looked.",
                rig.Target.Live.Model.Tables["Sales"].Measures["Total Sales"].Description);
            await Until(() => rig.Target.Closed == 2);

            // Review again and the publish goes through, from the fresh read.
            var again = await rig.Preview();
            var commit = await rig.Commit(again.ConfirmToken);
            Assert.True(commit.Committed, commit.Error);
            Assert.Equal(3, rig.Target.FullReads);   // the new preview read once; its commit reused it
        }

        [Fact]
        public async Task A_new_preview_closes_the_held_connection_before_it_opens_its_own()
        {
            using var rig = await OpenRigAsync();
            await rig.Preview();
            await rig.Preview();
            Assert.Equal(2, rig.Target.FullReads);
            await Until(() => rig.Target.Closed == 1);
            Assert.True(rig.Engine.HasHeldLive);
        }

        [Fact]
        public async Task A_held_connection_closes_after_it_sits_idle_and_the_commit_then_reads_again()
        {
            var was = LocalEngine.HeldLiveIdle;
            LocalEngine.HeldLiveIdle = TimeSpan.FromMilliseconds(100);
            try
            {
                using var rig = await OpenRigAsync();
                var preview = await rig.Preview();
                await Until(() => !rig.Engine.HasHeldLive);
                await Until(() => rig.Target.Closed == 1);

                var commit = await rig.Commit(preview.ConfirmToken);
                Assert.True(commit.Committed, commit.Error);   // correct, just slower
                Assert.Equal(2, rig.Target.FullReads);
                Assert.Equal(0, rig.Target.VersionChecks);
            }
            finally { LocalEngine.HeldLiveIdle = was; }
        }

        [Fact]
        public async Task Each_step_reports_its_time_on_the_result()
        {
            using var rig = await OpenRigAsync();
            var preview = await rig.Preview();
            Assert.Equal(new[] { "connect", "read", "compare" }, preview.Steps.Select(s => s.Step).ToArray());
            Assert.All(preview.Steps, s => { Assert.False(string.IsNullOrWhiteSpace(s.Words)); Assert.True(s.Ms >= 0); });
            Assert.StartsWith("Took ", preview.Timing);
            Assert.Contains("reading the live model", preview.Timing);

            var commit = await rig.Commit(preview.ConfirmToken);
            Assert.True(commit.Committed, commit.Error);
            Assert.Contains("check", commit.Steps.Select(s => s.Step));
            Assert.Contains("send", commit.Steps.Select(s => s.Step));
            Assert.DoesNotContain("read", commit.Steps.Select(s => s.Step));   // reused: no second read to time
            Assert.DoesNotContain("—", commit.Timing, StringComparison.Ordinal);
        }

        [Fact]
        public async Task The_page_is_told_each_step_in_plain_words_in_order_and_then_told_it_is_over()
        {
            using var rig = await OpenRigAsync();
            var preview = await rig.Preview();
            var first = rig.Events.Where(e => e.OpKey == "deploy_live").ToList();
            Assert.Equal(new[] { "Connecting to the service", "Reading the live model", "Comparing with your copy", null }, first.Select(e => e.Note).ToArray());
            Assert.Single(first.Select(e => e.RunId).Distinct());
            Assert.Equal(first.Select(e => e.Done).OrderBy(d => d), first.Select(e => e.Done));   // never backwards

            rig.Events.Clear();
            await rig.Commit(preview.ConfirmToken);
            var second = rig.Events.Where(e => e.OpKey == "deploy_live").Select(e => e.Note).ToArray();
            // The commit compares twice (the held read again for the review token, then for the write), so the same words may repeat.
            var said = second.Where(n => n != null).Distinct().ToArray();
            Assert.Equal(3, said.Length);
            Assert.Equal("Checking the live model has not changed", said[0]);
            Assert.Equal("Comparing with your copy", said[1]);
            Assert.StartsWith("Sending ", said[2]);
            Assert.Null(second[^1]);
            Assert.All(second.Where(n => n != null), n => Assert.DoesNotContain("—", n, StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_token_with_under_two_minutes_left_is_not_reused_the_commit_reads_and_signs_in_again()
        {
            using var rig = await OpenRigAsync();
            rig.Target.TokenExpires = DateTimeOffset.UtcNow.AddSeconds(90);
            var preview = await rig.Preview();
            var commit = await rig.Commit(preview.ConfirmToken);
            Assert.True(commit.Committed, commit.Error);       // correct, just slower
            Assert.Equal(2, rig.Target.FullReads);              // the held read was dropped, not trusted
            Assert.Equal(0, rig.Target.VersionChecks);          // and not even asked
            await Until(() => rig.Target.Closed == 2);
        }

        [Fact]
        public async Task Replacing_the_session_closes_the_held_connection_at_once_not_after_the_idle_timer()
        {
            using var rig = await OpenRigAsync();
            await rig.Preview();
            Assert.True(rig.Engine.HasHeldLive);
            var other = rig.Target.WriteBim(rig.Target.Live);
            try { await rig.Engine.OpenAsync(other); }
            finally { try { File.Delete(other); } catch { } }
            Assert.False(rig.Engine.HasHeldLive);
            await Until(() => rig.Target.Closed == 1);
        }

        private static readonly DateTime T0 = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        private static AmoLiveHandle.LiveStamp Stamp(long version = 7, int lastUpdateMin = 0, int modifiedMin = 0) =>
            new AmoLiveHandle.LiveStamp(T0.AddMinutes(modifiedMin), T0, version, T0.AddMinutes(lastUpdateMin));

        [Fact]
        public void The_real_check_says_current_only_when_every_part_of_the_stamp_is_the_same()
        {
            var read = Stamp();
            Assert.True(AmoLiveHandle.ForTest(read, () => Stamp(), DateTimeOffset.MaxValue).IsCurrent());
        }

        [Fact]
        public void The_real_check_reads_again_when_the_version_moved_though_the_model_times_did_not()
        {
            // Another writer edited one measure: the model row's two times stayed, the database version moved.
            var h = AmoLiveHandle.ForTest(Stamp(version: 7), () => Stamp(version: 8), DateTimeOffset.MaxValue);
            Assert.False(h.IsCurrent());
        }

        [Fact]
        public void The_real_check_reads_again_when_only_the_database_last_update_moved()
        {
            var h = AmoLiveHandle.ForTest(Stamp(), () => Stamp(lastUpdateMin: 3), DateTimeOffset.MaxValue);
            Assert.False(h.IsCurrent());
        }

        [Fact]
        public void The_real_check_reads_again_when_the_model_time_moved_or_the_question_fails()
        {
            Assert.False(AmoLiveHandle.ForTest(Stamp(), () => Stamp(modifiedMin: 1), DateTimeOffset.MaxValue).IsCurrent());
            Assert.False(AmoLiveHandle.ForTest(Stamp(), () => throw new InvalidOperationException("no answer"), DateTimeOffset.MaxValue).IsCurrent());
        }

        // D4 (hero19): the real question threw every time because AMO's ExecuteReader takes XMLA, and the text was bare SQL.
        // Tabular Editor sends <Statement>...</Statement> (TOMWrapper Database.ExecuteReader). The recorded reply below has the
        // two columns the live service returns for this rowset; the live run is still needed once (the Inspiron has no endpoint).
        [Fact]
        public void The_model_time_question_is_sent_as_an_XMLA_statement_the_way_Tabular_Editor_sends_it()
        {
            var el = System.Xml.Linq.XElement.Parse(AmoLiveHandle.ModelTimesStatement);
            Assert.Equal("Statement", el.Name.LocalName);
            Assert.Contains("$SYSTEM.TMSCHEMA_MODEL", el.Value);
            Assert.Contains("[ModifiedTime]", el.Value);
            Assert.Contains("[StructureModifiedTime]", el.Value);
        }

        [Fact]
        public void The_model_time_reply_is_read_from_a_recorded_row_and_a_missing_row_is_named()
        {
            var t = new System.Data.DataTable();
            t.Columns.Add("ModifiedTime", typeof(DateTime)); t.Columns.Add("StructureModifiedTime", typeof(DateTime));
            t.Rows.Add(T0.AddMinutes(5), T0);
            var (modified, structure) = AmoLiveHandle.ReadModelTimes(t.CreateDataReader());
            Assert.Equal(T0.AddMinutes(5), modified);
            Assert.Equal(T0, structure);
            t.Rows.Clear();
            var ex = Assert.Throws<InvalidOperationException>(() => AmoLiveHandle.ReadModelTimes(t.CreateDataReader()));
            Assert.Contains("no model row", ex.Message);
        }
    }
}
