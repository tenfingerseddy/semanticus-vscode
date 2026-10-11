using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// m11/sol1-quality: the four Sol 6.1 findings on the joined M11 build (area 4, Model quality). Each test is Sol's own scenario and
    /// each failed before its fix.
    ///  4.1 A related acceptance (a twin the person did not accept) must not lift the Best practice letter.
    ///  4.2 An acceptance belongs to the object it was made for, never to a later object that happens to share its name.
    ///  4.3 A model's own severity 3 rule keeps blocking the deploy gate when its fix is one the format fill rule withholds.
    ///  4.4 Saving TMDL takes out only the tool stamps at model level, never text inside another annotation's value.
    /// </summary>
    public sealed class Sol1QualityTests
    {
        private const string Why = "Declined on purpose; reports depend on it as it is.";

        // ---- 4.1 the grade counts a related finding as open ----------------------------------------------------------------

        [Fact]
        public void The_grade_counts_a_related_finding_as_open()
        {
            var card = new BpaScorecard
            {
                ObjectsScanned = 10,
                Violations = new[] { new BpaViolation
                {
                    RuleId = "HIDE_FOREIGN_KEYS", Severity = 2, ObjectRef = "column:Sales/CustomerKey",
                    Waived = true, WaivedAsTwin = true, WaiverReason = DeclinedChanges.TwinReason,   // set aside by a twin, nobody accepted THIS finding
                } },
            };

            BpaGrading.Stamp(card);

            Assert.NotEqual("A", card.BestPractice);
            Assert.Contains("1 warning", card.BestPracticeWhy);
        }

        [Fact]
        public void The_grade_still_does_not_count_an_accepted_finding()
        {
            var card = new BpaScorecard
            {
                ObjectsScanned = 10,
                Violations = new[] { new BpaViolation { RuleId = "HIDE_FOREIGN_KEYS", Severity = 2, ObjectRef = "column:Sales/CustomerKey", Waived = true, WaiverReason = Why } },
            };

            BpaGrading.Stamp(card);

            Assert.Equal("A", card.BestPractice);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Accepting_the_twin_in_ai_readiness_leaves_the_best_practice_grade_exactly_where_it_was()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Sol41", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "CustomerKey", "Int64", null, "human");
            await e.CreateTableAsync("Customer", "human");
            await e.CreateColumnAsync("table:Customer", "CustomerKey", "Int64", null, "human");
            await e.CreateRelationshipAsync("column:Sales/CustomerKey", "column:Customer/CustomerKey", null, true, "human");

            var before = await e.BpaScanAsync();
            await e.WaiveFindingAsync("air", "VIS-FK", "column:Sales/CustomerKey", Why, "human");
            var after = await e.BpaScanAsync();

            var twin = after.Violations.Single(v => v.RuleId == "HIDE_FOREIGN_KEYS" && v.ObjectRef == "column:Sales/CustomerKey");
            Assert.False(twin.IsAccepted());   // it is only a related finding
            Assert.Equal(before.BestPractice, after.BestPractice);
            Assert.Equal(before.BestPracticeWeight, after.BestPracticeWeight);
            Assert.Equal(before.BestPracticeWhy, after.BestPracticeWhy);
            Assert.Equal(before.ViolationCount, after.ViolationCount);
            Assert.NotEqual("A", after.BestPractice);
        }

        // ---- 4.2 a replacement object inherits an old object's acceptance ---------------------------------------------------

        private const string DivideRule = "USE_THE_DIVIDE_FUNCTION_FOR_DIVISION";

        private static async Task<DecisionFixture> RateModelAsync()
        {
            var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Sol42", 1604);
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "Units", "Double", null, "human");
            await e.CreateMeasureAsync("table:Sales", "Rate", "SUM ( Sales[Amount] ) / SUM ( Sales[Units] )", "human");
            return f;
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_measure_created_under_a_deleted_measures_name_does_not_inherit_its_acceptance()
        {
            using var f = await RateModelAsync();
            var e = f.Engine;
            await e.WaiveFindingAsync("bpa", DivideRule, "measure:Sales/Rate", Why, "human");
            Assert.Contains((await e.BpaScanAsync()).Violations, v => v.RuleId == DivideRule && v.IsAccepted());   // the acceptance works on its own object

            await e.DeleteObjectAsync("measure:Sales/Rate", "human");
            await e.CreateMeasureAsync("table:Sales", "Rate", "SUM ( Sales[Units] ) / SUM ( Sales[Amount] )", "human");

            var scan = await e.BpaScanAsync();
            var again = scan.Violations.Single(v => v.RuleId == DivideRule && v.ObjectRef == "measure:Sales/Rate");
            Assert.False(again.Waived, "the new measure reads as accepted on the strength of a deleted one");
            Assert.False(again.IsAccepted());
            Assert.Equal(0, scan.WaivedCount);
            Assert.True(scan.ViolationCount > 0);
        }

        [Fact]
        public async Task The_old_acceptance_is_kept_in_the_store_but_declines_nothing_on_the_new_measure()
        {
            using var f = await RateModelAsync();
            var e = f.Engine;
            await e.WaiveFindingAsync("bpa", DivideRule, "measure:Sales/Rate", Why, "human");
            await e.DeleteObjectAsync("measure:Sales/Rate", "human");
            await e.CreateMeasureAsync("table:Sales", "Rate", "SUM ( Sales[Units] )", "human");   // no division: the finding does not fire

            var scan = await e.BpaScanAsync();

            Assert.Single(await e.ListWaiversAsync());   // the person's record is not thrown away
            Assert.DoesNotContain(scan.Violations, v => v.RuleId == DivideRule && v.ObjectRef == "measure:Sales/Rate");   // and no "no longer fires" row for a measure it was never made for
            Assert.Equal(0, scan.WaivedCount);
        }

        [Fact]
        public async Task An_acceptance_still_follows_its_object_through_a_rename()
        {
            using var f = await RateModelAsync();
            var e = f.Engine;
            await e.WaiveFindingAsync("bpa", DivideRule, "measure:Sales/Rate", Why, "human");
            await e.RenameObjectAsync("measure:Sales/Rate", "Unit Rate", "human");

            var scan = await e.BpaScanAsync();

            Assert.Contains(scan.Violations, v => v.RuleId == DivideRule && v.ObjectRef == "measure:Sales/Unit Rate" && v.IsAccepted());
        }

        // ---- 4.3 a custom severity 3 rule is not advice ---------------------------------------------------------------------

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_models_own_severity_three_rule_with_a_withheld_format_fix_still_blocks_the_deploy_gate()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Sol43", 1604);
            var t = await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync(t, "Rate", "Double", "Rate", "human");
            var dt = await e.CreateTableAsync("Date", "human");
            var dkey = await e.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await e.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await e.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await e.MarkDateTableAsync(dt, "Date", "human");
            Assert.True((await McpTools.DeployGate(e)).Pass, "the baseline model passes");

            await e.LoadBpaRulesAsync("[{\"ID\":\"OWN_RATE_FMT\",\"Name\":\"Rates need a percent format\",\"Category\":\"Formatting\",\"Severity\":3,\"Scope\":\"DataColumn\","
                + "\"Expression\":\"DataType = \\\"Double\\\" and string.IsNullOrWhitespace(FormatString)\",\"FixExpression\":\"FormatString = \\\"0.0%\\\"\"}]", replace: false, "human");

            var gate = await McpTools.DeployGate(e);
            var scan = await e.BpaScanAsync();
            var own = scan.Violations.Single(v => v.RuleId == "OWN_RATE_FMT");
            Assert.False(gate.Pass, "an error the model wrote for itself was passed as advice");
            Assert.False(own.CanAutoFix, "the format fill rule withholds this fix");
            Assert.True(own.Advice, "its words stay advice, so the assistant is told to ask the person and not to write a format over one");
            Assert.False(own.IsAdviceTheGateSkips(), "a model's own error is its policy, not advice the gate skips");
        }

        [Fact]
        public async Task The_built_in_format_advice_stays_advice_and_a_models_severity_two_rule_stays_advice()
        {
            using var f = new DecisionFixture(TestEntitlements.Free);
            var e = f.Engine;
            await e.CreateModelAsync("Sol43b", 1604);
            var t = await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync(t, "Rate", "Double", "Rate", "human");
            await e.LoadBpaRulesAsync("[{\"ID\":\"OWN_RATE_FMT\",\"Name\":\"Rates need a percent format\",\"Category\":\"Formatting\",\"Severity\":2,\"Scope\":\"DataColumn\","
                + "\"Expression\":\"DataType = \\\"Double\\\" and string.IsNullOrWhitespace(FormatString)\",\"FixExpression\":\"FormatString = \\\"0.0%\\\"\"}]", replace: false, "human");

            var own = (await e.BpaScanAsync()).Violations.Single(v => v.RuleId == "OWN_RATE_FMT");

            Assert.True(own.Advice);
            Assert.True(own.IsAdviceTheGateSkips());
        }

        // ---- 4.4 saving TMDL deletes text inside another annotation ---------------------------------------------------------

        [Fact]
        public async Task Saving_tmdl_keeps_a_stamp_shaped_line_inside_another_annotations_value()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Sol44", 1604);
            await e.CreateTableAsync("Sales", "human");
            const string notes = "Before\nannotation __TEdtr = retained example\nAfter";
            await f.Sessions.Require().MutateAsync("human", "notes", m => m.SetAnnotation("Notes", notes));
            var folder = Path.Combine(f.Workspace, "Sol44");

            await e.SaveAsync(folder, "TMDL", false, "human");

            var written = File.ReadAllText(Path.Combine(folder, "model.tmdl"));
            Assert.True(written.Contains("annotation __TEdtr = retained example"), written);
            Assert.Contains("After", written);
            var reopened = await f.Sessions.Require().ReadAsync(m => m.GetAnnotation("Notes"));
            Assert.Equal(notes, reopened);   // in memory it was never touched
            await e.OpenAsync(folder, discardUnsaved: true);
            Assert.Equal(notes.Replace("\r\n", "\n"), (await f.Sessions.Require().ReadAsync(m => m.GetAnnotation("Notes"))).Replace("\r\n", "\n"));
        }

        [Fact]
        public async Task Saving_tmdl_still_takes_out_the_stamps_the_save_added_and_leaves_no_blank_gap_behind_a_multiline_value()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Sol44b", 1604);
            await e.CreateTableAsync("Sales", "human");
            await f.Sessions.Require().MutateAsync("human", "notes", m => m.SetAnnotation("Notes", "one\ntwo"));
            var folder = Path.Combine(f.Workspace, "Sol44b");

            await e.SaveAsync(folder, "TMDL", false, "human");

            var written = File.ReadAllText(Path.Combine(folder, "model.tmdl"));
            Assert.DoesNotContain("__TEdtr", written);
            Assert.DoesNotContain("TabularEditor_SerializeOptions", written);
            Assert.Contains("annotation Notes", written);
        }

        // The layout TE2's TMDL serializer really writes (measured on this build): the model's own annotations sit at column 0 after the
        // model block, and a multi-line value is indented two tabs below its declaration.
        [Fact]
        public void The_strip_takes_out_the_model_level_stamp_and_leaves_a_stamp_shaped_line_inside_a_value_and_one_on_a_nested_object()
        {
            var path = Path.Combine(Path.GetTempPath(), "sol44-" + Guid.NewGuid().ToString("N") + ".tmdl");
            var keep = "model Model\n\tculture: en-US\n\tperspective P\n\t\tannotation __TEdtr = nested, not the model's\n\n"
                + "annotation Notes =\n\t\tBefore\n\t\tannotation __TEdtr = retained example\n\t\tAfter\n\n"
                + "annotation Other = x\n\nref table Sales\n";
            try
            {
                File.WriteAllText(path, keep.Replace("annotation Other", "annotation __TEdtr = 1\n\nannotation Other"));

                Semanticus.Engine.ToolStamps.StripTmdl(path, new[] { "__TEdtr", "TabularEditor_SerializeOptions" });

                Assert.Equal(keep, File.ReadAllText(path));
            }
            finally { File.Delete(path); }
        }
    }
}
