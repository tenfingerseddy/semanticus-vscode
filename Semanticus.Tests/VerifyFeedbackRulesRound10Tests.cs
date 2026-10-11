using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier findings on m11/feedback-rules at 1ffc6fd7 (V1e, the full re-verify of THE FORMAT FILL RULE). The rule:
    /// an automatic format fill is allowed only where the engine can PROVE what kind of number the object holds, and no
    /// value a person reads may change after any automatic fix. The lane's one check, FormatFill.MayFill, is sound on the
    /// shape of a formula: every ratio, average, money or decimal sum, mixed IF or SWITCH and unclassifiable formula the
    /// verifier wrote is refused on every door and both tiers. These cases are where the proof still says "whole number"
    /// while what a person reads is not one, or is not a quantity: a calculation item that shows the count through its
    /// own arithmetic, a comment the DAX engine ends where the scanner does not, and year and ID columns the name list
    /// does not know. They fail at that head and name what must be true without choosing the fix.
    /// </summary>
    public sealed class VerifyFeedbackRulesRound10Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyFeedbackRulesRound10Tests(ModelTemplates templates) => _templates = templates;

        // Lean suite: the one-count model of the default-tier witness is built once and opened as a copy.
        private static async Task BuildOrdersCountAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
        }

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "test setup", edit);

        /// <summary>Press the rule's fix on both doors (a refusal is an acceptable answer), then Fix all on the Studio door,
        /// apply_safe_fixes on the MCP door, and every fix the change plan seeds, on both doors.</summary>
        private static async Task PressEverythingAsync(DecisionFixture f, string ruleId, string objRef)
        {
            var rpc = new EngineRpcTarget(f.Engine);
            try { await McpTools.ApplyFix(f.Engine, "bpa", ruleId, objRef); } catch (InvalidOperationException) { }
            try { await rpc.bpaFix(ruleId, objRef); } catch (InvalidOperationException) { }
            await rpc.bpaFixAll();
            await McpTools.ApplySafeFixes(f.Engine, "bpa");
            var plan = await McpTools.ProposePlan(f.Engine, null, false, 0);
            var fixes = plan.Items.Where(i => i.Kind == "bpa_fix").Select(i => i.Id).ToArray();
            if (fixes.Length > 0) await McpTools.ApplyPlan(f.Engine, fixes);
            var studioPlan = await rpc.proposePlan(null, false, 0);
            var studioFixes = studioPlan.Items.Where(i => i.Kind == "bpa_fix").Select(i => i.Id).ToArray();
            if (studioFixes.Length > 0) await rpc.applyPlan(studioFixes);
        }

        private static async Task<string[]> OfferedAsync(DecisionFixture f, string objRef) =>
            (await f.Engine.BpaScanAsync()).Violations
                .Where(v => v.ObjectRef == objRef && v.CanAutoFix && v.RuleId.EndsWith("_FORMATTING", StringComparison.Ordinal))
                .Select(v => v.RuleId).ToArray();

        // 1. A COUNT SHOWN THROUGH A CALCULATION ITEM. A calculation item with no format string expression of its own shows
        // its result in the format of the measure it is applied to ("Calculation items use the underlying measure
        // formatting by default", Microsoft Learn, Create calculation groups). So once a calculation group divides, averages
        // or shares out the measure, the count's format is what a person reads the division in. FormatFill proves the
        // measure's own formula is a count and fills #,0, and every value the group's "Per Day" item shows then loses its
        // fraction: 375 orders over 30 days read 12.5 before the fill and 13 after it. A calculation group's own "no
        // selection" expression is the same case with no click at all: it applies whenever the group is not filtered, and
        // with no format expression of its own it too shows its result in the measure's format. What is shown is not
        // proved to be a whole number, so the rule allows no fill here.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("a calculation item")]
        [InlineData("the group's no-selection expression")]
        public async Task Verify_a_count_shown_through_a_calculation_group_without_its_own_format_gets_no_automatic_fill(string through)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            var cg = await e.CreateCalculationGroupAsync("Views", "human");
            await e.CreateCalculationItemAsync(cg, "Current", "SELECTEDMEASURE ()", "human");
            if (through == "a calculation item")
                await e.CreateCalculationItemAsync(cg, "Per Day", "DIVIDE ( SELECTEDMEASURE (), 30 )", "human");
            else
                await Mutate(f, m => ((CalculationGroupTable)m.Tables["Views"]).NoSelectionExpression = "DIVIDE ( SELECTEDMEASURE (), 30 )");
            const string Target = "measure:Sales/Orders";

            var offered = await OfferedAsync(f, Target);
            Assert.True(offered.Length == 0,
                $"the count Orders is offered an automatic #,0 fill by {string.Join(", ", offered)}, but {through} has no format of its "
                + "own and shows Orders divided by 30 in that format, so 12.5 would read 13");

            await PressEverythingAsync(f, "INTEGER_FORMATTING", Target);
            var written = await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Orders"].FormatString);
            Assert.True(string.IsNullOrEmpty(written),
                $"Orders was given the format \"{written}\" automatically, which is the format {through} shows its division in");
        }

        // 2. A LINE COMMENT THE DAX ENGINE ENDS SOONER. The proof reads DaxScan tokens, and DaxScan ends a // or -- comment
        // only at a line feed. The DAX lexer ends it at any line break: a carriage return, and the Unicode next-line, line
        // and paragraph separators too (the vendored parity reference, external/TabularEditor/AntlrGrammars/DAXLexer.g4,
        // SINGLE_LINE_COMMENT is InputCharacter*, which excludes \r \n \u0085 \u2028 \u2029). So a formula whose line
        // break is one of those reads to the engine as COUNTROWS ( Sales ) / 30, a ratio, and to the proof as a bare count,
        // and the fill writes #,0 over what is a per-day rate.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("carriage return", "\r")]
        [InlineData("line separator", "\u2028")]
        public async Task Verify_a_line_comment_that_ends_at_a_line_break_other_than_a_line_feed_does_not_hide_a_division(string what, string lineBreak)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders Per Day", "1", "human");
            // Written through the model: the formula is what a file saved with that line break carries.
            await Mutate(f, m => m.Tables["Sales"].Measures["Orders Per Day"].Expression = "COUNTROWS ( Sales ) // per day" + lineBreak + "/ 30");
            const string Target = "measure:Sales/Orders Per Day";

            var offered = await OfferedAsync(f, Target);
            Assert.True(offered.Length == 0,
                $"COUNTROWS ( Sales ) / 30, written with a comment that ends at a {what}, is offered an automatic #,0 fill by "
                + $"{string.Join(", ", offered)}: the proof reads the division as part of the comment, and DAX does not");

            await PressEverythingAsync(f, "INTEGER_FORMATTING", Target);
            var written = await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Orders Per Day"].FormatString);
            Assert.True(string.IsNullOrEmpty(written), $"COUNTROWS ( Sales ) / 30 was given the format \"{written}\" automatically");
        }

        // 3. YEAR AND ID COLUMNS THE NAME LIST DOES NOT KNOW. A SUM, MIN or MAX of an Int64 column is proved a whole number
        // only when the column passes FormatFill.IsFillableWholeNumberColumn, and that test is a list of name words and four
        // upper-case suffixes. The lane's own comment says why it matters: #,0 on a year or an ID changes what a person
        // reads (2024 reads 2,024). The list misses the financial year as Australian and British models name it (FY), a raw
        // CSV header in capitals with no separator (FISCALYEAR), a lakehouse column in lower case with no separator
        // (ordernumber), and an ID written with a number sign (Invoice #). Each is the column of a common "latest" measure.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("FY")]
        [InlineData("FISCALYEAR")]
        [InlineData("ordernumber")]
        [InlineData("Invoice #")]
        public async Task Verify_no_automatic_fill_on_the_max_of_a_year_or_an_id_column(string column)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", column, "Int64", "c1", "human");
            await e.CreateMeasureAsync("table:Sales", "Latest", "MAX ( Sales[" + column + "] )", "human");
            const string Target = "measure:Sales/Latest";

            var offered = await OfferedAsync(f, Target);
            Assert.True(offered.Length == 0,
                $"MAX ( Sales[{column}] ) is offered an automatic #,0 fill by {string.Join(", ", offered)}: {column} is a year or an ID, and 2024 would read 2,024");

            await PressEverythingAsync(f, "INTEGER_FORMATTING", Target);
            var written = await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Latest"].FormatString);
            Assert.True(string.IsNullOrEmpty(written), $"MAX ( Sales[{column}] ) was given the format \"{written}\" automatically");
        }

        // 4. THE WORDS ON A PLANNED FILL THAT NO LONGER HOLDS. A plan seeded when a measure was a count asks the rule again
        // when it applies (BpaFixNoLongerApplies), and since the fill rule a fix can stop applying because the measure is no
        // longer proved a whole number: here the person made it a rate after the plan was built. The skip is right. The
        // note it carries is not: "a fix never writes over a format string that is already there", on a measure whose
        // format is still blank. The note is what the person and the assistant read about why their approved line did
        // nothing.

        [Fact]
        public async Task Verify_a_planned_fill_skipped_because_the_measure_is_no_longer_a_count_says_why()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await _templates.OpenCopyAsync(f, "orders-count", BuildOrdersCountAsync, "Northstar.bim");
            var plan = await e.ProposePlanAsync(null, includeAi: false, maxAiItems: 0, "human");
            var item = Assert.Single(plan.Items, i => i.Kind == "bpa_fix" && i.RuleId == "INTEGER_FORMATTING" && i.ObjectRef == "measure:Sales/Orders");

            await e.SetDaxAsync("measure:Sales/Orders", "DIVIDE ( COUNTROWS ( Sales ), 30 )", "human");
            var report = await e.ApplyPlanAsync(new[] { item.Id }, "human");

            var done = report.Items.Single(i => i.Id == item.Id);
            Assert.Equal("skipped", done.Status);
            Assert.Equal("", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Orders"].FormatString ?? ""));
            Assert.False(string.IsNullOrWhiteSpace(done.Note), "the skipped fill carries no note saying why it was not applied");
            Assert.False(done.Note.Contains("already there", StringComparison.Ordinal),
                "the skipped fill says the measure's format is already there, but its format is blank; it was skipped because "
                + "the measure is no longer proved a whole number: " + done.Note);
        }

        // 5. RUN THE PLAN AND THE JEV MEASURES FAMILY (Pro). The rule names Run the plan as an automatic path and says a
        // format it cannot prove "goes into the Give the words to your assistant queue ... never into Run the plan". The
        // measures family asks Jev for a format family for every measure with no format (MeasureDefaultsPoint.WantsProposal
        // now uses FormatFill.HasNoFormat, but not MayFill), turns the answer into a "Format as ..." line, and its row is a
        // Semanticus row (PlanTasks.DoerOf: "auto, and jev"), so Run the plan applies it with no look at the line. Here Jev
        // takes a SUM of money and a ratio for whole numbers, sure: the misjudgement the rule exists to keep out of the
        // model. Answers are recorded (no network), the way JevFamilyTests drive the family.

        // m11/jev-batch: an entry is keyed by the object ref (played with ByObjectRecordedOracle), not by a request digest, since a table is one request now.
        private static string MeasuresFixture(params (string objectRef, string family)[] rows)
        {
            var entries = rows.Where(r => r.objectRef != null).Select(r =>
                "{\"digest\":" + System.Text.Json.JsonSerializer.Serialize(r.objectRef)
                + ",\"point\":\"" + Semanticus.Decisions.DecisionRegistry.MeasureFolderAndFormat + "\""
                + ",\"usage\":{\"input_tokens\":900,\"output_tokens\":40}"
                + ",\"answers\":{\"display_folder\":{\"type\":\"choice\",\"choice\":\"" + Semanticus.Decisions.DecisionRegistry.NoFolder + "\",\"confidence\":0.95}"
                + ",\"format_family\":{\"type\":\"choice\",\"choice\":\"" + r.family + "\",\"confidence\":0.97}}}");
            return "{\"servedModelVersion\":\"jev-test-1\",\"entries\":[" + string.Join(",", entries) + "]}";
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_run_the_plan_writes_no_format_the_engine_cannot_prove()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Order Count", "COUNTROWS ( Sales )", "human", "Volume");
            await e.CreateMeasureAsync("table:Sales", "Revenue", "SUM ( Sales[Amount] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Share", "DIVIDE ( [Revenue], 3 )", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            await Mutate(f, m => m.Tables["Sales"].Measures["Order Count"].FormatString = "#,##0");
            await f.SaveAsAsync("Northstar.bim");
            f.UseNull();
            await f.ConsentAsync();
            f.UseRecordedByObject(MeasuresFixture(("measure:Sales/Revenue", "whole_number"), ("measure:Sales/Share", "whole_number"), ("measure:Sales/Orders", "whole_number")));

            await e.GetModelPlanAsync();
            await e.JevSettledForTest();
            var plan = await e.GetModelPlanAsync();
            var row = plan.Items.FirstOrDefault(i => i.Id == "jev.measures.table:Sales");
            var run = await e.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);

            var (rev, sh, cnt) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Sales"].Measures["Revenue"].FormatString ?? "", m.Tables["Sales"].Measures["Share"].FormatString ?? "",
                 m.Tables["Sales"].Measures["Orders"].FormatString ?? ""));
            Assert.True(rev.Length == 0 && sh.Length == 0,
                $"Run the plan wrote the format Jev chose onto measures the engine cannot prove are whole numbers: Revenue, a SUM of money, "
                + $"reads \"{rev}\" and Share, a ratio, reads \"{sh}\" (row {row?.Id} doer {row?.Doer} state {row?.StateWord}; {run.Sentence})");
            // The control: the family ran and Run the plan took its row, so the one line the rule allows (a count) is in.
            Assert.True(cnt.Length > 0, $"the count Orders, the one line the rule allows, was not filled: the family did not run ({run.Sentence})");
        }

        // 6. THE SAME ON THE PLAN'S COLUMN FORMAT FAMILY (Pro). ColumnFormatFamily turns Jev's format family for a visible
        // number or date column into a set_format_string line on a Jev row, which Run the plan applies the same way. Here
        // Jev takes a Double rate for a whole number: 0.73 would read 1. The Int64 quantity beside it is the control.

        private sealed class ColumnFormatOracle : Semanticus.Decisions.IDecisionOracle
        {
            public bool Available => true;
            public Task<Semanticus.Decisions.DecisionAnswers> AskAsync(Semanticus.Decisions.DecisionRequest request, System.Threading.CancellationToken ct)
            {
                if (request.PointName != Semanticus.Decisions.DecisionRegistry.ColumnFormat)
                    return Task.FromResult(Semanticus.Decisions.DecisionAnswers.NotDecided(request, "not asked in this case"));
                // m11/jev-batch: a table's columns arrive in one request, one question each, named "<plain>:<objectRef>".
                if (request.PerQuestion)
                    return Task.FromResult(BatchedByName.Reply(request, (plain, objectRef) => plain == "format_family"
                        ? "{\"type\":\"choice\",\"choice\":\"whole_number\",\"confidence\":0.97}" : null));
                var fixture = "{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":" + System.Text.Json.JsonSerializer.Serialize(request.Digest)
                    + ",\"point\":\"" + request.PointName + "\",\"usage\":{\"input_tokens\":900,\"output_tokens\":40}"
                    + ",\"answers\":{\"format_family\":{\"type\":\"choice\",\"choice\":\"whole_number\",\"confidence\":0.97}}}]}";
                return new Semanticus.Decisions.RecordedDecisionOracle(fixture, "jev-test-1").AskAsync(request, ct);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_run_the_plan_writes_no_column_format_the_engine_cannot_prove()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Rate", "Double", "Rate", "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", "Quantity", "human");
            await e.SetObjectPropertyAsync("column:Sales/Quantity", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await f.SaveAsAsync("Northstar.bim");
            await f.ConsentAsync();
            e.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(new ColumnFormatOracle(), new Semanticus.Decisions.DecisionBudget()));

            await e.GetModelPlanAsync();
            await e.JevSettledForTest();
            var plan = await e.GetModelPlanAsync();
            var row = plan.Items.FirstOrDefault(i => i.Id.StartsWith("jev.column-format.", StringComparison.Ordinal));
            var run = await e.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);

            var (rate, qty) = await f.Sessions.Require().ReadAsync(m =>
                (m.Tables["Sales"].Columns["Rate"].FormatString ?? "", m.Tables["Sales"].Columns["Quantity"].FormatString ?? ""));
            Assert.True(rate.Length == 0,
                $"Run the plan wrote the format Jev chose onto the Double column Rate: it reads \"{rate}\", so 0.73 shows as 1 "
                + $"(row {row?.Id} doer {row?.Doer} state {row?.StateWord}; {run.Sentence})");
            Assert.True(qty.Length > 0, $"the Int64 quantity, the one column the rule allows, was not filled: the family did not run ({run.Sentence})");
        }

        // 7. THE COLUMN HALF, THROUGH A MODEL'S OWN RULE. The rule's own words for a column: an Int64 column that is not a
        // key, an ID, a date key, a code or a flag may be filled with #,0. No shipped rule fills a column with #,0, so a
        // model's own rule is how a person meets this half. The same name list decides it, and it misses the financial year
        // (FY) and an order number written in lower case with no separator (ordernumber): #,0 then shows 2024 as 2,024.

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("FY")]
        [InlineData("ordernumber")]
        public async Task Verify_a_models_own_whole_number_rule_fills_no_year_or_id_column(string column)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Northstar", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", column, "Int64", "c1", "human");
            await e.LoadBpaRulesAsync("[{\"ID\":\"TEST_COLUMN_WHOLE\",\"Name\":\"Whole-number columns\",\"Category\":\"Formatting\",\"Severity\":2,"
                + "\"Scope\":\"DataColumn\",\"Expression\":\"DataType = \\\"Int64\\\" and (FormatString = null or FormatString = \\\"\\\")\","
                + "\"FixExpression\":\"FormatString = \\\"#,0\\\"\"}]", replace: false, "human");
            var target = "column:Sales/" + column;

            var finding = Assert.Single((await e.BpaScanAsync()).Violations, v => v.RuleId == "TEST_COLUMN_WHOLE" && v.ObjectRef == target);
            Assert.False(finding.CanAutoFix, $"the model's own rule offers an automatic #,0 fill on the Int64 column {column}, which is a year or an ID");

            await PressEverythingAsync(f, "TEST_COLUMN_WHOLE", target);
            var written = await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Columns[column].FormatString);
            Assert.True(string.IsNullOrEmpty(written), $"the Int64 column {column} was given the format \"{written}\" automatically");
        }
    }
}
