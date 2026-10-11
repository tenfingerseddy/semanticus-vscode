// The Plan's cleanup row, as Kane means it (round 5 of m11/plan-harm; Kane, 30 Sep 2026):
//   "this step is not for copilot. its removing unused columns to keep a model lean. its perfectly fine on a build from scratch.
//    generally we just give the user the option to waive this or customise it. ... i would think Jev could choose what to exclude.
//    in any case the plan is supposed to be exactly that, a plan. the user reviews before execution and can just turn this off or
//    customise. ... its more of a suggestion list as even a legit deletion may be required for a future report. only a user can
//    properly assess this with the business context."
//
// What these tests hold, one line each:
//   the row says plainly what it is for (a lean model, a suggestion the person reviews) and never mentions Copilot
//   (b) Work it out lists EVERY column the sweep calls unused, each with a start (ticked or unticked) and a reason, and writes nothing
//   (a) Run the plan never removes a cleanup line the person has not looked at: the row is never Semanticus's, so it is never in the
//       ready set (both doors, both tiers), and if it were (the plan's memory forced), an unreviewed line still stays: not on the
//       Studio door, not on the assistant's door, not with a set already worked out, not with none, and not because the person
//       ticked some OTHER line
//   (a) a person's own tick, Keep all or Approve makes the lines theirs, and the removal is one undo
//   (a) Free: Run the plan is refused, Work it out removes nothing, the assistant's generic apply_plan leaves a suggestion alone
//   (c) Jev can only UNTICK: "suggest" for everything ticks exactly what the rule ticks, a "leave it out" unticks with its reason,
//       and nothing Jev says can tick a column the rule left unticked
//   Jev not sure a column should go: the line is unticked with a not-sure note (the careful direction); Jev off, slow, unpermitted or
//   on Free: the rule's ticks stand, and Free asks nothing at all
//   (d) a fact's text column that holds a date as text, and a dimension's alternate key, arrive UNTICKED with a reason when Jev
//       answers (the held-out run's two columns, rebuilt small)

