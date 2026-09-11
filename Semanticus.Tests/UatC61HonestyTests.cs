using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using TabularEditor.TOMWrapper;
using Xunit;
using EvidenceArtifact = Semanticus.Engine.Evidence.EvidenceArtifact;
using EvidenceHash = Semanticus.Engine.Evidence.EvidenceHash;
using EvidenceVerdict = Semanticus.Engine.Evidence.Verdict;
using WorkflowEvidenceRenderer = Semanticus.Engine.Evidence.WorkflowEvidenceRenderer;

namespace Semanticus.Tests
{
    /// <summary>UAT C6.1: a grade, a badge and a certificate mean what they say (D-081 through D-138).</summary>
    public sealed class UatC61HonestyTests
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private sealed class Free : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "free" };
        }

        private static readonly SecurityStaticReport NoSec = SecurityStaticChecks.Evaluate(Array.Empty<RoleFilterInput>());

        private static RelationshipIntegrityReport Rels(params Verdict[] riVerdicts)
            => RelationshipIntegrity.Evaluate(riVerdicts.Select((v, i) => new RelationshipCheckInput
            {
                Name = "r" + i,
                ManyTable = "F", ManyColumn = "K", OneTable = "D", OneColumn = "K",
                Cardinality = "manyToOne", IsActive = true,
                ManyColumnType = v == Verdict.NotVerifiable ? null : "Int64",
                OneColumnType = v == Verdict.NotVerifiable ? null : "Int64",
                Probe = v == Verdict.NotVerifiable ? null : new RelationshipProbeResult
                {
                    OrphanRows = v == Verdict.Fail ? 7 : 0,
                    DuplicateKeys = 0, BlankForeignKeys = 0, BlankKeys = 0, ManyRowCount = 100, OneRowCount = 10,
                },
            }));

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-c61-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            return ws;
        }

        // D-081, D-122: a thin suite must not wear grade A.
        [Fact]
        public void Grade_cannot_be_A_when_coverage_is_thin()
        {
            var h = TestHealthAnalyzer.Analyze(
                Rels(Verdict.Pass, Verdict.NotVerifiable, Verdict.NotVerifiable, Verdict.NotVerifiable), NoSec, null);
            Assert.True(h.CoveragePct < 90.0);
            Assert.NotEqual("A", h.Grade);
        }

        [Fact]
        public void Empty_suite_is_not_grade_A()
        {
            var h = TestHealthAnalyzer.Analyze(Rels(), NoSec, null);
            Assert.Equal(0.0, h.CoveragePct);
            Assert.NotEqual("A", h.Grade);
        }

        // D-126: sealed test evidence is not Verified when most checks could not be decided.
        [Fact]
        public void Sealed_test_evidence_is_not_Verified_when_unknowns_remain()
        {
            var run = new TestSuiteRunResult
            {
                RunId = "test-thin",
                ModelName = "LaneA",
                ModelFingerprint = "fp",
                When = "2026-09-10T00:00:00.0000000Z",
                Live = false,
                Health = new TestHealth
                {
                    Grade = "F", CoveragePct = 17.6, Checked = 17, Passed = 3, Failed = 0, Suspect = 0, NotVerifiable = 14,
                },
            };
            var doc = TestReportRenderer.BuildEvidence(run);
            Assert.NotEqual(EvidenceVerdict.Verified, doc.Verdict);
            Assert.Equal(3, doc.Coverage.Verified);
            Assert.Equal(17, doc.Coverage.Total);
            Assert.Equal(14, doc.Coverage.Unknowns);
        }

        // D-122: an empty measure formula is a failure, not a silent A.
        [Fact]
        public async Task Empty_measure_formula_fails_instead_of_grading_A()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Free());
            await engine.CreateModelAsync("EmptyMeasure", 1604);
            var table = await engine.CreateTableAsync("Facts", "human");
            var measureRef = await engine.CreateMeasureAsync(table, "Blank Total", "1", "human");
            await sessions.Current.MutateAsync("human", "clear formula", m =>
            {
                ((Measure)ObjectRefs.Resolve(m, measureRef)).Expression = "";
            });

            var run = await engine.RunTestSuiteAsync(false, "human");
            var blank = Assert.Single(run.Reconciles, o => o.TargetRef == measureRef);
            Assert.Equal(Verdict.Fail, blank.Verdict);
            Assert.Contains("no formula", blank.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("A", run.Health.Grade);
        }

        // D-084: the step badge follows the record.
        [Fact]
        public async Task Instructional_step_is_done_not_passed()
        {
            var def = WorkflowParser.Parse("---\nname: describe-first\ntitle: Describe first\n---\n## Step 1: Optimize and describe first\nAdd descriptions.\n");
            Assert.Null(def.Error);
            var run = new WorkflowRunStore().Start(def, null);
            await WorkflowRunner.SubmitStepAsync(run, run.Plan[0].InstanceId, null, null);
            Assert.Equal("done", run.Results[0].Status);
            Assert.NotEqual("passed", run.Results[0].Status);
        }

        [Fact]
        public async Task Decline_only_step_is_done_not_passed()
        {
            var def = WorkflowParser.Parse(@"---
name: ask
title: Ask
---
## Step 1: Confirm
```yaml gate
inputs:
  - name: schemaCurated
    question: ""Have you curated the schema?""
    type: text
    required: answer-or-decline
```
");
            Assert.Null(def.Error);
            var run = new WorkflowRunStore().Start(def, null);
            await WorkflowRunner.SubmitStepAsync(run, run.Plan[0].InstanceId, new Dictionary<string, AnswerValue>
            {
                ["schemaCurated"] = new AnswerValue { Declined = true, DeclineReason = "UAT run, schema not curated" },
            }, null);
            Assert.Equal("done", run.Results[0].Status);
            Assert.True(run.Results[0].Answers["schemaCurated"].Declined);
        }

        [Fact]
        public async Task Answered_gate_without_checks_is_done_not_passed()
        {
            var def = WorkflowParser.Parse(@"---
name: ask
title: Ask
---
## Step 1: Confirm
```yaml gate
inputs:
  - name: schemaCurated
    question: ""Have you curated the schema?""
    type: text
    required: answer-or-decline
```
");
            Assert.Null(def.Error);
            var run = new WorkflowRunStore().Start(def, null);
            await WorkflowRunner.SubmitStepAsync(run, run.Plan[0].InstanceId, new Dictionary<string, AnswerValue>
            {
                ["schemaCurated"] = new AnswerValue { Value = "No: schema not curated and no AI instructions written" },
            }, null);
            Assert.Equal("done", run.Results[0].Status);
            Assert.NotEqual("passed", run.Results[0].Status);
        }

        [Fact]
        public async Task Warn_gate_failure_is_failed_not_passed()
        {
            var def = WorkflowParser.Parse(@"---
name: warn-check
title: Warn check
strictness: warn
---
## Step 1: Check
```yaml gate
verify:
  - kind: plan_item_staged
```
");
            Assert.Null(def.Error);
            var run = new WorkflowRunStore().Start(def, null);
            WorkflowVerifyExecutor failing = (spec, step, r, a) => Task.FromResult(
                new VerifyResult { Kind = spec.Kind, Status = "failed", Detail = "plan item was not found" });
            await WorkflowRunner.SubmitStepAsync(run, run.Plan[0].InstanceId, null, failing);
            Assert.Equal("failed", run.Results[0].Status);
            Assert.Equal("completed", run.Status);
            Assert.Contains("plan item was not found", run.Results[0].Note);
        }

        // D-083: skipped gates do not mint Verified.
        [Fact]
        public void Gate_off_run_is_not_Verified()
        {
            var def = new WorkflowDef
            {
                Name = "lane-a-child", Title = "Child", Version = 1, Strictness = "off",
                Steps = new[] { new WorkflowStep { Id = "step-1", Title = "Ask" } },
            };
            var run = new WorkflowRunStore().Start(def, "off");
            run.Results[0].Status = "skipped";
            run.Results[0].Note = "gate skipped (strictness off).";
            run.Results[0].EffectiveStrictness = "off";
            run.Status = "completed";
            run.StepIndex = 1;
            run.FinishedUtc = "2026-09-10T00:00:00Z";
            var doc = WorkflowEvidenceRenderer.Build(run);
            Assert.NotEqual(EvidenceVerdict.Verified, doc.Verdict);
        }

        [Fact]
        public void Passed_step_with_no_checks_is_not_Verified()
        {
            var def = new WorkflowDef
            {
                Name = "clean", Title = "Clean", Version = 1,
                Steps = new[]
                {
                    new WorkflowStep { Id = "step-1", Title = "One" },
                    new WorkflowStep { Id = "step-2", Title = "Two" },
                },
            };
            var run = new WorkflowRunStore().Start(def, null);
            run.Results[0].Status = "done";
            run.Results[1].Status = "done";
            run.Status = "completed";
            run.StepIndex = 2;
            run.FinishedUtc = "2026-09-10T00:00:00Z";
            run.Certificate = new WorkflowCertificate { Level = "FULL", ClaimLevel = "FULL", ComputedLevel = "FULL" };
            var doc = WorkflowEvidenceRenderer.Build(run);
            Assert.NotEqual(EvidenceVerdict.Verified, doc.Verdict);
        }

        // D-085: an offline DAX probe cannot wave a hard gate through.
        [Fact]
        public async Task Offline_dax_probe_blocks_a_hard_gate()
        {
            var ws = NewWorkspace();
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "probe-offline.md"), @"---
name: probe-offline
title: Probe offline honesty
strictness: hard
---
## Step 1: Probe
```yaml gate
inputs:
  - name: probeValue
    question: ""A known-good number.""
    type: verification
    required: answer-or-decline
  - name: target
    question: ""The measure to probe.""
    type: objectRef
    required: required
verify:
  - kind: dax_probe
    when: inputs.probeValue.answered
    probe: probeValue
```
");
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                var run = await e.StartWorkflowAsync("probe-offline", "human");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    e.SubmitWorkflowStepAsync(run.RunId, "step-1",
                        "{\"probeValue\": \"155\", \"target\": \"measure:Sales/Nonexistent YTD\"}", "human"));
                Assert.Contains("hard gate", ex.Message);
                var after = await e.GetWorkflowRunAsync(run.RunId);
                Assert.Equal("failed", after.Steps[0].Status);
                var probe = after.Steps[0].VerifyResults.Single(v => v.Kind == "dax_probe");
                Assert.Equal("unavailable", probe.Status);
                Assert.Contains("offline", probe.Detail);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // D-086: readiness_rescan cannot certify an unchanged model.
        [Fact]
        public async Task Readiness_rescan_fails_when_the_model_did_not_improve()
        {
            var bimDir = Path.Combine(Path.GetTempPath(), "smx-c61-air-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(bimDir);
            var bim = Path.Combine(bimDir, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            var wfDir = Path.Combine(LayoutStore.DirFor(bim), "workflows");
            Directory.CreateDirectory(wfDir);
            File.WriteAllText(Path.Combine(wfDir, "make-ai-ready.md"), @"---
name: make-ai-ready
title: Make the model AI-ready
strictness: hard
---
## Step 1: Score
```yaml gate
strictness: hard
verify:
  - kind: readiness_rescan
    scope: model
```
");
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro());
                await e.OpenAsync(bim);
                var run = await e.StartWorkflowAsync("make-ai-ready", "human");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "human"));
                Assert.Contains("hard gate", ex.Message);
                var after = await e.GetWorkflowRunAsync(run.RunId);
                Assert.Equal("failed", after.Steps[0].Status);
                var check = after.Steps[0].VerifyResults.Single(v => v.Kind == "readiness_rescan");
                Assert.Equal("failed", check.Status);
                Assert.Contains("no more ready", check.Detail, StringComparison.OrdinalIgnoreCase);
            }
            finally { sessions.Dispose(); Directory.Delete(bimDir, true); }
        }

        [Fact]
        public async Task Readiness_rescan_holds_when_the_playbook_only_forbids_a_drop()
        {
            var bimDir = Path.Combine(Path.GetTempPath(), "smx-c61-hold-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(bimDir);
            var bim = Path.Combine(bimDir, "model.bim");
            File.Copy(TestModels.FindBim(), bim);
            var wfDir = Path.Combine(LayoutStore.DirFor(bim), "workflows");
            Directory.CreateDirectory(wfDir);
            File.WriteAllText(Path.Combine(wfDir, "score-gate.md"), @"---
name: score-gate
title: Score and gate
strictness: hard
---
## Step 1: Score
```yaml gate
strictness: hard
verify:
  - kind: readiness_rescan
    scope: model
```
");
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro());
                await e.OpenAsync(bim);
                var run = await e.StartWorkflowAsync("score-gate", "human");
                var done = await e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "human");
                Assert.Equal("completed", done.Status);
                var check = done.Steps[0].VerifyResults.Single(v => v.Kind == "readiness_rescan");
                Assert.Equal("passed", check.Status);
            }
            finally { sessions.Dispose(); Directory.Delete(bimDir, true); }
        }

        // D-092: standalone HTML must not claim the HTML file itself is tamper-evident.
        [Fact]
        public void Exported_html_does_not_claim_the_html_file_is_tamper_evident()
        {
            var def = new WorkflowDef
            {
                Name = "close-check", Title = "Close check", Version = 1,
                Steps = new[] { new WorkflowStep { Id = "step-1", Title = "Confirm total" } },
            };
            var run = new WorkflowRunStore().Start(def, null);
            run.Results[0].Status = "passed";
            run.Results[0].VerifyResults = new[] { new VerifyResult { Kind = "dax_probe", Status = "passed", Detail = "matched 1" } };
            run.Status = "completed";
            run.StepIndex = 1;
            run.FinishedUtc = "2026-09-10T00:00:00Z";
            var html = EvidenceArtifact.Seal(WorkflowEvidenceRenderer.Build(run)).Html;
            Assert.DoesNotContain("Tamper-evident record", html);
            Assert.Contains("reading copy", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Verified 1 of 1", html);
            var altered = html.Replace("Verified 1 of 1", "Verified 9 of 9", StringComparison.Ordinal);
            Assert.Contains("Verified 9 of 9", altered);
            Assert.DoesNotContain("Tamper-evident record", altered);
        }

        // D-097: Free can review the latest run; signing stays Pro.
        [Fact]
        public async Task Free_tier_can_review_test_evidence()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Free());
            await engine.OpenAsync(TestModels.FindBim());
            var run = await engine.RunTestSuiteAsync(false, "human");
            Assert.Null(run.Error);
            var report = await engine.ExportTestReportAsync();
            Assert.Null(report.Error);
            Assert.Contains("signable test report is Pro", report.Note);
            Assert.False(string.IsNullOrWhiteSpace(report.Markdown));
            Assert.False(string.IsNullOrWhiteSpace(report.Html));
            Assert.True(string.IsNullOrWhiteSpace(report.Json));
            Assert.True(string.IsNullOrWhiteSpace(report.ContentHash));
        }

        // D-138: one model identity across sidecars.
        [Fact]
        public async Task Tests_and_evidence_share_one_model_identity()
        {
            var root = Path.Combine(Path.GetTempPath(), "smx-c61-id-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            var bim = Path.Combine(root, "s.bim");
            File.Copy(TestModels.FindBim(), bim);
            var sessions = new SessionManager();
            try
            {
                using var engine = new LocalEngine(sessions, new Pro(), root);
                await engine.OpenAsync(bim);
                var measureRef = (await engine.ListMeasuresAsync()).First().Ref;
                var saved = await engine.SaveTestDefinitionAsync(new TestDefinition
                {
                    Kind = TestKinds.MeasureReconcile,
                    Title = "Identity check",
                    TargetRef = measureRef,
                    ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest
                    {
                        MeasureRef = measureRef, Sql = "SELECT 1 AS v", BlankPolicy = "zero",
                    }),
                }, "human");
                Assert.False(string.IsNullOrWhiteSpace(saved.ModelIdentity));
                await engine.RunTestSuiteAsync(false, "human");
                var library = await engine.ListEvidenceAsync();
                Assert.False(string.IsNullOrWhiteSpace(library.ModelIdentity));
                Assert.Equal(saved.ModelIdentity, library.ModelIdentity);
            }
            finally { sessions.Dispose(); try { Directory.Delete(root, true); } catch { } }
        }
    }
}
