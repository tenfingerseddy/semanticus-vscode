using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// §9c op→workflow BINDING — the mandate axis (availability · REQUIRED · strictness). Pinned here, to the
    /// spec's §9.10 A/B/C decisions: a HARD binding refuses the bare op with a plain-language teaching error that
    /// names the op, the required set, start_workflow and get_workflow_policy (never "binding violation"); a WARN
    /// binding allows the edit but publishes a `landed_outside_required_workflow` advisory (the audit IS the
    /// enforcement in warn, §9.6); (A) the exemption is STEP-scoped — allowed only while an active run of a
    /// required workflow is AT a step that performs the op, not merely because a run exists (the closed
    /// start-and-freestyle hole); (B) enforcement keys on bindings.&lt;op&gt;.mode alone, so the global strictness
    /// kill-switch never voids a mandate; a dry_run (the DryRunScope exemption — replay admission must not trip a
    /// mandate) of a bound op succeeds; set_workflow_binding is Pro for hard/warn, validates the workflow set,
    /// clears+prunes on off, and (C) refuses an agent-door change to a userDisablable:false mandate; a malformed
    /// settings file fails safe (no binding); and get_workflow_policy reflects a binding both ways.
    /// Isolation mirrors WorkflowAvailabilityTests: a temp workspace, the un-gated vehicle, Fake entitlement.
    /// </summary>
    public sealed class WorkflowBindingTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // A 2-step, UN-GATED vehicle. Step 1 declares ops:[create_measure] (the performing step); step 2 does
        // not. Un-gated (ops-only fences carry no strictness/inputs/verify) so start_workflow runs FREE — the
        // binding axis is exercised in isolation from the entitlement + strictness axes.
        private const string VehicleMd = @"---
