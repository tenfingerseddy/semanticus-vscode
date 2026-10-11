// The compose.draft chooser: Jev picks the steps from a person's words once its replay clears the bar (composer lane
// L5, contract section 8; Kane 25 Sep: "Jev really drafts. this was the plan all along"), through ONE chooser for both doors: compose_task (the Plan field, Ctrl J) asks it on
// the whole words, describe_workflow_steps (Workflows) once per step the words name.
//
// WHAT IS PROVED HERE.
//   Free asks nothing and counts nothing, and neither does Pro until the replay bar is met.
//   Every pick is from the options the engine listed; an id it did not offer is refused and the word match decides.
//   The steps Jev is sure of are kept, in the engine's order; a step it leans to arrives dropped; a pick it is not sure
//   of stays a question for the person.
//   With no sure step, the draft opens as the menu.
//   No string Jev supplies reaches the view: titles, questions and answers are the engine's own.
//   Kane's own words, typo and all, get their ops on Workflows, and who could do a step is said as a gap.
//
// THE ORACLE HERE IS SCRIPTED BUT HONEST. It answers the exact request the engine built, through the same recorded
// oracle and answer reader every decision test uses, so an answer outside the offered options is refused by the real
// reader, not by this file.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class JevDraftBuilderTests
    {
        /// <summary>Answers each request from a script: (the words asked about, the question name) to a noul probability
        /// or a choice. A question the script does not name is a sure no (noul) or a sure none (choice).</summary>
        private sealed class ScriptedOracle : IDecisionOracle
        {
            private readonly Func<string, string, (string Choice, double P)?> _script;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            public ScriptedOracle(Func<string, string, (string, double)?> script) { _script = script; }
            public bool Available => true;
            /// <summary>True while the service is away: every ask comes back not decided.</summary>
            public volatile bool Away;
            /// <summary>Every request of any point (the Plan's ranking asks too when a draft lands on the Plan).</summary>
            public IReadOnlyList<DecisionRequest> All { get { lock (_seen) return _seen.ToList(); } }
            /// <summary>The compose.draft requests that ask which steps the words name. The adding rule's last question (version 6,
            /// "step:role:wanted", one call per add it would tick) is a request of its own and is left out of this count.</summary>
            public IReadOnlyList<DecisionRequest> Seen => All.Where(r => r.PointName == DecisionRegistry.ComposeDraft
                && !r.Questions.Any(q => q.Name.EndsWith(":" + JevDraftBuilder.Wanted, StringComparison.Ordinal))).ToList();

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (_seen) _seen.Add(request);
                if (Away) return Task.FromResult(DecisionAnswers.NotDecided(request, "the advice service could not be reached"));
                if (request.PointName != DecisionRegistry.ComposeDraft)
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "this test answers compose.draft only"));
                var words = request.Snapshot.TryGetValue("draft_words", out var w) ? w as string : null;
                var sb = new StringBuilder("{\"entries\":[{\"digest\":").Append(JsonSerializer.Serialize(request.Digest))
                    .Append(",\"point\":").Append(JsonSerializer.Serialize(request.PointName))
                    .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":30},\"answers\":{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    var a = _script(words, q.Name);
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(':');
                    if (q.Kind == DecisionQuestionKind.Noul)
                    {
                        var p = a?.P ?? 0.02;
                        sb.Append("{\"type\":\"noul\",\"noul\":").Append(p.ToString(CultureInfo.InvariantCulture)).Append('}');
                    }
                    else
                    {
                        var (choice, confidence) = a ?? (q.Name.EndsWith(":wanted", StringComparison.Ordinal) ? ("yes", 0.95) : ("none", 0.95));   // join part 2: the last question fails closed, so a script answers it yes
                        // "first:Sales" picks the first option the engine offered whose id names Sales, so a test need not
                        // know how the engine spells a partition's ref.
                        if (choice.StartsWith("first:", StringComparison.Ordinal))
                            choice = q.Options.First(o => o.Id.Contains(choice.Substring(6), StringComparison.Ordinal)).Id;
                        sb.Append("{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                          .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append('}');
                    }
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct);
            }
        }

        private static (string, double)? Yes(double p) => ("yes", p);
        private static (string, double)? Pick(string id, double confidence) => (id, confidence);

        /// <summary>Pro, a saved model with Sales and Returns, permission given, the replay bar met for this engine, and
        /// Jev answering from the script.</summary>
        private static async Task<(DecisionFixture F, ScriptedOracle Jev)> ProAsync(string file, Func<string, string, (string, double)?> script, bool barMet = true)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync(file);
            await f.Engine.CreateTableAsync("Returns", "human");
            await f.Engine.CreateColumnAsync("table:Returns", "Qty", "Int64", null, "human");
            await f.SaveAsAsync(file);
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = barMet;
            var jev = new ScriptedOracle(script);
            f.Engine.UseDecisionsForTest(new DecisionService(jev, new DecisionBudget()));
            return (f, jev);
        }

        private static bool About(string words, string fragment) =>
            words != null && words.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;

        // ---- Free, and Pro before the bar ----------------------------------------------------------------------

        [Fact]
        public async Task Free_asks_nothing_and_counts_nothing()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            await f.BuildModelAsync("Free.bim");
            f.Engine.JevDraftsForTest = true;
            var jev = new ScriptedOracle((w, q) => Yes(0.99));
            var budget = new DecisionBudget();
            f.Engine.UseDecisionsForTest(new DecisionService(jev, budget));

            await Assert.ThrowsAnyAsync<Exception>(() => f.Engine.ComposeTaskAsync("add a measure", null, null, null, null, "human"));
            await Assert.ThrowsAnyAsync<Exception>(() => f.Engine.DescribeWorkflowStepsAsync("create a measure"));
            Assert.Empty(jev.All);
            Assert.Equal(0, budget.Requests);
        }

        /// <summary>H1 (completeness audit): Tell Jev more had no Free test. A person who drafted on Pro and then lost it
        /// is refused before the refined words reach Jev, and nothing is counted.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_refine_after_Pro_ends_asks_nothing_and_counts_nothing()
        {
            var tier = new SwitchableTier();
            using var f = new DecisionFixture(tier);
            await f.BuildModelAsync("Lapsed.bim");
            await f.SaveAsAsync("Lapsed.bim");
            await f.ConsentAsync();
            f.Engine.JevDraftsForTest = true;
            var jev = new ScriptedOracle((w, q) => Yes(0.99));
            var budget = new DecisionBudget();
            f.Engine.UseDecisionsForTest(new DecisionService(jev, budget));
            var v = await f.Engine.ComposeTaskAsync("add a measure", null, null, null, null, "human");
            var asked = jev.All.Count;
            var requests = budget.Requests;

            tier.Pro = false;
            await Assert.ThrowsAnyAsync<Exception>(() => f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "and describe it", "human"));
            Assert.Equal(asked, jev.All.Count);
            Assert.Equal(requests, budget.Requests);
        }

        private sealed class SwitchableTier : Semanticus.Engine.Entitlement.IEntitlement
        {
            public bool Pro = true;
            public bool IsPro => Pro;
            public Semanticus.Engine.Entitlement.EntitlementInfo Info => Pro ? TestEntitlements.Pro.Info : TestEntitlements.Free.Info;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Pro_before_the_replay_bar_asks_nothing_and_opens_the_menu()
        {
            var (f, jev) = await ProAsync("NoBar.bim", (w, q) => Yes(0.99), barMet: false);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("add a measure to Sales", null, null, null, null, "human");
                var d = await f.Engine.DescribeWorkflowStepsAsync("create a meaure and test its performance");
                Assert.Empty(jev.Seen);
                Assert.Empty(v.Steps);
                Assert.Equal(DraftCopy.PickTheSteps, v.MenuLine);
                // On Workflows a read after the add is a step of the file (the assistant's), so the add is kept (M11 sol1-plan, join-2 item C).
                Assert.All(d.Steps, s => Assert.Equal("words", s.ChosenBy));
                Assert.False(d.JevAsked);
            }
        }

        // ---- the committed decision, per door (Kane, 26 Sep: judge each door on its own bar) ------------------------

        /// <summary>No test seam: the committed switches decide. Workflows cleared its bar on the first fresh set (93.4%);
        /// the Plan cleared Kane's 26 Sep bar on the second (88.8% against 67.5%, McNemar p 0.0002, fewer dropped and
        /// wrong steps). So a Pro describe and a Pro Plan draft, with permission, both ask Jev.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_committed_bar_lets_Jev_draft_on_both_doors()
        {
            var (f, jev) = await ProAsync("PerDoor.bim", (w, q) => q == "step:add-measure:include" ? Yes(0.97) : null);
            using (f)
            {
                f.Engine.JevDraftsForTest = null;
                var d = await f.Engine.DescribeWorkflowStepsAsync("create a meaure");
                Assert.Equal("jev", d.Steps[0].ChosenBy);
                var asked = jev.Seen.Count;
                Assert.True(asked > 0);

                var v = await f.Engine.ComposeTaskAsync("add a measure to Sales", null, null, null, null, "human");
                Assert.True(jev.Seen.Count > asked);
                Assert.Equal(new[] { "add-measure" }, v.Steps.Select(s => s.Id));
                Assert.Equal(DraftCopy.JevDraftedMenuLine, v.MenuLine);
            }
        }

        // ---- the Plan's door: compose_task from words ----------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Sure_steps_are_kept_in_the_engines_order_a_leaning_one_arrives_dropped_and_an_unsure_pick_is_a_question()
        {
            // Each clause is answered about its own words: since one step is kept per clause even when two clauses ask
            // for the same kind (D7, 26 Sep), an oracle that says yes to a step whatever the words would draft it once
            // per clause. The leaning refresh comes with the hide clause, which Jev is sure of, so it arrives dropped;
            // a clause Jev is sure of nothing in takes its word match instead (the per-clause rule).
            var (f, jev) = await ProAsync("Compose.bim", (w, q) => q switch
            {
                "step:refresh:include" when About(w, "hide") => Yes(0.6),  // leans yes, below the bar
                "step:add-measure:include" when About(w, "measure") => Yes(0.97),
                "step:add-measure:tableRef" => Pick("table:Sales", 0.95),
                "step:hide:include" when About(w, "hide") => Yes(0.93),
                "step:hide:objRef" => Pick("table:Returns", 0.5),         // not sure which
                _ => null,
            });
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("hide Returns and add a measure to Sales", null, null, null, null, "human");
                // One ask per step the words name (the Plan asks the way Workflows does), each about its own clause.
                Assert.Equal(new[] { "add a measure to Sales", "hide Returns" },
                    jev.Seen.Select(r => (string)r.Snapshot["draft_words"]).OrderBy(x => x, StringComparer.Ordinal));
                Assert.All(jev.Seen, r => Assert.Equal(DecisionRegistry.ComposeDraft, r.PointName));

                // The engine's order: the measure before hiding, and both before the refresh after the change.
                Assert.Equal(new[] { "add-measure", "hide" }, v.Steps.Select(s => s.Id));
                var refresh = Assert.Single(v.After);
                Assert.Equal("refresh", refresh.Id);
                Assert.False(refresh.Kept);
                Assert.All(v.Steps, s => Assert.True(s.Kept));

                // The sure pick answers its question with the engine's own ref; the unsure one stays the person's.
                Assert.Equal("table:Sales", v.Questions.Single(q => q.Name == "add-measure-tableRef").Answer);
                Assert.Null(v.Questions.Single(q => q.Name == "hide-objRef").Answer);
                Assert.Equal(DraftCopy.JevDraftedMenuLine, v.MenuLine);
                Assert.Equal(DraftCopy.JevDraftedWithUnsure(1), v.Sentence);
            }
        }

        /// <summary>The Plan's per-clause rule (26 Sep, committed before the second fresh set): Jev's step where Jev is
        /// sure of a clause, else that clause's word match, and a clause the words name is never dropped. The sentence
        /// says which steps the words named, so Jev is not credited with them.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_clause_Jev_is_not_sure_of_takes_its_word_match_and_is_never_dropped()
        {
            var (f, jev) = await ProAsync("PerClause.bim", (w, q) =>
                About(w, "measure") && q == "step:add-measure:include" ? Yes(0.97)
                : About(w, "tests") && q == "step:run-tests:include" ? Yes(0.6)   // leans, not sure
                : null);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("add a measure to Sales, then run the tests", null, null, null, null, "human");
                Assert.Equal(2, jev.Seen.Count);
                Assert.Equal(new[] { "add-measure" }, v.Steps.Select(s => s.Id));
                var after = Assert.Single(v.After);
                Assert.Equal("run-tests", after.Id);
                Assert.True(after.Kept);
                Assert.Equal(DraftCopy.JevDraftedWithWords(1), v.Sentence);
                Assert.Equal(DraftCopy.JevAndWordsMenuLine, v.MenuLine);
            }
        }

        /// <summary>Jev answered every clause and was sure of none, but the words name steps: the words draft them, and
        /// nothing says Jev picked them.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task When_Jev_is_sure_of_nothing_the_words_draft_and_Jev_is_not_credited()
        {
            var (f, jev) = await ProAsync("WordsOnly.bim", (w, q) => null);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("refresh Sales, then run the tests", null, null, null, null, "human");
                Assert.Equal(2, jev.Seen.Count);
                Assert.Empty(v.Steps);
                Assert.Equal(new[] { "refresh", "run-tests" }, v.After.Select(s => s.Id));
                Assert.All(v.After, s => Assert.True(s.Kept));
                Assert.DoesNotContain("Jev picked", v.Sentence);
                Assert.Equal(DraftCopy.WordsDrafted, v.Sentence);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task With_no_sure_step_the_draft_opens_as_the_menu()
        {
            var (f, jev) = await ProAsync("Menu.bim", (w, q) => q == "step:add-measure:include" ? Yes(0.7) : null);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("do the thing with the numbers", null, null, null, null, "human");
                Assert.Single(jev.Seen);
                Assert.Empty(v.Steps);
                Assert.Empty(v.After);
                Assert.NotNull(v.Menu);
                Assert.Equal(DraftCopy.JevNotSure, v.MenuLine);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_pick_is_one_the_engine_listed_and_an_id_it_did_not_offer_is_refused()
        {
            var (f, jev) = await ProAsync("Refused.bim", (w, q) => q switch
            {
                "step:add-measure:include" => Yes(0.97),
                "step:add-measure:tableRef" => Pick("table:Invented", 0.99),
                _ => null,
            });
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("add a measure to Sales", null, null, null, null, "human");
                var asked = Assert.Single(jev.Seen);

                // What was offered: every template is a real op, every include a yes or no, every pick a model object.
                var tables = new[] { "table:Sales", "table:Returns" };
                foreach (var q in asked.Questions)
                {
                    Assert.StartsWith(JevDraftBuilder.Prefix, q.Name, StringComparison.Ordinal);
                    if (q.Kind == DecisionQuestionKind.Noul) continue;
                    Assert.All(q.Options, o => Assert.True(o.Id == JevDraftBuilder.None || o.Id.Contains(':'), $"{q.Name} offered {o.Id}"));
                }
                Assert.Equal(tables.OrderBy(x => x), asked.Question("step:add-measure:tableRef").Options.Select(o => o.Id).Where(id => id != "none").OrderBy(x => x));
                Assert.Equal(new[] { "draft_words", "table_names" }, asked.Snapshot.Keys.OrderBy(k => k, StringComparer.Ordinal));

                // The reader refused the invented table, so nothing was drafted from Jev and the menu opens.
                Assert.Empty(v.Steps);
                Assert.Equal(DraftCopy.PickTheSteps, v.MenuLine);
                Assert.DoesNotContain("Invented", JsonSerializer.Serialize(v));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task No_string_Jev_supplies_reaches_the_view()
        {
            var (f, _) = await ProAsync("Strings.bim", (w, q) => q switch
            {
                "step:add-measure:include" => Yes(0.97),
                "step:add-measure:tableRef" => Pick("table:Sales", 0.95),
                "step:run-tests:include" => Yes(0.95),
                _ => null,
            });
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("add a measure to Sales then run the checks", null, null, null, null, "human");
                // Every title is a template's own; every question is the engine's; every answer is a ref it listed.
                var titles = DraftMenu.All.Concat(DescribedSteps.AfterTheChange).Select(t => t.Title).ToHashSet();
                Assert.All(v.Steps.Concat(v.After), s => Assert.Contains(s.Title, titles));
                Assert.All(v.Questions, q => Assert.True(q.Answer == null || q.Answer == "table:Sales", q.Answer));
                Assert.Equal(DraftCopy.JevDrafted, v.Sentence);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Tell_Jev_more_adds_the_new_step_and_keeps_what_was_there()
        {
            var (f, jev) = await ProAsync("Refine.bim", (w, q) => q switch
            {
                "step:add-measure:include" when About(w, "measure") => Yes(0.97),
                "step:describe:include" when About(w, "describe") => Yes(0.95),
                _ => null,
            });
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("add a measure", null, null, null, null, "human");
                Assert.Equal(new[] { "add-measure" }, v.Steps.Select(s => s.Id));
                v = await f.Engine.EditDraftAsync(v.DraftId, "drop", "add-measure", null, null, null, "human");
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "and describe it", "human");
                // "add a measure", then only the new words, as a clause of their own (D6b, 26 Sep): the old words' steps
                // are already on the draft as the person left them.
                Assert.Equal(2, jev.Seen.Count);
                Assert.Equal(new[] { "add-measure", "describe" }, v.Steps.Select(s => s.Id));
                Assert.False(v.Steps[0].Kept);   // the person's drop carries over
                Assert.Equal(DraftCopy.JevRedrafted(1), v.Sentence);
            }
        }

        /// <summary>Add more words on a draft that opened as the menu with nothing on it (Jev could not be asked when it
        /// was made) drafts all its words, the old ones and the new ones each their own clauses, even with no "and"
        /// between them (D6b, 26 Sep).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Add_more_words_on_an_empty_menu_draft_drafts_the_old_words_and_the_new_as_clauses_of_their_own()
        {
            var (f, jev) = await ProAsync("RefineEmpty.bim", (w, q) => null, barMet: false);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("refresh Sales", null, null, null, null, "human");
                Assert.Empty(v.Steps.Concat(v.After));
                f.Engine.JevDraftsForTest = true;
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "save the model", "human");
                Assert.Equal(new[] { "refresh Sales", "save the model" }, jev.Seen.Select(r => (string)r.Snapshot["draft_words"]).OrderBy(x => x, StringComparer.Ordinal));
                Assert.Equal(new[] { "refresh", "save" }, v.After.Where(s => s.Kept).Select(s => s.Id));
                Assert.Equal("refresh Sales save the model", v.Words);
                // The new words named one step; the earlier words' step is said as theirs (R3-D6b-copy).
                Assert.Equal(DraftCopy.MoreWordsAdded(0, 1, 0) + " " + DraftCopy.EarlierWordsAdded(new[] { "refresh Sales" }, 1), v.Sentence);
            }
        }

        // ---- the Workflows door: describe_workflow_steps --------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Kanes_own_words_get_their_ops_typo_and_all()
        {
            var (f, jev) = await ProAsync("KaneTypo.bim", (w, q) =>
                About(w, "meaure") ? q switch
                {
                    "step:add-measure:include" => Yes(0.96),
                    "step:add-measure:tableRef" => Pick("table:Sales", 0.9),
                    _ => null,
                }
                : About(w, "performance") ? q switch
                {
                    "step:performance:include" => Yes(0.94),
                    "step:profile:include" => Yes(0.9),
                    _ => null,
                }
                : null);
            using (f)
            {
                var d = await f.Engine.DescribeWorkflowStepsAsync("create a meaure and test its performance");
                // One ask per step the words name, each about that step's own words.
                Assert.Equal(2, jev.Seen.Count);
                Assert.Contains(jev.Seen, r => (string)r.Snapshot["draft_words"] == "create a meaure");
                Assert.Contains(jev.Seen, r => (string)r.Snapshot["draft_words"] == "test its performance");
                Assert.All(jev.Seen, r => Assert.All(r.Questions.Where(q => q.Name.EndsWith(":include", StringComparison.Ordinal)),
                    q => Assert.True(LocalEngine.OpSurface.Methods.ContainsKey(JevDraftBuilder.Candidates(JevDraftBuilder.Scope.Describe)
                        .Single(t => JevDraftBuilder.Q(t.Id, "include") == q.Name).Op))));

                Assert.Equal(new[] { "Create a meaure", "Test its performance" }, d.Steps.Select(s => s.Title));
                // Kane's own UAT words keep their add on Workflows even though a read follows it: a benchmark is a step of the file (the
                // assistant's), so nothing after the add is unread (M11 sol1-plan, join-2 item C; adding rule v6 K4 on this door).
                Assert.Equal(new[] { "create_measure" }, d.Steps[0].Ops);
                Assert.Equal(new[] { "benchmark_dax", "profile_dax" }, d.Steps[1].Ops);
                Assert.All(d.Steps, s => Assert.Equal("jev", s.ChosenBy));
                Assert.Equal("Sales", d.Steps[0].Target);
                // A measure needs its formula written and a benchmark its query: both are the assistant's.
                Assert.All(d.Steps, s => Assert.Equal("assistant", s.Performer));
                Assert.True(d.JevAsked);
                Assert.EndsWith(DraftCopy.DescribeChosen(2, 2), d.Sentence);
            }
        }

        [Fact]
        public async Task A_step_Semanticus_can_do_is_Semanticus_in_the_file_and_an_unmatched_step_says_why()
        {
            var (f, _) = await ProAsync("Gap.bim", (w, q) =>
                About(w, "refresh") ? (q == "step:refresh:include" ? Yes(0.95) : q == "step:refresh:partitionRef" ? Pick("first:Sales", 0.9) : null)
                : About(w, "check") ? (q == "step:run-tests:include" ? Yes(0.95) : null)
                : null);
            using (f)
            {
                var d = await f.Engine.DescribeWorkflowStepsAsync("refresh the sales model, check its correct and email finance");
                Assert.Equal(new[] { "refresh_partition", "run_tests" }, d.Steps.Take(2).Select(s => s.Op));
                Assert.All(d.Steps.Take(2), s => Assert.Equal("semanticus", s.Performer));
                Assert.Equal("Sales", d.Steps[0].Target);   // a partition is named by its table
                // Since C7 the file hands these steps to Semanticus, so the sentence must not say a saved workflow cannot.
                Assert.DoesNotContain("cannot hand a step to Semanticus", d.Sentence);
                var def = WorkflowParser.Parse(d.Document.Text);
                Assert.All(def.Steps.Take(2), s => Assert.Equal(WorkflowPerformers.Semanticus, s.Performer));
                Assert.True(DescribedSteps.AsDraft(d).Steps.First(s => s.Title == d.Steps[0].Title).BySemanticus);
                Assert.Null(d.Steps[2].Op);
                Assert.Equal(DraftCopy.DescribeJevNone, d.Steps[2].Reason);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_refused_answer_leaves_the_word_match_and_says_why()
        {
            var (f, _) = await ProAsync("DescribeRefused.bim", (w, q) => q == "step:refresh:include" ? Yes(0.95)
                : q == "step:refresh:partitionRef" ? Pick("partition:Nowhere", 0.99) : null);
            using (f)
            {
                var d = await f.Engine.DescribeWorkflowStepsAsync("refresh Sales, then run the saved checks");
                Assert.Equal(new[] { "refresh_partition", "run_tests" }, d.Steps.Select(s => s.Op));
                Assert.Equal("words", d.Steps[0].ChosenBy);
                Assert.StartsWith("Jev's answer was not used", d.JevNote, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Both_doors_describe_the_same_steps()
        {
            var (f, _) = await ProAsync("Doors.bim", (w, q) => q == "step:add-measure:include" ? Yes(0.96) : null);
            using (f)
            {
                var ui = await f.Engine.DescribeWorkflowStepsAsync("create a meaure");
                var agent = await McpTools.DescribeWorkflowSteps(f.Engine, "create a meaure");
                Assert.Equal(ui.Steps.Select(s => string.Join(",", s.Ops)), agent.Steps.Select(s => string.Join(",", s.Ops)));
                Assert.Equal("jev", agent.Steps[0].ChosenBy);
            }
        }

        /// <summary>m11/completeness D5: a clause that asks to delete never drafts an add, even when Jev is sure of one. No
        /// step template deletes, so the step stays a placeholder that says so, on both doors.</summary>
        [Fact]
        public async Task A_delete_clause_drops_a_sure_add_from_Jev_and_says_why()
        {
            var (f, _) = await ProAsync("SureAdd.bim", (w, q) => q == "step:add-measure:include" ? Yes(0.97) : null);
            using (f)
            {
                var ui = await f.Engine.DescribeWorkflowStepsAsync("delete the replaced measures");
                var agent = await McpTools.DescribeWorkflowSteps(f.Engine, "delete the replaced measures");
                foreach (var d in new[] { ui, agent })
                {
                    Assert.True(d.JevAsked);
                    Assert.Null(d.Steps[0].Op);
                    Assert.Empty(d.Steps[0].Ops);
                    Assert.Equal(DraftCopy.DescribeTakeAway, d.Steps[0].Reason);
                }
            }
        }

        /// <summary>m11/completeness D5: only a no at the bar is a sure no. A leaning no (0.4, so 0.6 sure of no) leaves the
        /// word match standing; a sure no (0.02) makes the step a placeholder with Jev's reason, whatever the words matched.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_leaning_no_keeps_the_word_match_and_a_sure_no_is_a_placeholder()
        {
            var (leaning, _) = await ProAsync("LeaningNo.bim", (w, q) => q.EndsWith(":include", StringComparison.Ordinal) ? ("no", 0.4) : null);
            using (leaning)
            {
                var d = await leaning.Engine.DescribeWorkflowStepsAsync("refresh Sales");
                Assert.True(d.JevAsked);
                Assert.Equal("refresh_partition", d.Steps[0].Op);
                Assert.Equal("words", d.Steps[0].ChosenBy);
            }
            var (sure, _) = await ProAsync("SureNo.bim", (w, q) => null);   // every include answered 0.02
            using (sure)
            {
                var d = await sure.Engine.DescribeWorkflowStepsAsync("refresh Sales");
                Assert.True(d.JevAsked);
                Assert.Null(d.Steps[0].Op);
                Assert.Null(d.Steps[0].ChosenBy);
                Assert.Equal(DraftCopy.DescribeJevNone, d.Steps[0].Reason);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task One_request_stays_inside_the_relays_bounds()
        {
            var (f, jev) = await ProAsync("Bounds.bim", (w, q) => null);
            using (f)
            {
                for (var i = 0; i < 30; i++) await f.Engine.CreateTableAsync("Table" + i.ToString(CultureInfo.InvariantCulture), "human");
                await f.Engine.ComposeTaskAsync("refresh everything and add a measure", null, null, null, null, "human");
                Assert.Equal(2, jev.Seen.Count);
                foreach (var asked in jev.Seen)
                {
                    Assert.InRange(asked.Questions.Count, 1, JevDraftBuilder.MaxQuestions);
                    var bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new
                    {
                        snapshot = asked.Snapshot,
                        questions = asked.Questions.Select(q => new { q.Name, q.Instructions, options = q.Options.Select(o => new { o.Id, o.Criterion }) }),
                    }));
                    Assert.True(bytes < 131_072, $"{bytes} bytes");
                }
            }
        }

        // ---- Sol on m11/integration 568435d3, findings 3 and 4 --------------------------------------------------------

        /// <summary>Finding 3: Jev can be sure of several ops for one clause, and the step's Ops listed them all, but the
        /// workflow file was written from the first alone, so "refresh Sales with validation" saved a refresh and no tests.
        /// Every op Jev was sure of is its own step in the file, each with its own questions and checks, on both doors.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_op_Jev_is_sure_of_in_one_clause_reaches_the_workflow_on_both_doors()
        {
            var (f, _) = await ProAsync("SolF3.bim", (w, q) =>
                q == "step:refresh:include" ? Yes(0.95) : q == "step:refresh:partitionRef" ? Pick("first:Sales", 0.9)
                : q == "step:run-tests:include" ? Yes(0.95) : null);
            using (f)
            {
                const string words = "refresh Sales with validation";
                var rpc = new EngineRpcTarget(f.Engine);
                foreach (var d in new[] { await rpc.describeWorkflowSteps(words), await McpTools.DescribeWorkflowSteps(f.Engine, words) })
                {
                    var step = Assert.Single(d.Steps);
                    Assert.Equal(new[] { "refresh_partition", "run_tests" }, step.Ops);
                    Assert.NotNull(d.Document);
                    var def = WorkflowParser.Parse(d.Document.Text);
                    Assert.Equal(new[] { "refresh_partition", "run_tests" }, def.Steps.Select(s => s.Action?.Op));
                    Assert.All(def.Steps, s => Assert.Equal(WorkflowPerformers.Semanticus, s.Performer));
                    foreach (var s in def.Steps)
                    {
                        // Its own checks: the template's, never another op's.
                        var template = DescribedSteps.RunnableTemplate(s.Action.Op);
                        Assert.Equal(template.Checks.Select(c => c.Kind), s.Checks.Select(c => c.Kind));
                    }
                    // Its own questions: the refresh asks which partition, and that question is the refresh step's.
                    var refresh = def.Steps[0];
                    Assert.NotEmpty(refresh.Questions);
                    Assert.DoesNotContain(def.Steps[1].Questions, q => refresh.Questions.Any(r => r.Name == q.Name));
                    Assert.Equal("Refresh Sales with validation", refresh.Title);
                    // The step's checks list both ops' checks, not just the first's.
                    Assert.Equal(step.Ops.SelectMany(o => DescribedSteps.ChecksOf(o)).Distinct(), step.Checks);
                }
            }
        }

        /// <summary>Finding 3, the read beside a change: a clause Jev is sure asks for a measure and its benchmark keeps
        /// both, the change as Semanticus's step and the read as your assistant's.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_read_Jev_is_sure_of_beside_a_change_is_kept_as_its_own_step()
        {
            var (f, _) = await ProAsync("SolF3Read.bim", (w, q) =>
                q == "step:add-measure:include" ? Yes(0.95) : q == "step:performance:include" ? Yes(0.95) : null);
            using (f)
            {
                var d = await f.Engine.DescribeWorkflowStepsAsync("create a measure to test performance");
                var step = Assert.Single(d.Steps);
                Assert.Equal(new[] { "create_measure", "benchmark_dax" }, step.Ops);
                var draft = DescribedSteps.AsDraft(d);
                Assert.Equal(2, draft.Steps.Count);
                Assert.Equal("create_measure", draft.Steps[0].Actions.Single().Op);
                Assert.Equal(DraftPerformers.Assistant, draft.Steps[1].Performer);
                Assert.Contains("benchmark_dax", draft.Steps[1].Brief);
            }
        }

        /// <summary>Finding 4, Workflows: words that name more than twelve steps kept the first twelve and dropped the rest,
        /// with only a count. Each clause past the twelfth is now a step to fill in, marked as not read and never asked of
        /// Jev, and the sentence names it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_description_past_the_limit_keeps_every_clause_and_names_the_ones_not_read()
        {
            var (f, jev) = await ProAsync("SolF4.bim", (w, q) => q == "step:refresh:include" ? Yes(0.95) : null);
            using (f)
            {
                var words = string.Join(", ", Enumerable.Range(1, 14).Select(i => "refresh Table" + i.ToString(CultureInfo.InvariantCulture)));
                foreach (var d in new[] { await f.Engine.DescribeWorkflowStepsAsync(words), await McpTools.DescribeWorkflowSteps(f.Engine, words) })
                {
                    Assert.Equal(14, d.Steps.Length);
                    Assert.Equal(2, d.LeftOut);
                    Assert.Equal(new[] { "refresh Table13", "refresh Table14" }, d.NotRead);
                    Assert.All(d.Steps.Take(12), s => Assert.False(s.NotRead));
                    foreach (var s in d.Steps.Skip(12))
                    {
                        Assert.True(s.NotRead);
                        Assert.Null(s.Op);
                        Assert.Empty(s.Ops);
                        Assert.Equal(DraftCopy.DescribeNotRead(DescribedSteps.MaxSteps), s.Reason);
                    }
                    Assert.Contains("\"refresh Table13\"", d.Sentence);
                    Assert.Contains("\"refresh Table14\"", d.Sentence);
                    // Kept visible: the file Author opens has all fourteen.
                    var def = WorkflowParser.Parse(d.Document.Text);
                    Assert.Equal(14, def.Steps.Length);
                    Assert.Contains(def.Steps, s => s.Title == "Refresh Table14");
                }
                // Twelve asks per describe, never one for a clause past the limit.
                Assert.Equal(24, jev.Seen.Count);
                Assert.DoesNotContain(jev.Seen, r => (r.Snapshot["draft_words"] as string)?.Contains("Table13") == true);
            }
        }

        /// <summary>Finding 4, the Plan: the draft said only how many clauses were past the limit, in a sentence that is
        /// gone after the call. It now names them, and the draft keeps a note naming them until Add more words reads them.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_Plan_draft_past_the_limit_names_the_clauses_not_read_and_keeps_them_visible()
        {
            var (f, jev) = await ProAsync("SolF4Plan.bim", (w, q) => null);
            using (f)
            {
                var words = string.Join(", ", Enumerable.Range(1, 14).Select(i => "refresh Table" + i.ToString(CultureInfo.InvariantCulture)));
                var v = await f.Engine.ComposeTaskAsync(words, null, null, null, null, "human");
                Assert.Equal(12, jev.Seen.Count);
                var note = DraftCopy.PlanNotRead(new[] { "refresh Table13", "refresh Table14" }, new[] { "refresh Table13", "refresh Table14" });
                Assert.Contains(DraftCopy.PlanStepsLeftOut(2, DescribedSteps.MaxSteps), v.Sentence);
                Assert.Contains(note, v.Notes);
                // Read again later, the note is still there: it is the draft's, not the call's.
                var again = await f.Engine.GetDraftAsync(v.DraftId);
                Assert.Contains(note, again.Notes);
                var agent = await McpTools.GetDraft(f.Engine, v.DraftId);
                Assert.Contains(note, agent.Notes);

                // Add more words asks exactly the words not read yet (R3-D6b): both are read now, once each (the one the
                // new words say again is asked as theirs), both steps arrive, and the note goes.
                var before = jev.Seen.Count;
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "refresh Table13", "human");
                Assert.Equal(new[] { "refresh Table13", "refresh Table14" },
                    jev.Seen.Skip(before).Select(r => (string)r.Snapshot["draft_words"]).OrderBy(x => x, StringComparer.Ordinal));
                Assert.Equal(14, v.Steps.Concat(v.After).Count(s => s.Id.StartsWith("refresh", StringComparison.Ordinal)));
                Assert.DoesNotContain(v.Notes, n => n.StartsWith("Not read yet", StringComparison.Ordinal));
            }
        }

        // ---- the coordinator's rule for R3-D6b (27 Sep): typed words are kept until an answered ask has read them ------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Words_already_read_are_never_asked_again()
        {
            var (f, jev) = await ProAsync("RuleReadOnce.bim", (w, q) => About(w, "hide") && q == "step:hide:include" ? Yes(0.95) : null);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("hide the keys, then describe the Sales table", null, null, null, null, "human");
                var before = jev.Seen.Count;
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "save the model", "human");
                var asked = jev.Seen.Skip(before).Select(r => (string)r.Snapshot["draft_words"]).ToArray();
                Assert.Equal(new[] { "save the model" }, asked);
                Assert.DoesNotContain(v.Notes, n => n.StartsWith("Not read yet", StringComparison.Ordinal));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Words_typed_while_Jev_is_away_are_kept_and_the_draft_says_the_list_has_no_step_for_them()
        {
            var (f, jev) = await ProAsync("RuleAway.bim", (w, q) => null);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("describe the Sales table", null, null, null, null, "human");
                jev.Away = true;
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "save the model", "human");
                Assert.Contains(DraftCopy.WordsKeptUnread, v.Sentence);
                var note = DraftCopy.PlanNotRead(new[] { "save the model" }, new[] { "save the model" });
                Assert.Contains(note, v.Notes);
                Assert.Contains("no step for \"save the model\"", note);
                Assert.Contains(note, (await McpTools.GetDraft(f.Engine, v.DraftId)).Notes);
                // Once Jev is back, the next Add more words reads them, and the note goes.
                jev.Away = false;
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "hide the keys", "human");
                Assert.Contains(v.Steps.Concat(v.After), s => s.Id == "save" && s.Kept);
                Assert.DoesNotContain(v.Notes, n => n.StartsWith("Not read yet", StringComparison.Ordinal));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Words_the_person_resolved_by_picking_their_step_by_hand_count_as_read()
        {
            var (f, jev) = await ProAsync("RuleHandPick.bim", (w, q) => null);
            using (f)
            {
                jev.Away = true;
                var v = await f.Engine.ComposeTaskAsync("hide the keys", null, null, null, null, "human");
                Assert.Contains(DraftCopy.PlanNotRead(new[] { "hide the keys" }, new string[0]), v.Notes);
                v = await f.Engine.EditDraftAsync(v.DraftId, "add", null, null, null, "hide", "human");
                Assert.DoesNotContain(v.Notes, n => n.StartsWith("Not read yet", StringComparison.Ordinal));
                jev.Away = false;
                var before = jev.Seen.Count;
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "save the model", "human");
                Assert.Equal(new[] { "save the model" }, jev.Seen.Skip(before).Select(r => (string)r.Snapshot["draft_words"]).ToArray());
                Assert.Single(v.Steps.Concat(v.After), s => s.Id.StartsWith("hide", StringComparison.Ordinal));
            }
        }

        // ---- the coordinator's rulings on my also-found items (27 Sep) ------------------------------------------------

        /// <summary>On the Workflows file path a placeholder's reason reached only the seeded-steps fallback, so Author
        /// showed an empty What it does. The file now writes it as the step's body, as the fallback does.</summary>
        [Fact]
        public async Task A_placeholder_in_the_workflow_file_says_why_in_what_it_does()
        {
            var (f, _) = await ProAsync("ReasonInFile.bim", (w, q) => null, barMet: false);
            using (f)
            {
                var words = "Separate the blinking lights, " + string.Join(", ", Enumerable.Range(1, 12).Select(i => "refresh Table" + i.ToString(CultureInfo.InvariantCulture)));
                var d = await f.Engine.DescribeWorkflowStepsAsync(words);
                Assert.Equal(13, d.Steps.Length);
                var def = WorkflowParser.Parse(d.Document.Text);
                Assert.Null(def.Error);
                var noMatch = Assert.Single(def.Steps, s => s.Title == "Separate the blinking lights");
                Assert.Equal(DraftCopy.DescribeNoMatch, (noMatch.Instructions ?? "").Trim());
                var notRead = Assert.Single(def.Steps, s => s.Title == "Refresh Table12");
                Assert.Equal(DraftCopy.DescribeNotRead(DescribedSteps.MaxSteps), (notRead.Instructions ?? "").Trim());
                // A step with an action carries no such line.
                Assert.All(def.Steps.Where(s => s.Action != null), s => Assert.True(string.IsNullOrWhiteSpace(s.Instructions)));
            }
        }

        /// <summary>describe_workflow_steps kept no limit on its words, so with every clause now kept a huge description
        /// made a huge workflow. It caps them at compose_task's limit, refused the same plain way on both doors.</summary>
        [Fact]
        public async Task A_description_past_the_full_text_limit_is_refused_the_same_way_on_both_doors()
        {
            var (f, jev) = await ProAsync("DescribeCap.bim", (w, q) => null, barMet: false);
            using (f)
            {
                var tooLong = new string('x', PlanTasks.MaxFullTextChars + 1);
                var rpc = new EngineRpcTarget(f.Engine);
                var ui = await Assert.ThrowsAsync<ArgumentException>(() => rpc.describeWorkflowSteps(tooLong));
                var agent = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.DescribeWorkflowSteps(f.Engine, tooLong));
                Assert.Equal(ui.Message, agent.Message);
                var compose = Assert.Throws<ArgumentException>(() => PlanTasks.RequireWithinFullText(tooLong, "a description", "words"));
                Assert.Equal(compose.Message, ui.Message);
                Assert.Contains("100,000", ui.Message);
                Assert.Empty(jev.All);
                // Exactly at the limit is kept whole.
                var atLimit = await f.Engine.DescribeWorkflowStepsAsync(new string('x', PlanTasks.MaxFullTextChars));
                Assert.Single(atLimit.Steps);
            }
        }
    }
}
