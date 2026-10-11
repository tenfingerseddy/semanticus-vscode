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
    /// D-128: the undoable merge into the open model exists in the engine, but the public agent tool
    /// could not name that target. C1.7 also puts the C1.2 review fence on that merge.
    /// </summary>
    public sealed class SessionMergeDoorTests : IClassFixture<DirTemplate>
    {
        private readonly DirTemplate _tpl;
        public SessionMergeDoorTests(DirTemplate tpl) => _tpl = tpl;

        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)));
        }

        /// <summary>Lean suite: the model is a few-table model (the merge fence asks about the review token and the session, not about
        /// AdventureWorks), and the source it merges from (the same model with one extra measure, written as TMDL) is built once for the class
        /// and copied for each test, because each test removes its own copy.</summary>
        private async Task<(LocalEngine engine, SessionManager sessions, string sourceDir, string mergeRef)> OpenWithSourceAsync()
        {
            var built = await _tpl.GetAsync(async root =>
            {
                using var e0 = new LocalEngine(new SessionManager(), new Pro());
                await e0.OpenAsync(TinyBim.Write(Path.Combine(root, "src", "Tiny.bim")));
                var t0 = (await e0.ListMeasuresAsync()).First().Table;
                await e0.CreateMeasureAsync("table:" + t0, "Door Merge Probe", "1", "human");
                await e0.SaveAsync(Path.Combine(root, "source"), "TMDL", false, "human");
                File.WriteAllText(Path.Combine(root, "table.txt"), t0);
                return root;
            });
            var table = File.ReadAllText(Path.Combine(built, "table.txt"));
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Pro());
            var work = Path.Combine(Path.GetTempPath(), "sem-session-merge-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var sourceDir = Path.Combine(work, "source");
            CopyTree(Path.Combine(built, "source"), sourceDir);
            await engine.OpenAsync(TinyBim.Write(Path.Combine(work, "open", "Tiny.bim")));
            return (engine, sessions, sourceDir, "measure:" + table + "/Door Merge Probe");
        }

        // D-128: omitting a file or live target used to error. The open model is the missing target.
        [Fact]
        public async Task Mcp_can_preview_a_merge_into_the_open_model()
        {
            var (engine, _, sourceDir, mergeRef) = await OpenWithSourceAsync();
            using (engine)
            {
                try
                {
                    var r = await McpTools.ApplyModelDiff(engine, sourceFile: sourceDir, selectedRefs: new[] { mergeRef }, commit: false);
                    Assert.True(string.IsNullOrEmpty(r.Error), r.Error);
                    Assert.False(r.Applied);
                    Assert.Equal(1, r.Count);
                    Assert.Equal("open model", r.Target);
                    Assert.Contains(mergeRef, r.AppliedRefs);
                    Assert.False(string.IsNullOrEmpty(r.ConfirmToken));
                    Assert.DoesNotContain((await engine.ListMeasuresAsync()).Select(m => m.Name), n => n == "Door Merge Probe");
                }
                finally { try { Directory.Delete(Path.GetDirectoryName(sourceDir), true); } catch { } }
            }
        }

        // D-128 + C1.2: a commit without the preview token must refuse and leave the open model alone.
        [Fact]
        public async Task Mcp_merge_into_the_open_model_without_review_token_writes_nothing()
        {
            var (engine, sessions, sourceDir, mergeRef) = await OpenWithSourceAsync();
            using (engine)
            {
                try
                {
                    var selected = new[] { mergeRef };
                    var preview = await McpTools.ApplyModelDiff(engine, sourceFile: sourceDir, selectedRefs: selected, commit: false);
                    Assert.False(preview.Applied);
                    Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));
                    var before = sessions.Current.Revision;

                    var r = await McpTools.ApplyModelDiff(engine, sourceFile: sourceDir, selectedRefs: selected, commit: true);
                    Assert.False(r.Applied);
                    Assert.Contains("review", r.Error, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("preview", r.Error, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(before, sessions.Current.Revision);
                    Assert.DoesNotContain((await engine.ListMeasuresAsync()).Select(m => m.Name), n => n == "Door Merge Probe");
                }
                finally { try { Directory.Delete(Path.GetDirectoryName(sourceDir), true); } catch { } }
            }
        }

        // D-128 + C1.2: the matching token writes once, and the whole merge is one undo step.
        [Fact]
        public async Task Mcp_merge_into_the_open_model_with_matching_token_applies_and_undoes()
        {
            var (engine, _, sourceDir, mergeRef) = await OpenWithSourceAsync();
            using (engine)
            {
                try
                {
                    var selected = new[] { mergeRef };
                    var preview = await McpTools.ApplyModelDiff(engine, sourceFile: sourceDir, selectedRefs: selected, commit: false);
                    var r = await McpTools.ApplyModelDiff(engine, sourceFile: sourceDir, selectedRefs: selected, commit: true, confirmToken: preview.ConfirmToken);
                    Assert.True(r.Applied, r.Error);
                    Assert.Equal(1, r.Count);
                    Assert.Contains(mergeRef, r.AppliedRefs);
                    Assert.Contains((await engine.ListMeasuresAsync()).Select(m => m.Name), n => n == "Door Merge Probe");

                    await engine.UndoAsync("human");
                    Assert.DoesNotContain((await engine.ListMeasuresAsync()).Select(m => m.Name), n => n == "Door Merge Probe");
                }
                finally { try { Directory.Delete(Path.GetDirectoryName(sourceDir), true); } catch { } }
            }
        }

        // C1.2: an edit after preview stale-s the token; the open model is unchanged.
        [Fact]
        public async Task Mcp_merge_into_the_open_model_refuses_when_the_session_changed_after_preview()
        {
            var (engine, sessions, sourceDir, mergeRef) = await OpenWithSourceAsync();
            using (engine)
            {
                try
                {
                    var selected = new[] { mergeRef };
                    var preview = await McpTools.ApplyModelDiff(engine, sourceFile: sourceDir, selectedRefs: selected, commit: false);
                    Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));

                    var table = (await engine.ListMeasuresAsync()).First().Table;
                    await engine.CreateMeasureAsync("table:" + table, "Door Merge Extra", "2", "human");
                    var before = sessions.Current.Revision;

                    var r = await McpTools.ApplyModelDiff(engine, sourceFile: sourceDir, selectedRefs: selected, commit: true, confirmToken: preview.ConfirmToken);
                    Assert.False(r.Applied);
                    Assert.Contains("changed since you reviewed", r.Error, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(before, sessions.Current.Revision);
                    Assert.DoesNotContain((await engine.ListMeasuresAsync()).Select(m => m.Name), n => n == "Door Merge Probe");
                }
                finally { try { Directory.Delete(Path.GetDirectoryName(sourceDir), true); } catch { } }
            }
        }

        // C1.2: a selection that no longer exists must not create an undo step.
        [Fact]
        public async Task Mcp_merge_into_the_open_model_with_zero_changes_mutates_nothing()
        {
            var (engine, sessions, sourceDir, _) = await OpenWithSourceAsync();
            using (engine)
            {
                try
                {
                    var gone = new[] { "measure:Sales/No Such Measure" };
                    var preview = await McpTools.ApplyModelDiff(engine, sourceFile: sourceDir, selectedRefs: gone, commit: false);
                    Assert.Equal(0, preview.Count);
                    var before = sessions.Current.Revision;

                    var r = await McpTools.ApplyModelDiff(engine, sourceFile: sourceDir, selectedRefs: gone, commit: true, confirmToken: preview.ConfirmToken);
                    Assert.False(r.Applied);
                    Assert.Equal(0, r.Count);
                    Assert.Contains("nothing to write", r.Note, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(before, sessions.Current.Revision);
                }
                finally { try { Directory.Delete(Path.GetDirectoryName(sourceDir), true); } catch { } }
            }
        }

        // The UI door already constructs a session target; the same fence must bind it.
        [Fact]
        public async Task Engine_session_merge_without_review_token_writes_nothing()
        {
            var (engine, sessions, sourceDir, mergeRef) = await OpenWithSourceAsync();
            using (engine)
            {
                try
                {
                    var left = new ModelRef { Kind = "file", Path = sourceDir };
                    var right = new ModelRef { Kind = "session" };
                    var selected = new[] { mergeRef };
                    var preview = await engine.ApplyDiffAsync(left, right, selected, commit: false, "human");
                    Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));
                    var before = sessions.Current.Revision;

                    var r = await engine.ApplyDiffAsync(left, right, selected, commit: true, "human");
                    Assert.False(r.Applied);
                    Assert.Contains("review", r.Error, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(before, sessions.Current.Revision);
                }
                finally { try { Directory.Delete(Path.GetDirectoryName(sourceDir), true); } catch { } }
            }
        }
    }
}
