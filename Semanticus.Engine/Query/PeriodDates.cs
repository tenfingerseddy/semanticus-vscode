// A period said in calendar dates (review-fix round 7, item 1). A column named Year holds a label, and the statement said only
// "where Year is 2025": a person who asked for a tax year or a school year read that as theirs and nothing on the screen said
// otherwise. Now the statement names every period it filters or splits in dates the calendar gives it: "Year is 2025 (1 January to
// 31 December 2025)". Said to the person only; what Jev is told never holds a date (StatedReading.ForJev).
//
// The dates are the engine's own reading of a label (a year is 1 January to 31 December, the quarter Q4 is October to December).
// A label this cannot read says nothing rather than guess, so a statement never states a span the engine did not work out.
//
// Only a column that is plainly the calendar is dated (the M11 join, 30 Sep, on the verifier's R16). A Fiscal Quarter, a Financial
// Year, a Tax Year or an FY Month starts when the business says it does, which the engine cannot know: dated as the calendar, the
// fiscal Q2 was shown under "1 April to 30 June", a wrong number under words vouching for it. So a column is dated only when every
// word of its name before its period noun is a plain word ("Year", "Calendar Year", "MonthName"), no word of it names a kind of
// year ("Quarter (FY)"), its table's name names none ("Fiscal Calendar", "Financial Year"), and, when the model marks a date table, it is on one. Any other period column says its label and no dates.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Semanticus.Engine.Query
{
    internal static class PeriodDates
    {
        private static readonly string[] Months = { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };
        private static readonly Regex YearIn = new Regex(@"(?<!\d)((?:19|20)\d{2})(?!\d)", RegexOptions.Compiled);

        private static readonly HashSet<string> PeriodNouns = new HashSet<string>(StringComparer.Ordinal) { "year", "quarter", "month" };

        /// <summary>Whether this column is plainly the calendar's, so its labels may be said in calendar dates.</summary>
        internal static bool Calendar(QueryColumn c, QueryCatalog cat)
        {
            if (cat != null && cat.DateTables.Count > 0 && !cat.DateTables.Contains(c.Table)) return false;
            // A plain Quarter or Year on a table named for a kind of year (Fiscal Calendar, Financial Year, Tax Periods, FY) is that
            // year's, whatever the column is called: the table carries the word the column leaves out (M11 join part 4, D3).
            if (QuerySplitShortlist.NameWords(c.Table).Any(PeriodQualifiers.KindInName)) return false;
            var words = QuerySplitShortlist.NameWords(c.Name);
            var noun = words.FindIndex(PeriodNouns.Contains);
            // Words AFTER the noun are held to the same rule as the ones before it (Sol 3.5): "Quarter (Retail)" is a retail quarter, and
            // "Retail" is in no list of kinds, so a name that only says it is not a kind of year still says something the engine cannot read.
            return noun >= 0 && words.Take(noun).All(PeriodQualifiers.PlainInName) && !words.Any(PeriodQualifiers.KindInName)
                && words.Skip(noun + 1).All(w => PeriodNouns.Contains(w) || PlainSuffixes.Contains(w));
        }

        /// <summary>The words a calendar column's own name may carry after its period noun ("Month Name", "Quarter Number", "Year Short"):
        /// they say how the label is written, not which calendar it is on. Any other word is a qualifier nothing here can explain.</summary>
        private static readonly HashSet<string> PlainSuffixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "names", "label", "labels", "text", "short", "long", "full", "abbr", "abbreviation", "number", "num", "no", "nr", "of", "in", "sort",
        };

        private static string Text(object v) => Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? "";

        private static int? YearOf(object v)
        {
            var m = YearIn.Match(Text(v));
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
        }

        /// <summary>The month (1 to 12) a label names, by its name or its number, once any year is taken out.</summary>
        private static int? MonthIn(string text)
        {
            var t = text.ToLowerInvariant();
            for (var i = 0; i < 12; i++)
                if (Regex.IsMatch(t, @"\b" + Months[i].Substring(0, 3))) return i + 1;
            var n = Regex.Match(t, @"(?<!\d)(0?[1-9]|1[0-2])(?!\d)");
            return n.Success ? int.Parse(n.Value, CultureInfo.InvariantCulture) : (int?)null;
        }

        private static int? QuarterIn(string text)
        {
            var m = Regex.Match(text, @"(?<![A-Za-z0-9])[Qq]?\s*([1-4])(?!\d)");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
        }

        /// <summary>The first and last day a label of a period column with a year in it names, or null when it cannot be read.</summary>
        private static (DateTime First, DateTime Last)? Bounds(QueryColumn c, object v)
        {
            var year = YearOf(v);
            if (year == null) return null;
            if (QueryPeriods.IsYear(c)) return (new DateTime(year.Value, 1, 1), new DateTime(year.Value, 12, 31));
            var rest = YearIn.Replace(Text(v), " ");
            if (QueryPeriods.IsYearMonth(c))
            {
                var month = MonthIn(rest);
                if (month == null) return null;
                var first = new DateTime(year.Value, month.Value, 1);
                return (first, first.AddMonths(1).AddDays(-1));
            }
            if (QueryPeriods.IsYearQuarter(c))
            {
                var q = QuarterIn(rest);
                if (q == null) return null;
                var first = new DateTime(year.Value, (q.Value - 1) * 3 + 1, 1);
                return (first, first.AddMonths(3).AddDays(-1));
            }
            return null;
        }

        private static string Range(DateTime a, DateTime b) =>
            a.Year == b.Year && a.Month == b.Month ? a.Day.ToString(CultureInfo.InvariantCulture) + " to " + b.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)
            : a.Year == b.Year ? a.ToString("d MMMM", CultureInfo.InvariantCulture) + " to " + b.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)
            : a.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) + " to " + b.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

        /// <summary>The dates of one label of a column: a month or a quarter with no year ("December", "Q3") has no year to give.</summary>
        private static string Dates(QueryColumn c, object v)
        {
            if (QueryPeriods.IsMonthOfYear(c))
            {
                var month = MonthIn(Text(v));
                if (month == null) return null;
                var name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Months[month.Value - 1]);
                var last = month == 2 ? "28 or 29" : DateTime.DaysInMonth(2001, month.Value).ToString(CultureInfo.InvariantCulture);
                return "1 to " + last + " " + name;
            }
            if (QueryPeriods.IsQuarter(c))
            {
                var q = QuarterIn(Text(v));
                if (q == null) return null;
                var first = new DateTime(2001, (q.Value - 1) * 3 + 1, 1);
                var last = first.AddMonths(3).AddDays(-1);
                return first.ToString("d MMMM", CultureInfo.InvariantCulture) + " to " + last.ToString("d MMMM", CultureInfo.InvariantCulture);
            }
            var b = Bounds(c, v);
            return b == null ? null : Range(b.Value.First, b.Value.Last);
        }

        /// <summary>The dates a set of filters on one period column keep, said in words ("1 January to 31 December 2025"), or null when
        /// the column is not a period the engine can date or a label cannot be read.</summary>
        public static string Of(QueryColumn column, IReadOnlyList<QueryFilter> filters, QueryCatalog cat)
        {
            if (column.Kind == "date" || !Calendar(column, cat) || !(QueryPeriods.IsYear(column) || QueryPeriods.IsYearMonth(column) || QueryPeriods.IsYearQuarter(column)
                || QueryPeriods.IsQuarter(column) || QueryPeriods.IsMonthOfYear(column))) return null;
            var parts = new List<string>();
            DateTime? from = null, upTo = null;
            foreach (var f in filters)
            {
                switch (f.Op)
                {
                    case QueryOps.Is:
                        var said = f.Values.Select(v => Dates(column, v)).ToList();
                        if (said.Count == 0 || said.Any(s => s == null)) return null;
                        parts.Add(string.Join(" and ", said));
                        break;
                    case QueryOps.IsNot:
                        // What is left out is not a span the statement can name in one range.
                        return null;
                    default:
                        var b = Bounds(column, f.Values.FirstOrDefault());
                        if (b == null) return null;
                        if (f.Op == QueryOps.From) from = b.Value.First;
                        else if (f.Op == QueryOps.After) from = b.Value.Last.AddDays(1);
                        else if (f.Op == QueryOps.Before) upTo = b.Value.First.AddDays(-1);
                        else if (f.Op == QueryOps.UpTo) upTo = b.Value.Last;
                        break;
                }
            }
            if (from != null && upTo != null) parts.Add("from " + Range(from.Value, upTo.Value));
            else if (from != null) parts.Add("from " + from.Value.ToString("d MMMM yyyy", CultureInfo.InvariantCulture));
            else if (upTo != null) parts.Add("up to " + upTo.Value.ToString("d MMMM yyyy", CultureInfo.InvariantCulture));
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }

        /// <summary>What one row of a split by this column is, in the calendar's own terms, or null for a column that is not a period
        /// (or already is days), or not plainly the calendar's.</summary>
        public static string Row(QueryColumn column, QueryCatalog cat)
        {
            if (column.Kind == "date" || !Calendar(column, cat)) return null;
            if (QueryPeriods.IsYear(column)) return "a calendar year, 1 January to 31 December";
            if (QueryPeriods.IsYearMonth(column) || QueryPeriods.IsMonthOfYear(column)) return "a calendar month";
            if (QueryPeriods.IsYearQuarter(column) || QueryPeriods.IsQuarter(column)) return "a calendar quarter";
            return null;
        }
    }
}
