using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The expected_values verify kind — the enforcement half of the anchor-based verification design:
    ///   parser    — the anchors: binding rules (text-typed, same-or-prior step, wrong-kind refusal, required);
    ///   AnchorGate — strict+defensive anchor-JSON parsing (the context-key grammar is the INJECTION boundary;
    ///                unknown/duplicate properties refused; JSON value types preserved into exact DAX literals;
    ///                caps), the gold-style tolerance (ABS 1e-6 / REL 1e-9; BLANK matches only BLANK; non-finite
    ///                never matches; text expects are typed-exact), the injective canonical fingerprint, and the
    ///                run-level first-set lock + live revision receipts (the anti-laundering boundary);
    ///   DaxBench  — the measure-faithful DEFINE + CALCULATE point query;
    ///   executor  — the fail-closed 'unavailable' branches (offline / malformed / zero anchors / drift /
    ///                unresolvable context ref), live row-returning revision receipts + their audit hashes, the
    ///                evaluation ceiling + wedged-connection retirement, and the hard-gate block on failed/unavailable;
    ///                check_workflow's same-or-prior-step target check.
    /// (Match/mismatch verdicts against a live model need an XMLA endpoint — proven here at the pure AnchorGate
    /// layer, exactly as dax_equivalence proves its comparator via EquivalenceGate/VerifyEquivalenceCore.)
    /// </summary>
    public sealed class ExpectedValuesVerifyTests
    {
        private sealed class Pro : IEntitlement { public bool IsPro => true; public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" }; }

        private static AnswerValue Answer(string v) => new AnswerValue { Value = v };

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-ev-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            return ws;
        }
        private static void WriteUserWorkflow(string ws, string file, string md) =>
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", file), md);

        private static string AnswersJson(params (string k, string v)[] kv) =>
            System.Text.Json.JsonSerializer.Serialize(kv.ToDictionary(x => x.k, x => x.v));

        // ============================ parser — the anchors: binding rules ============================

        private static string EvMd(string verifyBlock, string type = "text") => @"---
name: anchors-flow
title: Anchors
---
## Step 1: Author
Author the candidate.
```yaml gate
inputs:
  - name: target
    question: ""The measure.""
    type: objectRef
    required: required
  - name: anchorSet
    question: ""The anchors.""
    type: " + type + @"
    required: required
```
## Step 2: Prove
Prove the anchors.
```yaml gate
verify:
" + verifyBlock + @"
```
";

        [Fact]
        public void Parser_accepts_expected_values_with_a_same_step_text_anchors_input()
        {
            // The anchors input and the verify sit on the SAME step.
            var def = WorkflowParser.Parse(@"---
name: same-step
title: Same step
---
## Step 1: Prove
Prove.
```yaml gate
inputs:
  - name: target
    question: ""The measure.""
    type: objectRef
    required: required
  - name: anchorSet
    question: ""The anchors.""
    type: text
    required: required
verify:
  - kind: expected_values
    anchors: anchorSet
```
");
            Assert.Null(def.Error);
            var v = def.Steps[0].Gate.Verify.Single();
            Assert.Equal("expected_values", v.Kind);
            Assert.Equal("anchorSet", v.Anchors);
        }

        [Fact]
        public void Parser_refuses_anchors_bound_to_a_non_text_input()
        {
            var def = WorkflowParser.Parse(EvMd("  - kind: expected_values\n    anchors: anchorSet", type: "objectRef"));
            Assert.NotNull(def.Error);
            Assert.Contains("anchors 'anchorSet' must bind a 'text' input", def.Error);
            Assert.Contains("objectRef", def.Error);
        }

        [Fact]
        public void Parser_refuses_anchors_that_names_no_input()
        {
            var def = WorkflowParser.Parse(EvMd("  - kind: expected_values\n    anchors: notAnInput"));
            Assert.NotNull(def.Error);
            Assert.Contains("anchors 'notAnInput' does not name an input", def.Error);
        }

        [Fact]
        public void Parser_refuses_anchors_on_a_non_expected_values_kind()
        {
            var def = WorkflowParser.Parse(EvMd("  - kind: dax_probe\n    anchors: anchorSet"));
            Assert.NotNull(def.Error);
            Assert.Contains("anchors applies only to an expected_values verify", def.Error);
            Assert.Contains("dax_probe", def.Error);
        }

        [Fact]
        public void Parser_refuses_expected_values_with_no_anchors_binding()
        {
            var def = WorkflowParser.Parse(EvMd("  - kind: expected_values"));
            Assert.NotNull(def.Error);
            Assert.Contains("an expected_values verify needs an 'anchors: <inputName>'", def.Error);
        }

        [Fact]
        public void Parser_accepts_anchors_bound_to_a_prior_step_input()
        {
            // The binding step (1) declares the text input; the gated verify sits on a LATER step (2) — inheritance.
            var def = WorkflowParser.Parse(EvMd("  - kind: expected_values\n    anchors: anchorSet"));
            Assert.Null(def.Error);
            Assert.Equal("anchorSet", def.Steps[1].Gate.Verify.Single().Anchors);
        }

        private const string RevisableAnchorsMd = @"---
name: revisable-anchors
title: Revisable anchors
strictness: hard
---
## Step 1: Lock expected values
Lock the test-first anchors.
```yaml gate
inputs:
  - name: target
    question: ""The authored measure.""
    type: objectRef
    required: required
  - name: expectedValues
    question: ""The initial locked anchors.""
    type: text
    required: required
```
## Step 2: Author and verify
Leave expectedValues unanswered to inherit, or answer it only with a receipted revision.
```yaml gate
inputs:
  - name: expectedValues
    question: ""OPTIONAL EXPECTATION REVISION WITH REQUIRED RECEIPT.""
    type: text
    required: optional
verify:
  - kind: expected_values
    anchors: expectedValues
```
";

        private static async Task<(LocalEngine Engine, string Workspace, string RunId)> SetupRevisableRunAsync(
            SessionManager sessions, Func<string, ResultSet> execute, double candidate = 2)
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "revisable-anchors.md", RevisableAnchorsMd);
            var engine = new LocalEngine(sessions, new Pro(), ws);
            await engine.CreateModelAsync("RevisionModel", 1604);
            await engine.CreateTableAsync("Sales", "human");
            sessions.Current.LiveOrigin = new LiveOrigin("endpoint-revision", "RevisionModel", null);
            engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-revision", "RevisionModel", execute));
            var target = await engine.CreateMeasureAsync("table:Sales", "Candidate", candidate.ToString(System.Globalization.CultureInfo.InvariantCulture), "human");
            var run = await engine.StartWorkflowAsync("revisable-anchors", "human");
            await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", AnswersJson(
                ("target", target), ("expectedValues", "[{\"context\":{},\"expect\":100}]")), "human");
            return (engine, ws, run.RunId);
        }

        private static ResultSet OneRow(double value) => new ResultSet
        {
            Columns = new[] { new ColumnDef { Name = "v", Type = "Double" } },
            Rows = new[] { new object[] { value } },
            RowCount = 1,
        };

        [Fact]
        public async Task Runner_rejects_a_bare_corrected_anchor_with_no_machine_receipt()
        {
            var sessions = new SessionManager();
            string ws = null!;
            try
            {
                var setup = await SetupRevisableRunAsync(sessions, _ => OneRow(200));
                ws = setup.Workspace;
                var bare = "[{\"context\":{},\"expect\":200}]";
                await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Engine.SubmitWorkflowStepAsync(
                    setup.RunId, "step-2", AnswersJson(("expectedValues", bare)), "human"));
                var after = await setup.Engine.GetWorkflowRunAsync(setup.RunId);
                var verify = after.Steps[1].VerifyResults.Single(v => v.Kind == "expected_values");
                Assert.Equal("unavailable", verify.Status);
                Assert.Contains("originalExpect, correctedExpect, extractQuery", verify.Detail);
                Assert.Contains("Bare corrected JSON is refused", verify.Detail);
                Assert.Null(after.AnchorRevisions);
            }
            finally { sessions.Dispose(); if (ws != null) Directory.Delete(ws, true); }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Runner_rejects_a_receipted_revision_when_extract_fails_or_returns_no_rows(bool fails)
        {
            var sessions = new SessionManager();
            string ws = null!;
            try
            {
                var setup = await SetupRevisableRunAsync(sessions, query => query == "EVALUATE 'Sales'"
                    ? (fails ? ResultSet.FromError("extract exploded") : new ResultSet { Rows = Array.Empty<object[]>(), RowCount = 0 })
                    : OneRow(200));
                ws = setup.Workspace;
                var revision = "[{\"context\":{},\"expect\":200,\"originalExpect\":100,\"correctedExpect\":200,\"extractQuery\":\"EVALUATE 'Sales'\"}]";
                await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Engine.SubmitWorkflowStepAsync(
                    setup.RunId, "step-2", AnswersJson(("expectedValues", revision)), "human"));
                var after = await setup.Engine.GetWorkflowRunAsync(setup.RunId);
                var verify = after.Steps[1].VerifyResults.Single(v => v.Kind == "expected_values");
                Assert.Equal("unavailable", verify.Status);
                Assert.Contains(fails ? "extractQuery failed" : "extractQuery returned no rows", verify.Detail);
                Assert.Null(after.AnchorRevisions);
            }
            finally { sessions.Dispose(); if (ws != null) Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Runner_accepts_a_live_receipted_revision_enforces_it_and_audits_the_delta()
        {
            var sessions = new SessionManager();
            string ws = null!;
            try
            {
                var receiptCalls = 0;
                var setup = await SetupRevisableRunAsync(sessions, query =>
                {
                    if (query == "EVALUATE 'Sales'") { receiptCalls++; return OneRow(200); }
                    return OneRow(200);
                });
                ws = setup.Workspace;
                var revision = "[{\"context\":{},\"expect\":200,\"originalExpect\":100,\"correctedExpect\":200,\"extractQuery\":\"EVALUATE 'Sales'\"}]";
                var after = await setup.Engine.SubmitWorkflowStepAsync(
                    setup.RunId, "step-2", AnswersJson(("expectedValues", revision)), "human");

                Assert.Equal("completed", after.Status);
                Assert.Equal(1, receiptCalls);
                Assert.Contains("expected 200, actual 200", after.Steps[1].VerifyResults.Single().Detail);
                var anchorLock = Assert.Single(after.AnchorLocks);
                Assert.Equal("expectedValues", anchorLock.AnchorsInput);
                Assert.NotEqual(anchorLock.InitialHash, anchorLock.CurrentHash);
                Assert.Equal("step-1", anchorLock.StepId);
                var audit = Assert.Single(after.AnchorRevisions);
                Assert.Equal("expectedValues", audit.AnchorsInput);
                Assert.NotEqual(audit.BeforeHash, audit.AfterHash);
                var change = Assert.Single(audit.Changes);
                Assert.Equal("100", change.OriginalExpect);
                Assert.Equal("200", change.CorrectedExpect);
                Assert.Equal("EVALUATE 'Sales'", change.ExtractQuery);
                Assert.Equal(1, change.ExtractRowCount);
                Assert.Equal(64, change.ExtractResultHash.Length);
            }
            finally { sessions.Dispose(); if (ws != null) Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Runner_unanswered_latest_optional_declaration_inherits_the_initial_anchor_without_a_receipt()
        {
            var sessions = new SessionManager();
            string ws = null!;
            try
            {
                var setup = await SetupRevisableRunAsync(sessions, _ => OneRow(100), candidate: 1);
                ws = setup.Workspace;
                var after = await setup.Engine.SubmitWorkflowStepAsync(setup.RunId, "step-2", "{}", "human");
                Assert.Equal("completed", after.Status);
                Assert.Contains("expected 100, actual 100", after.Steps[1].VerifyResults.Single().Detail);
                Assert.False(after.Steps[1].Answers.ContainsKey("expectedValues"));
                var anchorLock = Assert.Single(after.AnchorLocks);
                Assert.Equal(anchorLock.InitialHash, anchorLock.CurrentHash);
                Assert.Null(after.AnchorRevisions);
            }
            finally { sessions.Dispose(); if (ws != null) Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Runner_retry_with_omitted_revision_input_inherits_the_current_accepted_revision()
        {
            var sessions = new SessionManager();
            string ws = null!;
            try
            {
                var anchorActual = 999d;
                var receiptCalls = 0;
                var setup = await SetupRevisableRunAsync(sessions, query =>
                {
                    if (query == "EVALUATE 'Sales'") { receiptCalls++; return OneRow(200); }
                    return OneRow(anchorActual);
                });
                ws = setup.Workspace;
                var revision = "[{\"context\":{},\"expect\":200,\"originalExpect\":100,\"correctedExpect\":200,\"extractQuery\":\"EVALUATE 'Sales'\"}]";
                await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Engine.SubmitWorkflowStepAsync(
                    setup.RunId, "step-2", AnswersJson(("expectedValues", revision)), "human"));

                var blocked = await setup.Engine.GetWorkflowRunAsync(setup.RunId);
                Assert.Equal("failed", blocked.Steps[1].VerifyResults.Single().Status);
                Assert.Single(blocked.AnchorRevisions);

                anchorActual = 200;
                var completed = await setup.Engine.SubmitWorkflowStepAsync(setup.RunId, "step-2", "{}", "human");
                Assert.Equal("completed", completed.Status);
                Assert.Contains("expected 200, actual 200", completed.Steps[1].VerifyResults.Single().Detail);
                Assert.Equal(1, receiptCalls);
                Assert.Single(completed.AnchorRevisions);
            }
            finally { sessions.Dispose(); if (ws != null) Directory.Delete(ws, true); }
        }

        // ============================ AnchorGate.Parse — strict + defensive anchor JSON ============================

        [Fact]
        public void Parse_reads_typed_anchors_preserving_json_value_kinds()
        {
            // B2: a JSON number stays a numeric literal; a JSON string stays a STRING filter even when it looks
            // numeric ("2023"); booleans become TRUE()/FALSE().
            var anchors = AnchorGate.Parse(
                "[{\"context\": {\"'Date'[Year]\": 2023, \"'Date'[Day of Week]\": \"Monday\"}, \"expect\": 123456.78}, "
                + "{\"context\": {}, \"expect\": \"BLANK\"}, {\"expect\": \"Bikes\"}]",
                out var err);
            Assert.Null(err);
            Assert.Equal(3, anchors.Length);

            Assert.Equal(2, anchors[0].Context.Length);
            Assert.Equal(123456.78, anchors[0].Number);
            Assert.Equal("Date", anchors[0].Context[0].Table);
            Assert.Equal("Year", anchors[0].Context[0].Column);
            Assert.Equal("2023", anchors[0].Context[0].Literal);            // JSON number ⇒ bare numeric literal
            Assert.Equal("\"Monday\"", anchors[0].Context[1].Literal);      // JSON string ⇒ quoted DAX string

            Assert.Empty(anchors[1].Context);          // {} = grand total
            Assert.True(anchors[1].Blank);

            Assert.Empty(anchors[2].Context);          // absent context = grand total
            Assert.Equal("Bikes", anchors[2].Text);
        }

        [Fact]
        public void Parse_keeps_a_numeric_looking_string_a_string_filter()
        {
            var anchors = AnchorGate.Parse("[{\"context\": {\"'Date'[Year]\": \"2023\"}, \"expect\": 1}]", out var err);
            Assert.Null(err);
            Assert.Equal("\"2023\"", anchors[0].Context[0].Literal);        // NOT bare 2023 — the JSON type is the contract
        }

        [Fact]
        public void Parse_makes_a_comma_grouped_string_a_string_never_invalid_dax()
        {
            var anchors = AnchorGate.Parse("[{\"context\": {\"'T'[C]\": \"1,234\"}, \"expect\": 1}]", out var err);
            Assert.Null(err);
            Assert.Equal("\"1,234\"", anchors[0].Context[0].Literal);
        }

        [Fact]
        public void Parse_compiles_booleans_to_dax_boolean_literals()
        {
            var anchors = AnchorGate.Parse("[{\"context\": {\"'T'[Flag]\": true, \"'T'[Off]\": false}, \"expect\": 1}]", out var err);
            Assert.Null(err);
            Assert.Equal("TRUE()", anchors[0].Context[0].Literal);
            Assert.Equal("FALSE()", anchors[0].Context[1].Literal);
        }

        [Fact]
        public void Parse_refuses_a_null_or_structured_context_value()
        {
            Assert.Null(AnchorGate.Parse("[{\"context\": {\"'T'[C]\": null}, \"expect\": 1}]", out var e1));
            Assert.Contains("must be a string, number, or boolean", e1);
            Assert.Null(AnchorGate.Parse("[{\"context\": {\"'T'[C]\": [1]}, \"expect\": 1}]", out var e2));
            Assert.Contains("must be a string, number, or boolean", e2);
        }

        [Fact]
        public void Parse_reads_a_fenced_json_block()
        {
            var raw = "Here are the anchors:\n```json\n[{\"context\": {}, \"expect\": 42}]\n```\n";
            var anchors = AnchorGate.Parse(raw, out var err);
            Assert.Null(err);
            Assert.Equal(42.0, Assert.Single(anchors).Number);
        }

        [Fact]
        public void Parse_reports_malformed_json_naming_the_defect_never_guesses()
        {
            var anchors = AnchorGate.Parse("[{\"context\": {}, \"expect\": ]", out var err);
            Assert.Null(anchors);
            Assert.Contains("did not parse", err);
        }

        [Fact]
        public void Parse_refuses_a_non_array_root()
        {
            var anchors = AnchorGate.Parse("{\"context\": {}, \"expect\": 1}", out var err);
            Assert.Null(anchors);
            Assert.Contains("must be an ARRAY", err);
        }

        [Fact]
        public void Parse_refuses_an_anchor_that_is_not_an_object()
        {
            var anchors = AnchorGate.Parse("[123]", out var err);
            Assert.Null(anchors);
            Assert.Contains("anchor #1 is not an object", err);
        }

        [Fact]
        public void Parse_refuses_a_missing_expect()
        {
            var anchors = AnchorGate.Parse("[{\"context\": {}}]", out var err);
            Assert.Null(anchors);
            Assert.Contains("missing 'expect'", err);
        }

        [Fact]
        public void Parse_refuses_a_non_scalar_expect()
        {
            var anchors = AnchorGate.Parse("[{\"context\": {}, \"expect\": {}}]", out var err);
            Assert.Null(anchors);
            Assert.Contains("'expect' must be a number", err);
        }

        [Fact]
        public void Parse_refuses_a_non_object_context()
        {
            var anchors = AnchorGate.Parse("[{\"context\": 5, \"expect\": 1}]", out var err);
            Assert.Null(anchors);
            Assert.Contains("'context' must be an object", err);
        }

        [Fact]
        public void Parse_returns_an_empty_set_for_an_empty_array()
        {
            var anchors = AnchorGate.Parse("[]", out var err);
            Assert.Null(err);
            Assert.Empty(anchors);
        }

        // ---- B1: strict property surface — a typo can never silently change what is enforced ----

        [Fact]
        public void Parse_refuses_an_unknown_anchor_property_the_contex_typo()
        {
            var anchors = AnchorGate.Parse("[{\"contex\": {\"'D'[Y]\": 2023}, \"expect\": 1}]", out var err);
            Assert.Null(anchors);                                          // NOT a silent grand-total anchor
            Assert.Contains("unknown property 'contex'", err);
        }

        [Fact]
        public void Parse_refuses_a_duplicated_anchor_property()
        {
            var anchors = AnchorGate.Parse("[{\"expect\": 1, \"expect\": 2}]", out var err);
            Assert.Null(anchors);
            Assert.Contains("more than once", err);
        }

        [Fact]
        public void Parse_refuses_a_duplicated_context_column_even_recased()
        {
            var anchors = AnchorGate.Parse("[{\"context\": {\"'D'[Y]\": 1, \"'d'[y]\": 2}, \"expect\": 1}]", out var err);
            Assert.Null(anchors);                                          // DAX names are case-insensitive — same column twice
            Assert.Contains("more than once", err);
        }

        // ---- B1: the context-key grammar is the injection boundary ----

        [Fact]
        public void Parse_refuses_an_injection_shaped_context_key()
        {
            // The exact spoof from review: a filter expression with a trailing line comment that would swallow
            // the generated equality if the key were ever emitted verbatim. Refused by GRAMMAR at parse.
            var anchors = AnchorGate.Parse(
                "[{\"context\": {\"FILTER(ALL('Date'),'Date'[Year]=2022) //\": 1}, \"expect\": 1}]", out var err);
            Assert.Null(anchors);
            Assert.Contains("is not a qualified column reference", err);
        }

        [Theory]
        [InlineData("'Date'[Year] = 2022")]          // trailing operator
        [InlineData("'Date'[Year] -- note")]         // trailing comment
        [InlineData("[Year]")]                        // no table qualifier
        [InlineData("'Date'")]                        // no column
        [InlineData("'Date'[Year] 'X'[Y]")]          // two refs
        [InlineData("1Tbl[Col]")]                     // bareword can't start with a digit
        [InlineData("'Unterminated[Col]")]            // unclosed quote
        [InlineData("'T'[Unterminated")]              // unclosed bracket
        public void Parse_refuses_non_pure_column_ref_keys(string key)
        {
            var anchors = AnchorGate.Parse(
                System.Text.Json.JsonSerializer.Serialize(new object[] { new Dictionary<string, object> { ["context"] = new Dictionary<string, object> { [key] = 1 }, ["expect"] = 1 } }),
                out var err);
            Assert.Null(anchors);
            Assert.Contains("is not a qualified column reference", err);
        }

        [Fact]
        public void Parse_accepts_escaped_and_bareword_refs_and_reemits_them_canonically()
        {
            var anchors = AnchorGate.Parse(
                "[{\"context\": {\"'O''Brien'[Weird ]] Col]\": 1, \"Date[Year]\": 2023}, \"expect\": 1}]", out var err);
            Assert.Null(err);
            var f0 = anchors[0].Context[0];
            Assert.Equal("O'Brien", f0.Table);                             // unescaped by the parser
            Assert.Equal("Weird ] Col", f0.Column);
            // Re-emitted CANONICALLY escaped — never the input text.
            Assert.Equal("'O''Brien'[Weird ]] Col] = 1", AnchorGate.CompileContextFilter(f0));
            Assert.Equal("'Date'[Year] = 2023", AnchorGate.CompileContextFilter(anchors[0].Context[1]));   // bareword table re-quoted
        }

        // ---- B4: caps ----

        [Fact]
        public void Parse_refuses_more_than_the_anchor_cap()
        {
            var json = "[" + string.Join(",", Enumerable.Repeat("{\"expect\": 1}", AnchorGate.MaxAnchors + 1)) + "]";
            var anchors = AnchorGate.Parse(json, out var err);
            Assert.Null(anchors);
            Assert.Contains($"cap of {AnchorGate.MaxAnchors} anchors", err);
        }

        [Fact]
        public void Parse_refuses_more_than_the_context_pair_cap()
        {
            var pairs = string.Join(",", Enumerable.Range(0, AnchorGate.MaxContextPairs + 1).Select(i => $"\"'T'[C{i}]\": 1"));
            var anchors = AnchorGate.Parse("[{\"context\": {" + pairs + "}, \"expect\": 1}]", out var err);
            Assert.Null(anchors);
            Assert.Contains($"cap of {AnchorGate.MaxContextPairs} context pairs", err);
        }

        // ============================ AnchorGate.Matches — the gold-style tolerance ============================

        private static AnchorGate.Anchor Num(double n) => new AnchorGate.Anchor { Number = n };

        [Fact]
        public void Matches_a_number_within_the_absolute_floor()
        {
            Assert.True(AnchorGate.Matches(Num(123456.78), 123456.780000005, out var label));
            Assert.Contains("123456.78", label);
        }

        [Fact]
        public void Rejects_a_number_past_the_tolerance()
        {
            Assert.False(AnchorGate.Matches(Num(100.0), 100.5, out _));
        }

        [Fact]
        public void Below_magnitude_1000_the_absolute_band_dominates_by_design()
        {
            // B5 policy pin: at magnitude ~100 the REL band (1e-9 ⇒ ~1e-7) is far tighter than ABS 1e-6, so the
            // ABS band decides — matching the gold scorer. 5e-7 off passes (rel alone would fail); 2e-6 fails.
            Assert.True(AnchorGate.Matches(Num(100.0), 100.0000005, out _));    // abs 5e-7 <= 1e-6, rel 5e-9 > 1e-9
            Assert.False(AnchorGate.Matches(Num(100.0), 100.000002, out _));    // abs 2e-6 > 1e-6, rel 2e-8 > 1e-9
        }

        [Fact]
        public void Matches_a_large_number_within_the_relative_band_but_not_beyond_it()
        {
            Assert.True(AnchorGate.Matches(Num(1e12), 1e12 + 500, out _));       // rel 5e-10 <= 1e-9
            Assert.False(AnchorGate.Matches(Num(1e12), 1e12 + 2000, out _));     // rel 2e-9 > 1e-9, abs > 1e-6
        }

        // ---- B5: non-finite never matches ----

        [Fact]
        public void Non_finite_values_never_match_any_expectation()
        {
            Assert.False(AnchorGate.Matches(Num(5.0), double.PositiveInfinity, out _));
            Assert.False(AnchorGate.Matches(Num(double.PositiveInfinity), double.PositiveInfinity, out _));   // ∞<=∞ bypass closed
            Assert.False(AnchorGate.Matches(Num(5.0), double.NaN, out _));
            Assert.False(AnchorGate.Matches(Num(double.NaN), double.NaN, out _));
        }

        [Fact]
        public void Parse_refuses_a_non_finite_expect()
        {
            // JSON has no Infinity literal, but an overflowing numeral (1e999) decodes to +inf — the finite guard
            // at parse refuses it; Matches independently rejects non-finite however an Anchor was constructed.
            var anchors = AnchorGate.Parse("[{\"expect\": 1e999}]", out var err);
            Assert.Null(anchors);
            Assert.NotNull(err);
        }

        [Fact]
        public void Blank_matches_only_a_blank_actual()
        {
            var blank = new AnchorGate.Anchor { Blank = true };
            Assert.True(AnchorGate.Matches(blank, null, out var l1));
            Assert.Equal("(blank)", l1);
            Assert.False(AnchorGate.Matches(blank, 0.0, out _));                 // BLANK is not zero
        }

        [Fact]
        public void A_concrete_number_never_matches_a_blank_actual()
        {
            Assert.False(AnchorGate.Matches(Num(5.0), null, out var label));
            Assert.Equal("(blank)", label);
        }

        // ---- SF6: text expects are typed-exact, never formatted-display ----

        [Fact]
        public void A_string_expectation_is_an_exact_text_comparison()
        {
            var bikes = new AnchorGate.Anchor { Text = "Bikes" };
            Assert.True(AnchorGate.Matches(bikes, "Bikes", out _));
            Assert.False(AnchorGate.Matches(bikes, "Cars", out _));
        }

        [Fact]
        public void A_numeric_actual_against_a_string_expect_is_a_type_mismatch_named_in_the_label()
        {
            var nine = new AnchorGate.Anchor { Text = "9" };
            Assert.False(AnchorGate.Matches(nine, 9.0, out var label));          // no formatted-display matching
            Assert.Contains("not text", label);
            Assert.Contains("number", label);
        }

        [Fact]
        public void A_string_actual_against_a_numeric_expect_is_a_type_mismatch_named_in_the_label()
        {
            Assert.False(AnchorGate.Matches(Num(9.0), "9", out var label));
            Assert.Contains("not a number", label);
        }

        // ============================ AnchorGate — canonical fingerprint (anti-laundering, injective) ============================

        [Fact]
        public void CanonicalHash_is_reorder_insensitive_but_value_sensitive()
        {
            var a = AnchorGate.Parse("[{\"context\": {\"'D'[Y]\": 2023}, \"expect\": 10}, {\"context\": {}, \"expect\": 20}]", out _);
            var reordered = AnchorGate.Parse("[{\"context\": {}, \"expect\": 20}, {\"context\": {\"'D'[Y]\": 2023}, \"expect\": 10}]", out _);
            var changed = AnchorGate.Parse("[{\"context\": {\"'D'[Y]\": 2023}, \"expect\": 11}, {\"context\": {}, \"expect\": 20}]", out _);

            Assert.Equal(AnchorGate.CanonicalHash(a), AnchorGate.CanonicalHash(reordered));   // reorder is not a change
            Assert.NotEqual(AnchorGate.CanonicalHash(a), AnchorGate.CanonicalHash(changed));  // an edited value IS
        }

        [Fact]
        public void CanonicalHash_is_case_insensitive_over_refs_like_dax()
        {
            var a = AnchorGate.Parse("[{\"context\": {\"'Date'[Year]\": 2023}, \"expect\": 10}]", out _);
            var recased = AnchorGate.Parse("[{\"context\": {\"'date'[YEAR]\": 2023}, \"expect\": 10}]", out _);
            Assert.Equal(AnchorGate.CanonicalHash(a), AnchorGate.CanonicalHash(recased));
        }

        [Fact]
        public void CanonicalHash_has_no_delimiter_collisions()
        {
            // B3: pre-fix, unescaped delimiters let a crafted VALUE collide with a two-key set (the review shape:
            // "x;'T'[B]=y" as ONE value vs the two-key set). The JSON canonical form is injective.
            var embedded = AnchorGate.Parse("[{\"context\": {\"'T'[A]\": \"x;'T'[B]=y\"}, \"expect\": 1}]", out var e1);
            var pair = AnchorGate.Parse("[{\"context\": {\"'T'[A]\": \"x\", \"'T'[B]\": \"y\"}, \"expect\": 1}]", out var e2);
            Assert.Null(e1); Assert.Null(e2);
            Assert.NotEqual(AnchorGate.CanonicalHash(embedded), AnchorGate.CanonicalHash(pair));
        }

        [Fact]
        public void CanonicalHash_distinguishes_a_string_filter_from_a_numeric_filter()
        {
            // B2×B3: "2023" (string) and 2023 (number) are DIFFERENT filters and must not collide.
            var str = AnchorGate.Parse("[{\"context\": {\"'D'[Y]\": \"2023\"}, \"expect\": 1}]", out _);
            var num = AnchorGate.Parse("[{\"context\": {\"'D'[Y]\": 2023}, \"expect\": 1}]", out _);
            Assert.NotEqual(AnchorGate.CanonicalHash(str), AnchorGate.CanonicalHash(num));
        }

        // ============================ AnchorGate — the anchor-set lock + revision receipt ============================

        private static WorkflowRunState LockRun()
        {
            var md = @"---
name: lock-anchors
title: Lock
---
## Step 1: Prove
Prove.
```yaml gate
inputs:
  - name: anchorSet
    question: ""Anchors?""
    type: text
    required: required
verify:
  - kind: expected_values
    anchors: anchorSet
```
";
            return new WorkflowRunStore().Start(WorkflowParser.Parse(md), null);
        }

        [Fact]
        public void RegisterAnchorSet_locks_first_set_and_receipts_every_change()
        {
            var run = LockRun();
            Assert.Null(AnchorGate.RegisterAnchorSet(run, "step-1", 0, "anchorSet", "hashA"));   // first = lock
            Assert.Null(AnchorGate.RegisterAnchorSet(run, "step-1", 0, "anchorSet", "hashA"));   // unchanged = fine
            var block = AnchorGate.RegisterAnchorSet(run, "step-1", 0, "anchorSet", "hashB");
            Assert.Contains("anchor set changed", block);
            var rev = Assert.Single(run.AnchorRevisions);
            Assert.Equal("hashA", rev.BeforeHash);
            Assert.Equal("hashB", rev.AfterHash);
            Assert.Equal("step-1", rev.StepId);
            Assert.NotNull(rev.TimestampUtc);
            Assert.Null(AnchorGate.RegisterAnchorSet(run, "step-1", 0, "anchorSet", "hashB"));   // re-locked — fresh eval OK
        }

        // ============================ DaxBench — the measure-faithful anchor query ============================

        [Fact]
        public void BuildMeasureContextQuery_defines_the_measure_and_applies_calculate_point_filters()
        {
            var spec = new DaxQuerySpec { HomeTable = "Sales", TargetMeasureName = "Total", ModelMeasureNames = new[] { "Total" } };
            var q = DaxBench.BuildMeasureContextQuery("SUM('Sales'[Amount])", new[] { "'Date'[Year] = 2023" }, spec, out var note);
            Assert.Null(note);                                       // trusted target identity → full fidelity
            Assert.Contains("DEFINE", q);
            Assert.Contains("MEASURE 'Sales'[Total]", q);            // shadows the deployed measure (measure-faithful)
            Assert.Contains("CALCULATE", q);
            Assert.Contains("'Date'[Year] = 2023", q);
        }

        [Fact]
        public void BuildMeasureContextQuery_grand_total_has_no_calculate_wrapper()
        {
            var spec = new DaxQuerySpec { HomeTable = "Sales", TargetMeasureName = "Total", ModelMeasureNames = new[] { "Total" } };
            var q = DaxBench.BuildMeasureContextQuery("SUM('Sales'[Amount])", Array.Empty<string>(), spec, out _);
            Assert.DoesNotContain("CALCULATE", q);
            Assert.Contains("ROW", q);
        }

        [Fact]
        public void BuildMeasureContextQuery_degrades_under_an_untrusted_spec()
        {
            var q = DaxBench.BuildMeasureContextQuery("SUM('Sales'[Amount])", new[] { "'Date'[Year] = 2023" }, new DaxQuerySpec { Trusted = false }, out var note);
            Assert.NotNull(note);                                    // the fidelity caveat the executor turns into 'unavailable'
            Assert.NotNull(q);
        }

        // ============================ executor — the fail-closed branches (offline engine) ============================

        private const string EngineMd = @"---
name: anchors-run
title: Anchors run
strictness: hard
---
## Step 1: Author the candidate
Create the candidate measure.
```yaml gate
ops: [create_measure]
inputs:
  - name: target
    question: ""The created measure.""
    type: objectRef
    required: required
  - name: anchorSet
    question: ""The locked anchors.""
    type: text
    required: required
```
## Step 2: Prove the anchors
Prove the measure reproduces the anchors.
```yaml gate
verify:
  - kind: expected_values
    anchors: anchorSet
```
";

        private static async Task<(LocalEngine e, string runId, string mref)> SetupRunAsync(SessionManager sessions, string ws, string anchorSet)
        {
            WriteUserWorkflow(ws, "anchors-run.md", EngineMd);
            var e = new LocalEngine(sessions, new Pro(), ws);
            await e.CreateModelAsync("Anchors", 1604);
            await e.CreateTableAsync("Sales", "human");
            var run = await e.StartWorkflowAsync("anchors-run", "human");
            var mref = await e.CreateMeasureAsync("table:Sales", "Candidate", "1", "human");
            await e.SubmitWorkflowStepAsync(run.RunId, "step-1", AnswersJson(("target", mref), ("anchorSet", anchorSet)), "human");
            return (e, run.RunId, mref);
        }

        [Fact]
        public async Task Offline_is_unavailable_and_blocks_the_hard_gate()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            try
            {
                var (e, runId, _) = await SetupRunAsync(sessions, ws, "[{\"context\": {}, \"expect\": 1}]");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(runId, "step-2", "{}", "human"));
                Assert.Contains("unavailable", ex.Message);
                var after = await e.GetWorkflowRunAsync(runId);
                var v = after.Steps[1].VerifyResults.Single(x => x.Kind == "expected_values");
                Assert.Equal("unavailable", v.Status);
                Assert.Contains("live connection", v.Detail);
                Assert.Equal("failed", after.Steps[1].Status);       // hard gate: the step stays blocked
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Malformed_anchors_are_unavailable_naming_the_defect()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            try
            {
                var (e, runId, _) = await SetupRunAsync(sessions, ws, "not json at all");
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(runId, "step-2", "{}", "human"));
                var after = await e.GetWorkflowRunAsync(runId);
                var v = after.Steps[1].VerifyResults.Single(x => x.Kind == "expected_values");
                Assert.Equal("unavailable", v.Status);
                Assert.Equal("a well-formed anchor set", v.Missing);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Zero_anchors_are_unavailable()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            try
            {
                var (e, runId, _) = await SetupRunAsync(sessions, ws, "[]");
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(runId, "step-2", "{}", "human"));
                var after = await e.GetWorkflowRunAsync(runId);
                var v = after.Steps[1].VerifyResults.Single(x => x.Kind == "expected_values");
                Assert.Equal("unavailable", v.Status);
                Assert.Equal("at least one anchor", v.Missing);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task An_unresolvable_context_column_is_unavailable_naming_the_ref()
        {
            // B1: the parsed table+column must EXIST on the model; unknown refs refuse before any query.
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            try
            {
                var (e, runId, _) = await SetupRunAsync(sessions, ws, "[{\"context\": {\"'Nope'[X]\": 1}, \"expect\": 1}]");
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(runId, "step-2", "{}", "human"));
                var after = await e.GetWorkflowRunAsync(runId);
                var v = after.Steps[1].VerifyResults.Single(x => x.Kind == "expected_values");
                Assert.Equal("unavailable", v.Status);
                Assert.Equal("a resolvable anchor context", v.Missing);
                Assert.Contains("'Nope'[X]", v.Detail);
                Assert.Contains("does not exist", v.Detail);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Candidate_changed_outside_the_workflow_is_detected_as_drift()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            try
            {
                var (e, runId, mref) = await SetupRunAsync(sessions, ws, "[{\"context\": {}, \"expect\": 1}]");
                // Change the measure OUTSIDE the workflow (step-2 does not declare update_measure) → drift, before
                // the offline reason (drift is a model read, checkable even without a live endpoint).
                await e.SetDaxAsync(mref, "2", "human");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(runId, "step-2", "{}", "human"));
                Assert.Contains("drift", ex.Message);
                var after = await e.GetWorkflowRunAsync(runId);
                var v = after.Steps[1].VerifyResults.Single(x => x.Kind == "expected_values");
                Assert.Equal("unavailable", v.Status);
                Assert.Equal("an unchanged candidate", v.Missing);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // ============================ B4 — the evaluation ceiling cancels and retires, never wedges ============================
        // Lane ownership rules: the ceiling clock starts at COMMAND start (lane-wait is charged to the total budget
        // only); retirement is legal ONLY when this verify's OWN command was started and ignored Cancel; the verdict
        // keys off ceiling expiry alone; the per-query token is min(remaining budget, ceiling).

        private static (LocalEngine e, string ws) EngineWithModel(SessionManager sessions, string name)
        {
            var ws = NewWorkspace();
            var e = new LocalEngine(sessions, new Pro(), ws);
            e.CreateModelAsync(name, 1604).GetAwaiter().GetResult();
            return (e, ws);
        }

        private static DateTime In(int seconds) => DateTime.UtcNow.AddSeconds(seconds);
        private static DateTime InMs(int ms) => DateTime.UtcNow.AddMilliseconds(ms);

        [Fact]
        public async Task Active_wedged_query_still_retires_the_connection()
        {
            // The ForTest execute path has no AdomdCommand to Cancel (it models the pathological driver that
            // ignores BOTH Cancel and CommandTimeout): our OWN command started and ignored cancellation, so the
            // wall-clock guard must hand back 'timed out' AND retire the wedged connection.
            var sessions = new SessionManager();
            var (e, ws) = EngineWithModel(sessions, "Ceiling");
            try
            {
                e.VerifyCeilingOverrideForTest = 1;
                e.VerifyGraceOverrideForTest = 1;
                var wedged = LiveConnection.ForTest("xmla", "endpoint-wedged", execute: q =>
                {
                    System.Threading.Thread.Sleep(TimeSpan.FromSeconds(8));   // far past ceiling+grace
                    return new ResultSet { RowCount = 0 };
                });
                e.SetLiveConnectionForTest(wedged);

                var (rs, timedOut, retired, cause) = await e.RunVerifyQueryAsync("EVALUATE ROW(\"v\", 1)", 10, wedged, In(60));
                Assert.True(timedOut);
                Assert.True(retired);                              // own command, started, ignored Cancel ⇒ legal
                Assert.Equal(LocalEngine.VerifyTimeoutCause.Ceiling, cause);
                Assert.Contains("evaluation ceiling", rs.Error);

                var status = await e.ConnectionStatusAsync();
                Assert.False(status.Connected);                    // the wedged connection was retired, lane unclogged
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Queued_verify_times_out_without_retiring_the_healthy_lane_owner()
        {
            // A verify QUEUED behind a healthy long query never owned a command — exhausting the budget in the
            // queue must time out WITHOUT retiring the connection the healthy owner is still using.
            var sessions = new SessionManager();
            var (e, ws) = EngineWithModel(sessions, "Queued");
            try
            {
                e.VerifyCeilingOverrideForTest = 1;
                e.VerifyGraceOverrideForTest = 1;
                var live = LiveConnection.ForTest("xmla", "endpoint-busy", execute: q =>
                {
                    System.Threading.Thread.Sleep(TimeSpan.FromSeconds(3));   // the healthy owner's long query
                    return new ResultSet { RowCount = 0 };
                });
                e.SetLiveConnectionForTest(live);

                var occupier = live.ExecuteAsync("EVALUATE Owner", 10, 30);   // healthy work holds the lane
                await Task.Delay(200);                                        // let it actually acquire the lane

                var (rs, timedOut, retired, cause) = await e.RunVerifyQueryAsync("EVALUATE ROW(\"v\", 1)", 10, live, In(1));
                Assert.True(timedOut);
                Assert.False(retired);                              // we never owned a command — retirement is illegal
                Assert.Equal(LocalEngine.VerifyTimeoutCause.LaneWait, cause);   // item 4: the cause is discriminated
                Assert.Contains("waiting for the query lane", rs.Error);
                Assert.True((await e.ConnectionStatusAsync()).Connected);   // the healthy owner keeps its connection

                await occupier;                                     // clean shutdown of the healthy op
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task A_success_landing_during_the_post_cancel_grace_is_still_timed_out_and_never_retires()
        {
            // The verdict keys off ceiling EXPIRY alone: a clean result produced past the ceiling (landing inside
            // the grace window) must stay 'timed out' — and since the command TERMINATED, no retirement.
            var sessions = new SessionManager();
            var (e, ws) = EngineWithModel(sessions, "Grace");
            try
            {
                e.VerifyCeilingOverrideForTest = 1;
                e.VerifyGraceOverrideForTest = 3;
                var live = LiveConnection.ForTest("xmla", "endpoint-grace", execute: q =>
                {
                    System.Threading.Thread.Sleep(TimeSpan.FromSeconds(2));   // past the 1s ceiling, inside the grace
                    return new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "v" } },
                        Rows = new[] { new object[] { 42.0 } },
                        RowCount = 1,                                          // a SUCCESS — no rs.Error to key off
                    };
                });
                e.SetLiveConnectionForTest(live);

                var (rs, timedOut, retired, cause) = await e.RunVerifyQueryAsync("EVALUATE ROW(\"v\", 1)", 10, live, In(60));
                Assert.True(timedOut);                              // ceiling expired ⇒ timed out, even on success
                Assert.Equal(LocalEngine.VerifyTimeoutCause.Ceiling, cause);
                Assert.Contains("evaluation ceiling", rs.Error);
                Assert.False(retired);                              // the command terminated — nothing to retire
                Assert.True((await e.ConnectionStatusAsync()).Connected);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task The_per_query_token_is_bounded_by_the_remaining_budget_not_the_full_ceiling()
        {
            // An anchor admitted with ~1s of budget left gets a ~1s token, not the 10s ceiling: total runtime
            // stays within budget + grace (+ slack), never budget + full-ceiling.
            var sessions = new SessionManager();
            var (e, ws) = EngineWithModel(sessions, "Budget");
            try
            {
                e.VerifyCeilingOverrideForTest = 10;                // the FULL ceiling a naive token would grant
                e.VerifyGraceOverrideForTest = 1;
                var live = LiveConnection.ForTest("xmla", "endpoint-budget", execute: q =>
                {
                    System.Threading.Thread.Sleep(TimeSpan.FromSeconds(20));  // ignores everything
                    return new ResultSet { RowCount = 0 };
                });
                e.SetLiveConnectionForTest(live);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (rs, timedOut, retired, _) = await e.RunVerifyQueryAsync("EVALUATE ROW(\"v\", 1)", 10, live, In(1));
                sw.Stop();
                Assert.True(timedOut);
                Assert.True(retired);                               // own command started + ignored Cancel
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6),   // ~1s token + 1s grace + slack — NOT 10+1
                    $"expected the 1s-budget token to bound the wait, took {sw.Elapsed.TotalSeconds:0.#}s");
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task A_fast_verify_query_passes_through_the_ceiling_untouched()
        {
            var sessions = new SessionManager();
            var (e, ws) = EngineWithModel(sessions, "Fast");
            try
            {
                var live = LiveConnection.ForTest("xmla", "endpoint-fast", execute: q => new ResultSet
                {
                    Columns = new[] { new ColumnDef { Name = "v" } },
                    Rows = new[] { new object[] { 42.0 } },
                    RowCount = 1,
                });
                e.SetLiveConnectionForTest(live);

                var (rs, timedOut, retired, cause) = await e.RunVerifyQueryAsync("EVALUATE ROW(\"v\", 1)", 10, live, In(60));
                Assert.False(timedOut);
                Assert.False(retired);
                Assert.Equal(LocalEngine.VerifyTimeoutCause.None, cause);
                Assert.Null(rs.Error);
                Assert.Equal(42.0, rs.Rows[0][0]);
                Assert.True((await e.ConnectionStatusAsync()).Connected);   // a healthy connection is never retired
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Connection_swap_mid_proof_is_unavailable_naming_mixed_evidence()
        {
            // Item 4 — the identity pin across the whole proof: the FIRST anchor's query answers cleanly, but its
            // execute swaps the session's connection out from under the proof; the SECOND anchor must refuse as
            // 'unavailable' (mixed evidence) instead of continuing on a different connection.
            var sessions = new SessionManager();
            var (e, ws) = EngineWithModel(sessions, "Swap");
            try
            {
                LiveConnection first = null;
                first = LiveConnection.ForTest("xmla", "endpoint-1", execute: q =>
                {
                    e.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-2"));   // the mid-proof swap
                    return new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "v" } },
                        Rows = new[] { new object[] { 42.0 } },
                        RowCount = 1,
                    };
                });
                e.SetLiveConnectionForTest(first);

                var anchors = AnchorGate.Parse("[{\"expect\": 42}, {\"expect\": 42}]", out var perr);
                Assert.Null(perr);
                var spec = new DaxQuerySpec { HomeTable = "Sales", TargetMeasureName = "M", ModelMeasureNames = new[] { "M" } };
                var v = await e.EvaluateAnchorsAsync("expected_values", anchors, "1", spec, first, "");

                Assert.Equal("unavailable", v.Status);
                Assert.Equal("a stable connection", v.Missing);
                // The POST-query check catches it: anchor 1's own query ran under the swap, so its result is untrusted.
                Assert.Contains("connection changed during evaluation", v.Detail);
                Assert.Contains("anchor 1 of 2", v.Detail);
                Assert.Contains("reconnect", v.Detail);            // the recovery is named explicitly
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Connection_swap_during_the_final_anchor_is_unavailable_never_passed()
        {
            // Item 3 — the LAST anchor's query completes with a MATCHING value, but the connection swapped while it
            // ran: the post-query identity check must refuse before the result is consumed — never 'passed'.
            var sessions = new SessionManager();
            var (e, ws) = EngineWithModel(sessions, "SwapFinal");
            try
            {
                LiveConnection first = null;
                first = LiveConnection.ForTest("xmla", "endpoint-1", execute: q =>
                {
                    e.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-2"));   // swap DURING the final anchor
                    return new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "v" } },
                        Rows = new[] { new object[] { 42.0 } },     // a value that WOULD match — must still be refused
                        RowCount = 1,
                    };
                });
                e.SetLiveConnectionForTest(first);

                var anchors = AnchorGate.Parse("[{\"expect\": 42}]", out var perr);
                Assert.Null(perr);
                var spec = new DaxQuerySpec { HomeTable = "Sales", TargetMeasureName = "M", ModelMeasureNames = new[] { "M" } };
                var v = await e.EvaluateAnchorsAsync("expected_values", anchors, "1", spec, first, "");

                Assert.Equal("unavailable", v.Status);              // NEVER "passed"
                Assert.Equal("a stable connection", v.Missing);
                Assert.Contains("anchor 1 of 1", v.Detail);
                Assert.Contains("untrusted", v.Detail);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Start_vs_abandon_race_exactly_one_side_wins()
        {
            // Item 1 — force the interleaving: the lane-wait timeout branch is HELD (via the race hook) until the
            // queued command has actually started, so its abandon CAS must LOSE and fall through to the normal
            // started-path wait. The command started with the budget already expired ⇒ its clean result is refused
            // as a CEILING timeout (cancellation coverage held) — never a lane-wait abandon, never a retirement,
            // and the healthy connection survives. Exactly one side won the handoff.
            var sessions = new SessionManager();
            var (e, ws) = EngineWithModel(sessions, "Race");
            try
            {
                e.VerifyCeilingOverrideForTest = 5;
                e.VerifyGraceOverrideForTest = 3;
                var verifyStarted = new System.Threading.ManualResetEventSlim(false);
                var live = LiveConnection.ForTest("xmla", "endpoint-race", execute: q =>
                {
                    // 3s hold ≫ the 1s budget: the lane-wait timeout branch ALWAYS fires while the lane is still
                    // held (a late Task.Delay under suite load cannot outlast the owner), so the branch is then
                    // deterministically HELD by the hook until the command starts — the forced interleaving.
                    if (q.Contains("Owner")) { System.Threading.Thread.Sleep(3000); return new ResultSet { RowCount = 0 }; }
                    verifyStarted.Set();                            // the queued command is now RUNNING
                    return new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "v" } },
                        Rows = new[] { new object[] { 42.0 } },
                        RowCount = 1,
                    };
                });
                e.SetLiveConnectionForTest(live);
                // Hold the abandon branch until the command has started — the deterministic interleaving.
                e.VerifyAbandonRaceHookForTest = () => verifyStarted.Wait(TimeSpan.FromSeconds(10));

                var occupier = live.ExecuteAsync("EVALUATE Owner", 10, 30);   // holds the lane past the budget
                await Task.Delay(200);

                var (rs, timedOut, retired, cause) = await e.RunVerifyQueryAsync("EVALUATE ROW(\"v\", 1)", 10, live, In(1));
                Assert.True(timedOut);                              // budget-expired start ⇒ zero token ⇒ refused
                Assert.Equal(LocalEngine.VerifyTimeoutCause.Ceiling, cause);   // NOT a lane-wait abandon — the start won
                Assert.False(retired);                              // the command terminated cleanly — nothing to retire
                Assert.True((await e.ConnectionStatusAsync()).Connected);

                await occupier;
            }
            finally
            {
                sessions.Dispose(); Directory.Delete(ws, true);
            }
        }

        [Fact]
        public async Task A_clean_result_finishing_after_a_sub_second_budget_is_refused()
        {
            // Item 2 (direction 1): the token is the EXACT remaining TimeSpan — a 0.6s budget cancels at 0.6s, and
            // a clean result produced past the total deadline is refused (the token fired).
            var sessions = new SessionManager();
            var (e, ws) = EngineWithModel(sessions, "SubSecond");
            try
            {
                e.VerifyCeilingOverrideForTest = 5;
                e.VerifyGraceOverrideForTest = 3;
                var live = LiveConnection.ForTest("xmla", "endpoint-subsec", execute: q =>
                {
                    System.Threading.Thread.Sleep(1200);            // finishes AFTER the 600ms deadline, inside grace
                    return new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "v" } },
                        Rows = new[] { new object[] { 42.0 } },
                        RowCount = 1,
                    };
                });
                e.SetLiveConnectionForTest(live);

                var (rs, timedOut, retired, cause) = await e.RunVerifyQueryAsync("EVALUATE ROW(\"v\", 1)", 10, live, InMs(600));
                Assert.True(timedOut);                              // the 0.6s token fired — the clean result is refused
                Assert.Equal(LocalEngine.VerifyTimeoutCause.Ceiling, cause);
                Assert.False(retired);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task A_delayed_phase2_continuation_does_not_shift_the_deadline()
        {
            // THE final-blocker pin: cancellation is armed INSIDE the lane lambda at command start (absolute
            // deadline), not in the awaiting continuation. Here the phase-2 continuation is HELD (via the hook)
            // until a clean POST-deadline result has completed — pre-fix, the delayed continuation would arm a
            // fresh full-duration timer, see no cancellation, and ACCEPT the late result. Post-fix the verdict
            // keys off the absolute command-start deadline: still refused as Ceiling, and no retirement (the
            // command terminated).
            var sessions = new SessionManager();
            var (e, ws) = EngineWithModel(sessions, "LateArm");
            try
            {
                e.VerifyCeilingOverrideForTest = 5;
                e.VerifyGraceOverrideForTest = 3;
                var live = LiveConnection.ForTest("xmla", "endpoint-latearm", execute: q =>
                {
                    System.Threading.Thread.Sleep(1200);            // clean result, but AFTER the 600ms deadline
                    return new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "v" } },
                        Rows = new[] { new object[] { 42.0 } },
                        RowCount = 1,
                    };
                });
                e.SetLiveConnectionForTest(live);
                // Hold the phase-2 continuation until well past the clean post-deadline completion (1.2s).
                e.VerifyPhase2DelayHookForTest = () => System.Threading.Thread.Sleep(2500);

                var (rs, timedOut, retired, cause) = await e.RunVerifyQueryAsync("EVALUATE ROW(\"v\", 1)", 10, live, InMs(600));
                Assert.True(timedOut);                              // the absolute deadline decides — not a re-armed timer
                Assert.Equal(LocalEngine.VerifyTimeoutCause.Ceiling, cause);
                Assert.False(retired);                              // the command terminated — nothing to retire
                Assert.True((await e.ConnectionStatusAsync()).Connected);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task A_parked_lane_continuation_after_an_on_time_completion_is_still_accepted()
        {
            // THE mirror-image pin: the completion timestamp is captured SYNCHRONOUSLY on the execution-producing
            // thread — here the LANE continuation (after the stamped execute returned) is parked ACROSS the
            // deadline following an ON-TIME completion. A resume-time timestamp would read past the deadline and
            // FALSELY refuse the on-time result; the synchronous stamp keeps it accepted, with no retirement.
            var sessions = new SessionManager();
            var (e, ws) = EngineWithModel(sessions, "LaneParked");
            try
            {
                e.VerifyCeilingOverrideForTest = 5;
                e.VerifyGraceOverrideForTest = 3;
                var live = LiveConnection.ForTest("xmla", "endpoint-laneparked", execute: q =>
                {
                    System.Threading.Thread.Sleep(300);             // finishes WELL BEFORE the 1s deadline
                    return new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "v" } },
                        Rows = new[] { new object[] { 42.0 } },
                        RowCount = 1,
                    };
                });
                e.SetLiveConnectionForTest(live);
                // Park the lane continuation until well past the deadline (and past the armed cancel timer).
                e.VerifyLaneContinuationHookForTest = () => System.Threading.Thread.Sleep(2000);

                var (rs, timedOut, retired, cause) = await e.RunVerifyQueryAsync("EVALUATE ROW(\"v\", 1)", 10, live, InMs(1000));
                Assert.False(timedOut);                             // the TRUE finish time (0.3s) decides — on time
                Assert.False(retired);
                Assert.Equal(LocalEngine.VerifyTimeoutCause.None, cause);
                Assert.Null(rs.Error);
                Assert.Equal(42.0, rs.Rows[0][0]);                  // the on-time value is delivered, not refused
                Assert.True((await e.ConnectionStatusAsync()).Connected);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task A_1_9s_budget_is_not_falsely_truncated_to_1s()
        {
            // Item 2 (direction 2): only the ADOMD CommandTimeout property rounds (up) — the cancellation token
            // gets the exact 1.9s, so a 1.4s query under a 1.9s budget SUCCEEDS (an integer floor to 1s would
            // have falsely refused it).
            var sessions = new SessionManager();
            var (e, ws) = EngineWithModel(sessions, "Exact");
            try
            {
                e.VerifyCeilingOverrideForTest = 5;
                e.VerifyGraceOverrideForTest = 3;
                var live = LiveConnection.ForTest("xmla", "endpoint-exact", execute: q =>
                {
                    System.Threading.Thread.Sleep(1400);            // > 1s (the false truncation), < 1.9s (the budget)
                    return new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "v" } },
                        Rows = new[] { new object[] { 42.0 } },
                        RowCount = 1,
                    };
                });
                e.SetLiveConnectionForTest(live);

                var (rs, timedOut, retired, cause) = await e.RunVerifyQueryAsync("EVALUATE ROW(\"v\", 1)", 10, live, InMs(1900));
                Assert.False(timedOut);                             // the exact 1.9s token never fired
                Assert.False(retired);
                Assert.Equal(LocalEngine.VerifyTimeoutCause.None, cause);
                Assert.Equal(42.0, rs.Rows[0][0]);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Lane_wait_exhaustion_message_never_blames_the_anchor()
        {
            // Item 4 — the consumer emits the cause-correct message: a lane-wait budget exhaustion talks about the
            // busy connection, never advises narrowing the anchor (nothing of ours ever ran).
            var sessions = new SessionManager();
            var (e, ws) = EngineWithModel(sessions, "LaneMsg");
            try
            {
                e.VerifyBudgetOverrideForTest = 1;                  // a 1s total budget for the whole proof
                e.VerifyCeilingOverrideForTest = 5;
                e.VerifyGraceOverrideForTest = 1;
                var live = LiveConnection.ForTest("xmla", "endpoint-lane", execute: q =>
                {
                    System.Threading.Thread.Sleep(3000);            // the healthy owner outlasts the verify budget
                    return new ResultSet { RowCount = 0 };
                });
                e.SetLiveConnectionForTest(live);

                var occupier = live.ExecuteAsync("EVALUATE Owner", 10, 30);
                await Task.Delay(200);

                var anchors = AnchorGate.Parse("[{\"expect\": 42}]", out var perr);
                Assert.Null(perr);
                var spec = new DaxQuerySpec { HomeTable = "Sales", TargetMeasureName = "M", ModelMeasureNames = new[] { "M" } };
                var v = await e.EvaluateAnchorsAsync("expected_values", anchors, "1", spec, live, "");

                Assert.Equal("unavailable", v.Status);
                Assert.Contains("waiting for the query lane", v.Detail);
                Assert.DoesNotContain("narrow the anchor", v.Detail);   // the lane message never blames the anchor
                Assert.True((await e.ConnectionStatusAsync()).Connected);

                await occupier;
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // ============================ SF7 — check_workflow: target availability is same-or-prior-step ============================

        [Fact]
        public async Task Check_workflow_warns_when_the_objectRef_is_declared_only_on_a_later_step()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "late-target.md", @"---
name: late-target
title: Late target
---
## Step 1: Prove
Prove.
```yaml gate
inputs:
  - name: anchorSet
    question: ""Anchors?""
    type: text
    required: required
verify:
  - kind: expected_values
    anchors: anchorSet
```
## Step 2: Collect the target too late
Collect.
```yaml gate
inputs:
  - name: target
    question: ""The measure.""
    type: objectRef
    required: required
```
");
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                var report = await e.CheckWorkflowAsync("late-target");
                Assert.Null(report.ParseError);
                var warn = report.Findings.Single(f => f.Message.Contains("expected_values") && f.Message.Contains("target"));
                Assert.Equal("warn", warn.Severity);
                Assert.Contains("prior step", warn.Message);       // the run-wide objectRef does NOT bless it
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Check_workflow_accepts_a_prior_step_objectRef_for_expected_values()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "anchors-run.md", EngineMd);     // target on step 1, verify on step 2
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                var report = await e.CheckWorkflowAsync("anchors-run");
                Assert.Null(report.ParseError);
                Assert.DoesNotContain(report.Findings, f => f.Message.Contains("expected_values") && f.Message.Contains("needs a target"));
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // ============================ runner — hard gate blocks on a failed anchor proof ============================

        [Fact]
        public async Task A_failed_anchor_proof_blocks_a_hard_step()
        {
            var md = @"---
name: ev-hard
title: EV hard
strictness: hard
---
## Step 1: Prove
Prove.
```yaml gate
inputs:
  - name: anchorSet
    question: ""Anchors?""
    type: text
    required: required
verify:
  - kind: expected_values
    anchors: anchorSet
```
";
            var run = new WorkflowRunStore().Start(WorkflowParser.Parse(md), null);
            WorkflowVerifyExecutor failed = (sp, st, r, a) => Task.FromResult(new VerifyResult
            {
                Kind = sp.Kind, Status = "failed",
                Detail = "2 of 3 anchor(s) diverged: [(grand total)] expected 100 but got 90",
            });

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(
                run, "step-1", new Dictionary<string, AnswerValue> { ["anchorSet"] = Answer("[{\"context\":{},\"expect\":100}]") }, failed));
            Assert.Contains("expected_values", ex.Message);
            Assert.Contains("diverged", ex.Message);
            Assert.Equal("failed", run.Results[0].Status);
            Assert.Equal("failed", run.Results[0].VerifyResults.Single().Status);
        }
    }
}
