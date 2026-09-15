using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The Tests redesign Kane approved on 2026-09-15 (the 1.2.0 candidate). Five behaviour changes,
    /// each pinned here because each one was measured on the shipped code first (Astra's Tests UAT, sections 3
    /// and 4): role filters could move the grade, a Tests run replayed the Model interview, a recorded run kept
    /// only a summary, an empty tick list ran everything, and a trusted-answer result carried a SQL comparison
    /// row that made the page print "SQL result NULL".</summary>
    public sealed class TestsRedesignTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static (LocalEngine Engine, SessionManager Sessions, string Ws) Make()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-redesign-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            var sessions = new SessionManager();
            return (new LocalEngine(sessions, new Pro(), ws), sessions, ws);
        }

        private static async Task OpenNamedModelAsync(LocalEngine engine, SessionManager sessions, string ws, string name)
        {
            await engine.CreateModelAsync(name, 1604);
            sessions.Current.SourcePath = Path.Combine(ws, name + ".bim");
        }

        private static ResultSet OneCell(object value) => new ResultSet
        {
            Columns = new[] { new ColumnDef { Name = "v" } },
            Rows = new[] { new[] { value } },
            RowCount = 1,
        };

        // ---- 1. Security leaves Tests, and the grade stops depending on it -------------------------------
        // Astra measured the cost of leaving it in: with everything else passing, flipping the role filters from
        // all-passing to all-failing moved the grade from 100/A to 77.8/C. Adding one always-true role filter to
        // a model must now change nothing at all about the run's health.
        [Fact]
        public async Task A_role_filter_cannot_move_the_test_grade()
        {
            var root = Path.Combine(Path.GetTempPath(), "smx-grade-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var model = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Pro(), root);
                await engine.OpenAsync(model);

                var before = await engine.RunTestSuiteAsync(false, "human");

                await engine.CreateRoleAsync("Everyone", "read", "human");
                var table = (await engine.ListTreeAsync(null)).First(o => o.Kind == "table");
                await engine.SetTablePermissionAsync("Everyone", table.Ref, "1 = 1", "human");

                var after = await engine.RunTestSuiteAsync(false, "human");

                Assert.Equal(before.Health.Grade, after.Health.Grade);
                Assert.Equal(before.Health.Overall, after.Health.Overall);
                Assert.Equal(before.Health.CoveragePct, after.Health.CoveragePct);
                Assert.Equal(before.Health.Checked, after.Health.Checked);
                Assert.Equal(before.Health.Failed, after.Health.Failed);
                Assert.DoesNotContain(after.Health.Categories, c => string.Equals(c.Category, "Security", StringComparison.OrdinalIgnoreCase));
                // Nothing security-shaped may reach either door on a run result.
                Assert.DoesNotContain("\"security\"", TestSuiteStore.Serialize(after), StringComparison.Ordinal);
                // The evaluator itself is gone, so it cannot be wired back in by accident.
                Assert.Null(typeof(LocalEngine).Assembly.GetType("Semanticus.Engine.SecurityStaticChecks"));
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        // ---- 2. The Model interview leaves a Tests run ---------------------------------------------------
        // A live run used to replay every saved value question and write the outcome back to the interview
        // store. The store, its questions and its own operations stay; a Tests run must not touch them.
        [Fact]
        public async Task A_full_run_makes_no_interview_call()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "NoInterview");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Total Sales", "4321");
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-interview", "NoInterview",
                    _ => OneCell(4321m)));

                var question = await engine.AddInterviewQuestionAsync(
                    "What are total sales?", "value", "EVALUATE ROW(\"v\", [Total Sales])",
                    null, null, null, null, "4321", null, false, null, "user", "project", "human");
                Assert.Null(question.LastRun);

                var run = await engine.RunTestSuiteAsync(false, "human");

                // The saved question was not asked, so its recorded outcome is still the one it was saved with.
                var pack = await engine.ListInterviewQuestionsAsync("project");
                Assert.Null(Assert.Single(pack.Questions).LastRun);
                // And no interview evidence reaches either door from a Tests run.
                Assert.DoesNotContain("\"interview\"", TestSuiteStore.Serialize(run), StringComparison.OrdinalIgnoreCase);
                // The replay hook a Tests run used to call is gone.
                Assert.Null(typeof(LocalEngine).GetMethod("ReplayInterviewEvidenceForTestsAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // ---- 3. A recorded run keeps every check's outcome, not a summary --------------------------------
        [Fact]
        public async Task A_recorded_run_keeps_every_check_outcome()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "Recorded");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Total Sales", "4321");
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-recorded", "Recorded",
                    _ => OneCell(1234m)));
                await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "Total Sales is 4321",
                    TargetRef = "measure:Sales/Total Sales",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest
                    {
                        MeasureRef = "measure:Sales/Total Sales", ExpectedValue = "4321",
                    }),
                }, "human");

                var run = await engine.RunTestSuiteAsync(true, "human");
                Assert.True(run.Persisted, "the full run should have been recorded");

                var line = Assert.Single(TestSuiteStore.ReadRunLines(Path.Combine(ws, LayoutStore.DirName)));
                Assert.Contains("\"outcomes\"", line, StringComparison.Ordinal);
                Assert.Contains("4321", line, StringComparison.Ordinal);   // the expected answer
                Assert.Contains("1234", line, StringComparison.Ordinal);   // what the model actually returned

                // list_test_runs stays a summary list: the per-check detail is not in it.
                var history = await engine.ListTestRunsAsync(20);
                Assert.DoesNotContain("\"outcomes\"", TestSuiteStore.Serialize(history), StringComparison.Ordinal);
                Assert.Equal(run.RunId, Assert.Single(history.Runs).RunId);

                // One run opens in full, and it opens the same way through the agent door: one engine path,
                // two doors (golden rule 2).
                foreach (var detail in new[] { await engine.GetTestRunAsync(run.RunId), await McpToolsTesting.GetTestRun(engine, run.RunId) })
                {
                    Assert.Null(detail.Note);
                    Assert.Equal(run.RunId, detail.Run.RunId);
                    Assert.Equal(run.Health.Grade, detail.Run.Health.Grade);
                    Assert.Equal(20, detail.Run.Outcomes.ContextCap);
                    Assert.Contains("rows of detail", detail.Run.Outcomes.Note);

                    var saved = Assert.Single(detail.Run.Outcomes.Checks, c => c.Kind == "savedCheck");
                    Assert.Equal("Total Sales is 4321", saved.Name);
                    Assert.Equal(Verdict.Fail, saved.Verdict);
                    Assert.Equal("4321", saved.Expected);
                    Assert.Equal("1234", saved.Actual);
                    Assert.Equal(-3087m, saved.Difference);
                    Assert.False(string.IsNullOrWhiteSpace(saved.Note));
                }

                // An id this model's history does not have says so rather than returning an empty run.
                var missing = await engine.GetTestRunAsync("not-a-run");
                Assert.Null(missing.Run);
                Assert.Contains("no recorded run with that id", missing.Note);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // A run recorded before 1.2.0 kept its totals and nothing else. It must still open, and say so.
        [Fact]
        public async Task A_run_recorded_before_1_2_0_opens_with_an_honest_note()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "Legacy");
                var dir = Path.Combine(ws, LayoutStore.DirName);
                var legacy = new TestRunRecord
                {
                    SchemaVersion = 1,
                    RunId = "legacyrun",
                    When = "2026-08-01T09:00:00.0000000Z",
                    Live = true,
                    Health = new TestHealth { Grade = "B", CoveragePct = 82.6 },
                };
                Assert.True(TestSuiteStore.AppendRun(dir, TestSuiteStore.Serialize(legacy)));

                var detail = await engine.GetTestRunAsync("legacyrun");

                Assert.Equal("legacyrun", detail.Run.RunId);
                Assert.Equal("B", detail.Run.Health.Grade);
                Assert.Null(detail.Run.Outcomes);
                Assert.Contains(
                    "Outcomes were not recorded before 1.2.0, so this run kept its grade and totals but not what each check found.",
                    detail.Note, StringComparison.Ordinal);

                // The grade itself is preserved exactly as it was scored, and the reader is told the rules it
                // was scored under, so History can label it instead of comparing letters across a rule change.
                Assert.Equal("B", detail.Run.Health.Grade);
                Assert.Equal(82.6, detail.Run.Health.CoveragePct);
                Assert.True(detail.GradedBeforeSecurityLeftTests);
                Assert.Contains("role filter", detail.Note, StringComparison.OrdinalIgnoreCase);

                // A run recorded under the new rules is not labelled.
                await engine.RunTestSuiteAsync(true, "human");
                var fresh = (await engine.ListTestRunsAsync(20)).Runs.Last(r => r.RunId != "legacyrun");
                Assert.False((await engine.GetTestRunAsync(fresh.RunId)).GradedBeforeSecurityLeftTests);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // The saved checks are not the only thing a recorded run has to keep: the relationship checks are the
        // ones that run on every model with no setup at all, so a recorded run that dropped them would lose
        // most of what it had decided.
        [Fact]
        public async Task A_recorded_run_keeps_the_relationship_checks_too()
        {
            var root = Path.Combine(Path.GetTempPath(), "smx-relrec-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var model = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Pro(), root);
                await engine.OpenAsync(model);

                var run = await engine.RunTestSuiteAsync(true, "human");
                Assert.True(run.Persisted, "the full run should have been recorded");

                var detail = await engine.GetTestRunAsync(run.RunId);
                var recorded = detail.Run.Outcomes.Checks.Where(c => c.Kind == "relationship").ToArray();

                // One recorded outcome per check the live run decided, with the same verdicts and no invention.
                var live = run.Relationships.Relationships.SelectMany(r => r.Checks).Where(c => c != null).ToArray();
                Assert.NotEmpty(recorded);                      // equal counts prove nothing if both are zero
                Assert.Equal(live.Length, recorded.Length);
                Assert.Equal(live.Select(c => c.Verdict).OrderBy(v => v), recorded.Select(c => c.Verdict).OrderBy(v => v));
                Assert.All(recorded, c =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(c.Name));
                    Assert.False(string.IsNullOrWhiteSpace(c.Check));
                    // A check that counts bad rows expects zero of them; one that compares types counts nothing
                    // and says nothing on either side rather than inventing a number.
                    if (c.Actual == null) Assert.Null(c.Expected);
                    else Assert.Equal("0", c.Expected);
                });
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        // ---- 4. Run scope: ticking nothing runs nothing, and a dead section is refused --------------------
        [Fact]
        public async Task An_empty_tick_list_and_a_dead_section_are_refused()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "Scope");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Total Sales", "4321");

                var nothingTicked = await engine.RunTestSuiteAsync(false, "human", Array.Empty<string>(), null);
                Assert.Empty(nothingTicked.Reconciles);
                Assert.Null(nothingTicked.Health);
                Assert.Contains("did not pick", nothingTicked.Note ?? "", StringComparison.OrdinalIgnoreCase);

                var security = await engine.RunTestSuiteAsync(false, "human", null, new[] { "security" });
                Assert.Empty(security.Reconciles);
                Assert.Null(security.Health);
                Assert.Contains("no longer part of Tests", security.Note ?? "", StringComparison.OrdinalIgnoreCase);

                var history = await engine.RunTestSuiteAsync(false, "human", null, new[] { "history" });
                Assert.Empty(history.Reconciles);
                Assert.Null(history.Health);
                Assert.Contains("nothing to run", history.Note ?? "", StringComparison.OrdinalIgnoreCase);

                // A real section still runs, and so does an omitted scope.
                var measures = await engine.RunTestSuiteAsync(false, "human", null, new[] { "measures" });
                Assert.NotNull(measures.Health);
                var everything = await engine.RunTestSuiteAsync(false, "human");
                Assert.NotNull(everything.Health);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // ---- a. Recording saves the run being shown, without running anything again ----------------------
        // "Record it" promised to save the run on screen. It could only ever save a NEW execution, against data
        // that may have moved since, so the saved run could disagree with the one the person was looking at.
        [Fact]
        public async Task Recording_saves_the_run_being_shown_and_never_runs_it_again()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "RecordShown");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Total Sales", "4321");
                var answer = 1234m;
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-record", "RecordShown",
                    _ => OneCell(answer)));
                await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "Total Sales is 4321",
                    TargetRef = "measure:Sales/Total Sales",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest
                    {
                        MeasureRef = "measure:Sales/Total Sales", ExpectedValue = "4321",
                    }),
                }, "human");

                var shown = await engine.RunTestSuiteAsync(false, "human");
                Assert.False(shown.Persisted);
                Assert.Empty(TestSuiteStore.ReadRunLines(Path.Combine(ws, LayoutStore.DirName)));

                // The data moves. A recording that re-ran would now see a pass.
                answer = 4321m;

                var recorded = await engine.RecordTestRunAsync(shown.RunId, "human");
                Assert.True(recorded.Recorded);
                Assert.Equal(shown.RunId, recorded.RunId);

                var detail = await engine.GetTestRunAsync(shown.RunId);
                var saved = Assert.Single(detail.Run.Outcomes.Checks, c => c.Kind == "savedCheck");
                Assert.Equal(Verdict.Fail, saved.Verdict);      // the run being shown, not a fresh one
                Assert.Equal("4321", saved.Expected);
                Assert.Equal("1234", saved.Actual);
                Assert.Equal(shown.Health.Grade, detail.Run.Health.Grade);
                Assert.Equal(shown.Health.CoveragePct, detail.Run.Health.CoveragePct);
                // The snapshot describes itself: which model, which connection, what scope.
                Assert.Equal("RecordShown", detail.Run.ModelName);
                Assert.NotNull(detail.Run.Scope);
                Assert.Equal(1, detail.Run.DefinitionCount);

                // Recording the same run twice keeps one snapshot and says so.
                var again = await engine.RecordTestRunAsync(shown.RunId, "human");
                Assert.False(again.Recorded);
                Assert.Contains("already", again.Note, StringComparison.OrdinalIgnoreCase);
                Assert.Single(TestSuiteStore.ReadRunLines(Path.Combine(ws, LayoutStore.DirName)));

                // A run this engine never produced is refused by name, not recorded as an empty shell.
                var unknown = await McpToolsTesting.RecordTestRun(engine, "not-a-run");
                Assert.False(unknown.Recorded);
                Assert.Contains("no run with that id", unknown.Note, StringComparison.OrdinalIgnoreCase);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // ---- b. The result names which groups ran and which did not ---------------------------------------
        [Fact]
        public async Task A_run_says_which_groups_ran_and_which_were_left_out()
        {
            var root = Path.Combine(Path.GetTempPath(), "smx-groups-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var model = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Pro(), root);
                await engine.OpenAsync(model);
                var measure = (await engine.ListMeasuresAsync())[0];
                var def = await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "A trusted answer",
                    TargetRef = measure.Ref,
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = measure.Ref, ExpectedValue = "1" }),
                }, "human");

                var everything = await engine.RunTestSuiteAsync(false, "human");
                Assert.Equal(new[] { "saved checks", "relationships", "table counts" }, everything.Scope.Ran);
                Assert.Empty(everything.Scope.Skipped);
                Assert.False(everything.Scope.Partial);

                // Ticking checks skips the automatic groups, and the result says so instead of leaving the
                // person to discover that "checked on every run" was not true of this run.
                var ticked = await engine.RunTestSuiteAsync(false, "human", new[] { def.Id }, null);
                Assert.Equal(new[] { "saved checks" }, ticked.Scope.Ran);
                Assert.Equal(new[] { "relationships", "table counts" }, ticked.Scope.Skipped);
                Assert.True(ticked.Scope.Partial);

                // CHANGED 2026-09-15 (Astra, finding 3): this used to expect "relationships" to run the table
                // counts as well, because one flag governed both families. That is the defect: the page offers
                // them as separate choices, so asking for relationships must not open a SQL connection nobody
                // asked for. Table counts have their own section now, pinned in TestsFixesTests.
                var relsOnly = await engine.RunTestSuiteAsync(false, "human", null, new[] { "relationships" });
                Assert.Equal(new[] { "relationships" }, relsOnly.Scope.Ran);
                Assert.Equal(new[] { "saved checks", "table counts" }, relsOnly.Scope.Skipped);

                // A part-run is graded on what it ran. It no longer throws that grade away, and it no longer
                // offers itself as the model's overall grade either: the note says what the letter covers.
                Assert.NotEqual("Partial", ticked.Health.Grade);
                Assert.Contains(ticked.Health.Grade, new[] { "A", "B", "C", "D", "F" });
                Assert.False(string.IsNullOrWhiteSpace(ticked.Scope.GradeCovers));
                Assert.Contains("only the part you ran", ticked.Note, StringComparison.OrdinalIgnoreCase);
                Assert.False(ticked.Persisted);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        // ---- c. Show rows opens real unmatched rows, bounded, and says it reads current data --------------
        [Fact]
        public async Task Unmatched_rows_are_bounded_and_disclose_that_they_read_current_data()
        {
            var root = Path.Combine(Path.GetTempPath(), "smx-unmatched-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var model = Path.Combine(root, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Pro(), root);
                await engine.OpenAsync(model);
                string asked = null;
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-unmatched", "Unmatched", q =>
                {
                    asked = q;
                    return new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "Key" }, new ColumnDef { Name = "UnmatchedRows" } },
                        Rows = new[] { new object[] { "C-9912", 1200L }, new object[] { "C-4410", 647L } },
                        RowCount = 2,
                    };
                }));
                var run = await engine.RunTestSuiteAsync(false, "human");
                var relationship = run.Relationships.Relationships[0].Name;

                var rows = await engine.GetUnmatchedRowsAsync(relationship, 200, "human");

                Assert.Null(rows.Error);
                Assert.Equal(relationship, rows.Relationship);
                Assert.Equal(200, rows.Cap);
                Assert.Equal(2, rows.RowsReturned);
                Assert.Equal(2, rows.Rows.Length);
                Assert.Contains("reads the data as it is now", rows.Note, StringComparison.OrdinalIgnoreCase);
                // The question asked is the same one the check counted, so the two can never disagree.
                Assert.Equal(RelationshipProbes.UnmatchedRowsQuery(
                    rows.ManyTable, rows.ManyColumn, rows.OneTable, rows.OneColumn, 200), asked);

                // The cap is real and is disclosed rather than quietly applied.
                var capped = await engine.GetUnmatchedRowsAsync(relationship, 1, "human");
                Assert.Equal(1, capped.Cap);
                Assert.Equal(1, capped.RowsReturned);
                Assert.True(capped.Truncated);
                Assert.Contains("1", capped.Note, StringComparison.Ordinal);

                var unknown = await engine.GetUnmatchedRowsAsync("Nothing[X] to Nowhere[Y]", 200, "human");
                Assert.Null(unknown.Rows);
                Assert.Contains("no relationship", unknown.Note ?? unknown.Error ?? "", StringComparison.OrdinalIgnoreCase);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        // ---- d. Wording that does not turn uncertainty into a cause ---------------------------------------
        [Fact]
        public void Differing_counts_and_differing_comparisons_say_exactly_what_they_prove()
        {
            var counts = TableRowCountReconciliation.Evaluate(new TableRowCountInput
            {
                ModelTable = "Sales", ModelCount = 1236402, SourceCount = 1236397, SnapshotAligned = false,
            });
            Assert.Equal(Verdict.NotVerifiable, counts.Check.Verdict);
            Assert.Contains(
                "Matching snapshots were not confirmed, so this alone does not prove missing rows.",
                counts.Check.Message, StringComparison.Ordinal);

            // "3 differ" beside a total invites the reader to think those three explain the total. Say the
            // counts, and disclose the cap whenever fewer rows are shown than were compared.
            Assert.Equal("3 comparisons differ; 45 match.",
                LocalEngine.ComparisonCountLine(3, 45, 3, 48));
            Assert.Equal("3 comparisons differ; 45 match. Showing the 2 biggest differences of 48 comparisons.",
                LocalEngine.ComparisonCountLine(3, 45, 2, 48));
            Assert.Equal("1 comparison differs; 1 matches.",
                LocalEngine.ComparisonCountLine(1, 1, 2, 2));
        }

        // ---- 5. A trusted-answer result carries expected, actual and the difference, and no SQL ----------
        // The old result fabricated one comparison row with an empty SQL side, so the page printed
        // "SQL result NULL" beside a check that never asked a database anything.
        [Fact]
        public async Task A_trusted_answer_result_carries_expected_actual_and_difference_and_no_sql()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "Trusted");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Total Sales", "4321");
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-trusted", "Trusted",
                    _ => OneCell(1234m)));

                var outcome = await engine.TryTestAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "Total Sales is 4321",
                    TargetRef = "measure:Sales/Total Sales",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest
                    {
                        MeasureRef = "measure:Sales/Total Sales", ExpectedValue = "4321",
                    }),
                }, "human");

                Assert.Equal(Verdict.Fail, outcome.Verdict);
                // No fabricated comparison row: there is no source side to show.
                Assert.True(outcome.Rows == null || outcome.Rows.Length == 0);
                Assert.Equal(0, outcome.RowsTotal);

                var wire = TestSuiteStore.Serialize(outcome);
                Assert.DoesNotContain("\"rows\"", wire, StringComparison.Ordinal);
                Assert.Contains("\"expected\"", wire, StringComparison.Ordinal);
                Assert.Contains("\"actual\"", wire, StringComparison.Ordinal);
                Assert.Contains("\"difference\"", wire, StringComparison.Ordinal);

                Assert.Equal("4321", outcome.Expected);
                Assert.Equal("1234", outcome.Actual);
                Assert.Equal(-3087m, outcome.Difference);

                // A check that could not run still says what it was looking for, and still shows no source side.
                engine.SetLiveConnectionForTest(null);
                var offline = await engine.TryTestAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "Total Sales is 4321",
                    TargetRef = "measure:Sales/Total Sales",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest
                    {
                        MeasureRef = "measure:Sales/Total Sales", ExpectedValue = "4321",
                    }),
                }, "human");
                Assert.Equal(Verdict.NotVerifiable, offline.Verdict);
                Assert.Equal("4321", offline.Expected);
                Assert.Null(offline.Actual);
                Assert.Null(offline.Difference);
                Assert.True(offline.Rows == null || offline.Rows.Length == 0);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }
    }
}
