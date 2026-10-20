using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>UAT P38/F55 (Kane, 18 Oct): a Validation check that differs can be waived. The difference is accepted, so it stops counting as a
    /// failure, but it is never a pass and it stays on the page as accepted. The waiver covers the exact difference that was accepted: when the
    /// difference moves, the check differs again. Each test runs a real check against a model and a mocked source.</summary>
    [Collection("restore-root")]
    public sealed class CheckWaiverTests : IDisposable
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _ws;
        private readonly SessionManager _sessions = new SessionManager();
        private readonly LocalEngine _engine;
        private decimal _okSource = 75249457.11m;
        private decimal _offSource = 75000000m;

        public CheckWaiverTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-waive-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ConnectionRegistry.RootOverride = _root;
            _ws = Path.Combine(Path.GetTempPath(), "smx-waive-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_ws);
            _engine = new LocalEngine(_sessions, new Pro(), _ws);
        }

        public void Dispose()
        {
            _sessions.Dispose();
            ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
            try { Directory.Delete(_ws, true); } catch { }
        }

        /// <summary>Two checks on one model: "Net Sales" (the source agrees) and "Gross Sales" (the source is off by 249,457.11).</summary>
        private async Task SetUpAsync()
        {
            await _engine.CreateModelAsync("Waive", 1604);
            _sessions.Current.SourcePath = Path.Combine(_ws, "Waive.bim");
            var sales = await McpTools.CreateTable(_engine, "Sales");
            await McpTools.CreateMeasure(_engine, sales, "Net Sales", "75249457.11");
            await McpTools.CreateMeasure(_engine, sales, "Gross Sales", "75249457.11");
            _engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-waive", "Waive",
                _ => new ResultSet { Columns = new[] { new ColumnDef { Name = "v" } }, Rows = new[] { new object[] { 75249457.11m } }, RowCount = 1 }));
            foreach (var name in new[] { "Net Sales", "Gross Sales" })
                await _engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureReconcile, Title = name + " ties to source", TargetRef = "measure:Sales/" + name, Enabled = true,
                    ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                    {
                        MeasureRef = "measure:Sales/" + name, Sql = "SELECT SUM(" + name.Replace(" ", "") + ") FROM dbo.Sales",
                        Server = "contoso-sql.database.windows.net", Database = "Warehouse", BlankPolicy = "zero",
                    }),
                }, "human");
            _engine.ReconcileSqlForTests = (server, database, sql) => new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "v" } },
                Rows = new[] { new object[] { sql.Contains("GrossSales") ? _offSource : _okSource } }, RowCount = 1,
            };
        }

        private static ReconcileOutcome Gross(TestSuiteRunResult run) => run.Reconciles.Single(o => o.Title == "Gross Sales ties to source");

        [Fact]
        public async Task A_waived_check_is_accepted_in_the_headline_and_never_a_pass()
        {
            await SetUpAsync();
            var before = await _engine.RunTestSuiteAsync(false, "human");
            Assert.Equal(Verdict.Fail, Gross(before).Verdict);
            Assert.Equal("1 of 2 checks pass: 1 differs.", before.Headline);

            // One press, no reason: the note is optional.
            await _engine.WaiveFindingAsync("test", Gross(before).DefId, null, null, "human");
            var after = await _engine.RunTestSuiteAsync(false, "human");
            var o = Gross(after);
            Assert.NotNull(o.Waiver);
            Assert.Equal("human", o.Waiver.By);
            Assert.False(string.IsNullOrEmpty(o.Waiver.When));
            Assert.Equal("1 of 2 checks pass, 1 accepted.", after.Headline);
            var tally = CheckTally.Of(after);
            Assert.Equal((1, 0, 0, 1), (tally.Passed, tally.Differs, tally.Cannot, tally.Accepted));
            Assert.Equal(0, after.Health.Failed);   // accepted: not a failure ...
            Assert.Equal(1, after.Health.Passed);   // ... and not a pass either
            Assert.Equal(1, after.Health.Accepted);
            Assert.Equal(before.Health.CoveragePct, after.Health.CoveragePct);   // a decision the person made is still covered
            Assert.Contains("1 accepted", McpToolsTesting.RunLabel(after));
            Assert.DoesNotContain("2 of 2", McpToolsTesting.RunLabel(after));
        }

        [Fact]
        public async Task The_report_says_accepted_not_passed()
        {
            await SetUpAsync();
            var first = await _engine.RunTestSuiteAsync(false, "human");
            await _engine.WaiveFindingAsync("test", Gross(first).DefId, null, "known rounding in the warehouse", "human");
            var run = await _engine.RunTestSuiteAsync(false, "human");
            var markdown = TestReportRenderer.Render(run);
            Assert.Contains("- Checks: 1 of 2 checks pass, 1 accepted.", markdown);
            Assert.Contains("Accepted difference", markdown);
            Assert.Contains("known rounding in the warehouse", markdown);
            Assert.DoesNotContain("Root causes\n\n- Measure Gross", markdown.Replace("\r\n", "\n"));
            Assert.DoesNotContain("Gross Sales ties to source**: Fail", markdown);
        }

        [Fact]
        public async Task A_waiver_covers_only_the_difference_it_accepted()
        {
            await SetUpAsync();
            var first = await _engine.RunTestSuiteAsync(false, "human");
            await _engine.WaiveFindingAsync("test", Gross(first).DefId, null, null, "human");
            _offSource = 75000001m;   // the gap moved
            var moved = await _engine.RunTestSuiteAsync(false, "human");
            Assert.Null(Gross(moved).Waiver);
            Assert.True(Gross(moved).WaiverLapsed);
            Assert.Equal(Verdict.Fail, Gross(moved).Verdict);
            Assert.Equal("1 of 2 checks pass: 1 differs.", moved.Headline);
            _offSource = 75000000m;   // and back to the accepted gap
            var back = await _engine.RunTestSuiteAsync(false, "human");
            Assert.NotNull(Gross(back).Waiver);
        }

        [Fact]
        public async Task Undo_waive_brings_the_check_back_as_differs_in_one_step()
        {
            await SetUpAsync();
            var first = await _engine.RunTestSuiteAsync(false, "human");
            await _engine.WaiveFindingAsync("test", Gross(first).DefId, null, null, "human");
            await _engine.UnwaiveFindingAsync("test", Gross(first).DefId, null, "human");
            var run = await _engine.RunTestSuiteAsync(false, "human");
            Assert.Null(Gross(run).Waiver);
            Assert.Equal("1 of 2 checks pass: 1 differs.", run.Headline);
            Assert.DoesNotContain(await _engine.ListWaiversAsync(), w => w.System == "test");
        }

        // The waiver is read from the run on screen too: waiving updates the last run in place, so the report of that run and the grade
        // say accepted without a second run (a second run would be a different question).
        [Fact]
        public async Task Waiving_updates_the_run_on_screen_without_a_new_run()
        {
            await SetUpAsync();
            var first = await _engine.RunTestSuiteAsync(false, "human");
            await _engine.WaiveFindingAsync("test", Gross(first).DefId, null, null, "human");
            Assert.NotNull(Gross(first).Waiver);
            Assert.Equal("1 of 2 checks pass, 1 accepted.", first.Headline);
            var report = await _engine.ExportTestReportAsync();
            Assert.Contains("1 of 2 checks pass, 1 accepted.", report.Markdown);
            await _engine.UnwaiveFindingAsync("test", Gross(first).DefId, null, "human");
            Assert.Null(Gross(first).Waiver);
            Assert.Equal("1 of 2 checks pass: 1 differs.", first.Headline);
        }

        [Fact]
        public async Task A_check_that_passes_has_nothing_to_accept_and_says_so()
        {
            await SetUpAsync();
            var first = await _engine.RunTestSuiteAsync(false, "human");
            var net = first.Reconciles.Single(o => o.Title == "Net Sales ties to source");
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => _engine.WaiveFindingAsync("test", net.DefId, null, null, "human"));
            Assert.Contains("nothing to accept", ex.Message);
        }

        // The assistant's door: the same acts through its own tools. Its run_tests result says accepted, never passed.
        [Fact]
        public async Task The_assistants_tools_waive_and_take_back_and_its_run_says_accepted()
        {
            await SetUpAsync();
            var first = await McpToolsTesting.RunTests(_engine);
            var id = Gross(first).DefId;
            await McpTools.WaiveFinding(_engine, "test", id, null, null);
            var run = await McpToolsTesting.RunTests(_engine);
            Assert.Equal("1 of 2 checks pass, 1 accepted.", run.Headline);
            Assert.Contains("1 accepted", McpToolsTesting.RunLabel(run));
            var w = Assert.Single(await McpTools.ListWaivers(_engine), x => x.System == "test");
            Assert.Equal("agent", w.By);
            Assert.True(w.InForce);
            await McpTools.UnwaiveFinding(_engine, "test", id, null);
            Assert.Equal("1 of 2 checks pass: 1 differs.", (await McpToolsTesting.RunTests(_engine)).Headline);
        }

        // Verifier (v-fix-validation-waive) saw Ctrl+Z after Waive leave the page, the headline and a new report in disagreement. The decision
        // (coordinator, 18 Oct): a check's waiver lives with the checks and is not on the model's undo stack. Undo waive is the one way back, so
        // Ctrl+Z cannot take it out from under the run on screen: the page, the headline, the list and a new report all keep agreeing.
        [Fact]
        public async Task Ctrl_Z_after_waive_leaves_the_waiver_alone_and_page_headline_and_report_agree()
        {
            await SetUpAsync();
            var first = await _engine.RunTestSuiteAsync(false, "human");
            var revision = _sessions.Current.Revision;
            await _engine.WaiveFindingAsync("test", Gross(first).DefId, null, null, "human");
            Assert.Equal(revision, _sessions.Current.Revision);   // not a model edit, so not an undo step
            Assert.Equal("1 of 2 checks pass, 1 accepted.", first.Headline);
            Assert.NotNull(Gross(first).Waiver);
            Assert.Single(await _engine.ListWaiversAsync(), w => w.System == "test");
            Assert.Contains("1 accepted", (await _engine.ExportTestReportAsync()).Markdown);
        }

        // The waiver lives with the checks, never in the model: waiving does not mark the model unsaved, writes nothing into it (so a Publish
        // cannot carry it to production), and the record is in the folder's .semanticus beside the checks, where a Save and a reopen of a live
        // model (which fetches a fresh copy of the model) cannot lose it.
        [Fact]
        public async Task A_waiver_lives_with_the_checks_not_in_the_model()
        {
            await SetUpAsync();
            var first = await _engine.RunTestSuiteAsync(false, "human");
            var s = _sessions.Current;
            var revision = s.Revision;
            var unsaved = s.HasUnsavedChanges;
            await _engine.WaiveFindingAsync("test", Gross(first).DefId, null, "known rounding", "human");
            Assert.Equal(revision, s.Revision);
            Assert.Equal(unsaved, s.HasUnsavedChanges);
            Assert.False(await s.ReadAsync(m => ((TabularEditor.TOMWrapper.IAnnotationObject)m).HasAnnotation(Semanticus.Analysis.WaiverStore.Annotation)),
                "the model carries no waiver annotation");
            var file = Path.Combine(_ws, ".semanticus", "tests", "waivers.jsonl");
            Assert.True(File.Exists(file), "the record sits beside the checks' own suite.jsonl");
            Assert.True(File.Exists(Path.Combine(_ws, ".semanticus", "tests", "suite.jsonl")));
            // A reopen reads it again: a second engine on the same folder, with the same model, sees the check accepted.
            using var again = new SessionManager();
            var engine2 = new LocalEngine(again, new Pro(), _ws);
            await engine2.CreateModelAsync("Waive", 1604);
            again.Current.SourcePath = Path.Combine(_ws, "Waive.bim");
            var w = Assert.Single(await engine2.ListWaiversAsync(), x => x.System == "test");
            Assert.Equal("known rounding", w.Reason);
        }

        // A live model with no folder keeps its waivers in the per-user home, keyed by the model, so they still outlive the session.
        [Fact]
        public async Task With_no_folder_the_waivers_live_in_the_home_keyed_by_the_model()
        {
            var home = Path.Combine(Path.GetTempPath(), "sem-home-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var before = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, home);
            try
            {
                using var sessions = new SessionManager();
                var engine = new LocalEngine(sessions, new Pro(), null);
                await engine.CreateModelAsync("NoFolder", 1604);
                var dir = engine.CheckWaiverDirFor(sessions.Current);
                Assert.NotNull(dir);
                Assert.StartsWith(Path.Combine(home, "models"), dir);   // the model home (LocalEngine.ModelHome.cs)
                Assert.True(CheckWaiverStore.Upsert(dir, new Semanticus.Analysis.WaiverRecord { System = "test", RuleId = "abc", By = "human", When = "2026-10-18T00:00:00Z", Accepted = "d=1" }));
                Assert.Single(CheckWaiverStore.Load(dir));
            }
            finally
            {
                Environment.SetEnvironmentVariable(SemanticusHome.Variable, before);
                try { Directory.Delete(home, true); } catch { }
            }
        }

        // A waiver an earlier build wrote into the model's annotation is read into the store once, then never written there again.
        [Fact]
        public async Task A_waiver_an_earlier_build_left_in_the_model_is_read_once_and_the_model_is_not_written()
        {
            await SetUpAsync();
            var first = await _engine.RunTestSuiteAsync(false, "human");
            var o = Gross(first);
            var record = new Semanticus.Analysis.WaiverRecord
            {
                System = "test", RuleId = o.DefId, ObjectRef = CheckWaivers.RefOf(o.DefId), By = "human", When = "2026-10-18T00:00:00Z",
                Reason = "from the old build", Accepted = CheckWaivers.Signature(o),
            };
            await _sessions.Current.MutateAsync("human", "old build", m => Semanticus.Analysis.WaiverStore.Save(m, new System.Collections.Generic.List<Semanticus.Analysis.WaiverRecord> { record }));
            var revision = _sessions.Current.Revision;
            var run = await _engine.RunTestSuiteAsync(false, "human");
            Assert.NotNull(Gross(run).Waiver);
            Assert.Equal("from the old build", Gross(run).Waiver.Note);
            Assert.Equal(revision, _sessions.Current.Revision);
            await _engine.UnwaiveFindingAsync("test", o.DefId, null, "human");
            Assert.Null(Gross(await _engine.RunTestSuiteAsync(false, "human")).Waiver);   // not read in again after it was taken back
        }

        // The bulk door: waive_findings accepts system "test" with the check's id, no objRef, and narrates it as accepting a difference.
        [Fact]
        public async Task The_bulk_tool_accepts_a_check_too()
        {
            await SetUpAsync();
            var first = await McpToolsTesting.RunTests(_engine);
            var result = await McpTools.WaiveFindings(_engine, new[] { new FindingWaiverItem { System = "test", RuleId = Gross(first).DefId } });
            Assert.Equal(1, result.Applied);
            Assert.Empty(result.Rejected ?? Array.Empty<BatchRejection>());
            Assert.Equal("1 of 2 checks pass, 1 accepted.", (await McpToolsTesting.RunTests(_engine)).Headline);
            var net = first.Reconciles.Single(o => o.Title == "Net Sales ties to source");
            var again = await McpTools.WaiveFindings(_engine, new[] { new FindingWaiverItem { System = "test", RuleId = net.DefId } });
            Assert.Equal(0, again.Applied);
            Assert.Contains("nothing to accept", Assert.Single(again.Rejected).Reason);
        }

        // The assistant's waive of a check is narrated as accepting a difference, and the Studio follows to Validation (not Model quality).
        [Fact]
        public void The_assistants_waive_of_a_check_names_the_check_and_not_a_finding()
        {
            var args = System.Text.Json.JsonDocument.Parse("{\"system\":\"test\",\"ruleId\":\"c1\"}").RootElement.EnumerateObject()
                .Select(p => new System.Collections.Generic.KeyValuePair<string, System.Text.Json.JsonElement>(p.Name, p.Value));
            Assert.Equal("check:c1", McpHealthAppender.FocusOf(args));
        }

        // Verifier (v-fix-validation-waive): the tool description tells the assistant "no objRef, reason optional", but over the real MCP
        // door waive_finding answered "This action needs 'objRef'" and then "This action needs 'reason'", and unwaive_finding answered
        // "This action needs 'objRef'". The call only worked with explicit nulls. The unit tests call McpTools directly and miss the schema.
        [Theory]
        [InlineData("WaiveFinding", "objRef")]
        [InlineData("WaiveFinding", "reason")]
        [InlineData("UnwaiveFinding", "objRef")]
        public void The_assistant_can_leave_out_what_the_description_says_to_leave_out(string method, string parameter)
        {
            var p = typeof(McpTools).GetMethod(method).GetParameters().Single(x => x.Name == parameter);
            Assert.True(p.HasDefaultValue, $"{method}.{parameter} is required by the MCP schema, but the description says to leave it out for a check");
        }

        // ---- checks and waivers with no folder (verifier v2 of fix-validation-waive: lost work after Connect) ----------------------------------------
        // With no folder open the extension's engine workspace is only VS Code's start folder. The checks and the waiver used to be written there, so
        // Connect (which opens ~/Semanticus/<model>) showed "Start with a check". They are now kept in the per-user home keyed by the model, and move
        // into whichever folder Semanticus opens for it.

        // One model per test: the scratch SEMANTICUS_HOME is shared by the whole run, and the home copy is keyed by the model.
        private readonly string _liveEndpoint = "powerbi://api.powerbi.com/v1.0/myorg/Waive" + Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>The same published model on a fresh engine. <paramref name="seed"/> saves the two checks (the first launch); a later launch has none of its own.</summary>
        private async Task<(LocalEngine Engine, SessionManager Sessions)> OpenPublishedAsync(string workspace, bool workspaceIsFolder, bool seed)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Pro(), workspace, workspaceIsFolder);
            await engine.CreateModelAsync("Waive", 1604);
            var sales = await McpTools.CreateTable(engine, "Sales");
            await McpTools.CreateMeasure(engine, sales, "Net Sales", "75249457.11");
            await McpTools.CreateMeasure(engine, sales, "Gross Sales", "75249457.11");
            sessions.Current.LiveOrigin = new LiveOrigin(_liveEndpoint, "Waive", null, "interactive");
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", _liveEndpoint, "Waive",
                _ => new ResultSet { Columns = new[] { new ColumnDef { Name = "v" } }, Rows = new[] { new object[] { 75249457.11m } }, RowCount = 1 }));
            engine.ReconcileSqlForTests = (server, database, sql) => new ResultSet
            {
                Columns = new[] { new ColumnDef { Name = "v" } },
                Rows = new[] { new object[] { sql.Contains("GrossSales") ? _offSource : _okSource } }, RowCount = 1,
            };
            if (seed)
                foreach (var name in new[] { "Net Sales", "Gross Sales" })
                    await engine.SaveTestDefinitionAsync(new TestDefinition
                    {
                        Kind = TestKinds.MeasureReconcile, Title = name + " ties to source", TargetRef = "measure:Sales/" + name, Enabled = true,
                        ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                        {
                            MeasureRef = "measure:Sales/" + name, Sql = "SELECT SUM(" + name.Replace(" ", "") + ") FROM dbo.Sales",
                            Server = "contoso-sql.database.windows.net", Database = "Warehouse", BlankPolicy = "zero",
                        }),
                    }, "human");
            return (engine, sessions);
        }

        [Fact]
        public async Task With_no_folder_the_checks_and_the_waiver_follow_the_model_into_the_folder_Connect_opens()
        {
            var root = Path.Combine(Path.GetTempPath(), "sem-nofolder-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var start = Path.Combine(root, "start"); var connect = Path.Combine(root, "Semanticus", "Waive"); var byHand = Path.Combine(root, "elsewhere");
            Directory.CreateDirectory(start); Directory.CreateDirectory(connect); Directory.CreateDirectory(byHand);
            try
            {
                // 1. No folder: make two checks and a waiver. Nothing is written into the window's start folder.
                var (first, firstSessions) = await OpenPublishedAsync(start, false, seed: true);
                using (firstSessions)
                {
                    var run = await first.RunTestSuiteAsync(false, "human");
                    await first.WaiveFindingAsync("test", Gross(run).DefId, null, "known rounding", "human");
                    Assert.False(Directory.Exists(Path.Combine(start, ".semanticus")), "the start folder is not where the person's checks are kept");
                }

                // 2. A window reload, still no folder: the same checks and the waiver are there.
                var (reload, reloadSessions) = await OpenPublishedAsync(start, false, seed: false);
                using (reloadSessions)
                {
                    Assert.Equal(2, (await reload.ListTestDefinitionsAsync()).Definitions.Length);
                    Assert.Single(await reload.ListWaiversAsync(), w => w.System == "test");
                }

                // 3. Connect opens the folder: the checks and the waiver are there, and now sit in the folder's own .semanticus.
                var (opened, openedSessions) = await OpenPublishedAsync(connect, true, seed: false);
                using (openedSessions)
                {
                    Assert.Equal(2, (await opened.ListTestDefinitionsAsync()).Definitions.Length);
                    // (A model made fresh in a test has new lineage tags, so a run here would call the checks vanished. The record is what carries over.)
                    Assert.Equal("known rounding", Assert.Single(await opened.ListWaiversAsync(), w => w.System == "test").Reason);
                    Assert.True(File.Exists(Path.Combine(connect, ".semanticus", "tests", "suite.jsonl")));
                    Assert.True(File.Exists(Path.Combine(connect, ".semanticus", "tests", "waivers.jsonl")));
                }

                // 4. The folder is opened again (reload): still there, and not doubled by the move.
                var (again, againSessions) = await OpenPublishedAsync(connect, true, seed: false);
                using (againSessions)
                {
                    Assert.Equal(2, (await again.ListTestDefinitionsAsync()).Definitions.Length);
                    Assert.Single(await again.ListWaiversAsync(), w => w.System == "test");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // fix-model-home: the class behind three rounds. Every per-model store (checks, waivers, the plan, Query rows, analysis records) answers
        // "where" through one resolver, so a published model's work is still there through every form it takes: no folder, Ctrl+S (it becomes a
        // file), a reload (the file opens with no live link), Connect (the file's folder opens), File > Open Folder by hand (the published model
        // opens live in another folder), and reopening the file from Recent.
        [Fact]
        public async Task Every_store_keeps_its_items_through_save_reload_connect_a_folder_by_hand_and_recent()
        {
            var root = Path.Combine(Path.GetTempPath(), "sem-modelhome-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var start = Path.Combine(root, "start"); var lasting = Path.Combine(root, "Semanticus", "Waive"); var byHand = Path.Combine(root, "elsewhere");
            var file = Path.Combine(lasting, "Waive.bim");
            Directory.CreateDirectory(start); Directory.CreateDirectory(lasting); Directory.CreateDirectory(byHand);
            string pinned = null, planRow = null;
            Task AllThere(LocalEngine e, SessionManager s, string step) => EveryItemThereAsync(e, s, pinned, planRow, step);
            async Task<(LocalEngine, SessionManager)> OpenFileAsync(string workspace, bool isFolder)
            {
                var sessions = new SessionManager();
                var engine = new LocalEngine(sessions, new Pro(), workspace, isFolder);
                await engine.OpenAsync(file);
                Assert.Null(sessions.Current.LiveOrigin);
                return (engine, sessions);
            }
            try
            {
                // 1. No folder: two checks, a waiver, a pinned Query row, Who does it > Me, and an analysis record.
                var (first, firstSessions) = await OpenPublishedAsync(start, false, seed: true);
                using (firstSessions)
                {
                    (pinned, planRow) = await MakeEveryItemAsync(first, firstSessions);
                    await AllThere(first, firstSessions, "no folder");

                    // 2. Ctrl+S: the published model becomes a lasting file.
                    await first.SaveAsync(file, "BIM", false, "human");
                    await AllThere(first, firstSessions, "after Ctrl+S");
                }
                // Nothing went into the window's start folder (the process's working folder): no store resolves there.
                Assert.False(Directory.Exists(Path.Combine(start, ".semanticus")), "the start folder is not where anyone's work is kept");

                // 3. Reload: the window opens the file, with no live link.
                var (reload, reloadSessions) = await OpenFileAsync(start, false);
                using (reloadSessions) await AllThere(reload, reloadSessions, "after a reload");

                // 4. Connect: the file's own folder opens.
                var (connect, connectSessions) = await OpenFileAsync(lasting, true);
                using (connectSessions) await AllThere(connect, connectSessions, "after Connect");

                // 5. File > Open Folder by hand: another folder, and the published model opened live there.
                var (byHandLive, byHandSessions) = await OpenPublishedAsync(byHand, true, seed: false);
                using (byHandSessions) await AllThere(byHandLive, byHandSessions, "in a folder opened by hand");
                Assert.False(Directory.Exists(Path.Combine(byHand, ".semanticus", "tests")), "the work stays in one place, not copied into every folder");

                // 6. Reopen from Recent: the file again, in that folder.
                var (recent, recentSessions) = await OpenFileAsync(byHand, true);
                using (recentSessions) await AllThere(recent, recentSessions, "reopened from Recent");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // UAT round 2, N1 (the same class, a second trigger): with no folder, checks were made, then the Studio's Connect your assistant made a
        // folder (~/Semanticus/<model>) and reloaded the window. The model reopened live there and Validation said "Start with a check": the
        // checks sat in .semanticus under the process's working folder. Every item must be there after Connect and after a reopen, and the
        // start folder must hold nothing.
        [Fact]
        public async Task Every_store_keeps_its_items_when_Connect_your_assistant_makes_the_folder_with_no_save()
        {
            var root = Path.Combine(Path.GetTempPath(), "sem-connect-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var start = Path.Combine(root, "start"); var connect = Path.Combine(root, "Semanticus", "Waive");
            Directory.CreateDirectory(start); Directory.CreateDirectory(connect);
            try
            {
                string pinned, planRow;
                var (first, firstSessions) = await OpenPublishedAsync(start, false, seed: true);
                using (firstSessions)
                {
                    (pinned, planRow) = await MakeEveryItemAsync(first, firstSessions);
                    await EveryItemThereAsync(first, firstSessions, pinned, planRow, "no folder");
                }
                Assert.False(Directory.Exists(Path.Combine(start, ".semanticus")), "the start folder is not where anyone's work is kept");

                // Connect your assistant: the folder it made opens, and the published model reopens live in it.
                var (connected, connectedSessions) = await OpenPublishedAsync(connect, true, seed: false);
                using (connectedSessions) await EveryItemThereAsync(connected, connectedSessions, pinned, planRow, "after Connect your assistant");
                Assert.True(File.Exists(Path.Combine(connect, ".semanticus", "tests", "suite.jsonl")), "the checks now sit in the folder Connect made");

                // Reopen (a later window on the same folder).
                var (again, againSessions) = await OpenPublishedAsync(connect, true, seed: false);
                using (againSessions) await EveryItemThereAsync(again, againSessions, pinned, planRow, "reopened");
                Assert.False(Directory.Exists(Path.Combine(start, ".semanticus")), "and still nothing in the start folder");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>Every per-model store gets one item: a waiver on the differing check (the two checks are seeded), a pinned Query row,
        /// Who does it > Me on a Plan row, and an analysis record.</summary>
        private async Task<(string Pinned, string PlanRow)> MakeEveryItemAsync(LocalEngine e, SessionManager s)
        {
            var run = await e.RunTestSuiteAsync(false, "human");
            await e.WaiveFindingAsync("test", Gross(run).DefId, null, "known rounding", "human");
            var pinned = (await e.AskDataAsync(new Semanticus.Engine.Query.DataAsk { Measures = new[] { "Net Sales" } })).QuestionId;
            Assert.False(string.IsNullOrEmpty(pinned));
            await e.QueryNotebookAsync("pin", pinned);
            var planRow = (await e.GetModelPlanAsync()).Items.First(i => i.Who == ModelPlanOwners.Assistant && !PlanTasks.IsTaskId(i.Id) && PlanStateWords.IsOpen(i.StateWord)).Id;
            await e.SetPlanItemDoerAsync(planRow, PlanDoers.You, "human");
            await e.RecordAnalysisForTest(new VerifiedEditRecord { SessionId = s.Current.Id, Origin = "human", Op = "explain_value", ObjectRef = "measure:Sales/Net Sales", Verdict = "explained" });
            return (pinned, planRow);
        }

        private static async Task EveryItemThereAsync(LocalEngine e, SessionManager s, string pinned, string planRow, string step)
        {
            Assert.True(2 == (await e.ListTestDefinitionsAsync()).Definitions.Length, step + ": both checks");
            Assert.True((await e.ListWaiversAsync()).Count(w => w.System == "test" && w.Reason == "known rounding") == 1, step + ": the waiver");
            Assert.True((await e.QueryNotebookAsync()).Questions.Any(q => q.Id == pinned && q.Pinned), step + ": the pinned Query row");
            Assert.True((await e.GetModelPlanAsync()).Items.Any(i => i.Id == planRow && i.Doer == PlanDoers.You), step + ": Who does it > Me");
            Assert.True(e.AnalysisRecordsFor(s.Current).Any(r => r.Op == "explain_value"), step + ": the analysis record");
        }

        // fix-model-home: a published model's work has one place. A folder that holds older checks of its own for the same model (made there before
        // the model had a place) is not hidden behind that place: its checks for this model join it, and its other models' checks stay where they are.
        [Fact]
        public async Task A_folder_with_older_checks_for_a_model_that_has_its_place_elsewhere_shows_them_too()
        {
            var root = Path.Combine(Path.GetTempPath(), "sem-merge-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var older = Path.Combine(root, "older"); var place = Path.Combine(root, "place", ".semanticus");
            Directory.CreateDirectory(older); Directory.CreateDirectory(place);
            try
            {
                string identity;
                var (first, firstSessions) = await OpenPublishedAsync(older, true, seed: true);
                using (firstSessions) identity = first.PlaceFor(firstSessions.Current).Identity;
                ModelHome.SetPlace(identity, place);   // the model's place is now another folder (it was saved there, say)

                var (again, againSessions) = await OpenPublishedAsync(older, true, seed: false);
                using (againSessions)
                {
                    Assert.Equal(Path.GetFullPath(place), Path.GetFullPath(again.PlaceFor(againSessions.Current).Dir));
                    Assert.Equal(2, (await again.ListTestDefinitionsAsync()).Definitions.Length);
                }
                Assert.Equal(2, TestSuiteStore.LoadSuite(Path.Combine(older, ".semanticus")).Defs.Count);   // copied, not taken: the folder is left as it was
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public async Task A_folder_opened_by_hand_finds_the_checks_made_with_no_folder_for_that_model()
        {
            var root = Path.Combine(Path.GetTempPath(), "sem-byhand-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var start = Path.Combine(root, "start"); var byHand = Path.Combine(root, "elsewhere");
            Directory.CreateDirectory(start); Directory.CreateDirectory(byHand);
            try
            {
                var (first, firstSessions) = await OpenPublishedAsync(start, false, seed: true);
                using (firstSessions) await first.RunTestSuiteAsync(false, "human");
                var (opened, openedSessions) = await OpenPublishedAsync(byHand, true, seed: false);
                using (openedSessions) Assert.Equal(2, (await opened.ListTestDefinitionsAsync()).Definitions.Length);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // An earlier build wrote the checks into the start folder. Open with no folder, they are read once into the home, for this model only.
        [Fact]
        public async Task Checks_an_earlier_build_left_in_the_start_folder_are_read_once_for_their_own_model()
        {
            var root = Path.Combine(Path.GetTempPath(), "sem-legacy-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var start = Path.Combine(root, "start");
            Directory.CreateDirectory(start);
            try
            {
                // The earlier build: the same engine, but the start folder is the workspace (a folder).
                var (old, oldSessions) = await OpenPublishedAsync(start, true, seed: true);
                using (oldSessions)
                {
                    var run = await old.RunTestSuiteAsync(false, "human");
                    await old.WaiveFindingAsync("test", Gross(run).DefId, null, null, "human");
                }
                // A definition for some other model sits in the same file: it is not this model's.
                TestSuiteStore.Upsert(Path.Combine(start, ".semanticus"), new TestDefinition { Title = "Other model check", Kind = TestKinds.MeasureValue, ModelIdentity = "live:somewhere-else", Enabled = true,
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = "measure:Sales/Net Sales", ExpectedValue = "1" }) }, () => "other");
                var (noFolder, noFolderSessions) = await OpenPublishedAsync(start, false, seed: false);
                using (noFolderSessions)
                {
                    var defs = (await noFolder.ListTestDefinitionsAsync()).Definitions;
                    Assert.Equal(2, defs.Length);
                    Assert.DoesNotContain(defs, d => d.Title == "Other model check");
                    Assert.Single(await noFolder.ListWaiversAsync(), w => w.System == "test");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // Verifier (fix-validation-waive v2): an empty Ctrl+Z still said "Undone" and marked the results "The model changed", because the undo was
        // published as a change when there was nothing to take back. Nothing to undo or redo is no change and no message.
        [Fact]
        public async Task An_undo_or_redo_with_nothing_to_take_back_changes_nothing_and_publishes_nothing()
        {
            await _engine.CreateModelAsync("Empty", 1604);
            var s = _sessions.Current;
            Assert.False(s.UndoStateNow().CanUndo);
            var revision = s.Revision;
            var published = 0;
            _sessions.Bus.Changed += _ => published++;
            var undone = await _engine.UndoAsync("human");
            var redone = await _engine.RedoAsync("human");
            Assert.Equal(0, published);
            Assert.Equal(revision, s.Revision);
            Assert.False(undone.CanUndo);
            Assert.False(redone.CanRedo);
        }

        // Verifier v3 (fix-validation-waive): with no folder the checks and the waiver wait in the home. Ctrl+S on the published model writes a
        // lasting file (~/Semanticus/<model>/<model>.bim); after a reload the open model is that file, and the checks are looked for beside it only
        // (TestsDirFor's anchored branch never takes the home copy). In the real window Validation then said "Nothing needs a look". Here the model
        // becomes that file within one session, which reaches the same branch.
        [Fact]
        public async Task V3_with_no_folder_a_save_keeps_the_checks_and_the_waiver_made_before_it()
        {
            var root = Path.Combine(Path.GetTempPath(), "sem-v3save-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var start = Path.Combine(root, "start"); var lasting = Path.Combine(root, "Semanticus", "Waive");
            Directory.CreateDirectory(start); Directory.CreateDirectory(lasting);
            try
            {
                var (engine, sessions) = await OpenPublishedAsync(start, false, seed: true);
                using (sessions)
                {
                    var run = await engine.RunTestSuiteAsync(false, "human");
                    await engine.WaiveFindingAsync("test", Gross(run).DefId, null, "known rounding", "human");
                    Assert.Equal(2, (await engine.ListTestDefinitionsAsync()).Definitions.Length);
                    await engine.SaveAsync(Path.Combine(lasting, "Waive.bim"), "BIM", false, "human");
                    Assert.False(ExperienceStore.IsEphemeralAnchor(sessions.Current.SourcePath));
                    Assert.Equal(2, (await engine.ListTestDefinitionsAsync()).Definitions.Length);
                    Assert.Single(await engine.ListWaiversAsync(), w => w.System == "test");
                }
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public async Task A_waiver_is_listed_with_who_and_when_for_the_audit()
        {
            await SetUpAsync();
            var first = await _engine.RunTestSuiteAsync(false, "human");
            await _engine.WaiveFindingAsync("test", Gross(first).DefId, null, "rounding", "agent");
            var w = Assert.Single(await _engine.ListWaiversAsync(), x => x.System == "test");
            Assert.Equal(Gross(first).DefId, w.RuleId);
            Assert.Equal("agent", w.By);
            Assert.Equal("rounding", w.Reason);
        }
    }
}
