using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Semanticus.Engine;
using TabularEditor.TOMWrapper.Linguistics;
using TabularEditor.TOMWrapper.Utils;
using Xunit;

namespace Semanticus.Tests
{
    [Collection("restore-root")]
    public sealed class BatchToolsTests : GateFixtureBase
    {
        public BatchToolsTests(RestoreRootFixture fixture) : base(fixture) { }

        // hero16: through the real door (semanticus-mcp -> owner engine over the pipe), every edit_objects item came back
        // "Operation is not valid due to the current state of the object." The pipe is Newtonsoft (RpcServer.ConfigureSerializer),
        // and ObjectEditItem.Set holds System.Text.Json elements, which do not survive that wire.
        [Fact]
        public void Edit_objects_values_survive_the_engine_pipe()
        {
            using var doc = JsonDocument.Parse("{\"name\":\"Customer City\",\"formatString\":\"#,0.00\"}");
            var item = new ObjectEditItem { ObjRef = "column:Customer/City", Set = doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) };
            var ser = new Newtonsoft.Json.JsonSerializer();
            RpcServer.ConfigureSerializer(ser);
            var sw = new System.IO.StringWriter();
            ser.Serialize(sw, item);
            var back = ser.Deserialize<ObjectEditItem>(new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(sw.ToString())));
            Assert.Equal(JsonValueKind.String, back.Set["name"].ValueKind);
            Assert.Equal("Customer City", back.Set["name"].GetString());
        }

        // hero16 N3: the first edit after a model opens paid the health probe's whole baseline scan (0.9 s on 3,000 fields,
        // 8 times a steady edit). It is taken at open now, ahead of any edit, so the first write is as fast as the next.
        [Fact]
        public async Task The_health_baseline_is_ready_before_the_first_edit()
        {
            var (engine, sessions, _) = await OpenAsync("bulk-warm-baseline");
            using (engine)
            {
                var probe = sessions.Current.Observers.OfType<HealthDeltaProbe>().Single();
                await engine.AiReadinessScanAsync();   // the first grade releases the warm-up (it waits for it, reads-itself 12 Oct)
                for (var i = 0; i < 100 && !probe.HasBaseline; i++) await Task.Delay(50);
                await sessions.Current.ReadAsync(_ => 0);
                Assert.True(probe.HasBaseline);
                var result = await engine.WriteWordsAsync(new[] { new WordsWriteItem { ObjRef = "measure:Sales/Total", Synonyms = new[] { "sales amount" } } }, "agent");
                Assert.Equal(1, result.Applied);
            }
        }

        // reads-itself (12 Oct): the grade waited about 2 s behind the warm-up, which was queued on the one writer thread first. A read
        // queued right after open now runs BEFORE the warm-up; the warm-up follows the first grade (or a 10 s grace) and an edit still
        // gets its baseline from BeforeCommit.
        [Fact]
        public async Task The_first_read_runs_before_the_warm_up()
        {
            var (engine, sessions, _) = await OpenAsync("bulk-read-first");
            using (engine)
            {
                var probe = sessions.Current.Observers.OfType<HealthDeltaProbe>().Single();
                var baselineSeenByFirstRead = await sessions.Current.ReadAsync(_ => probe.HasBaseline);
                Assert.False(baselineSeenByFirstRead);
            }
        }

        // hero16 N1's lesson: a unit test that calls LocalEngine directly never crosses the Newtonsoft pipe, so it cannot see a
        // value that does not survive it. This sends every bulk tool through a real RpcServer and RemoteEngine (the MCP door's path)
        // and asserts the model changed, not only that nothing threw.
        [Fact]
        public async Task Every_bulk_tool_applies_through_the_real_engine_pipe()
        {
            var (engine, sessions, _) = await OpenAsync("bulk-pipe");
            using (engine)
            {
                await sessions.Current.MutateAsync("human", "fixture", m =>
                {
                    m.AddPerspective("Business");
                    m.AddRole("Reader");
                    m.Tables["Sales"].AddMeasure("Second", "1");
                });
                await engine.WaiveFindingsAsync(new[] { new FindingWaiverItem { System = "air", RuleId = "DESC-MEASURE", ObjRef = "measure:Sales/Second", Reason = "Kept." } }, "human");
                var pipeName = TestPipe.Mint("semanticus-bulkp-");
                using var server = new RpcServer(sessions, engine, pipeName);
                using var stop = new System.Threading.CancellationTokenSource();
                var serving = server.RunAsync(stop.Token);
                RemoteEngine remote = null;
                try
                {
                    remote = await RemoteEngine.ConnectAsync(pipeName, timeoutMs: 5000);
                    using var edits = JsonDocument.Parse("{\"description\":\"Sales total.\",\"formatString\":\"#,0.00\",\"isHidden\":true}");
                    var edit = await remote.EditObjectsAsync(new[] { new ObjectEditItem { ObjRef = "measure:Sales/Total",
                        Set = edits.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) } }, "agent");
                    Assert.True(edit.Applied == 1, "edit_objects: " + string.Join("; ", (edit.Rejected ?? new BatchRejection[0]).Select(r => r.Reason)));
                    var total = await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"]);
                    Assert.Equal("Sales total.", total.Description);
                    Assert.Equal("#,0.00", total.FormatString);
                    Assert.True(total.IsHidden);

                    var words = await remote.WriteWordsAsync(new[] { new WordsWriteItem { ObjRef = "measure:Sales/Margin", Description = "Profit share.", Synonyms = new[] { "profit", "gain" } } }, "agent");
                    Assert.Equal(1, words.Applied);
                    Assert.Contains("gain", await sessions.Current.ReadAsync(m => SynonymHelper.GetSynonyms(m.Tables["Sales"].Measures["Margin"], m.Cultures.First())));

                    var made = await remote.CreateMeasuresAsync(new[] { new MeasureCreateItem { TableRef = "table:Sales", Name = "Piped", Expression = "1", DisplayFolder = "Pipe" } }, "agent");
                    Assert.Equal(1, made.Applied);
                    Assert.NotNull(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures.FirstOrDefault(x => x.Name == "Piped")));

                    var waived = await remote.WaiveFindingsAsync(new[] { new FindingWaiverItem { System = "air", RuleId = "DESC-MEASURE", ObjRef = "measure:Sales/Total", Reason = "Accepted." } }, "agent");
                    Assert.Equal(1, waived.Applied);
                    var unwaived = await remote.UnwaiveFindingsAsync(new[] { new FindingFixItem { System = "air", RuleId = "DESC-MEASURE", ObjRef = "measure:Sales/Second" } }, "agent");
                    Assert.Equal(1, unwaived.Applied);
                    Assert.Equal(1, (await remote.ApplyFixesAsync(new[] { new FindingFixItem { System = "air", RuleId = "FMT-SUMMARIZE", ObjRef = "column:Sales/Amount" } }, "agent")).Applied);
                    Assert.Equal(1, (await remote.DuplicateObjectsAsync(new[] { new ObjectDuplicateItem { ObjRef = "measure:Sales/Second", NewName = "Second copy" } }, "agent")).Applied);
                    Assert.Equal(1, (await remote.SetDocSectionsAsync(new[] { new DocSectionItem { ObjRef = "measure:Sales/Second", SectionKey = "notes", Markdown = "Business notes." } }, "agent")).Applied);
                    Assert.Equal(1, (await remote.SetPerspectiveMembersAsync(new[] { new PerspectiveMemberItem { PerspectiveRef = "perspective:Business", ObjRef = "measure:Sales/Second", Include = true } }, "agent")).Applied);
                    Assert.Equal(1, (await remote.SetObjectPermissionsAsync(new[] { new ObjectPermissionItem { RoleName = "Reader", ObjRef = "column:Sales/Amount", Permission = "None" } }, "agent")).Applied);
                }
                finally
                {
                    remote?.Dispose();
                    stop.Cancel();
                    try { await serving; } catch { }
                }
            }
        }

        [Fact]
        public async Task Synonym_activity_names_the_field_instead_of_the_culture()
        {
            var (engine, sessions, _) = await OpenAsync("activity-words");
            using (engine)
            {
                ChangeNotification heard = null;
                sessions.Bus.Changed += n => heard = n;
                var result = await engine.SetSynonymsAsync("measure:Sales/Total", new[] { "sales amount" }, null, "agent");
                Assert.Equal("Your assistant added synonyms to Total.", heard.Words?.Sentence);
                Assert.Equal(heard.Words.Sentence, result.Words.Sentence);
            }
        }

        [Fact]
        public async Task Unwaiving_a_TE_only_ignore_counts_the_change_and_one_undo_restores_it()
        {
            var (engine, sessions, _) = await OpenAsync("bulk-te-ignore");
            using (engine)
            {
                await sessions.Current.MutateAsync("human", "fixture", m => Semanticus.Analysis.WaiverStore.WriteTeIgnore(m.Tables["Sales"].Measures["Total"], "MEASURE_DESCRIPTION", true));
                var before = await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase));
                var result = await engine.UnwaiveFindingsAsync(new[] { new FindingFixItem { System = "bpa", RuleId = "MEASURE_DESCRIPTION", ObjRef = "measure:Sales/Total" } }, "human");
                Assert.Equal(1, result.Applied);
                Assert.False(await sessions.Current.ReadAsync(m => Semanticus.Analysis.WaiverStore.IsTeIgnored(m.Tables["Sales"].Measures["Total"], "MEASURE_DESCRIPTION")));
                await engine.UndoAsync("human");
                Assert.Equal(before, await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase)));
            }
        }

        [Fact]
        public async Task Removing_an_empty_perspective_table_counts_the_write_and_undo_restores_it()
        {
            var (engine, sessions, _) = await OpenAsync("bulk-empty-perspective-table");
            using (engine)
            {
                await sessions.Current.MutateAsync("human", "fixture", m =>
                    m.Tables["Sales"].InPerspective.EnsurePTExists(m.AddPerspective("Business")));
                var before = await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase));
                var result = await engine.SetPerspectiveMembersAsync(new[] {
                    new PerspectiveMemberItem { PerspectiveRef = "perspective:Business", ObjRef = "table:Sales", Include = false },
                }, "human");
                Assert.Equal(1, result.Applied);
                Assert.False(await sessions.Current.ReadAsync(m => m.Database.TOMDatabase.Model.Perspectives["Business"].PerspectiveTables.Contains("Sales")));
                await engine.UndoAsync("human");
                Assert.Equal(before, await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase)));
            }
        }

        [Theory]
        [InlineData("set AI instructions", "culture:en-US", "Content", "Your assistant changed the AI instructions.")]
        [InlineData("create linguistic schema", "culture:en-US", "Content", "Your assistant set up synonyms for the model.")]
        [InlineData("set description", "culture:en-US", "Description", "Your assistant changed the description of the model's language.")]
        [InlineData("set property", "model:Culture/en-US", "Content", "Your assistant changed the Copilot settings.")]
        [InlineData("set property", "measure:Sales/Total", "FormatString", "Your assistant changed the format of Total.")]
        public void Activity_sentence_names_the_change(string label, string reference, string prop, string expected)
            => Assert.Equal(expected, ChangeWords.From(label, new[] { new ChangeDelta { Ref = reference, Kind = "update", Props = new[] { prop } } }).Sentence);

        [Fact]
        public void Small_changes_name_each_field_and_large_changes_count_the_kind()
        {
            Assert.Equal("Your assistant changed Total and Region.", ChangeWords.From("edit", new[] {
                new ChangeDelta { Ref = "measure:Sales/Total", Kind = "update" },
                new ChangeDelta { Ref = "column:Sales/Region", Kind = "update" },
            }).Sentence);
            Assert.Equal("Your assistant changed the format of 4 measures.", ChangeWords.From("edit", Enumerable.Range(0, 4).Select(i =>
                new ChangeDelta { Ref = "measure:Sales/M" + i, Kind = "update", Props = new[] { "FormatString" } })).Sentence);
            Assert.Equal("Your assistant waived 2 findings.", ChangeWords.For("waived", "findings", "one", "two").Sentence);
        }

        [Fact]
        public async Task Single_and_bulk_waivers_use_the_same_finding_identity()
        {
            var (engine, sessions, _) = await OpenAsync("waiver-activity-identity");
            using (engine)
            {
                ChangeNotification single = null;
                sessions.Bus.Changed += n => { if (n.Words?.Noun == "findings") single = n; };
                await engine.WaiveFindingAsync("air", "DESC-MEASURE", "measure:Sales/Total", "Accepted.", "agent");
                var targets = single.Words.Targets;
                var bulk = await engine.WaiveFindingsAsync(new[] { new FindingWaiverItem {
                    System = "air", RuleId = "DESC-MEASURE", ObjRef = "measure:Sales/Total", Reason = "Accepted for this release." } }, "agent");
                Assert.Equal(targets, bulk.Words.Targets);
                Assert.Equal("Your assistant waived 1 finding.", bulk.Words.Sentence);
            }
        }

        [Fact]
        public async Task Bulk_synonyms_count_fields_and_ignore_rejections_and_noops()
        {
            var (engine, sessions, _) = await OpenAsync("bulk-synonym-activity");
            using (engine)
            {
                sessions.Bus.HumanClientAttached = () => true;
                var heard = new List<ChangeNotification>();
                sessions.Bus.Changed += heard.Add;
                var result = await engine.WriteWordsAsync(new[] {
                    new WordsWriteItem { ObjRef = "measure:Sales/Total", Synonyms = new[] { "sales amount" } },
                    new WordsWriteItem { ObjRef = "measure:Sales/Margin", Synonyms = new[] { "profit" } },
                    new WordsWriteItem { ObjRef = "measure:Sales/Missing", Synonyms = new[] { "missing" } },
                }, "agent");
                Assert.Equal(2, result.Applied);
                Assert.Single(result.Rejected);
                Assert.Equal("Your assistant added synonyms to Total and Margin.", Assert.Single(heard).Words.Sentence);
                Assert.Equal(result.Words.Sentence + " Undo", Assert.Single((await engine.GetStudioNotesAsync(null)).Notes).Say);
                Assert.Contains("sales amount", await sessions.Current.ReadAsync(m => SynonymHelper.GetSynonyms(m.Tables["Sales"].Measures["Total"], m.Cultures.First())));
                heard.Clear();
                var noop = await engine.WriteWordsAsync(new[] { new WordsWriteItem { ObjRef = "measure:Sales/Total", Synonyms = new[] { "sales amount" } } }, "agent");
                Assert.Equal(0, noop.Applied);
                Assert.Empty(heard);
            }
        }

        [Theory]
        [InlineData("UnwaiveFindingsAsync")]
        [InlineData("ApplyFixesAsync")]
        [InlineData("DuplicateObjectsAsync")]
        [InlineData("SetDocSectionsAsync")]
        [InlineData("SetPerspectiveMembersAsync")]
        [InlineData("SetObjectPermissionsAsync")]
        public async Task Remaining_bulk_forms_commit_once_name_bad_items_and_undo_exactly(string method)
        {
            var (engine, sessions, _) = await OpenAsync("bulk-more-" + method);
            using (engine)
            {
                await sessions.Current.MutateAsync("human", "fixture", m =>
                {
                    m.AddPerspective("Business");
                    m.AddRole("Reader");
                    m.Tables["Sales"].AddMeasure("Second", "1");
                });
                if (method == "UnwaiveFindingsAsync")
                    await engine.WaiveFindingsAsync(new[] {
                        new FindingWaiverItem { System = "air", RuleId = "DESC-MEASURE", ObjRef = "measure:Sales/Total", Reason = "Kept." },
                        new FindingWaiverItem { System = "air", RuleId = "DESC-MEASURE", ObjRef = "measure:Sales/Second", Reason = "Kept." },
                    }, "human");
                sessions.Bus.HumanClientAttached = () => true;
                var before = await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase));
                var scoreBefore = JsonSerializer.Serialize(await engine.AiReadinessScanAsync());
                var revision = sessions.Current.Revision;
                var steps = await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow().Steps);
                object Item(string objRef) => method switch
                {
                    "UnwaiveFindingsAsync" => new { system = objRef.Contains("Missing") ? "bad" : "air", ruleId = "DESC-MEASURE", objRef },
                    "ApplyFixesAsync" => new { system = "air", ruleId = "FMT-SUMMARIZE", objRef },
                    "DuplicateObjectsAsync" => new { objRef, newName = "Copy " + objRef.Split('/').Last() },
                    "SetDocSectionsAsync" => new { objRef, sectionKey = "notes", markdown = "Business notes." },
                    "SetPerspectiveMembersAsync" => new { objRef, perspectiveRef = "perspective:Business", include = true },
                    _ => new { objRef, roleName = "Reader", permission = "None" },
                };
                var refs = method == "ApplyFixesAsync" || method == "SetObjectPermissionsAsync"
                    ? new[] { "column:Sales/Amount", "column:Sales/ProductId" }
                    : new[] { "measure:Sales/Total", "measure:Sales/Second" };
                var result = await Batch(engine, method, new[] { Item(refs[0]), Item("measure:Sales/Missing"), Item(refs[1]) }, "agent");
                Assert.Equal(2, result.GetProperty("Applied").GetInt32());
                Assert.Equal(1, result.GetProperty("Rejected").GetArrayLength());
                Assert.Equal("measure:Sales/Missing", result.GetProperty("Rejected")[0].GetProperty("ObjRef").GetString());
                Assert.Equal(revision + 1, sessions.Current.Revision);
                Assert.Equal(steps + 1, await sessions.Current.ReadAsync(_ => sessions.Current.UndoStateNow().Steps));
                Assert.Equal(2, Assert.Single((await engine.GetStudioNotesAsync(null)).Notes).Count);
                var scoreAfter = JsonSerializer.Serialize(await engine.AiReadinessScanAsync());
                await engine.UndoAsync("human");
                Assert.Equal(before, await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase)));
                Assert.Equal(scoreBefore, JsonSerializer.Serialize(await engine.AiReadinessScanAsync()));
                foreach (var reference in refs)
                    switch (method)
                    {
                        case "UnwaiveFindingsAsync": await engine.UnwaiveFindingAsync("air", "DESC-MEASURE", reference, "human"); break;
                        case "ApplyFixesAsync": await engine.ApplyFixAsync("FMT-SUMMARIZE", reference, "human"); break;
                        case "DuplicateObjectsAsync": await engine.DuplicateObjectAsync(reference, "Copy " + reference.Split('/').Last(), null, "human"); break;
                        case "SetDocSectionsAsync": await engine.SetDocSectionAsync(reference, "notes", "Business notes.", "human"); break;
                        case "SetPerspectiveMembersAsync": await engine.SetPerspectiveMemberAsync("perspective:Business", reference, true, "human"); break;
                        default: await engine.SetColumnObjectPermissionAsync("Reader", reference, "None", "human"); break;
                    }
                Assert.Equal(scoreAfter, JsonSerializer.Serialize(await engine.AiReadinessScanAsync()));
            }
        }

        [Fact]
        public async Task Batch_dynamic_formats_and_schema_cells_restore_exactly_and_reject_an_item_as_a_unit()
        {
            var (engine, sessions, _) = await OpenAsync("bulk-format-schema");
            using (engine)
            {
                var before = await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase));
                var result = await Batch(engine, "EditObjectsAsync", new object[] {
                    new { objRef = "measure:Sales/Total", set = new { formatStringExpression = "\"0.00\"", aiSchema = false } },
                    new { objRef = "column:Sales/Amount", set = new { description = "Must roll back.", formatStringExpression = "\"0.00\"" } },
                    new { objRef = "measure:Sales/Margin", set = new { formatStringExpression = "\"0%\"" } },
                });
                Assert.Equal(2, result.GetProperty("Applied").GetInt32());
                Assert.Equal(1, result.GetProperty("Rejected").GetArrayLength());
                Assert.Equal("\"0.00\"", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].FormatStringExpression));
                Assert.True(string.IsNullOrEmpty(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Columns["Amount"].Description)));
                await engine.UndoAsync("human");
                Assert.Equal(before, await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase)));
            }
        }

        [Fact]
        public async Task Plan_item_batches_publish_once_keep_good_lines_and_leave_a_bad_line_untouched()
        {
            var (engine, sessions, _) = await OpenAsync("bulk-plan-lines");
            using (engine)
            {
                var announced = 0;
                sessions.Bus.PlanChanged += _ => announced++;
                var added = await engine.AddPlanItemsAsync(new[] {
                    new PlanItemAdd { ObjRef = "measure:Sales/Total", Kind = "set_description", After = "Total sales." },
                    new PlanItemAdd { ObjRef = "measure:Sales/Missing", Kind = "set_description", After = "Missing." },
                    new PlanItemAdd { ObjRef = "measure:Sales/Margin", Kind = "set_description", After = "Margin." },
                }, "human");
                Assert.Equal(2, added.Applied);
                Assert.Single(added.Rejected);
                Assert.Equal(1, announced);
                var ids = added.Plan.Items.Select(x => x.Id).ToArray();
                announced = 0;
                var edited = await engine.SetPlanItemsAsync(new[] {
                    new PlanItemEdit { ItemId = ids[0], After = "Sales total." },
                    new PlanItemEdit { ItemId = ids[1], After = "Must not survive.", Approved = true, ExpectedAfter = "An old value." },
                }, "human");
                Assert.Equal(1, edited.Applied);
                Assert.Single(edited.Rejected);
                Assert.Equal("Margin.", edited.Plan.Items.Single(x => x.Id == ids[1]).After);
                Assert.Equal(1, announced);
                Assert.Equal(0, sessions.Current.Revision);
                var applied = await engine.ApplyPlanAsync(ids, "human");
                Assert.Equal(2, applied.AppliedCount);
                Assert.Equal("Sales total.", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description));
                await engine.UndoAsync("human");
                Assert.True(string.IsNullOrEmpty(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Total"].Description)));
            }
        }

        [Fact]
        public async Task Plan_row_batch_changes_tasks_with_one_final_compose_and_one_broadcast()
        {
            var (engine, sessions, _) = await OpenAsync("bulk-plan-rows");
            using (engine)
            {
                var first = await engine.AddPlanTaskAsync(new PlanTaskRequest { Words = "Write the sales notes." });
                var second = await engine.AddPlanTaskAsync(new PlanTaskRequest { Words = "Review the margin." });
                var composed = 0;
                var announced = 0;
                engine.PlanComposePauseForTest = () => { composed++; return Task.CompletedTask; };
                sessions.Bus.PlanChanged += _ => announced++;
                try
                {
                    var result = await engine.PlanRowsAsync(new[] {
                        new PlanRowEdit { Id = first.ItemId, Action = "pause" },
                        new PlanRowEdit { Id = "pt-missing", Action = "pause" },
                        new PlanRowEdit { Id = "pt-missing", Action = "skip", Text = "This row does not exist." },
                        new PlanRowEdit { Id = "pt-missing", Action = "done" },
                        new PlanRowEdit { Id = second.ItemId, Action = "note", Text = "Check the totals." },
                        new PlanRowEdit { Id = second.ItemId, Action = "doer", Doer = "you" },
                        new PlanRowEdit { Id = first.ItemId, Action = "skip", Text = "After the review." },
                        new PlanRowEdit { Id = first.ItemId, Action = "unskip" },
                        new PlanRowEdit { Id = first.ItemId, Action = "remove" },
                        new PlanRowEdit { Id = first.ItemId, Action = "restore" },
                    }, "human");
                    Assert.Equal(2, result.Applied);
                    Assert.Equal(3, result.Rejected.Length);
                    Assert.Equal(2, composed);
                    Assert.Equal(1, announced);
                    Assert.True(result.Plan.Items.Single(x => x.Id == first.ItemId).Held);
                    Assert.Equal("Check the totals.", result.Plan.Items.Single(x => x.Id == second.ItemId).Note);
                    Assert.Equal("you", result.Plan.Items.Single(x => x.Id == second.ItemId).Doer);
                    Assert.Equal(0, sessions.Current.Revision);
                }
                finally { engine.PlanComposePauseForTest = null; }
            }
        }

        [Fact]
        public async Task Removing_a_draft_without_queue_parts_counts_the_row_and_restore_keeps_it()
        {
            var (engine, sessions, _) = await OpenAsync("bulk-plan-draft");
            using (engine)
            {
                var steps = JsonSerializer.Serialize(new[] {
                    new { op = "set_description", args = new { objRef = "measure:Sales/Total", text = "Sales total." } },
                });
                var draft = await engine.ComposeTaskAsync(null, null, null, steps, "Describe total sales", "human");
                var row = "draft/" + draft.DraftId;
                Assert.DoesNotContain((await engine.GetModelPlanAsync()).Items, i => PlanTasks.IsTaskId(i.Id) && i.OriginLink?.Kind == PlanOriginKinds.Draft);
                var removed = await engine.PlanRowsAsync(new[] { new PlanRowEdit { Id = row, Action = "remove" } }, "human");
                Assert.Empty(removed.Rejected);
                Assert.Equal(1, removed.Applied);
                Assert.DoesNotContain(removed.Plan.Items, i => i.Id == row);
                var restored = await engine.PlanRowsAsync(new[] { new PlanRowEdit { Id = row, Action = "restore" } }, "human");
                Assert.Equal(1, restored.Applied);
                Assert.Contains(restored.Plan.Items, i => i.Id == row);
                Assert.Equal(draft.DraftId, (await engine.GetDraftAsync(draft.DraftId)).DraftId);
                Assert.Equal(0, sessions.Current.Revision);
            }
        }

        [Fact]
        public async Task A_noop_row_control_does_not_count_another_rows_change()
        {
            var (engine, sessions, _) = await OpenAsync("bulk-plan-count-own-row");
            using (engine)
            {
                var task = await engine.AddPlanTaskAsync(new PlanTaskRequest { Words = "Write the sales notes." });
                await engine.SetPlanTaskNoteAsync(task.ItemId, "Keep this note.", "agent");   // the assistant's own note: the repeat below is a true no-op
                sessions.Bus.HumanClientAttached = () => true;
                engine.PlanRowBeforeControlForTest = async () =>
                {
                    engine.PlanRowBeforeControlForTest = null;
                    await engine.AddPlanTaskAsync(new PlanTaskRequest { Words = "A different row arrived." });
                };
                try
                {
                    var result = await engine.PlanRowsAsync(new[] {
                        new PlanRowEdit { Id = task.ItemId, Action = "note", Text = "Keep this note." },
                    }, "agent");
                    Assert.Equal(0, result.Applied);
                    Assert.Empty(result.Rejected);
                    Assert.Empty((await engine.GetStudioNotesAsync(null)).Notes);
                    Assert.Contains(result.Plan.Items, i => i.Title == "A different row arrived.");
                }
                finally { engine.PlanRowBeforeControlForTest = null; }
            }
        }

        [Fact]
        public async Task A_noop_row_control_does_not_count_a_persons_change_to_the_same_row()
        {
            var (engine, sessions, _) = await OpenAsync("bulk-plan-count-same-row");
            using (engine)
            {
                var task = await engine.AddPlanTaskAsync(new PlanTaskRequest { Words = "Write the sales notes." });
                await engine.SetPlanTaskNoteAsync(task.ItemId, "The old note.", "agent");   // the assistant's own note (drive-finish keeps it beside the person's), so the repeat below is a true no-op
                sessions.Bus.HumanClientAttached = () => true;
                engine.PlanRowBeforeControlForTest = async () =>
                {
                    engine.PlanRowBeforeControlForTest = null;
                    Task humanWrite;
                    using (System.Threading.ExecutionContext.SuppressFlow())
                        humanWrite = Task.Run(() => engine.MarkPlanTargetDoneAsync(task.ItemId, "human"));
                    await humanWrite;
                };
                try
                {
                    var result = await engine.PlanRowsAsync(new[] {
                        new PlanRowEdit { Id = task.ItemId, Action = "note", Text = "The old note." },
                    }, "agent");
                    Assert.Equal(0, result.Applied);
                    Assert.Empty(result.Rejected);
                    Assert.Empty((await engine.GetStudioNotesAsync(null)).Notes);
                    Assert.Equal("The old note.", result.Plan.Items.Single(i => i.Id == task.ItemId).AssistantNote);
                    Assert.Equal(PlanTaskStates.Done, engine.ReadPlanState(engine.BindPlanMemory()).FindTask(task.ItemId).State);
                }
                finally { engine.PlanRowBeforeControlForTest = null; }
            }
        }

        [Fact]
        public async Task Repeated_words_queue_refs_count_one_object()
        {
            var (engine, sessions, _) = await OpenAsync("repeated-words");
            using (engine)
            {
                sessions.Bus.HumanClientAttached = () => true;
                var result = await Batch(engine, "WriteWordsAsync", new object[]
                {
                    new { objRef = "measure:Sales/Total", description = "Sales total." },
                    new { objRef = "measure:Sales/Total", displayFolder = "Business" },
                }, "agent");
                Assert.Equal(1, result.GetProperty("Applied").GetInt32());
                Assert.Equal(1, Assert.Single((await engine.GetStudioNotesAsync(null)).Notes).Count);
            }
        }

        [Fact]
        public async Task Existing_bulk_delete_reports_its_real_count_and_one_undo_restores_all()
        {
            var (engine, sessions, _) = await OpenAsync("delete-many");
            using (engine)
            {
                await sessions.Current.MutateAsync("human", "fixture", m =>
                {
                    for (var i = 0; i < 50; i++) m.Tables["Sales"].AddMeasure("Delete " + i, "1");
                });
                sessions.Bus.HumanClientAttached = () => true;
                await engine.NoteAgentCallAsync("delete_objects", null);
                var before = await sessions.Current.ReadAsync(m => JsonSerializer.Serialize(m.Tables["Sales"].Measures.OrderBy(x => x.Name).Select(x => new { x.Name, x.Expression, x.Description, x.DisplayFolder, x.FormatString })));
                var result = await engine.DeleteObjectsAsync(Enumerable.Range(0, 50).Select(i => "measure:Sales/Delete " + i).Append("measure:Sales/Missing").ToArray(), "agent");
                Assert.True(result.Changed);
                var note = Assert.Single((await engine.GetStudioNotesAsync(null)).Notes);
                Assert.Equal(50, note.Count);
                Assert.Contains("50", note.Say);
                await engine.UndoAsync("human");
                Assert.Equal(before, await sessions.Current.ReadAsync(m => JsonSerializer.Serialize(m.Tables["Sales"].Measures.OrderBy(x => x.Name).Select(x => new { x.Name, x.Expression, x.Description, x.DisplayFolder, x.FormatString }))));
            }
        }

        [Theory]
        [InlineData("table:Sales")]
        [InlineData("Sales")]
        public async Task Create_fifty_measures_skips_a_bad_item_and_undo_restores_everything(string tableRef)
        {
            var (engine, sessions, _) = await OpenAsync("create-many");
            using (engine)
            {
                var before = await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase));
                var revision = sessions.Current.Revision;
                sessions.Bus.HumanClientAttached = () => true;
                var items = Enumerable.Range(0, 50).Select(i => (object)new { tableRef, name = "New " + i, expression = "1", displayFolder = "Business" })
                    .Append(new { tableRef = "table:Missing", name = "Bad", expression = "1" }).ToArray();
                var result = await Batch(engine, "CreateMeasuresAsync", items, "agent");
                Assert.Equal(50, result.GetProperty("Applied").GetInt32());
                Assert.Equal("Your assistant created 50 measures.", result.GetProperty("Words").GetProperty("Sentence").GetString());
                Assert.Equal(50, Assert.Single(result.GetProperty("Rejected").EnumerateArray()).GetProperty("Index").GetInt32());
                Assert.Equal(revision + 1, sessions.Current.Revision);
                Assert.Equal(50, Assert.Single((await engine.GetStudioNotesAsync(null)).Notes).Count);
                Assert.Equal(50, await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures.Count(x => x.Name.StartsWith("New ") && x.DisplayFolder == "Business")));
                await engine.UndoAsync("human");
                Assert.Equal(before, await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase)));
            }
        }

        private static async Task<JsonElement> Batch(LocalEngine engine, string method, object[] items, string origin = "human")
        {
            var target = typeof(IEngine).GetMethod(method);
            Assert.NotNull(target);
            var typed = JsonSerializer.Deserialize(JsonSerializer.Serialize(items), target.GetParameters()[0].ParameterType,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var task = (Task)target.Invoke(engine, new[] { typed, origin });
            await task;
            return JsonSerializer.SerializeToElement(task.GetType().GetProperty("Result").GetValue(task));
        }

        [Fact]
        public async Task General_edits_fix_formulas_and_roll_back_a_partly_written_bad_item()
        {
            var (engine, sessions, _) = await OpenAsync("general-edits");
            using (engine)
            {
                await sessions.Current.MutateAsync("human", "fixture", m => m.Tables["Sales"].AddMeasure("Dependent", "[Total] + 1"));
                var before = await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase));
                var result = await Batch(engine, "EditObjectsAsync", new object[]
                {
                    new { objRef = "measure:Sales/Total", set = new { name = "Revenue" } },
                    new { objRef = "column:Sales/Amount", set = new { description = "Must roll back.", summarizeBy = "NotAnAggregate" } },
                    new { objRef = "column:Sales/ProductId", set = new { dataType = "string", dataCategory = "", sortByColumn = "(none)" } },
                    new { objRef = "column:Sales/Region", set = new { sortByColumn = "Code" } },
                });
                Assert.Equal(3, result.GetProperty("Applied").GetInt32());
                Assert.Single(result.GetProperty("Rejected").EnumerateArray());
                Assert.Contains("[Revenue]", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Dependent"].Expression));
                Assert.True(string.IsNullOrEmpty(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Columns["Amount"].Description)));
                Assert.Equal("Code", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Columns["Region"].SortByColumn.Name));
                await engine.UndoAsync("human");
                Assert.Equal(before, await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase)));
            }
        }

        // Each item used to read the whole model twice (12 ms per item on 3,000 fields, most of a 300-item call).
        [Fact]
        public async Task A_bulk_call_reads_the_whole_model_once_for_all_its_items()
        {
            var (engine, sessions, _) = await OpenAsync("one-snapshot");
            using (engine)
            {
                await sessions.Current.MutateAsync("human", "fixture", m =>
                {
                    for (var i = 0; i < 20; i++) m.Tables["Sales"].AddMeasure("Snap " + i, "1");
                });
                var captures = await sessions.Current.ReadAsync(m => ModelSeal.For(m).Captures);
                var result = await Batch(engine, "EditObjectsAsync", Enumerable.Range(0, 20)
                    .Select(i => (object)new { objRef = "measure:Sales/Snap " + i, set = new { description = "Snap " + i, formatString = "0.0" } }).ToArray());
                Assert.Equal(20, result.GetProperty("Applied").GetInt32());
                // One for the call's own undo step, one for its items.
                Assert.Equal(captures + 2, await sessions.Current.ReadAsync(m => ModelSeal.For(m).Captures));
            }
        }

        // A bad item that wrote part of itself is taken back by rolling back the items since the last snapshot and running the
        // good ones again. The result must be the same as a call that never held the bad items.
        [Fact]
        public async Task Bad_items_between_good_ones_leave_exactly_the_good_edits_and_undo_and_redo_are_exact()
        {
            var (engine, sessions, _) = await OpenAsync("replay");
            var (twin, twinSessions, _) = await OpenAsync("replay-twin");
            using (engine)
            using (twin)
            {
                foreach (var s in new[] { sessions, twinSessions })
                    await s.Current.MutateAsync("human", "fixture", m =>
                    {
                        for (var i = 0; i < 30; i++) m.Tables["Sales"].AddMeasure("Item " + i, "1");
                        m.Tables["Sales"].AddMeasure("Uses", "[Item 0] + [Item 29]");
                    });
                object Good(int i) => new { objRef = "measure:Sales/Item " + i, set = new { name = "Done " + i, description = "Good " + i, formatString = "0.0" } };
                object Bad(int i) => new { objRef = "measure:Sales/Item " + i, set = new { description = "Must roll back " + i, summarizeBy = "Sum" } };
                var bad = new[] { 2, 3, 11, 29 };
                var before = await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase));
                var captures = await sessions.Current.ReadAsync(m => ModelSeal.For(m).Captures);
                var result = await Batch(engine, "EditObjectsAsync", Enumerable.Range(0, 30).Select(i => bad.Contains(i) ? Bad(i) : Good(i)).ToArray());
                Assert.Equal(26, result.GetProperty("Applied").GetInt32());
                Assert.Equal(bad, result.GetProperty("Rejected").EnumerateArray().Select(x => x.GetProperty("Index").GetInt32()).ToArray());
                // One more read per bad item, never one per item.
                Assert.Equal(captures + 2 + bad.Length, await sessions.Current.ReadAsync(m => ModelSeal.For(m).Captures));
                var twinResult = await Batch(twin, "EditObjectsAsync", Enumerable.Range(0, 30).Where(i => !bad.Contains(i)).Select(Good).ToArray());
                Assert.Equal(26, twinResult.GetProperty("Applied").GetInt32());
                string Shape(TabularEditor.TOMWrapper.Model m) => JsonSerializer.Serialize(m.Tables["Sales"].Measures.OrderBy(x => x.Name)
                    .Select(x => new { x.Name, x.Expression, x.Description, x.FormatString, x.DisplayFolder, x.IsHidden }));
                var after = await sessions.Current.ReadAsync(Shape);
                Assert.Equal(await twinSessions.Current.ReadAsync(Shape), after);
                Assert.Contains("[Done 0]", await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures["Uses"].Expression));
                var afterJson = await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase));
                await engine.UndoAsync("human");
                Assert.Equal(before, await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase)));
                await engine.RedoAsync("human");
                Assert.Equal(afterJson, await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase)));
            }
        }

        [Fact]
        public async Task Words_queue_items_use_the_write_input_ref_and_unchanged_words_count_zero()
        {
            var (engine, sessions, _) = await OpenAsync("words-queue");
            using (engine)
            {
                var queue = await engine.GetWordsQueueAsync();
                Assert.NotEmpty(queue.Items.Where(x => x.Kind == "describe"));
                Assert.All(queue.Items.Where(x => x.Kind == "describe"), item => Assert.Equal("write_words", item.Tool));
                Assert.All(queue.Items.Where(x => x.Kind == "name" || x.Kind == "format"), item => Assert.Equal("edit_objects", item.Tool));
                Assert.All(queue.Items.Where(x => x.Kind == "synonym" && x.RuleId != "SYN-SCHEMA"), item => Assert.Equal("write_words", item.Tool));
                var json = JsonSerializer.SerializeToElement(queue);
                Assert.All(json.GetProperty("Items").EnumerateArray(), item => Assert.Equal(item.GetProperty("ObjectRef").GetString(), item.GetProperty("ObjRef").GetString()));
                var revision = sessions.Current.Revision;
                var result = await Batch(engine, "WriteWordsAsync", new object[] { new { objRef = "measure:Sales/Total", description = "" } });
                Assert.Equal(0, result.GetProperty("Applied").GetInt32());
                Assert.Equal(revision, sessions.Current.Revision);

                // A real queue page feeds one write; its items leave, and reading from zero finds the next work.
                var page = await McpTools.GetWordsQueue(engine, 3);
                var authored = page.Items.Where(x => x.Kind == "describe").Select(x => x.ObjRef).Distinct()
                    .Select(r => new WordsWriteItem { ObjRef = r, Description = "The business meaning for this object." }).ToArray();
                Assert.NotEmpty(authored);
                var commits = new List<ChangeNotification>();
                sessions.Bus.Changed += commits.Add;
                var written = await engine.WriteWordsAsync(authored, "agent");
                Assert.Equal(authored.Length, written.Applied);
                Assert.Empty(written.Rejected);
                Assert.Single(commits);
                var next = await McpTools.GetWordsQueue(engine, 3, 0);
                Assert.True(next.Total < page.Total);
                Assert.DoesNotContain(next.Items, x => x.Kind == "describe" && authored.Any(a => a.ObjRef == x.ObjRef));
                await engine.UndoAsync("human");
                Assert.Equal(page.Total, (await engine.GetWordsQueueAsync()).Total);
            }
        }

        [Fact]
        public async Task Free_words_queue_keeps_the_single_tools()
        {
            var path = AssistantGateKit.WriteModel(Root, "free-words-queue");
            using var engine = new LocalEngine(new SessionManager(), TestEntitlements.Free);
            await engine.OpenAsync(path);
            var page = await McpTools.GetWordsQueue(engine);
            Assert.NotEmpty(page.Items);
            Assert.All(page.Items, item => Assert.Contains(item.Tool, new[]
            {
                "set_description", "set_synonyms", "rename_object", "set_measure_format", "set_property",
                "create_linguistic_schema", "set_ai_instructions",
            }));
        }

        [Theory]
        [InlineData("WaiveFindingsAsync")]
        [InlineData("WriteWordsAsync")]
        [InlineData("EditObjectsAsync")]
        public async Task Fifty_items_one_commit_one_undo_and_one_measured_note(string method)
        {
            var (engine, sessions, _) = await OpenAsync("fifty-" + method);
            using (engine)
            {
                await sessions.Current.MutateAsync("human", "fixture", m =>
                {
                    for (var i = 0; i < 50; i++) m.Tables["Sales"].AddMeasure("Word " + i, "1");
                });
                // Synonyms replace their old list, including a model that needs a new linguistic culture.
                await engine.SetSynonymsAsync("measure:Sales/Word 0", new[] { "old term" }, null, "human");
                sessions.Bus.HumanClientAttached = () => true;
                var before = await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase));
                var revision = sessions.Current.Revision;
                var items = Enumerable.Range(0, 50).Select(i => method == "EditObjectsAsync"
                    ? (object)new { objRef = "measure:Sales/Word " + i, set = new { name = "Renamed " + i, description = "Sales word " + i, displayFolder = "Business", formatString = "0.00", hidden = true } }
                    : method == "WriteWordsAsync"
                    ? (object)new { objRef = "measure:Sales/Word " + i, description = "Sales word " + i, synonyms = new[] { "new term " + i }, displayFolder = "Business" }
                    : new { system = i % 2 == 0 ? "air" : "bpa", ruleId = "DESC-MEASURE", objRef = "measure:Sales/Word " + i, reason = "Accepted for this model." }).ToArray();
                var result = await Batch(engine, method, items, "agent");
                Assert.Equal(50, result.GetProperty("Applied").GetInt32());
                Assert.Equal(50, result.GetProperty("Words").GetProperty("Count").GetInt32());
                Assert.Equal(method == "WaiveFindingsAsync" ? "Your assistant waived 50 findings."
                    : method == "WriteWordsAsync" ? "Your assistant wrote words for 50 fields."
                    : "Your assistant changed 50 measures.", result.GetProperty("Words").GetProperty("Sentence").GetString());
                Assert.Empty(result.GetProperty("Rejected").EnumerateArray());
                Assert.Equal(revision + 1, sessions.Current.Revision);
                var note = Assert.Single((await engine.GetStudioNotesAsync(null)).Notes);
                Assert.Equal(50, note.Count);
                Assert.Contains("50", note.Say);
                Assert.Contains("Undo", note.Say);
                Assert.Equal(result.GetProperty("Words").GetProperty("Sentence").GetString() + " Undo", note.Say);
                if (method == "EditObjectsAsync")
                    Assert.Equal(50, await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures.Count(x => x.Name.StartsWith("Renamed ") && x.IsHidden && x.FormatString == "0.00")));
                else if (method == "WriteWordsAsync")
                {
                    Assert.Equal(50, await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures.Count(x => x.Description.StartsWith("Sales word ") && x.DisplayFolder == "Business")));
                    var schema = await sessions.Current.ReadAsync(m => string.Join("", m.Cultures.Select(c => c.Content)));
                    Assert.DoesNotContain("old term", await sessions.Current.ReadAsync(m => SynonymHelper.GetSynonyms(m.Tables["Sales"].Measures["Word 0"], m.Cultures.First(c => !string.IsNullOrEmpty(c.Content)))));
                    Assert.Contains("new term 49", schema);
                }
                else Assert.Equal(50, (await engine.ListWaiversAsync()).Length);
                await engine.UndoAsync("human");
                Assert.Equal(before, await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase)));
            }
        }

        [Fact]
        public async Task A_synonym_only_write_words_of_50_fields_says_it_added_synonyms_to_50_fields()
        {
            var (engine, sessions, _) = await OpenAsync("words-50-activity");
            using (engine)
            {
                await sessions.Current.MutateAsync("human", "fixture", m =>
                {
                    for (var i = 0; i < 50; i++) m.Tables["Sales"].AddMeasure("Fifty " + i, "1");
                });
                sessions.Bus.HumanClientAttached = () => true;
                var heard = new List<ChangeNotification>();
                sessions.Bus.Changed += heard.Add;
                var result = await engine.WriteWordsAsync(Enumerable.Range(0, 50)
                    .Select(i => new WordsWriteItem { ObjRef = "measure:Sales/Fifty " + i, Synonyms = new[] { "fifty term " + i } }).ToArray(), "agent");
                Assert.Equal(50, result.Applied);
                Assert.Equal("Your assistant added synonyms to 50 fields.", result.Words?.Sentence);
                Assert.Equal(50, result.Words.Targets.Length);
                Assert.Equal(result.Words.Sentence, Assert.Single(heard).Words.Sentence);
            }
        }

        [Fact]
        public async Task A_write_words_item_with_a_description_and_synonyms_counts_once_in_the_words()
        {
            var (engine, sessions, _) = await OpenAsync("words-both-activity");
            using (engine)
            {
                sessions.Bus.HumanClientAttached = () => true;
                var result = await engine.WriteWordsAsync(new[]
                {
                    new WordsWriteItem { ObjRef = "measure:Sales/Total", Description = "Sales total.", Synonyms = new[] { "sales amount" } },
                }, "agent");
                Assert.Equal(1, result.Applied);
                Assert.Equal(new[] { "measure:Sales/Total" }, result.Words.Targets);
                Assert.Equal("Your assistant wrote words for Total.", result.Words.Sentence);
            }
        }

        [Fact]
        public async Task Duplicate_objects_lists_each_copy_once_when_an_item_in_the_middle_is_rejected()
        {
            var (engine, sessions, _) = await OpenAsync("duplicate-reject");
            using (engine)
            {
                var result = await engine.DuplicateObjectsAsync(new[]
                {
                    new ObjectDuplicateItem { ObjRef = "measure:Sales/Total", NewName = "Total one" },
                    new ObjectDuplicateItem { ObjRef = "measure:Sales/Margin", NewName = "Margin one" },
                    new ObjectDuplicateItem { ObjRef = "measure:Sales/Missing", NewName = "Missing one" },
                    new ObjectDuplicateItem { ObjRef = "measure:Sales/Total", NewName = "Total two" },
                }, "agent");
                Assert.Equal(3, result.Applied);
                Assert.Equal(2, Assert.Single(result.Rejected).Index);
                Assert.Equal(3, result.Created.Length);
                Assert.Equal(result.Created.Length, result.Created.Distinct().Count());
                Assert.Equal(3, await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures.Count(x => x.Name.EndsWith(" one") || x.Name.EndsWith(" two"))));
            }
        }

        [Fact]
        public async Task One_write_words_of_300_synonyms_puts_one_copy_of_the_schema_on_the_undo_stack()
        {
            var (engine, sessions, _) = await OpenAsync("words-300");
            using (engine)
            {
                await sessions.Current.MutateAsync("human", "fixture", m =>
                {
                    for (var i = 0; i < 300; i++) m.Tables["Sales"].AddMeasure("Syn " + i, "1");
                });
                await engine.SetSynonymsAsync("measure:Sales/Syn 0", new[] { "first" }, null, "human");   // the culture exists before the baseline
                var schemaBefore = await sessions.Current.ReadAsync(m => string.Join("", m.Cultures.Select(c => c.Content)));
                var copiesBefore = await sessions.Current.ReadAsync(SynonymIndexTests.ContentChanges);

                var items = Enumerable.Range(0, 300).Select(i => (object)new { objRef = "measure:Sales/Syn " + i, synonyms = new[] { "term " + i, "word " + i } }).ToArray();
                var result = await Batch(engine, "WriteWordsAsync", items, "agent");

                Assert.Equal(300, result.GetProperty("Applied").GetInt32());
                Assert.Empty(result.GetProperty("Rejected").EnumerateArray());
                Assert.Equal(copiesBefore + 1, await sessions.Current.ReadAsync(SynonymIndexTests.ContentChanges));   // one copy, not 300
                var terms = await sessions.Current.ReadAsync(m => SynonymHelper.GetSynonyms(m.Tables["Sales"].Measures["Syn 299"], m.Cultures.First(c => !string.IsNullOrEmpty(c.Content))));
                Assert.Contains("word 299", terms);
                Assert.DoesNotContain("first", await sessions.Current.ReadAsync(m => SynonymHelper.GetSynonyms(m.Tables["Sales"].Measures["Syn 0"], m.Cultures.First(c => !string.IsNullOrEmpty(c.Content)))));

                // The same words again change nothing, so nothing is counted.
                var again = await Batch(engine, "WriteWordsAsync", items, "agent");
                Assert.Equal(0, again.GetProperty("Applied").GetInt32());

                await engine.UndoAsync("human");   // one undo takes every word back
                Assert.Equal(schemaBefore, await sessions.Current.ReadAsync(m => string.Join("", m.Cultures.Select(c => c.Content))));
            }
        }

        [Theory]
        [InlineData("WaiveFindingsAsync")]
        [InlineData("WriteWordsAsync")]
        [InlineData("EditObjectsAsync")]
        public async Task A_bad_item_is_named_and_the_other_items_apply(string method)
        {
            var (engine, sessions, _) = await OpenAsync("bad-" + method);
            using (engine)
            {
                var items = method == "EditObjectsAsync" ? new object[]
                {
                    new { objRef = "measure:Sales/Total", set = new { description = "Sales total." } },
                    new { objRef = "table:Sales", set = new { name = "Changed Sales", displayFolder = "Bad" } },
                    new { objRef = "measure:Sales/Margin", set = new { hidden = true } },
                } : method == "WriteWordsAsync" ? new object[]
                {
                    new { objRef = "measure:Sales/Total", description = "Sales total." },
                    new { objRef = "table:Sales", description = "Must roll back.", displayFolder = "Bad" },
                    new { objRef = "measure:Sales/Margin", synonyms = new[] { "profit" } },
                } : new object[]
                {
                    new { system = "air", ruleId = "DESC-MEASURE", objRef = "measure:Sales/Total", reason = "Accepted." },
                    new { system = "air", ruleId = "DESC-MEASURE", objRef = "measure:Sales/Missing", reason = "" },
                    new { system = "bpa", ruleId = "TEST", objRef = "*", reason = "Accepted." },
                };
                var before = await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase));
                var result = await Batch(engine, method, items);
                Assert.Equal(2, result.GetProperty("Applied").GetInt32());
                var bad = Assert.Single(result.GetProperty("Rejected").EnumerateArray());
                Assert.Equal(1, bad.GetProperty("Index").GetInt32());
                Assert.Contains(method == "WaiveFindingsAsync" ? "measure:Sales/Missing" : "table:Sales", bad.GetProperty("ObjRef").GetString());
                Assert.False(string.IsNullOrWhiteSpace(bad.GetProperty("Reason").GetString()));
                await engine.UndoAsync("human");
                Assert.Equal(before, await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase)));
            }
        }

        [Theory]
        [InlineData("WaiveFindingsAsync", "waive_finding")]
        [InlineData("WriteWordsAsync", "set_description")]
        [InlineData("EditObjectsAsync", "set_property")]
        [InlineData("CreateMeasuresAsync", "create_measure")]
        [InlineData("UnwaiveFindingsAsync", "unwaive_finding")]
        [InlineData("ApplyFixesAsync", "apply_fix")]
        [InlineData("DuplicateObjectsAsync", "duplicate_object")]
        [InlineData("SetDocSectionsAsync", "set_doc_section")]
        [InlineData("SetPerspectiveMembersAsync", "set_perspective_member")]
        [InlineData("SetObjectPermissionsAsync", "set_table_ols")]
        [InlineData("SetPlanItemsAsync", "set_plan_item")]
        [InlineData("AddPlanItemsAsync", "add_plan_item")]
        [InlineData("EditStartPlansAsync", "edit_start_plan")]
        [InlineData("PlanRowsAsync", "pause_plan_item")]
        public async Task Free_is_sent_to_the_single_item_tool(string method, string alternative)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Free);
            await engine.OpenAsync(AssistantGateKit.WriteModel(Root, "free-" + method));
            var ex = await Record.ExceptionAsync(() => Batch(engine, method, Array.Empty<object>()));
            Assert.NotNull(ex);
            var message = (ex.InnerException ?? ex).Message;
            Assert.Contains("Pro", message);
            Assert.Contains("single-item", message);
            var tool = typeof(McpTools).GetMethod(method.Replace("Async", ""));
            Assert.Contains(alternative, tool.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description);
            Assert.Equal(0, sessions.Current.Revision);
        }

        [Theory]
        [InlineData("waive_findings")]
        [InlineData("write_words")]
        [InlineData("edit_objects")]
        [InlineData("create_measures")]
        [InlineData("unwaive_findings")]
        [InlineData("apply_fixes")]
        [InlineData("duplicate_objects")]
        [InlineData("set_doc_sections")]
        [InlineData("set_perspective_members")]
        [InlineData("set_object_permissions")]
        public async Task Pro_batch_tools_work_through_the_real_MCP_gate(string tool)
        {
            var (engine, sessions, _) = await OpenAsync("batch-door-" + tool);
            using (engine)
            {
                if (tool == "unwaive_findings") await engine.WaiveFindingAsync("air", "DESC-MEASURE", "measure:Sales/Total", "Kept.", "human");
                if (tool == "set_perspective_members") await sessions.Current.MutateAsync("human", "fixture", m => m.AddPerspective("Business"));
                if (tool == "set_object_permissions") await sessions.Current.MutateAsync("human", "fixture", m => m.AddRole("Reader"));
                var before = await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase));
                AgentPolicyStore.SetPreset(AssistantGateKit.Locked, "human");
                var toServer = new Pipe();
                var toClient = new Pipe();
                var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
                builder.Logging.ClearProviders();
                builder.Services.AddSingleton<IEngine>(AssistantEngine.ForAssistant(engine));
                builder.Services.AddMcpServer()
                    .WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream())
                    .WithToolsFromAssembly(typeof(McpTools).Assembly)
                    .WithRequestFilters(f => McpDoorFilters.Add(f, engine));
                using var host = builder.Build();
                await host.StartAsync();
                await using var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));
                try
                {
                    var item = new Dictionary<string, object> { ["objRef"] = "measure:Sales/Total" };
                    if (tool == "create_measures") { item["tableRef"] = "table:Sales"; item["name"] = "Created"; item["expression"] = "1"; }
                    else if (tool == "edit_objects") item["set"] = new { description = "Sum of sales." };
                    else if (tool == "write_words") item["description"] = "Sum of sales.";
                    else if (tool == "duplicate_objects") item["newName"] = "Created";
                    else if (tool == "set_doc_sections") { item["sectionKey"] = "notes"; item["markdown"] = "Sales notes."; }
                    else if (tool == "set_perspective_members") { item["perspectiveRef"] = "perspective:Business"; item["include"] = true; }
                    else if (tool == "set_object_permissions") { item["objRef"] = "table:Sales"; item["roleName"] = "Reader"; item["permission"] = "None"; }
                    else { item["system"] = "air"; item["ruleId"] = tool == "apply_fixes" ? "FMT-SUMMARIZE" : "DESC-MEASURE"; item["reason"] = "Accepted for this model."; if (tool == "apply_fixes") item["objRef"] = "column:Sales/Amount"; }
                    var args = new Dictionary<string, object> { ["items"] = new[] { item } };
                    var revision = sessions.Current.Revision;
                    var held = await client.CallToolAsync(tool, args);
                    Assert.True(held.IsError);
                    Assert.Equal(revision, sessions.Current.Revision);
                    var asked = await OnlyPendingAsync(engine);
                    await engine.ApproveAgentActionAsync(asked.Id, "human");
                    var result = await client.CallToolAsync(tool, args);
                    Assert.NotEqual(true, result.IsError);
                    Assert.NotEqual(before, await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase)));
                    await engine.UndoAsync("human");
                    Assert.Equal(before, await sessions.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase)));
                }
                finally { await host.StopAsync(); }
            }
        }
    }
}
