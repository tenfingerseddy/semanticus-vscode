using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// C1.5 / D-001 / D-002: a live preview that only counted adds and edits used to say the live
    /// model already matched, and nothing could remove the leftover object. The preview must name
    /// live-only objects, absence still never deletes, and a ticked ref is the only delete.
    /// </summary>
    [Collection("restore-root")]
    public sealed class LiveMatchDeleteTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private const string ExtraName = "UAT Added 20260909T0742Z";

        public LiveMatchDeleteTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-live-match-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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

        private static async Task<(LocalEngine engine, RecordedLiveTarget target, string path, string extraRef)> OpenWithLiveOnlyMeasureAsync()
        {
            var target = RecordedLiveTarget.FiveTable();
            var path = target.WriteBim(target.Live);
            var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.OpenAsync(path);
            target.Attach(engine);
            target.Live.Model.Tables["Sales"].Measures.Add(new TOM.Measure
            {
                Name = ExtraName,
                Expression = "1",
                LineageTag = "tag-uat-added"
            });
            return (engine, target, path, "measure:Sales/" + ExtraName);
        }

        [Fact]
        public void Already_matches_copy_is_only_for_an_empty_diff_both_ways()
        {
            var empty = LiveMatchCopy.ForPreview("V2", 0, Array.Empty<string>());
            Assert.Equal("Nothing to publish. V2 already has everything in your copy.", empty);
            Assert.DoesNotContain("already matches the session", empty, StringComparison.OrdinalIgnoreCase);

            var extra = LiveMatchCopy.ForPreview("V2", 0, new[] { "measure:Sales/" + ExtraName });
            Assert.StartsWith("Nothing new to publish.", extra);
            Assert.Contains(ExtraName, extra);
            Assert.Contains("unless you tick it", extra);
            Assert.DoesNotContain("already matches", extra, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\u2014", extra, StringComparison.Ordinal);

            Assert.Null(LiveMatchCopy.ForPreview("V2", 2, new[] { "measure:Sales/" + ExtraName }));
        }

        [Fact]
        public async Task Preview_names_a_live_only_measure_and_does_not_claim_a_match()
        {
            var (engine, target, path, extraRef) = await OpenWithLiveOnlyMeasureAsync();
            using (engine)
            {
                try
                {
                    var preview = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: false, origin: "human");
                    Assert.False(preview.Committed);
                    Assert.Equal(0, preview.TotalChanges);
                    Assert.Contains(extraRef, preview.LiveOnly);
                    Assert.StartsWith("Nothing new to publish.", preview.MatchNote);
                    Assert.Contains(ExtraName, preview.MatchNote);
                    Assert.DoesNotContain("already matches", preview.MatchNote, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(0, target.SaveChangesCount);
                    Assert.NotNull(target.Live.Model.Tables["Sales"].Measures.Find(ExtraName));
                }
                finally { File.Delete(path); }
            }
        }

        [Fact]
        public async Task Absence_still_never_deletes_without_a_ticked_ref()
        {
            var (engine, target, path, extraRef) = await OpenWithLiveOnlyMeasureAsync();
            using (engine)
            {
                try
                {
                    var preview = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: false, origin: "human");
                    var rep = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: true, origin: "human",
                        overrideReason: "recorded live double", confirmToken: preview.ConfirmToken);
                    Assert.DoesNotContain(extraRef, rep.DeletedRefs ?? Array.Empty<string>());
                    Assert.NotNull(target.Live.Model.Tables["Sales"].Measures.Find(ExtraName));
                }
                finally { File.Delete(path); }
            }
        }

        [Fact]
        public async Task Ticked_live_only_measure_is_removed_on_commit()
        {
            var (engine, target, path, extraRef) = await OpenWithLiveOnlyMeasureAsync();
            using (engine)
            {
                try
                {
                    var preview = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: false, origin: "human");
                    Assert.Contains(extraRef, preview.LiveOnly);

                    var reviewed = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: false, origin: "human",
                        deleteRefs: new[] { extraRef });
                    Assert.False(reviewed.Committed);
                    Assert.True(reviewed.Deleted >= 1, reviewed.Error);
                    Assert.Contains(extraRef, reviewed.DeletedRefs);
                    Assert.NotNull(target.Live.Model.Tables["Sales"].Measures.Find(ExtraName));
                    Assert.NotEqual(preview.ConfirmToken, reviewed.ConfirmToken);

                    var stale = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        engine.DeployLiveAsync(
                            RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                            "azcli", null, null, commit: true, origin: "human",
                            overrideReason: "recorded live double",
                            confirmToken: preview.ConfirmToken, deleteRefs: new[] { extraRef }));
                    Assert.Contains("changed since you reviewed", stale.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.NotNull(target.Live.Model.Tables["Sales"].Measures.Find(ExtraName));

                    var rep = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: true, origin: "human",
                        overrideReason: "recorded live double",
                        confirmToken: reviewed.ConfirmToken, deleteRefs: new[] { extraRef });
                    Assert.True(rep.Committed, rep.Error);
                    Assert.Contains(extraRef, rep.DeletedRefs);
                    Assert.Null(target.Live.Model.Tables["Sales"].Measures.Find(ExtraName));
                    Assert.True(target.SaveChangesCount >= 1);
                }
                finally { File.Delete(path); }
            }
        }

        [Fact]
        public async Task Mcp_door_preview_accepts_ticked_deletes_and_the_token_commits()
        {
            var (engine, target, path, extraRef) = await OpenWithLiveOnlyMeasureAsync();
            using (engine)
            {
                try
                {
                    var preview = await McpTools.DeployLive(engine,
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, commit: false, deleteRefs: new[] { extraRef });
                    Assert.False(preview.Committed);
                    Assert.Contains(extraRef, preview.LiveOnly);
                    Assert.Contains(extraRef, preview.DeletedRefs);
                    Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));
                    Assert.Equal(0, target.SaveChangesCount);

                    var rep = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: true, origin: "human",
                        overrideReason: "recorded live double",
                        confirmToken: preview.ConfirmToken, deleteRefs: new[] { extraRef });
                    Assert.True(rep.Committed, rep.Error);
                    Assert.Null(target.Live.Model.Tables["Sales"].Measures.Find(ExtraName));
                }
                finally { File.Delete(path); }
            }
        }
    }
}
