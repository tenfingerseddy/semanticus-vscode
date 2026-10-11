// Round 1, plan lane, engine half (Kane's hands-on findings, 8 Oct 2026).
//
// What these tests hold, one line each:
//   a run says each step of each row on the bus (work, apply, check, done) and returns the History entry that takes the press back
//   one press runs every wave: a row that waited for a row in the press runs in the same press, behind it
//   a run never saves by itself and says whether the window can offer Save
//   a person's row never holds up an automated row (the assistant's too), and sorts after them on a tie
//   rows default to the assistant, not the person: collisions, names, formulas, checks, the calendar
//   the five DAX-rewrite findings join the formulas row (the assistant's), with no row of their own
//   the descriptions and the other words are ONE row: "Write 3 descriptions and 1 synonym"
//   every derived row says what it is for (goal) and which rules it carries (rules)
//   no row jumps to the Model interview

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanRunEngineR1Tests
    {
        private static ModelPlanItem Row(ModelPlanView plan, string id) =>
            plan.Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

        private static string Dump(ModelPlanView plan) => string.Join("\n", plan.Items.Select(i =>
            i.Id + " doer=" + i.Doer + " word=" + i.StateWord + " goal=" + i.Goal + " rules=[" + string.Join(",", i.Rules ?? Array.Empty<string>())
            + "] waits=[" + string.Join(",", (i.WaitsFor ?? Array.Empty<PlanWaitRef>()).Select(w => w.Id)) + "] title=" + i.Title));

        private static Task<DecisionFixture> CollidingAsync(string tag) =>
            CommutationFixtures.All.Single(x => x.Name == "Colliding").Copy(tag);

        // ---- run progress: the order of the steps, and the History entry -----------------------------------------------

        [Fact]
        public async Task A_run_says_work_apply_check_done_for_each_row_and_returns_the_entry_that_takes_it_back()
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == "Wide").Copy("R1Progress");
            var seen = new List<PlanRunProgress>();
            f.Sessions.Bus.PlanRunProgressed += seen.Add;
            var plan = await f.Engine.GetModelPlanAsync();

            var result = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);

            Assert.True(result.Performed, result.Sentence);
            // The press finished everything Semanticus could do: no Semanticus row is left Ready (Kane, 8 Oct: it must work in one press).
            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items, i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready);
            var dump = Environment.GetEnvironmentVariable("R1_DUMP");
            if (!string.IsNullOrEmpty(dump))
                System.IO.File.WriteAllLines(dump, seen.Select(e => e.Ms + "ms w" + e.Wave + " " + e.Phase + " " + string.Join("+", e.ItemIds) + " c=" + e.Changes + " " + e.Reason)
                    .Concat(new[] { "waves=" + result.Waves + " applied=" + result.Applied + " ids=" + string.Join(",", result.ItemIds) + " :: " + result.Sentence }));
            Assert.NotEmpty(result.ItemIds);
            Assert.False(string.IsNullOrWhiteSpace(result.EntryId));
            Assert.False(string.IsNullOrWhiteSpace(result.RunId));
            Assert.All(seen, e => Assert.Equal(result.RunId, e.RunId));
            // A row that took two batches (new work arrived under its id) is checked batch by batch.
            foreach (var id in result.ItemIds)
                foreach (var wave in seen.Where(e => e.ItemIds.Contains(id)).GroupBy(e => e.Wave))
                {
                    var mine = wave.ToList();
                    int At(string phase) => mine.FindIndex(e => e.Phase == phase);
                    Assert.True(At(PlanRunPhases.Apply) >= 0 && At(PlanRunPhases.Check) > At(PlanRunPhases.Apply) && At(PlanRunPhases.Done) > At(PlanRunPhases.Check),
                        id + " wave " + wave.Key + ": " + string.Join(",", mine.Select(e => e.Phase)));
                    if (At(PlanRunPhases.Work) >= 0) Assert.True(At(PlanRunPhases.Work) < At(PlanRunPhases.Apply), id);
                    if (wave.Key == 1) Assert.True(At(PlanRunPhases.Work) >= 0, id + " has no worked-out changes before the run, so it says work first");
                    Assert.True(mine.Single(e => e.Phase == PlanRunPhases.Done).Changes > 0, id);
                }
            Assert.True(seen.Zip(seen.Skip(1), (a, b) => b.Ms >= a.Ms).All(x => x));   // one clock for the press

            // The entry is a real History entry of this press, and undoing to it takes the whole press back.
            var entry = f.Sessions.Ledger.Find(result.EntryId);
            Assert.NotNull(entry);
            Assert.Equal(ChangeCauseKinds.PlanItem, entry.Cause?.Kind);
            Assert.Equal("Time", await f.Sessions.Current.ReadAsync(m => m.Tables["Date"].DataCategory));
            await f.Engine.UndoToEntryAsync(result.EntryId, "human");
            Assert.NotEqual("Time", await f.Sessions.Current.ReadAsync(m => m.Tables["Date"].DataCategory));
        }

        // ---- one press, every wave -------------------------------------------------------------------------------------

        [Fact]
        public async Task One_press_runs_a_row_that_waited_for_a_row_in_the_press_in_a_second_wave_of_the_same_press()
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == "Wide").Copy("R1Waves");
            var plan = await ClearTheWayAsync(f);
            var blocked = Row(plan, "safefix/Formatting");
            Assert.NotNull(blocked);
            Assert.Equal(PlanStateWords.Waiting, blocked.StateWord);
            var seen = new List<PlanRunProgress>();
            f.Sessions.Bus.PlanRunProgressed += seen.Add;

            var result = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);

            Assert.True(result.Performed, result.Sentence);
            Assert.Contains("safefix/Formatting", result.ItemIds);
            Assert.True(result.Waves >= 2, "waves: " + result.Waves);
            Assert.Equal(result.Waves, result.EntryIds.Length);
            Assert.Contains(seen, e => e.Wave == 2 && e.Phase == PlanRunPhases.Done && e.ItemIds.Contains("safefix/Formatting"));
            Assert.StartsWith("Applied ", result.Sentence, StringComparison.Ordinal);
            Assert.Contains(result.Waves + " steps", result.Sentence, StringComparison.Ordinal);
            Assert.Equal(result.ItemIds.Length, result.ItemIds.Distinct().Count());   // a row is named once, however many batches it took
            // Nothing is left for Semanticus to run.
            var after = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(after.Items, i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready);

            // One undo to the first entry takes the whole press back.
            await f.Engine.UndoToEntryAsync(result.EntryId, "human");
            var back = await f.Engine.GetModelPlanAsync();
            Assert.NotEqual(PlanStateWords.Done, Row(back, "safefix/Formatting").StateWord);
            Assert.NotEqual("Time", await f.Sessions.Current.ReadAsync(m => m.Tables["Date"].DataCategory));
        }

        private static async Task<ModelPlanView> ClearTheWayAsync(DecisionFixture f)
        {
            var plan = await f.Engine.GetModelPlanAsync();
            foreach (var i in plan.Items.Where(i => i.Doer != PlanDoers.Semanticus
                                                   && (i.Status == ModelPlanStatuses.Ready || i.Status == ModelPlanStatuses.Proposed)))
                await f.Engine.SkipModelPlanItemAsync(i.Id, "out of the way for this test", "human");
            return await f.Engine.GetModelPlanAsync();
        }

        // ---- the run never saves; it says whether Save can be offered ---------------------------------------------------

        [Fact]
        public async Task A_run_does_not_save_and_says_the_window_can_offer_Save()
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == "Wide").Copy("R1Save");
            Assert.False(f.Sessions.Current.HasUnsavedEdits);
            var plan = await f.Engine.GetModelPlanAsync();

            var result = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);

            Assert.True(result.Performed, result.Sentence);
            Assert.True(f.Sessions.Current.HasUnsavedEdits);     // Run did not save
            Assert.True(result.Unsaved);
            Assert.True(result.CanSave);
            // A row that is done keeps what it was for: the starter measures answer an AI readiness rule, so the AI ready filter keeps them.
            var done = (await f.Engine.GetModelPlanAsync()).Items.Where(i => i.StateWord == PlanStateWords.Done).ToArray();
            Assert.NotEmpty(done);
            Assert.All(done, i => Assert.Contains(i.Goal, PlanGoals.All));
            Assert.All(done.Where(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal) || i.Id.StartsWith("starter/", StringComparison.Ordinal)),
                i => Assert.Equal(PlanGoals.Ai, i.Goal));
            // The agent door sees the same answer.
            var agent = PlanResultShape.Of(result);
            Assert.True(agent.CanSave);
            Assert.Equal(result.EntryId, agent.EntryId);
        }

        // ---- a person's row never holds up an automated row -------------------------------------------------------------

        private static Footprint Fp(string[] reads, string[] writes, bool candidate) =>
            new Footprint { Candidate = candidate }.ReadAll(reads).WriteAll(writes);

        [Fact]
        public void A_You_row_with_nothing_chosen_waits_for_the_assistants_row_that_shares_the_objects_facet_with_it()
        {
            // The Contoso shape: the collisions row (yours) and the words row (your assistant's) both read and write the objects
            // they name, and the collisions row also writes the objects facet. Before, the assistant's row waited for yours.
            var shared = new[] { "column:Sales/Amount", "measure:Sales/Total" };
            var collisions = Fp(shared.Concat(new[] { PlanFootprint.Objects }).ToArray(), shared.Concat(new[] { PlanFootprint.Objects, PlanFootprint.Revision }).ToArray(), candidate: true);
            var words = Fp(shared.Concat(new[] { PlanFootprint.Objects }).ToArray(), shared.Concat(new[] { PlanFootprint.Revision }).ToArray(), candidate: true);
            var rows = new[] { ("proposal/collisions", collisions), ("words/model", words) };
            var doers = new Dictionary<string, string> { ["proposal/collisions"] = PlanDoers.You, ["words/model"] = PlanDoers.Assistant };

            var (waits, _) = PlanFootprint.Waits(rows, doers);

            Assert.False(waits.ContainsKey("words/model"));
            Assert.Equal(new[] { "words/model" }, waits["proposal/collisions"]);
        }

        // ---- R1 fix-plan (Kane, 8 Oct): no Semanticus row waits for words it does not need ----------------------------------

        [Fact]
        public void A_Semanticus_row_never_waits_for_an_assistants_task_or_an_unchosen_assistant_row_that_shares_a_measure_with_it()
        {
            // The Northstar shape after Jev ranks: Jev's measures rows read every measure, and the assistant's task ("Describe Legacy
            // Discount %") writes one. The task feeds the row by that one read, so the row waited for the task and nothing was Ready.
            var measures = new[] { "measure:Sales/Total", "measure:Sales/Legacy Discount %" };
            var jev = Fp(measures.Concat(new[] { PlanFootprint.Objects }).ToArray(), new[] { "measure:Sales/Total", PlanFootprint.Objects, PlanFootprint.Revision }, candidate: false);
            var task = Fp(new[] { "measure:Sales/Legacy Discount %" }, new[] { "measure:Sales/Legacy Discount %", PlanFootprint.Revision }, candidate: false);
            var words = Fp(measures.Concat(new[] { PlanFootprint.Objects }).ToArray(), new[] { "measure:Sales/Legacy Discount %", PlanFootprint.Revision }, candidate: true);
            var taskId = PlanTasks.IdPrefix + "0a1b2c3d";
            var rows = new[] { (taskId, task), ("words/model", words), ("jev.measures.table:Sales", jev) };   // the assistant's rows come first in plan order
            var doers = new Dictionary<string, string>
            {
                [taskId] = PlanDoers.Assistant, ["words/model"] = PlanDoers.Assistant, ["jev.measures.table:Sales"] = PlanDoers.Semanticus,
            };

            var (waits, _) = PlanFootprint.Waits(rows, doers);

            Assert.False(waits.ContainsKey("jev.measures.table:Sales"), "waits for " + string.Join(",", waits.TryGetValue("jev.measures.table:Sales", out var w) ? w : new List<string>()));
        }

        [Fact]
        public void A_Semanticus_row_still_waits_for_an_assistant_row_that_has_chosen_changes_it_reads()
        {
            var created = new Footprint { Candidate = false }.ReadAll(new[] { PlanFootprint.Objects }).WriteAll(new[] { "measure:Sales/New", PlanFootprint.Objects, PlanFootprint.Revision });
            created.Chosen.Add("measure:Sales/New");
            var jev = Fp(new[] { "measure:Sales/New", PlanFootprint.Objects }, new[] { "measure:Sales/Total", PlanFootprint.Revision }, candidate: false);
            var rows = new[] { ("asst/create_measure", created), ("jev.measures.table:Sales", jev) };
            var doers = new Dictionary<string, string> { ["asst/create_measure"] = PlanDoers.Assistant, ["jev.measures.table:Sales"] = PlanDoers.Semanticus };

            var (waits, _) = PlanFootprint.Waits(rows, doers);

            Assert.Equal(new[] { "asst/create_measure" }, waits["jev.measures.table:Sales"]);
        }

        [Fact]
        public void Jev_rows_the_last_batch_made_stale_are_found_so_the_press_can_wait_for_Jev_instead_of_leaving_them_out()
        {
            ModelPlanItem Row(string id, bool stale) => new ModelPlanItem
            {
                Id = id, Doer = PlanDoers.Semanticus, StateWord = PlanStateWords.Ready,
                ChangeSet = new ModelPlanChangeSet { Stale = stale, State = ModelPlanChangeStates.Proposed },
            };
            var items = new[] { Row("jev.measures.table:Product", true), Row("jev.measures.table:Sales", false), Row("safefix/Formatting", true) };

            var looking = PlanReadySet.JevRowsLookingAgain(items, new HashSet<string>());

            Assert.Equal(new[] { "jev.measures.table:Product" }, looking.Select(r => r.Id).ToArray());
            Assert.Empty(PlanReadySet.JevRowsLookingAgain(items, new HashSet<string> { "jev.measures.table:Product" }));
        }

        // ---- K2: giving Jev permission is not the person's unsaved edit --------------------------------------------------------

        [Fact]
        public async Task Giving_Jev_permission_does_not_count_as_the_persons_unsaved_edit()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Consent", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.SaveAsAsync("R1Consent.bim");
            Assert.False((await e.SessionInfoAsync()).HasUnsavedChanges);

            await f.ConsentAsync();

            var info = await e.SessionInfoAsync();
            Assert.False(info.HasUnsavedChanges, "K19: permission is kept on this machine and is not a model edit, so nothing is unsaved");
            Assert.False(info.HasUnsavedEdits, "and nothing of the person's is unsaved");

            await e.SetDescriptionAsync("table:Sales", "Sales lines.", "human");
            Assert.True((await e.SessionInfoAsync()).HasUnsavedEdits);
        }

        [Fact]
        public async Task On_a_composed_plan_no_automated_row_waits_for_a_person_row_and_person_rows_sort_after_on_a_tie()
        {
            using var f = await CollidingAsync("R1Order");
            // The person takes the collisions row; before this change the assistant's words row then waited for it and sorted behind it.
            await f.Engine.SetPlanItemDoerAsync("proposal/collisions", PlanDoers.You);
            var plan = await f.Engine.GetModelPlanAsync();

            var person = plan.Items.Where(i => i.Doer == PlanDoers.You).Select(i => i.Id).ToHashSet();
            Assert.Contains("proposal/collisions", person);
            foreach (var row in plan.Items.Where(i => i.Doer != PlanDoers.You && PlanStateWords.IsOpen(i.StateWord)))
                Assert.DoesNotContain(row.WaitsFor, w => person.Contains(w.Id));
            var words = Row(plan, "words/model");
            Assert.NotNull(words);
            Assert.Equal(PlanDoers.Assistant, words.Doer);
            Assert.True(plan.Items.ToList().IndexOf(words) < plan.Items.ToList().IndexOf(Row(plan, "proposal/collisions")), Dump(plan));
        }

        // ---- the default doers ----------------------------------------------------------------------------------------

        [Fact]
        public async Task Collisions_names_and_synonyms_are_the_assistants_not_the_persons()
        {
            using var f = await CollidingAsync("R1Doers");
            var plan = await f.Engine.GetModelPlanAsync();

            var collisions = Row(plan, "proposal/collisions");
            Assert.NotNull(collisions);
            Assert.Equal(PlanDoers.Assistant, collisions.Doer);
            foreach (var family in new[] { "names", "synonyms", "formulas", "duplicate-measures", "implicit-measures", "ai-settings" })
            {
                var row = Row(plan, "proposal/" + family);
                if (row != null) Assert.Equal(PlanDoers.Assistant, row.Doer);
            }
            // The assistant's row waits for nothing a person holds.
            Assert.DoesNotContain(collisions.WaitsFor, w => Row(plan, w.Id)?.Doer == PlanDoers.You);
        }

        [Fact]
        public async Task A_check_for_a_measure_and_a_calendar_are_the_assistants()
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == "GappyReports").Copy("R1Checks");
            var plan = await f.Engine.GetModelPlanAsync();
            var checks = plan.Items.Where(i => i.Id.StartsWith("check/", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(checks);
            Assert.All(checks, c => Assert.Equal(PlanDoers.Assistant, c.Doer));
        }

        // ---- the DAX-rewrite findings and the one words row ------------------------------------------------------------

        private static async Task<DecisionFixture> DaxAsync(string file)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.Engine.CreateModelAsync("Contoso", 1604);
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Cost", "Double", null, "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Margin Pct", "IFERROR ( SUM ( Sales[Amount] ) / SUM ( Sales[Cost] ), 0 )", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Safe Total", "IF ( SUM ( Sales[Cost] ) = 0, BLANK (), SUM ( Sales[Amount] ) / SUM ( Sales[Cost] ) )", "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            return f;
        }

        [Fact]
        public async Task The_DAX_rewrite_findings_join_the_formulas_row_and_make_no_row_of_their_own()
        {
            using var f = await DaxAsync("R1Dax.bim");
            var plan = await f.Engine.GetModelPlanAsync();

            var formulas = Row(plan, "proposal/formulas");
            Assert.NotNull(formulas);
            Assert.Equal(PlanDoers.Assistant, formulas.Doer);
            Assert.Contains("BP-DAX-IFERROR", formulas.Rules);
            Assert.Equal(PlanGoals.Ai, formulas.Goal);
            // One row for them, and not also inside the words row.
            var words = Row(plan, "words/model");
            if (words != null) Assert.DoesNotContain("BP-DAX-IFERROR", words.Rules);
        }

        [Fact]
        public async Task The_descriptions_and_the_other_words_are_one_row()
        {
            using var f = await CollidingAsync("R1Words");
            var plan = await f.Engine.GetModelPlanAsync();

            Assert.Null(Row(plan, "describe/model"));
            var words = Row(plan, "words/model");
            Assert.NotNull(words);
            Assert.Equal(PlanDoers.Assistant, words.Doer);
            Assert.StartsWith("Write ", words.Title, StringComparison.Ordinal);
            Assert.Contains("description", words.Title, StringComparison.Ordinal);
            Assert.Contains(words.Rules, r => r.StartsWith("DESC-", StringComparison.Ordinal));
            Assert.Equal(PlanGoals.Ai, words.Goal);
            Assert.DoesNotContain("—", words.Title);
        }

        // ---- goal and rules on every row; no row at the interview --------------------------------------------------------

        [Fact]
        public async Task Every_row_says_what_it_is_for_and_a_readiness_row_is_ai_and_a_best_practice_row_is_quality()
        {
            using var f = await CollidingAsync("R1Goal");
            var plan = await f.Engine.GetModelPlanAsync();

            Assert.All(plan.Items, i =>
            {
                Assert.Contains(i.Goal, PlanGoals.All);
                Assert.NotNull(i.Rules);
            });
            Assert.Equal(PlanGoals.Ai, Row(plan, "proposal/collisions").Goal);
            Assert.NotEmpty(Row(plan, "proposal/collisions").Rules);
            var cleanup = Row(plan, "cleanup/model");
            if (cleanup != null) { Assert.Equal(PlanGoals.Quality, cleanup.Goal); Assert.Empty(cleanup.Rules); }

            // A row the person asked for is "aims", and a task sent from a finding names its rule.
            var added = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest
            {
                Words = "Fix a name", Doer = PlanDoers.Assistant,
                OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Finding, Ref = "NAME-DUP|measure:Sales/Group1 Key" },
                DoneWhen = PlanDoneWhen.ModelCheck,
            });
            var after = await f.Engine.GetModelPlanAsync();
            var asked = after.Items.Single(i => i.Id == added.ItemId);
            Assert.Equal(PlanGoals.Aims, asked.Goal);
            Assert.Equal(new[] { "NAME-DUP" }, asked.Rules);
        }

        [Fact]
        public async Task A_row_that_fixes_a_rule_both_pages_raise_shows_under_both_the_AI_ready_and_the_Quality_filter()
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == "Wide").Copy("R1Both");
            var plan = await f.Engine.GetModelPlanAsync();
            var dump = Environment.GetEnvironmentVariable("R1_DUMP");
            if (!string.IsNullOrEmpty(dump))
                System.IO.File.WriteAllLines(dump, plan.Items.Select(i => i.Id + " goal=" + i.Goal + " goals=[" + string.Join(",", i.Goals ?? Array.Empty<string>()) + "] rules=[" + string.Join(",", i.Rules ?? Array.Empty<string>()) + "]"));

            // Every row lists the filters it shows under, its own goal first.
            Assert.All(plan.Items, i => { Assert.NotEmpty(i.Goals); Assert.Equal(i.Goal, i.Goals[0]); });
            // A row of readiness fixes that Model quality raises as well counts under both.
            var shared = plan.Items.Where(i => i.Goals.Length == 2).ToArray();
            Assert.NotEmpty(shared);
            Assert.All(shared, i => Assert.Equal(new[] { PlanGoals.Ai, PlanGoals.Quality }, i.Goals));
            // A row only readiness raises stays under AI ready alone.
            Assert.Contains(plan.Items, i => i.Goal == PlanGoals.Ai && i.Goals.Length == 1);
        }

        [Theory]
        [InlineData("Colliding")]
        [InlineData("Wide")]
        [InlineData("GappyReports")]
        [InlineData("Star")]
        public async Task No_row_jumps_to_the_model_interview(string fixture)
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == fixture).Copy("R1Int" + fixture);
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.NotEmpty(plan.Items);
            Assert.DoesNotContain(plan.Items, i => string.Equals(i.Jump?.Tool, "interview", StringComparison.OrdinalIgnoreCase));
        }
    }
}
