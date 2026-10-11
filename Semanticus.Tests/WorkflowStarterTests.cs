using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Contract 21.2 (lane W1): the library becomes starters. "Use" is one op on both doors
    /// (instantiate_workflow_template, extended to take a stock workflow name). A starter a team rule requires is
    /// returned as it is and never copied, and no door may CREATE a project file that shadows a required
    /// workflow, because a project copy wins resolution outright and would quietly replace the required one.</summary>
    public sealed class WorkflowStarterTests : IDisposable
    {
        private sealed class Pro : IEntitlement
        {
            public bool IsPro => true;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "pro" };
        }

        private const string ShadowSentence = "A team rule requires the shipped version of this workflow, so a copy would quietly replace it. Use it as it is, or ask whoever set the rule.";

        private readonly string _dir;
        private readonly LocalEngine _engine;

        public WorkflowStarterTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sem-workflow-starters", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            File.Copy(TestModels.FindBim(), Path.Combine(_dir, "Model.bim"));
            _engine = new LocalEngine(new SessionManager(), new Pro());
            _engine.OpenAsync(Path.Combine(_dir, "Model.bim")).GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            _engine.Dispose();
            try { Directory.Delete(_dir, true); } catch { }
        }

        private string ProjectFile(string name) => Path.Combine(_dir, ".semanticus", "workflows", name + ".md");
        private static string StockFile(string name) => Path.Combine(AppContext.BaseDirectory, "workflows", name + ".md");
        private string SettingsFile => Path.Combine(_dir, ".semanticus", "workflow-settings.json");

        [Fact]
        public async Task Use_copies_a_starter_and_the_copy_opens()
        {
            var used = Assert.Single(await _engine.InstantiateWorkflowTemplateAsync("optimize-dax", null, null, "human"));
            Assert.Equal("optimize-dax", used.Name);
            Assert.Equal("user", used.Source);
            Assert.False(used.Starter);
            // The copy is the starter in today's format (F11), never a byte copy the page then calls "older".
            AssertSameMeaningInCurrentFormat(File.ReadAllText(StockFile("optimize-dax")), File.ReadAllText(ProjectFile("optimize-dax")));

            var opened = await _engine.GetWorkflowDocumentAsync("optimize-dax");
            Assert.Equal("user", opened.Library);
            Assert.True(opened.Metadata.Parses, opened.Metadata.ParseError);

            // A new name renames the copy's identity, and the stock starter stays untouched beside it.
            var renamed = Assert.Single(await McpTools.InstantiateWorkflowTemplate(_engine, "secure-with-rls", "our-rls"));
            Assert.Equal("our-rls", renamed.Name);
            Assert.Null(renamed.Error);
            Assert.True((await _engine.GetWorkflowDocumentAsync("our-rls")).Metadata.Parses);
            var library = await _engine.ListWorkflowsAsync();
            Assert.True(library.Single(w => w.Name == "secure-with-rls").Starter);
            Assert.Equal("stock", library.Single(w => w.Name == "secure-with-rls").Source);

            // Use is create-only: a second Use never replaces your copy.
            var again = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new EngineRpcTarget(_engine).instantiateWorkflowTemplate("optimize-dax", null));
            Assert.Contains("already exists", again.Message);
        }

        /// <summary>F11 (pre-handover journey): using "Test a complex measure" opened its fresh copy under "This
        /// workflow uses an older format. Upgrade it". The shipped starters are format 1 and Use copied their bytes, so
        /// every copy was born out of date. Use now writes the copy in the current format, through the same reviewed
        /// upgrade a person would apply, so the copy means exactly what the starter means and nothing asks to upgrade.</summary>
        [Fact]
        public async Task Every_starter_copy_is_written_in_the_current_format_with_the_same_meaning()
        {
            var stock = (await _engine.ListWorkflowsAsync()).Where(w => w.Starter && w.Source == "stock" && w.Error == null).Select(w => w.Name).ToArray();
            Assert.NotEmpty(stock);
            foreach (var name in stock)
            {
                var copy = "copy-of-" + name;
                Assert.Single(await _engine.InstantiateWorkflowTemplateAsync(name, copy, null, "human"));
                var doc = await _engine.GetWorkflowDocumentAsync(copy);
                Assert.True(doc.Metadata.Parses, name + ": " + doc.Metadata.ParseError);
                Assert.Equal(WorkflowParser.MaxSchemaVersion, doc.Metadata.SchemaVersion);
                AssertSameMeaningInCurrentFormat(File.ReadAllText(StockFile(name)), doc.ExactText, copy);
            }
            // A copy in the current format drafts, as its starter does (lane l9-finish).
            var draft = await _engine.ComposeTaskAsync(null, "copy-of-verified-measure", null, null, null, "human");
            Assert.NotEmpty(draft.Steps);
        }

        /// <summary>F12 (pre-handover journey): step 2 of "Test a complex measure" asked the person for "Machine field: the
        /// comma-separated qualified display columns", "one fenced JSON array of {context, optional axis, expect}" and
        /// "canonical shape ids (grand_total | cross | axis:&lt;column&gt;)". Those answers are read by Semanticus as data,
        /// so the run says which of the current step's questions the assistant fills, and the Studio does not ask the
        /// person for them. The questions a person can answer in words stay theirs.</summary>
        [Fact]
        public async Task A_run_names_the_questions_its_assistant_fills()
        {
            // Lane l9-finish: verified-measure drafts, and its gated steps run in the draft's guided run, which says the same.
            var draft = await _engine.ComposeTaskAsync(null, "verified-measure", null, null, null, "human");
            draft = await _engine.RunDraftAsync(draft.DraftId, "human");
            var run = await _engine.GetWorkflowRunAsync(draft.Steps.First(s => s.RunId != null).RunId);
            Assert.Null(run.CurrentStep.AssistantFills);
            run = await _engine.SubmitWorkflowStepAsync(run.RunId, run.CurrentStep.StepId,
                JsonSerializer.Serialize(new { requirement = "Margin over sales, recomputed at every grain.", clarification = new { declined = true, reason = "Nothing was ambiguous." } }), "human");
            Assert.Equal("step-2", run.CurrentStep.StepId);
            Assert.Equal(new[] { "equivalenceGrid", "expectedValues", "openGrains" }, run.CurrentStep.AssistantFills.OrderBy(x => x, StringComparer.Ordinal));
            Assert.DoesNotContain("naiveForm", run.CurrentStep.AssistantFills);
            Assert.All(run.CurrentStep.AssistantFills, n => Assert.Contains(run.CurrentStep.Questions, q => q.Name == n));
        }

        private static void AssertSameMeaningInCurrentFormat(string stockText, string copyText, string copyName = null)
        {
            var before = WorkflowParser.Parse(stockText);
            var after = WorkflowParser.Parse(copyText);
            Assert.Null(after.Error);
            Assert.Equal(WorkflowParser.MaxSchemaVersion, after.SchemaVersion);
            Assert.Equal(copyName ?? before.Name, after.Name);
            Assert.Equal(before.Title, after.Title);
            Assert.Equal(before.Version, after.Version);
            Assert.Equal(before.Strictness, after.Strictness);
            Assert.Equal(before.Steps.Select(s => s.Id), after.Steps.Select(s => s.Id));
            Assert.Equal(before.Steps.Select(s => s.Title), after.Steps.Select(s => s.Title));
            Assert.Equal(before.Steps.Select(s => s.Instructions), after.Steps.Select(s => s.Instructions));
            Assert.Equal(before.Steps.Select(s => string.Join("|", (s.Gate?.Inputs ?? Array.Empty<GateInput>()).Select(i => i.Name + ":" + i.Question))),
                after.Steps.Select(s => string.Join("|", (s.Gate?.Inputs ?? Array.Empty<GateInput>()).Select(i => i.Name + ":" + i.Question))));
            Assert.Equal(before.Steps.Select(s => string.Join("|", (s.Gate?.Verify ?? Array.Empty<VerifySpec>()).Select(v => v.Kind))),
                after.Steps.Select(s => string.Join("|", (s.Gate?.Verify ?? Array.Empty<VerifySpec>()).Select(v => v.Kind))));
        }

        [Fact]
        public async Task Use_on_a_required_starter_returns_the_required_workflow_on_both_doors_and_writes_no_file()
        {
            await _engine.ActivateWorkflowProfileAsync("consulting-delivery", "human");

            var viaMcp = Assert.Single(await McpTools.InstantiateWorkflowTemplate(_engine, "verified-measure", null));
            var viaRpc = Assert.Single(await new EngineRpcTarget(_engine).instantiateWorkflowTemplate("verified-measure", null));
            var named = Assert.Single(await _engine.InstantiateWorkflowTemplateAsync("verified-measure", "my-verified-measure", null, "agent"));
            foreach (var w in new[] { viaMcp, viaRpc, named })
            {
                Assert.Equal("verified-measure", w.Name);
                Assert.Equal("stock", w.Source);
                Assert.True(w.Required);
                Assert.False(w.ReplacesRequired);
            }
            Assert.False(File.Exists(ProjectFile("verified-measure")));
            Assert.False(File.Exists(ProjectFile("my-verified-measure")));
        }

        [Fact]
        public async Task Creating_a_project_file_that_shadows_a_required_workflow_is_refused_on_both_doors()
        {
            await _engine.ActivateWorkflowProfileAsync("consulting-delivery", "human");
            // add-relationship parked (lane W3); Consulting delivery still requires it, from its compat freeze.
            var stock = File.ReadAllText(WorkflowSeedLibraryTests.ParkedFreeze("add-relationship"));

            var mcp = await Assert.ThrowsAsync<InvalidOperationException>(() => McpTools.SaveWorkflow(_engine, "add-relationship", stock));
            Assert.Contains(ShadowSentence, mcp.Message);
            var rpc = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new EngineRpcTarget(_engine).saveWorkflow("add-relationship", stock, createOnly: true));
            Assert.Contains(ShadowSentence, rpc.Message);

            // A template filled under a required name is the same create, through Use.
            var values = JsonSerializer.Serialize(new { measure_goal = "year-on-year revenue growth percentage" });
            var viaTemplate = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _engine.InstantiateWorkflowTemplateAsync("hard-measure", "verified-measure", values, "agent"));
            Assert.Contains(ShadowSentence, viaTemplate.Message);

            Assert.False(File.Exists(ProjectFile("add-relationship")));
            Assert.False(File.Exists(ProjectFile("verified-measure")));
            Assert.Equal("stock", (await _engine.GetWorkflowAsync("add-relationship")).Source);
        }

        [Fact]
        public async Task An_existing_shadow_still_resolves_and_is_flagged()
        {
            var customised = File.ReadAllText(StockFile("verified-measure"));
            await _engine.SaveWorkflowAsync("verified-measure", customised, "human");   // before any team rule
            await _engine.ActivateWorkflowProfileAsync("consulting-delivery", "human");

            Assert.Equal("user", (await _engine.GetWorkflowAsync("verified-measure")).Source);
            var row = (await McpTools.ListWorkflows(_engine)).Single(w => w.Name == "verified-measure");
            Assert.Equal("user", row.Source);
            Assert.True(row.Required);
            Assert.True(row.ReplacesRequired);
            Assert.False(row.Starter);
            var rpcRow = (await new EngineRpcTarget(_engine).listWorkflows()).Single(w => w.Name == "verified-measure");
            Assert.True(rpcRow.ReplacesRequired);

            // Not rewritten, and still editable: the refusal is on CREATING a shadow only.
            await _engine.SaveWorkflowAsync("verified-measure", customised + "\n", "human");
            Assert.True(File.Exists(ProjectFile("verified-measure")));

            var other = (await _engine.ListWorkflowsAsync()).Single(w => w.Name == "optimize-dax");
            Assert.True(other.Starter);
            Assert.False(other.Required);
            Assert.False(other.ReplacesRequired);
        }

        [Fact]
        public async Task A_templates_setup_questions_are_asked_once()
        {
            var tmpl = await _engine.GetWorkflowTemplateAsync("hard-measure");
            var values = JsonSerializer.Serialize(new { measure_goal = "year-on-year revenue growth percentage" });

            // Use without a name takes the starter's own name, like a stock starter.
            var used = Assert.Single(await _engine.InstantiateWorkflowTemplateAsync("hard-measure", null, values, "human"));
            Assert.Equal("hard-measure", used.Name);
            Assert.Equal("user", used.Source);

            var instance = await _engine.GetWorkflowAsync("hard-measure");
            Assert.Empty(instance.Slots);
            var text = File.ReadAllText(ProjectFile("hard-measure"));
            Assert.DoesNotContain("{{", text);
            Assert.Contains("year-on-year revenue growth percentage", text);
            Assert.Contains("slot_values:", text);
            var runQuestions = instance.Steps.SelectMany(s => s.Gate?.Inputs ?? Array.Empty<GateInput>()).Select(i => i.Question).ToHashSet();
            Assert.All(tmpl.Slots, s => Assert.DoesNotContain(s.Question, runQuestions));
        }

        [Fact]
        public async Task A_stock_workflows_pin_still_resolves_after_Use()
        {
            await _engine.ActivateWorkflowProfileAsync("consulting-delivery", "human");
            var before = JsonNode.Parse(File.ReadAllText(SettingsFile))["requiredWorkflowRevisions"].ToJsonString();

            await _engine.InstantiateWorkflowTemplateAsync("verified-measure", null, null, "human");
            await _engine.InstantiateWorkflowTemplateAsync("optimize-dax", null, null, "human");
            await _engine.InstantiateWorkflowTemplateAsync("check-blast-radius", "our-blast-radius", null, "human");

            var pins = JsonNode.Parse(File.ReadAllText(SettingsFile))["requiredWorkflowRevisions"];
            Assert.Equal(before, pins.ToJsonString());
            foreach (var name in new[] { "verified-measure", "add-relationship" })
            {
                var doc = await _engine.GetWorkflowDocumentAsync(name);
                Assert.Equal("stock", doc.Library);
                Assert.Equal(pins[name].GetValue<string>(), doc.ByteHash);
            }
        }

        [Fact]
        public async Task Use_of_an_unknown_name_names_both_lists()
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => _engine.InstantiateWorkflowTemplateAsync("no-such-starter", null, null, "human"));
            Assert.Contains("list_workflows", ex.Message);
            Assert.Contains("list_workflow_templates", ex.Message);
        }

        [Fact]
        public async Task The_empty_profile_is_called_no_team_rules()
        {
            var profiles = await _engine.ListWorkflowProfilesAsync();
            var none = profiles.Single(p => p.Name == "standard");   // the id stays, so saved settings still resolve
            Assert.Equal("No team rules", none.Title);
            Assert.DoesNotContain(profiles, p => p.Title == "Solo analyst");
            Assert.All(profiles, p => Assert.DoesNotContain("playbook", p.Description + string.Join(" ", p.Effects), StringComparison.OrdinalIgnoreCase));
        }
    }
}
