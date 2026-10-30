using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Semanticus.Engine.MeasureContexts;

namespace Semanticus.Engine;

internal static class MeasureMarkJudge
{
    private static IEnumerable<MeasureMarkCell> DistinctPlaces(string name, IEnumerable<MeasureMarkCell> cells) =>
        name is "Filters" or "Contexts"
            ? cells.DistinctBy(c => Place(CellText(c.Context)), StringComparer.OrdinalIgnoreCase) : cells;

    internal static string ActionFor(string name) => name switch
    {
        "SQL" => "Set up a SQL check", "Trusted number" => "Add a number you trust",
        "Equivalence" => "See what moved", "Filters" => "Check filters", "Contexts" => "Check contexts",
        "Performance" => "Include performance",
        _ => "View check details"
    };

    internal static MeasureMark NotRun(string name, string sentence) => new()
    { Name = name, State = "not run", Sentence = sentence, Action = ActionFor(name) };

    private static MeasureMark Ran(string name, bool suspect, string sentence, IEnumerable<MeasureMarkCell> cells) => new()
    {
        Name = name, State = suspect ? "suspect" : "passed", Sentence = sentence,
        Cells = DistinctPlaces(name, cells).Take(6).Select(c => new MeasureMarkCell {
            Context = CellText(c.Context), Before = CellText(c.Before), After = CellText(c.After)
        }).ToArray(), Action = ActionFor(name), LastRan = DateTime.UtcNow.ToString("o")
    };

