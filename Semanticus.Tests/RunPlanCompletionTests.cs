using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class RunPlanCompletionTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public RunPlanCompletionTests(ModelTemplates templates) => _templates = templates;

        private async Task<DecisionFixture> ModelAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await _templates.OpenCopyAsync(f, "run-completion", async b =>
            {
                await b.Engine.CreateModelAsync("Contoso", 1604);
                foreach (var table in new[] { "Sales", "Customer" })
                {
                    await b.Engine.CreateTableAsync(table, "human");
                    await b.Engine.CreateColumnAsync("table:" + table, "Amount", "Double", null, "human");
                    var filed = await b.Engine.CreateMeasureAsync("table:" + table, table + " Orders", "COUNTROWS ( '" + table + "' )", "human", "Volume");
                    await b.Engine.SetMeasureFormatAsync(filed, "#,0", "human");
                }
                await b.Engine.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
                await b.Engine.SetObjectPropertyAsync("table:Sales", "IsHidden", "true", "human");
                await b.Engine.CreateLinguisticSchemaAsync(null, "human");
            }, file);
            f.UseNull();
            return f;
        }

        private static async Task<ModelPlanView> KeepOnlyAsync(DecisionFixture f, Func<ModelPlanItem, bool> keep)
        {
            var plan = await f.Engine.GetModelPlanAsync();
            foreach (var row in plan.Items.Where(i => i.Status != ModelPlanStatuses.Done && !keep(i)))
                await f.Engine.SkipModelPlanItemAsync(row.Id, "Outside this test.", "human");
            return await f.Engine.GetModelPlanAsync();
        }

        private static ColumnSummarizeFamily.Ask Ask(string name) => new ColumnSummarizeFamily.Ask
        {
            Ref = "column:Sales/" + name, ColumnName = name, TableRef = "table:Sales", TableName = "Sales", Current = "Default",
            HasLinguistic = true,
            NameOf = StarterMeasureTemplates.MeasureOptions.ToDictionary(o => o, o => new BaseMeasures().NameFor(o, name)),
        };

        [Fact]
        public async Task Hidden_table_measures_have_no_synonym_line_but_visible_measures_do()
        {
            using var f = await ModelAsync("Hidden.bim");
            var asks = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null).Asks);
            foreach (var ask in asks)
            {
                var lines = new List<ChangeItem>();
                ColumnSummarizeFamily.Map(ask, "sum", .99, .85, lines);
                Assert.Contains(lines, l => l.Kind == "create_measure");
                Assert.Equal(ask.TableName != "Sales", lines.Any(l => l.Kind == "set_synonyms"));
            }
        }

        [Theory]
        [InlineData("Total Children", "Total Children")]
        [InlineData("Total Sales", "Total Sales")]
        public void Base_measure_names_do_not_repeat_the_total_prefix(string column, string expected) =>
            Assert.Equal(expected, new BaseMeasures().NameFor("sum", column));

        [Theory]
        [InlineData("count", true, "none", false)]
        [InlineData("distinctcount", false, "many", false)]
        [InlineData("count", false, "one", false)]
        [InlineData("count", false, "none", true)]
        public void Counts_over_keys_and_relationship_ends_are_left_out(string pick, bool isKey, string role, bool keyPart)
        {
            var ask = Ask("OrderDateKey"); ask.IsKey = isKey; ask.Role = role; ask.KeyPart = keyPart;
            var lines = new List<ChangeItem>();
            ColumnSummarizeFamily.Map(ask, pick, .99, .85, lines);
            Assert.DoesNotContain(lines, l => l.Kind == "create_measure");
            Assert.Contains(lines, l => l.Kind == "set_summarize_by");
        }

        [Fact]
        public async Task One_failed_row_leaves_out_that_row_and_applies_the_rest_in_one_undo_step()
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == "Wide").Copy("RunBadRow");
            await KeepOnlyAsync(f, i => i.Id == "datetable/model" || i.Id == "safefix/Visibility");
            var proposed = await f.Engine.ModelPlanActionAsync("datetable/model", "propose", "human");
            var poison = proposed.Plan.Items.Single(i => i.Id == "datetable/model").ChangeSet.Lines.Single();
            await f.Engine.SetPlanItemAsync(poison.PlanItemId, "MissingDateColumn", true, "human");
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.Equal(2, PlanReadySet.Rows(plan.Items).Count);
            var revision = f.Sessions.Current.Revision;
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.True(ran.Performed, ran.Sentence);
            Assert.Equal(revision + 1, ran.Revision);
            Assert.Equal("True", (await f.Engine.GetObjectAsync("column:Sales/CustomerKey")).Properties["isHidden"]?.ToString(), ignoreCase: true);
            var skip = Assert.Single(ran.Skipped, x => x.What == null);
            Assert.Contains("MissingDateColumn", skip.Reason);
            Assert.DoesNotContain(skip.ItemId, ran.ItemIds);
            await f.Engine.UndoAsync("human");
            Assert.Equal("False", (await f.Engine.GetObjectAsync("column:Sales/CustomerKey")).Properties["isHidden"]?.ToString(), ignoreCase: true);
        }

        [Fact]
        public async Task A_person_runs_the_current_set_but_the_assistant_keeps_the_read_fence()
        {
            using var f = await ModelAsync("Moved.bim");
            var plan = await KeepOnlyAsync(f, i => i.Doer == PlanDoers.Semanticus);
            await f.Engine.SetDescriptionAsync("table:Sales", "Changed since the read.", "human");
            var agent = await f.Engine.ApplyPlanItemsAsync(null, "agent", plan.ReadyDigest);
            Assert.False(agent.Performed);
            Assert.Equal(LocalEngine.PlanMovedSentence, agent.Sentence);
            var person = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.True(person.Performed, person.Sentence);
        }

        [Fact]
        public async Task A_persons_stale_press_respects_a_row_paused_since_the_read()
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == "Wide").Copy("RunHumanPause");
            var plan = await KeepOnlyAsync(f, i => i.Id == "datetable/model" || i.Id == "safefix/Visibility");
            await f.Engine.HoldPlanItemAsync("datetable/model", true);
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.True(ran.Performed, ran.Sentence);
            Assert.DoesNotContain("datetable/model", ran.ItemIds);
            Assert.NotEqual("Time", await f.Sessions.Current.ReadAsync(m => m.Tables["Date"].DataCategory));
        }

        private static string Answers()
        {
            var entries = new List<object>();
            foreach (var table in new[] { "Sales", "Customer" })
            {
                entries.Add(new { digest = "column:" + table + "/Amount", point = "column.summarize", answers = new
                { summarize_by = new { type = "choice", choice = "sum", confidence = .99 } } });
                entries.Add(new { digest = "measure:" + table + "/Total " + table + " Amount", point = "measure.folder_and_format", answers = new
                {
                    display_folder = new { type = "choice", choice = MeasureDefaultsPoint.FolderOptionId("Volume"), confidence = .99 },
                    format_family = new { type = "choice", choice = "decimal", confidence = .99 },
                } });
            }
            entries.Add(new { digest = "column:Sales/Quantity", point = "column.summarize", answers = new
            { summarize_by = new { type = "choice", choice = "sum", confidence = .99 } } });
            entries.Add(new { digest = "measure:Sales/Total Quantity", point = "measure.folder_and_format", answers = new
            {
                display_folder = new { type = "choice", choice = MeasureDefaultsPoint.FolderOptionId("Volume"), confidence = .99 },
                format_family = new { type = "choice", choice = "whole_number", confidence = .99 },
            } });
            return JsonSerializer.Serialize(new { servedModelVersion = "jev-test-1", entries });
        }

        // Hold only the newly-created measures' answers. The first read cannot already have these Tidy rows.
        private sealed class NewMeasuresOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner = new ByObjectRecordedOracle(Answers());
            public readonly TaskCompletionSource<bool> Asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Available => true;
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PointName == "measure.folder_and_format" && request.Questions.Any(q => q.Name.Contains("/Total ", StringComparison.Ordinal)))
                {
                    Asked.TrySetResult(true);
                    await Release.Task.WaitAsync(ct);
                }
                return await _inner.AskAsync(request, ct);
            }
        }

        private static async Task<ModelPlanView> SummarizePlanAsync(DecisionFixture f, IDecisionOracle oracle)
        {
            await f.ConsentAsync();
            // Keep this replay about the two families involved in Kane's press.
            foreach (var point in DecisionRegistry.Points.Where(p => p.Name != "column.summarize" && p.Name != "measure.folder_and_format"))
                await f.Engine.SetDecisionPointEnabledAsync(point.Name, false, "human");
            f.Engine.UseDecisionsForTest(new DecisionService(oracle, new DecisionBudget()));
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await KeepOnlyAsync(f, i => i.Id.StartsWith("jev.", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_legacy_rolled_back_Jev_header_rebuilds_from_the_current_family_lines()
        {
            using var f = await ModelAsync("LegacyRollback.bim");
            var plan = await SummarizePlanAsync(f, new ByObjectRecordedOracle(Answers()));
            var row = PlanReadySet.Rows(plan.Items).First(i => i.Id.StartsWith("jev.column-summarize.", StringComparison.Ordinal));
            f.Engine.SeedPlanMemoryForTest(m => m.Changes[row.Id] = new PlanChangeHeader
            {
                State = ModelPlanChangeStates.RolledBack, Revision = row.ChangeSet.Revision,
                SessionId = "an-earlier-session", ModelIdentity = row.ChangeSet.ModelIdentity,
                SourceVersion = row.ChangeSet.SourceVersion, Lines = row.ChangeSet.Lines,
            });
            plan = await f.Engine.GetModelPlanAsync();
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.Contains(row.Id, ran.ItemIds);
            Assert.DoesNotContain("Pending changes", ran.Sentence);
        }

        [Fact]
        public async Task A_rolled_back_Jev_row_reuses_its_lines_instead_of_the_generic_propose_path()
        {
            using var f = await ModelAsync("RolledBack.bim");
            var plan = await SummarizePlanAsync(f, new ByObjectRecordedOracle(Answers()));
            var row = PlanReadySet.Rows(plan.Items).First(i => i.Id.StartsWith("jev.column-summarize.", StringComparison.Ordinal));
            var bad = row.ChangeSet.Lines.First(l => l.Op == "set_summarize_by");
            await f.Engine.SetPlanItemAsync(bad.PlanItemId, "NotAnAggregate", true, "human");
            plan = await f.Engine.GetModelPlanAsync();
            row = plan.Items.Single(i => i.Id == row.Id);
            var refused = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", row.ChangeSet.Digest);
            Assert.False(refused.Performed);
            Assert.Equal(ModelPlanChangeStates.RolledBack, refused.Plan.Items.Single(i => i.Id == row.Id).ChangeSet.State);
            plan = refused.Plan;
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.Contains(row.Id, ran.ItemIds);
            Assert.DoesNotContain("Pending changes", ran.Sentence);
        }

        [Fact]
        public async Task A_Jev_row_with_only_a_failed_line_names_the_real_reason_and_the_Plan()
        {
            using var f = await ModelAsync("NeedsYou.bim");
            var plan = await SummarizePlanAsync(f, new ByObjectRecordedOracle(Answers()));
            var row = plan.Items.Single(i => i.Id == "jev.column-summarize.table:Customer");
            await KeepOnlyAsync(f, i => i.Id == row.Id);
            var bad = row.ChangeSet.Lines.First(l => l.Op == "set_summarize_by");
            foreach (var line in row.ChangeSet.Lines.Where(l => l.PlanItemId != bad.PlanItemId))
                await f.Engine.SetPlanItemAsync(line.PlanItemId, line.After, false, "human");
            await f.Engine.SetPlanItemAsync(bad.PlanItemId, "NotAnAggregate", true, "human");
            row = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == row.Id);
            var refused = await f.Engine.ModelPlanActionAsync(row.Id, "approve", "human", row.ChangeSet.Digest);
            Assert.Equal(ModelPlanChangeStates.RolledBack, refused.Plan.Items.Single(i => i.Id == row.Id).ChangeSet.State);
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", refused.Plan.ReadyDigest);
            var needsYou = ran.Plan.Items.Single(i => i.Id == row.Id);
            Assert.Equal(PlanStateWords.NeedsYou, needsYou.StateWord);
            Assert.Contains("NotAnAggregate", needsYou.StateNote);
            Assert.Contains("Open this task on the Plan", needsYou.StateNote);
            Assert.DoesNotContain("Pending changes", ran.Sentence);
        }

        [Fact]
        public void Waiting_Jev_rows_are_included_when_the_press_waits_for_advice()
        {
            var waiting = new ModelPlanItem { Id = "jev.measures.table:Sales", Doer = PlanDoers.Semanticus,
                StateWord = PlanStateWords.Waiting, ChangeSet = new ModelPlanChangeSet { Stale = true } };
            Assert.Single(PlanReadySet.JevRowsLookingAgain(new[] { waiting }, new HashSet<string>()));
        }

        [Fact]
        public async Task The_press_waits_when_its_two_new_measures_have_no_Tidy_row_yet()
        {
            using var f = await ModelAsync("NewMeasures.bim");
            var oracle = new NewMeasuresOracle();
            await SummarizePlanAsync(f, oracle);
            var plan = await f.Engine.SkipModelPlanItemAsync("jev.column-summarize.table:Customer", "Outside this test.", "human");
            var press = f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            try
            {
                var started = await Task.WhenAny(oracle.Asked.Task, press, Task.Delay(10000));
                Assert.Same(oracle.Asked.Task, started);
                Assert.False(ReferenceEquals(await Task.WhenAny(press, Task.Delay(100)), press),
                    "The press ended while Jev was answering about two measures it just created.");
            }
            finally { oracle.Release.TrySetResult(true); }
            var ran = await press.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Contains("jev.measures.table:Sales", ran.ItemIds);
            foreach (var name in new[] { "Total Sales Amount", "Total Quantity" })
                Assert.Equal("Volume", (await f.Engine.GetObjectAsync("measure:Sales/" + name)).Properties["displayFolder"]);
        }

        [Fact]
        public async Task One_press_waits_for_new_measure_answers_and_finishes_Kanes_hidden_Sales_and_Tidy_rows()
        {
            using var f = await ModelAsync("Kane.bim");
            var oracle = new NewMeasuresOracle();
            var plan = await SummarizePlanAsync(f, oracle);
            Assert.Contains(plan.Items, i => i.Id == "jev.column-summarize.table:Sales");
            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith("jev.measures.", StringComparison.Ordinal));
            var press = f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            try
            {
                await oracle.Asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var finished = await Task.WhenAny(press, Task.Delay(100));
                Assert.False(ReferenceEquals(finished, press), "The press finished while Jev was answering about its new measures.");
            }
            finally { oracle.Release.TrySetResult(true); }
            var ran = await press.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(ran.Performed, ran.Sentence);
            Assert.Contains("jev.measures.table:Sales", ran.ItemIds);
            Assert.Contains("jev.measures.table:Customer", ran.ItemIds);
            var after = await f.Engine.GetModelPlanAsync();
            Assert.Empty(PlanReadySet.Rows(after.Items));
            foreach (var table in new[] { "Sales", "Customer" })
                Assert.Equal("Volume", (await f.Engine.GetObjectAsync("measure:" + table + "/Total " + table + " Amount")).Properties["displayFolder"]);
        }

        [Fact]
        public async Task Remaining_found_assistant_rows_become_tasks_in_the_existing_queue()
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == "Wide").Copy("RunFoundQueue");
            var plan = await KeepOnlyAsync(f, i => i.Id == "safefix/Visibility" || i.Id == "words/model");
            Assert.Contains(plan.Items, i => i.Id == "words/model" && i.Doer == PlanDoers.Assistant);
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.True(ran.Performed, ran.Sentence);
            var task = Assert.Single(ran.Plan.Items, i => i.OriginLink?.Kind == PlanOriginKinds.Item && i.OriginLink.Ref == "words/model");
            Assert.Contains("queued for your assistant", ran.Sentence);
            // A row that carries the assistant's own proposed lines stays a row, with its lines and what waits for it.
            var own = ran.Plan.Items.Where(i => i.Doer == PlanDoers.Assistant && (i.ChangeSet?.Lines?.Length ?? 0) > 0
                                                && PlanStateWords.IsOpen(i.StateWord)).ToList();
            Assert.DoesNotContain(ran.Plan.Items, i => i.OriginLink?.Kind == PlanOriginKinds.Item && own.Any(o => o.Id == i.OriginLink.Ref));
            // The task is the assistant's now and the queue is on. It is offered at once, or it waits only for the assistant's own row.
            Assert.Equal(PlanDoers.Assistant, task.Doer);
            var queue = await f.Engine.ListAssistantRequestsAsync();
            Assert.Equal(PlanAssistantQueue.On, queue.Switch);
            Assert.True(queue.Requests.Any(r => r.RequestId == task.Id)
                || (task.WaitsFor?.Length > 0 && task.WaitsFor.All(w => own.Any(o => o.Id == w.Id))),
                task.StateWord + " waits for " + string.Join(", ", (task.WaitsFor ?? Array.Empty<PlanWaitRef>()).Select(w => w.Id)));
            Assert.Empty(queue.Held);
        }

        [Fact]
        public async Task Finishing_Semanticus_rows_queues_the_assistant_work_and_keeps_a_paused_queue_paused()
        {
            using var f = await ModelAsync("Queue.bim");
            await KeepOnlyAsync(f, i => i.Doer == PlanDoers.Semanticus);
            ModelPlanActionResult task = null;
            f.Engine.RunReadyCheckedForTest = async () =>
                task = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest { Words = "Describe Sales for people." });
            await f.Engine.SetAssistantQueueAsync(PlanAssistantQueue.Paused);
            var plan = await f.Engine.GetModelPlanAsync();
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.True(ran.Performed, ran.Sentence);
            // The person paused the queue on purpose: the press queues the task but never turns the queue back on (verifier 5b).
            Assert.Contains("1 task is queued for your assistant. The queue is paused, so it waits until you turn it on.", ran.Sentence);
            var queue = await f.Engine.ListAssistantRequestsAsync();
            Assert.Equal(PlanAssistantQueue.Paused, queue.Switch);
            Assert.Equal(PlanAssistantQueue.Paused, ran.Plan.AssistantQueue);
            Assert.Contains(ran.Plan.Items, i => i.Id == task.ItemId && i.Doer == PlanDoers.Assistant);
            Assert.Empty(queue.Held);
        }

        [Fact]
        public async Task A_press_with_only_assistant_rows_left_keeps_a_paused_queue_paused()
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == "Wide").Copy("RunOnlyAssistantPaused");
            var plan = await KeepOnlyAsync(f, i => i.Id == "words/model");
            await f.Engine.SetAssistantQueueAsync(PlanAssistantQueue.Paused);
            plan = await f.Engine.GetModelPlanAsync();
            Assert.Empty(PlanReadySet.Rows(plan.Items));
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.False(ran.Performed, ran.Sentence);
            Assert.Contains("queued for your assistant. The queue is paused, so", ran.Sentence);
            Assert.Contains(ran.Plan.Items, i => i.OriginLink?.Kind == PlanOriginKinds.Item && i.OriginLink.Ref == "words/model");
            Assert.Equal(PlanAssistantQueue.Paused, (await f.Engine.ListAssistantRequestsAsync()).Switch);
        }
        // Verifier (v-runplan): a press with no Semanticus row left must still hand the assistant rows over.
        [Fact]
        public async Task A_press_with_only_assistant_rows_left_queues_them()
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == "Wide").Copy("RunOnlyAssistant");
            var plan = await KeepOnlyAsync(f, i => i.Id == "words/model");
            Assert.Contains(plan.Items, i => i.Id == "words/model" && i.Doer == PlanDoers.Assistant);
            Assert.Empty(PlanReadySet.Rows(plan.Items));
            var ran = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.Contains("queued for your assistant", ran.Sentence);
            var queue = await f.Engine.ListAssistantRequestsAsync();
            Assert.Equal(PlanAssistantQueue.On, queue.Switch);
            // The task is offered at once, or it waits only for the assistant's own proposed row (as in the found-row test above):
            // on this model it waits for asst/set_display_folder, which the assistant sees on its plan read.
            var task = Assert.Single(ran.Plan.Items, r => r.OriginLink?.Kind == PlanOriginKinds.Item && r.OriginLink.Ref == "words/model");
            Assert.Equal(PlanDoers.Assistant, task.Doer);
            Assert.True(queue.Requests.Any(r => r.RequestId == task.Id)
                || (task.WaitsFor?.Length > 0 && task.WaitsFor.All(w => w.Id.StartsWith("asst/", StringComparison.Ordinal))),
                task.StateWord + " waits for " + string.Join(", ", (task.WaitsFor ?? Array.Empty<PlanWaitRef>()).Select(w => w.Id)));
        }
    }
}
