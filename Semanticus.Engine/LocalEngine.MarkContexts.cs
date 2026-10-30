using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Engine.MeasureContexts;

namespace Semanticus.Engine;

public sealed partial class LocalEngine
{
    private sealed record MarkContextQuery(string Id, string Context, string Query);
    private sealed record MarkDataDate(ProbeFilter Filter, DateTime Start, string Range);

    private static ProbeFilter MarkYearFilter(string column, DateTime start) => new() {
        Column = column, Members = Enumerable.Range(0, (start.AddYears(1) - start).Days)
            .Select(day => ProbeMemberText(start.AddDays(day))).ToArray()
    };

    private static bool MarkUsesPreviousYear(MarkRun run) => run.After.Closure(run.Reference).Any(f =>
        Regex.IsMatch(f.Body, @"\b(SAMEPERIODLASTYEAR|PREVIOUSYEAR)\s*\(|\bDATEADD\s*\([^,]+,\s*-\s*1\s*,\s*YEAR\s*\)", RegexOptions.IgnoreCase));

    private static async Task<MarkDataDate> FindMarkDataDateAsync(LiveConnection live, ContextColumn column, string rows)
    {
        if (column == null || rows == null) return null;
        // Find a source-backed calendar date without evaluating the measure or its show-value guard.
        var date = column.Dax;
        var query = "EVALUATE TOPN(1, FILTER(ALL(" + date + "), NOT ISBLANK(" + date
            + ") && CALCULATE(" + rows + ") > 0), " + date + ", DESC)";
        try
        {
            using var factsOnly = live.WithMarkQueries(q => q);
            var result = await live.ExecuteAsync(query, 1, 60).ConfigureAwait(false);
            if (result == null || !string.IsNullOrEmpty(result.Error)) return null;
            var value = result.Rows?.FirstOrDefault()?.FirstOrDefault();
            var parsed = value is DateTime d ? d : value is DateTimeOffset offset ? offset.DateTime : (DateTime?)null;
            if (parsed == null && value is string text && DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var fromText)) parsed = fromText;
            if (parsed == null) return null;
            var start = new DateTime(parsed.Value.Year, 1, 1);
            // The window is one whole year: the spec's "Checked inside <year>." has no date to format, so it never reads in
            // another format than the measure's own (fx30e F2: "1 Jan 2025" beside values in yyyy-mm-dd).
            return new(MarkYearFilter(date, start), start, "inside " + start.Year.ToString(CultureInfo.InvariantCulture));
        }
        catch { return null; } // Errors in the measure are still probed, but an unknown date cannot judge its blanks.
    }

    private async Task ProbePlannedMarksAsync(MarkRun run)
    {
        var formula = run.After.Formulas[run.Reference];
        var measure = run.After.ContextModel.Measures.Single(m => m.Table == formula.Table && m.Name == formula.Name);
        var plan = (MeasureContextPlannerForTests ?? new MeasureContextPlanner()).Plan(new(run.After.ContextModel, measure));
        run.ContextPlan = plan;
        foreach (var name in new[] { "Filters", "Contexts", "Totals" })
            run.View.Marks.Single(m => m.Name == name).ChosenContexts = plan.ChosenContexts;

        var defaults = new Dictionary<string, ProbeScenario[]>(StringComparer.Ordinal);
        var selected = new List<(ContextCandidate Candidate, ProbeScenario[] Scenarios)>();
        foreach (var candidate in plan.ForMark("Contexts"))
        {
            if (candidate.Shape == ContextShape.GrandTotal)
            { selected.Add((candidate, new[] { new ProbeScenario { Name = "grand total" } })); continue; }
            if (candidate.Column == null) continue;
            var axis = candidate.Column.Dax;
            if (!defaults.TryGetValue(axis, out var scenarios))
            {
                scenarios = (await BuildDefaultScenariosAsync(run.Live, axis, 64, run.After.ContextModel).ConfigureAwait(false)).ToArray();
                defaults[axis] = scenarios;
            }
            var wanted = candidate.Shape switch
            {
                ContextShape.Single => scenarios.Where(s => s.Name.StartsWith("single-select", StringComparison.Ordinal)
                    || s.Name.StartsWith("empty-select", StringComparison.Ordinal) || s.Name.StartsWith("boundary", StringComparison.Ordinal)),
                ContextShape.Several => scenarios.Where(s => s.Name.StartsWith("multi-select", StringComparison.Ordinal)),
                ContextShape.Subtotal => scenarios.Where(s => s.Name.StartsWith("by ", StringComparison.Ordinal)),
                _ => scenarios.Where(s => s.Name != "grand total")
            };
            var chosen = wanted.ToArray();
            if (candidate.Shape == ContextShape.Subtotal && candidate.DetailColumn != null)
                chosen = chosen.Append(new ProbeScenario { Name = "detail under " + axis,
                    GroupBy = new[] { axis, candidate.DetailColumn.Dax } }).ToArray();
            selected.Add((candidate, chosen));
        }
        var allScenarios = selected.SelectMany(s => s.Scenarios).DistinctBy(s => s.Name).ToArray();
        var factRowsExpression = plan.UsesTimeLogic ? run.After.FactRowsExpression(run.Reference) : null;
        var dataDate = plan.UsesTimeLogic
            ? await FindMarkDataDateAsync(run.Live, plan.DataDateColumn, factRowsExpression).ConfigureAwait(false) : null;
        if (dataDate != null)
            allScenarios = allScenarios.Select(s => new ProbeScenario { Name = s.Name, GroupBy = s.GroupBy,
                Filters = s.Filters.Append(dataDate.Filter).ToArray() }).ToArray();
        var probe = await ProbeMeasureAsync(Baseline.MeasureRefExpr(formula.Name), null, allScenarios, false, 64).ConfigureAwait(false);
        var relationships = await MarkRelationshipRun(run).ConfigureAwait(false);
        var facts = run.After.Closure(run.Reference).SelectMany(f => f.FactTables).ToHashSet(StringComparer.Ordinal);
        var relevant = new RelationshipIntegrityReport
        {
            Relationships = relationships.Relationships?.Relationships?.Where(r => facts.Contains(r.ManyTable)).ToArray()
                ?? Array.Empty<RelationshipResult>()
        };
        ProbeResult witness = null, filterRows = null;
        string comparisonReason = null;
        if (MeasureMarkJudge.NeedsFactWitness(probe))
        {
            var expression = run.After.FilterRowsExpression(run.Reference, preserveFilterChanges: false);
            if (expression != null) witness = await ProbeMeasureAsync(expression, null, allScenarios, false, 64).ConfigureAwait(false);
            var filtered = run.After.FilterRowsExpression(run.Reference);
            if (expression == null || filtered == null)
                comparisonReason = run.After.FilterRowsUnavailableReason(run.Reference, plan.UsesTimeLogic);
            filterRows = filtered == expression ? witness : filtered == null ? new ProbeResult { Status = "not run" }
                : await ProbeMeasureAsync(filtered, null, allScenarios, false, 64).ConfigureAwait(false);
        }
        var filters = new List<MeasureMark>();
        foreach (var candidate in plan.ForMark("Filters"))
        {
            var scenarios = selected.FirstOrDefault(s => s.Candidate.Id == candidate.Id).Scenarios ?? Array.Empty<ProbeScenario>();
            var names = scenarios.Select(s => s.Name).Append("grand total").ToHashSet(StringComparer.Ordinal);
            ProbeResult Subset(ProbeResult source) => source == null ? null : new ProbeResult
            { Status = source.Status, Message = source.Message, Scenarios = source.Scenarios.Where(s => names.Contains(s.Name)).ToArray() };
            if (candidate.Shape is not (ContextShape.GrandTotal or ContextShape.Subtotal))
                filters.Add(MeasureMarkJudge.Filters(Subset(probe), Subset(witness), relevant,
                    plan.ExpectsConstant(candidate) || plan.ClearsFilter(candidate), Subset(filterRows), comparisonReason));
        }
        if (filters.Count == 0) filters.Add(MeasureMarkJudge.Filters(probe, witness, relevant, filterRows: filterRows, comparisonReason: comparisonReason));
        var filterMark = MeasureMarkJudge.Combine("Filters", filters);
        // A row count cannot check this formula's filters: the mark does not apply (d1 s3), unlike a probe that failed.
        if (filterMark.State == "not run" && comparisonReason != null && filterMark.Sentence == comparisonReason) filterMark.ReasonCode = "filters_no_rows";
        if (filterMark.State == "passed")
        {
            filterMark.Sentence = MeasureMarkJudge.FilterFinding(probe);
            filterMark = MeasureMarkJudge.FilterIntent(filterMark, probe, plan.FilterClears, run.After.FiltersOnPurpose.Contains(run.Reference),
                formula.FormatString);
        }
        SetMark(run, filterMark);
        ProbeResult factRows = null;
        var checkBlanks = !plan.UsesTimeLogic || dataDate != null;
        if (checkBlanks && probe.Scenarios.Any(s => s.Status == "ok" && s.Rows.Any(r => r.Blank || r.V == null || r.V is DBNull)))
        {
            var expression = factRowsExpression ?? run.After.FactRowsExpression(run.Reference);
            if (expression != null)
            {
                var spec = await BuildQuerySpecAsync(run.Live, null).ConfigureAwait(false);
                var dates = await ProbeDateColumnsAsync(allScenarios).ConfigureAwait(false);
                var counts = new List<ScenarioEvidence>();
                // One grouped count per context batch, with the same keys and filters. No per-cell or additivity queries.
                using var factsOnly = run.Live.WithMarkQueries(q => q);
                foreach (var scenario in allScenarios.Where(s => probe.Scenarios.Any(e => e.Name == s.Name && e.Status == "ok" && e.Rows.Length > 0)))
                {
                    var counted = dataDate != null && MarkUsesPreviousYear(run) ? new ProbeScenario {
                        Name = scenario.Name, GroupBy = scenario.GroupBy,
                        Filters = scenario.Filters.Where(f => f != dataDate.Filter)
                            .Append(MarkYearFilter(dataDate.Filter.Column, dataDate.Start.AddYears(-1))).ToArray()
                    } : scenario;
                    counts.Add(await RunScenarioAsync(run.Live, expression, counted, 64, spec, dates,
                        run.After.ContextModel, checkAdditivity: false).ConfigureAwait(false));
                }
                factRows = new ProbeResult { Status = "ok", Scenarios = counts.ToArray() };
            }
        }
        var contexts = MeasureMarkJudge.Contexts(probe, factRows, checkBlanks);
        if (dataDate != null) contexts.Sentence = "Checked " + dataDate.Range + ". " + contexts.Sentence;
        SetMark(run, contexts);
        run.TotalsProbes = TotalProbeBuilder.From(plan, probe);
        run.ContextQueries = selected.Select(s =>
        {
            var evidence = s.Scenarios.Select(sc => probe.Scenarios.FirstOrDefault(e => e.Name == sc.Name && e.Status == "ok"))
                .FirstOrDefault(e => e != null);
            return evidence == null ? null : new MarkContextQuery(s.Candidate.Id,
                s.Candidate.Shape == ContextShape.GrandTotal ? "Grand total" : MeasureMarkJudge.Place(evidence.Name), evidence.Dax);
        }).Where(q => q != null).ToArray();
    }
}
