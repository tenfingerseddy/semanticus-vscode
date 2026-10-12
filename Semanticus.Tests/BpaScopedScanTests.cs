using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The scoped best-practice check that runs after every edit (m14 bpa-fast). It used to parse and compile every rule's
    /// Dynamic-LINQ text on every scan and to filter each scope's whole collection once per rule, so a one-object check cost
    /// 45 ms on SpaceParts and 91 ms on 3,000 fields. Rules are now compiled once per process and each scope is filtered once
    /// per scan. These tests pin that the answers did not move: a scoped scan reports, field for field, what the full scan
    /// reports for the same objects, and a second scan compiles nothing.
    /// </summary>
    public sealed class BpaScopedScanTests
    {
        private static string Row(BpaViolation v) => JsonSerializer.Serialize(v);

        private static string[] Rows(IEnumerable<BpaViolation> vs) =>
            vs.Select(Row).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        // The full scan's rows for these refs. Rows that stand for a stored acceptance and not for a finding (an acceptance whose
        // finding no longer fires, the whole-rule row) are full-scan only by contract, so they are left out.
        private static string[] FullRowsFor(BpaScorecard full, ICollection<string> refs) =>
            Rows(full.Violations.Where(v => refs.Contains(v.ObjectRef) && !v.NoLongerFires && !v.WholeRuleRow));

        [Fact]
        public async Task A_scoped_scan_reports_exactly_the_full_scan_rows_for_every_object_and_for_a_mixed_scope()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            f.UseNull();
            await f.Engine.OpenAsync(TestModels.FindBim());

            // Accepted findings, per object and for a whole rule, so the waiver and twin passes run on both scans.
            var first = await f.Sessions.Current.ReadAsync(m => BpaAnalyzer.Analyze(m, LocalEngine.GetBpaRules(m)));
            var open = first.Violations.Where(v => !v.Waived).GroupBy(v => v.RuleId).Select(g => g.First()).ToArray();
            Assert.True(open.Length >= 3, "AdventureWorks needs a few open findings for this test");
            await f.Engine.WaiveFindingAsync("bpa", open[0].RuleId, open[0].ObjectRef, "Accepted for this test.", "human");
            await f.Engine.WaiveFindingAsync("bpa", open[1].RuleId, open[1].ObjectRef, "Accepted for this test.", "human");
            await f.Engine.WaiveFindingAsync("bpa", open[2].RuleId, null, "Accepted for the whole model.", "human");

            var mismatches = await f.Sessions.Current.ReadAsync(m =>
            {
                var rules = LocalEngine.GetBpaRules(m);
                var full = BpaAnalyzer.Analyze(m, rules);
                var refs = WaiverStore.AllObjects(m).Select(o => Refs.For(o)).Distinct(StringComparer.Ordinal).ToList();
                var bad = new List<string>();
                foreach (var r in refs)
                {
                    var scope = new HashSet<string>(new[] { r }, StringComparer.Ordinal);
                    var scoped = BpaAnalyzer.Analyze(m, rules, scope);
                    if (!Rows(scoped.Violations).SequenceEqual(FullRowsFor(full, scope))) bad.Add(r);
                }
                // A scope of many kinds at once shares each filtered collection across rules: it must still match.
                var mixed = new HashSet<string>(refs.Where((_, i) => i % 3 == 0), StringComparer.OrdinalIgnoreCase);
                var many = BpaAnalyzer.Analyze(m, rules, mixed);
                if (!Rows(many.Violations).SequenceEqual(FullRowsFor(full, mixed))) bad.Add("mixed scope of " + mixed.Count);
                Assert.Contains(full.Violations, v => v.Waived && !v.WholeRuleRow);   // the waiver passes really ran
                Assert.True(refs.Count > 300, "the walk covered the model: " + refs.Count);
                return bad;
            });
            Assert.Empty(mismatches);
        }

        [Fact]
        public async Task A_second_scan_compiles_no_rule_and_a_rule_that_does_not_parse_fails_the_same_way_each_time()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            f.UseNull();
            await f.Engine.OpenAsync(TestModels.FindBim());

            var broken = new BpaRule { ID = "TEST_DOES_NOT_PARSE", Name = "does not parse", Category = "Test", Severity = 2, Scope = "Measure", Expression = "Name ==" };
            var (firstErrors, compiledAfterFirst, compiledAfterMore, laterErrors, sameCards) = await f.Sessions.Current.ReadAsync(m =>
            {
                var rules = LocalEngine.GetBpaRules(m).Append(broken).ToList();
                var one = BpaAnalyzer.Analyze(m, rules);
                var after1 = BpaAnalyzer.CompiledPredicates;
                var two = BpaAnalyzer.Analyze(m, rules);
                var measure = m.AllMeasures.First();
                BpaAnalyzer.Analyze(m, rules, new HashSet<string> { Refs.For(measure) });
                BpaAnalyzer.Analyze(m, rules, new HashSet<string> { Refs.For(m.AllColumns.First()), Refs.For(m.Tables.First()) });
                return (one.RuleErrors, after1, BpaAnalyzer.CompiledPredicates, two.RuleErrors,
                        JsonSerializer.Serialize(one) == JsonSerializer.Serialize(two));
            });

            Assert.Equal(compiledAfterFirst, compiledAfterMore);          // nothing compiled again
            Assert.Contains(firstErrors, e => e.StartsWith("TEST_DOES_NOT_PARSE [Measure]:", StringComparison.Ordinal));
            Assert.Equal(firstErrors, laterErrors);                        // the parse error is reported on every scan
            Assert.True(sameCards, "two full scans of one model give the same card");
        }
    }
}
