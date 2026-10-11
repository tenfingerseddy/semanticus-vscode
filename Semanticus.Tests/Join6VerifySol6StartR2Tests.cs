using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// M11 join 6, the checker's re-check of fix round 2 on the spec-first start (Join6Sol6StartRound2Tests). Attacks, not the builder's own cases:
    /// S1's class (more orders of permission, approval and Jev; bookkeeping written by real engine calls; a person's own edits still stop the build),
    /// the bookkeeping list never hides a build write, and the time-measure Drop in the orders the builder did not try, on both doors, Free and Pro.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Join6VerifySol6StartR2Tests : GateFixtureBase
    {
        private const string Server = "probe.datawarehouse.fabric.microsoft.com", Db = "Warehouse";
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public Join6VerifySol6StartR2Tests(RestoreRootFixture fixture) : base(fixture)
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
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct) => Task.FromResult(DecisionAnswers.NotDecided(request, "silent oracle"));
        }

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };

        private static FabricSchema Schema() => new FabricSchema
        {
            Server = Server, Database = Db,
            Tables = new[]
            {
                new FabricTable { Schema = "dbo", Name = "Sales", KeyColumns = new[] { "Id" }, Columns = new[] { C("Id", "Int64"), C("DateKey", "Int64"), C("Amount", "Decimal"), C("Cost", "Decimal") } },
                new FabricTable { Schema = "dbo", Name = "DimDate", KeyColumns = new[] { "DateKey" }, Columns = new[] { C("DateKey", "Int64"), C("Date", "DateTime") } },
            },
            ForeignKeys = new[] { new FabricForeignKey { FromSchema = "dbo", FromTable = "Sales", FromColumn = "DateKey", ToSchema = "dbo", ToTable = "DimDate", ToColumn = "DateKey" } },
        };

        private LocalEngine Engine(SessionManager sm, bool pro, bool jevAvailable = false, IDecisionOracle oracle = null, DecisionBudget budget = null)
        {
            var e = new LocalEngine(sm, pro ? TestEntitlements.Pro : TestEntitlements.Free, Path.Combine(Root, "ws"));
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(Schema());
            e.UseDecisionsForTest(oracle != null ? new DecisionService(oracle, budget) : new DecisionService(new Oracle { Available = jevAvailable }));
            return e;
        }

        private Task<StartPlanView> Start(LocalEngine e, string door) =>
            door == "mcp"
                ? McpTools.StartModelFromSql(e, Server, Db, Path.Combine(Root, "m"), "Model")
                : e.StartModelFromSqlAsync(new StartModelRequest { Name = "Model", Folder = Path.Combine(Root, "m"), Server = Server, Database = Db }, "human");

        private static Task<StartPlanView> Edit(LocalEngine e, string door, string op, string target, string value = null) =>
            door == "mcp" ? McpTools.EditStartPlan(e, op, target, value) : e.EditStartPlanAsync(new StartPlanEdit { Op = op, Target = target, Value = value }, "human");

        private static Task<StartPlanBuildResult> Build(LocalEngine e, string digest, string door) =>
            door == "mcp" ? McpTools.BuildStartPlan(e, digest) : e.BuildStartPlanAsync(digest, "human");

        private static async Task<StartPlanView> Ready(LocalEngine e)
        {
            await e.StartDecideSettledForTest(); await e.StartPreviewSettledForTest();
            return await e.GetStartPlanAsync();
        }

        private static IEnumerable<StartLine> LinesOf(StartPlanView v) => v.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Lines);

        private static List<string> Reviewed(StartPlanView v) =>
            LinesOf(v).Where(l => l.Kept && l.Item.Kind == "create_measure").Select(l => l.Item.ObjectRef).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

        private static Task<List<string>> ModelMeasures(SessionManager sm) =>
            sm.Current.ReadAsync(m => m.AllMeasures.Select(x => "measure:" + x.Table.Name + "/" + x.Name).OrderBy(x => x, StringComparer.Ordinal).ToList());

        private static Task<List<string>> ModelAnnotations(SessionManager sm) =>
            sm.Current.ReadAsync(m => m.GetAnnotations().OrderBy(x => x, StringComparer.Ordinal).ToList());

        private static async Task Consent(LocalEngine e) =>
            await e.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");

        /// <summary>Approve what the person reads, build on the door, and prove the model's measures are the review's (A1's promise, every time).</summary>
        private static async Task<StartPlanBuildResult> ApproveBuildCompare(LocalEngine e, SessionManager sm, string door, StartPlanView view = null)
        {
            view ??= await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            Assert.True(view.CanBuild, view.BlockedBecause);
            var reviewed = Reviewed(view);
            var built = await Build(e, view.Digest, door);
            Assert.True(built.Built, built.Refused);
            Assert.Equal(reviewed, await ModelMeasures(sm));
            return built;
        }

        // ================================================================== S1's class: more orders

        /// <summary>Pro: permission, grant and approval, then the permission is set AGAIN after the approval (a new marker value). The build stands, both doors.</summary>
        [Theory]
        [InlineData(false, "ui")]
        [InlineData(false, "mcp")]
        [InlineData(true, "ui")]
        [InlineData(true, "mcp")]
        public async Task V1_Pro_permission_set_again_after_approval_does_not_move_the_build(bool jevSilent, string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, true, jevSilent);
            await Start(e, door);
            await Consent(e);
            var view = await Ready(e);
            view = await e.GrantStartDisclosureAsync(view.Digest, "human");
            view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            await Consent(e);
            var reviewed = Reviewed(view);
            var built = await Build(e, view.Digest, door);
            Assert.True(built.Built, built.Refused);
            Assert.Equal(reviewed, await ModelMeasures(sm));
        }

        /// <summary>Pro: the person takes permission back after the approval. Whatever the build then says, it is never the false "not the one you were shown".</summary>
        [Theory]
        [InlineData("ui")]
        [InlineData("mcp")]
        public async Task V2_Pro_permission_taken_back_after_approval_is_never_called_a_different_model(string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, true);
            await Start(e, door);
            await Consent(e);
            var view = await Ready(e);
            view = await e.GrantStartDisclosureAsync(view.Digest, "human");
            view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            await e.WithdrawConsentAsync("human");
            var built = await Build(e, view.Digest, door);
            Assert.True(built.Built || !built.Refused.Contains("not the one you were shown"), built.Refused);
        }

        /// <summary>The readiness rules and the installed-library list, multi-line, written into the model between the preview and the build: the build stands.</summary>
        [Theory]
        [InlineData(false, "ui")]
        [InlineData(true, "mcp")]
        public async Task V3_other_bookkeeping_written_after_the_preview_does_not_move_the_build(bool pro, string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, pro);
            await Start(e, door);
            if (pro) await Consent(e);
            var view = await Ready(e);
            if (pro) { view = await e.GrantStartDisclosureAsync(view.Digest, "human"); view = await Ready(e); }
            await sm.Current.MutateAsync("human", "rules", m =>
            {
                m.SetAnnotation(CustomReadinessRuleSet.AnnotationName, "{\n  \"rules\": [\n    { \"id\": \"X1\" }\n  ]\n}");
                m.SetAnnotation(DaxLibStore.Annotation, "[\n  { \"PackageId\": \"p\" }\n]");
            });
            var built = await ApproveBuildCompare(e, sm, door, view);
            Assert.True(built.Built, built.Refused);
        }

        /// <summary>A person's own model change between the preview and the build still stops it, and writes nothing: a description on the model, a culture, and
        /// best-practice rules loaded into the model by the engine's own call (an engine-written annotation that is NOT bookkeeping).</summary>
        [Theory]
        [InlineData("description", "ui")]
        [InlineData("description", "mcp")]
        [InlineData("culture", "ui")]
        [InlineData("bpa", "mcp")]
        [InlineData("annotation_named_like_ours", "ui")]
        public async Task V4_a_person_s_own_edit_after_the_preview_still_stops_the_build(string what, string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, door);
            var view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            switch (what)
            {
                case "description": await sm.Current.MutateAsync("human", "d", m => m.Description = "Our sales model"); break;
                case "culture": await sm.Current.MutateAsync("human", "c", m => m.Culture = "en-AU"); break;
                case "bpa": await sm.Current.MutateAsync("human", "bpa", m => m.SetAnnotation("BestPracticeAnalyzer", "[{\"ID\":\"R\"}]")); break;
                case "annotation_named_like_ours": await sm.Current.MutateAsync("human", "a", m => m.SetAnnotation("Semanticus_TeamNote", "ours")); break;
            }
            var refused = await Build(e, view.Digest, door);
            Assert.False(refused.Built);
            Assert.Contains("not the one you were shown", refused.Refused);
            Assert.Equal(0, await sm.Current.ReadAsync(m => m.Tables.Count));
        }

        // ================================================================== no unseen write behind the bookkeeping list

        /// <summary>The build writes none of the bookkeeping annotations (so leaving them out of the compare hides no build write), and every object it writes is reviewed.</summary>
        [Theory]
        [InlineData(false, "ui")]
        [InlineData(false, "mcp")]
        [InlineData(true, "ui")]
        [InlineData(true, "mcp")]
        public async Task V5_the_build_writes_no_bookkeeping_and_nothing_the_review_did_not_list(bool pro, string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, pro);
            await Start(e, door);
            if (pro) await Consent(e);
            var view = await Ready(e);
            if (pro) { view = await e.GrantStartDisclosureAsync(view.Digest, "human"); view = await Ready(e); }
            var before = await ModelAnnotations(sm);
            var trace = "before-approve: " + string.Join(",", before);
            view = await e.ApproveStartPlanAsync(view.Digest);
            trace += " | after-approve: " + string.Join(",", await ModelAnnotations(sm));
            var reviewedTables = LinesOf(view).Where(l => l.Kept && l.Item.Kind == "create_table").Select(l => l.Item.ObjectName).ToList();
            await ApproveBuildCompare(e, sm, door, view);
            var after = await ModelAnnotations(sm);
            trace += " | after-build: " + string.Join(",", after);
            var added = after.Except(before, StringComparer.Ordinal).ToList();
            Assert.True(!added.Intersect(StartPlanProjection.Bookkeeping, StringComparer.Ordinal).Any(), trace);
            foreach (var b in StartPlanProjection.Bookkeeping)
                Assert.Equal(await sm.Current.ReadAsync(m => before.Contains(b) ? m.GetAnnotation(b) : null), await sm.Current.ReadAsync(m => m.GetAnnotation(b)));
            var tables = await sm.Current.ReadAsync(m => m.Tables.Select(t => t.Name).ToList());
            if (reviewedTables.Count > 0) Assert.All(tables, t => Assert.Contains(t, reviewedTables));
            // and the bookkeeping on every object below the model too
            var deep = await sm.Current.ReadAsync(m => m.GetChildrenRecursive(false).OfType<IAnnotationObject>()
                .SelectMany(o => o.GetAnnotations()).Where(n => StartPlanProjection.Bookkeeping.Contains(n)).ToList());
            Assert.Empty(deep);
        }

        // ================================================================== time measures, more orders

        private static string TimeKey(string baseName, string kind) => "create_measure|measure:Sales/" + baseName + " " + kind + "|expression";

        /// <summary>Rename the base first, then drop one of its time measures by its NEW name: review equals model, both doors, Free and Pro.</summary>
        [Theory]
        [InlineData(false, "ui")]
        [InlineData(false, "mcp")]
        [InlineData(true, "ui")]
        [InlineData(true, "mcp")]
        public async Task V6_rename_then_drop_a_time_measure(bool pro, string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, pro);
            await Start(e, door);
            if (pro) await Consent(e);
            await Ready(e);
            await Edit(e, door, "rename", "measure:Sales/Total Amount", "Revenue");
            await Edit(e, door, "drop_line", "measure:Sales/Revenue YTD");
            var view = await Ready(e);
            if (pro) { view = await e.GrantStartDisclosureAsync(view.Digest, "human"); view = await Ready(e); }
            Assert.False(LinesOf(view).Single(l => l.Item.ObjectRef == "measure:Sales/Revenue YTD").Kept);
            await ApproveBuildCompare(e, sm, door, view);
            var model = await ModelMeasures(sm);
            Assert.DoesNotContain("measure:Sales/Revenue YTD", model);
            Assert.Contains("measure:Sales/Revenue QTD", model);
        }

        /// <summary>Drop all six, keep them all again: the digest is the first one and all six are built, both doors, Free and Pro.</summary>
        [Theory]
        [InlineData(false, "ui")]
        [InlineData(false, "mcp")]
        [InlineData(true, "ui")]
        [InlineData(true, "mcp")]
        public async Task V7_drop_all_six_then_keep_all_six(bool pro, string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, pro);
            await Start(e, door);
            if (pro) await Consent(e);
            var first = await Ready(e);
            var kinds = new[] { "YTD", "QTD", "MTD", "PY", "YoY", "YoYPct" };
            foreach (var k in kinds) await Edit(e, door, "drop_line", TimeKey("Total Amount", k));
            var dropped = await Ready(e);
            Assert.Equal(6, LinesOf(dropped).Count(l => l.Derived && !l.Kept));
            foreach (var k in Enumerable.Reverse(kinds)) await Edit(e, door, "keep_line", TimeKey("Total Amount", k));
            var view = await Ready(e);
            Assert.Equal(first.Digest, view.Digest);
            if (pro) { view = await e.GrantStartDisclosureAsync(view.Digest, "human"); view = await Ready(e); }
            await ApproveBuildCompare(e, sm, door, view);
            var model = await ModelMeasures(sm);
            foreach (var k in kinds) Assert.Contains("measure:Sales/Total Amount " + k, model);
        }

        /// <summary>Drop a time measure, drop its base, keep the base again: the time measure the person dropped stays dropped, the review says so, the model agrees.</summary>
        [Theory]
        [InlineData(false, "ui")]
        [InlineData(true, "mcp")]
        public async Task V8_a_time_drop_survives_its_base_being_dropped_and_kept(bool pro, string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, pro);
            await Start(e, door);
            if (pro) await Consent(e);
            await Ready(e);
            await Edit(e, door, "drop_line", TimeKey("Total Amount", "MTD"));
            await Edit(e, door, "drop_line", "measure:Sales/Total Amount");
            var mid = await Ready(e);
            Assert.DoesNotContain(Reviewed(mid), r => r.StartsWith("measure:Sales/Total Amount", StringComparison.Ordinal));
            await Edit(e, door, "keep_line", "measure:Sales/Total Amount");
            var view = await Ready(e);
            Assert.False(LinesOf(view).Single(l => l.Item.ObjectRef == "measure:Sales/Total Amount MTD").Kept);
            if (pro) { view = await e.GrantStartDisclosureAsync(view.Digest, "human"); view = await Ready(e); }
            await ApproveBuildCompare(e, sm, door, view);
            var model = await ModelMeasures(sm);
            Assert.Contains("measure:Sales/Total Amount", model);
            Assert.DoesNotContain("measure:Sales/Total Amount MTD", model);
        }

        /// <summary>Pin on a time measure is refused on the agent door too, by key and by ref, and nothing moves.</summary>
        [Theory]
        [InlineData("key")]
        [InlineData("ref")]
        public async Task V9_pin_on_a_time_measure_is_refused_on_the_agent_door(string how)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, true);
            await Start(e, "mcp");
            var before = await Ready(e);
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => Edit(e, "mcp", "pin", how == "key" ? TimeKey("Total Amount", "PY") : "measure:Sales/Total Amount PY", "1"));
            Assert.Contains("cannot be pinned", ex.Message);
            Assert.Equal(before.Digest, (await Ready(e)).Digest);
        }

        /// <summary>The stored plan (what an engine restart reads back) carries the drop: same digest, same left-out measure.</summary>
        [Fact]
        public async Task V10_a_time_drop_is_in_the_stored_plan()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, "ui");
            await Ready(e);
            await Edit(e, "ui", "drop_line", TimeKey("Total Amount", "YoYPct"));
            var view = await Ready(e);
            var live = e.StartPlanDocForTest();
            var stored = StartPlanStore.Read(live.Id);
            Assert.NotNull(stored);
            Assert.Equal(StartPlanViewBuilder.Digest(live), StartPlanViewBuilder.Digest(stored));
            Assert.DoesNotContain(stored.Manifest.TimeMeasures(), t => t.Name == "Total Amount YoYPct");
            Assert.Contains(stored.Manifest.AllTimeMeasures(), t => t.Name == "Total Amount YoYPct" && t.Dropped);
            Assert.Equal(new[] { "Total Amount YoYPct" }, stored.Manifest.ToSpec().TimeIntelligenceLeftOut);
        }

        // ================================================================== live Jev (JEV_REPLAY_LIVE=1, key from the key file)

        private static string LiveKey()
        {
            var key = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
            if (!string.IsNullOrEmpty(key)) return key;
            var file = Environment.GetEnvironmentVariable("TYPESAFE_ENV_FILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "typesafe", "env");
            var m = Regex.Match(File.ReadAllText(file), @"^\s*(?:export\s+)?[A-Z0-9_]*(?:KEY|TOKEN)[A-Z0-9_]*\s*=\s*[""']?([^""'\s]+)", RegexOptions.Multiline);
            if (!m.Success) throw new InvalidOperationException("No key in the key file.");
            return m.Groups[1].Value;
        }

        /// <summary>The hero journey with Jev really answering, permission after the preview, both doors, plus a time drop: it builds and the model is the review.</summary>
        [JevReplayFact]
        public Task V11_live_Jev_Pro_journey_builds_on_the_ui_door() => LiveJourney("ui");

        [JevReplayFact]
        public Task V11_live_Jev_Pro_journey_builds_on_the_mcp_door() => LiveJourney("mcp");

        private async Task LiveJourney(string door)
        {
            {
                var budget = new DecisionBudget(2_000_000);
                using var jev = new JevDecisionOracle(LiveKey(), budget);
                using var sm = new SessionManager(); using var e = Engine(sm, true, oracle: jev, budget: budget);
                await (door == "mcp"
                    ? McpTools.StartModelFromSql(e, Server, Db, Path.Combine(Root, "m-" + door), "Model" + door)
                    : e.StartModelFromSqlAsync(new StartModelRequest { Name = "Model" + door, Folder = Path.Combine(Root, "m-" + door), Server = Server, Database = Db }, "human"));
                var view = await Ready(e);
                await Consent(e);
                view = await Ready(e);
                view = await e.GrantStartDisclosureAsync(view.Digest, "human");
                view = await Ready(e);
                await Edit(e, door, "drop_line", TimeKey("Total Amount", "QTD"));
                view = await Ready(e);
                await ApproveBuildCompare(e, sm, door, view);
                Assert.DoesNotContain("measure:Sales/Total Amount QTD", await ModelMeasures(sm));
            }
        }
    }
}
