using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Engine
{
    /// <summary>
    /// The calendar plan (1.7): Semanticus reads the Date table, what each column holds, the fiscal start in the model's AI
    /// instructions and the measures written the old way, then offers ONE plan. propose_calendar is a pure read; setup_calendars
    /// does the whole plan as ONE undo step (raise the model version, add columns, make calendars, move the old measures);
    /// add_time_measures adds the time measures a calendar makes possible. The page and the assistant call the same three.
    /// </summary>
    public sealed partial class LocalEngine
    {
        private sealed class CalKindDef
        {
            public string Kind, Template, Name, Hint;
        }

        private static readonly CalKindDef[] CalendarKinds =
        {
            new CalKindDef { Kind = "year", Template = "gregorian", Name = "Calendar year", Hint = "January to December" },
            new CalKindDef { Kind = "fiscal", Template = "fiscal", Name = "Fiscal year", Hint = "Starts in July" },
            new CalKindDef { Kind = "retail", Template = "445", Name = "Retail 4-4-5", Hint = "Quarters of 4, 4 and 5 weeks" },
            new CalKindDef { Kind = "iso", Template = "iso", Name = "ISO weeks", Hint = "Weeks start on Monday" },
            new CalKindDef { Kind = "p13", Template = "13period", Name = "13 periods", Hint = "13 periods of 4 weeks" },
        };

        private const string TimeMeasuresFolder = "Time intelligence";

        private static CalKindDef KindOf(string kind)
            => CalendarKinds.FirstOrDefault(k => string.Equals(k.Kind, kind, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"'{kind}' is not a calendar kind. Use year, fiscal, retail, iso or p13.");

        // ---- the level ladder ------------------------------------------------------------------------

        private sealed class PlannedLevel
        {
            public string Unit, Column, Also, SortBy, Expr, AlsoExpr;
            public bool IsNew, AlsoNew;
            public TOM.DataType Type, AlsoType;
        }

        private sealed class PlannedCal
        {
            public CalKindDef Def;
            public string Name, Hint;
            public List<PlannedLevel> Levels = new List<PlannedLevel>();
            public List<string> TimeRelated = new List<string>();
        }

        private static readonly string[] UnitOrder =
            { "Year", "Semester", "SemesterOfYear", "Quarter", "QuarterOfYear", "QuarterOfSemester", "Month", "MonthOfYear", "MonthOfSemester", "MonthOfQuarter",
              "Week", "WeekOfYear", "WeekOfSemester", "WeekOfQuarter", "WeekOfMonth", "Date", "DayOfYear", "DayOfSemester", "DayOfQuarter", "DayOfMonth", "DayOfWeek" };

        private static int UnitRank(string unit) { var i = Array.IndexOf(UnitOrder, unit); return i < 0 ? 99 : i; }

        private static string LevelLabel(string unit)
        {
            if (unit == "Date") return "Day";
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < unit.Length; i++)
            {
                if (i > 0 && char.IsUpper(unit[i])) sb.Append(' ').Append(char.ToLowerInvariant(unit[i]));
                else sb.Append(unit[i]);
            }
            return sb.ToString();
        }

        // Names a Date table commonly uses for what a template level needs, by template column. Matched without case, spaces,
        // dashes or underscores, and only when the column's type fits (a month NAME is text, a month NUMBER is a number).
        private static readonly Dictionary<string, (string[] Names, bool Text)> ColumnAliases = new Dictionary<string, (string[], bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Year"] = (new[] { "year", "calendaryear", "yr" }, false),
            ["Year Quarter"] = (new[] { "yearquarter", "quarteryear", "calendarquarter" }, true),
            ["Quarter of Year"] = (new[] { "quarter", "quartername", "quarterofyear", "qtr" }, true),
            ["Year Month"] = (new[] { "yearmonth", "monthyear", "calendarmonth" }, true),
            ["Month of Year"] = (new[] { "monthname", "month", "monthofyear" }, true),
            ["Month Number"] = (new[] { "monthnumber", "monthno", "monthnum", "monthsort", "monthofyearnumber" }, false),
            ["Day of Week"] = (new[] { "dayofweek", "weekday", "dayname", "weekdayname", "dayofweekname" }, true),
            ["Day of Week Number"] = (new[] { "dayofweeknumber", "weekdaynumber", "weekdayno", "dayofweekno" }, false),
            ["Day of Month"] = (new[] { "dayofmonth", "day", "daynumber" }, false),
        };

        private static string Norm(string s) => Regex.Replace(s ?? "", @"[\s_\-]", "").ToLowerInvariant();

        private static TOM.Column FindExistingColumn(TOM.Table tom, string templateName, ISet<string> taken)
        {
            if (tom == null) return null;
            var exact = tom.Columns.Find(templateName);
            if (exact != null) return taken.Contains(exact.Name) ? null : exact;
            if (!ColumnAliases.TryGetValue(templateName, out var a)) return null;
            foreach (var alias in a.Names)
            {
                var hit = tom.Columns.FirstOrDefault(c => !taken.Contains(c.Name) && Norm(c.Name) == alias
                    && (a.Text ? c.DataType == TOM.DataType.String
                               : c.DataType == TOM.DataType.Int64 || c.DataType == TOM.DataType.Double || c.DataType == TOM.DataType.Decimal
                                 || (templateName == "Year" && c.DataType == TOM.DataType.String)));
                if (hit != null) return hit;
            }
            return null;
        }

        private static readonly Regex TimeRelatedName = new Regex(@"holiday|working|weekend|business day|workday|is open|season", RegexOptions.IgnoreCase);

        /// <summary>One calendar as a ladder: the template's levels, with an existing column used wherever it already plays that part.</summary>
        private static PlannedCal PlanCalendar(CalKindDef def, TOM.Table tom, string dateColumn, int fiscalStart, CalendarLevelChoice[] choices, ISet<string> timeRelatedTaken)
        {
            var tp = BuildTemplate(def.Template, dateColumn, fiscalStart);
            var cal = new PlannedCal
            {
                Def = def, Name = def.Name,
                Hint = def.Kind == "fiscal" ? "Starts in " + CalendarMeasureMove.MonthName(fiscalStart) : def.Hint,
            };
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { dateColumn };
            var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [dateColumn] = dateColumn };
            cal.Levels.Add(new PlannedLevel { Unit = "Date", Column = dateColumn, Type = TOM.DataType.DateTime });

            foreach (var c in tp.Columns.Where(x => x.TimeUnit != null && !x.Associated))
            {
                var lvl = new PlannedLevel { Unit = c.TimeUnit, Column = c.Name, IsNew = true, Expr = c.Expression, Type = c.DataType };
                var have = FindExistingColumn(tom, c.Name, taken);
                if (have != null) { lvl.Column = have.Name; lvl.IsNew = false; lvl.Expr = null; lvl.Type = have.DataType; }
                taken.Add(lvl.Column); actual[c.Name] = lvl.Column;
                cal.Levels.Add(lvl);
            }
            foreach (var c in tp.Columns.Where(x => x.TimeUnit != null && x.Associated))
            {
                var lvl = cal.Levels.FirstOrDefault(l => l.Unit == c.TimeUnit);
                if (lvl == null) continue;
                var have = FindExistingColumn(tom, c.Name, taken);
                lvl.Also = have?.Name ?? c.Name; lvl.AlsoNew = have == null; lvl.AlsoExpr = have == null ? c.Expression : null; lvl.AlsoType = have?.DataType ?? c.DataType;
                taken.Add(lvl.Also); actual[c.Name] = lvl.Also;
            }
            foreach (var (col, by) in tp.SortBy)
            {
                if (!actual.TryGetValue(col, out var colName) || !actual.TryGetValue(by, out var byName)) continue;
                var lvl = cal.Levels.FirstOrDefault(l => l.Column == colName);
                if (lvl != null) lvl.SortBy = byName;
            }

            foreach (var ch in (choices ?? Array.Empty<CalendarLevelChoice>()).Where(x => string.Equals(x?.Calendar, def.Kind, StringComparison.OrdinalIgnoreCase)))
            {
                var lvl = cal.Levels.FirstOrDefault(l => string.Equals(l.Unit, ch.Unit, StringComparison.OrdinalIgnoreCase))
                          ?? throw new InvalidOperationException($"The {def.Name} calendar has no '{ch.Unit}' level. Its levels are {string.Join(", ", cal.Levels.Select(l => l.Unit))}.");
                if (string.IsNullOrWhiteSpace(ch.Column))
                {
                    if (lvl.Unit == "Date") throw new InvalidOperationException("The day level stays: every calendar needs its date column.");
                    cal.Levels.Remove(lvl);
                    continue;
                }
                var col = tom?.Columns.Find(ch.Column.Trim())
                          ?? throw new InvalidOperationException($"'{ch.Column}' is not a column on the Date table, so it cannot fill the {LevelLabel(lvl.Unit).ToLowerInvariant()} level.");
                lvl.Column = col.Name; lvl.IsNew = false; lvl.Expr = null; lvl.Type = col.DataType;
                lvl.Also = null; lvl.AlsoNew = false; lvl.AlsoExpr = null; lvl.SortBy = null;
            }

            cal.Levels = cal.Levels.OrderBy(l => UnitRank(l.Unit)).ToList();
            if (def.Kind == "year" && tom != null)
            {
                var used = new HashSet<string>(cal.Levels.SelectMany(l => new[] { l.Column, l.Also }).Where(x => x != null), StringComparer.OrdinalIgnoreCase);
                foreach (var c in tom.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber && !used.Contains(c.Name) && TimeRelatedName.IsMatch(c.Name)))
                    if (timeRelatedTaken.Add(c.Name)) cal.TimeRelated.Add(c.Name);
            }
            return cal;
        }

        private static CalendarLadder ToLadder(PlannedCal c) => new CalendarLadder
        {
            Kind = c.Def.Kind, Name = c.Name, Hint = c.Hint, Exists = false,
            Levels = c.Levels.Select(l => new CalendarLevel
            {
                Unit = l.Unit, Level = LevelLabel(l.Unit), Column = l.Column, IsNew = l.IsNew, AlsoColumn = l.Also, SortBy = l.SortBy,
            }).ToArray(),
            Other = c.TimeRelated.ToArray(),
        };

        private static CalendarLadder DescribeExisting(CalendarInfo info)
        {
            var kind = CalendarKinds.FirstOrDefault(k => string.Equals(k.Name, info.Name, StringComparison.OrdinalIgnoreCase));
            var levels = info.Groups.Where(g => g.TimeUnit != null && g.PrimaryColumn != null)
                .Select(g => new CalendarLevel
                {
                    Unit = g.TimeUnit, Level = LevelLabel(g.TimeUnit), Column = g.PrimaryColumn,
                    AlsoColumn = g.AssociatedColumns.FirstOrDefault(),
                })
                .OrderBy(l => UnitRank(l.Unit)).ToArray();
            return new CalendarLadder
            {
                Kind = kind?.Kind ?? "custom", Name = info.Name, Hint = kind == null ? null : kind.Kind == "fiscal" ? null : kind.Hint, Exists = true, Levels = levels,
                Other = info.Groups.Where(g => g.TimeUnit == null).SelectMany(g => g.TimeRelatedColumns).ToArray(),
            };
        }

        // ---- what Semanticus reads ---------------------------------------------------------------------

        private sealed class CalendarFacts
        {
            public string State;          // ready | no-date-table | set-up | live | unavailable
            public string Note;
            public int Level;
            public Table Date;            // null when the model has none
            public string DateColumn;
            public string[] OtherDates = Array.Empty<string>();
            public Column FactDate;       // no-date-table: the column the new table is cut from
            public int Fiscal = 7;
            public string FiscalClue;
            public bool FiscalFromModel;
        }

        private static bool UsesLiveSource(Model m)
        {
            foreach (var t in m.Tables)
                foreach (var p in CalendarOps.Raw(t).Partitions)
                    if (p.Source is TOM.MPartitionSource ms && ms.Expression != null
                        && ms.Expression.IndexOf("AnalysisServices.Database", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            return false;
        }

        private static readonly string[] DateTableNames = { "date", "dates", "calendar", "dimdate", "dim date", "datedim", "date dim", "dim_date" };

        private static CalendarFacts ReadCalendarFacts(Model m, string requestedTable, int requestedFiscal)
        {
            var f = new CalendarFacts { Level = m.Database?.CompatibilityLevel ?? 0 };
            if (UsesLiveSource(m))
            {
                f.State = "live";
                f.Note = "This model reads another Power BI model live, and Power BI does not allow calendars on a live connection. Set them up on the model it reads from.";
                return f;
            }

            Table date = null;
            if (!string.IsNullOrWhiteSpace(requestedTable))
            {
                date = ResolveTable(m, requestedTable) ?? throw new InvalidOperationException($"'{requestedTable}' is not a table in this model. Run list_objects to see the tables.");
                if (!date.Columns.Any(c => c.DataType == DataType.DateTime))
                    throw new InvalidOperationException($"'{date.Name}' has no date column, so it cannot be the Date table.");
            }
            else
            {
                var manySide = new HashSet<Table>(m.Relationships.OfType<SingleColumnRelationship>().Select(r => r.FromTable));
                var oneSide = m.Relationships.OfType<SingleColumnRelationship>()
                    .Where(r => r.ToColumn?.DataType == DataType.DateTime && !manySide.Contains(r.ToTable)).Select(r => r.ToTable).Distinct().ToList();
                var withDates = m.Tables.Where(t => t.Columns.Any(c => c.DataType == DataType.DateTime)).ToList();
                var ranked = withDates
                    .OrderByDescending(t => CalendarOps.Raw(t).Calendars.Count > 0 ? 1 : 0)
                    .ThenByDescending(t => string.Equals(t.DataCategory, "Time", StringComparison.Ordinal) ? 1 : 0)
                    .ThenByDescending(t => oneSide.Contains(t) ? 1 : 0)
                    .ThenByDescending(t => DateTableNames.Contains(t.Name.ToLowerInvariant()) ? 1 : 0)
                    .ToList();
                var best = ranked.FirstOrDefault();
                var strong = best != null && (CalendarOps.Raw(best).Calendars.Count > 0 || string.Equals(best.DataCategory, "Time", StringComparison.Ordinal)
                                              || oneSide.Contains(best) || DateTableNames.Contains(best.Name.ToLowerInvariant()));
                if (strong)
                {
                    date = best;
                    f.OtherDates = ranked.Skip(1).Where(t => CalendarOps.Raw(t).Calendars.Count > 0 || string.Equals(t.DataCategory, "Time", StringComparison.Ordinal)
                                                             || oneSide.Contains(t) || DateTableNames.Contains(t.Name.ToLowerInvariant())).Select(t => t.Name).ToArray();
                }
            }

            if (date == null)
            {
                var facts = m.Tables.Where(t => !(t is CalculatedTable))
                    .SelectMany(t => t.Columns.Where(c => c.DataType == DataType.DateTime)).ToList();
                f.FactDate = facts.FirstOrDefault(c => Regex.IsMatch(c.Name, @"order.?date", RegexOptions.IgnoreCase))
                             ?? facts.FirstOrDefault(c => Regex.IsMatch(c.Name, @"date", RegexOptions.IgnoreCase)) ?? facts.FirstOrDefault();
                if (f.FactDate == null)
                {
                    f.State = "unavailable";
                    f.Note = "This model has no date column, so there is nothing to build a Date table from. Add a date column at the source first.";
                    return f;
                }
                f.State = "no-date-table";
                f.DateColumn = "Date";
            }
            else
            {
                f.Date = date;
                var cols = date.Columns.Where(c => c.DataType == DataType.DateTime).ToList();
                var key = cols.FirstOrDefault(c => c.IsKey)
                          ?? cols.FirstOrDefault(c => string.Equals(c.Name, "Date", StringComparison.OrdinalIgnoreCase))
                          ?? m.Relationships.OfType<SingleColumnRelationship>().Where(r => ReferenceEquals(r.ToTable, date) && r.ToColumn?.DataType == DataType.DateTime).Select(r => r.ToColumn).FirstOrDefault()
                          ?? cols.FirstOrDefault(c => Regex.IsMatch(c.Name, @"date", RegexOptions.IgnoreCase))
                          ?? cols[0];
                f.DateColumn = key.Name;
                f.State = CalendarOps.Raw(date).Calendars.Count > 0 ? "set-up" : "ready";
            }

            // The fiscal start: what the model's own instructions say, then the year ends of its old measures, then July.
            var (clueMonth, clueSentence) = FiscalFromInstructions(m);
            if (clueMonth != null) { f.Fiscal = clueMonth.Value; f.FiscalClue = clueSentence; f.FiscalFromModel = true; }
            else if (f.Date != null)
            {
                foreach (var me in m.AllMeasures)
                {
                    var start = YearEndStartOf(me.Expression, f.Date.Name, f.DateColumn);
                    if (start != null && start != 1)
                    {
                        f.Fiscal = start.Value;
                        f.FiscalClue = "Your measures end the year in " + CalendarMeasureMove.MonthName(start.Value == 1 ? 12 : start.Value - 1) + ", so the fiscal year starts in " + CalendarMeasureMove.MonthName(start.Value) + ".";
                        f.FiscalFromModel = true;
                        break;
                    }
                }
            }
            if (requestedFiscal >= 1 && requestedFiscal <= 12)
            {
                if (requestedFiscal != f.Fiscal) f.FiscalClue = null;   // the person chose a month: the clue no longer describes the plan
                f.Fiscal = requestedFiscal;
            }
            return f;
        }

        private static int? YearEndStartOf(string dax, string table, string column)
        {
            if (string.IsNullOrWhiteSpace(dax)) return null;
            var toks = DaxScan.Tokenize(dax);
            foreach (var fn in new[] { "TOTALYTD", "DATESYTD" })
                foreach (var open in DaxScan.AllCallOpens(toks, fn))
                {
                    var args = DaxScan.ArgRanges(toks, open);
                    var last = args.Count > 0 ? args[args.Count - 1] : (start: 0, end: 0);
                    if (args.Count >= (fn == "TOTALYTD" ? 3 : 2) && last.end - last.start == 1 && toks[last.start].Kind == DaxScan.Kind.String)
                    {
                        var start = CalendarMeasureMove.StartMonthFromYearEnd(toks[last.start].Inner);
                        if (start != null) return start;
                    }
                }
            return null;
        }

        private static readonly string MonthPattern = "(january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept|sep|oct|nov|dec)";

        private static (int? Month, string Sentence) FiscalFromInstructions(Model m)
        {
            string text = null;
            try { text = ReadCustomInstructions(FindLinguisticCulture(m, null)?.Content); } catch { /* no schema: no clue */ }
            if (string.IsNullOrWhiteSpace(text)) return (null, null);
            var lead = @"(?:fiscal|financial|tax|accounting)\s+year|\bFY\b";
            var day = @"(?:\s+(?:on|in|from|at))?\s*(?:the\s+)?(?:\d{1,2}(?:st|nd|rd|th)?\s+(?:of\s+)?)?";
            var starts = Regex.Match(text, "(?:" + lead + @")\s+(?:starts?|begins?|commences?|runs?\s+from)" + day + MonthPattern, RegexOptions.IgnoreCase);
            if (starts.Success)
            {
                var month = MonthIndex(starts.Groups[1].Value);
                if (month != null) return (month, "Your instructions say the fiscal year starts in " + CalendarMeasureMove.MonthName(month.Value) + ".");
            }
            var ends = Regex.Match(text, "(?:" + lead + @")\s+(?:ends?|finishes?)" + day + MonthPattern, RegexOptions.IgnoreCase);
            if (ends.Success)
            {
                var end = MonthIndex(ends.Groups[1].Value);
                if (end != null)
                {
                    var month = end.Value % 12 + 1;
                    return (month, "Your instructions say the fiscal year ends in " + CalendarMeasureMove.MonthName(end.Value) + ", so it starts in " + CalendarMeasureMove.MonthName(month) + ".");
                }
            }
            return (null, null);
        }

        private static int? MonthIndex(string word)
        {
            var w = (word ?? "").Trim().ToLowerInvariant();
            for (var i = 1; i <= 12; i++)
                if (CalendarMeasureMove.MonthName(i).ToLowerInvariant().StartsWith(w.Length >= 3 ? w.Substring(0, 3) : w)) return i;
            return null;
        }

        // ---- the read ---------------------------------------------------------------------------------------

        public Task<CalendarProposal> ProposeCalendarAsync(string dateTable, string[] calendars, int fiscalStartMonth, CalendarLevelChoice[] levels)
        {
            var s = _sessions.Require();
            var canCompare = _live != null;
            return s.ReadAsync(m =>
            {
                var p = BuildCalendarProposal(m, dateTable, calendars, fiscalStartMonth, levels, out _);
                // Setup moves a measure only after it compares the numbers on a live model. With none connected nothing moves, so the card
                // must not say "1 measure moves": every candidate is listed as staying, with the reason.
                if (!canCompare && p.Moves.Length > 0)
                {
                    p.Stays = p.Stays.Concat(p.Moves.Select(mv => new MeasureStay { Measure = mv.Measure, Why = NoModelToCompareReason })).ToArray();
                    p.Moves = Array.Empty<MeasureMove>();
                }
                return p;
            });
        }

        internal const string NoModelToCompareReason = "Semanticus moves a measure only after it compares its numbers on the published model, and none is connected, so it stays as it is.";

        private static string[] DefaultKinds(CalendarFacts f, string[] requested)
        {
            if (requested != null && requested.Length > 0)
            {
                var wanted = requested.Select(k => KindOf(k).Kind).Distinct().ToList();
                if (!wanted.Contains("year")) wanted.Insert(0, "year");
                return CalendarKinds.Select(k => k.Kind).Where(wanted.Contains).ToArray();
            }
            // A model that already has calendars plans nothing it was not asked for: the page offers the missing ones as choices.
            if (f.State == "set-up") return Array.Empty<string>();
            return f.FiscalFromModel ? new[] { "year", "fiscal" } : new[] { "year" };
        }

        private static CalendarProposal BuildCalendarProposal(Model m, string dateTable, string[] calendars, int fiscalStartMonth, CalendarLevelChoice[] levels, out List<PlannedCal> planned)
        {
            planned = new List<PlannedCal>();
            var f = ReadCalendarFacts(m, dateTable, fiscalStartMonth);
            var p = new CalendarProposal
            {
                State = f.State, Note = f.Note, CompatibilityLevel = f.Level, WillRaiseLevel = f.Level < CalendarOps.MinCompatibilityLevel,
                DateTable = f.Date?.Name ?? (f.State == "no-date-table" ? "Date" : null), DateColumn = f.DateColumn, OtherDateTables = f.OtherDates,
                FiscalStartMonth = f.Fiscal, FiscalClue = f.FiscalClue,
            };
            if (f.State == "live" || f.State == "unavailable") return p;
            if (f.FactDate != null) p.DateSource = $"{f.FactDate.Table.Name}[{f.FactDate.Name}]";

            var existing = f.Date == null ? new List<CalendarInfo>() : CalendarOps.Raw(f.Date).Calendars.Select(c => DescribeCalendar(f.Date.Name, c)).ToList();
            var existingKinds = existing.Select(e => CalendarKinds.FirstOrDefault(k => string.Equals(k.Name, e.Name, StringComparison.OrdinalIgnoreCase))?.Kind).Where(k => k != null).ToHashSet();
            var kinds = DefaultKinds(f, calendars).Where(k => !existingKinds.Contains(k)).ToArray();

            var tom = f.Date == null ? null : CalendarOps.Raw(f.Date);
            var trTaken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in kinds) planned.Add(PlanCalendar(KindOf(k), tom, f.DateColumn, f.Fiscal, levels, trTaken));

            p.Calendars = existing.Select(DescribeExisting).Concat(planned.Select(ToLadder)).ToArray();
            p.Choices = CalendarKinds.Select(k =>
            {
                var on = existingKinds.Contains(k.Kind) || kinds.Contains(k.Kind);
                return new CalendarKindChoice
                {
                    Kind = k.Kind, Name = k.Name, Short = k.Kind == "fiscal" ? "Fiscal, " + CalendarMeasureMove.MonthName(f.Fiscal) : k.Name,
                    Hint = k.Kind == "fiscal" ? "Starts in " + CalendarMeasureMove.MonthName(f.Fiscal) : k.Hint,
                    On = on, Locked = k.Kind == "year" && !existingKinds.Contains("year"), Exists = existingKinds.Contains(k.Kind),
                };
            }).ToArray();
            p.NewColumns = planned.SelectMany(c => c.Levels.SelectMany(l => new[] { l.IsNew ? l.Column : null, l.AlsoNew ? l.Also : null }))
                .Where(x => x != null).Distinct().ToArray();
            if (f.State == "no-date-table") p.NewColumns = new[] { f.DateColumn }.Concat(p.NewColumns).ToArray();

            // Candidate rewrites. Setup compares their daily numbers before applying them; a matching year end selects fiscal.
            var yearName = KindOf("year").Name;
            var fiscalName = kinds.Contains("fiscal") || existingKinds.Contains("fiscal") ? KindOf("fiscal").Name : null;
            var moves = new List<MeasureMove>(); var stays = new List<MeasureStay>();
            if (f.Date != null && (kinds.Contains("year") || existingKinds.Contains("year")))
            {
                foreach (var me in m.AllMeasures.OrderBy(x => x.Table?.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(me.Expression)) continue;
                    var o = CalendarMeasureMove.Rewrite(me.Expression, f.Date.Name, f.DateColumn, yearName, fiscalName, f.Fiscal);
                    if (o.After != null)
                        moves.Add(new MeasureMove
                        {
                            Measure = me.Name, Table = me.Table?.Name, Ref = ObjectRefs.For(me), Before = me.Expression, After = o.After,
                            Kind = o.Kind, Calendar = KindOf(o.Kind).Name,
                        });
                    else if (o.Why != null) stays.Add(new MeasureStay { Measure = me.Name, Why = o.Why });
                    else if (o.Rolling && UsesDateColumn(me.Expression, f.DateColumn))
                        stays.Add(new MeasureStay { Measure = me.Name, Why = "A rolling window works the same either way." });
                }
            }
            p.Moves = moves.ToArray(); p.Stays = stays.ToArray();

            var present = existingKinds.Concat(kinds).ToHashSet();
            p.TimeMeasures = ProposeTimeMeasures(m, present).ToArray();
            return p;
        }

        private static bool UsesDateColumn(string dax, string column)
            => DaxScan.Tokenize(dax).Any(t => t.Kind == DaxScan.Kind.Name && t.Delim == '[' && string.Equals(t.Inner, column, StringComparison.OrdinalIgnoreCase));

        // ---- the time measures a calendar makes possible --------------------------------------------------------

        private static Measure FindBaseMeasure(Model m)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var me in m.AllMeasures)
            {
                if (string.IsNullOrWhiteSpace(me.Expression)) continue;
                var toks = DaxScan.Tokenize(me.Expression);
                foreach (var fn in new[] { "TOTALYTD", "TOTALQTD", "TOTALMTD", "CALCULATE" })
                    foreach (var open in DaxScan.AllCallOpens(toks, fn))
                    {
                        var args = DaxScan.ArgRanges(toks, open);
                        if (args.Count == 0 || args[0].end - args[0].start != 1) continue;
                        var t = toks[args[0].start];
                        if (t.Kind != DaxScan.Kind.Name || t.Delim != '[') continue;
                        if (fn == "CALCULATE" && !DaxScan.CallsFunction(toks, "SAMEPERIODLASTYEAR") && !DaxScan.CallsFunction(toks, "DATEADD")) continue;
                        counts[t.Inner] = counts.GetValueOrDefault(t.Inner) + 1;
                    }
            }
            foreach (var name in counts.OrderByDescending(x => x.Value).Select(x => x.Key))
            {
                var hit = m.AllMeasures.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return null;
        }

        private static bool HasForm(Model m, string fn, string calendarName, string baseName)
        {
            foreach (var me in m.AllMeasures)
            {
                if (string.IsNullOrWhiteSpace(me.Expression)) continue;
                var toks = DaxScan.Tokenize(me.Expression);
                if (!toks.Any(t => t.Kind == DaxScan.Kind.Name && t.Delim == '\'' && string.Equals(t.Inner, calendarName, StringComparison.OrdinalIgnoreCase))) continue;
                if (!DaxScan.CallsFunction(toks, fn)) continue;
                if (toks.Any(t => t.Kind == DaxScan.Kind.Name && t.Delim == '[' && string.Equals(t.Inner, baseName, StringComparison.OrdinalIgnoreCase))) return true;
            }
            return false;
        }

        private static List<TimeMeasureProposal> ProposeTimeMeasures(Model m, ISet<string> kinds)
        {
            var list = new List<TimeMeasureProposal>();
            var b = FindBaseMeasure(m);
            if (b == null || b.Table == null) return list;
            var bn = b.Name; var bref = "[" + bn.Replace("]", "]]") + "]";
            void Add(string suffix, string fn, string kind, string dax, bool dedupe = true)
            {
                var cal = KindOf(kind).Name;
                var name = bn + " " + suffix;
                if (m.AllMeasures.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return;
                if (dedupe && HasForm(m, fn, cal, bn)) return;
                list.Add(new TimeMeasureProposal { Name = name, Table = b.Table.Name, Dax = dax, Kind = kind, Calendar = cal, Base = bn });
            }
            string Q(string kind) => "'" + KindOf(kind).Name + "'";
            if (kinds.Contains("year"))
            {
                Add("YTD", "TOTALYTD", "year", $"TOTALYTD ( {bref}, {Q("year")} )");
                Add("QTD", "TOTALQTD", "year", $"TOTALQTD ( {bref}, {Q("year")} )");
                Add("MTD", "TOTALMTD", "year", $"TOTALMTD ( {bref}, {Q("year")} )");
                Add("Last Year", "SAMEPERIODLASTYEAR", "year", $"CALCULATE ( {bref}, SAMEPERIODLASTYEAR ( {Q("year")} ) )");
                Add("vs Last Year %", "SAMEPERIODLASTYEAR", "year",
                    $"VAR cur = {bref} VAR py = CALCULATE ( {bref}, SAMEPERIODLASTYEAR ( {Q("year")} ) ) RETURN DIVIDE ( cur - py, py )", dedupe: false);
            }
            if (kinds.Contains("fiscal"))
            {
                Add("Fiscal YTD", "TOTALYTD", "fiscal", $"TOTALYTD ( {bref}, {Q("fiscal")} )");
                Add("Fiscal QTD", "TOTALQTD", "fiscal", $"TOTALQTD ( {bref}, {Q("fiscal")} )");
            }
            var week = new[] { "iso", "retail", "p13" }.FirstOrDefault(kinds.Contains);
            if (week != null)
            {
                Add("WTD", "TOTALWTD", week, $"TOTALWTD ( {bref}, {Q(week)} )");
                Add("Same Week Last Year", "DATEADD", week, $"CALCULATE ( {bref}, DATEADD ( {Q(week)}, -1, YEAR ) )");
            }
            if (kinds.Contains("retail")) Add("Retail Period to Date", "TOTALMTD", "retail", $"TOTALMTD ( {bref}, {Q("retail")} )");
            if (kinds.Contains("p13")) Add("Period to Date", "TOTALMTD", "p13", $"TOTALMTD ( {bref}, {Q("p13")} )");
            return list;
        }

        // ---- the write ----------------------------------------------------------------------------------------

        public async Task<CalendarSetupResult> SetupCalendarsAsync(string dateTable, string[] calendars, int fiscalStartMonth, CalendarLevelChoice[] levels, bool moveMeasures, string origin)
        {
            var s = _sessions.Require();
            var result = new CalendarSetupResult();
            var moved = new List<MeasureMove>();
            string tableName = null;
            // Read and compare before the undo batch. No query may approve a change that has already happened.
            var input = await s.ReadAsync(m =>
            {
                var proposal = BuildCalendarProposal(m, dateTable, calendars, fiscalStartMonth, levels, out _);
                var f = ReadCalendarFacts(m, dateTable, fiscalStartMonth);
                var day = f.Date == null ? null : CalendarDataChecks.Quote(f.Date.Name, f.DateColumn);
                var date = f.Date?.Columns[f.DateColumn];
                var facts = m.Relationships.OfType<SingleColumnRelationship>()
                    .Select(r => date != null && ReferenceEquals(r.ToColumn, date) ? r.FromColumn
                        : date != null && ReferenceEquals(r.FromColumn, date) ? r.ToColumn : null)
                    .Where(c => c != null && c.DataType == DataType.DateTime && !ReferenceEquals(c.Table, f.Date))
                    .Select(c => CalendarDataChecks.Quote(c.Table.Name, c.Name)).Distinct().ToArray();
                return (proposal, day, facts, revision: s.Revision);
            });
            var live = _live;
            var pairs = moveMeasures ? await CheckMovedNumbersAsync(input.proposal.Moves, input.day, input.facts, live)
                : new List<MeasureNumbersPair>();
            var proved = input.proposal.Moves.Where((mv, i) => i < pairs.Count && pairs[i].Verdict == "same")
                .ToDictionary(mv => mv.Ref);
            var proofCurrent = false;
            var rev = await s.MutateAsync(origin, "set up calendars", m =>
            {
                proofCurrent = s.Revision == input.revision && ReferenceEquals(live, _live);
                var proposal = BuildCalendarProposal(m, dateTable, calendars, fiscalStartMonth, levels, out var planned);
                if (proposal.State == "live" || proposal.State == "unavailable") throw new InvalidOperationException(proposal.Note);
                var f = ReadCalendarFacts(m, dateTable, fiscalStartMonth);
                result.FromLevel = f.Level;
                if (f.Level < CalendarOps.MinCompatibilityLevel) ApplyCompatibilityLevel(m, CalendarOps.MinCompatibilityLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
                result.ToLevel = m.Database?.CompatibilityLevel ?? f.Level;

                var table = f.Date;
                if (table == null)
                {
                    table = CreateDateTableForCalendars(m, f);
                    result.CreatedTable = true;
                }
                tableName = table.Name;

                var added = new List<string>(); var made = new List<string>();
                if (planned.Count > 0)
                {
                    foreach (var pc in planned)
                        if (m.Tables.Any(t => CalendarOps.Raw(t).Calendars.ContainsName(pc.Name)))
                            throw new InvalidOperationException($"The model already has a calendar named '{pc.Name}'.");
                    CalendarOps.Mutate(m, table, tom =>
                    {
                        foreach (var pc in planned)
                        {
                            ApplyPlannedCalendar(tom, pc, added);
                            made.Add(pc.Name);
                        }
                    });
                }
                result.ColumnsAdded = added.ToArray();
                result.Calendars = made.ToArray();

                var stays = proposal.Stays.ToList();
                if (moveMeasures)
                    foreach (var mv in proposal.Moves)
                    {
                        if (!proofCurrent || !proved.TryGetValue(mv.Ref, out var checkedMove)
                            || checkedMove.Before != mv.Before || checkedMove.After != mv.After)
                        {
                            stays.Add(new MeasureStay { Measure = mv.Measure, Why = live == null ? NoModelToCompareReason : CalendarMeasureMove.UnprovedReason });
                            continue;
                        }
                        if (!(ObjectRefs.Resolve(m, mv.Ref) is Measure me)) continue;
                        me.Expression = mv.After;
                        moved.Add(mv);
                    }
                s.RecordWords(ChangeWords.For("set up calendars on", "tables", ObjectRefs.For(table)));
                result.Stays = stays.ToArray();
            });
            result.Revision = rev;
            result.DateTable = tableName;
            result.Moved = moved.ToArray();
            result.NumberCheck = proofCurrent && pairs.Count > 0 ? NumberCheckOf(pairs, new[] { input.day })
                : new CalendarNumberCheck { Status = "not-checked", Reason = CalendarMeasureMove.UnprovedReason };
            result.Note = (result.CreatedTable ? $"Made the {tableName} table. " : "")
                        + (result.FromLevel < CalendarOps.MinCompatibilityLevel ? $"The model went from version {result.FromLevel} to {result.ToLevel}; Undo puts it back, and publishing keeps it. " : "")
                        + "Calendars are a Power BI preview feature. Save the model to keep this change.";
            return result;
        }

        private static void ApplyPlannedCalendar(TOM.Table tom, PlannedCal pc, List<string> added)
        {
            void AddColumn(string name, string expr, TOM.DataType type)
            {
                if (tom.Columns.Find(name) != null) return;
                tom.Columns.Add(new TOM.CalculatedColumn
                {
                    Name = name, Expression = expr, DataType = type, SummarizeBy = TOM.AggregateFunction.None, LineageTag = Guid.NewGuid().ToString(),
                });
                added.Add(name);
            }
            foreach (var l in pc.Levels)
            {
                if (l.IsNew) AddColumn(l.Column, l.Expr, l.Type);
                if (l.AlsoNew) AddColumn(l.Also, l.AlsoExpr, l.AlsoType);
            }
            foreach (var l in pc.Levels.Where(x => x.SortBy != null))
            {
                var col = tom.Columns.Find(l.Column); var by = tom.Columns.Find(l.SortBy);
                if (col != null && by != null && col.SortByColumn == null) col.SortByColumn = by;
            }
            var specs = new List<CalendarMappingSpec>();
            foreach (var l in pc.Levels)
            {
                specs.Add(new CalendarMappingSpec { Column = l.Column, TimeUnit = l.Unit });
                if (l.Also != null) specs.Add(new CalendarMappingSpec { Column = l.Also, TimeUnit = l.Unit, Associated = true });
            }
            foreach (var c in pc.TimeRelated) specs.Add(new CalendarMappingSpec { Column = c, TimeUnit = "timeRelated" });
            var cal = new TOM.Calendar { Name = pc.Name, LineageTag = Guid.NewGuid().ToString() };
            ApplyMappings(tom, cal, WithoutSortByAssociations(tom, specs), new List<string>());
            tom.Calendars.Add(cal);
        }

        /// <summary>A model with no Date table gets one cut from its own order dates: a calculated table, linked to the dates it came from and marked.</summary>
        private static Table CreateDateTableForCalendars(Model m, CalendarFacts f)
        {
            const string name = "Date";
            if (m.Tables.Contains(name)) throw new InvalidOperationException("A table named 'Date' already exists, but it has no date column. Rename it, or tell your assistant which table holds the dates.");
            string Q(string s) => "'" + s.Replace("'", "''") + "'";
            var src = $"{Q(f.FactDate.Table.Name)}[{f.FactDate.Name}]";
            var expr = $"CALENDAR ( DATE ( YEAR ( MIN ( {src} ) ), 1, 1 ), DATE ( YEAR ( MAX ( {src} ) ), 12, 31 ) )";
            var ct = m.AddCalculatedTable(name, expr);
            var date = ct.AddCalculatedTableColumn("Date", "[Date]", null, DataType.DateTime);
            date.IsNameInferred = true;
            MarkDateTableCore(ct, date.Name);
            CreateRelationshipCore(m, f.FactDate, date, null, null);
            return ct;
        }

        // Compare both local expressions in one query, at day grain. A deployed measure can be older than the local expression.
        // Missing calendars, a missing leap-day comparison or no live model leaves the original expression in place.
        private async Task<List<MeasureNumbersPair>> CheckMovedNumbersAsync(IReadOnlyList<MeasureMove> candidates, string day,
            string[] facts, LiveConnection live)
        {
            var pairs = new List<MeasureNumbersPair>();
            foreach (var mv in candidates)
            {
                if (live == null || day == null)
                {
                    pairs.Add(MeasureNumbers.JudgeCalendarMove(mv.Measure, null, "No live model is open, so the numbers were not compared"));
                    continue;
                }
                var suffix = Guid.NewGuid().ToString("N");
                var before = "__semanticus_before_" + suffix; var after = "__semanticus_after_" + suffix;
                var table = "'" + (mv.Table ?? "").Replace("'", "''") + "'";
                var define = "DEFINE MEASURE " + table + "[" + before + "] = " + mv.Before + "\n"
                    + "MEASURE " + table + "[" + after + "] = " + mv.After + "\n";
                ResultSet rs;
                try { rs = await live.ExecuteAsync(define + MeasureNumbers.Query(before, after, new[] { day }, day, facts), 2, 120).ConfigureAwait(false); }
                catch (Exception ex) { pairs.Add(MeasureNumbers.JudgeCalendarMove(mv.Measure, null, ex.Message)); continue; }
                var cell = rs.Rows != null && rs.Rows.Length > 0 && rs.Rows[0] != null && rs.Rows[0].Length > 0 ? rs.Rows[0][0]?.ToString() : null;
                pairs.Add(MeasureNumbers.JudgeCalendarMove(mv.Measure, cell, string.IsNullOrEmpty(rs.Error) ? null : rs.Error));
            }
            return pairs;
        }

        /// <summary>The page's number check from the engine's judged pairs. Only a pair that was really compared gets a row; a pair that could not run
        /// is not a pass, and its raw query error stays out of the page.</summary>
        internal static CalendarNumberCheck NumberCheckOf(IReadOnlyList<MeasureNumbersPair> pairs, string[] by)
        {
            var verdict = MeasureNumbers.Overall(pairs);
            var names = by.Select(x => { var i = x.LastIndexOf('['); return i >= 0 ? x.Substring(i + 1).TrimEnd(']') : x; });
            var check = new CalendarNumberCheck
            {
                Status = verdict == "same" ? "same" : verdict == "differs" ? "differ" : "not-checked",
                Slice = "every " + string.Join(" and ", names),
                Rows = pairs.Where(p => p.Verdict != "notChecked").Select(p => new CalendarNumberRow
                {
                    Measure = p.Old, Same = p.Verdict == "same",
                    Before = p.Verdict == "differs" && p.Examples.Length > 0 ? p.Examples[0] : null,
                    After = p.Reason,
                }).ToArray(),
            };
            if (verdict == "notChecked")
                check.Reason = pairs.Any(p => (p.Reason ?? "").IndexOf("Cannot find table", StringComparison.OrdinalIgnoreCase) >= 0 || (p.Reason ?? "").IndexOf("calendar", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? "The connected model has no calendars yet, so the new form cannot run there. Ask your assistant to compare the numbers once the model is published."
                    : "The numbers were not compared: " + (pairs.FirstOrDefault(p => p.Verdict == "notChecked")?.Reason ?? "the check could not run").TrimStart().Replace("Not checked: ", "").Split('\n')[0];
            return check;
        }

        // ---- the time measures --------------------------------------------------------------------------------

        public async Task<AddTimeMeasuresResult> AddTimeMeasuresAsync(string[] measures, string origin)
        {
            var s = _sessions.Require();
            var created = new List<GeneratedObject>(); var skipped = new List<string>();
            var rev = await s.MutateAsync(origin, "add time measures", m =>
            {
                var present = new HashSet<string>(CalendarOps.Enumerate(m).Select(c => CalendarKinds.FirstOrDefault(k => string.Equals(k.Name, c.Calendar.Name, StringComparison.OrdinalIgnoreCase))?.Kind)
                    .Where(k => k != null));
                if (present.Count == 0) throw new InvalidOperationException("The model has no calendars yet. Set up calendars first, then add time measures.");
                var all = ProposeTimeMeasures(m, present);
                if (all.Count == 0) throw new InvalidOperationException("There is nothing to add: the calendars already have their time measures, or the model has no measure to build them from.");
                var wanted = measures == null || measures.Length == 0 ? all : all.Where(p => measures.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();
                foreach (var name in (measures ?? Array.Empty<string>()).Where(n => !all.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))))
                    skipped.Add(name + " (not a time measure the calendars can carry, or it exists)");
                foreach (var p in wanted)
                {
                    var table = m.Tables.FirstOrDefault(t => t.Name == p.Table);
                    var baseMeasure = m.AllMeasures.FirstOrDefault(x => x.Name == p.Base);
                    if (table == null || baseMeasure == null) { skipped.Add(p.Name + " (its table or base measure is gone)"); continue; }
                    var me = table.AddMeasure(p.Name, p.Dax, TimeMeasuresFolder);
                    me.FormatString = p.Name.EndsWith("%", StringComparison.Ordinal) ? "0.0%" : baseMeasure.FormatString;
                    Lineage.LineageGraph.MarkAddedOnPurpose(me, "a time measure of " + p.Base + " on the " + p.Calendar + " calendar");
                    created.Add(new GeneratedObject { Ref = ObjectRefs.For(me), Name = me.Name, Expression = p.Dax });
                }
            });
            return new AddTimeMeasuresResult { Revision = rev, Created = created.ToArray(), Skipped = skipped.ToArray(),
                Note = created.Count == 0 ? null : created.Count + (created.Count == 1 ? " time measure added." : " time measures added.") };
        }
    }
}