using System;
using System.Collections.Generic;
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
    public sealed class PlanCleanupReviewTests
    {
        // ---- the model ------------------------------------------------------------------------------------------

        /// <summary>A client dimension with a name column, two plain text columns and a date, an invoice fact with an amount and one
        /// text column that holds a billing date as text, one hand-written measure. Saved and opened again, so nothing in it was built in
        /// this session. The sweep calls every column but the two keys unused.</summary>
        private static async Task<DecisionFixture> ModelAsync(string file, bool pro, Func<LocalEngine, Task> more = null)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso Consulting", 1604);
            await e.CreateTableAsync("dim_client", "human");
            await e.CreateColumnAsync("table:dim_client", "CLIENT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:dim_client", "CLNT_NM", "String", null, "human");
            await e.CreateColumnAsync("table:dim_client", "INDUSTRY_CD", "String", null, "human");
            await e.CreateColumnAsync("table:dim_client", "NOTE_TXT", "String", null, "human");
            await e.CreateColumnAsync("table:dim_client", "ONBOARDED_ON", "DateTime", null, "human");
            await e.CreateTableAsync("fact_invoice", "human");
            await e.CreateColumnAsync("table:fact_invoice", "CLIENT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "INV_AMT", "Double", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "BILLED_AS", "String", null, "human");
            await e.CreateRelationshipAsync("column:fact_invoice/CLIENT_KEY", "column:dim_client/CLIENT_KEY", null, true, "human");
            await e.CreateMeasureAsync("table:fact_invoice", "Invoice Count", "COUNTROWS ( fact_invoice )", "human");
            if (more != null) await more(e);
            await f.SaveAsAsync(file);
            await e.OpenAsync(Path.Combine(f.Workspace, file), discardUnsaved: true);
            f.UseNull();
            return f;
        }

        /// <summary>What the rule alone ticks on this model.</summary>
        private static readonly string[] DefaultTicked =
            { "column:dim_client/INDUSTRY_CD", "column:dim_client/NOTE_TXT", "column:fact_invoice/BILLED_AS" };

        /// <summary>The columns the sweep calls unused and this list holds: everything but the two keys, and not the measure.</summary>
        private static readonly string[] AllListed =
        {
            "column:dim_client/CLNT_NM", "column:dim_client/INDUSTRY_CD", "column:dim_client/NOTE_TXT", "column:dim_client/ONBOARDED_ON",
            "column:fact_invoice/BILLED_AS", "column:fact_invoice/INV_AMT",
        };

        private static ModelPlanItem Row(ModelPlanView plan, string id) => plan.Items.SingleOrDefault(i => i.Id == id);

        private static async Task<ModelPlanChangeSet> WorkItOutAsync(DecisionFixture f, string origin = "human")
        {
            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", origin);
            var set = Row(proposed.Plan, "cleanup/model")?.ChangeSet;
            Assert.NotNull(set);
            return set;
        }

        private static string[] Ticked(ModelPlanChangeSet set) => set.Lines.Where(l => l.Kept).Select(l => l.Target).OrderBy(t => t).ToArray();

        private static async Task<bool> HasColumnAsync(DecisionFixture f, string table, string column) =>
            await f.Sessions.Current.ReadAsync(m => m.Tables[table].Columns.Contains(column));

        private static async Task<string[]> ColumnsAsync(DecisionFixture f) =>
            await f.Sessions.Current.ReadAsync(m => m.Tables.SelectMany(t => t.Columns.Select(c => t.Name + "[" + c.Name + "]")).OrderBy(x => x).ToArray());

        /// <summary>Set aside every other open row, so the cleanup is the only thing Run the plan could take.</summary>
        private static async Task<ModelPlanView> ClearTheWayAsync(DecisionFixture f)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                var plan = await f.Engine.GetModelPlanAsync();
                foreach (var i in plan.Items.Where(i => i.Id != "cleanup/model"
                                                       && (i.Status == ModelPlanStatuses.Ready || i.Status == ModelPlanStatuses.Proposed)))
                    await f.Engine.SkipModelPlanItemAsync(i.Id, "out of the way for this test", "human");
            }
            return await f.Engine.GetModelPlanAsync();
        }

        /// <summary>The DEFENCE IN DEPTH case. The product never lets the cleanup row be Semanticus's (the doer switch refuses it and the
        /// row's own doer is the person), so Run the plan never takes it. A plan sidecar is a file, though, and a future change could
        /// let the switch through, so the test writes the doer into the plan's real memory (the seam every plan test uses) and proves
        /// Run the plan STILL leaves an unreviewed line alone.</summary>
        private static async Task<ModelPlanView> ForceTheCleanupIntoTheReadySetAsync(DecisionFixture f)
        {
            await ClearTheWayAsync(f);
            Assert.True(f.Engine.SeedPlanMemoryForTest(state => state.Residue["cleanup/model"] = new PlanRowResidue { Doer = PlanDoers.Semanticus }));
            var plan = await f.Engine.GetModelPlanAsync();
            var cleanup = Row(plan, "cleanup/model");
            Assert.Equal(PlanDoers.Semanticus, cleanup.Doer);
            Assert.Equal(PlanStateWords.Ready, cleanup.StateWord);
            Assert.Contains("cleanup/model", plan.ReadyRows());
            return plan;
        }

        // ---- the row says what it is, and never says Copilot ---------------------------------------------------------

        [Theory, InlineData(true), InlineData(false)]
        public async Task The_row_says_it_keeps_the_model_lean_is_a_suggestion_the_person_reviews_and_never_mentions_Copilot(bool pro)
        {
            using var f = await ModelAsync("CrCopy" + (pro ? "P" : "F") + ".bim", pro);
            var row = Row(await f.Engine.GetModelPlanAsync(), "cleanup/model");
            Assert.NotNull(row);

            Assert.Contains(LocalEngine.CleanupPurposeSentence, row.Why);
            Assert.Contains("lean", LocalEngine.CleanupPurposeSentence, StringComparison.Ordinal);
            Assert.Contains("untick any line", LocalEngine.CleanupPurposeSentence, StringComparison.Ordinal);
            Assert.Contains("set the whole row aside", LocalEngine.CleanupPurposeSentence, StringComparison.Ordinal);
            Assert.Contains("later", LocalEngine.CleanupPurposeSentence, StringComparison.Ordinal);   // a report you build later
            Assert.Contains(LocalEngine.CleanupCountSentence(DefaultTicked.Length, AllListed.Length - DefaultTicked.Length), row.Why);
            Assert.True(row.Optional);
            Assert.Contains(row.Why, w => w.Contains("Nothing else on the plan waits for this", StringComparison.Ordinal));

            var everything = string.Join("\n", row.Why.Concat(new[] { row.Title, row.Sub, row.Value, row.Touches }).Concat(row.Diff));
            Assert.DoesNotContain("Copilot", everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("data agent", everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\u2014", everything);
            // The row counts what starts ticked in its title and diff, and says how many start unticked. Nothing about it is a claim
            // that the rest are hidden: the count sentence says they are listed.
            Assert.Equal("Remove 3 unused columns", row.Title);
            Assert.Equal(3, row.Diff.Length);
            Assert.DoesNotContain(row.Diff, d => d.Contains("ONBOARDED_ON", StringComparison.Ordinal));
        }

        // ---- (b) every unused column is listed, each with a start and a reason -----------------------------------------

        [Theory, InlineData(true, "human"), InlineData(true, "agent"), InlineData(false, "human"), InlineData(false, "agent")]
        public async Task Work_it_out_lists_every_unused_column_ticks_the_suggested_ones_gives_each_a_reason_and_writes_nothing(bool pro, string door)
        {
            using var f = await ModelAsync("CrList" + (pro ? "P" : "F") + door + ".bim", pro);
            var before = await ColumnsAsync(f);
            var revision = f.Sessions.Current.Revision;

            var set = await WorkItOutAsync(f, door);

            // (b) Every column the sweep calls unused is a line. The two keys are not safe (a relationship uses them) and the measure is
            // not a column, so they are not on it; the row said so.
            var sweep = await f.Engine.UnusedObjectsAsync();
            var safeColumns = sweep.Items.Where(i => (i.Verdict == "safe" || i.Verdict == "visibleAttribute") && i.Kind != "measure").Select(i => i.Ref).OrderBy(r => r).ToArray();
            Assert.Equal(AllListed, safeColumns);
            Assert.Equal(AllListed, set.Lines.Select(l => l.Target).OrderBy(t => t).ToArray());

            // A start and a reason on every line, in plain words.
            Assert.All(set.Lines, l =>
            {
                Assert.False(string.IsNullOrWhiteSpace(l.Reason), l.Target + " has no reason");
                Assert.DoesNotContain("\u2014", l.Reason);
                Assert.Equal(l.Kept, l.Suggested);            // a line that starts ticked is a suggestion nobody has touched
            });
            Assert.Equal(DefaultTicked.OrderBy(t => t).ToArray(), Ticked(set));
            foreach (var t in DefaultTicked) Assert.Equal("Nothing uses it, and nothing about it looks like a key, a date, a name or a number.", set.Lines.Single(l => l.Target == t).Reason);
            Assert.Contains("date", set.Lines.Single(l => l.Target == "column:dim_client/ONBOARDED_ON").Reason, StringComparison.Ordinal);
            Assert.Contains("number", set.Lines.Single(l => l.Target == "column:fact_invoice/INV_AMT").Reason, StringComparison.Ordinal);
            Assert.StartsWith("Not ticked:", set.Lines.Single(l => l.Target == "column:dim_client/CLNT_NM").Reason, StringComparison.Ordinal);
            // The measure is named on the set's left-out list, with why, so it is not hidden either.
            Assert.Contains(set.Refused, r => r.Target == "measure:fact_invoice/Invoice Count" && r.Reason.Contains("measure", StringComparison.Ordinal));

            // Nothing was written.
            Assert.Equal(revision, f.Sessions.Current.Revision);
            Assert.Equal(before, await ColumnsAsync(f));
        }

        [Fact]
        public async Task A_model_built_from_scratch_gets_the_row_too_with_everything_unticked_and_the_reason_on_each_line()
        {
            // Kane: "its perfectly fine on a build from scratch". Built in this session, so nothing is suggested, and everything is listed.
            var f = new DecisionFixture(TestEntitlements.Free);
            using var _ = f;
            await f.Engine.CreateModelAsync("Scratch", 1604);
            await f.Engine.CreateTableAsync("Customer", "human");
            await f.Engine.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "Segment", "String", null, "human");
            await f.Engine.CreateTableAsync("Sales", "human");
            await f.Engine.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await f.Engine.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await f.Engine.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.SaveAsAsync("CrScratch.bim");

            var plan = await f.Engine.GetModelPlanAsync();
            var row = Row(plan, "cleanup/model");
            Assert.NotNull(row);
            Assert.Equal("Review 2 unused columns", row.Title);
            Assert.Empty(row.Diff);
            Assert.True(row.Optional);
            var set = await WorkItOutAsync(f);
            Assert.Equal(new[] { "column:Customer/Segment", "column:Sales/Amount" }, set.Lines.Select(l => l.Target).OrderBy(t => t).ToArray());
            Assert.Empty(Ticked(set));
            Assert.All(set.Lines, l => Assert.Contains("you built it in this session", l.Reason, StringComparison.Ordinal));
        }

        // ---- (a) Run the plan never deletes a line the person has not looked at ------------------------------------------

        [Theory, InlineData(true, "human"), InlineData(true, "agent"), InlineData(false, "human"), InlineData(false, "agent")]
        public async Task The_cleanup_row_is_never_Semanticuss_so_Run_the_plan_never_takes_it_on_either_door_on_either_tier(bool pro, string door)
        {
            using var f = await ModelAsync("CrNever" + (pro ? "P" : "F") + door + ".bim", pro);
            await WorkItOutAsync(f, door);
            var before = await ColumnsAsync(f);
            var plan = await ClearTheWayAsync(f);

            // The person cannot hand it over: "Let it run" is only on a row the engine can apply with no choices, and this is a list.
            await Assert.ThrowsAsync<ArgumentException>(() => f.Engine.SetPlanItemDoerAsync("cleanup/model", PlanDoers.Semanticus));
            plan = await f.Engine.GetModelPlanAsync();
            Assert.Equal(PlanDoers.You, Row(plan, "cleanup/model").Doer);
            Assert.DoesNotContain("cleanup/model", plan.ReadyRows());

            var run = await f.Engine.ApplyPlanItemsAsync(null, door, plan.ReadyDigest ?? "x");
            Assert.False(run.Performed, run.Sentence);
            Assert.Equal(before, await ColumnsAsync(f));
            // Pro: there is simply nothing ready. Free: Run the plan is refused before anything is read.
            Assert.Equal(pro ? LocalEngine.NothingReadySentence : LocalEngine.BulkRunIsProSentence, run.Sentence);
        }

        [Theory,
         InlineData("human", false), InlineData("agent", false),      // no set worked out yet: Run the plan would work it out itself
         InlineData("human", true), InlineData("agent", true)]        // a set is already on the row
        public async Task Even_if_the_cleanup_were_in_the_ready_set_Run_the_plan_never_removes_a_line_that_is_ticked_only_because_it_was_suggested(string door, bool workedOutFirst)
        {
            using var f = await ModelAsync("CrRun" + door + (workedOutFirst ? "W" : "N") + ".bim", pro: true);
            var before = await ColumnsAsync(f);
            if (workedOutFirst) await WorkItOutAsync(f, door);
            var plan = await ForceTheCleanupIntoTheReadySetAsync(f);
            var revision = f.Sessions.Current.Revision;

            var result = await f.Engine.ApplyPlanItemsAsync(null, door, plan.ReadyDigest);

            Assert.False(result.Performed, result.Sentence);
            Assert.Equal(0, result.Applied);
            Assert.Equal(revision, f.Sessions.Current.Revision);
            Assert.Equal(before, await ColumnsAsync(f));
            // Nothing applied, so the answer is the sentence: the row was left out, and why, and what to do.
            Assert.StartsWith("Nothing was changed.", result.Sentence, StringComparison.Ordinal);
            Assert.Contains(LocalEngine.CleanupNotReviewedSentence(DefaultTicked.Length).TrimEnd('.'), result.Sentence, StringComparison.Ordinal);
            Assert.Contains("Nothing was removed", result.Sentence, StringComparison.Ordinal);
            Assert.DoesNotContain("\u2014", result.Sentence);
        }

        [Fact]
        public async Task Ticking_some_other_line_does_not_review_the_suggestions()
        {
            using var f = await ModelAsync("CrOther.bim", pro: true);
            var set = await WorkItOutAsync(f);
            // The person ticks one column the rule left unticked. The three suggestions are still untouched.
            var date = set.Lines.Single(l => l.Target == "column:dim_client/ONBOARDED_ON");
            await f.Engine.SetPlanItemAsync(date.PlanItemId, null, approved: true, "human");
            var plan = await ForceTheCleanupIntoTheReadySetAsync(f);
            var before = await ColumnsAsync(f);

            var result = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);

            Assert.False(result.Performed, result.Sentence);
            Assert.Equal(before, await ColumnsAsync(f));
            Assert.Contains("Nothing was removed", result.Sentence, StringComparison.Ordinal);
        }

        [Theory, InlineData("human"), InlineData("agent")]
        public async Task If_the_cleanup_were_in_the_ready_set_a_person_who_looked_at_every_ticked_line_lets_Run_the_plan_remove_them_as_one_undo(string door)
        {
            using var f = await ModelAsync("CrTouched" + door + ".bim", pro: true);
            var set = await WorkItOutAsync(f, door);
            // Keep all, as the Studio's button does: every line is touched, so every ticked line is the person's now.
            foreach (var line in set.Lines.Where(l => l.Kept))
                await f.Engine.SetPlanItemAsync(line.PlanItemId, null, approved: true, door);
            var afterKeep = Row(await f.Engine.GetModelPlanAsync(), "cleanup/model").ChangeSet;
            Assert.All(afterKeep.Lines, l => Assert.False(l.Suggested));
            Assert.Equal(DefaultTicked.OrderBy(t => t).ToArray(), Ticked(afterKeep));
            var plan = await ForceTheCleanupIntoTheReadySetAsync(f);

            var result = await f.Engine.ApplyPlanItemsAsync(null, door, plan.ReadyDigest);

            Assert.True(result.Performed, result.Sentence);
            Assert.False(await HasColumnAsync(f, "dim_client", "INDUSTRY_CD"));
            Assert.False(await HasColumnAsync(f, "dim_client", "NOTE_TXT"));
            Assert.False(await HasColumnAsync(f, "fact_invoice", "BILLED_AS"));
            // What was never ticked is still there.
            Assert.True(await HasColumnAsync(f, "dim_client", "CLNT_NM"));
            Assert.True(await HasColumnAsync(f, "dim_client", "ONBOARDED_ON"));
            Assert.True(await HasColumnAsync(f, "fact_invoice", "INV_AMT"));

            await f.Engine.UndoAsync("human");
            Assert.True(await HasColumnAsync(f, "dim_client", "INDUSTRY_CD"));
            Assert.True(await HasColumnAsync(f, "dim_client", "NOTE_TXT"));
            Assert.True(await HasColumnAsync(f, "fact_invoice", "BILLED_AS"));
        }

        [Fact]
        public async Task If_the_cleanup_were_in_the_ready_set_a_person_who_unticks_one_suggestion_has_looked_and_the_rest_run()
        {
            using var f = await ModelAsync("CrUntick.bim", pro: true);
            var set = await WorkItOutAsync(f);
            // Unticking is looking too: the person drops BILLED_AS and, with the same look, keeps the other two.
            await f.Engine.SetPlanItemAsync(set.Lines.Single(l => l.Target == "column:fact_invoice/BILLED_AS").PlanItemId, null, approved: false, "human");
            foreach (var t in new[] { "column:dim_client/INDUSTRY_CD", "column:dim_client/NOTE_TXT" })
                await f.Engine.SetPlanItemAsync(set.Lines.Single(l => l.Target == t).PlanItemId, null, approved: true, "human");
            var plan = await ForceTheCleanupIntoTheReadySetAsync(f);

            var result = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);

            Assert.True(result.Performed, result.Sentence);
            Assert.False(await HasColumnAsync(f, "dim_client", "INDUSTRY_CD"));
            Assert.False(await HasColumnAsync(f, "dim_client", "NOTE_TXT"));
            Assert.True(await HasColumnAsync(f, "fact_invoice", "BILLED_AS"));
        }

        [Theory, InlineData("human"), InlineData("agent")]
        public async Task Approving_the_row_is_the_persons_own_act_and_applies_the_ticked_lines_as_one_undo(string door)
        {
            // Free on purpose: approve on a row that is the person's is free on every tier.
            using var f = await ModelAsync("CrApprove" + door + ".bim", pro: false);
            var set = await WorkItOutAsync(f, door);

            var approved = await f.Engine.ModelPlanActionAsync("cleanup/model", "approve", door, set.Digest);

            Assert.True(approved.Performed, approved.Sentence);
            Assert.False(await HasColumnAsync(f, "dim_client", "INDUSTRY_CD"));
            Assert.False(await HasColumnAsync(f, "dim_client", "NOTE_TXT"));
            Assert.False(await HasColumnAsync(f, "fact_invoice", "BILLED_AS"));
            Assert.True(await HasColumnAsync(f, "dim_client", "CLNT_NM"));
            await f.Engine.UndoAsync("human");
            Assert.True(await HasColumnAsync(f, "dim_client", "INDUSTRY_CD"));
        }

        // ---- Free ------------------------------------------------------------------------------------------------------

        [Theory, InlineData("human"), InlineData("agent")]
        public async Task On_Free_Run_the_plan_is_refused_and_Work_it_out_and_the_assistants_apply_plan_remove_nothing(string door)
        {
            using var f = await ModelAsync("CrFree" + door + ".bim", pro: false);
            var before = await ColumnsAsync(f);
            var set = await WorkItOutAsync(f, door);
            Assert.Equal(DefaultTicked.OrderBy(t => t).ToArray(), Ticked(set));    // the suggestions arrive ticked on Free too
            Assert.Equal(before, await ColumnsAsync(f));

            var plan = await f.Engine.GetModelPlanAsync();
            var run = await f.Engine.ApplyPlanItemsAsync(null, door, plan.ReadyDigest ?? "x");
            Assert.False(run.Performed);
            Assert.Equal(before, await ColumnsAsync(f));

            // The change plan's own apply_plan, from the assistant's door with nothing named: it applies what is approved, and a
            // suggestion nobody looked at is not approved by anyone. (From the person's own door it applies what they can see ticked.)
            if (string.Equals(door, "agent", StringComparison.Ordinal))
            {
                var report = await f.Engine.ApplyPlanAsync(null, "agent");
                Assert.Equal(0, report.AppliedCount);
                Assert.Equal(before, await ColumnsAsync(f));
                var stillThere = Row(await f.Engine.GetModelPlanAsync(), "cleanup/model").ChangeSet;
                Assert.Equal(DefaultTicked.OrderBy(t => t).ToArray(), Ticked(stillThere));   // still suggested, still waiting for the person
            }
        }

        // ---- Jev ---------------------------------------------------------------------------------------------------------

        private sealed class ScriptedOracle : IDecisionOracle
        {
            private readonly Func<string, (string Choice, double Confidence)> _answer;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            public ScriptedOracle(Func<string, (string, double)> answer) { _answer = answer; }
            public bool Available => true;
            public IReadOnlyList<DecisionRequest> Seen { get { lock (_seen) return _seen.ToList(); } }
            public IEnumerable<string> AskedRefs => Seen.SelectMany(r => r.Questions).Select(q => q.Name.Substring(CleanupSuggest.Prefix.Length));
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                // The plan asks other points too (column format, the ranking); this oracle speaks only for the cleanup.
                if (!string.Equals(request.PointName, DecisionRegistry.CleanupSuggest, StringComparison.Ordinal))
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "not the cleanup"));
                lock (_seen) _seen.Add(request);
                var answers = new Dictionary<string, object>();
                foreach (var q in request.Questions)
                {
                    var (choice, confidence) = _answer(q.Name.Substring(CleanupSuggest.Prefix.Length));
                    answers[q.Name] = new Dictionary<string, object> { ["type"] = "choice", ["choice"] = choice, ["confidence"] = confidence };
                }
                return Task.FromResult(DecisionAnswerReader.Read(request, JsonSerializer.SerializeToElement(answers), "jev-test-1"));
            }
        }

        private sealed class NeverAnswers : IDecisionOracle
        {
            public bool Available => true;
            public int Asked;
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (!string.Equals(request.PointName, DecisionRegistry.CleanupSuggest, StringComparison.Ordinal))
                    return DecisionAnswers.NotDecided(request, "not the cleanup");
                Interlocked.Increment(ref Asked);
                await Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None);   // deliberately ignores the token: a slow service
                return DecisionAnswers.NotDecided(request, "never");
            }
        }

        private static async Task<T> WithJevAsync<T>(DecisionFixture f, T oracle) where T : IDecisionOracle
        {
            await f.ConsentAsync();
            f.Engine.UseDecisionsForTest(new DecisionService(oracle, new DecisionBudget()));
            return oracle;
        }

        [Fact]
        public async Task Jev_answering_suggest_to_everything_ticks_exactly_what_the_rule_ticks()
        {
            using var f = await ModelAsync("CrJevAll.bim", pro: true);
            var baseline = Ticked(await WorkItOutAsync(f));
            Assert.Equal(DefaultTicked.OrderBy(t => t).ToArray(), baseline);

            var jev = await WithJevAsync(f, new ScriptedOracle(_ => ("suggest", 0.97)));
            var set = await WorkItOutAsync(f);

            Assert.Equal(baseline, Ticked(set));
            Assert.Equal(AllListed, set.Lines.Select(l => l.Target).OrderBy(t => t).ToArray());
            // It was asked, and asked about the ticked columns only: what the rule left unticked is never sent.
            Assert.Equal(DefaultTicked.OrderBy(t => t).ToArray(), jev.AskedRefs.OrderBy(t => t).ToArray());
            Assert.All(set.Lines.Where(l => l.Kept), l => Assert.Equal("Nothing uses it, and nothing about it looks like a key, a date, a name or a number.", l.Reason));
        }

        [Fact]
        public async Task Jev_leaving_a_column_out_unticks_it_with_its_reason_and_the_rest_stay_ticked()
        {
            using var f = await ModelAsync("CrJevOut.bim", pro: true);
            await WithJevAsync(f, new ScriptedOracle(r => r.EndsWith("/BILLED_AS", StringComparison.Ordinal) ? ("leave_out", 0.9) : ("suggest", 0.95)));

            var set = await WorkItOutAsync(f);

            Assert.Equal(new[] { "column:dim_client/INDUSTRY_CD", "column:dim_client/NOTE_TXT" }, Ticked(set));
            var billed = set.Lines.Single(l => l.Target == "column:fact_invoice/BILLED_AS");
            Assert.False(billed.Kept);
            Assert.False(billed.Suggested);
            Assert.Equal("Not ticked: Jev would leave it out of the suggestion (sure). Tick it if you want it removed.", billed.Reason);
            Assert.Equal(ModelPlanChangeKinds.Decision, billed.Provenance.Kind);
            Assert.Equal(DecisionRegistry.CleanupSuggest, billed.Provenance.Point);
            Assert.Equal("sure", billed.Provenance.ConfidenceWord);
            Assert.DoesNotContain("\u2014", billed.Reason);
            // It is still a line: the person can tick it.
            Assert.Contains(set.Lines, l => l.Target == "column:fact_invoice/BILLED_AS");
        }

        [Fact]
        public async Task Jev_not_sure_a_column_should_go_unticks_it_and_says_so_and_never_the_other_way()
        {
            using var f = await ModelAsync("CrJevLow.bim", pro: true);
            // Sure it goes (0.95): stays ticked. Not sure it goes (0.4): unticked with the not-sure note. A leave out at any confidence unticks.
            await WithJevAsync(f, new ScriptedOracle(r =>
                r.EndsWith("/NOTE_TXT", StringComparison.Ordinal) ? ("suggest", 0.4)
                : r.EndsWith("/BILLED_AS", StringComparison.Ordinal) ? ("leave_out", 0.4) : ("suggest", 0.95)));
            var set = await WorkItOutAsync(f);
            Assert.Equal(new[] { "column:dim_client/INDUSTRY_CD" }, Ticked(set));
            Assert.Equal("Not ticked: Jev is not sure it should go. Tick it if you want it removed.", set.Lines.Single(l => l.Target == "column:dim_client/NOTE_TXT").Reason);
            Assert.StartsWith("Not ticked: Jev would leave it out of the suggestion", set.Lines.Single(l => l.Target == "column:fact_invoice/BILLED_AS").Reason, StringComparison.Ordinal);
            Assert.Equal("unsure", set.Lines.Single(l => l.Target == "column:dim_client/NOTE_TXT").Provenance.ConfidenceWord);
        }

        [Fact]
        public async Task Nothing_Jev_says_can_tick_a_column_the_rule_left_unticked_or_add_one_to_the_list()
        {
            using var f = await ModelAsync("CrJevTick.bim", pro: true);
            var point = DecisionRegistry.Require(DecisionRegistry.CleanupSuggest);
            var picks = new Dictionary<string, CleanupGuard.Pick>(StringComparer.OrdinalIgnoreCase)
            {
                ["column:dim_client/ONBOARDED_ON"] = new CleanupGuard.Pick { Ticked = false, Reason = "Not ticked: it holds dates." },
                ["column:dim_client/NOTE_TXT"] = new CleanupGuard.Pick { Ticked = true, Reason = CleanupGuard.TickedReason },
            };
            // An answer for a column the rule left unticked, one for a column that is not on the list at all, and one for a ticked column.
            var refs = new[] { "column:dim_client/ONBOARDED_ON", "column:nowhere/Ghost", "column:dim_client/NOTE_TXT" };
            var questions = refs.Select(r => new DecisionQuestion(CleanupSuggest.Q(r), DecisionQuestionKind.Choice, "Should it go?",
                DecisionRegistry.CleanupChoices.Select(o => new DecisionOption(o, "criterion " + o)).ToList())).ToList();
            var request = new DecisionRequest(point.Name, point.SchemaVersion, new Dictionary<string, object>(), questions);
            var json = JsonSerializer.SerializeToElement(refs.ToDictionary(CleanupSuggest.Q, r => (object)new Dictionary<string, object>
                { ["type"] = "choice", ["choice"] = "suggest", ["confidence"] = 0.99 }));
            var answers = DecisionAnswerReader.Read(request, json, "jev-test-1");
            Assert.True(answers.Decided, answers.NotDecidedReason);

            var changed = CleanupSuggest.Apply(picks, answers, point.ConfidenceBar);

            Assert.Equal(0, changed);
            Assert.False(picks["column:dim_client/ONBOARDED_ON"].Ticked);
            Assert.True(picks["column:dim_client/NOTE_TXT"].Ticked);
            Assert.Equal(2, picks.Count);
        }

        [Fact]
        public async Task Jev_slow_or_not_deciding_or_not_permitted_leaves_the_rules_ticks_and_Free_asks_nothing()
        {
            var expected = DefaultTicked.OrderBy(t => t).ToArray();

            // Slow: the oracle never answers and ignores its token. Work it out still returns, with the rule's ticks.
            using (var f = await ModelAsync("CrJevSlow.bim", pro: true))
            {
                var slow = await WithJevAsync(f, new NeverAnswers());
                f.Engine.CleanupJevWaitForTest = TimeSpan.FromMilliseconds(300);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var set = await WorkItOutAsync(f);
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), "Work it out waited " + timer.Elapsed);
                Assert.True(slow.Asked > 0);
                Assert.Equal(expected, Ticked(set));
            }

            // Not deciding: an available service that has no answer for this evidence.
            using (var f = await ModelAsync("CrJevShrug.bim", pro: true))
            {
                await f.ConsentAsync();
                var recorded = f.UseEmptyRecorded();
                var set = await WorkItOutAsync(f);
                Assert.True(recorded.AskCount > 0);
                Assert.Equal(expected, Ticked(set));
            }

            // Not permitted: no consent, so nothing is sent.
            using (var f = await ModelAsync("CrJevNoConsent.bim", pro: true))
            {
                var oracle = new ScriptedOracle(_ => ("leave_out", 0.99));
                f.Engine.UseDecisionsForTest(new DecisionService(oracle, new DecisionBudget()));
                var set = await WorkItOutAsync(f);
                Assert.Empty(oracle.Seen);
                Assert.Equal(expected, Ticked(set));
            }

            // Switched off for this model: nothing is sent.
            using (var f = await ModelAsync("CrJevOff.bim", pro: true))
            {
                var oracle = await WithJevAsync(f, new ScriptedOracle(_ => ("leave_out", 0.99)));
                await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.CleanupSuggest, false, "human");
                var set = await WorkItOutAsync(f);
                Assert.Empty(oracle.Seen);
                Assert.Equal(expected, Ticked(set));
            }

            // Free: not asked, not even with consent and a service in place. Not counted either.
            using (var f = await ModelAsync("CrJevFree.bim", pro: false))
            {
                var oracle = await WithJevAsync(f, new ScriptedOracle(_ => ("leave_out", 0.99)));
                var set = await WorkItOutAsync(f);
                Assert.Empty(oracle.Seen);
                Assert.Equal(expected, Ticked(set));
            }
        }

        [Fact]
        public async Task The_question_Jev_gets_holds_names_types_and_shapes_and_never_a_value()
        {
            using var f = await ModelAsync("CrJevSnap.bim", pro: true);
            var jev = await WithJevAsync(f, new ScriptedOracle(_ => ("suggest", 0.95)));
            await WorkItOutAsync(f);

            var request = Assert.Single(jev.Seen);
            Assert.Equal(DecisionRegistry.CleanupSuggest, request.PointName);
            Assert.Equal(new[] { "cleanup_columns", "cleanup_tables" }, request.Snapshot.Keys.OrderBy(k => k).ToArray());
            var json = System.Text.Json.JsonSerializer.Serialize(request.Snapshot);
            foreach (var name in new[] { "BILLED_AS", "INDUSTRY_CD", "NOTE_TXT", "dim_client", "fact_invoice", "String" })
                Assert.Contains(name, json, StringComparison.Ordinal);
            Assert.Contains("lookup side", json, StringComparison.Ordinal);
            Assert.Contains("fact side", json, StringComparison.Ordinal);
            Assert.Equal(3, request.Questions.Count);
            Assert.All(request.Questions, q => Assert.Equal(new[] { "suggest", "leave_out" }, q.Options.Select(o => o.Id).ToArray()));
        }

        // ---- (d) the held-out run's two columns, rebuilt small ----------------------------------------------------------

        [Fact]
        public async Task A_dimensions_alternate_key_with_no_key_word_and_a_facts_date_held_as_text_arrive_unticked_when_Jev_answers()
        {
            using var f = await ModelAsync("CrHeldOut.bim", pro: true, more: async e =>
            {
                await e.CreateTableAsync("dim_item", "human");
                await e.CreateColumnAsync("table:dim_item", "ITEM_KEY", "Int64", null, "human");
                await e.CreateColumnAsync("table:dim_item", "Item", "String", null, "human");
                await e.CreateColumnAsync("table:dim_item", "BARCODE", "String", null, "human");
                await e.CreateColumnAsync("table:dim_item", "BRAND_NOTE", "String", null, "human");
                await e.CreateColumnAsync("table:fact_invoice", "ITEM_KEY", "Int64", null, "human");
                await e.CreateRelationshipAsync("column:fact_invoice/ITEM_KEY", "column:dim_item/ITEM_KEY", null, true, "human");
            });

            // The rule alone still ticks both (its belts are frozen and it has no signal word to go on). The person reviews them.
            var without = await WorkItOutAsync(f);
            Assert.Contains("column:dim_item/BARCODE", Ticked(without));
            Assert.Contains("column:fact_invoice/BILLED_AS", Ticked(without));

            var jev = await WithJevAsync(f, new ScriptedOracle(r =>
                r.EndsWith("/BARCODE", StringComparison.Ordinal) || r.EndsWith("/BILLED_AS", StringComparison.Ordinal) ? ("leave_out", 0.92) : ("suggest", 0.9)));
            var set = await WorkItOutAsync(f);

            foreach (var t in new[] { "column:dim_item/BARCODE", "column:fact_invoice/BILLED_AS" })
            {
                var line = set.Lines.Single(l => l.Target == t);
                Assert.False(line.Kept, t + " arrived ticked");
                Assert.Contains("Jev would leave it out", line.Reason, StringComparison.Ordinal);
            }
            Assert.Contains("column:dim_item/BRAND_NOTE", Ticked(set));    // an ordinary text column is still a suggestion
            Assert.Contains(jev.AskedRefs, r => r == "column:dim_item/BARCODE");
        }
    }

    internal static class PlanCleanupReviewExtensions
    {
        /// <summary>The ids of the rows Run the plan would take on this read.</summary>
        public static string[] ReadyRows(this ModelPlanView plan) =>
            plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held).Select(i => i.Id).ToArray();
    }
}
