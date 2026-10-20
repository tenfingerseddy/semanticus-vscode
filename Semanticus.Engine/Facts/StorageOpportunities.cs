// Storage opportunities, moved into the engine from the Storage tab's computeOpportunities (F-230).
//
// The webview used to compute these itself from a scan, the unused sweep and the column list, which meant the
// agent door could never see them and the two doors could disagree. This is a faithful port: the same four
// rules, the same thresholds, the same evidence gates and the same copy, so the tab renders exactly what it
// rendered before, now from the engine.
//
// Every rule is deterministic and justified by the scan; each carries an honest effect label. Thresholds:
//   Dictionary-dominated: the dictionary is the LARGEST component AND at least 50% of a materially large
//     (at least 1 MB) column.
//   Hash-index-heavy: the hash index is at least 25% of a materially large column. The finding is never called
//     "string"; a hash index is not evidence the column is text.
//   Unused large column: a safe verdict from the unused sweep that IS in the scan and materially large.
//   Identifier summarized by default: a materially large numeric identifier column still defaulting to Sum
//     (identifier = the key flag, or an identifier suffix on a real token boundary).
// Direct Lake: every size the scan reports is resident only, so the copy says so, and the unused finding's
// reduction claim flips from an upper bound ("up to") to the only thing resident sizes support, a lower bound.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Semanticus.Engine.Lineage;

namespace Semanticus.Engine.Facts
{
    public static class StorageEvidenceLevels
    {
        public const string CurrentEditingModel = "currentEditingModel";
        public const string StaleQueryCopy = "staleQueryCopy";
        public const string None = "none";

        /// <summary>Only sameInstance proves the scanned bytes describe the editing objects. A linked copy proves
        /// provenance, but either side may be stale, so its bytes stay observations of the copy.</summary>
        public static string For(string relationship) => relationship switch
        {
            "sameInstance" => CurrentEditingModel,
            "workingCopyAndPublished" or "workingCopyAndLocalRuntime" => StaleQueryCopy,
            _ => None,
        };
    }

    /// <summary>The column settings the summarize rule joins onto the scan.</summary>
    public sealed class StorageColumnMeta
    {
        public string Ref { get; set; }
        public string Name { get; set; }
        public string Table { get; set; }
        public string DataType { get; set; }
        public string SummarizeBy { get; set; }
        public bool IsKey { get; set; }
    }

    public static class StorageOpportunities
    {
        public const long MB = 1048576;
        public const long Material = 1 * MB;   // a column is "materially large" once it costs at least about 1 MB
        public const double SharePct = 25;      // a column at this share of the model is always named (the biggest cost)
        public const long ShareFloor = 64 * 1024;   // ... unless it is trivially small

        private static readonly HashSet<string> NumericTypes = new HashSet<string>(StringComparer.Ordinal) { "int64", "decimal", "double", "currency" };

