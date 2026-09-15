using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class WorkflowProfileTests
    {
        private sealed class Fake : IEntitlement
        {
            public bool IsPro { get; }
            public EntitlementInfo Info => new EntitlementInfo { Tier = IsPro ? "pro" : "free" };
            public Fake(bool pro) { IsPro = pro; }
        }

        [Fact]
        public async Task Profiles_replace_the_simple_policy_atomically_and_manual_edits_mark_it_custom()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-workflow-profiles", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "Model.bim");
            File.Copy(TestModels.FindBim(), model);
            try
            {
                using var engine = new LocalEngine(new SessionManager(), new Fake(true));
                await engine.OpenAsync(model);

                var team = await engine.ActivateWorkflowProfileAsync("team-standard", "human");
                Assert.Equal("team-standard", team.ActiveProfile);
                Assert.Equal("warn", team.Policy.Enforcement);
                Assert.Equal(2, team.Policy.Bindings.Length);
                Assert.All(team.Policy.Bindings, x => Assert.Equal("verified-measure", Assert.Single(x.Require)));
                Assert.Single((await engine.ListWorkflowProfilesAsync()).Where(x => x.Selected));
                var settingsFile = Path.Combine(dir, ".semanticus", "workflow-settings.json");
                var settings = JsonNode.Parse(File.ReadAllText(settingsFile)).AsObject();
                var currentFile = Path.Combine(AppContext.BaseDirectory, "workflows", "verified-measure.md");
                var currentRevision = "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(currentFile))).ToLowerInvariant();
                Assert.Equal(currentRevision, settings["requiredWorkflowRevisions"]["verified-measure"].GetValue<string>());

                await engine.SetWorkflowActivationAsync("make-ai-ready", "date.dayOfMonth >= 28", "off", "human");
                Assert.Equal("custom", (await engine.GetWorkflowPolicyAsync()).ActiveProfile);

                var reset = await engine.ActivateWorkflowProfileAsync("standard", "human");
                Assert.Equal("standard", reset.Policy.ActiveProfile);
                Assert.Null(reset.Policy.Enforcement);
                Assert.Empty(reset.Policy.Bindings);
                Assert.All(reset.Policy.Workflows, x => Assert.True(x.Enabled));
                Assert.All(reset.Policy.Workflows, x => Assert.True(x.Active));
                Assert.False(JsonNode.Parse(File.ReadAllText(settingsFile)).AsObject().ContainsKey("requiredWorkflowRevisions"));
            }
            finally { Directory.Delete(dir, true); }
        }

        /// <summary>Workflows became a whole Pro feature on 2026-09-15, so free is refused at the profile surface
        /// itself, the read included. The safe reset is no longer a free escape hatch, and the per-profile Pro flag
        /// survives only as a menu marker a Pro reader sees.</summary>
        [Fact]
        public async Task Workflow_profiles_are_pro_to_list_and_to_activate()
        {
            var dir = Path.Combine(Path.GetTempPath(), "sem-workflow-profile-free", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "Model.bim");
            File.Copy(TestModels.FindBim(), model);
            try
            {
                using (var free = new LocalEngine(new SessionManager(), new Fake(false)))
                {
                    await free.OpenAsync(model);
                    var listed = await Assert.ThrowsAsync<EntitlementException>(() => free.ListWorkflowProfilesAsync());
                    Assert.StartsWith("Workflows is a Semanticus Pro feature.", listed.Message);
                    var reset = await Assert.ThrowsAsync<EntitlementException>(() => free.ActivateWorkflowProfileAsync("standard", "human"));
                    Assert.StartsWith("Workflows is a Semanticus Pro feature.", reset.Message);
                }

                using (var pro = new LocalEngine(new SessionManager(), new Fake(true)))
                {
                    await pro.OpenAsync(model);
                    var profiles = await pro.ListWorkflowProfilesAsync();
                    Assert.Equal(3, profiles.Length);
                    Assert.False(profiles.Single(x => x.Name == "standard").Pro);
                    Assert.True(profiles.Single(x => x.Name == "team-standard").Pro);

                    var standard = await pro.ActivateWorkflowProfileAsync("standard", "human");
                    Assert.Equal("standard", standard.ActiveProfile);
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Unknown_profile_names_the_discovery_op()
        {
            using var engine = new LocalEngine(new SessionManager(), new Fake(true));
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ActivateWorkflowProfileAsync("missing", "agent"));
            Assert.Contains("list_workflow_profiles", ex.Message);
        }
    }
}
