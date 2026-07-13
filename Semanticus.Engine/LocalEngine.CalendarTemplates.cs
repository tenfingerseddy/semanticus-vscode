using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Engine
{
    /// <summary>
    /// define_calendar_from_template — the one-step modern replacement for generate_date_table: generates the
    /// template's calculated columns (only where absent; existing columns are kept and mapped as-is) AND the
    /// calendar with its TimeUnit mappings, as ONE undoable step. Targets an existing table with a date column,
    /// or creates a fresh CALENDAR() calculated table when the table doesn't exist. Every generated expression
    /// is self-contained over the date column (no cross-column references), so a partial pre-existing column
    /// set can never poison a generated one.
    /// </summary>
    public sealed partial class LocalEngine
    {
        public async Task<CalendarResult> DefineCalendarFromTemplateAsync(
            string template, string tableName, string dateColumn, int fiscalStartMonth,
            string startExpr, string endExpr, string calendarName, string origin)
        {
            var kind = NormalizeTemplate(template);
            if (fiscalStartMonth < 1 || fiscalStartMonth > 12)
                throw new ArgumentException($"fiscalStartMonth must be 1–12 (got {fiscalStartMonth}).");
            tableName = string.IsNullOrWhiteSpace(tableName) ? "Date" : tableName.Trim();

            var s = _sessions.Require();
            var created = new List<GeneratedObject>();
            var skipped = new List<string>();
            var mapped = new List<string>();
            string calName = null;
            var createdTable = false;

            var rev = await s.MutateAsync(origin, $"define {kind} calendar on {tableName}", m =>
            {
                RequireCalendarCl(m);

                var t = m.Tables.FirstOrDefault(x => x.Name == tableName);
                var isNew = t == null;
                var dateColName = isNew
                    ? (string.IsNullOrWhiteSpace(dateColumn) ? "Date" : dateColumn.Trim())
                    : ResolveDateColumnName(t, dateColumn, false);
                var plan = BuildTemplate(kind, dateColName, fiscalStartMonth);
                calName = string.IsNullOrWhiteSpace(calendarName) ? plan.CalendarName : calendarName.Trim();
                if (!isNew && CalendarOps.Raw(t).Calendars.ContainsName(calName))
                    throw new InvalidOperationException($"Table '{t.Name}' already has a calendar named '{calName}'. Pass a different calendarName, or delete_calendar first.");

                void ApplyTemplate(TOM.Table tom)
                {
                    if (isNew)
                    {
                        // CALENDAR() emits one column named Date; declare it eagerly so mappings can reference
                        // it offline (a processed model would infer the same metadata).
                        tom.Columns.Add(new TOM.CalculatedTableColumn
                        {
                            Name = dateColName, DataType = TOM.DataType.DateTime, IsNameInferred = true,
                            SourceColumn = $"[{dateColName}]", LineageTag = Guid.NewGuid().ToString(),
                        });
                    }

                    foreach (var c in plan.Columns)
                    {
                        if (tom.Columns.Find(c.Name) != null)
                        {
                            skipped.Add($"{c.Name} (column existed — kept its expression, mapped as-is)");
                            continue;
                        }
                        tom.Columns.Add(new TOM.CalculatedColumn
                        {
                            Name = c.Name, Expression = c.Expression, DataType = c.DataType,
                            SummarizeBy = TOM.AggregateFunction.None, LineageTag = Guid.NewGuid().ToString(),
                        });
                        created.Add(new GeneratedObject { Ref = $"column:{tom.Name}/{c.Name}", Name = c.Name, Expression = c.Expression });
                    }

                    // Visual sort pairing (month/day names by their numbers) — only when unset, never clobbering.
                    foreach (var (colName, byName) in plan.SortBy)
                    {
                        var col = tom.Columns.Find(colName); var by = tom.Columns.Find(byName);
                        if (col != null && by != null && col.SortByColumn == null) col.SortByColumn = by;
                    }

                    var cal = new TOM.Calendar { Name = calName, LineageTag = Guid.NewGuid().ToString() };
                    ApplyMappings(tom, cal, plan.Mappings(dateColName), mapped);
                    tom.Calendars.Add(cal);
                }

                if (isNew)
                {
                    var start = string.IsNullOrWhiteSpace(startExpr) ? "DATE(YEAR(TODAY())-5,1,1)" : startExpr.Trim();
                    var end = string.IsNullOrWhiteSpace(endExpr) ? "DATE(YEAR(TODAY()),12,31)" : endExpr.Trim();
                    CalendarOps.CreateCalculatedTable(m, tableName, $"CALENDAR({start}, {end})", ApplyTemplate);
                    createdTable = true;
                }
                else
                    CalendarOps.Mutate(m, t, ApplyTemplate);
            });

            return new CalendarResult
            {
                Revision = rev,
                Table = tableName,
                Calendar = calName,
                CreatedColumns = created.ToArray(),
                Mappings = mapped.ToArray(),
                Skipped = skipped.ToArray(),
                Note = (createdTable ? $"Created calculated table '{tableName}'. " : "")
                     + $"Calendar-aware DAX can target it: TOTALYTD(expr, '{calName}'). Generated columns materialize on deploy/process."
                     + (kind == "fiscal" ? $" Fiscal years are labeled by ENDING year (start month {fiscalStartMonth}: FY2025 spans {new DateTime(2024, fiscalStartMonth, 1):MMM yyyy}–{new DateTime(2025, fiscalStartMonth, 1).AddMonths(-1):MMM yyyy})." : "")
                     + " Persist with save_model.",
            };
        }

        private static string NormalizeTemplate(string template)
        {
            switch ((template ?? string.Empty).Trim().ToLowerInvariant().Replace("-", ""))
            {
                case "gregorian": return "gregorian";
                case "fiscal": return "fiscal";
                case "iso": return "iso";
                case "445": return "445";
                case "13period": case "13periods": return "13period";
                default:
                    throw new InvalidOperationException($"Unknown calendar template '{template}'. Use gregorian, fiscal, iso, 445, or 13period.");
            }
        }

        /// <summary>Pick the date column: explicit name, else 'Date', else the table's single DateTime column —
        /// ambiguity is an error listing the candidates, never a guess.</summary>
        private static string ResolveDateColumnName(Table t, string dateColumn, bool isNewTable)
        {
            if (isNewTable) return string.IsNullOrWhiteSpace(dateColumn) ? "Date" : dateColumn.Trim();
            var tom = CalendarOps.Raw(t);
            if (!string.IsNullOrWhiteSpace(dateColumn))
            {
                var name = dateColumn.Trim();
                if (tom.Columns.Find(name) == null)
                    throw new InvalidOperationException($"Table '{t.Name}' has no column named '{name}' — run list_columns on '{t.Name}' to see its columns, then pass an existing dateColumn.");
                return name;
            }
            if (tom.Columns.Find("Date") != null) return "Date";
            var dateCols = tom.Columns.Where(c => c.DataType == TOM.DataType.DateTime).Select(c => c.Name).ToArray();
            if (dateCols.Length == 1) return dateCols[0];
            throw new InvalidOperationException(dateCols.Length == 0
                ? $"Table '{t.Name}' has no DateTime column — pass dateColumn, or omit tableName to create a fresh date table."
                : $"Table '{t.Name}' has multiple DateTime columns ({string.Join(", ", dateCols)}) — pass dateColumn to pick one.");
        }

        // ---- Template definitions -------------------------------------------------------------------

        private sealed class TemplateColumn
        {
            public string Name; public string Expression; public TOM.DataType DataType;
            public string TimeUnit; public bool Associated;
        }

        private sealed class TemplatePlan
        {
            public string CalendarName;
            public TemplateColumn[] Columns;
            public (string col, string by)[] SortBy = Array.Empty<(string, string)>();
            /// <summary>Mapping order matters: primaries before their associated columns (ApplyMappings is loud otherwise).</summary>
            public IEnumerable<CalendarMappingSpec> Mappings(string dateColName)
            {
                yield return new CalendarMappingSpec { Column = dateColName, TimeUnit = "Date" };
                foreach (var c in Columns.Where(x => x.TimeUnit != null))
                    yield return new CalendarMappingSpec { Column = c.Name, TimeUnit = c.TimeUnit, Associated = c.Associated };
            }
        }

        private static TemplatePlan BuildTemplate(string kind, string dateColName, int fiscalStartMonth)
        {
            var d = $"[{dateColName}]";   // intra-table row reference — table-name-agnostic
            switch (kind)
            {
                case "gregorian":
                    return new TemplatePlan
                    {
                        CalendarName = "Gregorian",
                        Columns = new[]
                        {
                            Col("Year", $"YEAR({d})", TOM.DataType.Int64, "Year"),
                            Col("Year Quarter", $"YEAR({d}) & \"-Q\" & ROUNDUP(MONTH({d}) / 3, 0)", TOM.DataType.String, "Quarter"),
                            Col("Quarter of Year", $"\"Q\" & ROUNDUP(MONTH({d}) / 3, 0)", TOM.DataType.String, "QuarterOfYear"),
                            Col("Year Month", $"FORMAT({d}, \"yyyy-MM\")", TOM.DataType.String, "Month"),
                            Col("Month of Year", $"FORMAT({d}, \"mmmm\")", TOM.DataType.String, "MonthOfYear"),
                            Col("Month Number", $"MONTH({d})", TOM.DataType.Int64, "MonthOfYear", associated: true),
                            Col("Day of Week", $"FORMAT({d}, \"dddd\")", TOM.DataType.String, "DayOfWeek"),
                            Col("Day of Week Number", $"WEEKDAY({d}, 2)", TOM.DataType.Int64, "DayOfWeek", associated: true),
                            Col("Day of Month", $"DAY({d})", TOM.DataType.Int64, "DayOfMonth"),
                        },
                        SortBy = new[] { ("Month of Year", "Month Number"), ("Day of Week", "Day of Week Number") },
                    };

                case "fiscal":
                {
                    var s = fiscalStartMonth;
                    // Ending-year labels: FY2025 = Jul 2024 – Jun 2025 for start month 7. Start month 1 degenerates
                    // to the calendar year (no +1 — MONTH >= 1 is always true and would shift every label).
                    var fy = s == 1 ? $"YEAR({d})" : $"(YEAR({d}) + IF(MONTH({d}) >= {s}, 1, 0))";
                    var fmoy = $"(MOD(MONTH({d}) - {s}, 12) + 1)";
                    var fq = $"ROUNDUP({fmoy} / 3, 0)";
                    return new TemplatePlan
                    {
                        CalendarName = "Fiscal",
                        Columns = new[]
                        {
                            Col("Fiscal Year", $"\"FY\" & {fy}", TOM.DataType.String, "Year"),
                            Col("Fiscal Quarter", $"\"FY\" & {fy} & \" Q\" & {fq}", TOM.DataType.String, "Quarter"),
                            Col("Fiscal Quarter of Year", $"\"FQ\" & {fq}", TOM.DataType.String, "QuarterOfYear"),
                            Col("Fiscal Month", $"\"FY\" & {fy} & \" M\" & FORMAT({fmoy}, \"00\")", TOM.DataType.String, "Month"),
                            Col("Fiscal Month of Year", fmoy, TOM.DataType.Int64, "MonthOfYear"),
                        },
                    };
                }

                case "iso":
                    return new TemplatePlan
                    {
                        CalendarName = "ISO",
                        Columns = IsoColumns(d).Concat(new[]
                        {
                            Col("ISO Day of Week", $"WEEKDAY({d}, 2)", TOM.DataType.Int64, "DayOfWeek"),
                        }).ToArray(),
                    };

                case "445":
                {
                    // Weeks grouped 4-4-5 per quarter over the ISO week grid; week 53 folds into Q4/P12.
                    var vars445 = $"VAR wk = WEEKNUM({d}, 21) VAR q = MIN(ROUNDUP(wk / 13, 0), 4) VAR wq = wk - (q - 1) * 13 VAR p = (q - 1) * 3 + IF(wq <= 4, 1, IF(wq <= 8, 2, 3))";
                    return new TemplatePlan
                    {
                        CalendarName = "4-4-5",
                        Columns = IsoColumns(d).Concat(new[]
                        {
                            Col("445 Quarter", $"{vars445} RETURN {IsoYear(d)} & \" Q\" & q", TOM.DataType.String, "Quarter"),
                            Col("445 Quarter of Year", $"{vars445} RETURN \"Q\" & q", TOM.DataType.String, "QuarterOfYear"),
                            Col("445 Period", $"{vars445} RETURN {IsoYear(d)} & \" P\" & FORMAT(p, \"00\")", TOM.DataType.String, "Month"),
                            Col("445 Period of Year", $"{vars445} RETURN p", TOM.DataType.Int64, "MonthOfYear"),
                        }).ToArray(),
                    };
                }

                case "13period":
                {
                    var p = $"MIN(ROUNDUP(WEEKNUM({d}, 21) / 4, 0), 13)";   // 13 four-week periods; week 53 folds into P13
                    return new TemplatePlan
                    {
                        CalendarName = "13-Period",
                        Columns = IsoColumns(d).Concat(new[]
                        {
                            Col("13P Period", $"{IsoYear(d)} & \" P\" & FORMAT({p}, \"00\")", TOM.DataType.String, "Month"),
                            Col("13P Period of Year", p, TOM.DataType.Int64, "MonthOfYear"),
                        }).ToArray(),
                    };
                }

                default: throw new InvalidOperationException($"Unhandled template '{kind}'.");   // NormalizeTemplate guards this
            }
        }

        /// <summary>ISO year via the Thursday rule: the week's year is the year of its Thursday
        /// (WEEKDAY(d,3) is Monday-based 0–6, so d − WEEKDAY + 3 = that week's Thursday).</summary>
        private static string IsoYear(string d) => $"YEAR({d} + 3 - WEEKDAY({d}, 3))";

        /// <summary>The ISO week scaffolding shared by iso/445/13period — same column names on purpose, so the
        /// three week-based calendars coexist on one table mapping the same physical columns.</summary>
        private static TemplateColumn[] IsoColumns(string d) => new[]
        {
            Col("ISO Year", IsoYear(d), TOM.DataType.Int64, "Year"),
            Col("ISO Week", $"{IsoYear(d)} & \"-W\" & FORMAT(WEEKNUM({d}, 21), \"00\")", TOM.DataType.String, "Week"),
            Col("ISO Week of Year", $"WEEKNUM({d}, 21)", TOM.DataType.Int64, "WeekOfYear"),
        };

        private static TemplateColumn Col(string name, string expr, TOM.DataType type, string timeUnit, bool associated = false)
            => new TemplateColumn { Name = name, Expression = expr, DataType = type, TimeUnit = timeUnit, Associated = associated };
    }
}
