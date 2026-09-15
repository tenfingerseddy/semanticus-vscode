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
    /// Format v2, the three control keys and their static refusals (docs/workflow-canvas-spec.md §1.5-§1.7,
    /// §3.4). This slice PARSES, VALIDATES and STORES <c>when:</c>, <c>forEach:</c> and <c>call:</c>; nothing
    /// here executes a loop or a call, because <c>WorkflowRunner</c> is a later slice. The six new
    /// <c>check_workflow</c> checks of §3.4 land here, because lint is not execution.
    /// </summary>
    public sealed class WorkflowV2CheckTests
    {
        private sealed class Free : IEntitlement { public bool IsPro => false; public EntitlementInfo Info => new EntitlementInfo { Tier = "free" }; }

        private static (LocalEngine e, string ws) Make()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-wfv2-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            return (new LocalEngine(new SessionManager(), TestEntitlements.Pro, ws), ws);
        }

        private static string Md(params string[] lines) => string.Join("\n", lines);

        // ---- §1.5 `when:` parses and is stored --------------------------------------------------------

        [Fact]
        public void A_step_level_when_is_parsed_and_stored()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: One", "", "Body.", "",
                "## Step 2: Two", "",
                "```yaml step", "id: maybe", "when: inputs.optionalThing.answered", "```", "",
                "Body."));
            Assert.Null(def.Error);
            Assert.Equal("inputs.optionalThing.answered", def.Steps[1].When);
            Assert.Null(def.Steps[0].When);
        }

        [Fact]
        public void A_bare_boolean_fact_is_a_legal_condition()
        {
            // §1.5 claims the verify-level `when: inputs.X.answered` is "a strict subset of this grammar".
            // It is not, today: WorkflowPredicate.Comparison requires `fact op literal`, so a bare bool term
            // does not lex at all. Stage 1 makes the claim true by accepting a bare Bool fact as `== true`.
            var expr = WorkflowPredicate.Parse("inputs.witnessDax.answered", out var error);
            Assert.Null(error);
            Assert.NotNull(expr);
        }

        [Fact]
        public void The_two_new_fact_roots_are_readable()
        {
            Assert.Equal(PredicateFactType.Bool, WorkflowPredicate.Classify("inputs.witnessDax.answered"));
            Assert.Equal(PredicateFactType.Bool, WorkflowPredicate.Classify("inputs.witnessDax.declined"));
            Assert.Equal(PredicateFactType.Str, WorkflowPredicate.Classify("inputs.witnessDax.value"));
            Assert.Equal(PredicateFactType.Number, WorkflowPredicate.Classify("loop.index"));
            Assert.Equal(PredicateFactType.Str, WorkflowPredicate.Classify("loop.table"));
            // and an unreadable term stays Unknown, so the comparison is inert and lint can say so
            Assert.Equal(PredicateFactType.Unknown, WorkflowPredicate.Classify("inputs.witnessDax.answerd"));
        }

        [Fact]
        public async Task An_unreadable_when_term_is_a_check_workflow_warn_naming_the_term_and_the_step()
        {
            // §3.4 check 3 / acceptance check 11. This warn is load-bearing: at run time an unknown term
            // classifies Unknown, every comparison against it is false, and the step SILENTLY does not run.
            var (e, _) = Make();
            var md = Md(
                "---", "schemaVersion: 2", "name: bad-when", "title: T", "---", "",
                "## Step 1: One", "",
                "```yaml step", "id: maybe", "when: inputs.thing.answerd", "```", "",
                "Body.");
            await e.SaveWorkflowAsync("bad-when", md, "human");
            var r = await e.CheckWorkflowAsync("bad-when");
            var warn = r.Findings.SingleOrDefault(f => f.Severity == "warn" && f.Message.Contains("inputs.thing.answerd"));
            Assert.True(warn != null, "no warn named the unreadable term; findings: "
                + string.Join(" | ", r.Findings.Select(f => f.Severity + ": " + f.Message)));
            Assert.Contains("maybe", warn!.Message);
            Assert.False(r.Ok);
        }

        // ---- §1.6 `forEach:` --------------------------------------------------------------------------

        [Fact]
        public void A_for_each_is_parsed_and_stored_with_its_default_bound()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: One", "",
                "```yaml step", "id: each", "forEach:", "  in: [Sales, Product, Date]", "  as: table", "```", "",
                "Body."));
            Assert.Null(def.Error);
            var fe = def.Steps[0].ForEach;
            Assert.NotNull(fe);
            Assert.Equal(new[] { "Sales", "Product", "Date" }, fe!.InLiteral);
            Assert.Null(fe.InInput);
            Assert.Equal("table", fe.As);
            Assert.Equal(25, fe.MaxIterations);      // §1.6: default 25
        }

        [Fact]
        public void A_for_each_above_the_hard_ceiling_is_refused_naming_both_numbers()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: One", "",
                "```yaml step", "id: each", "forEach:", "  in: [a]", "  as: t", "  maxIterations: 101", "```", "",
                "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("101", def.Error);
            Assert.Contains("100", def.Error);
        }

        [Fact]
        public void A_loop_variable_may_not_collide_with_a_gate_input_name()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: One", "",
                "```yaml step", "id: each", "forEach:", "  in: [a]", "  as: note", "```", "",
                "Body.", "",
                "```yaml gate", "inputs:", "  - name: note", "    question: A note?", "```"));
            Assert.NotNull(def.Error);
            Assert.Contains("'note'", def.Error);
        }

        [Fact]
        public void A_for_each_source_input_collected_by_a_later_step_is_refused_naming_both_steps()
        {
            // §3.4 check 5. Loops expand when their step becomes current (§1.6.1), so a source collected by a
            // LATER step can never have a value in time. "Some step" would pass lint and fail at run time.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: Loop", "",
                "```yaml step", "id: each", "forEach:", "  in: inputs.tableList", "  as: table", "```", "",
                "Body.", "",
                "## Step 2: Collect", "", "Body.", "",
                "```yaml gate", "inputs:", "  - name: tableList", "    question: Which tables?", "```"));
            Assert.NotNull(def.Error);
            Assert.Contains("tableList", def.Error);
            Assert.Contains("each", def.Error);      // the loop step
            Assert.Contains("Step 2", def.Error);    // the step that must move
        }

        [Fact]
        public void A_for_each_source_collected_by_a_strictly_preceding_step_is_accepted()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: Collect", "", "Body.", "",
                "```yaml gate", "inputs:", "  - name: tableList", "    question: Which tables?", "```", "",
                "## Step 2: Loop", "",
                "```yaml step", "id: each", "forEach:", "  in: inputs.tableList", "  as: table", "```", "",
                "Body."));
            Assert.Null(def.Error);
            Assert.Equal("tableList", def.Steps[1].ForEach!.InInput);
        }

        [Fact]
        public void A_for_each_source_on_the_loop_steps_own_gate_is_accepted()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: Loop", "",
                "```yaml step", "id: each", "forEach:", "  in: inputs.tableList", "  as: table", "```", "",
                "Body.", "",
                "```yaml gate", "inputs:", "  - name: tableList", "    question: Which tables?", "```"));
            Assert.Null(def.Error);
        }

        [Fact]
        public void A_for_each_source_naming_nothing_at_all_is_refused()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: Loop", "",
                "```yaml step", "id: each", "forEach:", "  in: inputs.nowhere", "  as: table", "```", "",
                "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("nowhere", def.Error);
        }

        [Fact]
        public void A_for_each_source_that_is_neither_a_literal_list_nor_an_input_is_refused()
        {
            // §1.6: "Nothing else. No op results, no globs, no model queries."
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: Loop", "",
                "```yaml step", "id: each", "forEach:", "  in: model.tables", "  as: table", "```", "",
                "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("model.tables", def.Error);
        }

        // ---- §1.7 `call:` -----------------------------------------------------------------------------

        [Fact]
        public void A_call_is_parsed_and_stored()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: Hand off", "",
                "```yaml step", "id: prove-it", "call:", "  workflow: verified-measure",
                "  with:", "    targetMeasure: \"[[loop.measure]]\"", "  returns: [certificate]", "```", "",
                "Body."));
            Assert.Null(def.Error);
            var call = def.Steps[0].Call;
            Assert.NotNull(call);
            Assert.Equal("verified-measure", call!.Workflow);
            Assert.Equal("[[loop.measure]]", call.With["targetMeasure"]);
            Assert.Equal(new[] { "certificate" }, call.Returns);
        }

        [Fact]
        public void A_call_with_no_workflow_name_is_refused()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: Hand off", "",
                "```yaml step", "id: prove-it", "call:", "  returns: [certificate]", "```", "",
                "Body."));
            Assert.NotNull(def.Error);
            Assert.Contains("workflow", def.Error);
        }

        [Fact]
        public async Task A_call_to_a_workflow_that_does_not_exist_is_a_check_workflow_warn()
        {
            var (e, _) = Make();
            await e.SaveWorkflowAsync("caller", Caller("no-such-callee"), "human");
            var r = await e.CheckWorkflowAsync("caller");
            Assert.Contains(r.Findings, f => f.Severity == "warn" && f.Message.Contains("no-such-callee"));
            Assert.False(r.Ok);
        }

        [Fact]
        public async Task A_call_to_itself_is_refused()
        {
            var (e, _) = Make();
            await e.SaveWorkflowAsync("selfcaller", Caller("selfcaller", name: "selfcaller"), "human");
            var r = await e.CheckWorkflowAsync("selfcaller");
            Assert.Contains(r.Findings, f => f.Severity == "warn" && f.Message.Contains("selfcaller"));
            Assert.False(r.Ok);
        }

        [Fact]
        public async Task A_call_cycle_is_refused_naming_the_chain()
        {
            var (e, _) = Make();
            await e.SaveWorkflowAsync("cyc-a", Caller("cyc-b", name: "cyc-a"), "human");
            await e.SaveWorkflowAsync("cyc-b", Caller("cyc-a", name: "cyc-b"), "human");
            var r = await e.CheckWorkflowAsync("cyc-a");
            var warn = r.Findings.SingleOrDefault(f => f.Severity == "warn" && f.Message.Contains("cyc-b"));
            Assert.True(warn != null, "no cycle warn; findings: "
                + string.Join(" | ", r.Findings.Select(f => f.Severity + ": " + f.Message)));
            Assert.Contains("cyc-a", warn!.Message);   // the chain is named, not just "a cycle exists"
        }

        [Fact]
        public async Task A_call_chain_four_deep_is_refused_naming_the_chain_and_three_deep_is_not()
        {
            var (e, _) = Make();
            // d1 -> d2 -> d3 -> d4 is depth 4, counting the top-level run as depth 1
            await e.SaveWorkflowAsync("d1", Caller("d2", name: "d1"), "human");
            await e.SaveWorkflowAsync("d2", Caller("d3", name: "d2"), "human");
            await e.SaveWorkflowAsync("d3", Caller("d4", name: "d3"), "human");
            await e.SaveWorkflowAsync("d4", Leaf("d4"), "human");

            var deep = await e.CheckWorkflowAsync("d1");
            var warn = deep.Findings.SingleOrDefault(f => f.Severity == "warn" && f.Message.Contains("deep"));
            Assert.True(warn != null, "depth 4 was not refused; findings: "
                + string.Join(" | ", deep.Findings.Select(f => f.Severity + ": " + f.Message)));
            Assert.Contains("d1", warn!.Message);
            Assert.Contains("d2", warn.Message);
            Assert.Contains("d3", warn.Message);
            Assert.Contains("d4", warn.Message);

            // the same library entered one level down is depth 3 and is legal
            var ok = await e.CheckWorkflowAsync("d2");
            Assert.DoesNotContain(ok.Findings, f => f.Severity == "warn" && f.Message.Contains("deep"));
        }

        [Fact]
        public async Task A_with_key_that_is_not_a_declared_input_of_the_callee_is_refused()
        {
            // §3.4 check 4 / acceptance check 20: catchable without running anything.
            var (e, _) = Make();
            await e.SaveWorkflowAsync("callee", LeafWithInput("callee", "realInput"), "human");
            await e.SaveWorkflowAsync("caller2", Md(
                "---", "schemaVersion: 2", "name: caller2", "title: T", "---", "",
                "## Step 1: Hand off", "",
                "```yaml step", "id: handoff", "call:", "  workflow: callee",
                "  with:", "    notAnInput: x", "```", "",
                "Body."), "human");
            var r = await e.CheckWorkflowAsync("caller2");
            var warn = r.Findings.SingleOrDefault(f => f.Severity == "warn" && f.Message.Contains("notAnInput"));
            Assert.True(warn != null, "an undeclared with: key was accepted; findings: "
                + string.Join(" | ", r.Findings.Select(f => f.Severity + ": " + f.Message)));
            Assert.Contains("callee", warn!.Message);
        }

        [Fact]
        public async Task A_returns_name_the_callee_does_not_declare_is_refused()
        {
            var (e, _) = Make();
            await e.SaveWorkflowAsync("callee3", LeafWithInput("callee3", "realInput"), "human");
            await e.SaveWorkflowAsync("caller3", Md(
                "---", "schemaVersion: 2", "name: caller3", "title: T", "---", "",
                "## Step 1: Hand off", "",
                "```yaml step", "id: handoff", "call:", "  workflow: callee3", "  returns: [ghost]", "```", "",
                "Body."), "human");
            var r = await e.CheckWorkflowAsync("caller3");
            Assert.Contains(r.Findings, f => f.Severity == "warn" && f.Message.Contains("ghost"));
        }

        [Fact]
        public async Task A_call_to_a_template_is_refused_but_a_call_to_a_real_workflow_is_not()
        {
            // §1.7: the target must be `kind: workflow`, never a template. A template is a recipe with slots
            // and is not runnable, so a hand-off to one could only ever fail at run time.
            var (e, _) = Make();
            await e.SaveWorkflowAsync("caller-tmpl", Caller("hard-measure", "caller-tmpl"), "human");
            var bad = await e.CheckWorkflowAsync("caller-tmpl");
            var warn = bad.Findings.SingleOrDefault(f => f.Severity == "warn" && f.Message.Contains("hard-measure"));
            Assert.True(warn != null, "a call to a template was accepted; findings: "
                + string.Join(" | ", bad.Findings.Select(f => f.Severity + ": " + f.Message)));
            Assert.Contains("template", warn!.Message);

            // the control that stops this passing for the wrong reason: a real stock workflow is NOT warned
            await e.SaveWorkflowAsync("caller-real", Caller("new-measure", "caller-real"), "human");
            var good = await e.CheckWorkflowAsync("caller-real");
            Assert.DoesNotContain(good.Findings, f => f.Severity == "warn" && f.Message.Contains("new-measure"));
        }

        // ---- §2.2a: the escape hatch is parsed and its VALUE is checked, though nothing honours it yet ----

        [Fact]
        public void An_input_scope_is_parsed_and_stored()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: One", "", "Body.", "",
                "```yaml gate", "inputs:", "  - name: ticket", "    question: Which ticket?", "    scope: run", "```"));
            Assert.Null(def.Error);
            Assert.Equal("run", def.Steps[0].Gate.Inputs[0].Scope);
        }

        [Fact]
        public void An_invalid_input_scope_is_refused_naming_the_input_and_the_allowed_values()
        {
            // The key is parsed and stored ahead of the runner slice, the same seam as when:/forEach:/call:.
            // Nothing honours it yet, so a bad VALUE would otherwise sit in the file unremarked until the
            // slice that reads it. Refusing now is what stops an accepted-but-ignored key becoming an
            // accepted-but-WRONG key.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: One", "", "Body.", "",
                "```yaml gate", "inputs:", "  - name: ticket", "    question: Which ticket?", "    scope: global", "```"));
            Assert.NotNull(def.Error);
            Assert.Contains("'global'", def.Error);
            Assert.Contains("'ticket'", def.Error);      // by NAME, so the author knows which input
            Assert.Contains("iteration", def.Error);
            Assert.Contains("run", def.Error);
        }

        [Fact]
        public async Task An_invalid_input_scope_is_reported_by_check_workflow()
        {
            var (e, ws) = Make();
            var path = Path.Combine(ws, ".semanticus", "workflows");
            Directory.CreateDirectory(path);
            // written straight to disk: save_workflow parse-validates and would refuse it, which is the
            // point, but then check_workflow would have nothing to report on.
            File.WriteAllText(Path.Combine(path, "bad-scope.md"), Md(
                "---", "schemaVersion: 2", "name: bad-scope", "title: T", "---", "",
                "## Step 1: One", "", "Body.", "",
                "```yaml gate", "inputs:", "  - name: ticket", "    question: Which ticket?", "    scope: global", "```"));
            var r = await e.CheckWorkflowAsync("bad-scope");
            Assert.NotNull(r.ParseError);
            Assert.Contains("'ticket'", r.ParseError);
            Assert.False(r.Ok);
        }

        [Fact]
        public void V1_ignores_an_input_scope_entirely_including_a_bad_one()
        {
            // v1-forever. `scope:` on a gate input is a v2 key, so in v1 it is an unknown key and unknown
            // keys are preserved-and-ignored, exactly as they are today. A v1 file cannot start failing.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "title: T", "---", "",
                "## Step 1: One", "", "Body.", "",
                "```yaml gate", "inputs:", "  - name: ticket", "    question: Which ticket?", "    scope: global", "```"));
            Assert.Null(def.Error);
            Assert.Null(def.Steps[0].Gate.Inputs[0].Scope);
        }

        // ---- acceptance check 16b: the escape hatch cannot erase an earlier claim -----------------------

        [Fact]
        public void Certificate_may_not_be_collected_inside_a_step_that_repeats()
        {
            // §2.2a. ComputeCertificate reads the claim last-wins, so a `certificate` answered inside a loop
            // would let pass 5's FULL erase pass 2's OVERRIDDEN. That is exactly the laundering the
            // weakest-wins rule exists to stop, arriving through the escape hatch instead of the front door.
            var def = WorkflowParser.Parse(LoopCollecting("certificate", scope: null));
            Assert.NotNull(def.Error);
            Assert.Contains("'certificate'", def.Error);
            Assert.Contains("each", def.Error);          // the loop step, by id
        }

        [Fact]
        public void Certificate_is_refused_inside_a_loop_with_scope_run_too()
        {
            // "with or without scope: run" is the load-bearing half: scope: run is the hatch, so a rule that
            // only caught the default would leave the hatch wide open.
            var def = WorkflowParser.Parse(LoopCollecting("certificate", scope: "run"));
            Assert.NotNull(def.Error);
            Assert.Contains("'certificate'", def.Error);
        }

        [Fact]
        public void Certificate_collected_outside_a_loop_is_fine()
        {
            // the control that stops the three above passing for the wrong reason
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: Loop", "",
                "```yaml step", "id: each", "forEach:", "  in: [a, b]", "  as: item", "```", "", "Body.", "",
                "## Step 2: Claim", "", "Body.", "",
                "```yaml gate", "inputs:", "  - name: certificate", "    question: What level?", "```"));
            Assert.Null(def.Error);
        }

        /// <summary>The three verify bindings that read run-level lock state (§2.2a). Each resolves against a
        /// singleton, so a fresh answer per pass would re-point a lock that an earlier pass's evidence
        /// already depends on. Asserted one per binding from this list, never sampled.</summary>
        public static TheoryData<string> LockReadingBindings()
        {
            var d = new TheoryData<string>();
            foreach (var k in new[] { "openShapesFrom", "anchors", "dax_equivalence probe" }) d.Add(k);
            return d;
        }

        [Theory]
        [MemberData(nameof(LockReadingBindings))]
        public void An_input_a_lock_reading_verify_binds_may_not_be_collected_inside_a_loop(string binding)
        {
            var def = WorkflowParser.Parse(LoopBoundBy(binding));
            Assert.True(def.Error != null, $"binding '{binding}' inside a loop was accepted");
            Assert.Contains("locked", def.Error);
            Assert.Contains("each", def.Error);
        }

        private static string LoopCollecting(string inputName, string scope) => Md(
            "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
            "## Step 1: Loop", "",
            "```yaml step", "id: each", "forEach:", "  in: [a, b]", "  as: item", "```", "",
            "Body.", "",
            "```yaml gate", "inputs:", "  - name: " + inputName, "    question: What?",
            scope == null ? "    required: optional" : "    scope: " + scope, "```");

        /// <summary>A loop step collecting a text input that a verify on a LATER step binds as run-level lock
        /// state. The later step carries the objectRef and the verify, so the only defect is the binding.</summary>
        private static string LoopBoundBy(string binding)
        {
            var verify = binding switch
            {
                "openShapesFrom" => new[] { "  - kind: dax_equivalence", "    probe: witnessDax", "    openShapesFrom: shared" },
                "anchors" => new[] { "  - kind: expected_values", "    anchors: shared" },
                _ => new[] { "  - kind: dax_equivalence", "    probe: shared" },
            };
            var lines = new System.Collections.Generic.List<string>
            {
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "",
                "## Step 1: Loop", "",
                "```yaml step", "id: each", "forEach:", "  in: [a, b]", "  as: item", "```", "",
                "Body.", "",
                "```yaml gate", "inputs:", "  - name: shared", "    question: What?", "```", "",
                "## Step 2: Prove", "", "Body.", "",
                "```yaml gate", "inputs:",
                "  - name: target", "    question: Which measure?", "    type: objectRef",
                "  - name: witnessDax", "    question: The witness?",
                "verify:",
            };
            lines.AddRange(verify);
            lines.Add("```");
            return Md(lines.ToArray());
        }

        // ---- v1 keeps working, and its silence is broken by lint rather than by a behaviour change -----

        [Fact]
        public async Task A_v1_file_carrying_a_step_fence_gets_a_warn_that_the_fence_is_inert()
        {
            // The fence must stay inert TEXT in v1 (the v1-forever guarantee), but silence is exactly the
            // [T169] defect, so check_workflow says so rather than the parser changing what a v1 file means.
            var (e, _) = Make();
            await e.SaveWorkflowAsync("v1-fence", Md(
                "---", "name: v1-fence", "title: T", "---", "",
                "## Step 1: One", "",
                "```yaml step", "id: do-it", "```", "",
                "Body."), "human");
            var r = await e.CheckWorkflowAsync("v1-fence");
            var warn = r.Findings.SingleOrDefault(f => f.Severity == "warn" && f.Message.Contains("yaml step"));
            Assert.True(warn != null, "a v1 file's inert step fence passed unremarked; findings: "
                + string.Join(" | ", r.Findings.Select(f => f.Severity + ": " + f.Message)));
            Assert.Contains("schemaVersion: 2", warn!.Message);
        }

        [Fact]
        public async Task Every_stock_seed_still_checks_clean_under_the_v2_aware_linter()
        {
            // the v1-forever guarantee at the LINT layer, not just the parse layer
            var (e, _) = Make();
            foreach (var name in new[] { "verified-measure", "new-measure", "optimize-dax", "make-ai-ready" })
            {
                var r = await e.CheckWorkflowAsync(name);
                Assert.True(r.ParseError == null, $"{name} parse error: {r.ParseError}");
                Assert.DoesNotContain(r.Findings, f => f.Severity == "warn" && f.Message.Contains("yaml step"));
            }
        }

        // ---- fixtures ---------------------------------------------------------------------------------

        private static string Caller(string callee, string name = "caller") => Md(
            "---", "schemaVersion: 2", "name: " + name, "title: T", "---", "",
            "## Step 1: Hand off", "",
            "```yaml step", "id: handoff", "call:", "  workflow: " + callee, "```", "",
            "Body.");

        private static string Leaf(string name) => Md(
            "---", "schemaVersion: 2", "name: " + name, "title: T", "---", "",
            "## Step 1: Work", "", "Body.");

        private static string LeafWithInput(string name, string input) => Md(
            "---", "schemaVersion: 2", "name: " + name, "title: T", "---", "",
            "## Step 1: Work", "", "Body.", "",
            "```yaml gate", "inputs:", "  - name: " + input, "    question: Which one?", "```");
    }
}
