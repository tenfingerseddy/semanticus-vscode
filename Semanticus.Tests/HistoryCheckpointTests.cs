using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>R7 pins git as the durable Edit History store: model-only commits, explicit preview, rescue-first
    /// restore on the current branch, automatic reopen, and refusal when unrelated staged work could be swept in.</summary>
    public sealed class HistoryCheckpointTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private static (string Dir, string Model) Repo()
        {
            var dir = Path.Combine(Path.GetTempPath(), "history-checkpoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "model.bim");
            TinyBim.Write(model);
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "leave me alone");
            Git(dir, "init", "-q");
            Git(dir, "config", "user.email", "history@test.local");
            Git(dir, "config", "user.name", "History Test");
            Git(dir, "config", "commit.gpgsign", "false");
            Git(dir, "add", "--", "model.bim", "notes.txt");
            Git(dir, "commit", "-q", "-m", "initial");
            return (dir, model);
        }

        private static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(stderr);
            return stdout.Trim();
        }

        private static async Task<(LocalEngine Engine, string Ref)> Open(string model)
        {
            var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.OpenAsync(model);
            return (engine, (await engine.ListMeasuresAsync()).First().Ref);
        }

        [Fact]
        public async Task Preview_and_commit_are_model_scoped_and_listed_from_git()
        {
            var (dir, model) = Repo();
            try
            {
                var (engine, measure) = await Open(model);
                using (engine)
                {
                    await engine.SetDaxAsync(measure, "1", "human");
                    var preview = await engine.CreateHistoryCheckpointAsync("Accepted total", commit: false, "human");
                    Assert.True(preview.Preview);
                    Assert.True(preview.SavedModelFirst);
                    Assert.False(preview.Committed);

                    var made = await engine.CreateHistoryCheckpointAsync("Accepted total", commit: true, "human");
                    Assert.Null(made.Error);
                    Assert.True(made.Committed);
                    Assert.Equal(40, made.Checkpoint.Hash.Length);
                    Assert.Contains(made.Files, x => x == "model.bim");
                    Assert.DoesNotContain(made.Files, x => x == "notes.txt");

                    var list = await engine.ListHistoryCheckpointsAsync();
                    Assert.True(list.Supported);
                    var checkpoint = Assert.Single(list.Checkpoints);
                    Assert.Equal("Accepted total", checkpoint.Label);
                    Assert.Equal(made.Checkpoint.Hash, checkpoint.Hash);
                    Assert.Equal("model.bim", checkpoint.ModelPath);
                    Assert.Contains("Semanticus-Checkpoint: 1", Git(dir, "show", "-s", "--format=%b", checkpoint.Hash));
                }
            }
            finally { Delete(dir); }
        }

        [Fact]
        public async Task Unrelated_staged_work_refuses_without_committing_it()
        {
            var (dir, model) = Repo();
            try
            {
                var (engine, measure) = await Open(model);
                using (engine)
                {
                    await engine.SetDaxAsync(measure, "2", "human");
                    File.WriteAllText(Path.Combine(dir, "notes.txt"), "my unfinished notes");
                    Git(dir, "add", "--", "notes.txt");
                    var before = Git(dir, "rev-parse", "HEAD");

                    var made = await engine.CreateHistoryCheckpointAsync("Must stay scoped", commit: true, "human");
                    Assert.Contains("Unrelated files are already staged", made.Error);
                    Assert.Contains("notes.txt", made.Error);
                    Assert.Equal(before, Git(dir, "rev-parse", "HEAD"));
                    Assert.Contains("notes.txt", Git(dir, "diff", "--cached", "--name-only"));
                }
            }
            finally { Delete(dir); }
        }

        [Fact]
        public async Task Restore_creates_rescue_and_restored_commits_reopens_model_and_keeps_branch()
        {
            var (dir, model) = Repo();
            try
            {
                var (engine, measure) = await Open(model);
                using (engine)
                {
                    var branch = Git(dir, "branch", "--show-current");
                    await engine.SetDaxAsync(measure, "1", "human");
                    var first = await engine.CreateHistoryCheckpointAsync("Known good", commit: true, "human");
                    await engine.SetDaxAsync(measure, "2", "human");
                    await engine.CreateHistoryCheckpointAsync("Experiment", commit: true, "human");

                    var preview = await engine.RestoreHistoryCheckpointAsync(first.Checkpoint.ShortHash, restore: false, "human");
                    Assert.True(preview.Preview);
                    Assert.Equal(first.Checkpoint.Hash, preview.Target.Hash);

                    var restored = await engine.RestoreHistoryCheckpointAsync(first.Checkpoint.Hash, restore: true, "human");
                    Assert.Null(restored.Error);
                    Assert.True(restored.Restored);
                    Assert.NotNull(restored.RescueCheckpoint);
                    Assert.NotNull(restored.RestoredCheckpoint);
                    Assert.NotEqual(restored.RescueCheckpoint.Hash, restored.RestoredCheckpoint.Hash);
                    // Studio keys the line it carries across the remount to this (verifier r2 O4).
                    Assert.Equal((await engine.SessionInfoAsync()).SessionId, restored.SessionId);
                    Assert.Equal(branch, Git(dir, "branch", "--show-current"));
                    Assert.Equal("leave me alone", File.ReadAllText(Path.Combine(dir, "notes.txt")));
                    Assert.Equal("1", (await engine.ListMeasuresAsync()).Single(x => x.Ref == measure).Expression);
                    Assert.Equal("", Git(dir, "status", "--porcelain", "--", "model.bim"));
                }
            }
            finally { Delete(dir); }
        }

        // Verifier A2: after every restore, from either door, the window asked "The files on disk changed" although
        // the engine had just reopened the restored files itself. The window only asks when the engine says so.
        [Fact]
        public async Task Restore_leaves_the_session_matching_the_files_on_disk()
        {
            var (dir, model) = Repo();
            try
            {
                var (engine, measure) = await Open(model);
                using (engine)
                {
                    await engine.SetDaxAsync(measure, "1", "human");
                    var first = await engine.CreateHistoryCheckpointAsync("Known good", commit: true, "human");
                    await engine.SetDaxAsync(measure, "2", "human");
                    await engine.CreateHistoryCheckpointAsync("Experiment", commit: true, "human");

                    // The window's file watcher fires the moment git rewrites the file and asks at once, so it asks
                    // while the engine is still reopening. That answer must not be "the files changed under you".
                    bool? midRestore = null;
                    engine.AfterRestoreFilesForTest = async () => midRestore = (await engine.SessionInfoAsync()).DiskDiverged;
                    var restored = await engine.RestoreHistoryCheckpointAsync(first.Checkpoint.Hash, restore: true, "human");
                    Assert.Null(restored.Error);
                    Assert.False(midRestore ?? true, "asked between the file rewrite and the reopen, the engine said the files changed under the person");
                    var info = await engine.SessionInfoAsync();
                    Assert.False(info.HasUnsavedChanges);
                    Assert.False(info.DiskDiverged, "the engine wrote and reopened these files itself; nothing on disk moved under the person");
                }
            }
            finally { Delete(dir); }
        }

        // Verifier A1: a person who git-ignores .semanticus/ could checkpoint (the add tolerates an ignored sidecar) but
        // not restore: "error: pathspec '.semanticus' did not match any file(s) known to git".
        [Fact]
        public async Task Restore_works_when_the_sidecar_folder_is_git_ignored()
        {
            var (dir, model) = Repo();
            try
            {
                File.WriteAllText(Path.Combine(dir, ".gitignore"), ".semanticus/\n");
                Git(dir, "add", "--", ".gitignore");
                Git(dir, "commit", "-q", "-m", "ignore the sidecar");
                var (engine, measure) = await Open(model);
                using (engine)
                {
                    Directory.CreateDirectory(Path.Combine(dir, ".semanticus"));
                    File.WriteAllText(Path.Combine(dir, ".semanticus", "note.md"), "kept out of git on purpose");
                    await engine.SetDaxAsync(measure, "1", "human");
                    var first = await engine.CreateHistoryCheckpointAsync("Known good", commit: true, "human");
                    Assert.Null(first.Error);
                    await engine.SetDaxAsync(measure, "2", "human");
                    Assert.Null((await engine.CreateHistoryCheckpointAsync("Experiment", commit: true, "human")).Error);

                    var restored = await engine.RestoreHistoryCheckpointAsync(first.Checkpoint.Hash, restore: true, "human");
                    Assert.Null(restored.Error);
                    Assert.True(restored.Restored);
                    Assert.Equal("1", (await engine.ListMeasuresAsync()).Single(x => x.Ref == measure).Expression);
                    Assert.Equal("kept out of git on purpose", File.ReadAllText(Path.Combine(dir, ".semanticus", "note.md")));
                    Assert.Equal("", Git(dir, "status", "--porcelain", "--", "model.bim"));
                }
            }
            finally { Delete(dir); }
        }

        // Verifier r2 A1: a model below the repository root (every PBIP opens at <name>.SemanticModel/definition) restored
        // half-way and then refused: "restored state was not committed: Unrelated files are already staged ... model.bim",
        // and the next restore or checkpoint failed the same way. `git status --porcelain=v2` names paths from the folder
        // it runs in, while the checkpoint compares repo-relative keys, so none of the model's own files looked owned.
        [Theory]
        [InlineData("root")]
        [InlineData("folder")]
        [InlineData("pbip")]
        public async Task Restore_and_checkpoint_work_for_a_model_at_the_root_or_below_it(string layout)
        {
            var dir = Path.Combine(Path.GetTempPath(), "history-checkpoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "notes.txt"), "leave me alone");
                string model, modelKey, sidecar;
                if (layout == "pbip")
                {
                    // Written as TMDL by the engine itself, so the folder is the shape Power BI Desktop saves.
                    model = Path.Combine(dir, "Sales.SemanticModel", "definition");
                    using (var writer = new LocalEngine(new SessionManager(), new Pro()))
                    {
                        await writer.OpenAsync(TinyBim.Write(Path.Combine(dir + "-seed", "Seed.bim")));
                        await writer.SaveAsync(model, "TMDL", false, "human");
                    }
                    modelKey = "Sales.SemanticModel/definition";
                    sidecar = Path.Combine(dir, "Sales.SemanticModel", ".semanticus");
                }
                else
                {
                    var modelDir = layout == "root" ? dir : Path.Combine(dir, "models");
                    Directory.CreateDirectory(modelDir);
                    model = Path.Combine(modelDir, "model.bim");
                    TinyBim.Write(model);
                    modelKey = layout == "root" ? "model.bim" : "models/model.bim";
                    sidecar = Path.Combine(modelDir, ".semanticus");
                }
                Directory.CreateDirectory(sidecar);
                File.WriteAllText(Path.Combine(sidecar, "note.md"), "the model's own notes");
                Git(dir, "init", "-q");
                Git(dir, "config", "user.email", "history@test.local");
                Git(dir, "config", "user.name", "History Test");
                Git(dir, "config", "commit.gpgsign", "false");
                Git(dir, "add", "-A");
                Git(dir, "commit", "-q", "-m", "initial");

                var (engine, measure) = await Open(model);
                using (engine)
                {
                    await engine.SetDaxAsync(measure, "1", "human");
                    File.WriteAllText(Path.Combine(sidecar, "note.md"), "notes at known good");
                    var preview = await engine.CreateHistoryCheckpointAsync("Known good", commit: false, "human");
                    Assert.Contains(preview.Files, f => f == modelKey || f.StartsWith(modelKey + "/", StringComparison.Ordinal));
                    Assert.Contains(preview.Files, f => f.EndsWith(".semanticus/note.md", StringComparison.Ordinal) && !f.StartsWith("../", StringComparison.Ordinal));
                    var first = await engine.CreateHistoryCheckpointAsync("Known good", commit: true, "human");
                    Assert.Null(first.Error);
                    Assert.Equal(modelKey, first.Checkpoint.ModelPath);
                    await engine.SetDaxAsync(measure, "2", "human");
                    File.WriteAllText(Path.Combine(sidecar, "note.md"), "notes at experiment");
                    var second = await engine.CreateHistoryCheckpointAsync("Experiment", commit: true, "human");
                    Assert.Null(second.Error);

                    var restored = await engine.RestoreHistoryCheckpointAsync(first.Checkpoint.Hash, restore: true, "human");
                    Assert.Null(restored.Error);
                    Assert.True(restored.Restored);
                    Assert.NotNull(restored.RestoredCheckpoint);
                    Assert.Equal("1", (await engine.ListMeasuresAsync()).Single(x => x.Ref == measure).Expression);
                    Assert.Equal("notes at known good", File.ReadAllText(Path.Combine(sidecar, "note.md")));
                    Assert.False((await engine.SessionInfoAsync()).DiskDiverged);
                    Assert.Equal("", Git(dir, "diff", "--cached", "--name-only"));
                    Assert.Equal("leave me alone", File.ReadAllText(Path.Combine(dir, "notes.txt")));

                    // The next restore and the next checkpoint are the ones that refused.
                    var back = await engine.RestoreHistoryCheckpointAsync(second.Checkpoint.Hash, restore: true, "human");
                    Assert.Null(back.Error);
                    Assert.True(back.Restored);
                    Assert.Equal("2", (await engine.ListMeasuresAsync()).Single(x => x.Ref == measure).Expression);
                    await engine.SetDaxAsync(measure, "3", "human");
                    var third = await engine.CreateHistoryCheckpointAsync("After the restores", commit: true, "human");
                    Assert.Null(third.Error);
                    Assert.True(third.Committed);
                    Assert.Equal("", Git(dir, "status", "--porcelain", "--", modelKey));
                }
            }
            finally { Delete(dir); }
        }

        // Verifier r3 (failing on purpose): a model whose file or folder name has a letter outside plain ASCII still
        // restores half-way and then refuses, at the root and below it, the same way A1 did: "the restored state was
        // not committed: Unrelated files are already staged ...", naming the model's own files, which stay staged. git
        // prints such a path in quotes, with the letter as octal escapes (core.quotePath), so the quoted status path
        // never matches the model's own repo-relative key. Seen on the lane base too, so it is older than this lane.
        [Theory]
        [InlineData("root")]
        [InlineData("pbip")]
        public async Task Restore_and_checkpoint_work_for_a_model_whose_name_is_not_plain_ascii(string layout)
        {
            var dir = Path.Combine(Path.GetTempPath(), "history-checkpoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "notes.txt"), "leave me alone");
                string model, modelKey, sidecar;
                if (layout == "pbip")
                {
                    var folder = "Ventes Caf\u00e9.SemanticModel";
                    model = Path.Combine(dir, folder, "definition");
                    using (var writer = new LocalEngine(new SessionManager(), new Pro()))
                    {
                        await writer.OpenAsync(TinyBim.Write(Path.Combine(dir + "-seed", "Seed.bim")));
                        await writer.SaveAsync(model, "TMDL", false, "human");
                    }
                    modelKey = folder + "/definition";
                    sidecar = Path.Combine(dir, folder, ".semanticus");
                }
                else
                {
                    model = Path.Combine(dir, "Caf\u00e9.bim");
                    TinyBim.Write(model);
                    modelKey = "Caf\u00e9.bim";
                    sidecar = Path.Combine(dir, ".semanticus");
                }
                Directory.CreateDirectory(sidecar);
                File.WriteAllText(Path.Combine(sidecar, "note.md"), "the model's own notes");
                Git(dir, "init", "-q");
                Git(dir, "config", "user.email", "history@test.local");
                Git(dir, "config", "user.name", "History Test");
                Git(dir, "config", "commit.gpgsign", "false");
                Git(dir, "add", "-A");
                Git(dir, "commit", "-q", "-m", "initial");

                var (engine, measure) = await Open(model);
                using (engine)
                {
                    await engine.SetDaxAsync(measure, "1", "human");
                    File.WriteAllText(Path.Combine(sidecar, "note.md"), "notes at known good");
                    var preview = await engine.CreateHistoryCheckpointAsync("Known good", commit: false, "human");
                    Assert.Contains(preview.Files, f => f.EndsWith(".semanticus/note.md", StringComparison.Ordinal) && !f.StartsWith("\"", StringComparison.Ordinal));
                    var first = await engine.CreateHistoryCheckpointAsync("Known good", commit: true, "human");
                    Assert.Null(first.Error);
                    Assert.Equal(modelKey, first.Checkpoint.ModelPath);
                    await engine.SetDaxAsync(measure, "2", "human");
                    var second = await engine.CreateHistoryCheckpointAsync("Experiment", commit: true, "human");
                    Assert.Null(second.Error);

                    var restored = await engine.RestoreHistoryCheckpointAsync(first.Checkpoint.Hash, restore: true, "human");
                    Assert.Null(restored.Error);
                    Assert.True(restored.Restored);
                    Assert.Equal("1", (await engine.ListMeasuresAsync()).Single(x => x.Ref == measure).Expression);
                    Assert.Equal("", Git(dir, "diff", "--cached", "--name-only"));

                    await engine.SetDaxAsync(measure, "3", "human");
                    var third = await engine.CreateHistoryCheckpointAsync("After the restore", commit: true, "human");
                    Assert.Null(third.Error);
                    Assert.True(third.Committed);
                }
            }
            finally { Delete(dir); }
        }

        // Verifier r3 (failing on purpose): in a folder model (every PBIP) renaming a table renames its TMDL file, so
        // restoring a version from before the rename stages that rename. git status --porcelain=v2 writes a rename entry
        // with one more field than a plain change (the similarity score, such as R93), and the parser keeps the score
        // in the path: "R93 Sales.SemanticModel/definition/tables/Date.tmdl" is owned by nothing. The restore then
        // commits nothing and leaves the model's own files staged, which is the failure A1 set out to remove.
        [Fact]
        public async Task Restore_works_across_a_table_rename_in_a_pbip_model()
        {
            var dir = Path.Combine(Path.GetTempPath(), "history-checkpoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "notes.txt"), "leave me alone");
                var model = Path.Combine(dir, "Sales.SemanticModel", "definition");
                using (var writer = new LocalEngine(new SessionManager(), new Pro()))
                {
                    await writer.OpenAsync(TinyBim.Write(Path.Combine(dir + "-seed", "Seed.bim")));
                    await writer.SaveAsync(model, "TMDL", false, "human");
                }
                Git(dir, "init", "-q");
                Git(dir, "config", "user.email", "history@test.local");
                Git(dir, "config", "user.name", "History Test");
                Git(dir, "config", "commit.gpgsign", "false");
                Git(dir, "add", "-A");
                Git(dir, "commit", "-q", "-m", "initial");

                var (engine, measure) = await Open(model);
                using (engine)
                {
                    var table = (await engine.ListMeasuresAsync()).Single(x => x.Ref == measure).Table;
                    var before = await engine.CreateHistoryCheckpointAsync("Before the rename", commit: true, "human");
                    Assert.Null(before.Error);
                    var renamed = await engine.RenameObjectAsync("table:" + table, table + " Renamed", "human");
                    Assert.True(renamed.Changed);
                    var after = await engine.CreateHistoryCheckpointAsync("After the rename", commit: true, "human");
                    Assert.Null(after.Error);

                    var restored = await engine.RestoreHistoryCheckpointAsync(before.Checkpoint.Hash, restore: true, "human");
                    Assert.Null(restored.Error);
                    Assert.True(restored.Restored);
                    Assert.Equal(table, (await engine.ListMeasuresAsync()).Single(x => x.Ref == measure).Table);
                    Assert.Equal("", Git(dir, "diff", "--cached", "--name-only"));
                    var next = await engine.CreateHistoryCheckpointAsync("After the restore", commit: true, "human");
                    Assert.Null(next.Error);
                }
            }
            finally { Delete(dir); }
        }

        // A rename names two paths and the checkpoint commits the whole index, so reading the rename right must not
        // widen what a checkpoint takes: a file moved INTO the model folder from outside stages the deletion of an
        // outside path, and a model-only checkpoint refuses it, naming that outside path, and commits nothing.
        [Fact]
        public async Task Checkpoint_refuses_a_staged_move_into_the_model_from_outside()
        {
            var dir = Path.Combine(Path.GetTempPath(), "history-checkpoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "notes.txt"), "leave me alone, I live outside the model");
                var model = Path.Combine(dir, "Sales.SemanticModel", "definition");
                using (var writer = new LocalEngine(new SessionManager(), new Pro()))
                {
                    await writer.OpenAsync(TinyBim.Write(Path.Combine(dir + "-seed", "Seed.bim")));
                    await writer.SaveAsync(model, "TMDL", false, "human");
                }
                Git(dir, "init", "-q");
                Git(dir, "config", "user.email", "history@test.local");
                Git(dir, "config", "user.name", "History Test");
                Git(dir, "config", "commit.gpgsign", "false");
                Git(dir, "add", "-A");
                Git(dir, "commit", "-q", "-m", "initial");
                Git(dir, "mv", "notes.txt", "Sales.SemanticModel/definition/notes.txt");
                var head = Git(dir, "rev-parse", "HEAD");

                var (engine, _) = await Open(model);
                using (engine)
                {
                    var result = await engine.CreateHistoryCheckpointAsync("Moved in", commit: true, "human");
                    Assert.NotNull(result.Error);
                    Assert.Contains("Unrelated files are already staged", result.Error, StringComparison.Ordinal);
                    Assert.EndsWith(": notes.txt", result.Error, StringComparison.Ordinal);
                    Assert.Equal(head, Git(dir, "rev-parse", "HEAD"));
                }
            }
            finally { Delete(dir); }
        }

        private static void Delete(string dir)
        {
            try { if (Directory.Exists(dir + "-seed")) Directory.Delete(dir + "-seed", true); } catch { }
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, true);
            }
            catch { }
        }
    }

    /// <summary>A few-table model written as a .bim, for tests that ask what the engine does with a model FILE (history, git, locks, saves,
    /// fences), not what is in AdventureWorks. Lean suite: opening, saving and committing AdventureWorks (about twenty tables) is most of
    /// what these tests cost. It has three tables of five or six columns each, a relationship and three measures, so a table rename still
    /// leaves a TMDL file git reads as a rename (more than half the file is the same).</summary>
    internal static class TinyBim
    {
        public static string Write(string path, string name = "Tiny")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var db = new Microsoft.AnalysisServices.Tabular.Database(name) { CompatibilityLevel = 1604, Model = new Microsoft.AnalysisServices.Tabular.Model { Culture = "en-US" } };
            Microsoft.AnalysisServices.Tabular.Table Table(string table, string[] columns)
            {
                var t = new Microsoft.AnalysisServices.Tabular.Table { Name = table, LineageTag = "t-" + table };
                foreach (var c in columns)
                    t.Columns.Add(new Microsoft.AnalysisServices.Tabular.DataColumn
                    {
                        Name = c, SourceColumn = c, LineageTag = "c-" + table + "-" + c,
                        DataType = c == "Amount" ? Microsoft.AnalysisServices.Tabular.DataType.Decimal : c.EndsWith("Key", StringComparison.Ordinal) || c == "Qty" || c == "Year" ? Microsoft.AnalysisServices.Tabular.DataType.Int64 : c == "Date" || c == "OrderDate" ? Microsoft.AnalysisServices.Tabular.DataType.DateTime : Microsoft.AnalysisServices.Tabular.DataType.String,
                    });
                t.Partitions.Add(new Microsoft.AnalysisServices.Tabular.Partition { Name = table, Source = new Microsoft.AnalysisServices.Tabular.MPartitionSource { Expression = "let Source = #table(type table [" + columns[0] + " = text], {}) in Source" } });
                return t;
            }
            var sales = Table("Sales", new[] { "OrderDate", "CustomerKey", "Amount", "Qty", "Region", "Channel" });
            sales.Measures.Add(new Microsoft.AnalysisServices.Tabular.Measure { Name = "Total Sales", Expression = "SUM ( Sales[Amount] )", LineageTag = "m-total" });
            sales.Measures.Add(new Microsoft.AnalysisServices.Tabular.Measure { Name = "Order Rows", Expression = "COUNTROWS ( Sales )", LineageTag = "m-rows" });
            var customer = Table("Customer", new[] { "CustomerKey", "Name", "City", "Country", "Segment" });
            customer.Measures.Add(new Microsoft.AnalysisServices.Tabular.Measure { Name = "Customers", Expression = "DISTINCTCOUNT ( Customer[CustomerKey] )", LineageTag = "m-cust" });
            var date = Table("Date", new[] { "Date", "Year", "Month", "Quarter", "Day" });
            db.Model.Tables.Add(sales); db.Model.Tables.Add(customer); db.Model.Tables.Add(date);
            db.Model.Relationships.Add(new Microsoft.AnalysisServices.Tabular.SingleColumnRelationship
            {
                Name = "r-sales-customer", FromColumn = sales.Columns["CustomerKey"], ToColumn = customer.Columns["CustomerKey"],
                FromCardinality = Microsoft.AnalysisServices.Tabular.RelationshipEndCardinality.Many, ToCardinality = Microsoft.AnalysisServices.Tabular.RelationshipEndCardinality.One,
            });
            File.WriteAllText(path, Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(db));
            return path;
        }
    }
}
