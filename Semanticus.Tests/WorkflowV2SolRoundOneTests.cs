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
    /// Sol round-1 findings against format v2 slice 1 (branch head 12abf34d). One test per finding, written
    /// from Sol's own concrete input and watched fail before anything was changed, so each test both proves
    /// the claim and satisfies golden rule 5. A finding whose reproduction did NOT fail is marked REFUTED in
    /// the report with the observed output, and nothing was changed for it.
    ///
    /// Findings 1 and 3 are the same family as the `scope:` bug caught during slice 1: a v2 rule leaking
    /// into the v1 path. That family is closed here rather than instance by instance.
    /// </summary>
    public sealed class WorkflowV2SolRoundOneTests
    {
        private sealed class Free : IEntitlement { public bool IsPro => false; public EntitlementInfo Info => new EntitlementInfo { Tier = "free" }; }
        private sealed class Pro : IEntitlement { public bool IsPro => true; public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" }; }

        private static (LocalEngine e, string ws) Make(IEntitlement ent = null)
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-sol1-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            return (new LocalEngine(new SessionManager(), ent ?? new Free(), ws), ws);
        }

        private static string Md(params string[] lines) => string.Join("\n", lines);

        // ---- finding 1: a v1 file with an inline `provenance:` scalar --------------------------------

        [Fact]
        public void F1_a_v1_file_may_carry_an_inline_provenance_scalar()
        {
            // Sol's input verbatim. Before v2 this stored Provenance["provenance"] = "legacy"; the v2
            // block-form handling made it throw in v1 too. The golden missed it because no shipped file
            // happens to use the key.
            var def = WorkflowParser.Parse(Md("---", "name: t", "provenance: legacy", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "v1 refused an inline provenance scalar: " + def.Error);
            Assert.Equal("legacy", def.Provenance["provenance"]);
        }

        [Fact]
        public void F1_family_no_v2_only_frontmatter_rule_fires_in_v1()
        {
            // The FAMILY, not the instance: every key v2 gives dedicated handling to in the FRONTMATTER must
            // still behave as an ordinary unknown key in v1, preserved into Provenance and never an error.
            //
            // Scoped down from a hand-written six-key list after Sol round 2. That list also named
            // `successCriteria`, `script` and `after`, which are reserved in the GATE and STEP scopes, and
            // testing them here proved nothing: in the frontmatter they are unknown keys that v2 does not
            // reserve either, so the assertion held for the wrong reason. It also read as coverage of the
            // step scope while omitting five of its keys. The whole reservation set is now enumerated from
            // the shared constant, in the scope each key is actually reserved in, and with the observation
            // that scope actually offers (a gate or step key never reaches Provenance, which only the
            // frontmatter writes to) — WorkflowV2SolRoundTwoTests.R3_*.
            foreach (var key in new[] { "provenance", "variables", "agents" })
            {
                var def = WorkflowParser.Parse(Md("---", "name: t", key + ": legacy", "---", "## Step 1: Do", "Body."));
                Assert.True(def.Error == null, $"v1 refused frontmatter key '{key}': {def.Error}");
                Assert.Equal("legacy", def.Provenance[key]);
            }
        }

        // ---- finding 2: a second schemaVersion is ignored --------------------------------------------

        [Fact]
        public void F2_a_repeated_schema_version_is_refused()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "schemaVersion: 3", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error != null, "a file declaring two different schemaVersions parsed as v2");
            Assert.Contains("schemaVersion", def.Error);
        }

        // ---- finding 3: an inline value on `inputs:` / `verify:` is discarded ------------------------

        [Theory]
        [InlineData("inputs")]
        [InlineData("verify")]
        public void F3_an_inline_value_on_a_gate_list_key_is_refused_in_v2(string key)
        {
            // `verify: bpa_clean` parses to ZERO verifies today, so the gate the author wrote silently is
            // not enforced at all. Losing a check without saying so is the worst shape a parser bug can take.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "---", "## Step 1: Do", "Body.",
                "```yaml gate", "strictness: hard", key + ": bpa_clean", "```"));
            Assert.True(def.Error != null, $"'{key}: bpa_clean' was accepted and the value dropped");
            Assert.Contains(key, def.Error);
        }

        [Fact]
        public void F3_v1_still_accepts_an_inline_value_on_a_gate_list_key_and_still_drops_it()
        {
            // v1-forever: the file must parse exactly as it does today, dropped value and all.
            //
            // NOT covered here, and named so it is not mistaken for covered: v1 gets no lint warn about the
            // dropped value, unlike the v1 `yaml step` fence which does. The fence survives in
            // Instructions so lint can see it; this value is discarded by the parser before any def exists,
            // so warning would mean carrying it on WorkflowDef purely to complain about it. Deferred rather
            // than bodged, and recorded in the report.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "---", "## Step 1: Do", "Body.",
                "```yaml gate", "strictness: hard", "verify: bpa_clean", "```"));
            Assert.Null(def.Error);
            Assert.Empty(def.Steps[0].Gate?.Verify ?? Array.Empty<VerifySpec>());
        }

        // ---- finding 4: trailing text after a closing quote is discarded -----------------------------

        [Fact]
        public void F4_trailing_text_after_a_quoted_scalar_is_refused_in_v2()
        {
            // Sol's case is the dangerous one: the discarded tail carries a RESERVED key, so the reservation
            // is bypassed by quoting the value before it.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "---", "## Step 1: Do",
                "```yaml step", "id: \"do-it\" instructions: x", "```", "", "Body."));
            Assert.True(def.Error != null, "trailing text after a quoted scalar was silently discarded");
        }

        [Fact]
        public void F4_a_plain_quoted_scalar_is_still_fine()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: \"A title: with a colon\"", "---",
                "## Step 1: Do", "", "Body."));
            Assert.Null(def.Error);
            Assert.Equal("A title: with a colon", def.Title);
        }

        // ---- finding 5: v2 slot maps are silently open -----------------------------------------------

        [Fact]
        public void F5_a_v2_template_refuses_an_unknown_slot_key()
        {
            // Section 1.3 states `with:` is THE one open map. That claim is false while a slot map swallows
            // anything, and a misspelled slot key means the fill-in silently never appears.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "kind: template", "title: T",
                "slots:",
                "  - name: customer",
                "    quesion: Which customer?",
                "    example: Acme",
                "---", "## Step 1: Do", "Body about {{customer}}."));
            Assert.True(def.Error != null, "a misspelled slot key was swallowed in v2");
            Assert.Contains("quesion", def.Error);
        }

        [Fact]
        public void F5_v1_slot_maps_stay_open()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "kind: template", "title: T",
                "slots:",
                "  - name: customer",
                "    quesion: Which customer?",
                "    example: Acme",
                "---", "## Step 1: Do", "Body about {{customer}}."));
            Assert.Null(def.Error);
        }

        // ---- finding 6: a step `when:` naming an input no gate collects ------------------------------

        [Fact]
        public async Task F6_a_when_naming_an_undeclared_input_is_a_check_workflow_warn()
        {
            // The term is SHAPE-readable, so the unreadable-fact warn never fires. At run time the fact is
            // unknown, every comparison is false, and the step silently does not run. The verify-level
            // equivalent has been warned about for a long time (LocalEngine.Workflows.cs:910); the
            // step-level one was not.
            var (e, _) = Make();
            await e.SaveWorkflowAsync("typo-when", Md(
                "---", "schemaVersion: 2", "name: typo-when", "title: T", "---",
                "## Step 1: Ask", "Body.",
                "```yaml gate", "inputs:", "  - name: approval", "    question: Approved?", "```",
                "## Step 2: Act",
                "```yaml step", "id: act", "when: inputs.aproval.answered", "```", "", "Body."), "human");
            var r = await e.CheckWorkflowAsync("typo-when");
            var warn = r.Findings.SingleOrDefault(f => f.Severity == "warn" && f.Message.Contains("aproval"));
            Assert.True(warn != null, "a misspelled input in a step when: passed lint; findings: "
                + string.Join(" | ", r.Findings.Select(f => f.Severity + ": " + f.Message)));
            Assert.Contains("act", warn!.Message);
            Assert.False(r.Ok);
        }

        [Fact]
        public async Task F6_a_when_naming_a_declared_input_is_not_warned()
        {
            var (e, _) = Make();
            await e.SaveWorkflowAsync("good-when", Md(
                "---", "schemaVersion: 2", "name: good-when", "title: T", "---",
                "## Step 1: Ask", "Body.",
                "```yaml gate", "inputs:", "  - name: approval", "    question: Approved?", "```",
                "## Step 2: Act",
                "```yaml step", "id: act", "when: inputs.approval.answered", "```", "", "Body."), "human");
            var r = await e.CheckWorkflowAsync("good-when");
            Assert.DoesNotContain(r.Findings, f => f.Severity == "warn" && f.Message.Contains("approval"));
        }

        // ---- finding 7: the new roots leak into activation rules -------------------------------------

        [Fact]
        public async Task F7_an_activation_rule_may_not_use_a_step_scope_fact()
        {
            // The blast radius of making the bare-bool grammar work: inputs.* and loop.* are STEP facts, and
            // an activation rule has no step and no answer frame. Accepting one writes a rule that can never
            // match, with nothing to explain why. Refused at the activation write path rather than in the
            // shared classifier, so the one-evaluator invariant survives.
            var (e, _) = Make(new Pro());
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                e.SetWorkflowActivationAsync("new-measure", "inputs.approval.answered", "on", "human"));
            Assert.Contains("inputs.approval.answered", ex.Message);
        }

        [Fact]
        public async Task F7_an_ordinary_activation_condition_still_works()
        {
            // the control: the refusal must be about the step-scope roots, not about conditions in general
            var (e, _) = Make(new Pro());
            var ex = await Record.ExceptionAsync(() =>
                e.SetWorkflowActivationAsync("new-measure", "connection.kind == 'local'", "on", "human"));
            Assert.True(ex == null, "an ordinary activation condition was refused: " + ex?.Message);
        }

        // ---- finding 8: hyphens lex in the bare form but not in a comparison -------------------------

        [Fact]
        public void F8_a_hyphenated_input_name_lexes_in_both_forms()
        {
            // A gate input name may carry a hyphen (WhenExpr allows [A-Za-z0-9_-]+), so a grammar that reads
            // the bare form but not the compared form is inconsistent on names the format itself permits.
            WorkflowPredicate.Parse("inputs.approval-state.answered", out var bareErr);
            Assert.Null(bareErr);

            var expr = WorkflowPredicate.Parse("inputs.approval-state.value == 'yes'", out var cmpErr);
            Assert.True(expr != null, "the compared form did not lex: " + cmpErr);
            Assert.Null(cmpErr);
        }

        [Fact]
        public void F8_a_negative_number_literal_still_parses()
        {
            // the control for widening the ref charset with '-': the literal side must not be eaten
            var expr = WorkflowPredicate.Parse("date.monthEndOffset >= -3", out var err);
            Assert.Null(err);
            Assert.NotNull(expr);
            Assert.Equal("-3", expr!.OrGroups[0][0].Literal);
            Assert.Equal("date.monthEndOffset", expr.OrGroups[0][0].Left);
        }

        // ---- finding 9: an unclosed inline list --------------------------------------------------------

        [Fact]
        public void F9_an_unclosed_inline_loop_list_is_refused()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "---", "## Step 1: Do",
                "```yaml step", "id: each", "forEach:", "  in: [Sales, Product", "  as: table", "```", "", "Body."));
            Assert.True(def.Error != null, "an unclosed inline list was parsed as items '[Sales' and 'Product'");
            Assert.Contains("line", def.Error);   // [T217]: the library's refusal names the line, not the text
        }

        [Fact]
        public void F9_an_unclosed_returns_list_is_refused()
        {
            // same bracket bug, second v2 site; fixing only the one Sol happened to name would leave the pair
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "---", "## Step 1: Do",
                "```yaml step", "id: h", "call:", "  workflow: other", "  returns: [certificate", "```", "", "Body."));
            Assert.True(def.Error != null, "an unclosed returns list was accepted");
        }

        // ---- finding 10: a with: VALUE is never resolved -----------------------------------------------

        [Fact]
        public async Task F10_a_with_value_naming_an_undeclared_caller_input_is_a_warn()
        {
            // §1.7: a with: value is a literal, a [[loop.*]] reference, or an inputs.<name> reference IN THE
            // CALLER. Only the key was checked, so the caller side of the mapping went unvalidated.
            var (e, _) = Make();
            await e.SaveWorkflowAsync("callee-x", Md(
                "---", "schemaVersion: 2", "name: callee-x", "title: T", "---",
                "## Step 1: Work", "Body.",
                "```yaml gate", "inputs:", "  - name: target", "    question: Which one?", "```"), "human");
            await e.SaveWorkflowAsync("caller-x", Md(
                "---", "schemaVersion: 2", "name: caller-x", "title: T", "---",
                "## Step 1: Hand off",
                "```yaml step", "id: h", "call:", "  workflow: callee-x",
                "  with:", "    target: inputs.targte", "```", "", "Body."), "human");
            var r = await e.CheckWorkflowAsync("caller-x");
            var warn = r.Findings.SingleOrDefault(f => f.Severity == "warn" && f.Message.Contains("targte"));
            Assert.True(warn != null, "an unresolvable with: value passed lint; findings: "
                + string.Join(" | ", r.Findings.Select(f => f.Severity + ": " + f.Message)));
        }

        [Fact]
        public async Task F10_a_literal_with_value_is_not_warned()
        {
            // the control: only an inputs.<name> reference is resolved; a literal is a literal
            var (e, _) = Make();
            await e.SaveWorkflowAsync("callee-y", Md(
                "---", "schemaVersion: 2", "name: callee-y", "title: T", "---",
                "## Step 1: Work", "Body.",
                "```yaml gate", "inputs:", "  - name: target", "    question: Which one?", "```"), "human");
            await e.SaveWorkflowAsync("caller-y", Md(
                "---", "schemaVersion: 2", "name: caller-y", "title: T", "---",
                "## Step 1: Hand off",
                "```yaml step", "id: h", "call:", "  workflow: callee-y",
                "  with:", "    target: Sales Amount", "```", "", "Body."), "human");
            var r = await e.CheckWorkflowAsync("caller-y");
            Assert.DoesNotContain(r.Findings, f => f.Severity == "warn" && f.Message.Contains("Sales Amount"));
        }
    }
}
