using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier, m11/solfx (Sol A6 findings 3 and 4). Each case states the binding Microsoft's pages give, and fails where the rename picks another one.
    /// Model: Sales ( Label, Amount, P ), Other ( P ), Metrics ( measure P, measure New P, measure Host ). Sales[P] becomes Q or Other[P] becomes R.
    /// Sources: https://learn.microsoft.com/en-us/dax/dax-syntax-reference ("an unqualified name ... in an aggregation function that is scanning over the same table"),
    /// https://learn.microsoft.com/en-us/dax/summarize-function-dax (expression: "the value for each row in that column", the rows are the groups),
    /// https://learn.microsoft.com/en-us/dax/firstnonblank-function-dax (expression "evaluated ... for each value of column").
    /// </summary>
    public sealed class VerifySolFxTests
    {
        public static IEnumerable<object[]> Cases() => new (string Id, string Before, string Rename, string To, string After)[]
        {
            // SUMMARIZE: a bare name in an expression reads the GROUP row, which holds the groupBy columns only. P is not grouped here, so [P] is the measure.
            ("S1 summarize ungrouped", "SUMMARIZE ( Sales, Sales[Label], \"x\", [P] )", "column:Sales/P", "Q", "SUMMARIZE ( Sales, Sales[Label], \"x\", [P] )"),
            // the wrong number: Metrics has a measure [New P]; renaming the ungrouped column Sales[P] to "New P" must not move the expression onto that measure
            ("S4 summarize ungrouped, rename onto a measure name", "SUMX ( SUMMARIZE ( Sales, Sales[Label], \"x\", [P] ), [x] )", "column:Sales/P", "New P", "SUMX ( SUMMARIZE ( Sales, Sales[Label], \"x\", [P] ), [x] )"),
            ("S2 summarize grouped", "SUMMARIZE ( Sales, Sales[P], \"x\", [P] )", "column:Sales/P", "Q", "SUMMARIZE ( Sales, 'Sales'[Q], \"x\", [Q] )"),
            ("S3 summarize ungrouped in Other scan", "SUMX ( Other, COUNTROWS ( SUMMARIZE ( Sales, Sales[Label], \"x\", [P] ) ) )", "column:Other/P", "R", "SUMX ( Other, COUNTROWS ( SUMMARIZE ( Sales, Sales[Label], \"x\", [R] ) ) )"),
            // TOPN n: caller context.
            ("T1 topn n outer", "SUMX ( Other, COUNTROWS ( TOPN ( [P], Sales, Sales[Amount] ) ) )", "column:Other/P", "R", "SUMX ( Other, COUNTROWS ( TOPN ( [R], Sales, Sales[Amount] ) ) )"),
            ("T2 topn n not scanned", "SUMX ( Other, COUNTROWS ( TOPN ( [P], Sales, Sales[Amount] ) ) )", "column:Sales/P", "Q", "SUMX ( Other, COUNTROWS ( TOPN ( [P], Sales, Sales[Amount] ) ) )"),
            // CALCULATETABLE filters: caller context.
            ("C1 calculatetable filter outer", "SUMX ( Other, COUNTROWS ( CALCULATETABLE ( Sales, [P] > 0 ) ) )", "column:Other/P", "R", "SUMX ( Other, COUNTROWS ( CALCULATETABLE ( Sales, [R] > 0 ) ) )"),
            ("C2 calculatetable filter not scanned", "SUMX ( Other, COUNTROWS ( CALCULATETABLE ( Sales, [P] > 0 ) ) )", "column:Sales/P", "Q", "SUMX ( Other, COUNTROWS ( CALCULATETABLE ( Sales, [P] > 0 ) ) )"),
            ("C3 scan over calculatetable", "SUMX ( CALCULATETABLE ( Sales, Sales[Amount] > 0 ), [P] )", "column:Sales/P", "Q", "SUMX ( CALCULATETABLE ( Sales, Sales[Amount] > 0 ), [Q] )"),
            // nested CONCATENATEX inside SUMX: delimiter is the outer row, orderBy is Sales' row.
            ("X1 concatenatex in sumx, Sales", "SUMX ( Other, LEN ( CONCATENATEX ( Sales, [Label], [P], [P], ASC ) ) )", "column:Sales/P", "Q", "SUMX ( Other, LEN ( CONCATENATEX ( Sales, [Label], [P], [Q], ASC ) ) )"),
            ("X2 concatenatex in sumx, Other", "SUMX ( Other, LEN ( CONCATENATEX ( Sales, [Label], [P], [P], ASC ) ) )", "column:Other/P", "R", "SUMX ( Other, LEN ( CONCATENATEX ( Sales, [Label], [R], [P], ASC ) ) )"),
            // FIRSTNONBLANK / LASTNONBLANK: the row holds the column's values only.
            ("F1 firstnonblank", "FIRSTNONBLANK ( Sales[Label], [P] )", "column:Sales/P", "Q", "FIRSTNONBLANK ( Sales[Label], [P] )"),
            ("F2 lastnonblank", "LASTNONBLANK ( Sales[Label], [P] )", "column:Sales/P", "Q", "LASTNONBLANK ( Sales[Label], [P] )"),
            ("F3 firstnonblank in Other scan", "SUMX ( Other, COUNTROWS ( FIRSTNONBLANK ( Sales[Label], [P] ) ) )", "column:Other/P", "R", "SUMX ( Other, COUNTROWS ( FIRSTNONBLANK ( Sales[Label], [R] ) ) )"),
            // a VAR table, and a VAR inside an argument
            ("V2 var in caller argument", "PERCENTILEX.INC ( Sales, [Amount], VAR k = [P] RETURN k )", "column:Sales/P", "Q", "PERCENTILEX.INC ( Sales, [Amount], VAR k = [P] RETURN k )"),
            ("V3 var in row argument", "SUMX ( Sales, VAR v = [P] RETURN v * 2 )", "column:Sales/P", "Q", "SUMX ( Sales, VAR v = [Q] RETURN v * 2 )"),
            // MAXX variant and RANKX value inside an outer scan
            ("M1 maxx variant outer", "SUMX ( Other, MAXX ( Sales, [Amount], [P] ) )", "column:Other/P", "R", "SUMX ( Other, MAXX ( Sales, [Amount], [R] ) )"),
            ("R1 rankx in sumx, Sales", "SUMX ( Other, RANKX ( Sales, [P], [P] ) )", "column:Sales/P", "Q", "SUMX ( Other, RANKX ( Sales, [Q], [P] ) )"),
            ("R2 rankx in sumx, Other", "SUMX ( Other, RANKX ( Sales, [P], [P] ) )", "column:Other/P", "R", "SUMX ( Other, RANKX ( Sales, [P], [R] ) )"),
        }.Select(c => new object[] { c.Id, c.Before, c.Rename, c.To, c.After });

        // Lean suite (8 Oct): the default run keeps these rows; the whole matrix is the sweep.
        public static IEnumerable<object[]> CasesSample() => Cases().Where(r => new[] { "S4", "S2", "X2", "R1" }.Contains(((string)r[0]).Split(' ')[0]));

        [Theory]
        [MemberData(nameof(CasesSample))]
        public Task A_rename_follows_the_binding_microsofts_pages_give_sample(string id, string before, string rename, string to, string after) => A_rename_follows_the_binding_microsofts_pages_give(id, before, rename, to, after);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Cases))]
        public async Task A_rename_follows_the_binding_microsofts_pages_give(string id, string before, string rename, string to, string after)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("VerifyScan", 1601);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable("Sales");
                sales.AddDataColumn("Label", "Label", null, DataType.String);
                sales.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
                sales.AddDataColumn("P", "P", null, DataType.Decimal);
                m.AddTable("Other").AddDataColumn("P", "P", null, DataType.Decimal);
                var metrics = m.AddTable("Metrics");
                metrics.AddMeasure("P", "0.5");
                metrics.AddMeasure("New P", "0.9");
                metrics.AddMeasure("Host", before);
            });
            await engine.RenameObjectAsync(rename, to, "human");
            Assert.Equal(id + ": " + after, id + ": " + await sessions.Require().ReadAsync(m => m.Tables["Metrics"].Measures["Host"].Expression));
        }

        // ---- X2: the KPI helper rewrite binds by occurrence ----

        private static async Task<(LocalEngine, SessionManager)> KpiModelAsync(string host)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("VerifyKpi", 1601);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable("Sales");
                sales.AddDataColumn("Label", "Label", null, DataType.String);
                sales.AddDataColumn("_Revenue Goal", "G", null, DataType.Decimal);
                sales.AddDataColumn("_Sales Revenue Goal", "H", null, DataType.Decimal);
                m.AddTable("Other").AddDataColumn("_Cost Goal", "C", null, DataType.Decimal);
                var metrics = m.AddTable("Metrics");
                metrics.AddMeasure("Revenue", "100").AddKPI().TargetExpression = "50";
                var cost = metrics.AddMeasure("Cost", "70").AddKPI();
                cost.TargetExpression = "60";
                cost.StatusExpression = "IF ( [Cost] > [_Revenue Goal], 1, -1 )";
                metrics.AddMeasure("Host", host);
            });
            return (engine, sessions);
        }

        private static Task<string> HostAsync(SessionManager s) => s.Require().ReadAsync(m => m.Tables["Metrics"].Measures["Host"].Expression);

        public static IEnumerable<object[]> KpiCases() => new (string Id, string Before, string After)[]
        {
            ("K1 string", "\"[_Revenue Goal]\" & [_Revenue Goal] + SUM ( Sales[_Revenue Goal] )", "\"[_Revenue Goal]\" & [_Sales Revenue Goal] + SUM ( Sales[_Revenue Goal] )"),
            ("K2 line comment", "// [_Revenue Goal] Sales[_Revenue Goal]\n[_Revenue Goal] + SUM ( Sales[_Revenue Goal] )", "// [_Revenue Goal] Sales[_Revenue Goal]\n[_Sales Revenue Goal] + SUM ( Sales[_Revenue Goal] )"),
            ("K3 block comment", "/* Sales[_Revenue Goal] */ SUM ( Sales[_Revenue Goal] ) + [_Revenue Goal]", "/* Sales[_Revenue Goal] */ SUM ( Sales[_Revenue Goal] ) + [_Sales Revenue Goal]"),
            ("K4 quoted tables", "'Sales'[_Revenue Goal] + 'Metrics'[_Revenue Goal]", "'Sales'[_Revenue Goal] + 'Metrics'[_Sales Revenue Goal]"),
            ("K5 two kpis", "[_Revenue Goal] + [_Cost Goal] + Sales[_Revenue Goal] + Other[_Cost Goal] + [_Cost Status]", "[_Sales Revenue Goal] + [_Cost Goal] + Sales[_Revenue Goal] + Other[_Cost Goal] + [_Cost Status]"),
            ("K6 crlf", "SUM ( Sales[_Revenue Goal] )\r\n+ [_Revenue Goal]", "SUM ( Sales[_Revenue Goal] )\n+ [_Sales Revenue Goal]"), // any rename drops the CR, on 0f6ee998 too (join item)
            ("K8 scan of Sales", "SUMX ( Sales, [_Revenue Goal] ) + [_Revenue Goal]", "SUMX ( Sales, [_Revenue Goal] ) + [_Sales Revenue Goal]"),
        }.Select(c => new object[] { c.Id, c.Before, c.After });

        // Lean suite (8 Oct): the default run keeps these rows; the whole matrix is the sweep.
        public static IEnumerable<object[]> KpiCasesSample() => KpiCases().Where(r => new[] { "K5" }.Contains(((string)r[0]).Split(' ')[0]));

        [Theory]
        [MemberData(nameof(KpiCasesSample))]
        public Task A_kpi_rename_rewrites_only_bound_helper_occurrences_sample(string id, string before, string after) => A_kpi_rename_rewrites_only_bound_helper_occurrences(id, before, after);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(KpiCases))]
        public async Task A_kpi_rename_rewrites_only_bound_helper_occurrences(string id, string before, string after)
        {
            var (engine, sessions) = await KpiModelAsync(before);
            using (engine)
            {
                await engine.RenameObjectAsync("measure:Metrics/Revenue", "Sales Revenue", "human");
                Assert.Equal(id + ": " + after, id + ": " + await HostAsync(sessions));
            }
        }

        [Fact]
        public async Task Two_kpis_renamed_in_turn_and_a_kpi_status_formula_follow()
        {
            var (engine, sessions) = await KpiModelAsync("[_Revenue Goal] + [_Cost Goal] + Sales[_Revenue Goal] + Other[_Cost Goal] + [_Cost Status]");
            using (engine)
            {
                await engine.RenameObjectAsync("measure:Metrics/Revenue", "Sales Revenue", "human");
                await engine.RenameObjectAsync("measure:Metrics/Cost", "Total Cost", "human");
                Assert.Equal("[_Sales Revenue Goal] + [_Total Cost Goal] + Sales[_Revenue Goal] + Other[_Cost Goal] + [_Total Cost Status]", await HostAsync(sessions));
                Assert.Equal("IF ( [Total Cost] > [_Sales Revenue Goal], 1, -1 )",
                    await sessions.Require().ReadAsync(m => m.Tables["Metrics"].Measures["Total Cost"].KPI.StatusExpression));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Rename_undo_redo_then_rename_again_keeps_the_column()
        {
            const string before = "[_Revenue Goal] + SUM ( Sales[_Revenue Goal] )";
            var (engine, sessions) = await KpiModelAsync(before);
            using (engine)
            {
                await engine.RenameObjectAsync("measure:Metrics/Revenue", "Sales Revenue", "human");
                Assert.Equal("[_Sales Revenue Goal] + SUM ( Sales[_Revenue Goal] )", await HostAsync(sessions));
                await engine.UndoAsync("human");
                Assert.Equal(before, await HostAsync(sessions));
                await engine.RedoAsync("human");
                Assert.Equal("[_Sales Revenue Goal] + SUM ( Sales[_Revenue Goal] )", await HostAsync(sessions));
                await engine.UndoAsync("human");
                await engine.RenameObjectAsync("measure:Metrics/Revenue", "Net Revenue", "human");
                Assert.Equal("[_Net Revenue Goal] + SUM ( Sales[_Revenue Goal] )", await HostAsync(sessions));
                await engine.RenameObjectAsync("measure:Metrics/Net Revenue", "Sales Revenue", "human");
                Assert.Equal("[_Sales Revenue Goal] + SUM ( Sales[_Revenue Goal] )", await HostAsync(sessions));
            }
        }
    }
}