name: bind-vehicle
title: Binding vehicle
whenToUse: ""Author a measure the reviewed way.""
---
## Step 1: Author the measure
Create the measure at this step.
```yaml gate
ops: [create_measure]
```
## Step 2: Review
Review what you made — this step does not perform create_measure.
";

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-wfbind-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "bind-vehicle.md"), VehicleMd);
            return ws;
        }

        private static string SettingsFile(string ws) => Path.Combine(ws, ".semanticus", "workflow-settings.json");
        private static void WriteSettings(string ws, string json) => File.WriteAllText(SettingsFile(ws), json);

        // The two enforced shapes, and a hand-locked committed mandate (§9.10C).
        private const string HardBinding = @"{ ""bindings"": { ""create_measure"": { ""require"": [""bind-vehicle""], ""mode"": ""hard"" } } }";
        private const string WarnBinding = @"{ ""bindings"": { ""create_measure"": { ""require"": [""bind-vehicle""], ""mode"": ""warn"" } } }";

        private static async Task OpenModelWithFactsAsync(LocalEngine engine)
        {
            await engine.CreateModelAsync("BindTest", 1701);
            var t = await engine.CreateTableAsync("Facts", "human");     // create_table is NOT bound in these tests → free
            await engine.CreateColumnAsync(t, "Amount", "Decimal", "Amount", "human");
        }

        // ---- (a) HARD binding refuses the bare op with the teaching error --------------------------------------

        [Fact]
        public async Task Hard_binding_refuses_the_bare_op_and_the_error_teaches()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    await OpenModelWithFactsAsync(engine);
                    WriteSettings(ws, HardBinding);

                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.CreateMeasureAsync("table:Facts", "Blocked", "SUM(Facts[Amount])", "agent"));
                    // names the actual op, the required set, start_workflow AND get_workflow_policy — never "binding violation".
                    Assert.Contains("create_measure", ex.Message);
                    Assert.Contains("bind-vehicle", ex.Message);
                    Assert.Contains("start_workflow", ex.Message);
                    Assert.Contains("get_workflow_policy", ex.Message);
                    Assert.DoesNotContain("binding violation", ex.Message);

                    // refused BEFORE any mutation — the measure never landed.
                    Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "Blocked");
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- (b) WARN binding allows the edit + records the advisory (§9.6 audit posture) ----------------------

        [Fact]
        public async Task Warn_binding_allows_the_edit_and_publishes_the_advisory()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    await OpenModelWithFactsAsync(engine);
                    WriteSettings(ws, WarnBinding);

                    var captured = new List<ActivityEvent>();
                    void Handler(ActivityEvent e) => captured.Add(e);
                    sessions.Bus.Activity += Handler;
                    try
                    {
                        var newRef = await engine.CreateMeasureAsync("table:Facts", "Allowed", "SUM(Facts[Amount])", "agent");
                        Assert.NotNull(newRef);   // warn does NOT block — the measure landed
                        Assert.Contains(await engine.ListMeasuresAsync(), m => m.Name == "Allowed");
                    }
                    finally { sessions.Bus.Activity -= Handler; }

                    var advisory = Assert.Single(captured, e => e.Kind == "landed_outside_required_workflow");
                    Assert.True(advisory.Ok);
                    Assert.Equal("create_measure", advisory.Target);
                    Assert.Contains("create_measure", advisory.Label);
                    Assert.Contains("bind-vehicle", advisory.Label);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- (c) STEP-scoped exemption (§9.10A): at the performing step it passes; off it, hard still bites -----

        [Fact]
        public async Task Exemption_is_step_scoped_not_run_scoped()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    await OpenModelWithFactsAsync(engine);
                    WriteSettings(ws, HardBinding);

                    // At step 1 (its ops declare create_measure) the bound op is EXEMPT and succeeds.
                    var run = await engine.StartWorkflowAsync("bind-vehicle", "agent");
                    Assert.Equal("active", run.Status);
                    var atStep1 = await engine.CreateMeasureAsync("table:Facts", "AtStep1", "SUM(Facts[Amount])", "agent");
                    Assert.NotNull(atStep1);
                    Assert.Contains(await engine.ListMeasuresAsync(), m => m.Name == "AtStep1");

                    // Advance to step 2 (which does NOT perform create_measure). A run is still active — but merely
                    // being active is not enough (the closed start-and-freestyle hole): hard rejects again.
                    var advanced = await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "agent");
                    Assert.Equal("active", advanced.Status);
                    Assert.Equal("step-2", advanced.CurrentStep.StepId);

                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.CreateMeasureAsync("table:Facts", "AtStep2", "SUM(Facts[Amount])", "agent"));
                    Assert.Contains("bind-vehicle", ex.Message);
                    Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "AtStep2");
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- (d) independence (§9.10B): the strictness kill-switch does NOT void a mandate ---------------------

        [Fact]
        public async Task Strictness_off_does_not_void_a_hard_binding()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    await OpenModelWithFactsAsync(engine);
                    // Global enforcement OFF *and* a hard binding in the same file. Binding reads only its own mode.
                    WriteSettings(ws, @"{ ""strictness"": ""off"", ""bindings"": { ""create_measure"": { ""require"": [""bind-vehicle""], ""mode"": ""hard"" } } }");
                    Assert.False((await engine.GetWorkflowEnforcementAsync()).Enforced);   // the kill-switch really is off

                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.CreateMeasureAsync("table:Facts", "StillBound", "SUM(Facts[Amount])", "agent"));
                    Assert.Contains("bind-vehicle", ex.Message);   // the mandate still bites
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- (e) DryRunScope exemption: a rehearsal of a bound op is not a landing -----------------------------

        [Fact]
        public async Task Dry_run_of_a_bound_op_succeeds()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    await OpenModelWithFactsAsync(engine);
                    WriteSettings(ws, HardBinding);   // a HARD binding that would reject the bare op…

                    var rpt = await engine.DryRunOpAsync("create_measure",
                        "{\"tableRef\":\"table:Facts\",\"name\":\"Rehearsed\",\"expression\":\"SUM(Facts[Amount])\"}");
                    // …is exempt under the rehearsal scope: the dry-run succeeds and carries the op's real result.
                    Assert.True(rpt.WouldSucceed);
                    Assert.Null(rpt.Error);
                    Assert.Contains("Rehearsed", rpt.Result);
                    Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "Rehearsed");   // rolled back, as ever
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- (f) set_workflow_binding: Pro-gated · validates names · clears+prunes · siblings survive ----------

        [Fact]
        public async Task Set_binding_is_pro_gated_validates_and_prunes()
        {
            var ws = NewWorkspace();
            var file = SettingsFile(ws);

            // Pro gate: writing a mandate (hard/warn) on the FREE tier is refused with the entitlement upsell.
            var freeSessions = new SessionManager();
            var free = new LocalEngine(freeSessions, new Fake(pro: false), ws);
            try
            {
                using (free)
                    await Assert.ThrowsAsync<EntitlementException>(
                        () => free.SetWorkflowBindingAsync("create_measure", new[] { "bind-vehicle" }, "hard", "agent"));
            }
            finally { freeSessions.Dispose(); }

            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                using (engine)
                {
                    // Unknown workflow name is refused instructively (list_workflows names the fix).
                    var bad = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.SetWorkflowBindingAsync("create_measure", new[] { "ghost-flow" }, "hard", "human"));
                    Assert.Contains("Unknown workflow", bad.Message);
                    Assert.Contains("list_workflows", bad.Message);

                    // Two sibling bindings written; both present in the file.
                    await engine.SetWorkflowBindingAsync("create_measure", new[] { "bind-vehicle" }, "hard", "human");
                    await engine.SetWorkflowBindingAsync("create_table", new[] { "bind-vehicle" }, "warn", "human");
                    var json = File.ReadAllText(file);
                    Assert.Contains("create_measure", json);
                    Assert.Contains("create_table", json);

                    // Clearing create_measure (mode:off) prunes ONLY it; the sibling survives the merge.
                    await engine.SetWorkflowBindingAsync("create_measure", null, "off", "human");
                    json = File.ReadAllText(file);
                    Assert.DoesNotContain("create_measure", json);
                    Assert.Contains("create_table", json);      // sibling intact
                    Assert.Contains("bindings", json);          // the container survives while a sibling remains

                    // Clearing the LAST binding prunes the emptied "bindings" container entirely.
                    await engine.SetWorkflowBindingAsync("create_table", Array.Empty<string>(), "off", "human");
                    json = File.ReadAllText(file);
                    Assert.DoesNotContain("bindings", json);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- (g) §9.10C: a userDisablable:false mandate is locked against the AGENT door -----------------------

        [Fact]
        public async Task UserDisablable_false_locks_the_agent_door_but_not_the_human()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    // A committed mandate a contributor must not be able to quietly turn off.
                    WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": { ""require"": [""bind-vehicle""], ""mode"": ""hard"", ""userDisablable"": false } } }");

                    // Agent door: refused with the teaching lock error (routes to the reviewed file edit).
                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.SetWorkflowBindingAsync("create_measure", Array.Empty<string>(), "off", "agent"));
                    Assert.Contains("locked by committed team policy", ex.Message);
                    Assert.Contains("workflow-settings.json", ex.Message);
                    Assert.Contains("create_measure", File.ReadAllText(SettingsFile(ws)));   // still there — the agent could not clear it

                    // Human/file door: still governs it — the clear goes through.
                    await engine.SetWorkflowBindingAsync("create_measure", Array.Empty<string>(), "off", "human");
                    Assert.DoesNotContain("create_measure", File.ReadAllText(SettingsFile(ws)));
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- (h) FAIL CLOSED: a PRESENT-but-corrupt settings file must NOT bypass a mandate --------------------
        // Audit finding: parsing a corrupt file returned "no binding", so corrupting the JSON silently BYPASSED a
        // mandated workflow (the old test asserted the op flowed FREE — that was the vulnerability). Now a
        // present-but-unreadable file fails CLOSED: bindable ops are refused until it's repaired, while a MISSING
        // file stays the normal no-bindings default. Neuter: drop the WorkflowSettingsCorrupt() guard in
        // EnforceBindingAsync and the corrupt op is allowed again — this test fails.
        [Fact]
        public async Task Corrupt_settings_fails_closed_but_a_missing_file_flows_free()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    await OpenModelWithFactsAsync(engine);

                    // No settings file at all → the normal default: no bindings, the op flows free.
                    var free = await engine.CreateMeasureAsync("table:Facts", "FreeNoFile", "SUM(Facts[Amount])", "agent");
                    Assert.NotNull(free);

                    // A PRESENT-but-corrupt file FAILS CLOSED: the bindable op is refused with a repair message.
                    WriteSettings(ws, "{ this is not valid json ]");   // garbage
                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.CreateMeasureAsync("table:Facts", "Blocked", "SUM(Facts[Amount])", "agent"));
                    Assert.Contains("failing closed", ex.Message);
                    Assert.Contains("workflow-settings.json", ex.Message);
                    // …and the error advertises the recovery escape: any settings write (set_workflow_enforcement)
                    // preserves the unreadable file aside and writes a fresh valid one — the user is never stuck.
                    Assert.Contains("set_workflow_enforcement", ex.Message);
                    Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "Blocked");

                    // Surfaced loudly (not just at the gate): the policy view lints it and enforcement stays ON.
                    var policy = await engine.GetWorkflowPolicyAsync();
                    Assert.Contains(policy.Lints, l => l.Message.Contains("failing CLOSED"));
                    Assert.True((await engine.GetWorkflowEnforcementAsync()).Enforced);

                    // The advertised escape genuinely unblocks: the settings write preserves the corrupt file
                    // aside, writes a fresh valid file, and the bindable op flows again (no bindings remain).
                    await engine.SetWorkflowEnforcementAsync("default", "human");
                    Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(ws, ".semanticus"), "workflow-settings.json.corrupt-*"));
                    var unblocked = await engine.CreateMeasureAsync("table:Facts", "Unblocked", "SUM(Facts[Amount])", "agent");
                    Assert.NotNull(unblocked);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- (i) the READ side judges corruption with the SAME strict decode as the WRITE side ------------------
        // Review follow-up (sol, round 5): the readers decoded via File.ReadAllText (replacement fallback), so a
        // malformed UTF-16 file carrying a valid-looking "strictness":"off" beside an invalid sequence read as
        // HEALTHY — WorkflowSettingsCorrupt() said fine, enforcement honored the "off", and a malformed file
        // CONTROLLED the very enforcement this posture exists to fail closed. Every reader now routes through the
        // strict decoder. Neuter: switch the readers back to ReadAllText and the file reads healthy again — the
        // enforcement honors the "off" and the bindable op flows → both asserts fail.
        [Fact]
        public async Task Replacement_parseable_corruption_fails_closed_on_the_read_side_too()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    await OpenModelWithFactsAsync(engine);

                    // UTF-16LE BOM + a file that decodes to VALID JSON under replacement fallback (the unpaired
                    // high surrogate 00 D8 becomes U+FFFD inside the "note" string) and carries "strictness":"off".
                    var corrupt = new byte[] { 0xFF, 0xFE }
                        .Concat(System.Text.Encoding.Unicode.GetBytes("{ \"strictness\": \"off\", \"note\": \"x"))
                        .Concat(new byte[] { 0x00, 0xD8 })
                        .Concat(System.Text.Encoding.Unicode.GetBytes("y\" }")).ToArray();
                    File.WriteAllBytes(SettingsFile(ws), corrupt);

                    // The read side must call this CORRUPT: enforcement fails closed — the "off" is NOT honored.
                    var enf = await engine.GetWorkflowEnforcementAsync();
                    Assert.True(enf.Enforced);
                    Assert.Contains("unreadable", enf.Note);

                    // …and a bindable op refuses until the file is repaired (same round-2 semantics, now read-side).
                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.CreateMeasureAsync("table:Facts", "Blocked", "SUM(Facts[Amount])", "agent"));
                    Assert.Contains("failing closed", ex.Message);
                    Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "Blocked");
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- (j) [D6] §9.11 ARRAY-form binding: selected by `when:` via the SHARED predicate evaluator ----------

        [Fact]
        public async Task Array_form_binding_selects_by_when_via_shared_evaluator()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    await OpenModelWithFactsAsync(engine);

                    // The array rule's `when:` holds (the test session is OFFLINE) ⇒ the hard binding bites.
                    WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": [ { ""when"": ""connection.kind == 'offline'"", ""require"": [""bind-vehicle""], ""mode"": ""hard"" } ] } }");
                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.CreateMeasureAsync("table:Facts", "Blocked", "SUM(Facts[Amount])", "agent"));
                    Assert.Contains("bind-vehicle", ex.Message);
                    Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "Blocked");

                    // Flip the `when:` to a condition that is FALSE offline ⇒ the rule does not match ⇒ the op flows free.
                    WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": [ { ""when"": ""connection.kind == 'xmla'"", ""require"": [""bind-vehicle""], ""mode"": ""hard"" } ] } }");
                    var made = await engine.CreateMeasureAsync("table:Facts", "Allowed", "SUM(Facts[Amount])", "agent");
                    Assert.NotNull(made);
                    Assert.Contains(await engine.ListMeasuresAsync(), m => m.Name == "Allowed");
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- (k) [D6] a `target.*` op-arg fact is DEFERRED to T4: the rule never matches, and it's linted -------

        [Fact]
        public async Task Array_binding_target_fact_is_unknown_and_linted()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    await OpenModelWithFactsAsync(engine);
                    // target.* is not a T2 fact — the rule must NEVER silently match (the op flows free), and the
                    // policy must surface it as a lint rather than hide the never-matching binding.
                    WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": [ { ""when"": ""target.table == 'Sales'"", ""require"": [""bind-vehicle""], ""mode"": ""hard"" } ] } }");
                    var made = await engine.CreateMeasureAsync("table:Facts", "NotBlocked", "SUM(Facts[Amount])", "agent");
                    Assert.NotNull(made);   // not silently matching
                    Assert.Contains(await engine.ListMeasuresAsync(), m => m.Name == "NotBlocked");

                    var policy = await engine.GetWorkflowPolicyAsync();
                    Assert.Contains(policy.Lints, l => l.Message.Contains("available yet"));
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- (i) get_workflow_policy reflects a binding BOTH ways (inverted onto the workflow + the raw list) --

        [Fact]
        public async Task Get_workflow_policy_reflects_a_binding_both_ways()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);
            try
            {
                using (engine)
                {
                    WriteSettings(ws, HardBinding);
                    var policy = await engine.GetWorkflowPolicyAsync();

                    // the raw binding list
                    var b = Assert.Single(policy.Bindings, x => x.Op == "create_measure");
                    Assert.Equal("hard", b.Mode);
                    Assert.Contains("bind-vehicle", b.Require);
                    Assert.True(b.UserDisablable);   // absent key ⇒ the safe default

                    // inverted onto the workflow entry
                    var wf = Assert.Single(policy.Workflows, x => x.Name == "bind-vehicle");
                    Assert.Contains("create_measure", wf.RequiredForOps);
                    Assert.Equal("Author a measure the reviewed way.", wf.WhenToUse);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }
    }
}
