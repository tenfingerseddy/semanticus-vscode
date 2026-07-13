using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// §9a availability toggle + §9b whenToUse routing hint. Pinned here: a disabled workflow stays IN the
    /// listing (marked enabled:false so the designer can re-enable it) but start_workflow refuses it with a
    /// teaching error; the toggle round-trips through the settings file and re-broadcasts; the write MERGES
    /// (per-workflow strictness survives); enabling REMOVES the key (absent = the safe default, keeps the file
    /// minimal); a malformed settings file never hides the library (fail-safe = AVAILABLE); the read is live off
    /// disk (hot-reload — a settings edit by the other door is seen without a restart); and whenToUse surfaces on
    /// WorkflowInfo so a caller can pick among siblings. Availability is Free — curating a menu is content.
    /// </summary>
    public sealed class WorkflowAvailabilityTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info { get; }
            public Fake(bool pro) { IsPro = pro; Info = new EntitlementInfo { Tier = pro ? "pro" : "free" }; }
        }

        // A minimal, un-gated workflow: no enforced gate → a FREE start completes, so the availability axis is
        // tested in isolation from the entitlement/strictness axes. Carries a whenToUse hint for §9b.
        private const string VehicleMd = @"---
name: avail-vehicle
title: Availability vehicle
whenToUse: ""Pick this when you are testing the availability toggle among siblings.""
---
## Step 1: Do the thing
Just do it. There is no gate here.
";

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-wfavail-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "avail-vehicle.md"), VehicleMd);
            return ws;
        }

        private static string SettingsFile(string ws) => Path.Combine(ws, ".semanticus", "workflow-settings.json");

        // ---- the toggle end-to-end: list keeps it (flagged), start flips ------------------------------------------

        [Fact]
        public async Task Disable_keeps_it_listed_but_refuses_start_and_enable_restores()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(pro: false), ws);   // FREE: availability is free
            try
            {
                using (engine)
                {
                    // Default: available and startable.
                    var before = (await engine.ListWorkflowsAsync()).First(w => w.Name == "avail-vehicle");
                    Assert.True(before.Enabled);
                    var run0 = await engine.StartWorkflowAsync("avail-vehicle", "human");
                    Assert.Equal("completed", (await engine.SubmitWorkflowStepAsync(run0.RunId, "step-1", "{}", "human")).Status);

                    // Disable: still LISTED (flagged), but start is refused with a teaching error that names the fix.
                    var listed = await engine.SetWorkflowEnabledAsync("avail-vehicle", false, "human");
                    Assert.False(listed.First(w => w.Name == "avail-vehicle").Enabled);   // returned library reflects it live
                    Assert.Contains("avail-vehicle", listed.Select(w => w.Name));         // NOT dropped
                    var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.StartWorkflowAsync("avail-vehicle", "human"));
                    Assert.Contains("turned off", ex.Message);
                    Assert.Contains("set_workflow_enabled", ex.Message);

                    // Enable: back on the menu and startable again.
                    var reenabled = await engine.SetWorkflowEnabledAsync("avail-vehicle", true, "human");
                    Assert.True(reenabled.First(w => w.Name == "avail-vehicle").Enabled);
                    var run1 = await engine.StartWorkflowAsync("avail-vehicle", "human");
                    Assert.Equal("completed", (await engine.SubmitWorkflowStepAsync(run1.RunId, "step-1", "{}", "human")).Status);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- the write merges + enabling removes the key (minimal file) --------------------------------------------

        [Fact]
        public async Task Set_enabled_merges_and_enabling_removes_the_key()
        {
            var ws = NewWorkspace();
            var file = SettingsFile(ws);
            File.WriteAllText(file, "{ \"workflows\": { \"avail-vehicle\": { \"strictness\": \"warn\" } } }");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false), ws);
            try
            {
                using (engine)
                {
                    await engine.SetWorkflowEnabledAsync("avail-vehicle", false, "human");
                    var json = File.ReadAllText(file);
                    Assert.Contains("\"enabled\": false", json);     // the toggle landed…
                    Assert.Contains("\"strictness\": \"warn\"", json); // …and the sibling per-workflow key survived

                    await engine.SetWorkflowEnabledAsync("avail-vehicle", true, "human");
                    json = File.ReadAllText(file);
                    Assert.DoesNotContain("\"enabled\"", json);        // enabling REMOVES the key (absent = default)
                    Assert.Contains("\"strictness\": \"warn\"", json); // strictness still preserved

                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.SetWorkflowEnabledAsync("does-not-exist", false, "human"));
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // Re-enabling a workflow whose ONLY per-workflow key was enabled:false must leave no orphan {} behind —
        // the emptied per-workflow object (and the emptied "workflows" container) are pruned, so the file no
        // longer names it at all. (Contrast the test above, where a surviving strictness keeps the subtree.)
        [Fact]
        public async Task Enabling_a_workflow_with_only_the_toggle_prunes_it_from_the_file()
        {
            var ws = NewWorkspace();
            var file = SettingsFile(ws);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false), ws);
            try
            {
                using (engine)
                {
                    // Disable with no other per-workflow keys → enabled:false is the only content.
                    await engine.SetWorkflowEnabledAsync("avail-vehicle", false, "human");
                    Assert.Contains("avail-vehicle", File.ReadAllText(file));

                    // Re-enable removes the key; with nothing left, the whole subtree is pruned.
                    await engine.SetWorkflowEnabledAsync("avail-vehicle", true, "human");
                    var json = File.ReadAllText(file);
                    Assert.DoesNotContain("avail-vehicle", json);       // no orphan {} object — the name is gone
                    Assert.DoesNotContain("\"enabled\"", json);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- fail-safe: a malformed settings file never hides the library ------------------------------------------

        [Fact]
        public async Task Malformed_settings_defaults_to_available()
        {
            var ws = NewWorkspace();
            File.WriteAllText(SettingsFile(ws), "{ this is not valid json ]");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false), ws);
            try
            {
                using (engine)
                {
                    var info = (await engine.ListWorkflowsAsync()).First(w => w.Name == "avail-vehicle");
                    Assert.True(info.Enabled);                                   // garbage settings ⇒ AVAILABLE, not hidden
                    var run = await engine.StartWorkflowAsync("avail-vehicle", "human");   // and still startable
                    Assert.Equal("completed", (await engine.SubmitWorkflowStepAsync(run.RunId, "step-1", "{}", "human")).Status);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- hot-reload: the read is live off disk (the other door's edit is seen without a restart) ---------------

        [Fact]
        public async Task Availability_is_read_live_off_disk()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false), ws);
            try
            {
                using (engine)
                {
                    Assert.True((await engine.ListWorkflowsAsync()).First(w => w.Name == "avail-vehicle").Enabled);

                    // Simulate the human door (or a teammate's git pull) writing the settings file directly.
                    File.WriteAllText(SettingsFile(ws),
                        "{ \"workflows\": { \"avail-vehicle\": { \"enabled\": false } } }");

                    Assert.False((await engine.ListWorkflowsAsync()).First(w => w.Name == "avail-vehicle").Enabled);
                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.StartWorkflowAsync("avail-vehicle", "human"));
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }

        // ---- §9b: the whenToUse routing hint surfaces on the listing -----------------------------------------------

        [Fact]
        public async Task WhenToUse_surfaces_on_workflow_info()
        {
            var ws = NewWorkspace();
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, new Fake(false), ws);
            try
            {
                using (engine)
                {
                    var info = (await engine.ListWorkflowsAsync()).First(w => w.Name == "avail-vehicle");
                    Assert.Equal("Pick this when you are testing the availability toggle among siblings.", info.WhenToUse);
                    // and the full definition carries it too (get_workflow → WorkflowDef.WhenToUse)
                    Assert.Equal(info.WhenToUse, (await engine.GetWorkflowAsync("avail-vehicle")).WhenToUse);
                }
            }
            finally { sessions.Dispose(); try { Directory.Delete(ws, true); } catch { } }
        }
    }
}
