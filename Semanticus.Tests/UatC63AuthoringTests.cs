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
                // 1.2.0: a part-run is graded on what it ran, and says what the letter covers, instead of
                // reporting the word "Partial" and telling the person nothing about the checks they asked for.
                Assert.NotEqual("Partial", partial.Health.Grade);
                Assert.Equal("the 1 check you picked", partial.Scope.GradeCovers);
                Assert.Equal(new[] { "saved checks" }, partial.Scope.Ran);
                Assert.Contains("only the part you ran", partial.Note, StringComparison.OrdinalIgnoreCase);
                Assert.False(partial.Persisted);
                Assert.Contains("full run", partial.Note, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Ask the user", partial.Note ?? "", StringComparison.Ordinal);
                Assert.Contains(partial.Reconciles, o => o.DefId == a.Id);
                Assert.DoesNotContain(partial.Reconciles, o => o.DefId == b.Id);

                var full = await engine.RunTestSuiteAsync(false, "human");
                Assert.False(full.Scope?.Partial ?? false);
                Assert.Null(full.Scope.GradeCovers);
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

        // UX11 (1): a check saved from a FILTERED visual keeps the filter lines, and the runner narrows the DAX
        // side with them. Without this a calculation filtered to one year silently became a check over the whole
        // model — a wrong check that reads as a right one. Asserted on the DAX the runner builds, not a live query.
        [Fact]
        public async Task Saved_sql_check_keeps_the_visual_filters_and_narrows_the_dax()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "FilteredCheck");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Revenue", "190");
                var stored = await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureReconcile,
                    Title = "Revenue matches the warehouse for 2026",
                    TargetRef = "measure:Sales/Revenue",
                    ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                    {
                        MeasureRef = "measure:Sales/Revenue",
                        GroupBy = new[] { "'Date'[Year]" },
                        Sql = "select year, sum(amount) from fact where year = 2026 group by year",
                        BlankPolicy = "zero",
                        FilterDax = "'Date'[Year] = 2026",
                    }),
                }, "human");

                var listed = await engine.ListTestDefinitionsAsync();
                var back = TestSuiteStore.Deserialize<ReconcileRequest>(
                    Assert.Single(listed.Definitions, d => d.Id == stored.Id).ParamsJson);
                Assert.Equal("'Date'[Year] = 2026", back.FilterDax);

                var grouped = LocalEngine.BuildReconcileDaxQuery(back, "Revenue", new[] { "'Date'[Year]" });
                Assert.Contains("CALCULATETABLE(", grouped, StringComparison.Ordinal);
                Assert.Contains("'Date'[Year] = 2026", grouped, StringComparison.Ordinal);

                var total = LocalEngine.BuildReconcileDaxQuery(back, "Revenue", Array.Empty<string>());
                Assert.Contains("CALCULATE([Revenue], 'Date'[Year] = 2026)", total, StringComparison.Ordinal);

                // No filter must leave the query exactly as it was: the narrowing is opt-in, never ambient.
                var unfiltered = LocalEngine.BuildReconcileDaxQuery(
                    new ReconcileRequest { MeasureRef = "measure:Sales/Revenue" }, "Revenue", new[] { "'Date'[Year]" });
                Assert.DoesNotContain("CALCULATETABLE", unfiltered, StringComparison.Ordinal);
            }
            finally { Directory.Delete(ws, true); }
        }

        // UX11 (2): an EDITED formula can be saved as its own check. The definition holds the expression and the
        // runner asks THAT, not the measure's saved formula.
        [Fact]
        public async Task Saved_expression_test_holds_the_expression_and_the_runner_asks_it()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "EditedFormula");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Revenue", "190");
                const string edited = "CALCULATE([Revenue], 'Date'[Year] = 2026)";
                var stored = await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "The edited Revenue totals 190",
                    TargetRef = "measure:Sales/Revenue",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest
                    {
                        MeasureRef = "measure:Sales/Revenue", ExpectedValue = "190", ExpressionDax = edited,
                    }),
                }, "human");

                var listed = await engine.ListTestDefinitionsAsync();
                var back = TestSuiteStore.Deserialize<MeasureValueRequest>(
                    Assert.Single(listed.Definitions, d => d.Id == stored.Id).ParamsJson);
                Assert.Equal(edited, back.ExpressionDax);

                // The SUITE run is the real path, not just a one-off try: the saved definition must ask the
                // edited expression when the whole suite runs.
                var run = await engine.RunTestSuiteAsync();
                var outcome = Assert.Single(run.Reconciles, o => o.DefId == stored.Id);
                Assert.Contains(edited, outcome.Dax, StringComparison.Ordinal);

                // The measure as saved stays the default shape when no expression was supplied.
                Assert.Equal("EVALUATE ROW(\"v\", [Revenue])", LocalEngine.BuildMeasureValueDax("Revenue", null, null, out _));
                // A complete query is the author's own, so it runs as written when the check has no filter of its
                // own. A filter is NOT already inside it: Astra proved the saved filter was silently dropped
                // (2026-09-14), so the query is scoped from outside instead.
                Assert.Equal("EVALUATE ROW(\"x\", 1)",
                    LocalEngine.BuildMeasureValueDax("Revenue", "EVALUATE ROW(\"x\", 1)", null, out _));
                Assert.Equal("EVALUATE CALCULATETABLE(ROW(\"x\", 1), 'Date'[Year] = 2026)",
                    LocalEngine.BuildMeasureValueDax("Revenue", "EVALUATE ROW(\"x\", 1)", "'Date'[Year] = 2026", out _));
            }
            finally { Directory.Delete(ws, true); }
        }

        // UX11 (3): the Tests-and-queries connection a check was AUTHORED against persists and is reported on the
        // run. A record, never a pin — the run still uses whatever is current.
        [Fact]
        public async Task Authored_connection_persists_and_is_reported_on_the_run()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "AuthoredAgainst");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Revenue", "190");
                var stored = await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "Revenue totals 190",
                    TargetRef = "measure:Sales/Revenue",
                    AuthoredAgainst = "live:abc12345",
                    AuthoredAgainstLabel = "Sales DW on powerbi://api.powerbi.com/v1.0/myorg/Finance",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest { MeasureRef = "measure:Sales/Revenue", ExpectedValue = "190" }),
                }, "human");
                Assert.Equal("live:abc12345", stored.AuthoredAgainst);

                var listed = await engine.ListTestDefinitionsAsync();
                var reloaded = Assert.Single(listed.Definitions, d => d.Id == stored.Id);
                Assert.Equal("live:abc12345", reloaded.AuthoredAgainst);
                Assert.Equal("Sales DW on powerbi://api.powerbi.com/v1.0/myorg/Finance", reloaded.AuthoredAgainstLabel);

                var run = await engine.RunTestSuiteAsync();
                var outcome = Assert.Single(run.Reconciles, o => o.DefId == stored.Id);
                Assert.Equal("live:abc12345", outcome.AuthoredAgainst);
                Assert.Equal("Sales DW on powerbi://api.powerbi.com/v1.0/myorg/Finance", outcome.AuthoredAgainstLabel);
                // Nothing is connected in this test, so the run reports no target rather than echoing the record.
                Assert.Null(outcome.RanAgainstLabel);
            }
            finally { Directory.Delete(ws, true); }
        }

        // A trusted-answer check asks ONE number, so a result that is not a single cell has no verdict to give.
        // Astra's spot review (2026-09-14) saved a grouped visual query as one of these: the run read the FIRST
        // CELL of EVALUATE SUMMARIZECOLUMNS('Date'[Year], "Total Sales", [Total Sales]), which is the YEAR, and
        // compared it with the trusted number 2024. It matched, so a check that never asked the measure read as
        // a pass. Judging a table by its first cell is the defect; the shape is what must be refused.
        [Fact]
        public async Task MeasureValue_over_a_two_column_table_is_not_checked_even_when_the_first_cell_matches()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "ShapeGate");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Total Sales", "4321");
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-shape", "ShapeGate", _ => new ResultSet
                {
                    Columns = new[] { new ColumnDef { Name = "Date[Year]" }, new ColumnDef { Name = "Total Sales" } },
                    Rows = new[] { new object[] { 2024, 4321m } },
                    RowCount = 1,
                }));
                var outcome = await engine.TryTestAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "Total Sales totals 2024",
                    TargetRef = "measure:Sales/Total Sales",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest
                    {
                        MeasureRef = "measure:Sales/Total Sales",
                        ExpectedValue = "2024",
                        ExpressionDax = "EVALUATE SUMMARIZECOLUMNS('Date'[Year], \"Total Sales\", [Total Sales])",
                    }),
                }, "human");
                Assert.NotEqual(Verdict.Pass, outcome.Verdict);
                Assert.Equal(Verdict.NotVerifiable, outcome.Verdict);
                Assert.Contains("1 row by 2 columns", outcome.Message);
                Assert.Contains("one number", outcome.Message);
                // No first-cell evidence may be left behind either: a Grand total row carrying 2024 would read as
                // a checked answer in the saved-tests list even with the verdict withheld.
                Assert.True(outcome.Rows == null || outcome.Rows.Length == 0);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // More rows is the same defect wearing a different hat, and the shape gate must not swallow the honest
        // single-cell case it exists to protect.
        [Fact]
        public async Task MeasureValue_judges_a_single_cell_and_refuses_a_many_row_table()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "ShapeGate2");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Total Sales", "4321");
                var shape = "single";
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-shape-2", "ShapeGate2", _ => shape == "single"
                    ? new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "v" } },
                        Rows = new[] { new object[] { 4321m } },
                        RowCount = 1,
                    }
                    : new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "Product[Name]" } },
                        Rows = new[] { new object[] { 4321m }, new object[] { 99m }, new object[] { 7m } },
                        RowCount = 3,
                    }));
                var def = new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "Total Sales totals 4321",
                    TargetRef = "measure:Sales/Total Sales",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest
                    {
                        MeasureRef = "measure:Sales/Total Sales", ExpectedValue = "4321",
                    }),
                };
                var single = await engine.TryTestAsync(def, "human");
                Assert.Equal(Verdict.Pass, single.Verdict);

                shape = "many";
                var many = await engine.TryTestAsync(def, "human");
                Assert.Equal(Verdict.NotVerifiable, many.Verdict);
                Assert.Contains("3 rows by 1 column", many.Message);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }


        // Astra's re-check (2026-09-14): a saved check carried BOTH a complete EVALUATE query and a separate
        // 'Date'[Year] = 2024 filter, and the engine ran the query verbatim. The year filter was silently
        // dropped, so the check could Pass or Fail on the wrong scope. A single-cell query hides it best,
        // because the shape gate has nothing to object to. The filter must be applied around the query.
        [Fact]
        public async Task MeasureValue_applies_a_saved_filter_around_a_complete_single_cell_query()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "FilterScope");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Total Sales", "4321");
                string asked = null;
                // The fake stands in for the model: 4321 only inside 2024, 999 over all years. A check that drops
                // the filter therefore reads 999 and can never report the filtered number by accident.
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-filter", "FilterScope", q =>
                {
                    asked = q;
                    var scoped = q.Contains("'Date'[Year] = 2024", StringComparison.Ordinal);
                    return new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "v" } },
                        Rows = new[] { new object[] { scoped ? 4321m : 999m } },
                        RowCount = 1,
                    };
                }));
                var outcome = await engine.TryTestAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "Total Sales in 2024 totals 4321",
                    TargetRef = "measure:Sales/Total Sales",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest
                    {
                        MeasureRef = "measure:Sales/Total Sales",
                        ExpectedValue = "4321",
                        ExpressionDax = "EVALUATE ROW(\"v\", [Total Sales] + 1)",
                        FilterDax = "'Date'[Year] = 2024",
                    }),
                }, "human");
                Assert.NotNull(asked);
                Assert.Contains("CALCULATETABLE", asked, StringComparison.Ordinal);
                Assert.Contains("'Date'[Year] = 2024", asked, StringComparison.Ordinal);
                Assert.Contains("ROW(\"v\", [Total Sales] + 1)", asked, StringComparison.Ordinal);
                Assert.Equal(asked, outcome.Dax);
                Assert.Equal(Verdict.Pass, outcome.Verdict);

                // The same rule, provable without a connection: the query goes inside CALCULATETABLE, never verbatim.
                Assert.Equal("EVALUATE CALCULATETABLE(ROW(\"v\", [Total Sales] + 1), 'Date'[Year] = 2024)",
                    LocalEngine.BuildMeasureValueDax("Total Sales", "EVALUATE ROW(\"v\", [Total Sales] + 1)", "'Date'[Year] = 2024", out var refusal));
                Assert.Null(refusal);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // Some queries cannot be scoped from outside: a DEFINE block, a second EVALUATE, a trailing ORDER BY or
        // START AT. Wrapping those is either invalid DAX or a different question. Refuse them in plain words
        // BEFORE any verdict, and before the query is sent, rather than judging the wrong scope.
        [Fact]
        public async Task MeasureValue_refuses_a_query_it_cannot_scope_and_never_asks_it()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "FilterRefuse");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Total Sales", "4321");
                var asked = 0;
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-refuse", "FilterRefuse", _ =>
                {
                    asked++;
                    return new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "v" } },
                        Rows = new[] { new object[] { 4321m } },
                        RowCount = 1,
                    };
                }));
                var define = await engine.TryTestAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "Total Sales totals 4321",
                    TargetRef = "measure:Sales/Total Sales",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest
                    {
                        MeasureRef = "measure:Sales/Total Sales",
                        ExpectedValue = "4321",
                        ExpressionDax = "DEFINE MEASURE 'Sales'[X] = [Total Sales] * 2 EVALUATE ROW(\"v\", [X])",
                        FilterDax = "'Date'[Year] = 2024",
                    }),
                }, "human");
                Assert.Equal(Verdict.NotVerifiable, define.Verdict);
                Assert.Contains("DEFINE", define.Message, StringComparison.Ordinal);
                Assert.Contains("filter", define.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, asked);
                Assert.True(define.Rows == null || define.Rows.Length == 0);

                var ordered = await engine.TryTestAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "Total Sales totals 4321",
                    TargetRef = "measure:Sales/Total Sales",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest
                    {
                        MeasureRef = "measure:Sales/Total Sales",
                        ExpectedValue = "4321",
                        ExpressionDax = "EVALUATE ROW(\"v\", [Total Sales]) ORDER BY [v]",
                        FilterDax = "'Date'[Year] = 2024",
                    }),
                }, "human");
                Assert.Equal(Verdict.NotVerifiable, ordered.Verdict);
                Assert.Contains("ORDER BY", ordered.Message, StringComparison.Ordinal);
                Assert.Equal(0, asked);

                // A DEFINE block with no filter was never sendable either: the old builder wrapped it in
                // ROW(), which is not valid DAX. Refuse it with a reason instead of a server syntax error.
                LocalEngine.BuildMeasureValueDax("Total Sales", "DEFINE MEASURE 'Sales'[X] = 1 EVALUATE ROW(\"v\", [X])", null, out var noFilter);
                Assert.NotNull(noFilter);
                Assert.Contains("DEFINE", noFilter, StringComparison.Ordinal);

                // A query may open with a comment. The body is cut at the keyword's own position, so chopping a
                // fixed eight characters off the comment cannot mangle it.
                Assert.Equal("EVALUATE CALCULATETABLE(ROW(\"v\", [Total Sales]), 'Date'[Year] = 2024)",
                    LocalEngine.BuildMeasureValueDax("Total Sales", "// the 2024 total" + ((char)10) + "EVALUATE ROW(\"v\", [Total Sales])", "'Date'[Year] = 2024", out var commented));
                Assert.Null(commented);

                // The word EVALUATE sitting inside a name or a string is text, not a second query.
                Assert.Equal("EVALUATE CALCULATETABLE(ROW(\"EVALUATE\", [Total Sales]), 'Date'[Year] = 2024)",
                    LocalEngine.BuildMeasureValueDax("Total Sales", "EVALUATE ROW(\"EVALUATE\", [Total Sales])", "'Date'[Year] = 2024", out var literal));
                Assert.Null(literal);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // The scalar and saved-measure paths are what the filter always worked on. The scoping fix must not
        // move them: both still narrow with CALCULATE, through the one path both doors run.
        [Fact]
        public async Task MeasureValue_still_filters_a_scalar_expression_and_the_saved_measure()
        {
            var (engine, sessions, ws) = Make();
            try
            {
                await OpenNamedModelAsync(engine, sessions, ws, "FilterScalar");
                var table = await McpTools.CreateTable(engine, "Sales");
                await McpTools.CreateMeasure(engine, table, "Total Sales", "4321");
                string asked = null;
                engine.SetLiveConnectionForTest(LiveConnection.ForTest("xmla", "endpoint-scalar", "FilterScalar", q =>
                {
                    asked = q;
                    // The simple Column/Value filter quotes its value, so scope is read from the column, not the
                    // whole text: both filter shapes must narrow, and neither may reach the model unscoped.
                    var scoped = q.Contains("'Date'[Year] = ", StringComparison.Ordinal);
                    return new ResultSet
                    {
                        Columns = new[] { new ColumnDef { Name = "v" } },
                        Rows = new[] { new object[] { scoped ? 4321m : 999m } },
                        RowCount = 1,
                    };
                }));
                var scalar = await engine.TryTestAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "Total Sales in 2024 totals 4321",
                    TargetRef = "measure:Sales/Total Sales",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest
                    {
                        MeasureRef = "measure:Sales/Total Sales",
                        ExpectedValue = "4321",
                        ExpressionDax = "[Total Sales] + 1",
                        FilterDax = "'Date'[Year] = 2024",
                    }),
                }, "human");
                Assert.Equal("EVALUATE ROW(\"v\", CALCULATE([Total Sales] + 1, 'Date'[Year] = 2024))", asked);
                Assert.Equal(Verdict.Pass, scalar.Verdict);

                var saved = await engine.TryTestAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureValue,
                    Title = "Total Sales in 2024 totals 4321",
                    TargetRef = "measure:Sales/Total Sales",
                    ParamsJson = TestSuiteStore.Serialize(new MeasureValueRequest
                    {
                        MeasureRef = "measure:Sales/Total Sales",
                        ExpectedValue = "4321",
                        FilterColumn = "'Date'[Year]",
                        FilterValue = "2024",
                    }),
                }, "human");
                Assert.Equal("EVALUATE ROW(\"v\", CALCULATE([Total Sales], 'Date'[Year] = \"2024\"))", asked);
                Assert.Equal(Verdict.Pass, saved.Verdict);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
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
