// What the two column points send Jev, built once for both (T268, the fair columns replay of 26 September).
//
// THE QUESTION THAT SHIPS IS THE QUESTION THE REPLAY SCORED. Kane switched column.category and column.summarize on
// because of the fair replay's evidence (docs/redesign/jev-columns-replay.md, "Third run"), so each ask carries the
// same facts that replay's call carried (tools/jev-replay/columns/build-calls-fair.mjs, the main arm), under the four
// fields these points declare:
//
//   replay's state                                 this snapshot
//   column.table, column.name                      table_name, column_name
//   other_column_names_in_the_same_table           table_columns (names only, in the table's own order)
//   column.data_type, column.is_a_calculated_column,
//   this_column_is_used_in_a_relationship,
//   measure_count_in_the_same_table, model_tables,
//   and every key of derived_facts                 column_facts, under the replay's own key names
//
// Two differences, both forced and both stated where the equivalence is proved (ColumnFamiliesFixTests, and the node
// test beside build-calls-fair.mjs that pins the same expected payload):
//   the data types are spelled as the model spells them ("Int64"), not in the replay's lower case ("int64");
//   table_rows is rounded to two significant figures (4,096 is sent as 4,100), because Semanticus never sends a
//   table's exact count of rows. The replay sent it exactly.
// The question itself is the replay's word for word (instructions and criteria), one setting per call, because each
// point is asked and answered on its own; the replay asked four settings in one call.
//
// NO DATA IS NOT NO FACTS. With no data connected the structure is still known (keys, relationships, which measures
// name the column, which column sorts by it), so it is sent exactly as the replay's builder would send an unmeasured
// column: measured is empty, table_rows is null and there are no one-to-one partners. The rows then say Jev saw names
// only (LocalEngine.JevFamilies.cs, StampJevColumnNotes).
//
// NEVER A VALUE. Every fact is a name, a type, a count, a share or a yes or no, worked out on this computer. No value,
// minimum, maximum or sample, no byte count, and never any column's own summarize setting or data category.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Semanticus.Decisions;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    internal static class ColumnAskFacts
    {
        /// <summary>Safety caps well above anything the replay saw (at most 70 tables in a model and 83 other columns in
        /// a table), so a very wide model still fits the 64 KB snapshot limit. No replay column reached either.</summary>
        internal const int MaxModelTables = 300;
        internal const int MaxTableColumns = 500;

        private static readonly Regex SystemTable = new Regex("^(LocalDateTable_|DateTableTemplate_)", RegexOptions.CultureInvariant);
        private static readonly Regex BareTable = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
        private static readonly Regex SpaceBeforeBracket = new Regex(@"\s+\[", RegexOptions.CultureInvariant);

        /// <summary>What every ask on one model shares, read once per run.</summary>
        internal sealed class ModelShape
        {
            public Dictionary<Column, string> Roles;
            public List<object> ModelTables;
            public string[] MeasureExpressions;
        }

        internal static ModelShape Read(Model m)
        {
            var tables = new List<object>();
            foreach (var t in m.Tables)
            {
                if (SystemTable.IsMatch(t.Name ?? "")) continue;
                var columns = t.Columns.Count(c => c.Type != ColumnType.RowNumber);
                if (columns == 0) continue;
                if (tables.Count >= MaxModelTables) break;
                tables.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "table", t.Name },
                    { "columns", columns },
                    { "measures", t.Measures.Count },
                });
            }
            return new ModelShape
            {
                Roles = StarterMeasureTemplates.RelationshipRoles(m),
                ModelTables = tables,
                // Lower case, with the space before a bracket closed up, exactly as the replay read them.
                MeasureExpressions = m.AllMeasures.Select(x => SpaceBeforeBracket.Replace((x.Expression ?? "").ToLowerInvariant(), "[")).ToArray(),
            };
        }

        /// <summary>How many measures name this column in their formula, counted the way the replay counted it
        /// (gather-pbix.py count_measure_refs): 'Table'[Column], or Table[Column] when the table name needs no quotes.</summary>
        internal static int UsedByMeasures(string table, string column, IReadOnlyList<string> expressions)
        {
            var col = ("[" + (column ?? "").Replace("]", "]]") + "]").ToLowerInvariant();
            var bare = BareTable.IsMatch(table ?? "") ? (table + col).ToLowerInvariant() : null;
            var quoted = ("'" + (table ?? "").Replace("'", "''") + "'" + col).ToLowerInvariant();
            return expressions.Count(e => e.Contains(quoted, StringComparison.Ordinal) || (bare != null && e.Contains(bare, StringComparison.Ordinal)));
        }

        /// <summary>A row count to two significant figures, so the size of a table goes and its exact count does not.
        /// Under a hundred rows the count already has two figures and is sent as it is.</summary>
        internal static long? RoundRows(long? rows)
        {
            if (rows == null) return null;
            var r = rows.Value;
            if (r < 100) return r;
            var digits = r.ToString(CultureInfo.InvariantCulture).Length;
            long scale = 1;
            for (var i = 0; i < digits - 2; i++) scale *= 10;
            return (r + scale / 2) / scale * scale;
        }

        /// <summary>The replay's measured block (gather-pbix.py derive_measured, less the one-to-one list and the byte
        /// counts it dropped before sending), under the replay's own key names. Empty when the column was not measured,
        /// as the replay's builder sends an unmeasured column.</summary>
        internal static Dictionary<string, object> Measured(Facts.MeasuredFacts m)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            if (m == null) return d;
            static double R(double x) => Math.Round(x, 3, MidpointRounding.AwayFromZero);
            d["distinct"] = m.Distinct;
            d["blankShare"] = R(m.BlankShare);
            d["wholeNumbers"] = m.WholeNumbers;
            d["uniqueOnEveryRow"] = m.UniqueOnEveryRow;
            d["looksLikeYear"] = m.LooksLikeYear;
            d["looksLikeMonthName"] = m.LooksLikeMonthName;
            d["looksLikeMonthNumber"] = m.LooksLikeMonthNumber;
            d["looksLikeDayName"] = m.LooksLikeDayName;
            d["looksLikeQuarter"] = m.LooksLikeQuarter;
            d["looksLikePeriodNumber"] = m.LooksLikePeriodNumber;
            d["placeShare"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "country", R(m.PlaceShare?.Country ?? 0) },
                { "region", R(m.PlaceShare?.Region ?? 0) },
                // No city list is bundled yet, so the engine never knows this share; it goes as unknown, never as 0.
                { "city", m.PlaceShare?.City is double city ? R(city) : (object)null },
                { "postcode", R(m.PlaceShare?.Postcode ?? 0) },
            };
            d["urlShare"] = R(m.UrlShare);
            d["imageUrlShare"] = R(m.ImageUrlShare);
            d["emailShare"] = R(m.EmailShare);
            // The replay's cardinality is the column's distinct count (derive_measured), not the storage engine's.
            d["cardinality"] = m.Distinct;
            d["sampled"] = m.Sampled;
            return d;
        }

        /// <summary>One column's declared snapshot, built through <see cref="DecisionSnapshot"/> so an undeclared field
        /// cannot be sent.</summary>
        internal static IReadOnlyDictionary<string, object> Snapshot(ModelShape shape, Table table, Column c,
                                                                     Facts.TableFacts tableFacts, Facts.MeasuredFacts measured,
                                                                     DecisionPoint point)
        {
            var role = shape.Roles.TryGetValue(c, out var r) ? r : "none";
            var own = table.Columns.Where(x => x.Type != ColumnType.RowNumber).ToList();
            var partners = (measured?.OneToOneWith ?? Array.Empty<string>())
                .Select(refOf => own.FirstOrDefault(x => string.Equals(ObjectRefs.For(x), refOf, StringComparison.OrdinalIgnoreCase)))
                .Where(x => x != null && !ReferenceEquals(x, c))
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "name", x.Name },
                    { "data_type", x.DataType.ToString() },
                })
                .ToList();

            var columnFacts = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                // The replay's "column" block and its three model-shape facts.
                { "data_type", c.DataType.ToString() },
                { "is_a_calculated_column", c is CalculatedColumn || c is CalculatedTableColumn },
                { "this_column_is_used_in_a_relationship", !string.Equals(role, "none", StringComparison.Ordinal) },
                { "measure_count_in_the_same_table", table.Measures.Count },
                { "model_tables", shape.ModelTables },
                // The replay's derived_facts, key for key.
                { "table_rows", RoundRows(tableFacts?.Rows) },
                { "is_key", c.IsKey },
                { "relationship_role", role },
                { "used_by_measures", UsedByMeasures(table.Name, c.Name, shape.MeasureExpressions) },
                { "measured", Measured(measured) },
                { "one_to_one_partners", partners },
                { "used_as_sort_target", own.Any(x => ReferenceEquals(x.SortByColumn, c)) },
            };
            var others = own.Where(x => !ReferenceEquals(x, c)).Take(MaxTableColumns).Select(x => (object)x.Name).ToList();

            return DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("table_name", table.Name),
                new KeyValuePair<string, object>("column_name", c.Name),
                new KeyValuePair<string, object>("column_facts", columnFacts),
                new KeyValuePair<string, object>("table_columns", others),
            });
        }

        // ---- the batched shape (m11/jev-batch, JevTableBatch.cs) ------------------------------------------------------
        //
        // The same facts as <see cref="Snapshot"/>, split by who shares them: what a TABLE's columns share goes once (the model's
        // tables, the table's column names, its row count and measure count) and what is about ONE column is its entry in
        // `columns`. Every key keeps the replay's own name. Two differences from the per-column request, both forced by the
        // split: `table_columns` lists every column of the table, this one included (it used to leave the asked column out),
        // and the model's tables are sent once for the whole chunk rather than inside every column's facts.

        /// <summary>What every column asked about in one table shares.</summary>
        internal sealed class TableShare
        {
            public string TableRef;
            public string TableName;
            public List<object> TableColumns;
            public List<object> ModelTables;
            public Dictionary<string, object> TableFacts;
        }

        internal static TableShare Share(ModelShape shape, Table table, Facts.TableFacts tableFacts) => new TableShare
        {
            TableRef = ObjectRefs.For(table),
            TableName = table.Name,
            ModelTables = shape.ModelTables,
            TableColumns = table.Columns.Where(x => x.Type != ColumnType.RowNumber).Take(MaxTableColumns).Select(x => (object)x.Name).ToList(),
            TableFacts = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "table_rows", RoundRows(tableFacts?.Rows) },
                { "measure_count_in_the_same_table", table.Measures.Count },
            },
        };

        /// <summary>One column's entry in `columns`: the replay's column block and derived facts, key for key, less what its
        /// table shares.</summary>
        internal static Dictionary<string, object> Entry(ModelShape shape, Table table, Column c, Facts.MeasuredFacts measured)
        {
            var role = shape.Roles.TryGetValue(c, out var r) ? r : "none";
            var own = table.Columns.Where(x => x.Type != ColumnType.RowNumber).ToList();
            var partners = (measured?.OneToOneWith ?? Array.Empty<string>())
                .Select(refOf => own.FirstOrDefault(x => string.Equals(ObjectRefs.For(x), refOf, StringComparison.OrdinalIgnoreCase)))
                .Where(x => x != null && !ReferenceEquals(x, c))
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => (object)new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "name", x.Name },
                    { "data_type", x.DataType.ToString() },
                })
                .ToList();
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "name", c.Name },
                { "data_type", c.DataType.ToString() },
                { "is_a_calculated_column", c is CalculatedColumn || c is CalculatedTableColumn },
                { "this_column_is_used_in_a_relationship", !string.Equals(role, "none", StringComparison.Ordinal) },
                { "is_key", c.IsKey },
                { "relationship_role", role },
                { "used_by_measures", UsedByMeasures(table.Name, c.Name, shape.MeasureExpressions) },
                { "measured", Measured(measured) },
                { "one_to_one_partners", partners },
                { "used_as_sort_target", own.Any(x => ReferenceEquals(x.SortByColumn, c)) },
            };
        }

        /// <summary>The declared snapshot of one request: the entries of columns of one or many tables (each entry names its table), the model's
        /// table list once, and what each table shares (its column names and its facts) in maps keyed by the table's name.</summary>
        internal static IReadOnlyDictionary<string, object> BatchSnapshot(DecisionPoint point, IEnumerable<(string TableName, TableShare Share)> shares,
                                                                         IEnumerable<Dictionary<string, object>> entries)
        {
            var list = shares.ToList();
            var tableColumns = new Dictionary<string, object>(StringComparer.Ordinal);
            var tableFacts = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (name, share) in list)
            {
                if (tableColumns.ContainsKey(name)) continue;
                tableColumns[name] = share.TableColumns;
                tableFacts[name] = share.TableFacts;
            }
            var withTable = entries.Zip(list, (e, s) => (object)JevTableBatch.WithTable(e, s.TableName)).ToList();
            return DecisionSnapshot.Build(point, new[]
            {
                new KeyValuePair<string, object>("table_columns", tableColumns),
                new KeyValuePair<string, object>("model_tables", list.Count == 0 ? null : list[0].Share.ModelTables),
                new KeyValuePair<string, object>("table_facts", tableFacts),
                new KeyValuePair<string, object>("columns", withTable),
            });
        }

        /// <summary>The model facts for one table and one column of it, or nulls when nothing was measured.</summary>
        internal static (Facts.TableFacts Table, Facts.MeasuredFacts Measured) FactsOf(Facts.ModelFacts facts, Table table, Column c)
        {
            var columnRef = ObjectRefs.For(c);
            var tableFacts = facts?.Tables?.FirstOrDefault(t => string.Equals(t.Name, table.Name, StringComparison.OrdinalIgnoreCase));
            return (tableFacts, tableFacts?.Columns?.FirstOrDefault(x => string.Equals(x.Ref, columnRef, StringComparison.OrdinalIgnoreCase))?.Measured);
        }
    }
}
