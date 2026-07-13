using System.Collections.Generic;

namespace Semanticus.Analysis
{
    /// <summary>
    /// A curated default Best Practice rule set (standard-format, expressions in the donor's Dynamic-LINQ
    /// dialect). Conservative on purpose — correctness over breadth; users can layer the full community
    /// BPARules.json on top (model-embedded rules are merged by id). Rules with a literal FixExpression
    /// are deterministically auto-fixable; the rest route to the agent. Severity: 1=info,2=warning,3=error.
    /// </summary>
    public static class BpaDefaultRules
    {
        public static IReadOnlyList<BpaRule> Rules { get; } = new List<BpaRule>
        {
            // ---- Formatting -------------------------------------------------------------------
            new BpaRule {
                ID = "FORMATTING_MEASURE_FORMAT_STRING", Name = "Visible measures should have a format string",
                Category = "Formatting", Severity = 2, Scope = "Measure",
                Expression = "not IsHidden and (FormatString = null or FormatString = \"\")",
                Description = "Measure %object% has no format string; values may render with poor precision/units.",
            },
            new BpaRule {
                ID = "FORMATTING_NUMERIC_COLUMN_FORMAT", Name = "Visible numeric columns should have a format string",
                Category = "Formatting", Severity = 1, Scope = "DataColumn, CalculatedColumn",
                Expression = "not IsHidden and SummarizeBy <> \"None\" and (DataType = \"Int64\" or DataType = \"Double\" or DataType = \"Decimal\") and (FormatString = null or FormatString = \"\")",
                Description = "Numeric column %object% has no format string.",
            },

            // ---- Maintenance (descriptions) ---------------------------------------------------
            new BpaRule {
                ID = "MAINTENANCE_MEASURE_DESCRIPTION", Name = "Visible measures should have descriptions",
                Category = "Maintenance", Severity = 2, Scope = "Measure",
                Expression = "not IsHidden and (Description = null or Description = \"\")",
                Description = "Measure %object% has no description.",
            },
            new BpaRule {
                ID = "MAINTENANCE_TABLE_DESCRIPTION", Name = "Visible tables should have descriptions",
                Category = "Maintenance", Severity = 1, Scope = "Table",
                Expression = "not IsHidden and (Description = null or Description = \"\")",
                Description = "Table %object% has no description.",
            },

            // ---- Performance ------------------------------------------------------------------
            new BpaRule {
                ID = "PERFORMANCE_BIDIRECTIONAL_RELATIONSHIP", Name = "Avoid bi-directional relationships",
                Category = "Performance", Severity = 2, Scope = "Relationship",
                Expression = "CrossFilteringBehavior = \"BothDirections\"",
                FixExpression = "CrossFilteringBehavior = OneDirection",
                Description = "Relationship %object% filters both ways (bi-directional); prefer single-direction filtering in a star schema.",
            },
            new BpaRule {
                ID = "PERFORMANCE_SUMMARIZE_KEY_COLUMN", Name = "Key columns should not summarize by default",
                Category = "Performance", Severity = 2, Scope = "DataColumn, CalculatedColumn",
                Expression = "SummarizeBy <> \"None\" and SummarizeBy <> \"Default\" and (IsKey or Name.ToLower().EndsWith(\"key\") or Name.ToLower().EndsWith(\"id\"))",
                FixExpression = "SummarizeBy = None",
                Description = "Key/identifier column %object% is summed by default; stop it summing (SummarizeBy = None).",
            },
            new BpaRule {
                ID = "PERFORMANCE_FLOATING_POINT_DATA_TYPE", Name = "Avoid floating point (Double) data types",
                Category = "Performance", Severity = 1, Scope = "DataColumn",
                Expression = "DataType = \"Double\"",
                Description = "Column %object% uses Double; prefer Decimal/Fixed for exactness and compression (review before changing).",
            },

            // ---- DAX expressions --------------------------------------------------------------
            new BpaRule {
                ID = "DAX_AVOID_IFERROR", Name = "Avoid using IFERROR",
                Category = "DAX Expressions", Severity = 2, Scope = "Measure",
                // Expression kept for TE-compat/export; TokenCheck actually evaluates it (no false-fire on a comment,
                // string, or [bracketed name] the text form can't tell apart from a real IFERROR( call).
                Expression = "Expression <> null and Expression.ToUpper().Contains(\"IFERROR\")",
                TokenCheck = "calls:IFERROR",
                Description = "Measure %object% uses IFERROR, which can hurt performance; handle errors explicitly (DIVIDE, etc.).",
            },
            new BpaRule {
                ID = "DAX_AVOID_FILTER_TABLE_REFERENCE", Name = "Avoid FILTER over a full table in measures",
                Category = "DAX Expressions", Severity = 1, Scope = "Measure",
                // Expression kept for TE-compat/export; TokenCheck evaluates it — and catches the UNQUOTED bare-table
                // case (FILTER(Sales,…)) the text "FILTER('" form structurally could not.
                Expression = "Expression <> null and Expression.ToUpper().Contains(\"FILTER('\")",
                TokenCheck = "filter-bare-table",
                Description = "Measure %object% appears to FILTER a whole table; prefer FILTER(column) or boolean filters in CALCULATE.",
            },
        };
    }
}
