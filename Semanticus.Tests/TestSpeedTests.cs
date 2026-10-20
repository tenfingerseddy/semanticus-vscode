using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// A full Validation run used to make about a thousand round trips one after another (Kane, 12 Oct: "validation
    /// with lots of tests is a bit slow"). These pin the speed-up AND the rule that came with it: every check gives the
    /// same pass or fail, the same numbers and the same words as before. The fake live connection counts every query
    /// it is asked and answers from fixed values, so a change in the number of round trips, or in what a check says,
    /// shows up here with no endpoint.
    /// </summary>
    [Collection("restore-root")]
    public sealed class TestSpeedTests : IDisposable
    {
        private const string Server = "fabric.example.com";
        private const string Database = "LH_Gold";
        private const string DaxEndpoint = "powerbi://example/workspace";

        private readonly string _root;
        private readonly string _safeRoot;

        public TestSpeedTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-testspeed-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            ConnectionRegistry.RootOverride = Path.Combine(_root, "registry");
            // The checks below name this server inline. A sign-in goes only to a server saved on this machine.
            SqlSourceRegistry.Save(null, "Fabric", Server, Database, "azcli", null);
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        // ---- a small model: Sales with two foreign keys, two lookups, three measures ------------------------------
        private SessionManager _sessions;

        private async Task<LocalEngine> ModelAsync(string name, params string[] measures)
        {
            var dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            _sessions = new SessionManager();
            var engine = new LocalEngine(_sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync(name, 1604);
            await engine.CreateTableAsync("Sales", "human");
            await engine.CreateTableAsync("Dim1", "human");
            await engine.CreateTableAsync("Dim2", "human");
            foreach (var (t, c, type) in new[] { ("Sales", "K1", "int64"), ("Sales", "K2", "int64"), ("Sales", "Region", "string"), ("Dim1", "K", "int64"), ("Dim2", "K", "int64") })
                await engine.CreateColumnAsync(t, c, type, null, "human");
            await engine.CreateRelationshipAsync("column:Sales/K1", "column:Dim1/K", null, true, "human");
            await engine.CreateRelationshipAsync("column:Sales/K2", "column:Dim2/K", null, true, "human");
            foreach (var m in measures.DefaultIfEmpty("Total Rows"))
                await engine.CreateMeasureAsync("Sales", m, "COUNTROWS('Sales')", "human");
            await engine.SaveAsync(dir, "TMDL", false, "human");
            return engine;
        }

        // A saved check in the one shape that can PASS: grouped by region, with its own grand total. (A grand total alone
        // is "insufficient coverage" by design.) The tag goes into both SQL texts so a fake can tell the checks apart.
        private static Task<TestDefinition> SaveCheckAsync(LocalEngine engine, string measure, string title = null,
            string authMode = null, string tenantId = null) =>
            engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureReconcile, Title = title ?? measure, TargetRef = "measure:Sales/" + measure,
                ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                {
                    MeasureRef = measure, Server = Server, Database = Database, BlankPolicy = "zero",
                    GroupBy = new[] { "Sales[Region]" },
                    Sql = "SELECT members " + measure, SqlGrandTotal = "SELECT total " + measure,
                    AuthMode = authMode, TenantId = tenantId,
                }),
            }, "human");

        // What the model and the source answer when nothing goes wrong: East 10 and West 5, 15 in total, on both sides.
        private static ResultSet DaxAnswer(string q) => q.Contains("SUMMARIZECOLUMNS")
            ? new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "Region" }, new ColumnDef { Name = "__recon_value" }, new ColumnDef { Name = "__recon_present" } },
                Rows = new[] { new object[] { "East", 10.0, 1L }, new object[] { "West", 5.0, 1L } }, RowCount = 2, ElapsedMs = 11,
            }
            : Value(15.0, ms: 2);

        private static ResultSet SqlAnswer(string sql) => sql.Contains("total")
            ? Value(15.0, ms: 3)
            : new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "region" }, new ColumnDef { Name = "value" } },
                Rows = new[] { new object[] { "East", 10.0 }, new object[] { "West", 5.0 } }, RowCount = 2, ElapsedMs = 7,
            };

        private static ResultSet Value(object v, long ms = 0) => new ResultSet
        {
            Columns = new[] { new ColumnDef { Name = "[v]" } }, Rows = new[] { new object[] { v } }, RowCount = 1, ElapsedMs = ms,
        };

        private static ResultSet Failed(string why) => new ResultSet { Error = why };

        // The six probe labels, so a fake can tell the combined query from a single one.
        private static readonly string[] Labels = { "OrphanRows", "BlankForeignKeys", "DuplicateKeys", "BlankKeys", "ManyRowCount", "OneRowCount" };
        private static readonly object[] LabelValues = { 3L, 1L, 0L, null, 100L, 10L };

        private sealed class Counting
        {
            public readonly List<string> Queries = new List<string>();
            public bool RefuseCombined;
            public string BreakSingle;
            public int Count { get { lock (Queries) return Queries.Count; } }

            public ResultSet Answer(string q)
            {
                lock (Queries) Queries.Add(q);
                var asked = Labels.Where(l => q.Contains("\"" + l + "\"")).ToArray();
                if (asked.Length == Labels.Length)
                    return RefuseCombined ? Failed("combined probes are not allowed here") : new ResultSet
                    {
                        Columns = Labels.Select(l => new ColumnDef { Name = "[" + l + "]" }).ToArray(),
                        Rows = new[] { LabelValues }, RowCount = 1,
                    };
                if (asked.Length == 1)
                    return asked[0] == BreakSingle ? Failed("this probe failed") : Value(LabelValues[Array.IndexOf(Labels, asked[0])]);
                return Value(5.0);
            }
        }

        // The saved checks the run ran, without the "no check yet" rows it lists for measures that have none.
        private static ReconcileOutcome[] Ran(TestSuiteRunResult run) =>
            run.Reconciles.Where(o => !o.DefId.StartsWith("unmapped:", StringComparison.Ordinal)).ToArray();

        private static string Fingerprint(TestSuiteRunResult run) =>
            TestSuiteStore.Serialize(run.Relationships.Relationships);

        // ---- 1. relationship probes: one round trip each ---------------------------------------------------------

        [Fact]
        public async Task A_relationship_costs_one_round_trip_not_six_and_gives_the_same_checks()
        {
            using var engine = await ModelAsync("Probes");
            var combined = new Counting();
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Probes", combined.Answer));
            var fast = await engine.RunTestSuiteAsync(sections: new[] { "relationships" });

            var oneByOne = new Counting { RefuseCombined = true };
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Probes", oneByOne.Answer));
            var slow = await engine.RunTestSuiteAsync(sections: new[] { "relationships" });

            Assert.Equal(2, combined.Count);                 // two relationships, one query each
            Assert.Equal(2 + 12, oneByOne.Count);            // refused: the combined try, then the six singles, per relationship
            Assert.Equal(Fingerprint(slow), Fingerprint(fast));
            var first = fast.Relationships.Relationships[0];
            Assert.Equal((long?)1, first.BlankForeignKeys);
            Assert.Equal((long?)0, first.BlankKeys);                 // a BLANK count is zero, in both shapes
            Assert.Equal((long?)100, first.ManyRowCount);
            Assert.Equal(Verdict.Fail, first.ReferentialIntegrity.Verdict);
            Assert.Equal((long?)3, first.ReferentialIntegrity.Count);
        }

        [Fact]
        public void The_combined_query_carries_the_exact_expression_of_each_single_probe()
        {
            // Same numbers by construction: every column of the combined query is the text a single query evaluates.
            var rel = new RelationshipCheckInput { ManyTable = "Sale's", ManyColumn = "Key]Id", OneTable = "Dim", OneColumn = "Id" };
            var single = RelationshipProbes.For(rel);
            var combined = RelationshipProbes.CombinedQuery(rel);
            foreach (var (label, query) in new[]
            {
                ("OrphanRows", single.OrphanRows), ("BlankForeignKeys", single.BlankForeignKeys), ("DuplicateKeys", single.DuplicateKeys),
                ("BlankKeys", single.BlankKeys), ("ManyRowCount", single.ManyRowCount), ("OneRowCount", single.OneRowCount),
            })
            {
                var comma = query.IndexOf(',');
                var expr = query.Substring(comma + 1).TrimEnd();
                if (expr.EndsWith(")")) expr = expr.Substring(0, expr.Length - 1).TrimEnd();   // the ROW( closes after the expression
                Assert.Contains(expr.Trim(), combined);
                Assert.Contains("\"" + label + "\"", combined);
            }
            Assert.StartsWith("EVALUATE", combined);
        }

        [Fact]
        public async Task A_failed_probe_blanks_only_itself()
        {
            using var engine = await ModelAsync("Partial");
            var live = new Counting { RefuseCombined = true, BreakSingle = "DuplicateKeys" };
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Partial", live.Answer));

            var run = await engine.RunTestSuiteAsync(sections: new[] { "relationships" });

            foreach (var rel in run.Relationships.Relationships)
            {
                Assert.Equal(Verdict.NotVerifiable, rel.KeyUniqueness.Verdict);   // its own probe failed
                Assert.Equal((long?)1, rel.BlankForeignKeys);                            // the others kept what landed
                Assert.Equal((long?)100, rel.ManyRowCount);
                Assert.Equal((long?)3, rel.ReferentialIntegrity.Count);
            }
        }

        // ---- 2. the reconcile SQL token is fetched once per run, per sign-in mode and tenant ---------------------

        [Fact]
        public async Task The_sql_token_is_fetched_once_per_mode_and_tenant_not_once_per_check()
        {
            using var engine = await ModelAsync("Tokens", "A", "B", "C", "D", "E", "F");
            await SaveCheckAsync(engine, "A", authMode: "interactive");
            await SaveCheckAsync(engine, "B", authMode: "interactive");
            await SaveCheckAsync(engine, "C", authMode: "interactive");
            await SaveCheckAsync(engine, "D", authMode: null);          // no mode means azcli
            await SaveCheckAsync(engine, "E", authMode: "azcli");
            await SaveCheckAsync(engine, "F", authMode: "interactive", tenantId: "tenant-two");
            var fetched = new List<(string Mode, string Tenant)>();
            engine.SqlTokenFetchForTests = (mode, tenant, origin) => { lock (fetched) fetched.Add((mode, tenant)); return "token"; };
            engine.ReconcileSqlForTests = (server, db, sql) => SqlAnswer(sql);
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Tokens", DaxAnswer));

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });

            Assert.Equal(6, Ran(run).Length);
            Assert.All(Ran(run), o => Assert.True(o.Verdict == Verdict.Pass, o.Message));
            Assert.Equal(3, fetched.Count);   // interactive, azcli, interactive on tenant-two
            Assert.Equal(3, fetched.Distinct().Count());
        }

        [Fact]
        public async Task A_token_that_could_not_be_fetched_is_tried_again_for_the_next_check_with_the_same_words()
        {
            using var engine = await ModelAsync("BadToken", "A", "B");
            await SaveCheckAsync(engine, "A", authMode: "interactive");
            await SaveCheckAsync(engine, "B", authMode: "interactive");
            var tries = 0;
            engine.SqlTokenFetchForTests = (mode, tenant, origin) => { tries++; throw new InvalidOperationException("sign-in was cancelled"); };
            engine.ReconcileSqlForTests = (server, db, sql) => SqlAnswer(sql);
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "BadToken", DaxAnswer));

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });

            Assert.Equal(2, tries);   // a failure is never remembered
            Assert.All(Ran(run), o =>
            {
                Assert.Equal(Verdict.NotVerifiable, o.Verdict);
                Assert.Contains("Could not acquire a SQL token: sign-in was cancelled", o.Message);
            });
        }

        // ---- 3. a recorded run keeps its time, and each check keeps its DAX and SQL time -------------------------

        [Fact]
        public async Task A_recorded_run_keeps_its_duration_and_each_checks_dax_and_sql_time()
        {
            using var engine = await ModelAsync("Timing", "A");
            await SaveCheckAsync(engine, "A");
            var lines = new List<string>();
            engine.RunLogForTests = lines.Add;
            engine.ReconcileSqlForTests = (server, db, sql) => SqlAnswer(sql);
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Timing", q =>
            {
                Thread.Sleep(25);   // so a whole run cannot honestly take 0 ms
                return DaxAnswer(q);
            }));

            var run = await engine.RunTestSuiteAsync(persist: true, origin: "human");

            Assert.True(run.Persisted);
            Assert.True(run.DurationMs > 0);
            var record = Assert.Single((await engine.ListTestRunsAsync(5)).Runs);
            Assert.True(record.DurationMs > 0, "the saved record carries the run's duration");
            var detail = await engine.GetTestRunAsync(record.RunId);
            var check = detail.Run.Outcomes.Checks.Single(c => c.Kind == "savedCheck");
            Assert.Equal((long?)(11 + 2), check.DaxMs);   // the members, then the grand total
            Assert.Equal((long?)(7 + 3), check.SqlMs);

            // One engine log line per phase: counts and milliseconds, never a value from the model or a query.
            Assert.Contains(lines, l => l.StartsWith("[tests] relationships:", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("[tests] table counts:", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("[tests] compare with source:", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("[tests] trusted numbers:", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("[tests] run:", StringComparison.Ordinal));
            Assert.All(lines, l =>
            {
                Assert.DoesNotContain("East", l);
                Assert.DoesNotContain("SELECT", l);
                Assert.DoesNotContain("EVALUATE", l);
                Assert.Matches(@"^\[tests\] [a-z ]+: .*\d+ ms\)?$", l);
            });
        }

        // ---- 4. the DAX and the SQL of one check run side by side ------------------------------------------------

        [Fact]
        public async Task A_checks_sql_starts_while_its_dax_is_still_running()
        {
            using var engine = await ModelAsync("Overlap", "A");
            await SaveCheckAsync(engine, "A");
            var sqlStarted = new ManualResetEventSlim(false);
            var calls = 0;
            var firstDaxSawSql = false;
            engine.ReconcileSqlForTests = (server, db, sql) => { sqlStarted.Set(); return SqlAnswer(sql); };
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Overlap", q =>
            {
                if (Interlocked.Increment(ref calls) == 1) firstDaxSawSql = sqlStarted.Wait(TimeSpan.FromSeconds(3));
                return DaxAnswer(q);
            }));

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });

            Assert.True(firstDaxSawSql, "the SQL must have started before the first DAX finished");
            var only = Ran(run).Single();
            Assert.True(only.Verdict == Verdict.Pass, only.Message);
        }

        [Fact]
        public async Task Ok_dax_error_sql_error_and_both_error_read_the_same_and_stay_in_order()
        {
            using var engine = await ModelAsync("Order", "M_ok", "M_dax", "M_sql", "M_both", "M_gtdax", "M_gtsql", "M_gtboth");
            foreach (var m in new[] { "M_ok", "M_dax", "M_sql", "M_both", "M_gtdax", "M_gtsql", "M_gtboth" })
                await SaveCheckAsync(engine, m);
            engine.ReconcileSqlForTests = (server, db, sql) =>
                sql.Contains("members M_sql") || sql.Contains("members M_both") || sql.Contains("total M_gtsql") || sql.Contains("total M_gtboth")
                    ? Failed("sql went wrong") : SqlAnswer(sql);
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Order", q =>
            {
                var member = q.Contains("SUMMARIZECOLUMNS");
                var broken = member ? q.Contains("[M_dax]") || q.Contains("[M_both]") : q.Contains("[M_gtdax]") || q.Contains("[M_gtboth]");
                return broken ? Failed("dax went wrong") : DaxAnswer(q);
            }));

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });

            var ran = Ran(run);
            Assert.Equal(new[] { "M_ok", "M_dax", "M_sql", "M_both", "M_gtdax", "M_gtsql", "M_gtboth" }, ran.Select(o => o.Title).ToArray());
            Assert.True(ran[0].Verdict == Verdict.Pass, ran[0].Message);
            foreach (var o in ran.Skip(1)) Assert.Equal(Verdict.NotVerifiable, o.Verdict);
            Assert.Contains("DAX member query", ran[1].Message);       Assert.Contains("dax went wrong", ran[1].Message);
            Assert.Contains("SQL member query", ran[2].Message);       Assert.Contains("sql went wrong", ran[2].Message);
            Assert.Contains("DAX member query", ran[3].Message);       Assert.DoesNotContain("sql went wrong", ran[3].Message);   // the DAX error is reported first
            Assert.Contains("DAX total query", ran[4].Message);  Assert.Contains("dax went wrong", ran[4].Message);
            Assert.Contains("SQL total query", ran[5].Message);  Assert.Contains("sql went wrong", ran[5].Message);
            Assert.Contains("DAX total query", ran[6].Message);  Assert.DoesNotContain("sql went wrong", ran[6].Message);
        }

        // ---- 5. progress while a run goes ------------------------------------------------------------------------

        private async Task<(List<OperationProgress> Events, TestSuiteRunResult Run, int Units)> RunWithProgressAsync(
            string name, TimeSpan interval, int saved, int daxSleepMs = 0, string runId = null)
        {
            var measures = Enumerable.Range(0, saved).Select(i => "M" + i).ToArray();
            using var engine = await ModelAsync(name, measures);
            foreach (var m in measures) await SaveCheckAsync(engine, m);
            engine.ReconcileSqlForTests = (server, db, sql) => SqlAnswer(sql);
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, name, q =>
            {
                if (daxSleepMs > 0) Thread.Sleep(daxSleepMs);
                return q.Contains("\"OrphanRows\"") ? ProbeAll() : DaxAnswer(q);
            }));
            engine.RunProgressIntervalForTests = interval;
            var events = new List<OperationProgress>();
            _sessions.Bus.Progress += e => { lock (events) events.Add(e); };

            var run = await engine.RunTestSuiteAsync(origin: "human", runId: runId);

            // The checks that take time: each relationship, each table count, each saved check.
            var units = run.Relationships.Relationships.Count + run.Relationships.TableRowCounts.Count + saved;
            lock (events) return (events.ToList(), run, units);
        }

        private static ResultSet ProbeAll() => new ResultSet
        {
            Columns = Labels.Select(l => new ColumnDef { Name = "[" + l + "]" }).ToArray(), Rows = new[] { LabelValues }, RowCount = 1,
        };

        [Fact]
        public async Task A_fast_run_tells_the_page_where_it_started_and_where_it_ended_not_once_per_check()
        {
            var (events, run, units) = await RunWithProgressAsync("Fast", TimeSpan.FromHours(1), saved: 24, runId: "page-run-7");

            Assert.NotNull(run.Health);
            Assert.Equal(2, events.Count);
            Assert.All(events, e => { Assert.Equal("run_tests", e.OpKey); Assert.Equal("page-run-7", e.RunId); Assert.Equal(units, e.Total); });
            Assert.Equal(0, events[0].Done);
            Assert.Equal(units, events[1].Done);
        }

        [Fact]
        public async Task A_slower_run_reports_a_few_times_and_the_count_only_goes_up()
        {
            var (events, _, units) = await RunWithProgressAsync("Slow", TimeSpan.FromMilliseconds(40), saved: 24, daxSleepMs: 8);

            Assert.True(events.Count >= 4, "a slow run reports while it goes: " + events.Count);
            Assert.True(events.Count < units, "but not once per check: " + events.Count + " of " + units);
            for (var i = 1; i < events.Count; i++) Assert.True(events[i].Done >= events[i - 1].Done);
            Assert.Equal(0, events[0].Done);
            Assert.Equal(units, events[^1].Done);
            Assert.Single(events.Select(e => e.RunId).Distinct());   // the run names itself when the page gave no id
        }

        [Fact]
        public async Task A_run_with_nothing_to_check_sends_no_progress()
        {
            using var engine = await ModelAsync("Empty", "Idle");   // a measure with no saved check, and no live connection
            var events = new List<OperationProgress>();
            _sessions.Bus.Progress += events.Add;

            await engine.RunTestSuiteAsync(origin: "human");

            Assert.Empty(events);
        }

        [Fact]
        public async Task A_grouped_check_with_a_grand_total_keeps_its_numbers_stamps_and_times()
        {
            using var engine = await ModelAsync("Grouped", "M");
            var sqlSeen = new List<string>();
            engine.ReconcileSqlForTests = (server, db, sql) => { lock (sqlSeen) sqlSeen.Add(sql); return SqlAnswer(sql); };
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Grouped", DaxAnswer));
            var before = DateTime.UtcNow;

            var r = await engine.ReconcileMeasureAsync(new ReconcileRequest
            {
                MeasureRef = "M", Server = Server, Database = Database, BlankPolicy = "zero",
                GroupBy = new[] { "Sales[Region]" }, Sql = "SELECT members", SqlGrandTotal = "SELECT total",
            });

            Assert.Equal(ReconcileStatus.Reconciled, r.Status);
            Assert.Equal(3, r.Matches);   // two members and the grand total
            Assert.True(r.HasGrandTotal);
            Assert.Equal(11 + 2, r.DaxElapsedMs);
            Assert.Equal(7 + 3, r.SqlElapsedMs);
            Assert.True(r.DaxExecutedAtUtc >= before && r.SqlExecutedAtUtc >= before);
            Assert.Equal(new[] { "SELECT members", "SELECT total" }, sqlSeen.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }

        // ---- 6. trusted numbers ask in batches, with the same answers ---------------------------------------------

        private static Task<TestDefinition> SaveValueCheckAsync(LocalEngine engine, string measure, string expected, string expressionDax = null) =>
            engine.SaveTestDefinitionAsync(new TestDefinition
            {
                Kind = TestKinds.MeasureValue, Title = measure, TargetRef = "measure:Sales/" + measure,
                ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { ExpectedValue = expected, ExpressionDax = expressionDax }),
            }, "human");

        // A model that answers a measure named M7 with 8, in the shape the query asks: one column v for a single query, a column
        // v0, v1, ... for a batch. It counts every query and can be told to refuse batches or to break on one measure.
        private sealed class ValueModel
        {
            public readonly List<string> Queries = new List<string>();
            public bool RefuseBatch;
            public bool ShortBatch;
            public string BreakOn;
            public int Count { get { lock (Queries) return Queries.Count; } }
            public int Batches { get { lock (Queries) return Queries.Count(q => q.Contains("\"v1\"")); } }

            private static readonly Regex Column = new Regex("\"v(\\d*)\", \\[(M\\d+)\\]", RegexOptions.Compiled);
            private static double Answer(string measure) => int.Parse(measure.Substring(1), System.Globalization.CultureInfo.InvariantCulture) + 1;

            public ResultSet Ask(string q)
            {
                lock (Queries) Queries.Add(q);
                if (BreakOn != null && q.Contains("[" + BreakOn + "]")) return Failed("measure " + BreakOn + " could not be calculated");
                var cols = Column.Matches(q).Cast<Match>().ToArray();
                if (cols.Length == 0) return DaxAnswer(q);
                if (q.Contains("\"v1\""))
                {
                    if (RefuseBatch) return Failed("batches are not allowed here");
                    var n = ShortBatch ? cols.Length - 1 : cols.Length;
                    return new ResultSet
                    {
                        Columns = cols.Take(n).Select(c => new ColumnDef { Name = "[v" + c.Groups[1].Value + "]" }).ToArray(),
                        Rows = new[] { cols.Take(n).Select(c => (object)Answer(c.Groups[2].Value)).ToArray() }, RowCount = 1,
                    };
                }
                return Value(Answer(cols[0].Groups[2].Value));
            }
        }

        private async Task<LocalEngine> ValueChecksAsync(string name, int count, params (int Index, string Expected)[] wrong)
        {
            var engine = await ModelAsync(name, Enumerable.Range(0, count).Select(i => "M" + i).ToArray());
            for (var i = 0; i < count; i++)
                await SaveValueCheckAsync(engine, "M" + i, wrong.Any(w => w.Index == i) ? wrong.First(w => w.Index == i).Expected : (i + 1).ToString());
            return engine;
        }

        private static string[] Said(TestSuiteRunResult run) =>
            Ran(run).Select(o => string.Join("|", o.Title, o.Verdict, o.Message, o.Expected, o.Actual, o.Difference, o.Matches, o.Mismatches)).ToArray();

        [Fact]
        public async Task Trusted_numbers_are_asked_twenty_to_a_query_and_every_check_passes_as_before()
        {
            using var engine = await ValueChecksAsync("Batch", 45);
            var model = new ValueModel();
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Batch", model.Ask));

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });

            var ran = Ran(run);
            Assert.Equal(45, ran.Length);
            Assert.All(ran, o => Assert.True(o.Verdict == Verdict.Pass, o.Title + ": " + o.Message));
            Assert.Equal(3, model.Count);   // 20 + 20 + 5, not 45
            Assert.Equal(new[] { "M0", "M1", "M2" }, ran.Take(3).Select(o => o.Title).ToArray());   // same order as the suite
            Assert.All(ran, o => Assert.Contains("\"v", o.Dax));   // a pass shows the query that produced its number: the batch
            Assert.Equal("1", ran[0].Expected);
        }

        [Fact]
        public async Task A_batch_gives_the_same_words_numbers_and_verdicts_as_asking_one_by_one()
        {
            var wrong = new[] { (3, "999"), (17, "12.5") };
            using var batched = await ValueChecksAsync("SameA", 25, wrong);
            var fast = new ValueModel();
            batched.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "SameA", fast.Ask));
            var a = await batched.RunTestSuiteAsync(sections: new[] { "measures" });

            using var single = await ValueChecksAsync("SameB", 25, wrong);
            var slow = new ValueModel { RefuseBatch = true };
            single.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "SameB", slow.Ask));
            var b = await single.RunTestSuiteAsync(sections: new[] { "measures" });

            Assert.Equal(Said(b), Said(a));
            Assert.Equal(2, Ran(a).Count(o => o.Verdict == Verdict.Fail));
            Assert.Equal(a.Health.Grade, b.Health.Grade);
            Assert.Equal(a.Health.Passed, b.Health.Passed);
            Assert.True(fast.Count < slow.Count, fast.Count + " round trips against " + slow.Count);
        }

        [Fact]
        public async Task A_check_that_does_not_match_is_asked_again_alone_so_its_evidence_is_the_exact_query()
        {
            using var engine = await ValueChecksAsync("Evidence", 5, (2, "999"));
            var model = new ValueModel();
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Evidence", model.Ask));

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });

            var ran = Ran(run);
            Assert.Equal(2, model.Count);   // the batch, then the one that did not match
            Assert.Equal(Verdict.Fail, ran[2].Verdict);
            Assert.Equal("EVALUATE ROW(\"v\", [M2])", ran[2].Dax);   // what a person can paste and run
            Assert.Equal("3", ran[2].Actual);
            Assert.All(ran.Where((_, i) => i != 2), o => Assert.Equal(Verdict.Pass, o.Verdict));
        }

        [Fact]
        public async Task A_batch_that_errors_is_dropped_and_each_check_is_asked_alone_so_only_the_bad_one_blanks()
        {
            using var engine = await ValueChecksAsync("Broken", 8);
            var model = new ValueModel { BreakOn = "M5" };
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Broken", model.Ask));

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });

            var ran = Ran(run);
            Assert.Equal(1 + 8, model.Count);   // the batch that failed, then one query each
            Assert.Equal(Verdict.NotVerifiable, ran[5].Verdict);
            Assert.Contains("measure M5 could not be calculated", ran[5].Message);
            Assert.All(ran.Where((_, i) => i != 5), o => Assert.Equal(Verdict.Pass, o.Verdict));
            Assert.Equal("EVALUATE ROW(\"v\", [M0])", ran[0].Dax);
        }

        [Fact]
        public async Task A_batch_that_comes_back_the_wrong_shape_is_not_believed()
        {
            using var engine = await ValueChecksAsync("Shape", 6);
            var model = new ValueModel { ShortBatch = true };
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Shape", model.Ask));

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });

            Assert.Equal(1 + 6, model.Count);
            Assert.All(Ran(run), o => Assert.Equal(Verdict.Pass, o.Verdict));
        }

        [Fact]
        public async Task A_check_with_a_comment_in_its_text_is_never_put_in_a_batch()
        {
            using var engine = await ModelAsync("Comment", "M0", "M1", "M2");
            await SaveValueCheckAsync(engine, "M0", "1");
            await SaveValueCheckAsync(engine, "M1", "2", expressionDax: "[M1] // the second one");
            await SaveValueCheckAsync(engine, "M2", "3");
            var model = new ValueModel();
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Comment", model.Ask));

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });

            Assert.Equal(3, Ran(run).Length);
            lock (model.Queries)
            {
                Assert.Equal(1, model.Queries.Count(q => q.Contains("//")));
                Assert.DoesNotContain(model.Queries, q => q.Contains("//") && q.Contains("\"v1\""));   // it is asked alone
                Assert.Equal(2, model.Queries.Count);   // M0 and M2 together, the commented one alone
            }
        }

        [Fact]
        public void The_batch_query_is_one_row_with_a_column_per_check_and_each_column_is_the_single_checks_own_expression()
        {
            var single = LocalEngine.BuildMeasureValueDax("Total Sales", null, "'Date'[Year] = 2024", out var refusal);
            var scalar = LocalEngine.BuildMeasureValueScalar("Total Sales", null, "'Date'[Year] = 2024");
            Assert.Null(refusal);
            Assert.Equal("EVALUATE ROW(\"v\", " + scalar + ")", single);
            Assert.Null(LocalEngine.BuildMeasureValueScalar("Total Sales", "EVALUATE ROW(\"v\", 1)", null));   // a complete query is never a column
            var batch = LocalEngine.BuildMeasureValueBatchDax(new[] { scalar, "[Other]" });
            Assert.StartsWith("EVALUATE ROW(", batch);
            Assert.Contains("\"v0\", " + scalar, batch);
            Assert.Contains("\"v1\", [Other]", batch);
        }

        // ---- 7. the SQL of several checks runs side by side, three at a time, in the same order of results -------

        [Fact]
        public async Task The_sql_of_different_checks_runs_together_up_to_three_and_results_keep_their_order()
        {
            var names = Enumerable.Range(0, 8).Select(i => "C" + i).ToArray();
            using var engine = await ModelAsync("Parallel", names);
            foreach (var m in names) await SaveCheckAsync(engine, m);
            var running = 0; var most = 0;
            engine.ReconcileSqlForTests = (server, db, sql) =>
            {
                var now = Interlocked.Increment(ref running);
                lock (names) most = Math.Max(most, now);
                Thread.Sleep(120);
                Interlocked.Decrement(ref running);
                return SqlAnswer(sql);
            };
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Parallel", DaxAnswer));

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });

            Assert.Equal(names, Ran(run).Select(o => o.Title).ToArray());
            Assert.All(Ran(run), o => Assert.True(o.Verdict == Verdict.Pass, o.Message));
            Assert.Equal(3, most);   // three at once: never more, and more than one check's own two
        }

        [Fact]
        public async Task Checks_that_wait_on_the_same_sign_in_share_one_fetch_even_when_they_ask_together()
        {
            var names = new[] { "A", "B", "C", "D" };
            using var engine = await ModelAsync("SharedToken", names);
            foreach (var m in names) await SaveCheckAsync(engine, m, authMode: "interactive");
            var fetches = 0;
            engine.SqlTokenFetchForTests = (mode, tenant, origin) => { Interlocked.Increment(ref fetches); Thread.Sleep(200); return "token"; };
            engine.ReconcileSqlForTests = (server, db, sql) => SqlAnswer(sql);
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "SharedToken", DaxAnswer));

            var run = await engine.RunTestSuiteAsync(sections: new[] { "measures" });

            Assert.Equal(1, fetches);
            Assert.All(Ran(run), o => Assert.True(o.Verdict == Verdict.Pass, o.Message));
        }

        // ---- 8. table-count SQL starts while the relationship DAX still runs ---------------------------------------

        [Fact]
        public async Task A_table_counts_sql_starts_before_the_relationship_probes_have_finished()
        {
            using var engine = await ModelAsync("TableOverlap", "Idle");
            await engine.CreateImportTableAsync("Orders", "let Source = Sql.Database(\"fabric.example.com\", \"LH_Gold\"), t = Source{[Schema=\"dbo\",Item=\"orders\"]}[Data] in t", "human");
            await engine.SaveAsync(Path.Combine(_root, "TableOverlap"), "TMDL", false, "human");
            var sqlStarted = new ManualResetEventSlim(false);
            var firstProbeSawSql = false;
            var calls = 0;
            engine.SqlTokenFetchForTests = (mode, tenant, origin) => "token";
            engine.ReconcileSqlForTests = (server, db, sql) => { sqlStarted.Set(); return Value(15L); };
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "TableOverlap", q =>
            {
                if (q.Contains("\"OrphanRows\"") && Interlocked.Increment(ref calls) == 1) firstProbeSawSql = sqlStarted.Wait(TimeSpan.FromSeconds(3));
                return q.Contains("\"OrphanRows\"") ? ProbeAll() : q.Contains("__table_rows") ? Value(15L) : DaxAnswer(q);
            }));

            var run = await engine.RunTestSuiteAsync(sections: new[] { "relationships", "tableCounts" });

            Assert.True(firstProbeSawSql, "the table SQL must have started before the first relationship probe finished");
            var row = run.Relationships.TableRowCounts.Single(t => t.ModelTable == "Orders");
            Assert.Equal((long?)15, row.ModelCount);
            Assert.Equal((long?)15, row.SourceCount);
        }

        // ---- 9. the Validation tools the assistant runs reach the page as one sentence ---------------------------

        [Fact]
        public async Task A_single_try_a_saved_or_deleted_check_and_a_recorded_run_each_reach_the_page_as_one_sentence()
        {
            using var engine = await ModelAsync("Notes", "M0");
            var heard = new List<ActivityEvent>();
            _sessions.Bus.Activity += e => { lock (heard) heard.Add(e); };
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", DaxEndpoint, "Notes", q => Value(1.0)));
            var def = new TestDefinition
            {
                Kind = TestKinds.MeasureValue, Title = "M0 is one", TargetRef = "measure:Sales/M0",
                ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { ExpectedValue = "1" }),
            };

            var outcome = await McpToolsTesting.TryTest(engine, def);
            var saved = await McpToolsTesting.SaveTest(engine, def);
            var gone = await McpToolsTesting.DeleteTest(engine, saved.Id);
            var kept = await McpToolsTesting.RecordTestRun(engine, "no-such-run");

            Assert.Equal(Verdict.Pass, outcome.Verdict);
            Assert.True(gone);
            lock (heard)
            {
                Assert.Equal(new[] { "try_test", "save_test", "delete_test", "record_test_run" }, heard.Select(e => e.Kind).ToArray());
                Assert.All(heard, e => Assert.Equal("agent", e.Origin));
                Assert.StartsWith("Tried a check: M0 is one.", heard[0].Label);
                Assert.Contains("matched", heard[0].Label);
                Assert.True(heard[0].Ok);
                Assert.Equal("Saved a check: M0 is one.", heard[1].Label);
                Assert.Equal("Deleted a check.", heard[2].Label);
                Assert.False(kept.Recorded);
                Assert.False(heard[3].Ok);
                Assert.All(heard, e => Assert.DoesNotContain("\u2014", e.Label));
            }
        }
    }
}
