using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The unscoped review of the whole slice, aimed at ONE question: where does the parser throw away
    /// something the author wrote instead of refusing it? Four more were found after round 1's finding 3 and
    /// PR #301's P1, which makes six in the family.
    ///
    /// THE SHARED ROOT, stated once: this format has no rule that every line an author writes must be either
    /// CONSUMED or REFUSED. Each site decided for itself what to ignore, and four of them decided to ignore
    /// something load-bearing. The four mechanisms are genuinely different (a fence not recognised, children
    /// attached to a scalar key, a section key overwritten, an inline value on a map key), so there is no
    /// one-line fix; what they share is the missing invariant, and the fix applies that one rule at each
    /// site. The tests below enumerate the family rather than sampling it.
    ///
    /// The remaining run-time half of the same problem is bounded to repetition and hand-offs. Step conditions
    /// now execute. Public loop start admits a loop-bearing file unexpanded only after BOTH Unit 18 loop entry
    /// AND the per-frame certificate fold. Public calls share that plan and preserve closed answer boundaries.
    /// </summary>
    public sealed class WorkflowV2SilentDropTests
    {
        private sealed class Free : IEntitlement { public bool IsPro => false; public EntitlementInfo Info => new EntitlementInfo { Tier = "free" }; }
        private sealed class Pro : IEntitlement { public bool IsPro => true; public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" }; }

        private static (LocalEngine e, string ws) Make(IEntitlement ent = null)
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-drop-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            return (new LocalEngine(new SessionManager(), ent ?? new Free(), ws), ws);
        }

        private static string Md(params string[] lines) => string.Join("\n", lines);


        [Theory]
        [InlineData("local")]
        [InlineData("mcp")]
        [InlineData("rpc")]
        public async Task Public_calls_bind_inputs_return_declared_answers_and_keep_namespaces_closed(string door)
        {
            await ExerciseDoor(door, async (owner, start, submit) =>
            {
                await owner.SaveWorkflowAsync("public-specialist", Md(
                    "---", "schemaVersion: 2", "name: public-specialist", "title: Specialist", "---",
                    "## Step 1: Caller private must stay outside", "Body.",
                    "```yaml step", "id: leaked-in", "when: inputs.private.value == 'caller-private'", "```",
                    "## Step 2: Bound inputs", "Body.",
                    "```yaml step", "id: answer", "when: inputs.region.value == 'North' && inputs.label.value == 'fixed-label'", "```",
                    "```yaml gate", "inputs:",
                    "  - name: region", "    question: Region?", "    required: required",
                    "  - name: label", "    question: Label?", "    required: required",
                    "  - name: outcome", "    question: Outcome?", "    required: required",
                    "  - name: private", "    question: Private note?", "    required: optional", "    scope: run", "```"), "human");
                await owner.SaveWorkflowAsync("public-composite", Md(
                    "---", "schemaVersion: 2", "name: public-composite", "title: Composite", "---",
                    "## Step 1: Choose", "Body.", "```yaml step", "id: choose", "```",
                    "```yaml gate", "inputs:",
                    "  - name: region", "    question: Region?", "    required: required",
                    "  - name: private", "    question: Private note?", "    required: optional", "    scope: run", "```",
                    "## Step 2: Hand off", "Body.",
                    "```yaml step", "id: hand-off", "call:", "  workflow: public-specialist", "  with:",
                    "    region: inputs.region", "    label: fixed-label", "  returns: [outcome]", "```",
                    "## Step 3: Use the answer", "Body.",
                    "```yaml step", "id: use-return", "when: inputs.outcome.value == 'accepted'", "```",
                    "## Step 4: Callee private must stay outside", "Body.",
                    "```yaml step", "id: leaked-out", "when: inputs.private.value == 'callee-private'", "```"), "human");

                var run = await start("public-composite");
                Assert.Equal(new[] { "choose", "hand-off", "hand-off/leaked-in", "hand-off/answer", "use-return", "leaked-out" },
                    run.Steps.Select(s => s.StepId));
                var id = run.RunId;
                run = await submit(id, "choose", JsonSerializer.Serialize(new { region = "North", @private = "caller-private" }));
                Assert.Equal("hand-off", run.CurrentStep.StepId);
                run = await submit(id, "hand-off", "{}");
                Assert.Equal("hand-off/answer", run.CurrentStep.StepId);
                Assert.Equal("not_applicable", run.Steps.Single(s => s.StepId == "hand-off/leaked-in").Status);
                // Both required with-bound inputs are honored without supplying them a second time.
                run = await submit(id, "hand-off/answer", JsonSerializer.Serialize(new { outcome = "accepted", @private = "callee-private" }));
                Assert.Equal("use-return", run.CurrentStep.StepId);
                run = await submit(id, "use-return", "{}");
                Assert.Equal("completed", run.Status);
                Assert.Equal(id, run.RunId);
                Assert.Equal("not_applicable", run.Steps.Single(s => s.StepId == "leaked-out").Status);
                var frame = Assert.Single(run.Frames);
                Assert.Equal("passed", frame.State);
                Assert.Equal(new[] { "outcome" }, frame.Returned);
            });
        }

        [Fact]
        public async Task Public_call_passes_a_required_source_list_into_the_callees_loop()
        {
            await ExerciseDoor("local", async (owner, start, submit) =>
            {
                await owner.SaveWorkflowAsync("public-list-worker", Md(
                    "---", "schemaVersion: 2", "name: public-list-worker", "title: Worker", "---",
                    "## Step 1: Repeat", "Process [[loop.region]].",
                    "```yaml gate", "inputs:", "  - name: regions", "    question: Regions?", "    required: required", "```",
                    "```yaml step", "id: each", "forEach:", "  in: inputs.regions", "  as: region", "```"), "human");
                await owner.SaveWorkflowAsync("public-list-parent", Md(
                    "---", "schemaVersion: 2", "name: public-list-parent", "title: Parent", "---",
                    "## Step 1: Choose", "Body.", "```yaml step", "id: choose", "```",
                    "```yaml gate", "inputs:", "  - name: regions", "    question: Regions?", "    required: required", "```",
                    "## Step 2: Delegate", "Body.", "```yaml step", "id: delegate", "call:",
                    "  workflow: public-list-worker", "  with:", "    regions: inputs.regions", "```"), "human");
                var run = await start("public-list-parent");
                Assert.True(run.TotalStepsProvisional);
                run = await submit(run.RunId, "choose", JsonSerializer.Serialize(new { regions = "North,South" }));
                run = await submit(run.RunId, "delegate", "{}");
                Assert.Equal("delegate/each", run.CurrentStep.StepId);
                run = await submit(run.RunId, "delegate/each", "{}");
                Assert.Equal("delegate/each#0", run.CurrentStep.StepId);
                Assert.Contains("North", run.CurrentStep.Instructions);
                run = await submit(run.RunId, "delegate/each#0", "{}");
                Assert.Equal("delegate/each#1", run.CurrentStep.StepId);
                Assert.Contains("South", run.CurrentStep.Instructions);
                run = await submit(run.RunId, "delegate/each#1", "{}");
                Assert.Equal("completed", run.Status);
                Assert.All(run.Frames, frame => Assert.Equal("passed", frame.State));
                Assert.Equal(new[] { "North", "South" }, run.Frames.Where(f => f.Kind == "iteration").Select(f => f.LoopValue));
            });
        }

        [Theory]
        [InlineData("local")]
        [InlineData("mcp")]
        [InlineData("rpc")]
        public async Task Public_combined_loop_calls_bind_each_iteration_and_finish_one_run(string door)
        {
            await ExerciseDoor(door, async (owner, start, submit) =>
            {
                await owner.SaveWorkflowAsync("public-loop-worker", Md(
                    "---", "schemaVersion: 2", "name: public-loop-worker", "title: Worker", "---",
                    "## Step 1: Sales", "Body.",
                    "```yaml step", "id: sales", "when: inputs.table.value == 'Sales' && inputs.ordinal.value == '0'", "```",
                    "```yaml gate", "inputs:", "  - name: table", "    question: Table?", "    required: required",
                    "  - name: ordinal", "    question: Item number?", "    required: required", "```",
                    "## Step 2: Product", "Body.",
                    "```yaml step", "id: product", "when: inputs.table.value == 'Product' && inputs.ordinal.value == '1'", "```"), "human");
                await owner.SaveWorkflowAsync("public-loop-caller", Md(
                    "---", "schemaVersion: 2", "name: public-loop-caller", "title: Caller", "---",
                    "## Step 1: Delegate each", "Body.", "```yaml step", "id: each", "forEach:",
                    "  in: [Sales, Product]", "  as: table", "call:", "  workflow: public-loop-worker",
                    "  with:", "    table: '[[loop.table]]'", "    ordinal: '[[loop.index]]'", "```"), "human");
                var run = await start("public-loop-caller");
                AssertUnexpanded(run, "each", 1);
                var id = run.RunId;
                run = await submit(id, "each", "{}");
                Assert.Equal("each#0", run.CurrentStep.StepId);
                run = await submit(id, "each#0", "{}");
                Assert.Equal("each#0/sales", run.CurrentStep.StepId);
                run = await submit(id, "each#0/sales", "{}");
                Assert.Equal("each#1", run.CurrentStep.StepId);
                run = await submit(id, "each#1", "{}");
                Assert.Equal("each#1/product", run.CurrentStep.StepId);
                run = await submit(id, "each#1/product", "{}");
                Assert.Equal("completed", run.Status);
                Assert.Equal(id, run.RunId);
                Assert.Equal(2, run.Frames.Count(f => f.Kind == "call"));
                Assert.All(run.Frames, frame => Assert.Equal("passed", frame.State));
                Assert.Equal(2, run.Steps.Count(s => s.Status == "not_applicable"));
            });
        }

        [Fact]
        public async Task Public_nested_call_returns_relay_through_two_declared_boundaries()
        {
            await ExerciseDoor("local", async (owner, start, submit) =>
            {
                await owner.SaveWorkflowAsync("public-leaf", Md(
                    "---", "schemaVersion: 2", "name: public-leaf", "title: Leaf", "---",
                    "## Step 1: Answer", "Body.", "```yaml step", "id: answer", "```",
                    "```yaml gate", "inputs:", "  - name: outcome", "    question: Outcome?", "    required: required", "```"), "human");
                await owner.SaveWorkflowAsync("public-relay", Md(
                    "---", "schemaVersion: 2", "name: public-relay", "title: Relay", "---",
                    "## Step 1: Relay", "Body.", "```yaml step", "id: relay", "call:",
                    "  workflow: public-leaf", "  returns: [outcome]", "```"), "human");
                await owner.SaveWorkflowAsync("public-nested", Md(
                    "---", "schemaVersion: 2", "name: public-nested", "title: Nested", "---",
                    "## Step 1: Delegate", "Body.", "```yaml step", "id: delegate", "call:",
                    "  workflow: public-relay", "  returns: [outcome]", "```",
                    "## Step 2: Use", "Body.", "```yaml step", "id: use", "when: inputs.outcome.value == 'ok'", "```"), "human");
                var run = await start("public-nested");
                Assert.Equal(new[] { "delegate", "delegate/relay", "delegate/relay/answer", "use" }, run.Steps.Select(s => s.StepId));
                run = await submit(run.RunId, "delegate", "{}");
                run = await submit(run.RunId, "delegate/relay", "{}");
                run = await submit(run.RunId, "delegate/relay/answer", JsonSerializer.Serialize(new { outcome = "ok" }));
                Assert.Equal("use", run.CurrentStep.StepId);
                run = await submit(run.RunId, "use", "{}");
                Assert.Equal("completed", run.Status);
                Assert.Equal(new int?[] { 2, 3 }, run.Frames.Select(f => f.Depth));
                Assert.All(run.Frames, frame => Assert.Equal("passed", frame.State));
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Public_loop_call_returns_preserve_the_declared_iteration_scope(bool runScoped)
        {
            await ExerciseDoor("local", async (owner, start, submit) =>
            {
                await owner.SaveWorkflowAsync("public-note-worker", Md(new[] {
                    "---", "schemaVersion: 2", "name: public-note-worker", "title: Worker", "---",
                    "## Step 1: Note", "Body.", "```yaml step", "id: note", "```",
                    "```yaml gate", "inputs:", "  - name: note", "    question: Note?", "    required: required",
                }.Concat(runScoped ? new[] { "    scope: run" } : Array.Empty<string>()).Concat(new[] { "```" }).ToArray()), "human");
                await owner.SaveWorkflowAsync("public-note-loop", Md(
                    "---", "schemaVersion: 2", "name: public-note-loop", "title: Loop", "---",
                    "## Step 1: Each region", "Body.", "```yaml step", "id: each", "forEach:",
                    "  in: [North, South]", "  as: region", "call:", "  workflow: public-note-worker",
                    "  with:", "    note: '[[loop.region]]'", "  returns: [note]", "```",
                    "## Step 2: Read the final note", "Body.", "```yaml step", "id: after",
                    "when: inputs.note.value == 'South'", "```"), "human");
                var run = await start("public-note-loop");
                run = await submit(run.RunId, "each", "{}");
                for (var i = 0; i < 2; i++)
                {
                    run = await submit(run.RunId, "each#" + i, "{}");
                    run = await submit(run.RunId, "each#" + i + "/note", "{}");
                }
                if (runScoped)
                {
                    Assert.Equal("active", run.Status);
                    Assert.Equal("after", run.CurrentStep.StepId);
                    run = await submit(run.RunId, "after", "{}");
                    Assert.Equal("done", run.Steps.Single(s => s.StepId == "after").Status);   // no passing check: done, not passed
                }
                else Assert.Equal("not_applicable", run.Steps.Single(s => s.StepId == "after").Status);
                Assert.Equal("completed", run.Status);
            });
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("self")]
        [InlineData("cycle")]
        [InlineData("depth")]
        [InlineData("disabled")]
        [InlineData("template")]
        [InlineData("unreadable")]
        [InlineData("with")]
        [InlineData("returns")]
        public async Task Public_call_invalid_closures_refuse_without_allocating_a_run(string problem)
        {
            await ExerciseDoor("local", async (owner, start, submit) =>
            {
                const string root = "public-invalid-root";
                const string sub = "public-invalid-sub";
                await owner.SaveWorkflowAsync(sub, PlainCallTargetMd(sub), "human");
                if (problem == "template") await owner.SaveWorkflowAsync(sub, Md(
                    "---", "schemaVersion: 2", "name: " + sub, "title: Template", "kind: template",
                    "slots:", "  - name: region", "    question: Region?", "---", "## Step 1: Work", "Work in {{region}}."), "human");
                if (problem == "unreadable") File.WriteAllText((await owner.GetWorkflowAsync(sub)).FilePath, Md(
                    "---", "schemaVersion: 2", "name: " + sub, "unknownField: broken", "---", "## Step 1: Work", "Body."));
                var target = problem == "self" ? root : problem == "missing" ? "absent-worker" : sub;
                if (problem == "cycle") await owner.SaveWorkflowAsync(sub, CallOnlyMd(sub, root), "human");
                if (problem == "depth")
                {
                    await owner.SaveWorkflowAsync("public-too-deep", PlainCallTargetMd("public-too-deep"), "human");
                    await owner.SaveWorkflowAsync("public-deeper", CallOnlyMd("public-deeper", "public-too-deep"), "human");
                    await owner.SaveWorkflowAsync(sub, CallOnlyMd(sub, "public-deeper"), "human");
                }
                var control = problem == "with" ? new[] { "  with:", "    nonexistent: literal" }
                    : problem == "returns" ? new[] { "  returns: [nonexistent]" } : Array.Empty<string>();
                await owner.SaveWorkflowAsync(root, Md(new[] {
                    "---", "schemaVersion: 2", "name: " + root, "title: Caller", "---",
                    "## Step 1: Delegate", "Body.", "```yaml step", "id: delegate", "call:", "  workflow: " + target,
                }.Concat(control).Concat(new[] { "```" }).ToArray()), "human");
                if (problem == "disabled") await owner.SetWorkflowEnabledAsync(sub, false, "human");
                Assert.NotNull(await Record.ExceptionAsync(() => start(root)));
                Assert.Equal(0, owner.ActiveWorkflowRunsForTest);
                Assert.NotNull(await Record.ExceptionAsync(() => owner.GetWorkflowRunAsync(null)));
                await owner.SaveWorkflowAsync("public-after-refusal", PlainCallTargetMd("public-after-refusal"), "human");
                var accepted = await start("public-after-refusal");
                Assert.Equal("wfr-1", accepted.RunId);
                Assert.Null(accepted.Frames);
                Assert.Null(accepted.TotalStepsProvisional);
            });
        }

        [Fact]
        public async Task Public_call_entitles_the_enforced_callee_before_allocating_a_run()
        {
            var (owner, _) = Make(new Free());
            await owner.SaveWorkflowAsync("public-paid-callee", Md(
                "---", "schemaVersion: 2", "name: public-paid-callee", "title: Paid", "strictness: hard", "---",
                "## Step 1: Ask", "Body.", "```yaml gate", "inputs:",
                "  - name: answer", "    question: Answer?", "    required: required", "```"), "human");
            await owner.SaveWorkflowAsync("public-free-root", CallOnlyMd("public-free-root", "public-paid-callee"), "human");
            var refused = await Record.ExceptionAsync(() => owner.StartWorkflowAsync("public-free-root", "human"));
            Assert.NotNull(refused);
            Assert.Contains("Pro", refused.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, owner.ActiveWorkflowRunsForTest);
        }

        private static string PlainCallTargetMd(string name) => Md(
            "---", "schemaVersion: 2", "name: " + name, "title: Worker", "---",
            "## Step 1: Work", "Body.", "```yaml step", "id: work", "```");

        private static async Task ExerciseDoor(string door,
            Func<LocalEngine, Func<string, Task<WorkflowRunView>>, Func<string, string, string, Task<WorkflowRunView>>, Task> exercise)
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-public-call-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            using var sessions = new SessionManager();
            var owner = new LocalEngine(sessions, new Pro(), ws);
            using var cts = new CancellationTokenSource();
            RpcServer server = null;
            RemoteEngine remote = null;
            Task serverTask = Task.CompletedTask;
            try
            {
                IEngine engine = owner;
                if (door == "rpc")
                {
                    var pipe = "semanticus-public-call-" + Guid.NewGuid().ToString("N");
                    server = new RpcServer(sessions, owner, pipe);
                    serverTask = server.RunAsync(cts.Token);
                    remote = await RemoteEngine.ConnectAsync(pipe);
                    engine = remote;
                }
                await exercise(owner,
                    name => door == "mcp" ? McpTools.StartWorkflow(engine, name) : engine.StartWorkflowAsync(name, "human"),
                    (run, step, answers) => door == "mcp" ? McpTools.SubmitWorkflowStep(engine, run, step, answers)
                        : engine.SubmitWorkflowStepAsync(run, step, answers, "human"));
            }
            finally
            {
                remote?.Dispose();
                cts.Cancel();
                try { await serverTask; } catch (OperationCanceledException) { }
                server?.Dispose();
                sessions.Dispose();
                Directory.Delete(ws, true);
            }
        }

        // ---- U1: public start needs Unit 18 AND the certificate fold; neither alone unlocks it ---------

        [Fact]
        public async Task Public_start_admits_an_inline_loop_unexpanded()
        {
            // Units 15-16 already execute an inline forEach. Public start still needs Unit 18 and the
            // certificate fold together before it may let that file in, and must not expand it: start still
            // accepts no answers.
            var (e, _) = Make(new Pro());
            await e.SaveWorkflowAsync("ctrl-for-each", InlineLoopMd("ctrl-for-each"), "human");

            var run = await e.StartWorkflowAsync("ctrl-for-each", "human");
            AssertUnexpanded(run, "act", 1);
        }

        [Fact]
        public async Task Public_start_unrolls_a_call_without_executing_its_header()
        {
            var (e, _) = Make(new Pro());
            await e.SaveWorkflowAsync("sub-thing", PlainCallTargetMd("sub-thing"), "human");
            await e.SaveWorkflowAsync("ctrl-call", CallOnlyMd("ctrl-call", "sub-thing"), "human");
            var run = await e.StartWorkflowAsync("ctrl-call", "human");
            Assert.Equal("hand-off", run.CurrentStep.StepId);
            Assert.Equal(new[] { "hand-off", "hand-off/work" }, run.Steps.Select(s => s.StepId));
            Assert.Equal("pending", run.Steps[1].Status);
            Assert.Equal(1, e.ActiveWorkflowRunsForTest);
        }

        [Fact]
        public async Task U1_control_authoring_and_checking_such_a_file_stays_legal()
        {
            // Authoring and checking remain available alongside public execution.
            var (e, _) = Make(new Pro());
            var info = await e.SaveWorkflowAsync("ctrl-ok", Md(
                "---", "schemaVersion: 2", "name: ctrl-ok", "title: T", "---",
                "## Step 1: Ask", "Body.",
                "```yaml gate", "inputs:", "  - name: approval", "    question: Approved?", "```",
                "## Step 2: Act",
                "```yaml step", "id: act", "when: inputs.approval.answered", "```", "", "Body."), "human");
            Assert.Contains(info, w => w.Name == "ctrl-ok");
            var r = await e.CheckWorkflowAsync("ctrl-ok");
            Assert.DoesNotContain(r.Findings, f => f.Severity == "error");
        }

        [Fact]
        public async Task U1_control_a_v2_workflow_with_no_control_field_still_runs()
        {
            // The refusal must be about the unexecutable fields, not about v2. A plain v2 workflow runs.
            var (e, _) = Make(new Pro());
            await e.SaveWorkflowAsync("plain-v2", Md(
                "---", "schemaVersion: 2", "name: plain-v2", "title: T", "---",
                "## Step 1: Ask", "Body.",
                "```yaml gate", "inputs:", "  - name: approval", "    question: Approved?", "```"), "human");
            var run = await e.StartWorkflowAsync("plain-v2", "human");
            Assert.NotNull(run);
        }

        [Fact]
        public async Task Public_start_admits_an_adjacent_earlier_source_loop_unexpanded()
        {
            var (e, _) = Make(new Pro());
            await e.SaveWorkflowAsync("adj-source", AdjacentSourceMd("adj-source"), "human");

            var run = await e.StartWorkflowAsync("adj-source", "human");
            AssertUnexpanded(run, "choose", 2);
            Assert.Equal(new[] { "choose", "each" }, run.Steps.Select(s => s.StepId).ToArray());
        }

        [Fact]
        public async Task Public_start_admits_a_nonadjacent_earlier_source_loop_unexpanded()
        {
            // T-1556: after both Unit 18 and the certificate fold, public start must admit a declarer two or
            // more rows back. Neither precondition alone unlocks that admission. The loop stays one
            // unexpanded template. Refusing it at start was the stale Unit-17 gap.
            var (e, _) = Make(new Pro());
            await e.SaveWorkflowAsync("nonadj-source", NonadjacentSourceMd("nonadj-source"), "human");

            var run = await e.StartWorkflowAsync("nonadj-source", "human");
            AssertUnexpanded(run, "choose", 3);
            Assert.Equal(new[] { "choose", "between", "each" }, run.Steps.Select(s => s.StepId).ToArray());
        }

        [Fact]
        public async Task Public_start_admits_a_self_sourced_loop_unexpanded()
        {
            var (e, _) = Make(new Pro());
            await e.SaveWorkflowAsync("self-source", SelfSourceMd("self-source"), "human");

            var run = await e.StartWorkflowAsync("self-source", "human");
            AssertUnexpanded(run, "each", 1);
        }

        [Fact]
        public async Task Public_start_never_judges_an_unexpanded_when()
        {
            // Public start itself must leave an unexpanded loop current, even when its when: is already
            // false. Judging that predicate at start would hide setup before the source is checked.
            var (e, _) = Make(new Pro());
            await e.SaveWorkflowAsync("false-when-loop", Md(
                "---", "schemaVersion: 2", "name: false-when-loop", "title: T", "---",
                "## Step 1: Act",
                "```yaml step", "id: act", "when: inputs.missing.answered",
                "forEach:", "  in: [Sales, Product]", "  as: table", "```", "", "Body."), "human");

            var run = await e.StartWorkflowAsync("false-when-loop", "human");
            AssertUnexpanded(run, "act", 1);
            Assert.All(run.Steps, s => Assert.NotEqual("not_applicable", s.Status));
        }

        [Fact]
        public async Task Public_start_optional_blank_becomes_an_empty_loop()
        {
            var (e, _) = Make(new Pro());
            await e.SaveWorkflowAsync("opt-blank", Md(
                "---", "schemaVersion: 2", "name: opt-blank", "title: T", "---",
                "## Step 1: Repeat", "Body.",
                "```yaml gate", "inputs:",
                "  - name: tables", "    question: Which tables?", "    required: optional", "```",
                "```yaml step", "id: each", "forEach:", "  in: inputs.tables", "  as: table", "```"), "human");

            var started = await e.StartWorkflowAsync("opt-blank", "human");
            AssertUnexpanded(started, "each", 1);
            var run = await e.SubmitWorkflowStepAsync(started.RunId, "each", "{\"tables\":\"\"}", "human");
            Assert.Equal("completed", run.Status);
            Assert.Equal(1, run.TotalSteps);
            Assert.DoesNotContain(run.Steps, s => s.StepId.Contains("#"));
            Assert.Equal("not_applicable", run.Steps[0].Status);
            Assert.Contains("empty", run.Steps[0].Note, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Public_start_unusable_source_refuses_before_a_false_or_unreadable_when()
        {
            // The setup call must validate its SOURCE before it handles the condition, so an unusable list
            // refuses rather than being quietly skipped as `condition_false`.
            //
            // T-3839 correction: this case was authored with a second row whose `when:` was the structurally
            // unreadable `model.tableCount >`. That row could never run, on this base or on the base the pack
            // was written against, because `WorkflowPredicate.Parse` returns null for that shape
            // (`WorkflowPredicate.cs:240`) and `save_workflow` refuses the file outright. It went unnoticed
            // only because the pre-admission ban failed the first row first. Loop-free FALSE is therefore the
            // one unusable-source-beside-a-condition shape the PUBLIC door can express, and it is asserted
            // here. The unreadable half is a runner-level shape: it needs an in-memory definition that skips
            // the parser, and `WorkflowRunPlanTests.Unusable_loop_entry_source_refuses_beside_false_and_unreadable_when`
            // already owns all twelve source/condition pairs. This case pins the public-door truth instead:
            // an unreadable condition never reaches a run at all.
            var (e, _) = Make(new Pro());

            const string unusableFalse = "unusable-false";
            await e.SaveWorkflowAsync(unusableFalse, Md(
                "---", "schemaVersion: 2", "name: " + unusableFalse, "title: T", "---",
                "## Step 1: Repeat", "Body.",
                "```yaml gate", "inputs:",
                "  - name: tables", "    question: Which tables?", "```",
                "```yaml step", "id: each", "when: inputs.missing.answered",
                "forEach:", "  in: inputs.tables", "  as: table", "```"), "human");

            var started = await e.StartWorkflowAsync(unusableFalse, "human");
            AssertUnexpanded(started, "each", 1);
            var ex = await Record.ExceptionAsync(
                () => e.SubmitWorkflowStepAsync(started.RunId, "each", "{}", "human"));
            Assert.True(ex != null, unusableFalse + ": an unusable source was skipped as a condition");
            Assert.Contains("skip", ex!.Message, StringComparison.OrdinalIgnoreCase);
            var still = await e.GetWorkflowRunAsync(started.RunId);
            AssertUnexpanded(still, "each", 1);
            Assert.All(still.Steps, s => Assert.NotEqual("not_applicable", s.Status));

            // The unreadable condition is refused at SAVE, before any run exists, so admitting public loop
            // start did not open a door onto it. Nothing was written, so there is nothing to start.
            var saveEx = await Record.ExceptionAsync(() => e.SaveWorkflowAsync("unusable-unread", Md(
                "---", "schemaVersion: 2", "name: unusable-unread", "title: T", "---",
                "## Step 1: Repeat", "Body.",
                "```yaml gate", "inputs:",
                "  - name: tables", "    question: Which tables?", "```",
                "```yaml step", "id: each", "when: model.tableCount >",
                "forEach:", "  in: inputs.tables", "  as: table", "```"), "human"));
            Assert.True(saveEx != null, "an unreadable condition must not save");
            Assert.Contains("could not read the condition", saveEx!.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was written", saveEx.Message, StringComparison.Ordinal);
            var startEx = await Record.ExceptionAsync(() => e.StartWorkflowAsync("unusable-unread", "human"));
            Assert.NotNull(startEx);
        }

        [Fact]
        public async Task Public_start_loop_fact_when_lands_on_the_first_applicable_iteration()
        {
            var (e, _) = Make(new Pro());
            await e.SaveWorkflowAsync("loop-fact-when", Md(
                "---", "schemaVersion: 2", "name: loop-fact-when", "title: T", "---",
                "## Step 1: Act",
                "```yaml step", "id: act", "when: loop.table == 'Product'",
                "forEach:", "  in: [Sales, Product]", "  as: table", "```", "", "Body."), "human");

            var started = await e.StartWorkflowAsync("loop-fact-when", "human");
            AssertUnexpanded(started, "act", 1);
            var run = await e.SubmitWorkflowStepAsync(started.RunId, "act", "{}", "human");
            Assert.Equal("active", run.Status);
            Assert.Equal("act#1", run.CurrentStep.StepId);
            Assert.Contains(run.Steps, s => s.StepId == "act#0" && s.Status == "not_applicable");
            Assert.Contains(run.Steps, s => s.StepId == "act#1" && s.Status != "not_applicable");
        }

        [Fact]
        public async Task Public_start_unrolls_a_callee_loop_and_marks_its_total_provisional()
        {
            var (e, _) = Make(new Pro());
            await e.SaveWorkflowAsync("loop-callee", InlineLoopMd("loop-callee"), "human");
            await e.SaveWorkflowAsync("loop-parent", CallOnlyMd("loop-parent", "loop-callee"), "human");
            var run = await e.StartWorkflowAsync("loop-parent", "human");
            Assert.Equal(new[] { "hand-off", "hand-off/act" }, run.Steps.Select(s => s.StepId));
            Assert.Equal("hand-off", run.CurrentStep.StepId);
            Assert.True(run.TotalStepsProvisional);
            Assert.Equal(1, e.ActiveWorkflowRunsForTest);
        }

        [Fact]
        public async Task Public_start_refusal_coverage_keeps_parse_errors_and_templates_blocked()
        {
            var (e, ws) = Make(new Pro());
            var userDir = Path.Combine(ws, ".semanticus", "workflows");
            Directory.CreateDirectory(userDir);
            File.WriteAllText(Path.Combine(userDir, "broken-v2.md"), Md(
                "---", "schemaVersion: 2", "name: broken-v2", "title: T", "  stray: line", "---",
                "## Step 1: Do", "Body."));
            var parseEx = await Record.ExceptionAsync(() => e.StartWorkflowAsync("broken-v2", "human"));
            Assert.True(parseEx != null, "a parse-error workflow started");
            Assert.Contains("parse error", parseEx!.Message);

            await e.SaveWorkflowAsync("tmpl-v2", Md(
                "---", "schemaVersion: 2", "name: tmpl-v2", "kind: template", "title: T",
                "slots:", "  - name: region", "    question: Which region?",
                "---", "## Step 1: Do", "Body about {{region}}."), "human");
            var tmplEx = await Record.ExceptionAsync(() => e.StartWorkflowAsync("tmpl-v2", "human"));
            Assert.True(tmplEx != null, "a template started as if it were a workflow");
            Assert.Contains("template", tmplEx!.Message);

            Assert.Equal(0, e.ActiveWorkflowRunsForTest);
        }

        [Fact]
        public async Task Public_start_three_doors_admit_a_nonadjacent_earlier_source_unexpanded()
        {
            var failures = new List<string>();
            foreach (var door in new[] { "local", "mcp", "rpc" })
            {
                try
                {
                    var run = await StartOnDoor(door, "nonadj-" + door, NonadjacentSourceMd("nonadj-" + door));
                    AssertUnexpanded(run, "choose", 3);
                    Assert.Equal(new[] { "choose", "between", "each" }, run.Steps.Select(s => s.StepId).ToArray());
                }
                catch (Exception ex)
                {
                    failures.Add(door + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            Assert.True(failures.Count == 0, "public start must admit the nonadjacent earlier-source on every door, unexpanded. "
                + string.Join(" | ", failures));
        }

        [Fact]
        public async Task Public_start_three_doors_admit_an_inline_loop_unexpanded()
        {
            await AssertAdmittedOnThreeDoors("inline-", InlineLoopMd, "act", 1, new[] { "act" });
        }

        [Fact]
        public async Task Public_start_three_doors_admit_a_self_sourced_loop_unexpanded()
        {
            await AssertAdmittedOnThreeDoors("self-", SelfSourceMd, "each", 1, new[] { "each" });
        }

        [Fact]
        public async Task Public_start_three_doors_admit_an_adjacent_earlier_source_unexpanded()
        {
            await AssertAdmittedOnThreeDoors("adj-", AdjacentSourceMd, "choose", 2, new[] { "choose", "each" });
        }

        [Fact]
        public void Public_start_a_top_level_step_may_carry_both_forEach_and_call()
        {
            // The format accepts both controls on one step; each expanded iteration owns one call.
            var def = WorkflowParser.Parse(CombinedForEachAndCallMd("t", "sub-thing"));
            Assert.True(def.Error == null, "a legal combined forEach+call step was refused: " + def.Error);
            Assert.Equal("table", def.Steps[0].ForEach!.As);
            Assert.Equal("sub-thing", def.Steps[0].Call!.Workflow);
        }

        [Fact]
        public void Public_start_legacy_record_control_keeps_the_exact_no_loop_JSON_bytes()
        {
            // Legacy pre-v7 terminal-record control. The coverage-backed v7 golden below is the real
            // acceptance-check-34 guard; this pin stays only so a reshape of the old shape is still visible.

            var path = Path.Combine(AppContext.BaseDirectory, "workflows", "new-measure.md");
            if (!File.Exists(path))
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
                Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
                path = Path.Combine(dir.FullName, "Semanticus.Engine", "workflows", "new-measure.md");
            }
            Assert.True(File.Exists(path), "stock workflow missing: " + path);
            var def = WorkflowParser.ParseFile(path, "stock");
            Assert.Null(def.Error);
            var run = new WorkflowRunState("wfr-seed-record", def, null)
            {
                Status = "completed",
                StartedUtc = "2026-01-02T03:04:05.0000000Z",
                FinishedUtc = "2026-01-02T03:05:06.0000000Z",
                ModelName = "Invented Sales Model",
                ModelFingerprint = "fixture-fingerprint",
            };
            foreach (var result in run.Results)
            {
                result.Status = "passed";
                result.EffectiveStrictness = result.StepId == "step-2" ? null : "hard";
            }

            var json = JsonSerializer.Serialize(WorkflowRunner.BuildRunRecord(run));
            Assert.Equal("{\"runId\":\"wfr-seed-record\",\"workflow\":\"new-measure\",\"version\":1,\"status\":\"completed\","
                + "\"abortReason\":null,\"startedUtc\":\"2026-01-02T03:04:05.0000000Z\",\"finishedUtc\":\"2026-01-02T03:05:06.0000000Z\","
                + "\"modelName\":\"Invented Sales Model\",\"modelFingerprint\":\"fixture-fingerprint\",\"steps\":["
                + "{\"StepId\":\"step-1\",\"Status\":\"passed\",\"Note\":null,\"EffectiveStrictness\":\"hard\",\"answers\":[],\"verify\":[],\"verifyHistory\":null},"
                + "{\"StepId\":\"step-2\",\"Status\":\"passed\",\"Note\":null,\"EffectiveStrictness\":null,\"answers\":[],\"verify\":[],\"verifyHistory\":null},"
                + "{\"StepId\":\"step-3\",\"Status\":\"passed\",\"Note\":null,\"EffectiveStrictness\":\"hard\",\"answers\":[],\"verify\":[],\"verifyHistory\":null}],"
                + "\"witnessLocks\":null,\"witnessRevisions\":null,\"partitionRevisions\":null,\"anchorLocks\":null,\"anchorRevisions\":null}", json);
        }

        [Fact]
        public async Task Public_start_no_loop_v7_terminal_record_is_byte_identical_to_the_recorded_golden()
        {
            // Copied from the checked T-1660 golden (commit 160d326; 42cecf0 is not in this clone).
            // Do not recapture. Public start does not edit certificate tests; this isolated copy is the
            // pack's own proof that the coverage-backed v7 no-loop record stays exact.
            var json = await SerializeNormalizedNoLoopV7Record();
            var golden = File.ReadAllText(V7GoldenPath());
            Assert.Equal(golden, json);
        }

        // ---- U2: an indented fence is not a fence -----------------------------------------------------

        [Fact]
        public void U2_an_indented_gate_fence_is_refused_in_v2()
        {
            // One leading space and the whole gate becomes prose: the hard bpa_clean verification inside it
            // disappears with no error and no warning, and the step ships with no gate at all.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Check", "Body.",
                " ```yaml gate", " strictness: hard", " verify:", "   - kind: bpa_clean", " ```"));
            Assert.True(def.Error != null, "an indented 'yaml gate' fence was read as ordinary text");
            Assert.Contains("gate", def.Error);
        }

        [Fact]
        public void U2_an_indented_step_fence_is_refused_in_v2()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Act",
                "  ```yaml step", "  id: act", "  ```", "", "Body."));
            Assert.True(def.Error != null, "an indented 'yaml step' fence was read as ordinary text");
            Assert.Contains("step", def.Error);
        }

        [Fact]
        public void U2_control_v1_still_reads_an_indented_fence_as_text()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "---", "## Step 1: Check", "Body.",
                " ```yaml gate", " strictness: hard", " ```"));
            Assert.Null(def.Error);
            Assert.Null(def.Steps[0].Gate);
        }

        // ---- U3: children attached to a key that has no children --------------------------------------

        [Fact]
        public void U3_an_indented_when_under_a_scalar_key_is_refused()
        {
            // `id: guarded` followed by an indented `when:` parses with When == null: the condition is
            // attached to `id` as children, and `id` does not read children, so the author's guard vanishes.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Act",
                "```yaml step", "id: guarded", "  when: date.dayOfMonth < 0", "```", "", "Body."));
            Assert.True(def.Error != null, "an indented 'when:' was attached to 'id:' and dropped");
        }

        [Fact]
        public void U3_an_indented_reserved_key_still_hits_its_reservation()
        {
            // The nastier half: indenting a RESERVED key smuggled it past the refusal that exists to teach
            // "not yet", so the author was told nothing at all.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Act",
                "```yaml step", "id: act", "  instructions: do the thing", "```", "", "Body."));
            Assert.True(def.Error != null, "an indented reserved 'instructions:' bypassed its reservation");
        }

        [Fact]
        public void U3_control_a_key_that_really_takes_children_still_does()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Act",
                "```yaml step", "id: each", "forEach:", "  in: [Sales, Product]", "  as: table", "```", "", "Body."));
            Assert.True(def.Error == null, "a legal forEach block was refused: " + def.Error);
            Assert.Equal("table", def.Steps[0].ForEach!.As);
        }

        // ---- U4: a repeated gate section overwrites the first -----------------------------------------

        [Theory]
        [InlineData("verify")]
        [InlineData("inputs")]
        public void U4_a_repeated_gate_section_is_refused_in_v2(string key)
        {
            // Two `verify:` blocks and only the second survives: the first hard check is deleted in silence.
            var rows = key == "verify"
                ? new[] { "  - kind: bpa_clean", key + ":", "  - kind: readiness_rescan" }
                : new[] { "  - name: a", "    question: A?", key + ":", "  - name: b", "    question: B?" };
            var def = WorkflowParser.Parse(Md(
                new[] { "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Check", "Body.",
                        "```yaml gate", "strictness: hard", key + ":" }
                    .Concat(rows).Concat(new[] { "```" }).ToArray()));
            Assert.True(def.Error != null, $"a repeated '{key}:' section silently replaced the first one");
            Assert.Contains(key, def.Error);
        }

        [Fact]
        public void U4_control_v1_keeps_last_wins_exactly_as_it_did()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "---", "## Step 1: Check", "Body.",
                "```yaml gate", "strictness: hard", "verify:", "  - kind: bpa_clean",
                "verify:", "  - kind: readiness_rescan", "```"));
            Assert.Null(def.Error);
            Assert.Equal(new[] { "readiness_rescan" }, def.Steps[0].Gate!.Verify.Select(v => v.Kind).ToArray());
        }

        // ---- U5: an inline `with:` map is discarded ---------------------------------------------------

        [Fact]
        public void U5_an_inline_with_map_is_read_not_dropped()
        {
            // `with: { targetMeasure: inputs.target }` used to parse to an EMPTY map, so the binding the
            // author wrote was gone; the hand reader's remedy was a refusal, because it could not read the
            // flow form at all. [T217] ends the family at the root: the flow form is ordinary YAML, the
            // library reads it, and the value must ARRIVE. Exit-pack twin: WorkflowV2YamlReaderExitTests.E1.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Hand off",
                "```yaml step", "id: hand-off", "call:", "  workflow: other",
                "  with: { targetMeasure: inputs.target }", "```", "", "Body."));
            Assert.True(def.Error == null, "a legal inline 'with:' map was refused: " + def.Error);
            Assert.Equal("inputs.target", def.Steps[0].Call!.With["targetMeasure"]);
        }

        [Fact]
        public void U5_control_the_block_form_of_with_still_parses()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Hand off",
                "```yaml step", "id: hand-off", "call:", "  workflow: other", "  with:",
                "    targetMeasure: inputs.target", "```", "", "Body."));
            Assert.True(def.Error == null, "a legal block-form with: was refused: " + def.Error);
            Assert.Equal("inputs.target", def.Steps[0].Call!.With["targetMeasure"]);
        }


        // ---- U7: the invariant itself (spec section 1.1, enforced rather than re-established) ----------

        [Fact]
        public void U7_a_rich_legal_v2_file_is_not_touched_by_the_invariant()
        {
            // The false-positive hunt, done deliberately rather than hoped for: comments (with and without
            // colons), blank lines, a slots block, a provenance block, a gate with both lists, and a step
            // fence with a loop. If the invariant fires on any of this, the invariant is the defect.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "kind: template", "title: T",
                "# a plain comment", "", "# a comment: with a colon in it",
                "tags: [finance]",
                "provenance:", "  derived_from: something", "  # a comment inside a block",
                "slots:", "  - name: region", "    question: Which region?", "    # a comment inside a slot",
                "---", "", "## Step 1: Ask", "", "Body about {{region}}.", "",
                "```yaml gate", "strictness: hard", "# a comment inside a gate",
                "inputs:", "  - name: approval", "    question: Approved?", "    type: verification",
                "verify:", "  - kind: bpa_clean", "```", "",
                "## Step 2: Repeat", "",
                "```yaml step", "id: each", "# a comment inside a step fence",
                "forEach:", "  in: [Sales, Product]", "  as: table", "```", "", "Body."));
            Assert.True(def.Error == null, "the invariant fired on a legal v2 file: " + def.Error);
            Assert.Equal(new[] { "region" }, def.Slots.Select(x => x.Name).ToArray());
            Assert.Equal("something", def.Provenance["derived_from"]);
            Assert.Equal(new[] { "bpa_clean" }, def.Steps[0].Gate!.Verify.Select(v => v.Kind).ToArray());
            Assert.Equal("table", def.Steps[1].ForEach!.As);
        }

        [Fact]
        public void U7_a_comment_is_a_comment_whether_or_not_it_contains_a_colon()
        {
            // Measured before the invariant was written and fixed as its precondition: `# note` was ignored
            // and `# note: x` was refused as an unknown key called '# note'. An exemption that only holds for
            // half the comments would have made the invariant dishonest on the other half.
            foreach (var comment in new[] { "# plain", "# with: a colon", "  # indented, with: a colon" })
            {
                var def = WorkflowParser.Parse(Md(
                    "---", "schemaVersion: 2", "name: t", "title: T", comment, "---", "## Step 1: Do", "Body."));
                Assert.True(def.Error == null, $"a comment line was refused [{comment}]: {def.Error}");
            }
        }

        [Fact]
        public void U7_v1_is_untouched_by_the_invariant()
        {
            // v1 absorbs unknown lines by design and that is frozen, and v1 never touches the YAML
            // library ([T217]). The same file that v2 refuses above loads clean here.
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "title: T", "  stray: line", "---", "## Step 1: Do", "Body."));
            Assert.Null(def.Error);
        }


        // ---- V1..V5: the second unscoped read -------------------------------------------------------

        [Fact]
        public void V1_a_duplicate_key_inside_a_list_item_is_refused()
        {
            // Found as the VALUE-drop class the old line ledger could not see; since [T217] the YAML
            // model builder refuses a duplicate key in every scope, this one included.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Check", "Body.",
                "```yaml gate", "verify:", "  - kind: bpa_clean", "    kind: readiness_rescan", "```"));
            Assert.True(def.Error != null, "a duplicate 'kind:' silently replaced the first value");
            Assert.Contains("kind", def.Error);
        }

        [Fact]
        public void V1_family_a_duplicate_key_is_refused_in_every_flat_map()
        {
            // The family: gate inputs, gate verifies and template slots are all the same flat-map reader.
            var slots = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "kind: template", "title: T",
                "slots:", "  - name: region", "    question: A?", "    question: B?",
                "---", "## Step 1: Do", "Body about {{region}}."));
            Assert.True(slots.Error != null, "a duplicate slot 'question:' silently replaced the first");
            var inputs = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Ask", "Body.",
                "```yaml gate", "inputs:", "  - name: a", "    question: A?", "    name: b", "```"));
            Assert.True(inputs.Error != null, "a duplicate input 'name:' silently replaced the first");
        }

        [Fact]
        public void V1_control_v1_keeps_last_wins()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "name: t", "---", "## Step 1: Check", "Body.",
                "```yaml gate", "verify:", "  - kind: bpa_clean", "    kind: readiness_rescan", "```"));
            Assert.Null(def.Error);
            Assert.Equal(new[] { "readiness_rescan" }, def.Steps[0].Gate!.Verify.Select(v => v.Kind).ToArray());
        }

        [Fact]
        public void V2_a_fence_before_the_first_step_is_refused()
        {
            // The family's eighth member: a RECOGNISED fence, thrown away whole because there is no step to
            // attach it to. The author wrote a hard gate and the workflow loads without it.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "```yaml gate", "strictness: hard", "verify:", "  - kind: bpa_clean", "```",
                "## Step 1: Do", "Body."));
            Assert.True(def.Error != null, "a gate fence before Step 1 was discarded");
            Assert.Contains("gate", def.Error);
        }

        [Fact]
        public void V2_control_ordinary_preamble_prose_is_still_allowed()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---",
                "Some words about this workflow.", "", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "preamble prose was refused: " + def.Error);
        }

        [Fact]
        public void V3_a_quoted_comma_does_not_split_an_inline_list()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "tags: [\"finance, monthly\"]", "---",
                "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "the file did not parse: " + def.Error);
            Assert.Equal(new[] { "finance, monthly" }, def.Tags);
        }

        [Fact]
        public void V3_control_an_unquoted_comma_still_splits()
        {
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "tags: [finance, monthly]", "---",
                "## Step 1: Do", "Body."));
            Assert.Null(def.Error);
            Assert.Equal(new[] { "finance", "monthly" }, def.Tags);
        }

        [Fact]
        public void V4_a_loop_variable_named_index_is_refused()
        {
            // `loop.index` is the fixed iteration counter. A loop bound `as: index` makes that name mean two
            // things, and nothing would say which one a condition read.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T", "---", "## Step 1: Repeat",
                "```yaml step", "id: each", "forEach:", "  in: [Sales]", "  as: index", "```", "", "Body."));
            Assert.True(def.Error != null, "a loop variable called 'index' shadowed the iteration counter");
            Assert.Contains("index", def.Error);
        }

        [Fact]
        public void V5_a_comment_does_not_end_a_frontmatter_block()
        {
            // The comment exemption was applied where lines are READ but not where block structure is
            // decided, so a comment at column zero dedented out of the block and the key beneath it became a
            // top-level key. A comment may not change what a file MEANS.
            var def = WorkflowParser.Parse(Md(
                "---", "schemaVersion: 2", "name: t", "title: T",
                "provenance:", "# note", "  derived_from: wfr-1", "---", "## Step 1: Do", "Body."));
            Assert.True(def.Error == null, "a comment inside a provenance block broke it: " + def.Error);
            Assert.Equal("wfr-1", def.Provenance["derived_from"]);
        }

        // ---- U6: a step-scope condition in a hand-edited binding -------------------------------------

        [Fact]
        public async Task U6_a_step_scope_condition_in_a_binding_is_reported_by_policy_lint()
        {
            // There is no write path for a binding condition, so this file can only be hand-edited, and the
            // condition parses cleanly: `inputs.approval.answered` is a perfectly readable fact that simply
            // has no value outside a run. The rule therefore never matches, the HARD binding never enforces,
            // and nothing said so. An activation rule with the same root is refused at its write path; this
            // is the same rule reached through the only door that has no write path.
            var (e, ws) = Make(new Pro());
            var dir = Path.Combine(ws, ".semanticus");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "workflow-settings.json"),
                "{\"bindings\":{\"create_measure\":[{\"when\":\"inputs.approval.answered\","
                + "\"require\":[\"verified-measure\"],\"mode\":\"hard\"}]}}");

            var policy = await e.GetWorkflowPolicyAsync();
            var text = JsonSerializer.Serialize(policy);
            Assert.Contains("inputs.approval.answered", text);
        }

        private static string InlineLoopMd(string name) => Md(
            "---", "schemaVersion: 2", "name: " + name, "title: T", "---",
            "## Step 1: Act",
            "```yaml step", "id: act", "forEach:", "  in: [Sales, Product]", "  as: table", "```", "", "Body.");

        private static string AdjacentSourceMd(string name) => Md(
            "---", "schemaVersion: 2", "name: " + name, "title: T", "---",
            "## Step 1: Choose", "Body.",
            "```yaml gate", "inputs:", "  - name: tables", "    question: Which tables?", "```",
            "```yaml step", "id: choose", "```",
            "## Step 2: Repeat",
            "```yaml step", "id: each", "forEach:", "  in: inputs.tables", "  as: table", "```", "", "Body.");

        private static string NonadjacentSourceMd(string name) => Md(
            "---", "schemaVersion: 2", "name: " + name, "title: T", "---",
            "## Step 1: Choose", "Body.",
            "```yaml gate", "inputs:", "  - name: tables", "    question: Which tables?", "```",
            "```yaml step", "id: choose", "```",
            "## Step 2: Between", "Ordinary work.",
            "```yaml step", "id: between", "```",
            "## Step 3: Repeat",
            "```yaml step", "id: each", "forEach:", "  in: inputs.tables", "  as: table", "```", "", "Body.");

        private static string SelfSourceMd(string name) => Md(
            "---", "schemaVersion: 2", "name: " + name, "title: T", "---",
            "## Step 1: Repeat", "Body.",
            "```yaml gate", "inputs:", "  - name: tables", "    question: Which tables?", "```",
            "```yaml step", "id: each", "forEach:", "  in: inputs.tables", "  as: table", "```");

        private static string CombinedForEachAndCallMd(string name, string sub) => Md(
            "---", "schemaVersion: 2", "name: " + name, "title: T", "---",
            "## Step 1: Hand off each",
            "```yaml step", "id: hand-off", "forEach:", "  in: [Sales, Product]", "  as: table",
            "call:", "  workflow: " + sub, "```", "", "Body.");

        private static string CallOnlyMd(string name, string sub) => Md(
            "---", "schemaVersion: 2", "name: " + name, "title: T", "---",
            "## Step 1: Hand off",
            "```yaml step", "id: hand-off", "call:", "  workflow: " + sub, "```", "", "Body.");

        private static string V7GoldenPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return Path.Combine(dir.FullName, "Semanticus.Tests", "goldens", "workflow-v7-no-loop-record.json");
        }

        private static async Task<string> SerializeNormalizedNoLoopV7Record()
        {
            var run = new WorkflowRunState("wfr-v7-noloop", new WorkflowDef
            {
                Name = "certificate-test",
                Title = "Certificate test",
                Strictness = "hard",
                Steps = new[] { new WorkflowStep
                {
                    Id = "step-1",
                    Number = 1,
                    Title = "Step 1",
                    Gate = new GateSpec
                    {
                        Inputs = Array.Empty<GateInput>(),
                        Verify = new[] { new VerifySpec { Kind = "workflow_admissible" } },
                    },
                } },
            }, null);
            run.CoverageSurface = new CoverageSurfaceLock
            {
                CurrentGrid = new[] { "'Product'[Category]", "'Date'[Year]" },
                CurrentOpenGrains = Array.Empty<string>(),
            };
            run.StartedUtc = "2026-01-02T03:04:05.0000000Z";
            run.ModelName = "Invented Sales Model";
            run.ModelFingerprint = "fixture-fingerprint";

            await WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>(),
                (spec, step, state, answers) => Task.FromResult(new VerifyResult
                {
                    Kind = spec.Kind, Status = "passed", Detail = "verified",
                }));

            Assert.Equal("completed", run.Status);
            run.FinishedUtc = "2026-01-02T03:05:06.0000000Z";
            foreach (var attempt in run.Results[0].VerifyHistory ?? new List<VerifyAttempt>())
                attempt.TimestampUtc = "2026-01-02T03:04:30.0000000Z";

            return JsonSerializer.Serialize(WorkflowRunner.BuildRunRecord(run));
        }

        private static void AssertUnexpanded(WorkflowRunView run, string currentId, int total)
        {
            Assert.NotNull(run);
            Assert.Equal("active", run.Status);
            Assert.Equal(currentId, run.CurrentStep.StepId);
            Assert.Equal(0, run.StepIndex);
            Assert.Equal(total, run.TotalSteps);
            Assert.True(run.TotalStepsProvisional);
            Assert.Equal(total, run.Steps.Length);
            Assert.All(run.Steps, s => Assert.DoesNotContain("#", s.StepId));
            Assert.Null(run.Frames);
            Assert.All(run.Steps, s => Assert.Null(s.PendingForEach));
        }

        private static async Task<WorkflowRunView> StartOnDoor(string door, string name, string md)
        {
            if (door == "rpc") return await StartRpc(name, md);
            var (e, _) = Make(new Pro());
            await e.SaveWorkflowAsync(name, md, "human");
            if (door == "mcp") return await McpTools.StartWorkflow(e, name);
            return await e.StartWorkflowAsync(name, "human");
        }

        private static async Task<WorkflowRunView> StartRpc(string name, string md)
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-drop-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            var pipe = "semanticus-drop-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var sessions = new SessionManager();
            var owner = new LocalEngine(sessions, new Pro(), ws);
            await owner.SaveWorkflowAsync(name, md, "human");
            using var server = new RpcServer(sessions, owner, pipe);
            using var cts = new CancellationTokenSource();
            var serverTask = server.RunAsync(cts.Token);
            RemoteEngine remote = null;
            try
            {
                remote = await RemoteEngine.ConnectAsync(pipe);
                return await remote.StartWorkflowAsync(name, "agent");
            }
            finally
            {
                remote?.Dispose();
                cts.Cancel();
                try { await serverTask; } catch { }
                sessions.Dispose();
            }
        }

        private static async Task AssertAdmittedOnThreeDoors(
            string prefix, Func<string, string> md, string currentId, int total, string[] ids)
        {
            var failures = new List<string>();
            foreach (var door in new[] { "local", "mcp", "rpc" })
            {
                try
                {
                    var name = prefix + door;
                    var run = await StartOnDoor(door, name, md(name));
                    AssertUnexpanded(run, currentId, total);
                    Assert.Equal(ids, run.Steps.Select(s => s.StepId).ToArray());
                }
                catch (Exception ex)
                {
                    failures.Add(door + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            Assert.True(failures.Count == 0, "public start must admit " + prefix.TrimEnd('-')
                + " on every door, unexpanded. " + string.Join(" | ", failures));
        }
    }
}
