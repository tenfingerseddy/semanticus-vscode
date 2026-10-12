using System;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>The six generated probe queries for one relationship. Each is a single <c>EVALUATE</c> returning a
    /// one-column, one-row scalar named the same as the property — a runner executes each and reads the like-named
    /// column back into <see cref="RelationshipProbeResult"/>.</summary>
    public sealed class RelationshipProbeQueries
    {
        public string OrphanRows { get; set; }
        public string BlankForeignKeys { get; set; }
        public string DuplicateKeys { get; set; }
        public string BlankKeys { get; set; }
        public string ManyRowCount { get; set; }
        public string OneRowCount { get; set; }
    }

    /// <summary>
    /// PURE DAX probe generation for relationship integrity — emits the exact queries a live runner would execute
    /// (orphan / blank-FK / duplicate-key / blank-key / row counts) and NOTHING more: no ADOMD, no connection, no
    /// execution. The live adapter (wired later by the coordinator) runs these and feeds the counts back into
    /// <see cref="RelationshipProbeResult"/>.
    ///
    /// Identifier escaping is the load-bearing part and is unit-tested string-for-string: a table with an embedded
    /// apostrophe or a column with an embedded ']' must not break the query or — far worse — silently FORGE a
    /// different reference. Tables are single-quoted with the embedded ' doubled ('Sales''s Data'); column
    /// references bracket the column with the embedded ] doubled ('Sales'[Net ]]USD]]]). This matches the engine's
    /// existing convention (InterviewSeeds / LocalEngine.Explain).
    ///
    /// The anti-join queries deliberately do NOT lean on the model's own relationship (we are testing the DATA, not
    /// trusting the relationship under test): the one-side key set is read with <c>VALUES</c>, which — with no
    /// CALCULATE around it — is unaffected by the FILTER's row context and so is the full, unfiltered key population.
    /// </summary>
    public static class RelationshipProbes
    {
        // 'Table Name' — single-quoted, embedded ' doubled (the DAX table-literal escape).
        public static string QuoteTable(string table)
            => "'" + (table ?? string.Empty).Replace("'", "''") + "'";

        // 'Table'[Column] — the column bracketed, embedded ] doubled (the DAX column-reference escape). A ] inside a
        // column name that isn't doubled would close the reference early and change WHICH column is meant.
        public static string ColumnRef(string table, string column)
            => QuoteTable(table) + "[" + (column ?? string.Empty).Replace("]", "]]") + "]";

        /// <summary>All six probe queries for one relationship, read straight off the DTO's STRUCTURAL sides
        /// (Many = FK side, One = key side). THROWS <see cref="ArgumentException"/> if any endpoint identifier is
        /// blank — generating a probe for an unnamed object is a CALLER bug, not a data condition (the analyzer
        /// already routes a blank-endpoint DTO to NotVerifiable and never asks for its queries). Pure text; whether
        /// each result is trustworthy (e.g. many-to-many) is the analyzer's call.</summary>
        public static RelationshipProbeQueries For(RelationshipCheckInput rel)
        {
            if (rel == null) throw new ArgumentNullException(nameof(rel));
            return new RelationshipProbeQueries
            {
                OrphanRows = OrphanRowsQuery(rel.ManyTable, rel.ManyColumn, rel.OneTable, rel.OneColumn),
                BlankForeignKeys = BlankForeignKeysQuery(rel.ManyTable, rel.ManyColumn),
                DuplicateKeys = DuplicateKeysQuery(rel.OneTable, rel.OneColumn),
                BlankKeys = BlankKeysQuery(rel.OneTable, rel.OneColumn),
                ManyRowCount = RowCountQuery(rel.ManyTable, "ManyRowCount"),
                OneRowCount = RowCountQuery(rel.OneTable, "OneRowCount"),
            };
        }

        /// <summary>The same six measurements as ONE query: a single <c>EVALUATE ROW</c> with six columns, in the
        /// order of <see cref="CombinedColumns"/>. Every column is the exact expression its single query evaluates, so
        /// the numbers are the same; the only change is that a live endpoint is asked once, not six times. When the
        /// combined query fails for any reason the runner asks the six single queries instead, so a probe that
        /// cannot run still blanks only its own field.</summary>
        public static string CombinedQuery(RelationshipCheckInput rel)
        {
            if (rel == null) throw new ArgumentNullException(nameof(rel));
            Require(rel.ManyTable, nameof(rel.ManyTable)); Require(rel.ManyColumn, nameof(rel.ManyColumn));
            Require(rel.OneTable, nameof(rel.OneTable)); Require(rel.OneColumn, nameof(rel.OneColumn));
            return "EVALUATE\nROW(\n"
                + "    \"OrphanRows\",\n    " + OrphanRowsExpr(rel.ManyTable, rel.ManyColumn, rel.OneTable, rel.OneColumn) + ",\n"
                + "    \"BlankForeignKeys\",\n    " + BlankForeignKeysExpr(rel.ManyTable, rel.ManyColumn) + ",\n"
                + "    \"DuplicateKeys\",\n    " + DuplicateKeysExpr(rel.OneTable, rel.OneColumn) + ",\n"
                + "    \"BlankKeys\",\n    " + BlankKeysExpr(rel.OneTable, rel.OneColumn) + ",\n"
                + "    \"ManyRowCount\",\n    " + RowCountExpr(rel.ManyTable) + ",\n"
                + "    \"OneRowCount\",\n    " + RowCountExpr(rel.OneTable) + "\n)";
        }

        /// <summary>The column order of <see cref="CombinedQuery"/>: the six probe names.</summary>
        public static readonly string[] CombinedColumns = { "OrphanRows", "BlankForeignKeys", "DuplicateKeys", "BlankKeys", "ManyRowCount", "OneRowCount" };

        // One probe as its own query: the label and the expression, in the shape every single query has always had.
        private static string Single(string label, string expr) => $"EVALUATE\nROW(\n    \"{label}\",\n    {expr}\n)";

        // MANY-side rows whose NON-blank FK has no match in the full one-side key set. Blank FKs are excluded here
        // (they are counted by BlankForeignKeysQuery) so orphans and blanks never double-count the same rows.
        public static string OrphanRowsQuery(string manyTable, string manyColumn, string oneTable, string oneColumn)
        {
            Require(manyTable, nameof(manyTable));
            Require(manyColumn, nameof(manyColumn));
            Require(oneTable, nameof(oneTable));
            Require(oneColumn, nameof(oneColumn));
            return Single("OrphanRows", OrphanRowsExpr(manyTable, manyColumn, oneTable, oneColumn));
        }

        private static string OrphanRowsExpr(string manyTable, string manyColumn, string oneTable, string oneColumn)
        {
            var many = QuoteTable(manyTable);
            var fk = ColumnRef(manyTable, manyColumn);
            var key = ColumnRef(oneTable, oneColumn);
            return
$@"COUNTROWS(
        FILTER(
            {many},
            NOT ISBLANK({fk}) && NOT( {fk} IN VALUES({key}) )
        )
    )";
        }

        /// <summary>The unmatched many-side key values behind an orphan count: one row per DISTINCT key value the
        /// fact side uses that the lookup side does not have, with how many fact rows carry it, biggest first. The
        /// predicate is character-for-character the one <see cref="OrphanRowsQuery"/> counts, so the list and the
        /// count can never disagree. Two hundred rows of the same key value tell a person nothing; which values are
        /// missing, and how much data hangs off each, is the answer they came for.
        ///
        /// It asks for ONE MORE row than the cap on purpose: that is how "these are all of them" is told apart from
        /// "this is the top of a longer list", which a bare TOPN(cap) cannot say.
        ///
        /// VALUES({key}) sits outside any CALCULATE, so the FILTER's row context does not narrow it and the
        /// comparison is against the whole key population (the same reasoning as the six probes above).</summary>
        public static string UnmatchedRowsQuery(string manyTable, string manyColumn, string oneTable, string oneColumn, int cap)
        {
            Require(manyTable, nameof(manyTable));
            Require(manyColumn, nameof(manyColumn));
            Require(oneTable, nameof(oneTable));
            Require(oneColumn, nameof(oneColumn));
            var many = QuoteTable(manyTable);
            var fk = ColumnRef(manyTable, manyColumn);
            var key = ColumnRef(oneTable, oneColumn);
            var ask = (cap <= 0 ? 200 : cap) + 1;
            return
$@"EVALUATE
TOPN(
    {ask},
    ADDCOLUMNS(
        FILTER(
            VALUES({fk}),
            NOT ISBLANK({fk}) && NOT( {fk} IN VALUES({key}) )
        ),
        ""UnmatchedRows"", CALCULATE( COUNTROWS( {many} ) )
    ),
    [UnmatchedRows], DESC
)";
        }


        // MANY-side rows whose FK is blank — legitimate in many models, reported as information (never a Fail).
        public static string BlankForeignKeysQuery(string manyTable, string manyColumn)
        {
            Require(manyTable, nameof(manyTable));
            Require(manyColumn, nameof(manyColumn));
            return Single("BlankForeignKeys", BlankForeignKeysExpr(manyTable, manyColumn));
        }

        private static string BlankForeignKeysExpr(string manyTable, string manyColumn)
            => $"COUNTROWS( FILTER( {QuoteTable(manyTable)}, ISBLANK({ColumnRef(manyTable, manyColumn)}) ) )";

        // DISTINCT non-blank ONE-side key values that occur on more than one row (context transition via CALCULATE
        // turns each iterated value into a filter). Blanks are excluded — they are reported separately as blank
        // keys, so the two counts never double-count.
        public static string DuplicateKeysQuery(string oneTable, string oneColumn)
        {
            Require(oneTable, nameof(oneTable));
            Require(oneColumn, nameof(oneColumn));
            return Single("DuplicateKeys", DuplicateKeysExpr(oneTable, oneColumn));
        }

        private static string DuplicateKeysExpr(string oneTable, string oneColumn)
        {
            var one = QuoteTable(oneTable);
            var key = ColumnRef(oneTable, oneColumn);
            return
$@"COUNTROWS(
        FILTER(
            VALUES({key}),
            NOT ISBLANK({key}) && CALCULATE( COUNTROWS({one}) ) > 1
        )
    )";
        }

        // ONE-side rows whose key is blank — informational, like blank FKs.
        public static string BlankKeysQuery(string oneTable, string oneColumn)
        {
            Require(oneTable, nameof(oneTable));
            Require(oneColumn, nameof(oneColumn));
            return Single("BlankKeys", BlankKeysExpr(oneTable, oneColumn));
        }

        private static string BlankKeysExpr(string oneTable, string oneColumn)
            => $"COUNTROWS( FILTER( {QuoteTable(oneTable)}, ISBLANK({ColumnRef(oneTable, oneColumn)}) ) )";

        // A plain population count. The label is a fixed literal (never user input), so it needs no escaping.
        public static string RowCountQuery(string table, string label)
        {
            Require(table, nameof(table));
            return $@"EVALUATE ROW(""{label}"", {RowCountExpr(table)})";
        }

        private static string RowCountExpr(string table) => $"COUNTROWS({QuoteTable(table)})";

        // Probe generation for an unnamed object is a CALLER bug: emitting a forged '' / [] reference would error at
        // execution or — worse — silently address the wrong object, so we throw here rather than let garbage through.
        private static string Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("identifier must be a non-blank table/column name (probe generation for an unnamed object is a caller bug)", name);
            return value;
        }
    }

    /// <summary>Runs the probes for one relationship against whatever answers a query. The combined query goes first
    /// (one round trip); if it errors, throws, or comes back the wrong shape, the six single queries run exactly as they
    /// always did, so one probe that cannot run blanks only its own field and the counts that did land are kept.</summary>
    public static class RelationshipProbeRunner
    {
        public static async Task<RelationshipProbeResult> RunAsync(Func<string, Task<ResultSet>> execute, RelationshipCheckInput rel)
        {
            RelationshipProbeQueries q;
            string combined;
            try { q = RelationshipProbes.For(rel); combined = RelationshipProbes.CombinedQuery(rel); }
            catch (ArgumentException) { return null; }   // blank endpoint: the analyzer routes it NotVerifiable
            var fast = await TryCombinedAsync(execute, combined).ConfigureAwait(false);
            if (fast != null) return fast;
            return new RelationshipProbeResult
            {
                OrphanRows = await ScalarAsync(execute, q.OrphanRows).ConfigureAwait(false),
                BlankForeignKeys = await ScalarAsync(execute, q.BlankForeignKeys).ConfigureAwait(false),
                DuplicateKeys = await ScalarAsync(execute, q.DuplicateKeys).ConfigureAwait(false),
                BlankKeys = await ScalarAsync(execute, q.BlankKeys).ConfigureAwait(false),
                ManyRowCount = await ScalarAsync(execute, q.ManyRowCount).ConfigureAwait(false),
                OneRowCount = await ScalarAsync(execute, q.OneRowCount).ConfigureAwait(false),
            };
        }

        private static async Task<RelationshipProbeResult> TryCombinedAsync(Func<string, Task<ResultSet>> execute, string query)
        {
            try
            {
                var rs = await execute(query).ConfigureAwait(false);
                if (rs == null || !string.IsNullOrEmpty(rs.Error) || rs.Rows == null || rs.Rows.Length == 0
                    || rs.Rows[0] == null || rs.Rows[0].Length < RelationshipProbes.CombinedColumns.Length) return null;
                var row = rs.Rows[0];
                return new RelationshipProbeResult
                {
                    OrphanRows = Count(row[0]), BlankForeignKeys = Count(row[1]), DuplicateKeys = Count(row[2]),
                    BlankKeys = Count(row[3]), ManyRowCount = Count(row[4]), OneRowCount = Count(row[5]),
                };
            }
            catch { return null; }
        }

        private static async Task<long?> ScalarAsync(Func<string, Task<ResultSet>> execute, string query)
        {
            try
            {
                var rs = await execute(query).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(rs.Error) || rs.Rows == null || rs.Rows.Length == 0 || rs.Rows[0].Length == 0) return null;
                return Count(rs.Rows[0][0]);
            }
            catch { return null; }
        }

        // COUNTROWS over an empty FILTER is BLANK, not 0: surfacing it as null would leave the check NotVerifiable when
        // the true measurement is "zero rows". BLANK here IS zero. A value that is not a number blanks only itself.
        private static long? Count(object v)
        {
            if (v == null || v is DBNull) return 0;
            try { return Convert.ToInt64(v); } catch { return null; }
        }
    }
}
