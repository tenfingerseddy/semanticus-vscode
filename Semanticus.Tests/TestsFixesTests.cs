using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Astra's rejection of the Tests build, 2026-09-15. The engine half of what she found:
    /// "Only this" could not ask for table row counts alone, a partial run threw away the automatic
    /// families' last results, a recorded run kept no settings to read it by, and the grade-cap sentence
    /// spoke in "(s)" and in the engine's word "integrity". Each one was watched failing first.</summary>
    [Collection("restore-root")]   // saves a named SQL source, which lives under the static registry root
    public sealed class TestsFixesTests : IDisposable
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private readonly string _root;
        private readonly string _safeRoot;

        public TestsFixesTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-tests-fixes-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ConnectionRegistry.RootOverride = _root;
        }

        public void Dispose()
        {
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static (LocalEngine Engine, SessionManager Sessions, string Ws) Make()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-fixes-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            var sessions = new SessionManager();
            return (new LocalEngine(sessions, new Pro(), ws), sessions, ws);
        }

        private static async Task OpenNamedModelAsync(LocalEngine engine, SessionManager sessions, string ws, string name)
        {
            await engine.CreateModelAsync(name, 1604);
            sessions.Current.SourcePath = Path.Combine(ws, name + ".bim");
        }

        // ---- 3. "Only this" means only this ---------------------------------------------------------------
        // The page offered Relationships and Table row counts as separate choices and the engine ran BOTH for
        // either, because one flag governed the two families. A person who asks for table counts gets table
        // counts, and a person who asks for relationships does not have a SQL source read behind their back.
        [Fact]
        public async Task Table_row_counts_have_their_own_run_scope()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "ScopeSplit");
                var sales = await McpTools.CreateTable(engine, "Sales");
                var dates = await McpTools.CreateTable(engine, "Dim");
                await McpTools.CreateColumn(engine, sales, "K", "int64");
                await McpTools.CreateColumn(engine, dates, "K", "int64");
                await engine.CreateRelationshipAsync("column:Sales/K", "column:Dim/K", null, true, "human");
                var source = await engine.SaveSqlSourceAsync(null, "Warehouse", "contoso-sql.database.windows.net", "Warehouse", "interactive", null, "human");
                await engine.SetTableSourceMappingAsync("Sales", source.Id, "sales", "orders", "human");

                var relationshipsOnly = await engine.RunTestSuiteAsync(false, "human", null, new[] { "relationships" });
                Assert.Contains("relationships", relationshipsOnly.Scope.Ran);
                Assert.Contains("table counts", relationshipsOnly.Scope.Skipped);
                Assert.NotEmpty(relationshipsOnly.Relationships.Relationships);
                Assert.Empty(relationshipsOnly.Relationships.TableRowCounts);

                var countsOnly = await engine.RunTestSuiteAsync(false, "human", null, new[] { "tableCounts" });
                Assert.Contains("table counts", countsOnly.Scope.Ran);
                Assert.Contains("relationships", countsOnly.Scope.Skipped);
                Assert.Contains("saved checks", countsOnly.Scope.Skipped);
                Assert.NotEmpty(countsOnly.Relationships.TableRowCounts);
                Assert.Empty(countsOnly.Relationships.Relationships);

                // Both together still means both, and everything still means everything.
                var both = await engine.RunTestSuiteAsync(false, "human", null, new[] { "relationships", "tableCounts" });
                Assert.NotEmpty(both.Relationships.Relationships);
                Assert.NotEmpty(both.Relationships.TableRowCounts);

                // A name with no evaluator is still refused, and the refusal now names all three runnable parts.
                var dead = await engine.RunTestSuiteAsync(false, "human", null, new[] { "tablecount" });
                Assert.Null(dead.Health);
                Assert.Contains("tableCounts", dead.Note, StringComparison.Ordinal);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // ---- 4. A partial run keeps what it did not re-run -------------------------------------------------
        // list_table_mappings read the last counts out of _lastTestRun alone, so a saved-check rerun REPLACED
        // that run and every table went back to "not counted yet". The last real count belongs to the model,
        // not to whichever run happened most recently.
        [Fact]
        public async Task A_saved_check_rerun_keeps_the_last_table_counts()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "Retain");
                var sales = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, sales, "Total Sales", "4321");
                var source = await engine.SaveSqlSourceAsync(null, "Warehouse", "contoso-sql.database.windows.net", "Warehouse", "interactive", null, "human");
                await engine.SetTableSourceMappingAsync("Sales", source.Id, "sales", "orders", "human");

                await engine.RunTestSuiteAsync(false, "human");
                var counted = (await engine.ListTableSourceMappingsAsync()).Rows.Single(r => r.ModelTable == "Sales");
                Assert.False(string.IsNullOrEmpty(counted.LastVerdict));
                Assert.False(string.IsNullOrEmpty(counted.LastRunUtc));

                // A run that asks only for the saved checks must not erase what the table counts last found.
                var measuresOnly = await engine.RunTestSuiteAsync(false, "human", null, new[] { "measures" });
                Assert.Contains("table counts", measuresOnly.Scope.Skipped);
                var afterRerun = (await engine.ListTableSourceMappingsAsync()).Rows.Single(r => r.ModelTable == "Sales");
                Assert.Equal(counted.LastVerdict, afterRerun.LastVerdict);
                Assert.Equal(counted.LastMessage, afterRerun.LastMessage);
                Assert.Equal(counted.LastRunUtc, afterRerun.LastRunUtc);

                // A run that DOES count tables replaces them, so the retention never freezes a stale number.
                var recount = await engine.RunTestSuiteAsync(false, "human", null, new[] { "tableCounts" });
                Assert.Contains("table counts", recount.Scope.Ran);
                var afterRecount = (await engine.ListTableSourceMappingsAsync()).Rows.Single(r => r.ModelTable == "Sales");
                Assert.NotEqual(counted.LastRunUtc, afterRecount.LastRunUtc);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // UAT 1.5.104 round 2: tables failed to count (no source yet), then the person added the model's source, and the row went on saying
        // "5 could not be counted" from the run before the source existed. A count belongs to the source it was taken from.
        [Fact]
        public async Task A_count_taken_before_the_source_changed_is_forgotten()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "Forget");
                var sales = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateTable(engine, "Store");
                var first = await engine.SaveSqlSourceAsync(null, "Warehouse", "contoso-sql.database.windows.net", "Warehouse", "interactive", null, "human");
                var second = await engine.SaveSqlSourceAsync(null, "Archive", "contoso-sql.database.windows.net", "Archive", "interactive", null, "human");
                await engine.SetTableSourceMappingAsync("Sales", first.Id, "sales", "orders", "human");
                await engine.SetTableSourceMappingAsync("Store", first.Id, "sales", "stores", "human");
                await engine.RunTestSuiteAsync(false, "human");
                var rows = (await engine.ListTableSourceMappingsAsync()).Rows;
                Assert.False(string.IsNullOrEmpty(rows.Single(r => r.ModelTable == "Sales").LastVerdict));
                Assert.False(string.IsNullOrEmpty(rows.Single(r => r.ModelTable == "Store").LastVerdict));

                // Choosing another source for ONE table forgets that table's old count and keeps the other's.
                await engine.SetTableSourceMappingAsync("Sales", second.Id, "sales", "orders", "human");
                rows = (await engine.ListTableSourceMappingsAsync()).Rows;
                Assert.Null(rows.Single(r => r.ModelTable == "Sales").LastVerdict);
                Assert.False(string.IsNullOrEmpty(rows.Single(r => r.ModelTable == "Store").LastVerdict));

                // Linking the model to a source changes where every unmapped table counts, so every old count is forgotten.
                await McpTools.SetModelSqlEndpoint(engine, first.Id);
                rows = (await engine.ListTableSourceMappingsAsync()).Rows;
                Assert.All(rows, r => Assert.Null(r.LastVerdict));
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // A retained count belongs to ONE model. Opening another model must not show it the first model's
        // numbers, which is what a session-wide "last run" would have done.
        [Fact]
        public async Task A_retained_table_count_never_crosses_to_another_model()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "First");
                var sales = await McpTools.CreateTable(engine, "Sales");
                var source = await engine.SaveSqlSourceAsync(null, "Warehouse", "contoso-sql.database.windows.net", "Warehouse", "interactive", null, "human");
                await engine.SetTableSourceMappingAsync("Sales", source.Id, "sales", "orders", "human");
                await engine.RunTestSuiteAsync(false, "human");
                Assert.False(string.IsNullOrEmpty((await engine.ListTableSourceMappingsAsync()).Rows.Single(r => r.ModelTable == "Sales").LastVerdict));

                await OpenNamedModelAsync(engine, sessions, ws, "Second");
                await McpTools.CreateTable(engine, "Sales");
                var other = (await engine.ListTableSourceMappingsAsync()).Rows.Single(r => r.ModelTable == "Sales");
                Assert.Null(other.LastVerdict);
                Assert.Null(other.LastRunUtc);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // ---- 6. A recorded run keeps the settings it was run with ------------------------------------------
        // The snapshot kept each check's totals but not the query it asked, the filter it applied, the tolerance
        // it allowed or which SQL source it resolved to. Reading today's definition to describe a run from weeks
        // ago describes a check that may since have been edited, which is not evidence.
        [Fact]
        public async Task A_recorded_run_keeps_the_query_filter_tolerance_and_source()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "Recorded");
                var sales = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateColumn(engine, sales, "K", "int64");
                var dim = await McpTools.CreateTable(engine, "Dim");
                await McpTools.CreateColumn(engine, dim, "K", "int64");
                await engine.CreateRelationshipAsync("column:Sales/K", "column:Dim/K", null, true, "human");
                await McpTools.CreateMeasure(engine, sales, "Total Sales", "4321");
                var source = await engine.SaveSqlSourceAsync(null, "Warehouse", "contoso-sql.database.windows.net", "Warehouse", "interactive", null, "human");

                await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureReconcile,
                    Title = "revenue vs source",
                    TargetRef = "measure:Sales/Total Sales",
                    Enabled = true,
                    ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                    {
                        MeasureRef = "measure:Sales/Total Sales",
                        Sql = "SELECT 4321 AS total",
                        SqlSourceId = source.Id,
                        FilterDax = "'Dim'[K] = 1",
                        BlankPolicy = "zero",
                        ToleranceAbsolute = 0.5,
                        ToleranceRelative = 0.001,
                    }),
                }, "human");

                var run = await engine.RunTestSuiteAsync(false, "human");
                var recorded = await engine.RecordTestRunAsync(run.RunId, "human");
                Assert.True(recorded.Recorded, recorded.Note);
                var detail = await engine.GetTestRunAsync(run.RunId);

                var check = detail.Run.Outcomes.Checks.Single(c => c.Kind == "savedCheck" && c.Name == "revenue vs source");
                Assert.Equal("SELECT 4321 AS total", check.Query);
                Assert.Equal("'Dim'[K] = 1", check.Filter);
                Assert.False(string.IsNullOrWhiteSpace(check.Tolerance));
                Assert.Contains("0.5", check.Tolerance, StringComparison.Ordinal);
                Assert.Equal("Warehouse", check.Source);

                // A relationship check still records WHICH of its checks the row is, so the opened run can name it.
                var relationship = detail.Run.Outcomes.Checks.First(c => c.Kind == "relationship");
                Assert.False(string.IsNullOrWhiteSpace(relationship.Check));

                // And the snapshot is the run's own, not a re-read of today's definition: editing the check
                // afterwards must not change what the recorded run says it asked.
                var saved = (await engine.ListTestDefinitionsAsync()).Definitions.Single(d => d.Title == "revenue vs source");
                saved.ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                {
                    MeasureRef = "measure:Sales/Total Sales", Sql = "SELECT 9999 AS total", BlankPolicy = "zero",
                });
                await engine.SaveTestDefinitionAsync(saved, "human");
                var again = await engine.GetTestRunAsync(run.RunId);
                Assert.Equal("SELECT 4321 AS total",
                    again.Run.Outcomes.Checks.Single(c => c.Kind == "savedCheck" && c.Name == "revenue vs source").Query);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // ---- 9. The grade-cap sentence is a sentence ------------------------------------------------------
        // It read "2 integrity check(s) failed: capped at D until the data is fixed": a bracketed plural, an
        // engine word no person uses, and no sentence. It is the engine's wording because both doors read it.
        [Fact]
        public void The_grade_cap_says_what_happened_in_plain_words()
        {
            var twoFailed = TestHealthAnalyzer.Analyze(RelationshipsWith(Verdict.Fail, Verdict.Fail, Verdict.Pass), null);
            Assert.Contains("2 data checks failed. The grade cannot rise above D until they are fixed.", twoFailed.GatedBy);

            var oneFailed = TestHealthAnalyzer.Analyze(RelationshipsWith(Verdict.Fail, Verdict.Pass, Verdict.Pass), null);
            Assert.Contains("1 data check failed. The grade cannot rise above D until it is fixed.", oneFailed.GatedBy);

            foreach (var line in twoFailed.GatedBy.Concat(oneFailed.GatedBy))
            {
                Assert.DoesNotContain("(s)", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("integrity", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain('—', line);   // no em dash in anything a person reads
                Assert.EndsWith(".", line.Trim(), StringComparison.Ordinal);
            }

            var noneFailed = TestHealthAnalyzer.Analyze(RelationshipsWith(Verdict.Pass, Verdict.Pass, Verdict.Pass), null);
            Assert.DoesNotContain(noneFailed.GatedBy, l => l.Contains("data check", StringComparison.OrdinalIgnoreCase));
        }

        // ---- ROUND TWO, F3. An executed comparison records the tolerance it was actually set to -----------
        // RunReconcileDefAsync stamps the precise setting text, then OVERWRITES it with ComposeToleranceNote
        // the moment the reconciliation returns. That formatter rounds a small number to two significant
        // digits, so 0.000001234567 was recorded as 1.2e-6 and History could not recover what the check
        // allowed (Astra, 2026-09-15, round two). Run directly, which is the executed path's own formatter.
        [Fact]
        public void An_executed_comparison_keeps_its_tolerance_unrounded()
        {
            var executed = new ReconcileRunResult
            {
                ToleranceAbsolute = 0.000001234567,
                ToleranceRelative = 0.0000001,
                BlankPolicy = nameof(BlankPolicy.BlankIsZero),
            };
            var note = LocalEngine.ComposeToleranceNoteForTest(executed, null);
            Assert.Equal("Allowed difference: 0.000001234567 or 0.00001%, whichever is larger. Blanks read as zero.", note);
            Assert.DoesNotContain("e-", note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("match window", note, StringComparison.OrdinalIgnoreCase);

            // A window loose enough to hide a real error still says so, in the same sentence list.
            executed.SuspiciouslyLoose = true;
            var loose = LocalEngine.ComposeToleranceNoteForTest(executed, null);
            Assert.StartsWith("Allowed difference: 0.000001234567", loose, StringComparison.Ordinal);
            Assert.Contains("loose enough to hide a real error", loose, StringComparison.Ordinal);

            // The other two blank policies keep their own words.
            Assert.Contains("Blanks read as no value.",
                LocalEngine.ComposeToleranceNoteForTest(new ReconcileRunResult { ToleranceAbsolute = 0.5, BlankPolicy = nameof(BlankPolicy.BlankIsNull) }, null),
                StringComparison.Ordinal);
            Assert.Contains("Blanks kept distinct from zero and null.",
                LocalEngine.ComposeToleranceNoteForTest(new ReconcileRunResult { ToleranceAbsolute = 0.5, BlankPolicy = nameof(BlankPolicy.BlankIsDistinct) }, null),
                StringComparison.Ordinal);
        }

        // A limit the engine ACCEPTS must survive being written down. PlainNumber rounded to 15 significant
        // digits and stopped at 20 decimal places, so an absolute limit of 1e-21 recorded as "0" and a long
        // one lost its tail, on both formatter branches (Astra, 2026-09-15, round three). ReconcileMeasureAsync
        // accepts any finite, non-negative tolerance, so these are valid settings, not abuse.
        [Fact]
        public void A_tolerance_the_engine_accepts_is_recorded_in_full()
        {
            // Astra's three probe cases, each on BOTH branches: a completed result, and a failed one that
            // falls back to the request. The first two already passed; the third is the defect.
            var cases = new (double Absolute, string Expected)[]
            {
                (1.234567E-06, "0.000001234567"),
                (0.12345678901234566, "0.12345678901234566"),
                (1E-21, "0.000000000000000000001"),
            };
            foreach (var (absolute, expected) in cases)
            {
                var sentence = "Allowed difference: " + expected + " or 0.00001%, whichever is larger. Blanks read as zero.";

                var executed = new ReconcileRunResult
                {
                    ToleranceAbsolute = absolute, ToleranceRelative = 0.0000001, BlankPolicy = nameof(BlankPolicy.BlankIsZero),
                };
                Assert.Equal(sentence, LocalEngine.ComposeToleranceNoteForTest(executed, null));

                var failed = new ReconcileRunResult
                {
                    Error = "Could not acquire a SQL token.", Status = ReconcileStatus.InsufficientCoverage, Complete = false,
                };
                var request = new ReconcileRequest
                {
                    MeasureRef = "measure:Sales/Total Sales", Sql = "SELECT 1", BlankPolicy = "zero",
                    ToleranceAbsolute = absolute, ToleranceRelative = 0.0000001,
                };
                Assert.Equal(sentence, LocalEngine.ComposeToleranceNoteForTest(failed, request));
            }

            // The percent side is derived by shifting the decimal point, not by multiplying in floating point,
            // so 1e-7 reads as 0.00001% instead of 0.000009999999999999999%.
            var relativeOnly = new ReconcileRunResult
            {
                ToleranceAbsolute = 0, ToleranceRelative = 1E-21, BlankPolicy = nameof(BlankPolicy.BlankIsZero),
            };
            Assert.Equal("Allowed difference: 0.0000000000000000001%. Blanks read as zero.",
                LocalEngine.ComposeToleranceNoteForTest(relativeOnly, null));

            // Ordinary numbers keep reading like ordinary numbers.
            foreach (var (value, expected) in new (double, string)[] { (0.5, "0.5"), (1, "1"), (100, "100"), (0.05, "0.05") })
            {
                var plain = new ReconcileRunResult { ToleranceAbsolute = value, BlankPolicy = nameof(BlankPolicy.BlankIsZero) };
                Assert.Equal("Allowed difference: " + expected + ". Blanks read as zero.",
                    LocalEngine.ComposeToleranceNoteForTest(plain, null));
            }
        }

        // A reconciliation that could not finish never established its window, so the note falls back to what
        // the CHECK is set to rather than reporting the empty result's zeros as the check's real limits.
        [Fact]
        public void A_comparison_that_could_not_run_still_says_what_it_would_have_allowed()
        {
            // The exact shape ReconcileRunResult.Fail produces: an error, nothing measured, no window.
            var refused = new ReconcileRunResult
            {
                Error = "Could not acquire a SQL token.", Status = ReconcileStatus.InsufficientCoverage, Complete = false,
            };
            var request = new ReconcileRequest
            {
                MeasureRef = "measure:Sales/Total Sales", Sql = "SELECT 1", BlankPolicy = "zero",
                ToleranceAbsolute = 0.000001234567, ToleranceRelative = 0.0000001,
            };
            var note = LocalEngine.ComposeToleranceNoteForTest(refused, request);
            Assert.Equal("Allowed difference: 0.000001234567 or 0.00001%, whichever is larger. Blanks read as zero.", note);
            Assert.DoesNotContain("none. The numbers must match exactly", note, StringComparison.Ordinal);
        }

        // And it reaches the RECORDED run and both doors, which is where History reads it from. A live XMLA
        // connection with no reachable SQL endpoint takes the executed branch and comes back failed, which is
        // exactly the case the fallback above governs; no live gate is claimed by this.
        [Fact]
        public async Task A_recorded_run_stores_and_serialises_the_same_tolerance_sentence()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "Tolerance");
                var sales = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, sales, "Total Sales", "4321");
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-tolerance", "Tolerance",
                    _ => new ResultSet { Columns = new[] { new ColumnDef { Name = "v" } }, Rows = new[] { new object[] { 4321m } }, RowCount = 1 }));

                await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureReconcile,
                    Title = "revenue vs source",
                    TargetRef = "measure:Sales/Total Sales",
                    Enabled = true,
                    ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                    {
                        MeasureRef = "measure:Sales/Total Sales",
                        Sql = "SELECT 4321 AS total",
                        Server = "contoso-sql.database.windows.net", Database = "Warehouse",
                        BlankPolicy = "zero",
                        ToleranceAbsolute = 0.000001234567,
                        ToleranceRelative = 0.0000001,
                    }),
                }, "human");
                // A check bound by its lineage tag now finds its measure (join 10, s10fix B3), so the run reaches its SQL. Answer it here: a real run signs in
                // with the Azure CLI, which no test may do (the no-browser net failed the engine suite on exactly this call).
                engine.ReconcileSqlForTests = (server, database, sql) => new ResultSet { Columns = new[] { new ColumnDef { Name = "v" } }, Rows = new[] { new object[] { 4321m } }, RowCount = 1 };

                var run = await engine.RunTestSuiteAsync(false, "human");
                const string sentence = "Allowed difference: 0.000001234567 or 0.00001%, whichever is larger. Blanks read as zero.";
                var outcome = run.Reconciles.Single(o => o.Title == "revenue vs source");
                Assert.Equal(sentence, outcome.ToleranceNote);

                var recorded = await engine.RecordTestRunAsync(run.RunId, "human");
                Assert.True(recorded.Recorded, recorded.Note);
                var detail = await engine.GetTestRunAsync(run.RunId);
                Assert.Equal(sentence, detail.Run.Outcomes.Checks.Single(c => c.Name == "revenue vs source").Tolerance);

                // STORAGE AND SERIALISATION evidence, and nothing more. This serialises the LocalEngine
                // results; it never invokes EngineRpcTarget or McpTools, so it does not prove transport
                // parity (Astra, round three, ruling 3). What holds the two doors together is that
                // EngineRpcTarget.cs:61 and McpTools.Testing.cs:21 both return this same engine result and
                // neither formats a tolerance of its own.
                Assert.Contains(sentence.Replace("%", "%"), TestSuiteStore.Serialize(run), StringComparison.Ordinal);
                Assert.Contains(sentence, TestSuiteStore.Serialize(detail), StringComparison.Ordinal);
                Assert.DoesNotContain("1.2e-6", TestSuiteStore.Serialize(detail), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("match window", TestSuiteStore.Serialize(detail), StringComparison.OrdinalIgnoreCase);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // UAT 1.5.104, Validation: a total that matches its source to the cent said "Could not check". A check that compares only
        // the overall total used to be filed as "not enough checked" even when the two totals were identical. The person asked for a
        // total; a matching total is a pass, and a real gap is still a failure.
        private async Task<ReconcileOutcome> RunTotalCheckAsync(decimal sourceTotal, string name, string sqlError = null)
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, name);
                var sales = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, sales, "Net Sales", "75249457.11");
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-" + name, name,
                    _ => new ResultSet { Columns = new[] { new ColumnDef { Name = "v" } }, Rows = new[] { new object[] { 75249457.11m } }, RowCount = 1 }));
                await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureReconcile, Title = "Net Sales ties to source SQL", TargetRef = "measure:Sales/Net Sales", Enabled = true,
                    ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                    {
                        MeasureRef = "measure:Sales/Net Sales", Sql = "SELECT SUM(NetSalesAmount) FROM dbo.Sales",
                        Server = "contoso-sql.database.windows.net", Database = "Warehouse", BlankPolicy = "zero",
                    }),
                }, "human");
                engine.ReconcileSqlForTests = (server, database, sql) => sqlError != null
                    ? new ResultSet { Error = sqlError }
                    : new ResultSet { Columns = new[] { new ColumnDef { Name = "v" } }, Rows = new[] { new object[] { sourceTotal } }, RowCount = 1 };
                var run = await engine.RunTestSuiteAsync(false, "human");
                return run.Reconciles.Single(o => o.Title == "Net Sales ties to source SQL");
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // Round 2 N10: a SQL that leaves out the groups says which ones to add, in plain words, not "grouped mode".
        [Fact]
        public async Task A_sql_without_its_groups_says_which_columns_to_add()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "NoGroups");
                var sales = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, sales, "Net Sales", "75249457.11");
                await engine.CreateColumnAsync(sales, "Brand", "String", "Brand", "human");
                await engine.CreateColumnAsync(sales, "Year", "Int64", "Year", "human");
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-nogroups", "NoGroups",
                    _ => new ResultSet { Columns = new[] { new ColumnDef { Name = "a" }, new ColumnDef { Name = "b" }, new ColumnDef { Name = "v" } }, Rows = new[] { new object[] { "Acme", 2024L, 1m } }, RowCount = 1 }));
                await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureReconcile, Title = "Net Sales by Brand and Year ties to source SQL", TargetRef = "measure:Sales/Net Sales", Enabled = true,
                    ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                    {
                        MeasureRef = "measure:Sales/Net Sales", Sql = "SELECT SUM(NetSalesAmount) FROM dbo.Sales",
                        Server = "contoso-sql.database.windows.net", Database = "Warehouse", BlankPolicy = "zero",
                        GroupBy = new[] { "column:Sales/Brand", "column:Sales/Year" },
                    }),
                }, "human");
                engine.ReconcileSqlForTests = (server, database, sql) =>
                    new ResultSet { Columns = new[] { new ColumnDef { Name = "total" } }, Rows = new[] { new object[] { 1m } }, RowCount = 1 };
                var run = await engine.RunTestSuiteAsync(false, "human");
                var outcome = run.Reconciles.Single(o => o.Title.StartsWith("Net Sales by Brand"));
                Assert.Equal("Add Brand and Year to the SELECT, before the number.", outcome.Message);
                foreach (var jargon in new[] { "ground-truth", "grouped mode", "groupBy" })
                    Assert.DoesNotContain(jargon, outcome.Message ?? "", StringComparison.OrdinalIgnoreCase);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task A_total_that_matches_its_source_passes()
        {
            var outcome = await RunTotalCheckAsync(75249457.11m, "TotalMatch");
            Assert.Equal(Verdict.Pass, outcome.Verdict);
            Assert.Equal("The model and the source both give 75,249,457.11.", outcome.Message);
            Assert.DoesNotContain("blank-row", outcome.Message ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Insufficient", outcome.Message ?? "", StringComparison.OrdinalIgnoreCase);
        }

        // UAT 1.5.104 (F50): a query that did not run said "failed: ... A failed query is never a passing run" and named a "grand-total query".
        [Fact]
        public async Task A_query_that_did_not_run_says_so_in_plain_words()
        {
            var outcome = await RunTotalCheckAsync(0m, "QueryDown", "Invalid column name 'NetSalesAmount'.");
            Assert.Equal(Verdict.NotVerifiable, outcome.Verdict);
            Assert.Equal("The SQL total query did not run: Invalid column name 'NetSalesAmount'. Fix it and try again.", outcome.Message);
            Assert.DoesNotContain("grand-total", outcome.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("never a passing run", outcome.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task A_total_that_differs_from_its_source_still_fails()
        {
            var outcome = await RunTotalCheckAsync(75000000m, "TotalDiffers");
            Assert.Equal(Verdict.Fail, outcome.Verdict);
        }

        // UAT 1.5.104 (Kane): "176 saved mapping(s) belong to another model or predate model scoping and were hidden." A person never
        // sees a count of things that are hidden from them: checks that belong to another model leave no note.
        [Fact]
        public async Task Checks_that_belong_to_another_model_leave_no_note_for_the_person()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "Mine");
                var sales = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, sales, "Net Sales", "1");
                var dir = LayoutStore.DirFor(sessions.Current.SourcePath);
                TestSuiteStore.Upsert(dir, new TestDefinition
                {
                    Id = "other-model-check", Kind = TestKinds.MeasureReconcile, Title = "Belongs elsewhere", TargetRef = "measure:Elsewhere/Total",
                    Enabled = true, ModelIdentity = "live:deadbeef",
                }, () => "other-model-check");
                var info = await engine.ListTestDefinitionsAsync();
                Assert.Empty(info.Definitions);
                Assert.True(string.IsNullOrEmpty(info.Note), info.Note);
                var run = await engine.RunTestSuiteAsync(false, "human");
                Assert.DoesNotContain("hidden", run.Note ?? "", StringComparison.OrdinalIgnoreCase);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // UAT 1.5.104: after a SQL source and a check were saved the footer said "unsaved" for good, and opening another model asked
        // "This model has unsaved changes". Saving a source or a check writes to the person's own folders, never to the model.
        [Fact]
        public async Task Saving_a_source_and_a_check_never_marks_the_model_unsaved()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "Clean");
                var sales = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, sales, "Net Sales", "1");
                await engine.SaveAsync(Path.Combine(ws, "Clean.bim"), "bim", false, "human");
                Assert.False((await engine.SessionInfoAsync()).HasUnsavedChanges);

                var source = await engine.SaveSqlSourceAsync(null, "Northstar Source", "contoso-sql.database.windows.net", "Warehouse", "interactive", null, "human");
                await engine.SetTableSourceMappingAsync("Sales", source.Id, "dbo", "Sales", "human");
                await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureReconcile, Title = "Net Sales ties to source SQL", TargetRef = "measure:Sales/Net Sales", Enabled = true,
                    ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest { MeasureRef = "measure:Sales/Net Sales", Sql = "SELECT 1", SqlSourceId = source.Id }),
                }, "human");
                await engine.ListTestDefinitionsAsync();
                await engine.ListTableSourceMappingsAsync();
                var info = await engine.SessionInfoAsync();
                Assert.False(info.HasUnsavedChanges, "a source and a check are saved outside the model");
                Assert.False(info.HasUnsavedEdits);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // fix2-uat-validation: what a check says about itself is in the person's words, never the reconciler's ("All clear: 3/3 cells
        // reconcile within tolerance", "[DAX=... vs SQL=NULL, delta=..., rel=100%]", "run list_columns").
        [Fact]
        public void A_passing_comparison_says_places_not_cells()
        {
            var said = LocalEngine.PlainReconciledMessage(new ReconcileRunResult
            { Status = ReconcileStatus.Reconciled, Matches = 3, Complete = true });
            Assert.Equal("All 3 places compared match.", said);
            Assert.DoesNotContain("cells", said);
            Assert.DoesNotContain("tolerance", said);
        }

        [Fact]
        public void A_difference_names_the_biggest_place_without_engine_syntax()
        {
            var cell = new ReconcileCell { GroupingKey = Array.Empty<string>(), Dax = 75249457.11m, SqlNull = true };
            var r = new ReconcileRunResult
            {
                Status = ReconcileStatus.Mismatch, Mismatches = 1, WorstKey = "(grand total)",
                Cells = new[] { new CellVerdict { Cell = cell, Verdict = ReconcileVerdict.Mismatch } },
            };
            var said = LocalEngine.PlainWorstLine(r, "Northstar Source");
            Assert.Equal(" The biggest difference is the overall total: the model gives 75,249,457.11 and Northstar Source gives nothing.", said);
            foreach (var word in new[] { "DAX=", "SQL=", "delta", "rel=", "[", "NULL" }) Assert.DoesNotContain(word, said);
        }

        [Fact]
        public void A_renamed_grouping_column_says_renamed_or_removed_and_offers_picking_it_again()
        {
            var said = LocalEngine.GroupColumnGoneMessage("column:Product/Brand");
            Assert.Equal("The column Product Brand was renamed or removed, so this check cannot group by it. Pick the column again.", said);
            Assert.DoesNotContain("list_columns", said);
            Assert.DoesNotContain("groupBy", said);
            Assert.Equal("The column Product Brand was renamed or removed, so this check cannot group by it. Pick the column again.",
                LocalEngine.GroupColumnGoneMessage("'Product'[Brand]"));
        }

        private static RelationshipIntegrityReport RelationshipsWith(params Verdict[] verdicts)
            => RelationshipIntegrity.Evaluate(verdicts.Select((v, i) => new RelationshipCheckInput
            {
                Name = "r" + i,
                ManyTable = "F", ManyColumn = "K", OneTable = "D", OneColumn = "K",
                Cardinality = "manyToOne", IsActive = true,
                ManyColumnType = "Int64", OneColumnType = "Int64",
                Probe = new RelationshipProbeResult
                {
                    OrphanRows = v == Verdict.Fail ? 7 : 0,
                    DuplicateKeys = 0, BlankForeignKeys = 0, BlankKeys = 0, ManyRowCount = 100, OneRowCount = 10,
                },
            }).ToList());
    }
}
