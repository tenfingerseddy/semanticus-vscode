using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class WorkflowRunFrameTests
    {
        private static readonly string[] ProofStoreNames =
        {
            "WitnessLocks",
            "WitnessRevisions",
            "PartitionLocks",
            "PartitionRevisions",
            "RunAnchorLocks",
            "AnchorRevisions",
            "CoverageSurface",
            "CoverageSurfaceRevisions",
            "ShapeMismatchLedger",
            "ShapeMismatchCountersigns",
            "LastPassedEquivalenceCandidate",
            "AnchorFormRepairCount",
        };

        // The six stores ComputeCertificate actually reads. WitnessLocks, WitnessRevisions,
        // PartitionLocks, PartitionRevisions, RunAnchorLocks and LastPassedEquivalenceCandidate
        // stay off the certificate; the candidate is a per-frame shortcut that must never fold.
        private static readonly string[] CertificateInputStores =
        {
            "CoverageSurface",
            "CoverageSurfaceRevisions",
            "ShapeMismatchLedger",
            "ShapeMismatchCountersigns",
            "AnchorRevisions",
            "AnchorFormRepairCount",
        };

        private static WorkflowRunState NewRun()
        {
            var def = new WorkflowDef
            {
                Name = "invented-frame-fixture",
                Steps = new[]
                {
                    new WorkflowStep { Id = "step-1", Number = 1, Title = "First" },
                    new WorkflowStep { Id = "step-2", Number = 2, Title = "Second" },
                },
            };
            return new WorkflowRunState("wfr-frame-fixture", def, null);
        }

        [Fact]
        public void A_run_starts_with_one_top_level_frame_and_every_planned_step_names_it()
        {
            var run = NewRun();

            var frame = Assert.Single(run.Frames);
            Assert.Equal(run.RunId, frame.FrameId);
            Assert.All(run.Plan, planned =>
            {
                Assert.Equal(frame.FrameId, planned.FrameId);
                Assert.Contains(run.Frames, candidate => candidate.FrameId == planned.FrameId);
                Assert.Null(planned.IterationIndex);
            });
            var view = WorkflowRunner.BuildView(run);
            Assert.Null(view.Frames);
            var wireJson = JsonConvert.SerializeObject(view, new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore,
            });
            Assert.DoesNotContain("\"frames\"", wireJson, StringComparison.Ordinal);
            var mcpJson = System.Text.Json.JsonSerializer.Serialize(view, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
            Assert.DoesNotContain("\"frames\"", mcpJson, StringComparison.Ordinal);
        }

        [Fact]
        public void Synthetic_iteration_and_call_frames_project_the_ratified_shapes_in_order()
        {
            var run = NewRun();
            run.Def.Steps[0].ForEach = new ForEachSpec
            {
                InLiteral = new[] { "Sales" }, As = "table", MaxIterations = 3,
            };
            var iteration = RunFrame.CreateIteration(
                "iteration-frame", "step-1", 2, "table", "Sales", "passed");
            var call = RunFrame.CreateCall(
                "call-frame", "prove-it", "verified-measure", 2,
                new[] { "targetMeasure" }, new[] { "certificate" }, "in_progress");
            run.Frames.Add(iteration);
            run.Frames.Add(call);
            run.Plan[0] = new PlannedStep(run.Plan[0].Step, "step-1#2", iteration.FrameId, 2);
            run.Plan[1] = new PlannedStep(run.Plan[1].Step, "prove-it/step-1", call.FrameId, null);
            run.Results[0].StepId = "step-1#2";
            run.Results[0].Status = "passed";
            run.Results[1].StepId = "prove-it/step-1";

            iteration.WitnessLocks["candidate"] = "invented-hash";
            iteration.WitnessRevisions.Add(new WitnessRevision { Probe = "candidate", BeforeHash = "before", AfterHash = "after" });
            iteration.PartitionRevisions.Add(new PartitionRevision { Key = "partition-key", Before = "before", After = "after" });
            iteration.RunAnchorLocks["anchors"] = new AnchorRunLock
            {
                AnchorsInput = "anchors", InitialHash = "initial", CurrentHash = "current", StepId = "check-each-table#2",
            };
            iteration.AnchorRevisions.Add(new AnchorRevision { AnchorsInput = "anchors", BeforeHash = "before", AfterHash = "after" });
            iteration.ShapeMismatchLedger["grain"] = new ShapeMismatchLedgerEntry { ShapeId = "grain", State = "DISPUTED" };
            iteration.ShapeMismatchCountersigns.Add(new ShapeMismatchCountersign { ShapeId = "grain", Coordinate = "invented-cell" });

            var view = WorkflowRunner.BuildView(run);

            Assert.Equal(2, view.Frames.Length);
            var iterationView = Assert.IsType<WorkflowRunFrameView>(view.Frames[0]);
            Assert.Equal("iteration", iterationView.Kind);
            Assert.Equal("step-1", iterationView.StepId);
            Assert.Equal(2, iterationView.IterationIndex);
            Assert.Equal("table", iterationView.LoopVariable);
            Assert.Equal("Sales", iterationView.LoopValue);
            Assert.Equal("passed", iterationView.State);
            Assert.Same(view.Steps[0], Assert.Single(iterationView.Steps));
            Assert.NotSame(run.Results[0], view.Steps[0]);
            Assert.Single(iterationView.WitnessLocks);
            Assert.Single(iterationView.WitnessRevisions);
            Assert.Single(iterationView.PartitionRevisions);
            Assert.Single(iterationView.AnchorLocks);
            Assert.Single(iterationView.AnchorRevisions);
            Assert.Single(iterationView.ShapeMismatchLedger);
            Assert.Single(iterationView.ShapeMismatchCountersigns);

            var callView = Assert.IsType<WorkflowRunFrameView>(view.Frames[1]);
            Assert.Equal("call", callView.Kind);
            Assert.Equal("prove-it", callView.StepId);
            Assert.Equal("verified-measure", callView.Workflow);
            Assert.Equal(2, callView.Depth);
            Assert.Equal(new[] { "targetMeasure" }, callView.Passed);
            Assert.Equal(new[] { "certificate" }, callView.Returned);
            Assert.Equal("in_progress", callView.State);
            Assert.Same(view.Steps[1], Assert.Single(callView.Steps));

            run.Results[0].Note = "later mutation";
            iteration.WitnessRevisions[0].AfterHash = "later mutation";
            Assert.Null(view.Steps[0].Note);
            Assert.Equal("after", iterationView.WitnessRevisions[0].AfterHash);
        }

        [Fact]
        public async System.Threading.Tasks.Task A_non_iteration_nested_plan_row_uses_its_exact_planned_identity()
        {
            var run = NewRun();
            var authored = run.Def.Steps[0];
            var call = RunFrame.CreateCall("call-frame", "prove-it", "invented-callee", 2,
                Array.Empty<string>(), Array.Empty<string>(), "in_progress");
            run.Frames.Add(call);
            run.Plan[0] = new PlannedStep(authored, "prove-it/step-1", call.FrameId, null);
            run.Results[0].StepId = "prove-it/step-1";

            Assert.Equal("prove-it/step-1", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(run, authored.Id, null, null));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(run, "", null, null));

            await WorkflowRunner.SubmitStepAsync(run, "prove-it/step-1", null, null);

            Assert.Equal(1, run.StepIndex);
            Assert.Equal("step-1", authored.Id);
        }

        [Fact]
        public void RPC_round_trip_keeps_typed_frames_and_MCP_json_keeps_each_discriminated_shape()
        {
            var run = NewRun();
            var iteration = RunFrame.CreateIteration(
                "iteration-frame", "check-each-table", 2, "table", "Sales", "passed");
            var call = RunFrame.CreateCall(
                "call-frame", "prove-it", "verified-measure", 2,
                new[] { "targetMeasure" }, new[] { "certificate" }, "in_progress");
            run.Frames.Add(iteration);
            run.Frames.Add(call);

            var serializer = new Newtonsoft.Json.JsonSerializer();
            RpcServer.ConfigureSerializer(serializer);
            var writer = new System.IO.StringWriter();
            serializer.Serialize(writer, WorkflowRunner.BuildView(run));
            WorkflowRunView remoteView;
            using (var reader = new JsonTextReader(new System.IO.StringReader(writer.ToString())))
                remoteView = Assert.IsType<WorkflowRunView>(serializer.Deserialize<WorkflowRunView>(reader));

            Assert.All(remoteView.Frames, frame => Assert.IsType<WorkflowRunFrameView>(frame));
            AssertDiscriminatedFrameShapes(writer.ToString());

            var mcpJson = System.Text.Json.JsonSerializer.Serialize(remoteView, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
            AssertDiscriminatedFrameShapes(mcpJson);
        }

        private static void AssertDiscriminatedFrameShapes(string json)
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var frames = document.RootElement.GetProperty("frames");
            var iterationJson = frames[0];
            Assert.Equal("iteration", iterationJson.GetProperty("kind").GetString());
            Assert.Equal(2, iterationJson.GetProperty("iterationIndex").GetInt32());
            Assert.Equal("table", iterationJson.GetProperty("loopVariable").GetString());
            Assert.Equal("Sales", iterationJson.GetProperty("loopValue").GetString());
            Assert.False(iterationJson.TryGetProperty("workflow", out _));
            Assert.False(iterationJson.TryGetProperty("depth", out _));
            Assert.False(iterationJson.TryGetProperty("passed", out _));
            Assert.False(iterationJson.TryGetProperty("returned", out _));

            var callJson = frames[1];
            Assert.Equal("call", callJson.GetProperty("kind").GetString());
            Assert.Equal("verified-measure", callJson.GetProperty("workflow").GetString());
            Assert.Equal(2, callJson.GetProperty("depth").GetInt32());
            Assert.Equal("targetMeasure", callJson.GetProperty("passed")[0].GetString());
            Assert.Equal("certificate", callJson.GetProperty("returned")[0].GetString());
            Assert.False(callJson.TryGetProperty("iterationIndex", out _));
            Assert.False(callJson.TryGetProperty("loopVariable", out _));
            Assert.False(callJson.TryGetProperty("loopValue", out _));
        }

        [Theory]
        [InlineData("passed")]
        [InlineData("failed")]
        public void Completing_a_frame_preserves_the_frame_and_every_proof_store_identity(string outcome)
        {
            var run = NewRun();
            run.Def.Steps[0].ForEach = new ForEachSpec
            {
                InLiteral = new[] { "Sales" }, As = "table", MaxIterations = 1,
            };
            var frame = RunFrame.CreateIteration(
                "iteration-frame", "step-1", 0, "table", "Sales", "in_progress");
            frame.CoverageSurface = new CoverageSurfaceLock();
            frame.LastPassedEquivalenceCandidate = new EquivalenceCandidateReceipt();
            run.Frames.Add(frame);
            run.Plan[0] = new PlannedStep(run.Plan[0].Step, "step-1#0", frame.FrameId, 0);
            run.Results[0].StepId = "step-1#0";

            var stores = ProofStoreNames.Where(name => name != "AnchorFormRepairCount")
                .Select(name => typeof(RunFrame).GetProperty(name)!.GetValue(frame)!).ToArray();

            frame.Complete(outcome);

            Assert.Same(frame, run.Frames[1]);
            var currentStores = ProofStoreNames.Where(name => name != "AnchorFormRepairCount")
                .Select(name => typeof(RunFrame).GetProperty(name)!.GetValue(frame)!).ToArray();
            for (var i = 0; i < stores.Length; i++) Assert.Same(stores[i], currentStores[i]);
            var projected = Assert.Single(WorkflowRunner.BuildView(run).Frames);
            Assert.Equal(outcome, projected.State);
        }

        [Fact]
        public void Separate_frames_hold_separate_proof_state()
        {
            var first = new RunFrame("wfr-frame-fixture");
            var second = new RunFrame("wfr-frame-fixture#1")
            {
                CoverageSurface = new CoverageSurfaceLock(),
                LastPassedEquivalenceCandidate = new EquivalenceCandidateReceipt(),
            };
            first.CoverageSurface = new CoverageSurfaceLock();
            first.LastPassedEquivalenceCandidate = new EquivalenceCandidateReceipt();

            foreach (var storeName in ProofStoreNames.Where(name => name != "AnchorFormRepairCount"))
            {
                var property = typeof(RunFrame).GetMember(storeName).OfType<PropertyInfo>().SingleOrDefault();
                Assert.NotNull(property);
                Assert.NotSame(property.GetValue(first), property.GetValue(second));
            }
            first.AnchorFormRepairCount = 2;
            Assert.Equal(0, second.AnchorFormRepairCount);
        }

        [Fact]
        public void A_submission_routes_all_twelve_accessors_to_its_captured_non_top_frame()
        {
            var run = NewRun();
            var top = run.Frames[0];
            var captured = RunFrame.CreateIteration(
                "iteration-frame", "check-each-table", 0, "table", "Sales", "in_progress");
            run.Frames.Add(captured);
            run.Plan[0] = new PlannedStep(run.Plan[0].Step, "check-each-table#0", captured.FrameId, 0);

            using (run.CaptureSubmissionFrame())
            {
                foreach (var storeName in ProofStoreNames)
                {
                    var runProperty = typeof(WorkflowRunState).GetProperty(storeName)!;
                    var frameProperty = typeof(RunFrame).GetProperty(storeName)!;
                    var routed = runProperty.GetValue(run);
                    var expected = frameProperty.GetValue(captured);
                    if (frameProperty.PropertyType.IsValueType)
                        Assert.Equal(expected, routed);
                    else
                        Assert.Same(expected, routed);
                }

                run.WitnessLocks["witness"] = "captured";
                run.WitnessRevisions.Add(new WitnessRevision { Probe = "witness" });
                run.PartitionLocks["partition"] = "captured";
                run.PartitionRevisions.Add(new PartitionRevision { Key = "partition" });
                run.RunAnchorLocks["anchors"] = new AnchorRunLock { AnchorsInput = "anchors" };
                run.AnchorRevisions.Add(new AnchorRevision { AnchorsInput = "anchors" });
                run.CoverageSurface = new CoverageSurfaceLock();
                run.CoverageSurfaceRevisions.Add(new CoverageSurfaceRevision());
                run.ShapeMismatchLedger["shape"] = new ShapeMismatchLedgerEntry { ShapeId = "shape" };
                run.ShapeMismatchCountersigns.Add(new ShapeMismatchCountersign { ShapeId = "shape" });
                run.LastPassedEquivalenceCandidate = new EquivalenceCandidateReceipt();
                run.AnchorFormRepairCount = 1;

                run.StepIndex = 1;
                Assert.Same(captured.WitnessLocks, run.WitnessLocks);
            }

            Assert.Empty(top.WitnessLocks);
            Assert.Empty(top.WitnessRevisions);
            Assert.Empty(top.PartitionLocks);
            Assert.Empty(top.PartitionRevisions);
            Assert.Empty(top.RunAnchorLocks);
            Assert.Empty(top.AnchorRevisions);
            Assert.Null(top.CoverageSurface);
            Assert.Empty(top.CoverageSurfaceRevisions);
            Assert.Empty(top.ShapeMismatchLedger);
            Assert.Empty(top.ShapeMismatchCountersigns);
            Assert.Null(top.LastPassedEquivalenceCandidate);
            Assert.Equal(0, top.AnchorFormRepairCount);
            Assert.Same(top.WitnessLocks, run.WitnessLocks);
        }

        [Fact]
        public void Submission_scope_fails_loudly_and_exception_disposal_restores_top_level_routing()
        {
            var run = NewRun();
            var top = run.Frames[0];
            var captured = new RunFrame("iteration-frame");
            run.Frames.Add(captured);
            run.Plan[0] = new PlannedStep(run.Plan[0].Step, "step-1#0", captured.FrameId, 0);

            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var scope = run.CaptureSubmissionFrame();
                Assert.Same(captured.WitnessLocks, run.WitnessLocks);
                Assert.Throws<InvalidOperationException>((Action)(() => { run.CaptureSubmissionFrame(); }));
                throw new InvalidOperationException("invented submission failure");
            }));
            Assert.Same(top.WitnessLocks, run.WitnessLocks);

            run.Plan[0] = new PlannedStep(run.Plan[0].Step, "step-1#0", "missing-frame", 0);
            Assert.Throws<InvalidOperationException>((Action)(() => { run.CaptureSubmissionFrame(); }));
            run.Frames.Add(new RunFrame(captured.FrameId));
            run.Plan[0] = new PlannedStep(run.Plan[0].Step, "step-1#0", captured.FrameId, 0);
            Assert.Throws<InvalidOperationException>((Action)(() => { run.CaptureSubmissionFrame(); }));
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_submission_keeps_witness_lock_recording_in_the_captured_frame_after_advance()
        {
            var def = new WorkflowDef
            {
                Name = "invented-local-frame-fixture",
                Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "step-1", Number = 1, Title = "Capture witness",
                        ForEach = new ForEachSpec
                        {
                            InLiteral = new[] { "invented" }, As = "measure", MaxIterations = 1,
                        },
                        Gate = new GateSpec
                        {
                            Inputs = new[] { new GateInput { Name = "witness", Required = "required" } },
                            Verify = new[] { new VerifySpec { Kind = "dax_equivalence", Probe = "witness" } },
                        },
                    },
                },
            };
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(def, null);
            var top = run.Frames[0];
            var captured = RunFrame.CreateIteration(
                "iteration-frame", "step-1", 0, "measure", "invented", "in_progress");
            run.Frames.Add(captured);
            run.Plan[0] = new PlannedStep(run.Plan[0].Step, "step-1#0", captured.FrameId, 0);
            run.Results[0].StepId = "step-1#0";

            var view = await engine.SubmitWorkflowStepAsync(
                run.RunId, "step-1#0", "{\"witness\":\"SUM ( Invented[Value] )\"}", "human");

            Assert.Equal("completed", view.Status);
            Assert.Equal(1, run.StepIndex);
            Assert.True(captured.WitnessLocks.ContainsKey("witness"));
            Assert.Empty(top.WitnessLocks);
            Assert.Same(top.WitnessLocks, run.WitnessLocks);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_submission_keeps_witness_lock_recording_in_the_captured_frame_when_runner_throws()
        {
            var def = new WorkflowDef
            {
                Name = "invented-throwing-frame-fixture",
                Strictness = "hard",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "step-1", Number = 1, Title = "Reject after witness",
                        ForEach = new ForEachSpec
                        {
                            InLiteral = new[] { "invented" }, As = "measure", MaxIterations = 1,
                        },
                        Gate = new GateSpec
                        {
                            Inputs = new[] { new GateInput { Name = "witness", Required = "required" } },
                            Verify = new[]
                            {
                                new VerifySpec { Kind = "baseline_exists" },
                                new VerifySpec { Kind = "dax_equivalence", Probe = "witness" },
                            },
                        },
                    },
                },
            };
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(def, null);
            var top = run.Frames[0];
            var captured = RunFrame.CreateIteration(
                "iteration-frame", "step-1", 0, "measure", "invented", "in_progress");
            run.Frames.Add(captured);
            run.Plan[0] = new PlannedStep(run.Plan[0].Step, "step-1#0", captured.FrameId, 0);
            run.Results[0].StepId = "step-1#0";

            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                run.RunId, "step-1#0", "{\"witness\":\"SUM ( Invented[Value] )\"}", "human"));

            Assert.Equal(0, run.StepIndex);
            Assert.True(captured.WitnessLocks.ContainsKey("witness"));
            Assert.Empty(top.WitnessLocks);
            Assert.Same(top.WitnessLocks, run.WitnessLocks);
        }

        [Fact]
        public async System.Threading.Tasks.Task Rejected_iteration_identity_cannot_rewrite_hidden_witness_receipts()
        {
            var def = new WorkflowDef
            {
                Name = "invented-rejected-identity-fixture",
                Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "step-1", Number = 1, Title = "Keep witness unchanged",
                        ForEach = new ForEachSpec
                        {
                            InLiteral = new[] { "invented" }, As = "measure", MaxIterations = 1,
                        },
                        Gate = new GateSpec
                        {
                            Inputs = new[] { new GateInput { Name = "witness", Required = "required" } },
                            Verify = new[] { new VerifySpec { Kind = "dax_equivalence", Probe = "witness" } },
                        },
                    },
                },
            };
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(def, null);
            var captured = RunFrame.CreateIteration(
                "iteration-frame", "step-1", 0, "measure", "invented", "in_progress",
                new Dictionary<string, AnswerValue>
                {
                    ["witness"] = new AnswerValue { Value = "SUM ( Invented[Value] )" },
                });
            run.Frames.Add(captured);
            run.Plan[0] = new PlannedStep(run.Plan[0].Step, "step-1#0", captured.FrameId, 0);
            run.Results[0].StepId = "step-1#0";
            captured.WitnessLocks["witness"] = "unchanged-lock";

            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                run.RunId, "step-1", "{}", "human"));

            Assert.Equal("unchanged-lock", captured.WitnessLocks["witness"]);
            Assert.Empty(captured.WitnessRevisions);
            Assert.Equal(0, run.StepIndex);
            Assert.Equal("in_progress", run.Results[0].Status);
            Assert.Null(run.SubmissionOrigin);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_frozen_self_declared_loop_source_refusal_is_atomic_before_submission_scope()
        {
            const string source = "items";
            var step = new WorkflowStep
            {
                Id = "step-1", Number = 1, Title = "Repeat frozen witness",
                ForEach = new ForEachSpec { InInput = source, As = "item", MaxIterations = 2 },
                Gate = new GateSpec
                {
                    Inputs = new[] { new GateInput { Name = source, Required = "required" } },
                    Verify = new[] { new VerifySpec { Kind = "dax_equivalence", Probe = source } },
                },
            };
            var def = new WorkflowDef
            {
                Name = "invented-frozen-source-fixture", Strictness = "hard", Steps = new[] { step },
            };
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(def, null);
            run.Results[0].Answers[source] = new AnswerValue { Value = "SUM ( Invented[Before] )" };
            WorkflowRunner.ExpandCurrentForEach(run, new[] { "one" });
            var frame = run.Frames[1];
            var top = run.Frames[0];
            frame.WitnessLocks[source] = "deliberately-different-lock";
            var result = run.Results[0];
            var frozen = result.Answers[source];
            var retained = new AnswerValue { Value = "unchanged answer" };
            result.Answers["retained"] = retained;
            result.Note = "unchanged note";
            var verifyResults = new[] { new VerifyResult { Kind = "existing", Status = "passed" } };
            result.VerifyResults = verifyResults;
            var verifyAttempt = new VerifyAttempt
            {
                Ordinal = 1,
                TimestampUtc = "2000-01-01T00:00:00.0000000Z",
                Results = new[] { new VerifyResult { Kind = "existing-history", Status = "failed" } },
            };
            result.VerifyHistory.Add(verifyAttempt);
            result.EffectiveStrictness = "warn";
            var answers = result.Answers;
            var beforeFrameStores = FrameProofStoreReferences(frame);
            var beforeTopStores = FrameProofStoreReferences(top);

            var current = WorkflowRunner.BuildView(run).CurrentStep;
            Assert.DoesNotContain(current.Questions, input => input.Name == source);
            Assert.Equal("SUM ( Invented[Before] )", WorkflowRunner.AllAnswers(run)[source].Value);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                run.RunId, "step-1#0", "{\"items\":\"SUM ( Invented[After] )\"}", "human"));

            Assert.Contains("fixed at expansion", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("deliberately-different-lock", frame.WitnessLocks[source]);
            Assert.Empty(frame.WitnessRevisions);
            Assert.Equal(0, run.StepIndex);
            Assert.Equal("active", run.Status);
            Assert.Equal("in_progress", result.Status);
            Assert.Equal("unchanged note", result.Note);
            Assert.Same(answers, result.Answers);
            Assert.Same(frozen, result.Answers[source]);
            Assert.Equal("SUM ( Invented[Before] )", frozen.Value);
            Assert.Same(retained, result.Answers["retained"]);
            Assert.Same(verifyResults, result.VerifyResults);
            Assert.Same(verifyAttempt, Assert.Single(result.VerifyHistory));
            Assert.Equal("warn", result.EffectiveStrictness);
            Assert.Equal("in_progress", frame.State);
            Assert.Equal(beforeFrameStores, FrameProofStoreReferences(frame));
            Assert.Equal(beforeTopStores, FrameProofStoreReferences(top));
            Assert.Null(run.SubmissionOrigin);
        }

        [Fact]
        public void Answer_seed_is_deep_cloned_frozen_and_set_exactly_once_even_when_empty()
        {
            var sourceValue = new AnswerValue { Value = "invented seed", DeclineReason = "invented reason" };
            var source = new Dictionary<string, AnswerValue> { ["seeded"] = sourceValue };
            var frame = new RunFrame("seeded-frame", source);
            var run = NewRun();
            run.Frames.Add(frame);
            run.Plan[0] = new PlannedStep(run.Plan[0].Step, run.Plan[0].InstanceId, frame.FrameId, null);

            sourceValue.Value = "mutated source";
            source["seeded"] = new AnswerValue { Value = "replaced source" };

            var firstRead = WorkflowRunner.AllAnswers(run);
            Assert.Equal("invented seed", firstRead["seeded"].Value);
            firstRead["seeded"].Value = "mutated result";
            Assert.Equal("invented seed", WorkflowRunner.AllAnswers(run)["seeded"].Value);
            Assert.Throws<InvalidOperationException>(() => frame.SetAnswerSeed(new Dictionary<string, AnswerValue>()));
            var wire = System.Text.Json.JsonSerializer.Serialize(WorkflowRunner.BuildView(run));
            Assert.DoesNotContain("invented seed", wire, StringComparison.Ordinal);
            Assert.DoesNotContain("answerSeed", wire, StringComparison.Ordinal);

            var empty = new RunFrame("empty-seed");
            Assert.Throws<InvalidOperationException>(() => empty.SetAnswerSeed(new Dictionary<string, AnswerValue>()));
        }

        [Fact]
        public void Same_frame_answers_overlay_the_seed_and_keep_latest_wins()
        {
            var run = NewRun();
            var frame = RunFrame.CreateIteration("same-frame", "loop", 0, "item", "one", "in_progress",
                new Dictionary<string, AnswerValue>
                {
                    ["shared"] = new AnswerValue { Value = "seed" },
                });
            run.Frames.Add(frame);
            run.Plan[0] = new PlannedStep(run.Plan[0].Step, "loop/step-1", frame.FrameId, 0);
            run.Plan[1] = new PlannedStep(run.Plan[1].Step, "loop/step-2", frame.FrameId, 0);
            run.Results[0].Answers["shared"] = new AnswerValue { Value = "first" };
            run.Results[1].Answers["shared"] = new AnswerValue { Value = "second" };
            run.StepIndex = 1;

            var answers = WorkflowRunner.AllAnswers(run);

            Assert.Equal("second", Assert.Single(answers).Value.Value);
        }

        [Fact]
        public void Ordinary_iteration_answers_do_not_enter_the_next_iteration_or_top_frame()
        {
            var run = new WorkflowRunState("wfr-frame-fixture", new WorkflowDef
            {
                Name = "invented-frame-fixture",
                Steps = new[] {
                    new WorkflowStep { Id = "step-1", Title = "First" },
                    new WorkflowStep { Id = "step-2", Title = "Second" },
                    new WorkflowStep { Id = "after", Title = "After" },
                },
            }, null);
            var first = RunFrame.CreateIteration("iteration-1", "loop", 0, "item", "one", "passed");
            var second = RunFrame.CreateIteration("iteration-2", "loop", 1, "item", "two", "in_progress");
            run.Frames.Add(first);
            run.Frames.Add(second);
            run.Plan[0] = new PlannedStep(run.Plan[0].Step, "loop#0", first.FrameId, 0);
            run.Plan[1] = new PlannedStep(run.Plan[1].Step, "loop#1", second.FrameId, 1);
            run.Results[0].Answers["ordinary"] = new AnswerValue { Value = "iteration one" };

            run.StepIndex = 1;
            Assert.False(WorkflowRunner.AllAnswers(run).ContainsKey("ordinary"));

            run.Results[2].Status = "in_progress";
            run.StepIndex = 2;
            run.Results[2].Answers["topOnly"] = new AnswerValue { Value = "top value" };
            Assert.False(WorkflowRunner.AllAnswers(run).ContainsKey("ordinary"));
            Assert.Equal("top value", WorkflowRunner.AllAnswers(run)["topOnly"].Value);

            run.Status = "completed";
            run.StepIndex = run.Plan.Count;
            Assert.False(WorkflowRunner.AllAnswers(run).ContainsKey("ordinary"));
            Assert.Equal("top value", WorkflowRunner.AllAnswers(run)["topOnly"].Value);
        }

        [Fact]
        public void Run_scoped_iteration_answer_is_visible_later_and_after_the_loop_latest_wins()
        {
            var run = NewRun();
            var first = RunFrame.CreateIteration("iteration-1", "loop", 0, "item", "one", "passed");
            var second = RunFrame.CreateIteration("iteration-2", "loop", 1, "item", "two", "passed");
            run.Frames.Add(first);
            run.Frames.Add(second);
            run.Def.Steps[0].Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "ticket", Scope = "run" } } };
            run.Def.Steps[1].Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "ticket", Scope = "run" } } };
            run.Plan[0] = new PlannedStep(run.Def.Steps[0], "loop#0", first.FrameId, 0);
            run.Plan[1] = new PlannedStep(run.Def.Steps[1], "loop#1", second.FrameId, 1);
            run.Results[0].Answers["ticket"] = new AnswerValue { Value = "first ticket" };
            run.Results[1].Answers["ticket"] = new AnswerValue { Value = "second ticket" };

            run.StepIndex = 1;
            Assert.Equal("second ticket", WorkflowRunner.AllAnswers(run)["ticket"].Value);

            run.Plan.Add(new PlannedStep(new WorkflowStep { Id = "after", Title = "After" }, "after", run.RunId, null));
            run.Results.Add(new StepResult { StepId = "after", Title = "After", Status = "in_progress" });
            run.StepIndex = 2;
            Assert.Equal("second ticket", WorkflowRunner.AllAnswers(run)["ticket"].Value);
        }

        [Fact]
        public void Run_scoped_call_outputs_do_not_enter_top_or_iteration_targets()
        {
            var run = NewRun();
            var call = RunFrame.CreateCall("call-frame", "call", "invented-callee", 1,
                Array.Empty<string>(), Array.Empty<string>(), "passed");
            run.Frames.Add(call);
            run.Def.Steps[0].Gate = new GateSpec
            {
                Inputs = new[] { new GateInput { Name = "calleeOutput", Scope = "run" } },
            };
            run.Plan[0] = new PlannedStep(run.Def.Steps[0], "call/step-1", call.FrameId, null);
            run.Results[0].Answers["calleeOutput"] = new AnswerValue { Value = "closed result" };
            run.StepIndex = 1;

            Assert.False(WorkflowRunner.AllAnswers(run).ContainsKey("calleeOutput"));

            var iteration = RunFrame.CreateIteration("iteration-frame", "loop", 0, "item", "one", "in_progress");
            run.Frames.Add(iteration);
            run.Plan[1] = new PlannedStep(run.Plan[1].Step, "loop#0", iteration.FrameId, 0);

            Assert.False(WorkflowRunner.AllAnswers(run).ContainsKey("calleeOutput"));
        }

        [Fact]
        public void Call_frame_sees_only_its_explicit_seed_not_the_caller_namespace()
        {
            var run = NewRun();
            var caller = run.Frames[0];
            var call = RunFrame.CreateCall("call-frame", "call", "invented-callee", 2,
                new[] { "passedName" }, Array.Empty<string>(), "in_progress",
                new Dictionary<string, AnswerValue>
                {
                    ["passedName"] = new AnswerValue { Value = "explicit value" },
                });
            run.Frames.Add(call);
            run.Def.Steps[0].Gate = new GateSpec
            {
                Inputs = new[]
                {
                    new GateInput { Name = "callerOnly" },
                    new GateInput { Name = "runScoped", Scope = "run" },
                },
            };
            run.Results[0].Answers["callerOnly"] = new AnswerValue { Value = "caller value" };
            run.Results[0].Answers["runScoped"] = new AnswerValue { Value = "caller run value" };
            run.Plan[1] = new PlannedStep(run.Plan[1].Step, "call/step-1", call.FrameId, null);
            run.StepIndex = 1;

            var answers = WorkflowRunner.AllAnswers(run);

            Assert.Equal(new[] { "passedName" }, answers.Keys.ToArray());
            Assert.Equal("explicit value", answers["passedName"].Value);
            Assert.Same(caller, run.Frames[0]);
        }

        [Fact]
        public async System.Threading.Tasks.Task Advancement_uses_the_next_iterations_seed_without_crossing_ordinary_answers()
        {
            var def = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "iteration-answer-frame",
                Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "step-1",
                        Title = "Submit",
                        When = "inputs.iterationSeed.answered && inputs.ordinary.answered == false",
                        ForEach = new ForEachSpec
                        {
                            InLiteral = new[] { "one", "two" },
                            As = "item",
                            MaxIterations = 25,
                        },
                        Gate = new GateSpec
                        {
                            Inputs = new[] { new GateInput { Name = "ordinary", Required = "optional" } },
                        },
                    },
                    new WorkflowStep
                    {
                        Id = "step-2",
                        Title = "Next iteration",
                        When = "inputs.iterationSeed.answered && inputs.ordinary.answered == false",
                    },
                },
            };
            var run = new WorkflowRunState("iteration-answer-run", def, null);
            var submitted = RunFrame.CreateIteration("submitted-frame", "step-1", 0, "item", "one", "in_progress");
            var next = RunFrame.CreateIteration("next-frame", "step-1", 1, "item", "two", "in_progress",
                new Dictionary<string, AnswerValue>
                {
                    ["iterationSeed"] = new AnswerValue { Value = "two" },
                });
            run.Frames.Add(submitted);
            run.Frames.Add(next);
            run.Plan[0] = new PlannedStep(run.Def.Steps[0], "loop#0", submitted.FrameId, 0);
            run.Plan[1] = new PlannedStep(run.Def.Steps[0], "loop#1", next.FrameId, 1);
            run.Results[0].StepId = "loop#0";
            run.Results[1].StepId = "loop#1";

            using (run.CaptureSubmissionFrame())
            {
                await WorkflowRunner.SubmitStepAsync(run, "loop#0", new Dictionary<string, AnswerValue>
                {
                    ["ordinary"] = new AnswerValue { Value = "iteration one" },
                }, null);

                Assert.Equal(1, run.StepIndex);
                Assert.Equal("in_progress", run.Results[1].Status);
                var captured = WorkflowRunner.AllAnswers(run);
                Assert.Equal("iteration one", captured["ordinary"].Value);
                Assert.False(captured.ContainsKey("iterationSeed"));
            }

            var nextAnswers = WorkflowRunner.AllAnswers(run);
            Assert.Equal("two", nextAnswers["iterationSeed"].Value);
            Assert.False(nextAnswers.ContainsKey("ordinary"));
        }

        private static WorkflowRunState NewExpandedIterationRun(WorkflowStep step, params string[] values)
        {
            step.ForEach ??= new ForEachSpec { InLiteral = values, As = "item", MaxIterations = 25 };
            var def = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-terminal-frame-fixture",
                Strictness = "hard",
                Steps = new[] { step },
            };
            var run = new WorkflowRunState("invented-terminal-frame-run", def, null);
            WorkflowRunner.ExpandCurrentForEach(run, values);
            return run;
        }

        private static object[] FrameProofStoreReferences(RunFrame frame) =>
            ProofStoreNames.Where(name => name != "AnchorFormRepairCount")
                .Select(name => typeof(RunFrame).GetProperty(name)!.GetValue(frame)!).ToArray();

        private static string[] SnapshotAllTwelveFrameProofStores(RunFrame frame) =>
            ProofStoreNames.Select(name => name + ":" + JsonConvert.SerializeObject(
                typeof(RunFrame).GetProperty(name)!.GetValue(frame))).ToArray();

        private static WorkflowRunState NewSelfDeclaredSourceRun(params string[] values)
        {
            var step = new WorkflowStep
            {
                Id = "review-region",
                Number = 1,
                Title = "Review invented region",
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 25 },
                Gate = new GateSpec
                {
                    Inputs = new[]
                    {
                        new GateInput { Name = "first", Question = "First remaining question?", Required = "optional" },
                        new GateInput { Name = "regions", Question = "Which invented regions?", Required = "optional" },
                        new GateInput { Name = "last", Question = "Last remaining question?", Required = "optional" },
                    },
                    Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                },
            };
            var run = new WorkflowRunState("invented-frozen-source-run", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-frozen-source",
                Strictness = "hard",
                Steps = new[] { step },
            }, null);
            run.Results[0].Answers["regions"] = new AnswerValue { Value = string.Join(", ", values) };
            WorkflowRunner.ExpandCurrentForEach(run, values);
            return run;
        }

        [Fact]
        public void Iteration_view_hides_only_the_frozen_self_declared_list_source_question()
        {
            var run = NewSelfDeclaredSourceRun("North", "South");

            var questions = WorkflowRunner.BuildView(run).CurrentStep.Questions;

            Assert.Equal(new[] { "first", "last" }, questions.Select(question => question.Name));
            Assert.Equal(new[] { "First remaining question?", "Last remaining question?" },
                questions.Select(question => question.Question));
        }

        [Fact]
        public void Earlier_declared_source_and_inline_loop_question_controls_remain_unchanged()
        {
            var earlier = new WorkflowStep
            {
                Id = "choose",
                Number = 1,
                Title = "Choose",
                Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
            };
            var loop = new WorkflowStep
            {
                Id = "review",
                Number = 2,
                Title = "Review",
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 2 },
                Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "finding", Required = "optional" } } },
            };
            var run = new WorkflowRunState("earlier-question-control", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "earlier-question-control",
                Strictness = "off",
                Steps = new[] { earlier, loop },
            }, null) { StepIndex = 1 };
            run.Results[0].Status = "passed";
            run.Results[0].Answers["regions"] = new AnswerValue { Value = "North" };
            run.Results[1].Status = "in_progress";
            WorkflowRunner.ExpandCurrentForEach(run, new[] { "North" });
            Assert.Equal(new[] { "finding" }, WorkflowRunner.BuildView(run).CurrentStep.Questions.Select(q => q.Name));

            var inlineStep = new WorkflowStep
            {
                Id = "inline",
                Number = 1,
                Title = "Inline",
                ForEach = new ForEachSpec { InLiteral = new[] { "one" }, As = "item", MaxIterations = 2 },
                Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
            };
            var inline = NewExpandedIterationRun(inlineStep, "one");
            Assert.Equal(new[] { "regions" }, WorkflowRunner.BuildView(inline).CurrentStep.Questions.Select(q => q.Name));
        }

        [Fact]
        public async System.Threading.Tasks.Task Omitted_frozen_source_survives_hard_gate_retry_and_success()
        {
            var run = NewSelfDeclaredSourceRun("North");
            var attempts = 0;
            WorkflowVerifyExecutor executor = (spec, step, state, answers) =>
                System.Threading.Tasks.Task.FromResult(new VerifyResult
                {
                    Kind = spec.Kind,
                    Status = ++attempts == 1 ? "failed" : "passed",
                    Detail = "invented evidence",
                });

            await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(
                run, "review-region#0", new Dictionary<string, AnswerValue>
                {
                    ["first"] = new AnswerValue { Value = "first attempt" },
                }, executor));

            Assert.Equal("North", run.Results[0].Answers["regions"].Value);
            Assert.Equal("first attempt", run.Results[0].Answers["first"].Value);
            var frozenAfterFailure = run.Results[0].Answers["regions"];

            await WorkflowRunner.SubmitStepAsync(run, "review-region#0", new Dictionary<string, AnswerValue>
            {
                ["last"] = new AnswerValue { Value = "second attempt" },
            }, executor);

            Assert.Equal("passed", run.Results[0].Status);
            Assert.Equal("North", run.Results[0].Answers["regions"].Value);
            Assert.Equal("second attempt", run.Results[0].Answers["last"].Value);
            Assert.False(run.Results[0].Answers.ContainsKey("first"));
            Assert.NotSame(frozenAfterFailure, run.Results[0].Answers["regions"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async System.Threading.Tasks.Task Supplying_or_declining_frozen_source_refuses_before_any_iteration_mutation(bool decline)
        {
            var run = NewSelfDeclaredSourceRun("North", "South");
            var result = run.Results[0];
            result.Note = "unchanged note";
            result.EffectiveStrictness = "unchanged strictness";
            result.VerifyResults = new[] { new VerifyResult { Kind = "existing", Status = "passed" } };
            result.VerifyHistory.Add(new VerifyAttempt
            {
                Ordinal = 1,
                TimestampUtc = "2026-01-02T03:04:05.0000000Z",
                Results = new[] { new VerifyResult { Kind = "archived", Status = "failed" } },
            });
            var frozen = result.Answers["regions"];
            var frame = run.Frames[1];
            var other = run.Frames[2];
            var stores = FrameProofStoreReferences(frame);
            var otherStores = FrameProofStoreReferences(other);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(
                run, "review-region#0", new Dictionary<string, AnswerValue>
                {
                    ["regions"] = decline
                        ? new AnswerValue { Declined = true, DeclineReason = "invented decline" }
                        : new AnswerValue { Value = "East" },
                    ["first"] = new AnswerValue { Value = "must not land" },
                }, null));

            Assert.Contains("fixed at expansion", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("remaining iteration questions", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, run.StepIndex);
            Assert.Equal("active", run.Status);
            Assert.Equal("in_progress", result.Status);
            Assert.Equal("unchanged note", result.Note);
            Assert.Equal("unchanged strictness", result.EffectiveStrictness);
            Assert.Same(frozen, Assert.Single(result.Answers).Value);
            Assert.Equal("existing", Assert.Single(result.VerifyResults).Kind);
            Assert.Equal("archived", Assert.Single(Assert.Single(result.VerifyHistory).Results).Kind);
            Assert.Equal("in_progress", frame.State);
            Assert.Equal("in_progress", other.State);
            Assert.Equal(stores, FrameProofStoreReferences(frame));
            Assert.Equal(otherStores, FrameProofStoreReferences(other));
        }

        [Fact]
        public async System.Threading.Tasks.Task Successful_iteration_completes_its_exact_frame_before_advancing_to_the_second()
        {
            var run = NewExpandedIterationRun(
                new WorkflowStep { Id = "step-1", Number = 1, Title = "Repeat" }, "one", "two");
            var first = run.Frames[1];
            var second = run.Frames[2];
            var firstStores = FrameProofStoreReferences(first);
            var secondStores = FrameProofStoreReferences(second);

            await WorkflowRunner.SubmitStepAsync(run, "step-1#0", null, null);

            Assert.Equal("done", run.Results[0].Status);      // the row ran no check; the frame still finished
            Assert.Equal("passed", first.State);
            Assert.Equal(1, run.StepIndex);
            Assert.Equal("in_progress", run.Results[1].Status);
            Assert.Equal("in_progress", second.State);
            Assert.Same(first, run.Frames[1]);
            Assert.Same(second, run.Frames[2]);
            Assert.Equal(firstStores, FrameProofStoreReferences(first));
            Assert.Equal(secondStores, FrameProofStoreReferences(second));
        }

        [Fact]
        public void Audited_iteration_skip_completes_its_exact_frame_as_failed()
        {
            var run = NewExpandedIterationRun(
                new WorkflowStep { Id = "step-1", Number = 1, Title = "Repeat" }, "one", "two");
            var first = run.Frames[1];
            var second = run.Frames[2];

            WorkflowRunner.SkipStep(run, "step-1#0", "invented accountable reason");

            Assert.Equal("skipped", run.Results[0].Status);
            Assert.Equal("failed", first.State);
            Assert.Equal("in_progress", second.State);
            Assert.Equal(1, run.StepIndex);
        }

        [Theory]
        [InlineData("inputs.ready.answered", "evaluated false")]
        [InlineData("inputs.ready.answered && (", "was unreadable")]
        public void Inapplicable_iteration_completes_its_frame_as_passed_before_automatic_advancement(
            string when, string noteFragment)
        {
            var step = new WorkflowStep { Id = "step-1", Number = 1, Title = "Repeat", When = when };
            var run = NewExpandedIterationRun(step, "one", "two");
            var first = run.Frames[1];
            var second = run.Frames[2];

            WorkflowRunner.AdvancePastInapplicableSteps(run);

            Assert.Equal("not_applicable", run.Results[0].Status);
            Assert.Contains(noteFragment, run.Results[0].Note, StringComparison.Ordinal);
            Assert.Equal("passed", first.State);
            Assert.Equal("passed", second.State);
            Assert.Equal(2, run.StepIndex);
            Assert.Equal("completed", run.Status);
        }

        [Fact]
        public async System.Threading.Tasks.Task Hard_gate_failure_keeps_iteration_frame_in_progress_for_an_exact_retry()
        {
            var step = new WorkflowStep
            {
                Id = "step-1",
                Number = 1,
                Title = "Retry proof",
                Gate = new GateSpec { Verify = new[] { new VerifySpec { Kind = "invented_check" } } },
            };
            var run = NewExpandedIterationRun(step, "one");
            var frame = run.Frames[1];
            var attempts = 0;
            WorkflowVerifyExecutor executor = (spec, current, state, answers) =>
                System.Threading.Tasks.Task.FromResult(new VerifyResult
                {
                    Kind = spec.Kind,
                    Status = ++attempts == 1 ? "failed" : "passed",
                    Detail = "invented evidence",
                });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(run, "step-1#0", null, executor));

            Assert.Equal("failed", run.Results[0].Status);
            Assert.Equal("in_progress", frame.State);
            Assert.Equal(0, run.StepIndex);

            await WorkflowRunner.SubmitStepAsync(run, "step-1#0", null, executor);

            Assert.Equal("passed", run.Results[0].Status);
            Assert.Equal("passed", frame.State);
            Assert.Equal("completed", run.Status);
        }

        [Theory]
        [InlineData("submit")]
        [InlineData("skip")]
        [InlineData("condition")]
        public async System.Threading.Tasks.Task Terminal_current_iteration_frame_refuses_atomically(string activity)
        {
            var step = new WorkflowStep
            {
                Id = "step-1",
                Number = 1,
                Title = "Refuse stale terminal frame",
                When = activity == "condition" ? "inputs.ready.answered" : null,
                Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "answer", Required = "optional" } } },
            };
            var run = NewExpandedIterationRun(step, "one", "two");
            var frame = run.Frames[1];
            var other = run.Frames[2];
            frame.Complete("passed");
            run.Results[0].Note = "unchanged note";
            run.Results[0].Answers["existing"] = new AnswerValue { Value = "unchanged answer" };
            run.Results[0].VerifyResults = new[] { new VerifyResult { Kind = "existing", Status = "passed" } };
            var beforeStores = FrameProofStoreReferences(frame);

            var error = activity == "submit"
                ? await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(
                    run, "step-1#0", new Dictionary<string, AnswerValue>
                    {
                        ["answer"] = new AnswerValue { Value = "new answer" },
                    }, null))
                : activity == "skip"
                    ? Assert.Throws<InvalidOperationException>(() => WorkflowRunner.SkipStep(
                        run, "step-1#0", "invented reason"))
                    : Assert.Throws<InvalidOperationException>(() => WorkflowRunner.AdvancePastInapplicableSteps(run));

            Assert.Contains("in_progress", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, run.StepIndex);
            Assert.Equal("in_progress", run.Results[0].Status);
            Assert.Equal("unchanged note", run.Results[0].Note);
            Assert.Equal("unchanged answer", Assert.Single(run.Results[0].Answers).Value.Value);
            Assert.Equal("existing", Assert.Single(run.Results[0].VerifyResults).Kind);
            Assert.Equal("passed", frame.State);
            Assert.Equal("in_progress", other.State);
            Assert.Equal(beforeStores, FrameProofStoreReferences(frame));
        }

        [Fact]
        public async System.Threading.Tasks.Task Ordinary_and_non_iteration_nested_rows_do_not_complete_projected_frames()
        {
            var ordinary = NewRun();
            var top = Assert.Single(ordinary.Frames);
            var topState = top.State;
            await WorkflowRunner.SubmitStepAsync(ordinary, "step-1", null, null);
            Assert.Same(top, Assert.Single(ordinary.Frames));
            Assert.Equal(topState, top.State);

            var nested = NewRun();
            var call = RunFrame.CreateCall("call-frame", "prove-it", "invented-callee", 1,
                Array.Empty<string>(), Array.Empty<string>(), "in_progress");
            nested.Frames.Add(call);
            nested.Plan[0] = new PlannedStep(nested.Def.Steps[0], "prove-it/step-1", call.FrameId, null);
            nested.Results[0].StepId = "prove-it/step-1";

            await WorkflowRunner.SubmitStepAsync(nested, "prove-it/step-1", null, null);

            // [T220] INVERTED by the call plan splice, deliberately. This half asserted that a call frame is
            // left alone because no runner unit completed one; the splice's frame lifecycle is the card that
            // adds that completion (load-bearing sentence 7: a call frame completes when the cursor leaves it,
            // once, from one site, judged over the frame and its descendants). The row here is the run's only
            // row, so submitting it moves StepIndex past the frame's last row and the frame completes passed.
            // The ORDINARY half above is unchanged: the top-level frame has no projection kind and is never
            // completed by this path.
            Assert.Equal("passed", call.State);
        }

        // ---- unit 15: the preparation submission sits outside the receipt scope ------------------

        private const string PreparationSetupInstructions =
            "This call only fixes the loop list. Answer exactly the visible source question. Do not do iteration work yet. The first applicable iteration will return the first real instructions.";
        private const string InlineSetupInstructions =
            "The authored list is ready. Submit the current base id with no answers. Do not do iteration work yet. The first applicable iteration will return the first real instructions.";
        private const string DeferredSetupInstructions =
            "This loop does not ask you for its list. Submit the current base id with no answers. Do not do iteration work yet. The first applicable iteration will return the first real instructions.";
        private const string AuthoredPreparationInstructions =
            "Authored body: review [[loop.item]] at [[loop.index]].";
        private const string AuthoredInlineInstructions =
            "Authored body: review [[loop.item]] at [[loop.index]].";

        private static WorkflowStep PreparationStep() => new WorkflowStep
        {
            Id = "step-1",
            Number = 1,
            Title = "Prepare then prove",
            Instructions = AuthoredPreparationInstructions,
            Ops = new[] { "edit_measure" },
            ForEach = new ForEachSpec { InInput = "items", As = "item", MaxIterations = 4 },
            Gate = new GateSpec
            {
                Inputs = new[]
                {
                    new GateInput { Name = "items", Question = "Which invented items?", Required = "optional" },
                    new GateInput { Name = "witness", Question = "Which invented witness?", Required = "required" },
                },
                Verify = new[]
                {
                    new VerifySpec { Kind = "baseline_exists" },
                    new VerifySpec { Kind = "dax_equivalence", Probe = "witness" },
                },
            },
        };

        private static WorkflowStep InlineExpansionStep(string[] literal, int maxIterations = 4) => new WorkflowStep
        {
            Id = "step-1",
            Number = 1,
            Title = "Inline then prove",
            Instructions = AuthoredInlineInstructions,
            Ops = new[] { "edit_measure" },
            ForEach = new ForEachSpec { InLiteral = literal, As = "item", MaxIterations = maxIterations },
            Gate = new GateSpec
            {
                Inputs = new[]
                {
                    new GateInput { Name = "finding", Question = "What was found?", Required = "optional" },
                    new GateInput { Name = "witness", Question = "Which invented witness?", Required = "required" },
                },
                Verify = new[]
                {
                    new VerifySpec { Kind = "baseline_exists" },
                    new VerifySpec { Kind = "dax_equivalence", Probe = "witness" },
                },
            },
        };

        private static WorkflowDef InlineExpansionDef(string name, string[] literal, int maxIterations = 4, params WorkflowStep[] following) =>
            new WorkflowDef
            {
                SchemaVersion = 2,
                Name = name,
                Strictness = "hard",
                Steps = new[] { InlineExpansionStep(literal, maxIterations) }.Concat(following).ToArray(),
            };

        private static WorkflowDef PreparationDef(string name) => new WorkflowDef
        {
            SchemaVersion = 2,
            Name = name,
            Strictness = "hard",
            Steps = new[] { PreparationStep() },
        };

        [Fact]
        public async System.Threading.Tasks.Task Local_preparation_runs_outside_the_submission_frame_and_witness_receipt_scope()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(PreparationDef("invented-preparation-boundary"), null);
            var top = run.Frames[0];

            var prepared = await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"items\":\"one, two\"}", "human");

            // The preparation answers the list and splices the plan. It runs no verify, so it produces no
            // evidence and no receipt: nothing here may have entered the witness-lock scope.
            Assert.Equal("active", prepared.Status);
            Assert.Equal("step-1#0", prepared.CurrentStep.StepId);
            Assert.Equal(new[] { "witness" }, prepared.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(0, run.StepIndex);
            Assert.Equal(3, run.Frames.Count);
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessRevisions));
            Assert.All(run.Results, result => Assert.Empty(result.VerifyResults));
            Assert.All(run.Results, result => Assert.Empty(result.VerifyHistory));
            Assert.Null(run.SubmissionOrigin);
            Assert.Same(top.WitnessLocks, run.WitnessLocks);

            // The FIRST real iteration submission is the first verification and the first receipt event.
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                run.RunId, "step-1#0", "{\"witness\":\"SUM ( Invented[Value] )\"}", "human"));

            Assert.True(run.Frames[1].WitnessLocks.ContainsKey("witness"));
            Assert.Empty(run.Frames[1].WitnessRevisions);
            Assert.Empty(top.WitnessLocks);
            Assert.Empty(run.Frames[2].WitnessLocks);
            Assert.Same(top.WitnessLocks, run.WitnessLocks);
            // A hard failure keeps iteration #0 current, in progress, and retryable.
            Assert.Equal(0, run.StepIndex);
            Assert.Equal("failed", run.Results[0].Status);
            Assert.Single(run.Results[0].VerifyHistory);
            Assert.Equal("in_progress", run.Frames[1].State);
            Assert.Equal("active", run.Status);
            Assert.Null(run.SubmissionOrigin);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_preparation_refuses_malformed_and_extra_field_payloads_before_any_run_change()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(PreparationDef("invented-preparation-refusal"), null);
            var plan = run.Plan.ToArray();
            var results = run.Results.ToArray();
            var frames = run.Frames.ToArray();
            var answers = run.Results[0].Answers;

            foreach (var payload in new[] { "{", "[]" })
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    engine.SubmitWorkflowStepAsync(run.RunId, "step-1", payload, "human"));

            var extra = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                run.RunId, "step-1", "{\"items\":\"one, two\",\"witness\":\"SUM ( Invented[Value] )\"}", "human"));
            Assert.Contains("witness", extra.Message, StringComparison.Ordinal);

            Assert.Equal(plan, run.Plan);
            Assert.Equal(results, run.Results);
            Assert.Equal(frames, run.Frames);
            Assert.Same(answers, run.Results[0].Answers);
            Assert.Empty(run.Results[0].Answers);
            Assert.Equal(0, run.StepIndex);
            Assert.Equal("in_progress", run.Results[0].Status);
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessRevisions));
            Assert.Null(run.SubmissionOrigin);
        }

        [Fact]
        public async System.Threading.Tasks.Task Preparation_seeds_every_iteration_frame_with_an_independent_frozen_source()
        {
            var step = new WorkflowStep
            {
                Id = "review-region",
                Number = 1,
                Title = "Review invented region",
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 4 },
                Gate = new GateSpec
                {
                    Inputs = new[]
                    {
                        new GateInput { Name = "regions", Required = "optional" },
                        new GateInput { Name = "finding", Required = "optional" },
                    },
                },
            };
            var run = new WorkflowRunState("invented-preparation-seed-run", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-preparation-seed",
                Strictness = "off",
                Steps = new[] { step },
            }, null);

            await WorkflowRunner.SubmitStepAsync(run, "review-region", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South", DeclineReason = "invented note" },
            }, null);

            var first = new Dictionary<string, AnswerValue>();
            var second = new Dictionary<string, AnswerValue>();
            run.Frames[1].CopyAnswerSeedTo(first);
            run.Frames[2].CopyAnswerSeedTo(second);
            Assert.Equal("North, South", first["regions"].Value);
            Assert.Equal("invented note", first["regions"].DeclineReason);
            Assert.Equal("North, South", second["regions"].Value);
            Assert.NotSame(first["regions"], second["regions"]);

            first["regions"].Value = "changed";
            var firstAgain = new Dictionary<string, AnswerValue>();
            run.Frames[1].CopyAnswerSeedTo(firstAgain);
            Assert.Equal("North, South", firstAgain["regions"].Value);
            Assert.Equal("North, South", run.Results[0].Answers["regions"].Value);
            Assert.Equal("North, South", run.Results[1].Answers["regions"].Value);
            Assert.NotSame(run.Results[0].Answers["regions"], run.Results[1].Answers["regions"]);

            // Seeds are resolution state, never a second answer ledger on the wire.
            var wire = System.Text.Json.JsonSerializer.Serialize(WorkflowRunner.BuildView(run));
            Assert.DoesNotContain("answerSeed", wire, StringComparison.Ordinal);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_preparation_refuses_a_blank_non_optional_source_before_any_run_change()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var def = PreparationDef("invented-preparation-blank-source");
            // The list source is non-optional here, so a blank answer is the same gap EnforceInputs refuses on an
            // ordinary step. The preparation runs no input gate, so this rule binds inside the transition or nowhere.
            def.Steps[0].Gate.Inputs[0].Required = "required";
            var run = sessions.CurrentContext.WorkflowRuns.Start(def, null);
            var plan = run.Plan.ToArray();
            var results = run.Results.ToArray();
            var frames = run.Frames.ToArray();
            var answers = run.Results[0].Answers;

            var blank = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"items\":\"  ,  \"}", "human"));
            Assert.Contains("items", blank.Message, StringComparison.Ordinal);
            Assert.Contains("Which invented items?", blank.Message, StringComparison.Ordinal);
            Assert.Contains("required: it cannot be declined", blank.Message, StringComparison.Ordinal);
            // The preparation enforces the SOURCE, never the iteration questions it refuses to accept.
            Assert.DoesNotContain("witness", blank.Message, StringComparison.Ordinal);

            Assert.Equal(plan, run.Plan);
            Assert.Equal(results, run.Results);
            Assert.Equal(frames, run.Frames);
            Assert.Same(answers, run.Results[0].Answers);
            Assert.Empty(run.Results[0].Answers);
            Assert.Equal(0, run.StepIndex);
            Assert.Equal("in_progress", run.Results[0].Status);
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessRevisions));
            Assert.Null(run.SubmissionOrigin);

            // Before expansion the engine door teaches only the source question.
            var view = await engine.GetWorkflowRunAsync(run.RunId);
            Assert.Equal(new[] { "items" }, view.CurrentStep.Questions.Select(q => q.Name));

            // A real list still expands through the same door.
            var prepared = await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"items\":\"one, two\"}", "human");
            Assert.Equal("step-1#0", prepared.CurrentStep.StepId);
            Assert.Equal(new[] { "witness" }, prepared.CurrentStep.Questions.Select(q => q.Name));
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_inline_expansion_runs_outside_the_submission_frame_and_witness_receipt_scope()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(
                InlineExpansionDef("invented-inline-boundary", new[] { "one", "two" }), null);
            var top = run.Frames[0];

            var expanded = await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", null, "human");

            Assert.Equal("active", expanded.Status);
            Assert.Equal("step-1#0", expanded.CurrentStep.StepId);
            Assert.Equal(new[] { "finding", "witness" }, expanded.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(0, run.StepIndex);
            Assert.Equal(3, run.Frames.Count);
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessRevisions));
            Assert.All(run.Results, result => Assert.Empty(result.VerifyResults));
            Assert.All(run.Results, result => Assert.Empty(result.VerifyHistory));
            Assert.Null(run.SubmissionOrigin);
            Assert.Same(top.WitnessLocks, run.WitnessLocks);

            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                run.RunId, "step-1#0", "{\"witness\":\"SUM ( Invented[Value] )\"}", "human"));

            Assert.True(run.Frames[1].WitnessLocks.ContainsKey("witness"));
            Assert.Empty(run.Frames[1].WitnessRevisions);
            Assert.Empty(top.WitnessLocks);
            Assert.Empty(run.Frames[2].WitnessLocks);
            Assert.Same(top.WitnessLocks, run.WitnessLocks);
            Assert.Equal(0, run.StepIndex);
            Assert.Equal("failed", run.Results[0].Status);
            Assert.Single(run.Results[0].VerifyHistory);
            Assert.Equal("in_progress", run.Frames[1].State);
            Assert.Equal("active", run.Status);
            Assert.Null(run.SubmissionOrigin);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_inline_expansion_refuses_malformed_extra_and_named_payloads_before_any_run_change()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(
                InlineExpansionDef("invented-inline-refusal", new[] { "one", "two" }), null);
            var plan = run.Plan.ToArray();
            var results = run.Results.ToArray();
            var frames = run.Frames.ToArray();
            var answers = run.Results[0].Answers;

            foreach (var payload in new[] { "{", "[]" })
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    engine.SubmitWorkflowStepAsync(run.RunId, "step-1", payload, "human"));

            var extra = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                run.RunId, "step-1", "{\"finding\":\"must not land\"}", "human"));
            Assert.Contains("finding", extra.Message, StringComparison.Ordinal);

            var declined = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SubmitWorkflowStepAsync(
                run.RunId, "step-1", "{\"finding\":{\"declined\":true,\"reason\":\"invented decline\"}}", "human"));
            Assert.Contains("finding", declined.Message, StringComparison.Ordinal);

            Assert.Equal(plan, run.Plan);
            Assert.Equal(results, run.Results);
            Assert.Equal(frames, run.Frames);
            Assert.Same(answers, run.Results[0].Answers);
            Assert.Empty(run.Results[0].Answers);
            Assert.Equal(0, run.StepIndex);
            Assert.Equal("in_progress", run.Results[0].Status);
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessRevisions));
            Assert.Null(run.SubmissionOrigin);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_inline_null_and_empty_object_payloads_both_expand()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            foreach (var payload in new[] { null, "", "{}" })
            {
                var run = sessions.CurrentContext.WorkflowRuns.Start(
                    InlineExpansionDef("invented-inline-empty-payload-" + (payload ?? "null"), new[] { "one", "two" }), null);
                var expanded = await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", payload, "human");
                Assert.Equal("step-1#0", expanded.CurrentStep.StepId);
                Assert.All(run.Results, result => Assert.Empty(result.Answers));
                Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));
                Assert.Null(run.SubmissionOrigin);
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_empty_inline_expansion_creates_no_receipt_and_cannot_execute_hash_zero()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-inline-empty-local",
                Strictness = "hard",
                Steps = new[]
                {
                    InlineExpansionStep(Array.Empty<string>()),
                    new WorkflowStep
                    {
                        Id = "next",
                        Number = 2,
                        Title = "Next",
                        Instructions = "Authored next-step body.",
                    },
                },
            }, null);

            var advanced = await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "human");
            Assert.Equal("next", advanced.CurrentStep.StepId);
            Assert.Equal("Authored next-step body.", advanced.CurrentStep.Instructions);
            Assert.NotEqual(InlineSetupInstructions, advanced.CurrentStep.Instructions);
            Assert.DoesNotContain(run.Plan, planned => planned.InstanceId.Contains("#"));
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.Empty(run.Results[0].Answers);
            Assert.Null(run.SubmissionOrigin);

            var stale = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.SubmitWorkflowStepAsync(run.RunId, "step-1#0", "{\"witness\":\"SUM ( Invented[Value] )\"}", "human"));
            Assert.Contains("not the current step", stale.Message, StringComparison.Ordinal);
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.All(run.Results, result => Assert.Empty(result.VerifyHistory));
            Assert.Equal("next", WorkflowRunner.BuildView(run).CurrentStep.StepId);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_inline_over_bound_stale_and_duplicate_frame_refusals_leave_receipt_stores_unchanged()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);

            var over = sessions.CurrentContext.WorkflowRuns.Start(
                InlineExpansionDef("invented-inline-over", new[] { "one", "two", "three" }, 2), null);
            var overPlan = over.Plan.ToArray();
            var overError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.SubmitWorkflowStepAsync(over.RunId, "step-1", "{}", "human"));
            Assert.Contains("3", overError.Message, StringComparison.Ordinal);
            Assert.Contains("2", overError.Message, StringComparison.Ordinal);
            Assert.Equal(overPlan, over.Plan);
            Assert.All(over.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.Null(over.SubmissionOrigin);

            var stale = sessions.CurrentContext.WorkflowRuns.Start(
                InlineExpansionDef("invented-inline-stale-local", new[] { "one" }), null);
            var staleError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.SubmitWorkflowStepAsync(stale.RunId, "step-1#0", "{}", "human"));
            Assert.Contains("not the current step", staleError.Message, StringComparison.Ordinal);
            Assert.Single(stale.Plan);
            Assert.All(stale.Frames, frame => Assert.Empty(frame.WitnessLocks));

            var duplicate = sessions.CurrentContext.WorkflowRuns.Start(
                InlineExpansionDef("invented-inline-dup-local", new[] { "one" }), null);
            duplicate.Frames.Add(new RunFrame(duplicate.RunId + ":step-1:0"));
            var dupError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.SubmitWorkflowStepAsync(duplicate.RunId, "step-1", "{}", "human"));
            Assert.Contains("already exists", dupError.Message, StringComparison.Ordinal);
            Assert.Single(duplicate.Plan);
            Assert.All(duplicate.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.Null(duplicate.SubmissionOrigin);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_expansion_only_rows_project_setup_then_restore_authored_metadata_on_hash_zero()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);

            var inline = sessions.CurrentContext.WorkflowRuns.Start(
                InlineExpansionDef("invented-inline-project-local", new[] { "  North  ", "East, West" }), null);
            var inlineBefore = await engine.SubmitWorkflowStepAsync(inline.RunId, "step-1", "{}", "human");
            // After expansion the returned view is #0. Capture the setup projection from a fresh unexpanded run.
            var unexpanded = sessions.CurrentContext.WorkflowRuns.Start(
                InlineExpansionDef("invented-inline-project-before", new[] { "  North  ", "East, West" }), null);
            var setup = WorkflowRunner.BuildView(unexpanded);
            Assert.Equal("step-1", setup.CurrentStep.StepId);
            Assert.Equal("Inline then prove", setup.CurrentStep.Title);
            Assert.Equal(InlineSetupInstructions, setup.CurrentStep.Instructions);
            Assert.Empty(setup.CurrentStep.Questions);
            Assert.Empty(setup.CurrentStep.VerifyKinds);
            Assert.Empty(setup.CurrentStep.Ops);
            Assert.Null(setup.CurrentStep.EffectiveStrictness);

            Assert.Equal("step-1#0", inlineBefore.CurrentStep.StepId);
            Assert.Equal("Authored body: review   North   at 0.", inlineBefore.CurrentStep.Instructions);
            Assert.Equal(new[] { "finding", "witness" }, inlineBefore.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(new[] { "baseline_exists", "dax_equivalence" }, inlineBefore.CurrentStep.VerifyKinds);
            Assert.Equal(new[] { "edit_measure" }, inlineBefore.CurrentStep.Ops);
            Assert.Equal("hard", inlineBefore.CurrentStep.EffectiveStrictness);

            var preparedRun = sessions.CurrentContext.WorkflowRuns.Start(PreparationDef("invented-prep-project-local"), null);
            var prepSetup = WorkflowRunner.BuildView(preparedRun);
            Assert.Equal("step-1", prepSetup.CurrentStep.StepId);
            Assert.Equal("Prepare then prove", prepSetup.CurrentStep.Title);
            Assert.Equal(PreparationSetupInstructions, prepSetup.CurrentStep.Instructions);
            Assert.Equal(new[] { "items" }, prepSetup.CurrentStep.Questions.Select(q => q.Name));
            Assert.Empty(prepSetup.CurrentStep.VerifyKinds);
            Assert.Empty(prepSetup.CurrentStep.Ops);
            Assert.Null(prepSetup.CurrentStep.EffectiveStrictness);

            var prepared = await engine.SubmitWorkflowStepAsync(preparedRun.RunId, "step-1", "{\"items\":\"one, two\"}", "human");
            Assert.Equal("step-1#0", prepared.CurrentStep.StepId);
            Assert.Equal("Authored body: review one at 0.", prepared.CurrentStep.Instructions);
            Assert.Equal(new[] { "witness" }, prepared.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(new[] { "baseline_exists", "dax_equivalence" }, prepared.CurrentStep.VerifyKinds);
            Assert.Equal(new[] { "edit_measure" }, prepared.CurrentStep.Ops);
            Assert.Equal("hard", prepared.CurrentStep.EffectiveStrictness);
        }

        private static WorkflowDef EarlierSourceLocalDef(string name, int maxIterations = 4, params WorkflowStep[] following)
        {
            var choose = new WorkflowStep
            {
                Id = "choose",
                Number = 1,
                Title = "Choose invented regions",
                Instructions = "Choose the invented list.",
                Ops = new[] { "ask_user" },
                Gate = new GateSpec
                {
                    Inputs = new[]
                    {
                        new GateInput { Name = "regions", Question = "Which invented regions?", Required = "optional" },
                        new GateInput { Name = "witness", Question = "Which invented witness?", Required = "optional" },
                    },
                },
            };
            var middle = new WorkflowStep
            {
                Id = "middle",
                Number = 2,
                Title = "Intervening invented note",
                Instructions = "Record the intervening invented note.",
                Ops = new[] { "ask_user" },
                Gate = new GateSpec
                {
                    Inputs = new[]
                    {
                        new GateInput { Name = "note", Question = "What is the intervening note?", Required = "optional" },
                    },
                },
            };
            var loop = new WorkflowStep
            {
                Id = "review-region",
                Number = 3,
                Title = "Review invented region",
                Instructions = "Authored body: review [[loop.region]] at [[loop.index]].",
                Ops = new[] { "edit_measure" },
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = maxIterations },
                Gate = new GateSpec
                {
                    Inputs = new[] { new GateInput { Name = "finding", Question = "What was found?", Required = "optional" } },
                    Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                },
            };
            return new WorkflowDef
            {
                SchemaVersion = 2,
                Name = name,
                Strictness = "off",
                Steps = new[] { choose, middle, loop }.Concat(following).ToArray(),
            };
        }

        private static WorkflowDef DeferredLocalDef(string name, bool hard = false, int maxIterations = 4, bool optional = true, params WorkflowStep[] following)
        {
            var choose = new WorkflowStep
            {
                Id = "choose",
                Number = 1,
                Title = "Choose invented regions",
                Instructions = "Choose the invented list.",
                Ops = new[] { "ask_user" },
                Gate = new GateSpec
                {
                    Inputs = new[]
                    {
                        new GateInput { Name = "regions", Question = "Which invented regions?", Required = optional ? "optional" : "required" },
                        new GateInput { Name = "witness", Question = "Which invented witness?", Required = "optional" },
                    },
                    Verify = new[] { new VerifySpec { Kind = "dax_equivalence", Probe = "witness" } },
                },
            };
            var loop = new WorkflowStep
            {
                Id = "review-region",
                Number = 2,
                Title = "Review invented region",
                Instructions = "Authored body: review [[loop.region]] at [[loop.index]].",
                Ops = new[] { "edit_measure" },
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = maxIterations },
                Gate = new GateSpec
                {
                    Inputs = new[] { new GateInput { Name = "finding", Question = "What was found?", Required = "optional" } },
                    Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                },
            };
            return new WorkflowDef
            {
                SchemaVersion = 2,
                Name = name,
                Strictness = hard ? "hard" : "off",
                Steps = new[] { choose, loop }.Concat(following).ToArray(),
            };
        }

        private static void AssertPairedDecision(StepResult result, string expectedOutcome, string expectedState,
            string[] expectedValues, string expectedTargetId)
        {
            Assert.NotNull(result.PendingForEach);
            Assert.Equal(expectedOutcome, result.PendingForEach.Outcome);
            Assert.Equal(expectedState, result.PendingForEach.State);
            Assert.Equal(expectedValues, result.PendingForEach.Values);
            Assert.Equal(expectedTargetId, result.PendingForEach.TargetStepId);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_deferred_source_submit_expands_the_following_loop_inside_its_own_receipt_scope()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(DeferredLocalDef("invented-deferred-receipt"), null);
            var top = run.Frames[0];

            var after = await engine.SubmitWorkflowStepAsync(run.RunId, "choose",
                "{\"regions\":\"North, South\",\"witness\":\"SUM ( Invented[Value] )\"}", "human");

            Assert.Equal("review-region#0", after.CurrentStep.StepId);
            Assert.True(top.WitnessLocks.ContainsKey("witness"));
            Assert.Empty(run.Frames[1].WitnessLocks);
            Assert.Empty(run.Frames[2].WitnessLocks);
            Assert.Same(top.WitnessLocks, run.WitnessLocks);
            Assert.Null(run.SubmissionOrigin);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_deferred_hard_failure_leaves_the_pair_visible_on_get_workflow_run()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var def = DeferredLocalDef("invented-deferred-hard", hard: true);
            def.Steps[0].Gate.Verify = new[] { new VerifySpec { Kind = "invented_check" } };
            var run = sessions.CurrentContext.WorkflowRuns.Start(def, null);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.SubmitWorkflowStepAsync(run.RunId, "choose", "{\"regions\":\"North, South\"}", "human"));
            Assert.Contains("hard gate", error.Message, StringComparison.OrdinalIgnoreCase);

            var view = await engine.GetWorkflowRunAsync(run.RunId);
            Assert.Equal("failed", view.Steps[0].Status);
            Assert.Equal("North, South", view.Steps[0].Answers["regions"].Value);
            AssertPairedDecision(view.Steps[0], "expand", "pending", new[] { "North", "South" }, "review-region");
            Assert.Equal("review-region", view.Steps[1].StepId);
            Assert.False(run.Plan[1].ForEachExpanded);
            Assert.Null(run.SubmissionOrigin);
            var newtonsoft = JsonConvert.SerializeObject(view);
            var stj = System.Text.Json.JsonSerializer.Serialize(view);
            Assert.Contains("PendingForEach", newtonsoft + stj);
            Assert.DoesNotContain("\"PendingForEach\":", JsonConvert.SerializeObject(view.Steps[1]));
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_over_bound_deferred_loop_refuses_before_the_submission_scope()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(DeferredLocalDef("invented-deferred-over", maxIterations: 2), null);
            var plan = run.Plan.ToArray();
            var results = run.Results.ToArray();
            var frames = run.Frames.ToArray();
            var answers = run.Results[0].Answers;

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.SubmitWorkflowStepAsync(run.RunId, "choose", "{\"regions\":\"North, South, East\"}", "human"));
            Assert.Contains("3", error.Message);
            Assert.Contains("2", error.Message);

            Assert.Equal(plan, run.Plan);
            Assert.Equal(results, run.Results);
            Assert.Equal(frames, run.Frames);
            Assert.Same(answers, run.Results[0].Answers);
            Assert.Empty(run.Results[0].Answers);
            Assert.Null(run.Results[0].PendingForEach);
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessRevisions));
            Assert.Null(run.SubmissionOrigin);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_current_unexpanded_deferred_loop_expands_on_a_no_answer_engine_setup()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(
                EarlierSourceLocalDef("invented-deferred-setup"), null);
            var top = run.Frames[0];
            await engine.SubmitWorkflowStepAsync(run.RunId, "choose", "{\"regions\":\"North, South\"}", "human");
            await engine.SubmitWorkflowStepAsync(run.RunId, "middle", "{\"note\":\"intervening\"}", "human");
            Assert.Equal("review-region", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            Assert.Equal(DeferredSetupInstructions, WorkflowRunner.BuildView(run).CurrentStep.Instructions);
            var beforeStores = FrameProofStoreReferences(top);

            var named = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.SubmitWorkflowStepAsync(run.RunId, "review-region", "{\"regions\":\"North\"}", "human"));
            Assert.Contains("regions", named.Message, StringComparison.Ordinal);
            Assert.Equal(beforeStores, FrameProofStoreReferences(top));
            Assert.False(run.Plan[2].ForEachExpanded);
            Assert.Null(run.SubmissionOrigin);

            var after = await engine.SubmitWorkflowStepAsync(run.RunId, "review-region", null, "human");
            Assert.Equal("review-region#0", after.CurrentStep.StepId);
            Assert.Equal(beforeStores, FrameProofStoreReferences(top));
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.All(run.Results, result => Assert.Null(result.PendingForEach));
            Assert.Null(run.SubmissionOrigin);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_step_that_does_not_own_the_next_row_submits_ordinarily_through_the_engine_door()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-nearest-local",
                Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "alpha", Number = 1, Title = "Alpha",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
                    },
                    new WorkflowStep
                    {
                        Id = "choose", Number = 2, Title = "Choose invented regions",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
                    },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 3, Title = "Review invented region",
                        Instructions = "Authored body: review [[loop.region]] at [[loop.index]].",
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 4 },
                    },
                },
            }, null);

            var first = await engine.SubmitWorkflowStepAsync(run.RunId, "alpha", "{\"regions\":\"North, South\"}", "human");
            Assert.Equal("choose", first.CurrentStep.StepId);
            Assert.Null(run.Results[0].PendingForEach);
            Assert.False(run.Plan[2].ForEachExpanded);
            var view = await engine.GetWorkflowRunAsync(run.RunId);
            Assert.Null(view.Steps[0].PendingForEach);

            var second = await engine.SubmitWorkflowStepAsync(run.RunId, "choose", "{}", "human");
            Assert.Equal("review-region#0", second.CurrentStep.StepId);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_blank_optional_deferred_source_marks_the_loop_not_applicable_and_creates_no_iteration_receipt()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(
                DeferredLocalDef("invented-deferred-blank", following: new[] { new WorkflowStep { Id = "after", Number = 3, Title = "After" } }),
                null);

            var after = await engine.SubmitWorkflowStepAsync(run.RunId, "choose", "{\"regions\":\"  \"}", "human");
            Assert.Equal("after", after.CurrentStep.StepId);
            Assert.DoesNotContain(run.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessRevisions));
            Assert.Equal("not_applicable", run.Results[1].Status);

            var stale = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.SubmitWorkflowStepAsync(run.RunId, "review-region#0", "{}", "human"));
            Assert.Contains("not the current step", stale.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("after", run.Results[2].StepId);
            Assert.Equal("in_progress", run.Results[2].Status);
        }

        [Fact]
        public async System.Threading.Tasks.Task Deferred_source_answered_inside_an_iteration_frame_only_crosses_with_run_scope()
        {
            WorkflowStep LoopA(string scope) => new WorkflowStep
            {
                Id = "loop-a", Number = 1, Title = "Loop A",
                Instructions = "Authored body: [[loop.item]].",
                ForEach = new ForEachSpec { InLiteral = new[] { "one", "two" }, As = "item", MaxIterations = 4 },
                Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional", Scope = scope } } },
            };
            var loopB = new WorkflowStep
            {
                Id = "loop-b", Number = 2, Title = "Loop B",
                Instructions = "Authored body: review [[loop.region]].",
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 4 },
            };

            var hidden = new WorkflowRunState("wfr-u17-scope-hidden", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-scope-hidden", Version = 2, Strictness = "off",
                Steps = new[] { LoopA(null), loopB },
            }, null);
            await WorkflowRunner.SubmitStepAsync(hidden, "loop-a", null, null);
            await WorkflowRunner.SubmitStepAsync(hidden, "loop-a#0", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North" },
            }, null);
            Assert.Null(hidden.Results[0].PendingForEach);
            var gap = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(hidden, "loop-a#1", new Dictionary<string, AnswerValue>
                {
                    ["regions"] = new AnswerValue { Value = "North" },
                }, null));
            Assert.Contains("unanswered", gap.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(hidden.Results[1].PendingForEach);

            var visible = new WorkflowRunState("wfr-u17-scope-run", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-scope-run", Version = 2, Strictness = "off",
                Steps = new[] { LoopA("run"), loopB },
            }, null);
            await WorkflowRunner.SubmitStepAsync(visible, "loop-a", null, null);
            await WorkflowRunner.SubmitStepAsync(visible, "loop-a#0", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(visible, "loop-a#1", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "South" },
            }, null);
            AssertPairedDecision(visible.Results[1], "expand", "applied", new[] { "South" }, "loop-b");
            Assert.Contains(visible.Plan, p => p.InstanceId == "loop-b#0");
        }

        [Fact]
        public void No_loop_run_view_omits_the_pending_expansion_member_entirely()
        {
            var view = WorkflowRunner.BuildView(new WorkflowRunState("wfr-noloop-pending", new WorkflowDef
            {
                Name = "invented-noloop-pending",
                Steps = new[] { new WorkflowStep { Id = "plain", Number = 1, Title = "Plain" } },
            }, null));
            var newtonsoft = JsonConvert.SerializeObject(view, new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore,
            });
            var stj = System.Text.Json.JsonSerializer.Serialize(view, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
            Assert.DoesNotContain("pendingForEach", newtonsoft, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pendingForEach", stj, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PendingForEach", newtonsoft, StringComparison.Ordinal);
            Assert.DoesNotContain("PendingForEach", stj, StringComparison.Ordinal);
        }

        [Fact]
        public async System.Threading.Tasks.Task BuildView_snapshot_does_not_tear_when_the_live_pendingForEach_mutates()
        {
            var run = new WorkflowRunState("wfr-view-snap", DeferredLocalDef("invented-view-snap", hard: true), null);
            run.Def.Steps[0].Gate.Verify = new[] { new VerifySpec { Kind = "invented_check" } };
            WorkflowVerifyExecutor fail = (spec, step, state, answers) =>
                System.Threading.Tasks.Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = "invented evidence" });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(run, "choose", new Dictionary<string, AnswerValue>
                {
                    ["regions"] = new AnswerValue { Value = "North, South" },
                }, fail));

            var view = WorkflowRunner.BuildView(run);
            Assert.NotSame(run.Results[0], view.Steps[0]);
            Assert.NotSame(run.Results[0].PendingForEach, view.Steps[0].PendingForEach);
            Assert.NotSame(run.Results[0].PendingForEach.Values, view.Steps[0].PendingForEach.Values);
            AssertPairedDecision(view.Steps[0], "expand", "pending", new[] { "North", "South" }, "review-region");

            run.Results[0].PendingForEach.State = "abandoned";
            run.Results[0].PendingForEach.Outcome = "empty";
            run.Results[0].PendingForEach.Values[0] = "mutated";
            AssertPairedDecision(view.Steps[0], "expand", "pending", new[] { "North", "South" }, "review-region");

            view.Steps[0].PendingForEach.State = "applied";
            view.Steps[0].PendingForEach.Values[1] = "West";
            Assert.Equal("abandoned", run.Results[0].PendingForEach.State);
            Assert.Equal(new[] { "mutated", "South" }, run.Results[0].PendingForEach.Values);
        }

        [Fact]
        public async System.Threading.Tasks.Task PendingForEach_round_trips_through_both_wire_serializers_for_each_state()
        {
            void AssertRoundTrip(WorkflowRunState run, string expectedOutcome, string expectedState, string[] expectedValues)
            {
                var view = WorkflowRunner.BuildView(run);
                var serializer = new JsonSerializer();
                RpcServer.ConfigureSerializer(serializer);
                var writer = new System.IO.StringWriter();
                serializer.Serialize(writer, view);
                var rpcJson = writer.ToString();
                WorkflowRunView remoteView;
                using (var reader = new JsonTextReader(new System.IO.StringReader(rpcJson)))
                    remoteView = Assert.IsType<WorkflowRunView>(serializer.Deserialize<WorkflowRunView>(reader));
                AssertPairedDecision(remoteView.Steps[0], expectedOutcome, expectedState, expectedValues, "review-region");
                Assert.Equal(1, remoteView.Steps[0].PendingForEach.TargetIndex);
                Assert.Equal("regions", remoteView.Steps[0].PendingForEach.SourceInput);
                Assert.Null(remoteView.Steps[1].PendingForEach);
                Assert.Contains("pendingForEach", rpcJson, StringComparison.Ordinal);
                Assert.DoesNotContain("\"pendingForEach\":null", rpcJson, StringComparison.Ordinal);

                var mcpJson = System.Text.Json.JsonSerializer.Serialize(view, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                });
                var mcpView = System.Text.Json.JsonSerializer.Deserialize<WorkflowRunView>(mcpJson, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true,
                });
                AssertPairedDecision(mcpView.Steps[0], expectedOutcome, expectedState, expectedValues, "review-region");
                Assert.Contains("pendingForEach", mcpJson, StringComparison.Ordinal);
                Assert.DoesNotContain("\"pendingForEach\":null", mcpJson, StringComparison.Ordinal);
            }

            var pending = new WorkflowRunState("wfr-wire-pending", DeferredLocalDef("invented-wire-pending", hard: true), null);
            pending.Def.Steps[0].Gate.Verify = new[] { new VerifySpec { Kind = "invented_check" } };
            WorkflowVerifyExecutor fail = (spec, step, state, answers) =>
                System.Threading.Tasks.Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = "invented evidence" });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(pending, "choose", new Dictionary<string, AnswerValue>
                {
                    ["regions"] = new AnswerValue { Value = "North, South" },
                }, fail));
            AssertRoundTrip(pending, "expand", "pending", new[] { "North", "South" });

            WorkflowRunner.SkipStep(pending, "choose", "invented skip after fail");
            AssertRoundTrip(pending, "expand", "abandoned", new[] { "North", "South" });

            var applied = new WorkflowRunState("wfr-wire-applied", DeferredLocalDef("invented-wire-applied"), null);
            await WorkflowRunner.SubmitStepAsync(applied, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, null);
            AssertRoundTrip(applied, "expand", "applied", new[] { "North", "South" });

            var empty = new WorkflowRunState("wfr-wire-empty", DeferredLocalDef("invented-wire-empty", following: new[] { new WorkflowStep { Id = "after", Number = 3, Title = "After" } }), null);
            await WorkflowRunner.SubmitStepAsync(empty, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "  " },
            }, null);
            AssertRoundTrip(empty, "empty", "applied", Array.Empty<string>());

            var falsy = new WorkflowRunState("wfr-wire-false", DeferredLocalDef("invented-wire-false"), null);
            falsy.Def.Steps[1].When = "inputs.missing.answered";
            await WorkflowRunner.SubmitStepAsync(falsy, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, null);
            AssertRoundTrip(falsy, "condition_false", "applied", new[] { "North", "South" });
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_adjacent_deferred_source_submit_expands_the_following_loop()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-earlier-local",
                Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "choose",
                        Number = 1,
                        Title = "Choose invented regions",
                        Instructions = "Choose the invented list.",
                        Ops = new[] { "ask_user" },
                        Gate = new GateSpec
                        {
                            Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } },
                            Verify = new[] { new VerifySpec { Kind = "baseline_exists" } },
                        },
                    },
                    new WorkflowStep
                    {
                        Id = "review-region",
                        Number = 2,
                        Title = "Review invented region",
                        Instructions = "Review the later loop [[loop.region]].",
                        Ops = new[] { "edit_measure" },
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 4 },
                        Gate = new GateSpec
                        {
                            Inputs = new[] { new GateInput { Name = "finding", Question = "What was found?" } },
                            Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                        },
                    },
                },
            }, null);

            var choose = WorkflowRunner.BuildView(run);
            Assert.Equal("Choose the invented list.", choose.CurrentStep.Instructions);
            Assert.Equal(new[] { "ask_user" }, choose.CurrentStep.Ops);
            Assert.Equal(new[] { "baseline_exists" }, choose.CurrentStep.VerifyKinds);
            Assert.NotEqual(InlineSetupInstructions, choose.CurrentStep.Instructions);

            var after = await engine.SubmitWorkflowStepAsync(run.RunId, "choose", "{\"regions\":\"North, South\"}", "human");
            Assert.Equal("review-region#0", after.CurrentStep.StepId);
            Assert.Equal(3, run.Plan.Count);
            Assert.True(run.Plan[1].ForEachExpanded);
            Assert.Equal("Review the later loop North.", after.CurrentStep.Instructions);
            Assert.Equal(new[] { "finding" }, after.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(new[] { "invented_check" }, after.CurrentStep.VerifyKinds);
            Assert.Equal(new[] { "edit_measure" }, after.CurrentStep.Ops);
            Assert.NotEqual(PreparationSetupInstructions, after.CurrentStep.Instructions);
            Assert.NotEqual(InlineSetupInstructions, after.CurrentStep.Instructions);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_ordinary_step_keeps_its_authored_projection()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(new WorkflowDef
            {
                Name = "invented-ordinary-local",
                Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "plain",
                        Number = 1,
                        Title = "Plain",
                        Instructions = "Do the ordinary work.",
                        Ops = new[] { "edit_measure" },
                        Gate = new GateSpec
                        {
                            Inputs = new[] { new GateInput { Name = "answer", Required = "optional" } },
                            Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                        },
                    },
                    new WorkflowStep { Id = "next", Number = 2, Title = "Next" },
                },
            }, null);
            var view = WorkflowRunner.BuildView(run);
            Assert.Equal("plain", view.CurrentStep.StepId);
            Assert.Equal("Do the ordinary work.", view.CurrentStep.Instructions);
            Assert.Equal(new[] { "answer" }, view.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(new[] { "invented_check" }, view.CurrentStep.VerifyKinds);
            Assert.Equal(new[] { "edit_measure" }, view.CurrentStep.Ops);
            Assert.Equal("off", view.CurrentStep.EffectiveStrictness);
            Assert.NotEqual(InlineSetupInstructions, view.CurrentStep.Instructions);

            var after = await engine.SubmitWorkflowStepAsync(run.RunId, "plain", "{\"answer\":\"42\"}", "human");
            Assert.Equal("next", after.CurrentStep.StepId);
        }

        [Fact]
        public void Expansion_only_keeps_engine_signature_and_call_prefills_are_optional()
        {
            var submit = typeof(IEngine).GetMethod(nameof(IEngine.SubmitWorkflowStepAsync));
            Assert.NotNull(submit);
            // The fifth parameter is the optional callGate added by 4c16b391; it stays optional on the interface.
            Assert.Equal(new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(string) },
                submit.GetParameters().Select(p => p.ParameterType));
            Assert.True(submit.GetParameters()[4].IsOptional, "callGate must stay optional so existing callers compile");

            // HandOff is the ninth member, added by 4c16b391 so a parent row can carry its callee's warning.
            // The count is asserted so a tenth member cannot arrive unnamed.
            var names = new[] { "EffectiveStrictness", "HandOff", "Instructions", "Ops", "ProvidedAnswers", "Questions", "StepId", "Title", "VerifyKinds" };
            Assert.Equal(names.Length, typeof(CurrentStepView).GetMembers().OfType<PropertyInfo>().Count());
            foreach (var name in names)
                Assert.NotNull(typeof(CurrentStepView).GetProperty(name));

            // Both controls now execute. The optional call-prefill member stays absent for an ordinary loop.
            var def = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-start-foreach",
                Steps = new[] { InlineExpansionStep(new[] { "one" }) },
            };
            var blocked = WorkflowParser.UnexecutableControlFields(def);
            Assert.DoesNotContain(blocked, u => u.Field == "forEach");

            var withCall = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-start-call",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "delegate", Number = 1, Title = "Delegate",
                        Call = new CallSpec { Workflow = "invented-callee" },
                    },
                },
            };
            var blockedCall = WorkflowParser.UnexecutableControlFields(withCall);
            Assert.Empty(blockedCall);
        }

        [Fact]
        public void Submit_workflow_step_tool_description_teaches_ordinary_verify_self_source_inline_and_deferred()
        {
            var method = typeof(McpTools).GetMethod(nameof(McpTools.SubmitWorkflowStep));
            Assert.NotNull(method);
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
            Assert.False(string.IsNullOrWhiteSpace(description));

            // Ordinary and iteration submits still run the current gate and verification.
            Assert.Contains("ordinary or iteration", description, StringComparison.Ordinal);
            Assert.Contains("gate and verification", description, StringComparison.Ordinal);

            // An unexpanded self-sourced loop is a source-only setup: no body verify, no receipt.
            Assert.Contains("unexpanded self-sourced", description, StringComparison.Ordinal);
            Assert.Contains("source-only", description, StringComparison.Ordinal);
            Assert.Contains("visible source question", description, StringComparison.Ordinal);
            Assert.Contains("Do not do iteration work yet", description, StringComparison.Ordinal);

            // An unexpanded inline loop is a no-answer setup call on the current base id.
            Assert.Contains("unexpanded inline", description, StringComparison.Ordinal);
            Assert.Contains("with no answers", description, StringComparison.Ordinal);
            Assert.Contains("current base id", description, StringComparison.Ordinal);

            // A current unexpanded loop whose list comes from an earlier step is a third no-answer setup.
            Assert.Contains("earlier step", description, StringComparison.Ordinal);
            Assert.Contains("first applicable iteration", description, StringComparison.Ordinal);
            Assert.Contains("first real instructions", description, StringComparison.Ordinal);
            Assert.Contains("missing", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("declined", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("blank", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("maxIterations", description, StringComparison.Ordinal);
            Assert.Contains("scope: run", description, StringComparison.Ordinal);
            Assert.Contains("condition", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("skip_workflow_step", description, StringComparison.Ordinal);
            Assert.Contains("no gate", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no verification", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no receipt", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("#0 will return", description, StringComparison.Ordinal);
            Assert.DoesNotContain("#0 returns the first real instructions", description, StringComparison.Ordinal);
            Assert.DoesNotContain("already answered", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("already resolved", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("was answered", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("was resolved", description, StringComparison.OrdinalIgnoreCase);

            Assert.Contains("immediately following", description, StringComparison.Ordinal);
            Assert.Contains("decides the next loop", description, StringComparison.Ordinal);
            Assert.Contains("expand or empty", description, StringComparison.Ordinal);
            Assert.Contains("condition_false", description, StringComparison.Ordinal);
            Assert.Contains("TotalStepsProvisional remains true", description, StringComparison.Ordinal);
            Assert.Contains("full authored gate", description, StringComparison.Ordinal);
            Assert.Contains("not_applicable", description, StringComparison.Ordinal);
            Assert.Contains("maxIterations", description, StringComparison.Ordinal);
            Assert.Contains("pre-pair checks this unit owns", description, StringComparison.Ordinal);
            Assert.Contains("before anything is recorded", description, StringComparison.Ordinal);
            Assert.DoesNotContain("expands that loop in the same submission", description, StringComparison.Ordinal);

            // The old copy claimed every submit verified and then returned the next authored step.
            Assert.DoesNotContain("The ENGINE then runs the step's verify checks against the live model", description, StringComparison.Ordinal);
            Assert.DoesNotContain("NEXT step", description, StringComparison.Ordinal);
        }

        [Fact]
        public void Submit_workflow_step_description_teaches_the_earlier_source_setup_call()
        {
            var method = typeof(McpTools).GetMethod(nameof(McpTools.SubmitWorkflowStep));
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
            Assert.Contains("earlier step", description, StringComparison.Ordinal);
            Assert.Contains("no answers", description, StringComparison.Ordinal);
            Assert.Contains("Do not do iteration work yet", description, StringComparison.Ordinal);
        }

        [Fact]
        public void Submit_workflow_step_description_promises_first_applicable_iteration_not_index_zero()
        {
            var method = typeof(McpTools).GetMethod(nameof(McpTools.SubmitWorkflowStep));
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
            Assert.Contains("first applicable iteration", description, StringComparison.Ordinal);
            Assert.DoesNotContain("#0 will return", description, StringComparison.Ordinal);
            Assert.DoesNotContain("#0 returns the first real instructions", description, StringComparison.Ordinal);
        }

        [Fact]
        public void Preparation_setup_instructions_promise_first_applicable_iteration_not_index_zero()
        {
            var instructions = WorkflowRunner.BuildView(new WorkflowRunState("wfr-u18-prep-copy", PreparationDef("invented-prep-copy"), null)).CurrentStep.Instructions;
            Assert.Contains("first applicable iteration", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("#0 will return", instructions, StringComparison.Ordinal);
            Assert.Equal(PreparationSetupInstructions, instructions);
        }

        [Fact]
        public void Inline_setup_instructions_promise_first_applicable_iteration_not_index_zero()
        {
            var instructions = WorkflowRunner.BuildView(new WorkflowRunState("wfr-u18-inline-copy", InlineExpansionDef("invented-inline-copy", new[] { "one" }), null)).CurrentStep.Instructions;
            Assert.Contains("first applicable iteration", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("#0 will return", instructions, StringComparison.Ordinal);
            Assert.Equal(InlineSetupInstructions, instructions);
        }

        [Fact]
        public async System.Threading.Tasks.Task Deferred_setup_instructions_promise_first_applicable_iteration_not_index_zero()
        {
            var run = new WorkflowRunState("wfr-u18-deferred-copy", EarlierSourceLocalDef("invented-deferred-copy"), null);
            await WorkflowRunner.SubmitStepAsync(run, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(run, "middle", new Dictionary<string, AnswerValue>
            {
                ["note"] = new AnswerValue { Value = "intervening" },
            }, null);
            var instructions = WorkflowRunner.BuildView(run).CurrentStep.Instructions;
            Assert.Contains("first applicable iteration", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("#0 will return", instructions, StringComparison.Ordinal);
            Assert.Equal(DeferredSetupInstructions, instructions);
            Assert.NotEqual(InlineSetupInstructions, instructions);
            Assert.NotEqual(PreparationSetupInstructions, instructions);
        }

        [Fact]
        public async System.Threading.Tasks.Task Mcp_submit_accepts_a_self_source_preparation_without_claiming_the_step_passed()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(PreparationDef("invented-mcp-preparation-label"), null);
            var top = run.Frames[0];
            var activities = new List<ActivityEvent>();
            sessions.Bus.Activity += activities.Add;

            var prepared = await McpTools.SubmitWorkflowStep(engine, run.RunId, "step-1", "{\"items\":\"one, two\"}");

            Assert.Equal("active", prepared.Status);
            Assert.Equal("step-1#0", prepared.CurrentStep.StepId);
            Assert.Equal(new[] { "witness" }, prepared.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(0, run.StepIndex);
            Assert.All(run.Results, result => Assert.Empty(result.VerifyResults));
            Assert.All(run.Results, result => Assert.Empty(result.VerifyHistory));
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessRevisions));
            Assert.Null(run.SubmissionOrigin);
            Assert.Same(top.WitnessLocks, run.WitnessLocks);

            var activity = Assert.Single(activities, a => a.Kind == "submit_workflow_step");
            Assert.Equal("agent", activity.Origin);
            Assert.True(activity.Ok);
            Assert.Equal(run.Def.Name, activity.Target);
            Assert.Contains("submission accepted", activity.Label, StringComparison.Ordinal);
            Assert.DoesNotContain("step passed", activity.Label, StringComparison.Ordinal);
            Assert.Contains("now on step-1#0", activity.Label, StringComparison.Ordinal);
        }

        [Fact]
        public async System.Threading.Tasks.Task Mcp_ordinary_submit_uses_the_same_accepted_label_and_still_returns_the_next_current_or_terminal_view()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(new WorkflowDef
            {
                Name = "invented-mcp-ordinary-label",
                Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep { Id = "step-1", Number = 1, Title = "First" },
                    new WorkflowStep { Id = "step-2", Number = 2, Title = "Second" },
                },
            }, null);
            var activities = new List<ActivityEvent>();
            sessions.Bus.Activity += activities.Add;

            var advanced = await McpTools.SubmitWorkflowStep(engine, run.RunId, "step-1", null);
            Assert.Equal("active", advanced.Status);
            Assert.Equal("step-2", advanced.CurrentStep.StepId);
            var first = Assert.Single(activities, a => a.Kind == "submit_workflow_step");
            Assert.Contains("submission accepted", first.Label, StringComparison.Ordinal);
            Assert.DoesNotContain("step passed", first.Label, StringComparison.Ordinal);
            Assert.Contains("now on step-2", first.Label, StringComparison.Ordinal);

            activities.Clear();
            var done = await McpTools.SubmitWorkflowStep(engine, run.RunId, "step-2", null);
            Assert.Equal("completed", done.Status);
            Assert.Null(done.CurrentStep);
            var terminal = Assert.Single(activities, a => a.Kind == "submit_workflow_step");
            Assert.Contains("submission accepted", terminal.Label, StringComparison.Ordinal);
            Assert.DoesNotContain("step passed", terminal.Label, StringComparison.Ordinal);
            Assert.Contains("run COMPLETED", terminal.Label, StringComparison.Ordinal);
        }

        private sealed class FreeEntitlement : Semanticus.Engine.Entitlement.IEntitlement
        {
            public bool IsPro => false;
            public Semanticus.Engine.Entitlement.EntitlementInfo Info =>
                new Semanticus.Engine.Entitlement.EntitlementInfo { Tier = "free" };
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_loop_entry_expansion_splices_without_receipt_and_keeps_intervening_seeds()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(
                EarlierSourceLocalDef("invented-loop-entry-local"), null);
            var top = run.Frames[0];

            await engine.SubmitWorkflowStepAsync(run.RunId, "choose", "{\"regions\":\"North, South\"}", "human");
            await engine.SubmitWorkflowStepAsync(run.RunId, "middle", "{\"note\":\"intervening\"}", "human");
            var setup = WorkflowRunner.BuildView(run);
            Assert.Equal("review-region", setup.CurrentStep.StepId);
            Assert.Empty(setup.CurrentStep.Questions);
            Assert.Null(run.Results[2].PendingForEach);
            Assert.All(run.Results, result => Assert.Empty(result.VerifyHistory));
            Assert.All(run.Results, result => Assert.Empty(result.VerifyResults));
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));

            var coverage = new CoverageSurfaceLock();
            top.CoverageSurface = coverage;
            top.AnchorFormRepairCount = 4;
            Assert.Equal(12, ProofStoreNames.Length);
            var beforeTopStores = SnapshotAllTwelveFrameProofStores(top);
            var after = await engine.SubmitWorkflowStepAsync(run.RunId, "review-region", "{}", "human");
            Assert.Equal("review-region#0", after.CurrentStep.StepId);
            Assert.Equal("Authored body: review North at 0.", after.CurrentStep.Instructions);
            Assert.Equal(new[] { "finding" }, after.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(new[] { "edit_measure" }, after.CurrentStep.Ops);
            Assert.Equal(new[] { "invented_check" }, after.CurrentStep.VerifyKinds);
            Assert.Equal(new[] { "choose", "middle", "review-region#0", "review-region#1" }, run.Plan.Select(p => p.InstanceId));
            Assert.Same(top, run.Frames[0]);
            Assert.Equal(beforeTopStores, SnapshotAllTwelveFrameProofStores(top));
            Assert.Same(coverage, top.CoverageSurface);
            Assert.Equal(4, top.AnchorFormRepairCount);
            Assert.All(run.Frames.Skip(1), frame =>
            {
                Assert.Empty(frame.WitnessLocks);
                Assert.Empty(frame.WitnessRevisions);
                Assert.Empty(frame.PartitionLocks);
                Assert.Empty(frame.PartitionRevisions);
                Assert.Empty(frame.RunAnchorLocks);
                Assert.Empty(frame.AnchorRevisions);
                Assert.Null(frame.CoverageSurface);
                Assert.Empty(frame.CoverageSurfaceRevisions);
                Assert.Empty(frame.ShapeMismatchLedger);
                Assert.Empty(frame.ShapeMismatchCountersigns);
                Assert.Null(frame.LastPassedEquivalenceCandidate);
                Assert.Equal(0, frame.AnchorFormRepairCount);
            });
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessRevisions));
            Assert.All(run.Results, result => Assert.Empty(result.VerifyResults));
            Assert.All(run.Results, result => Assert.Empty(result.VerifyHistory));
            Assert.All(run.Results, result => Assert.Null(result.PendingForEach));
            Assert.Null(run.SubmissionOrigin);

            var first = new Dictionary<string, AnswerValue>();
            var second = new Dictionary<string, AnswerValue>();
            run.Frames[1].CopyAnswerSeedTo(first);
            run.Frames[2].CopyAnswerSeedTo(second);
            Assert.Equal("North, South", first["regions"].Value);
            Assert.Equal("North, South", second["regions"].Value);
            Assert.Equal("intervening", first["note"].Value);
            Assert.Equal("intervening", second["note"].Value);
            Assert.NotSame(first["regions"], second["regions"]);
            Assert.NotSame(first["note"], second["note"]);

            WorkflowStep LoopA(string scope) => new WorkflowStep
            {
                Id = "loop-a", Number = 1, Title = "Loop A",
                Instructions = "Authored body: [[loop.item]].",
                ForEach = new ForEachSpec { InLiteral = new[] { "one", "two" }, As = "item", MaxIterations = 4 },
                Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional", Scope = scope } } },
            };
            var middle = new WorkflowStep { Id = "middle", Number = 2, Title = "Middle" };
            var loopB = new WorkflowStep
            {
                Id = "review-region", Number = 3, Title = "Review invented region",
                Instructions = "Authored body: review [[loop.region]].",
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 4 },
            };

            var hidden = sessions.CurrentContext.WorkflowRuns.Start(new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-scope-hidden-local", Version = 2, Strictness = "off",
                Steps = new[] { LoopA(null), middle, loopB },
            }, null);
            await engine.SubmitWorkflowStepAsync(hidden.RunId, "loop-a", null, "human");
            await engine.SubmitWorkflowStepAsync(hidden.RunId, "loop-a#0", "{\"regions\":\"North\"}", "human");
            await engine.SubmitWorkflowStepAsync(hidden.RunId, "loop-a#1", null, "human");
            await engine.SubmitWorkflowStepAsync(hidden.RunId, "middle", null, "human");
            var hiddenStores = SnapshotAllTwelveFrameProofStores(hidden.Frames[0]);
            var hiddenError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.SubmitWorkflowStepAsync(hidden.RunId, "review-region", "{}", "human"));
            Assert.Contains("scope: run", hiddenError.Message, StringComparison.Ordinal);
            Assert.Contains("regions", hiddenError.Message, StringComparison.Ordinal);
            Assert.Equal(hiddenStores, SnapshotAllTwelveFrameProofStores(hidden.Frames[0]));
            Assert.All(hidden.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.Null(hidden.SubmissionOrigin);

            var visible = sessions.CurrentContext.WorkflowRuns.Start(new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-scope-run-local", Version = 2, Strictness = "off",
                Steps = new[] { LoopA("run"), middle, loopB },
            }, null);
            await engine.SubmitWorkflowStepAsync(visible.RunId, "loop-a", null, "human");
            await engine.SubmitWorkflowStepAsync(visible.RunId, "loop-a#0", "{\"regions\":\"North\"}", "human");
            await engine.SubmitWorkflowStepAsync(visible.RunId, "loop-a#1", "{\"regions\":\"South\"}", "human");
            await engine.SubmitWorkflowStepAsync(visible.RunId, "middle", null, "human");
            var visibleTop = visible.Frames[0];
            var visibleStores = SnapshotAllTwelveFrameProofStores(visibleTop);
            var visibleAfter = await engine.SubmitWorkflowStepAsync(visible.RunId, "review-region", "{}", "human");
            Assert.Contains(visible.Plan, p => p.InstanceId == "review-region#0");
            Assert.Equal("review-region#0", visibleAfter.CurrentStep.StepId);
            Assert.Same(visibleTop, visible.Frames[0]);
            Assert.Equal(visibleStores, SnapshotAllTwelveFrameProofStores(visibleTop));
            Assert.All(visible.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.All(visible.Results, result => Assert.Empty(result.VerifyHistory));
            Assert.Null(visible.SubmissionOrigin);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_loop_entry_projects_full_setup_view_then_restores_authored_metadata()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            var run = sessions.CurrentContext.WorkflowRuns.Start(
                EarlierSourceLocalDef("invented-loop-entry-setup"), null);
            await engine.SubmitWorkflowStepAsync(run.RunId, "choose", "{\"regions\":\"North, South\"}", "human");
            await engine.SubmitWorkflowStepAsync(run.RunId, "middle", "{\"note\":\"intervening\"}", "human");

            var setup = WorkflowRunner.BuildView(run);
            Assert.Equal("review-region", setup.CurrentStep.StepId);
            Assert.Equal("Review invented region", setup.CurrentStep.Title);
            Assert.Equal(DeferredSetupInstructions, setup.CurrentStep.Instructions);
            Assert.DoesNotContain("answered", setup.CurrentStep.Instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(setup.CurrentStep.Questions);
            Assert.Empty(setup.CurrentStep.VerifyKinds);
            Assert.Empty(setup.CurrentStep.Ops);
            Assert.Null(setup.CurrentStep.EffectiveStrictness);
            Assert.True(setup.TotalStepsProvisional);
            Assert.All(setup.Steps, step => Assert.Null(step.PendingForEach));

            var after = await engine.SubmitWorkflowStepAsync(run.RunId, "review-region", null, "human");
            Assert.Equal("review-region#0", after.CurrentStep.StepId);
            Assert.Equal("Review invented region", after.CurrentStep.Title);
            Assert.Equal("Authored body: review North at 0.", after.CurrentStep.Instructions);
            Assert.NotEqual(InlineSetupInstructions, after.CurrentStep.Instructions);
            Assert.NotEqual(DeferredSetupInstructions, after.CurrentStep.Instructions);
            Assert.NotEqual(PreparationSetupInstructions, after.CurrentStep.Instructions);
            Assert.Equal(new[] { "finding" }, after.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(new[] { "invented_check" }, after.CurrentStep.VerifyKinds);
            Assert.Equal(new[] { "edit_measure" }, after.CurrentStep.Ops);
            Assert.Null(run.SubmissionOrigin);
        }

        [Fact]
        public async System.Threading.Tasks.Task Local_loop_entry_refusals_leave_receipt_and_all_proof_stores_unchanged()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);

            var extra = sessions.CurrentContext.WorkflowRuns.Start(
                EarlierSourceLocalDef("invented-loop-entry-extra"), null);
            await engine.SubmitWorkflowStepAsync(extra.RunId, "choose", "{\"regions\":\"North, South\"}", "human");
            await engine.SubmitWorkflowStepAsync(extra.RunId, "middle", "{\"note\":\"intervening\"}", "human");
            var extraTop = extra.Frames[0];
            var extraStores = SnapshotAllTwelveFrameProofStores(extraTop);
            var extraPlan = extra.Plan.ToArray();
            var extraError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.SubmitWorkflowStepAsync(extra.RunId, "review-region", "{\"finding\":\"must not land\"}", "human"));
            Assert.Contains("finding", extraError.Message, StringComparison.Ordinal);
            Assert.Equal(extraPlan, extra.Plan);
            Assert.Equal(extraStores, SnapshotAllTwelveFrameProofStores(extraTop));
            Assert.All(extra.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.Null(extra.SubmissionOrigin);
            Assert.All(extra.Results, result => Assert.Null(result.PendingForEach));

            var over = sessions.CurrentContext.WorkflowRuns.Start(
                EarlierSourceLocalDef("invented-loop-entry-over", maxIterations: 1), null);
            await engine.SubmitWorkflowStepAsync(over.RunId, "choose", "{\"regions\":\"North, South\"}", "human");
            await engine.SubmitWorkflowStepAsync(over.RunId, "middle", "{\"note\":\"intervening\"}", "human");
            var overStores = SnapshotAllTwelveFrameProofStores(over.Frames[0]);
            var overError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.SubmitWorkflowStepAsync(over.RunId, "review-region", "{}", "human"));
            Assert.Contains("2", overError.Message, StringComparison.Ordinal);
            Assert.Contains("1", overError.Message, StringComparison.Ordinal);
            Assert.Contains("skip_workflow_step", overError.Message, StringComparison.Ordinal);
            Assert.Equal(overStores, SnapshotAllTwelveFrameProofStores(over.Frames[0]));
            Assert.Single(over.Frames);
            Assert.Null(over.SubmissionOrigin);

            var stale = sessions.CurrentContext.WorkflowRuns.Start(
                EarlierSourceLocalDef("invented-loop-entry-stale"), null);
            await engine.SubmitWorkflowStepAsync(stale.RunId, "choose", "{\"regions\":\"North\"}", "human");
            await engine.SubmitWorkflowStepAsync(stale.RunId, "middle", "{\"note\":\"intervening\"}", "human");
            var staleError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.SubmitWorkflowStepAsync(stale.RunId, "review-region#0", "{}", "human"));
            Assert.Contains("not the current step", staleError.Message, StringComparison.Ordinal);
            Assert.Equal(3, stale.Plan.Count);
            Assert.All(stale.Frames, frame => Assert.Empty(frame.WitnessLocks));
        }

        [Fact]
        public async System.Threading.Tasks.Task Loop_entry_source_answered_inside_an_iteration_frame_only_crosses_with_run_scope()
        {
            WorkflowStep LoopA(string scope) => new WorkflowStep
            {
                Id = "loop-a", Number = 1, Title = "Loop A",
                Instructions = "Authored body: [[loop.item]].",
                ForEach = new ForEachSpec { InLiteral = new[] { "one", "two" }, As = "item", MaxIterations = 4 },
                Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional", Scope = scope } } },
            };
            var middle = new WorkflowStep { Id = "middle", Number = 2, Title = "Middle" };
            var loopB = new WorkflowStep
            {
                Id = "review-region", Number = 3, Title = "Review invented region",
                Instructions = "Authored body: review [[loop.region]].",
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 4 },
            };

            var hidden = new WorkflowRunState("wfr-u18-scope-hidden", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-scope-hidden-entry", Version = 2, Strictness = "off",
                Steps = new[] { LoopA(null), middle, loopB },
            }, null);
            await WorkflowRunner.SubmitStepAsync(hidden, "loop-a", null, null);
            await WorkflowRunner.SubmitStepAsync(hidden, "loop-a#0", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(hidden, "loop-a#1", null, null);
            await WorkflowRunner.SubmitStepAsync(hidden, "middle", null, null);
            Assert.All(hidden.Results, result => Assert.Null(result.PendingForEach));
            var gap = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(hidden, "review-region", null, null));
            Assert.Contains("scope: run", gap.Message, StringComparison.Ordinal);
            Assert.Contains("regions", gap.Message, StringComparison.Ordinal);
            Assert.Contains("loop-a", gap.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("unanswered", gap.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(hidden.Plan[3].ForEachExpanded);
            Assert.All(hidden.Results, result => Assert.Null(result.PendingForEach));

            var visible = new WorkflowRunState("wfr-u18-scope-run", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-scope-run-entry", Version = 2, Strictness = "off",
                Steps = new[] { LoopA("run"), middle, loopB },
            }, null);
            await WorkflowRunner.SubmitStepAsync(visible, "loop-a", null, null);
            await WorkflowRunner.SubmitStepAsync(visible, "loop-a#0", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(visible, "loop-a#1", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "South" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(visible, "middle", null, null);
            await WorkflowRunner.SubmitStepAsync(visible, "review-region", null, null);
            Assert.Contains(visible.Plan, p => p.InstanceId == "review-region#0");
            Assert.Equal("South", visible.Frames.Last(f => f.ProjectionKind == "iteration").LoopValue);
            Assert.All(visible.Results, result => Assert.Null(result.PendingForEach));
        }

        [Fact]
        public void Acceptance_17b_moves_the_exact_twelve_store_set_behind_each_frame()
        {
            Assert.Equal(12, ProofStoreNames.Length);
            Assert.Equal(12, ProofStoreNames.Distinct(StringComparer.Ordinal).Count());

            var run = NewRun();
            var frame = Assert.Single(run.Frames);
            foreach (var storeName in ProofStoreNames)
            {
                var runProperty = typeof(WorkflowRunState).GetMember(storeName).OfType<PropertyInfo>().SingleOrDefault();
                var frameProperty = typeof(RunFrame).GetMember(storeName).OfType<PropertyInfo>().SingleOrDefault();
                Assert.NotNull(runProperty);
                Assert.NotNull(frameProperty);
                Assert.Equal(typeof(WorkflowRunState), runProperty.DeclaringType);
                Assert.Equal(typeof(RunFrame), frameProperty.DeclaringType);

                var frameValue = frameProperty.GetValue(frame);
                var runValue = runProperty.GetValue(run);
                if (frameProperty.PropertyType.IsValueType)
                    Assert.Equal(frameValue, runValue);
                else
                    Assert.Same(frameValue, runValue);
            }

            var coverage = new CoverageSurfaceLock();
            run.CoverageSurface = coverage;
            Assert.Same(coverage, frame.CoverageSurface);
            var candidate = new EquivalenceCandidateReceipt();
            frame.LastPassedEquivalenceCandidate = candidate;
            Assert.Same(candidate, run.LastPassedEquivalenceCandidate);
            run.AnchorFormRepairCount = 3;
            Assert.Equal(3, frame.AnchorFormRepairCount);
        }

        [Fact]
        public void Certificate_frames_align_with_the_run_view_frames_and_carry_no_frame_id()
        {
            var run = NewRun();
            run.CoverageSurface = new CoverageSurfaceLock
            {
                CurrentGrid = new[] { "'Product'[Category]" },
                CurrentOpenGrains = Array.Empty<string>(),
            };
            var surfaced = RunFrame.CreateIteration(
                "iteration-surfaced", "step-1", 0, "table", "Sales", "passed");
            surfaced.CoverageSurface = new CoverageSurfaceLock
            {
                CurrentGrid = new[] { "'Product'[Category]" },
                CurrentOpenGrains = Array.Empty<string>(),
            };
            var surfaceless = RunFrame.CreateIteration(
                "iteration-surfaceless", "step-1", 1, "table", "Finance", "passed");
            var call = RunFrame.CreateCall(
                "call-surfaceless", "prove-it", "verified-measure", 1,
                new[] { "targetMeasure" }, new[] { "certificate" }, "passed");
            run.Frames.Add(surfaced);
            run.Frames.Add(surfaceless);
            run.Frames.Add(call);
            run.Plan[0] = new PlannedStep(run.Plan[0].Step, "step-1#0", surfaced.FrameId, 0);
            run.Plan[1] = new PlannedStep(run.Plan[1].Step, "prove-it/step-1", call.FrameId, null);
            run.Results[0].StepId = "step-1#0";
            run.Results[0].Status = "passed";
            run.Results[1].StepId = "prove-it/step-1";
            run.Results[1].Status = "passed";
            run.Plan.Add(new PlannedStep(run.Plan[0].Step, "step-1#1", surfaceless.FrameId, 1));
            run.Results.Add(new StepResult { StepId = "step-1#1", Title = "First", Status = "passed" });
            // A certificate is a terminal artifact, so the run is terminal here. It also has to be: these rows are
            // hand-built iteration instances of a step carrying no forEach:, which is coherent enough for the fold
            // (it selects rows by FrameId alone) but not for current-step instruction rendering, and BuildView
            // refuses an incoherent current row rather than rendering it.
            run.Status = "completed";
            run.StepIndex = run.Plan.Count;

            var certificate = WorkflowRunner.ComputeCertificate(run);
            var view = WorkflowRunner.BuildView(run);
            Assert.Equal(view.Frames.Length, certificate.Frames.Length);
            Assert.Equal(3, certificate.Frames.Length);
            for (var i = 0; i < view.Frames.Length; i++)
            {
                Assert.Equal(view.Frames[i].Kind, certificate.Frames[i].Kind);
                Assert.Equal(view.Frames[i].StepId, certificate.Frames[i].StepId);
                Assert.Equal(view.Frames[i].State, certificate.Frames[i].State);
                Assert.Equal(view.Frames[i].IterationIndex, certificate.Frames[i].IterationIndex);
                Assert.Equal(view.Frames[i].LoopVariable, certificate.Frames[i].LoopVariable);
                Assert.Equal(view.Frames[i].LoopValue, certificate.Frames[i].LoopValue);
                Assert.Equal(view.Frames[i].Workflow, certificate.Frames[i].Workflow);
                Assert.Equal(view.Frames[i].Depth, certificate.Frames[i].Depth);
                Assert.Equal(view.Frames[i].Passed, certificate.Frames[i].Passed);
                Assert.Equal(view.Frames[i].Returned, certificate.Frames[i].Returned);
            }

            Assert.Null(typeof(CertificateFrame).GetProperty("FrameId"));
            Assert.DoesNotContain(typeof(CertificateFrame).GetProperties(),
                p => p.PropertyType == typeof(RunFrame) || p.Name.IndexOf("FrameId", StringComparison.Ordinal) >= 0);

            var stj = System.Text.Json.JsonSerializer.Serialize(certificate);
            var newtonsoft = JsonConvert.SerializeObject(certificate);
            Assert.DoesNotContain("iteration-surfaced", stj, StringComparison.Ordinal);
            Assert.DoesNotContain("iteration-surfaceless", stj, StringComparison.Ordinal);
            Assert.DoesNotContain("call-surfaceless", stj, StringComparison.Ordinal);
            Assert.DoesNotContain("FrameId", stj, StringComparison.Ordinal);
            Assert.DoesNotContain("iteration-surfaced", newtonsoft, StringComparison.Ordinal);
            Assert.DoesNotContain("FrameId", newtonsoft, StringComparison.Ordinal);
        }

        [Fact]
        public void Fold_reads_every_frame_for_each_certificate_input_store()
        {
            Assert.Equal(6, CertificateInputStores.Length);
            Assert.Equal(CertificateInputStores, CertificateInputStores.Distinct(StringComparer.Ordinal));
            Assert.All(CertificateInputStores, name =>
            {
                Assert.Contains(name, ProofStoreNames);
                Assert.NotNull(typeof(RunFrame).GetProperty(name));
                Assert.NotNull(typeof(WorkflowRunState).GetProperty(name));
            });
            Assert.True(CertificateInputStores.All(name => ProofStoreNames.Contains(name)));

            var run = NewRun();
            run.CoverageSurface = new CoverageSurfaceLock
            {
                CurrentGrid = new[] { "'Product'[Category]" },
                CurrentOpenGrains = Array.Empty<string>(),
            };
            var first = RunFrame.CreateIteration("iter-0", "step-1", 0, "table", "Sales", "passed");
            var second = RunFrame.CreateIteration("iter-1", "step-1", 1, "table", "Finance", "passed");
            first.CoverageSurface = new CoverageSurfaceLock
            {
                CurrentGrid = new[] { "'Product'[Category]" },
                CurrentOpenGrains = Array.Empty<string>(),
            };
            second.CoverageSurface = new CoverageSurfaceLock
            {
                CurrentGrid = new[] { "'Date'[Year]" },
                CurrentOpenGrains = Array.Empty<string>(),
            };
            first.CoverageSurfaceRevisions.Add(new CoverageSurfaceRevision { StepId = "sales" });
            second.CoverageSurfaceRevisions.Add(new CoverageSurfaceRevision { StepId = "finance" });
            first.ShapeMismatchLedger["grand_total"] = new ShapeMismatchLedgerEntry
            {
                ShapeId = "grand_total", Open = true, State = "DISPUTED",
                Cells = new[] { new ShapeMismatchLedgerCell { Coordinate = "sales-cell", CandidateValue = "1", WitnessValue = "0" } },
            };
            second.ShapeMismatchLedger["grand_total"] = new ShapeMismatchLedgerEntry
            {
                ShapeId = "grand_total", Open = true, State = "DISPUTED",
                Cells = new[] { new ShapeMismatchLedgerCell { Coordinate = "finance-cell", CandidateValue = "2", WitnessValue = "0" } },
            };
            first.ShapeMismatchCountersigns.Add(new ShapeMismatchCountersign { ShapeId = "grand_total", Coordinate = "sales-sign", CandidateValue = "1", WitnessValue = "0" });
            second.ShapeMismatchCountersigns.Add(new ShapeMismatchCountersign { ShapeId = "grand_total", Coordinate = "finance-sign", CandidateValue = "2", WitnessValue = "0" });
            first.AnchorRevisions.Add(new AnchorRevision { StepId = "sales" });
            second.AnchorRevisions.Add(new AnchorRevision { StepId = "finance" });
            second.AnchorRevisions.Add(new AnchorRevision { StepId = "finance" });
            first.AnchorFormRepairCount = 1;
            second.AnchorFormRepairCount = 2;
            run.Frames.Add(first);
            run.Frames.Add(second);
            run.Plan[0] = new PlannedStep(run.Plan[0].Step, "step-1#0", first.FrameId, 0);
            run.Plan[1] = new PlannedStep(run.Plan[1].Step, "step-1#1", second.FrameId, 1);
            run.Results[0].StepId = "step-1#0";
            run.Results[0].Status = "passed";
            run.Results[1].StepId = "step-1#1";
            run.Results[1].Status = "passed";

            var certificate = WorkflowRunner.ComputeCertificate(run);
            Assert.Equal(new[] { "'Product'[Category]" }, certificate.Frames[0].GridColumns);
            Assert.Equal(new[] { "'Date'[Year]" }, certificate.Frames[1].GridColumns);
            Assert.Equal(new[] { "sales", "finance" }, certificate.CoverageSurfaceRevisions.Select(x => x.StepId));
            Assert.Equal(new[] { "sales-cell", "finance-cell" }, certificate.DisputedCells.Select(x => x.Coordinate));
            Assert.Equal(new[] { "sales-sign", "finance-sign" }, certificate.CountersignedCells.Select(x => x.Coordinate));
            Assert.Equal(3, certificate.AnchorRevisionCount);
            Assert.Equal(3, certificate.FormRepairCount);
        }

        [Fact]
        public void Fold_reads_no_non_certificate_store()
        {
            var excluded = ProofStoreNames.Except(CertificateInputStores, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[]
            {
                "WitnessLocks",
                "WitnessRevisions",
                "PartitionLocks",
                "PartitionRevisions",
                "RunAnchorLocks",
                "LastPassedEquivalenceCandidate",
            }, excluded);

            var forbidden = new[]
            {
                typeof(EquivalenceCandidateReceipt),
                typeof(WitnessRevision[]),
                typeof(PartitionRevision[]),
                typeof(AnchorRunLock),
                typeof(AnchorLockView[]),
            };
            foreach (var type in new[] { typeof(WorkflowCertificate), typeof(CertificateFrame) })
            {
                Assert.DoesNotContain(type.GetProperties(), p => forbidden.Contains(p.PropertyType));
            }

            var run = NewRun();
            run.CoverageSurface = new CoverageSurfaceLock
            {
                CurrentGrid = new[] { "'Product'[Category]" },
                CurrentOpenGrains = Array.Empty<string>(),
            };
            var first = RunFrame.CreateIteration("iter-0", "step-1", 0, "table", "Sales", "passed");
            var second = RunFrame.CreateIteration("iter-1", "step-1", 1, "table", "Finance", "passed");
            first.LastPassedEquivalenceCandidate = new EquivalenceCandidateReceipt { TargetLineageTag = "sales-lineage" };
            second.LastPassedEquivalenceCandidate = new EquivalenceCandidateReceipt { TargetLineageTag = "finance-lineage" };
            first.CoverageSurface = run.CoverageSurface;
            run.Frames.Add(first);
            run.Frames.Add(second);
            run.Plan[0] = new PlannedStep(run.Plan[0].Step, "step-1#0", first.FrameId, 0);
            run.Plan[1] = new PlannedStep(run.Plan[1].Step, "step-1#1", second.FrameId, 1);
            run.Results[0].Status = "passed";
            run.Results[1].Status = "passed";

            var certificate = WorkflowRunner.ComputeCertificate(run);
            var json = System.Text.Json.JsonSerializer.Serialize(certificate);
            Assert.DoesNotContain("sales-lineage", json, StringComparison.Ordinal);
            Assert.DoesNotContain("finance-lineage", json, StringComparison.Ordinal);
        }
    }
}
