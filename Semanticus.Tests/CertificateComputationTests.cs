using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class CertificateComputationTests
    {
        private static WorkflowStep Step(int number, bool verify = true, params GateInput[] inputs) => new WorkflowStep
        {
            Id = "step-" + number,
            Number = number,
            Title = "Step " + number,
            Gate = new GateSpec
            {
                Inputs = inputs ?? Array.Empty<GateInput>(),
                Verify = verify ? new[] { new VerifySpec { Kind = "workflow_admissible" } } : Array.Empty<VerifySpec>(),
            },
        };

        private static WorkflowRunState Run(params WorkflowStep[] steps)
        {
            var run = new WorkflowRunState("wfr-cert", new WorkflowDef
            {
                Name = "certificate-test",
                Title = "Certificate test",
                Strictness = "hard",
                Steps = steps,
            }, null);
            run.CoverageSurface = new CoverageSurfaceLock
            {
                CurrentGrid = new[] { "'Product'[Category]", "'Date'[Year]" },
                CurrentOpenGrains = Array.Empty<string>(),
            };
            return run;
        }

        private static AnswerValue Answer(string value) => new AnswerValue { Value = value };

        private static AnswerValue DeclinedAnswer(string reason) => new AnswerValue
        {
            Declined = true, DeclineReason = reason,
        };

        private static GateInput CertificateInput(string scope = null) => new GateInput
        {
            Name = "certificate", Question = "Certificate claim?", Type = "text", Required = "required",
            Scope = scope,
        };

        private static WorkflowStep NamedStep(string id, int number, string title, params GateInput[] inputs) => new WorkflowStep
        {
            Id = id,
            Number = number,
            Title = title,
            Gate = new GateSpec
            {
                Inputs = inputs ?? Array.Empty<GateInput>(),
                Verify = Array.Empty<VerifySpec>(),
            },
        };

        [Theory]
        [InlineData("FULL", "PARTIAL", "PARTIAL")]
        [InlineData("PARTIAL", "FULL", "PARTIAL")]
        [InlineData("FULL", "FULL", "FULL")]
        // Prose claims: any non-alphanumeric boundary after the level keyword parses. A period demoted an
        // honest "FULL. All four grains..." claim to OVERRIDDEN in the live pilot (2026-07-19).
        [InlineData("FULL. All four grains anchored and proven.", "FULL", "FULL")]
        [InlineData("PARTIAL, one ledger context stays open.", "FULL", "PARTIAL")]
        [InlineData("full - honest downgrade named", "FULL", "FULL")]
        [InlineData("FULLY covered", "FULL", "OVERRIDDEN")]     // not a level keyword; unparseable stays weakest
        [InlineData("no claim at all", "FULL", "OVERRIDDEN")]
        public void Minimum_certificate_level_uses_the_weaker_of_claim_and_computed(
            string claim, string computed, string expected)
        {
            Assert.Equal(expected, WorkflowRunner.MinimumCertificateLevel(claim, computed));
        }

        [Fact]
        public async Task Completion_materializes_the_minimum_certificate_from_an_earlier_claim()
        {
            var claimStep = Step(1, verify: false, new GateInput
            {
                Name = "certificate", Question = "Certificate claim?", Type = "text", Required = "required",
            });
            var finalStep = Step(2);
            var run = Run(claimStep, finalStep);
            run.CoverageSurface.CurrentOpenGrains = new[] { "axis:'Product'[Category]" };

            await WorkflowRunner.SubmitStepAsync(run, "step-1",
                new Dictionary<string, AnswerValue> { ["certificate"] = Answer("FULL") }, null);
            await WorkflowRunner.SubmitStepAsync(run, "step-2", new Dictionary<string, AnswerValue>(),
                (spec, step, state, answers) => Task.FromResult(new VerifyResult
                {
                    Kind = spec.Kind, Status = "passed", Detail = "verified",
                }));

            Assert.Equal("completed", run.Status);
            Assert.Equal("FULL", run.Certificate.ClaimLevel);
            Assert.Equal("PARTIAL", run.Certificate.ComputedLevel);
            Assert.Equal("PARTIAL", run.Certificate.Level);
            Assert.Equal("FULL", run.Certificate.AgentClaim);
            Assert.Equal("PARTIAL", WorkflowRunner.BuildView(run).Certificate.Level);
        }

        [Fact]
        public void Skipping_a_hard_verify_step_recomputes_strictness_and_forces_OVERRIDDEN()
        {
            var run = Run(Step(1));
            Assert.Null(run.Results[0].EffectiveStrictness);

            WorkflowRunner.SkipStep(run, "step-1", "the user accepted the unresolved proof risk");

            Assert.Equal("completed", run.Status);
            Assert.Null(run.Results[0].EffectiveStrictness);
            Assert.Equal("OVERRIDDEN", run.Certificate.ComputedLevel);
            Assert.Equal("OVERRIDDEN", run.Certificate.Level);
            var skipped = Assert.Single(run.Certificate.SkippedSteps);
            Assert.Equal("hard", skipped.EffectiveStrictness);
            Assert.Equal("the user accepted the unresolved proof risk", skipped.Reason);
        }

        [Fact]
        public void Skipping_a_hard_input_only_step_forces_OVERRIDDEN()
        {
            var run = Run(Step(4, verify: false));
            Assert.Empty(run.Def.Steps[0].Gate.Verify);
            Assert.Null(run.Results[0].EffectiveStrictness);

            WorkflowRunner.SkipStep(run, "step-4", "the witness could not be authored");

            Assert.Equal("completed", run.Status);
            Assert.Equal("OVERRIDDEN", run.Certificate.ComputedLevel);
            Assert.Equal("OVERRIDDEN", run.Certificate.Level);
            Assert.Contains(WorkflowRunner.OverriddenCertificateConsequence, run.Results[0].Note);
            var skipped = Assert.Single(run.Certificate.SkippedSteps);
            Assert.Equal("step-4", skipped.StepId);
            Assert.Equal("hard", skipped.EffectiveStrictness);
        }

        [Fact]
        public void Certificate_record_carries_coverage_countersigns_revisions_repairs_disputes_and_skip_reasons()
        {
            var run = Run(Step(1), Step(2));
            run.Results[0].Status = "skipped";
            run.Results[0].Note = "skipped: external policy accepted the exception "
                + WorkflowRunner.OverriddenCertificateConsequence;
            run.Results[1].Status = "passed";
            run.Results[1].Answers["certificate"] = Answer("FULL");
            run.CoverageSurface.CurrentOpenGrains = new[] { "axis:'Product'[Category]" };
            run.ShapeMismatchCountersigns.Add(new ShapeMismatchCountersign
            {
                ShapeId = "axis:'Product'[Category]",
                Coordinate = "axis:'Product'[Category] | 'Product'[Category]=Bikes",
                CandidateValue = "100",
                WitnessValue = "90",
                Stated = "The requirement deliberately leaves this category open.",
                StepId = "step-2",
            });
            run.ShapeMismatchLedger["axis:'Product'[Category]"] = new ShapeMismatchLedgerEntry
            {
                ShapeId = "axis:'Product'[Category]",
                Open = true,
                State = "DISPUTED",
                Cells = new[]
                {
                    new ShapeMismatchLedgerCell
                    {
                        Coordinate = "axis:'Product'[Category] | 'Product'[Category]=Components",
                        CandidateValue = "12",
                        WitnessValue = "10",
                    },
                },
            };
            run.AnchorRevisions.Add(new AnchorRevision());
            run.AnchorRevisions.Add(new AnchorRevision());
            run.AnchorFormRepairCount = 1;

            var certificate = WorkflowRunner.ComputeCertificate(run);

            Assert.Equal("OVERRIDDEN", certificate.Level);
            Assert.Equal(new[] { "'Product'[Category]", "'Date'[Year]" }, certificate.GridColumns);
            Assert.Equal(new[]
            {
                "grand_total:anchored",
                "axis:'Product'[Category]:open",
                "axis:'Date'[Year]:anchored",
                "cross:anchored",
            }, certificate.Coverage.Select(x => x.ShapeId + ":" + x.State));
            var signed = Assert.Single(certificate.CountersignedCells);
            Assert.Equal("100", signed.CandidateValue);
            Assert.Equal("90", signed.WitnessValue);
            Assert.Equal("The requirement deliberately leaves this category open.", signed.Stated);
            Assert.Contains("Components", Assert.Single(certificate.DisputedCells).Coordinate);
            Assert.Equal(2, certificate.AnchorRevisionCount);
            Assert.Equal(1, certificate.FormRepairCount);
            Assert.Equal("external policy accepted the exception", Assert.Single(certificate.SkippedSteps).Reason);

            run.Certificate = certificate;
            var view = WorkflowRunner.BuildView(run);
            Assert.Equal("OVERRIDDEN", view.Certificate.Level);
            Assert.NotSame(certificate, view.Certificate);
            var terminalJson = JsonSerializer.Serialize(WorkflowRunner.BuildRunRecord(run));
            Assert.Contains("\"certificate\":", terminalJson);
            Assert.Contains("\"Level\":\"OVERRIDDEN\"", terminalJson);
            Assert.Contains("The requirement deliberately leaves this category open.", terminalJson);
        }

        [Fact]
        public async Task Hard_gate_block_and_skip_response_state_the_OVERRIDDEN_consequence()
        {
            var run = Run(Step(1));
            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(
                run, "step-1", new Dictionary<string, AnswerValue>(),
                (spec, step, state, answers) => Task.FromResult(new VerifyResult
                {
                    Kind = spec.Kind, Status = "failed", Detail = "proof disagreed",
                })));
            Assert.Contains(WorkflowRunner.OverriddenCertificateConsequence, blocked.Message);

            WorkflowRunner.SkipStep(run, "step-1", "accepted by the user");

            Assert.Contains(WorkflowRunner.OverriddenCertificateConsequence, run.Results[0].Note);
            Assert.Equal("OVERRIDDEN", WorkflowRunner.BuildView(run).Certificate.Level);
        }

        [Fact]
        public async Task Run_without_a_coverage_surface_keeps_the_agent_asserted_certificate_answer_only()
        {
            var step = Step(1, verify: false, new GateInput
            {
                Name = "certificate", Question = "Legacy certificate?", Type = "text", Required = "required",
            });
            var run = new WorkflowRunState("wfr-v6", new WorkflowDef
            {
                Name = "v6-certificate",
                Title = "V6 certificate",
                Version = 6,
                Strictness = "hard",
                Steps = new[] { step },
            }, null);

            await WorkflowRunner.SubmitStepAsync(run, "step-1",
                new Dictionary<string, AnswerValue> { ["certificate"] = Answer("FULL") }, null);

            Assert.Equal("completed", run.Status);
            Assert.Equal("FULL", run.Results[0].Answers["certificate"].Value);
            Assert.Null(run.Certificate);
            Assert.Null(WorkflowRunner.BuildView(run).Certificate);
        }

        [Fact]
        public void V6_terminal_record_matches_the_exact_legacy_JSON_bytes()
        {
            var run = new WorkflowRunState("wfr-v6-record", new WorkflowDef
            {
                Name = "v6-record",
                Title = "V6 record",
                Version = 6,
                Strictness = "hard",
                Steps = new[] { Step(1, verify: false, new GateInput
                {
                    Name = "certificate", Question = "Legacy certificate?", Type = "text", Required = "required",
                }) },
            }, null);
            run.Results[0].Status = "passed";
            run.Results[0].Note = "legacy complete";
            run.Results[0].EffectiveStrictness = "hard";
            run.Results[0].Answers["certificate"] = Answer("FULL");
            run.Status = "completed";
            run.StartedUtc = "2025-01-02T03:04:05.0000000Z";
            run.FinishedUtc = "2025-01-02T03:05:06.0000000Z";
            run.ModelName = "Legacy Model";
            run.ModelFingerprint = "legacy-fp";

            var json = JsonSerializer.Serialize(WorkflowRunner.BuildRunRecord(run));

            Assert.Equal("{\"runId\":\"wfr-v6-record\",\"workflow\":\"v6-record\",\"version\":6,\"status\":\"completed\","
                + "\"abortReason\":null,\"startedUtc\":\"2025-01-02T03:04:05.0000000Z\",\"finishedUtc\":\"2025-01-02T03:05:06.0000000Z\","
                + "\"modelName\":\"Legacy Model\",\"modelFingerprint\":\"legacy-fp\",\"steps\":[{\"StepId\":\"step-1\",\"Status\":\"passed\","
                + "\"Note\":\"legacy complete\",\"EffectiveStrictness\":\"hard\",\"answers\":[{\"name\":\"certificate\",\"Value\":\"FULL\","
                + "\"declined\":null,\"reason\":null}],\"verify\":[],\"verifyHistory\":null}],\"witnessLocks\":null,\"witnessRevisions\":null,"
                + "\"partitionRevisions\":null,\"anchorLocks\":null,\"anchorRevisions\":null}", json);
        }

        // Per-frame certificate fold (DECISION 2.3.1 / 2.3.2).
        private static readonly string[] FoldGrid = { "'Product'[Category]", "'Date'[Year]" };

        private static CoverageSurfaceLock FoldSurface(params string[] openGrains) => new CoverageSurfaceLock
        {
            CurrentGrid = FoldGrid.ToArray(),
            CurrentOpenGrains = openGrains ?? Array.Empty<string>(),
        };

        private static WorkflowStep ReviewRegionStep(string[] values) => new WorkflowStep
        {
            Id = "review-region",
            Number = 2,
            Title = "Review region",
            ForEach = new ForEachSpec { InLiteral = values, As = "region", MaxIterations = Math.Max(25, values.Length) },
            Gate = new GateSpec { Verify = new[] { new VerifySpec { Kind = "workflow_admissible" } } },
        };

        private static WorkflowRunState FoldRun(
            string runId, string[] values, Action<WorkflowRunState, RunFrame[]> arrange,
            bool topSurface = true, bool completeRemaining = true)
        {
            var top = Step(1);
            var loop = ReviewRegionStep(values);
            var run = new WorkflowRunState(runId, new WorkflowDef
            {
                Name = "certificate-fold",
                Title = "Certificate fold",
                Strictness = "hard",
                Steps = new[] { top, loop },
            }, null);
            run.CoverageSurface = topSurface ? FoldSurface() : null;
            run.Plan.RemoveAt(1);
            run.Results.RemoveAt(1);
            run.Results[0].Status = "passed";
            run.Results[0].Answers["certificate"] = Answer("FULL");
            var frames = new RunFrame[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var frame = RunFrame.CreateIteration(
                    run.RunId + ":review-region:" + i, "review-region", i, "region", values[i], "in_progress");
                run.Frames.Add(frame);
                run.Plan.Add(new PlannedStep(run.Def.Steps[1], "review-region#" + i, frame.FrameId, i));
                run.Results.Add(new StepResult
                {
                    StepId = "review-region#" + i,
                    Title = loop.Title,
                    Status = "passed",
                });
                frames[i] = frame;
            }
            arrange?.Invoke(run, frames);
            if (completeRemaining)
            {
                foreach (var frame in frames)
                {
                    if (frame.State == "in_progress") frame.Complete("passed");
                }
            }
            return run;
        }

        [Fact]
        public void Iteration_frame_open_grain_demotes_the_run_certificate_to_PARTIAL()
        {
            var run = FoldRun("wfr-fold-r1", new[] { "North", "South" }, (_, frames) =>
            {
                frames[1].CoverageSurface = FoldSurface("axis:'Product'[Category]");
            });

            var certificate = WorkflowRunner.ComputeCertificate(run);

            Assert.Equal("PARTIAL", certificate.ComputedLevel);
            Assert.Equal("PARTIAL", certificate.Level);
            Assert.Equal(2, certificate.Frames.Length);
            Assert.Equal("North", certificate.Frames[0].LoopValue);
            var frame = certificate.Frames[1];
            Assert.Equal("iteration", frame.Kind);
            Assert.Equal("review-region", frame.StepId);
            Assert.Equal(1, frame.IterationIndex);
            Assert.Equal("region", frame.LoopVariable);
            Assert.Equal("South", frame.LoopValue);
            Assert.Equal(new[] { "axis:'Product'[Category]" }, frame.OpenGrains);
            Assert.Empty(certificate.Frames[0].OpenGrains);
        }

        [Fact]
        public void Certificate_exists_when_only_a_non_top_frame_proved_anything()
        {
            var run = FoldRun("wfr-fold-r2", new[] { "North" }, (_, frames) =>
            {
                frames[0].CoverageSurface = FoldSurface();
            }, topSurface: false);

            var certificate = WorkflowRunner.ComputeCertificate(run);
            Assert.NotNull(certificate);
            Assert.Empty(certificate.GridColumns);
            Assert.Empty(certificate.Coverage);
            Assert.DoesNotContain(certificate.Coverage, g => g.ShapeId == "grand_total");
            Assert.DoesNotContain(certificate.Coverage, g => g.ShapeId == "cross");
            Assert.Equal(FoldGrid, Assert.Single(certificate.Frames).GridColumns);

            run.Certificate = certificate;
            var json = JsonSerializer.Serialize(WorkflowRunner.BuildRunRecord(run));
            Assert.Contains("\"certificate\":", json, StringComparison.Ordinal);
            Assert.Contains("\"coverageSurfaceRevisions\":", json, StringComparison.Ordinal);
        }

        [Fact]
        public void A_surfaceless_frame_folds_without_throwing_and_still_contributes()
        {
            var run = FoldRun("wfr-fold-r2b", new[] { "North" }, (_, frames) =>
            {
                frames[0].CoverageSurface = null;
                frames[0].AnchorRevisions.Add(new AnchorRevision { StepId = "review-region" });
                frames[0].AnchorRevisions.Add(new AnchorRevision { StepId = "review-region" });
                frames[0].AnchorFormRepairCount = 1;
                frames[0].ShapeMismatchLedger["grand_total"] = new ShapeMismatchLedgerEntry
                {
                    ShapeId = "grand_total",
                    Open = true,
                    State = "DISPUTED",
                    Cells = new[]
                    {
                        new ShapeMismatchLedgerCell
                        {
                            Coordinate = "grand_total",
                            CandidateValue = "10",
                            WitnessValue = "9",
                        },
                    },
                };
            });

            var certificate = WorkflowRunner.ComputeCertificate(run);

            Assert.Equal("PARTIAL", certificate.ComputedLevel);
            Assert.Equal("PARTIAL", certificate.Level);
            Assert.Equal(2, certificate.AnchorRevisionCount);
            Assert.Equal(1, certificate.FormRepairCount);
            var frame = Assert.Single(certificate.Frames);
            Assert.Empty(frame.GridColumns);
            Assert.Empty(frame.Coverage);
            Assert.Equal("grand_total", Assert.Single(certificate.DisputedCells).Coordinate);
        }

        [Fact]
        public void Anchor_revision_and_form_repair_counts_sum_across_every_frame()
        {
            var run = FoldRun("wfr-fold-r3", new[] { "North", "South" }, (state, frames) =>
            {
                state.AnchorRevisions.Add(new AnchorRevision { StepId = "step-1" });
                state.AnchorFormRepairCount = 1;
                state.CoverageSurfaceRevisions.Add(new CoverageSurfaceRevision { StepId = "step-1" });

                frames[0].CoverageSurface = FoldSurface();
                frames[0].AnchorRevisions.Add(new AnchorRevision { StepId = "review-region" });
                frames[0].AnchorRevisions.Add(new AnchorRevision { StepId = "review-region" });
                frames[0].AnchorFormRepairCount = 2;
                frames[0].CoverageSurfaceRevisions.Add(new CoverageSurfaceRevision { StepId = "review-region" });
                frames[0].CoverageSurfaceRevisions.Add(new CoverageSurfaceRevision { StepId = "review-region" });

                frames[1].CoverageSurface = null;
                frames[1].AnchorRevisions.Add(new AnchorRevision { StepId = "review-region" });
                frames[1].AnchorRevisions.Add(new AnchorRevision { StepId = "review-region" });
                frames[1].AnchorRevisions.Add(new AnchorRevision { StepId = "review-region" });
                frames[1].AnchorFormRepairCount = 3;
            });

            var certificate = WorkflowRunner.ComputeCertificate(run);

            Assert.Equal(6, certificate.AnchorRevisionCount);
            Assert.Equal(6, certificate.FormRepairCount);
            Assert.Equal(new[] { "step-1", "review-region", "review-region" },
                certificate.CoverageSurfaceRevisions.Select(x => x.StepId));
        }

        [Fact]
        public void Countersigned_and_disputed_cells_gather_from_every_frame_in_frame_order()
        {
            var run = FoldRun("wfr-fold-r4", new[] { "North", "South" }, (state, frames) =>
            {
                state.ShapeMismatchCountersigns.Add(new ShapeMismatchCountersign
                {
                    ShapeId = "grand_total",
                    Coordinate = "top-cell",
                    CandidateValue = "1",
                    WitnessValue = "0",
                    Stated = "top",
                    StepId = "step-1",
                });
                frames[0].CoverageSurface = FoldSurface();
                frames[0].ShapeMismatchCountersigns.Add(new ShapeMismatchCountersign
                {
                    ShapeId = "grand_total",
                    Coordinate = "north-cell",
                    CandidateValue = "2",
                    WitnessValue = "0",
                    Stated = "north",
                    StepId = "review-region",
                });
                frames[1].CoverageSurface = null;
                frames[1].ShapeMismatchLedger["grand_total"] = new ShapeMismatchLedgerEntry
                {
                    ShapeId = "grand_total",
                    Open = true,
                    State = "DISPUTED",
                    Cells = new[]
                    {
                        new ShapeMismatchLedgerCell
                        {
                            Coordinate = "south-cell",
                            CandidateValue = "3",
                            WitnessValue = "0",
                        },
                    },
                };
            });

            var certificate = WorkflowRunner.ComputeCertificate(run);

            Assert.Equal(new[] { "top-cell", "north-cell" }, certificate.CountersignedCells.Select(x => x.Coordinate));
            Assert.Equal(new[] { "1", "2" }, certificate.CountersignedCells.Select(x => x.CandidateValue));
            Assert.Equal(new[] { "0", "0" }, certificate.CountersignedCells.Select(x => x.WitnessValue));
            Assert.Equal("south-cell", Assert.Single(certificate.DisputedCells).Coordinate);
            Assert.Equal("3", certificate.DisputedCells[0].CandidateValue);
        }

        [Fact]
        public void Certificate_fold_never_unions_proof_grids()
        {
            // The top frame proves its own single axis. The two iterations prove one axis each, and their union is
            // a two-axis lattice no single proof ever held: the run-level grid must be the top frame's own, so the
            // top grid is deliberately NOT the union here or the last assertion could not tell the two apart.
            var run = FoldRun("wfr-fold-grids", new[] { "North", "South" }, (state, frames) =>
            {
                state.CoverageSurface = new CoverageSurfaceLock
                {
                    CurrentGrid = new[] { "'Geography'[Country]" },
                    CurrentOpenGrains = Array.Empty<string>(),
                };
                frames[0].CoverageSurface = new CoverageSurfaceLock
                {
                    CurrentGrid = new[] { "'Product'[Category]" },
                    CurrentOpenGrains = Array.Empty<string>(),
                };
                frames[1].CoverageSurface = new CoverageSurfaceLock
                {
                    CurrentGrid = new[] { "'Date'[Year]" },
                    CurrentOpenGrains = Array.Empty<string>(),
                };
            });

            var certificate = WorkflowRunner.ComputeCertificate(run);

            Assert.Equal(new[] { "'Geography'[Country]" }, certificate.GridColumns);
            Assert.Equal(new[] { "'Product'[Category]" }, certificate.Frames[0].GridColumns);
            Assert.Equal(new[] { "'Date'[Year]" }, certificate.Frames[1].GridColumns);
            Assert.False(certificate.Frames[0].GridColumns.SequenceEqual(certificate.Frames[1].GridColumns));
            Assert.False(certificate.GridColumns.SequenceEqual(
                certificate.Frames.SelectMany(f => f.GridColumns).Distinct().ToArray()));
        }

        [Fact]
        public void Iteration_tally_names_the_failed_loop_values_and_bounds_them_at_the_shared_cap()
        {
            var values = Enumerable.Range(0, 12).Select(i => "region-" + i).ToArray();
            var run = FoldRun("wfr-fold-r5", values, (_, frames) =>
            {
                for (var i = 0; i < 11; i++) frames[i].Complete("failed");
            });

            var certificate = WorkflowRunner.ComputeCertificate(run);
            run.Certificate = certificate;

            Assert.Equal(12, certificate.IterationsTotal);
            Assert.Equal(11, certificate.IterationsFailed);
            Assert.Equal(1, certificate.IterationsPassed);
            Assert.Equal(values.Take(EquivalenceGate.MaxStoredMismatchCoordinatesPerShape), certificate.FailedIterationValues);
            Assert.Equal(EquivalenceGate.MaxStoredMismatchCoordinatesPerShape, certificate.FailedIterationValues.Length);
            Assert.Equal(certificate.IterationsTotal, certificate.Frames.Length);

            var view = WorkflowRunner.BuildView(run);
            var json = JsonSerializer.Serialize(WorkflowRunner.BuildRunRecord(run));
            Assert.NotSame(certificate, view.Certificate);
            Assert.Equal(certificate.FailedIterationValues, view.Certificate.FailedIterationValues);
            Assert.Contains("\"IterationsFailed\":11", json, StringComparison.Ordinal);
            run.Certificate.FailedIterationValues[0] = "mutated";
            Assert.Equal("region-0", view.Certificate.FailedIterationValues[0]);
            Assert.DoesNotContain("mutated", json, StringComparison.Ordinal);
        }

        [Fact]
        public void An_in_progress_iteration_frame_counts_as_failed()
        {
            var run = FoldRun("wfr-fold-r5-open", new[] { "North", "South" }, (_, frames) =>
            {
                frames[0].Complete("passed");
            }, completeRemaining: false);

            var certificate = WorkflowRunner.ComputeCertificate(run);

            Assert.Equal(2, certificate.IterationsTotal);
            Assert.Equal(1, certificate.IterationsFailed);
            Assert.Equal(1, certificate.IterationsPassed);
            Assert.Equal(new[] { "South" }, certificate.FailedIterationValues);
        }

        [Fact]
        public void Warn_iteration_skip_counts_as_failed_without_demoting_proof_strength()
        {
            var run = FoldRun("wfr-fold-r5-warn-skip", new[] { "North" }, (state, frames) =>
            {
                state.Plan[1].Step.Gate.Strictness = "warn";
                state.Results[1].Status = "skipped";
                frames[0].Complete("failed");
            });

            var certificate = WorkflowRunner.ComputeCertificate(run);

            Assert.Equal("FULL", certificate.ComputedLevel);
            Assert.Equal("FULL", certificate.Level);
            Assert.Equal(1, certificate.IterationsFailed);
            Assert.Equal(new[] { "North" }, certificate.FailedIterationValues);
        }

        [Fact]
        public void Skip_consequence_names_the_certificate_when_only_another_frame_holds_the_surface()
        {
            var run = Run(Step(1));
            run.CoverageSurface = null;
            var iteration = RunFrame.CreateIteration(
                run.RunId + ":review-region:0", "review-region", 0, "region", "North", "in_progress");
            iteration.CoverageSurface = FoldSurface();
            run.Frames.Add(iteration);

            WorkflowRunner.SkipStep(run, "step-1", "accepted by the user");

            Assert.Contains(WorkflowRunner.OverriddenCertificateConsequence, run.Results[0].Note);
            var certificate = WorkflowRunner.ComputeCertificate(run);
            Assert.NotNull(certificate);
            Assert.Equal("OVERRIDDEN", certificate.Level);
        }

        [Fact]
        public async Task Hard_gate_blocker_names_the_certificate_when_only_another_frame_holds_the_surface()
        {
            var run = Run(Step(1));
            run.CoverageSurface = null;
            var iteration = RunFrame.CreateIteration(
                run.RunId + ":review-region:0", "review-region", 0, "region", "North", "in_progress");
            iteration.CoverageSurface = FoldSurface();
            run.Frames.Add(iteration);

            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(
                run, "step-1", new Dictionary<string, AnswerValue>(),
                (spec, step, state, answers) => Task.FromResult(new VerifyResult
                {
                    Kind = spec.Kind, Status = "failed", Detail = "proof disagreed",
                })));
            Assert.Contains(WorkflowRunner.OverriddenCertificateConsequence, blocked.Message);
        }

        [Theory]
        [InlineData("skipped-top", true, false, false, "OVERRIDDEN")]
        [InlineData("open-iteration", false, true, false, "PARTIAL")]
        [InlineData("all-full", false, false, false, "FULL")]
        // The mixed pair: top open with a clean iteration, and its reverse below. skipTop stays false here — a
        // hard skip in the top frame's own rows is OVERRIDDEN by the row half, and no open grain can lift a
        // minimum back up, so pairing skipTop with this expectation would contradict the skipped-top row above.
        [InlineData("open-top", false, false, true, "PARTIAL")]
        [InlineData("open-both", false, true, true, "PARTIAL")]
        public void Certificate_level_is_the_weakest_across_frames_never_one_frames(
            string _case, bool skipTop, bool openIteration, bool openTop, string expected)
        {
            var run = FoldRun("wfr-fold-r7-" + _case, new[] { "North" }, (state, frames) =>
            {
                frames[0].CoverageSurface = openIteration
                    ? FoldSurface("axis:'Product'[Category]")
                    : FoldSurface();
                if (openTop) state.CoverageSurface.CurrentOpenGrains = new[] { "axis:'Date'[Year]" };
                if (skipTop)
                {
                    state.Results[0].Status = "skipped";
                    state.Results[0].Note = "skipped: accepted "
                        + WorkflowRunner.OverriddenCertificateConsequence;
                }
            });

            var certificate = WorkflowRunner.ComputeCertificate(run);
            Assert.Equal(expected, certificate.ComputedLevel);
            Assert.Equal(expected, certificate.Level);
        }

        [Fact]
        public void Each_frame_level_combines_its_own_hard_rows_with_its_own_surface()
        {
            var run = FoldRun("wfr-fold-r7b", new[] { "North", "South" }, (_, frames) =>
            {
                frames[0].CoverageSurface = FoldSurface();
                frames[1].CoverageSurface = FoldSurface();
            });
            run.Results[2].Status = "failed";
            run.Results[2].VerifyResults = new[]
            {
                new VerifyResult { Kind = "workflow_admissible", Status = "failed", Detail = "south disagreed" },
            };

            var failed = WorkflowRunner.ComputeCertificate(run);
            Assert.Equal("OVERRIDDEN", failed.Level);
            Assert.Equal("FULL", failed.Frames[0].Level);
            Assert.Equal("OVERRIDDEN", failed.Frames[1].Level);

            run.Results[2].Status = "passed";
            run.Frames[2].CoverageSurface = FoldSurface("axis:'Product'[Category]");
            var partial = WorkflowRunner.ComputeCertificate(run);
            Assert.Equal("PARTIAL", partial.Level);
            Assert.Equal("FULL", partial.Frames[0].Level);
            Assert.Equal("PARTIAL", partial.Frames[1].Level);
        }

        [Fact]
        public void Frame_entries_carry_their_own_step_results_and_verify_evidence()
        {
            var run = FoldRun("wfr-fold-r7c", new[] { "North", "South" }, (_, frames) =>
            {
                frames[0].CoverageSurface = FoldSurface();
                frames[1].CoverageSurface = FoldSurface();
            });
            run.Results[1].VerifyResults = new[]
            {
                new VerifyResult { Kind = "workflow_admissible", Status = "passed", Detail = "north-2" },
            };
            run.Results[1].VerifyHistory.Add(new VerifyAttempt
            {
                Ordinal = 1,
                TimestampUtc = "2026-01-02T03:04:30.0000000Z",
                Results = new[] { new VerifyResult { Kind = "workflow_admissible", Status = "failed", Detail = "north-1" } },
            });
            run.Results[1].VerifyHistory.Add(new VerifyAttempt
            {
                Ordinal = 2,
                TimestampUtc = "2026-01-02T03:04:45.0000000Z",
                Results = new[] { new VerifyResult { Kind = "workflow_admissible", Status = "passed", Detail = "north-2" } },
            });
            run.Results[2].VerifyResults = new[]
            {
                new VerifyResult { Kind = "workflow_admissible", Status = "passed", Detail = "south" },
            };

            var certificate = WorkflowRunner.ComputeCertificate(run);
            Assert.Equal("review-region#0", Assert.Single(certificate.Frames[0].Steps).StepId);
            Assert.Equal("review-region#1", Assert.Single(certificate.Frames[1].Steps).StepId);
            Assert.Equal(2, certificate.Frames[0].Steps[0].VerifyHistory.Count);
            Assert.Equal("north-2", certificate.Frames[0].Steps[0].VerifyResults[0].Detail);
            Assert.Equal("south", certificate.Frames[1].Steps[0].VerifyResults[0].Detail);
            Assert.NotSame(run.Results[1], certificate.Frames[0].Steps[0]);
            Assert.NotSame(run.Results[2], certificate.Frames[1].Steps[0]);
            var clone = certificate.Clone();
            Assert.NotSame(certificate.Frames, clone.Frames);
            Assert.NotSame(certificate.Frames[0].Steps, clone.Frames[0].Steps);
            Assert.NotSame(certificate.Frames[0].Steps[0], clone.Frames[0].Steps[0]);
        }

        [Fact]
        public void Call_frame_is_represented_even_without_a_coverage_surface()
        {
            var run = FoldRun("wfr-fold-call", new[] { "North" }, (_, frames) =>
            {
                frames[0].CoverageSurface = null;
            }, topSurface: true);
            var call = RunFrame.CreateCall(
                run.RunId + ":prove-it", "prove-it", "verified-measure", 1,
                new[] { "targetMeasure" }, new[] { "certificate" }, "passed");
            call.CoverageSurface = null;
            call.AnchorRevisions.Add(new AnchorRevision { StepId = "prove-it" });
            call.AnchorFormRepairCount = 2;
            run.Frames.Add(call);
            run.Plan.Add(WorkflowOwnerFixtures.ForeignRow(Step(3), "prove-it/step-1", call.FrameId));
            run.Results.Add(new StepResult { StepId = "prove-it/step-1", Title = "Step 3", Status = "passed" });

            var certificate = WorkflowRunner.ComputeCertificate(run);
            Assert.Equal(2, certificate.Frames.Length);
            Assert.Equal("iteration", certificate.Frames[0].Kind);
            Assert.Equal("call", certificate.Frames[1].Kind);
            Assert.Equal("prove-it", certificate.Frames[1].StepId);
            Assert.Equal("verified-measure", certificate.Frames[1].Workflow);
            Assert.Equal(1, certificate.Frames[1].Depth);
            Assert.Equal(new[] { "targetMeasure" }, certificate.Frames[1].Passed);
            Assert.Equal(new[] { "certificate" }, certificate.Frames[1].Returned);
            Assert.Empty(certificate.Frames[0].GridColumns);
            Assert.Empty(certificate.Frames[1].GridColumns);
            Assert.Equal(2, certificate.FormRepairCount);
            Assert.Equal("prove-it/step-1", Assert.Single(certificate.Frames[1].Steps).StepId);
        }

        [Fact]
        public void Certificate_clone_is_deep_over_every_reference_member()
        {
            var run = FoldRun("wfr-fold-r12", new[] { "North" }, (_, frames) =>
            {
                frames[0].CoverageSurface = FoldSurface("axis:'Product'[Category]");
                frames[0].ShapeMismatchCountersigns.Add(new ShapeMismatchCountersign
                {
                    ShapeId = "grand_total", Coordinate = "cell", CandidateValue = "1", WitnessValue = "0",
                });
            });
            var certificate = WorkflowRunner.ComputeCertificate(run);
            var clone = certificate.Clone();
            AssertReferenceMembersDeepCloned(typeof(WorkflowCertificate), certificate, clone);
            AssertReferenceMembersDeepCloned(typeof(CertificateFrame), certificate.Frames[0], clone.Frames[0]);
        }

        private static void AssertReferenceMembersDeepCloned(Type type, object original, object clone)
        {
            Assert.NotSame(original, clone);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType.IsValueType || property.PropertyType == typeof(string)) continue;
                var left = property.GetValue(original);
                var right = property.GetValue(clone);
                if (left == null && right == null) continue;
                Assert.NotSame(left, right);
            }
        }

        // Top-frame claim ownership. The fold's claim reader is the top frame, not AccumulatedAnswers.
        [Fact]
        public void Callee_claim_with_scope_run_is_ignored()
        {
            // C1: a callee scope-run claim cannot replace a top-frame claim.
            var c1 = Run(Step(1, verify: false, CertificateInput()));
            c1.Results[0].Status = "passed";
            c1.Results[0].Answers["certificate"] = Answer("PARTIAL");
            var c1Step = NamedStep("callee-claim", 2, "Callee claim", CertificateInput("run"));
            var c1Call = RunFrame.CreateCall(
                c1.RunId + ":c1", "prove-it", "verified-measure", 1,
                Array.Empty<string>(), new[] { "certificate" }, "passed");
            c1.Frames.Add(c1Call);
            c1.Plan.Add(WorkflowOwnerFixtures.ForeignRow(c1Step, "prove-it/claim", c1Call.FrameId));
            c1.Results.Add(new StepResult
            {
                StepId = "prove-it/claim", Title = c1Step.Title, Status = "passed",
                Answers = { ["certificate"] = Answer("FULL") },
            });
            c1.Status = "active";
            c1.StepIndex = 1;

            var c1Certificate = WorkflowRunner.ComputeCertificate(c1);
            Assert.Equal("FULL", c1Certificate.ComputedLevel);
            Assert.Equal("PARTIAL", c1Certificate.AgentClaim);
            Assert.Equal("PARTIAL", c1Certificate.ClaimLevel);
            Assert.Equal("PARTIAL", c1Certificate.Level);

            // C2: an iteration-frame scope-run claim cannot supply the claim when the top frame is silent.
            // A call-frame C2 would pass under the rejected AccumulatedAnswers(run, Frames[0], cutoff) reuse,
            // which already hides callee rows; only an iteration row with scope: run catches that reuse.
            var c2 = Run(Step(1));
            c2.Results[0].Status = "passed";
            c2.CoverageSurface.CurrentOpenGrains = new[] { "axis:'Product'[Category]" };
            var c2Step = NamedStep("review-region", 2, "Review region", CertificateInput("run"));
            var c2Iteration = RunFrame.CreateIteration(
                c2.RunId + ":c2", "review-region", 0, "region", "North", "passed");
            c2.Frames.Add(c2Iteration);
            c2.Plan.Add(WorkflowOwnerFixtures.ForeignRow(c2Step, "review-region#0", c2Iteration.FrameId, 0));
            c2.Results.Add(new StepResult
            {
                StepId = "review-region#0", Title = c2Step.Title, Status = "passed",
                Answers = { ["certificate"] = Answer("FULL") },
            });
            c2.Status = "active";
            c2.StepIndex = 1;

            var c2Certificate = WorkflowRunner.ComputeCertificate(c2);
            Assert.Equal("PARTIAL", c2Certificate.ComputedLevel);
            Assert.Null(c2Certificate.AgentClaim);
            Assert.Equal("OVERRIDDEN", c2Certificate.ClaimLevel);
            Assert.Equal("OVERRIDDEN", c2Certificate.Level);

            // C3: an iteration scope-run claim cannot replace a top-frame claim.
            var c3 = Run(Step(1, verify: false, CertificateInput()));
            c3.Results[0].Status = "passed";
            c3.Results[0].Answers["certificate"] = Answer("PARTIAL");
            var c3Step = NamedStep("review-region", 2, "Review region", CertificateInput("run"));
            var c3Iteration = RunFrame.CreateIteration(
                c3.RunId + ":c3", "review-region", 0, "region", "South", "passed");
            c3.Frames.Add(c3Iteration);
            c3.Plan.Add(WorkflowOwnerFixtures.ForeignRow(c3Step, "review-region#0", c3Iteration.FrameId, 0));
            c3.Results.Add(new StepResult
            {
                StepId = "review-region#0", Title = c3Step.Title, Status = "passed",
                Answers = { ["certificate"] = Answer("FULL") },
            });
            c3.Status = "active";
            c3.StepIndex = 1;

            var c3Certificate = WorkflowRunner.ComputeCertificate(c3);
            Assert.Equal("FULL", c3Certificate.ComputedLevel);
            Assert.Equal("PARTIAL", c3Certificate.AgentClaim);
            Assert.Equal("PARTIAL", c3Certificate.ClaimLevel);
            Assert.Equal("PARTIAL", c3Certificate.Level);

            // C4: a top-frame scope-run claim remains eligible.
            var c4 = Run(Step(1, verify: false, CertificateInput("run")));
            c4.Results[0].Status = "passed";
            c4.Results[0].Answers["certificate"] = Answer("PARTIAL");
            var c4Step = NamedStep("review-region", 2, "Review region");
            var c4Iteration = RunFrame.CreateIteration(
                c4.RunId + ":c4", "review-region", 0, "region", "West", "passed");
            c4.Frames.Add(c4Iteration);
            c4.Plan.Add(WorkflowOwnerFixtures.ForeignRow(c4Step, "review-region#0", c4Iteration.FrameId, 0));
            c4.Results.Add(new StepResult
            {
                StepId = "review-region#0", Title = c4Step.Title, Status = "passed",
            });
            c4.Status = "active";
            c4.StepIndex = 1;

            var c4Certificate = WorkflowRunner.ComputeCertificate(c4);
            Assert.Equal("FULL", c4Certificate.ComputedLevel);
            Assert.Equal("PARTIAL", c4Certificate.AgentClaim);
            Assert.Equal("PARTIAL", c4Certificate.ClaimLevel);
            Assert.Equal("PARTIAL", c4Certificate.Level);
        }

        [Fact]
        public void Duplicate_top_frame_claims_use_the_later_top_frame_answer()
        {
            var run = Run(
                Step(1, verify: false, CertificateInput()),
                Step(2, verify: false, CertificateInput()));
            run.Results[0].Status = "passed";
            run.Results[1].Status = "passed";
            run.Status = "completed";

            run.Results[0].Answers["certificate"] = Answer("FULL");
            run.Results[1].Answers["certificate"] = Answer("PARTIAL");
            var laterPartial = WorkflowRunner.ComputeCertificate(run);
            Assert.Equal("FULL", laterPartial.ComputedLevel);
            Assert.Equal("PARTIAL", laterPartial.AgentClaim);
            Assert.Equal("PARTIAL", laterPartial.ClaimLevel);
            Assert.Equal("PARTIAL", laterPartial.Level);

            run.Results[0].Answers["certificate"] = Answer("PARTIAL");
            run.Results[1].Answers["certificate"] = Answer("FULL");
            var laterFull = WorkflowRunner.ComputeCertificate(run);
            Assert.Equal("FULL", laterFull.ComputedLevel);
            Assert.Equal("FULL", laterFull.AgentClaim);
            Assert.Equal("FULL", laterFull.ClaimLevel);
            Assert.Equal("FULL", laterFull.Level);

            run.Results[0].Answers["certificate"] = Answer("FULL");
            run.Results[1].Answers["certificate"] = DeclinedAnswer("the witness declined the claim");
            var laterDeclined = WorkflowRunner.ComputeCertificate(run);
            Assert.Equal("FULL", laterDeclined.ComputedLevel);
            Assert.Null(laterDeclined.AgentClaim);
            Assert.Equal("OVERRIDDEN", laterDeclined.ClaimLevel);
            Assert.Equal("OVERRIDDEN", laterDeclined.Level);
        }

        [Fact]
        public void Root_owned_call_header_claim_is_eligible()
        {
            // A depth-one call header stays owned by the top frame and may answer the claim itself.
            var depthOneHeader = Step(1, verify: false, CertificateInput());
            depthOneHeader.Call = new CallSpec { Workflow = "verified-measure" };
            var depthOneRun = Run(depthOneHeader);
            depthOneRun.Results[0].Status = "passed";
            depthOneRun.Results[0].Answers["certificate"] = Answer("PARTIAL");
            var depthOneBody = NamedStep("callee-body", 2, "Callee body");
            var depthOne = RunFrame.CreateCall(
                depthOneRun.RunId + ":depth-one", "step-1", "verified-measure", 1,
                Array.Empty<string>(), Array.Empty<string>(), "passed");
            depthOneRun.Frames.Add(depthOne);
            depthOneRun.Plan.Add(WorkflowOwnerFixtures.ForeignRow(depthOneBody, "step-1/callee-body", depthOne.FrameId));
            depthOneRun.Results.Add(new StepResult
            {
                StepId = "step-1/callee-body", Title = depthOneBody.Title, Status = "passed",
            });
            depthOneRun.Status = "active";
            depthOneRun.StepIndex = 1;

            var depthOneCertificate = WorkflowRunner.ComputeCertificate(depthOneRun);
            Assert.Equal("FULL", depthOneCertificate.ComputedLevel);
            Assert.Equal("PARTIAL", depthOneCertificate.AgentClaim);
            Assert.Equal("PARTIAL", depthOneCertificate.ClaimLevel);
            Assert.Equal("PARTIAL", depthOneCertificate.Level);

            // At depth two the header is itself owned by a callee frame and remains ineligible.
            var parentHeader = Step(1, verify: false, CertificateInput());
            parentHeader.Call = new CallSpec { Workflow = "verified-measure" };
            var depthTwoRun = Run(parentHeader);
            depthTwoRun.Results[0].Status = "passed";
            depthTwoRun.Results[0].Answers["certificate"] = Answer("FULL");
            var nestedHeader = NamedStep("nested-header", 2, "Nested call header", CertificateInput());
            nestedHeader.Call = new CallSpec { Workflow = "nested-callee" };
            var parentCall = RunFrame.CreateCall(
                depthTwoRun.RunId + ":parent", "step-1", "verified-measure", 1,
                Array.Empty<string>(), Array.Empty<string>(), "passed");
            var nestedCall = RunFrame.CreateCall(
                depthTwoRun.RunId + ":nested", "nested-header", "nested-callee", 2,
                Array.Empty<string>(), Array.Empty<string>(), "passed");
            depthTwoRun.Frames.Add(parentCall);
            depthTwoRun.Frames.Add(nestedCall);
            depthTwoRun.Plan.Add(WorkflowOwnerFixtures.ForeignRow(
                nestedHeader, "step-1/nested-header", parentCall.FrameId));
            depthTwoRun.Results.Add(new StepResult
            {
                StepId = "step-1/nested-header", Title = nestedHeader.Title, Status = "passed",
                Answers = { ["certificate"] = Answer("PARTIAL") },
            });
            depthTwoRun.Status = "active";
            depthTwoRun.StepIndex = 1;

            var depthTwoCertificate = WorkflowRunner.ComputeCertificate(depthTwoRun);
            Assert.Equal("FULL", depthTwoCertificate.ComputedLevel);
            Assert.Equal("FULL", depthTwoCertificate.AgentClaim);
            Assert.Equal("FULL", depthTwoCertificate.ClaimLevel);
            Assert.Equal("FULL", depthTwoCertificate.Level);
        }

        [Fact]
        public void Callee_only_certificate_claim_is_ignored()
        {
            var run = Run(Step(1));
            run.Results[0].Status = "passed";
            run.CoverageSurface.CurrentOpenGrains = new[] { "axis:'Product'[Category]" };
            var calleeStep = NamedStep("callee-claim", 2, "Callee claim", CertificateInput());
            var call = RunFrame.CreateCall(
                run.RunId + ":prove-it", "prove-it", "verified-measure", 1,
                Array.Empty<string>(), new[] { "certificate" }, "passed");
            run.Frames.Add(call);
            run.Plan.Add(WorkflowOwnerFixtures.ForeignRow(calleeStep, "prove-it/claim", call.FrameId));
            run.Results.Add(new StepResult
            {
                StepId = "prove-it/claim", Title = calleeStep.Title, Status = "passed",
                Answers = { ["certificate"] = Answer("FULL") },
            });
            run.Status = "active";
            run.StepIndex = 1;

            var certificate = WorkflowRunner.ComputeCertificate(run);
            Assert.Equal("PARTIAL", certificate.ComputedLevel);
            Assert.Null(certificate.AgentClaim);
            Assert.Equal("OVERRIDDEN", certificate.ClaimLevel);
            Assert.Equal("OVERRIDDEN", certificate.Level);
        }

        [Fact]
        public void No_top_frame_claim_behaves_as_no_claim()
        {
            var run = Run(Step(1));
            run.Results[0].Status = "passed";
            run.CoverageSurface.CurrentOpenGrains = new[] { "axis:'Product'[Category]" };
            run.Status = "completed";

            var certificate = WorkflowRunner.ComputeCertificate(run);
            Assert.Equal("PARTIAL", certificate.ComputedLevel);
            Assert.Null(certificate.AgentClaim);
            Assert.Equal("OVERRIDDEN", certificate.ClaimLevel);
            Assert.Equal("OVERRIDDEN", certificate.Level);
        }

        [Fact]
        public void Later_callee_claim_cannot_replace_an_earlier_top_frame_claim()
        {
            var run = Run(Step(1, verify: false, CertificateInput()));
            run.Results[0].Status = "passed";
            run.Results[0].Answers["certificate"] = Answer("PARTIAL");

            var calleeStep = NamedStep("callee-claim", 2, "Callee claim", CertificateInput());
            var call = RunFrame.CreateCall(
                run.RunId + ":prove-it", "prove-it", "verified-measure", 1,
                Array.Empty<string>(), new[] { "certificate" }, "passed");
            run.Frames.Add(call);
            run.Plan.Add(WorkflowOwnerFixtures.ForeignRow(calleeStep, "prove-it/claim", call.FrameId));
            run.Results.Add(new StepResult
            {
                StepId = "prove-it/claim", Title = calleeStep.Title, Status = "passed",
                Answers = { ["certificate"] = Answer("FULL") },
            });
            run.Status = "active";
            run.StepIndex = 1;

            var certificate = WorkflowRunner.ComputeCertificate(run);
            Assert.Equal("FULL", certificate.ComputedLevel);
            Assert.Equal("PARTIAL", certificate.AgentClaim);
            Assert.Equal("PARTIAL", certificate.ClaimLevel);
            Assert.Equal("PARTIAL", certificate.Level);
        }

        // The RPC door serializes with Newtonsoft (RpcServer.CreateHandler -> JsonMessageFormatter, configured by
        // ConfigureSerializer), NOT System.Text.Json. So the v7-added ContextParts carriers need
        // [Newtonsoft.Json.JsonIgnore] as well; a [System.Text.Json.Serialization.JsonIgnore] alone would let a v6
        // payload silently gain a `contextParts` field over the wire. The System.Text.Json golden test above cannot
        // see that door, so this pins the actual Newtonsoft wire shape for both carriers.
        [Fact]
        public void ContextParts_never_reaches_the_Newtonsoft_RPC_wire()
        {
            var cell = new MismatchCell
            {
                Context = "Category=Bikes",
                ContextParts = new[] { new MismatchContextPart { Name = "'Product'[Category]", Type = "string", Value = "Bikes" } },
                ValueA = "10",
                ValueB = "11",
            };
            var mismatch = new EquivalenceMismatch
            {
                Context = "Year=2025",
                ContextParts = new[] { new MismatchContextPart { Name = "'Date'[Year]", Type = "int64", Value = "2025" } },
                ValueA = "20",
                ValueB = "21",
            };

            var cellJson = RpcWire(cell);
            var mismatchJson = RpcWire(mismatch);

            // ContextParts is suppressed on the wire under ANY casing the naming strategy might pick.
            Assert.DoesNotContain("contextparts", cellJson.ToLowerInvariant());
            Assert.DoesNotContain("contextparts", mismatchJson.ToLowerInvariant());
            // The v6 fields still ride the wire, camelCased.
            foreach (var json in new[] { cellJson, mismatchJson })
            {
                Assert.Contains("\"context\"", json);
                Assert.Contains("\"valueA\"", json);
                Assert.Contains("\"valueB\"", json);
            }
        }

        private static string RpcWire(object value)
        {
            var ser = new Newtonsoft.Json.JsonSerializer();
            RpcServer.ConfigureSerializer(ser);
            var sw = new System.IO.StringWriter();
            ser.Serialize(sw, value);
            return sw.ToString();
        }
    }
}
