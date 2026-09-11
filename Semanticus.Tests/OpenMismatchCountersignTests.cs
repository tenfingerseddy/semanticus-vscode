using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class OpenMismatchCountersignTests
    {
        private const string Axis = "axis:'Product'[Category]";
        private const string CellContext = "'Product'[Category]=Bikes";

        private static WorkflowRunState Run()
        {
            var def = new WorkflowDef
            {
                Name = "open-mismatch-test",
                Steps = Array.Empty<WorkflowStep>(),
            };
            return new WorkflowRunState("wfr-test", def, null)
            {
                CoverageSurface = new CoverageSurfaceLock
                {
                    CurrentGrid = new[] { "'Product'[Category]" },
                    CurrentOpenGrains = new[] { Axis },
                },
            };
        }

        private static ShapeVerifyResult Shape(string id, bool pinned, int mismatchCount,
            string witness = "10", string candidate = "11") => new ShapeVerifyResult
        {
            ShapeId = id,
            Pinned = pinned,
            RowsCompared = 1,
            MismatchCount = mismatchCount,
            Sample = mismatchCount == 0 ? Array.Empty<MismatchCell>() : new[]
            {
                new MismatchCell { Context = CellContext, ValueA = witness, ValueB = candidate },
            },
        };

        private static EquivalenceGate.GateOutcome Passed(params ShapeVerifyResult[] shapes) => new EquivalenceGate.GateOutcome
        {
            Status = "passed",
            Detail = "legacy equivalence detail",
            Shapes = shapes,
        };

        [Fact]
        public void Ledger_records_open_mismatch_cells_across_evaluations_while_pinned_failure_stays_failed()
        {
            var run = Run();
            var open = Shape(Axis, pinned: false, mismatchCount: 1);
            var pinned = Shape("grand_total", pinned: true, mismatchCount: 1);

            EquivalenceGate.RecordOpenMismatches(run, new[] { open, pinned });
            EquivalenceGate.RecordOpenMismatches(run, new[] { open, pinned });

            var ledger = run.ShapeMismatchLedger[Axis];
            Assert.Equal(Axis, ledger.ShapeId);
            Assert.Equal(2, ledger.TotalMismatchCount);
            Assert.Equal("DISPUTED", ledger.State);
            Assert.Single(ledger.Cells);
            var pinnedLedger = run.ShapeMismatchLedger["grand_total"];
            Assert.False(pinnedLedger.Open);
            Assert.Equal("PINNED-MISMATCH", pinnedLedger.State);
            Assert.Equal(2, pinnedLedger.TotalMismatchCount);

            var failed = new EquivalenceGate.GateOutcome { Status = "failed", Detail = "pinned mismatch" };
            Assert.Same(failed, EquivalenceGate.ApplyOpenMismatchCountersign(run, "step-6", failed, null));
            Assert.Equal("failed", failed.Status);
        }

        [Fact]
        public void Countersign_block_lists_shape_coordinate_and_both_candidate_and_witness_values()
        {
            var run = Run();
            var outcome = Passed(Shape(Axis, pinned: false, mismatchCount: 1, witness: "9", candidate: "10"));
            EquivalenceGate.RecordOpenMismatches(run, outcome.Shapes);

            var blocked = EquivalenceGate.ApplyOpenMismatchCountersign(run, "step-6", outcome, null);

            Assert.Equal("unavailable", blocked.Status);
            Assert.Contains(Axis, blocked.Detail);
            Assert.Contains(CellContext, blocked.Detail);
            Assert.Contains("candidate=10", blocked.Detail);
            Assert.Contains("witness=9", blocked.Detail);
            Assert.Contains("Exit A", blocked.Detail);
            Assert.Contains("Exit B", blocked.Detail);
        }

        [Fact]
        public void Countersign_exact_set_refuses_missing_and_extra_coordinates_with_the_diff()
        {
            var run = Run();
            var outcome = Passed(Shape(Axis, pinned: false, mismatchCount: 1));
            EquivalenceGate.RecordOpenMismatches(run, outcome.Shapes);
            var coordinate = Assert.Single(run.ShapeMismatchLedger[Axis].Cells).Coordinate;

            var missing = EquivalenceGate.ApplyOpenMismatchCountersign(run, "step-6", outcome,
                new AnswerValue { Value = JsonSerializer.Serialize(new { cells = Array.Empty<string>(), stated = "The requirement permits this visual exception." }) });
            Assert.Equal("unavailable", missing.Status);
            Assert.Contains("Missing: [" + coordinate + "]", missing.Detail);
            Assert.Contains("Extra: []", missing.Detail);

            var extra = EquivalenceGate.ApplyOpenMismatchCountersign(run, "step-6", outcome,
                new AnswerValue { Value = JsonSerializer.Serialize(new { cells = new[] { coordinate, "axis:'Product'[Category] | fake" }, stated = "The requirement permits this visual exception." }) });
            Assert.Equal("unavailable", extra.Status);
            Assert.Contains("Missing: []", extra.Detail);
            Assert.Contains("Extra: [axis:'Product'[Category] | fake]", extra.Detail);
            Assert.Empty(run.ShapeMismatchCountersigns);
        }

        [Fact]
        public void Exit_A_clean_re_evaluation_clears_the_shape_dispute()
        {
            var run = Run();
            EquivalenceGate.RecordOpenMismatches(run, new[] { Shape(Axis, pinned: false, mismatchCount: 1) });

            EquivalenceGate.RecordOpenMismatches(run, new[] { Shape(Axis, pinned: false, mismatchCount: 0) });
            var ledger = run.ShapeMismatchLedger[Axis];

            Assert.Equal("OPEN-CLEAN", ledger.State);
            Assert.Empty(ledger.Cells);
            Assert.Equal(1, ledger.TotalMismatchCount);
            var cleanOutcome = Passed();
            Assert.Same(cleanOutcome, EquivalenceGate.ApplyOpenMismatchCountersign(run, "step-6", cleanOutcome, null));
        }

        [Fact]
        public void Exit_B_records_the_verbatim_stated_sentence_and_changed_values_re_dispute()
        {
            var run = Run();
            var outcome = Passed(Shape(Axis, pinned: false, mismatchCount: 1, witness: "9", candidate: "10"));
            EquivalenceGate.RecordOpenMismatches(run, outcome.Shapes);
            var coordinate = Assert.Single(run.ShapeMismatchLedger[Axis].Cells).Coordinate;
            const string stated = "The requirement intentionally leaves this category open.";
            var answer = new AnswerValue { Value = JsonSerializer.Serialize(new { cells = new[] { coordinate }, stated }) };

            var accepted = EquivalenceGate.ApplyOpenMismatchCountersign(run, "step-6", outcome, answer);
            Assert.Equal("passed", accepted.Status);
            var receipt = Assert.Single(run.ShapeMismatchCountersigns);
            Assert.Equal(stated, receipt.Stated);
            Assert.Equal("10", receipt.CandidateValue);
            Assert.Equal("9", receipt.WitnessValue);
            Assert.Equal("COUNTERSIGNED", run.ShapeMismatchLedger[Axis].State);

            EquivalenceGate.RecordOpenMismatches(run,
                new[] { Shape(Axis, pinned: false, mismatchCount: 1, witness: "9", candidate: "10") });
            Assert.Equal("COUNTERSIGNED", run.ShapeMismatchLedger[Axis].State);
            Assert.Empty(run.ShapeMismatchLedger[Axis].Cells);

            EquivalenceGate.RecordOpenMismatches(run,
                new[] { Shape(Axis, pinned: false, mismatchCount: 1, witness: "9", candidate: "12") });
            var redisputed = Assert.Single(run.ShapeMismatchLedger[Axis].Cells);
            Assert.Equal("12", redisputed.CandidateValue);
            Assert.Equal("DISPUTED", run.ShapeMismatchLedger[Axis].State);
            Assert.Single(run.ShapeMismatchCountersigns);
        }

        [Fact]
        public void Literal_blank_string_countersign_does_not_cover_a_later_true_blank_coordinate()
        {
            ShapeVerifyResult TypedShape(string type) => new ShapeVerifyResult
            {
                ShapeId = Axis,
                Pinned = false,
                RowsCompared = 1,
                MismatchCount = 1,
                Sample = new[]
                {
                    new MismatchCell
                    {
                        Context = "'Product'[Category]=(blank)",
                        ContextParts = new[]
                        {
                            new MismatchContextPart { Name = "Product[Category]", Type = type, Value = "(blank)" },
                        },
                        ValueA = "10",
                        ValueB = "11",
                    },
                },
            };

            var run = Run();
            var literalOutcome = Passed(TypedShape("string"));
            EquivalenceGate.RecordOpenMismatches(run, literalOutcome.Shapes);
            var literalCoordinate = Assert.Single(run.ShapeMismatchLedger[Axis].Cells).Coordinate;
            var accepted = EquivalenceGate.ApplyOpenMismatchCountersign(run, "step-6", literalOutcome,
                new AnswerValue
                {
                    Value = JsonSerializer.Serialize(new
                    {
                        cells = new[] { literalCoordinate },
                        stated = "The requirement permits this literal member to remain open.",
                    }),
                });
            Assert.Equal("passed", accepted.Status);
            Assert.Equal("COUNTERSIGNED", run.ShapeMismatchLedger[Axis].State);

            EquivalenceGate.RecordOpenMismatches(run, new[] { TypedShape("blank") });

            var redisputed = Assert.Single(run.ShapeMismatchLedger[Axis].Cells);
            Assert.NotEqual(literalCoordinate, redisputed.Coordinate);
            Assert.Equal("DISPUTED", run.ShapeMismatchLedger[Axis].State);
            Assert.Single(run.ShapeMismatchCountersigns);
        }

        [Fact]
        public void Eleven_mismatches_cannot_be_countersigned_from_the_ten_stored_coordinates_but_clean_re_evaluation_clears_them()
        {
            var run = Run();
            var samples = Enumerable.Range(0, EquivalenceGate.MaxStoredMismatchCoordinatesPerShape)
                .Select(i => new MismatchCell
                {
                    Context = $"'Product'[Category]=C{i}",
                    ContextParts = new[] { new MismatchContextPart { Name = "Product[Category]", Value = $"C{i}" } },
                    ValueA = "10",
                    ValueB = "11",
                }).ToArray();
            var shape = new ShapeVerifyResult
            {
                ShapeId = Axis,
                Pinned = false,
                RowsCompared = 11,
                MismatchCount = 11,
                Sample = samples,
            };
            var outcome = Passed(shape);
            EquivalenceGate.RecordOpenMismatches(run, outcome.Shapes);
            var ledger = run.ShapeMismatchLedger[Axis];
            Assert.Equal(10, ledger.Cells.Length);

            var answer = new AnswerValue
            {
                Value = JsonSerializer.Serialize(new
                {
                    cells = ledger.Cells.Select(c => c.Coordinate).ToArray(),
                    stated = "The requirement leaves these sampled cells open.",
                }),
            };
            var blocked = EquivalenceGate.ApplyOpenMismatchCountersign(run, "step-6", outcome, answer);

            Assert.Equal("unavailable", blocked.Status);
            Assert.Contains("too many disagreements to acknowledge individually; fix a side instead", blocked.Detail);
            Assert.Contains("11 disagreements but only 10 stored coordinates", blocked.Detail);
            Assert.Empty(run.ShapeMismatchCountersigns);
            Assert.Equal("DISPUTED", ledger.State);

            EquivalenceGate.RecordOpenMismatches(run, new[] { Shape(Axis, pinned: false, mismatchCount: 0) });
            Assert.Equal("OPEN-CLEAN", ledger.State);
            Assert.Empty(ledger.Cells);
        }

        [Fact]
        public void Structured_coordinates_distinguish_rows_that_collided_under_delimiter_concatenation()
        {
            var run = Run();
            const string oldCollision = "A=x, B=y";
            var shape = new ShapeVerifyResult
            {
                ShapeId = "cross",
                Pinned = false,
                RowsCompared = 2,
                MismatchCount = 2,
                Sample = new[]
                {
                    new MismatchCell
                    {
                        Context = oldCollision,
                        ContextParts = new[] { new MismatchContextPart { Name = "A", Value = "x, B=y" } },
                        ValueA = "1", ValueB = "2",
                    },
                    new MismatchCell
                    {
                        Context = oldCollision,
                        ContextParts = new[]
                        {
                            new MismatchContextPart { Name = "A", Value = "x" },
                            new MismatchContextPart { Name = "B", Value = "y" },
                        },
                        ValueA = "1", ValueB = "2",
                    },
                },
            };

            EquivalenceGate.RecordOpenMismatches(run, new[] { shape });

            var coordinates = run.ShapeMismatchLedger["cross"].Cells.Select(c => c.Coordinate).ToArray();
            Assert.Equal(2, coordinates.Length);
            Assert.Equal(2, coordinates.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void Countersign_message_escapes_control_characters_and_caps_member_display()
        {
            var run = Run();
            var longTail = new string('X', 300);
            var raw = "North\nWest\t" + longTail;
            var shape = new ShapeVerifyResult
            {
                ShapeId = Axis,
                Pinned = false,
                RowsCompared = 1,
                MismatchCount = 1,
                Sample = new[]
                {
                    new MismatchCell
                    {
                        Context = "Region=" + raw,
                        ContextParts = new[] { new MismatchContextPart { Name = "Region", Value = raw } },
                        ValueA = "9", ValueB = "10",
                    },
                },
            };
            var outcome = Passed(shape);
            EquivalenceGate.RecordOpenMismatches(run, outcome.Shapes);

            var blocked = EquivalenceGate.ApplyOpenMismatchCountersign(run, "step-6", outcome, null);

            Assert.Contains("North\\nWest\\t", blocked.Detail);
            Assert.DoesNotContain("North\nWest", blocked.Detail);
            Assert.DoesNotContain(new string('X', 200), blocked.Detail);
        }

        [Fact]
        public void Definition_without_openMismatch_keeps_the_report_only_outcome_byte_for_byte()
        {
            var run = Run();
            var outcome = Passed(Shape(Axis, pinned: false, mismatchCount: 1));
            var before = JsonSerializer.Serialize(outcome);

            EquivalenceGate.RecordOpenMismatches(run, outcome.Shapes);

            Assert.Equal(before, JsonSerializer.Serialize(outcome));
            Assert.Equal("passed", outcome.Status);
            Assert.Equal("legacy equivalence detail", outcome.Detail);
        }

        [Fact]
        public void One_column_grid_retains_a_separate_logical_cross_shape_without_a_second_query()
        {
            var eq = new EquivalenceResult
            {
                Shapes = new[]
                {
                    new ShapeComparison { ShapeId = "grand_total", RowsCompared = 1 },
                    new ShapeComparison
                    {
                        ShapeId = Axis,
                        RowsCompared = 1,
                        MismatchCount = 1,
                        Mismatches = new[] { new EquivalenceMismatch { Context = CellContext, ValueA = "9", ValueB = "10" } },
                    },
                },
            };

            EquivalenceGate.PreserveLogicalCross(eq, 1);

            Assert.Equal(3, eq.Shapes.Length);
            var cross = Assert.Single(eq.Shapes, s => s.ShapeId == "cross");
            Assert.Equal(1, cross.MismatchCount);
            Assert.Equal(CellContext, Assert.Single(cross.Mismatches).Context);
        }

        [Fact]
        public void Parser_def_gates_openMismatch_and_requires_a_text_countersign_input()
        {
            const string markdown = @"---
name: countersign-parser
title: Countersign parser
---
## Step 1: Verify
```yaml gate
inputs:
  - name: witness
    question: ""Witness.""
    type: text
    required: required
verify:
  - kind: dax_equivalence
    probe: witness
    openMismatch: countersign
```
";
            // The verbatim literal inherits this source file's line endings (CRLF on a Windows checkout), so
            // normalize before anchoring a Replace on "\n" - otherwise the splice is a silent no-op there.
            var md = markdown.Replace("\r\n", "\n");
            var missing = WorkflowParser.Parse(md);
            Assert.Contains("openMismatch countersign 'countersign' does not name an input", missing.Error);

            var valid = WorkflowParser.Parse(md.Replace("verify:\n",
                "  - name: countersign\n    question: \"Countersign exact disputes.\"\n    type: text\n    required: optional\nverify:\n"));
            Assert.Null(valid.Error);
            Assert.Equal("countersign", valid.Steps[0].Gate.Verify[0].OpenMismatch);

            var answerOrDecline = WorkflowParser.Parse(md.Replace("verify:\n",
                "  - name: countersign\n    question: \"Countersign exact disputes.\"\n    type: text\n    required: answer-or-decline\nverify:\n"));
            Assert.Contains("must use 'required: optional'", answerOrDecline.Error);
            Assert.Contains("a decline is not a countersign", answerOrDecline.Error);
        }

        [Fact]
        public async Task X12_open_axis_disagreement_blocks_until_exact_countersign_and_audits_both_values()
        {
            const string markdown = @"---
name: x12-counterfactual
title: X12 counterfactual
strictness: hard
---
## Step 1: Finalize
Finalize only after resolving the open visual disagreement.
```yaml gate
inputs:
  - name: countersign
    question: ""Countersign every current open mismatch.""
    type: text
    required: optional
verify:
  - kind: dax_equivalence
    openMismatch: countersign
```
";
            var def = WorkflowParser.Parse(markdown);
            Assert.Null(def.Error);
            var run = new WorkflowRunStore().Start(def, null);
            run.CoverageSurface = new CoverageSurfaceLock
            {
                CurrentGrid = new[] { "'Product'[Category]" },
                CurrentOpenGrains = new[] { Axis },
            };

            async Task<VerifyResult> Execute(VerifySpec spec, WorkflowStep step, WorkflowRunState state,
                IReadOnlyDictionary<string, AnswerValue> answers)
            {
                await Task.Yield();
                var outcome = Passed(
                    Shape("grand_total", pinned: true, mismatchCount: 0),
                    Shape(Axis, pinned: false, mismatchCount: 1, witness: "90", candidate: "100"));
                EquivalenceGate.RecordOpenMismatches(state, outcome.Shapes);
                answers.TryGetValue("countersign", out var answer);
                outcome = EquivalenceGate.ApplyOpenMismatchCountersign(state, step.Id, outcome, answer);
                return new VerifyResult { Kind = spec.Kind, Status = outcome.Status, Missing = outcome.Missing, Detail = outcome.Detail };
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(
                run, "step-1", new Dictionary<string, AnswerValue>(), Execute));
            Assert.Equal("unavailable", run.Results[0].VerifyResults.Single().Status);
            var coordinate = Assert.Single(run.ShapeMismatchLedger[Axis].Cells).Coordinate;

            var countersign = JsonSerializer.Serialize(new
            {
                cells = new[] { coordinate },
                stated = "The requirement deliberately leaves this category open.",
            });
            await WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>
            {
                ["countersign"] = new AnswerValue { Value = countersign },
            }, Execute);

            Assert.Equal("completed", run.Status);
            var receipt = Assert.Single(run.ShapeMismatchCountersigns);
            Assert.Equal("100", receipt.CandidateValue);
            Assert.Equal("90", receipt.WitnessValue);
            Assert.Equal(coordinate, receipt.Coordinate);
            var viewReceipt = Assert.Single(WorkflowRunner.BuildView(run).ShapeMismatchCountersigns);
            Assert.Equal(receipt.Stated, viewReceipt.Stated);
            Assert.NotSame(receipt, viewReceipt);
        }
    }
}
