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
    /// UAT-S02-j3-004, UAT-S02-2-004 / UAT-S02-j7-004 and UAT-S02-2-005 (Sol, 25 Sep): one team rule whose saved
    /// stock revision is no longer shipped took the whole workflow library down with it. list_workflows and
    /// get_workflow_policy threw, so the page stuck on "Reading your workflows" with no Margin review, Team rules said
    /// no workflow was required, and set_workflow_enforcement WROTE the setting and then threw from its library
    /// rebroadcast, so the switch kept showing the old state while the engine held the new one.
    ///
    /// The unavailable pin now stays loud where it matters (the pinned workflow cannot be read or run, and it is
    /// listed with the reason as its error) without hiding every other workflow or failing a setter that landed.
    /// </summary>
    public sealed class WorkflowUnavailablePinTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info { get; } = new EntitlementInfo { Tier = "pro" };
        }

        private const string MarginReview = @"---
name: margin-review
title: Margin review
---
## Step 1: Confirm the margin convention
Restate how margin is defined.
";

        // The UAT fixture's shape: verified-measure (a CURRENT stock workflow) is required and pinned to a revision
        // this install does not ship, beside a project workflow of the person's own.
        private const string Settings = @"{ ""bindings"": { ""update_measure"": { ""require"": [""verified-measure""], ""mode"": ""warn"" } }, ""requiredWorkflowRevisions"": { ""verified-measure"": ""sha256:unavailable"" } }";

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-wfpin-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(ws, ".semanticus", "workflows"));
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflows", "margin-review.md"), MarginReview);
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflow-settings.json"), Settings);
            return ws;
        }

        private static Task InWorkspace(Func<LocalEngine, Task> body) => InWorkspace((engine, _) => body(engine));

        private static async Task InWorkspace(Func<LocalEngine, string, Task> body)
        {
            var ws = NewWorkspace();
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(), ws);
                await body(engine, ws);
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        [Fact]
        public Task The_library_still_lists_every_other_workflow_and_names_the_unavailable_one() => InWorkspace(async engine =>
        {
            var list = await engine.ListWorkflowsAsync();

            var margin = Assert.Single(list, w => w.Name == "margin-review");
            Assert.Null(margin.Error);
            Assert.Contains(list, w => w.Name == "optimize-dax" && w.Error == null);   // an unpinned stock workflow too

            var pinned = Assert.Single(list, w => w.Name == "verified-measure");
            Assert.True(pinned.Required);
            Assert.Contains("pinned", pinned.Error);
            Assert.Contains("unavailable", pinned.Error);
        });

        [Fact]
        public Task The_pinned_workflow_is_never_swapped_for_todays_file() => InWorkspace(async engine =>
        {
            // Reads and runs of the pinned name still refuse with the reason, exactly as before this fix.
            var read = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.GetWorkflowAsync("verified-measure"));
            Assert.Contains("unavailable", read.Message);
            var run = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartWorkflowAsync("verified-measure", "agent"));
            Assert.Contains("unavailable", run.Message);
            var doc = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.GetWorkflowDocumentAsync("verified-measure"));
            Assert.Equal(read.Message, doc.Message);
        });

        [Fact]
        public Task Team_rules_show_the_required_workflow() => InWorkspace(async engine =>
        {
            var policy = await engine.GetWorkflowPolicyAsync();

            var rule = Assert.Single(policy.Bindings, b => b.Op == "update_measure");
            Assert.Equal(new[] { "verified-measure" }, rule.Require);
            Assert.Contains(policy.Workflows, w => w.Name == "margin-review");
        });

        [Fact]
        public Task The_checks_switch_answers_with_what_it_saved_both_ways() => InWorkspace(async engine =>
        {
            var off = await engine.SetWorkflowEnforcementAsync("off", "human");
            Assert.False(off.Enforced);
            Assert.False((await engine.GetWorkflowEnforcementAsync()).Enforced);

            var restored = await engine.SetWorkflowEnforcementAsync("default", "agent");
            Assert.True(restored.Enforced);
            Assert.Null(restored.Mode);
            Assert.True((await engine.GetWorkflowEnforcementAsync()).Enforced);
        });

        // UAT-S11-3-002 / -003 (Sol's reviewer-off retest): the same setter with warn, read back from disk too. Every
        // mode the setter saves is the mode it answers with and the mode the file holds.
        [Theory]
        [InlineData("warn", "warn", true)]
        [InlineData("off", "off", false)]
        [InlineData("hard", "hard", true)]
        [InlineData("default", null, true)]
        public Task The_setter_answers_what_the_file_now_holds(string mode, string saved, bool enforced) => InWorkspace(async (engine, ws) =>
        {
            var result = await engine.SetWorkflowEnforcementAsync(mode, "agent");
            Assert.Equal(saved, result.Mode);
            Assert.Equal(enforced, result.Enforced);
            Assert.Equal(saved, OnDisk(ws));
            Assert.Equal(saved, (await engine.GetWorkflowEnforcementAsync()).Mode);
        });

        // The same class of defect by another road: a malformed revision map made the library rebroadcast throw
        // AFTER the setter had saved, so the caller was told it failed while the file held the new mode. A saved
        // setting is reported as saved; the broken map stays loud where the library is read.
        [Fact]
        public async Task A_setter_that_saved_never_reports_failure_because_the_rebroadcast_failed()
        {
            var ws = NewWorkspace();
            File.WriteAllText(Path.Combine(ws, ".semanticus", "workflow-settings.json"),
                @"{ ""bindings"": { ""update_measure"": { ""require"": [""verified-measure""], ""mode"": ""warn"" } }, ""requiredWorkflowRevisions"": { ""verified-measure"": 5 } }");
            try
            {
                using var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Fake(), ws);
                var off = await engine.SetWorkflowEnforcementAsync("off", "agent");
                Assert.Equal("off", off.Mode);
                Assert.Equal("off", OnDisk(ws));
                await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ListWorkflowsAsync());   // still loud there
            }
            finally { try { Directory.Delete(ws, true); } catch { } }
        }

        private static string OnDisk(string ws)
        {
            var file = Path.Combine(ws, ".semanticus", "workflow-settings.json");
            return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))?["strictness"]?.GetValue<string>();
        }

        [Fact]
        public Task Other_workflows_still_run() => InWorkspace(async engine =>
        {
            var run = await engine.StartWorkflowAsync("margin-review", "agent");
            Assert.Equal("margin-review", run.Workflow);
        });
    }
}
