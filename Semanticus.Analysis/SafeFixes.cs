using System.Collections.Generic;
using System.Linq;
using TabularEditor.TOMWrapper;

namespace Semanticus.Analysis
{
    /// <summary>
    /// Deterministic, low-risk remediations behind "Apply all safe fixes". These mutate the model
    /// and MUST be called inside the engine's MutateAsync batch so they are one undoable step and
    /// fire change notifications. Safe-by-default: no renames, no description content, no structural
    /// (bidirectional / date-table) changes — those stay suggestions/AI-content. A dimension's row label is a
    /// flag on one column, so it is here.
    /// </summary>
    public static class SafeFixes
    {
        /// <summary>Every safe fix, which is what apply_safe_fixes asks for.</summary>
        public static List<string> Apply(Model m) => Apply(m, null, null);

        /// <summary>The safe fixes, narrowed to <paramref name="onlyRules"/> when it is not null.
        ///
        /// The narrowing exists because the Model Plan groups its safe-fix rows BY RULE CATEGORY, and a person
        /// who starts one row is choosing that row and not the whole sweep. Each step below already names the
        /// rule it implements (its waiver check does), so gating the step on that same id applies exactly the
        /// findings the chosen row was built from and leaves every other row alone, including one somebody
        /// deliberately set aside. Null means no narrowing.</summary>
        public static List<string> Apply(Model m, ISet<string> onlyRules) => Apply(m, onlyRules, null);

        /// <summary>The same, leaving alone every (rule, column) that <paramref name="decidedElsewhere"/> names. The Model
        /// Plan passes the columns a live Jev family has decided (Plan/JevFamilies), so a rule does not write a column
        /// whose setting that family chose, in either direction. Null means nothing is decided elsewhere.</summary>
        public static List<string> Apply(Model m, ISet<string> onlyRules, System.Func<string, Column, bool> decidedElsewhere)
            => Apply(m, onlyRules, decidedElsewhere, null);

