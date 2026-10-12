using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Facts;
using ModelFacts = Semanticus.Engine.Facts.ModelFacts;   // not the workflow predicate facts of the same name
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// L5 of the spec-first start (m11/sf-sample): the sample read of a Fabric SQL source and the facts it gives a
    /// model that has no live connection. A fake endpoint answers the sample's two T-SQL queries from in-memory
    /// rows, so every claim is checked against a count worked out by hand, and the privacy rule (derived facts only,
    /// never a raw value) is checked by scanning every serialized artifact for the fixture's literal values.
    /// </summary>
    [Collection("restore-root")]
    public sealed class SourceSampleTests : IDisposable
    {
        private const string Server = "wh-test.datawarehouse.fabric.microsoft.com";
        private const string Db = "SalesDW";
        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private readonly string _root = Path.Combine(Path.GetTempPath(), "sem-ss-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        private readonly string _safeRoot;
        private readonly string _policyRoot;

        public SourceSampleTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _policyRoot = Path.Combine(_root, "policy");
            Directory.CreateDirectory(Path.Combine(_root, "workspace"));
            Directory.CreateDirectory(_policyRoot);
            AgentPolicyStore.RootOverride = _policyRoot;
            ApprovalLedger.RootOverride = _policyRoot;
            ConnectionRegistry.RootOverride = Path.Combine(_root, "user");
        }

        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot; ConnectionRegistry.RootOverride = _safeRoot;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        // ---- The fixture's source -----------------------------------------------------------------------------

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };

        private static FabricSchema Schema() => new FabricSchema
        {
            Server = Server, Database = Db,
            Tables = new[]
            {
                new FabricTable { Schema = "dbo", Name = "dim_date", KeyColumns = new[] { "DATE_KEY" },
                    Columns = new[] { C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime"), C("FY_NO", "Int64"), C("FY_QTR", "Int64"), C("MTH_NO", "Int64") } },
                new FabricTable { Schema = "dbo", Name = "dim_customer", KeyColumns = new[] { "CUST_ID" },
                    Columns = new[] { C("CUST_ID", "String"), C("CUST_NM", "String"), C("EMAIL", "String"), C("COUNTRY", "String") } },
                new FabricTable { Schema = "dbo", Name = "fact_sales", KeyColumns = new[] { "SALE_ID" },
                    Columns = new[] { C("SALE_ID", "String"), C("CUST_ID", "String"), C("DATE_KEY", "Int64"), C("AMOUNT", "Decimal"), C("QTY", "Int64") } },
            },
            ForeignKeys = new[]
            {
                new FabricForeignKey { FromSchema = "dbo", FromTable = "fact_sales", FromColumn = "CUST_ID", ToSchema = "dbo", ToTable = "dim_customer", ToColumn = "CUST_ID" },
                new FabricForeignKey { FromSchema = "dbo", FromTable = "fact_sales", FromColumn = "DATE_KEY", ToSchema = "dbo", ToTable = "dim_date", ToColumn = "DATE_KEY" },
            },
        };

        private static readonly string[] CustomerNames = { "Zq Pinecrest Alpha", "Zq Pinecrest Bravo", "Zq Pinecrest Charlie", "Zq Pinecrest Delta" };
        private static readonly string[] CustomerEmails = { "zq-alpha@contoso.example", "zq-bravo@contoso.example", "zq-charlie@contoso.example", "not an address zq" };
        private static readonly string[] Countries = { "Australia", "Canada", "New Zealand", "Zqlandia Sentinel" };

        /// <summary>A fake Fabric SQL endpoint. It understands only the sample's own two query shapes and records every
        /// query it was sent, so a test can count them and read the row limit each one carried.</summary>
        private sealed class FakeSource
        {
            public readonly Dictionary<string, (string[] Cols, Func<long> Count, Func<int, object[]> Row)> Tables
                = new Dictionary<string, (string[], Func<long>, Func<int, object[]>)>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Queries = new List<string>();
            public readonly List<int> MaxRows = new List<int>();
            public string FailTable;                 // a table whose queries come back with an error
            public string FailCountOf;               // a table whose COUNT times out
            public bool IgnoreTop;                   // an endpoint that sends more rows than TOP asked for
            public Func<string, string> ThrowFor;    // a query that throws instead of answering

            public static DateTime FirstDay => new DateTime(2022, 6, 1);
            public static int DateRows => (int)(new DateTime(2024, 8, 31) - FirstDay).TotalDays + 1;

            public FakeSource()
            {
                var dateCols = new[] { "DATE_KEY", "CAL_DT", "FY_NO", "FY_QTR", "MTH_NO" };
                Tables["dbo.dim_date"] = (dateCols, () => DateRows, i =>
                {
                    var d = FirstDay.AddDays(i);
                    var fy = d.Year + (d.Month >= 7 ? 1 : 0);
                    return new object[] { (long)(d.Year * 10000 + d.Month * 100 + d.Day), d, (long)fy, (long)(((d.Month - 7 + 12) % 12) / 3 + 1), (long)d.Month };
                });
                Tables["dbo.dim_customer"] = (new[] { "CUST_ID", "CUST_NM", "EMAIL", "COUNTRY" }, () => 4, i =>
                    new object[] { "C" + (i + 1), CustomerNames[i], CustomerEmails[i], Countries[i] });
                Tables["dbo.fact_sales"] = (new[] { "SALE_ID", "CUST_ID", "DATE_KEY", "AMOUNT", "QTY" }, () => 50, i =>
                    new object[] { "SALE-" + (7001 + i), "C" + (i % 4 + 1), (long)20230101 + i, 98765.4321m + i * 1.5m, (long)(i % 9 + 1) });
            }

            public Task<ResultSet> Execute(string server, string database, string sql, int maxRows)
            {
                lock (Queries) { Queries.Add(sql); MaxRows.Add(maxRows); }
                var thrown = ThrowFor?.Invoke(sql);
                if (thrown != null) throw new OperationCanceledException(thrown);
                var count = Regex.Match(sql, @"^SELECT COUNT_BIG\(\*\) FROM \[((?:[^\]]|\]\])*)\]\.\[((?:[^\]]|\]\])*)\]$");
                if (count.Success)
                {
                    var name = count.Groups[2].Value;
                    if (string.Equals(FailCountOf, name, StringComparison.OrdinalIgnoreCase)) return Task.FromResult(ResultSet.FromError("Execution Timeout Expired."));
                    var t = Tables[count.Groups[1].Value + "." + name];
                    return Task.FromResult(new ResultSet { Columns = new[] { new ColumnDef { Name = "" } }, Rows = new[] { new object[] { t.Count() } }, RowCount = 1 });
                }
                var top = Regex.Match(sql, @"^SELECT TOP \((\d+)\) (.+) FROM \[((?:[^\]]|\]\])*)\]\.\[((?:[^\]]|\]\])*)\]$");
                if (!top.Success) return Task.FromResult(ResultSet.FromError("Unexpected query in the sample fixture: " + sql));
                var n = int.Parse(top.Groups[1].Value, CultureInfo.InvariantCulture);
                var tn = top.Groups[3].Value + "." + top.Groups[4].Value;
                if (string.Equals(FailTable, top.Groups[4].Value, StringComparison.OrdinalIgnoreCase)) return Task.FromResult(ResultSet.FromError("Execution Timeout Expired."));
                var tbl = Tables[tn];
                var names = Regex.Matches(top.Groups[2].Value, @"\[((?:[^\]]|\]\])*)\]").Select(m => m.Groups[1].Value.Replace("]]", "]")).ToArray();
                var idx = names.Select(c => Array.IndexOf(tbl.Cols, c)).ToArray();
                var send = IgnoreTop ? (int)Math.Min(tbl.Count(), n + 10000) : (int)Math.Min(tbl.Count(), n);
                var rows = new List<object[]>();
                var truncated = false;
                for (var i = 0; i < send; i++)
                {
                    if (!IgnoreTop && rows.Count >= maxRows) { truncated = true; break; }
                    var r = tbl.Row(i);
                    rows.Add(idx.Select(j => r[j]).ToArray());
                }
                return Task.FromResult(new ResultSet { Columns = names.Select(c => new ColumnDef { Name = c }).ToArray(), Rows = rows.ToArray(), RowCount = rows.Count, Truncated = truncated });
            }
        }

        // An unlabelled target counts as dev since 1.4, so the tests of the ask loop label theirs production first.
        private static void LabelProd()
        {
            var rec = ConnectionRegistry.Remember("sql", Server, Db);
            ConnectionRegistry.SetLabel(rec.Id, "prod", "human");
        }

        private static SourceSampleTarget Target(string server = Server) => new SourceSampleTarget { Server = server, Database = Db };

        private static SourceSampleTable[] Tables(FabricSchema s) =>
            s.Tables.Select(t => new SourceSampleTable { Schema = t.Schema, Name = t.Name, Columns = t.Columns.Select(c => c.Name).ToArray() }).ToArray();

        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<LocalEngine, SessionManager> _sessions
            = new System.Runtime.CompilerServices.ConditionalWeakTable<LocalEngine, SessionManager>();

        private LocalEngine NewEngine(FakeSource src)
        {
            var sessions = new SessionManager();
            var e = new LocalEngine(sessions, TestEntitlements.Free, Path.Combine(_root, "workspace"));
            _sessions.Add(e, sessions);
            if (src != null) e.SourceSampleExecutorForTest = src.Execute;
            return e;
        }

        private Session Cur(LocalEngine e) => _sessions.TryGetValue(e, out var m) ? m.Current : null;

        /// <summary>The fixture's draft model: the schema through the product's own mapping, then built (no live
        /// connection anywhere), as a new model from a SQL source is.</summary>
        private async Task BuildDraftAsync(LocalEngine e)
        {
            await e.CreateModelAsync("Sales DW", 1604);
            var spec = LocalEngine.FabricSchemaToSpec(Schema(), Server, Db, "import");
            await e.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            var report = await e.BuildModelFromSpecAsync("human");
            Assert.Empty(report.Errors);
        }

        private static ColumnFacts Col(ModelFacts f, string table, string column) =>
            f.Tables.Single(t => t.Name == table).Columns.Single(c => c.Name == column);

        // An unlabelled target counts as test since 1.4 (3a4dc874), so a test of the QueryData gate labels the source production first.
        private static void LabelSourceProd() =>
            ConnectionRegistry.SetLabel(ConnectionRegistry.Remember("xmla", Server, Db, Db).Id, "prod", "human");

        // ---- Fails first 1: the QueryData gate -------------------------------------------------------------

        [Fact]
        public async Task An_agent_on_a_Deny_target_runs_0_sample_queries_and_the_person_is_never_gated()
        {
            var src = new FakeSource();
            using var e = NewEngine(src);
            LabelProd();
            AgentPolicyStore.SetPreset("client", "human");   // QueryData on a production target: deny
            LabelSourceProd();

            var refused = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "agent", CancellationToken.None);

            Assert.Empty(src.Queries);                                 // counted through the injected executor: 0
            Assert.Equal(0, refused.Queries);
            Assert.False(string.IsNullOrWhiteSpace(refused.Refused));
            Assert.Contains("does not permit", refused.Refused, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(ApprovalLedger.List());                       // a denial queues nothing
            Assert.All(refused.Tables, t => { Assert.Null(t.Rows); Assert.False(string.IsNullOrWhiteSpace(t.NotMeasured)); });
            Assert.Equal(Tables(Schema()).Length, refused.Tables.Length);   // the plan still sees every table, as "not measured"

            var person = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "human", CancellationToken.None);
            Assert.Null(person.Refused);
            Assert.NotEmpty(src.Queries);
            Assert.All(person.Tables, t => Assert.NotNull(t.Rows));
        }

        [Fact]
        public async Task An_agent_on_an_Ask_target_runs_0_queries_and_a_person_approval_is_queued()
        {
            var src = new FakeSource();
            using var e = NewEngine(src);
            LabelProd();
            AgentPolicyStore.SetPreset("standard", "human");   // QueryData on a production target: ask
            LabelSourceProd();

            var r = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "agent", CancellationToken.None);

            Assert.Empty(src.Queries);
            Assert.Contains("approval", r.Refused, StringComparison.OrdinalIgnoreCase);
            Assert.Single(ApprovalLedger.List());
        }

        [Fact]
        public async Task A_source_that_is_not_a_Fabric_endpoint_is_not_read_in_this_release()
        {
            var src = new FakeSource();
            using var e = NewEngine(src);
            AgentPolicyStore.SetPreset("open", "human");

            foreach (var host in new[] { "sql.contoso.example.com", "localhost", "10.0.0.5", "evil.com/.fabric.microsoft.com", "fabric.microsoft.com.evil.com" })
            {
                var r = await e.ReadSourceSampleAsync(Target(host), Tables(Schema()), "human", CancellationToken.None);
                Assert.Contains("Fabric", r.Refused);
                Assert.All(r.Tables, t => Assert.Null(t.Rows));
            }
            Assert.Empty(src.Queries);
            Assert.True(SourceSample.IsFabricEndpoint("abc123.datawarehouse.fabric.microsoft.com"));
            Assert.True(SourceSample.IsFabricEndpoint("ABC123.Datawarehouse.Fabric.Microsoft.com,1433"));
        }

        // ---- Fails first 2: privacy ------------------------------------------------------------------------

        private static string[] Literals()
        {
            var src = new FakeSource();
            var rows = src.Tables.Values.SelectMany(t => Enumerable.Range(0, Math.Min(60, (int)t.Count())).Select(t.Row));
            var strings = rows.SelectMany(r => r).OfType<string>().Where(s => s.Length >= 5)
                .Where(s => !Schema().Tables.SelectMany(t => t.Columns).Any(c => c.Name == s));
            var numbers = src.Tables["dbo.fact_sales"].Row(0).Concat(Enumerable.Range(0, 50).Select(i => src.Tables["dbo.fact_sales"].Row(i)[3]))
                .OfType<decimal>().Select(d => d.ToString(CultureInfo.InvariantCulture));
            return strings.Concat(numbers).Concat(new[] { "zq-alpha", "contoso.example", "Zqlandia", "SALE-70", "20230101", "2023-0" }).Distinct().ToArray();
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task No_raw_value_appears_in_the_facts_the_sidecar_or_any_decision_request_built_from_sample_facts()
        {
            var src = new FakeSource();
            using var e = NewEngine(src);
            AgentPolicyStore.SetPreset("open", "human");
            await BuildDraftAsync(e);
            var sample = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "human", CancellationToken.None);
            var facts = await e.BuildSampleFactsAsync(Cur(e), sample);
            Assert.Equal(ModelFactsData.SourceSample, facts.Data);

            var folder = Path.Combine(_root, "built");
            await e.SaveAsync(folder, "TMDL", false, "human");
            var sidecar = e.SaveSourceFacts(Cur(e), sample);
            Assert.EndsWith(Path.Combine(".semanticus", "source-facts.json"), sidecar);

            // The requests the families send: the summarize family's per-table batches, and the category family's asks.
            var texts = await Cur(e).ReadAsync(m =>
            {
                var point = DecisionRegistry.Require(DecisionRegistry.ColumnSummarize);
                var batches = ColumnSummarizeFamily.Batches(ColumnSummarizeFamily.Prepare(m, facts));
                var list = batches.Select(b => JsonSerializer.Serialize(new { b.Snapshot, b.Questions, Digest = b.RequestFor(point).Digest })).ToList();
                list.AddRange(ColumnCategoryFamily.Prepare(m, facts).Asks.Select(a => JsonSerializer.Serialize(a.Snapshot)));
                return list;
            });
            Assert.NotEmpty(texts);
            var all = texts.Concat(new[] { JsonSerializer.Serialize(facts), File.ReadAllText(sidecar), JsonSerializer.Serialize(sample) }).ToArray();

            var literals = Literals();
            Assert.True(literals.Length > 40, "the scan must cover the fixture, not a handful of values");
            foreach (var text in all)
                foreach (var literal in literals)
                    Assert.False(text.Contains(literal, StringComparison.OrdinalIgnoreCase), "A raw value leaked: " + literal);
            // And the derived facts are really there (the scan would pass on an empty snapshot).
            Assert.Equal(4, Col(facts, "dim_customer", "CUST_NM").Measured.Distinct);
            Assert.Equal(0.75, Col(facts, "dim_customer", "EMAIL").Measured.EmailShare);
        }

        // ---- Fails first 3: the caps -----------------------------------------------------------------------

        private static FakeSource BigTable(string name, int columns, long rows, FakeSource src = null)
        {
            src ??= new FakeSource();
            var cols = Enumerable.Range(0, columns).Select(i => "C" + i).ToArray();
            src.Tables["dbo." + name] = (cols, () => rows, i =>
            {
                var r = new object[columns];
                r[0] = (long)i;   // unique on every row
                for (var c = 1; c < columns; c++) r[c] = (long)(i % 7);
                return r;
            });
            return src;
        }

        [Fact]
        public async Task The_row_cap_holds_a_big_table_is_read_as_TOP_20000_and_marked_sampled()
        {
            var src = BigTable("big", 3, 50_000);
            using var e = NewEngine(src);
            AgentPolicyStore.SetPreset("open", "human");

            var r = await e.ReadSourceSampleAsync(Target(), new[] { new SourceSampleTable { Schema = "dbo", Name = "big", Columns = new[] { "C0", "C1", "C2" } } }, "agent", CancellationToken.None);

            Assert.Equal(2, src.Queries.Count);   // exactly two queries per table: the count and the values
            Assert.Equal("SELECT COUNT_BIG(*) FROM [dbo].[big]", src.Queries[0]);
            Assert.Equal("SELECT TOP (20000) [C0], [C1], [C2] FROM [dbo].[big]", src.Queries[1]);
            Assert.True(src.MaxRows[1] <= 20_000);
            var t = Assert.Single(r.Tables);
            Assert.Equal(50_000, t.Rows);
            Assert.True(t.Sampled);
            Assert.Equal(20_000, t.Columns.Single(c => c.Source == "C0").Distinct);   // 20,000 rows were read, not 50,000
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_cell_cap_holds_a_wide_table_gets_fewer_rows_so_rows_times_columns_stays_within_a_million()
        {
            var src = BigTable("wide", 250, 40_000);
            using var e = NewEngine(src);
            AgentPolicyStore.SetPreset("open", "human");
            var cols = Enumerable.Range(0, 250).Select(i => "C" + i).ToArray();

            var r = await e.ReadSourceSampleAsync(Target(), new[] { new SourceSampleTable { Schema = "dbo", Name = "wide", Columns = cols } }, "human", CancellationToken.None);

            var n = int.Parse(Regex.Match(src.Queries[1], @"TOP \((\d+)\)").Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.True(n * 250 <= 1_000_000, "rows times columns was " + n * 250);
            Assert.Equal(4_000, n);
            Assert.True(src.MaxRows[1] <= n);
            Assert.True(Assert.Single(r.Tables).Sampled);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_cap_holds_even_when_the_endpoint_sends_more_rows_than_TOP_asked_for_and_a_table_too_wide_for_the_floor_is_cut_by_columns()
        {
            var src = BigTable("big", 3, 50_000);
            src.IgnoreTop = true;
            using var e = NewEngine(src);
            AgentPolicyStore.SetPreset("open", "human");
            var r = await e.ReadSourceSampleAsync(Target(), new[] { new SourceSampleTable { Schema = "dbo", Name = "big", Columns = new[] { "C0", "C1", "C2" } } }, "human", CancellationToken.None);
            Assert.Equal(20_000, Assert.Single(r.Tables).Columns.Single(c => c.Source == "C0").Distinct);

            // 1,200 columns would need 1,200,000 cells even at the 1,000-row floor: only as many columns as fit are read.
            var wide = BigTable("huge", 1_200, 3_000);
            using var e2 = NewEngine(wide);
            var all = Enumerable.Range(0, 1_200).Select(i => "C" + i).ToArray();
            var r2 = await e2.ReadSourceSampleAsync(Target(), new[] { new SourceSampleTable { Schema = "dbo", Name = "huge", Columns = all } }, "human", CancellationToken.None);
            var q = wide.Queries[1];
            var rows = int.Parse(Regex.Match(q, @"TOP \((\d+)\)").Groups[1].Value, CultureInfo.InvariantCulture);
            var readCols = Regex.Matches(q.Substring(0, q.IndexOf(" FROM ", StringComparison.Ordinal)), @"\[C\d+\]").Count;
            Assert.True(rows * readCols <= 1_000_000, rows + " x " + readCols);
            var t2 = Assert.Single(r2.Tables);
            Assert.Equal(readCols, t2.Columns.Length);
            Assert.Contains("columns", t2.Note, StringComparison.OrdinalIgnoreCase);
        }

        // ---- Fails first 4: facts follow renames by source column -----------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Facts_follow_a_renamed_table_and_a_renamed_column_by_their_source_names()
        {
            var src = new FakeSource();
            using var e = NewEngine(src);
            AgentPolicyStore.SetPreset("open", "human");
            await BuildDraftAsync(e);
            var sample = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "human", CancellationToken.None);
            var before = await e.BuildSampleFactsAsync(Cur(e), sample);
            var amount = Col(before, "fact_sales", "AMOUNT").Measured;
            Assert.Equal(50, amount.Distinct);

            await e.RenameObjectAsync("column:fact_sales/AMOUNT", "Revenue", "human");
            await e.RenameObjectAsync("table:fact_sales", "Sales", "human");
            await e.RenameObjectAsync("column:dim_customer/EMAIL", "Contact Email", "human");

            var after = await e.BuildSampleFactsAsync(Cur(e), sample);
            var sales = after.Tables.Single(t => t.Name == "Sales");
            Assert.Equal(50, sales.Rows);
            var revenue = sales.Columns.Single(c => c.Name == "Revenue");
            Assert.Equal(50, revenue.Measured.Distinct);
            Assert.Equal(amount.WholeNumbers, revenue.Measured.WholeNumbers);
            Assert.Equal(0.75, Col(after, "dim_customer", "Contact Email").Measured.EmailShare);
            // A one-to-one partner is named by its model ref NOW, not by the source name it had.
            foreach (var c in sales.Columns.Where(c => c.Measured != null))
                Assert.All(c.Measured.OneToOneWith, r => Assert.Contains(sales.Columns, x => x.Ref == r));
            Assert.DoesNotContain(after.Tables.SelectMany(t => t.Columns), c => c.Measured != null && c.Name == "AMOUNT");
        }

        [Fact]
        public async Task A_column_that_is_not_from_the_source_gets_no_facts()
        {
            var src = new FakeSource();
            using var e = NewEngine(src);
            AgentPolicyStore.SetPreset("open", "human");
            await BuildDraftAsync(e);
            await e.CreateMeasureAsync("table:fact_sales", "Total", "SUM ( fact_sales[AMOUNT] )", "human");
            var sample = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "human", CancellationToken.None);
            var facts = await e.BuildSampleFactsAsync(Cur(e), sample);
            Assert.All(facts.Tables.SelectMany(t => t.Columns).Where(c => c.Measured == null), c => Assert.False(string.IsNullOrEmpty(c.Name)));
            Assert.NotNull(Col(facts, "fact_sales", "AMOUNT").Measured);
        }

        /// <summary>The sample describes one server and database. A model table that reads a table of the same schema and name
        /// somewhere else is a different table, so it is shown none of this sample's facts.</summary>
        [Fact]
        public async Task A_table_that_reads_another_databases_table_of_the_same_name_gets_none_of_this_samples_facts()
        {
            var src = new FakeSource();
            using var e = NewEngine(src);
            AgentPolicyStore.SetPreset("open", "human");
            await BuildDraftAsync(e);
            var sample = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "human", CancellationToken.None);
            Assert.NotNull(Col(await e.BuildSampleFactsAsync(Cur(e), sample), "fact_sales", "AMOUNT").Measured);   // precondition

            // One table now reads the same-named table on another server, in its own M.
            await Cur(e).MutateAsync("human", "test setup", m => m.Tables["fact_sales"].Partitions.First().Expression =
                "let Source = Sql.Database(\"other.datawarehouse.fabric.microsoft.com\", \"" + Db + "\"), Data = Source{[Schema=\"dbo\",Item=\"fact_sales\"]}[Data] in Data");
            var facts = await e.BuildSampleFactsAsync(Cur(e), sample);
            Assert.Null(Col(facts, "fact_sales", "AMOUNT").Measured);
            Assert.Null(facts.Tables.Single(t => t.Name == "fact_sales").Rows);
            Assert.NotNull(Col(facts, "dim_customer", "EMAIL").Measured);   // the others still read this sample's database

            // Every table now reads another database on the same server, through the shared expression they all name.
            await Cur(e).MutateAsync("human", "test setup", m => m.Expressions["FabricSource"].Expression =
                "let Source = Sql.Database(\"" + Server + "\", \"OtherDW\") in Source");
            var none = await e.BuildSampleFactsAsync(Cur(e), sample);
            Assert.All(none.Tables.SelectMany(t => t.Columns), c => Assert.Null(c.Measured));
            Assert.NotEqual(ModelFactsData.SourceSample, none.Data);
        }

        // ---- Fails first 5: the built model, no live connection -------------------------------------------

        private async Task<string[]> SummarizeDigestsAsync(Session s, ModelFacts facts) =>
            await s.ReadAsync(m =>
            {
                var point = DecisionRegistry.Require(DecisionRegistry.ColumnSummarize);
                return ColumnSummarizeFamily.Batches(ColumnSummarizeFamily.Prepare(m, facts)).Select(b => b.RequestFor(point).Digest).ToArray();
            });

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_built_model_with_no_live_connection_gives_a_summarize_request_digest_equal_to_the_drafts()
        {
            var src = new FakeSource();
            string[] draftDigests;
            string folder = Path.Combine(_root, "built");
            using (var e = NewEngine(src))
            {
                AgentPolicyStore.SetPreset("open", "human");
                await BuildDraftAsync(e);
                var sample = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "human", CancellationToken.None);
                var draftFacts = await e.BuildSampleFactsAsync(Cur(e), sample);
                draftDigests = await SummarizeDigestsAsync(Cur(e), draftFacts);
                await e.SaveAsync(folder, "TMDL", false, "human");
                e.SaveSourceFacts(Cur(e), sample);
            }
            Assert.NotEmpty(draftDigests);

            // A fresh engine opens the built model: no live connection, no executor, no sample read.
            var probe = new FakeSource();
            using var built = NewEngine(null);
            await built.OpenAsync(folder);
            var facts = await built.GetModelFactsAsync();
            Assert.Equal(ModelFactsData.SourceSample, facts.Data);
            var builtDigests = await SummarizeDigestsAsync(Cur(built), facts);

            Assert.Equal(draftDigests, builtDigests);
            Assert.Empty(probe.Queries);
            // The equality is not an empty one: with no facts at all the same requests have different digests.
            Assert.NotEqual(draftDigests, await SummarizeDigestsAsync(Cur(built), null));
        }

        [Fact]
        public async Task The_producer_text_says_the_facts_came_from_a_sample_taken_at_the_start_and_not_from_a_connected_copy()
        {
            var src = new FakeSource();
            var folder = Path.Combine(_root, "built");
            using (var e = NewEngine(src))
            {
                AgentPolicyStore.SetPreset("open", "human");
                await BuildDraftAsync(e);
                var sample = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "human", CancellationToken.None);
                await e.SaveAsync(folder, "TMDL", false, "human");
                e.SaveSourceFacts(Cur(e), sample);
            }
            using var built = NewEngine(null);
            await built.OpenAsync(folder);
            var f = await built.GetModelFactsAsync();

            var p = f.Producers["measured.distinct"];
            Assert.Equal("source sample", p.Producer);
            Assert.Contains("3 of 3 tables", p.Coverage);
            Assert.Contains(Server, p.Freshness);
            Assert.Contains("not a live read", p.Freshness, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("never rows", p.Coverage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("source sample", f.Producers["tables.rows"].Producer);
            Assert.Null(f.Tables.First().Bytes);                       // no storage bytes: nothing was scanned
            Assert.StartsWith("Not read", f.Producers["tables.bytes"].Coverage);
            Assert.Empty(f.StorageOpportunities);
        }

        [Fact]
        public async Task A_model_with_no_sidecar_still_says_none_and_a_live_connection_wins_over_the_sidecar()
        {
            var src = new FakeSource();
            var folder = Path.Combine(_root, "built");
            using (var e = NewEngine(src))
            {
                AgentPolicyStore.SetPreset("open", "human");
                await BuildDraftAsync(e);
                await e.SaveAsync(folder, "TMDL", false, "human");
                var f0 = await e.GetModelFactsAsync();
                Assert.Equal(ModelFactsData.None, f0.Data);
                Assert.All(f0.Tables.SelectMany(t => t.Columns), c => Assert.Null(c.Measured));
                // A sidecar that appears later (no revision change) is noticed.
                var sample = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "human", CancellationToken.None);
                e.SaveSourceFacts(Cur(e), sample);
                var f1 = await e.GetModelFactsAsync();
                Assert.Equal(ModelFactsData.SourceSample, f1.Data);
                Assert.NotNull(Col(f1, "fact_sales", "AMOUNT").Measured);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_agent_denied_data_reads_on_the_source_is_refused_the_sidecar_facts_in_words_about_facts()
        {
            var src = new FakeSource();
            var folder = Path.Combine(_root, "built");
            using (var e = NewEngine(src))
            {
                AgentPolicyStore.SetPreset("open", "human");
                await BuildDraftAsync(e);
                var sample = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "human", CancellationToken.None);
                await e.SaveAsync(folder, "TMDL", false, "human");
                e.SaveSourceFacts(Cur(e), sample);
            }
            using var built = NewEngine(null);
            await built.OpenAsync(folder);
            AgentPolicyStore.SetPreset("client", "human");
            LabelSourceProd();
            var refusal = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => built.GetModelFactsAsync("agent"));
            Assert.Equal(LocalEngine.FactsDeniedMessage, refusal.Message);
            var person = await built.GetModelFactsAsync("human");
            Assert.Equal(ModelFactsData.SourceSample, person.Data);
        }

        [Fact]
        public void A_table_name_that_exists_in_two_schemas_is_never_guessed_between()
        {
            var r = new SourceSampleResult
            {
                Tables = new[]
                {
                    new SourceTableFacts { Schema = "dbo", Name = "Sales", Rows = 10 },
                    new SourceTableFacts { Schema = "staging", Name = "Sales", Rows = 99 },
                    new SourceTableFacts { Schema = "dbo", Name = "Customer", Rows = 4 },
                },
            };
            Assert.Equal(10, r.Find("dbo", "Sales").Rows);
            Assert.Equal(99, r.Find("STAGING", "sales").Rows);
            Assert.Null(r.Find(null, "Sales"));              // which one? not guessed
            Assert.Null(r.Find("", "Sales"));
            Assert.Equal(4, r.Find(null, "Customer").Rows);   // only one has this name
            Assert.Null(r.Find("other", "Customer"));         // a named schema that is not there is not a match
        }

        // ---- Timeouts and failures read "not measured" --------------------------------------------------------

        [Fact]
        public async Task A_timeout_or_a_failure_leaves_that_table_not_measured_and_never_a_measured_zero()
        {
            var src = new FakeSource { FailTable = "dim_customer", FailCountOf = "fact_sales" };
            using var e = NewEngine(src);
            AgentPolicyStore.SetPreset("open", "human");
            await BuildDraftAsync(e);

            var sample = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "human", CancellationToken.None);

            var customer = sample.Tables.Single(t => t.Name == "dim_customer");
            var sales = sample.Tables.Single(t => t.Name == "fact_sales");
            Assert.Null(customer.Rows);
            Assert.Contains("Timeout", customer.NotMeasured, StringComparison.OrdinalIgnoreCase);
            Assert.Null(sales.Rows);
            Assert.NotNull(sample.Tables.Single(t => t.Name == "dim_date").Rows);   // the others are unharmed

            var facts = await e.BuildSampleFactsAsync(Cur(e), sample);
            Assert.All(facts.Tables.Single(t => t.Name == "dim_customer").Columns, c => Assert.Null(c.Measured));
            Assert.Null(facts.Tables.Single(t => t.Name == "dim_customer").Rows);
            Assert.All(facts.Tables.Single(t => t.Name == "fact_sales").Columns, c => Assert.Null(c.Measured));
            Assert.NotNull(Col(facts, "dim_date", "CAL_DT").Measured);
            Assert.Contains("1 of 3 tables", facts.Producers["measured.distinct"].Coverage);
            Assert.Contains("2 could not be read", facts.Producers["measured.distinct"].Coverage);
        }

        [Fact]
        public async Task A_query_that_throws_or_is_cancelled_is_a_not_measured_table_and_the_read_never_throws()
        {
            var src = new FakeSource { ThrowFor = q => q.Contains("dim_customer") ? "the connection was cancelled" : null };
            using var e = NewEngine(src);
            AgentPolicyStore.SetPreset("open", "human");

            var sample = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "human", CancellationToken.None);

            Assert.Null(sample.Tables.Single(t => t.Name == "dim_customer").Rows);
            Assert.False(string.IsNullOrWhiteSpace(sample.Tables.Single(t => t.Name == "dim_customer").NotMeasured));
            Assert.NotNull(sample.Tables.Single(t => t.Name == "fact_sales").Rows);
        }

        [Fact]
        public async Task A_token_that_cannot_be_had_leaves_every_table_not_measured_with_the_reason()
        {
            using var e = NewEngine(null);   // no executor seam: the real path
            e.SourceSampleTokenForTest = () => throw new InvalidOperationException("No Entra sign-in is available.");
            AgentPolicyStore.SetPreset("open", "human");

            var r = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "human", CancellationToken.None);

            Assert.Contains("sign", r.Refused, StringComparison.OrdinalIgnoreCase);
            Assert.All(r.Tables, t => Assert.Null(t.Rows));
        }

        // ---- The dialect ----------------------------------------------------------------------------------------

        [Fact]
        public void The_T_SQL_dialect_quotes_every_identifier_and_the_DAX_queries_are_unchanged()
        {
            Assert.Equal("SELECT COUNT_BIG(*) FROM [dbo].[we]]ird]", FactsMeasurer.TSql.RowCountQuery("dbo", "we]ird"));
            Assert.Equal("SELECT TOP (500) [a], [b]]c] FROM [s].[t]", FactsMeasurer.TSql.ValuesQuery("s", "t", new[] { "a", "b]c" }, 500));
            Assert.Equal("SELECT [a] FROM [s].[t]", FactsMeasurer.TSql.ValuesQuery("s", "t", new[] { "a" }, null));
            // The live path keeps its own dialect and its exact text.
            Assert.Equal("EVALUATE ROW(\"rows\", COUNTROWS('T'))", FactsMeasurer.RowCountQuery("T"));
            Assert.Equal("EVALUATE TOPNSKIP(10, 0, SELECTCOLUMNS('T', \"c0\", 'T'[A]))", FactsMeasurer.ValuesQuery("T", new[] { "A" }, 10));
        }

        // ---- The fiscal year start --------------------------------------------------------------------------

        [Fact]
        public async Task The_fiscal_year_start_is_worked_out_from_the_date_tables_rows_when_the_names_point_at_fiscal_columns()
        {
            var src = new FakeSource();
            using var e = NewEngine(src);
            AgentPolicyStore.SetPreset("open", "human");

            var r = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "human", CancellationToken.None);

            Assert.Equal(7, r.FiscalYearStart.StartMonth);
            Assert.Equal(6, r.FiscalYearStart.EndMonth);
            Assert.Equal("sample", r.FiscalYearStart.Source);
            Assert.Equal("dim_date", r.FiscalYearStart.Table);
            Assert.Contains("FY_NO", r.FiscalYearStart.Why);
            // It is a fact about the table, never a date: the file holds no day, year or row.
            Assert.DoesNotContain("2022", JsonSerializer.Serialize(r.FiscalYearStart));
        }

        [Fact]
        public void With_no_rows_the_names_alone_say_a_fiscal_calendar_exists_but_not_when_it_starts()
        {
            var named = FiscalYearStart.FromNames("dim_date", new[] { "DATE_KEY", "CAL_DT", "FY_NO", "FY_QTR", "MTH_NO" });
            Assert.Null(named.StartMonth);
            Assert.Equal("names", named.Source);
            Assert.Contains("rows", named.Why);

            var none = FiscalYearStart.FromNames("dim_date", new[] { "DATE_KEY", "CAL_DT", "YEAR", "MONTH_NAME" });
            Assert.Null(none.StartMonth);
            Assert.Equal("none", none.Source);

            // A name that says the month: "ending June" is a July start, "starts April" is April.
            Assert.Equal(7, FiscalYearStart.FromNames("Date", new[] { "Date", "FiscalYearEndingJune" }).StartMonth);
            Assert.Equal(4, FiscalYearStart.FromNames("Date", new[] { "Date", "Fiscal Year Start April" }).StartMonth);
            Assert.Null(FiscalYearStart.FromNames("Date", new[] { "Date", "FY_Jul" }).StartMonth);   // which end of the year? not guessed
        }

        private static (string[] Cols, object[][] Rows) DateRows(Func<DateTime, object[]> fiscal, string[] fiscalCols, int days = 800)
        {
            var cols = new[] { "Date" }.Concat(fiscalCols).ToArray();
            var rows = Enumerable.Range(0, days).Select(i => { var d = new DateTime(2022, 1, 1).AddDays(i); return new object[] { d }.Concat(fiscal(d)).ToArray(); }).ToArray();
            return (cols, rows);
        }

        [Theory]
        [InlineData(1, 1)]    // a fiscal year that is the calendar year: January
        [InlineData(4, 4)]
        [InlineData(7, 7)]
        [InlineData(10, 10)]
        public void The_start_month_is_where_the_fiscal_year_value_changes_whatever_the_value_looks_like(int start, int expected)
        {
            string FyText(DateTime d) => "FY" + (d.Year + (d.Month >= start && start != 1 ? 1 : 0));
            var (cols, rows) = DateRows(d => new object[] { FyText(d) }, new[] { "Fiscal Year" });
            var fact = FiscalYearStart.FromRows("Date", cols, rows);
            Assert.Equal(expected, fact.StartMonth);
            Assert.Equal("sample", fact.Source);
        }

        [Fact]
        public void A_fiscal_month_number_that_restarts_at_one_gives_the_start_and_beats_a_year_column_that_disagrees()
        {
            var (cols, rows) = DateRows(d => new object[] { (long)(((d.Month - 7 + 12) % 12) + 1), "FY" + d.Year }, new[] { "FiscalMonthNumber", "Fiscal Year" });
            var fact = FiscalYearStart.FromRows("Date", cols, rows);
            Assert.Equal(7, fact.StartMonth);
            Assert.Contains("FiscalMonthNumber", fact.Why);
        }

        [Fact]
        public void Disagreeing_or_missing_evidence_is_not_guessed()
        {
            // Transitions on two different calendar months (a 4-4-5 style calendar): not a month start, so no answer.
            var (c1, r1) = DateRows(d => new object[] { d.Month % 5 < 2 ? "A" : "B" }, new[] { "Fiscal Year" });
            Assert.Null(FiscalYearStart.FromRows("Date", c1, r1).StartMonth);
            // One fiscal year in the whole sample: no transition seen.
            var (c2, r2) = DateRows(d => new object[] { "FY2023" }, new[] { "Fiscal Year" }, days: 200);
            var one = FiscalYearStart.FromRows("Date", c2, r2);
            Assert.Null(one.StartMonth);
            Assert.Contains("transition", one.Why, StringComparison.OrdinalIgnoreCase);
            // No fiscal-named column at all: nothing to read, and the rows are not looked at.
            var (c3, r3) = DateRows(d => new object[] { (long)d.Year }, new[] { "Year" });
            Assert.Equal("none", FiscalYearStart.FromRows("Date", c3, r3).Source);
            // No date column in the rows.
            Assert.Null(FiscalYearStart.FromRows("Date", new[] { "Fiscal Year" }, new[] { new object[] { "FY1" }, new object[] { "FY2" } }).StartMonth);
        }

        /// <summary>A sample with a gap (a TOP read of a date table comes back in no particular order and need not be a whole run of
        /// days): a value that changed across days the sample did not read says only that the year started somewhere in the gap,
        /// so it never names a month. Here the year really starts on 1 July, July was not sampled, and the change is first seen on
        /// 1 August.</summary>
        [Fact]
        public void A_date_table_sampled_with_a_gap_claims_no_fiscal_year_start_rather_than_a_wrong_month()
        {
            var (cols, all) = DateRows(d => new object[] { "FY" + (d.Year + (d.Month >= 7 ? 1 : 0)), (long)(((d.Month - 7 + 12) % 12) + 1) },
                                       new[] { "Fiscal Year", "FiscalMonthNumber" });
            var gapped = all.Where(r => ((DateTime)r[0]).Year == 2022 && ((DateTime)r[0]).Month is 5 or 6 or 8 or 9).ToArray();
            var fact = FiscalYearStart.FromRows("Date", cols, gapped);
            Assert.Null(fact.StartMonth);
            Assert.Equal("sample", fact.Source);

            // Every other day sampled: no two sampled days are next to each other, so no change is pinned to a day.
            var everyOther = all.Where((r, i) => i % 2 == 1).ToArray();
            Assert.Null(FiscalYearStart.FromRows("Date", cols, everyOther).StartMonth);

            // The same table read as a whole run of days still gives July.
            Assert.Equal(7, FiscalYearStart.FromRows("Date", cols, all).StartMonth);
        }

        [Fact]
        public async Task The_fiscal_year_start_survives_the_sidecar_and_a_source_with_no_fiscal_columns_reports_none()
        {
            var src = new FakeSource();
            using var e = NewEngine(src);
            AgentPolicyStore.SetPreset("open", "human");
            await BuildDraftAsync(e);
            var sample = await e.ReadSourceSampleAsync(Target(), Tables(Schema()), "human", CancellationToken.None);
            await e.SaveAsync(Path.Combine(_root, "built"), "TMDL", false, "human");
            var path = e.SaveSourceFacts(Cur(e), sample);
            var back = SourceFactsStore.Load(Path.GetDirectoryName(path));
            Assert.Equal(7, back.FiscalYearStart.StartMonth);

            var noFiscal = new FakeSource();
            noFiscal.Tables.Remove("dbo.dim_date");
            var cols = new[] { "DATE_KEY", "CAL_DT" };
            noFiscal.Tables["dbo.dim_date"] = (cols, () => 400, i => new object[] { (long)i, new DateTime(2023, 1, 1).AddDays(i) });
            using var e2 = NewEngine(noFiscal);
            var r2 = await e2.ReadSourceSampleAsync(Target(), new[] { new SourceSampleTable { Schema = "dbo", Name = "dim_date", Columns = cols } }, "human", CancellationToken.None);
            Assert.Equal("none", r2.FiscalYearStart.Source);
            Assert.Null(r2.FiscalYearStart.StartMonth);
        }
    }
}
