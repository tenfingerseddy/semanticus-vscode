using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class WorkflowRunRecordStabilityTests
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

        // Acceptance check 34: capture today's no-loop v7 bytes before the fold exists, then keep them.
        [Fact]
        public async Task No_loop_v7_terminal_record_is_byte_identical_to_the_recorded_golden()
        {
            var json = await SerializeNormalizedNoLoopV7Record();
            var golden = File.ReadAllText(V7GoldenPath());
            Assert.Equal(golden, json);
        }

        [Fact]
        public async Task No_loop_certificate_omits_every_folded_member()
        {
            var json = await SerializeNormalizedNoLoopV7Record();
            Assert.DoesNotContain("\"Frames\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"IterationsTotal\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"IterationsPassed\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"IterationsFailed\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"FailedIterationValues\"", json, StringComparison.Ordinal);
        }

        private static string V7GoldenPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "Semanticus.Tests", "goldens", "workflow-v7-no-loop-record.json");
        }

        private static async Task<string> SerializeNormalizedNoLoopV7Record()
        {
            var run = new WorkflowRunState("wfr-v7-noloop", new WorkflowDef
            {
                Name = "certificate-test",
                Title = "Certificate test",
                Strictness = "hard",
                Steps = new[] { Step(1) },
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
    }
}
