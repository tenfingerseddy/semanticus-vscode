using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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
            var file = Path.Combine(dir, FileName);
            var shape = new FileShape { Version = CurrentVersion, Tables = entries.ToList() };
            File.WriteAllText(file, JsonSerializer.Serialize(shape, JsonOpts));
            return file;
        }
    }
}
