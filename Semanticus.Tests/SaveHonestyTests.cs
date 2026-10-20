using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// C4.2: a save is whole, honest, and does not clobber. Both doors share IEngine, so these
    /// pins run through LocalEngine, the RPC target, and the MCP wrappers.
    /// </summary>
    public sealed class SaveHonestyTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private static string Snapshot2(string dir) => string.Join("\n", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".semanticus")).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => f + "=" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(f)))));

        private static string Scratch(string prefix)
        {
            var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void Delete(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, true);
            }
            catch { }
        }

        private static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(stderr);
            return stdout.Trim();
        }

        private static void InitRepo(string dir)
        {
            Git(dir, "init", "-q");
            Git(dir, "config", "user.email", "save-honesty@test.local");
            Git(dir, "config", "user.name", "Save Honesty");
            Git(dir, "config", "commit.gpgsign", "false");
        }

        private static string PrimerMarkdown(string overview) =>
            PrimerContract.Template("Honesty").Replace("_Add what people and the AI Assistant should know._", overview);

        [Fact]
        public async Task D031_save_refuses_to_overwrite_a_newer_external_edit_until_overwrite_is_set()
        {
            var root = Scratch("d031-");
            var bim = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            var rpc = new EngineRpcTarget(engine);
            try
            {
                await engine.OpenAsync(bim);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "1 + 1", "human");
                File.WriteAllText(bim, File.ReadAllText(bim).Replace("\"name\":", "\"name\":"));
                File.AppendAllText(bim, "\n");
                Assert.True((await engine.SessionInfoAsync()).DiskDiverged);

                var mcp = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SaveModel(engine, null, "TMDL"));
                Assert.Contains("files on disk changed", mcp.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("overwrite", mcp.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("save_model", mcp.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("\u2014", mcp.Message);

                var ui = await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.save(null, "TMDL"));
                Assert.Contains("files on disk changed", ui.Message, StringComparison.OrdinalIgnoreCase);

                var still = File.ReadAllText(bim);
                Assert.EndsWith("\n", still.Replace("\r\n", "\n"));
                Assert.DoesNotContain("1 + 1", still, StringComparison.Ordinal);

                var saved = await McpTools.SaveModel(engine, null, "TMDL", overwrite: true);
                Assert.Equal(bim, saved.Path);
                Assert.Contains("1 + 1", File.ReadAllText(bim), StringComparison.Ordinal);
                Assert.False((await engine.SessionInfoAsync()).HasUnsavedChanges);
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task D024_a_failed_folder_save_leaves_the_original_tree_intact()
        {
            var root = Scratch("d024-");
            var tmdl = Path.Combine(root, "model");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(tmdl, "TMDL", false, "human");
                var before = Snapshot(tmdl);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "9 + 9", "human");
                engine.FailFolderSaveBeforeSwapForTest = new IOException("forced mid-save failure");

                var ex = await Assert.ThrowsAsync<IOException>(() => engine.SaveAsync(null, "TMDL", false, "human"));
                Assert.Contains("forced mid-save failure", ex.Message, StringComparison.Ordinal);
                Assert.Equal(before, Snapshot(tmdl));
                Assert.DoesNotContain("9 + 9", Snapshot(tmdl), StringComparison.Ordinal);
                Assert.True((await engine.SessionInfoAsync()).HasUnsavedChanges);
            }
            finally { Delete(root); }
        }

        // Verifier v-fix-fleet-core (17 Oct): a fleet worker saved "a copy" into a folder INSIDE the open TMDL folder. The nested
        // model.tmdl then merges with the real one and the model can never be opened again (owner crash, every fleet tick fails).
        [Fact]
        public async Task A_copy_saved_inside_the_open_model_folder_leaves_the_model_openable()
        {
            var root = Scratch("nest-");
            var tmdl = Path.Combine(root, "model");
            try
            {
                using (var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
                {
                    await engine.OpenAsync(TestModels.FindBim());
                    await engine.SaveAsync(tmdl, "TMDL", false, "human");
                }
                using (var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
                {
                    await engine.OpenAsync(tmdl);
                    try { await McpTools.SaveModel(engine, Path.Combine(tmdl, "Model-copy"), "TMDL"); }
                    catch (InvalidOperationException) { } // refusing the nested copy is a fine answer too
                }
                using (var reopen = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
                    Assert.False(string.IsNullOrEmpty((await reopen.OpenAsync(tmdl)).ModelName), "the model folder still opens after the copy");
            }
            finally { Delete(root); }
        }

        // fix-save-inside (17 Oct): the refusal says where a copy CAN go, writes nothing, and leaves both a save in place and a save outside working.
        [Fact]
        public async Task A_save_inside_the_open_folder_is_refused_in_a_plain_line_and_saves_in_place_and_outside_still_work()
        {
            var root = Scratch("nest2-");
            var tmdl = Path.Combine(root, "model");
            try
            {
                using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(tmdl, "TMDL", false, "human");
                await engine.OpenAsync(tmdl);
                foreach (var origin in new[] { "agent", "human" })
                {
                    var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SaveAsync(Path.Combine(tmdl, "deeper", "Model-copy"), "TMDL", false, origin));
                    Assert.Contains("outside that folder", refused.Message);
                    Assert.Contains(Path.Combine(root, "model copy"), refused.Message);
                    Assert.False(Directory.Exists(Path.Combine(tmdl, "deeper")));
                }
                Assert.Empty(ModelPathResolver.NestedModelRoots(tmdl));
                var outside = await McpTools.SaveModel(engine, Path.Combine(root, "model copy"), "TMDL");
                Assert.Equal(Path.Combine(root, "model copy"), outside.Path);
                Assert.True(File.Exists(Path.Combine(root, "model copy", "model.tmdl")));
                var inPlace = await McpTools.SaveModel(engine, null, "TMDL");
                Assert.Equal(tmdl, inPlace.Path);
            }
            finally { Delete(root); }
        }

        // F-296 (Codex on pull request 402): a copy saved as TMDL is a folder, and only a copy FILE was remembered, so the assistant's second
        // save to the same copy path was refused although the instructions promise it refreshes the copy. A folder that is still exactly what this
        // session wrote is replaced whole; one that changed since goes untouched and the copy is written beside it; a folder it did not write is refused.
        [Fact]
        public async Task A_second_agent_save_refreshes_an_unchanged_tmdl_copy_folder_and_goes_beside_a_changed_one_and_a_foreign_folder_is_still_refused()
        {
            var root = Scratch("f296-");
            var sales = Path.Combine(root, "Sales");
            var copy = Path.Combine(root, "Sales backup");
            var foreign = Path.Combine(root, "foreign");
            try
            {
                using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(sales, "TMDL", false, "human");
                await engine.OpenAsync(sales);

                var first = await engine.SaveAsync(copy, "TMDL", false, "agent");
                Assert.True(first.IsBackupCopy);
                Assert.True(File.Exists(Path.Combine(copy, "model.tmdl")));

                var refreshed = await engine.SaveAsync(copy, "TMDL", false, "agent");   // nothing in the copy changed: it is replaced, as the instructions promise
                Assert.True(refreshed.IsBackupCopy);
                Assert.Equal(Path.GetFullPath(copy), Path.GetFullPath(refreshed.Path));
                Assert.True(File.Exists(Path.Combine(copy, "model.tmdl")));
                Assert.Empty(Directory.GetFileSystemEntries(root, "Sales backup.semanticus-bak-*"));

                var stray = Path.Combine(copy, "stray.txt");
                File.WriteAllText(stray, "added to the copy after it was written");
                var beside = await engine.SaveAsync(copy, "TMDL", false, "agent");   // something changed: the copy goes beside it
                Assert.True(beside.IsBackupCopy);
                Assert.Equal(Path.GetFullPath(Path.Combine(root, "Sales backup 2")), Path.GetFullPath(beside.Path));
                Assert.True(File.Exists(Path.Combine(beside.Path, "model.tmdl")));
                Assert.True(File.Exists(stray), "a file somebody added to the copy folder was deleted");
                Assert.Equal("added to the copy after it was written", File.ReadAllText(stray));
                Assert.Contains("Sales backup 2", beside.Note);

                // A folder this session did not write is never replaced.
                Directory.CreateDirectory(foreign);
                var theirs = Path.Combine(foreign, "notes.txt");
                File.WriteAllText(theirs, "somebody else's file");
                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SaveAsync(foreign, "TMDL", false, "agent"));
                Assert.Contains("Nothing was saved", refused.Message);
                Assert.Equal("somebody else's file", File.ReadAllText(theirs));
                Assert.False(File.Exists(Path.Combine(foreign, "model.tmdl")));
            }
            finally { Delete(root); }
        }

        // Verifier v-fix-codex402 (F-296 lost-work bar): a file the person adds to a copy folder after Semanticus wrote it is the person's
        // work. The next copy to that path may refresh what Semanticus wrote, or refuse, but it must never delete the person's file.
        [Fact]
        public async Task Verifier_a_file_the_person_added_to_the_copy_folder_survives_the_next_copy()
        {
            var root = Scratch("vf296a-");
            var sales = Path.Combine(root, "Sales");
            var copy = Path.Combine(root, "Sales backup");
            try
            {
                using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(sales, "TMDL", false, "human");
                await engine.OpenAsync(sales);
                await engine.SaveAsync(copy, "TMDL", false, "agent");
                var notes = Path.Combine(copy, "my notes.txt");
                File.WriteAllText(notes, "the person's own notes");

                var again = await engine.SaveAsync(copy, "TMDL", false, "agent");   // a redirect, never a refusal with no way on
                Assert.NotEqual(Path.GetFullPath(copy), Path.GetFullPath(again.Path));
                Assert.True(File.Exists(Path.Combine(again.Path, "model.tmdl")));

                Assert.True(File.Exists(notes), "the next copy deleted a file the person added to the copy folder");
                Assert.Equal("the person's own notes", File.ReadAllText(notes));
            }
            finally { Delete(root); }
        }

        // Same bar: a file Semanticus wrote and the person then edited is no longer what Semanticus wrote. Refuse or keep it; never overwrite it.
        [Fact]
        public async Task Verifier_an_edit_the_person_made_inside_the_copy_folder_survives_the_next_copy()
        {
            var root = Scratch("vf296b-");
            var sales = Path.Combine(root, "Sales");
            var copy = Path.Combine(root, "Sales backup");
            try
            {
                using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(sales, "TMDL", false, "human");
                await engine.OpenAsync(sales);
                await engine.SaveAsync(copy, "TMDL", false, "agent");
                var model = Path.Combine(copy, "model.tmdl");
                File.AppendAllText(model, Environment.NewLine + "// the person's edit" + Environment.NewLine);

                var again = await engine.SaveAsync(copy, "TMDL", false, "agent");   // a redirect, never a refusal with no way on
                Assert.NotEqual(Path.GetFullPath(copy), Path.GetFullPath(again.Path));
                Assert.True(File.Exists(Path.Combine(again.Path, "model.tmdl")));

                Assert.Contains("// the person's edit", File.ReadAllText(model));
            }
            finally { Delete(root); }
        }

        // Same bar: the person removed the copy and put their own folder at that path. The session wrote the PATH once, not this folder.
        [Fact]
        public async Task Verifier_a_folder_the_person_put_at_a_former_copy_path_is_not_replaced()
        {
            var root = Scratch("vf296c-");
            var sales = Path.Combine(root, "Sales");
            var copy = Path.Combine(root, "Sales backup");
            try
            {
                using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(sales, "TMDL", false, "human");
                await engine.OpenAsync(sales);
                await engine.SaveAsync(copy, "TMDL", false, "agent");
                Directory.Delete(copy, true);
                Directory.CreateDirectory(copy);
                var theirs = Path.Combine(copy, "report.xlsx");
                File.WriteAllText(theirs, "somebody's spreadsheet");

                var again = await engine.SaveAsync(copy, "TMDL", false, "agent");   // a redirect, never a refusal with no way on
                Assert.NotEqual(Path.GetFullPath(copy), Path.GetFullPath(again.Path));
                Assert.True(File.Exists(Path.Combine(again.Path, "model.tmdl")));

                Assert.True(File.Exists(theirs), "the next copy deleted a folder the person made at a path the session once wrote");
            }
            finally { Delete(root); }
        }

        // Verifier v2-codex402 (F-296 hash record): every kind of change since the copy was written sends the next copy beside it,
        // and the change is still there afterwards.
        [Theory]
        [InlineData("rename")]
        [InlineData("same-size")]
        [InlineData("empty-subfolder")]
        [InlineData("file-symlink")]
        [InlineData("dir-symlink")]
        [InlineData("dangling-symlink")]
        [InlineData("remove")]
        public async Task V2_any_change_to_a_copy_folder_sends_the_next_copy_beside_it(string change)
        {
            var root = Scratch("v2f296-");
            var sales = Path.Combine(root, "Sales");
            var copy = Path.Combine(root, "Sales backup");
            var outside = Path.Combine(root, "outside");
            try
            {
                using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(sales, "TMDL", false, "human");
                await engine.OpenAsync(sales);
                await engine.SaveAsync(copy, "TMDL", false, "agent");
                var model = Path.Combine(copy, "model.tmdl");
                var renamed = Path.Combine(copy, "model renamed.tmdl");
                var sub = Path.Combine(copy, "my empty folder");
                var link = Path.Combine(copy, "link");
                Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside");
                string sameSizeText = null;
                switch (change)
                {
                    case "rename": File.Move(model, renamed); break;
                    case "same-size":
                        var bytes = File.ReadAllBytes(model);
                        var i = Array.FindIndex(bytes, b => b == (byte)'m');
                        bytes[i] = (byte)'M';
                        File.WriteAllBytes(model, bytes);
                        sameSizeText = File.ReadAllText(model);
                        break;
                    case "empty-subfolder": Directory.CreateDirectory(sub); break;
                    case "file-symlink":
                        try { File.CreateSymbolicLink(link, Path.Combine(outside, "keep.txt")); } catch (Exception) { return; }   // Windows without the right
                        break;
                    case "dir-symlink":
                        try { Directory.CreateSymbolicLink(link, outside); } catch (Exception) { return; }
                        break;
                    case "dangling-symlink":
                        try { File.CreateSymbolicLink(link, Path.Combine(outside, "nothing-here.txt")); } catch (Exception) { return; }
                        break;
                    case "remove":
                        var victim = Directory.EnumerateFiles(copy, "*.tmdl", SearchOption.AllDirectories).First(f => !f.EndsWith("model.tmdl"));
                        File.Delete(victim);
                        break;
                }

                var again = await engine.SaveAsync(copy, "TMDL", false, "agent");
                Assert.NotEqual(Path.GetFullPath(copy), Path.GetFullPath(again.Path));
                Assert.True(File.Exists(Path.Combine(again.Path, "model.tmdl")));
                Assert.Contains(Path.GetFileName(again.Path), again.Note);
                Assert.True(Directory.Exists(copy), "the changed copy folder is gone");
                switch (change)
                {
                    case "rename": Assert.True(File.Exists(renamed)); Assert.False(File.Exists(model)); break;
                    case "same-size": Assert.Equal(sameSizeText, File.ReadAllText(model)); break;
                    case "empty-subfolder": Assert.True(Directory.Exists(sub)); break;
                    case "file-symlink": case "dir-symlink": case "dangling-symlink": Assert.NotNull(new FileInfo(link).LinkTarget); break;
                }
                Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "keep.txt")));
                Assert.Empty(Directory.GetFileSystemEntries(root, "*.semanticus-bak-*"));

                // The redirected copy is now a copy this session wrote: the next copy aimed at it refreshes it in place.
                var refresh = await engine.SaveAsync(again.Path, "TMDL", false, "agent");
                Assert.Equal(Path.GetFullPath(again.Path), Path.GetFullPath(refresh.Path));
            }
            finally { Delete(root); }
        }

        // fix-save-parent (17 Oct): a copy aimed at the folder that HOLDS the open model, or at a folder that holds another model, is
        // refused in one plain line with a place that works. Nothing is written, nothing is removed, and the happy paths still save.
        [Fact]
        public async Task A_copy_aimed_at_a_folder_that_holds_models_is_refused_with_a_place_that_works_and_removes_nothing()
        {
            var root = Scratch("up2-");
            var models = Path.Combine(root, "models");
            var sales = Path.Combine(models, "Sales");
            var finance = Path.Combine(models, "Finance");
            try
            {
                using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(sales, "TMDL", false, "human");
                await engine.SaveAsync(finance, "TMDL", false, "human");
                await engine.OpenAsync(sales);
                foreach (var origin in new[] { "agent", "human" })
                {
                    var holds = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SaveAsync(models, "TMDL", false, origin));
                    Assert.Contains("Nothing was saved", holds.Message);
                    Assert.Contains(Path.Combine(root, "Sales copy"), holds.Message);
                    Assert.DoesNotContain('\n', holds.Message);
                    Assert.DoesNotContain("—", holds.Message);
                }
                // A folder above the models holds them too.
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SaveModel(engine, root, "TMDL"));
                // A folder that holds ANOTHER model (not the open one) is refused for a copy as well.
                var other = Path.Combine(root, "elsewhere");
                await engine.SaveAsync(Path.Combine(other, "Marketing"), "TMDL", false, "human");
                await engine.OpenAsync(sales);
                var before = Snapshot2(root);
                var neighbour = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SaveModel(engine, other, "TMDL"));
                Assert.Contains("new or empty folder", neighbour.Message);
                // A copy onto another model's own folder is refused, too.
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SaveModel(engine, finance, "TMDL"));
                Assert.Equal(before, Snapshot2(root));

                // The place the line names works, and so does a plain save in place.
                var good = await McpTools.SaveModel(engine, Path.Combine(models, "Sales copy"), "TMDL");
                Assert.True(File.Exists(Path.Combine(good.Path, "model.tmdl")));
                Assert.Equal(sales, (await McpTools.SaveModel(engine, null, "TMDL")).Path);
                // An existing but empty folder is a fine place for a copy.
                var empty = Path.Combine(root, "empty"); Directory.CreateDirectory(empty);
                Assert.True(File.Exists(Path.Combine((await McpTools.SaveModel(engine, empty, "TMDL")).Path, "model.tmdl")));
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task A_save_inside_a_pbip_semantic_model_folder_is_refused_but_a_copy_beside_a_loose_bim_is_not()
        {
            var root = Scratch("nest3-");
            var envelope = Path.Combine(root, "proj", "Sales.SemanticModel");
            var looseBim = Path.Combine(root, "loose", "Sales.bim");
            try
            {
                using (var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
                {
                    await engine.OpenAsync(TestModels.FindBim());
                    await engine.SaveAsync(Path.Combine(envelope, "definition"), "TMDL", false, "human");
                    await engine.SaveAsync(looseBim, "bim", false, "human");
                }
                using (var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
                {
                    await engine.OpenAsync(envelope);
                    await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SaveModel(engine, Path.Combine(envelope, "copy"), "TMDL"));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SaveModel(engine, Path.Combine(envelope, "definition", "copy"), "TMDL"));
                    Assert.False(Directory.Exists(Path.Combine(envelope, "copy")));
                }
                using (var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
                {
                    await engine.OpenAsync(looseBim);
                    var beside = await McpTools.SaveModel(engine, Path.Combine(root, "loose", "Sales-backup.bim"), "bim");
                    Assert.True(File.Exists(beside.Path));
                }
            }
            finally { Delete(root); }
        }

        // A folder that already holds a nested copy (saved by an older build) opens as the top model, says where the copy is,
        // and keeps the copy whole through a save in place.
        [Fact]
        public async Task A_folder_that_already_holds_a_nested_copy_opens_as_the_top_model_and_names_the_copy()
        {
            var root = Scratch("nest4-");
            var tmdl = Path.Combine(root, "model");
            var nested = Path.Combine(tmdl, "Model-copy");
            try
            {
                using (var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
                {
                    await engine.OpenAsync(TestModels.FindBim());
                    await engine.SaveAsync(tmdl, "TMDL", false, "human");
                    // What an older build left behind: a whole second model inside the first.
                    await engine.SaveAsync(Path.Combine(root, "elsewhere"), "TMDL", false, "human");
                }
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "elsewhere"), "*", SearchOption.AllDirectories))
                {
                    var dest = Path.Combine(nested, Path.GetRelativePath(Path.Combine(root, "elsewhere"), file));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(file, dest);
                }
                using var reopen = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                var opened = await reopen.OpenAsync(tmdl);
                Assert.True(opened.Tables > 0);
                Assert.Equal(tmdl, opened.Source);
                Assert.Contains("Model-copy", opened.Note);
                Assert.DoesNotContain("\u2014", opened.Note);
                var saved = await McpTools.SaveModel(reopen, null, "TMDL");
                Assert.Equal(tmdl, saved.Path);
                Assert.True(File.Exists(Path.Combine(nested, "model.tmdl")));
                var again = await reopen.OpenAsync(tmdl);
                Assert.Equal(opened.Tables, again.Tables);
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task A_folder_save_whose_directory_move_hits_a_sharing_violation_tries_again_and_lands_whole()
        {
            var root = Scratch("wsv-");
            var tmdl = Path.Combine(root, "model");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(tmdl, "TMDL", false, "human");
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "7 + 7", "human");
                var tries = 0;
                engine.DirectoryMoveRetryDelaysForTest = new[] { 1, 1, 1, 1, 1, 1 };
                engine.DirectoryMoveHookForTest = (_, _) =>
                {
                    if (++tries <= 2) throw new IOException("The process cannot access the file because it is being used by another process.");
                };

                await engine.SaveAsync(null, "TMDL", false, "human");

                Assert.True(tries > 2, "the move was never tried again");
                Assert.Contains("7 + 7", Snapshot(tmdl), StringComparison.Ordinal);
                Assert.False((await engine.SessionInfoAsync()).HasUnsavedChanges);
                Assert.Empty(Directory.GetDirectories(root).Where(d => d.Contains(".semanticus-")));
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task A_folder_save_whose_directory_move_never_lands_puts_the_original_tree_back_and_says_why()
        {
            var root = Scratch("wsf-");
            var tmdl = Path.Combine(root, "model");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(tmdl, "TMDL", false, "human");
                var before = Snapshot(tmdl);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "8 + 8", "human");
                engine.DirectoryMoveRetryDelaysForTest = new[] { 1, 1, 1 };
                // Only the move that puts the new tree in place fails, every time; the move that puts the old tree back is allowed.
                engine.DirectoryMoveHookForTest = (from, _) =>
                {
                    if (Path.GetFileName(from).StartsWith(".semanticus-save-", StringComparison.Ordinal))
                        throw new IOException("The process cannot access the file because it is being used by another process.");
                };

                var ex = await Assert.ThrowsAsync<IOException>(() => engine.SaveAsync(null, "TMDL", false, "human"));

                Assert.Contains("being used by another process", ex.Message, StringComparison.Ordinal);
                Assert.Equal(before, Snapshot(tmdl));
                Assert.DoesNotContain("8 + 8", Snapshot(tmdl), StringComparison.Ordinal);
                Assert.True((await engine.SessionInfoAsync()).HasUnsavedChanges);
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task D062_save_broadcasts_so_the_unsaved_marker_can_clear()
        {
            var root = Scratch("d062-");
            var bim = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(bim);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "3 + 3", "human");
                Assert.True((await engine.SessionInfoAsync()).HasUnsavedChanges);

                var seen = 0;
                Action<ChangeNotification> handler = n => { if (n.Label == "save") seen++; };
                sessions.Bus.Changed += handler;
                try
                {
                    await engine.SaveAsync(null, "TMDL", false, "human");
                }
                finally { sessions.Bus.Changed -= handler; }

                Assert.True(seen >= 1);
                Assert.False((await engine.SessionInfoAsync()).HasUnsavedChanges);
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task D047_sidecars_sit_beside_a_SemanticModel_folder_even_without_pbism()
        {
            var root = Scratch("d047-");
            var sem = Path.Combine(root, "LaneA.SemanticModel");
            var def = Path.Combine(sem, "definition");
            Directory.CreateDirectory(def);
            File.WriteAllText(Path.Combine(def, "model.tmdl"), "model\n\tref table Date\n");
            File.WriteAllText(Path.Combine(def, "database.tmdl"), "database\n\tcompatibilityLevel: 1604\n");
            try
            {
                Assert.True(ModelPathResolver.IsPbipDefinitionFolder(def));
                var sidecar = LayoutStore.DirFor(def);
                Assert.Equal(Path.Combine(sem, ".semanticus"), sidecar);
                Assert.DoesNotContain(Path.Combine("definition", ".semanticus"), sidecar);
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task D117_sidecars_travel_when_the_model_is_saved_to_a_new_folder()
        {
            var root = Scratch("d117-");
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            using var engine = new LocalEngine(new SessionManager(), new Pro());
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(first, "TMDL", false, "human");
                var primer = PrimerMarkdown("Travel with the model.");
                var savedPrimer = await engine.SetPrimerAsync(primer, "human");
                Assert.True(savedPrimer.Exists);
                await engine.SaveWorkflowAsync("uat-review", @"---
name: uat-review
title: UAT review
---
## Step 1: Read
Read the model.

## Step 2: Done
Finish up.
", "human");

                await engine.SaveAsync(second, "TMDL", false, "human");
                await engine.OpenAsync(second);

                var moved = await engine.GetPrimerAsync();
                Assert.True(moved.Exists);
                Assert.Contains("Travel with the model.", moved.Markdown, StringComparison.Ordinal);
                Assert.StartsWith(second, moved.FilePath, StringComparison.OrdinalIgnoreCase);
                var workflows = await engine.ListWorkflowsAsync();
                Assert.Contains(workflows, w => w.Name == "uat-review");
            }
            finally { Delete(root); }
        }

        // Kane 8 Oct: a blank commit message is never a refusal. A default is filled in, on both doors, so saving a
        // version never stops at a text box.
        [Theory]
        [InlineData("human", "")]
        [InlineData("agent", "   ")]
        public async Task A_blank_commit_message_gets_a_default_and_commits(string origin, string message)
        {
            var root = Scratch("blankmsg-");
            var bim = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            InitRepo(root);
            Git(root, "add", "--", "model.bim");
            Git(root, "commit", "-q", "-m", "initial");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(bim);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "5 + 5", "human");

                var committed = await engine.GitCommitAsync(message, null, commit: true, origin);

                Assert.True(committed.Committed, committed.Error);
                Assert.Equal("Saved from Semanticus", Git(root, "log", "-1", "--format=%s").Trim());
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task D127_engine_telemetry_does_not_block_checkout()
        {
            var root = Scratch("d127-");
            var bim = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            InitRepo(root);
            Git(root, "add", "--", "model.bim");
            Git(root, "commit", "-q", "-m", "initial");
            Git(root, "branch", "other");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(bim);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "4 + 4", "human");
                Directory.CreateDirectory(Path.Combine(root, ".semanticus", "baselines"));
                File.WriteAllText(Path.Combine(root, ".semanticus", "experience.jsonl"), "{\"kind\":\"open\"}\n");
                File.WriteAllText(Path.Combine(root, ".semanticus", "baselines", "vitals.jsonl"), "{\"kind\":\"vitals\"}\n");

                var committed = await engine.GitCommitAsync("model save", null, commit: true, "human");
                Assert.True(committed.Committed, committed.Error);
                Assert.DoesNotContain(committed.Files ?? Array.Empty<string>(), f => f.Replace('\\', '/').Contains("experience.jsonl"));
                Assert.DoesNotContain(committed.Files ?? Array.Empty<string>(), f => f.Replace('\\', '/').Contains("vitals.jsonl"));

                File.AppendAllText(Path.Combine(root, ".semanticus", "experience.jsonl"), "{\"kind\":\"edit\"}\n");
                var checkout = await engine.GitCheckoutAsync("other", "human");
                Assert.True(checkout.Ok, checkout.Error);
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task D089_checkpoint_includes_new_model_folders_and_primer()
        {
            var root = Scratch("d089-");
            var tmdl = Path.Combine(root, "LaneA.SemanticModel", "definition");
            using var engine = new LocalEngine(new SessionManager(), new Pro());
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(tmdl, "TMDL", false, "human");
                InitRepo(root);
                Git(root, "add", "-A", "--", "LaneA.SemanticModel/definition");
                Git(root, "commit", "-q", "-m", "initial");

                await engine.CreateRoleAsync("HonestyRole", "Read", "human");
                await engine.CreatePerspectiveAsync("HonestyView", "human");
                await engine.SetPrimerAsync(PrimerMarkdown("Keep me."), "human");

                var made = await engine.CreateHistoryCheckpointAsync("With folders", commit: true, "human");
                Assert.Null(made.Error);
                Assert.True(made.Committed, made.Note);
                var names = Git(root, "show", "--name-only", "--pretty=format:", made.Checkpoint.Hash)
                    .Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var listed = string.Join(" | ", names);
                var onDisk = string.Join(" | ", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')));
                Assert.True(listed.IndexOf("primers", StringComparison.OrdinalIgnoreCase) >= 0, "commit=" + listed + " disk=" + onDisk);
                if (onDisk.IndexOf("/roles/", StringComparison.OrdinalIgnoreCase) >= 0 || onDisk.IndexOf("/roles\\", StringComparison.OrdinalIgnoreCase) >= 0)
                    Assert.True(listed.IndexOf("roles", StringComparison.OrdinalIgnoreCase) >= 0, listed);
                if (onDisk.IndexOf("perspective", StringComparison.OrdinalIgnoreCase) >= 0)
                    Assert.True(listed.IndexOf("perspective", StringComparison.OrdinalIgnoreCase) >= 0, listed);
            }
            finally { Delete(root); }
        }

        /// <summary>CicdSmoke's "git_diff: clean working tree after the commit" failed about one run in two (verify-askdata,
        /// 26 Sep, on the base and the head alike): git_diff found ".semanticus/.gitignore" not yet committed. An edit
        /// schedules the History's write in the background (EditLedger), and that write is what first creates the
        /// sidecar and its .gitignore. git_commit staged the model while the write was still under way, so the file
        /// landed a moment after the commit. The write is held back here (EditLedger.WriteDelayMsForTest, before it takes
        /// any lock) so the race always goes the wrong way; a commit must settle the engine's own pending writes before
        /// it stages anything.</summary>
        [Fact]
        public async Task A_commit_waits_for_the_history_write_so_the_tree_is_clean_after_it()
        {
            var root = Scratch("commit-settles-");
            var model = Path.Combine(root, "Sales.SemanticModel");
            var ignore = Path.Combine(model, ".semanticus", ".gitignore");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Pro());
            try
            {
                InitRepo(root);
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(model, "TMDL", false, "human");
                var first = await engine.GitCommitAsync("initial model", null, commit: true, "agent");
                Assert.True(first.Committed, first.Error ?? first.Note);
                await sessions.Ledger.FlushAsync();
                // Whether the first save already left a sidecar behind differs by machine (it did on one fleet laptop
                // and not on this one), so the edit always starts from none: the History's next write makes it again.
                var sidecar = Path.GetDirectoryName(ignore);
                if (Directory.Exists(sidecar))
                {
                    Directory.Delete(sidecar, true);
                    if (Git(root, "status", "--porcelain").Length > 0) { Git(root, "add", "-A"); Git(root, "commit", "-q", "-m", "start with no sidecar"); }
                }
                Assert.False(File.Exists(ignore), "the sidecar existed before the edit, so this test proves nothing");

                EditLedger.WriteDelayMsForTest = 1500;
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDescriptionAsync(measure.Ref, "Edited for the commit race.", "agent");
                var second = await engine.GitCommitAsync("describe a measure", null, commit: true, "agent");
                Assert.True(second.Committed, second.Error ?? second.Note);

                // Read the tree once the engine's own writes have landed, the way the smoke's next call did.
                await sessions.Ledger.FlushAsync();
                Assert.True(File.Exists(ignore), "the edit's History write made no sidecar, so the race was not forced");
                var diff = await engine.GitDiffAsync(null, false);
                Assert.True(diff.Empty, "the working tree is not clean after the commit:\n" + diff.Text);
            }
            finally
            {
                EditLedger.WriteDelayMsForTest = 0;
                engine.Dispose();
                Delete(root);
            }
        }

        /// <summary>UAT-S08-2-004. A PBIP model opens at its definition folder, but its notes and plan memory live one
        /// level up in the .SemanticModel folder. git_commit's preview listed the modified plan memory, then the commit
        /// staged only the definition folder and answered "working tree clean" with the file still modified and no
        /// commit made. The commit must take what the preview listed.</summary>
        [Fact]
        public async Task UAT_S08_2_004_git_commit_commits_the_model_notes_its_preview_listed()
        {
            var root = Scratch("uat-s08-2-004-");
            var tmdl = Path.Combine(root, "Contoso.SemanticModel", "definition");
            var memory = Path.Combine(root, "Contoso.SemanticModel", ".semanticus", "plan-memory.json");
            using var engine = new LocalEngine(new SessionManager(), new Pro());
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(tmdl, "TMDL", false, "human");
                await engine.OpenAsync(tmdl);
                Directory.CreateDirectory(Path.GetDirectoryName(memory));
                File.WriteAllText(memory, "{\"version\":1}\n");
                InitRepo(root);
                Git(root, "add", "-A");
                Git(root, "commit", "-q", "-m", "baseline");
                File.WriteAllText(memory, "{\"version\":1,\"skipped\":[\"item\"]}\n");

                var preview = await engine.GitCommitAsync("plan memory", null, commit: false, "agent");
                Assert.Contains(preview.Files, f => f.Replace('\\', '/').EndsWith(".semanticus/plan-memory.json", StringComparison.Ordinal));

                var committed = await engine.GitCommitAsync("plan memory", null, commit: true, "agent");
                Assert.True(committed.Committed, committed.Note ?? committed.Error);
                Assert.Contains(committed.Files, f => f.Replace('\\', '/').EndsWith(".semanticus/plan-memory.json", StringComparison.Ordinal));
                Assert.Equal("", Git(root, "status", "--porcelain", "--", "Contoso.SemanticModel/.semanticus/plan-memory.json").Trim());
                Assert.Equal("2", Git(root, "rev-list", "--count", "HEAD").Trim());
            }
            finally { Delete(root); }
        }

        /// <summary>UAT-S11-2-004, a variant of UAT-S08-2-004 (Sol's retest on 0ee45add). git_commit with commit=false and
        /// one explicit file listed every changed file, an untracked insights file included, although the commit stages
        /// only the files it was given. The preview must name exactly what that commit would take.</summary>
        [Fact]
        public async Task UAT_S11_2_004_git_commit_preview_honours_the_explicit_files()
        {
            var root = Scratch("uat-s11-2-004-");
            var bim = Path.Combine(root, "model.bim");
            var memory = Path.Combine(root, ".semanticus", "plan-memory.json");
            var insights = Path.Combine(root, ".semanticus", "knowledge", "insights.jsonl");
            File.Copy(TestModels.FindBim(), bim);
            Directory.CreateDirectory(Path.GetDirectoryName(memory));
            File.WriteAllText(memory, "{\"version\":1}\n");
            InitRepo(root);
            Git(root, "add", "-A");
            Git(root, "commit", "-q", "-m", "baseline");
            using var engine = new LocalEngine(new SessionManager(), new Pro());
            try
            {
                await engine.OpenAsync(bim);
                // Sol's state: the plan memory is modified and an insights file is new and untracked.
                File.WriteAllText(memory, "{\"version\":1,\"skipped\":[\"item\"]}\n");
                Directory.CreateDirectory(Path.GetDirectoryName(insights));
                File.WriteAllText(insights, "{\"op\":\"add\"}\n");

                var only = new[] { ".semanticus/plan-memory.json" };
                var preview = await McpTools.GitCommit(engine, "plan memory", only, commit: false);
                Assert.Null(preview.Error);
                Assert.Equal(only, preview.Files.Select(f => f.Replace('\\', '/')).ToArray());

                var committed = await McpTools.GitCommit(engine, "plan memory", only, commit: true);
                Assert.True(committed.Committed, committed.Note ?? committed.Error);
                Assert.Equal(preview.Files, committed.Files);   // the preview named exactly what the commit took
                Assert.Contains("insights.jsonl", Git(root, "status", "--porcelain", "--untracked-files=all"));
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task D096_checkpoint_preview_names_the_files_that_will_be_saved()
        {
            var root = Scratch("d096-");
            var bim = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            InitRepo(root);
            Git(root, "add", "--", "model.bim");
            Git(root, "commit", "-q", "-m", "initial");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(bim);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "5 + 5", "human");
                var preview = await engine.CreateHistoryCheckpointAsync("Will save", commit: false, "human");
                Assert.True(preview.Preview);
                Assert.True(preview.SavedModelFirst);
                Assert.NotEmpty(preview.Files);
                Assert.Contains(preview.Files, f => f.Replace('\\', '/').Contains("model.bim"));
                Assert.DoesNotContain("No file delta", preview.Note ?? "", StringComparison.OrdinalIgnoreCase);
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task D093_restore_reopens_the_session_and_broadcasts()
        {
            var root = Scratch("d093-");
            var bim = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            InitRepo(root);
            Git(root, "add", "--", "model.bim");
            Git(root, "commit", "-q", "-m", "initial");
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(bim);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "1", "human");
                var first = await engine.CreateHistoryCheckpointAsync("Known good", commit: true, "human");
                await engine.SetDaxAsync(measure.Ref, "2", "human");
                await engine.CreateHistoryCheckpointAsync("Later", commit: true, "human");

                var seen = 0;
                Action<ChangeNotification> handler = n => { if (n.Label == "restore") seen++; };
                sessions.Bus.Changed += handler;
                HistoryRestoreResult restored;
                try { restored = await engine.RestoreHistoryCheckpointAsync(first.Checkpoint.Hash, restore: true, "human"); }
                finally { sessions.Bus.Changed -= handler; }

                Assert.Null(restored.Error);
                Assert.True(restored.Restored);
                Assert.True(seen >= 1);
                Assert.Equal("1", (await engine.ListMeasuresAsync()).First(m => m.Ref == measure.Ref).Expression);
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task D139_restore_flag_stays_true_when_the_follow_up_checkpoint_fails()
        {
            var root = Scratch("d139-");
            var bim = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            InitRepo(root);
            Git(root, "add", "--", "model.bim");
            Git(root, "commit", "-q", "-m", "initial");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(bim);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "1", "human");
                var first = await engine.CreateHistoryCheckpointAsync("Known good", commit: true, "human");
                await engine.SetDaxAsync(measure.Ref, "2", "human");
                await engine.CreateHistoryCheckpointAsync("Later", commit: true, "human");

                engine.FailCheckpointCommitMatchingForTest = "Restored to";
                var restored = await McpTools.RestoreHistoryCheckpoint(engine, first.Checkpoint.Hash, restore: true);
                Assert.True(restored.Restored);
                Assert.False(string.IsNullOrEmpty(restored.Error));
                Assert.Contains("restored", restored.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("not committed", restored.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("1", (await engine.ListMeasuresAsync()).First(m => m.Ref == measure.Ref).Expression);
            }
            finally { Delete(root); }
        }

        // ---------------- round 4 repairs (D-024, D-031, D-228) ----------------

        /// <summary>Build a real PBIP on disk the way the engine itself writes one: a TMDL save into
        /// &lt;name&gt;.SemanticModel/definition, plus the two envelope files a project carries. Returns the .pbip path.</summary>
        private static async Task<(string Pbip, string Definition)> MakePbipAsync(string root)
        {
            // Save As from the fixture, so the tree is one the engine itself would write, then add the two
            // envelope files a Power BI project carries and open the .pbip the way a person would.
            var bim = Path.Combine(root, "seed.bim");
            File.Copy(TestModels.FindBim(), bim);
            var proj = Path.Combine(root, "proj");
            var sem = Path.Combine(proj, "Model.SemanticModel");
            var def = Path.Combine(sem, "definition");
            Directory.CreateDirectory(proj);
            using (var seed = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
            {
                await seed.OpenAsync(bim);
                await seed.SaveAsync(def, "TMDL", false, "human");
            }
            File.WriteAllText(Path.Combine(sem, "definition.pbism"), "{\"version\":\"1.0\",\"settings\":{}}");
            var pbip = Path.Combine(proj, "Model.pbip");
            File.WriteAllText(pbip, "{\"version\":\"1.0\",\"artifacts\":[]}");
            return (pbip, def);
        }

        [Fact]
        public async Task D031b_an_external_edit_to_a_non_tmdl_file_in_the_model_folder_is_seen_and_refused()
        {
            // SAVE-08: the round-4 lane edited a PBIP model outside the engine, then pressed Save Model and the
            // engine overwrote it without asking. The guard below already exists; it missed the edit because the
            // disk stamp only hashed *.tmdl under the definition folder, so anything else in the tree (a diagram
            // layout, definition.pbism, a hand-kept note) was invisible. The model folder is the unit the save
            // replaces, so the whole tree is the unit the guard must watch.
            var root = Scratch("d031b-");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            try
            {
                var (pbip, def) = await MakePbipAsync(root);
                File.WriteAllText(Path.Combine(def, "diagramLayout.json"), "{\"version\":1}");
                await engine.OpenAsync(pbip);
                Assert.False((await engine.SessionInfoAsync()).DiskDiverged);

                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "1 + 1", "human");
                File.WriteAllText(Path.Combine(def, "diagramLayout.json"), "{\"version\":2,\"edited\":\"outside\"}");

                Assert.True((await engine.SessionInfoAsync()).DiskDiverged);
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SaveAsync(null, "TMDL", false, "human"));
                Assert.Contains("files on disk changed", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("\"edited\"", File.ReadAllText(Path.Combine(def, "diagramLayout.json")), StringComparison.Ordinal);

                var saved = await engine.SaveAsync(null, "TMDL", true, "human");
                Assert.Equal(def, saved.Path);
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task D228_a_folder_save_keeps_files_it_did_not_write()
        {
            // Round 4, JOURNEY-07 s6b: an unrelated file under definition/ was deleted by Save Model with no
            // mention. The save writes a fresh temp tree and swaps it over the target, so anything the serializer
            // did not write disappears with the old tree. Only the model definition is the engine's to rewrite.
            var root = Scratch("d228-");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            try
            {
                var (pbip, def) = await MakePbipAsync(root);
                var keep = Path.Combine(def, "README-notes.txt");
                File.WriteAllText(keep, "hand written, not mine to delete");
                var nested = Path.Combine(def, "notes", "deep.md");
                Directory.CreateDirectory(Path.GetDirectoryName(nested));
                File.WriteAllText(nested, "nested note");

                await engine.OpenAsync(pbip);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "2 + 2", "human");
                await engine.SaveAsync(null, "TMDL", true, "human");

                Assert.True(File.Exists(keep));
                Assert.Equal("hand written, not mine to delete", File.ReadAllText(keep));
                Assert.True(File.Exists(nested));
                Assert.Equal("nested note", File.ReadAllText(nested));
                // The model itself still round-trips through its own definition files.
                Assert.Contains("2 + 2", string.Join("\n", Directory.EnumerateFiles(def, "*.tmdl", SearchOption.AllDirectories).Select(File.ReadAllText)), StringComparison.Ordinal);
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task D024c_a_preservation_copy_failure_refuses_before_replacing_the_original_tree()
        {
            // A broken link is an unreadable, non-definition file. The old preservation pass swallowed the
            // File.Copy error, then swapped a tree that had already lost the link. The save must refuse and leave
            // the model plus the unreadable file in place. This reproduction is Linux-only because Windows may
            // refuse creating the link before the save path is reached.
            if (!OperatingSystem.IsLinux()) return;
            var root = Scratch("d024c-");
            var folder = Path.Combine(root, "model");
            var bim = Path.Combine(root, "seed.bim");
            File.Copy(TestModels.FindBim(), bim);
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(bim);
                await engine.SaveAsync(folder, "TMDL", false, "human");
                var measure = (await engine.ListMeasuresAsync()).First();
                var unreadable = Path.Combine(folder, "notes", "missing.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(unreadable));
                File.CreateSymbolicLink(unreadable, Path.Combine(folder, "notes", "does-not-exist.txt"));
                var before = SnapshotTmdl(folder);

                await engine.SetDaxAsync(measure.Ref, "11 + 11", "human");
                var ex = await Assert.ThrowsAsync<IOException>(() => engine.SaveAsync(null, "TMDL", true, "human"));

                Assert.Contains("preserve", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, SnapshotTmdl(folder));
                Assert.True((File.GetAttributes(unreadable) & FileAttributes.ReparsePoint) != 0);
                Assert.True((await engine.SessionInfoAsync()).HasUnsavedChanges);
                Assert.Equal("11 + 11", (await engine.ListMeasuresAsync()).First(m => m.Ref == measure.Ref).Expression);
            }
            finally { Delete(root); }
        }

        [Theory]
        [InlineData("TMDL")]
        [InlineData("folder")]
        public async Task D228b_standalone_folder_save_preserves_sidecars_and_repository(string format)
        {
            // A standalone model owns its .semanticus data and may also be the root of a Git repository. Both are
            // inside the folder the staged save replaces, so both TMDL and JSON folder saves must carry them over.
            var root = Scratch("d228b-");
            var folder = Path.Combine(root, format == "TMDL" ? "tmdl-model" : "json-model");
            var bim = Path.Combine(root, "seed.bim");
            File.Copy(TestModels.FindBim(), bim);
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(bim);
                await engine.SaveAsync(folder, format, false, "human");
                var sidecar = LayoutStore.DirFor(folder);
                LayoutStore.Write(folder, new[] { new LayoutStore.Entry { Name = "Sales", X = 17, Y = 29 } });
                Directory.CreateDirectory(Path.Combine(sidecar, "primers"));
                Directory.CreateDirectory(Path.Combine(sidecar, "workflows"));
                File.WriteAllText(Path.Combine(sidecar, "primers", "model.md"), "primer survives");
                File.WriteAllText(Path.Combine(sidecar, "workflows", "review.md"), "workflow survives");
                InitRepo(folder);
                Git(folder, "add", "-A");
                Git(folder, "commit", "-q", "-m", "baseline");
                var beforeHead = Git(folder, "rev-parse", "HEAD");

                await engine.OpenAsync(folder);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "12 + 12", "human");
                await engine.SaveAsync(folder, format, true, "human");

                Assert.Equal(beforeHead, Git(folder, "rev-parse", "HEAD"));
                Assert.Equal(Path.GetFullPath(folder), Path.GetFullPath(Git(folder, "rev-parse", "--show-toplevel")));
                Assert.Contains("17", File.ReadAllText(Path.Combine(sidecar, "layout.json")), StringComparison.Ordinal);
                Assert.Equal("primer survives", File.ReadAllText(Path.Combine(sidecar, "primers", "model.md")));
                Assert.Equal("workflow survives", File.ReadAllText(Path.Combine(sidecar, "workflows", "review.md")));
                Assert.Contains("12 + 12", string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(p => p.EndsWith(".tmdl", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .Select(File.ReadAllText)), StringComparison.Ordinal);
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task D024b_a_failed_bim_write_leaves_the_last_good_model_on_disk()
        {
            // FAIL-03: with the disk full, the engine's .bim save truncated the model file and then failed,
            // leaving the last good model destroyed. The write goes straight onto the model file, so the
            // old bytes are gone before the new ones land. Linux proves it for real with a write-size limit
            // (the kernel refuses bytes past it, exactly as a full disk does); the model is far bigger than
            // the limit, so the failure lands mid-file.
            if (!OperatingSystem.IsLinux())
            {
                // No portable way to make one write fail part-way on Windows: a share violation or a
                // read-only file is refused BEFORE the file is truncated, so it cannot reproduce this loss.
                // The Linux leg of this suite is the failing-first proof; this leg only re-asserts the contract.
                var skipRoot = Scratch("d024b-skip-");
                try
                {
                    var skipBim = Path.Combine(skipRoot, "model.bim");
                    File.Copy(TestModels.FindBim(), skipBim);
                    using var skipEngine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                    var before = File.ReadAllBytes(skipBim);
                    await skipEngine.OpenAsync(skipBim);
                    var m = (await skipEngine.ListMeasuresAsync()).First();
                    await skipEngine.SetDaxAsync(m.Ref, "4 + 4", "human");
                    await skipEngine.SaveAsync(null, "BIM", false, "human");
                    Assert.NotEqual(before.Length, new FileInfo(skipBim).Length);   // the save did land
                }
                finally { Delete(skipRoot); }
                return;
            }

            var root = Scratch("d024b-");
            var bim = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            var original = File.ReadAllBytes(bim);
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(bim);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "9 + 9", "human");

                Exception failure = null;
                using (WriteSizeLimit.Cap(400 * 1024))
                {
                    try { await engine.SaveAsync(null, "BIM", false, "human"); }
                    catch (Exception ex) { failure = ex; }
                }

                Assert.NotNull(failure);                                          // the write did fail
                Assert.Equal(original.Length, new FileInfo(bim).Length);          // the old bytes were not cut short
                Assert.Equal(original, File.ReadAllBytes(bim));                   // and the last good model survived
                Assert.True((await engine.SessionInfoAsync()).HasUnsavedChanges);  // the edits are still unsaved
                Assert.Equal("9 + 9", (await engine.ListMeasuresAsync()).First(m => m.Ref == measure.Ref).Expression);
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task SAVE01_a_saved_model_reopens_from_disk_with_the_saved_content()
        {
            // SAVE-01 s4-reopen-disk-copy: the round trip the save path exists for. Whatever the save does
            // internally, a fresh engine opening the same path must serve exactly what was written.
            var root = Scratch("save01-");
            var bim = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(bim);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "6 * 7", "human");
                await engine.SaveAsync(null, "BIM", false, "human");
                Assert.False((await engine.SessionInfoAsync()).HasUnsavedChanges);

                using var reopened = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                await reopened.OpenAsync(bim);
                Assert.False((await reopened.SessionInfoAsync()).HasUnsavedChanges);
                Assert.Equal("6 * 7", (await reopened.ListMeasuresAsync()).First(m => m.Ref == measure.Ref).Expression);
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task SAVE05_a_project_entry_point_opens_from_all_three_natural_paths()
        {
            // SAVE-05: opening a .pbip was accepted and then failed to load. A project is reached three ways in
            // practice, so all three are pinned against a tree shaped like a real one: a report beside the model,
            // the report's own pointer back at it, and diagramLayout.json inside the model folder.
            var root = Scratch("save05-");
            var (pbip, def) = await MakePbipAsync(root);
            var proj = Path.GetDirectoryName(pbip);
            var sem = Path.GetDirectoryName(def);
            var report = Path.Combine(proj, "Model.Report");
            Directory.CreateDirectory(report);
            File.WriteAllText(Path.Combine(report, "definition.pbir"),
                "{\"version\":\"1.0\",\"datasetReference\":{\"byPath\":{\"path\":\"../Model.SemanticModel\"}}}");
            File.WriteAllText(Path.Combine(report, "report.json"), "{\"config\":{}}");
            File.WriteAllText(Path.Combine(sem, "diagramLayout.json"), "{\"version\":1,\"diagrams\":[]}");
            try
            {
                foreach (var entry in new[] { pbip, sem, proj })
                {
                    using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                    var opened = await engine.OpenAsync(entry);
                    Assert.False(string.IsNullOrEmpty(opened.SessionId), entry);
                    // Whichever door was used, the model that comes up is the project's own model.
                    Assert.True((await engine.SessionInfoAsync()).Tables > 0, entry);
                }
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task SAVE04_a_freshly_opened_model_is_clean_and_the_two_doors_agree()
        {
            // SAVE-04: a model that was just opened read as unsaved. Every local shape is pinned here, and both
            // doors are read for the same three facts, so a door that drifts is caught rather than assumed.
            // NOTE: the round-4 repro was a LIVE model. This box has no XMLA endpoint and no Power BI Desktop,
            // so that leg cannot run here and is not claimed. See the result for what was proven instead.
            var root = Scratch("save04-");
            var bim = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            var folder = Path.Combine(root, "tmdl");
            var (pbip, def) = await MakePbipAsync(root);
            using (var seed = new LocalEngine(new SessionManager(), TestEntitlements.Pro))
            {
                await seed.OpenAsync(bim);
                await seed.SaveAsync(folder, "TMDL", false, "human");
            }
            try
            {
                foreach (var path in new[] { bim, folder, pbip, def })
                {
                    using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                    var rpc = new EngineRpcTarget(engine);
                    await engine.OpenAsync(path);

                    async Task Check(string when)
                    {
                        var direct = await engine.SessionInfoAsync();
                        var overRpc = await rpc.sessionInfo();
                        var overMcp = await McpTools.ModelOverview(engine);
                        Assert.False(direct.HasUnsavedChanges, $"{path} {when}: engine says unsaved");
                        Assert.False(direct.DiskDiverged, $"{path} {when}: engine says the files on disk changed");
                        Assert.False(overRpc.HasUnsavedChanges, $"{path} {when}: the editor door says unsaved");
                        Assert.False(overRpc.DiskDiverged, $"{path} {when}: the editor door says the files on disk changed");
                        Assert.False(overMcp.HasUnsavedChanges, $"{path} {when}: the agent door says unsaved");
                        Assert.False(overMcp.DiskDiverged, $"{path} {when}: the agent door says the files on disk changed");
                    }

                    await Check("right after open");
                    // The read-only work the editors do the moment a model opens. None of it is an edit.
                    await engine.ListTreeAsync(null);
                    await engine.GetLayoutAsync();
                    await Check("after the opening read-only work");
                }
            }
            finally { Delete(root); }
        }

        [Fact]
        public async Task D240b_an_out_of_range_window_is_refused_instead_of_silently_wrapping()
        {
            // The Save-policy case: an out of range count. The day arithmetic multiplies by 365 as a 32-bit int, so
            // a huge count wraps: 5,900,000 years comes out as a negative number of days. The store window then
            // reads as NARROWER than a 10 day refresh window, and the save is refused with a message that
            // contradicts the numbers on the screen ("the refresh window is wider than the store window",
            // when the person just typed a store window millions of years wide).
            var root = Scratch("d240b-");
            var bim = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            try
            {
                await engine.OpenAsync(bim);
                await engine.SetCompatibilityLevelAsync(1604, "human");   // refresh policies need 1450 or higher
                await engine.SaveAsync(null, "BIM", false, "human");                      // clean baseline: nothing unsaved but the policy
                var table = (await engine.ListTreeAsync(null)).First(n => n.Kind == "table").Ref;

                // The pure check first: a store window millions of years wide plainly covers a 10 day refresh
                // window. It reads as NOT covering it today, because the day count wrapped negative.
                Assert.True(LocalEngine.RefreshWindowFitsArchive(5_900_000,
                    TabularEditor.TOMWrapper.RefreshGranularityType.Year, 10,
                    TabularEditor.TOMWrapper.RefreshGranularityType.Day));

                // And a count no calendar can express is refused outright, in plain words, rather than wrapping.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SetIncrementalRefreshPolicyAsync(
                    table, null, 5_900_000, "Year", 10, "Day", null, null, null, false, "human"));
                Assert.Contains("window", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("\u2014", ex.Message);

                // And the model is untouched: a refused policy is not a half-written one.
                Assert.False((await engine.GetIncrementalRefreshPolicyAsync(table)).Enabled);
                Assert.False((await engine.SessionInfoAsync()).HasUnsavedChanges);
            }
            finally { Delete(root); }
        }

        /// <summary>Cap the bytes one file may reach, for real: the kernel then refuses a write past the cap
        /// exactly as a full disk does (EFBIG), leaving whatever was written so far. SIGXFSZ would otherwise kill
        /// the process, so it is ignored for the same window. Process-wide, so the window is one save and nothing
        /// else: the cap sits far above every other write this suite makes.</summary>
        private sealed class WriteSizeLimit : IDisposable
        {
            private const int RLimitFsize = 1;
            private const int SigXfsz = 25;
            private readonly RLimit _restore;

            private WriteSizeLimit(RLimit restore) { _restore = restore; }

            public static WriteSizeLimit Cap(long bytes)
            {
                var old = new RLimit();
                getrlimit(RLimitFsize, ref old);
                signal(SigXfsz, new IntPtr(1));   // SIG_IGN, a refusal and not a death
                var capped = new RLimit { Cur = (ulong)bytes, Max = old.Max };
                setrlimit(RLimitFsize, ref capped);
                return new WriteSizeLimit(old);
            }

            public void Dispose()
            {
                var old = _restore;
                setrlimit(RLimitFsize, ref old);
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct RLimit { public ulong Cur; public ulong Max; }

            [DllImport("libc", SetLastError = true)] private static extern int setrlimit(int resource, ref RLimit rlim);
            [DllImport("libc", SetLastError = true)] private static extern int getrlimit(int resource, ref RLimit rlim);
            [DllImport("libc")] private static extern IntPtr signal(int signum, IntPtr handler);
        }

        // The edit ledger's file (.semanticus/history.json, Plan-heart E4) is left out: it is this machine's record of
        // edits, rewritten after every edit and git-ignored, not part of the saved model these tests compare. The
        // .semanticus/.gitignore that ledger writes in the background is left out for the same reason (it lands on its
        // own timing, so a snapshot taken before and after a save could differ by that one file).
        private static string Snapshot(string dir) => string.Join("\n",
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(p => !Path.GetFileName(p).StartsWith(EditLedger.FileName, StringComparison.Ordinal))
                .Where(p => !(Path.GetFileName(p) == ".gitignore" && Path.GetFileName(Path.GetDirectoryName(p)) == ".semanticus"))
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/') + ":" + File.ReadAllText(p)));

        private static string SnapshotTmdl(string dir) => string.Join("\n",
            Directory.EnumerateFiles(dir, "*.tmdl", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/') + ":" + File.ReadAllText(p)));
    }
}
