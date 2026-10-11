using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Composer;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Join part 6, area B, the last eighteen rows: each held write that reads state outside the model and its arguments gets a witness. One test
    /// each, through the real assistant door under Locked: the person says yes to a call, the thing the write reads changes (the person's edit, a file
    /// replaced, a draft edited), and the same call is retried. It must ask again and write nothing. A third row type is pinned too: a row that reads
    /// only its own arguments says so.
    /// </summary>
    [Collection("restore-root")]
    public sealed class AssistantGateWitnessRowsTests : GateFixtureBase
    {
        public AssistantGateWitnessRowsTests(RestoreRootFixture fixture) : base(fixture) { }

        public override void Dispose()
        {
            DaxLibRest.ClientFactoryForTests = null;
            base.Dispose();
        }

        private const string Locked = AssistantGateKit.Locked;

        private static async Task<ApprovalRecord[]> UnansweredAsync(LocalEngine engine) =>
            (await engine.ListPendingApprovalsAsync()).Where(p => p.GrantedUtc == null).ToArray();

        private static async Task ApproveAskedAsync(LocalEngine engine) =>
            await engine.ApproveAgentActionAsync(Assert.Single(await UnansweredAsync(engine)).Id, "human");

        /// <summary>The call is asked about and refused; the person says yes; <paramref name="change"/> moves what the call reads; the retry is refused
        /// again with a NEW question (the old yes is not spent on it). The caller then checks nothing was written.</summary>
        private async Task StaleAfterYesAsync(LocalEngine engine, Func<Task> call, Func<Task> change)
        {
            await RefusedAsync(call);
            await ApproveAskedAsync(engine);
            await change();
            await RefusedAsync(call);
            Assert.Single(await UnansweredAsync(engine));
        }

        // ---- run_draft: the draft's steps and blanks ---------------------------------------------------------------------------------

        private const string ReadThenAdd = "---\nschemaVersion: 3\nname: read-then-add\ntitle: Read then add\n---\n"
            + "## Step 1: Read the columns\n```yaml step\nid: read\nperformer: semanticus\naction:\n  op: list_columns\n```\n"
            + "## Step 2: Add the measure Units\n```yaml step\nid: units\nperformer: semanticus\naction:\n  op: create_measure\n"
            + "  tableRef: table:Sales\n  name: Units\n  expression: SUM ( Sales[Amount] )\n```\n";

        [Fact]
        public async Task Run_draft_is_not_spent_on_a_draft_edited_after_the_yes()
        {
            var (engine, sessions, _) = await OpenAsync("w-draft");
            using (engine)
            {
                await engine.SaveWorkflowAsync("read-then-add", ReadThenAdd, "human");
                var draft = await engine.ComposeTaskAsync(null, "read-then-add", null, null, null, "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.RunDraftAsync(draft.DraftId, "agent"),
                    () => engine.EditDraftAsync(draft.DraftId, "drop", "units", null, null, null, "human"));
                Assert.False(await sessions.Current.ReadAsync(m => m.Tables["Sales"].Measures.Contains("Units")), "the edited draft ran on the old yes");
            }
        }

        // ---- load_bpa_rules and load_readiness_rules: the file they read -------------------------------------------------------------

        private const string BpaRule = "[{\"ID\":\"W_RULE\",\"Name\":\"first\",\"Category\":\"Formatting\",\"Severity\":2,\"Scope\":\"Measure\",\"Expression\":\"string.IsNullOrWhitespace(Description)\"}]";
        private const string ReadinessRule = "{\"ID\":\"W-RULE\",\"Name\":\"first\",\"Category\":\"Descriptions\",\"Severity\":\"Medium\",\"Scope\":\"Measure\","
            + "\"AppliesTo\":\"DisplayFolder = \\\"KPI\\\"\",\"Expression\":\"string.IsNullOrEmpty(Description)\",\"Message\":\"%object% needs a description\",\"FixKind\":\"AiContent\"}";

        [Fact]
        public async Task Load_bpa_rules_is_not_spent_on_a_rules_file_changed_after_the_yes()
        {
            var (engine, sessions, _) = await OpenAsync("w-bpa");
            using (engine)
            {
                var file = Path.Combine(Root, "rules.json");
                File.WriteAllText(file, BpaRule);
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.LoadBpaRulesAsync(file, true, "agent"),
                    () => { File.WriteAllText(file, BpaRule.Replace("W_RULE", "OTHER_RULE")); return Task.CompletedTask; });
                Assert.Null(await sessions.Current.ReadAsync(m => m.GetAnnotation("BestPracticeAnalyzer")));
            }
        }

        [Fact]
        public async Task Load_readiness_rules_is_not_spent_on_a_rules_file_changed_after_the_yes()
        {
            var (engine, sessions, _) = await OpenAsync("w-air");
            using (engine)
            {
                var file = Path.Combine(Root, "ready.json");
                File.WriteAllText(file, ReadinessRule);
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.LoadReadinessRulesAsync(file, true, "agent"),
                    () => { File.WriteAllText(file, ReadinessRule.Replace("W-RULE", "OTHER-RULE")); return Task.CompletedTask; });
                Assert.Equal(0, (await engine.AiReadinessScanAsync()).Findings.Count(f => f.RuleId == "W-RULE" || f.RuleId == "OTHER-RULE"));
            }
        }

        // ---- dax_lib_install: the package as the registry serves it ----------------------------------------------------------------

        private const string Tmdl = "function 'Sample.Add' =\n        (x: INT64, y: INT64) =>\n            x + y\n\n    annotation DAXLIB_PackageId = Sample\n    annotation DAXLIB_PackageVersion = 1.0.0\n";

        private static byte[] Package(string functions)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Add(string name, string content) { var e = zip.CreateEntry(name); using var w = new StreamWriter(e.Open()); w.Write(content); }
                Add("[Content_Types].xml", "<Types/>");
                Add("manifest.daxlib", "{\"id\":\"Sample\",\"version\":\"1.0.0\",\"authors\":[\"Daxlib\"]}");
                Add("lib/functions.tmdl", functions);
            }
            return ms.ToArray();
        }

        private sealed class Served : HttpMessageHandler
        {
            public byte[] Pkg;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                var u = req.RequestUri!.AbsoluteUri;
                (int, string, byte[]) r =
                    u.Contains("/Sample/versions.json") ? (200, "application/json", System.Text.Encoding.UTF8.GetBytes("{\"versions\":[\"1.0.0\"]}"))
                    : u.Contains("/Sample/1.0.0/metadata.json") ? (200, "application/json", System.Text.Encoding.UTF8.GetBytes("{\"id\":\"Sample\",\"version\":\"1.0.0\"}"))
                    : u.Contains(".daxpkg") ? (200, "application/octet-stream", Pkg)
                    : (404, "application/json", System.Text.Encoding.UTF8.GetBytes("{}"));
                var resp = new HttpResponseMessage((HttpStatusCode)r.Item1) { Content = new ByteArrayContent(r.Item3) };
                resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(r.Item2);
                return Task.FromResult(resp);
            }
        }

        [Fact]
        public async Task Dax_lib_install_is_not_spent_on_a_package_that_changed_after_the_yes()
        {
            var (engine, sessions, _) = await OpenAsync("w-daxlib");
            using (engine)
            {
                var handler = new Served { Pkg = Package(Tmdl) };
                DaxLibRest.ClientFactoryForTests = baseUrl => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(baseUrl) };
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.DaxLibInstallAsync("Sample", null, false, "agent"),
                    () => { handler.Pkg = Package(Tmdl.Replace("x + y", "x - y")); return Task.CompletedTask; });
                Assert.False(await sessions.Current.ReadAsync(m => m.Functions.Any()), "the changed package was installed on the old yes");
            }
        }

        // ---- git_push: the commits it would send ---------------------------------------------------------------------------------------

        private static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false" }.Concat(args)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
            return o.Trim();
        }

        [Fact]
        public async Task Git_push_is_not_spent_on_commits_made_after_the_yes()
        {
            var (engine, _, path) = await OpenAsync("w-push");
            using (engine)
            {
                var dir = Path.GetDirectoryName(path)!;
                var remote = Path.Combine(Root, "remote.git");
                Directory.CreateDirectory(remote);
                Git(remote, "init", "--bare", "-b", "main");
                Git(dir, "init", "-b", "main");
                File.WriteAllText(Path.Combine(dir, ".gitignore"), ".semanticus/\n");
                Git(dir, "add", "-A"); Git(dir, "commit", "-m", "first");
                Git(dir, "remote", "add", "origin", remote);
                Git(dir, "push", "-u", "origin", "main");
                File.WriteAllText(Path.Combine(dir, "note.txt"), "one");
                Git(dir, "add", "-A"); Git(dir, "commit", "-m", "second");   // what the person approved sending
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.GitPushAsync("origin", "main", true, "agent"),
                    () => { File.WriteAllText(Path.Combine(dir, "note.txt"), "two"); Git(dir, "add", "-A"); Git(dir, "commit", "-m", "third"); return Task.CompletedTask; });
                Assert.Equal(Git(dir, "rev-parse", "main~2"), Git(remote, "rev-parse", "main"));   // the remote still has the first commit only
                Assert.NotEqual(Git(dir, "rev-parse", "main"), Git(remote, "rev-parse", "main"));
            }
        }

        // ---- save_workflow and save_workflow_template: the file they overwrite -----------------------------------------------------

        private static string Workflow(string title) => "---\nschemaVersion: 3\nname: wf-a\ntitle: " + title + "\n---\n"
            + "## Step 1: Read the columns\n```yaml step\nid: read\nperformer: semanticus\naction:\n  op: list_columns\n```\n";

        [Fact]
        public async Task Save_workflow_does_not_overwrite_the_persons_newer_workflow()
        {
            var (engine, _, _) = await OpenAsync("w-wf");
            using (engine)
            {
                await engine.SaveWorkflowAsync("wf-a", Workflow("The person wrote this"), "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.SaveWorkflowAsync("wf-a", Workflow("The assistant wrote this"), "agent"),
                    () => engine.SaveWorkflowAsync("wf-a", Workflow("The person rewrote it"), "human"));
                Assert.Equal("The person rewrote it", (await engine.GetWorkflowAsync("wf-a")).Title);
            }
        }

        private static string Template(string title) => "---\nkind: template\nname: tpl-a\ntitle: " + title + " {{surface}}\nversion: 1\nstrictness: warn\nslots:\n"
            + "  - name: surface\n    question: \"What surface?\"\n    type: text\n    required: required\n    example: \"the dashboard\"\n---\n\n## Step 1: Do it\nCertify {{surface}}.\n";

        [Fact]
        public async Task Save_workflow_template_does_not_overwrite_the_persons_newer_template()
        {
            var (engine, _, _) = await OpenAsync("w-tpl");
            using (engine)
            {
                await engine.SaveWorkflowTemplateAsync("tpl-a", Template("Person's"), "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.SaveWorkflowTemplateAsync("tpl-a", Template("Assistant's"), "agent"),
                    () => engine.SaveWorkflowTemplateAsync("tpl-a", Template("Person's rewrite"), "human"));
                var kept = (await engine.GetWorkflowTemplateAsync("tpl-a"));
                Assert.Contains("Person's rewrite", kept.Markdown);
            }
        }

        [Fact]
        public async Task Instantiate_workflow_template_is_not_spent_on_a_template_changed_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("w-inst");
            using (engine)
            {
                await engine.SaveWorkflowTemplateAsync("tpl-a", Template("First"), "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.InstantiateWorkflowTemplateAsync("tpl-a", "inst-a", "{\"surface\":\"Sales\"}", "agent"),
                    () => engine.SaveWorkflowTemplateAsync("tpl-a", Template("Changed"), "human"));
                Assert.DoesNotContain("inst-a", (await engine.ListWorkflowsAsync()).Select(w => w.Name));
            }
        }

        // ---- save_test_definition and run_test_suite: the stored tests -------------------------------------------------------------

        private static TestDefinition Test(string id, string title) => new TestDefinition
        {
            Id = id, Kind = TestKinds.MeasureReconcile, Title = title, TargetRef = "measure:Sales/Total",
            ParamsJson = TestSuiteStore.Serialize(new ReconcileRequest { MeasureRef = "measure:Sales/Total", Sql = "SELECT 1", BlankPolicy = "zero" }),
        };

        [Fact]
        public async Task Save_test_definition_does_not_overwrite_the_persons_newer_test()
        {
            var (engine, _, _) = await OpenAsync("w-testdef");
            using (engine)
            {
                var first = await engine.SaveTestDefinitionAsync(Test(null, "Person's test"), "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.SaveTestDefinitionAsync(Test(first.Id, "Assistant's test"), "agent"),
                    () => engine.SaveTestDefinitionAsync(Test(first.Id, "Person's rewrite"), "human"));
                Assert.Contains((await engine.ListTestDefinitionsAsync()).Definitions, t => t.Id == first.Id && t.Title == "Person's rewrite");
            }
        }

        [Fact]
        public async Task Run_test_suite_that_keeps_the_record_is_not_spent_on_a_suite_changed_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("w-suite");
            using (engine)
            {
                await engine.SaveTestDefinitionAsync(Test(null, "First test"), "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.RunTestSuiteAsync(true, "agent", null, null),
                    () => engine.SaveTestDefinitionAsync(Test(null, "A test added after the yes"), "human"));
                Assert.Empty((await engine.ListTestRunsAsync()).Runs);   // nothing was recorded
                await ApproveAskedAsync(engine);   // positive control: asked about the suite as it stands now, the run goes through
                await gate.RunTestSuiteAsync(true, "agent", null, null);
            }
        }

        // ---- save_evidence: the artifact it saves ------------------------------------------------------------------------------------

        [Fact]
        public async Task Save_evidence_is_not_spent_on_a_test_report_that_changed_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("w-evid");
            using (engine)
            {
                await engine.SaveTestDefinitionAsync(Test(null, "First test"), "human");
                await engine.RunTestSuiteAsync(false, "human", null, null);
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.SaveEvidenceAsync("tests", null, "agent"), async () =>
                {
                    await engine.SaveTestDefinitionAsync(Test(null, "Another test"), "human");
                    await engine.RunTestSuiteAsync(false, "human", null, null);   // a newer run: the report the person approved is gone
                });
                Assert.Empty((await engine.ListEvidenceAsync()).Items);
            }
        }

        // ---- run_interview: the stored question ------------------------------------------------------------------------------------

        [Fact]
        public async Task Run_interview_is_not_spent_on_a_question_changed_after_the_yes()
        {
            var (engine, _, _) = await OpenAsync("w-iv");
            using (engine)
            {
                var q = await engine.AddInterviewQuestionAsync("What is the total?", "value", "EVALUATE ROW(\"v\", 1)", null, null, null, null, "1", null, false, null, "user", "project", "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.RunInterviewAsync(q.Id, null, false, null, "agent"),
                    () => engine.AddInterviewQuestionAsync("What is the total?", "value", "EVALUATE ROW(\"v\", 2)", null, null, null, null, "2", null, false, null, "user", "project", "human", q.Id));
                var now = (await engine.ListInterviewQuestionsAsync("project")).Questions.Single(x => x.Id == q.Id);
                Assert.Null(now.LastRun);   // it never ran
            }
        }

        // ---- the primer: what it replaces and the suggestion it takes ------------------------------------------------------------

        [Fact]
        public async Task Set_primer_does_not_replace_the_persons_newer_primer()
        {
            var (engine, _, _) = await OpenAsync("w-primer");
            using (engine)
            {
                var template = PrimerContract.Template("Gate");
                await engine.SetPrimerAsync(template + "\nThe person's first words.\n", "human");
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.SetPrimerAsync(template + "\nThe assistant's words.\n", "agent"),
                    () => engine.SetPrimerAsync(template + "\nThe person's newer words.\n", "human"));
                Assert.Contains("The person's newer words.", (await engine.GetPrimerAsync()).Markdown);
                await ApproveAskedAsync(engine);   // positive control: the call as it stands now, once the person says yes to it, runs
                await gate.SetPrimerAsync(template + "\nThe assistant's words.\n", "agent");
                Assert.Contains("The assistant's words.", (await engine.GetPrimerAsync()).Markdown);
            }
        }

        private static async Task<string> SuggestionAsync(LocalEngine engine)
        {
            var note = await engine.AddInsightAsync("Revenue is net of returns.", new[] { "primer:business-rules" }, "insight", "project", true, "human");
            await engine.ApproveInsightAsync(note.Id, "human");
            Assert.Contains((await engine.ListPrimerSuggestionsAsync()).Suggestions, s => s.Id == note.Id);
            return note.Id;
        }

        [Fact]
        public async Task Reject_primer_suggestion_is_not_spent_on_a_suggestion_the_person_rewrote()
        {
            var (engine, _, _) = await OpenAsync("w-reject");
            using (engine)
            {
                var id = await SuggestionAsync(engine);
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.RejectPrimerSuggestionAsync(id, "agent"),
                    () => engine.EditInsightAsync(id, "Revenue is gross of returns.", null, "human"));
                Assert.Contains((await engine.ListPrimerSuggestionsAsync()).Suggestions, s => s.Id == id);   // still waiting
                await ApproveAskedAsync(engine);   // positive control
                await gate.RejectPrimerSuggestionAsync(id, "agent");
                Assert.DoesNotContain((await engine.ListPrimerSuggestionsAsync()).Suggestions, s => s.Id == id);
            }
        }

        [Fact]
        public async Task Accept_primer_suggestion_is_not_spent_on_a_suggestion_the_person_rewrote()
        {
            var (engine, _, _) = await OpenAsync("w-accept");
            using (engine)
            {
                var id = await SuggestionAsync(engine);
                AgentPolicyStore.SetPreset(Locked, "human");
                var gate = Gate(engine);
                await StaleAfterYesAsync(engine, () => gate.AcceptPrimerSuggestionAsync(id, "agent"),
                    () => engine.EditInsightAsync(id, "Revenue is gross of returns.", null, "human"));
                Assert.DoesNotContain("Revenue is", (await engine.GetPrimerAsync()).Markdown);
                await ApproveAskedAsync(engine);   // positive control: asked about the suggestion as it stands now, the accept runs
                await gate.AcceptPrimerSuggestionAsync(id, "agent");
                Assert.Contains("Revenue is gross of returns.", (await engine.GetPrimerAsync()).Markdown);
            }
        }

        // ---- the rows that read only their own arguments say so, and the test prints why -------------------------------------------

        [Fact]
        public void Record_test_run_upgrade_workflow_and_capture_baseline_read_only_their_own_arguments()
        {
            // record_test_run names a finished run by its unique id, and a finished run is an immutable snapshot, so the id IS the content.
            // upgrade_workflow applies only with expectPath and expectByteHash from the reviewed preview: the file's hash is an argument the
            // approval binds, and the engine refuses when the file no longer has it. capture_baseline writes a NEW baseline (a fresh id) of the
            // values as they are when it runs and replaces nothing.
            foreach (var m in new[] { "RecordTestRunAsync", "UpgradeWorkflowAsync", "CaptureBaselineAsync" })
            {
                var row = GateRegistry.Find(m);
                Assert.True(row != null && (row.Kind == GateKind.Hold || row.Choose != null), m + " is not a held row");
            }
        }
    }
}
