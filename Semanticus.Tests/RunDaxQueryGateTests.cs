using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// #129 — the QueryData gate for ROW-returning run_dax. run_dax maps to QueryCalc (a scalar evaluation is a
    /// calculation, allowed everywhere), but `EVALUATE &lt;table&gt;` returns real source rows — the SAME exfiltration
    /// surface preview_table gates. Two layers of proof: the pure shape classifier (the fail-closed grammar) and the
    /// engine wiring (a row query routes through GuardAgent(QueryData, …) with preview_table's session-grant semantics,
    /// a scalar probe stays ungated even on a deny target).
    /// </summary>
    public sealed class DaxQueryClassifierTests
    {
        // ---- SCALAR shapes stay ungated (the verified-measure / probe forms that run constantly) ----
        [Theory]
        [InlineData("EVALUATE ROW(\"v\", [Total Sales])")]
        [InlineData("EVALUATE ROW( \"v\", CALCULATE([Total Sales], 'Date'[Year] = 2024) )")]
        [InlineData("EVALUATE { 1, 2, 3 }")]
        [InlineData("EVALUATE {[Total Sales]}")]
        [InlineData("DEFINE MEASURE 'Sales'[M] = SUM(Sales[Amount])\nEVALUATE ROW(\"v\", [M])")]
        [InlineData("EVALUATE (ROW(\"v\", [M]))")]                              // leading paren peeled
        [InlineData("evaluate row(\"v\", [m])")]                               // case-insensitive
        [InlineData("EVALUATE ROW(\"v\", EVALUATEANDLOG([M]))")]                // EVALUATEANDLOG is a function, not the statement
        [InlineData("EVALUATE ROW(\"note\", \"EVALUATE 'Sales'\")")]           // an EVALUATE / table-ref inside a string is masked
        [InlineData("EVALUATE ROW(\"v\", [M]) // EVALUATE 'Sales'")]           // ...and inside a line comment
        [InlineData("EVALUATE ROW(\"a\", [M])\nEVALUATE { 1 }")]               // every statement is scalar-shaped
        [InlineData("-- EVALUATE 'Sales'\nEVALUATE ROW(\"v\", [M])")]          // the -- line-comment form is masked too
        [InlineData("/* EVALUATE 'Sales' */\nEVALUATE { 1 }")]                // block comment is masked
        [InlineData("EVALUATE ROW(\"a \"\"quoted\"\" label\", [M])")]          // doubled-quote escape inside a string
        [InlineData("EVALUATE ROW(\"v\", CALCULATE([M], 'It''s Sales'[X] = 1))")]   // doubled-quote escape inside a table ref
        [InlineData("EVALUATE ROW (\"v\", [M])")]                              // whitespace between ROW and its paren
        [InlineData("EVALUATE\tROW(\n\"v\", [M])")]                            // tabs/newlines as whitespace
        [InlineData("EVALUATE ROW(\"v\", [M])")]                          // NBSP: IsWhiteSpace covers it; a lexer that rejects it errors server-side — no rows either way
        public void Scalar_shapes_are_not_row_returning(string q)
        {
            Assert.True(DaxQueryClassifier.IsScalar(q), q);
            Assert.False(DaxQueryClassifier.IsRowReturning(q), q);
        }

        // ---- ROW-returning + ambiguous shapes are gated (fail closed) ----
        [Theory]
        [InlineData("EVALUATE 'Sales'")]                                       // bare table reference
        [InlineData("EVALUATE Sales")]                                         // unquoted table reference
        [InlineData("EVALUATE SUMMARIZECOLUMNS('Date'[Year], \"S\", [M])")]    // table function
        [InlineData("EVALUATE FILTER('Sales', [Amount] > 0)")]
        [InlineData("EVALUATE TOPN(10, 'Sales')")]
        [InlineData("EVALUATE VALUES('Sales'[Region])")]                       // distinct source values ARE data
        [InlineData("EVALUATE ROWNUMBER('Sales')")]                            // ROW-prefixed, but NOT the row constructor
        [InlineData("EVALUATE VAR _v = [M] RETURN ROW(\"v\", _v)")]            // ambiguous — can't see past VAR/RETURN ⇒ fail closed
        [InlineData("DEFINE MEASURE 'Sales'[M] = 1")]                         // no readable EVALUATE ⇒ fail closed
        [InlineData("EVALUATE ROW(\"a\", [M])\nEVALUATE 'Sales'")]             // one row-returning statement gates the batch
        [InlineData("")]                                                       // empty ⇒ fail closed
        [InlineData("   ")]
        [InlineData("/* outer /* inner */ EVALUATE 'Sales'")]                  // block comments do NOT nest: the first */ ends it, the table scan is live ⇒ gated
        [InlineData("EVALUATE 'ROW'")]                                         // a quoted TABLE literally named ROW is a table ref, not the row constructor
        [InlineData("EVALUATE ROW")]                                           // bare ROW identifier (no call paren) ⇒ not the constructor ⇒ fail closed
        [InlineData("EVALUATE -- ROW(\"v\", [M])\n'Sales'")]                   // a line comment eating ROW leaves a live table ref
        [InlineData("ЕVALUATE ROW(\"v\", [M])")]                              // homoglyph EVALUATE (Cyrillic 'Е'): not a readable EVALUATE ⇒ fail closed
        public void Row_and_ambiguous_shapes_are_row_returning(string q)
        {
            Assert.True(DaxQueryClassifier.IsRowReturning(q), q);
            Assert.False(DaxQueryClassifier.IsScalar(q), q);
        }
    }

    /// <summary>
    /// The engine wiring: a row-returning run_dax by the AGENT is gated on the connected target's QueryData policy —
    /// the identical GuardAgent(QueryData, …, consumeGrant:false) call preview_table uses — while a scalar probe is
    /// never gated. Driven against a never-opened live stub (LiveConnection.ForTest): a query that PASSES the gate
    /// reaches execution and fails with a connection error (never a policy refusal), which is exactly the signal we
    /// assert on. The policy/registry/ledger roots are redirected to a scratch dir so the real home is never touched.
    /// </summary>
    [Collection("restore-root")]   // mutates the static AgentPolicyStore/ApprovalLedger roots — serialize with the family
    public sealed class RunDaxGateTests : IDisposable
    {
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS";
        private readonly string _root;
        private readonly string _safeRoot;

        public RunDaxGateTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-rundax-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            AgentPolicyStore.RootOverride = _root;
            ApprovalLedger.RootOverride = _root;
            ConnectionRegistry.RootOverride = _root;
        }
        public void Dispose()
        {
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot; ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        private LocalEngine Connected()
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(pro: true));
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", Endpoint));   // DataSource = Endpoint, Database = ""
            return engine;
        }

        // A QueryData=DENY posture: the 'client' preset denies data preview off UAT/prod, and the target is labelled prod.
        private void DenyQueryData()
        {
            AgentPolicyStore.SetPreset("client", "human", isPro: true);
            var rec = ConnectionRegistry.Remember("xmla", Endpoint, "");   // "" matches the ForTest stub's empty Database
            ConnectionRegistry.SetLabel(rec.Id, "prod", "human");
        }

        // ---- a genuinely scalar EVALUATE ROW(...) is a calculation: NEVER gated, even on a deny target ----
        [Fact]
        public async Task Scalar_dax_passes_ungated_on_a_querydata_deny_target()
        {
            using var engine = Connected();
            DenyQueryData();

            var r = await engine.RunDaxAsync("EVALUATE ROW(\"v\", [Total Sales])", 100, origin: "agent");

            // It reached execution (and failed on the never-opened stub) — it was NOT refused by the policy.
            Assert.DoesNotContain("does not permit", r.Error ?? "");
            Assert.DoesNotContain("approval", r.Error ?? "", StringComparison.OrdinalIgnoreCase);
        }

        // ---- a bare EVALUATE over a table reads rows: gated, and DENY has no approval path ----
        [Fact]
        public async Task Bare_evaluate_over_a_table_is_refused_on_a_deny_target()
        {
            using var engine = Connected();
            DenyQueryData();

            var r = await engine.RunDaxAsync("EVALUATE 'Sales'", 100, origin: "agent");

            Assert.Contains("does not permit", r.Error);   // the DENY refusal names the rule + who can recover it
            Assert.Contains("previewing rows of data", r.Error);
        }

        // ---- an ambiguous shape we cannot confirm scalar is treated as row-returning (fail closed) ----
        [Fact]
        public async Task Ambiguous_shape_is_refused_fail_closed_on_a_deny_target()
        {
            using var engine = Connected();
            DenyQueryData();

            // Semantically a single-value probe, but the classifier cannot see the ROW past VAR/RETURN ⇒ gate it.
            var r = await engine.RunDaxAsync("EVALUATE VAR _v = [Total Sales] RETURN ROW(\"v\", _v)", 100, origin: "agent");

            Assert.Contains("does not permit", r.Error);
        }

        // ---- the session-grant loop: an approved QueryData grant lets subsequent row queries through WITHOUT being
        //      consumed (a time-boxed session, not one-shot) — the same semantics preview_table uses ----
        [Fact]
        public async Task A_querydata_grant_allows_row_queries_without_being_consumed()
        {
            using var engine = Connected();   // default 'standard' preset; the unlabelled target resolves to prod ⇒ Ask

            var q = "EVALUATE 'Sales'";
            var r1 = await engine.RunDaxAsync(q, 100, origin: "agent");
            Assert.Contains("approval", r1.Error, StringComparison.OrdinalIgnoreCase);   // Ask ⇒ refused, request queued
            var pending = Assert.Single(ApprovalLedger.List());
            Assert.Equal(pending.Id, r1.ApprovalId);                                     // the UI routes to this exact card

            ApprovalLedger.Approve(pending.Id, "human");                                 // the human grants it in the UI

            var r2 = await engine.RunDaxAsync(q, 100, origin: "agent");                  // retry: the live grant lets it through
            Assert.DoesNotContain("does not permit", r2.Error ?? "");
            Assert.DoesNotContain("approval", r2.Error ?? "", StringComparison.OrdinalIgnoreCase);

            var r3 = await engine.RunDaxAsync(q, 100, origin: "agent");                  // a SECOND query: still allowed…
            Assert.DoesNotContain("approval", r3.Error ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Single(ApprovalLedger.List());                                        // …the grant was never consumed
        }

        // ---- a human at the same door is never gated (the matrix restrains the AGENT, not the authority) ----
        [Fact]
        public async Task Human_row_query_is_never_gated()
        {
            using var engine = Connected();
            DenyQueryData();

            var r = await engine.RunDaxAsync("EVALUATE 'Sales'", 100, origin: "human");

            Assert.DoesNotContain("does not permit", r.Error ?? "");
        }

        // ---- evaluate_and_log is gated UNCONDITIONALLY at agent origin, like pivot_measure: the EVALUATEANDLOG log
        //      channel samples arbitrary table rows regardless of the OUTER query shape (a scalar shell like
        //      EVALUATE ROW("v", COUNTROWS(EVALUATEANDLOG('Sales'))) still logs Sales rows), so shape classification
        //      is deliberately NOT used for this op ----
        [Fact]
        public async Task EvaluateAndLog_agent_is_gated_even_for_a_scalar_shaped_query()
        {
            using var engine = Connected();
            DenyQueryData();

            // A SCALAR-shaped query at agent origin is still refused — the stronger, shape-independent rule.
            var q = "EVALUATE ROW(\"v\", COUNTROWS(EVALUATEANDLOG('Sales')))";
            var refused = await engine.EvaluateAndLogAsync(q, 100, origin: "agent");
            Assert.Contains("does not permit", refused.Error);

            // The same query at human origin passes the gate (reaches the stub → connection error, never policy text).
            var human = await engine.EvaluateAndLogAsync(q, 100, origin: "human");
            Assert.DoesNotContain("does not permit", human.Error ?? "");
        }

        // ---- ...and under Ask, a granted QueryData session covers it without being consumed (the shared grant) ----
        [Fact]
        public async Task EvaluateAndLog_agent_runs_under_a_grant_without_consuming_it()
        {
            using var engine = Connected();   // default 'standard' preset; the unlabelled target resolves to prod ⇒ Ask

            var q = "EVALUATE ROW(\"v\", EVALUATEANDLOG([Total Sales]))";
            var r1 = await engine.EvaluateAndLogAsync(q, 100, origin: "agent");
            Assert.Contains("approval", r1.Error, StringComparison.OrdinalIgnoreCase);   // Ask ⇒ refused, request queued
            var pending = Assert.Single(ApprovalLedger.List());
            Assert.Equal(pending.Id, r1.ApprovalId);

            ApprovalLedger.Approve(pending.Id, "human");

            var r2 = await engine.EvaluateAndLogAsync(q, 100, origin: "agent");          // the live grant lets it through
            Assert.DoesNotContain("approval", r2.Error ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Single(ApprovalLedger.List());                                        // session grant, never consumed
        }

        // ---- pivot_measure returns SUMMARIZECOLUMNS rows BY CONSTRUCTION: every agent call is gated, humans never ----
        [Fact]
        public async Task PivotMeasure_agent_is_refused_on_a_deny_target_and_human_is_not()
        {
            using var engine = Connected();
            DenyQueryData();

            var refused = await engine.PivotMeasureAsync("[Total Sales]", new[] { "'Date'[Year]" }, null, null, 100, origin: "agent");
            Assert.Contains("does not permit", refused.Error);

            var human = await engine.PivotMeasureAsync("[Total Sales]", new[] { "'Date'[Year]" }, null, null, 100, origin: "human");
            Assert.DoesNotContain("does not permit", human.Error ?? "");
        }

        // ---- run_interview: an agent's row-returning ATTEMPT takes the run_dax gate (the entry door's origin
        //      rides every DAX the call executes) — the refusal folds into an honest Unverified, never a row read ----
        [Fact]
        public async Task Interview_agent_row_returning_attempt_is_gated()
        {
            using var engine = Connected();   // default 'standard' preset; the unlabelled target resolves to prod ⇒ Ask

            var inline = "{\"question\":\"What are the raw sales rows?\",\"tier\":\"value\"," +
                         "\"query\":\"EVALUATE ROW(\\\"v\\\", [Total Sales])\",\"expectedValue\":\"1\"}";
            var r = await engine.RunInterviewAsync(null, inline, abstained: false,
                attemptDax: "EVALUATE 'Sales'", origin: "agent");

            Assert.Equal("Unverified", r.Outcome);                                  // an ungraded refusal is not a pass
            Assert.Contains("approval", r.Detail, StringComparison.OrdinalIgnoreCase);   // the Ask reason rides the detail
            // P2: a policy refusal is not a query error — the detail must carry ONLY the approval recovery, never
            // the contradictory "fix the DAX attempt" advice (the DAX was fine; it was never executed).
            Assert.Contains("refused by the agent policy", r.Detail);
            Assert.DoesNotContain("fix the DAX attempt", r.Detail);
            Assert.Single(ApprovalLedger.List());                                   // ...and the request reached the queue
        }
    }

    /// <summary>
    /// Round-2 P1: saved-question replay must not launder agent origin. A saved question can itself be
    /// agent-authored (add_interview_question), so the origin the executed DAX is gated under is the ENTRY DOOR's,
    /// always — an MCP replay of a saved row-returning oracle is gated exactly like an inline one, while the
    /// engine-initiated replays (the deploy-gate advisory) and the human door run as before. Workspace-anchored
    /// project store in a temp dir; USERPROFILE redirected so the global-scope fallback never touches the real home.
    /// </summary>
    [Collection("restore-root")]   // mutates the static AgentPolicyStore/ApprovalLedger roots — serialize with the family
    public sealed class InterviewReplayOriginTests : IDisposable
    {
        private const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/WS";
        private readonly string _root;
        private readonly string _safeRoot;
        private readonly string _ws;
        private readonly string _origHome;

        public InterviewReplayOriginTests(RestoreRootFixture fixture)
        {
            _safeRoot = fixture.Root;
            _root = Path.Combine(Path.GetTempPath(), "sem-replay-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            AgentPolicyStore.RootOverride = _root;
            ApprovalLedger.RootOverride = _root;
            ConnectionRegistry.RootOverride = _root;
            _ws = Path.Combine(Path.GetTempPath(), "sem-replay-ws-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(_ws, ".semanticus"));
            _origHome = Environment.GetEnvironmentVariable("USERPROFILE");
            var home = Path.Combine(_ws, "home");
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("USERPROFILE", home);
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("USERPROFILE", _origHome);
            AgentPolicyStore.RootOverride = _safeRoot; ApprovalLedger.RootOverride = _safeRoot; ConnectionRegistry.RootOverride = _safeRoot;
            try { Directory.Delete(_root, true); } catch { }
            try { Directory.Delete(_ws, true); } catch { }
        }

        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // A workspace-anchored engine (project interview store in the temp ws) with the never-opened live stub
        // attached — the default 'standard' preset + unlabelled target resolve to prod ⇒ QueryData asks.
        private LocalEngine Connected()
        {
            var engine = new LocalEngine(new SessionManager(), new Fake(pro: true), _ws);
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", Endpoint));
            return engine;
        }

        // Save a question whose ORACLE is row-returning — authored by the AGENT (the laundering scenario).
        private static Task<InterviewQuestion> SaveRowOracle(LocalEngine e) =>
            e.AddInterviewQuestionAsync("Sales by year?", "value", "EVALUATE VALUES('Date'[Year])", null, null,
                null, null, null, "[[\"2024\",\"1\"]]", false, null, "user", "project", "agent");

        // ---- the laundering hole, closed: an agent-door replay of the SAVED row-returning oracle is gated ----
        [Fact]
        public async Task Agent_door_replay_of_a_saved_row_returning_oracle_is_gated()
        {
            using var engine = Connected();
            var q = await SaveRowOracle(engine);

            var r = await engine.RunInterviewAsync(q.Id, null, abstained: false, attemptDax: null, origin: "agent");

            Assert.Equal("Unverified", r.Outcome);                                   // honest: nothing was read
            Assert.Contains("refused by the agent policy", r.Detail);                // the P2 wording, not "fix the DAX"
            Assert.DoesNotContain("fix the DAX attempt", r.Detail);
            Assert.Single(ApprovalLedger.List());                                    // the Ask registered a request
        }

        // ---- the engine-initiated advisory replay is NOT the agent door: it still runs (no refusal, no request) ----
        [Fact]
        public async Task Internal_advisory_replay_of_the_same_pack_is_not_gated()
        {
            using var engine = Connected();
            await SaveRowOracle(engine);

            var adv = await engine.InterviewGateAdvisoryAsync();

            Assert.Equal(1, adv.Replayed);                                           // it ran (and failed on the stub)
            Assert.Empty(ApprovalLedger.List());                                     // no policy ask was ever raised
        }

        // ---- the human door replays as before: never gated ----
        [Fact]
        public async Task Human_door_replay_of_the_same_pack_is_not_gated()
        {
            using var engine = Connected();
            var q = await SaveRowOracle(engine);

            var r = await engine.RunInterviewAsync(q.Id, null, abstained: false, attemptDax: null, origin: "human");

            Assert.DoesNotContain("refused by the agent policy", r.Detail ?? "");
            Assert.Empty(ApprovalLedger.List());
        }

        // ---- a SCALAR saved oracle stays ungated even at the agent door (the classifier, not the origin, decides) ----
        [Fact]
        public async Task Agent_door_replay_of_a_saved_scalar_oracle_is_not_gated()
        {
            using var engine = Connected();
            var q = await engine.AddInterviewQuestionAsync("Total sales in 2024?", "value",
                "EVALUATE ROW(\"v\", CALCULATE([Total Sales], 'Date'[Year]=2024))", null, null,
                null, null, "1234567.89", null, false, null, "user", "project", "agent");

            var r = await engine.RunInterviewAsync(q.Id, null, abstained: false, attemptDax: null, origin: "agent");

            Assert.DoesNotContain("refused by the agent policy", r.Detail ?? "");    // ran (stub error), not refused
            Assert.Empty(ApprovalLedger.List());
        }
    }
}
