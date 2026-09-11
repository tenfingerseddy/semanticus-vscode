using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Evidence;
using EvidenceVerdict = Semanticus.Engine.Evidence.Verdict;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class WorkflowStepConditionTests
    {
        private static VerifyResult Passed(VerifySpec spec) =>
            new VerifyResult { Kind = spec.Kind, Status = "passed", Detail = "invented fixture proof" };

        private static WorkflowStep Step(string id, string when = null, bool hardGate = false) => new WorkflowStep
        {
            Id = id,
            Number = int.Parse(id.Substring("step-".Length)),
            Title = id,
            When = when,
            Gate = hardGate ? new GateSpec
            {
                Strictness = "hard",
                Verify = new[] { new VerifySpec { Kind = "workflow_admissible" } },
            } : null,
        };

        [Fact]
        public async Task False_conditions_record_facts_and_advance_across_consecutive_steps()
        {
            var first = Step("step-1");
            first.Gate = new GateSpec
            {
                Inputs = new[] { new GateInput { Name = "optionalThing", Question = "Invented answer?", Required = "optional" } },
            };
            var def = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "conditional-run",
                Steps = new[]
                {
                    first,
                    Step("step-2", "inputs.optionalThing.answered"),
                    Step("step-3", "inputs.optionalThing.declined"),
                    Step("step-4", "inputs.optionalThing.value == 'invented'"),
                    Step("step-5"),
                },
            };
            var run = new WorkflowRunState("wfr-condition", def, null);

            await WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>(),
                (spec, step, state, answers) => Task.FromResult(Passed(spec)));

            Assert.Equal("step-5", run.CurrentStep.Id);
            Assert.Equal("not_applicable", run.Results[1].Status);
            Assert.Equal("not_applicable", run.Results[2].Status);
            Assert.Equal("not_applicable", run.Results[3].Status);
            Assert.Contains("inputs.optionalThing.answered", run.Results[1].Note);
            Assert.Contains("inputs.optionalThing.answered=false", run.Results[1].Note);
            Assert.Contains("inputs.optionalThing.declined=false", run.Results[2].Note);
            Assert.Contains("inputs.optionalThing.value=unknown", run.Results[3].Note);
        }

        [Fact]
        public void Malformed_condition_warns_and_the_guarded_step_does_not_run()
        {
            var def = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "malformed-condition",
                Steps = new[] { Step("step-1", "model.tableCount >"), Step("step-2") },
            };

            var warning = Assert.Single(WorkflowParser.V2Findings(def, new[] { def }, Array.Empty<WorkflowDef>())
                .Where(x => x.Severity == "warn"));
            Assert.Contains("could not read the condition", warning.Message);

            var run = new WorkflowRunState("wfr-malformed", def, null);
            WorkflowRunner.AdvancePastInapplicableSteps(run);

            Assert.Equal("not_applicable", run.Results[0].Status);
            Assert.Equal("step-2", run.CurrentStep.Id);
            Assert.Contains("condition 'model.tableCount >' was unreadable", run.Results[0].Note);
        }

        [Fact]
        public void Unknown_fact_warns_in_check_and_is_false_at_run_time()
        {
            var def = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "unknown-condition",
                Steps = new[] { Step("step-1", "model.notARealFact == true"), Step("step-2") },
            };

            var warning = Assert.Single(WorkflowParser.V2Findings(def, new[] { def }, Array.Empty<WorkflowDef>())
                .Where(x => x.Severity == "warn"));
            Assert.Contains("model.notARealFact", warning.Message);

            var run = new WorkflowRunState("wfr-unknown", def, null);
            WorkflowRunner.AdvancePastInapplicableSteps(run);

            Assert.Equal("not_applicable", run.Results[0].Status);
            Assert.Equal("step-2", run.CurrentStep.Id);
            Assert.Contains("model.notARealFact=unknown", run.Results[0].Note);
        }

        [Fact]
        public void Expanded_iteration_conditions_read_the_current_frames_item_and_zero_based_index()
        {
            var run = ExpandedConditionalLoop("loop.item == 'North' && loop.index == 0");
            run.FrameFacts = StartFactsWithInjectedLoopValue("stale");

            WorkflowRunner.AdvancePastInapplicableSteps(run);

            Assert.Equal(0, run.StepIndex);
            Assert.Equal("in_progress", run.Results[0].Status);
            Assert.Null(run.Results[0].Note);
        }

        [Fact]
        public void False_first_iteration_records_its_own_loop_facts_then_advances_to_matching_second()
        {
            var run = ExpandedConditionalLoop("loop.item == 'South' && loop.index == 1");

            WorkflowRunner.AdvancePastInapplicableSteps(run);

            Assert.Equal(1, run.StepIndex);
            Assert.Equal("not_applicable", run.Results[0].Status);
            Assert.Contains("loop.item=North", run.Results[0].Note);
            Assert.Contains("loop.index=0", run.Results[0].Note);
            Assert.Equal("in_progress", run.Results[1].Status);
            Assert.Null(run.Results[1].Note);
        }

        [Fact]
        public void Non_loop_steps_cannot_see_injected_loop_facts()
        {
            var def = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "non-loop-condition",
                Steps = new[] { Step("step-1", "loop.index == 7 || loop.item == 'injected'"), Step("step-2") },
            };
            var run = new WorkflowRunState("wfr-non-loop-facts", def, null)
            {
                FrameFacts = StartFactsWithInjectedLoopValue("injected"),
            };

            WorkflowRunner.AdvancePastInapplicableSteps(run);

            Assert.Equal(1, run.StepIndex);
            Assert.Equal("not_applicable", run.Results[0].Status);
            Assert.Contains("loop.index=unknown", run.Results[0].Note);
            Assert.Contains("loop.item=unknown", run.Results[0].Note);
        }

        [Theory]
        [InlineData("planned-index")]
        [InlineData("missing-planned-index")]
        [InlineData("authored-step")]
        [InlineData("loop-variable")]
        [InlineData("frame-kind")]
        [InlineData("null-loop-value")]
        [InlineData("result-row")]
        public void Incoherent_iteration_plan_and_frame_metadata_refuses_atomically_on_both_doors(string mismatch)
        {
            var run = ExpandedConditionalLoop("loop.item == 'North'");
            CorruptCurrentIteration(run, mismatch);

            var beforeIndex = run.StepIndex;
            var beforeStatuses = run.Results.Select(x => x.Status).ToArray();
            var beforeNotes = run.Results.Select(x => x.Note).ToArray();

            var conditionError = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.AdvancePastInapplicableSteps(run));
            var viewError = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.BuildView(run));

            Assert.Contains("condition", conditionError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("render", viewError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(beforeIndex, run.StepIndex);
            Assert.Equal(beforeStatuses, run.Results.Select(x => x.Status));
            Assert.Equal(beforeNotes, run.Results.Select(x => x.Note));
        }

        [Fact]
        public void Current_iteration_instructions_render_the_first_and_second_exact_frame_values()
        {
            var run = ExpandedConditionalLoop("loop.index >= 0",
                "Pass [[ loop.index ]] reviews [[loop.item ]].");

            var first = WorkflowRunner.BuildView(run);
            Assert.Equal("Pass 0 reviews North.", first.CurrentStep.Instructions);
            Assert.Equal("step-1#0", first.CurrentStep.StepId);

            run.Results[0].Status = "passed";
            run.Results[1].Status = "in_progress";
            run.StepIndex = 1;
            var second = WorkflowRunner.BuildView(run);

            Assert.Equal("Pass 1 reviews South.", second.CurrentStep.Instructions);
            Assert.Equal("step-1#1", second.CurrentStep.StepId);
        }

        [Fact]
        public async Task Exact_iteration_identity_is_required_and_each_success_teaches_the_next_identity()
        {
            var run = ExpandedConditionalLoop("loop.index >= 0");
            var authored = run.Def.Steps[0];

            Assert.Equal("step-1#0", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            await AssertSubmissionRefusesAtomically(run, "step-1");
            await AssertSubmissionRefusesAtomically(run, "step-1#1");
            await AssertSubmissionRefusesAtomically(run, "");
            AssertSkipRefusesAtomically(run, "step-1");
            AssertSkipRefusesAtomically(run, "step-1#1");
            AssertSkipRefusesAtomically(run, "");

            await WorkflowRunner.SubmitStepAsync(run, "step-1#0", null, null);

            Assert.Equal(1, run.StepIndex);
            Assert.Equal("step-1#1", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            await AssertSubmissionRefusesAtomically(run, "step-1#0");
            AssertSkipRefusesAtomically(run, "step-1#0");
            WorkflowRunner.SkipStep(run, "step-1#1", "invented second-iteration override");

            Assert.Equal("completed", run.Status);
            Assert.Equal("step-1", authored.Id);
            Assert.Same(authored, run.Plan[0].Step);
            Assert.Same(authored, run.Plan[1].Step);
        }

        [Theory]
        [InlineData("submit", "planned-index")]
        [InlineData("submit", "missing-planned-index")]
        [InlineData("submit", "authored-step")]
        [InlineData("submit", "loop-variable")]
        [InlineData("submit", "frame-kind")]
        [InlineData("submit", "null-loop-value")]
        [InlineData("submit", "result-row")]
        [InlineData("skip", "planned-index")]
        [InlineData("skip", "missing-planned-index")]
        [InlineData("skip", "authored-step")]
        [InlineData("skip", "loop-variable")]
        [InlineData("skip", "frame-kind")]
        [InlineData("skip", "null-loop-value")]
        [InlineData("skip", "result-row")]
        public async Task Iteration_submit_and_skip_share_every_coherence_refusal_before_mutation(
            string operation, string mismatch)
        {
            var run = ExpandedConditionalLoop("loop.index >= 0");
            CorruptCurrentIteration(run, mismatch);

            var error = operation == "submit"
                ? await AssertSubmissionRefusesAtomically(run, "step-1#0")
                : AssertSkipRefusesAtomically(run, "step-1#0");
            Assert.Contains("cannot continue", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Ordinary_step_teaches_and_accepts_its_authored_identity_and_keeps_legacy_blank_addressing()
        {
            var step = Step("step-1");
            var run = new WorkflowRunState("wfr-linear-identity", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "linear-identity",
                Steps = new[] { step },
            }, null);

            Assert.Equal("step-1", WorkflowRunner.BuildView(run).CurrentStep.StepId);
            await WorkflowRunner.SubmitStepAsync(run, "", null, null);

            Assert.Equal("completed", run.Status);
            Assert.Equal("step-1", step.Id);
        }

        [Fact]
        public void Rendering_is_literal_single_pass_and_keeps_authored_content_immutable()
        {
            const string instructions = "Index=[[loop.index]]; value=[[loop.item]]; again=[[loop.item]]";
            var exactValue = "$1\\invented\n[[loop.index]] and [[loop.item]]";
            var run = ExpandedConditionalLoop("loop.index >= 0", instructions, exactValue, "unused");
            var authored = run.Def.Steps[0];
            authored.Title = "Title [[loop.item]]";
            authored.Gate = new GateSpec
            {
                Inputs = new[] { new GateInput { Name = "q", Question = "Question [[loop.item]]?", Required = "optional" } },
            };
            authored.Ops = new[] { "op-[[loop.item]]" };

            var view = WorkflowRunner.BuildView(run);

            Assert.Equal("Index=0; value=" + exactValue + "; again=" + exactValue, view.CurrentStep.Instructions);
            Assert.Equal(instructions, authored.Instructions);
            Assert.Equal("Title [[loop.item]]", authored.Title);
            Assert.Equal("Title [[loop.item]]", view.CurrentStep.Title);
            Assert.Equal("Question [[loop.item]]?", Assert.Single(view.CurrentStep.Questions).Question);
            Assert.Equal("op-[[loop.item]]", Assert.Single(view.CurrentStep.Ops));
        }

        [Fact]
        public void An_unbound_well_shaped_loop_reference_refuses_before_mutation()
        {
            var run = ExpandedConditionalLoop("loop.index >= 0", "Review [[loop.other]].");
            var beforeIndex = run.StepIndex;
            var beforeStatuses = run.Results.Select(x => x.Status).ToArray();
            var beforeInstructions = run.CurrentStep.Instructions;

            var error = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.BuildView(run));

            Assert.Contains("loop.other", error.Message);
            Assert.Contains("not bound", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(beforeIndex, run.StepIndex);
            Assert.Equal(beforeStatuses, run.Results.Select(x => x.Status));
            Assert.Equal(beforeInstructions, run.CurrentStep.Instructions);
        }

        [Fact]
        public void Non_iteration_instructions_are_preserved_byte_for_byte()
        {
            const string instructions = "Keep [[loop.index]], [[loop.item]], [[ loop.item ]], $1, \\ and\nline two exactly.";
            var step = Step("step-1");
            step.Instructions = instructions;
            var run = new WorkflowRunState("wfr-linear-render", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "linear-render",
                Steps = new[] { step },
            }, null);

            var view = WorkflowRunner.BuildView(run);

            Assert.Equal(instructions, view.CurrentStep.Instructions);
            Assert.Equal(instructions, step.Instructions);
        }

        [Fact]
        public async Task Existing_input_and_start_facts_remain_available_beside_loop_facts()
        {
            var loop = LoopStep("model.hasRls == true && connection.kind == 'offline' && git.dirty == true && session.planLoaded == true && date.dayOfMonth == 28 && inputs.flag.value == 'ready' && loop.item == 'North'");
            loop.Gate = new GateSpec
            {
                Inputs = new[] { new GateInput { Name = "flag", Question = "Invented flag?", Required = "required" } },
            };
            var def = new WorkflowDef { SchemaVersion = 2, Name = "preserved-condition-facts", Steps = new[] { loop } };
            var run = new WorkflowRunState("wfr-preserved-condition-facts", def, null)
            {
                FrameFacts = new PredicateFacts
                {
                    Model = new ModelFacts { HasRls = true },
                    Connection = new ConnectionFacts { Kind = "offline" },
                    Git = new GitFacts { Dirty = true },
                    Session = new SessionFacts { PlanLoaded = true },
                    Date = new DateFacts { Iso = "2026-08-28", DayOfMonth = 28 },
                },
            };
            run.Results[0].Answers["flag"] = new AnswerValue { Value = "ready" };
            WorkflowRunner.ExpandCurrentForEach(run, new[] { "North" });

            WorkflowRunner.AdvancePastInapplicableSteps(run);

            Assert.Equal(0, run.StepIndex);
            Assert.Equal("in_progress", run.Results[0].Status);
            await WorkflowRunner.SubmitStepAsync(run, "step-1#0", new Dictionary<string, AnswerValue>
            {
                ["flag"] = new AnswerValue { Value = "ready" },
            }, (spec, step, state, answers) => Task.FromResult(Passed(spec)));
            Assert.Equal("completed", run.Status);
        }

        [Fact]
        public async Task Not_applicable_hard_gate_is_excluded_from_certificate_population()
        {
            var def = CertificateDefinition();
            var run = new WorkflowRunState("wfr-certificate-condition", def, null)
            {
                CoverageSurface = new CoverageSurfaceLock
                {
                    CurrentGrid = Array.Empty<string>(),
                    CurrentOpenGrains = Array.Empty<string>(),
                },
            };
            var executor = new WorkflowVerifyExecutor((spec, step, state, answers) => Task.FromResult(Passed(spec)));

            await WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>
            {
                ["certificate"] = new AnswerValue { Value = "FULL" },
            }, executor);
            Assert.Equal("not_applicable", run.Results[1].Status);
            await WorkflowRunner.SubmitStepAsync(run, "step-3", new Dictionary<string, AnswerValue>(), executor);

            Assert.Equal("completed", run.Status);
            Assert.Equal("FULL", run.Certificate.ComputedLevel);
            Assert.Equal("FULL", run.Certificate.Level);
            Assert.Empty(run.Certificate.SkippedSteps);
        }

        [Fact]
        public async Task Evidence_keeps_not_applicable_row_but_excludes_it_from_grade_and_counts()
        {
            var def = CertificateDefinition();
            var run = new WorkflowRunState("wfr-evidence-condition", def, null);
            var executor = new WorkflowVerifyExecutor((spec, step, state, answers) => Task.FromResult(Passed(spec)));

            await WorkflowRunner.SubmitStepAsync(run, "step-1", new Dictionary<string, AnswerValue>
            {
                ["certificate"] = new AnswerValue { Value = "FULL" },
            }, executor);
            await WorkflowRunner.SubmitStepAsync(run, "step-3", new Dictionary<string, AnswerValue>(), executor);

            var doc = WorkflowEvidenceRenderer.Build(run);
            doc.Validate();
            var steps = Assert.IsType<StepsSection>(doc.Sections.Single(x => x is StepsSection));
            var row = steps.Steps[1];

            Assert.Equal(5, Verdicts.Words.Length);
            Assert.Equal(EvidenceVerdict.Unknown, row.Verdict);
            Assert.Contains("did not apply", row.Note, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("inputs.optionalThing.answered", row.Note);
            Assert.Equal(2, doc.Coverage.Total);
            Assert.Equal(2, doc.Coverage.Verified);
            Assert.Equal(0, doc.Coverage.Unknowns);
            Assert.Equal(0, doc.VerdictCounts[EvidenceVerdict.Unknown.ToString()]);
            Assert.Equal(EvidenceVerdict.Verified, doc.Verdict);
        }

        [Fact]
        public async Task Loop_entry_current_row_condition_walk_uses_each_iteration_binding()
        {
            var run = new WorkflowRunState("wfr-u18-condition-walk", new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-loop-entry-when",
                Version = 2,
                Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "choose", Number = 1, Title = "Choose",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
                    },
                    new WorkflowStep { Id = "middle", Number = 2, Title = "Middle" },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 3, Title = "Review",
                        Instructions = "Authored body: review [[loop.region]] at [[loop.index]].",
                        When = "loop.region == 'South' && loop.index == 1",
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                    },
                },
            }, null);

            await WorkflowRunner.SubmitStepAsync(run, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(run, "middle", null, null);
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

            Assert.Equal("not_applicable", run.Results[2].Status);
            Assert.Contains("loop.region=North", run.Results[2].Note);
            Assert.Contains("loop.index=0", run.Results[2].Note);
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

            var mixed = new WorkflowRunState("wfr-u18-condition-mixed", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-loop-entry-mixed", Version = 2, Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "choose", Number = 1, Title = "Choose",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
                    },
                    new WorkflowStep { Id = "middle", Number = 2, Title = "Middle" },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 3, Title = "Review",
                        Instructions = "Authored body: review [[loop.region]] at [[loop.index]].",
                        When = "inputs.regions.answered && loop.region == 'South'",
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                    },
                    new WorkflowStep { Id = "after", Number = 4, Title = "After", Instructions = "Authored after body." },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(mixed, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(mixed, "middle", null, null);
            await WorkflowRunner.SubmitStepAsync(mixed, "review-region", null, null);
            Assert.Equal("not_applicable", mixed.Results[2].Status);
            Assert.Contains("loop.region=North", mixed.Results[2].Note);
            Assert.Equal("review-region#1", mixed.Plan[mixed.StepIndex].InstanceId);
            Assert.Equal("in_progress", mixed.Results[3].Status);

            var traps = new WorkflowRunState("wfr-u18-condition-traps", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-loop-entry-traps", Version = 2, Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "choose", Number = 1, Title = "Choose",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
                    },
                    new WorkflowStep { Id = "middle", Number = 2, Title = "Middle" },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 3, Title = "Review",
                        When = "inputs.team.value == 'loop.index'",
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                    },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(traps, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(traps, "middle", null, null);
            await WorkflowRunner.SubmitStepAsync(traps, "review-region", null, null);
            Assert.Equal("not_applicable", traps.Results[2].Status);
            Assert.False(traps.Plan[2].ForEachExpanded);

            var semantic = new WorkflowRunState("wfr-u18-condition-semantic", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-loop-entry-semantic", Version = 2, Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "choose", Number = 1, Title = "Choose",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
                    },
                    new WorkflowStep { Id = "middle", Number = 2, Title = "Middle" },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 3, Title = "Review",
                        When = "loop.index ~ 'x'",
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                    },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(semantic, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(semantic, "middle", null, null);
            await WorkflowRunner.SubmitStepAsync(semantic, "review-region", null, null);
            Assert.Equal(new[] { "choose", "middle", "review-region#0", "review-region#1" }, semantic.Plan.Select(p => p.InstanceId));

            var excluded = new WorkflowRunState("wfr-u18-condition-all", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-loop-entry-all", Version = 2, Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "choose", Number = 1, Title = "Choose",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
                    },
                    new WorkflowStep { Id = "middle", Number = 2, Title = "Middle" },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 3, Title = "Review",
                        When = "loop.region == 'East'",
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                    },
                    new WorkflowStep { Id = "after", Number = 4, Title = "After", Instructions = "Authored after body." },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(excluded, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(excluded, "middle", null, null);
            await WorkflowRunner.SubmitStepAsync(excluded, "review-region", null, null);
            Assert.Equal("not_applicable", excluded.Results[2].Status);
            Assert.Equal("not_applicable", excluded.Results[3].Status);
            Assert.Equal("after", WorkflowRunner.BuildView(excluded).CurrentStep.StepId);
            Assert.Equal("Authored after body.", WorkflowRunner.BuildView(excluded).CurrentStep.Instructions);
        }

        [Fact]
        public async Task Setup_condition_walk_is_the_same_for_inline_self_sourced_and_deferred_setup()
        {
            async Task AssertWalk(WorkflowRunState run, string setupId, Dictionary<string, AnswerValue> answers, string firstApplicable)
            {
                var before = WorkflowRunner.BuildView(run);
                Assert.NotNull(before.CurrentStep);
                Assert.Equal(setupId, before.CurrentStep.StepId);
                Assert.NotEqual("not_applicable", run.Results[run.StepIndex].Status);
                Assert.True(string.IsNullOrEmpty(run.Results[run.StepIndex].Note));
                WorkflowRunner.AdvancePastInapplicableSteps(run);
                var waiting = WorkflowRunner.BuildView(run);
                Assert.NotNull(waiting.CurrentStep);
                Assert.Equal(setupId, waiting.CurrentStep.StepId);
                await WorkflowRunner.SubmitStepAsync(run, setupId, answers, null);
                Assert.Equal(firstApplicable, run.Plan[run.StepIndex].InstanceId);
                Assert.Contains("loop.", run.Results.First(r => r.Status == "not_applicable").Note);
                Assert.All(run.Frames.Where(f => f.ProjectionKind == "iteration" && f.State == "passed"), f => Assert.Empty(f.WitnessLocks));
                Assert.Null(run.Frames[0].State);
                Assert.All(run.Results, result => Assert.Empty(result.VerifyHistory));
            }

            var inline = new WorkflowRunState("wfr-shared-walk-inline", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-shared-walk-inline", Version = 2, Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "inline-loop", Number = 1, Title = "Inline",
                        When = "loop.item == 'South'",
                        ForEach = new ForEachSpec { InLiteral = new[] { "North", "South" }, As = "item", MaxIterations = 9 },
                    },
                },
            }, null);
            await AssertWalk(inline, "inline-loop", null, "inline-loop#1");

            var self = new WorkflowRunState("wfr-shared-walk-self", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-shared-walk-self", Version = 2, Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 1, Title = "Review",
                        When = "loop.region == 'South'",
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
                    },
                },
            }, null);
            await AssertWalk(self, "review-region", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, "review-region#1");

            var deferred = new WorkflowRunState("wfr-shared-walk-deferred", new WorkflowDef
            {
                SchemaVersion = 2, Name = "invented-shared-walk-deferred", Version = 2, Strictness = "off",
                Steps = new[]
                {
                    new WorkflowStep
                    {
                        Id = "choose", Number = 1, Title = "Choose",
                        Gate = new GateSpec { Inputs = new[] { new GateInput { Name = "regions", Required = "optional" } } },
                    },
                    new WorkflowStep { Id = "middle", Number = 2, Title = "Middle" },
                    new WorkflowStep
                    {
                        Id = "review-region", Number = 3, Title = "Review",
                        When = "loop.region == 'South'",
                        ForEach = new ForEachSpec { InInput = "regions", As = "region", MaxIterations = 9 },
                    },
                },
            }, null);
            await WorkflowRunner.SubmitStepAsync(deferred, "choose", new Dictionary<string, AnswerValue>
            {
                ["regions"] = new AnswerValue { Value = "North, South" },
            }, null);
            await WorkflowRunner.SubmitStepAsync(deferred, "middle", null, null);
            await AssertWalk(deferred, "review-region", null, "review-region#1");
        }

        private static WorkflowStep LoopStep(string when)
        {
            var step = Step("step-1", when);
            step.ForEach = new ForEachSpec
            {
                InLiteral = new[] { "North", "South" },
                As = "item",
                MaxIterations = 25,
            };
            return step;
        }

        private static WorkflowRunState ExpandedConditionalLoop(string when, string? instructions = null,
            params string[] values)
        {
            var step = LoopStep(when);
            step.Instructions = instructions!;
            var def = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "expanded-conditional-loop",
                Steps = new[] { step },
            };
            var run = new WorkflowRunState("wfr-expanded-condition", def, null);
            WorkflowRunner.ExpandCurrentForEach(run, values.Length == 0 ? new[] { "North", "South" } : values);
            return run;
        }

        private static void CorruptCurrentIteration(WorkflowRunState run, string mismatch)
        {
            var planned = run.Plan[0];
            var frame = run.Frames.Single(x => x.FrameId == planned.FrameId);
            if (mismatch == "planned-index")
                run.Plan[0] = new PlannedStep(planned.Step, planned.InstanceId, planned.FrameId, 1, true);
            else if (mismatch == "missing-planned-index")
                run.Plan[0] = new PlannedStep(planned.Step, planned.InstanceId, planned.FrameId, null, true);
            else if (mismatch == "authored-step")
                ReplaceCurrentFrame(run, RunFrame.CreateIteration(frame.FrameId, "step-99", 0, "item", "North", "in_progress"));
            else if (mismatch == "loop-variable")
                ReplaceCurrentFrame(run, RunFrame.CreateIteration(frame.FrameId, "step-1", 0, "other", "North", "in_progress"));
            else if (mismatch == "frame-kind")
                ReplaceCurrentFrame(run, RunFrame.CreateCall(frame.FrameId, "step-1", "invented-callee", 2,
                    Array.Empty<string>(), Array.Empty<string>(), "in_progress"));
            else if (mismatch == "null-loop-value")
                ReplaceCurrentFrame(run, RunFrame.CreateIteration(frame.FrameId, "step-1", 0, "item", null!, "in_progress"));
            else
                run.Results[0].StepId = "step-1#wrong";
        }

        private static async Task<InvalidOperationException> AssertSubmissionRefusesAtomically(
            WorkflowRunState run, string stepId)
        {
            var beforeIndex = run.StepIndex;
            var beforeRunStatus = run.Status;
            var beforeStatuses = run.Results.Select(x => x.Status).ToArray();
            var beforeNotes = run.Results.Select(x => x.Note).ToArray();
            var beforeAnswers = run.Results.Select(x => x.Answers).ToArray();
            var beforeHistoryCounts = run.Results.Select(x => x.VerifyHistory.Count).ToArray();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                WorkflowRunner.SubmitStepAsync(run, stepId, null, null));

            Assert.Equal(beforeIndex, run.StepIndex);
            Assert.Equal(beforeRunStatus, run.Status);
            Assert.Equal(beforeStatuses, run.Results.Select(x => x.Status));
            Assert.Equal(beforeNotes, run.Results.Select(x => x.Note));
            Assert.Equal(beforeAnswers, run.Results.Select(x => x.Answers));
            Assert.Equal(beforeHistoryCounts, run.Results.Select(x => x.VerifyHistory.Count));
            return error;
        }

        private static InvalidOperationException AssertSkipRefusesAtomically(
            WorkflowRunState run, string stepId)
        {
            var beforeIndex = run.StepIndex;
            var beforeRunStatus = run.Status;
            var beforeStatuses = run.Results.Select(x => x.Status).ToArray();
            var beforeNotes = run.Results.Select(x => x.Note).ToArray();

            var error = Assert.Throws<InvalidOperationException>(() =>
                WorkflowRunner.SkipStep(run, stepId, "invented refusal reason"));

            Assert.Equal(beforeIndex, run.StepIndex);
            Assert.Equal(beforeRunStatus, run.Status);
            Assert.Equal(beforeStatuses, run.Results.Select(x => x.Status));
            Assert.Equal(beforeNotes, run.Results.Select(x => x.Note));
            return error;
        }

        private static PredicateFacts StartFactsWithInjectedLoopValue(string value) => new PredicateFacts
        {
            LoopIndex = 7,
            LoopValues = new Dictionary<string, string> { ["item"] = value },
        };

        private static void ReplaceCurrentFrame(WorkflowRunState run, RunFrame replacement)
        {
            var index = run.Frames.FindIndex(x => x.FrameId == replacement.FrameId);
            Assert.True(index >= 0);
            run.Frames[index] = replacement;
        }

        private static WorkflowDef CertificateDefinition()
        {
            var first = Step("step-1", hardGate: true);
            first.Gate.Inputs = new[]
            {
                new GateInput { Name = "certificate", Question = "Fixture certificate?", Required = "required" },
                new GateInput { Name = "optionalThing", Question = "Invented optional input?", Required = "optional" },
            };
            return new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "conditional-certificate",
                Title = "Conditional certificate",
                Strictness = "hard",
                Steps = new[]
                {
                    first,
                    Step("step-2", "inputs.optionalThing.answered", hardGate: true),
                    Step("step-3", hardGate: true),
                },
            };
        }
    }
}
