// Join part 8, the items found at the join. Each test here failed first on the merged tree (m11/join after the four lanes, before the fix).
//
// K2. A stored start plan that says "deciding" after an engine restart stayed stuck: the new engine had no decide loop, nothing kicked one (no verb, no unpark),
// and Approve stayed greyed for ever (plan statement check 3, note 1). A read of a plan that says "deciding" while this engine never started a decide for it
// now says it stopped, with the same way on that the person already has (any edit, any words, or Approve).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Facts;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class Join8StoppedPlanTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _origHome;
        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public Join8StoppedPlanTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "j8s-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            Directory.CreateDirectory(Path.Combine(_root, "policy"));
            _origHome = Environment.GetEnvironmentVariable(SemanticusHome.Variable);
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, Path.Combine(_root, "home"));
            ConnectionRegistry.RootOverride = Path.Combine(_root, "user");
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
        }

        public void Dispose()
        {
            foreach (var o in _owned) { try { o.Dispose(); } catch { } }
            Environment.SetEnvironmentVariable(SemanticusHome.Variable, _origHome);
            ConnectionRegistry.RootOverride = _safeRoot; AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private LocalEngine NewProEngine(StartPlanDecideTests.PolicyOracle policy)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, Path.Combine(_root, "ws"));
            _owned.Add(engine); _owned.Add(sessions);
            engine.JevBatchedForTest = _ => true;
            engine.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            engine.StartPlanSchemaReaderForTest = (req, origin) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            engine.UseDecisionsForTest(new DecisionService(policy));
            return engine;
        }

        /// <summary>A Pro plan whose decide is running (held on a slow answer), then the engine that held it is stopped. What is on disk says "deciding".
        /// Returns the folder of the model, which a new engine opens at once (the model is open, so nothing parks the plan and nothing unparks it).</summary>
        private async Task<string> StoredDecidingPlanAsync()
        {
            var policy = new StartPlanDecideTests.PolicyOracle();
            var first = NewProEngine(policy);
            var modelDir = Path.Combine(_root, "model");
            var view = await first.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "wh-j8.datawarehouse.fabric.microsoft.com", Database = "ContosoConsulting", Folder = modelDir, Name = "Uplift",
            }, "human");
            Assert.True(view.Exists);
            await first.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            var hold = new SemaphoreSlim(0);
            policy.BeforeAnswer = async (req, ct) => await hold.WaitAsync(TimeSpan.FromSeconds(30), ct);
            await first.GrantStartDisclosureAsync((await first.GetStartPlanAsync()).Digest, "human");
            await Task.Delay(300);
            Assert.True(first.StartPlanDocForTest().Decide.State == StartDecideState.Deciding && first.StartDecideAliveForTest,
                "precondition: the decide is running (" + first.StartPlanDocForTest().Decide.State + ")");
            // The engine stops while the decide runs: its loop ends with it, and the plan on disk still says deciding.
            first.Dispose();
            hold.Release(1000);
            return modelDir;
        }

        [Fact]
        public async Task A_stored_plan_that_says_deciding_after_a_restart_reads_as_stopped_and_can_be_approved_on_both_doors()
        {
            var modelDir = await StoredDecidingPlanAsync();

            var second = NewProEngine(new StartPlanDecideTests.PolicyOracle());
            await second.OpenAsync(modelDir);   // the model is open before anything reads the plan: no park, no unpark, no kick
            Assert.False(second.StartDecideAliveForTest, "precondition: the new engine runs no decide");

            var viaMcp = await McpTools.GetStartPlan(second);
            Assert.True(viaMcp.Exists);
            Assert.Equal(StartDecideState.Incomplete, viaMcp.Decide.State);
            Assert.Contains("stopped", viaMcp.Decide.Reason ?? "", StringComparison.OrdinalIgnoreCase);

            var viaRpc = await new EngineRpcTarget(second).getStartPlan();
            Assert.Equal(StartDecideState.Incomplete, viaRpc.Decide.State);
            Assert.Equal(viaMcp.Decide.Reason, viaRpc.Decide.Reason);

            // The same way on the person already has: nothing is greyed for the reason "Jev is still working on the plan".
            Assert.DoesNotContain("still working", viaRpc.BlockedBecause ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(StartDecideState.Deciding, second.StartPlanDocForTest().Decide.State);
        }
    }

    /// <summary>
    /// K3. Free start proposed "Total OrderLine" (a line number) and "Total IsReturn" (a 0/1 flag) on the Northstar shape: a total of either means nothing. No path
    /// totals a flag or a line number now (the rule a Pro plan already had, ColumnSummarizeFamily.FlagOrOrdinal, is in the
    /// starter rule every path reads). No existing rule counts the true rows of a flag, so a flag gets no measure. The Northstar shape's base measures were 6 and are 4.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Join8FlagAndLineNumberTests : GateFixtureBase
    {
        private const string Server = "ns.datawarehouse.fabric.microsoft.com", Db = "Northstar Source";
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public Join8FlagAndLineNumberTests(RestoreRootFixture fixture) : base(fixture)
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

        [Theory]
        [InlineData("OrderLine")]
        [InlineData("IsReturn")]
        [InlineData("IsWeekend")]
        [InlineData("Has Discount")]
        [InlineData("Weekend Flag")]
        [InlineData("Invoice_Line")]
        public void The_starter_rule_gives_a_flag_or_a_line_number_no_total(string column)
        {
            Assert.Equal(StarterMeasureTemplates.None, StarterMeasureTemplates.RuleChoice(column, false, "none", "Default"));
        }

        [Theory]
        [InlineData("Quantity")]
        [InlineData("NetSalesAmount")]
        [InlineData("DiscountAmount")]
        [InlineData("Line Total")]
        [InlineData("Island Sales")]
        public void The_starter_rule_still_totals_an_amount(string column)
        {
            Assert.Equal(StarterMeasureTemplates.Sum, StarterMeasureTemplates.RuleChoice(column, false, "none", "Default"));
        }

        [Theory]
        [InlineData("CustomerKeyOld")]
        [InlineData("ProductId_Prev")]
        [InlineData("OrderNo2")]
        [InlineData("CustomerID")]
        [InlineData("SalesKey_New")]
        public void The_starter_rule_gives_a_key_with_or_without_a_copy_suffix_no_total(string column)
        {
            Assert.Equal(StarterMeasureTemplates.None, StarterMeasureTemplates.RuleChoice(column, false, "none", "Default"));
        }

        [Theory]
        [InlineData("Key Account Revenue")]
        [InlineData("Old Balance")]
        [InlineData("Quantity2")]
        public void A_name_that_only_contains_a_key_word_or_a_copy_word_is_still_totalled(string column)
        {
            Assert.Equal(StarterMeasureTemplates.Sum, StarterMeasureTemplates.RuleChoice(column, false, "none", "Default"));
        }

        [Fact]
        public void A_fact_with_a_CustomerKeyOld_column_gets_no_total_of_it()
        {
            var schema = StartLinkGuessTests.Northstar();
            var sales = schema.Tables.Single(t => t.Name == "Sales");
            sales.Columns = sales.Columns.Concat(new[] { new FabricColumn { Name = "CustomerKeyOld", DataType = "Int64" } }).ToArray();
            var m = StructureManifest.Derive(schema, null, null, "seed", "x", "DW", "import", LinkGuessFacts.NamesOnly());
            Assert.DoesNotContain(m.BaseMeasures, b => b.Column == "CustomerKeyOld");
            Assert.Contains(m.BaseMeasures, b => b.Column == "Quantity");
        }

        [Fact]
        public void The_Northstar_shape_has_four_base_measures_and_no_total_of_the_line_number_or_the_flag()
        {
            var m = StructureManifest.Derive(StartLinkGuessTests.Northstar(), null, null, "seed", "x", "DW", "import", LinkGuessFacts.NamesOnly());
            var names = m.BaseMeasures.Select(b => b.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "Total CostAmount", "Total DiscountAmount", "Total NetSalesAmount", "Total Quantity" }, names);
            Assert.Equal(4, m.BaseMeasures.Count);
            // Six time measures per base measure, as before: the count moves with the base measures and no more.
            Assert.Equal(6 * m.BaseMeasures.Count, m.TimeMeasures().Count());
        }

        [Theory]
        [InlineData("ui")]
        [InlineData("mcp")]
        public async Task The_plan_either_door_reads_lists_no_total_of_the_line_number_or_the_flag(string door)
        {
            using var sm = new SessionManager();
            using var e = new LocalEngine(sm, TestEntitlements.Free, Path.Combine(Root, "ws"));
            var schema = StartLinkGuessTests.Northstar(); schema.Server = Server; schema.Database = Db;
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(schema);
            e.UseDecisionsForTest(new DecisionService(new NoAnswer()));
            if (door == "mcp") await McpTools.StartModelFromSql(e, Server, Db, Path.Combine(Root, "m"), "Northstar");
            else await e.StartModelFromSqlAsync(new StartModelRequest { Name = "Northstar", Folder = Path.Combine(Root, "m"), Server = Server, Database = Db }, "human");
            await e.StartDecideSettledForTest(); await e.StartPreviewSettledForTest();
            var view = door == "mcp" ? await McpTools.GetStartPlan(e) : await e.GetStartPlanAsync();
            var measures = view.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Lines)
                .Where(l => l.Item.Kind == "create_measure").Select(l => l.Item.ObjectName ?? "").ToList();
            Assert.Contains(measures, n => n == "Total Quantity");
            Assert.DoesNotContain(measures, n => n.Contains("OrderLine", StringComparison.Ordinal));
            Assert.DoesNotContain(measures, n => n.Contains("IsReturn", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// K4. A History row from an earlier session, not undone, whose edit was never saved, read as an ordinary active row although the file lacks the edit (the
    /// reopened model does not have it). It now says what is true: the edit was not saved. Both doors read the one answer (GetPlanHistoryAsync).
    /// </summary>
    public sealed class Join8NotSavedHistoryTests
    {
        private static string WriteSeed(string dir)
        {
            var db = new TOM.Database("NotSaved") { CompatibilityLevel = 1600, Model = new TOM.Model() };
            var sales = new TOM.Table { Name = "Sales", LineageTag = "tag-sales" };
            sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-amount" });
            sales.Measures.Add(new TOM.Measure { Name = "M1", Expression = "SUM(Sales[Amount])", LineageTag = "m-1" });
            db.Model.Tables.Add(sales);
            var path = Path.Combine(dir, "model.bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        private static async Task<(PlanHistoryRow Row, string Description)> EditThenRestartAsync(bool save)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ns-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var model = WriteSeed(dir);
                var first = new SessionManager();
                var engine = new LocalEngine(first, TestEntitlements.Pro);
                await engine.OpenAsync(model);
                await engine.SetDescriptionAsync("measure:Sales/M1", "words that were not saved", "human");
                if (save) await engine.SaveAsync(null!, null!, true, "human");
                await first.Ledger.FlushAsync();
                engine.Dispose();

                var again = new SessionManager();
                using var next = new LocalEngine(again, TestEntitlements.Pro);
                await next.OpenAsync(model);
                var row = Assert.Single((await next.GetPlanHistoryAsync()).Rows.Where(r => r.Kind == "edit"));
                var held = await again.Current.ReadAsync(m => m.Tables["Sales"].Measures["M1"].Description ?? "");
                return (row, held);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public async Task An_edit_of_an_earlier_session_that_was_never_saved_says_so()
        {
            var (row, held) = await EditThenRestartAsync(save: false);
            Assert.True(row.BeforeThisSession);
            Assert.Equal("", held);                       // the reopened file lacks the edit
            Assert.False(row.Undone);                     // it was not taken back: it was lost
            Assert.True(row.NotSaved, "the row reads as an ordinary active edit although the file lacks it");
        }

        [Fact]
        public async Task An_edit_of_an_earlier_session_that_was_saved_does_not_say_it_was_not_saved()
        {
            var (row, held) = await EditThenRestartAsync(save: true);
            Assert.Equal("words that were not saved", held);
            Assert.False(row.NotSaved);
        }

        private static string PrimerWith(string overview) =>
            PrimerContract.Template("NotSaved").Replace("## Overview\n\n_Add what people and the AI Assistant should know._", "## Overview\n\n" + overview);

        [Fact]
        public async Task A_Primer_edit_taken_back_after_a_model_save_still_reads_undone_after_a_restart()
        {
            // The Primer is its own file, written the moment it is edited or taken back, so a save of the MODEL's file says nothing about it (Sol, join 8, A2 finding 3).
            var dir = Path.Combine(Path.GetTempPath(), "ns-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var model = WriteSeed(dir);
                var first = new SessionManager();
                var engine = new LocalEngine(first, TestEntitlements.Pro);
                await engine.OpenAsync(model);
                await engine.SetPrimerAsync(PrimerWith("Words A."), "human");
                await engine.SaveAsync(null!, null!, true, "human");
                await engine.SetPrimerAsync(PrimerWith("Words B."), "human");
                await engine.SaveAsync(null!, null!, true, "human");
                await engine.UndoAsync("human");
                await first.Ledger.FlushAsync();
                engine.Dispose();

                var again = new SessionManager();
                using var next = new LocalEngine(again, TestEntitlements.Pro);
                await next.OpenAsync(model);
                var primerRows = (await next.GetPlanHistoryAsync()).Rows.Where(r => r.Kind == "edit" && (r.Deltas ?? System.Array.Empty<ChangeDelta>()).Any(d => d.Ref == "primer")).OrderBy(r => r.When).ToList();
                Assert.Equal(2, primerRows.Count);
                Assert.False(primerRows[0].Undone);
                Assert.True(primerRows[1].Undone, "the Primer edit that was taken back reads as active after a restart");
                Assert.False(primerRows[1].NotSaved);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public async Task An_edit_taken_back_then_saved_then_brought_back_and_never_saved_again_says_it_was_not_saved_after_a_restart()
        {
            // K12 (the uat7-engine check): undo, save, redo, then restart with no save. The save wrote the file without the edit and the redo brought it back only in the open model.
            var dir = Path.Combine(Path.GetTempPath(), "ns-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var model = WriteSeed(dir);
                var first = new SessionManager();
                var engine = new LocalEngine(first, TestEntitlements.Pro);
                await engine.OpenAsync(model);
                await engine.SetDescriptionAsync("measure:Sales/M1", "words brought back but never saved", "human");
                await engine.UndoAsync("human");
                await engine.SaveAsync(null!, null!, true, "human");
                await engine.RedoAsync("human");
                await first.Ledger.FlushAsync();
                engine.Dispose();

                var again = new SessionManager();
                using var next = new LocalEngine(again, TestEntitlements.Pro);
                await next.OpenAsync(model);
                var row = Assert.Single((await next.GetPlanHistoryAsync()).Rows.Where(r => r.Kind == "edit"));
                Assert.Equal("", await again.Current.ReadAsync(m => m.Tables["Sales"].Measures["M1"].Description ?? ""));
                Assert.False(row.Undone);
                Assert.True(row.NotSaved, "the row reads as an active edit although the file lacks it");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public async Task An_edit_made_in_this_session_and_not_yet_saved_is_not_called_lost()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ns-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(WriteSeed(dir));
                await engine.SetDescriptionAsync("measure:Sales/M1", "still in the open model", "human");
                var row = Assert.Single((await engine.GetPlanHistoryAsync()).Rows.Where(r => r.Kind == "edit"));
                Assert.False(row.BeforeThisSession);
                Assert.False(row.NotSaved);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    /// <summary>
    /// K1. The person's keep of a flagged name (write_start_words kind keep_name, psc3) had no Studio control. The view now says which names are flagged (StartLine.NameFlag
    /// on a table or base measure line, StartColumn.NameFlag on a column), so the page draws "Keep this name" only there; the keep is the one engine call both doors have, and a
    /// kept name is not flagged any more. Failed first: the contract had no NameFlag and the view never set one.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Join8KeepNameTests : GateFixtureBase
    {
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public Join8KeepNameTests(RestoreRootFixture fixture) : base(fixture)
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

        private async Task<LocalEngine> FreeEngineAsync(SessionManager sm)
        {
            var e = new LocalEngine(sm, TestEntitlements.Free, Path.Combine(Root, "ws"));
            e.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            e.UseDecisionsForTest(new DecisionService(new NoAnswer()));
            var view = await e.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "wh-j8.datawarehouse.fabric.microsoft.com", Database = "ContosoConsulting", Folder = Path.Combine(Root, "m"), Name = "Uplift",
            }, "human");
            Assert.True(view.Exists);
            await e.StartPreviewSettledForTest();
            return e;
        }

        private static IEnumerable<StartLine> LinesOf(StartPlanView v) => v.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Lines);
        private static StartLine TableLine(StartPlanView v, string table) => LinesOf(v).Single(l => l.Item.Kind == "include_table" && l.Item.ObjectName == table);
        private static StartColumn Column(StartPlanView v, string table, string column) =>
            v.Sections.SelectMany(s => s.Groups).Where(g => g.Table == table).SelectMany(g => g.Columns ?? new List<StartColumn>()).Single(c => c.Name == column);

        [Fact]
        public async Task A_table_or_a_column_with_a_name_the_rules_flag_carries_the_reason_and_a_clean_name_carries_none()
        {
            using var sm = new SessionManager(); using var e = await FreeEngineAsync(sm);
            var view = await e.GetStartPlanAsync();
            Assert.False(string.IsNullOrWhiteSpace(TableLine(view, "dim_client").NameFlag), "dim_client reads as a code and carries no reason");
            Assert.False(string.IsNullOrWhiteSpace(Column(view, "dim_client", "CLNT_NM").NameFlag), "CLNT_NM reads as a code and carries no reason");
            // A person's rename to a plain name settles it the other way: nothing is flagged any more, and no control is offered.
            view = await e.EditStartPlanAsync(new StartPlanEdit { Op = "rename", Target = "table:dim_client", Value = "Client" }, "human");
            Assert.Null(TableLine(view, "Client").NameFlag);
        }

        [Fact]
        public async Task The_person_keeps_a_flagged_table_name_and_a_flagged_column_name_through_the_human_door_and_the_flag_goes()
        {
            using var sm = new SessionManager(); using var e = await FreeEngineAsync(sm);
            var result = await e.WriteStartWordsAsync(new[]
            {
                new StartWord { Ref = "table:dim_client", Kind = "keep_name", Text = "" },
                new StartWord { Ref = "column:dim_client/CLNT_NM", Kind = "keep_name", Text = "" },
            }, "human");
            Assert.True(result.Written == 2, "refused: " + string.Join("; ", result.Refused));
            var view = await e.GetStartPlanAsync();
            Assert.Null(TableLine(view, "dim_client").NameFlag);
            Assert.Null(Column(view, "dim_client", "CLNT_NM").NameFlag);
            // The keep is the person's: a line of their own, so the assistant's later rename does not take it back.
            var keeps = LinesOf(view).Where(l => l.Item.Kind == "keep_name").ToList();
            Assert.Equal(2, keeps.Count);
            Assert.All(keeps, k => Assert.Equal("person", k.DecidedBy));
            // And a neighbour that nobody kept is still flagged.
            Assert.False(string.IsNullOrWhiteSpace(Column(view, "dim_client", "IND_CD").NameFlag));
        }

        [Fact]
        public async Task The_assistants_keep_over_the_agent_door_clears_the_same_flag()
        {
            using var sm = new SessionManager(); using var e = await FreeEngineAsync(sm);
            Assert.False(string.IsNullOrWhiteSpace(Column(await McpTools.GetStartPlan(e), "dim_client", "CLNT_NM").NameFlag), "precondition: the name is flagged before anyone keeps it");
            var result = await McpTools.WriteStartWords(e, new[] { new StartWord { Ref = "column:dim_client/CLNT_NM", Kind = "keep_name", Text = "" } });
            Assert.True(result.Written == 1, "refused: " + string.Join("; ", result.Refused));
            var view = await McpTools.GetStartPlan(e);
            Assert.Null(Column(view, "dim_client", "CLNT_NM").NameFlag);
        }
    }

    /// <summary>
    /// K6 (Sol, join 8, finding 1). Refusing ONE table's sample (set_table_sample off) left the stored grant in force: its SampleTables still named the table, the sample was cut
    /// against that old grant, and a rename gave the next question a new digest, so the refused table's facts reached Jev. The refusal now narrows the grant kept beside the
    /// model's permission (and the plan's own copy), on disk, and the next request carries none of the refused table's numbers. Both doors, since both can refuse.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Join8RefusedSampleTests : GateFixtureBase
    {
        private const string Server = "probe.datawarehouse.fabric.microsoft.com", Db = "Warehouse";
        private const string GrantedRows = "424242", RefusedRows = "987654", RefusedDistinct = "7531", RefusedBlank = "0.2468";
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public Join8RefusedSampleTests(RestoreRootFixture fixture) : base(fixture)
        {
            ConnectionRegistry.RootOverride = Path.Combine(Root, "connections");
            AgentPolicyStore.SetPreset("open", "human");
        }

        public override void Dispose()
        {
            ConnectionRegistry.RootOverride = _oldConnections;
            base.Dispose();
        }

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
            public void Clear() { lock (_requests) _requests.Clear(); }
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

        private static bool CarriesRefused(string request) => request.Contains(RefusedRows) || request.Contains(RefusedDistinct) || request.Contains(RefusedBlank);

        private static async Task<StartPlanView> Ready(LocalEngine e)
        {
            await e.StartDecideSettledForTest(); await e.StartPreviewSettledForTest();
            return await e.GetStartPlanAsync();
        }

        [Theory]
        [InlineData("ui")]
        [InlineData("mcp")]
        public async Task A_table_whose_sample_is_refused_after_the_grant_reaches_no_later_Jev_request(string door)
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
            var view = await Ready(e);
            await e.GrantStartDisclosureAsync(view.Digest, "human");
            await Ready(e);
            // The positive control: with both tables granted, the recorder sees Payroll's numbers, so a later silence is the fix and not a blind recorder.
            Assert.Contains(oracle.All(), CarriesRefused);

            oracle.Clear();
            if (door == "mcp") await McpTools.EditStartPlan(e, "set_table_sample", "Payroll", "off");
            else await e.EditStartPlanAsync(new StartPlanEdit { Op = "set_table_sample", Target = "Payroll", Value = "off" }, "human");
            // A rename gives the next question a new request digest, so a cached answer cannot hide a request that still carries the facts.
            if (door == "mcp") await McpTools.EditStartPlan(e, "rename", "column:Payroll/GrossPay", "Base Pay");
            else await e.EditStartPlanAsync(new StartPlanEdit { Op = "rename", Target = "column:Payroll/GrossPay", Value = "Base Pay" }, "human");
            await Ready(e);

            Assert.DoesNotContain(oracle.All(), CarriesRefused);
            // The grant kept beside the model's permission no longer names Payroll's sample, and what is cut for Jev holds none of its numbers.
            var cut = await e.SampleForJevAsync(id, Sample());
            Assert.NotNull(cut);
            var json = System.Text.Json.JsonSerializer.Serialize(cut);
            Assert.False(CarriesRefused(json), "the sample cut for Jev still holds the refused table's numbers");
            Assert.Contains(GrantedRows, json);
        }
    }

    /// <summary>
    /// K7 (Sol, join 8, finding 2, a merge resolution). psc3's writer marked the person's words from the human door as the assistant's, so a later MCP write replaced
    /// them. One owner value ("person") now holds for every kind of word and every update: a human description, a human rename and a human keep_name all stand against the
    /// assistant, and the tally counts them.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Join8WordOwnerTests : GateFixtureBase
    {
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public Join8WordOwnerTests(RestoreRootFixture fixture) : base(fixture)
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

        private async Task<LocalEngine> FreeEngineAsync(SessionManager sm)
        {
            var e = new LocalEngine(sm, TestEntitlements.Free, Path.Combine(Root, "ws"));
            e.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            e.UseDecisionsForTest(new DecisionService(new NoAnswer()));
            var view = await e.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "wh-j8.datawarehouse.fabric.microsoft.com", Database = "ContosoConsulting", Folder = Path.Combine(Root, "m"), Name = "Uplift",
            }, "human");
            Assert.True(view.Exists);
            await e.StartPreviewSettledForTest();
            return e;
        }

        private const string Column = "column:dim_client/CLNT_NM";

        [Fact]
        public async Task A_description_the_person_edited_is_not_replaced_by_the_assistants_later_write()
        {
            using var sm = new SessionManager(); using var e = await FreeEngineAsync(sm);
            var first = await McpTools.WriteStartWords(e, new[] { new StartWord { Ref = Column, Kind = "description", Text = "The assistant's first words." } });
            Assert.Equal(1, first.Written);
            var edited = await e.WriteStartWordsAsync(new[] { new StartWord { Ref = Column, Kind = "description", Text = "The person's own words.", Expected = "The assistant's first words." } }, "human");
            Assert.True(edited.Written == 1, "refused: " + string.Join("; ", edited.Refused));
            var line = e.StartPlanDocForTest().Lines.Single(l => l.Item.Target == "description" && l.Item.ObjectRef.EndsWith("CLNT_NM", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("person", line.DecidedBy);

            var later = await McpTools.WriteStartWords(e, new[] { new StartWord { Ref = Column, Kind = "description", Text = "The assistant's second words." } });
            Assert.Equal(0, later.Written);
            Assert.Contains(later.Refused, r => r.Contains("chose this yourself", StringComparison.OrdinalIgnoreCase));
            line = e.StartPlanDocForTest().Lines.Single(l => l.Item.Target == "description" && l.Item.ObjectRef.EndsWith("CLNT_NM", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("The person's own words.", line.Word);
            // The tally counts the person's words as words the plan has (they are handmade), so the page's count does not drop when the person edits.
            Assert.True(e.StartPlanDocForTest().Words.Total - e.StartPlanDocForTest().Words.Open >= 1);
        }

        [Fact]
        public async Task A_name_the_person_kept_is_not_replaced_by_the_assistants_later_rename()
        {
            using var sm = new SessionManager(); using var e = await FreeEngineAsync(sm);
            var kept = await e.WriteStartWordsAsync(new[] { new StartWord { Ref = Column, Kind = "keep_name", Text = "" } }, "human");
            Assert.Equal(1, kept.Written);
            Assert.Equal("person", e.StartPlanDocForTest().Lines.Single(l => l.Item.Kind == "keep_name").DecidedBy);
            var later = await McpTools.WriteStartWords(e, new[] { new StartWord { Ref = Column, Kind = "display_name", Text = "Client Name" } });
            Assert.Equal(0, later.Written);
            Assert.Contains(later.Refused, r => r.Contains("kept this name yourself", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task An_edit_that_starts_from_words_the_assistant_has_since_replaced_is_refused()
        {
            using var sm = new SessionManager(); using var e = await FreeEngineAsync(sm);
            await McpTools.WriteStartWords(e, new[] { new StartWord { Ref = Column, Kind = "description", Text = "A." } });
            await McpTools.WriteStartWords(e, new[] { new StartWord { Ref = Column, Kind = "description", Text = "B." } });
            var edit = await e.WriteStartWordsAsync(new[] { new StartWord { Ref = Column, Kind = "description", Text = "Mine.", Expected = "A." } }, "human");
            Assert.Equal(0, edit.Written);
            Assert.Contains(edit.Refused, r => r.Contains("changed while you were editing", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("B.", e.StartPlanDocForTest().Lines.Single(l => l.Item.Target == "description" && l.Item.ObjectRef.EndsWith("CLNT_NM", StringComparison.OrdinalIgnoreCase)).Word);
        }
    }

    /// <summary>
    /// K8 (Sol, join 8, finding 3). The first Pro decide round removes the rule-made base measures before it builds the scratch, so a description written for one of them
    /// before the person agreed to Jev was found missing and deleted by StartPlanHold.Settle. A hand-made line on a base measure the loop removes or replaces is HELD first
    /// and comes back with its measure.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Join8HeldBeforeRebaseTests : GateFixtureBase
    {
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public Join8HeldBeforeRebaseTests(RestoreRootFixture fixture) : base(fixture)
        {
            ConnectionRegistry.RootOverride = Path.Combine(Root, "connections");
            AgentPolicyStore.SetPreset("open", "human");
        }

        public override void Dispose()
        {
            ConnectionRegistry.RootOverride = _oldConnections;
            base.Dispose();
        }

        private sealed class Unsure : IDecisionOracle
        {
            public bool Available { get; set; } = true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct) => Task.FromResult(DecisionAnswers.NotDecided(request, "unsure"));
        }

        [Fact]
        public async Task A_description_written_for_a_rule_made_measure_before_the_grant_survives_the_first_decide_round()
        {
            using var sm = new SessionManager();
            using var e = new LocalEngine(sm, TestEntitlements.Pro, Path.Combine(Root, "ws"));
            e.JevBatchedForTest = _ => true;
            e.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            e.UseDecisionsForTest(new DecisionService(new Unsure()));
            await e.StartModelFromSqlAsync(new StartModelRequest
            {
                Server = "wh-j8.datawarehouse.fabric.microsoft.com", Database = "ContosoConsulting", Folder = Path.Combine(Root, "m"), Name = "Uplift",
            }, "human");
            await e.StartPreviewSettledForTest();
            var measure = e.StartPlanDocForTest().Manifest.BaseMeasures.First(b => b.DecidedBy == StartDecidedBy.Rule || string.IsNullOrEmpty(b.DecidedBy));
            var measureRef = "measure:" + measure.Table + "/" + measure.Name;
            var written = await McpTools.WriteStartWords(e, new[] { new StartWord { Ref = measureRef, Kind = "description", Text = "What the hours total means." } });
            Assert.True(written.Written == 1, "refused: " + string.Join("; ", written.Refused));

            await e.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            var view = await e.GetStartPlanAsync();
            await e.GrantStartDisclosureAsync(view.Digest, "human");
            await e.StartDecideSettledForTest();

            var doc = e.StartPlanDocForTest();
            Assert.True(doc.Decide.Round > 0, "precondition: a decide round ran");
            var line = doc.Lines.SingleOrDefault(l => l.Item.Target == "description" && l.Item.ObjectRef == measureRef);
            Assert.True(line != null, "the description written before the grant was deleted by the first decide round");
            Assert.Equal("What the hours total means.", line.Word);
        }
    }

    /// <summary>
    /// K10 (Sol, join 8, A2 finding 2). Dropping every guessed Sales link takes the rule-made base measures away (Sales is no fact table without a link), and keeping the links
    /// made them again with an empty list of left-out time measures: a year to date the person had dropped came back. The choice now waits under the base measure's identity
    /// while a link change suspends the base (it is not built meanwhile) and returns with it.
    /// </summary>
    public sealed class Join8SuspendedTimeDropTests
    {
        private static StructureManifest Derive() =>
            StructureManifest.Derive(StartLinkGuessTests.Northstar(), null, null, "seed", "x", "DW", "import", StartLinkGuessTests.NorthstarSample());

        private static StartPlanDoc DocOf(StructureManifest m) => new StartPlanDoc { Id = "sp-test", Manifest = m, IdentitySeed = "seed" };
        private static string Ref(ManifestLink l) => "relationship:" + l.Identity;
        private const string Ytd = "Total NetSalesAmount YTD";

        [Fact]
        public void A_time_measure_the_person_dropped_stays_dropped_through_dropping_every_guessed_link_and_keeping_them_again()
        {
            var m = Derive();
            var doc = DocOf(m);
            Assert.Contains(m.TimeMeasures(), t => t.Name == Ytd);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = "measure:Sales/" + Ytd }, "human").Ok);
            Assert.DoesNotContain(m.TimeMeasures(), t => t.Name == Ytd);

            foreach (var l in m.Links.ToList()) Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = Ref(l) }, "human").Ok);
            Assert.Empty(m.BuiltBases());   // the suspended base is not built
            foreach (var l in m.Links.ToList()) Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "keep_line", Target = Ref(l) }, "human").Ok);

            Assert.Contains(m.BuiltBases(), b => b.Name == "Total NetSalesAmount");
            Assert.DoesNotContain(m.TimeMeasures(), t => t.Name == Ytd);
            Assert.Contains(m.AllTimeMeasures(), t => t.Name == Ytd && t.Dropped);
            // The other time measures of that base came back with it.
            Assert.Contains(m.TimeMeasures(), t => t.Name == "Total NetSalesAmount MTD");
        }

        [Fact]
        public void The_assistants_drop_of_a_time_measure_keeps_its_owner_through_the_same_round_trip()
        {
            var m = Derive();
            var doc = DocOf(m);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = "measure:Sales/" + Ytd }, "agent").Ok);
            foreach (var l in m.Links.ToList()) StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = Ref(l) }, "human");
            foreach (var l in m.Links.ToList()) StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "keep_line", Target = Ref(l) }, "human");
            var back = m.AllTimeMeasures().Single(t => t.Name == Ytd);
            Assert.True(back.Dropped);
            Assert.Equal(StartDecidedBy.Assistant, back.DroppedBy);
        }
    }

    /// <summary>
    /// K13 (the uat7-links check): a column unique in both tables of a pair, with nothing to say which is the one side, is ONE link shown off with that reason, never a link each way.
    /// The three verify tests in VerifyUat7LinksTests.cs (closest name on, a sample that removes the winner, a young fact) are the other half and are not edited.
    /// </summary>
    public sealed class Join8UnclearPairTests
    {
        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };
        private static FabricTable T(string name, params FabricColumn[] cols) => new FabricTable { Schema = "dbo", Name = name, KeyColumns = new string[0], Columns = cols };

        [Fact]
        public void A_column_unique_in_both_tables_with_nothing_to_tell_the_sides_apart_is_one_link_shown_off_with_its_reason()
        {
            var s = new FabricSchema { Server = "x", Database = "DW", Tables = new[] { T("Alpha", C("ItemKey", "Int64"), C("Name", "String")), T("Beta", C("ItemKey", "Int64"), C("Title", "String")) } };
            var facts = LinkGuessFacts.NamesOnly()
                .Measured("dbo.Alpha", new System.Collections.Generic.Dictionary<string, bool> { ["ItemKey"] = true })
                .Measured("dbo.Beta", new System.Collections.Generic.Dictionary<string, bool> { ["ItemKey"] = true });
            var m = StructureManifest.Derive(s, null, null, "seed", "x", "DW", "import", facts);
            var link = Assert.Single(m.Links);
            Assert.False(link.Active);
            Assert.Contains("cannot tell which one is the one side", link.Note);
            Assert.Equal(0, m.ToSpec().Relationships.Count(r => r.IsActive != false));
        }

        [Fact]
        public void The_table_that_owns_the_key_name_is_the_one_side_when_the_sample_shows_a_pair_unique_both_ways()
        {
            var s = new FabricSchema { Server = "x", Database = "DW", Tables = new[]
            {
                T("Invoice", C("InvoiceNo", "String"), C("ClientKey", "Int64"), C("Amount", "Decimal")),
                T("Client", C("ClientKey", "Int64"), C("Name", "String")),
            } };
            var facts = LinkGuessFacts.NamesOnly()
                .Measured("dbo.Invoice", new System.Collections.Generic.Dictionary<string, bool> { ["InvoiceNo"] = true, ["ClientKey"] = true })
                .Measured("dbo.Client", new System.Collections.Generic.Dictionary<string, bool> { ["ClientKey"] = true });
            var m = StructureManifest.Derive(s, null, null, "seed", "x", "DW", "import", facts);
            var link = Assert.Single(m.Links);
            Assert.Equal("Invoice", link.FromTable);
            Assert.Equal("Client", link.ToTable);
            Assert.True(link.Active);
            Assert.Equal("fact", m.Tables.Single(t => t.Name == "Invoice").Role);
        }
    }

    /// <summary>
    /// K15 (the uat7-start check, psc3 defects 3 and 4 seen again in the join): after the assistant has answered every word the queue asked for, the plan's words count is honest.
    /// Whatever the queue still asks is exactly what the count says is open, and an item the assistant can answer with a name or a keep does not stay open for ever.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Join8WordsCountTests : GateFixtureBase
    {
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;
        private readonly Xunit.Abstractions.ITestOutputHelper _out;

        public Join8WordsCountTests(RestoreRootFixture fixture, Xunit.Abstractions.ITestOutputHelper output) : base(fixture)
        {
            _out = output;
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

        private static string Humanise(string r)
        {
            var name = (r ?? "").Substring((r ?? "").LastIndexOf('/') + 1);
            var colon = name.IndexOf(':'); if (colon >= 0) name = name.Substring(colon + 1);
            return string.Join(" ", name.Split('_', ' ').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1).ToLowerInvariant()));
        }

        [Fact]
        public async Task The_words_count_after_the_assistant_answered_every_item_says_what_the_queue_still_asks()
        {
            using var sm = new SessionManager();
            using var e = new LocalEngine(sm, TestEntitlements.Free, Path.Combine(Root, "ws"));
            e.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            e.UseDecisionsForTest(new DecisionService(new NoAnswer()));
            await e.StartModelFromSqlAsync(new StartModelRequest { Server = "wh-j8.datawarehouse.fabric.microsoft.com", Database = "ContosoConsulting", Folder = Path.Combine(Root, "m"), Name = "Uplift" }, "human");
            await e.StartPreviewSettledForTest();
            var refusals = new List<string>();
            for (var pass = 0; pass < 60; pass++)
            {
                var page = await e.GetStartWordsAsync(40, 0);
                if (page.Items.Count == 0) break;
                // An assistant that answers every item: a plain name where the name can be made plainer, a keep where it cannot, words for the rest.
                var words = page.Items.Select(i => i.Kind == "display_name"
                    ? new StartWord { Ref = i.Ref, Kind = "display_name", Text = Humanise(i.Ref) }
                    : new StartWord { Ref = i.Ref, Kind = i.Kind, Text = i.Kind == "synonyms" ? "alias one, alias two" : i.Kind == "instructions" ? "This model answers questions about consulting work." : "What " + Humanise(i.Ref) + " means for the business." }).ToArray();
                var res = await e.WriteStartWordsAsync(words, "agent");
                // What was refused is answered with a keep (the name is right as it is) or a different name, as the refusal says.
                var retry = new List<StartWord>();
                foreach (var r in res.Refused)
                {
                    refusals.Add(r);
                    var refText = r.Substring(0, r.IndexOf(':'));
                    var w = words.FirstOrDefault(x => r.StartsWith(x.Ref + ":", StringComparison.OrdinalIgnoreCase));
                    if (w != null && w.Kind == "display_name") retry.Add(new StartWord { Ref = w.Ref, Kind = "keep_name", Text = "" });
                }
                if (retry.Count > 0) await e.WriteStartWordsAsync(retry.ToArray(), "agent");
                if (res.Written == 0 && retry.Count == 0) break;
            }
            foreach (var r in refusals.Take(20)) _out.WriteLine("refused: " + r);
            var left = await e.GetStartWordsAsync(40, 0);
            var view = await e.GetStartPlanAsync();
            _out.WriteLine("queue still asks " + left.Items.Count + ": " + string.Join(", ", left.Items.Select(i => i.Kind + " " + i.Ref)));
            Assert.Equal(left.Items.Count, view.Words.Open);   // the number on the page is the number the queue asks
            Assert.Equal(0, view.Words.Open);                  // and an assistant that answers everything leaves nothing open
        }
    }

    /// <summary>
    /// Hero run 5 (9 Oct): a typed endpoint opens a browser sign-in; closing that browser left the start on "Reading the source", Cancel did not stop it, and the next start said
    /// "Another start is already running" until VS Code restarted. Cancel (discardStartPlan) now ends a read that is waiting, and a read that never comes back ends on its own with a
    /// plain line. Both free the start slot. Failed first: the read took no token, so nothing could end it.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Join8StuckStartTests : GateFixtureBase
    {
        public Join8StuckStartTests(RestoreRootFixture fixture) : base(fixture) { }

        private LocalEngine NewEngine()
        {
            var e = new LocalEngine(new SessionManager(), TestEntitlements.Free, Path.Combine(Root, "ws"));
            e.StartPlanSeedForTest = "00112233445566778899aabbccddeeff";
            return e;
        }

        private StartModelRequest Req(string folder) => new StartModelRequest
        {
            Server = "wh-j8.datawarehouse.fabric.microsoft.com", Database = "ContosoConsulting", Folder = Path.Combine(Root, folder), Name = "Uplift",
        };

        [Fact]
        public async Task Cancel_ends_a_read_that_is_waiting_for_a_sign_in_and_frees_the_slot()
        {
            var e = NewEngine();
            var waiting = new TaskCompletionSource<bool>();
            e.StartPlanSchemaReaderCtForTest = async (r, o, ct) => { waiting.TrySetResult(true); await Task.Delay(Timeout.Infinite, ct); return null; };
            var first = e.StartModelFromSqlAsync(Req("a"), "human");
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var cancelled = await e.DiscardStartPlanAsync("human").WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(cancelled.Exists);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(StartSourceCopy.SignInClosed(), ex.Message);
            // The slot is free: a new start does not say "Another start is already running".
            e.StartPlanSchemaReaderCtForTest = (r, o, ct) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            var again = await e.StartModelFromSqlAsync(Req("b"), "human").WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(again.Exists);
        }

        [Fact]
        public async Task Cancel_of_a_start_leaves_the_plan_that_was_there_before_untouched()
        {
            var e = NewEngine();
            e.StartPlanSchemaReaderCtForTest = (r, o, ct) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            var before = await e.StartModelFromSqlAsync(Req("a"), "human");
            Assert.True(before.Exists);
            var waiting = new TaskCompletionSource<bool>();
            e.StartPlanSchemaReaderCtForTest = async (r, o, ct) => { waiting.TrySetResult(true); await Task.Delay(Timeout.Infinite, ct); return null; };
            var second = e.StartModelFromSqlAsync(Req("b"), "human");
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var after = await e.DiscardStartPlanAsync("human").WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(after.Exists, "Cancel ended the read; it did not throw away the plan that was already there");
            Assert.Equal(before.Id, after.Id);
        }

        [Fact]
        public async Task An_assistant_cannot_cancel_a_start_the_person_began()
        {
            var e = NewEngine();
            var waiting = new TaskCompletionSource<bool>();
            e.StartPlanSchemaReaderCtForTest = async (r, o, ct) => { waiting.TrySetResult(true); await Task.Delay(Timeout.Infinite, ct); return null; };
            var first = e.StartModelFromSqlAsync(Req("a"), "human");
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var discard = e.DiscardStartPlanAsync("agent");
            await Task.Delay(300);
            Assert.False(first.IsCompleted, "the person's start is still reading");
            await e.DiscardStartPlanAsync("human");   // the person's Cancel ends it, and frees the assistant's call too
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(10)));
            await discard.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task A_read_that_never_comes_back_ends_with_a_plain_line_and_frees_the_slot()
        {
            var e = NewEngine();
            e.StartReadLimitForTest = TimeSpan.FromMilliseconds(200);
            e.StartPlanSchemaReaderCtForTest = async (r, o, ct) => { await Task.Delay(Timeout.Infinite, ct); return null; };
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await e.StartModelFromSqlAsync(Req("a"), "human").WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("Sign-in was closed. Start again when ready.", ex.Message);
            e.StartPlanSchemaReaderCtForTest = (r, o, ct) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            Assert.True((await e.StartModelFromSqlAsync(Req("b"), "human").WaitAsync(TimeSpan.FromSeconds(30))).Exists);
        }

        [Fact]
        public async Task A_failed_read_frees_the_slot()
        {
            var e = NewEngine();
            e.StartPlanSchemaReaderCtForTest = (r, o, ct) => Task.FromResult(new FabricSchema { Error = "boom" });
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await e.StartModelFromSqlAsync(Req("a"), "human"));
            e.StartPlanSchemaReaderCtForTest = (r, o, ct) => Task.FromResult(SqlDraftBuildTests.J8Schema());
            Assert.True((await e.StartModelFromSqlAsync(Req("b"), "human").WaitAsync(TimeSpan.FromSeconds(30))).Exists);
        }
    }
}
