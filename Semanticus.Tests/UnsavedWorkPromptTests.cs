using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// C4.1: unsaved work is refused at both doors before a session-replacing open, and a disk
    /// change under a loaded model is reported so a checkout cannot be saved over in silence.
    /// </summary>
    public sealed class UnsavedWorkPromptTests
    {
        [Fact]
        public async Task D029_mcp_open_model_refuses_while_the_open_model_has_unsaved_edits()
        {
            var root = Scratch("d029-mcp-");
            var a = Path.Combine(root, "a.bim");
            var b = Path.Combine(root, "b.bim");
            File.Copy(TestModels.FindBim(), a);
            File.Copy(TestModels.FindBim(), b);
            using var engine = new LocalEngine(new SessionManager());
            try
            {
                var opened = await engine.OpenAsync(a);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "1 + 1", "human");
                Assert.True((await engine.SessionInfoAsync()).HasUnsavedChanges);

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.OpenModel(engine, b));
                Assert.Contains("unsaved", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("save_model", ex.Message, StringComparison.Ordinal);

                var info = await engine.SessionInfoAsync();
                Assert.Equal(opened.SessionId, info.SessionId);
                Assert.Equal("1 + 1", (await engine.ListMeasuresAsync()).First(m => m.Ref == measure.Ref).Expression);
            }
            finally { engine.Dispose(); Delete(root); }
        }

        [Fact]
        public async Task D029_rpc_open_refuses_while_the_open_model_has_unsaved_edits()
        {
            var root = Scratch("d029-rpc-");
            var a = Path.Combine(root, "a.bim");
            var b = Path.Combine(root, "b.bim");
            File.Copy(TestModels.FindBim(), a);
            File.Copy(TestModels.FindBim(), b);
            using var engine = new LocalEngine(new SessionManager());
            var rpc = new EngineRpcTarget(engine);
            try
            {
                var opened = await engine.OpenAsync(a);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "2 + 2", "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.open(b));
                Assert.Contains("unsaved", ex.Message, StringComparison.OrdinalIgnoreCase);

                var info = await engine.SessionInfoAsync();
                Assert.Equal(opened.SessionId, info.SessionId);
                Assert.Equal("2 + 2", (await engine.ListMeasuresAsync()).First(m => m.Ref == measure.Ref).Expression);
            }
            finally { engine.Dispose(); Delete(root); }
        }

        [Fact]
        public async Task D029_mcp_create_model_refuses_while_the_open_model_has_unsaved_edits()
        {
            var root = Scratch("d029-create-");
            var a = Path.Combine(root, "a.bim");
            File.Copy(TestModels.FindBim(), a);
            using var engine = new LocalEngine(new SessionManager());
            try
            {
                var opened = await engine.OpenAsync(a);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "3", "human");

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.CreateModel(engine, "Other"));
                Assert.Contains("unsaved", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(opened.SessionId, (await engine.SessionInfoAsync()).SessionId);
            }
            finally { engine.Dispose(); Delete(root); }
        }

        [Fact]
        public async Task D029_discard_unsaved_allows_the_agent_door_to_replace_the_session()
        {
            var root = Scratch("d029-discard-");
            var a = Path.Combine(root, "a.bim");
            var b = Path.Combine(root, "b.bim");
            File.Copy(TestModels.FindBim(), a);
            File.Copy(TestModels.FindBim(), b);
            using var engine = new LocalEngine(new SessionManager());
            try
            {
                var opened = await engine.OpenAsync(a);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "9", "human");

                var replaced = await McpTools.OpenModel(engine, b, discardUnsaved: true);
                Assert.NotEqual(opened.SessionId, replaced.SessionId);
                Assert.False((await engine.SessionInfoAsync()).HasUnsavedChanges);
            }
            finally { engine.Dispose(); Delete(root); }
        }

        [Fact]
        public async Task D025_a_never_saved_model_refuses_a_pathless_save_on_both_doors()
        {
            using var engine = new LocalEngine(new SessionManager());
            await engine.CreateModelAsync("First Save", 1604);
            var mcp = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SaveModel(engine, null, "TMDL"));
            var rpc = await Assert.ThrowsAsync<InvalidOperationException>(() => new EngineRpcTarget(engine).save(null, "TMDL"));
            Assert.Contains("never been saved", mcp.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("never been saved", rpc.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("save_model", mcp.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("save_model", rpc.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task D080_session_info_reports_when_disk_diverged_from_the_loaded_model()
        {
            var root = Scratch("d080-disk-");
            var bim = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            using var engine = new LocalEngine(new SessionManager());
            try
            {
                await engine.OpenAsync(bim);
                Assert.False((await engine.SessionInfoAsync()).DiskDiverged);

                File.WriteAllText(bim, File.ReadAllText(bim) + "\n");
                Assert.True((await engine.SessionInfoAsync()).DiskDiverged);
                Assert.False((await engine.SessionInfoAsync()).HasUnsavedChanges);
            }
            finally { engine.Dispose(); Delete(root); }
        }

        [Fact]
        public async Task D080_a_save_clears_disk_divergence()
        {
            var root = Scratch("d080-save-");
            var bim = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            using var engine = new LocalEngine(new SessionManager());
            try
            {
                await engine.OpenAsync(bim);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "7", "human");
                File.WriteAllText(bim, File.ReadAllText(bim) + "\n");
                Assert.True((await engine.SessionInfoAsync()).DiskDiverged);

                var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SaveAsync(null, "BIM"));
                Assert.Contains("files on disk changed", blocked.Message, StringComparison.OrdinalIgnoreCase);

                await engine.SaveAsync(null, "BIM", overwrite: true);
                Assert.False((await engine.SessionInfoAsync()).DiskDiverged);
                Assert.False((await engine.SessionInfoAsync()).HasUnsavedChanges);
            }
            finally { engine.Dispose(); Delete(root); }
        }

        private static string Scratch(string prefix) =>
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "semanticus-" + prefix + Guid.NewGuid().ToString("N"))).FullName;

        private static void Delete(string path)
        {
            try { Directory.Delete(path, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}
