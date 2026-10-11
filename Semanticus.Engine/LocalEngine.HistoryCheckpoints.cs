using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>R7 durable Edit History checkpoints. Git remains the only store: a checkpoint is a normal commit
    /// with model-scoped trailers, and restore creates a new commit on the current branch instead of detaching HEAD.</summary>
    public sealed partial class LocalEngine
    {
        private const string CheckpointSubject = "Semanticus checkpoint: ";
        private const string CheckpointMarker = "Semanticus-Checkpoint: 1";

        /// <summary>Test seam: when a checkpoint label contains this text, CreateHistoryCheckpoint returns an error instead of committing.</summary>
        internal string FailCheckpointCommitMatchingForTest;

        /// <summary>Test seam: runs after a restore has rewritten the model files and before it reopens them.</summary>
        internal Func<Task> AfterRestoreFilesForTest;

        private sealed class CheckpointContext
        {
            public Session Session;
            public string RepoRoot;
            public string ModelKey;
            public string[] Paths;
            public GitStatus Status;
        }

        public async Task<HistoryCheckpointList> ListHistoryCheckpointsAsync(int max = 50)
        {
            var context = await CheckpointContextAsync();
            if (context == null)
                return new HistoryCheckpointList { Supported = false, Note = "Open or save a model inside a git repository to create durable checkpoints." };

            var checkpoints = await ReadCheckpointLogAsync(context, max);
            return new HistoryCheckpointList
            {
                Supported = true,
                Branch = context.Status.Branch,
                ModelDirty = context.Session.HasUnsavedChanges,
                Checkpoints = checkpoints,
                OwnedPaths = context.Paths,
                Note = context.Status.Detached
                    ? "The repository is on a detached commit. Checkpoints can be read, but create and restore need a branch."
                    : checkpoints.Length == 0
                        ? "No durable checkpoints for this model yet. Session undo remains available until the model closes."
                        : "Checkpoints are git commits for this model. Restoring creates a rescue checkpoint first and keeps the current branch.",
            };
        }

        public async Task<HistoryCheckpointResult> CreateHistoryCheckpointAsync(string label, bool commit, string origin)
        {
            if (commit) GuardOutsideWrite(origin);
            var result = await CreateHistoryCheckpointCoreAsync(label, commit, origin);
            // Contract 8.7: a version saved on purpose is a flag row in History. The two a restore makes for itself
            // (the rescue and the restored state) are told by the one "restored" row instead.
            if (result.Committed && result.Checkpoint != null && _sessions.Current is Session s)
                _sessions.Ledger.RecordFlag(s.Id, EditLedgerKinds.Saved, "Saved a version: " + result.Checkpoint.Label,
                    ChangeBy.For(null, origin), result.Checkpoint.Hash);
            return result;
        }

        private async Task<HistoryCheckpointResult> CreateHistoryCheckpointCoreAsync(string label, bool commit, string origin)
        {
            var context = await CheckpointContextAsync();
            if (context == null)
                return new HistoryCheckpointResult { Preview = !commit, Error = "Open or save a model inside a git repository first." };
            if (context.Status.Detached)
                return new HistoryCheckpointResult { Preview = !commit, Error = "The repository is on a detached commit. Check out a branch before creating a checkpoint." };

            var cleanLabel = CleanCheckpointLabel(label, context.Session.Revision);
            if (!string.IsNullOrEmpty(FailCheckpointCommitMatchingForTest)
                && cleanLabel.IndexOf(FailCheckpointCommitMatchingForTest, StringComparison.Ordinal) >= 0)
                return new HistoryCheckpointResult { Error = "forced test failure", SavedModelFirst = context.Session.HasUnsavedChanges };
            // A rename names two paths, and a table rename in a folder model renames its file, so both count.
            var ownedNow = context.Status.Files.SelectMany(ChangePaths).Where(p => IsOwned(p, context.Paths)).Distinct(PathComparer).ToList();
            if (context.Session.HasUnsavedChanges && !ownedNow.Contains(context.ModelKey, PathComparer))
                ownedNow.Add(context.ModelKey);
            if (!commit)
                return new HistoryCheckpointResult
                {
                    Preview = true,
                    Files = ownedNow.ToArray(),
                    SavedModelFirst = context.Session.HasUnsavedChanges,
                    Note = context.Session.HasUnsavedChanges
                        ? "Preview only. The open model will be saved first, then only its model files and project notes are committed."
                        : "Preview only. Creating the checkpoint commits only this model's files. Other repository changes stay untouched.",
                };

            var saved = false;
            if (context.Session.HasUnsavedChanges)
            {
                var format = Directory.Exists(context.Session.SourcePath) ? "TMDL"
                    : context.Session.SourcePath.EndsWith(".bim", StringComparison.OrdinalIgnoreCase) ? "BIM" : "TMDL";
                await SaveAsync(context.Session.SourcePath, format, false, origin);
                saved = true;
            }
            // The History's pending write lands before the folder is read and staged (LocalEngine.cs, SettleSidecarWritesAsync).
            await SettleSidecarWritesAsync();

            context = await CheckpointContextAsync();
            if (context == null)
                return new HistoryCheckpointResult { Error = "The model stopped resolving to its git repository after save.", SavedModelFirst = saved };
            var unmerged = context.Status.Files.Where(f => f.Status == "U").Select(f => f.Path).ToArray();
            if (unmerged.Length > 0)
                return new HistoryCheckpointResult { Error = "Resolve the repository conflicts before checkpointing: " + string.Join(", ", unmerged), SavedModelFirst = saved };
            // The commit takes the whole index, so a staged rename is the model's own only when BOTH of its paths are:
            // a file moved into the model folder from outside would also commit that outside path's deletion.
            var stagedOutside = context.Status.Files.Where(f => f.Staged).SelectMany(ChangePaths)
                .Where(p => !IsOwned(p, context.Paths)).Distinct(PathComparer).ToArray();
            if (stagedOutside.Length > 0)
                return new HistoryCheckpointResult
                {
                    Error = "Unrelated files are already staged, so a model-only checkpoint would accidentally include them. Commit or unstage these first: " + string.Join(", ", stagedOutside),
                    SavedModelFirst = saved,
                };

            // The sidecar .gitignore keeps the live log and lock out. Magic exclude pathspecs after -- can fail
            // on some git builds ("pathspec did not match"), which would drop primers and workflows from the commit.
            var add = await GitCli.RunAsync(context.RepoRoot, "add", "-A", "--", context.ModelKey);
            foreach (var spec in context.Paths.Where(p => !string.Equals(p, context.ModelKey, PathComparison)))
            {
                var side = await GitCli.RunAsync(context.RepoRoot, "add", "-A", "--", spec);
                if (!side.Ok && (side.Stderr ?? "").IndexOf("ignored", StringComparison.OrdinalIgnoreCase) < 0)
                    add = side;
            }
            if (!add.Ok) return new HistoryCheckpointResult { Error = GitCli.Combine(add), SavedModelFirst = saved };
            // -z: without it git quotes a name with a letter outside plain ASCII as octal escapes in this list too.
            var cached = await GitCli.RunAsync(context.RepoRoot, new[] { "diff", "--cached", "--name-only", "-z", "--" }.Concat(context.Paths).ToArray());
            if (!cached.Ok) return new HistoryCheckpointResult { Error = GitCli.Combine(cached), SavedModelFirst = saved };
            var files = GitCli.NulFields(cached.Stdout);

            var auditHead = await context.Session.ReadAsync(m => VerifiedEditsStore.Load(m).Records.LastOrDefault()?.Hash ?? "");
            var body = string.Join("\n", new[]
            {
                CheckpointMarker,
                "Semanticus-Model: " + context.ModelKey,
                "Semanticus-Session: " + context.Session.Id,
                "Semanticus-Revision: " + context.Session.Revision,
                "Semanticus-Audit-Head: " + auditHead,
            });
            var cr = await GitCli.RunAsync(context.RepoRoot, "commit", "--allow-empty", "-m", CheckpointSubject + cleanLabel, "-m", body);
            if (!cr.Ok) return new HistoryCheckpointResult { Error = GitCli.Combine(cr), Files = files, SavedModelFirst = saved };

            var hash = await GitCli.HeadCommitAsync(context.RepoRoot);
            var checkpoint = (await ReadCheckpointLogAsync(context, 1)).FirstOrDefault(x => string.Equals(x.Hash, hash, StringComparison.OrdinalIgnoreCase));
            return new HistoryCheckpointResult
            {
                Committed = checkpoint != null,
                Checkpoint = checkpoint,
                Files = files,
                SavedModelFirst = saved,
                Note = files.Length == 0
                    ? "Created an accepted-state marker; the model files were already identical to the previous commit."
                    : $"Checkpointed {files.Length} model file(s). Unrelated repository changes were not included.",
            };
        }

        public async Task<HistoryRestoreResult> RestoreHistoryCheckpointAsync(string hash, bool restore, string origin)
        {
            var context = await CheckpointContextAsync();
            if (context == null)
                return new HistoryRestoreResult { Preview = !restore, Error = "Open or save a model inside a git repository first." };
            if (context.Status.Detached)
                return new HistoryRestoreResult { Preview = !restore, Error = "The repository is on a detached commit. Check out a branch before restoring." };
            var target = (await ReadCheckpointLogAsync(context, 500))
                .FirstOrDefault(x => string.Equals(x.Hash, hash, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(x.ShortHash, hash, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                return new HistoryRestoreResult { Preview = !restore, Error = $"Checkpoint '{hash}' is not in this model's current branch history." };

            if (!restore)
                return new HistoryRestoreResult
                {
                    Preview = true,
                    Target = target,
                    Paths = context.Paths,
                    Note = "Preview only. Restore first commits the current model as a rescue checkpoint, then restores only this model's tracked files and commits the result on the current branch. Unrelated files and branch history are not reset.",
                };

            // Restoring replaces the model's files and reopens it, so under Locked an assistant's restore waits for the
            // person BEFORE the rescue checkpoint is made: the grant names this checkpoint and nothing else.
            var heldRestore = HoldFileWrite(origin, "Your assistant wants to restore the model files to the saved version \"" + target.Label + "\" (" + target.ShortHash + "). Anything unsaved is kept as a rescue version first.", "restore:" + target.Hash);
            if (heldRestore != null) return new HistoryRestoreResult { Target = target, Paths = context.Paths, Error = heldRestore };
            // The restore was just cleared above (or is the person's), and its rescue and restored checkpoints are part of
            // it: they save as the engine, so one approval is one restore and not three asks. This is the only place an
            // assistant's origin is swapped, and only after the hold above let it through.
            var innerOrigin = string.Equals(origin, "agent", StringComparison.Ordinal) ? "system" : origin;
            var sourcePath = context.Session.SourcePath;
            var rescue = await CreateHistoryCheckpointCoreAsync("Before restore to " + target.ShortHash, commit: true, innerOrigin);
            if (!string.IsNullOrEmpty(rescue.Error))
                return new HistoryRestoreResult { Target = target, Paths = context.Paths, Error = "The rescue checkpoint failed, so nothing was restored. " + rescue.Error };

            context = await CheckpointContextAsync();
            // git restore refuses the whole call when one pathspec matches nothing it knows, which is what a sidecar
            // folder the person git-ignores does. The checkpoint side already tolerates that folder, so the restore
            // passes only the sidecar paths git holds in the target or the index; an untracked sidecar is left as is.
            var restorePaths = new List<string> { context.ModelKey };
            foreach (var spec in context.Paths.Where(p => !string.Equals(p, context.ModelKey, PathComparison)))
                if (await GitKnowsPathAsync(context.RepoRoot, target.Hash, spec)) restorePaths.Add(spec);
            OpenResult reopened;
            _restoringModelFiles = true;
            try
            {
                var rr = await GitCli.RunAsync(context.RepoRoot,
                    new[] { "restore", "--source", target.Hash, "--staged", "--worktree", "--" }.Concat(restorePaths).ToArray());
                if (!rr.Ok)
                    return new HistoryRestoreResult
                    {
                        Target = target, RescueCheckpoint = rescue.Checkpoint, Paths = context.Paths,
                        Error = "The current state is safe in the rescue checkpoint, but git could not restore the target: " + GitCli.Combine(rr),
                    };

                if (AfterRestoreFilesForTest != null) await AfterRestoreFilesForTest();
                reopened = await OpenCoreAsync(sourcePath);
            }
            finally { _restoringModelFiles = false; }
            var opened = _sessions.Current;
            if (opened != null) PublishSaveNotification(opened, "restore");
            // Contract 8.7: the restore reopened the model, so this is a new session and a new undo stack. The row
            // is the seam: everything above it in History reads "Before the restore".
            if (opened != null)
                _sessions.Ledger.RecordFlag(opened.Id, EditLedgerKinds.Restored, "Restored " + target.Label,
                    ChangeBy.For(null, origin), "restored " + target.ShortHash + "; the state before it is saved as " + rescue.Checkpoint?.ShortHash);
            PublishSessionSwap(reopened, "restore");
            var restored = await CreateHistoryCheckpointCoreAsync("Restored to " + target.ShortHash + " - " + target.Label, commit: true, innerOrigin);
            if (!string.IsNullOrEmpty(restored.Error))
                return new HistoryRestoreResult
                {
                    Restored = true,
                    Target = target, RescueCheckpoint = rescue.Checkpoint, Paths = context.Paths, SessionId = opened?.Id,
                    Error = "The model files were restored and reopened, but the restored state was not committed: " + restored.Error,
                };

            return new HistoryRestoreResult
            {
                Restored = true,
                Target = target,
                RescueCheckpoint = rescue.Checkpoint,
                RestoredCheckpoint = restored.Checkpoint,
                Paths = context.Paths,
                SessionId = opened?.Id,
                Note = $"Restored {target.ShortHash} on the current branch. The previous state is {rescue.Checkpoint?.ShortHash}; the restored state is {restored.Checkpoint?.ShortHash}.",
            };
        }

        /// <summary>Whether git tracks anything under <paramref name="spec"/> in the index or in commit <paramref name="hash"/>.</summary>
        private static async Task<bool> GitKnowsPathAsync(string repoRoot, string hash, string spec)
        {
            var index = await GitCli.RunAsync(repoRoot, "ls-files", "--", spec);
            if (index.Ok && !string.IsNullOrWhiteSpace(index.Stdout)) return true;
            var tree = await GitCli.RunAsync(repoRoot, "ls-tree", "-r", "--name-only", hash, "--", spec);
            return tree.Ok && !string.IsNullOrWhiteSpace(tree.Stdout);
        }

        private async Task<CheckpointContext> CheckpointContextAsync()
        {
            var s = _sessions.Current;
            if (s == null || string.IsNullOrWhiteSpace(s.SourcePath)) return null;
            var working = GitWorkingDirOrNull();
            if (working == null) return null;
            var found = await GitCli.RepoRootAsync(working);
            if (string.IsNullOrWhiteSpace(found)) return null;
            var root = Path.GetFullPath(found);
            // From the root: `git status --porcelain=v2` names paths relative to the folder it runs in, and every owned
            // key below is repo-relative. Run in the model's folder, a model below the root (every PBIP) owned none of
            // its own files, so a restore committed nothing and left them staged for the next checkpoint to refuse.
            var status = await GitCli.StatusAsync(root);
            if (!status.IsRepo) return null;
            var source = Path.GetFullPath(s.SourcePath);
            var modelKey = RepoRelative(root, source);
            if (modelKey == null) return null;
            var paths = new List<string> { modelKey };
            var sidecar = LayoutStore.DirFor(source);
            if (!string.IsNullOrWhiteSpace(sidecar) && Directory.Exists(sidecar))
            {
                LayoutStore.EnsureRuntimeIgnore(sidecar);
                var sidecarKey = RepoRelative(root, Path.GetFullPath(sidecar));
                if (sidecarKey != null && !paths.Contains(sidecarKey, PathComparer))
                    paths.Add(sidecarKey);
            }
            return new CheckpointContext { Session = s, RepoRoot = root, ModelKey = modelKey, Paths = paths.ToArray(), Status = status };
        }

        private async Task<HistoryCheckpoint[]> ReadCheckpointLogAsync(CheckpointContext context, int max)
        {
            var n = Math.Max(1, Math.Min(max <= 0 ? 50 : max, 500));
            var r = await GitCli.RunAsync(context.RepoRoot, "log", "-n", "500", "--pretty=format:%H%x1f%h%x1f%an%x1f%aI%x1f%s%x1f%b%x1e");
            if (!r.Ok) return Array.Empty<HistoryCheckpoint>();
            var found = new List<HistoryCheckpoint>();
            foreach (var rec in r.Stdout.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = rec.Trim('\r', '\n').Split('\x1f');
                if (f.Length < 6 || !f[4].StartsWith(CheckpointSubject, StringComparison.Ordinal)) continue;
                var fields = Lines(f[5]).Select(line => line.Split(new[] { ": " }, 2, StringSplitOptions.None))
                    .Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1], StringComparer.Ordinal);
                if (!fields.ContainsKey("Semanticus-Checkpoint")
                    || !fields.TryGetValue("Semanticus-Model", out var model)
                    || !string.Equals(model, context.ModelKey, PathComparison)) continue;
                fields.TryGetValue("Semanticus-Revision", out var rev);
                fields.TryGetValue("Semanticus-Session", out var session);
                fields.TryGetValue("Semanticus-Audit-Head", out var audit);
                found.Add(new HistoryCheckpoint
                {
                    Hash = f[0], ShortHash = f[1], Author = f[2], When = f[3],
                    Label = f[4].Substring(CheckpointSubject.Length), ModelPath = model,
                    SessionId = session, Revision = long.TryParse(rev, out var revision) ? revision : 0, AuditHead = audit,
                });
                if (found.Count >= n) break;
            }
            return found.ToArray();
        }

        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static string RepoRelative(string root, string path)
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/').TrimEnd('/');
            if (rel == ".." || rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel) || rel.Contains('\n') || rel.Contains('\r')) return null;
            return string.IsNullOrEmpty(rel) ? "." : rel;
        }

        private static IEnumerable<string> ChangePaths(GitFileChange f)
            => string.IsNullOrEmpty(f.OrigPath) ? new[] { f.Path } : new[] { f.Path, f.OrigPath };

        private static bool IsOwned(string path, IEnumerable<string> specs)
        {
            var p = (path ?? "").Replace('\\', '/').TrimEnd('/');
            return specs.Any(spec => spec == "." || string.Equals(p, spec, PathComparison)
                || p.StartsWith(spec.TrimEnd('/') + "/", PathComparison));
        }

        private static string CleanCheckpointLabel(string label, long revision)
        {
            var oneLine = string.Join(" ", (label ?? "").Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (string.IsNullOrEmpty(oneLine)) oneLine = revision > 0 ? "Accepted model state at revision " + revision : "Accepted model state";
            return oneLine.Length <= 120 ? oneLine : oneLine.Substring(0, 120);
        }

        private static string[] Lines(string value) => (value ?? "").Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
    }
}
