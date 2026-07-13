using System.Linq;
using Semanticus.Analysis;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The deterministic DAX best-practice lint. The load-bearing tests are the FALSE-POSITIVE guards — the
    /// whole reason this runs on the token path and not <c>Expression.Contains()</c>: a function name inside a comment,
    /// a string literal, or a <c>[bracketed name]</c> must NOT trip a rule (that's what got SYN-COLLIDE cut).</summary>
    public sealed class DaxLintTests
    {
        private static bool Has(string dax, string ruleId) => DaxLint.Analyze(dax).Findings.Any(f => f.RuleId == ruleId);

        // With a model context (real table names) the identity-dependent rules unlock; without it they stand down.
        private static bool HasCtx(string dax, string ruleId, params string[] tables)
        {
            var ctx = new DaxLintContext();
            foreach (var t in tables) ctx.TableNames.Add(t);
            return DaxLint.Analyze(dax, ctx).Findings.Any(f => f.RuleId == ruleId);
        }

        // ---- detection (true positives) ----
        [Fact] public void Flags_iferror() => Assert.True(Has("IFERROR ( SUM(Sales[Amt]) / SUM(Sales[Qty]), 0 )", "avoid-iferror"));
        [Fact] public void Flags_iserror() => Assert.True(Has("IF ( ISERROR ( [x] ), 0, [x] )", "avoid-iferror"));
        [Fact] public void Flags_error_function() => Assert.True(Has("IF ( [x] < 0, ERROR ( \"neg\" ), [x] )", "no-error-function"));
        [Fact] public void Flags_earlier() => Assert.True(Has("SUMX ( Sales, Sales[Amt] * EARLIER ( Sales[Rate] ) )", "prefer-var-over-earlier"));
        [Fact] public void Flags_columns_in_summarize() => Assert.True(Has("SUMMARIZE ( Sales, Sales[Cat], \"Tot\", SUM(Sales[Amt]) )", "no-columns-inside-summarize"));
        [Fact] public void Flags_not_equals_blank() => Assert.True(Has("IF ( [Sales] <> BLANK(), [Sales], 0 )", "isblank-not-equals-blank"));

        // ---- false-positive guards (the point of the token path) ----
        [Fact] public void Ignores_iferror_in_a_comment() => Assert.False(Has("// avoid IFERROR here\nSUM ( Sales[Amt] )", "avoid-iferror"));
        [Fact] public void Ignores_iferror_in_a_block_comment() => Assert.False(Has("/* IFERROR( is bad */ SUM ( Sales[Amt] )", "avoid-iferror"));
        [Fact] public void Ignores_iferror_in_a_string_literal() => Assert.False(Has("CONCATENATE ( \"IFERROR(\", \"x\" )", "avoid-iferror"));
        [Fact] public void Ignores_iferror_in_a_bracketed_name() => Assert.False(Has("[IFERROR Rate] * 2", "avoid-iferror"));
        [Fact] public void Ignores_error_when_only_iferror_present() => Assert.False(Has("IFERROR ( [x], 0 )", "no-error-function"));
        [Fact] public void Ignores_group_only_summarize() => Assert.False(Has("SUMMARIZE ( Sales, Sales[Cat], Sales[Sub] )", "no-columns-inside-summarize"));
        [Fact] public void Ignores_summarize_with_rollup() => Assert.False(Has("SUMMARIZE ( Sales, ROLLUP ( Sales[Cat] ), \"Tot\", SUM(Sales[Amt]) )", "no-columns-inside-summarize"));
        [Fact] public void Ignores_var_assignment_of_blank() => Assert.False(Has("VAR z = BLANK() RETURN z + 1", "isblank-not-equals-blank"));

        // ---- clean measure → no findings ----
        [Fact] public void Clean_measure_has_no_findings() => Assert.Empty(DaxLint.Analyze("DIVIDE ( SUM(Sales[Amt]), SUM(Sales[Qty]) )").Findings);

        // ---- no-iferror-for-division — a BARE division trapped by IFERROR / IF(ISERROR(…)) → DIVIDE ----
        [Fact] public void Division_iferror_flagged() => Assert.True(Has("IFERROR(SUM(S[A])/SUM(S[Q]), 0)", "no-iferror-for-division"));
        [Fact] public void Division_iserror_flagged() => Assert.True(Has("IF(ISERROR([A]/[B]), 0, [A]/[B])", "no-iferror-for-division"));
        [Fact] public void Division_not_flagged_when_extra_top_level_op() => Assert.False(Has("IFERROR(SUM(S[A])/SUM(S[Q]) + 1, 0)", "no-iferror-for-division")); // not a bare division
        [Fact] public void Division_not_flagged_when_already_divide() => Assert.False(Has("IFERROR(DIVIDE([A],[B]), 0)", "no-iferror-for-division"));

        // ---- no-iferror-for-search — SEARCH/FIND trapped as the ROOT of the IFERROR arg ----
        [Fact] public void Search_iferror_flagged() => Assert.True(Has("IFERROR(SEARCH(\"x\", T[C]), 0)", "no-iferror-for-search"));
        [Fact] public void Find_iferror_flagged() => Assert.True(Has("IFERROR(FIND(\"x\", T[C]), 0)", "no-iferror-for-search"));
        [Fact] public void Search_not_flagged_when_nested_deeper() => Assert.False(Has("IFERROR(1 + SEARCH(\"x\", T[C]), 0)", "no-iferror-for-search")); // SEARCH is not the root

        // ---- divide-instead-of-if-zero-guard — hand-rolled guard where denom ≡ guarded expr ----
        [Fact] public void DivideGuard_not_equal_zero_flagged() => Assert.True(Has("IF(SUM(S[Q]) <> 0, SUM(S[A]) / SUM(S[Q]))", "divide-instead-of-if-zero-guard"));
        [Fact] public void DivideGuard_isblank_flagged() => Assert.True(Has("IF(ISBLANK([D]), BLANK(), [N]/[D])", "divide-instead-of-if-zero-guard"));
        [Fact] public void DivideGuard_reversed_zero_equals_flagged() => Assert.True(Has("IF(0 = SUM(S[Q]), BLANK(), SUM(S[A])/SUM(S[Q]))", "divide-instead-of-if-zero-guard"));
        [Fact] public void DivideGuard_not_flagged_when_denominator_differs() => Assert.False(Has("IF(SUM(S[Q]) <> 0, SUM(S[A])/SUM(S[X]))", "divide-instead-of-if-zero-guard"));
        [Fact] public void DivideGuard_not_flagged_when_guard_is_a_different_flag() => Assert.False(Has("IF([Flag] <> 0, [A]/[B])", "divide-instead-of-if-zero-guard")); // guarded [Flag] != denom [B]

        // ---- var-not-a-live-alias-context-shift — CALCULATE(<scalar VAR>, <modifier>) ----
        [Fact] public void VarAlias_scalar_var_with_modifier_flagged() => Assert.True(Has("VAR s = [Sales] RETURN CALCULATE(s, SAMEPERIODLASTYEAR('Date'[Date]))", "var-not-a-live-alias-context-shift"));
        [Fact] public void VarAlias_not_flagged_when_var_wrapped_in_call() => Assert.False(Has("VAR t = VALUES(Sales[C]) RETURN CALCULATE(COUNTROWS(t), ALL(Sales))", "var-not-a-live-alias-context-shift")); // first arg is a call, not a bare var
        [Fact] public void VarAlias_not_flagged_without_vars() => Assert.False(Has("CALCULATE([Sales], ALL(Sales))", "var-not-a-live-alias-context-shift"));
        [Fact] public void VarAlias_not_flagged_when_var_not_in_expr_slot() => Assert.False(Has("VAR s = [Sales] RETURN CALCULATE([Other], ALL(T))", "var-not-a-live-alias-context-shift"));
        [Fact] public void VarAlias_not_flagged_without_modifier_arg() => Assert.False(Has("VAR s = [Sales] RETURN CALCULATE(s)", "var-not-a-live-alias-context-shift"));

        // ---- var-unused — declared but never referenced (collision with a table ref reads as a use) ----
        [Fact] public void VarUnused_flagged_and_names_the_var() =>
            Assert.Contains("b", DaxLint.Analyze("VAR a = 1 VAR b = 2 RETURN a").Findings.First(f => f.RuleId == "var-unused").Message);
        [Fact] public void VarUnused_not_flagged_when_referenced() => Assert.False(Has("VAR a = 1 RETURN a + 1", "var-unused"));
        [Fact] public void VarUnused_not_flagged_on_name_collision_with_table() => Assert.False(Has("VAR Sales = 1 RETURN SUMX(Sales, [x])", "var-unused")); // the table word reads as a use — deliberately safe

        // ---- selectedvalue-over-if-hasonevalue-values — same column mandatory ----
        [Fact] public void SelectedValue_values_form_flagged() => Assert.True(Has("IF(HASONEVALUE('D'[Year]), VALUES('D'[Year]), \"All\")", "selectedvalue-over-if-hasonevalue-values"));
        [Fact] public void SelectedValue_distinct_form_flagged() => Assert.True(Has("IF(HASONEVALUE('D'[Year]), DISTINCT('D'[Year]), \"All\")", "selectedvalue-over-if-hasonevalue-values"));
        [Fact] public void SelectedValue_not_flagged_on_different_columns() => Assert.False(Has("IF(HASONEVALUE('D'[Year]), VALUES('D'[Month]))", "selectedvalue-over-if-hasonevalue-values"));

        // ---- distinctcount-over-countrows-distinct-values — COLUMN arg only ----
        [Fact] public void DistinctCount_over_distinct_flagged() => Assert.True(Has("COUNTROWS(DISTINCT(S[Cust]))", "distinctcount-over-countrows-distinct-values"));
        [Fact] public void DistinctCount_over_values_flagged() => Assert.True(Has("COUNTROWS(VALUES('Sales'[Cust]))", "distinctcount-over-countrows-distinct-values"));
        [Fact] public void DistinctCount_not_flagged_on_table_arg() => Assert.False(Has("COUNTROWS(DISTINCT(Sales))", "distinctcount-over-countrows-distinct-values"));
        [Fact] public void DistinctCount_not_flagged_on_filter() => Assert.False(Has("COUNTROWS(FILTER(Sales, Sales[A] > 0))", "distinctcount-over-countrows-distinct-values"));

        // ---- dax-prefer-removefilters-over-all-modifier — ALL as a filter MODIFIER (arg 2..N) ----
        [Fact] public void RemoveFilters_all_modifier_flagged() => Assert.True(Has("CALCULATE([S], ALL(P[Brand]))", "dax-prefer-removefilters-over-all-modifier"));
        [Fact] public void RemoveFilters_all_through_keepfilters_flagged() => Assert.True(Has("CALCULATE([S], KEEPFILTERS(ALL(P[Brand])))", "dax-prefer-removefilters-over-all-modifier"));
        [Fact] public void RemoveFilters_not_flagged_when_all_in_expression_slot() => Assert.False(Has("CALCULATE(SUMX(ALL(Sales), [x]))", "dax-prefer-removefilters-over-all-modifier")); // ALL is inside the first (expression) arg
        [Fact] public void RemoveFilters_not_flagged_standalone_all() => Assert.False(Has("COUNTROWS(ALL(Sales))", "dax-prefer-removefilters-over-all-modifier"));
        [Fact] public void RemoveFilters_not_flagged_when_all_is_filter_table() => Assert.False(Has("CALCULATE([S], FILTER(ALL(T), T[A] > 0))", "dax-prefer-removefilters-over-all-modifier")); // root is FILTER, not ALL

        // ---- calculate-bare-table-filter — context-gated (real table names) ----
        [Fact] public void BareTable_flagged_with_ctx() => Assert.True(HasCtx("CALCULATE([M], Sales)", "calculate-bare-table-filter", "Sales"));
        [Fact] public void BareTable_quoted_flagged_with_ctx() => Assert.True(HasCtx("CALCULATE([M], 'Sales Data')", "calculate-bare-table-filter", "Sales Data"));
        [Fact] public void BareTable_not_flagged_without_ctx() => Assert.False(Has("CALCULATE([M], Sales)", "calculate-bare-table-filter")); // no tables → stands down
        [Fact] public void BareTable_not_flagged_when_word_is_a_var() => Assert.False(HasCtx("VAR Sales = FILTER(T, T[A]>0) RETURN CALCULATE([M], Sales)", "calculate-bare-table-filter", "Sales")); // the var wins over the table name
        [Fact] public void BareTable_not_flagged_when_function_wrapped() => Assert.False(HasCtx("CALCULATE([M], VALUES(Sales))", "calculate-bare-table-filter", "Sales"));
        [Fact] public void BareTable_not_flagged_when_word_is_not_a_known_table() => Assert.False(HasCtx("CALCULATE([M], Foo)", "calculate-bare-table-filter", "Sales")); // no identity → no flag

        // ---- dax-measure-in-calculate-boolean-predicate — bare [Measure] in a boolean filter ----
        [Fact] public void MeasurePredicate_bare_measure_flagged() => Assert.True(Has("CALCULATE([S], [Total Qty] > 100)", "dax-measure-in-calculate-boolean-predicate"));
        [Fact] public void MeasurePredicate_not_flagged_on_qualified_column() => Assert.False(Has("CALCULATE([S], Sales[Qty] > 100)", "dax-measure-in-calculate-boolean-predicate"));
        [Fact] public void MeasurePredicate_not_flagged_on_correct_filter_form() => Assert.False(Has("CALCULATE([S], FILTER(VALUES(P[P]), [Qty] > 100))", "dax-measure-in-calculate-boolean-predicate")); // comparison is call-wrapped, not top-level
        [Fact] public void MeasurePredicate_not_flagged_on_quoted_qualified_column() => Assert.False(Has("CALCULATE([S], 'Tbl'[Col] = 5)", "dax-measure-in-calculate-boolean-predicate"));

        // ---- regressions ----
        // The isblank-not-equals-blank finding is a warning (not info).
        [Fact] public void IsBlank_severity_is_warning() =>
            Assert.Equal("warning", DaxLint.Analyze("IF ( [Sales] <> BLANK(), [Sales], 0 )").Findings.First(f => f.RuleId == "isblank-not-equals-blank").Severity);
        // Two SUMMARIZE calls where only the SECOND carries a string-literal extension — pins the AllCallOpens fix
        // (a first-call-only walk would miss it).
        [Fact] public void Summarize_second_call_with_extension_flagged() => Assert.True(Has("UNION(SUMMARIZE(T, T[A]), SUMMARIZE(T, T[B], \"x\", SUM(T[C])))", "no-columns-inside-summarize"));

        // ---- adversarial-review fixes (2026-07-03) ----
        // A 4-arg SEARCH/FIND can't raise not-found — its IFERROR guards a DIFFERENT error class; "pass the 4th
        // argument" would be a no-op, so the specific rule must stand down (the generic avoid-iferror still fires).
        [Fact] public void Search_with_notfound_arg_not_flagged() => Assert.False(Has("IFERROR(SEARCH(\"x\", T[C], 1, -1), 0)", "no-iferror-for-search"));
        [Fact] public void Find_with_notfound_arg_not_flagged() => Assert.False(Has("IFERROR(FIND(\"x\", T[C], 1, -1), 0)", "no-iferror-for-search"));
        // The doc's carve-out: only an aggregation/CALCULATE/measure extension pays for the ADDCOLUMNS rewrite —
        // a constant-text extension column must NOT trip the rule.
        [Fact] public void Summarize_constant_extension_not_flagged() => Assert.False(Has("SUMMARIZE(Product, Product[Cat], \"Report Date\", \"2024\")", "no-columns-inside-summarize"));
        [Fact] public void Summarize_measure_ref_extension_flagged() => Assert.True(Has("SUMMARIZE(Sales, Sales[Cat], \"M\", [Margin])", "no-columns-inside-summarize"));
        // Demoted to info: the detector can't tell [M] > 100 from the contested col > [M] direction, so it may
        // surface but never carry warning weight (and the readiness rule is advisory — see BestPracticeRulesTests).
        [Fact] public void MeasurePredicate_severity_is_info() =>
            Assert.Equal("info", DaxLint.Analyze("CALCULATE([S], [Total Qty] > 100)").Findings.First(f => f.RuleId == "dax-measure-in-calculate-boolean-predicate").Severity);
    }
}
