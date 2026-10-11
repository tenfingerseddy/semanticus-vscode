using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Sol A6 findings 3 and 4, fixed as a class. Which arguments of a row-scan function read the scanned row, and which read the caller's context, is ONE table in
    /// FormulaFixup.Patched.cs (RowScanSpecs), each row cited from its Microsoft Learn page. Every case below has a column [P] on the scanned table Sales and a measure [P]
    /// on the home table Metrics. Renaming Sales[P] must rewrite the bare [P] in a per-row argument and leave the bare [P] in a caller-context argument on the measure.
    /// </summary>
    public sealed class SolFxScanArgumentTests
    {
        // (formula, the formula after Sales[P] becomes Sales[Q]). Per-row arguments change to [Q]; caller-context arguments keep [P] (the measure).
        public static IEnumerable<object[]> Cases() => new (string Before, string After)[]
        {
            // X aggregators (table, expression): only the expression is per row. MAXX/MINX variant, PERCENTILEX k: caller context.
            ("SUMX ( Sales, [P] )", "SUMX ( Sales, [Q] )"),
            ("AVERAGEX ( Sales, [P] )", "AVERAGEX ( Sales, [Q] )"),
            ("COUNTX ( Sales, [P] )", "COUNTX ( Sales, [Q] )"),
            ("COUNTAX ( Sales, [P] )", "COUNTAX ( Sales, [Q] )"),
            ("PRODUCTX ( Sales, [P] )", "PRODUCTX ( Sales, [Q] )"),
            ("MEDIANX ( Sales, [P] )", "MEDIANX ( Sales, [Q] )"),
            ("GEOMEANX ( Sales, [P] )", "GEOMEANX ( Sales, [Q] )"),
            ("STDEVX.S ( Sales, [P] )", "STDEVX.S ( Sales, [Q] )"),
            ("STDEVX.P ( Sales, [P] )", "STDEVX.P ( Sales, [Q] )"),
            ("VARX.S ( Sales, [P] )", "VARX.S ( Sales, [Q] )"),
            ("VARX.P ( Sales, [P] )", "VARX.P ( Sales, [Q] )"),
            ("MAXX ( Sales, [P], [P] )", "MAXX ( Sales, [Q], [P] )"),
            ("MINX ( Sales, [P], [P] )", "MINX ( Sales, [Q], [P] )"),
            ("PERCENTILEX.INC ( Sales, [P], [P] )", "PERCENTILEX.INC ( Sales, [Q], [P] )"),
            ("PERCENTILEX.EXC ( Sales, [P], [P] )", "PERCENTILEX.EXC ( Sales, [Q], [P] )"),
            // CONCATENATEX ( table, expression, delimiter, orderBy, order, orderBy, order ): expression and each orderBy are per row.
            ("CONCATENATEX ( Sales, [P], [P], [P], [P], [P], [P] )", "CONCATENATEX ( Sales, [Q], [P], [Q], [P], [Q], [P] )"),
            // RANKX ( table, expression, value, order, ties ): only the expression is per row.
            ("RANKX ( Sales, [P], [P], [P], [P] )", "RANKX ( Sales, [Q], [P], [P], [P] )"),
            // TOPN / SAMPLE ( n, table, orderBy, order, orderBy, order ), TOPNSKIP ( n, skip, table, orderBy, order, ... ): the orderBy expressions are per row.
            ("TOPN ( [P], Sales, [P], [P], [P], [P] )", "TOPN ( [P], Sales, [Q], [P], [Q], [P] )"),
            ("SAMPLE ( [P], Sales, [P], [P], [P], [P] )", "SAMPLE ( [P], Sales, [Q], [P], [Q], [P] )"),
            ("TOPNSKIP ( [P], [P], Sales, [P], [P], [P], [P] )", "TOPNSKIP ( [P], [P], Sales, [Q], [P], [Q], [P] )"),
            // FILTER, ADDCOLUMNS, SELECTCOLUMNS, GENERATE, GENERATEALL: the condition, the expressions, the second table are per row.
            ("FILTER ( Sales, [P] > 0 )", "FILTER ( Sales, [Q] > 0 )"),
            ("ADDCOLUMNS ( Sales, \"x\", [P], \"y\", [P] )", "ADDCOLUMNS ( Sales, \"x\", [Q], \"y\", [Q] )"),
            ("SELECTCOLUMNS ( Sales, \"x\", [P], \"y\", [P] )", "SELECTCOLUMNS ( Sales, \"x\", [Q], \"y\", [Q] )"),
            ("GENERATE ( Sales, ROW ( \"x\", [P] ) )", "GENERATE ( Sales, ROW ( \"x\", [Q] ) )"),
            ("GENERATEALL ( Sales, ROW ( \"x\", [P] ) )", "GENERATEALL ( Sales, ROW ( \"x\", [Q] ) )"),
            // SUMMARIZE (fix round 2, Y1): an expression reads the GROUP row, which holds the groupBy columns only (Microsoft's SUMMARIZE page: one row per group; dax.guide/summarize;
            // SQLBI "All the secrets of SUMMARIZE": "a row context, containing the cluster headers"). A bare name binds to a column named in the groupBy arguments; any other name is the caller's.
            // The round-1 row here asserted [Q] for an UNGROUPED Sales[P]: that was the wrong binding.
            ("SUMMARIZE ( Sales, Sales[Label], \"x\", [P] )", "SUMMARIZE ( Sales, Sales[Label], \"x\", [P] )"),
            ("SUMMARIZE ( Sales, Sales[P], \"x\", [P] )", "SUMMARIZE ( Sales, 'Sales'[Q], \"x\", [Q] )"),
            ("SUMMARIZE ( Sales, Sales[Label], Sales[P], \"x\", [P], \"y\", [Label] )", "SUMMARIZE ( Sales, Sales[Label], 'Sales'[Q], \"x\", [Q], \"y\", [Label] )"),
            ("SUMMARIZE ( Sales, ROLLUP ( Sales[P] ), \"x\", [P] )", "SUMMARIZE ( Sales, ROLLUP ( 'Sales'[Q] ), \"x\", [Q] )"),
            ("SUMMARIZE ( Sales, Other[P], \"x\", [P] )", "SUMMARIZE ( Sales, Other[P], \"x\", [P] )"),
            // GROUPBY (fix round 2, Y2): an X aggregator over CURRENTGROUP () scans the rows of GROUPBY's table argument (the GROUPBY page: CURRENTGROUP "returns the set of rows from the table argument of GROUPBY").
            ("GROUPBY ( Sales, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [P] ) )", "GROUPBY ( Sales, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [Q] ) )"),
            ("GROUPBY ( Sales, Sales[Label], \"x\", MAXX ( CURRENTGROUP (), [P], [P] ) )", "GROUPBY ( Sales, Sales[Label], \"x\", MAXX ( CURRENTGROUP (), [Q], [P] ) )"),
            ("GROUPBY ( Other, Other[P], \"x\", SUMX ( CURRENTGROUP (), [P] ) )", "GROUPBY ( Other, Other[P], \"x\", SUMX ( CURRENTGROUP (), [P] ) )"),
            // a VAR table (fix round 2, Y3): the scan of t binds to the table t's expression is over, exactly as the same expression inline would.
            ("VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [P] )", "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [Q] )"),
            ("VAR t = Sales RETURN SUMX ( t, [P] )", "VAR t = Sales RETURN SUMX ( t, [Q] )"),
            ("VAR t = ALL ( Sales ) VAR u = FILTER ( t, Sales[Amount] > 0 ) RETURN SUMX ( u, [P] )", "VAR t = ALL ( Sales ) VAR u = FILTER ( t, Sales[Amount] > 0 ) RETURN SUMX ( u, [Q] )"),
            ("VAR t = FILTER ( Other, Other[P] > 0 ) RETURN SUMX ( t, [P] )", "VAR t = FILTER ( Other, Other[P] > 0 ) RETURN SUMX ( t, [P] )"),
            ("CALCULATETABLE ( Sales, Sales[Amount] > [P] )", "CALCULATETABLE ( Sales, Sales[Amount] > [P] )"),
        }.Select(c => new object[] { c.Before, c.After });

        private static async Task<(LocalEngine Engine, SessionManager Sessions)> ModelAsync(string formula)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("ScanArgs", 1601);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable("Sales");
                sales.AddDataColumn("Label", "Label", null, DataType.String);
                sales.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
                sales.AddDataColumn("P", "P", null, DataType.Decimal);
                var other = m.AddTable("Other");
                other.AddDataColumn("P", "P", null, DataType.Decimal);
                var metrics = m.AddTable("Metrics");
                metrics.AddMeasure("P", "0.5");
                metrics.AddMeasure("Host", formula);
            });
            return (engine, sessions);
        }

        private static Task<string> HostAsync(SessionManager s) => s.Require().ReadAsync(m => m.Tables["Metrics"].Measures["Host"].Expression);

        // Lean suite (8 Oct): the default run keeps these rows; the whole matrix is the sweep.
        public static IEnumerable<object[]> CasesSample() => Cases().Where(r => new[] { "SUMX ( Sales, [P] )", "MAXX ( Sales, [P], [P] )", "RANKX ( Sales, [P], [P], [P], [P] )", "SUMMARIZE ( Sales, Sales[Label], \"x\", [P] )", "GROUPBY ( Sales, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [P] ) )" }.Contains((string)r[0]));

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(CasesSample))]
        public Task Renaming_a_scanned_column_rewrites_per_row_arguments_and_keeps_caller_context_arguments_on_the_measure_sample(string before, string after) => Renaming_a_scanned_column_rewrites_per_row_arguments_and_keeps_caller_context_arguments_on_the_measure(before, after);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Cases))]
        public async Task Renaming_a_scanned_column_rewrites_per_row_arguments_and_keeps_caller_context_arguments_on_the_measure(string before, string after)
        {
            var (engine, sessions) = await ModelAsync(before);
            using (engine)
            {
                await engine.RenameObjectAsync("column:Sales/P", "Q", "human");
                Assert.Equal(after, await HostAsync(sessions));
            }
        }

        /// <summary>A caller-context argument is resolved against the OUTER scope, as RANKX's value always was: inside SUMX ( Other, ... ) the bare [P] of PERCENTILEX's k is Other's column.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_caller_context_argument_binds_to_the_enclosing_scan()
        {
            const string formula = "SUMX ( Other, PERCENTILEX.INC ( Sales, [P], [P] ) )";
            var (engine, sessions) = await ModelAsync(formula);
            using (engine)
            {
                await engine.RenameObjectAsync("column:Sales/P", "Q", "human");
                Assert.Equal("SUMX ( Other, PERCENTILEX.INC ( Sales, [Q], [P] ) )", await HostAsync(sessions));
                await engine.RenameObjectAsync("column:Other/P", "R", "human");
                Assert.Equal("SUMX ( Other, PERCENTILEX.INC ( Sales, [Q], [R] ) )", await HostAsync(sessions));
            }
        }

        /// <summary>Y1, the check's S3: an ungrouped SUMMARIZE expression name is the caller's, so inside SUMX ( Other, .. ) it is Other's column; a grouped one is the table's.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_summarize_expression_name_that_is_not_grouped_binds_in_the_callers_scope()
        {
            var (engine, sessions) = await ModelAsync("SUMX ( Other, COUNTROWS ( SUMMARIZE ( Sales, Sales[Label], \"x\", [P] ) ) )");
            using (engine)
            {
                await engine.RenameObjectAsync("column:Other/P", "R", "human");
                Assert.Equal("SUMX ( Other, COUNTROWS ( SUMMARIZE ( Sales, Sales[Label], \"x\", [R] ) ) )", await HostAsync(sessions));
            }
        }

        /// <summary>Y1, the check's S1 as a number: renaming the ungrouped Sales[P] to the measure's new name must not move the expression onto the measure.</summary>
        [Fact]
        public async Task Renaming_an_ungrouped_column_to_a_measures_name_leaves_the_summarize_expression_on_its_measure()
        {
            var (engine, sessions) = await ModelAsync("SUMX ( SUMMARIZE ( Sales, Sales[Label], \"x\", [P] ), [x] )");
            using (engine)
            {
                await engine.RenameObjectAsync("column:Sales/P", "New P", "human");
                Assert.Equal("SUMX ( SUMMARIZE ( Sales, Sales[Label], \"x\", [P] ), [x] )", await HostAsync(sessions));
            }
        }

        /// <summary>Y3: the table behind a VAR is resolved as it would be inline, so the rename of Other[P] reaches a scan of a VAR over Other.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_scan_of_a_var_table_over_another_table_follows_that_table()
        {
            var (engine, sessions) = await ModelAsync("VAR t = FILTER ( Other, Other[P] > 0 ) RETURN SUMX ( t, [P] )");
            using (engine)
            {
                await engine.RenameObjectAsync("column:Other/P", "R", "human");
                Assert.Equal("VAR t = FILTER ( Other, 'Other'[R] > 0 ) RETURN SUMX ( t, [R] )", await HostAsync(sessions));
            }
        }

        // ---- X2 (finding 4): the KPI helper rewrite follows the BINDING of each occurrence, not the text of a token. ----

        public static IEnumerable<object[]> KpiCases() => new (string Before, string After)[]
        {
            // a bare helper and one qualified by the KPI's own host table are the helper; a column of Sales with the same name is not
            ("[_Revenue Goal] + Metrics[_Revenue Goal]", "[_Sales Revenue Goal] + Metrics[_Sales Revenue Goal]"),
            ("[_Revenue Goal] + SUM ( Sales[_Revenue Goal] )", "[_Sales Revenue Goal] + SUM ( Sales[_Revenue Goal] )"),
            ("SUM ( Sales[_Revenue Goal] ) + [_Revenue Status] + [_Revenue Trend]", "SUM ( Sales[_Revenue Goal] ) + [_Sales Revenue Status] + [_Sales Revenue Trend]"),
            // a bare name inside a scan of Sales is Sales' column; the same name after the scan is the helper
            ("SUMX ( Sales, [_Revenue Goal] ) + [_Revenue Goal]", "SUMX ( Sales, [_Revenue Goal] ) + [_Sales Revenue Goal]"),
            // a name qualified by a table that is neither the KPI's host nor has such a column is not the helper
            ("Other[_Revenue Goal] + [_Revenue Goal]", "Other[_Revenue Goal] + [_Sales Revenue Goal]"),
            // an earlier reference that this same rename rewrites does not move the helper's binding
            ("[Revenue] + Sales[_Revenue Goal] + [_Revenue Goal]", "[Sales Revenue] + Sales[_Revenue Goal] + [_Sales Revenue Goal]"),
        }.Select(c => new object[] { c.Before, c.After });

        // Lean suite (8 Oct): the default run keeps these rows; the whole matrix is the sweep.
        public static IEnumerable<object[]> KpiCasesSample() => KpiCases().Where(r => new[] { "[_Revenue Goal] + SUM ( Sales[_Revenue Goal] )" }.Contains((string)r[0]));

        [Theory]
        [MemberData(nameof(KpiCasesSample))]
        public Task A_kpi_rename_rewrites_only_the_occurrences_bound_to_its_helper_sample(string before, string after) => A_kpi_rename_rewrites_only_the_occurrences_bound_to_its_helper(before, after);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(KpiCases))]
        public async Task A_kpi_rename_rewrites_only_the_occurrences_bound_to_its_helper(string before, string after)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("KpiBinding", 1601);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable("Sales");
                sales.AddDataColumn("_Revenue Goal", "G", null, DataType.Decimal);
                sales.AddDataColumn("_Sales Revenue Goal", "H", null, DataType.Decimal);
                m.AddTable("Other").AddDataColumn("Z", "Z", null, DataType.Decimal);
                var metrics = m.AddTable("Metrics");
                var revenue = metrics.AddMeasure("Revenue", "100");
                revenue.AddKPI().TargetExpression = "50";
                metrics.AddMeasure("Host", before);
            });
            await engine.RenameObjectAsync("measure:Metrics/Revenue", "Sales Revenue", "human");
            Assert.Equal(after, await HostAsync(sessions));
        }
    }
}
