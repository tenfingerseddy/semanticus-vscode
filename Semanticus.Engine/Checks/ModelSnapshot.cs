#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Semanticus.Analysis;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Utils;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Engine.Checks;

public enum ColumnKind { Text, Integer, Decimal, Double, Date, Boolean }
public enum PeriodRole { None, Date, Year, Quarter, QuarterOfYear, Month, MonthOfYear, DayOfWeek }
public record ModelColumn(Axis Axis, ColumnKind Kind, PeriodRole Period = PeriodRole.None)
{
    /// <summary>Hidden from report authors, so no report can slice it (a sort column). Left out of the structure hash: it changes
    /// which column a place slices, not what any number means.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Hidden { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HierarchyLevel { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public bool SortedLabel { get; init; }
    /// <summary>The period a date-table label stands for when the calendar gave the role to another column: the time unit the
    /// calendar associates it with, else the role of the column it sorts by ('Month' sorted by the calendar's 'Month Number'). A slicer
    /// on it picks days like the period column itself (F3: X01 kept 'Month' beside a year to date). Hashed only when present.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public PeriodRole LabelPeriod { get; init; }
    /// <summary>The chosen calendar names this column (a primary or an associated column of a time unit). The calendar functions clear a tagged
    /// column of the date table and keep any other (f4); a name guess is not a tag. Left out of the structure hash like Hidden.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Tagged { get; init; }
    /// <summary>Columns the server requires beside this column whenever it is grouped.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public Axis[] GroupByColumns { get; init; } = Array.Empty<Axis>();
    /// <summary>The column belongs to a table carrying the ParameterMetadata marker.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool FieldParameter { get; init; }
}
public record ModelRelationship(Axis From, Axis To, bool Active, bool TwoWay);
public record MeasureDefinition(string Table, string Name, string Expression, string FormatString);
public record ModelStructure(ModelColumn[] Columns, ModelRelationship[] Relationships,
    string DateTable, string DateColumn, string? CalendarObject, int FiscalYearEndMonth,
    string StructureHash, bool HasCalculationGroups)
{
    /// <summary>The date table is marked as a date table; time logic on it then clears its other filters.</summary>
    public bool DateTableMarked { get; init; }
    /// <summary>The measure (or a measure it reads) names its calendar: TOTALYTD([Sales], 'Fiscal year'). Calendar-based time logic
    /// clears every tagged and time-related column of the table before it finds the period, so a weekday slicer does not move the end.</summary>
    public bool CalendarMeasure { get; init; }
    /// <summary>A DATESINPERIOD in the closure takes a calendar (not a date column) as its first argument, so its end behavior is PRECISE.</summary>
    public bool CalendarWindow { get; init; }
    /// <summary>The closure uses both calendar and date-column time calls.</summary>
    public bool MixedTime { get; init; }
    public bool CalendarToDate { get; init; }
    /// <summary>Any time call in the closure (to-date, window, shift) is given a calendar object, not a date column (f4r2).</summary>
    public bool CalendarTime { get; init; }
    /// <summary>The model holds a field parameter. Its presence alone does not change a named measure's result.</summary>
    public bool HasFieldParameter { get; init; }
    /// <summary>A role filters rows of a table (row-level security): the viewer's total is not the company total (lane Z0, UZ-05).</summary>
    public bool HasRowFilter { get; init; }
    /// <summary>The names of the model's calculation group tables, so a formula that applies an item by name reads as date logic (d10b).</summary>
    public string[] CalculationGroupTables { get; init; } = Array.Empty<string>();
    /// <summary>The first non-identity calculation item supplies the example in the successful check's scope note.</summary>
    public string? FirstCalculationItemName { get; init; }
}
public record ModelSnapshot(ModelStructure Structure, MeasureDefinition Candidate,
    MeasureDefinition[] Dependencies, bool Trusted, string? Fidelity);

/// <summary>Run Capture on the model thread. All other query work uses these detached values.</summary>
public static class ModelSnapshots
{
    public static ModelSnapshot Capture(Model model, string measureRef, bool trusted, int? fiscalYearEndMonth = null)
        => CaptureCandidate(model, measureRef, trusted, null, fiscalYearEndMonth);

