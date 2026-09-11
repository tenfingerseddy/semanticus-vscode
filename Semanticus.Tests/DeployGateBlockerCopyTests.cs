using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The deploy gate told a user a NUMBER, not what to do. Live-verified 2026-07-30 against two real Power BI
    /// Desktop models: the whole violation blocker was the bare string "41 blocking BPA error(s)", which names no
    /// rule, no object and no action, and the unknown blocker handed over a raw rule ID plus our internal doctrine
    /// wording ("coverage unknown, not clean").
    ///
    /// Three faults, all fixed here and each pinned by a test below:
    ///   1. "BPA" is engine vocabulary and the UI is never allowed to speak it (docs/product-copy-style.md rules 2, 5).
    ///   2. The gate counted <c>Severity &gt;= 2</c> and called every one of them an "error", while
    ///      <c>Bpa.cs</c> documents severity 2 as a WARNING and 3 as an error. A user with 41 warnings was told
    ///      they had 41 errors. The comment-versus-code class this project keeps finding.
    ///   3. Neither message told anyone what to do next.
    ///
    /// This is a COPY AND REPORTING change only. <c>Verdict_and_counts_did_not_move</c> is the guard that stops it
    /// becoming a behaviour change: the blocking predicate is still <c>Severity &gt;= 2 &amp;&amp; !CanAutoFix
    /// &amp;&amp; !Waived</c>, severity 2 still blocks, and the counts below were measured on the pre-change engine
    /// (c06e39a6) and pasted in, not read back from the new code.
    /// </summary>
    public sealed class DeployGateBlockerCopyTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        // Semanticus's own em dash guard. The character, never typed literally into an assertion.
        private const char EmDash = '—';

        // A bare rule id in the bundled corpus is SHOUTING_SNAKE_CASE ("MODEL_SHOULD_HAVE_A_DATE_TABLE"). Rule NAMES
        // never look like this (verified against all 74 shipped names in Shipped_rule_names_carry_nothing_the_copy_
        // rules_forbid), so an underscore joining two upper-case runs is a reliable id fingerprint and does not
        // false-fire on the short acronyms the copy guide permits (DAX, RLS, Q&A).
        private static readonly Regex BareRuleId = new Regex(@"[A-Z0-9]+_[A-Z0-9_/]*[A-Z0-9]", RegexOptions.Compiled);

        // "BPA" in any spelling a person might actually write it. The first version of this check tested only the
        // exact upper-case substring, so "bpa", "Bpa" or "B.P.A." all sailed through a check whose whole job was to
        // keep engine vocabulary out of user copy. Caught by Sol on review. Word boundaries are what keep it honest:
        // without them, stripping punctuation makes "cab package" match on the "bpa" spanning its two words.
        private static readonly Regex EngineJargon =
            new Regex(@"\bb[\s.\-_]?p[\s.\-_]?a\b|\bbest\s+practice\s+analy[sz]er\b",
                      RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // ---- fixtures ------------------------------------------------------------------------------------------

        // The clean, gate-PASSING model DeployFeatureTests uses, so anything red below can only come from what the
        // individual fixture removes or adds.
        private static async Task<LocalEngine> CleanModelAsync(string name)
        {
            var engine = new LocalEngine(new SessionManager(), new Fake());
            await engine.CreateModelAsync(name, 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            var amt = await engine.CreateColumnAsync(t, "Sales Amount", "Decimal", "Sales Amount", "human");
            await engine.CreateColumnAsync(t, "Customer Name", "String", "Customer Name", "human");
            await engine.SetColumnMetadataAsync(amt, true, null, null, null, "human");
            var mref = await engine.CreateMeasureAsync(t, "Total Sales", "SUM ( Sales[Sales Amount] )", "human");
            await engine.SetMeasureFormatAsync(mref, "#,0", "human");
            await engine.SetDescriptionAsync(mref, "The sum of all sales amounts across the model.", "human");
            var dt = await engine.CreateTableAsync("Date", "human");
            var dkey = await engine.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await engine.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await engine.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            await engine.MarkDateTableAsync(dt, "Date", "human");
            return engine;
        }

        // Kane's own case: the clean model with the date table NOT marked. The two date rules
        // (MODEL_SHOULD_HAVE_A_DATE_TABLE and DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE) are the ONLY
        // blockers, and both are severity 2 — a warning, not an error. That makes one fixture serve the
        // date-table case and the severity-words case at once.
        private static async Task<LocalEngine> DateTableOnlyAsync()
        {
            var engine = new LocalEngine(new SessionManager(), new Fake());
            await engine.CreateModelAsync("NoDateTable", 1604);
            var t = await engine.CreateTableAsync("Sales", "human");
            var amt = await engine.CreateColumnAsync(t, "Sales Amount", "Decimal", "Sales Amount", "human");
            await engine.CreateColumnAsync(t, "Customer Name", "String", "Customer Name", "human");
            await engine.SetColumnMetadataAsync(amt, true, null, null, null, "human");
            var mref = await engine.CreateMeasureAsync(t, "Total Sales", "SUM ( Sales[Sales Amount] )", "human");
            await engine.SetMeasureFormatAsync(mref, "#,0", "human");
            await engine.SetDescriptionAsync(mref, "The sum of all sales amounts across the model.", "human");
            var dt = await engine.CreateTableAsync("Date", "human");
            var dkey = await engine.CreateColumnAsync(dt, "Date", "DateTime", "Date", "human");
            await engine.SetObjectPropertyAsync(dkey, "IsKey", "true", "human");
            await engine.SetObjectPropertyAsync(dkey, "FormatString", "yyyy-mm-dd", "human");
            // deliberately NOT MarkDateTableAsync
            return engine;
        }

        // The unmarked-date-table model PLUS a visible measure with no format string, which is
        // PROVIDE_FORMAT_STRING_FOR_MEASURES at severity 3 — a real error. So this fixture carries both levels and
        // proves the copy reports them with different words.
        private static async Task<LocalEngine> ErrorsAndWarningsAsync()
        {
            var engine = await DateTableOnlyAsync();
            var m = await engine.CreateMeasureAsync("table:Sales", "Unformatted Sales", "SUM ( Sales[Sales Amount] )", "human");
            await engine.SetDescriptionAsync(m, "A measure left without a format string on purpose.", "human");
            return engine;
        }

        // A rule that throws part way through its scope, so the scan cannot finish it. Same construction
        // BpaUnknownScopeTests uses; the name is what the copy must show instead of the id.
        private const string UnevaluableRule =
            "[{\"ID\":\"SEM_PROBE_UNEVALUABLE\",\"Name\":\"Measure names are at least three characters\",\"Category\":\"Test\"," +
            "\"Severity\":3,\"Scope\":\"Measure\",\"Expression\":\"Name.Substring(3).Length >= 0\"}]";

        // The mixed model PLUS the rule above PLUS the object it throws on. The short name is load-bearing:
        // Name.Substring(3) only throws on a name under three characters, so without a measure named "AB" the rule
        // evaluates cleanly and there is NO unknown to report. Measured 2026-07-30: the first draft of this fixture
        // omitted it and produced two extra violations instead of an unknown.
        private static async Task<LocalEngine> WithUnevaluableRuleAsync()
        {
            var engine = await ErrorsAndWarningsAsync();
            var m = await engine.CreateMeasureAsync("table:Sales", "AB", "1", "human");
            await engine.SetMeasureFormatAsync(m, "#,0", "human");
            await engine.SetDescriptionAsync(m, "A deliberately short measure name for the throw case.", "human");
            await engine.LoadBpaRulesAsync(UnevaluableRule, replace: false, "human");
            return engine;
        }

        private static string ViolationBlocker(DeployGate g) =>
            g.Blockers.FirstOrDefault(b => !b.Contains("could not", StringComparison.OrdinalIgnoreCase)
                                        && !b.Contains("undescribed", StringComparison.OrdinalIgnoreCase));

        private static string UnknownBlocker(DeployGate g) =>
            g.Blockers.FirstOrDefault(b => b.Contains("could not", StringComparison.OrdinalIgnoreCase));

        // ---- 1) the violation blocker names a rule NAME and an OBJECT ------------------------------------------

        [Fact]
        public async Task Violation_blocker_names_a_rule_by_name_and_an_object()
        {
            using var engine = await ErrorsAndWarningsAsync();
            var gate = await engine.DeployGateAsync(null, "human");
            Assert.False(gate.Pass);
            var msg = ViolationBlocker(gate);
            Assert.NotNull(msg);

            // The rule's own name, not its id, and the object it fired on.
            Assert.Contains("Date/calendar tables should be marked as a date table", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Provide format string for measures", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Unformatted Sales", msg, StringComparison.Ordinal);
        }

        // ---- 2) no engine vocabulary, no raw ids, no em dash --------------------------------------------------

        [Fact]
        public async Task Blocker_copy_speaks_no_engine_vocabulary()
        {
            using var engine = await WithUnevaluableRuleAsync();
            var gate = await engine.DeployGateAsync(null, "human");
            // Both blocker paths must be present, or this test would be checking only half the copy.
            Assert.Equal(2, gate.Blockers.Length);
            Assert.Equal(1, gate.BpaUnknownBlocking);

            foreach (var s in gate.Blockers.Concat(new[] { gate.Note }))
            {
                var jargon = EngineJargon.Match(s);
                Assert.False(jargon.Success, "engine vocabulary leaked into user-facing copy: '" + jargon.Value + "' in: " + s);
                Assert.DoesNotContain("coverage unknown, not clean", s, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(EmDash, s);

                // Raw ids are forbidden in PRIMARY copy and allowed only in the unknown path's trailing id clause,
                // which exists so an engineer still gets the exact identifier. The split uses the marker the code
                // itself publishes, so the test cannot drift from the boundary it is asserting.
                var cut = s.IndexOf(GateBlockerCopy.IdClauseMarker, StringComparison.Ordinal);
                var primary = cut < 0 ? s : s.Substring(0, cut);
                var id = BareRuleId.Match(primary);
                Assert.False(id.Success, "a raw rule id leaked into PRIMARY user-facing copy: '" + id.Value + "' in: " + primary);
            }

            // The unknown path specifically: the rule's NAME, the two facts that must survive any rewrite, and the
            // id carried where an engineer can find it.
            var unk = UnknownBlocker(gate);
            Assert.NotNull(unk);
            Assert.Contains("Measure names are at least three characters", unk, StringComparison.Ordinal);
            Assert.Contains("waiver cannot clear it", unk, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not the same as clean", unk, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Rule id: SEM_PROBE_UNEVALUABLE.", unk, StringComparison.Ordinal);
        }

        // The copy emits rule names VERBATIM, so the shipped corpus is part of the user-facing surface and asserting
        // one fixture's name proves nothing about the other 73. This checks all of them. If a future corpus bump
        // brings in a name carrying an em dash, the word BPA, or an id-shaped token, this test is where it surfaces,
        // and that is a finding to be decided rather than a check to be loosened.
        [Fact]
        public void Shipped_rule_names_carry_nothing_the_copy_rules_forbid()
        {
            var rules = BpaRuleSet.Standard();
            Assert.Equal(74, rules.Count);   // measured 2026-07-30; a corpus change should be a deliberate one

            foreach (var r in rules)
            {
                Assert.False(string.IsNullOrWhiteSpace(r.Name), r.ID + " has no name, so the copy would fall back to its id");
                Assert.DoesNotContain(EmDash, r.Name);
                var jargon = EngineJargon.Match(r.Name);
                Assert.False(jargon.Success, "shipped rule name speaks engine vocabulary ('" + jargon.Value + "'): " + r.Name);
                var id = BareRuleId.Match(r.Name);
                Assert.False(id.Success, "shipped rule name contains an id-shaped token ('" + id.Value + "'), which would "
                             + "defeat the raw-id check in the blocker copy: " + r.Name);
            }
        }

        // One rule, TWO scopes, throwing on both. Name.Substring(3) throws on the short measure and on the short
        // table, so the scan produces TWO BpaRuleUnknown records that share a rule id AND a display name and differ
        // only in scope. This is the case that exposed the name-collapse defect: the gate counts two, so the copy
        // must say two.
        private const string UnevaluableAtTwoScopes =
            "[{\"ID\":\"SEM_PROBE_TWO_SCOPES\",\"Name\":\"Names are at least three characters\",\"Category\":\"Test\"," +
            "\"Severity\":3,\"Scope\":\"Measure, Table\",\"Expression\":\"Name.Substring(3).Length >= 0\"}]";

        // ---- 2b) an unknown at two scopes is TWO checks, and the copy may not collapse them ------------------

        [Fact]
        public async Task An_unknown_at_two_scopes_is_rendered_as_two_and_never_collapsed_by_name()
        {
            using var engine = await DateTableOnlyAsync();
            await engine.CreateTableAsync("AB", "human");               // a table name under three characters
            var m = await engine.CreateMeasureAsync("table:Sales", "CD", "1", "human");
            await engine.SetMeasureFormatAsync(m, "#,0", "human");
            await engine.SetDescriptionAsync(m, "A deliberately short measure name for the throw case.", "human");
            await engine.LoadBpaRulesAsync(UnevaluableAtTwoScopes, replace: false, "human");

            var card = await engine.BpaScanAsync();
            var blockingUnknowns = card.Unknowns.Where(u => u.BlocksGate && u.RuleId == "SEM_PROBE_TWO_SCOPES").ToArray();
            Assert.Equal(2, blockingUnknowns.Length);
            Assert.Single(blockingUnknowns.Select(u => u.RuleName).Distinct());   // same NAME, so a name key collapses them

            var gate = await engine.DeployGateAsync(null, "human");
            var unk = UnknownBlocker(gate);
            Assert.NotNull(unk);

            // The lead count is the gate's own count. These two agreeing is the whole invariant: the message and the
            // field cannot tell the reader different things.
            Assert.Equal(2, gate.BpaUnknownBlocking);
            Assert.StartsWith("2 checks could not run", unk, StringComparison.Ordinal);
            // Both scopes are visible, so the reader can see why two entries are two.
            Assert.Contains("(on Measure)", unk, StringComparison.Ordinal);
            Assert.Contains("(on Table)", unk, StringComparison.Ordinal);
            // The id appears ONCE in the trailing clause: distinct ids, not one per pair.
            Assert.Contains("Rule id: SEM_PROBE_TWO_SCOPES.", unk, StringComparison.Ordinal);
        }

        // ---- 3) the date-table case names the rule and an action ---------------------------------------------

        [Fact]
        public async Task The_missing_date_table_case_names_the_rule_and_what_to_do()
        {
            using var engine = await DateTableOnlyAsync();
            var gate = await engine.DeployGateAsync(null, "human");
            Assert.False(gate.Pass, "the unmarked date table must still block; blockers were: " + string.Join(" | ", gate.Blockers));
            var msg = ViolationBlocker(gate);
            Assert.NotNull(msg);

            Assert.Contains("marked as a date table", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Date", msg, StringComparison.Ordinal);
            // An action, not just a count: the copy must tell the reader what to do with these findings.
            Assert.Contains("Fix ", msg, StringComparison.Ordinal);
        }

        // ---- 4) severity WORDS match severity NUMBERS --------------------------------------------------------

        [Fact]
        public async Task Severity_words_match_severity_numbers()
        {
            // THE EXPECTED SPLIT IS A LITERAL, measured on the pre-change engine (c06e39a6) with the same throwaway
            // diagnostic that produced the guard baselines. It used to be derived from the live scan, which made the
            // test self-supplied as behaviour evidence: it could only ever prove the copy agreed with whatever the
            // scan had just said, never that the split itself was right. Caught by Sol on review.
            //
            // dateTableOnly:      0 errors, 2 warnings (MODEL_SHOULD_HAVE_A_DATE_TABLE and DATE/CALENDAR..., both sev 2)
            // errorsAndWarnings:  1 error,  4 warnings (PROVIDE_FORMAT_STRING_FOR_MEASURES sev 3; + AVOID_DUPLICATE x2)
            // withUnknown:        3 errors, 4 warnings (+ SEM_PROBE_UNEVALUABLE sev 3 on two measures)

            // Only severity-2 findings block here, so nothing may be called an error.
            using var warnOnly = await DateTableOnlyAsync();
            var card = await warnOnly.BpaScanAsync();
            var blocking = card.Violations.Where(v => v.Severity >= 2 && !v.CanAutoFix && !v.Waived).ToArray();
            Assert.Equal(2, blocking.Length);
            Assert.All(blocking, v => Assert.Equal(2, v.Severity));

            var msg = ViolationBlocker(await warnOnly.DeployGateAsync(null, "human"));
            Assert.DoesNotContain("error", msg, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("2 warnings to fix", msg, StringComparison.Ordinal);

            // With severity-3 findings present, BOTH words appear and each carries its own literal count.
            using var mixed = await ErrorsAndWarningsAsync();
            var mixedMsg = ViolationBlocker(await mixed.DeployGateAsync(null, "human"));
            Assert.StartsWith("1 error and 4 warnings to fix", mixedMsg, StringComparison.Ordinal);

            using var withUnknown = await WithUnevaluableRuleAsync();
            var unkMsg = ViolationBlocker(await withUnknown.DeployGateAsync(null, "human"));
            Assert.StartsWith("3 errors and 4 warnings to fix", unkMsg, StringComparison.Ordinal);
        }

        // ---- 5) the guard: the DECISION did not move ---------------------------------------------------------

        // The blocking SET for a fixture: one line per blocking finding, "RULE_ID|objectRef", sorted. The rule id and
        // the object ref together are what identifies a finding, and they are the two things a copy change must not
        // be able to touch.
        private static string[] BlockingSet(BpaScorecard card) => card.Violations
            .Where(v => v.Severity >= 2 && !v.CanAutoFix && !v.Waived)
            .Select(v => v.RuleId + "|" + v.ObjectRef)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        private static string[] UnknownSet(BpaScorecard card) => card.Unknowns
            .Where(u => u.BlocksGate)
            .Select(u => u.RuleId + "|" + u.Scope)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        // Every value here was MEASURED ON THE PRE-CHANGE ENGINE (main at c06e39a6, 2026-07-30) with a throwaway
        // diagnostic that dumped each fixture's verdict, counts and blocking findings, then pasted in. They were not
        // read back out of the new code, which is the only thing that makes this a guard rather than a restatement.
        //
        // IT ASSERTS THE SET, NOT JUST THE COUNTS. The first version checked verdicts and counts only, which meant an
        // equal-size SWAP of one blocker for another passed it cleanly: the count-instead-of-a-set defect, sitting
        // inside the very test written to stop a behaviour change. Caught by Sol on review, and proved by mutation
        // rather than argued: renaming the fixture's "Date" table to "Calendar" keeps the count at 2 and still turns
        // this test red, on the object ref, exactly as it must.
        [Fact]
        public async Task Verdict_and_blocking_set_did_not_move()
        {
            using var clean = await CleanModelAsync("StillPasses");
            var c = await clean.DeployGateAsync(null, "human");
            Assert.True(c.Pass, "blockers were: " + string.Join(" | ", c.Blockers));
            Assert.Empty(c.Blockers);
            Assert.Equal(0, c.BpaBlocking);
            Assert.Equal(0, c.BpaUnknownBlocking);
            Assert.Equal(8, c.BpaViolations);
            Assert.Equal("Gate passed.", c.Note);
            Assert.Empty(BlockingSet(await clean.BpaScanAsync()));

            using var dateOnly = await DateTableOnlyAsync();
            var d = await dateOnly.DeployGateAsync(null, "human");
            Assert.False(d.Pass);
            Assert.Single(d.Blockers);
            Assert.Equal(2, d.BpaBlocking);          // the two severity-2 date rules, neither auto-fixable
            Assert.Equal(0, d.BpaUnknownBlocking);
            Assert.Equal(0, d.BpaWaivedBlocking);
            Assert.Equal(10, d.BpaViolations);
            Assert.Equal(new[]
            {
                "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE|table:Date",
                "MODEL_SHOULD_HAVE_A_DATE_TABLE|obj:Model/Model",
            }, BlockingSet(await dateOnly.BpaScanAsync()));

            using var mixed = await ErrorsAndWarningsAsync();
            var m = await mixed.DeployGateAsync(null, "human");
            Assert.False(m.Pass);
            Assert.Single(m.Blockers);
            // 5, not 3: the second measure duplicates the first one's expression, so AVOID_DUPLICATE_MEASURES fires
            // on BOTH measures on top of the format-string error and the two date rules. Measured, not assumed.
            Assert.Equal(5, m.BpaBlocking);
            Assert.Equal(0, m.BpaUnknownBlocking);
            Assert.Equal(14, m.BpaViolations);
            Assert.Equal(new[]
            {
                "AVOID_DUPLICATE_MEASURES|measure:Sales/Total Sales",
                "AVOID_DUPLICATE_MEASURES|measure:Sales/Unformatted Sales",
                "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE|table:Date",
                "MODEL_SHOULD_HAVE_A_DATE_TABLE|obj:Model/Model",
                "PROVIDE_FORMAT_STRING_FOR_MEASURES|measure:Sales/Unformatted Sales",
            }, BlockingSet(await mixed.BpaScanAsync()));

            using var withUnknown = await WithUnevaluableRuleAsync();
            var u = await withUnknown.DeployGateAsync(null, "human");
            Assert.False(u.Pass);
            Assert.Equal(7, u.BpaBlocking);
            Assert.Equal(1, u.BpaUnknownBlocking);   // severity 3, no auto-fix, so its unknown blocks
            Assert.Equal(2, u.Blockers.Length);      // one violation blocker, one unknown blocker
            Assert.Equal(16, u.BpaViolations);
            var uCard = await withUnknown.BpaScanAsync();
            Assert.Equal(new[]
            {
                "AVOID_DUPLICATE_MEASURES|measure:Sales/Total Sales",
                "AVOID_DUPLICATE_MEASURES|measure:Sales/Unformatted Sales",
                "DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE|table:Date",
                "MODEL_SHOULD_HAVE_A_DATE_TABLE|obj:Model/Model",
                "PROVIDE_FORMAT_STRING_FOR_MEASURES|measure:Sales/Unformatted Sales",
                "SEM_PROBE_UNEVALUABLE|measure:Sales/Total Sales",
                "SEM_PROBE_UNEVALUABLE|measure:Sales/Unformatted Sales",
            }, BlockingSet(uCard));
            // The unknown that blocks is a rule/SCOPE pair, and the pair is what the copy must render one of.
            Assert.Equal(new[] { "SEM_PROBE_UNEVALUABLE|Measure" }, UnknownSet(uCard));
        }
    }
}
