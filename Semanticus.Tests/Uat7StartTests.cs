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
    /// M11 join 7, Sol's UAT of the start journeys (UAT-J7-S1, UAT-J7-S2). Each test was written before its fix.
    /// Item 1 (UAT-J7-S2-008): the review shows every property the build writes, not only the object set.
    /// </summary>
    [Collection("restore-root")]
    public sealed class Uat7StartTests : GateFixtureBase
    {
        private const string Server = "probe.datawarehouse.fabric.microsoft.com", Db = "Warehouse";
        private readonly string _oldConnections = ConnectionRegistry.RootOverride;

        public Uat7StartTests(RestoreRootFixture fixture) : base(fixture)
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
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct) => Task.FromResult(DecisionAnswers.NotDecided(request, "recording oracle"));
        }

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };

        private static FabricSchema Schema() => new FabricSchema
        {
            Server = Server, Database = Db,
            Tables = new[]
            {
                new FabricTable { Schema = "dbo", Name = "Sales", KeyColumns = new[] { "Id" }, Columns = new[] { C("Id", "Int64"), C("DateKey", "Int64"), C("Amount", "Decimal"), C("Note", "String") } },
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

        private Task<StartPlanView> Start(LocalEngine e, string folder, string name = "Model") =>
            e.StartModelFromSqlAsync(new StartModelRequest { Name = name, Folder = folder, Server = Server, Database = Db }, "human");

        private static async Task<StartPlanView> Ready(LocalEngine e)
        {
            await e.StartDecideSettledForTest(); await e.StartPreviewSettledForTest();
            return await e.GetStartPlanAsync();
        }

        private async Task<StartPlanView> GrantPro(LocalEngine e)
        {
            await e.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "include_table", Target = "Payroll", Value = "false" }, "human");
            var view = await Ready(e);
            await e.GrantStartDisclosureAsync(view.Digest, "human");
            return await Ready(e);
        }

        private static IEnumerable<StartLine> LinesOf(StartPlanView v) => v.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Lines);

        private static readonly JsonSerializerOptions Wire = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // ================================================================== item 1: the review shows every property the build writes

        private async Task<(LocalEngine E, SessionManager Sm, StartPlanView View)> ApprovedFree(string name = "m")
        {
            var sm = new SessionManager(); var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, name));
            var view = await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            return (e, sm, view);
        }

        [Fact]
        public async Task Item1_the_review_names_the_folder_of_every_measure_it_builds()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"));
            var view = await Ready(e);
            var measures = LinesOf(view).Where(l => l.Kept && l.Item.Kind == "create_measure").ToList();
            Assert.True(measures.Count >= 7, "the base measure and its six time measures");
            foreach (var l in measures)
            {
                var wire = JsonSerializer.Serialize(l, Wire);
                var folder = l.Item.ObjectName.EndsWith(" YTD") || l.Item.ObjectName.Split(' ').Last() is "QTD" or "MTD" or "PY" or "YoY" or "YoYPct" ? "Time Intelligence" : "Base Measures";
                Assert.True(wire.Contains(folder, StringComparison.Ordinal), l.Item.ObjectName + " does not say its folder " + folder + ": " + wire);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Item1_the_folder_the_review_names_is_the_folder_the_build_writes_on_every_measure(bool pro)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, pro);
            await Start(e, Path.Combine(Root, "m"));
            var view = pro ? await GrantPro(e) : await Ready(e);
            view = await e.ApproveStartPlanAsync(view.Digest);
            var said = LinesOf(view).Where(l => l.Kept && l.Item.Kind == "create_measure")
                .ToDictionary(l => l.Item.ObjectRef, l => JsonSerializer.Serialize(l, Wire));
            var built = await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            var folders = await sm.Current.ReadAsync(m => m.AllMeasures.ToDictionary(x => "measure:" + x.Table.Name + "/" + x.Name, x => x.DisplayFolder));
            Assert.NotEmpty(folders);
            foreach (var (r, folder) in folders)
            {
                Assert.False(string.IsNullOrEmpty(folder), r + " was built with no folder, which the review also must say");
                Assert.True(said[r].Contains(folder, StringComparison.Ordinal), r + " is filed in " + folder + " and its review line does not say so: " + said[r]);
            }
        }

        public static IEnumerable<object[]> UnshownWrites => new[]
        {
            new object[] { "measure folder", "DisplayFolder" },
            new object[] { "column description", "Description" },
            new object[] { "table hidden", "IsHidden" },
            new object[] { "measure format", "FormatString" },
            new object[] { "column sort by", "SortByColumn" },
            new object[] { "column hidden", "ColumnIsHidden" },
            new object[] { "column summarize", "SummarizeBy" },
            new object[] { "column data category", "DataCategory" },
            new object[] { "measure description", "MeasureDescription" },
            new object[] { "column format", "ColumnFormat" },
        };

        [Theory]
        [MemberData(nameof(UnshownWrites))]
        public async Task Item1_a_build_that_sets_a_property_the_review_did_not_show_is_refused_and_writes_nothing(string label, string what)
        {
            var (e, sm, view) = await ApprovedFree();
            using (e) using (sm)
            {
                e.StartPlanBuildSeamsForTest = new StartPlanBuildSeams
                {
                    AfterStructureForTest = m =>
                    {
                        var sales = m.Tables.First(t => t.Name == "Sales");
                        var measure = sales.Measures.First();
                        var amount = sales.Columns.First(c => c.Name == "Amount");
                        switch (what)
                        {
                            case "DisplayFolder": measure.DisplayFolder = "Secret folder"; break;
                            case "Description": amount.Description = "A note nobody saw"; break;
                            case "IsHidden": sales.IsHidden = true; break;
                            case "FormatString": measure.FormatString = "0.0000"; break;
                            case "SortByColumn": amount.SortByColumn = sales.Columns.First(c => c.Name == "Note"); break;
                            case "ColumnIsHidden": amount.IsHidden = true; break;
                            case "SummarizeBy": amount.SummarizeBy = AggregateFunction.Average; break;
                            case "DataCategory": amount.DataCategory = "Percentage"; break;
                            case "MeasureDescription": measure.Description = "Unseen words"; break;
                            case "ColumnFormat": amount.FormatString = "0.00%"; break;
                        }
                    },
                };
                var built = await e.BuildStartPlanAsync(view.Digest, "human");
                Assert.False(built.Built, label + " was built although the review never showed it");
                Assert.Contains("did not show", built.Refused);
                Assert.Equal(0, await sm.Current.ReadAsync(m => m.Tables.Count));
            }
        }

        [Fact]
        public async Task Item1_a_property_the_review_does_show_is_set_with_the_value_it_shows()
        {
            var (e, sm, view) = await ApprovedFree();
            using (e) using (sm)
            {
                // The review says Total Amount is filed in "Base Measures"; a build that files it elsewhere is refused even though the folder IS shown (the value differs).
                e.StartPlanBuildSeamsForTest = new StartPlanBuildSeams { AfterStructureForTest = m => m.Tables.First(t => t.Name == "Sales").Measures.First(x => x.Name == "Total Amount").DisplayFolder = "Somewhere else" };
                var built = await e.BuildStartPlanAsync(view.Digest, "human");
                Assert.False(built.Built);
                Assert.Contains("display folder", built.Refused);
                Assert.Equal(0, await sm.Current.ReadAsync(m => m.Tables.Count));
            }
        }

        [Fact]
        public void Item1_every_default_the_check_knows_has_its_own_reason()
        {
            var known = StartPlanReview.KnownDefaults;
            Assert.All(known, k => Assert.False(string.IsNullOrWhiteSpace(k.Reason), k.Id + " has no reason"));
            Assert.Equal(known.Length, known.Select(k => k.Id).Distinct().Count());
            // Sol's triage of 3 Oct names these: Power BI's own import defaults, the synonyms of an object's own name, the key that comes from the reviewed date table.
            foreach (var id in new[] { "summarize-power-bi-default", "synonym-own-name", "date-table-key" })
                Assert.Contains(known, k => k.Id == id);
            Assert.True(StartPlanReview.IsOwnNameSynonym("Net Sales Amount", "net sales amount"));
            Assert.True(StartPlanReview.IsOwnNameSynonym("Sales", "sales"));
            Assert.False(StartPlanReview.IsOwnNameSynonym("Sales", "revenue"));
        }

        [Fact]
        public async Task Item1_a_synonym_no_line_wrote_and_that_is_not_the_objects_own_name_is_refused()
        {
            var (e, sm, view) = await ApprovedFree();
            using (e) using (sm)
            {
                e.StartPlanBuildSeamsForTest = new StartPlanBuildSeams
                {
                    AfterLinesForTest = m =>
                    {
                        // The engine's own create_linguistic_schema core (private), so the culture has a schema to hold a synonym.
                        typeof(LocalEngine).GetMethod("CreateLinguisticSchemaCore", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { m, "en-US" });
                        LsdlSynonyms.SetSynonyms(m.Tables.First(t => t.Name == "Sales"), m.Cultures["en-US"], "turnover");
                    },
                };
                var built = await e.BuildStartPlanAsync(view.Digest, "human");
                Assert.False(built.Built, "a synonym nobody reviewed was built");
                Assert.Contains("turnover", built.Refused);
                Assert.Equal(0, await sm.Current.ReadAsync(m => m.Tables.Count));
            }
        }

        [Fact]
        public async Task Item1_a_synonym_the_person_reviewed_is_built()
        {
            var (e, sm, view) = await ApprovedFree();
            using (e) using (sm)
            {
                // The words go in before approval, so the approval below is for a plan that has them.
                var words = await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Sales", Kind = "synonyms", Text = "turnover, takings" } }, "agent");
                Assert.Equal(1, words.Written);
                await e.StartPreviewSettledForTest();
                var fresh = await e.ApproveStartPlanAsync((await e.GetStartPlanAsync()).Digest);
                var built = await e.BuildStartPlanAsync(fresh.Digest, "human");
                Assert.True(built.Built, built.Refused);
            }
        }

        // ================================================================== item 4: a refused measure never comes back as a Plan row (UAT-J7-S2-009)

        private static string PlanText(ModelPlanView plan) => JsonSerializer.Serialize(plan, Wire);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Item4_a_measure_the_person_refused_in_the_start_plan_is_not_offered_again_by_the_Plan_after_the_build(bool pro)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, pro);
            await Start(e, Path.Combine(Root, "m"));
            var view = pro ? await GrantPro(e) : await Ready(e);
            // The person says no measure for Amount: Total Amount (and the time measures that follow it) are out of the plan.
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "drop_line", Target = "measure:Sales/Total Amount" }, "human");
            view = await Ready(e);
            Assert.DoesNotContain(LinesOf(view), l => l.Kept && l.Item.Kind == "create_measure");
            view = await e.ApproveStartPlanAsync(view.Digest);
            var built = await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            Assert.Equal(0, await sm.Current.ReadAsync(m => m.AllMeasures.Count()));

            var plan = await e.GetModelPlanAsync();
            var text = PlanText(plan);
            Assert.DoesNotContain("Total Amount", text);
            Assert.DoesNotContain(plan.Items, i => i.Id != null && i.Id.StartsWith("starter/", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Item4_undoing_the_build_gives_the_refused_measure_back_to_the_Plan()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"));
            var view = await Ready(e);
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "drop_line", Target = "measure:Sales/Total Amount" }, "human");
            view = await e.ApproveStartPlanAsync((await Ready(e)).Digest);
            Assert.True((await e.BuildStartPlanAsync(view.Digest, "human")).Built);
            Assert.NotEmpty(e.RemovedMeasureRefsForTest(sm.Current));   // the refusal travels with the build, in the model's History
            await e.UndoAsync("human");
            Assert.Equal(0, await sm.Current.ReadAsync(m => m.Tables.Count));
            Assert.Empty(e.RemovedMeasureRefsForTest(sm.Current));      // a build taken back takes the refusal back with it
        }

        // ================================================================== item 2: the person's line controls (UAT-J7-S1-002, -003, S2-001, -005, -006, -007)

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Item2_every_line_the_page_can_press_has_its_own_id(bool pro)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, pro);
            await Start(e, Path.Combine(Root, "m"));
            var view = pro ? await GrantPro(e) : await Ready(e);
            await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "Every sale." } }, "agent");
            view = await Ready(e);
            var ids = LinesOf(view).Select(l => l.Item.Id).ToList();
            Assert.True(ids.Count >= 10, "the plan has tables, a link, the date table, measures and words");
            Assert.All(ids, id => Assert.False(string.IsNullOrEmpty(id), "a line with no id shares one open state with every other"));
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public async Task Item2_drop_and_keep_by_the_id_the_page_sends_work_on_every_kind_of_line()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"));
            await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "Every sale." } }, "agent");
            var view = await Ready(e);
            async Task<StartPlanView> Press(string op, StartLine l) => await e.EditStartPlanAsync(new StartPlanEdit { Op = op, Target = l.Item.Id }, "human");
            StartLine Find(StartPlanView v, Func<StartLine, bool> f) => LinesOf(v).Single(f);

            // a stored line (the assistant's words), the one Sol pressed
            var words = Find(view, l => l.Item.Kind == "set_description");
            view = await Press("drop_line", words);
            Assert.False(Find(view, l => l.Item.Kind == "set_description").Kept);
            Assert.Equal("person", Find(view, l => l.Item.Kind == "set_description").DecidedBy);
            view = await Press("keep_line", Find(view, l => l.Item.Kind == "set_description"));
            Assert.True(Find(view, l => l.Item.Kind == "set_description").Kept);

            // a table line
            view = await Press("drop_line", Find(view, l => l.Item.Kind == "include_table" && l.Item.ObjectName == "Payroll"));
            Assert.False(Find(view, l => l.Item.Kind == "include_table" && l.Item.ObjectName == "Payroll").Kept);
            view = await Press("keep_line", Find(view, l => l.Item.Kind == "include_table" && l.Item.ObjectName == "Payroll"));
            Assert.True(Find(view, l => l.Item.Kind == "include_table" && l.Item.ObjectName == "Payroll").Kept);

            // a link line
            view = await Press("drop_line", Find(view, l => l.Item.Kind == "create_relationship"));
            Assert.False(Find(view, l => l.Item.Kind == "create_relationship").Kept);
            view = await Press("keep_line", Find(view, l => l.Item.Kind == "create_relationship"));
            Assert.True(Find(view, l => l.Item.Kind == "create_relationship").Kept);

            // a time measure, then its base measure (which takes the time measures with it)
            view = await Press("drop_line", Find(view, l => l.Item.ObjectName == "Total Amount YTD"));
            Assert.False(Find(view, l => l.Item.ObjectName == "Total Amount YTD").Kept);
            view = await Press("keep_line", Find(view, l => l.Item.ObjectName == "Total Amount YTD"));
            view = await Press("drop_line", Find(view, l => l.Item.ObjectName == "Total Amount"));
            Assert.False(Find(view, l => l.Item.ObjectName == "Total Amount").Kept);
            view = await Press("keep_line", Find(view, l => l.Item.ObjectName == "Total Amount"));
            Assert.True(Find(view, l => l.Item.ObjectName == "Total Amount").Kept);

            // the date table has no switch: refused in words, nothing changed
            var date = Find(view, l => l.Item.Kind == "mark_date_table");
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => Press("drop_line", date));
            Assert.Contains("date table", refused.Message);
        }

        [Fact]
        public async Task Item2_the_person_can_pin_a_value_on_a_line_by_its_id()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"));
            var view = await Ready(e);
            var time = LinesOf(view).Single(l => l.Item.ObjectName == "Total Amount YTD");
            // A time measure's formula cannot be pinned, and says so; a stored line can.
            await Assert.ThrowsAnyAsync<Exception>(() => e.EditStartPlanAsync(new StartPlanEdit { Op = "pin", Target = time.Item.Id, Value = "x" }, "human"));
            await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "Every sale." } }, "agent");
            view = await Ready(e);
            var words = LinesOf(view).Single(l => l.Item.Kind == "set_description");
            view = await e.EditStartPlanAsync(new StartPlanEdit { Op = "pin", Target = words.Item.Id, Value = "Every sale, tax included." }, "human");
            Assert.Equal("Every sale, tax included.", LinesOf(view).Single(l => l.Item.Kind == "set_description").Item.After);
        }

        [Fact]
        public async Task Item2_the_person_edits_words_and_the_assistant_cannot_write_over_them()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"));
            await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "Every sale." } }, "agent");
            var before = await Ready(e);
            var approved = await e.ApproveStartPlanAsync(before.Digest);
            var edited = await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "One row per order line." } }, "human");
            Assert.Equal(1, edited.Written);
            var line = LinesOf(edited.View).Single(l => l.Item.Kind == "set_description");
            Assert.Equal("person", line.DecidedBy);
            Assert.Equal("One row per order line.", line.Item.After);
            Assert.Equal("One row per order line.", line.Word);
            Assert.True(line.Kept);
            Assert.NotEqual(approved.Digest, edited.View.Digest);
            Assert.Null((await e.GetStartPlanAsync()).Approval);   // an edit clears the approval
            Assert.Equal(before.Words.Total, edited.View.Words.Total);   // the person's words are still counted, not dropped from the total

            var again = await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "Assistant tries again." } }, "agent");
            Assert.Equal(0, again.Written);
            Assert.Contains("your choice stands", again.Refused.Single());
            Assert.Equal("One row per order line.", LinesOf(await e.GetStartPlanAsync()).Single(l => l.Item.Kind == "set_description").Item.After);
        }

        [Fact]
        public async Task Item2_a_plan_with_the_persons_own_words_still_previews_approves_and_builds()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"));
            await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "Every sale." } }, "agent");
            await Ready(e);
            var edited = await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "One row per order line.", Expected = "Every sale." } }, "human");
            Assert.Equal(1, edited.Written);
            var view = await Ready(e);
            Assert.True(e.StartPreviewLastErrorForTest == null, "the preview could not be made: " + e.StartPreviewLastErrorForTest);
            Assert.False(view.Grades.AfterBuildProvisional, "the preview never caught up with the person's words");
            view = await e.ApproveStartPlanAsync(view.Digest);
            var built = await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            Assert.Equal("One row per order line.", await sm.Current.ReadAsync(m => m.Tables["Sales"].Description));
        }

        [Fact]
        public async Task Item2_an_edit_of_words_over_words_written_since_is_refused_and_nothing_is_lost()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"));
            await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "Every sale." } }, "agent");
            await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "Every sale, as the assistant now says." } }, "agent");
            var stale = await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "Mine.", Expected = "Every sale." } }, "human");
            Assert.Equal(0, stale.Written);
            Assert.Contains("changed while you were editing", stale.Refused.Single());
            Assert.Equal("Every sale, as the assistant now says.", LinesOf(await e.GetStartPlanAsync()).Single(l => l.Item.Kind == "set_description").Item.After);
        }

        [Fact]
        public async Task Item2_a_person_editing_words_after_dropping_them_keeps_them_again()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"));
            await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "Every sale." } }, "agent");
            var line = LinesOf(await Ready(e)).Single(l => l.Item.Kind == "set_description");
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "drop_line", Target = line.Item.Id }, "human");
            var edited = await e.WriteStartWordsAsync(new[] { new StartWord { Ref = "table:Sales", Kind = "description", Text = "Back again." } }, "human");
            var now = LinesOf(edited.View).Single(l => l.Item.Kind == "set_description");
            Assert.True(now.Kept);
            Assert.Equal("Back again.", now.Item.After);
        }

        [Fact]
        public async Task Item2_a_rename_from_the_person_changes_the_plan_and_its_review()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"));
            var view = await e.EditStartPlanAsync(new StartPlanEdit { Op = "rename", Target = "column:Sales/Note", Value = "Remark" }, "human");
            var cols = view.Sections.SelectMany(x => x.Groups).Where(g => g.Table == "Sales").SelectMany(g => g.Columns ?? new List<StartColumn>()).ToList();
            Assert.Contains(cols, c => c.Name == "Remark" && c.SourceName == "Note");
            Assert.DoesNotContain(cols, c => c.Name == "Note");
        }

        // ================================================================== item 3: the person's per-table choice, as the assistant has it (UAT-J7-S2-002, -003)

        private static string Source(string table) => "dbo." + table;

        [Fact]
        public async Task Item3_a_table_can_be_left_out_of_the_sample_without_leaving_the_plan_on_both_doors()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, true);
            await Start(e, Path.Combine(Root, "m"));
            var before = await Ready(e);
            var ui = await e.EditStartPlanAsync(new StartPlanEdit { Op = "set_table_sample", Target = "Payroll", Value = "off" }, "human");
            Assert.NotEqual(before.Digest, ui.Digest);   // what Jev may see is part of what is approved
            var group = ui.Sections.Single(x => x.Id == "tables").Groups.Single(g => g.Table == "Payroll");
            Assert.True(group.SampleOff);
            Assert.True(group.Lines.Single().Kept, "the table stays in the plan");
            Assert.All(ui.Sections.Single(x => x.Id == "tables").Groups.Where(g => g.Table != "Payroll"), g => Assert.False(g.SampleOff));
            var agent = await McpTools.EditStartPlan(e, "set_table_sample", "Sales", "off");
            Assert.True(agent.Sections.Single(x => x.Id == "tables").Groups.Single(g => g.Table == "Sales").SampleOff);
            var on = await e.EditStartPlanAsync(new StartPlanEdit { Op = "set_table_sample", Target = "Sales", Value = "on" }, "human");
            Assert.False(on.Sections.Single(x => x.Id == "tables").Groups.Single(g => g.Table == "Sales").SampleOff);
            await Assert.ThrowsAnyAsync<Exception>(() => e.EditStartPlanAsync(new StartPlanEdit { Op = "set_table_sample", Target = "Nope", Value = "off" }, "human"));
        }

        [Fact]
        public async Task Item3_the_grant_sends_no_sample_fact_for_a_table_whose_sample_the_person_refused()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, true);
            await Start(e, Path.Combine(Root, "m"));
            e.SetStartPlanSample(e.StartPlanDocForTest().Id, SampleOf("Sales", "Payroll"));
            await e.SetConsentAsync(DecisionRegistry.DeclaredFields.ToArray(), DecisionRegistry.ConsentSchemaVersion, "human");
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "set_table_sample", Target = "Payroll", Value = "off" }, "human");
            var view = await Ready(e);
            await e.GrantStartDisclosureAsync(view.Digest, "human");
            var cut = await e.SampleForJevAsync(e.StartPlanDocForTest().Id, SampleOf("Sales", "Payroll"));
            Assert.NotNull(cut);
            Assert.Null(cut.Find("dbo", "Sales").NotMeasured);
            Assert.NotNull(cut.Find("dbo", "Payroll").NotMeasured);
            Assert.Empty(cut.Find("dbo", "Payroll").Columns);
            // and the plan's own copy of the grant says the same, in the view
            Assert.Contains("Payroll", e.StartPlanDocForTest().SampleRefused.Single());
        }

        [Fact]
        public void Item3_allowing_a_table_sample_after_the_grant_asks_again_and_refusing_one_does_not()
        {
            var tables = new[] { Source("Sales"), Source("Payroll") };
            var refusedPayroll = new DisclosedScope("s/d", tables, true, new[] { Source("Sales") });
            var allowAll = new DisclosedScope("s/d", tables, true);
            Assert.Equal(new[] { "dbo.sales" }, refusedPayroll.SampleTables);
            Assert.NotEqual(refusedPayroll.Digest, allowAll.Digest);
            Assert.Contains("sample of", StartPlanConsent.WhyNot(refusedPayroll, allowAll, null));
            Assert.Null(StartPlanConsent.WhyNot(allowAll, refusedPayroll, null));   // narrower is never a reason to stop
            // A grant made before the person could choose names no sample tables: it covers every included table, with the digest it always had.
            Assert.Equal(new DisclosedScope("s/d", tables, true).Digest, new DisclosedScope("s/d", tables, true, tables).Digest);
        }

        [Fact]
        public async Task Item3_the_needs_you_band_names_the_tables_and_the_sample_before_the_person_allows_it()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, true);
            await Start(e, Path.Combine(Root, "m"));
            e.SetStartPlanSample(e.StartPlanDocForTest().Id, SampleOf("Sales", "Payroll"));
            await e.EditStartPlanAsync(new StartPlanEdit { Op = "set_table_sample", Target = "Payroll", Value = "off" }, "human");
            var view = await Ready(e);
            var grant = view.NeedsYou.Single(n => n.Kind == "grant");
            Assert.Contains("Sales", grant.Text); Assert.Contains("DimDate", grant.Text); Assert.Contains("Payroll", grant.Text);
            Assert.Contains("counts and patterns", grant.Text);
            Assert.Contains("never the rows", grant.Text);
            Assert.Contains("refused the sample for Payroll", grant.Text);
        }

        private static SourceSampleResult SampleOf(params string[] tables) => new SourceSampleResult
        {
            Server = Server, Database = Db, MeasuredAtUtc = DateTime.UtcNow.ToString("O"),
            Tables = tables.Select(t => new SourceTableFacts { Schema = "dbo", Name = t, Rows = 50, Columns = new[] { new SourceColumnFacts { Source = t == "Sales" ? "Amount" : "GrossPay", Distinct = 31, BlankShare = 0.14 } } }).ToArray(),
        };

        // ================================================================== item 5: friction worth fixing now

        [Fact]
        public async Task Item5_an_undo_of_the_build_tells_the_page_the_plan_is_a_plan_again()
        {
            using var sm = new SessionManager(); using var e = Engine(sm, false);
            await Start(e, Path.Combine(Root, "m"));
            var view = await e.ApproveStartPlanAsync((await Ready(e)).Digest);
            var built = await e.BuildStartPlanAsync(view.Digest, "human");
            Assert.True(built.Built, built.Refused);
            Assert.True((await e.GetStartPlanAsync()).Built);
            var heard = new List<StartPlanView>();
            sm.Bus.StartPlanChanged += v => { lock (heard) heard.Add(v); };
            await e.UndoAsync("human");
            lock (heard) { Assert.NotEmpty(heard); Assert.False(heard.Last().Built, "the page was left saying built"); }
            await e.RedoAsync("human");
            lock (heard) Assert.True(heard.Last().Built);
        }

        [Fact]
        public void Item5_not_sure_is_never_said_beside_a_sure_decision()
        {
            var unsure = new ChangeItem { DecisionWord = "unsure", DecisionConfidence = 0.4 };
            var sure = new ChangeItem { DecisionWord = "sure", DecisionConfidence = 1.0 };
            var likely = new ChangeItem { DecisionWord = "likely", DecisionConfidence = 0.7 };
            Assert.Contains("not sure", StartPlanDecide.LeftOutWhy(unsure));
            Assert.DoesNotContain("not sure", StartPlanDecide.LeftOutWhy(sure));
            Assert.Contains("100%", StartPlanDecide.LeftOutWhy(sure));
            Assert.DoesNotContain("not sure", StartPlanDecide.LeftOutWhy(likely));
            Assert.Contains("not sure", StartPlanDecide.LeftOutWhy(new ChangeItem()));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Item5_the_review_lists_the_column_names_under_each_table(bool pro)
        {
            using var sm = new SessionManager(); using var e = Engine(sm, pro);
            await Start(e, Path.Combine(Root, "m"));
            var view = pro ? await GrantPro(e) : await Ready(e);
            var groups = view.Sections.Single(x => x.Id == "tables").Groups;
            Assert.Equal(new[] { "Id", "DateKey", "Amount", "Note" }, groups.Single(g => g.Table == "Sales").Columns.Select(c => c.Name));
            var amount = groups.Single(g => g.Table == "Sales").Columns.Single(c => c.Name == "Amount");
            Assert.Equal("Decimal", amount.DataType);
            Assert.Null(amount.Summarize);                  // Sum on a number is Power BI's own default: not shown
            Assert.True(groups.Single(g => g.Table == "Sales").Columns.Single(c => c.Name == "Id").Hidden);
            Assert.True(groups.Single(g => g.Table == "Sales").Columns.Single(c => c.Name == "Id").Key);
            // the counts on the table line agree with the list
            foreach (var g in groups.Where(g => g.Lines.Single().Kept))
                Assert.Contains($"({g.Columns.Count(c => c.Included)} of {g.Columns.Count} columns)", g.Lines.Single().Item.Title);
        }
    }
}
