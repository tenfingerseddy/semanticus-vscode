// Verifier v-fix-save-parent (17 Oct): a copy never deletes or changes a file it did not write, END TO END the way the product runs.
// The owner engine serves the pipe (RpcServer); the Studio is a Human pipe connection that calls "save" with a path; the person's
// Claude Code is the MCP door, AssistantEngine.ForAssistant(RemoteEngine) as Program.cs wires it, calling McpTools.SaveModel.
// Snapshots never follow a link, and record each file's bytes and mode, each link's target and each empty folder.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class VFixSaveParentDoorTests
    {
        private const string Challenge = "verify-save-parent-studio-challenge-0123456789abcdef";
        private readonly ITestOutputHelper _out;
        public VFixSaveParentDoorTests(ITestOutputHelper output) => _out = output;

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
            var pipeName = TestPipe.Mint("semanticus-vsp-");
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
            var dir = Path.Combine(Path.GetTempPath(), "vsp-" + prefix + Guid.NewGuid().ToString("N").Substring(0, 10));
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

        private static async Task SeedAsync(string path, string format)
        {
            using var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await e.OpenAsync(TestModels.FindBim());
            await e.SaveAsync(path, format, false, "human");
        }

        /// <summary>Everything under the folder, never following a link: a file's bytes and mode, a link's target, an empty folder.</summary>
        // Unix file modes do not exist on Windows (the CI leg threw PlatformNotSupportedException): there the read-only attribute stands in.
        private static string ModeOf(string path) => OperatingSystem.IsWindows()
            ? ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0 ? "readonly" : "writable")
            : File.GetUnixFileMode(path).ToString();

        private static void MakeReadOnly(string path)
        {
            if (OperatingSystem.IsWindows()) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            else File.SetUnixFileMode(path, UnixFileMode.UserRead);
        }

        private static void MakeWritable(string path)
        {
            if (OperatingSystem.IsWindows()) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            else if ((File.GetUnixFileMode(path) & UnixFileMode.UserWrite) == 0) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        private static SortedDictionary<string, string> Tree(string root)
        {
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            void Walk(string dir)
            {
                var any = false;
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir).OrderBy(x => x, StringComparer.Ordinal))
                {
                    any = true;
                    var rel = Path.GetRelativePath(root, entry).Replace('\\', '/');
                    var info = Directory.Exists(entry) ? (FileSystemInfo)new DirectoryInfo(entry) : new FileInfo(entry);
                    if (info.LinkTarget != null) { map[rel] = "link:" + info.LinkTarget; continue; }
                    if (info is DirectoryInfo) { Walk(entry); continue; }
                    // A running engine writes its history through a temp file and renames it; a file seen in the listing that is gone by the read was
                    // that temp file (history.json.tmp, seen on a loaded machine), and its final name is in the map. A file that stays is still counted.
                    try { map[rel] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry))) + " mode:" + ModeOf(entry); }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { if (File.Exists(entry)) throw; }
                }
                if (!any && dir != root) map[Path.GetRelativePath(root, dir).Replace('\\', '/') + "/"] = "empty";
            }
            if (Directory.Exists(root)) Walk(root);
            return map;
        }

        private static string Diff(SortedDictionary<string, string> a, SortedDictionary<string, string> b)
            => string.Join(", ", a.Keys.Union(b.Keys).Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y) || x != y)
                .Select(k => k + " [" + (a.TryGetValue(k, out var x) ? x : "missing") + " -> " + (b.TryGetValue(k, out var y) ? y : "missing") + "]").Take(12));

        private static async Task<int> FreshOpenTablesAsync(string path)
        {
            using var e = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            return (await e.OpenAsync(path)).Tables;
        }

        private static void PlainLine(string message)
        {
            Assert.DoesNotContain('\n', message);
            Assert.DoesNotContain("—", message);
            Assert.Contains("Nothing was saved", message);
        }

        /// <summary>The place the refusal names, which must not exist yet and must take the copy.</summary>
        private static string Suggested(string message)
        {
            var m = Regex.Match(message, "for example '([^']+)'");
            Assert.True(m.Success, "the refusal names no place: " + message);
            return m.Groups[1].Value;
        }

        /// <summary>Refused on the MCP door and on the Studio door, with nothing under <paramref name="watch"/> changed; the named place works.</summary>
        private static async Task RefusedOnBothDoorsAsync(Doors d, string watch, string target, string format = "TMDL", bool studioToo = true)
        {
            var before = Tree(watch);
            var mcp = await Assert.ThrowsAnyAsync<Exception>(() => McpTools.SaveModel(d.Assistant, target, format));
            PlainLine(mcp.Message);
            Assert.True(Diff(before, Tree(watch)) == "", "MCP save_model changed something for " + target + ": " + Diff(before, Tree(watch)));
            if (studioToo)
            {
                var ui = await Assert.ThrowsAnyAsync<Exception>(() => d.Studio.InvokeAsync<JObject>("save", target, format, false, true));
                PlainLine(ui.Message);
                Assert.True(Diff(before, Tree(watch)) == "", "Studio save changed something for " + target + ": " + Diff(before, Tree(watch)));
            }
            Assert.NotNull(await d.Studio.InvokeAsync<JObject>("sessionInfo"));
            Assert.NotNull(await d.Remote.SessionInfoAsync());
            var place = Suggested(mcp.Message);
            Assert.False(Directory.Exists(place) || File.Exists(place), "the named place already exists: " + place);
        }

        [Fact]
        public async Task The_parent_a_grandparent_a_dotdot_path_and_a_link_to_the_folder_holding_the_open_model_are_refused_on_both_doors()
        {
            var root = Scratch("up-");
            var models = Path.Combine(root, "models");
            var sales = Path.Combine(models, "Sales");
            var finance = Path.Combine(models, "Finance");
            try
            {
                await SeedTmdlAsync(sales);
                await SeedTmdlAsync(finance);
                File.WriteAllText(Path.Combine(models, "notes.txt"), "keep me");
                Directory.CreateSymbolicLink(Path.Combine(root, "plink"), models);
                var tables = await FreshOpenTablesAsync(sales);
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", sales, false);
                foreach (var target in new[]
                {
                    models, root, Path.Combine(sales, ".."), Path.Combine(root, "nowhere", "..", "models"), Path.Combine(root, "plink"),
                    models + Path.DirectorySeparatorChar, Path.Combine(finance, "..", ".."),
                })
                    await RefusedOnBothDoorsAsync(d, root, target);
                // The place the refusal names takes the copy, and the open model still saves in place.
                var place = Suggested((await Assert.ThrowsAnyAsync<Exception>(() => McpTools.SaveModel(d.Assistant, models, "TMDL"))).Message);
                Assert.Equal(tables, await FreshOpenTablesAsync((await McpTools.SaveModel(d.Assistant, place, "TMDL")).Path));
                Assert.Equal(sales, (await McpTools.SaveModel(d.Assistant, null, null)).Path);
                Assert.Equal(sales, (string)(await d.Studio.InvokeAsync<JObject>("save", null, "TMDL", false))["path"]);
                Assert.Equal(tables, await FreshOpenTablesAsync(sales));
                Assert.Equal(tables, await FreshOpenTablesAsync(finance));
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task Folders_that_hold_other_models_or_loose_tmdl_are_refused_on_both_doors()
        {
            var root = Scratch("oth-");
            var sales = Path.Combine(root, "open", "Sales");
            var several = Path.Combine(root, "several");
            var loose = Path.Combine(root, "loose");
            var twoProj = Path.Combine(root, "two-proj");
            try
            {
                await SeedTmdlAsync(sales);
                await SeedTmdlAsync(Path.Combine(several, "A"));
                await SeedTmdlAsync(Path.Combine(several, "deep", "B"));
                Directory.CreateDirectory(loose);
                File.WriteAllText(Path.Combine(loose, "measures.tmdl"), "createOrReplace\n");
                File.WriteAllText(Path.Combine(loose, "readme.md"), "loose");
                await SeedTmdlAsync(Path.Combine(twoProj, "A.SemanticModel", "definition"));
                await SeedTmdlAsync(Path.Combine(twoProj, "B.SemanticModel", "definition"));
                Directory.CreateSymbolicLink(Path.Combine(root, "lnk-several"), several);
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", sales, false);
                foreach (var target in new[] { several, loose, twoProj, Path.Combine(root, "lnk-several"), Path.Combine(several, "deep") })
                    await RefusedOnBothDoorsAsync(d, root, target);
                // The assistant may not replace ONE other model either, nor a PBIP project of one model.
                var proj = Path.Combine(root, "proj");
                await SeedTmdlAsync(Path.Combine(proj, "Finance.SemanticModel", "definition"));
                File.WriteAllText(Path.Combine(proj, "Finance.pbip"), "{}");
                Directory.CreateDirectory(Path.Combine(proj, "Finance.Report"));
                File.WriteAllText(Path.Combine(proj, "Finance.Report", "report.json"), "{\"r\":1}");
                foreach (var target in new[] { Path.Combine(several, "A"), proj, Path.Combine(proj, "Finance.SemanticModel") })
                    await RefusedOnBothDoorsAsync(d, root, target, studioToo: false);
                Assert.Equal(sales, (await d.Remote.SessionInfoAsync()).Source);
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task The_open_models_own_pbip_project_folder_saves_in_place_and_a_folder_holding_that_project_is_refused()
        {
            var root = Scratch("pp-");
            var proj = Path.Combine(root, "work", "proj");
            var envelope = Path.Combine(proj, "Sales.SemanticModel");
            try
            {
                await SeedTmdlAsync(Path.Combine(envelope, "definition"));
                File.WriteAllText(Path.Combine(envelope, "definition.pbism"), "{\"version\":\"4.0\",\"settings\":{}}");
                File.WriteAllText(Path.Combine(proj, "Sales.pbip"), "{}");
                Directory.CreateDirectory(Path.Combine(proj, "Sales.Report"));
                File.WriteAllText(Path.Combine(proj, "Sales.Report", "report.json"), "{\"r\":1}");
                var tables = await FreshOpenTablesAsync(proj);
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", proj, false);
                var report = Tree(Path.Combine(proj, "Sales.Report"));
                // The project folder resolves to the model's own definition/: a save in place.
                var r = await McpTools.SaveModel(d.Assistant, proj, "TMDL");
                Assert.Equal(Path.Combine(envelope, "definition"), r.Path);
                await RefusedOnBothDoorsAsync(d, root, Path.Combine(root, "work"));
                await RefusedOnBothDoorsAsync(d, root, root);
                Assert.Equal("", Diff(report, Tree(Path.Combine(proj, "Sales.Report"))));
                Assert.Equal(tables, await FreshOpenTablesAsync(proj));
            }
            finally { Delete(root); }
        }

        /// <summary>A folder of someone's unrelated things: nested, hidden, an executable, a read-only file, an empty folder, and links.</summary>
        private static void SeedUnrelated(string dir, string outside)
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub", "deep"));
            Directory.CreateDirectory(Path.Combine(dir, "emptydir"));
            File.WriteAllText(Path.Combine(dir, "readme.md"), "# notes\n");
            File.WriteAllText(Path.Combine(dir, ".hidden"), "h");
            File.WriteAllBytes(Path.Combine(dir, "sub", "deep", "data.bin"), Enumerable.Range(0, 70000).Select(i => (byte)(i * 7)).ToArray());
            File.WriteAllText(Path.Combine(dir, "run.sh"), "#!/bin/sh\necho hi\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(dir, "run.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            File.WriteAllText(Path.Combine(dir, "locked.txt"), "read only");
            MakeReadOnly(Path.Combine(dir, "locked.txt"));
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "precious.txt"), "outside the target");
            File.WriteAllText(outside + ".txt", "outside file");
            File.WriteAllText(Path.Combine(outside, "ro.txt"), "read only outside");
            MakeReadOnly(Path.Combine(outside, "ro.txt"));
            File.CreateSymbolicLink(Path.Combine(dir, "flink"), outside + ".txt");
            Directory.CreateSymbolicLink(Path.Combine(dir, "dlink"), outside);
        }

        // The rule (17 Oct): a copy goes only to a new or an empty folder. A folder that has ANYTHING in it is refused before a byte is
        // written, whatever it holds: unrelated things, links, empty folders, a .bim, a Tabular Editor folder, a project, one other model.
        // Nothing outside the copy's own new folder ever changes, through a link or otherwise.
        [Fact]
        public async Task A_copy_into_a_folder_that_has_anything_in_it_is_refused_on_both_doors_and_nothing_anywhere_changes()
        {
            var root = Scratch("unr-");
            var sales = Path.Combine(root, "Sales");
            try
            {
                await SeedTmdlAsync(sales);
                var tables = await FreshOpenTablesAsync(sales);
                var stuff = Path.Combine(root, "stuff"); var outside = Path.Combine(root, "out");
                SeedUnrelated(stuff, outside);
                var linkOnly = Path.Combine(root, "linkonly"); Directory.CreateDirectory(linkOnly);
                Directory.CreateSymbolicLink(Path.Combine(linkOnly, "fin"), sales);
                var emptyDirOnly = Path.Combine(root, "emptydironly"); Directory.CreateDirectory(Path.Combine(emptyDirOnly, "inner"));
                var bims = Path.Combine(root, "bims"); await SeedAsync(Path.Combine(bims, "Finance.bim"), "bim");
                var te = Path.Combine(root, "te"); await SeedAsync(te, "folder");
                Assert.True(File.Exists(Path.Combine(te, "database.json")), "the Tabular Editor folder model was not made");
                var proj = Path.Combine(root, "proj");
                await SeedAsync(Path.Combine(proj, "Finance.SemanticModel", "model.bim"), "bim");
                File.WriteAllText(Path.Combine(proj, "Finance.SemanticModel", "definition.pbism"), "{\"version\":\"1.0\"}");
                File.WriteAllText(Path.Combine(proj, "Finance.pbip"), "{}");
                var finance = Path.Combine(root, "Finance"); await SeedTmdlAsync(finance);
                File.WriteAllText(Path.Combine(finance, "notes.md"), "mine");
                Directory.CreateDirectory(Path.Combine(root, "free"));
                File.WriteAllText(Path.Combine(root, "free", "x.txt"), "x");
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", sales, false);
                var open = Tree(sales);
                foreach (var target in new[] { stuff, linkOnly, emptyDirOnly, bims, te, proj, finance })
                    await RefusedOnBothDoorsAsync(d, root, target);
                // The same through a link that points at one of them.
                Directory.CreateSymbolicLink(Path.Combine(root, "lnk-stuff"), stuff);
                await RefusedOnBothDoorsAsync(d, root, Path.Combine(root, "lnk-stuff"));
                Assert.True(ModelPathResolver.ShapeOf(te).HasFlag(ModelPathResolver.ModelShape.TabularEditorFolder), "the Tabular Editor model must stay itself");
                Assert.False(ModelPathResolver.IsTmdlRoot(proj));
                Assert.Equal("", Diff(open, Tree(sales)));
                Assert.Equal(tables, await FreshOpenTablesAsync(sales));
                Assert.Equal(tables, await FreshOpenTablesAsync(finance));
                // The place named beside the refused folder takes the copy, and it reopens.
                var place = Suggested((await Assert.ThrowsAnyAsync<Exception>(() => McpTools.SaveModel(d.Assistant, stuff, "TMDL"))).Message);
                Assert.Equal(Path.Combine(root, "Sales copy"), place);
                Assert.Equal(tables, await FreshOpenTablesAsync((await McpTools.SaveModel(d.Assistant, place, "TMDL")).Path));
                Assert.Equal(sales, (await d.Remote.SessionInfoAsync()).Source);
            }
            finally
            {
                foreach (var f in Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>())
                    try { MakeWritable(f); } catch { }
                Delete(root);
            }
        }

        [Fact]
        public async Task Saves_in_place_and_copies_to_empty_or_new_folders_still_work_and_reopen_on_both_doors()
        {
            var root = Scratch("ok-");
            var sales = Path.Combine(root, "Sales");
            try
            {
                await SeedTmdlAsync(sales);
                var tables = await FreshOpenTablesAsync(sales);
                var empty1 = Path.Combine(root, "empty1"); Directory.CreateDirectory(empty1);
                var empty2 = Path.Combine(root, "empty2"); Directory.CreateDirectory(empty2);
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", sales, false);
                foreach (var target in new[] { empty1, Path.Combine(root, "new", "deeper", "Sales copy"), Path.Combine(root, "Sales copy") })
                {
                    var r = await McpTools.SaveModel(d.Assistant, target, "TMDL");
                    Assert.Equal(tables, await FreshOpenTablesAsync(r.Path));
                }
                // A target written with a trailing separator is the same place.
                var slash = await McpTools.SaveModel(d.Assistant, empty2 + Path.DirectorySeparatorChar, "TMDL");
                Assert.Equal(empty2, slash.Path);
                Assert.Equal(tables, await FreshOpenTablesAsync(empty2));
                Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".semanticus-*", SearchOption.TopDirectoryOnly));
                Assert.True(File.Exists((await McpTools.SaveModel(d.Assistant, Path.Combine(root, "Sales.bim"), "bim")).Path));
                // A copy this session already wrote is replaced, so one copy stays fresh (271d5cb2, verifier 18 Oct P4).
                Assert.True(File.Exists((await McpTools.SaveModel(d.Assistant, Path.Combine(root, "Sales.bim"), "bim")).Path));
                // The assistant's copy may not replace a .bim this session did not write; the place it names works.
                var foreign = Path.Combine(root, "Foreign.bim");
                File.WriteAllText(foreign, "{}");
                var again = await Assert.ThrowsAnyAsync<Exception>(() => McpTools.SaveModel(d.Assistant, foreign, "bim"));
                Assert.Equal("{}", File.ReadAllText(foreign));
                PlainLine(again.Message);
                Assert.True(File.Exists((await McpTools.SaveModel(d.Assistant, Suggested(again.Message), "bim")).Path));
                Assert.Equal(sales, (await d.Remote.SessionInfoAsync()).Source);
                Assert.Equal(sales, (await McpTools.SaveModel(d.Assistant, null, null)).Path);
                Assert.Equal(sales, (string)(await d.Studio.InvokeAsync<JObject>("save", null, "TMDL", false))["path"]);
                // The person's own save to a new empty folder moves the model there, and it reopens.
                var empty3 = Path.Combine(root, "empty3"); Directory.CreateDirectory(empty3);
                Assert.Equal(empty3, (string)(await d.Studio.InvokeAsync<JObject>("save", empty3, "TMDL", false, true))["path"]);
                Assert.Equal(empty3, (string)(await d.Studio.InvokeAsync<JObject>("save", null, "TMDL", false))["path"]);
                Assert.Equal(tables, await FreshOpenTablesAsync(empty3));
                Assert.Equal(tables, await FreshOpenTablesAsync(sales));
            }
            finally { Delete(root); }
        }

        // Verifier v2-fix-save-parent (17 Oct): the Studio door (the person's save over the pipe, from a dialog or not) on every case the rule
        // names. Each refusal changes nothing anywhere and names a free place that then takes the save; each save to a new or empty place
        // changes nothing outside it; save in place and a first save still work; an open of a folder holding a nested copy carries the note.
        [Fact]
        public async Task V2_the_studio_door_refuses_every_occupied_target_saves_new_or_empty_ones_and_changes_nothing_else()
        {
            var top = Scratch("v2s-");
            var root = Path.Combine(top, "t");
            var sales = Path.Combine(root, "models", "Sales");
            var outside = Path.Combine(root, "out");
            // The History keeps its files in .semanticus folders beside a model, and a save that moves the model moves them: not the target's things.
            static bool Sidecar(string k) => k.StartsWith(".semanticus/", StringComparison.Ordinal) || k.Contains("/.semanticus/", StringComparison.Ordinal);
            static List<string> Changed(SortedDictionary<string, string> a, SortedDictionary<string, string> b)
                => a.Keys.Union(b.Keys).Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y) || x != y).ToList();
            try
            {
                await SeedTmdlAsync(sales);
                await SeedTmdlAsync(Path.Combine(root, "models", "Finance"));
                await SeedAsync(Path.Combine(root, "bims", "Fin.bim"), "bim");
                await SeedAsync(Path.Combine(root, "te"), "folder");
                SeedUnrelated(Path.Combine(root, "stuff"), outside);
                File.WriteAllText(Path.Combine(root, "notes.txt"), "a file");
                File.CreateSymbolicLink(Path.Combine(root, "lnk.bim"), Path.Combine(root, "bims", "Fin.bim"));
                Directory.CreateDirectory(Path.Combine(root, "emptyonly", "inner"));
                Directory.CreateSymbolicLink(Path.Combine(root, "plink"), Path.Combine(root, "models"));
                var tables = await FreshOpenTablesAsync(sales);
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", sales, false);
                foreach (var dialog in new[] { true, false })
                    foreach (var (target, format) in new[]
                    {
                        (Path.Combine(root, "models"), "TMDL"), (Path.Combine(root, "models") + "/", "TMDL"), (Path.Combine(root, "plink"), "TMDL"),
                        (Path.Combine(root, "models", "Finance"), "TMDL"), (Path.Combine(root, "stuff"), "TMDL"), (Path.Combine(root, "emptyonly"), "TMDL"),
                        (Path.Combine(root, "te"), "folder"), (Path.Combine(root, "bims"), "TMDL"), (Path.Combine(root, "bims", "Fin.bim"), "bim"),
                        (Path.Combine(root, "notes.txt"), "TMDL"), (Path.Combine(root, "lnk.bim"), "bim"), (outside + ".txt", "bim"), (root, "TMDL"),
                    })
                    {
                        var before = Tree(top);
                        var ui = await Assert.ThrowsAnyAsync<Exception>(() => d.Studio.InvokeAsync<JObject>("save", target, format, false, dialog));
                        PlainLine(ui.Message);
                        Assert.True(Diff(before, Tree(top)) == "", "Studio save changed something for " + target + ": " + Diff(before, Tree(top)));
                        var place = Suggested(ui.Message);
                        Assert.False(Directory.Exists(place) || File.Exists(place), "the named place already exists: " + place);
                        // The named place takes the save, and nothing else changes. The person's save moves the model there, so it is opened back.
                        var saved = await d.Studio.InvokeAsync<JObject>("save", place, format, false, dialog);
                        var rel = Path.GetRelativePath(top, (string)saved["path"]).Replace('\\', '/');
                        var stray = Changed(before, Tree(top)).Where(k => k != rel && !k.StartsWith(rel + "/", StringComparison.Ordinal) && !Sidecar(k)).ToList();
                        Assert.True(stray.Count == 0, "the save to the named place " + place + " changed " + string.Join(", ", stray.Take(8)));
                        Assert.Equal(tables, await FreshOpenTablesAsync((string)saved["path"]));
                        await d.Studio.InvokeAsync<JObject>("open", sales, false);
                    }
                // New or empty places take the person's save; an empty folder written with "/" and a link to an empty folder too.
                Directory.CreateDirectory(Path.Combine(root, "empty1"));
                Directory.CreateDirectory(Path.Combine(root, "realempty"));
                Directory.CreateSymbolicLink(Path.Combine(root, "lnk-empty"), Path.Combine(root, "realempty"));
                foreach (var (target, format) in new[] { (Path.Combine(root, "empty1") + "/", "TMDL"), (Path.Combine(root, "n", "Sales copy"), "TMDL"),
                                                         (Path.Combine(root, "nb", "Sales.bim"), "bim"), (Path.Combine(root, "lnk-empty"), "TMDL") })
                {
                    var before = Tree(top);
                    var saved = await d.Studio.InvokeAsync<JObject>("save", target, format, false, true);
                    var rel = Path.GetRelativePath(top, (string)saved["path"]).Replace('\\', '/');
                    var stray = Changed(before, Tree(top)).Where(k => k != rel && !k.StartsWith(rel + "/", StringComparison.Ordinal) && !Sidecar(k)).ToList();
                    _out.WriteLine("studio save to " + target + " -> " + saved["path"] + (stray.Count > 0 ? " also changed " + string.Join(", ", stray) : ""));
                    Assert.Equal(tables, await FreshOpenTablesAsync((string)saved["path"]));
                    if (!target.EndsWith("lnk-empty", StringComparison.Ordinal)) Assert.True(stray.Count == 0, "a save to " + target + " changed " + string.Join(", ", stray.Take(8)));
                    await d.Studio.InvokeAsync<JObject>("open", sales, false);
                }
                Assert.Equal(sales, (string)(await d.Studio.InvokeAsync<JObject>("save", null, "TMDL", false))["path"]);
                Assert.Equal(tables, await FreshOpenTablesAsync(sales));
                // A first save of a new model from the Studio: into a folder of things, and onto an existing .bim it did not pick in a dialog.
                foreach (var (target, format) in new[] { (Path.Combine(root, "stuff"), "TMDL"), (Path.Combine(root, "bims", "Fin.bim"), "bim") })
                {
                    await d.Studio.InvokeAsync<JObject>("createModel", "Brand", 1604, true);
                    var before = Tree(top);
                    var first = await d.Studio.InvokeAsync<JObject>("save", target, format, false, false);
                    var rel = Path.GetRelativePath(top, (string)first["path"]).Replace('\\', '/');
                    var stray = Changed(before, Tree(top)).Where(k => k != rel && !k.StartsWith(rel + "/", StringComparison.Ordinal) && !Sidecar(k)).ToList();
                    Assert.True(stray.Count == 0, "a first save to " + target + " changed " + string.Join(", ", stray.Take(8)));
                    _out.WriteLine("first save to " + target + " -> " + first["path"] + " note: " + first["note"]);
                }
                // An open of a folder that holds a nested copy says so, on the pipe the Studio's hub uses.
                await d.Studio.InvokeAsync<JObject>("open", sales, true);
                await McpTools.SaveModel(d.Assistant, Path.Combine(root, "models", "Finance", "Sales copy"), "TMDL");
                var withNote = await d.Studio.InvokeAsync<JObject>("open", Path.Combine(root, "models", "Finance"), false);
                Assert.False(string.IsNullOrEmpty((string)(withNote["note"] ?? withNote["Note"])), "the open of a folder holding a nested copy carries no note: " + withNote);
                _out.WriteLine("open note: " + (withNote["note"] ?? withNote["Note"]));
            }
            finally
            {
                foreach (var f in Directory.Exists(top) ? Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>())
                    try { MakeWritable(f); } catch { }
                Delete(top);
            }
        }

        // The person's own save aimed at a folder that holds a model is refused too: a copy never replaces a model.
        [Fact]
        public async Task The_persons_save_onto_one_existing_model_folder_is_refused_and_both_models_stay_whole()
        {
            var root = Scratch("jc-");
            var sales = Path.Combine(root, "Sales");
            var finance = Path.Combine(root, "Finance");
            try
            {
                await SeedTmdlAsync(sales);
                await SeedTmdlAsync(finance);
                File.WriteAllText(Path.Combine(finance, "notes.md"), "mine");
                var before = Tree(root);
                await using var d = await StartAsync();
                await d.Studio.InvokeAsync<JObject>("open", sales, false);
                var ui = await Assert.ThrowsAnyAsync<Exception>(() => d.Studio.InvokeAsync<JObject>("save", finance, "TMDL", false, true));
                PlainLine(ui.Message);
                Assert.Equal("", Diff(before, Tree(root)));
            }
            finally { Delete(root); }
        }
    }
}
