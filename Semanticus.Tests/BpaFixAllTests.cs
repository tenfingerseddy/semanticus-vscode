using System;
using System.IO;
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
    public sealed class BpaFixAllTests : IClassFixture<TemplateCopies>
    {
        private readonly TemplateCopies _copies;
        public BpaFixAllTests(TemplateCopies copies) => _copies = copies;

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
        private async Task<(LocalEngine engine, SessionManager sessions)> CascadeModelAsync()
        {
            var sessions = new SessionManager();
            return (await _copies.OpenAsync("cascade", BuildCascadeAsync, new Fake(), sessions), sessions);
        }

        private static async Task BuildCascadeAsync(LocalEngine engine)
        {
            await engine.CreateModelAsync("FixAllCascade", 1601);

            var facts = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(facts, "Amount", "Int64", "Amount", "human");
            await engine.CreateColumnAsync(facts, "CustomerKey", "Int64", "CustomerKey", "human");
            var dim = await engine.CreateTableAsync("Customer", "human");
            await engine.CreateColumnAsync(dim, "CustomerKey", "Int64", "CustomerKey", "human");

            await engine.CreateRelationshipAsync("column:Facts/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
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

        [Trait(TestCost.Key, TestCost.Sweep)]
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

        private async Task<(LocalEngine engine, SessionManager sessions)> MisleadingFixModelAsync()
        {
            var sessions = new SessionManager();
            return (await _copies.OpenAsync("misleading", BuildMisleadingAsync, new Fake(), sessions), sessions);
        }

        private static async Task BuildMisleadingAsync(LocalEngine engine)
        {
            await engine.CreateModelAsync("FixAllMisleading", 1601);
            var t = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
            await engine.CreateMeasureAsync(t, "Total", "1", "human");
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

        // ---- a bulk fix that would do quiet harm is left to the person (m11/join) ----------------------------------
        // Fix all is one press over the whole model. Two built-in fixes change more than their finding says:
        // MARK_PRIMARY_KEYS sets IsKey, and a table has one key column, so on a table that already has a key it takes
        // the key away from that column (TOMWrapper Column.cs clears the others). AVOID_FLOATING_POINT_DATA_TYPES turns
        // a Double into a Decimal, which keeps four decimal places, so the values a column shows can change. Both doors
        // run the same BpaFixAllAsync (Studio's bpaFixAll, the MCP door's apply_safe_fixes with system='bpa').

        private async Task<(LocalEngine engine, SessionManager sessions)> KeyedDimensionModelAsync()
        {
            var sessions = new SessionManager();
            return (await _copies.OpenAsync("keyed", BuildKeyedDimensionAsync, new Fake(), sessions), sessions);
        }

        private static async Task BuildKeyedDimensionAsync(LocalEngine engine)
        {
            await engine.CreateModelAsync("FixAllKeys", 1601);
            var facts = await engine.CreateTableAsync("Facts", "human");
            await engine.CreateColumnAsync(facts, "CustomerKey", "Int64", "CustomerKey", "human");
            await engine.CreateColumnAsync(facts, "ProductKey", "Int64", "ProductKey", "human");
            var customer = await engine.CreateTableAsync("Customer", "human");
            await engine.CreateColumnAsync(customer, "CustomerKey", "Int64", "CustomerKey", "human");
            var code = await engine.CreateColumnAsync(customer, "Customer Code", "String", "CustomerCode", "human");
            var product = await engine.CreateTableAsync("Product", "human");
            await engine.CreateColumnAsync(product, "ProductKey", "Int64", "ProductKey", "human");
            await engine.SetObjectPropertyAsync(code, "IsKey", "true", "human");   // the table's real key, chosen by a person
            await engine.CreateRelationshipAsync("column:Facts/CustomerKey", "column:Customer/CustomerKey", null, true, "human");
            await engine.CreateRelationshipAsync("column:Facts/ProductKey", "column:Product/ProductKey", null, true, "human");
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Fix_all_never_takes_the_key_away_from_a_tables_real_key(string door)
        {
            var (engine, sessions) = await KeyedDimensionModelAsync();
            using (engine)
            {
                var before = await engine.BpaScanAsync();
                Assert.Contains(before.Violations, v => v.RuleId == "MARK_PRIMARY_KEYS" && v.ObjectRef == "column:Customer/CustomerKey" && v.CanAutoFix);
                Assert.Contains(before.Violations, v => v.RuleId == "MARK_PRIMARY_KEYS" && v.ObjectRef == "column:Product/ProductKey" && v.CanAutoFix);

                var bulk = door == "mcp"
                    ? (BpaFixAllResult)await McpTools.ApplySafeFixes(engine, "bpa")
                    : await engine.BpaFixAllAsync("human");

                var keys = await sessions.Require().ReadAsync(m => (
                    code: m.Tables["Customer"].Columns["Customer Code"].IsKey,
                    customerKey: m.Tables["Customer"].Columns["CustomerKey"].IsKey,
                    productKey: m.Tables["Product"].Columns["ProductKey"].IsKey));
                Assert.True(keys.code, "Fix all took the key away from Customer[Customer Code], the key a person chose");
                Assert.False(keys.customerKey, "Fix all set a second key on a table that already has one");
                Assert.True(keys.productKey, "a table with no key still gets its key from the fix");

                var left = Assert.Single(bulk.Unfixed, u => u.RuleId == "MARK_PRIMARY_KEYS");
                Assert.Equal("column:Customer/CustomerKey", left.ObjectRef);
                Assert.Contains("Customer Code", left.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("—", left.Reason);
            }
        }

        [Fact]
        public async Task The_plans_best_practice_row_does_not_offer_a_second_key_either()
        {
            var (engine, _) = await KeyedDimensionModelAsync();
            using (engine)
            {
                var line = new ChangeItem { ObjectRef = "column:Customer/CustomerKey", Kind = "bpa_fix", RuleId = "MARK_PRIMARY_KEYS", Target = "iskey", Before = "False", After = "True" };
                var why = await engine.BpaLineRefusalForTest(line, "column:Customer/CustomerKey");
                Assert.False(string.IsNullOrWhiteSpace(why), "the row would set a second key and take the first one away");
                Assert.Contains("Customer Code", why, StringComparison.Ordinal);
                var keyless = new ChangeItem { ObjectRef = "column:Product/ProductKey", Kind = "bpa_fix", RuleId = "MARK_PRIMARY_KEYS", Target = "iskey", Before = "False", After = "True" };
                Assert.Null(await engine.BpaLineRefusalForTest(keyless, "column:Product/ProductKey"));
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("studio")]
        [InlineData("mcp")]
        public async Task Fix_all_does_not_turn_a_double_into_a_decimal_and_says_why(string door)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake());
            using (engine)
            {
                await engine.CreateModelAsync("FixAllDouble", 1601);
                var t = await engine.CreateTableAsync("Facts", "human");
                // A rate held to six decimal places: as a Decimal it would keep four, so 0.123456 would show as 0.1235.
                await engine.CreateColumnAsync(t, "Exchange Rate", "Double", "ExchangeRate", "human");
                var before = await engine.BpaScanAsync();
                Assert.Contains(before.Violations, v => v.RuleId == "AVOID_FLOATING_POINT_DATA_TYPES" && v.ObjectRef == "column:Facts/Exchange Rate" && v.CanAutoFix);

                var bulk = door == "mcp"
                    ? (BpaFixAllResult)await McpTools.ApplySafeFixes(engine, "bpa")
                    : await engine.BpaFixAllAsync("human");

                var type = await sessions.Require().ReadAsync(m => m.Tables["Facts"].Columns["Exchange Rate"].DataType);
                Assert.Equal(TabularEditor.TOMWrapper.DataType.Double, type);
                var left = Assert.Single(bulk.Unfixed, u => u.RuleId == "AVOID_FLOATING_POINT_DATA_TYPES");
                Assert.Equal("column:Facts/Exchange Rate", left.ObjectRef);
                Assert.Contains("four decimal places", left.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("—", left.Reason);

                // The finding stays, and the person can still make the change on purpose, one finding at a time.
                var one = await engine.BpaFixAsync("AVOID_FLOATING_POINT_DATA_TYPES", "column:Facts/Exchange Rate", "human");
                Assert.True(one.Changed);
                Assert.Equal(TabularEditor.TOMWrapper.DataType.Decimal,
                    await sessions.Require().ReadAsync(m => m.Tables["Facts"].Columns["Exchange Rate"].DataType));
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

    /// <summary>
    /// Lean suite, lane B: the template-open helper for the engine classes that build their model on a bare LocalEngine and a
    /// SessionManager of their own (no DecisionFixture). Use it as an <c>IClassFixture</c>. The first test for a key builds the
    /// model once on a scratch engine (ModelTemplates) and saves it; every later test copies that file into this class's scratch
    /// folder and opens the copy in a new engine, which is one file read instead of a dozen writes on the single-writer dispatcher.
    /// A copy is the model as the build saved it: the build's undo steps are not in it, so a test that counts them keeps the build.
    /// The scratch folder is deleted when the class's last test is done, so a test that saves or waives beside its copy leaves nothing.
    /// </summary>
    public sealed class TemplateCopies : IDisposable
    {
        private readonly ModelTemplates _templates = new ModelTemplates();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sem-copies-" + Guid.NewGuid().ToString("N").Substring(0, 12));

        /// <summary>A new engine, on <paramref name="sessions"/>, with a private copy of the model <paramref name="build"/> makes
        /// (built once per key) already open.</summary>
        internal Task<LocalEngine> OpenAsync(string key, Func<LocalEngine, Task> build, IEntitlement entitlement, SessionManager sessions) =>
            OpenFromFixtureAsync(key, f => build(f.Engine), entitlement, sessions);

        /// <summary>The same, for a build that also needs the scratch fixture's SessionManager (a hide-keys mutation, say).</summary>
        internal async Task<LocalEngine> OpenFromFixtureAsync(string key, Func<DecisionFixture, Task> build, IEntitlement entitlement, SessionManager sessions)
        {
            var template = await _templates.PathAsync(key, build);
            var dir = Path.Combine(_root, Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, "Model.bim");
            File.Copy(template, copy);
            var engine = new LocalEngine(sessions, entitlement);
            await engine.OpenAsync(copy);
            return engine;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
            _templates.Dispose();
        }
    }
}
