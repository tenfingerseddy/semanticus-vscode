using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane m11/uat-undo, round 3: the classes behind the verifier's five failures, each tried the other ways round.
    /// F1 an approval for a walk is that walk (rows and length), through the engine and the assistant's tool, undo and redo.
    /// F2 undoing a perspective edit, and refusing a delete of a grouped column, leave the model as it was.
    /// F3 every assistant tool that writes to a path asks when the path is a file the model is read from or already exists.
    /// F4 group-by columns come back whole from every kind of delete, in every order.
    /// J1 an assistant cannot throw the person's unsaved work away without the question, by either door.
    /// </summary>
    [Collection("restore-root")]
    public sealed class UatUndoRound3Tests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public UatUndoRound3Tests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "u3r-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(_root);
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
        }

        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private string WriteModel(string tag, bool twoGroupers = false)
        {
            var dir = Path.Combine(_root, tag);
            Directory.CreateDirectory(dir);
            var db = new TOM.Database("R3") { CompatibilityLevel = 1604, Model = new TOM.Model { DiscourageImplicitMeasures = true } };
            var sales = new TOM.Table { Name = "Sales", LineageTag = "t-sales" };
            sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region", LineageTag = "c-re" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Code", DataType = TOM.DataType.String, SourceColumn = "Code", LineageTag = "c-co" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Zone", DataType = TOM.DataType.String, SourceColumn = "Zone", LineageTag = "c-zo" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-am" });
            sales.Measures.Add(new TOM.Measure { Name = "Total", Expression = "SUM(Sales[Amount])", LineageTag = "m-total" });
            sales.Measures.Add(new TOM.Measure { Name = "Margin", Expression = "1", LineageTag = "m-margin" });
            sales.Partitions.Add(new TOM.Partition { Name = "P1", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            db.Model.Tables.Add(sales);
            db.Model.Perspectives.Add(new TOM.Perspective { Name = "View" });
            var path = Path.Combine(dir, "model.bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        private async Task<(LocalEngine engine, SessionManager sessions, string path)> OpenAsync(string tag)
        {
            var path = WriteModel(tag);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(path);
            return (engine, sessions, path);
        }

        private static async Task<string> SnapshotAsync(SessionManager sessions)
        {
            var json = await sessions.Current.ReadAsync(m => TOM.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase,
                new TOM.SerializeOptions { IgnoreTimestamps = true, IgnoreInferredObjects = true, IgnoreInferredProperties = true, IncludeRestrictedInformation = true }));
            var node = JsonNode.Parse(json);
            node!["name"] = "x"; node["id"] = "x";
            return Normal(node).ToJsonString();
        }

        private static JsonNode Normal(JsonNode n)
        {
            switch (n)
            {
                case JsonObject o:
                    var sorted = new JsonObject();
                    foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal).ToList()) sorted[kv.Key] = Normal(kv.Value);
                    return sorted;
                case JsonArray a:
                    return new JsonArray(a.Select(Normal).OrderBy(i => (i as JsonObject)?["name"]?.ToString() ?? i?.ToJsonString(), StringComparer.Ordinal).ToArray());
                default:
                    return n == null ? null : JsonNode.Parse(n.ToJsonString());
            }
        }

        private static async Task<string> SavedAndReopenedAsync(LocalEngine engine, string folder)
        {
            await engine.SaveAsync(folder, "TMDL", true, "human");
            var sessions = new SessionManager();
            using var fresh = new LocalEngine(sessions, TestEntitlements.Pro);
            await fresh.OpenAsync(folder);
            await fresh.ListRolesAsync();
            await fresh.GetModelPlanAsync();
            return await SnapshotAsync(sessions);
        }

        // The only engine an assistant holds: every call it makes is asked about, in words made from the request, before the engine sees it.
        private static IEngine Assistant(LocalEngine engine) => AssistantEngine.ForAssistant(engine);

        private static async Task<string> DescriptionAsync(SessionManager sessions) =>
            await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description);

        // ---- F1: a walk's approval is that walk -------------------------------------------------------------------------

        private async Task<(LocalEngine engine, SessionManager sessions, PlanHistoryRow[] rows)> ThreeEditsAsync(string tag)
        {
            var (engine, sessions, _) = await OpenAsync(tag);
            await engine.SetDescriptionAsync("measure:Sales/Total", "first", "human");
            await engine.SetDescriptionAsync("measure:Sales/Total", "second", "human");
            await engine.SetDescriptionAsync("measure:Sales/Total", "third", "human");
            var rows = (await engine.GetPlanHistoryAsync()).Rows.Where(r => r.Kind == "edit").ToArray();   // newest first
            Assert.Equal(3, rows.Length);
            return (engine, sessions, rows);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_question_for_a_walk_names_the_changes_it_takes_back_and_how_many()
        {
            var (engine, sessions, rows) = await ThreeEditsAsync("words");
            using (engine)
            {
                AgentPolicyStore.SetPreset("locked", "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).UndoToEntryAsync(rows[2].Id, "agent"));
                var asked = Assert.Single(await engine.ListPendingApprovalsAsync());
                Assert.Contains("undo 3 changes", asked.Summary);
                Assert.Contains("third", asked.Summary);
                Assert.Contains("first", asked.Summary);
                Assert.Equal("third", await DescriptionAsync(sessions));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_approved_walk_is_spent_once_and_only_on_the_same_rows_through_both_the_engine_and_the_assistants_tool()
        {
            var (engine, sessions, rows) = await ThreeEditsAsync("both");
            using (engine)
            {
                AgentPolicyStore.SetPreset("locked", "human");
                // Asked for ONE step by the engine, approved; the assistant's tool then asks for three and must be refused.
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).UndoToEntryAsync(rows[0].Id, "agent"));
                await engine.ApproveAgentActionAsync(Assert.Single(await engine.ListPendingApprovalsAsync()).Id, "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.UndoChange(Assistant(engine), rows[2].Id));
                Assert.Equal("third", await DescriptionAsync(sessions));
                // The one step it was approved for goes through, once.
                await McpTools.UndoChange(Assistant(engine), rows[0].Id);
                Assert.Equal("second", await DescriptionAsync(sessions));
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.UndoChange(Assistant(engine), rows[1].Id));
                Assert.Equal("second", await DescriptionAsync(sessions));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_approved_redo_walk_is_not_spent_on_a_longer_redo_walk()
        {
            var (engine, sessions, rows) = await ThreeEditsAsync("redo");
            using (engine)
            {
                await engine.UndoToEntryAsync(rows[2].Id, "human");   // all three taken back
                Assert.True(string.IsNullOrEmpty(await DescriptionAsync(sessions)));
                AgentPolicyStore.SetPreset("locked", "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).RedoToEntryAsync(rows[2].Id, "agent"));   // the oldest: one step
                await engine.ApproveAgentActionAsync(Assert.Single(await engine.ListPendingApprovalsAsync()).Id, "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.RedoChange(Assistant(engine), rows[0].Id));      // the newest: three steps
                Assert.True(string.IsNullOrEmpty(await DescriptionAsync(sessions)));
                await McpTools.RedoChange(Assistant(engine), rows[2].Id);
                Assert.Equal("first", await DescriptionAsync(sessions));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_approved_walk_is_not_spent_after_another_change_has_landed()
        {
            var (engine, sessions, rows) = await ThreeEditsAsync("stale");
            using (engine)
            {
                AgentPolicyStore.SetPreset("locked", "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).UndoToEntryAsync(rows[0].Id, "agent"));
                await engine.ApproveAgentActionAsync(Assert.Single(await engine.ListPendingApprovalsAsync()).Id, "human");
                await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's newer words", "human");   // the model moved since the person looked
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).UndoToEntryAsync(rows[0].Id, "agent"));
                Assert.Equal("third", await DescriptionAsync(sessions));
            }
        }

        // ---- F2: a perspective edit undoes whole; a refused delete of a grouped column leaves it alone -------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("measure:Sales/Total")]
        [InlineData("column:Sales/Region")]
        [InlineData("table:Sales")]
        public async Task Putting_something_in_a_perspective_is_taken_back_whole_by_undo_and_redo(string objRef)
        {
            var (engine, sessions, _) = await OpenAsync("persp-" + objRef.Length);
            using (engine)
            {
                var before = await SnapshotAsync(sessions);
                await engine.SetPerspectiveMemberAsync("perspective:View", objRef, true, "human");
                var after = await SnapshotAsync(sessions);
                Assert.NotEqual(before, after);
                await engine.UndoAsync("human");
                Assert.Equal(before, await SnapshotAsync(sessions));
                await engine.RedoAsync("human");
                Assert.Equal(after, await SnapshotAsync(sessions));
                await engine.UndoAsync("human");
                Assert.Equal(before, await SnapshotAsync(sessions));
                // and the same by taking the member out again
                await engine.SetPerspectiveMemberAsync("perspective:View", objRef, true, "human");
                await engine.SetPerspectiveMemberAsync("perspective:View", objRef, false, "human");
                await engine.UndoAsync("human");
                Assert.Equal(after, await SnapshotAsync(sessions));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_question_for_a_perspective_member_names_the_object_and_the_perspective()
        {
            var (engine, sessions, _) = await OpenAsync("persp-words");
            using (engine)
            {
                AgentPolicyStore.SetPreset("locked", "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => Assistant(engine).SetPerspectiveMemberAsync("perspective:View", "measure:Sales/Total", true, "agent"));
                var asked = Assert.Single(await engine.ListPendingApprovalsAsync());
                Assert.Contains("[Total]", asked.Summary);
                Assert.Contains("View", asked.Summary);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("column:Sales/Code")]
        [InlineData("column:Sales/Region")]
        [InlineData("table:Sales")]
        public async Task Under_Locked_a_refused_delete_of_a_column_that_groups_or_is_grouped_leaves_the_model_and_its_undo_stack_as_it_was(string objRef)
        {
            var (engine, sessions, _) = await OpenAsync("refdel-" + objRef.Length);
            using (engine)
            {
                await sessions.Current.MutateAsync("human", "group by", m => m.Tables["Sales"].Columns["Region"].GroupByColumns.Add(m.Tables["Sales"].Columns["Code"]));
                var before = await SnapshotAsync(sessions);
                var stateBefore = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow());
                AgentPolicyStore.SetPreset("locked", "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.DeleteObjectAsync(objRef, "agent"));
                Assert.Equal(before, await SnapshotAsync(sessions));
                Assert.Equal(stateBefore.Steps, (await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow())).Steps);
                await engine.GetModelPlanAsync();   // and it still reads
            }
        }

        // ---- F3: every assistant tool that writes to a path asks when the file is the model's own or already there ------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_an_assistants_spec_save_and_vpax_export_ask_for_the_models_file_for_a_file_that_exists_and_for_a_new_one()
        {
            var (engine, sessions, path) = await OpenAsync("paths");
            using (engine)
            {
                AgentPolicyStore.SetPreset("locked", "human");
                var modelBytes = File.ReadAllBytes(path);
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SaveSpec(engine, path));
                Assert.Equal(modelBytes, File.ReadAllBytes(path));
                var existing = Path.Combine(_root, "paths", "keep.vpax");
                File.WriteAllText(existing, "the person's file");
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.ExportVpax(engine, existing));
                Assert.Equal("the person's file", File.ReadAllText(existing));
                var asked = await engine.ListPendingApprovalsAsync();
                Assert.Equal(2, asked.Length);
                Assert.All(asked, a => Assert.Equal("DeployFile", a.Capability));
                Assert.Contains(asked, a => a.Summary.Contains("model.bim"));
                // a new path asks too: the Locked card says "even local writes ask first", and every assistant file write is a write
                var fresh = Path.Combine(_root, "paths", "new.vpax");
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.ExportVpax(engine, fresh));
                Assert.False(File.Exists(fresh));
                var asked3 = await engine.ListPendingApprovalsAsync();
                Assert.Equal(3, asked3.Length);
                Assert.Contains(asked3, a => a.Summary.Contains("create a new file"));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_an_approved_write_over_the_models_file_goes_through_once_and_a_link_to_it_is_the_same_file()
        {
            var (engine, sessions, path) = await OpenAsync("approved");
            using (engine)
            {
                AgentPolicyStore.SetPreset("locked", "human");
                var link = Path.Combine(_root, "approved", "alias.vpax");
                File.CreateSymbolicLink(link, path);
                var before = File.ReadAllBytes(path);
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.ExportVpax(engine, link));
                Assert.Equal(before, File.ReadAllBytes(path));
                await engine.ApproveAgentActionAsync(Assert.Single(await engine.ListPendingApprovalsAsync()).Id, "human");
                var result = await McpTools.ExportVpax(engine, link);
                Assert.True(result.Exported, result.Error);
                Assert.NotEqual(before, File.ReadAllBytes(path));   // the person said yes, so it wrote
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.ExportVpax(engine, link));   // and the yes is spent
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_merging_a_model_into_the_models_own_file_asks_first()
        {
            var (engine, sessions, path) = await OpenAsync("merge");
            using (engine)
            {
                var other = WriteModel("merge-src");
                var db = TOM.JsonSerializer.DeserializeDatabase(File.ReadAllText(other));
                db.Model.Tables["Sales"].Measures["Total"].Description = "the other model's words";
                File.WriteAllText(other, TOM.JsonSerializer.SerializeDatabase(db));
                var preview = await McpTools.ApplyModelDiff(engine, targetFile: path, sourceFile: other, commit: false);
                Assert.False(string.IsNullOrEmpty(preview.ConfirmToken), preview.Error);
                AgentPolicyStore.SetPreset("locked", "human");
                var before = File.ReadAllBytes(path);
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.ApplyModelDiff(engine, targetFile: path, sourceFile: other, commit: true, confirmToken: preview.ConfirmToken));
                Assert.Equal(before, File.ReadAllBytes(path));
                Assert.NotEmpty(await engine.ListPendingApprovalsAsync());
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Below_Locked_the_presets_matrix_decides_and_a_new_file_is_never_in_the_way()
        {
            var (engine, sessions, path) = await OpenAsync("standard");
            using (engine)
            {
                AgentPolicyStore.SetPreset("standard", "human");
                var fresh = Path.Combine(_root, "standard", "new.vpax");
                Assert.True((await McpTools.ExportVpax(engine, fresh)).Exported);
                Assert.Empty(await engine.ListPendingApprovalsAsync());   // Write model files is allowed in this preset, so nothing is asked
            }
        }

        // ---- F4: group-by columns come back whole from every kind of delete ---------------------------------------------

        public static IEnumerable<object[]> Deletes() => new[]
        {
            new object[] { "grouper alone", new[] { "column:Sales/Code" } },
            new object[] { "grouped alone", new[] { "column:Sales/Region" } },
            new object[] { "grouper then grouped", new[] { "column:Sales/Code", "column:Sales/Region" } },
            new object[] { "grouped then grouper", new[] { "column:Sales/Region", "column:Sales/Code" } },
            new object[] { "the whole table", new[] { "table:Sales" } },
            new object[] { "one grouper of two", new[] { "column:Sales/Zone" } },
            new object[] { "both groupers", new[] { "column:Sales/Zone", "column:Sales/Code" } },
        };

        /// <summary>Lean suite: the default tier runs one order on one door with no TMDL round trip (the group-by pair comes back whole).
        /// Every order, both doors and the saved-and-reopened check are the sweep.</summary>
        [Fact]
        public Task A_column_that_groups_or_is_grouped_comes_back_whole_from_a_delete_sample() =>
            GroupByAsync("grouper then grouped", new[] { "column:Sales/Code", "column:Sales/Region" }, new[] { "engine" }, tmdlRoundTrip: false);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Deletes))]
        public Task A_column_that_groups_or_is_grouped_comes_back_whole_from_every_delete_by_both_doors_and_a_TMDL_round_trip(string order, string[] refs) =>
            GroupByAsync(order, refs, new[] { "engine", "tool" }, tmdlRoundTrip: true);

        private async Task GroupByAsync(string order, string[] refs, string[] doors, bool tmdlRoundTrip)
        {
            foreach (var door in doors)
            {
                var (engine, sessions, path) = await OpenAsync("gb3-" + door + "-" + order.Length + refs.Length);
                using (engine)
                {
                    await sessions.Current.MutateAsync("human", "group by", m =>
                    {
                        m.Tables["Sales"].Columns["Region"].GroupByColumns.Add(m.Tables["Sales"].Columns["Code"]);
                        m.Tables["Sales"].Columns["Region"].GroupByColumns.Add(m.Tables["Sales"].Columns["Zone"]);
                    });
                    var before = await SnapshotAsync(sessions);
                    async Task<UndoState> State() => await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow());
                    var steps0 = (await State()).Steps;
                    if (door == "tool") await McpTools.DeleteObjects(engine, refs);
                    else foreach (var r in refs) await engine.DeleteObjectAsync(r, "human");
                    await engine.GetModelPlanAsync();
                    await engine.ListRolesAsync();
                    var afterDelete = await SnapshotAsync(sessions);
                    for (var round = 0; round < 3; round++)
                    {
                        while ((await State()).Steps > steps0)
                        {
                            if (door == "tool") await McpTools.UndoChange(engine); else await engine.UndoAsync("human");
                        }
                        await engine.GetModelPlanAsync();
                        Assert.True(before == await SnapshotAsync(sessions), door + " / " + order + ": not whole after undo " + round);
                        if (round == 2) break;
                        while ((await State()).CanRedo)
                        {
                            if (door == "tool") await McpTools.RedoChange(engine); else await engine.RedoAsync("human");
                        }
                        await engine.GetModelPlanAsync();
                        Assert.True(afterDelete == await SnapshotAsync(sessions), door + " / " + order + ": redo is not the delete again, round " + round);
                    }
                    if (tmdlRoundTrip)
                        Assert.True(before == await SavedAndReopenedAsync(engine, Path.Combine(Path.GetDirectoryName(path)!, "tmdl")), door + " / " + order + ": a saved and reopened model differs");
                }
            }
        }

        // ---- J1: discarding the person's unsaved work is the person's call --------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_assistants_open_that_would_discard_unsaved_work_asks_in_plain_words_and_goes_through_once_approved()
        {
            var (engine, sessions, path) = await OpenAsync("j1");
            using (engine)
            {
                await engine.SetDescriptionAsync("measure:Sales/Total", "a person's unsaved words", "human");
                AgentPolicyStore.SetPreset("locked", "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.OpenModel(engine, path, discardUnsaved: true));
                var asked = Assert.Single(await engine.ListPendingApprovalsAsync());
                Assert.Contains("unsaved", asked.Summary);
                Assert.Contains("lost", asked.Summary);
                Assert.Equal("a person's unsaved words", await DescriptionAsync(sessions));
                // Without the flag it still refuses, as it always did, and does not ask.
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.OpenModel(engine, path, discardUnsaved: false));
                Assert.Single(await engine.ListPendingApprovalsAsync());
                await engine.ApproveAgentActionAsync(asked.Id, "human");
                await McpTools.OpenModel(engine, path, discardUnsaved: true);
                Assert.True(string.IsNullOrEmpty(await DescriptionAsync(sessions)));   // the person said yes, so it was thrown away
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_person_and_a_clean_model_are_never_asked_and_the_pipe_door_asks_for_an_assistant_connection()
        {
            var (engine, sessions, path) = await OpenAsync("j1b");
            using (engine)
            {
                AgentPolicyStore.SetPreset("locked", "human");
                await new EngineRpcTarget(engine, "agent").open(path, discardUnsaved: true);   // nothing unsaved: nothing to lose, nothing asked
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                await engine.SetDescriptionAsync("measure:Sales/Total", "a person's unsaved words", "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => new EngineRpcTarget(engine, "agent").open(path, discardUnsaved: true));
                await Assert.ThrowsAsync<InvalidOperationException>(() => new EngineRpcTarget(engine, "agent").createModel("Fresh", 1604, discardUnsaved: true));
                Assert.Equal("a person's unsaved words", await DescriptionAsync(sessions));
                await new EngineRpcTarget(engine, "human").open(path, discardUnsaved: true);   // the person's own door is never asked
                Assert.True(string.IsNullOrEmpty(await DescriptionAsync(sessions)));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Below_Locked_an_assistants_open_with_discard_follows_the_matrix_which_allows_it()
        {
            var (engine, sessions, path) = await OpenAsync("j1c");
            using (engine)
            {
                AgentPolicyStore.SetPreset("standard", "human");
                await engine.SetDescriptionAsync("measure:Sales/Total", "a person's unsaved words", "human");
                await McpTools.OpenModel(engine, path, discardUnsaved: true);
                Assert.Empty(await engine.ListPendingApprovalsAsync());
                Assert.True(string.IsNullOrEmpty(await DescriptionAsync(sessions)));
            }
        }
    }
}
