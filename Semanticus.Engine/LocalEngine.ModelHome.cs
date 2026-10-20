using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Semanticus.Engine
{
    // ONE answer to "where does this model's own work live" (fix-model-home, after three rounds of the same defect). The plan memory, the Query
    // rows, the checks and their waivers each had their own idea of where, so each round fixed one path and the next path broke: with no folder,
    // Ctrl+S made the published model a file, a reload opened that file under a disk: key beside its own .semanticus, and the checks made before
    // it waited unseen in the home while Validation said nothing needed a look.
    //
    // A model has one identity across its forms. A published model, the file Semanticus saved it to, and the folder it is opened in are the
    // SAME model: the save records the published identity beside the file (model-home.json in the file's .semanticus), so the file reopens
    // under it (PaneIdentity). Its work has one place. A model that is a file of its own keeps it beside the file, as it always did. A published
    // model keeps it where it was first given a folder (an index in SEMANTICUS_HOME remembers which); before that, in the open folder; with no
    // folder at all, in SEMANTICUS_HOME/models/<model>. When a folder becomes the place, what waited in the home moves into it, once.
    // Analysis records stay in the home (they are never written beside the model) and are keyed by this same identity.
    public sealed partial class LocalEngine
    {
        /// <summary>Where one model's own work lives, and who it is. <see cref="Dir"/> is a .semanticus-shaped folder; null when the model has
        /// nowhere (an unsaved model with no folder open). <see cref="Endpoint"/> and <see cref="Database"/> are set for a published model
        /// (open live, or a file saved from one).</summary>
        internal sealed class ModelPlace
        {
            public string Dir;
            public string Identity;
            public string Endpoint;
            public string Database;
            public bool InHome;
        }

        private readonly ConditionalWeakTable<Session, HashSet<string>> _placeCarried = new ConditionalWeakTable<Session, HashSet<string>>();

        private ModelPlace PlaceFor(SessionContext context) => PlaceFor(context?.Session);

        /// <summary>The folder open in the window, or null when none is. With no folder open the workspace is only the window's start folder
        /// (the host's working folder, often the person's home), which is never a place for anyone's work (UAT round 2, N1).</summary>
        private string FolderDir => _workspaceIsFolder ? _workspaceDir : null;

        /// <summary>The .semanticus a store falls back to for a model with no file of its own: the open folder's, else the model's own place
        /// (SEMANTICUS_HOME while it has no folder). Null for an unsaved model with no folder open. Never the window's start folder.</summary>
        private string NoFileSidecar(Session s) => FolderDir != null ? Path.Combine(FolderDir, LayoutStore.DirName) : PlaceFor(s)?.Dir;

        internal ModelPlace PlaceFor(Session s)
        {
            if (s == null) return null;
            var identity = PaneIdentity(s, null);
            var isFile = !ExperienceStore.IsEphemeralAnchor(s.SourcePath);
            var own = isFile ? LayoutStore.DirFor(s.SourcePath)
                    : _workspaceIsFolder && !string.IsNullOrWhiteSpace(_workspaceDir) ? Path.Combine(_workspaceDir, LayoutStore.DirName) : null;
            if (identity == null || !identity.StartsWith("live:", StringComparison.Ordinal))
                return new ModelPlace { Dir = own, Identity = identity };   // a file of its own, or an unsaved model: beside it, as always

            string endpoint = s.LiveOrigin?.Endpoint, database = s.LiveOrigin?.Database;
            if (s.LiveOrigin != null && isFile) ModelHome.Record(s.SourcePath, identity, endpoint, database);
            else if (s.LiveOrigin == null && isFile && ModelHome.RecordedFor(s.SourcePath) is { } rec) { endpoint = rec.Endpoint; database = rec.Database; }

            var home = ModelHome.HomeDir(identity);
            var placed = ModelHome.PlaceOf(identity);
            var dir = placed ?? own ?? home;
            if (dir == null) return new ModelPlace { Identity = identity, Endpoint = endpoint, Database = database };
            var inHome = home != null && SameDir(dir, home);
            CarryOnce(s, identity, dir, inHome, own, placed == null && !inHome);
            // The model already has its place, and this folder (or file) holds older work of its own for it: that work joins the place, so none
            // of it is hidden. Only this model's items are taken, and they are copied: the folder may hold other models' work too.
            if (placed != null && own != null && !SameDir(own, placed)) MergeOnce(s, identity, own, placed);
            return new ModelPlace { Dir = dir, Identity = identity, Endpoint = endpoint, Database = database, InHome = inHome };
        }

        /// <summary>Moves what this model left elsewhere into its place, once per session and place: the home copy, the checks home an earlier
        /// build of this lane kept (checks/), and checks an earlier build left in the window's start folder (read in, for this model
        /// only). Nothing the place already has is replaced. Never throws: a copy that stays where it is is moved by a later open.</summary>
        private void CarryOnce(Session s, string identity, string dir, bool inHome, string own, bool claim)
        {
            var done = _placeCarried.GetOrCreateValue(s);
            lock (done) if (!done.Add(identity + "\n" + dir)) return;
            try
            {
                if (!inHome && ModelHome.HomeDir(identity) is { } home && Directory.Exists(home))
                    if (ModelHome.CarryInto(home, dir, identity)) TryDeleteTree(home);
                if (claim) ModelHome.SetPlace(identity, dir);
                if (ModelHome.LegacyChecksDir(Sha8(identity)) is { } oldChecks && Directory.Exists(oldChecks))
                    if (ModelHome.CarryInto(oldChecks, dir, identity)) TryDeleteTree(oldChecks);
                // With no folder the workspace is only the window's start folder: checks an earlier build wrote there are read in, for this model only.
                if (own == null && !_workspaceIsFolder && !string.IsNullOrWhiteSpace(_workspaceDir) && !Directory.Exists(Path.Combine(dir, TestSuiteStore.SubDir)))
                {
                    var start = Path.Combine(_workspaceDir, LayoutStore.DirName);
                    var mine = TestSuiteStore.LoadSuite(start).Defs.Where(d => string.Equals(d.ModelIdentity, identity, StringComparison.Ordinal)).ToList();
                    foreach (var d in mine) TestSuiteStore.Upsert(dir, d, () => d.Id);
                    var ids = new HashSet<string>(mine.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
                    foreach (var w in CheckWaiverStore.Load(start).Where(w => ids.Contains(w.RuleId))) CheckWaiverStore.Upsert(dir, w);
                }
            }
            catch { /* what is left behind moves on a later open */ }
        }

        private void MergeOnce(Session s, string identity, string from, string into)
        {
            var done = _placeCarried.GetOrCreateValue(s);
            lock (done) if (!done.Add(identity + "\n" + from + "\n" + into)) return;
            ModelHome.CarryInto(from, into, identity, onlyThisModel: true);
        }

        private static bool SameDir(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                      OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
            catch { return false; }
        }
    }

    /// <summary>The files behind <see cref="LocalEngine.PlaceFor(Session)"/>: the identity a saved file records, the index of where each
    /// published model's work lives, and the move from one place into another.</summary>
    internal static class ModelHome
    {
        /// <summary>Beside a model file, in its .semanticus: which published model each file in that folder was saved from. Coordinates only,
        /// never a secret. IDENTITY ONLY, like the snapshot's origin marker: it names the model, and binds no deploy and no query.</summary>
        public const string MarkerName = "model-home.json";
        private const string ModelsDir = "models";
        private const string IndexName = "index.json";
        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        public sealed class Recorded
        {
            public string Identity { get; set; }
            public string Endpoint { get; set; }
            public string Database { get; set; }
        }

        private static string MarkerFor(string sourcePath)
        {
            var side = LayoutStore.DirFor(sourcePath);
            return side == null ? null : Path.Combine(side, MarkerName);
        }

        private static string Key(string sourcePath)
        {
            var full = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(full);
            return OperatingSystem.IsWindows() ? name.ToLowerInvariant() : name;
        }

        private static Dictionary<string, Recorded> ReadMarker(string file)
        {
            try { return File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, Recorded>>(File.ReadAllText(file)) ?? new Dictionary<string, Recorded>() : new Dictionary<string, Recorded>(); }
            catch { return new Dictionary<string, Recorded>(); }
        }

        /// <summary>The published model this file was saved from, or null (a file of its own, or one saved before this was recorded).</summary>
        public static Recorded RecordedFor(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return null;
            try
            {
                var file = MarkerFor(sourcePath);
                if (file == null || !File.Exists(file)) return null;
                lock (Gate) return ReadMarker(file).TryGetValue(Key(sourcePath), out var r) && !string.IsNullOrWhiteSpace(r?.Identity) ? r : null;
            }
            catch { return null; }
        }

        /// <summary>Say beside the file which published model it is. Written only when it is missing or says something else.</summary>
        public static void Record(string sourcePath, string identity, string endpoint, string database)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(identity)) return;
            try
            {
                var file = MarkerFor(sourcePath);
                if (file == null) return;
                lock (Gate)
                {
                    var all = ReadMarker(file);
                    var key = Key(sourcePath);
                    if (all.TryGetValue(key, out var have) && have?.Identity == identity && have.Endpoint == endpoint && have.Database == database) return;
                    all[key] = new Recorded { Identity = identity, Endpoint = endpoint, Database = database };
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(all, Json));
                    File.Move(file + ".tmp", file, overwrite: true);
                }
            }
            catch { /* without it the file reopens as a file of its own, as before */ }
        }

        private static string Root()
        {
            try { return SemanticusHome.Root(); } catch { return null; }   // no per-user folder: nowhere to keep it
        }

        /// <summary>The home copy of one published model's work, used while it has no folder.</summary>
        public static string HomeDir(string identity)
        {
            var root = Root();
            return root == null || string.IsNullOrEmpty(identity) ? null : Path.Combine(root, ModelsDir, Hash(identity));
        }

        /// <summary>Where an earlier build of this lane kept a published model's checks with no folder (SEMANTICUS_HOME/checks/&lt;sha8&gt;).</summary>
        public static string LegacyChecksDir(string sha8)
        {
            var root = Root();
            return root == null ? null : Path.Combine(root, "checks", sha8);
        }

        private static string Hash(string s)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s)), 0, 8).ToLowerInvariant();
        }

        private static string IndexFile() => Root() is { } r ? Path.Combine(r, ModelsDir, IndexName) : null;

        private static Dictionary<string, string> ReadIndex(string file)
        {
            try { return File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new Dictionary<string, string>() : new Dictionary<string, string>(); }
            catch { return new Dictionary<string, string>(); }
        }

        /// <summary>The folder a published model's work was given, while it still exists; else null.</summary>
        public static string PlaceOf(string identity)
        {
            var file = IndexFile();
            if (file == null) return null;
            lock (Gate) return ReadIndex(file).TryGetValue(identity, out var dir) && !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;
        }

        public static void SetPlace(string identity, string dir)
        {
            var file = IndexFile();
            if (file == null) return;
            try
            {
                lock (Gate)
                {
                    var all = ReadIndex(file);
                    all[identity] = Path.GetFullPath(dir);
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(all, Json));
                    File.Move(file + ".tmp", file, overwrite: true);
                }
            }
            catch { /* the model's place is worked out again on the next open */ }
        }

        /// <summary>Move one model's work from <paramref name="from"/> into <paramref name="to"/>: its checks, their waivers, runs and the
        /// files beside them, its plan memory entry, and every other file it kept there. Nothing <paramref name="to"/> already has is replaced.
        /// True when everything is across, so <paramref name="from"/> may go. With <paramref name="onlyThisModel"/>, <paramref name="from"/> is a
        /// folder other models may share: only the checks bound to this model, their waivers and its plan entry are taken. (Query rows never
        /// move: a published model's rows stay in SEMANTICUS_HOME in every form, LocalEngine.Query.cs DataQuestionFile.)</summary>
        public static bool CarryInto(string from, string to, string identity, bool onlyThisModel = false)
        {
            try
            {
                var tests = Path.Combine(from, TestSuiteStore.SubDir);
                if (onlyThisModel && !Directory.Exists(tests) && !File.Exists(PlanMemory.FileIn(from))) return true;
                Directory.CreateDirectory(to);
                if (Directory.Exists(tests))
                {
                    var have = new HashSet<string>(TestSuiteStore.LoadSuite(to).Defs.Select(d => d.Id), StringComparer.Ordinal);
                    var mine = TestSuiteStore.LoadSuite(from).Defs.Where(d => !onlyThisModel || string.Equals(d.ModelIdentity, identity, StringComparison.Ordinal)).ToList();
                    foreach (var d in mine.Where(d => !have.Contains(d.Id)))
                        if (TestSuiteStore.Upsert(to, d, () => d.Id) == null) return false;
                    var ids = new HashSet<string>(mine.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
                    var waived = new HashSet<string>(CheckWaiverStore.Load(to).Select(w => w.RuleId), StringComparer.OrdinalIgnoreCase);
                    foreach (var w in CheckWaiverStore.Load(from).Where(w => !waived.Contains(w.RuleId) && (!onlyThisModel || ids.Contains(w.RuleId))))
                        if (!CheckWaiverStore.Upsert(to, w)) return false;
                    if (!onlyThisModel)
                    {
                        var runs = new HashSet<string>(TestSuiteStore.ReadRunLines(to), StringComparer.Ordinal);
                        foreach (var line in TestSuiteStore.ReadRunLines(from).Where(l => !runs.Contains(l))) TestSuiteStore.AppendRun(to, line);
                        // The files beside the checks (which object each check names, the SQL table mapping, the last row counts): taken when the place has none.
                        var waiverFile = Path.GetFileName(CheckWaiverStore.FileFor(from));
                        foreach (var f in Directory.EnumerateFiles(tests))
                        {
                            var name = Path.GetFileName(f);
                            var dest = Path.Combine(to, TestSuiteStore.SubDir, name);
                            if (string.Equals(name, waiverFile, StringComparison.OrdinalIgnoreCase) || File.Exists(dest) || IsSuiteFile(name)) continue;
                            Directory.CreateDirectory(Path.GetDirectoryName(dest));
                            File.Copy(f, dest);
                        }
                    }
                }
                var planFrom = PlanMemory.FileIn(from);
                if (planFrom != null && File.Exists(planFrom) && !PlanMemory.CarryModel(planFrom, PlanMemory.FileIn(to), identity)) return false;
                // Everything else the model kept while it had no folder (its History, primers, interview packs, ...) comes along too,
                // file by file, where the place has no such file yet. Nothing the place has is replaced.
                if (!onlyThisModel)
                    foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(from, f);
                        var top = rel.Split(Path.DirectorySeparatorChar)[0];
                        if (top == TestSuiteStore.SubDir || rel == PlanMemory.FileName
                            || rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) continue;
                        var dest = Path.Combine(to, rel);
                        if (File.Exists(dest)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        File.Copy(f, dest);
                    }
                return true;
            }
            catch { return false; }
        }

        // The suite's own files are moved by their stores above (definitions, runs), never copied whole over what the place has.
        private static bool IsSuiteFile(string name) =>
            string.Equals(name, TestSuiteStore.SuiteFile, StringComparison.OrdinalIgnoreCase) || string.Equals(name, TestSuiteStore.RunsFile, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
    }
}
