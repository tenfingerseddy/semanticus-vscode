// Reads a bounded slice of each table's data and turns it into derived facts.
//
// Two queries per table, both on the query connection, both read only:
//   1. EVALUATE ROW("rows", COUNTROWS('T'))              the row count, cheap on any engine
//   2. EVALUATE SELECTCOLUMNS('T', "c0", 'T'[A], ...)    the values, wrapped in TOPNSKIP(n, 0, ...) when the
//                                                       table has more rows than the budget allows
// The values query is bounded twice: a row limit, and a cell budget that shrinks the row limit for wide tables.
// A table over the limit is measured on the first n rows the engine returns and every column is marked
// `sampled`. The values are classified in ValueFacts and dropped; nothing here returns one.
//
// The same two queries also come in a T-SQL dialect, for the sample read of a Fabric SQL source (SourceSample):
//   1. SELECT COUNT_BIG(*) FROM [schema].[table]
//   2. SELECT TOP (n) [A], [B] FROM [schema].[table]
// The bounds are identical, and the T-SQL dialect always carries TOP so a table that grew since it was counted still
// cannot send more than the budget. Rows past the limit are dropped here whatever the endpoint sent.
//
// Tables stored as DirectQuery or Direct Lake are NOT read. A DirectQuery read runs against the source system,
// and a Direct Lake read pages every column it touches into memory on the capacity. Neither is a cost a
// background snapshot should pay without being asked, so those tables carry no measured facts and the
// producers entry says why.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine.Facts
{
    internal sealed class FactsBudget
    {
        /// <summary>Most rows read from one table.</summary>
        public int RowLimit { get; set; } = 20000;
        /// <summary>Most cells (rows times columns) read from one table; shrinks the row limit for wide tables.</summary>
        public int CellBudget { get; set; } = 1000000;
        /// <summary>Never shrink below this many rows for a wide table.</summary>
        public int MinRows { get; set; } = 1000;
        /// <summary>Pairs checked for one-to-one per table, so a table of many equal-cardinality columns stays bounded.</summary>
        public int PairLimit { get; set; } = 5000;
        public int TimeoutSeconds { get; set; } = 120;

        public int RowsFor(int columnCount) =>
            Math.Max(1, Math.Min(RowLimit, Math.Max(MinRows, CellBudget / Math.Max(1, columnCount))));
    }

    internal enum FactsDialect { Dax, TSql }

    internal sealed class MeasureTable
    {
        public string Name;
        /// <summary>The source schema, for the T-SQL dialect only (null on the DAX path).</summary>
        public string Schema;
        public FactsDialect Dialect = FactsDialect.Dax;
        public string[] Columns = Array.Empty<string>();
        /// <summary>Called once with the (already bounded) rows, columns in <see cref="Columns"/> order, before they are
        /// dropped, by a caller that works a derived fact out of them (the fiscal year start). It must return nothing
        /// that holds a row.</summary>
        public Action<object[][]> Inspect;
    }

    internal sealed class MeasuredTable
    {
        public long Rows;
        public bool Sampled;
        public string Error;
        public ValueFactsResult[] Columns = Array.Empty<ValueFactsResult>();
        /// <summary>For each column, the indexes of the columns it is one to one with.</summary>
        public List<int>[] OneToOne = Array.Empty<List<int>>();
    }

    internal static class FactsMeasurer
    {
        /// <summary>The T-SQL spelling of the two queries. Every identifier is bracketed, with a closing bracket doubled.</summary>
        internal static class TSql
        {
            internal static string Quote(string name) => "[" + (name ?? "").Replace("]", "]]") + "]";
            private static string From(string schema, string table) =>
                (string.IsNullOrEmpty(schema) ? "" : Quote(schema) + ".") + Quote(table);

            internal static string RowCountQuery(string schema, string table) => "SELECT COUNT_BIG(*) FROM " + From(schema, table);

            internal static string ValuesQuery(string schema, string table, IReadOnlyList<string> columns, int? limit) =>
                "SELECT " + (limit == null ? "" : "TOP (" + limit.Value.ToString(CultureInfo.InvariantCulture) + ") ")
                + string.Join(", ", columns.Select(Quote)) + " FROM " + From(schema, table);
        }

        internal static string QuoteTable(string name) => "'" + (name ?? "").Replace("'", "''") + "'";
        internal static string QuoteColumn(string table, string column) => QuoteTable(table) + "[" + (column ?? "").Replace("]", "]]") + "]";

        internal static string RowCountQuery(string table) => "EVALUATE ROW(\"rows\", COUNTROWS(" + QuoteTable(table) + "))";

        internal static string ValuesQuery(string table, IReadOnlyList<string> columns, int? limit)
        {
            var select = "SELECTCOLUMNS(" + QuoteTable(table)
                + string.Concat(columns.Select((c, i) => ", \"c" + i.ToString(CultureInfo.InvariantCulture) + "\", " + QuoteColumn(table, c))) + ")";
            return limit == null
                ? "EVALUATE " + select
                : "EVALUATE TOPNSKIP(" + limit.Value.ToString(CultureInfo.InvariantCulture) + ", 0, " + select + ")";
        }

        /// <summary>One column's classified values as the snapshot's derived facts (the one-to-one partners are the caller's to
        /// name, because they are refs and the caller knows them).</summary>
        internal static MeasuredFacts ToMeasured(ValueFactsResult v, bool sampled) => new MeasuredFacts
        {
            Distinct = v.Distinct,
            BlankShare = v.Rows == 0 ? 0 : Math.Round((double)v.Blanks / v.Rows, 4),
            WholeNumbers = v.WholeNumbers,
            ExactInDouble = v.ExactInDouble,
            FitsFixedDecimal = v.FitsFixedDecimal,
            ContiguousDays = v.AllDates ? v.ContiguousDays : (bool?)null,
            UniqueOnEveryRow = v.Rows > 0 && v.Blanks == 0 && v.Distinct == v.Rows,
            LooksLikeYear = v.LooksLikeYear,
            LooksLikeMonthName = v.LooksLikeMonthName,
            LooksLikeMonthNumber = v.LooksLikeMonthNumber,
            LooksLikeDayName = v.LooksLikeDayName,
            LooksLikeQuarter = v.LooksLikeQuarter,
            LooksLikePeriodNumber = v.LooksLikePeriodNumber,
            PlaceShare = new PlaceShare { Country = v.Country, Region = v.Region, City = null, Postcode = v.Postcode },
            UrlShare = v.Url,
            ImageUrlShare = v.ImageUrl,
            EmailShare = v.Email,
            Sampled = sampled,
        };

        /// <summary>A SQL cell as the value tests understand it: a time with an offset is a point in time, and the types the
        /// tests have no pattern for (a GUID, a time of day, bytes) become a short text, so two different values stay two
        /// values and a binary column is never held whole.</summary>
        private static object Plain(object v) => v switch
        {
            DateTimeOffset o => o.UtcDateTime,
            TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
            Guid g => g.ToString("D"),
            byte[] b => "bytes:" + b.Length.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToBase64String(b, 0, Math.Min(b.Length, 32)),
            _ => v,
        };

        internal static async Task<MeasuredTable> MeasureAsync(Func<string, int, Task<ResultSet>> execute, MeasureTable table,
            FactsBudget budget, CancellationToken ct)
        {
            var result = new MeasuredTable();
            if (table.Columns.Length == 0) { result.Error = "The table has no columns to measure."; return result; }

            var tsql = table.Dialect == FactsDialect.TSql;
            var count = await execute(tsql ? TSql.RowCountQuery(table.Schema, table.Name) : RowCountQuery(table.Name), 1).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(count?.Error)) { result.Error = count.Error; return result; }
            if (count?.Rows == null || count.Rows.Length == 0 || count.Rows[0].Length == 0) { result.Error = "The row count came back empty."; return result; }
            try { result.Rows = count.Rows[0][0] == null ? 0 : Convert.ToInt64(count.Rows[0][0], CultureInfo.InvariantCulture); }
            catch { result.Error = "The row count was not a number."; return result; }
            ct.ThrowIfCancellationRequested();

            var limit = budget.RowsFor(table.Columns.Length);
            result.Sampled = result.Rows > limit;
            var valuesQuery = tsql ? TSql.ValuesQuery(table.Schema, table.Name, table.Columns, limit)
                : ValuesQuery(table.Name, table.Columns, result.Sampled ? limit : (int?)null);
            var values = await execute(valuesQuery,
                (int)Math.Min(int.MaxValue, Math.Max(1, Math.Min(result.Rows, limit)))).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(values?.Error)) { result.Error = values.Error; return result; }
            // A capped result (more rows than asked for) is still a bounded sample, and says so.
            if (values.Truncated) result.Sampled = true;
            ct.ThrowIfCancellationRequested();

            var rows = values.Rows ?? Array.Empty<object[]>();
            // The budget holds whatever the endpoint sent: the first `limit` rows are the sample and the rest are dropped.
            if (rows.Length > limit) { rows = rows.Take(limit).ToArray(); result.Sampled = true; }
            if (tsql) rows = rows.Select(r => r?.Select(Plain).ToArray()).ToArray();
            table.Inspect?.Invoke(rows);
            result.Columns = new ValueFactsResult[table.Columns.Length];
            for (var c = 0; c < table.Columns.Length; c++)
            {
                var column = new object[rows.Length];
                for (var r = 0; r < rows.Length; r++) column[r] = rows[r] != null && c < rows[r].Length ? rows[r][c] : null;
                result.Columns[c] = ValueFacts.Compute(column);
            }

            // One to one only makes sense between columns with the same number of distinct values, and a column with
            // a single value is trivially one to one with any other single-valued column, which says nothing.
            result.OneToOne = Enumerable.Range(0, table.Columns.Length).Select(_ => new List<int>()).ToArray();
            var checkedPairs = 0;
            foreach (var group in Enumerable.Range(0, table.Columns.Length).Where(i => result.Columns[i].Distinct > 1)
                         .GroupBy(i => result.Columns[i].Distinct))
            {
                var members = group.ToArray();
                for (var a = 0; a < members.Length; a++)
                    for (var b = a + 1; b < members.Length; b++)
                    {
                        if (++checkedPairs > budget.PairLimit) goto done;
                        if (!ValueFacts.OneToOne(result.Columns[members[a]], result.Columns[members[b]])) continue;
                        result.OneToOne[members[a]].Add(members[b]);
                        result.OneToOne[members[b]].Add(members[a]);
                    }
            }
            done:
            foreach (var c in result.Columns) c.RowKeys = null;   // drop the per-row keys as soon as they are used
            return result;
        }
    }
}
