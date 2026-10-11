using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier attacks on m11/sola3 (rename scope in row scans, KPI helper renames, Explain timestamps). Tests that fail on the lane
    /// prove a defect and are left failing. DAX rules cited: https://learn.microsoft.com/en-us/dax/dax-syntax-reference ("Naming requirements":
    /// "columns can be referenced without referencing the base table that they belong to, except when there might be a name conflict";
    /// an unqualified name is used "in an aggregation function that is scanning over the same table"), TOPN
    /// (https://learn.microsoft.com/en-us/dax/topn-function-dax: OrderBy_Expression is "evaluated for each row of table", and the table is the
    /// SECOND argument), RANKX (https://learn.microsoft.com/en-us/dax/rankx-function-dax: value is the scalar "whose rank is to be found";
    /// only expression "is evaluated for each row of table"), and "Date and time" in the syntax reference (a datetime is one floating-point number).
    /// </summary>
    public sealed class VerifySola3Tests
    {
        private static async Task<(LocalEngine Engine, SessionManager Sessions)> ModelAsync(Action<Model> seed)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("VSola3", 1601);
            await sessions.Require().MutateAsync("human", "seed", seed);
            return (engine, sessions);
        }

        private static void Sales(Model m, bool metricsHasRevenue)
        {
            var sales = m.AddTable("Sales");
            sales.AddDataColumn("Revenue", "Revenue", null, DataType.Decimal);
            sales.AddDataColumn("Net Revenue", "Net Revenue", null, DataType.Decimal);
            sales.AddDataColumn("Region", "Region", null, DataType.String);
            var metrics = m.AddTable("Metrics");
            if (metricsHasRevenue) metrics.AddDataColumn("Revenue", "Revenue", null, DataType.Decimal);
            metrics.AddDataColumn("Rate", "Rate", null, DataType.Decimal);
        }

        private static Task<string> Expr(SessionManager s, string table, string measure) =>
            s.Require().ReadAsync(m => m.Tables[table].Measures[measure].Expression);

        // ---- S1: TOPN and SAMPLE scan their SECOND argument. The lane's list has no TOPN, so the order-by [Revenue] is read from the home table:
        // renaming Metrics[Revenue] rewrites the Sales sort key to [Net Revenue] and the top row (and every number built on it) changes.

        [Theory]
        [InlineData("CONCATENATEX ( TOPN ( 1, Sales, [Revenue] ), Sales[Region], \",\" )")]
        [InlineData("COUNTROWS ( TOPN ( 1, Sales, [Revenue], DESC ) )")]
        [InlineData("COUNTROWS ( SAMPLE ( 2, Sales, [Revenue] ) )")]
        public async Task A_bare_order_by_column_inside_topn_or_sample_over_another_table_is_left_alone_when_the_home_tables_column_is_renamed(string formula)
        {
            var (engine, sessions) = await ModelAsync(m => { Sales(m, true); m.Tables["Metrics"].AddMeasure("Host", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Net Revenue", "human");
                Assert.Equal(formula, await Expr(sessions, "Metrics", "Host"));
            }
        }

        // ---- S1 regression: a scan of a table EXPRESSION over the measure's own table. Revenue exists only in Sales, so [Revenue] has no name conflict and
        // is Sales[Revenue] (syntax reference). m11/join 0fa4b384 rewrote these (home table Sales); the lane records no dependency and leaves the old name.

        [Theory]
        [InlineData("SUMX ( FILTER ( Sales, Sales[Region] = \"West\" ), [Revenue] )")]
        [InlineData("SUMX ( ALL ( Sales ), [Revenue] )")]
        [InlineData("AVERAGEX ( CALCULATETABLE ( Sales, Sales[Region] = \"West\" ), [Revenue] )")]
        [InlineData("RANKX ( ALL ( Sales ), [Revenue] )")]
        public async Task A_bare_column_inside_a_scan_of_an_expression_over_its_own_table_still_follows_the_rename(string formula)
        {
            var (engine, sessions) = await ModelAsync(m => { Sales(m, false); m.Tables["Sales"].AddMeasure("Host", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal(formula.Replace("[Revenue]", "[Gross Revenue]"), await Expr(sessions, "Sales", "Host"));
            }
        }

        [Fact]
        public async Task A_bare_column_in_a_calculated_column_ranking_its_own_table_still_follows_the_rename()
        {
            const string formula = "RANKX ( ALL ( Sales ), [Revenue] )";
            var (engine, sessions) = await ModelAsync(m => { Sales(m, false); m.Tables["Sales"].AddCalculatedColumn("Rank", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal("RANKX ( ALL ( Sales ), [Gross Revenue] )", await sessions.Require().ReadAsync(m => ((CalculatedColumn)m.Tables["Sales"].Columns["Rank"]).Expression));
            }
        }

        // ---- S1 regression, nested scans: the inner Sales row has no Rate, the outer Metrics row has it, and the name is unique in the model.
        // 0fa4b384 rewrote it (home table Metrics); the lane reads only the innermost scan and drops the reference.

        [Fact]
        public async Task A_bare_column_of_the_outer_scan_inside_a_nested_scan_follows_the_rename()
        {
            const string formula = "SUMX ( Metrics, SUMX ( Sales, Sales[Revenue] * [Rate] ) )";
            var (engine, sessions) = await ModelAsync(m => { Sales(m, false); m.Tables["Metrics"].AddMeasure("Host", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Metrics/Rate", "Fx Rate", "human");
                Assert.Equal("SUMX ( Metrics, SUMX ( Sales, Sales[Revenue] * [Fx Rate] ) )", await Expr(sessions, "Metrics", "Host"));
            }
        }

        // ---- S1 attack that holds: the innermost scan wins over an outer scan that has the same column name.

        [Fact]
        public async Task The_innermost_scan_wins_over_an_outer_scan_with_the_same_column()
        {
            const string formula = "SUMX ( Metrics, SUMX ( Sales, [Revenue] ) )";
            var (engine, sessions) = await ModelAsync(m => { Sales(m, true); m.Tables["Metrics"].AddMeasure("Host", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Own Revenue", "human");
                Assert.Equal(formula, await Expr(sessions, "Metrics", "Host"));
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal("SUMX ( Metrics, SUMX ( Sales, [Gross Revenue] ) )", await Expr(sessions, "Metrics", "Host"));
            }
        }

        // ---- The measure-only refinement in an unknown scan, attacked with a COLUMN and a MEASURE of the same name: no column is ever rewritten wrongly.

        [Fact]
        public async Task In_a_scan_of_a_table_expression_a_name_a_column_and_a_measure_share_is_never_rewritten_for_the_column()
        {
            const string formula = "SUMX ( VALUES ( Sales[Region] ), [Rate] )";
            var (engine, sessions) = await ModelAsync(m =>
            {
                Sales(m, false);
                m.Tables["Sales"].AddMeasure("Rate", "1");   // a measure named like the column Metrics[Rate]
                m.Tables["Sales"].AddMeasure("Host", formula);
            });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Metrics/Rate", "Fx Rate", "human");
                Assert.Equal(formula, await Expr(sessions, "Sales", "Host"));
                await engine.RenameObjectAsync("column:Sales/Region", "Area", "human");
                Assert.Equal("SUMX ( VALUES ( 'Sales'[Area] ), [Rate] )", await Expr(sessions, "Sales", "Host"));
            }
        }

        // ---- S2 attacks: chains, undo then rename again, the batch door.

        private static void Kpi(Model m)
        {
            var sales = m.AddTable("Sales");
            var kpi = sales.AddMeasure("Achievement", "1").AddKPI();
            kpi.TargetExpression = "1";
            kpi.StatusExpression = "DIVIDE ( [Achievement], [_Achievement Goal] )";
            sales.AddMeasure("Reader", "[_Achievement Goal] + [_Achievement Status] + 'Sales'[_Achievement Trend]");
        }

        [Fact]
        public async Task Two_renames_in_a_row_carry_the_helpers_to_the_last_name()
        {
            var (engine, sessions) = await ModelAsync(Kpi);
            using (engine)
            {
                await engine.RenameObjectAsync("measure:Sales/Achievement", "Progress", "human");
                await engine.RenameObjectAsync("measure:Sales/Progress", "Score", "human");
                Assert.Equal("[_Score Goal] + [_Score Status] + 'Sales'[_Score Trend]", await Expr(sessions, "Sales", "Reader"));
                Assert.Equal("DIVIDE ( [Score], [_Score Goal] )", await sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Score"].KPI.StatusExpression));
            }
        }

        [Fact]
        public async Task A_rename_after_an_undo_still_carries_the_helpers()
        {
            var (engine, sessions) = await ModelAsync(Kpi);
            using (engine)
            {
                await engine.RenameObjectAsync("measure:Sales/Achievement", "Progress", "human");
                await engine.UndoAsync("human");
                Assert.Equal("[_Achievement Goal] + [_Achievement Status] + 'Sales'[_Achievement Trend]", await Expr(sessions, "Sales", "Reader"));
                await engine.RenameObjectAsync("measure:Sales/Achievement", "Score", "human");
                Assert.Equal("[_Score Goal] + [_Score Status] + 'Sales'[_Score Trend]", await Expr(sessions, "Sales", "Reader"));
                await engine.UndoAsync("human");
                await engine.RedoAsync("human");
                Assert.Equal("[_Score Goal] + [_Score Status] + 'Sales'[_Score Trend]", await Expr(sessions, "Sales", "Reader"));
            }
        }

        [Fact]
        public async Task A_kpi_base_rename_in_one_batch_carries_the_helpers()
        {
            var (engine, sessions) = await ModelAsync(Kpi);
            using (engine)
            {
                var s = sessions.Require();
                await s.MutateAsync("human", "script", m =>
                {
                    s.Rename((TabularNamedObject)ObjectRefs.Resolve(m, "measure:Sales/Achievement"), "Progress");
                    s.Rename((TabularNamedObject)ObjectRefs.Resolve(m, "table:Sales"), "Facts");
                });
                Assert.Equal("[_Progress Goal] + [_Progress Status] + 'Facts'[_Progress Trend]", await Expr(sessions, "Facts", "Reader"));
            }
        }

        /// <summary>A KPI added in the session, after the reader was parsed: its helpers must still follow the base measure's rename.</summary>
        [Fact]
        public async Task A_kpi_added_after_its_reader_still_carries_the_helpers()
        {
            var (engine, sessions) = await ModelAsync(m =>
            {
                var sales = m.AddTable("Sales");
                sales.AddMeasure("Achievement", "1");
                sales.AddMeasure("Reader", "[_Achievement Goal]");
            });
            using (engine)
            {
                await sessions.Require().MutateAsync("human", "add kpi", m => { var k = m.Tables["Sales"].Measures["Achievement"].AddKPI(); k.TargetExpression = "1"; });
                await engine.RenameObjectAsync("measure:Sales/Achievement", "Progress", "human");
                Assert.Equal("[_Progress Goal]", await Expr(sessions, "Sales", "Reader"));
            }
        }

        /// <summary>RANKX's third argument is the value whose rank is found, outside the rows of the table; in a calculated column of Metrics a bare [Revenue]
        /// there is Metrics' own column. Only the second argument is read per row of Sales.</summary>
        [Fact]
        public async Task The_value_argument_of_rankx_is_not_in_the_rows_of_the_ranked_table()
        {
            const string formula = "RANKX ( Sales, [Revenue], [Revenue] )";
            var (engine, sessions) = await ModelAsync(m => { Sales(m, true); m.Tables["Metrics"].AddCalculatedColumn("Rank", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Own Revenue", "human");
                Assert.Equal("RANKX ( Sales, [Revenue], [Own Revenue] )", await sessions.Require().ReadAsync(m => ((CalculatedColumn)m.Tables["Metrics"].Columns["Rank"]).Expression));
            }
        }

        // ---- S3: the emitted DAX must be the SAME double as the stored member, not the same after rounding to a millisecond.
        // A datetime is one floating-point number (syntax reference, "Date and time"); TREATAS keeps the rows whose value equals the member.
        // The stored value is taken as .NET's DateTime.ToOADate (whole milliseconds over 86,400,000, one division). DAX evaluates the emitted text left to right.

        private static double EvalLeftToRight(string dax)
        {
            var m = System.Text.RegularExpressions.Regex.Match(dax, @"DATE \( (\d+), (\d+), (\d+) \) \+ TIME \( (\d+), (\d+), (\d+) \)(?: \+ (\d+) / (\d+))? \}");
            Assert.True(m.Success, dax);
            long G(int i) => long.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture);
            double v = new DateTime((int)G(1), (int)G(2), (int)G(3)).ToOADate();   // DATE: a whole day number
            v = v + (G(4) * 3600 + G(5) * 60 + G(6)) / 86400.0;                    // + TIME: a fraction of a day
            if (m.Groups[7].Success) v = v + (double)G(7) / G(8);                  // + the rest of the second
            return v;
        }

        [Theory]
        [InlineData("2026-10-05T12:34:56.001")]
        [InlineData("2002-09-14T01:52:36.127")]
        [InlineData("2026-10-05T12:34:56.500")]
        public void The_emitted_member_is_bit_for_bit_the_stored_member(string text)
        {
            var stored = DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToOADate();
            var dax = DaxBench.CompileMemberFilter("'Sales'[Timestamp]", new[] { text }, true);
            var emitted = EvalLeftToRight(dax);
            Assert.True(BitConverter.DoubleToInt64Bits(stored) == BitConverter.DoubleToInt64Bits(emitted),
                $"{text}: stored {stored:R}, the DAX {dax} evaluates to {emitted:R}");
        }

        [Fact]
        public void Over_a_day_of_milliseconds_no_emitted_member_misses_its_stored_value()
        {
            var day = new DateTime(2026, 10, 5);
            int bad = 0, total = 0;
            for (var ms = 1; ms < 86_400_000; ms += 9_973)
            {
                var d = day.AddMilliseconds(ms);
                if (d.Millisecond == 0) continue;
                total++;
                var text = d.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                if (BitConverter.DoubleToInt64Bits(d.ToOADate()) != BitConverter.DoubleToInt64Bits(EvalLeftToRight(DaxBench.CompileMemberFilter("'Sales'[Timestamp]", new[] { text }, true))))
                    bad++;
            }
            Assert.True(bad == 0, $"{bad} of {total} fractional members compile to a different double than the stored one");
        }

        [Fact]
        public void Whole_second_members_stay_bit_for_bit()
        {
            var day = new DateTime(2026, 10, 5);
            for (var s = 0; s < 86_400; s += 7)
            {
                var d = day.AddSeconds(s);
                var text = d.ToString("s", System.Globalization.CultureInfo.InvariantCulture);
                var dax = DaxBench.CompileMemberFilter("'Sales'[Timestamp]", new[] { text }, true);
                if (s == 0) continue;   // midnight is DATE only
                Assert.Equal(BitConverter.DoubleToInt64Bits(d.ToOADate()), BitConverter.DoubleToInt64Bits(EvalLeftToRight(dax)));
            }
        }
    }
}
