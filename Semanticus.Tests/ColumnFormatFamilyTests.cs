// The column format family (T268, M4 columns first): for every visible number or date column that has no format
// yet, which of six format families it shows as. docs/redesign/jev-columns-replay.md is the evidence: format was
// the one column setting where Jev beat the rule (names only 98%, the stronger rule 81%), so it is the one column
// setting Jev decides, and it sends names and types only.
//
// WHAT IS PROVED HERE, AND HOW.
//   Free: no column item, nothing asked, no run started, nothing measured. Counted off the oracle.
//   Pro: one item per table, "Format N columns in T", owned by Jev, already proposed, every line a decision on
//   column.format with an engine-built reason and check, the format string taken from the format library.
//   Picks come only from the options: a family that does not fit the column's type, a currency when the model has
//   no money format yet, and an option that was never offered all end as no line.
//   Below the bar: the line arrives dropped, approve leaves the column as it was, and the rule's finding for it
//   is still on the Plan.
//   Approve writes the kept formats as one undo step.
//   What goes to Jev: the four declared fields, names and types only.
//
// THE DIGESTS ARE COMPUTED FROM THE FAMILY'S OWN REQUEST (ColumnFormatFamily.Prepare, the call the run makes), so
// a run that sent a different request would find no answer here.

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
using Semanticus.Engine.Decisions;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class ColumnFormatFamilyTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public ColumnFormatFamilyTests(ModelTemplates templates) => _templates = templates;

        private const string Point = "column.format";

        /// <summary>Customer is a dimension; Sales and Returns are facts. The asked columns are Sales/Amount,
        /// Sales/Quantity, Sales/Discount Rate, Sales/Ship Date and Returns/Qty. Left alone: two relationship keys,
        /// a column that already has a format, a text column and a hidden column. With money, Sales/Priced carries a
        /// money format, so the currency family has one this model already uses.</summary>
        private async Task<DecisionFixture> ModelAsync(string file, bool pro = true, bool money = true)
        {
            var f = new DecisionFixture(pro ? TestEntitlements.Pro : TestEntitlements.Free);
            await _templates.OpenCopyAsync(f, money ? "money" : "plain", b => BuildModelAsync(b, money), file);
            f.UseNull();
            return f;
        }

        private static async Task BuildModelAsync(DecisionFixture f, bool money)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", null, "human");
            await e.SetObjectPropertyAsync("column:Sales/Quantity", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateColumnAsync("table:Sales", "Discount Rate", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Ship Date", "DateTime", null, "human");
            await e.CreateColumnAsync("table:Sales", "Priced", "Double", null, "human");
            await e.SetObjectPropertyAsync("column:Sales/Priced", "FormatString", money ? "$#,##0.00" : "#,##0.00", "human");
            await e.CreateColumnAsync("table:Sales", "Note", "String", null, "human");
            await e.CreateColumnAsync("table:Sales", "Internal", "Double", null, "human");
            await e.SetColumnMetadataAsync("column:Sales/Internal", true, null, null, null, "human");
            await e.CreateTableAsync("Returns", "human");
            await e.CreateColumnAsync("table:Returns", "Qty", "Int64", null, "human");
            await e.SetObjectPropertyAsync("column:Returns/Qty", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, null, "human");
        }

        private sealed class CapturingOracle : IDecisionOracle
        {
            private readonly IDecisionOracle _inner;
            private readonly List<DecisionRequest> _seen = new List<DecisionRequest>();
            public CapturingOracle(IDecisionOracle inner) { _inner = inner; }
            public bool Available => true;
            public IReadOnlyList<DecisionRequest> Seen { get { lock (_seen) return _seen.ToList(); } }
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                lock (_seen) _seen.Add(request);
                return _inner.AskAsync(request, ct);
            }
        }

        /// <summary>The family's own request for each candidate column, keyed "Table/Column".</summary>
        private static async Task<Dictionary<string, string>> DigestsAsync(DecisionFixture f)
        {
            var point = DecisionRegistry.Require(Point);
            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnFormatFamily.Prepare(m));
            return prepared.Asks.ToDictionary(
                a => a.TableName + "/" + a.ColumnName,
                a => a.Ref,   // the family asks a table's columns at once (m11/jev-batch): an entry is keyed by the column's ref
                StringComparer.Ordinal);
        }

        private static string Fixture(IEnumerable<(string Digest, string Choice, double Confidence)> rows)
        {
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var (digest, choice, confidence) in rows)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(digest))
                  .Append(",\"point\":\"").Append(Point).Append('"')
                  .Append(",\"usage\":{\"input_tokens\":500,\"output_tokens\":20}")
                  .Append(",\"answers\":{\"format_family\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                  .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append("}}}");
            }
            return sb.Append("]}").ToString();
        }

        /// <summary>Pro, permission given, and Jev's answers recorded: Amount is money (sure), Quantity a whole
        /// number (sure), Discount Rate a percent (likely), Ship Date a date (sure), Returns/Qty a whole number but
        /// unsure.</summary>
        private static async Task<CapturingOracle> AnswerAsync(DecisionFixture f,
            string amount = "currency", string shipDate = "date_or_time")
        {
            await f.ConsentAsync();
            var d = await DigestsAsync(f);
            var recorded = new ByObjectRecordedOracle(Fixture(new[]
            {
                (d["Sales/Amount"], amount, 0.95),
                (d["Sales/Quantity"], "whole_number", 0.93),
                (d["Sales/Discount Rate"], "percent", 0.7),
                (d["Sales/Ship Date"], shipDate, 0.9),
                (d["Returns/Qty"], "whole_number", 0.4),
            }), "jev-test-1");
            var capturing = new CapturingOracle(recorded);
            f.Engine.UseDecisionsForTest(new DecisionService(capturing, new DecisionBudget()));
            return capturing;
        }

        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return await f.Engine.GetModelPlanAsync();
        }

        private static async Task<string> FormatOf(DecisionFixture f, string columnRef) =>
            (await f.Engine.GetObjectAsync(columnRef)).Properties["formatString"]?.ToString() ?? "";

        // ---- free -------------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_free_session_asks_nothing_and_has_no_column_format_item()
        {
            using var f = await ModelAsync("ColFmtFree.bim", pro: false);
            await f.ConsentAsync();
            // An oracle that WOULD answer, so a zero below is the gate and not an empty fixture.
            var oracle = f.UseRecorded(Fixture(new[] { (new string('0', 64), "currency", 0.99) }));

            var plan = await f.Engine.GetModelPlanAsync();
            await f.Engine.SetDescriptionAsync("table:Returns", "Goods sent back.", "human");
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();

            Assert.DoesNotContain(plan.Items, i => i.Id.StartsWith("jev.column-format.", StringComparison.Ordinal));
            Assert.DoesNotContain(plan.Items, i => i.Who == ModelPlanOwners.Jev);
            Assert.False(plan.JevPending);
            Assert.Equal(0, oracle.AskCount);
            Assert.Equal(0, f.Engine.Decisions.RequestsStarted);
            Assert.Equal(0, f.Engine.JevRunsStartedForTest);
            Assert.Equal(0, f.Engine.FactsComputationsForTest);
        }

        // ---- Pro --------------------------------------------------------------------------------------------

        [Fact]
        public async Task Pro_puts_one_Format_N_columns_row_per_table_on_the_Plan()
        {
            using var f = await ModelAsync("ColFmtPro.bim");
            await AnswerAsync(f);

            var plan = await SettledPlanAsync(f);
            var rows = plan.Items.Where(i => i.Id.StartsWith("jev.column-format.", StringComparison.Ordinal))
                                 .OrderBy(i => i.Id, StringComparer.Ordinal).ToArray();

            Assert.Equal(new[] { "jev.column-format.table:Returns", "jev.column-format.table:Sales" }, rows.Select(i => i.Id).ToArray());
            var sales = rows.Single(i => i.Id == "jev.column-format.table:Sales");
            Assert.Equal("Format 4 columns in Sales", sales.Title);
            Assert.Equal("Format 1 column in Returns", rows.Single(i => i.Id == "jev.column-format.table:Returns").Title);
            Assert.Equal(ModelPlanSections.Numbers, sales.Section);
            Assert.Equal(ModelPlanOwners.Jev, sales.Who);
            Assert.Equal(ModelPlanItemStates.Proposed, sales.State);
            Assert.Equal(ModelPlanChangeStates.Proposed, sales.ChangeSet.State);
            Assert.False(sales.ChangeSet.Stale, sales.ChangeSet.StaleReason);
            Assert.Equal("4 columns in Sales", sales.Touches);

            // The columns left alone: keys, a formatted column, text, hidden.
            var targets = sales.ChangeSet.Lines.Select(l => l.Target).ToArray();
            Assert.Equal(new[] { "column:Sales/Amount", "column:Sales/Discount Rate", "column:Sales/Quantity", "column:Sales/Ship Date" },
                         targets.OrderBy(t => t, StringComparer.Ordinal).ToArray());

            foreach (var line in rows.SelectMany(i => i.ChangeSet.Lines))
            {
                Assert.Equal("set_format_string", line.Op);
                Assert.Equal("formatString", line.Property);
                Assert.Equal(ModelPlanChangeKinds.Decision, line.Provenance.Kind);
                Assert.Equal(Point, line.Provenance.Point);
                Assert.False(string.IsNullOrWhiteSpace(line.Reason), line.Id);
                // A suggestion is not a check: its plain note is its own field and it carries no check.
                var checkText = line.Suggestion ? line.Note : line.Check.Text;
                Assert.True(line.Suggestion ? line.Check == null : line.Check.Exists, line.Id);
                Assert.False(string.IsNullOrWhiteSpace(checkText), line.Id);
                Assert.DoesNotContain("\u2014", line.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("\u2014", checkText, StringComparison.Ordinal);
            }

            string After(string target) => sales.ChangeSet.Lines.Single(l => l.Target == target).After;
            Assert.Equal("$#,##0.00", After("column:Sales/Amount"));   // the money format this model already uses
            Assert.Equal("#,##0", After("column:Sales/Quantity"));     // the library's first whole number format
            // A rate is offered with one decimal, as the engine's own YoY % is (v-rules-v1i decision 3; it was the library's first
            // percent template, "0%", which shows 15.6% as 16%).
            Assert.Equal("0.0%", After("column:Sales/Discount Rate"));
            Assert.Equal("yyyy-mm-dd", After("column:Sales/Ship Date"));
            Assert.Null(sales.ChangeSet.Lines.Single(l => l.Target == "column:Sales/Discount Rate").Provenance.ConfidenceWord);   // a suggestion carries no word (addendum 3 item 17)
            // THE FORMAT FILL RULE: only the Int64 quantity is proved, so only its line arrives kept. The money, percent and date
            // formats are suggestions the person keeps after checking, each saying so in plain words.
            foreach (var line in sales.ChangeSet.Lines)
            {
                var proved = line.Target == "column:Sales/Quantity";
                Assert.Equal(proved, line.Kept);
                if (!proved) { Assert.Equal("Jev suggests this format; check it fits before you run it.", line.Note); Assert.True(line.Suggestion); Assert.Null(line.Check); }
            }
        }

        /// <summary>N6: the strip says Jev updated the plan only when Jev answered, and the column loop never counted its
        /// answers. This model has no measures and no starter point is live, so column format is the only family that
        /// answers: before the fix the strip read the rules' line over four lines Jev decided.</summary>
        [Fact]
        public async Task Column_answers_count_so_the_strip_says_Jev_updated_the_plan()
        {
            using var f = await ModelAsync("ColFmtStrip.bim");
            await AnswerAsync(f);

            var plan = await SettledPlanAsync(f);
            Assert.Contains(plan.Items, i => i.Id == "jev.column-format.table:Sales");
            Assert.Equal(PlanStripUpdatedBy.Jev, plan.Strip.Updated.By);
        }

        [Fact]
        public async Task A_Jev_row_names_the_objects_its_lines_change()
        {
            using var f = await ModelAsync("ColFmtObjects.bim");
            await AnswerAsync(f);

            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-format.table:Sales");

            // The row detail's Objects field reads this list; empty, it said "Nothing named yet." over four lines.
            Assert.Equal(new[] { "column:Sales/Amount", "column:Sales/Discount Rate", "column:Sales/Quantity", "column:Sales/Ship Date" },
                         sales.Objects.OrderBy(o => o, StringComparer.Ordinal).ToArray());
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Approve_writes_the_kept_formats_as_one_undo_step()
        {
            using var f = await ModelAsync("ColFmtApprove.bim");
            await AnswerAsync(f);
            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-format.table:Sales");

            var result = await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest);

            Assert.True(result.Performed, result.Sentence);
            Assert.Equal("", await FormatOf(f, "column:Sales/Amount"));          // a suggestion is never written by approve
            Assert.Equal("#,##0", await FormatOf(f, "column:Sales/Quantity"));   // the one proved line
            Assert.Equal("", await FormatOf(f, "column:Sales/Discount Rate"));
            Assert.Equal("", await FormatOf(f, "column:Sales/Ship Date"));

            await f.Engine.UndoAsync("human");
            Assert.Equal("", await FormatOf(f, "column:Sales/Amount"));
            Assert.Equal("", await FormatOf(f, "column:Sales/Quantity"));
            Assert.Equal("", await FormatOf(f, "column:Sales/Discount Rate"));
            Assert.Equal("", await FormatOf(f, "column:Sales/Ship Date"));
        }

        [Fact]
        public async Task A_second_read_changes_nothing_so_a_digest_stays_approvable()
        {
            using var f = await ModelAsync("ColFmtStable.bim");
            await AnswerAsync(f);

            var first = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-format.table:Sales");
            var second = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "jev.column-format.table:Sales");

            Assert.Equal(first.ChangeSet.Digest, second.ChangeSet.Digest);
        }

        [Fact]
        public async Task Each_point_switches_off_on_its_own()
        {
            using var f = await ModelAsync("ColFmtSwitch.bim");
            await AnswerAsync(f);
            Assert.Contains((await SettledPlanAsync(f)).Items, i => i.Id == "jev.column-format.table:Sales");

            // The measures point off leaves the column rows exactly as they were.
            await f.Engine.SetDecisionPointEnabledAsync(DecisionRegistry.MeasureFolderAndFormat, false, "human");
            Assert.Contains((await SettledPlanAsync(f)).Items, i => i.Id == "jev.column-format.table:Sales");

            // The column point off takes them off the Plan on the very next read, with nothing asked.
            await f.Engine.SetDecisionPointEnabledAsync(Point, false, "human");
            Assert.DoesNotContain((await f.Engine.GetModelPlanAsync()).Items, i => i.Id.StartsWith("jev.column-format.", StringComparison.Ordinal));
            Assert.DoesNotContain((await SettledPlanAsync(f)).Items, i => i.Id.StartsWith("jev.column-format.", StringComparison.Ordinal));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_Format_row_never_waits_for_the_cleanup_even_for_a_line_a_person_kept()
        {
            // The cleanup is ALWAYS optional since m11/plan-harm round 2 (coordinator ruling, 28 Sep: no row ever waits
            // for it, whether or not a report has been read). It used to hold a Format row once a person kept a
            // delete of a column the row writes; now it never offers such a column (a fact table's numbers and dates
            // are held back), and a kept line of its own still holds nothing: it waits, the Format row goes first.
            using var f = await ModelAsync("ColFmtWaits.bim");
            await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, "ColFmtWaits.bim");
            await AnswerAsync(f);
            await PlanHarmKit.CheckReportsAgainAsync(f);   // granting permission moved the revision the reading was stamped at

            var plan = await SettledPlanAsync(f);
            var sales = plan.Items.Single(i => i.Id == "jev.column-format.table:Sales");
            Assert.DoesNotContain(sales.WaitsFor, w => w.Id == "cleanup/model");

            // Keep every line the cleanup offers: none is a column the Format row writes, and the row still waits for
            // nothing the cleanup holds.
            var proposed = await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human", null);
            var lines = proposed.Plan.Items.Single(i => i.Id == "cleanup/model").ChangeSet.Lines;
            Assert.NotEmpty(lines);
            // Round 5: the cleanup LISTS every unused column (the Format row's columns are lines too, unticked, "it holds numbers"),
            // and what it offers is what starts ticked. None of those is a column the Format row writes.
            var offered = lines.Where(l => l.Kept).ToArray();
            Assert.NotEmpty(offered);
            Assert.DoesNotContain(offered, l => sales.Writes.Contains(l.Target));
            foreach (var l in offered) await f.Engine.SetPlanItemAsync(l.PlanItemId, null, true, "human");
            var after = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "jev.column-format.table:Sales");
            Assert.DoesNotContain(after.WaitsFor ?? Array.Empty<PlanWaitRef>(), w => w.Id == "cleanup/model");
        }

        // ---- picks come only from the options -----------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_family_that_does_not_fit_the_column_type_is_refused_not_applied()
        {
            using var f = await ModelAsync("ColFmtType.bim");
            await AnswerAsync(f, amount: "date_or_time", shipDate: "percent");

            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-format.table:Sales");

            Assert.DoesNotContain(sales.ChangeSet.Lines, l => l.Target == "column:Sales/Amount");
            Assert.DoesNotContain(sales.ChangeSet.Lines, l => l.Target == "column:Sales/Ship Date");
            Assert.Contains(sales.ChangeSet.Refused, r => r.Target == "column:Sales/Amount");
            Assert.Contains(sales.ChangeSet.Refused, r => r.Target == "column:Sales/Ship Date");
            Assert.All(sales.ChangeSet.Refused, r => Assert.DoesNotContain("\u2014", r.Reason, StringComparison.Ordinal));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Currency_with_no_money_format_in_the_model_is_refused_never_guessed()
        {
            using var f = await ModelAsync("ColFmtNoMoney.bim", money: false);
            await AnswerAsync(f);

            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-format.table:Sales");

            Assert.DoesNotContain(sales.ChangeSet.Lines, l => l.Target == "column:Sales/Amount");
            Assert.Contains(sales.ChangeSet.Refused, r => r.Target == "column:Sales/Amount" && r.Reason.Contains("money", StringComparison.Ordinal));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_option_that_was_never_offered_is_no_line()
        {
            using var f = await ModelAsync("ColFmtBogus.bim");
            await AnswerAsync(f, amount: "scientific");

            var sales = (await SettledPlanAsync(f)).Items.Single(i => i.Id == "jev.column-format.table:Sales");

            Assert.DoesNotContain(sales.ChangeSet.Lines, l => l.Target == "column:Sales/Amount");
            Assert.Contains(sales.ChangeSet.Lines, l => l.Target == "column:Sales/Quantity");
        }

        [Fact]
        public void Map_takes_only_a_family_from_the_column_options_and_a_format_from_the_library()
        {
            var ask = new ColumnFormatFamily.Ask
            {
                Ref = "column:Sales/Amount", ColumnName = "Amount", TableRef = "table:Sales", TableName = "Sales",
                DataType = "Double", MoneyFormat = null,
            };
            var templates = new[]
            {
                new FormatTemplateInfo { Category = "Whole numbers", Kind = "static", FormatString = "#,##0" },
                new FormatTemplateInfo { Category = "Decimals", Kind = "static", FormatString = "#,##0.00" },
            };
            foreach (var family in new[] { "date_or_time", "percent", "currency", "text_or_none" })
            {
                var lines = new List<ChangeItem>();
                var refused = new List<ModelPlanChangeRefusal>();
                ColumnFormatFamily.Map(ask, family, 0.99, 0.85, templates, lines, refused);
                Assert.Empty(lines);   // off type, no template, no money format, or nothing to do
            }
            var ok = new List<ChangeItem>();
            ColumnFormatFamily.Map(ask, "decimal", 0.99, 0.85, templates, ok, new List<ModelPlanChangeRefusal>());
            Assert.Equal("#,##0.00", Assert.Single(ok).After);
            // The options the engine lists for a column come from its type.
            Assert.DoesNotContain("date_or_time", ColumnFormatFamily.OptionsFor("Int64"));
            Assert.Equal(new[] { "date_or_time", "text_or_none" }, ColumnFormatFamily.OptionsFor("DateTime"));
        }

        // ---- below the bar --------------------------------------------------------------------------------------

        [Fact]
        public async Task Below_the_bar_the_line_arrives_dropped_and_the_rule_keeps_the_column()
        {
            using var f = await ModelAsync("ColFmtUnsure.bim");
            await AnswerAsync(f);

            var plan = await SettledPlanAsync(f);
            var returns = plan.Items.Single(i => i.Id == "jev.column-format.table:Returns");
            var unsure = Assert.Single(returns.ChangeSet.Lines);
            Assert.Equal("unsure", unsure.Provenance.ConfidenceWord);
            Assert.False(unsure.Kept);

            var result = await f.Engine.ModelPlanActionAsync(returns.Id, "approve", "human", returns.ChangeSet.Digest);
            Assert.Equal("", await FormatOf(f, "column:Returns/Qty"));

            // The rule still names the column: the readiness finding that asks for a format is untouched.
            var findings = await f.Engine.AiReadinessScanAsync();
            Assert.Contains(findings.Findings, x => x.RuleId == "FMT-COLUMN" && (x.ObjectRef ?? "").Contains("Returns/Qty", StringComparison.Ordinal));
        }

        /// <summary>N8: one owner per column. The rules' "Set the format on N fields" row and the Jev rows both listed
        /// the same columns until the Jev rows were applied. A column a live, kept Jev line formats leaves the rule
        /// row; a column whose Jev line arrived dropped (unsure) stays on it, because the rule still owns it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_rule_format_row_leaves_out_the_columns_a_kept_Jev_line_formats()
        {
            using var f = await ModelAsync("ColFmtOneOwner.bim");
            var before = (await f.Engine.GetModelPlanAsync()).Items.Single(i => i.Id == "proposal/formats");
            Assert.Contains(before.Diff, d => d.Contains("Amount", StringComparison.Ordinal));
            // N9: a rule row with no change lines names the objects it covers, so its detail never says
            // "Nothing named yet." over five columns.
            Assert.Equal(5, before.Objects.Length);
            Assert.Contains(before.Objects, o => o.Contains("Sales/Amount", StringComparison.Ordinal));

            await AnswerAsync(f);
            var plan = await SettledPlanAsync(f);
            var rule = plan.Items.Single(i => i.Id == "proposal/formats");
            var diff = string.Join(" | ", rule.Diff);
            // JOIN, part 2 (item 9, after m11/feedback-rules and m11/rule-clashes): one proposal per column. Quantity is a proved line, kept.
            // Amount, Discount Rate and Ship Date are formats the rules cannot prove, which Jev suggests with some confidence (0.95, 0.7 and 0.9):
            // its unticked suggestion owns each of them, so the rules' row, which could only advise there, no longer lists them beside it.
            // Only the column Jev was unsure of (Returns/Qty, 0.4) stays with the rules.
            foreach (var goes in new[] { "Quantity", "Amount", "Discount Rate", "Ship Date" })
                Assert.DoesNotContain(goes, diff, StringComparison.Ordinal);
            Assert.Contains("Qty", diff, StringComparison.Ordinal);
            Assert.Equal("Set the format on 1 field", rule.Title);
            Assert.Single(rule.Objects);
            // And each of them still has exactly one proposal: Jev's line.
            var jevLines = plan.Items.Where(i => i.Id.StartsWith("jev.column-format.", StringComparison.Ordinal)).SelectMany(i => i.ChangeSet.Lines).Select(l => l.Target).ToArray();
            foreach (var one in new[] { "column:Sales/Amount", "column:Sales/Discount Rate", "column:Sales/Ship Date" })
                Assert.Single(jevLines, t => t == one);
        }

        /// <summary>UAT 26 Sep (verifier round 2): once Jev's kept lines own every column the rules' "Set the format on
        /// N fields" row listed, the row steps aside. Nobody set those formats yet, so the row is not recorded as done;
        /// the work moved to Jev's rows, and the rule's row comes back if those lines go.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_rule_format_row_standing_aside_for_Jev_is_not_recorded_as_done()
        {
            using var f = await ModelAsync("ColFmtAside.bim");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "proposal/formats");

            await f.ConsentAsync();
            var d = await DigestsAsync(f);
            f.Engine.UseDecisionsForTest(new DecisionService(new ByObjectRecordedOracle(Fixture(new[]
            {
                (d["Sales/Amount"], "currency", 0.95),
                (d["Sales/Quantity"], "whole_number", 0.93),
                (d["Sales/Discount Rate"], "percent", 0.9),
                (d["Sales/Ship Date"], "date_or_time", 0.9),
                (d["Returns/Qty"], "whole_number", 0.9),
            }), "jev-test-1"), new DecisionBudget()));
            var plan = await SettledPlanAsync(f);
            plan = await f.Engine.GetModelPlanAsync();

            Assert.DoesNotContain(plan.Items, i => i.Id == "proposal/formats" && i.Status == ModelPlanStatuses.Done);
            Assert.Equal("", await FormatOf(f, "column:Sales/Amount"));

            // Permission withdrawn: Jev's rows go, and the rule's row is back, open.
            await f.Engine.WithdrawConsentAsync("human");
            var back = await f.Engine.GetModelPlanAsync();
            Assert.Contains(back.Items, i => i.Id == "proposal/formats" && i.Status != ModelPlanStatuses.Done);
            Assert.DoesNotContain(back.Items, i => i.Id == "proposal/formats" && i.Status == ModelPlanStatuses.Done);
        }

        // ---- verify round 3 (m11/m4-relationships at 038569a5, 26 Sep): defects ---------------------------------

        /// <summary>verify: round 2's found item was "Set the format on N fields" reading as done once the column.format
        /// family is live, though nobody set a format. 038569a5 stops the row being RECORDED as done, but the Plan's
        /// History still announces it: the redraw's sentence comes from PlanMemory.RefreshNote, which counts every row
        /// that left the plan as done ("1 item done, 3 new."), while the same entry's Completed list is empty. Seen in
        /// UAT lane e (Plan, History: "17:13 Semanticus 1 item done, 3 new."). Nothing was done here, so no redraw may
        /// say an item was.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_the_plan_history_never_says_a_row_that_stood_aside_for_Jev_was_done()
        {
            using var f = await ModelAsync("ColFmtAsideHistory.bim");
            Assert.Contains((await f.Engine.GetModelPlanAsync()).Items, i => i.Id == "proposal/formats");

            await f.ConsentAsync();
            var d = await DigestsAsync(f);
            f.Engine.UseDecisionsForTest(new DecisionService(new ByObjectRecordedOracle(Fixture(new[]
            {
                (d["Sales/Amount"], "currency", 0.95),
                (d["Sales/Quantity"], "whole_number", 0.93),
                (d["Sales/Discount Rate"], "percent", 0.9),
                (d["Sales/Ship Date"], "date_or_time", 0.9),
                (d["Returns/Qty"], "whole_number", 0.9),
            }), "jev-test-1"), new DecisionBudget()));
            var settled = await SettledPlanAsync(f);
            // Jev's format lines the rule cannot prove arrive as unticked suggestions (m11/feedback-rules, the format fill rule), and a
            // suggestion owns no column until the person ticks it. So the person ticks them, as on the page, and the rules' row then
            // steps aside for lines that are kept.
            foreach (var line in settled.Items.Where(i => i.Id.StartsWith("jev.column-format.", StringComparison.Ordinal))
                                         .SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Where(l => !l.Kept))
                await f.Engine.SetPlanItemAsync(line.PlanItemId, null, true, "human");
            await SettledPlanAsync(f);
            var plan = await f.Engine.GetModelPlanAsync();

            Assert.DoesNotContain(plan.Items, i => i.Id == "proposal/formats");
            Assert.Equal("", await FormatOf(f, "column:Sales/Amount"));
            foreach (var entry in plan.History.Where(h => h.Kind == ModelPlanHistoryKinds.Redrawn))
                Assert.False((entry.Sentence ?? "").Contains(" done", StringComparison.Ordinal),
                    "the Plan's History says '" + entry.Sentence + "' although nothing was done (completed: "
                    + entry.Completed.Length + ", gone: " + string.Join("; ", entry.Gone) + ")");
        }

        // ---- History (joint verifier, round 3 of m11/plan-waits: the sibling of DT4) --------------------------------

        /// <summary>A Jev row is never remembered as a seen row (LocalEngine.Plan.cs: "A JEV ROW IS NOT REMEMBERED
        /// EITHER"), but the History sentence and its Appeared list compare what was remembered with every row now on the
        /// plan, Jev's included. So every redraw after a Jev row arrives tells it as new again, however long it has been
        /// there. Seen live in lane g at fd784aea: "2 items done, 3 new." after Run the plan, the three being column
        /// format rows that had been on the plan since consent. Here: the Jev rows are on the plan, a time intelligence
        /// measure brings the date table row, and undo takes it away again. The undo's entry must tell one item done and
        /// nothing new, because no row appeared.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_a_Jev_row_already_on_the_plan_is_not_told_as_new_again_in_History()
        {
            using var f = await ModelAsync("ColFmtHistoryNew.bim");
            // A date column and no date table now gives the Plan an "Add a date table" row of its own (hero 20 D9), so the model starts with its
            // date table marked and the row that appears below is the person clearing the mark.
            await AnswerAsync(f);
            await f.Engine.MarkDateTableAsync("table:Sales", "Ship Date", "human");
            var jev = (await SettledPlanAsync(f)).Items
                .Where(i => i.Id.StartsWith("jev.column-format.", StringComparison.Ordinal)).Select(i => i.Title).ToArray();
            Assert.NotEmpty(jev);

            await f.Engine.SetObjectPropertyAsync("table:Sales", "DataCategory", "", "human");
            var withDate = await SettledPlanAsync(f);
            Assert.Contains(withDate.Items, i => i.Id == "datetable/model");
            Assert.True(jev.All(t => withDate.Items.Any(i => i.Title == t)), "the Jev rows stay on the plan");

            await f.Engine.UndoAsync("human");
            var after = await SettledPlanAsync(f);
            Assert.DoesNotContain(after.Items, i => i.Id == "datetable/model" && i.Status != ModelPlanStatuses.Done);

            var entry = after.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn);
            var told = entry.Sentence + " | appeared: " + string.Join(", ", entry.Appeared ?? Array.Empty<string>())
                     + " | gone: " + string.Join(", ", entry.Gone ?? Array.Empty<string>());
            Assert.Contains(entry.Gone ?? Array.Empty<string>(), g => g.Contains("date table", StringComparison.OrdinalIgnoreCase));
            // An undo only takes rows away, so nothing is new in this entry, and above all not the Jev rows.
            Assert.False((entry.Appeared ?? Array.Empty<string>()).Any(a => jev.Contains(a)), told);
            Assert.False(entry.Sentence.Contains("new", StringComparison.OrdinalIgnoreCase), told);
        }

        /// <summary>The same count for an assistant's row, which is not remembered either (LocalEngine.Plan.cs: "AN
        /// ASSISTANT'S ROW IS NOT REMEMBERED HERE"). Free, so no Jev row is on the plan: the assistant's rename is on the
        /// plan before both moves, and the undo's entry must not tell it as new.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_an_assistant_row_already_on_the_plan_is_not_told_as_new_again_in_History()
        {
            using var f = await ModelAsync("ColFmtHistoryAsst.bim", pro: false);
            await f.Engine.DeleteObjectAsync("column:Sales/Ship Date", "human");   // the date column arriving below is what brings the date table row (hero 20 D9)
            await f.Engine.AddPlanItemAsync("column:Sales/Note", "rename", "Remark", null, null, null, "agent");
            var first = await f.Engine.GetModelPlanAsync();
            var asst = first.Items.Where(i => i.Id.StartsWith(LocalEngine.AssistantItemPrefix, StringComparison.Ordinal)).Select(i => i.Title).ToArray();
            Assert.NotEmpty(asst);

            await f.Engine.CreateColumnAsync("table:Sales", "Ship Date", "DateTime", null, "human");
            var withDate = await f.Engine.GetModelPlanAsync();
            Assert.Contains(withDate.Items, i => i.Id == "datetable/model");

            await f.Engine.UndoAsync("human");
            var after = await f.Engine.GetModelPlanAsync();

            var entry = after.History.Last(h => h.Kind == ModelPlanHistoryKinds.Redrawn);
            var told = entry.Sentence + " | appeared: " + string.Join(", ", entry.Appeared ?? Array.Empty<string>())
                     + " | gone: " + string.Join(", ", entry.Gone ?? Array.Empty<string>());
            Assert.Contains(entry.Gone ?? Array.Empty<string>(), g => g.Contains("date table", StringComparison.OrdinalIgnoreCase));
            Assert.False((entry.Appeared ?? Array.Empty<string>()).Any(a => asst.Contains(a)), told);
            Assert.False(entry.Sentence.Contains("new", StringComparison.OrdinalIgnoreCase), told);
        }

        /// <summary>The final batch's fix for the two tests above computes the History sentence over the rows the plan
        /// remembers, which is right for History, and the plan's RefreshedWhy is that same sentence. But RefreshedWhy is
        /// "what changed since the last call" (ModelPlanView.RefreshedWhy), and a Jev row is never remembered, so on the
        /// call where Jev's rows first arrive the MCP door is told "Nothing on the plan changed." while those rows are new
        /// on the plan. Seen at 56f86c1b: 9 rows, then 12 (two Jev rows and a save row), and the call that brought the
        /// two Jev rows said nothing changed. Before the fix that call said "2 new." (and then kept saying it).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_the_call_where_Jev_rows_first_arrive_does_not_say_nothing_on_the_plan_changed()
        {
            using var f = await ModelAsync("ColFmtArriveWhy.bim");
            await AnswerAsync(f);
            var before = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(before.Items, i => i.Id.StartsWith("jev.column-format.", StringComparison.Ordinal));

            await f.Engine.JevSettledForTest();
            var arrived = await f.Engine.GetModelPlanAsync();
            var jev = arrived.Items.Where(i => i.Id.StartsWith("jev.column-format.", StringComparison.Ordinal)).ToArray();
            Assert.NotEmpty(jev);

            Assert.True(arrived.RefreshedWhy != "Nothing on the plan changed.",
                jev.Length + " Jev rows arrived on this call and RefreshedWhy reads " + arrived.RefreshedWhy);
        }

        /// <summary>The same for an assistant's own row (L1 of the joint verifier's plan-waits check): an "asst/" row is
        /// never remembered either, so the call that brings it must still say something changed, and the call after it,
        /// with the row simply still there, must not tell it as new again. History stays over the remembered rows: the
        /// row's arrival writes no entry that calls it new.</summary>
        [Fact]
        public async Task The_call_where_an_assistant_row_first_arrives_does_not_say_nothing_on_the_plan_changed()
        {
            using var f = await ModelAsync("ColFmtArriveAsst.bim", pro: false);
            var before = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(before.Items, i => i.Id.StartsWith(LocalEngine.AssistantItemPrefix, StringComparison.Ordinal));

            await f.Engine.AddPlanItemAsync("column:Sales/Note", "rename", "Remark", null, null, null, "agent");
            var arrived = await f.Engine.GetModelPlanAsync();
            Assert.Contains(arrived.Items, i => i.Id.StartsWith(LocalEngine.AssistantItemPrefix, StringComparison.Ordinal));
            Assert.Equal("1 new.", arrived.RefreshedWhy);

            var again = await f.Engine.GetModelPlanAsync();
            Assert.Equal("Nothing on the plan changed.", again.RefreshedWhy);
            Assert.DoesNotContain(again.History, h => h.Kind == ModelPlanHistoryKinds.Redrawn
                && (h.Sentence ?? "").Contains("new", StringComparison.OrdinalIgnoreCase));
        }

        // ---- what goes --------------------------------------------------------------------------------------------

        [Fact]
        public async Task Only_names_and_types_go_to_Jev()
        {
            using var f = await ModelAsync("ColFmtSent.bim");
            var oracle = await AnswerAsync(f);
            await SettledPlanAsync(f);

            var sent = oracle.Seen.Where(r => r.PointName == Point).ToArray();
            // One request for the whole model (m16/jev-batch): Sales' four columns and Returns' one.
            Assert.Single(sent);
            var declared = DecisionRegistry.Require(Point).SnapshotFields;
            foreach (var r in sent)
            {
                Assert.Equal(new[] { "columns", "table_columns" }, r.Snapshot.Keys.OrderBy(x => x, StringComparer.Ordinal));
                Assert.All(r.Snapshot.Keys, k => Assert.Contains(k, declared));
                using var entries = JsonDocument.Parse(JsonSerializer.Serialize(r.Snapshot["columns"]));
                foreach (var e in entries.RootElement.EnumerateArray())
                    Assert.Equal(new[] { "data_type", "is_calculated", "name", "table" },
                                 e.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                // What a table shares is a map keyed by the table's name, once for each table in the request; every entry names a table in it.
                var others = JsonSerializer.Serialize(r.Snapshot["table_columns"]);
                using var otherDoc = JsonDocument.Parse(others);
                Assert.Equal(new[] { "Returns", "Sales" }, otherDoc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                Assert.All(entries.RootElement.EnumerateArray(), e => Assert.True(otherDoc.RootElement.TryGetProperty(e.GetProperty("table").GetString(), out _)));
                foreach (var c in otherDoc.RootElement.EnumerateObject().SelectMany(t => t.Value.EnumerateArray()))
                    Assert.Equal(new[] { "data_type", "name" }, c.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                // One question for each column, naming its entry by path and by its own name and table (m16/jev-size), and the question itself is
                // what it always was.
                Assert.Equal(entries.RootElement.GetArrayLength(), r.Questions.Count);
                for (var i = 0; i < r.Questions.Count; i++)
                {
                    var e = entries.RootElement[i];
                    Assert.StartsWith("For the column `columns[" + i + "]`, named \"" + e.GetProperty("name").GetString() + "\" in the table \""
                                      + e.GetProperty("table").GetString() + "\": ", r.Questions[i].Instructions, StringComparison.Ordinal);
                    Assert.EndsWith(ColumnFormatFamily.Instructions, r.Questions[i].Instructions, StringComparison.Ordinal);
                    Assert.StartsWith("format_family:column:", r.Questions[i].Name, StringComparison.Ordinal);
                }
            }
            // The column that already carries a format is asked about by nobody.
            Assert.DoesNotContain(sent, r => ColumnNames(r).Contains("Priced"));
        }

        /// <summary>The names of the columns a batched request asks about, in its `columns` list.</summary>
        private static string[] ColumnNames(DecisionRequest r)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(r.Snapshot["columns"]));
            return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToArray();
        }

        // ---- an accepted finding (join, part 2, item 1) ---------------------------------------------------------------

        /// <summary>The person accepted "Column has no format string" (FMT-COLUMN) on Sales[Amount]: that is a decision to leave the
        /// format blank, and Jev's column-format family must ask the declined set like every other path that writes or queues a
        /// change (m11/rule-clashes), so it neither works out a format for that column nor spends a call asking about it. Before
        /// the join the family read no acceptance at all: the column stayed on Jev's row as a suggestion the person could tick over
        /// their own decision. Jev is on and sure here (Amount is money, 0.95), and the other columns still get their lines.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_accepted_column_format_finding_stops_Jevs_format_line_and_is_never_asked_about()
        {
            using var f = await ModelAsync("ColFmtAccepted.bim");
            var oracle = await AnswerAsync(f);
            var kept = await f.Engine.WaiveFindingAsync("air", "FMT-COLUMN", "column:Sales/Amount", "Left blank on purpose: the report sets its own format.", "human");
            Assert.True(kept.Changed);

            var plan = await SettledPlanAsync(f);
            var lines = plan.Items.Where(i => i.Id.StartsWith("jev.column-format.", StringComparison.Ordinal))
                                  .SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Select(l => l.Target).ToArray();

            Assert.Contains("column:Sales/Quantity", lines);   // not vacuous: Jev still formats the columns nobody accepted
            Assert.DoesNotContain("column:Sales/Amount", lines);
            Assert.DoesNotContain(oracle.Seen.Where(r => r.PointName == Point), r => ColumnNames(r).Contains("Amount"));

            var sales = plan.Items.Single(i => i.Id == "jev.column-format.table:Sales");
            var result = await f.Engine.ModelPlanActionAsync(sales.Id, "approve", "human", sales.ChangeSet.Digest);
            Assert.True(result.Performed, result.Sentence);
            Assert.Equal("", await FormatOf(f, "column:Sales/Amount"));
        }

        /// <summary>LIVE, the same proof with the real Jev (JEV_REPLAY_LIVE=1 and a development key in SEMANTICUS_JEV_KEY, read from this
        /// process and never printed): Jev is asked about the columns nobody accepted, never about the accepted one, and the accepted
        /// column has no line.</summary>
        [JevReplayFact]
        public async Task Live_an_accepted_column_format_finding_stops_the_real_Jevs_format_line()
        {
            var key = Environment.GetEnvironmentVariable(JevDecisionOracle.KeyVariable);
            Assert.False(string.IsNullOrWhiteSpace(key), JevDecisionOracle.KeyVariable + " is not set for the live run");
            using var f = await ModelAsync("ColFmtAcceptedLive.bim");
            await f.ConsentAsync();
            using var live = new JevDecisionOracle(key, new DecisionBudget());
            var oracle = new CapturingOracle(live);
            f.Engine.UseDecisionsForTest(new DecisionService(oracle, new DecisionBudget()));
            Assert.True((await f.Engine.WaiveFindingAsync("air", "FMT-COLUMN", "column:Sales/Amount", "Left blank on purpose: the report sets its own format.", "human")).Changed);

            var plan = await SettledPlanAsync(f);
            var lines = plan.Items.Where(i => i.Id.StartsWith("jev.column-format.", StringComparison.Ordinal))
                                  .SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()).Select(l => l.Target).ToArray();

            var asked = oracle.Seen.Where(r => r.PointName == Point).SelectMany(ColumnNames).ToArray();
            Assert.NotEmpty(asked);   // the real Jev was asked about the others
            Assert.DoesNotContain("Amount", asked);
            Assert.DoesNotContain("column:Sales/Amount", lines);
            Assert.NotEmpty(lines);
        }
    }
}
