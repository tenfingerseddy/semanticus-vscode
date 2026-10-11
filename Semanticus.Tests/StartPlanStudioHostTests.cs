// A REAL engine for the Studio's start plan driver (tools/uishot/startplan-real-drive.mjs): not a proof on its own, a host.
//
// A real engine process cannot read a Fabric schema offline: the schema seam (StartPlanSchemaReaderForTest) and the sample seam
// (SourceSampleExecutorForTest) exist only inside this assembly, and product code has no backdoor to them on purpose. So the
// browser driver runs THIS test as a long-lived child: it builds a real LocalEngine on the J8 schema with a fake source for the
// sample, serves it on a real named RPC pipe (the same RpcServer, handshake and framing the extension uses), writes where to
// find it to <dir>/pipe.json, and blocks until <dir>/stop exists. The driver then plays the person (the human pipe, which also
// carries HumanGovernanceRpcTarget's approveStartPlan) and the person's assistant (the agent pipe, which writes the words).
//
// Without SEMANTICUS_STUDIO_HOST_DIR the test does nothing and passes at once, so the suite never waits on it.
//   SEMANTICUS_STUDIO_HOST_DIR=<dir>          where pipe.json is written and the stop file is watched (required to host)
//   SEMANTICUS_STUDIO_HOST_TIER=pro           a Pro engine with a recorded-shape policy oracle (StartPlanDecideTests.PolicyOracle); Free otherwise
//   SEMANTICUS_STUDIO_HOST_MINUTES=<n>        the longest it hosts before it stops by itself (default 20)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class StartPlanStudioHostTests
    {
        private const string Challenge = "studio-host-challenge-0123456789abcdef";
        // The endpoint the driver types. A sample is read only from a Fabric SQL endpoint (the J8 schema's own contoso-sql.example.com is refused
        // as "not one"), so this is the end-to-end proof's Fabric name for the same source.
        private const string SampleServer = "wh-j8.datawarehouse.fabric.microsoft.com", SampleDatabase = "ContosoConsulting";
        private readonly RestoreRootFixture _fixture;

        public StartPlanStudioHostTests(RestoreRootFixture fixture) { _fixture = fixture; }

        [Fact]
        public async Task Hosts_a_real_engine_on_a_pipe_for_the_studio_driver_when_asked()
        {
            var dir = Environment.GetEnvironmentVariable("SEMANTICUS_STUDIO_HOST_DIR");
            if (string.IsNullOrWhiteSpace(dir)) { Assert.True(string.IsNullOrWhiteSpace(dir), "no host dir named"); return; }   // the suite's run: nothing to host
            Directory.CreateDirectory(dir);
            var log = Path.Combine(dir, "host.log");
            void Log(string s) { lock (Challenge) File.AppendAllText(log, DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + s + Environment.NewLine); }
            var pro = string.Equals(Environment.GetEnvironmentVariable("SEMANTICUS_STUDIO_HOST_TIER"), "pro", StringComparison.OrdinalIgnoreCase);
            var minutes = int.TryParse(Environment.GetEnvironmentVariable("SEMANTICUS_STUDIO_HOST_MINUTES"), out var m) && m > 0 ? m : 20;

            // Every store the engine writes is under the host dir: a scratch home, its own connections, policy and ledger.
            var origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Directory.CreateDirectory(Path.Combine(dir, "home"));
            Directory.CreateDirectory(Path.Combine(dir, "policy"));
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(dir, "home"));
            ConnectionRegistry.RootOverride = Path.Combine(dir, "user");
            AgentPolicyStore.RootOverride = Path.Combine(dir, "policy");
            ApprovalLedger.RootOverride = Path.Combine(dir, "policy");
            AgentPolicyStore.SetPreset("open", "human");   // the person allowed the assistant to read a sample (QueryData), as the end-to-end proof does

            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, pro ? TestEntitlements.Pro : TestEntitlements.Free, Path.Combine(dir, "ws"));
            var source = new J8Source(SqlDraftBuildTests.J8Schema());
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => { Log($"schema read for {req?.Server}/{req?.Database} by {origin}"); return Task.FromResult(SqlDraftBuildTests.J8Schema()); };
            engine.SourceSampleExecutorForTest = source.Execute;
            if (pro)
            {
                engine.JevBatchedForTest = _ => true;
                engine.UseDecisionsForTest(new DecisionService(new StartPlanDecideTests.PolicyOracle()));
            }
            sessions.Bus.StartPlanChanged += v => Log($"startPlan/didChange digest {v?.Digest} decide {v?.Decide?.State} sample {v?.Sample?.State} built {v?.Built}");

            var pipeName = TestPipe.Mint("sem-sh-");
            using var server = new RpcServer(sessions, engine, pipeName, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            var stopFile = Path.Combine(dir, "stop");
            try
            {
                File.WriteAllText(Path.Combine(dir, "pipe.json.tmp"), JsonSerializer.Serialize(new
                {
                    pipe = pipeName, socket = TestPipe.SocketPathFor(Path.GetTempPath(), pipeName), challenge = Challenge,
                    tier = pro ? "pro" : "free", server = SampleServer, database = SampleDatabase,
                    pid = Environment.ProcessId,
                }));
                File.Move(Path.Combine(dir, "pipe.json.tmp"), Path.Combine(dir, "pipe.json"), true);
                Log($"hosting {(pro ? "Pro" : "Free")} on {pipeName}");
                var until = DateTime.UtcNow.AddMinutes(minutes);
                while (!File.Exists(stopFile) && DateTime.UtcNow < until) await Task.Delay(200);
                Log(File.Exists(stopFile) ? "stop file seen" : $"stopped after {minutes} minutes with no stop file");
                Log($"sample queries {source.Queries}");
            }
            finally
            {
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                try { engine.Dispose(); } catch { }
                try { sessions.Dispose(); } catch { }
                Environment.SetEnvironmentVariable(SemanticusHome.Variable, origHome);
                ConnectionRegistry.RootOverride = _fixture.Root; AgentPolicyStore.RootOverride = _fixture.Root; ApprovalLedger.RootOverride = _fixture.Root;
            }
        }

        // ================================================================== the source's rows (a copy of StartPlanEndToEndTests.J8Source, in-code generator only)

        private sealed class J8Source
        {
            private readonly Dictionary<string, (string[] Cols, List<object[]> Rows)> _tables = new Dictionary<string, (string[], List<object[]>)>(StringComparer.OrdinalIgnoreCase);
            public int Queries;

            public J8Source(FabricSchema schema)
            {
                foreach (var t in schema.Tables) _tables["dbo." + t.Name] = (t.Columns.Select(c => c.Name).ToArray(), Generated(t));
            }

            // The same shape as the fixture's gen.py, smaller: yyyymmdd date keys, Y/N flags, coded dimensions, a blank PAID_DT_KEY for unpaid invoices,
            // a unique audit text column. Deterministic, no clock.
            private static List<object[]> Generated(FabricTable t)
            {
                var r = new Random(20260927 + t.Name.Length);
                var rows = new List<object[]>();
                string Pick(params string[] a) => a[r.Next(a.Length)];
                long Key(DateTime d) => d.Year * 10000 + d.Month * 100 + d.Day;
                var first = new DateTime(2024, 7, 1);
                switch (t.Name)
                {
                    case "ref_codes":
                        foreach (var type in new[] { "IND", "RGN", "GRD", "PRC", "PST", "ENG" })
                            for (var i = 1; i <= 4; i++) rows.Add(new object[] { type, type + i, type + " description " + i });
                        break;
                    case "dim_date":
                        for (var i = 0; i < 730; i++)
                        {
                            var d = first.AddDays(i);
                            var fy = d.Year + (d.Month >= 7 ? 1 : 0);
                            rows.Add(new object[] { Key(d), d, (long)fy, (long)(((d.Month - 7 + 12) % 12) / 3 + 1), (long)d.Month, d.ToString("MMM", CultureInfo.InvariantCulture), (long)((int)d.DayOfWeek + 1), d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday ? "N" : "Y" });
                        }
                        break;
                    case "dim_client":
                        for (var i = 1; i <= 60; i++) rows.Add(new object[] { "C" + i.ToString("000"), "Client " + i, Pick("IND1", "IND2", "IND3"), Pick("RGN1", "RGN2", "RGN3"), Pick("Y", "Y", "N"), Key(first.AddDays(r.Next(300))) });
                        break;
                    case "dim_staff":
                        for (var i = 1; i <= 80; i++) rows.Add(new object[] { "E" + i.ToString("000"), "Staff " + i, Pick("GRD1", "GRD2", "GRD3", "GRD4"), Pick("PRC1", "PRC2", "PRC3"), 80m + r.Next(0, 120), Key(first.AddDays(-r.Next(900))), Pick("N", "N", "N", "Y") });
                        break;
                    case "dim_project":
                        for (var i = 1; i <= 180; i++) rows.Add(new object[] { "P" + i.ToString("0000"), "C" + (r.Next(60) + 1).ToString("000"), "Project " + i, Pick("PRC1", "PRC2", "PRC3"), Pick("ENG1", "ENG2", "ENG3"), (long)r.Next(50, 800), 10000m + r.Next(0, 90000), Key(first.AddDays(r.Next(300))), Key(first.AddDays(300 + r.Next(300))), Pick("PST1", "PST2", "PST3") });
                        break;
                    case "fact_timesheet":
                        for (var i = 1; i <= 600; i++) rows.Add(new object[] { "TS" + i.ToString("00000"), "E" + (r.Next(80) + 1).ToString("000"), "P" + (r.Next(180) + 1).ToString("0000"), Key(first.AddDays(r.Next(700))), (decimal)(r.Next(1, 16)) / 2m, Pick("Y", "Y", "N"), 90m + r.Next(0, 110), Pick("Y", "Y", "Y", "N"), "audit row " + i + " " + i.ToString("x8") });
                        break;
                    case "fact_invoice":
                        for (var i = 1; i <= 300; i++)
                        {
                            var d = first.AddDays(r.Next(600));
                            rows.Add(new object[] { "INV" + i.ToString("00000"), "P" + (r.Next(180) + 1).ToString("0000"), "C" + (r.Next(60) + 1).ToString("000"), Key(d), Key(d.AddDays(30)), i % 4 == 0 ? null : (object)Key(d.AddDays(r.Next(20, 60))), 1000m + r.Next(0, 90000), 100m + r.Next(0, 9000), Pick("AUD", "AUD", "NZD"), Pick("N", "N", "N", "Y") });
                        }
                        break;
                }
                return rows;
            }

            public Task<ResultSet> Execute(string server, string database, string sql, int maxRows)
            {
                Interlocked.Increment(ref Queries);
                var count = Regex.Match(sql, @"^SELECT COUNT_BIG\(\*\) FROM \[((?:[^\]]|\]\])*)\]\.\[((?:[^\]]|\]\])*)\]$");
                if (count.Success)
                {
                    var t = _tables[count.Groups[1].Value + "." + count.Groups[2].Value];
                    return Task.FromResult(new ResultSet { Columns = new[] { new ColumnDef { Name = "" } }, Rows = new[] { new object[] { (long)t.Rows.Count } }, RowCount = 1 });
                }
                var top = Regex.Match(sql, @"^SELECT TOP \((\d+)\) (.+) FROM \[((?:[^\]]|\]\])*)\]\.\[((?:[^\]]|\]\])*)\]$");
                if (!top.Success) return Task.FromResult(ResultSet.FromError("Unexpected query in the J8 fixture: " + sql));
                var n = int.Parse(top.Groups[1].Value, CultureInfo.InvariantCulture);
                var tbl = _tables[top.Groups[3].Value + "." + top.Groups[4].Value];
                var names = Regex.Matches(top.Groups[2].Value, @"\[((?:[^\]]|\]\])*)\]").Select(x => x.Groups[1].Value.Replace("]]", "]")).ToArray();
                var idx = names.Select(c => Array.IndexOf(tbl.Cols, c)).ToArray();
                var rows = tbl.Rows.Take(Math.Min(n, maxRows)).Select(row => idx.Select(j => row[j]).ToArray()).ToArray();
                return Task.FromResult(new ResultSet { Columns = names.Select(c => new ColumnDef { Name = c }).ToArray(), Rows = rows, RowCount = rows.Length, Truncated = tbl.Rows.Count > rows.Length });
            }
        }
    }
}
