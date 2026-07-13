using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper;

namespace Semanticus.Analysis
{
    /// <summary>
    /// Deterministic, low-risk remediations behind "Apply all safe fixes". These mutate the model
    /// and MUST be called inside the engine's MutateAsync batch so they are one undoable step and
    /// fire change notifications. Safe-by-default: no renames, no description content, no structural
    /// (bidirectional / date-table) changes — those stay suggestions/AI-content.
    /// </summary>
    public static class SafeFixes
    {
        public static List<string> Apply(Model m)
        {
            var applied = new List<string>();
            // Honour waivers: a SafeFix finding the user consciously ACCEPTED must NOT be auto-fixed here (else the
            // bulk pass silently reverts an audited decision). Each step's rule id matches the corresponding AIR rule;
            // a rule-level waiver is covered automatically by WaiverStore.Match's IsRuleLevel branch. Mirrors bpa_fix_all.
            var waivers = WaiverStore.Load(m);
            bool Waived(string ruleId, Column c) => WaiverStore.Match(waivers, "air", ruleId, Refs.For(c)) != null;

            // 1. Hide foreign-key (relationship "from") columns — excluded from AI grounding once hidden.
            foreach (var c in m.Relationships.OfType<SingleColumnRelationship>().Select(r => r.FromColumn)
                         .Where(c => c != null).Distinct().ToList())
            {
                if (Waived("VIS-FK", c)) continue;
                if (!c.IsHidden) { c.IsHidden = true; applied.Add($"Hid FK column [{c.Table?.Name}].[{c.Name}]"); }
            }

            // 2. SummarizeBy = None on key / relationship-endpoint columns (they should not auto-aggregate).
            var endpoints = m.Relationships.OfType<SingleColumnRelationship>()
                .SelectMany(r => new[] { r.FromColumn, r.ToColumn }).Where(c => c != null).ToHashSet();
            foreach (var c in m.AllColumns.Where(c => c.Type != ColumnType.RowNumber).ToList())
            {
                if (Waived("FMT-SUMMARIZE", c)) continue;
                if ((c.IsKey || endpoints.Contains(c)) && c.SummarizeBy != AggregateFunction.None && c.SummarizeBy != AggregateFunction.Default)
                {
                    c.SummarizeBy = AggregateFunction.None;
                    applied.Add($"Set SummarizeBy=None on [{c.Table?.Name}].[{c.Name}]");
                }
            }

            // 3. Data category on obvious geographic columns.
            foreach (var c in m.AllColumns.Where(c => !c.IsHidden && string.IsNullOrEmpty(c.DataCategory)).ToList())
            {
                if (Waived("CAT-GEO", c)) continue;
                var cat = ReadinessRuleSet.GeoCategory(c.Name);
                if (cat != null) { c.DataCategory = cat; applied.Add($"Set DataCategory={cat} on [{c.Table?.Name}].[{c.Name}]"); }
            }

            // 4. SummarizeBy = None on VISIBLE numeric columns whose NAME is a non-additive identifier (year, number,
            // postal code, …) and aren't keys/relationship endpoints (those are step 2) — summing them is meaningless.
            // Mirrors the SUMMARIZE-DIMENSION rule's population/fix. Numeric Default resolves to Sum, so we fix it too.
            foreach (var c in m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber
                         && (c.DataType == DataType.Int64 || c.DataType == DataType.Double || c.DataType == DataType.Decimal)
                         && !c.IsKey && !endpoints.Contains(c) && ReadinessRuleSet.IsNonAdditiveDimensionName(c.Name)).ToList())
            {
                if (Waived("SUMMARIZE-DIMENSION", c)) continue;
                if (c.SummarizeBy != AggregateFunction.None)
                {
                    c.SummarizeBy = AggregateFunction.None;
                    applied.Add($"Set SummarizeBy=None on non-additive [{c.Table?.Name}].[{c.Name}]");
                }
            }

            return applied;
        }
    }
}
