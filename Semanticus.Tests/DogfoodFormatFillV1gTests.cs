using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// THE FORMAT FILL RULE, round V1g (coordinator addendum 2, 29 Sep, after the V1f FAIL). One group per item, each written
    /// to fail on the head the verifier failed (6112696c) and pass on the fix:
    ///  8. Proof at write time: a Jev format line is proved again at the moment it is written, and only a line a PERSON
    ///     ticked may write an unproven format. A re-sync carries a person's tick (against the same text), never the engine's
    ///     own approval.
    ///  9. A column format the rule leaves blank is advice and never blocks a deploy.
    /// 10. Purpose, not letter, for calculation groups and structure: a format expression that passes the measure's own
    ///     format through, a measure that applies a calculation item, an integer column of the date table, a column another
    ///     column groups by or sorts by.
    /// 11. A suggestion's plain note is on the row as text, and it never carries a check mark or "sure".
    /// The verifier's own 28 cases (VerifyFormatFillRound11Tests) stay as the round's acceptance; these are the builder's.
    /// </summary>
    public sealed class DogfoodFormatFillV1gTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public DogfoodFormatFillV1gTests(ModelTemplates templates) => _templates = templates;

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "test setup", edit);

        // ---- item 10: purpose, not letter --------------------------------------------------------------------------

        private static async Task<DecisionFixture> GroupModelAsync(string itemFormat, Action<CalculationGroupTable> more = null)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Items", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            var cg = await e.CreateCalculationGroupAsync("Time Calc", "human");
            await e.CreateCalculationItemAsync(cg, "Per Day", "DIVIDE ( SELECTEDMEASURE (), 30 )", "human");
            await Mutate(f, m =>
            {
                var g = (CalculationGroupTable)m.Tables["Time Calc"];
                g.CalculationItems["Per Day"].FormatStringExpression = itemFormat;
                more?.Invoke(g);
            });
            return f;
        }

        private static Task<bool> MayFillAsync(DecisionFixture f, string table, string measure) =>
            f.Sessions.Require().ReadAsync(m => FormatFill.MayFill(m.Tables[table].Measures[measure], "#,0"));

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("SELECTEDMEASUREFORMATSTRING ()")]
        [InlineData("selectedmeasureformatstring()")]
        [InlineData("SELECTEDMEASUREFORMATSTRING ( )")]
        [InlineData("IF ( ISSELECTEDMEASURE ( [Other] ), \"0.0%\", SELECTEDMEASUREFORMATSTRING () )")]
        [InlineData("VAR f = SELECTEDMEASUREFORMATSTRING () RETURN f")]
        [InlineData("SELECTEDMEASUREFORMATSTRING () & \"\"")]
        public async Task A_format_expression_that_passes_the_measures_own_format_through_stops_every_fill(string itemFormat)
        {
            using var f = await GroupModelAsync(itemFormat);
            Assert.False(await MayFillAsync(f, "Sales", "Orders"), "the item shows its division in the count's own format, so a #,0 would round it");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("\"#,0.0\"")]
        [InlineData("\"0.0%\" // never SELECTEDMEASUREFORMATSTRING ()")]
        [InlineData("\"SELECTEDMEASUREFORMATSTRING ()\"")]
        [InlineData("/* SELECTEDMEASUREFORMATSTRING () */ \"#,0.00\"")]
        public async Task A_format_expression_that_only_names_the_function_in_a_string_or_comment_is_the_items_own(string itemFormat)
        {
            using var f = await GroupModelAsync(itemFormat);
            Assert.True(await MayFillAsync(f, "Sales", "Orders"), "control: an item with a format of its own of any spelling is not a pass through");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("NoSelection")]
        [InlineData("MultipleOrEmpty")]
        public async Task A_group_level_expression_that_passes_the_measures_format_through_stops_every_fill(string which)
        {
            using var f = await GroupModelAsync("\"#,0.0\"", g =>
            {
                if (which == "NoSelection") { g.NoSelectionExpression = "SELECTEDMEASURE ()"; g.NoSelectionFormatStringExpression = "SELECTEDMEASUREFORMATSTRING ()"; }
                else { g.MultipleOrEmptySelectionExpression = "SELECTEDMEASURE ()"; g.MultipleOrEmptySelectionFormatStringExpression = "SELECTEDMEASUREFORMATSTRING ()"; }
            });
            Assert.False(await MayFillAsync(f, "Sales", "Orders"));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("CALCULATE ( [Orders], 'Time Calc'[Name] = \"Per Day\" )", false)]
        [InlineData("CALCULATE ( [Orders], KEEPFILTERS ( 'Time Calc'[Name] = \"Per Day\" ) )", false)]
        [InlineData("CALCULATE ( COUNTROWS ( Sales ), FILTER ( ALL ( 'Time Calc' ), 'Time Calc'[Name] = \"Per Day\" ) )", false)]
        [InlineData("CALCULATE ( [Orders], Sales[Region] = \"East\" )", true)]
        [InlineData("CALCULATE ( [Orders] )", true)]
        public async Task A_measure_that_applies_a_calculation_item_is_never_a_proved_count(string dax, bool fills)
        {
            using var f = await GroupModelAsync("\"#,0.0\"");
            await f.Engine.CreateColumnAsync("table:Sales", "Region", "String", "Region", "human");
            await f.Engine.CreateMeasureAsync("table:Sales", "Under Test", dax, "human");
            // a second measure that only reads the first: applying an item is inherited through a reference
            await f.Engine.CreateMeasureAsync("table:Sales", "Reads It", "[Under Test]", "human");
            Assert.Equal(fills, await MayFillAsync(f, "Sales", "Under Test"));
            Assert.Equal(fills, await MayFillAsync(f, "Sales", "Reads It"));
            Assert.True(await MayFillAsync(f, "Sales", "Orders"), "control: the plain count is still proved while every item has a format of its own");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_integer_column_of_the_marked_date_table_is_never_a_quantity_and_a_second_table_still_is()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Dates", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", "Quantity", "human");
            await e.SetObjectPropertyAsync("column:Sales/Quantity", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateTableAsync("Dates", "human");
            await e.CreateColumnAsync("table:Dates", "Date", "DateTime", "Date", "human");
            await e.CreateColumnAsync("table:Dates", "年度", "Int64", "Fy", "human");
            await e.SetObjectPropertyAsync("column:Dates/年度", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.MarkDateTableAsync("table:Dates", "Date", "human");
            await e.CreateMeasureAsync("table:Sales", "Sum Year", "SUM ( 'Dates'[年度] )", "human");
            var (year, sumYear, qty) = await f.Sessions.Require().ReadAsync(m => (
                FormatFill.MayFill(m.Tables["Dates"].Columns["年度"], "#,0"),
                FormatFill.MayFill(m.Tables["Sales"].Measures["Sum Year"], "#,0"),
                FormatFill.MayFill(m.Tables["Sales"].Columns["Quantity"], "#,0")));
            Assert.False(year);
            Assert.False(sumYear);
            Assert.True(qty, "control: an ordinary integer column of an ordinary table is proved");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("the column a label groups by")]
        [InlineData("a column that groups by another")]
        [InlineData("a column sorted by another")]
        [InlineData("the column another sorts by")]
        public async Task A_column_on_either_side_of_a_group_by_or_a_sort_by_is_never_a_quantity(string what)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Pairs", 1702);
            await e.CreateTableAsync("Customers", "human");
            await e.CreateColumnAsync("table:Customers", "Customer", "String", "Customer", "human");
            await e.CreateColumnAsync("table:Customers", "Acct", "Int64", "Acct", "human");
            await e.SetObjectPropertyAsync("column:Customers/Acct", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateColumnAsync("table:Customers", "Loose", "Int64", "Loose", "human");
            await e.SetObjectPropertyAsync("column:Customers/Loose", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateColumnAsync("table:Customers", "Spare", "Int64", "Spare", "human");
            await e.SetObjectPropertyAsync("column:Customers/Spare", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateMeasureAsync("table:Customers", "Sum Acct", "SUM ( Customers[Acct] )", "human");
            await Mutate(f, m =>
            {
                var t = m.Tables["Customers"];
                switch (what)
                {
                    case "the column a label groups by": t.Columns["Customer"].GroupByColumns.Add(t.Columns["Acct"]); break;
                    case "a column that groups by another": t.Columns["Acct"].GroupByColumns.Add(t.Columns["Loose"]); break;
                    case "a column sorted by another": t.Columns["Acct"].SortByColumn = t.Columns["Loose"]; break;
                    default: t.Columns["Customer"].SortByColumn = t.Columns["Acct"]; break;
                }
            });
            var (acct, sum, spare) = await f.Sessions.Require().ReadAsync(m => (
                FormatFill.MayFill(m.Tables["Customers"].Columns["Acct"], "#,0"),
                FormatFill.MayFill(m.Tables["Customers"].Measures["Sum Acct"], "#,0"),
                FormatFill.MayFill(m.Tables["Customers"].Columns["Spare"], "#,0")));
            Assert.False(acct, what);
            Assert.False(sum, what + " (a SUM of it)");
            Assert.True(spare, "control: an integer column with no such sign is proved");
        }

        // ---- item 9: a column format the rule leaves blank is advice -------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_column_format_the_rule_leaves_blank_is_advice_on_every_column()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Columns", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateColumnAsync("table:Sales", "Order Date", "DateTime", "OrderDate", "human");
            await e.CreateColumnAsync("table:Sales", "Ratio", "Double", "Ratio", "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", "Quantity", "human");
            await e.SetObjectPropertyAsync("column:Sales/Quantity", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateColumnAsync("table:Sales", "Spaced", "Decimal", "Spaced", "human");
            await Mutate(f, m => m.Tables["Sales"].Columns["Spaced"].FormatString = "   ");
            var scan = await e.BpaScanAsync();
            var mine = scan.Violations.Where(v => v.RuleId == "SEM_COLUMN_NEEDS_FORMAT_STRING").ToDictionary(v => v.ObjectRef);
            foreach (var blank in new[] { "Amount", "Order Date", "Ratio", "Spaced" })
            {
                Assert.True(mine.TryGetValue("column:Sales/" + blank, out var v), blank + " is raised by the rule");
                Assert.True(v.Advice, $"{blank}: a column format the rule leaves blank is advice, not a fault to fix");
                Assert.False(v.CanAutoFix);
            }
            Assert.True(mine.TryGetValue("column:Sales/Quantity", out var q) && q.Advice,
                "addendum 3 item 14: a whole-number column is advice too, since no automatic path fills a column format, so it never blocks a deploy");
            // and the assistant is told it is advice, in the analyzer's own words
            var prompt = await McpTools.GetFixPrompt(e, "bpa", "SEM_COLUMN_NEEDS_FORMAT_STRING", "column:Sales/Amount");
            Assert.Contains("advice", prompt.Prompt, StringComparison.OrdinalIgnoreCase);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task The_gate_passes_a_blank_column_format_and_still_blocks_a_severity_two_error_with_no_fix(string door)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Gate", 1604);
            var t = await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            var total = await e.CreateMeasureAsync(t, "Total Amount", "SUM ( Sales[Amount] )", "human");
            await e.SetMeasureFormatAsync(total, "#,0.00", "human");
            await e.SetDescriptionAsync(total, "The sum of every sale amount.", "human");
            var dt = await e.CreateTableAsync("Date", "human");
            var dkey = await e.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await e.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await e.MarkDateTableAsync(dt, "Date", "human");
            async Task<DeployGate> GateAsync() => door == "mcp" ? await McpTools.DeployGate(e) : await new EngineRpcTarget(e).deployGate();
            var gate = await GateAsync();
            Assert.True(gate.Pass, "blocked: " + string.Join(" | ", gate.Blockers ?? Array.Empty<string>()));
            await Mutate(f, m => m.Tables["Sales"].AddDataColumn("Broken", null, null, DataType.Int64).IsHidden = true);
            Assert.False((await GateAsync()).Pass, "a real error must still block");
        }

        // ---- item 8: proof at write time ---------------------------------------------------------------------------

        private sealed class ByName : IDecisionOracle
        {
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                // m11/jev-batch: a table's objects arrive in one request, one question each, named "<plain>:<objectRef>".
                if (request.PerQuestion)
                    return Task.FromResult(BatchedByName.Reply(request, (plain, objectRef) =>
                        plain == "display_folder" ? "{\"type\":\"choice\",\"choice\":\"" + DecisionRegistry.NoFolder + "\",\"confidence\":0.95}"
                        : plain == "format_family" ? "{\"type\":\"choice\",\"choice\":\"whole_number\",\"confidence\":0.97}" : null));
                var parts = new List<string>();
                foreach (var q in request.Questions)
                {
                    if (q.Name == "display_folder") parts.Add("\"display_folder\":{\"type\":\"choice\",\"choice\":\"" + DecisionRegistry.NoFolder + "\",\"confidence\":0.95}");
                    else if (q.Name == "format_family") parts.Add("\"format_family\":{\"type\":\"choice\",\"choice\":\"whole_number\",\"confidence\":0.97}");
                }
                var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":");
                sb.Append(JsonSerializer.Serialize(request.Digest)).Append(",\"point\":\"").Append(request.PointName).Append('"');
                sb.Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":40},\"answers\":{").Append(string.Join(",", parts)).Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct);
            }
        }

        private static async Task BuildJevModelAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Write", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Store", "Int64", "Store", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Order Share", "DIVIDE ( [Orders], 30 )", "human");
        }

        // Lean suite: the plain model is built once for the class and each test opens a copy.
        private async Task<DecisionFixture> JevModelAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await _templates.OpenCopyAsync(f, "write", BuildJevModelAsync, "Write.bim");
            await f.ConsentAsync();
            e.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(new ByName(), new DecisionBudget()));
            return f;
        }

        private static async Task<ModelPlanView> SettledAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        private static ModelPlanChangeLine Line(ModelPlanView plan, string target, string op = "set_measure_format") =>
            plan.Items.Where(i => i.Id.StartsWith("jev.", StringComparison.Ordinal))
                .SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>())
                .FirstOrDefault(l => l.Op == op && string.Equals(l.Target, target, StringComparison.OrdinalIgnoreCase));

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("agent")]
        [InlineData("human")]
        public async Task A_suggestion_is_written_only_when_a_person_ticked_it(string who)
        {
            using var f = await JevModelAsync();
            var suggestion = Line(await SettledAsync(f), "measure:Sales/Order Share");
            Assert.True(suggestion != null && !suggestion.Kept, "precondition: the ratio's whole-number line arrives as a suggestion");
            var view = await f.Engine.SetPlanItemAsync(suggestion.PlanItemId, null, approved: true, who);
            var item = view.Items.Single(i => i.Id == suggestion.PlanItemId);
            Assert.Equal(who == "human", item.KeptByPerson);
            var report = await f.Engine.ApplyPlanAsync(new[] { suggestion.PlanItemId }, who);
            var written = await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Order Share"].FormatString ?? "");
            if (who == "human")
                Assert.Equal(suggestion.After, written);
            else
            {
                // Addendum 3 item 16: an assistant's approve records nothing (only the person ticks a suggestion), so there is nothing to apply.
                Assert.Equal("", written);
                Assert.NotEqual("approved", item.Status);
                Assert.Equal(0, report.AppliedCount);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_engine_approved_line_the_rule_no_longer_proves_is_refused_by_apply_plan_itself()
        {
            using var f = await JevModelAsync();
            var line = Line(await SettledAsync(f), "measure:Sales/Orders");
            Assert.True(line?.Kept == true && !string.IsNullOrEmpty(line.PlanItemId), "precondition: Jev's line for the plain count arrives approved");
            // The line is on the change plan, approved by the engine. Now the model stops proving it and apply_plan is called
            // directly, before any background re-sync could have replaced the line.
            await f.Engine.SetDaxAsync("measure:Sales/Orders", "DIVIDE ( COUNTROWS ( Sales ), 30 )", "human");
            var report = await f.Engine.ApplyPlanAsync(new[] { line.PlanItemId }, "agent");
            Assert.Equal("", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Orders"].FormatString ?? ""));
            Assert.Equal(0, report.AppliedCount);
        }

        [Fact]
        public async Task A_persons_keep_is_carried_only_over_the_text_they_saw_and_a_drop_always_is()
        {
            using var f = await JevModelAsync();
            var suggestion = Line(await SettledAsync(f), "measure:Sales/Order Share");
            await f.Engine.SetPlanItemAsync(suggestion.PlanItemId, null, approved: true, "human");
            await f.Engine.CreateTableAsync("Elsewhere", "human");
            Assert.True(Line(await SettledAsync(f), "measure:Sales/Order Share")?.Kept == true, "the tick survives an unrelated change");
            await Mutate(f, m => m.Tables["Sales"].Measures["Order Share"].FormatString = "   ");
            var after = Line(await SettledAsync(f), "measure:Sales/Order Share");
            Assert.False(after?.Kept == true, "the tick was for a write over nothing, not over blank spaces typed since");
            Assert.Null(after);   // addendum 3 item 18: no line is offered over typed text at all, so no tick can write over it
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task No_column_line_is_offered_over_typed_blank_spaces()
        {
            using var f = await JevModelAsync();
            await Mutate(f, m => m.Tables["Sales"].Columns["Store"].FormatString = "   ");
            Assert.Null(Line(await SettledAsync(f), "column:Sales/Store", "set_format_string"));
        }

        // ---- the words queue, with Jev on ----------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_jev_suggestion_and_the_assistants_words_are_both_kept_on_the_plan_for_the_same_measure()
        {
            using var f = await JevModelAsync();
            var plan = await SettledAsync(f);
            var line = Line(plan, "measure:Sales/Order Share");
            Assert.True(line != null && line.Suggestion, "the Jev suggestion is on the plan");
            await McpTools.ProposePlan(f.Engine, null, true, 500);
            var words = (await SettledAsync(f)).Items.Where(i => i.Doer == "assistant")
                .SelectMany(i => i.Objects ?? Array.Empty<string>()).ToArray();
            Assert.Contains("measure:Sales/Order Share", words, StringComparer.OrdinalIgnoreCase);
        }

        // ---- item 11: the note on the page ---------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_suggestion_line_carries_its_note_as_a_field_and_no_check_and_a_proved_line_keeps_it()
        {
            using var f = await JevModelAsync();
            var plan = await SettledAsync(f);
            var suggestion = Line(plan, "measure:Sales/Order Share");
            Assert.True(suggestion.Suggestion);
            Assert.Equal("Jev suggests this format; check it fits before you run it.", suggestion.Note);
            Assert.Null(suggestion.Check);
            Assert.StartsWith("Jev's reading, not proved:", suggestion.Reason ?? "");
            Assert.DoesNotContain("It counts whole things", suggestion.Reason ?? "");   // never stated as fact on a ratio

            var proved = Line(plan, "measure:Sales/Orders");
            Assert.False(proved.Suggestion);
            Assert.Null(proved.Note);
            Assert.True(proved.Check?.Exists == true);
            Assert.Equal("sure", proved.Provenance?.ConfidenceWord);
        }
    }
}
