// Verifier v-fix-save-inside (17 Oct): a save that lands inside the open model's own folder, END TO END the way the product runs.
// The owner engine serves the pipe (RpcServer); the Studio is a Human pipe connection that calls "save" with a path (Save As); the
// person's Claude Code is the MCP door, which holds AssistantEngine.ForAssistant(RemoteEngine) (Program.cs) and calls McpTools.SaveModel.
// Every refused target must write nothing on either door and leave the owner answering; every allowed target must still save.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class VFixSaveInsideDoorTests
    {
        private const string Challenge = "verify-save-inside-studio-challenge-0123456789abcdef";

        private sealed class Doors : IAsyncDisposable
        {
            public LocalEngine Owner;
            public RpcServer Server;
            public System.Threading.CancellationTokenSource Stop = new System.Threading.CancellationTokenSource();
            public System.IO.Pipes.NamedPipeClientStream Pipe;
            public StreamJsonRpc.JsonRpc Studio;
            public RemoteEngine Remote;
            public IEngine Assistant;

            public async ValueTask DisposeAsync()
            {
                try { Remote?.Dispose(); } catch { }
                try { Studio?.Dispose(); Pipe?.Dispose(); } catch { }
                Stop.Cancel();
                try { Server?.Dispose(); } catch { }
                try { Owner?.Dispose(); } catch { }
                await Task.CompletedTask;
            }
        }

        private static async Task<Doors> StartAsync()
        {
            var d = new Doors();
            var sessions = new SessionManager();
            d.Owner = new LocalEngine(sessions, TestEntitlements.Pro);
            var pipeName = TestPipe.Mint("semanticus-vsi-");
            d.Server = new RpcServer(sessions, d.Owner, pipeName, Challenge);
            _ = d.Server.RunAsync(d.Stop.Token);
            d.Pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await d.Pipe.ConnectAsync(5000);
            await RpcHandshake.WriteAsync(d.Pipe, RpcConnectionRole.Human, Challenge);
            await RpcHandshake.ReadAcceptedAsync(d.Pipe);
            d.Studio = new StreamJsonRpc.JsonRpc(RpcServer.CreateHandler(d.Pipe));
            d.Studio.StartListening();
            d.Remote = await RemoteEngine.ConnectAsync(pipeName, timeoutMs: 5000);
            d.Assistant = AssistantEngine.ForAssistant(d.Remote);
            return d;
        }

        private static string Scratch(string prefix)
        {
            var dir = Path.Combine(Path.GetTempPath(), "vsi-" + prefix + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Delete(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }

        private static async Task SeedTmdlAsync(string folder)
        {
            using var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.OpenAsync(TestModels.FindBim());
            await e.SaveAsync(folder, "TMDL", false, "human");
        }

        /// <summary>Every file under the folder with its bytes' hash: the proof that a refusal wrote nothing.</summary>
        private static SortedDictionary<string, string> Tree(string root)
        {
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (!Directory.Exists(root)) return map;
            foreach (var f in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                map[Path.GetRelativePath(root, f)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
            foreach (var dir in Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) map[Path.GetRelativePath(root, dir) + "/"] = "";
            return map;
        }

        private static string Diff(SortedDictionary<string, string> a, SortedDictionary<string, string> b)
            => string.Join(", ", a.Keys.Union(b.Keys).Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y) || x != y).Take(8));

        private static void PlainLine(string message)
        {
            Assert.DoesNotContain('\n', message);
            Assert.DoesNotContain("—", message);
            Assert.Contains("Nothing was saved", message);
            Assert.Contains("outside", message);
        }

        private static async Task<int> FreshOpenTablesAsync(string path)
        {
            using var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            return (await e.OpenAsync(path)).Tables;
        }

        private static async Task RefusedOnBothDoorsAsync(Doors d, string watch, string target, string format = "TMDL")
        {
            var before = Tree(watch);
            var mcp = await Assert.ThrowsAnyAsync<Exception>(() => McpTools.SaveModel(d.Assistant, target, format));
            PlainLine(mcp.Message);
            Assert.True(Diff(before, Tree(watch)) == "", "MCP save_model wrote something for " + target + ": " + Diff(before, Tree(watch)));
            var ui = await Assert.ThrowsAnyAsync<Exception>(() => d.Studio.InvokeAsync<JObject>("save", target, format, false, true));
            PlainLine(ui.Message);
            Assert.True(Diff(before, Tree(watch)) == "", "Studio Save As wrote something for " + target + ": " + Diff(before, Tree(watch)));
            // The owner still answers on both doors.
            Assert.NotNull(await d.Studio.InvokeAsync<JObject>("sessionInfo"));
            Assert.NotNull(await d.Remote.SessionInfoAsync());
        }

        [Fact]
        public async Task Every_target_inside_an_open_tmdl_folder_is_refused_on_both_doors_and_nothing_is_written()
        {
            var root = Scratch("in-");
            var model = Path.Combine(root, "model");
            try
            {
                await SeedTmdlAsync(model);
                var tables = await FreshOpenTablesAsync(model);
                Directory.CreateSymbolicLink(Path.Combine(root, "link"), model);
                Directory.CreateSymbolicLink(Path.Combine(root, "link-tables"), Path.Combine(model, "tables"));
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", model, false);
                foreach (var target in new[]
                {
                    Path.Combine(model, "Model-copy"),
                    Path.Combine(model, "tables", "copy"),
                    Path.Combine(model, "..", "model", "copy"),
                    Path.Combine(root, "nowhere", "..", "model", "copy"),
                    Path.Combine(root, "link", "copy"),
                    Path.Combine(root, "link-tables", "copy"),
                    model + Path.DirectorySeparatorChar + "copy" + Path.DirectorySeparatorChar,
                })
                    await RefusedOnBothDoorsAsync(d, root, target);
                await RefusedOnBothDoorsAsync(d, root, Path.Combine(model, "copy.bim"), "bim");
                Assert.Empty(ModelPathResolver.NestedModelRoots(model));
                Assert.Equal(tables, await FreshOpenTablesAsync(model));
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task A_model_opened_through_a_link_refuses_a_copy_inside_its_real_folder()
        {
            var root = Scratch("lk-");
            var model = Path.Combine(root, "model");
            try
            {
                await SeedTmdlAsync(model);
                Directory.CreateSymbolicLink(Path.Combine(root, "link"), model);
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", Path.Combine(root, "link"), false);
                await RefusedOnBothDoorsAsync(d, root, Path.Combine(model, "copy"));
                await RefusedOnBothDoorsAsync(d, root, Path.Combine(root, "link", "copy"));
                Assert.Empty(ModelPathResolver.NestedModelRoots(model));
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task Outside_targets_and_saves_in_place_still_save_on_both_doors()
        {
            var root = Scratch("out-");
            var model = Path.Combine(root, "model");
            try
            {
                await SeedTmdlAsync(model);
                var tables = await FreshOpenTablesAsync(model);
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", model, false);
                // The assistant: a sibling, a name that only starts with the model folder's name, and a .bim beside it. Each is a copy.
                foreach (var target in new[] { Path.Combine(root, "model copy"), Path.Combine(root, "model2"), Path.Combine(root, "backup", "model") })
                {
                    var r = await McpTools.SaveModel(d.Assistant, target, "TMDL");
                    Assert.Equal(target, r.Path);
                    Assert.Equal(tables, await FreshOpenTablesAsync(target));
                }
                var bim = await McpTools.SaveModel(d.Assistant, Path.Combine(root, "model.bim"), "bim");
                Assert.True(File.Exists(bim.Path));
                Assert.Equal(model, (await d.Remote.SessionInfoAsync()).Source);
                // Saves in place, both doors.
                Assert.Equal(model, (await McpTools.SaveModel(d.Assistant, null, null)).Path);
                Assert.Equal(model, (string)(await d.Studio.InvokeAsync<JObject>("save", null, "TMDL", false))["path"]);
                // The person's Save As outside moves the model; the next in-place save writes the new home.
                var moved = Path.Combine(root, "model moved");
                Assert.Equal(moved, (string)(await d.Studio.InvokeAsync<JObject>("save", moved, "TMDL", false, true))["path"]);
                Assert.Equal(moved, (string)(await d.Studio.InvokeAsync<JObject>("save", null, "TMDL", false))["path"]);
                Assert.Equal(tables, await FreshOpenTablesAsync(model));
                Assert.Equal(tables, await FreshOpenTablesAsync(moved));
                Assert.Empty(ModelPathResolver.NestedModelRoots(model));
            }
            finally { Delete(root); }
        }

        // On a case-sensitive disk (Linux) "MODEL" is another folder, so a copy there is outside: it saves and the model is untouched.
        // On Windows and macOS it is the same folder and must be refused.
        [Fact]
        public async Task A_different_case_path_is_refused_where_the_disk_ignores_case_and_is_a_harmless_outside_copy_where_it_does_not()
        {
            var root = Scratch("case-");
            var model = Path.Combine(root, "model");
            try
            {
                await SeedTmdlAsync(model);
                var tables = await FreshOpenTablesAsync(model);
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", model, false);
                var target = Path.Combine(root, "MODEL", "copy");
                var caseInsensitive = Directory.Exists(Path.Combine(root, "MODEL"));
                if (caseInsensitive) await RefusedOnBothDoorsAsync(d, root, target);
                else
                {
                    Assert.Equal(target, (await McpTools.SaveModel(d.Assistant, target, "TMDL")).Path);
                    Assert.Empty(ModelPathResolver.NestedModelRoots(model));
                    Assert.Equal(tables, await FreshOpenTablesAsync(model));
                }
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task Every_target_inside_a_pbip_semantic_model_folder_is_refused_on_both_doors()
        {
            var root = Scratch("pbip-");
            var envelope = Path.Combine(root, "proj", "Sales.SemanticModel");
            try
            {
                await SeedTmdlAsync(Path.Combine(envelope, "definition"));
                File.WriteAllText(Path.Combine(envelope, "definition.pbism"), "{\"version\":\"4.0\",\"settings\":{}}");
                var tables = await FreshOpenTablesAsync(envelope);
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", envelope, false);
                foreach (var target in new[] { Path.Combine(envelope, "copy"), Path.Combine(envelope, "definition", "copy"), Path.Combine(envelope, "definition", "tables", "copy") })
                    await RefusedOnBothDoorsAsync(d, root, target);
                // A sibling of the project still saves.
                Assert.Equal(tables, await FreshOpenTablesAsync((await McpTools.SaveModel(d.Assistant, Path.Combine(root, "Sales copy"), "TMDL")).Path));
                Assert.Equal(tables, await FreshOpenTablesAsync(envelope));
            }
            finally { Delete(root); }
        }

        // ATTACK: the .SemanticModel folder ITSELF as the target. It is the open model's own folder (OwnModelFolder), so the copy lands
        // beside definition/; a folder save then swaps a fresh tree over the target and drops every .tmdl it did not write, which is
        // the whole definition/ the session follows.
        [Fact]
        public async Task A_copy_aimed_at_the_open_semantic_model_folder_itself_leaves_its_definition_whole()
        {
            var root = Scratch("env-");
            var envelope = Path.Combine(root, "proj", "Sales.SemanticModel");
            try
            {
                await SeedTmdlAsync(Path.Combine(envelope, "definition"));
                File.WriteAllText(Path.Combine(envelope, "definition.pbism"), "{\"version\":\"4.0\",\"settings\":{}}");
                var tables = await FreshOpenTablesAsync(envelope);
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", envelope, false);
                var before = Tree(Path.Combine(envelope, "definition"));
                try { await McpTools.SaveModel(d.Assistant, envelope, "TMDL"); } catch (Exception) { }   // refusing is a fine answer
                Assert.True(Diff(before, Tree(Path.Combine(envelope, "definition"))) == "",
                    "the open model's definition changed: " + Diff(before, Tree(Path.Combine(envelope, "definition"))));
                Assert.Equal(tables, await FreshOpenTablesAsync(envelope));
            }
            finally { Delete(root); }
        }

        // ATTACK: a copy aimed at the folder that HOLDS the open model (and a second model beside it). The target is outside the model's
        // folder, so it is allowed; the folder save then drops every .tmdl under the target it did not write.
        [Fact]
        public async Task A_copy_aimed_at_the_folder_that_holds_the_open_model_leaves_that_model_and_its_neighbours_whole()
        {
            var root = Scratch("up-");
            var models = Path.Combine(root, "models");
            var model = Path.Combine(models, "Sales");
            var other = Path.Combine(models, "Finance");
            try
            {
                await SeedTmdlAsync(model);
                await SeedTmdlAsync(other);
                var tables = await FreshOpenTablesAsync(model);
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", model, false);
                var beforeModel = Tree(model); var beforeOther = Tree(other);
                try { await McpTools.SaveModel(d.Assistant, models, "TMDL"); } catch (Exception) { }   // refusing is a fine answer
                var lost = "neighbour: [" + Diff(beforeOther, Tree(other)) + "] open model: [" + Diff(beforeModel, Tree(model)) + "]";
                Assert.True(Diff(beforeOther, Tree(other)) == "" && Diff(beforeModel, Tree(model)) == "", "a copy over the parent folder removed model files: " + lost);
                Assert.Equal(tables, await FreshOpenTablesAsync(model));
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task A_folder_that_holds_nested_copies_opens_on_both_doors_with_one_plain_note_and_saves_in_place_keep_the_copies()
        {
            var root = Scratch("nest-");
            var model = Path.Combine(root, "model");
            try
            {
                await SeedTmdlAsync(model);
                var tables = await FreshOpenTablesAsync(model);
                // What an older build left: a whole copy at the top, one deep in tables/, and a PBIP-shaped one.
                await SeedTmdlAsync(Path.Combine(model, "Model-copy"));
                await SeedTmdlAsync(Path.Combine(model, "tables", "old"));
                await SeedTmdlAsync(Path.Combine(model, "Backup.SemanticModel", "definition"));
                var copies = new[] { "Model-copy", Path.Combine("tables", "old"), "Backup.SemanticModel" }.Select(c => Path.Combine(model, c)).ToArray();
                var before = copies.Select(Tree).ToArray();
                await using var d = await StartAsync();

                var ui = await d.Studio.InvokeAsync<JObject>("open", model, false);
                Assert.Equal(tables, (int)ui["tables"]);
                var note = (string)ui["note"];
                Assert.False(string.IsNullOrEmpty(note), "the Studio's open carries no note: " + ui);
                Assert.DoesNotContain('\n', note);
                Assert.DoesNotContain("—", note);
                Assert.True(new[] { "Model-copy", "old", "Backup.SemanticModel" }.Any(n => note.Contains(n)), "the note names no nested copy: " + note);

                var mcp = await McpTools.OpenModel(d.Assistant, model, true);
                Assert.Equal(tables, mcp.Tables);
                Assert.Equal(note, mcp.Note);

                Assert.Equal(model, (await McpTools.SaveModel(d.Assistant, null, null)).Path);
                Assert.Equal(model, (string)(await d.Studio.InvokeAsync<JObject>("save", null, "TMDL", false))["path"]);
                for (var i = 0; i < copies.Length; i++)
                    Assert.True(Diff(before[i], Tree(copies[i])) == "", "a nested copy changed: " + copies[i] + ": " + Diff(before[i], Tree(copies[i])));
                Assert.Equal(tables, (int)(await d.Studio.InvokeAsync<JObject>("open", model, true))["tables"]);
                Assert.Equal(tables, await FreshOpenTablesAsync(Path.Combine(model, "Model-copy")));
            }
            finally { Delete(root); }
        }

        // The lane's choice: a loose .bim's own folder is not blocked. Proof that a copy beside it is harmless on both doors.
        [Fact]
        public async Task A_copy_beside_a_loose_bim_saves_on_both_doors_and_the_bim_still_opens_whole()
        {
            var root = Scratch("bim-");
            var bim = Path.Combine(root, "loose", "Sales.bim");
            try
            {
                using (var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
                {
                    await e.OpenAsync(TestModels.FindBim());
                    await e.SaveAsync(bim, "bim", false, "human");
                }
                var tables = await FreshOpenTablesAsync(bim);
                var bytes = File.ReadAllBytes(bim);
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", bim, false);
                Assert.Equal(tables, await FreshOpenTablesAsync((await McpTools.SaveModel(d.Assistant, Path.Combine(root, "loose", "Sales-backup.bim"), "bim")).Path));
                Assert.Equal(tables, await FreshOpenTablesAsync((await McpTools.SaveModel(d.Assistant, Path.Combine(root, "loose", "Sales folder"), "TMDL")).Path));
                Assert.Equal(bytes, File.ReadAllBytes(bim));
                Assert.Equal(tables, await FreshOpenTablesAsync(bim));
                var moved = (string)(await d.Studio.InvokeAsync<JObject>("save", Path.Combine(root, "loose", "Sales v2.bim"), "bim", false, true))["path"];
                Assert.Equal(tables, await FreshOpenTablesAsync(moved));
                Assert.Equal(tables, await FreshOpenTablesAsync(bim));
            }
            finally { Delete(root); }
        }
    }
}
