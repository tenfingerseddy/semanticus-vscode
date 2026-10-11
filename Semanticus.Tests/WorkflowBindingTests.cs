using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        // not. Un-gated (ops-only fences carry no strictness/inputs/verify) so the binding axis is exercised in
        // isolation from the strictness axis.
        //
        // Tiers are mixed here on purpose since 2026-09-15. A test that only writes settings by hand and then
        // calls the BOUND op stays on free, because create_measure is free and the mandate must bite there too.
        // A test that calls a workflow op (start, policy, enforcement, set_workflow_binding) holds Pro, because
        // the whole workflow area is Pro.
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
        private static string ShippedWorkflow(string library, string name) =>
            Path.Combine(AppContext.BaseDirectory, library, name + ".md");
        /// <summary>Today's definition of a shipped name: the stock file, or for a parked starter (contract 21.2) its
        /// park freeze, which is what the resolver treats as current.</summary>
        private static string CurrentWorkflow(string name) =>
            File.Exists(ShippedWorkflow("workflows", name)) ? ShippedWorkflow("workflows", name) : WorkflowSeedLibraryTests.ParkedFreeze(name);
        private static string RevisionOf(string file) =>
            "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        private static JsonObject ReadSettings(string ws) => JsonNode.Parse(File.ReadAllText(SettingsFile(ws))).AsObject();
        private static string SavedRevision(string ws, string name) =>
            ReadSettings(ws)["requiredWorkflowRevisions"][name].GetValue<string>();
        private static string ParsedTitle(string file) => WorkflowParser.ParseFile(file, "stock").Title;

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

        [Trait(TestCost.Key, TestCost.Sweep)]
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

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Exemption_is_step_scoped_not_run_scoped()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
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

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("binding-specialist", true)]
        [InlineData("binding-composite", false)]
        [InlineData("binding-later", false)]
        public async Task Binding_exemption_uses_the_current_rows_owner(string required, bool allowed)
        {
            var ws = NewWorkspace();
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                await OpenModelWithFactsAsync(engine);
                foreach (var name in new[] { "binding-specialist", "binding-later" })
                    await engine.SaveWorkflowAsync(name, string.Join(Environment.NewLine,
                        "---", "schemaVersion: 2", "name: " + name, "title: Specialist", "---",
                        "## Step 1: Create", "Create a measure.", "```yaml step", "id: create", "```",
                        "```yaml gate", "ops: [create_measure]", "```"), "human");
                await engine.SaveWorkflowAsync("binding-composite", string.Join(Environment.NewLine,
                    "---", "schemaVersion: 2", "name: binding-composite", "title: Composite", "---",
                    "## Step 1: Specialist", "Delegate.", "```yaml step", "id: delegate", "call:",
                    "  workflow: binding-specialist", "```",
                    "## Step 2: Later", "Delegate later.", "```yaml step", "id: later", "call:",
                    "  workflow: binding-later", "```"), "human");
                await engine.SetWorkflowBindingAsync("create_measure", new[] { required }, "hard", "human");
                var run = await engine.StartWorkflowAsync("binding-composite", "human");
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.CreateMeasureAsync("table:Facts", "BeforeCall", "1", "human"));
                run = await engine.SubmitWorkflowStepAsync(run.RunId, "delegate", "{}", "human");
                Assert.Equal("delegate/create", run.CurrentStep.StepId);
                var refusal = await Record.ExceptionAsync(
                    () => engine.CreateMeasureAsync("table:Facts", "InsideCall", "1", "human"));
                if (allowed) Assert.Null(refusal);
                else Assert.IsType<InvalidOperationException>(refusal);
                Assert.Equal(allowed, (await engine.ListMeasuresAsync()).Any(m => m.Name == "InsideCall"));
                run = await engine.SubmitWorkflowStepAsync(run.RunId, "delegate/create", "{}", "human");
                Assert.Equal("later", run.CurrentStep.StepId);
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.CreateMeasureAsync("table:Facts", "AfterCall", "1", "human"));
            }
            finally { sessions.Dispose(); Directory.Delete(ws, true); }
        }

        // ---- (d) independence (§9.10B): the strictness kill-switch does NOT void a mandate ---------------------

        [Fact]
        public async Task Strictness_off_does_not_void_a_hard_binding()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
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

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Warn_enforcement_copy_does_not_claim_to_soften_a_hard_binding()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                using (engine)
                {
                    await OpenModelWithFactsAsync(engine);
                    WriteSettings(ws, HardBinding);
                    var warn = await engine.SetWorkflowEnforcementAsync("warn", "human");
                    Assert.Equal("warn", warn.Mode);
                    Assert.DoesNotContain("regardless of what the workflow declares", warn.Note);
                    Assert.Contains("Actions a team rule requires still go through their workflow", warn.Note, StringComparison.OrdinalIgnoreCase);

                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.CreateMeasureAsync("table:Facts", "StillBound", "SUM(Facts[Amount])", "agent"));
                    Assert.Contains("bind-vehicle", ex.Message);
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

        [Theory]
        [InlineData("add-relationship", "9a25a07542864cb73f785eaf22e7a9a576cecf8fc01236b175a576780a9f195c")]
        [InlineData("calendar-setup", "1787b80825ad1be76760e0d417d0dbff9ac441af43b4c710fc2393d9ff1e8450")]
        [InlineData("check-blast-radius", "9ea7331bc98d14234662d68c83f8e77ab57f482610ff9359e83aae99f5a16f89")]
        [InlineData("deploy-to-production", "047a9881205d9ad12bb6c2816dfba5d8129bc72ae6984558a24453e5d3e0056b")]
        [InlineData("governed-rename", "09811863cc7f6a1168476ddc31ac1999be827d440311e60b81838887fe2ace82")]
        [InlineData("import-table", "321216d6b18ba1911b8c0e9d6f7194f2604a7b572223e930369c8efbff57898a")]
        [InlineData("incremental-refresh-setup", "ed384367e410ecc6385ad3b55fceb6142f9629c534eb927d9952d364245f9fef")]
        [InlineData("make-ai-ready", "0d5ee474e07f055a7b4a1d14a58b348f6106d8e3f0ad37f5f582c23203051d77")]
        [InlineData("model-hygiene-pass", "f878083566a3e18635cd85a1a040ee81503bcf5150b5aa5046b03b3654a70c3d")]
        [InlineData("new-measure", "8b8eab5c185d10f25524dfb7c4afea97a0f28b015ba371f9acab8f7bc9d48d77")]
        [InlineData("optimize-dax", "1050e3db40cdcd1975309c4c7fc84498508abb26ba3e7493b766e28a92f49507")]
        [InlineData("refactor-to-calculation-group", "2a3b9a8f0f5a16e9553d4faa6a41bf88315f47d7fbb803be3ccf3924ec585e87")]
        [InlineData("secure-with-rls", "76f0cf76890c71eaf08fc26bc518c87cd34de80e8bed66257b74a994cbeabdaf")]
        [InlineData("time-intelligence-variants", "d8bd38cc036655022f31ada51bc47d06326e7c65b1319ade1d6d2a6be3d97f0b")]
        [InlineData("verified-measure", "536dd4b781f76eb6f2acd0bc8b849f5fab270990427a4f65706369cc7591bc51")]
        public void Shipped_v1_1_3_compatibility_files_are_byte_exact(string name, string expectedHash)
        {
            Assert.Equal("sha256:" + expectedHash, RevisionOf(ShippedWorkflow("workflows-compat/v1.1.3", name)));
        }

        [Fact]
        public async Task Legacy_required_stock_uses_baseline_without_rewriting_settings_and_document_reads_match()
        {
            var ws = NewWorkspace();
            WriteSettings(ws, @"{ ""futureKey"": 17, ""bindings"": { ""create_measure"": { ""require"": [""new-measure""], ""mode"": ""hard"" } } }");
            var original = File.ReadAllBytes(SettingsFile(ws));
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                var required = await engine.GetWorkflowAsync("new-measure");
                var unrelated = await engine.GetWorkflowAsync("optimize-dax");
                Assert.Equal(Path.GetFullPath(ShippedWorkflow("workflows-compat/v1.1.3", "new-measure")), required.FilePath);
                Assert.Equal(Path.GetFullPath(ShippedWorkflow("workflows", "optimize-dax")), unrelated.FilePath);

                var ui = await engine.GetWorkflowDocumentAsync("new-measure");
                var agent = await McpTools.GetWorkflowDocument(engine, "new-measure");
                Assert.Equal(required.FilePath, ui.Path);
                Assert.Equal(ui.Path, agent.Path);
                Assert.Equal(ui.ByteHash, agent.ByteHash);
                Assert.Equal(ui.ExactText, agent.ExactText);
                Assert.True(original.SequenceEqual(File.ReadAllBytes(SettingsFile(ws))));
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Binding_updates_preserve_old_requirements_choose_current_for_new_ones_and_prune_removed_ones()
        {
            var ws = NewWorkspace();
            WriteSettings(ws, @"{ ""futureKey"": 17, ""bindings"": { ""create_measure"": { ""require"": [""new-measure""], ""mode"": ""hard"" } } }");
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                await engine.SetWorkflowBindingAsync("create_measure", new[] { "new-measure" }, "warn", "human");
                var oldRevision = RevisionOf(ShippedWorkflow("workflows-compat/v1.1.3", "new-measure"));
                Assert.Equal(oldRevision, SavedRevision(ws, "new-measure"));
                Assert.Equal(17, ReadSettings(ws)["futureKey"].GetValue<int>());

                await engine.SetWorkflowBindingAsync("update_measure", new[] { "optimize-dax" }, "hard", "human");
                var currentRevision = RevisionOf(ShippedWorkflow("workflows", "optimize-dax"));
                Assert.Equal(oldRevision, SavedRevision(ws, "new-measure"));
                Assert.Equal(currentRevision, SavedRevision(ws, "optimize-dax"));

                await engine.SetWorkflowBindingAsync("create_measure", Array.Empty<string>(), "off", "human");
                var revisions = ReadSettings(ws)["requiredWorkflowRevisions"].AsObject();
                Assert.False(revisions.ContainsKey("new-measure"));
                Assert.Equal(currentRevision, revisions["optimize-dax"].GetValue<string>());
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Conditional_legacy_binding_resolves_a_reachable_stock_child_from_baseline()
        {
            var ws = NewWorkspace();
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "compat-caller.md"), @"---
schemaVersion: 2
name: compat-caller
title: Compatibility caller
---
## Step 1: Continue in the measure workflow
Hand off to the required child.
```yaml step
id: child
call:
  workflow: new-measure
```
");
            WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": [{ ""when"": ""date.dayOfMonth >= 1"", ""require"": [""compat-caller""], ""mode"": ""hard"" }] } }");
            var original = File.ReadAllText(SettingsFile(ws));
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                var child = await engine.GetWorkflowAsync("new-measure");
                Assert.Equal(Path.GetFullPath(ShippedWorkflow("workflows-compat/v1.1.3", "new-measure")), child.FilePath);
                Assert.Equal(original, File.ReadAllText(SettingsFile(ws)));
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task User_workflow_wins_even_when_the_saved_stock_revision_is_unavailable()
        {
            var ws = NewWorkspace();
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "new-measure.md"), @"---
name: new-measure
title: Project measure workflow
---
## Step 1: Use the project method
Follow the project-specific steps.
");
            WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": { ""require"": [""new-measure""], ""mode"": ""hard"" } }, ""requiredWorkflowRevisions"": { ""new-measure"": ""sha256:unavailable"" } }");
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                var def = await engine.GetWorkflowAsync("new-measure");
                var doc = await engine.GetWorkflowDocumentAsync("new-measure");
                Assert.Equal("user", def.Source);
                Assert.Equal("user", doc.Library);
                Assert.Equal(Path.Combine(ws, ".semanticus", "workflows", "new-measure.md"), doc.Path);
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task Unavailable_required_stock_revision_fails_loudly_for_execution_and_document_reads()
        {
            var ws = NewWorkspace();
            WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": { ""require"": [""new-measure""], ""mode"": ""hard"" } }, ""requiredWorkflowRevisions"": { ""new-measure"": ""sha256:unavailable"" } }");
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                var runRead = await Assert.ThrowsAsync<InvalidOperationException>(async () => await engine.GetWorkflowAsync("new-measure"));
                var documentRead = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.GetWorkflowDocument(engine, "new-measure"));
                Assert.Contains("pinned", runRead.Message);
                Assert.Contains("unavailable", runRead.Message);
                Assert.Equal(runRead.Message, documentRead.Message);
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        // A run freezes its whole closure at start (T220). The compatibility resolver feeds that freeze but must
        // not reach into it afterwards: dropping the pin moves the LIBRARY to today's definition while the
        // in-flight run keeps the v1.1.3 one it admitted against.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_running_snapshot_keeps_its_baseline_definition_after_the_pin_moves_to_current()
        {
            var ws = NewWorkspace();
            // optimize-dax since new-measure parked (lane W3): the library must still HOLD today's definition after
            // the pin moves, and a parked name leaves the library once nothing requires it.
            WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": { ""require"": [""optimize-dax""], ""mode"": ""warn"" } } }");
            var baselineTitle = ParsedTitle(ShippedWorkflow("workflows-compat/v1.1.3", "optimize-dax"));
            var currentTitle = ParsedTitle(ShippedWorkflow("workflows", "optimize-dax"));
            Assert.NotEqual(baselineTitle, currentTitle);

            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                using (engine)
                {
                    await OpenModelWithFactsAsync(engine);
                    var run = await engine.StartWorkflowAsync("optimize-dax", "human");
                    Assert.Equal(baselineTitle, run.Title);

                    // Clearing the binding prunes the pin, so the library falls back to today's stock file.
                    await engine.SetWorkflowBindingAsync("create_measure", Array.Empty<string>(), "off", "human");
                    Assert.Equal(currentTitle, (await engine.GetWorkflowAsync("optimize-dax")).Title);
                    Assert.Equal(currentTitle, (await engine.GetWorkflowDocumentAsync("optimize-dax")).Metadata.Title);

                    // ...and the frozen run is untouched by that move.
                    var after = await engine.GetWorkflowRunAsync(run.RunId);
                    Assert.Equal(baselineTitle, after.Title);
                    Assert.Equal(run.TotalSteps, after.TotalSteps);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // A required name that was user-shadowed when the revision map was written is absent from that map. It
        // never chose a pre-redesign definition, so deleting the project copy must fall to today's stock file --
        // not refuse, which would take every library read down with it.
        // Q&A was dropped from the stock make-ai-ready workflow on 2026-09-23 (Microsoft retires it in February
        // 2027). A binding saved against the definition shipped in v1.2.0 pins that revision's bytes, so those
        // bytes must stay shipped under workflows-compat or the pinned binding refuses to run.
        [Fact]
        public async Task A_binding_pinned_to_the_v1_2_0_make_ai_ready_still_resolves_after_Q_and_A_left_the_stock_file()
        {
            const string v120 = "sha256:b97b5e7ade8a663aad110db345f8f424fdf5a774d8dcf72b42207b84958ba7a3";
            Assert.Equal(v120, RevisionOf(ShippedWorkflow("workflows-compat/v1.2.0", "make-ai-ready")));
            var current = File.ReadAllText(CurrentWorkflow("make-ai-ready"));
            Assert.DoesNotContain("Q&A", current);
            Assert.DoesNotContain("enable_qna", current);
            Assert.DoesNotContain("qnaPath", current);

            var ws = NewWorkspace();
            WriteSettings(ws, @"{ ""bindings"": { ""set_description"": { ""require"": [""make-ai-ready""], ""mode"": ""hard"" } }, ""requiredWorkflowRevisions"": { ""make-ai-ready"": """ + v120 + @""" } }");
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                var def = await engine.GetWorkflowAsync("make-ai-ready");
                Assert.Equal(Path.GetFullPath(ShippedWorkflow("workflows-compat/v1.2.0", "make-ai-ready")), def.FilePath);
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        // A saved workflow file (a project copy, or the v1.2.0 compat make-ai-ready) that still lists enable_qna
        // must keep running after the rename to create_linguistic_schema: check_workflow does not call it an
        // unknown op, and the running step offers the agent the op it can actually call.
        [Fact]
        public async Task A_saved_workflow_listing_enable_qna_still_checks_clean_and_offers_create_linguistic_schema()
        {
            var ws = NewWorkspace();
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "legacy-schema.md"), @"---
name: legacy-schema
title: Legacy schema set-up
whenToUse: ""Set up synonyms the old way.""
---
## Step 1: Set up the schema
Set up the synonym store.
```yaml gate
ops: [enable_qna, set_synonyms]
```
");
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                await engine.CreateModelAsync("LegacySchema", 1604);
                var check = await engine.CheckWorkflowAsync("legacy-schema");
                Assert.DoesNotContain(check.Findings, f => f.Message.Contains("enable_qna", StringComparison.Ordinal));

                var run = await engine.StartWorkflowAsync("legacy-schema", "human");
                Assert.Contains("create_linguistic_schema", run.CurrentStep.Ops);
                Assert.DoesNotContain("enable_qna", run.CurrentStep.Ops);
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        // impact_of folded into impact_assessment(scope='model') on 2026-09-24 (the slice 6 tool merge), so the two
        // stock workflows that named it were edited. A binding saved against the v1.2.1 definitions pins those
        // bytes, so they ship under workflows-compat/v1.2.1, and their retired op still checks clean via OpAliases.
        [Theory]
        [InlineData("refactor-to-calculation-group", "sha256:a9ee421b8969e36b3825b33bb83b13f40346d7b3dff19d14eda8a34ce649017e")]
        [InlineData("model-hygiene-pass", "sha256:4fc46931bd898350aef82ae167350566aa97948ea99565068a39f48fe5a19664")]
        public async Task A_binding_pinned_to_a_v1_2_1_workflow_still_resolves_after_impact_of_left_the_stock_file(string name, string v121)
        {
            Assert.Equal(v121, RevisionOf(ShippedWorkflow("workflows-compat/v1.2.1", name)));
            var current = File.ReadAllText(CurrentWorkflow(name));
            Assert.DoesNotContain("impact_of", current);
            Assert.Contains("impact_assessment", current);

            var ws = NewWorkspace();
            WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": { ""require"": [""" + name + @"""], ""mode"": ""hard"" } }, ""requiredWorkflowRevisions"": { """ + name + @""": """ + v121 + @""" } }");
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                await engine.CreateModelAsync("PinnedV121", 1604);
                var def = await engine.GetWorkflowAsync(name);
                Assert.Equal(Path.GetFullPath(ShippedWorkflow("workflows-compat/v1.2.1", name)), def.FilePath);
                var check = await engine.CheckWorkflowAsync(name);
                Assert.DoesNotContain(check.Findings, f => f.Message.Contains("impact_of", StringComparison.Ordinal));
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        // Contract 21.2 (lane W3): seven starters parked when the composer shipped. Each file's last shipped bytes
        // were frozen into workflows-compat before it left the stock shelf, so a project whose binding pinned that
        // revision still resolves it, checks it and runs it. One case per parked file, pinned by hash.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("add-relationship", "sha256:ba2475c09d42018e398eaa19903f3b7e3460c2ffd72318dc51dc0ec9a986d634")]
        [InlineData("calendar-setup", "sha256:954d75b19437fe78e334e2337d8a9d48263c50ef380128f2294bf342f797cbd4")]
        [InlineData("import-table", "sha256:893f8181778fdaa223cfc4cf912dcf6afd7fdf1c95cb2534c18543b2f4a6a901")]
        [InlineData("make-ai-ready", "sha256:b2f621a168cacd8d4d0c5c1c2affbe97df374307c53e2d1cf4b0e9859087b67f")]
        [InlineData("model-hygiene-pass", "sha256:e561f8374b48f75813f1ac973570936a703e1ed1e1c60c541224930a8805f175")]
        [InlineData("new-measure", "sha256:3014e8b6cd90e2fc582c3e5b5f7b098c35a6aaf93625d41d7eebfb045fcb6a38")]
        [InlineData("time-intelligence-variants", "sha256:74ff612cfc737626aabbd9380e66fcd965866d2ccb7740c5e3240ada2942b5a2")]
        public async Task A_binding_pinned_to_a_parked_starter_still_resolves_and_runs_from_its_freeze(string name, string pinned)
        {
            var frozen = ShippedWorkflow("workflows-compat/" + WorkflowSeedLibraryTests.ParkRelease, name);
            Assert.Equal(pinned, RevisionOf(frozen));
            Assert.False(File.Exists(ShippedWorkflow("workflows", name)), $"'{name}' is parked but still on the stock shelf");

            var ws = NewWorkspace();
            WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": { ""require"": [""" + name + @"""], ""mode"": ""hard"" } }, ""requiredWorkflowRevisions"": { """ + name + @""": """ + pinned + @""" } }");
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                await engine.CreateModelAsync("PinnedParked", 1702);
                var def = await engine.GetWorkflowAsync(name);
                Assert.Equal(Path.GetFullPath(frozen), def.FilePath);
                Assert.Equal("stock", def.Source);
                var doc = await McpTools.GetWorkflowDocument(engine, name);
                Assert.Equal(pinned, doc.ByteHash);
                var check = await engine.CheckWorkflowAsync(name);
                Assert.Null(check.ParseError);

                var run = await engine.StartWorkflowAsync(name, "human");
                Assert.Equal(name, run.Workflow);
                Assert.NotNull(run.CurrentStep);
                await engine.AbortWorkflowAsync(run.RunId, "test done", "human");
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // Lane author-gaps, L9: the starters' files moved to format 3. Each file's bytes from before the conversion were
        // frozen into workflows-compat first, so a team rule pinned to the old revision still resolves to exactly those
        // bytes and still runs, while an unpinned read gets today's format-3 file, which runs the same guided steps.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("verified-measure", "sha256:ddc0ac37be593465a35476755aa7e60da900a9dfc86f83a4fff9b3fbb4df8366")]
        [InlineData("optimize-dax", "sha256:72cf353331a9dd31f30b08b378046f2d1f975b9868453cb67648408ed8d30790")]
        [InlineData("deploy-to-production", "sha256:a7240ecfedaf67f0b12c1ba8b2d790d5d5b8d640b31e7b0816e96820d257140f")]
        [InlineData("governed-rename", "sha256:efff0ff70dbdab1ab7fae86d7312ac5d10869d45b6b80159aec7ff1dd3435608")]
        [InlineData("check-blast-radius", "sha256:100c3e09222a766124b2422c797e63c759adde5b870b03cde7531f7923cac0e3")]
        [InlineData("secure-with-rls", "sha256:daa03ec64fb9fd8f60d0e36c37e315b9d7e87e935582f9f8fa76df0769d8f838")]
        public async Task A_binding_pinned_to_a_converted_starters_old_revision_still_resolves_and_runs(string name, string pinned)
        {
            var frozen = ShippedWorkflow("workflows-compat/" + WorkflowSeedLibraryTests.ParkRelease, name);
            Assert.True(File.Exists(frozen), $"'{name}' has no freeze of its bytes from before the conversion");
            Assert.Equal(pinned, RevisionOf(frozen));
            var today = WorkflowParser.ParseFile(ShippedWorkflow("workflows", name), "stock");
            Assert.Null(today.Error);
            Assert.Equal(3, today.SchemaVersion);
            // Lane l9-finish: today's file runs as a composed draft (StarterDraftTests); the pin still gets the freeze.
            Assert.True(WorkflowParser.IsComposed(today));

            var ws = NewWorkspace();
            WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": { ""require"": [""" + name + @"""], ""mode"": ""hard"" } }, ""requiredWorkflowRevisions"": { """ + name + @""": """ + pinned + @""" } }");
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                await engine.CreateModelAsync("PinnedConverted", 1702);
                var def = await engine.GetWorkflowAsync(name);
                Assert.Equal(Path.GetFullPath(frozen), def.FilePath);
                Assert.Equal(1, def.SchemaVersion);
                var run = await engine.StartWorkflowAsync(name, "human");
                Assert.Equal(name, run.Workflow);
                Assert.NotNull(run.CurrentStep);
                await engine.AbortWorkflowAsync(run.RunId, "test done", "human");
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // The same file, unpinned: today's format-3 file drafts (lane l9-finish), and every step of the freeze is still there,
        // with the same title and gate; the steps Semanticus now runs itself are added after them or took over one that
        // only named its ops.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("verified-measure")]
        [InlineData("optimize-dax")]
        [InlineData("deploy-to-production")]
        [InlineData("governed-rename")]
        [InlineData("check-blast-radius")]
        [InlineData("secure-with-rls")]
        public async Task A_converted_starter_runs_the_same_guided_steps_as_its_freeze(string name)
        {
            var today = WorkflowParser.ParseFile(ShippedWorkflow("workflows", name), "stock");
            var before = WorkflowParser.ParseFile(ShippedWorkflow("workflows-compat/" + WorkflowSeedLibraryTests.ParkRelease, name), "stock");
            foreach (var b in before.Steps)
            {
                var t = today.Steps.Single(s => s.Id == b.Id);
                if (t.Performer == WorkflowPerformers.Semanticus) { Assert.Null(b.Gate); continue; }   // it only named its ops
                Assert.Equal(JsonSerializer.Serialize(b.Gate), JsonSerializer.Serialize(t.Gate));
            }
            var ws = NewWorkspace();
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                await engine.CreateModelAsync("Converted", 1702);
                var check = await engine.CheckWorkflowAsync(name);
                Assert.Null(check.ParseError);
                var draft = await engine.ComposeTaskAsync(null, name, null, null, null, "human");
                Assert.Equal(today.Steps.Select(s => s.Id), draft.Steps.Concat(draft.After).OrderBy(s => s.Number).Select(s => s.Id));
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // Contract 21.2: an UNPINNED call to a parked name gets a sentence naming what replaced it, never "not found",
        // on both doors and for every way of reaching a workflow by name.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("new-measure", "Describe the task in words")]
        [InlineData("import-table", "Describe the task in words")]
        [InlineData("add-relationship", "Describe the task in words")]
        [InlineData("model-hygiene-pass", "the Plan")]
        [InlineData("make-ai-ready", "the Plan")]
        [InlineData("calendar-setup", "define_calendar_from_template")]
        [InlineData("time-intelligence-variants", "generate_time_intelligence")]
        public async Task An_unpinned_call_to_a_parked_starter_teaches_its_replacement(string name, string replacement)
        {
            var ws = NewWorkspace();
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                await engine.CreateModelAsync("UnpinnedParked", 1702);
                var calls = new Func<Task>[]
                {
                    () => McpTools.StartWorkflow(engine, name),
                    () => new EngineRpcTarget(engine).startWorkflow(name),
                    () => engine.GetWorkflowAsync(name),
                    () => engine.CheckWorkflowAsync(name),
                    () => engine.GetWorkflowDocumentAsync(name),
                    () => engine.InstantiateWorkflowTemplateAsync(name, null, null, "human"),
                    () => engine.SetWorkflowBindingAsync("create_measure", new[] { name }, "hard", "human"),
                };
                foreach (var call in calls)
                {
                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(call);
                    Assert.Contains($"'{name}' is no longer a starter", ex.Message);
                    Assert.Contains(replacement, ex.Message);
                    Assert.DoesNotContain("not found", ex.Message);
                }
                Assert.DoesNotContain(await engine.ListWorkflowsAsync(), w => w.Name == name);
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // Gap 4 (coordinator, lane W3): a person's own workflow whose `call:` names a parked starter keeps working. The
        // callee resolves to its park freeze, the same definition a required binding runs, so parking never silently
        // breaks a hand-off someone already built.
        private static string ParkedCaller(string callee) => string.Join("\n",
            "---", "schemaVersion: 2", "name: my-caller", "title: My caller", "---", "",
            "## Step 1: Hand off", "",
            "```yaml step", "id: handoff", "call:", "  workflow: " + callee, "```", "",
            "Body.", "");

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("new-measure")]
        [InlineData("model-hygiene-pass")]
        [InlineData("calendar-setup")]
        public async Task A_users_workflow_that_calls_a_parked_starter_still_checks_and_runs(string callee)
        {
            var ws = NewWorkspace();
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "my-caller.md"), ParkedCaller(callee));
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                await engine.CreateModelAsync("CallsParked", 1702);
                var check = await engine.CheckWorkflowAsync("my-caller");
                Assert.Null(check.ParseError);
                Assert.DoesNotContain(check.Findings, f => f.Severity == "warn" && f.Message.Contains(callee));

                var run = await engine.StartWorkflowAsync("my-caller", "human");
                Assert.Equal("my-caller", run.Workflow);
                await engine.AbortWorkflowAsync(run.RunId, "test done", "human");

                // The callee is reachable through the hand-off only; it does not come back onto the shelf.
                Assert.DoesNotContain(await engine.ListWorkflowsAsync(), w => w.Name == callee);
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // When a parked callee cannot resolve (its freeze is missing from the install), the hand-off finding teaches
        // the replacement instead of calling it an unknown workflow.
        [Fact]
        public void An_unresolvable_call_to_a_parked_starter_teaches_its_replacement()
        {
            var caller = WorkflowParser.Parse(ParkedCaller("new-measure"));
            Assert.Null(caller.Error);
            var findings = WorkflowParser.V2Findings(caller, new[] { caller }, Array.Empty<WorkflowDef>());
            var raw = Assert.Single(findings, f => f.Message.Contains("new-measure"));
            Assert.Contains("is not a workflow this library holds", raw.Message);

            var taught = LocalEngine.TeachParkedCallees(findings);
            var finding = Assert.Single(taught, f => f.Message.Contains("new-measure"));
            Assert.Contains("'new-measure' is no longer a starter workflow", finding.Message);
            Assert.Contains("Describe the task in words", finding.Message);
            Assert.DoesNotContain("not a workflow this library holds", finding.Message);
            Assert.DoesNotContain("not found", finding.Message);
        }

        // Consulting delivery requires add-relationship for new relationships, and add-relationship parked. The team
        // rule keeps working: activating the profile pins the frozen copy, which resolves and is listed as required.
        [Fact]
        public async Task Consulting_delivery_still_requires_the_parked_add_relationship_from_its_freeze()
        {
            var ws = NewWorkspace();
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                await engine.ActivateWorkflowProfileAsync("consulting-delivery", "human");
                var frozen = ShippedWorkflow("workflows-compat/" + WorkflowSeedLibraryTests.ParkRelease, "add-relationship");
                Assert.Equal(RevisionOf(frozen), SavedRevision(ws, "add-relationship"));
                Assert.Equal(Path.GetFullPath(frozen), (await engine.GetWorkflowAsync("add-relationship")).FilePath);
                var row = (await engine.ListWorkflowsAsync()).Single(w => w.Name == "add-relationship");
                Assert.True(row.Required);
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // A saved workflow that lists tools merged on 2026-09-24 keeps checking clean, and the running step offers
        // the agent the tool it can actually call.
        [Fact]
        public async Task A_saved_workflow_listing_merged_tools_still_checks_clean_and_offers_their_survivors()
        {
            var ws = NewWorkspace();
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "legacy-merged.md"), @"---
name: legacy-merged
title: Legacy merged tools
whenToUse: ""Check the model the old way.""
---
## Step 1: Check the model
Look at the graph and the findings.
```yaml gate
ops: [model_graph_summary, bpa_summary, bpa_fix_all, load_readiness_rules, benchmark_dax_coldwarm, impact_of]
```
");
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                await engine.CreateModelAsync("LegacyMerged", 1604);
                var check = await engine.CheckWorkflowAsync("legacy-merged");
                foreach (var old in new[] { "model_graph_summary", "bpa_summary", "bpa_fix_all", "load_readiness_rules", "benchmark_dax_coldwarm", "impact_of" })
                    Assert.DoesNotContain(check.Findings, f => f.Message.Contains(old, StringComparison.Ordinal));

                var run = await engine.StartWorkflowAsync("legacy-merged", "human");
                Assert.Equal(new[] { "get_model_graph", "bpa_scan", "apply_safe_fixes", "load_rules", "benchmark_dax", "impact_assessment" },
                    run.CurrentStep.Ops);
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public async Task A_required_name_missing_from_an_existing_revision_map_falls_to_the_current_definition()
        {
            var ws = NewWorkspace();
            var pinned = RevisionOf(ShippedWorkflow("workflows", "optimize-dax"));
            WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": { ""require"": [""new-measure"", ""optimize-dax""], ""mode"": ""hard"" } }, ""requiredWorkflowRevisions"": { ""optimize-dax"": """ + pinned + @""" } }");
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                // new-measure is parked (lane W3), so its current definition is its park freeze.
                var def = await engine.GetWorkflowAsync("new-measure");
                Assert.Equal(Path.GetFullPath(CurrentWorkflow("new-measure")), def.FilePath);
                Assert.Equal(def.FilePath, (await engine.GetWorkflowDocumentAsync("new-measure")).Path);
                Assert.Contains(await engine.ListWorkflowsAsync(), x => x.Name == "new-measure");
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        // A legacy binding can name a workflow that no longer exists (the project copy was deleted, or the file
        // was hand-edited). That resolved to nothing before pinning existed, so the library read must still
        // succeed and leave the complaint to binding enforcement -- not take every workflow down with it.
        [Fact]
        public async Task A_binding_naming_a_workflow_that_does_not_exist_still_reads_the_library()
        {
            var ws = NewWorkspace();
            WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": { ""require"": [""deleted-project-flow""], ""mode"": ""hard"" } } }");
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                var library = await engine.ListWorkflowsAsync();
                Assert.Contains(library, x => x.Name == "optimize-dax");
                Assert.DoesNotContain(library, x => x.Name == "deleted-project-flow");
                Assert.Equal(Path.GetFullPath(ShippedWorkflow("workflows", "optimize-dax")),
                    (await engine.GetWorkflowDocumentAsync("optimize-dax")).Path);
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- (g) §9.10C: a userDisablable:false mandate is locked against the AGENT door -----------------------

        [Fact]
        public async Task UserDisablable_false_locks_the_agent_door_but_not_the_human()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
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
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Corrupt_settings_fails_closed_but_a_missing_file_flows_free()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
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
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
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

        [Trait(TestCost.Key, TestCost.Sweep)]
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

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Array_binding_target_fact_is_unknown_and_linted()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
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
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
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

        // ==================== (l) the loop SETUP row is not a performing step ====================
        // Public loop start admits a loop-bearing file unexpanded, so a run can now sit on an expansion-only
        // current row: the row that fixes the list, runs no gate and shows no ops. `WorkflowRunState.CurrentStep`
        // still hands back the AUTHORED body step for that row, ops and all, so the two readers that ask "is a
        // declaring step current?", the §9.10A step-scoped binding exemption and the E3(b) authored-measure
        // baseline, used to answer yes before any iteration existed. Both now route the question through
        // `WorkflowRunner.IsExpansionOnlySubmission`, the same classification the runner already uses for the
        // three setup shapes, so the setup row is neither an exemption nor a provenance moment. Every name and
        // value below is invented.

        // The three setup shapes, each with `ops: [create_measure]` on the LOOP BODY. Two of them carry inputs,
        // so these vehicles start Pro; the entitlement axis is not what is under test here.
        private const string InlineLoopVehicleMd = @"---
schemaVersion: 2
name: loop-inline
title: Inline loop vehicle
whenToUse: ""Author one measure per authored item.""
---
## Step 1: Author per item
Author the measure for this item.
```yaml gate
ops: [create_measure]
```
```yaml step
id: each
forEach:
  in: [alpha]
  as: item
```
";

        // Preparation: the loop's source is declared on the loop's OWN gate, so the setup row asks that question.
        private const string SelfSourcedLoopVehicleMd = @"---
schemaVersion: 2
name: loop-self
title: Self-sourced loop vehicle
whenToUse: ""Author one measure per chosen item.""
---
## Step 1: Author per item
Author the measure for this item.
```yaml gate
ops: [create_measure]
inputs:
  - name: items
    question: ""Which items?""
```
```yaml step
id: each
forEach:
  in: inputs.items
  as: item
```
";

        // Deferred: the source is declared on an EARLIER, NON-ADJACENT step, so the setup row asks nothing and
        // takes one no-answer submission. (An adjacent declarer resolves the list on its own submission, so the
        // unexpanded row never becomes current; the intervening ordinary row is what makes this shape reachable.)
        // This is the shape whose current row carries the full authored gate with no question of its own, which
        // is where a raw index or iteration test would have gone wrong.
        private const string DeferredLoopVehicleMd = @"---
schemaVersion: 2
name: loop-deferred
title: Deferred loop vehicle
whenToUse: ""Author one measure per chosen item.""
---
## Step 1: Choose
Choose the items.
```yaml gate
inputs:
  - name: items
    question: ""Which items?""
```
```yaml step
id: choose
```
## Step 2: Between
Ordinary work between the source and the loop.
```yaml step
id: between
```
## Step 3: Author per item
Author the measure for this item.
```yaml gate
ops: [create_measure]
```
```yaml step
id: each
forEach:
  in: inputs.items
  as: item
```
";

        private static string NewLoopWorkspace(string fileName, string md)
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-wfloopbind-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", fileName), md);
            return ws;
        }

        // ---- (l1) a required-loop SETUP row grants no hard-binding exemption, in all three setup shapes -------

        [Fact]
        public async Task Setup_rows_of_a_required_loop_grant_no_hard_binding_exemption()
        {
            // Inline: step 1 IS the loop, so the run starts ON the setup row.
            await AssertSetupRowIsNotExemptAsync("loop-inline.md", InlineLoopVehicleMd, reachSetup: null);

            // Preparation: the run also starts on the setup row, but that row asks the loop's own source question.
            await AssertSetupRowIsNotExemptAsync("loop-self.md", SelfSourcedLoopVehicleMd,
                reachSetup: null, setupAnswers: "{\"items\":\"alpha\"}");

            // Deferred: an ordinary step answers the source first, THEN the setup row becomes current.
            await AssertSetupRowIsNotExemptAsync("loop-deferred.md", DeferredLoopVehicleMd,
                reachSetup: async (engine, runId) =>
                {
                    await engine.SubmitWorkflowStepAsync(runId, "choose", "{\"items\":\"alpha\"}", "human");
                    return await engine.SubmitWorkflowStepAsync(runId, "between", "{}", "human");
                });
        }

        private static async Task AssertSetupRowIsNotExemptAsync(
            string fileName, string md,
            Func<LocalEngine, string, Task<WorkflowRunView>> reachSetup, string setupAnswers = "{}")
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            var ws = NewLoopWorkspace(fileName, md);
            WriteSettings(ws, @"{ ""bindings"": { ""create_measure"": { ""require"": [""" + name + @"""], ""mode"": ""hard"" } } }");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                using (engine)
                {
                    await OpenModelWithFactsAsync(engine);
                    var run = await engine.StartWorkflowAsync(name, "human");
                    Assert.Equal("active", run.Status);
                    if (reachSetup != null) run = await reachSetup(engine, run.RunId);

                    // The setup row is current and shows NO ops: nothing here advertises the bound op.
                    Assert.Equal("each", run.CurrentStep.StepId);
                    Assert.Empty(run.CurrentStep.Ops);

                    // Yet the authored body declares create_measure. A hard binding must still bite: no iteration
                    // is current, so no step is performing this op.
                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.CreateMeasureAsync("table:Facts", "Premature", "SUM(Facts[Amount])", "agent"));
                    Assert.Contains("create_measure", ex.Message);
                    Assert.Contains(name, ex.Message);
                    Assert.DoesNotContain(await engine.ListMeasuresAsync(), m => m.Name == "Premature");

                    // The refusal did not disturb the run: the same setup row is still current and still takes its
                    // submission, and the FIRST ACTUAL ITERATION does grant the exemption.
                    var iteration = await engine.SubmitWorkflowStepAsync(run.RunId, "each", setupAnswers, "human");
                    Assert.Equal("each#0", iteration.CurrentStep.StepId);
                    Assert.Equal(new[] { "create_measure" }, iteration.CurrentStep.Ops);

                    var landed = await engine.CreateMeasureAsync("table:Facts", "AtIteration", "SUM(Facts[Amount])", "agent");
                    Assert.NotNull(landed);
                    Assert.Contains(await engine.ListMeasuresAsync(), m => m.Name == "AtIteration");
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- (l2) a warn-mode outside edit DURING setup is advised and gets no authored provenance ------------

        // Deferred loop, plus the E3(b) provenance readout: step 1 authors the candidate and declares the source,
        // the loop body revises it once per item, and a closing step proves the candidate against its witness.
        // The two run-scoped answers survive the loop frames, so the closing step can still name its target.
        private const string DeferredProvenanceVehicleMd = @"---
schemaVersion: 2
name: loop-drift
title: Deferred loop drift vehicle
whenToUse: ""Author a candidate, revise it per item, then prove it.""
strictness: hard
---
## Step 1: Author the candidate
Create the candidate and name the items.
```yaml gate
ops: [create_measure]
inputs:
  - name: items
    question: ""Which items?""
  - name: target
    question: ""The created measure.""
    type: objectRef
    required: required
    scope: run
  - name: witnessDax
    question: ""The witness.""
    type: text
    required: required
    scope: run
```
```yaml step
id: choose
```
## Step 2: Between
Ordinary work between the source and the loop.
```yaml step
id: between
```
## Step 3: Revise per item
Revise the candidate for this item.
```yaml gate
ops: [update_measure]
```
```yaml step
id: each
forEach:
  in: inputs.items
  as: item
```
## Step 4: Prove equivalence
Prove the candidate equals its witness.
```yaml gate
verify:
  - kind: dax_equivalence
    probe: witnessDax
```
```yaml step
id: prove
```
";

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_warn_edit_on_the_setup_row_is_advised_and_never_becomes_workflow_authored()
        {
            var ws = NewLoopWorkspace("loop-drift.md", DeferredProvenanceVehicleMd);
            WriteSettings(ws, @"{ ""bindings"": { ""update_measure"": { ""require"": [""loop-drift""], ""mode"": ""warn"" } } }");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: true), ws);
            try
            {
                using (engine)
                {
                    await engine.CreateModelAsync("Drift", 1604);
                    await engine.CreateTableAsync("Sales", "human");
                    var run = await engine.StartWorkflowAsync("loop-drift", "human");

                    // Authored AT the declaring step: this write really is workflow-authored, and its hash is the
                    // E3(b) baseline the closing equivalence checks against.
                    var mref = await engine.CreateMeasureAsync("table:Sales", "Candidate", "1", "human");
                    await engine.SubmitWorkflowStepAsync(run.RunId, "choose",
                        "{\"items\":\"alpha\", \"target\":\"" + mref + "\", \"witnessDax\":\"EVALUATE ROW(\\\"v\\\", 1)\"}", "human");
                    run = await engine.SubmitWorkflowStepAsync(run.RunId, "between", "{}", "human");

                    // The deferred setup row is now current. It runs no gate and shows no ops.
                    Assert.Equal("each", run.CurrentStep.StepId);
                    Assert.Empty(run.CurrentStep.Ops);

                    // A warn-bound edit made HERE is outside the required workflow's performing step: warn lets it
                    // land, but the advisory IS the enforcement artifact and must be published.
                    var captured = new List<ActivityEvent>();
                    void Handler(ActivityEvent e) => captured.Add(e);
                    sessions.Bus.Activity += Handler;
                    try { await engine.SetDaxAsync(mref, "2", "agent"); }
                    finally { sessions.Bus.Activity -= Handler; }

                    var advisory = Assert.Single(captured, e => e.Kind == "landed_outside_required_workflow");
                    Assert.Equal("update_measure", advisory.Target);
                    Assert.Contains("loop-drift", advisory.Label);

                    // ...and it must NOT have been recorded as workflow-authored. Walk on out of the loop to the
                    // step that reads that provenance: the premature write is real drift, and the equivalence
                    // verify has to say so. Had the setup row laundered it into the authored baseline, the
                    // recorded hash would match the current expression and this drift would go unseen.
                    run = await engine.SubmitWorkflowStepAsync(run.RunId, "each", "{}", "human");
                    Assert.Equal("each#0", run.CurrentStep.StepId);
                    run = await engine.SubmitWorkflowStepAsync(run.RunId, "each#0", "{}", "human");
                    Assert.Equal("prove", run.CurrentStep.StepId);

                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.SubmitWorkflowStepAsync(run.RunId, "prove", "{}", "human"));
                    Assert.Contains("drift", ex.Message);

                    var after = await engine.GetWorkflowRunAsync(run.RunId);
                    var v = after.Steps.Single(s => s.StepId == "prove").VerifyResults.Single(x => x.Kind == "dax_equivalence");
                    Assert.Equal("unavailable", v.Status);
                    Assert.Contains("drift", v.Detail);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }
    }
}
