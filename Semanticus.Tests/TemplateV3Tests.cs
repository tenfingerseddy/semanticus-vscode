using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Lane l9-finish, item 3. The template renderer (LocalEngine.Templates.cs RenderInstance) writes format 3 for a
    /// format-3 template: no v1-only frontmatter keys (template, template_version, instantiated and slot_values ride the
    /// format's own `provenance:` map), and every slot answer that lands in YAML is quoted, so a title like
    /// "FY26: July # final" reads back exactly. Then hard-measure, deploy-freeze-guard and month-end-close move to
    /// format 3 as the starters did, each drafts, and their format-1 bytes are frozen in workflow-templates-compat.
    /// A person's own format-1 template still renders exactly as before.
    /// </summary>
    public sealed class TemplateV3Tests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        }

        private static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

        private static string NewWorkspace()
        {
            var ws = Path.Combine(Path.GetTempPath(), "sem-tmpl3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ws);
            return ws;
        }

        /// <summary>Slot answers chosen to break unquoted YAML: a colon-space, a hash, double quotes and a line break.</summary>
        public static readonly TheoryData<string, string, string> Templates = new TheoryData<string, string, string>
        {
            { "hard-measure", "{\"measure_goal\":\"Margin %: gross \\\"after returns\\\" # by region\"}",
              "Author \"Margin %: gross \\\"after returns\\\" # by region\" and reconcile it against the requirement at every grain" },
            { "deploy-freeze-guard", "{\"freeze_window\":\"month-end: last 2 days # prod only\",\"change_policy\":\"fixes: approved only\",\"override_authority\":\"the release manager\"}",
              "Release checks for month-end: last 2 days # prod only" },
            { "month-end-close", "{\"period_label\":\"FY26: July # final\",\"close_checklist\":\"lock refresh\\nreconcile GL\",\"control_totals\":\"measure:Sales/Quantity ~ (grand total) ~ 42\",\"signoff_owner\":\"the Financial Controller\"}",
              "Month-end close for FY26: July # final" },
        };

        // Lean suite (8 Oct): the default run keeps the first of the three templates; all three are the sweep.
        public static IEnumerable<object[]> TemplatesSample() => Templates.Take(1);

        [Theory]
        [MemberData(nameof(TemplatesSample))]
        public Task A_format_3_template_renders_a_format_3_workflow_that_drafts_with_its_title_exact_sample(string name, string values, string title) =>
            A_format_3_template_renders_a_format_3_workflow_that_drafts_with_its_title_exact(name, values, title);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Templates))]
        public async Task A_format_3_template_renders_a_format_3_workflow_that_drafts_with_its_title_exact(string name, string values, string title)
        {
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true, workspace: NewWorkspace());
            using (sm)
            {
                var template = await engine.GetWorkflowTemplateAsync(name);
                Assert.Null(template.Error);
                Assert.StartsWith("---\nschemaVersion: 3\n", template.Markdown.Replace("\r\n", "\n"));

                var made = await engine.InstantiateWorkflowTemplateAsync(name, "my-" + name, values, "human");
                Assert.Single(made);
                var def = await engine.GetWorkflowAsync("my-" + name);
                Assert.Null(def.Error);
                Assert.Equal(3, def.SchemaVersion);
                Assert.Equal(title.Replace("\\\"", "\""), def.Title);
                var text = File.ReadAllText(def.FilePath).Replace("\r\n", "\n");
                var front = text.Split("\n---\n")[0];
                foreach (var v1Key in new[] { "template:", "template_version:", "instantiated:", "slot_values:", "kind:", "slots:" })
                    Assert.DoesNotContain("\n" + v1Key, front);
                // The provenance rides the format's own map, and month-end-close's gate still reads its slot values there.
                Assert.Equal(name, def.Provenance["template"]);
                using (var sv = JsonDocument.Parse(def.Provenance["slot_values"]))
                    Assert.Equal(JsonDocument.Parse(values).RootElement.EnumerateObject().First().Value.GetString(),
                                 sv.RootElement.GetProperty(JsonDocument.Parse(values).RootElement.EnumerateObject().First().Name).GetString());
                Assert.True(WorkflowParser.IsComposed(def));
                Assert.Empty(WorkflowParser.UnexecutableControlFields(def));
                var draft = await engine.ComposeTaskAsync(null, "my-" + name, null, null, null, "human");
                Assert.Equal(def.Steps.Length, draft.Steps.Length + draft.After.Length);
            }
        }

        [Theory]
        [InlineData("hard-measure", "1ebfb4e9e2015482dc64efecbd38b2fd4778a8f1cd2d874b32b636c0f389dd8d")]
        [InlineData("deploy-freeze-guard", "bb8215ffdf5b7609f76012b276896c9e067c48d2ef6068e773dd58d4bc7a9689")]
        [InlineData("month-end-close", "6c7568d526836c879287fda9fdfefa167877daaf9a33fad5fb0969200cb1f029")]
        public void A_converted_template_keeps_its_format_1_bytes_frozen_and_every_gate(string name, string sha)
        {
            var root = RepoRoot();
            var frozenFile = Path.Combine(root, "Semanticus.Engine", "workflow-templates-compat", WorkflowSeedLibraryTests.ParkRelease, name + ".md");
            Assert.True(File.Exists(frozenFile), $"{name} has no freeze of its format-1 bytes.");
            Assert.Equal(sha, Sha(frozenFile));
            var frozen = WorkflowParser.ParseFile(frozenFile, "stock");
            var today = WorkflowParser.ParseFile(Path.Combine(root, "Semanticus.Engine", "workflow-templates", name + ".md"), "stock");
            Assert.Null(frozen.Error);
            Assert.Null(today.Error);
            Assert.Equal(1, frozen.SchemaVersion);
            Assert.Equal(3, today.SchemaVersion);
            Assert.Equal("template", today.Kind);
            Assert.Equal(JsonSerializer.Serialize(frozen.Slots), JsonSerializer.Serialize(today.Slots));
            foreach (var before in frozen.Steps.Where(s => s.Gate != null && (s.Gate.Inputs.Length > 0 || s.Gate.Verify.Length > 0)))
            {
                var now = today.Steps.Single(s => s.Id == before.Id);
                Assert.Equal(WorkflowPerformers.Assistant, now.Performer);
                Assert.Equal(JsonSerializer.Serialize(before.Gate.Inputs), JsonSerializer.Serialize(now.Gate.Inputs));
                Assert.Equal(JsonSerializer.Serialize(before.Gate.Verify), JsonSerializer.Serialize(now.Gate.Verify));
                Assert.Equal(before.Gate.Strictness, now.Gate.Strictness);
            }
        }

        [Fact]
        public async Task A_slot_answer_cannot_inject_yaml_into_a_format_3_instance()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true, workspace: NewWorkspace());
            using (sm)
            {
                var values = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["freeze_window"] = "x\"\nstrictness: off\ntriggers: []\n#",
                    ["change_policy"] = "anything\"\n  - kind: bpa_clean",
                    ["override_authority"] = "nobody",
                });
                var made = await engine.InstantiateWorkflowTemplateAsync("deploy-freeze-guard", "freeze-injected", values, "human");
                var def = await engine.GetWorkflowAsync("freeze-injected");
                var template = WorkflowParser.Parse((await engine.GetWorkflowTemplateAsync("deploy-freeze-guard")).Markdown);
                Assert.Null(def.Error);
                Assert.Equal("hard", def.Strictness);
                Assert.Equal(template.Triggers, def.Triggers);
                Assert.Equal(template.Steps.Select(s => JsonSerializer.Serialize(s.Gate)), def.Steps.Select(s => JsonSerializer.Serialize(s.Gate)));
            }
        }

        [Fact]
        public async Task A_persons_format_1_template_still_renders_as_it_always_did()
        {
            var (engine, sm) = await ComposerFrame.OpenAsync(pro: true, workspace: NewWorkspace());
            using (sm)
            {
                const string V1 = "---\nkind: template\nname: tidy-folder\ntitle: Tidy {{folder}}\nversion: 1\nstrictness: warn\nslots:\n"
                    + "  - name: folder\n    question: \"Which folder?\"\n    type: text\n    required: required\n    example: \"Sales\"\n---\n\n"
                    + "## Step 1: Tidy {{folder}}\n\nTidy the folder {{folder}}.\n\n```yaml gate\ninputs:\n  - name: done\n    question: \"What did you tidy?\"\n    type: text\n    required: required\n```\n";
                await engine.SaveWorkflowTemplateAsync("tidy-folder", V1, "human");
                await engine.InstantiateWorkflowTemplateAsync("tidy-folder", "tidy-sales", "{\"folder\":\"Sales\"}", "human");
                var def = await engine.GetWorkflowAsync("tidy-sales");
                Assert.Null(def.Error);
                Assert.Equal("Tidy Sales", def.Title);
                Assert.Equal("tidy-folder", def.Provenance["template"]);
                Assert.Contains("\"folder\":\"Sales\"", def.Provenance["slot_values"]);
            }
        }
    }
}
