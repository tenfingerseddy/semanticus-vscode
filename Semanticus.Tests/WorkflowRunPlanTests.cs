using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class WorkflowRunPlanTests
    {
        private static WorkflowDef NewMeasureSeed()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "workflows", "new-measure.md");
            Assert.True(File.Exists(path), $"stock workflow was not copied beside the test binary: {path}");
            var def = WorkflowParser.ParseFile(path, "stock");
            Assert.Null(def.Error);
            return def;
        }

        [Fact]
        public void A_new_run_has_one_mutable_planned_entry_and_aligned_result_per_definition_step()
        {
            var def = NewMeasureSeed();
            var run = new WorkflowRunState("wfr-plan", def, null);

            Assert.IsType<List<PlannedStep>>(run.Plan);
            Assert.IsType<List<StepResult>>(run.Results);
            Assert.Equal(def.Steps.Length, run.Plan.Count);
            Assert.Equal(run.Plan.Count, run.Results.Count);
            for (var i = 0; i < run.Plan.Count; i++)
            {
                // [T220] The run owns a FROZEN COPY of the definition, so a plan row points at the copy's
                // step, never at the mutable input the caller still holds.
                Assert.NotSame(def, run.Def);
                Assert.Same(run.Def.Steps[i], run.Plan[i].Step);
                Assert.NotSame(def.Steps[i], run.Plan[i].Step);
                Assert.Equal(def.Steps[i].Id, run.Plan[i].InstanceId);
                Assert.Equal(run.RunId, run.Plan[i].FrameId);
                Assert.Null(run.Plan[i].IterationIndex);
                Assert.Equal(run.Plan[i].InstanceId, run.Results[i].StepId);
            }

            run.Plan.Add(new PlannedStep(run.Def.Steps[0], "step-1#1", run.RunId, 1));
            run.Results.Add(new StepResult { StepId = "step-1#1", Title = def.Steps[0].Title, Status = "pending" });
            Assert.Equal(run.Plan.Count, run.Results.Count);
            Assert.Equal(def.Steps.Length + 1, WorkflowRunner.BuildView(run).TotalSteps);
        }

        [Fact]
        public void Current_step_advance_view_total_and_certificate_strictness_follow_the_plan()
        {
            var definitionStep = new WorkflowStep { Id = "step-1", Number = 1, Title = "Definition step" };
            var plannedSource = new WorkflowStep
            {
                Id = "step-1",
                Number = 1,
                Title = "Planned source",
                Gate = new GateSpec { Verify = new[] { new VerifySpec { Kind = "workflow_admissible" } } },
            };
            var run = new WorkflowRunState("wfr-repoint", new WorkflowDef
            {
                Name = "plan-repoint",
                Strictness = "hard",
                Steps = new[] { definitionStep },
            }, null);
            run.Plan[0] = WorkflowOwnerFixtures.ForeignRow(plannedSource, plannedSource.Id, run.RunId,
                ownerName: "plan-repoint-source", strictness: "hard");
            plannedSource = run.Plan[0].Step;
            run.Results[0].Title = plannedSource.Title;
            run.Results[0].Answers["certificate"] = new AnswerValue { Value = "FULL" };
            run.CoverageSurface = new CoverageSurfaceLock
            {
                CurrentGrid = Array.Empty<string>(),
                CurrentOpenGrains = Array.Empty<string>(),
            };

            Assert.Same(plannedSource, run.CurrentStep);
            Assert.Equal(1, WorkflowRunner.BuildView(run).TotalSteps);

            WorkflowRunner.SkipStep(run, "step-1", "fixture accepts the missing proof");

            Assert.Equal("completed", run.Status);
            Assert.Equal("OVERRIDDEN", run.Certificate.ComputedLevel);
            Assert.Contains(WorkflowRunner.OverriddenCertificateConsequence, run.Results[0].Note);
        }

        [Fact]
        public void Loop_total_is_provisional_until_only_expanded_iteration_instances_remain()
        {
            var loop = new WorkflowStep
            {
                Id = "step-2",
                Number = 2,
                Title = "Review invented regions",
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 4 },
            };
            var run = new WorkflowRunState("wfr-provisional", new WorkflowDef
            {
                Name = "invented-loop",
                Version = 2,
                Steps = new[]
                {
                    new WorkflowStep { Id = "step-1", Number = 1, Title = "Choose invented regions" },
                    loop,
                },
            }, null);

            var provisional = WorkflowRunner.BuildView(run);
            Assert.True(provisional.TotalStepsProvisional);
            Assert.Equal(2, provisional.TotalSteps);
            Assert.Contains("\"TotalStepsProvisional\":true", JsonSerializer.Serialize(provisional));
            Assert.Contains("\"TotalStepsProvisional\":true", Newtonsoft.Json.JsonConvert.SerializeObject(provisional));

            run.Plan.RemoveAt(1);
            run.Results.RemoveAt(1);
            for (var i = 0; i < 2; i++)
            {
                run.Plan.Add(new PlannedStep(run.Def.Steps[1], $"step-2#{i}", $"wfr-provisional:step-2:{i}", i));
                run.Results.Add(new StepResult { StepId = $"step-2#{i}", Title = loop.Title, Status = "pending" });
            }

            var expanded = WorkflowRunner.BuildView(run);
            Assert.False(expanded.TotalStepsProvisional);
            Assert.Equal(3, expanded.TotalSteps);
            Assert.Contains("\"TotalStepsProvisional\":false", JsonSerializer.Serialize(expanded));
            Assert.Contains("\"TotalStepsProvisional\":false", Newtonsoft.Json.JsonConvert.SerializeObject(expanded));
        }

        [Fact]
        public void Plan_injected_loop_marks_the_total_provisional_when_definition_has_no_loop()
        {
            var definitionStep = new WorkflowStep { Id = "step-1", Number = 1, Title = "Call placeholder" };
            var injectedLoop = new WorkflowStep
            {
                Id = "called-step",
                Number = 1,
                Title = "Review invented regions",
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 4 },
            };
            var run = new WorkflowRunState("wfr-injected-loop", new WorkflowDef
            {
                Name = "invented-caller",
                Version = 2,
                Steps = new[] { definitionStep },
            }, null);
            var call = RunFrame.CreateCall("wfr-injected-loop:call", definitionStep.Id, "invented-callee", 2,
                Array.Empty<string>(), Array.Empty<string>(), "in_progress");
            run.Frames.Add(call);
            run.Plan[0] = WorkflowOwnerFixtures.ForeignRow(injectedLoop, injectedLoop.Id, call.FrameId,
                ownerName: "invented-callee");
            run.Results[0].StepId = injectedLoop.Id;

            Assert.True(WorkflowRunner.BuildView(run).TotalStepsProvisional);
        }

        [Fact]
        public void Three_value_middle_loop_splices_aligned_plan_results_and_ordered_frames_in_place()
        {
            var first = new WorkflowStep { Id = "prepare", Number = 1, Title = "Prepare invented regions" };
            var loop = new WorkflowStep
            {
                Id = "review-region",
                Number = 2,
                Title = "Review invented region",
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 4 },
            };
            var last = new WorkflowStep { Id = "finish", Number = 3, Title = "Finish invented review" };
            var run = new WorkflowRunState("wfr-splice", new WorkflowDef
            {
                Name = "invented-middle-loop",
                Version = 2,
                Steps = new[] { first, loop, last },
            }, null) { StepIndex = 1 };
            run.Results[0].Status = "passed";
            run.Results[1].Status = "in_progress";
            var beforePlan = run.Plan[0];
            var afterPlan = run.Plan[2];
            var beforeResult = run.Results[0];
            var afterResult = run.Results[2];

            WorkflowRunner.ExpandCurrentForEach(run, new[] { "North", "South", "West" });

            Assert.Equal(1, run.StepIndex);
            Assert.Equal(5, run.Plan.Count);
            Assert.Equal(run.Plan.Count, run.Results.Count);
            Assert.Same(beforePlan, run.Plan[0]);
            Assert.Same(afterPlan, run.Plan[4]);
            Assert.Same(beforeResult, run.Results[0]);
            Assert.Same(afterResult, run.Results[4]);
            for (var i = 0; i < 3; i++)
            {
                var planned = run.Plan[i + 1];
                Assert.Same(run.Def.Steps[1], planned.Step);
                Assert.Equal($"review-region#{i}", planned.InstanceId);
                Assert.Equal($"wfr-splice:review-region:{i}", planned.FrameId);
                Assert.Equal(i, planned.IterationIndex);
                Assert.True(planned.ForEachExpanded);
                Assert.Equal(planned.InstanceId, run.Results[i + 1].StepId);
                Assert.Equal(i == 0 ? "in_progress" : "pending", run.Results[i + 1].Status);
            }
            Assert.Equal(new[] { "North", "South", "West" }, run.Frames.Skip(1).Select(f => f.LoopValue));
            Assert.All(run.Frames.Skip(1), frame =>
            {
                Assert.Equal("iteration", frame.ProjectionKind);
                Assert.Equal("review-region", frame.StepId);
                Assert.Equal("region", frame.LoopVariable);
                Assert.Equal("in_progress", frame.State);
            });
            Assert.False(WorkflowRunner.BuildView(run).TotalStepsProvisional);
        }

        [Fact]
        public void Repeated_authored_loop_occurrences_extend_their_distinct_planned_identities()
        {
            var loop = new WorkflowStep
            {
                Id = "review-region",
                Number = 1,
                Title = "Review invented region",
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 2 },
            };
            var run = new WorkflowRunState("wfr-repeated-loop", new WorkflowDef
            {
                Name = "invented-repeated-loop",
                Version = 2,
                Steps = new[] { loop },
            }, null);
            run.Frames.Add(RunFrame.CreateCall("wfr-repeated-loop:call-a", "call-a", "invented-callee", 2,
                Array.Empty<string>(), Array.Empty<string>(), "in_progress"));
            run.Frames.Add(RunFrame.CreateCall("wfr-repeated-loop:call-b", "call-b", "invented-callee", 2,
                Array.Empty<string>(), Array.Empty<string>(), "in_progress"));
            run.Plan.Clear();
            // Both call rows are the SAME callee, so they share one frozen owner: repeated reachability of one
            // workflow name reuses one frozen definition rather than freezing it twice.
            var calleeOwner = WorkflowOwnerFixtures.FrozenOwner("invented-callee", null, loop);
            run.Plan.Add(new PlannedStep(calleeOwner.Steps[0], "call-a/review-region", "wfr-repeated-loop:call-a", null, calleeOwner));
            run.Plan.Add(new PlannedStep(calleeOwner.Steps[0], "call-b/review-region", "wfr-repeated-loop:call-b", null, calleeOwner));
            run.Results.Clear();
            run.Results.Add(new StepResult { StepId = "call-a/review-region", Title = loop.Title, Status = "in_progress" });
            run.Results.Add(new StepResult { StepId = "call-b/review-region", Title = loop.Title, Status = "pending" });

            WorkflowRunner.ExpandCurrentForEach(run, new[] { "North", "South" });
            run.StepIndex = 2;
            run.Results[2].Status = "in_progress";
            WorkflowRunner.ExpandCurrentForEach(run, new[] { "East", "West" });

            Assert.Equal(new[]
            {
                "call-a/review-region#0", "call-a/review-region#1",
                "call-b/review-region#0", "call-b/review-region#1",
            }, run.Plan.Select(p => p.InstanceId));
            Assert.Equal(run.Plan.Select(p => p.InstanceId), run.Results.Select(r => r.StepId));
            Assert.Equal(new[]
            {
                "wfr-repeated-loop:call-a:call-a/review-region:0",
                "wfr-repeated-loop:call-a:call-a/review-region:1",
                "wfr-repeated-loop:call-b:call-b/review-region:0",
                "wfr-repeated-loop:call-b:call-b/review-region:1",
            }, run.Plan.Select(p => p.FrameId));
            Assert.Equal(4, run.Plan.Select(p => p.InstanceId).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(4, run.Results.Select(r => r.StepId).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(4, run.Plan.Select(p => p.FrameId).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(run.Plan.Count, run.Results.Count);

            var iterations = run.Frames.Where(f => f.ProjectionKind == "iteration").ToArray();
            Assert.Equal(new[] { "North", "South", "East", "West" }, iterations.Select(f => f.LoopValue));
            Assert.Equal(run.Plan.Select(p => p.FrameId), iterations.Select(f => f.FrameId));
            Assert.All(iterations, frame => Assert.Equal(loop.Id, frame.StepId));
        }

        [Fact]
        public void Iteration_frames_receive_deep_isolated_loop_entry_answer_seeds()
        {
            var prior = new WorkflowStep { Id = "prepare", Number = 1, Title = "Prepare" };
            var loop = new WorkflowStep
            {
                Id = "repeat",
                Number = 2,
                Title = "Repeat",
                ForEach = new ForEachSpec { InLiteral = new[] { "A", "B" }, As = "item", MaxIterations = 2 },
            };
            var run = new WorkflowRunState("wfr-seeds", new WorkflowDef
            {
                Name = "invented-seeds",
                Version = 2,
                Steps = new[] { prior, loop },
            }, null) { StepIndex = 1 };
            run.Results[0].Status = "passed";
            run.Results[0].Answers["ticket"] = new AnswerValue { Value = "CASE-42", DeclineReason = "invented note" };
            run.Results[1].Status = "in_progress";

            WorkflowRunner.ExpandCurrentForEach(run, new[] { "A", "B" });
            var firstSeed = new Dictionary<string, AnswerValue>();
            var secondSeed = new Dictionary<string, AnswerValue>();
            run.Frames[1].CopyAnswerSeedTo(firstSeed);
            run.Frames[2].CopyAnswerSeedTo(secondSeed);
            firstSeed["ticket"].Value = "changed";
            firstSeed["ticket"].DeclineReason = "changed";

            Assert.Equal("CASE-42", secondSeed["ticket"].Value);
            Assert.Equal("invented note", secondSeed["ticket"].DeclineReason);
            var firstSeedAgain = new Dictionary<string, AnswerValue>();
            run.Frames[1].CopyAnswerSeedTo(firstSeedAgain);
            Assert.Equal("CASE-42", firstSeedAgain["ticket"].Value);
        }

        [Fact]
        public void Over_bound_loop_expansion_refuses_before_any_mutation()
        {
            var loop = new WorkflowStep
            {
                Id = "bounded",
                Number = 1,
                Title = "Bounded invented loop",
                ForEach = new ForEachSpec { InInput = "items", As = "item", MaxIterations = 2 },
            };
            var run = new WorkflowRunState("wfr-bound", new WorkflowDef
            {
                Name = "invented-bound",
                Version = 2,
                Steps = new[] { loop },
            }, null);
            var plan = run.Plan.ToArray();
            var results = run.Results.ToArray();
            var statuses = run.Results.Select(r => r.Status).ToArray();
            var frames = run.Frames.ToArray();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                WorkflowRunner.ExpandCurrentForEach(run, new[] { "one", "two", "three" }));

            Assert.Contains("3", ex.Message);
            Assert.Contains("2", ex.Message);
            Assert.Equal(0, run.StepIndex);
            Assert.Equal(plan, run.Plan);
            Assert.Equal(results, run.Results);
            Assert.Equal(statuses, run.Results.Select(r => r.Status));
            Assert.Equal(frames, run.Frames);
        }

        [Fact]
        public void Empty_loop_stays_plan_aligned_records_not_applicable_and_advances_with_exact_total()
        {
            var loop = new WorkflowStep
            {
                Id = "empty-loop",
                Number = 1,
                Title = "Empty invented loop",
                ForEach = new ForEachSpec { InLiteral = Array.Empty<string>(), As = "item", MaxIterations = 3 },
            };
            var next = new WorkflowStep { Id = "next", Number = 2, Title = "Next" };
            var run = new WorkflowRunState("wfr-empty", new WorkflowDef
            {
                Name = "invented-empty",
                Version = 2,
                Steps = new[] { loop, next },
            }, null);
            var sourceResult = run.Results[0];

            WorkflowRunner.ExpandCurrentForEach(run, Array.Empty<string>());

            Assert.Equal(2, run.Plan.Count);
            Assert.Equal(run.Plan.Count, run.Results.Count);
            Assert.Same(run.Def.Steps[0], run.Plan[0].Step);
            Assert.Same(sourceResult, run.Results[0]);
            Assert.True(run.Plan[0].ForEachExpanded);
            Assert.Null(run.Plan[0].IterationIndex);
            Assert.Equal("not_applicable", run.Results[0].Status);
            Assert.Contains("empty", run.Results[0].Note, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, run.StepIndex);
            Assert.Equal("in_progress", run.Results[1].Status);
            Assert.False(WorkflowRunner.BuildView(run).TotalStepsProvisional);
        }

        [Fact]
        public void Second_expansion_refuses_without_mutating_the_expanded_run()
        {
            var loop = new WorkflowStep
            {
                Id = "once",
                Number = 1,
                Title = "Expand once",
                ForEach = new ForEachSpec { InLiteral = new[] { "only" }, As = "item", MaxIterations = 2 },
            };
            var run = new WorkflowRunState("wfr-once", new WorkflowDef
            {
                Name = "invented-once",
                Version = 2,
                Steps = new[] { loop },
            }, null);
            WorkflowRunner.ExpandCurrentForEach(run, new[] { "only" });
            var plan = run.Plan.ToArray();
            var results = run.Results.ToArray();
            var frames = run.Frames.ToArray();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                WorkflowRunner.ExpandCurrentForEach(run, new[] { "again" }));

            Assert.Contains("already expanded", ex.Message);
            Assert.Equal(plan, run.Plan);
            Assert.Equal(results, run.Results);
            Assert.Equal(frames, run.Frames);
            Assert.Equal(0, run.StepIndex);
        }

        [Fact]
        public void Expansion_precondition_refusals_leave_every_run_collection_and_status_untouched()
        {
            static void AssertAtomicRefusal(WorkflowRunState run, IReadOnlyList<string> values)
            {
                var plan = run.Plan.ToArray();
                var results = run.Results.ToArray();
                var statuses = run.Results.Select(r => r.Status).ToArray();
                var frames = run.Frames.ToArray();
                var stepIndex = run.StepIndex;
                var runStatus = run.Status;
                Assert.ThrowsAny<Exception>(() => WorkflowRunner.ExpandCurrentForEach(run, values));
                Assert.Equal(plan, run.Plan);
                Assert.Equal(results, run.Results);
                Assert.Equal(statuses, run.Results.Select(r => r.Status));
                Assert.Equal(frames, run.Frames);
                Assert.Equal(stepIndex, run.StepIndex);
                Assert.Equal(runStatus, run.Status);
            }

            var plain = new WorkflowRunState("wfr-plain", new WorkflowDef
            {
                Name = "invented-plain",
                Steps = new[] { new WorkflowStep { Id = "plain", Number = 1, Title = "Plain" } },
            }, null);
            AssertAtomicRefusal(plain, new[] { "x" });

            var loop = new WorkflowStep
            {
                Id = "loop",
                Number = 1,
                Title = "Loop",
                ForEach = new ForEachSpec { InLiteral = new[] { "x" }, As = "item", MaxIterations = 2 },
            };
            var iteration = new WorkflowRunState("wfr-iteration", new WorkflowDef
            {
                Name = "invented-iteration",
                Version = 2,
                Steps = new[] { loop },
            }, null);
            iteration.Plan[0] = new PlannedStep(loop, "loop#0", "wfr-iteration:loop:0", 0);
            AssertAtomicRefusal(iteration, new[] { "x" });

            var terminal = new WorkflowRunState("wfr-terminal", new WorkflowDef
            {
                Name = "invented-terminal",
                Version = 2,
                Steps = new[] { loop },
            }, null) { Status = "completed" };
            AssertAtomicRefusal(terminal, new[] { "x" });

            var nullValues = new WorkflowRunState("wfr-null", new WorkflowDef
            {
                Name = "invented-null",
                Version = 2,
                Steps = new[] { loop },
            }, null);
            AssertAtomicRefusal(nullValues, null!);
        }

        // ---- unit 14: freeze a self-declared input-backed list source on synthetic rows ----------

        [Fact]
        public void Self_declared_input_list_answer_is_deep_cloned_into_each_iteration_result()
        {
            var control = new AnswerValue { Value = "North, South", DeclineReason = "invented control note" };
            var loop = new WorkflowStep
            {
                Id = "review-region",
                Number = 1,
                Title = "Review invented region",
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 4 },
                Gate = new GateSpec
                {
                    Inputs = new[]
                    {
                        new GateInput { Name = "regions", Question = "Which invented regions?", Required = "optional" },
                        new GateInput { Name = "finding", Question = "What was found?", Required = "optional" },
                    },
                },
            };
            var run = new WorkflowRunState("wfr-frozen-source", new WorkflowDef
            {
                Name = "invented-frozen-source",
                Version = 2,
                Strictness = "off",
                Steps = new[] { loop },
            }, null);
            run.Results[0].Answers["regions"] = control;

            WorkflowRunner.ExpandCurrentForEach(run, new[] { "North", "South" });

            Assert.Equal(2, run.Results.Count);
            var first = Assert.Single(run.Results[0].Answers).Value;
            var second = Assert.Single(run.Results[1].Answers).Value;
            Assert.Equal("North, South", first.Value);
            Assert.Equal("invented control note", first.DeclineReason);
            Assert.NotSame(control, first);
            Assert.NotSame(control, second);
            Assert.NotSame(first, second);
            first.Value = "changed";
            first.DeclineReason = "changed";
            Assert.Equal("North, South", second.Value);
            Assert.Equal("invented control note", second.DeclineReason);
            Assert.Equal("North, South", control.Value);
        }

        [Fact]
        public void Self_declared_input_backed_loop_without_an_answered_entry_seed_refuses_atomically()
        {
            static void AssertRefusal(AnswerValue? sourceAnswer)
            {
                var step = new WorkflowStep
                {
                    Id = "step-1", Number = 1, Title = "Refuse missing source",
                    ForEach = new ForEachSpec { InInput = "items", As = "item", MaxIterations = 2 },
                    Gate = new GateSpec
                    {
                        Inputs = new[] { new GateInput { Name = "items", Required = "optional" } },
                    },
                };
                var run = new WorkflowRunState("invented-missing-source-run", new WorkflowDef
                {
                    Name = "invented-missing-source-fixture", Steps = new[] { step },
                }, null);
                if (sourceAnswer != null) run.Results[0].Answers["items"] = sourceAnswer;
                var plan = run.Plan.ToArray();
                var results = run.Results.ToArray();
                var statuses = run.Results.Select(result => result.Status).ToArray();
                var frames = run.Frames.ToArray();
                var answers = run.Results[0].Answers;
                var note = run.Results[0].Note;

                var error = Assert.Throws<InvalidOperationException>(() =>
                    WorkflowRunner.ExpandCurrentForEach(run, new[] { "one" }));

                Assert.Contains("answered", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(plan, run.Plan);
                Assert.Equal(results, run.Results);
                Assert.Equal(statuses, run.Results.Select(result => result.Status));
                Assert.Equal(frames, run.Frames);
                Assert.Same(answers, run.Results[0].Answers);
                Assert.Equal(note, run.Results[0].Note);
                Assert.Equal(0, run.StepIndex);
                Assert.Equal("active", run.Status);
            }

            AssertRefusal(null);
            AssertRefusal(new AnswerValue { Value = null });
            AssertRefusal(new AnswerValue { Declined = true, DeclineReason = "invented decline" });
        }

        [Fact]
        public void Earlier_declared_list_source_is_not_duplicated_into_iteration_results()
        {
            var run = InputBackedRun("wfr-earlier-source", out _);
            run.Results[0].Status = "passed";
            run.Results[0].Answers["regions"] = new AnswerValue { Value = "North, South" };
            run.StepIndex = 1;
            run.Results[1].Status = "in_progress";

            WorkflowRunner.ExpandCurrentForEach(run, new[] { "North", "South" });

            Assert.Equal("North, South", run.Results[0].Answers["regions"].Value);
            Assert.Empty(run.Results[1].Answers);
            Assert.Empty(run.Results[2].Answers);
        }

        [Fact]
        public void Empty_self_declared_loop_retains_its_source_answer_on_the_surviving_result()
        {
            var loop = new WorkflowStep
            {
                Id = "empty-input-loop",
                Number = 1,
                Title = "Empty invented input loop",
                ForEach = new ForEachSpec { InInput = "items", As = "item", MaxIterations = 3 },
                Gate = new GateSpec
                {
                    Inputs = new[] { new GateInput { Name = "items", Question = "Which invented items?", Required = "optional" } },
                },
            };
            var run = new WorkflowRunState("wfr-empty-input", new WorkflowDef
            {
                Name = "invented-empty-input",
                Version = 2,
                Steps = new[] { loop, new WorkflowStep { Id = "next", Number = 2, Title = "Next" } },
            }, null);
            var source = new AnswerValue { Value = "" };
            run.Results[0].Answers["items"] = source;

            WorkflowRunner.ExpandCurrentForEach(run, Array.Empty<string>());

            Assert.Same(source, run.Results[0].Answers["items"]);
            Assert.Equal("not_applicable", run.Results[0].Status);
            Assert.Equal(1, run.StepIndex);
        }

        [Fact]
        public void Inline_and_no_loop_expansion_controls_do_not_invent_frozen_answers()
        {
            var inline = new WorkflowRunState("wfr-inline-control", new WorkflowDef
            {
                Name = "invented-inline-control",
                Version = 2,
                Steps = new[] { InlineLoop(new[] { "one", "two" }) },
            }, null);
            WorkflowRunner.ExpandCurrentForEach(inline, new[] { "one", "two" });
            Assert.All(inline.Results, result => Assert.Empty(result.Answers));

            var plain = new WorkflowRunState("wfr-plain-control", new WorkflowDef
            {
                Name = "invented-plain-control",
                Steps = new[] { new WorkflowStep { Id = "plain", Number = 1, Title = "Plain" } },
            }, null);
            Assert.Equal(new[] { "plain" }, WorkflowRunner.BuildView(plain).Steps.Select(step => step.StepId));
            Assert.Empty(plain.Results[0].Answers);
        }

        // ---- unit 15: the one PREPARATION submission that answers a self-declared list source ----

        private const string PreparationSetupInstructions =
            "This call only fixes the loop list. Answer exactly the visible source question. Do not do iteration work yet. The first applicable iteration will return the first real instructions.";
        private const string InlineSetupInstructions =
            "The authored list is ready. Submit the current base id with no answers. Do not do iteration work yet. The first applicable iteration will return the first real instructions.";
        private const string DeferredSetupInstructions =
            "This loop does not ask you for its list. Submit the current base id with no answers. Do not do iteration work yet. The first applicable iteration will return the first real instructions.";
        private const string AuthoredPreparationInstructions =
            "Authored body: review [[loop.region]] at [[loop.index]].";
        private const string AuthoredInlineInstructions =
            "Authored body: review [[loop.item]] at [[loop.index]].";

        /// <summary>A run whose current row is an unexpanded forEach template declaring its own list source,
        /// with that source's own `required:` rule under the caller's control. The source is declared SECOND so
        /// a view that shows only the source cannot pass by accidentally taking the first authored input.</summary>
        private static WorkflowRunState PreparationRunWithSourceRule(string runId, string sourceRequired)
        {
            var loop = new WorkflowStep
            {
                Id = "review-region",
                Number = 1,
                Title = "Review invented region",
                Instructions = AuthoredPreparationInstructions,
                Ops = new[] { "edit_measure" },
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 4 },
                Gate = new GateSpec
                {
                    Inputs = new[]
                    {
                        new GateInput { Name = "finding", Question = "What was found?", Required = "optional" },
                        new GateInput { Name = "regions", Question = "Which invented regions?", Required = sourceRequired },
                        new GateInput { Name = "note", Question = "Any note?", Required = "answer-or-decline" },
                    },
                    Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                },
            };
            return new WorkflowRunState(runId, new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-preparation-rule",
                Version = 2,
                Strictness = "hard",
                Steps = new[] { loop },
            }, null);
        }

        /// <summary>A run whose current row is an unexpanded forEach template declaring its own list source.</summary>
        private static WorkflowRunState PreparationRun(string runId, params WorkflowStep[] following)
        {
            var loop = new WorkflowStep
            {
                Id = "review-region",
                Number = 1,
                Title = "Review invented region",
                Instructions = AuthoredPreparationInstructions,
                Ops = new[] { "edit_measure" },
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 4 },
                Gate = new GateSpec
                {
                    Inputs = new[]
                    {
                        new GateInput { Name = "regions", Question = "Which invented regions?", Required = "optional" },
                        new GateInput { Name = "finding", Question = "What was found?", Required = "optional" },
                    },
                    Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                },
            };
            return new WorkflowRunState(runId, new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-preparation",
                Version = 2,
                Strictness = "hard",
                Steps = new[] { loop }.Concat(following).ToArray(),
            }, null);
        }

        private static Task<Exception> AssertAtomicPreparationRefusal(
            WorkflowRunState run, string stepId, Dictionary<string, AnswerValue> answers)
        {
            var before = Snapshot(run);
            var answerStores = run.Results.Select(r => (object)r.Answers).ToArray();
            var answerValues = run.Results.SelectMany(r => r.Answers.Values).ToArray();
            var verifyResults = run.Results.Select(r => (object)r.VerifyResults).ToArray();
            var histories = run.Results.Select(r => (object)r.VerifyHistory).ToArray();
            var notes = run.Results.Select(r => r.Note).ToArray();
            var strictness = run.Results.Select(r => r.EffectiveStrictness).ToArray();
            var frameStates = run.Frames.Select(f => f.State).ToArray();
            var locks = run.Frames.Select(f => (object)f.WitnessLocks).ToArray();
            var lockCounts = run.Frames.Select(f => f.WitnessLocks.Count).ToArray();
            var revisions = run.Frames.Select(f => (object)f.WitnessRevisions).ToArray();
            var revisionCounts = run.Frames.Select(f => f.WitnessRevisions.Count).ToArray();
            var origin = run.Origin;

            return AssertAsync();

            async Task<Exception> AssertAsync()
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(
                    () => WorkflowRunner.SubmitStepAsync(run, stepId, answers, null));

                AssertUnchanged(before, run);
                Assert.Equal(answerStores, run.Results.Select(r => (object)r.Answers));
                Assert.Equal(answerValues, run.Results.SelectMany(r => r.Answers.Values));
                Assert.Equal(verifyResults, run.Results.Select(r => (object)r.VerifyResults));
                Assert.Equal(histories, run.Results.Select(r => (object)r.VerifyHistory));
                Assert.Equal(notes, run.Results.Select(r => r.Note));
                Assert.Equal(strictness, run.Results.Select(r => r.EffectiveStrictness));
                Assert.Equal(frameStates, run.Frames.Select(f => f.State));
                Assert.Equal(locks, run.Frames.Select(f => (object)f.WitnessLocks));
                Assert.Equal(lockCounts, run.Frames.Select(f => f.WitnessLocks.Count));
                Assert.Equal(revisions, run.Frames.Select(f => (object)f.WitnessRevisions));
                Assert.Equal(revisionCounts, run.Frames.Select(f => f.WitnessRevisions.Count));
                Assert.Equal(origin, run.Origin);
                Assert.Null(run.SubmissionOrigin);
                return error;
            }
        }

        private static void AssertLoopEntryNamedFieldsGoToTheCurrentIteration(string message)
        {
            // ONE spelling across all three setup shapes: inline, preparation and loop entry all send a named
            // field to the row the splice makes current with the same words, so a reader who has met one
            // refusal has met them all.
            Assert.Contains("against the iteration this expansion makes current", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("answered", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("resolved", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Preparation_submission_expands_a_two_item_source_without_running_the_loop()
        {
            var run = PreparationRun("wfr-prepare");
            // The existing §1.6 resolver semantics: comma OR line break, trimmed, blanks dropped.
            var submitted = new AnswerValue { Value = " North , South\r\n\n , " };
            var verifyCalls = 0;
            WorkflowVerifyExecutor executor = (spec, step, state, answers) =>
            {
                verifyCalls++;
                return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed" });
            };

            await WorkflowRunner.SubmitStepAsync(run, "review-region", new Dictionary<string, AnswerValue>
            {
                ["regions"] = submitted,
            }, executor);

            Assert.Equal(0, verifyCalls);
            Assert.Equal(0, run.StepIndex);
            Assert.Equal("active", run.Status);
            Assert.Equal(new[] { "review-region#0", "review-region#1" }, run.Plan.Select(p => p.InstanceId));
            Assert.Equal(run.Plan.Select(p => p.InstanceId), run.Results.Select(r => r.StepId));
            Assert.Equal(new int?[] { 0, 1 }, run.Plan.Select(p => p.IterationIndex));
            Assert.All(run.Plan, planned => Assert.True(planned.ForEachExpanded));
            Assert.Equal(new[] { "in_progress", "pending" }, run.Results.Select(r => r.Status));
            Assert.All(run.Results, result => Assert.Empty(result.VerifyResults));
            Assert.All(run.Results, result => Assert.Empty(result.VerifyHistory));
            Assert.All(run.Results, result => Assert.Null(result.Note));
            Assert.Equal(new[] { "North", "South" }, run.Frames.Skip(1).Select(f => f.LoopValue));
            Assert.All(run.Frames.Skip(1), frame => Assert.Equal("in_progress", frame.State));

            var view = WorkflowRunner.BuildView(run);
            Assert.Equal("review-region#0", view.CurrentStep.StepId);
            Assert.Equal(new[] { "finding" }, view.CurrentStep.Questions.Select(q => q.Name));
            Assert.False(view.TotalStepsProvisional);

            var first = run.Results[0].Answers["regions"];
            var second = run.Results[1].Answers["regions"];
            Assert.Equal(" North , South\r\n\n , ", first.Value);
            Assert.Equal(" North , South\r\n\n , ", second.Value);
            Assert.NotSame(submitted, first);
            Assert.NotSame(submitted, second);
            Assert.NotSame(first, second);
            first.Value = "changed";
            Assert.Equal(" North , South\r\n\n , ", second.Value);
            Assert.Equal(" North , South\r\n\n , ", submitted.Value);
        }

        [Fact]
        public async Task Preparation_payload_naming_another_gate_input_refuses_and_keeps_it_a_visible_question()
        {
            var run = PreparationRun("wfr-prepare-extra");

            var error = await AssertAtomicPreparationRefusal(run, "review-region", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
                ["finding"] = new AnswerValue { Value = "must not land" },
            });

            Assert.Contains("finding", error.Message, StringComparison.Ordinal);
            Assert.Contains("regions", error.Message, StringComparison.Ordinal);
            Assert.Empty(run.Results[0].Answers);
            // The refusal does not make the extra name askable here: before expansion the view teaches only the
            // source question, and 'finding' becomes visible on the iteration that owns it.
            Assert.Equal(new[] { "regions" }, WorkflowRunner.BuildView(run).CurrentStep.Questions.Select(q => q.Name));

            await WorkflowRunner.SubmitStepAsync(run, "review-region", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, null);

            Assert.Equal(new[] { "finding" }, WorkflowRunner.BuildView(run).CurrentStep.Questions.Select(q => q.Name));
            Assert.False(run.Results[0].Answers.ContainsKey("finding"));
            Assert.False(run.Results[1].Answers.ContainsKey("finding"));
        }

        [Fact]
        public async Task Preparation_refusals_leave_every_run_object_answer_store_and_receipt_untouched()
        {
            var missing = await AssertAtomicPreparationRefusal(
                PreparationRun("wfr-prep-missing"), "review-region", new Dictionary<string, AnswerValue>());
            Assert.Contains("regions", missing.Message, StringComparison.Ordinal);

            var noPayload = await AssertAtomicPreparationRefusal(
                PreparationRun("wfr-prep-null-payload"), "review-region", null!);
            Assert.Contains("regions", noPayload.Message, StringComparison.Ordinal);

            // A null value and a decline are gaps, not empty lists: neither may fix an iteration count.
            var nullValued = await AssertAtomicPreparationRefusal(
                PreparationRun("wfr-prep-null"), "review-region", new Dictionary<string, AnswerValue>
                {
                    ["regions"] = new AnswerValue { Value = null },
                });
            Assert.Contains("unanswered", nullValued.Message, StringComparison.OrdinalIgnoreCase);

            var declined = await AssertAtomicPreparationRefusal(
                PreparationRun("wfr-prep-declined"), "review-region", new Dictionary<string, AnswerValue>
                {
                    ["regions"] = new AnswerValue { Declined = true, DeclineReason = "invented decline" },
                });
            Assert.Contains("declined", declined.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("invented decline", declined.Message, StringComparison.Ordinal);

            var overMax = await AssertAtomicPreparationRefusal(
                PreparationRun("wfr-prep-over-max"), "review-region", new Dictionary<string, AnswerValue>
                {
                    ["regions"] = new AnswerValue { Value = "a, b, c, d, e" },
                });
            Assert.Contains("5", overMax.Message, StringComparison.Ordinal);
            Assert.Contains("4", overMax.Message, StringComparison.Ordinal);

            var stale = await AssertAtomicPreparationRefusal(
                PreparationRun("wfr-prep-stale"), "review-region#0", new Dictionary<string, AnswerValue>
                {
                    ["regions"] = new AnswerValue { Value = "North" },
                });
            Assert.Contains("not the current step", stale.Message, StringComparison.Ordinal);

            var incoherent = PreparationRun("wfr-prep-incoherent");
            incoherent.Frames.Add(RunFrame.CreateIteration(
                "wfr-prep-incoherent:outer:0", "outer", 0, "outer", "one", "in_progress"));
            incoherent.Plan[0] = new PlannedStep(
                incoherent.Plan[0].Step, "review-region", "wfr-prep-incoherent:outer:0", null);
            var incoherentError = await AssertAtomicPreparationRefusal(
                incoherent, "review-region", new Dictionary<string, AnswerValue>
                {
                    ["regions"] = new AnswerValue { Value = "North" },
                });
            Assert.Contains("iteration frame", incoherentError.Message, StringComparison.Ordinal);

            var duplicate = PreparationRun("wfr-prep-duplicate");
            duplicate.Frames.Add(new RunFrame("wfr-prep-duplicate"));
            var duplicateError = await AssertAtomicPreparationRefusal(
                duplicate, "review-region", new Dictionary<string, AnswerValue>
                {
                    ["regions"] = new AnswerValue { Value = "North" },
                });
            Assert.Contains("duplicate frame", duplicateError.Message, StringComparison.Ordinal);

            // A second preparation cannot re-fix a list expansion already fixed.
            var repeated = PreparationRun("wfr-prep-repeated");
            await WorkflowRunner.SubmitStepAsync(repeated, "review-region", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, null);
            var repeatedError = await AssertAtomicPreparationRefusal(
                repeated, "review-region#0", new Dictionary<string, AnswerValue>
                {
                    ["regions"] = new AnswerValue { Value = "East, West" },
                });
            Assert.Contains("fixed at expansion", repeatedError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, repeated.Plan.Count);
            Assert.Equal("North, South", repeated.Results[0].Answers["regions"].Value);
        }

        [Fact]
        public async Task Answered_blank_optional_source_preparation_records_expansion_and_advances_exactly_once()
        {
            var run = PreparationRun("wfr-prepare-blank", new WorkflowStep
            {
                Id = "next",
                Number = 2,
                Title = "Next",
                Instructions = "Authored next-step body.",
                Ops = new[] { "ask_user" },
            });
            var sourceResult = run.Results[0];
            var nextResult = run.Results[1];

            await WorkflowRunner.SubmitStepAsync(run, "review-region", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "  \r\n , " },
            }, null);

            Assert.Equal(2, run.Plan.Count);
            Assert.Equal(run.Plan.Count, run.Results.Count);
            Assert.Same(sourceResult, run.Results[0]);
            Assert.Same(nextResult, run.Results[1]);
            Assert.True(run.Plan[0].ForEachExpanded);
            Assert.Null(run.Plan[0].IterationIndex);
            Assert.Equal("not_applicable", run.Results[0].Status);
            Assert.Contains("empty", run.Results[0].Note, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("hard", run.Results[0].EffectiveStrictness);
            // The visible audit answer survives: it is what the empty expansion was decided from.
            Assert.Equal("  \r\n , ", Assert.Single(run.Results[0].Answers).Value.Value);
            Assert.Empty(run.Results[0].VerifyResults);
            Assert.Empty(run.Results[0].VerifyHistory);
            Assert.Equal(1, run.StepIndex);
            Assert.Equal("in_progress", run.Results[1].Status);
            Assert.Equal("active", run.Status);
            Assert.Single(run.Frames);
            var afterBlank = WorkflowRunner.BuildView(run);
            Assert.False(afterBlank.TotalStepsProvisional);
            Assert.Equal("next", afterBlank.CurrentStep.StepId);
            Assert.Equal("Authored next-step body.", afterBlank.CurrentStep.Instructions);
            Assert.Equal(new[] { "ask_user" }, afterBlank.CurrentStep.Ops);
            Assert.NotEqual(PreparationSetupInstructions, afterBlank.CurrentStep.Instructions);

            var terminal = PreparationRun("wfr-prepare-blank-terminal");
            await WorkflowRunner.SubmitStepAsync(terminal, "review-region", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "" },
            }, null);

            Assert.Equal("completed", terminal.Status);
            Assert.NotNull(terminal.FinishedUtc);
            Assert.Equal(1, terminal.StepIndex);
            Assert.Equal("not_applicable", terminal.Results[0].Status);
            Assert.Equal("", Assert.Single(terminal.Results[0].Answers).Value.Value);
            Assert.Null(WorkflowRunner.BuildView(terminal).CurrentStep);
        }

        [Fact]
        public async Task Inline_and_ordinary_submissions_keep_their_behavior_and_an_adjacent_deferred_source_expands()
        {
            // An inline literal is expansion-only: one empty-payload submit splices the list and does not run #0.
            var inline = new WorkflowRunState("wfr-inline-submit", new WorkflowDef
            {
                Name = "invented-inline-submit",
                Version = 2,
                Strictness = "off",
                Steps = new[] { InlineExpansionStep(new[] { "one", "two" }), new WorkflowStep { Id = "next", Number = 2, Title = "Next" } },
            }, null);
            var verifyCalls = 0;
            WorkflowVerifyExecutor executor = (spec, step, state, answers) =>
            {
                verifyCalls++;
                return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed" });
            };
            await WorkflowRunner.SubmitStepAsync(inline, "inline-loop", null, executor);
            Assert.Equal(0, verifyCalls);
            Assert.Equal("in_progress", inline.Results[0].Status);
            Assert.Equal(0, inline.StepIndex);
            Assert.Equal(new[] { "inline-loop#0", "inline-loop#1", "next" }, inline.Plan.Select(p => p.InstanceId));
            Assert.Equal(3, inline.Frames.Count);

            // A source declared on an EARLIER step is not this step's own gate, so answering it is ordinary too.
            var earlier = InputBackedRun("wfr-earlier-submit", out _);
            earlier.Def.Strictness = "hard";
            earlier.Def.Steps[0].Instructions = "Choose the invented list.";
            earlier.Def.Steps[0].Ops = new[] { "ask_user" };
            earlier.Def.Steps[1].Instructions = "Review the later loop [[loop.region]].";
            earlier.Def.Steps[1].Ops = new[] { "edit_measure" };
            earlier.Def.Steps[1].Gate = new GateSpec
            {
                Inputs = new[] { new GateInput { Name = "finding", Question = "What was found?" } },
                Verify = new[] { new VerifySpec { Kind = "invented_check" } },
            };
            var chooseView = WorkflowRunner.BuildView(earlier);
            Assert.Equal("Choose the invented list.", chooseView.CurrentStep.Instructions);
            Assert.Equal(new[] { "ask_user" }, chooseView.CurrentStep.Ops);
            Assert.NotEqual(InlineSetupInstructions, chooseView.CurrentStep.Instructions);
            Assert.NotEqual(PreparationSetupInstructions, chooseView.CurrentStep.Instructions);
            await WorkflowRunner.SubmitStepAsync(earlier, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, null);
            Assert.Equal("done", earlier.Results[0].Status);      // an answer with no check is proof-free
            Assert.Equal(1, earlier.StepIndex);
            Assert.Equal("North, South", earlier.Results[0].Answers["regions"].Value);
            Assert.Equal(3, earlier.Plan.Count);
            Assert.Equal(3, earlier.Frames.Count);
            var loopView = WorkflowRunner.BuildView(earlier);
            Assert.Equal("review-region#0", loopView.CurrentStep.StepId);
            Assert.True(earlier.Plan[1].ForEachExpanded);
            Assert.Equal("Review the later loop North.", loopView.CurrentStep.Instructions);
            Assert.Equal(new[] { "finding" }, loopView.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(new[] { "invented_check" }, loopView.CurrentStep.VerifyKinds);
            Assert.Equal(new[] { "edit_measure" }, loopView.CurrentStep.Ops);
            Assert.Equal("hard", loopView.CurrentStep.EffectiveStrictness);
            Assert.NotEqual(InlineSetupInstructions, loopView.CurrentStep.Instructions);
            Assert.NotEqual(PreparationSetupInstructions, loopView.CurrentStep.Instructions);

            // An ordinary step still runs its gate and advances.
            var ordinary = new WorkflowRunState("wfr-ordinary-submit", new WorkflowDef
            {
                Name = "invented-ordinary-submit",
                Strictness = "hard",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "plain",
                        Number = 1,
                        Title = "Plain",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "answer" } } },
                    },
                },
            }, null);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(ordinary, "plain", null, null));
            await WorkflowRunner.SubmitStepAsync(ordinary, "plain", new Dictionary<string, AnswerValue>
            {
                ["answer"] = new AnswerValue { Value = "42" },
            }, null);
            Assert.Equal("completed", ordinary.Status);
            Assert.Equal("42", ordinary.Results[0].Answers["answer"].Value);
        }

        [Fact]
        public async Task Unexpanded_self_sourced_template_asks_only_its_list_source_and_then_only_the_rest()
        {
            var run = PreparationRunWithSourceRule("wfr-prep-view", "optional");

            // Before expansion the ONLY answerable question is the list source: the preparation refuses every
            // other name, so offering one would teach an agent to submit a payload this step cannot accept.
            var before = WorkflowRunner.BuildView(run);
            Assert.Equal("review-region", before.CurrentStep.StepId);
            Assert.Equal("Review invented region", before.CurrentStep.Title);
            Assert.Equal(PreparationSetupInstructions, before.CurrentStep.Instructions);
            Assert.Equal(new[] { "regions" }, before.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal("Which invented regions?", Assert.Single(before.CurrentStep.Questions).Question);
            Assert.Empty(before.CurrentStep.VerifyKinds);
            Assert.Empty(before.CurrentStep.Ops);
            Assert.Null(before.CurrentStep.EffectiveStrictness);
            Assert.True(before.TotalStepsProvisional);

            await WorkflowRunner.SubmitStepAsync(run, "review-region", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, null);

            // After expansion only the frozen source is hidden, and the rest keep their AUTHORED order.
            var after = WorkflowRunner.BuildView(run);
            Assert.Equal("review-region#0", after.CurrentStep.StepId);
            Assert.Equal("Review invented region", after.CurrentStep.Title);
            Assert.Equal("Authored body: review North at 0.", after.CurrentStep.Instructions);
            Assert.NotEqual(PreparationSetupInstructions, after.CurrentStep.Instructions);
            Assert.Equal(new[] { "finding", "note" }, after.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(new[] { "invented_check" }, after.CurrentStep.VerifyKinds);
            Assert.Equal(new[] { "edit_measure" }, after.CurrentStep.Ops);
            Assert.Equal("hard", after.CurrentStep.EffectiveStrictness);
        }

        [Fact]
        public async Task Unexpanded_inline_earlier_source_and_ordinary_rows_still_ask_every_authored_question()
        {
            // An unexpanded inline loop is a setup row: no body questions, ops, kinds, or strictness yet.
            var inline = new WorkflowRunState("wfr-view-inline", new WorkflowDef
            {
                Name = "invented-view-inline",
                Version = 2,
                Strictness = "hard",
                Steps = new[] { InlineExpansionStep(new[] { "one", "two" }) },
            }, null);
            var inlineView = WorkflowRunner.BuildView(inline);
            Assert.Equal("inline-loop", inlineView.CurrentStep.StepId);
            Assert.Equal("Inline invented loop", inlineView.CurrentStep.Title);
            Assert.Equal(InlineSetupInstructions, inlineView.CurrentStep.Instructions);
            Assert.Empty(inlineView.CurrentStep.Questions);
            Assert.Empty(inlineView.CurrentStep.VerifyKinds);
            Assert.Empty(inlineView.CurrentStep.Ops);
            Assert.Null(inlineView.CurrentStep.EffectiveStrictness);

            // A source declared on an EARLIER step is that step's ordinary question, not a preparation source.
            var earlier = InputBackedRun("wfr-view-earlier", out _);
            earlier.Def.Strictness = "hard";
            earlier.Def.Steps[0].Instructions = "Choose the invented list.";
            earlier.Def.Steps[0].Ops = new[] { "ask_user" };
            earlier.Def.Steps[0].Gate.Verify = new[] { new VerifySpec { Kind = "baseline_exists" } };
            earlier.Def.Steps[1].Instructions = "Review the later loop [[loop.region]].";
            earlier.Def.Steps[1].Ops = new[] { "edit_measure" };
            earlier.Def.Steps[1].Gate = new GateSpec
            {
                Inputs = new[] { new GateInput { Name = "finding", Question = "What was found?" } },
                Verify = new[] { new VerifySpec { Kind = "invented_check" } },
            };
            var earlierView = WorkflowRunner.BuildView(earlier);
            Assert.Equal("choose", earlierView.CurrentStep.StepId);
            Assert.Equal("Choose the invented list.", earlierView.CurrentStep.Instructions);
            Assert.Equal(new[] { "regions" }, earlierView.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(new[] { "baseline_exists" }, earlierView.CurrentStep.VerifyKinds);
            Assert.Equal(new[] { "ask_user" }, earlierView.CurrentStep.Ops);
            Assert.Equal("hard", earlierView.CurrentStep.EffectiveStrictness);
            Assert.NotEqual(InlineSetupInstructions, earlierView.CurrentStep.Instructions);
            Assert.NotEqual(PreparationSetupInstructions, earlierView.CurrentStep.Instructions);
            Assert.NotEqual(DeferredSetupInstructions, earlierView.CurrentStep.Instructions);

            var deferred = EarlierSourceRun("wfr-view-deferred-current");
            await ReachLoopEntry(deferred);
            var deferredView = WorkflowRunner.BuildView(deferred);
            Assert.Equal("review-region", deferredView.CurrentStep.StepId);
            Assert.Equal("Review invented region", deferredView.CurrentStep.Title);
            Assert.Equal(DeferredSetupInstructions, deferredView.CurrentStep.Instructions);
            Assert.Empty(deferredView.CurrentStep.Questions);
            Assert.Empty(deferredView.CurrentStep.VerifyKinds);
            Assert.Empty(deferredView.CurrentStep.Ops);
            Assert.Null(deferredView.CurrentStep.EffectiveStrictness);
            Assert.True(deferredView.TotalStepsProvisional);
            Assert.NotEqual(InlineSetupInstructions, deferredView.CurrentStep.Instructions);
            Assert.NotEqual(PreparationSetupInstructions, deferredView.CurrentStep.Instructions);

            // An ordinary step is untouched.
            var ordinary = new WorkflowRunState("wfr-view-ordinary", new WorkflowDef
            {
                Name = "invented-view-ordinary",
                Strictness = "hard",
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
                            Inputs = new[]
                            {
                                new GateInput { Name = "first", Question = "First?" },
                                new GateInput { Name = "second", Question = "Second?" },
                            },
                            Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                        },
                    },
                },
            }, null);
            var ordinaryView = WorkflowRunner.BuildView(ordinary);
            Assert.Equal("plain", ordinaryView.CurrentStep.StepId);
            Assert.Equal("Do the ordinary work.", ordinaryView.CurrentStep.Instructions);
            Assert.Equal(new[] { "first", "second" }, ordinaryView.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(new[] { "invented_check" }, ordinaryView.CurrentStep.VerifyKinds);
            Assert.Equal(new[] { "edit_measure" }, ordinaryView.CurrentStep.Ops);
            Assert.Equal("hard", ordinaryView.CurrentStep.EffectiveStrictness);
            Assert.NotEqual(InlineSetupInstructions, ordinaryView.CurrentStep.Instructions);
            Assert.NotEqual(PreparationSetupInstructions, ordinaryView.CurrentStep.Instructions);
            Assert.NotEqual(DeferredSetupInstructions, ordinaryView.CurrentStep.Instructions);

            // An ALREADY-expanded empty template keeps the ordinary projection too: nothing is frozen on a
            // non-iteration row, so hiding the source there would erase a question the run never asked.
            var expanded = PreparationRunWithSourceRule("wfr-view-expanded", "optional");
            await WorkflowRunner.SubmitStepAsync(expanded, "review-region", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "  " },
            }, null);
            Assert.Equal("not_applicable", expanded.Results[0].Status);
            Assert.Null(WorkflowRunner.BuildView(expanded).CurrentStep);
        }

        [Fact]
        public async Task Blank_answer_to_a_non_optional_list_source_refuses_with_its_question_and_required_rule()
        {
            // §1.6 acceptance check 14b: an input-backed EMPTY loop is spelled only through `required: optional`.
            // The preparation does not run EnforceInputs, so the source's own rule is enforced here or nowhere.
            foreach (var blank in new[] { "", "   ", ",,", " , \r\n , " })
            {
                var required = await AssertAtomicPreparationRefusal(
                    PreparationRunWithSourceRule("wfr-prep-required" + blank.Length, "required"),
                    "review-region",
                    new Dictionary<string, AnswerValue> { ["regions"] = new AnswerValue { Value = blank } });
                Assert.Contains("regions", required.Message, StringComparison.Ordinal);
                Assert.Contains("Which invented regions?", required.Message, StringComparison.Ordinal);
                Assert.Contains("required: it cannot be declined", required.Message, StringComparison.Ordinal);
                Assert.Contains("skip_workflow_step", required.Message, StringComparison.Ordinal);
                // The unrelated iteration questions are NOT enforced by a preparation, so neither is named.
                Assert.DoesNotContain("finding", required.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("note", required.Message, StringComparison.Ordinal);

                var answerOrDecline = await AssertAtomicPreparationRefusal(
                    PreparationRunWithSourceRule("wfr-prep-aod" + blank.Length, "answer-or-decline"),
                    "review-region",
                    new Dictionary<string, AnswerValue> { ["regions"] = new AnswerValue { Value = blank } });
                Assert.Contains("regions", answerOrDecline.Message, StringComparison.Ordinal);
                Assert.Contains("Which invented regions?", answerOrDecline.Message, StringComparison.Ordinal);
                Assert.Contains("decline explicitly", answerOrDecline.Message, StringComparison.Ordinal);
                Assert.Contains("skip_workflow_step", answerOrDecline.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("finding", answerOrDecline.Message, StringComparison.Ordinal);
            }

            // A NONBLANK answer to the same non-optional source expands exactly as before.
            var expands = PreparationRunWithSourceRule("wfr-prep-required-ok", "required");
            await WorkflowRunner.SubmitStepAsync(expands, "review-region", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, null);
            Assert.Equal(new[] { "review-region#0", "review-region#1" }, expands.Plan.Select(p => p.InstanceId));

            // Only `required: optional` may spell blank or separators-only as an empty loop.
            var optional = PreparationRunWithSourceRule("wfr-prep-optional-blank", "optional");
            await WorkflowRunner.SubmitStepAsync(optional, "review-region", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = " , \r\n , " },
            }, null);
            Assert.Equal("not_applicable", optional.Results[0].Status);
            Assert.Equal(" , \r\n , ", Assert.Single(optional.Results[0].Answers).Value.Value);
            Assert.Equal("completed", optional.Status);
        }

        [Fact]
        public async Task Inline_expansion_splices_a_verbatim_two_item_list_without_running_the_loop()
        {
            var authored = new[] { "  North  ", "East, West" };
            var run = InlineExpansionRun("wfr-inline-expand", authored);
            var authoredLiteral = run.Def.Steps[0].ForEach.InLiteral;
            var verifyCalls = 0;
            WorkflowVerifyExecutor executor = (spec, step, state, answers) =>
            {
                verifyCalls++;
                return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed" });
            };

            await WorkflowRunner.SubmitStepAsync(run, "inline-loop", null, executor);

            Assert.Equal(0, verifyCalls);
            Assert.Equal(0, run.StepIndex);
            Assert.Equal("active", run.Status);
            Assert.Equal(new[] { "inline-loop#0", "inline-loop#1" }, run.Plan.Select(p => p.InstanceId));
            Assert.Equal(run.Plan.Select(p => p.InstanceId), run.Results.Select(r => r.StepId));
            Assert.Equal(new int?[] { 0, 1 }, run.Plan.Select(p => p.IterationIndex));
            Assert.All(run.Plan, planned => Assert.True(planned.ForEachExpanded));
            Assert.Equal(new[] { "in_progress", "pending" }, run.Results.Select(r => r.Status));
            Assert.All(run.Results, result => Assert.Empty(result.Answers));
            Assert.All(run.Results, result => Assert.Empty(result.VerifyResults));
            Assert.All(run.Results, result => Assert.Empty(result.VerifyHistory));
            Assert.All(run.Results, result => Assert.Null(result.Note));
            Assert.Equal(new[] { "  North  ", "East, West" }, run.Frames.Skip(1).Select(f => f.LoopValue));
            Assert.Same(authoredLiteral, run.Def.Steps[0].ForEach.InLiteral);
            Assert.Equal(authored, run.Def.Steps[0].ForEach.InLiteral);
            authoredLiteral[0] = "changed";
            Assert.Equal("  North  ", run.Frames[1].LoopValue);

            var view = WorkflowRunner.BuildView(run);
            Assert.Equal("inline-loop#0", view.CurrentStep.StepId);
            Assert.False(view.TotalStepsProvisional);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(run, "", null, executor));
            Assert.Equal("inline-loop#0", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            Assert.Equal(0, verifyCalls);
        }

        [Fact]
        public async Task Inline_expansion_treats_null_and_empty_payloads_as_equivalent()
        {
            var authored = new[] { "  North  ", "East, West" };
            var nullPayload = InlineExpansionRun("wfr-inline-null", authored);
            var emptyPayload = InlineExpansionRun("wfr-inline-empty-object", authored.ToArray());

            await WorkflowRunner.SubmitStepAsync(nullPayload, "inline-loop", null, null);
            await WorkflowRunner.SubmitStepAsync(emptyPayload, "inline-loop", new Dictionary<string, AnswerValue>(), null);

            Assert.Equal(nullPayload.Plan.Select(p => p.InstanceId), emptyPayload.Plan.Select(p => p.InstanceId));
            Assert.Equal(new[] { "inline-loop#0", "inline-loop#1" }, nullPayload.Plan.Select(p => p.InstanceId));
            Assert.All(nullPayload.Results, result => Assert.Empty(result.Answers));
            Assert.All(emptyPayload.Results, result => Assert.Empty(result.Answers));
            Assert.Equal(0, nullPayload.StepIndex);
            Assert.Equal(0, emptyPayload.StepIndex);
        }

        [Fact]
        public async Task Inline_expansion_payload_naming_any_field_refuses_before_mutation()
        {
            var named = InlineExpansionRun("wfr-inline-named", new[] { "North", "South" });
            var namedError = await AssertAtomicPreparationRefusal(named, "inline-loop", new Dictionary<string, AnswerValue>
            {
                ["finding"] = new AnswerValue { Value = "must not land" },
            });
            Assert.Contains("finding", namedError.Message, StringComparison.Ordinal);
            Assert.Contains("iteration", namedError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(WorkflowRunner.BuildView(named).CurrentStep.Questions);

            var declined = InlineExpansionRun("wfr-inline-declined", new[] { "North", "South" });
            var declinedError = await AssertAtomicPreparationRefusal(declined, "inline-loop", new Dictionary<string, AnswerValue>
            {
                ["finding"] = new AnswerValue { Declined = true, DeclineReason = "invented decline" },
            });
            Assert.Contains("finding", declinedError.Message, StringComparison.Ordinal);
            Assert.Contains("iteration", declinedError.Message, StringComparison.OrdinalIgnoreCase);

            await WorkflowRunner.SubmitStepAsync(named, "inline-loop", null, null);
            Assert.Equal(new[] { "finding", "note" }, WorkflowRunner.BuildView(named).CurrentStep.Questions.Select(q => q.Name));
            Assert.False(named.Results[0].Answers.ContainsKey("finding"));
            Assert.False(named.Results[1].Answers.ContainsKey("finding"));
        }

        [Fact]
        public async Task Empty_inline_list_records_not_applicable_and_advances_exactly_once()
        {
            var next = new WorkflowStep
            {
                Id = "next",
                Number = 2,
                Title = "Next",
                Instructions = "Authored next-step body.",
                Ops = new[] { "ask_user" },
            };
            var run = InlineExpansionRun("wfr-inline-empty-list", Array.Empty<string>(), next);
            var sourceResult = run.Results[0];
            var nextResult = run.Results[1];
            var authoredLiteral = run.Def.Steps[0].ForEach.InLiteral;

            await WorkflowRunner.SubmitStepAsync(run, "inline-loop", null, null);

            Assert.Equal(2, run.Plan.Count);
            Assert.Same(sourceResult, run.Results[0]);
            Assert.Same(nextResult, run.Results[1]);
            Assert.True(run.Plan[0].ForEachExpanded);
            Assert.Null(run.Plan[0].IterationIndex);
            Assert.Equal("not_applicable", run.Results[0].Status);
            Assert.Equal("did not apply: the forEach value list was empty.", run.Results[0].Note);
            Assert.Empty(run.Results[0].Answers);
            Assert.Empty(run.Results[0].VerifyResults);
            Assert.Empty(run.Results[0].VerifyHistory);
            Assert.Equal(1, run.StepIndex);
            Assert.Equal("in_progress", run.Results[1].Status);
            Assert.Equal("active", run.Status);
            Assert.Single(run.Frames);
            Assert.DoesNotContain(run.Plan, planned => planned.InstanceId.Contains("#"));
            Assert.Same(authoredLiteral, run.Def.Steps[0].ForEach.InLiteral);
            var after = WorkflowRunner.BuildView(run);
            Assert.Equal("next", after.CurrentStep.StepId);
            Assert.Equal("Authored next-step body.", after.CurrentStep.Instructions);
            Assert.NotEqual(InlineSetupInstructions, after.CurrentStep.Instructions);

            var terminal = InlineExpansionRun("wfr-inline-empty-terminal", Array.Empty<string>());
            await WorkflowRunner.SubmitStepAsync(terminal, "inline-loop", null, null);
            Assert.Equal("completed", terminal.Status);
            Assert.NotNull(terminal.FinishedUtc);
            Assert.Equal(1, terminal.StepIndex);
            Assert.Equal("not_applicable", terminal.Results[0].Status);
            Assert.Empty(terminal.Results[0].Answers);
            Assert.Null(WorkflowRunner.BuildView(terminal).CurrentStep);
            Assert.DoesNotContain(terminal.Plan, planned => planned.InstanceId.Contains("#"));
        }

        [Fact]
        public async Task Over_bound_inline_expansion_refuses_before_any_mutation()
        {
            var run = new WorkflowRunState("wfr-inline-over-bound", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-inline-over-bound",
                Strictness = "hard",
                Steps = new[] { InlineExpansionStep(new[] { "one", "two", "three" }, maxIterations: 2) },
            }, null);
            var error = await AssertAtomicPreparationRefusal(run, "inline-loop", null!);
            Assert.Contains("3", error.Message, StringComparison.Ordinal);
            Assert.Contains("2", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Inline_expansion_refusals_leave_every_run_object_and_receipt_store_untouched()
        {
            var extra = await AssertAtomicPreparationRefusal(
                InlineExpansionRun("wfr-inline-extra", new[] { "North" }),
                "inline-loop",
                new Dictionary<string, AnswerValue> { ["note"] = new AnswerValue { Value = "no" } });
            Assert.Contains("note", extra.Message, StringComparison.Ordinal);

            var stale = await AssertAtomicPreparationRefusal(
                InlineExpansionRun("wfr-inline-stale", new[] { "North" }),
                "inline-loop#0",
                null!);
            Assert.Contains("not the current step", stale.Message, StringComparison.Ordinal);

            var incoherent = InlineExpansionRun("wfr-inline-incoherent", new[] { "North" });
            incoherent.Frames.Add(RunFrame.CreateIteration(
                "wfr-inline-incoherent:outer:0", "outer", 0, "outer", "one", "in_progress"));
            incoherent.Plan[0] = new PlannedStep(
                incoherent.Plan[0].Step, "inline-loop", "wfr-inline-incoherent:outer:0", null);
            var incoherentError = await AssertAtomicPreparationRefusal(incoherent, "inline-loop", null!);
            Assert.Contains("iteration frame", incoherentError.Message, StringComparison.Ordinal);

            var duplicate = InlineExpansionRun("wfr-inline-duplicate", new[] { "North" });
            duplicate.Frames.Add(new RunFrame("wfr-inline-duplicate:inline-loop:0"));
            var duplicateError = await AssertAtomicPreparationRefusal(duplicate, "inline-loop", null!);
            Assert.Contains("already exists", duplicateError.Message, StringComparison.Ordinal);

            var repeated = InlineExpansionRun("wfr-inline-repeated", new[] { "North", "South" });
            await WorkflowRunner.SubmitStepAsync(repeated, "inline-loop", null, null);
            var repeatedError = await AssertAtomicPreparationRefusal(repeated, "inline-loop", null!);
            Assert.Contains("not the current step", repeatedError.Message, StringComparison.Ordinal);
            Assert.Equal(new[] { "inline-loop#0", "inline-loop#1" }, repeated.Plan.Select(p => p.InstanceId));
            var beforeExpand = Snapshot(repeated);
            var expandError = Assert.Throws<InvalidOperationException>(() =>
                WorkflowRunner.ExpandCurrentForEach(repeated, new[] { "East" }));
            Assert.Contains("iteration row", expandError.Message, StringComparison.Ordinal);
            AssertUnchanged(beforeExpand, repeated);
        }

        [Fact]
        public async Task False_when_on_an_unexpanded_inline_loop_creates_no_iterations()
        {
            var step = InlineExpansionStep(new[] { "North", "South" });
            step.When = "inputs.missing.answered";
            var run = new WorkflowRunState("wfr-inline-false-when", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-inline-false-when",
                Strictness = "hard",
                Steps = new[] { step },
            }, null);

            WorkflowRunner.AdvancePastInapplicableSteps(run);

            var waiting = WorkflowRunner.BuildView(run);
            Assert.NotNull(waiting.CurrentStep);
            Assert.Equal("inline-loop", waiting.CurrentStep.StepId);
            Assert.Equal(InlineSetupInstructions, waiting.CurrentStep.Instructions);
            Assert.NotEqual("not_applicable", run.Results[0].Status);
            Assert.Single(run.Plan);
            Assert.Null(run.Plan[0].IterationIndex);
            Assert.False(run.Plan[0].ForEachExpanded);
            Assert.DoesNotContain(run.Plan, planned => planned.InstanceId.Contains("#"));
            Assert.Single(run.Frames);
            Assert.NotEqual("completed", run.Status);

            await WorkflowRunner.SubmitStepAsync(run, "inline-loop", null, null);

            Assert.Equal("not_applicable", run.Results[0].Status);
            Assert.Single(run.Plan);
            Assert.Null(run.Plan[0].IterationIndex);
            Assert.False(run.Plan[0].ForEachExpanded);
            Assert.DoesNotContain(run.Plan, planned => planned.InstanceId.Contains("#"));
            Assert.Single(run.Frames);
            Assert.Equal("completed", run.Status);
        }

        [Fact]
        public void Skip_of_an_unexpanded_inline_loop_is_an_audited_skip_and_does_not_expand()
        {
            var run = InlineExpansionRun("wfr-inline-skip", new[] { "North", "South" },
                new WorkflowStep { Id = "next", Number = 2, Title = "Next" });

            WorkflowRunner.SkipStep(run, "inline-loop", "invented skip reason");

            Assert.Equal("skipped", run.Results[0].Status);
            Assert.Contains("invented skip reason", run.Results[0].Note, StringComparison.Ordinal);
            Assert.Equal(2, run.Plan.Count);
            Assert.False(run.Plan[0].ForEachExpanded);
            Assert.Null(run.Plan[0].IterationIndex);
            Assert.Equal(1, run.StepIndex);
            Assert.Single(run.Frames);
        }

        [Fact]
        public async Task Consecutive_inline_loops_expand_only_on_separate_current_row_submissions()
        {
            var run = new WorkflowRunState("wfr-inline-consecutive", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-inline-consecutive",
                Strictness = "off",
                Steps = new[]
                {
                    InlineExpansionStep(new[] { "North", "South" }, id: "first-loop", number: 1),
                    InlineExpansionStep(new[] { "East" }, id: "second-loop", number: 2),
                },
            }, null);

            await WorkflowRunner.SubmitStepAsync(run, "first-loop", null, null);
            Assert.Equal("first-loop#0", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            Assert.Equal(new[] { "first-loop#0", "first-loop#1", "second-loop" }, run.Plan.Select(p => p.InstanceId));
            Assert.False(run.Plan[2].ForEachExpanded);
            Assert.Null(run.Plan[2].IterationIndex);

            await WorkflowRunner.SubmitStepAsync(run, "first-loop#0", null, null);
            await WorkflowRunner.SubmitStepAsync(run, "first-loop#1", null, null);

            Assert.Equal("second-loop", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            Assert.False(run.Plan[2].ForEachExpanded);
            Assert.Equal(InlineSetupInstructions, WorkflowRunner.BuildView(run).CurrentStep.Instructions);

            await WorkflowRunner.SubmitStepAsync(run, "second-loop", null, null);
            Assert.Equal("second-loop#0", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            Assert.True(run.Plan[2].ForEachExpanded);
            Assert.Equal(0, run.Plan[2].IterationIndex);
        }

        [Fact]
        public async Task Shared_literal_array_is_copied_and_the_authored_definition_is_unchanged()
        {
            var shared = new[] { "  North  ", "East, West" };
            var first = InlineExpansionStep(shared, id: "first-loop", number: 1);
            var second = InlineExpansionStep(shared, id: "second-loop", number: 2);
            var run = new WorkflowRunState("wfr-inline-shared", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-inline-shared",
                Strictness = "off",
                Steps = new[] { first, second },
            }, null);
            Assert.Same(shared, first.ForEach.InLiteral);
            Assert.Same(shared, second.ForEach.InLiteral);

            await WorkflowRunner.SubmitStepAsync(run, "first-loop", null, null);

            Assert.Same(shared, first.ForEach.InLiteral);
            Assert.Same(shared, second.ForEach.InLiteral);
            Assert.Equal(new[] { "  North  ", "East, West" }, first.ForEach.InLiteral);
            Assert.Equal(new[] { "  North  ", "East, West" }, run.Frames.Skip(1).Take(2).Select(f => f.LoopValue));
            shared[0] = "changed";
            Assert.Equal("  North  ", run.Frames[1].LoopValue);
            // [T220] The run's own definition is a frozen deep copy, so it does not share the authored array
            // at all: a later edit of `shared` cannot reach the run even before expansion copies the values.
            Assert.NotSame(shared, run.Def.Steps[0].ForEach.InLiteral);
            Assert.NotSame(shared, run.Def.Steps[1].ForEach.InLiteral);
            Assert.Equal(new[] { "  North  ", "East, West" }, run.Def.Steps[0].ForEach.InLiteral);
            Assert.Equal(new[] { "  North  ", "East, West" }, run.Def.Steps[1].ForEach.InLiteral);
        }

        [Fact]
        public async Task Unexpanded_inline_template_projects_setup_then_restores_authored_metadata_on_hash_zero()
        {
            var run = InlineExpansionRun("wfr-inline-project", new[] { "  North  ", "East, West" });
            var before = WorkflowRunner.BuildView(run);
            Assert.Equal("inline-loop", before.CurrentStep.StepId);
            Assert.Equal("Inline invented loop", before.CurrentStep.Title);
            Assert.Equal(InlineSetupInstructions, before.CurrentStep.Instructions);
            Assert.Empty(before.CurrentStep.Questions);
            Assert.Empty(before.CurrentStep.VerifyKinds);
            Assert.Empty(before.CurrentStep.Ops);
            Assert.Null(before.CurrentStep.EffectiveStrictness);
            Assert.True(before.TotalStepsProvisional);

            await WorkflowRunner.SubmitStepAsync(run, "inline-loop", null, null);

            var after = WorkflowRunner.BuildView(run);
            Assert.Equal("inline-loop#0", after.CurrentStep.StepId);
            Assert.Equal("Inline invented loop", after.CurrentStep.Title);
            Assert.Equal("Authored body: review   North   at 0.", after.CurrentStep.Instructions);
            Assert.NotEqual(InlineSetupInstructions, after.CurrentStep.Instructions);
            Assert.Equal(new[] { "finding", "note" }, after.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(new[] { "invented_check" }, after.CurrentStep.VerifyKinds);
            Assert.Equal(new[] { "edit_measure" }, after.CurrentStep.Ops);
            Assert.Equal("hard", after.CurrentStep.EffectiveStrictness);
        }

        // ---- unit 17: one deferred forEach list source on the immediately preceding step ----------

        [Fact]
        public async Task Deferred_source_answer_expands_the_following_loop_in_the_same_submission()
        {
            var run = InputBackedRun("wfr-u17-expand", out _);
            await WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North, South"), null);

            Assert.Equal("done", run.Results[0].Status);          // an answer with no check is proof-free
            Assert.Equal("North, South", run.Results[0].Answers["regions"].Value);
            AssertPairedDecision(run.Results[0], "expand", "applied", new[] { "North", "South" }, "review-region");
            Assert.Equal(1, run.StepIndex);
            Assert.Equal(new[] { "choose", "review-region#0", "review-region#1" }, run.Plan.Select(p => p.InstanceId));
            Assert.Equal(run.Plan.Select(p => p.InstanceId), run.Results.Select(r => r.StepId));
            Assert.Equal(new[] { "done", "in_progress", "pending" }, run.Results.Select(r => r.Status));   // choose ran no check: done, not passed
            Assert.Equal(new int?[] { null, 0, 1 }, run.Plan.Select(p => p.IterationIndex));
            Assert.True(run.Plan[1].ForEachExpanded);
            Assert.True(run.Plan[2].ForEachExpanded);
            Assert.Equal(3, run.Frames.Count);
            var iterations = run.Frames.Where(f => f.ProjectionKind == "iteration").ToArray();
            Assert.Equal(new[] { "North", "South" }, iterations.Select(f => f.LoopValue));
            var view = WorkflowRunner.BuildView(run);
            Assert.Equal("review-region#0", view.CurrentStep.StepId);
            Assert.Equal(new[] { "finding" }, view.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal("Authored body: review North at 0.", view.CurrentStep.Instructions);
            Assert.Equal(new[] { "edit_measure" }, view.CurrentStep.Ops);
            Assert.Equal(new[] { "invented_check" }, view.CurrentStep.VerifyKinds);
            Assert.Equal("hard", view.CurrentStep.EffectiveStrictness);
            Assert.False(view.TotalStepsProvisional);
            Assert.Empty(run.Results[1].Answers);
            Assert.Empty(run.Results[2].Answers);
            AssertNoDecision(run.Results[1]);
            AssertNoDecision(run.Results[2]);
        }

        [Fact]
        public async Task Deferred_expansion_seeds_each_iteration_from_the_answering_row_and_isolates_them()
        {
            var submitted = new AnswerValue { Value = "North, South" };
            var run = InputBackedRun("wfr-u17-seeds", out _);
            await WorkflowRunner.SubmitStepAsync(run, "choose", new Dictionary<string, AnswerValue> { ["regions"] = submitted }, null);

            var first = new Dictionary<string, AnswerValue>();
            var second = new Dictionary<string, AnswerValue>();
            run.Frames[1].CopyAnswerSeedTo(first);
            run.Frames[2].CopyAnswerSeedTo(second);
            Assert.Equal("North, South", first["regions"].Value);
            Assert.Equal("North, South", second["regions"].Value);
            Assert.NotSame(first["regions"], second["regions"]);
            first["regions"].Value = "changed";
            var firstAgain = new Dictionary<string, AnswerValue>();
            run.Frames[1].CopyAnswerSeedTo(firstAgain);
            Assert.Equal("North, South", firstAgain["regions"].Value);
            Assert.Equal("North, South", run.Results[0].Answers["regions"].Value);
            Assert.Equal("North, South", submitted.Value);
            Assert.NotSame(submitted, run.Results[0].Answers["regions"]);
            Assert.NotSame(run.Results[0].Answers["regions"], first["regions"]);
        }

        [Fact]
        public async Task Deferred_expansion_runs_after_the_declaring_step_gate_verify_and_pass_is_decided()
        {
            var run = InputBackedRun("wfr-u17-during-verify", out _);
            var verifyCalls = 0;
            WorkflowVerifyExecutor executor = (spec, step, state, answers) =>
            {
                verifyCalls++;
                Assert.Equal(2, state.Plan.Count);
                Assert.DoesNotContain(state.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));
                AssertPairedDecision(state.Results[0], "expand", "pending", new[] { "North", "South" }, "review-region");
                Assert.Equal("North, South", state.Results[0].Answers["regions"].Value);
                return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed" });
            };

            await WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North, South"), executor);

            Assert.Equal(1, verifyCalls);
            Assert.Single(run.Results[0].VerifyHistory);
            AssertPairedDecision(run.Results[0], "expand", "applied", new[] { "North", "South" }, "review-region");
            Assert.Equal(3, run.Plan.Count);
        }

        [Fact]
        public async Task Deferred_hard_gate_failure_keeps_the_answer_and_the_unapplied_decision_together()
        {
            var run = InputBackedRun("wfr-u17-hard-fail", out _);
            WorkflowVerifyExecutor fail = (spec, step, state, answers) =>
                Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = "invented evidence" });

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North, South"), fail));
            Assert.Contains("hard gate", error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.Equal("failed", run.Results[0].Status);
            Assert.Contains("hard gate blocked", run.Results[0].Note, StringComparison.Ordinal);
            Assert.Equal("North, South", run.Results[0].Answers["regions"].Value);
            AssertPairedDecision(run.Results[0], "expand", "pending", new[] { "North", "South" }, "review-region");
            Assert.Equal("failed", Assert.Single(run.Results[0].VerifyResults).Status);
            Assert.Single(run.Results[0].VerifyHistory);
            Assert.Equal(2, run.Plan.Count);
            Assert.False(run.Plan[1].ForEachExpanded);
            Assert.Equal("review-region", run.Plan[1].InstanceId);
            Assert.Single(run.Frames);
            Assert.Equal(0, run.StepIndex);
            var view = WorkflowRunner.BuildView(run);
            Assert.Equal("choose", view.CurrentStep.StepId);
            Assert.Equal("Choose the invented list.", view.CurrentStep.Instructions);
            Assert.NotEqual(PreparationSetupInstructions, view.CurrentStep.Instructions);
            var pending = run.Results[0].PendingForEach;
            WorkflowRunner.Abort(run, "invented abort after hard failure");
            Assert.Equal("aborted", run.Status);
            Assert.Same(pending, run.Results[0].PendingForEach);
            AssertPairedDecision(run.Results[0], "expand", "pending", new[] { "North", "South" }, "review-region");
        }

        [Fact]
        public async Task Deferred_retry_after_a_hard_failure_redecides_and_replaces_the_stale_decision()
        {
            var run = InputBackedRun("wfr-u17-retry", out _);
            WorkflowVerifyExecutor fail = (spec, step, state, answers) =>
                Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = "invented evidence" });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North, South"), fail));

            var omit = await AssertAtomicPreparationRefusal(run, "choose", new Dictionary<string, AnswerValue>());
            Assert.Contains("unanswered", omit.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("failed", run.Results[0].Status);
            Assert.Equal("North, South", run.Results[0].Answers["regions"].Value);
            AssertPairedDecision(run.Results[0], "expand", "pending", new[] { "North", "South" }, "review-region");

            WorkflowVerifyExecutor pass = (spec, step, state, answers) =>
                Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed" });
            await WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North"), pass);

            AssertPairedDecision(run.Results[0], "expand", "applied", new[] { "North" }, "review-region");
            Assert.Equal(new[] { "choose", "review-region#0" }, run.Plan.Select(p => p.InstanceId));
            Assert.Equal(2, run.Frames.Count);
            var seed = new Dictionary<string, AnswerValue>();
            run.Frames[1].CopyAnswerSeedTo(seed);
            Assert.Equal("North", seed["regions"].Value);
            Assert.Equal("North", run.Results[0].Answers["regions"].Value);
            Assert.Equal(2, run.Results[0].VerifyHistory.Count);
            Assert.Equal("passed", Assert.Single(run.Results[0].VerifyResults).Status);
            Assert.Equal(1, run.StepIndex);
            Assert.Equal("review-region#0", WorkflowRunner.BuildView(run).CurrentStep.StepId);
        }

        [Fact]
        public async Task Over_bound_deferred_loop_refuses_before_any_answer_decision_evidence_or_status_change()
        {
            var run = InputBackedRun("wfr-u17-overbound", out _);
            run.Def.Steps[1].ForEach.MaxIterations = 2;
            var error = await AssertAtomicPreparationRefusal(run, "choose", Regions("North, South, East"));
            AssertNoDecision(run.Results[0]);
            Assert.Contains("3", error.Message);
            Assert.Contains("2", error.Message);
            Assert.Contains("review-region", error.Message);
            Assert.Contains("skip_workflow_step", error.Message);

            await WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North, South"), null);
            Assert.Equal(3, run.Plan.Count);
            AssertPairedDecision(run.Results[0], "expand", "applied", new[] { "North", "South" }, "review-region");
        }

        [Fact]
        public async Task Declined_deferred_source_refuses_before_mutation_and_names_the_skip_route()
        {
            var optional = InputBackedRun("wfr-u17-decline-opt", out _);
            var declined = await AssertAtomicPreparationRefusal(optional, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Declined = true, DeclineReason = "invented decline" },
            });
            Assert.Contains("declined", declined.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("invented decline", declined.Message);
            Assert.Contains("review-region", declined.Message);
            Assert.Contains("skip_workflow_step", declined.Message);

            var aod = InputBackedRun("wfr-u17-decline-aod", out _);
            aod.Def.Steps[0].Gate.Inputs[0].Required = "answer-or-decline";
            var still = await AssertAtomicPreparationRefusal(aod, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Declined = true, DeclineReason = "still declined" },
            });
            Assert.Contains("declined", still.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("skip_workflow_step", still.Message);
        }

        [Fact]
        public async Task Unanswered_deferred_source_refuses_with_the_gap_sentence_whatever_the_shape_of_the_gap()
        {
            var omitted = await AssertAtomicPreparationRefusal(
                InputBackedRun("wfr-u17-gap-omit", out _), "choose", new Dictionary<string, AnswerValue>());
            Assert.Contains("unanswered", omitted.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("regions", omitted.Message);

            // The required Unit 9 gap sentence must SAY the word "empty" -- its whole job is to teach that an
            // answered blank list is an empty loop while an unanswered one is a gap. Banning the word would
            // ban the sentence. The gap is proved by the STATE the refusal leaves behind instead: no
            // decision, no expansion, no committed answer -- none of which an empty list would produce.
            var nullRun = InputBackedRun("wfr-u17-gap-null", out _);
            var nullValued = await AssertAtomicPreparationRefusal(nullRun, "choose",
                new Dictionary<string, AnswerValue> { ["regions"] = new AnswerValue { Value = null } });
            Assert.Contains("unanswered", nullValued.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("is a gap", nullValued.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(nullRun.Results[0].PendingForEach);
            Assert.False(nullRun.Results[0].Answers.ContainsKey("regions"));
            Assert.Equal(new[] { "choose", "review-region" }, nullRun.Plan.Select(p => p.InstanceId));
            Assert.False(nullRun.Plan[1].ForEachExpanded);
            Assert.NotEqual("not_applicable", nullRun.Results[1].Status);

            var dropped = await AssertAtomicPreparationRefusal(
                InputBackedRun("wfr-u17-gap-drop", out _), "choose",
                new Dictionary<string, AnswerValue> { ["region"] = new AnswerValue { Value = "North" } });
            Assert.Contains("unanswered", dropped.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("regions", dropped.Message);

            var earlier = new WorkflowRunState("wfr-u17-gap-earlier", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-earlier-answer",
                Version = 2,
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
                        Instructions = "Choose the invented list.",
                        Ops = new[] { "ask_user" },
                        Gate = new GateSpec
                        {
                            Inputs = new[] { new GateInput { Name = "regions", Question = "Which invented regions?", Required = "optional" } },
                            Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                        },
                    },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 3, Title = "Review invented region",
                        Instructions = AuthoredPreparationInstructions,
                        Ops = new[] { "edit_measure" },
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                        Gate = new GateSpec
                        {
                            Inputs = new[] { new GateInput { Name = "finding", Required = "optional" } },
                            Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                        },
                    },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(earlier, "alpha", Regions("North, South"), null);
            AssertNoDecision(earlier.Results[0]);
            await WorkflowRunner.SubmitStepAsync(earlier, "choose", new Dictionary<string, AnswerValue>(), null);
            AssertPairedDecision(earlier.Results[1], "expand", "applied", new[] { "North", "South" }, "review-region");
            Assert.Equal("review-region#0", WorkflowRunner.BuildView(earlier).CurrentStep.StepId);
        }

        [Fact]
        public async Task Blank_deferred_source_is_an_empty_loop_only_when_the_declaration_is_optional()
        {
            foreach (var blank in new[] { "  ", ", ," })
            {
                var withAfter = InputBackedRun("wfr-u17-empty-after-" + blank.Length, out _);
                withAfter.Plan.Add(WorkflowOwnerFixtures.ForeignRow(
                    new WorkflowStep { Id = "after", Number = 3, Title = "After", Instructions = "Authored next-step body.", Ops = new[] { "ask_user" } },
                    "after", withAfter.RunId));
                withAfter.Results.Add(new StepResult { StepId = "after", Title = "After", Status = "pending" });
                var kept = withAfter.Results[1];
                await WorkflowRunner.SubmitStepAsync(withAfter, "choose", Regions(blank), null);
                Assert.Equal("done", withAfter.Results[0].Status);     // a blank answer with no check is proof-free
                AssertPairedDecision(withAfter.Results[0], "empty", "applied", Array.Empty<string>(), "review-region");
                Assert.Same(kept, withAfter.Results[1]);
                Assert.True(withAfter.Plan[1].ForEachExpanded);
                Assert.Null(withAfter.Plan[1].IterationIndex);
                Assert.Equal("not_applicable", withAfter.Results[1].Status);
                Assert.Equal("did not apply: the forEach value list was empty.", withAfter.Results[1].Note);
                Assert.Empty(withAfter.Results[1].Answers);
                AssertNoDecision(withAfter.Results[1]);
                Assert.DoesNotContain(withAfter.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));
                Assert.Single(withAfter.Frames);
                Assert.Equal(2, withAfter.StepIndex);
                Assert.Equal("in_progress", withAfter.Results[2].Status);
                Assert.Equal("Authored next-step body.", WorkflowRunner.BuildView(withAfter).CurrentStep.Instructions);
            }

            var terminal = InputBackedRun("wfr-u17-empty-last", out _);
            await WorkflowRunner.SubmitStepAsync(terminal, "choose", Regions("  "), null);
            Assert.Equal("completed", terminal.Status);
            Assert.NotNull(terminal.FinishedUtc);
            Assert.Null(WorkflowRunner.BuildView(terminal).CurrentStep);
            Assert.Equal("not_applicable", terminal.Results[1].Status);

            foreach (var required in new[] { "required", "answer-or-decline" })
            {
                foreach (var strictness in new[] { "hard", "off" })
                {
                    var run = InputBackedRun("wfr-u17-blank-" + required + strictness, out _);
                    run.Def.Strictness = strictness;
                    run.Def.Steps[0].Gate.Inputs[0].Required = required;
                    var error = await AssertAtomicPreparationRefusal(run, "choose", Regions("  "));
                    Assert.Contains("Which invented regions?", error.Message);
                    Assert.Contains("regions", error.Message);
                    Assert.Contains("skip_workflow_step", error.Message, StringComparison.Ordinal);
                }
            }
        }

        [Fact]
        public async Task Advance_does_not_resurrect_a_row_whose_outcome_was_already_decided()
        {
            var decided = new WorkflowRunState("wfr-u17-advance-stomp", new WorkflowDef
            {
                Name = "invented-advance-stomp",
                Steps = new[]
                {
                    new WorkflowStep { Id = "first", Number = 1, Title = "First" },
                    new WorkflowStep { Id = "already", Number = 2, Title = "Already decided" },
                    new WorkflowStep { Id = "third", Number = 3, Title = "Third", Instructions = "Authored third." },
                },
            }, null);
            decided.Results[1].Status = "not_applicable";
            decided.Results[1].Note = "already decided";
            await WorkflowRunner.SubmitStepAsync(decided, "first", null, null);
            Assert.Equal("not_applicable", decided.Results[1].Status);
            Assert.Equal("already decided", decided.Results[1].Note);
            Assert.Equal(2, decided.StepIndex);
            Assert.Equal("in_progress", decided.Results[2].Status);
            Assert.Equal("Authored third.", WorkflowRunner.BuildView(decided).CurrentStep.Instructions);

            var pending = new WorkflowRunState("wfr-u17-advance-pending", new WorkflowDef
            {
                Name = "invented-advance-pending",
                Steps = new[]
                {
                    new WorkflowStep { Id = "first", Number = 1, Title = "First" },
                    new WorkflowStep { Id = "second", Number = 2, Title = "Second" },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(pending, "first", null, null);
            Assert.Equal("in_progress", pending.Results[1].Status);
            Assert.Equal(1, pending.StepIndex);

            var noLoop = new WorkflowRunState("wfr-u17-advance-noloop", new WorkflowDef
            {
                Name = "invented-noloop-advance",
                Steps = new[]
                {
                    new WorkflowStep { Id = "first", Number = 1, Title = "First" },
                    new WorkflowStep { Id = "second", Number = 2, Title = "Second" },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(noLoop, "first", null, null);
            Assert.Equal("done", noLoop.Results[0].Status);        // no gate, no check: proof-free
            Assert.Equal("in_progress", noLoop.Results[1].Status);
            Assert.Equal(1, noLoop.StepIndex);
        }

        [Fact]
        public async Task A_step_that_does_not_own_the_next_row_decides_nothing_and_is_never_refused_for_it()
        {
            var nearest = new WorkflowRunState("wfr-u17-own-nearest", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-nearest",
                Version = 2,
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
                        Instructions = "Choose the invented list.",
                        Gate = new GateSpec
                        {
                            Inputs = new[] { new GateInput { Name = "regions", Question = "Which invented regions?", Required = "optional" } },
                        },
                    },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 3, Title = "Review invented region",
                        Instructions = AuthoredPreparationInstructions,
                        Ops = new[] { "edit_measure" },
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "finding", Required = "optional" } } },
                    },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(nearest, "alpha", Regions("North, South"), null);
            Assert.Equal("skipped", nearest.Results[0].Status);    // this run's strictness is off, so its gate was off
            AssertNoDecision(nearest.Results[0]);
            Assert.Equal(3, nearest.Plan.Count);
            Assert.False(nearest.Plan[2].ForEachExpanded);
            await WorkflowRunner.SubmitStepAsync(nearest, "choose", new Dictionary<string, AnswerValue>(), null);
            AssertPairedDecision(nearest.Results[1], "expand", "applied", new[] { "North", "South" }, "review-region");
            Assert.Equal(4, nearest.Plan.Count);

            var between = new WorkflowRunState("wfr-u17-own-between", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-between",
                Version = 2,
                Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "choose", Number = 1, Title = "Choose invented regions",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
                    },
                    new WorkflowStep { Id = "middle", Number = 2, Title = "Middle" },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 3, Title = "Review invented region",
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                    },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(between, "choose", Regions("North, South"), null);
            Assert.Equal("skipped", between.Results[0].Status);    // gate off under this run's strictness
            AssertNoDecision(between.Results[0]);
            Assert.Equal(3, between.Plan.Count);
            await WorkflowRunner.SubmitStepAsync(between, "middle", null, null);
            Assert.Equal("done", between.Results[1].Status);    // ungated and unverified: proof-free
            AssertNoDecision(between.Results[1]);
            Assert.False(between.Plan[2].ForEachExpanded);

            var undeclared = InputBackedRun("wfr-u17-own-undeclared", out _);
            undeclared.Def.Steps[0].Gate.Inputs = new[] { new GateInput { Name = "note", Required = "optional" } };
            await WorkflowRunner.SubmitStepAsync(undeclared, "choose", new Dictionary<string, AnswerValue>
            {
                ["note"] = new AnswerValue { Value = "hi" },
            }, null);
            Assert.Equal("done", undeclared.Results[0].Status);   // hard gate, but no executor handed in: the row carries no proof
            AssertNoDecision(undeclared.Results[0]);
            Assert.Equal(2, undeclared.Plan.Count);

            var prep = new WorkflowRunState("wfr-u17-own-prep", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-own-prep",
                Version = 2,
                Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "choose", Number = 1, Title = "Choose invented regions",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
                    },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 2, Title = "Review invented region",
                        Instructions = AuthoredPreparationInstructions,
                        Ops = new[] { "edit_measure" },
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                        Gate = new GateSpec
                        {
                            Inputs = new[] { new GateInput { Name = "regions", Question = "Which invented regions?", Required = "optional" } },
                            Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                        },
                    },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(prep, "choose", Regions("North, South"), null);
            Assert.Equal("skipped", prep.Results[0].Status);   // gate off under this run's strictness
            AssertNoDecision(prep.Results[0]);
            Assert.Equal(2, prep.Plan.Count);
            Assert.False(prep.Plan[1].ForEachExpanded);
            var view = WorkflowRunner.BuildView(prep);
            Assert.Equal("review-region", view.CurrentStep.StepId);
            Assert.Equal(PreparationSetupInstructions, view.CurrentStep.Instructions);
        }

        [Fact]
        public async Task Two_deferred_loops_fed_by_one_source_expand_the_second_on_its_own_setup_call()
        {
            var run = new WorkflowRunState("wfr-u17-two-loops", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-two-loops",
                Version = 2,
                Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "choose", Number = 1, Title = "Choose invented regions",
                        Instructions = "Choose the invented list.",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Question = "Which invented regions?", Required = "optional" } } },
                    },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 2, Title = "Review invented region",
                        Instructions = AuthoredPreparationInstructions,
                        Ops = new[] { "edit_measure" },
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "finding", Required = "optional" } } },
                    },
                    new WorkflowStep
                    {
                        Id = "review-again", Number = 3, Title = "Review again",
                        Instructions = "Authored body: review [[loop.region]] again.",
                        Ops = new[] { "edit_measure" },
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                    },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North, South"), null);
            AssertPairedDecision(run.Results[0], "expand", "applied", new[] { "North", "South" }, "review-region");
            Assert.Equal("review-again", run.Plan[3].InstanceId);
            Assert.False(run.Plan[3].ForEachExpanded);
            AssertNoDecision(run.Results[3]);

            await WorkflowRunner.SubmitStepAsync(run, "review-region#0", null, null);
            await WorkflowRunner.SubmitStepAsync(run, "review-region#1", null, null);
            var waiting = WorkflowRunner.BuildView(run);
            Assert.Equal("review-again", waiting.CurrentStep.StepId);
            Assert.Equal(DeferredSetupInstructions, waiting.CurrentStep.Instructions);
            Assert.Empty(waiting.CurrentStep.Questions);
            Assert.False(run.Plan[3].ForEachExpanded);

            await WorkflowRunner.SubmitStepAsync(run, "review-again", null, null);
            Assert.Equal("review-again#0", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            Assert.True(run.Plan[3].ForEachExpanded);
            Assert.True(run.Plan[4].ForEachExpanded);
            AssertNoDecision(run.Results[3]);
            AssertPairedDecision(run.Results[0], "expand", "applied", new[] { "North", "South" }, "review-region");
        }

        [Fact]
        public async Task Loop_free_when_on_the_following_deferred_loop_decides_expansion_without_creating_stray_iterations()
        {
            async Task<WorkflowRunState> RunWithWhen(string id, string when, string list = "North, South")
            {
                var run = InputBackedRun(id, out _);
                run.Def.Steps[1].When = when;
                await WorkflowRunner.SubmitStepAsync(run, "choose", Regions(list), null);
                return run;
            }

            var holds = await RunWithWhen("wfr-u17-when-true", "inputs.regions.answered");
            AssertPairedDecision(holds.Results[0], "expand", "applied", new[] { "North", "South" }, "review-region");
            Assert.Equal(3, holds.Plan.Count);
            Assert.NotEqual("not_applicable", holds.Results[1].Status);

            // Unit 17's declaring submission still decides condition_false and still splices nothing. What it
            // no longer does is leave the advance walk to record the target's outcome: the walk defers EVERY
            // condition on an unexpanded template, so the row is reached current and undecided, and its own
            // base-id setup call validates the source and then records the zero-iteration outcome.
            var falsy = await RunWithWhen("wfr-u17-when-false", "inputs.missing.answered");
            AssertPairedDecision(falsy.Results[0], "condition_false", "applied", new[] { "North", "South" }, "review-region");
            Assert.Equal(2, falsy.Plan.Count);
            Assert.False(falsy.Plan[1].ForEachExpanded);
            Assert.Equal("in_progress", falsy.Results[1].Status);
            Assert.Null(falsy.Results[1].Note);
            var waitingFalsy = WorkflowRunner.BuildView(falsy);
            Assert.Equal("review-region", waitingFalsy.CurrentStep.StepId);
            Assert.Equal(DeferredSetupInstructions, waitingFalsy.CurrentStep.Instructions);
            await WorkflowRunner.SubmitStepAsync(falsy, "review-region", null, null);
            Assert.Equal("not_applicable", falsy.Results[1].Status);
            Assert.Contains("did not apply: condition", falsy.Results[1].Note);
            Assert.False(falsy.Plan[1].ForEachExpanded);
            Assert.True(WorkflowRunner.BuildView(falsy).TotalStepsProvisional);
            Assert.Single(falsy.Frames);
            Assert.DoesNotContain(falsy.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));
            Assert.Equal("North, South", falsy.Results[0].Answers["regions"].Value);

            var unread = await RunWithWhen("wfr-u17-when-unread", "not a condition");
            AssertPairedDecision(unread.Results[0], "condition_false", "applied", new[] { "North", "South" }, "review-region");
            Assert.Equal("in_progress", unread.Results[1].Status);
            Assert.Null(unread.Results[1].Note);
            await WorkflowRunner.SubmitStepAsync(unread, "review-region", null, null);
            Assert.Equal("not_applicable", unread.Results[1].Status);
            Assert.Contains("unreadable", unread.Results[1].Note);
            Assert.DoesNotContain(unread.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));

            var over = InputBackedRun("wfr-u17-when-over", out _);
            over.Def.Steps[1].When = "inputs.missing.answered";
            over.Def.Steps[1].ForEach.MaxIterations = 1;
            var overErr = await AssertAtomicPreparationRefusal(over, "choose", Regions("North, South"));
            Assert.Contains("2", overErr.Message);
            Assert.Contains("1", overErr.Message);
            var gap = InputBackedRun("wfr-u17-when-gap", out _);
            gap.Def.Steps[1].When = "inputs.missing.answered";
            var gapErr = await AssertAtomicPreparationRefusal(gap, "choose", new Dictionary<string, AnswerValue>());
            Assert.Contains("unanswered", gapErr.Message, StringComparison.OrdinalIgnoreCase);

            var agree = await RunWithWhen("wfr-u17-when-agree", "inputs.regions.answered");
            AssertPairedDecision(agree.Results[0], "expand", "applied", new[] { "North", "South" }, "review-region");
            Assert.NotEqual("not_applicable", agree.Results[1].Status);
        }

        [Fact]
        public async Task Loop_fact_when_on_the_following_deferred_loop_is_judged_per_iteration_not_before_expansion()
        {
            async Task<WorkflowRunState> Submit(string id, string when, string list = "North, South", string required = "optional")
            {
                var run = InputBackedRun(id, out _);
                run.Def.Steps[0].Gate.Inputs[0].Required = required;
                run.Def.Steps[1].When = when;
                await WorkflowRunner.SubmitStepAsync(run, "choose", Regions(list), null);
                return run;
            }

            var asSelect = await Submit("wfr-u17-loop-as", "loop.region == 'North'");
            AssertPairedDecision(asSelect.Results[0], "expand", "applied", new[] { "North", "South" }, "review-region");
            Assert.NotEqual("condition_false", asSelect.Results[0].PendingForEach.Outcome);
            Assert.Equal(new[] { "choose", "review-region#0", "review-region#1" }, asSelect.Plan.Select(p => p.InstanceId));
            Assert.Equal(3, asSelect.Frames.Count);
            Assert.Equal("in_progress", asSelect.Results[1].Status);
            Assert.Equal("review-region#0", WorkflowRunner.BuildView(asSelect).CurrentStep.StepId);
            await WorkflowRunner.SubmitStepAsync(asSelect, "review-region#0", null, null);
            Assert.Equal("not_applicable", asSelect.Results[2].Status);
            Assert.Contains("loop.region=South", asSelect.Results[2].Note);

            var indexSelect = await Submit("wfr-u17-loop-index", "loop.index == 1");
            AssertPairedDecision(indexSelect.Results[0], "expand", "applied", new[] { "North", "South" }, "review-region");
            Assert.Equal("not_applicable", indexSelect.Results[1].Status);
            Assert.Contains("loop.index=0", indexSelect.Results[1].Note);
            // `#1` is plan index 2, not 1: `choose` is 0 and the walk stepped past `#0`.
            Assert.Equal(2, indexSelect.StepIndex);
            Assert.Equal("review-region#1", indexSelect.Plan[indexSelect.StepIndex].InstanceId);
            Assert.Equal("in_progress", indexSelect.Results[2].Status);
            Assert.Equal("Authored body: review South at 1.", WorkflowRunner.BuildView(indexSelect).CurrentStep.Instructions);

            var mixedFull = new WorkflowRunState("wfr-u17-loop-mixed-full", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-mixed", Version = 2, Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "mode", Number = 1, Title = "Mode",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "mode", Required = "optional" } } },
                    },
                    new WorkflowStep
                    {
                        Id = "choose", Number = 2, Title = "Choose invented regions",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Question = "Which invented regions?", Required = "optional" } } },
                    },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 3, Title = "Review invented region",
                        Instructions = AuthoredPreparationInstructions,
                        When = "inputs.mode.value == 'full' && loop.region == 'North'",
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                    },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(mixedFull, "mode", new Dictionary<string, AnswerValue> { ["mode"] = new AnswerValue { Value = "full" } }, null);
            await WorkflowRunner.SubmitStepAsync(mixedFull, "choose", Regions("North, South"), null);
            AssertPairedDecision(mixedFull.Results[1], "expand", "applied", new[] { "North", "South" }, "review-region");
            Assert.NotEqual("condition_false", mixedFull.Results[1].PendingForEach.Outcome);
            Assert.Equal("in_progress", mixedFull.Results[2].Status);
            await WorkflowRunner.SubmitStepAsync(mixedFull, "review-region#0", null, null);
            Assert.Equal("not_applicable", mixedFull.Results[3].Status);
            Assert.Contains("loop.region=South", mixedFull.Results[3].Note);
            Assert.Contains("inputs.mode.value=full", mixedFull.Results[3].Note);

            var mixedPartial = new WorkflowRunState("wfr-u17-loop-mixed-partial", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-mixed-partial", Version = 2, Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "mode", Number = 1, Title = "Mode",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "mode", Required = "optional" } } },
                    },
                    new WorkflowStep
                    {
                        Id = "choose", Number = 2, Title = "Choose invented regions",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Question = "Which invented regions?", Required = "optional" } } },
                    },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 3, Title = "Review invented region",
                        Instructions = AuthoredPreparationInstructions,
                        When = "inputs.mode.value == 'full' && loop.region == 'North'",
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                    },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(mixedPartial, "mode", new Dictionary<string, AnswerValue> { ["mode"] = new AnswerValue { Value = "partial" } }, null);
            await WorkflowRunner.SubmitStepAsync(mixedPartial, "choose", Regions("North, South"), null);
            AssertPairedDecision(mixedPartial.Results[1], "expand", "applied", new[] { "North", "South" }, "review-region");
            Assert.Equal(4, mixedPartial.Plan.Count);
            Assert.Equal("not_applicable", mixedPartial.Results[2].Status);
            Assert.Equal("not_applicable", mixedPartial.Results[3].Status);

            var orShape = new WorkflowRunState("wfr-u17-loop-or", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-or", Version = 2, Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "mode", Number = 1, Title = "Mode",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "mode", Required = "optional" } } },
                    },
                    new WorkflowStep
                    {
                        Id = "choose", Number = 2, Title = "Choose invented regions",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Question = "Which invented regions?", Required = "optional" } } },
                    },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 3, Title = "Review invented region",
                        Instructions = AuthoredPreparationInstructions,
                        When = "inputs.mode.value == 'full' || loop.region == 'North'",
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                    },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(orShape, "mode", new Dictionary<string, AnswerValue> { ["mode"] = new AnswerValue { Value = "partial" } }, null);
            await WorkflowRunner.SubmitStepAsync(orShape, "choose", Regions("North, South"), null);
            AssertPairedDecision(orShape.Results[1], "expand", "applied", new[] { "North", "South" }, "review-region");
            Assert.Equal("in_progress", orShape.Results[2].Status);
            Assert.Equal("review-region#0", WorkflowRunner.BuildView(orShape).CurrentStep.StepId);

            var emptyLoopWhen = await Submit("wfr-u17-loop-empty", "loop.region == 'North'", "  ");
            AssertPairedDecision(emptyLoopWhen.Results[0], "empty", "applied", Array.Empty<string>(), "review-region");
            Assert.NotEqual("condition_false", emptyLoopWhen.Results[0].PendingForEach.Outcome);
            Assert.True(emptyLoopWhen.Plan[1].ForEachExpanded);
            Assert.Null(emptyLoopWhen.Plan[1].IterationIndex);
            Assert.Equal("not_applicable", emptyLoopWhen.Results[1].Status);
            Assert.Equal("did not apply: the forEach value list was empty.", emptyLoopWhen.Results[1].Note);

            // A quoted literal that SPELLS a loop fact and a hyphenated input name that merely starts with
            // "loop" are both loop-FREE: only the parsed form's referenced roots decide, never the authored
            // text. An unexpanded plan alone cannot prove that any more, because a template deferred as a
            // loop fact is also unexpanded until its own setup call. So each control now drives that call and
            // pins the loop-free verdict: the row records its outcome with NO resolved loop binding in the
            // note, which is the one thing a text-matching implementation could not produce.
            async Task AssertDecidedLoopFree(WorkflowRunState run)
            {
                Assert.Equal(2, run.Plan.Count);
                Assert.Equal("in_progress", run.Results[1].Status);
                await WorkflowRunner.SubmitStepAsync(run, "review-region", null, null);
                Assert.Equal("not_applicable", run.Results[1].Status);
                Assert.Contains("did not apply: ", run.Results[1].Note, StringComparison.Ordinal);
                Assert.DoesNotContain("loop.index=", run.Results[1].Note, StringComparison.Ordinal);
                Assert.DoesNotContain("loop.region=", run.Results[1].Note, StringComparison.Ordinal);
                Assert.Equal(2, run.Plan.Count);
                Assert.DoesNotContain(run.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));
            }

            var quoted = await Submit("wfr-u17-loop-quoted", "inputs.team.value == 'loop.index'");
            AssertPairedDecision(quoted.Results[0], "condition_false", "applied", new[] { "North", "South" }, "review-region");
            await AssertDecidedLoopFree(quoted);

            var hyphen = await Submit("wfr-u17-loop-hyphen", "inputs.loop-mode.answered");
            AssertPairedDecision(hyphen.Results[0], "condition_false", "applied", new[] { "North", "South" }, "review-region");
            await AssertDecidedLoopFree(hyphen);

            // The contrast that gives those two their meaning: a REAL loop fact expands and is judged per
            // iteration, so its excluded rows do carry resolved loop bindings.
            var semantic = await Submit("wfr-u17-loop-semantic", "loop.index ~ 'x'");
            AssertPairedDecision(semantic.Results[0], "expand", "applied", new[] { "North", "South" }, "review-region");
            Assert.Equal(3, semantic.Plan.Count);
            Assert.Equal(new[] { "review-region#0", "review-region#1" }, semantic.Plan.Skip(1).Select(p => p.InstanceId));
            Assert.All(semantic.Results.Skip(1), result => Assert.Equal("not_applicable", result.Status));
            Assert.Contains("loop.index=0", semantic.Results[1].Note, StringComparison.Ordinal);
            Assert.Contains("loop.index=1", semantic.Results[2].Note, StringComparison.Ordinal);

            var structural = await Submit("wfr-u17-loop-struct", "not a condition");
            AssertPairedDecision(structural.Results[0], "condition_false", "applied", new[] { "North", "South" }, "review-region");
            Assert.Equal("in_progress", structural.Results[1].Status);
            Assert.Null(structural.Results[1].Note);
            await WorkflowRunner.SubmitStepAsync(structural, "review-region", null, null);
            Assert.Equal("not_applicable", structural.Results[1].Status);
            Assert.Contains("unreadable", structural.Results[1].Note);
            Assert.DoesNotContain(structural.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));

            var over = InputBackedRun("wfr-u17-loop-over", out _);
            over.Def.Steps[1].When = "loop.region == 'North'";
            over.Def.Steps[1].ForEach.MaxIterations = 1;
            var overErr = await AssertAtomicPreparationRefusal(over, "choose", Regions("North, South"));
            Assert.Contains("2", overErr.Message);
            Assert.Contains("1", overErr.Message);
            AssertNoDecision(over.Results[0]);
        }

        [Fact]
        public async Task Current_unexpanded_deferred_loop_expands_when_usable_and_stays_skippable_when_unusable()
        {
            var skipped = InputBackedRun("wfr-u17-orphan-skip", out _);
            WorkflowRunner.SkipStep(skipped, "choose", "invented skip of declarer");
            Assert.Equal("review-region", WorkflowRunner.BuildView(skipped).CurrentStep.StepId);
            var view = WorkflowRunner.BuildView(skipped);
            Assert.Equal(DeferredSetupInstructions, view.CurrentStep.Instructions);
            Assert.Empty(view.CurrentStep.Questions);
            Assert.NotEqual(PreparationSetupInstructions, view.CurrentStep.Instructions);
            Assert.NotEqual(InlineSetupInstructions, view.CurrentStep.Instructions);
            var refused = await AssertAtomicPreparationRefusal(skipped, "review-region", null);
            Assert.Contains("regions", refused.Message);
            Assert.Contains("skip_workflow_step", refused.Message);
            WorkflowRunner.SkipStep(skipped, "review-region", "invented skip of orphan");
            Assert.Equal("skipped", skipped.Results[1].Status);

            var falseWhen = InputBackedRun("wfr-u17-orphan-when", out _);
            falseWhen.Def.Steps[0].When = "inputs.missing.answered";
            WorkflowRunner.AdvancePastInapplicableSteps(falseWhen);
            Assert.Equal("review-region", WorkflowRunner.BuildView(falseWhen).CurrentStep.StepId);
            var whenRefused = await AssertAtomicPreparationRefusal(falseWhen, "review-region", null);
            Assert.Contains("regions", whenRefused.Message);
            Assert.Contains("skip_workflow_step", whenRefused.Message);
            WorkflowRunner.SkipStep(falseWhen, "review-region", "invented skip of unusable when");
            Assert.Equal("skipped", falseWhen.Results[1].Status);

            var undeclared = new WorkflowRunState("wfr-u17-orphan-undeclared", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-orphan-undeclared", Version = 2, Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep { Id = "plain", Number = 1, Title = "Plain" },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 2, Title = "Review invented region",
                        Instructions = AuthoredPreparationInstructions,
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                    },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(undeclared, "plain", null, null);
            var noDecl = await AssertAtomicPreparationRefusal(undeclared, "review-region", null);
            Assert.Contains("regions", noDecl.Message);
            Assert.Contains("skip_workflow_step", noDecl.Message);
            WorkflowRunner.SkipStep(undeclared, "review-region", "invented skip of undeclared");
            Assert.Equal("skipped", undeclared.Results[1].Status);

            var usable = EarlierSourceRun("wfr-u17-usable-setup");
            await ReachLoopEntry(usable);
            Assert.Equal(DeferredSetupInstructions, WorkflowRunner.BuildView(usable).CurrentStep.Instructions);
            await WorkflowRunner.SubmitStepAsync(usable, "review-region", null, null);
            Assert.Equal("review-region#0", WorkflowRunner.BuildView(usable).CurrentStep.StepId);
            Assert.True(usable.Plan[2].ForEachExpanded);
            Assert.Equal(new[] { "choose", "middle", "review-region#0", "review-region#1" }, usable.Plan.Select(p => p.InstanceId));
        }

        [Fact]
        public async Task Skipping_a_declaring_step_that_already_failed_abandons_its_decision()
        {
            var run = InputBackedRun("wfr-u17-abandon", out _);
            WorkflowVerifyExecutor fail = (spec, step, state, answers) =>
                Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = "invented evidence" });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North, South"), fail));
            WorkflowRunner.SkipStep(run, "choose", "invented skip after fail");
            Assert.Equal("skipped", run.Results[0].Status);
            Assert.Contains("invented skip after fail", run.Results[0].Note);
            AssertPairedDecision(run.Results[0], "expand", "abandoned", new[] { "North", "South" }, "review-region");
            Assert.False(run.Plan[1].ForEachExpanded);
            var orphan = await AssertAtomicPreparationRefusal(run, "review-region", Regions("North"));
            Assert.Contains("regions", orphan.Message);
        }

        [Fact]
        public void PendingForEach_clone_copies_every_field_and_a_fresh_values_array()
        {
            var original = new StepResult
            {
                StepId = "choose",
                Status = "failed",
                PendingForEach = new PendingForEachExpansion
                {
                    TargetStepId = "review-region",
                    TargetIndex = 1,
                    SourceInput = "regions",
                    Values = new[] { "North", "South" },
                    Outcome = "expand",
                    State = "pending",
                },
            };

            var clone = original.Clone();
            Assert.NotSame(original, clone);
            Assert.NotSame(original.PendingForEach, clone.PendingForEach);
            Assert.NotSame(original.PendingForEach.Values, clone.PendingForEach.Values);
            AssertPairedDecision(clone, "expand", "pending", new[] { "North", "South" }, "review-region");
            Assert.Equal(1, clone.PendingForEach.TargetIndex);

            clone.PendingForEach.State = "abandoned";
            clone.PendingForEach.Outcome = "empty";
            clone.PendingForEach.TargetStepId = "other";
            clone.PendingForEach.TargetIndex = 9;
            clone.PendingForEach.SourceInput = "other";
            clone.PendingForEach.Values[0] = "East";
            clone.PendingForEach.Values = new[] { "West" };
            AssertPairedDecision(original, "expand", "pending", new[] { "North", "South" }, "review-region");
            Assert.Equal(1, original.PendingForEach.TargetIndex);
            Assert.Equal("regions", original.PendingForEach.SourceInput);

            original.PendingForEach.Values[1] = "mutated";
            Assert.Equal(new[] { "West" }, clone.PendingForEach.Values);

            var empty = new StepResult { StepId = "plain", Status = "pending" }.Clone();
            Assert.Null(empty.PendingForEach);

            var nullValues = new StepResult
            {
                PendingForEach = new PendingForEachExpansion { Outcome = "condition_false", State = "applied", Values = null },
            }.Clone();
            Assert.Null(nullValues.PendingForEach.Values);
            Assert.Equal("applied", nullValues.PendingForEach.State);
        }

        [Fact]
        public async Task Deferred_retry_replaces_the_decision_object_and_does_not_mutate_the_stale_one()
        {
            var run = InputBackedRun("wfr-u17-retry-replace", out _);
            WorkflowVerifyExecutor fail = (spec, step, state, answers) =>
                Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = "invented evidence" });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North, South"), fail));

            var stale = run.Results[0].PendingForEach;
            var staleValues = stale.Values;
            Assert.Same(stale, run.Results[0].PendingForEach);

            WorkflowVerifyExecutor pass = (spec, step, state, answers) =>
                Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed" });
            await WorkflowRunner.SubmitStepAsync(run, "choose", Regions("East"), pass);

            var live = run.Results[0].PendingForEach;
            Assert.NotSame(stale, live);
            Assert.NotSame(staleValues, live.Values);
            Assert.Equal("pending", stale.State);
            Assert.Equal("expand", stale.Outcome);
            Assert.Equal(new[] { "North", "South" }, stale.Values);
            stale.Values[0] = "mutated";
            stale.State = "abandoned";
            AssertPairedDecision(run.Results[0], "expand", "applied", new[] { "East" }, "review-region");
            Assert.Equal(new[] { "choose", "review-region#0" }, run.Plan.Select(p => p.InstanceId));
        }

        [Fact]
        public async Task Deferred_retry_can_replace_an_expand_decision_with_an_empty_one()
        {
            var run = InputBackedRun("wfr-u17-retry-empty", out _);
            WorkflowVerifyExecutor fail = (spec, step, state, answers) =>
                Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = "invented evidence" });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North, South"), fail));
            var stale = run.Results[0].PendingForEach;

            await WorkflowRunner.SubmitStepAsync(run, "choose", Regions("  "), null);

            Assert.NotSame(stale, run.Results[0].PendingForEach);
            Assert.Equal("pending", stale.State);
            AssertPairedDecision(run.Results[0], "empty", "applied", Array.Empty<string>(), "review-region");
            Assert.Equal("not_applicable", run.Results[1].Status);
            Assert.DoesNotContain(run.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));
            Assert.Equal("completed", run.Status);
        }

        [Fact]
        public async Task Skip_abandons_the_live_decision_in_place_and_leaves_a_prior_clone_pending()
        {
            var run = InputBackedRun("wfr-u17-skip-clone", out _);
            WorkflowVerifyExecutor fail = (spec, step, state, answers) =>
                Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = "invented evidence" });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North, South"), fail));

            var live = run.Results[0].PendingForEach;
            var snapshot = run.Results[0].Clone();
            Assert.NotSame(live, snapshot.PendingForEach);
            Assert.NotSame(live.Values, snapshot.PendingForEach.Values);

            WorkflowRunner.SkipStep(run, "choose", "invented skip after fail");

            Assert.Same(live, run.Results[0].PendingForEach);
            Assert.Equal("abandoned", live.State);
            Assert.Equal("skipped", run.Results[0].Status);
            AssertPairedDecision(snapshot, "expand", "pending", new[] { "North", "South" }, "review-region");
            Assert.False(run.Plan[1].ForEachExpanded);
        }

        [Fact]
        public void Skipping_a_declaring_step_that_never_decided_leaves_pendingForEach_null()
        {
            var run = InputBackedRun("wfr-u17-skip-null", out _);
            WorkflowRunner.SkipStep(run, "choose", "invented skip with no decision");
            Assert.Equal("skipped", run.Results[0].Status);
            Assert.Null(run.Results[0].PendingForEach);
            Assert.False(run.Plan[1].ForEachExpanded);
        }

        [Fact]
        public async Task Abort_preserves_pending_and_applied_decisions_and_never_abandons_them()
        {
            var pendingRun = InputBackedRun("wfr-u17-abort-pending", out _);
            WorkflowVerifyExecutor fail = (spec, step, state, answers) =>
                Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = "invented evidence" });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(pendingRun, "choose", Regions("North, South"), fail));
            var pending = pendingRun.Results[0].PendingForEach;
            var pendingValues = pending.Values;
            WorkflowRunner.Abort(pendingRun, "invented abort of a failed pair");
            Assert.Equal("aborted", pendingRun.Status);
            Assert.Same(pending, pendingRun.Results[0].PendingForEach);
            Assert.Same(pendingValues, pendingRun.Results[0].PendingForEach.Values);
            AssertPairedDecision(pendingRun.Results[0], "expand", "pending", new[] { "North", "South" }, "review-region");
            Assert.Equal("failed", pendingRun.Results[0].Status);
            Assert.False(pendingRun.Plan[1].ForEachExpanded);

            var appliedRun = InputBackedRun("wfr-u17-abort-applied", out _);
            await WorkflowRunner.SubmitStepAsync(appliedRun, "choose", Regions("North"), null);
            var applied = appliedRun.Results[0].PendingForEach;
            WorkflowRunner.Abort(appliedRun, "invented abort after splice");
            Assert.Equal("aborted", appliedRun.Status);
            Assert.Same(applied, appliedRun.Results[0].PendingForEach);
            AssertPairedDecision(appliedRun.Results[0], "expand", "applied", new[] { "North" }, "review-region");
            Assert.Equal("review-region#0", appliedRun.Plan[1].InstanceId);
        }

        [Fact]
        public async Task Deferred_expansion_leaves_the_authored_definition_untouched_and_owns_every_answer_it_records()
        {
            var run = InputBackedRun("wfr-u17-owned", out var loop);
            var defSteps = run.Def.Steps;
            var spec = loop.ForEach;
            var submitted = new AnswerValue { Value = "North, South" };
            await WorkflowRunner.SubmitStepAsync(run, "choose", new Dictionary<string, AnswerValue> { ["regions"] = submitted }, null);
            Assert.Same(defSteps, run.Def.Steps);
            Assert.Same(spec, loop.ForEach);
            Assert.Equal("regions", loop.ForEach.InInput);
            Assert.Equal("North, South", submitted.Value);
            Assert.NotSame(submitted, run.Results[0].Answers["regions"]);
            Assert.Equal("North, South", run.Results[0].Answers["regions"].Value);
            Assert.Empty(run.Results[1].Answers);
            var seed = new Dictionary<string, AnswerValue>();
            run.Frames[1].CopyAnswerSeedTo(seed);
            Assert.NotSame(run.Results[0].Answers["regions"], seed["regions"]);
        }

        [Fact]
        public async Task Caller_mutation_during_verify_cannot_move_the_decision_the_loop_or_any_seed()
        {
            async Task AssertOwned(WorkflowRunState run, AnswerValue submitted)
            {
                Assert.Equal("North, South", run.Results[0].Answers["regions"].Value);
                Assert.NotSame(submitted, run.Results[0].Answers["regions"]);
                Assert.Equal("East, West, South", submitted.Value);
                AssertPairedDecision(run.Results[0], "expand", "applied", new[] { "North", "South" }, "review-region");
                Assert.Equal(new[] { "choose", "review-region#0", "review-region#1" }, run.Plan.Select(p => p.InstanceId));
                var iterations = run.Frames.Where(f => f.ProjectionKind == "iteration").ToArray();
                Assert.Equal(new[] { "North", "South" }, iterations.Select(f => f.LoopValue));
                Assert.DoesNotContain(iterations, f => f.LoopValue == "East" || f.LoopValue == "West");
                var seeds = new List<Dictionary<string, AnswerValue>>();
                foreach (var frame in iterations)
                {
                    var seed = new Dictionary<string, AnswerValue>();
                    frame.CopyAnswerSeedTo(seed);
                    Assert.Equal("North, South", seed["regions"].Value);
                    Assert.NotSame(submitted, seed["regions"]);
                    seeds.Add(seed);
                }
                Assert.NotSame(seeds[0]["regions"], seeds[1]["regions"]);
                Assert.NotSame(seeds[0]["regions"], run.Results[0].Answers["regions"]);
            }

            var inside = InputBackedRun("wfr-u17-mut-inside", out _);
            var retained = new AnswerValue { Value = "North, South" };
            WorkflowVerifyExecutor mutInside = (spec, step, state, answers) =>
            {
                retained.Value = "East, West, South";
                return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed" });
            };
            await WorkflowRunner.SubmitStepAsync(inside, "choose", new Dictionary<string, AnswerValue> { ["regions"] = retained }, mutInside);
            await AssertOwned(inside, retained);

            var between = InputBackedRun("wfr-u17-mut-between", out _);
            var retained2 = new AnswerValue { Value = "North, South" };
            // The window this case needs is a verify that is genuinely IN FLIGHT, so the executor must hand
            // back an incomplete task and let `SubmitStepAsync` suspend at its await. Blocking inside the
            // executor body cannot produce that window: the executor is invoked synchronously on this
            // thread, so this thread would be the one parked waiting for the release only it can give.
            // `RunContinuationsAsynchronously` keeps the resumption off this thread when it does come.
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var go = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            WorkflowVerifyExecutor mutBetween = async (spec, step, state, answers) =>
            {
                entered.TrySetResult(true);
                await go.Task;
                return new VerifyResult { Kind = spec.Kind, Status = "passed" };
            };
            var submit = WorkflowRunner.SubmitStepAsync(between, "choose",
                new Dictionary<string, AnswerValue> { ["regions"] = retained2 }, mutBetween);
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.False(submit.IsCompleted);
            retained2.Value = "East, West, South";
            go.SetResult(true);
            await submit;
            await AssertOwned(between, retained2);

            var failed = InputBackedRun("wfr-u17-mut-fail", out _);
            var retained3 = new AnswerValue { Value = "North, South" };
            WorkflowVerifyExecutor mutFail = (spec, step, state, answers) =>
            {
                retained3.Value = "East, West, South";
                return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = "invented evidence" });
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(failed, "choose", new Dictionary<string, AnswerValue> { ["regions"] = retained3 }, mutFail));
            Assert.Equal("North, South", failed.Results[0].Answers["regions"].Value);
            AssertPairedDecision(failed.Results[0], "expand", "pending", new[] { "North", "South" }, "review-region");
            Assert.Equal(2, failed.Plan.Count);
            Assert.Single(failed.Frames);
        }

        // ---- unit 18: explicit loop-entry expansion of a source two or more rows earlier ----------

        [Fact]
        public void Preparation_setup_instructions_promise_first_applicable_iteration_not_index_zero()
        {
            var instructions = WorkflowRunner.BuildView(PreparationRun("wfr-u18-prep-copy")).CurrentStep.Instructions;
            Assert.Contains("first applicable iteration", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("#0 will return", instructions, StringComparison.Ordinal);
            Assert.Equal(PreparationSetupInstructions, instructions);
        }

        [Fact]
        public void Inline_setup_instructions_promise_first_applicable_iteration_not_index_zero()
        {
            var instructions = WorkflowRunner.BuildView(InlineExpansionRun("wfr-u18-inline-copy", new[] { "one" })).CurrentStep.Instructions;
            Assert.Contains("first applicable iteration", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("#0 will return", instructions, StringComparison.Ordinal);
            Assert.Equal(InlineSetupInstructions, instructions);
        }

        [Fact]
        public async Task Deferred_setup_instructions_promise_first_applicable_iteration_not_index_zero()
        {
            var run = EarlierSourceRun("wfr-u18-deferred-copy");
            await ReachLoopEntry(run);
            var instructions = WorkflowRunner.BuildView(run).CurrentStep.Instructions;
            Assert.Contains("first applicable iteration", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("#0 will return", instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("answered", instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(DeferredSetupInstructions, instructions);
            Assert.NotEqual(InlineSetupInstructions, instructions);
            Assert.NotEqual(PreparationSetupInstructions, instructions);
        }

        [Fact]
        public async Task Explicit_loop_entry_expands_a_source_two_or_more_rows_earlier()
        {
            var two = EarlierSourceRun("wfr-u18-two-back");
            await ReachLoopEntry(two);
            var setup = WorkflowRunner.BuildView(two);
            Assert.Equal("review-region", setup.CurrentStep.StepId);
            Assert.Equal("Review invented region", setup.CurrentStep.Title);
            Assert.Equal(DeferredSetupInstructions, setup.CurrentStep.Instructions);
            Assert.Empty(setup.CurrentStep.Questions);
            Assert.Empty(setup.CurrentStep.Ops);
            Assert.Empty(setup.CurrentStep.VerifyKinds);
            Assert.Null(setup.CurrentStep.EffectiveStrictness);
            Assert.True(setup.TotalStepsProvisional);
            Assert.Equal(3, two.Plan.Count);
            Assert.False(two.Plan[2].ForEachExpanded);
            AssertNoDecision(two.Results[0]);
            AssertNoDecision(two.Results[2]);

            var verifyCalls = 0;
            WorkflowVerifyExecutor executor = (spec, step, state, answers) =>
            {
                verifyCalls++;
                return Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed" });
            };
            await WorkflowRunner.SubmitStepAsync(two, "review-region", null, executor);

            Assert.Equal(0, verifyCalls);
            Assert.Equal(new[] { "choose", "middle", "review-region#0", "review-region#1" }, two.Plan.Select(p => p.InstanceId));
            Assert.Equal(two.Plan.Select(p => p.InstanceId), two.Results.Select(r => r.StepId));
            // Both completed rows are gated by a run whose strictness is off, so the badge says skipped.
            Assert.Equal(new[] { "skipped", "skipped", "in_progress", "pending" }, two.Results.Select(r => r.Status));
            Assert.Equal(new int?[] { null, null, 0, 1 }, two.Plan.Select(p => p.IterationIndex));
            Assert.True(two.Plan[2].ForEachExpanded);
            Assert.True(two.Plan[3].ForEachExpanded);
            Assert.Equal(new[] { "North", "South" }, two.Frames.Where(f => f.ProjectionKind == "iteration").Select(f => f.LoopValue));
            Assert.Empty(two.Results[2].Answers);
            Assert.Empty(two.Results[3].Answers);
            AssertNoDecision(two.Results[0]);
            AssertNoDecision(two.Results[2]);
            var view = WorkflowRunner.BuildView(two);
            Assert.Equal("review-region#0", view.CurrentStep.StepId);
            Assert.Equal("Authored body: review North at 0.", view.CurrentStep.Instructions);
            Assert.Equal(new[] { "finding" }, view.CurrentStep.Questions.Select(q => q.Name));
            Assert.Equal(new[] { "edit_measure" }, view.CurrentStep.Ops);
            Assert.Equal(new[] { "invented_check" }, view.CurrentStep.VerifyKinds);
            Assert.False(view.TotalStepsProvisional);

            var three = new WorkflowRunState("wfr-u18-three-back", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-three-back",
                Version = 2,
                Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "choose", Number = 1, Title = "Choose invented regions",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
                    },
                    new WorkflowStep { Id = "alpha", Number = 2, Title = "Alpha" },
                    new WorkflowStep { Id = "beta", Number = 3, Title = "Beta" },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 4, Title = "Review invented region",
                        Instructions = AuthoredPreparationInstructions,
                        Ops = new[] { "edit_measure" },
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "finding", Required = "optional" } } },
                    },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(three, "choose", Regions("North, South"), null);
            await WorkflowRunner.SubmitStepAsync(three, "alpha", null, null);
            await WorkflowRunner.SubmitStepAsync(three, "beta", null, null);
            Assert.Equal("review-region", WorkflowRunner.BuildView(three).CurrentStep.StepId);
            await WorkflowRunner.SubmitStepAsync(three, "review-region", new Dictionary<string, AnswerValue>(), null);
            Assert.Equal(new[] { "choose", "alpha", "beta", "review-region#0", "review-region#1" }, three.Plan.Select(p => p.InstanceId));
            Assert.Equal("review-region#0", WorkflowRunner.BuildView(three).CurrentStep.StepId);
        }

        [Fact]
        public async Task Intervening_answers_are_frozen_into_each_loop_entry_seed_and_isolated()
        {
            var submittedList = new AnswerValue { Value = "North, South" };
            var submittedNote = new AnswerValue { Value = "intervening" };
            var run = EarlierSourceRun("wfr-u18-seeds");
            await WorkflowRunner.SubmitStepAsync(run, "choose", new Dictionary<string, AnswerValue> { ["regions"] = submittedList }, null);
            await WorkflowRunner.SubmitStepAsync(run, "middle", new Dictionary<string, AnswerValue> { ["note"] = submittedNote }, null);
            await WorkflowRunner.SubmitStepAsync(run, "review-region", null, null);

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
            Assert.NotSame(submittedList, first["regions"]);
            Assert.NotSame(submittedNote, first["note"]);
            first["regions"].Value = "changed";
            first["note"].Value = "mutated";
            var firstAgain = new Dictionary<string, AnswerValue>();
            run.Frames[1].CopyAnswerSeedTo(firstAgain);
            Assert.Equal("North, South", firstAgain["regions"].Value);
            Assert.Equal("intervening", firstAgain["note"].Value);
            Assert.Equal("North, South", run.Results[0].Answers["regions"].Value);
            Assert.Equal("intervening", run.Results[1].Answers["note"].Value);
            Assert.Empty(run.Results[2].Answers);
        }

        [Fact]
        public async Task Loop_entry_treats_base_id_null_and_empty_payloads_as_equivalent()
        {
            var nullPayload = EarlierSourceRun("wfr-u18-null");
            var emptyPayload = EarlierSourceRun("wfr-u18-empty");
            await ReachLoopEntry(nullPayload);
            await ReachLoopEntry(emptyPayload);

            await WorkflowRunner.SubmitStepAsync(nullPayload, "review-region", null, null);
            await WorkflowRunner.SubmitStepAsync(emptyPayload, "review-region", new Dictionary<string, AnswerValue>(), null);

            Assert.Equal(nullPayload.Plan.Select(p => p.InstanceId), emptyPayload.Plan.Select(p => p.InstanceId));
            Assert.Equal(new[] { "choose", "middle", "review-region#0", "review-region#1" }, nullPayload.Plan.Select(p => p.InstanceId));
            Assert.Equal("review-region#0", WorkflowRunner.BuildView(nullPayload).CurrentStep.StepId);
            Assert.Equal("review-region#0", WorkflowRunner.BuildView(emptyPayload).CurrentStep.StepId);
            Assert.All(nullPayload.Results.Skip(2), result => Assert.Empty(result.Answers));
            Assert.All(emptyPayload.Results.Skip(2), result => Assert.Empty(result.Answers));
        }

        [Fact]
        public async Task Loop_entry_payload_naming_any_field_refuses_before_mutation()
        {
            var named = EarlierSourceRun("wfr-u18-named");
            await ReachLoopEntry(named);
            var namedError = await AssertAtomicPreparationRefusal(named, "review-region", new Dictionary<string, AnswerValue>
            {
                ["finding"] = new AnswerValue { Value = "must not land" },
            });
            Assert.Contains("finding", namedError.Message, StringComparison.Ordinal);
            AssertLoopEntryNamedFieldsGoToTheCurrentIteration(namedError.Message);
            Assert.Equal("review-region", WorkflowRunner.BuildView(named).CurrentStep.StepId);
            Assert.False(named.Plan[2].ForEachExpanded);

            var declined = EarlierSourceRun("wfr-u18-named-decline");
            await ReachLoopEntry(declined);
            var declinedError = await AssertAtomicPreparationRefusal(declined, "review-region", new Dictionary<string, AnswerValue>
            {
                ["finding"] = new AnswerValue { Declined = true, DeclineReason = "invented decline" },
            });
            Assert.Contains("finding", declinedError.Message, StringComparison.Ordinal);
            AssertLoopEntryNamedFieldsGoToTheCurrentIteration(declinedError.Message);

            var sourced = EarlierSourceRun("wfr-u18-named-source");
            await ReachLoopEntry(sourced);
            var sourcedError = await AssertAtomicPreparationRefusal(sourced, "review-region", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "East, West" },
            });
            Assert.Contains("regions", sourcedError.Message, StringComparison.Ordinal);
            Assert.Contains("choose", sourcedError.Message, StringComparison.Ordinal);
            Assert.Contains("this call", sourcedError.Message, StringComparison.OrdinalIgnoreCase);
            AssertLoopEntryNamedFieldsGoToTheCurrentIteration(sourcedError.Message);
            Assert.False(sourced.Plan[2].ForEachExpanded);
            Assert.Equal("North, South", sourced.Results[0].Answers["regions"].Value);

            var many = EarlierSourceRun("wfr-u18-named-sorted");
            await ReachLoopEntry(many);
            var manyError = await AssertAtomicPreparationRefusal(many, "review-region", new Dictionary<string, AnswerValue>
            {
                ["zebra"] = new AnswerValue { Value = "must not land" },
                ["finding"] = new AnswerValue { Value = "also must not" },
            });
            Assert.Contains("finding, zebra", manyError.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("zebra, finding", manyError.Message, StringComparison.Ordinal);
            Assert.Contains("submit them against the iteration this expansion makes current", manyError.Message, StringComparison.OrdinalIgnoreCase);
            AssertLoopEntryNamedFieldsGoToTheCurrentIteration(manyError.Message);
            Assert.False(many.Plan[2].ForEachExpanded);

            await WorkflowRunner.SubmitStepAsync(named, "review-region", null, null);
            Assert.Equal(new[] { "finding" }, WorkflowRunner.BuildView(named).CurrentStep.Questions.Select(q => q.Name));
            Assert.False(named.Results[2].Answers.ContainsKey("finding"));
            Assert.False(named.Results[3].Answers.ContainsKey("finding"));
        }

        [Fact]
        public async Task Missing_declined_blank_and_over_bound_loop_entry_sources_refuse_before_mutation()
        {
            var missing = EarlierSourceRun("wfr-u18-missing");
            await WorkflowRunner.SubmitStepAsync(missing, "choose", new Dictionary<string, AnswerValue>(), null);
            await WorkflowRunner.SubmitStepAsync(missing, "middle", new Dictionary<string, AnswerValue>
            {
                ["note"] = new AnswerValue { Value = "intervening" },
            }, null);
            var missingError = await AssertAtomicPreparationRefusal(missing, "review-region", null);
            Assert.Contains("regions", missingError.Message);
            Assert.Contains("unanswered", missingError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("skip_workflow_step", missingError.Message);
            Assert.False(missing.Plan[2].ForEachExpanded);

            var declined = EarlierSourceRun("wfr-u18-declined");
            await WorkflowRunner.SubmitStepAsync(declined, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Declined = true, DeclineReason = "invented decline" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(declined, "middle", new Dictionary<string, AnswerValue>
            {
                ["note"] = new AnswerValue { Value = "intervening" },
            }, null);
            var declinedError = await AssertAtomicPreparationRefusal(declined, "review-region", null);
            Assert.Contains("declined", declinedError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("invented decline", declinedError.Message);
            Assert.Contains("skip_workflow_step", declinedError.Message);
            Assert.False(declined.Plan[2].ForEachExpanded);

            var requiredBlank = EarlierSourceRun("wfr-u18-required-blank");
            requiredBlank.Def.Steps[0].Gate.Inputs[0].Required = "required";
            await WorkflowRunner.SubmitStepAsync(requiredBlank, "choose", Regions("North"), null);
            requiredBlank.Results[0].Answers["regions"] = new AnswerValue { Value = "  " };
            await WorkflowRunner.SubmitStepAsync(requiredBlank, "middle", new Dictionary<string, AnswerValue>
            {
                ["note"] = new AnswerValue { Value = "intervening" },
            }, null);
            var blankError = await AssertAtomicPreparationRefusal(requiredBlank, "review-region", null);
            Assert.Contains("blank", blankError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("regions", blankError.Message);
            Assert.Contains("skip_workflow_step", blankError.Message, StringComparison.Ordinal);
            Assert.False(requiredBlank.Plan[2].ForEachExpanded);

            var optionalBlank = EarlierSourceRun("wfr-u18-optional-blank", following: new[]
            {
                new WorkflowStep { Id = "after", Number = 4, Title = "After" },
            });
            await ReachLoopEntry(optionalBlank, list: "  ");
            await WorkflowRunner.SubmitStepAsync(optionalBlank, "review-region", null, null);
            Assert.Equal(4, optionalBlank.Plan.Count);
            Assert.True(optionalBlank.Plan[2].ForEachExpanded);
            Assert.Null(optionalBlank.Plan[2].IterationIndex);
            Assert.Equal("not_applicable", optionalBlank.Results[2].Status);
            Assert.Equal("did not apply: the forEach value list was empty.", optionalBlank.Results[2].Note);
            Assert.Empty(optionalBlank.Results[2].Answers);
            Assert.Equal("after", WorkflowRunner.BuildView(optionalBlank).CurrentStep.StepId);
            Assert.DoesNotContain(optionalBlank.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));

            var over = EarlierSourceRun("wfr-u18-overbound", maxIterations: 1);
            await ReachLoopEntry(over);
            var overError = await AssertAtomicPreparationRefusal(over, "review-region", null);
            Assert.Contains("2", overError.Message);
            Assert.Contains("1", overError.Message);
            Assert.Contains("review-region", overError.Message);
            Assert.Contains("skip_workflow_step", overError.Message);
            Assert.False(over.Plan[2].ForEachExpanded);
            Assert.Equal(3, over.Plan.Count);

            var nullValued = EarlierSourceRun("wfr-u18-null-source");
            await WorkflowRunner.SubmitStepAsync(nullValued, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = null },
            }, null);
            await WorkflowRunner.SubmitStepAsync(nullValued, "middle", new Dictionary<string, AnswerValue>
            {
                ["note"] = new AnswerValue { Value = "intervening" },
            }, null);
            var nullError = await AssertAtomicPreparationRefusal(nullValued, "review-region", null);
            Assert.Contains("unanswered", nullError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("regions", nullError.Message);
            Assert.Contains("skip_workflow_step", nullError.Message);
            Assert.False(nullValued.Plan[2].ForEachExpanded);

            var callBoundary = EarlierSourceRun("wfr-u18-call-boundary");
            callBoundary.Def.Steps[0].Gate.Inputs[0].Scope = "run";
            await ReachLoopEntry(callBoundary);
            var declared = callBoundary.Plan[0];
            var call = RunFrame.CreateCall("wfr-u18-call-boundary:call", "choose", "invented-callee", 2,
                Array.Empty<string>(), Array.Empty<string>(), "passed");
            callBoundary.Frames.Add(call);
            callBoundary.Plan[0] = new PlannedStep(declared.Step, declared.InstanceId, call.FrameId,
                declared.IterationIndex, declared.ForEachExpanded);
            var callError = await AssertAtomicPreparationRefusal(callBoundary, "review-region", null);
            Assert.Contains("regions", callError.Message, StringComparison.Ordinal);
            Assert.Contains("unanswered", callError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("skip_workflow_step", callError.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("scope: run", callError.Message, StringComparison.Ordinal);
            Assert.False(callBoundary.Plan[2].ForEachExpanded);

            var separators = EarlierSourceRun("wfr-u18-separators", following: new[]
            {
                new WorkflowStep { Id = "after", Number = 4, Title = "After" },
            });
            await ReachLoopEntry(separators, list: ", ,");
            await WorkflowRunner.SubmitStepAsync(separators, "review-region", null, null);
            Assert.Equal(4, separators.Plan.Count);
            Assert.True(separators.Plan[2].ForEachExpanded);
            Assert.Null(separators.Plan[2].IterationIndex);
            Assert.Equal("not_applicable", separators.Results[2].Status);
            Assert.Equal("did not apply: the forEach value list was empty.", separators.Results[2].Note);
            Assert.Equal("after", WorkflowRunner.BuildView(separators).CurrentStep.StepId);

            foreach (var required in new[] { "required", "answer-or-decline" })
            {
                var aod = EarlierSourceRun("wfr-u18-aod-" + required);
                aod.Def.Steps[0].Gate.Inputs[0].Required = required;
                await WorkflowRunner.SubmitStepAsync(aod, "choose", Regions("North"), null);
                aod.Results[0].Answers["regions"] = new AnswerValue { Value = "  " };
                await WorkflowRunner.SubmitStepAsync(aod, "middle", new Dictionary<string, AnswerValue>
                {
                    ["note"] = new AnswerValue { Value = "intervening" },
                }, null);
                var aodError = await AssertAtomicPreparationRefusal(aod, "review-region", null);
                Assert.Contains("blank", aodError.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Which invented regions?", aodError.Message);
                Assert.Contains("regions", aodError.Message);
                Assert.Contains("skip_workflow_step", aodError.Message, StringComparison.Ordinal);
                Assert.False(aod.Plan[2].ForEachExpanded);
            }

            // A SECOND declaration of the same name that nobody answered cannot re-own the source. `hold` sits
            // between that declaration and the loop on purpose: without it `middle` is the loop's immediate
            // predecessor and Unit 17's delivered adjacent path owns the splice, so the row would already be
            // expanded and this case would prove nothing about loop-entry resolution.
            var duplicate = EarlierSourceRun("wfr-u18-dup-declarer", between: new[]
            {
                new WorkflowStep { Id = "hold", Number = 3, Title = "Hold" },
            });
            duplicate.Def.Steps[1].Gate.Inputs = new[]
            {
                new GateInput { Name = "note", Question = "What is the intervening note?", Required = "optional" },
                new GateInput { Name = "regions", Question = "Which later invented regions?", Required = "optional" },
            };
            await WorkflowRunner.SubmitStepAsync(duplicate, "choose", Regions("North, South"), null);
            await WorkflowRunner.SubmitStepAsync(duplicate, "middle", new Dictionary<string, AnswerValue>
            {
                ["note"] = new AnswerValue { Value = "intervening" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(duplicate, "hold", null, null);
            Assert.Equal("review-region", WorkflowRunner.BuildView(duplicate).CurrentStep.StepId);
            Assert.False(duplicate.Plan[3].ForEachExpanded);
            await WorkflowRunner.SubmitStepAsync(duplicate, "review-region", null, null);
            Assert.Equal(
                new[] { "choose", "middle", "hold", "review-region#0", "review-region#1" },
                duplicate.Plan.Select(p => p.InstanceId));
            Assert.Equal("North, South", duplicate.Results[0].Answers["regions"].Value);
            Assert.False(duplicate.Results[1].Answers.ContainsKey("regions"));

            var bothMissing = EarlierSourceRun("wfr-u18-dup-gap", between: new[]
            {
                new WorkflowStep { Id = "hold", Number = 3, Title = "Hold" },
            }, following: new[]
            {
                new WorkflowStep
                {
                    Id = "later", Number = 5, Title = "Later",
                    Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
                },
            });
            bothMissing.Def.Steps[1].Gate.Inputs = new[]
            {
                new GateInput { Name = "note", Question = "What is the intervening note?", Required = "optional" },
                new GateInput { Name = "regions", Question = "Which nearest invented regions?", Required = "optional" },
            };
            await WorkflowRunner.SubmitStepAsync(bothMissing, "choose", new Dictionary<string, AnswerValue>(), null);
            await WorkflowRunner.SubmitStepAsync(bothMissing, "middle", new Dictionary<string, AnswerValue>
            {
                ["note"] = new AnswerValue { Value = "intervening" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(bothMissing, "hold", null, null);
            var bothError = await AssertAtomicPreparationRefusal(bothMissing, "review-region", null);
            Assert.Contains("declared on step 'middle'", bothError.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("declared on step 'choose'", bothError.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("declared on step 'later'", bothError.Message, StringComparison.Ordinal);
            Assert.Contains("skip_workflow_step", bothError.Message);
            Assert.False(bothMissing.Plan[3].ForEachExpanded);
        }

        [Fact]
        public async Task Later_visible_optional_blank_over_earlier_required_means_empty()
        {
            var run = EarlierSourceRun("wfr-u18-blank-optional-wins", between: new[]
            {
                new WorkflowStep { Id = "hold", Number = 3, Title = "Hold" },
            }, following: new[]
            {
                new WorkflowStep { Id = "after", Number = 5, Title = "After" },
            });
            run.Def.Steps[0].Gate.Inputs[0].Required = "required";
            run.Def.Steps[1].Gate.Inputs = new[]
            {
                new GateInput { Name = "note", Question = "What is the intervening note?", Required = "optional" },
                new GateInput { Name = "regions", Question = "Which later invented regions?", Required = "optional" },
            };
            await WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North, South"), null);
            await WorkflowRunner.SubmitStepAsync(run, "middle", new Dictionary<string, AnswerValue>
            {
                ["note"] = new AnswerValue { Value = "intervening" },
                ["regions"] = new AnswerValue { Value = "  " },
            }, null);
            await WorkflowRunner.SubmitStepAsync(run, "hold", null, null);
            await WorkflowRunner.SubmitStepAsync(run, "review-region", null, null);
            Assert.Equal("not_applicable", run.Results[3].Status);
            Assert.Equal("did not apply: the forEach value list was empty.", run.Results[3].Note);
            Assert.DoesNotContain(run.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));
            Assert.Equal("after", WorkflowRunner.BuildView(run).CurrentStep.StepId);
        }

        [Fact]
        public async Task Later_visible_required_blank_over_earlier_optional_refuses()
        {
            var run = EarlierSourceRun("wfr-u18-blank-required-wins", between: new[]
            {
                new WorkflowStep { Id = "hold", Number = 3, Title = "Hold" },
            });
            run.Def.Steps[0].Gate.Inputs[0].Required = "optional";
            run.Def.Steps[1].Gate.Inputs = new[]
            {
                new GateInput { Name = "note", Question = "What is the intervening note?", Required = "optional" },
                new GateInput { Name = "regions", Question = "Which later invented regions?", Required = "required" },
            };
            await WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North, South"), null);
            await WorkflowRunner.SubmitStepAsync(run, "middle", new Dictionary<string, AnswerValue>
            {
                ["note"] = new AnswerValue { Value = "intervening" },
                ["regions"] = new AnswerValue { Value = "  " },
            }, null);
            await WorkflowRunner.SubmitStepAsync(run, "hold", null, null);
            var error = await AssertAtomicPreparationRefusal(run, "review-region", null);
            Assert.Contains("blank", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("regions", error.Message);
            Assert.Contains("Which later invented regions?", error.Message);
            Assert.Contains("skip_workflow_step", error.Message, StringComparison.Ordinal);
            Assert.False(run.Plan[3].ForEachExpanded);
            Assert.NotEqual("not_applicable", run.Results[3].Status);
        }

        [Fact]
        public async Task Loop_free_false_when_on_loop_entry_creates_no_iterations()
        {
            var run = EarlierSourceRun("wfr-u18-when-false", following: new[]
            {
                new WorkflowStep { Id = "after", Number = 4, Title = "After" },
            });
            run.Def.Steps[2].When = "inputs.missing.answered";
            await ReachLoopEntry(run);
            Assert.Equal("review-region", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            Assert.False(run.Plan[2].ForEachExpanded);

            await WorkflowRunner.SubmitStepAsync(run, "review-region", null, null);

            Assert.Equal(4, run.Plan.Count);
            Assert.False(run.Plan[2].ForEachExpanded);
            Assert.Null(run.Plan[2].IterationIndex);
            Assert.Equal("not_applicable", run.Results[2].Status);
            Assert.Contains("did not apply: condition", run.Results[2].Note);
            Assert.DoesNotContain(run.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));
            Assert.DoesNotContain(run.Frames, f => f.ProjectionKind == "iteration");
            Assert.Equal("after", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            Assert.True(WorkflowRunner.BuildView(run).TotalStepsProvisional);
            AssertNoDecision(run.Results[0]);

            var unread = EarlierSourceRun("wfr-u18-when-unread");
            unread.Def.Steps[2].When = "not a condition";
            await ReachLoopEntry(unread);
            Assert.Equal("review-region", WorkflowRunner.BuildView(unread).CurrentStep.StepId);
            await WorkflowRunner.SubmitStepAsync(unread, "review-region", null, null);
            Assert.Equal("not_applicable", unread.Results[2].Status);
            Assert.Contains("unreadable", unread.Results[2].Note);
            Assert.False(unread.Plan[2].ForEachExpanded);

            var overFalse = EarlierSourceRun("wfr-u18-when-over-false", maxIterations: 1);
            overFalse.Def.Steps[2].When = "inputs.missing.answered";
            await ReachLoopEntry(overFalse);
            var overFalseError = await AssertAtomicPreparationRefusal(overFalse, "review-region", null);
            Assert.Contains("2", overFalseError.Message);
            Assert.Contains("1", overFalseError.Message);
            Assert.Contains("skip_workflow_step", overFalseError.Message);
            Assert.False(overFalse.Plan[2].ForEachExpanded);
            Assert.NotEqual("not_applicable", overFalse.Results[2].Status);

            var gapUnread = EarlierSourceRun("wfr-u18-when-gap-unread");
            gapUnread.Def.Steps[2].When = "not a condition";
            await WorkflowRunner.SubmitStepAsync(gapUnread, "choose", new Dictionary<string, AnswerValue>(), null);
            await WorkflowRunner.SubmitStepAsync(gapUnread, "middle", new Dictionary<string, AnswerValue>
            {
                ["note"] = new AnswerValue { Value = "intervening" },
            }, null);
            var gapUnreadError = await AssertAtomicPreparationRefusal(gapUnread, "review-region", null);
            Assert.Contains("unanswered", gapUnreadError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("skip_workflow_step", gapUnreadError.Message);
            Assert.False(gapUnread.Plan[2].ForEachExpanded);
            Assert.NotEqual("not_applicable", gapUnread.Results[2].Status);
        }

        [Theory]
        [InlineData("missing", "false")]
        [InlineData("missing", "unreadable")]
        [InlineData("declined", "false")]
        [InlineData("declined", "unreadable")]
        [InlineData("blank", "false")]
        [InlineData("blank", "unreadable")]
        [InlineData("overbound", "false")]
        [InlineData("overbound", "unreadable")]
        [InlineData("scope", "false")]
        [InlineData("scope", "unreadable")]
        [InlineData("call", "false")]
        [InlineData("call", "unreadable")]
        public async Task Unusable_loop_entry_source_refuses_beside_false_and_unreadable_when(string source, string condition)
        {
            var when = condition == "false" ? "inputs.missing.answered" : "not a condition";
            WorkflowRunState run;
            string loopId;
            int loopIndex;
            if (source == "scope" || source == "call")
            {
                run = new WorkflowRunState("wfr-u18-matrix-" + source + "-" + condition, new WorkflowDef
                {
                    SchemaVersion = 2,
                    Name = "invented-matrix-scope",
                    Version = 2,
                    Strictness = "off",
                    Steps = new[]
                    {
                        new WorkflowStep
                        {
                            Id = "loop-a", Number = 1, Title = "Loop A",
                            ForEach = new ForEachSpec { InLiteral = new[] { "one" }, As = "item", MaxIterations = 4 },
                            Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional", Scope = source == "call" ? "run" : null } } },
                        },
                        new WorkflowStep { Id = "middle", Number = 2, Title = "Middle" },
                        new WorkflowStep
                        {
                            Id = "review-region", Number = 3, Title = "Review",
                            When = when,
                            ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                        },
                    },
                }, null);
                await WorkflowRunner.SubmitStepAsync(run, "loop-a", null, null);
                await WorkflowRunner.SubmitStepAsync(run, "loop-a#0", Regions("North"), null);
                await WorkflowRunner.SubmitStepAsync(run, "middle", null, null);
                loopId = "review-region";
                loopIndex = run.StepIndex;
                if (source == "call")
                {
                    var declared = run.Plan[0];
                    var call = RunFrame.CreateCall(run.RunId + ":call", "loop-a", "invented-callee", 2,
                        Array.Empty<string>(), Array.Empty<string>(), "passed");
                    run.Frames.Add(call);
                    run.Plan[0] = new PlannedStep(declared.Step, declared.InstanceId, call.FrameId,
                        declared.IterationIndex, declared.ForEachExpanded);
                }
            }
            else
            {
                run = EarlierSourceRun("wfr-u18-matrix-" + source + "-" + condition, maxIterations: source == "overbound" ? 1 : 9);
                run.Def.Steps[2].When = when;
                if (source == "blank")
                    run.Def.Steps[0].Gate.Inputs[0].Required = "required";
                if (source == "missing")
                {
                    await WorkflowRunner.SubmitStepAsync(run, "choose", new Dictionary<string, AnswerValue>(), null);
                    await WorkflowRunner.SubmitStepAsync(run, "middle", new Dictionary<string, AnswerValue>
                    {
                        ["note"] = new AnswerValue { Value = "intervening" },
                    }, null);
                }
                else if (source == "declined")
                {
                    await WorkflowRunner.SubmitStepAsync(run, "choose", new Dictionary<string, AnswerValue>
                    {
                        ["regions"] = new AnswerValue { Declined = true, DeclineReason = "invented decline" },
                    }, null);
                    await WorkflowRunner.SubmitStepAsync(run, "middle", new Dictionary<string, AnswerValue>
                    {
                        ["note"] = new AnswerValue { Value = "intervening" },
                    }, null);
                }
                else if (source == "blank")
                {
                    await WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North"), null);
                    run.Results[0].Answers["regions"] = new AnswerValue { Value = "  " };
                    await WorkflowRunner.SubmitStepAsync(run, "middle", new Dictionary<string, AnswerValue>
                    {
                        ["note"] = new AnswerValue { Value = "intervening" },
                    }, null);
                }
                else
                    await ReachLoopEntry(run);
                loopId = "review-region";
                loopIndex = 2;
            }

            var before = Snapshot(run);
            var error = await AssertAtomicPreparationRefusal(run, loopId, null);
            Assert.Contains("skip_workflow_step", error.Message, StringComparison.Ordinal);
            if (source == "scope")
                Assert.Contains("scope: run", error.Message, StringComparison.Ordinal);
            if (source == "call")
            {
                Assert.Contains("unanswered", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("scope: run", error.Message, StringComparison.Ordinal);
            }
            Assert.False(run.Plan[loopIndex].ForEachExpanded);
            Assert.NotEqual("not_applicable", run.Results[loopIndex].Status);
            Assert.True(string.IsNullOrEmpty(run.Results[loopIndex].Note) || !run.Results[loopIndex].Note.Contains("did not apply: condition", StringComparison.Ordinal));
            AssertUnchanged(before, run);
        }

        [Fact]
        public async Task Optional_blank_loop_entry_source_beside_false_when_is_an_empty_list()
        {
            var run = EarlierSourceRun("wfr-u18-matrix-optional-blank", following: new[]
            {
                new WorkflowStep { Id = "after", Number = 4, Title = "After" },
            });
            run.Def.Steps[2].When = "inputs.missing.answered";
            await ReachLoopEntry(run, list: "  ");
            // The control is that an OPTIONAL blank source is ACCEPTED where every unusable source beside the
            // same false condition refuses: the submission returns instead of throwing. Which reason is then
            // recorded follows the shared helper's order, and the helper classifies the condition BEFORE it
            // splices, so the loop-free false verdict is what lands on the row. The outcome the case is named
            // for is unchanged: zero iterations, nothing minted, the run walks on.
            await WorkflowRunner.SubmitStepAsync(run, "review-region", null, null);
            Assert.Equal("not_applicable", run.Results[2].Status);
            Assert.Contains("did not apply: condition", run.Results[2].Note, StringComparison.Ordinal);
            Assert.False(run.Plan[2].ForEachExpanded);
            Assert.DoesNotContain(run.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));
            Assert.Equal("after", WorkflowRunner.BuildView(run).CurrentStep.StepId);

            // Same blank source, no condition to decide first: then the empty list is what the row records.
            // The pair is what proves the blank was accepted in both and only the ORDER chose the sentence.
            var noWhen = EarlierSourceRun("wfr-u18-matrix-optional-blank-plain", following: new[]
            {
                new WorkflowStep { Id = "after", Number = 4, Title = "After" },
            });
            await ReachLoopEntry(noWhen, list: "  ");
            await WorkflowRunner.SubmitStepAsync(noWhen, "review-region", null, null);
            Assert.Equal("not_applicable", noWhen.Results[2].Status);
            Assert.Equal("did not apply: the forEach value list was empty.", noWhen.Results[2].Note);
            Assert.True(noWhen.Plan[2].ForEachExpanded);
            Assert.DoesNotContain(noWhen.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));
            Assert.Equal("after", WorkflowRunner.BuildView(noWhen).CurrentStep.StepId);
        }

        [Fact]
        public async Task Loop_dependent_when_on_loop_entry_is_judged_per_iteration_not_before_expansion()
        {
            var asSelect = EarlierSourceRun("wfr-u18-loop-as");
            asSelect.Def.Steps[2].When = "loop.region == 'North'";
            await ReachLoopEntry(asSelect);
            Assert.Equal("review-region", WorkflowRunner.BuildView(asSelect).CurrentStep.StepId);
            Assert.NotEqual("not_applicable", asSelect.Results[2].Status);
            await WorkflowRunner.SubmitStepAsync(asSelect, "review-region", null, null);
            Assert.Equal(new[] { "choose", "middle", "review-region#0", "review-region#1" }, asSelect.Plan.Select(p => p.InstanceId));
            Assert.Equal("in_progress", asSelect.Results[2].Status);
            Assert.Equal("review-region#0", WorkflowRunner.BuildView(asSelect).CurrentStep.StepId);
            await WorkflowRunner.SubmitStepAsync(asSelect, "review-region#0", null, null);
            Assert.Equal("not_applicable", asSelect.Results[3].Status);
            Assert.Contains("loop.region=South", asSelect.Results[3].Note);

            var indexSelect = EarlierSourceRun("wfr-u18-loop-index");
            indexSelect.Def.Steps[2].When = "loop.index == 1";
            await ReachLoopEntry(indexSelect);
            await WorkflowRunner.SubmitStepAsync(indexSelect, "review-region", null, null);
            Assert.Equal("not_applicable", indexSelect.Results[2].Status);
            Assert.Contains("loop.index=0", indexSelect.Results[2].Note);
            Assert.Equal("review-region#1", indexSelect.Plan[indexSelect.StepIndex].InstanceId);
            Assert.Equal("in_progress", indexSelect.Results[3].Status);
            Assert.Equal("Authored body: review South at 1.", WorkflowRunner.BuildView(indexSelect).CurrentStep.Instructions);

            var mixed = EarlierSourceRun("wfr-u18-loop-mixed");
            mixed.Def.Steps[2].When = "inputs.note.answered && loop.region == 'North'";
            await ReachLoopEntry(mixed);
            await WorkflowRunner.SubmitStepAsync(mixed, "review-region", null, null);
            Assert.Equal(new[] { "choose", "middle", "review-region#0", "review-region#1" }, mixed.Plan.Select(p => p.InstanceId));
            Assert.Equal("in_progress", mixed.Results[2].Status);
            await WorkflowRunner.SubmitStepAsync(mixed, "review-region#0", null, null);
            Assert.Equal("not_applicable", mixed.Results[3].Status);
            Assert.Contains("loop.region=South", mixed.Results[3].Note);
            Assert.Contains("inputs.note", mixed.Results[3].Note);

            var quoted = EarlierSourceRun("wfr-u18-loop-quoted");
            quoted.Def.Steps[2].When = "inputs.note.value == 'loop.index'";
            await ReachLoopEntry(quoted);
            await WorkflowRunner.SubmitStepAsync(quoted, "review-region", null, null);
            Assert.Equal(new[] { "choose", "middle", "review-region" }, quoted.Plan.Select(p => p.InstanceId));
            Assert.False(quoted.Plan[2].ForEachExpanded);
            Assert.Equal("not_applicable", quoted.Results[2].Status);
            Assert.DoesNotContain(quoted.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));

            var hyphen = EarlierSourceRun("wfr-u18-loop-hyphen");
            hyphen.Def.Steps[2].When = "inputs.loop-mode.answered";
            await ReachLoopEntry(hyphen);
            await WorkflowRunner.SubmitStepAsync(hyphen, "review-region", null, null);
            Assert.Equal("not_applicable", hyphen.Results[2].Status);
            Assert.False(hyphen.Plan[2].ForEachExpanded);

            var semantic = EarlierSourceRun("wfr-u18-loop-semantic");
            semantic.Def.Steps[2].When = "loop.index ~ 'x'";
            await ReachLoopEntry(semantic);
            await WorkflowRunner.SubmitStepAsync(semantic, "review-region", null, null);
            Assert.Equal(new[] { "choose", "middle", "review-region#0", "review-region#1" }, semantic.Plan.Select(p => p.InstanceId));

            var allExcluded = EarlierSourceRun("wfr-u18-loop-all", following: new[]
            {
                new WorkflowStep { Id = "after", Number = 4, Title = "After", Instructions = "Authored after body." },
            });
            allExcluded.Def.Steps[2].When = "loop.region == 'East'";
            await ReachLoopEntry(allExcluded);
            await WorkflowRunner.SubmitStepAsync(allExcluded, "review-region", null, null);
            Assert.Equal(new[] { "choose", "middle", "review-region#0", "review-region#1", "after" }, allExcluded.Plan.Select(p => p.InstanceId));
            Assert.Equal("not_applicable", allExcluded.Results[2].Status);
            Assert.Equal("not_applicable", allExcluded.Results[3].Status);
            Assert.Equal("after", WorkflowRunner.BuildView(allExcluded).CurrentStep.StepId);
            Assert.Equal("Authored after body.", WorkflowRunner.BuildView(allExcluded).CurrentStep.Instructions);

            var inline = InlineExpansionRun("wfr-u18-inline-keep", new[] { "one", "two" });
            await WorkflowRunner.SubmitStepAsync(inline, "inline-loop", null, null);
            Assert.Equal(new[] { "inline-loop#0", "inline-loop#1" }, inline.Plan.Select(p => p.InstanceId));
            Assert.Equal(InlineSetupInstructions, WorkflowRunner.BuildView(InlineExpansionRun("wfr-u18-inline-setup", new[] { "one" })).CurrentStep.Instructions);

            var self = PreparationRun("wfr-u18-self-keep");
            Assert.Equal(PreparationSetupInstructions, WorkflowRunner.BuildView(self).CurrentStep.Instructions);
            await WorkflowRunner.SubmitStepAsync(self, "review-region", Regions("North, South"), null);
            Assert.Equal("review-region#0", WorkflowRunner.BuildView(self).CurrentStep.StepId);
            Assert.Equal(new[] { "finding" }, WorkflowRunner.BuildView(self).CurrentStep.Questions.Select(q => q.Name));
        }

        [Fact]
        public async Task Skipped_hard_failed_source_recovery_leaves_abandoned_decision_unchanged_and_overrides_the_certificate()
        {
            var recover = EarlierSourceRun("wfr-u18-recover", hard: true);
            WorkflowVerifyExecutor fail = (spec, step, state, answers) =>
                Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = "invented evidence" });
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(recover, "choose", Regions("North, South"), fail));
            Assert.Equal("failed", recover.Results[0].Status);
            AssertNoDecision(recover.Results[0]);
            WorkflowVerifyExecutor pass = (spec, step, state, answers) =>
                Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed" });
            await WorkflowRunner.SubmitStepAsync(recover, "choose", Regions("North, South"), pass);
            await WorkflowRunner.SubmitStepAsync(recover, "middle", new Dictionary<string, AnswerValue>
            {
                ["note"] = new AnswerValue { Value = "intervening" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(recover, "review-region", null, null);
            Assert.Equal("review-region#0", WorkflowRunner.BuildView(recover).CurrentStep.StepId);
            Assert.Equal(4, recover.Plan.Count);

            var skipped = AdjacentThenWaitingRun("wfr-u18-abandon-skip", hard: true);
            skipped.CoverageSurface = new CoverageSurfaceLock
            {
                CurrentGrid = Array.Empty<string>(),
                CurrentOpenGrains = Array.Empty<string>(),
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(skipped, "choose", Regions("North, South"), fail));
            var abandoned = skipped.Results[0].PendingForEach;
            AssertPairedDecision(skipped.Results[0], "expand", "pending", new[] { "North", "South" }, "review-region");
            WorkflowRunner.SkipStep(skipped, "choose", "invented skip after fail");
            AssertPairedDecision(skipped.Results[0], "expand", "abandoned", new[] { "North", "South" }, "review-region");
            Assert.Same(abandoned, skipped.Results[0].PendingForEach);
            Assert.Equal("abandoned", abandoned.State);
            Assert.Equal("North, South", skipped.Results[0].Answers["regions"].Value);
            Assert.Equal("review-region", WorkflowRunner.BuildView(skipped).CurrentStep.StepId);
            await WorkflowRunner.SubmitStepAsync(skipped, "review-region", null, null);
            Assert.Equal(new[] { "choose", "review-region#0", "review-region#1", "review-again" }, skipped.Plan.Select(p => p.InstanceId));
            Assert.Equal("review-region#0", WorkflowRunner.BuildView(skipped).CurrentStep.StepId);
            Assert.True(skipped.Plan[1].ForEachExpanded);
            AssertPairedDecision(skipped.Results[0], "expand", "abandoned", new[] { "North", "South" }, "review-region");
            Assert.Same(abandoned, skipped.Results[0].PendingForEach);
            Assert.Equal("abandoned", abandoned.State);
            AssertNoDecision(skipped.Results[1]);
            await WorkflowRunner.SubmitStepAsync(skipped, "review-region#0", null, null);
            await WorkflowRunner.SubmitStepAsync(skipped, "review-region#1", null, null);
            await WorkflowRunner.SubmitStepAsync(skipped, "review-again", null, null);
            await WorkflowRunner.SubmitStepAsync(skipped, "review-again#0", null, null);
            await WorkflowRunner.SubmitStepAsync(skipped, "review-again#1", null, null);
            Assert.Equal("completed", skipped.Status);
            Assert.Equal("OVERRIDDEN", skipped.Certificate.ComputedLevel);
            Assert.Equal(new[] { "choose" }, skipped.Certificate.SkippedSteps.Select(s => s.StepId));
            Assert.Contains("invented skip after fail", skipped.Certificate.SkippedSteps[0].Reason);

            var replaced = new WorkflowRunState("wfr-u18-run-scope", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-run-scope-entry",
                Version = 2,
                Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "loop-a", Number = 1, Title = "Loop A",
                        Instructions = "Authored body: [[loop.item]].",
                        ForEach = new ForEachSpec { InLiteral = new[] { "one", "two" }, As = "item", MaxIterations = 4 },
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional", Scope = "run" } } },
                    },
                    new WorkflowStep { Id = "middle", Number = 2, Title = "Middle" },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 3, Title = "Review invented region",
                        Instructions = AuthoredPreparationInstructions,
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                    },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(replaced, "loop-a", null, null);
            await WorkflowRunner.SubmitStepAsync(replaced, "loop-a#0", Regions("North"), null);
            await WorkflowRunner.SubmitStepAsync(replaced, "loop-a#1", Regions("East, West"), null);
            await WorkflowRunner.SubmitStepAsync(replaced, "middle", null, null);
            Assert.Equal("review-region", WorkflowRunner.BuildView(replaced).CurrentStep.StepId);
            await WorkflowRunner.SubmitStepAsync(replaced, "review-region", null, null);
            Assert.Equal(new[] { "loop-a#0", "loop-a#1", "middle", "review-region#0", "review-region#1" }, replaced.Plan.Select(p => p.InstanceId));
            Assert.Equal(new[] { "East", "West" }, replaced.Frames.Where(f => f.LoopValue == "East" || f.LoopValue == "West").Select(f => f.LoopValue));
            Assert.DoesNotContain(replaced.Frames, f => f.LoopValue == "North");
        }

        [Fact]
        public async Task Retry_skip_and_abort_do_not_expand_a_loop_entry_row()
        {
            var skip = EarlierSourceRun("wfr-u18-skip", following: new[]
            {
                new WorkflowStep { Id = "after", Number = 4, Title = "After" },
            });
            await ReachLoopEntry(skip);
            WorkflowRunner.SkipStep(skip, "review-region", "invented skip of loop entry");
            Assert.Equal("skipped", skip.Results[2].Status);
            Assert.Contains("invented skip of loop entry", skip.Results[2].Note);
            Assert.Equal(4, skip.Plan.Count);
            Assert.False(skip.Plan[2].ForEachExpanded);
            Assert.Null(skip.Plan[2].IterationIndex);
            Assert.Equal("after", WorkflowRunner.BuildView(skip).CurrentStep.StepId);
            Assert.DoesNotContain(skip.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));

            var abort = EarlierSourceRun("wfr-u18-abort");
            await ReachLoopEntry(abort);
            var pendingPlan = abort.Plan.ToArray();
            WorkflowRunner.Abort(abort, "invented abort of loop entry");
            Assert.Equal("aborted", abort.Status);
            Assert.Equal(pendingPlan, abort.Plan);
            Assert.False(abort.Plan[2].ForEachExpanded);
            Assert.Equal("review-region", abort.Plan[2].InstanceId);

            var retry = EarlierSourceRun("wfr-u18-retry");
            await ReachLoopEntry(retry);
            await AssertAtomicPreparationRefusal(retry, "review-region", new Dictionary<string, AnswerValue>
            {
                ["finding"] = new AnswerValue { Value = "must not land" },
            });
            Assert.False(retry.Plan[2].ForEachExpanded);
            await WorkflowRunner.SubmitStepAsync(retry, "review-region", null, null);
            Assert.Equal("review-region#0", WorkflowRunner.BuildView(retry).CurrentStep.StepId);
            Assert.True(retry.Plan[2].ForEachExpanded);
        }

        [Fact]
        public async Task Current_row_condition_walk_starts_at_the_expanded_loop()
        {
            var run = EarlierSourceRun("wfr-u18-current-walk", following: new[]
            {
                new WorkflowStep { Id = "after", Number = 4, Title = "After" },
            });
            run.Def.Steps[2].When = "loop.region == 'South'";
            await ReachLoopEntry(run);
            var beforeSetup = WorkflowRunner.BuildView(run);
            Assert.NotNull(beforeSetup.CurrentStep);
            Assert.Equal("review-region", beforeSetup.CurrentStep.StepId);
            Assert.NotEqual("not_applicable", run.Results[2].Status);
            Assert.True(string.IsNullOrEmpty(run.Results[2].Note));
            WorkflowRunner.AdvancePastInapplicableSteps(run);
            var stillWaiting = WorkflowRunner.BuildView(run);
            Assert.NotNull(stillWaiting.CurrentStep);
            Assert.Equal("review-region", stillWaiting.CurrentStep.StepId);
            Assert.NotEqual("not_applicable", run.Results[2].Status);
            Assert.True(string.IsNullOrEmpty(run.Results[2].Note));
            var top = run.Frames[0];
            await WorkflowRunner.SubmitStepAsync(run, "review-region", null, null);

            Assert.Equal(new[] { "choose", "middle", "review-region#0", "review-region#1", "after" }, run.Plan.Select(p => p.InstanceId));
            Assert.Equal("not_applicable", run.Results[2].Status);
            Assert.Contains("loop.region=North", run.Results[2].Note);
            Assert.Equal("review-region#1", run.Plan[run.StepIndex].InstanceId);
            Assert.Equal("in_progress", run.Results[3].Status);
            Assert.Equal("Authored body: review South at 1.", WorkflowRunner.BuildView(run).CurrentStep.Instructions);
            var iterations = run.Frames.Where(f => f.ProjectionKind == "iteration").OrderBy(f => f.IterationIndex).ToArray();
            Assert.Equal("passed", iterations[0].State);
            Assert.Equal("in_progress", iterations[1].State);
            Assert.Same(top, run.Frames[0]);
            Assert.Null(run.Frames[0].State);
            Assert.All(run.Frames, frame => Assert.Empty(frame.WitnessLocks));
            Assert.All(run.Results, result => Assert.Empty(result.VerifyHistory));
        }

        [Fact]
        public async Task Loop_entry_expansion_shifts_later_plan_indices()
        {
            var after = new WorkflowStep { Id = "after", Number = 4, Title = "After", Instructions = "Authored after body." };
            var run = EarlierSourceRun("wfr-u18-shift", following: after);
            await ReachLoopEntry(run);
            Assert.Equal(4, run.Plan.Count);
            Assert.Equal("after", run.Plan[3].InstanceId);
            Assert.Equal(3, run.Plan.FindIndex(p => p.InstanceId == "after"));

            await WorkflowRunner.SubmitStepAsync(run, "review-region", null, null);

            Assert.Equal(new[] { "choose", "middle", "review-region#0", "review-region#1", "after" }, run.Plan.Select(p => p.InstanceId));
            Assert.Equal(4, run.Plan.FindIndex(p => p.InstanceId == "after"));
            Assert.Equal("after", run.Results[4].StepId);
            Assert.Equal("pending", run.Results[4].Status);
            Assert.Same(run.Def.Steps[3], run.Plan[4].Step);
            await WorkflowRunner.SubmitStepAsync(run, "review-region#0", null, null);
            await WorkflowRunner.SubmitStepAsync(run, "review-region#1", null, null);
            Assert.Equal("after", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            Assert.Equal(4, run.StepIndex);
        }

        [Fact]
        public async Task Two_loops_sharing_a_source_each_expand_at_their_own_loop_entry()
        {
            var run = AdjacentThenWaitingRun("wfr-u18-two-loops");
            await WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North, South"), null);
            AssertPairedDecision(run.Results[0], "expand", "applied", new[] { "North", "South" }, "review-region");
            Assert.Equal(new[] { "choose", "review-region#0", "review-region#1", "review-again" }, run.Plan.Select(p => p.InstanceId));
            Assert.Equal("review-region#0", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            Assert.False(run.Plan[3].ForEachExpanded);
            Assert.Equal("review-again", run.Plan[3].InstanceId);
            AssertNoDecision(run.Results[3]);

            await WorkflowRunner.SubmitStepAsync(run, "review-region#0", null, null);
            await WorkflowRunner.SubmitStepAsync(run, "review-region#1", null, null);
            var waiting = WorkflowRunner.BuildView(run);
            Assert.Equal("review-again", waiting.CurrentStep.StepId);
            Assert.Equal(DeferredSetupInstructions, waiting.CurrentStep.Instructions);
            Assert.Empty(waiting.CurrentStep.Questions);
            Assert.Empty(waiting.CurrentStep.Ops);
            Assert.True(waiting.TotalStepsProvisional);
            Assert.False(run.Plan[3].ForEachExpanded);

            await WorkflowRunner.SubmitStepAsync(run, "review-again", null, null);
            Assert.Equal(new[] { "choose", "review-region#0", "review-region#1", "review-again#0", "review-again#1" }, run.Plan.Select(p => p.InstanceId));
            Assert.Equal("review-again#0", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            Assert.True(run.Plan[3].ForEachExpanded);
            Assert.True(run.Plan[4].ForEachExpanded);
            AssertNoDecision(run.Results[3]);
            AssertPairedDecision(run.Results[0], "expand", "applied", new[] { "North", "South" }, "review-region");
        }

        [Fact]
        public async Task Loop_entry_expansion_leaves_pendingForEach_absent_from_every_result_and_serializer()
        {
            var run = EarlierSourceRun("wfr-u18-no-pending");
            await ReachLoopEntry(run);
            await WorkflowRunner.SubmitStepAsync(run, "review-region", null, null);
            Assert.All(run.Results, result => Assert.Null(result.PendingForEach));

            var view = WorkflowRunner.BuildView(run);
            Assert.All(view.Steps, step => Assert.Null(step.PendingForEach));
            var json = JsonSerializer.Serialize(view);
            var newtonsoft = Newtonsoft.Json.JsonConvert.SerializeObject(view);
            Assert.DoesNotContain("pendingForEach", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PendingForEach", json, StringComparison.Ordinal);
            Assert.DoesNotContain("pendingForEach", newtonsoft, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PendingForEach", newtonsoft, StringComparison.Ordinal);

            run.StartedUtc = "2026-01-02T03:04:05.0000000Z";
            await WorkflowRunner.SubmitStepAsync(run, "review-region#0", null, null);
            await WorkflowRunner.SubmitStepAsync(run, "review-region#1", null, null);
            Assert.Equal("completed", run.Status);
            run.FinishedUtc = "2026-01-02T03:05:06.0000000Z";
            var recordJson = JsonSerializer.Serialize(WorkflowRunner.BuildRunRecord(run));
            Assert.DoesNotContain("pendingForEach", recordJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"pendingForEach\":null", recordJson, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Loop_entry_expansion_keeps_authored_step_and_foreach_spec_identity()
        {
            var run = EarlierSourceRun("wfr-u18-identity");
            var authoredSteps = run.Def.Steps;
            var loop = authoredSteps[2];
            var spec = loop.ForEach;
            await ReachLoopEntry(run);
            await WorkflowRunner.SubmitStepAsync(run, "review-region", null, null);
            Assert.Same(authoredSteps, run.Def.Steps);
            Assert.Same(loop, run.Def.Steps[2]);
            Assert.Same(spec, run.Def.Steps[2].ForEach);
            Assert.Same(loop, run.Plan[2].Step);
            Assert.Same(loop, run.Plan[3].Step);
            Assert.Equal("regions", spec.InInput);
            Assert.Equal("region", spec.As);
            Assert.Null(spec.InLiteral);
            Assert.All(run.Results, result => Assert.Null(result.PendingForEach));
        }

        [Fact]
        public async Task Setup_condition_handling_is_shared_by_inline_self_sourced_and_deferred_shapes()
        {
            var after = new WorkflowStep { Id = "after", Number = 9, Title = "After" };

            var inlineFalse = InlineExpansionRun("wfr-shared-inline-false", new[] { "North", "South" }, after);
            inlineFalse.Def.Steps[0].When = "inputs.missing.answered";
            await WorkflowRunner.SubmitStepAsync(inlineFalse, "inline-loop", null, null);
            AssertUnexpandedNotApplicable(inlineFalse, 0, "did not apply: condition");

            var inlineUnread = InlineExpansionRun("wfr-shared-inline-unread", new[] { "North", "South" }, after);
            inlineUnread.Def.Steps[0].When = "not a condition";
            await WorkflowRunner.SubmitStepAsync(inlineUnread, "inline-loop", null, null);
            AssertUnexpandedNotApplicable(inlineUnread, 0, "unreadable");

            var inlineOver = InlineExpansionRun("wfr-shared-inline-over", new[] { "North", "South" });
            inlineOver.Def.Steps[0].ForEach.MaxIterations = 1;
            inlineOver.Def.Steps[0].When = "inputs.missing.answered";
            var inlineOverError = await AssertAtomicPreparationRefusal(inlineOver, "inline-loop", null);
            Assert.Contains("skip_workflow_step", inlineOverError.Message, StringComparison.Ordinal);
            Assert.False(inlineOver.Plan[0].ForEachExpanded);
            Assert.NotEqual("not_applicable", inlineOver.Results[0].Status);

            var inlineOverUnread = InlineExpansionRun("wfr-shared-inline-over-unread", new[] { "North", "South" });
            inlineOverUnread.Def.Steps[0].ForEach.MaxIterations = 1;
            inlineOverUnread.Def.Steps[0].When = "not a condition";
            var inlineOverUnreadError = await AssertAtomicPreparationRefusal(inlineOverUnread, "inline-loop", null);
            Assert.Contains("skip_workflow_step", inlineOverUnreadError.Message, StringComparison.Ordinal);
            Assert.False(inlineOverUnread.Plan[0].ForEachExpanded);
            Assert.NotEqual("not_applicable", inlineOverUnread.Results[0].Status);

            var inlineLoop = InlineExpansionRun("wfr-shared-inline-loop", new[] { "North", "South" });
            inlineLoop.Def.Steps[0].When = "loop.item == 'South'";
            await WorkflowRunner.SubmitStepAsync(inlineLoop, "inline-loop", null, null);
            Assert.Equal("inline-loop#1", inlineLoop.Plan[inlineLoop.StepIndex].InstanceId);
            Assert.Equal("not_applicable", inlineLoop.Results[0].Status);

            var selfFalse = PreparationRun("wfr-shared-self-false", after);
            selfFalse.Def.Steps[0].When = "inputs.missing.answered";
            await WorkflowRunner.SubmitStepAsync(selfFalse, "review-region", Regions("North, South"), null);
            AssertUnexpandedNotApplicable(selfFalse, 0, "did not apply: condition");

            var selfUnread = PreparationRun("wfr-shared-self-unread", after);
            selfUnread.Def.Steps[0].When = "not a condition";
            await WorkflowRunner.SubmitStepAsync(selfUnread, "review-region", Regions("North, South"), null);
            AssertUnexpandedNotApplicable(selfUnread, 0, "unreadable");

            var selfMissing = PreparationRun("wfr-shared-self-missing");
            selfMissing.Def.Steps[0].When = "inputs.missing.answered";
            var selfMissingError = await AssertAtomicPreparationRefusal(selfMissing, "review-region", new Dictionary<string, AnswerValue>());
            Assert.Contains("skip_workflow_step", selfMissingError.Message, StringComparison.Ordinal);
            Assert.False(selfMissing.Plan[0].ForEachExpanded);
            Assert.NotEqual("not_applicable", selfMissing.Results[0].Status);

            var selfOver = PreparationRun("wfr-shared-self-over");
            selfOver.Def.Steps[0].ForEach.MaxIterations = 1;
            selfOver.Def.Steps[0].When = "inputs.missing.answered";
            var selfOverError = await AssertAtomicPreparationRefusal(selfOver, "review-region", Regions("North, South"));
            Assert.Contains("skip_workflow_step", selfOverError.Message, StringComparison.Ordinal);
            Assert.False(selfOver.Plan[0].ForEachExpanded);
            Assert.NotEqual("not_applicable", selfOver.Results[0].Status);

            var selfOverUnread = PreparationRun("wfr-shared-self-over-unread");
            selfOverUnread.Def.Steps[0].ForEach.MaxIterations = 1;
            selfOverUnread.Def.Steps[0].When = "not a condition";
            var selfOverUnreadError = await AssertAtomicPreparationRefusal(selfOverUnread, "review-region", Regions("North, South"));
            Assert.Contains("skip_workflow_step", selfOverUnreadError.Message, StringComparison.Ordinal);
            Assert.False(selfOverUnread.Plan[0].ForEachExpanded);
            Assert.NotEqual("not_applicable", selfOverUnread.Results[0].Status);

            var selfAnswered = PreparationRun("wfr-shared-self-source-answered");
            selfAnswered.Def.Steps[0].When = "inputs.regions.answered";
            Assert.Empty(selfAnswered.Results[0].Answers);
            await WorkflowRunner.SubmitStepAsync(selfAnswered, "review-region", Regions("North, South"), null);
            Assert.Equal("review-region#0", selfAnswered.Plan[selfAnswered.StepIndex].InstanceId);

            var selfValue = PreparationRun("wfr-shared-self-source-value");
            selfValue.Def.Steps[0].When = "inputs.regions.value == 'North, South'";
            Assert.Empty(selfValue.Results[0].Answers);
            await WorkflowRunner.SubmitStepAsync(selfValue, "review-region", Regions("North, South"), null);
            Assert.Equal("review-region#0", selfValue.Plan[selfValue.StepIndex].InstanceId);

            var selfLoop = PreparationRun("wfr-shared-self-loop");
            selfLoop.Def.Steps[0].When = "loop.region == 'South'";
            await WorkflowRunner.SubmitStepAsync(selfLoop, "review-region", Regions("North, South"), null);
            Assert.Equal("review-region#1", selfLoop.Plan[selfLoop.StepIndex].InstanceId);
            Assert.Equal("not_applicable", selfLoop.Results[0].Status);

            var deferredFalse = EarlierSourceRun("wfr-shared-deferred-false", hard: true, following: new[] { after });
            deferredFalse.Def.Steps[2].When = "inputs.missing.answered";
            await ReachLoopEntry(deferredFalse);
            await WorkflowRunner.SubmitStepAsync(deferredFalse, "review-region", null, null);
            AssertUnexpandedNotApplicable(deferredFalse, 2, "did not apply: condition");

            var deferredUnread = EarlierSourceRun("wfr-shared-deferred-unread", hard: true, following: new[] { after });
            deferredUnread.Def.Steps[2].When = "not a condition";
            await ReachLoopEntry(deferredUnread);
            await WorkflowRunner.SubmitStepAsync(deferredUnread, "review-region", null, null);
            AssertUnexpandedNotApplicable(deferredUnread, 2, "unreadable");

            var deferredMissing = EarlierSourceRun("wfr-shared-deferred-missing");
            deferredMissing.Def.Steps[2].When = "inputs.missing.answered";
            await WorkflowRunner.SubmitStepAsync(deferredMissing, "choose", new Dictionary<string, AnswerValue>(), null);
            await WorkflowRunner.SubmitStepAsync(deferredMissing, "middle", new Dictionary<string, AnswerValue>
            {
                ["note"] = new AnswerValue { Value = "intervening" },
            }, null);
            var deferredMissingError = await AssertAtomicPreparationRefusal(deferredMissing, "review-region", null);
            Assert.Contains("skip_workflow_step", deferredMissingError.Message, StringComparison.Ordinal);
            Assert.False(deferredMissing.Plan[2].ForEachExpanded);
            Assert.NotEqual("not_applicable", deferredMissing.Results[2].Status);

            var deferredOver = EarlierSourceRun("wfr-shared-deferred-over", maxIterations: 1);
            deferredOver.Def.Steps[2].When = "inputs.missing.answered";
            await ReachLoopEntry(deferredOver);
            var deferredOverError = await AssertAtomicPreparationRefusal(deferredOver, "review-region", null);
            Assert.Contains("skip_workflow_step", deferredOverError.Message, StringComparison.Ordinal);
            Assert.False(deferredOver.Plan[2].ForEachExpanded);
            Assert.NotEqual("not_applicable", deferredOver.Results[2].Status);

            Assert.Equal(NotApplicableRecordShape(inlineFalse, 0), NotApplicableRecordShape(selfFalse, 0));
            Assert.Equal(NotApplicableRecordShape(inlineFalse, 0), NotApplicableRecordShape(deferredFalse, 2));

            var ordinaryUnread = new WorkflowRunState("wfr-shared-ordinary-unread", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-ordinary-unread",
                Version = 2,
                Strictness = "hard",
                Steps = new[]
                {
                    // Gate-bearing on purpose. The recorder stamps EffectiveStrictness only on a gate-bearing
                    // row, so comparing a gateless walk row against the gate-bearing setup rows would compare
                    // two different shapes and pass on the null. The drift this control exists to catch is the
                    // strictness stamp, so both sides have to be able to carry one.
                    new WorkflowStep
                    {
                        Id = "ordinary", Number = 1, Title = "Ordinary",
                        When = "not a condition",
                        Gate = new GateSpec
                        {
                            Inputs = new[] { new GateInput { Name = "finding", Question = "What was found?", Required = "optional" } },
                        },
                    },
                    new WorkflowStep { Id = "after", Number = 2, Title = "After" },
                },
            }, null);
            WorkflowRunner.AdvancePastInapplicableSteps(ordinaryUnread);
            var unreadableRecord = FullNotApplicableRecord(inlineUnread.Results[0]);
            Assert.Equal(unreadableRecord, FullNotApplicableRecord(selfUnread.Results[0]));
            Assert.Equal(unreadableRecord, FullNotApplicableRecord(deferredUnread.Results[2]));
            Assert.Equal(unreadableRecord, FullNotApplicableRecord(ordinaryUnread.Results[0]));

            var deferredLoop = EarlierSourceRun("wfr-shared-deferred-loop");
            deferredLoop.Def.Steps[2].When = "loop.region == 'South'";
            await ReachLoopEntry(deferredLoop);
            await WorkflowRunner.SubmitStepAsync(deferredLoop, "review-region", null, null);
            Assert.Equal("review-region#1", deferredLoop.Plan[deferredLoop.StepIndex].InstanceId);
            Assert.Equal("not_applicable", deferredLoop.Results[2].Status);
        }

        private static string FullNotApplicableRecord(StepResult result)
        {
            var clone = result.Clone();
            clone.StepId = "<expected identity>";
            clone.Title = "<expected title>";
            return JsonSerializer.Serialize(clone);
        }

        private static (string Status, string Note, string EffectiveStrictness, int Answers,
            int VerifyResults, int VerifyHistory, bool HasPendingForEach) NotApplicableRecordShape(
            WorkflowRunState run, int index)
        {
            var result = run.Results[index];
            return (result.Status, result.Note, result.EffectiveStrictness, result.Answers.Count,
                result.VerifyResults.Count(), result.VerifyHistory.Count, result.PendingForEach != null);
        }

        private static void AssertUnexpandedNotApplicable(WorkflowRunState run, int index, string noteNeedle)
        {
            Assert.False(run.Plan[index].ForEachExpanded);
            Assert.Null(run.Plan[index].IterationIndex);
            Assert.Equal("not_applicable", run.Results[index].Status);
            Assert.Contains(noteNeedle, run.Results[index].Note);
            Assert.DoesNotContain(run.Plan, p => p.InstanceId.Contains("#", StringComparison.Ordinal));
            Assert.DoesNotContain(run.Frames, f => f.ProjectionKind == "iteration");
        }

        [Fact]
        public void Loop_entry_expansion_leaves_the_no_loop_golden_bytes_unchanged()
        {
            var run = new WorkflowRunState("wfr-seed-record", NewMeasureSeed(), null)
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

        // ---- unit 9: internal, non-mutating resolution of the current forEach list ----------------

        private static WorkflowStep InlineLoop(string[] literal) => new WorkflowStep
        {
            Id = "inline-loop",
            Number = 1,
            Title = "Inline invented loop",
            ForEach = new ForEachSpec { InLiteral = literal, As = "item", MaxIterations = 9 },
        };

        private static WorkflowStep InlineExpansionStep(
            string[] literal, string id = "inline-loop", int number = 1, int maxIterations = 9) => new WorkflowStep
        {
            Id = id,
            Number = number,
            Title = "Inline invented loop",
            Instructions = AuthoredInlineInstructions,
            Ops = new[] { "edit_measure" },
            ForEach = new ForEachSpec { InLiteral = literal, As = "item", MaxIterations = maxIterations },
            Gate = new GateSpec
            {
                Inputs = new[]
                {
                    new GateInput { Name = "finding", Question = "What was found?", Required = "optional" },
                    new GateInput { Name = "note", Question = "Any note?", Required = "optional" },
                },
                Verify = new[] { new VerifySpec { Kind = "invented_check" } },
            },
        };

        private static WorkflowRunState InlineExpansionRun(string runId, string[] literal, params WorkflowStep[] following) =>
            new WorkflowRunState(runId, new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-inline-expansion",
                Version = 2,
                Strictness = "hard",
                Steps = new[] { InlineExpansionStep(literal) }.Concat(following).ToArray(),
            }, null);


        /// <summary>A run whose list source is declared two steps before the loop, with one intervening row.</summary>
        private static WorkflowRunState EarlierSourceRun(
            string runId, bool hard = false, int maxIterations = 9, WorkflowStep[] between = null, params WorkflowStep[] following)
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
                    },
                    Verify = new[] { new VerifySpec { Kind = "invented_check" } },
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
                Instructions = AuthoredPreparationInstructions,
                Ops = new[] { "edit_measure" },
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = maxIterations },
                Gate = new GateSpec
                {
                    Inputs = new[] { new GateInput { Name = "finding", Question = "What was found?", Required = "optional" } },
                    Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                },
            };
            return new WorkflowRunState(runId, new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-earlier-source",
                Version = 2,
                Strictness = hard ? "hard" : "off",
                Steps = new[] { choose, middle }.Concat(between ?? Array.Empty<WorkflowStep>()).Concat(new[] { loop }).Concat(following).ToArray(),
            }, null);
        }

        private static async Task ReachLoopEntry(WorkflowRunState run, string list = "North, South", string note = "intervening")
        {
            await WorkflowRunner.SubmitStepAsync(run, "choose", Regions(list), null);
            AssertNoDecision(run.Results[0]);
            Assert.DoesNotContain(run.Plan, p => p.ForEachExpanded);
            await WorkflowRunner.SubmitStepAsync(run, "middle", new Dictionary<string, AnswerValue>
            {
                ["note"] = new AnswerValue { Value = note },
            }, null);
            AssertNoDecision(run.Results[1]);
        }

        /// <summary>A run whose list source is adjacent to the first loop, with a second loop waiting for its own setup.</summary>
        private static WorkflowRunState AdjacentThenWaitingRun(string runId, bool hard = false)
        {
            return new WorkflowRunState(runId, new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-adjacent-then-waiting",
                Version = 2,
                Strictness = hard ? "hard" : "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "choose", Number = 1, Title = "Choose invented regions",
                        Instructions = "Choose the invented list.",
                        Ops = new[] { "ask_user" },
                        Gate = new GateSpec
                        {
                            Inputs = new[] { new GateInput { Name = "regions", Question = "Which invented regions?", Required = "optional" } },
                            Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                        },
                    },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 2, Title = "Review invented region",
                        Instructions = AuthoredPreparationInstructions,
                        Ops = new[] { "edit_measure" },
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                        Gate = new GateSpec
                        {
                            Inputs = new[] { new GateInput { Name = "finding", Required = "optional" } },
                            Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                        },
                    },
                    new WorkflowStep
                    {
                        Id = "review-again", Number = 3, Title = "Review again",
                        Instructions = "Authored body: review [[loop.region]] again.",
                        Ops = new[] { "edit_measure" },
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                    },
                },
            }, null);
        }

        /// <summary>A run whose step 1 declares the list input and whose step 2 is the loop over it.</summary>
        private static WorkflowRunState InputBackedRun(string runId, out WorkflowStep loop)
        {
            var declaring = new WorkflowStep
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
                    },
                    Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                },
            };
            loop = new WorkflowStep
            {
                Id = "review-region",
                Number = 2,
                Title = "Review invented region",
                Instructions = AuthoredPreparationInstructions,
                Ops = new[] { "edit_measure" },
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                Gate = new GateSpec
                {
                    Inputs = new[] { new GateInput { Name = "finding", Question = "What was found?", Required = "optional" } },
                    Verify = new[] { new VerifySpec { Kind = "invented_check" } },
                },
            };
            return new WorkflowRunState(runId, new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-input-loop",
                Version = 2,
                Strictness = "hard",
                Steps = new[] { declaring, loop },
            }, null);
        }

        private static void AssertPairedDecision(StepResult result, string expectedOutcome, string expectedState,
            string[] expectedValues, string expectedTargetId)
        {
            Assert.NotNull(result.PendingForEach);
            Assert.Equal(expectedOutcome, result.PendingForEach.Outcome);
            Assert.Equal(expectedState, result.PendingForEach.State);
            Assert.Equal(expectedValues, result.PendingForEach.Values);
            Assert.Equal(expectedTargetId, result.PendingForEach.TargetStepId);
            Assert.Equal("regions", result.PendingForEach.SourceInput);
        }

        private static void AssertNoDecision(StepResult result) => Assert.Null(result.PendingForEach);

        private static Dictionary<string, AnswerValue> Regions(string value) => new Dictionary<string, AnswerValue>
        {
            ["regions"] = new AnswerValue { Value = value },
        };

        [Fact]
        public void Inline_loop_values_resolve_to_a_defensive_copy_of_the_exact_authored_strings()
        {
            var authored = new[] { "  North  ", "South", "", "East, West" };
            var loop = InlineLoop(authored);
            var run = new WorkflowRunState("wfr-inline", new WorkflowDef
            {
                Name = "invented-inline",
                Version = 2,
                Steps = new[] { loop },
            }, null);

            var values = WorkflowRunner.ResolveCurrentForEachValues(run);

            // Inline entries are already separate items: they are handed back verbatim, never re-split,
            // re-trimmed, or filtered, so an author who wrote a padded or comma-bearing name still gets it.
            Assert.Equal(authored, values);
            Assert.NotSame(authored, values);
            Assert.NotSame(values, WorkflowRunner.ResolveCurrentForEachValues(run));
            Assert.Equal(authored, loop.ForEach.InLiteral);
            Assert.Same(authored, loop.ForEach.InLiteral);

            var emptyRun = new WorkflowRunState("wfr-inline-empty",
                new WorkflowDef { Name = "invented-inline-empty", Version = 2, Steps = new[] { InlineLoop(Array.Empty<string>()) } },
                null);
            var firstEmpty = Assert.IsType<string[]>(WorkflowRunner.ResolveCurrentForEachValues(emptyRun));
            var secondEmpty = Assert.IsType<string[]>(WorkflowRunner.ResolveCurrentForEachValues(emptyRun));
            Assert.Empty(firstEmpty);
            Assert.Empty(secondEmpty);
            Assert.NotSame(firstEmpty, secondEmpty);
        }

        [Fact]
        public void Input_backed_loop_values_split_on_commas_and_line_breaks_then_trim_and_drop_blanks()
        {
            var run = InputBackedRun("wfr-split", out _);
            run.Results[0].Status = "passed";
            run.Results[0].Answers["regions"] = new AnswerValue { Value = " North , South\r\nEast\n\n  \n , ,West  " };
            run.StepIndex = 1;
            run.Results[1].Status = "in_progress";

            Assert.Equal(new[] { "North", "South", "East", "West" }, WorkflowRunner.ResolveCurrentForEachValues(run));

            // Acceptance check 14(b): an OPTIONAL list input ANSWERED blank is an empty loop, not a refusal and
            // not a one-item loop over the empty string. The empty and all-whitespace spellings are asserted
            // separately from the separators-only one: a blank check written as IsNullOrWhiteSpace passes the
            // separators-only case by accident (a comma is not whitespace) and refuses these two.
            foreach (var answeredBlank in new[] { "", "   ", "\r\n", " \t ", "  \r\n , \n " })
            {
                var blank = InputBackedRun("wfr-blank", out _);
                blank.Results[0].Answers["regions"] = new AnswerValue { Value = answeredBlank };
                blank.StepIndex = 1;
                var firstEmpty = Assert.IsType<string[]>(WorkflowRunner.ResolveCurrentForEachValues(blank));
                var secondEmpty = Assert.IsType<string[]>(WorkflowRunner.ResolveCurrentForEachValues(blank));
                Assert.Empty(firstEmpty);
                Assert.Empty(secondEmpty);
                Assert.NotSame(firstEmpty, secondEmpty);
            }

            var single = InputBackedRun("wfr-single", out _);
            single.Results[0].Answers["regions"] = new AnswerValue { Value = "Sales" };
            single.StepIndex = 1;
            Assert.Equal(new[] { "Sales" }, WorkflowRunner.ResolveCurrentForEachValues(single));
        }

        [Fact]
        public void Input_backed_resolution_reads_the_current_planned_frame_through_an_inclusive_cutoff()
        {
            var run = InputBackedRun("wfr-frame", out _);
            var loop = run.Plan[1].Step;
            run.Frames.Add(RunFrame.CreateIteration("wfr-frame:outer:0", "outer", 0, "outer", "one", "in_progress",
                new Dictionary<string, AnswerValue> { ["regions"] = new AnswerValue { Value = "Seeded" } }));
            run.Plan[1] = new PlannedStep(loop, "review-region@outer", "wfr-frame:outer:0", null);
            run.Results[1].StepId = "review-region@outer";
            run.StepIndex = 1;

            // The top-frame row at index 0 is a different frame, and `regions` is not scope: run, so it stays
            // invisible: the loop resolves against its own frame's seed.
            run.Results[0].Answers["regions"] = new AnswerValue { Value = "TopFrameOnly" };
            Assert.Equal(new[] { "Seeded" }, WorkflowRunner.ResolveCurrentForEachValues(run));

            // The cutoff is INCLUSIVE of the loop row itself, so an answer recorded on the loop's own result
            // (the submission that resolves the list) wins over the seed.
            run.Results[1].Answers["regions"] = new AnswerValue { Value = "North,South" };
            Assert.Equal(new[] { "North", "South" }, WorkflowRunner.ResolveCurrentForEachValues(run));

            // A LATER row in the same frame is past the cutoff and must not be read.
            run.Plan.Add(new PlannedStep(loop, "later", "wfr-frame:outer:0", null));
            run.Results.Add(new StepResult { StepId = "later", Title = "Later", Status = "pending" });
            run.Results[2].Answers["regions"] = new AnswerValue { Value = "FromTheFuture" };
            Assert.Equal(new[] { "North", "South" }, WorkflowRunner.ResolveCurrentForEachValues(run));
        }

        [Fact]
        public void A_missing_or_declined_loop_input_refuses_clearly_without_mutating_the_run()
        {
            var missing = InputBackedRun("wfr-missing", out _);
            missing.StepIndex = 1;
            var missingEx = AssertNonMutatingRefusal(missing);
            Assert.Contains("regions", missingEx.Message);
            Assert.Contains("unanswered", missingEx.Message, StringComparison.OrdinalIgnoreCase);

            // A null value is the same gap as no row at all: it is not an empty list.
            var nullValued = InputBackedRun("wfr-null-answer", out _);
            nullValued.Results[0].Answers["regions"] = new AnswerValue { Value = null };
            nullValued.StepIndex = 1;
            Assert.Contains("unanswered", AssertNonMutatingRefusal(nullValued).Message, StringComparison.OrdinalIgnoreCase);

            var declined = InputBackedRun("wfr-declined", out _);
            declined.Results[0].Answers["regions"] = new AnswerValue { Declined = true, DeclineReason = "invented reason" };
            declined.StepIndex = 1;
            var declinedEx = AssertNonMutatingRefusal(declined);
            Assert.Contains("regions", declinedEx.Message);
            Assert.Contains("declined", declinedEx.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("invented reason", declinedEx.Message);
        }

        [Fact]
        public void Resolution_refuses_a_null_run_a_terminal_run_a_missing_entry_and_a_misaligned_plan()
        {
            Assert.Throws<ArgumentNullException>(() => WorkflowRunner.ResolveCurrentForEachValues(null));

            var terminal = InputBackedRun("wfr-res-terminal", out _);
            terminal.Results[0].Answers["regions"] = new AnswerValue { Value = "North" };
            terminal.StepIndex = 1;
            terminal.Status = "completed";
            Assert.Contains("completed", AssertNonMutatingRefusal(terminal).Message);

            var past = InputBackedRun("wfr-res-past", out _);
            past.StepIndex = past.Plan.Count;
            Assert.Contains("no current plan entry", AssertNonMutatingRefusal(past).Message);

            var unaligned = InputBackedRun("wfr-res-unaligned", out _);
            unaligned.StepIndex = 1;
            unaligned.Results.RemoveAt(0);
            Assert.Contains("not aligned", AssertNonMutatingRefusal(unaligned).Message);

            var wrongStepId = InputBackedRun("wfr-res-wrong-id", out _);
            wrongStepId.StepIndex = 1;
            wrongStepId.Results[1].StepId = "some-other-instance";
            Assert.Contains("not aligned", AssertNonMutatingRefusal(wrongStepId).Message);
        }

        [Fact]
        public void Resolution_refuses_a_non_loop_row_an_iteration_row_and_an_already_expanded_template()
        {
            var plain = new WorkflowRunState("wfr-res-plain", new WorkflowDef
            {
                Name = "invented-res-plain",
                Steps = new[] { new WorkflowStep { Id = "plain", Number = 1, Title = "Plain" } },
            }, null);
            Assert.Contains("not a forEach template", AssertNonMutatingRefusal(plain).Message);

            var loop = InlineLoop(new[] { "only" });
            var iteration = new WorkflowRunState("wfr-res-iteration", new WorkflowDef
            {
                Name = "invented-res-iteration",
                Version = 2,
                Steps = new[] { loop },
            }, null);
            iteration.Frames.Add(RunFrame.CreateIteration("wfr-res-iteration:inline-loop:0", "inline-loop", 0,
                "item", "only", "in_progress"));
            iteration.Plan[0] = new PlannedStep(loop, "inline-loop#0", "wfr-res-iteration:inline-loop:0", 0);
            iteration.Results[0].StepId = "inline-loop#0";
            Assert.Contains("iteration row", AssertNonMutatingRefusal(iteration).Message);

            var expanded = new WorkflowRunState("wfr-res-expanded", new WorkflowDef
            {
                Name = "invented-res-expanded",
                Version = 2,
                Steps = new[] { InlineLoop(Array.Empty<string>()), new WorkflowStep { Id = "next", Number = 2, Title = "Next" } },
            }, null);
            WorkflowRunner.ExpandCurrentForEach(expanded, Array.Empty<string>());
            expanded.StepIndex = 0;
            Assert.Contains("already expanded", AssertNonMutatingRefusal(expanded).Message);
        }

        [Fact]
        public void A_successful_resolution_mutates_neither_the_run_nor_the_authored_definition()
        {
            var run = InputBackedRun("wfr-res-pure", out var loop);
            run.Results[0].Status = "passed";
            run.Results[0].Answers["regions"] = new AnswerValue { Value = "North, South" };
            run.StepIndex = 1;
            run.Results[1].Status = "in_progress";
            var before = Snapshot(run);
            var authoredForEach = loop.ForEach;

            Assert.Equal(new[] { "North", "South" }, WorkflowRunner.ResolveCurrentForEachValues(run));

            AssertUnchanged(before, run);
            Assert.Same(authoredForEach, loop.ForEach);
            Assert.Equal("regions", loop.ForEach.InInput);
            Assert.Null(loop.ForEach.InLiteral);
            Assert.Equal("North, South", run.Results[0].Answers["regions"].Value);

            // Resolution is not expansion: the current row is still the unexpanded template.
            Assert.False(run.Plan[1].ForEachExpanded);
            Assert.True(WorkflowRunner.BuildView(run).TotalStepsProvisional);
        }

        private static readonly string[] TopFrameProofStoreNames =
        {
            "WitnessLocks", "WitnessRevisions", "PartitionLocks", "PartitionRevisions",
            "RunAnchorLocks", "AnchorRevisions", "CoverageSurface", "CoverageSurfaceRevisions",
            "ShapeMismatchLedger", "ShapeMismatchCountersigns", "LastPassedEquivalenceCandidate",
            "AnchorFormRepairCount",
        };

        private sealed record PlanSnapshot(
            PlannedStep[] Plan, StepResult[] Results, string[] Statuses, RunFrame[] Frames,
            string[] TopFrameProofStores, int StepIndex, string Status);

        private static PlanSnapshot Snapshot(WorkflowRunState run) => new PlanSnapshot(
            run.Plan.ToArray(), run.Results.ToArray(), run.Results.Select(r => r.Status).ToArray(),
            run.Frames.ToArray(), SnapshotTopFrameProofStores(run.Frames[0]), run.StepIndex, run.Status);

        private static string[] SnapshotTopFrameProofStores(RunFrame frame) =>
            TopFrameProofStoreNames.Select(name =>
            {
                var value = typeof(RunFrame).GetProperty(name)!.GetValue(frame);
                return name + ":" + JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object));
            }).ToArray();

        private static void AssertUnchanged(PlanSnapshot before, WorkflowRunState run)
        {
            Assert.Equal(before.Plan, run.Plan);
            Assert.Equal(before.Results, run.Results);
            Assert.Equal(before.Statuses, run.Results.Select(r => r.Status));
            Assert.Equal(before.Frames, run.Frames);
            Assert.Equal(12, before.TopFrameProofStores.Length);
            Assert.Equal(before.TopFrameProofStores, SnapshotTopFrameProofStores(run.Frames[0]));
            Assert.Equal(before.StepIndex, run.StepIndex);
            Assert.Equal(before.Status, run.Status);
        }

        private static Exception AssertNonMutatingRefusal(WorkflowRunState run)
        {
            var before = Snapshot(run);
            var ex = Assert.ThrowsAny<Exception>(() => WorkflowRunner.ResolveCurrentForEachValues(run));
            AssertUnchanged(before, run);
            return ex;
        }

        [Fact]
        public void Both_run_view_serializers_omit_the_provisional_member_for_a_no_loop_v1_run()
        {
            var view = WorkflowRunner.BuildView(new WorkflowRunState("wfr-linear", NewMeasureSeed(), null));

            Assert.Null(view.TotalStepsProvisional);
            Assert.DoesNotContain("TotalStepsProvisional", JsonSerializer.Serialize(view));
            Assert.DoesNotContain("TotalStepsProvisional", Newtonsoft.Json.JsonConvert.SerializeObject(view));
        }

        [Fact]
        public void No_loop_stock_seed_terminal_record_keeps_the_exact_legacy_JSON_bytes()
        {
            var run = new WorkflowRunState("wfr-seed-record", NewMeasureSeed(), null)
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
        public async Task Deferred_terminal_record_carries_the_applied_decision_and_omits_it_from_other_rows()
        {
            var run = InputBackedRun("wfr-u17-terminal", out _);
            run.StartedUtc = "2026-01-02T03:04:05.0000000Z";
            await WorkflowRunner.SubmitStepAsync(run, "choose", Regions("North, South"), null);
            await WorkflowRunner.SubmitStepAsync(run, "review-region#0", null, null);
            await WorkflowRunner.SubmitStepAsync(run, "review-region#1", null, null);
            Assert.Equal("completed", run.Status);
            run.FinishedUtc = "2026-01-02T03:05:06.0000000Z";

            var record = WorkflowRunner.BuildRunRecord(run);
            var json = JsonSerializer.Serialize(record);
            using var document = JsonDocument.Parse(json);
            var steps = document.RootElement.GetProperty("steps");
            Assert.Equal(3, steps.GetArrayLength());
            Assert.True(steps[0].TryGetProperty("pendingForEach", out var pending));
            Assert.Equal("expand", pending.GetProperty("Outcome").GetString());
            Assert.Equal("applied", pending.GetProperty("State").GetString());
            Assert.Equal("review-region", pending.GetProperty("TargetStepId").GetString());
            Assert.Equal(1, pending.GetProperty("TargetIndex").GetInt32());
            Assert.Equal("regions", pending.GetProperty("SourceInput").GetString());
            Assert.Equal(new[] { "North", "South" }, pending.GetProperty("Values").EnumerateArray().Select(v => v.GetString()).ToArray());
            Assert.False(steps[1].TryGetProperty("pendingForEach", out _));
            Assert.False(steps[2].TryGetProperty("pendingForEach", out _));
            Assert.DoesNotContain("\"pendingForEach\":null", json, StringComparison.Ordinal);

            run.Results[0].PendingForEach.State = "abandoned";
            run.Results[0].PendingForEach.Values[0] = "mutated";
            using var held = JsonDocument.Parse(JsonSerializer.Serialize(record));
            var frozen = held.RootElement.GetProperty("steps")[0].GetProperty("pendingForEach");
            Assert.Equal("applied", frozen.GetProperty("State").GetString());
            Assert.Equal("North", frozen.GetProperty("Values")[0].GetString());
        }

        // ---- T220 planned-row ownership: the red foundation (T1-T5) ------------------------------
        // Recovered verbatim in intent from the T-1553 pack (9a1ab178, never an ancestor of main) so the
        // two branches stay comparable. Three deliberate repairs, per section 3.4 of
        // docs/notes/T220-planned-step-owner-plan.md: the run's root is a FROZEN COPY, so an assertion that
        // compared an owner against the mutable constructor input now proves NotSame(input, run.Def) first
        // and Same(run.Def, owner) second; a foreign row is built from its own frozen owner's steps, because
        // owner-step coherence is by reference; and public identity is still asserted by VALUE.
        // Owner is reached by reflection because it is internal by design and this pack must compile, and
        // fail, against a tree where it does not exist.

        [Fact]
        public void Planned_row_owner_is_the_run_definition_on_every_top_row()
        {
            var first = new WorkflowStep { Id = "ask", Number = 1, Title = "Ask" };
            var second = new WorkflowStep { Id = "prove", Number = 2, Title = "Prove" };
            var def = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-owner-top",
                Version = 2,
                Strictness = "off",
                Steps = new[] { first, second },
            };
            var run = new WorkflowRunState("wfr-owner-top", def, "warn");

            Assert.Equal(2, run.Plan.Count);
            Assert.Same(run.Def, RowOwner(run.Plan[0]));
            Assert.Same(run.Def, RowOwner(run.Plan[1]));
            Assert.NotSame(def, run.Def);
            Assert.Same(run.Def.Steps[0], run.Plan[0].Step);
            Assert.Same(run.Def.Steps[1], run.Plan[1].Step);
            Assert.NotSame(first, run.Plan[0].Step);
            Assert.NotSame(second, run.Plan[1].Step);

            // The frozen root answers every public identity question exactly as the input did.
            Assert.Equal("invented-owner-top", run.Def.Name);
            Assert.Equal(2, run.Def.Version);
            Assert.Equal("off", run.Def.Strictness);
            Assert.Equal(new[] { "ask", "prove" }, run.Def.Steps.Select(s => s.Id).ToArray());
        }

        [Fact]
        public void Planned_row_owner_is_preserved_when_a_loop_row_expands()
        {
            var loop = new WorkflowStep
            {
                Id = "review-region",
                Number = 1,
                Title = "Review invented region",
                ForEach = new ForEachSpec { InLiteral = new[] { "North", "South" }, As = "region", MaxIterations = 9 },
            };
            var def = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-owner-loop",
                Version = 2,
                Strictness = "off",
                Steps = new[] { loop },
            };

            var expanded = new WorkflowRunState("wfr-owner-loop", def, null);
            var owner = RowOwner(expanded.Plan[0]);
            Assert.Same(expanded.Def, owner);
            Assert.NotSame(def, owner);
            var frozenLoop = expanded.Plan[0].Step;
            WorkflowRunner.ExpandCurrentForEach(expanded, new[] { "North", "South" });
            Assert.Equal(2, expanded.Plan.Count);
            Assert.Same(owner, RowOwner(expanded.Plan[0]));
            Assert.Same(owner, RowOwner(expanded.Plan[1]));
            Assert.Same(frozenLoop, expanded.Plan[0].Step);
            Assert.Same(frozenLoop, expanded.Plan[1].Step);

            var empty = new WorkflowRunState("wfr-owner-empty-loop", def, null);
            var emptyOwner = RowOwner(empty.Plan[0]);
            WorkflowRunner.ExpandCurrentForEach(empty, Array.Empty<string>());
            Assert.Single(empty.Plan);
            Assert.True(empty.Plan[0].ForEachExpanded);
            Assert.Same(emptyOwner, RowOwner(empty.Plan[0]));
        }

        [Fact]
        public void Planned_row_owner_keeps_same_named_caller_and_callee_inputs_apart()
        {
            var callerStep = new WorkflowStep
            {
                Id = "ask",
                Number = 1,
                Title = "Ask",
                Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "item", Required = "optional" } } },
            };
            var callStep = new WorkflowStep { Id = "prove-it", Number = 2, Title = "Prove it" };
            var caller = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-caller",
                Version = 2,
                Strictness = "off",
                Steps = new[] { callerStep, callStep },
            };
            var calleeStep = new WorkflowStep
            {
                Id = "use",
                Number = 1,
                Title = "Use",
                Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "item", Required = "optional" } } },
            };
            var callee = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-callee",
                Version = 2,
                Strictness = "off",
                Steps = new[] { calleeStep },
            };
            var run = new WorkflowRunState("wfr-owner-same-name", caller, null);
            var frozenCallee = FrozenCopyOf(callee);
            run.Results[0].Answers["item"] = new AnswerValue { Value = "caller-item" };
            var call = RunFrame.CreateCall(
                "wfr-owner-same-name:prove-it", "prove-it", "invented-callee", 2,
                new[] { "item" }, Array.Empty<string>(), "in_progress",
                new Dictionary<string, AnswerValue> { ["item"] = new AnswerValue { Value = "callee-item" } });
            run.Frames.Add(call);
            run.Plan[1] = AssignOwner(
                new PlannedStep(frozenCallee.Steps[0], "prove-it/use", call.FrameId, null), frozenCallee);
            run.Results[1].StepId = "prove-it/use";
            run.Results[1].Answers["item"] = new AnswerValue { Value = "callee-item" };

            Assert.Same(run.Def, RowOwner(run.Plan[0]));
            Assert.Same(frozenCallee, RowOwner(run.Plan[1]));
            Assert.NotSame(RowOwner(run.Plan[0]), RowOwner(run.Plan[1]));

            run.StepIndex = 1;
            var calleeAnswers = WorkflowRunner.AllAnswers(run);
            Assert.Equal("callee-item", calleeAnswers["item"].Value);
            Assert.DoesNotContain("caller-item", calleeAnswers.Values.Select(v => v.Value));

            run.StepIndex = 0;
            var callerAnswers = WorkflowRunner.AllAnswers(run);
            Assert.Equal("caller-item", callerAnswers["item"].Value);
            Assert.DoesNotContain("callee-item", callerAnswers.Values.Select(v => v.Value));
        }

        [Fact]
        public async Task Planned_row_owner_freezes_per_workflow_settings()
        {
            var callerBody = new WorkflowStep
            {
                Id = "ask",
                Number = 1,
                Title = "Ask",
                Gate = new GateSpec(),
            };
            var callStep = new WorkflowStep { Id = "prove-it", Number = 2, Title = "Prove it" };
            var caller = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-caller-settings",
                Version = 2,
                Strictness = "hard",
                Steps = new[] { callerBody, callStep },
            };
            var calleeBody = new WorkflowStep
            {
                Id = "use",
                Number = 1,
                Title = "Use",
                Gate = new GateSpec(),
            };
            var callee = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-callee-settings",
                Version = 2,
                Strictness = "off",
                Steps = new[] { calleeBody },
            };
            var run = new WorkflowRunState("wfr-owner-settings", caller, "warn");
            var frozenCallee = FrozenCopyOf(callee);
            Assert.Same(run.Def, RowOwner(run.Plan[0]));

            // The run holds "warn" for the CALLER's name only, so the caller row resolves settings over its
            // own frozen frontmatter and a later edit of the source file cannot retune it.
            caller.Strictness = "off";
            await WorkflowRunner.SubmitStepAsync(run, "ask", null, null);
            Assert.Equal("warn", run.Results[0].EffectiveStrictness);

            var call = RunFrame.CreateCall(
                "wfr-owner-settings:prove-it", "prove-it", "invented-callee-settings", 2,
                Array.Empty<string>(), Array.Empty<string>(), "in_progress");
            run.Frames.Add(call);
            run.Plan[1] = AssignOwner(
                new PlannedStep(frozenCallee.Steps[0], "prove-it/use", call.FrameId, null), frozenCallee);
            run.Results[1].StepId = "prove-it/use";
            run.Results[1].Title = frozenCallee.Steps[0].Title;
            // No per-workflow setting was frozen for the callee's name, so its row falls to its OWN frozen
            // frontmatter, not to the caller's "warn" and not to the post-start mutation below.
            callee.Strictness = "hard";
            await WorkflowRunner.SubmitStepAsync(run, "prove-it/use", null, null);
            Assert.Equal("off", run.Results[1].EffectiveStrictness);
        }

        [Fact]
        public void Planned_row_owner_leaves_caller_identity_public()
        {
            var callerStep = new WorkflowStep { Id = "ask", Number = 1, Title = "Ask" };
            var callStep = new WorkflowStep { Id = "prove-it", Number = 2, Title = "Prove it" };
            var caller = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-caller",
                Title = "Invented caller",
                Version = 2,
                Strictness = "off",
                Steps = new[] { callerStep, callStep },
            };
            var calleeStep = new WorkflowStep { Id = "use", Number = 1, Title = "Use" };
            var callee = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-callee",
                Title = "Invented callee",
                Version = 4,
                Strictness = "off",
                Steps = new[] { calleeStep },
            };
            var run = new WorkflowRunState("wfr-owner-public", caller, null);
            var frozenCallee = FrozenCopyOf(callee);
            var call = RunFrame.CreateCall(
                "wfr-owner-public:prove-it", "prove-it", "invented-callee", 2,
                Array.Empty<string>(), Array.Empty<string>(), "in_progress");
            run.Frames.Add(call);
            run.Plan[1] = AssignOwner(
                new PlannedStep(frozenCallee.Steps[0], "prove-it/use", call.FrameId, null), frozenCallee);
            run.Results[1].StepId = "prove-it/use";
            run.Results[1].Title = frozenCallee.Steps[0].Title;

            Assert.Same(run.Def, RowOwner(run.Plan[0]));
            Assert.Same(frozenCallee, RowOwner(run.Plan[1]));

            var view = WorkflowRunner.BuildView(run);
            Assert.Equal("invented-caller", view.Workflow);
            Assert.Equal("Invented caller", view.Title);
            Assert.Equal(2, view.WorkflowVersion);
            var viewJson = JsonSerializer.Serialize(view);
            Assert.DoesNotContain("\"Owner\"", viewJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"OwnerClosure\"", viewJson, StringComparison.Ordinal);
            Assert.Contains("\"Workflow\":\"invented-caller\"", viewJson, StringComparison.Ordinal);

            var recordJson = JsonSerializer.Serialize(WorkflowRunner.BuildRunRecord(run));
            Assert.Contains("\"workflow\":\"invented-caller\"", recordJson, StringComparison.Ordinal);
            Assert.DoesNotContain("invented-callee", recordJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Owner\"", recordJson, StringComparison.Ordinal);
            Assert.DoesNotContain("\"OwnerClosure\"", recordJson, StringComparison.Ordinal);
        }

        // ---- T220 planned-row ownership: the closure foundation (T6, T15-T19) ---------------------

        [Fact]
        public async Task Planned_row_owner_freezes_a_later_definition_strictness_mutation_out(/* T6 */)
        {
            var callerStep = new WorkflowStep
            {
                Id = "ask", Number = 1, Title = "Ask",
                Gate = new GateSpec { Verify = new[] { new VerifySpec { Kind = "dax_probe" } } },
            };
            var callStep = new WorkflowStep { Id = "prove-it", Number = 2, Title = "Prove it" };
            var caller = new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-t6-caller", Version = 2, Strictness = "hard",
                Steps = new[] { callerStep, callStep },
            };
            var calleeStep = new WorkflowStep
            {
                Id = "use", Number = 1, Title = "Use",
                Gate = new GateSpec { Verify = new[] { new VerifySpec { Kind = "dax_probe" } } },
            };
            var callee = new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-t6-callee", Version = 2, Strictness = "warn",
                Steps = new[] { calleeStep },
            };
            var run = WorkflowOwnerFixtures.ClosureRun("wfr-t6", null, (caller, null), (callee, null));

            // The source definitions are edited AFTER start, exactly as a mid-run file reload would.
            callee.Strictness = "hard";
            calleeStep.Gate.Verify[0].Kind = "bpa_clean";

            var call = RunFrame.CreateCall("wfr-t6:prove-it", "prove-it", callee.Name, 2,
                Array.Empty<string>(), Array.Empty<string>(), "in_progress");
            run.Frames.Add(call);
            run.Plan[1] = WorkflowOwnerFixtures.CalleeRow(run, callee.Name, 0, "prove-it/use", call.FrameId);
            run.Results[1].StepId = "prove-it/use";
            run.Results[1].Title = "Use";

            WorkflowVerifyExecutor failing = (s, st, r, a) =>
                Task.FromResult(new VerifyResult { Kind = s.Kind, Status = "failed", Detail = "invented failure" });

            // The caller row is hard, so its failing verify blocks and the run stays on it.
            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WorkflowRunner.SubmitStepAsync(run, "ask", null, failing));
            Assert.Contains("did not clear its hard gate", blocked.Message);
            Assert.Equal("failed", run.Results[0].Status);
            Assert.Equal("hard", run.Results[0].EffectiveStrictness);

            WorkflowRunner.SkipStep(run, "ask", "fixture moves past the caller row");
            Assert.Equal(1, run.StepIndex);

            // The callee row is warn, from its OWN frozen frontmatter. A design that froze only settings would
            // read "hard" here, because that is what the source object says by now.
            await WorkflowRunner.SubmitStepAsync(run, "prove-it/use", null, failing);
            Assert.Equal("warn", run.Results[1].EffectiveStrictness);
            Assert.Equal("failed", run.Results[1].Status);          // warn advances the run; the badge still follows the record
            Assert.StartsWith("warn gate: ", run.Results[1].Note);
            Assert.Contains("dax_probe", run.Results[1].Note);
            Assert.Equal("completed", run.Status);
            // The mutated verify kind never reached the row either.
            Assert.Equal("dax_probe", run.Plan[1].Step.Gate.Verify[0].Kind);
        }

        [Fact]
        public void Reachable_closure_is_complete_and_precedes_every_refusal(/* T15 */)
        {
            var a = CallDef("invented-a", ("to-b", "invented-b"), ("to-c", "invented-c"));
            var b = CallDef("invented-b", ("to-d", "invented-d"));
            var c = CallDef("invented-c", ("to-d", "invented-d"));
            var d = CallDef("invented-d");
            var library = new[] { a, b, c, d };

            var reachable = WorkflowParser.ReachableClosure(a, library, Array.Empty<WorkflowDef>(), out var clean);
            Assert.Empty(clean);
            // Discovery order, root first, depth first: A, then B and everything under B, then C.
            Assert.Equal(new[] { "invented-a", "invented-b", "invented-d", "invented-c" }, reachable.Select(x => x.Name));

            // One frozen D by reference, and one settings read per NAME rather than per edge.
            var reads = new List<string>();
            var run = WorkflowOwnerFixtures.ClosureRun("wfr-t15", null,
                reachable.Select(x => { reads.Add(x.Name); return (x, (string)null); }).ToArray());
            Assert.Equal(new[] { "invented-a", "invented-b", "invented-d", "invented-c" }, reads);
            Assert.Equal(4, run.OwnerClosure.Definitions.Count);
            Assert.Same(run.OwnerClosure.Require("invented-d"), run.OwnerClosure.Require("INVENTED-D"));
            Assert.Same(run.Def, run.OwnerClosure.Root);
            Assert.True(run.OwnerClosure.Contains(run.Def));

            // Every unrunnable variant is named by its OWN reason, and each is found by the same walk.
            AssertClosureProblem(CallDef("invented-self", ("loop", "invented-self")), Array.Empty<WorkflowDef>(),
                "is this workflow itself");
            AssertClosureProblem(CallDef("invented-missing-root", ("gone", "invented-absent")), Array.Empty<WorkflowDef>(),
                "is not a workflow this library holds");
            AssertClosureProblem(a, new[] { b, c }, "is not a workflow this library holds");   // D missing, deep
            AssertClosureProblem(CallDef("invented-tpl-root", ("t", "invented-tpl")),
                new[] { new WorkflowDef { Name = "invented-tpl", Kind = "template" } }, "is a template, not a workflow");
            AssertClosureProblem(CallDef("invented-bad-root", ("x", "invented-broken")),
                new[] { new WorkflowDef { Name = "invented-broken", Error = "invented parse failure" } }, "cannot be read");

            var cycleA = CallDef("invented-cycle-a", ("to-b", "invented-cycle-b"));
            var cycleB = CallDef("invented-cycle-b", ("back", "invented-cycle-a"));
            AssertClosureProblem(cycleA, new[] { cycleB }, "loop back on themselves");

            var deep1 = CallDef("invented-deep-1", ("to2", "invented-deep-2"));
            var deep2 = CallDef("invented-deep-2", ("to3", "invented-deep-3"));
            var deep3 = CallDef("invented-deep-3", ("to4", "invented-deep-4"));
            AssertClosureProblem(deep1, new[] { deep2, deep3, CallDef("invented-deep-4") }, "deep");

            // The binding half, and it is only found because EVERY reachable definition is validated, not just
            // the root: B's hand-off to D names a question D never asks.
            var badB = CallDef("invented-bad-b", ("to-d", "invented-d"));
            badB.Steps[0].Call.With["notAQuestion"] = "invented";
            var badRoot = CallDef("invented-bad-root-2", ("to-b", "invented-bad-b"));
            AssertClosureProblem(badRoot, new[] { badB, d }, "is not a question that 'invented-d' asks");
        }

        [Fact]
        public void Public_run_construction_freezes_and_enforces_owner_step_coherence(/* T16 */)
        {
            var step = new WorkflowStep
            {
                Id = "ask", Number = 1, Title = "Ask", When = "model.measures > 0",
                Gate = new GateSpec
                {
                    Strictness = "warn",
                    Inputs = new[] { new GateInput { Name = "note", Question = "Why?", Scope = "run" } },
                    Verify = new[] { new VerifySpec { Kind = "dax_probe", PinnedShapes = new[] { "grand_total" } } },
                },
                ForEach = new ForEachSpec { InLiteral = new[] { "North" }, As = "region", MaxIterations = 4 },
                Call = new CallSpec { Workflow = "invented-t16-callee", Returns = new[] { "note" } },
            };
            var def = new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-t16", Title = "T16", Version = 3, Strictness = "warn",
                Triggers = new[] { "invented_trigger" }, Tags = new[] { "invented" }, Steps = new[] { step },
                Provenance = new Dictionary<string, string>(StringComparer.Ordinal) { ["slot_values"] = "{}" },
            };
            var run = new WorkflowRunState("wfr-t16", def, "hard");

            Assert.NotSame(def, run.Def);
            Assert.NotSame(step, run.Def.Steps[0]);
            Assert.NotSame(step.Gate, run.Def.Steps[0].Gate);
            Assert.NotSame(step.Gate.Inputs[0], run.Def.Steps[0].Gate.Inputs[0]);
            Assert.NotSame(step.Gate.Verify[0], run.Def.Steps[0].Gate.Verify[0]);
            Assert.NotSame(step.ForEach, run.Def.Steps[0].ForEach);
            Assert.NotSame(step.Call, run.Def.Steps[0].Call);
            Assert.NotSame(step.Call.With, run.Def.Steps[0].Call.With);
            Assert.NotSame(def.Provenance, run.Def.Provenance);
            Assert.NotSame(def.Triggers, run.Def.Triggers);
            Assert.Same(run.Def, RowOwner(run.Plan[0]));
            Assert.Same(run.Def.Steps[0], run.Plan[0].Step);

            // Mutate every nested container the row could read from, then prove none of it moved.
            def.Strictness = "off";
            def.Version = 99;
            def.Provenance["slot_values"] = "{\"control_totals\":\"mutated\"}";
            step.Gate.Strictness = "hard";
            step.Gate.Inputs[0].Scope = null;
            step.Gate.Verify[0].Kind = "bpa_clean";
            step.ForEach.MaxIterations = 99;
            step.When = "model.measures > 999";
            step.Call.Returns = Array.Empty<string>();
            step.Title = "Mutated";

            Assert.Equal("warn", run.Def.Strictness);
            Assert.Equal(3, run.Def.Version);
            Assert.Equal("{}", run.Def.Provenance["slot_values"]);
            Assert.Equal("warn", run.Def.Steps[0].Gate.Strictness);
            Assert.Equal("run", run.Def.Steps[0].Gate.Inputs[0].Scope);
            Assert.Equal("dax_probe", run.Def.Steps[0].Gate.Verify[0].Kind);
            Assert.Equal(4, run.Def.Steps[0].ForEach.MaxIterations);
            Assert.Equal("model.measures > 0", run.Def.Steps[0].When);
            Assert.Equal(new[] { "note" }, run.Def.Steps[0].Call.Returns);
            Assert.Equal("Ask", run.Def.Steps[0].Title);
            Assert.Equal("hard", run.SettingsStrictness);

            // Coherence: a row that mixes the frozen owner with the SOURCE step object refuses by reference.
            var mixed = AssignOwner(new PlannedStep(step, "ask", run.RunId, null), run.Def);
            var mismatch = Assert.Throws<InvalidOperationException>(() => run.RequireCoherentRow(mixed));
            Assert.Contains("did not author this step", mismatch.Message);
            var orphan = new PlannedStep(new WorkflowStep { Id = "elsewhere", Title = "Elsewhere" }, "elsewhere", run.RunId, null);
            var unowned = Assert.Throws<InvalidOperationException>(() => run.RequireCoherentRow(orphan));
            Assert.Contains("has no workflow owner", unowned.Message);
            Assert.Same(run.Def, run.RequireCoherentRow(run.Plan[0]));
        }

        [Fact]
        public void Public_store_start_freezes_once_and_internal_start_preserves_the_closure(/* T17 */)
        {
            var step = new WorkflowStep { Id = "ask", Number = 1, Title = "Ask" };
            var def = new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-t17", Version = 1, Strictness = "warn", Steps = new[] { step },
            };
            var store = new WorkflowRunStore();
            var stored = store.Start(def, "hard");
            def.Strictness = "off";
            step.Title = "Mutated";

            Assert.NotSame(def, stored.Def);
            Assert.Equal("warn", stored.Def.Strictness);
            Assert.Equal("Ask", stored.Def.Steps[0].Title);
            Assert.Equal("hard", stored.SettingsStrictness);
            Assert.Same(stored, store.Require(stored.RunId));

            // The internal start takes an ALREADY-frozen closure and must not freeze it a second time: every
            // member has to come back by reference, or no later splice could look its owner up.
            var frozenRoot = WorkflowFreeze.Freeze(def);
            var frozenCallee = WorkflowFreeze.Freeze(new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-t17-callee", Version = 1, Steps = new[] { new WorkflowStep { Id = "use", Title = "Use" } },
            });
            var closure = new WorkflowOwnerClosure(new[] { (frozenRoot, "warn"), (frozenCallee, (string)null) });
            var internalRun = store.Start(closure, "off");
            Assert.Same(frozenRoot, internalRun.Def);
            Assert.Same(frozenCallee, internalRun.OwnerClosure.Require("invented-t17-callee"));
            Assert.Same(frozenRoot.Steps[0], internalRun.Plan[0].Step);
            Assert.Same(frozenRoot, RowOwner(internalRun.Plan[0]));
            Assert.Equal("warn", internalRun.SettingsStrictness);
            Assert.Equal("off", internalRun.GlobalStrictness);
        }

        [Fact]
        public void Session_replacement_preserves_rather_than_retargets_the_run_owners(/* T18 */)
        {
            var caller = CallDef("invented-t18-caller", ("to-callee", "invented-t18-callee"));
            var callee = CallDef("invented-t18-callee");
            var run = WorkflowOwnerFixtures.ClosureRun("wfr-t18", null, (caller, "warn"), (callee, null));
            var frozenRoot = run.Def;
            var frozenCallee = run.OwnerClosure.Require("invented-t18-callee");

            var store = new WorkflowRunStore();
            var before = new SessionContext(null, store, null);
            // The commit seam constructs the NEXT context with the EXISTING run store, which is the whole
            // mechanism: the run and its run-owned frozen closure ride across a model replacement untouched.
            var after = new SessionContext(null, before.WorkflowRuns, null);
            Assert.Same(before.WorkflowRuns, after.WorkflowRuns);

            // A same-named definition in the replacement session cannot retarget an existing owner.
            var replacement = WorkflowFreeze.Freeze(new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-t18-callee", Version = 99, Strictness = "hard",
                Steps = new[] { new WorkflowStep { Id = "different", Title = "Different" } },
            });
            Assert.NotSame(replacement, run.OwnerClosure.Require("invented-t18-callee"));
            Assert.Same(frozenCallee, run.OwnerClosure.Require("invented-t18-callee"));
            Assert.Same(frozenRoot, run.Def);
            Assert.Same(frozenRoot, RowOwner(run.Plan[0]));
            Assert.Same(frozenRoot, run.RequireCoherentRow(run.Plan[0]));
        }

        [Fact]
        public async Task Visible_forEach_source_resolution_follows_the_winning_rows_owner(/* T19 */)
        {
            // Same-named gate input on caller and callee. The caller declares it `required`, the callee
            // `optional`, and only the callee's declaration may permit a blank list on a callee loop.
            var declare = new WorkflowStep
            {
                Id = "declare", Number = 1, Title = "Declare",
                Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Question = "Which?", Required = "required" } } },
            };
            var caller = new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-t19-caller", Version = 1, Strictness = "off",
                Steps = new[] { declare, new WorkflowStep { Id = "prove-it", Number = 2, Title = "Prove it" } },
            };
            var calleeDeclare = new WorkflowStep
            {
                Id = "callee-declare", Number = 1, Title = "Callee declare",
                Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Question = "Which?", Required = "optional" } } },
            };
            var calleeLoop = new WorkflowStep
            {
                Id = "callee-loop", Number = 2, Title = "Callee loop",
                ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 4 },
            };
            var callee = new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-t19-callee", Version = 1, Strictness = "off",
                Steps = new[] { calleeDeclare, calleeLoop },
            };
            var run = WorkflowOwnerFixtures.ClosureRun("wfr-t19", null, (caller, null), (callee, null));

            run.Plan[1] = WorkflowOwnerFixtures.CalleeRow(run, callee.Name, 0, "prove-it/callee-declare", run.RunId);
            run.Results[1].StepId = "prove-it/callee-declare";
            run.Plan.Add(WorkflowOwnerFixtures.CalleeRow(run, callee.Name, 1, "prove-it/callee-loop", run.RunId));
            run.Results.Add(new StepResult { StepId = "prove-it/callee-loop", Title = "Callee loop", Status = "pending" });
            run.Results[0].Status = "passed";
            run.Results[0].Answers["regions"] = new AnswerValue { Value = "North" };
            run.Results[1].Status = "passed";
            run.Results[1].Answers["regions"] = new AnswerValue { Value = "  " };
            run.StepIndex = 2;
            run.Results[2].Status = "in_progress";

            // The nearest VISIBLE answering row is the callee's own declaration, and its owner declares the
            // source `optional`, so a blank spells an empty loop. Binding the same blank through the CALLER's
            // same-named `required` declaration would refuse instead.
            await WorkflowRunner.SubmitStepAsync(run, "prove-it/callee-loop", null, null);
            Assert.Equal("not_applicable", run.Results[2].Status);
            Assert.Equal("did not apply: the forEach value list was empty.", run.Results[2].Note);
            Assert.True(run.Plan[2].ForEachExpanded);
            Assert.Same(run.OwnerClosure.Require(callee.Name), RowOwner(run.Plan[2]));

            // Same arrangement, but the winning visible answer is the CALLER's row, whose declaration is
            // `required`: the refusal quotes the caller's own question, never the callee's.
            var callerWins = WorkflowOwnerFixtures.ClosureRun("wfr-t19-caller-wins", null, (caller, null), (callee, null));
            callerWins.Plan[1] = WorkflowOwnerFixtures.CalleeRow(callerWins, callee.Name, 1, "prove-it/callee-loop", callerWins.RunId);
            callerWins.Results[1].StepId = "prove-it/callee-loop";
            callerWins.Results[1].Title = "Callee loop";
            callerWins.Results[0].Status = "passed";
            callerWins.Results[0].Answers["regions"] = new AnswerValue { Value = "  " };
            callerWins.StepIndex = 1;
            callerWins.Results[1].Status = "in_progress";

            var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WorkflowRunner.SubmitStepAsync(callerWins, "prove-it/callee-loop", null, null));
            Assert.Contains("(required: it cannot be declined)", refused.Message, StringComparison.Ordinal);
            Assert.Equal("in_progress", callerWins.Results[1].Status);

            // The declarer named in refusal copy comes from the CURRENT row's own owner, so a same-named caller
            // step cannot be named for a callee row.
            var gapRun = WorkflowOwnerFixtures.ClosureRun("wfr-t19-gap", null, (caller, null), (callee, null));
            gapRun.Plan[1] = WorkflowOwnerFixtures.CalleeRow(gapRun, callee.Name, 0, "prove-it/callee-declare", gapRun.RunId);
            gapRun.Results[1].StepId = "prove-it/callee-declare";
            gapRun.Plan.Add(WorkflowOwnerFixtures.CalleeRow(gapRun, callee.Name, 1, "prove-it/callee-loop", gapRun.RunId));
            gapRun.Results.Add(new StepResult { StepId = "prove-it/callee-loop", Title = "Callee loop", Status = "in_progress" });
            gapRun.StepIndex = 2;
            var gap = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCurrentForEachValues(gapRun));
            Assert.Contains("declared on step 'callee-declare'", gap.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("step 'declare'", gap.Message, StringComparison.Ordinal);

            // A row whose owner did not author its step refuses before any declaration is read.
            gapRun.Plan[1] = AssignOwner(new PlannedStep(gapRun.Def.Steps[0], "prove-it/callee-declare", gapRun.RunId, null),
                gapRun.OwnerClosure.Require(callee.Name));
            // A null-valued answer keeps the row in the projection while sending resolution down the gap path,
            // which is the branch that binds a declaration through the answering row's owner.
            gapRun.Results[1].Answers["regions"] = new AnswerValue { Value = null };
            var mixed = Assert.Throws<InvalidOperationException>(
                () => WorkflowRunner.ResolveCurrentForEachValues(gapRun));
            Assert.Contains("did not author this step", mixed.Message, StringComparison.Ordinal);
        }

        /// <summary>A definition whose steps are `call:` hand-offs, one per named callee.</summary>
        private static WorkflowDef CallDef(string name, params (string StepId, string Callee)[] calls) => new WorkflowDef
        {
            SchemaVersion = 2,
            Name = name,
            Version = 1,
            Strictness = "off",
            Steps = calls.Select((c, i) => new WorkflowStep
            {
                Id = c.StepId, Number = i + 1, Title = c.StepId,
                Call = new CallSpec { Workflow = c.Callee },
            }).ToArray(),
        };

        private static void AssertClosureProblem(WorkflowDef root, IReadOnlyList<WorkflowDef> others, string expected)
        {
            var library = new[] { root }.Concat(others).ToArray();
            var templates = others.Where(o => string.Equals(o.Kind, "template", StringComparison.Ordinal)).ToArray();
            WorkflowParser.ReachableClosure(root, library, templates, out var problems);
            Assert.NotEmpty(problems);
            Assert.Contains(problems, p => p.Message.Contains(expected, StringComparison.Ordinal));
        }

        /// <summary>A frozen copy of a definition, taken the only way a caller can take one: by starting a
        /// run over it and reading the run's own root. Nothing here reaches a freeze helper directly, so the
        /// pack compiles against a tree that has none.</summary>
        private static WorkflowDef FrozenCopyOf(WorkflowDef def)
        {
            var frozen = new WorkflowRunState("wfr-frozen-" + def.Name, def, null).Def;
            Assert.NotNull(frozen);
            return frozen;
        }

        private static WorkflowDef RowOwner(PlannedStep planned)
        {
            var property = typeof(PlannedStep).GetProperty(
                "Owner", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.True(property != null, "PlannedStep must carry a mandatory internal Owner.");
            Assert.Equal(typeof(WorkflowDef), property.PropertyType);
            var owner = property.GetValue(planned) as WorkflowDef;
            Assert.True(owner != null, "A planned row Owner cannot be null.");
            return owner;
        }

        private static PlannedStep AssignOwner(PlannedStep planned, WorkflowDef owner)
        {
            var property = typeof(PlannedStep).GetProperty(
                "Owner", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.True(property != null, "PlannedStep must carry a mandatory internal Owner.");
            if (property.CanWrite)
            {
                property.SetValue(planned, owner);
            }
            else
            {
                var backing = typeof(PlannedStep).GetField(
                    "<Owner>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.True(backing != null, "PlannedStep.Owner must be settable so a callee row can own a different workflow.");
                backing.SetValue(planned, owner);
            }
            Assert.Same(owner, property.GetValue(planned));
            return planned;
        }
    }

    /// <summary>[T220] Fixture support for a row a test SPLICES into a run plan for a step the run's own
    /// definition never authored — a call body, a repointed row, an injected loop. Every run plan row is owned
    /// by the frozen definition that authored its exact step, so such a fixture has to bring that step's owner
    /// with it instead of leaving the row to be read against the caller's frontmatter.</summary>
    internal static class WorkflowOwnerFixtures
    {
        /// <summary>A frozen definition over <paramref name="steps"/>, taken the only way a caller can take
        /// one: by starting a run over it and reading that run's own root.</summary>
        internal static WorkflowDef FrozenOwner(string name, string strictness, params WorkflowStep[] steps) =>
            new WorkflowRunState("wfr-frozen-" + name, new WorkflowDef
            {
                SchemaVersion = 2,
                Name = name,
                Version = 1,
                Strictness = strictness,
                Steps = steps,
            }, null).Def;

        /// <summary>One planned row owned by a fresh frozen definition holding just this step. The row's
        /// <c>Step</c> is the FROZEN copy, so a caller that needs to compare identity reads it back off the row.</summary>
        internal static PlannedStep ForeignRow(WorkflowStep step, string instanceId, string frameId,
            int? iterationIndex = null, string ownerName = null, string strictness = null)
        {
            var owner = FrozenOwner(ownerName ?? ("invented-owner-" + instanceId.Replace('/', '-').Replace('#', '-')),
                strictness, step);
            return new PlannedStep(owner.Steps[0], instanceId, frameId, iterationIndex, owner);
        }

        /// <summary>A run over a MULTI-DEFINITION frozen closure — the shape production builds at start, and
        /// the only shape in which a foreign-owner row is a genuine closure member.</summary>
        internal static WorkflowRunState ClosureRun(string runId, string globalStrictness,
            params (WorkflowDef Def, string Settings)[] members) =>
            new WorkflowRunState(runId,
                new WorkflowOwnerClosure(members.Select(m => (WorkflowFreeze.Freeze(m.Def), m.Settings)).ToArray()),
                globalStrictness);

        /// <summary>One row owned by a reachable callee of <paramref name="run"/>, resolved from the run's own
        /// frozen closure the way a call splice will: <c>Require</c> the name, then take the step from that
        /// frozen owner.</summary>
        internal static PlannedStep CalleeRow(WorkflowRunState run, string calleeName, int stepIndex,
            string instanceId, string frameId, int? iterationIndex = null)
        {
            var owner = run.OwnerClosure.Require(calleeName);
            return new PlannedStep(owner.Steps[stepIndex], instanceId, frameId, iterationIndex, owner);
        }

        /// <summary>Several rows over ONE frozen owner, so same-owner fixtures (a two-step call body, a loop's
        /// iterations) keep a single owner object rather than one per row.</summary>
        internal static PlannedStep[] ForeignRows(WorkflowDef frozenOwner,
            params (string InstanceId, string FrameId, int? IterationIndex)[] rows)
        {
            var built = new PlannedStep[rows.Length];
            for (var i = 0; i < rows.Length; i++)
                built[i] = new PlannedStep(frozenOwner.Steps[i], rows[i].InstanceId, rows[i].FrameId,
                    rows[i].IterationIndex, frozenOwner);
            return built;
        }
    }
}
