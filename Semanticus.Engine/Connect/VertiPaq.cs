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
        /// <summary>Distinct values in the column, from COLUMNSTATISTICS when the caller read it; null when not read
        /// or not reported. Only the count is kept: the statistic's minimum and maximum are raw values and are never
        /// copied out of the result.</summary>
        public long? Cardinality { get; set; }
    }

    public sealed class VpaqTable
    {
        public string Name { get; set; }
        public long? Rows { get; set; }      // null = unknown (Direct Lake: ROWS_COUNT is resident-only, not the table total)
        public long Size { get; set; }
        /// <summary>How many MODEL columns the table has in storage: one per column, never a storage structure.</summary>
        public int Columns { get; set; }
        /// <summary>Bytes of storage structures that belong to the table but are not columns (attribute hierarchies,
        /// relationship indexes, user hierarchies). Counted in <see cref="Size"/>, never listed as columns.</summary>
        public long StructureSize { get; set; }
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
    /// joining segments (data size) to columns (dictionary + string-index size). Cardinality is joined in only
    /// when the caller also read COLUMNSTATISTICS (the model facts snapshot does; the Storage scan does not, to
    /// keep its cost where it was).
    /// </summary>
    public static class VertiPaq
    {
        public static VpaqReport Compute(ResultSet segments, ResultSet columns, ResultSet tables, int topN, string storageMode = VpaqStorageMode.Unknown,
            ResultSet columnStatistics = null, ResultSet modelTables = null, ResultSet modelColumns = null)
        {
            if (!string.IsNullOrEmpty(segments?.Error)) return VpaqReport.FromError(segments.Error);
            if (!string.IsNullOrEmpty(columns?.Error)) return VpaqReport.FromError(columns.Error);
            // A result that was never read is an empty one: nothing to join, never a NullReferenceException. The
            // missing tables result used to pass the error guard above (null?.Error is null) and then throw.
            segments ??= new ResultSet();
            columns ??= new ResultSet();
            tables ??= new ResultSet();

            storageMode = VpaqStorageMode.Normalize(storageMode);
            var isDirectLake = storageMode == VpaqStorageMode.DirectLake;
            var rowCountsAreTotals = storageMode == VpaqStorageMode.Import;

            // The storage DMVs list more than the model's columns: a RowNumber row per table, an attribute hierarchy per
            // column (TABLE_ID "H$..."), relationship indexes ("R$...") and user hierarchies ("U$..."). Counting every
            // row as a column read 153 for a 55-column model, with a column listed twice (UAT N2, Sol, 90716ced). A
            // column is a row that is not one of those structures; when the model's own column list was read it is
            // also a row the model names. Their bytes are real storage, so they stay in the table and model sizes.
            var segIx = Index(segments);
            var colIx = Index(columns);
            var useTableId = segIx.ContainsKey("TABLE_ID") && colIx.ContainsKey("TABLE_ID");
            string Owner(object[] r, Dictionary<string, int> ix) => useTableId ? Cell(r, ix, "TABLE_ID") : Cell(r, ix, "DIMENSION_NAME");

            var dataByKey = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in segments.Rows ?? Array.Empty<object[]>())
            {
                var key = Owner(r, segIx) + "|" + Cell(r, segIx, "COLUMN_ID");
                dataByKey[key] = (dataByKey.TryGetValue(key, out var v) ? v : 0) + ToLong(Get(r, segIx, "USED_SIZE"));
            }

            // Classify every storage row once. When the model's own list was read and names none of them (a spelling
            // the two sources do not share), it is set aside and the rows decide alone: a count of zero is never the answer.
            var merged = new Dictionary<string, VpaqColumn>(StringComparer.OrdinalIgnoreCase);
            var structureByTable = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            void Classify(HashSet<string> known)
            {
                merged.Clear(); structureByTable.Clear();
                foreach (var r in columns.Rows ?? Array.Empty<object[]>())
                {
                    var columnId = Cell(r, colIx, "COLUMN_ID");
                    var name = Cell(r, colIx, "ATTRIBUTE_NAME");
                    // A column always carries its attribute name. A row without one is a relationship index or a hierarchy
                    // (live R$ rows name COLUMN_ID "INDEX"), so it is structure, not a column called "INDEX". The old
                    // fallback to COLUMN_ID is kept only for a server whose DMV has no ATTRIBUTE_NAME at all.
                    var unnamed = colIx.ContainsKey("ATTRIBUTE_NAME") && string.IsNullOrEmpty(name);
                    if (string.IsNullOrEmpty(name)) name = columnId;
                    if (string.IsNullOrEmpty(name) || IsRowNumber(name) || IsRowNumber(columnId)) continue;
                    var table = Cell(r, colIx, "DIMENSION_NAME");
                    var data = dataByKey.TryGetValue(Owner(r, colIx) + "|" + columnId, out var d) ? d : 0;
                    var dict = ToLong(Get(r, colIx, "DICTIONARY_SIZE"));
                    var hidx = ToLong(Get(r, colIx, "STRING_INDEX_SIZE"));
                    var isColumn = !unnamed && !IsStructure(Owner(r, colIx)) && !IsStructure(columnId)
                        && (known == null || known.Contains((table ?? "") + "|" + name));
                    if (!isColumn)
                    {
                        structureByTable[table ?? ""] = (structureByTable.TryGetValue(table ?? "", out var sb) ? sb : 0) + data + dict + hidx;
                        continue;
                    }
                    var mk = (table ?? "") + "|" + name;
                    if (!merged.TryGetValue(mk, out var c))
                    {
                        merged[mk] = new VpaqColumn { Table = table, Column = name, Encoding = EncodingName(Get(r, colIx, "COLUMN_ENCODING")) };
                        c = merged[mk];
                    }
                    // A column that appears twice as a plain row keeps one entry; its bytes add, so nothing is lost.
                    c.DataSize += data; c.DictionarySize += dict; c.HashIndexSize += hidx; c.TotalSize += data + dict + hidx;
                }
            }
            var modelNames = ModelColumnNames(modelTables, modelColumns);
            Classify(modelNames);
            if (modelNames != null && merged.Count == 0) Classify(null);
            var cols = merged.Values.ToList();

            var cardinality = CardinalityByColumn(columnStatistics);
            if (cardinality.Count > 0)
                foreach (var c in cols)
                    if (cardinality.TryGetValue((c.Table ?? "") + "|" + (c.Column ?? ""), out var n)) c.Cardinality = n;

            var modelSize = cols.Sum(c => c.TotalSize) + structureByTable.Values.Sum();
            foreach (var c in cols) c.PctOfModel = modelSize > 0 ? Math.Round(100.0 * c.TotalSize / modelSize, 2) : 0;

            var rowsByTable = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(tables?.Error))
            {
                var ix = Index(tables);
                foreach (var r in tables.Rows ?? Array.Empty<object[]>()) rowsByTable[Cell(r, ix, "DIMENSION_NAME")] = ToLong(Get(r, ix, "ROWS_COUNT"));
            }

            var byTable = cols.GroupBy(c => c.Table ?? "").ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            foreach (var t in structureByTable.Keys) if (!byTable.ContainsKey(t)) byTable[t] = new List<VpaqColumn>();
            var tbls = byTable.Select(g =>
            {
                var structure = structureByTable.TryGetValue(g.Key, out var sbytes) ? sbytes : 0;
                var size = g.Value.Sum(x => x.TotalSize) + structure;
                return new VpaqTable
                {
                    Name = g.Key,
                    Size = size,
                    StructureSize = structure,
                    Columns = g.Value.Count,
                    // Row counts are totals only after Import is proven. Direct Lake and unknown mode both report null
                    // rather than silently presenting a resident-only count as the table total.
                    Rows = rowCountsAreTotals ? (rowsByTable.TryGetValue(g.Key, out var rc) ? rc : 0) : (long?)null,
                    PctOfModel = modelSize > 0 ? Math.Round(100.0 * size / modelSize, 2) : 0,
                };
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

        private static bool IsRowNumber(string s) => s != null && s.StartsWith("RowNumber", StringComparison.OrdinalIgnoreCase);

        /// <summary>A storage structure that is not a column: an attribute hierarchy (H$), a relationship index (R$) or a
        /// user hierarchy (U$), named by the prefix the storage engine gives its TABLE_ID or COLUMN_ID.</summary>
        private static bool IsStructure(string id) =>
            id != null && id.Length > 1 && id[1] == '$' && (id[0] == 'H' || id[0] == 'R' || id[0] == 'U');

        /// <summary>Table|Column of every column the MODEL has (TMSCHEMA_COLUMNS, RowNumber excluded), or null when that
        /// was not read or failed: the storage rows then decide alone. Not a guess at a shape: a result with no usable
        /// rows changes nothing.</summary>
        internal static HashSet<string> ModelColumnNames(ResultSet tables, ResultSet columns)
        {
            if (tables == null || columns == null || !string.IsNullOrEmpty(tables.Error) || !string.IsNullOrEmpty(columns.Error)) return null;
            var tix = Index(tables); var cix = Index(columns);
            if (!tix.ContainsKey("ID") || !tix.ContainsKey("Name") || !cix.ContainsKey("TableID")) return null;
            var tableName = new Dictionary<string, string>();
            foreach (var r in tables.Rows ?? Array.Empty<object[]>()) tableName[Cell(r, tix, "ID") ?? ""] = Cell(r, tix, "Name");
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in columns.Rows ?? Array.Empty<object[]>())
            {
                if (Cell(r, cix, "Type") == "3") continue;   // a RowNumber column is not an authored one
                var name = Cell(r, cix, "ExplicitName");
                if (string.IsNullOrEmpty(name)) name = Cell(r, cix, "InferredName");
                if (string.IsNullOrEmpty(name) || IsRowNumber(name) || !tableName.TryGetValue(Cell(r, cix, "TableID") ?? "", out var t)) continue;
                set.Add(t + "|" + name);
            }
            return set.Count > 0 ? set : null;
        }

        /// <summary>Table|Column to cardinality from a COLUMNSTATISTICS result. Matches the FULL column phrases
        /// ("Table Name", "Column Name", "Cardinality"), never a bare "Column": the function name itself contains
        /// that word and some servers prefix every label with it. A failed or unexpected result joins nothing.</summary>
        internal static Dictionary<string, long> CardinalityByColumn(ResultSet stats)
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (stats == null || !string.IsNullOrEmpty(stats.Error) || stats.Columns == null || stats.Rows == null) return map;
            int Idx(string phrase) => Array.FindIndex(stats.Columns, c => c?.Name != null && c.Name.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0);
            int ti = Idx("Table Name"), ci = Idx("Column Name"), ki = Idx("Cardinality");
            if (ti < 0 || ci < 0 || ki < 0) return map;
            var need = Math.Max(ti, Math.Max(ci, ki));
            foreach (var row in stats.Rows)
            {
                if (row == null || row.Length <= need || row[ki] == null) continue;
                var col = row[ci]?.ToString();
                if (string.IsNullOrEmpty(col) || col.StartsWith("RowNumber", StringComparison.OrdinalIgnoreCase)) continue;
                try { map[(row[ti]?.ToString() ?? "") + "|" + col] = Convert.ToInt64(row[ki]); } catch { /* not a number: leave unknown */ }
            }
            return map;
        }

        private static Dictionary<string, int> Index(ResultSet r)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var cols = r?.Columns ?? Array.Empty<ColumnDef>();
            for (var i = 0; i < cols.Length; i++) if (cols[i]?.Name != null) d[cols[i].Name] = i;
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
