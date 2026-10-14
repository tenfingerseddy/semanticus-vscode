using System;
using System.IO;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// D-005: the deploy gate used to refuse a one-measure change because of warnings on objects
    /// this change did not touch. The gate now scores the change set. Older whole-model warnings
    /// become a count, not a block.
    /// </summary>
    public sealed class DeployGateScopeTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static async Task<string> WriteDirtyBimAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.CreateModelAsync("Dirty", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            var canary = await engine.CreateMeasureAsync(t, "UAT Canary", "1 + 1", "human");
            await engine.SetMeasureFormatAsync(canary, "#,0", "human");
            await engine.SetDescriptionAsync(canary,
                "A canary measure used to prove a one-object change does not inherit older warnings.", "human");
            for (var i = 0; i < 8; i++)
                await engine.CreateMeasureAsync(t, "Junk" + i, "1", "human");
            var dt = await engine.CreateTableAsync("Date", "human");
            var dkey = await engine.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await engine.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await engine.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await engine.MarkDateTableAsync(dt, "Date", "human");
            var path = Path.Combine(Path.GetTempPath(), "sem-gate-scope-" + Guid.NewGuid().ToString("N") + ".bim");
            await engine.SaveAsync(path, "bim", false, "human");
            engine.Dispose();
            return path;
        }

        private static async Task<(LocalEngine engine, string path)> OpenDirtyAsync()
        {
            var path = await WriteDirtyBimAsync();
            var engine = new LocalEngine(new SessionManager(), new Pro());
            await engine.OpenAsync(path);
            return (engine, path);
        }

        // The D-005 repro: open a model that already has many warnings, change one clean measure,
        // and the gate must still pass. Older warnings stay visible as a count.
        [Fact]
        public async Task One_measure_change_does_not_block_on_older_whole_model_warnings()
        {
            var (engine, path) = await OpenDirtyAsync();
            using (engine)
            {
                try
                {
                    await engine.SetObjectPropertyAsync("measure:Sales/UAT Canary", "Expression", "2 + 2", "human");
                    var gate = await engine.DeployGateAsync(null, "human");

                    Assert.True(gate.Pass,
                        "a one-measure change must not inherit the model's older warnings; blockers were: "
                        + string.Join(" | ", gate.Blockers));
                    Assert.Empty(gate.Blockers);
                    Assert.True(gate.OlderWarnings >= 8, "older warnings must still be counted, got " + gate.OlderWarnings);
                    Assert.Contains("nothing to fix", gate.CheckLine, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("older warning", gate.CheckLine, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("AI Readiness", gate.CheckLine, StringComparison.Ordinal);
                    Assert.DoesNotContain("\u2014", gate.CheckLine, StringComparison.Ordinal);
                    Assert.DoesNotContain("BPA", gate.CheckLine, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("list_waivers", gate.CheckLine, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal("Gate passed.", gate.Note);
                }
                finally { File.Delete(path); }
            }
        }

        // UAT-S08-1-001. With no edits in the session there is no change set to scope to, so the gate judges the whole
        // model on purpose and still blocks on its findings: a publish then sends the whole model, and nothing says the
        // target already has it. That verdict stays. The defect was the words: the line said "Checks on this change"
        // for a model with no change, so older findings read as if this change had caused them.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task With_no_edits_the_gate_says_it_checked_the_whole_model()
        {
            var (engine, path) = await OpenDirtyAsync();
            using (engine)
            {
                try
                {
                    var gate = await engine.DeployGateAsync(null, "human");
                    Assert.False(gate.Pass);                       // the safety verdict is unchanged
                    Assert.NotEmpty(gate.Blockers);
                    Assert.True(gate.WholeModel);
                    Assert.StartsWith("No edits in this session yet, so the whole model was checked.", gate.CheckLine, StringComparison.Ordinal);
                    Assert.DoesNotContain("this change", gate.CheckLine, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("\u2014", gate.CheckLine, StringComparison.Ordinal);
                    Assert.DoesNotContain("BPA", gate.CheckLine, StringComparison.OrdinalIgnoreCase);

                    var agent = await McpTools.DeployGate(engine);
                    Assert.Equal(gate.CheckLine, agent.CheckLine);
                    Assert.True(agent.WholeModel);

                    await engine.SetObjectPropertyAsync("measure:Sales/UAT Canary", "Expression", "2 + 2", "human");
                    var scoped = await engine.DeployGateAsync(null, "human");
                    Assert.False(scoped.WholeModel);
                    Assert.StartsWith("Checks on this change:", scoped.CheckLine, StringComparison.Ordinal);
                }
                finally { File.Delete(path); }
            }
        }

        // Dual drive: the agent door reads the same scoped gate.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Agent_door_sees_the_same_scoped_gate()
        {
            var (engine, path) = await OpenDirtyAsync();
            using (engine)
            {
                try
                {
                    await engine.SetObjectPropertyAsync("measure:Sales/UAT Canary", "Expression", "2 + 2", "human");
                    var human = await engine.DeployGateAsync(null, "human");
                    var agent = await McpTools.DeployGate(engine);
                    Assert.Equal(human.Pass, agent.Pass);
                    Assert.Equal(human.OlderWarnings, agent.OlderWarnings);
                    Assert.Equal(human.CheckLine, agent.CheckLine);
                    Assert.True(agent.Pass);
                }
                finally { File.Delete(path); }
            }
        }

        // A finding this change introduces still blocks, and names the object.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_problem_this_change_introduces_still_blocks()
        {
            var (engine, path) = await OpenDirtyAsync();
            using (engine)
            {
                try
                {
                    await engine.CreateMeasureAsync("table:Sales", "New Bad", "1", "human");
                    var gate = await engine.DeployGateAsync(null, "human");
                    Assert.False(gate.Pass);
                    Assert.NotEmpty(gate.Blockers);
                    var text = string.Join(" ", gate.Blockers) + " " + gate.CheckLine;
                    Assert.True(
                        text.IndexOf("New Bad", StringComparison.Ordinal) >= 0
                        || text.IndexOf("undescribed", StringComparison.OrdinalIgnoreCase) >= 0,
                        "the new measure must be why the gate blocked; saw: " + text);
                    Assert.Contains("Checks on this change", gate.CheckLine, StringComparison.Ordinal);
                    Assert.Contains("AI Readiness", gate.CheckLine, StringComparison.Ordinal);
                    Assert.DoesNotContain("\u2014", gate.CheckLine, StringComparison.Ordinal);
                    Assert.DoesNotContain("list_waivers", gate.CheckLine, StringComparison.OrdinalIgnoreCase);
                }
                finally { File.Delete(path); }
            }
        }

        // D-005 live path: a one-measure commit against the recorded live double does not need an override.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Live_commit_of_one_measure_does_not_need_an_override_for_older_warnings()
        {
            var (engine, path) = await OpenDirtyAsync();
            using (engine)
            {
                try
                {
                    engine.DeployLiveSyncHook = (bim, endpoint, database, commit, _dels) =>
                        new DeployReport
                        {
                            Committed = commit,
                            TotalChanges = 1,
                            Endpoint = endpoint,
                            Database = database,
                            Changes = new[] { "measure:Sales/UAT Canary expression" },
                        };
                    await engine.SetObjectPropertyAsync("measure:Sales/UAT Canary", "Expression", "2 + 2", "human");

                    var preview = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: false, origin: "human");
                    Assert.False(string.IsNullOrEmpty(preview.ConfirmToken));

                    var rep = await engine.DeployLiveAsync(
                        RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName,
                        "azcli", null, null, commit: true, origin: "human",
                        overrideReason: null, confirmToken: preview.ConfirmToken);
                    Assert.True(rep.Committed, rep.Error);
                }
                finally { File.Delete(path); }
            }
        }

        // h17 P4: a model opened live and published back to where it came from has nothing left to save. The Next line said "Save your
        // changes" and the footer said "unsaved" after a good publish. A file model published to a workspace is still unsaved as a file.
        [Fact]
        public async Task A_published_live_model_is_clean_and_a_published_file_model_is_not()
        {
            foreach (var live in new[] { true, false })
            {
                var path = await WriteDirtyBimAsync();
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Pro());
                try
                {
                    await engine.OpenAsync(path);
                    if (live) sessions.Current.LiveOrigin = new LiveOrigin(RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, null, "azcli");
                    engine.DeployLiveSyncHook = (bim, endpoint, database, commit, _dels) =>
                        new DeployReport { Committed = commit, TotalChanges = 1, Endpoint = endpoint, Database = database, Changes = new[] { "measure:Sales/UAT Canary expression" } };
                    await engine.SetObjectPropertyAsync("measure:Sales/UAT Canary", "Expression", "2 + 2", "human");
                    Assert.True((await engine.SessionInfoAsync()).HasUnsavedEdits);

                    var preview = await engine.DeployLiveAsync(RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, "azcli", null, null, commit: false, origin: "human");
                    var rep = await engine.DeployLiveAsync(RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, "azcli", null, null, commit: true, origin: "human",
                        overrideReason: null, confirmToken: preview.ConfirmToken);
                    Assert.True(rep.Committed, rep.Error);

                    Assert.Equal(!live, (await engine.SessionInfoAsync()).HasUnsavedEdits);
                }
                finally { File.Delete(path); }
            }
        }

        // Verifier (v-h17 P4): a publish that left a session edit unwritten (Unmatched: a new data column the metadata deploy cannot
        // create) did not make the live model equal the session. Marking it saved hides lost work, so it must stay unsaved.
        [Fact]
        public async Task A_live_publish_that_left_an_edit_unwritten_stays_unsaved()
        {
            var path = await WriteDirtyBimAsync();
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro());
            try
            {
                await engine.OpenAsync(path);
                sessions.Current.LiveOrigin = new LiveOrigin(RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, null, "azcli");
                engine.DeployLiveSyncHook = (bim, endpoint, database, commit, _dels) =>
                    new DeployReport { Committed = commit, TotalChanges = 1, Endpoint = endpoint, Database = database,
                        Changes = new[] { "measure:Sales/UAT Canary expression" }, Unmatched = new[] { "column:Sales/New Data Column" } };
                await engine.SetObjectPropertyAsync("measure:Sales/UAT Canary", "Expression", "2 + 2", "human");
                var preview = await engine.DeployLiveAsync(RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, "azcli", null, null, commit: false, origin: "human");
                var rep = await engine.DeployLiveAsync(RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, "azcli", null, null, commit: true, origin: "human",
                    overrideReason: null, confirmToken: preview.ConfirmToken);
                Assert.True(rep.Committed, rep.Error);
                Assert.True((await engine.SessionInfoAsync()).HasUnsavedEdits, "an unwritten edit was marked saved");
            }
            finally { File.Delete(path); }
        }

        // Verifier (v-h17 P4, second half): an edit made while the push ran is not in what was written, so it stays unsaved too.
        [Fact]
        public async Task An_edit_made_during_a_live_publish_stays_unsaved()
        {
            var path = await WriteDirtyBimAsync();
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro());
            try
            {
                await engine.OpenAsync(path);
                sessions.Current.LiveOrigin = new LiveOrigin(RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, null, "azcli");
                engine.DeployLiveSyncHook = (bim, endpoint, database, commit, _dels) =>
                {
                    if (commit) engine.SetObjectPropertyAsync("measure:Sales/UAT Canary", "Expression", "3 + 3", "human").GetAwaiter().GetResult();
                    return new DeployReport { Committed = commit, TotalChanges = 1, Endpoint = endpoint, Database = database, Changes = new[] { "measure:Sales/UAT Canary expression" } };
                };
                await engine.SetObjectPropertyAsync("measure:Sales/UAT Canary", "Expression", "2 + 2", "human");
                var preview = await engine.DeployLiveAsync(RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, "azcli", null, null, commit: false, origin: "human");
                var rep = await engine.DeployLiveAsync(RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, "azcli", null, null, commit: true, origin: "human",
                    overrideReason: null, confirmToken: preview.ConfirmToken);
                Assert.True(rep.Committed, rep.Error);
                Assert.True((await engine.SessionInfoAsync()).HasUnsavedEdits, "an edit made during the push was marked saved");
            }
            finally { File.Delete(path); }
        }

        // Verifier (v-h17 P3): the Link holds a slow call open until the engine reports its end. deploy_live gave it no end at all, so the Link
        // stayed "thinking" for two minutes after every call. A preview and a refused write now each end with an activity.
        [Fact]
        public async Task The_assistants_deploy_live_ends_with_an_activity_for_a_preview_and_for_a_refusal()
        {
            var path = await WriteDirtyBimAsync();
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro());
            var seen = new System.Collections.Concurrent.ConcurrentQueue<ActivityEvent>();
            sessions.Bus.Activity += e => { if (e.Kind == "deploy_live") seen.Enqueue(e); };
            try
            {
                await engine.OpenAsync(path);
                sessions.Current.LiveOrigin = new LiveOrigin(RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, null, "azcli");
                engine.DeployLiveSyncHook = (bim, endpoint, database, commit, _dels) =>
                    new DeployReport { Committed = commit, TotalChanges = 1, Endpoint = endpoint, Database = database };
                await engine.SetObjectPropertyAsync("measure:Sales/UAT Canary", "Expression", "2 + 2", "human");
                await McpTools.DeployLive(engine, RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, "azcli", null, commit: false);
                await Assert.ThrowsAnyAsync<Exception>(() => McpTools.DeployLive(engine, RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, "azcli", null, commit: true));
                for (var i = 0; i < 50 && seen.Count < 2; i++) await Task.Delay(20);
                var all = seen.ToArray();
                Assert.Equal(2, all.Length);
                Assert.True(all[0].Ok, "the preview ends ok");
                Assert.False(all[1].Ok, "the refused write ends with an error");
                Assert.False(string.IsNullOrWhiteSpace(all[1].Error));
            }
            finally { File.Delete(path); }
        }

        // Verifier (v-h17 P16): the tree delete asks nothing now, so one Undo must bring back a table with its relationship,
        // the measures that reference it and the role filter on it, exactly.
        [Fact]
        public async Task One_undo_brings_back_a_deleted_table_with_its_relationship_and_references()
        {
            var dir = Path.Combine(Path.GetTempPath(), "vh17-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            var db = new Microsoft.AnalysisServices.Tabular.Database("U") { CompatibilityLevel = 1600, Model = new Microsoft.AnalysisServices.Tabular.Model() };
            var sales = new Microsoft.AnalysisServices.Tabular.Table { Name = "Sales", LineageTag = "t-s" };
            sales.Columns.Add(new Microsoft.AnalysisServices.Tabular.DataColumn { Name = "ProductId", DataType = Microsoft.AnalysisServices.Tabular.DataType.Int64, SourceColumn = "ProductId", LineageTag = "c-spi" });
            sales.Columns.Add(new Microsoft.AnalysisServices.Tabular.DataColumn { Name = "Amount", DataType = Microsoft.AnalysisServices.Tabular.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-am" });
            sales.Measures.Add(new Microsoft.AnalysisServices.Tabular.Measure { Name = "Products Sold", Expression = "COUNTROWS(Product)", LineageTag = "m-ps" });
            sales.Measures.Add(new Microsoft.AnalysisServices.Tabular.Measure { Name = "Names", Expression = "DISTINCTCOUNT(Product[Name])", LineageTag = "m-n" });
            sales.Partitions.Add(new Microsoft.AnalysisServices.Tabular.Partition { Name = "P1", Source = new Microsoft.AnalysisServices.Tabular.MPartitionSource { Expression = "let Source = 1 in Source" } });
            db.Model.Tables.Add(sales);
            var product = new Microsoft.AnalysisServices.Tabular.Table { Name = "Product", LineageTag = "t-p" };
            product.Columns.Add(new Microsoft.AnalysisServices.Tabular.DataColumn { Name = "ProductId", DataType = Microsoft.AnalysisServices.Tabular.DataType.Int64, SourceColumn = "ProductId", LineageTag = "c-ppi", IsKey = true });
            product.Columns.Add(new Microsoft.AnalysisServices.Tabular.DataColumn { Name = "Name", DataType = Microsoft.AnalysisServices.Tabular.DataType.String, SourceColumn = "Name", LineageTag = "c-pn" });
            product.Measures.Add(new Microsoft.AnalysisServices.Tabular.Measure { Name = "Product Count", Expression = "COUNTROWS(Product)", LineageTag = "m-pc" });
            product.Partitions.Add(new Microsoft.AnalysisServices.Tabular.Partition { Name = "P2", Source = new Microsoft.AnalysisServices.Tabular.MPartitionSource { Expression = "let Source = 2 in Source" } });
            db.Model.Tables.Add(product);
            db.Model.Relationships.Add(new Microsoft.AnalysisServices.Tabular.SingleColumnRelationship { Name = "r1", FromColumn = sales.Columns["ProductId"], ToColumn = product.Columns["ProductId"], FromCardinality = Microsoft.AnalysisServices.Tabular.RelationshipEndCardinality.Many, ToCardinality = Microsoft.AnalysisServices.Tabular.RelationshipEndCardinality.One });
            var role = new Microsoft.AnalysisServices.Tabular.ModelRole { Name = "Bikes", ModelPermission = Microsoft.AnalysisServices.Tabular.ModelPermission.Read };
            role.TablePermissions.Add(new Microsoft.AnalysisServices.Tabular.TablePermission { Table = product, FilterExpression = "[Name] = \"Bikes\"" });
            db.Model.Roles.Add(role);
            var path = Path.Combine(dir, "U.bim");
            File.WriteAllText(path, Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(db));
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro());
            try
            {
                await engine.OpenAsync(path);
                var s = sessions.Current;
                Func<Task<string>> json = () => s.ReadAsync(_ => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(s.TomDatabase));
                var before = await json();
                await engine.DeleteObjectAsync("table:Product", "human");
                var gone = await json();
                Assert.False(await s.ReadAsync(_ => s.TomDatabase.Model.Tables.Find("Product") != null), "deleted");
                await engine.UndoAsync("human");
                var after = await json();
                var t = await s.ReadAsync(_ => s.TomDatabase.Model.Tables.Find("Product") != null);
                var rels = await s.ReadAsync(_ => s.TomDatabase.Model.Relationships.Count);
                var m1 = await s.ReadAsync(_ => s.TomDatabase.Model.Tables["Sales"].Measures["Products Sold"].Expression);
                var rf = await s.ReadAsync(_ => s.TomDatabase.Model.Roles["Bikes"].TablePermissions.Find("Product")?.FilterExpression);
                var pc = await s.ReadAsync(_ => s.TomDatabase.Model.Tables.Find("Product")?.Measures.Find("Product Count")?.Expression);
                Assert.True(t, "table back");
                Assert.Equal(1, rels);
                Assert.Equal("COUNTROWS(Product)", m1);
                Assert.Equal("COUNTROWS(Product)", pc);
                Assert.NotNull(rf);
                Assert.Equal(before, after);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void Check_line_copy_has_the_four_states()
        {
            var clean = Semanticus.Analysis.GateBlockerCopy.CheckLine(Array.Empty<string>(), 0);
            Assert.Equal("Checks on this change: nothing to fix.", clean);

            var older = Semanticus.Analysis.GateBlockerCopy.CheckLine(Array.Empty<string>(), 20);
            Assert.Equal(
                "Checks on this change: nothing to fix. The model has 20 older warnings this change did not cause. See them on AI Readiness.",
                older);

            var one = Semanticus.Analysis.GateBlockerCopy.CheckLine(Array.Empty<string>(), 1);
            Assert.Contains("1 older warning", one, StringComparison.Ordinal);
            Assert.DoesNotContain("warnings", one, StringComparison.Ordinal);

            var blocked = Semanticus.Analysis.GateBlockerCopy.CheckLine(
                new[] { "1 warning to fix before this change can ship. Start with: Provide format string for measures (UAT Canary)." },
                20);
            Assert.StartsWith("Checks on this change:", blocked, StringComparison.Ordinal);
            Assert.Contains("UAT Canary", blocked, StringComparison.Ordinal);
            Assert.Contains("20 older warnings", blocked, StringComparison.Ordinal);
            Assert.Contains("AI Readiness", blocked, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2014", blocked, StringComparison.Ordinal);
            Assert.DoesNotContain("BPA", blocked, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("list_waivers", blocked, StringComparison.OrdinalIgnoreCase);
        }

        // 1.5 plan-live (hero19 D13): the Publish page writes through the compare path (apply_model_diff), not deploy_live. A person's publish of
        // everything pending to the model's own live source leaves nothing to save, so the footer stops saying unsaved. A publish of only part of it does not.
        [Fact]
        public async Task The_Publish_page_write_of_every_change_to_the_live_source_leaves_the_model_saved_and_a_partial_one_does_not()
        {
            foreach (var whole in new[] { true, false })
            {
                var path = await WriteDirtyBimAsync();
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Pro());
                try
                {
                    await engine.OpenAsync(path);
                    sessions.Current.LiveOrigin = new LiveOrigin(RecordedLiveTarget.Endpoint, RecordedLiveTarget.DatabaseName, null, "azcli");
                    var live = Microsoft.AnalysisServices.Tabular.JsonSerializer.DeserializeDatabase(File.ReadAllText(path), null, Microsoft.AnalysisServices.CompatibilityMode.PowerBI);
                    engine.WorkspaceSnapshotHook = () => Task.FromResult(live);
                    engine.WorkspacePushHook = (_, __) => new DeployReport { Committed = true, TotalChanges = 1, SyncedRefs = new[] { "measure:Sales/UAT Canary", "measure:Sales/Junk0" } };
                    await engine.SetObjectPropertyAsync("measure:Sales/UAT Canary", "Expression", "2 + 2", "human");
                    await engine.SetObjectPropertyAsync("measure:Sales/Junk0", "Expression", "5", "human");
                    Assert.True((await engine.SessionInfoAsync()).HasUnsavedEdits);

                    var target = new ModelRef { Kind = "workspace", Endpoint = RecordedLiveTarget.Endpoint, Database = RecordedLiveTarget.DatabaseName, AuthMode = "azcli" };
                    var r = await engine.ApplyDiffAsync(new ModelRef { Kind = "session" }, target,
                        whole ? null : new[] { "measure:Sales/UAT Canary" }, commit: true, "human");
                    Assert.True(r.Applied, r.Error + " " + r.Note);
                    Assert.Equal(!whole, (await engine.SessionInfoAsync()).HasUnsavedEdits);
                }
                finally { File.Delete(path); }
            }
        }

        [Fact]
        public void Scope_match_is_exact_object_or_child_of_a_changed_table()
        {
            var changed = new[] { "measure:Sales/UAT Canary" };
            Assert.True(DeployGateScope.Touches("measure:Sales/UAT Canary", changed, wholeModel: false));
            Assert.False(DeployGateScope.Touches("measure:Sales/Junk0", changed, wholeModel: false));
            Assert.False(DeployGateScope.Touches("obj:Model/Model", changed, wholeModel: false));
            Assert.True(DeployGateScope.Touches("obj:Model/Model", changed, wholeModel: true));
            Assert.True(DeployGateScope.Touches("measure:Sales/Junk0", Array.Empty<string>(), wholeModel: true));

            var tableChanged = new[] { "table:Sales" };
            Assert.True(DeployGateScope.Touches("measure:Sales/UAT Canary", tableChanged, wholeModel: false));
            Assert.False(DeployGateScope.Touches("measure:Other/X", tableChanged, wholeModel: false));

            Assert.True(DeployGateScope.ForcesWholeModel(Array.Empty<string>()));
            Assert.True(DeployGateScope.ForcesWholeModel(new[] { "model:" }));
            Assert.False(DeployGateScope.ForcesWholeModel(new[] { "measure:Sales/UAT Canary" }));
        }
    }
}
