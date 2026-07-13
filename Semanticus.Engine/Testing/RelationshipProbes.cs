using System;

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

        // MANY-side rows whose NON-blank FK has no match in the full one-side key set. Blank FKs are excluded here
        // (they are counted by BlankForeignKeysQuery) so orphans and blanks never double-count the same rows.
        public static string OrphanRowsQuery(string manyTable, string manyColumn, string oneTable, string oneColumn)
        {
            Require(manyTable, nameof(manyTable));
            Require(manyColumn, nameof(manyColumn));
            Require(oneTable, nameof(oneTable));
            Require(oneColumn, nameof(oneColumn));
            var many = QuoteTable(manyTable);
            var fk = ColumnRef(manyTable, manyColumn);
            var key = ColumnRef(oneTable, oneColumn);
            return
$@"EVALUATE
ROW(
    ""OrphanRows"",
    COUNTROWS(
        FILTER(
            {many},
            NOT ISBLANK({fk}) && NOT( {fk} IN VALUES({key}) )
        )
    )
)";
        }

        // MANY-side rows whose FK is blank — legitimate in many models, reported as information (never a Fail).
        public static string BlankForeignKeysQuery(string manyTable, string manyColumn)
        {
            Require(manyTable, nameof(manyTable));
            Require(manyColumn, nameof(manyColumn));
            var many = QuoteTable(manyTable);
            var fk = ColumnRef(manyTable, manyColumn);
            return
$@"EVALUATE
ROW(
    ""BlankForeignKeys"",
    COUNTROWS( FILTER( {many}, ISBLANK({fk}) ) )
)";
        }

        // DISTINCT non-blank ONE-side key values that occur on more than one row (context transition via CALCULATE
        // turns each iterated value into a filter). Blanks are excluded — they are reported separately as blank
        // keys, so the two counts never double-count.
        public static string DuplicateKeysQuery(string oneTable, string oneColumn)
        {
            Require(oneTable, nameof(oneTable));
            Require(oneColumn, nameof(oneColumn));
            var one = QuoteTable(oneTable);
            var key = ColumnRef(oneTable, oneColumn);
            return
$@"EVALUATE
ROW(
    ""DuplicateKeys"",
    COUNTROWS(
        FILTER(
            VALUES({key}),
            NOT ISBLANK({key}) && CALCULATE( COUNTROWS({one}) ) > 1
        )
    )
)";
        }

        // ONE-side rows whose key is blank — informational, like blank FKs.
        public static string BlankKeysQuery(string oneTable, string oneColumn)
        {
            Require(oneTable, nameof(oneTable));
            Require(oneColumn, nameof(oneColumn));
            var one = QuoteTable(oneTable);
            var key = ColumnRef(oneTable, oneColumn);
            return
$@"EVALUATE
ROW(
    ""BlankKeys"",
    COUNTROWS( FILTER( {one}, ISBLANK({key}) ) )
)";
        }

        // A plain population count. The label is a fixed literal (never user input), so it needs no escaping.
        public static string RowCountQuery(string table, string label)
        {
            Require(table, nameof(table));
            return $@"EVALUATE ROW(""{label}"", COUNTROWS({QuoteTable(table)}))";
        }

        // Probe generation for an unnamed object is a CALLER bug: emitting a forged '' / [] reference would error at
        // execution or — worse — silently address the wrong object, so we throw here rather than let garbage through.
        private static string Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("identifier must be a non-blank table/column name (probe generation for an unnamed object is a caller bug)", name);
            return value;
        }
    }
}
