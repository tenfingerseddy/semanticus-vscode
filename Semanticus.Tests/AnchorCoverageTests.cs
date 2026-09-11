using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class AnchorCoverageTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private const string Grid3 = "'Product'[Category], 'Date'[Year], 'Region'[Name]";
        private const string Grand = "{\"context\":{},\"expect\":1}";
        private const string Category = "{\"context\":{\"'Product'[Category]\":\"Bikes\"},\"axis\":[\"'Product'[Category]\"],\"expect\":1}";
        private const string Year = "{\"context\":{\"'Date'[Year]\":2024},\"expect\":1}";
        private const string Region = "{\"context\":{\"'Region'[Name]\":\"Pacific\"},\"expect\":1}";
        private const string Cross = "{\"context\":{\"'Product'[Category]\":\"Bikes\",\"'Date'[Year]\":2024,\"'Region'[Name]\":\"Pacific\"},\"expect\":1}";

        private static string AnchorSet(params string[] anchors) => "[" + string.Join(",", anchors) + "]";

        private static (WorkflowRunState Run, WorkflowStep Step, VerifySpec Spec) State()
        {
            var spec = new VerifySpec { Kind = "anchor_coverage", Anchors = "expectedValues" };
            var step = new WorkflowStep
            {
                Id = "step-2",
                Number = 2,
                Title = "Declare coverage",
                Gate = new GateSpec { Verify = new[] { spec } },
            };
            var def = new WorkflowDef { Name = "coverage", Title = "Coverage", Version = 7, Steps = new[] { step } };
            return (new WorkflowRunStore().Start(def, null), step, spec);
        }

        private static VerifyResult Evaluate(string anchors, string grid, string? openGrains = null,
            WorkflowRunState? run = null, WorkflowStep? step = null, VerifySpec? spec = null)
        {
            if (run == null || step == null || spec == null)
            {
                var state = State();
                run = state.Run;
                step = state.Step;
                spec = state.Spec;
            }
            var answers = new Dictionary<string, AnswerValue>(StringComparer.Ordinal)
            {
                ["expectedValues"] = new AnswerValue { Value = anchors },
                ["equivalenceGrid"] = new AnswerValue { Value = grid },
            };
            if (openGrains != null) answers["openGrains"] = new AnswerValue { Value = openGrains };
            return LocalEngine.WorkflowAnchorCoverage(spec!, step!, run!, answers);
        }

        private static string AllAnchors() => AnchorSet(Grand, Category, Year, Region, Cross);

        private static string Without(string grain)
        {
            var anchors = new List<string> { Grand, Category, Year, Region, Cross };
            switch (grain)
            {
                case "grand_total": anchors.Remove(Grand); break;
                case "axis:'Product'[Category]": anchors.Remove(Category); break;
                case "cross": anchors.Remove(Cross); break;
                default: throw new ArgumentOutOfRangeException(nameof(grain));
            }
            return AnchorSet(anchors.ToArray());
        }

        [Theory]
        [InlineData("grand_total")]
        [InlineData("axis:'Product'[Category]")]
        [InlineData("cross")]
        public void Coverage_XOR_matrix_enforces_covered_open_contradiction_and_uncovered_for_a_three_column_grid(string grain)
        {
            Assert.Equal("passed", Evaluate(AllAnchors(), Grid3).Status);

            var open = Evaluate(Without(grain), Grid3, grain);
            Assert.Equal("passed", open.Status);
            Assert.Contains("Open: [" + grain + "]", open.Detail);

            var contradiction = Evaluate(AllAnchors(), Grid3, grain);
            Assert.Equal("failed", contradiction.Status);
            Assert.Contains("cannot be both anchored and declared open", contradiction.Detail);
            Assert.Contains(grain, contradiction.Detail);

            var uncovered = Evaluate(Without(grain), Grid3);
            Assert.Equal("failed", uncovered.Status);
            Assert.Contains("anchor coverage is incomplete", uncovered.Detail);
            Assert.Contains(grain, uncovered.Detail);
        }

        [Fact]
        public void Uncovered_grains_are_batched_with_one_copy_paste_skeleton_per_grain()
        {
            var result = Evaluate("[]", Grid3);

            Assert.Equal("failed", result.Status);
            Assert.Contains("grand_total:", result.Detail);
            Assert.Contains("axis:'Product'[Category]:", result.Detail);
            Assert.Contains("axis:'Date'[Year]:", result.Detail);
            Assert.Contains("axis:'Region'[Name]:", result.Detail);
            Assert.Contains("cross:", result.Detail);
            Assert.Equal(5, result.Detail.Split("{\"context\":", StringSplitOptions.None).Length - 1);
            Assert.Equal(5, result.Detail.Split("\"expect\":\"<placeholder>\"", StringSplitOptions.None).Length - 1);
        }

        [Fact]
        public void Unknown_openGrains_id_is_refused_with_the_valid_ids_for_the_declared_grid()
        {
            var result = Evaluate("[]", Grid3, "axis:'Product'[Categry]");

            Assert.Equal("failed", result.Status);
            Assert.Contains("axis:'Product'[Categry]", result.Detail);
            Assert.Contains("Valid ids:", result.Detail);
            Assert.Contains("grand_total", result.Detail);
            Assert.Contains("axis:'Product'[Category]", result.Detail);
            Assert.Contains("axis:'Date'[Year]", result.Detail);
            Assert.Contains("axis:'Region'[Name]", result.Detail);
            Assert.Contains("cross", result.Detail);
            Assert.Contains("grid must list every display column the anchors and openGrains exercise", result.Detail);
        }

        [Fact]
        public void Anchor_column_absent_from_grid_is_refused_by_the_provenance_check()
        {
            var result = Evaluate(AnchorSet(Grand, Year), "'Product'[Category]");

            Assert.Equal("failed", result.Status);
            Assert.Contains("'Date'[Year]", result.Detail);
            Assert.Contains("absent from equivalenceGrid", result.Detail);
            Assert.Contains("grid must list every display column the anchors exercise", result.Detail);
        }

        [Fact]
        public void Coverage_surface_revision_accepts_only_receipted_supersets_and_refuses_removal()
        {
            var state = State();
            var initial = Evaluate(AnchorSet(Grand, Category), "'Product'[Category]", null, state.Run, state.Step, state.Spec);
            Assert.Equal("passed", initial.Status);
            Assert.Equal(new[] { "'Product'[Category]" }, state.Run.CoverageSurface.CurrentGrid);
            Assert.Empty(state.Run.CoverageSurfaceRevisions);

            const string twoColumnCross = "{\"context\":{\"'Product'[Category]\":\"Bikes\",\"'Date'[Year]\":2024},\"expect\":1}";
            var expandedAnchors = AnchorSet(Grand, Category, twoColumnCross);
            var expanded = Evaluate(expandedAnchors, "'Product'[Category], 'Date'[Year]", "axis:'Date'[Year]",
                state.Run, state.Step, state.Spec);

            Assert.Equal("passed", expanded.Status);
            var receipt = Assert.Single(state.Run.CoverageSurfaceRevisions);
            Assert.Equal(new[] { "'Date'[Year]" }, receipt.AddedGridColumns);
            Assert.Equal(new[] { "axis:'Date'[Year]" }, receipt.AddedOpenGrains);
            Assert.Equal("step-2", receipt.StepId);
            Assert.False(string.IsNullOrWhiteSpace(receipt.TimestampUtc));
            Assert.Equal(new[] { "'Product'[Category]", "'Date'[Year]" }, state.Run.CoverageSurface.CurrentGrid);

            var removal = Evaluate(expandedAnchors, "'Product'[Category]", null,
                state.Run, state.Step, state.Spec);
            Assert.Equal("failed", removal.Status);
            Assert.Contains("superset-only", removal.Missing);
            Assert.Contains("Start a new run to change the enforced surface", removal.Detail);
            Assert.Equal(new[] { "'Product'[Category]", "'Date'[Year]" }, state.Run.CoverageSurface.CurrentGrid);
            Assert.Single(state.Run.CoverageSurfaceRevisions);
        }

        [Fact]
        public async Task Coverage_surface_revision_receipt_is_durable_in_the_terminal_record_and_certificate()
        {
            WorkflowStep CoverageStep(int number) => new WorkflowStep
            {
                Id = "step-" + number,
                Number = number,
                Title = "Coverage " + number,
                Gate = new GateSpec
                {
                    Inputs = new[]
                    {
                        new GateInput { Name = "expectedValues", Type = "text", Required = "required" },
                        new GateInput { Name = "equivalenceGrid", Type = "text", Required = "required" },
                        new GateInput { Name = "openGrains", Type = "text", Required = "optional" },
                    },
                    Verify = new[] { new VerifySpec { Kind = "anchor_coverage", Anchors = "expectedValues" } },
                },
            };
            var run = new WorkflowRunStore().Start(new WorkflowDef
            {
                Name = "coverage-audit",
                Title = "Coverage audit",
                Version = 7,
                Strictness = "hard",
                Steps = new[] { CoverageStep(1), CoverageStep(2) },
            }, null);
            WorkflowVerifyExecutor executor = (spec, step, state, answers) => Task.FromResult(
                LocalEngine.WorkflowAnchorCoverage(spec, step, state, answers));

            await WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>
            {
                ["expectedValues"] = new AnswerValue { Value = AnchorSet(Grand, Category) },
                ["equivalenceGrid"] = new AnswerValue { Value = "'Product'[Category]" },
            }, executor);
            const string twoColumnCross = "{\"context\":{\"'Product'[Category]\":\"Bikes\",\"'Date'[Year]\":2024},\"expect\":1}";
            await WorkflowRunner.SubmitStepAsync(run, "step-2", new Dictionary<string, AnswerValue>
            {
                ["expectedValues"] = new AnswerValue { Value = AnchorSet(Grand, Category, twoColumnCross) },
                ["equivalenceGrid"] = new AnswerValue { Value = "'Product'[Category], 'Date'[Year]" },
                ["openGrains"] = new AnswerValue { Value = "axis:'Date'[Year]" },
            }, executor);

            Assert.Equal("completed", run.Status);
            var receipt = Assert.Single(run.CoverageSurfaceRevisions);
            var certifiedReceipt = Assert.Single(run.Certificate.CoverageSurfaceRevisions);
            Assert.Equal(receipt.TimestampUtc, certifiedReceipt.TimestampUtc);
            Assert.Equal(new[] { "'Date'[Year]" }, certifiedReceipt.AddedGridColumns);
            Assert.Equal(new[] { "axis:'Date'[Year]" }, certifiedReceipt.AddedOpenGrains);

            var terminalJson = JsonSerializer.Serialize(WorkflowRunner.BuildRunRecord(run));
            Assert.Contains("\"coverageSurfaceRevisions\":", terminalJson, StringComparison.Ordinal);
            Assert.Contains(receipt.TimestampUtc, terminalJson, StringComparison.Ordinal);
            Assert.Contains("AddedOpenGrains", terminalJson, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Anchor_coverage_runs_offline_at_step_two_without_a_target_object()
        {
            var workspace = Path.Combine(Path.GetTempPath(), "smx-anchor-coverage-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(workspace, ".semanticus", "workflows"));
            var markdown = @"---
name: offline-coverage
title: Offline coverage
version: 7
strictness: hard
---
## Step 1: State requirements
State them.

## Step 2: Declare the enforced surface
Declare it before authoring.
```yaml gate
inputs:
  - name: expectedValues
    question: ""Anchors?""
    type: text
    required: required
  - name: equivalenceGrid
    question: ""Grid?""
    type: text
    required: required
  - name: openGrains
    question: ""Open grains?""
    type: text
    required: optional
verify:
  - kind: anchor_coverage
    anchors: expectedValues
```
";
            File.WriteAllText(Path.Combine(workspace, ".semanticus", "workflows", "offline-coverage.md"), markdown);
            var sessions = new SessionManager();
            try
            {
                var engine = new LocalEngine(sessions, new Pro(), workspace);
                var run = await engine.StartWorkflowAsync("offline-coverage", "human");
                await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "human");
                var answers = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["expectedValues"] = AnchorSet(Grand, Category),
                    ["equivalenceGrid"] = "'Product'[Category]",
                });

                var completed = await engine.SubmitWorkflowStepAsync(run.RunId, "step-2", answers, "human");

                Assert.Equal("completed", completed.Status);
                var verify = completed.Steps[1].VerifyResults.Single();
                Assert.Equal("anchor_coverage", verify.Kind);
                Assert.Equal("passed", verify.Status);
            }
            finally
            {
                sessions.Dispose();
                Directory.Delete(workspace, true);
            }
        }

        [Fact]
        public async Task Version_six_definition_without_anchor_coverage_has_zero_new_submission_behavior()
        {
            var definition = WorkflowParser.Parse(@"---
name: v6-def-gate
title: V6 def gate
version: 6
strictness: hard
---
## Step 1: Legacy declaration
No coverage verify is declared.
```yaml gate
inputs:
  - name: equivalenceGrid
    question: ""Legacy grid text?""
    type: text
    required: required
  - name: openGrains
    question: ""Uninterpreted extension text?""
    type: text
    required: required
```
");
            Assert.Null(definition.Error);
            Assert.DoesNotContain(definition.Steps.SelectMany(s => s.Gate?.Verify ?? Array.Empty<VerifySpec>()), v => v.Kind == "anchor_coverage");
            var run = new WorkflowRunStore().Start(definition, null);

            await WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>
            {
                ["equivalenceGrid"] = new AnswerValue { Value = "not a column" },
                ["openGrains"] = new AnswerValue { Value = "not a shape" },
            }, null);

            Assert.Equal("completed", run.Status);
            Assert.Null(run.CoverageSurface);
            Assert.Empty(run.CoverageSurfaceRevisions);
        }

        [Fact]
        public void OpenShapesFrom_accepts_the_locked_openGrains_answer_as_its_source()
        {
            var spec = new VerifySpec { Kind = "dax_equivalence", OpenShapesFrom = "openGrains" };
            var answers = new Dictionary<string, AnswerValue>
            {
                ["openGrains"] = new AnswerValue { Value = "grand_total, axis:'Product'[Category]" },
            };
            var evaluated = new[] { "grand_total", "axis:'Product'[Category]", "cross" };

            var open = EquivalenceGate.ResolveOpenShapes(spec, answers, evaluated, out var error);

            Assert.Null(error);
            Assert.Equal(new[] { "grand_total", "axis:'Product'[Category]" }, open);
        }
    }
}
