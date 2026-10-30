using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Semanticus.Analysis;

namespace Semanticus.Engine
{
    /// <summary>
    /// Moves a measure written the old way (TOTALYTD(x, 'Date'[Date]), SAMEPERIODLASTYEAR('Date'[Date]) ...) onto a calendar
    /// (TOTALYTD(x, 'Calendar year')). Token based: it edits the date argument of a time function and nothing else, so text in a
    /// string, a comment or a [name] is never touched. A call with a year end ("6/30") moves to the fiscal calendar when the
    /// fiscal year starts the month after. Only period-to-date calls are candidates; shifts and rolling windows stay.
    /// A candidate is not permission to move: setup must prove its numbers first.
    /// </summary>
    internal static class CalendarMeasureMove
    {
        internal sealed class Outcome
        {
            public string After;            // the new text; null when the measure stays as it is
            public string Kind;             // the kind the measure moved to (year | fiscal); when calls differ, the first one's
            public string Why;              // plain words when the measure stays
            public bool Rolling;            // the measure holds a rolling window
        }

        // The index of the argument that holds the dates. TOTAL* take the expression first.
        private static readonly Dictionary<string, int> DateArg = new(StringComparer.OrdinalIgnoreCase)
        {
            ["TOTALYTD"] = 1, ["TOTALQTD"] = 1, ["TOTALMTD"] = 1,
            ["DATESYTD"] = 0, ["DATESQTD"] = 0, ["DATESMTD"] = 0,
            ["SAMEPERIODLASTYEAR"] = 0, ["DATEADD"] = 0, ["PARALLELPERIOD"] = 0,
            ["PREVIOUSMONTH"] = 0, ["PREVIOUSQUARTER"] = 0, ["PREVIOUSYEAR"] = 0,
            ["NEXTMONTH"] = 0, ["NEXTQUARTER"] = 0, ["NEXTYEAR"] = 0,
            ["DATESINPERIOD"] = 0, ["DATESBETWEEN"] = 0,
        };

        private static readonly HashSet<string> PeriodToDate = new(StringComparer.OrdinalIgnoreCase)
            { "TOTALYTD", "TOTALQTD", "TOTALMTD", "DATESYTD", "DATESQTD", "DATESMTD" };

        internal const string UnprovedReason = "Its numbers could not be proved the same for each day around 29 February, so it stays as it is.";

        // Where a year end may sit: TOTALYTD(expr, dates, [filter], [yearEnd]) and DATESYTD(dates, [yearEnd]).
        private static readonly HashSet<string> YearEndFns = new(StringComparer.OrdinalIgnoreCase) { "TOTALYTD", "DATESYTD" };

        private static readonly string[] Months =
            { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };

        /// <summary>True when the measure calls a time function on this date column (the old way), or a rolling window.</summary>
        internal static bool UsesClassicOn(string dax, string dateTable, string dateColumn)
        {
            if (string.IsNullOrWhiteSpace(dax)) return false;
            var toks = DaxScan.Tokenize(dax);
            return Calls(toks).Any(c => DateArgIsColumn(toks, c, dateTable, dateColumn));
        }

        /// <param name="fiscalKindName">the fiscal calendar's name when the plan has it; null when it does not</param>
        /// <param name="fiscalStartMonth">1 to 12; only read when fiscalKindName is set</param>
        internal static Outcome Rewrite(string dax, string dateTable, string dateColumn, string yearName, string fiscalName, int fiscalStartMonth)
        {
            var o = new Outcome();
            if (string.IsNullOrWhiteSpace(dax)) return o;
            var toks = DaxScan.Tokenize(dax);
            o.Rolling = DaxScan.CallsFunction(toks, "DATESINPERIOD") || DaxScan.CallsFunction(toks, "DATESBETWEEN");

            var calls = Calls(toks).Where(c => DateArgIsColumn(toks, c, dateTable, dateColumn)).ToList();
            if (calls.Count == 0) return o;

            // One calendar function inside another is not allowed on a calendar, so such a measure is left for a person.
            foreach (var a in calls)
                foreach (var b in calls)
                    if (!ReferenceEquals(a, b) && b.open > a.open && b.open < a.close)
                    {
                        o.Why = "It nests one time function inside another, so it stays as it is.";
                        return o;
                    }

            // Calendar shifts have different date-selection semantics. Do not infer equivalence from a function name
            // or interval spelling (YEAR, four QUARTERs and twelve MONTHs can all cross a leap day).
            if (calls.Any(c => !PeriodToDate.Contains(c.fn)))
            {
                o.Why = UnprovedReason;
                return o;
            }

            var edits = new List<(int from, int to, string text)>();
            foreach (var c in calls)
            {
                var kind = "year";
                var name = yearName;
                (int s, int e)? yearEnd = null;
                if (YearEndFns.Contains(c.fn))
                {
                    var after = c.args.Skip(c.dates + 1).ToList();
                    var lit = after.Count > 0 ? after[after.Count - 1] : ((int, int)?)null;
                    if (lit != null && lit.Value.Item2 - lit.Value.Item1 == 1 && toks[lit.Value.Item1].Kind == DaxScan.Kind.String)
                        yearEnd = lit;
                }
                if (yearEnd != null)
                {
                    var start = StartMonthFromYearEnd(toks[yearEnd.Value.s].Inner);
                    if (start == null)
                    {
                        o.Why = "Its year end is not the last day of a month, or is written in a way Semanticus cannot read, so it stays as it is.";
                        return o;
                    }
                    if (start == 1) { /* a year that ends on 31 December is the calendar year */ }
                    else if (fiscalName == null)
                    {
                        o.Why = "Its year ends in " + MonthName(start.Value - 1 == 0 ? 12 : start.Value - 1) + ". Turn on the fiscal calendar to move it.";
                        return o;
                    }
                    else if (start != fiscalStartMonth)
                    {
                        o.Why = "Its year does not start in the same month as your fiscal year, so it stays as it is.";
                        return o;
                    }
                    else { kind = "fiscal"; name = fiscalName; }
                    // The year end goes: the calendar knows it. Eat the comma and spaces in front of it.
                    var prev = c.args[c.args.IndexOf(yearEnd.Value) - 1];
                    edits.Add((toks[prev.Item2 - 1].End, toks[yearEnd.Value.e - 1].End, ""));
                }
                var d = c.args[c.dates];
                edits.Add((toks[d.Item1].Start, toks[d.Item2 - 1].End, "'" + name.Replace("'", "''") + "'"));
                o.Kind ??= kind;
            }

            var text = dax;
            foreach (var ed in edits.OrderByDescending(x => x.from))
                text = text.Substring(0, ed.from) + ed.text + text.Substring(ed.to);
            o.After = text;
            return o;
        }