        /// <summary>As above, and a change <paramref name="declined"/> names is left alone. A waiver declines a CHANGE,
        /// not a rule id: the change is (object ref, property), and a person who accepted the Model quality twin of a
        /// step here (HIDE_FOREIGN_KEYS for VIS-FK, NUMERIC_COLUMN_SUMMARIZE_BY for FMT-SUMMARIZE) has declined the
        /// same write. The engine works the set out from what each accepted finding's fix writes; this class only asks,
        /// with the property names the plan's lines use ("isHidden", "summarizeBy", "dataCategory").</summary>
        public static List<string> Apply(Model m, ISet<string> onlyRules, System.Func<string, Column, bool> decidedElsewhere,
                                         System.Func<string, string, bool> declined)
        {
            bool Runs(string ruleId) => onlyRules == null || onlyRules.Contains(ruleId);

            var applied = new List<string>();
            // Honour waivers: a SafeFix finding the user consciously ACCEPTED must NOT be auto-fixed here (else the
            // bulk pass silently reverts an audited decision). Each step's rule id matches the corresponding AIR rule;
            // a rule-level waiver is covered automatically by WaiverStore.Match's IsRuleLevel branch. Mirrors apply_safe_fixes with system='bpa'.
            var waivers = WaiverStore.Load(m);
            bool Waived(string ruleId, Column c, string property) =>
                WaiverStore.Match(waivers, "air", ruleId, Refs.For(c)) != null || (decidedElsewhere != null && decidedElsewhere(ruleId, c))
                || (declined?.Invoke(Refs.For(c), property) ?? false);

            // Shared by steps 2 and 4, so it is read once and outside both gates: a relationship endpoint is a
            // fact about the model, not a fix.
            var endpoints = m.Relationships.OfType<SingleColumnRelationship>()
                .SelectMany(r => new[] { r.FromColumn, r.ToColumn }).Where(c => c != null).ToHashSet();

            // 1. Hide foreign-key (relationship "from") columns — excluded from AI grounding once hidden.
            if (Runs("VIS-FK"))
            {
                foreach (var c in m.Relationships.OfType<SingleColumnRelationship>().Select(r => r.FromColumn)
                             .Where(c => c != null).Distinct().ToList())
                {
                    if (Waived("VIS-FK", c, "isHidden")) continue;
                    if (!c.IsHidden) { c.IsHidden = true; applied.Add($"Hid FK column [{c.Table?.Name}].[{c.Name}]"); }
                }
            }

            // 2. SummarizeBy = None on key / relationship-endpoint columns (they should not auto-aggregate). The same
            // population and the same test as the FMT-SUMMARIZE rule, so a numeric key left at Default (which sums) is fixed.
            if (Runs("FMT-SUMMARIZE"))
            {
                foreach (var c in m.AllColumns.Where(c => c.Type != ColumnType.RowNumber).ToList())
                {
                    if (Waived("FMT-SUMMARIZE", c, "summarizeBy")) continue;
                    if ((c.IsKey || endpoints.Contains(c)) && ReadinessRuleSet.AutoAggregates(c))
                    {
                        c.SummarizeBy = AggregateFunction.None;
                        applied.Add($"Set SummarizeBy=None on [{c.Table?.Name}].[{c.Name}]");
                    }
                }
            }

            // 3. Data category on obvious geographic columns.
            if (Runs("CAT-GEO"))
            {
                foreach (var c in m.AllColumns.Where(c => !c.IsHidden && string.IsNullOrEmpty(c.DataCategory)).ToList())
                {
                    if (Waived("CAT-GEO", c, "dataCategory")) continue;
                    var cat = ReadinessRuleSet.GeoCategory(c.Name);
                    if (cat != null) { c.DataCategory = cat; applied.Add($"Set DataCategory={cat} on [{c.Table?.Name}].[{c.Name}]"); }
                }
            }

            // 4. SummarizeBy = None on VISIBLE numeric columns whose NAME is a non-additive identifier (year, number,
            // postal code, …) and aren't keys/relationship endpoints (those are step 2) — summing them is meaningless.
            // Mirrors the SUMMARIZE-DIMENSION rule's population/fix. Numeric Default resolves to Sum, so we fix it too.
            if (Runs("SUMMARIZE-DIMENSION"))
            {
                foreach (var c in m.AllColumns.Where(c => !c.IsHidden && c.Type != ColumnType.RowNumber
                             && (c.DataType == DataType.Int64 || c.DataType == DataType.Double || c.DataType == DataType.Decimal)
                             && !c.IsKey && !endpoints.Contains(c) && ReadinessRuleSet.IsNonAdditiveDimensionName(c.Name)).ToList())
                {
                    if (Waived("SUMMARIZE-DIMENSION", c, "summarizeBy")) continue;
                    if (c.SummarizeBy != AggregateFunction.None)
                    {
                        c.SummarizeBy = AggregateFunction.None;
                        applied.Add($"Set SummarizeBy=None on non-additive [{c.Table?.Name}].[{c.Name}]");
                    }
                }
            }

            // 5. The row label on a dimension that has none (DIM-PRIMARY-NAME): the column that names each row, marked
            // IsDefaultLabel. One property on one column, undoable; the column is the one the finding names.
            if (Runs("DIM-PRIMARY-NAME"))
            {
                foreach (var (t, label, clean) in ReadinessRuleSet.DimensionRowLabels(m).ToList())
                {
                    if (clean || Waived("DIM-PRIMARY-NAME", label, "isDefaultLabel")) continue;
                    MarkRowLabel(label);
                    applied.Add($"Set [{t.Name}].[{label.Name}] as the row label of '{t.Name}'");
                }
            }

            return applied;
        }

        /// <summary>Make <paramref name="label"/> its table's one row label: mark it, and clear the mark from every other
        /// column of the table (a hidden column still marked from an earlier design counts for nothing, since Copilot
        /// ignores hidden columns, and leaving it would give the table two row labels). Every route that sets
        /// DIM-PRIMARY-NAME's fix calls this. Must run inside the engine's MutateAsync batch, so it is one undo step.
        /// Returns whether anything changed.</summary>
        public static bool MarkRowLabel(Column label)
        {
            var changed = false;
            foreach (var other in label.Table.Columns.Where(c => !ReferenceEquals(c, label) && c.IsDefaultLabel).ToList())
            {
                other.IsDefaultLabel = false;
                changed = true;
            }
            if (!label.IsDefaultLabel) { label.IsDefaultLabel = true; changed = true; }
            return changed;
        }
    }
}
