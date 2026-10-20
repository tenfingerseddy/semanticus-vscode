using System;
using System.IO;
using System.Linq;
using Semanticus.Analysis;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Hero run 19, J2 (13 Oct 2026): "get this model as good as it can be" stopped with six questions. Every assistant row now says
    /// when it is done and what to choose without asking, the date table row tells the assistant to add a calculated date table (or says
    /// why it cannot), and a Waiting row always says who it waits for.
    /// </summary>
    public sealed class HandsOffRowsTests
    {
        private static ReadinessFinding DateMark() => new ReadinessFinding
        {
            RuleId = "DATE-MARK", RuleTitle = "Mark the date table", Category = "DataAgentConfig", ObjectRef = "table:Sales", ObjectName = "Sales",
            Message = "No table is marked as the date table.",
        };

        [Fact]
        public void An_assistant_row_ends_with_its_done_when_and_its_default()
        {
            var rows = new[] { LocalEngine.CompatItemId, LocalEngine.CoverageBpaItemId, LocalEngine.CoverageAiItemId, "cleanup/model", "datetable/model",
                               "proposal/duplicate-measures", "proposal/collisions", "proposal/synonyms", "proposal/limits", "proposal/unknown", "ship/unsaved" }
                .Select(id => new ModelPlanItem { Id = id, Who = ModelPlanOwners.Assistant, Why = new[] { "first reason" } }).ToArray();
            AssistantDefaults.Apply(rows);
            foreach (var r in rows)
            {
                // The person reads Why in Details; the lines are the assistant's brief and never go there.
                Assert.Equal(new[] { "first reason" }, r.Why);
                Assert.StartsWith(AssistantDefaults.DoneWhenLead, r.Brief, StringComparison.Ordinal);
                Assert.Contains(AssistantDefaults.DefaultLead, r.Brief);
            }
            var once = rows.Select(r => r.Brief).ToArray();
            AssistantDefaults.Apply(rows);   // again: nothing doubles
            Assert.Equal(once, rows.Select(r => r.Brief).ToArray());
        }

        [Fact]
        public void The_duplicate_measures_default_counts_report_use_and_hides_a_used_duplicate_instead_of_deleting_it()
        {
            // get_lineage cannot see a report, so the default must not tell the assistant to delete on lineage alone (verifier, v-drive-finish).
            var choice = AssistantDefaults.For("proposal/duplicate-measures").Choice;
            Assert.Contains("check_reports", choice);
            Assert.Contains("remove_safe_objects", choice);
            Assert.Contains("no report uses it", choice);
            Assert.Contains("hide it (edit_objects, hidden) and write on the row why", choice);
            Assert.DoesNotContain("delete_objects", choice);
            Assert.DoesNotContain("delete the rest", choice);
        }

        [Fact]
        public void The_defaults_decide_the_six_questions_of_the_run()
        {
            Assert.Contains("keep the measure that is used", AssistantDefaults.For("proposal/duplicate-measures").Choice);
            Assert.Contains("Remove the generic", AssistantDefaults.For("proposal/collisions").Choice);
            Assert.Contains("remove_safe_objects", AssistantDefaults.For("cleanup/model").Choice);
            Assert.Contains("raise it", AssistantDefaults.For(LocalEngine.CompatItemId).Choice);
            Assert.Contains("generate_date_table", AssistantDefaults.For("datetable/model").Choice);
            Assert.Contains("save_model and no path", AssistantDefaults.For("ship/unsaved").Choice);
            Assert.DoesNotContain("save a copy next to it", AssistantDefaults.For("ship/unsaved").Choice);   // 18 Oct: that wording made three model files beside the person's own
            Assert.All(new[] { "proposal/duplicate-measures", "proposal/collisions", "cleanup/model", LocalEngine.CompatItemId, "proposal/synonyms", "ship/unsaved" },
                id => Assert.DoesNotContain("ask the person first", AssistantDefaults.For(id).Choice.Replace("do not ask first", "")));
        }

        [Fact]
        public void A_person_row_gets_no_default()
        {
            var row = new ModelPlanItem { Id = "proposal/relationships", Who = ModelPlanOwners.You, Why = new[] { "one" } };
            AssistantDefaults.Apply(new[] { row });
            Assert.Single(row.Why);
            Assert.Null(row.Brief);
        }

        [Fact]
        public void The_date_table_row_tells_the_assistant_to_add_one_when_a_date_column_exists()
        {
            var row = LocalEngine.DateTableItem(new[] { DateMark() }, null, hasDateColumn: true);
            Assert.Equal(ModelPlanOwners.Assistant, row.Who);
            Assert.Contains(row.Why, w => w.Contains("generate_date_table"));
        }

        [Fact]
        public void The_date_table_row_says_why_when_no_table_has_a_date_column()
        {
            var row = LocalEngine.DateTableItem(new[] { DateMark() }, null, hasDateColumn: false);
            Assert.Contains(row.Why, w => w.Contains("No table in this model has a date column"));
            Assert.DoesNotContain(row.Why, w => w.Contains("generate_date_table"));
        }

        // Verifier probe (v-hands-off): CAL-TI-NO-CALENDAR fires on a CL 1701+ model with classic time-intelligence DAX and no
        // calendar object, which is the usual model that ALREADY has a marked date table (so DATE-MARK is quiet). The calendar row
        // must not tell the person "no table is the date table" nor send the assistant to add a second date table.
        [Fact]
        public void The_calendar_row_on_a_model_with_a_marked_date_table_does_not_ask_for_a_new_date_table()
        {
            var calendar = new ReadinessFinding
            {
                RuleId = "CAL-TI-NO-CALENDAR", RuleTitle = "Classic time-intelligence DAX but no calendars", Category = "Relationships",
                ObjectRef = "model:Model", ObjectName = "Model", Message = "3 measure(s) use classic time-intelligence DAX but this model defines no calendars.",
            };
            var row = LocalEngine.DateTableItem(new[] { calendar }, null, hasDateColumn: true);
            Assert.Equal("calendar/model", row.Id);
            Assert.DoesNotContain(row.Why, w => w.Contains("no table is the date table"));
            Assert.DoesNotContain(row.Why, w => w.Contains("generate_date_table"));
        }

        private static ModelPlanItem Waiting(string doer, string blockerDoer)
        {
            var item = new ModelPlanItem
            {
                Id = "proposal/synonyms", Doer = doer, Who = doer == PlanDoers.Assistant ? ModelPlanOwners.Assistant : ModelPlanOwners.You,
                Status = ModelPlanStatuses.Ready, WaitsFor = new[] { new PlanWaitRef { Id = "datetable/model", Title = "Fix 2 best-practice issues", Doer = blockerDoer } },
            };
            return item;
        }

        [Fact]
        public void A_waiting_row_says_who_it_waits_for_and_why()
        {
            var row = Waiting(PlanDoers.Assistant, PlanDoers.You);
            PlanTasks.StampState(row, null, false, PlanAssistantQueue.On, null, () => false);
            Assert.Equal(PlanStateWords.Waiting, row.StateWord);
            Assert.StartsWith("Waiting for you: Fix 2 best-practice issues", row.StateNote);
            Assert.Contains("same things", row.StateNote);
        }

        [Fact]
        public void A_waiting_assistant_row_with_no_assistant_says_so()
        {
            var away = Waiting(PlanDoers.Assistant, PlanDoers.Assistant);
            PlanTasks.StampState(away, null, false, PlanAssistantQueue.On, null, () => true);
            Assert.Equal(PlanTasks.WaitingForAssistantNote, away.StateNote);
            var byHand = Waiting(PlanDoers.Assistant, PlanDoers.Assistant);
            PlanTasks.StampState(byHand, null, false, PlanAssistantQueue.Paused, null, () => false);
            Assert.Equal("Waiting for your assistant. Ask it to run the plan.", byHand.StateNote);
        }

        [Fact]
        public void A_waiting_row_the_person_took_never_says_to_ask_the_assistant()
        {
            // UAT F17: Who does it > Me moved the row under You, but it still read "Waiting, ask your assistant", because the words went by the
            // row's natural owner (Who) and ignored the person's choice (Doer). The row keeps its Who; the words follow the Doer.
            var mine = Waiting(PlanDoers.Assistant, PlanDoers.You);
            mine.Doer = PlanDoers.You;   // the person's choice, kept as residue; Who stays "asst"
            Assert.Equal(ModelPlanOwners.Assistant, mine.Who);
            foreach (var queue in new[] { PlanAssistantQueue.On, PlanAssistantQueue.Paused })
            {
                PlanTasks.StampState(mine, null, false, queue, null, () => true);
                Assert.DoesNotContain("assistant", mine.StateNote ?? "", StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void A_ready_assistant_row_with_no_assistant_connected_says_to_ask_it()
        {
            var away = Waiting(PlanDoers.Assistant, PlanDoers.Assistant); away.WaitsFor = Array.Empty<PlanWaitRef>();
            PlanTasks.StampState(away, null, false, PlanAssistantQueue.On, null, () => true);
            Assert.Equal(PlanStateWords.Ready, away.StateWord);
            Assert.Equal(PlanTasks.AskAssistantNote, away.StateNote);
            var here = Waiting(PlanDoers.Assistant, PlanDoers.Assistant); here.WaitsFor = Array.Empty<PlanWaitRef>();
            PlanTasks.StampState(here, null, false, PlanAssistantQueue.On, null, () => false);
            Assert.NotEqual(PlanTasks.AskAssistantNote, here.StateNote);
            var byHand = Waiting(PlanDoers.Assistant, PlanDoers.Assistant); byHand.WaitsFor = Array.Empty<PlanWaitRef>();
            PlanTasks.StampState(byHand, null, false, PlanAssistantQueue.Paused, null, () => false);
            Assert.Contains("Ask your assistant to run the plan", byHand.StateNote);
        }

        [Fact]
        public void A_limit_finding_does_not_tell_the_assistant_to_ask_the_person()
        {
            var f = new ReadinessFinding { RuleId = "LIMIT-TABLES", Message = "The model has 900 tables." };
            var brief = LocalEngine.AirBrief(f);
            Assert.DoesNotContain("Ask the person", brief);
            Assert.Contains("Do not ask", brief);
        }

        [Fact]
        public async System.Threading.Tasks.Task Every_assistant_row_on_a_messy_model_carries_its_answer_and_no_row_waits_without_a_reason()
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == "Wide").Copy("handsoff");
            var plan = await f.Engine.GetModelPlanAsync();
            var mine = plan.Items.Where(i => i.Who == ModelPlanOwners.Assistant && !PlanTasks.IsTaskId(i.Id)).ToArray();
            Assert.NotEmpty(mine);
            foreach (var r in mine)
            {
                var brief = plan.AssistantBriefs[r.Id];
                Assert.StartsWith(AssistantDefaults.DoneWhenLead, brief, StringComparison.Ordinal);
                Assert.Contains(AssistantDefaults.DefaultLead, brief);
                Assert.DoesNotContain(r.Why, w => w.StartsWith(AssistantDefaults.DoneWhenLead, StringComparison.Ordinal) || w.StartsWith(AssistantDefaults.DefaultLead, StringComparison.Ordinal));
            }
            Assert.All(plan.Items.Where(i => i.StateWord == PlanStateWords.Waiting), i => Assert.StartsWith("Waiting for", i.StateNote ?? ""));
        }

        // Verifier probe (v-hands-off round 2): the queue offers tasks only, so a found assistant row reaches the assistant as the task
        // GiveRowToAssistantAsync makes from it (the same path Run the plan uses to queue the assistant's rows). That task's request must
        // still carry the row's Done when and Default, or the assistant claims it with nothing that says what to choose.
        [Fact]
        public async System.Threading.Tasks.Task A_row_given_to_the_assistant_keeps_its_done_when_and_default_in_the_request_brief()
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == "Wide").Copy("hoprobe");
            var plan = await f.Engine.GetModelPlanAsync();
            var rows = plan.Items.Where(i => i.Who == ModelPlanOwners.Assistant && !PlanTasks.IsTaskId(i.Id)
                && (i.ChangeSet?.Lines?.Length ?? 0) == 0 && PlanStateWords.IsOpen(i.StateWord)).ToArray();
            Assert.NotEmpty(rows);
            foreach (var r in rows) await f.Engine.SetPlanItemDoerAsync(r.Id, PlanDoers.Assistant, "agent");
            var list = await f.Engine.ListAssistantRequestsAsync();
            var all = list.Requests.Concat(list.Held ?? Array.Empty<Semanticus.Engine.Jev.AssistantRequest>()).ToArray();
            var given = all.Where(q => rows.Any(r => q.Need == r.Title)).ToArray();
            Assert.True(given.Length > 0, "no request offered for " + string.Join(", ", rows.Select(r => r.Id)) + "; offered: " + string.Join(" | ", all.Select(q => q.Need)));
            var request = given[0];
            Assert.Contains(AssistantDefaults.DoneWhenLead, request.Brief ?? "");
            Assert.Contains(AssistantDefaults.DefaultLead, request.Brief ?? "");
        }

        // UAT F17 round 2: Who does it > Me survived one engine only. A model with no folder keeps its plan in the engine's memory, so File > Open Folder
        // (a fresh engine, then one with a folder) brought the row back under the assistant. The person's choice is kept beside the model, in the
        // per-user home, keyed by the model, and moves into the folder's plan memory when the model gets one.
        private static async System.Threading.Tasks.Task<LocalEngine> OpenNorthstarAsync(string workspace, bool workspaceIsFolder = true)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro, workspace, workspaceIsFolder);
            await engine.CreateModelAsync("Northstar F17 " + nameof(OpenNorthstarAsync), 1604);
            await engine.CreateTableAsync("Sales", "human");
            await engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await engine.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            // The model is the published one, whichever engine opens it (the Service identity the UAT used).
            sessions.Current.LiveOrigin = new LiveOrigin("powerbi://api.powerbi.com/v1.0/myorg/F17Home", "NorthstarF17", null, "interactive");
            return engine;
        }

        [Fact]
        public async System.Threading.Tasks.Task Who_does_it_Me_survives_a_new_engine_and_File_Open_Folder()
        {
            var folder = Path.Combine(Path.GetTempPath(), "sem-f17-" + Guid.NewGuid().ToString("N"), "ws");
            Directory.CreateDirectory(folder);
            try
            {
                var first = await OpenNorthstarAsync(null);
                var row = (await first.GetModelPlanAsync()).Items
                    .First(i => i.Who == ModelPlanOwners.Assistant && !PlanTasks.IsTaskId(i.Id) && PlanStateWords.IsOpen(i.StateWord));
                var taken = await first.SetPlanItemDoerAsync(row.Id, PlanDoers.You, "human");
                Assert.Equal(PlanDoers.You, taken.Items.Single(i => i.Id == row.Id).Doer);

                // A new engine for the same model, still no folder (a window reload).
                var second = await OpenNorthstarAsync(null);
                Assert.Equal(PlanDoers.You, (await second.GetModelPlanAsync()).Items.Single(i => i.Id == row.Id).Doer);

                // The folder is opened: the engine now has somewhere to keep it, and the choice is still there, and stays after another reopen.
                var third = await OpenNorthstarAsync(folder);
                Assert.Equal(PlanDoers.You, (await third.GetModelPlanAsync()).Items.Single(i => i.Id == row.Id).Doer);
                var fourth = await OpenNorthstarAsync(folder);
                Assert.Equal(PlanDoers.You, (await fourth.GetModelPlanAsync()).Items.Single(i => i.Id == row.Id).Doer);
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(folder), true); } catch { /* scratch */ } }
        }

        // Verifier v2 (UAT F17 in a real window): with no folder open the extension still starts the engine with a workspace, the window's
        // start folder (extension.ts workspaceDir() falls back to process.cwd()), so the plan is never "nowhere". The choice must survive the
        // folder the person then opens.
        [Fact]
        public async System.Threading.Tasks.Task Who_does_it_Me_survives_File_Open_Folder_when_the_window_started_in_another_folder()
        {
            var root = Path.Combine(Path.GetTempPath(), "sem-f17v2-" + Guid.NewGuid().ToString("N"));
            var start = Path.Combine(root, "start"); var folder = Path.Combine(root, "ws");
            Directory.CreateDirectory(start); Directory.CreateDirectory(folder);
            try
            {
                // The extension tells the engine no folder is open (SEMANTICUS_NO_FOLDER), so the start folder is not where the plan is kept.
                var first = await OpenNorthstarAsync(start, workspaceIsFolder: false);
                var row = (await first.GetModelPlanAsync()).Items
                    .First(i => i.Who == ModelPlanOwners.Assistant && !PlanTasks.IsTaskId(i.Id) && PlanStateWords.IsOpen(i.StateWord));
                await first.SetPlanItemDoerAsync(row.Id, PlanDoers.You, "human");

                var opened = await OpenNorthstarAsync(folder);
                Assert.Equal(PlanDoers.You, (await opened.GetModelPlanAsync()).Items.Single(i => i.Id == row.Id).Doer);
            }
            finally { try { Directory.Delete(root, true); } catch { /* scratch */ } }
        }
    }
}
