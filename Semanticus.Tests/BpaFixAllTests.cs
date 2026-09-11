using System;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// One press of "Fix all" must clear EVERY auto-fixable finding, and the result must say so honestly.
    ///
    /// Why this exists: <c>BpaFixAllAsync</c> used to take ONE scan before the batch and apply that list. A fix
    /// can create a finding -- hiding a foreign-key column is exactly what makes that same column qualify for
    /// <c>ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS</c>, which the pre-press scan could not have listed -- so
    /// the press left auto-fixable findings behind and the count on the button was a promise it did not keep.
    /// </summary>
    public sealed class BpaFixAllTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private const string AvailableInMdxRule = "ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS";
        private const string HideForeignKeysRule = "HIDE_FOREIGN_KEYS";

        /// <summary>A star-schema slice with exactly the shape the cascade needs: a visible foreign-key column on
        /// the Many side of a relationship that is not used in any sort-by, hierarchy or variation. Nothing about
        /// that column trips the IsAvailableInMDX rule while it is visible; hiding it does.</summary>
        private static async Task<(LocalEngine engine, SessionManager sessions)> CascadeModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("FixAllCascade", 1601);

            var facts = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(facts, "Amount", "Int64", "Amount", "human");
            await engine.CreateColumnAsync(facts, "CustomerKey", "Int64", "CustomerKey", "human");
            var dim = await engine.CreateTableAsync("Customer", "human");
            await engine.CreateColumnAsync(dim, "CustomerKey", "Int64", "CustomerKey", "human");

            await engine.CreateRelationshipAsync("column:Facts/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            return (engine, sessions);
        }

        [Fact]
        public async Task One_press_clears_every_auto_fixable_finding_even_one_a_fix_creates()
        {
            var (engine, sessions) = await CascadeModelAsync();
            using (engine)
            {
                // The premise: the IsAvailableInMDX rule does NOT fire before the press. Hiding the foreign key
                // is what makes it fire, so the press's own first scan could not have known about it.
                var before = await engine.BpaScanAsync();
                Assert.Contains(before.Violations, v => v.RuleId == HideForeignKeysRule && v.CanAutoFix);
                Assert.DoesNotContain(before.Violations, v => v.RuleId == AvailableInMdxRule);

                var bulk = await engine.BpaFixAllAsync("human");

                // THE PROMISE ON THE BUTTON. One press, and nothing auto-fixable is left.
                var leftOver = bulk.Scorecard.Violations.Where(v => v.CanAutoFix && !v.Waived)
                    .Select(v => v.RuleId + " on " + v.ObjectRef).ToList();
                Assert.True(leftOver.Count == 0,
                    "still auto-fixable after one press: " + string.Join(", ", leftOver));
                Assert.Equal(0, bulk.Scorecard.AutoFixable);

                // And the cascade really happened: the rule the press could not have listed is now satisfied
                // rather than merely absent, i.e. the column ended up with IsAvailableInMDX = false.
                var stillAvailableInMdx = await sessions.Require().ReadAsync(m =>
                    m.Tables["Facts"].Columns["CustomerKey"].IsAvailableInMDX);
                Assert.False(stillAvailableInMdx);
            }
        }

        [Fact]
        public async Task A_second_press_is_a_no_op_rather_than_a_failure()
        {
            // The BPA cases are re-run against the same model after the first pass. The second press must be a
            // clean no-op, not an error and not a fresh round of edits on the undo timeline.
            var (engine, sessions) = await CascadeModelAsync();
            using (engine)
            {
                var first = await engine.BpaFixAllAsync("human");
                var revAfterFirst = await sessions.Require().ReadAsync(m => m.Tables.Count);

                var second = await engine.BpaFixAllAsync("human");

                Assert.Equal(0, second.Applied);
                Assert.Equal(0, second.Scorecard.AutoFixable);
                Assert.Equal(first.Scorecard.ViolationCount, second.Scorecard.ViolationCount);
                Assert.True(revAfterFirst > 0);
            }
        }

        // ---- a fix that runs and does not clear its finding -----------------------------------------------
        // A custom rule is the only path that can introduce a FixExpression which is a valid deterministic
        // assignment yet unrelated to the rule's own predicate. The write succeeds, so counting it as applied
        // reported a fixed model over findings that were still there, and nothing anywhere named them.

        private const string MisleadingFixRuleJson =
            "[{\"ID\":\"TEST_MISLEADING_FIX\",\"Name\":\"Visible measures need review\",\"Category\":\"Maintenance\"," +
            "\"Severity\":2,\"Scope\":\"Measure\",\"Expression\":\"not IsHidden\"," +
            "\"FixExpression\":\"Description = \\\"reviewed\\\"\"}]";

        private static async Task<(LocalEngine engine, SessionManager sessions)> MisleadingFixModelAsync()
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            await engine.CreateModelAsync("FixAllMisleading", 1601);
            var t = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            await engine.CreateMeasureAsync(t, "Total", "1", "human");
            return (engine, sessions);
        }

        [Fact]
        public async Task A_fix_that_does_not_clear_its_finding_is_reported_not_counted_as_done()
        {
            var (engine, sessions) = await MisleadingFixModelAsync();
            using (engine)
            {
                await engine.LoadBpaRulesAsync(MisleadingFixRuleJson, replace: true, "human");

                // Scoped to the rule under test: the bundled standard set also fires on this model.
                var before = await engine.BpaScanAsync();
                var mine = Assert.Single(before.Violations, v => v.RuleId == "TEST_MISLEADING_FIX");
                Assert.True(mine.CanAutoFix);

                var bulk = await engine.BpaFixAllAsync("human");

                // The write DID happen. This is not "nothing was applied", it is "applying it changed nothing the
                // rule looks at", and the result has to carry that or a caller cannot tell a clean model from a
                // press that quietly did not finish.
                var description = await sessions.Require().ReadAsync(m => m.Tables["Facts"].Measures["Total"].Description);
                Assert.Equal("reviewed", description);

                var leftover = Assert.Single(bulk.Unfixed, u => u.RuleId == "TEST_MISLEADING_FIX");
                Assert.Equal("measure:Facts/Total", leftover.ObjectRef);
                Assert.False(string.IsNullOrWhiteSpace(leftover.Reason));
                Assert.Equal(bulk.Unfixed.Length, bulk.Remaining);

                // The number the UI shows as "N auto-fixable" must still count it, or the model reads clean.
                Assert.Contains(bulk.Scorecard.Violations, v => v.RuleId == "TEST_MISLEADING_FIX" && v.CanAutoFix);
            }
        }

        [Fact]
        public async Task A_press_that_clears_everything_reports_nothing_left_over()
        {
            var (engine, _) = await CascadeModelAsync();
            using (engine)
            {
                var bulk = await engine.BpaFixAllAsync("human");
                Assert.Empty(bulk.Unfixed);
                Assert.Equal(0, bulk.Remaining);
            }
        }

        // ---- the severity filter exists on BOTH doors -------------------------------------------------------
        // The findings list gained a severity filter in the UI; the agent door needs the same narrowing or a
        // change to one door is a capability only that door can reach. bpa_scan returns every violation with its
        // severity, and severityMin is the mirror of the list's Errors / Warnings / Info chips.

        private const string ThreeSeveritiesRuleJson =
            "[{\"ID\":\"TEST_SEV_INFO\",\"Name\":\"Info rule\",\"Category\":\"Formatting\",\"Severity\":1,\"Scope\":\"Measure\",\"Expression\":\"not IsHidden\"}," +
            "{\"ID\":\"TEST_SEV_WARN\",\"Name\":\"Warning rule\",\"Category\":\"Formatting\",\"Severity\":2,\"Scope\":\"Measure\",\"Expression\":\"not IsHidden\"}," +
            "{\"ID\":\"TEST_SEV_ERR\",\"Name\":\"Error rule\",\"Category\":\"Formatting\",\"Severity\":3,\"Scope\":\"Measure\",\"Expression\":\"not IsHidden\"}]";

        [Fact]
        public async Task BpaScan_filters_by_severity_min_and_refuses_a_word_it_does_not_have()
        {
            var (engine, _) = await MisleadingFixModelAsync();
            using (engine)
            {
                await engine.LoadBpaRulesAsync(ThreeSeveritiesRuleJson, replace: true, "human");

                var all = await McpTools.BpaScan(engine);
                Assert.Contains(all.Violations, v => v.Severity == 1);
                Assert.Contains(all.Violations, v => v.Severity == 2);
                Assert.Contains(all.Violations, v => v.Severity == 3);

                var warningsUp = await McpTools.BpaScan(engine, severityMin: "Warning");
                Assert.NotEmpty(warningsUp.Violations);
                Assert.All(warningsUp.Violations, v => Assert.True(v.Severity >= 2));
                Assert.DoesNotContain(warningsUp.Violations, v => v.Severity == 1);
                // Counts stay model-wide while only the list narrows, exactly like every other bpa_scan filter.
                Assert.Equal(all.ViolationCount, warningsUp.ViolationCount);

                var errorsOnly = await McpTools.BpaScan(engine, severityMin: "Error");
                Assert.NotEmpty(errorsOnly.Violations);
                Assert.All(errorsOnly.Violations, v => Assert.Equal(3, v.Severity));

                // An unrecognised word refuses loudly rather than silently returning everything, which would be the
                // opposite of the intended narrowing.
                var ex = await Assert.ThrowsAsync<ArgumentException>(() => McpTools.BpaScan(engine, severityMin: "Critical"));
                Assert.Contains("severityMin", ex.Message, StringComparison.Ordinal);
            }
        }
    }
}
