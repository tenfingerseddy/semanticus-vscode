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
                await engine.SaveAsync(tmdl, "TMDL");
                var before = Snapshot(tmdl);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "9 + 9", "human");
                engine.FailFolderSaveBeforeSwapForTest = new IOException("forced mid-save failure");

                var ex = await Assert.ThrowsAsync<IOException>(() => engine.SaveAsync(null, "TMDL"));
                Assert.Contains("forced mid-save failure", ex.Message, StringComparison.Ordinal);
                Assert.Equal(before, Snapshot(tmdl));
                Assert.DoesNotContain("9 + 9", Snapshot(tmdl), StringComparison.Ordinal);
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
                    await engine.SaveAsync(null, "TMDL");
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
                await engine.SaveAsync(first, "TMDL");
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

                await engine.SaveAsync(second, "TMDL");
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
                await engine.SaveAsync(tmdl, "TMDL");
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
                await seed.SaveAsync(def, "TMDL");
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
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SaveAsync(null, "TMDL"));
                Assert.Contains("files on disk changed", ex.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("\"edited\"", File.ReadAllText(Path.Combine(def, "diagramLayout.json")), StringComparison.Ordinal);

                var saved = await engine.SaveAsync(null, "TMDL", overwrite: true);
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
                await engine.SaveAsync(null, "TMDL", overwrite: true);

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
                await engine.SaveAsync(folder, "TMDL");
                var measure = (await engine.ListMeasuresAsync()).First();
                var unreadable = Path.Combine(folder, "notes", "missing.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(unreadable));
                File.CreateSymbolicLink(unreadable, Path.Combine(folder, "notes", "does-not-exist.txt"));
                var before = SnapshotTmdl(folder);

                await engine.SetDaxAsync(measure.Ref, "11 + 11", "human");
                var ex = await Assert.ThrowsAsync<IOException>(() => engine.SaveAsync(null, "TMDL", overwrite: true));

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
                await engine.SaveAsync(folder, format);
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
                await engine.SaveAsync(folder, format, overwrite: true);

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
                    await skipEngine.SaveAsync(null, "BIM");
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
                    try { await engine.SaveAsync(null, "BIM"); }
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
                await engine.SaveAsync(null, "BIM");
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
                await seed.SaveAsync(folder, "TMDL");
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
                await engine.SaveAsync(null, "BIM");                      // clean baseline: nothing unsaved but the policy
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

        private static string Snapshot(string dir) => string.Join("\n",
            Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/') + ":" + File.ReadAllText(p)));

        private static string SnapshotTmdl(string dir) => string.Join("\n",
            Directory.EnumerateFiles(dir, "*.tmdl", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/') + ":" + File.ReadAllText(p)));
    }
}
