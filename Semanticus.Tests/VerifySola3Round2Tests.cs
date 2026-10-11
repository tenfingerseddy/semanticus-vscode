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
    /// Verifier attacks on m11/sola3 fix round 2 (row-scan resolution order, TOPN/SAMPLE/TOPNSKIP/RANKX positions, Explain members).
    /// DAX rules: https://learn.microsoft.com/en-us/dax/dax-syntax-reference ("Naming requirements": "Table names are required whenever the column is from a
    /// different table than the current table"; an unqualified name is used "in an aggregation function that is scanning over the same table"),
    /// https://learn.microsoft.com/en-us/dax/topn-function-dax, sample-function-dax, topnskip-function-dax (the table argument and the per-row order by),
    /// https://learn.microsoft.com/en-us/dax/rankx-function-dax (only expression is evaluated for each row of table; value is evaluated once),
    /// https://learn.microsoft.com/en-us/dax/dax-parameter-naming-conventions ("value ... evaluated exactly once before all other operations").
    /// </summary>
    public sealed class VerifySola3Round2Tests
    {
        private static async Task<(LocalEngine Engine, SessionManager Sessions)> ModelAsync(Action<Model> seed)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("VSola32", 1601);
            await sessions.Require().MutateAsync("human", "seed", seed);
            return (engine, sessions);
        }

        /// <summary>Sales(Revenue, Net Revenue, Region), Metrics(Rate [, Revenue]), Regions(Code).</summary>
        private static void Three(Model m, bool metricsHasRevenue)
        {
            var sales = m.AddTable("Sales");
            sales.AddDataColumn("Revenue", "Revenue", null, DataType.Decimal);
            sales.AddDataColumn("Net Revenue", "Net Revenue", null, DataType.Decimal);
            sales.AddDataColumn("Region", "Region", null, DataType.String);
            var metrics = m.AddTable("Metrics");
            if (metricsHasRevenue) metrics.AddDataColumn("Revenue", "Revenue", null, DataType.Decimal);
            metrics.AddDataColumn("Rate", "Rate", null, DataType.Decimal);
            m.AddTable("Regions").AddDataColumn("Code", "Code", null, DataType.Decimal);
        }

        private static Task<string> Expr(SessionManager s, string table, string measure) =>
            s.Require().ReadAsync(m => m.Tables[table].Measures[measure].Expression);

        private static Task<string> Calc(SessionManager s, string table, string column) =>
            s.Require().ReadAsync(m => ((CalculatedColumn)m.Tables[table].Columns[column]).Expression);

        // ---- T1: a name that is a column in TWO tables, inside a scan of an unknown table: renaming either column leaves it alone (no guess).
        // Join 11 (J-S2): FILTER, ALL, CALCULATETABLE, TOPN, SAMPLE and TOPNSKIP over one plain table name are not unknown, they return that table's rows
        // (Microsoft's FILTER page: "a table that represents a subset of another table"; ALL: "all the rows in a table"), so those two cases moved to Join11Tests.
        // Only a variable whose expression is no one plain table stays unknown (solfx fix round 2, Y3: a VAR over one plain table is that table).

        [Theory]
        [InlineData("VAR t = ADDCOLUMNS ( Sales, \"x\", 1 ) RETURN SUMX ( t, [Revenue] )")]
        public async Task A_two_table_name_inside_an_unknown_scan_is_left_alone_by_either_rename(string formula)
        {
            foreach (var host in new[] { "Sales", "Metrics" })
            {
                var (engine, sessions) = await ModelAsync(m => { Three(m, true); m.Tables[host].AddMeasure("Host", formula); });
                using (engine)
                {
                    await engine.RenameObjectAsync("column:Metrics/Revenue", "Own Revenue", "human");
                    Assert.Equal(formula, await Expr(sessions, host, "Host"));
                }
                (engine, sessions) = await ModelAsync(m => { Three(m, true); m.Tables[host].AddMeasure("Host", formula); });
                using (engine)
                {
                    await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                    Assert.Equal(formula, await Expr(sessions, host, "Host"));   // conservative: not knowable without types, so not guessed
                }
            }
        }

        /// <summary>The join 11 form of the two cases above (J-S2): the same formulas now bind Sales' row, whichever table hosts them.</summary>
        [Theory]
        [InlineData("SUMX ( FILTER ( Sales, Sales[Region] = \"W\" ), [Revenue] )")]
        [InlineData("COUNTROWS ( TOPN ( 1, ALL ( Sales ), [Revenue] ) )")]
        [InlineData("VAR t = FILTER ( Sales, Sales[Region] = \"W\" ) RETURN SUMX ( t, [Revenue] )")] // solfx fix round 2 (Y3): a VAR over one plain table is that table, as inline
        public async Task A_two_table_name_inside_a_scan_over_one_plain_table_follows_that_tables_rename_only(string formula)
        {
            foreach (var host in new[] { "Sales", "Metrics" })
            {
                var (engine, sessions) = await ModelAsync(m => { Three(m, true); m.Tables[host].AddMeasure("Host", formula); });
                using (engine)
                {
                    await engine.RenameObjectAsync("column:Metrics/Revenue", "Own Revenue", "human");
                    Assert.Equal(formula, await Expr(sessions, host, "Host"));
                }
                (engine, sessions) = await ModelAsync(m => { Three(m, true); m.Tables[host].AddMeasure("Host", formula); });
                using (engine)
                {
                    await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                    Assert.Equal(formula.Replace("[Revenue]", "[Gross Revenue]"), await Expr(sessions, host, "Host"));
                }
            }
        }

        // ---- T1: three-deep nesting. Each bare name binds to the innermost open scan that has it.

        [Fact]
        public async Task Three_deep_nesting_binds_each_name_to_the_innermost_scan_that_has_it()
        {
            const string formula = "SUMX ( Regions, SUMX ( Metrics, SUMX ( Sales, [Code] * [Rate] * [Revenue] ) ) )";
            var (engine, sessions) = await ModelAsync(m => { Three(m, true); m.Tables["Metrics"].AddMeasure("Host", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Own Revenue", "human");   // shadowed by Sales: untouched
                Assert.Equal(formula, await Expr(sessions, "Metrics", "Host"));
                await engine.RenameObjectAsync("column:Regions/Code", "Region Code", "human");
                await engine.RenameObjectAsync("column:Metrics/Rate", "Fx Rate", "human");
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal("SUMX ( Regions, SUMX ( Metrics, SUMX ( Sales, [Region Code] * [Fx Rate] * [Gross Revenue] ) ) )", await Expr(sessions, "Metrics", "Host"));
                await engine.UndoAsync("human"); await engine.UndoAsync("human"); await engine.UndoAsync("human"); await engine.UndoAsync("human");
                Assert.Equal(formula, await Expr(sessions, "Metrics", "Host"));
            }
        }

        // ---- T1: a VAR table over the home table binds a unique name (home owns it); over another table it is left alone.

        [Fact]
        public async Task A_var_scan_over_the_home_table_follows_the_rename()
        {
            const string formula = "VAR t = FILTER ( Sales, Sales[Region] = \"W\" ) RETURN SUMX ( t, [Revenue] )";
            var (engine, sessions) = await ModelAsync(m => { Three(m, false); m.Tables["Sales"].AddMeasure("Host", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal(formula.Replace("[Revenue]", "[Gross Revenue]"), await Expr(sessions, "Sales", "Host"));
            }
        }

        // ---- T1: the scan closes and the next bare name is the home table's again (also after TOPN and a nested unknown scan).

        [Theory]
        [InlineData("SUMX ( Sales, [Revenue] ) + [Revenue]", "SUMX ( Sales, [Gross Revenue] ) + [Revenue]", "SUMX ( Sales, [Revenue] ) + [Own Revenue]")]
        [InlineData("COUNTROWS ( TOPN ( 1, Sales, [Revenue] ) ) + [Revenue]", "COUNTROWS ( TOPN ( 1, Sales, [Gross Revenue] ) ) + [Revenue]", "COUNTROWS ( TOPN ( 1, Sales, [Revenue] ) ) + [Own Revenue]")]
        [InlineData("SUMX ( FILTER ( Sales, Sales[Region] = \"W\" ), 1 ) + [Revenue]", "SUMX ( FILTER ( Sales, Sales[Region] = \"W\" ), 1 ) + [Revenue]", "SUMX ( FILTER ( Sales, Sales[Region] = \"W\" ), 1 ) + [Own Revenue]")]
        public async Task After_a_scan_closes_the_next_bare_name_is_the_home_tables_again(string formula, string afterSales, string afterMetrics)
        {
            var (engine, sessions) = await ModelAsync(m => { Three(m, true); m.Tables["Metrics"].AddCalculatedColumn("Host", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal(afterSales, await Calc(sessions, "Metrics", "Host"));
                await engine.UndoAsync("human");
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Own Revenue", "human");
                Assert.Equal(afterMetrics, await Calc(sessions, "Metrics", "Host"));
            }
        }

        // ---- T2: TOPN / SAMPLE / TOPNSKIP table at the real position, with nested commas in earlier arguments; the n argument is outer context.

        [Theory]
        [InlineData("COUNTROWS ( TOPN ( MAX ( 1, 2 ), Sales, [Revenue], DESC, [Net Revenue], ASC ) )")]
        [InlineData("COUNTROWS ( SAMPLE ( IF ( TRUE (), 2, 3 ), Sales, [Revenue], ASC ) )")]
        [InlineData("COUNTROWS ( TOPNSKIP ( 1, 0, Sales, [Revenue] ) )")]
        [InlineData("COUNTROWS ( TOPNSKIP ( MIN ( 1, 2 ), 0, 'Sales', [Revenue], DESC ) )")]
        public async Task Topn_sample_topnskip_order_by_reads_the_scanned_table(string formula)
        {
            var (engine, sessions) = await ModelAsync(m => { Three(m, true); m.Tables["Metrics"].AddMeasure("Host", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Own Revenue", "human");
                Assert.Equal(formula, await Expr(sessions, "Metrics", "Host"));
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal(formula.Replace("[Revenue]", "[Gross Revenue]"), await Expr(sessions, "Metrics", "Host"));
            }
        }

        /// <summary>TOPN's n argument is evaluated once, outside the rows: in a calculated column on Metrics a bare [Rate] there is Metrics' own column.</summary>
        [Fact]
        public async Task The_n_argument_of_topn_is_not_in_the_rows_of_the_table()
        {
            const string formula = "COUNTROWS ( TOPN ( [Revenue], Sales, [Revenue] ) )";
            var (engine, sessions) = await ModelAsync(m => { Three(m, true); m.Tables["Metrics"].AddCalculatedColumn("Host", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Own Revenue", "human");
                Assert.Equal("COUNTROWS ( TOPN ( [Own Revenue], Sales, [Revenue] ) )", await Calc(sessions, "Metrics", "Host"));
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal("COUNTROWS ( TOPN ( [Own Revenue], Sales, [Gross Revenue] ) )", await Calc(sessions, "Metrics", "Host"));
            }
        }

        /// <summary>RANKX: expression per row of Sales, value once in the outer context (Metrics' row), order and ties are enumerations.</summary>
        [Theory]
        [InlineData("RANKX ( Sales, [Revenue] * 2, [Revenue] * 2, DESC, DENSE )", "RANKX ( Sales, [Gross Revenue] * 2, [Own Revenue] * 2, DESC, DENSE )")]
        [InlineData("RANKX ( 'Sales', SUMX ( Sales, [Revenue] ), [Revenue] )", "RANKX ( 'Sales', SUMX ( Sales, [Gross Revenue] ), [Own Revenue] )")]
        public async Task Rankx_reads_the_value_in_the_outer_row(string formula, string after)
        {
            var (engine, sessions) = await ModelAsync(m => { Three(m, true); m.Tables["Metrics"].AddCalculatedColumn("Host", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Metrics/Revenue", "Own Revenue", "human");
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                Assert.Equal(after, await Calc(sessions, "Metrics", "Host"));
            }
        }

        // ---- Item 6: the builder's narrowing. Revenue only in Sales, the scan is a table expression over Sales, the host is Metrics.
        // Real DAX binds Sales[Revenue] (the row of the filtered Sales). Round 2 left the name, so after the rename the formula named a column that was gone and the strict
        // DAX check flagged it. Join 11 (J-S2) binds it: the name follows the rename, nothing is left behind to flag.

        [Theory]
        [InlineData("SUMX ( FILTER ( Sales, Sales[Region] = \"W\" ), [Revenue] )")]
        [InlineData("SUMX ( Metrics, SUMX ( FILTER ( Sales, Sales[Region] = \"W\" ), Sales[Revenue] * [Rate] ) )")]
        public async Task Narrowing_case_the_name_follows_the_rename_and_the_strict_dax_check_has_nothing_to_flag(string formula)
        {
            var (engine, sessions) = await ModelAsync(m => { Three(m, false); m.Tables["Sales"].AddMeasure("Host", formula); m.Tables["Metrics"].AddMeasure("Host2", formula); });
            using (engine)
            {
                await engine.RenameObjectAsync("column:Sales/Revenue", "Gross Revenue", "human");
                await engine.RenameObjectAsync("column:Metrics/Rate", "Fx Rate", "human");
                foreach (var (t, n) in new[] { ("Sales", "Host"), ("Metrics", "Host2") })
                {
                    var after = await Expr(sessions, t, n);
                    Assert.Equal(formula.Replace("Sales[Revenue]", "'Sales'[Gross Revenue]").Replace("[Revenue]", "[Gross Revenue]").Replace("[Rate]", "[Fx Rate]"), after);   // a rewritten qualified name is always quoted
                    Assert.DoesNotContain("[Revenue]", after);
                }
            }
        }

        // ---- T3: the emitted member, evaluated as DAX evaluates it (left to right, DATE a whole day number from 1899-12-30, TIME a fraction, a / b one
        // IEEE division; INTEGER / INTEGER is REAL per the Power BI data types conversion table), is bit for bit DateTime.ToOADate.

        internal static double EvalDax(string literal)
        {
            double v = 0; var first = true;
            foreach (var raw in literal.Split(" + "))
            {
                var term = raw.Trim(); double x;
                long[] Args(string t) => t.Substring(t.IndexOf('(') + 1, t.LastIndexOf(')') - t.IndexOf('(') - 1).Split(',').Select(a => long.Parse(a.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if (term.StartsWith("DATE ( ", StringComparison.Ordinal)) { var a = Args(term); x = (new DateTime((int)a[0], (int)a[1], (int)a[2]) - new DateTime(1899, 12, 30)).Days; }
                else if (term.StartsWith("TIME ( ", StringComparison.Ordinal)) { var a = Args(term); x = (a[0] * 3600 + a[1] * 60 + a[2]) / 86400.0; }
                else if (term.Contains(" / ")) { var p = term.Split(" / "); x = (double)long.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture) / (double)long.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture); }
                else throw new InvalidOperationException("unknown term: " + term);
                v = first ? x : v + x; first = false;
            }
            return v;
        }

        private static string Member(string text)
        {
            var dax = DaxBench.CompileMemberFilter("'T'[C]", new[] { text }, true);
            return dax.Substring("TREATAS ( { ".Length, dax.Length - "TREATAS ( { ".Length - " }, 'T'[C] )".Length);
        }

        [Fact]
        public void Every_millisecond_of_a_day_and_200000_random_members_are_bit_for_bit()
        {
            var bad = new List<string>();
            var day = new DateTime(2026, 10, 5);
            for (var ms = 0; ms < 86_400_000; ms++)
            {
                var d = day.AddMilliseconds(ms);
                var text = d.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                if (BitConverter.DoubleToInt64Bits(d.ToOADate()) != BitConverter.DoubleToInt64Bits(EvalDax(Member(text)))) { bad.Add(text); if (bad.Count > 5) break; }
            }
            var rnd = new Random(20261005);
            var lo = new DateTime(1900, 3, 1).Ticks / TimeSpan.TicksPerMillisecond; var hi = new DateTime(9999, 12, 31).Ticks / TimeSpan.TicksPerMillisecond;
            for (var i = 0; i < 200_000 && bad.Count <= 5; i++)
            {
                var d = new DateTime((lo + (long)(rnd.NextDouble() * (hi - lo))) * TimeSpan.TicksPerMillisecond);
                var text = d.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
                if (BitConverter.DoubleToInt64Bits(d.ToOADate()) != BitConverter.DoubleToInt64Bits(EvalDax(Member(text)))) bad.Add(text);
            }
            Assert.True(bad.Count == 0, "differ: " + string.Join(", ", bad));
        }

        /// <summary>The lane keeps DATE + TIME for a whole second and calls it bit-exact. It is two rounded terms, so on a day with a small day number (1900 to
        /// about 1950) it misses ToOADate's one division in the last bit: 192 of the 86,399 seconds of 1900-07-11 (19:32:32 is one), none after 1950.
        /// DAX supports dates from March 1, 1900 (data types table), so these are valid members; TREATAS then matches no row and Explain shows a blank.</summary>
        [Theory]
        [InlineData("1900-07-11")]
        [InlineData("1925-02-03")]
        [InlineData("2026-10-05")]
        public void Every_whole_second_of_a_day_is_bit_for_bit(string day)
        {
            var start = DateTime.Parse(day, System.Globalization.CultureInfo.InvariantCulture);
            var bad = new List<string>();
            for (var sec = 1; sec < 86_400; sec++)
            {
                var d = start.AddSeconds(sec);
                var text = d.ToString("s", System.Globalization.CultureInfo.InvariantCulture);
                if (BitConverter.DoubleToInt64Bits(d.ToOADate()) != BitConverter.DoubleToInt64Bits(EvalDax(Member(text)))) bad.Add(text);
            }
            Assert.True(bad.Count == 0, $"{bad.Count} of 86399 whole seconds differ, first {bad.FirstOrDefault()} -> {(bad.Count > 0 ? Member(bad[0]) : "")}");
        }
    }
}
