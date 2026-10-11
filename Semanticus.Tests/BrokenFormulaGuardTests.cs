using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Plan statement check 4 (P2): a formula that names an object the model does not have, caught by the engine's own reference check and wired so that neither AI readiness
    /// nor best practice can read A on such a model. Pins the readiness rule DAX-REFERENCE (applicable, violating and dormant populations, the grade cap, the cap that an
    /// accepted finding does not lift) and the best-practice rule FORMULA_REFERENCE_NOT_IN_MODEL with its letter.
    /// </summary>
    public sealed class BrokenFormulaGuardTests
    {
        private static ReadinessRule Rule() => ReadinessRuleSet.Default().Single(r => r.Id == "DAX-REFERENCE");

        private static async Task<(LocalEngine Engine, Session S)> ModelAsync(Action<Model> build)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Free);
            await engine.CreateModelAsync("BrokenFormula", 1604);
            var s = sessions.Require();
            if (build != null) await s.MutateAsync("human", "build", build);
            return (engine, s);
        }

        private static void Sales(Model m)
        {
            var t = m.AddTable("Sales");
            t.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
            t.AddDataColumn("Order Date", "Order Date", null, DataType.DateTime);
            t.AddMeasure("Total Amount", "SUM ( 'Sales'[Amount] )");
        }

        [Fact]
        public async Task The_rule_is_dormant_on_a_model_with_no_formula()
        {
            var (engine, s) = await ModelAsync(m => m.AddTable("Plain").AddDataColumn("A", "A", null, DataType.String));
            using (engine)
            {
                var ev = await s.ReadAsync(m => Rule().Evaluate(m));
                Assert.Equal(0, ev.Applicable);
                Assert.Empty(ev.Violations);
            }
        }

        [Fact]
        public async Task Applicable_is_the_formulas_checked_and_a_whole_model_has_no_finding()
        {
            var (engine, s) = await ModelAsync(m =>
            {
                Sales(m);
                var t = m.Tables["Sales"];
                t.AddMeasure("Share", "DIVIDE ( [Total Amount], CALCULATE ( [Total Amount], ALL ( 'Sales' ) ) )").FormatStringExpression = "IF ( [Total Amount] > 0, \"0.0%\", \"0%\" )";
                t.AddMeasure("Net by day", "SUMX ( ADDCOLUMNS ( VALUES ( 'Sales'[Order Date] ), \"Net\", [Total Amount] ), [Net] )");
                t.AddMeasure("With var", "VAR total = [Total Amount] RETURN total * 2");
                m.AddRole("Readers").RowLevelSecurity[t] = "'Sales'[Amount] > 0";
            });
            using (engine)
            {
                var ev = await s.ReadAsync(m => Rule().Evaluate(m));
                Assert.Equal(6, ev.Applicable);   // 4 measures + 1 format string expression + 1 row filter
                Assert.Empty(ev.Violations);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("SUM ( 'Ghost'[Amount] )", "Unknown table 'Ghost'")]
        [InlineData("SUM ( 'Sales'[Ghost] )", "Table 'Sales' has no column 'Ghost'")]
        [InlineData("[Ghost] * 2", "Unknown measure or column '[Ghost]'")]
        public async Task A_measure_that_names_nothing_the_model_has_is_found_and_named(string dax, string what)
        {
            var (engine, s) = await ModelAsync(m => { Sales(m); m.Tables["Sales"].AddMeasure("Broken", dax); });
            using (engine)
            {
                var ev = await s.ReadAsync(m => Rule().Evaluate(m));
                var f = Assert.Single(ev.Violations);
                Assert.Equal("measure:Sales/Broken", f.ObjectRef);
                Assert.Contains(what, f.Message);
                Assert.Equal(2, ev.Applicable);
            }
        }

        [Fact]
        public async Task Every_kind_of_formula_is_read()
        {
            var (engine, s) = await ModelAsync(m =>
            {
                Sales(m);
                var t = m.Tables["Sales"];
                t.AddMeasure("Format", "1").FormatStringExpression = "FORMAT ( [Ghost One], \"0\" )";
                var kpi = t.AddMeasure("Kpi", "1").AddKPI();
                kpi.TargetExpression = "[Ghost Two]";
                t.AddCalculatedColumn("Calc", "'Ghost Three'[X]");
                m.AddCalculationGroup("Time").AddCalculationItem("Item", "CALCULATE ( SELECTEDMEASURE (), 'Sales'[Ghost Four] > 0 )");
                m.AddRole("Readers").RowLevelSecurity[t] = "'Sales'[Ghost Five] > 0";
            });
            using (engine)
            {
                var ev = await s.ReadAsync(m => Rule().Evaluate(m));
                Assert.Equal(new[] { "calcitem:Time/Item", "column:Sales/Calc", "measure:Sales/Format", "measure:Sales/Kpi", "role:Readers" },
                    ev.Violations.Select(v => v.ObjectRef).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            }
        }

        [Fact]
        public async Task A_broken_formula_caps_AI_readiness_below_A_and_an_accepted_finding_does_not_lift_the_cap()
        {
            var (engine, s) = await ModelAsync(m => { Sales(m); m.Tables["Sales"].AddMeasure("Broken", "SUM ( 'Ghost'[Amount] )"); });
            using (engine)
            {
                var card = await s.ReadAsync(m => new ReadinessAnalyzer().Analyze(m));
                Assert.NotEqual("A", card.Grade);
                Assert.NotEqual("B", card.Grade);
                Assert.NotEqual("C", card.Grade);
                Assert.True(card.Overall <= 69, "overall " + card.Overall);
                Assert.Contains(card.GatedBy, g => g.Contains("refers to an object the model does not have"));
                Assert.Contains(card.Findings, f => f.RuleId == "DAX-REFERENCE" && f.ObjectRef == "measure:Sales/Broken");

                await s.MutateAsync("human", "accept", m => WaiverStore.Save(m, new System.Collections.Generic.List<WaiverRecord>
                {
                    new WaiverRecord { System = "air", RuleId = "DAX-REFERENCE", ObjectRef = "measure:Sales/Broken", Reason = "on purpose", By = "human", When = DateTime.UtcNow.ToString("o") },
                }));
                var after = await s.ReadAsync(m => new ReadinessAnalyzer().Analyze(m));
                Assert.True(after.Overall <= 69, "an accepted finding lifted the cap to " + after.Overall);
                Assert.NotEqual("A", after.Grade);
            }
        }

        [Fact]
        public async Task The_same_model_made_whole_is_not_capped_by_the_rule()
        {
            var (engine, s) = await ModelAsync(m => { Sales(m); m.Tables["Sales"].AddMeasure("Fine", "[Total Amount] * 2"); });
            using (engine)
            {
                var card = await s.ReadAsync(m => new ReadinessAnalyzer().Analyze(m));
                Assert.DoesNotContain(card.GatedBy, g => g.Contains("refers to an object"));
                Assert.DoesNotContain(card.Findings, f => f.RuleId == "DAX-REFERENCE");
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Best_practice_reports_the_formula_as_an_error_and_cannot_read_A_even_when_it_is_accepted()
        {
            var (engine, s) = await ModelAsync(m => { Sales(m); m.Tables["Sales"].AddMeasure("Broken", "SUM ( 'Ghost'[Amount] )"); });
            using (engine)
            {
                var card = await s.ReadAsync(m => BpaAnalyzer.Analyze(m, BpaRuleSet.ForModel(m)));
                var v = Assert.Single(card.Violations, x => x.RuleId == BpaGrading.BrokenFormulaRuleId);
                Assert.Equal(3, v.Severity);
                Assert.Equal("measure:Sales/Broken", v.ObjectRef);
                Assert.NotEqual("A", card.BestPractice);

                await s.MutateAsync("human", "accept", m => WaiverStore.Save(m, new System.Collections.Generic.List<WaiverRecord>
                {
                    new WaiverRecord { System = "bpa", RuleId = BpaGrading.BrokenFormulaRuleId, ObjectRef = "measure:Sales/Broken", Reason = "on purpose", By = "human", When = DateTime.UtcNow.ToString("o") },
                }));
                var after = await s.ReadAsync(m => BpaAnalyzer.Analyze(m, BpaRuleSet.ForModel(m)));
                Assert.NotEqual("A", after.BestPractice);
                Assert.Contains("refers to an object the model does not have", after.BestPracticeWhy);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_whole_model_is_not_marked_by_the_best_practice_rule()
        {
            var (engine, s) = await ModelAsync(m => { Sales(m); m.Tables["Sales"].AddMeasure("Fine", "[Total Amount] * 2"); });
            using (engine)
            {
                var card = await s.ReadAsync(m => BpaAnalyzer.Analyze(m, BpaRuleSet.ForModel(m)));
                Assert.DoesNotContain(card.Violations, x => x.RuleId == BpaGrading.BrokenFormulaRuleId);
                Assert.Empty(card.RuleErrors);
            }
        }

        [Fact]
        public void A_broken_formula_stops_the_letter_at_the_rule_and_the_sentence_says_why()
        {
            Assert.Equal("A", BpaGrading.Compute(0, 0, 0, 10, 0).Letter);
            var (letter, _, why) = BpaGrading.Compute(0, 0, 3, 10, 0, brokenFormulas: 1);
            Assert.NotEqual("A", letter);
            Assert.Contains("1 formula refers to an object the model does not have", why);
        }

        // ---- Gate only (Kane, 4 Oct): the rule holds the cap and shows the finding, and counts in no average.

        /// <summary>The scorecard with the default rules and with every default rule but DAX-REFERENCE: the two must agree on everything the average is made of.</summary>
        internal static void AssertSameScoreWithoutTheRule(Model m, string what)
        {
            var with = new ReadinessAnalyzer().Analyze(m);
            var without = new ReadinessAnalyzer(ReadinessRuleSet.Default().Where(r => r.Id != "DAX-REFERENCE").ToList()).Analyze(m);
            Assert.True(with.RawOverall == without.RawOverall, what + ": RawOverall " + with.RawOverall + " vs " + without.RawOverall);
            Assert.True(with.Overall == without.Overall || (with.Overall < without.Overall && with.GatedBy.Any(g => g.Contains("refers to an object"))), what + ": Overall " + with.Overall + " vs " + without.Overall);
            if (!with.GatedBy.Any(g => g.Contains("refers to an object"))) Assert.Equal(without.Grade, with.Grade);
            Assert.Equal(without.Categories.Select(c => (c.Category, c.Score, c.Applicable, c.Violations, c.HasRules)).ToArray(), with.Categories.Select(c => (c.Category, c.Score, c.Applicable, c.Violations, c.HasRules)).ToArray());
        }

        [Fact]
        public async Task A_model_with_formulas_scores_the_same_with_and_without_the_rule_in_every_category()
        {
            var (engine, s) = await ModelAsync(m =>
            {
                Sales(m);
                var t = m.Tables["Sales"];
                for (var i = 0; i < 12; i++) t.AddMeasure("Healthy " + i, "[Total Amount] * " + (i + 2));
            });
            using (engine)
            {
                Assert.Equal(13, await s.ReadAsync(m => FormulaReferences.CountFormulas(m)));
                await s.ReadAsync(m => { AssertSameScoreWithoutTheRule(m, "a healthy model"); return 0; });
                var naming = await s.ReadAsync(m => new ReadinessAnalyzer().Analyze(m).Categories.Single(c => c.Category == "Naming"));
                var namingWithout = await s.ReadAsync(m => new ReadinessAnalyzer(ReadinessRuleSet.Default().Where(r => r.Id != "DAX-REFERENCE").ToList()).Analyze(m).Categories.Single(c => c.Category == "Naming"));
                Assert.Equal(namingWithout.Applicable, naming.Applicable);   // the 13 formulas read are not in the Naming population
                Assert.Equal(namingWithout.Score, naming.Score);
            }
        }

        [Fact]
        public async Task A_broken_model_keeps_its_cap_its_finding_and_its_sentence_and_its_Naming_score_is_the_same_with_and_without_the_rule()
        {
            var (engine, s) = await ModelAsync(m =>
            {
                Sales(m);
                for (var i = 0; i < 6; i++) m.Tables["Sales"].AddMeasure("Healthy " + i, "[Total Amount] * " + (i + 2));
                m.Tables["Sales"].AddMeasure("Broken", "SUM ( 'Ghost'[Amount] )");
            });
            using (engine)
            {
                var with = await s.ReadAsync(m => new ReadinessAnalyzer().Analyze(m));
                var without = await s.ReadAsync(m => new ReadinessAnalyzer(ReadinessRuleSet.Default().Where(r => r.Id != "DAX-REFERENCE").ToList()).Analyze(m));
                Assert.Equal(without.Categories.Single(c => c.Category == "Naming").Score, with.Categories.Single(c => c.Category == "Naming").Score);
                Assert.Equal(without.Categories.Single(c => c.Category == "Naming").Applicable, with.Categories.Single(c => c.Category == "Naming").Applicable);
                Assert.Equal(without.Categories.Single(c => c.Category == "Naming").Violations, with.Categories.Single(c => c.Category == "Naming").Violations);
                Assert.Equal(without.RawOverall, with.RawOverall);
                Assert.True(with.Overall <= 69, "overall " + with.Overall);
                Assert.Contains(with.GatedBy, g => g.Contains("1 object holds a formula that refers to an object the model does not have; capped at D"));
                Assert.Contains(with.Findings, f => f.RuleId == "DAX-REFERENCE" && f.ObjectRef == "measure:Sales/Broken");
                Assert.DoesNotContain(without.Findings, f => f.RuleId == "DAX-REFERENCE");
            }
        }

        [Fact]
        public async Task Only_the_broken_formula_rule_is_gate_only_and_its_finding_lifts_nothing()
        {
            Assert.Equal(new[] { "DAX-REFERENCE" }, ReadinessRuleSet.Default().Where(r => r.GateOnly).Select(r => r.Id).ToArray());
            var (engine, s) = await ModelAsync(m => { Sales(m); m.Tables["Sales"].AddMeasure("Broken", "SUM ( 'Ghost'[Amount] )"); });
            using (engine)
            {
                var lifts = await s.ReadAsync(m => new ReadinessAnalyzer().Lifts(m, out _));
                Assert.DoesNotContain(lifts.Keys, k => k.Contains("DAX-REFERENCE"));
                var ev = await s.ReadAsync(m => Rule().Evaluate(m));
                Assert.Equal(2, ev.Applicable);   // the rule still reads every formula: Applicable 2, one violation
                Assert.Single(ev.Violations);
            }
        }
    }
}
