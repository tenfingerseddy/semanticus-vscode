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
    /// The verifier's attacks on m11/sol6-start (Sol, join 6, area A). Same fixture as Join6SolStartTests.
    /// S1: the Pro hero journey with no round that moves the plan (Jev off or silent): permission is given AFTER the preview, then the person builds.
    /// S2: a base measure the person dropped (its pick is none) builds without it.
    /// A1-A4: the review is the build, a sample stays in its grant, a start keeps the old plan, the writers share one gate.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Join6VerifySol6StartTests : GateFixtureBase
    {
        private const string Server = "probe.datawarehouse.fabric.microsoft.com", Db = "Warehouse";
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public Join6VerifySol6StartTests(RestoreRootFixture fixture) : base(fixture)
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
                new FabricTable { Schema = "dbo", Name = "Sales", KeyColumns = new[] { "Id" }, Columns = new[] { C("Id", "Int64"), C("DateKey", "Int64"), C("Amount", "Decimal"), C("Cost", "Decimal") } },
                new FabricTable { Schema = "dbo", Name = "DimDate", KeyColumns = new[] { "DateKey" }, Columns = new[] { C("DateKey", "Int64"), C("Date", "DateTime") } },
                new FabricTable { Schema = "dbo", Name = "Payroll", KeyColumns = new[] { "Id" }, Columns = new[] { C("Id", "Int64"), C("GrossPay", "Decimal") } },
            },
            ForeignKeys = new[] { new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "DateKey", ToSchema = "dbo", ToTable = "DimDate", ToColumn = "DateKey" } },
        };

        private LocalEngine Engine(SessionManager sm, bool pro, Oracle oracle = null)
        {
            var e = new LocalEngine(sm, pro ? TestEntitlements.Pro : TestEntitlements.Free, Path.Combine(Root, "ws"));
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(Schema());
            e.UseDecisionsForTest(new DecisionService(oracle ?? new Oracle()));
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

        private static IEnumerable<StartLine> LinesOf(StartPlanView v) => v.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Lines);

        private static Task<StartPlanBuildResult> Build(LocalEngine e, string digest, string door) =>
            door == "mcp" ? McpTools.BuildStartPlan(e, digest) : e.BuildStartPlanAsync(digest, "human");

        // ================================================================== S1: the Pro journey with no round that moves the plan

        /// <summary>Kane's hero journey on Pro: start, read the plan, give the model permission, allow the tables, approve, build. Jev is off (unavailable) or answers
        /// nothing, so no round moves the plan and no edit is made. The build must land. The builder's join suspicion 1: the preview was made on the model before the
        /// permission marker went into it, the digest never moves, so no new preview is made and the build says "not the one you were shown".</summary>
        [Theory]
        [InlineData(false, "ui")]
        [InlineData(true, "ui")]
        [InlineData(false, "mcp")]
        public async Task S1_a_Pro_start_given_permission_after_the_preview_builds_with_no_edit(bool jevAnswers, string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, true, new Oracle { Available = jevAnswers });
            var taps = new List<(string, string)>();
            e.StartPlanBuildSeamsForTest = new StartPlanBuildSeams { Tap = (k, t) => { lock (taps) taps.Add((k, t)); } };
            await Start(e, Path.Combine(Root, "m"), "Model", door);
            var view = await Ready(e);
            await e.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            view = await Ready(e);
            view = await e.GrantStartDisclosureAsync(view.Digest, "human");
            view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            Assert.True(view.CanBuild, view.BlockedBecause);
            var built = await Build(e, view.Digest, door);
            if (!built.Built)
            {
                var pv = taps.LastOrDefault(t => t.Item1 == "preview").Item2 ?? ""; var bv = taps.LastOrDefault(t => t.Item1 == "build").Item2 ?? "";
                var pl = pv.Split('\n'); var bl = bv.Split('\n');
                var diff = string.Join(" | ", bl.Except(pl).Take(6)) + " /// preview-only: " + string.Join(" | ", pl.Except(bl).Take(6));
                Assert.True(built.Built, "Pro hero journey with no edit refused: " + built.Refused + " taps=" + string.Join(",", taps.Select(t => t.Item1)) + " DIFF: " + diff);
            }
        }

        // ================================================================== S2: a dropped base measure

        [Theory]
        [InlineData("drop_line", "measure:Sales/Total Amount", null)]
        [InlineData("set_pick", "Sales/Amount", "none")]
        public async Task S2_a_base_measure_the_person_dropped_builds_without_it_or_its_time_measures(string op, string target, string value)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"));
            await Ready(e);
            await e.EditStartPlanAsync(new StartPlanEdit { Op = op, Target = target, Value = value }, "human");
            var view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            var reviewed = LinesOf(view).Where(l => l.Kept && l.Item.Kind == "create_measure").Select(l => l.Item.ObjectRef).OrderBy(x => x).ToList();
            Assert.DoesNotContain("measure:Sales/Total Amount", reviewed);
            var built = await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, "a plan with a dropped measure did not build: " + built.Refused);
            var measures = await sm.Current.ReadAsync(m => m.AllMeasures.Select(x => "measure:" + x.Table.Name + "/" + x.Name).OrderBy(x => x).ToList());
            Assert.Equal(reviewed, measures);
        }

        // ================================================================== A1: the review is the build

        /// <summary>A rename of the base measure moves its six time measures; the review and the build still list the same objects with the same formulas.</summary>
        [Theory]
        [InlineData("ui")]
        [InlineData("mcp")]
        public async Task A1_after_a_rename_and_a_second_sum_the_review_and_the_build_are_one_list(string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"), "Model", door);
            await Ready(e);
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "rename", Target = "measure:Sales/Total Amount", Value = "Revenue" }, "human");
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "set_pick", Target = "Sales/Cost", Value = "sum" }, "human");
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "set_fiscal_year_end", Target = "", Value = "6/30" }, "human");
            var view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            var reviewed = LinesOf(view).Where(l => l.Kept && l.Item.Kind == "create_measure").ToList();
            var built = await Build(e, view.Digest, door);
            Assert.True(built.Built, built.Refused);
            var model = await sm.Current.ReadAsync(m => m.AllMeasures.ToDictionary(x => "measure:" + x.Table.Name + "/" + x.Name, x => x.Expression));
            Assert.Equal(reviewed.Select(l => l.Item.ObjectRef).OrderBy(x => x), model.Keys.OrderBy(x => x));
            Assert.Contains("measure:Sales/Revenue YTD", model.Keys);
            Assert.True(reviewed.Count >= 14, "two sums with six time measures each were not all reviewed: " + reviewed.Count);
            foreach (var l in reviewed.Where(l => l.Item.After != null))
                Assert.Equal(string.Concat(l.Item.After.Where(c => !char.IsWhiteSpace(c))), string.Concat(model[l.Item.ObjectRef].Where(c => !char.IsWhiteSpace(c))));
            Assert.Contains("6/30", model["measure:Sales/Revenue YTD"]);
        }

        /// <summary>After the person approved, an ungated writer (a Jev round) renames a base measure, drops one, or adds a sum (and so six time measures). The
        /// build refuses and writes nothing.</summary>
        [Theory]
        [InlineData("rename")]
        [InlineData("drop")]
        [InlineData("add")]
        [InlineData("fiscal")]
        public async Task A1_a_plan_moved_after_approval_never_builds_what_the_person_did_not_see(string how)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"));
            var view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            e.StartPlanDocForTestMutate(d =>
            {
                var b = d.Manifest.BaseMeasures.First(x => x.Name == "Total Amount");
                switch (how)
                {
                    case "rename": b.Name = "Revenue"; break;
                    case "drop": b.Pick = "none"; break;
                    case "add": d.Manifest.BaseMeasures.Add(new ManifestBaseMeasure { Table = "Sales", Column = "Cost", Pick = "sum", Name = "Total Cost", DecidedBy = "jev", Identity = "x-cost" }); break;
                    case "fiscal": d.Manifest.DateTable.FiscalYearEnd = "6/30"; break;
                }
                d.Manifest.RefreshTimeIntelligence();
            });
            var built = await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.False(built.Built);
            Assert.False(string.IsNullOrWhiteSpace(built.Refused));
            Assert.Equal(0, await sm.Current.ReadAsync(m => m.Tables.Count));
        }

        // ================================================================== A2: a sample stays inside its grant

        private static SourceSampleResult Sample() => new SourceSampleResult
        {
            Server = Server, Database = Db, MeasuredAtUtc = DateTime.UtcNow.ToString("O"),
            Tables = new[]
            {
                new SourceTableFacts { Schema = "dbo", Name = "Sales", Rows = 50, Columns = new[] { new SourceColumnFacts { Source = "Amount", Distinct = 31, BlankShare = 0.14 } } },
                new SourceTableFacts { Schema = "dbo", Name = "Payroll", Rows = 65432, Columns = new[] { new SourceColumnFacts { Source = "GrossPay", Distinct = 9871, BlankShare = 0.4321 } } },
            },
        };

        private static void NoPayrollFact(Oracle oracle)
        {
            lock (oracle.Requests)
                foreach (var r in oracle.Requests)
                {
                    Assert.DoesNotContain("65432", r);
                    Assert.DoesNotContain("9871", r);
                    Assert.DoesNotContain("0.4321", r);
                }
        }

        private async Task<(LocalEngine e, SessionManager sm, Oracle oracle)> GrantedBuiltAsync(Oracle oracle)
        {
            var sm = new SessionManager(); var e = Engine(sm, true, oracle);
            await Start(e, Path.Combine(Root, "model"));
            e.SetStartPlanSample(e.StartPlanDocForTest().Id, Sample());
            await Ready(e);
            await e.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "Payroll", Value = "false" }, "human");
            var view = await Ready(e);
            await e.GrantStartDisclosureAsync(view.Digest, "human");
            view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            var built = await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            return (e, sm, oracle);
        }

        /// <summary>Payroll comes back under ANOTHER name (Wages) over the same physical table, after the person's own facts view filled the cache with the whole
        /// sample. The Plan's Jev requests carry none of Payroll's numbers.</summary>
        [Fact]
        public async Task A2_a_table_put_back_under_another_name_over_the_same_source_sends_no_sample_fact_even_after_the_person_read_the_facts()
        {
            var oracle = new Oracle { Available = true };
            var (e, sm, _) = await GrantedBuiltAsync(oracle);
            using (sm) using (e)
            {
                await e.CreateImportTableAsync("Wages", "let s = Sql.Database(\"" + Server + "\",\"" + Db + "\"), t = s{[Schema=\"dbo\",Item=\"Payroll\"]}[Data] in t", "human");
                await e.CreateColumnAsync("table:Wages", "Pay", "Decimal", "GrossPay", "human");
                // The person's own view of the facts is not Jev and may carry the whole sample; it must not become what Jev reads.
                var mine = await e.GetModelFactsAsync("human");
                var json = JsonSerializer.Serialize(mine);
                Assert.Contains("9871", json);   // the sample really matches Wages by its source, so the attack is live
                lock (oracle.Requests) oracle.Requests.Clear();
                await e.GetModelPlanAsync(); await e.JevSettledForTest();
                lock (oracle.Requests) Assert.NotEmpty(oracle.Requests);
                NoPayrollFact(oracle);
            }
        }

        /// <summary>The decide path: Payroll is put back into the plan after the grant (before the build). Any round asks nothing about it with its numbers.</summary>
        [Fact]
        public async Task A2_a_table_put_back_into_the_plan_after_the_grant_sends_no_sample_fact_from_the_decide()
        {
            var oracle = new Oracle { Available = true };
            using var sm = new SessionManager(); using var e = Engine(sm, true, oracle);
            await Start(e, Path.Combine(Root, "model"));
            e.SetStartPlanSample(e.StartPlanDocForTest().Id, Sample());
            await Ready(e);
            await e.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "Payroll", Value = "false" }, "human");
            var view = await Ready(e);
            await e.GrantStartDisclosureAsync(view.Digest, "human");
            await Ready(e);
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "Payroll", Value = "true" }, "human");
            await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Payroll", Kind = "description", Text = "Pay per person." } }, "agent");
            await Ready(e);
            NoPayrollFact(oracle);
            lock (oracle.Requests) Assert.DoesNotContain(oracle.Requests, r => r.Contains("9871"));   // the assertion the hygiene rule wants in the test's own body
        }

        // ================================================================== A3: no start verb drops the plan before the new one stands

        [Fact]
        public async Task A3_a_second_start_refused_for_unsaved_work_on_the_person_door_or_for_an_empty_source_keeps_the_first_plan()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "a"), "A");
            var view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            Assert.True((await e.BuildStartPlanAsync(view.Digest, "human")).Built);
            var id = e.StartPlanDocForTest().Id;
            // The person's own door, unsaved work: refused, the plan stays.
            await Assert.ThrowsAnyAsync<Exception>(() => Start(e, Path.Combine(Root, "b"), "B"));
            Assert.NotNull(StartPlanStore.Read(id));
            // Saved, then a source with no tables: refused, the plan stays.
            await e.SaveAsync(null, "tmdl", false, "human");
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(new FabricSchema { Server = Server, Database = Db, Tables = Array.Empty<FabricTable>() });
            await Assert.ThrowsAnyAsync<Exception>(() => Start(e, Path.Combine(Root, "c"), "C"));
            Assert.NotNull(StartPlanStore.Read(id));
            // A bad name: refused before anything, the plan stays.
            await Assert.ThrowsAnyAsync<Exception>(() => Start(e, Path.Combine(Root, "d"), ""));
            Assert.NotNull(StartPlanStore.Read(id));
            var after = await e.GetStartPlanAsync();
            Assert.True(after.Exists); Assert.Equal(id, after.Id);
        }

        // ================================================================== A4: the writers race

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

        /// <summary>Words in flight, then the person approves the digest they saw: the approval waits, then refuses (the plan moved) or approves the new plan; the
        /// build with the old digest refuses, the words stay, and the build of the new approval writes them.</summary>
        [Fact]
        public async Task A4_an_approval_racing_a_words_write_waits_and_the_words_reach_the_model()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "model"));
            var view = await Ready(e);
            var scratch = await StartPlanScratch.CloneAsync(sm.Current, Path.Combine(Root, "reader"));
            using (scratch)
            {
                var doc = e.StartPlanDocForTest();
                await scratch.Session.MutateAsync("human", "fixture", m => StartPlanBuild.Materialize(e, m, scratch.Session, doc));
                var reader = new PausedReader { Session = scratch.Session };
                e.StartPlanPreviewReaderForTest = reader;
                var words = e.WriteStartWordsAsync(SalesWords(), "agent");
                await reader.ReadDone.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var approve = e.ApproveStartPlanAsync(view.Digest);
                await Task.Delay(300);
                Assert.False(approve.IsCompleted, "the approval ran beside a words write");
                reader.Resume.TrySetResult(true);
                Assert.Equal(1, (await words.WaitAsync(TimeSpan.FromSeconds(20))).Written);
                try { await approve.WaitAsync(TimeSpan.FromSeconds(20)); } catch (InvalidOperationException) { }
                e.StartPlanPreviewReaderForTest = null;
            }
            var old = await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.False(old.Built, "the build committed a digest that was not the plan's");
            Assert.Equal(0, await sm.Current.ReadAsync(m => m.Tables.Count));
            var fresh = await Ready(e);
            Assert.NotEqual(view.Digest, fresh.Digest);
            fresh = await e.ApproveStartPlanAsync(fresh.Digest);
            var built = await e.BuildStartPlanAsync(fresh.Digest, "human");
            Assert.True(built.Built, built.Refused);
            Assert.Equal("Revenue excluding tax.", await sm.Current.ReadAsync(m => m.Tables["Sales"].Description));
        }

        /// <summary>An edit that waits behind a running build: it either is refused or stays in the plan. It is never accepted and then lost.</summary>
        [Fact]
        public async Task A4_an_edit_behind_a_running_build_is_refused_or_kept_never_accepted_and_lost()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "model"));
            var view = await Ready(e); view = await e.ApproveStartPlanAsync(view.Digest);
            using var releaseBuild = new ManualResetEventSlim(false);
            var inside = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            e.StartPlanBuildSeamsForTest = new StartPlanBuildSeams { InsideBuildForTest = () => { inside.TrySetResult(true); releaseBuild.Wait(TimeSpan.FromSeconds(20)); } };
            var build = e.BuildStartPlanAsync(view.Digest, "human");
            await inside.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var edit = e.EditStartPlanAsync(new StartPlanEdit { Op = "rename", Target = "measure:Sales/Total Amount", Value = "Revenue" }, "human");
            await Task.Delay(300);
            Assert.False(edit.IsCompleted, "the edit ran beside the build");
            releaseBuild.Set();
            var built = await build.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(built.Built, built.Refused);
            StartPlanView edited = null;
            try { edited = await edit.WaitAsync(TimeSpan.FromSeconds(20)); } catch (InvalidOperationException) { }
            if (edited != null)
            {
                // Accepted: then it must be in the plan the person can read, and an undo of the build gives them that plan.
                await e.UndoAsync("human");
                var plan = await e.GetStartPlanAsync();
                Assert.Contains(LinesOf(plan), l => l.Item.ObjectRef == "measure:Sales/Revenue");
            }
        }
    }
}
