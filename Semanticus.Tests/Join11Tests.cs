// Join 11: the fixes the join itself makes on top of the merged lanes. Each test here failed first on the merge of sola3 (and drafts-sol).
//
// J-S1  a whole-second Explain member from 1900-03-01 on compiles to the one-division form a fractional one uses (the verify test
//       VerifySola3Round2Tests.Every_whole_second_of_a_day_is_bit_for_bit covers every second; these pin the boundary and the old form before it).
// J-S2  a bare [column] inside a scan whose table argument is FILTER, ALL, CALCULATETABLE, TOPN, SAMPLE or TOPNSKIP over ONE plain table name binds to THAT table's row.
//       Microsoft, FILTER (https://learn.microsoft.com/en-us/dax/filter-function-dax): "Returns a table that represents a subset of another table or expression", so the rows
//       a SUMX over it walks are the rows of the filtered table; ALL (https://learn.microsoft.com/en-us/dax/all-function-dax): "Returns all the rows in a table ... ignoring
//       any filters"; TOPN, SAMPLE and TOPNSKIP return rows of their table argument too. Before the fix the walk gave up on the table expression and, with Revenue in two
//       tables, left the bare name beside a renamed column (it then read the HOME table's column: a wrong number), and with Revenue only in Sales, left a name that no longer exists.

