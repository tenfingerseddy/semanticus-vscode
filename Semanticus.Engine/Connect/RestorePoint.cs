using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Semanticus.Engine
{
    /// <summary>
    /// A pre-push snapshot of a PUBLISHED model's metadata, written to disk before a live write so the write can be
    /// undone. This exists because a live delete is otherwise PERMANENT: <c>LiveDeploy.RemoveExplicit</c> removes 11
    /// object kinds, but <c>SyncModels</c> can only ever add back measures, calculated columns, calculated tables and
    /// named expressions. Relationship, role (RLS), perspective, hierarchy, partition, culture, datasource and data
    /// table are otherwise lost with no way home — and a push sourced from a file or git ref creates no undo step at
    /// all (a deploy is not a model mutation, so its Revision is 0).
    ///
    /// The push ALREADY exports the live target's full metadata immediately before writing (snapshot B, the drift
    /// baseline). This just keeps it instead of deleting it. Kane's rule, ratified 2026-07-09:
    /// <b>no restore point, no delete</b> — fail closed.
    ///
    /// Location is the user's home (<c>~/.semanticus/restore/</c>), NOT a temp dir: a restore point is an artifact the
    /// user is meant to know about, find and prune. It holds the target's full model METADATA (measures, M queries,
    /// role filters, datasource connection strings — never row data) in clear text, exactly as the model file on disk
    /// does. Retention is bounded per target; <c>purge_restore_points</c> removes them on demand.
    /// </summary>
    public sealed class RestorePointRecord
    {
        public string Id { get; set; }              // 20260710T031500Z-3f9c1a — sortable, unique per target
        public string Endpoint { get; set; }
        public string Database { get; set; }
        public string CapturedUtc { get; set; }     // round-trip ISO 8601
        public string BimPath { get; set; }         // the snapshot itself
        public long Bytes { get; set; }
        public string Op { get; set; }              // the op that created it, e.g. apply_model_diff
        public string Reason { get; set; }          // human summary: "3 change(s), 1 delete(s)"
        public string[] Pushed { get; set; } = Array.Empty<string>();
        public string[] Deleted { get; set; } = Array.Empty<string>();
        public string IntegrityError { get; set; }
    }

    public sealed class RestorePointPurgeCandidate
    {
        public string Id { get; set; }
        public string Endpoint { get; set; }
        public string Database { get; set; }
        public string CapturedUtc { get; set; }
        public long Bytes { get; set; }
        public string IntegrityError { get; set; }
    }

    public sealed class RestorePointPurgeResult
    {
        public RestorePointPurgeCandidate[] Candidates { get; set; } = Array.Empty<RestorePointPurgeCandidate>();
        public int Matched { get; set; }
        public int Removed { get; set; }
        public string[] FailedIds { get; set; } = Array.Empty<string>();
        public bool Confirmed { get; set; }
        public bool Completed { get; set; }
        public string ConfirmToken { get; set; }
        public string Error { get; set; }
        public string Note { get; set; }
    }

    public static class RestorePointStore
    {
        public const int MaxPerTarget = 10;
        private const string DirName = "restore";
        private const long MaxManifestBytes = 1024 * 1024;
        private static readonly object Gate = new object();
        private static readonly Regex SafeId = new Regex("^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{6}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SafeTarget = new Regex("^[0-9a-f]{12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        /// <summary>Test seam: point the store at a scratch dir so tests never write to the real home.</summary>
        public static string RootOverride { get; set; }

        /// <summary>Test seam for deterministic delete-failure coverage. Production always uses File.Delete.</summary>
        internal static Action<string> DeleteFileOverride { get; set; }

        private static void DeleteFile(string path)
        {
            if (DeleteFileOverride != null) DeleteFileOverride(path);
            else File.Delete(path);
        }

        public static string Root() => RootOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".semanticus", DirName);

        // An endpoint is a URL and a database is a free-text name; neither is a safe path component. Hash the PAIR so
        // one target maps to exactly one directory, and retention can never prune across targets.
        internal static string TargetKey(string endpoint, string database)
        {
            using var sha = SHA256.Create();
            var raw = (endpoint ?? "") + "\0" + (database ?? "");
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).Substring(0, 12).ToLowerInvariant();
        }

        private static string TargetDir(string endpoint, string database) => Path.Combine(Root(), TargetKey(endpoint, database));

        /// <summary>Persist a snapshot. The caller must pass the metadata of the target as it was BEFORE the push —
        /// serialize it before any merge mutates the in-memory database. Throws if the write fails; a caller about to
        /// DELETE must treat that as fatal and refuse the push.</summary>
        public static RestorePointRecord Write(string endpoint, string database, string bimJson, string op, string reason,
                                               IEnumerable<string> pushed, IEnumerable<string> deleted)
        {
            if (string.IsNullOrEmpty(bimJson)) throw new ArgumentException("A restore point needs the target's metadata.", nameof(bimJson));

            lock (Gate)
            {
                var root = Path.GetFullPath(Root());
                Directory.CreateDirectory(root);
                if (IsReparsePoint(root)) throw new IOException("The restore-point root is a link; refusing to write metadata through it.");
                var dir = TargetDir(endpoint, database);
                if (!IsWithin(root, dir)) throw new IOException("The restore-point target resolved outside its store.");
                if (Directory.Exists(dir) && IsReparsePoint(dir)) throw new IOException("The restore-point target folder is a link; refusing to write metadata through it.");
                Directory.CreateDirectory(dir);
                if (IsReparsePoint(dir)) throw new IOException("The restore-point target folder became a link; refusing to write metadata through it.");

                var now = DateTimeOffset.UtcNow;
                var id = now.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                var bim = Path.Combine(dir, id + ".bim");

                File.WriteAllText(bim, bimJson);
                var rec = new RestorePointRecord
                {
                    Id = id,
                    Endpoint = endpoint,
                    Database = database,
                    CapturedUtc = now.ToString("O"),
                    BimPath = bim,
                    Bytes = new FileInfo(bim).Length,
                    Op = op,
                    Reason = reason,
                    Pushed = pushed?.ToArray() ?? Array.Empty<string>(),
                    Deleted = deleted?.ToArray() ?? Array.Empty<string>(),
                };
                File.WriteAllText(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(rec, JsonOpts));

                Prune(dir);
                return rec;
            }
        }

        // Keep the newest MaxPerTarget. Pruning is best-effort: losing an OLD restore point is not a correctness
        // failure, and it must never take down the push that just wrote a new one.
        private static void Prune(string dir)
        {
            try
            {
                var stale = Directory.EnumerateFiles(dir, "*.json")
                    .OrderByDescending(p => p, StringComparer.Ordinal)   // id is lexicographically sortable by time
                    .Skip(MaxPerTarget).ToList();
                foreach (var json in stale)
                {
                    try { File.Delete(Path.ChangeExtension(json, ".bim")); } catch { }
                    try { File.Delete(json); } catch { }
                }
            }
            catch { /* best-effort */ }
        }

        private sealed class Entry
        {
            public RestorePointRecord Record { get; set; }
            public string TargetKey { get; set; }
            public string ManifestPath { get; set; }
            public string SnapshotPath { get; set; }
        }

        private static StringComparison PathComparison => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static bool IsWithin(string parent, string path)
        {
            var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, PathComparison);
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        private static bool TryEntryAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path); // lstat semantics preserve broken-link visibility
                return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            attributes = default;
            return false;
        }

        private static IReadOnlyList<Entry> Entries(string endpoint = null, string database = null)
        {
            var root = Path.GetFullPath(Root());
            if (!Directory.Exists(root) || IsReparsePoint(root)) return Array.Empty<Entry>();

            string[] dirs;
            try
            {
                dirs = (endpoint != null && database != null)
                    ? new[] { TargetDir(endpoint, database) }.Where(Directory.Exists).ToArray()
                    : Directory.EnumerateDirectories(root).ToArray();
            }
            catch { return Array.Empty<Entry>(); }

            var all = new List<Entry>();
            foreach (var dir in dirs)
            {
                var targetKey = Path.GetFileName(dir);
                if (!IsWithin(root, dir) || !SafeTarget.IsMatch(targetKey) || IsReparsePoint(dir)) continue;
                string[] manifests;
                try { manifests = Directory.EnumerateFiles(dir, "*.json").ToArray(); }
                catch { continue; }
                foreach (var json in manifests)
                {
                    if (!IsWithin(dir, json)) continue;
                    var actualId = Path.GetFileNameWithoutExtension(json);
                    if (!SafeId.IsMatch(actualId)) continue;

                    RestorePointRecord rec = null;
                    var manifestParsed = false;
                    if (IsReparsePoint(json))
                        rec = UnsafeRecord(actualId, "The restore point manifest is a reparse point and was not read.");
                    else try
                    {
                        if (new FileInfo(json).Length > MaxManifestBytes)
                            rec = UnsafeRecord(actualId, "The restore point manifest exceeds the safe size limit and was not read.");
                        else
                        {
                            rec = JsonSerializer.Deserialize<RestorePointRecord>(File.ReadAllText(json), JsonOpts);
                            manifestParsed = rec != null;
                            if (rec == null) rec = UnsafeRecord(actualId, "The restore point manifest is empty or invalid.");
                        }
                    }
                    catch { rec = UnsafeRecord(actualId, "The restore point manifest is not valid JSON and was not read."); }
                    var manifestId = manifestParsed ? rec.Id : null;
                    var targetMatches = !manifestParsed || string.Equals(TargetKey(rec.Endpoint, rec.Database), targetKey, StringComparison.OrdinalIgnoreCase);
                    if (manifestParsed) rec.IntegrityError = null; // never trust a persisted integrity verdict

                    var snapshot = Path.ChangeExtension(json, ".bim");
                    if (!IsWithin(dir, snapshot)) continue;
                    bool snapshotExists;
                    long snapshotBytes;
                    try
                    {
                        snapshotExists = TryEntryAttributes(snapshot, out var snapshotAttributes);
                        var snapshotReparse = snapshotExists && (snapshotAttributes & FileAttributes.ReparsePoint) != 0;
                        var snapshotDirectory = snapshotExists && (snapshotAttributes & FileAttributes.Directory) != 0;
                        snapshotBytes = snapshotExists && !snapshotReparse && !snapshotDirectory ? new FileInfo(snapshot).Length : 0;
                        if (snapshotReparse)
                            AddIntegrityError(rec, "The restore point snapshot is a reparse point and cannot be used for rollback.");
                        else if (snapshotDirectory)
                            AddIntegrityError(rec, "The restore point snapshot path is a directory and cannot be used for rollback.");
                    }
                    catch
                    {
                        snapshotExists = false;
                        snapshotBytes = 0;
                        AddIntegrityError(rec, "The restore point snapshot could not be inspected.");
                    }

                    // Id and BimPath are duplicated inside a clear-text manifest. Never trust either for IO: the
                    // enumerated, validated sibling names are the authority for list, rollback and purge.
                    rec.Id = actualId;
                    if (manifestParsed && !string.Equals(manifestId, actualId, StringComparison.OrdinalIgnoreCase))
                        AddIntegrityError(rec, "The manifest id does not match its validated filename.");
                    if (!targetMatches)
                        AddIntegrityError(rec, "The manifest target does not match its validated storage folder.");
                    rec.BimPath = snapshotExists && string.IsNullOrEmpty(rec.IntegrityError) ? snapshot : null;
                    if (TryCapturedUtc(actualId, out var captured)) rec.CapturedUtc = captured.ToString("O");
                    rec.Bytes = snapshotBytes;
                    all.Add(new Entry { Record = rec, TargetKey = targetKey, ManifestPath = json, SnapshotPath = snapshot });
                }
            }
            return all.OrderByDescending(r => r.Record.Id, StringComparer.Ordinal).ToList();
        }

        private static RestorePointRecord UnsafeRecord(string id, string error) => new RestorePointRecord
        {
            Id = id,
            IntegrityError = error,
        };

        private static void AddIntegrityError(RestorePointRecord record, string error)
        {
            record.IntegrityError = string.IsNullOrWhiteSpace(record.IntegrityError)
                ? error
                : record.IntegrityError + " " + error;
        }

        /// <summary>Newest first. A manifest whose .bim has vanished is reported with a null BimPath rather than
        /// silently dropped — a restore point you cannot restore from must be visible, not absent.</summary>
        public static IReadOnlyList<RestorePointRecord> List(string endpoint = null, string database = null) =>
            Entries(endpoint, database).Select(x => x.Record).ToList();

        private static bool TryCapturedUtc(string id, out DateTimeOffset captured)
        {
            captured = default;
            if (!SafeId.IsMatch(id)) return false;
            return DateTimeOffset.TryParseExact(id.Substring(0, 16), "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out captured);
        }

        private static RestorePointPurgeCandidate Candidate(Entry x) => new RestorePointPurgeCandidate
        {
            Id = x.Record.Id,
            Endpoint = x.Record.Endpoint,
            Database = x.Record.Database,
            CapturedUtc = x.Record.CapturedUtc,
            Bytes = x.Record.Bytes,
            IntegrityError = x.Record.IntegrityError,
        };

        private static string PurgeToken(string selector, IReadOnlyList<Entry> entries)
        {
            var identity = string.Join("\n", entries
                .Select(x => x.TargetKey + "/" + x.Record.Id)
                .OrderBy(x => x, StringComparer.Ordinal));
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(selector + "\n" + identity));
            return "PURGE-" + Convert.ToHexString(bytes).Substring(0, 32);
        }

        public static RestorePointRecord Find(string id) =>
            string.IsNullOrWhiteSpace(id) ? null : List().FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Preview or delete exactly one restore point by id, or every restore point older than the supplied
        /// non-negative age. Selection and confirmation live here so both public doors share the same fail-closed rule.</summary>
        public static RestorePointPurgeResult Purge(string id = null, int? olderThanDays = null, bool confirm = false, string confirmToken = null)
        {
            id = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
            var selectorCount = (id == null ? 0 : 1) + (olderThanDays.HasValue ? 1 : 0);
            if (selectorCount != 1)
                return new RestorePointPurgeResult { Error = "Choose exactly one selector: id or olderThanDays.", Note = "Nothing was deleted." };
            if (olderThanDays < 0)
                return new RestorePointPurgeResult { Error = "olderThanDays must be zero or greater.", Note = "Nothing was deleted." };
            if (id != null && !SafeId.IsMatch(id))
                return new RestorePointPurgeResult { Error = "The restore point id is not valid. List the available restore points and use an exact id from that result.", Note = "Nothing was deleted." };

            lock (Gate)
            {
                var entries = Entries();
                var cutoff = olderThanDays.HasValue ? DateTimeOffset.UtcNow.AddDays(-olderThanDays.Value) : (DateTimeOffset?)null;
                var selected = entries.Where(x => id != null
                    ? string.Equals(x.Record.Id, id, StringComparison.OrdinalIgnoreCase)
                    : cutoff.HasValue && TryCapturedUtc(x.Record.Id, out var captured) && captured < cutoff.Value).ToList();
                var selector = id != null ? "id:" + id.ToLowerInvariant() : "age:" + olderThanDays.Value.ToString(CultureInfo.InvariantCulture);
                var result = new RestorePointPurgeResult
                {
                    Candidates = selected.Select(Candidate).ToArray(),
                    Matched = selected.Count,
                };

                if (id != null && selected.Count == 0)
                {
                    result.Error = "No restore point with that id was found.";
                    result.Note = "Nothing was deleted.";
                    return result;
                }
                if (id != null && selected.Count > 1)
                {
                    result.Error = "That id matched more than one restore point. Refusing an ambiguous delete.";
                    result.Note = "Nothing was deleted.";
                    return result;
                }

                var token = PurgeToken(selector, selected);
                if (!confirm)
                {
                    result.ConfirmToken = selected.Count == 0 ? null : token;
                    result.Note = selected.Count == 0
                        ? "DRY RUN: no restore points match this selector."
                        : $"DRY RUN: {selected.Count} restore point(s) would be permanently deleted. Re-run with confirmation and this exact token.";
                    return result;
                }

                if (selected.Count == 0)
                {
                    result.Confirmed = true;
                    result.Completed = true;
                    result.Note = "No restore points matched; nothing was deleted.";
                    return result;
                }
                if (!string.Equals(confirmToken, token, StringComparison.Ordinal))
                {
                    result.Error = "The confirmation token does not match the current preview. Run the dry-run again and review its candidates.";
                    result.Note = "Nothing was deleted.";
                    return result;
                }

                result.Confirmed = true;
                var failed = new List<string>();
                foreach (var entry in selected)
                {
                    try
                    {
                        DeleteSnapshotSibling(entry.SnapshotPath);
                        if (TryEntryAttributes(entry.SnapshotPath, out _)) throw new IOException("The snapshot path still exists after delete.");
                        DeleteFile(entry.ManifestPath);
                        if (File.Exists(entry.ManifestPath)) throw new IOException("The manifest file still exists after delete.");
                        result.Removed++;
                    }
                    catch { failed.Add(entry.Record.Id); }
                }
                result.FailedIds = failed.ToArray();
                result.Completed = failed.Count == 0;
                result.Error = failed.Count == 0 ? null : $"Could not completely remove {failed.Count} restore point(s). Their ids are in failedIds.";
                result.Note = failed.Count == 0
                    ? $"Permanently deleted {result.Removed} restore point(s)."
                    : $"Deleted {result.Removed} of {result.Matched} restore point(s); {failed.Count} failed and were not counted as removed.";
                return result;
            }
        }

        private static void DeleteSnapshotSibling(string path)
        {
            if (!TryEntryAttributes(path, out var attributes)) return;
            if ((attributes & FileAttributes.Directory) == 0)
            {
                DeleteFile(path);
                return;
            }
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                throw new IOException("The snapshot path is an unexpected directory.");
            Directory.Delete(path, recursive: false); // delete the directory link, never its target
        }
    }
}
