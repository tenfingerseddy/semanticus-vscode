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
                var source = await engine.SaveSqlSourceAsync(null, "Warehouse", "contoso-sql.example", "Warehouse", "interactive", null, "human");
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
                var source = await engine.SaveSqlSourceAsync(null, "Warehouse", "contoso-sql.example", "Warehouse", "interactive", null, "human");
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
                var source = await engine.SaveSqlSourceAsync(null, "Warehouse", "contoso-sql.example", "Warehouse", "interactive", null, "human");
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
                var source = await engine.SaveSqlSourceAsync(null, "Warehouse", "contoso-sql.example", "Warehouse", "interactive", null, "human");

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
                        Server = "contoso-sql.example", Database = "Warehouse",
                        BlankPolicy = "zero",
                        ToleranceAbsolute = 0.000001234567,
                        ToleranceRelative = 0.0000001,
                    }),
                }, "human");

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
