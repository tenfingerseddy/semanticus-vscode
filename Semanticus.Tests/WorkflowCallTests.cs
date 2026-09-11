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
    /// <summary>
    /// Workflow calls: structure, binding, return boundaries, loop composition and public admission.
    ///
    /// One bounded transition pair: a PURE <c>ResolveCallSplice</c> that refuses and mutates nothing, and a
    /// mutating <c>SpliceCallAt</c> that turns one `call:` row into a call frame plus the callee's steps as
    /// planned rows immediately after it. Public start initializes the reachable call structure, while
    /// loop setup creates one callee block for each expanded iteration.
    ///
    /// Every fixture name is invented. Both prerequisites are consumed, not delivered, here: planned-row
    /// ownership (`PlannedStep.Owner`, `WorkflowRunState.OwnerClosure`) and the per-frame certificate fold.
    /// </summary>
    public sealed class WorkflowCallTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Long_loop_advances_do_not_search_every_frame_for_every_future_frame(bool calls)
        {
            const int count = 48;
            var loop = new ForEachSpec { As = "item", MaxIterations = count,
                InLiteral = Enumerable.Range(0, count).Select(i => i.ToString()).ToArray() };
            var header = calls ? CallHeader("repeat", forEach: loop) : new WorkflowStep { Id = "repeat", ForEach = loop };
            var run = CallerRun(header, Def(CalleeName, null, Plain("work", 1)));
            WorkflowRunner.InitializeCalls(run);
            await WorkflowRunner.SubmitStepAsync(run, "repeat", new(), null);
            var lookups = 0;
            run.FrameLookupForTest = () => lookups++;
            WorkflowRunner.AdvancePastInapplicableSteps(run);
            Assert.True(lookups <= run.Plan.Count,
                $"One advance made {lookups} full frame searches for {run.Plan.Count} rows.");
            run.FrameLookupForTest = null;
            Assert.Equal("repeat#0", run.Plan[run.StepIndex].InstanceId);
            Assert.All(run.Frames.Where(f => f.ProjectionKind != null), f => Assert.Equal("in_progress", f.State));

            await WorkflowRunner.SubmitStepAsync(run, "repeat#0", new(), null);
            if (calls)
            {
                Assert.Equal("in_progress", run.Frames.Single(f => f.ProjectionKind == "iteration" && f.IterationIndex == 0).State);
                await WorkflowRunner.SubmitStepAsync(run, "repeat#0/work", new(), null);
            }
            Assert.Equal("passed", run.Frames.Single(f => f.ProjectionKind == "iteration" && f.IterationIndex == 0).State);
            Assert.All(run.Frames.Where(f => f.ProjectionKind == "iteration" && f.IterationIndex > 0),
                f => Assert.Equal("in_progress", f.State));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Completed_call_returns_do_not_rescan_descendant_rows_on_every_answer_read(bool returns)
        {
            const int count = 12;
            var header = CallHeader("repeat", returns: returns ? new[] { "reply" } : Array.Empty<string>(),
                forEach: new ForEachSpec { As = "item", MaxIterations = count,
                    InLiteral = Enumerable.Range(0, count).Select(i => i.ToString()).ToArray() });
            var callee = Def(CalleeName, null, Plain("work", 1, gate: new GateSpec { Inputs = new[] {
                new GateInput { Name = "reply", Type = "text", Scope = "run", Required = "optional", Question = "Reply?" } } }));
            var run = CallerRun(header, callee);
            WorkflowRunner.InitializeCalls(run);
            await WorkflowRunner.SubmitStepAsync(run, "repeat", new(), null);
            for (var i = 0; i < count; i++)
            {
                await WorkflowRunner.SubmitStepAsync(run, $"repeat#{i}", new(), null);
                await WorkflowRunner.SubmitStepAsync(run, $"repeat#{i}/work", new() {
                    ["reply"] = new AnswerValue { Value = i.ToString() } }, null);
            }
            Assert.Equal("wrap-up", run.Plan[run.StepIndex].InstanceId);
            Assert.Equal(Enumerable.Range(0, count).Select(i => (int?)(i * 2 + 1)),
                run.Frames.Where(f => f.ProjectionKind == "call").Select(f => f.ReturnPlanIndex));
            var lookups = 0;
            run.FrameLookupForTest = () => lookups++;
            var visible = WorkflowRunner.VisibleAnswerSources(run);
            Assert.True(lookups <= run.Plan.Count,
                $"One answer read made {lookups} full frame searches for {run.Plan.Count} rows.");
            run.FrameLookupForTest = null;
            if (returns) Assert.Equal((count - 1).ToString(), visible["reply"].Answer.Value);
            else Assert.DoesNotContain("reply", visible.Keys);
            Assert.All(run.Frames.Where(f => f.ProjectionKind != null), f => Assert.Equal("passed", f.State));
        }

        [Fact]
        public async Task Published_return_position_survives_a_later_loop_expansion()
        {
            var callee = Def(CalleeName, null, Plain("work", 1, gate: new GateSpec { Inputs = new[] {
                new GateInput { Name = "reply", Type = "text", Required = "optional", Question = "Reply?" } } }));
            var caller = Def(CallerName, null, CallHeader(returns: new[] { "reply" }),
                new WorkflowStep { Id = "later", Number = 2, ForEach = new ForEachSpec {
                    As = "item", InLiteral = new[] { "a", "b", "c" } } }, Plain("wrap-up", 3));
            var run = WorkflowOwnerFixtures.ClosureRun("wfr-return-position", null, (caller, null), (callee, null));
            WorkflowRunner.InitializeCalls(run);
            await WorkflowRunner.SubmitStepAsync(run, "prove-it", new(), null);
            await WorkflowRunner.SubmitStepAsync(run, "prove-it/work", new() {
                ["reply"] = new AnswerValue { Value = "kept" } }, null);
            var call = run.Frames.Single(f => f.ProjectionKind == "call");
            Assert.Equal(1, call.ReturnPlanIndex);
            Assert.Equal("kept", WorkflowRunner.AllAnswers(run)["reply"].Value);
            await WorkflowRunner.SubmitStepAsync(run, "later", new(), null);
            Assert.Equal(6, run.Plan.Count);
            Assert.Equal(1, call.ReturnPlanIndex);
            for (var i = 0; i < 3; i++)
                await WorkflowRunner.SubmitStepAsync(run, $"later#{i}", new(), null);
            Assert.Equal("kept", WorkflowRunner.AllAnswers(run)["reply"].Value);
            Assert.Equal(1, WorkflowRunner.VisibleAnswerSources(run)["reply"].PlanIndex);
        }

        [Fact]
        public async Task Nested_iterations_keep_run_scoped_answers_inside_call_boundary()
        {
            GateSpec Input(string name) => new GateSpec { Inputs = new[] {
                new GateInput { Name = name, Type = "text", Required = "optional", Scope = "run", Question = "Value?" } } };
            var callee = Def(CalleeName, null, new WorkflowStep {
                Id = "repeat", Title = "Repeat", Gate = Input("callee"),
                ForEach = new ForEachSpec { As = "item", InLiteral = new[] { "a", "b" } } });
            var run = CallerRun(header: CallHeader(gate: Input("caller")), callee: callee);
            Splice(run);
            await WorkflowRunner.SubmitStepAsync(run, "prove-it", new() { ["caller"] = new AnswerValue { Value = "private" } }, null);
            await WorkflowRunner.SubmitStepAsync(run, "prove-it/repeat", new(), null);
            Assert.DoesNotContain("caller", WorkflowRunner.AllAnswers(run).Keys);
            await WorkflowRunner.SubmitStepAsync(run, "prove-it/repeat#0", new() { ["callee"] = new AnswerValue { Value = "inside" } }, null);
            Assert.Equal("inside", WorkflowRunner.AllAnswers(run)["callee"].Value);
            await WorkflowRunner.SubmitStepAsync(run, "prove-it/repeat#1", new(), null);
            Assert.DoesNotContain("callee", WorkflowRunner.AllAnswers(run).Keys);
            Assert.Equal("private", WorkflowRunner.AllAnswers(run)["caller"].Value);
        }

        [Fact]
        public async Task Final_callee_answer_does_not_prepare_callers_adjacent_loop()
        {
            GateSpec Source(string required) => new GateSpec { Inputs = new[] { new GateInput {
                Name = "items", Type = "text", Required = required, Scope = "run", Question = "Items?" } } };
            var caller = Def(CallerName, null,
                CallHeader(gate: Source("optional")),
                new WorkflowStep { Id = "repeat", ForEach = new ForEachSpec { As = "item", InInput = "items" } });
            var callee = Def(CalleeName, null, Plain("gather", 1, gate: Source("required")));
            var run = WorkflowOwnerFixtures.ClosureRun("wfr-adjacent", null, (caller, null), (callee, null));
            Splice(run);
            await WorkflowRunner.SubmitStepAsync(run, "prove-it", new() { ["items"] = new AnswerValue { Value = "" } }, null);
            await WorkflowRunner.SubmitStepAsync(run, "prove-it/gather", new() { ["items"] = new AnswerValue { Value = "callee value" } }, null);
            Assert.Null(run.Results[1].PendingForEach);
            Assert.Equal("repeat", run.Plan[run.StepIndex].InstanceId);
            await WorkflowRunner.SubmitStepAsync(run, "repeat", new(), null);
            Assert.Equal("completed", run.Status);
            Assert.Equal("not_applicable", run.Results[2].Status);
        }

        [Fact]
        public async Task Initialized_nested_calls_bind_once_return_only_named_answers_and_record_used_values()
        {
            var third = Def("invented-third", "hard", Plain("answer", 1, gate: new GateSpec { Inputs = new[] {
                new GateInput { Name = "target", Type = "text", Required = "required", Question = "Target?" },
                new GateInput { Name = "reply", Type = "text", Required = "optional", Question = "Reply?" },
                new GateInput { Name = "private", Type = "text", Required = "optional", Question = "Private?" } } }));
            var middle = Def(CalleeName, "hard", CallHeader("relay", callee: third.Name,
                with: new() { ["target"] = "inputs.incoming" }, returns: new[] { "reply" },
                gate: new GateSpec { Inputs = new[] {
                    new GateInput { Name = "incoming", Type = "text", Required = "required", Question = "Incoming?" } } }));
            var run = CallerRun(CallHeader(with: new() { ["incoming"] = "literal value" }, returns: new[] { "reply" }),
                middle, extraMembers: new[] { third });
            WorkflowRunner.InitializeCalls(run);
            Assert.Equal(new[] { "prove-it", "prove-it/relay", "prove-it/relay/answer", "wrap-up" }, run.Plan.Select(p => p.InstanceId));
            Assert.All(run.Results, result => Assert.Empty(result.Answers));
            var initialFrames = WorkflowRunner.BuildView(run).Frames;
            Assert.Null(initialFrames[0].ParentFrameIndex);
            Assert.Equal(0, initialFrames[1].ParentFrameIndex);
            Assert.Equal(3, initialFrames[1].Depth);
            Assert.All(initialFrames, frame => Assert.Empty(frame.Returned));

            await WorkflowRunner.SubmitStepAsync(run, "prove-it", new(), null);
            Assert.All(WorkflowRunner.BuildView(run).Frames, frame => Assert.Empty(frame.Returned));
            Assert.Equal("literal value", WorkflowRunner.BuildView(run).CurrentStep.ProvidedAnswers["incoming"].Value);
            await WorkflowRunner.SubmitStepAsync(run, "prove-it/relay", new(), null);
            await WorkflowRunner.SubmitStepAsync(run, "prove-it/relay/answer", new() {
                ["reply"] = new AnswerValue { Value = "returned value" },
                ["private"] = new AnswerValue { Value = "not exported" } }, null);
            Assert.Equal("wrap-up", run.Plan[run.StepIndex].InstanceId);
            Assert.Equal("literal value", run.Results[2].Answers["target"].Value);
            var visible = WorkflowRunner.AllAnswers(run);
            Assert.Equal("returned value", visible["reply"].Value);
            Assert.DoesNotContain("private", visible.Keys);
            Assert.DoesNotContain("target", visible.Keys);
            Assert.All(run.Frames.Skip(1), frame => Assert.Equal("passed", frame.State));
            Assert.All(run.Frames.Skip(1), frame => Assert.Equal(2, frame.ReturnPlanIndex));
            Assert.All(WorkflowRunner.BuildView(run).Frames, frame => Assert.Equal(new[] { "reply" }, frame.Returned));
        }

        [Fact]
        public async Task Failed_header_keeps_bindings_uncommitted_and_retry_records_corrected_values()
        {
            var input = new GateInput { Name = "source", Type = "text", Required = "required", Question = "Source?" };
            var run = CallerRun(CallHeader(with: new() { ["target"] = "inputs.source" },
                gate: new GateSpec { Inputs = new[] { input }, Verify = new[] { new VerifySpec { Kind = "invented-proof" } } }),
                Def(CalleeName, "hard", Plain("check", 1, gate: new GateSpec { Inputs = new[] {
                    new GateInput { Name = "target", Type = "text", Required = "required", Question = "Target?" } } })),
                callerStrictness: "hard");
            WorkflowRunner.InitializeCalls(run);
            WorkflowVerifyExecutor fail = (spec, step, state, answers) => Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "failed" });
            await Assert.ThrowsAsync<InvalidOperationException>(() => WorkflowRunner.SubmitStepAsync(run, "prove-it",
                new() { ["source"] = new AnswerValue { Value = "first" } }, fail));
            Assert.False(run.Frames[1].CallInputsCommitted);
            Assert.Empty(run.Frames[1].Passed);
            WorkflowVerifyExecutor pass = (spec, step, state, answers) => Task.FromResult(new VerifyResult { Kind = spec.Kind, Status = "passed" });
            await WorkflowRunner.SubmitStepAsync(run, "prove-it", new() { ["source"] = new AnswerValue { Value = "second" } }, pass);
            var provided = WorkflowRunner.BuildView(run).CurrentStep.ProvidedAnswers;
            Assert.Equal("second", provided["target"].Value);
            provided["target"].Value = "external mutation";
            await WorkflowRunner.SubmitStepAsync(run, "prove-it/check", new(), null);
            Assert.Equal("second", run.Results[1].Answers["target"].Value);
            Assert.Equal(2, run.Results[0].VerifyHistory.Count);
        }

        [Fact]
        public void Failed_call_initialization_registers_no_run_and_preserves_next_id()
        {
            var store = new WorkflowRunStore();
            var bad = CallerRun(callee: Def(CalleeName, null));
            Assert.Throws<InvalidOperationException>(() => store.Start(bad.OwnerClosure, initialize: WorkflowRunner.InitializeCalls));
            Assert.Empty(store.ActiveRuns());
            Assert.Null(store.Get(null));
            var valid = CallerRun();
            var started = store.Start(valid.OwnerClosure, initialize: WorkflowRunner.InitializeCalls);
            Assert.Equal("wfr-1", started.RunId);
            Assert.Equal(4, started.Plan.Count);
        }

        [Fact]
        public async Task Loop_call_runs_one_callee_per_iteration_and_keeps_iteration_open_through_body()
        {
            var run = CallerRun(CallHeader(forEach: new ForEachSpec { As = "item", InLiteral = new[] { "first", "second" } },
                with: new() { ["target"] = "[[loop.item]]", ["position"] = "[[loop.index]]" }),
                Def(CalleeName, "hard", Plain("check", 1, gate: new GateSpec { Inputs = new[] {
                    new GateInput { Name = "target", Type = "text", Required = "required", Question = "Target?" },
                    new GateInput { Name = "position", Type = "text", Required = "required", Question = "Position?" } } })));
            WorkflowRunner.InitializeCalls(run);
            Assert.Equal(2, run.Plan.Count);
            await WorkflowRunner.SubmitStepAsync(run, "prove-it", new(), null);
            Assert.Equal(new[] { "prove-it#0", "prove-it#0/check", "prove-it#1", "prove-it#1/check", "wrap-up" }, run.Plan.Select(p => p.InstanceId));
            await WorkflowRunner.SubmitStepAsync(run, "prove-it#0", new(), null);
            Assert.Equal("in_progress", run.Frames.Single(f => f.ProjectionKind == "iteration" && f.IterationIndex == 0).State);
            await WorkflowRunner.SubmitStepAsync(run, "prove-it#0/check", new(), null);
            Assert.Equal("first", run.Results[1].Answers["target"].Value);
            Assert.Equal("0", run.Results[1].Answers["position"].Value);
            Assert.Equal("passed", run.Frames.Single(f => f.ProjectionKind == "iteration" && f.IterationIndex == 0).State);
            WorkflowRunner.SkipStep(run, "prove-it#1", "second item deferred");
            Assert.Equal("wrap-up", run.Plan[run.StepIndex].InstanceId);
            Assert.Equal("skipped", run.Results[3].Status);
            Assert.Equal("failed", run.Frames.Single(f => f.ProjectionKind == "iteration" && f.IterationIndex == 1).State);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Missing_return_stays_absent_and_declined_return_preserves_reason(bool decline)
        {
            var run = CallerRun(CallHeader(returns: new[] { "reply" }),
                Def(CalleeName, "hard", Plain("check", 1, gate: new GateSpec { Inputs = new[] {
                    new GateInput { Name = "reply", Type = "text", Required = "optional", Question = "Reply?" } } })));
            WorkflowRunner.InitializeCalls(run);
            await WorkflowRunner.SubmitStepAsync(run, "prove-it", new(), null);
            var answers = new Dictionary<string, AnswerValue>();
            if (decline) answers["reply"] = new AnswerValue { Declined = true, DeclineReason = "not known" };
            await WorkflowRunner.SubmitStepAsync(run, "prove-it/check", answers, null);
            var visible = WorkflowRunner.AllAnswers(run);
            if (decline)
            {
                Assert.True(visible["reply"].Declined);
                Assert.Equal("not known", visible["reply"].DeclineReason);
            }
            else Assert.DoesNotContain("reply", visible.Keys);
        }

        private sealed class Pro : IEntitlement { public bool IsPro => true; public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" }; }

        private const string CallerName = "invented-call-caller";
        private const string CalleeName = "invented-call-callee";

        // ---------------------------------------------------------------- fixtures

        private static WorkflowStep Plain(string id, int number, string title = null, GateSpec gate = null, string when = null) =>
            new WorkflowStep { Id = id, Number = number, Title = title ?? ("Invented " + id), Gate = gate, When = when };

        private static WorkflowStep CallHeader(
            string id = "prove-it", int number = 1, string callee = CalleeName,
            Dictionary<string, string> with = null, string[] returns = null,
            ForEachSpec forEach = null, string when = null, GateSpec gate = null) => new WorkflowStep
            {
                Id = id,
                Number = number,
                Title = "Hand off to " + callee,
                When = when,
                Gate = gate,
                ForEach = forEach,
                Call = new CallSpec
                {
                    Workflow = callee,
                    With = with ?? new Dictionary<string, string>(StringComparer.Ordinal),
                    Returns = returns ?? Array.Empty<string>(),
                },
            };

        private static WorkflowDef Def(string name, string strictness, params WorkflowStep[] steps) => new WorkflowDef
        {
            SchemaVersion = 2, Name = name, Version = 1, Strictness = strictness, Steps = steps,
        };

        private static WorkflowDef TwoStepCallee(string name = CalleeName, string strictness = null) =>
            Def(name, strictness, Plain("gather", 1, "Gather invented evidence"), Plain("check", 2, "Check it"));

        /// <summary>A caller run whose row 0 is a `call:` header and whose row 1 is an ordinary following row,
        /// over a closure that also holds the callee. This is the shape the run-start splice driver will build;
        /// nothing public reaches it yet.</summary>
        private static WorkflowRunState CallerRun(
            WorkflowStep header = null, WorkflowDef callee = null,
            string callerStrictness = null, string callerSettings = null, string calleeSettings = null,
            string globalStrictness = null, string runId = "wfr-call",
            params WorkflowDef[] extraMembers)
        {
            header = header ?? CallHeader();
            callee = callee ?? TwoStepCallee();
            var caller = Def(CallerName, callerStrictness, header, Plain("wrap-up", 2, "Wrap up"));
            var members = new List<(WorkflowDef, string)> { (caller, callerSettings), (callee, calleeSettings) };
            foreach (var extra in extraMembers ?? Array.Empty<WorkflowDef>()) members.Add((extra, null));
            return WorkflowOwnerFixtures.ClosureRun(runId, globalStrictness, members.ToArray());
        }

        /// <summary>Everything a refused resolve or splice promises to leave byte-identical.</summary>
        private static string Snapshot(WorkflowRunState run) => JsonSerializer.Serialize(new
        {
            run.StepIndex,
            run.Status,
            Plan = run.Plan.Select(p => new
            {
                p.InstanceId, p.FrameId, p.IterationIndex, p.ForEachExpanded, p.CallExpanded,
                Owner = run.RowOwner(p).Name, StepId = p.Step.Id,
            }).ToArray(),
            Results = run.Results.Select(r => new { r.StepId, r.Status, r.Note, r.EffectiveStrictness }).ToArray(),
            Frames = run.Frames.Select(f => new { f.FrameId, f.ProjectionKind, f.State, f.Depth, f.Workflow, f.ParentFrameId }).ToArray(),
        });

        private static void AssertUnchanged(WorkflowRunState run, string before, string because) =>
            Assert.True(before == Snapshot(run), because + " must leave the run byte-identical.");

        private static CallSplice Splice(WorkflowRunState run, int headerIndex = 0)
        {
            var resolved = WorkflowRunner.ResolveCallSplice(run, headerIndex);
            WorkflowRunner.SpliceCallAt(run, headerIndex, resolved);
            return resolved;
        }

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-call-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            return ws;
        }

        private static void WriteUserWorkflow(string ws, string file, string md) =>
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", file), md);

        private static CoverageSurfaceLock Surface(params string[] openGrains) => new CoverageSurfaceLock
        {
            CurrentGrid = new[] { "'Product'[Category]", "'Date'[Year]" },
            CurrentOpenGrains = openGrains ?? Array.Empty<string>(),
        };

        /// <summary>[T220] A deterministic no-call, no-loop run whose every varying field is an invented
        /// literal, so its four serializations are byte-comparable against output captured on the pre-splice
        /// parent commit d53f9a1978cf90552c4a45d54baaa0687de03e3f. Rich on purpose: answers, declines, current
        /// and archived verify evidence, a skipped and a not_applicable row, witness and anchor receipts, a
        /// coverage surface and a computed certificate, plus a live current step so BuildView's current-step
        /// projection is inside the comparison too.</summary>
        private static WorkflowRunState NoCallBaselineRun()
        {
            var verify = new GateSpec { Verify = new[] { new VerifySpec { Kind = "workflow_admissible" } } };
            var asked = new GateSpec
            {
                Strictness = "warn",
                Inputs = new[]
                {
                    new GateInput { Name = "invented-note", Question = "What is the invented note?", Type = "text" },
                },
                Verify = new[] { new VerifySpec { Kind = "workflow_admissible" } },
            };
            var def = new WorkflowDef
            {
                SchemaVersion = 2,
                Name = "invented-baseline",
                Title = "Invented baseline workflow",
                Version = 3,
                Strictness = "warn",
                Steps = new[]
                {
                    new WorkflowStep { Id = "baseline-one", Number = 1, Title = "Baseline one", Gate = verify },
                    new WorkflowStep { Id = "baseline-two", Number = 2, Title = "Baseline two", Gate = asked },
                    new WorkflowStep { Id = "baseline-three", Number = 3, Title = "Baseline three", Gate = verify },
                    new WorkflowStep
                    {
                        Id = "baseline-four", Number = 4, Title = "Baseline four",
                        When = "inputs.invented-absent.answered",
                    },
                },
            };

            var run = new WorkflowRunState("wfr-invented-baseline", def, "hard");
            run.StartedUtc = "2026-01-02T03:04:05.0000000Z";
            run.ModelName = "invented-model";
            run.ModelFingerprint = "invented-fingerprint-01";

            var one = run.Results[0];
            one.Status = "passed";
            one.Note = "invented note on the first row";
            one.EffectiveStrictness = "hard";
            one.Answers["invented-input"] = new AnswerValue { Value = "invented value" };
            one.Answers["invented-declined"] = new AnswerValue { Declined = true, DeclineReason = "invented decline reason" };
            one.VerifyResults = new[]
            {
                new VerifyResult { Kind = "workflow_admissible", Status = "passed", Detail = "invented passing detail" },
            };
            one.VerifyHistory.Add(new VerifyAttempt
            {
                Ordinal = 1,
                TimestampUtc = "2026-01-02T03:06:07.0000000Z",
                Results = new[]
                {
                    new VerifyResult
                    {
                        Kind = "workflow_admissible", Status = "unavailable",
                        Detail = "invented first attempt", Missing = "invented missing thing",
                    },
                },
            });
            one.VerifyHistory.Add(new VerifyAttempt
            {
                Ordinal = 2,
                TimestampUtc = "2026-01-02T03:08:09.0000000Z",
                Results = new[]
                {
                    new VerifyResult { Kind = "workflow_admissible", Status = "passed", Detail = "invented passing detail" },
                },
            });

            run.Results[1].Status = "in_progress";
            run.Results[2].Status = "skipped";
            run.Results[2].Note = "skipped: invented skip reason";
            run.Results[2].EffectiveStrictness = "warn";
            run.Results[3].Status = "not_applicable";
            run.Results[3].Note = "did not apply: condition 'inputs.invented-absent.answered' evaluated false.";

            var top = run.Frames[0];
            top.CoverageSurface = new CoverageSurfaceLock
            {
                CurrentGrid = new[] { "'Product'[Category]", "'Date'[Year]" },
                CurrentOpenGrains = new[] { "'Date'[Year]=2026" },
            };
            top.WitnessLocks["invented-probe"] = "invented-witness-hash";
            top.RunAnchorLocks["invented-anchors"] = new AnchorRunLock
            {
                AnchorsInput = "invented-anchors",
                InitialHash = "invented-initial-hash",
                CurrentHash = "invented-current-hash",
                StepId = "baseline-one",
            };
            top.AnchorRevisions.Add(new AnchorRevision
            {
                Key = "invented-anchors",
                AnchorsInput = "invented-anchors",
                BeforeHash = "invented-before-hash",
                AfterHash = "invented-after-hash",
                StepId = "baseline-one",
                TimestampUtc = "2026-01-02T03:10:11.0000000Z",
            });
            top.AnchorFormRepairCount = 2;

            run.StepIndex = 1;
            run.Certificate = WorkflowRunner.ComputeCertificate(run);
            return run;
        }

        /// <summary>The four projections the ratified contract requires to stay byte-identical on a run that
        /// holds no `call:` row, in a fixed order: record then view, System.Text.Json then Newtonsoft.</summary>
        private static string[] NoCallBaselineBytes(WorkflowRunState run) => new[]
        {
            JsonSerializer.Serialize(WorkflowRunner.BuildRunRecord(run)),
            Newtonsoft.Json.JsonConvert.SerializeObject(WorkflowRunner.BuildRunRecord(run)),
            JsonSerializer.Serialize(WorkflowRunner.BuildView(run)),
            Newtonsoft.Json.JsonConvert.SerializeObject(WorkflowRunner.BuildView(run)),
        };

        // ---------------------------------------------------------------- pre-splice byte baselines
        //
        // Captured by running BuildRunRecord and BuildView over NoCallBaselineRun() on the PARENT commit
        // d53f9a1978cf90552c4a45d54baaa0687de03e3f, before one line of this unit existed, and pasted here
        // verbatim. That is what makes case 16 a byte guard rather than four spot checks: an added field, a
        // removed field, a renamed field, a reordered field, a changed value and a changed null/default all
        // fail it, and none of them fails a "does not contain 'Owner'" assertion.
        //
        // To recapture: extract the parent commit (`git archive <sha> | tar -x -C <dir>`), drop a fixture that
        // calls NoCallBaselineBytes into that tree's test project, register it in TestSources.txt, run it, and
        // paste the four strings back. Regenerate ONLY when the ratified no-call wire shape itself changes.

        private const string PreSpliceRunRecordStj = """{"runId":"wfr-invented-baseline","workflow":"invented-baseline","version":3,"status":"active","abortReason":null,"startedUtc":"2026-01-02T03:04:05.0000000Z","finishedUtc":null,"modelName":"invented-model","modelFingerprint":"invented-fingerprint-01","steps":[{"StepId":"baseline-one","Status":"passed","Note":"invented note on the first row","EffectiveStrictness":"hard","answers":[{"name":"invented-input","Value":"invented value","declined":null,"reason":null},{"name":"invented-declined","Value":null,"declined":true,"reason":"invented decline reason"}],"verify":[{"Kind":"workflow_admissible","Status":"passed","Detail":"invented passing detail","Missing":null,"Shapes":null,"MismatchCells":null}],"verifyHistory":[{"Ordinal":1,"TimestampUtc":"2026-01-02T03:06:07.0000000Z","Results":[{"Kind":"workflow_admissible","Status":"unavailable","Detail":"invented first attempt","Missing":"invented missing thing","Shapes":null,"MismatchCells":null}]},{"Ordinal":2,"TimestampUtc":"2026-01-02T03:08:09.0000000Z","Results":[{"Kind":"workflow_admissible","Status":"passed","Detail":"invented passing detail","Missing":null,"Shapes":null,"MismatchCells":null}]}]},{"StepId":"baseline-two","Status":"in_progress","Note":null,"EffectiveStrictness":null,"answers":[],"verify":[],"verifyHistory":null},{"StepId":"baseline-three","Status":"skipped","Note":"skipped: invented skip reason","EffectiveStrictness":"warn","answers":[],"verify":[],"verifyHistory":null},{"StepId":"baseline-four","Status":"not_applicable","Note":"did not apply: condition \u0027inputs.invented-absent.answered\u0027 evaluated false.","EffectiveStrictness":null,"answers":[],"verify":[],"verifyHistory":null}],"witnessLocks":[{"probe":"invented-probe","hash":"invented-witness-hash"}],"witnessRevisions":null,"partitionRevisions":null,"anchorLocks":[{"AnchorsInput":"invented-anchors","InitialHash":"invented-initial-hash","CurrentHash":"invented-current-hash","StepId":"baseline-one"}],"anchorRevisions":[{"Key":"invented-anchors","AnchorsInput":"invented-anchors","BeforeHash":"invented-before-hash","AfterHash":"invented-after-hash","StepId":"baseline-one","TimestampUtc":"2026-01-02T03:10:11.0000000Z","Changes":[]}],"coverageSurfaceRevisions":null,"shapeMismatchLedger":null,"shapeMismatchCountersigns":null,"certificate":{"Level":"OVERRIDDEN","AgentClaim":null,"ClaimLevel":"OVERRIDDEN","ComputedLevel":"OVERRIDDEN","GridColumns":["\u0027Product\u0027[Category]","\u0027Date\u0027[Year]"],"Coverage":[{"ShapeId":"grand_total","State":"anchored","Anchored":true,"Open":false},{"ShapeId":"axis:\u0027Product\u0027[Category]","State":"anchored","Anchored":true,"Open":false},{"ShapeId":"axis:\u0027Date\u0027[Year]","State":"anchored","Anchored":true,"Open":false},{"ShapeId":"cross","State":"anchored","Anchored":true,"Open":false}],"OpenGrains":["\u0027Date\u0027[Year]=2026"],"CountersignedCells":[],"DisputedCells":[],"AnchorRevisionCount":1,"FormRepairCount":2,"CoverageSurfaceRevisions":[],"SkippedSteps":[{"StepId":"baseline-three","Title":"Baseline three","Reason":"invented skip reason","EffectiveStrictness":"hard"}]}}""";

        private const string PreSpliceRunRecordNewtonsoft = """{"runId":"wfr-invented-baseline","workflow":"invented-baseline","version":3,"status":"active","abortReason":null,"startedUtc":"2026-01-02T03:04:05.0000000Z","finishedUtc":null,"modelName":"invented-model","modelFingerprint":"invented-fingerprint-01","steps":[{"StepId":"baseline-one","Status":"passed","Note":"invented note on the first row","EffectiveStrictness":"hard","answers":[{"name":"invented-input","Value":"invented value","declined":null,"reason":null},{"name":"invented-declined","Value":null,"declined":true,"reason":"invented decline reason"}],"verify":[{"Kind":"workflow_admissible","Status":"passed","Detail":"invented passing detail","Missing":null,"Shapes":null,"MismatchCells":null}],"verifyHistory":[{"Ordinal":1,"TimestampUtc":"2026-01-02T03:06:07.0000000Z","Results":[{"Kind":"workflow_admissible","Status":"unavailable","Detail":"invented first attempt","Missing":"invented missing thing","Shapes":null,"MismatchCells":null}]},{"Ordinal":2,"TimestampUtc":"2026-01-02T03:08:09.0000000Z","Results":[{"Kind":"workflow_admissible","Status":"passed","Detail":"invented passing detail","Missing":null,"Shapes":null,"MismatchCells":null}]}]},{"StepId":"baseline-two","Status":"in_progress","Note":null,"EffectiveStrictness":null,"answers":[],"verify":[],"verifyHistory":null},{"StepId":"baseline-three","Status":"skipped","Note":"skipped: invented skip reason","EffectiveStrictness":"warn","answers":[],"verify":[],"verifyHistory":null},{"StepId":"baseline-four","Status":"not_applicable","Note":"did not apply: condition 'inputs.invented-absent.answered' evaluated false.","EffectiveStrictness":null,"answers":[],"verify":[],"verifyHistory":null}],"witnessLocks":[{"probe":"invented-probe","hash":"invented-witness-hash"}],"witnessRevisions":null,"partitionRevisions":null,"anchorLocks":[{"AnchorsInput":"invented-anchors","InitialHash":"invented-initial-hash","CurrentHash":"invented-current-hash","StepId":"baseline-one"}],"anchorRevisions":[{"Key":"invented-anchors","AnchorsInput":"invented-anchors","BeforeHash":"invented-before-hash","AfterHash":"invented-after-hash","StepId":"baseline-one","TimestampUtc":"2026-01-02T03:10:11.0000000Z","Changes":[]}],"coverageSurfaceRevisions":null,"shapeMismatchLedger":null,"shapeMismatchCountersigns":null,"certificate":{"Level":"OVERRIDDEN","AgentClaim":null,"ClaimLevel":"OVERRIDDEN","ComputedLevel":"OVERRIDDEN","GridColumns":["'Product'[Category]","'Date'[Year]"],"Coverage":[{"ShapeId":"grand_total","State":"anchored","Anchored":true,"Open":false},{"ShapeId":"axis:'Product'[Category]","State":"anchored","Anchored":true,"Open":false},{"ShapeId":"axis:'Date'[Year]","State":"anchored","Anchored":true,"Open":false},{"ShapeId":"cross","State":"anchored","Anchored":true,"Open":false}],"OpenGrains":["'Date'[Year]=2026"],"CountersignedCells":[],"DisputedCells":[],"AnchorRevisionCount":1,"FormRepairCount":2,"CoverageSurfaceRevisions":[],"SkippedSteps":[{"StepId":"baseline-three","Title":"Baseline three","Reason":"invented skip reason","EffectiveStrictness":"hard"}]}}""";

        private const string PreSpliceViewStj = """{"RunId":"wfr-invented-baseline","Workflow":"invented-baseline","Title":"Invented baseline workflow","WorkflowVersion":3,"Status":"active","AbortReason":null,"StartedUtc":"2026-01-02T03:04:05.0000000Z","FinishedUtc":null,"ModelName":"invented-model","ModelFingerprint":"invented-fingerprint-01","StepIndex":1,"TotalSteps":4,"Steps":[{"StepId":"baseline-one","Title":"Baseline one","Status":"passed","Note":"invented note on the first row","Answers":{"invented-input":{"Value":"invented value","Declined":false,"DeclineReason":null,"Answered":true},"invented-declined":{"Value":null,"Declined":true,"DeclineReason":"invented decline reason","Answered":false}},"VerifyResults":[{"Kind":"workflow_admissible","Status":"passed","Detail":"invented passing detail","Missing":null,"Shapes":null,"MismatchCells":null}],"VerifyHistory":[{"Ordinal":1,"TimestampUtc":"2026-01-02T03:06:07.0000000Z","Results":[{"Kind":"workflow_admissible","Status":"unavailable","Detail":"invented first attempt","Missing":"invented missing thing","Shapes":null,"MismatchCells":null}]},{"Ordinal":2,"TimestampUtc":"2026-01-02T03:08:09.0000000Z","Results":[{"Kind":"workflow_admissible","Status":"passed","Detail":"invented passing detail","Missing":null,"Shapes":null,"MismatchCells":null}]}],"EffectiveStrictness":"hard"},{"StepId":"baseline-two","Title":"Baseline two","Status":"in_progress","Note":null,"Answers":{},"VerifyResults":[],"VerifyHistory":[],"EffectiveStrictness":null},{"StepId":"baseline-three","Title":"Baseline three","Status":"skipped","Note":"skipped: invented skip reason","Answers":{},"VerifyResults":[],"VerifyHistory":[],"EffectiveStrictness":"warn"},{"StepId":"baseline-four","Title":"Baseline four","Status":"not_applicable","Note":"did not apply: condition \u0027inputs.invented-absent.answered\u0027 evaluated false.","Answers":{},"VerifyResults":[],"VerifyHistory":[],"EffectiveStrictness":null}],"CurrentStep":{"StepId":"baseline-two","Title":"Baseline two","Instructions":null,"Questions":[{"Name":"invented-note","Question":"What is the invented note?","Type":"text","Required":"answer-or-decline","DaxPurity":null,"Scope":null}],"VerifyKinds":["workflow_admissible"],"EffectiveStrictness":"warn","Ops":[]},"WitnessLocks":[{"Probe":"invented-probe","Hash":"invented-witness-hash"}],"WitnessRevisions":null,"PartitionRevisions":null,"AnchorLocks":[{"AnchorsInput":"invented-anchors","InitialHash":"invented-initial-hash","CurrentHash":"invented-current-hash","StepId":"baseline-one"}],"AnchorRevisions":[{"Key":"invented-anchors","AnchorsInput":"invented-anchors","BeforeHash":"invented-before-hash","AfterHash":"invented-after-hash","StepId":"baseline-one","TimestampUtc":"2026-01-02T03:10:11.0000000Z","Changes":[]}],"ShapeMismatchLedger":null,"ShapeMismatchCountersigns":null,"Certificate":{"Level":"OVERRIDDEN","AgentClaim":null,"ClaimLevel":"OVERRIDDEN","ComputedLevel":"OVERRIDDEN","GridColumns":["\u0027Product\u0027[Category]","\u0027Date\u0027[Year]"],"Coverage":[{"ShapeId":"grand_total","State":"anchored","Anchored":true,"Open":false},{"ShapeId":"axis:\u0027Product\u0027[Category]","State":"anchored","Anchored":true,"Open":false},{"ShapeId":"axis:\u0027Date\u0027[Year]","State":"anchored","Anchored":true,"Open":false},{"ShapeId":"cross","State":"anchored","Anchored":true,"Open":false}],"OpenGrains":["\u0027Date\u0027[Year]=2026"],"CountersignedCells":[],"DisputedCells":[],"AnchorRevisionCount":1,"FormRepairCount":2,"CoverageSurfaceRevisions":[],"SkippedSteps":[{"StepId":"baseline-three","Title":"Baseline three","Reason":"invented skip reason","EffectiveStrictness":"hard"}]},"Distillable":false,"DistillableWhy":null}""";

        private const string PreSpliceViewNewtonsoft = """{"RunId":"wfr-invented-baseline","Workflow":"invented-baseline","Title":"Invented baseline workflow","WorkflowVersion":3,"Status":"active","AbortReason":null,"StartedUtc":"2026-01-02T03:04:05.0000000Z","FinishedUtc":null,"ModelName":"invented-model","ModelFingerprint":"invented-fingerprint-01","StepIndex":1,"TotalSteps":4,"Steps":[{"StepId":"baseline-one","Title":"Baseline one","Status":"passed","Note":"invented note on the first row","Answers":{"invented-input":{"Value":"invented value","Declined":false,"DeclineReason":null,"Answered":true},"invented-declined":{"Value":null,"Declined":true,"DeclineReason":"invented decline reason","Answered":false}},"VerifyResults":[{"Kind":"workflow_admissible","Status":"passed","Detail":"invented passing detail","Missing":null,"Shapes":null,"MismatchCells":null}],"VerifyHistory":[{"Ordinal":1,"TimestampUtc":"2026-01-02T03:06:07.0000000Z","Results":[{"Kind":"workflow_admissible","Status":"unavailable","Detail":"invented first attempt","Missing":"invented missing thing","Shapes":null,"MismatchCells":null}]},{"Ordinal":2,"TimestampUtc":"2026-01-02T03:08:09.0000000Z","Results":[{"Kind":"workflow_admissible","Status":"passed","Detail":"invented passing detail","Missing":null,"Shapes":null,"MismatchCells":null}]}],"EffectiveStrictness":"hard"},{"StepId":"baseline-two","Title":"Baseline two","Status":"in_progress","Note":null,"Answers":{},"VerifyResults":[],"VerifyHistory":[],"EffectiveStrictness":null},{"StepId":"baseline-three","Title":"Baseline three","Status":"skipped","Note":"skipped: invented skip reason","Answers":{},"VerifyResults":[],"VerifyHistory":[],"EffectiveStrictness":"warn"},{"StepId":"baseline-four","Title":"Baseline four","Status":"not_applicable","Note":"did not apply: condition 'inputs.invented-absent.answered' evaluated false.","Answers":{},"VerifyResults":[],"VerifyHistory":[],"EffectiveStrictness":null}],"CurrentStep":{"StepId":"baseline-two","Title":"Baseline two","Instructions":null,"Questions":[{"Name":"invented-note","Question":"What is the invented note?","Type":"text","Required":"answer-or-decline","DaxPurity":null,"Scope":null}],"VerifyKinds":["workflow_admissible"],"EffectiveStrictness":"warn","Ops":[]},"WitnessLocks":[{"Probe":"invented-probe","Hash":"invented-witness-hash"}],"WitnessRevisions":null,"PartitionRevisions":null,"AnchorLocks":[{"AnchorsInput":"invented-anchors","InitialHash":"invented-initial-hash","CurrentHash":"invented-current-hash","StepId":"baseline-one"}],"AnchorRevisions":[{"Key":"invented-anchors","AnchorsInput":"invented-anchors","BeforeHash":"invented-before-hash","AfterHash":"invented-after-hash","StepId":"baseline-one","TimestampUtc":"2026-01-02T03:10:11.0000000Z","Changes":[]}],"ShapeMismatchLedger":null,"ShapeMismatchCountersigns":null,"Certificate":{"Level":"OVERRIDDEN","AgentClaim":null,"ClaimLevel":"OVERRIDDEN","ComputedLevel":"OVERRIDDEN","GridColumns":["'Product'[Category]","'Date'[Year]"],"Coverage":[{"ShapeId":"grand_total","State":"anchored","Anchored":true,"Open":false},{"ShapeId":"axis:'Product'[Category]","State":"anchored","Anchored":true,"Open":false},{"ShapeId":"axis:'Date'[Year]","State":"anchored","Anchored":true,"Open":false},{"ShapeId":"cross","State":"anchored","Anchored":true,"Open":false}],"OpenGrains":["'Date'[Year]=2026"],"CountersignedCells":[],"DisputedCells":[],"AnchorRevisionCount":1,"FormRepairCount":2,"CoverageSurfaceRevisions":[],"SkippedSteps":[{"StepId":"baseline-three","Title":"Baseline three","Reason":"invented skip reason","EffectiveStrictness":"hard"}]},"Distillable":false,"DistillableWhy":null}""";


        // ================================================================ 1-4: the shape

        [Fact]
        public void Case01_A_two_step_callee_splices_into_two_pending_rows_after_the_header()
        {
            var run = CallerRun();
            var before = run.StepIndex;

            Splice(run);

            Assert.Equal(new[] { "prove-it", "prove-it/gather", "prove-it/check", "wrap-up" },
                run.Plan.Select(p => p.InstanceId));
            Assert.Equal(run.Plan.Select(p => p.InstanceId), run.Results.Select(r => r.StepId));
            Assert.Equal("pending", run.Results[1].Status);
            Assert.Equal("pending", run.Results[2].Status);
            Assert.Equal("in_progress", run.Results[0].Status);
            Assert.Equal(before, run.StepIndex);
            Assert.True(run.Plan[0].CallExpanded);
            Assert.False(run.Plan[1].CallExpanded);
            Assert.False(run.Plan[2].CallExpanded);

            var frameId = "wfr-call:prove-it";
            Assert.Equal(frameId, run.Plan[1].FrameId);
            Assert.Equal(frameId, run.Plan[2].FrameId);
            Assert.Single(run.Frames.Where(f => f.FrameId == frameId));
            // The callee's own step objects, by REFERENCE, off the frozen closure member.
            var owner = run.OwnerClosure.Require(CalleeName);
            Assert.Same(owner.Steps[0], run.Plan[1].Step);
            Assert.Same(owner.Steps[1], run.Plan[2].Step);
        }

        [Fact]
        public void Case02_The_return_point_is_a_plan_position_and_the_following_row_is_untouched()
        {
            var run = CallerRun();
            var wrapStep = run.Plan[1].Step;
            var wrapOwner = run.RowOwner(run.Plan[1]);

            Splice(run);

            var wrap = run.Plan[3];
            Assert.Equal("wrap-up", wrap.InstanceId);
            Assert.Equal(run.RunId, wrap.FrameId);
            Assert.Same(wrapStep, wrap.Step);
            Assert.Same(wrapOwner, run.RowOwner(wrap));
            Assert.Same(run.Def, run.RowOwner(wrap));
            Assert.Equal("pending", run.Results[3].Status);
            // No return row was minted and no marker written: the plan simply resumes.
            Assert.Equal(4, run.Plan.Count);
        }

        [Fact]
        public void Case03_The_call_frame_projects_the_ratified_wire_shape_and_groups_only_the_callee_rows()
        {
            var run = CallerRun();
            Splice(run);

            var view = WorkflowRunner.BuildView(run);
            var frame = Assert.Single(view.Frames.Where(f => f.Kind == "call"));
            Assert.Equal("prove-it", frame.StepId);
            Assert.Equal(CalleeName, frame.Workflow);
            Assert.Equal(2, frame.Depth);
            Assert.Equal("in_progress", frame.State);
            Assert.Empty(frame.Passed);
            Assert.Empty(frame.Returned);
            Assert.Equal(new[] { "prove-it/gather", "prove-it/check" }, frame.Steps.Select(s => s.StepId));
        }

        [Fact]
        public void Case04_Plan_and_results_stay_aligned_and_every_instance_id_stays_distinct()
        {
            var run = CallerRun();
            Splice(run);

            Assert.Equal(run.Plan.Count, run.Results.Count);
            for (var i = 0; i < run.Plan.Count; i++)
                Assert.Equal(run.Plan[i].InstanceId, run.Results[i].StepId);
            Assert.Equal(run.Plan.Count, run.Plan.Select(p => p.InstanceId).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(run.Frames.Count, run.Frames.Select(f => f.FrameId).Distinct(StringComparer.Ordinal).Count());
        }

        // ================================================================ 5-9: the refusals

        [Fact]
        public void Case05_A_second_splice_of_the_same_header_refuses_and_changes_nothing()
        {
            var run = CallerRun();
            Splice(run);
            var before = Snapshot(run);

            var refusal = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCallSplice(run, 0));
            Assert.Contains("already", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("prove-it", refusal.Message, StringComparison.Ordinal);
            AssertUnchanged(run, before, "R3");
        }

        [Fact]
        public void Case06_A_header_the_run_has_already_moved_past_or_run_refuses()
        {
            // R4a: the cursor is past the header.
            var past = CallerRun();
            past.StepIndex = 1;
            var pastBefore = Snapshot(past);
            Assert.Contains("current", Assert.Throws<InvalidOperationException>(
                () => WorkflowRunner.ResolveCallSplice(past, 0)).Message, StringComparison.OrdinalIgnoreCase);
            AssertUnchanged(past, pastBefore, "R4 (cursor past the header)");

            // R4b: the header's result is terminal.
            var passed = CallerRun();
            passed.Results[0].Status = "passed";
            var passedBefore = Snapshot(passed);
            Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCallSplice(passed, 0));
            AssertUnchanged(passed, passedBefore, "R4 (passed header)");

            // R4c: the header already holds answers.
            var answered = CallerRun();
            answered.Results[0].Answers["invented-note"] = new AnswerValue { Value = "already given" };
            var answeredBefore = Snapshot(answered);
            Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCallSplice(answered, 0));
            AssertUnchanged(answered, answeredBefore, "R4 (answered header)");

            // R4d: the header has verify history.
            var verified = CallerRun();
            verified.Results[0].VerifyHistory.Add(new VerifyAttempt());
            var verifiedBefore = Snapshot(verified);
            Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCallSplice(verified, 0));
            AssertUnchanged(verified, verifiedBefore, "R4 (header with verify history)");
        }

        [Fact]
        public void Case07_Undeclared_with_or_returns_names_refuse_and_the_bare_shape_splices()
        {
            var withRun = CallerRun(CallHeader(with: new Dictionary<string, string>(StringComparer.Ordinal)
                { ["targetMeasure"] = "inputs.anchor" }));
            var withBefore = Snapshot(withRun);
            var withRefusal = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCallSplice(withRun, 0));
            Assert.Contains("with:", withRefusal.Message, StringComparison.Ordinal);
            Assert.Contains("targetMeasure", withRefusal.Message, StringComparison.Ordinal);
            AssertUnchanged(withRun, withBefore, "R8 (with:)");

            var returnsRun = CallerRun(CallHeader(returns: new[] { "certificate" }));
            var returnsBefore = Snapshot(returnsRun);
            var returnsRefusal = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCallSplice(returnsRun, 0));
            Assert.Contains("returns:", returnsRefusal.Message, StringComparison.Ordinal);
            Assert.Contains("certificate", returnsRefusal.Message, StringComparison.Ordinal);
            AssertUnchanged(returnsRun, returnsBefore, "R8 (returns:)");

            // The bounded subset, asserted directly: neither field present, so it splices.
            var bare = CallerRun();
            Splice(bare);
            Assert.Equal(4, bare.Plan.Count);
        }

        [Fact]
        public void Case08_A_loop_call_template_waits_for_its_iteration_expansion()
        {
            var run = CallerRun(CallHeader(forEach: new ForEachSpec { InLiteral = new[] { "North" }, As = "region", MaxIterations = 4 }));
            var before = Snapshot(run);

            var refusal = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCallSplice(run, 0));
            Assert.Contains("forEach list", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("iteration", refusal.Message, StringComparison.OrdinalIgnoreCase);
            AssertUnchanged(run, before, "R2");
        }

        [Fact]
        public async Task Case09_A_stepless_callee_refuses_here_and_its_siblings_refuse_at_start()
        {
            var run = CallerRun(callee: Def(CalleeName, null));
            var before = Snapshot(run);
            var refusal = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCallSplice(run, 0));
            Assert.Contains("no steps", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(CalleeName, refusal.Message, StringComparison.Ordinal);
            AssertUnchanged(run, before, "R5");

            // The three round-one siblings live at start now, before a run object exists.
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "invented-r5-caller.md", string.Join("\n",
                "---", "schemaVersion: 2", "name: invented-r5-caller", "title: Caller", "strictness: off", "---", "",
                "## Step 1: Hand off", "", "```yaml step", "id: hand-off", "call:", "  workflow: invented-r5-absent", "```", "", "Hand off.", ""));
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                var start = await Assert.ThrowsAsync<InvalidOperationException>(() => e.StartWorkflowAsync("invented-r5-caller", "human"));
                Assert.Contains("is not a workflow this library holds", start.Message, StringComparison.Ordinal);
                var none = await Assert.ThrowsAsync<InvalidOperationException>(() => e.GetWorkflowRunAsync(null));
                Assert.Contains("No workflow run exists", none.Message, StringComparison.Ordinal);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // ================================================================ 10-11: cycle and depth

        [Fact]
        public void Case10_A_callee_already_on_the_frame_chain_refuses_and_matches_the_checker()
        {
            // A workflow handing off to itself: the chain root is the run's own Def.Name, so one rule catches it.
            var selfHeader = CallHeader(callee: "invented-self-call");
            var self = Def("invented-self-call", null, selfHeader, Plain("wrap-up", 2));
            var run = WorkflowOwnerFixtures.ClosureRun("wfr-self", null, (self, null));
            var before = Snapshot(run);

            var refusal = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCallSplice(run, 0));
            Assert.Contains("loop back on themselves", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("invented-self-call -> invented-self-call", refusal.Message, StringComparison.Ordinal);
            AssertUnchanged(run, before, "R6");

            // The checker reaches the same verdict for the same file, in the same words.
            var selfDef = Def("invented-self-call", null, selfHeader);
            var findings = WorkflowParser.V2Findings(selfDef, new[] { selfDef }, Array.Empty<WorkflowDef>());
            Assert.Contains(findings, f => f.Message.Contains("is this workflow itself", StringComparison.Ordinal));

            // Two levels: root -> mid -> root is a cycle the frame chain sees at the second splice.
            var chain = ChainRun("wfr-cycle-2", "invented-cycle-root", "invented-cycle-mid", "invented-cycle-root");
            Splice(chain, 0);
            var chainBefore = Snapshot(chain);
            var second = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCallSplice(chain, 1));
            Assert.Contains("loop back on themselves", second.Message, StringComparison.Ordinal);
            Assert.Contains("invented-cycle-root -> invented-cycle-mid -> invented-cycle-root", second.Message, StringComparison.Ordinal);
            AssertUnchanged(chain, chainBefore, "R6 at depth 2");
        }

        /// <summary>A run over root -> mid -> leaf, each a one-step hand-off, so the splice can be driven down
        /// the chain by hand exactly as the run-start driver will.</summary>
        private static WorkflowRunState ChainRun(string runId, params string[] names)
        {
            var defs = new List<WorkflowDef>();
            for (var i = 0; i < names.Length; i++)
            {
                var isLast = i == names.Length - 1;
                var step = isLast
                    ? Plain("do-it", 1, "Do the invented thing")
                    : CallHeader("to-" + (i + 1), 1, names[i + 1]);
                defs.Add(Def(names[i], null, step));
            }
            // A repeated name is one closure member: the cycle is in the CHAIN, not in the library.
            var members = new List<(WorkflowDef, string)>();
            foreach (var d in defs)
                if (!members.Any(m => string.Equals(m.Item1.Name, d.Name, StringComparison.OrdinalIgnoreCase)))
                    members.Add((d, null));
            return WorkflowOwnerFixtures.ClosureRun(runId, null, members.ToArray());
        }

        [Fact]
        public void Case11_Depth_is_walked_from_the_frame_chain_and_an_iteration_frame_does_not_deepen_it()
        {
            var run = ChainRun("wfr-depth", "invented-depth-root", "invented-depth-mid", "invented-depth-leaf", "invented-depth-deep");

            // depth 1 -> 2
            Splice(run, 0);
            Assert.Equal(2, run.Frames.Single(f => f.FrameId == "wfr-depth:to-1").Depth);
            Assert.Equal("to-1/to-2", run.Plan[1].InstanceId);

            // depth 2 -> 3
            Splice(run, 1);
            var third = run.Frames.Single(f => f.FrameId == "wfr-depth:to-1:to-1/to-2");
            Assert.Equal(3, third.Depth);
            Assert.Equal(3, WorkflowRunner.DepthOf(run, third));
            Assert.Equal("to-1/to-2/to-3", run.Plan[2].InstanceId);

            // depth 3 -> 4 refuses, in the checker's own words.
            var before = Snapshot(run);
            var refusal = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCallSplice(run, 2));
            Assert.Contains("run 4 deep", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("The limit is 3, counting this run as 1", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("invented-depth-root -> invented-depth-mid -> invented-depth-leaf -> invented-depth-deep",
                refusal.Message, StringComparison.Ordinal);
            AssertUnchanged(run, before, "R7");

            // An iteration frame between two call frames does NOT raise the depth: repeating a step ten times
            // is not ten hand-offs.
            var callFrame = run.Frames.Single(f => f.FrameId == "wfr-depth:to-1");
            var iteration = RunFrame.CreateIteration("wfr-depth:to-1:invented-loop:0", "invented-loop", 0,
                "region", "North", "in_progress", null, parentFrameId: callFrame.FrameId);
            run.Frames.Add(iteration);
            Assert.Equal(2, WorkflowRunner.DepthOf(run, iteration));
            Assert.Equal(1, WorkflowRunner.DepthOf(run, run.Frames[0]));
        }

        // ================================================================ 12-14: the closed boundary

        [Fact]
        public void Case12_The_call_boundary_is_closed_in_both_directions_including_scope_run()
        {
            var callee = Def(CalleeName, null,
                Plain("gather", 1, "Gather", new GateSpec
                {
                    Inputs = new[] { new GateInput { Name = "invented-callee-note", Question = "Callee note?", Required = "optional", Scope = "run" } },
                }),
                Plain("check", 2, "Check"));
            var header = CallHeader(gate: new GateSpec
            {
                Inputs = new[] { new GateInput { Name = "invented-caller-note", Question = "Caller note?", Required = "optional", Scope = "run" } },
            });
            var run = CallerRun(header, callee);
            Splice(run);

            // The caller's own run-scoped answer, given on the header row.
            run.Results[0].Answers["invented-caller-note"] = new AnswerValue { Value = "caller said this" };
            // The callee's own run-scoped answer, given on a callee row.
            run.Results[1].Answers["invented-callee-note"] = new AnswerValue { Value = "callee said this" };

            // Resolving for the CALLEE's frame sees neither the caller's row nor its run-scoped name.
            run.StepIndex = 2;
            var insideCall = WorkflowRunner.AllAnswers(run);
            Assert.False(insideCall.ContainsKey("invented-caller-note"));
            Assert.True(insideCall.ContainsKey("invented-callee-note"));

            // Resolving for the CALLER's later row sees neither the callee's row nor its run-scoped name.
            run.StepIndex = 3;
            var afterCall = WorkflowRunner.AllAnswers(run);
            Assert.True(afterCall.ContainsKey("invented-caller-note"));
            Assert.False(afterCall.ContainsKey("invented-callee-note"));

            // The call frame's own seed is empty: a callee row resolves against the callee's earlier rows only.
            var seed = new Dictionary<string, AnswerValue>(StringComparer.Ordinal);
            run.Frames.Single(f => f.ProjectionKind == "call").CopyAnswerSeedTo(seed);
            Assert.Empty(seed);
        }

        [Fact]
        public void Case13_A_callee_gate_input_is_offered_as_an_ordinary_question_on_its_own_row()
        {
            var gate = new GateSpec
            {
                Inputs = new[] { new GateInput { Name = "invented-evidence", Question = "Which invented evidence?", Required = "required" } },
                Verify = new[] { new VerifySpec { Kind = "workflow_admissible" } },
            };
            var callee = Def(CalleeName, null, Plain("gather", 1, "Gather", gate), Plain("check", 2, "Check"));
            var run = CallerRun(callee: callee);
            Splice(run);

            run.StepIndex = 1;
            run.Results[1].Status = "in_progress";
            var view = WorkflowRunner.BuildView(run);
            Assert.Equal("prove-it/gather", view.CurrentStep.StepId);
            var question = Assert.Single(view.CurrentStep.Questions);
            Assert.Equal("invented-evidence", question.Name);
            Assert.Equal("Which invented evidence?", question.Question);
            Assert.Equal("required", question.Required);
        }

        [Fact]
        public void Case14_A_callee_condition_over_a_name_no_callee_step_collects_records_not_applicable()
        {
            var callee = Def(CalleeName, null,
                Plain("gather", 1, "Gather", when: "inputs.invented-absent.answered"),
                Plain("check", 2, "Check"));
            var run = CallerRun(callee: callee);
            Splice(run);

            run.StepIndex = 1;
            WorkflowRunner.AdvancePastInapplicableSteps(run);

            Assert.Equal("not_applicable", run.Results[1].Status);
            Assert.Contains("did not apply", run.Results[1].Note, StringComparison.Ordinal);
            Assert.Contains("facts:", run.Results[1].Note, StringComparison.Ordinal);
            Assert.Equal(2, run.StepIndex);
        }

        // ================================================================ 15, 15a, 15b: ownership and evidence

        [Fact]
        public void Case15_Every_spliced_row_carries_the_frozen_closure_member_and_is_graded_by_it()
        {
            var callee = TwoStepCallee(strictness: "hard");
            var run = CallerRun(callee: callee, callerStrictness: "off", callerSettings: "off", calleeSettings: null,
                header: CallHeader());
            var member = run.OwnerClosure.Require(CalleeName);
            Splice(run);

            Assert.Same(member, run.RowOwner(run.Plan[1]));
            Assert.Same(member, run.RowOwner(run.Plan[2]));
            Assert.Same(run.Def, run.RowOwner(run.Plan[0]));
            Assert.Same(run.Def, run.RowOwner(run.Plan[3]));

            // The callee's OWN frontmatter grades its rows, not the caller's settings override.
            var gated = Def(CalleeName, "hard",
                Plain("gather", 1, "Gather", new GateSpec { Verify = new[] { new VerifySpec { Kind = "workflow_admissible" } } }),
                Plain("check", 2, "Check"));
            var graded = CallerRun(callee: gated, callerStrictness: "off", callerSettings: "off");
            Splice(graded);
            graded.Frames[0].CoverageSurface = Surface();
            WorkflowRunner.SkipStep(graded, "prove-it", "invented reason");
            // Disposal stamps the DISPOSED row's own resolution, taken through that row's owner: the callee's
            // frontmatter 'hard', never the caller's 'off' settings override.
            Assert.Equal("hard", graded.Results[1].EffectiveStrictness);
            graded.StepIndex = graded.Plan.Count;
            var gradedCert = WorkflowRunner.ComputeCertificate(graded);
            Assert.Contains(gradedCert.SkippedSteps, x => x.StepId == "prove-it/gather" && x.EffectiveStrictness == "hard");
            Assert.Contains(gradedCert.SkippedSteps, x => x.StepId == "prove-it" && x.EffectiveStrictness == null);

            // Mutating the source definition after start changes nothing: the closure member is frozen.
            callee.Strictness = "off";
            callee.Steps[0].Title = "Mutated after start";
            Assert.Equal("hard", member.Strictness);
            Assert.Equal("Gather invented evidence", run.Plan[1].Step.Title);
        }

        [Fact]
        public void Case15a_The_four_owner_refusals_are_diagnosed_apart_and_mutate_nothing()
        {
            // O1 — swapped target: a legitimate closure member whose name is not the one the header asks for.
            var o1 = CallerRun(extraMembers: Def("invented-other-callee", null, Plain("other", 1)));
            var o1Before = Snapshot(o1);
            var o1Refusal = Assert.Throws<InvalidOperationException>(
                () => WorkflowRunner.ResolveCallSplice(o1, 0, o1.OwnerClosure.Require("invented-other-callee")));
            Assert.Contains("swapped", o1Refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("prove-it", o1Refusal.Message, StringComparison.Ordinal);
            Assert.Contains(CalleeName, o1Refusal.Message, StringComparison.Ordinal);
            Assert.Contains("invented-other-callee", o1Refusal.Message, StringComparison.Ordinal);
            AssertUnchanged(o1, o1Before, "O1");

            // O2 — duplicate name: Definitions answers the target name twice, so Require's dictionary picked one.
            var duplicateA = TwoStepCallee();
            var duplicateB = TwoStepCallee();
            var o2 = WorkflowOwnerFixtures.ClosureRun("wfr-dup", null,
                (Def(CallerName, null, CallHeader(), Plain("wrap-up", 2)), null),
                (duplicateA, null), (duplicateB, null));
            var o2Before = Snapshot(o2);
            var o2Refusal = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCallSplice(o2, 0));
            Assert.Contains("2 times", o2Refusal.Message, StringComparison.Ordinal);
            Assert.Contains(CalleeName, o2Refusal.Message, StringComparison.Ordinal);
            AssertUnchanged(o2, o2Before, "O2");

            // O3 — the freeze bypass: a MUTABLE definition whose every public value matches the frozen member.
            var mutable = TwoStepCallee();
            var o3 = CallerRun();
            var frozen = o3.OwnerClosure.Require(CalleeName);
            Assert.Equal(frozen.Name, mutable.Name);
            Assert.Equal(frozen.Version, mutable.Version);
            Assert.Equal(frozen.Steps.Length, mutable.Steps.Length);
            var o3Before = Snapshot(o3);
            var o3Refusal = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCallSplice(o3, 0, mutable));
            Assert.Contains("frozen", o3Refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(CalleeName, o3Refusal.Message, StringComparison.Ordinal);
            AssertUnchanged(o3, o3Before, "O3");
            mutable.Strictness = "hard";
            mutable.Steps[0].Title = "Mutated";
            AssertUnchanged(o3, o3Before, "O3 after the candidate was mutated");

            // O4 — foreign owner: a frozen definition belonging to ANOTHER run's closure.
            var otherRun = CallerRun(runId: "wfr-other");
            var foreign = otherRun.OwnerClosure.Require(CalleeName);
            var o4 = CallerRun();
            Assert.False(o4.OwnerClosure.Contains(foreign));
            var o4Before = Snapshot(o4);
            var o4Refusal = Assert.Throws<InvalidOperationException>(() => WorkflowRunner.ResolveCallSplice(o4, 0, foreign));
            Assert.Contains("frozen", o4Refusal.Message, StringComparison.OrdinalIgnoreCase);
            AssertUnchanged(o4, o4Before, "O4");
        }

        [Fact]
        public void Case15b_A_call_frame_is_a_folded_certificate_input_not_an_ambient_one()
        {
            var verified = new GateSpec { Verify = new[] { new VerifySpec { Kind = "workflow_admissible" } } };
            var callee = Def(CalleeName, null, Plain("gather", 1, "Gather", verified), Plain("check", 2, "Check", verified));
            var run = CallerRun(callee: callee, header: CallHeader(gate: verified));
            Splice(run);

            var top = run.Frames[0];
            var call = run.Frames.Single(f => f.ProjectionKind == "call");
            top.CoverageSurface = Surface();
            call.CoverageSurface = Surface("'Product'[Category]=Bikes");

            // Evidence the call frame OWNS, on both counters the fold sums. Nonzero on the call frame and
            // different from the top frame's, so a fold that quietly dropped the call frame's contribution
            // would still produce a plausible run total and would still be caught here.
            top.AnchorRevisions.Add(InventedAnchorRevision("prove-it"));
            top.AnchorFormRepairCount = 1;
            call.AnchorRevisions.Add(InventedAnchorRevision("prove-it/gather"));
            call.AnchorRevisions.Add(InventedAnchorRevision("prove-it/check"));
            call.AnchorFormRepairCount = 2;

            // Verify evidence on the CALLEE's rows, current and archived. The frame entry's steps are clones,
            // and a clone that silently dropped either field would still pass every id and level assertion.
            run.Results[1].VerifyResults = new[]
            {
                new VerifyResult { Kind = "workflow_admissible", Status = "passed", Detail = "invented callee evidence" },
            };
            run.Results[1].VerifyHistory.Add(new VerifyAttempt
            {
                Ordinal = 1,
                TimestampUtc = "2026-01-02T03:04:05.0000000Z",
                Results = new[]
                {
                    new VerifyResult { Kind = "workflow_admissible", Status = "failed", Detail = "invented first callee attempt" },
                },
            });

            // The agent's claim rides a TOP-frame row, which is the only place the fold reads it from.
            run.Results[0].Answers["certificate"] = new AnswerValue { Value = "FULL" };

            foreach (var r in run.Results) { r.Status = "passed"; }
            run.StepIndex = run.Plan.Count;
            call.Complete("passed");

            var cert = WorkflowRunner.ComputeCertificate(run);
            Assert.NotNull(cert);
            // Weakest wins across the frames: the callee's open grain demotes the whole run.
            Assert.Equal("PARTIAL", cert.ComputedLevel);

            // ...and weakest wins again between the claim and the computed level, which is the FINAL Level the
            // record carries. ComputedLevel alone never exposes this: the claim here is the stronger of the two.
            Assert.Equal("FULL", cert.AgentClaim);
            Assert.Equal("FULL", cert.ClaimLevel);
            Assert.Equal("PARTIAL", cert.Level);

            // The run sums include the call frame's own contributions, and the call frame's entry carries them.
            Assert.Equal(3, cert.AnchorRevisionCount);
            Assert.Equal(3, cert.FormRepairCount);

            var view = WorkflowRunner.BuildView(run);
            var entry = Assert.Single(cert.Frames.Where(f => f.Kind == "call"));
            var index = Array.FindIndex(cert.Frames, f => f.Kind == "call");
            var viewFrame = view.Frames[index];
            Assert.Equal(viewFrame.Kind, entry.Kind);
            Assert.Equal(viewFrame.StepId, entry.StepId);
            Assert.Equal(viewFrame.Workflow, entry.Workflow);
            Assert.Equal(viewFrame.Depth, entry.Depth);
            Assert.Equal(viewFrame.Passed, entry.Passed);
            Assert.Equal(viewFrame.Returned, entry.Returned);
            Assert.Equal(viewFrame.State, entry.State);

            Assert.Equal("PARTIAL", entry.Level);
            Assert.Equal(new[] { "'Product'[Category]=Bikes" }, entry.OpenGrains);
            Assert.Equal(new[] { "prove-it/gather", "prove-it/check" }, entry.Steps.Select(s => s.StepId));
            foreach (var s in entry.Steps) Assert.DoesNotContain(run.Results, r => ReferenceEquals(r, s));
            Assert.Equal(2, entry.AnchorRevisionCount);
            Assert.Equal(2, entry.FormRepairCount);
            // The cloned callee step keeps BOTH evidence fields, by value and not by reference.
            var clonedGather = entry.Steps.Single(x => x.StepId == "prove-it/gather");
            var liveGather = run.Results[1];
            Assert.NotSame(liveGather, clonedGather);
            var clonedVerify = Assert.Single(clonedGather.VerifyResults);
            Assert.NotSame(liveGather.VerifyResults[0], clonedVerify);
            Assert.Equal("workflow_admissible", clonedVerify.Kind);
            Assert.Equal("passed", clonedVerify.Status);
            Assert.Equal("invented callee evidence", clonedVerify.Detail);
            var clonedAttempt = Assert.Single(clonedGather.VerifyHistory);
            Assert.NotSame(liveGather.VerifyHistory[0], clonedAttempt);
            Assert.Equal(1, clonedAttempt.Ordinal);
            Assert.Equal("2026-01-02T03:04:05.0000000Z", clonedAttempt.TimestampUtc);
            Assert.Equal("failed", Assert.Single(clonedAttempt.Results).Status);
            Assert.Equal("invented first callee attempt", clonedAttempt.Results[0].Detail);
            // The run-level lattice stays the TOP frame's own.
            Assert.Empty(cert.OpenGrains);
            Assert.Equal(top.CoverageSurface.CurrentGrid, cert.GridColumns);
            // A call frame is never counted as an iteration.
            Assert.Null(cert.IterationsTotal);
            Assert.Null(cert.FailedIterationValues);

            // The existence gate: the CALLER has no surface and the callee does, and a certificate still exists.
            var gateRun = CallerRun(callee: callee, header: CallHeader(gate: verified), runId: "wfr-exist");
            Splice(gateRun);
            gateRun.Frames.Single(f => f.ProjectionKind == "call").CoverageSurface = Surface("'Date'[Year]=2026");
            foreach (var r in gateRun.Results) r.Status = "passed";
            gateRun.StepIndex = gateRun.Plan.Count;
            gateRun.Frames.Single(f => f.ProjectionKind == "call").Complete("passed");
            var gateCert = WorkflowRunner.ComputeCertificate(gateRun);
            Assert.NotNull(gateCert);
            Assert.Equal("PARTIAL", gateCert.ComputedLevel);
            gateRun.Certificate = gateCert;
            var record = JsonSerializer.Serialize(WorkflowRunner.BuildRunRecord(gateRun));
            Assert.Contains("certificate", record, StringComparison.OrdinalIgnoreCase);

            // Weakest-wins the other way round: a claim WEAKER than the computed level is the one that lands,
            // so Level is not merely ComputedLevel under another name.
            gateRun.Results[0].Answers["certificate"] = new AnswerValue { Value = "OVERRIDDEN" };
            var weakClaim = WorkflowRunner.ComputeCertificate(gateRun);
            Assert.Equal("PARTIAL", weakClaim.ComputedLevel);
            Assert.Equal("OVERRIDDEN", weakClaim.ClaimLevel);
            Assert.Equal("OVERRIDDEN", weakClaim.Level);
        }

        /// <summary>One invented run-level anchor revision receipt, attributed to the row that changed it.</summary>
        private static AnchorRevision InventedAnchorRevision(string stepId) => new AnchorRevision
        {
            Key = "invented-anchors:" + stepId,
            AnchorsInput = "invented-anchors",
            BeforeHash = "invented-before-hash",
            AfterHash = "invented-after-hash",
            StepId = stepId,
            TimestampUtc = "2026-01-02T03:04:05.0000000Z",
        };

        // ================================================================ 16: the regression guard

        [Fact]
        public void Case16_A_run_with_no_calls_and_no_loops_is_unchanged_under_both_serializers()
        {
            WorkflowRunState NoCallRun()
            {
                var def = Def("invented-no-call", "off",
                    Plain("step-1", 1, "Step one", new GateSpec { Verify = new[] { new VerifySpec { Kind = "workflow_admissible" } } }),
                    Plain("step-2", 2, "Step two"));
                var r = new WorkflowRunState("wfr-no-call", def, null);
                r.Results[0].Status = "passed";
                r.Results[1].Status = "passed";
                r.StepIndex = 2;
                r.Status = "completed";
                return r;
            }

            var run = NoCallRun();
            foreach (var planned in run.Plan)
            {
                Assert.Same(run.Def, run.RowOwner(planned));
                Assert.False(planned.CallExpanded);
            }
            Assert.Single(run.OwnerClosure.Definitions);
            Assert.Single(run.Frames);
            Assert.Null(run.Frames[0].ParentFrameId);

            foreach (var bytes in new[]
            {
                JsonSerializer.Serialize(WorkflowRunner.BuildRunRecord(run)),
                Newtonsoft.Json.JsonConvert.SerializeObject(WorkflowRunner.BuildRunRecord(run)),
                JsonSerializer.Serialize(WorkflowRunner.BuildView(run)),
                Newtonsoft.Json.JsonConvert.SerializeObject(WorkflowRunner.BuildView(run)),
            })
            {
                Assert.DoesNotContain("Owner", bytes, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Closure", bytes, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("CallExpanded", bytes, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("ParentFrameId", bytes, StringComparison.OrdinalIgnoreCase);
                // One run, one name: the workflow name appears exactly once, as the run's own, and never a
                // second time as a row owner or a closure inventory entry.
                Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(bytes, "invented-no-call").Count);
                // No frame grouping is projected at all on a run that never left its top-level frame.
                Assert.DoesNotContain("\"frames\"", bytes, StringComparison.OrdinalIgnoreCase);
            }

            // ---- The byte guard the four assertions above cannot be: an added, removed, renamed, reordered,
            // revalued or newly-defaulted field passes every one of them. These four compare the WHOLE output
            // against bytes captured on the pre-splice parent commit, which is what the ratified contract asks
            // for: unchanged no-call BuildRunRecord and BuildView under BOTH serializers.
            var baseline = NoCallBaselineRun();
            Assert.DoesNotContain(baseline.Plan, p => p.Step.Call != null);
            Assert.All(baseline.Plan, p => Assert.False(p.CallExpanded));
            Assert.Single(baseline.Frames);
            var actual = NoCallBaselineBytes(baseline);

            // Named apart, not looped, so a failure says which of the four projections moved.
            Assert.Equal(PreSpliceRunRecordStj, actual[0]);
            Assert.Equal(PreSpliceRunRecordNewtonsoft, actual[1]);
            Assert.Equal(PreSpliceViewStj, actual[2]);
            Assert.Equal(PreSpliceViewNewtonsoft, actual[3]);

            // The comparison is worth nothing if the fixture is thin, so assert it is not: it carries a live
            // current step, answers and a decline, current and archived verify evidence, a skipped and a
            // not_applicable row, witness and anchor receipts and a computed certificate.
            Assert.All(actual, bytes =>
            {
                Assert.Contains("invented-fingerprint-01", bytes, StringComparison.Ordinal);
                Assert.Contains("invented decline reason", bytes, StringComparison.Ordinal);
                Assert.Contains("invented first attempt", bytes, StringComparison.Ordinal);
                Assert.Contains("invented-witness-hash", bytes, StringComparison.Ordinal);
                Assert.Contains("invented-after-hash", bytes, StringComparison.Ordinal);
                Assert.Contains("OVERRIDDEN", bytes, StringComparison.Ordinal);
            });
            Assert.Contains("baseline-two", actual[2], StringComparison.Ordinal);
        }

        // ================================================================ 17-21: disposal and lifecycle

        [Fact]
        public void Case17_A_header_whose_condition_is_false_disposes_its_frame_as_not_applicable()
        {
            var run = CallerRun(CallHeader(when: "inputs.invented-absent.answered"));
            Splice(run);
            WorkflowRunner.AdvancePastInapplicableSteps(run);

            Assert.Equal("not_applicable", run.Results[0].Status);
            Assert.Equal("not_applicable", run.Results[1].Status);
            Assert.Equal("not_applicable", run.Results[2].Status);
            Assert.Contains("the hand-off on 'prove-it' did not apply.", run.Results[1].Note, StringComparison.Ordinal);
            Assert.Contains("the hand-off on 'prove-it' did not apply.", run.Results[2].Note, StringComparison.Ordinal);
            Assert.Equal("passed", run.Frames.Single(f => f.ProjectionKind == "call").State);
            Assert.Equal(3, run.StepIndex);
            Assert.Equal("in_progress", run.Results[3].Status);
        }

        [Fact]
        public void Case18_A_skipped_header_disposes_its_frame_and_pins_the_caller_s_following_row()
        {
            var run = CallerRun();
            Splice(run);
            WorkflowRunner.SkipStep(run, "prove-it", "invented skip reason");

            Assert.Equal("skipped", run.Results[0].Status);
            Assert.Equal("skipped", run.Results[1].Status);
            Assert.Equal("skipped", run.Results[2].Status);
            Assert.Contains("the hand-off on 'prove-it' was skipped.", run.Results[1].Note, StringComparison.Ordinal);
            Assert.Equal("failed", run.Frames.Single(f => f.ProjectionKind == "call").State);
            // The cursor NEVER rests on a disposed callee row: assert the index itself.
            Assert.Equal(3, run.StepIndex);
            Assert.Equal("wrap-up", run.Plan[run.StepIndex].InstanceId);
            Assert.Equal("in_progress", run.Results[3].Status);

            // A callee row carrying a `when:` keeps its skip note rather than acquiring a condition note.
            var conditional = CallerRun(callee: Def(CalleeName, null,
                Plain("gather", 1, "Gather", when: "inputs.invented-absent.answered"), Plain("check", 2, "Check")));
            Splice(conditional);
            WorkflowRunner.SkipStep(conditional, "prove-it", "invented skip reason");
            Assert.Equal("skipped", conditional.Results[1].Status);
            Assert.DoesNotContain("did not apply", conditional.Results[1].Note, StringComparison.Ordinal);

            // A header that is the plan's LAST row completes the run through the same ending.
            var last = WorkflowOwnerFixtures.ClosureRun("wfr-last", null,
                (Def(CallerName, null, CallHeader()), null), (TwoStepCallee(), null));
            Splice(last);
            WorkflowRunner.SkipStep(last, "prove-it", "invented skip reason");
            Assert.Equal(last.Plan.Count, last.StepIndex);
            Assert.Equal("completed", last.Status);

            // A hard-gated callee row drives the certificate to OVERRIDDEN, at the CALLEE's own strictness.
            var hard = CallerRun(callee: Def(CalleeName, "hard",
                Plain("gather", 1, "Gather", new GateSpec { Verify = new[] { new VerifySpec { Kind = "workflow_admissible" } } }),
                Plain("check", 2, "Check")), callerStrictness: "off", callerSettings: "off");
            Splice(hard);
            hard.Frames[0].CoverageSurface = Surface();
            WorkflowRunner.SkipStep(hard, "prove-it", "invented skip reason");
            Assert.Equal("hard", hard.Results[1].EffectiveStrictness);
            hard.Results[3].Status = "passed";
            hard.StepIndex = hard.Plan.Count;
            var cert = WorkflowRunner.ComputeCertificate(hard);
            Assert.Equal("OVERRIDDEN", cert.ComputedLevel);
            Assert.Contains(cert.SkippedSteps, s => s.StepId == "prove-it/gather" && s.EffectiveStrictness == "hard");
        }

        [Fact]
        public void Case19_A_skipped_callee_row_is_not_a_disposal()
        {
            var run = CallerRun();
            Splice(run);
            run.StepIndex = 1;
            run.Results[1].Status = "in_progress";

            WorkflowRunner.SkipStep(run, "prove-it/gather", "invented row reason");

            Assert.Equal("skipped", run.Results[1].Status);
            // The remaining callee row still runs.
            Assert.Equal("in_progress", run.Results[2].Status);
            Assert.Equal(2, run.StepIndex);
            Assert.Equal("in_progress", run.Frames.Single(f => f.ProjectionKind == "call").State);

            // The frame completes FAILED only when the cursor leaves it.
            run.Results[2].Status = "passed";
            run.StepIndex = 3;
            WorkflowRunner.AdvancePastInapplicableSteps(run);
            Assert.Equal("failed", run.Frames.Single(f => f.ProjectionKind == "call").State);
        }

        [Fact]
        public void Case20_A_frame_completes_once_when_the_cursor_leaves_it_and_an_abort_leaves_it_open()
        {
            var run = CallerRun();
            Splice(run);
            run.Results[0].Status = "passed";
            run.Results[1].Status = "passed";
            run.Results[2].Status = "passed";
            run.StepIndex = 3;
            WorkflowRunner.AdvancePastInapplicableSteps(run);
            Assert.Equal("passed", run.Frames.Single(f => f.ProjectionKind == "call").State);

            // Completing the RUN completes a still-open call frame BEFORE the certificate is computed, so the
            // fold records a terminal state rather than unfinished work in a completed run.
            var completing = CallerRun(runId: "wfr-complete");
            Splice(completing);
            completing.Frames[0].CoverageSurface = Surface();
            foreach (var r in completing.Results) r.Status = "passed";
            completing.StepIndex = completing.Plan.Count - 1;
            completing.Results[completing.StepIndex].Status = "in_progress";
            WorkflowRunner.SkipStep(completing, "wrap-up", "invented last reason");
            Assert.Equal("completed", completing.Status);
            Assert.Equal("passed", completing.Frames.Single(f => f.ProjectionKind == "call").State);
            var entry = Assert.Single(completing.Certificate.Frames.Where(f => f.Kind == "call"));
            Assert.Equal("passed", entry.State);

            // Abort leaves an in-progress frame in progress: the record should show what was outstanding.
            var aborted = CallerRun(runId: "wfr-abort");
            Splice(aborted);
            aborted.StepIndex = 1;
            WorkflowRunner.Abort(aborted, "invented abort reason");
            Assert.Equal("in_progress", aborted.Frames.Single(f => f.ProjectionKind == "call").State);
        }

        [Fact]
        public async Task Case21_A_failed_callee_row_keeps_the_cursor_and_a_later_retry_completes_the_frame()
        {
            var run = CallerRun();
            Splice(run);
            run.StepIndex = 1;
            run.Results[1].Status = "failed";
            run.Results[1].Note = "invented failure";

            Assert.Equal(1, run.StepIndex);
            Assert.Equal("in_progress", run.Frames.Single(f => f.ProjectionKind == "call").State);
            // A failed row is addressed by its exact qualified id and stays retryable.
            Assert.Equal("prove-it/gather", WorkflowRunner.ValidateSubmission(run, "prove-it/gather", new Dictionary<string, AnswerValue>()));

            run.Results[1].Status = "passed";
            run.Results[2].Status = "passed";
            run.StepIndex = 3;
            WorkflowRunner.AdvancePastInapplicableSteps(run);
            Assert.Equal("passed", run.Frames.Single(f => f.ProjectionKind == "call").State);
            await Task.CompletedTask;
        }

        // ================================================================ 22-23: addressing and the unspliced row

        [Fact]
        public void Case22_A_callee_row_must_be_addressed_by_its_qualified_id()
        {
            var run = CallerRun();
            Splice(run);
            run.StepIndex = 1;
            run.Results[1].Status = "in_progress";

            var refusal = Assert.Throws<InvalidOperationException>(
                () => WorkflowRunner.ValidateSubmission(run, "gather", new Dictionary<string, AnswerValue>()));
            Assert.Contains("prove-it/gather", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Case23_An_unspliced_call_row_refuses_every_submission_and_offers_the_skip_route()
        {
            // The nested shape: the callee itself holds a `call:` row, which this unit lands UNSPLICED.
            var nested = Def(CalleeName, null, CallHeader("inner", 1, "invented-nested-leaf"), Plain("check", 2));
            var run = CallerRun(callee: nested, extraMembers: Def("invented-nested-leaf", null, Plain("leaf", 1)));
            Splice(run);
            run.StepIndex = 1;
            run.Results[1].Status = "in_progress";
            Assert.False(run.Plan[1].CallExpanded);

            var refusal = Assert.Throws<InvalidOperationException>(
                () => WorkflowRunner.ValidateSubmission(run, "prove-it/inner", new Dictionary<string, AnswerValue>()));
            Assert.Contains("prove-it/inner", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("hand-off", refusal.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("skip_workflow_step", refusal.Message, StringComparison.Ordinal);

            // The refusal reads only this row: no other row's call state is inspected, and nothing mutated.
            Assert.Equal("in_progress", run.Results[1].Status);

            // Skipping it is allowed: it has no frame to dispose, so it is an ordinary skip.
            WorkflowRunner.SkipStep(run, "prove-it/inner", "invented reason");
            Assert.Equal("skipped", run.Results[1].Status);
            Assert.Equal(2, run.StepIndex);
        }

        // ================================================================ 24-25: loops through a hand-off

        [Fact]
        public void Case24_A_loop_reached_through_a_call_extends_the_call_qualified_identity()
        {
            var loop = new WorkflowStep
            {
                Id = "review-region",
                Number = 1,
                Title = "Review invented region",
                ForEach = new ForEachSpec { InLiteral = new[] { "North", "South" }, As = "region", MaxIterations = 4 },
            };
            var callee = Def(CalleeName, null, loop);
            var run = CallerRun(callee: callee);
            var member = run.OwnerClosure.Require(CalleeName);
            Splice(run);

            run.StepIndex = 1;
            run.Results[1].Status = "in_progress";
            WorkflowRunner.ExpandCurrentForEach(run, new[] { "North", "South" });

            Assert.Equal(new[] { "prove-it", "prove-it/review-region#0", "prove-it/review-region#1", "wrap-up" },
                run.Plan.Select(p => p.InstanceId));
            Assert.Equal(new[] { "wfr-call:prove-it:prove-it/review-region:0", "wfr-call:prove-it:prove-it/review-region:1" },
                run.Plan.Skip(1).Take(2).Select(p => p.FrameId));
            // The callee's frozen owner is preserved BY REFERENCE on every row the loop splice minted.
            Assert.Same(member, run.RowOwner(run.Plan[1]));
            Assert.Same(member, run.RowOwner(run.Plan[2]));

            // The empty half: the surviving empty-loop row keeps the callee's owner too.
            var emptyLoop = new WorkflowStep
            {
                Id = "review-region",
                Number = 1,
                Title = "Review invented region",
                ForEach = new ForEachSpec { InLiteral = Array.Empty<string>(), As = "region", MaxIterations = 4 },
            };
            var emptyRun = CallerRun(callee: Def(CalleeName, null, emptyLoop), runId: "wfr-empty");
            var emptyMember = emptyRun.OwnerClosure.Require(CalleeName);
            Splice(emptyRun);
            emptyRun.StepIndex = 1;
            emptyRun.Results[1].Status = "in_progress";
            WorkflowRunner.ExpandCurrentForEach(emptyRun, Array.Empty<string>());
            Assert.Equal("not_applicable", emptyRun.Results[1].Status);
            Assert.Same(emptyMember, emptyRun.RowOwner(emptyRun.Plan[1]));

            // Two hand-offs to the same callee cannot collide.
            var twice = WorkflowOwnerFixtures.ClosureRun("wfr-twice", null,
                (Def(CallerName, null, CallHeader("call-a", 1), CallHeader("call-b", 2)), null),
                (Def(CalleeName, null, loop), null));
            Splice(twice, 0);
            Splice(twice, 2);
            Assert.Equal(new[] { "call-a", "call-a/review-region", "call-b", "call-b/review-region" },
                twice.Plan.Select(p => p.InstanceId));
            Assert.Equal(4, twice.Plan.Select(p => p.InstanceId).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(twice.Frames.Count, twice.Frames.Select(f => f.FrameId).Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void Case25_A_loop_expanding_inside_a_call_frame_cannot_complete_it_early()
        {
            var loop = new WorkflowStep
            {
                Id = "review-region",
                Number = 1,
                Title = "Review invented region",
                ForEach = new ForEachSpec { InLiteral = new[] { "North", "South" }, As = "region", MaxIterations = 4 },
            };
            var run = CallerRun(callee: Def(CalleeName, null, loop));
            Splice(run);
            run.StepIndex = 1;
            run.Results[1].Status = "in_progress";
            WorkflowRunner.ExpandCurrentForEach(run, new[] { "North", "South" });

            var call = run.Frames.Single(f => f.ProjectionKind == "call");
            // The template row LEFT the call frame's own row set the moment it expanded. Judged over FrameId
            // equality alone the set would be empty and the frame would complete while the run is still inside it.
            Assert.DoesNotContain(run.Plan, p => p.FrameId == call.FrameId);
            WorkflowRunner.AdvancePastInapplicableSteps(run);
            Assert.Equal("in_progress", call.State);

            run.Results[1].Status = "passed";
            run.StepIndex = 2;
            WorkflowRunner.AdvancePastInapplicableSteps(run);
            Assert.Equal("in_progress", call.State);

            run.Results[2].Status = "passed";
            run.StepIndex = 3;
            WorkflowRunner.AdvancePastInapplicableSteps(run);
            Assert.Equal("passed", call.State);
        }

        // ================================================================ 28-30: the commit's own authority

        [Fact]
        public void Case28_A_resolve_that_went_stale_before_the_commit_refuses_and_mutates_nothing()
        {
            // A resolve is a SNAPSHOT the caller then holds. Everything R4 refuses at resolve can arrive
            // between the two calls, and the commit is the boundary that has to say no.
            void Stale(string because, Action<WorkflowRunState> drift, string expectedFragment)
            {
                var run = CallerRun(runId: "wfr-stale");
                var resolved = WorkflowRunner.ResolveCallSplice(run, 0);
                drift(run);
                var before = Snapshot(run);
                var refusal = Assert.Throws<InvalidOperationException>(
                    () => WorkflowRunner.SpliceCallAt(run, 0, resolved));
                Assert.Contains(expectedFragment, refusal.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("prove-it", refusal.Message, StringComparison.Ordinal);
                AssertUnchanged(run, before, because);
                // Nothing half-built either: no frame, no extra row, no expanded header.
                Assert.Single(run.Frames);
                Assert.Equal(2, run.Plan.Count);
                Assert.False(run.Plan[0].CallExpanded);
            }

            // R4a — the cursor moved past the header. Committing here would insert rows below an index the run
            // has already passed and shift the row the cursor previously named.
            Stale("a stale resolve under a moved cursor", r => r.StepIndex = 1, "the current plan index is 1");
            // R4b — the header ran.
            Stale("a stale resolve over a header that has run", r => r.Results[0].Status = "passed", "already run");
            // R4c — the header collected an answer.
            Stale("a stale resolve over an answered header",
                r => r.Results[0].Answers["invented-note"] = new AnswerValue { Value = "given after the resolve" },
                "already holds answers");
            // R4d — the header collected verify history.
            Stale("a stale resolve over a header with verify history",
                r => r.Results[0].VerifyHistory.Add(new VerifyAttempt()), "already holds verify history");
            // R3 — someone else committed the same hand-off first.
            var raced = CallerRun(runId: "wfr-raced");
            var first = WorkflowRunner.ResolveCallSplice(raced, 0);
            var second = WorkflowRunner.ResolveCallSplice(raced, 0);
            WorkflowRunner.SpliceCallAt(raced, 0, first);
            var racedBefore = Snapshot(raced);
            Assert.Contains("already", Assert.Throws<InvalidOperationException>(
                () => WorkflowRunner.SpliceCallAt(raced, 0, second)).Message, StringComparison.OrdinalIgnoreCase);
            AssertUnchanged(raced, racedBefore, "a second commit of the same resolve");

            // The refusal is not a dead end: resolving again against the live run still splices.
            var recovered = CallerRun(runId: "wfr-recovered");
            var doomed = WorkflowRunner.ResolveCallSplice(recovered, 0);
            recovered.Results[0].Answers["invented-note"] = new AnswerValue { Value = "given after the resolve" };
            Assert.Throws<InvalidOperationException>(() => WorkflowRunner.SpliceCallAt(recovered, 0, doomed));
            recovered.Results[0].Answers.Clear();
            WorkflowRunner.SpliceCallAt(recovered, 0, WorkflowRunner.ResolveCallSplice(recovered, 0));
            Assert.Equal(new[] { "prove-it", "prove-it/gather", "prove-it/check", "wrap-up" },
                recovered.Plan.Select(x => x.InstanceId));
        }

        [Fact]
        public void Case29_A_blueprint_whose_rows_or_owner_were_replaced_refuses_before_any_mutation()
        {
            // The blueprint is a value the caller holds, so the commit proves the rows against the live run
            // rather than trusting the labels it was handed.
            void Forged(string because, Func<CallSplice, WorkflowRunState, CallSplice> forge, string expectedFragment)
            {
                var run = CallerRun(runId: "wfr-forged");
                var resolved = WorkflowRunner.ResolveCallSplice(run, 0);
                var before = Snapshot(run);
                var refusal = Assert.Throws<InvalidOperationException>(
                    () => WorkflowRunner.SpliceCallAt(run, 0, forge(resolved, run)));
                Assert.Contains(expectedFragment, refusal.Message, StringComparison.OrdinalIgnoreCase);
                AssertUnchanged(run, before, because);
                Assert.Single(run.Frames);
                Assert.Equal(2, run.Plan.Count);
            }

            // Substituted steps: the frozen owner's label kept, an unfrozen twin's steps supplied. Every public
            // value matches, which is exactly why only reference identity catches it.
            Forged("a blueprint whose callee steps came from an unfrozen twin", (resolved, run) =>
            {
                var twin = TwoStepCallee();
                return new CallSplice(resolved.Owner, resolved.Depth, resolved.FrameId, resolved.HeaderInstanceId,
                    resolved.Rows.Select((r, i) => (r.InstanceId, twin.Steps[i])).ToList());
            }, "no longer what this run resolves");

            // Reordered steps under the RIGHT instance ids: each step really is the frozen owner's own, so a
            // membership check passes and only order tells the two rows apart.
            Forged("a blueprint whose callee steps were transposed", (resolved, run) => new CallSplice(
                resolved.Owner, resolved.Depth, resolved.FrameId, resolved.HeaderInstanceId,
                new[]
                {
                    (resolved.Rows[0].InstanceId, resolved.Rows[1].Step),
                    (resolved.Rows[1].InstanceId, resolved.Rows[0].Step),
                }), "no longer what this run resolves");

            // A dropped row: the frame would complete over a plan that never held the callee's second step.
            Forged("a blueprint missing a callee row", (resolved, run) => new CallSplice(
                resolved.Owner, resolved.Depth, resolved.FrameId, resolved.HeaderInstanceId,
                resolved.Rows.Take(1).ToList()), "1 callee row");

            // A swapped owner: a legitimate frozen member of this run's closure, under the wrong name.
            Forged("a blueprint whose owner is another closure member", (resolved, run) => new CallSplice(
                run.OwnerClosure.Require(CallerName), resolved.Depth, resolved.FrameId, resolved.HeaderInstanceId,
                resolved.Rows), "swapped");

            // A same-named unfrozen definition, and a definition belonging to no closure at all.
            Forged("a blueprint whose owner is an unfrozen twin", (resolved, run) => new CallSplice(
                TwoStepCallee(), resolved.Depth, resolved.FrameId, resolved.HeaderInstanceId,
                resolved.Rows), "frozen");
            Forged("a blueprint with no owner at all", (resolved, run) => new CallSplice(
                null, resolved.Depth, resolved.FrameId, resolved.HeaderInstanceId, resolved.Rows),
                "no longer what this run resolves");

            // A forged frame id and a forged depth are the same class of lie and get the same answer.
            Forged("a blueprint naming a frame id this run does not resolve", (resolved, run) => new CallSplice(
                resolved.Owner, resolved.Depth, "wfr-forged:invented-other-frame", resolved.HeaderInstanceId,
                resolved.Rows), "frame id");
            Forged("a blueprint claiming a depth this run does not resolve", (resolved, run) => new CallSplice(
                resolved.Owner, resolved.Depth + 1, resolved.FrameId, resolved.HeaderInstanceId,
                resolved.Rows), "depth");

            // And the blueprint itself cannot be edited after the fact: it is a value object with no setters
            // over a wrapped row list, so the only route left is the deliberate reconstruction above.
            var members = System.Reflection.RuntimeReflectionExtensions
                .GetRuntimeProperties(typeof(CallSplice)).ToArray();
            Assert.NotEmpty(members);
            Assert.All(members, x => Assert.Null(x.SetMethod));
            var live = WorkflowRunner.ResolveCallSplice(CallerRun(runId: "wfr-immutable"), 0);
            Assert.Throws<NotSupportedException>(
                () => ((IList<(string InstanceId, WorkflowStep Step)>)live.Rows).Clear());
        }

        /// <summary>root -> mid -> inner, with a loop in the innermost callee, so ONE outer call frame owns a
        /// descendant call frame AND descendant iteration frames at the same time. Each definition carries its
        /// own strictness so a disposed row's recorded resolution can only have come from its own owner.</summary>
        private static WorkflowRunState NestedCallRun(string runId)
        {
            var verified = new GateSpec { Verify = new[] { new VerifySpec { Kind = "workflow_admissible" } } };
            var inner = Def("invented-nested-inner", "hard",
                Plain("inner-one", 1, "Inner one", verified),
                new WorkflowStep
                {
                    Id = "review-region",
                    Number = 2,
                    Title = "Review invented region",
                    ForEach = new ForEachSpec { InLiteral = new[] { "North", "South" }, As = "region", MaxIterations = 4 },
                });
            var mid = Def("invented-nested-mid", "warn",
                Plain("mid-one", 1, "Mid one", verified),
                CallHeader("mid-hand-off", 2, "invented-nested-inner"),
                Plain("mid-three", 3, "Mid three"));
            var root = Def("invented-nested-root", "off",
                CallHeader("prove-it", 1, "invented-nested-mid"),
                Plain("wrap-up", 2, "Wrap up"));
            var run = WorkflowOwnerFixtures.ClosureRun(runId, null, (root, null), (mid, null), (inner, null));

            Splice(run, 0);   // prove-it -> the mid rows
            Splice(run, 2);   // prove-it/mid-hand-off -> the inner rows
            // The loop expands inside the inner call frame, which is what makes the descendants MIXED.
            run.StepIndex = 4;
            run.Results[4].Status = "in_progress";
            WorkflowRunner.ExpandCurrentForEach(run, new[] { "North", "South" });
            // Rewind: the expansion is structure, and every question below is about a cursor that has not yet
            // reached the hand-off.
            run.StepIndex = 0;
            foreach (var r in run.Results) r.Status = "pending";
            run.Results[0].Status = "in_progress";
            return run;
        }

        [Fact]
        public void Case30_An_outer_call_frame_disposes_its_call_and_iteration_descendants_together()
        {
            var run = NestedCallRun("wfr-nested-dispose");
            run.Frames[0].CoverageSurface = Surface();

            Assert.Equal(
                new[]
                {
                    "prove-it", "prove-it/mid-one", "prove-it/mid-hand-off",
                    "prove-it/mid-hand-off/inner-one",
                    "prove-it/mid-hand-off/review-region#0", "prove-it/mid-hand-off/review-region#1",
                    "prove-it/mid-three", "wrap-up",
                },
                run.Plan.Select(p => p.InstanceId));
            var midFrame = run.Frames.Single(f => f.Workflow == "invented-nested-mid");
            var innerFrame = run.Frames.Single(f => f.Workflow == "invented-nested-inner");
            var iterations = run.Frames.Where(f => f.ProjectionKind == "iteration").ToArray();
            Assert.Equal(2, iterations.Length);
            Assert.Equal(2, midFrame.Depth);
            Assert.Equal(3, innerFrame.Depth);
            Assert.All(iterations, f => Assert.Equal(innerFrame.FrameId, f.ParentFrameId));

            WorkflowRunner.SkipStep(run, "prove-it", "invented nested skip reason");

            // Every descendant row is disposed, whichever frame kind it belongs to.
            Assert.All(Enumerable.Range(0, 7), i => Assert.Equal("skipped", run.Results[i].Status));
            Assert.All(Enumerable.Range(1, 6), i =>
                Assert.Contains("the hand-off on 'prove-it' was skipped.", run.Results[i].Note, StringComparison.Ordinal));
            // The reason on the header is its OWN, and it is not the disposal note.
            Assert.StartsWith("skipped: invented nested skip reason", run.Results[0].Note, StringComparison.Ordinal);

            // Strictness is each disposed row's own, resolved through that row's owner: the innermost callee's
            // 'hard' and the middle callee's 'warn', never the root's 'off'. A gateless row records nothing.
            Assert.Equal("warn", run.Results[1].EffectiveStrictness);           // mid-one, gated, owner mid
            Assert.Equal("hard", run.Results[3].EffectiveStrictness);           // inner-one, gated, owner inner
            Assert.Null(run.Results[6].EffectiveStrictness);                    // mid-three, no gate
            Assert.Null(run.Results[0].EffectiveStrictness);                    // the header itself, no gate

            // Every descendant frame is terminal, call and iteration alike, and the verdict is the disposal's.
            Assert.Equal("failed", midFrame.State);
            Assert.Equal("failed", innerFrame.State);
            Assert.All(iterations, f => Assert.Equal("failed", f.State));

            // The cursor never rests on a disposed row: it lands on the caller's own following row.
            Assert.Equal(7, run.StepIndex);
            Assert.Equal("wrap-up", run.Plan[run.StepIndex].InstanceId);
            Assert.Equal("in_progress", run.Results[7].Status);

            // Only now, with every status, reason, strictness, cursor and frame state settled, the certificate.
            run.Results[7].Status = "passed";
            run.StepIndex = run.Plan.Count;
            var cert = WorkflowRunner.ComputeCertificate(run);
            Assert.Equal("OVERRIDDEN", cert.ComputedLevel);
            Assert.Equal(2, cert.Frames.Count(f => f.Kind == "call"));
            Assert.All(cert.Frames.Where(f => f.Kind == "call"), f => Assert.Equal("failed", f.State));
            Assert.Contains(cert.SkippedSteps,
                x => x.StepId == "prove-it/mid-hand-off/inner-one" && x.EffectiveStrictness == "hard");
            Assert.Contains(cert.SkippedSteps,
                x => x.StepId == "prove-it/mid-one" && x.EffectiveStrictness == "warn");
        }

        [Fact]
        public void Case31_Walking_out_of_nested_call_frames_completes_the_inner_one_first()
        {
            var run = NestedCallRun("wfr-nested-exit");
            run.Frames[0].CoverageSurface = Surface();
            var midFrame = run.Frames.Single(f => f.Workflow == "invented-nested-mid");
            var innerFrame = run.Frames.Single(f => f.Workflow == "invented-nested-inner");
            var iterations = run.Frames.Where(f => f.ProjectionKind == "iteration").ToArray();

            foreach (var r in run.Results) r.Status = "passed";
            // The iteration frames close on their own submissions, as they do on the ordinary path; this test is
            // about the two CALL frames the cursor walks out of.
            foreach (var f in iterations) f.Complete("passed");

            // Inside the inner frame still: neither call frame is finished.
            run.StepIndex = 5;
            WorkflowRunner.AdvancePastInapplicableSteps(run);
            Assert.Equal("in_progress", innerFrame.State);
            Assert.Equal("in_progress", midFrame.State);

            // Out of the inner frame and back into the middle one: the inner completes, the outer does not.
            run.StepIndex = 6;
            WorkflowRunner.AdvancePastInapplicableSteps(run);
            Assert.Equal("passed", innerFrame.State);
            Assert.Equal("in_progress", midFrame.State);

            // Out of the middle frame: it completes too, and the run is still active on the caller's own row.
            run.StepIndex = 7;
            WorkflowRunner.AdvancePastInapplicableSteps(run);
            Assert.Equal("passed", midFrame.State);
            Assert.Equal("active", run.Status);
            Assert.Equal("wrap-up", run.Plan[run.StepIndex].InstanceId);
            Assert.All(run.Results, r => Assert.Equal("passed", r.Status));

            // Ordinary completion, and every frame is terminal BEFORE the certificate is computed: the stored
            // certificate is the proof of the ordering, since it reads frame state directly.
            run.Results[7].Status = "in_progress";
            WorkflowRunner.SkipStep(run, "wrap-up", "invented last reason");
            Assert.Equal("completed", run.Status);
            Assert.All(run.Frames.Where(f => f.ProjectionKind != null), f => Assert.NotEqual("in_progress", f.State));

            var cert = run.Certificate;
            Assert.NotNull(cert);
            Assert.Equal(2, cert.Frames.Count(f => f.Kind == "call"));
            Assert.All(cert.Frames.Where(f => f.Kind == "call"), f => Assert.Equal("passed", f.State));
            Assert.Equal(new[] { "invented-nested-mid", "invented-nested-inner" },
                cert.Frames.Where(f => f.Kind == "call").Select(f => f.Workflow));
            Assert.Equal(new int?[] { 2, 3 }, cert.Frames.Where(f => f.Kind == "call").Select(f => f.Depth));
            // The two iteration frames are counted as iterations, and the two call frames are not.
            Assert.Equal(2, cert.IterationsTotal);
            Assert.Equal(2, cert.IterationsPassed);
        }

        // ================================================================ 26-27: the record and the closed door

        [Fact]
        public void Case26_The_view_and_the_record_carry_the_qualified_ids_under_the_caller_s_name()
        {
            var run = CallerRun();
            Splice(run);
            foreach (var r in run.Results) r.Status = "passed";
            run.StepIndex = run.Plan.Count;
            run.Status = "completed";

            var view = WorkflowRunner.BuildView(run);
            Assert.Equal(CallerName, view.Workflow);
            Assert.Equal(new[] { "prove-it", "prove-it/gather", "prove-it/check", "wrap-up" }, view.Steps.Select(s => s.StepId));
            Assert.Equal(4, view.TotalSteps);
            Assert.True(view.TotalStepsProvisional != true);

            var record = JsonSerializer.Serialize(WorkflowRunner.BuildRunRecord(run));
            Assert.Contains("prove-it/gather", record, StringComparison.Ordinal);
            Assert.Contains("prove-it/check", record, StringComparison.Ordinal);
            Assert.Contains(CallerName, record, StringComparison.Ordinal);
            Assert.DoesNotContain(CalleeName, record, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Case27_start_workflow_expands_a_call_bearing_file_before_its_header_runs()
        {
            Assert.Empty(WorkflowParser.UnexecutableControlFields(Def(CallerName, null, CallHeader(), Plain("wrap-up", 2))));

            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "invented-door-caller.md", string.Join("\n",
                "---", "schemaVersion: 2", "name: invented-door-caller", "title: Caller", "strictness: off", "---", "",
                "## Step 1: Hand off", "", "```yaml step", "id: hand-off", "call:", "  workflow: invented-door-callee", "```", "", "Hand off.", ""));
            WriteUserWorkflow(ws, "invented-door-callee.md", string.Join("\n",
                "---", "schemaVersion: 2", "name: invented-door-callee", "title: Callee", "strictness: off", "---", "",
                "## Step 1: Do", "", "Do a thing.", ""));
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                var started = await e.StartWorkflowAsync("invented-door-caller", "human");
                Assert.Equal("hand-off", started.CurrentStep.StepId);
                Assert.Equal(2, started.TotalSteps);
                Assert.Single(started.Frames);
                Assert.All(started.Steps, step => Assert.Empty(step.Answers));
                var entered = await e.SubmitWorkflowStepAsync(started.RunId, "hand-off", "{}", "human");
                Assert.StartsWith("hand-off/", entered.CurrentStep.StepId);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }
    }
}