    /// <summary>Capture a proposed formula without writing it, including the dependencies that formula actually reads.</summary>
    internal static ModelSnapshot CaptureCandidate(Model model, string measureRef, bool trusted, string? expression, int? fiscalYearEndMonth = null)
    {
        var candidate = ObjectRefs.Resolve(model, measureRef) as Measure
            ?? throw new ArgumentException("The check needs a measure.", nameof(measureRef));
        var all = model.AllMeasures.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        var closure = new Dictionary<string, MeasureDefinition>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<Measure>();
        pending.Enqueue(candidate);
        while (pending.Count > 0)
        {
            var m = pending.Dequeue();
            if (closure.ContainsKey(m.Name)) continue;
            var formula = m == candidate && expression != null ? expression : m.Expression ?? "";
            closure[m.Name] = new MeasureDefinition(m.Table.Name, m.Name, formula, m.FormatString ?? "");
            foreach (var reference in References(formula))
                if (all.TryGetValue(reference.Column, out var dependency)
                    && (reference.Table.Length == 0 || Eq(reference.Table, dependency.Table.Name)))
                    pending.Enqueue(dependency);
        }

        // Prefer an explicitly referenced calendar/date table, then the active fact path.
        var tablesUsed = closure.Values.SelectMany(m => References(m.Expression)).Select(r => r.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var calendarNames = closure.Values.SelectMany(m => DaxScan.Tokenize(m.Expression))
            .Where(t => t.Kind == DaxScan.Kind.Name && t.Delim == '\'').Select(t => t.Inner).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = model.Relationships.OfType<SingleColumnRelationship>()
            .Where(r => r.FromColumn != null && r.ToColumn != null)
            .Select(r => new ModelRelationship(new Axis(r.FromTable.Name, r.FromColumn.Name),
                new Axis(r.ToTable.Name, r.ToColumn.Name), r.IsActive,
                r.CrossFilteringBehavior == CrossFilteringBehavior.BothDirections)).ToArray();
        var calendars = CalendarOps.Enumerate(model).ToArray();
        // The measure's own time calls pick the calendar before the measures it reads do: a classic year to date guarded by the ISO-week
        // WTD measure must not make ISO weeks the calendar (v-rx6 side note, 22 Oct). A call over a date column names none, so the
        // closure's calendars are not ranked at all and the first calendar stands, as for a classic measure alone.
        var ownTokens = DaxScan.Tokenize(closure[candidate.Name].Expression);
        bool NamesAnyCalendar(DaxScan.Tok t) => calendars.Any(c => t.Kind == DaxScan.Kind.Name && t.Delim == '\'' && Eq(t.Inner, c.Calendar.Name)
            || t.Kind == DaxScan.Kind.Word && Eq(t.Text, c.Calendar.Name));
        bool ownCalendar = false, ownColumn = false;
        foreach (var (function, dateArgument) in new[] { ("TOTALYTD", 1), ("TOTALQTD", 1), ("TOTALMTD", 1), ("TOTALWTD", 1),
                     ("DATESYTD", 0), ("DATESQTD", 0), ("DATESMTD", 0), ("DATESWTD", 0), ("DATESINPERIOD", 0) })
            foreach (var open in DaxScan.AllCallOpens(ownTokens, function))
            {
                var args = DaxScan.ArgRanges(ownTokens, open);
                if (args.Count <= dateArgument) continue;
                if (ownTokens.Skip(args[dateArgument].start).Take(args[dateArgument].end - args[dateArgument].start).Any(NamesAnyCalendar)) ownCalendar = true;
                else ownColumn = true;
            }
        var rankNames = ownCalendar ? ownTokens.Where(NamesAnyCalendar).Select(t => t.Kind == DaxScan.Kind.Name ? t.Inner : t.Text).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : ownColumn ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : calendarNames;
        var chosen = calendars.OrderByDescending(c => rankNames.Contains(c.Calendar.Name))
            .ThenByDescending(c => tablesUsed.Contains(c.Table.Name))
            .ThenByDescending(c => paths.Any(p => p.Active && tablesUsed.Contains(p.From.Table) && Eq(p.To.Table, c.Table.Name)))
            .ThenBy(c => c.Table.Name, StringComparer.Ordinal).FirstOrDefault();
        var dateTable = chosen.Table ?? model.Tables
            .Where(t => Eq(t.DataCategory, "Time"))
            .OrderByDescending(t => tablesUsed.Contains(t.Name))
            .ThenByDescending(t => paths.Any(p => p.Active && tablesUsed.Contains(p.From.Table) && Eq(p.To.Table, t.Name)))
            .ThenBy(t => t.Name, StringComparer.Ordinal).FirstOrDefault();
        var roles = new Dictionary<Axis, PeriodRole>();
        if (chosen.Calendar != null)
            foreach (var group in chosen.Calendar.CalendarColumnGroups.OfType<TOM.TimeUnitColumnAssociation>())
                if (group.PrimaryColumn != null && Enum.TryParse<PeriodRole>(group.TimeUnit.ToString(), true, out var role))
                    roles[new Axis(chosen.Table!.Name, group.PrimaryColumn.Name)] = role;
        // A name guess gives a date table column a role only when the chosen calendar has none: with a Gregorian and a fiscal calendar on
        // one table, 'Fiscal Year' must not be a year of the Gregorian calendar (lv, 21 Oct 2026: it sorted first and became the year axis).
        var calendarRoles = roles.Values.Where(r => r != PeriodRole.Date).ToHashSet();
        PeriodRole Guess(string name, string date)
        {
            var guess = InferPeriod(name, date);
            return guess != PeriodRole.Date && calendarRoles.Contains(guess) ? PeriodRole.None : guess;
        }
        var associated = new Dictionary<Axis, PeriodRole>();
        if (chosen.Calendar != null)
            foreach (var group in chosen.Calendar.CalendarColumnGroups.OfType<TOM.TimeUnitColumnAssociation>())
                if (Enum.TryParse<PeriodRole>(group.TimeUnit.ToString(), true, out var role))
                    foreach (var column in group.AssociatedColumns) associated.TryAdd(new Axis(chosen.Table!.Name, column.Name), role);
        var dateColumn = roles.FirstOrDefault(p => p.Value == PeriodRole.Date).Key?.Column
            ?? dateTable?.Columns.FirstOrDefault(c => c.IsKey && c.DataType == DataType.DateTime)?.Name
            ?? dateTable?.Columns.FirstOrDefault(c => c.DataType == DataType.DateTime)?.Name ?? "";
        PeriodRole Label(Table t, Column c)
        {
            if (!Eq(t.Name, dateTable?.Name)) return PeriodRole.None;
            if (associated.TryGetValue(new Axis(t.Name, c.Name), out var role)) return role;
            if (c.SortByColumn is not { } sort || sort == c) return PeriodRole.None;
            return roles.TryGetValue(new Axis(t.Name, sort.Name), out var sorted) ? sorted : Guess(sort.Name, dateColumn);
        }
        var fieldParameterTables = model.Tables.Where(LocalEngine.IsFieldParameterTable).Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = model.Tables.SelectMany(t => t.Columns.Where(c => c.Type != ColumnType.RowNumber).Select(c =>
        {
            var axis = new Axis(t.Name, c.Name);
            var kind = c.DataType switch {
                DataType.Int64 => ColumnKind.Integer, DataType.Decimal => ColumnKind.Decimal,
                DataType.Double => ColumnKind.Double, DataType.DateTime => ColumnKind.Date,
                DataType.Boolean => ColumnKind.Boolean, _ => ColumnKind.Text
            };
            return new ModelColumn(axis, kind, roles.TryGetValue(axis, out var role) ? role
                : Eq(t.Name, dateTable?.Name) ? Guess(c.Name, dateColumn) : PeriodRole.None) { Hidden = c.IsHidden, LabelPeriod = Label(t, c),
                Tagged = roles.ContainsKey(axis) || associated.ContainsKey(axis),
                HierarchyLevel = c.UsedInHierarchies.Any(), SortedLabel = c.SortByColumn != null,
                GroupByColumns = c.GroupByColumns?.Select(g => new Axis(g.Table.Name, g.Name)).ToArray() ?? Array.Empty<Axis>(),
                FieldParameter = fieldParameterTables.Contains(t.Name) };
        })).OrderBy(c => c.Axis.Table, StringComparer.Ordinal).ThenBy(c => c.Axis.Column, StringComparer.Ordinal).ToArray();
        var fiscal = fiscalYearEndMonth ?? FiscalEnd(closure.Values.Select(m => m.Expression));
        if (fiscal < 1 || fiscal > 12) throw new ArgumentOutOfRangeException(nameof(fiscalYearEndMonth));
        var groups = model.Tables.OfType<CalculationGroupTable>().Select(t => new {
            t.Name, Items = t.CalculationItems.Select(i => new { i.Name, i.Expression }).ToArray()
        }).ToArray();
        var calendarShape = calendars.Select(c => new {
            Table = c.Table.Name, c.Calendar.Name,
            Groups = c.Calendar.CalendarColumnGroups.Select(g => g switch {
                TOM.TimeUnitColumnAssociation a => a.TimeUnit + ":" + a.PrimaryColumn?.Name + ":" + string.Join(",", a.AssociatedColumns.Select(x => x.Name)),
                TOM.TimeRelatedColumnGroup r => string.Join(",", r.Columns.Select(x => x.Name)),
                _ => g.GetType().Name
            }).ToArray()
        }).ToArray();
        var calendarMeasure = chosen.Calendar != null && closure.Values.SelectMany(m => DaxScan.Tokenize(m.Expression))
            .Any(t => t.Kind == DaxScan.Kind.Name && t.Delim == '\'' && Eq(t.Inner, chosen.Calendar.Name)
                || t.Kind == DaxScan.Kind.Word && Eq(t.Text, chosen.Calendar.Name));
        // The window rule follows the DATESINPERIOD call's own first argument, not the closure: a classic window that reads a calendar measure
        // (a guard on a YTD measure) still keeps whole months (v-rx6 item 3c).
        bool NamesCalendar(DaxScan.Tok t) => t.Kind == DaxScan.Kind.Name && t.Delim == '\'' && Eq(t.Inner, chosen.Calendar!.Name)
            || t.Kind == DaxScan.Kind.Word && Eq(t.Text, chosen.Calendar!.Name);
        var calendarWindow = chosen.Calendar != null && closure.Values.Any(m =>
        {
            var toks = DaxScan.Tokenize(m.Expression);
            return DaxScan.AllCallOpens(toks, "DATESINPERIOD").Any(open =>
                DaxScan.ArgRanges(toks, open).Take(1).Any(a => toks.Skip(a.start).Take(a.end - a.start).Any(NamesAnyCalendar)));
        });
        // Each time call is read for its own date argument. A closure that holds a time call over a calendar AND one over a date column
        // The capture records both calendar-based and date-column time calls in the dependency closure.
        var timeCalls = new List<(string Function, int Argument)> { ("TOTALYTD", 1), ("TOTALQTD", 1), ("TOTALMTD", 1), ("TOTALWTD", 1),
            ("DATESYTD", 0), ("DATESQTD", 0), ("DATESMTD", 0), ("DATESWTD", 0), ("DATESINPERIOD", 0), ("DATEADD", 0),
            ("SAMEPERIODLASTYEAR", 0), ("PARALLELPERIOD", 0) };
        foreach (var unit in new[] { "MONTH", "QUARTER", "YEAR" })
        {
            foreach (var family in new[] { "PREVIOUS", "NEXT", "STARTOF", "ENDOF" }) timeCalls.Add((family + unit, 0));
            foreach (var family in new[] { "OPENINGBALANCE", "CLOSINGBALANCE" }) timeCalls.Add((family + unit, 1));
        }
        timeCalls.Add(("PREVIOUSDAY", 0));
        timeCalls.Add(("NEXTDAY", 0));
        (bool Calendar, bool Column) FirstArguments(IEnumerable<(string Function, int Argument)> calls)
        {
            bool calendar = false, column = false;
            foreach (var m in closure.Values)
            {
                var toks = DaxScan.Tokenize(m.Expression);
                foreach (var (function, dateArgument) in calls)
                    foreach (var open in DaxScan.AllCallOpens(toks, function))
                    {
                        var args = DaxScan.ArgRanges(toks, open);
                        if (args.Count <= dateArgument) continue;
                        var a = args[dateArgument];
                        if (toks.Skip(a.start).Take(a.end - a.start).Any(NamesAnyCalendar)) calendar = true; else column = true;
                    }
            }
            return (calendar, column);
        }
        var anyTime = FirstArguments(timeCalls);
        // v-rx6 re-check 4: a date-table filter written by hand (a CALCULATE filter argument that names the date table outside
        // every calendar time call: DATESBETWEEN, FILTER over the dates, a predicate) is a date-column time step too.
        var dateTables = calendars.Select(c => c.Table.Name).Append(dateTable?.Name).Where(n => n != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var handColumn = anyTime.Calendar && closure.Values.Any(m =>
        {
            var toks = DaxScan.Tokenize(m.Expression);
            var calendarSpans = timeCalls.SelectMany(c => DaxScan.AllCallOpens(toks, c.Function).Select(open => (open, args: DaxScan.ArgRanges(toks, open), c.Argument)))
                .Where(x => x.args.Count > x.Argument && toks.Skip(x.args[x.Argument].start).Take(x.args[x.Argument].end - x.args[x.Argument].start).Any(NamesAnyCalendar))
                .Select(x => (Start: x.open, End: x.args[^1].end)).ToArray();
            return new[] { "CALCULATE", "CALCULATETABLE" }.SelectMany(f => DaxScan.AllCallOpens(toks, f)).Any(open =>
                DaxScan.ArgRanges(toks, open).Skip(1).Any(a => Enumerable.Range(a.start, a.end - a.start).Any(i =>
                    (toks[i].Kind == DaxScan.Kind.Name && toks[i].Delim == '\'' && dateTables.Contains(toks[i].Inner)
                        || toks[i].Kind == DaxScan.Kind.Word && dateTables.Contains(toks[i].Text))
                    && !calendarSpans.Any(sp => i > sp.Start && i < sp.End))));
        });
        anyTime = (anyTime.Calendar, anyTime.Column || handColumn);
        var fieldParameter = fieldParameterTables.Count > 0;
        var rowFilter = model.Roles.Any(r => r.TablePermissions.Any(tp => !string.IsNullOrWhiteSpace(tp.FilterExpression)));
        var shape = new {
            Columns = columns, Paths = paths, Calendars = calendarShape, Groups = groups, Fiscal = fiscal,
            MarkedTables = model.Tables.Where(t => Eq(t.DataCategory, "Time")).Select(t => t.Name).ToArray()
        };
        // A label's period changes what a slicer on it selects. Added only when one exists, so other models keep their hash.
        var labels = columns.Where(c => c.LabelPeriod != PeriodRole.None).Select(c => c.Axis.Column + ":" + c.LabelPeriod).ToArray();
        var grouped = columns.Where(c => c.GroupByColumns.Length > 0).Select(c => new { c.Axis, c.GroupByColumns }).ToArray();
        var parameterTables = columns.Where(c => c.FieldParameter).Select(c => c.Axis.Table).Distinct().ToArray();
        // Model flags also change the hash only when present (M3).
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((labels.Length == 0 ? JsonSerializer.Serialize(shape)
            : JsonSerializer.Serialize(new { shape.Columns, shape.Paths, shape.Calendars, shape.Groups, shape.Fiscal, shape.MarkedTables, Labels = labels }))
            + (fieldParameter ? "|field parameter" : "") + (rowFilter ? "|row filter" : "")
            + (grouped.Length == 0 && parameterTables.Length == 0 ? "" : "|column groups:" + JsonSerializer.Serialize(new { grouped, parameterTables })))));
        var structure = new ModelStructure(columns, paths, dateTable?.Name ?? "", dateColumn, chosen.Calendar?.Name,
            fiscal, hash, groups.Length != 0) { DateTableMarked = dateTable != null && Eq(dateTable.DataCategory, "Time"),
            HasFieldParameter = fieldParameter, HasRowFilter = rowFilter,
            CalculationGroupTables = groups.Select(g => g.Name).ToArray(),
            FirstCalculationItemName = model.Tables.OfType<CalculationGroupTable>()
                .SelectMany(t => t.CalculationItems.OrderBy(i => i.Ordinal))
                .Where(i => !IsIdentityCalculationItem(i.Expression)).Select(i => i.Name).FirstOrDefault(),
            CalendarMeasure = calendarMeasure, CalendarWindow = calendarWindow,
            MixedTime = anyTime.Calendar && anyTime.Column, CalendarTime = anyTime.Calendar,
            CalendarToDate = FirstArguments(new[] { ("TOTALYTD", 1), ("TOTALQTD", 1), ("TOTALMTD", 1), ("TOTALWTD", 1),
                ("DATESYTD", 0), ("DATESQTD", 0), ("DATESMTD", 0), ("DATESWTD", 0) }).Calendar };
        var target = closure[candidate.Name];
        var spec = QuerySpec(structure, target, all.Keys.ToArray(), trusted);
        var fidelity = DaxBench.PlanEval(spec, false, target.Expression).Note;
        return new ModelSnapshot(structure, target, closure.Values.Where(m => !Eq(m.Name, candidate.Name)).ToArray(), trusted, fidelity);
    }

