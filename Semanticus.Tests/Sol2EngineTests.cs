// Sol 6.1's review of the M11 delta d141ce01..b118c6d9 (1 Oct), the engine findings, lane m11/sol2-engine. Each of A1, A2 and B1 is the SECOND
// strike of an earlier finding (1.3, 2.3, 4.3), so the facts below are written against the class and not only against the path Sol walked.
// (B5 is in AskDataTests.Sol2Engine.cs.) Every fact failed first, on the build at b118c6d9.
//   A1  a Plan refresh must never delete a line somebody wrote, whatever the family offers next
//   A2  an undone permission must stay undone however another model's open lands on an undo that is already running
//   B1  a copy of a bundled rule is the bundled rule only when it BEHAVES the same, not only when its expression reads the same

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.Decisions;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class Sol2EngineTests
    {
        // ---- A1: a line somebody wrote is never removed by a family sync ------------------------------------------------------------

        internal static string SummarizeFixture(IEnumerable<(string Digest, string Choice, double Confidence)> rows)
        {
            var sb = new StringBuilder("{\"servedModelVersion\":\"jev-test-1\",\"entries\":[");
            var first = true;
            foreach (var (digest, choice, confidence) in rows)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"digest\":").Append(JsonSerializer.Serialize(digest))
                  .Append(",\"point\":\"column.summarize\"")
                  .Append(",\"usage\":{\"input_tokens\":500,\"output_tokens\":20}")
                  .Append(",\"answers\":{\"summarize_by\":{\"type\":\"choice\",\"choice\":").Append(JsonSerializer.Serialize(choice))
                  .Append(",\"confidence\":").Append(confidence.ToString(CultureInfo.InvariantCulture)).Append("}}}");
            }
            return sb.Append("]}").ToString();
        }

        internal const string Words = "Money in, as finance counts it.";

        /// <summary>A Pro model whose only Jev family is column.summarize, with its Total Amount description row written over by a person
        /// (or the assistant). Returns the fixture, the engine and the id of the authored line.</summary>
        internal static async Task<(DecisionFixture F, LocalEngine E, string LineId)> AuthoredDescriptionAsync(string file, string origin)
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await f.SaveAsAsync(file);
            f.UseNull();
            await f.ConsentAsync();
            foreach (var other in new[] { DecisionRegistry.MeasureFolderAndFormat, DecisionRegistry.ColumnFormat, DecisionRegistry.ColumnCategory,
                                          DecisionRegistry.RelationshipJoin, DecisionRegistry.DateTable, DecisionRegistry.AiSchema })
                await e.SetDecisionPointEnabledAsync(other, false, "human");
            var prepared = await f.Sessions.Current.ReadAsync(m => ColumnSummarizeFamily.Prepare(m, null));
            var ask = prepared.Asks.Single(a => a.TableName == "Sales" && a.ColumnName == "Amount");
            e.UseDecisionsForTest(new DecisionService(new ByObjectRecordedOracle(SummarizeFixture(new[] { (ask.Ref, "sum", 0.95) }), "jev-test-1"), new DecisionBudget()));
            await e.GetModelPlanAsync();
            await e.JevSettledForTest();
            var line = DescriptionLine(await e.GetModelPlanAsync());
            await e.SetPlanItemAsync(line.PlanItemId, Words, null, origin);
            return (f, e, line.PlanItemId);
        }

        internal static ModelPlanChangeLine DescriptionLine(ModelPlanView p) => p.Items.Single(i => i.Id == "jev.column-summarize.table:Sales")
            .ChangeSet.Lines.Single(l => l.Op == "set_description" && l.Target == "measure:Sales/Total Amount");

        internal static async Task<ChangeItem[]> WaitingWordsAsync(LocalEngine e) =>
            (await e.GetPlanAsync()).Items.Where(i => i.After == Words).ToArray();

        // Lean suite (8 Oct): the default run keeps this row; every row is the sweep.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("agent")]
        public Task A1_an_authored_description_stays_when_the_measure_is_made_elsewhere_and_the_family_stops_offering_it_sample(string origin) => A1_an_authored_description_stays_when_the_measure_is_made_elsewhere_and_the_family_stops_offering_it(origin);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("agent")]
        [InlineData("human")]
        public async Task A1_an_authored_description_stays_when_the_measure_is_made_elsewhere_and_the_family_stops_offering_it(string origin)
        {
            var (f, e, _) = await AuthoredDescriptionAsync("Sol2A1-" + origin + ".bim", origin);
            using (f)
            {
                // Before the row is approved the assistant makes the same measure itself, and leaves its description blank.
                await e.CreateMeasureAsync("table:Sales", "Total Amount", "SUM ( 'Sales'[Amount] )", "agent");
                await e.GetModelPlanAsync();
                await e.JevSettledForTest();
                await e.GetModelPlanAsync();
                await e.JevSettledForTest();

                var kept = await WaitingWordsAsync(e);
                Assert.True(kept.Length == 1, "the person's words were removed from the Plan by a refresh (" + kept.Length + " lines carry them)");
                Assert.NotEqual("approved", kept[0].Status);   // a held line waits for a fresh tick: the measure is not the one it was written for
                Assert.False(string.IsNullOrWhiteSpace(kept[0].Reason), "a held line says why it is held");
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A1_a_held_authored_line_does_not_apply_over_a_description_somebody_else_wrote_since()
        {
            var (f, e, _) = await AuthoredDescriptionAsync("Sol2A1b.bim", "human");
            using (f)
            {
                await e.CreateMeasureAsync("table:Sales", "Total Amount", "SUM ( 'Sales'[Amount] )", "agent");
                await e.GetModelPlanAsync();
                await e.JevSettledForTest();
                await e.GetModelPlanAsync();
                var held = (await WaitingWordsAsync(e)).Single();
                await e.SetObjectPropertyAsync("measure:Sales/Total Amount", "Description", "Somebody else's words.", "human");
                // The edit starts a background sync that holds the line again for its new reason; let it land before the tick, or it
                // lands after it and takes the tick back, and the apply then finds nothing to skip (v-sf-gate-2: a test race, not lost work).
                await e.JevSettledForTest();

                await e.SetPlanItemAsync(held.Id, null, true, "human");
                var report = await e.ApplyPlanAsync(new[] { held.Id }, "human");

                Assert.Equal("Somebody else's words.", (await e.GetObjectAsync("measure:Sales/Total Amount")).Properties["description"]?.ToString());
                Assert.Equal(1, report.SkippedCount);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A1_a_held_authored_line_that_still_fits_the_measure_can_be_ticked_and_applied()
        {
            var (f, e, _) = await AuthoredDescriptionAsync("Sol2A1c.bim", "human");
            using (f)
            {
                await e.CreateMeasureAsync("table:Sales", "Total Amount", "SUM ( 'Sales'[Amount] )", "agent");
                await e.GetModelPlanAsync();
                await e.JevSettledForTest();
                await e.GetModelPlanAsync();
                var held = (await WaitingWordsAsync(e)).Single();

                await e.SetPlanItemAsync(held.Id, null, true, "human");
                var report = await e.ApplyPlanAsync(new[] { held.Id }, "human");

                Assert.Equal(1, report.AppliedCount);
                Assert.Equal(Words, (await e.GetObjectAsync("measure:Sales/Total Amount")).Properties["description"]?.ToString());
            }
        }

        private static async Task<ChangeItem> TickedHeldLineAsync(LocalEngine e)
        {
            await e.CreateMeasureAsync("table:Sales", "Total Amount", "SUM ( 'Sales'[Amount] )", "agent");
            await e.GetModelPlanAsync();
            await e.JevSettledForTest();
            await e.GetModelPlanAsync();
            await e.JevSettledForTest();
            var held = (await WaitingWordsAsync(e)).Single();
            await e.SetPlanItemAsync(held.Id, null, true, "human");
            Assert.Equal("approved", (await WaitingWordsAsync(e)).Single().Status);   // precondition: the person's tick
            return held;
        }

        /// <summary>Every edit works the family's lines out again, and the sync holds a held line again. When the reason it is held for has not
        /// changed, the person's tick stays (v-sf-gate-2, for the join): before, each sync took it back, so a held line could not be applied
        /// while syncs kept running.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A1_a_tick_on_a_held_line_stays_when_a_later_sync_holds_it_again_for_the_same_reason()
        {
            var (f, e, _) = await AuthoredDescriptionAsync("Sol2A1d.bim", "human");
            using (f)
            {
                var held = await TickedHeldLineAsync(e);

                await e.CreateTableAsync("Other", "human");   // an edit that has nothing to do with the line
                await e.GetModelPlanAsync();
                await e.JevSettledForTest();

                var after = (await WaitingWordsAsync(e)).Single();
                Assert.Equal(held.Reason, after.Reason);
                Assert.Equal("approved", after.Status);
                Assert.Equal(1, (await e.ApplyPlanAsync(new[] { after.Id }, "human")).AppliedCount);
                Assert.Equal(Words, (await e.GetObjectAsync("measure:Sales/Total Amount")).Properties["description"]?.ToString());
            }
        }

        /// <summary>A held line held again for a NEW reason (what it was written over has changed since) loses the tick: the person ticked it
        /// for what it said then.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A1_a_tick_on_a_held_line_is_taken_back_when_it_is_held_again_for_a_new_reason()
        {
            var (f, e, _) = await AuthoredDescriptionAsync("Sol2A1e.bim", "human");
            using (f)
            {
                await TickedHeldLineAsync(e);

                await e.SetObjectPropertyAsync("measure:Sales/Total Amount", "Description", "Somebody else's words.", "human");
                await e.GetModelPlanAsync();
                await e.JevSettledForTest();

                var after = (await WaitingWordsAsync(e)).Single();
                Assert.Equal(JevFamily.HeldChangedReason, after.Reason);
                Assert.NotEqual("approved", after.Status);
            }
        }

        // ---- A2: the permission settles INSIDE the undo, so no model swap can come between ------------------------------------------

        private sealed class HoldCommit : ISessionObserver
        {
            public readonly ManualResetEventSlim Inside = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim Go = new ManualResetEventSlim(false);
            public void BeforeCommit(Model model) { Inside.Set(); Go.Wait(TimeSpan.FromSeconds(30)); }
            public void AfterCommit(SessionCommit commit) { }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("undo")]
        [InlineData("undo-to")]
        public async Task A2_an_undo_already_running_when_another_model_opens_still_takes_the_permission_back(string path)
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("Sol2A2-" + path + ".bim");
            await f.ConsentAsync();
            Assert.True((await f.Engine.GetConsentAsync()).Current);
            var a = f.Sessions.Current;
            string entry = null;
            if (path == "undo-to")
            {
                entry = f.Sessions.Ledger.Entries(a.Id).Last().EntryId;
                await f.Engine.SetObjectPropertyAsync("measure:Sales/Total Sales", "Description", "later edit", "human");
            }
            var hold = new HoldCommit();
            a.RegisterObserver(hold);
            var undo = path == "undo-to" ? f.Engine.UndoToEntryAsync(entry, "human") : f.Engine.UndoAsync("human");
            Assert.True(await Task.Run(() => hold.Inside.Wait(TimeSpan.FromSeconds(20))), "the undo never started");
            // The undo is admitted. Another model opens now: its swap closes admission on this session and waits for the undo to finish.
            Task swap;
            try
            {
                swap = f.Engine.CreateModelAsync("Other", 1604, discardUnsaved: true);
                // The wrapper gate makes the replacement's build wait for the admitted undo, so the old session is not retired
                // while it runs. What matters here is that the swap cannot complete under the running undo.
                await Task.Delay(500);
                Assert.False(swap.IsCompleted, "the swap completed while the undo was still running");
            }
            finally { hold.Go.Set(); }                       // a failed assert must never leave the dispatcher wedged
            // The undo lands, but its caller may be told the model was replaced by the time it reads the state back: what matters is the permission.
            try { await undo; } catch (InvalidOperationException) { }
            await swap;

            await f.Engine.OpenAsync(Path.Combine(f.Workspace, "Sol2A2-" + path + ".bim"), true);
            Assert.False((await f.Engine.GetConsentAsync()).Current, "the permission the person undid came back (" + path + ")");
        }

        // ---- B1: a rule is the bundled rule only when it behaves the same -------------------------------------------------------------

        private const string FormatRuleId = "SEM_COLUMN_NEEDS_FORMAT_STRING";

        private static BpaRule BundledFormatRule() => BpaRuleSet.Standard().Single(r => r.ID == FormatRuleId);

        private static BpaRule CopyOf(BpaRule r) => JsonSerializer.Deserialize<BpaRule>(JsonSerializer.Serialize(r));

        private static async Task<(DecisionFixture F, IEngine E)> UnformattedRateModelAsync(string name)
        {
            var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync(name, 1604);
            var t = await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync(t, "Rate", "Double", "Rate", "human");
            var dt = await e.CreateTableAsync("Date", "human");
            var dkey = await e.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await e.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await e.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await e.MarkDateTableAsync(dt, "Date", "human");
            return (f, e);
        }

        [Fact]
        public async Task B1_a_bundled_rule_copy_with_its_severity_raised_is_the_models_own_and_blocks_the_gate()
        {
            var (f, e) = await UnformattedRateModelAsync("Sol2B1");
            using (f)
            {
                Assert.True((await McpTools.DeployGate(e)).Pass, "the baseline: the bundled severity 2 rule is advice the gate skips: " + (await McpTools.DeployGate(e)).CheckLine);
                var own = CopyOf(BundledFormatRule());
                Assert.Equal(2, own.Severity);
                own.Severity = 3;
                await e.LoadBpaRulesAsync(JsonSerializer.Serialize(new[] { own }), replace: false, "human");

                var hit = (await e.BpaScanAsync()).Violations.First(v => v.RuleId == FormatRuleId);
                Assert.Equal(3, hit.Severity);
                Assert.True(hit.Custom, "a rule the model raised to severity 3 is the model's own policy, not the bundled rule");
                Assert.False(hit.IsAdviceTheGateSkips());
                var gate = await McpTools.DeployGate(e);
                Assert.False(gate.Pass, "the model's own severity 3 rule was skipped by the gate: " + gate.CheckLine);
            }
        }

        [Fact]
        public async Task B1_an_unchanged_copy_of_the_bundled_rule_is_still_the_bundled_rule()
        {
            var (f, e) = await UnformattedRateModelAsync("Sol2B1c");
            using (f)
            {
                await e.LoadBpaRulesAsync(JsonSerializer.Serialize(new[] { CopyOf(BundledFormatRule()) }), replace: false, "human");
                var hit = (await e.BpaScanAsync()).Violations.First(v => v.RuleId == FormatRuleId);
                Assert.False(hit.Custom);
                Assert.True((await McpTools.DeployGate(e)).Pass);
            }
        }

        // Every behaviour field of the rule, one at a time: changing any of them makes the copy the model's own. A field added to BpaRule later
        // is a behaviour field until somebody says otherwise (the test below), so a new one cannot be skipped by a copy that differs in it.
        public static IEnumerable<object[]> ChangedFields() => new[]
        {
            "Severity", "Scope", "FixExpression", "CompatibilityLevel", "TokenCheck", "ExemptFieldParameters", "Expression", "Category",
        }.Select(n => new object[] { n });

        [Theory]
        [MemberData(nameof(ChangedFields))]
        public void B1_a_copy_that_differs_in_any_behaviour_field_is_the_models_own_rule(string field)
        {
            var bundled = BundledFormatRule();
            var copy = CopyOf(bundled);
            var p = typeof(BpaRule).GetProperty(field);
            p.SetValue(copy, field switch
            {
                "Severity" => bundled.Severity + 1,
                "CompatibilityLevel" => bundled.CompatibilityLevel + 100,
                "ExemptFieldParameters" => !bundled.ExemptFieldParameters,
                _ => ((string)p.GetValue(bundled) ?? "") + " and not Name.Contains(\"zzz\")",
            });
            Assert.False(BpaRuleSet.SameRule(bundled, copy), field + " changed and the copy still reads as the bundled rule");
        }

        [Fact]
        public void B1_only_the_words_a_person_reads_may_differ_in_a_copy_of_the_bundled_rule()
        {
            var bundled = BundledFormatRule();
            var copy = CopyOf(bundled);
            copy.Name += " (our wording)";
            copy.Description += " Ours.";
            copy.Expression = "  " + bundled.Expression.Replace(" and ", "  and ") + " ";
            Assert.True(BpaRuleSet.SameRule(bundled, copy));
        }

        [Fact]
        public void B1_every_property_of_a_rule_is_a_behaviour_field_unless_it_is_named_as_words_or_provenance()
        {
            // The class cannot recur: a property added to BpaRule joins the comparison on its own. Only these may be left out of it.
            var left = new[] { nameof(BpaRule.Name), nameof(BpaRule.Description), nameof(BpaRule.FromModelAnnotation) };
            var compared = BpaRuleSet.BehaviourFields.Select(p => p.Name).ToHashSet();
            foreach (var p in typeof(BpaRule).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != nameof(BpaRule.ID)))
                Assert.True(left.Contains(p.Name) != compared.Contains(p.Name), p.Name + " is neither compared nor named as words or provenance");
        }
    }
}
