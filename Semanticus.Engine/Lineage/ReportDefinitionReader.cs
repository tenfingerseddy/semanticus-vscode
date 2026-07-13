using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Semanticus.Engine.Lineage
{
    /// <summary>
    /// Parses Power BI report definitions (PBIR — the modern Fabric "Get Report Definition" / PBIP on-disk format)
    /// to the set of MODEL fields (table-qualified column/measure references) each report actually uses — the
    /// Phase-3 "Measure Killer" core that lets the safe-to-remove sweep see report usage a model-only check misses.
    ///
    /// The field-reference SHAPES are the confirmed PBIR/semantic-query expressions (docs/lineage-impact-plan.md
    /// "CONFIRMED: the PBIR field-reference schema"):
    ///   Column   : { "Column":  { "Expression": { "SourceRef": { "Entity": "T" } }, "Property": "C" } }
    ///   Measure  : { "Measure": { "Expression": { "SourceRef": { "Entity": "T" } }, "Property": "M" } }
    ///   extension: SourceRef carries "Schema":"extension"  ⇒ a REPORT-level measure (not a model object)
    ///   alias    : SourceRef carries "Source":"alias" (filters) ⇒ resolve via the enclosing query's "From" clause
    ///   Aggregation wraps a Column/Measure — handled for free by the recursive descent (we hit the inner shape).
    ///
    /// The reader walks the ENTIRE part tree (report.json + every visual.json + page filters + reportExtensions.json),
    /// so it covers ALL reference locations — projections, filters, sort, conditional formatting — in one pass; a
    /// too-narrow parser would over-report "unused". It is pure (string content in → refs out): the SAME parser
    /// serves a local PBIP folder today and the cloud getDefinition parts later. It tolerates $schema version drift
    /// (the shapes are stable across 2.0.0 / 2.4.0 / 2.5.0). Reconciliation to the open model is BY NAME against TOM
    /// (the report layer has no LineageTag) — done in LineageGraph, not here.
    /// </summary>
    internal static class ReportDefinitionReader
    {
        public sealed class FieldRef : IEquatable<FieldRef>
        {
            public string Entity { get; set; }      // owning table name (null when an alias couldn't be resolved)
            public string Property { get; set; }    // column / measure name
            public string Kind { get; set; }        // "column" | "measure"
            public bool IsExtension { get; set; }    // report-level / extension measure (not a model object)
            // PBIR part provenance (from the part PATH, not the field shape): which page/visual this reference came
            // from. DELIBERATELY excluded from Equals/GetHashCode below, so the report-level `Fields` HashSet dedups
            // EXACTLY as before (back-compat with the existing tests + roll-up). Per-visual attribution is recovered
            // from `Occurrences` (the un-deduped list), where the same field used by two visuals appears twice.
            public string Page { get; set; }        // PBIR page id (null = report-level part / unknown)
            public string Visual { get; set; }      // PBIR visual id (null = a page-level filter or report-level part)
            public string VisualType { get; set; }  // e.g. "barChart" — best-effort, read from the visual.json

            public bool Equals(FieldRef o) => o != null && Kind == o.Kind && IsExtension == o.IsExtension
                && string.Equals(Entity, o.Entity, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Property, o.Property, StringComparison.OrdinalIgnoreCase);
            public override bool Equals(object o) => Equals(o as FieldRef);
            public override int GetHashCode() => (Kind + "|" + (Entity ?? "") + "|" + Property + "|" + IsExtension)
                .ToLowerInvariant().GetHashCode();
        }

        public sealed class ParseResult
        {
            public HashSet<FieldRef> Fields { get; } = new HashSet<FieldRef>();   // report-level distinct refs (back-compat)
            public List<FieldRef> Occurrences { get; } = new List<FieldRef>();    // every emit, page/visual-stamped (per-visual attribution)
            public int Unresolved { get; set; }     // refs whose Source alias couldn't be resolved (⇒ usage caveat)
            public bool DefinitionFound { get; set; }  // a PBIR definition (report.json) was located + read at the path
        }

        // Part provenance derived from the PBIR part PATH (page/visual ids) — constant for a whole part, threaded into Emit.
        private readonly struct PartCtx
        {
            public readonly string Page, Visual, VisualType;
            public PartCtx(string page, string visual, string visualType) { Page = page; Visual = visual; VisualType = visualType; }
        }
        private static readonly PartCtx NoCtx = new PartCtx(null, null, null);

        /// <summary>Parse one or more PBIR part CONTENTS (no part paths ⇒ no page/visual attribution). Kept for callers
        /// that don't have the part path; delegates to the path-aware overload with null paths.</summary>
        public static ParseResult Parse(IEnumerable<string> partContents)
            => Parse(partContents.Select(c => ((string)null, c)));

        /// <summary>Parse PBIR parts WITH their part paths (visual.json / report.json / page.json / reportExtensions.json),
        /// so each reference is stamped with the page+visual it came from (derived from the path
        /// <c>pages/{page}/visuals/{visual}/…</c>). A malformed part is skipped — never throws on bad JSON.</summary>
        public static ParseResult Parse(IEnumerable<(string path, string content)> parts)
        {
            var result = new ParseResult();
            foreach (var (path, content) in parts)
            {
                if (string.IsNullOrWhiteSpace(content)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(content);
                    Walk(doc.RootElement, EmptyAliases, result, PartContext(path, doc.RootElement));
                }
                catch (JsonException) { /* tolerate a malformed/oddly-versioned part — skip it */ }
            }
            return result;
        }

        // page/visual ids + best-effort visual type from a part path like definition/pages/{page}/visuals/{visual}/visual.json.
        private static PartCtx PartContext(string path, JsonElement root)
        {
            if (string.IsNullOrEmpty(path)) return NoCtx;
            var norm = path.Replace('\\', '/');
            var mv = Regex.Match(norm, @"pages/([^/]+)/visuals/([^/]+)/", RegexOptions.IgnoreCase);
            if (mv.Success) return new PartCtx(mv.Groups[1].Value, mv.Groups[2].Value, TryVisualType(root));
            var mp = Regex.Match(norm, @"pages/([^/]+)/", RegexOptions.IgnoreCase);
            return mp.Success ? new PartCtx(mp.Groups[1].Value, null, null) : NoCtx;   // a page.json filter (page, no visual)
        }

        // PBIR visual.json carries its type at visual.visualType (some shapes at the root); best-effort, null if absent.
        private static string TryVisualType(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("visual", out var vis) && vis.ValueKind == JsonValueKind.Object
                && vis.TryGetProperty("visualType", out var vt) && vt.ValueKind == JsonValueKind.String) return vt.GetString();
            if (root.TryGetProperty("visualType", out var vt2) && vt2.ValueKind == JsonValueKind.String) return vt2.GetString();
            return null;
        }

        /// <summary>Read a local PBIR definition folder (a PBIP "&lt;Report&gt;.Report/definition" directory, or any
        /// folder containing report.json + pages/**/visual.json + reportExtensions.json) and parse it. Returns an
        /// empty result if the path isn't a PBIR definition folder.</summary>
        public static ParseResult ReadLocalPbir(string path)
        {
            var dir = ResolveDefinitionDir(path);
            if (dir == null) return new ParseResult();
            var parts = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
                .Where(f => { var n = Path.GetFileName(f).ToLowerInvariant(); return n == "visual.json" || n == "report.json" || n == "page.json" || n == "reportextensions.json"; })
                // Pass the path RELATIVE to the definition folder so page/visual ids (pages/{p}/visuals/{v}/…) are recoverable.
                .Select(f => { try { return (path: Path.GetRelativePath(dir, f).Replace('\\', '/'), content: File.ReadAllText(f)); } catch { return (path: (string)null, content: (string)null); } })
                .Where(t => t.content != null);
            var result = Parse(parts);
            result.DefinitionFound = true;
            return result;
        }

        /// <summary>Resolve the PBIR <c>definition</c> folder from a path that may be the definition folder itself, a
        /// <c>*.Report</c> folder, a <c>*.pbip</c> file, or a PBIP project root. Returns null if no PBIR definition
        /// (report.json) is found — e.g. a legacy .pbix (PBIRLegacy) which this reader does not handle.</summary>
        private static string ResolveDefinitionDir(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (File.Exists(path) && path.EndsWith(".pbip", StringComparison.OrdinalIgnoreCase))
                path = Path.GetDirectoryName(path);
            if (!Directory.Exists(path)) return null;

            // Direct hit: this folder (or a 'definition' subfolder) has a report.json.
            if (File.Exists(Path.Combine(path, "report.json"))) return path;
            var def = Path.Combine(path, "definition");
            if (File.Exists(Path.Combine(def, "report.json"))) return def;
            // Search shallowly for a *.Report/definition/report.json (a PBIP project root).
            foreach (var sub in SafeEnumerateDirs(path))
            {
                var d = Path.Combine(sub, "definition");
                if (File.Exists(Path.Combine(d, "report.json"))) return d;
                if (File.Exists(Path.Combine(sub, "report.json"))) return sub;
            }
            return null;
        }

        private static IEnumerable<string> SafeEnumerateDirs(string root)
        {
            try { return Directory.EnumerateDirectories(root); } catch { return Enumerable.Empty<string>(); }
        }

        private static readonly IReadOnlyDictionary<string, string> EmptyAliases = new Dictionary<string, string>(0);

        // Recursive descent. `aliases` maps a From-clause alias (SourceRef.Source) to its Entity (table), so filter
        // references — which use Source rather than Entity — resolve to the right table.
        private static void Walk(JsonElement node, IReadOnlyDictionary<string, string> aliases, ParseResult acc, PartCtx ctx)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    // A query/From context introduces aliases for the subtree below it.
                    var childAliases = MaybeExtendAliases(node, aliases);

                    foreach (var prop in node.EnumerateObject())
                    {
                        if ((prop.NameEquals("Column") || prop.NameEquals("Measure")) && IsFieldRefShape(prop.Value))
                            Emit(prop.Value, prop.Name == "Measure" ? "measure" : "column", childAliases, acc, ctx);
                        Walk(prop.Value, childAliases, acc, ctx);
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var el in node.EnumerateArray()) Walk(el, aliases, acc, ctx);
                    break;
            }
        }

        // A field-ref value is an object with an "Expression" (carrying a SourceRef) and a "Property".
        private static bool IsFieldRefShape(JsonElement v) =>
            v.ValueKind == JsonValueKind.Object
            && v.TryGetProperty("Property", out _)
            && v.TryGetProperty("Expression", out var expr)
            && expr.ValueKind == JsonValueKind.Object;

        private static void Emit(JsonElement refObj, string kind, IReadOnlyDictionary<string, string> aliases, ParseResult acc, PartCtx ctx)
        {
            var property = refObj.GetProperty("Property").GetString();
            if (string.IsNullOrEmpty(property)) return;

            // SourceRef sits at Expression.SourceRef (direct ref) — but a few shapes nest one more level
            // (Expression.Column.Expression.SourceRef). Find the nearest SourceRef under Expression.
            if (!TryFindSourceRef(refObj.GetProperty("Expression"), out var sourceRef)) { acc.Unresolved++; return; }

            bool isExtension = sourceRef.TryGetProperty("Schema", out var schema)
                && string.Equals(schema.GetString(), "extension", StringComparison.OrdinalIgnoreCase);

            string entity = null;
            if (sourceRef.TryGetProperty("Entity", out var e)) entity = e.GetString();
            else if (sourceRef.TryGetProperty("Source", out var s))
            {
                var alias = s.GetString();
                if (alias != null) aliases.TryGetValue(alias, out entity);
            }

            if (!isExtension && string.IsNullOrEmpty(entity)) { acc.Unresolved++; return; }   // can't attribute ⇒ caveat, never silently "used nothing"
            var field = new FieldRef
            {
                Entity = entity, Property = property, Kind = kind, IsExtension = isExtension,
                Page = ctx.Page, Visual = ctx.Visual, VisualType = ctx.VisualType,
            };
            acc.Fields.Add(field);          // report-level distinct set (Page/Visual ignored by Equals — dedup unchanged)
            acc.Occurrences.Add(field);     // per-visual attribution (un-deduped; grouped by page/visual downstream)
        }

        // Find a SourceRef object within an Expression node (handles Expression.SourceRef and one nesting level).
        private static bool TryFindSourceRef(JsonElement expr, out JsonElement sourceRef)
        {
            if (expr.ValueKind == JsonValueKind.Object)
            {
                if (expr.TryGetProperty("SourceRef", out sourceRef) && sourceRef.ValueKind == JsonValueKind.Object) return true;
                foreach (var p in expr.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Object && TryFindSourceRef(p.Value, out sourceRef)) return true;
            }
            sourceRef = default;
            return false;
        }

        // If the object has a "From" array of { Name, Entity } entries, return aliases extended with them.
        private static IReadOnlyDictionary<string, string> MaybeExtendAliases(JsonElement obj, IReadOnlyDictionary<string, string> aliases)
        {
            if (!obj.TryGetProperty("From", out var from) || from.ValueKind != JsonValueKind.Array) return aliases;
            var map = new Dictionary<string, string>(aliases, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in from.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (entry.TryGetProperty("Name", out var name) && entry.TryGetProperty("Entity", out var ent))
                {
                    var n = name.GetString(); var e = ent.GetString();
                    if (!string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(e)) map[n] = e;
                }
            }
            return map;
        }
    }
}
