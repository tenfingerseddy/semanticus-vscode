using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.Facts;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// M11 join part 6, Sol's four findings on the spec-first start (area A). Each test was written before its fix and failed on the merged tree.
    /// A1: the review shows every object the build writes (the six time measures too), from one list.
    /// A2: facts from a sample reach Jev only for the tables the person's grant names.
    /// A3: a refused second start leaves the first plan's document where it was.
    /// A4: every start-plan writer takes the one gate, and a build commits only the plan the person approved.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Join6SolStartTests : GateFixtureBase
    {
        private const string Server = "probe.datawarehouse.fabric.microsoft.com", Db = "Warehouse";
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public Join6SolStartTests(RestoreRootFixture fixture) : base(fixture)
        {
            ConnectionRegistry.RootOverride = Path.Combine(Root, "connections");
            AgentPolicyStore.SetPreset("open", "human");
        }

        public override void Dispose()
        {
            ConnectionRegistry.RootOverride = _oldConnections;
            base.Dispose();
        }

        private sealed class Oracle : IDecisionOracle
        {
            public bool Available { get; set; }
            public readonly List<string> Requests = new List<string>();
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (Requests) Requests.Add(JsonSerializer.Serialize(new { request.PointName, request.Snapshot }));
                return Task.FromResult(DecisionAnswers.NotDecided(request, "recording oracle"));
            }
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

        private LocalEngine Engine(SessionManager sm, bool pro, string ws = "ws")
        {
            var e = new LocalEngine(sm, pro ? TestEntitlements.Pro : TestEntitlements.Free, Path.Combine(Root, ws));
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(Schema());
            e.UseDecisionsForTest(new DecisionService(new Oracle()));
            return e;
        }

        private Task<StartPlanView> Start(LocalEngine e, string folder, string name = "Model", string door = "ui") =>
            door == "mcp"
                ? McpTools.StartModelFromSql(e, Server, Db, folder, name)
                : e.StartModelFromSqlAsync(new StartModelRequest { Name = name, Folder = folder, Server = Server, Database = Db }, "human");

        private static async Task<StartPlanView> Ready(LocalEngine e)
        {
            await e.StartDecideSettledForTest(); await e.StartPreviewSettledForTest();
            return await e.GetStartPlanAsync();
        }

        private async Task<StartPlanView> GrantPro(LocalEngine e)
        {
            await e.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            // Payroll is left out so the grant names two tables, not three (the A2 tests read what Jev was and was not shown).
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "Payroll", Value = "false" }, "human");
            var view = await Ready(e);
            await e.GrantStartDisclosureAsync(view.Digest, "human");
            return await Ready(e);
        }

        private static IEnumerable<StartLine> LinesOf(StartPlanView v) => v.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Lines);

        // ================================================================== A1: the review shows what the build writes

        private static readonly string[] TimeKinds = { "YTD", "QTD", "MTD", "PY", "YoY", "YoYPct" };

        [Fact]
        public async Task A1_the_review_lists_each_time_measure_with_its_formula_under_its_base_measure()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"));
            var view = await Ready(e);
            var measures = LinesOf(view).Where(l => l.Item.Kind == "create_measure").ToList();
            var names = measures.Select(l => l.Item.ObjectName).ToList();
            foreach (var k in TimeKinds) Assert.Contains("Total Amount " + k, names);
            var ytd = measures.Single(l => l.Item.ObjectName == "Total Amount YTD");
            Assert.Contains("TOTALYTD([Total Amount]", ytd.Item.After);
            Assert.True(ytd.Kept);
            // Under the base measure: the base line comes first, its six follow it, in one group (the table's).
            var group = view.Sections.SelectMany(s => s.Groups).Single(g => g.Lines.Any(l => l.Item.ObjectName == "Total Amount YTD"));
            var order = group.Lines.Select(l => l.Item.ObjectName).ToList();
            Assert.Equal("Total Amount", order[0]);
            Assert.Equal(TimeKinds.Select(k => "Total Amount " + k), order.Skip(1).Take(6));
            Assert.Contains("Total Amount", ytd.Why);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(false, "ui")]
        [InlineData(false, "mcp")]
        [InlineData(true, "ui")]
        [InlineData(true, "mcp")]
        public async Task A1_the_objects_the_review_lists_are_the_objects_the_build_writes(bool pro, string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, pro);
            await Start(e, Path.Combine(Root, "m"), "Model", door);
            var view = pro ? await GrantPro(e) : await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            var reviewed = LinesOf(view).Where(l => l.Kept).ToList();
            var built = door == "mcp" ? await McpTools.BuildStartPlan(e, view.Digest) : await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);

            var model = await sm.Current.ReadAsync(m => new
            {
                Tables = m.Tables.Select(t => "table:" + t.Name).ToList(),
                Measures = m.AllMeasures.Select(x => "measure:" + x.Table.Name + "/" + x.Name + "=" + x.Expression).ToList(),
                Relationships = m.Relationships.Select(r => r.FromTable.Name + "." + r.FromColumn.Name + ">" + r.ToTable.Name + "." + r.ToColumn.Name).ToList(),
                Columns = m.Tables.ToDictionary(t => t.Name, t => t.Columns.Count),
            });

            Assert.Equal(reviewed.Where(l => l.Item.Kind == "include_table").Select(l => l.Item.ObjectRef).OrderBy(x => x), model.Tables.OrderBy(x => x));
            var reviewedMeasures = reviewed.Where(l => l.Item.Kind == "create_measure").Select(l => l.Item.ObjectRef).OrderBy(x => x).ToList();
            Assert.Equal(reviewedMeasures, model.Measures.Select(x => x.Substring(0, x.IndexOf('='))).OrderBy(x => x));
            Assert.Equal(model.Relationships.Count, reviewed.Count(l => l.Item.Kind == "create_relationship"));
            // Every time measure's formula in the review is the formula in the model.
            foreach (var line in reviewed.Where(l => l.Item.Kind == "create_measure" && l.Item.After != null))
                Assert.Contains(line.Item.ObjectRef + "=" + line.Item.After, model.Measures);
            // Columns: the review says how many of each table's columns come in.
            foreach (var line in reviewed.Where(l => l.Item.Kind == "include_table"))
            {
                var name = line.Item.ObjectName;
                Assert.Contains($"({model.Columns[name]} of ", line.Item.Title);
            }
            Assert.True(reviewedMeasures.Count >= 7, "the six time measures were not in the review");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A1_a_build_that_would_write_an_object_the_review_did_not_show_is_refused_and_writes_nothing()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"));
            var view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            e.StartPlanBuildSeamsForTest = new StartPlanBuildSeams
            {
                AfterStructureForTest = m => m.Tables.First(t => t.Name == "Sales").AddMeasure("Unseen", "1"),
            };
            var built = await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.False(built.Built);
            Assert.Contains("did not show", built.Refused);
            Assert.Equal(0, await sm.Current.ReadAsync(m => m.Tables.Count));
        }

        // ================================================================== A2: the sample reaches Jev only for the tables the grant names

        private static SourceSampleResult Sample() => new SourceSampleResult
        {
            Server = Server, Database = Db, MeasuredAtUtc = DateTime.UtcNow.ToString("O"),
            Tables = new[]
            {
                new SourceTableFacts { Schema = "dbo", Name = "Sales", Rows = 50, Columns = new[] { new SourceColumnFacts { Source = "Amount", Distinct = 31, BlankShare = 0.14 } } },
                new SourceTableFacts { Schema = "dbo", Name = "Payroll", Rows = 80, Columns = new[] { new SourceColumnFacts { Source = "GrossPay", Distinct = 17, BlankShare = 0.25 } } },
            },
        };

        [Fact]
        public void A2_a_sample_is_cut_to_the_tables_and_source_the_grant_names()
        {
            var grant = new DisclosedScope(Server + "/" + Db, new[] { "dbo.Sales", "dbo.DimDate" }, true);
            var cut = Sample().RestrictedTo(grant);
            var sales = cut.Find("dbo", "Sales");
            var payroll = cut.Find("dbo", "Payroll");
            Assert.NotNull(sales); Assert.Null(sales.NotMeasured); Assert.Single(sales.Columns);
            Assert.NotNull(payroll.NotMeasured); Assert.Empty(payroll.Columns); Assert.Null(payroll.Rows);
            // Another source's grant covers none of it.
            var other = Sample().RestrictedTo(new DisclosedScope("other.server/" + Db, new[] { "dbo.Sales", "dbo.Payroll" }, true));
            Assert.All(other.Tables, t => { Assert.NotNull(t.NotMeasured); Assert.Empty(t.Columns); });
        }

        private async Task<(LocalEngine e, SessionManager sm, Oracle oracle)> GrantedThenPayrollAddedBackAsync()
        {
            var sm = new SessionManager(); var e = Engine(sm, true);
            await Start(e, Path.Combine(Root, "model"));
            e.SetStartPlanSample(e.StartPlanDocForTest().Id, Sample());
            var view = await GrantPro(e);   // leaves Payroll out
            view = await e.ApproveStartPlanAsync(view.Digest);
            var built = await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            await e.CreateImportTableAsync("Payroll", "let s = Sql.Database(\"" + Server + "\",\"" + Db + "\"), t = s{[Schema=\"dbo\",Item=\"Payroll\"]}[Data] in t", "human");
            await e.CreateColumnAsync("table:Payroll", "GrossPay", "Decimal", "GrossPay", "human");
            var oracle = new Oracle { Available = true };
            e.UseDecisionsForTest(new DecisionService(oracle));
            return (e, sm, oracle);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A2_a_table_added_back_after_the_grant_sends_no_sample_fact_to_Jev_on_the_plan_path()
        {
            var (e, sm, oracle) = await GrantedThenPayrollAddedBackAsync();
            using (sm) using (e)
            {
                await e.GetModelPlanAsync(); await e.JevSettledForTest();
                lock (oracle.Requests)
                {
                    Assert.NotEmpty(oracle.Requests);
                    Assert.DoesNotContain(oracle.Requests, r => r.Contains("Payroll") && r.Contains("0.25"));
                }
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A2_the_decide_scratch_path_lays_only_granted_tables_of_the_sample_over_the_model()
        {
            var (e, sm, oracle) = await GrantedThenPayrollAddedBackAsync();
            using (sm) using (e)
            {
                var doc = e.StartPlanDocForTest();
                var cut = await e.SampleForJevAsync(doc.Id, Sample());
                Assert.NotNull(cut);
                Assert.Null(cut.Find("dbo", "Sales").NotMeasured);
                Assert.NotNull(cut.Find("dbo", "Payroll").NotMeasured);
                Assert.Empty(cut.Find("dbo", "Payroll").Columns);
                // A plan the person never granted gets nothing at all.
                Assert.Null(await e.SampleForJevAsync("sp-00000000", Sample()));
            }
        }

        // ================================================================== A3: a refused start leaves the first plan alone

        private async Task<(LocalEngine e, SessionManager sm, string id)> BuiltUnsavedAsync(bool pro = false)
        {
            var sm = new SessionManager(); var e = Engine(sm, pro);
            await Start(e, Path.Combine(Root, "a"), "A");
            var view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            var built = await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            return (e, sm, e.StartPlanDocForTest().Id);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A3_a_second_start_refused_for_unsaved_work_keeps_the_first_plans_document_and_its_undo()
        {
            var (e, sm, id) = await BuiltUnsavedAsync();
            using (sm) using (e)
            {
                Assert.NotNull(StartPlanStore.Read(id));
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Start(e, Path.Combine(Root, "b"), "B", "mcp"));
                Assert.Contains("not saved", ex.Message);
                Assert.NotNull(StartPlanStore.Read(id));
                var after = await e.GetStartPlanAsync();
                Assert.True(after.Exists); Assert.True(after.Built); Assert.Equal(id, after.Id);
                await e.UndoAsync("human");
                var again = await e.GetStartPlanAsync();
                Assert.True(again.Exists); Assert.False(again.Built);
                Assert.NotNull(again.Approval);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A3_a_second_start_refused_by_its_folder_keeps_the_first_plans_document()
        {
            var (e, sm, id) = await BuiltUnsavedAsync();
            using (sm) using (e)
            {
                await e.SaveAsync(null, "tmdl", false, "human");
                var busy = Path.Combine(Root, "busy"); Directory.CreateDirectory(busy); File.WriteAllText(Path.Combine(busy, "x.txt"), "x");
                await Assert.ThrowsAsync<InvalidOperationException>(() => Start(e, busy, "B"));
                Assert.NotNull(StartPlanStore.Read(id));
                Assert.True((await e.GetStartPlanAsync()).Exists);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A3_a_second_start_whose_source_cannot_be_read_keeps_the_first_plans_document()
        {
            var (e, sm, id) = await BuiltUnsavedAsync();
            using (sm) using (e)
            {
                await e.SaveAsync(null, "tmdl", false, "human");
                e.StartPlanSchemaReaderForTest = (r, o) => throw new InvalidOperationException("the source is down");
                await Assert.ThrowsAsync<InvalidOperationException>(() => Start(e, Path.Combine(Root, "b"), "B"));
                Assert.NotNull(StartPlanStore.Read(id));
                Assert.True((await e.GetStartPlanAsync()).Exists);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A3_a_second_start_that_succeeds_retires_the_first_plans_document()
        {
            var (e, sm, id) = await BuiltUnsavedAsync();
            using (sm) using (e)
            {
                await e.SaveAsync(null, "tmdl", false, "human");
                var second = await Start(e, Path.Combine(Root, "b"), "B");
                Assert.True(second.Exists); Assert.NotEqual(id, second.Id);
                Assert.Null(StartPlanStore.Read(id));
                Assert.NotNull(StartPlanStore.Read(second.Id));
            }
        }

        // ================================================================== A4: one gate for every writer, and a build commits what was approved

        private sealed class PausedReader : LocalEngine.IStartPreviewReader
        {
            public Session Session;
            public readonly TaskCompletionSource<bool> ReadDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task<T> ReadAsync<T>(Func<Model, T> read)
            {
                var result = await Session.ReadAsync(read);
                ReadDone.TrySetResult(true); await Resume.Task;
                return result;
            }
        }

        private static StartWord[] SalesWords() => new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "Revenue excluding tax." } };

        private async Task<(PausedReader reader, StartPlanScratch scratch)> PauseReaderAsync(LocalEngine e, SessionManager sm, StartPlanDoc doc)
        {
            var scratch = await StartPlanScratch.CloneAsync(sm.Current, Path.Combine(Root, "reader"));
            await scratch.Session.MutateAsync("human", "fixture", m => StartPlanBuild.Materialize(e, m, scratch.Session, doc));
            var reader = new PausedReader { Session = scratch.Session };
            e.StartPlanPreviewReaderForTest = reader;
            return (reader, scratch);
        }

        [Fact]
        public async Task A4_a_words_write_in_flight_stops_the_build_which_refuses_with_a_plain_line_and_the_words_stay()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "model"));
            var view = await Ready(e); view = await e.ApproveStartPlanAsync(view.Digest);
            var (reader, scratch) = await PauseReaderAsync(e, sm, e.StartPlanDocForTest());
            using (scratch)
            {
                var words = e.WriteStartWordsAsync(SalesWords(), "agent");
                await reader.ReadDone.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var build = e.BuildStartPlanAsync(view.Digest, "human");
                await Task.Delay(300);
                Assert.False(build.IsCompleted, "the build ran beside a words write: the two did not share one gate");
                reader.Resume.TrySetResult(true);
                var written = await words.WaitAsync(TimeSpan.FromSeconds(20));
                var built = await build.WaitAsync(TimeSpan.FromSeconds(20));

                Assert.Equal(1, written.Written);
                Assert.False(built.Built);
                Assert.False(string.IsNullOrWhiteSpace(built.Refused));
                Assert.Equal(0, await sm.Current.ReadAsync(m => m.Tables.Count));
                var live = await e.GetStartPlanAsync();
                Assert.Contains(LinesOf(live), l => l.Item.Kind == "set_description" && l.Item.After == "Revenue excluding tax.");
            }
        }

        [Fact]
        public async Task A4_a_words_write_that_waits_behind_a_running_build_is_refused_not_accepted_and_lost()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "model"));
            var view = await Ready(e); view = await e.ApproveStartPlanAsync(view.Digest);
            using var releaseBuild = new ManualResetEventSlim(false);
            var inside = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            e.StartPlanBuildSeamsForTest = new StartPlanBuildSeams { InsideBuildForTest = () => { inside.TrySetResult(true); releaseBuild.Wait(TimeSpan.FromSeconds(20)); } };
            var build = e.BuildStartPlanAsync(view.Digest, "human");
            await inside.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var words = e.WriteStartWordsAsync(SalesWords(), "agent");
            await Task.Delay(300);
            Assert.False(words.IsCompleted, "the words write ran beside the build");
            releaseBuild.Set();
            var built = await build.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(built.Built, built.Refused);
            StartWordsResult written = null; Exception refused = null;
            try { written = await words.WaitAsync(TimeSpan.FromSeconds(20)); } catch (Exception ex) { refused = ex; }
            Assert.True(refused != null || written.Written == 0, "the words were accepted after the build had taken its copy of the plan");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A4_the_build_commits_only_while_the_live_plan_still_has_the_approved_digest()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "model"));
            var view = await Ready(e); view = await e.ApproveStartPlanAsync(view.Digest);
            // A writer that does not take the gate moves the live plan after the build took its copy and before it commits.
            e.StartPlanBuildSeamsForTest = new StartPlanBuildSeams
            {
                InsideBuildForTest = () => e.StartPlanDocForTestMutate(d => { d.Pins["table:Sales"] = "Moved under the build."; }),
            };
            var built = await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.False(built.Built);
            Assert.Contains("changed", built.Refused);
            Assert.Equal(0, await sm.Current.ReadAsync(m => m.Tables.Count));
            Assert.Equal("Moved under the build.", e.StartPlanDocForTest().Pins["table:Sales"]);
        }
        // Verifier (v-agent-controls, 11 Oct 2026): the person says yes in chat, the assistant approves and builds through the gated MCP door.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task V_agentctl_the_assistant_approves_then_builds_a_start_plan_through_the_gated_MCP_door_and_one_undo_takes_it_back(bool pro)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, pro);
            await Start(e, Path.Combine(Root, "m"), "Model", "mcp");
            var view = pro ? await GrantPro(e) : await Ready(e);
            var gated = AssistantEngine.ForAssistant(e);
            var approved = await McpTools.ApproveStartPlan(gated, view.Digest);
            Assert.Equal(view.Digest, approved.Approval?.Digest);
            var built = await McpTools.BuildStartPlan(gated, approved.Digest);
            Assert.True(built.Built, built.Refused);
            Assert.True(await sm.Current.ReadAsync(m => m.Tables.Count) > 0);
            await McpTools.UndoChange(gated);
            Assert.Equal(0, await sm.Current.ReadAsync(m => m.Tables.Count));
        }

        // A build the assistant approved (the person said yes in chat) is credited to the assistant in History; one the person approved stays theirs.
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task A_start_plan_build_names_who_approved_it_the_assistant_or_the_person(bool byAssistant)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"), "Model", "mcp");
            var view = await Ready(e);
            var gated = AssistantEngine.ForAssistant(e);
            var approved = byAssistant ? await McpTools.ApproveStartPlan(gated, view.Digest) : await e.ApproveStartPlanAsync(view.Digest);
            var commits = new List<ChangeNotification>();
            sm.Bus.Changed += n => { if (n.IsCommit) lock (commits) commits.Add(n); };
            var built = await McpTools.BuildStartPlan(gated, approved.Digest);
            Assert.True(built.Built, built.Refused);
            var build = Assert.Single(commits, n => n.Cause?.Kind == ChangeCauseKinds.StartPlan);
            Assert.Equal(byAssistant ? ChangeBy.Assistant : ChangeBy.You, build.Cause.ApprovedBy);
        }
    }
}
