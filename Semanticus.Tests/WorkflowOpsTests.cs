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
    /// The workflow OPS layer on <see cref="LocalEngine"/> — the entitlement chokepoint, disk-backed
    /// hot-reload through the engine, the workflow/didChange broadcast, and the terminal-run
    /// Activity record (what the ExperienceTee persists). The kernel itself is pinned in
    /// WorkflowKernelTests; the full §7 matrix (dual-drive over RPC, live verify executors) lands
    /// with the McpSmoke slice.
    /// </summary>
    public sealed class WorkflowOpsTests
    {
        private const string GatedMd = @"---
name: gated
title: A gated workflow
strictness: hard
---
## Step 1: Ask
Ask things.
```yaml gate
inputs:
  - name: answer
    question: ""What is the answer?""
    required: answer-or-decline
```
";
        private const string AdviceMd = @"---
name: advice
title: Ungated advice
---
## Step 1: Read
Just read this.

## Step 2: Done
Finish up.
";

        private sealed class Pro : IEntitlement { public bool IsPro => true; public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" }; }
        private sealed class Free : IEntitlement { public bool IsPro => false; public EntitlementInfo Info => new EntitlementInfo { Tier = "free" }; }

        private static (LocalEngine engine, SessionManager sessions, string ws) Make(IEntitlement ent)
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-wfops-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "gated.md"), GatedMd);
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "advice.md"), AdviceMd);
            var sessions = new SessionManager();
            return (new LocalEngine(sessions, ent, ws), sessions, ws);
        }

        /// <summary>Since 2026-09-15 the whole workflow area is Pro, the library read included, so a free caller is
        /// refused at list, read and start alike. What used to be free (an ungated playbook) is Pro too now, because
        /// the gate is on the feature and not on the individual workflow's strictness.</summary>
        [Fact]
        public async Task The_whole_workflow_library_is_pro_and_the_refusal_stays_clean()
        {
            var (free, _, ws) = Make(new Free());
            try
            {
                var listed = await Assert.ThrowsAsync<EntitlementException>(() => free.ListWorkflowsAsync());
                Assert.StartsWith("Workflows is a Semanticus Pro feature.", listed.Message);
                await Assert.ThrowsAsync<EntitlementException>(() => free.GetWorkflowAsync("gated"));

                var gated = await Assert.ThrowsAsync<EntitlementException>(() => free.StartWorkflowAsync("gated", "human"));
                Assert.Contains("Pro feature", gated.Message);
                Assert.DoesNotContain("start_workflow", gated.Message);
                Assert.DoesNotContain("get_workflow", gated.Message);
                Assert.DoesNotContain("SEMANTICUS_LICENSE", gated.Message);
                Assert.DoesNotContain("~/.semanticus", gated.Message);

                // the ungated playbook is refused by the same one sentence, so the copy never implies a per-workflow price
                var advice = await Assert.ThrowsAsync<EntitlementException>(() => free.StartWorkflowAsync("advice", "human"));
                Assert.Equal(gated.Message, advice.Message);
            }
            finally { free.Dispose(); Directory.Delete(ws, true); }

            var (pro, _, proWs) = Make(new Pro());
            try
            {
                var list = await pro.ListWorkflowsAsync();                      // Pro door, no session open
                Assert.Contains(list, w => w.Name == "gated" && w.Gated);
                Assert.Contains(list, w => w.Name == "advice" && !w.Gated);
                Assert.Contains("Ask things.", (await pro.GetWorkflowAsync("gated")).Steps[0].Instructions);

                var run = await pro.StartWorkflowAsync("advice", "human");
                Assert.Equal("step-1", run.CurrentStep.StepId);
            }
            finally { pro.Dispose(); Directory.Delete(proWs, true); }
        }

        [Fact]
        public async Task Parent_library_card_follows_a_callee_gate()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-wfops-call-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "gated-child.md"), @"---
