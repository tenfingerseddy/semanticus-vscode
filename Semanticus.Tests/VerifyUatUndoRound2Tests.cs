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
    /// Verifier, lane m11/uat-undo round 2 (84bba3f2): the Locked bypass attacked as a class (D1), an approval tried on a
    /// different or larger write (D2), group-by deletes taken back in every order (D3), and the join's J1 item (an
    /// assistant's open_model or create_model with discardUnsaved throwing away the person's unsaved work under Locked).
    /// </summary>
    [Collection("restore-root")]
    public sealed class VerifyUatUndoRound2Tests : IDisposable
    {
        private readonly string _root;
        private readonly string _safeRoot;

        public VerifyUatUndoRound2Tests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "vu2-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(_root);
            AgentPolicyStore.RootOverride = Path.Combine(_root, "policy");
            ApprovalLedger.RootOverride = Path.Combine(_root, "policy");
        }

        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private string WriteModel(string tag)
        {
            var dir = Path.Combine(_root, tag);
            Directory.CreateDirectory(dir);
            var db = new TOM.Database("V2") { CompatibilityLevel = 1604, Model = new TOM.Model { DiscourageImplicitMeasures = true } };
            var sales = new TOM.Table { Name = "Sales", LineageTag = "t-sales" };
            sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region", LineageTag = "c-re" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Code", DataType = TOM.DataType.String, SourceColumn = "Code", LineageTag = "c-co" });
            sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount", LineageTag = "c-am" });
            sales.Measures.Add(new TOM.Measure { Name = "Total", Expression = "SUM(Sales[Amount])", LineageTag = "m-total" });
            sales.Measures.Add(new TOM.Measure { Name = "Margin", Expression = "1", LineageTag = "m-margin" });
            sales.Partitions.Add(new TOM.Partition { Name = "P1", Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" } });
            db.Model.Tables.Add(sales);
            var role = new TOM.ModelRole { Name = "Managers", ModelPermission = TOM.ModelPermission.Read };
            db.Model.Roles.Add(role);
            db.Model.Perspectives.Add(new TOM.Perspective { Name = "View" });
            db.Model.Cultures.Add(new TOM.Culture { Name = "en-US" });
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

        // ---- D2: an approval is that one write ---------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_an_approved_undo_to_one_row_is_not_spent_on_a_longer_walk_to_an_older_row_with_the_same_label()
        {
            var (engine, sessions, _) = await OpenAsync("walk");
            using (engine)
            {
                await engine.SetDescriptionAsync("measure:Sales/Total", "first", "human");
                await engine.SetDescriptionAsync("measure:Sales/Total", "second", "human");
                await engine.SetDescriptionAsync("measure:Sales/Total", "third", "human");
                var rows = (await engine.GetPlanHistoryAsync()).Rows.Where(r => r.Kind == "edit").ToArray();   // newest first
                var newest = rows[0];
                var oldest = rows[2];
                AgentPolicyStore.SetPreset("locked", "human");

                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.UndoToEntryAsync(newest.Id, "agent"));
                var pending = Assert.Single(await engine.ListPendingApprovalsAsync());
                await engine.ApproveAgentActionAsync(pending.Id, "human");   // the person agrees to take back ONE step

                Exception refused = null;
                try { await engine.UndoToEntryAsync(oldest.Id, "agent"); } catch (Exception ex) { refused = ex; }
                var now = await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description);
                Assert.True(refused != null && now == "third",
                    "the approval for undoing one step (\"" + pending.Summary + "\") was spent on a walk of three: the description now reads \"" + now + "\"");
            }
        }

        public static IEnumerable<object[]> PairedWrites() => new[]
        {
            new object[] { "perspective member" },
            new object[] { "role member" },
            new object[] { "column object permission" },
            new object[] { "table permission" },
            new object[] { "synonyms" },
            new object[] { "ai instructions" },
            new object[] { "partition M" },
            new object[] { "role permission" },
            new object[] { "is hidden" },
            new object[] { "display folder" },
            new object[] { "format string" },
        };

        private static Task Write(LocalEngine e, string kind, bool second) => kind switch
        {
            "perspective member" => e.SetPerspectiveMemberAsync("perspective:View", second ? "measure:Sales/Margin" : "measure:Sales/Total", true, "agent"),
            "role member" => e.SetRoleMemberAsync("Managers", second ? "mallory@example.com" : "alice@example.com", true, "agent"),
            "column object permission" => e.SetColumnObjectPermissionAsync("Managers", second ? "column:Sales/Region" : "column:Sales/Amount", "None", "agent"),
            "table permission" => e.SetTablePermissionAsync("Managers", "table:Sales", second ? "[Region] = \"B\"" : "[Region] = \"A\"", "agent"),
            "synonyms" => e.SetSynonymsAsync("measure:Sales/Total", second ? new[] { "cost" } : new[] { "revenue" }, "en-US", "agent"),
            "ai instructions" => e.SetAiInstructionsAsync(second ? "Always answer in French." : "Be brief.", "en-US", "agent"),
            "partition M" => e.SetPartitionMAsync("partition:Sales/P1", second ? "let Source = Web.Contents(\"https://example.com/x\") in Source" : "let Source = 2 in Source", "agent"),
            "role permission" => e.SetRolePermissionAsync("Managers", second ? "Administrator" : "None", "agent"),
            "is hidden" => e.SetObjectPropertyAsync(second ? "column:Sales/Region" : "measure:Sales/Total", "IsHidden", "true", "agent"),
            "display folder" => e.SetDisplayFolderAsync(new[] { second ? "measure:Sales/Margin" : "measure:Sales/Total" }, second ? "Other" : "Money", "agent"),
            "format string" => e.SetMeasureFormatAsync(second ? "measure:Sales/Margin" : "measure:Sales/Total", second ? "0%" : "0.00", "agent"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        public static IEnumerable<object[]> PairedWritesSample_Under_Locked_an_approval() { var all = PairedWrites().ToArray(); return new[] { all[10] }; }
        [Theory]
        [MemberData(nameof(PairedWritesSample_Under_Locked_an_approval))]
        public Task Under_Locked_an_approval_for_one_write_is_not_spent_on_a_different_write_of_the_same_kind_sample(string kind) => Under_Locked_an_approval_for_one_write_is_not_spent_on_a_different_write_of_the_same_kind(kind);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(PairedWrites))]
        public async Task Under_Locked_an_approval_for_one_write_is_not_spent_on_a_different_write_of_the_same_kind(string kind)
        {
            var (engine, sessions, _) = await OpenAsync("pair-" + kind.Replace(' ', '-'));
            using (engine)
            {
                AgentPolicyStore.SetPreset("locked", "human");
                var before = await SnapshotAsync(sessions);
                Exception first = null;
                try { await Write(engine, kind, second: false); } catch (Exception ex) { first = ex; }
                Assert.True(first is InvalidOperationException, kind + ": the first write was not held under Locked: " + first?.GetType().Name + " " + first?.Message);
                Assert.Equal(before, await SnapshotAsync(sessions));
                var pending = Assert.Single(await engine.ListPendingApprovalsAsync());
                await engine.ApproveAgentActionAsync(pending.Id, "human");

                Exception second = null;
                try { await Write(engine, kind, second: true); } catch (Exception ex) { second = ex; }
                Assert.True(second != null && before == await SnapshotAsync(sessions),
                    kind + ": the approval for \"" + pending.Summary + "\" let a different write through");
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_a_refused_write_leaves_a_saved_model_clean_and_the_persons_redo_where_it_was()
        {
            var (engine, sessions, _) = await OpenAsync("trace");
            using (engine)
            {
                await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's words", "human");
                await engine.UndoAsync("human");
                await engine.SaveAsync(null, "BIM", true, "human");
                var stateBefore = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow());
                Assert.False(sessions.Current.HasUnsavedEdits);
                AgentPolicyStore.SetPreset("locked", "human");
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SetDescriptionAsync("measure:Sales/Total", "Sum of sales.", "agent"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.DeleteObjectAsync("table:Sales", "agent"));
                var stateAfter = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow());
                Assert.False(sessions.Current.HasUnsavedEdits, "a refused write left the model marked unsaved");
                Assert.Equal(stateBefore.Steps, stateAfter.Steps);
                Assert.Equal(stateBefore.RedoSteps, stateAfter.RedoSteps);
                await engine.RedoAsync("human");
                Assert.Equal("a person's words", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Margin"].Description));
            }
        }

        // ---- D1: no assistant write reaches the model's files unasked, by any tool ---------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_an_assistants_vpax_export_does_not_overwrite_the_model_file()
        {
            var (engine, sessions, path) = await OpenAsync("vpax");
            using (engine)
            {
                AgentPolicyStore.SetPreset("locked", "human");
                var before = File.ReadAllBytes(path);
                try { await McpTools.ExportVpax(engine, path); } catch (Exception) { }
                Assert.True(before.SequenceEqual(File.ReadAllBytes(path)), "export_vpax wrote over the model's own file under Locked with no ask");
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Under_Locked_no_tool_given_the_model_files_own_path_writes_over_it_unasked()
        {
            var wrote = new List<string>();
            var tried = new List<string>();
            foreach (var (name, method) in LocalEngine.OpSurface.Methods.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var ps = method.GetParameters();
                var pathParams = ps.Where(p => p.ParameterType == typeof(string) && IsPath(p.Name)).ToArray();
                if (pathParams.Length == 0) continue;
                if (name is "open_model" or "create_model" or "git_clone") continue;   // read the path, or J1 below
                var (engine, sessions, path) = await OpenAsync("p-" + tried.Count);
                using (engine)
                {
                    await engine.SetDescriptionAsync("measure:Sales/Margin", "a person's unsaved words", "human");
                    AgentPolicyStore.SetPreset("locked", "human");
                    var before = File.ReadAllBytes(path);
                    var args = ps.Select(p => Arg(p, engine, path)).ToArray();
                    tried.Add(name);
                    try
                    {
                        var r = method.Invoke(null, args);
                        if (r is Task t) { var done = await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(30))); if (done == t) await t; }
                    }
                    catch (Exception) { }
                    if (!File.Exists(path) || !before.SequenceEqual(File.ReadAllBytes(path))) wrote.Add(name);
                }
            }
            Assert.True(tried.Count > 3, "few path tools found: " + string.Join(" ", tried));
            Assert.True(wrote.Count == 0, "Under Locked these tools wrote over the model file unasked: " + string.Join(", ", wrote) + " (tried " + string.Join(" ", tried) + ")");
        }

        private static bool IsPath(string n)
        {
            var l = (n ?? "").ToLowerInvariant();
            return l == "path" || l == "file" || l.EndsWith("path") || l.EndsWith("file");
        }

        private static object Arg(System.Reflection.ParameterInfo p, LocalEngine engine, string modelPath)
        {
            var t = p.ParameterType;
            if (t == typeof(IEngine) || t == typeof(LocalEngine)) return engine;
            if (t == typeof(System.Threading.CancellationToken)) return System.Threading.CancellationToken.None;
            if (t == typeof(string))
            {
                if (IsPath(p.Name)) return modelPath;
                var l = (p.Name ?? "").ToLowerInvariant();
                if (l == "format") return l.Contains("vpax") ? "vpax" : "BIM";
                if (l.Contains("ref")) return "measure:Sales/Total";
                return p.HasDefaultValue ? p.DefaultValue : "x";
            }
            if (t == typeof(bool))
            {
                var l = (p.Name ?? "").ToLowerInvariant();
                return !(l.Contains("dry") || l.Contains("preview") || l.Contains("discard") || l.Contains("cancel"));
            }
            if (p.HasDefaultValue) return p.DefaultValue;
            return t.IsValueType ? Activator.CreateInstance(t) : null;
        }

        // ---- J1 (for the join): discardUnsaved under Locked --------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("open_model")]
        [InlineData("create_model")]
        public async Task Under_Locked_an_assistant_cannot_throw_away_the_persons_unsaved_work_by_opening_or_creating_a_model(string tool)
        {
            var (engine, sessions, path) = await OpenAsync("j1-" + tool);
            using (engine)
            {
                await engine.SetDescriptionAsync("measure:Sales/Total", "a person's unsaved words", "human");
                AgentPolicyStore.SetPreset("locked", "human");
                try
                {
                    if (tool == "open_model") await McpTools.OpenModel(engine, path, discardUnsaved: true);
                    else await McpTools.CreateModel(engine, "Fresh", 1604, discardUnsaved: true);
                }
                catch (Exception) { }
                var kept = sessions.Current != null && await sessions.Current.ReadAsync(m =>
                    m.Tables.Contains("Sales") && m.Tables["Sales"].Measures["Total"].Description == "a person's unsaved words");
                Assert.True(kept, tool + "(discardUnsaved: true) threw away the person's unsaved words under Locked with no ask");
            }
        }

        // ---- U3: the row that reads "undone" after a restart cannot be used to lose the saved words --------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_row_that_reads_undone_after_a_restart_cannot_take_the_saved_words_away_by_either_door()
        {
            var path = WriteModel("u3");
            var sessions = new SessionManager();
            using (var engine = new LocalEngine(sessions, TestEntitlements.Pro))
            {
                await engine.OpenAsync(path);
                await engine.SetDescriptionAsync("measure:Sales/Total", "saved words", "human");
                await engine.SaveAsync(null, null, false, "human");
                await engine.UndoAsync("human");
                await sessions.Ledger.FlushAsync();
            }
            var again = new SessionManager();
            using var next = new LocalEngine(again, TestEntitlements.Pro);
            await next.OpenAsync(path, discardUnsaved: true);
            async Task<string> Words() => await again.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description);
            Assert.Equal("saved words", await Words());
            var row = Assert.Single((await next.GetPlanHistoryAsync()).Rows, r => r.Kind == "edit" && r.Deltas.Any(d => d.Ref == "measure:Sales/Total"));
            Assert.False(row.Undoable);
            Assert.False(row.Redoable);
            var tries = new Func<Task>[]
            {
                () => next.UndoToEntryAsync(row.Id, "human"), () => next.RedoToEntryAsync(row.Id, "human"),
                () => McpTools.UndoChange(next, row.Id), () => McpTools.RedoChange(next, row.Id),
                () => next.UndoAsync("human"), () => next.RedoAsync("agent"),
            };
            foreach (var t in tries)
            {
                try { await t(); } catch (Exception) { }
                Assert.Equal("saved words", await Words());
            }
            await next.SaveAsync(null, null, true, "human");
            Assert.Contains("saved words", File.ReadAllText(path));
        }

        // ---- D3: group-by deletes in every order -------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("grouping column alone")]
        [InlineData("grouping column first, then the grouped one")]
        [InlineData("grouped column first, then the grouping one")]
        public async Task A_deleted_group_by_pair_comes_back_whole_in_any_order_and_survives_a_TMDL_save_and_reopen(string order)
        {
            var (engine, sessions, path) = await OpenAsync("gb-" + order.Length);
            using (engine)
            {
                await sessions.Current.MutateAsync("human", "group by", m =>
                    m.Tables["Sales"].Columns["Region"].GroupByColumns.Add(m.Tables["Sales"].Columns["Code"]));
                var before = await SnapshotAsync(sessions);
                Assert.Contains("groupByColumns", before);
                var refs = order switch
                {
                    "grouping column alone" => new[] { "column:Sales/Code" },
                    "grouping column first, then the grouped one" => new[] { "column:Sales/Code", "column:Sales/Region" },
                    _ => new[] { "column:Sales/Region", "column:Sales/Code" },
                };
                Exception thrown = null;
                var step = "delete";
                try
                {
                    await McpTools.DeleteObjects(engine, refs);
                    step = "read after the delete"; await engine.GetModelPlanAsync();
                    for (var round = 0; round < 2; round++)
                    {
                        step = "undo " + round; await engine.UndoAsync("human");
                        step = "read after undo " + round; await engine.GetModelPlanAsync();
                        await engine.ListRolesAsync();
                        step = "redo " + round; await engine.RedoAsync("human");
                    }
                    step = "last undo (MCP)"; await McpTools.UndoChange(engine);
                    step = "read after the last undo"; await engine.GetModelPlanAsync();
                }
                catch (Exception ex) { thrown = ex; }
                Assert.True(thrown == null, order + ", at " + step + ": " + thrown?.GetType().Name + " " + thrown?.Message + " " + thrown?.StackTrace?.Split('\n').FirstOrDefault());
                Assert.Equal(before, await SnapshotAsync(sessions));
                Assert.Equal(before, await SavedAndReopenedAsync(engine, Path.Combine(Path.GetDirectoryName(path)!, "tmdl")));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_field_parameters_fields_column_cannot_be_deleted_alone_so_the_parameter_stays_whole()
        {
            var (engine, sessions, path) = await OpenAsync("fpcol");
            using (engine)
            {
                await engine.CreateFieldParameterAsync("Pick", new[]
                {
                    new FieldParameterItem { ObjectRef = "measure:Sales/Total" }, new FieldParameterItem { ObjectRef = "column:Sales/Region" },
                }, "human");
                var grouping = await sessions.Current.ReadAsync(m =>
                    m.Tables["Pick"].Columns.First(c => c.GroupByColumns.Count > 0).GroupByColumns.First().Name);
                var before = await SnapshotAsync(sessions);
                await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.DeleteObject(engine, "column:Pick/" + grouping));
                await engine.GetModelPlanAsync();
                Assert.Equal(before, await SnapshotAsync(sessions));
                Assert.Equal(before, await SavedAndReopenedAsync(engine, Path.Combine(Path.GetDirectoryName(path)!, "tmdl")));
            }
        }
    }
}
