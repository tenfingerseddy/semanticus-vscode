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
            var context = await CheckpointContextAsync();
            if (context == null)
                return new HistoryCheckpointResult { Preview = !commit, Error = "Open or save a model inside a git repository first." };
            if (context.Status.Detached)
                return new HistoryCheckpointResult { Preview = !commit, Error = "The repository is on a detached commit. Check out a branch before creating a checkpoint." };

            var cleanLabel = CleanCheckpointLabel(label, context.Session.Revision);
            var ownedNow = context.Status.Files.Where(f => IsOwned(f.Path, context.Paths)).Select(f => f.Path).Distinct(PathComparer).ToArray();
            if (!commit)
                return new HistoryCheckpointResult
                {
                    Preview = true,
                    Files = ownedNow,
                    SavedModelFirst = context.Session.HasUnsavedChanges,
                    Note = context.Session.HasUnsavedChanges
                        ? "Preview only. Creating the checkpoint saves the open model first, then commits only its model and .semanticus paths."
                        : "Preview only. Creating the checkpoint commits only this model's files; unrelated repository changes stay untouched.",
                };

            var saved = false;
            if (context.Session.HasUnsavedChanges)
            {
                var format = Directory.Exists(context.Session.SourcePath) ? "TMDL"
                    : context.Session.SourcePath.EndsWith(".bim", StringComparison.OrdinalIgnoreCase) ? "BIM" : "TMDL";
                await SaveAsync(context.Session.SourcePath, format);
                saved = true;
            }

            context = await CheckpointContextAsync();
            if (context == null)
                return new HistoryCheckpointResult { Error = "The model stopped resolving to its git repository after save.", SavedModelFirst = saved };
            var unmerged = context.Status.Files.Where(f => f.Status == "U").Select(f => f.Path).ToArray();
            if (unmerged.Length > 0)
                return new HistoryCheckpointResult { Error = "Resolve the repository conflicts before checkpointing: " + string.Join(", ", unmerged), SavedModelFirst = saved };
            var stagedOutside = context.Status.Files.Where(f => f.Staged && !IsOwned(f.Path, context.Paths)).Select(f => f.Path).ToArray();
            if (stagedOutside.Length > 0)
                return new HistoryCheckpointResult
                {
                    Error = "Unrelated files are already staged, so a model-only checkpoint would accidentally include them. Commit or unstage these first: " + string.Join(", ", stagedOutside),
                    SavedModelFirst = saved,
                };

            // Exclude the volatile broker files anywhere in the pathspec: engine.lock is held FileShare.None (staging
            // it throws "Permission denied") and engine.json is churn, so neither belongs in a durable checkpoint.
            var add = await GitCli.RunAsync(context.RepoRoot, new[] { "add", "-A", "--" }.Concat(context.Paths)
                .Concat(new[] { ":(exclude,glob)**/engine.lock", ":(exclude,glob)**/engine.json" }).ToArray());
            if (!add.Ok) return new HistoryCheckpointResult { Error = GitCli.Combine(add), SavedModelFirst = saved };
            var cached = await GitCli.RunAsync(context.RepoRoot, new[] { "diff", "--cached", "--name-only", "--" }.Concat(context.Paths).ToArray());
            if (!cached.Ok) return new HistoryCheckpointResult { Error = GitCli.Combine(cached), SavedModelFirst = saved };
            var files = Lines(cached.Stdout);

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

            var sourcePath = context.Session.SourcePath;
            var rescue = await CreateHistoryCheckpointAsync("Before restore to " + target.ShortHash, commit: true, origin);
            if (!string.IsNullOrEmpty(rescue.Error))
                return new HistoryRestoreResult { Target = target, Paths = context.Paths, Error = "The rescue checkpoint failed, so nothing was restored. " + rescue.Error };

            context = await CheckpointContextAsync();
            var rr = await GitCli.RunAsync(context.RepoRoot,
                new[] { "restore", "--source", target.Hash, "--staged", "--worktree", "--" }.Concat(context.Paths).ToArray());
            if (!rr.Ok)
                return new HistoryRestoreResult
                {
                    Target = target, RescueCheckpoint = rescue.Checkpoint, Paths = context.Paths,
                    Error = "The current state is safe in the rescue checkpoint, but git could not restore the target: " + GitCli.Combine(rr),
                };

            await OpenAsync(sourcePath);
            var restored = await CreateHistoryCheckpointAsync("Restored to " + target.ShortHash + " - " + target.Label, commit: true, origin);
            if (!string.IsNullOrEmpty(restored.Error))
                return new HistoryRestoreResult
                {
                    Target = target, RescueCheckpoint = rescue.Checkpoint, Paths = context.Paths,
                    Error = "The model files were restored and reopened, but the restored state was not committed: " + restored.Error,
                };

            return new HistoryRestoreResult
            {
                Restored = true,
                Target = target,
                RescueCheckpoint = rescue.Checkpoint,
                RestoredCheckpoint = restored.Checkpoint,
                Paths = context.Paths,
                Note = $"Restored {target.ShortHash} on the current branch. The previous state is {rescue.Checkpoint?.ShortHash}; the restored state is {restored.Checkpoint?.ShortHash}.",
            };
        }

        private async Task<CheckpointContext> CheckpointContextAsync()
        {
            var s = _sessions.Current;
            if (s == null || string.IsNullOrWhiteSpace(s.SourcePath)) return null;
            var working = GitWorkingDirOrNull();
            if (working == null) return null;
            var status = await GitCli.StatusAsync(working);
            if (!status.IsRepo || string.IsNullOrWhiteSpace(status.RepoRoot)) return null;
            var root = Path.GetFullPath(status.RepoRoot);
            var source = Path.GetFullPath(s.SourcePath);
            var modelKey = RepoRelative(root, source);
            if (modelKey == null) return null;
            var paths = new List<string> { modelKey };
            var sidecar = LayoutStore.DirFor(source);
            var sidecarKey = string.IsNullOrWhiteSpace(sidecar) ? null : RepoRelative(root, Path.GetFullPath(sidecar));
            if (sidecarKey != null && !paths.Contains(sidecarKey, PathComparer))
            {
                // Add the sidecar ONLY when git already tracks it (never merely because the directory exists on disk):
                // a gitignored .semanticus makes `git add` error "paths are ignored", and staging it wholesale would
                // try to add the broker's locked engine.lock. Tracked-only matches the "tracked files" UI promise.
                var tracked = await GitCli.RunAsync(root, "ls-files", "--", sidecarKey);
                if (tracked.Ok && Lines(tracked.Stdout).Length > 0) paths.Add(sidecarKey);
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
