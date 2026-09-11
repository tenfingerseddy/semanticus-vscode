using System;
using System.Collections.Generic;
using System.Linq;
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
        public void Call_evidence_identifies_frozen_owners_and_qualified_steps_with_identical_titles()
        {
            var caller = new WorkflowDef
            {
                Name = "review-model", Version = 2, Strictness = "off",
                Steps = new[] { new WorkflowStep
                {
                    Id = "review", Title = "Review", Call = new CallSpec { Workflow = "review-table" },
                } },
            };
            var callee = new WorkflowDef
            {
                Name = "review-table", Version = 7, Strictness = "hard",
                Steps = new[] { new WorkflowStep { Id = "review", Title = "Review" } },
            };
            var run = WorkflowOwnerFixtures.ClosureRun("wfr-owners", null, (caller, null), (callee, "warn"));
            WorkflowRunner.InitializeCalls(run);
            callee.Version = 99;
            var doc = WorkflowEvidenceRenderer.Build(run);
            var owner = Assert.IsType<Semanticus.Engine.Evidence.KeyValueSection>(
                doc.Sections.Single(x => x.Title == "Workflow definition: review-table"));
            Assert.Contains(owner.Pairs, x => x.Key == "Version" && x.Value == "7");
            Assert.Contains(owner.Pairs, x => x.Key == "Settings strictness" && x.Value == "warn");
            var step = Assert.IsType<Semanticus.Engine.Evidence.KeyValueSection>(
                doc.Sections.Single(x => x.Title == "Step 2: Review"));
            Assert.Contains(step.Pairs, x => x.Key == "Step" && x.Value == "review/review");
            Assert.Contains(step.Pairs, x => x.Key == "Workflow" && x.Value == "review-table");
            Assert.Contains(step.Pairs, x => x.Key == "Workflow version" && x.Value == "7");
            Assert.Contains(step.Pairs, x => x.Key == "Definition strictness" && x.Value == "hard");
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
        public void Partial_folded_certificate_never_exports_as_verified_and_keeps_certificate_frames()
        {
            var step = new WorkflowStep { Id = "review-region", Title = "Review region" };
            var run = new WorkflowRunStore().Start(new WorkflowDef
            {
                Name = "regional-proof",
                Title = "Regional proof",
                Version = 7,
                Steps = new[] { step },
            }, null);
            run.Results[0].Status = "passed";
            run.Status = "completed";
            run.StepIndex = 1;
            run.FinishedUtc = "2026-08-29T12:00:00Z";
            run.Certificate = new WorkflowCertificate
            {
                Level = "PARTIAL",
                AgentClaim = "FULL, all regions checked",
                ClaimLevel = "FULL",
                ComputedLevel = "PARTIAL",
                GridColumns = new[] { "'Region'[Name]" },
                Coverage = new[]
                {
                    new CertificateGrainCoverage { ShapeId = "axis:'Region'[Name]", State = "open", Open = true },
                },
                OpenGrains = new[] { "axis:'Region'[Name]" },
                AnchorRevisionCount = 2,
                FormRepairCount = 1,
                IterationsTotal = 2,
                IterationsPassed = 1,
                IterationsFailed = 1,
                FailedIterationValues = new[] { "South" },
                Frames = new[]
                {
                    new CertificateFrame
                    {
                        Kind = "iteration",
                        StepId = "review-region",
                        State = "failed",
                        Level = "PARTIAL",
                        IterationIndex = 1,
                        LoopVariable = "region",
                        LoopValue = "South",
                        Steps = new[] { run.Results[0].Clone() },
                        GridColumns = new[] { "'Region'[Name]" },
                        OpenGrains = new[] { "axis:'Region'[Name]" },
                    },
                },
            };

            var artifact = EvidenceArtifact.Seal(WorkflowEvidenceRenderer.Build(run));
            var doc = EvidenceHash.Deserialize(artifact.Json);

            Assert.Equal(EvidenceVerdict.Unknown, doc.Verdict);
            Assert.NotEqual(EvidenceVerdict.Verified, doc.Verdict);
            var certificate = Assert.IsType<Semanticus.Engine.Evidence.KeyValueSection>(
                doc.Sections.Single(x => x.Title == "Certificate"));
            Assert.Contains(certificate.Pairs, x => x.Key == "Level" && x.Value == "PARTIAL");
            Assert.Contains(certificate.Pairs, x => x.Key == "Failed iteration values" && x.Value == "South");
            var frame = Assert.IsType<Semanticus.Engine.Evidence.KeyValueSection>(
                doc.Sections.Single(x => x.Title == "Certificate frame 1: iteration review-region"));
            Assert.Contains(frame.Pairs, x => x.Key == "Level" && x.Value == "PARTIAL");
            Assert.Contains(frame.Pairs, x => x.Key == "Loop value" && x.Value == "South");
            Assert.Contains(frame.Pairs, x => x.Key == "Open grains" && x.Value == "axis:'Region'[Name]");
        }

        // The certificate ladder is deliberately blind to warn-strictness rows (FrameRowStatus only inspects
        // IsHardStep/IsHardVerifyStep), and the spec says a warn-only skip does not demote the certificate.
        // So a FULL certificate must never be allowed to lift the export above what the steps themselves prove.
        [Fact]
        public void A_full_certificate_never_lifts_a_warn_skip_or_a_warn_gate_failure_to_verified()
        {
            var doc = BuildWithFullCertificate(run =>
            {
                run.Results[0].Status = "passed";
                run.Results[0].EffectiveStrictness = "warn";
                run.Results[0].VerifyResults = new[] { new VerifyResult { Kind = "dax_probe", Status = "passed", Detail = "matched 1" } };
                run.Results[1].Status = "skipped";
                run.Results[1].EffectiveStrictness = "warn";
                run.Results[1].Note = "skipped: warn gate, region owner away";
            }, skipped: new[]
            {
                new CertificateSkippedStep
                {
                    StepId = "step-2",
                    Title = "Publish",
                    Reason = "warn gate, region owner away",
                    EffectiveStrictness = "warn",
                },
            });

            Assert.NotEqual(EvidenceVerdict.Verified, doc.Verdict);
            Assert.Equal(EvidenceVerdict.Overridden, doc.Verdict);
            Assert.Contains("region owner away", doc.OverrideReason);

            var warnVerifyFailed = BuildWithFullCertificate(run =>
            {
                run.Results[0].Status = "passed";
                run.Results[0].EffectiveStrictness = "warn";
                run.Results[0].VerifyResults = new[]
                {
                    new VerifyResult { Kind = "dax_probe", Status = "failed", Detail = "warn gate, not enforced" },
                };
                run.Results[1].Status = "passed";
            }, skipped: Array.Empty<CertificateSkippedStep>());

            Assert.NotEqual(EvidenceVerdict.Verified, warnVerifyFailed.Verdict);
            Assert.Equal(EvidenceVerdict.NeedsReview, warnVerifyFailed.Verdict);

            var warnVerifyUnavailable = BuildWithFullCertificate(run =>
            {
                run.Results[0].Status = "passed";
                run.Results[0].EffectiveStrictness = "warn";
                run.Results[0].VerifyResults = new[]
                {
                    new VerifyResult { Kind = "dax_probe", Status = "unavailable", Detail = "no live endpoint" },
                };
                run.Results[1].Status = "passed";
            }, skipped: Array.Empty<CertificateSkippedStep>());

            Assert.NotEqual(EvidenceVerdict.Verified, warnVerifyUnavailable.Verdict);
            Assert.Equal(EvidenceVerdict.Unknown, warnVerifyUnavailable.Verdict);
        }

        // A FULL certificate over steps that all genuinely passed still exports Verified: the fold may only
        // ever weaken the headline, never strengthen it, and never gratuitously weaken it either.
        [Fact]
        public void A_full_certificate_over_clean_steps_still_exports_verified()
        {
            var doc = BuildWithFullCertificate(run =>
            {
                run.Results[0].Status = "passed";
                run.Results[0].VerifyResults = new[] { new VerifyResult { Kind = "dax_probe", Status = "passed", Detail = "matched 1" } };
                run.Results[1].Status = "passed";
                run.Results[1].VerifyResults = new[] { new VerifyResult { Kind = "dax_probe", Status = "passed", Detail = "matched 1" } };
            }, skipped: Array.Empty<CertificateSkippedStep>());

            Assert.Equal(EvidenceVerdict.Verified, doc.Verdict);
            Assert.Null(doc.OverrideReason);
        }

        private static Semanticus.Engine.Evidence.EvidenceDoc BuildWithFullCertificate(
            Action<WorkflowRunState> arrange, CertificateSkippedStep[] skipped)
        {
            var run = new WorkflowRunStore().Start(new WorkflowDef
            {
                Name = "regional-proof",
                Title = "Regional proof",
                Version = 7,
                Strictness = "warn",
                Steps = new[]
                {
                    new WorkflowStep { Id = "step-1", Title = "Confirm total" },
                    new WorkflowStep { Id = "step-2", Title = "Publish" },
                },
            }, null);
            arrange(run);
            run.Status = "completed";
            run.StepIndex = 2;
            run.FinishedUtc = "2026-08-30T12:00:00Z";
            run.Certificate = new WorkflowCertificate
            {
                Level = "FULL",
                AgentClaim = "FULL, everything checked",
                ClaimLevel = "FULL",
                ComputedLevel = "FULL",
                GridColumns = new[] { "'Region'[Name]" },
                SkippedSteps = skipped,
            };

            var artifact = EvidenceArtifact.Seal(WorkflowEvidenceRenderer.Build(run));
            return EvidenceHash.Deserialize(artifact.Json);
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
