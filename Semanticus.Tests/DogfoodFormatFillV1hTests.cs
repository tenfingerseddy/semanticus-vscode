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
    /// THE FORMAT FILL RULE, round V1h (coordinator addendum 3, 29 Sep, after the V1g FAIL). The builder's cases, one group per
    /// item. The verifier's own 29 cases (VerifyFormatFillRound12Tests) are the round's acceptance and stay as they are; these
    /// cover what they do not name.
    ///  12. A keep belongs to the object and what it computes, never to a name (a change of formula voids it, carry and write time).
    ///  13. A format line is proved against the model as it will be at the end of the batch, and follows its object through a rename.
    ///  14. Column format advice is advice on every column; the model's own severity 3 rule keeps blocking.
    ///  16. Run the plan skips a line nobody ticked and runs the rest; an assistant's approve is no keep and says so; it never erases a
    ///      person's keep; an assistant cannot rewrite a Jev format line.
    ///  17. No confidence word on a suggestion on either door.
    ///  18. No line is offered over typed blank characters.
    ///  19. The summary counts Jev lines.
    ///  21. Lows: an engine drop does not carry (a person's does), the column advice fits an integer column, the measure-saved prompt
    ///      asks the rule.
    /// </summary>
    public sealed class DogfoodFormatFillV1hTests : IClassFixture<ModelTemplates>
    {
        private readonly ModelTemplates _templates;
        public DogfoodFormatFillV1hTests(ModelTemplates templates) => _templates = templates;

        private static Task<long> Mutate(DecisionFixture f, Action<Model> edit) =>
            f.Sessions.Require().MutateAsync("human", "test setup", edit);

        /// <summary>Jev answering by name, as the live service does. It can go silent, and its confidence can be changed.</summary>
        private sealed class ByName : IDecisionOracle
        {
            public volatile bool Silent;
            public double Confidence = 0.97;
            public bool Available => true;
            public Task<DecisionAnswers> AskAsync(DecisionRequest request, CancellationToken ct)
            {
                if (Silent) return Task.FromResult(DecisionAnswers.NotDecided(request, "the service did not answer"));
                // m11/jev-batch: a table's objects arrive in one request, one question each, named "<plain>:<objectRef>".
                if (request.PerQuestion)
                    return Task.FromResult(BatchedByName.Reply(request, (plain, objectRef) =>
                        plain == "display_folder" ? "{\"type\":\"choice\",\"choice\":\"" + DecisionRegistry.NoFolder + "\",\"confidence\":0.95}"
                        : plain == "format_family" ? "{\"type\":\"choice\",\"choice\":\"whole_number\",\"confidence\":" + Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}" : null));
                var parts = new List<string>();
                foreach (var q in request.Questions)
                {
                    if (q.Name == "display_folder") parts.Add("\"display_folder\":{\"type\":\"choice\",\"choice\":\"" + DecisionRegistry.NoFolder + "\",\"confidence\":0.95}");
                    else if (q.Name == "format_family")
                        parts.Add("\"format_family\":{\"type\":\"choice\",\"choice\":\"whole_number\",\"confidence\":" + Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
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
            await e.CreateModelAsync("Round13", 1702);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Qty", "Int64", "Qty", "human");
            await e.CreateMeasureAsync("table:Sales", "Orders", "COUNTROWS ( Sales )", "human");
            await e.CreateMeasureAsync("table:Sales", "Units", "SUM ( Sales[Qty] )", "human");
            await e.CreateMeasureAsync("table:Sales", "Share", "DIVIDE ( [Orders], 30 )", "human");
            await e.CreateMeasureAsync("table:Sales", "Rate", "DIVIDE ( [Units], [Orders] )", "human");
        }

        // Lean suite: the plain model is built once for the class and each test opens a copy.
        private async Task<(DecisionFixture f, ByName jev)> JevModelAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await _templates.OpenCopyAsync(f, "round13h", BuildJevModelAsync, "Round13.bim");
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

        private static ModelPlanChangeLine FormatLine(ModelPlanView plan, string target, string op = "set_measure_format") =>
            (plan?.Items ?? Array.Empty<ModelPlanItem>())
                .Where(i => i.Id.StartsWith("jev.", StringComparison.Ordinal))
                .SelectMany(i => i.ChangeSet?.Lines ?? Array.Empty<ModelPlanChangeLine>())
                .FirstOrDefault(l => l.Op == op && string.Equals(l.Target, target, StringComparison.OrdinalIgnoreCase));

        private static async Task<List<string>> RunThePlanAsync(DecisionFixture f, string door = "studio")
        {
            var said = new List<string>();
            for (var pass = 0; pass < 4; pass++)
            {
                var plan = await SettledAsync(f, door);
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

        // ---- item 12: a keep belongs to the object and what it computes -----------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task A_persons_tick_is_void_when_the_measure_they_ticked_changes_what_it_computes(string door)
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                var share = FormatLine(await SettledAsync(f, door), "measure:Sales/Share");
                Assert.True(share != null && share.Suggestion && !share.Kept, "precondition: Share's whole-number line is a suggestion");
                await new EngineRpcTarget(e).setPlanItem(share.PlanItemId, null, true);
                await e.CreateMeasureAsync("table:Sales", "Spare", "COUNTROWS ( Sales )", "human");
                Assert.True(FormatLine(await SettledAsync(f, door), "measure:Sales/Share")?.Kept == true, "control: an unrelated change keeps the tick");

                // The same measure, now a different number: the tick was given to a ratio of orders, not to units per order.
                await Mutate(f, m => m.Tables["Sales"].Measures["Share"].Expression = "DIVIDE ( [Units], [Orders] )");
                var line = FormatLine(await SettledAsync(f, door), "measure:Sales/Share");
                Assert.NotNull(line);
                Assert.False(line.Kept, "the tick was carried onto a measure that computes something else");
                Assert.True(line.Suggestion);
                Assert.Contains("ticked an earlier version", line.Note ?? "", StringComparison.Ordinal);
                await RunThePlanAsync(f, door);
                Assert.Equal("", await FormatOf(f, "Share"));
            }
        }

        // Default tier: the witness row of the sweep form, through the same body.
        [Fact]
        public Task A_persons_tick_is_void_when_the_measure_they_ticked_changes_what_it_computes_sample() =>
            A_persons_tick_is_void_when_the_measure_they_ticked_changes_what_it_computes("studio");

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task A_tick_is_void_at_write_time_when_the_measure_changed_before_the_plan_was_worked_out_again(bool changed)
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                var share = FormatLine(await SettledAsync(f), "measure:Sales/Share");
                Assert.True(share != null && share.Suggestion, "precondition: a suggestion");
                await new EngineRpcTarget(e).setPlanItem(share.PlanItemId, null, true);
                if (changed) await Mutate(f, m => m.Tables["Sales"].Measures["Share"].Expression = "DIVIDE ( [Units], [Orders] )");
                await e.ApplyPlanAsync(null, "human");   // no re-sync in between: the write itself asks again
                Assert.Equal(changed ? "" : "#,##0", await FormatOf(f, "Share"));
            }
        }

        // ---- item 13: proof at the end of the batch, and a line follows its object -----------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_format_line_follows_its_object_through_a_rename_in_the_same_batch_and_never_lands_on_the_measure_that_took_the_name()
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                Assert.True(FormatLine(await SettledAsync(f), "measure:Sales/Orders")?.Kept == true, "precondition: Orders (a count) has an approved line");
                await e.AddPlanItemAsync("measure:Sales/Orders", "rename", "Old Orders", null, null, null, "human");
                await e.AddPlanItemAsync("measure:Sales/Share", "rename", "Orders", null, null, null, "human");
                foreach (var r in (await e.GetPlanAsync()).Items.Where(i => i.Kind == "rename"))
                    await new EngineRpcTarget(e).setPlanItem(r.Id, null, true);
                await e.ApplyPlanAsync(null, "human");
                var (old, taken) = await f.Sessions.Require().ReadAsync(m => (
                    m.Tables["Sales"].Measures["Old Orders"].FormatString ?? "", m.Tables["Sales"].Measures["Orders"].FormatString ?? ""));
                Assert.Equal("#,##0", old);   // the count keeps the format it was proved for, under its new name
                Assert.Equal("", taken);      // the ratio that took the name Orders is not the object the line was about
            }
        }

        // ---- item 14: column advice is advice; the model's own severity 3 rule still blocks ------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_models_own_severity_three_column_rule_keeps_blocking_the_deploy()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Own", 1604);
            var t = await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync(t, "Rate", "Double", "Rate", "human");
            var dt = await e.CreateTableAsync("Date", "human");
            var dkey = await e.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await e.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await e.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await e.MarkDateTableAsync(dt, "Date", "human");
            Assert.True((await McpTools.DeployGate(e)).Pass, "control: the built-in column advice does not block");
            await e.LoadBpaRulesAsync("[{\"ID\":\"OWN_COLFMT\",\"Name\":\"Columns need formats\",\"Category\":\"Formatting\",\"Severity\":3,\"Scope\":\"DataColumn\","
                + "\"Expression\":\"DataType = \\\"Double\\\" and string.IsNullOrWhitespace(FormatString)\"}]", replace: false, "human");
            Assert.False((await McpTools.DeployGate(e)).Pass, "the model's own severity 3 rule is the model's policy and keeps blocking");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_column_advice_for_an_integer_column_names_the_thousands_separator_not_the_cents()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Why", 1604);
            var t = await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync(t, "Units", "Int64", "Units", "human");
            var prompt = await McpTools.GetFixPrompt(e, "bpa", "SEM_COLUMN_NEEDS_FORMAT_STRING", "column:Sales/Units");
            Assert.Contains("thousands separator", prompt.Prompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cents", prompt.Prompt, StringComparison.OrdinalIgnoreCase);
        }

        // ---- item 16: a line nobody ticked is skipped and the rest runs; the assistant's approve says what happened ---------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("mcp")]
        [InlineData("agent pipe")]
        public async Task An_assistants_approve_of_a_suggestion_records_nothing_and_says_so(string door)
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                var share = FormatLine(await SettledAsync(f), "measure:Sales/Share");
                Assert.True(share != null && share.Suggestion, "precondition: a suggestion");
                var view = door == "mcp"
                    ? await McpTools.SetPlanItem(e, share.PlanItemId, null, true)
                    : await new EngineRpcTarget(e, "agent").setPlanItem(share.PlanItemId, null, true);
                var item = view.Items.Single(i => i.Id == share.PlanItemId);
                Assert.NotEqual("approved", item.Status);
                Assert.False(item.KeptByPerson);
                Assert.Contains("only the person", view.Note ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.False(FormatLine(await SettledAsync(f), "measure:Sales/Share")?.Kept == true, "the page must not show a line the assistant approved as kept");
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_assistants_approve_never_erases_a_persons_keep()
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                var share = FormatLine(await SettledAsync(f), "measure:Sales/Share");
                await new EngineRpcTarget(e).setPlanItem(share.PlanItemId, null, true);            // the person ticks
                var view = await McpTools.SetPlanItem(e, share.PlanItemId, null, true);              // the assistant approves too
                var item = view.Items.Single(i => i.Id == share.PlanItemId);
                Assert.True(item.KeptByPerson, "the assistant's approve erased the person's tick");
                await RunThePlanAsync(f);
                Assert.Equal("#,##0", await FormatOf(f, "Share"));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task An_assistant_cannot_rewrite_the_value_of_a_jev_format_line()
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var share = FormatLine(await SettledAsync(f), "measure:Sales/Share");
                var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SetPlanItem(f.Engine, share.PlanItemId, "#,0.00", null));
                Assert.Contains("suggested format", thrown.Message, StringComparison.OrdinalIgnoreCase);
                // and the person's own edit of the value is not refused (it is theirs to choose)
                await new EngineRpcTarget(f.Engine).setPlanItem(share.PlanItemId, "#,0.00", null);
            }
        }

        /// <summary>Run the plan is all-or-nothing for its rows, but a format line the rule refuses at the end of the batch is left out with
        /// its note and the rest goes on (formatSkips in ApplyPlanCoreAsync). No natural Run reaches it any more: a suggestion is never
        /// approved for it (an assistant's approve records nothing) and a stale row is left out before the batch, so the only way to get
        /// there is another row of the same run changing what the rule proves, and no Semanticus row does. So the words a skipped format
        /// line gets are checked here, and the run itself as far as it goes.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_skipped_format_line_is_named_in_the_sentence_as_a_format_and_never_as_a_removal_in_use()
        {
            var format = new ApplyPlanSkip { ItemId = "jev.measures.table:Sales", Title = "Tidy 2 measures in Sales", What = "Orders", Kind = "format",
                Reason = "Semanticus could no longer prove this format fits, and nobody ticked the line, so it was not written." };
            var removal = new ApplyPlanSkip { ItemId = "cleanup", Title = "Remove", What = "Old Measure", Reason = "It is in use now." };
            var said = LocalEngine.SkippedSentenceForTest(new[] { format, removal });
            Assert.Contains("1 format was left out, and the rest was applied: Orders (Semanticus could no longer prove this format fits, and nobody ticked the line, so it was not written)", said);
            Assert.Contains("1 removal was left out because it is in use now: Old Measure", said);
            Assert.DoesNotContain("Orders in use", said);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Run_the_plan_writes_every_proved_line_of_the_row_in_one_step()
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var plan = await SettledAsync(f);
                Assert.True(FormatLine(plan, "measure:Sales/Orders")?.Kept == true && FormatLine(plan, "measure:Sales/Units")?.Kept == true, "precondition");
                var said = await RunThePlanAsync(f);
                Assert.Equal("#,##0", await FormatOf(f, "Orders"));
                Assert.Equal("#,##0", await FormatOf(f, "Units"));
                Assert.Equal("", await FormatOf(f, "Share"));   // a suggestion nobody ticked is never part of the run
                Assert.Contains("Applied", string.Join(" ", said));
            }
        }

        // ---- item 17: no confidence word on a suggestion -----------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task A_suggestion_carries_no_confidence_word_on_either_door_and_a_proved_line_keeps_it(string door)
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var plan = await SettledAsync(f, door);
                var share = FormatLine(plan, "measure:Sales/Share");
                var orders = FormatLine(plan, "measure:Sales/Orders");
                Assert.True(share != null && share.Suggestion && orders != null && !orders.Suggestion, "precondition");
                Assert.True(string.IsNullOrEmpty(share.Provenance?.ConfidenceWord), "a suggestion says \"" + share.Provenance?.ConfidenceWord + "\"");
                Assert.Equal("sure", orders.Provenance?.ConfidenceWord);
                var view = await McpTools.GetPlan(f.Engine);
                var line = view.Items.Single(i => i.Id == share.PlanItemId);
                Assert.True(line.Suggestion, "the change plan line says it is a suggestion");
                Assert.Null(line.DecisionWord);
                Assert.True(view.Summary.Jev >= 3, "the summary counts Jev lines in a category of their own");
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task With_jev_silent_the_engines_approval_goes_back_to_a_suggestion_and_the_set_is_not_stale(string door)
        {
            var (f, jev) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                Assert.True(FormatLine(await SettledAsync(f, door), "measure:Sales/Orders")?.Kept == true, "precondition: an engine-approved line");
                jev.Silent = true;
                var cg = await e.CreateCalculationGroupAsync("Views", "human");
                await e.CreateCalculationItemAsync(cg, "Per Day", "DIVIDE ( SELECTEDMEASURE (), 30 )", "human");
                var plan = await SettledAsync(f, door);
                var line = FormatLine(plan, "measure:Sales/Orders");
                Assert.NotNull(line);
                Assert.False(line.Kept);
                Assert.True(line.Suggestion);
                Assert.True(string.IsNullOrEmpty(line.Provenance?.ConfidenceWord));
                Assert.Contains("could no longer prove", line.Note ?? "", StringComparison.Ordinal);
                var item = plan.Items.Single(i => i.ChangeSet?.Lines?.Any(l => l.PlanItemId == line.PlanItemId) == true);
                Assert.False(item.ChangeSet.Stale, "the row must not be stuck as out of date for want of an answer Jev cannot give");
                // the person can tick it, and the tick writes (their choice, over a model whose group would show the count through its own arithmetic)
                await new EngineRpcTarget(e).setPlanItem(line.PlanItemId, null, true);
                Assert.True(FormatLine(await SettledAsync(f, door), "measure:Sales/Orders")?.Kept == true);
            }
        }

        // ---- item 18: typed blank characters are a value ------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("   ")]
        [InlineData(" ")]
        [InlineData("\t")]
        public async Task No_jev_line_is_offered_over_typed_blank_characters_and_the_text_is_never_written_over(string typed)
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                await e.CreateColumnAsync("table:Sales", "Store", "Int64", "Store", "human");
                await e.CreateMeasureAsync("table:Sales", "Lines", "COUNTROWS ( Sales )", "human");
                await Mutate(f, m =>
                {
                    m.Tables["Sales"].Measures["Orders"].FormatString = typed;
                    m.Tables["Sales"].Columns["Store"].FormatString = typed;
                });
                var plan = await SettledAsync(f);
                Assert.Null(FormatLine(plan, "measure:Sales/Orders"));
                Assert.Null(FormatLine(plan, "column:Sales/Store", "set_format_string"));
                Assert.True(FormatLine(plan, "measure:Sales/Lines")?.Kept == true, "control: a count with no text in its format still has its line");
                await RunThePlanAsync(f);
                Assert.Equal(typed, await FormatOf(f, "Orders"));
                Assert.Equal(typed, await f.Sessions.Require().ReadAsync(m => m.Tables["Sales"].Columns["Store"].FormatString ?? ""));
            }
        }

        // ---- item 21: the lows -------------------------------------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("engine")]
        [InlineData("person")]
        public async Task Only_a_drop_somebody_made_carries_the_engines_own_drop_of_an_unsure_line_does_not(string who)
        {
            var (f, jev) = await JevModelAsync();
            using (f)
            {
                var e = f.Engine;
                jev.Confidence = 0.3;   // unsure: the line arrives dropped
                var first = FormatLine(await SettledAsync(f), "measure:Sales/Orders");
                Assert.True(first != null && !first.Kept, "precondition: an unsure line arrives dropped");
                if (who == "person")
                {
                    await new EngineRpcTarget(e).setPlanItem(first.PlanItemId, null, false);
                }
                jev.Confidence = 0.97;   // Jev now answers sure, and the rule proves the count
                await e.CreateMeasureAsync("table:Sales", "Spare", "COUNTROWS ( Sales )", "human");   // the model changes, so Jev is asked again
                var again = FormatLine(await SettledAsync(f), "measure:Sales/Orders");
                Assert.NotNull(again);
                Assert.Equal(who == "engine", again.Kept);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("count", false)]
        [InlineData("ratio", true)]
        public async Task The_measure_saved_prompt_asks_the_rule_and_carries_the_same_note(string kind, bool suggestion)
        {
            var (f, _) = await JevModelAsync();
            using (f)
            {
                var proposal = await f.Engine.ProposeMeasureDefaultsAsync(kind == "count" ? "measure:Sales/Orders" : "measure:Sales/Share");
                Assert.Equal("whole_number", proposal.ProposedFormatFamily);
                Assert.Equal(suggestion, proposal.ProposedFormatIsSuggestion);
                Assert.Equal(suggestion, proposal.ProposedFormatNote != null);
                if (suggestion) Assert.Contains("check it fits", proposal.ProposedFormatNote, StringComparison.Ordinal);
            }
        }
    }
}
