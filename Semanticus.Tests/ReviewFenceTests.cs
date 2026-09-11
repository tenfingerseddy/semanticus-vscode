using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// C1.2: a live commit, a file merge, and a dry-run of a publish-link write are all fenced to the
    /// preview that was reviewed. Failures here are D-006, D-112, and D-116.
    /// </summary>
    [Collection("restore-root")]
    public sealed class ReviewFenceTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public ReviewFenceTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-review-fence-" + Guid.NewGuid().ToString("N").Substring(0, 8));
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

        private sealed class Free : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "free" };
        }

        [Fact]
        public void Review_token_covers_session_target_and_change_set()
        {
            var a = ReviewFence.Mint("s1", 1, "t1", new[] { "measure:Sales/A" });
            Assert.StartsWith("REVIEW-", a);
            Assert.Equal(a, ReviewFence.Mint("s1", 1, "t1", new[] { "measure:Sales/A" }));
            Assert.NotEqual(a, ReviewFence.Mint("s1", 1, "t1", new[] { "measure:Sales/B" }));
            Assert.NotEqual(a, ReviewFence.Mint("s1", 2, "t1", new[] { "measure:Sales/A" }));
            Assert.NotEqual(a, ReviewFence.Mint("s1", 1, "t2", new[] { "measure:Sales/A" }));
            Assert.Contains("preview", ReviewFence.Missing, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("preview", ReviewFence.Stale, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("revision", ReviewFence.Missing, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("revision", ReviewFence.Stale, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\u2014", ReviewFence.Missing, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2014", ReviewFence.Stale, StringComparison.Ordinal);
        }

        private static string CopyBim(string suffix)
        {
            var target = Path.Combine(Path.GetTempPath(), $"sem-fence-{suffix}-{Guid.NewGuid():N}.bim");
            File.Copy(TestModels.FindBim(), target);
            return target;
        }

        private static async Task<(LocalEngine engine, RecordedLiveTarget target, string path)> OpenOnFakeAsync()
        {
            var target = RecordedLiveTarget.FiveTable();
            var path = target.WriteBim(target.Live);
            var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.OpenAsync(path);
            target.Attach(engine);
            await engine.SetObjectPropertyAsync("measure:Sales/Total Sales", "Expression", "1 + 1", "human");
            return (engine, target, path);
        }

        // D-006: a live commit without the preview token must refuse and write nothing.
        [Fact]
        public async Task Live_commit_without_review_token_refuses_and_writes_nothing()
        {
            var (engine, target, path) = await OpenOnFakeAsync();
            using (engine)
            {
                try
                {
                    var preview = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: false, origin: "human");
                    Assert.False(preview.Committed);
                    Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));
                    Assert.Equal(0, target.SaveChangesCount);

                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        engine.DeployLiveAsync(
                            RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                            "azcli", null, null, commit: true, origin: "human",
                            overrideReason: "recorded live double"));
                    Assert.Contains("review", ex.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("preview", ex.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("revision", ex.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(0, target.SaveChangesCount);
                    Assert.Equal("SUM ( Sales[Amount] )",
                        target.Live.Model.Tables["Sales"].Measures["Total Sales"].Expression);
                }
                finally { File.Delete(path); }
            }
        }

        // D-006: the matching token from the preview is what lets the commit through.
        [Fact]
        public async Task Live_commit_with_matching_review_token_reaches_the_fake()
        {
            var (engine, target, path) = await OpenOnFakeAsync();
            using (engine)
            {
                try
                {
                    var preview = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: false, origin: "human");
                    Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));

                    var rep = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: true, origin: "human",
                        overrideReason: "recorded live double", confirmToken: preview.ConfirmToken);
                    Assert.True(rep.Committed, rep.Error);
                    Assert.Equal("1 + 1", target.Live.Model.Tables["Sales"].Measures["Total Sales"].Expression);
                    Assert.True(target.SaveChangesCount >= 1);
                }
                finally { File.Delete(path); }
            }
        }

        // D-006: two previews of the same session mint the same token (payload hash is stable).
        [Fact]
        public async Task Live_preview_token_is_stable_until_the_session_changes()
        {
            var (engine, target, path) = await OpenOnFakeAsync();
            using (engine)
            {
                try
                {
                    var first = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: false, origin: "human");
                    var second = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: false, origin: "human");
                    Assert.Equal(first.ConfirmToken, second.ConfirmToken);

                    await engine.SetObjectPropertyAsync("measure:Sales/Total Sales", "Expression", "1 + 2", "human");
                    var third = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: false, origin: "human");
                    Assert.NotEqual(first.ConfirmToken, third.ConfirmToken);
                    Assert.Equal(0, target.SaveChangesCount);
                }
                finally { File.Delete(path); }
            }
        }

        // D-006: after the session changes, the reviewed token no longer matches.
        [Fact]
        public async Task Live_commit_refuses_when_the_session_changed_after_preview()
        {
            var (engine, target, path) = await OpenOnFakeAsync();
            using (engine)
            {
                try
                {
                    var preview = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: false, origin: "human");

                    await engine.SetObjectPropertyAsync("measure:Sales/Total Sales", "Expression", "1 + 2", "human");

                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        engine.DeployLiveAsync(
                            RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                            "azcli", null, null, commit: true, origin: "human",
                            overrideReason: "recorded live double", confirmToken: preview.ConfirmToken));
                    Assert.Contains("changed since you reviewed", ex.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(0, target.SaveChangesCount);
                    Assert.Equal("SUM ( Sales[Amount] )",
                        target.Live.Model.Tables["Sales"].Measures["Total Sales"].Expression);
                }
                finally { File.Delete(path); }
            }
        }

        // D-006: a token minted for one destination cannot write a different one.
        [Fact]
        public async Task Live_commit_refuses_when_the_target_no_longer_matches()
        {
            var (engine, target, path) = await OpenOnFakeAsync();
            using (engine)
            {
                try
                {
                    var preview = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: false, origin: "human");

                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        engine.DeployLiveAsync(
                            RecordedLiveTarget.Endpoint, "OtherModel",
                            "azcli", null, null, commit: true, origin: "human",
                            overrideReason: "recorded live double", confirmToken: preview.ConfirmToken));
                    Assert.Contains("changed since you reviewed", ex.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(0, target.SaveChangesCount);
                }
                finally { File.Delete(path); }
            }
        }

        // D-006: a live edit under the preview must refuse; the later live value stays.
        [Fact]
        public async Task Live_commit_refuses_when_the_live_model_changed_under_preview()
        {
            var (engine, target, path) = await OpenOnFakeAsync();
            using (engine)
            {
                try
                {
                    var preview = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: false, origin: "human");
                    Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));

                    target.ChangeUnderPreview(m => m.Tables["Sales"].Measures["Total Sales"].Expression = "99");

                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        engine.DeployLiveAsync(
                            RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                            "azcli", null, null, commit: true, origin: "human",
                            overrideReason: "recorded live double", confirmToken: preview.ConfirmToken));
                    Assert.Contains("changed since you reviewed", ex.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(0, target.SaveChangesCount);
                    Assert.Equal("99",
                        target.Live.Model.Tables["Sales"].Measures["Total Sales"].Expression);
                }
                finally { File.Delete(path); }
            }
        }

        // D-006 MCP door: the same token is required on the agent path.
        [Fact]
        public async Task Mcp_live_commit_without_review_token_refuses()
        {
            var (engine, target, path) = await OpenOnFakeAsync();
            using (engine)
            {
                try
                {
                    var preview = await McpTools.DeployLive(engine,
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, commit: false);
                    Assert.False(preview.Committed);
                    Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));

                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        McpTools.DeployLive(engine,
                            RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                            "azcli", null, commit: true, overrideReason: "recorded live double"));
                    Assert.Contains("review", ex.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(0, target.SaveChangesCount);
                }
                finally { File.Delete(path); }
            }
        }

        // D-112: a dry-run of the publish-link write is refused, so it cannot persist a registry change.
        [Fact]
        public async Task Dry_run_of_set_publish_destination_is_refused_and_writes_nothing()
        {
            using var engine = new LocalEngine(new SessionManager(), new Free());
            await engine.OpenAsync(TestModels.FindBim());
            var remembered = ConnectionRegistry.Remember(
                "xmla", "powerbi://example/review-fence", "Sales", "Sales model");

            var before = await engine.ConnectionContextAsync();
            Assert.False(before.Publishing.Available);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                McpTools.DryRun(engine, "set_publish_destination",
                    "{\"connectionId\":\"" + remembered.Id + "\"}"));
            Assert.Contains("set_publish_destination", ex.Message, StringComparison.Ordinal);
            Assert.Contains("bookkeeping", ex.Message, StringComparison.OrdinalIgnoreCase);

            var after = await engine.ConnectionContextAsync();
            Assert.False(after.Publishing.Available);
            Assert.Null(after.Publishing.ConnectionId);
        }

        // D-116: a file merge without the preview token refuses and leaves the target bytes alone.
        [Fact]
        public async Task File_merge_without_review_token_refuses_and_rewrites_nothing()
        {
            using var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.OpenAsync(TestModels.FindBim());
            var table = (await engine.ListTreeAsync(null)).First(t => t.Kind == "table");
            await engine.CreateMeasureAsync(table.Ref, "Fence Merge A", "1", "human");
            var target = CopyBim("no-token");
            try
            {
                var file = new ModelRef { Kind = "file", Path = target };
                var preview = await engine.ApplyDiffAsync(null, file, new[] { $"measure:{table.Name}/Fence Merge A" }, commit: false, "human");
                Assert.False(preview.Applied);
                Assert.Equal(1, preview.Count);
                Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));

                var before = File.ReadAllBytes(target);
                var r = await engine.ApplyDiffAsync(null, file, new[] { $"measure:{table.Name}/Fence Merge A" }, commit: true, "human");
                Assert.False(r.Applied);
                Assert.False(string.IsNullOrEmpty(r.Error));
                Assert.Contains("review", r.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, File.ReadAllBytes(target));
            }
            finally { File.Delete(target); }
        }

        // D-116: an external edit after preview is refused; the later bytes stay.
        [Fact]
        public async Task File_merge_refuses_when_the_target_changed_after_preview()
        {
            using var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.OpenAsync(TestModels.FindBim());
            var table = (await engine.ListTreeAsync(null)).First(t => t.Kind == "table");
            await engine.CreateMeasureAsync(table.Ref, "Fence Merge A", "1", "human");
            var target = CopyBim("stale");
            try
            {
                var file = new ModelRef { Kind = "file", Path = target };
                var preview = await engine.ApplyDiffAsync(null, file, new[] { $"measure:{table.Name}/Fence Merge A" }, commit: false, "human");
                Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));

                File.WriteAllBytes(target, File.ReadAllBytes(target).Concat(new byte[] { 0x0A }).ToArray());
                var afterEdit = File.ReadAllBytes(target);

                var r = await engine.ApplyDiffAsync(null, file, new[] { $"measure:{table.Name}/Fence Merge A" },
                    commit: true, "human", overrideReason: null, confirmToken: preview.ConfirmToken);
                Assert.False(r.Applied);
                Assert.Contains("changed since you reviewed", r.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(afterEdit, File.ReadAllBytes(target));
            }
            finally { File.Delete(target); }
        }

        // D-116: a selection that no longer exists is a no-op and must not rewrite the file.
        [Fact]
        public async Task File_merge_with_zero_changes_rewrites_nothing()
        {
            using var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.OpenAsync(TestModels.FindBim());
            var table = (await engine.ListTreeAsync(null)).First(t => t.Kind == "table");
            await engine.CreateMeasureAsync(table.Ref, "Fence Merge A", "1", "human");
            var target = CopyBim("zero");
            try
            {
                var file = new ModelRef { Kind = "file", Path = target };
                var preview = await engine.ApplyDiffAsync(null, file, new[] { $"measure:{table.Name}/Fence Merge A" }, commit: false, "human");
                Assert.Equal(1, preview.Count);

                var before = File.ReadAllBytes(target);
                var gone = await engine.ApplyDiffAsync(null, file, new[] { $"measure:{table.Name}/No Such Measure" },
                    commit: true, "human", overrideReason: null, confirmToken: preview.ConfirmToken);
                Assert.False(gone.Applied);
                Assert.Equal(0, gone.Count);
                Assert.Contains("nothing to write", gone.Note, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, File.ReadAllBytes(target));
            }
            finally { File.Delete(target); }
        }

        // D-116: matching token plus a real change writes once.
        [Fact]
        public async Task File_merge_with_matching_review_token_writes_the_selected_change()
        {
            using var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.OpenAsync(TestModels.FindBim());
            var table = (await engine.ListTreeAsync(null)).First(t => t.Kind == "table");
            await engine.CreateMeasureAsync(table.Ref, "Fence Merge A", "1", "human");
            var target = CopyBim("ok");
            try
            {
                var file = new ModelRef { Kind = "file", Path = target };
                var selected = new[] { $"measure:{table.Name}/Fence Merge A" };
                var preview = await engine.ApplyDiffAsync(null, file, selected, commit: false, "human");
                Assert.Equal(1, preview.Count);

                var r = await engine.ApplyDiffAsync(null, file, selected, commit: true, "human",
                    overrideReason: null, confirmToken: preview.ConfirmToken);
                Assert.True(r.Applied);
                Assert.Equal(1, r.Count);
                Assert.Contains(selected[0], r.AppliedRefs);
            }
            finally { File.Delete(target); }
        }

        // D-116: a session edit after preview is refused; the target file stays as it was.
        [Fact]
        public async Task File_merge_refuses_when_the_session_changed_after_preview()
        {
            using var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.OpenAsync(TestModels.FindBim());
            var table = (await engine.ListTreeAsync(null)).First(t => t.Kind == "table");
            await engine.CreateMeasureAsync(table.Ref, "Fence Merge A", "1", "human");
            var target = CopyBim("session-stale");
            try
            {
                var file = new ModelRef { Kind = "file", Path = target };
                var selected = new[] { $"measure:{table.Name}/Fence Merge A" };
                var preview = await engine.ApplyDiffAsync(null, file, selected, commit: false, "human");
                Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));

                await engine.CreateMeasureAsync(table.Ref, "Fence Merge B", "2", "human");
                var before = File.ReadAllBytes(target);
                var r = await engine.ApplyDiffAsync(null, file, selected, commit: true, "human",
                    overrideReason: null, confirmToken: preview.ConfirmToken);
                Assert.False(r.Applied);
                Assert.Contains("changed since you reviewed", r.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, File.ReadAllBytes(target));
            }
            finally { File.Delete(target); }
        }

        // D-116 MCP door: the same token is required on the agent path.
        [Fact]
        public async Task Mcp_file_merge_without_review_token_refuses_and_rewrites_nothing()
        {
            using var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.OpenAsync(TestModels.FindBim());
            var table = (await engine.ListTreeAsync(null)).First(t => t.Kind == "table");
            await engine.CreateMeasureAsync(table.Ref, "Fence Merge A", "1", "human");
            var target = CopyBim("mcp-no-token");
            try
            {
                var selected = new[] { $"measure:{table.Name}/Fence Merge A" };
                var preview = await McpTools.ApplyModelDiff(engine, targetFile: target, selectedRefs: selected, commit: false);
                Assert.False(preview.Applied);
                Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));

                var before = File.ReadAllBytes(target);
                var r = await McpTools.ApplyModelDiff(engine, targetFile: target, selectedRefs: selected, commit: true);
                Assert.False(r.Applied);
                Assert.Contains("review", r.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, File.ReadAllBytes(target));
            }
            finally { File.Delete(target); }
        }
    }
}