    private static bool IsIdentityCalculationItem(string expression)
    {
        var tokens = DaxScan.Tokenize(expression);
        return tokens.Count == 3 && tokens[0].Kind == DaxScan.Kind.Word && Eq(tokens[0].Text, "SELECTEDMEASURE")
            && tokens[1].Kind == DaxScan.Kind.LParen && tokens[2].Kind == DaxScan.Kind.RParen;
    }

    /// <summary>
    /// A cheap hash of everything in a snapshot's structure hash that does not depend on one measure: columns and their
    /// types, keys, tables marked as dates, relationships, calendars and calculation groups. It moves exactly when the
    /// structure hash of a measure that reads the changed part moves. Fiscal year comes from a measure's own formula,
    /// which an Expression edit already re-checks.
    /// </summary>
    public static string StructureFingerprint(Model model)
    {
        var text = new StringBuilder();
        foreach (var table in model.Tables)
        {
            text.Append('T').Append(table.Name).Append('|').Append(table.DataCategory);
            foreach (var column in table.Columns.Where(c => c.Type != ColumnType.RowNumber))
                text.Append('\n').Append(column.Name).Append(':').Append(column.DataType).Append(column.IsKey ? ":key" : "")
                    .Append(column.SortByColumn is { } sort && Eq(table.DataCategory, "Time") ? ":sort=" + sort.Name : "");
            if (table is CalculationGroupTable group)
                foreach (var item in group.CalculationItems) text.Append("\nI").Append(item.Name).Append('=').Append(item.Expression);
            text.Append('\n');
        }
        foreach (var path in model.Relationships.OfType<SingleColumnRelationship>().Where(r => r.FromColumn != null && r.ToColumn != null))
            text.Append('R').Append(path.FromTable.Name).Append('.').Append(path.FromColumn.Name).Append('>')
                .Append(path.ToTable.Name).Append('.').Append(path.ToColumn.Name).Append(path.IsActive).Append(path.CrossFilteringBehavior).Append('\n');
        foreach (var (table, calendar) in CalendarOps.Enumerate(model))
        {
            text.Append('C').Append(table.Name).Append('.').Append(calendar.Name);
            foreach (var group in calendar.CalendarColumnGroups)
                text.Append('\n').Append(group switch {
                    TOM.TimeUnitColumnAssociation a => a.TimeUnit + ":" + a.PrimaryColumn?.Name + ":" + string.Join(",", a.AssociatedColumns.Select(x => x.Name)),
                    TOM.TimeRelatedColumnGroup r => string.Join(",", r.Columns.Select(x => x.Name)),
                    _ => group.GetType().Name
                });
            text.Append('\n');
        }
        foreach (var table in model.Tables.Where(LocalEngine.IsFieldParameterTable)) text.Append("F").Append(table.Name).Append('\n');
        foreach (var role in model.Roles)
            foreach (var permission in role.TablePermissions.Where(tp => !string.IsNullOrWhiteSpace(tp.FilterExpression)))
                text.Append("P").Append(role.Name).Append('.').Append(permission.Table.Name).Append('=').Append(permission.FilterExpression).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    internal static DaxQuerySpec QuerySpec(ModelStructure structure, MeasureDefinition target, string[] names, bool trusted) =>
        new() { HomeTable = target.Table, TargetMeasureName = target.Name, ModelMeasureNames = names,
            ModelHasCalcGroups = structure.HasCalculationGroups, Trusted = trusted };

    public static Axis[] References(string? expression)
    {
        var tokens = DaxScan.Tokenize(expression ?? "");
        var result = new List<Axis>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != DaxScan.Kind.Name || token.Delim != '[') continue;
            var previous = i == 0 ? null : tokens[i - 1];
            var table = previous?.Kind == DaxScan.Kind.Word && !BareReferenceKeywords.Contains(previous.Text) ? previous.Text
                : previous?.Kind == DaxScan.Kind.Name && previous.Delim == '\'' ? previous.Inner : "";
            result.Add(new Axis(table, token.Inner));
        }
        return result.Distinct().ToArray();
    }

    private static readonly HashSet<string> BareReferenceKeywords = new(StringComparer.OrdinalIgnoreCase) {
        "RETURN", "VAR", "IN", "NOT", "AND", "OR", "DEFINE", "EVALUATE", "ORDER", "BY", "ASC", "DESC", "START", "AT"
    };

    public static bool HasFunction(ModelSnapshot snapshot, params string[] functions) =>
        snapshot.Dependencies.Append(snapshot.Candidate).Any(m => {
            var tokens = DaxScan.Tokenize(m.Expression);
            return functions.Any(f => DaxScan.CallsFunction(tokens, f));
        });

    private static int FiscalEnd(IEnumerable<string> expressions)
    {
        foreach (var expression in expressions)
        {
            var tokens = DaxScan.Tokenize(expression);
            foreach (var function in new[] { "TOTALYTD", "DATESYTD" })
                foreach (var open in DaxScan.AllCallOpens(tokens, function))
                {
                    var args = DaxScan.ArgRanges(tokens, open);
                    if (args.Count < (function == "TOTALYTD" ? 3 : 2)) continue;
                    var last = args[args.Count - 1];
                    if (last.end - last.start != 1 || tokens[last.start].Kind != DaxScan.Kind.String) continue;
                    var start = CalendarMeasureMove.StartMonthFromYearEnd(tokens[last.start].Inner);
                    if (start.HasValue) return start.Value == 1 ? 12 : start.Value - 1;
                }
        }
        return 12;
    }

    /// <summary>A column named like the weekday ('Day of Week', 'Weekday'), whatever role the calendar gave it.</summary>
    internal static bool NamedWeekday(string name) => InferPeriod(name, "") == PeriodRole.DayOfWeek;

    private static PeriodRole InferPeriod(string name, string dateColumn)
    {
        if (Eq(name, dateColumn)) return PeriodRole.Date;
        var normalized = string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return normalized switch {
            "year" or "calendaryear" or "fiscalyear" => PeriodRole.Year,
            "yearquarter" => PeriodRole.Quarter,
            "quarter" or "quarterofyear" or "quarternumber" => PeriodRole.QuarterOfYear,
            "yearmonth" => PeriodRole.Month,
            "month" or "monthofyear" or "monthnumber" => PeriodRole.MonthOfYear,
            "dayofweek" or "weekday" or "dayofweeknumber" => PeriodRole.DayOfWeek,
            _ => PeriodRole.None
        };
    }

    internal static bool Eq(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

public static class PlaceDax
{
    public static string Table(string name) => "'" + name.Replace("'", "''") + "'";
    public static string Name(string name) => "[" + name.Replace("]", "]]") + "]";
    public static string Column(Axis axis) => Table(axis.Table) + Name(axis.Column);
    public static string Text(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    public static string Date(DateOnly value) => "DATE(" + value.Year.ToString(CultureInfo.InvariantCulture) + ","
        + value.Month.ToString(CultureInfo.InvariantCulture) + "," + value.Day.ToString(CultureInfo.InvariantCulture) + ")";
    public static KeyPart Key(object? value) => value == null || value is DBNull ? new(KeyKind.Blank, null)
        : new(KeyKind.Value, value switch {
            DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            double n => n.ToString("R", CultureInfo.InvariantCulture),
            float n => n.ToString("R", CultureInfo.InvariantCulture),
            IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        });

    public static string Literal(KeyPart key, ColumnKind kind)
    {
        if (key.Kind == KeyKind.Blank) return "BLANK()";
        if (key.Kind != KeyKind.Value || key.Value == null) throw new ArgumentException("A total is not a filter value.");
        return kind switch {
            ColumnKind.Date => Date(DateOnly.ParseExact(key.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ColumnKind.Boolean => bool.Parse(key.Value) ? "TRUE()" : "FALSE()",
            ColumnKind.Integer => long.Parse(key.Value, NumberStyles.Integer, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            ColumnKind.Decimal => decimal.Parse(key.Value, NumberStyles.Float, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            ColumnKind.Double => FiniteNumber(key.Value),
            _ => Text(key.Value)
        };
    }

    private static string FiniteNumber(string value)
    {
        var number = double.Parse(value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number)) throw new ArgumentException("A filter number must be finite.");
        return number.ToString("R", CultureInfo.InvariantCulture);
    }

    public static string Filter(Slicer slicer, ModelStructure structure)
    {
        var axis = new Axis(slicer.Table, slicer.Column);
        var column = structure.Columns.FirstOrDefault(c => c.Axis == axis)
            ?? throw new ArgumentException("The filter column is not in the model.");
        var values = slicer.Values.Length == 0 ? "FILTER({BLANK()}, FALSE())"
            : "{" + string.Join(", ", slicer.Values.Select(k => Literal(k, column.Kind))) + "}";
        return "TREATAS(" + values + ", " + Column(axis) + ")";
    }
}
