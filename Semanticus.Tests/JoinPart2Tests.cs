// Tests the M11 join, part 2, added where two lanes met and no lane's own tests could see the meeting (each says which).
//
//   item 2a the adding rule keeps no take-back or decline phrasing the version 6 reading (5e and the any-words lead-in) had kept
//   item 2d run_draft's answer names each kept add it ran
//   item 6  starter measures never total a key or a date key, on every path that offers one (plan-harm's rule and rule-clashes'
//           declared keys, joined)
//
// Failing-first is noted on each test that has one; a check that held on the joined tree is a pin, and says so.

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
    public sealed class JoinPart2Tests
    {
        // ---- item 6: starter measures never total a key or a date key ----------------------------------------------

        /// <summary>PIN (held on the joined tree, so it had no failing run): "Autogenerate from model" offers a total for a number
        /// column of a fact table only when a total means something. DUE_DT_KEY and PAID_DT_KEY are yyyymmdd date keys, BILL_RATE is
        /// a price, and CLIENT_KEY is a relationship key: none gets a "Total". INV_AMT does, so the check is not vacuous.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Autogenerate_from_model_never_totals_a_key_a_date_key_or_a_rate()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso Consulting", 1604);
            await e.CreateTableAsync("dim_client", "human");
            await e.CreateColumnAsync("table:dim_client", "CLIENT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:dim_client", "CLNT_NM", "String", null, "human");
            await e.CreateTableAsync("fact_invoice", "human");
            await e.CreateColumnAsync("table:fact_invoice", "CLIENT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "DUE_DT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "PAID_DT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "BILL_RATE", "Double", null, "human");
            await e.CreateColumnAsync("table:fact_invoice", "INV_AMT", "Double", null, "human");
            await e.CreateRelationshipAsync("column:fact_invoice/CLIENT_KEY", "column:dim_client/CLIENT_KEY", null, true, "human");

            var view = await e.AutogenerateSpecFromModelAsync("human");
            var names = (view.Spec.Measures ?? Array.Empty<SpecMeasure>()).Select(m => m.Name).ToArray();

            Assert.Contains("Total INV_AMT", names);
            foreach (var never in new[] { "DUE_DT_KEY", "PAID_DT_KEY", "BILL_RATE", "CLIENT_KEY" })
                Assert.DoesNotContain(names, n => n.Contains(never, StringComparison.Ordinal));
        }

        // ---- item 2a: the phrasings the version 6 reading kept ----------------------------------------------------------

        public static IEnumerable<object[]> TakeBackAndDecline() => new[]
        {
            "add a role Interns. No Interns role after all.",
            "add a role Temp Access. no Temp Access role",
            "add a role Interns. no new roles after all",
            "add a role Interns. No Interns role, thanks.",
            "add a measure Temp Sales. this was a mistake",
            "add a measure Temp Sales. it was a mistake",
            "add a role Interns. those were a mistake",
            "add a measure Temp Sales. so never mind",
            "add a role Interns. they said no to the Interns role",
            "add a role Interns, which we won't need after all",
            "add a role Interns. because HR said no",
            "add a role Interns. this undoes the Interns role",
            "Rather than add a measure Margin Pct, just format Margin as a percentage",
            "Rather than create a new role, reuse Regional Managers",
            "Rather than build a new calculation group, extend Time Calculations",
            "Cancel the plan to add a role Temp Access",
            "add a role Temp Access\nforget the plan to add a Temp Access role",
            "Stop trying to add a Temp Access role",
            "It is too late to add a role Temp Access",
            "I was going to add a role Temp Access but let's not",
            "we need a role Temp Access, just kidding",
        }.Select(x => new object[] { x });

        /// <summary>The verifier's list of take-back and decline phrasings (VR19's family, v-sol-fixes-v6): 24 that version 6 newly kept
        /// through reading line 5e ("explains" openers) and "any words before the verb are a lead-in". Both are out again, so the rule
        /// alone keeps none of them: an add the words take back is never ticked. (Their red run at the merge, before the revert, is
        /// in the join report.)</summary>
        [Theory]
        [MemberData(nameof(TakeBackAndDecline))]
        public void The_rule_alone_keeps_no_take_back_or_decline_phrasing(string words)
        {
            var rule = DescribedSteps.RuleOfWords(words, new[] { "Sales" }, new[] { "Sales", "Amount", "Total Sales", "Regional Managers" });
            Assert.DoesNotContain(rule, r => r.Tier == AddingRule.Tier.Kept);
        }

        // ---- item 2d: run_draft names each kept add it ran --------------------------------------------------------------

        /// <summary>Jev, scripted: every include on a step that adds is a sure yes, the last question is a sure yes, every other
        /// include no, every listed argument "none".</summary>
        private sealed class SureJev : IDecisionOracle
        {
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PointName != DecisionRegistry.ComposeDraft)
                    return Task.FromResult(DecisionAnswers.NotDecided(request, "this test answers compose.draft only"));
                var sb = new StringBuilder("{\"entries\":[{\"digest\":").Append(JsonSerializer.Serialize(request.Digest))
                    .Append(",\"point\":").Append(JsonSerializer.Serialize(request.PointName))
                    .Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":30},\"answers\":{");
                var first = true;
                foreach (var q in request.Questions)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(q.Name)).Append(':');
                    if (q.Name.EndsWith(":wanted", StringComparison.Ordinal)) sb.Append("{\"type\":\"choice\",\"choice\":\"yes\",\"confidence\":0.95}");
                    else if (q.Kind == DecisionQuestionKind.Noul)
                    {
                        var adds = DraftMenu.All.Any(t => AddingRule.Adds(t.Op) && q.Name == JevDraftBuilder.Q(t.Id, JevDraftBuilder.Include));
                        sb.Append("{\"type\":\"noul\",\"noul\":").Append((adds ? 0.99 : 0.02).ToString(CultureInfo.InvariantCulture)).Append('}');
                    }
                    else sb.Append("{\"type\":\"choice\",\"choice\":\"none\",\"confidence\":0.95}");
                }
                sb.Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-join2").AskAsync(request, ct);
            }
        }

        /// <summary>The assistant's own run_draft counts as the person's review of a kept add, and its answer names each kept add it
        /// runs (the coordinator's ruling on VR19), in the person's own words, so nothing an assistant runs is left to be found in the
        /// model. Failed first at the merge: the answer said only "It runs by itself when every part passes its checks."</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Run_draft_names_each_kept_add_it_runs()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var file = "J2Run" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bim";
            await f.BuildModelAsync(file);
            await f.SaveAsAsync(file);
            await f.ConsentAsync();
            f.Engine.UseDecisionsForTest(new DecisionService(new SureJev(), new DecisionBudget()));
            var v = await McpTools.ComposeTask(f.Engine, words: "add a role for managers");
            Assert.Contains(v.Steps, s => s.Kept && s.Id.StartsWith("role", StringComparison.Ordinal));   // not vacuous: the add is kept
            foreach (var q in v.Questions.Where(q => q.Answer == null && !q.Name.EndsWith("-expected", StringComparison.Ordinal) && !q.Name.EndsWith("-measure", StringComparison.Ordinal)))
            {
                var answer = q.Options?.FirstOrDefault()?.Ref ?? q.Values.FirstOrDefault();
                if (answer != null) v = await McpTools.EditDraft(f.Engine, v.DraftId, "answer", question: q.Name, text: answer);
            }

            var ran = await McpTools.RunDraft(f.Engine, v.DraftId);

            Assert.Contains("Add a role", ran.Sentence ?? "", StringComparison.Ordinal);
            Assert.Contains("add a role for managers", ran.Sentence ?? "", StringComparison.Ordinal);
        }

        // ---- item 8: the starter rows run in one press -----------------------------------------------------------------

        /// <summary>Five fact tables, each with an amount and a key to one client table. Every fact's starter row writes the objects
        /// facet, and each used to READ it too, so each waited for every starter row before it and Run the plan added one table's measures
        /// per press (21 presses on the 42 tables of a real model). Now none of them waits for another, and ONE press adds every fact's
        /// total. Failed first at the merge: the second and later rows waited for the row before.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_starter_rows_of_many_fact_tables_run_in_one_press()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Many Facts", 1604);
            await e.CreateTableAsync("dim_client", "human");
            await e.CreateColumnAsync("table:dim_client", "CLIENT_KEY", "Int64", null, "human");
            await e.CreateColumnAsync("table:dim_client", "CLNT_NM", "String", null, "human");
            var facts = new[] { "fact_a", "fact_b", "fact_c", "fact_d", "fact_e" };
            foreach (var t in facts)
            {
                await e.CreateTableAsync(t, "human");
                await e.CreateColumnAsync("table:" + t, "CLIENT_KEY", "Int64", null, "human");
                await e.CreateColumnAsync("table:" + t, t + "_AMT", "Double", null, "human");
                await e.CreateRelationshipAsync("column:" + t + "/CLIENT_KEY", "column:dim_client/CLIENT_KEY", null, true, "human");
            }
            await f.SaveAsAsync("J2Many.bim");
            f.UseNull();

            var plan = await e.GetModelPlanAsync();
            var starters = plan.Items.Where(i => i.Id.StartsWith("starter/", StringComparison.Ordinal)).ToArray();
            Assert.Equal(facts.Length, starters.Length);   // not vacuous: one row per fact table
            // No starter row waits for another one (a row may still wait for a different kind of row, such as the Formatting safe fixes
            // that set a key column's summary, which the starters read).
            Assert.All(starters, r => Assert.DoesNotContain(r.WaitsFor ?? Array.Empty<PlanWaitRef>(), w => w.Id.StartsWith("starter/", StringComparison.Ordinal)));

            // Run the plan until nothing is ready. Every fact's total arrives in the SAME press: never one table per press.
            var totalsAfterEachPress = new List<int>();
            for (var press = 0; press < 6; press++)
            {
                plan = await e.GetModelPlanAsync();
                if (!plan.Items.Any(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held)) break;
                var ran = await e.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                if (!ran.Performed) break;
                var made = await f.Sessions.Require().ReadAsync(m => m.AllMeasures.Select(x => x.Name).ToArray());
                totalsAfterEachPress.Add(facts.Count(t => made.Contains("Total " + t + "_AMT")));
            }
            Assert.Contains(facts.Length, totalsAfterEachPress);                                  // not vacuous: they all came
            Assert.DoesNotContain(totalsAfterEachPress, n => n > 0 && n < facts.Length);         // and never a few at a time
        }
    }
}
