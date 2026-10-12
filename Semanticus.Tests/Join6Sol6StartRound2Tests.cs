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
    /// M11 join 6, second round on the spec-first start: the two journeys the checker proved broken (Join6VerifySol6StartTests S1 and S2) and the time-measure Drop.
    /// S1: the preview and the build compare one projection whatever order permission, approval and Jev's rounds come in, because no marker the engine keeps for itself is in it.
    /// S2: a dropped base measure is in neither the review, the spec nor the build. T: a time measure can be dropped and kept, on both doors, and the build writes what the review keeps.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Join6Sol6StartRound2Tests : GateFixtureBase
    {
        private const string Server = "probe.datawarehouse.fabric.microsoft.com", Db = "Warehouse";
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public Join6Sol6StartRound2Tests(RestoreRootFixture fixture) : base(fixture)
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

        private LocalEngine Engine(SessionManager sm, bool pro, bool jevAvailable = false)
        {
            var e = new LocalEngine(sm, pro ? TestEntitlements.Pro : TestEntitlements.Free, Path.Combine(Root, "ws"));
            e.StartPlanSchemaReaderForTest = (r, o) => Task.FromResult(Schema());
            e.UseDecisionsForTest(new DecisionService(new Oracle { Available = jevAvailable }));
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

        private static Task<List<string>> ModelMeasures(SessionManager sm) =>
            sm.Current.ReadAsync(m => m.AllMeasures.Select(x => "measure:" + x.Table.Name + "/" + x.Name).OrderBy(x => x).ToList());

        private static async Task<StartPlanView> ProBeforeApproval(LocalEngine e, string consentAt)
        {
            var fields = DecisionRegistry.DeclaredFields.ToArray();
            StartPlanView view;
            if (consentAt == "before_preview") { await e.SetConsentAsync(fields, DecisionRegistry.ConsentSchemaVersion, "human"); view = await Ready(e); }
            else { view = await Ready(e); await e.SetConsentAsync(fields, DecisionRegistry.ConsentSchemaVersion, "human"); view = await Ready(e); }
            view = await e.GrantStartDisclosureAsync(view.Digest, "human");
            return await Ready(e);
        }

        // ================================================================== S1: every order of permission, approval and Jev

        /// <summary>The Pro journey with no edit: permission before or after the one preview, Jev off or answering nothing, either door. Each lands, and the model
        /// built is the one the review listed.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("before_preview", false, "ui")]
        [InlineData("before_preview", true, "ui")]
        [InlineData("before_preview", false, "mcp")]
        [InlineData("before_preview", true, "mcp")]
        [InlineData("after_preview", false, "ui")]
        [InlineData("after_preview", true, "ui")]
        [InlineData("after_preview", false, "mcp")]
        [InlineData("after_preview", true, "mcp")]
        public async Task S1_a_Pro_plan_builds_in_every_order_of_permission_and_Jev(string consentAt, bool jevAnswersNothing, string door)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, true, jevAnswersNothing);
            await Start(e, door);
            var view = await ProBeforeApproval(e, consentAt);
            view = await e.ApproveStartPlanAsync(view.Digest);
            Assert.True(view.CanBuild, view.BlockedBecause);
            var reviewed = LinesOf(view).Where(l => l.Kept && l.Item.Kind == "create_measure").Select(l => l.Item.ObjectRef).OrderBy(x => x).ToList();
            var built = await Build(e, view.Digest, door);
            Assert.True(built.Built, built.Refused);
            Assert.Equal(reviewed, await ModelMeasures(sm));
        }

        /// <summary>Permission given after the person approved (a second consent is not needed, but the marker can still land later): the build stands.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task S1_a_marker_written_after_the_approval_does_not_move_the_build()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, "ui");
            var view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            await e.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            var built = await Build(e, view.Digest, "ui");
            Assert.True(built.Built, built.Refused);
        }

        /// <summary>The other engine bookkeeping annotations (the waivers, with a value that runs over several lines) written after the preview never move the build either.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task S1_the_waivers_written_after_the_preview_do_not_move_the_build()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, "ui");
            var view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            await sm.Current.MutateAsync("human", "waivers", m => m.SetAnnotation(WaiverStore.Annotation, "[\n  {\n    \"ruleId\": \"R1\",\n    \"ref\": \"model\"\n  }\n]"));
            var built = await Build(e, view.Digest, "ui");
            Assert.True(built.Built, built.Refused);
        }

        /// <summary>A difference that is the person's own is still a real one: the build stops and writes nothing.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task S1_an_annotation_the_person_wrote_after_the_preview_still_stops_the_build()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, "ui");
            var view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            await sm.Current.MutateAsync("human", "note", m => m.SetAnnotation("TeamNote", "kept by the person"));
            var refused = await Build(e, view.Digest, "ui");
            Assert.False(refused.Built);
            Assert.Contains("not the one you were shown", refused.Refused);
            Assert.Equal(0, await sm.Current.ReadAsync(m => m.Tables.Count));
        }

        [Fact]
        public void S1_the_projection_leaves_out_every_engine_bookkeeping_annotation_even_one_that_runs_over_lines()
        {
            var a = Path.Combine(Path.GetTempPath(), "sfra" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var b = Path.Combine(Path.GetTempPath(), "sfrb" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(a); Directory.CreateDirectory(b);
                var head = "model Model\n\tculture: en-US\n\n\tannotation Keep = yes\n\n";
                var multi = "\tannotation Semanticus_Waivers = ```\n\t\t\t[\n\t\t\t  { \"a\": 1 }\n\t\t\t]\n\t\t\t```\n\n";
                var tail = "\tannotation SourceKeep = after\n\n";
                File.WriteAllText(Path.Combine(a, "model.tmdl"), head + "\tannotation Semanticus_ConsentId = abc\n\n" + multi + "\tannotation Semanticus_ReadinessRules = x\n\n\tannotation Semanticus_DaxLibInstalled = y\n\n" + tail);
                File.WriteAllText(Path.Combine(b, "model.tmdl"), head + tail);
                Assert.Equal(StartPlanProjection.OfFolder(a), StartPlanProjection.OfFolder(b));
                Assert.DoesNotContain("Semanticus_", StartPlanProjection.OfFolder(a));
                Assert.Contains("annotation SourceKeep = after", StartPlanProjection.OfFolder(a));   // what follows a taken-out value is still there
                File.WriteAllText(Path.Combine(b, "model.tmdl"), head + tail.Replace("after", "later"));
                Assert.NotEqual(StartPlanProjection.OfFolder(a), StartPlanProjection.OfFolder(b));   // a real difference is never ignored
            }
            finally
            {
                try { Directory.Delete(a, true); } catch { }
                try { Directory.Delete(b, true); } catch { }
            }
        }

        /// <summary>How the drift guard finds an annotation name in source: the whole quoted name, with underscores and digits (join 7: "[A-Za-z]+" stopped at the first
        /// underscore or digit, so Semanticus_Fake_Marker and Semanticus_Fake2 slipped past unclassified).</summary>
        private const string AnnotationNameScan = "\"(Semanticus_[A-Za-z0-9_]+)\"";

        [Theory]
        [InlineData("Semanticus_FakeMarker")]
        [InlineData("Semanticus_Fake_Marker")]
        [InlineData("Semanticus_Fake2")]
        public void The_drift_guard_sees_a_whole_annotation_name_with_underscores_and_digits(string name)
        {
            var m = Regex.Match("model.SetAnnotation(\"" + name + "\", value);", AnnotationNameScan);
            Assert.True(m.Success && m.Groups[1].Value == name, "the scan read '" + m.Groups[1].Value + "' for " + name);
        }

        /// <summary>The drift guard. Every annotation name the engine's source writes under its own prefix is either left out of the projection (bookkeeping) or declared as
        /// written by a build. A new one that is neither fails here, so a marker cannot start to move the build without somebody deciding which kind it is.</summary>
        [Fact]
        public void Every_engine_annotation_is_classified_for_the_projection()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "Semanticus.sln"))) root = root.Parent;
            Assert.True(root != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var project in new[] { "Semanticus.Engine", "Semanticus.Core", "Semanticus.Analysis" })
                foreach (var f in Directory.EnumerateFiles(Path.Combine(root!.FullName, project), "*.cs", SearchOption.AllDirectories)
                             .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
                    foreach (Match m in Regex.Matches(File.ReadAllText(f), AnnotationNameScan))
                        found.Add(m.Groups[1].Value);
            var known = StartPlanProjection.Bookkeeping.Concat(StartPlanProjection.BuildWritten).Concat(StartPlanProjection.AuditTrail)
                .Concat(new[] { VerifiedEditsStore.ArchivePrefix, VerifiedEditsStore.ArchivePrefix.TrimEnd('_') }).ToList();   // the numbered archive names are built from this prefix (the scan reads the prefix itself, trailing underscore and all)
            var unclassified = found.Where(n => !known.Any(k => string.Equals(k, n, StringComparison.Ordinal) || k.StartsWith(n + "_", StringComparison.Ordinal))).ToList();
            Assert.True(unclassified.Count == 0, "an engine annotation is in neither StartPlanProjection.Bookkeeping nor BuildWritten: " + string.Join(", ", unclassified));
            Assert.Empty(StartPlanProjection.Bookkeeping.Intersect(StartPlanProjection.BuildWritten));
        }

        // ================================================================== S2: a dropped base measure

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(false, "ui", "drop_line")]
        [InlineData(false, "mcp", "drop_line")]
        [InlineData(true, "ui", "drop_line")]
        [InlineData(true, "mcp", "drop_line")]
        [InlineData(false, "ui", "set_pick")]
        [InlineData(false, "mcp", "set_pick")]
        [InlineData(true, "ui", "set_pick")]
        [InlineData(true, "mcp", "set_pick")]
        public async Task S2_a_dropped_base_measure_is_in_no_list_and_the_plan_builds(bool pro, string door, string how)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, pro);
            await Start(e, door);
            await Ready(e);
            await Edit(e, door, how, how == "drop_line" ? "measure:Sales/Total Amount" : "Sales/Amount", how == "drop_line" ? null : "none");
            var view = pro ? await ProBeforeApproval(e, "after_preview") : await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            var reviewed = LinesOf(view).Where(l => l.Kept && l.Item.Kind == "create_measure").Select(l => l.Item.ObjectRef).OrderBy(x => x).ToList();
            Assert.DoesNotContain(reviewed, r => r.StartsWith("measure:Sales/Total Amount", StringComparison.Ordinal));
            var built = await Build(e, view.Digest, door);
            Assert.True(built.Built, built.Refused);
            Assert.Equal(reviewed, await ModelMeasures(sm));
            var doc = e.StartPlanDocForTest();
            Assert.DoesNotContain(doc.Manifest.ToSpec().Measures, x => x.Name == "Total Amount");
        }

        // ================================================================== T: a time measure can be dropped

        private static string TimeKey(string kind) => "create_measure|measure:Sales/Total Amount " + kind + "|expression";

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(false, "ui", "key")]
        [InlineData(false, "mcp", "key")]
        [InlineData(true, "ui", "ref")]
        [InlineData(true, "mcp", "ref")]
        public async Task T_a_dropped_time_measure_is_left_out_of_the_review_and_the_build_together(bool pro, string door, string how)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, pro);
            await Start(e, door);
            await Ready(e);
            await Edit(e, door, "drop_line", how == "key" ? TimeKey("YoY") : "measure:Sales/Total Amount YoY");
            var view = pro ? await ProBeforeApproval(e, "after_preview") : await Ready(e);
            var yoy = LinesOf(view).Single(l => l.Item.ObjectRef == "measure:Sales/Total Amount YoY");
            Assert.False(yoy.Kept);
            Assert.True(yoy.Derived);
            Assert.Equal(door == "ui" ? "person" : "assistant", yoy.Item.DroppedBy);
            Assert.Equal(11, LinesOf(view).Count(l => l.Kept && l.Derived));   // two sums, six time measures each, one dropped
            view = await e.ApproveStartPlanAsync(view.Digest);
            var reviewed = LinesOf(view).Where(l => l.Kept && l.Item.Kind == "create_measure").Select(l => l.Item.ObjectRef).OrderBy(x => x).ToList();
            var built = await Build(e, view.Digest, door);
            Assert.True(built.Built, built.Refused);
            var model = await ModelMeasures(sm);
            Assert.Equal(reviewed, model);
            Assert.DoesNotContain("measure:Sales/Total Amount YoY", model);
            Assert.Contains("measure:Sales/Total Amount YoYPct", model);   // only the one the person dropped is gone
            Assert.Contains("measure:Sales/Total Amount YTD", model);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task T_keeping_a_dropped_time_measure_puts_the_plan_back_exactly()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, "ui");
            var before = await Ready(e);
            await Edit(e, "ui", "drop_line", TimeKey("PY"));
            var dropped = await Ready(e);
            Assert.NotEqual(before.Digest, dropped.Digest);
            await Edit(e, "ui", "keep_line", TimeKey("PY"));
            var after = await Ready(e);
            Assert.Equal(before.Digest, after.Digest);
            Assert.True(LinesOf(after).Single(l => l.Item.ObjectRef == "measure:Sales/Total Amount PY").Kept);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task T_a_dropped_time_measure_stays_dropped_through_a_rename_and_goes_with_its_base()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, "ui");
            await Ready(e);
            await Edit(e, "ui", "drop_line", TimeKey("QTD"));
            await Edit(e, "ui", "rename", "measure:Sales/Total Amount", "Revenue");
            var view = await Ready(e);
            Assert.False(LinesOf(view).Single(l => l.Item.ObjectRef == "measure:Sales/Revenue QTD").Kept);
            Assert.Equal(11, LinesOf(view).Count(l => l.Kept && l.Derived));   // two sums, six time measures each, one dropped
            await Edit(e, "ui", "drop_line", "measure:Sales/Revenue");
            view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            var built = await Build(e, view.Digest, "ui");
            Assert.True(built.Built, built.Refused);
            Assert.DoesNotContain(await ModelMeasures(sm), x => x.Contains("Revenue") || x.Contains("Total Amount"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task T_every_time_measure_can_be_dropped_and_the_base_still_builds()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, "ui");
            await Ready(e);
            foreach (var k in new[] { "YTD", "QTD", "MTD", "PY", "YoY", "YoYPct" }) await Edit(e, "ui", "drop_line", TimeKey(k));
            var view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            var built = await Build(e, view.Digest, "ui");
            Assert.True(built.Built, built.Refused);
            var model = await ModelMeasures(sm);
            Assert.Contains("measure:Sales/Total Amount", model);
            Assert.DoesNotContain(model, x => x.StartsWith("measure:Sales/Total Amount ", StringComparison.Ordinal));
        }

        /// <summary>A formula cannot be pinned on a time measure: the engine says so in plain words and nothing moves.</summary>
        [Fact]
        public async Task T_pinning_a_time_measure_is_refused_in_plain_words_and_changes_nothing()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, "ui");
            var before = await Ready(e);
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => e.EditStartPlanAsync(new StartPlanEdit { Op = "pin", Target = TimeKey("YTD"), Value = "1" }, "human"));
            Assert.Contains("cannot be pinned", refused.Message);
            Assert.Equal(before.Digest, (await Ready(e)).Digest);
        }
    }
}
