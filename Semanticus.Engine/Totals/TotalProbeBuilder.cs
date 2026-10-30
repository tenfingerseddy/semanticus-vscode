using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Semanticus.Engine.MeasureContexts;

namespace Semanticus.Engine;

internal static class TotalProbeBuilder
{
    // Reuse E3's complete unfiltered groups. A sampled or failed query cannot establish a total conflict.
    internal static TotalProbe[] From(MeasureContextPlan plan, ProbeResult probe)
    {
        var result = new List<TotalProbe>();
        bool Complete(ScenarioEvidence e) => e?.Status == "ok" && !e.Truncated;
        var grand = probe.Scenarios.FirstOrDefault(s => s.Name == "grand total");
        if (!Complete(grand) || grand.Rows.Length != 1 || !Number(grand.Rows[0], out var total)) return result.ToArray();
        // Filters and Totals must agree about which contexts can split the result.
        var candidates = plan.ForMark("Totals").Where(c => !plan.ExpectsConstant(c)).ToArray();
        foreach (var candidate in candidates.Where(c => c.Column != null).DistinctBy(c => c.Column))
        {
            var axis = candidate.Column.Dax;
            var group = probe.Scenarios.FirstOrDefault(s => s.Name == "by " + axis + " (unfiltered)");
            if (!Complete(group) || group.Rows.Length == 0) continue;
            var rows = Rows(group.Rows);
            if (rows != null) result.Add(new(axis, rows, total));
        }
        foreach (var candidate in candidates.Where(c => c.Shape == ContextShape.Subtotal
            && c.Column != null && c.DetailColumn != null && !plan.ExpectsConstant(c.DetailColumn)))
        {
            var axis = candidate.Column.Dax;
            var group = probe.Scenarios.FirstOrDefault(s => s.Name == "by " + axis + " (unfiltered)");
            var detail = probe.Scenarios.FirstOrDefault(s => s.Name == "detail under " + axis);
            if (!Complete(group) || !Complete(detail)) continue;
            // One subtotal per candidate, with every detail row for that member.
            var subtotal = group.Rows.FirstOrDefault();
            if (subtotal == null || !Number(subtotal, out var value) || !subtotal.Members.TryGetValue(axis, out var member)) continue;
            var detailRows = detail.Rows.Where(r => r.Members.TryGetValue(axis, out var m) && Equals(m, member)).ToArray();
            var rows = Rows(detailRows);
            if (rows?.Length > 0) result.Add(new(candidate.DetailColumn.Dax, rows, value)
                { Context = axis + ": " + (Convert.ToString(member, CultureInfo.InvariantCulture) ?? "BLANK") });
        }
        return result.ToArray();
    }

    private static TotalProbeRow[] Rows(ProbeRow[] rows)
    {
        var result = new List<TotalProbeRow>();
        foreach (var row in rows) {
            if (!Number(row, out var value)) return null;
            result.Add(new(string.Join(", ", row.Members.Values.Select(m => Convert.ToString(m, CultureInfo.InvariantCulture) ?? "BLANK")), value));
        }
        return result.ToArray();
    }
    private static bool Number(ProbeRow row, out double? value)
    {
        value = null;
        if (row.Blank || row.V == null || row.V is DBNull) return true;
        if (row.V is bool or DateTime or DateTimeOffset) return false;
        if (!double.TryParse(Convert.ToString(row.V, CultureInfo.InvariantCulture), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) return false;
        value = number; return true;
    }
}
