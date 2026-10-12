using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Semanticus.Engine
{
    /// <summary>
    /// Reads/writes the engine-owned diagram-layout sidecar (<c>.semanticus/layout.json</c>) that lives BESIDE the
    /// model on disk — NOT inside its TMDL <c>definition/</c>. This closes hole #1 (docs/strategy/04 §D1): diagram
    /// positions used to live only in the webview's per-machine <c>vscode.setState</c>, so a layout never round-tripped
    /// and the diagram was demo-only. Now the engine owns layout, so both doors (UI + agent) share one persisted set.
    ///
    /// Two deliberate properties:
    ///  • <b>Keyed by LineageTag</b> (a model object's stable GUID, present on CL ≥ 1540), with a <b>Name fallback</b>
    ///    for older/tagless models. The LineageTag key is why a position SURVIVES A RENAME — the diff-clean rename
    ///    contract (Phase 1b) changes the name, not the tag, so the box stays put.
    ///  • <b>Excluded from the model diff.</b> The sidecar is outside the model definition, so <c>ModelCompare</c>
    ///    (which loads the model through TOM) never sees it — layout never pollutes the "PR for your model" diff that
    ///    the change-plan depends on. Annotations-in-the-model (the rejected D1 alternative) WOULD have polluted it.
    ///
    /// The PBIP-native <c>diagramLayout.json</c> (Power BI Desktop's own diagram positions, beside the model in the
    /// .SemanticModel folder) is READ as a <b>read-only base layer</b> via <see cref="ReadNativePbip"/>, so a freshly
    /// opened PBIP's diagram matches what the user sees in Desktop; the engine sidecar then OVERLAYS it per object, so
    /// a reposition in Semanticus wins and persists. We still do NOT WRITE diagramLayout.json: MS's projects-dataset
    /// docs (verified 2026-06-28) state it "doesn't support external editing" during the PBIP preview — Desktop
    /// regenerates it, so an external write would be clobbered. The sidecar remains the sole writable layout layer.
    /// </summary>
    internal static class LayoutStore
    {
        public const string DirName = ".semanticus";
        /// <summary>A folder's one file of decision switches from before each model kept its own, still read, and where
        /// each model in the folder also records its offs with its name (LocalEngine.Decisions.cs).</summary>
        public const string DecisionSettingsFile = "decision-settings.json";
        /// <summary>Each model's own decision switches, one file per model.</summary>
        public const string DecisionSettingsFolder = "decision-settings";
        public const string FileName = "layout.json";
        private const int CurrentVersion = 1;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,                                  // human-diffable if a user does choose to commit it
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>One persisted node: the stable key (lineageTag, may be null) + name (fallback key + readability)
        /// + the box. Width/Height are 0 when the UI hasn't sized the node (it falls back to its default).</summary>
        public sealed class Entry
        {
            public string LineageTag { get; set; }
            public string Name { get; set; }
            public double X { get; set; }
            public double Y { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            /// <summary>The Diagram leaves this table where it is when it makes room for the lines (LayoutNode.Keep).
            /// Written only when set, so a sidecar without kept tables reads exactly as before.</summary>
            [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
            public bool? Keep { get; set; }
        }

        private sealed class FileShape
        {
            public int Version { get; set; } = CurrentVersion;
            public List<Entry> Tables { get; set; } = new List<Entry>();
        }

        /// <summary>The sidecar directory for a model opened/saved at <paramref name="sourcePath"/> (a folder or a
        /// file), or null when the model has never been saved (no on-disk anchor → nothing to persist beside).</summary>
        public static string DirFor(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return null;
            var full = Path.GetFullPath(sourcePath);
            var modelDir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(modelDir)) return null;
            // For a PBIP the model anchor is the inner definition/ folder, but engine sidecars belong in the
            // .SemanticModel PARENT (beside diagramLayout.json / VerifiedAnswers), NOT inside the TMDL-only,
            // publishable definition/ tree. Hop up one level when the anchor is a PBIP definition folder.
            if (ModelPathResolver.IsPbipDefinitionFolder(modelDir))
                modelDir = Directory.GetParent(modelDir)?.FullName ?? modelDir;
            return Path.Combine(modelDir, DirName);
        }

        public static string FileFor(string sourcePath)
        {
            var dir = DirFor(sourcePath);
            return dir == null ? null : Path.Combine(dir, FileName);
        }

        /// <summary>The stable key for an entry/object: the LineageTag when present, else a name-scoped key. Both the
        /// reader and writer key the same way, so a rename (tag-stable) keeps its box and a tagless model falls back
        /// to name matching.</summary>
        public static string Key(string lineageTag, string name) =>
            string.IsNullOrEmpty(lineageTag) ? "name:" + (name ?? string.Empty) : lineageTag;

        /// <summary>Read the persisted entries (empty list when there is no sidecar yet, or it's unreadable — a
        /// corrupt/partial layout file must never break opening a model). Pure file IO; call off the dispatcher.</summary>
        public static List<Entry> Read(string sourcePath)
        {
            var file = FileFor(sourcePath);
            if (file == null || !File.Exists(file)) return new List<Entry>();
            try
            {
                var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(file), JsonOpts);
                return shape?.Tables ?? new List<Entry>();
            }
            catch { return new List<Entry>(); }   // best-effort: layout is non-authoritative; never throw on read
        }

        /// <summary>Read Power BI Desktop's native diagram layout (<c>diagramLayout.json</c>, beside the model in a
        /// PBIP's .SemanticModel folder) into the same <see cref="Entry"/> shape — keyed by the table's LineageTag
        /// (the file's <c>nodeLineageTag</c>) with the table name (<c>nodeIndex</c>) as the fallback key. This is the
        /// READ-ONLY base layer so a freshly opened PBIP matches Desktop; the engine sidecar overlays it. Best-effort:
        /// any absence / parse error yields an empty list (layout is non-authoritative — never break opening a model).
        /// When a table appears in several diagrams, the lowest-ordinal (default view) position is taken.</summary>
        public static List<Entry> ReadNativePbip(string sourcePath)
        {
            var entries = new List<Entry>();
            var file = NativeFileFor(sourcePath);
            if (file == null) return entries;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("diagrams", out var diagrams) || diagrams.ValueKind != JsonValueKind.Array)
                    return entries;
                // Visit diagrams so the position the user actually SEES wins: the default diagram (root "defaultDiagram")
                // first, then lowest ordinal. ValueKind-guard the ordinal read — JsonElement.TryGetInt32 THROWS on a
                // non-Number element (e.g. "ordinal":"0"), which via the outer catch would silently drop EVERY native
                // position; the guard contains a malformed diagram to itself.
                var defaultDiagram = doc.RootElement.TryGetProperty("defaultDiagram", out var dd) && dd.ValueKind == JsonValueKind.String ? dd.GetString() : null;
                int Ordinal(JsonElement d) => d.TryGetProperty("ordinal", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var oi) ? oi : int.MaxValue;
                string DiagName(JsonElement d) => d.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null;
                var seen = new HashSet<string>(); // first placement wins (default diagram, then lowest ordinal)
                foreach (var diagram in diagrams.EnumerateArray()
                             .OrderBy(d => defaultDiagram != null && string.Equals(DiagName(d), defaultDiagram, StringComparison.Ordinal) ? 0 : 1)
                             .ThenBy(Ordinal))
                {
                    if (!diagram.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) continue;
                    foreach (var n in nodes.EnumerateArray())
                    {
                        var tag = n.TryGetProperty("nodeLineageTag", out var lt) && lt.ValueKind == JsonValueKind.String ? lt.GetString() : null;
                        var name = n.TryGetProperty("nodeIndex", out var ni) && ni.ValueKind == JsonValueKind.String ? ni.GetString() : null;
                        if (string.IsNullOrEmpty(tag) && string.IsNullOrEmpty(name)) continue;
                        if (!seen.Add(Key(tag, name))) continue;            // already placed from a lower-ordinal diagram
                        double X = 0, Y = 0, W = 0, H = 0;
                        if (n.TryGetProperty("location", out var loc)) { X = GetD(loc, "x"); Y = GetD(loc, "y"); }
                        if (n.TryGetProperty("size", out var sz)) { W = GetD(sz, "width"); H = GetD(sz, "height"); }
                        entries.Add(new Entry { LineageTag = tag, Name = name, X = X, Y = Y, Width = W, Height = H });
                    }
                }
            }
            catch { return new List<Entry>(); }   // best-effort: a corrupt/partial native file must never break opening
            return entries;
        }

        private static double GetD(JsonElement obj, string prop)
            => obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

        /// <summary>diagramLayout.json lives in the .SemanticModel folder — the parent of a PBIP's definition/ TMDL
        /// root. Returns null when there is no such file (not a PBIP, or no native layout saved yet).</summary>
        private static string NativeFileFor(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return null;
            var full = Path.GetFullPath(sourcePath);
            var modelDir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(modelDir)) return null;
            if (ModelPathResolver.IsPbipDefinitionFolder(modelDir))
                modelDir = Directory.GetParent(modelDir)?.FullName ?? modelDir;
            var candidate = Path.Combine(modelDir, "diagramLayout.json");
            return File.Exists(candidate) ? candidate : null;
        }

        /// <summary>Write the entries to the sidecar (creating <c>.semanticus/</c>). Returns the file path. Pure file
        /// IO; call off the dispatcher.</summary>
        public static string Write(string sourcePath, IEnumerable<Entry> entries)
        {
            var dir = DirFor(sourcePath);
            if (dir == null) throw new InvalidOperationException("The model has no on-disk location to store layout beside.");
            Directory.CreateDirectory(dir);
            EnsureRuntimeIgnore(dir);
            var file = Path.Combine(dir, FileName);
            var shape = new FileShape { Version = CurrentVersion, Tables = entries.ToList() };
            File.WriteAllText(file, JsonSerializer.Serialize(shape, JsonOpts));
            return file;
        }

        /// <summary>Ignore engine runtime files so a model checkpoint can keep primers and workflows without
        /// committing the live log. The one writer of <c>.semanticus/.gitignore</c>: see <see cref="WriteIgnore"/>.</summary>
        public static void EnsureRuntimeIgnore(string dir) => WriteIgnore(dir, withSwitch: false);

        /// <summary>Keeps the What Pro decides files that mean nothing after Power BI Desktop closes out of the model's
        /// commits: one per run of Desktop or live endpoint, and the three shared Desktop files. The per-file and per-title
        /// files are the ones worth keeping. Adds only these two lines, once.</summary>
        public static void EnsureSwitchIgnore(string dir) => WriteIgnore(dir, withSwitch: true);

        /// <summary>Test seam: runs before every try to read or replace the ignore file and may throw, to stand in for the sharing violation
        /// another writer causes on Windows.</summary>
        internal static Action<string> IgnoreIoHookForTest;

        /// <summary>Test seam: the waits between tries. Null keeps the real ones.</summary>
        internal static int[] IgnoreRetryDelaysForTest;

        private static readonly int[] IgnoreRetryDelaysMs = { 20, 50, 100, 200, 400, 600 };

        // Every writer in this process goes through here one at a time. The History, the experience log, the question history, a layout
        // save and a checkpoint all call in from different threads, and two of them reading, appending and replacing the same small
        // file at once is what failed a Windows folder save with a sharing violation (and could double a line elsewhere).
        private static readonly object IgnoreGate = new object();

        /// <summary>The one writer of <c>.semanticus/.gitignore</c>. It writes only when the file is missing or lacks a line, goes through a temp
        /// file and an atomic replace so no reader sees half a file, and waits out a sharing violation for about 1.4 s. It never throws: a
        /// missing ignore line must not fail a save, a layout write or a History write, and the next caller adds the line.</summary>
        private static void WriteIgnore(string dir, bool withSwitch)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                lock (IgnoreGate)
                {
                    Directory.CreateDirectory(dir);
                    var gitignore = Path.Combine(dir, ".gitignore");
                    WithIgnoreRetry(gitignore, () =>
                    {
                        var existing = File.Exists(gitignore) ? ReadShared(gitignore) : null;
                        var wanted = IgnoreTextFor(existing, withSwitch);
                        if (wanted == null) return;
                        var tmp = gitignore + ".tmp";
                        File.WriteAllText(tmp, wanted);
                        File.Move(tmp, gitignore, overwrite: true);
                    });
                }
            }
            catch { /* the ignore line is a courtesy */ }
        }

        private static string ReadShared(string file)
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return reader.ReadToEnd();
        }

        private static void WithIgnoreRetry(string gitignore, Action act)
        {
            var delays = IgnoreRetryDelaysForTest ?? IgnoreRetryDelaysMs;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    IgnoreIoHookForTest?.Invoke(gitignore);
                    act();
                    return;
                }
                catch (Exception ex) when (attempt < delays.Length
                    && (ex is IOException || ex is UnauthorizedAccessException)
                    && !(ex is DirectoryNotFoundException || ex is FileNotFoundException || ex is PathTooLongException))
                {
                    Thread.Sleep(delays[attempt]);
                }
            }
        }

        /// <summary>What the file should say, or null when it already says it. The edit ledger (Plan-heart E4) and the data question history
        /// (ask_data, 26 Sep) came after many sidecars already had this file, so each one's lines are added to an existing one once. Only
        /// these lines: a line the person removed on purpose is not put back.</summary>
        private static string IgnoreTextFor(string existing, bool withSwitch)
        {
            if (existing == null)
                existing = "engine.lock\nengine.json\nexperience.jsonl\nbaselines/vitals.jsonl\n" + HistoryIgnore + QuestionsIgnore
                         + (withSwitch ? SwitchRunIgnore + "\n" + SwitchDesktopIgnore + "\n" : "");
            else
            {
                var lines = existing.Split('\n').Select(l => l.Trim()).ToList();
                var add = (lines.Contains(EditLedger.FileName) ? "" : HistoryIgnore)
                        + (lines.Contains(Query.DataQuestionStore.FileName) ? "" : QuestionsIgnore)
                        + (withSwitch && !lines.Contains(SwitchRunIgnore) ? SwitchRunIgnore + "\n" : "")
                        + (withSwitch && !lines.Contains(SwitchDesktopIgnore) ? SwitchDesktopIgnore + "\n" : "");
                if (add.Length == 0) return null;
                existing += (existing.Length == 0 || existing.EndsWith("\n", StringComparison.Ordinal) ? "" : "\n") + add;
                return existing;
            }
            return existing;
        }

        private const string SwitchRunIgnore = DecisionSettingsFolder + "/live-*.json";
        private const string SwitchDesktopIgnore = DecisionSettingsFolder + "/desktop-*.json";

        // The History is this machine's record of edits, not part of the team's model: it is written after every edit,
        // so a tracked copy would show as a change after every commit (found by CicdSmoke's clean-after-commit check).
        private const string HistoryIgnore = EditLedger.FileName + "\n" + EditLedger.FileName + ".tmp\n";

        // The data question history holds the filter values a reading matched in the model's data and the questions
        // people asked. It stays on this computer, so it never rides the model's commits.
        private const string QuestionsIgnore = Query.DataQuestionStore.FileName + "\n" + Query.DataQuestionStore.FileName + ".tmp\n"
                                              + Query.DataQuestionStore.LiveFolder + "/\n";

        public static readonly string[] GitAddExcludes =
        {
            ":(exclude,glob)**/engine.lock",
            ":(exclude,glob)**/engine.json",
            ":(exclude,glob)**/experience.jsonl",
            ":(exclude,glob)**/baselines/vitals.jsonl",
            ":(exclude,glob)**/vitals.jsonl",
            ":(exclude,glob)**/.semanticus/history.json",
            ":(exclude,glob)**/.semanticus/history.json.tmp",
            ":(exclude,glob)**/.semanticus/data-questions.json",
            ":(exclude,glob)**/.semanticus/data-questions.json.tmp",
            ":(exclude,glob)**/.semanticus/data-questions/**",
        };

        /// <summary>Copy the sidecar beside a save-as target so primers and workflows travel with the model.</summary>
        public static void CopyForSaveAs(string oldSource, string newSource)
        {
            var oldDir = DirFor(oldSource);
            var newDir = DirFor(newSource);
            if (string.IsNullOrEmpty(oldDir) || string.IsNullOrEmpty(newDir)) return;
            if (string.Equals(Path.GetFullPath(oldDir), Path.GetFullPath(newDir), StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(oldDir)) return;
            CopyTree(oldDir, newDir);
            EnsureRuntimeIgnore(newDir);
        }

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                // Decision switches are one model's each, and the save carries that model's own. Copying the files would
                // hand every other model's offs in the old folder to the models already in the new one.
                var top = Path.GetRelativePath(from, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (string.Equals(top, DecisionSettingsFile, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(top, DecisionSettingsFolder, StringComparison.OrdinalIgnoreCase))
                    continue;
                var name = Path.GetFileName(file);
                if (string.Equals(name, "engine.lock", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "engine.json", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "experience.jsonl", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "vitals.jsonl", StringComparison.OrdinalIgnoreCase))
                    continue;
                var dest = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                if (!File.Exists(dest)) File.Copy(file, dest);
            }
        }
    }
}
