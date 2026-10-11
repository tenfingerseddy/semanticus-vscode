// The ONE query shape ask_data runs, and the plain line that reads it back.
//
// Every question, whoever settled its parts, becomes this and nothing else:
//
//   DEFINE
//       VAR __Filter1 = TREATAS ( { "Coast" }, 'Store'[Region] )                          is: the values named
//       VAR __Filter2 = FILTER ( KEEPFILTERS ( VALUES ( 'Date'[Year] ) ), 'Date'[Year] >= 2024 )   the rest
//       VAR __Result =
//           SUMMARIZECOLUMNS (
//               'Product'[Category],
//               __Filter1,
//               __Filter2,
//               "@Net Sales", [Net Sales]
//           )
//   EVALUATE
//       TOPN ( 10, __Result, [@Net Sales], DESC )                                         only with a top N
//   ORDER BY
//       [@Net Sales] DESC
//
// The result columns carry an "@" because a result column named exactly like a measure is read as the measure
// inside TOPN and ORDER BY, and the measure there would ignore the filters above. The Studio shows the plain names.
// Filter values are the model's own values, matched on this computer (ValueMatcher); nothing here is written by Jev.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Semanticus.Engine.Query
{
    /// <summary>The settled parts of one question, ready to build. Values are typed .NET values from the model.</summary>
    internal sealed class QuerySpec
    {
        public List<QueryColumn> GroupBy = new List<QueryColumn>();
        public List<QueryMeasure> Measures = new List<QueryMeasure>();
        public List<QueryFilter> Filters = new List<QueryFilter>();
        public int? Top;
        public QueryMeasure TopBy;
        public bool Lowest;
        /// <summary>A column or a measure to sort by; null takes the default order.</summary>
        public QueryColumn SortColumn;
        public QueryMeasure SortMeasure;
        public bool Descending;
    }

    internal sealed class QueryFilter
    {
        public QueryColumn Column;
        public string Op = QueryOps.Is;
        public List<object> Values = new List<object>();
    }

    internal static class QueryTemplate
    {
        public const int DefaultRowCap = 500;
        public const int MaxRowCap = 10000;

        public static string ColumnRef(string table, string column) => Quote(table) + "[" + column.Replace("]", "]]") + "]";

        public static string MeasureRef(string measure) => "[" + measure.Replace("]", "]]") + "]";

        public static string Quote(string table) => "'" + table.Replace("'", "''") + "'";

        private static string ResultName(QueryMeasure m) => "@" + m.Name;

        private static string ResultRef(QueryMeasure m) => "[" + ResultName(m).Replace("]", "]]") + "]";

        public static string Text(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        /// <summary>The refusal for a sort on a column the question does not split by.</summary>
        public static string SortNotSplit(string shown) =>
            $"Sorting by {shown} needs {shown} among the columns the numbers are split by. Split by it too, or sort by a split column or a number the question shows.";

        /// <summary>A column kind in plain words: "holds text", "holds dates".</summary>
        public static string KindWords(string kind) => kind switch
        {
            "text" => "text",
            "whole" => "whole numbers",
            "decimal" => "decimal numbers",
            "date" => "dates",
            "truefalse" => "true or false values",
            _ => "values of another kind",
        };

        /// <summary>A value as a DAX literal of the column's kind. Throws for a kind the template does not filter
        /// (dates, true or false, other): the caller has already refused those in plain words.</summary>
        public static string Literal(QueryColumn c, object v)
        {
            switch (c.Kind)
            {
                case "text": return Text(Convert.ToString(v, CultureInfo.InvariantCulture));
                case "whole": return Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                case "decimal": return Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                // A day, compared only (after, from, before, up to): a range of days, never a list of them.
                case "date" when v is DateTime d: return string.Format(CultureInfo.InvariantCulture, "DATE ( {0}, {1}, {2} )", d.Year, d.Month, d.Day);
                default: throw new InvalidOperationException($"{c.Name} holds {KindWords(c.Kind)}, which a question cannot filter yet.");
            }
        }

        /// <summary>True for the column kinds a filter may use. A column of days takes a range only (after, from, before,
        /// up to), which the caller checks.</summary>
        public static bool Filterable(QueryColumn c) => c.Kind == "text" || c.Kind == "whole" || c.Kind == "decimal" || c.Kind == "date";

        /// <summary>The columns the model sorts the split columns by, each once and never one the question already splits by: they
        /// ride in the query only to set the order, and the answer drops them (<see cref="OrderColumns"/>).</summary>
        public static List<string> OrderColumns(QuerySpec s)
        {
            var extra = new List<string>();
            foreach (var c in s.GroupBy)
            {
                if (string.IsNullOrEmpty(c.SortByName)) continue;
                var dax = ColumnRef(c.Table, c.SortByName);
                if (s.GroupBy.Any(g => string.Equals(g.Dax, dax, StringComparison.Ordinal)) || extra.Contains(dax)) continue;
                extra.Add(dax);
            }
            return extra;
        }

        /// <summary>How an order by a split column reads: "A to Z" or "Z to A", and for a column the model sorts by another (the months)
        /// "in the model's own order", because that is the order it runs in.</summary>
        internal static string SortWords(QueryColumn c, bool descending) =>
            string.IsNullOrEmpty(c.SortByName) ? (descending ? "Z to A" : "A to Z") : (descending ? "in the model's own order, last first" : "in the model's own order");

        private static string OrderRef(QuerySpec s, QueryColumn c) =>
            string.IsNullOrEmpty(c.SortByName) ? c.Dax : ColumnRef(c.Table, c.SortByName);

        public static string Build(QuerySpec s)
        {
            if (s.Measures.Count == 0) throw new InvalidOperationException("A question needs at least one measure to show.");
            // ORDER BY can only name a column of the result. A column the numbers are not split by is not one, and the
            // server refuses the query ("a single value for column ... cannot be determined").
            if (s.SortColumn != null && !s.GroupBy.Contains(s.SortColumn))
                throw new InvalidOperationException(SortNotSplit(s.SortColumn.Name));
            if (s.SortMeasure != null && !s.Measures.Contains(s.SortMeasure))
                throw new InvalidOperationException($"Sorting by {s.SortMeasure.Name} needs it among the numbers the question shows.");
            var sb = new StringBuilder();
            sb.Append("DEFINE\n");
            var filterVars = new List<string>();
            for (var i = 0; i < s.Filters.Count; i++)
            {
                var f = s.Filters[i];
                var name = "__Filter" + (i + 1).ToString(CultureInfo.InvariantCulture);
                filterVars.Add(name);
                sb.Append("    VAR ").Append(name).Append(" =\n        ").Append(FilterExpression(f)).Append('\n');
            }
            sb.Append("    VAR __Result =\n        SUMMARIZECOLUMNS (\n");
            var args = new List<string>();
            args.AddRange(s.GroupBy.Select(c => c.Dax));
            args.AddRange(OrderColumns(s));
            args.AddRange(filterVars);
            args.AddRange(s.Measures.Select(m => Text(ResultName(m)) + ", " + m.Dax));
            sb.Append(string.Join(",\n", args.Select(a => "            " + a))).Append('\n');
            sb.Append("        )\n");
            sb.Append("EVALUATE\n");
            if (s.Top.HasValue && s.Top.Value > 0)
            {
                var by = s.TopBy ?? s.Measures[0];
                sb.Append("    TOPN ( ").Append(s.Top.Value.ToString(CultureInfo.InvariantCulture)).Append(", __Result, ")
                  .Append(ResultRef(by)).Append(s.Lowest ? ", ASC" : ", DESC").Append(" )\n");
            }
            else sb.Append("    __Result\n");
            var order = OrderBy(s);
            if (order != null) sb.Append("ORDER BY\n    ").Append(order).Append('\n');
            return sb.ToString();
        }

        internal static string FilterExpression(QueryFilter f)
        {
            var col = f.Column.Dax;
            var values = f.Values.Select(v => Literal(f.Column, v)).ToList();
            switch (f.Op)
            {
                case QueryOps.Is:
                    return "TREATAS ( { " + string.Join(", ", values) + " }, " + col + " )";
                case QueryOps.IsNot:
                    return "FILTER ( KEEPFILTERS ( VALUES ( " + col + " ) ), NOT ( " + col + " IN { " + string.Join(", ", values) + " } ) )";
                default:
                    var sign = f.Op switch
                    {
                        QueryOps.After => ">",
                        QueryOps.From => ">=",
                        QueryOps.Before => "<",
                        QueryOps.UpTo => "<=",
                        _ => throw new InvalidOperationException("Unknown filter '" + f.Op + "'."),
                    };
                    if (values.Count != 1) throw new InvalidOperationException("A filter that compares takes exactly one value.");
                    // A day bounds whole days, and a column of days may hold a time of day: "up to" a day keeps all of it
                    // and "after" a day keeps none of it, so both are read against the next day's start (Sol, 29 Sep).
                    if (f.Column.Kind == "date" && f.Values[0] is DateTime day && (f.Op == QueryOps.UpTo || f.Op == QueryOps.After))
                        return "FILTER ( KEEPFILTERS ( VALUES ( " + col + " ) ), " + col + (f.Op == QueryOps.UpTo ? " < " : " >= ") + Literal(f.Column, day.Date.AddDays(1)) + " )";
                    return "FILTER ( KEEPFILTERS ( VALUES ( " + col + " ) ), " + col + " " + sign + " " + values[0] + " )";
            }
        }

        private static string OrderBy(QuerySpec s)
        {
            if (s.SortMeasure != null && s.Measures.Contains(s.SortMeasure)) return ResultRef(s.SortMeasure) + (s.Descending ? " DESC" : " ASC");
            if (s.SortColumn != null && s.GroupBy.Contains(s.SortColumn)) return OrderRef(s, s.SortColumn) + (s.Descending ? " DESC" : " ASC");
            if (s.Top.HasValue && s.Top.Value > 0) return ResultRef(s.TopBy ?? s.Measures[0]) + (s.Lowest ? " ASC" : " DESC");
            if (s.GroupBy.Count > 0) return string.Join(", ", s.GroupBy.Select(c => OrderRef(s, c) + " ASC"));
            return null;
        }

        // ---- the reading ------------------------------------------------------------------------------------

        /// <summary>The question read back in one plain line: "Net Sales by Region, Year = 2025, top 10 by Net Sales".</summary>
        public static string Reading(QuerySpec s, QueryCatalog cat)
        {
            var sb = new StringBuilder();
            sb.Append(Join(s.Measures.Select(m => m.Name).ToList()));
            if (s.GroupBy.Count > 0) sb.Append(" by ").Append(Join(s.GroupBy.Select(cat.Shown).ToList()));
            foreach (var f in s.Filters) sb.Append(", ").Append(FilterWords(f, cat));
            if (s.Top.HasValue && s.Top.Value > 0)
                sb.Append(s.Lowest ? ", bottom " : ", top ").Append(s.Top.Value.ToString("N0", CultureInfo.InvariantCulture))
                  .Append(" by ").Append((s.TopBy ?? s.Measures[0]).Name);
            // The sort is said whenever it was asked for, with or without a top few, and only when it will happen: a
            // sort the query cannot do is refused before this line is ever shown.
            if (s.SortMeasure != null && s.Measures.Contains(s.SortMeasure))
                sb.Append(", ").Append(s.Descending ? "highest " : "lowest ").Append(s.SortMeasure.Name).Append(" first");
            else if (s.SortColumn != null && s.GroupBy.Contains(s.SortColumn))
                sb.Append(", ").Append(cat.Shown(s.SortColumn)).Append(' ').Append(SortWords(s.SortColumn, s.Descending));
            return sb.ToString();
        }

        internal static string FilterWords(QueryFilter f, QueryCatalog cat)
        {
            var name = cat.Shown(f.Column);
            var shown = f.Values.Select(v => Shown(f.Column, v)).ToList();
            return f.Op switch
            {
                QueryOps.Is => name + " = " + string.Join(" or ", shown),
                QueryOps.IsNot => name + " is not " + string.Join(" or ", shown),
                QueryOps.After => name + " after " + shown[0],
                QueryOps.From => name + " from " + shown[0],
                QueryOps.Before => name + " before " + shown[0],
                QueryOps.UpTo => name + " up to " + shown[0],
                _ => name,
            };
        }

        private static string Shown(QueryColumn c, object v) =>
            v is DateTime d ? d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)
            : c.Kind == "decimal" ? Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("0.##########", CultureInfo.InvariantCulture)
            : Convert.ToString(v, CultureInfo.InvariantCulture);

        private static string Join(IReadOnlyList<string> items) =>
            items.Count == 0 ? "" : items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1];

        // ---- reading references from a caller -----------------------------------------------------------------

        private static readonly Regex QuotedCol = new Regex(@"^\s*'((?:[^']|'')+)'\s*\[((?:[^\]]|\]\])+)\]\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex BareCol = new Regex(@"^\s*([^\[\]']+?)\s*\[((?:[^\]]|\]\])+)\]\s*$", RegexOptions.CultureInvariant);

        /// <summary>Reads 'Table'[Column], Table[Column] and column:Table/Column.</summary>
        public static bool TryParseColumn(string reference, out string table, out string column)
        {
            table = column = null;
            if (string.IsNullOrWhiteSpace(reference)) return false;
            var r = reference.Trim();
            if (r.StartsWith("column:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = r.Substring(7);
                var slash = rest.IndexOf('/');
                if (slash <= 0 || slash == rest.Length - 1) return false;
                table = rest.Substring(0, slash);
                column = rest.Substring(slash + 1);
                return true;
            }
            var m = QuotedCol.Match(r);
            if (m.Success) { table = m.Groups[1].Value.Replace("''", "'"); column = m.Groups[2].Value.Replace("]]", "]"); return true; }
            m = BareCol.Match(r);
            if (m.Success) { table = m.Groups[1].Value; column = m.Groups[2].Value.Replace("]]", "]"); return true; }
            return false;
        }

        /// <summary>Reads a measure name, [Name], or measure:Table/Name.</summary>
        public static string ParseMeasure(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            var r = reference.Trim();
            if (r.StartsWith("measure:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = r.Substring(8);
                var slash = rest.IndexOf('/');
                return slash >= 0 ? rest.Substring(slash + 1) : rest;
            }
            if (r.StartsWith("[", StringComparison.Ordinal) && r.EndsWith("]", StringComparison.Ordinal)) return r.Substring(1, r.Length - 2).Replace("]]", "]");
            return r;
        }
    }
}