using System;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class Join11Tests
    {
        private static async Task<(LocalEngine Engine, SessionManager Sessions)> ModelAsync(Action<Model> seed)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Join11", 1601);
            await sessions.Require().MutateAsync("human", "seed", seed);
            return (engine, sessions);
        }

        /// <summary>Sales(Revenue, Net Revenue, Region), Metrics(Rate [, Revenue]).</summary>
        private static void Seed(Model m, bool metricsHasRevenue)
        {
            var sales = m.AddTable("Sales");
            sales.AddDataColumn("Revenue", "Revenue", null, DataType.Decimal);
            sales.AddDataColumn("Net Revenue", "Net Revenue", null, DataType.Decimal);
            sales.AddDataColumn("Region", "Region", null, DataType.String);
            var metrics = m.AddTable("Metrics");
            if (metricsHasRevenue) metrics.AddDataColumn("Revenue", "Revenue", null, DataType.Decimal);
            metrics.AddDataColumn("Rate", "Rate", null, DataType.Decimal);
        }

        private static Task<string> Calc(SessionManager s, string table, string column) =>
            s.Require().ReadAsync(m => ((CalculatedColumn)m.Tables[table].Columns[column]).Expression);

        private static Task<string> Expr(SessionManager s, string table, string measure) =>
            s.Require().ReadAsync(m => m.Tables[table].Measures[measure].Expression);

        public static readonly string[] SalesScans =
        {
            "SUMX ( FILTER ( Sales, Sales[Region] = \"W\" ), [Revenue] )",
            "SUMX ( ALL ( Sales ), [Revenue] )",
            "SUMX ( CALCULATETABLE ( Sales, Sales[Region] = \"W\" ), [Revenue] )",
            "SUMX ( TOPN ( 3, Sales, Sales[Net Revenue] ), [Revenue] )",
            "SUMX ( SAMPLE ( 3, Sales, Sales[Net Revenue] ), [Revenue] )",
            "SUMX ( TOPNSKIP ( 3, 1, Sales, Sales[Net Revenue] ), [Revenue] )",
            "SUMX ( FILTER ( ALL ( Sales ), Sales[Region] = \"W\" ), [Revenue] )",
            "SUMX ( TOPN ( 3, FILTER ( 'Sales', Sales[Region] = \"W\" ), Sales[Net Revenue] ), [Revenue] )",
        };
        public static System.Collections.Generic.IEnumerable<object[]> SalesScanCases() { foreach (var s in SalesScans) yield return new object[] { s }; }

        /// <summary>The check's items (d) and (e): Revenue is in BOTH tables, the column is on Metrics. Real DAX binds the row of the table the scan returns (Sales).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(SalesScanCases))]
        public async Task A_bare_name_inside_a_scan_of_a_table_expression_over_Sales_follows_the_Sales_column_and_not_the_home_tables(string formula)
        {
            var (engine, sessions) = await ModelAsync(m => { Seed(m, true); m.Tables["Metrics"].AddCalculatedColumn("Host", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Own Revenue", "human");
                Assert.Equal(formula, await Calc(sessions, "Metrics", "Host"));   // the home table's column is not the one the scan reads
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal(formula.Replace("[Revenue]", "[Gross Revenue]"), await Calc(sessions, "Metrics", "Host"));
                await engine.UndoAsync("human"); await engine.UndoAsync("human");
                Assert.Equal(formula, await Calc(sessions, "Metrics", "Host"));
            }
        }

        /// <summary>The check's item 6: Revenue is only in Sales, the scan is a table expression over Sales, the host is Metrics. The name must follow the rename, not be left behind.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(SalesScanCases))]
        public async Task A_bare_name_inside_a_scan_of_a_table_expression_over_Sales_hosted_on_Metrics_follows_the_rename_when_only_Sales_has_it(string formula)
        {
            var (engine, sessions) = await ModelAsync(m => { Seed(m, false); m.Tables["Metrics"].AddMeasure("Host", formula); m.Tables["Metrics"].AddCalculatedColumn("HostCol", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal(formula.Replace("[Revenue]", "[Gross Revenue]"), await Expr(sessions, "Metrics", "Host"));
                Assert.Equal(formula.Replace("[Revenue]", "[Gross Revenue]"), await Calc(sessions, "Metrics", "HostCol"));
            }
        }

        /// <summary>A nested scan keeps the outer row: the inner FILTER binds Sales, [Rate] is not a Sales column and still reads the outer Metrics row.</summary>
        [Fact]
        public async Task A_name_the_inner_Sales_scan_does_not_have_is_read_from_the_outer_row()
        {
            const string formula = "SUMX ( Metrics, SUMX ( FILTER ( Sales, Sales[Region] = \"W\" ), Sales[Revenue] * [Rate] ) )";
            var (engine, sessions) = await ModelAsync(m => { Seed(m, false); m.Tables["Metrics"].AddMeasure("Host", formula); m.Tables["Sales"].AddMeasure("Host2", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Metrics/Rate", "Fx Rate", "human");
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                const string after = "SUMX ( Metrics, SUMX ( FILTER ( Sales, Sales[Region] = \"W\" ), 'Sales'[Gross Revenue] * [Fx Rate] ) )";
                Assert.Equal(after, await Expr(sessions, "Metrics", "Host"));
                Assert.Equal(after, await Expr(sessions, "Sales", "Host2"));
            }
        }

        /// <summary>Still not knowable, still left alone: ALL over a COLUMN, a VALUES table, and a UNION (no one plain table name), with Revenue in both tables.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("SUMX ( ALL ( Sales[Region] ), [Revenue] )")]
        [InlineData("SUMX ( FILTER ( VALUES ( Sales[Region] ), TRUE () ), [Revenue] )")]
        [InlineData("SUMX ( UNION ( Sales, Sales ), [Revenue] )")]
        [InlineData("SUMX ( FILTER ( Sales, TRUE () ) , [Revenue] ) + SUMX ( VALUES ( Sales[Region] ), [Revenue] )")]
        public async Task A_scan_whose_table_is_not_one_plain_table_name_is_still_left_alone_by_either_rename(string formula)
        {
            foreach (var table in new[] { "Metrics", "Sales" })
            {
                var (engine, sessions) = await ModelAsync(m => { Seed(m, true); m.Tables["Metrics"].AddMeasure("Host", formula); });
                using (engine)
                {
                    await engine.RenameObjectAsync($"column:{table}/Revenue", "Renamed", "human");
                    var after = await Expr(sessions, "Metrics", "Host");
                    // the one plain FILTER ( Sales ) scan in the last case is the only part that may follow the Sales rename
                    var expected = table == "Sales" && formula.StartsWith("SUMX ( FILTER ( Sales, TRUE () )", StringComparison.Ordinal)
                        ? formula.Replace("SUMX ( FILTER ( Sales, TRUE () ) , [Revenue] )", "SUMX ( FILTER ( Sales, TRUE () ) , [Renamed] )") : formula;
                    Assert.Equal(expected, after);
                }
            }
        }

        // ---- J-S1: the boundary of the one-division form.

        [Fact]
        public void A_whole_second_member_is_the_one_division_form_from_1_March_1900_and_the_sum_before_it()
        {
            // 1900-03-01T00:00:01 is 61 days (5,270,400,000 ms) after 1899-12-30, plus one second.
            Assert.Equal("TREATAS ( { DATE ( 1899, 12, 30 ) + TIME ( 0, 0, 0 ) + 5270401000 / 86400000 }, 'Date'[Date] )",
                DaxBench.CompileMemberFilter("'Date'[Date]", new[] { "1900-03-01T00:00:01" }, true));
            Assert.Equal("TREATAS ( { DATE ( 1900, 2, 28 ) + TIME ( 23, 59, 59 ) }, 'Date'[Date] )",
                DaxBench.CompileMemberFilter("'Date'[Date]", new[] { "1900-02-28T23:59:59" }, true));
            Assert.Equal("TREATAS ( { DATE ( 1900, 3, 1 ) }, 'Date'[Date] )",
                DaxBench.CompileMemberFilter("'Date'[Date]", new[] { "1900-03-01T00:00:00" }, true));   // midnight stays DATE ( )
        }
    }
}
