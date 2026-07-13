using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The token-path (BpaRule.TokenCheck) versions of the two false-positive-prone DAX text rules. Same contract as
    /// DaxLintTests: the load-bearing cases are the FALSE-POSITIVE guards — a function name inside a comment / a
    /// [bracketed measure ref] must NOT flag DAX_AVOID_IFERROR, and FILTER('T'[Col]…) must NOT flag the bare-table
    /// rule — while the token path ALSO catches the unquoted-bare-table case the old "FILTER('" text form could not.
    /// An unrecognized TokenCheck value must surface a rule error (fail loud), never silently pass.
    /// </summary>
    public sealed class BpaTokenCheckTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // A tiny model with a couple of real tables so filter-bare-table can resolve unquoted identifiers to tables.
        private static async Task<(LocalEngine engine, SessionManager sessions)> BuildModelAsync(
            params (string table, string measure, string dax)[] measures)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false));
            await engine.CreateModelAsync("TokenCheckTest", 1604);
            await engine.CreateTableAsync("Sales", "human");
            await engine.CreateTableAsync("Date", "human");
            foreach (var (table, name, dax) in measures)
                await engine.CreateMeasureAsync("table:" + table, name, dax, "human");
            return (engine, sessions);
        }

        // Only the two rules under test — isolates from the rest of the default set and any model-embedded rules.
        private static readonly IReadOnlyList<BpaRule> TwoRules =
            BpaDefaultRules.Rules.Where(r =>
                r.ID == "DAX_AVOID_IFERROR" || r.ID == "DAX_AVOID_FILTER_TABLE_REFERENCE").ToList();

        private static Task<BpaScorecard> ScanAsync(SessionManager sessions, IReadOnlyList<BpaRule>? rules = null)
            => sessions.Require().ReadAsync(m => BpaAnalyzer.Analyze(m, rules ?? TwoRules));

        private static bool Flagged(BpaScorecard card, string ruleId, string objectName) =>
            card.Violations.Any(v => v.RuleId == ruleId && v.ObjectName.Contains(objectName));

        // ---- DAX_AVOID_IFERROR (calls:IFERROR) ----------------------------------------------------------------

        [Fact]
        public async Task Iferror_in_a_comment_only_is_not_flagged()
        {
            var (engine, sessions) = await BuildModelAsync(("Sales", "Commented", "// IFERROR is bad\nSUM ( Sales[Amt] )"));
            using (engine)
                Assert.False(Flagged(await ScanAsync(sessions), "DAX_AVOID_IFERROR", "Commented"));
        }

        [Fact]
        public async Task Iferror_as_a_bracketed_measure_ref_is_not_flagged()
        {
            var (engine, sessions) = await BuildModelAsync(("Sales", "RefsError", "[IfError Rate] * 2"));
            using (engine)
                Assert.False(Flagged(await ScanAsync(sessions), "DAX_AVOID_IFERROR", "RefsError"));
        }

        [Fact]
        public async Task Real_iferror_call_is_flagged()
        {
            var (engine, sessions) = await BuildModelAsync(("Sales", "RealIferror", "IFERROR ( SUM(Sales[Amt]) / SUM(Sales[Qty]), 0 )"));
            using (engine)
                Assert.True(Flagged(await ScanAsync(sessions), "DAX_AVOID_IFERROR", "RealIferror"));
        }

        // ---- DAX_AVOID_FILTER_TABLE_REFERENCE (filter-bare-table) ---------------------------------------------

        [Fact]
        public async Task Filter_over_a_bare_quoted_table_is_flagged()
        {
            var (engine, sessions) = await BuildModelAsync(
                ("Sales", "QuotedBare", "CALCULATE ( SUM(Sales[Amt]), FILTER ( 'Sales', Sales[Amt] > 0 ) )"));
            using (engine)
                Assert.True(Flagged(await ScanAsync(sessions), "DAX_AVOID_FILTER_TABLE_REFERENCE", "QuotedBare"));
        }

        [Fact]
        public async Task Filter_over_an_unquoted_table_name_is_flagged_the_old_rule_missed_this()
        {
            var (engine, sessions) = await BuildModelAsync(
                ("Sales", "UnquotedBare", "CALCULATE ( SUM(Sales[Amt]), FILTER ( Sales, Sales[Amt] > 0 ) )"));
            using (engine)
                Assert.True(Flagged(await ScanAsync(sessions), "DAX_AVOID_FILTER_TABLE_REFERENCE", "UnquotedBare"));
        }

        [Fact]
        public async Task Filter_over_values_of_a_column_is_not_flagged()
        {
            var (engine, sessions) = await BuildModelAsync(
                ("Sales", "FilterValues", "CALCULATE ( SUM(Sales[Amt]), FILTER ( VALUES ( Sales[Cat] ), Sales[Amt] > 0 ) )"));
            using (engine)
                Assert.False(Flagged(await ScanAsync(sessions), "DAX_AVOID_FILTER_TABLE_REFERENCE", "FilterValues"));
        }

        [Fact]
        public async Task Filter_over_a_qualified_column_head_is_not_flagged()
        {
            // FILTER('Sales'[Amt], …) — the quoted-table + [column] is TWO tokens, so it's not a bare table ref.
            var (engine, sessions) = await BuildModelAsync(
                ("Sales", "QualifiedCol", "CALCULATE ( SUM(Sales[Amt]), FILTER ( 'Sales'[Amt], Sales[Amt] > 0 ) )"));
            using (engine)
                Assert.False(Flagged(await ScanAsync(sessions), "DAX_AVOID_FILTER_TABLE_REFERENCE", "QualifiedCol"));
        }

        // ---- a token-ONLY rule (no Expression) must evaluate — TokenCheck REPLACES Expression ------------------

        [Fact]
        public async Task Token_only_rule_without_expression_still_evaluates()
        {
            var (engine, sessions) = await BuildModelAsync(("Sales", "RealIferror", "IFERROR ( SUM(Sales[Amt]), 0 )"));
            using (engine)
            {
                // The documented contract: TokenCheck REPLACES Dynamic-LINQ evaluation, so Expression is optional.
                // The old Expression-required guard silently dropped exactly this rule (adversarial-review find).
                var tokenOnly = new List<BpaRule>
                {
                    new BpaRule
                    {
                        ID = "TOKEN_ONLY", Name = "Token only", Category = "DAX Expressions", Severity = 1, Scope = "Measure",
                        TokenCheck = "calls:IFERROR", Description = "%object% uses IFERROR.",
                    },
                };
                var card = await ScanAsync(sessions, tokenOnly);
                Assert.True(Flagged(card, "TOKEN_ONLY", "RealIferror"));
                Assert.Empty(card.RuleErrors);
            }
        }

        // ---- fail-loud: an unrecognized TokenCheck surfaces a rule error, never a silent pass ------------------

        [Fact]
        public async Task Unknown_token_check_surfaces_a_rule_error()
        {
            var (engine, sessions) = await BuildModelAsync(("Sales", "Anything", "SUM ( Sales[Amt] )"));
            using (engine)
            {
                var bogus = new List<BpaRule>
                {
                    new BpaRule
                    {
                        ID = "DAX_BOGUS", Name = "Bogus", Category = "DAX Expressions", Severity = 1, Scope = "Measure",
                        Expression = "Expression <> null", TokenCheck = "nonsense-check",
                        Description = "should never evaluate",
                    },
                };
                var card = await ScanAsync(sessions, bogus);
                Assert.Empty(card.Violations);   // no silent pass masquerading as a violation…
                Assert.Contains(card.RuleErrors, e => e.Contains("DAX_BOGUS") && e.Contains("unsupported token check"));
            }
        }
    }
}
