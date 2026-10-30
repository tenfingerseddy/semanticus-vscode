using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Semanticus.Engine
{
    // Two checks the calendar work needs and the engine did not have (1.7 engine groundwork, design notes section 6):
    //   - the calendar DATA check: do the calendar's dates cover the dates in the facts, does the calendar run past the data, and is the
    //     calendar itself sound (no blanks, each quarter inside one year, a column and its other names one-to-one);
    //   - the NUMBER check: do an old measure and its replacement give the same numbers over a known slice.
    // This file is the pure half: the DAX it asks and the judgement of what comes back. It runs nothing, so every rule is testable offline.
    // The live half (LocalEngine.CalendarChecks.cs) runs the queries and never reports a degraded run as a pass: no live model, a failed
    // query or a slice that is blank on both sides is "not checked".
    //
    // Both ask their questions as ONE `EVALUATE ROW(...)` with a single text cell. That is the shape the data gate reads as a calculation
    // (DaxQueryClassifier), so the check moves no source rows to the assistant and never waits for a data approval.

    public sealed class CalendarDataFinding
    {
        public string Calendar { get; set; }
        /// <summary>covers-facts | past-the-data | duplicate-dates | blanks | level-split | one-to-one</summary>
        public string Check { get; set; }
        /// <summary>problem: a wrong number is likely. note: worth knowing, nothing is wrong yet.</summary>
        public string Level { get; set; }
        public string Sentence { get; set; }
        public string[] Examples { get; set; } = new string[0];
    }

    public sealed class CalendarDataTable
    {
        public string Table { get; set; }
        public string DateColumn { get; set; }
        public string[] Calendars { get; set; } = new string[0];
        public string CalendarFrom { get; set; }
        public string CalendarTo { get; set; }
        public string DataFrom { get; set; }
        public string DataTo { get; set; }
        /// <summary>The fact columns that point at this date column, as 'Table'[Column].</summary>
        public string[] FactColumns { get; set; } = new string[0];
    }

    public sealed class CalendarDataCheckResult
    {
        /// <summary>ok: checked, nothing to fix. found: checked, there are findings. notChecked: it could not run, and that is not a pass.</summary>
        public string Status { get; set; }
        public string Reason { get; set; }
        public CalendarDataTable[] Tables { get; set; } = new CalendarDataTable[0];
        public CalendarDataFinding[] Findings { get; set; } = new CalendarDataFinding[0];
        public string Sentence { get; set; }
    }

    /// <summary>One calendar read from the model: what the structure checks need.</summary>
    public sealed class CalendarCheckShape
    {
        public string Name { get; set; }
        /// <summary>Levels in order, by the unit they hold: Year, Quarter, Month, Date. Only the units that nest are listed.</summary>
        public (string Unit, string Column)[] Levels { get; set; } = new (string, string)[0];
        /// <summary>A column and the other columns that must move with it one-to-one.</summary>
        public (string Primary, string[] Associated)[] Pairs { get; set; } = new (string, string[])[0];
    }

    public sealed class CalendarCheckInput
    {
        public string Table { get; set; }
        public string DateColumn { get; set; }
        /// <summary>The fact columns in 'Table'[Column] form.</summary>
        public string[] FactColumns { get; set; } = new string[0];
        public CalendarCheckShape[] Calendars { get; set; } = new CalendarCheckShape[0];
    }

    public sealed class CalendarRange
    {
        public DateTime? CalendarFrom, CalendarTo, DataFrom, DataTo;
        public long Rows, Days;
    }

    public static class CalendarDataChecks
    {
        private static readonly string[] NestingOrder = { "Year", "Semester", "Quarter", "Month", "Date" };

        /// <summary>The units a hierarchy check applies to, in order from the widest to the day. "QuarterOfYear" and the like cycle (Q1 comes round every year), so they never nest.</summary>
        public static int NestingRank(string unit) => Array.IndexOf(NestingOrder, unit ?? string.Empty);

        public static string Quote(string table, string column) => "'" + (table ?? "").Replace("'", "''") + "'[" + (column ?? "").Replace("]", "]]") + "]";

        /// <summary>The dates: the calendar's first and last, how many rows and distinct days it holds, and the first and last date in every fact column that points at it.</summary>
        public static string RangeQuery(CalendarCheckInput i)
        {
            var date = Quote(i.Table, i.DateColumn);
            var cells = new List<string>
            {
                "\"calMin\", MIN ( " + date + " )", "\"calMax\", MAX ( " + date + " )",
                "\"rows\", COUNTROWS ( '" + i.Table.Replace("'", "''") + "' )", "\"days\", DISTINCTCOUNT ( " + date + " )",
            };
            for (var n = 0; n < i.FactColumns.Length; n++)
            {
                cells.Add("\"f" + n + "min\", MIN ( " + i.FactColumns[n] + " )");
                cells.Add("\"f" + n + "max\", MAX ( " + i.FactColumns[n] + " )");
            }
            return "EVALUATE ROW ( " + string.Join(", ", cells) + " )";
        }

        /// <summary>The structure of one calendar as counts of rows that break a rule. Cell names say which rule: b0 (blanks in a level), s0 (child in two parents), a0 (a column and its other name do not pair).</summary>
        public static (string Dax, string[] Labels) StructureQuery(string table, CalendarCheckShape cal)
        {
            var t = "'" + table.Replace("'", "''") + "'";
            var cells = new List<string>(); var labels = new List<string>();
            for (var n = 0; n < cal.Levels.Length; n++)
            {
                var c = Quote(table, cal.Levels[n].Column);
                cells.Add("\"b" + n + "\", COUNTROWS ( FILTER ( " + t + ", ISBLANK ( " + c + " ) ) )");
                labels.Add("b" + n + "|" + cal.Levels[n].Unit + "|" + cal.Levels[n].Column);
            }
            for (var n = 0; n + 1 < cal.Levels.Length; n++)
            {
                var parent = Quote(table, cal.Levels[n].Column); var child = Quote(table, cal.Levels[n + 1].Column);
                cells.Add("\"s" + n + "\", COUNTROWS ( FILTER ( VALUES ( " + child + " ), CALCULATE ( DISTINCTCOUNT ( " + parent + " ) ) > 1 ) )");
                labels.Add("s" + n + "|" + cal.Levels[n].Column + "|" + cal.Levels[n + 1].Column);
            }
            var k = 0;
            foreach (var (primary, associated) in cal.Pairs)
                foreach (var other in associated)
                {
                    var p = Quote(table, primary); var a = Quote(table, other);
                    cells.Add("\"a" + k + "\", COUNTROWS ( FILTER ( VALUES ( " + p + " ), CALCULATE ( DISTINCTCOUNT ( " + a + " ) ) > 1 ) ) + COUNTROWS ( FILTER ( VALUES ( " + a + " ), CALCULATE ( DISTINCTCOUNT ( " + p + " ) ) > 1 ) )");
                    labels.Add("a" + k + "|" + primary + "|" + other);
                    k++;
                }
            return cells.Count == 0 ? (null, labels.ToArray()) : ("EVALUATE ROW ( " + string.Join(", ", cells) + " )", labels.ToArray());
        }

        /// <summary>Judge the dates. A fact date the calendar does not hold is a problem (time intelligence drops those rows). A calendar that runs past the last fact date
        /// is a note: it is normal to build a calendar to the end of the year, but a measure written to start from the calendar's last date reads empty months.</summary>
        public static IEnumerable<CalendarDataFinding> JudgeRange(CalendarCheckInput i, CalendarRange r)
        {
            var name = i.Table;
            if (r.DataFrom == null || r.DataTo == null)
            {
                if (i.FactColumns.Length > 0)
                    yield return new CalendarDataFinding { Calendar = name, Check = "covers-facts", Level = "note", Sentence = "The fact tables have no dates to compare, so coverage was not checked." };
                yield break;
            }
            if (r.CalendarFrom == null || r.CalendarTo == null)
            {
                yield return new CalendarDataFinding { Calendar = name, Check = "covers-facts", Level = "problem", Sentence = "The calendar table '" + name + "' holds no dates." };
                yield break;
            }
            if (r.DataFrom.Value.Date < r.CalendarFrom.Value.Date)
                yield return new CalendarDataFinding
                {
                    Calendar = name, Check = "covers-facts", Level = "problem",
                    Sentence = "The facts start " + Days(r.CalendarFrom.Value, r.DataFrom.Value) + " before the calendar does, so those rows get no year or month. The calendar starts " + Day(r.CalendarFrom.Value) + " and the data starts " + Day(r.DataFrom.Value) + ".",
                    Examples = new[] { "Calendar from " + Day(r.CalendarFrom.Value), "Data from " + Day(r.DataFrom.Value) },
                };
            if (r.DataTo.Value.Date > r.CalendarTo.Value.Date)
                yield return new CalendarDataFinding
                {
                    Calendar = name, Check = "covers-facts", Level = "problem",
                    Sentence = "The facts run " + Days(r.DataTo.Value, r.CalendarTo.Value) + " past the end of the calendar, so those rows get no year or month. The calendar ends " + Day(r.CalendarTo.Value) + " and the data ends " + Day(r.DataTo.Value) + ".",
                    Examples = new[] { "Calendar to " + Day(r.CalendarTo.Value), "Data to " + Day(r.DataTo.Value) },
                };
            // Past the data by more than the rest of the month the data ends in.
            var endOfDataMonth = new DateTime(r.DataTo.Value.Year, r.DataTo.Value.Month, DateTime.DaysInMonth(r.DataTo.Value.Year, r.DataTo.Value.Month));
            if (r.CalendarTo.Value.Date > endOfDataMonth)
                yield return new CalendarDataFinding
                {
                    Calendar = name, Check = "past-the-data", Level = "note",
                    Sentence = "The calendar runs to " + Day(r.CalendarTo.Value) + ", " + Months(endOfDataMonth, r.CalendarTo.Value) + " past the last date with data (" + Day(r.DataTo.Value) + "). A measure that starts from the last date of the calendar reads empty months. The rolling measures Semanticus makes start from the last date with data.",
                    Examples = new[] { "Calendar to " + Day(r.CalendarTo.Value), "Data to " + Day(r.DataTo.Value) },
                };
            // The facts' first and last dates can sit inside the calendar while a day between them is missing: that day's rows get no year or month.
            var span = (long)(r.CalendarTo.Value.Date - r.CalendarFrom.Value.Date).TotalDays + 1;
            if (r.Days > 0 && r.Days < span)
                yield return new CalendarDataFinding
                {
                    Calendar = name, Check = "gap-in-calendar", Level = "problem",
                    Sentence = "The calendar runs from " + Day(r.CalendarFrom.Value) + " to " + Day(r.CalendarTo.Value) + " but holds " + r.Days + " of its " + span + " days, so " + (span - r.Days) + (span - r.Days == 1 ? " day is" : " days are") + " missing in between. Facts on a missing day get no year or month.",
                    Examples = new[] { "Calendar from " + Day(r.CalendarFrom.Value), "Calendar to " + Day(r.CalendarTo.Value) },
                };
            if (r.Rows > 0 && r.Days > 0 && r.Rows != r.Days)
                yield return new CalendarDataFinding
                {
                    Calendar = name, Check = "duplicate-dates", Level = "problem",
                    Sentence = "The calendar table has " + r.Rows + " rows but " + r.Days + " different dates, so some dates are there twice and sums over it will count them twice.",
                };
        }

        /// <summary>Judge the structure counts that came back, keyed by cell name.</summary>
        public static IEnumerable<CalendarDataFinding> JudgeStructure(string calendar, string[] labels, IReadOnlyDictionary<string, long> counts)
        {
            foreach (var label in labels)
            {
                var parts = label.Split('|');
                if (!counts.TryGetValue(parts[0], out var n) || n <= 0) continue;
                switch (parts[0][0])
                {
                    case 'b':
                        yield return new CalendarDataFinding
                        {
                            Calendar = calendar, Check = "blanks", Level = "problem",
                            Sentence = n + (n == 1 ? " date has" : " dates have") + " no " + parts[1].ToLowerInvariant() + " (" + parts[2] + " is blank), so " + (n == 1 ? "it" : "they") + " fall out of every " + parts[1].ToLowerInvariant() + " total.",
                        };
                        break;
                    case 's':
                        yield return new CalendarDataFinding
                        {
                            Calendar = calendar, Check = "level-split", Level = "problem",
                            Sentence = n + (n == 1 ? " value of " : " values of ") + parts[2] + " sit" + (n == 1 ? "s" : "") + " in more than one " + parts[1] + ", so a total for that " + parts[1] + " will be wrong. Make " + parts[2] + " say which " + parts[1] + " it belongs to (for example 2025 Q1, not Q1).",
                        };
                        break;
                    case 'a':
                        yield return new CalendarDataFinding
                        {
                            Calendar = calendar, Check = "one-to-one", Level = "problem",
                            Sentence = parts[1] + " and " + parts[2] + " do not pair up one to one for " + n + (n == 1 ? " value" : " values") + ". Each value of one must have exactly one value of the other.",
                        };
                        break;
                }
            }
        }

        public static string Sentence(CalendarDataFinding[] findings, int tables)
        {
            var problems = findings.Count(f => f.Level == "problem"); var notes = findings.Count(f => f.Level == "note");
            if (findings.Length == 0) return "The calendar dates cover the data and the calendar is sound (" + tables + (tables == 1 ? " date table" : " date tables") + " checked).";
            return problems + (problems == 1 ? " problem" : " problems") + (notes > 0 ? " and " + notes + (notes == 1 ? " note" : " notes") : "") + " in the calendar data.";
        }

        private static string Day(DateTime d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        private static string Days(DateTime a, DateTime b)
        {
            var n = (int)Math.Abs((a.Date - b.Date).TotalDays);
            return n + (n == 1 ? " day" : " days");
        }
        private static string Months(DateTime from, DateTime to)
        {
            var n = (to.Year - from.Year) * 12 + to.Month - from.Month;
            return n + (n == 1 ? " month" : " months");
        }
    }

    // ---- the number check ------------------------------------------------------------------------

    public sealed class MeasureNumbersPair
    {
        public string Old { get; set; }
        public string New { get; set; }
        /// <summary>same | differs | notChecked</summary>
        public string Verdict { get; set; }
        public long Slices { get; set; }
        public long Differing { get; set; }
        /// <summary>Up to five slices that differ: the slice, then the old value and the new value.</summary>
        public string[] Examples { get; set; } = new string[0];
        public string Reason { get; set; }
    }

    public sealed class MeasureNumbersResult
    {
        /// <summary>same | differs | notChecked. Same only when every pair is same.</summary>
        public string Verdict { get; set; }
        /// <summary>The columns the numbers were compared by, as 'Table'[Column].</summary>
        public string[] By { get; set; } = new string[0];
        public MeasureNumbersPair[] Pairs { get; set; } = new MeasureNumbersPair[0];
        public string Sentence { get; set; }
    }

    public static class MeasureNumbers
    {
        public const string AbsoluteTolerance = "0.000000001";
        public const string RelativeTolerance = "0.0000001";
        public const int ExampleLimit = 5;

        private static string Measure(string name) => "[" + (name ?? "").Replace("]", "]]") + "]";

        /// <summary>One scalar query: the old and the new measure side by side over every slice, answered as text "slices|bothBlank|differing|examples".
        /// A blank counts as zero, so a measure that shows nothing where the other shows 0 is not a difference; a number that moved is.
        /// With leapDate, append a fifth field: the number of compared days around a leap day inside the fact date range.</summary>
        public static string Query(string oldMeasure, string newMeasure, string[] by, string leapDate = null, string[] factColumns = null)
        {
            if (by == null || by.Length == 0) throw new ArgumentException("Say which columns to compare the numbers by.", nameof(by));
            var cols = string.Join(", ", by);
            var slice = string.Join(" & \" / \" & ", by.Select(b => b + " & \"\""));
            var a = "COALESCE ( [a], 0 )"; var b2 = "COALESCE ( [b], 0 )";
            var sb = new StringBuilder();
            sb.Append("EVALUATE ROW ( \"r\",\n");
            sb.Append("  VAR _t = SUMMARIZECOLUMNS ( ").Append(cols).Append(", \"a\", ").Append(Measure(oldMeasure)).Append(", \"b\", ").Append(Measure(newMeasure)).Append(" )\n");
            sb.Append("  VAR _bad = FILTER ( _t, ABS ( ").Append(a).Append(" - ").Append(b2).Append(" ) > ").Append(AbsoluteTolerance).Append(" + ").Append(RelativeTolerance).Append(" * ABS ( ").Append(b2).Append(" ) )\n");
            if (leapDate != null)
            {
                // A future leap year in an extended Date table is not evidence. The leap day must fall inside the fact dates,
                // and the comparison must evaluate Feb 28, Feb 29 and Mar 1 of that same year at day grain.
                string Bound(string fn) => factColumns == null || factColumns.Length == 0 ? "BLANK ()"
                    : fn + "X ( { " + string.Join(", ", factColumns.Select(c => fn + " ( " + c + " )")) + " }, [Value] )";
                sb.Append("  VAR _from = ").Append(Bound("MIN")).Append("\n  VAR _to = ").Append(Bound("MAX")).Append('\n');
                sb.Append("  VAR _leapYear = MAXX ( FILTER ( _t, NOT ISBLANK ( _from ) && NOT ISBLANK ( _to ) && ")
                    .Append(leapDate).Append(" >= _from && ").Append(leapDate).Append(" <= _to && MONTH ( ").Append(leapDate)
                    .Append(" ) = 2 && DAY ( ").Append(leapDate).Append(" ) = 29 && NOT ( ISBLANK ( [a] ) && ISBLANK ( [b] ) ) ), YEAR ( ")
                    .Append(leapDate).Append(" ) )\n");
                sb.Append("  VAR _aroundLeap = FILTER ( _t, NOT ISBLANK ( _leapYear ) && ").Append(leapDate)
                    .Append(" >= DATE ( _leapYear, 2, 28 ) && ").Append(leapDate).Append(" <= DATE ( _leapYear, 3, 1 ) )\n");
            }
            sb.Append("  RETURN COUNTROWS ( _t ) & \"|\" & COUNTROWS ( FILTER ( _t, ISBLANK ( [a] ) && ISBLANK ( [b] ) ) ) & \"|\" & COUNTROWS ( _bad ) & \"|\" &\n");
            sb.Append("    CONCATENATEX ( TOPN ( ").Append(ExampleLimit).Append(", _bad, ABS ( ").Append(a).Append(" - ").Append(b2).Append(" ), DESC ), ").Append(slice)
              .Append(" & \": \" & FORMAT ( [a], \"0.######\" ) & \" vs \" & FORMAT ( [b], \"0.######\" ), \";\" )");
            if (leapDate != null) sb.Append(" & \"|\" & COUNTROWS ( _aroundLeap )");
            sb.Append(" )");
            return sb.ToString();
        }

        internal static MeasureNumbersPair JudgeCalendarMove(string measure, string answer, string error = null)
        {
            var parts = (answer ?? "").Split('|');
            if (parts.Length != 5 || !long.TryParse(parts[4], out var days) || days != 3)
                return NotChecked(new MeasureNumbersPair { Old = measure, New = measure },
                    error ?? "the day comparison did not cover 28 February, 29 February and 1 March in a leap year in the data");
            return Judge(measure, measure, string.Join("|", parts.Take(4)), error);
        }

        /// <summary>Read the answer. Anything that does not read as four parts is "not checked", never a pass.</summary>
        public static MeasureNumbersPair Judge(string oldMeasure, string newMeasure, string answer, string error = null)
        {
            var p = new MeasureNumbersPair { Old = oldMeasure, New = newMeasure };
            if (!string.IsNullOrEmpty(error)) return NotChecked(p, "the query failed: " + error);
            var parts = (answer ?? string.Empty).Split(new[] { '|' }, 4);
            if (parts.Length < 3 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slices)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bothBlank)
                || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var differing))
                return NotChecked(p, "the answer could not be read");
            p.Slices = slices; p.Differing = differing;
            if (slices == 0) return NotChecked(p, "there are no rows in the slice, so nothing was compared");
            if (bothBlank == slices) return NotChecked(p, "both measures are blank everywhere in the slice, so nothing was compared");
            if (differing == 0)
            {
                p.Verdict = "same";
                p.Reason = "Same numbers in all " + slices + (slices == 1 ? " slice." : " slices.");
                return p;
            }
            p.Verdict = "differs";
            p.Examples = parts.Length > 3 && parts[3].Length > 0 ? parts[3].Split(';') : new string[0];
            p.Reason = differing + " of " + slices + (slices == 1 ? " slice differs." : " slices differ.");
            return p;
        }

        private static MeasureNumbersPair NotChecked(MeasureNumbersPair p, string why)
        {
            p.Verdict = "notChecked"; p.Reason = "Not checked: " + why + ".";
            return p;
        }

        public static string Overall(IReadOnlyCollection<MeasureNumbersPair> pairs)
        {
            if (pairs.Any(p => p.Verdict == "differs")) return "differs";
            if (pairs.Count == 0 || pairs.Any(p => p.Verdict != "same")) return "notChecked";
            return "same";
        }

        public static string Sentence(string verdict, IReadOnlyCollection<MeasureNumbersPair> pairs)
        {
            var differ = pairs.Count(p => p.Verdict == "differs"); var notChecked = pairs.Count(p => p.Verdict == "notChecked");
            switch (verdict)
            {
                case "same": return pairs.Count == 1 ? "Same numbers. " + pairs.First().Reason : "Same numbers for all " + pairs.Count + " measures.";
                case "differs": return (differ == 1 ? "The new measure gives different numbers than the old one." : differ + " new measures give different numbers than their old ones.") + " The slices that differ are listed.";
                default: return notChecked == 0 ? "Nothing was compared." : notChecked + (notChecked == 1 ? " pair was" : " pairs were") + " not checked. That is not a pass.";
            }
        }
    }
}
