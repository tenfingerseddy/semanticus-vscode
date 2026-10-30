using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Decisions;

namespace Semanticus.Engine;

// J1 questions.py tot_behaviour(v1) and tot_fit(v4). Do not change the measured wording.
public static class TotalsQuestions
{
    public const string TypePoint = DecisionRegistry.TotalsType;
    public const string FitPoint = DecisionRegistry.TotalsFit;
    public static readonly IReadOnlyList<DecisionOption> Rules = new[] {
        new DecisionOption("sum_of_rows", "The measure adds up an amount or a count over lines that meet fixed conditions (a filter such as a colour, a category list, a price test or a date test inside CALCULATE still adds up), so the total is the sum of the rows"),
        new DecisionOption("recomputed_at_total", "The result is not additive: a ratio, share, growth, rolling or to-date figure, top N, median, a value capped or tiered per row, or an amount added once per cell, so the total is worked out again and is not the sum of the rows"),
        new DecisionOption("last_value_over_time", "The total shows the value at the last period (a balance, snapshot or running total), never the sum of the periods"),
        new DecisionOption("first_value_over_time", "The total shows the value at the first date or period (an opening balance or the first day), never the sum of the periods"),
        new DecisionOption("distinct_count", "The measure counts distinct items (customers, stores, categories, brands), so the total is lower than the sum of the rows when an item appears in more than one row"),
        new DecisionOption("average", "The measure averages a quantity over lines or items (AVERAGE, AVERAGEX, a daily or monthly average), so the total is an average over all of them and lies between the smallest and largest row"),
        new DecisionOption("blank_at_total", "The total is blank on purpose")
    };
    public static readonly IReadOnlyList<DecisionQuestion> TypeQuestions = new[] {
        new DecisionQuestion("rule", DecisionQuestionKind.Choice,
            "When `name` is shown in a table with a total row, which rule should the total follow, given `description` and `formula`?", Rules)
    };
    public static readonly IReadOnlyList<DecisionQuestion> FitQuestions = new[] {
        new DecisionQuestion("fits", DecisionQuestionKind.Noul,
            "Does `probe.total` follow `expected_total_rule`, given the rows and facts in `probe`? A recalculated total is not the plain mean of the row values.",
            new[] { new DecisionOption("yes", "The total follows the expected rule"),
                new DecisionOption("no", "The total follows a different rule, for example it is the sum of rows, the mean of the rows, equals one row, or is blank") })
    };
    public static Dictionary<string, object> TypeSnapshot(TotalsInput input) => new() {
        ["name"] = input.Name, ["description"] = input.Description, ["formula"] = input.Formula
    };
    public static Dictionary<string, object> FitSnapshot(TotalsInput input, TotalProbe probe, string rule)
    {
        var state = TypeSnapshot(input);
        // DecisionDigest accepts primitive dictionaries and lists, never anonymous CLR objects.
        state["probe"] = new Dictionary<string, object> {
            ["grouped_by"] = probe.GroupedBy,
            ["rows"] = probe.Rows.Select(r => new Dictionary<string, object> { ["member"] = r.Member, ["value"] = r.Value }).ToArray(),
            ["total"] = probe.Total, ["facts"] = Facts(probe)
        };
        state["expected_total_rule"] = Rules.Single(r => r.Id == rule).Criterion;
        return state;
    }

    public static Dictionary<string, object> Facts(TotalProbe probe)
    {
        var values = probe.Rows.Where(r => r.Value.HasValue).Select(r => r.Value.Value).ToArray();
        var total = probe.Total;
        // Match build.py probe_facts exactly; v4 adds a separate tolerance for the mean.
        static bool Close(double? a, double? b) => a.HasValue && b.HasValue
            && Math.Abs(a.Value - b.Value) <= 1e-9 * Math.Max(1, Math.Abs(b.Value)) + 1e-9;
        static double Format(double value) => Math.Round(value,
            Math.Abs(value) >= 1e6 ? 0 : Math.Abs(value) >= 1000 ? 2 : Math.Abs(value) >= 10 ? 3 : 4);
        var mean = values.Length > 0 ? (double?)values.Average() : null;
        var facts = new Dictionary<string, object> {
            ["sum_of_rows"] = values.Length > 0 ? Format(values.Sum()) : null,
            ["total_is_blank"] = !total.HasValue,
            ["mean_of_rows"] = mean.HasValue ? Math.Round(mean.Value, 4) : null,
            ["total_equals_mean_of_rows"] = total.HasValue && mean.HasValue
                && Math.Abs(total.Value - mean.Value) <= .002 * Math.Max(Math.Abs(mean.Value), 1e-9)
        };
        if (total.HasValue && values.Length > 0) {
            facts["total_equals_sum_of_rows"] = Close(values.Sum(), total);
            facts["total_equals_one_of_the_rows"] = values.Any(v => Close(v, total));
            facts["total_equals_last_row"] = Close(probe.Rows.LastOrDefault()?.Value, total);
            facts["total_equals_first_row"] = Close(probe.Rows.FirstOrDefault()?.Value, total);
            facts["total_is_between_smallest_and_largest_row"] = values.Min() <= total && total <= values.Max();
        }
        return facts;
    }
}
