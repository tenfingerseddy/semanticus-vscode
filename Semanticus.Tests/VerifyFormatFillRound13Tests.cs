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
    /// Verifier findings on m11/feedback-rules at be35be63 (V1h, the full verify of THE FORMAT FILL RULE with addendum 3).
    /// Each case fails at that head on its defect assertion, after preconditions that prove the case is the one described,
    /// and each group carries a control, so none can pass because nothing happened.
    ///
    /// 1. A KEEP OUTLIVES A CHANGE IN WHAT THE MEASURE COMPUTES when the change is outside the measure's own formula.
    ///    Addendum 3 item 12: "A KEEP BELONGS TO THE OBJECT AND WHAT IT COMPUTES ... at carry time AND at write time a keep
    ///    whose object or digest changed is void". The digest (FormatFill.ComputeDigest) is the measure's own formula read as
    ///    tokens, plus its lineage tag. A measure that reads another measure ([Share]) or sums a column computes something
    ///    else when that measure's formula changes or that column's type changes, and its digest does not move, so the
    ///    person's tick carries and Run the plan writes a whole-number format over money or a sum of decimals the person
    ///    never ticked. The Jev snapshot digest, the rule's other named option, would have moved for the column case.
    /// 2. A TICK ON A LINE WORKED OUT BEFORE THE MODEL CHANGED BINDS TO THE NEW FORMULA. While Jev has not answered again
    ///    after a change, the Pending changes panel still lists the old line and a person's tick is recorded against what the
    ///    measure computes NOW (SetPlanItemAsync takes the digest at tick time, while KeptAgainst beside it is the line's own
    ///    Before), so the tick given to a line Jev worked out for a ratio writes #,##0 over the currency sum: at once through
    ///    that row's Apply (nothing on the apply path reads the line's revision), or later through Run the plan when Jev
    ///    answers the same format. Refusing the tick in words also passes these cases.
    /// 3. A YEAR IN A DATE DIMENSION KEYED BY AN INTEGER DATE KEY is proved a quantity (item 15's purpose). The Kimball date
    ///    dimension (DateKey 20240131 on the one side, the date itself a column nobody relates) is the usual SQL shape; item
    ///    15 covers only a DateTime column on the one side, so its year gets #,##0 and 2024 reads 2,024. This case encodes
    ///    the widening the verifier recommends (a table on the one side of a relationship that holds a DateTime column is a
    ///    date table), for the coordinator to accept or strike; a neutral-named column beside the year keeps a name-list
    ///    patch from passing it.
    /// 4. JEV SILENT LEAVES A ROW WITH FOLDER LINES STUCK. Item 17: "the row is not stuck". The silent re-check stamps only the
    ///    FORMAT lines of the objects Jev did not answer for (RevalidateSilentFormatLinesAsync); their folder lines keep the
    ///    revision they were worked out at, a set is only as fresh as its oldest line, so the measures row is out of date,
    ///    Run the plan leaves it out ("work them out again", which needs the Jev that is silent), and the column row that
    ///    waits for it never runs either. Live Jev proposes folders, so this is the usual row, not a corner.
    /// 5. A COUNTY CODE THE ENGINE ITSELF CALLS A COUNTY is proved a whole-number quantity. Int64 County (a FIPS code, 6037)
    ///    carries no key, relationship, sort or category, and "county" is on no name list, so Jev's #,##0 arrives approved and
    ///    the column row writes it on its own (the engine's own CAT-GEO safe-fix row, which would mark it a County, runs in a
    ///    later pass): 6037 reads 6,037. The rule never fills "a key, ID, date key, code or flag", and the engine's own
    ///    readings (ReadinessRuleSet.GeoCategory, IsNonAdditiveDimensionName) already name it one. This case encodes a veto by
    ///    those readings, for the coordinator to accept or strike.
    /// 6. WITH JEV SILENT, A SUGGESTION WORKED OUT FOR THE OLD FORMULA IS SHOWN AS CURRENT. Item 17's re-check stamps every silent
    ///    format line current (RevalidateSilentFormatLinesAsync) but leaves an untouched suggestion exactly as it was, Jev's reading
    ///    of the OLD formula included ("it counts whole things"), so the row turns Ready and a person's tick writes #,##0 over the
    ///    money the measure now shows. At 8b4f7295 the same row stayed out of date and nothing could be written: this round made
    ///    it reachable.
    /// 7. THE BEST-PRACTICE ROW IS REFUSED WHOLE WHEN ONE OF ITS OWN FILLS STOPS BEING PROVABLE IN THE SAME BATCH. Item 13: "A fill
    ///    that stops being provable mid-batch is left out with a note; the rest of the batch goes on." The row's approve is all or
    ///    none, so when the row holds Don't summarize for a visible whole-number column and the whole-number fill for the SUM that
    ///    reads it (the usual Quantity plus Total Quantity), nothing at all is applied. Encodes item 13's letter; if the coordinator
    ///    keeps a row approve all or none, strike it.
    /// </summary>
    public sealed class VerifyFormatFillRound13Tests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public VerifyFormatFillRound13Tests(ModelTemplates templates) => _templates = templates;

        private static async Task BuildJevModelAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Round13", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Qty", "Int64", "Qty", "human");
            await e.SetObjectPropertyAsync("column:Sales/Qty", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Share", "DIVIDE ( [Orders], 30 )", "human");
            await e.CreateMeasureAsync("table:Sales", "Headline", "[Share]", "human");
        }

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "verify setup", edit);

        /// <summary>Jev answering by name, as the live service does; it can stop answering (silent) or be held (slow).</summary>
        private sealed class ByName : IDecisionOracle
        {
            public volatile bool Silent;
            public SemaphoreSlim Hold;
            public string Folder;   // a model folder id ("folder:Counts") Jev picks for every measure, or null for none
            public bool Available => true;
            public async Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                var hold = Hold;
                if (hold != null) { try { await hold.WaitAsync(TimeSpan.FromSeconds(60), ct); } catch { } }
                if (Silent) return DecisionAnswers.NotDecided(request, "the service did not answer");
                // m11/jev-batch: a table's objects arrive in one request, one question each, named "<plain>:<objectRef>".
                if (request.PerQuestion)
                    return BatchedByName.Reply(request, (plain, objectRef) =>
                        plain == "display_folder" ? "{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(Folder ?? DecisionRegistry.NoFolder) + ",\"confidence\":0.95}"
                        : plain == "format_family" ? "{\"type\":\"choice\",\"choice\":\"whole_number\",\"confidence\":0.97}" : null);
                var parts = new List<string>();
                foreach (var q in request.Questions)
                {
                    if (q.Name == "display_folder") parts.Add("\"display_folder\":{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(Folder ?? DecisionRegistry.NoFolder) + ",\"confidence\":0.95}");
                    else if (q.Name == "format_family") parts.Add("\"format_family\":{\"type\":\"choice\",\"choice\":\"whole_number\",\"confidence\":0.97}");
                }
                var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":");
                sb.Append(JsonSerializer.Serialize(request.Digest)).Append(",\"point\":\"").Append(request.PointName).Append('"');
                sb.Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":40},\"answers\":{").Append(string.Join(",", parts)).Append("}}]}");
                return await new RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct);
            }
        }

        // Lean suite: the plain model (no extra step) is built once for the class and each test opens a copy; a test that adds its own
        // extra step to the model still builds it in full.
        private async Task<(DecisionFixture f, ByName jev)> JevModelAsync(Func<LocalEngine, Task> extra = null)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            if (extra == null) await _templates.OpenCopyAsync(f, "round13", BuildJevModelAsync, "Round13.bim");
            else
            {
                await BuildJevModelAsync(f);
                await extra(e);
                await f.SaveAsAsync("Round13.bim");
            }
            await f.ConsentAsync();
            var jev = new ByName();
            e.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(jev, new DecisionBudget()));
            return (f, jev);
        }

        private static async Task<ModelPlanView> SettledPlanAsync(DecisionFixture f, string door)
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return door == "mcp" ? await McpPlanWithDetailAsync(f.Engine) : await new EngineRpcTarget(f.Engine).getModelPlan();
        }

        /// <summary>The agent door reads a plan over 40,000 characters short, each row's lines and objects left out (PlanReadShape).
        /// A 13-row plan on even a small model passes that now, so the detail is read one row at a time, as an assistant does
        /// (item = the row's id), and put back in the short read's rows.</summary>
        internal static async Task<ModelPlanView> McpPlanWithDetailAsync(IEngine engine)
        {
            var plan = await McpTools.GetModelPlan(engine);
            for (var n = 0; n < plan.Items.Length; n++)
            {
                var id = plan.Items[n].Id;
                plan.Items[n] = (await McpTools.GetModelPlan(engine, id)).Items.Single(i => i.Id == id);
            }
            return plan;
        }

        private static ModelPlanChangeLine FormatLine(ModelPlanView plan, string target) =>
            (plan?.Items ?? Array.Empty<ModelPlanItem>())
                .Where(i => i.Id.StartsWith("jev.", StringComparison.Ordinal))
                .SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>())
                .FirstOrDefault(l => (l.Op == "set_measure_format" || l.Op == "set_format_string") && string.Equals(l.Target, target, StringComparison.OrdinalIgnoreCase));

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

        private static Task<string> FormatOf(DecisionFixture f, string table, string measure) =>
            f.Sessions.Require().ReadAsync(m => m.Tables[table].Measures[measure].FormatString ?? "");

        private static async Task<bool> KeptByPersonAsync(DecisionFixture f, string planItemId) =>
            (await f.Engine.GetPlanAsync()).Items.FirstOrDefault(i => i.Id == planItemId)?.KeptByPerson == true;

        // ---- 1. a keep and what the measure computes ---------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", "a measure it reads becomes a sum of money")]
        [InlineData("mcp", "a measure it reads becomes a sum of money")]
        [InlineData("studio", "the column it sums becomes a decimal")]
        [InlineData("mcp", "the column it sums becomes a decimal")]
        [InlineData("studio", "control: an unrelated change")]
        public async Task Verify_a_keep_is_void_when_what_the_measure_computes_changes_outside_its_own_formula(string door, string change)
        {
            var viaColumn = change.StartsWith("the column", StringComparison.Ordinal);
            var (f, _) = await JevModelAsync(async e =>
            {
                // A SUM of Qty the rule does not prove (Qty is a hierarchy level), so its whole-number line is a suggestion.
                await e.CreateMeasureAsync("table:Sales", "Qty Units", "SUM ( Sales[Qty] )", "human");
            });
            using (f)
            {
                var e = f.Engine;
                await Mutate(f, m => m.Tables["Sales"].AddHierarchy("Qty Levels", null, "Qty"));
                var subject = viaColumn ? "Qty Units" : "Headline";
                var line = FormatLine(await SettledPlanAsync(f, door), "measure:Sales/" + subject);
                Assert.True(line != null && line.Suggestion && !line.Kept, $"precondition: {subject}'s whole-number line is a suggestion");
                await new EngineRpcTarget(e).setPlanItem(line.PlanItemId, null, true);   // the person ticks it
                Assert.True(await KeptByPersonAsync(f, line.PlanItemId), "precondition: the tick is recorded as the person's keep");

                if (change.StartsWith("control", StringComparison.Ordinal))
                    await e.CreateTableAsync("Unrelated", "human");
                else if (viaColumn)
                    await Mutate(f, m => m.Tables["Sales"].Columns["Qty"].DataType = DataType.Decimal);
                else
                    await e.SetDaxAsync("measure:Sales/Share", "SUM ( Sales[Amount] )", "human");

                var after = FormatLine(await SettledPlanAsync(f, door), "measure:Sales/" + subject);
                Assert.NotNull(after);
                Assert.True(await f.Sessions.Require().ReadAsync(m => !FormatFill.MayFill(m.Tables["Sales"].Measures[subject], "#,0")),
                    "precondition: the rule does not prove the measure, so only a keep could write it");
                await RunThePlanAsync(f, door);
                var written = await FormatOf(f, "Sales", subject);
                if (change.StartsWith("control", StringComparison.Ordinal))
                {
                    Assert.True(after.Kept, "control: an unrelated change keeps the person's tick");
                    Assert.Equal("#,##0", written);
                    return;
                }
                Assert.Equal("#,##0", await FormatOf(f, "Sales", "Orders"));   // the run happened: the proved count was written
                Assert.False(after.Kept, $"the person's tick was carried onto {subject}, which now computes something else ({change})");
                Assert.True(after.Suggestion && (after.Note ?? "").Contains("ticked", StringComparison.Ordinal),
                    "item 12: the line goes back to an unticked suggestion WITH A NOTE that says the tick no longer holds: " + after.Note);
                Assert.Equal("", written);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_a_keep_is_void_while_jev_is_silent_when_a_measure_it_reads_now_computes_something_else(string door)
        {
            var (f, jev) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                var line = FormatLine(await SettledPlanAsync(f, door), "measure:Sales/Headline");
                Assert.True(line != null && line.Suggestion, "precondition: Headline's line is a suggestion");
                await new EngineRpcTarget(e).setPlanItem(line.PlanItemId, null, true);
                Assert.True(await KeptByPersonAsync(f, line.PlanItemId), "precondition: the tick is recorded as the person's keep");
                jev.Silent = true;   // the relay is down, or the session's budget is spent
                await e.SetDaxAsync("measure:Sales/Share", "SUM ( Sales[Amount] )", "human");
                await SettledPlanAsync(f, door);
                _ = door == "mcp" ? await McpTools.ApplyPlan(e, null) : await new EngineRpcTarget(e).applyPlan(null);
                Assert.Equal("#,##0", await FormatOf(f, "Sales", "Orders"));   // the apply ran: the proved count was written
                Assert.Equal("", await FormatOf(f, "Sales", "Headline"));
            }
        }

        // ---- 2. a tick on a line worked out before the model changed -----------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", "the row's Apply in the window")]
        [InlineData("mcp", "the row's Apply in the window")]
        [InlineData("studio", "Run the plan after Jev answers")]
        [InlineData("mcp", "Run the plan after Jev answers")]
        public async Task Verify_a_tick_on_a_line_worked_out_before_the_model_changed_never_writes_over_the_new_formula(string door, string how)
        {
            var (f, jev) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                var line = FormatLine(await SettledPlanAsync(f, door), "measure:Sales/Share");
                Assert.True(line != null && line.Suggestion && !line.Kept, "precondition: Share's line is a suggestion");
                jev.Hold = new SemaphoreSlim(0);   // Jev is slow: the old line stays listed while it works the new one out
                try
                {
                    await e.SetDaxAsync("measure:Sales/Share", "SUM ( Sales[Amount] )", "human");
                    await e.GetModelPlanAsync();
                    var listed = (await e.GetPlanAsync()).Items.FirstOrDefault(i => i.Id == line.PlanItemId);
                    Assert.True(listed != null && listed.ProposedAtRevision < f.Sessions.Require().Revision,
                        "precondition: the Pending changes panel still lists the line worked out for the ratio");
                    // The person ticks the line they see (the Pending changes panel's tick is setPlanItem from the person's door).
                    // Refusing the tick in words is a fix these cases accept.
                    try { await new EngineRpcTarget(e).setPlanItem(line.PlanItemId, null, true); }
                    catch (InvalidOperationException refused) { Assert.False(string.IsNullOrWhiteSpace(refused.Message)); }
                    if (how.StartsWith("the row's Apply", StringComparison.Ordinal))
                        // The person's Apply on that row of Pending changes, or the assistant's apply_plan with the line's id.
                        _ = door == "mcp" ? await McpTools.ApplyPlan(e, new[] { line.PlanItemId }) : await new EngineRpcTarget(e).applyPlan(new[] { line.PlanItemId });
                }
                finally { jev.Hold.Release(1000); jev.Hold = null; }
                if (how.StartsWith("Run the plan", StringComparison.Ordinal))
                {
                    await RunThePlanAsync(f, door);
                    Assert.Equal("#,##0", await FormatOf(f, "Sales", "Orders"));   // the run happened: the proved count was written
                }
                Assert.Equal("", await FormatOf(f, "Sales", "Share"));
            }
        }

        // Default tier: the witness row of the sweep form, through the same body.
        [Fact]
        public Task Verify_a_tick_on_a_line_worked_out_before_the_model_changed_never_writes_over_the_new_formula_sample() =>
            Verify_a_tick_on_a_line_worked_out_before_the_model_changed_never_writes_over_the_new_formula("mcp", "the row's Apply in the window");

        // ---- 3. the date dimension keyed by an integer date key -------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_a_year_in_a_date_dimension_keyed_by_an_integer_date_key_gets_no_automatic_fill(string door)
        {
            var (f, _) = await JevModelAsync(async e =>
            {
                await e.CreateTableAsync("DimDate", "human");
                await e.CreateColumnAsync("table:DimDate", "DateKey", "Int64", "DateKey", "human");
                await e.CreateColumnAsync("table:DimDate", "Datum", "DateTime", "Datum", "human");
                await e.CreateColumnAsync("table:DimDate", "Jaar", "Int64", "Jaar", "human");
                await e.SetObjectPropertyAsync("column:DimDate/Jaar", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
                await e.CreateColumnAsync("table:DimDate", "Veld7", "Int64", "Veld7", "human");
                await e.SetObjectPropertyAsync("column:DimDate/Veld7", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)   // a name no list will ever carry
                await e.CreateColumnAsync("table:Sales", "OrderDateKey", "Int64", "OrderDateKey", "human");
                await e.CreateRelationshipAsync("column:Sales/OrderDateKey", "column:DimDate/DateKey", null, true, "human");
            });
            using (f)
            {
                var plan = await SettledPlanAsync(f, door);
                Assert.True(FormatLine(plan, "column:Sales/Qty")?.Kept == true, "control: an ordinary quantity in the fact table has its approved line");
                await RunThePlanAsync(f, door);
                Assert.Equal("#,##0", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Columns["Qty"].FormatString ?? ""));
                Assert.Equal("", await f.Sessions.Require().ReadAsync(m => m.Tables["DimDate"].Columns["Jaar"].FormatString ?? ""));
                Assert.Equal("", await f.Sessions.Require().ReadAsync(m => m.Tables["DimDate"].Columns["Veld7"].FormatString ?? ""));
            }
        }

        // ---- 4. Jev silent and a row that carries folder lines ------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_with_jev_silent_a_row_that_carries_folder_lines_is_not_left_stuck(string door)
        {
            var (f, jev) = await JevModelAsync(async e =>
            {
                // A measure already filed, so the model has a folder Jev can pick for the others.
                var anchor = await e.CreateMeasureAsync("table:Sales", "Anchor", "COUNTROWS ( Sales )", "human");
                await e.SetMeasureFormatAsync(anchor, "#,0", "human");
                await e.SetObjectPropertyAsync(anchor, "DisplayFolder", "Counts", "human");
            });
            using (f)
            {
                var e = f.Engine;
                jev.Folder = "folder:Counts";
                await e.CreateTableAsync("Nudge", "human");   // ask again with the folder answer
                var plan = await SettledPlanAsync(f, door);
                var row = plan.Items.FirstOrDefault(i => i.Id == "jev.measures.table:Sales");
                Assert.True(row?.ChangeSet?.Lines?.Any(l => l.Op == "set_display_folder") == true, "precondition: the measures row carries folder lines");
                Assert.True(FormatLine(plan, "column:Sales/Qty")?.Kept == true, "precondition: the whole-number quantity Qty has an approved column line");

                jev.Silent = true;
                var cg = await e.CreateCalculationGroupAsync("Views", "human");
                await e.CreateCalculationItemAsync(cg, "Per Day", "DIVIDE ( SELECTEDMEASURE (), 30 )", "human");
                var after = await SettledPlanAsync(f, door);
                var orders = FormatLine(after, "measure:Sales/Orders");
                Assert.True(orders != null && orders.Suggestion && !orders.Kept, "precondition: the engine's approval went back to a suggestion (item 17)");
                var stuck = after.Items.First(i => i.Id == "jev.measures.table:Sales");
                Assert.NotNull(stuck.ChangeSet);
                var columns = after.Items.FirstOrDefault(i => i.Id == "jev.column-format.table:Sales");
                Assert.True(columns != null && (columns.WaitsFor ?? Array.Empty<PlanWaitRef>()).All(w => w.Id == stuck.Id),
                    "precondition: the column row waits for nothing but the measures row, so only the measures row can hold it back");
                Assert.False(stuck.ChangeSet.Stale,
                    "the measures row is left out of date while Jev is silent (its folder lines were never re-checked), so Run the plan leaves it out: " + stuck.ChangeSet?.StaleReason);
                await RunThePlanAsync(f, door);
                Assert.Equal("#,##0", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Columns["Qty"].FormatString ?? ""));
            }
        }

        // ---- 5. a county code the engine itself calls a county ---------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_a_county_code_the_engine_itself_calls_a_county_gets_no_automatic_fill(string door)
        {
            var (f, _) = await JevModelAsync(async e =>
            {
                await e.CreateTableAsync("Places", "human");
                await e.CreateColumnAsync("table:Places", "County", "Int64", "County", "human");
                await e.SetObjectPropertyAsync("column:Places/County", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
                await e.CreateColumnAsync("table:Places", "Population", "Int64", "Population", "human");
                await e.SetObjectPropertyAsync("column:Places/Population", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            });
            using (f)
            {
                Assert.Equal("County", ReadinessRuleSet.GeoCategory("County"));   // the engine's own reading: a county
                var plan = await SettledPlanAsync(f, door);
                Assert.True(FormatLine(plan, "column:Places/Population")?.Kept == true, "control: a real quantity beside it has its approved line");
                await RunThePlanAsync(f, door);
                Assert.Equal("#,##0", await f.Sessions.Require().ReadAsync(m => m.Tables["Places"].Columns["Population"].FormatString ?? ""));
                Assert.Equal("", await f.Sessions.Require().ReadAsync(m => m.Tables["Places"].Columns["County"].FormatString ?? ""));
            }
        }

        // ---- 6. Jev silent: a suggestion worked out for the old formula, shown as current ----------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Verify_with_jev_silent_a_suggestion_worked_out_for_the_old_formula_is_not_shown_as_current(string door)
        {
            var (f, jev) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                var before = FormatLine(await SettledPlanAsync(f, door), "measure:Sales/Share");
                Assert.True(before != null && before.Suggestion && !before.Kept, "precondition: Share's line is a suggestion");
                var oldReading = before.Reason;
                jev.Silent = true;
                await e.SetDaxAsync("measure:Sales/Share", "SUM ( Sales[Amount] )", "human");   // a ratio becomes money
                var plan = await SettledPlanAsync(f, door);
                var row = plan.Items.First(i => i.ChangeSet?.Lines?.Any(l => l.PlanItemId == before.PlanItemId) == true);
                var line = row.ChangeSet.Lines.First(l => l.PlanItemId == before.PlanItemId);
                var shownAsCurrent = !row.ChangeSet.Stale && line.Reason == oldReading
                    && line.Note == Semanticus.Engine.Decisions.JevFormatSuggestion.Note;
                Assert.False(shownAsCurrent,
                    "the line Jev worked out for the ratio is shown as current, with its old reading (" + oldReading + "), on a measure that now sums money");
            }
        }

        // ---- 7. the best-practice row and a fill the same row stops proving --------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio", "free")]
        [InlineData("mcp", "free")]
        [InlineData("studio", "pro")]
        public async Task Verify_the_best_practice_row_applies_the_rest_when_one_of_its_fills_stops_being_provable(string door, string tier)
        {
            using var f = new DecisionFixture(tier == "pro" ? TestEntitlements.Pro : TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("BestPractice", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Qty", "Int64", "Qty", "human");
            await e.SetObjectPropertyAsync("column:Sales/Qty", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Units", "SUM ( Sales[Qty] )", "human");
            await f.SaveAsAsync("BestPractice.bim");
            f.UseNull();
            var rpc = new EngineRpcTarget(e);
            var seeded = door == "mcp" ? await McpTools.ProposePlan(e, null, false, 0) : await rpc.proposePlan(null, false, 0);
            Assert.Contains(seeded.Items, i => i.Kind == "bpa_fix" && i.ObjectRef == "column:Sales/Qty" && i.After == "None");
            Assert.Contains(seeded.Items, i => i.Kind == "bpa_fix" && i.ObjectRef == "measure:Sales/Units" && i.After == "#,0");
            var plan = door == "mcp" ? await McpTools.GetModelPlan(e) : await rpc.getModelPlan();
            var row = plan.Items.Single(i => i.ChangeSet?.Lines?.Any(l => l.Op == "bpa_fix" && l.Target == "measure:Sales/Units") == true);
            var result = door == "mcp" ? DoorResult.Of(await McpTools.ModelPlanAction(e, row.Id, "approve", row.ChangeSet.Digest))
                                       : DoorResult.Of(await rpc.modelPlanAction(row.Id, "approve", "human", row.ChangeSet.Digest));
            if (tier == "free")
            {
                // The best-practice row is a Semanticus row (Who = auto, m11/plan-rows), so approving it is running the plan, which is
                // Pro (Kane, 22 Sep: the Plan is free to read and Pro to run). Free is told so and nothing is written.
                Assert.False(result.Performed, "a Free session ran the best-practice row");
                Assert.Contains("Semanticus Pro", result.Sentence, StringComparison.Ordinal);
                var untouched = await f.Sessions.Require().ReadAsync(m => (m.Tables["Sales"].Measures["Orders"].FormatString ?? "", m.Tables["Sales"].Measures["Units"].FormatString ?? "",
                    m.Tables["Sales"].Columns["Qty"].SummarizeBy));
                Assert.Equal(("", "", AggregateFunction.Sum), untouched);   // Sum is what the fixture set on Qty on purpose (a column fill needs a summed column)
                return;
            }
            Assert.True(result.Performed, "the whole best-practice row was refused because one of its own fills stopped being provable: " + result.Sentence);
            var state = await f.Sessions.Require().ReadAsync(m => (m.Tables["Sales"].Measures["Orders"].FormatString ?? "", m.Tables["Sales"].Measures["Units"].FormatString ?? "",
                m.Tables["Sales"].Columns["Qty"].SummarizeBy));
            Assert.Equal("#,0", state.Item1);                       // the count's proved fill went in
            Assert.Equal(AggregateFunction.None, state.Item3);       // and the rest of the row
            Assert.Equal("", state.Item2);                          // the sum the same row made unprovable was left out
        }
    }

    /// <summary>Test helper for the by-name oracles of the format-fill test classes (m11/jev-batch). A batched request (one per table,
    /// PerQuestion) has one question for each object, named "&lt;plain question&gt;:&lt;objectRef&gt;" (for example
    /// "format_family:measure:Sales/Orders"); this composes the one reply a service would send, question by question, from a
    /// callback that gives the answer JSON for a plain question and the object ref it is about, or null to leave it unanswered.</summary>
    internal static class BatchedByName
    {
        public static DecisionAnswers Reply(DecisionRequest request, Func<string, string, string> answerFor)
        {
            var parts = new List<string>();
            foreach (var q in request.Questions)
            {
                var at = q.Name.IndexOf(':');
                if (at < 0) continue;
                var json = answerFor(q.Name.Substring(0, at), q.Name.Substring(at + 1));
                if (json != null) parts.Add(JsonSerializer.Serialize(q.Name) + ":" + json);
            }
            if (parts.Count == 0) return DecisionAnswers.NotDecided(request, "not asked in this case");
            return DecisionAnswerReader.Read(request, JsonDocument.Parse("{" + string.Join(",", parts) + "}").RootElement, "jev-test-1", 900, 40);
        }

        /// <summary>The name after the table in an object ref ("measure:Sales/Total Sales" gives "Total Sales").</summary>
        public static string NameOf(string objectRef) => objectRef.Substring(objectRef.IndexOf('/') + 1);
    }
}
