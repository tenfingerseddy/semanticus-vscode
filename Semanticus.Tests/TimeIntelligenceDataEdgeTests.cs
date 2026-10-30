using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The DAX research of 17 Oct found a wrong-number bug: the rolling windows from generate_time_intelligence were written
    /// as DATESINPERIOD(date, MAX(date), -N, MONTH). With no filter on the date (a card, a total row) MAX(date) is the LAST DATE OF THE
    /// CALENDAR, and a calendar that runs past the data (the usual: it is built to 2030 and sales stop in October) puts the window over
    /// months that have no sales, so "Rolling 12M" shows the sum of a window that is partly empty. The window must end on the end of the month,
    /// clamped to the month that holds the last date with data. The first fix anchored on the last day with data of each period, which moved a
    /// complete month's window inside the data (see the live number test below).</summary>
    public sealed class TimeIntelligenceDataEdgeTests
    {
        private static async Task<GenerateResult> GenerateAsync(params string[] variants)
        {
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await engine.CreateModelAsync("TiEdge", 1604);
            var t = await engine.CreateTableAsync("Sales", "agent");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "agent");
            await engine.CreateTableAsync("Date", "agent");
            await engine.CreateColumnAsync("table:Date", "Date", "DateTime", "Date", "agent");
            await engine.CreateMeasureAsync(t, "Total Amount", "SUM ( Sales[Amount] )", "agent");
            return await engine.GenerateTimeIntelligenceAsync("measure:Sales/Total Amount", "column:Date/Date", variants, null, "agent");
        }

        [Theory]
        [InlineData("ROLL12", 12)]
        [InlineData("R6M", 6)]
        [InlineData("R3M", 3)]
        public async Task A_rolling_window_keeps_the_month_end_and_clamps_it_to_the_month_that_holds_the_last_data(string variant, int months)
        {
            var r = await GenerateAsync(variant);
            var expr = Assert.Single(r.Created).Expression;

            // The last date with data is found once, across the whole calendar, so a month's own window never moves inside the data.
            Assert.Contains("VAR _lastData = CALCULATE ( MAXX ( FILTER ( VALUES ( 'Date'[Date] ), NOT ISBLANK ( [Total Amount] ) ), 'Date'[Date] ), REMOVEFILTERS ( ) )", expr);
            // The window still ends on the end of the filtered period, clamped to the month that holds the last data date.
            Assert.Contains("VAR _end = MIN ( MAX ( 'Date'[Date] ), EOMONTH ( _lastData, 0 ) )", expr);
            Assert.Contains("DATESINPERIOD ( 'Date'[Date], _end, -" + months + ", MONTH )", expr);
            // A period that starts after the data is blank, not a window over empty months.
            Assert.Contains("MIN ( 'Date'[Date] ) <= _lastData", expr);
            // The first version anchored on the last day with data of the filtered period. That moved the window inside the data.
            Assert.DoesNotContain("_anchor", expr);
        }

        /// <summary>The live number check: the generated windows are evaluated by a real engine, read only, and set beside the plain
        /// DATESINPERIOD(MAX(date)) measure they replace. Runs only when SEMANTICUS_LIVE_NORTHSTAR_DB names the Northstar dataset on the
        /// FrameworkTesting workspace (and AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_CLIENT_SECRET are set); otherwise xunit reports it skipped.
        /// Nothing is written: every measure lives in the query's DEFINE.</summary>
        public sealed class NorthstarLiveFactAttribute : FactAttribute
        {
            public NorthstarLiveFactAttribute()
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SEMANTICUS_LIVE_NORTHSTAR_DB")))
                    Skip = "The live number check is on demand; set SEMANTICUS_LIVE_NORTHSTAR_DB (and the AZURE_* service principal variables).";
            }
        }

        private static double? Num(object o) => o == null || o is DBNull ? (double?)null
            : o is System.Text.Json.JsonElement j ? (j.ValueKind == System.Text.Json.JsonValueKind.Number ? j.GetDouble() : (double?)null)
            : Convert.ToDouble(o);

        [NorthstarLiveFact]
        public async Task The_rolling_windows_equal_the_old_measure_in_every_month_inside_the_data_and_are_blank_after_it()
        {
            // The base measure is named B1 on the local stand-in, so the generated text drops into the query as it is.
            var generated = new Dictionary<int, string>();
            foreach (var (variant, months) in new[] { ("R3M", 3), ("R6M", 6), ("ROLL12", 12) })
            {
                using var local = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                await local.CreateModelAsync("NsLive", 1604);
                var t = await local.CreateTableAsync("Sales", "agent");
                await local.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "agent");
                await local.CreateTableAsync("Date", "agent");
                await local.CreateColumnAsync("table:Date", "Date", "DateTime", "Date", "agent");
                await local.CreateMeasureAsync(t, "B1", "SUM ( Sales[Amount] )", "agent");
                var r = await local.GenerateTimeIntelligenceAsync("measure:Sales/B1", "column:Date/Date", new[] { variant }, null, "agent");
                generated[months] = r.Created.Single().Expression;
            }

            var q = new StringBuilder();
            q.AppendLine("DEFINE");
            // Data stops on 17 Sep 2025. B1 is the plain base. B2 also closes two weekdays, so a month's last day can have no sales
            // (the case that broke the first fix).
            q.AppendLine("  MEASURE Sales[B1] = CALCULATE ( [Net Sales], KEEPFILTERS ( 'Date'[Date] <= DATE ( 2025, 9, 17 ) ) )");
            q.AppendLine("  MEASURE Sales[B2] = CALCULATE ( [Net Sales], KEEPFILTERS ( 'Date'[Date] <= DATE ( 2025, 9, 17 ) ), KEEPFILTERS ( NOT 'Date'[DayOfWeekNumber] IN { 1, 7 } ) )");
            var cols = new StringBuilder();
            foreach (var b in new[] { "B1", "B2" })
                foreach (var m in new[] { 3, 6, 12 })
                {
                    q.AppendLine($"  MEASURE Sales[Old{b}_{m}] = CALCULATE ( [{b}], DATESINPERIOD ( 'Date'[Date], MAX ( 'Date'[Date] ), -{m}, MONTH ) )");
                    q.AppendLine($"  MEASURE Sales[New{b}_{m}] = {generated[m].Replace("[B1]", "[" + b + "]")}");
                    cols.Append($", \"o{b}_{m}\", [Old{b}_{m}], \"n{b}_{m}\", [New{b}_{m}]");
                }
            q.AppendLine($"EVALUATE SUMMARIZECOLUMNS ( 'Date'[YearMonth]{cols} ) ORDER BY 'Date'[YearMonth]");

            // The service principal comes from AZURE_* in the environment, or from the KEY=VALUE file SEMANTICUS_LIVE_SP_ENV_FILE names
            // (read here, never printed). The test host of some runners does not pass the variables on.
            var spFile = Environment.GetEnvironmentVariable("SEMANTICUS_LIVE_SP_ENV_FILE");
            if (!string.IsNullOrEmpty(spFile) && System.IO.File.Exists(spFile))
                foreach (var line in System.IO.File.ReadAllLines(spFile))
                {
                    var eq = line.IndexOf('=');
                    if (eq > 0 && !line.TrimStart().StartsWith("#"))
                        Environment.SetEnvironmentVariable(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim().Trim('"', '\''));
                }

            using var live = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await live.ConnectXmlaAsync("powerbi://api.powerbi.com/v1.0/myorg/FrameworkTesting", Environment.GetEnvironmentVariable("SEMANTICUS_LIVE_NORTHSTAR_DB"), "serviceprincipal", null);
            var rs = await live.RunDaxAsync(q.ToString(), 1000, "human");
            Assert.True(string.IsNullOrEmpty(rs.Error), rs.Error);

            var idx = rs.Columns.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name.Trim('[', ']'), x => x.i);
            int sawInside = 0, sawAfter = 0;
            foreach (var row in rs.Rows)
            {
                var ym = Convert.ToString(row[idx.First(k => k.Key.EndsWith("YearMonth")).Value]);
                var inside = string.CompareOrdinal(ym, "2025-09") <= 0;
                foreach (var b in new[] { "B1", "B2" })
                    foreach (var m in new[] { 3, 6, 12 })
                    {
                        var o = Num(row[idx[$"o{b}_{m}"]]); var n = Num(row[idx[$"n{b}_{m}"]]);
                        if (inside)
                        {
                            sawInside++;
                            Assert.True(o.HasValue == n.HasValue && (!o.HasValue || Math.Abs(o.Value - n.Value) < 0.005), $"{ym} {b} rolling {m}: old {o} new {n}");
                            if (ym == "2025-09" && b == "B1" && m == 12) Assert.Equal(36251380.48, n.Value, 2);
                        }
                        else { sawAfter++; Assert.Null(n); }
                    }
            }
            Assert.True(sawInside >= 6 * 12 && sawAfter > 0, "the query returned too few months to prove anything");
        }

        /// <summary>v2-m17 found it live: the last data date is read in the slice's own filters, so a customer (or a closed store) whose sales
        /// stop before the model's data does gets a BLANK rolling total in every month after their last sale. Customer M00647 last bought in
        /// Jul 2024; in Aug 2024 the old measure shows their trailing 12 months (2,964.20) and the generated one shows blank. Inside the data,
        /// a slice must keep the old number.</summary>
        [NorthstarLiveFact]
        public async Task A_slice_whose_own_sales_stop_early_keeps_its_rolling_total_inside_the_data()
        {
            var generated = new Dictionary<int, string>();
            foreach (var (variant, months) in new[] { ("R3M", 3), ("ROLL12", 12) })
            {
                using var local = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
                await local.CreateModelAsync("NsLive", 1604);
                var t = await local.CreateTableAsync("Sales", "agent");
                await local.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "agent");
                await local.CreateTableAsync("Date", "agent");
                await local.CreateColumnAsync("table:Date", "Date", "DateTime", "Date", "agent");
                await local.CreateMeasureAsync(t, "B1", "SUM ( Sales[Amount] )", "agent");
                var r = await local.GenerateTimeIntelligenceAsync("measure:Sales/B1", "column:Date/Date", new[] { variant }, null, "agent");
                generated[months] = r.Created.Single().Expression;
            }

            var q = new StringBuilder();
            q.AppendLine("DEFINE");
            q.AppendLine("  MEASURE Sales[B1] = CALCULATE ( [Net Sales], KEEPFILTERS ( 'Date'[Date] <= DATE ( 2025, 9, 17 ) ) )");
            foreach (var m in new[] { 3, 12 })
            {
                q.AppendLine($"  MEASURE Sales[Old_{m}] = CALCULATE ( [B1], DATESINPERIOD ( 'Date'[Date], MAX ( 'Date'[Date] ), -{m}, MONTH ) )");
                q.AppendLine($"  MEASURE Sales[New_{m}] = {generated[m]}");
            }
            q.AppendLine("EVALUATE CALCULATETABLE ( SUMMARIZECOLUMNS ( 'Date'[YearMonth], \"o3\", [Old_3], \"n3\", [New_3], \"o12\", [Old_12], \"n12\", [New_12] ), Customer[CustomerCode] = \"M00647\" ) ORDER BY 'Date'[YearMonth]");

            var spFile = Environment.GetEnvironmentVariable("SEMANTICUS_LIVE_SP_ENV_FILE");
            if (!string.IsNullOrEmpty(spFile) && System.IO.File.Exists(spFile))
                foreach (var line in System.IO.File.ReadAllLines(spFile))
                {
                    var eq = line.IndexOf('=');
                    if (eq > 0 && !line.TrimStart().StartsWith("#"))
                        Environment.SetEnvironmentVariable(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim().Trim('"', '\''));
                }

            using var live = new LocalEngine(new SessionManager(), TestEntitlements.Pro);
            await live.ConnectXmlaAsync("powerbi://api.powerbi.com/v1.0/myorg/FrameworkTesting", Environment.GetEnvironmentVariable("SEMANTICUS_LIVE_NORTHSTAR_DB"), "serviceprincipal", null);
            var rs = await live.RunDaxAsync(q.ToString(), 1000, "human");
            Assert.True(string.IsNullOrEmpty(rs.Error), rs.Error);

            var idx = rs.Columns.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name.Trim('[', ']'), x => x.i);
            var wrong = new List<string>();
            foreach (var row in rs.Rows)
            {
                var ym = Convert.ToString(row[idx.First(k => k.Key.EndsWith("YearMonth")).Value]);
                if (string.CompareOrdinal(ym, "2025-09") > 0) continue;
                foreach (var m in new[] { 3, 12 })
                {
                    var o = Num(row[idx[$"o{m}"]]); var n = Num(row[idx[$"n{m}"]]);
                    if (!(o.HasValue == n.HasValue && (!o.HasValue || Math.Abs(o.Value - n.Value) < 0.005))) wrong.Add($"{ym} rolling {m}: old {o} new {n}");
                }
            }
            Assert.True(rs.Rows.Count() >= 12, "the query returned too few months to prove anything");
            Assert.True(wrong.Count == 0, "a customer whose sales stop early loses its rolling total inside the data:\n" + string.Join("\n", wrong));
        }

        [Fact]
        public async Task The_other_variants_are_unchanged()
        {
            var r = await GenerateAsync("YTD", "PY");
            Assert.Contains(r.Created, c => c.Expression == "TOTALYTD([Total Amount], 'Date'[Date])");
            Assert.Contains(r.Created, c => c.Expression == "CALCULATE([Total Amount], DATEADD('Date'[Date], -1, YEAR))");
        }

        [Fact]
        public async Task A_rolling_window_is_written_with_balanced_brackets_so_the_engine_can_save_it()
        {
            var r = await GenerateAsync("ROLL12");
            var expr = r.Created.Single().Expression;
            Assert.Equal(expr.Count(c => c == '('), expr.Count(c => c == ')'));
            Assert.Equal(expr.Count(c => c == '['), expr.Count(c => c == ']'));
        }
    }
}
