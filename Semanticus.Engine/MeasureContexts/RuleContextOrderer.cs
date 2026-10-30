using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine.MeasureContexts;

/// <summary>Stable rule order. Totals, relationship paths and control dimensions keep slots under the cap.</summary>
public sealed class RuleContextOrderer : IContextOrderer
{
    public const int MaximumContexts = 15;
    public const int MinimumContexts = 8;

    public IReadOnlyList<ContextCandidate> Order(ContextOrderRequest request)
    {
        var candidates = request.Candidates.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).OrderBy(c => c.Priority).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
        var totals = candidates.Where(c => c.Role == ContextRole.Total).ToArray();
        var controls = candidates.Where(c => c.Role == ContextRole.Control)
            .GroupBy(c => c.Column.Table, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(2).ToArray();
        var relationships = candidates.Where(c => c.Role == ContextRole.Related && c.RelationshipPath.Length > 0)
            .GroupBy(c => c.Column.Table, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(2).ToArray();
        var rest = candidates.Where(c => c.Role != ContextRole.Total && !controls.Contains(c) && !relationships.Contains(c))
            .Take(MaximumContexts - totals.Length - controls.Length - relationships.Length);
        return rest.Concat(relationships).OrderBy(c => c.Priority).ThenBy(c => c.Id, StringComparer.Ordinal)
            .Concat(controls).Concat(totals).ToArray();
    }
}
