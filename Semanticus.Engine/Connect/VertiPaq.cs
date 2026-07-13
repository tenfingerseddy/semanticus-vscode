using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
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
        public long ModelSize { get; set; }
        public int ColumnCount { get; set; }
        public VpaqTable[] Tables { get; set; } = Array.Empty<VpaqTable>();
        public VpaqColumn[] TopColumns { get; set; } = Array.Empty<VpaqColumn>();
        public bool IsDirectLake { get; set; } // storage figures below are RESIDENT-ONLY (paged-in columns), not model totals
        public string Caveat { get; set; }     // human-readable note set when IsDirectLake (null otherwise)
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
        public static VpaqReport Compute(ResultSet segments, ResultSet columns, ResultSet tables, int topN, bool isDirectLake = false)
        {
            if (!string.IsNullOrEmpty(segments?.Error)) return VpaqReport.FromError(segments.Error);
            if (!string.IsNullOrEmpty(columns?.Error)) return VpaqReport.FromError(columns.Error);

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
                // Direct Lake: ROWS_COUNT is resident-only, so report "unknown" (null) rather than a misleading
                // partial/zero count. Import: the DMV count (or 0 when the table truly has no rows).
                Rows = isDirectLake ? (long?)null : (rowsByTable.TryGetValue(g.Key ?? "", out var rc) ? rc : 0),
                PctOfModel = modelSize > 0 ? Math.Round(100.0 * g.Sum(x => x.TotalSize) / modelSize, 2) : 0,
            }).OrderByDescending(t => t.Size).ToArray();

            return new VpaqReport
            {
                ModelSize = modelSize,
                ColumnCount = cols.Count,
                Tables = tbls,
                TopColumns = cols.OrderByDescending(c => c.TotalSize).Take(topN <= 0 ? 25 : topN).ToArray(),
                IsDirectLake = isDirectLake,
                Caveat = isDirectLake
                    ? "Direct Lake: storage sizes and row counts reflect only the columns currently resident in memory (paged in by recent queries) — they are partial, not the full-model totals."
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
