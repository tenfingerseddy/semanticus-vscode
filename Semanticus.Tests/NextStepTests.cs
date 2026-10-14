// The Next line, engine half (1.5, option A; contract /tmp/semanticus-joint-review/m15/next-step/contract.md).
//
// The happy path on a real model first (open, change, save, check), then the five moments' words on Free and Pro, the source
// order (the assistant beats Jev beats the rules), Free asks nothing, a burst of commits asks Jev at most once, hide, stop and
// turn off, the preference file across engines, the broadcast once per change, and both doors giving the same JSON.
// No test reaches a real service: Pro runs on a scripted oracle that answers the way a recorded replay would.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    public sealed class NextStepTests
    {
        /// <summary>Answers next.step with a fixed kind and confidence, counts every next.step ask, and answers nothing else.</summary>
        private sealed class NextOracle : IDecisionOracle
        {
            public string Choice = "run-plan";
            public double P = 0.95;
            public int Asks;
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PointName != DecisionRegistry.NextStep)
                    return new RecordedDecisionOracle("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[]}", "jev-test-1").AskAsync(request, ct);
                Interlocked.Increment(ref Asks);
                var json = "{\"entries\":[{\"digest\":" + JsonSerializer.Serialize(request.Digest)
                    + ",\"point\":" + JsonSerializer.Serialize(request.PointName)
                    + ",\"usage\":{\"input_tokens\":200,\"output_tokens\":5},\"answers\":{\"next_kind\":{\"type\":\"choice\",\"choice\":"
                    + JsonSerializer.Serialize(Choice) + ",\"confidence\":" + P.ToString(CultureInfo.InvariantCulture) + "}}}]}";
                return new RecordedDecisionOracle(json, "jev-test-1").AskAsync(request, ct);
            }
        }

        private sealed class Rig : IDisposable
        {
            public DecisionFixture F;
            public NextOracle Jev;
            public LocalEngine Engine => F.Engine;
            public string Home => Path.Combine(F.Home, "user-home");
            public readonly ConcurrentQueue<NextStepView> Sent = new ConcurrentQueue<NextStepView>();
            public void Dispose() => F.Dispose();
        }

        private static async Task<Rig> BuildAsync(bool pro = false, string home = null)
        {
            var rig = new Rig { F = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free) };
            rig.Engine.NextStepHomeForTest = home ?? rig.Home;
            rig.Engine.NextStepSettle = TimeSpan.FromMilliseconds(40);
            rig.Jev = new NextOracle();
            rig.Engine.UseDecisionsForTest(new DecisionService(rig.Jev, new DecisionBudget()));
            rig.F.Sessions.Bus.StudioNextStep += rig.Sent.Enqueue;
            await rig.F.BuildModelAsync();
            if (pro) await rig.F.ConsentAsync();
            return rig;
        }

        private static NextStepInputs Moment(string key) => key switch
        {
            "open" => new NextStepInputs { ModelOpen = true, TasksReady = 14, TopTaskTitle = "Mark Date as the date table", TopTaskKind = "Structure" },
            "changed" => new NextStepInputs { ModelOpen = true, Unsaved = 6, TasksReady = 7, TasksDone = 2 },
            "plandone" => new NextStepInputs { ModelOpen = true, TasksDone = 12, PlanJustFinished = true, SavedSinceChange = true, ChecksCount = 12 },
            "saved" => new NextStepInputs { ModelOpen = true, TasksDone = 6, SavedSinceChange = true, ChecksCount = 12 },
            "repeat" => new NextStepInputs { ModelOpen = true, TasksDone = 12, RepeatTimes = 4, RepeatSteps = 3, RepeatNoun = "tables", PublishedProduction = true, ChecksRunSinceSave = true },
            _ => throw new ArgumentException(key),
        };

        // ---- the happy path on a real model --------------------------------------------------------------------------------

        [Fact]
        public async Task A_real_model_goes_open_then_save_then_run_checks_then_publish_to_test()
        {
            using var rig = await BuildAsync();

            // Open: the Plan has ready tasks, so the line hands the plan to the assistant.
            var open = await rig.Engine.GetNextStepAsync();
            Assert.True(open.On);
            Assert.Equal("run-plan", open.Step.Kind);
            Assert.Equal("Ask your assistant to run the plan", open.Step.Ask);
            Assert.EndsWith("ready. They run in Plan order.", open.Step.Why);
            Assert.Equal("From your Plan", open.Step.Source);
            Assert.Equal("rules", open.Step.ChosenBy);
            Assert.Null(open.Step.DoIt);
            Assert.Equal(2, open.Stage);
            Assert.Equal("Get it AI ready", open.StageName);
            Assert.StartsWith("ns-", open.Id);
            Assert.Equal(15, open.Id.Length);

            // A change: save comes first.
            await rig.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "All sales.", "agent");
            var changed = await rig.Engine.GetNextStepAsync();
            Assert.Equal("save", changed.Step.Kind);
            Assert.Equal("Save your changes", changed.Step.Ask);
            Assert.Equal("1 change is not saved yet.", changed.Step.Why);
            Assert.Equal("save", changed.Step.DoIt.Op);
            Assert.Equal("Saved.", changed.Step.DoIt.Done);

            // Save: with the plan still holding ready tasks, the plan comes back; with the plan's tasks set aside, the checks come.
            await rig.Engine.SaveAsync(null, null, false, "human");
            var saved = await rig.Engine.GetNextStepAsync();
            Assert.NotEqual("save", saved.Step.Kind);
            var plan = await rig.Engine.GetModelPlanAsync();
            foreach (var item in plan.Items.Where(i => i.Status == ModelPlanStatuses.Ready))
                await rig.Engine.SkipModelPlanItemAsync(item.Id, "Not today.", "human");
            var afterSkip = await rig.Engine.GetNextStepAsync();
            Assert.Equal("run-checks", afterSkip.Step.Kind);
            Assert.Equal("Run your checks", afterSkip.Step.Ask);
            Assert.Equal("Saved, checks not run", afterSkip.Step.Source);
            Assert.Equal("run_checks", afterSkip.Step.DoIt.Op);
            Assert.Equal(3, afterSkip.Stage);

            // Run the checks: the line moves on.
            var run = await rig.Engine.RunTestSuiteAsync(false, "human");
            var checkedView = await rig.Engine.GetNextStepAsync();
            if ((run.Health?.Failed ?? 0) == 0)
            {
                Assert.Equal("publish-test", checkedView.Step.Kind);
                Assert.Equal("Publish to test", checkedView.Step.Ask);
                Assert.Equal(5, checkedView.Stage);
            }
            else Assert.Null(checkedView.Step);
        }

        [Fact]
        public async Task The_same_change_on_three_measures_by_hand_suggests_a_workflow()
        {
            using var rig = await BuildAsync();
            await rig.Engine.CreateMeasureAsync("table:Sales", "Avg Sales", "AVERAGE ( Sales[Amount] )", "human");
            foreach (var m in new[] { "Total Sales", "Order Count", "Avg Sales" })
                await rig.Engine.SetDescriptionAsync("measure:Sales/" + m, "Described.", "human");
            await rig.Engine.SaveAsync(null, null, false, "human");
            var view = await rig.Engine.GetNextStepAsync();
            Assert.Equal("workflow", view.Step.Kind);
            Assert.Equal("Save these steps as a workflow", view.Step.Ask);
            Assert.Equal("You did the same 1 step on 3 measures.", view.Step.Why);
            Assert.Equal("draft_workflow", view.Step.DoIt.Op);
        }

        // ---- the five moments, word for word -------------------------------------------------------------------------------

        [Theory]
        [InlineData("open", "run-plan", null, "Ask your assistant to run the plan", "14 tasks are ready. They run in Plan order.", "14 tasks are ready. Jev put \"Mark Date as the date table\" first.", "From your Plan", null)]
        [InlineData("changed", "save", null, "Save your changes", "6 changes are not saved yet.", "6 changes are not saved yet. Save before the next task.", "Not saved yet", "Saved.")]
        [InlineData("plandone", "check-then-publish", "Your plan is done.", "Check the numbers, then publish to test", "12 checks test the measures the plan changed.", "12 checks test the measures the plan changed. Then test is the next stop.", "Plan done", "Checks running.")]
        [InlineData("saved", "run-checks", null, "Run your checks", "12 checks test the measures you changed. They have not run since your save.", "12 checks test the measures you changed. Run them before you publish.", "Saved, checks not run", "Checks running.")]
        [InlineData("repeat", "workflow", null, "Save these steps as a workflow", "You did the same 3 steps on 4 tables.", "You did the same 3 steps on 4 tables.", "Same steps 3 times", "Draft workflow made. Open Workflows to see it.")]
        public void Each_moment_has_the_mocks_words_on_free_and_pro(string moment, string kind, string lead, string ask, string free, string pro, string source, string done)
        {
            var i = Moment(moment);
            Assert.Equal(kind, NextStepRules.Eligible(i)[0]);
            var f = NextStepRules.Words(kind, i, pro: false);
            var p = NextStepRules.Words(kind, i, pro: true);
            Assert.Equal(lead, f.Lead);
            Assert.Equal(ask, f.Ask);
            Assert.Equal(free, f.Why);
            Assert.Equal(pro, p.Why);
            Assert.Equal(source, f.Source);
            Assert.Equal(done, f.DoIt?.Done);
            foreach (var s in new[] { f.Lead, f.Ask, f.Why, p.Why, f.Source, f.Prompt, f.DoIt?.Done })
                Assert.DoesNotContain("—", s ?? "");
        }

        [Fact]
        public void Every_kind_has_words_without_an_em_dash_and_the_stage_names_are_seven()
        {
            var i = new NextStepInputs { ModelOpen = true, Unsaved = 2, TasksReady = 3, RepeatTimes = 3, RepeatSteps = 2, ChecksCount = 4 };
            foreach (var k in DecisionRegistry.NextStepKinds)
                foreach (var pro in new[] { false, true })
                {
                    var w = NextStepRules.Words(k, i, pro);
                    Assert.False(string.IsNullOrWhiteSpace(w.Ask));
                    Assert.False(string.IsNullOrWhiteSpace(w.Why));
                    Assert.False(string.IsNullOrWhiteSpace(w.Prompt));
                    Assert.DoesNotContain("—", w.Ask + w.Why + w.Source + w.Prompt + w.Lead);
                    Assert.DoesNotContain("one click", (w.Ask + w.Why).ToLowerInvariant());
                }
            Assert.Equal(7, NextStepRules.StageNames.Length);
            Assert.Equal(2, NextStepRules.StageOf(Moment("open")));
            Assert.Equal(3, NextStepRules.StageOf(Moment("saved")));
            Assert.Equal(7, NextStepRules.StageOf(Moment("repeat")));
        }

        // ---- Pro: Jev chooses among the eligible kinds -------------------------------------------------------------------

        [Fact]
        public async Task Pro_jev_picks_another_eligible_kind_above_the_bar_and_the_rules_stand_below_it()
        {
            using var rig = await BuildAsync(pro: true);
            await rig.Engine.GetNextStepAsync();   // the Studio reads the line on mount
            await rig.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "All sales.", "agent");   // save and run-plan are both eligible
            rig.Jev.Choice = "run-plan"; rig.Jev.P = 0.95;
            await Task.Delay(100); await rig.Engine.NextStepIdleForTest();
            var view = await rig.Engine.GetNextStepAsync();
            Assert.True(view.Step.Kind == "run-plan", "jev: " + rig.Engine.NextStepJevNote + "; asks " + rig.Jev.Asks);
            Assert.Equal("jev", view.Step.ChosenBy);
            Assert.Equal("Jev chose this", view.Step.Source);
            Assert.Equal("Ask your assistant to run the plan", view.Step.Ask);   // the words are the table's, not Jev's
            Assert.True(rig.Jev.Asks >= 1);

            // Below the bar: a fresh state (another change) and an unsure answer leave the rules' first match.
            rig.Jev.P = 0.5;
            await rig.Engine.SetDescriptionAsync("measure:Sales/Order Count", "Orders.", "agent");
            await Task.Delay(100); await rig.Engine.NextStepIdleForTest();
            var unsure = await rig.Engine.GetNextStepAsync();
            Assert.Equal("save", unsure.Step.Kind);
            Assert.Equal("rules", unsure.Step.ChosenBy);
            Assert.Equal("Not saved yet", unsure.Step.Source);
        }

        [Fact]
        public async Task Pro_jev_naming_a_kind_the_rules_did_not_find_is_not_used()
        {
            using var rig = await BuildAsync(pro: true);
            await rig.Engine.GetNextStepAsync();
            await rig.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "All sales.", "agent");
            rig.Jev.Choice = "publish-production"; rig.Jev.P = 0.99;
            await Task.Delay(100); await rig.Engine.NextStepIdleForTest();
            var view = await rig.Engine.GetNextStepAsync();
            Assert.Equal("save", view.Step.Kind);
            Assert.Equal("rules", view.Step.ChosenBy);
        }

        [Fact]
        public async Task The_assistants_suggestion_beats_jev_and_a_newer_note_takes_it_away()
        {
            using var rig = await BuildAsync(pro: true);
            await rig.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "All sales.", "agent");
            await rig.Engine.SuggestNextAsync(new StudioSuggestRequest
            {
                Say = "Here is what I would do next.",
                Steps = new[] { new StudioStep { Label = "Fix the missing descriptions", Prompt = "Fix the missing descriptions." }, new StudioStep { Label = "Run the tests", Prompt = "Run the tests." } },
            });
            var view = await rig.Engine.GetNextStepAsync();
            Assert.Equal("assistant", view.Step.Kind);
            Assert.Equal("Fix the missing descriptions", view.Step.Ask);
            Assert.Equal("Fix the missing descriptions.", view.Step.Prompt);
            Assert.Equal("Here is what I would do next.", view.Step.Why);
            Assert.Equal("Your assistant suggested this", view.Step.Source);
            Assert.Equal("assistant", view.Step.ChosenBy);

            rig.Engine.StudioClock = () => DateTime.UtcNow.AddMinutes(1);
            await rig.Engine.ShowInStudioAsync(new StudioShowRequest { Place = "plan", Say = "Working on the plan." });
            var after = await rig.Engine.GetNextStepAsync();
            Assert.NotEqual("assistant", after.Step.Kind);
        }

        // ---- Free asks nothing; a burst asks once ------------------------------------------------------------------------

        [Fact]
        public async Task Free_never_asks_the_oracle()
        {
            using var rig = await BuildAsync(pro: false);
            await rig.Engine.GetNextStepAsync();
            await rig.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "All sales.", "agent");
            await Task.Delay(100); await rig.Engine.NextStepIdleForTest();
            var view = await rig.Engine.GetNextStepAsync();
            Assert.Equal("save", view.Step.Kind);
            Assert.Equal(0, rig.Jev.Asks);
            Assert.Equal(0, rig.Engine.Decisions.RequestsStarted);
        }

        [Fact]
        public async Task Fifty_commits_in_a_burst_ask_jev_at_most_once()
        {
            using var rig = await BuildAsync(pro: true);
            rig.Engine.NextStepSettle = TimeSpan.FromMilliseconds(300);
            await rig.Engine.GetNextStepAsync();
            for (var n = 0; n < 50; n++)
                await rig.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "Sales " + n, "agent");
            await Task.Delay(500); await rig.Engine.NextStepIdleForTest();
            Assert.True(rig.Jev.Asks <= 1, "asked " + rig.Jev.Asks + " times");
        }

        // ---- hide, stop, turn off ----------------------------------------------------------------------------------------

        [Fact]
        public async Task Hide_keeps_the_stage_and_unhide_brings_the_step_back()
        {
            using var rig = await BuildAsync();
            var view = await rig.Engine.GetNextStepAsync();
            var hidden = await rig.Engine.SetNextStepAsync("hide", view.Id);
            Assert.True(hidden.Hidden);
            Assert.Null(hidden.Step);
            Assert.Equal(view.Stage, hidden.Stage);
            Assert.Equal(view.StageName, hidden.StageName);
            Assert.Equal(view.Id, hidden.Id);

            // A new state is a new id, so it shows again by itself.
            await rig.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "All sales.", "agent");
            var next = await rig.Engine.GetNextStepAsync();
            Assert.False(next.Hidden);
            Assert.NotNull(next.Step);

            await rig.Engine.UndoAsync("human");
            Assert.True((await rig.Engine.GetNextStepAsync()).Hidden);
            var back = await rig.Engine.SetNextStepAsync("unhide", view.Id);
            Assert.False(back.Hidden);
            Assert.Equal(view.Step.Kind, back.Step.Kind);
            Assert.False(File.Exists(Path.Combine(rig.Home, LocalEngine.NextStepFileName)), "hiding is session memory only");
        }

        [Fact]
        public async Task Stop_a_kind_and_turn_off_are_kept_for_a_new_engine_on_the_same_home()
        {
            var home = Path.Combine(Path.GetTempPath(), "sem-next-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var rig = await BuildAsync(home: home))
                {
                    var view = await rig.Engine.GetNextStepAsync();
                    Assert.Equal("run-plan", view.Step.Kind);
                    var stopped = await rig.Engine.SetNextStepAsync("stopKind", "run-plan");
                    Assert.NotEqual("run-plan", stopped.Step?.Kind);
                    var off = await rig.Engine.SetNextStepAsync("turnOff", null);
                    Assert.False(off.On);
                    Assert.Null(off.Step);
                }
                var file = File.ReadAllText(Path.Combine(home, LocalEngine.NextStepFileName));
                using (var doc = JsonDocument.Parse(file))
                {
                    Assert.True(doc.RootElement.GetProperty("off").GetBoolean());
                    Assert.Equal("run-plan", doc.RootElement.GetProperty("stopped")[0].GetString());
                }
                using (var again = await BuildAsync(home: home))
                {
                    var off = await again.Engine.GetNextStepAsync();
                    Assert.False(off.On);
                    Assert.Null(off.Step);
                    var on = await again.Engine.SetNextStepAsync("turnOn", null);
                    Assert.True(on.On);
                    Assert.NotEqual("run-plan", on.Step?.Kind);
                    var resumed = await again.Engine.SetNextStepAsync("resumeKind", "run-plan");
                    Assert.Equal("run-plan", resumed.Step.Kind);
                }
            }
            finally { try { Directory.Delete(home, true); } catch { } }
        }

        [Fact]
        public async Task An_unknown_action_or_kind_is_refused_with_the_list()
        {
            using var rig = await BuildAsync();
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => rig.Engine.SetNextStepAsync("explode", null));
            Assert.Contains("turnOff", ex.Message);
            var ex2 = await Assert.ThrowsAsync<ArgumentException>(() => rig.Engine.SetNextStepAsync("stopKind", "dance"));
            Assert.Contains("run-checks", ex2.Message);
        }

        // ---- the broadcast and the two doors -----------------------------------------------------------------------------

        [Fact]
        public async Task The_broadcast_goes_once_per_change_and_never_twice_for_the_same_view()
        {
            using var rig = await BuildAsync();
            await rig.Engine.GetNextStepAsync();
            await Task.Delay(100); await rig.Engine.NextStepIdleForTest();
            while (rig.Sent.TryDequeue(out _)) { }

            await rig.Engine.GetNextStepAsync();
            await rig.Engine.GetNextStepAsync();
            Assert.Empty(rig.Sent);

            await rig.Engine.SetDescriptionAsync("measure:Sales/Total Sales", "All sales.", "agent");
            await Task.Delay(100); await rig.Engine.NextStepIdleForTest();
            await rig.Engine.GetNextStepAsync();
            var sent = rig.Sent.ToArray();
            Assert.Single(sent);
            Assert.Equal("save", sent[0].Step.Kind);
        }

        [Fact]
        public async Task Both_doors_give_the_same_json()
        {
            using var rig = await BuildAsync();
            var rpc = new EngineRpcTarget(rig.Engine);
            var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var viaMcp = JsonSerializer.Serialize(await McpTools.GetNextStep(rig.Engine), opts);
            var viaRpc = JsonSerializer.Serialize(await rpc.getNextStep(), opts);
            Assert.Equal(viaMcp, viaRpc);
            Assert.Contains("\"stageName\":\"Get it AI ready\"", viaMcp);

            var id = (await rig.Engine.GetNextStepAsync()).Id;
            var setMcp = JsonSerializer.Serialize(await McpTools.SetNextStep(rig.Engine, "hide", id), opts);
            var setRpc = JsonSerializer.Serialize(await rpc.setNextStep("hide", id), opts);
            Assert.Equal(setMcp, setRpc);
        }

        [Fact]
        public void The_point_is_registered_pro_only_live_with_its_fields_and_the_consent_version_is_six()
        {
            var p = DecisionRegistry.Require(DecisionRegistry.NextStep);
            Assert.True(p.ProOnly);
            Assert.Equal(0.85, p.ConfidenceBar);
            Assert.Equal("get_next_step", p.Caller);
            Assert.Equal(new[] { "journey_stage", "journey_counts", "journey_flags", "candidate_kinds", "plan_top_kind" }, p.SnapshotFields.ToArray());
            Assert.Equal(6, DecisionRegistry.ConsentSchemaVersion);
            Assert.True(LocalEngine.NextStepJevDecides);
        }
    }
}
