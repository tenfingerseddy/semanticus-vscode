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
    /// Calendar-based time intelligence (CL 1701+) — the modern replacement for the classic marked-date-table
    /// approach (docs/calendars-redesign-plan.md). The vendored TOMWrapper predates TOM's Calendar objects, so
    /// writes go through <see cref="CalendarOps.Mutate"/>: raw-TOM mutation wrapped in a BEFORE/AFTER table-TMDL
    /// undo action (the proven TmdlApplier mechanism) — one undoable step on the shared timeline, both doors.
    /// Reads are pure TOM (offline-tolerant; no INFO.* connection needed).
    /// </summary>
    public sealed partial class LocalEngine
    {
        // ---- Reads --------------------------------------------------------------------------------

        public async Task<CalendarListResult> ListCalendarsAsync(string tableRef)
        {
            var s = _sessions.Require();
            return await s.ReadAsync(m =>
            {
                var tables = string.IsNullOrWhiteSpace(tableRef)
                    ? m.Tables.ToArray()
                    : new[] { RequireCalendarTable(m, tableRef) };
                var cl = m.Database?.CompatibilityLevel ?? 0;
                var infos = new List<CalendarInfo>();
                foreach (var t in tables)
                {
                    foreach (var cal in CalendarOps.Raw(t).Calendars)
                        infos.Add(DescribeCalendar(t.Name, cal));
                }
                return new CalendarListResult
                {
                    Calendars = infos.ToArray(),
                    CompatibilityLevel = cl,
                    CalendarsSupported = cl >= CalendarOps.MinCompatibilityLevel,
                    Note = cl >= CalendarOps.MinCompatibilityLevel
                        ? (infos.Count == 0 ? "No calendars defined. define_calendar / define_calendar_from_template creates one." : null)
                        : $"Compatibility level {cl} < {CalendarOps.MinCompatibilityLevel}: calendars unavailable until set_compatibility_level({CalendarOps.MinCompatibilityLevel}).",
                };
            });
        }

        private static CalendarInfo DescribeCalendar(string tableName, TOM.Calendar cal)
        {
            var groups = new List<CalendarGroupInfo>();
            foreach (var g in cal.CalendarColumnGroups)
            {
                switch (g)
                {
                    case TOM.TimeUnitColumnAssociation a:
                        groups.Add(new CalendarGroupInfo
                        {
                            TimeUnit = a.TimeUnit.ToString(),
                            PrimaryColumn = a.PrimaryColumn?.Name,
                            AssociatedColumns = a.AssociatedColumns.Select(c => c.Name).ToArray(),
                        });
                        break;
                    case TOM.TimeRelatedColumnGroup r:
                        groups.Add(new CalendarGroupInfo
                        {
                            TimeRelatedColumns = r.Columns.Select(c => c.Name).ToArray(),
                        });
                        break;
                }
            }
            return new CalendarInfo { Table = tableName, Name = cal.Name, Description = cal.Description, Groups = groups.ToArray() };
        }

        /// <summary>Compact, model-wide per-calendar grounding lines (name + column→TimeUnit map) for get_grounding, so
        /// an agent authoring DAX knows it can write calendar-aware forms (TOTALYTD(expr,'Fiscal')) instead of classic
        /// ones. Model-wide because a calendar is referenced by NAME, independent of the object's own table. Pure TOM
        /// read via the CalendarOps raw seam (Calendars aren't wrapped); empty when no calendars exist.</summary>
        internal static string[] CalendarGroundingLines(Model m)
        {
            var lines = new List<string>();
            foreach (var t in m.Tables)
                foreach (var cal in CalendarOps.Raw(t).Calendars)
                {
                    var parts = new List<string>();
                    foreach (var g in cal.CalendarColumnGroups)
                        switch (g)
                        {
                            case TOM.TimeUnitColumnAssociation a when a.PrimaryColumn != null:
                                parts.Add($"{a.PrimaryColumn.Name}→{a.TimeUnit}"); break;
                            case TOM.TimeRelatedColumnGroup r:
                                foreach (var c in r.Columns) parts.Add($"{c.Name}→time-related"); break;
                        }
                    lines.Add($"'{cal.Name}' on [{t.Name}]: {string.Join(", ", parts)}");
                }
            return lines.ToArray();
        }

        // ---- Writes -------------------------------------------------------------------------------

        public async Task<CalendarResult> DefineCalendarAsync(string tableRef, string name, CalendarMappingSpec[] mappings, string description, string origin)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A calendar name is required.");
            if (mappings == null || mappings.Length == 0)
                throw new ArgumentException("At least one column mapping is required (e.g. {column:'Date', timeUnit:'Date'}).");
            var s = _sessions.Require();
            var mapped = new List<string>();
            string tableName = null;
            var rev = await s.MutateAsync(origin, $"define calendar {name}", m =>
            {
                RequireCalendarCl(m);
                var t = RequireCalendarTable(m, tableRef);
                tableName = t.Name;
                if (CalendarOps.Raw(t).Calendars.ContainsName(name))
                    throw new InvalidOperationException($"Table '{t.Name}' already has a calendar named '{name}'. Use tag_calendar_column to extend it, or delete_calendar first.");
                CalendarOps.Mutate(m, t, tom =>
                {
                    var cal = new TOM.Calendar { Name = name, LineageTag = Guid.NewGuid().ToString() };
                    if (!string.IsNullOrWhiteSpace(description)) cal.Description = description;
                    ApplyMappings(tom, cal, mappings, mapped);
                    tom.Calendars.Add(cal);
                });
            });
            return new CalendarResult
            {
                Revision = rev, Table = tableName, Calendar = name, Mappings = mapped.ToArray(),
                Note = "Calendar-aware DAX (e.g. TOTALYTD(expr, '" + name + "')) can now target this calendar. Persist with save_model.",
            };
        }

        public async Task<SetResult> DeleteCalendarAsync(string tableRef, string name, string origin)
        {
            var s = _sessions.Require();
            var changed = false;
            var rev = await s.MutateAsync(origin, $"delete calendar {name}", m =>
            {
                var t = RequireCalendarTable(m, tableRef);
                if (!CalendarOps.Raw(t).Calendars.ContainsName(name ?? string.Empty))
                    throw new InvalidOperationException($"Table '{t.Name}' has no calendar named '{name}'. list_calendars shows what exists.");
                CalendarOps.Mutate(m, t, tom => tom.Calendars.Remove(name));
                changed = true;
            });
            return new SetResult { Revision = rev, Changed = changed };
        }

        public async Task<CalendarResult> TagCalendarColumnAsync(string tableRef, string calendarName, string column, string timeUnit, bool associated, bool remove, string origin)
        {
            if (string.IsNullOrWhiteSpace(column)) throw new ArgumentException("A column name is required.");
            var s = _sessions.Require();
            var mapped = new List<string>();
            string tableName = null;
            var rev = await s.MutateAsync(origin, $"{(remove ? "untag" : "tag")} calendar column {column}", m =>
            {
                RequireCalendarCl(m);
                var t = RequireCalendarTable(m, tableRef);
                tableName = t.Name;
                if (!CalendarOps.Raw(t).Calendars.ContainsName(calendarName ?? string.Empty))
                    throw new InvalidOperationException($"Table '{t.Name}' has no calendar named '{calendarName}'. define_calendar creates one; list_calendars shows what exists.");
                CalendarOps.Mutate(m, t, tom =>
                {
                    var cal = tom.Calendars[calendarName];
                    if (remove) RemoveMapping(cal, RequireColumn(tom, column), mapped);
                    else ApplyMappings(tom, cal, new[] { new CalendarMappingSpec { Column = column, TimeUnit = timeUnit, Associated = associated } }, mapped);
                });
            });
            return new CalendarResult { Revision = rev, Table = tableName, Calendar = calendarName, Mappings = mapped.ToArray() };
        }

        // ---- Mapping mechanics ----------------------------------------------------------------------

        private static void RequireCalendarCl(Model m)
        {
            var cl = m.Database?.CompatibilityLevel ?? 0;
            if (cl < CalendarOps.MinCompatibilityLevel)
                throw new InvalidOperationException(
                    $"Calendars require compatibility level {CalendarOps.MinCompatibilityLevel}+ (this model is at {cl}). Raise the compatibility level before creating a calendar. This is a one-way upgrade.");
        }

        /// <summary>ResolveTable (the shared name-or-'table:Name' resolver) made loud for the calendar ops.</summary>
        private static Table RequireCalendarTable(Model m, string tableRef)
        {
            if (string.IsNullOrWhiteSpace(tableRef)) throw new InvalidOperationException("A table is required (name or 'table:Name' ref).");
            return ResolveTable(m, tableRef) ?? throw new InvalidOperationException($"{tableRef} is not a table in this model — pass a table ref (a name or 'table:Name'); run list_objects to see the model's tables.");
        }

        private static TOM.Column RequireColumn(TOM.Table tom, string name)
            => tom.Columns.Find((name ?? string.Empty).Trim())
               ?? throw new InvalidOperationException($"Table '{tom.Name}' has no column named '{name}'. Mappings reference columns on the calendar's own table.");

        /// <summary>Parse a TimeUnit name; null/empty/"timeRelated" means the untagged time-related bucket.</summary>
        private static TOM.TimeUnit? ParseTimeUnit(string timeUnit)
        {
            var v = (timeUnit ?? string.Empty).Trim();
            if (v.Length == 0 || v.Equals("timeRelated", StringComparison.OrdinalIgnoreCase)) return null;
            if (Enum.TryParse<TOM.TimeUnit>(v, ignoreCase: true, out var tu) && tu != TOM.TimeUnit.Unknown) return tu;
            throw new InvalidOperationException(
                $"'{timeUnit}' is not a TimeUnit. Use one of: {string.Join(", ", Enum.GetNames(typeof(TOM.TimeUnit)).Where(n => n != "Unknown"))}, or 'timeRelated' for the untagged bucket.");
        }

        /// <summary>Apply mappings onto a calendar (new or existing): one TimeUnitColumnAssociation per unit
        /// (primary replaceable, associated additive), plus a single time-related bucket. Deterministic + loud:
        /// an associated mapping whose unit has no primary is an error, not a silent guess.</summary>
        private static void ApplyMappings(TOM.Table tom, TOM.Calendar cal, IEnumerable<CalendarMappingSpec> mappings, List<string> mapped)
        {
            foreach (var spec in mappings)
            {
                var col = RequireColumn(tom, spec?.Column);
                var unit = ParseTimeUnit(spec.TimeUnit);
                if (unit == null)
                {
                    var bucket = cal.CalendarColumnGroups.OfType<TOM.TimeRelatedColumnGroup>().FirstOrDefault();
                    if (bucket == null) { bucket = new TOM.TimeRelatedColumnGroup(); cal.CalendarColumnGroups.Add(bucket); }
                    if (!bucket.Columns.Contains(col)) bucket.Columns.Add(col);
                    mapped.Add($"{col.Name} → time-related");
                    continue;
                }
                var assoc = cal.CalendarColumnGroups.OfType<TOM.TimeUnitColumnAssociation>().FirstOrDefault(a => a.TimeUnit == unit.Value);
                if (spec.Associated)
                {
                    if (assoc == null)
                        throw new InvalidOperationException($"Cannot add '{col.Name}' as an ASSOCIATED {unit} column: the calendar has no primary {unit} column yet. Map the primary first (associated:false).");
                    if (!assoc.AssociatedColumns.Contains(col)) assoc.AssociatedColumns.Add(col);
                    mapped.Add($"{col.Name} → {unit} (associated)");
                }
                else if (assoc == null)
                {
                    cal.CalendarColumnGroups.Add(new TOM.TimeUnitColumnAssociation(unit.Value) { PrimaryColumn = col });
                    mapped.Add($"{col.Name} → {unit}");
                }
                else
                {
                    assoc.PrimaryColumn = col;   // re-tagging a unit replaces its primary — last-writer-wins, visible in the result
                    mapped.Add($"{col.Name} → {unit} (replaced primary)");
                }
            }
        }

        private static void RemoveMapping(TOM.Calendar cal, TOM.Column col, List<string> mapped)
        {
            var hit = false;
            foreach (var a in cal.CalendarColumnGroups.OfType<TOM.TimeUnitColumnAssociation>().ToArray())
            {
                if (a.AssociatedColumns.Contains(col)) { a.AssociatedColumns.Remove(col); mapped.Add($"{col.Name} ⇸ {a.TimeUnit} (associated)"); hit = true; }
                if (a.PrimaryColumn == col)
                {
                    if (a.AssociatedColumns.Count > 0)
                        throw new InvalidOperationException($"'{col.Name}' is the primary {a.TimeUnit} column and still has associated columns — remove those first, or tag a replacement primary.");
                    cal.CalendarColumnGroups.Remove(a); mapped.Add($"{col.Name} ⇸ {a.TimeUnit}"); hit = true;
                }
            }
            foreach (var r in cal.CalendarColumnGroups.OfType<TOM.TimeRelatedColumnGroup>().ToArray())
            {
                if (!r.Columns.Contains(col)) continue;
                r.Columns.Remove(col); hit = true;
                if (r.Columns.Count == 0) cal.CalendarColumnGroups.Remove(r);
                mapped.Add($"{col.Name} ⇸ time-related");
            }
            if (!hit) throw new InvalidOperationException($"'{col.Name}' is not mapped in calendar '{cal.Name}'. list_calendars shows current mappings.");
        }
    }
}