    internal static string CellText(string text) => text == null ? null : Regex.Replace(text,
        @"(?<!\w)\d{4}-\d{2}-\d{2}(?:[T ]\d{2}:\d{2}(?::\d{2})?(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?(?!\w)",
        match => DateTimeOffset.TryParse(match.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString(date.TimeOfDay == TimeSpan.Zero ? "d MMM yyyy"
                : date.Second == 0 && date.Millisecond == 0 ? "d MMM yyyy HH:mm" : "d MMM yyyy HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)
            : match.Value);

    private static string Number(object value) => value == null || value is DBNull ? "BLANK" : DaxBench.Fmt(value);
    private static IEnumerable<(ScenarioEvidence Scenario, ProbeRow Row)> Rows(ProbeResult probe) =>
        (probe?.Scenarios ?? Array.Empty<ScenarioEvidence>()).SelectMany(s => s.Rows.Select(r => (s, r)));
    private static MeasureMarkCell Cell(ScenarioEvidence scenario, ProbeRow row) => new()
    {
        Context = row.Members.Count == 0 ? Place(scenario.Name)
            : string.Join(", ", row.Members.Select(m => ColumnName(m.Key) + ": " + (m.Value == null || m.Value is DBNull ? "Blank" : Number(m.Value)))),
        After = Number(row.V)
    };
    internal static string ColumnName(string column) => column?.Contains('[') == true
        ? column[(column.LastIndexOf('[') + 1)..^1].Replace("]]", "]") : column;

    internal static string Place(string context)
    {
        if (string.IsNullOrWhiteSpace(context) || context.Equals("grand total", StringComparison.OrdinalIgnoreCase)) return "Grand total";
        var plain = context.Replace("[__mark_context]=", "", StringComparison.Ordinal);
        plain = Regex.Replace(plain, @"(?:'(?:[^']|'')+'|[\w]+)?\[(?<column>[^\]]+)\]\s*=", "${column}: ");
        plain = Regex.Replace(plain, @"^(?:single-select|multi-select|empty-select|boundary|by|detail under)\s+", "");
        plain = Regex.Replace(plain, @"(?:'(?:[^']|'')+'|[\w]+)?\[(?<column>[^\]]+)\]", "${column}");
        return plain.Replace(" (unfiltered)", "", StringComparison.Ordinal);
    }
    private static bool Blank(ProbeRow row) => row.Blank || row.V == null || row.V is DBNull;
    private static bool Constant(ScenarioEvidence scenario) => scenario?.Rows.Length > 1
        && scenario.Rows.All(r => !Blank(r) && DaxBench.ValuesEqual(r.V, scenario.Rows[0].V))
        && scenario.Rows.Select(r => string.Join("|", r.Members.OrderBy(m => m.Key).Select(m => m.Key + "=" + Number(m.Value))))
            .Distinct(StringComparer.Ordinal).Count() > 1;

    private static IEnumerable<MeasureMarkCell> UncheckedCells(ProbeResult probe) => probe.Scenarios
        .Where(s => s.Status == "error" && s.ErrorKind == "context")
        .Select(s => new MeasureMarkCell { Context = Place(s.Name),
            After = "Could not check this place: " + (s.ContextError ?? s.Error) });

    private static MeasureMark Unchecked(string name, ProbeResult probe)
    {
        var mark = NotRun(name, "Could not check this place.");
        mark.Cells = DistinctPlaces(name, UncheckedCells(probe).Concat(Rows(probe).Select(r => Cell(r.Scenario, r.Row)))).Take(6).ToArray();
        mark.LastRan = DateTime.UtcNow.ToString("o");
        return mark;
    }

    private static MeasureMark Information(string name, string sentence, IEnumerable<MeasureMarkCell> cells)
    {
        var mark = Ran(name, false, sentence, cells);
        mark.State = "info";
        return mark;
    }

    internal static string FilterFinding(ProbeResult probe) => "No ignored filter in "
        + DistinctPlaces("Filters", Rows(probe).Select(r => Cell(r.Scenario, r.Row))).Count()
        + " places." + Limit(probe);

    // A formula that ignores a filter passes only on the person's word ("It is on purpose", the
    // Semanticus.Filters.OnPurpose annotation). Description words never pass it: a prose rule passed bugs (1.7 item 2).
    internal static MeasureMark FilterIntent(MeasureMark finding, ProbeResult probe,
        IReadOnlyList<ContextFilterClear> clears, bool onPurpose, string format = null)
    {
        if (finding.State != "passed" || probe?.Status != "ok") return finding;
        finding.Sentence ??= FilterFinding(probe);
        if (clears.Count == 0) return finding;
        // A clear in one part of a formula need not clear the returned value (for example, a ratio).
        clears = clears.Where(c => c.Column != null && RepeatsAlong(probe, c.Column)).ToArray();
        if (clears.Count == 0) return finding;
        var columns = clears.Where(c => c.Column != null).Select(c => c.Column).Distinct().ToArray();
        var targets = columns.GroupBy(c => c.Table, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Count() > 1 || g.First().Name == g.Key
                ? g.Key : g.Key + " " + g.First().Name).ToArray();
        var scope = targets.Length == 0 ? "all" : targets.Length == 1 ? targets[0]
            : string.Join(", ", targets[..^1]) + " and " + targets[^1];
        var cells = Rows(probe).Select(r => Cell(r.Scenario, r.Row));
        if (onPurpose) return OnPurpose(Information("Filters", "Ignores " + scope + " filters on purpose. You marked this.", cells));
        var rows = Rows(probe).Select(r => r.Row).ToArray();
        var repeated = rows.Length > 1 && rows.All(r => !Blank(r) && DaxBench.ValuesEqual(r.V, rows[0].V));
        var grand = probe?.Scenarios.FirstOrDefault(s => s.Name == "grand total")?.Rows.FirstOrDefault();
        // The number reads as the measure's format shows it, the same text as the Equivalence cells (MarkNumberFormat).
        var values = repeated ? ": every place shows " + MarkNumberFormat.DisplayText(rows[0].V, format) + "."
            : grand != null && !Blank(grand) ? ". The grand total is " + MarkNumberFormat.DisplayText(grand.V, format) + "." : ".";
        var functions = string.Join(", ", clears.Select(c => c.Function).Distinct(StringComparer.OrdinalIgnoreCase));
        return Lost(Information("Filters", "This formula ignores " + scope + " filters" + values
            + " If that is not meant, check the " + functions + ".", cells));
    }

    // Fix the formula and It is on purpose carry equal weight on a lost filter (1.7 item 3).
    private static MeasureMark Lost(MeasureMark mark)
    {
        mark.State = "info"; mark.ReasonCode = "filters_lost"; mark.ActionCodes = new[] { "fix_formula", "on_purpose" };
        return mark;
    }
    private static MeasureMark OnPurpose(MeasureMark mark)
    {
        mark.State = "info"; mark.ReasonCode = "filters_on_purpose"; mark.ActionCodes = new[] { "not_on_purpose" };
        return mark;
    }

    // A stored check can predate the person's word. Every cached surface follows it at once; the run that the changed
    // inputs start then writes the full sentence. Only a lost-filter mark changes: a pass or a warning stays as found.
    internal static MeasureMark ApplyFiltersOnPurpose(MeasureMark mark, bool onPurpose)
    {
        if (mark?.Name != "Filters") return mark;
        if (onPurpose && mark.ReasonCode == "filters_lost")
        { OnPurpose(mark); mark.Sentence = "Ignores these filters on purpose. You marked this."; }
        else if (!onPurpose && mark.ReasonCode == "filters_on_purpose")
        { Lost(mark); mark.Sentence = "This formula ignores these filters. If that is not meant, fix the formula."; }
        return mark;
    }

    private static bool RepeatsAlong(ProbeResult probe, ContextColumn column)
    {
        var rows = Rows(probe).Where(r => r.Scenario.Status == "ok"
            && r.Row.Members.ContainsKey(column.Dax)
            && r.Row.Members.Keys.All(k => k == column.Dax || column.GroupByColumns.Contains(k, StringComparer.OrdinalIgnoreCase)))
            .Select(r => r.Row).ToArray();
        return rows.Length > 1 && rows.All(r => !Blank(r) && DaxBench.ValuesEqual(r.V, rows[0].V))
            && rows.Select(r => Number(r.Members[column.Dax])).Distinct(StringComparer.Ordinal).Count() > 1;
    }

    // "More places are available." is cut (spec section 5, cut 12): the count says how many places were checked.
    private static string Limit(ProbeResult probe) => "";

    private static bool SameMember(object left, object right) =>
        Equals(left is DBNull ? null : left, right is DBNull ? null : right);

    private static object FactRows(ProbeResult facts, ScenarioEvidence scenario, ProbeRow row)
    {
        if (facts?.Status != "ok") return null;
        var batch = facts.Scenarios.FirstOrDefault(s => s.Name == scenario.Name && s.Status == "ok");
        // A bounded query can return a different order. Absence is unknown, never proof of zero rows.
        return batch?.Rows.FirstOrDefault(r => r.Members.Count == row.Members.Count
            && row.Members.All(m => r.Members.TryGetValue(m.Key, out var value) && SameMember(value, m.Value)))?.V;
    }

    internal static MeasureMark Contexts(ProbeResult probe, ProbeResult factRows = null, bool checkBlanks = true)
    {
        if (probe?.Status != "ok") return NotRun("Contexts", probe?.Message ?? "The places could not be checked.");
        var errors = probe.Scenarios.Where(s => s.Status == "error" && s.ErrorKind != "context").ToArray();
        var uncheckedCells = UncheckedCells(probe).ToArray();
        var rows = Rows(probe).ToArray();
        if (errors.Length > 0)
            return Ran("Contexts", true, errors.Length == probe.Scenarios.Length
                ? "The measure gives an error in every place checked." : "The measure gives an error in some places checked.",
                errors.Select(s => new MeasureMarkCell { Context = Place(s.Name), After = "ERROR: " + s.Error })
                    .Concat(uncheckedCells).Concat(rows.Select(r => Cell(r.Scenario, r.Row))));
        if (!checkBlanks)
        {
            var mark = Unchecked("Contexts", probe);
            mark.Sentence = "Could not find a date with source rows. Blank results were not checked.";
            return mark;
        }
        var blanks = rows.Where(r => Blank(r.Row)).ToArray();
        var counted = blanks.Select(r => (r.Scenario, r.Row, Count: FactRows(factRows, r.Scenario, r.Row))).ToArray();
        var unexpected = counted.Where(r => r.Count != null && r.Count is not DBNull
            && Convert.ToDecimal(r.Count, System.Globalization.CultureInfo.InvariantCulture) > 0).ToArray();
        if (unexpected.Length > 0)
            return Ran("Contexts", true, "The measure is blank even though there are rows to calculate.",
                unexpected.Select(r => Cell(r.Scenario, r.Row)).Concat(uncheckedCells)
                    .Concat(rows.Where(r => !Blank(r.Row)).Select(r => Cell(r.Scenario, r.Row))));
        if (uncheckedCells.Length > 0) return Unchecked("Contexts", probe);
        if (rows.Length == 0) return NotRun("Contexts", "The check returned no values.");
        if (counted.Any(r => r.Count == null || r.Count is DBNull) && (factRows != null || blanks.Length < rows.Length))
            return Information("Contexts", "Some blank results could not be compared with the source rows." + Limit(probe),
                rows.Select(r => Cell(r.Scenario, r.Row)));
        return Ran("Contexts", false, blanks.Length == rows.Length
            ? "Blank in all " + rows.Length + " places." + Limit(probe)
            // "Blank results have no rows to calculate." is cut (spec section 5, cut 13).
            : blanks.Length > 0 ? "Values in " + (rows.Length - blanks.Length) + " places." + Limit(probe)
            : "Values in all " + rows.Length + " places." + Limit(probe), rows.Select(r => Cell(r.Scenario, r.Row)));
    }

    internal static bool NeedsFactWitness(ProbeResult probe)
    {
        if (probe?.Status != "ok") return false;
        if (probe.Scenarios.Any(Constant)) return true;
        var grand = probe.Scenarios.FirstOrDefault(s => s.Name == "grand total")?.Rows.FirstOrDefault();
        return grand != null && !Blank(grand) && probe.Scenarios.Where(s => s.Name.StartsWith("single-select", StringComparison.Ordinal))
            .Any(s => s.Rows.Length == 1 && !Blank(s.Rows[0]) && DaxBench.ValuesEqual(s.Rows[0].V, grand.V));
    }

    internal static MeasureMark Filters(ProbeResult probe, ProbeResult facts, RelationshipIntegrityReport relationships,
        bool expectsConstant = false, ProbeResult filterRows = null, string comparisonReason = null)
    {
        var orphans = (relationships?.Relationships ?? Array.Empty<RelationshipResult>())
            .Where(r => r.ReferentialIntegrity?.Count > 0 && r.ReferentialIntegrity.Verdict is Verdict.Fail or Verdict.Suspect).ToArray();
        if (orphans.Length > 0)
            return Ran("Filters", true, "Some fact rows have no related key, so their filters can lose these rows.",
                orphans.Select(r => new MeasureMarkCell { Context = r.Name + ": orphan rows", After = Number(r.ReferentialIntegrity.Count) }));
        if (probe?.Status != "ok") return NotRun("Filters", probe?.Message ?? "The filters could not be probed.");
        filterRows ??= facts;
        var factGrand = facts?.Scenarios.FirstOrDefault(s => s.Name == "grand total")?.Rows.FirstOrDefault();
        var usedGrand = filterRows?.Scenarios.FirstOrDefault(s => s.Name == "grand total")?.Rows.FirstOrDefault();
        var ignored = new List<MeasureMarkCell>();
        foreach (var (scenario, row) in Rows(probe).Where(r => !expectsConstant && r.Row.Members.Count > 0))
        {
            var source = FactRows(facts, scenario, row);
            var used = FactRows(filterRows, scenario, row);
            if (source != null && source is not DBNull && used != null && used is not DBNull && factGrand != null && usedGrand != null
                && !Blank(factGrand) && !Blank(usedGrand) && !DaxBench.ValuesEqual(source, factGrand.V)
                && DaxBench.ValuesEqual(used, usedGrand.V))
                ignored.Add(new MeasureMarkCell { Context = Cell(scenario, row).Context, Before = Number(source), After = Number(used) });
        }
        if (ignored.Count > 0)
            return Ran("Filters", true, "The filter changes the source rows, but this formula still uses all of them.", ignored);
        if (UncheckedCells(probe).Any()) return Unchecked("Filters", probe);
        if (comparisonReason != null && NeedsFactWitness(probe)) return NotRun("Filters", comparisonReason);
        if (!expectsConstant && NeedsFactWitness(probe) && (facts?.Status != "ok" || facts.Scenarios.Any(s => s.Status != "ok")
            || filterRows?.Status != "ok" || filterRows.Scenarios.Any(s => s.Status != "ok")))
            return NotRun("Filters", "The check could not read the rows for this measure. Check the data connection, then check this measure again.");
        if (probe.Scenarios.Any(s => s.Status != "ok"))
            return NotRun("Filters", "Some filter contexts could not finish; check the Contexts mark.");
        if ((relationships?.Relationships ?? Array.Empty<RelationshipResult>()).Any(r => r.ReferentialIntegrity?.Verdict == Verdict.NotVerifiable))
            return NotRun("Filters", "The filter probe finished, but the related orphan-key check could not run.");
        var rows = Rows(probe).ToArray();
        if (rows.Length == 0) return NotRun("Filters", "The filter probe returned no cells.");
        return Ran("Filters", false, FilterFinding(probe), rows.Select(r => Cell(r.Scenario, r.Row)));
    }

    internal static MeasureMark Saved(string name, ReconcileOutcome[] outcomes)
    {
        if (outcomes == null || outcomes.Length == 0) return NotRun(name, name == "SQL"
            ? "No SQL check is saved for this measure." : "No trusted number is saved for this measure.");
        IEnumerable<MeasureMarkCell> Evidence(ReconcileOutcome o)
        {
            if (o.Rows?.Length > 0) return o.Rows.OrderBy(r => r.Verdict == Verdict.Pass ? 1 : 0)
                .Select(r => new MeasureMarkCell { Context = Place(r.Context), Before = Number(r.Sql), After = Number(r.Dax) });
            return new[] { new MeasureMarkCell { Context = Place(o.Filter ?? o.Title ?? "Grand total"), Before = o.Expected, After = o.Actual } };
        }
        var failed = outcomes.Where(o => o.Verdict is Verdict.Fail or Verdict.Suspect).ToArray();
        if (failed.Length > 0) return Ran(name, true, name == "SQL"
            ? "The measure and the saved SQL check give different numbers."
            : "The measure differs from a number you trust.", failed.SelectMany(Evidence));
        var unavailable = outcomes.FirstOrDefault(o => o.Verdict == Verdict.NotVerifiable || o.Unverifiable > 0);
        if (unavailable != null) return NotRun(name, unavailable.Message ?? "A saved check could not run.");
        return Ran(name, false, name == "SQL" ? "The measure matches the saved SQL check." : "The measure matches the saved numbers you trust.",
            outcomes.SelectMany(Evidence));
    }

    internal static MeasureMark Equivalence(BaselineDiff diff)
    {
        if (diff == null) return NotRun("Equivalence", "The edit comparison returned no numbers.");
        // Spec 4.4: "Your edit moved numbers in <k> places." ("This change moved these numbers." is cut 21).
        if (diff.Verdict == "moved") return Ran("Equivalence", true, "Your edit moved numbers in "
            + Math.Max(diff.MismatchCount, diff.Mismatches.Length) + (Math.Max(diff.MismatchCount, diff.Mismatches.Length) == 1 ? " place." : " places."),
            diff.Mismatches.Select(m => new MeasureMarkCell { Context = Place(m.Context), Before = m.ValueA, After = m.ValueB }));
        if (diff.Verdict != "unchanged" || diff.Truncated || diff.RowsCompared <= 0)
            return NotRun("Equivalence", diff.Note ?? "The before and after grid could not be compared completely.");
        return Ran("Equivalence", false, "Nothing moved on the sampled grid.", Array.Empty<MeasureMarkCell>());
    }

    internal static MeasureMark Combine(string name, IReadOnlyList<MeasureMark> marks)
    {
        var worst = marks.FirstOrDefault(m => m.State == "suspect") ?? marks.FirstOrDefault(m => m.State == "not run")
            ?? marks.FirstOrDefault(m => m.State == "info") ?? marks.First();
        return new MeasureMark
        {
            Name = name, State = worst.State, Sentence = worst.Sentence, Action = worst.Action, LastRan = worst.LastRan,
            Cells = DistinctPlaces(name, marks.OrderBy(m => m == worst ? 0 : 1).SelectMany(m => m.Cells)).Take(6).ToArray()
        };
    }
}
