using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// The workflow VERIFY EXECUTORS wired on <see cref="LocalEngine"/> (docs/pro-mode-spec.md §4/§7) —
    /// the engine-evaluated half of a gate that WorkflowKernelTests (pure kernel, injected fake executor)
    /// and WorkflowOpsTests (ops plumbing, no real verify) deliberately do NOT cover. Pinned here:
    /// dax_probe offline → skipped-not-passed through the real engine (a hard gate does not block on a
    /// skip); the missing-snapshot honesty for bpa_clean (fails instructively rather than silently
    /// passing); the bpa_clean happy path (snapshot diff, no new violations); and RPC-door dual-drive
    /// (one run store shared across both doors).
    /// </summary>
    public sealed class WorkflowVerifyExecutorTests
    {
        private sealed class Pro : IEntitlement { public bool IsPro => true; public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" }; }

        // A hard-gated step whose only verify is a dax_probe over an answered objectRef target — offline,
        // this must come back SKIPPED (never a fabricated pass), and a hard gate must not block on a skip.
        private const string ProbeOfflineMd = @"---
name: probe-offline
title: Probe offline honesty
strictness: hard
---
## Step 1: Probe
Probe the measure against the user's known-good number.
```yaml gate
inputs:
  - name: probeValue
    question: ""A known-good number to verify against.""
    type: verification
    required: answer-or-decline
  - name: target
    question: ""The ref of the measure to probe.""
    type: objectRef
    required: required
verify:
  - kind: dax_probe
    when: inputs.probeValue.answered
    probe: probeValue
```
";

        // A hard-gated bpa_clean (model scope) — its start-of-run snapshot is only taken when a session is
        // open at start_workflow, so this is the vehicle for both the missing-snapshot failure (start with
        // no session) and the happy path (open first, then start).
        private const string BpaCleanMd = @"---
name: bpa-clean
title: BPA clean at model scope
strictness: hard
---
## Step 1: Check
Check that the step introduced no new BPA violations.
```yaml gate
verify:
  - kind: bpa_clean
    scope: model
```
";

        // A hard-gated benchmark_delta over an answered baseline + objectRef target. NO `when` clause so the
        // verify ALWAYS runs — that lets the offline tests reach the executor's OWN answer-validation branches
        // (missing/non-numeric baseline, absent target) which the executor checks BEFORE the live-connection skip.
        private const string BenchmarkDeltaMd = @"---
name: benchmark-delta
title: Benchmark delta offline honesty
strictness: hard
---
## Step 1: Benchmark
Prove the tuned measure did not regress past tolerance.
```yaml gate
inputs:
  - name: baselineMs
    question: ""The pre-rewrite warm median in ms.""
    type: number
    required: optional
  - name: target
    question: ""The ref of the measure to benchmark.""
    type: objectRef
    required: optional
verify:
  - kind: benchmark_delta
    probe: baselineMs
```
";

        private const string RpcMd = @"---
name: rpc
title: RPC dual-drive
strictness: hard
---
## Step 1: Ask
Ask a thing.
```yaml gate
inputs:
  - name: thing
    question: ""What thing?""
    required: answer-or-decline
```
";

        // A hard-gated step whose only verify is workflow_admissible over an answered text input naming a
        // workflow — the meta-gate that "author-a-workflow" uses. Offline-safe (check_workflow is a static
        // resolve), so both the pass and the fail paths run for real here.
        private const string AdmissibleMd = @"---
name: admit-checker
title: Admissibility checker vehicle
strictness: hard
---
## Step 1: Check
Check the named workflow is admissible.
```yaml gate
inputs:
  - name: authoredName
    question: ""The name of the workflow to check.""
    type: text
    required: required
verify:
  - kind: workflow_admissible
    probe: authoredName
```
";

        // A clean, admissible target: no ops/triggers/verify to resolve → check_workflow finds nothing → Ok.
        private const string CleanTargetMd = @"---
name: clean-flow
title: Clean target
strictness: warn
---
## Step 1: Ask
Ask a thing.
```yaml gate
inputs:
  - name: note
    question: ""A note?""
    required: optional
```
";

        // Parse-clean but NOT admissible: a phantom op check_workflow flags as a warn (save_workflow accepts it
        // because it parses; the workflow_admissible gate is what catches the deeper defect).
        private const string PhantomOpTargetMd = @"---
name: phantom-flow
title: Phantom-op target
strictness: warn
---
## Step 1: Do
Do a thing.
```yaml gate
ops: [totally_not_an_op]
strictness: warn
inputs:
  - name: note
    question: ""A note?""
    required: optional
```
";

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-wfverify-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            return ws;
        }

        private static void WriteUserWorkflow(string ws, string file, string md) =>
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", file), md);

        // A temp COPY of the fixture (NOT under a "semanticus-*" temp path — that prefix is the ephemeral
        // live-snapshot marker → IsEphemeralAnchor). Opening it makes the session non-ephemeral so the user
        // workflow dir anchors to the model, exactly like a saved project.
        private static string TempCopyOfBim()
        {
            var dir = Path.Combine(Path.GetTempPath(), "smx-wfverify-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var bim = Path.Combine(dir, "AdventureWorks.bim");
            File.Copy(TestModels.FindBim(), bim);
            return bim;
        }

        // The dir WorkflowDirs() resolves for a session opened on a SAVED model: LayoutStore.DirFor IS the
        // `.semanticus` sidecar dir (PBIP hop included) and workflows nest one level under it. This test
        // originally caught a DOUBLED ".semanticus" in that resolution — it now pins the fixed path, so a
        // regression puts the doubling back and this fails.
        private static string UserWorkflowDirForOpenModel(string bimPath) =>
            Path.Combine(LayoutStore.DirFor(bimPath), "workflows");

        [Fact]
        public async Task Dax_probe_offline_comes_back_skipped_not_passed_and_a_hard_gate_does_not_block()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "probe-offline.md", ProbeOfflineMd);
            var sessions = new SessionManager();
            try
            {
                // Pro (the gate enforces), NO session, NO live connection: the probe executor returns skipped
                // BEFORE touching the session, so the step passes with an honest skip recorded on the evidence.
                var e = new LocalEngine(sessions, new Pro(), ws);
                var run = await e.StartWorkflowAsync("probe-offline", "human");
                var done = await e.SubmitWorkflowStepAsync(run.RunId, "step-1",
                    "{\"probeValue\": \"100\", \"target\": \"measure:Sales/Total\"}", "human");

                Assert.Equal("completed", done.Status);            // a hard gate does NOT block on a skip
                Assert.Equal("passed", done.Steps[0].Status);
                var probe = done.Steps[0].VerifyResults.Single(v => v.Kind == "dax_probe");
                Assert.Equal("skipped", probe.Status);             // skipped != passed — the offline honesty contract
                Assert.Contains("offline", probe.Detail);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Bpa_clean_with_no_start_of_run_snapshot_fails_instructively_hard()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "bpa-clean.md", BpaCleanMd);
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                // Start with NO session open → StartWorkflowAsync skips the snapshot (guarded on _sessions.Current).
                var run = await e.StartWorkflowAsync("bpa-clean", "human");
                // A session exists only at SUBMIT time → the executor is reached but has no before/after baseline.
                await e.OpenAsync(TestModels.FindBim());

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "human"));
                Assert.Contains("no start-of-run", ex.Message);    // the missing-snapshot honesty, not a silent pass

                var after = await e.GetWorkflowRunAsync(run.RunId);
                Assert.Equal("failed", after.Steps[0].Status);     // hard gate: the run stays on the failed step
                Assert.Contains("no start-of-run",
                    after.Steps[0].VerifyResults.Single(v => v.Kind == "bpa_clean").Detail);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Bpa_clean_happy_path_passes_when_the_step_introduces_no_new_violations()
        {
            var bim = TempCopyOfBim();
            var wfDir = UserWorkflowDirForOpenModel(bim);
            Directory.CreateDirectory(wfDir);
            File.WriteAllText(Path.Combine(wfDir, "bpa-clean.md"), BpaCleanMd);
            var sessions = new SessionManager();
            try
            {
                // Open the model FIRST → start_workflow takes the start-of-run BPA snapshot; a submit that changes
                // nothing diffs clean, so the hard gate passes with "no new BPA violations".
                var e = new LocalEngine(sessions, new Pro());
                await e.OpenAsync(bim);
                var run = await e.StartWorkflowAsync("bpa-clean", "human");
                var done = await e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "human");

                Assert.Equal("completed", done.Status);
                var bpa = done.Steps[0].VerifyResults.Single(v => v.Kind == "bpa_clean");
                Assert.Equal("passed", bpa.Status);
                Assert.Contains("no new BPA violations", bpa.Detail);
            }
            finally { sessions.Dispose(); Directory.Delete(Path.GetDirectoryName(bim), true); }
        }

        [Fact]
        public async Task Benchmark_delta_offline_comes_back_skipped_not_passed_with_the_open_live_hint()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "benchmark-delta.md", BenchmarkDeltaMd);
            var sessions = new SessionManager();
            try
            {
                // Valid answered baseline + target, NO live connection: the executor validates the answers, then
                // SKIPS honestly (it cannot time anything offline) — a skip must never block the hard gate.
                var e = new LocalEngine(sessions, new Pro(), ws);
                var run = await e.StartWorkflowAsync("benchmark-delta", "human");
                var done = await e.SubmitWorkflowStepAsync(run.RunId, "step-1",
                    "{\"baselineMs\": \"100\", \"target\": \"measure:Sales/Total\"}", "human");

                Assert.Equal("completed", done.Status);
                var bd = done.Steps[0].VerifyResults.Single(v => v.Kind == "benchmark_delta");
                Assert.Equal("skipped", bd.Status);
                Assert.Contains("offline", bd.Detail);
                Assert.Contains("open_live", bd.Detail);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Benchmark_delta_with_no_baseline_fails_teaching_benchmark_dax_coldwarm()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "benchmark-delta.md", BenchmarkDeltaMd);
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                var run = await e.StartWorkflowAsync("benchmark-delta", "human");
                // baselineMs absent (probe unanswered) → the hard gate fails BEFORE the offline skip, and the
                // rejection text names the tool that records the baseline.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"target\": \"measure:Sales/Total\"}", "human"));
                Assert.Contains("benchmark_dax_coldwarm", ex.Message);

                var after = await e.GetWorkflowRunAsync(run.RunId);
                Assert.Equal("failed", after.Steps[0].Status);
                Assert.Contains("benchmark_dax_coldwarm",
                    after.Steps[0].VerifyResults.Single(v => v.Kind == "benchmark_delta").Detail);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Benchmark_delta_with_a_non_numeric_baseline_fails_naming_the_fix()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "benchmark-delta.md", BenchmarkDeltaMd);
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                var run = await e.StartWorkflowAsync("benchmark-delta", "human");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1",
                        "{\"baselineMs\": \"fast\", \"target\": \"measure:Sales/Total\"}", "human"));
                Assert.Contains("is not a number of milliseconds", ex.Message);
                Assert.Contains("benchmark_dax_coldwarm", ex.Message);   // the fix is named in the teaching text
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Benchmark_delta_with_no_objectRef_target_fails_instructively()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "benchmark-delta.md", BenchmarkDeltaMd);
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                var run = await e.StartWorkflowAsync("benchmark-delta", "human");
                // Numeric baseline but NO answered objectRef input → the executor cannot know which measure to time.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"baselineMs\": \"100\"}", "human"));
                Assert.Contains("objectRef", ex.Message);

                var after = await e.GetWorkflowRunAsync(run.RunId);
                Assert.Equal("failed", after.Steps[0].Status);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Workflow_admissible_passes_when_the_named_workflow_checks_clean()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "admit-checker.md", AdmissibleMd);
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                await e.SaveWorkflowAsync("clean-flow", CleanTargetMd, "agent");   // author a valid workflow first
                var run = await e.StartWorkflowAsync("admit-checker", "human");
                var done = await e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"authoredName\": \"clean-flow\"}", "human");

                Assert.Equal("completed", done.Status);
                var v = done.Steps[0].VerifyResults.Single(x => x.Kind == "workflow_admissible");
                Assert.Equal("passed", v.Status);
                Assert.Contains("admission dry-run", v.Detail);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Workflow_admissible_fails_hard_when_the_named_workflow_has_a_phantom_op()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "admit-checker.md", AdmissibleMd);
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                await e.SaveWorkflowAsync("phantom-flow", PhantomOpTargetMd, "agent");   // parses, but names a phantom op
                var run = await e.StartWorkflowAsync("admit-checker", "human");

                // A hard gate over a FAILED verify throws (like bpa_clean/benchmark_delta), and the run stays on the
                // failed step with the specific reason — the phantom op — named in the evidence.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"authoredName\": \"phantom-flow\"}", "human"));
                Assert.Contains("NOT admissible", ex.Message);
                Assert.Contains("totally_not_an_op", ex.Message);

                var after = await e.GetWorkflowRunAsync(run.RunId);
                Assert.Equal("failed", after.Steps[0].Status);
                var v = after.Steps[0].VerifyResults.Single(x => x.Kind == "workflow_admissible");
                Assert.Contains("totally_not_an_op", v.Detail);   // the exact defect, forwarded from check_workflow
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Workflow_admissible_fails_instructively_when_the_named_workflow_does_not_exist()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "admit-checker.md", AdmissibleMd);
            var sessions = new SessionManager();
            try
            {
                var e = new LocalEngine(sessions, new Pro(), ws);
                var run = await e.StartWorkflowAsync("admit-checker", "human");
                // Name a workflow that was never saved → check_workflow throws "not found", caught and surfaced.
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"authoredName\": \"never-saved\"}", "human"));
                Assert.Contains("could not check", ex.Message);

                var after = await e.GetWorkflowRunAsync(run.RunId);
                Assert.Equal("failed", after.Steps[0].Status);
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Rpc_door_and_owner_share_one_workflow_run_store()
        {
            var ws = NewWorkspace();
            WriteUserWorkflow(ws, "rpc.md", RpcMd);
            var pipe = "semanticus-wfverify-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var sessions = new SessionManager();
            var owner = new LocalEngine(sessions, new Pro(), ws);
            using var server = new RpcServer(sessions, owner, pipe);
            using var cts = new CancellationTokenSource();
            var serverTask = server.RunAsync(cts.Token);
            RemoteEngine remote = null;
            try
            {
                remote = await RemoteEngine.ConnectAsync(pipe);

                // Start the run via the REMOTE (agent) door; read it back via the OWNER (UI) door — one shared
                // session-held store, so both doors see the SAME run (golden rule #2, dual-drive).
                var started = await remote.StartWorkflowAsync("rpc", "agent");
                Assert.Equal("active", started.Status);
                var seenByOwner = await owner.GetWorkflowRunAsync(started.RunId);
                Assert.Equal(started.RunId, seenByOwner.RunId);
                Assert.Equal("active", seenByOwner.Status);

                // Abort via the remote door; the owner door observes the terminal state on the same run.
                var aborted = await remote.AbortWorkflowAsync(started.RunId, "smoke done", "agent");
                Assert.Equal("aborted", aborted.Status);
                Assert.Equal("aborted", (await owner.GetWorkflowRunAsync(started.RunId)).Status);
            }
            finally
            {
                remote?.Dispose();
                cts.Cancel();
                try { await serverTask; } catch { }
                sessions.Dispose();
                Directory.Delete(ws, true);
            }
        }
    }
}