        private sealed class Call
        {
            public string fn;
            public int open, close;        // the token index of '(' and ')'
            public int dates;              // the index of the dates argument
            public List<(int, int)> args = new();
        }

        private static IEnumerable<Call> Calls(IReadOnlyList<DaxScan.Tok> toks)
        {
            for (var k = 0; k + 1 < toks.Count; k++)
            {
                if (toks[k].Kind != DaxScan.Kind.Word || toks[k + 1].Kind != DaxScan.Kind.LParen) continue;
                if (!DateArg.TryGetValue(toks[k].Text, out var idx)) continue;
                var close = DaxScan.MatchingRParen(toks, k + 1);
                if (close < 0) continue;
                var args = DaxScan.ArgRanges(toks, k + 1);
                if (args.Count <= idx) continue;
                yield return new Call { fn = toks[k].Text, open = k + 1, close = close, dates = idx, args = args.Select(a => (a.start, a.end)).ToList() };
            }
        }

        // 'Date'[Date] or Date[Date], and nothing else in the argument.
        private static bool DateArgIsColumn(IReadOnlyList<DaxScan.Tok> toks, Call c, string table, string column)
        {
            var (s, e) = c.args[c.dates];
            if (e - s != 2) return false;
            var t = toks[s]; var col = toks[s + 1];
            var tableOk = (t.Kind == DaxScan.Kind.Name && t.Delim == '\'' && Eq(t.Inner, table)) || (t.Kind == DaxScan.Kind.Word && Eq(t.Text, table));
            return tableOk && col.Kind == DaxScan.Kind.Name && col.Delim == '[' && Eq(col.Inner, column);
        }

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>The month the year starts in, from the date its year ends: "6/30" and "30 June" give 7. Null when unreadable.</summary>
        internal static int? StartMonthFromYearEnd(string text)
        {
            var t = (text ?? "").Trim();
            if (t.Length == 0) return null;
            int end;
            var m = Regex.Match(t, @"^(\d{1,2})\s*[/\-.]\s*(\d{1,2})$");
            if (m.Success)
            {
                var a = int.Parse(m.Groups[1].Value); var b = int.Parse(m.Groups[2].Value);
                int day;
                end = a >= 1 && a <= 12 ? a : b;
                day = a >= 1 && a <= 12 ? b : a;
                if (end < 1 || end > 12) return null;
                if (!IsLastDay(end, day)) return null;   // "6/15": the year starts June 16, which a calendar cannot hold
            }
            else
            {
                end = 0;
                for (var i = 0; i < Months.Length && end == 0; i++)
                    if (Regex.IsMatch(t, @"\b" + Months[i].Substring(0, 3), RegexOptions.IgnoreCase)) end = i + 1;
                if (end == 0) return null;
                // "30 June" and "June 30" carry a day; "June" alone means the month's end.
                var d = Regex.Match(t, @"\d{1,2}");
                if (d.Success && !IsLastDay(end, int.Parse(d.Value))) return null;
            }
            return end % 12 + 1;
        }

        private static bool IsLastDay(int month, int day) => day == DateTime.DaysInMonth(2001, month) || (month == 2 && day == 29);

        internal static string MonthName(int month) => char.ToUpperInvariant(Months[month - 1][0]) + Months[month - 1].Substring(1);
    }
}
