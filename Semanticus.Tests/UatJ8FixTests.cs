// UAT of the join 8 build (Sol, 4 Oct): the engine's half of the fixes. Each test here failed first on 676a6ac8.
//
// U2  the per-table sample refusal on the start plan reaches the plan view and the grant from both doors (the Studio control sends the same edit).
// U3  get_start_plan says what the build does to the date table's key: the date column is the key, and the declared key lets go.
// U4  the SQL start writes only an empty model into a folder that was empty or new, and writes nothing into a folder that holds anything.
// U5  Explain pins a date cell with a date, not with text, and says a conversion refusal in plain words.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Facts;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class UatJ8StartFixTests : GateFixtureBase
    {
        private const string Server = "probe.datawarehouse.fabric.microsoft.com", Db = "Warehouse";
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public UatJ8StartFixTests(RestoreRootFixture fixture) : base(fixture)
        {
            ConnectionRegistry.RootOverride = Path.Combine(Root, "connections");
            AgentPolicyStore.SetPreset("open", "human");
        }

        public override void Dispose()
        {
            ConnectionRegistry.RootOverride = _oldConnections;
            base.Dispose();
        }

        private sealed class NoAnswer : IDecisionOracle
        {
            public bool Available { get; set; }
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct) => Task.FromResult(DecisionAnswers.NotDecided(request, "no answer"));
        }

        private async Task<LocalEngine> FreeEngineAsync(SessionManager sm, string folder = null)
        {
            var e = new LocalEngine(sm, TestEntitlements.Free, Path.Combine(Root, "ws"));
            e.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            e.UseDecisionsForTest(new DecisionService(new NoAnswer()));
            var view = await e.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "wh-j8.datawarehouse.fabric.microsoft.com", Database = "ContosoConsulting", Folder = folder ?? Path.Combine(Root, "m"), Name = "Uplift",
            }, "human");
            Assert.True(view.Exists);
            await e.StartPreviewSettledForTest();
            return e;
        }

        private static StartColumn Column(StartPlanView v, string table, string column) =>
            v.Sections.SelectMany(s => s.Groups).Where(g => g.Table == table).SelectMany(g => g.Columns ?? new List<StartColumn>()).Single(c => c.Name == column);

        // ---------------------------------------------------------------- U3

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("ui")]
        [InlineData("mcp")]
        public async Task The_plan_says_the_date_column_is_the_key_of_the_marked_date_table_as_the_build_makes_it(string door)
        {
            using var sm = new SessionManager(); using var e = await FreeEngineAsync(sm);
            var view = door == "mcp" ? await McpTools.GetStartPlan(e) : await e.GetStartPlanAsync();
            // The plan marks dim_date as the date table on its date column.
            Assert.Contains(view.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Lines),
                l => l.Item?.Kind == "mark_date_table" && l.Item.Title.Contains("dim_date") && l.Item.Title.Contains("CAL_DT"));
            // The build makes CAL_DT the key and lets the declared key (DATE_KEY) go (StructureManifest.ToSpec). The review used to say the opposite.
            Assert.True(Column(view, "dim_date", "CAL_DT").Key, "the plan says the date column is not the key, and the build makes it the key");
            Assert.False(Column(view, "dim_date", "DATE_KEY").Key, "the plan says the declared key stays the key, and the build takes the key off it");
        }

        [Fact]
        public async Task The_key_the_plan_shows_is_the_key_the_built_model_has()
        {
            using var sm = new SessionManager(); using var e = await FreeEngineAsync(sm);
            var view = await e.GetStartPlanAsync();
            var shown = view.Sections.SelectMany(s => s.Groups).Where(g => g.Table == "dim_date").SelectMany(g => g.Columns).Where(c => c.Key).Select(c => c.Name).ToArray();
            var spec = e.StartPlanDocForTest().Manifest.ToSpec();
            var built = spec.Tables.Single(t => t.Name == "dim_date").Columns.Where(c => c.IsKey).Select(c => c.Name).ToArray();
            Assert.Equal(built, shown);
        }

        // ---------------------------------------------------------------- U4

        private static string[] FilesUnder(string dir) =>
            !Directory.Exists(dir) ? Array.Empty<string>()
                : Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/')).OrderBy(p => p, StringComparer.Ordinal).ToArray();

        [Fact]
        public async Task The_start_writes_an_empty_model_into_a_new_folder_and_nothing_else_before_the_build()
        {
            using var sm = new SessionManager();
            var folder = Path.Combine(Root, "brand-new");
            using var e = await FreeEngineAsync(sm, folder);
            var files = FilesUnder(folder);
            // Only the empty model: its database and model documents. No table, no measure, no relationship is written until the build.
            Assert.All(files, f => Assert.StartsWith("Uplift.SemanticModel", f));
            var documents = files.Where(f => f.EndsWith(".tmdl", StringComparison.Ordinal) || f.EndsWith(".pbism", StringComparison.Ordinal)).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Contains("database.tmdl", documents);
            Assert.Contains("model.tmdl", documents);
            Assert.DoesNotContain(files, f => f.Contains("/tables/", StringComparison.Ordinal) || f.EndsWith("relationships.tmdl", StringComparison.Ordinal));
            var model = File.ReadAllText(Directory.EnumerateFiles(folder, "model.tmdl", SearchOption.AllDirectories).Single());
            Assert.DoesNotContain("ref table", model);
        }

        [Theory]
        [InlineData("any other file")]
        public async Task A_folder_that_holds_anything_is_refused_and_not_one_file_is_written_into_it(string what)
        {
            using var sm = new SessionManager();
            var folder = Path.Combine(Root, "has-things");
            Directory.CreateDirectory(folder);
            var mine = Path.Combine(folder, "notes.txt");
            File.WriteAllText(mine, "keep me");
            var before = FilesUnder(folder);
            var bytes = before.Where(f => File.Exists(Path.Combine(folder, f))).ToDictionary(f => f, f => File.ReadAllText(Path.Combine(folder, f)));

            var e = new LocalEngine(sm, TestEntitlements.Free, Path.Combine(Root, "ws"));
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            e.UseDecisionsForTest(new DecisionService(new NoAnswer()));
            using (e)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => e.StartModelFromSqlAsync(new StartModelRequest
                {
                    Server = "wh-j8.datawarehouse.fabric.microsoft.com", Database = "ContosoConsulting", Folder = folder, Name = "Uplift",
                }, "human"));
            }
            Assert.Equal(before, FilesUnder(folder));
            foreach (var kv in bytes) Assert.Equal(kv.Value, File.ReadAllText(Path.Combine(folder, kv.Key)));
        }

        [Fact]
        public async Task A_folder_that_holds_the_persons_own_model_gets_the_new_model_in_a_new_folder_and_the_old_one_is_untouched()
        {
            // K24 (9 Oct): the person's own Uplift.SemanticModel in the folder is no longer a refusal. The start takes the next free name in a new folder.
            using var sm = new SessionManager();
            var folder = Path.Combine(Root, "has-a-model");
            var mine = Path.Combine(folder, "Uplift.SemanticModel");
            Directory.CreateDirectory(Path.Combine(mine, "definition"));
            File.WriteAllText(Path.Combine(mine, "definition", "database.tmdl"), "database Mine\n\tcompatibilityLevel: 1604\n");
            var before = FilesUnder(folder);
            var bytes = before.Where(f => File.Exists(Path.Combine(folder, f))).ToDictionary(f => f, f => File.ReadAllText(Path.Combine(folder, f)));

            var e = new LocalEngine(sm, TestEntitlements.Free, Path.Combine(Root, "ws"));
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            e.UseDecisionsForTest(new DecisionService(new NoAnswer()));
            using (e)
            {
                var started = await e.StartModelFromSqlAsync(new StartModelRequest
                {
                    Server = "wh-j8.datawarehouse.fabric.microsoft.com", Database = "ContosoConsulting", Folder = folder, Name = "Uplift",
                }, "human");
                Assert.Equal("Uplift 2", started.Destination.Name);
                Assert.Equal(Path.Combine(folder, "Uplift 2", "Uplift 2.SemanticModel"), started.Destination.ModelPath);
                Assert.True(Directory.Exists(Path.Combine(folder, "Uplift 2")));
            }
            // Nothing is written into the existing folder's own model: every file it held is still there, byte for byte. The new model is a sibling folder.
            Assert.Equal(before, FilesUnder(folder).Where(f => !f.StartsWith("Uplift 2", StringComparison.Ordinal)).ToArray());
            foreach (var kv in bytes) Assert.Equal(kv.Value, File.ReadAllText(Path.Combine(folder, kv.Key)));
            Assert.All(FilesUnder(Path.Combine(folder, "Uplift 2")), f => Assert.StartsWith("Uplift 2.SemanticModel", f));
        }

        // ---------------------------------------------------------------- U2 (the engine's half)

        private const string GrantedRows = "424242", RefusedRows = "987654";

        private sealed class Recorder : IDecisionOracle
        {
            public bool Available { get; set; } = true;
            private readonly List<string> _requests = new List<string>();
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                string whole;
                try { whole = System.Text.Json.JsonSerializer.Serialize(request); }
                catch { whole = System.Text.Json.JsonSerializer.Serialize(new { request.PointName, request.Snapshot }); }
                lock (_requests) _requests.Add(request.PointName + " " + whole);
                return Task.FromResult(DecisionAnswers.NotDecided(request, "recording oracle"));
            }
            public string[] All() { lock (_requests) return _requests.ToArray(); }
        }

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };

        private static FabricSchema Schema() => new FabricSchema
        {
            Server = Server, Database = Db,
            Tables = new[]
            {
                new FabricTable { Schema = "dbo", Name = "Sales", KeyColumns = new[] { "Id" }, Columns = new[] { C("Id", "Int64"), C("DateKey", "Int64"), C("Amount", "Decimal") } },
                new FabricTable { Schema = "dbo", Name = "DimDate", KeyColumns = new[] { "DateKey" }, Columns = new[] { C("DateKey", "Int64"), C("Date", "DateTime") } },
                new FabricTable { Schema = "dbo", Name = "Payroll", KeyColumns = new[] { "Id" }, Columns = new[] { C("Id", "Int64"), C("GrossPay", "Decimal") } },
            },
            ForeignKeys = new[] { new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "DateKey", ToSchema = "dbo", ToTable = "DimDate", ToColumn = "DateKey" } },
        };

        private static SourceSampleResult Sample() => new SourceSampleResult
        {
            Server = Server, Database = Db, MeasuredAtUtc = DateTime.UtcNow.ToString("O"),
            Tables = new[]
            {
                new SourceTableFacts { Schema = "dbo", Name = "Sales", Rows = 424242, Columns = new[] { new SourceColumnFacts { Source = "Amount", Distinct = 3131, BlankShare = 0.1414 } } },
                new SourceTableFacts { Schema = "dbo", Name = "Payroll", Rows = 987654, Columns = new[] { new SourceColumnFacts { Source = "GrossPay", Distinct = 7531, BlankShare = 0.2468 } } },
            },
        };

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("ui")]
        [InlineData("mcp")]
        public async Task After_the_grant_a_table_refused_on_either_door_reads_as_refused_on_the_plan_and_leaves_the_grant(string door)
        {
            var oracle = new Recorder();
            using var sm = new SessionManager();
            using var e = new LocalEngine(sm, TestEntitlements.Pro, Path.Combine(Root, "ws"));
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(Schema());
            e.UseDecisionsForTest(new DecisionService(oracle));
            await e.StartModelFromSqlAsync(new StartModelRequest { Name = "Model", Folder = Path.Combine(Root, "model"), Server = Server, Database = Db }, "human");
            var id = e.StartPlanDocForTest().Id;
            e.SetStartPlanSample(id, Sample());
            await e.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            await e.StartDecideSettledForTest(); await e.StartPreviewSettledForTest();
            var view = await e.GetStartPlanAsync();
            await e.GrantStartDisclosureAsync(view.Digest, "human");
            await e.StartDecideSettledForTest(); await e.StartPreviewSettledForTest();

            StartGroup Group(StartPlanView v, string t) => v.Sections.SelectMany(s => s.Groups).Single(g => g.Table == t && g.Columns != null);
            Assert.True((await e.GetStartPlanAsync()).SampleFacts, "the grant carries the sample, so the plan offers a refusal per table");
            Assert.False(Group(await e.GetStartPlanAsync(), "Payroll").SampleOff);

            view = door == "mcp" ? await McpTools.EditStartPlan(e, "set_table_sample", "Payroll", "off")
                : await e.EditStartPlanAsync(new StartPlanEdit { Op = "set_table_sample", Target = "Payroll", Value = "off" }, "human");
            Assert.True(Group(view, "Payroll").SampleOff, "the answer says the refusal stands");
            Assert.False(Group(view, "Sales").SampleOff, "and only that table's");
            Assert.True(Group(await (door == "mcp" ? McpTools.GetStartPlan(e) : e.GetStartPlanAsync()), "Payroll").SampleOff, "the other door reads the same");

            var cut = await e.SampleForJevAsync(id, Sample());
            Assert.NotNull(cut);
            var json = System.Text.Json.JsonSerializer.Serialize(cut);
            Assert.DoesNotContain(RefusedRows, json);
            Assert.Contains(GrantedRows, json);
        }

        // ---------------------------------------------------------------- U7

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("ui")]
        [InlineData("mcp")]
        public async Task An_unanswered_pick_is_built_and_the_band_and_the_review_say_the_same_on_both_doors(string door)
        {
            using var sm = new SessionManager(); using var e = await FreeEngineAsync(sm);
            // Jev has looked and is not sure of any total: every base measure keeps the rule's pick and is asked about (StartPlanDecide.NeedsYou).
            e.StartPlanDocForTestMutate(d => d.Decide = new StartDecide { State = StartDecideState.Stable });
            var view = door == "mcp" ? await McpTools.GetStartPlan(e) : await e.GetStartPlanAsync();
            var picks = view.NeedsYou.Where(n => n.Kind == "unsure_pick").ToList();
            Assert.True(picks.Count > 0, "no unanswered pick to check");   // Jev gave no answer, so every base measure keeps the rule's pick and is asked about
            var spec = e.StartPlanDocForTest().Manifest.ToSpec();
            foreach (var need in picks)
            {
                var baseMeasure = e.StartPlanDocForTest().Manifest.BuiltBases().Single(b => "unsure_pick:" + b.Identity == need.Id);
                // The review lists it, as a line the plan keeps...
                var line = view.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Lines)
                    .Single(l => l.Item?.Kind == "create_measure" && l.Item.ObjectName == baseMeasure.Name);
                Assert.True(line.Kept, "the review lists the rule's pick as a line that is not kept");
                // ...and the build makes it, so the band's question says what is true: the plan uses the pick.
                Assert.Contains(spec.Measures, m => m.Name == baseMeasure.Name);
                Assert.Contains("the plan uses " + baseMeasure.Pick, need.Text);
                Assert.DoesNotContain("stays out", need.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(baseMeasure.Pick, need.Recommended);
            }
        }

        // ---------------------------------------------------------------- U5

        [Fact]
        public void A_date_cell_pins_the_filter_with_a_date_and_a_text_cell_with_text()
        {
            Assert.Equal("TREATAS ( { DATE ( 2018, 6, 25 ) }, 'Customer'[JoinDate] )",
                DaxBench.CompileMemberFilter("'Customer'[JoinDate]", new[] { "2018-06-25T00:00:00" }, dateColumn: true));
            Assert.Equal("TREATAS ( { DATE ( 1899, 12, 30 ) + TIME ( 0, 0, 0 ) + 3739095005000 / 86400000 }, 'Customer'[JoinDate] )",
                DaxBench.CompileMemberFilter("'Customer'[JoinDate]", new[] { "2018-06-25T13:30:05" }, dateColumn: true));
            Assert.Equal("TREATAS ( { DATE ( 2018, 6, 25 ), DATE ( 2018, 6, 26 ) }, 'Customer'[JoinDate] )",
                DaxBench.CompileMemberFilter("'Customer'[JoinDate]", new[] { "2018-06-25", "2018-06-26" }, dateColumn: true));
            // Not a date column: a member that looks like a date is still the text it is.
            Assert.Equal("TREATAS ( { \"2018-06-25T00:00:00\" }, 'Customer'[Code] )",
                DaxBench.CompileMemberFilter("'Customer'[Code]", new[] { "2018-06-25T00:00:00" }, dateColumn: false));
            // The two-argument form every other caller uses keeps its old output.
            Assert.Equal("TREATAS ( { \"Stonebridge\" }, 'Customer'[City] )", DaxBench.CompileMemberFilter("'Customer'[City]", new[] { "Stonebridge" }));
        }

        [Fact]
        public async Task Explain_reads_the_type_of_each_filtered_column_from_the_model_and_sends_a_date_as_a_date()
        {
            using var sm = new SessionManager();
            using var e = new LocalEngine(sm, TestEntitlements.Free);
            await e.OpenAsync(TestModels.FindBim());
            var args = await e.ExplainMemberArgsAsync(new[]
            {
                new ExplainFilter { Column = "'Date'[Date]", Members = new[] { "2018-06-25T00:00:00" } },
                new ExplainFilter { Column = "'Customer'[Customer Id]", Members = new[] { "2018-06-25T00:00:00" } },
                new ExplainFilter { Column = "'Date'[Date]", Empty = true },
            });
            Assert.Equal("TREATAS ( { DATE ( 2018, 6, 25 ) }, 'Date'[Date] )", args[0]);
            Assert.StartsWith("TREATAS ( { \"2018-06-25T00:00:00\" }", args[1]);   // a column that is not a date keeps the text
            Assert.StartsWith("FILTER ( ALL (", args[2]);
        }

        [Fact]
        public void A_refused_conversion_is_said_in_plain_words_without_the_engines_internals()
        {
            const string raw = "Cannot convert value '2018-06-25T00:00:00' of type Text to type Date while processing column - [StaticColumn] → Customer[JoinDate].";
            var said = DaxErrorText.Sentence(raw);
            Assert.DoesNotContain("StaticColumn", said);
            Assert.DoesNotContain("Cannot convert", said);
            Assert.DoesNotContain("→", said);
            Assert.Contains("JoinDate", said);
            Assert.Contains("date", said, StringComparison.OrdinalIgnoreCase);
            Assert.False(said.Contains('—') || said.Contains('–'), "no dash in the words");
            // Any other error keeps its first line, without the engine's markup.
            Assert.Equal("The query is wrong.", DaxErrorText.Sentence("<oii>The query is wrong.</oii>\nsecond line"));
        }
    }
}
