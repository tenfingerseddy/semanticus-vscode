using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Engine
{
    // The calendar data check and the number check (1.7 engine groundwork). The DAX they ask and the way they judge the answers are in
    // Testing/CalendarChecks.cs and run offline; this half reads the model for the shapes, runs the scalar queries on the live model and
    // says so plainly when it cannot: a degraded or skipped run is "not checked", never a pass.
    public sealed partial class LocalEngine
    {
        // ---- check_calendar_data ----------------------------------------------------------------

        /// <summary>Check the dates of the calendars (or marked date tables) against the dates in the facts, and the calendars themselves.
        /// <paramref name="tableRef"/> narrows it to one table; empty checks every calendar and date table. Reads only; needs a live model.</summary>
        public async Task<CalendarDataCheckResult> CheckCalendarDataAsync(string tableRef)
        {
            var s = _sessions.Require();
            var inputs = await s.ReadAsync(m => CollectCalendarInputs(m, tableRef)).ConfigureAwait(false);
            if (inputs.Count == 0)
                return NotCheckedCalendars("The model has no calendar and no marked date table to check" + (string.IsNullOrWhiteSpace(tableRef) ? "." : " in " + tableRef + "."));
            var live = _live;
            if (live == null)
                return NotCheckedCalendars("Not checked: the dates in your data can only be read from a live model. Connect to the model (Connections), then check again.");

            var tables = new List<CalendarDataTable>(); var findings = new List<CalendarDataFinding>();
            foreach (var i in inputs)
            {
                ResultSet rs;
                try { rs = await live.ExecuteAsync(CalendarDataChecks.RangeQuery(i), 2, 120).ConfigureAwait(false); }
                catch (Exception ex) { return NotCheckedCalendars("Not checked: the date query could not run (" + ex.Message + ")."); }
                if (!string.IsNullOrEmpty(rs.Error)) return NotCheckedCalendars("Not checked: the date query failed (" + rs.Error + ").");
                var range = ReadRange(rs, i.FactColumns.Length);
                if (range == null) return NotCheckedCalendars("Not checked: the date query did not return the dates.");
                tables.Add(new CalendarDataTable
                {
                    Table = i.Table, DateColumn = i.DateColumn, Calendars = i.Calendars.Select(c => c.Name).ToArray(), FactColumns = i.FactColumns,
                    CalendarFrom = Iso(range.CalendarFrom), CalendarTo = Iso(range.CalendarTo), DataFrom = Iso(range.DataFrom), DataTo = Iso(range.DataTo),
                });
                findings.AddRange(CalendarDataChecks.JudgeRange(i, range));

                foreach (var cal in i.Calendars)
                {
                    var (dax, labels) = CalendarDataChecks.StructureQuery(i.Table, cal);
                    if (dax == null) continue;
                    ResultSet st;
                    try { st = await live.ExecuteAsync(dax, 2, 120).ConfigureAwait(false); }
                    catch (Exception ex) { return NotCheckedCalendars("Not checked: the calendar '" + cal.Name + "' could not be read (" + ex.Message + ")."); }
                    if (!string.IsNullOrEmpty(st.Error)) return NotCheckedCalendars("Not checked: the calendar '" + cal.Name + "' could not be read (" + st.Error + ").");
                    findings.AddRange(CalendarDataChecks.JudgeStructure(cal.Name, labels, ReadCounts(st)));
                }
            }
            var arr = findings.ToArray();
            return new CalendarDataCheckResult
            {
                Status = arr.Length == 0 ? "ok" : "found", Tables = tables.ToArray(), Findings = arr,
                Sentence = CalendarDataChecks.Sentence(arr, tables.Count),
            };
        }

        private static CalendarDataCheckResult NotCheckedCalendars(string reason) =>
            new CalendarDataCheckResult { Status = "notChecked", Reason = reason, Sentence = reason };

        private static string Iso(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static object Cell(ResultSet rs, string name)
        {
            if (rs?.Rows == null || rs.Rows.Length == 0 || rs.Rows[0] == null) return null;
            for (var c = 0; c < rs.Columns.Length && c < rs.Rows[0].Length; c++)
                if (string.Equals((rs.Columns[c].Name ?? "").Trim('[', ']'), name, StringComparison.OrdinalIgnoreCase)) return rs.Rows[0][c];
            return null;
        }

        private static CalendarRange ReadRange(ResultSet rs, int factColumns)
        {
            if (rs.Rows == null || rs.Rows.Length == 0) return null;
            DateTime? D(string n) => TryTiDate(Cell(rs, n), out var d) ? d.Date : (DateTime?)null;
            long N(string n) { var v = ReconcileCoercion.Coerce(Cell(rs, n)); return v.Empty || v.Unsupported ? 0 : (long)v.Value; }
            var r = new CalendarRange { CalendarFrom = D("calMin"), CalendarTo = D("calMax"), Rows = N("rows"), Days = N("days") };
            for (var f = 0; f < factColumns; f++)
            {
                var lo = D("f" + f + "min"); var hi = D("f" + f + "max");
                if (lo != null && (r.DataFrom == null || lo < r.DataFrom)) r.DataFrom = lo;
                if (hi != null && (r.DataTo == null || hi > r.DataTo)) r.DataTo = hi;
            }
            return r;
        }

        private static Dictionary<string, long> ReadCounts(ResultSet rs)
        {
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            if (rs?.Rows == null || rs.Rows.Length == 0 || rs.Rows[0] == null) return counts;
            for (var c = 0; c < rs.Columns.Length && c < rs.Rows[0].Length; c++)
            {
                var v = ReconcileCoercion.Coerce(rs.Rows[0][c]);
                counts[(rs.Columns[c].Name ?? "").Trim('[', ']')] = v.Empty || v.Unsupported ? 0 : (long)v.Value;
            }
            return counts;
        }

        /// <summary>The date tables to check: every table with a calendar, every table marked as a date table, and any table that is one by its shape
        /// (a date column on the one side of a relationship). Pure model read.</summary>
        internal static List<CalendarCheckInput> CollectCalendarInputs(Model m, string tableRef)
        {
            Table only = null;
            if (!string.IsNullOrWhiteSpace(tableRef))
                only = ResolveTable(m, tableRef) ?? throw new InvalidOperationException($"{tableRef} is not a table in this model. Pass a table ref (a name or 'table:Name').");
            var inputs = new List<CalendarCheckInput>();
            foreach (var t in m.Tables)
            {
                if (only != null && !ReferenceEquals(t, only)) continue;
                var cals = CalendarOps.Raw(t).Calendars.ToList();
                var isDateTable = cals.Count > 0 || string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase) || Semanticus.Analysis.FormatFill.IsDateTableByStructure(t);
                if (!isDateTable) continue;
                var shapes = cals.Select(ShapeOf).ToArray();
                var dateCol = DateColumnOf(t, cals);
                if (dateCol == null) continue;
                var facts = m.Relationships.OfType<SingleColumnRelationship>()
                    .Select(r => ReferenceEquals(r.ToColumn, dateCol) ? r.FromColumn : ReferenceEquals(r.FromColumn, dateCol) ? r.ToColumn : null)
                    .Where(c => c != null && c.DataType == DataType.DateTime && !ReferenceEquals(c.Table, t))
                    .Select(c => CalendarDataChecks.Quote(c.Table.Name, c.Name)).Distinct(StringComparer.Ordinal).ToArray();
                inputs.Add(new CalendarCheckInput { Table = t.Name, DateColumn = dateCol.Name, FactColumns = facts, Calendars = shapes });
            }
            return inputs;
        }

        private static Column DateColumnOf(Table t, IList<TOM.Calendar> cals)
        {
            foreach (var cal in cals)
                foreach (var g in cal.CalendarColumnGroups.OfType<TOM.TimeUnitColumnAssociation>())
                    if (g.TimeUnit == TOM.TimeUnit.Date && g.PrimaryColumn != null && t.Columns.Contains(g.PrimaryColumn.Name))
                        return t.Columns[g.PrimaryColumn.Name];
            var dates = t.Columns.Where(c => c.DataType == DataType.DateTime && c.Type != ColumnType.RowNumber).ToList();
            return dates.FirstOrDefault(c => c.IsKey)
                ?? dates.FirstOrDefault(c => c.UsedInRelationships.OfType<SingleColumnRelationship>().Any(r => ReferenceEquals(r.ToColumn, c) || ReferenceEquals(r.FromColumn, c)))
                ?? dates.FirstOrDefault();
        }

        private static CalendarCheckShape ShapeOf(TOM.Calendar cal)
        {
            var levels = new List<(string Unit, string Column)>(); var pairs = new List<(string, string[])>();
            foreach (var g in cal.CalendarColumnGroups.OfType<TOM.TimeUnitColumnAssociation>())
            {
                if (g.PrimaryColumn == null) continue;
                var unit = g.TimeUnit.ToString();
                if (CalendarDataChecks.NestingRank(unit) >= 0) levels.Add((unit, g.PrimaryColumn.Name));
                var others = g.AssociatedColumns.Select(c => c.Name).Concat(g.PrimaryColumn.SortByColumn != null ? new[] { g.PrimaryColumn.SortByColumn.Name } : new string[0]).Distinct().ToArray();   // the sort-by column pairs with its primary too
                if (others.Length > 0) pairs.Add((g.PrimaryColumn.Name, others));
            }
            return new CalendarCheckShape
            {
                Name = cal.Name,
                Levels = levels.OrderBy(l => CalendarDataChecks.NestingRank(l.Unit)).ToArray(),
                Pairs = pairs.ToArray(),
            };
        }

        // ---- check_same_numbers -----------------------------------------------------------------

        /// <summary>Do each old measure and its new one (same position in the two lists) give the same numbers over a slice? The slice is the columns in
        /// <paramref name="by"/> ('Table'[Column] or column: refs); empty picks the date table's Year and Month. Needs a live model: without one the answer
        /// is "not checked", which is not a pass.</summary>
        public async Task<MeasureNumbersResult> CheckSameNumbersAsync(string[] oldMeasures, string[] newMeasures, string[] by)
        {
            oldMeasures ??= Array.Empty<string>(); newMeasures ??= Array.Empty<string>();
            if (oldMeasures.Length == 0 || oldMeasures.Length != newMeasures.Length)
                throw new ArgumentException("Give the old measures and the new measures as two lists of the same length: the first old one is compared with the first new one.");
            var s = _sessions.Require();
            var (olds, news, slice) = await s.ReadAsync(m =>
            {
                string Name(string r) => (ObjectRefs.Resolve(m, r) as Measure)?.Name
                    ?? m.AllMeasures.FirstOrDefault(x => string.Equals(x.Name, r, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? throw new InvalidOperationException($"{r} is not a measure in this model. Pass 'measure:Table/Name' or the measure's name; run list_measures to see them.");
                return (oldMeasures.Select(Name).ToArray(), newMeasures.Select(Name).ToArray(), ResolveSlice(m, by));
            }).ConfigureAwait(false);

            var live = _live;
            var pairs = new List<MeasureNumbersPair>();
            for (var n = 0; n < olds.Length; n++)
            {
                if (live == null)
                {
                    pairs.Add(MeasureNumbers.Judge(olds[n], news[n], null, "no live model is connected. Connect to the model (Connections), then check again"));
                    continue;
                }
                ResultSet rs;
                try { rs = await live.ExecuteAsync(MeasureNumbers.Query(olds[n], news[n], slice), 2, 120).ConfigureAwait(false); }
                catch (Exception ex) { pairs.Add(MeasureNumbers.Judge(olds[n], news[n], null, ex.Message)); continue; }
                var cell = rs.Rows != null && rs.Rows.Length > 0 && rs.Rows[0] != null && rs.Rows[0].Length > 0 ? rs.Rows[0][0]?.ToString() : null;
                pairs.Add(MeasureNumbers.Judge(olds[n], news[n], cell, string.IsNullOrEmpty(rs.Error) ? null : rs.Error));
            }
            var verdict = MeasureNumbers.Overall(pairs);
            return new MeasureNumbersResult { Verdict = verdict, By = slice, Pairs = pairs.ToArray(), Sentence = MeasureNumbers.Sentence(verdict, pairs) };
        }

        /// <summary>The columns to compare by: the ones the caller named, else the date table's Year and Month, else its date column. Pure model read.</summary>
        internal static string[] ResolveSlice(Model m, string[] by)
        {
            if (by != null && by.Length > 0)
                return by.Select(b =>
                {
                    if (b.TrimStart().StartsWith("'", StringComparison.Ordinal)) return b.Trim();
                    if (ObjectRefs.Resolve(m, b) is Column c) return CalendarDataChecks.Quote(c.Table.Name, c.Name);
                    throw new InvalidOperationException($"{b} is not a column. Pass 'column:Table/Column' or 'Table'[Column].");
                }).ToArray();

            var input = CollectCalendarInputs(m, null).FirstOrDefault();
            if (input == null) throw new InvalidOperationException("Say which columns to compare the numbers by: the model has no date table to pick them from.");
            var t = m.Tables[input.Table];
            Column Pick(params string[] names) => names.Select(n => t.Columns.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(c => c != null);
            var year = Pick("Year", "Calendar Year", "Year Number");
            var month = Pick("Month Number", "MonthNumber", "Month No", "Month", "Month Name");
            var picked = new[] { year, month }.Where(c => c != null).Select(c => CalendarDataChecks.Quote(t.Name, c.Name)).ToArray();
            return picked.Length == 2 ? picked : new[] { CalendarDataChecks.Quote(t.Name, input.DateColumn) };
        }
    }
}
