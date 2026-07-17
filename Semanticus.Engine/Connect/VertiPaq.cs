using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    public static class VpaqStorageMode
    {
        public const string Import = "import";
        public const string DirectLake = "directLake";
        public const string Unknown = "unknown";

        public static string Normalize(string mode) =>
            mode == Import || mode == DirectLake ? mode : Unknown;
    }

    public sealed class VpaqColumn
    {
        public string Table { get; set; }
        public string Column { get; set; }
        public long TotalSize { get; set; }
        public long DataSize { get; set; }
        public long DictionarySize { get; set; }
        public long HashIndexSize { get; set; }
        public string Encoding { get; set; }
        public double PctOfModel { get; set; }
    }

    public sealed class VpaqTable
    {
        public string Name { get; set; }
        public long? Rows { get; set; }      // null = unknown (Direct Lake: ROWS_COUNT is resident-only, not the table total)
        public long Size { get; set; }
        public int Columns { get; set; }
        public double PctOfModel { get; set; }
    }

    public sealed class VpaqReport
    {
        // Canonical identity of the live query target that produced this report. LocalEngine captures it from the
        // same LiveConnection instance used for every DMV read, so a later connection-status refresh cannot retag
        // the report with another target. The endpoint|database vocabulary matches the webview persistence anchor.
        public string QueryIdentity { get; set; }
        public long ModelSize { get; set; }
        public int ColumnCount { get; set; }
        public VpaqTable[] Tables { get; set; } = Array.Empty<VpaqTable>();
        public VpaqColumn[] TopColumns { get; set; } = Array.Empty<VpaqColumn>();
        // Explicit tri-state. Unknown is never treated as Import because the same DMVs can expose only currently
        // resident data for a model whose mode could not be proven.
        public string StorageMode { get; set; } = VpaqStorageMode.Unknown;
        public string Caveat { get; set; }
        public string Error { get; set; }

        public static VpaqReport FromError(string e) => new VpaqReport { Error = e };
    }

    /// <summary>
    /// Computes a VertiPaq storage report (size per column/table) from the storage DMV result sets,
    /// joining segments (data size) to columns (dictionary + string-index size). Cardinality is
    /// omitted here (it needs per-column DISTINCTCOUNT queries) — this is the "what's big" view.
    /// </summary>
    public static class VertiPaq
    {
        public static VpaqReport Compute(ResultSet segments, ResultSet columns, ResultSet tables, int topN, string storageMode = VpaqStorageMode.Unknown)
        {
            if (!string.IsNullOrEmpty(segments?.Error)) return VpaqReport.FromError(segments.Error);
            if (!string.IsNullOrEmpty(columns?.Error)) return VpaqReport.FromError(columns.Error);

            storageMode = VpaqStorageMode.Normalize(storageMode);
            var isDirectLake = storageMode == VpaqStorageMode.DirectLake;
            var rowCountsAreTotals = storageMode == VpaqStorageMode.Import;

            var dataByKey = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            {
                var ix = Index(segments);
                foreach (var r in segments.Rows)
                {
                    var key = Cell(r, ix, "DIMENSION_NAME") + "|" + Cell(r, ix, "COLUMN_ID");
                    dataByKey[key] = (dataByKey.TryGetValue(key, out var v) ? v : 0) + ToLong(Get(r, ix, "USED_SIZE"));
                }
            }

            var cols = new List<VpaqColumn>();
            {
                var ix = Index(columns);
                foreach (var r in columns.Rows)
                {
                    var name = Cell(r, ix, "ATTRIBUTE_NAME");
                    if (string.IsNullOrEmpty(name) || name.StartsWith("RowNumber", StringComparison.OrdinalIgnoreCase)) continue;
                    var table = Cell(r, ix, "DIMENSION_NAME");
                    var key = table + "|" + Cell(r, ix, "COLUMN_ID");
                    var data = dataByKey.TryGetValue(key, out var d) ? d : 0;
                    var dict = ToLong(Get(r, ix, "DICTIONARY_SIZE"));
                    var hidx = ToLong(Get(r, ix, "STRING_INDEX_SIZE"));
                    cols.Add(new VpaqColumn
                    {
                        Table = table,
                        Column = name,
                        DataSize = data,
                        DictionarySize = dict,
                        HashIndexSize = hidx,
                        TotalSize = data + dict + hidx,
                        Encoding = EncodingName(Get(r, ix, "COLUMN_ENCODING")),
                    });
                }
            }

            var modelSize = cols.Sum(c => c.TotalSize);
            foreach (var c in cols) c.PctOfModel = modelSize > 0 ? Math.Round(100.0 * c.TotalSize / modelSize, 2) : 0;

            var rowsByTable = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(tables?.Error))
            {
                var ix = Index(tables);
                foreach (var r in tables.Rows) rowsByTable[Cell(r, ix, "DIMENSION_NAME")] = ToLong(Get(r, ix, "ROWS_COUNT"));
            }

            var tbls = cols.GroupBy(c => c.Table).Select(g => new VpaqTable
            {
                Name = g.Key,
                Size = g.Sum(x => x.TotalSize),
                Columns = g.Count(),
                // Row counts are totals only after Import is proven. Direct Lake and unknown mode both report null
                // rather than silently presenting a resident-only count as the table total.
                Rows = rowCountsAreTotals ? (rowsByTable.TryGetValue(g.Key ?? "", out var rc) ? rc : 0) : (long?)null,
                PctOfModel = modelSize > 0 ? Math.Round(100.0 * g.Sum(x => x.TotalSize) / modelSize, 2) : 0,
            }).OrderByDescending(t => t.Size).ToArray();

            return new VpaqReport
            {
                ModelSize = modelSize,
                ColumnCount = cols.Count,
                Tables = tbls,
                TopColumns = cols.OrderByDescending(c => c.TotalSize).Take(topN <= 0 ? 25 : topN).ToArray(),
                StorageMode = storageMode,
                Caveat = isDirectLake
                    ? "Direct Lake: storage sizes and row counts reflect only the columns currently resident in memory (paged in by recent queries). They are partial, not the full-model totals."
                    : storageMode == VpaqStorageMode.Unknown
                        ? "Storage mode could not be confirmed. Storage sizes and row counts may reflect only data currently in memory, so they are not treated as full model totals."
                        : null,
            };
        }

        private static Dictionary<string, int> Index(ResultSet r)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < r.Columns.Length; i++) d[r.Columns[i].Name] = i;
            return d;
        }
        private static object Get(object[] row, Dictionary<string, int> ix, string col) => ix.TryGetValue(col, out var i) && i < row.Length ? row[i] : null;
        private static string Cell(object[] row, Dictionary<string, int> ix, string col) => Get(row, ix, col)?.ToString();
        private static long ToLong(object o) { try { return o == null ? 0 : Convert.ToInt64(o); } catch { return 0; } }
        private static string EncodingName(object o)
        {
            var s = o?.ToString();
            return s switch { "1" => "Hash", "2" => "Value", _ => s };
        }
    }
}