schemaVersion: 2
name: gated-child
title: Gated child
strictness: hard
---
## Step 1: Ask
Ask.
```yaml gate
inputs:
  - name: answer
    question: ""What?""
    required: required
```
");
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "ungated-parent.md"), @"---
schemaVersion: 2
name: ungated-parent
title: Ungated parent
strictness: off
---
## Step 1: Hand off
Hand off.
```yaml step
id: hand-off
call:
  workflow: gated-child
```
");
            var sessions = new SessionManager();
            var e = new LocalEngine(sessions, new Pro(), ws);   // the library read is Pro now, so the card needs Pro to be seen
            var freeSessions = new SessionManager();
            var free = new LocalEngine(freeSessions, new Free(), ws);
            try
            {
                var list = await e.ListWorkflowsAsync();
                var child = Assert.Single(list, w => w.Name == "gated-child");
                var parent = Assert.Single(list, w => w.Name == "ungated-parent");
                Assert.True(child.Gated);
                Assert.True(parent.Gated);   // the card follows the call graph, same as start
                Assert.True((await e.GetWorkflowPolicyAsync()).Workflows.Single(w => w.Name == "ungated-parent").Gated);

                // Gated is a marker about the callee's checks, never about the tier: free is refused whatever it says.
                var refused = await Assert.ThrowsAsync<EntitlementException>(() => free.StartWorkflowAsync("ungated-parent", "human"));
                Assert.StartsWith("Workflows is a Semanticus Pro feature.", refused.Message);
            }
            finally { free.Dispose(); freeSessions.Dispose(); e.Dispose(); sessions.Dispose(); Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Gate_rejects_then_accepts_and_the_run_broadcasts_and_logs_its_terminal_record()
        {
            var (e, sessions, ws) = Make(new Pro());
            try
            {
                var broadcasts = 0;
                sessions.Bus.WorkflowChanged += _ => broadcasts++;
                ActivityEvent terminal = null;
                sessions.Bus.Activity += a => { if (a.Kind == "workflow_run") terminal = a; };

                var run = await e.StartWorkflowAsync("gated", "human");
                Assert.Equal(1, broadcasts);

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "human"));
                Assert.Contains("This question still needs an answer.", ex.Message);
                Assert.Null(terminal);                                         // a rejected submit is not a transition

                var done = await e.SubmitWorkflowStepAsync(run.RunId, "step-1", "{\"answer\": \"42\"}", "human");
                Assert.Equal("completed", done.Status);
                Assert.True(broadcasts >= 2);
                Assert.NotNull(terminal);                                      // the terminal record rides the Activity bus → experience log
                Assert.True(terminal.Ok);
                Assert.Contains("gated", terminal.Label);
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Definitions_hot_reload_and_settings_relax_the_gate()
        {
            var (e, _, ws) = Make(new Pro());
            try
            {
                // the settings file relaxes the gated workflow to off, so its inputs stop being demanded (hot-read,
                // no restart). Strictness is about the checks, never about the tier: the whole area is Pro either way.
                File.WriteAllText(Path.Combine(ws, ".semanticus", "workflow-settings.json"),
                    "{ \"workflows\": { \"gated\": { \"strictness\": \"off\" } } }");
                Assert.False((await e.ListWorkflowsAsync()).Single(w => w.Name == "gated").Gated);
                var run = await e.StartWorkflowAsync("gated", "human");
                var done = await e.SubmitWorkflowStepAsync(run.RunId, null, null, "human");   // off: inputs not demanded
                Assert.Equal("completed", done.Status);

                // a broken edit is surfaced on the next call, not skipped
                File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "gated.md"), "not a workflow");
                Assert.NotNull((await e.ListWorkflowsAsync()).Single(w => w.Name == "gated").Error);
                await Assert.ThrowsAsync<InvalidOperationException>(() => e.StartWorkflowAsync("gated", "human"));
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Abort_records_the_partial_run_and_get_run_requires_one()
        {
            var (e, sessions, ws) = Make(new Pro());
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => e.GetWorkflowRunAsync(null));
                ActivityEvent terminal = null;
                sessions.Bus.Activity += a => { if (a.Kind == "workflow_run") terminal = a; };

                var run = await e.StartWorkflowAsync("advice", "human");
                Assert.Equal("in_progress", run.Steps[0].Status);
                var aborted = await e.AbortWorkflowAsync(run.RunId, "changed course", "human");
                Assert.Equal("aborted", aborted.Status);
                Assert.Equal("in_progress", aborted.Steps[0].Status);          // abort keeps the step that was underway
                Assert.NotNull(terminal);
                Assert.False(terminal.Ok);                                     // an abandoned run is data, honestly marked
                var stored = await e.GetWorkflowRunAsync(run.RunId);
                Assert.Equal("aborted", stored.Status);
                Assert.Equal("in_progress", stored.Steps[0].Status);
            }
            finally { Directory.Delete(ws, true); }
        }

        [Fact]
        public async Task Skip_workflow_step_public_doors_preserve_the_accountable_override()
        {
            var (e, sessions, ws) = Make(new Pro());
            try
            {
                var rpc = new EngineRpcTarget(e);
                var workflowChanges = new List<WorkflowRunView>();
                var activities = new List<ActivityEvent>();
                var modelChanges = 0;
                sessions.Bus.WorkflowChanged += workflowChanges.Add;
                sessions.Bus.Activity += activities.Add;
                sessions.Bus.Changed += _ => modelChanges++;

                var started = await rpc.startWorkflow("gated");
                Assert.Equal("step-1", started.CurrentStep.StepId);

                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.SkipWorkflowStep(e, "  ", started.RunId, "step-1"));
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => McpTools.SkipWorkflowStep(e, "reviewed exception", started.RunId, "step-2"));
                Assert.Single(workflowChanges);                       // refusals are not transitions
                Assert.Empty(activities);                            // or false audit successes

                var completed = await McpTools.SkipWorkflowStep(
                    e, "  accepted exception for this run  ", started.RunId, "step-1");

                Assert.Equal("completed", completed.Status);
                Assert.Null(completed.CurrentStep);
                var skipped = Assert.Single(completed.Steps);
                Assert.Equal("skipped", skipped.Status);             // an override is never reported as a pass
                Assert.Equal("skipped: accepted exception for this run", skipped.Note);

                var uiRead = await rpc.getWorkflowRun(started.RunId);
                Assert.Equal(completed.RunId, uiRead.RunId);
                Assert.Equal("skipped", uiRead.Steps[0].Status);      // the UI door sees the agent transition
                Assert.Equal(2, workflowChanges.Count);
                Assert.Equal("completed", workflowChanges[1].Status);

                var terminal = Assert.Single(activities, a => a.Kind == "workflow_run");
                Assert.Equal("agent", terminal.Origin);
                Assert.True(terminal.Ok);
                Assert.Contains("accepted exception for this run", JsonSerializer.Serialize(terminal.Result));
                var toolActivity = Assert.Single(activities, a => a.Kind == "skip_workflow_step");
                Assert.Equal("agent", toolActivity.Origin);
                Assert.True(toolActivity.Ok);
                Assert.Equal("gated", toolActivity.Target);
                Assert.Contains("accepted exception for this run", JsonSerializer.Serialize(toolActivity.Result));
                Assert.Equal(0, modelChanges);                        // workflow state has its own change channel

                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => rpc.skipWorkflowStep(started.RunId, "step-1", "second override"));
                Assert.Equal(2, workflowChanges.Count);               // terminal runs cannot be mutated again
                Assert.Equal(2, activities.Count);

                var humanRun = await rpc.startWorkflow("advice");
                var humanSkipped = await rpc.skipWorkflowStep(
                    humanRun.RunId, "step-1", "not relevant to this review");
                Assert.Equal("active", humanSkipped.Status);
                Assert.Equal("skipped", humanSkipped.Steps[0].Status);
                Assert.Equal("step-2", humanSkipped.CurrentStep.StepId);

                var agentRead = await McpTools.GetWorkflowRun(e, humanRun.RunId);
                Assert.Equal("skipped: not relevant to this review", agentRead.Steps[0].Note);
                Assert.Equal(4, workflowChanges.Count);               // start + human skip crossed the shared channel
                var humanActivity = Assert.Single(activities, a => a.Kind == "skip_workflow_step" && a.Origin == "human");
                Assert.Contains("not relevant to this review", JsonSerializer.Serialize(humanActivity.Result));
                Assert.Equal(3, activities.Count);                    // the override is persisted before the run is terminal
                Assert.Equal(0, modelChanges);
            }
            finally { Directory.Delete(ws, true); }
        }
    }
}
