using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using EvidenceArtifact = Semanticus.Engine.Evidence.EvidenceArtifact;
using EvidenceHash = Semanticus.Engine.Evidence.EvidenceHash;
using EvidenceVerdict = Semanticus.Engine.Evidence.Verdict;
using WorkflowEvidenceRenderer = Semanticus.Engine.Evidence.WorkflowEvidenceRenderer;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class WorkflowEvidenceTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        [Fact]
        public void Artifact_preserves_instructions_answers_declines_gates_and_accountable_skip()
        {
            var def = new WorkflowDef
            {
                Name = "close-check",
                Title = "Close check",
                Version = 3,
                Strictness = "hard",
                Steps = new[]
                {
                    new WorkflowStep { Id = "step-1", Title = "Confirm total", Instructions = "Compare the signed total.", Ops = new[] { "probe_measure" } },
                    new WorkflowStep { Id = "step-2", Title = "Publish", Instructions = "Publish after review.", Ops = new[] { "deploy_live" } },
                },
            };
            var run = new WorkflowRunStore().Start(def, null);
            run.ModelName = "Contoso";
            run.ModelFingerprint = "fp-contoso";
            run.SessionId = "s1";
            run.Origin = "human";
            run.Results[0].Status = "passed";
            run.Results[0].EffectiveStrictness = "hard";
            run.Results[0].Answers = new Dictionary<string, AnswerValue>
            {
                ["signedTotal"] = new AnswerValue { Value = "1234.50" },
                ["comment"] = new AnswerValue { Declined = true, DeclineReason = "not available at close" },
            };
            run.Results[0].VerifyResults = new[] { new VerifyResult { Kind = "dax_probe", Status = "passed", Detail = "matched 1234.50" } };
            run.Results[1].Status = "skipped";
            run.Results[1].Note = "skipped: release window closed";
            run.Status = "completed";
            run.StepIndex = 2;
            run.FinishedUtc = "2026-07-12T12:00:00Z";

            var artifact = EvidenceArtifact.Seal(WorkflowEvidenceRenderer.Build(run));
            var doc = EvidenceHash.Deserialize(artifact.Json);

            Assert.Equal("workflow-run", doc.Kind);
            Assert.Equal(EvidenceVerdict.Overridden, doc.Verdict);
            Assert.Contains("release window closed", doc.OverrideReason);
            Assert.Contains("Compare the signed total.", artifact.Json);
            Assert.Contains("1234.50", artifact.Json);
            Assert.Contains("not available at close", artifact.Json);
            Assert.Contains("matched 1234.50", artifact.Json);
            Assert.Equal(artifact.ContentHash, EvidenceHash.HashOfJsonText(artifact.Json));
            Assert.Contains(artifact.ContentHash, artifact.Html);
        }

        [Fact]
        public async Task Export_is_terminal_and_bound_to_the_owning_model()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Pro());
            var opened = await engine.OpenAsync(TestModels.FindBim());
            var active = await engine.StartWorkflowAsync("new-measure", "human");

            var early = await engine.ExportWorkflowEvidenceAsync(active.RunId);
            Assert.Contains("still active", early.Note);
            Assert.Null(early.Json);

            await engine.AbortWorkflowAsync(active.RunId, "validation fixture", "human");
            var artifact = await engine.ExportWorkflowEvidenceAsync(active.RunId);
            Assert.Null(artifact.Error);
            Assert.Contains(opened.ModelName, artifact.Json);
            Assert.Contains("validation fixture", artifact.Json);

            await engine.CreateModelAsync("Different model", 1604);
            var stale = await engine.ExportWorkflowEvidenceAsync(active.RunId);
            Assert.Contains("different model", stale.Note);
            Assert.Null(stale.Json);
        }
    }
}
