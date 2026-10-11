// The Plan item model and the task store (Plan at the heart, lane E1; contract sections 1, 2 and 15).
//
// What these tests hold, one line each:
//   the frozen wire shape (docs/redesign/contracts/plan-item.json) names every item field and nothing else
//   an asked item has a random id that never repeats, across a restart included
//   a task persists with no assistant attached, and a model with nowhere to keep it says so
//   the state word precedence table, row by row
//   each way done is known
//   "jev" is a source and never a doer
//   a typed target migrates to an aim task and its old id still resolves

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanTaskTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        private static JsonObject Shape() =>
            (JsonObject)JsonNode.Parse(File.ReadAllText(
                Path.Combine(RepoRoot(), "docs", "redesign", "contracts", "plan-item.json")));

        private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name.Substring(1);

        private static string[] WireNames(Type t) =>
            t.GetMembers(BindingFlags.Public | BindingFlags.Instance).OfType<PropertyInfo>()
             .Select(p => Camel(p.Name)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        private static string[] Strings(JsonNode node) =>
            ((JsonArray)node).Select(n => n.GetValue<string>()).ToArray();

        // ---- the frozen wire shape ----------------------------------------------------------------------

        [Fact]
        public void The_frozen_shape_file_names_every_item_field_and_nothing_else()
        {
            var shape = Shape();
            var rows = (JsonArray)shape["rows"];
            Assert.Equal(12, rows.Count);   // one per row of the mockup's sample (plan-heart.html, BASE)

            var fields = WireNames(typeof(ModelPlanItem));
            foreach (var row in rows.OfType<JsonObject>())
                Assert.Equal(fields, row.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());

            // Every row reads back into the engine's own type, so a field of the wrong kind fails here.
            var wire = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            foreach (var row in rows)
                Assert.NotNull(row.Deserialize<ModelPlanItem>(wire));

            Assert.Equal(WireNames(typeof(PlanStrip)),
                ((JsonObject)shape["strip"]).Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void The_frozen_shape_file_spells_every_word_the_engine_spells()
        {
            var enums = (JsonObject)Shape()["enums"];
            Assert.Equal(PlanSources.All, Strings(enums["source"]));
            Assert.Equal(PlanDoers.All, Strings(enums["doer"]));
            Assert.Equal(PlanStateWords.InPrecedence, Strings(enums["stateWord"]));
            Assert.Equal(PlanDoneWhen.All, Strings(enums["doneWhen"]));
            Assert.Equal(PlanOriginKinds.All, Strings(enums["originKind"]));
            Assert.Equal(PlanAssistantQueue.All, Strings(enums["assistantQueue"]));

            foreach (var row in ((JsonArray)Shape()["rows"]).OfType<JsonObject>())
            {
                Assert.Contains(row["source"].GetValue<string>(), PlanSources.All);
                Assert.Contains(row["doer"].GetValue<string>(), PlanDoers.All);
                Assert.Contains(row["stateWord"].GetValue<string>(), PlanStateWords.InPrecedence);
                Assert.Contains(row["doneWhen"].GetValue<string>(), PlanDoneWhen.All);
                // Order is only ever on a row the assistant does (contract 1.2).
                if (row["order"] != null) Assert.Equal(PlanDoers.Assistant, row["doer"].GetValue<string>());
            }
        }

        [Fact]
        public void Jev_is_a_source_and_never_a_doer()
        {
            Assert.DoesNotContain("jev", PlanDoers.All);
            Assert.Contains("jev", PlanSources.All);
            foreach (var row in ((JsonArray)Shape()["rows"]).OfType<JsonObject>())
                Assert.NotEqual("jev", row["doer"].GetValue<string>());
        }
            // ---- the engine -------------------------------------------------------------------------------------

        /// <summary>The small gappy model the target tests use, saved so it has a durable identity.</summary>
        private static async Task<DecisionFixture> GappyAsync(string file, bool pro = false)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "City", "String", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            if (file != null) await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        private static Task<ModelPlanActionResult> AskAsync(DecisionFixture f, string words, string doer = null,
                                                             string doneWhen = null, string[] objects = null,
                                                             PlanOriginLink origin = null, string page = null) =>
            f.Engine.AddPlanTaskAsync(new PlanTaskRequest
            {
                Words = words, Doer = doer, DoneWhen = doneWhen, Objects = objects, OriginLink = origin, SourcePage = page,
            });

        private static ModelPlanItem Row(ModelPlanView plan, string id) =>
            Assert.Single(plan.Items.Where(i => i.Id == id));

        [Fact]
        public void A_new_id_is_pt_and_eight_hex_and_ten_thousand_never_repeat()
        {
            // Through NewIdFor, the path every add takes. NewId alone is 32 random bits, so ten thousand of them
            // collide about one run in ninety (n squared over 2 to the 33rd); it did on the slice 7 base run.
            // The guarantee is NewIdFor's check against the store, so that is what is proven here.
            var state = new PlanMemoryState();
            for (var n = 0; n < 10000; n++)
                state.Tasks.Add(new PlanTask { Id = PlanTasks.NewIdFor(state), Words = "t" });
            var ids = state.Tasks.Select(t => t.Id).ToArray();
            Assert.All(ids, id => Assert.Matches("^pt-[0-9a-f]{8}$", id));
            Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public async Task Task_ids_never_repeat_across_a_restart_and_the_old_ones_still_resolve()
        {
            using var f = await GappyAsync("TaskIds.bim");
            var first = new List<string>();
            for (var i = 0; i < 12; i++) first.Add((await AskAsync(f, "First run task " + i)).ItemId);

            // A restart: a new engine over the same workspace, opening the same file.
            using var sessions = new SessionManager();
            using var again = new LocalEngine(sessions, TestEntitlements.Free, f.Workspace);
            await again.OpenAsync(Path.Combine(f.Workspace, "TaskIds.bim"));
            var second = new List<string>();
            for (var i = 0; i < 12; i++)
                second.Add((await again.AddPlanTaskAsync(new PlanTaskRequest { Words = "Second run task " + i })).ItemId);

            var all = first.Concat(second).ToArray();
            Assert.All(all, id => Assert.Matches("^pt-[0-9a-f]{8}$", id));
            Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
            var plan = await again.GetModelPlanAsync();
            Assert.All(first, id => Row(plan, id));
        }

        [Fact]
        public async Task A_task_persists_with_no_assistant_attached_and_starts_with_your_assistant()
        {
            using var f = await GappyAsync("TaskKept.bim");
            var added = await AskAsync(f, "Describe the Sales measures");

            // Nothing is attached anywhere in this test: the task is stored, not queued.
            var row = Row(added.Plan, added.ItemId);
            Assert.Equal(PlanSources.Words, row.Source);
            Assert.Equal(PlanDoers.Assistant, row.Doer);         // Kane, 24 Sep: a typed task's doer
            Assert.Equal(PlanStateWords.Ready, row.StateWord);
            Assert.Equal(PlanTasks.OnNextCallNote, row.StateNote);
            Assert.Equal(PlanDoneWhen.AssistantSays, row.DoneWhen);
            Assert.Equal(1, row.Order);

            await f.Engine.CreateModelAsync("Somewhere Else", 1604);
            await f.Engine.OpenAsync(Path.Combine(f.Workspace, "TaskKept.bim"));
            f.UseNull();
            var back = Row(await f.Engine.GetModelPlanAsync(), added.ItemId);
            Assert.Equal("Describe the Sales measures", back.Title);
            Assert.Equal(PlanDoers.Assistant, back.Doer);
        }

        [Fact]
        public async Task A_model_with_nowhere_to_keep_tasks_keeps_them_for_the_session_and_says_so()
        {
            using var f = await GappyAsync(file: null);   // never saved: no durable identity
            var added = await AskAsync(f, "Check the Customer table");

            Assert.Contains(PlanTasks.NotKeptSentence, added.Sentence, StringComparison.Ordinal);
            var row = Row(added.Plan, added.ItemId);
            Assert.Equal(PlanTasks.NotKeptSentence, row.Sub);
            Assert.Contains(PlanTasks.NotKeptSentence, row.Why);

            // Still there on the next read of the same session.
            Row(await f.Engine.GetModelPlanAsync(), added.ItemId);

            // Another model: the session's tasks do not follow.
            await f.Engine.CreateModelAsync("Another", 1604);
            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == added.ItemId);
        }

        // ---- the state word -------------------------------------------------------------------------------

        /// <summary>The precedence table of contract 1.4, row by row: each fact on its own gives its word, and
        /// with every fact BELOW it also true it still gives its word, so the order is proven, not assumed.</summary>
        [Theory]
        [InlineData(0, PlanStateWords.Done)]
        [InlineData(1, PlanStateWords.SetAside)]
        [InlineData(2, PlanStateWords.Checking)]
        [InlineData(3, PlanStateWords.OnIt)]
        [InlineData(4, PlanStateWords.Paused)]
        [InlineData(5, PlanStateWords.Waiting)]
        [InlineData(6, PlanStateWords.NeedsYou)]
        public void The_state_word_follows_the_precedence_table(int row, string word)
        {
            PlanStateFacts With(int from, int to)
            {
                var f = new PlanStateFacts();
                for (var i = from; i <= to; i++)
                    switch (i)
                    {
                        case 0: f.Done = true; break;
                        case 1: f.SetAside = true; break;
                        case 2: f.Checking = true; break;
                        case 3: f.OnIt = true; break;
                        case 4: f.Held = true; break;
                        case 5: f.Waiting = true; break;
                        case 6: f.NeedsYou = true; break;
                    }
                return f;
            }
            Assert.Equal(word, PlanTasks.StateWordOf(With(row, row)));
            Assert.Equal(word, PlanTasks.StateWordOf(With(row, 6)));
            Assert.Equal(PlanStateWords.InPrecedence[row], word);
        }

        [Fact]
        public void A_waiting_row_keeps_the_note_an_earlier_step_wrote()
        {
            // Lane E2 fills WaitsFor and, where the plan's order decided the waits, writes this note. The state
            // step runs after it and must not wipe it (E2's patch to E1, 24 Sep).
            var row = new ModelPlanItem
            {
                Id = "timeintel/measure:Sales/Sales Amount", Status = ModelPlanStatuses.Ready, Doer = PlanDoers.Semanticus,
                WaitsFor = new[] { new PlanWaitRef { Id = "datetable/table:Date", Title = "Mark Date as the date table" } },
                StateNote = "Semanticus ordered these by the plan's order.",
            };
            PlanTasks.StampState(row, null, false, PlanAssistantQueue.On);
            Assert.Equal(PlanStateWords.Waiting, row.StateWord);
            Assert.Equal("Semanticus ordered these by the plan's order.", row.StateNote);
        }

        [Fact]
        public void Nothing_true_is_ready()
        {
            Assert.Equal(PlanStateWords.Ready, PlanTasks.StateWordOf(new PlanStateFacts()));
            Assert.Equal(PlanStateWords.Ready, PlanStateWords.InPrecedence.Last());
        }

        [Fact]
        public async Task Every_row_on_a_real_plan_carries_a_source_a_doer_and_a_state_word()
        {
            using var f = await GappyAsync("TaskWords.bim");
            await AskAsync(f, "Tidy the Customer table", doer: "you");
            var plan = await f.Engine.GetModelPlanAsync();

            Assert.NotEmpty(plan.Items);
            Assert.All(plan.Items, i =>
            {
                Assert.Contains(i.Source, PlanSources.All);
                Assert.Contains(i.Doer, PlanDoers.All);
                Assert.Contains(i.StateWord, PlanStateWords.InPrecedence);
                Assert.Contains(i.DoneWhen, PlanDoneWhen.All);
            });
            // A row that is the person's to do reads Needs you, unless it waits for another row: Waiting comes
            // before Needs you in the precedence (contract 1.4), and lane E2 fills WaitsFor.
            Assert.All(plan.Items.Where(i => i.Doer == PlanDoers.You && i.Status != ModelPlanStatuses.Done),
                i => Assert.Equal(i.WaitsFor is { Length: > 0 } ? PlanStateWords.Waiting : PlanStateWords.NeedsYou, i.StateWord));
            Assert.Contains(plan.Items, i => i.Doer == PlanDoers.You && i.StateWord == PlanStateWords.NeedsYou);
            Assert.Equal(PlanAssistantQueue.On, plan.AssistantQueue);
        }

        [Fact]
        public void Jev_rows_get_a_doer_by_kind()
        {
            Assert.Equal(PlanDoers.Semanticus, PlanTasks.DoerOf(ModelPlanOwners.Jev));
            Assert.Equal(PlanDoers.Semanticus, PlanTasks.DoerOf(ModelPlanOwners.Auto));
            Assert.Equal(PlanDoers.You, PlanTasks.DoerOf(ModelPlanOwners.You));
            Assert.Equal(PlanDoers.Assistant, PlanTasks.DoerOf(ModelPlanOwners.Assistant));
            Assert.Equal(PlanSources.Jev, PlanTasks.SourceOf("jev.measures.table:Sales", "asst/"));
            Assert.Equal(PlanSources.Words, PlanTasks.SourceOf("asst/set_description", "asst/"));
            Assert.Equal(PlanSources.Found, PlanTasks.SourceOf("safefix/Visibility", "asst/"));
        }

        // ---- how done is known ----------------------------------------------------------------------------

        [Fact]
        public async Task A_model_check_task_closes_when_the_finding_it_links_is_gone()
        {
            using var f = await GappyAsync("TaskModelCheck.bim");
            var link = new PlanOriginLink { Kind = PlanOriginKinds.Finding, Ref = "DESC-MEASURE|measure:Sales/Total Sales" };
            var added = await AskAsync(f, "Describe Total Sales", origin: link, page: "readiness");
            var row = Row(added.Plan, added.ItemId);
            Assert.Equal(PlanDoneWhen.ModelCheck, row.DoneWhen);
            Assert.Equal(PlanSources.You, row.Source);
            Assert.Equal("readiness", row.SourcePage);
            Assert.Equal(PlanStateWords.Ready, row.StateWord);

            // Said done before the model changed: Applied, checking, not Done.
            var said = await f.Engine.ModelPlanActionAsync(added.ItemId, "done", "human");
            Assert.Equal(PlanStateWords.Checking, Row(said.Plan, added.ItemId).StateWord);

            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "All sales, in dollars.", "human");
            Assert.Equal(PlanStateWords.Done, Row(await f.Engine.GetModelPlanAsync(), added.ItemId).StateWord);
        }

        [Fact]
        public async Task A_has_words_task_is_done_when_every_named_object_has_words()
        {
            using var f = await GappyAsync("TaskHasWords.bim");
            var objects = new[] { "measure:Sales/Total Sales", "column:Customer/City" };
            var added = await AskAsync(f, "Describe two things", doneWhen: "has_words", objects: objects);
            // Open, and not done: Ready, or Waiting when a row earlier in the plan writes the same objects (lane E3:
            // a task's footprint is the objects it names, so it waits for the plan's own describe row).
            static string OpenWord(ModelPlanItem r) => r.WaitsFor is { Length: > 0 } ? PlanStateWords.Waiting : PlanStateWords.Ready;
            Assert.Equal(OpenWord(Row(added.Plan, added.ItemId)), Row(added.Plan, added.ItemId).StateWord);
            Assert.Equal("Semanticus checks the model: all 2 have words.", Row(added.Plan, added.ItemId).DoneText);

            await f.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "All sales.", "human");
            var half = Row(await f.Engine.GetModelPlanAsync(), added.ItemId);
            Assert.Equal(OpenWord(half), half.StateWord);

            await f.Engine.SetDescriptionAsync("column:Customer/City", "Where the customer lives.", "human");
            Assert.Equal(PlanStateWords.Done, Row(await f.Engine.GetModelPlanAsync(), added.ItemId).StateWord);
        }

        [Theory]
        [InlineData(null, PlanDoneWhen.AssistantSays)]
        [InlineData("you", PlanDoneWhen.YouMark)]
        public async Task A_task_the_doer_closes_is_done_when_it_is_marked_done(string doer, string doneWhen)
        {
            using var f = await GappyAsync("TaskSays" + (doer ?? "asst") + ".bim");
            var added = await AskAsync(f, "Answer a question about Sales", doer: doer);
            Assert.Equal(doneWhen, Row(added.Plan, added.ItemId).DoneWhen);
            Assert.NotEqual(PlanStateWords.Done, Row(added.Plan, added.ItemId).StateWord);

            var revision = f.Sessions.Current.Revision;
            var done = await f.Engine.ModelPlanActionAsync(added.ItemId, "done", "human");
            Assert.False(done.Performed);
            Assert.Equal(revision, f.Sessions.Current.Revision);
            var row = Row(done.Plan, added.ItemId);
            Assert.Equal(PlanStateWords.Done, row.StateWord);
            Assert.Null(row.Order);
        }

        [Fact]
        public async Task A_check_the_engine_cannot_run_is_refused_when_the_task_is_added()
        {
            using var f = await GappyAsync("TaskBadCheck.bim");
            await Assert.ThrowsAsync<ArgumentException>(() => AskAsync(f, "Make it better", doneWhen: "model_check"));
            await Assert.ThrowsAsync<ArgumentException>(() => AskAsync(f, "Describe things", doneWhen: "has_words"));
            await Assert.ThrowsAsync<ArgumentException>(() => AskAsync(f, "Do it", doer: "semanticus"));
            // Words longer than a title are kept whole (UAT S12-1-005); only text past the one limit is refused.
            await Assert.ThrowsAsync<ArgumentException>(() => AskAsync(f, new string('x', PlanTasks.MaxFullTextChars + 1)));
        }

        // ---- migration (contract 15) ----------------------------------------------------------------------

        [Fact]
        public async Task A_pre_upgrade_file_migrates_targets_to_aims_keeping_dates_and_old_ids()
        {
            using var f = await GappyAsync("TaskMigrate.bim");
            // Make the sidecar exist under this model's identity, then write the OLD shape into it by hand. The seed
            // must change something: a write that changes nothing is not made, so it would leave no file (F-291).
            // The skip it leaves is replaced below.
            Assert.True(f.Engine.SeedPlanMemoryForTest(s => s.Skips.Add(new PlanSkip { ItemId = "seed", Reason = "seed" })));
            var file = Directory.GetFiles(f.Home, PlanMemory.FileName, SearchOption.AllDirectories).Single();
            var whole = (JsonObject)JsonNode.Parse(File.ReadAllText(file));
            var entry = (JsonObject)((JsonObject)whole["models"]).Single().Value;
            var added = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
            var finished = new DateTimeOffset(2026, 9, 3, 17, 30, 0, TimeSpan.Zero);
            entry["tasks"] = new JsonArray();
            entry["targets"] = new JsonArray(
                new JsonObject { ["id"] = "every-measure-has-a-check", ["text"] = "Every measure has a check",
                                 ["section"] = "Trust", ["when"] = added.ToString("o") },
                new JsonObject { ["id"] = "a-marked-date-table", ["text"] = "A marked date table",
                                 ["section"] = "Structure", ["when"] = added.ToString("o"), ["doneUtc"] = finished.ToString("o") });
            entry["skips"] = new JsonArray(new JsonObject
            {
                ["itemId"] = "target/every-measure-has-a-check", ["reason"] = "later", ["when"] = added.ToString("o"),
            });
            File.WriteAllText(file, whole.ToJsonString());

            var plan = await f.Engine.GetModelPlanAsync();

            var open = Assert.Single(plan.Targets.Where(t => t.Text == "Every measure has a check"));
            var done = Assert.Single(plan.Targets.Where(t => t.Text == "A marked date table"));
            Assert.Matches("^pt-[0-9a-f]{8}$", open.Id);
            Assert.Equal(added, open.When);
            Assert.Equal(finished, done.DoneUtc);

            var doneRow = Row(plan, done.Id);
            Assert.Equal(PlanStateWords.Done, doneRow.StateWord);
            Assert.Equal(PlanDoers.You, doneRow.Doer);                   // aims keep the person as doer
            Assert.Equal(PlanDoneWhen.YouMark, doneRow.DoneWhen);
            Assert.True(PlanStateWords.IsOpen(Row(plan, open.Id).StateWord) || Row(plan, open.Id).StateWord == PlanStateWords.SetAside);
            // The old skip, recorded against the old item id, still sets the aim aside.
            Assert.Equal(PlanStateWords.SetAside, Row(plan, open.Id).StateWord);

            // The file now holds tasks and no targets: the migration was written, once.
            var after = (JsonObject)((JsonObject)JsonNode.Parse(File.ReadAllText(file))["models"]).Single().Value;
            Assert.Empty((JsonArray)after["targets"]);
            Assert.Equal(2, ((JsonArray)after["tasks"]).Count);

            // The old ids still resolve: the old item id through "done", the old target id through remove.
            await f.Engine.UnskipModelPlanItemAsync("target/every-measure-has-a-check", "human");
            var marked = await f.Engine.ModelPlanActionAsync("target/every-measure-has-a-check", "done", "human");
            Assert.Equal(open.Id, marked.ItemId);
            Assert.Equal(PlanStateWords.Done, Row(marked.Plan, open.Id).StateWord);
            var removed = await f.Engine.RemovePlanTargetAsync("a-marked-date-table", "human");
            Assert.DoesNotContain(removed.Items, i => i.Id == done.Id);
        }

        [Fact]
        public void Migration_is_idempotent_and_a_completed_target_is_never_open()
        {
            var state = new PlanMemoryState();
            state.Targets.Add(new ModelPlanTarget { Id = "t1", Text = "One", Section = "Meaning", When = DateTimeOffset.UnixEpoch, DoneUtc = DateTimeOffset.UnixEpoch.AddDays(1) });
            PlanTasks.MigrateTargets(state);
            var task = Assert.Single(state.Tasks);
            Assert.Equal(PlanTaskStates.Done, task.State);
            Assert.Equal(PlanDoers.You, task.DoneBy);
            Assert.False(PlanTasks.IsOpenTask(task));
            Assert.Equal(new[] { "t1", "target/t1" }, task.Aliases);

            state.Targets.Add(new ModelPlanTarget { Id = "t1", Text = "One", Section = "Meaning" });
            PlanTasks.MigrateTargets(state);
            Assert.Single(state.Tasks);
        }

        // ---- a Send given twice (Sol 6 slice 9 review) ------------------------------------------------------

        private static PlanTaskRequest FindingSend(string sendKey) => new PlanTaskRequest
        {
            Words = "Fix \"Measures have a description\" on Total Sales",
            Brief = "Write a business description for Sales[Total Sales].",
            SourcePage = "readiness",
            Objects = new[] { "measure:Sales/Total Sales" },
            DoneWhen = PlanDoneWhen.ModelCheck,
            OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Finding, Ref = "DESC-MEASURE|measure:Sales/Total Sales" },
            SendKey = sendKey,
        };

        [Fact]
        public async Task The_same_send_key_twice_gives_one_task_and_the_row_carries_the_key()
        {
            using var f = await GappyAsync("SendTwice.bim");
            const string key = "readiness|DESC-MEASURE|measure:Sales/Total Sales";
            var first = await f.Engine.AddPlanTaskAsync(FindingSend(key));
            // A reconnect forgot the page's memory, so the person presses Give again: the engine answers with the
            // task already on the plan instead of adding a second one for the assistant to do.
            var second = await f.Engine.AddPlanTaskAsync(FindingSend(key));

            Assert.Equal(first.ItemId, second.ItemId);
            Assert.Single(second.Plan.Items.Where(i => i.OriginLink?.SendKey == key));
            Assert.Equal(key, Row(second.Plan, first.ItemId).OriginLink.SendKey);

            // The key is scoped to the page it came from: the same key from another page is another task.
            var other = FindingSend(key);
            other.SourcePage = "bpa";
            other.DoneWhen = PlanDoneWhen.AssistantSays;
            other.OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Page, Ref = "bpa" };
            Assert.NotEqual(first.ItemId, (await f.Engine.AddPlanTaskAsync(other)).ItemId);

            // A closed task no longer holds the key: giving it again after it is done is new work.
            await f.Engine.ModelPlanActionAsync(first.ItemId, "done", "human");
            var third = await f.Engine.AddPlanTaskAsync(FindingSend(key));
            Assert.NotEqual(first.ItemId, third.ItemId);

            // A task typed with no key is never folded into another.
            var a = await AskAsync(f, "Describe the Sales measures");
            var b = await AskAsync(f, "Describe the Sales measures");
            Assert.NotEqual(a.ItemId, b.ItemId);
        }

        [Fact]
        public async Task The_same_send_key_survives_a_restart_and_still_gives_one_task()
        {
            using var f = await GappyAsync("SendRestart.bim");
            const string key = "docs|measure:Sales/Total Sales|business";
            var first = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest
            {
                Words = "Write the Business context docs for Total Sales", SourcePage = "docs", SendKey = key,
            });

            using var sessions = new SessionManager();
            using var again = new LocalEngine(sessions, TestEntitlements.Free, f.Workspace);
            await again.OpenAsync(Path.Combine(f.Workspace, "SendRestart.bim"));
            var second = await again.AddPlanTaskAsync(new PlanTaskRequest
            {
                Words = "Write the Business context docs for Total Sales", SourcePage = "docs", SendKey = key,
            });
            Assert.Equal(first.ItemId, second.ItemId);
        }

        [Fact]
        public async Task A_send_key_over_the_limit_is_refused_in_plain_words()
        {
            using var f = await GappyAsync("SendLong.bim");
            var e = await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.AddPlanTaskAsync(new PlanTaskRequest
            {
                Words = "w", SourcePage = "docs", SendKey = new string('k', PlanTasks.MaxSendKeyChars + 1),
            }));
            Assert.DoesNotContain("\u2014", e.Message);
        }

        [Fact]
        public void The_closed_tasks_are_capped_and_the_oldest_go_first()
        {
            var state = new PlanMemoryState();
            var t0 = DateTimeOffset.UnixEpoch;
            for (var i = 0; i < PlanMemory.MaxClosedTasks + 5; i++)
                state.Tasks.Add(new PlanTask { Id = "pt-" + i.ToString("x8"), Words = "w", State = PlanTaskStates.Done, DoneUtc = t0.AddMinutes(i) });
            state.Tasks.Add(new PlanTask { Id = "pt-open", Words = "w", Doer = PlanDoers.Assistant, State = PlanTaskStates.Open });
            PlanTasks.Trim(state);
            Assert.Equal(PlanMemory.MaxClosedTasks + 1, state.Tasks.Count);
            Assert.DoesNotContain(state.Tasks, t => t.Id == "pt-00000000");
            Assert.Contains(state.Tasks, t => t.Id == "pt-open");
            Assert.Equal(1, state.Tasks.Single(t => t.Id == "pt-open").Order);
        }
    }
}
