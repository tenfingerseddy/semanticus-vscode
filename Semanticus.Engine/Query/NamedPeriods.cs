// A year or a month the question names must reach the query, or be said (K21, Kane's live Contoso, 9 Oct).
//
// "sales in 2008" ran the all-time total with nothing said. The model's Year column holds labels ("CY 2008"), and the
// year column that holds the plain number is hidden, so no value matched the word 2008 and the year was dropped before the
// query. The value match now reads "CY 2008" as 2008 (ValueMatcher). This is the second line, for what it cannot reach:
//
//   BIND   a year word no filter holds is looked for in a year column's labels, visible first and then hidden (the
//          column of plain numbers a label column sorts by), and filtered to the value that holds it. Never a guess:
//          a column is used only when exactly its own values hold the year.
//   SAY    the invariant (the guard against the whole class): a year, a month, a quarter or a week number the question names,
//          that the final query neither filters by nor is already said to leave out, is said in one plain line, "Could not
//          apply '2008', so this covers every date." Never the all-time total with nothing said.
//   QUARTER (A3, hero run 4) "sales in Q1 2008" on a model with no quarter column ran the full year with nothing said. A
//          calendar quarter is read from what the model does hold: the three months on its column of months, else, when the
//          question names one year, the range of days on its column of days. Only when neither is there is it said.
//
// Deterministic and local. Nothing here asks Jev, and nothing leaves the machine.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Semanticus.Engine.Query
{
    internal static class NamedPeriods
    {
        private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        /// <summary>A year (1900 to 2099) standing alone: not part of a longer number, a decimal, a code ("FY2008") or a rank ("top 2010").</summary>
        private static readonly Regex YearWord = new Regex(@"(?<![\p{L}\p{N}.,/\-])((?:19|20)\d{2})(?![\p{L}\p{N}]|[.,]\d)", Opt);
        private static readonly Regex RankBefore = new Regex(@"\b(top|bottom|first|last|best|worst|highest|lowest|biggest|smallest|largest)\s+$", Opt);

        private static readonly string[] Months = { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };
        /// <summary>"may" and "march" are also verbs: they name a month only after a word that puts a month there, or before a year.</summary>
        private static readonly Regex MonthWord = new Regex(
            @"\b(?<m>january|february|april|june|july|august|september|october|november|december)\b"
            + @"|(?<![\p{L}\p{N}])(?<m>march|may)\b(?=\s+(?:19|20)\d{2}\b)"
            + @"|\b(?:in|during|for|of|since|from|until|till|after|before)\s+(?<m>march|may)\b", Opt);

        /// <summary>"Q1", "the first quarter", "quarter 1": the quarter's number. A fiscal quarter starts on another month, so it is never read as months.</summary>
        private static readonly Regex[] QuarterWords =
        {
            new Regex(@"(?<![\p{L}\p{N}])q(?<n>[1-4])(?![\p{L}\p{N}])", Opt),
            new Regex(@"(?<![\p{L}\p{N}])(?<n>first|1st|second|2nd|third|3rd|fourth|4th)\s+quarter(?![\p{L}])", Opt),
            new Regex(@"(?<![\p{L}\p{N}])quarter\s+(?<n>[1-4])(?![\p{L}\p{N}])", Opt),
        };
        private static readonly Regex FiscalBefore = new Regex(@"\b(fiscal|financial)\s+(year\s+)?$", Opt);
        private static readonly Regex WeekWord = new Regex(@"(?<![\p{L}\p{N}])(?:week|wk)\s*(?<n>\d{1,2})(?![\p{L}\p{N}])", Opt);
        private static readonly string[] Ordinals = { "first", "second", "third", "fourth" };

        /// <summary>A year, a month, a quarter or a week number the question names.</summary>
        internal sealed class Token
        {
            /// <summary>year, month, quarter or week.</summary>
            public string Kind;
            /// <summary>As the question typed it.</summary>
            public string Text;
            /// <summary>The year, the month's number (1 to 12), the quarter's (1 to 4) or the week's.</summary>
            public int Number;
            /// <summary>A quarter the question calls fiscal or financial.</summary>
            public bool Fiscal;
        }

        internal static List<Token> Read(string question)
        {
            var found = new List<Token>();
            if (string.IsNullOrWhiteSpace(question)) return found;
            foreach (Match m in YearWord.Matches(question))
            {
                if (RankBefore.IsMatch(question.Substring(0, m.Index))) continue;
                var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (found.Any(t => t.Kind == "year" && t.Number == year)) continue;
                found.Add(new Token { Kind = "year", Text = m.Value, Number = year });
            }
            foreach (Match m in MonthWord.Matches(question))
            {
                var g = m.Groups["m"];
                var number = Array.IndexOf(Months, g.Value.ToLowerInvariant()) + 1;
                if (number == 0 || found.Any(t => t.Kind == "month" && t.Number == number)) continue;
                found.Add(new Token { Kind = "month", Text = g.Value, Number = number });
            }
            foreach (var rx in QuarterWords)
                foreach (Match m in rx.Matches(question))
                {
                    var n = m.Groups["n"].Value;
                    var number = int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var digit) ? digit
                        : n.Length > 0 && char.IsDigit(n[0]) ? n[0] - '0' : Array.IndexOf(Ordinals, n.ToLowerInvariant()) + 1;
                    if (number < 1 || number > 4 || found.Any(t => t.Kind == "quarter" && t.Number == number)) continue;
                    found.Add(new Token { Kind = "quarter", Text = m.Groups[0].Value.Trim(), Number = number, Fiscal = FiscalBefore.IsMatch(question.Substring(0, m.Index)) });
                }
            foreach (Match m in WeekWord.Matches(question))
            {
                var number = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                if (number < 1 || number > 53 || found.Any(t => t.Kind == "week" && t.Number == number)) continue;
                found.Add(new Token { Kind = "week", Text = Regex.Replace(m.Value, @"\s+", " "), Number = number });
            }
            return found;
        }

        /// <summary>The line said when a year, a month, a quarter or a week number the question names is not in the query.</summary>
        internal static string NotApplied(Token t) => $"Could not apply '{t.Text}', so this covers every date.";

        // ---- is it in the query? ---------------------------------------------------------------------------------

        private static bool HoldsYear(object value, int year)
        {
            if (value is DateTime d) return d.Year == year;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            return Regex.IsMatch(text, @"(?<!\d)" + year.ToString(CultureInfo.InvariantCulture) + @"(?!\d)");
        }

        private static bool HoldsMonth(object value, int month)
        {
            if (value is DateTime d) return d.Month == month;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            var full = Months[month - 1];
            return Regex.IsMatch(text, @"(?<![\p{L}])(" + full + "|" + full.Substring(0, 3) + @")(?![\p{L}])", Opt);
        }

        private static bool HoldsQuarter(object value, QueryColumn column, int quarter)
        {
            if (value is DateTime d) return (d.Month - 1) / 3 + 1 == quarter;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            var n = quarter.ToString(CultureInfo.InvariantCulture);
            if (Regex.IsMatch(text, @"(?<![\p{L}\p{N}])(q|quarter\s*)" + n + @"(?![\p{L}\p{N}])", Opt)) return true;
            return column != null && QueryPeriods.IsQuarter(column) && text.Trim() == n;
        }

        private static bool HoldsWeek(object value, QueryColumn column)
        {
            // Only a column of weeks can hold a week number.
            return column != null && QuerySplitShortlist.NameWords(column.Name).Contains("week") && value != null;
        }

        /// <summary>The query narrows by the period: a filter holds the year, or the month (by name, by a date inside it, or on a column
        /// of months), or the number is part of the name of what is shown (a measure called "2008 Budget").</summary>
        internal static bool InQuery(Token t, QuerySpec spec)
        {
            if (spec.Measures.Any(m => m.Name.IndexOf(t.Text, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            foreach (var f in spec.Filters)
            {
                if (f.Column == null) continue;
                if (t.Kind == "year" && f.Values.Any(v => HoldsYear(v, t.Number))) return true;
                if (t.Kind == "month" && (f.Values.Any(v => HoldsMonth(v, t.Number)) || QueryPeriods.IsMonthOfYear(f.Column) || QueryPeriods.IsYearMonth(f.Column))) return true;
                if (t.Kind == "quarter" && (f.Values.Any(v => HoldsQuarter(v, f.Column, t.Number)) || HoldsMonths(f, t.Number))) return true;
                if (t.Kind == "week" && f.Values.Any(v => HoldsWeek(v, f.Column) && Regex.IsMatch(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "", @"(?<!\d)" + t.Number.ToString(CultureInfo.InvariantCulture) + @"(?!\d)"))) return true;
                // A range of days is the period the question names only when it holds it.
                if (t.Kind == "year" && f.Column.Kind == "date" && f.Values.OfType<DateTime>().Any(v => v.Year == t.Number)) return true;
            }
            return false;
        }

        private static int[] MonthsOf(int quarter) => new[] { quarter * 3 - 2, quarter * 3 - 1, quarter * 3 };

        /// <summary>One filter that holds all three months of the quarter (by name, or by dates inside each).</summary>
        private static bool HoldsMonths(QueryFilter f, int quarter) =>
            (QueryPeriods.IsMonthOfYear(f.Column) || f.Column.Kind == "date") && MonthsOf(quarter).All(m => f.Values.Any(v => HoldsMonth(v, m)));

        // ---- bind ------------------------------------------------------------------------------------------------

        private static bool Reaches(QueryColumn c, QuerySpec spec, QueryCatalog cat) => spec.Measures.All(m => cat.Unreached(c.Table, m).Length == 0);

        /// <summary>The year columns a year word may bind to: visible ones, or with <paramref name="hidden"/> the hidden ones (the
        /// plain numbers a label column sorts by), that the numbers reach. The marked date table and one-word names first.</summary>
        internal static List<QueryColumn> YearColumns(QueryCatalog cat, QuerySpec spec, bool hidden) =>
            cat.Columns.Where(c => c.Hidden == hidden && !c.Key && QueryPeriods.IsYear(c) && Reaches(c, spec, cat))
                .OrderByDescending(c => cat.DateTables.Contains(c.Table))
                .ThenByDescending(c => QuerySplitShortlist.NameWords(c.Name).Count == 1)
                .ThenBy(c => c.Kind == "text" ? 1 : 0)
                .ToList();

        /// <summary>The values of the column that hold exactly the year: the number alone, or words around it with no other number.</summary>
        private static List<object> Holding(QueryColumn c, object[] values, int year)
        {
            var hits = new List<object>();
            foreach (var v in values ?? Array.Empty<object>())
            {
                if (v == null || v is DBNull) continue;
                var text = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                var numbers = Regex.Matches(text, @"\d+").Cast<Match>().Select(m => m.Value).ToList();
                if (numbers.Count == 1 && numbers[0] == year.ToString(CultureInfo.InvariantCulture)) hits.Add(c.Kind == "whole" ? QueryPeriods.Typed(c, v) : v);
            }
            return hits;
        }

        /// <summary>Filter by each year the query does not hold yet, on the first year column whose own values hold it. Returns the
        /// years still not applied. <paramref name="values"/> holds the distinct values read per column (by Id).</summary>
        internal static List<Token> BindYears(JevQueryReader.Reading reading, QueryCatalog cat, IReadOnlyDictionary<string, object[]> values,
            IEnumerable<Token> tokens, bool hidden)
        {
            var spec = reading.Spec;
            var open = new List<Token>();
            var bound = new List<(QueryColumn Column, object Value)>();
            foreach (var t in tokens)
            {
                if (t.Kind != "year" || InQuery(t, spec)) continue;
                var column = YearColumns(cat, spec, hidden).FirstOrDefault(c => values.TryGetValue(c.Id, out var vs) && Holding(c, vs, t.Number).Count > 0);
                if (column == null) { open.Add(t); continue; }
                var held = Holding(column, values[column.Id], t.Number);
                var candidate = new ValueCandidate { Column = column, Words = t.Text, Values = held, Kind = "value", Exact = true };
                reading.Filters.Add((candidate, QueryOps.Is));
                var same = spec.Filters.FirstOrDefault(f => f.Column == column && f.Op == QueryOps.Is);
                if (same != null) { foreach (var v in held) if (!same.Values.Contains(v)) same.Values.Add(v); }
                else spec.Filters.Add(new QueryFilter { Column = column, Op = QueryOps.Is, Values = held.ToList() });
                bound.Add((column, held[0]));
            }
            // Two years of one column, filtered, add up into one total that is neither: show each year on its own row.
            foreach (var g in bound.GroupBy(b => b.Column).Where(g => g.Count() > 1))
                if (!spec.GroupBy.Contains(g.Key) && spec.GroupBy.Count < JevQueryReader.GroupSlots) spec.GroupBy.Insert(0, g.Key);
            return open;
        }

        /// <summary>The year the data does not hold, as one plain line, or null: a year column was read, none of its values holds the
        /// year the question names, and the years it does hold are all before it or all after it (or it sits between them).</summary>
        internal static string NoDataFor(Token t, QueryCatalog cat, QuerySpec spec, IReadOnlyDictionary<string, object[]> values)
        {
            if (t.Kind != "year") return null;
            foreach (var c in YearColumns(cat, spec, false))
            {
                if (!values.TryGetValue(c.Id, out var vs) || vs == null) continue;
                var years = vs.Where(v => v != null && !(v is DBNull))
                    .Select(v => Regex.Matches(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "", @"(?<!\d)(?:19|20)\d{2}(?!\d)").Cast<Match>().Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToList())
                    .Where(l => l.Count == 1).Select(l => l[0]).ToList();
                if (years.Count == 0) continue;
                if (years.Contains(t.Number)) return null;
                var edge = t.Number < years.Min() ? "The data starts in " + years.Min() : t.Number > years.Max() ? "The data ends in " + years.Max() : "The data has no rows for that year";
                return $"There is no data for {t.Number}. {edge}.";
            }
            return null;
        }

        /// <summary>A calendar quarter the question names that no filter holds yet (A3): the three months on the model's column of months,
        /// else, when the question names one year, the range of days on its column of days. Adds one plain line per quarter read to
        /// <paramref name="notes"/>. What it cannot read is left to <see cref="SayWhatWasNotApplied"/>. A fiscal quarter is never read as months.</summary>
        internal static void BindQuarters(JevQueryReader.Reading reading, QueryCatalog cat, IReadOnlyDictionary<string, object[]> values,
            IEnumerable<Token> tokens, List<string> notes)
        {
            var spec = reading.Spec;
            var all = tokens.ToList();
            var years = all.Where(t => t.Kind == "year").ToList();
            var quarters = all.Where(t => t.Kind == "quarter" && !t.Fiscal && !InQuery(t, spec)).ToList();
            if (quarters.Count == 0) return;
            string Name(int m) => CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m);
            var cols = QueryPeriods.Find(cat, spec.Measures);
            var month = cols.MonthOfYear;
            if (month != null && values.TryGetValue(month.Id, out var monthValues) && monthValues != null && Reaches(month, spec, cat) && spec.Filters.All(f => f.Column != month))
            {
                var wanted = quarters.SelectMany(q => MonthsOf(q.Number)).Distinct().ToList();
                var found = monthValues.Where(v => v != null && !(v is DBNull) && wanted.Any(m => HoldsMonth(v, m))).ToList();
                if (wanted.All(m => found.Any(v => HoldsMonth(v, m))))
                {
                    reading.Filters.Add((new ValueCandidate { Column = month, Words = string.Join(" ", quarters.Select(q => q.Text)), Values = found, Kind = "value", Exact = true }, QueryOps.Is));
                    spec.Filters.Add(new QueryFilter { Column = month, Op = QueryOps.Is, Values = found.ToList() });
                    foreach (var q in quarters) notes.Add($"'{q.Text}' is read as {Name(MonthsOf(q.Number)[0])} to {Name(MonthsOf(q.Number)[2])}.");
                    return;
                }
            }
            if (cols.Days != null && quarters.Count == 1 && years.Count == 1 && spec.Filters.All(f => f.Column != cols.Days))
            {
                var q = quarters[0];
                var from = new DateTime(years[0].Number, MonthsOf(q.Number)[0], 1);
                var before = from.AddMonths(3);
                spec.Filters.Add(new QueryFilter { Column = cols.Days, Op = QueryOps.From, Values = new List<object> { from } });
                spec.Filters.Add(new QueryFilter { Column = cols.Days, Op = QueryOps.Before, Values = new List<object> { before } });
                notes.Add($"'{q.Text}' is read as {from.ToString("d MMMM", CultureInfo.InvariantCulture)} to {before.AddDays(-1).ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}.");
            }
        }

        // ---- say -------------------------------------------------------------------------------------------------

        /// <summary>The invariant: every year, month, quarter or week number the question names that the final query does not filter by is said.</summary>
        internal static void SayWhatWasNotApplied(JevQueryReader.Reading reading, string question)
        {
            foreach (var t in Read(question))
                if (!InQuery(t, reading.Spec))
                    JevQueryReader.Assume(reading, "period:" + t.Kind + t.Number.ToString(CultureInfo.InvariantCulture), NotApplied(t), JevQueryReader.RankCaveat);
        }
    }
}
