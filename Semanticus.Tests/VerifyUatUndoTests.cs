using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Jev;
using Xunit;
using static Semanticus.Tests.PlanQueueKit;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier, lane m11/uat-undo (cd4418cb), U1 to U5 attacked past the builder's own tests: removals the builder's
    /// thirteen do not cover (OLS on a column, a role with two table permissions, a field parameter), through the MCP
    /// door and "Undo to here", then a TMDL save and reopen; History after a restart once the edit had been saved; a
    /// redone delete under a task; apply_tmdl words on more object kinds.
    /// </summary>
    public sealed class VerifyUatUndoClassTests
    {
        internal static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException(stderr);
            return stdout.Trim();
        }

        internal static string Dir(string tag)
        {
            var dir = Path.Combine(Path.GetTempPath(), "vu-" + tag + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string Write(string dir)
        {
            var db = new TOM.Database("VRoundTrip") { CompatibilityLevel = 1604, Model = new TOM.Model { DiscourageImplicitMeasures = true } };
            var sales = new TOM.Table { Name = "Sales", LineageTag = "t-sales", Description = "one row per sale" };
            sales.Columns.Add(new TOM.DataColumn { Name = "ProductKey", DataType = TOM.DataType.Int64, SourceColumn = "ProductKey", LineageTag = "c-pk" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region", LineageTag = "c-re" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-am", Description = "money" });
            sales.Measures.Add(new TOM.Measure { Name = "Total", Expression = "SUM(Sales[Amount])", LineageTag = "m-total" });
            sales.Partitions.Add(new TOM.Partition { Name = "P1", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            db.Model.Tables.Add(sales);
            var product = new TOM.Table { Name = "Product", LineageTag = "t-product" };
            product.Columns.Add(new TOM.DataColumn { Name = "ProductKey", DataType = TOM.DataType.Int64, SourceColumn = "ProductKey", IsKey = true, LineageTag = "c-ppk" });
            product.Columns.Add(new TOM.DataColumn { Name = "Name", DataType = TOM.DataType.String, SourceColumn = "Name", LineageTag = "c-pn" });
            product.Columns.Add(new TOM.DataColumn { Name = "Cost", DataType = TOM.DataType.Decimal, SourceColumn = "Cost", LineageTag = "c-pc" });
            product.Partitions.Add(new TOM.Partition { Name = "Product", Source = new TOM.MPartitionSource { Expression = "let Source = 3 in Source" } });
            db.Model.Tables.Add(product);
            db.Model.Relationships.Add(new TOM.SingleColumnRelationship
            {
                Name = "rel-sales-product", FromColumn = sales.Columns["ProductKey"], ToColumn = product.Columns["ProductKey"],
                FromCardinality = TOM.RelationshipEndCardinality.Many, ToCardinality = TOM.RelationshipEndCardinality.One,
            });
            // A role with TWO table permissions, each with OLS on a column, an annotation and a filter.
            var role = new TOM.ModelRole { Name = "Managers", ModelPermission = TOM.ModelPermission.Read, Description = "regional" };
            var tp = new TOM.TablePermission { Table = sales, FilterExpression = "Sales[Region] = \"North\"" };
            tp.ColumnPermissions.Add(new TOM.ColumnPermission { Column = sales.Columns["Amount"], MetadataPermission = TOM.MetadataPermission.None });
            tp.Annotations.Add(new TOM.Annotation { Name = "why", Value = "regional managers" });
            role.TablePermissions.Add(tp);
            var tp2 = new TOM.TablePermission { Table = product, FilterExpression = "Product[Name] <> \"Secret\"" };
            tp2.ColumnPermissions.Add(new TOM.ColumnPermission { Column = product.Columns["Cost"], MetadataPermission = TOM.MetadataPermission.None });
            role.TablePermissions.Add(tp2);
            role.Members.Add(new TOM.ExternalModelRoleMember { MemberName = "ann@contoso.com", IdentityProvider = "AzureAD" });
            db.Model.Roles.Add(role);
            var path = Path.Combine(dir, "VRoundTrip.bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
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
                    var items = a.Select(Normal).ToList();
                    var sortedItems = items.OrderBy(i => (i as JsonObject)?["name"]?.ToString() ?? (i as JsonObject)?["table"]?.ToString() ?? i?.ToJsonString(), StringComparer.Ordinal).ToList();
                    return new JsonArray(sortedItems.Select(i => i == null ? null : JsonNode.Parse(i.ToJsonString())).ToArray());
                default:
                    return n == null ? null : JsonNode.Parse(n.ToJsonString());
            }
        }

        // What a TMDL save of the open model reads back as in a fresh engine.
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

        public static IEnumerable<object[]> Removals() => new[]
        {
            "column:Sales/Amount",          // OLS on this column in a role
            "column:Product/Cost",          // OLS on the SECOND table permission's column
            "table:Product",                // the second table permission goes with its table
            "role:Managers",                // a role with two table permissions, both with OLS
            "table:Pick",                   // a field parameter
        }.Select(r => new object[] { r });

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Removals))]
        public async Task A_removal_comes_back_whole_through_the_MCP_door_and_survives_a_TMDL_save_and_reopen(string objRef)
        {
            var dir = Dir("rt");
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(Write(dir));
                await engine.CreateFieldParameterAsync("Pick", new[]
                {
                    new FieldParameterItem { ObjectRef = "measure:Sales/Total" }, new FieldParameterItem { ObjectRef = "column:Sales/Region" },
                }, "human");
                var before = await SnapshotAsync(sessions);
                var reopenedBefore = await SavedAndReopenedAsync(engine, Path.Combine(dir, "before"));

                var r = await McpTools.DeleteObject(engine, objRef);
                Assert.True(r.Changed, objRef + " was not removed");
                var gone = await SnapshotAsync(sessions);
                Assert.NotEqual(before, gone);

                await McpTools.UndoChange(engine);
                await engine.ListRolesAsync();
                await engine.GetModelPlanAsync();
                Assert.Equal(before, await SnapshotAsync(sessions));
                await engine.RedoAsync("agent");
                Assert.Equal(gone, await SnapshotAsync(sessions));
                await McpTools.UndoChange(engine);
                Assert.Equal(before, await SnapshotAsync(sessions));

                Assert.Equal(reopenedBefore, await SavedAndReopenedAsync(engine, Path.Combine(dir, "after")));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_deleted_field_parameter_is_not_lost_when_the_person_takes_the_delete_back()
        {
            var dir = Dir("fp");
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(Write(dir));
                await engine.CreateFieldParameterAsync("Pick", new[]
                {
                    new FieldParameterItem { ObjectRef = "measure:Sales/Total" }, new FieldParameterItem { ObjectRef = "column:Sales/Region" },
                }, "human");
                await engine.DeleteObjectAsync("table:Pick", "human");
                Exception thrown = null;
                try { await engine.UndoAsync("human"); } catch (Exception ex) { thrown = ex; }
                var back = await sessions.Current.ReadAsync(m => m.Tables.Contains("Pick"));
                var plan = await engine.GetModelPlanAsync();   // the model still reads
                Assert.True(thrown == null && back, $"undo threw: {thrown?.Message}; field parameter back: {back}");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Undo_to_here_on_an_emptied_row_filter_after_later_edits_brings_it_back_readable_both_doors()
        {
            var dir = Dir("uth");
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(Write(dir));
                var before = await SnapshotAsync(sessions);
                var r = await engine.SetTablePermissionAsync("Managers", "table:Sales", "", "human");   // empty: removes the permission
                Assert.True(r.Changed);
                var removedAt = sessions.Current.Revision;
                await engine.SetDescriptionAsync("measure:Sales/Total", "later one", "human");
                await engine.SetDescriptionAsync("table:Product", "later two", "agent");
                var entry = (await engine.GetPlanHistoryAsync()).Rows.Single(x => x.Kind == "edit" && x.Revision == removedAt);

                await McpTools.UndoChange(engine, entry.Id);   // the agent's "Undo to here"
                await engine.ListRolesAsync();
                await engine.GetModelPlanAsync();
                Assert.Equal(before, await SnapshotAsync(sessions));
                await engine.RedoToEntryAsync(entry.Id, "human");
                await engine.UndoToEntryAsync(entry.Id, "human");   // the person's "Undo to here"
                var role = Assert.Single(await engine.ListRolesAsync());
                Assert.Equal(2, role.TableFilters.Length);
                Assert.Equal(before, await SnapshotAsync(sessions));
                Assert.Equal(await SavedAndReopenedAsync(engine, Path.Combine(dir, "a")), await SavedAndReopenedAsync(engine, Path.Combine(dir, "b")));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_refresh_policy_switched_off_then_taken_back_by_undo_to_here_survives_a_TMDL_save_and_reopen()
        {
            var dir = Dir("irp");
            try
            {
                var db = new TOM.Database("Irp") { CompatibilityLevel = 1604, Model = new TOM.Model() };
                var fact = new TOM.Table { Name = "Fact", LineageTag = "t-fact" };
                fact.Columns.Add(new TOM.DataColumn { Name = "When", DataType = TOM.DataType.DateTime, SourceColumn = "When", LineageTag = "c-when" });
                fact.Partitions.Add(new TOM.Partition { Name = "Fact", Source = new TOM.MPartitionSource { Expression = "let Source = Table.SelectRows(x, each [When] >= RangeStart and [When] < RangeEnd) in Source" } });
                fact.RefreshPolicy = new TOM.BasicRefreshPolicy
                {
                    RollingWindowGranularity = TOM.RefreshGranularityType.Year, RollingWindowPeriods = 2,
                    IncrementalGranularity = TOM.RefreshGranularityType.Day, IncrementalPeriods = 7,
                    SourceExpression = "let Source = Table.SelectRows(x, each [When] >= RangeStart and [When] < RangeEnd) in Source",
                    PollingExpression = "let x = 1 in x", Mode = TOM.RefreshPolicyMode.Hybrid,
                };
                db.Model.Tables.Add(fact);
                db.Model.Expressions.Add(new TOM.NamedExpression { Name = "RangeStart", Kind = TOM.ExpressionKind.M, Expression = "#datetime(2020, 1, 1, 0, 0, 0) meta [IsParameterQuery=true, Type=\"DateTime\", IsParameterQueryRequired=true]" });
                db.Model.Expressions.Add(new TOM.NamedExpression { Name = "RangeEnd", Kind = TOM.ExpressionKind.M, Expression = "#datetime(2021, 1, 1, 0, 0, 0) meta [IsParameterQuery=true, Type=\"DateTime\", IsParameterQueryRequired=true]" });
                var path = Path.Combine(dir, "irp.bim");
                File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));

                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
                await engine.OpenAsync(path);
                var before = await SnapshotAsync(sessions);
                await engine.SetObjectPropertyAsync("table:Fact", "EnableRefreshPolicy", "false", "agent");
                await engine.SetDescriptionAsync("table:Fact", "later", "human");
                var entry = (await engine.GetPlanHistoryAsync()).Rows.First(x => x.Kind == "edit" && x.Deltas.Any(d => d.Ref == "table:Fact") && !x.Deltas.Any(d => d.Props != null && d.Props.Contains("Description")));
                await engine.UndoToEntryAsync(entry.Id, "human");
                Assert.Equal(before, await SnapshotAsync(sessions));
                // Twice off and back, so a second snapshot does not pick up a stale one.
                await engine.RemoveIncrementalRefreshPolicyAsync("table:Fact", "human");
                await engine.UndoAsync("human");
                await engine.RemoveIncrementalRefreshPolicyAsync("table:Fact", "human");
                await engine.UndoAsync("human");
                Assert.Equal(before, await SnapshotAsync(sessions));
                var reopened = await SavedAndReopenedAsync(engine, Path.Combine(dir, "tmdl"));
                Assert.Contains("\"pollingExpression\"", reopened);
                Assert.Contains("\"incrementalPeriods\":7", reopened);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public async Task An_edit_saved_then_undone_and_not_saved_reads_as_the_reopened_model_has_it()
        {
            // U3 attack: after a restart History must match the MODEL. Saved with the words, then undone without a save:
            // the file still has the words, so a reopened model has them and the row must not read undone.
            var dir = Dir("h3");
            try
            {
                var model = LedgerFixture.Write(dir, "model.bim");
                var sessions = new SessionManager();
                using (var engine = new LocalEngine(sessions, TestEntitlements.Pro))
                {
                    await engine.OpenAsync(model);
                    await engine.SetDescriptionAsync("measure:Sales/M1", "saved words", "human");
                    await engine.SaveAsync(null, null, false, "human");
                    await engine.UndoAsync("human");
                    await sessions.Ledger.FlushAsync();
                }
                var again = new SessionManager();
                using var next = new LocalEngine(again, TestEntitlements.Pro);
                await next.OpenAsync(model, discardUnsaved: true);
                var hasWords = await again.Current.ReadAsync(m => m.Tables["Sales"].Measures["M1"].Description) == "saved words";
                var row = Assert.Single((await next.GetPlanHistoryAsync()).Rows, r => r.Kind == "edit" && r.Deltas.Any(d => d.Ref == "measure:Sales/M1"));
                Assert.Equal(!hasWords, row.Undone);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_edit_undone_then_redone_reads_active_after_a_restart()
        {
            var dir = Dir("h4");
            try
            {
                var model = LedgerFixture.Write(dir, "model.bim");
                var sessions = new SessionManager();
                using (var engine = new LocalEngine(sessions, TestEntitlements.Pro))
                {
                    await engine.OpenAsync(model);
                    await engine.SetDescriptionAsync("measure:Sales/M1", "words", "human");
                    await engine.UndoAsync("human");
                    await engine.RedoAsync("human");
                    await engine.SaveAsync(null, null, false, "human");
                    await sessions.Ledger.FlushAsync();
                }
                var again = new SessionManager();
                using var next = new LocalEngine(again, TestEntitlements.Pro);
                await next.OpenAsync(model);
                Assert.Equal("words", await again.Current.ReadAsync(m => m.Tables["Sales"].Measures["M1"].Description));
                var row = Assert.Single((await next.GetPlanHistoryAsync()).Rows, r => r.Kind == "edit" && r.Deltas.Any(d => d.Ref == "measure:Sales/M1"));
                Assert.False(row.Undone);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_delete_undone_and_then_redone_makes_the_task_gone_again()
        {
            const string Total = "measure:Sales/Total Sales";
            using var f = await GappyAsync("VerifyGoneRedo.bim");
            var id = await AskAsync(f.Engine, "Check the Total Sales format", new[] { Total });
            var bound = f.Engine.BindPlanMemory();
            f.Engine.WritePlanTasks(bound, state => { foreach (var t in state.Tasks) t.Grounds = null; });
            await f.Engine.DeleteObjectAsync(Total, "human");
            await f.Engine.UndoAsync("human");
            await f.Engine.RedoAsync("human");
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.Equal(HandoffRefusal.NotReady, claim.Refusal);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_object_a_new_task_is_to_create_is_not_called_gone_after_a_first_try_is_deleted()
        {
            // A task added today (grounded as usual) names an object to MAKE. A first try is made and deleted again
            // after the task was added. The task is still to make it: "no longer in the model, cannot be done" is wrong.
            using var f = await GappyAsync("VerifyToCreate.bim");
            var id = await AskAsync(f.Engine, "Add a Margin measure", new[] { "measure:Sales/Margin" });
            await f.Engine.CreateMeasureAsync("table:Sales", "Margin", "1", "human");
            await f.Engine.DeleteObjectAsync("measure:Sales/Margin", "human");
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.False(claim.Refusal == HandoffRefusal.NotReady && (claim.Message ?? "").Contains("no longer in the model"), claim.Message);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_grounded_task_on_an_object_deleted_by_apply_tmdl_is_not_claimed()
        {
            const string Total = "measure:Sales/Total Sales";
            using var f = await GappyAsync("VerifyGoneTmdl.bim");
            var id = await AskAsync(f.Engine, "Check the Total Sales format", new[] { Total });
            await f.Engine.DeleteObjectAsync(Total, "agent");
            var claim = await f.Engine.ClaimAssistantRequestAsync(id);
            Assert.Equal(HandoffRefusal.NotReady, claim.Refusal);
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.NotEqual(PlanStateWords.Done, Row(plan, id).StateWord);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Apply_tmdl_with_windows_line_endings_keeps_a_table_doc_comment_and_a_calc_item_one()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("TmdlWordsV", 1604);
            await engine.CreateImportTableAsync("Sales", "let Source = 1 in Source", "human");
            await engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await engine.SetDescriptionAsync("table:Sales", "One row per sale.", "human");
            var script = (await engine.ScriptObjectsAsync(new[] { "table:Sales" }, "tmdl")).Replace("/// One row per sale.", "/// Edited words.").Replace("\n", "\r\n");
            var result = await engine.ApplyTmdlScriptAsync(script, "human");
            Assert.Empty(result.Skipped);
            Assert.Equal("Edited words.", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Description));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Apply_tmdl_of_a_role_keeps_the_descriptions_of_tables_the_script_does_not_mention()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("TmdlWordsW", 1604);
            await engine.CreateImportTableAsync("Sales", "let Source = 1 in Source", "human");
            await engine.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await engine.SetDescriptionAsync("table:Sales", "One row per sale.", "human");
            await engine.SetDescriptionAsync("column:Sales/Amount", "Money.", "human");
            await engine.CreateRoleAsync("Managers", "Read", "human");
            await engine.SetDescriptionAsync("role:Managers", "Regional.", "human");
            // The role's document first, then the table's own: the table's line sits BETWEEN the two documents.
            var role = await engine.ScriptObjectsAsync(new[] { "role:Managers" }, "tmdl");
            var table = await engine.ScriptObjectsAsync(new[] { "table:Sales" }, "tmdl");
            var result = await engine.ApplyTmdlScriptAsync(role + "\n" + table, "human");
            Assert.Empty(result.Skipped);
            Assert.Equal(new[] { "One row per sale.", "Money.", "Regional." }, await sessions.Current.ReadAsync(m => new[]
                { m.Tables["Sales"].Description, m.Tables["Sales"].Columns["Amount"].Description, m.Roles["Managers"].Description }));
        }
    }

    /// <summary>
    /// Verifier, lane m11/uat-undo, U6 (security): under Locked no assistant write reaches the model or its files
    /// unasked, by ANY path. The builder gated the commit point and save_model; these drive the other assistant tools
    /// that save the model to disk on their way (git_commit, create_history_checkpoint, restore_history_checkpoint), and
    /// whether one approval can be spent on a different write than the one the person was asked about.
    /// </summary>
    [Collection("restore-root")]
    public sealed class VerifyUatUndoLockedTests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public VerifyUatUndoLockedTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "vu-locked-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            AgentPolicyStore.RootOverride = _root;
            ApprovalLedger.RootOverride = _root;
        }

        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private static async Task<(LocalEngine engine, SessionManager sessions, string dir, string model)> InGitAsync(string tag)
        {
            var dir = VerifyUatUndoClassTests.Dir(tag);
            var model = LedgerFixture.Write(dir, "model.bim");
            VerifyUatUndoClassTests.Git(dir, "init", "-q");
            VerifyUatUndoClassTests.Git(dir, "config", "user.email", "locked@test.local");
            VerifyUatUndoClassTests.Git(dir, "config", "user.name", "Locked Test");
            VerifyUatUndoClassTests.Git(dir, "config", "commit.gpgsign", "false");
            VerifyUatUndoClassTests.Git(dir, "add", "--", "model.bim");
            VerifyUatUndoClassTests.Git(dir, "commit", "-q", "-m", "initial");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.OpenAsync(model);
            return (engine, sessions, dir, model);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_an_assistant_git_commit_does_not_write_the_model_file_unasked()
        {
            var (engine, sessions, dir, model) = await InGitAsync("lgc");
            try
            {
                using (engine)
                {
                    await engine.SetDescriptionAsync("measure:Sales/M1", "a person's unsaved words", "human");
                    AgentPolicyStore.SetPreset("locked", "human");
                    var onDisk = File.ReadAllText(model);
                    try { await McpTools.GitCommit(engine, "assistant commit", null, true); } catch (InvalidOperationException) { }
                    Assert.Equal(onDisk, File.ReadAllText(model));   // "Write model files" is Ask under Locked
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_an_assistant_checkpoint_does_not_write_the_model_file_unasked()
        {
            var (engine, sessions, dir, model) = await InGitAsync("lcp");
            try
            {
                using (engine)
                {
                    await engine.SetDescriptionAsync("measure:Sales/M1", "a person's unsaved words", "human");
                    AgentPolicyStore.SetPreset("locked", "human");
                    var onDisk = File.ReadAllText(model);
                    try { await engine.CreateHistoryCheckpointAsync("assistant checkpoint", commit: true, "agent"); } catch (InvalidOperationException) { }
                    Assert.Equal(onDisk, File.ReadAllText(model));
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_an_assistant_checkpoint_restore_does_not_rewrite_the_model_unasked()
        {
            var (engine, sessions, dir, model) = await InGitAsync("lrs");
            try
            {
                using (engine)
                {
                    await engine.SetDescriptionAsync("measure:Sales/M1", "first words", "human");
                    var a = await engine.CreateHistoryCheckpointAsync("checkpoint A", commit: true, "human");
                    Assert.True(a.Committed, a.Error);
                    await engine.SetDescriptionAsync("measure:Sales/M1", "second words", "human");
                    var b = await engine.CreateHistoryCheckpointAsync("checkpoint B", commit: true, "human");
                    Assert.True(b.Committed, b.Error);
                    AgentPolicyStore.SetPreset("locked", "human");
                    var onDisk = File.ReadAllText(model);
                    Assert.Contains("second words", onDisk);
                    try { await McpTools.RestoreHistoryCheckpoint(engine, a.Checkpoint.Hash, true); } catch (InvalidOperationException) { }
                    Assert.Equal(onDisk, File.ReadAllText(model));
                    Assert.Equal("second words", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["M1"].Description));
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public async Task Under_Locked_one_approval_is_not_spent_on_a_different_write_than_the_one_asked_about()
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("LockedBind", 1604);
            await engine.CreateTableAsync("Sales", "human");
            await engine.CreateMeasureAsync("table:Sales", "Total", "1", "human");
            await engine.CreateMeasureAsync("table:Sales", "Margin", "2", "human");
            AgentPolicyStore.SetPreset("locked", "human");

            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SetDescriptionAsync("measure:Sales/Total", "Sum of sales.", "agent"));
            var pending = Assert.Single(await engine.ListPendingApprovalsAsync());
            await engine.ApproveAgentActionAsync(pending.Id, "human");

            // The person approved a description on Total. The grant must not write a different text on a different measure.
            try { await engine.SetDescriptionAsync("measure:Sales/Margin", "Something the person never saw.", "agent"); } catch (InvalidOperationException) { }
            Assert.True(string.IsNullOrEmpty(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Margin"].Description)),
                "an approval for one write was spent on another: the request named only \"" + pending.Summary + "\"");
        }
    }
}
