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
    /// Verifier findings on m11/feedback-rules at 9a1199c0 (V1f, the full verify of THE FORMAT FILL RULE with addendum 1).
    /// Each case fails at that head on its first defect assertion, and each carries a precondition or a control, so none
    /// can pass because nothing happened.
    ///
    /// 1. A JEV LINE THE RULE STOPS PROVING STAYS APPROVED. Addendum item 1: a Jev format line FormatFill.MayFill cannot
    ///    prove arrives as a suggestion, and Run the plan never applies it. The families ask MayFill when they prepare, but
    ///    the change plan's sync then copies the OLD line's status onto the new one whenever the proposed write is the same
    ///    (LocalEngine.JevFamilies.cs, ApplyJevRunAsync: "a person's keep or drop on a line that is still proposed carries
    ///    to the same line worked out again"). It cannot tell a person's keep from the engine's own approval, so a line
    ///    approved while the object WAS provable stays approved after the model changes and the rule no longer proves it.
    ///    Run the plan then writes it, with nobody looking.
    /// 2. THE DEPLOY GATE STILL BLOCKS ON A COLUMN FORMAT THE RULE LEAVES BLANK. Addendum item 6: "A measure or column the
    ///    rule leaves blank must not block a deploy". SEM_COLUMN_NEEDS_FORMAT_STRING (severity 2, no fix) says exactly what
    ///    PROVIDE_FORMAT_STRING_FOR_MEASURES says, for a column, and it is not advice, so it blocks.
    /// 3. TWO WAYS A CALCULATION GROUP STILL SHOWS A COUNT THROUGH ITS OWN ARITHMETIC. Addendum item 2 stops a measure fill
    ///    while an item has no format string expression of its own. An item whose expression is SELECTEDMEASUREFORMATSTRING ()
    ///    has one, but it returns the measure's own format (Microsoft Learn: "the format string of the measure that is
    ///    currently in context"), so the count's #,0 is still what the item's division is shown in. And a measure that
    ///    applies an item inside CALCULATE is "CALCULATE of a count" to the proof, while its value is the item's ratio,
    ///    shown in the measure's own format.
    /// </summary>
    public sealed class VerifyFormatFillRound11Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyFormatFillRound11Tests(ModelTemplates templates) => _templates = templates;

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "verify setup", edit);

        // ---- 1. a Jev line the rule stops proving ------------------------------------------------------------------------

        /// <summary>Jev's answers by NAME, as the live service answers: asked about the same measure or column again after the
        /// model moved, it gives the same family back. The request's own digest is echoed, so the recorded oracle serves it.</summary>
        private sealed class ByName : IDecisionOracle
        {
            private readonly Func<string, string> _measure, _column;
            public ByName(Func<string, string> measure, Func<string, string> column) { _measure = measure; _column = column; }
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                // m11/jev-batch: a table's objects arrive in one request, one question each, named "<plain>:<object ref>".
                if (request.PerQuestion)
                    return Task.FromResult(BatchedByName.Reply(request, (plain, objectRef) =>
                    {
                        var byName = request.PointName == DecisionRegistry.MeasureFolderAndFormat ? _measure
                                   : request.PointName == DecisionRegistry.ColumnFormat ? _column : null;
                        var wanted = byName?.Invoke(BatchedByName.NameOf(objectRef));
                        if (wanted == null) return null;
                        return plain == "display_folder" ? "{\"type\":\"choice\",\"choice\":\"" + DecisionRegistry.NoFolder + "\",\"confidence\":0.95}"
                             : plain == "format_family" ? "{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(wanted) + ",\"confidence\":0.97}" : null;
                    }));
                string family = null;
                if (request.PointName == DecisionRegistry.MeasureFolderAndFormat && _measure != null)
                    family = _measure(request.Snapshot.TryGetValue("measure_name", out var n) ? n as string : null);
                else if (request.PointName == DecisionRegistry.ColumnFormat && _column != null)
                    family = _column(request.Snapshot.TryGetValue("column_name", out var c) ? c as string : null);
                if (family == null) return Task.FromResult(DecisionAnswers.NotDecided(request, "not asked in this case"));
                var parts = new List<string>();
                foreach (var q in request.Questions)
                {
                    if (q.Name == "display_folder") parts.Add("\"display_folder\":{\"type\":\"choice\",\"choice\":\"" + DecisionRegistry.NoFolder + "\",\"confidence\":0.95}");
                    else if (q.Name == "format_family") parts.Add("\"format_family\":{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(family) + ",\"confidence\":0.97}");
                }
                var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":");
                sb.Append(JsonSerializer.Serialize(request.Digest)).Append(",\"point\":\"").Append(request.PointName).Append('"');
                sb.Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":40},\"answers\":{").Append(string.Join(",", parts)).Append("}}]}");
                return new RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct);
            }
        }

        /// <summary>Pro, permission given, Jev answering by name. Orders is a plain count and Store a plain Int64 column with
        /// no key, relationship, sort, hierarchy or category: the rule proves both, so Jev's whole-number lines may arrive
        /// approved.</summary>
        // Lean suite: the model is built once for the class; each test opens a copy, then gives it consent and Jev's answers.
        private async Task<DecisionFixture> JevModelAsync(Func<string, string> measure, Func<string, string> column)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await _templates.OpenCopyAsync(f, "carry", BuildCarryAsync, "Carry.bim");
            await f.ConsentAsync();
            e.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(new ByName(measure, column), new DecisionBudget()));
            return f;
        }

        private static async Task BuildCarryAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Carry", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Store", "Int64", "Store", "human");
            await e.SetObjectPropertyAsync("column:Sales/Store", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
        }

        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f, string door)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return door == "mcp" ? await McpTools.GetModelPlan(f.Engine) : await new EngineRpcTarget(f.Engine).getModelPlan();
        }

        private static ModelPlanChangeLine FormatLine(ModelPlanView plan, string op, string target) =>
            (plan?.Items ?? Array.Empty<ModelPlanItem>())
                .Where(i => i.Id.StartsWith("jev.", StringComparison.Ordinal))
                .SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>())
                .FirstOrDefault(l => l.Op == op && string.Equals(l.Target, target, StringComparison.OrdinalIgnoreCase));

        /// <summary>Run the plan on the door until nothing more is ready (a row can wait on another one for a pass), with Jev
        /// settled before each read.</summary>
        private static async Task RunThePlanAsync(DecisionFixture f, string door)
        {
            for (var pass = 0; pass < 4; pass++)
            {
                var plan = await SettledPlanAsync(f, door);
                if (string.IsNullOrEmpty(plan.ReadyDigest)) return;
                var run = door == "mcp"
                    ? DoorResult.Of(await McpTools.ApplyPlanItems(f.Engine, null, plan.ReadyDigest))
                    : DoorResult.Of(await new EngineRpcTarget(f.Engine).applyPlanItems(null, "human", plan.ReadyDigest));
                if (!run.Performed) return;
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("a calculation group arrives", "studio")]
        [InlineData("a calculation group arrives", "mcp")]
        [InlineData("blank spaces are typed into its format", "studio")]
        [InlineData("blank spaces are typed into its format", "mcp")]
        [InlineData("the count becomes a ratio", "studio")]
        [InlineData("the count becomes a ratio", "mcp")]
        public async Task Verify_a_jev_measure_format_line_the_rule_no_longer_proves_is_never_run_by_the_plan(string change, string door)
        {
            using var f = await JevModelAsync(_ => "whole_number", null);
            var first = FormatLine(await SettledPlanAsync(f, door), "set_measure_format", "measure:Sales/Orders");
            Assert.True(first?.Kept == true, "precondition: while Orders is a plain count the rule proves it, so Jev's whole-number line arrives approved");

            var typed = "";
            switch (change)
            {
                case "a calculation group arrives":
                    var cg = await f.Engine.CreateCalculationGroupAsync("Views", "human");
                    await f.Engine.CreateCalculationItemAsync(cg, "Per Day", "DIVIDE ( SELECTEDMEASURE (), 30 )", "human");
                    break;
                case "blank spaces are typed into its format":
                    typed = "   ";
                    await Mutate(f, m => m.Tables["Sales"].Measures["Orders"].FormatString = typed);
                    break;
                default:
                    await f.Engine.SetDaxAsync("measure:Sales/Orders", "DIVIDE ( COUNTROWS ( Sales ), 30 )", "human");
                    break;
            }
            Assert.False(await f.Sessions.Require().ReadAsync(m => FormatFill.MayFill(m.Tables["Sales"].Measures["Orders"], "#,0")),
                $"precondition: after {change} the rule no longer proves Orders");

            var line = FormatLine(await SettledPlanAsync(f, door), "set_measure_format", "measure:Sales/Orders");
            Assert.False(line?.Kept == true,
                $"after {change}, Jev's line \"{line?.After}\" on Orders is still approved (its check reads \"{line?.Check?.Text}\"): "
                + "the change plan carried the engine's own approval over to a line the rule no longer proves");

            await RunThePlanAsync(f, door);
            Assert.Equal(typed, await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Orders"].FormatString ?? ""));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("it is set to Don't summarize", "studio")]
        [InlineData("it is set to Don't summarize", "mcp")]
        [InlineData("it becomes a hierarchy level", "studio")]
        [InlineData("it becomes a hierarchy level", "mcp")]
        public async Task Verify_a_jev_column_format_line_the_rule_no_longer_proves_is_never_run_by_the_plan(string change, string door)
        {
            using var f = await JevModelAsync(null, _ => "whole_number");
            var first = FormatLine(await SettledPlanAsync(f, door), "set_format_string", "column:Sales/Store");
            Assert.True(first?.Kept == true, "precondition: while Store is a plain Int64 quantity the rule proves it, so Jev's whole-number line arrives approved");

            // The person tells the model Store is not a quantity: a store number is an identifier, so it is not summed, or it
            // is a level people drill through.
            await Mutate(f, m =>
            {
                var t = m.Tables["Sales"];
                if (change == "it is set to Don't summarize") t.Columns["Store"].SummarizeBy = AggregateFunction.None;
                else t.AddHierarchy("Stores", null, "Store");
            });
            Assert.False(await f.Sessions.Require().ReadAsync(m => FormatFill.MayFill(m.Tables["Sales"].Columns["Store"], "#,0")),
                $"precondition: after {change} the rule no longer proves Store a quantity");

            var line = FormatLine(await SettledPlanAsync(f, door), "set_format_string", "column:Sales/Store");
            Assert.False(line?.Kept == true,
                $"after {change}, Jev's line \"{line?.After}\" on Store is still approved (its check reads \"{line?.Check?.Text}\"): "
                + "store 1042 would read 1,042");

            await RunThePlanAsync(f, door);
            Assert.Equal("", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Columns["Store"].FormatString ?? ""));
        }

        /// <summary>The other half of item 1, and the guard on any fix of the cases above: a suggestion the PERSON kept is their
        /// decision about that exact write, so it still applies after an unrelated model change. A fix that simply stops
        /// carrying a status would fail this. It passes at 9a1199c0; it is here so the fix cannot drop it.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_a_suggestion_the_person_kept_still_applies_after_an_unrelated_change(string door)
        {
            using var f = await JevModelAsync(_ => "whole_number", null);
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Share", "DIVIDE ( [Orders], 30 )", "human");
            var suggestion = FormatLine(await SettledPlanAsync(f, door), "set_measure_format", "measure:Sales/Order Share");
            Assert.True(suggestion != null && !suggestion.Kept, "precondition: the ratio's whole-number line arrives as a suggestion");
            await f.Engine.SetPlanItemAsync(suggestion.PlanItemId, null, approved: true, "human");   // the person keeps it
            await f.Engine.CreateTableAsync("Unrelated", "human");                                      // the model moves
            var line = FormatLine(await SettledPlanAsync(f, door), "set_measure_format", "measure:Sales/Order Share");
            Assert.True(line?.Kept == true, "the person's keep on a suggestion was lost when the plan was worked out again");
            await RunThePlanAsync(f, door);
            Assert.Equal(line.After, await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Order Share"].FormatString ?? ""));
        }

        /// <summary>A kept line is a decision about a write over what was there. When the person then types into the format
        /// (blank spaces here), the write the keep was for is gone: Run the plan must not put the format over their text
        /// (item 7, and brief attack 4). At 9a1199c0 the kept status is carried to the line worked out again, whose Before now
        /// reads the spaces, and Run the plan writes over them.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_a_kept_line_is_never_written_over_blank_spaces_typed_after_the_keep(string door)
        {
            using var f = await JevModelAsync(_ => "whole_number", null);
            await f.Engine.CreateMeasureAsync("table:Sales", "Order Share", "DIVIDE ( [Orders], 30 )", "human");
            var suggestion = FormatLine(await SettledPlanAsync(f, door), "set_measure_format", "measure:Sales/Order Share");
            Assert.True(suggestion != null && !suggestion.Kept, "precondition: the ratio's whole-number line arrives as a suggestion");
            await f.Engine.SetPlanItemAsync(suggestion.PlanItemId, null, approved: true, "human");
            await Mutate(f, m => m.Tables["Sales"].Measures["Order Share"].FormatString = "   ");
            await RunThePlanAsync(f, door);
            Assert.Equal("   ", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Order Share"].FormatString ?? ""));
        }

        // Default tier: the witness row of the sweep form, through the same body.
        [Fact]
        public Task Verify_a_kept_line_is_never_written_over_blank_spaces_typed_after_the_keep_sample() =>
            Verify_a_kept_line_is_never_written_over_blank_spaces_typed_after_the_keep("studio");

        // ---- 2. the deploy gate and a column the rule leaves blank --------------------------------------------------------

        /// <summary>A model the gate passes: one described measure with its own format, a date table, and one visible column
        /// with no format that the rule never fills: a Decimal amount (cents must not be lost) or the date itself (a date
        /// format can hide the time a date holds, so DATECOLUMN_FORMATSTRING's fix is advice). Its format is a choice for the
        /// person, and the addendum says that choice must not block a deploy.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("a Decimal amount", "studio")]
        [InlineData("a Decimal amount", "mcp")]
        [InlineData("a date", "studio")]
        [InlineData("a date", "mcp")]
        public async Task Verify_the_deploy_gate_does_not_block_on_a_column_format_the_rule_leaves_blank_and_still_blocks_a_real_error(string blank, string door)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Gate", 1604);
            var t = await e.CreateTableAsync("Sales", "human");
            var amount = await e.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            if (blank != "a Decimal amount") await e.SetColumnMetadataAsync(amount, true, null, null, null, "human");
            var total = await e.CreateMeasureAsync(t, "Total Amount", "SUM ( Sales[Amount] )", "human");
            await e.SetMeasureFormatAsync(total, "#,0.00", "human");
            await e.SetDescriptionAsync(total, "The sum of every sale amount.", "human");
            var dt = await e.CreateTableAsync("Date", "human");
            var dkey = await e.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await e.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            if (blank != "a date") await e.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await e.MarkDateTableAsync(dt, "Date", "human");
            if (blank == "a date")
                Assert.Contains((await e.BpaScanAsync()).Violations,
                    v => v.RuleId == "DATECOLUMN_FORMATSTRING" && v.ObjectRef == "column:Date/Date" && v.Advice && !v.CanAutoFix);
            else
                Assert.False(await f.Sessions.Require().ReadAsync(m => FormatFill.MayFill(m.Tables["Sales"].Columns["Amount"], "#,0")),
                    "precondition: the rule leaves a Decimal column's format blank");

            async Task<DeployGate> GateAsync() => door == "mcp"
                ? await McpTools.DeployGate(e)
                : await new EngineRpcTarget(e).deployGate();

            var gate = await GateAsync();
            Assert.True(gate.Pass,
                "the deploy is blocked over a column format the rule leaves blank: " + string.Join(" | ", gate.Blockers ?? Array.Empty<string>()));

            // The control: a real error still blocks. A data column with no source column cannot be refreshed.
            await Mutate(f, m => m.Tables["Sales"].AddDataColumn("Broken", null, null, DataType.Int64).IsHidden = true);
            var broken = await GateAsync();
            Assert.False(broken.Pass, "a real error (a data column with no source column) must still block the deploy");
        }

        // ---- 2b. the words queue carries every fill the rule refuses ------------------------------------------------------

        /// <summary>The rule: everything it cannot fill "becomes a choice: the advice stays, and on the Plan it goes into the
        /// Give the words to your assistant queue as choose a format for these N measures". On this branch that queue is the
        /// assistant's format row, built from FMT-MEASURE's words. Two refused fills never reach it: with Jev on, a Jev
        /// suggestion for the same measure hides the assistant's words (OneLinePerChange lets any waiting Jev line win, kept
        /// or not), and a measure whose format is only blank spaces is "no format yet" to the advice (item 7) but not to
        /// FMT-MEASURE. The Jev-off ratio is the control: the same refused fill is in the assistant's words there.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("a ratio, with Jev on")]
        [InlineData("a count whose format is blank spaces, with Jev off")]
        [InlineData("a ratio, with Jev off (control)")]
        public async Task Verify_the_assistants_words_carry_every_fill_the_rule_refuses(string what)
        {
            using var f = await JevModelAsync(_ => "whole_number", null);
            var e = f.Engine;
            await e.CreateMeasureAsync("table:Sales", "Order Share", "DIVIDE ( [Orders], 30 )", "human");
            await e.CreateMeasureAsync("table:Sales", "Spaced Orders", "COUNTROWS ( Sales )", "human");
            await Mutate(f, m => m.Tables["Sales"].Measures["Spaced Orders"].FormatString = "   ");
            if (what.Contains("Jev off")) f.UseNull();
            var refused = what.Contains("blank spaces") ? "measure:Sales/Spaced Orders" : "measure:Sales/Order Share";
            var (advice, mayFill) = await f.Sessions.Require().ReadAsync(m =>
            {
                var x = (Measure)Semanticus.Engine.ObjectRefs.Resolve(m, refused);
                return (FormatFill.HasNoFormat(x), FormatFill.MayFill(x, "#,0"));
            });
            Assert.True(advice && !mayFill, $"precondition: the rule refuses the fill on {refused} and its advice says it has no format");

            await SettledPlanAsync(f, "mcp");
            await McpTools.ProposePlan(e, null, true, 500);   // the assistant asks for its words
            var plan = await SettledPlanAsync(f, "mcp");
            // Only the assistant's rows about FORMATS count: the Describe row lists every undescribed measure too.
            static bool AboutFormats(ModelPlanItem i) =>
                i.Id.Contains("format", StringComparison.OrdinalIgnoreCase) || (i.Title ?? "").Contains("format", StringComparison.OrdinalIgnoreCase)
                || (i.Why ?? Array.Empty<string>()).Any(w => w.Contains("format", StringComparison.OrdinalIgnoreCase));
            var words = plan.Items.Where(i => i.Doer == "assistant" && AboutFormats(i)).SelectMany(i => i.Objects ?? Array.Empty<string>()).ToArray();
            Assert.True(words.Contains(refused, StringComparer.OrdinalIgnoreCase),
                $"{what}: the fill the rule refuses on {refused} is not in the assistant's words queue. Assistant rows: "
                + string.Join("; ", plan.Items.Where(i => i.Doer == "assistant").Select(i => i.Id + " (" + i.Title + ")")));
        }

        // ---- 3. a calculation group that still shows the count through its own arithmetic --------------------------------

        private static async Task<DecisionFixture> ItemModelAsync(string itemFormat)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Items", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Other", "DIVIDE ( [Orders], 7 )", "human");
            var cg = await e.CreateCalculationGroupAsync("Time Calc", "human");
            await e.CreateCalculationItemAsync(cg, "Per Day", "DIVIDE ( SELECTEDMEASURE (), 30 )", "human");
            await Mutate(f, m => ((CalculationGroupTable)m.Tables["Time Calc"]).CalculationItems["Per Day"].FormatStringExpression = itemFormat);
            return f;
        }

        private static async Task FixAllBothDoorsAsync(DecisionFixture f)
        {
            await new EngineRpcTarget(f.Engine).bpaFixAll();
            await McpTools.ApplySafeFixes(f.Engine, "bpa");
            var plan = await McpTools.ProposePlan(f.Engine, null, false, 0);
            var fixes = plan.Items.Where(i => i.Kind == "bpa_fix").Select(i => i.Id).ToArray();
            if (fixes.Length > 0) await McpTools.ApplyPlan(f.Engine, fixes);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("SELECTEDMEASUREFORMATSTRING ()")]
        [InlineData("selectedmeasureformatstring()")]
        [InlineData("IF ( ISSELECTEDMEASURE ( [Other] ), \"0.0%\", SELECTEDMEASUREFORMATSTRING () )")]
        [InlineData("SELECTEDMEASUREFORMATSTRING () & \"\"")]
        public async Task Verify_a_count_shown_through_an_item_whose_format_is_the_measures_own_gets_no_automatic_fill(string itemFormat)
        {
            // SELECTEDMEASUREFORMATSTRING () is "the format string of the measure that is currently in context" (Microsoft
            // Learn). So under Per Day, 375 orders over 30 days show in the count's own format: 12.5 before a fill, 13 after.
            // The conditional form is the common real one: another measure gets its own format, every other one passes through.
            using var f = await ItemModelAsync(itemFormat);
            Assert.False(await f.Sessions.Require().ReadAsync(m => FormatFill.MayFill(m.Tables["Sales"].Measures["Orders"], "#,0")),
                "Orders is proved fillable while the Per Day item shows its division in Orders' own format (SELECTEDMEASUREFORMATSTRING ())");
            await FixAllBothDoorsAsync(f);
            Assert.Equal("", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Orders"].FormatString ?? ""));
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Verify_a_measure_that_applies_a_calculation_item_inside_calculate_gets_no_automatic_fill()
        {
            // Every item has a format of its own, so the count itself may be filled (the control). But Orders Per Day applies
            // the Per Day item inside CALCULATE: its value is 12.5, and it is shown in its OWN format, because the item's
            // format applies to the measure a query asks for, not to one a formula wraps. The proof reads "CALCULATE of a
            // count" and fills it, so 12.5 reads 13.
            using var f = await ItemModelAsync("\"#,0.0\"");
            await f.Engine.CreateMeasureAsync("table:Sales", "Orders Per Day", "CALCULATE ( [Orders], 'Time Calc'[Name] = \"Per Day\" )", "human");
            var (orders, perDay) = await f.Sessions.Require().ReadAsync(m =>
                (FormatFill.MayFill(m.Tables["Sales"].Measures["Orders"], "#,0"), FormatFill.MayFill(m.Tables["Sales"].Measures["Orders Per Day"], "#,0")));
            Assert.False(perDay, "Orders Per Day = CALCULATE ( [Orders], 'Time Calc'[Name] = \"Per Day\" ) is proved a count, but it shows the item's ratio");
            Assert.True(orders, "control: with every item formatted, the count itself is still proved");
            await FixAllBothDoorsAsync(f);
            Assert.Equal("", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Orders Per Day"].FormatString ?? ""));
            Assert.Equal("#,0", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Orders"].FormatString ?? ""));
        }

        // ---- 4. a year or a code the structure does not read ---------------------------------------------------------------

        /// <summary>Addendum item 3 is headed "structure, not names, for keys and years", and names may only veto. Two structural
        /// signs the proof does not read: a column of the table the model marks as its date table (DataCategory "Time") is a
        /// date part, and a column another column groups by (GroupByColumns) is the identity behind a label. With a name the
        /// list does not know (年度 is Japanese for fiscal year; Acct), each is proved a quantity and filled #,0: 2024 reads
        /// 2,024, account 1042 reads 1,042. A model's own whole-number column rule is the press here; Jev plus Run the plan
        /// writes the same (probe P3). Quantity, an ordinary column of the same model, is the control.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("a year in the marked date table", "column:Dates/年度")]
        [InlineData("a code another column groups by", "column:Customers/Acct")]
        public async Task Verify_a_year_in_the_date_table_or_a_group_by_code_gets_no_automatic_fill(string what, string colRef)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Structure", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Quantity", "Int64", "Quantity", "human");
            await e.SetObjectPropertyAsync("column:Sales/Quantity", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateTableAsync("Dates", "human");
            await e.CreateColumnAsync("table:Dates", "Date", "DateTime", "Date", "human");
            await e.CreateColumnAsync("table:Dates", "年度", "Int64", "FiscalYear", "human");
            await e.SetObjectPropertyAsync("column:Dates/年度", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.MarkDateTableAsync("table:Dates", "Date", "human");
            await e.CreateTableAsync("Customers", "human");
            await e.CreateColumnAsync("table:Customers", "Customer", "String", "Customer", "human");
            await e.CreateColumnAsync("table:Customers", "Acct", "Int64", "Acct", "human");
            await e.SetObjectPropertyAsync("column:Customers/Acct", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await Mutate(f, m => m.Tables["Customers"].Columns["Customer"].GroupByColumns.Add(m.Tables["Customers"].Columns["Acct"]));
            await e.LoadBpaRulesAsync("[{\"ID\":\"OWN_COLUMN_WHOLE\",\"Name\":\"Whole-number columns\",\"Category\":\"Formatting\",\"Severity\":2,"
                + "\"Scope\":\"DataColumn\",\"Expression\":\"DataType = \\\"Int64\\\" and string.IsNullOrWhitespace(FormatString)\","
                + "\"FixExpression\":\"FormatString = \\\"#,0\\\"\"}]", replace: false, "human");

            Assert.False(await f.Sessions.Require().ReadAsync(m => FormatFill.MayFill((Column)Semanticus.Engine.ObjectRefs.Resolve(m, colRef), "#,0")),
                $"{what} ({colRef}) is proved a whole-number quantity");
            var rpc = new EngineRpcTarget(e);
            foreach (var r in new[] { colRef, "column:Sales/Quantity" })
            {
                try { await McpTools.ApplyFix(e, "bpa", "OWN_COLUMN_WHOLE", r); } catch (InvalidOperationException) { }
                try { await rpc.bpaFix("OWN_COLUMN_WHOLE", r); } catch (InvalidOperationException) { }
            }
            Assert.Equal("", await f.Sessions.Require().ReadAsync(m => ((Column)Semanticus.Engine.ObjectRefs.Resolve(m, colRef)).FormatString ?? ""));
            Assert.Equal("#,0", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Columns["Quantity"].FormatString ?? ""));
        }
    }
}
