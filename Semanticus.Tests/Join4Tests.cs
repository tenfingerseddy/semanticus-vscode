// M11 join part 4 (1 Oct): the fixes the full check and Sol's delta left for the join. Each test was written to fail first.
//   D1  Jev's column-summarize row reads the stored acceptances like the column-format row does (summarize, visibility, Excel).
//   E1  Approve on a model that was built but never saved writes, and says so: written, undoable, not saved yet.
//   E3  A copy of a bundled rule embedded in the model (same id and expression) is the bundled rule; a truly custom rule keeps its gate.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class Join4Tests
    {
        // ---- D1: the summarize family's other lines read the stored acceptances -------------------------------------

        private static ColumnSummarizeFamily.Ask AskFor(string column, bool hidden = false) => new ColumnSummarizeFamily.Ask
        {
            Ref = "column:Sales/" + column, ColumnName = column, TableRef = "table:Sales", TableName = "Sales", Current = "Default",
            Role = "none", IsHidden = hidden, MdxCanGoOff = true,
            NameOf = StarterMeasureTemplates.MeasureOptions.ToDictionary(o => o, o => StarterMeasureTemplates.Name(o, column)),
        };

        [Fact]
        public void An_accepted_visibility_leaves_the_hide_line_and_its_excel_line_out_but_keeps_the_measure()
        {
            var ask = AskFor("Amount");
            ask.KeepsVisibility = true;
            var lines = new List<ChangeItem>();
            ColumnSummarizeFamily.Map(ask, "sum", 0.97, 0.85, lines, new List<ModelPlanChangeRefusal>());

            Assert.Contains(lines, l => l.Kind == "create_measure");
            Assert.Contains(lines, l => l.Kind == "set_summarize_by");
            Assert.DoesNotContain(lines, l => l.Kind == "set_column_hidden");
            Assert.DoesNotContain(lines, l => l.Kind == "set_is_available_in_mdx");   // it needs the hide line, which is not there
        }

        [Fact]
        public void An_accepted_summarize_leaves_its_line_out_and_an_accepted_excel_setting_leaves_the_excel_line_out()
        {
            var ask = AskFor("Amount");
            ask.KeepsSummarize = true;
            ask.KeepsExcel = true;
            var lines = new List<ChangeItem>();
            ColumnSummarizeFamily.Map(ask, "sum", 0.97, 0.85, lines, new List<ModelPlanChangeRefusal>());

            Assert.DoesNotContain(lines, l => l.Kind == "set_summarize_by");
            Assert.DoesNotContain(lines, l => l.Kind == "set_is_available_in_mdx");
            Assert.Contains(lines, l => l.Kind == "create_measure");
            Assert.Contains(lines, l => l.Kind == "set_column_hidden");
        }

        [Fact]
        public async Task Prepare_reads_a_stored_visibility_acceptance_into_the_ask()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await f.SaveAsAsync("Join4Vis.bim");
            var kept = await e.WaiveFindingAsync("air", "VIS-FK", "column:Sales/Amount", "It stays in view on purpose.", "human");
            Assert.True(kept.Changed, "the acceptance did not take");

            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null));

            var amount = prepared.Asks.Single(a => a.ColumnName == "Amount");
            var quantity = prepared.Asks.Single(a => a.ColumnName == "Quantity");
            Assert.True(amount.KeepsVisibility, "a stored acceptance that keeps visibility was not read");
            Assert.False(quantity.KeepsVisibility);
        }

        // ---- E1: approve on a model with nowhere to keep the record ---------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Approve_on_a_model_never_saved_says_it_was_written_undoable_and_not_saved_yet()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            f.UseNull();   // never saved: no file, so the plan has nowhere to keep a record

            // The assistant puts the set on the plan (its lines live on the change plan, so no record is needed to hold it).
            await e.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, "agent");
            var row = (await e.GetModelPlanAsync()).Items.First(i => i.Id.StartsWith("safefix/", StringComparison.Ordinal));
            var set = row.ChangeSet;
            Assert.NotNull(set);
            Assert.NotEmpty(set.Lines);
            var before = (await e.GetModelPlanAsync()).Revision;

            var result = await e.ModelPlanActionAsync(row.Id, "approve", "human", set.Digest);

            Assert.True(result.Performed, "the changes were written, so the answer is not a failure: " + result.Sentence);
            Assert.NotEqual(before, result.Revision);
            Assert.Contains("not saved", result.Sentence, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("undo", result.Sentence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("failed", result.Sentence, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nowhere", result.Sentence, StringComparison.OrdinalIgnoreCase);
            await e.UndoAsync("human");   // and it really is one undo step
            Assert.Equal("false", (await e.GetObjectAsync("column:Sales/CustomerKey")).Properties["isHidden"]?.ToString()?.ToLowerInvariant());
        }

        // ---- E3: an embedded copy of a bundled rule is the bundled rule ------------------------------------------------

        private const string BundledId = "PROVIDE_FORMAT_STRING_FOR_MEASURES";

        private static async Task<(DecisionFixture F, IEngine E)> UnformattedMeasureModelAsync(string name)
        {
            var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync(name, 1604);
            var t = await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync(t, "Amount", "Double", "Amount", "human");
            var dt = await e.CreateTableAsync("Date", "human");
            var dkey = await e.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await e.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await e.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await e.MarkDateTableAsync(dt, "Date", "human");
            var total = await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
            await e.SetObjectPropertyAsync(total, "Description", "Everything sold, added up.", "human");   // only its missing format is left to find
            return (f, e);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_embedded_copy_of_the_bundled_measure_format_rule_is_advice_the_gate_skips()
        {
            var (f, e) = await UnformattedMeasureModelAsync("Join4E3");
            using (f)
            {
                Assert.True((await McpTools.DeployGate(e)).Pass, "the baseline model with one unformatted measure already fails the gate: " + (await McpTools.DeployGate(e)).CheckLine);
                var bundled = BpaRuleSet.Standard().Single(r => string.Equals(r.ID, BundledId, StringComparison.OrdinalIgnoreCase));
                Assert.True(bundled.Severity >= 3, "the premise: the bundled rule is severity 3");
                await e.LoadBpaRulesAsync(JsonSerializer.Serialize(new[] { bundled }), replace: false, "human");

                var gate = await McpTools.DeployGate(e);
                var hit = (await e.BpaScanAsync()).Violations.First(v => v.RuleId == BundledId);

                Assert.True(hit.Advice);
                Assert.False(hit.Custom, "a copy of the bundled rule is not the model's own rule");
                Assert.True(hit.IsAdviceTheGateSkips());
                Assert.True(gate.Pass, "the embedded standard set blocked the gate on an unformatted measure: " + string.Join(" | ", gate.Blockers) + " / " + gate.CheckLine);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_rule_under_the_bundled_id_with_a_different_expression_is_the_models_own_and_keeps_its_gate()
        {
            var (f, e) = await UnformattedMeasureModelAsync("Join4E3b");
            using (f)
            {
                await e.LoadBpaRulesAsync("[{\"ID\":\"" + BundledId + "\",\"Name\":\"Own format rule\",\"Category\":\"Formatting\",\"Severity\":3,\"Scope\":\"Measure\","
                    + "\"Expression\":\"string.IsNullOrWhitespace(FormatString) and not Name.Contains(\\\"zzz\\\")\",\"FixExpression\":\"FormatString = \\\"#,0\\\"\"}]", replace: false, "human");

                var hit = (await e.BpaScanAsync()).Violations.First(v => v.RuleId == BundledId);

                Assert.True(hit.Custom, "a different expression is the model's own rule");
                Assert.False(hit.IsAdviceTheGateSkips(), "a rule the model truly wrote at severity 3 keeps its gate");
            }
        }

        // ---- K1: no session ceiling ---------------------------------------------------------------------------------

        [Fact]
        public void The_apps_budget_has_no_ceiling_and_never_refuses_on_a_session_total()
        {
            var budget = new DecisionBudget();
            Assert.Null(budget.CapInputTokens);
            Assert.Null(budget.RemainingInputTokens);
            for (var i = 0; i < 50; i++)
            {
                Assert.True(budget.TryReserve(400_000, out var spend), "request " + i + " was refused on a session total");
                spend.Settle(400_000);
            }
            Assert.False(budget.Exhausted);
            Assert.Equal(20_000_000L, budget.UsedInputTokens);   // still counted, to the token
            Assert.Equal(50, budget.Requests);
        }

        [Fact]
        public void The_budget_still_holds_pessimistically_and_counts_what_a_failed_attempt_may_have_cost()
        {
            var budget = new DecisionBudget();
            Assert.True(budget.TryReserve(1_000, out var failed));
            Assert.True(budget.TryReserve(2_000, out var held));
            Assert.Equal(3_000, budget.CommittedInputTokens);          // holds are counted while in flight
            failed.SettleUnknown();                                     // a timeout may have been billed: the estimate stands
            Assert.Equal(1_000, budget.UsedInputTokens);
            held.Refund();                                              // never reached a service: given back
            Assert.Equal(1_000, budget.CommittedInputTokens);
        }

        [Fact]
        public void An_explicit_ceiling_still_fails_closed_for_a_test_or_a_tool_that_asks_for_one()
        {
            var budget = new DecisionBudget(1_000);
            Assert.True(budget.TryReserve(800, out var first));
            Assert.False(budget.TryReserve(800, out _), "a second hold past the ceiling must be refused");
            first.Settle(900);
            Assert.False(budget.TryReserve(200, out _));
        }

        [Fact]
        public void Money_is_tokens_times_Jevs_price_and_output_is_free()
        {
            var budget = new DecisionBudget();
            budget.Record(1_000_000, 500_000);   // output tokens do not move the money
            Assert.Equal(0.042, budget.UsedUsd, 6);
            Assert.Equal(1.9992, JevPrice.UsdFor(47_600_000), 4);   // the relay's USD 2 a month
            Assert.Contains("USD 0.042 per million input tokens", JevPrice.PriceWords, StringComparison.Ordinal);
        }

        [Fact]
        public async Task The_budget_read_says_there_is_no_session_cap_and_reports_the_money()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var info = await f.Engine.GetDecisionBudgetAsync();

            Assert.Null(info.CapInputTokens);
            Assert.Null(info.RemainingInputTokens);
            Assert.False(info.Exhausted);
            Assert.Equal(0, info.UsedUsd);
            Assert.Contains("USD 0.042 per million input tokens", info.PriceNote, StringComparison.Ordinal);
        }

        // ---- E2: a row worked out again must not stay stale ----------------------------------------------------------

        private static string Fixture(IEnumerable<(string Digest, string Choice, double Confidence)> rows)
        {
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var (digest, choice, confidence) in rows)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(digest))
                  .Append(",\"point\":\"column.summarize\",\"usage\":{\"input_tokens\":500,\"output_tokens\":20}")
                  .Append(",\"answers\":{\"summarize_by\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                  .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append("}}}");
            }
            return sb.Append("]}").ToString();
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_row_worked_out_again_after_Run_the_plan_is_not_stale_and_the_next_press_takes_it()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
            await f.SaveAsAsync("Join4Stale.bim");
            await f.ConsentAsync();
            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null));
            var d = prepared.Asks.ToDictionary(a => a.TableName + "/" + a.ColumnName, a => a.Ref, StringComparer.Ordinal);
            var rows = new List<(string, string, double)> { (d["Sales/Amount"], "sum", 0.95), (d["Sales/CustomerKey"], "none", 0.97), (d["Customer/CustomerKey"], "none", 0.97) };
            f.Engine.UseDecisionsForTest(new DecisionService(new ByObjectRecordedOracle(Fixture(rows), "jev-test-1"), new DecisionBudget()));

            var trail = new StringBuilder();
            await e.GetModelPlanAsync();
            await e.JevSettledForTest();
            var plan = await e.GetModelPlanAsync();
            var ran = await e.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            trail.AppendLine("first press: " + ran.Performed + " " + ran.Sentence);

            // What a person does when a press says a row was worked out before the model last changed: read the plan, work it out again.
            for (var turn = 0; turn < 4; turn++)
            {
                await e.GetModelPlanAsync();
                await e.JevSettledForTest();
                plan = await e.GetModelPlanAsync();
                var ready = plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held).ToList();
                if (ready.Count == 0) break;
                foreach (var r in ready.Where(r => r.ChangeSet?.Stale == true))
                {
                    await e.ModelPlanActionAsync(r.Id, "cancel", "human", null);
                    var again = await e.ModelPlanActionAsync(r.Id, "propose", "human", null);
                    await e.JevSettledForTest();
                    var now = (await e.GetModelPlanAsync()).Items.FirstOrDefault(i => i.Id == r.Id);
                    trail.AppendLine("turn " + turn + " rework " + r.Id + ": stale=" + now?.ChangeSet?.Stale + " why=" + now?.ChangeSet?.StaleReason + " rev=" + f.Sessions.Require().Revision);
                    Assert.True(now?.ChangeSet?.Stale != true, "a row worked out again and settled still reads stale:\n" + trail);
                }
                plan = await e.GetModelPlanAsync();
                ran = await e.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
                trail.AppendLine("turn " + turn + " press: " + ran.Performed + " " + ran.Sentence);
                Assert.DoesNotContain("changed after these changes were worked out", ran.Sentence ?? "", StringComparison.Ordinal);
            }
        }

        /// <summary>R1 fix-plan (Kane, 8 Oct): ONE press runs every Semanticus row to the end. A Jev row is stale the moment the first
        /// batch moves the model, and Jev looks again; the press waits for that and runs the row, so a second press is never needed.</summary>
        [Fact]
        public async Task One_press_runs_every_Semanticus_row_even_when_Jev_looks_again_after_the_first_batch()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
            await f.SaveAsAsync("R1OnePress.bim");
            await f.ConsentAsync();
            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null));
            var d = prepared.Asks.ToDictionary(a => a.TableName + "/" + a.ColumnName, a => a.Ref, StringComparer.Ordinal);
            var rows = new List<(string, string, double)> { (d["Sales/Amount"], "sum", 0.95), (d["Sales/CustomerKey"], "none", 0.97), (d["Customer/CustomerKey"], "none", 0.97) };
            f.Engine.UseDecisionsForTest(new DecisionService(new ByObjectRecordedOracle(Fixture(rows), "jev-test-1"), new DecisionBudget()));

            await e.GetModelPlanAsync();
            await e.JevSettledForTest();
            var plan = await e.GetModelPlanAsync();
            var ran = await e.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);

            Assert.True(ran.Performed, ran.Sentence);
            await e.JevSettledForTest();
            var after = await e.GetModelPlanAsync();
            var left = after.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held).Select(i => i.Id + " stale=" + i.ChangeSet?.Stale).ToArray();
            Assert.True(left.Length == 0, "one press left Semanticus rows ready: " + string.Join("; ", left) + " :: " + ran.Sentence);
            Assert.DoesNotContain("Jev is looking again", ran.Sentence ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("Nothing was changed", ran.Sentence ?? "", StringComparison.Ordinal);
        }

        [Fact]
        public void A_press_stopped_on_a_Jev_row_that_is_out_of_date_does_not_send_the_person_to_cancel_it()
        {
            var jev = LocalEngine.StaleRowSentence(new ModelPlanItem { Id = "jev.column-summarize.table:Sales" });
            var own = LocalEngine.StaleRowSentence(new ModelPlanItem { Id = "safefix/Visibility" });

            Assert.DoesNotContain("Run the plan once more", jev, StringComparison.Ordinal);
            Assert.Contains("Open this task on the Plan", jev, StringComparison.Ordinal);
            Assert.DoesNotContain("Work them out again", jev, StringComparison.Ordinal);
            Assert.Contains("Open the task again to get new changes, then press Run the plan", own, StringComparison.Ordinal);
        }
    }
}
