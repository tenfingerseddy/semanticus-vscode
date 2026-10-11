// Time words in a data question: "last month", "this year", "the summer", "December" (Kane, 28 Sep).
//
// Jev picks parts from what the engine lists, and the engine lists no time: it does not know today's date. So "how did
// sales do last month" used to drop the words and run the all-time total, a wrong number shown as the answer. Now:
//
//   WITH A CALENDAR (a table marked as the date table, or one holding a column of days and a year column, that reaches
//   the numbers): "this" and "last" month, quarter or year are read against the model's own latest date, the last day
//   its data holds a number on. A period the data is still in is "this" one; "last" is the one before it, unless the
//   data ends on the period's last day, when that whole period is the last one. The note says what was read, plainly.
//   WITHOUT ONE, or for words that name no single period ("recently", "the summer", "year over year"): the numbers are
//   split by the period, so the person reads off the one they meant, and the note says so.
//   A MONTH NAME with no year ("how was December") on data holding more than one year is split by year, not added up;
//   and a ranking of months ("our 3 biggest months ever") ranks each month of each year, not January of every year.
//
// NEVER THE ALL-TIME TOTAL IN SILENCE (Kane's ruling, 28 Sep, round 2): a time word is read, or the answer needs you.
//   WEEKS AND DAYS ("last week", "this week", "yesterday") and any single period the calendar has no label for are read
//   as a range of days on the model's column of days (the calendar's, or a column of days on the rows the numbers
//   count), against the same latest date. A week runs Monday to Sunday; the day the data ends on is its last day.
//   A SPLIT THAT CANNOT NAME EACH PERIOD (a month name beside another split, with no room for its year) is not run:
//   September of every year added up is not last month. Nor is a period no column can name.
//   WORDS THE ENGINE CANNOT APPLY YET ("last financial year", "month over month", "the past 30 days") need you, and
//   the answer names them. So does a period read against the latest date that misses a period the words also name
//   ("last month in 2024" on data running to 2025).
//
// Nothing here is sent to Jev: the latest date and the period's labels are read from the connected model on this
// computer, the way the filter values are. Two fixed read-only queries, built here.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Semanticus.Engine.Query
{
    internal static class QueryPeriods
    {
        private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        /// <summary>What the words say about time.</summary>
        internal sealed class Said
        {
            /// <summary>month, quarter or year.</summary>
            public string Unit;
            /// <summary>The single periods named, as typed, and whether each is this one or the last one.</summary>
            public List<(string Words, bool Last)> Periods = new List<(string, bool)>();
            /// <summary>Words that name a time the engine cannot pin to one period, as typed, when there are no Periods.</summary>
            public string Vague;
            /// <summary>True when the vague words name several months ("the summer"), not one.</summary>
            public bool Several;
            /// <summary>Time words the engine cannot apply yet, as typed. The answer needs you and names them.</summary>
            public string[] Unsupported;
            /// <summary>"last year / quarter / month to date": the same span of the earlier period (its first day to the same
            /// day of it as the last day the data holds), never the whole of it (G3, the ruling of 29 Sep).</summary>
            public bool ToDate;
            /// <summary>Two periods named together, as the question wrote them ("this year vs last year"), when both are in its
            /// words; the connecting word is the question's own, never a word of ours. Null otherwise.</summary>
            public string Together;
            /// <summary>What the words said, as typed, for a note or a line that names them.</summary>
            public string[] Words => Unsupported ?? (Vague != null ? new[] { Vague } : Periods.Select(p => p.Words).ToArray());
        }

        private static Regex This(string unit) => new Regex($@"\b(this|current)\s+{unit}\b|\b{unit}\s+to\s+date\b|\b{unit[0]}td\b", Opt);
        private static Regex Last(string unit) => new Regex($@"\b(last|previous|prior)\s+{unit}\b", Opt);
        private static readonly Regex LastToDate = new Regex(@"\b(last|previous|prior)\s+(year|quarter|month)\s+to\s+date\b", Opt);
        private static readonly Regex VagueYear = new Regex(@"\byear\s+(over|on)\s+year\b|\byoy\b|\b(this|same)\s+time\s+last\s+year\b", Opt);
        /// <summary>How many periods, in figures or in words ("the last 3 months", "the past two weeks").</summary>
        private const string Many = @"(\d+|a\s+few|few|couple(\s+of)?|several|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|fourteen|thirty|sixty|ninety)";
        private static readonly Regex VagueMonths = new Regex(@"\b(last|past|previous)\s+" + Many + @"\s+months\b|\brecent(ly)?\b|\blately\b", Opt);
        private static readonly Regex VagueYears = new Regex(@"\b(last|past|previous)\s+" + Many + @"\s+years\b", Opt);
        private static readonly Regex VagueQuarters = new Regex(@"\b(last|past|previous)\s+" + Many + @"\s+quarters\b", Opt);
        private static readonly Regex Season = new Regex(@"\b(summer|winter|spring|autumn)\b", Opt);
        private static readonly Regex Today = new Regex(@"\btoday\b", Opt);
        private static readonly Regex Yesterday = new Regex(@"\byesterday\b", Opt);

        /// <summary>Time words that name a time the engine cannot apply yet: a financial year (where it starts is the
        /// model's business, not a column), a change from one period to the next, the future, and a count of days or weeks.
        /// "by fiscal quarter" names a column, so only a relative financial period is here.</summary>
        private static readonly Regex Unsupported = new Regex(
            @"\b(this|last|next|previous|prior|current|the\s+past)\s+(financial|fiscal)\s+(year|quarter|month|half)\b"
            + @"|\b(this|last|next|previous|prior|current)\s+fy\b"
            + @"|\b(financial|fiscal)\s+year\s+to\s+date\b|\bfytd\b"
            + @"|\b(day|week|month|quarter)\s+(over|on)\s+(day|week|month|quarter)\b|\bqoq\b"
            + @"|\b(next|coming)\s+(day|week|month|quarter|year)\b|\btomorrow\b"
            + @"|\b(last|past|previous|recent)\s+" + Many + @"\s+(days|weeks)\b|\bpast\s+(week|fortnight)\b|\bfortnight\b"
            + @"|\b(trailing|rolling)\s+" + Many + @"\s+(days|weeks|months)\b|\bttm\b"
            + @"|\b(this|last)\s+weekend\b|\bsame\s+(period|day|week|month|quarter)\s+last\s+year\b"
            // "the past year" is as often the last twelve months as the last calendar year; half a year has no column.
            + @"|\bpast\s+(month|quarter|year)\b|\b(first|second|1st|2nd)\s+half\b|\bh[12]\b", Opt);

        /// <summary>The words with every matched value taken out first: a word inside a value ("Summer Sale") is that
        /// value, not a time.</summary>
        public static Said Read(string words, IEnumerable<string> values)
        {
            words ??= "";
            foreach (var v in (values ?? Array.Empty<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).OrderByDescending(v => v.Length))
                words = Regex.Replace(words, @"(?<![\p{L}\p{N}])" + Regex.Escape(v.Trim()) + @"(?![\p{L}\p{N}])", " ", Opt);
            return Read(words);
        }

        /// <summary>A compare word leading straight into "last" ("compared to last year", "vs the previous month"):
        /// this period against the last one, so both are read.</summary>
        private static Regex AgainstLast(string unit) =>
            new Regex($@"\b(vs\.?|versus|compared\s+(to|with)|against|than)\s+(the\s+)?(last|previous|prior)\s+{unit}\b", Opt);

        public static Said Read(string words)
        {
            // "year-over-year", "month-over-month", "year-to-date": words joined by a hyphen are read as the words.
            words = Regex.Replace(words ?? "", @"(?<=\p{L})[-\u2010\u2011](?=\p{L})", " ");
            var m = Unsupported.Match(words);
            if (m.Success) return new Said { Unsupported = new[] { m.Value } };
            // "last year to date" is one period, the earlier one up to the same day, not "year to date" (this year) beside
            // "last year" (all of it). Another period named beside it cannot be read with it yet.
            var toDate = LastToDate.Match(words);
            if (toDate.Success)
            {
                var rest = words.Remove(toDate.Index, toDate.Length);
                var other = new[] { "month", "quarter", "year", "week" }
                    .SelectMany(u => This(u).Matches(rest).Cast<Match>().Concat(Last(u).Matches(rest).Cast<Match>())).Select(x => x.Value)
                    .Concat(Today.Matches(rest).Cast<Match>().Concat(Yesterday.Matches(rest).Cast<Match>()).Select(x => x.Value)).ToList();
                if (other.Count > 0) return new Said { Unsupported = new[] { toDate.Value }.Concat(other).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
                var td = new Said { Unit = toDate.Groups[2].Value.ToLowerInvariant(), ToDate = true };
                td.Periods.Add((toDate.Value, true));
                return td;
            }
            // Two different units ("last week versus this month") cannot both be read yet: neither is dropped quietly.
            var named = new[] { "month", "quarter", "year", "week" }
                .SelectMany(u => This(u).Matches(words).Cast<Match>().Concat(Last(u).Matches(words).Cast<Match>()).Select(x => (Unit: u, x.Index, x.Value)))
                .Concat(Today.Matches(words).Cast<Match>().Concat(Yesterday.Matches(words).Cast<Match>()).Select(x => (Unit: "day", x.Index, x.Value)))
                .OrderBy(x => x.Index).ToList();
            if (named.Select(x => x.Unit).Distinct().Count() > 1)
                return new Said { Unsupported = named.Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
            m = VagueYear.Match(words);
            if (m.Success) return new Said { Unit = "year", Vague = m.Value };
            foreach (var unit in new[] { "month", "quarter", "year", "week" })
            {
                var said = new Said { Unit = unit };
                var matches = new List<Match>();
                foreach (Match t in This(unit).Matches(words)) { said.Periods.Add((t.Value, false)); matches.Add(t); }
                foreach (Match l in Last(unit).Matches(words)) { said.Periods.Add((l.Value, true)); matches.Add(l); }
                if (said.Periods.Count == 0) continue;
                said.Periods = said.Periods.GroupBy(p => p.Last).Select(g => g.First()).OrderBy(p => p.Last).ToList();
                if (said.Periods.Count >= 2 && matches.Count >= 2)
                {
                    var from = matches.Min(x => x.Index);
                    said.Together = words.Substring(from, matches.Max(x => x.Index + x.Length) - from);
                }
                if (said.Periods.Count == 1 && said.Periods[0].Last && AgainstLast(unit).IsMatch(words)) said.Periods.Insert(0, ("this " + unit, false));
                return said;
            }
            var day = new Said { Unit = "day" };
            foreach (Match t in Today.Matches(words)) day.Periods.Add((t.Value, false));
            foreach (Match y in Yesterday.Matches(words)) day.Periods.Add((y.Value, true));
            if (day.Periods.Count > 0) { day.Periods = day.Periods.GroupBy(p => p.Last).Select(g => g.First()).OrderBy(p => p.Last).ToList(); return day; }
            m = VagueMonths.Match(words);
            if (m.Success) return new Said { Unit = "month", Vague = m.Value, Several = true };
            m = VagueQuarters.Match(words);
            if (m.Success) return new Said { Unit = "quarter", Vague = m.Value, Several = true };
            m = VagueYears.Match(words);
            if (m.Success) return new Said { Unit = "year", Vague = m.Value, Several = true };
            m = Season.Match(words);
            if (m.Success) return new Said { Unit = "month", Vague = m.Value, Several = true };
            return null;
        }

        // ---- the period columns ------------------------------------------------------------------------------

        /// <summary>The columns that name periods, and the calendar's column of days when the model has a calendar.</summary>
        internal sealed class Columns
        {
            public QueryColumn Day, Year, YearMonth, MonthOfYear, Quarter, YearQuarter;
            /// <summary>The column of days a range of days is read on: the calendar's, or with no calendar, a column of
            /// days on the rows the first number counts (Sales[OrderDate]). Null when the model has neither.</summary>
            public QueryColumn Days;
            /// <summary>The calendar's columns that name a period, the ones a range read against the latest date is
            /// checked against when the words also name one.</summary>
            public IEnumerable<QueryColumn> Named => new[] { Year, YearMonth, MonthOfYear, Quarter, YearQuarter }.Where(c => c != null);
        }

        private static List<string> W(QueryColumn c) => QuerySplitShortlist.NameWords(c.Name);
        private static bool Has(QueryColumn c, string w) => W(c).Contains(w);
        private static bool Labels(QueryColumn c) => !c.Key && (c.Kind == "text" || c.Kind == "whole");
        internal static bool IsYear(QueryColumn c) => Labels(c) && Has(c, "year") && !Has(c, "month") && !Has(c, "quarter") && !Has(c, "week") && !Has(c, "day") && !Has(c, "fiscal");
        internal static bool IsYearMonth(QueryColumn c) => Labels(c) && Has(c, "year") && Has(c, "month") && !Has(c, "fiscal");
        /// <summary>A month of the year with no year in it: MonthName. Split or filtered by it, every year's January is one.</summary>
        internal static bool IsMonthOfYear(QueryColumn c) => c.Kind == "text" && !c.Key && Has(c, "month") && !Has(c, "year") && !Has(c, "number") && !Has(c, "start") && !Has(c, "end");
        internal static bool IsQuarter(QueryColumn c) => Labels(c) && Has(c, "quarter") && !Has(c, "year");
        internal static bool IsYearQuarter(QueryColumn c) => Labels(c) && Has(c, "quarter") && Has(c, "year") && !Has(c, "fiscal");

        /// <summary>The period columns that reach every measure. The calendar, when there is one, is where they are all
        /// read from, so one row of it labels a day with its month and its year.</summary>
        public static Columns Find(QueryCatalog cat, IReadOnlyCollection<QueryMeasure> measures)
        {
            bool Reaches(string table) => measures.All(m => cat.Unreached(table, m).Length == 0);
            QueryColumn DayOf(string table) =>
                cat.Columns.Where(c => c.Table == table && c.Kind == "date").OrderByDescending(c => string.Equals(c.Name, "Date", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            var calendar = cat.Columns.Select(c => c.Table).Distinct(StringComparer.Ordinal)
                .Where(t => Reaches(t) && DayOf(t) != null && (cat.DateTables.Contains(t) || cat.Visible.Any(c => c.Table == t && IsYear(c))))
                .OrderByDescending(t => cat.DateTables.Contains(t)).FirstOrDefault();
            var pool = cat.Visible.Where(c => calendar != null ? c.Table == calendar : Reaches(c.Table)).ToList();
            QueryColumn Best(Func<QueryColumn, bool> f) => pool.Where(f).OrderByDescending(c => W(c).Count == 1).FirstOrDefault();
            // No calendar: a column of days on the rows the numbers count, never another table's (a store's OpenDate
            // reaches the sales too, and is not when they were made).
            var rows = measures.Count == 0 ? Array.Empty<string>() : cat.Roots(measures.First());
            var onRows = calendar != null ? null : cat.Visible.Where(c => c.Kind == "date" && !c.Key && rows.Contains(c.Table) && Reaches(c.Table))
                .OrderByDescending(c => string.Equals(c.Name, "Date", StringComparison.OrdinalIgnoreCase)).ThenByDescending(c => Has(c, "order")).FirstOrDefault();
            return new Columns
            {
                Day = calendar == null ? null : DayOf(calendar),
                Days = calendar == null ? onRows : DayOf(calendar),
                Year = Best(IsYear), YearMonth = Best(IsYearMonth), MonthOfYear = Best(IsMonthOfYear),
                Quarter = pool.Where(IsQuarter).OrderBy(c => Has(c, "fiscal")).FirstOrDefault(), YearQuarter = Best(IsYearQuarter),
            };
        }

        /// <summary>The columns one period of this unit is named by: YearMonth, or Year and MonthName.</summary>
        private static List<QueryColumn> Naming(Columns cols, string unit, int room)
        {
            var list = new List<QueryColumn>();
            switch (unit)
            {
                case "month":
                    if (cols.YearMonth != null) list.Add(cols.YearMonth);
                    else if (cols.MonthOfYear != null) { if (cols.Year != null && room >= 2) list.Add(cols.Year); list.Add(cols.MonthOfYear); }
                    break;
                case "quarter":
                    if (cols.YearQuarter != null) list.Add(cols.YearQuarter);
                    else if (cols.Quarter != null) { if (cols.Year != null && room >= 2) list.Add(cols.Year); list.Add(cols.Quarter); }
                    break;
                case "year":
                    if (cols.Year != null) list.Add(cols.Year);
                    break;
                // A week or a day has no label column: it is read as a range of days, or not at all.
            }
            return list;
        }

        private static string Unit(string unit, bool plural) => plural ? unit + "s" : unit;
        private static string Quoted(IEnumerable<string> words) => string.Join(" and ", words.Select(w => "'" + w + "'"));

        // ---- no calendar, or no single period: split, and say so --------------------------------------------

        /// <summary>Split by the period so the person reads off the one they meant, and say so. A split that cannot name
        /// each period on its own (a month name with no year beside it, on data holding more than one year) adds the same
        /// month of every year together, so it is not run: the answer needs you, and says why. So does a period no column
        /// of this model can name. <paramref name="years"/> is how many years the data holds, when read.</summary>
        public static void Split(JevQueryReader.Reading reading, Said said, Columns cols, QueryCatalog cat, int? years)
        {
            var spec = reading.Spec;
            var words = said.Words;
            var filtered = new HashSet<QueryColumn>(spec.Filters.Select(f => f.Column).Concat(reading.Filters.Select(f => f.Candidate.Column)));
            var room = JevQueryReader.GroupSlots - spec.GroupBy.Count;
            // Already split by this unit's own period (YearMonth for a month), or narrowed to one: nothing to add.
            var naming = Naming(cols, said.Unit, room + (spec.GroupBy.Contains(cols.MonthOfYear) ? 1 : 0));
            if (naming.Count == 0)
            {
                reading.Unsure.Add(CannotPick(words, cols.Days == null && (said.Unit == "week" || said.Unit == "day")
                    ? "this model has no column of days to read it on" : $"no column of this model names each {said.Unit}"));
                return;
            }
            if (naming.All(c => spec.GroupBy.Contains(c) || filtered.Contains(c)) && Names(spec, said.Unit, cols, years))
            {
                if (naming.Any(spec.GroupBy.Contains))
                    reading.Periods.Add(new JevQueryReader.PeriodFact { Unit = said.Unit, Words = said.Together ?? string.Join(" and ", words), Span = "split" });
                if (said.Periods.Count >= 2 && naming.Any(spec.GroupBy.Contains)) reading.SideBySide = true;
                return;
            }
            // A month-of-year split is replaced, not kept beside YearMonth: its months add every year together.
            if (said.Unit == "month" && cols.YearMonth != null && spec.GroupBy.Contains(cols.MonthOfYear)) spec.GroupBy.Remove(cols.MonthOfYear);
            var add = naming.Where(c => !spec.GroupBy.Contains(c) && !filtered.Contains(c)).Take(JevQueryReader.GroupSlots - spec.GroupBy.Count).ToList();
            var split = new QuerySpec();
            split.GroupBy.AddRange(add.Concat(spec.GroupBy));
            split.Filters.AddRange(spec.Filters);
            if (add.Count == 0 || !Names(split, said.Unit, cols, years))
            {
                var beside = spec.GroupBy.Where(c => !naming.Contains(c)).Select(c => JevQueryReader.Shown(c, cat)).ToList();
                reading.Unsure.Add(CannotPick(words, beside.Count == 0 ? $"no column of this model names each {said.Unit} with its year"
                    : $"split by {string.Join(" and ", naming.Where(c => !filtered.Contains(c)).Select(c => JevQueryReader.Shown(c, cat)))} beside {string.Join(" and ", beside)}, the numbers would add the same {said.Unit} of every year together"));
                return;
            }
            spec.GroupBy.InsertRange(0, add);
                        reading.Periods.Add(new JevQueryReader.PeriodFact { Unit = said.Unit, Words = said.Together ?? string.Join(" and ", words), Span = "split" });
            // Two periods named ("this year vs last year") are each a row of the split: side by side.
            if (said.Periods.Count >= 2) reading.SideBySide = true;
            var several = said.Several || words.Length > 1;
            reading.Notes.Add($"Semanticus cannot tell which {Unit(said.Unit, several)} you mean by {Quoted(words)}, so the numbers are split by "
                + $"{string.Join(" and ", spec.GroupBy.Where(add.Contains).Select(c => JevQueryReader.Shown(c, cat)))}. Read off the {(several ? "ones" : "one")} you meant.");
        }

        /// <summary>Whether a split names each period of the unit on its own: YearMonth does; a month name does only beside
        /// the year, a year narrowed to one, or data holding one year.</summary>
        private static bool Names(QuerySpec spec, string unit, Columns cols, int? years)
        {
            bool Split(QueryColumn c) => c != null && spec.GroupBy.Contains(c);
            bool OneYear() => Split(cols.Year) || years == 1
                || spec.Filters.Any(f => f.Column == cols.Year && f.Op == QueryOps.Is && f.Values.Count == 1);
            return unit switch
            {
                "month" => Split(cols.YearMonth) || (Split(cols.MonthOfYear) && OneYear()) || spec.Filters.Any(f => f.Column == cols.YearMonth),
                "quarter" => Split(cols.YearQuarter) || (Split(cols.Quarter) && OneYear()) || spec.Filters.Any(f => f.Column == cols.YearQuarter),
                "year" => Split(cols.Year) || spec.Filters.Any(f => f.Column == cols.Year),
                _ => false,
            };
        }

        /// <summary>The line for a time the engine cannot pick out: the words, and why.</summary>
        internal static string CannotPick(IEnumerable<string> words, string why) =>
            $"Semanticus cannot pick out {Quoted(words)}: {why}. Nothing was run. Ask for a period it can read, such as a year, or last month.";

        /// <summary>The assumption line when time words could not be picked out, applied or checked (K16): the reading runs over
        /// every date and says so.</summary>
        internal static string NotApplied(IEnumerable<string> words) => $"Could not apply {Quoted(words)}, so this covers every date.";

        /// <summary>The line for time words the engine cannot apply yet.</summary>
        internal static string CannotApply(IEnumerable<string> words) =>
            $"Semanticus cannot apply {Quoted(words)} yet, so nothing was run. Ask for a period it can read, such as a year, last month or last week.";

        // ---- a calendar: read against the model's latest date ---------------------------------------------------

        /// <summary>The last day the data holds a number on for the reading's first measure.</summary>
        public static string LatestQuery(QueryColumn day, QueryMeasure measure) =>
            "EVALUATE\n    ROW ( \"Latest\", MAXX ( FILTER ( VALUES ( " + day.Dax + " ), NOT ISBLANK ( " + measure.Dax + " ) ), " + day.Dax + " ) )\n";

        /// <summary>The calendar's own labels (YearMonth, Year...) for each day asked about, one row per day.</summary>
        public static string LabelsQuery(QueryColumn day, IEnumerable<DateTime> days, IEnumerable<QueryColumn> labels) =>
            "EVALUATE\n    FILTER ( ALL ( " + string.Join(", ", new[] { day }.Concat(labels).Select(c => c.Dax)) + " ), "
            + day.Dax + " IN { " + string.Join(", ", days.Select(d => string.Format(CultureInfo.InvariantCulture, "DATE ( {0}, {1}, {2} )", d.Year, d.Month, d.Day))) + " } )\n";

        private static int QuarterOf(DateTime d) => (d.Month - 1) / 3;
        private static bool EndsPeriod(DateTime d, string unit) => unit switch
        {
            "day" => true,
            "week" => d.DayOfWeek == DayOfWeek.Sunday,
            "month" => d.AddDays(1).Month != d.Month,
            "quarter" => QuarterOf(d.AddDays(1)) != QuarterOf(d) || d.AddDays(1).Year != d.Year,
            _ => d.Month == 12 && d.Day == 31,
        };
        private static DateTime EndBefore(DateTime d, string unit) => unit switch
        {
            "day" => d.Date.AddDays(-1),
            "week" => WeekStart(d).AddDays(-1),
            "month" => new DateTime(d.Year, d.Month, 1).AddDays(-1),
            "quarter" => new DateTime(d.Year, QuarterOf(d) * 3 + 1, 1).AddDays(-1),
            _ => new DateTime(d.Year, 1, 1).AddDays(-1),
        };

        /// <summary>A day inside each period the words name, read against the latest date. "this" is the period the data
        /// is in; "last" is the one before it, unless the data ends on its period's last day and "this" is not also
        /// asked, when that whole period is the last one.</summary>
        public static List<DateTime> Days(Said said, DateTime latest)
        {
            var both = said.Periods.Any(p => p.Last) && said.Periods.Any(p => !p.Last);
            return said.Periods.Select(p => !p.Last || (!said.ToDate && !both && EndsPeriod(latest, said.Unit)) ? latest : EndBefore(latest, said.Unit)).ToList();
        }

        /// <summary>Where each period the words name was read: the period the data is in ("latest") or the one before it
        /// ("before"), by the same rule Days uses. Told to Jev without a date.</summary>
        internal static List<string> Spans(Said said, DateTime latest)
        {
            var both = said.Periods.Any(p => p.Last) && said.Periods.Any(p => !p.Last);
            return said.Periods.Select(p => !p.Last || (!said.ToDate && !both && EndsPeriod(latest, said.Unit)) ? "latest" : "before").ToList();
        }

        /// <summary>The span "last year / quarter / month to date" reads: the first day of the earlier period up to the same
        /// day of it as the last day the data holds (clamped to the earlier period's own last day), as the day it starts
        /// and the first day after it ends.</summary>
        public static (DateTime From, DateTime Before) ToDateSpan(DateTime latest, string unit)
        {
            latest = latest.Date;
            var end = unit switch
            {
                "month" => latest.AddMonths(-1),
                "quarter" => latest.AddMonths(-3),
                _ => latest.AddYears(-1),
            };
            return (Span(end, unit).From, end.AddDays(1));
        }

        /// <summary>What a resolve needs to be tried: the words name single periods, the model has a calendar, and the
        /// columns naming the period are free (a column the person already narrowed by is theirs).</summary>
        public static List<QueryColumn> ResolveColumns(JevQueryReader.Reading reading, Said said, Columns cols)
        {
            if (said == null || said.Periods.Count == 0 || cols.Day == null) return null;
            var naming = Naming(cols, said.Unit, 2);
            // A month name or a quarter with no year beside it is that month of EVERY year: not a label for one period.
            // The days are read instead (RangeReady). A label column the words already narrow by is kept: the read must
            // meet it (MustMeet), so "last year in 2024" is said, not quietly shown as 2024.
            var names = said.Unit switch
            {
                "month" => cols.YearMonth != null || (cols.Year != null && cols.MonthOfYear != null),
                "quarter" => cols.YearQuarter != null || (cols.Year != null && cols.Quarter != null),
                _ => naming.Count > 0,
            };
            return names && naming.Count > 0 ? naming : null;
        }

        /// <summary>Filter by the labels read for each period, split by them when two periods are compared, and say what
        /// was read. False when the labels do not name each period on their own (then the caller splits instead).</summary>
        public static bool Resolve(JevQueryReader.Reading reading, Said said, Columns cols, QueryCatalog cat, DateTime latest,
            IReadOnlyList<QueryColumn> naming, IReadOnlyList<object[]> labels)
        {
            if (labels.Count != said.Periods.Count || labels.Any(l => l == null || l.Length != naming.Count || l.Any(v => v == null || v is DBNull))) return false;
            var spec = reading.Spec;
            // Two periods named by two columns each (Year and MonthName for December and January) would cross into
            // periods nobody asked for (January of the first year): only one column may differ between them.
            var differs = Enumerable.Range(0, naming.Count).Where(i => labels.Select(l => Text(l[i])).Distinct().Count() > 1).ToList();
            if (differs.Count > 1) return false;
            if (differs.Count == 1)
            {
                // Two periods side by side need a split of their own; with no room, both filtered would add them together.
                var by = naming[differs[0]];
                // A month name the labels name themselves (Year and MonthName for two months) gives way to the one label
                // that differs. A month split beside two YEARS is the question's own ("this year vs last year by month").
                var own = naming.Contains(cols.MonthOfYear) ? cols.MonthOfYear : null;
                var left = spec.GroupBy.Where(c => c != own).ToList();
                if (!left.Contains(by) && left.Count >= JevQueryReader.GroupSlots)
                {
                    reading.Unsure.Add($"Semanticus cannot show {Quoted(said.Words)} side by side beside {string.Join(" and ", left.Select(c => JevQueryReader.Shown(c, cat)))}, "
                        + "and added into one total they are not what the question compares. Nothing was run. Ask with one split fewer.");
                    return true;
                }
                if (own != null) spec.GroupBy.Remove(own);
                if (!spec.GroupBy.Contains(by)) spec.GroupBy.Insert(0, by);
                reading.SideBySide = true;
            }
            var spans = Spans(said, latest);
            var facts = said.Periods.Select((p, i) => new JevQueryReader.PeriodFact { Unit = said.Unit, Words = p.Words, Span = spans[i] }).ToList();
            for (var i = 0; i < naming.Count; i++)
            {
                var values = labels.Select(l => l[i]).GroupBy(Text).Select(g => g.First()).ToList();
                // Already narrowed to exactly these labels by the words, and the read meets it (MustMeet): not filtered twice. A filter that
                // only MEETS the read ("not 2023" beside last year 2024, or "2024 and 2025") leaves other periods in, so the read is added
                // beside it and the query keeps what both say (Sol 3.3): the period the words named, never a wider one under a note that
                // says the words were applied.
                var wanted = new HashSet<string>(values.Select(Text), StringComparer.Ordinal);
                if (spec.Filters.Any(f => f.Column == naming[i] && f.Op == QueryOps.Is && f.Values.Select(Text).ToHashSet(StringComparer.Ordinal).SetEquals(wanted))) continue;
                var added = new QueryFilter { Column = naming[i], Op = QueryOps.Is, Values = values };
                spec.Filters.Add(added);
                foreach (var fact in facts) fact.Filters.Add(added);
            }
            reading.Periods.AddRange(facts);
            string Named(object[] l) => string.Join(", ", naming.Select((c, i) => JevQueryReader.Shown(c, cat) + " " + Text(l[i])));
            var read = said.Periods.Select((p, i) => i == 0 ? $"'{p.Words}' is read as {Named(labels[i])}" : $"'{p.Words}' as {Named(labels[i])}");
            reading.Notes.Add($"This model's data runs to {latest.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}, so {string.Join(" and ", read)}.");
            return true;
        }

        // ---- a range of days: weeks, days, and a single period no label of the calendar names --------------------

        /// <summary>A week runs Monday to Sunday.</summary>
        private static DateTime WeekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

        /// <summary>The first day of the period of this unit that holds the day, and the first day after it. The range
        /// ends BEFORE the next period's first day, so a column of days that also holds a time of day is not cut short.</summary>
        public static (DateTime From, DateTime Before) Span(DateTime d, string unit)
        {
            d = d.Date;
            switch (unit)
            {
                case "day": return (d, d.AddDays(1));
                case "week": return (WeekStart(d), WeekStart(d).AddDays(7));
                case "month": { var f = new DateTime(d.Year, d.Month, 1); return (f, f.AddMonths(1)); }
                case "quarter": { var f = new DateTime(d.Year, QuarterOf(d) * 3 + 1, 1); return (f, f.AddMonths(3)); }
                default: { var f = new DateTime(d.Year, 1, 1); return (f, f.AddYears(1)); }
            }
        }

        /// <summary>A range can be read: one single period, a column of days, and no period column the person named.</summary>
        public static bool RangeReady(JevQueryReader.Reading reading, Said said, Columns cols)
        {
            if (said == null || said.Periods.Count != 1 || cols.Days == null) return false;
            var filtered = reading.Spec.Filters.Select(f => f.Column).Concat(reading.Filters.Select(f => f.Candidate.Column)).ToList();
            return !filtered.Contains(cols.Days);
        }

        /// <summary>Filter the column of days to the one period the words name, and say what was read.</summary>
        public static void ResolveRange(JevQueryReader.Reading reading, Said said, Columns cols, QueryCatalog cat, DateTime latest)
        {
            var day = Days(said, latest)[0];
            var (from, before) = said.ToDate ? ToDateSpan(latest, said.Unit) : Span(day, said.Unit);
            var lower = new QueryFilter { Column = cols.Days, Op = QueryOps.From, Values = new List<object> { from } };
            var upper = new QueryFilter { Column = cols.Days, Op = QueryOps.Before, Values = new List<object> { before } };
            reading.Spec.Filters.Add(lower);
            reading.Spec.Filters.Add(upper);
            reading.Periods.Add(new JevQueryReader.PeriodFact
            {
                Unit = said.Unit, Words = said.Periods[0].Words, Span = said.ToDate ? "todate-before" : Spans(said, latest)[0],
                Filters = { lower, upper },
            });
            var read = said.Unit == "day" ? Day(from) + (from == latest.Date ? ", its last day" : "") : Range(from, before.AddDays(-1), said.Unit == "week");
            reading.Notes.Add($"This model's data runs to {Day(latest)}, so '{said.Periods[0].Words}' is read as {read}"
                + (cols.Day == null ? ", by " + JevQueryReader.Shown(cols.Days, cat) : "") + ".");
        }

        private static string Day(DateTime d) => d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

        private static string Range(DateTime first, DateTime last, bool weekdays)
        {
            var day = weekdays ? "dddd d MMMM" : "d MMMM";
            return first.Year == last.Year
                ? first.ToString(day, CultureInfo.InvariantCulture) + " to " + last.ToString(day + " yyyy", CultureInfo.InvariantCulture)
                : first.ToString(day + " yyyy", CultureInfo.InvariantCulture) + " to " + last.ToString(day + " yyyy", CultureInfo.InvariantCulture);
        }

        // ---- a period read against the latest date must meet a period the words also name ----------------------

        /// <summary>The filters on the calendar's period columns a read must meet ("in 2024" beside "last month"): each one
        /// the same on every day of the period read, so one day of it settles whether they meet. A week is checked day by
        /// day, since it can cross a month or a year.</summary>
        public static List<QueryFilter> MustMeet(JevQueryReader.Reading reading, Said said, Columns cols, IEnumerable<QueryColumn> naming)
        {
            var same = said.Unit switch
            {
                "month" => new[] { cols.Year, cols.YearMonth, cols.MonthOfYear, cols.Quarter, cols.YearQuarter },
                "quarter" => new[] { cols.Year, cols.Quarter, cols.YearQuarter },
                "year" => new[] { cols.Year },
                _ => cols.Named.ToArray(),
            };
            // Every kind of filter ("in 2024", "not 2024", "before 2025"), and the label columns the read itself names.
            return reading.Spec.Filters.Where(f => f.Column != null && same.Concat(naming).Contains(f.Column)).ToList();
        }

        /// <summary>The days whose labels settle whether the read meets them: one per period, or each day of a week.</summary>
        public static List<DateTime> CheckDays(Said said, DateTime latest) =>
            said.Unit == "week" ? Enumerable.Range(0, 7).Select(i => Span(Days(said, latest)[0], "week").From.AddDays(i)).ToList() : Days(said, latest);

        /// <summary>Whether the labels read meet every filter: on every period read, or on some day of a week.</summary>
        public static bool Meets(IReadOnlyList<QueryFilter> must, IReadOnlyList<QueryColumn> asked, IReadOnlyList<object[]> labels, bool anyDay)
        {
            bool On(object[] l) => l != null && must.All(f =>
            {
                var i = asked.ToList().IndexOf(f.Column);
                if (i < 0 || i >= l.Length) return false;
                var label = l[i];
                switch (f.Op)
                {
                    case QueryOps.Is: return f.Values.Any(v => Text(v) == Text(label));
                    case QueryOps.IsNot: return f.Values.All(v => Text(v) != Text(label));
                    default:
                        if (!double.TryParse(Text(label), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                            || !double.TryParse(Text(f.Values[0]), NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return false;
                        return f.Op switch { QueryOps.After => x > b, QueryOps.From => x >= b, QueryOps.Before => x < b, _ => x <= b };
                }
            });
            return anyDay ? labels.Any(On) : labels.All(On);
        }

        /// <summary>The line when a period read against the latest date misses a period the words also name.</summary>
        public static string Misses(Said said, DateTime latest, IEnumerable<QueryFilter> must, QueryCatalog cat) =>
            $"{Quoted(said.Words)} is read against this model's latest date, {Day(latest)}, and falls outside "
            + $"{string.Join(" and ", must.Select(f => QueryTemplate.FilterWords(f, cat)))}. Nothing was run. Say which period you mean.";

        /// <summary>The line when there is no calendar to check a period read against one the words also name.</summary>
        public static string CannotCheck(Said said, IEnumerable<QueryFilter> must, QueryCatalog cat) =>
            $"Semanticus cannot tell whether {Quoted(said.Words)} and {string.Join(" and ", must.Select(f => QueryTemplate.FilterWords(f, cat)))} meet on this model. Nothing was run. Say which period you mean.";

        internal static string Text(object v) => v is DateTime d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture);

        /// <summary>A label as the query's filter wants it: a whole number as a whole number.</summary>
        public static object Typed(QueryColumn c, object v)
        {
            if (c.Kind != "whole" || v is long) return v;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return v; }
        }

        // ---- months of the year ----------------------------------------------------------------------------------

        /// <summary>A month or a quarter of the year with no year in it (MonthName, Quarter): every year's is one.</summary>
        internal static bool OfYear(QueryColumn c) => IsMonthOfYear(c) || IsQuarter(c);

        /// <summary>The query pins every row to one year for <paramref name="ofYear"/> (a month or a quarter of the year):
        /// split by a column of the same calendar that names the year (Year, YearMonth, a day), or filtered to one year of
        /// it (Year = 2025, months or quarters of one year, days inside one year). A range of years ("since 2024") does
        /// not, and nor does a year of another table ("stores opening in 2023"): January of each year would still be one
        /// row. A column of days on the rows the numbers count (Sales[OrderDate]) is the same calendar.</summary>
        internal static bool YearNamed(QuerySpec spec, QueryColumn ofYear, QueryCatalog cat)
        {
            var rows = new HashSet<string>(spec.Measures.SelectMany(m => cat.Roots(m)), StringComparer.Ordinal);
            bool Same(QueryColumn c) => c.Table == ofYear.Table || (c.Kind == "date" && rows.Contains(c.Table));
            if (spec.GroupBy.Any(c => Same(c) && (IsYear(c) || IsYearMonth(c) || IsYearQuarter(c) || c.Kind == "date"))) return true;
            foreach (var f in spec.Filters.Where(f => Same(f.Column)))
            {
                if (IsYear(f.Column) && f.Op == QueryOps.Is && f.Values.Count == 1) return true;
                if ((IsYearMonth(f.Column) || IsYearQuarter(f.Column)) && f.Op == QueryOps.Is && f.Values.Count > 0
                    && f.Values.Select(v => Text(v)).Select(t => t.Length >= 4 ? t.Substring(0, 4) : t).Distinct().Count() == 1) return true;
            }
            // Days inside one year: a lower and an upper bound on one column of days that meet in the same year.
            foreach (var g in spec.Filters.Where(f => Same(f.Column) && f.Column.Kind == "date" && f.Values.Count == 1 && f.Values[0] is DateTime).GroupBy(f => f.Column))
            {
                var low = g.Where(f => f.Op == QueryOps.From || f.Op == QueryOps.After).Select(f => (DateTime)f.Values[0]).DefaultIfEmpty(DateTime.MinValue).Max();
                var high = g.Where(f => f.Op == QueryOps.Before || f.Op == QueryOps.UpTo).Select(f => f.Op == QueryOps.Before ? ((DateTime)f.Values[0]).AddDays(-1) : (DateTime)f.Values[0])
                    .DefaultIfEmpty(DateTime.MaxValue).Min();
                if (low != DateTime.MinValue && high != DateTime.MaxValue && low.Year == high.Year) return true;
            }
            return false;
        }


        private static readonly Regex RankingQuarters = new Regex(
            @"\b(best|worst|biggest|highest|lowest|strongest|weakest|busiest|quietest|top|bottom)\b(\s+\w+){0,3}\s+quarters\b|\bquarters\b.*\b(ever|all[\s-]time|on\s+record)\b", Opt);
        private static readonly Regex RankingMonths = new Regex(
            @"\b(best|worst|biggest|highest|lowest|strongest|weakest|busiest|quietest|top|bottom)\b(\s+\w+){0,3}\s+months\b|\bmonths\b.*\b(ever|all[\s-]time|on\s+record)\b", Opt);

        /// <summary>A month or a quarter of the year with no year (December, Q3, split by MonthName), on data holding
        /// more than one year, is split by year too, so the same month of every year is never added up; a ranking of months
        /// ranks each month of each year. With no room for the year, nothing is changed here, and the coverage check needs
        /// you (QueryCoverage). <paramref name="yearValues"/> is how many years the data holds, when read.</summary>
        public static void MonthsOfYears(JevQueryReader.Reading reading, string words, Columns cols, QueryCatalog cat, int? yearValues)
        {
            foreach (var (ofYear, withYear, unit) in new[] { (cols.MonthOfYear, cols.YearMonth, "month"), (cols.Quarter, cols.YearQuarter, "quarter") })
            {
                var spec = reading.Spec;
                if (ofYear == null || YearNamed(spec, ofYear, cat)) continue;
                var room = spec.GroupBy.Count < JevQueryReader.GroupSlots;
                var filter = spec.Filters.FirstOrDefault(f => f.Column == ofYear && f.Op == QueryOps.Is);
                if (filter != null && !spec.GroupBy.Contains(ofYear))
                {
                    if (cols.Year == null || !(yearValues > 1) || !room) continue;
                    spec.GroupBy.Insert(0, cols.Year);
                    reading.Notes.Add($"{string.Join(" and ", filter.Values.Select(Text))} comes round in more than one year of this model's data, so the numbers are split by {JevQueryReader.Shown(cols.Year, cat)}.");
                    continue;
                }
                if (!spec.GroupBy.Contains(ofYear)) continue;
                var ranks = spec.Top.HasValue || RankingMonths.IsMatch(words ?? "") || RankingQuarters.IsMatch(words ?? "");
                if (!ranks && !(yearValues > 1)) continue;
                // A ranking wants each period of each year as one row, so the one column naming both comes first. Otherwise
                // the year goes beside the period, the way the question split it.
                if (withYear != null && (ranks || !room || cols.Year == null))
                {
                    spec.GroupBy[spec.GroupBy.IndexOf(ofYear)] = withYear;
                    if (spec.SortColumn == ofYear) spec.SortColumn = withYear;
                    reading.Notes.Add($"Split by {JevQueryReader.Shown(withYear, cat)}, so each {unit} of each year is its own row.");
                }
                else if (cols.Year != null && room)
                {
                    spec.GroupBy.Insert(spec.GroupBy.IndexOf(ofYear), cols.Year);
                    reading.Notes.Add($"Split by {JevQueryReader.Shown(cols.Year, cat)} too, so each {unit} of each year is its own row.");
                }
            }
        }
    }
}
