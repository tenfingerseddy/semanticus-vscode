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
    /// Verifier findings on m11/feedback-rules at 966c1577 (V1g, the full verify of THE FORMAT FILL RULE with addendum 2).
    /// Each case fails at that head on its defect assertion, after preconditions that prove the case is the one described,
    /// and each carries a control, so none can pass because nothing happened.
    ///
    /// 1. A PERSON'S KEEP FOLLOWS A NAME, NOT A MEASURE. Addendum 2 item 8: only a line the person ticked may write an
    ///    unproven format, and a re-sync carries only the person's decision. The carry matches a line by its write (item,
    ///    kind, object ref, property, value) and the text it was ticked over, and the object ref is a NAME. When the measure
    ///    the person ticked is replaced (deleted and another measure renamed into its name in one change, or two renames
    ///    applied together from the change plan), the tick is carried onto the other measure's line, which the person never
    ///    saw, and Run the plan writes a whole-number format on a ratio.
    /// 2. JEV'S SILENCE KEEPS THE ENGINE'S OWN APPROVAL. When Jev gives no answer on the re-ask after a model change (the
    ///    relay is down, the session's budget is spent), the sync leaves that object's old lines exactly as they were
    ///    (ApplyJevRunAsync, "Unanswered ... whose waiting lines stay as they are"). A line the ENGINE approved while the
    ///    rule proved it therefore stays approved, with its "sure" and its check, on a model where the rule now refuses it:
    ///    the re-sync kept the engine's approval on a line the rule refuses, which item 8 says it never does, and the page
    ///    shows "sure" and a check mark on a line the rule does not prove (item 11). Nothing wrong is written: the set is
    ///    marked out of date, Run the plan leaves the row out ("work them out again"), and the write-time check refuses the
    ///    line on apply_plan. But the row stays stuck that way for as long as Jev is silent.
    /// 3. A WHOLE-NUMBER COLUMN STILL BLOCKS THE DEPLOY. Addendum 2 item 9: "SEM_COLUMN_NEEDS_FORMAT_STRING (and any
    ///    column-format finding the rule leaves blank) is advice, like item 6". The lane makes it advice only where FormatFill
    ///    refuses the column. A visible Int64 quantity the rule WOULD fill stays a severity 2 finding with no fix, and no
    ///    shipped rule and no free path writes a column's format (INTEGER_FORMATTING is scoped to measures), so the deploy
    ///    is refused over a column format that nothing fills. Fix all only unblocks it by the side effect of
    ///    NUMERIC_COLUMN_SUMMARIZE_BY (Don't summarize makes the rule refuse the column, so the finding turns into advice).
    ///    Either repair passes this case: the finding is advice, or a proved fix fills the column so the gate has a fix.
    /// 4. A CALCULATION ITEM FORMAT EXPRESSION THAT IS ONLY A COMMENT counts as the item's own format (items 2 and 7).
    /// 5. A YEAR OR A DATE KEY IN A DATE TABLE NOBODY MARKED is proved a quantity (item 10's purpose; this case encodes the
    ///    widening the verifier recommends, for the coordinator to accept or strike).
    /// 6. AN ASSISTANT'S APPROVAL OF A SUGGESTION STOPS RUN THE PLAN FOR EVERY ROW (item 8's refusal is right; the all-or-none
    ///    run it causes, and its words, are not).
    /// 7. PROOF AT WRITE TIME IS ASKED OF A HALF-APPLIED BATCH: a format proved against a count is committed in the same batch
    ///    that makes the count a ratio (or the column Don't summarize).
    /// 1b is the silent variant of 1: the kept line is never replaced, follows the name, and apply_plan writes it.
    /// </summary>
    public sealed class VerifyFormatFillRound12Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyFormatFillRound12Tests(ModelTemplates templates) => _templates = templates;

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "verify setup", edit);

        /// <summary>Jev answering by name, as the live service does; it can be told to stop answering (the relay is down or the
        /// budget is spent), and then every ask comes back not decided.</summary>
        private sealed class ByName : IDecisionOracle
        {
            private readonly Func<string, string> _measure, _column;
            public volatile bool Silent;
            public ByName(Func<string, string> measure, Func<string, string> column) { _measure = measure; _column = column; }
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (request.PerQuestion)
                {
                    // m11/jev-batch: a table's objects arrive in one request, one question each, named "<plain>:<objectRef>"; the
                    // callbacks still get the object's own name, and an object they return null for is left unanswered.
                    if (Silent) return Task.FromResult(DecisionAnswers.NotDecided(request, "the service did not answer"));
                    var measures = request.PointName == DecisionRegistry.MeasureFolderAndFormat;
                    var pick = measures ? _measure : request.PointName == DecisionRegistry.ColumnFormat ? _column : null;
                    if (pick == null) return Task.FromResult(DecisionAnswers.NotDecided(request, "not asked in this case"));
                    return Task.FromResult(BatchedByName.Reply(request, (plain, objectRef) =>
                    {
                        var family = pick(BatchedByName.NameOf(objectRef));
                        if (family == null) return null;
                        return plain == "display_folder" ? "{\"type\":\"choice\",\"choice\":\"" + DecisionRegistry.NoFolder + "\",\"confidence\":0.95}"
                             : plain == "format_family" ? "{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(family) + ",\"confidence\":0.97}" : null;
                    }));
                }
                string family = null;
                if (request.PointName == DecisionRegistry.MeasureFolderAndFormat && _measure != null)
                    family = _measure(request.Snapshot.TryGetValue("measure_name", out var n) ? n as string : null);
                else if (request.PointName == DecisionRegistry.ColumnFormat && _column != null)
                    family = _column(request.Snapshot.TryGetValue("column_name", out var c) ? c as string : null);
                if (family == null || Silent) return Task.FromResult(DecisionAnswers.NotDecided(request, Silent ? "the service did not answer" : "not asked in this case"));
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

        // Lean suite: the model is built once for the class; each test opens a copy, then gives it consent and Jev.
        private async Task<(DecisionFixture f, ByName jev)> JevModelAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await _templates.OpenCopyAsync(f, "round12", BuildJevModelAsync, "Round12.bim");
            await f.ConsentAsync();
            var jev = new ByName(_ => "whole_number", null);
            e.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(jev, new DecisionBudget()));
            return (f, jev);
        }

        private static async Task BuildJevModelAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Round12", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Qty", "Int64", "Qty", "human");
            await e.SetObjectPropertyAsync("column:Sales/Qty", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Units", "SUM ( Sales[Qty] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Share", "DIVIDE ( [Orders], 30 )", "human");
            await e.CreateMeasureAsync("table:Sales", "Rate", "DIVIDE ( [Units], [Orders] )", "human");
        }

        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f, string door)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return door == "mcp" ? await McpTools.GetModelPlan(f.Engine) : await new EngineRpcTarget(f.Engine).getModelPlan();
        }

        private static ModelPlanChangeLine FormatLine(ModelPlanView plan, string target) =>
            (plan?.Items ?? Array.Empty<ModelPlanItem>())
                .Where(i => i.Id.StartsWith("jev.", StringComparison.Ordinal))
                .SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>())
                .FirstOrDefault(l => l.Op == "set_measure_format" && string.Equals(l.Target, target, StringComparison.OrdinalIgnoreCase));

        private static async Task<List<string>> RunThePlanAsync(DecisionFixture f, string door)
        {
            var said = new List<string>();
            for (var pass = 0; pass < 4; pass++)
            {
                var plan = await SettledPlanAsync(f, door);
                if (string.IsNullOrEmpty(plan.ReadyDigest)) break;
                var run = door == "mcp"
                    ? DoorResult.Of(await McpTools.ApplyPlanItems(f.Engine, null, plan.ReadyDigest))
                    : DoorResult.Of(await new EngineRpcTarget(f.Engine).applyPlanItems(null, "human", plan.ReadyDigest));
                said.Add(run.Sentence ?? "");
                if (!run.Performed) break;
            }
            return said;
        }

        private static Task<string> FormatOf(DecisionFixture f, string measure) =>
            f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures[measure].FormatString ?? "");

        // ---- 1. a person's keep follows a name -----------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("one change", "studio")]
        [InlineData("one change", "mcp")]
        [InlineData("a rename batch on the change plan", "studio")]
        [InlineData("a rename batch on the change plan", "mcp")]
        public async Task Verify_a_persons_keep_is_never_carried_onto_another_measure_that_takes_its_name(string how, string door)
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                await e.CreateMeasureAsync("table:Sales", "Per Week", "DIVIDE ( [Units], 7 )", "human");
                var plan0 = await SettledPlanAsync(f, door);
                var share = FormatLine(plan0, "measure:Sales/Share");
                var perWeek = FormatLine(plan0, "measure:Sales/Per Week");
                Assert.True(share != null && !share.Kept && share.Suggestion, "precondition: Share's whole-number line arrives as a suggestion");
                Assert.True(perWeek != null && !perWeek.Kept && perWeek.Suggestion, "precondition: so does Per Week's");
                await new EngineRpcTarget(e).setPlanItem(share.PlanItemId, null, true);     // the person ticks Share
                await new EngineRpcTarget(e).setPlanItem(perWeek.PlanItemId, null, true);   // and Per Week, which the swap leaves alone
                Assert.True(FormatLine(await SettledPlanAsync(f, door), "measure:Sales/Share")?.Kept == true, "control: the tick holds for Share");

                if (how == "one change")
                {
                    // Share is replaced in one change (a script, a pasted definition, an assistant's batch): the old Share goes
                    // and Rate (units per order, 2.5) takes its name.
                    await Mutate(f, m =>
                    {
                        m.Tables["Sales"].Measures["Share"].Delete();
                        m.Tables["Sales"].Measures["Rate"].Name = "Share";
                    });
                }
                else
                {
                    // The same replacement as an ordinary reviewed step: two renames on the change plan, applied together (the
                    // old Share steps aside as Share Old, and Rate takes the name).
                    await e.AddPlanItemAsync("measure:Sales/Share", "rename", "Share Old", null, null, null, "human");
                    await e.AddPlanItemAsync("measure:Sales/Rate", "rename", "Share", null, null, null, "human");
                    var renames = (await e.GetPlanAsync()).Items.Where(i => i.Kind == "rename").Select(i => i.Id).ToArray();
                    Assert.Equal(2, renames.Length);
                    foreach (var id in renames) await new EngineRpcTarget(e).setPlanItem(id, null, true);
                    var applied = await new EngineRpcTarget(e).applyPlan(renames);
                    Assert.Equal(2, applied.AppliedCount);
                }
                Assert.Equal("DIVIDE ( [Units], [Orders] )", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Share"].Expression));
                Assert.False(await f.Sessions.Require().ReadAsync(m => FormatFill.MayFill(m.Tables["Sales"].Measures["Share"], "#,0")),
                    "precondition: the measure now named Share is a ratio the rule refuses");

                var after = await SettledPlanAsync(f, door);
                var line = FormatLine(after, "measure:Sales/Share");
                Assert.True(line != null && line.Suggestion, "precondition: the measure now named Share has a suggestion line of its own");
                Assert.True(FormatLine(after, "measure:Sales/Per Week")?.Kept == true,
                    "control: a person's tick on a measure the swap does not touch still holds (a fix may not drop every keep)");
                Assert.False(line.Kept,
                    $"after {how}, the person's tick for the old Share was carried onto the measure renamed into its name ({line.After}, kept): nobody ticked a line for this measure");
                await RunThePlanAsync(f, door);
                Assert.Equal("", await FormatOf(f, "Share"));
            }
        }

        // Default tier: the witness row of the sweep form, through the same body.
        [Fact]
        public Task Verify_a_persons_keep_is_never_carried_onto_another_measure_that_takes_its_name_sample() =>
            Verify_a_persons_keep_is_never_carried_onto_another_measure_that_takes_its_name("one change", "mcp");

        // ---- 2. Jev's silence keeps the engine's own approval --------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_an_engine_approved_line_is_not_kept_when_jev_is_silent_after_the_rule_stops_proving_it(string door)
        {
            var (f, jev) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                var orders = FormatLine(await SettledPlanAsync(f, door), "measure:Sales/Orders");
                Assert.True(orders?.Kept == true && !orders.Suggestion, "precondition: while Orders is a plain count the engine approves Jev's line");

                jev.Silent = true;   // the relay stops answering
                var cg = await e.CreateCalculationGroupAsync("Views", "human");
                await e.CreateCalculationItemAsync(cg, "Per Day", "DIVIDE ( SELECTEDMEASURE (), 30 )", "human");
                Assert.False(await f.Sessions.Require().ReadAsync(m => FormatFill.MayFill(m.Tables["Sales"].Measures["Orders"], "#,0")),
                    "precondition: with Per Day showing its division in the count's own format the rule refuses Orders");

                var line = FormatLine(await SettledPlanAsync(f, door), "measure:Sales/Orders");
                Assert.False(line?.Kept == true,
                    $"with Jev silent the engine's own approval of Orders is still on the plan (kept, word {line?.Provenance?.ConfidenceWord}, "
                    + $"check {(line?.Check?.Exists == true ? "shown" : "none")}) on a model where the rule refuses it");
                Assert.True(line == null || line.Suggestion || (line.Check == null && line.Provenance?.ConfidenceWord != "sure"),
                    "item 11: a line the rule does not prove shows no \"sure\" and no check");
                await RunThePlanAsync(f, door);   // a regression guard: nothing wrong is written today either
                Assert.Equal("", await FormatOf(f, "Orders"));
            }
        }

        // ---- 4. a calculation item whose format expression is only a comment -------------------------------------------------

        /// <summary>Item 2 refuses every measure fill while an item has no format string expression of its own, and item 7 reads a
        /// format expression of blank characters as blank. An expression that is only a comment is blank to DAX too (the lexer
        /// drops comments): there is no expression, so the item has no format of its own and Per Day's division is shown in the
        /// count's format. A commented-out format is the common real form of it. The proof reads the text, not the tokens, so it
        /// calls such an item "formatted" and fills the count. The control is the same item with a real format of its own.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("// the measure's own format\n", false)]
        [InlineData("/* \"0.0%\" */", false)]
        [InlineData("-- \"0.0%\"\n", false)]
        [InlineData("\"#,0.0\" // its own format", true)]
        public async Task Verify_an_item_format_expression_that_is_only_a_comment_is_no_format_of_its_own(string itemFormat, bool fills)
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Comment", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            var cg = await e.CreateCalculationGroupAsync("Time Calc", "human");
            await e.CreateCalculationItemAsync(cg, "Per Day", "DIVIDE ( SELECTEDMEASURE (), 30 )", "human");
            await Mutate(f, m => ((CalculationGroupTable)m.Tables["Time Calc"]).CalculationItems["Per Day"].FormatStringExpression = itemFormat);
            Assert.Equal(fills, await f.Sessions.Require().ReadAsync(m => FormatFill.MayFill(m.Tables["Sales"].Measures["Orders"], "#,0")));
            await new EngineRpcTarget(e).bpaFixAll();
            Assert.Equal(fills ? "#,0" : "", await FormatOf(f, "Orders"));
        }

        // ---- 5. a year or a date key in a date table nobody marked --------------------------------------------------------

        /// <summary>Item 10 refuses any integer column of the table marked as the date table, because no name list knows every
        /// language's word for a year. A calendar table that is not marked is just as much a date table by its structure: its
        /// date column is the one side of the relationship the facts are filtered by. Its integer columns are date parts, and a
        /// name the list does not know (Année, Datumschlüssel) proves them quantities, so Jev's whole-number line arrives
        /// approved and Run the plan writes it: 2024 reads 2,024 and 20240131 reads 20,240,131. The control is an ordinary
        /// integer column of the fact table, which is still proved and filled on the same run.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_a_year_or_a_date_key_in_an_unmarked_date_table_gets_no_automatic_fill(string door)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            using (f)
            {
                var e = f.Engine;
                await e.CreateModelAsync("Calendar", 1702);
                await e.CreateTableAsync("Sales", "human");
                await e.CreateColumnAsync("table:Sales", "Date", "DateTime", "Date", "human");
                await e.CreateColumnAsync("table:Sales", "Qty", "Int64", "Qty", "human");
                await e.SetObjectPropertyAsync("column:Sales/Qty", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
                await e.CreateTableAsync("Calendar", "human");
                await e.CreateColumnAsync("table:Calendar", "Date", "DateTime", "Date", "human");
                await e.CreateColumnAsync("table:Calendar", "Année", "Int64", "Annee", "human");
                await e.SetObjectPropertyAsync("column:Calendar/Année", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
                await e.CreateColumnAsync("table:Calendar", "Datumschlüssel", "Int64", "DatumKey", "human");
                await e.SetObjectPropertyAsync("column:Calendar/Datumschlüssel", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
                await e.CreateColumnAsync("table:Calendar", "Blwyddyn", "Int64", "Blwyddyn", "human");
                await e.SetObjectPropertyAsync("column:Calendar/Blwyddyn", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)   // Welsh for year: no name list will carry it
                await e.CreateRelationshipAsync("column:Sales/Date", "column:Calendar/Date", null, true, "human");
                await f.SaveAsAsync("Calendar.bim");
                await f.ConsentAsync();
                e.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(new ByName(null, _ => "whole_number"), new DecisionBudget()));
                var (year, key, qty) = await f.Sessions.Require().ReadAsync(m => (
                    FormatFill.MayFill(m.Tables["Calendar"].Columns["Année"], "#,0") || FormatFill.MayFill(m.Tables["Calendar"].Columns["Blwyddyn"], "#,0"),
                    FormatFill.MayFill(m.Tables["Calendar"].Columns["Datumschlüssel"], "#,0"),
                    FormatFill.MayFill(m.Tables["Sales"].Columns["Qty"], "#,0")));
                Assert.True(qty, "control: an ordinary integer column of the fact table is proved");
                await RunThePlanAsync(f, door);
                var formats = await f.Sessions.Require().ReadAsync(m => (
                    Year: (m.Tables["Calendar"].Columns["Année"].FormatString ?? "") + (m.Tables["Calendar"].Columns["Blwyddyn"].FormatString ?? ""),
                    Key: m.Tables["Calendar"].Columns["Datumschlüssel"].FormatString ?? "",
                    Qty: m.Tables["Sales"].Columns["Qty"].FormatString ?? ""));
                Assert.False(string.IsNullOrEmpty(formats.Qty), "control: the run filled the ordinary integer column");
                Assert.False(year || key, $"a year (Année, Blwyddyn) or a date key (Datumschlüssel) of the calendar table is proved a quantity: year {year}, key {key}; "
                    + $"Run the plan wrote the years {formats.Year}, Datumschlüssel={formats.Key}");
                Assert.Equal(("", ""), (formats.Year, formats.Key));
            }
        }

        // ---- 6. an assistant's approval of a suggestion stops Run the plan for every row ----------------------------------------

        /// <summary>Only a person's tick may write an unproven format (item 8), so an assistant that approves a Jev suggestion
        /// through set_plan_item (or an agent connection on the pipe) does not make it the person's keep. Right. But the line
        /// is now approved, the Plan shows it kept, and Run the plan takes the row: the write-time check refuses the line, and
        /// Run the plan is all or nothing, so NOTHING runs, not the proved counts on the same row and not the column row. The
        /// sentence also says things that are not true of a ratio the rule never proved ("no longer a count", "can no longer
        /// prove", "nobody ticked the line" beside a line the page shows ticked). A guardrail must not stop the rest (item 6,
        /// Kane's rule). The control is the same run with nothing approved by the assistant.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("mcp set_plan_item", "studio")]
        [InlineData("mcp set_plan_item", "mcp")]
        [InlineData("agent pipe connection", "studio")]
        [InlineData("none (control)", "studio")]
        public async Task Verify_an_assistants_approval_of_a_suggestion_does_not_stop_run_the_plan(string approver, string door)
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                var share = FormatLine(await SettledPlanAsync(f, door), "measure:Sales/Share");
                Assert.True(share != null && share.Suggestion && !share.Kept, "precondition: Share's whole-number line is a suggestion");
                if (approver == "mcp set_plan_item") await McpTools.SetPlanItem(e, share.PlanItemId, null, true);
                else if (approver == "agent pipe connection") await new EngineRpcTarget(e, "agent").setPlanItem(share.PlanItemId, null, true);
                var said = await RunThePlanAsync(f, door);
                Assert.Equal("", await FormatOf(f, "Share"));   // never the assistant's approval alone
                Assert.True(await FormatOf(f, "Orders") == "#,##0",
                    $"with Share approved by {approver}, Run the plan did not write the proved count either: " + string.Join(" / ", said));
            }
        }

        // ---- 1b. the same keep, when Jev is silent: the old line is never replaced and follows the name --------------------------

        /// <summary>With Jev silent after the swap, the re-sync leaves the person's kept line exactly as it was, and its object
        /// ref now names the measure that took the name. Run the plan leaves the stale row out, but apply_plan (every approved
        /// line, which is also Apply approved on the Pending changes panel) writes it: the ratio gets the whole-number format the
        /// person ticked for another measure. A repair of the carry alone does not reach this path.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_a_persons_kept_line_is_never_written_to_the_measure_that_took_its_name_while_jev_is_silent(string door)
        {
            var (f, jev) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                var share = FormatLine(await SettledPlanAsync(f, door), "measure:Sales/Share");
                Assert.True(share != null && share.Suggestion && !share.Kept, "precondition: Share's line is a suggestion");
                await new EngineRpcTarget(e).setPlanItem(share.PlanItemId, null, true);   // the person ticks it, for Share
                jev.Silent = true;
                await e.AddPlanItemAsync("measure:Sales/Share", "rename", "Share Old", null, null, null, "human");
                await e.AddPlanItemAsync("measure:Sales/Rate", "rename", "Share", null, null, null, "human");
                var renames = (await e.GetPlanAsync()).Items.Where(i => i.Kind == "rename").Select(i => i.Id).ToArray();
                foreach (var id in renames) await new EngineRpcTarget(e).setPlanItem(id, null, true);
                Assert.Equal(2, (await new EngineRpcTarget(e).applyPlan(renames)).AppliedCount);
                await SettledPlanAsync(f, door);
                Assert.Equal("DIVIDE ( [Units], [Orders] )", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Share"].Expression));
                if (door == "mcp") await McpTools.ApplyPlan(e, null); else await new EngineRpcTarget(e).applyPlan(null);
                Assert.Equal("", await FormatOf(f, "Share"));        // the ratio that took the name: nobody ticked a line for it
            }
        }

        // ---- 7. proof at write time is asked of a half-applied batch ------------------------------------------------------------

        /// <summary>Item 8: every automatic format write asks the rule again "at the moment of writing, against the model as it is
        /// then". The check runs line by line inside ONE apply batch, and the batch is one commit and one undo step. When the
        /// engine's own approved whole-number line for Orders (a count) comes before an approved change that makes Orders a
        /// ratio in the same batch (an assistant's set_dax the person approved, then apply_plan with every approved line, which
        /// is also the Pending changes panel's Apply approved), the format is proved against the count, written, and then the
        /// formula becomes the ratio: the model the person gets shows 12.5 as 13, and nobody approved a whole-number format for
        /// a ratio. The same shape on a column: Jev's line for Qty, then Don't summarize in the same batch. The control is the
        /// same batch without the later change: the proved fill is still written.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("a formula change after the format line", "studio")]
        [InlineData("a formula change after the format line", "mcp")]
        [InlineData("Don't summarize after the column line", "studio")]
        [InlineData("none (control)", "studio")]
        public async Task Verify_a_format_proved_mid_batch_is_not_committed_when_the_same_batch_makes_the_rule_refuse_it(string later, string door)
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                // Jev's column family too, so Qty has an engine-approved line
                e.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(new ByName(_ => "whole_number", _ => "whole_number"), new DecisionBudget()));
                var plan = await SettledPlanAsync(f, door);
                Assert.True(FormatLine(plan, "measure:Sales/Orders")?.Kept == true, "precondition: the engine approves Jev's line for the count");
                if (later == "a formula change after the format line")
                {
                    var cp = await e.AddPlanItemAsync("measure:Sales/Orders", "set_dax", "DIVIDE ( COUNTROWS ( Sales ), 30 )", null, null, null, "agent");
                    await new EngineRpcTarget(e).setPlanItem(cp.Items.Last(i => i.Kind == "set_dax").Id, null, true);   // the person approves it
                }
                else if (later == "Don't summarize after the column line")
                    await e.AddPlanItemAsync("column:Sales/Qty", "set_summarize_by", "None", null, null, null, "agent");
                var report = door == "mcp" ? await McpTools.ApplyPlan(e, null) : await new EngineRpcTarget(e).applyPlan(null);
                var (expr, orders, qty, qtyBy, ordersMay, qtyMay) = await f.Sessions.Require().ReadAsync(m => (
                    m.Tables["Sales"].Measures["Orders"].Expression, m.Tables["Sales"].Measures["Orders"].FormatString ?? "",
                    m.Tables["Sales"].Columns["Qty"].FormatString ?? "", m.Tables["Sales"].Columns["Qty"].SummarizeBy,
                    FormatFill.ProvenWholeNumber(m.Tables["Sales"].Measures["Orders"]) != null, FormatFill.IsFillableWholeNumberColumn(m.Tables["Sales"].Columns["Qty"])));
                var what = $"committed: Orders = {expr} with format {orders} (proved now {ordersMay}); Qty summarize {qtyBy} format {qty} (proved now {qtyMay}); "
                    + string.Join(" | ", report.Items.Select(i => $"{i.Kind} {i.ObjectRef} {i.Status}"));
                if (later == "none (control)") { Assert.Equal("#,##0", orders); Assert.Equal("#,##0", qty); return; }
                if (later == "Don't summarize after the column line")
                    Assert.True(qtyMay || qty == "", "a whole-number format was committed on a column the same batch made Don't summarize: " + what);
                else
                    Assert.True(ordersMay || orders == "", "a whole-number format was committed on a measure the same batch made a ratio: " + what);
            }
        }

        /// <summary>The same half-applied batch on Free, with no Jev at all: the change plan seeds INTEGER_FORMATTING's fix for the
        /// count, an assistant adds a formula change that makes it a ratio and the person approves it, and apply_plan writes both.
        /// The bpa_fix is asked again at the moment of writing (BpaFixNoLongerApplies), which is before the formula change in the
        /// same batch. The control is the same apply without the formula change.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", true)]
        [InlineData("mcp", true)]
        [InlineData("studio", false)]
        public async Task Verify_on_free_a_seeded_fix_is_not_committed_when_the_same_batch_makes_the_count_a_ratio(string door, bool formulaChange)
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("FreeBatch", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            f.UseNull();
            var seeded = door == "mcp" ? await McpTools.ProposePlan(e, null, false, 0) : await new EngineRpcTarget(e).proposePlan(null, false, 0);
            Assert.Contains(seeded.Items, i => i.Kind == "bpa_fix" && i.ObjectRef == "measure:Sales/Orders" && i.Status == "approved");
            if (formulaChange)
            {
                var cp = await e.AddPlanItemAsync("measure:Sales/Orders", "set_dax", "DIVIDE ( COUNTROWS ( Sales ), 30 )", null, null, null, "agent");
                await new EngineRpcTarget(e).setPlanItem(cp.Items.Last(i => i.Kind == "set_dax").Id, null, true);   // the person approves it
            }
            var report = door == "mcp" ? await McpTools.ApplyPlan(e, null) : await new EngineRpcTarget(e).applyPlan(null);
            var (expr, fs, proved) = await f.Sessions.Require().ReadAsync(m => (m.Tables["Sales"].Measures["Orders"].Expression,
                m.Tables["Sales"].Measures["Orders"].FormatString ?? "", FormatFill.ProvenWholeNumber(m.Tables["Sales"].Measures["Orders"]) != null));
            var what = $"committed: Orders = {expr} with format {fs}; " + string.Join(" | ", report.Items.Select(i => $"{i.Kind} {i.RuleId} {i.Status}"));
            if (!formulaChange) { Assert.Equal("#,0", fs); return; }   // control: the proved fill still lands
            Assert.True(proved || fs == "", "a whole-number format was committed on a measure the same batch made a ratio: " + what);
        }

        // ---- 3. a whole-number column no path fills blocks the deploy --------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", "free")]
        [InlineData("mcp", "free")]
        [InlineData("studio", "pro")]
        [InlineData("mcp", "pro")]
        public async Task Verify_the_deploy_gate_does_not_block_on_a_whole_number_column_format_and_still_blocks_a_real_error(string door, string tier)
        {
            using var f = new DecisionFixture(tier == "pro" ? TestEntitlements.Pro : TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Gate12", 1604);
            var t = await e.CreateTableAsync("Sales", "human");
            var amount = await e.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            await e.SetObjectPropertyAsync(amount, "FormatString", "#,0.00", "human");
            var total = await e.CreateMeasureAsync(t, "Total Amount", "SUM ( Sales[Amount] )", "human");
            await e.SetMeasureFormatAsync(total, "#,0.00", "human");
            await e.SetDescriptionAsync(total, "The sum of every sale amount.", "human");
            var dt = await e.CreateTableAsync("Date", "human");
            var dkey = await e.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await e.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await e.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await e.MarkDateTableAsync(dt, "Date", "human");
            async Task<DeployGate> GateAsync() => door == "mcp" ? await McpTools.DeployGate(e) : await new EngineRpcTarget(e).deployGate();
            Assert.True((await GateAsync()).Pass, "control: the model passes the gate before the column is added");

            var units = await e.CreateColumnAsync(t, "Units", "Int64", "Units", "human");   // a visible whole-number quantity, no format
            var found = (await e.BpaScanAsync()).Violations.Where(v => v.ObjectRef == units).ToArray();
            Assert.Contains(found, v => v.RuleId == "SEM_COLUMN_NEEDS_FORMAT_STRING");   // precondition: the column rule raises it
            var gate = await GateAsync();
            Assert.True(gate.Pass,
                "the deploy is refused over a column format: " + string.Join(" | ", gate.Blockers ?? Array.Empty<string>())
                + " (the finding: " + string.Join(", ", found.Select(v => $"{v.RuleId} severity {v.Severity}, advice {v.Advice}, fix {v.CanAutoFix}")) + ")");

            // The control: a real error still blocks. A data column with no source column cannot be refreshed.
            await Mutate(f, m => m.Tables["Sales"].AddDataColumn("Broken", null, null, DataType.Int64).IsHidden = true);
            Assert.False((await GateAsync()).Pass, "a real error (a data column with no source column) must still block the deploy");
        }
    }
}
