// verify: the verifier's failing tests for lane m11/describe-jev 1ddd7b69..84f38cdd (the Plan door's per-clause rule,
// the second fresh set, and Jev drafting switched on for the Plan door on Pro). Each test names ONE defect and is LEFT
// FAILING on purpose: it passes once the defect is fixed. Found by attack tests on 84f38cdd merged with m11/integration
// 568435d3, and in UAT lane g with Jev on (Pro, Northstar), 26 Sep 2026.
//
// The oracle here is the same scripted, honest oracle JevDraftBuilderTests uses: it answers the exact request the engine
// built, through the real recorded oracle and answer reader. No test here sets JevDraftsForTest: the committed per-door
// switches decide, as they do in the product.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
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
    public sealed class VerifyDescribeJevDefectTests
    {
        private sealed class ScriptedOracle : IDecisionOracle
        {
            private readonly Func<string, string, (string Choice, double P)?> _script;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            public ScriptedOracle(Func<string, string, (string, double)?> script) { _script = script; }
            public bool Available => true;
            public IReadOnlyList<DecisionRequest> All { get { lock (_seen) return _seen.ToList(); } }
            public IReadOnlyList<DecisionRequest> Seen => All.Where(r => r.PointName == DecisionRegistry.ComposeDraft).ToList();

            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (_seen) _seen.Add(request);
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
                        // JOIN, part 2: the last question (":wanted") now fails closed on Pro, so this scripted Jev answers it yes: the test is about the steps, not about that question.
                        var (choice, confidence) = a ?? (q.Name.EndsWith(":wanted", StringComparison.Ordinal) ? ("yes", 0.95) : ("none", 0.95));
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
        private static bool About(string words, string fragment) =>
            words != null && words.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Pro, a saved model with Sales and Returns, permission given, Jev answering from the script, and the
        /// committed per-door switches deciding (no JevDraftsForTest), as in the product.</summary>
        private static async Task<(DecisionFixture F, ScriptedOracle Jev)> ProAsync(string file, Func<string, string, (string, double)?> script)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            await f.BuildModelAsync(file);
            await f.Engine.CreateTableAsync("Returns", "human");
            await f.Engine.CreateColumnAsync("table:Returns", "Qty", "Int64", null, "human");
            await f.SaveAsAsync(file);
            await f.ConsentAsync();
            var jev = new ScriptedOracle(script);
            f.Engine.UseDecisionsForTest(new DecisionService(jev, new DecisionBudget()));
            return (f, jev);
        }

        private static IEnumerable<DraftStepView> AllSteps(DraftView v) => v.Steps.Concat(v.After);

        // ---- D1: the agent door still says Jev does not draft ---------------------------------------------------------

        /// <summary>DEFECT D1. 31a92ca3 switched Jev drafting on for the Plan door, but compose_task's MCP description, which
        /// an assistant reads before it calls the tool, still says words alone open a menu with no step chosen and that
        /// "Jev does not choose steps yet", and its 'words' parameter says they open the menu. On Pro the words are now
        /// drafted into kept steps, so an assistant that trusts the description adds the same steps again.</summary>
        [Fact]
        public void D1_compose_task_tells_the_agent_door_that_Jev_drafts_from_words()
        {
            Assert.True(JevDraftBuilder.JevDraftsOnPlan);
            var m = typeof(McpTools).GetMethod(nameof(McpTools.ComposeTask), BindingFlags.Public | BindingFlags.Static);
            var text = m.GetCustomAttribute<DescriptionAttribute>().Description;
            var words = m.GetParameters().Single(p => p.Name == "words").GetCustomAttribute<DescriptionAttribute>().Description;
            Assert.DoesNotContain("Jev does not choose steps yet", text);
            Assert.DoesNotContain("no step is chosen", text);
            Assert.DoesNotContain("Alone, they open the draft as the menu", words);
        }

        // ---- D2: describe_workflow_steps says it reads nothing in the model ---------------------------------------------

        /// <summary>DEFECT D2 (in the part merged at 1ddd7b69, e05cdaa9). describe_workflow_steps' MCP description says it
        /// "Reads nothing in the model" and never mentions Jev, but on Pro with permission it reads the model's table
        /// names and pick options and sends them to Jev, once per step the words name.</summary>
        [Fact]
        public async Task D2_describe_workflow_steps_does_not_say_it_reads_nothing_when_it_reads_the_model_for_Jev()
        {
            Assert.True(JevDraftBuilder.JevDraftsOnWorkflows);
            var m = typeof(McpTools).GetMethod(nameof(McpTools.DescribeWorkflowSteps), BindingFlags.Public | BindingFlags.Static);
            var text = m.GetCustomAttribute<DescriptionAttribute>().Description;
            var (f, jev) = await ProAsync("DescribeReads.bim", (w, q) => q == "step:add-measure:include" ? Yes(0.97) : null);
            using (f)
            {
                var d = await f.Engine.DescribeWorkflowStepsAsync("add a measure to Sales");
                var asked = Assert.Single(jev.Seen);
                var tables = asked.Snapshot["table_names"] as IEnumerable<string>;
                Assert.Contains("Sales", tables ?? Array.Empty<string>());
                Assert.DoesNotContain("Reads nothing in the model", text);
            }
        }

        // ---- D3: what the audit surface says compose.draft sends and does ----------------------------------------------

        /// <summary>DEFECT D3a. compose.draft's purpose, shown under What Pro decides > What is sent and by get_decision_points
        /// ("the whole audit surface"), says it sends "the words they typed and the names of the model's tables, and nothing
        /// else: no columns". A Plan draft now asks it on Pro, and the ask offers the model's column and measure names as the
        /// options of its picks.</summary>
        [Fact]
        public async Task D3a_compose_draft_sends_only_what_its_purpose_says()
        {
            var (f, jev) = await ProAsync("Sends.bim", (w, q) => q == "step:hide:include" ? Yes(0.97) : null);
            using (f)
            {
                await f.Engine.ComposeTaskAsync("hide Amount", null, null, null, null, "human");
                var asked = Assert.Single(jev.Seen);
                var purpose = DecisionRegistry.Require(DecisionRegistry.ComposeDraft).Purpose;
                var offered = asked.Questions.SelectMany(q => q.Options ?? Array.Empty<DecisionOption>()).Select(o => o.Id)
                    .Where(id => id.StartsWith("column:", StringComparison.Ordinal) || id.StartsWith("measure:", StringComparison.Ordinal))
                    .Distinct().ToArray();
                // Fixer's note (m11/describe-jev fix, 26 Sep): as first written, this test asserted the OLD text ("no columns")
                // and that nothing was offered, so it could pass only by stripping the picks out of every ask; the lane's brief
                // is to make the text true instead. It now asserts the same defect from the other side: what one ask offered
                // is named by the purpose, and the purpose no longer says "no columns".
                Assert.NotEmpty(offered);
                Assert.DoesNotContain("no columns", purpose);
                foreach (var kind in offered.Select(id => id.Substring(0, id.IndexOf(':'))).Distinct())
                    Assert.True(purpose.Contains(kind + " names", StringComparison.Ordinal), "one Plan draft offered Jev " + kind
                        + " names, and compose.draft's purpose does not say it sends them: " + string.Join(", ", offered));
            }
        }

        /// <summary>DEFECT D3b. compose.draft's low-confidence behaviour, on the same audit surface, says "with no sure step
        /// the draft opens as the menu" (and that an unsure step arrives dropped). Since the per-clause rule (8f917deb) and the
        /// Plan switch (31a92ca3), a Plan draft where Jev is sure of nothing opens with the word match's steps KEPT, not as
        /// the menu.</summary>
        [Fact]
        public async Task D3b_compose_draft_says_what_happens_when_Jev_is_sure_of_nothing()
        {
            var (f, jev) = await ProAsync("NotSure.bim", (w, q) => null);   // Jev answers every clause, sure of nothing
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("refresh Sales, then run the tests", null, null, null, null, "human");
                Assert.Equal(2, jev.Seen.Count);
                var behaviour = DecisionRegistry.Require(DecisionRegistry.ComposeDraft).LowConfidenceBehaviour;
                var kept = AllSteps(v).Where(s => s.Kept).Select(s => s.Id).ToArray();
                // Fixer's note (m11/describe-jev fix, 26 Sep): as first written, this test asserted the OLD text and that the
                // draft opened with no kept step, which contradicts the lane's committed per-clause rule
                // (When_Jev_is_sure_of_nothing_the_words_draft_and_Jev_is_not_credited). The brief is to describe the real
                // Pro path, so it now asserts that: the words' steps are kept, and the text says so, not "opens as the menu".
                Assert.Equal(new[] { "refresh", "run-tests" }, kept);
                Assert.DoesNotContain("with no sure step the draft opens as the menu", behaviour);
                Assert.Contains("word match", behaviour);
            }
        }

        // ---- D4: Add more words says it added steps that arrive dropped ------------------------------------------------

        /// <summary>DEFECT D4 (seen in lane g with Jev on: "Jev read your new words and added 5 steps." when 1 step was kept
        /// and 4 arrived dropped). RefineAsync counts a step Jev only leaned to, which arrives dropped, as added and credits
        /// Jev with it.</summary>
        [Fact]
        public async Task D4_add_more_words_does_not_say_it_added_a_step_that_arrives_dropped()
        {
            var (f, _) = await ProAsync("RefineLean.bim", (w, q) =>
                About(w, "measure") && q == "step:add-measure:include" ? Yes(0.97)
                : About(w, "describe") && q == "step:describe:include" ? Yes(0.96)
                // leans, not sure: arrives dropped. A step that does not add: the adding rule (version 2, m11/sol-fixes-1
                // round 4) gives "and describe it" no step that adds, sure or leaning, so Relate two tables can no longer lean.
                : About(w, "describe") && q == "step:folder:include" ? Yes(0.6)
                : null);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("add a measure", null, null, null, null, "human");
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "and describe it", "human");
                Assert.Contains(v.Steps, s => s.Id == "describe" && s.Kept);
                Assert.Contains(v.Steps, s => s.Id == "folder" && !s.Kept);
                Assert.DoesNotContain("added 2 steps", v.Sentence ?? "");
            }
        }

        /// <summary>DEFECT D4b (seen in lane g with Jev on: "Jev read your new words and added 1 step." when Add more words
        /// added 2 kept steps, Put objects in a folder and Describe an object). When some added steps are Jev's and some
        /// are the words', RefineAsync says only Jev's count (JevRedrafted(added - addedByWords)), so the sentence
        /// under-counts what was added and never says the words named the rest.</summary>
        [Fact]
        public async Task D4b_add_more_words_counts_every_step_it_added()
        {
            var (f, _) = await ProAsync("RefineMixed.bim", (w, q) =>
                About(w, "measure") && q == "step:add-measure:include" ? Yes(0.97)
                : About(w, "folder") && q == "step:folder:include" ? Yes(0.96)
                : null);   // "describe it": Jev not sure, so the word match names the describe step (as in lane g)
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("add a measure", null, null, null, null, "human");
                var before = AllSteps(v).Where(s => s.Kept).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "then describe it and put it in a KPI folder", "human");
                // JOIN, part 2 (verifier VR20): Jev's sure pick for a later clause no longer answers K4, so the folder clause (Jev's alone)
                // leaves "add a measure" offered unticked. What this test counts is the steps the new words ADDED, so it counts the kept steps
                // that were not kept before, not the change in the number of kept steps.
                var added = AllSteps(v).Count(s => s.Kept && !before.Contains(s.Id));
                Assert.Equal(2, added);
                Assert.True(!(v.Sentence ?? "").Contains("added 1 step", StringComparison.Ordinal),
                    added + " steps were added, and the sentence reads: " + v.Sentence);
            }
        }

        // ---- D5: the summary asks for answers to a dropped step's questions -------------------------------------------

        /// <summary>DEFECT D5 (seen in lane g: "You answer 9 questions ..." when 8 of the 9 belonged to steps that arrived
        /// dropped). Only a kept step's questions are asked before the run (DraftRunner.AskedBeforeTheRun), but the summary
        /// counts every unanswered question (DraftRunner.cs openQuestions), so a step Jev leaned to, which arrives dropped
        /// with its own questions, inflates the count. The count is from lane L4; Jev drafting on the Plan makes it common.</summary>
        [Fact]
        public async Task D5_the_summary_does_not_count_the_questions_of_a_dropped_step()
        {
            var (f, _) = await ProAsync("DroppedQuestions.bim", (w, q) => q switch
            {
                "step:add-measure:include" => Yes(0.97),
                "step:add-measure:tableRef" => Pick("table:Sales", 0.95),
                "step:relate:include" => Yes(0.6),   // leans: arrives dropped, with its own questions
                _ => null,
            });
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("add a measure to Sales", null, null, null, null, "human");
                var relate = Assert.Single(v.Steps, s => s.Id == "relate");
                Assert.False(relate.Kept);
                Assert.Contains(v.Questions, q => q.Name.StartsWith("relate-", StringComparison.Ordinal));
                Assert.DoesNotContain("You answer", v.Summary);
            }
        }

        // ---- D7: two clauses that ask for the same kind of step become one step ----------------------------------------

        /// <summary>DEFECT D7. AskJevDraftByClauseAsync keeps each step template once ("A template is kept once, with the
        /// first sure pick for each argument"), so two clauses that ask for the same kind of step on different objects,
        /// "format Return Rate % as a percentage, then format Average Order Value as currency", draft ONE format step: the
        /// second clause and its object are dropped with no word to the person. The Workflows door keeps one step per
        /// clause. No labelled description in labels.json, labels-fresh.json or labels-plan2.json repeats an op, and the
        /// scorer compares sets of ops, so the replay could not see this.</summary>
        [Fact]
        public async Task D7_two_clauses_asking_for_the_same_kind_of_step_draft_two_steps()
        {
            var (f, jev) = await ProAsync("TwoFormats.bim", (w, q) =>
                q == "step:format:include" ? Yes(0.97)
                : q == "step:format:objRef" && About(w, "Total Sales") ? Pick("measure:Sales/Total Sales", 0.95)
                : q == "step:format:objRef" && About(w, "Order Count") ? Pick("measure:Sales/Order Count", 0.95)
                : null);
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("format Total Sales as currency, then format Order Count as a whole number",
                    null, null, null, null, "human");
                Assert.Equal(2, jev.Seen.Count);
                var formats = AllSteps(v).Where(s => s.Id.StartsWith("format", StringComparison.Ordinal)).ToArray();
                var targets = v.Questions.Where(q => q.Name.StartsWith("format", StringComparison.Ordinal) && q.Name.EndsWith("-objRef", StringComparison.Ordinal))
                    .Select(q => q.Answer).ToArray();
                Assert.True(formats.Length == 2, "two clauses each asked to format a measure; the draft has " + formats.Length
                    + " format step(s), for: " + string.Join(", ", targets) + ". Sentence: " + v.Sentence);
            }
        }

        // ---- D6: the per-clause rule's promise, "a clause the words name is never dropped" -----------------------------

        /// <summary>DEFECT D6a. The clauses are cut by DescribedSteps.From, which stops at 12: a thirteenth clause is
        /// dropped from the draft with no word to the person, although the per-clause rule says a clause the words name is
        /// never dropped.</summary>
        [Fact]
        public async Task D6a_a_thirteenth_clause_is_not_dropped_silently()
        {
            var (f, jev) = await ProAsync("Thirteen.bim", (w, q) => null);   // Jev sure of nothing: the words decide
            using (f)
            {
                const string twelve = "add a Margin measure, format it as a percent, put it in a KPI folder, describe the Sales table, "
                    + "hide the key columns, rename Qty to Quantity, add synonyms for revenue, relate Sales to Returns, "
                    + "mark Date as the date table, stop Amount summing, sort Month by Month Number, add a role for managers";
                Assert.Equal(12, DescribedSteps.From(twelve).Steps.Length);
                var v = await f.Engine.ComposeTaskAsync(twelve + ", then save the model", null, null, null, null, "human");
                var saved = AllSteps(v).Any(s => s.Id == "save");
                var said = (v.Sentence ?? "").Contains("save", StringComparison.OrdinalIgnoreCase)
                           || (v.Sentence ?? "").Contains("more than", StringComparison.OrdinalIgnoreCase)
                           || (v.MenuLine ?? "").Contains("more than", StringComparison.OrdinalIgnoreCase);
                Assert.True(saved || said, "the words name 'save the model' as clause 13; it was neither drafted nor mentioned. Sentence: " + v.Sentence);
            }
        }

        /// <summary>DEFECT D6b. Add more words joins the new words to the old ones with a space (RefineAsync), so new words
        /// typed without "and" or "then" become part of the old words' last clause. When Jev is not sure of that joined
        /// clause, the word match names one step for it, and the step the new words name is never drafted.</summary>
        [Fact]
        public async Task D6b_add_more_words_typed_without_and_still_drafts_the_step_they_name()
        {
            var (f, _) = await ProAsync("RefineJoin.bim", (w, q) => null);   // Jev sure of nothing: the words decide
            using (f)
            {
                var v = await f.Engine.ComposeTaskAsync("refresh Sales", null, null, null, null, "human");
                Assert.Contains(AllSteps(v), s => s.Id == "refresh" && s.Kept);
                v = await f.Engine.EditDraftAsync(v.DraftId, "refine", null, null, null, "save the model", "human");
                Assert.True(AllSteps(v).Any(s => s.Id == "save"), "the new words 'save the model' were not drafted. Sentence: " + v.Sentence
                    + " Steps: " + string.Join(", ", AllSteps(v).Select(s => s.Id + (s.Kept ? "" : " (dropped)"))));
            }
        }
    }
}