        // Identifier-suffix detection with a token boundary: the suffix must start its own word. The camelCase branch
        // is case SENSITIVE (the lowercase to capital hump IS the boundary, so AmountPaid, Valid, Casino and Episode
        // never match); the separator branch is case INSENSITIVE (the separator is the boundary).
        private static readonly Regex IdSuffixCamel = new Regex("(?:^|[a-z0-9])(?:Key|Id|ID|Code|No|Number)$", RegexOptions.CultureInvariant);
        private static readonly Regex IdSuffixSep = new Regex(@"(?:^|[_\s.-])(?:key|id|code|no|number)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static bool IdentifierLike(string name, bool isKey) =>
            isKey || IdSuffixCamel.IsMatch(name ?? "") || IdSuffixSep.IsMatch(name ?? "");

        public static string FmtMB(long bytes)
        {
            var mb = bytes / 1024.0 / 1024.0;
            return mb >= 1
                ? mb.ToString("F1", CultureInfo.InvariantCulture) + " MB"
                : (bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " KB";
        }

        public static string ColRef(string table, string column) => "column:" + table + "/" + column;

        // JavaScript's Math.round rounds halves up; .NET's default rounds them to even.
        private static int JsRound(double v) => (int)Math.Floor(v + 0.5);
        private static int PctOfTable(long bytes, long tableSize) => tableSize > 0 ? JsRound(100.0 * bytes / tableSize) : 0;
        private static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);

        public static StorageOpportunity[] Compute(VpaqReport report, UnusedResult unused,
            IReadOnlyCollection<StorageColumnMeta> columns, string evidenceLevel)
        {
            var output = new List<StorageOpportunity>();
            if (report == null || !string.IsNullOrEmpty(report.Error)) return output.ToArray();
            var tableSize = (report.Tables ?? Array.Empty<VpaqTable>())
                .GroupBy(t => t.Name ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Size, StringComparer.Ordinal);
            long TSize(string t) => tableSize.TryGetValue(t ?? "", out var v) ? v : 0;
            var scanned = new Dictionary<string, VpaqColumn>(StringComparer.Ordinal);
            foreach (var c in report.TopColumns ?? Array.Empty<VpaqColumn>()) scanned[ColRef(c.Table, c.Column)] = c;

            var mode = VpaqStorageMode.Normalize(report.StorageMode);
            var staleQueryCopy = evidenceLevel == StorageEvidenceLevels.StaleQueryCopy;
            string Sz(long b) => mode == VpaqStorageMode.DirectLake ? FmtMB(b) + " resident" : mode == VpaqStorageMode.Unknown ? FmtMB(b) + " observed" : FmtMB(b);
            var share = mode == VpaqStorageMode.DirectLake ? "of resident storage" : mode == VpaqStorageMode.Unknown ? "of observed storage" : "of the model";
            var observationPrefix = staleQueryCopy ? "Linked query-copy observation. " : "";
            var observationEffect = staleQueryCopy ? "Read-only query-copy observation" : null;

            foreach (var c in report.TopColumns ?? Array.Empty<VpaqColumn>())
            {
                if (c.TotalSize < Material) continue;
                long data = c.DataSize, dict = c.DictionarySize, hash = c.HashIndexSize, total = c.TotalSize;
                StorageOpportunity Base() => new StorageOpportunity
                {
                    Ref = ColRef(c.Table, c.Column), Name = c.Table + "[" + c.Column + "]", Table = c.Table, Column = c.Column,
                    Encoding = c.Encoding, Data = data, Dict = dict, Hash = hash, Total = total, PctModel = c.PctOfModel,
                    PctTable = PctOfTable(total, TSize(c.Table)), Bytes = total, Severity = 1, Group = "Worth reviewing",
                };
                var largest = Math.Max(data, Math.Max(dict, hash));
                if (dict == largest && dict >= 0.5 * total)
                {
                    var o = Base();
                    o.Kind = "dict"; o.RuleId = "STORAGE-DICT"; o.RuleName = "Dictionary-dominated column";
                    o.Message = $"{observationPrefix}Dictionary is the largest component: {Sz(dict)} of {Sz(total)} ({JsRound(100.0 * dict / total)}%). Often indicates a high-cardinality or wide text column.";
                    o.Effect = observationEffect ?? "No estimate before refresh and rescan";
                    output.Add(o);
                }
                if (hash >= 0.25 * total)
                {
                    var o = Base();
                    o.Kind = "hash"; o.RuleId = "STORAGE-HASH"; o.RuleName = "Hash-index-heavy column";
                    o.Message = $"{observationPrefix}Hash index is {JsRound(100.0 * hash / total)}% of this column ({Sz(hash)} of {Sz(total)}).";
                    o.Effect = observationEffect ?? "No estimate. Grouping-index controls are not yet available; review with Ask AI.";
                    output.Add(o);
                }
            }

            // The biggest cost is always named (UAT P16): one column at a clear share of the model is its own finding, first in the list, even when it is
            // under the byte floor above (a small model where every column is under 1 MB) and even when the dictionary or hash rule names it too.
            // Those say why it is big; this says how much of the model it is.
            var top = (report.TopColumns ?? Array.Empty<VpaqColumn>()).OrderByDescending(c => c.TotalSize).FirstOrDefault();
            if (top != null && top.PctOfModel >= SharePct && top.TotalSize >= ShareFloor)
            {
                output.Insert(0, new StorageOpportunity
                {
                    Ref = ColRef(top.Table, top.Column), Name = top.Table + "[" + top.Column + "]", Table = top.Table, Column = top.Column,
                    Encoding = top.Encoding, Data = top.DataSize, Dict = top.DictionarySize, Hash = top.HashIndexSize, Total = top.TotalSize, PctModel = top.PctOfModel,
                    PctTable = PctOfTable(top.TotalSize, TSize(top.Table)), Bytes = top.TotalSize, Severity = 1, Group = "Worth reviewing",
                    Kind = "share", RuleId = "STORAGE-SHARE",
                    // The headline IS the finding: the list shows it before anyone opens a row.
                    RuleName = $"{top.Column} is {JsRound(top.PctOfModel)}% {share}",
                    Message = $"{observationPrefix}It takes {Sz(top.TotalSize)}. Ask your assistant to shrink it, or check that the model needs all of it.",
                    Effect = observationEffect ?? "No estimate before refresh and rescan",
                });
            }

            // These rules join query-engine rows to EDITING-session metadata by textual ref. Only sameInstance proves
            // revision and object equivalence, so a linked copy's bytes never become measurements of editing objects.
            if (evidenceLevel == StorageEvidenceLevels.CurrentEditingModel)
            {
                // Summarize-by cleanup: metadata joined to the scan. Storage effect is none; it fixes wrong totals.
                foreach (var cr in columns ?? Array.Empty<StorageColumnMeta>())
                {
                    if (!NumericTypes.Contains((cr.DataType ?? "").ToLowerInvariant()) || cr.SummarizeBy != "Sum" || !IdentifierLike(cr.Name, cr.IsKey)) continue;
                    if (!scanned.TryGetValue(cr.Ref ?? "", out var sc) || sc.TotalSize < Material) continue;
                    output.Add(new StorageOpportunity
                    {
                        Ref = cr.Ref, Name = cr.Table + "[" + cr.Name + "]", Table = cr.Table, Column = cr.Name, Kind = "summarize",
                        RuleId = "STORAGE-SUMMARIZE", RuleName = "Identifier summarized by default", Group = "Behavior cleanup", Severity = 1,
                        Bytes = sc.TotalSize, Total = sc.TotalSize, PctModel = sc.PctOfModel, PctTable = PctOfTable(sc.TotalSize, TSize(cr.Table)),
                        Message = "Numeric identifier defaulting to Sum. Aggregating a key produces meaningless totals for report authors and AI.",
                        Effect = "Storage effect: none",
                    });
                }

                // Unused large column: only a SAFE verdict, present in the related scan and materially large.
                foreach (var it in unused?.Items ?? Array.Empty<UnusedItem>())
                {
                    if (it.Verdict != "safe" || it.Ref == null || !it.Ref.StartsWith("column:", StringComparison.Ordinal)) continue;
                    if (!scanned.TryGetValue(it.Ref, out var sc) || sc.TotalSize < Material) continue;
                    output.Add(new StorageOpportunity
                    {
                        Ref = it.Ref, Name = sc.Table + "[" + sc.Column + "]", Table = sc.Table, Column = sc.Column, Kind = "unused",
                        RuleId = "STORAGE-UNUSED", RuleName = "Unused large column", Group = "Can remove", Severity = 2, Bytes = sc.TotalSize,
                        Total = sc.TotalSize, PctModel = sc.PctOfModel, PctTable = PctOfTable(sc.TotalSize, TSize(sc.Table)),
                        Message = $"Not referenced by any measure, relationship, hierarchy or sort-by (model-only; report usage not checked here). {Sz(sc.TotalSize)}, {Num(sc.PctOfModel)}% {share}.",
                        Effect = mode == VpaqStorageMode.DirectLake
                            ? $"Reduction estimate: at least {FmtMB(sc.TotalSize)} currently resident"
                            : mode == VpaqStorageMode.Unknown
                                ? "Reduction estimate unavailable until storage mode is confirmed"
                                : $"Reduction estimate: up to {FmtMB(sc.TotalSize)} after refresh",
                    });
                }
            }

            if (staleQueryCopy)
            {
                // Keep only the safe editing-model verdict and a same-named query-copy observation as read-only context.
                // No query-copy byte threshold, totals, shares or reduction estimate. The only enabled mutation is the
                // delete_if_unused plan path, whose apply step re-verifies referential safety in the editing model.
                foreach (var it in unused?.Items ?? Array.Empty<UnusedItem>())
                {
                    if (it.Verdict != "safe" || it.Ref == null || !it.Ref.StartsWith("column:", StringComparison.Ordinal) || !scanned.ContainsKey(it.Ref)) continue;
                    output.Add(new StorageOpportunity
                    {
                        Ref = it.Ref, Name = it.Table + "[" + it.Name + "]", Table = it.Table, Column = it.Name, Kind = "unused",
                        RuleId = "STORAGE-UNUSED", RuleName = "Unused column with linked query-copy observation", Group = "Can remove", Severity = 2, Bytes = 0,
                        Message = "No editing-model reference was found. The linked query copy has a same-named column, but its storage observation may be stale and is not a measurement of this editing object.",
                        Effect = "Reduction estimate unavailable for linked copies",
                    });
                }
            }

            return output.ToArray();
        }
    }
}
