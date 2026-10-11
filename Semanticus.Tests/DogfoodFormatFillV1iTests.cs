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
    /// THE FORMAT FILL RULE, round V1i (coordinator addendum 4, 29 Sep, after the V1h FAIL). The builder's cases, one group per item.
    /// The verifier's own 22 cases (VerifyFormatFillRound13Tests) are the round's acceptance and stay as they are; these cover what they
    /// do not name.
    ///  22. What a keep's digest covers: the formula, every measure it reads (recursively), the type of every column it reads.
    ///  23. A format line is stamped with the object as it was when it was worked out; a tick or a write on an older version is refused.
    ///  24. A silent Jev never leaves a row stuck: its folder lines are brought up to date, its stale suggestion says so.
    ///  25. A row's own approve goes past a format line the same batch made unprovable, and says which.
    ///  26. A table on the one side of any relationship that holds a DateTime column is a date table.
    ///  27. The engine's own readings (a place, a non-additive dimension) veto a fill; a format row waits for a Semanticus row on its object.
    ///  28. Model column-format rules under another id are advice below severity 3; the wire says "suggestion"; a rename says so.
    /// </summary>
    public sealed class DogfoodFormatFillV1iTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public DogfoodFormatFillV1iTests(ModelTemplates templates) => _templates = templates;

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "test setup", edit);

        /// <summary>Jev answering by name, as the live service does. It can go silent or slow, and it can pick a folder.</summary>
        private sealed class ByName : IDecisionOracle
        {
            public volatile bool Silent;
            public SemaphoreSlim Hold;
            public string Folder;
            /// <summary>The format family Jev picks for every object; a whole number unless a test says otherwise.</summary>
            public string Family = "whole_number";
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
                        : plain == "format_family" ? "{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(Family) + ",\"confidence\":0.97}" : null);
                var parts = new List<string>();
                foreach (var q in request.Questions)
                {
                    if (q.Name == "display_folder") parts.Add("\"display_folder\":{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(Folder ?? DecisionRegistry.NoFolder) + ",\"confidence\":0.95}");
                    else if (q.Name == "format_family") parts.Add("\"format_family\":{\"type\":\"choice\",\"choice\":" + JsonSerializer.Serialize(Family) + ",\"confidence\":0.97}");
                }
                var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[{\"digest\":");
                sb.Append(JsonSerializer.Serialize(request.Digest)).Append(",\"point\":\"").Append(request.PointName).Append('"');
                sb.Append(",\"usage\":{\"input_tokens\":900,\"output_tokens\":40},\"answers\":{").Append(string.Join(",", parts)).Append("}}]}");
                return await new RecordedDecisionOracle(sb.ToString(), "jev-test-1").AskAsync(request, ct);
            }
        }

        private static async Task BuildJevModelAsync(DecisionFixture f)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Round14", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Qty", "Int64", "Qty", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Share", "DIVIDE ( [Orders], 30 )", "human");
            await e.CreateMeasureAsync("table:Sales", "Headline", "[Share]", "human");
        }

        // Lean suite: the plain model is built once for the class and each test opens a copy (a test that adds its own extra step still builds in full).
        private async Task<(DecisionFixture f, ByName jev)> JevModelAsync(Func<LocalEngine, Task> extra = null, string templateKey = null)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            if (extra == null || templateKey != null)
                await _templates.OpenCopyAsync(f, templateKey ?? "round14", async b => { await BuildJevModelAsync(b); if (extra != null) await extra(b.Engine); }, "Round14.bim");
            else
            {
                await BuildJevModelAsync(f);
                await extra(e);
                await f.SaveAsAsync("Round14.bim");
            }
            await f.ConsentAsync();
            var jev = new ByName();
            e.UseDecisionsForTest(new Semanticus.Engine.Decisions.DecisionService(jev, new DecisionBudget()));
            return (f, jev);
        }

        private static async Task<ModelPlanView> SettledAsync(DecisionFixture f, string door = "studio")
        {
            await f.Engine.GetModelPlanAsync();
            await f.Engine.JevSettledForTest();
            return door == "mcp" ? await McpTools.GetModelPlan(f.Engine) : await new EngineRpcTarget(f.Engine).getModelPlan();
        }

        private static ModelPlanChangeLine FormatLine(ModelPlanView plan, string target) =>
            (plan?.Items ?? Array.Empty<ModelPlanItem>())
                .Where(i => i.Id.StartsWith("jev.", StringComparison.Ordinal))
                .SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>())
                .FirstOrDefault(l => (l.Op == "set_measure_format" || l.Op == "set_format_string") && string.Equals(l.Target, target, StringComparison.OrdinalIgnoreCase));

        private static async Task RunThePlanAsync(DecisionFixture f, string door = "studio")
        {
            for (var pass = 0; pass < 4; pass++)
            {
                var plan = await SettledAsync(f, door);
                if (string.IsNullOrEmpty(plan.ReadyDigest)) return;
                var run = door == "mcp"
                    ? DoorResult.Of(await McpTools.ApplyPlanItems(f.Engine, null, plan.ReadyDigest))
                    : DoorResult.Of(await new EngineRpcTarget(f.Engine).applyPlanItems(null, "human", plan.ReadyDigest));
                if (!run.Performed) return;
            }
        }

        private static Task<string> FormatOf(DecisionFixture f, string table, string measure) =>
            f.Sessions.Require().ReadAsync(m => m.Tables[table].Measures[measure].FormatString ?? "");

        private static Task<string> DigestOf(DecisionFixture f, string table, string measure) =>
            f.Sessions.Require().ReadAsync(m => FormatFill.ComputeDigest(m.Tables[table].Measures[measure]));

        // ---- item 22: what a keep's digest covers ---------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_digest_moves_with_a_measure_it_reads_at_any_depth_and_with_the_type_of_a_column_it_reads_and_with_nothing_else()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Digest", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Qty", "Int64", "Qty", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Decimal", "Amount", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Share", "DIVIDE ( [Orders], 30 )", "human");
            await e.CreateMeasureAsync("table:Sales", "Headline", "[Share]", "human");
            await e.CreateMeasureAsync("table:Sales", "Top", "[Headline]", "human");
            await e.CreateMeasureAsync("table:Sales", "Qty Units", "SUM ( Sales[Qty] )", "human");

            var (headline, top, units, orders) = (await DigestOf(f, "Sales", "Headline"), await DigestOf(f, "Sales", "Top"),
                                                  await DigestOf(f, "Sales", "Qty Units"), await DigestOf(f, "Sales", "Orders"));

            await e.CreateMeasureAsync("table:Sales", "Unrelated", "COUNTROWS ( Sales )", "human");
            Assert.Equal(headline, await DigestOf(f, "Sales", "Headline"));   // control: an unrelated measure moves nothing

            await e.SetDaxAsync("measure:Sales/Share", "SUM ( Sales[Amount] )", "human");
            var headline2 = await DigestOf(f, "Sales", "Headline");
            Assert.NotEqual(headline, headline2);                             // a measure it reads became something else
            Assert.NotEqual(top, await DigestOf(f, "Sales", "Top"));          // and so did the measure that reads THAT one
            Assert.Equal(orders, await DigestOf(f, "Sales", "Orders"));       // a measure that reads neither did not move

            await e.SetDaxAsync("measure:Sales/Share", "SUM ( Sales[Amount] ) // money", "human");
            Assert.Equal(headline2, await DigestOf(f, "Sales", "Headline"));  // a comment is nothing

            await Mutate(f, m => m.Tables["Sales"].Columns["Qty"].DataType = DataType.Decimal);
            Assert.NotEqual(units, await DigestOf(f, "Sales", "Qty Units"));  // the column it sums changed type
            Assert.Equal(headline2, await DigestOf(f, "Sales", "Headline"));  // a column it does not read moved nothing

            await e.RenameObjectAsync("measure:Sales/Share", "Ratio", "human");
            Assert.Contains("[Ratio]", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Headline"].Expression), StringComparison.Ordinal);
            Assert.Equal(headline2, await DigestOf(f, "Sales", "Headline"));  // a measure it reads is read by what it computes, not by its name
        }

        // ---- item 23: a line is stamped when it is worked out ----------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Every_format_line_carries_the_digest_of_its_object_as_it_was_worked_out()
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                await SettledAsync(f);
                var lines = (await e.GetPlanAsync()).Items.Where(i => i.Source == "jev" && (i.Kind == "set_measure_format" || i.Kind == "set_format_string")).ToList();
                Assert.Contains(lines, l => l.Kind == "set_measure_format");
                Assert.Contains(lines, l => l.Kind == "set_format_string");
                foreach (var l in lines)
                {
                    var now = await f.Sessions.Require().ReadAsync(m => FormatFill.ComputeDigest(ObjectRefs.Resolve(m, l.ObjectRef)));
                    Assert.False(string.IsNullOrEmpty(l.WorkedOutDigest), l.ObjectRef + " carries no stamp");
                    Assert.Equal(now, l.WorkedOutDigest);
                }
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_tick_on_a_line_worked_out_for_an_older_version_is_refused_in_words_and_the_line_says_it_is_out_of_date()
        {
            var (f, jev) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                var line = FormatLine(await SettledAsync(f), "measure:Sales/Share");
                Assert.True(line != null && line.Suggestion, "precondition: Share's line is a suggestion");
                jev.Hold = new SemaphoreSlim(0);
                try
                {
                    await e.SetDaxAsync("measure:Sales/Share", "SUM ( Sales[Amount] )", "human");
                    await e.GetModelPlanAsync();
                    var view = await new EngineRpcTarget(e).setPlanItem(line.PlanItemId, null, true);
                    var item = view.Items.Single(i => i.Id == line.PlanItemId);
                    Assert.False(item.KeptByPerson, "the tick was recorded against a measure Jev never looked at");
                    Assert.NotEqual("approved", item.Status);
                    Assert.Equal(Semanticus.Engine.Decisions.JevFormatSuggestion.StaleNote, item.CheckText);
                    Assert.Contains("Not ticked", view.Note ?? "", StringComparison.Ordinal);
                    await e.ApplyPlanAsync(null, "human");
                }
                finally { jev.Hold.Release(1000); jev.Hold = null; }
                Assert.Equal("", await FormatOf(f, "Sales", "Share"));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_tick_on_a_current_line_is_recorded_against_the_digest_the_line_was_worked_out_for()
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                var line = FormatLine(await SettledAsync(f), "measure:Sales/Share");
                var view = await new EngineRpcTarget(e).setPlanItem(line.PlanItemId, null, true);
                var item = view.Items.Single(i => i.Id == line.PlanItemId);
                Assert.True(item.KeptByPerson);
                Assert.Equal(item.WorkedOutDigest, item.KeptDigest);
                await RunThePlanAsync(f);
                Assert.Equal("#,##0", await FormatOf(f, "Sales", "Share"));   // the person's explicit keep writes (V1g F1, L4)
            }
        }

        // ---- item 24: a silent Jev never leaves a row stuck ------------------------------------------------------------------

        private async Task<(DecisionFixture f, ByName jev)> FolderModelAsync() => await JevModelAsync(async e =>
        {
            var anchor = await e.CreateMeasureAsync("table:Sales", "Anchor", "COUNTROWS ( Sales )", "human");
            await e.SetMeasureFormatAsync(anchor, "#,0", "human");
            await e.SetObjectPropertyAsync(anchor, "DisplayFolder", "Counts", "human");
        });

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task With_jev_silent_the_stale_suggestion_says_so_its_tick_is_refused_and_the_rows_folder_lines_still_run()
        {
            var (f, jev) = await FolderModelAsync();
            using (f)
            {
                var e = f.Engine;
                jev.Folder = "folder:Counts";
                await e.CreateTableAsync("Nudge", "human");
                var plan = await SettledAsync(f);
                var share = FormatLine(plan, "measure:Sales/Share");
                Assert.True(share != null && share.Suggestion, "precondition: Share's line is a suggestion");
                Assert.True(plan.Items.First(i => i.Id == "jev.measures.table:Sales").ChangeSet.Lines.Any(l => l.Op == "set_display_folder"), "precondition: folder lines");

                jev.Silent = true;
                await e.SetDaxAsync("measure:Sales/Share", "SUM ( Sales[Amount] )", "human");
                plan = await SettledAsync(f);
                var row = plan.Items.First(i => i.Id == "jev.measures.table:Sales");
                var stale = FormatLine(plan, "measure:Sales/Share");
                Assert.False(row.ChangeSet.Stale, "the row is not left stuck: " + row.ChangeSet.StaleReason);
                Assert.Equal(Semanticus.Engine.Decisions.JevFormatSuggestion.StaleNote, stale.Note);
                Assert.NotEqual(share.Reason, stale.Reason);

                var view = await new EngineRpcTarget(e).setPlanItem(stale.PlanItemId, null, true);
                Assert.False(view.Items.Single(i => i.Id == stale.PlanItemId).KeptByPerson);

                await RunThePlanAsync(f);
                Assert.Equal("Counts", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Headline"].DisplayFolder ?? ""));
                Assert.Equal("", await FormatOf(f, "Sales", "Share"));
                Assert.Equal("#,##0", await FormatOf(f, "Sales", "Orders"));   // the run happened: the proved count was written
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task With_jev_silent_a_folder_line_for_a_measure_somebody_filed_since_is_dropped_never_applied_over_it()
        {
            var (f, jev) = await FolderModelAsync();
            using (f)
            {
                var e = f.Engine;
                jev.Folder = "folder:Counts";
                await e.CreateTableAsync("Nudge", "human");
                var plan = await SettledAsync(f);
                Assert.True(plan.Items.First(i => i.Id == "jev.measures.table:Sales").ChangeSet.Lines.Any(l => l.Op == "set_display_folder" && l.Target == "measure:Sales/Headline"),
                    "precondition: a folder line for Headline");
                jev.Silent = true;
                await e.SetObjectPropertyAsync("measure:Sales/Headline", "DisplayFolder", "Mine", "human");
                plan = await SettledAsync(f);
                Assert.DoesNotContain(plan.Items.SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>()),
                    l => l.Op == "set_display_folder" && l.Target == "measure:Sales/Headline");
                await RunThePlanAsync(f);
                Assert.Equal("Mine", await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Measures["Headline"].DisplayFolder ?? ""));
            }
        }

        // ---- item 25: a row's own approve goes past a format the batch made unprovable ----------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task The_best_practice_row_says_which_format_it_left_out_and_how_many_changes_it_made(string door)
        {
            // The best-practice row is a Semanticus row (Who = auto, m11/plan-rows): approving it is running the plan, which is Pro.
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("BestPractice2", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Qty", "Int64", "Qty", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Units", "SUM ( Sales[Qty] )", "human");
            await f.SaveAsAsync("BestPractice2.bim");
            f.UseNull();
            var rpc = new EngineRpcTarget(e);
            if (door == "mcp") await McpTools.ProposePlan(e, null, false, 0); else await rpc.proposePlan(null, false, 0);
            var plan = door == "mcp" ? await McpTools.GetModelPlan(e) : await rpc.getModelPlan();
            var row = plan.Items.Single(i => i.ChangeSet?.Lines?.Any(l => l.Op == "bpa_fix" && l.Target == "measure:Sales/Units") == true);
            var result = door == "mcp" ? DoorResult.Of(await McpTools.ModelPlanAction(e, row.Id, "approve", row.ChangeSet.Digest))
                                       : DoorResult.Of(await rpc.modelPlanAction(row.Id, "approve", "human", row.ChangeSet.Digest));
            Assert.True(result.Performed, result.Sentence);
            var madeCount = row.ChangeSet.Lines.Count(l => l.Kept) - 1;   // every kept line but the one format the row itself made unprovable
            Assert.True(madeCount >= 2, "precondition: the row holds more than the one format");
            Assert.Contains(madeCount + " changes were made", result.Sentence, StringComparison.Ordinal);
            Assert.Contains("1 format was left out", result.Sentence, StringComparison.Ordinal);
            Assert.Contains("Units", result.Sentence, StringComparison.Ordinal);
            Assert.Equal("", await FormatOf(f, "Sales", "Units"));
            Assert.Equal("#,0", await FormatOf(f, "Sales", "Orders"));
        }

        // ---- item 26: a table on the one side of any relationship that holds a DateTime column is a date table --------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_year_in_a_table_keyed_by_an_integer_and_holding_a_date_is_refused_and_a_quantity_beside_it_is_not()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Dates", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Qty", "Int64", "Qty", "human");
            await e.CreateColumnAsync("table:Sales", "OrderDateKey", "Int64", "OrderDateKey", "human");
            await e.CreateColumnAsync("table:Sales", "ShipDate", "DateTime", "ShipDate", "human");   // a date on the MANY side: not a date table
            await e.CreateTableAsync("DimDate", "human");
            await e.CreateColumnAsync("table:DimDate", "DateKey", "Int64", "DateKey", "human");
            await e.CreateColumnAsync("table:DimDate", "Datum", "DateTime", "Datum", "human");
            await e.CreateColumnAsync("table:DimDate", "Jaar", "Int64", "Jaar", "human");
            await e.CreateColumnAsync("table:DimDate", "Veld7", "Int64", "Veld7", "human");
            await e.SetObjectPropertyAsync("column:DimDate/Veld7", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateTableAsync("DimSize", "human");                                              // one side, no date at all
            await e.CreateColumnAsync("table:DimSize", "SizeKey", "Int64", "SizeKey", "human");
            await e.CreateColumnAsync("table:DimSize", "Stuks", "Int64", "Stuks", "human");
            await e.SetObjectPropertyAsync("column:DimSize/Stuks", "SummarizeBy", "Sum", "human");   // join: a column fill needs proof it is summed (v-rules-v1i decision 2)
            await e.CreateColumnAsync("table:Sales", "SizeRef", "Int64", "SizeRef", "human");
            await e.CreateRelationshipAsync("column:Sales/OrderDateKey", "column:DimDate/DateKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/SizeRef", "column:DimSize/SizeKey", null, true, "human");
            await e.CreateMeasureAsync("table:Sales", "Year Sum", "SUM ( DimDate[Jaar] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Qty Sum", "SUM ( Sales[Qty] )", "human");
            var (jaar, veld, qty, stuks, yearSum, qtySum) = await f.Sessions.Require().ReadAsync(m => (
                FormatFill.MayFill(m.Tables["DimDate"].Columns["Jaar"], "#,0"), FormatFill.MayFill(m.Tables["DimDate"].Columns["Veld7"], "#,0"),
                FormatFill.MayFill(m.Tables["Sales"].Columns["Qty"], "#,0"), FormatFill.MayFill(m.Tables["DimSize"].Columns["Stuks"], "#,0"),
                FormatFill.MayFill(m.Tables["Sales"].Measures["Year Sum"], "#,0"), FormatFill.MayFill(m.Tables["Sales"].Measures["Qty Sum"], "#,0")));
            Assert.False(jaar, "a year in a date dimension keyed by an integer");
            Assert.False(veld, "a neutral-named column of the same table");
            Assert.False(yearSum, "a sum of the year");
            Assert.True(qty, "control: a quantity in the fact table");
            Assert.True(stuks, "control: a one-side table with no date is not a date table");
            Assert.True(qtySum, "control: a sum of the quantity");
        }

        // ---- item 27: the engine's own readings veto a fill --------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_column_the_engine_reads_as_a_place_or_a_non_additive_dimension_is_never_filled()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Places", 1702);
            await e.CreateTableAsync("Places", "human");
            foreach (var name in new[] { "County", "Population", "Display Order", "Age", "Pin", "Country Flag Size", "Households" })
            {
                await e.CreateColumnAsync("table:Places", name, "Int64", name.Replace(" ", ""), "human");
                // The model sums every one of them on purpose, so what refuses a column here is the engine's own reading, never the
                // missing summed proof (join: a column fill needs proof it is summed, v-rules-v1i decision 2).
                await e.SetObjectPropertyAsync("column:Places/" + name, "SummarizeBy", "Sum", "human");
            }
            await e.CreateMeasureAsync("table:Places", "County Sum", "SUM ( Places[County] )", "human");
            await e.CreateMeasureAsync("table:Places", "Population Sum", "SUM ( Places[Population] )", "human");
            var may = await f.Sessions.Require().ReadAsync(m => new[] { "County", "Population", "Display Order", "Age", "Pin", "Country Flag Size", "Households" }
                .ToDictionary(n => n, n => FormatFill.MayFill(m.Tables["Places"].Columns[n], "#,0")));
            var sums = await f.Sessions.Require().ReadAsync(m => (FormatFill.MayFill(m.Tables["Places"].Measures["County Sum"], "#,0"),
                                                                  FormatFill.MayFill(m.Tables["Places"].Measures["Population Sum"], "#,0")));
            Assert.False(may["County"], "a county code (6037 would read 6,037)");
            Assert.False(may["Display Order"]);
            Assert.False(may["Age"]);
            Assert.False(may["Pin"]);
            Assert.False(may["Country Flag Size"], "a place word inside a name is the engine's own reading of a place");
            Assert.True(may["Population"], "control: a quantity");
            Assert.True(may["Households"], "control: a quantity");
            Assert.False(sums.Item1, "a sum of a county code");
            Assert.True(sums.Item2, "control: a sum of a quantity");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void A_format_row_waits_for_a_semanticus_row_that_changes_the_same_object_whatever_the_plan_order()
        {
            const string column = "column:Places/County";
            Footprint Format() => new Footprint().Write(column).Write(PlanFootprint.Revision).Read(PlanFootprint.Objects);
            Footprint Safe() => new Footprint().Write(column).Read(column).Write(PlanFootprint.Revision).Read(PlanFootprint.Objects);
            var doers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["jev.column-format.table:Places"] = PlanDoers.Semanticus, ["safefix/BestPractice"] = PlanDoers.Semanticus, ["asst/other"] = PlanDoers.You,
            };
            foreach (var formatFirst in new[] { true, false })
            {
                var rows = formatFirst
                    ? new[] { ("jev.column-format.table:Places", Format()), ("safefix/BestPractice", Safe()) }
                    : new[] { ("safefix/BestPractice", Safe()), ("jev.column-format.table:Places", Format()) };
                var waits = PlanFootprint.Waits(rows, doers).Waits;
                Assert.True(waits.TryGetValue("jev.column-format.table:Places", out var list) && list.Contains("safefix/BestPractice"),
                    "the format row waits for the Semanticus row (format row first: " + formatFirst + ")");
                Assert.False(waits.TryGetValue("safefix/BestPractice", out var back) && back.Contains("jev.column-format.table:Places"), "and not the other way round");
            }
            // A row the person does keeps the ordinary rule (plan order): the format row is first here, so the other row waits for IT.
            var byPlan = PlanFootprint.Waits(new[] { ("jev.column-format.table:Places", Format()), ("asst/other", Safe()) }, doers).Waits;
            Assert.False(byPlan.ContainsKey("jev.column-format.table:Places"));
            Assert.Contains("jev.column-format.table:Places", byPlan["asst/other"]);
        }

        // ---- item 28: another id, the wire, the words ---------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(2, true)]
        [InlineData(3, false)]
        public async Task A_models_own_column_format_rule_under_another_id_is_advice_below_severity_three_and_blocks_at_three(int severity, bool passes)
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("OwnOther", 1604);
            var t = await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync(t, "Units", "Int64", "Units", "human");
            var dt = await e.CreateTableAsync("Date", "human");
            var dkey = await e.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await e.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await e.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await e.MarkDateTableAsync(dt, "Date", "human");
            await e.LoadBpaRulesAsync("[{\"ID\":\"OWN_COLFMT\",\"Name\":\"Columns need formats\",\"Category\":\"Formatting\",\"Severity\":" + severity + ",\"Scope\":\"DataColumn\","
                + "\"Expression\":\"DataType = \\\"Int64\\\" and string.IsNullOrWhitespace(FormatString)\"}]", replace: false, "human");
            var gate = await McpTools.DeployGate(e);
            Assert.Equal(passes, gate.Pass);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_wire_says_suggestion_for_a_line_the_rule_did_not_prove_and_safe_for_one_it_did()
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                await SettledAsync(f);
                var items = (await McpTools.GetPlan(f.Engine)).Items;
                var share = items.Single(i => i.Source == "jev" && i.Kind == "set_measure_format" && i.ObjectRef == "measure:Sales/Share");
                var orders = items.Single(i => i.Source == "jev" && i.Kind == "set_measure_format" && i.ObjectRef == "measure:Sales/Orders");
                Assert.True(share.Suggestion);
                Assert.Equal("suggestion", share.Risk);
                Assert.False(orders.Suggestion);
                Assert.Equal("safe", orders.Risk);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_rename_that_voids_a_persons_tick_says_so()
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                var line = FormatLine(await SettledAsync(f), "measure:Sales/Share");
                await new EngineRpcTarget(e).setPlanItem(line.PlanItemId, null, true);
                await e.RenameObjectAsync("measure:Sales/Share", "Share Renamed", "human");
                var after = FormatLine(await SettledAsync(f), "measure:Sales/Share Renamed");
                Assert.NotNull(after);
                Assert.False(after.Kept);
                Assert.Equal(Semanticus.Engine.Decisions.JevFormatSuggestion.RenamedKeepNote, after.Note);
                await RunThePlanAsync(f);
                Assert.Equal("", await FormatOf(f, "Sales", "Share Renamed"));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_column_advice_never_says_not_proved_here_of_a_column_the_rule_proves()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Why2", 1604);
            var t = await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync(t, "Units", "Int64", "Units", "human");     // proved
            await e.CreateColumnAsync(t, "Year", "Int64", "Year", "human");       // vetoed by name
            // Both are summed on purpose, so the name is what vetoes Year (join: a column fill needs proof it is summed, decision 2).
            await e.SetObjectPropertyAsync("column:Sales/Units", "SummarizeBy", "Sum", "human");
            await e.SetObjectPropertyAsync("column:Sales/Year", "SummarizeBy", "Sum", "human");
            await e.CreateColumnAsync(t, "Price", "Decimal", "Price", "human");   // never proved
            Assert.True(await f.Sessions.Require().ReadAsync(m => FormatFill.MayFill(m.Tables["Sales"].Columns["Units"], "#,0")));
            var proved = (await McpTools.GetFixPrompt(e, "bpa", "SEM_COLUMN_NEEDS_FORMAT_STRING", "column:Sales/Units")).Prompt;
            var year = (await McpTools.GetFixPrompt(e, "bpa", "SEM_COLUMN_NEEDS_FORMAT_STRING", "column:Sales/Year")).Prompt;
            var price = (await McpTools.GetFixPrompt(e, "bpa", "SEM_COLUMN_NEEDS_FORMAT_STRING", "column:Sales/Price")).Prompt;
            Assert.DoesNotContain("not proved here", proved, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not proved here", year, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not proved here", price, StringComparison.OrdinalIgnoreCase);
        }

        // ---- the V1i verifier's decision 2 (coordinator, M11 join part 1): a column fill needs proof the column is summed ----------

        /// <summary>An Int64 identifier with a neutral name, left at the default summarize and on no relationship (a store, a branch,
        /// a route, a terminal number), looked exactly like a quantity to the rule, so Jev's whole-number line arrived approved and Run
        /// the plan wrote #,##0 on it by itself: store 1234 then read 1,234 (v-rules-v1i, section 9, decision 2; probe/store.txt).
        /// Structure cannot tell such a column from a quantity (Quantity beside them is the same shape), so a COLUMN fill now needs
        /// proof the column is summed: a SUM measure that reads it, or a summarize set to Sum on purpose. Without that the line is a
        /// suggestion the person ticks, and nothing is written by itself.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task An_integer_column_nobody_sums_is_not_filled_by_itself_and_one_a_measure_or_the_model_sums_is(string door)
        {
            string[] unsummed = { "Store", "Branch", "Route", "Terminal", "Quantity" };
            var (f, _) = await JevModelAsync(async e =>
            {
                foreach (var c in unsummed) await e.CreateColumnAsync("table:Sales", c, "Int64", c, "human");
                await e.CreateColumnAsync("table:Sales", "Pieces", "Int64", "Pieces", "human");
                await e.CreateMeasureAsync("table:Sales", "Total Pieces", "SUM ( Sales[Pieces] )", "human");   // a measure sums it
                await e.CreateColumnAsync("table:Sales", "Cartons", "Int64", "Cartons", "human");
                await e.SetObjectPropertyAsync("column:Sales/Cartons", "SummarizeBy", "Sum", "human");       // the model sums it, on purpose
            }, "unsummed");
            using (f)
            {
                var mayFill = await f.Sessions.Require().ReadAsync(m => unsummed.Concat(new[] { "Pieces", "Cartons" })
                    .ToDictionary(c => c, c => FormatFill.MayFill(m.Tables["Sales"].Columns[c], "#,0")));
                foreach (var c in unsummed) Assert.False(mayFill[c], c + " is summed by nobody, yet the rule proves it a quantity");
                Assert.True(mayFill["Pieces"], "control: a SUM measure reads Pieces");
                Assert.True(mayFill["Cartons"], "control: the model sums Cartons on purpose");

                var plan = await SettledAsync(f, door);
                foreach (var c in unsummed)
                {
                    var line = FormatLine(plan, "column:Sales/" + c);
                    Assert.NotNull(line);
                    Assert.True(line.Suggestion && !line.Kept, c + " arrived as a line Semanticus runs by itself: kept=" + line.Kept);
                }
                Assert.True(FormatLine(plan, "column:Sales/Pieces")?.Kept == true, "control: the summed column's line arrives kept");

                await RunThePlanAsync(f, door);
                var formats = await f.Sessions.Require().ReadAsync(m => unsummed.Concat(new[] { "Pieces", "Cartons" })
                    .ToDictionary(c => c, c => m.Tables["Sales"].Columns[c].FormatString ?? ""));
                foreach (var c in unsummed) Assert.True(formats[c] == "", "Run the plan wrote " + formats[c] + " on " + c + " by itself");
                Assert.Equal("#,##0", formats["Pieces"]);
                Assert.Equal("#,##0", formats["Cartons"]);
            }
        }

        // Default tier: the witness row of the sweep form, through the same body.
        [Fact]
        public Task An_integer_column_nobody_sums_is_not_filled_by_itself_and_one_a_measure_or_the_model_sums_is_sample() =>
            An_integer_column_nobody_sums_is_not_filled_by_itself_and_one_a_measure_or_the_model_sums_is("mcp");

        // ---- the V1i verifier's decision 3: a rate is offered with one decimal ------------------------------------------------------

        /// <summary>Jev's percent family took the format library's FIRST percent template, "0%", so every rate was offered as a whole
        /// percent (15.6% read 16%), one tick from the named harm. The engine's own YoY % and MoM % use "0.0%"; so does Jev's percent
        /// line now, on a measure and on a column (v-rules-v1i, section 9, decision 3).</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Jevs_percent_line_offers_a_rate_with_one_decimal_on_a_measure_and_a_column()
        {
            var (f, jev) = await JevModelAsync(e => e.CreateColumnAsync("table:Sales", "Discount Rate", "Double", "Discount Rate", "human"));
            using (f)
            {
                jev.Family = "percent";
                var plan = await SettledAsync(f);
                Assert.Equal("0.0%", FormatLine(plan, "measure:Sales/Share")?.After);
                Assert.Equal("0.0%", FormatLine(plan, "column:Sales/Discount Rate")?.After);
            }
        }

        // ---- the V1i verifier's decision 4: a type change in the same step says what happened ------------------------------------

        /// <summary>A person ticks Jev's decimal format for a Double column, and the same Apply also runs the best-practice fix that
        /// turns that column into a Decimal. The type change is part of what the column computes, so the tick is set aside and nothing
        /// is written; that stays. But the note said the column "has changed since (or another one now has its name)", when the only
        /// change was the product's own fix in this same step (v-rules-v1i, section 8 and section 9, decision 4). It now says so.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task A_tick_set_aside_by_a_type_change_in_the_same_step_says_so_and_nothing_untrue(string door)
        {
            var (f, jev) = await JevModelAsync(async e =>
            {
                await e.CreateTableAsync("Absence", "human");
                await e.CreateColumnAsync("table:Absence", "Hours", "Double", "Hours", "human");
            });
            using (f)
            {
                var e = f.Engine;
                jev.Family = "decimal";
                var rpc = new EngineRpcTarget(e);
                await rpc.proposePlan(null, true, 200);
                var line = FormatLine(await SettledAsync(f), "column:Absence/Hours");
                Assert.True(line != null && line.Suggestion, "precondition: Jev's decimal format for Hours is a suggestion");
                var typeFix = (await rpc.getPlan()).Items.SingleOrDefault(i => i.ObjectRef == "column:Absence/Hours" && i.RuleId == "AVOID_FLOATING_POINT_DATA_TYPES");
                Assert.True(typeFix != null && typeFix.Status == "approved", "precondition: the same plan turns Hours into a Decimal");
                var ticked = (await rpc.setPlanItem(line.PlanItemId, null, true)).Items.First(i => i.Id == line.PlanItemId);
                Assert.True(ticked.KeptByPerson, "precondition: the person ticked it");

                var report = door == "mcp" ? await McpTools.ApplyPlan(e, null) : await rpc.applyPlan(null);

                var format = report.Items.Single(i => i.Id == line.PlanItemId);
                Assert.Equal("skipped", format.Status);
                Assert.Equal("Decimal", await f.Sessions.Require().ReadAsync(m => m.Tables["Absence"].Columns["Hours"].DataType.ToString()));
                Assert.Equal("", await f.Sessions.Require().ReadAsync(m => m.Tables["Absence"].Columns["Hours"].FormatString ?? ""));
                Assert.DoesNotContain("changed since", format.Note, StringComparison.Ordinal);
                Assert.DoesNotContain("now has its name", format.Note, StringComparison.Ordinal);
                Assert.Contains("also changes its data type", format.Note, StringComparison.Ordinal);
            }
        }
    }
}
