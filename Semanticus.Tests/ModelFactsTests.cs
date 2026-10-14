using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Facts;
using ModelFacts = Semanticus.Engine.Facts.ModelFacts;   // not the workflow predicate facts of the same name
using Semanticus.Engine.Lineage;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The model facts snapshot (M4 groundwork contract, section 1, T268). A small model with known data sits behind
    /// a fake query connection that answers the snapshot's own queries from in-memory rows, so every fact can be
    /// checked against a value worked out by hand, and the privacy rule (derived facts only, never a raw value, a
    /// minimum or a maximum) can be checked by scanning the serialized snapshot for the fixture's literal values.
    /// </summary>
    [Collection("restore-root")]
    public sealed class ModelFactsTests : IDisposable, IClassFixture<ModelTemplates>
    {
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/Contoso Facts";
        private const string Database = "Contoso Facts Model";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sem-facts-" + Guid.NewGuid().ToString("N"));
        private readonly string _safeRoot;
        private readonly ModelTemplates _templates;

        public ModelFactsTests(RestoreRootFixture fixture, ModelTemplates templates)
        {
            _safeRoot = fixture.Root;
            _templates = templates;
            Directory.CreateDirectory(Path.Combine(_root, "workspace"));
            ConnectionRegistry.RootOverride = Path.Combine(_root, "user");
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        // ---- The fixture's data -------------------------------------------------------------------------------

        private static readonly string[] CustomerColumns = { "CustomerId", "Name", "Email", "Website", "Photo", "Country", "State", "Postcode" };
        private static readonly object[][] CustomerRows =
        {
            new object[] { 1L, "Contoso Pinecrest Alpha", "zq-alpha@contoso.example", "https://contoso.example/zq/alpha", "https://contoso.example/zq/alpha.png", "Australia", "New South Wales", "2913" },
            new object[] { 2L, "Contoso Pinecrest Bravo", "zq-bravo@contoso.example", "https://contoso.example/zq/bravo", "https://contoso.example/zq/bravo.jpg?size=2", "Canada", "Ontario", "90210" },
            new object[] { 3L, "Contoso Pinecrest Charlie", "zq-charlie@contoso.example", "https://contoso.example/zq/charlie", "https://contoso.example/zq/charlie.gif", "New Zealand", "Texas", "SW1A 1AA" },
            new object[] { 4L, "Contoso Pinecrest Delta", "not an address zq", "www.contoso.example/zq/delta", "https://contoso.example/zq/delta", "Zqlandia Sentinel", "QLD", "M5V 3L9" },
        };

        private static readonly string[] SalesColumns = { "OrderKey", "CustomerId", "Amount", "Year", "Month", "MonthNumber", "Day", "Quarter", "Note" };
        private static readonly string[] Months = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
        private static readonly string[] Days = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };
        private static readonly object[][] SalesRows = Enumerable.Range(0, 12).Select(i => new object[]
        {
            1001L + i,                                   // unique on every row
            (long)(i % 4 + 1),                           // the relationship's many side
            98765.4321 + i * 1.5,                        // distinctive, never whole
            i < 6 ? 2021L : 2022L,
            Months[i],
            (long)(i + 1),
            Days[i % 7],
            "Q" + (i / 3 + 1).ToString(CultureInfo.InvariantCulture),
            i % 4 == 0 ? null : "Zq note sentinel " + (i % 3).ToString(CultureInfo.InvariantCulture),
        }).ToArray();

        private const string StatMinSentinel = "Zq Min Sentinel 7731";
        private const string StatMaxSentinel = "Zq Max Sentinel 8842";

        private sealed class FakeData
        {
            public readonly Dictionary<string, (string[] cols, object[][] rows)> Tables = new Dictionary<string, (string[], object[][])>(StringComparer.Ordinal)
            {
                ["Customer"] = (CustomerColumns, CustomerRows),
                ["Sales"] = (SalesColumns, SalesRows),
            };
            public readonly List<string> Queries = new List<string>();

            public ResultSet Execute(string q)
            {
                lock (Queries) Queries.Add(q);
                if (q.Contains("DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS")) return Segments();
                if (q.Contains("DISCOVER_STORAGE_TABLE_COLUMNS")) return StorageColumns();
                if (q.Contains("DISCOVER_STORAGE_TABLES")) return StorageTables();
                if (q.Contains("COLUMNSTATISTICS")) return Statistics();
                var count = Regex.Match(q, @"^EVALUATE ROW\(""rows"", COUNTROWS\('((?:[^']|'')*)'\)\)$");
                if (count.Success)
                {
                    var t = Tables[count.Groups[1].Value.Replace("''", "'")];
                    return new ResultSet { Columns = new[] { new ColumnDef { Name = "[rows]" } }, Rows = new[] { new object[] { (long)t.rows.Length } } };
                }
                var select = Regex.Match(q, @"SELECTCOLUMNS\('((?:[^']|'')*)'(.*)\)");
                if (select.Success)
                {
                    var t = Tables[select.Groups[1].Value.Replace("''", "'")];
                    var names = Regex.Matches(select.Groups[2].Value, @"\[((?:[^\]]|\]\])*)\]").Select(m => m.Groups[1].Value.Replace("]]", "]")).ToArray();
                    var idx = names.Select(n => Array.IndexOf(t.cols, n)).ToArray();
                    IEnumerable<object[]> rows = t.rows;
                    var top = Regex.Match(q, @"^EVALUATE TOPNSKIP\((\d+), 0, ");
                    if (top.Success) rows = rows.Take(int.Parse(top.Groups[1].Value, CultureInfo.InvariantCulture));
                    return new ResultSet
                    {
                        Columns = names.Select((n, i) => new ColumnDef { Name = "[c" + i + "]" }).ToArray(),
                        Rows = rows.Select(r => idx.Select(i => r[i]).ToArray()).ToArray(),
                    };
                }
                return ResultSet.FromError("Unexpected query in the facts fixture: " + q);
            }

            // Storage: Customer[Name] is dictionary heavy, Customer[Photo] is large and unused, Sales[CustomerId] is a
            // large identifier summed by default, Sales[Note] is hash heavy. Everything else is small.
            private static readonly (string table, string column, long data, long dict, long hash)[] Sizes =
            {
                ("Customer", "Name", 100_000, 3_000_000, 0),
                ("Customer", "Photo", 1_500_000, 100, 0),
                ("Sales", "CustomerId", 2_000_000, 1_000, 0),
                ("Sales", "Note", 600_000, 100_000, 400_000),
                ("Sales", "Amount", 5_000, 1_000, 0),
                ("Customer", "Country", 2_000, 500, 0),
            };

            private static ResultSet Segments() => new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "DIMENSION_NAME" }, new ColumnDef { Name = "COLUMN_ID" }, new ColumnDef { Name = "USED_SIZE" } },
                Rows = Sizes.Select((s, i) => new object[] { s.table, "c" + i, s.data }).ToArray(),
            };

            private static ResultSet StorageColumns() => new ResultSet
            {
                Columns = new[] { "DIMENSION_NAME", "ATTRIBUTE_NAME", "COLUMN_ID", "COLUMN_ENCODING", "DICTIONARY_SIZE", "STRING_INDEX_SIZE" }.Select(n => new ColumnDef { Name = n }).ToArray(),
                Rows = Sizes.Select((s, i) => new object[] { s.table, s.column, "c" + i, "1", s.dict, s.hash }).ToArray(),
            };

            private static ResultSet StorageTables() => new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "DIMENSION_NAME" }, new ColumnDef { Name = "ROWS_COUNT" } },
                Rows = new[] { new object[] { "Customer", 4L }, new object[] { "Sales", 12L } },
            };

            // The statistic carries a minimum and a maximum, which are raw values. Only the cardinality may survive.
            private static ResultSet Statistics() => new ResultSet
            {
                Columns = new[] { "Table Name", "Column Name", "Min", "Max", "Cardinality", "Max Length" }.Select(n => new ColumnDef { Name = "[" + n + "]" }).ToArray(),
                Rows = new[]
                {
                    new object[] { "Customer", "Name", StatMinSentinel, StatMaxSentinel, 4L, 25L },
                    new object[] { "Sales", "Amount", StatMinSentinel, StatMaxSentinel, 12L, 0L },
                },
            };
        }

        // ---- Building the model -------------------------------------------------------------------------------

        private sealed class Rig : IDisposable
        {
            public SessionManager Sessions;
            public LocalEngine Engine;
            public FakeData Data = new FakeData();
            public void Dispose() { Engine?.Dispose(); Sessions?.Dispose(); }
        }

        // Lean suite (8 Oct): the 21 tests of this class used to build the same 25-call model each. It is built once per class and saved;
        // each test opens its own copy in its own workspace, so the connection and the fake data stay per test.
        private static async Task BuildModelAsync(LocalEngine e)
        {
            await e.CreateModelAsync("Contoso Facts", 1604);
            await e.CreateTableAsync("Customer", "human");
            foreach (var c in CustomerColumns) await e.CreateColumnAsync("table:Customer", c, c == "CustomerId" ? "Int64" : "String", c, "human");
            await e.CreateTableAsync("Sales", "human");
            foreach (var c in SalesColumns)
                await e.CreateColumnAsync("table:Sales", c, c switch { "Amount" => "Double", "OrderKey" or "CustomerId" or "Year" or "MonthNumber" => "Int64", _ => "String" }, c, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerId", "column:Customer/CustomerId", null, true, "human");
            await e.CreateMeasureAsync("table:Sales", "Total Amount", "SUM ( Sales[Amount] )", "human");
            await e.SetColumnMetadataAsync("column:Sales/Month", null, null, null, "MonthNumber", "human");
            await e.SetColumnMetadataAsync("column:Sales/CustomerId", null, "Sum", null, null, "human");
        }

        private async Task<Rig> BuildAsync(bool connect = true)
        {
            var rig = new Rig { Sessions = new SessionManager() };
            rig.Engine = new LocalEngine(rig.Sessions, TestEntitlements.Free, Path.Combine(_root, "workspace"));
            await _templates.OpenCopyAsync(rig.Engine, "facts", BuildModelAsync, TestEntitlements.Free,
                copyTo: Path.Combine(_root, "workspace", "ContosoFacts.bim"));
            if (connect) Connect(rig);
            return rig;
        }

        private static void Connect(Rig rig)
        {
            // Editing and querying the same live model: the relationship that lets data facts describe this model.
            rig.Sessions.Current.LiveOrigin = new LiveOrigin(Endpoint, Database, null);
            rig.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", Endpoint, Database, rig.Data.Execute));
        }

        private static ColumnFacts Col(ModelFacts f, string table, string column) =>
            f.Tables.Single(t => t.Name == table).Columns.Single(c => c.Name == column);

        // ---- Tests --------------------------------------------------------------------------------------------

        [Fact]
        public async Task Every_fact_on_a_small_model_matches_the_known_data()
        {
            using var rig = await BuildAsync();
            var f = await rig.Engine.GetModelFactsAsync();

            Assert.Equal(ModelFactsData.Live, f.Data);
            Assert.Equal(rig.Sessions.Current.Revision, f.Revision);
            Assert.False(string.IsNullOrEmpty(f.ModelIdentity));
            Assert.Equal(new[] { "Customer", "Sales" }, f.Tables.Select(t => t.Name).ToArray());

            var sales = f.Tables.Single(t => t.Name == "Sales");
            var customer = f.Tables.Single(t => t.Name == "Customer");
            Assert.Equal("table:Sales", sales.Ref);
            Assert.Equal(12, sales.Rows);
            Assert.Equal(4, customer.Rows);
            Assert.Equal(2_000_000 + 1_000 + 600_000 + 100_000 + 400_000 + 5_000 + 1_000, sales.Bytes);
            Assert.False(sales.IsDateTableMarked);

            // Settings and structure.
            var custId = Col(f, "Sales", "CustomerId");
            Assert.Equal("column:Sales/CustomerId", custId.Ref);
            Assert.Equal("Int64", custId.DataType);
            Assert.Equal("Sum", custId.SummarizeBy);
            Assert.Equal("many", custId.RelationshipRole);
            Assert.Equal("one", Col(f, "Customer", "CustomerId").RelationshipRole);
            Assert.Equal("none", Col(f, "Sales", "Amount").RelationshipRole);
            Assert.Equal("column:Sales/MonthNumber", Col(f, "Sales", "Month").SortByColumn);
            Assert.Equal(1, Col(f, "Sales", "Amount").UsedByMeasures);
            Assert.Equal(0, Col(f, "Sales", "Year").UsedByMeasures);
            Assert.Null(Col(f, "Sales", "Year").UsedInReports);   // no reports were checked

            // Derived facts.
            var orderKey = Col(f, "Sales", "OrderKey").Measured;
            Assert.Equal(12, orderKey.Distinct);
            Assert.True(orderKey.UniqueOnEveryRow);
            Assert.True(orderKey.WholeNumbers);
            Assert.False(orderKey.LooksLikeYear);
            Assert.False(orderKey.Sampled);

            var amount = Col(f, "Sales", "Amount").Measured;
            Assert.False(amount.WholeNumbers);
            Assert.Equal(6_000, amount.DataBytes + amount.DictionaryBytes);
            Assert.Equal(12, amount.Cardinality);

            var year = Col(f, "Sales", "Year").Measured;
            Assert.True(year.LooksLikeYear);
            Assert.False(year.LooksLikeMonthNumber);
            Assert.Equal(2, year.Distinct);
            Assert.False(year.UniqueOnEveryRow);

            Assert.True(Col(f, "Sales", "Month").Measured.LooksLikeMonthName);
            Assert.True(Col(f, "Sales", "MonthNumber").Measured.LooksLikeMonthNumber);
            Assert.False(Col(f, "Sales", "MonthNumber").Measured.LooksLikeQuarter);
            Assert.True(Col(f, "Sales", "Day").Measured.LooksLikeDayName);
            Assert.True(Col(f, "Sales", "Quarter").Measured.LooksLikeQuarter);
            // A period number (the fair columns replay's fact): whole numbers from 1 to 53, 2 to 53 of them. Text never is.
            Assert.True(Col(f, "Sales", "MonthNumber").Measured.LooksLikePeriodNumber);
            Assert.True(Col(f, "Sales", "CustomerId").Measured.LooksLikePeriodNumber);
            Assert.False(Col(f, "Sales", "Year").Measured.LooksLikePeriodNumber);
            Assert.False(Col(f, "Sales", "OrderKey").Measured.LooksLikePeriodNumber);
            Assert.False(Col(f, "Sales", "Quarter").Measured.LooksLikePeriodNumber);
            Assert.False(Col(f, "Sales", "Quarter").Measured.LooksLikeMonthName);
            Assert.Contains("column:Sales/MonthNumber", Col(f, "Sales", "Month").Measured.OneToOneWith);
            Assert.DoesNotContain("column:Sales/Year", Col(f, "Sales", "Month").Measured.OneToOneWith);

            var note = Col(f, "Sales", "Note").Measured;
            Assert.Equal(0.25, note.BlankShare);   // 3 of 12 rows are blank
            Assert.Equal(4, note.Distinct);        // three texts plus blank

            Assert.Equal(0.75, Col(f, "Customer", "Country").Measured.PlaceShare.Country);
            Assert.Equal(1.0, Col(f, "Customer", "State").Measured.PlaceShare.Region);
            Assert.Equal(1.0, Col(f, "Customer", "Postcode").Measured.PlaceShare.Postcode);
            Assert.Null(Col(f, "Customer", "Country").Measured.PlaceShare.City);
            Assert.Equal(1.0, Col(f, "Customer", "Website").Measured.UrlShare);
            Assert.Equal(0.75, Col(f, "Customer", "Photo").Measured.ImageUrlShare);
            Assert.Equal(0.75, Col(f, "Customer", "Email").Measured.EmailShare);
            Assert.Equal(0, Col(f, "Customer", "Name").Measured.UrlShare);
            Assert.Equal(4, Col(f, "Customer", "Name").Measured.Cardinality);

            // Storage findings, moved from the Storage tab.
            var opps = f.StorageOpportunities;
            Assert.Contains(opps, o => o.RuleId == "STORAGE-DICT" && o.Ref == "column:Customer/Name");
            Assert.Contains(opps, o => o.RuleId == "STORAGE-HASH" && o.Ref == "column:Sales/Note");
            Assert.Contains(opps, o => o.RuleId == "STORAGE-SUMMARIZE" && o.Ref == "column:Sales/CustomerId");
            // Customer[Photo] is a visible column of a dimension that nothing references: the unused sweep calls it a
            // visibleAttribute, never safe, so storage does not offer it for removal (dogfood feedback, 27 Sep).
            Assert.DoesNotContain(opps, o => o.RuleId == "STORAGE-UNUSED" && o.Ref == "column:Customer/Photo");
            Assert.DoesNotContain(opps, o => o.Ref == "column:Sales/Amount");
            var dict = opps.Single(o => o.RuleId == "STORAGE-DICT");
            Assert.Equal("Dictionary is the largest component: 2.9 MB of 3.0 MB (97%). Often indicates a high-cardinality or wide text column.", dict.Message);

            // Producers are filled for every fact family.
            foreach (var key in new[] { "columns.settings", "columns.usedByMeasures", "columns.usedInReports", "tables.rows", "tables.bytes",
                                        "measured.distinct", "measured.placeShare", "measured.placeShare.city", "measured.cardinality", "storageOpportunities" })
                Assert.True(f.Producers.ContainsKey(key), key);
            Assert.Equal("none", f.Producers["measured.placeShare.city"].Producer);

            // Hidden, the same unused column is a removal candidate with its storage estimate.
            await rig.Engine.SetColumnMetadataAsync("column:Customer/Photo", true, null, null, null, "human");
            var hiddenOpps = (await rig.Engine.GetModelFactsAsync()).StorageOpportunities;
            var unused = Assert.Single(hiddenOpps, o => o.RuleId == "STORAGE-UNUSED" && o.Ref == "column:Customer/Photo");
            Assert.Equal("Can remove", unused.Group);
            Assert.Equal("Reduction estimate: up to 1.4 MB after refresh", unused.Effect);
        }

        [Fact]
        public async Task A_model_with_no_data_says_none_and_has_no_measured_blocks()
        {
            using var rig = await BuildAsync(connect: false);
            var f = await rig.Engine.GetModelFactsAsync();

            Assert.Equal(ModelFactsData.None, f.Data);
            Assert.All(f.Tables.SelectMany(t => t.Columns), c => Assert.Null(c.Measured));
            Assert.All(f.Tables, t => { Assert.Null(t.Rows); Assert.Null(t.Bytes); });
            Assert.Empty(f.StorageOpportunities);
            Assert.Equal("none", f.Producers["measured.distinct"].Producer);
            Assert.Empty(rig.Data.Queries);
            // Settings are still there: absent data never hides the model.
            Assert.Equal("Sum", Col(f, "Sales", "CustomerId").SummarizeBy);
        }

        [Fact]
        public async Task A_connection_to_a_different_model_is_not_this_models_data()
        {
            using var rig = await BuildAsync(connect: false);
            rig.Engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "powerbi://api.powerbi.com/v1.0/myorg/Elsewhere", "Other Model", rig.Data.Execute));
            var f = await rig.Engine.GetModelFactsAsync();

            Assert.Equal(ModelFactsData.None, f.Data);
            Assert.All(f.Tables.SelectMany(t => t.Columns), c => Assert.Null(c.Measured));
            Assert.All(f.Tables, t => Assert.Null(t.Bytes));
            // The storage findings still describe the connected model, as the Storage tab always showed them, but
            // only the size rules run: nothing about it may be joined onto this model's objects.
            Assert.DoesNotContain(f.StorageOpportunities, o => o.RuleId == "STORAGE-UNUSED" || o.RuleId == "STORAGE-SUMMARIZE");
            Assert.Contains(f.StorageOpportunities, o => o.RuleId == "STORAGE-DICT");
            Assert.DoesNotContain(rig.Data.Queries, q => q.Contains("COUNTROWS") || q.Contains("SELECTCOLUMNS") || q.Contains("COLUMNSTATISTICS"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_table_over_the_row_limit_is_measured_on_a_bounded_sample_and_says_so()
        {
            using var rig = await BuildAsync();
            rig.Engine.FactsBudgetForTest = new FactsBudget { RowLimit = 6, MinRows = 1 };
            var f = await rig.Engine.GetModelFactsAsync();

            Assert.All(f.Tables.Single(t => t.Name == "Sales").Columns, c => Assert.True(c.Measured.Sampled, c.Name));
            Assert.All(f.Tables.Single(t => t.Name == "Customer").Columns, c => Assert.False(c.Measured.Sampled, c.Name));
            Assert.Equal(12, f.Tables.Single(t => t.Name == "Sales").Rows);   // the count is still the whole table
            Assert.Equal(6, Col(f, "Sales", "OrderKey").Measured.Distinct);   // the values are the sample's
            Assert.Contains(rig.Data.Queries, q => q.StartsWith("EVALUATE TOPNSKIP(6, 0, SELECTCOLUMNS('Sales'", StringComparison.Ordinal));
            Assert.DoesNotContain(rig.Data.Queries, q => q.StartsWith("EVALUATE TOPNSKIP(", StringComparison.Ordinal) && q.Contains("'Customer'"));
            Assert.Contains("bounded sample", f.Producers["measured.distinct"].Coverage);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_snapshot_is_cached_per_revision_and_data_source_and_rebuilt_in_the_background()
        {
            using var rig = await BuildAsync();
            rig.Engine.FactsBackgroundDelayMsForTest = 20;
            var first = await rig.Engine.GetModelFactsAsync();
            var again = await rig.Engine.GetModelFactsAsync();
            Assert.Same(first, again);
            Assert.Equal(1, rig.Engine.FactsComputationsForTest);

            // A change: the background rebuild lands without anyone asking.
            await rig.Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (rig.Engine.FactsComputationsForTest < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal(2, rig.Engine.FactsComputationsForTest);
            var afterChange = await rig.Engine.GetModelFactsAsync();
            Assert.Equal(2, rig.Engine.FactsComputationsForTest);   // served from the background build, not rebuilt
            Assert.Equal(rig.Sessions.Current.Revision, afterChange.Revision);
            Assert.True(afterChange.Revision > first.Revision);

            // Dropping the data connection changes the answer without moving the revision.
            rig.Engine.SetLiveConnectionForTest(null);
            var offline = await rig.Engine.GetModelFactsAsync();
            Assert.Equal(afterChange.Revision, offline.Revision);
            Assert.Equal(ModelFactsData.None, offline.Data);
            Assert.Equal(3, rig.Engine.FactsComputationsForTest);
        }

        [Fact]
        public async Task Used_in_reports_becomes_a_yes_or_no_once_the_chosen_report_is_read()
        {
            using var rig = await BuildAsync();
            var report = WriteReportUsingColumn(_root, "Contoso Report", "Customer", "Country");
            await rig.Engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "Contoso report", Path = report } }, "human");

            var unread = await rig.Engine.GetModelFactsAsync();
            Assert.Null(Col(unread, "Customer", "Name").UsedInReports);   // chosen but not read: unknown

            await rig.Engine.CheckReportsAsync(null, true, null, null, null, "human", default);
            var read = await rig.Engine.GetModelFactsAsync();
            Assert.NotSame(unread, read);   // the report basis is part of the cache key
            Assert.True(Col(read, "Customer", "Country").UsedInReports);
            Assert.False(Col(read, "Customer", "Name").UsedInReports);
        }

        // F-240: every chosen report was read in full and none of them uses a model field, so "no" is proven for
        // every column. An empty used set is an answer, not a missing one.
        [Fact]
        public async Task Used_in_reports_is_no_for_every_column_when_the_read_report_uses_no_model_field()
        {
            using var rig = await BuildAsync();
            var def = Path.Combine(_root, "Contoso Blank", "R.Report", "definition");
            Directory.CreateDirectory(def);
            File.WriteAllText(Path.Combine(def, "report.json"), "{ \"filterConfig\": { \"filters\": [] } }");
            await rig.Engine.SetReportScopeAsync(new[] { new ReportScopeChoice { Kind = "local", Name = "Contoso blank", Path = Path.Combine(_root, "Contoso Blank") } }, "human");
            await rig.Engine.CheckReportsAsync(null, true, null, null, null, "human", default);

            var read = await rig.Engine.GetModelFactsAsync();
            var columns = read.Tables.SelectMany(t => t.Columns).ToList();
            Assert.NotEmpty(columns);
            Assert.All(columns, c => Assert.False(c.UsedInReports));
        }

        [Fact]
        public async Task Both_doors_return_the_same_snapshot()
        {
            using var rig = await BuildAsync();
            var agent = await McpTools.GetModelFacts(rig.Engine);
            var ui = await new EngineRpcTarget(rig.Engine).getModelFacts();

            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(agent), System.Text.Json.JsonSerializer.Serialize(ui));
            var wire = RpcWire(ui);
            Assert.Contains("\"storageOpportunities\":", wire);
            Assert.Contains("\"producers\":", wire);
            Assert.Contains("\"measured.distinct\":", wire);   // producer keys are data and keep their spelling
            Assert.Contains("\"oneToOneWith\":", wire);
        }

        [Fact]
        public async Task No_raw_value_minimum_or_maximum_appears_anywhere_in_the_snapshot()
        {
            using var rig = await BuildAsync();
            var f = await rig.Engine.GetModelFactsAsync();
            var texts = new[] { System.Text.Json.JsonSerializer.Serialize(f), RpcWire(f) };

            // Every fixture value long enough not to occur by accident (a four-character postcode or "Q1" could
            // turn up inside a byte count or a date), both sentinel statistics, and every distinctive number.
            var literals = CustomerRows.Concat(SalesRows).SelectMany(r => r)
                .OfType<string>().Where(s => s.Length >= 5 && !CustomerColumns.Contains(s) && !SalesColumns.Contains(s))
                .Concat(new[] { StatMinSentinel, StatMaxSentinel, "Zq note sentinel", "Zqlandia", "zq-alpha", "contoso.example" })
                .Concat(SalesRows.Select(r => ((double)r[2]).ToString(CultureInfo.InvariantCulture)))
                .Concat(SalesRows.Select(r => ((long)r[0]).ToString(CultureInfo.InvariantCulture)))
                .Distinct().ToArray();
            Assert.True(literals.Length > 40, "the scan must cover the fixture, not a handful of values");
            // A bare number (an order id such as 1012) can sit by chance inside the model identity (8 hex characters hashed from a random temp path) or inside the
            // clock stamp's fraction (30.1012345Z), which is not a value. A number counts as leaked only when it stands alone: no letter, digit, dot or dash on either side.
            // CI run 4, Windows: "leaked a raw value: 1012" was one of those chance matches.
            static bool Leaks(string text, string literal) =>
                literal.All(char.IsDigit)
                    ? System.Text.RegularExpressions.Regex.IsMatch(text, @"(?<![\w.\-])" + literal + @"(?![\w.])")
                    : text.Contains(literal, StringComparison.OrdinalIgnoreCase);
            foreach (var text in texts)
                foreach (var literal in literals)
                    Assert.False(Leaks(text, literal), "The snapshot leaked a raw value: " + literal);
        }

        [Fact]
        public void VertiPaq_joins_cardinality_from_column_statistics_and_drops_the_minimum_and_maximum()
        {
            var segments = new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "DIMENSION_NAME" }, new ColumnDef { Name = "COLUMN_ID" }, new ColumnDef { Name = "USED_SIZE" } },
                Rows = new[] { new object[] { "Sales", "1", 100L }, new object[] { "Sales", "2", 50L } },
            };
            var columns = new ResultSet
            {
                Columns = new[] { "DIMENSION_NAME", "ATTRIBUTE_NAME", "COLUMN_ID", "COLUMN_ENCODING", "DICTIONARY_SIZE", "STRING_INDEX_SIZE" }.Select(n => new ColumnDef { Name = n }).ToArray(),
                Rows = new[] { new object[] { "Sales", "Amount", "1", "2", 20L, 5L }, new object[] { "Sales", "Region", "2", "1", 9L, 1L } },
            };
            var stats = new ResultSet
            {
                Columns = new[] { "[COLUMNSTATISTICS].[Table Name]", "[COLUMNSTATISTICS].[Column Name]", "[Min]", "[Max]", "[Cardinality]" }.Select(n => new ColumnDef { Name = n }).ToArray(),
                Rows = new[] { new object[] { "Sales", "amount", StatMinSentinel, StatMaxSentinel, 37L } },
            };

            var report = VertiPaq.Compute(segments, columns, new ResultSet(), 25, VpaqStorageMode.Import, stats);

            Assert.Equal(37, report.TopColumns.Single(c => c.Column == "Amount").Cardinality);
            Assert.Null(report.TopColumns.Single(c => c.Column == "Region").Cardinality);   // not reported: unknown, not zero
            var json = System.Text.Json.JsonSerializer.Serialize(report);
            Assert.DoesNotContain(StatMinSentinel, json);
            Assert.DoesNotContain(StatMaxSentinel, json);
            Assert.Null(VertiPaq.Compute(segments, columns, new ResultSet(), 25).TopColumns[0].Cardinality);   // no statistics read
        }

        [Theory]
        [InlineData("CustomerKey", true)] [InlineData("OrderDateKey", true)] [InlineData("ProductCode", true)] [InlineData("OrderNo", true)]
        [InlineData("InvoiceNumber", true)] [InlineData("order_id", true)] [InlineData("order key", true)] [InlineData("store-code", true)]
        [InlineData("ID", true)] [InlineData("RowId", true)] [InlineData("CUSTOMER_ID", true)] [InlineData("ORDER ID", true)]
        [InlineData("STORE-CODE", true)] [InlineData("ORDER KEY", true)] [InlineData("INVOICE_NUMBER", true)] [InlineData("Order_Id", true)]
        [InlineData("AmountPaid", false)] [InlineData("Valid", false)] [InlineData("Period", false)] [InlineData("Casino", false)]
        [InlineData("Episode", false)] [InlineData("Ratio", false)] [InlineData("Tornado", false)] [InlineData("Grid", false)]
        [InlineData("Placebo", false)] [InlineData("Piano", false)] [InlineData("VALID", false)] [InlineData("CASINO", false)]
        public void The_engine_identifier_test_matches_the_storage_tab_cases_it_replaced(string name, bool expected)
        {
            // The same names Semanticus.VSCode/test/storage-logic.test.mjs holds storagecalc.identifierLike to.
            Assert.Equal(expected, StorageOpportunities.IdentifierLike(name, false));
            Assert.True(StorageOpportunities.IdentifierLike("TotallyOrdinaryName", true));
        }

        // ---- The facts feed the plan executor's data checks (T268 wiring) ----------------------------------------

        private static async Task<ChangeItem> ApplyOneAsync(LocalEngine e, string objRef, string kind, string after)
        {
            var view = await e.AddPlanItemAsync(objRef, kind, after, null, null, null, "human");
            var line = view.Items.Last(i => i.ObjectRef == objRef && i.Kind == kind);
            if (line.Status != "approved") await e.SetPlanItemAsync(line.Id, null, true, "human");
            var report = await e.ApplyPlanAsync(new[] { line.Id }, "human");
            return report.Items.Single(i => i.Id == line.Id);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_sort_by_line_passes_its_one_to_one_check_once_facts_exist_and_refuses_as_not_measured_before()
        {
            using var rig = await BuildAsync();

            // No snapshot yet for this revision: the check has nothing to read, so it refuses rather than guesses.
            Assert.Null(rig.Engine.TryGetCurrentFacts());
            var early = await ApplyOneAsync(rig.Engine, "column:Sales/OrderKey", "set_sort_by_column", "MonthNumber");
            Assert.Equal("failed", early.Status);
            Assert.Contains("measured", early.Note, StringComparison.OrdinalIgnoreCase);

            // Build the facts for the revision the next apply runs at. OrderKey and MonthNumber both have twelve
            // distinct values over twelve rows, so they pair one to one; Day has seven, so it does not.
            await rig.Engine.GetModelFactsAsync();
            Assert.NotNull(rig.Engine.TryGetCurrentFacts());

            var refused = await ApplyOneAsync(rig.Engine, "column:Sales/OrderKey", "set_sort_by_column", "Day");
            Assert.Equal("failed", refused.Status);
            Assert.Contains("one to one", refused.Note, StringComparison.OrdinalIgnoreCase);

            await rig.Engine.GetModelFactsAsync();   // the refused apply may have moved the revision
            var applied = await ApplyOneAsync(rig.Engine, "column:Sales/OrderKey", "set_sort_by_column", "MonthNumber");
            Assert.Equal("applied", applied.Status);
            var col = await rig.Engine.GetObjectAsync("column:Sales/OrderKey");
            Assert.Equal("MonthNumber", col.Properties["sortByColumn"]?.ToString());
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_current_facts_are_only_the_snapshot_for_this_revision_and_never_wait_for_a_build()
        {
            using var rig = await BuildAsync();
            rig.Engine.FactsBackgroundDelayMsForTest = 60_000;   // keep the background rebuild out of the way
            await rig.Engine.GetModelFactsAsync();
            var snapshot = rig.Engine.TryGetCurrentFacts();
            Assert.NotNull(snapshot);
            Assert.Equal(rig.Sessions.Current.Revision, snapshot.Revision);
            var facts = new JevModelFactsAdapter(snapshot);
            Assert.Contains("column:Sales/MonthNumber", facts.OneToOneWith("column:Sales/Month"));
            Assert.False(facts.WholeNumbers("column:Sales/Amount"));
            Assert.True(facts.WholeNumbers("column:Sales/OrderKey"));
            Assert.Null(facts.OneToOneWith("column:Nowhere/Nothing"));

            // A change moves the revision; the old snapshot is not this model's any more.
            await rig.Engine.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human");
            Assert.Null(rig.Engine.TryGetCurrentFacts());
        }

        [Fact]
        public async Task A_snapshot_with_no_data_answers_not_measured_for_every_column()
        {
            using var rig = await BuildAsync(connect: false);
            await rig.Engine.GetModelFactsAsync();
            var snapshot = rig.Engine.TryGetCurrentFacts();
            Assert.NotNull(snapshot);
            var facts = new JevModelFactsAdapter(snapshot);
            Assert.Null(facts.OneToOneWith("column:Sales/Month"));
            Assert.Null(facts.WholeNumbers("column:Sales/OrderKey"));
        }

        // ---- Facts a write check may rely on (Sol 6 review of T268) --------------------------------------------

        // Eight rows. Id carries 2^53 and 2^53 + 1, which a double cannot tell apart. Small is ordinary whole
        // numbers. Qty is whole on its first six rows and not on the seventh. Code and CodeSort pair one to one on
        // the first six rows and not on the whole table (G sits at two sort positions).
        private static readonly string[] LedgerColumns = { "Id", "Small", "Qty", "Code", "CodeSort" };
        private static readonly object[][] LedgerRows =
        {
            new object[] { 9007199254740992L, 1L, 1.0, "A", 1L },
            new object[] { 9007199254740993L, 2L, 2.0, "B", 2L },
            new object[] { 3L, 3L, 3.0, "C", 3L },
            new object[] { 4L, 4L, 4.0, "D", 4L },
            new object[] { 5L, 5L, 5.0, "E", 5L },
            new object[] { 6L, 6L, 6.0, "F", 6L },
            new object[] { 7L, 7L, 7.5, "G", 7L },
            new object[] { 8L, 8L, 8.0, "G", 8L },
        };

        private static async Task AddLedgerAsync(Rig rig)
        {
            await rig.Engine.CreateTableAsync("Ledger", "human");
            foreach (var c in LedgerColumns)
                await rig.Engine.CreateColumnAsync("table:Ledger", c, c switch { "Qty" => "Double", "Code" => "String", _ => "Int64" }, c, "human");
            rig.Data.Tables["Ledger"] = (LedgerColumns, LedgerRows);
        }

        /// <summary>Build the facts for the revision the next apply runs at, then apply one line.</summary>
        private static async Task<ChangeItem> ApplyWithFactsAsync(Rig rig, string objRef, string kind, string after)
        {
            await rig.Engine.GetModelFactsAsync();
            Assert.NotNull(rig.Engine.TryGetCurrentFacts());
            return await ApplyOneAsync(rig.Engine, objRef, kind, after);
        }


        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_whole_number_column_changes_to_fixed_decimal_only_when_the_facts_prove_every_value_fits()
        {
            using var rig = await BuildAsync();
            await AddLedgerAsync(rig);

            // A fixed decimal stops at 922,337,203,685,477 either way, and Id holds 9007199254740993.
            var refused = await ApplyWithFactsAsync(rig, "column:Ledger/Id", "set_data_type", "Decimal");
            Assert.Equal("failed", refused.Status);
            Assert.Contains("Id", refused.Note, StringComparison.Ordinal);
            Assert.DoesNotContain("9007199254740993", refused.Note, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2014", refused.Note, StringComparison.Ordinal);
            Assert.Equal("Int64", (await rig.Engine.GetObjectAsync("column:Ledger/Id")).Properties["dataType"]?.ToString());

            var applied = await ApplyWithFactsAsync(rig, "column:Ledger/Small", "set_data_type", "Decimal");
            Assert.Equal("applied", applied.Status);
            Assert.Equal("Decimal", (await rig.Engine.GetObjectAsync("column:Ledger/Small")).Properties["dataType"]?.ToString());
        }

        [Fact]
        public async Task Facts_read_from_a_sample_are_unknown_to_the_write_checks()
        {
            using var rig = await BuildAsync();
            await AddLedgerAsync(rig);
            rig.Engine.FactsBudgetForTest = new FactsBudget { RowLimit = 6, MinRows = 1 };
            await rig.Engine.GetModelFactsAsync();
            var snapshot = rig.Engine.TryGetCurrentFacts();
            Assert.True(Col(snapshot, "Ledger", "Qty").Measured.Sampled, "the fixture has to sample Ledger");
            // The sample says Qty is whole and Code pairs with CodeSort; the table says neither.
            Assert.True(Col(snapshot, "Ledger", "Qty").Measured.WholeNumbers);
            Assert.Contains("column:Ledger/CodeSort", Col(snapshot, "Ledger", "Code").Measured.OneToOneWith);

            var facts = new JevModelFactsAdapter(snapshot);
            Assert.Null(facts.WholeNumbers("column:Ledger/Qty"));
            Assert.Null(facts.OneToOneWith("column:Ledger/Code"));
            Assert.Null(facts.OneToOneWith("column:Ledger/CodeSort"));
            // A table read in full still answers.
            Assert.True(facts.WholeNumbers("column:Customer/CustomerId"));

            // So the unseen 7.5 cannot slip through a change to whole numbers...
            var qty = await ApplyWithFactsAsync(rig, "column:Ledger/Qty", "set_data_type", "Int64");
            Assert.Equal("failed", qty.Status);
            Assert.Contains("measured", qty.Note, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Double", (await rig.Engine.GetObjectAsync("column:Ledger/Qty")).Properties["dataType"]?.ToString());

            // ...and the unseen second G cannot slip through a sort.
            var sort = await ApplyWithFactsAsync(rig, "column:Ledger/Code", "set_sort_by_column", "CodeSort");
            Assert.Equal("failed", sort.Status);
            Assert.Contains("measured", sort.Note, StringComparison.OrdinalIgnoreCase);
            Assert.Null((await rig.Engine.GetObjectAsync("column:Ledger/Code")).Properties["sortByColumn"]);
        }

        [Fact]
        public async Task A_whole_number_column_changes_to_decimal_only_when_the_facts_prove_every_value_fits()
        {
            using var rig = await BuildAsync();
            await AddLedgerAsync(rig);

            // 9007199254740993 has no exact double: it would come back as 9007199254740992.
            var refused = await ApplyWithFactsAsync(rig, "column:Ledger/Id", "set_data_type", "Double");
            Assert.Equal("failed", refused.Status);
            Assert.Contains("Id", refused.Note, StringComparison.Ordinal);
            Assert.DoesNotContain("9007199254740993", refused.Note, StringComparison.Ordinal);   // a sentence, never a value
            Assert.DoesNotContain("\u2014", refused.Note, StringComparison.Ordinal);
            Assert.Equal("Int64", (await rig.Engine.GetObjectAsync("column:Ledger/Id")).Properties["dataType"]?.ToString());

            // Every value of Small was read and every one fits, so the change is proved safe and goes in.
            var applied = await ApplyWithFactsAsync(rig, "column:Ledger/Small", "set_data_type", "Double");
            Assert.Equal("applied", applied.Status);
            Assert.Equal("Double", (await rig.Engine.GetObjectAsync("column:Ledger/Small")).Properties["dataType"]?.ToString());
        }

        [Fact]
        public void Large_integers_are_told_apart_by_their_exact_value()
        {
            var two = ValueFacts.Compute(new object[] { 9007199254740992L, 9007199254740993L });
            Assert.Equal(2, two.Distinct);
            Assert.NotEqual(ValueFacts.Key(9007199254740992L), ValueFacts.Key(9007199254740993L));
            // The same number stays one value whatever integer type carries it.
            Assert.Equal(ValueFacts.Key(7L), ValueFacts.Key(7));
            Assert.Equal(1, ValueFacts.Compute(new object[] { 7L, 7, (short)7 }).Distinct);
        }

        [Fact]
        public void Long_decimals_are_told_apart_by_their_exact_value()
        {
            // Both are exact decimals and both become the same double.
            var a = 1.0000000000000000000000000001m;
            var b = 1.0000000000000000000000000002m;
            Assert.Equal((double)a, (double)b);
            Assert.NotEqual(ValueFacts.Key(a), ValueFacts.Key(b));
            Assert.Equal(2, ValueFacts.Compute(new object[] { a, b }).Distinct);
            Assert.Equal(2, ValueFacts.Compute(new object[] { 922337203685477.5806m, 922337203685477.5807m }).Distinct);
            // Trailing zeros do not make a different value, and a whole decimal is the same value as the integer.
            Assert.Equal(ValueFacts.Key(1.5m), ValueFacts.Key(1.50m));
            Assert.Equal(ValueFacts.Key(7L), ValueFacts.Key(7.0000m));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Distinct_and_unique_count_two_large_integers_as_two()
        {
            using var rig = await BuildAsync();
            await AddLedgerAsync(rig);
            var f = await rig.Engine.GetModelFactsAsync();

            var id = Col(f, "Ledger", "Id").Measured;
            Assert.Equal(8, id.Distinct);
            Assert.True(id.UniqueOnEveryRow);
        }

        // ---- The agent door needs the QueryData permission (Kane: the facts are worked out from data) ---------

        internal const string FactsDeniedSentence =
            "Your assistant is not allowed to read facts measured from this model's data. You can allow it in Your assistant > Permissions.";

        [Fact]
        public async Task Under_the_default_policy_the_agent_reads_the_facts_with_no_approval_request()
        {
            using var rig = await BuildAsync();
            var policyRoot = Path.Combine(_root, "policy");
            Directory.CreateDirectory(policyRoot);
            var oldPolicy = AgentPolicyStore.RootOverride;
            var oldLedger = ApprovalLedger.RootOverride;
            AgentPolicyStore.RootOverride = policyRoot;
            ApprovalLedger.RootOverride = policyRoot;
            try
            {
                // The default ("standard") policy on an unlabelled connected target, which resolves to prod. The facts
                // are counts and patterns, not rows (Kane: no friction; undo is the safety net), so this is a read.
                AgentPolicyStore.SetPreset("standard", "human");
                var agent = await McpTools.GetModelFacts(rig.Engine);
                Assert.Equal(ModelFactsData.Live, agent.Data);
                Assert.Empty(ApprovalLedger.List());   // no approval was asked for
            }
            finally
            {
                AgentPolicyStore.RootOverride = oldPolicy;
                ApprovalLedger.RootOverride = oldLedger;
            }
        }

        [Fact]
        public async Task An_explicit_query_data_deny_refuses_the_agent_with_a_sentence_about_facts_and_the_person_is_not_gated()
        {
            using var rig = await BuildAsync();
            var policyRoot = Path.Combine(_root, "policy");
            Directory.CreateDirectory(policyRoot);
            var oldPolicy = AgentPolicyStore.RootOverride;
            var oldLedger = ApprovalLedger.RootOverride;
            AgentPolicyStore.RootOverride = policyRoot;
            ApprovalLedger.RootOverride = policyRoot;
            try
            {
                // The 'client' preset denies reading data from a production target, and this target is labelled prod.
                AgentPolicyStore.SetPreset("client", "human");
                var rec = ConnectionRegistry.Remember("xmla", Endpoint, Database);
                ConnectionRegistry.SetLabel(rec.Id, "prod", "human");

                var refusal = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => McpTools.GetModelFacts(rig.Engine));
                Assert.Equal(FactsDeniedSentence, refusal.Message);
                Assert.Equal(0, rig.Engine.FactsComputationsForTest);   // refused before anything was measured
                Assert.Empty(rig.Data.Queries);
                Assert.Empty(ApprovalLedger.List());

                // An agent reaching the engine over RPC (the --mcp host's RemoteEngine) is the agent, and is refused.
                var viaRpc = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new EngineRpcTarget(rig.Engine, "agent").getModelFacts());
                Assert.Equal(FactsDeniedSentence, viaRpc.Message);

                // The person, on the RPC door, is never gated.
                var person = await new EngineRpcTarget(rig.Engine, "human").getModelFacts();
                Assert.Equal(ModelFactsData.Live, person.Data);
            }
            finally
            {
                AgentPolicyStore.RootOverride = oldPolicy;
                ApprovalLedger.RootOverride = oldLedger;
            }
        }

        [Fact]
        public async Task With_no_data_connection_the_agent_door_is_not_gated()
        {
            using var rig = await BuildAsync(connect: false);
            var policyRoot = Path.Combine(_root, "policy");
            Directory.CreateDirectory(policyRoot);
            var oldPolicy = AgentPolicyStore.RootOverride;
            var oldLedger = ApprovalLedger.RootOverride;
            AgentPolicyStore.RootOverride = policyRoot;
            ApprovalLedger.RootOverride = policyRoot;
            try
            {
                AgentPolicyStore.SetPreset("locked", "human");
                var f = await McpTools.GetModelFacts(rig.Engine);   // nothing is read from data, so nothing to gate
                Assert.Equal(ModelFactsData.None, f.Data);
            }
            finally
            {
                AgentPolicyStore.RootOverride = oldPolicy;
                ApprovalLedger.RootOverride = oldLedger;
            }
        }

        // ---- VertiPaq with a missing result ---------------------------------------------------------------------

        [Fact]
        public void VertiPaq_with_no_results_returns_an_empty_report_rather_than_throwing()
        {
            var empty = VertiPaq.Compute(null, null, null, 25);
            Assert.Null(empty.Error);
            Assert.Equal(0, empty.ModelSize);
            Assert.Empty(empty.Tables);
            Assert.Empty(empty.TopColumns);

            var columns = new ResultSet
            {
                Columns = new[] { "DIMENSION_NAME", "ATTRIBUTE_NAME", "COLUMN_ID", "COLUMN_ENCODING", "DICTIONARY_SIZE", "STRING_INDEX_SIZE" }.Select(n => new ColumnDef { Name = n }).ToArray(),
                Rows = new[] { new object[] { "Sales", "Amount", "1", "2", 20L, 5L } },
            };
            var noTables = VertiPaq.Compute(new ResultSet(), columns, null, 25, VpaqStorageMode.Import);
            Assert.Equal(25, noTables.ModelSize);
            Assert.Equal("Sales", Assert.Single(noTables.Tables).Name);
        }

        // ---- Helpers ------------------------------------------------------------------------------------------

        private static string RpcWire(object value)
        {
            var serializer = new Newtonsoft.Json.JsonSerializer();
            RpcServer.ConfigureSerializer(serializer);
            using var writer = new StringWriter();
            serializer.Serialize(writer, value);
            return writer.ToString();
        }

        // A PBIR report whose only reference to the column is a report-level filter.
        private static string WriteReportUsingColumn(string root, string folder, string table, string column)
        {
            var json = "{ \"filterConfig\": { \"filters\": [ { \"field\": { \"Column\": { " +
                "\"Expression\": { \"SourceRef\": { \"Entity\": \"" + table + "\" } }, \"Property\": \"" + column + "\" } } } ] } }";
            var def = Path.Combine(root, folder, "R.Report", "definition");
            Directory.CreateDirectory(def);
            File.WriteAllText(Path.Combine(def, "report.json"), json);
            return Path.Combine(root, folder);
        }
    }
}
