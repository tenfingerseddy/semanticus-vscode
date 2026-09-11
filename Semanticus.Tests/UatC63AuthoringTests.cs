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
    /// <summary>UAT C6.3: a person can author tests, pick a run scope, and answer workflow gates without the agent
    /// (D-087, D-094, D-090, D-102, D-152, D-088).</summary>
    public sealed class UatC63AuthoringTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private static (LocalEngine Engine, SessionManager Sessions, string Ws) Make()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-c63-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            var sessions = new SessionManager();
            return (new LocalEngine(sessions, new Pro(), ws), sessions, ws);
        }

        private static async Task OpenNamedModelAsync(LocalEngine engine, SessionManager sessions, string ws, string name)
        {
            await engine.CreateModelAsync(name, 1604);
            sessions.Current.SourcePath = Path.Combine(ws, name + ".bim");
        }

        // D-087: Expected total is a first-class suite kind a person can save.
        [Fact]
        public async Task Save_measureValue_kind_is_accepted_and_listed()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "Authoring");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Revenue", "190");
                var stored = await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "Revenue totals 190",
                    TargetRef = "measure:Revenue",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { ExpectedValue = "190", Provenance = "the 2024 close pack" }),
                }, "human");
                Assert.False(string.IsNullOrEmpty(stored.Id));
                Assert.Equal(TestKinds.MeasureValue, stored.Kind);
                Assert.Equal("human", stored.CreatedBy);
                var listed = await engine.ListTestDefinitionsAsync();
                Assert.Equal(stored.Id, Assert.Single(listed.Definitions).Id);
            }
            finally { Directory.Delete(ws, true); }
        }

        // D-087: Try it now must not write a saved test.
        [Fact]
        public async Task Try_test_returns_an_outcome_and_saves_nothing()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "TryOnce");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Revenue", "190");
                var outcome = await engine.TryTestAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "Revenue totals 190",
                    TargetRef = "measure:Revenue",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { ExpectedValue = "190" }),
                }, "human");
                Assert.NotNull(outcome);
                Assert.False(string.IsNullOrEmpty(outcome.Verdict.ToString()));
                Assert.Empty((await engine.ListTestDefinitionsAsync()).Definitions);
            }
            finally { Directory.Delete(ws, true); }
        }

        // D-087: both doors replace a question in one step.
        [Fact]
        public async Task Add_interview_question_with_id_replaces_the_same_question()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "InterviewEdit");
                var first = await engine.AddInterviewQuestionAsync(
                    "What were total sales?", "value", "EVALUATE ROW(\"v\", 1)", null, null, null, null,
                    "1", null, false, null, "user", "project", "human");
                var replaced = await engine.AddInterviewQuestionAsync(
                    "What were total sales in 2024?", "value", "EVALUATE ROW(\"v\", 1)", null, null, null, null,
                    "190", null, false, null, "user", "project", "human", first.Id);
                Assert.Equal(first.Id, replaced.Id);
                Assert.Equal("What were total sales in 2024?", replaced.Question);
                Assert.Equal("190", replaced.ExpectedValue);
                var pack = await engine.ListInterviewQuestionsAsync("project");
                Assert.Equal(first.Id, Assert.Single(pack.Questions).Id);
            }
            finally { Directory.Delete(ws, true); }
        }

        // D-094: a subset run is Partial, never recorded, and a full run still grades.
        [Fact]
        public async Task Scoped_run_is_partial_refuses_persist_and_full_run_still_grades()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "Scope");
                var a = await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.RowLevelAssertion, Title = "Check A", TargetRef = "role:A",
                }, "human");
                var b = await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.RowLevelAssertion, Title = "Check B", TargetRef = "role:B",
                }, "human");

                var partial = await engine.RunTestSuiteAsync(true, "human", new[] { a.Id }, null);
                Assert.True(partial.Scope.Partial);
                Assert.Equal("Partial", partial.Health.Grade);
                Assert.False(partial.Persisted);
                Assert.Contains("full run", partial.Note, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Ask the user", partial.Note ?? "", StringComparison.Ordinal);
                Assert.Contains(partial.Reconciles, o => o.DefId == a.Id);
                Assert.DoesNotContain(partial.Reconciles, o => o.DefId == b.Id);

                var full = await engine.RunTestSuiteAsync(false, "human");
                Assert.False(full.Scope?.Partial ?? false);
                Assert.NotEqual("Partial", full.Health.Grade);
                Assert.Contains(full.Reconciles, o => o.DefId == a.Id);
                Assert.Contains(full.Reconciles, o => o.DefId == b.Id);
            }
            finally { Directory.Delete(ws, true); }
        }

        // D-152 / D-102: engine refusals are structured; the person line has no JSON and does not ask the user.
        [Fact]
        public async Task Gate_refusal_is_structured_with_person_words_and_agent_words()
        {
            var def = WorkflowParser.Parse(@"---
name: gate-copy
title: Gate copy
strictness: hard
---
## Step 1: Ask
Ask.
```yaml gate
inputs:
  - name: finding
    question: ""What did you find?""
    type: text
    required: required
```
");
            Assert.Null(def.Error);
            var run = new WorkflowRunStore().Start(def, null);
            var unanswered = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>(), null));
            Assert.True(GateCopy.TryRead(unanswered, out var problems, out var display));
            var problem = Assert.Single(problems);
            Assert.Equal("finding", problem.Input);
            Assert.Equal(GateCopy.Unanswered, problem.Kind);
            Assert.Contains("Ask the user", unanswered.Message, StringComparison.Ordinal);
            Assert.Equal("This question still needs an answer.", problem.DisplayMessage);
            Assert.DoesNotContain("Ask the user", display, StringComparison.Ordinal);
            Assert.DoesNotContain("{\"declined\"", display, StringComparison.Ordinal);
            Assert.DoesNotContain("step-1", display, StringComparison.Ordinal);

            var required = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WorkflowRunner.SubmitStepAsync(run, "step-1",
                    new Dictionary<string, AnswerValue> { ["finding"] = new AnswerValue { Declined = true, DeclineReason = "not sure" } }, null));
            Assert.True(GateCopy.TryRead(required, out var declinedProblems, out _));
            Assert.Equal(GateCopy.DeclinedButRequired, Assert.Single(declinedProblems).Kind);
            Assert.Equal("This one cannot be declined. Type an answer.", declinedProblems[0].DisplayMessage);
            Assert.Contains("may not be declined", required.Message, StringComparison.Ordinal);
        }

        // D-090: planItem is a real gate type; Safe rename step 2 uses it.
        [Fact]
        public void Plan_item_input_type_parses_and_safe_rename_uses_it()
        {
            var parsed = WorkflowParser.Parse(@"---
name: plan-pick
title: Plan pick
---
## Step 1: Stage
Stage it.
```yaml gate
inputs:
  - name: planItemId
    question: ""Which staged rename?""
    type: planItem
    required: required
```
");
            Assert.Null(parsed.Error);
            Assert.Equal("planItem", parsed.Steps[0].Gate.Inputs[0].Type);

            var stock = File.ReadAllText(FindRepoFile("Semanticus.Engine/workflows/governed-rename.md"));
            var def = WorkflowParser.Parse(stock);
            Assert.Null(def.Error);
            var step2 = def.Steps.Single(s => s.Id == "step-2");
            var planInput = step2.Gate.Inputs.Single(i => i.Name == "planItemId");
            Assert.Equal("planItem", planInput.Type);
            Assert.Equal("required", planInput.Required);
        }

        // D-088: a parent that expects returns from a gate-off child is warned at admission.
        [Fact]
        public async Task Check_workflow_warns_when_a_hand_off_expects_values_from_a_gate_off_child()
        {
            var (engine, _, ws) = Make();
            try
            {
                await engine.SaveWorkflowAsync("check-region-totals", @"---
schemaVersion: 2
name: check-region-totals
title: Check region totals
strictness: off
---
## Step 1: Skip
Nothing is asked.
```yaml step
id: skip
```
```yaml gate
inputs:
  - name: regionTotal
    question: ""What did this region return?""
    type: text
    required: optional
```
", "human");
                await engine.SaveWorkflowAsync("report-regions", @"---
schemaVersion: 2
name: report-regions
title: Report regions
strictness: hard
---
## Step 1: Hand off
Collect the region.
```yaml step
id: handoff
call:
  workflow: check-region-totals
  returns: [regionTotal]
```

## Step 2: Report
Report what the child returned for each region.
```yaml step
id: report
```
```yaml gate
inputs:
  - name: summary
    question: ""Report what the child returned for each region.""
    type: text
    required: required
```
", "human");
                var report = await engine.CheckWorkflowAsync("report-regions");
                Assert.Contains(report.Findings, f =>
                    f.Severity == "warn"
                    && f.Message.Contains("Check region totals", StringComparison.Ordinal)
                    && f.Message.Contains("gate off", StringComparison.Ordinal)
                    && !f.Message.Contains("Ask the user", StringComparison.Ordinal));
            }
            finally { Directory.Delete(ws, true); }
        }

        // D-088: after a gate-off child finishes, the frame says why nothing came back.
        [Fact]
        public async Task Call_frame_names_gate_off_when_nothing_is_handed_back()
        {
            var callee = WorkflowParser.Parse(@"---
schemaVersion: 2
name: check-region-totals
title: Check region totals
strictness: off
---
## Step 1: Skip
Nothing is asked.
```yaml step
id: skip
```
```yaml gate
inputs:
  - name: regionTotal
    question: ""What did this region return?""
    type: text
    required: optional
```
");
            var caller = WorkflowParser.Parse(@"---
schemaVersion: 2
name: report-regions
title: Report regions
strictness: hard
---
## Step 1: Hand off
Collect the region.
```yaml step
id: handoff
call:
  workflow: check-region-totals
  returns: [regionTotal]
```

## Step 2: Report
Report what came back.
```yaml step
id: report
```
```yaml gate
inputs:
  - name: summary
    question: ""Report what each region returned.""
    type: text
    required: optional
```
");
            Assert.Null(callee.Error);
            Assert.Null(caller.Error);
            var run = WorkflowOwnerFixtures.ClosureRun("wfr-c63-handoff", null, (caller, null), (callee, null));
            WorkflowRunner.InitializeCalls(run);
            var view = WorkflowRunner.BuildView(run);
            Assert.NotNull(view.CurrentStep.HandOff);
            Assert.True(view.CurrentStep.HandOff.GateOff);
            Assert.Contains("Check region totals", view.CurrentStep.HandOff.CalleeTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("Ask the user", view.CurrentStep.HandOff.CalleeTitle ?? "", StringComparison.Ordinal);

            await WorkflowRunner.SubmitStepAsync(run, view.CurrentStep.StepId, new Dictionary<string, AnswerValue>(), null);
            for (var i = 0; i < 8 && run.Status == "active"; i++)
            {
                var current = WorkflowRunner.BuildView(run).CurrentStep;
                if (current == null || current.StepId == "report") break;
                await WorkflowRunner.SubmitStepAsync(run, current.StepId, new Dictionary<string, AnswerValue>(), null);
            }
            var after = WorkflowRunner.BuildView(run);
            var call = Assert.Single(after.Frames ?? Array.Empty<WorkflowRunFrameView>(), f => f.Kind == "call");
            Assert.Equal("Handed back: nothing. Its gate was off.", call.ReturnNote);
        }

        // D-090: LocalEngine human-origin refusal uses person words.
        [Fact]
        public async Task Human_submit_of_an_unanswered_required_gate_uses_person_words()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "HumanGate");
                await engine.SaveWorkflowAsync("need-an-answer", @"---
name: need-an-answer
title: Need an answer
strictness: hard
---
## Step 1: Ask
Ask.
```yaml gate
inputs:
  - name: finding
    question: ""What did you find?""
    type: text
    required: required
```
", "human");
                var run = await engine.StartWorkflowAsync("need-an-answer", "human");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.SubmitWorkflowStepAsync(run.RunId, run.CurrentStep.StepId, "{}", "human"));
                Assert.DoesNotContain("Ask the user", ex.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("{\"declined\"", ex.Message, StringComparison.Ordinal);
                Assert.Contains("still needs an answer", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally { Directory.Delete(ws, true); }
        }

        private static string FindRepoFile(string relative)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException(relative);
        }
    }
}
