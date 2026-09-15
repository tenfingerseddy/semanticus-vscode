using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using Xunit.Abstractions;

namespace Semanticus.Tests
{
    public sealed class WorkflowUpgradeTests
    {
        private readonly ITestOutputHelper _output;
        public WorkflowUpgradeTests(ITestOutputHelper output) => _output = output;
        private static readonly string Lf = ((char)10).ToString();
        private static readonly string CrLf = ((char)13).ToString() + (char)10;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly string Simple = string.Join(Lf, "---", "name: upgrade-test", "title: Upgrade test", "---", "## Step 1: Work", "Keep this instruction.");

        private sealed class Free : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "free" };
        }

        private sealed class Fixture : IDisposable
        {
            public string Workspace { get; } = Path.Combine(Path.GetTempPath(), "smx-upgrade-" + Guid.NewGuid().ToString("N"));
            public SessionManager Sessions { get; } = new SessionManager();
            public LocalEngine Engine { get; }
            public string FilePath => Path.Combine(Workspace, ".semanticus", "workflows", "upgrade-test.md");
            public Fixture(string text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllBytes(FilePath, Utf8.GetBytes(text));
                Engine = new LocalEngine(Sessions, TestEntitlements.Pro, Workspace);
            }
            public void Dispose() { Sessions.Dispose(); Directory.Delete(Workspace, true); }
        }

        private static string ReconstructDiff(string diff, bool after)
        {
            var lines = diff.Split((char)10);
            var records = new List<(char Kind, string Text)>();
            foreach (var line in lines.Skip(3))
            {
                if (line.Length == 0) continue;
                if (line.StartsWith(((char)92).ToString() + " No newline", StringComparison.Ordinal))
                {
                    var previous = records[^1];
                    records[^1] = (previous.Kind, previous.Text.Substring(0, previous.Text.Length - 1));
                }
                else records.Add((line[0], line.Substring(1) + Lf));
            }
            return string.Concat(records.Where(record => after ? record.Kind != '-' : record.Kind != '+').Select(record => record.Text));
        }

        private static string[] WireJson(WorkflowUpgradeResult result)
        {
            var serializer = new Newtonsoft.Json.JsonSerializer();
            RpcServer.ConfigureSerializer(serializer);
            using var writer = new StringWriter();
            serializer.Serialize(writer, result);
            return new[] { writer.ToString(), System.Text.Json.JsonSerializer.Serialize(result, ModelContextProtocol.McpJsonUtilities.DefaultOptions) };
        }

        private static void AssertNoNextAction(WorkflowUpgradeResult result)
        {
            foreach (var json in WireJson(result))
            {
                using var wire = System.Text.Json.JsonDocument.Parse(json);
                Assert.False(wire.RootElement.TryGetProperty("suggested_next_action", out _));
                Assert.False(wire.RootElement.TryGetProperty("suggestedNextAction", out _));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Preview_and_apply_preserve_golden_bom_mixed_endings_gate_and_unterminated_tail(bool bom)
        {
            var prefix = bom ? ((char)0xfeff).ToString() : "";
            var source = prefix + "---" + CrLf + "name: upgrade-test" + Lf + "title: Café" + CrLf + "---" + Lf
                + "# Keep this comment" + CrLf + "##Step  1 :First  " + CrLf + "Keep the spacing.  " + Lf
                + "```yaml gate" + CrLf + "inputs:" + Lf + "  - name: note" + CrLf + "    question: Note?" + Lf
                + "    required: optional" + CrLf + "```" + Lf + "## Step 2: Last" + Lf + "Tail without newline";
            var expected = prefix + "---" + CrLf + "schemaVersion: 2" + CrLf + "name: upgrade-test" + Lf + "title: Café" + CrLf + "---" + Lf
                + "# Keep this comment" + CrLf + "##Step  1 :First  " + CrLf + "```yaml step" + CrLf + "id: step-1" + CrLf + "```" + CrLf
                + "Keep the spacing.  " + Lf + "```yaml gate" + CrLf + "inputs:" + Lf + "  - name: note" + CrLf + "    question: Note?" + Lf
                + "    required: optional" + CrLf + "```" + Lf + "## Step 2: Last" + Lf + "```yaml step" + Lf + "id: step-2" + Lf + "```" + Lf
                + "Tail without newline";
            using var f = new Fixture(source);
            var broadcasts = 0;
            var activities = new List<ActivityEvent>();
            f.Sessions.Bus.WorkflowLibraryChanged += _ => broadcasts++;
            f.Sessions.Bus.Activity += activities.Add;
            var preview = await f.Engine.UpgradeWorkflowAsync("upgrade-test");
            Assert.True(preview.DryRun);
            Assert.False(preview.Changed);
            Assert.True(preview.CanApply, preview.Reason);
            Assert.Null(preview.ParseError);
            Assert.Equal(7, preview.AddedLines);
            Assert.Equal(source, preview.Document.ExactText);
            Assert.Equal(expected, preview.ProposedText);
            Assert.Equal(source, ReconstructDiff(preview.Diff, after: false));
            Assert.Equal(expected, ReconstructDiff(preview.Diff, after: true));
            Assert.Equal(Utf8.GetBytes(source), File.ReadAllBytes(f.FilePath));
            Assert.Equal(0, broadcasts);
            Assert.Empty(activities);

            var applied = await f.Engine.UpgradeWorkflowAsync("upgrade-test", false, preview.Document.ByteHash, preview.Document.Path);
            Assert.True(applied.Changed, applied.Reason);
            Assert.False(applied.DryRun);
            Assert.False(applied.CanApply);
            AssertNoNextAction(applied);
            Assert.Equal(expected, applied.Document.ExactText);
            Assert.Equal(2, applied.Document.Metadata.SchemaVersion);
            Assert.True(applied.Document.Metadata.ExplicitIds);
            Assert.Equal(new[] { "step-1", "step-2" }, applied.Document.Metadata.StepIds);
            Assert.Equal(Utf8.GetBytes(expected), File.ReadAllBytes(f.FilePath));
            Assert.Equal(1, broadcasts);
            Assert.Single(activities, activity => activity.Kind == "upgrade_workflow" && activity.Ok);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(f.FilePath)!, "*.tmp"));
        }

        [Theory]
        [InlineData("1", true)]
        [InlineData("1", false)]
        [InlineData("'1'", true)]
        [InlineData("'1'", false)]
        [InlineData("\"01\"", true)]
        [InlineData("\"01\"", false)]
        [InlineData("+1", true)]
        [InlineData("+1", false)]
        public async Task Active_explicit_v1_is_promoted_and_only_its_declaration_moves_when_needed(string value, bool first)
        {
            var declaration = "schemaVersion:  " + value + "   # Keep version comment" + CrLf;
            var promoted = "schemaVersion:  " + value.Replace("1", "2") + "   # Keep version comment" + CrLf;
            var identity = "name: upgrade-test" + Lf + "title: Keep title" + Lf;
            var body = "---" + Lf + "## Step 1: Work" + Lf + "Keep text";
            var source = "---" + Lf + (first ? declaration + identity : identity + declaration) + body;
            var expected = "---" + Lf + promoted + identity + "---" + Lf + "## Step 1: Work" + Lf
                + "```yaml step" + Lf + "id: step-1" + Lf + "```" + Lf + "Keep text";
            using var f = new Fixture(source);
            var preview = await f.Engine.UpgradeWorkflowAsync("upgrade-test");
            Assert.True(preview.CanApply, preview.Reason);
            Assert.Equal(expected, preview.ProposedText);
            Assert.Equal(source, ReconstructDiff(preview.Diff, false));
            Assert.Equal(expected, ReconstructDiff(preview.Diff, true));
            Assert.Contains("-schemaVersion:  " + value, preview.Diff);
            Assert.Contains("+schemaVersion:  " + value.Replace("1", "2"), preview.Diff);
            Assert.Equal("Keep title", WorkflowParser.Parse(expected).Title);
            Assert.DoesNotContain("schemaVersion", WorkflowParser.Parse(expected).Provenance.Keys);
            Assert.Equal(Utf8.GetBytes(source), File.ReadAllBytes(f.FilePath));
        }

        [Fact]
        public async Task Heading_at_eof_keeps_an_unterminated_eof_after_adding_its_fence()
        {
            var source = string.Join(CrLf, "---", "name: upgrade-test", "---", "## Step 1: Empty");
            var expected = string.Join(CrLf, "---", "schemaVersion: 2", "name: upgrade-test", "---", "## Step 1: Empty", "```yaml step", "id: step-1", "```");
            using var f = new Fixture(source);
            var preview = await f.Engine.UpgradeWorkflowAsync("upgrade-test");
            Assert.True(preview.CanApply, preview.Reason);
            Assert.Equal(expected, preview.ProposedText);
            Assert.Equal(source, ReconstructDiff(preview.Diff, false));
            Assert.Equal(expected, ReconstructDiff(preview.Diff, true));
            Assert.False(preview.ProposedText.EndsWith(Lf, StringComparison.Ordinal));
        }

        [Fact]
        public async Task Version_already_first_after_comments_is_not_repositioned()
        {
            var source = string.Join(Lf, "---", "# Keep this comment", "", "schemaVersion: 1 # Keep this too",
                "name: upgrade-test", "---", "## Step 1: Work", "Text");
            using var f = new Fixture(source);
            var preview = await f.Engine.UpgradeWorkflowAsync("upgrade-test");
            Assert.True(preview.CanApply, preview.Reason);
            Assert.StartsWith(string.Join(Lf, "---", "# Keep this comment", "", "schemaVersion: 2 # Keep this too"), preview.ProposedText);
            Assert.Equal(source, ReconstructDiff(preview.Diff, false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Valid_v2_is_a_noop_even_with_implicit_step_ids(bool dryRun)
        {
            var source = "---" + Lf + "schemaVersion: 2" + Lf + Simple.Substring(4);
            using var f = new Fixture(source);
            var fixedTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(f.FilePath, fixedTime);
            var document = await f.Engine.GetWorkflowDocumentAsync("upgrade-test");
            var broadcasts = 0;
            f.Sessions.Bus.WorkflowLibraryChanged += _ => broadcasts++;
            var result = await f.Engine.UpgradeWorkflowAsync("upgrade-test", dryRun, document.ByteHash, document.Path);
            Assert.False(result.Changed);
            Assert.False(result.CanApply);
            AssertNoNextAction(result);
            Assert.Contains("already uses version 2", result.Reason);
            Assert.Equal(source, result.ProposedText);
            Assert.Equal("", result.Diff);
            Assert.Equal(0, result.AddedLines);
            Assert.Equal(Utf8.GetBytes(source), File.ReadAllBytes(f.FilePath));
            Assert.Equal(fixedTime, File.GetLastWriteTimeUtc(f.FilePath));
            Assert.Equal(0, broadcasts);
        }

        [Theory]
        [InlineData("opaque-frontmatter")]
        [InlineData("unknown-gate")]
        [InlineData("ignored-step")]
        [InlineData("semantic-drift")]
        [InlineData("duplicate-version")]
        [InlineData("inert-version")]
        [InlineData("indented-heading")]
        public async Task Upgrade_refuses_opaque_or_newly_active_semantics_without_writing(string scenario)
        {
            var source = scenario switch
            {
                "opaque-frontmatter" => Simple.Replace("title: Upgrade test", "title: Upgrade test" + Lf + "futureMetadata: keep me"),
                "unknown-gate" => Simple + Lf + string.Join(Lf, "```yaml gate", "futureGate: keep me", "```"),
                "ignored-step" => Simple + Lf + string.Join(Lf, "```yaml step", "id: formerly-inert", "```"),
                "semantic-drift" => Simple.Replace("title: Upgrade test", "title: Upgrade test" + Lf + "triggers: [\"create_measure,run_dax\"]"),
                "duplicate-version" => Simple.Replace("name: upgrade-test", "schemaVersion: 1" + Lf + "name: upgrade-test" + Lf + "schemaVersion: 1"),
                "inert-version" => Simple.Replace("name: upgrade-test", "name: upgrade-test" + Lf + " schemaVersion: 1"),
                _ => Simple + Lf + " ## Step 2: Previously prose" + Lf + "Keep me",
            };
            Assert.Null(WorkflowParser.Parse(source).Error);
            using var f = new Fixture(source);
            var preview = await f.Engine.UpgradeWorkflowAsync("upgrade-test");
            Assert.False(preview.CanApply);
            AssertNoNextAction(preview);
            Assert.False(preview.Changed);
            if (scenario == "semantic-drift")
            {
                Assert.Null(preview.ParseError);
                Assert.Contains("would change", preview.Reason);
                Assert.Contains("Triggers", preview.Reason);
            }
            var refused = await f.Engine.UpgradeWorkflowAsync("upgrade-test", false, preview.Document.ByteHash, preview.Document.Path);
            Assert.False(refused.Changed);
            Assert.False(refused.CanApply);
            AssertNoNextAction(refused);
            Assert.Equal(source, refused.Document.ExactText);
            Assert.Equal(Utf8.GetBytes(source), File.ReadAllBytes(f.FilePath));
        }

        [Theory]
        [InlineData(true, "hash")]
        [InlineData(false, "hash")]
        [InlineData(true, "path")]
        [InlineData(false, "path")]
        public async Task Supplied_preview_fences_and_required_apply_fences_refuse_stale_sources(bool dryRun, string fence)
        {
            using var f = new Fixture(Simple);
            var document = await f.Engine.GetWorkflowDocumentAsync("upgrade-test");
            var result = await f.Engine.UpgradeWorkflowAsync("upgrade-test", dryRun,
                fence == "hash" ? "sha256:stale" : document.ByteHash,
                fence == "path" ? document.Path + ".other-project" : document.Path);
            Assert.False(result.Changed);
            Assert.False(result.CanApply);
            AssertNoNextAction(result);
            Assert.Equal(Simple, result.Document.ExactText);
            Assert.Contains(fence == "hash" ? "changed on disk" : "different file", result.Reason);
            Assert.Equal(Utf8.GetBytes(Simple), File.ReadAllBytes(f.FilePath));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Apply_needs_both_reviewed_fences(bool pathMissing)
        {
            using var f = new Fixture(Simple);
            var document = await f.Engine.GetWorkflowDocumentAsync("upgrade-test");
            var result = await f.Engine.UpgradeWorkflowAsync("upgrade-test", false,
                pathMissing ? document.ByteHash : null, pathMissing ? null : document.Path);
            Assert.False(result.Changed);
            AssertNoNextAction(result);
            Assert.Contains(pathMissing ? "expectPath" : "expectByteHash", result.Reason);
            Assert.Equal(Utf8.GetBytes(Simple), File.ReadAllBytes(f.FilePath));
        }

        [Fact]
        public async Task External_edits_after_preview_return_the_current_document_without_writing()
        {
            using var f = new Fixture(Simple);
            var preview = await f.Engine.UpgradeWorkflowAsync("upgrade-test");
            var edited = Simple + Lf + "An outside edit";
            File.WriteAllBytes(f.FilePath, Utf8.GetBytes(edited));
            var result = await f.Engine.UpgradeWorkflowAsync("upgrade-test", false, preview.Document.ByteHash, preview.Document.Path);
            Assert.False(result.Changed);
            Assert.Contains("changed on disk", result.Reason);
            Assert.Equal(edited, result.Document.ExactText);
            Assert.Equal(Utf8.GetBytes(edited), File.ReadAllBytes(f.FilePath));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Identical_copy_in_a_moved_project_does_not_satisfy_a_reviewed_path(bool dryRun)
        {
            using var f = new Fixture(Simple);
            var session = await f.Sessions.CreateAsync("Upgrade model", 1600);
            session.SourcePath = Path.Combine(f.Workspace, "model.bim");
            var preview = await f.Engine.UpgradeWorkflowAsync("upgrade-test");
            var second = Path.Combine(f.Workspace, "second-project");
            var secondFile = Path.Combine(second, ".semanticus", "workflows", "upgrade-test.md");
            Directory.CreateDirectory(Path.GetDirectoryName(secondFile)!);
            File.WriteAllBytes(secondFile, Utf8.GetBytes(Simple));
            session.SourcePath = Path.Combine(second, "model.bim");
            var result = await f.Engine.UpgradeWorkflowAsync("upgrade-test", dryRun, preview.Document.ByteHash, preview.Document.Path);
            Assert.False(result.Changed);
            Assert.Contains("different file", result.Reason);
            Assert.Equal(secondFile, result.Document.Path);
            Assert.Equal(Utf8.GetBytes(Simple), File.ReadAllBytes(secondFile));
            Assert.Equal(Utf8.GetBytes(Simple), File.ReadAllBytes(f.FilePath));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Queued_upgrade_refuses_a_replaced_context(bool dryRun)
        {
            using var f = new Fixture(Simple);
            var document = await f.Engine.GetWorkflowDocumentAsync("upgrade-test");
            var context = f.Sessions.CurrentContext;
            await context.WorkflowGate.WaitAsync();
            var pending = f.Engine.UpgradeWorkflowAsync("upgrade-test", dryRun, document.ByteHash, document.Path);
            Assert.False(pending.IsCompleted);
            f.Sessions.ExchangeRetiredContext(new SessionContext(null));
            context.WorkflowGate.Release();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            Assert.Contains("model changed", error.Message);
            Assert.Equal(Utf8.GetBytes(Simple), File.ReadAllBytes(f.FilePath));
            context.Dispose();
        }

        [Fact]
        public async Task Stock_library_previews_are_safe_and_never_modify_stock_files()
        {
            using var f = new Fixture(Simple);
            var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "workflows"), "*.md");
            Assert.NotEmpty(files);
            var safe = 0;
            var refusedCount = 0;
            foreach (var file in files)
            {
                var bytes = File.ReadAllBytes(file);
                var preview = await f.Engine.UpgradeWorkflowAsync(Path.GetFileNameWithoutExtension(file));
                Assert.False(preview.Changed);
                Assert.False(preview.CanApply);
                AssertNoNextAction(preview);
                if (preview.Document.Metadata.SchemaVersion == 2 || preview.Reason.Contains("project copy")) safe++;
                else
                {
                    refusedCount++;
                    Assert.True(preview.ParseError != null || preview.Reason.Contains("would change"), preview.Name + ": " + preview.Reason);
                }
                _output.WriteLine(preview.Name + ": " + preview.Reason);
                if (preview.Diff.Length > 0)
                {
                    Assert.Equal(preview.Document.ExactText, ReconstructDiff(preview.Diff, false));
                    Assert.Equal(preview.ProposedText, ReconstructDiff(preview.Diff, true));
                }
                var refused = await f.Engine.UpgradeWorkflowAsync(preview.Name, false, preview.Document.ByteHash, preview.Document.Path);
                Assert.False(refused.Changed);
                AssertNoNextAction(refused);
                Assert.Contains("project copy", refused.Reason);
                Assert.DoesNotContain("save_workflow", refused.Reason);
                Assert.DoesNotContain("createOnly", refused.Reason);
                Assert.Equal(bytes, File.ReadAllBytes(file));
            }
            var unfenced = await f.Engine.UpgradeWorkflowAsync("new-measure", false);
            Assert.False(unfenced.Changed);
            Assert.Contains("project copy", unfenced.Reason);
            Assert.True(safe > 0);
            _output.WriteLine($"Stock totals: {safe} safe, {refusedCount} refused, {files.Length} files; zero stock writes.");
        }

        [Theory]
        [InlineData("Check the calendar: compare its answer with a trusted number.")]
        [InlineData("Review the model: fix selected findings and scan again.")]
        public async Task Legacy_colon_prose_is_refused_without_requoting_its_description(string prose)
        {
            // User files retain legacy spelling even when the stock library's wording changes.
            var source = Simple.Replace("title: Upgrade test", "title: Upgrade test" + Lf + "description: " + prose);
            using var f = new Fixture(source);
            var document = await f.Engine.GetWorkflowDocumentAsync("upgrade-test");
            var description = document.ExactText.Split((char)10).Single(line => line.StartsWith("description:", StringComparison.Ordinal));
            var preview = await f.Engine.UpgradeWorkflowAsync("upgrade-test");
            Assert.False(preview.CanApply);
            Assert.False(preview.Changed);
            Assert.NotNull(preview.ParseError);
            Assert.Contains("frontmatter", preview.ParseError);
            Assert.Contains(description, preview.ProposedText);
            Assert.Equal(document.ExactText, ReconstructDiff(preview.Diff, false));
            Assert.Equal(Utf8.GetBytes(document.ExactText), File.ReadAllBytes(document.Path));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Mcp_default_preview_and_explicit_apply_share_the_named_pipe_owner(bool modelOpen)
        {
            using var f = new Fixture(Simple);
            string sessionId = null;
            if (modelOpen)
            {
                var session = await f.Sessions.CreateAsync("Upgrade model", 1600);
                session.SourcePath = Path.Combine(f.Workspace, "model.bim");
                sessionId = session.Id;
            }
            var pipe = "semanticus-upgrade-" + Guid.NewGuid().ToString("N");
            using var server = new RpcServer(f.Sessions, f.Engine, pipe);
            using var cts = new CancellationTokenSource();
            var running = server.RunAsync(cts.Token);
            try
            {
                using var remote = await RemoteEngine.ConnectAsync(pipe);
                var preview = await McpTools.UpgradeWorkflow(remote, "upgrade-test");
                Assert.True(preview.DryRun);
                Assert.False(preview.Changed);
                Assert.Equal(Utf8.GetBytes(Simple), File.ReadAllBytes(f.FilePath));
                foreach (var json in WireJson(preview))
                {
                    using var wire = System.Text.Json.JsonDocument.Parse(json);
                    Assert.True(wire.RootElement.TryGetProperty("suggested_next_action", out var action), "A safe preview must carry its structured apply action on both doors.");
                    Assert.False(wire.RootElement.TryGetProperty("suggestedNextAction", out _));
                    Assert.Equal("upgrade_workflow", action.GetProperty("op").GetString());
                    Assert.Contains("Review", action.GetProperty("why").GetString());
                    var args = action.GetProperty("args");
                    Assert.Equal("upgrade-test", args.GetProperty("name").GetString());
                    Assert.False(args.GetProperty("dryRun").GetBoolean());
                    Assert.Equal(preview.Document.ByteHash, args.GetProperty("expectByteHash").GetString());
                    Assert.Equal(preview.Document.Path, args.GetProperty("expectPath").GetString());
                    if (modelOpen) Assert.Equal(sessionId, args.GetProperty("sessionId").GetString());
                    else Assert.False(args.TryGetProperty("sessionId", out _));
                }
                await Assert.ThrowsAnyAsync<Exception>(() => McpTools.UpgradeWorkflow(remote, "upgrade-test", false,
                    preview.Document.ByteHash, preview.Document.Path, "wrong-session"));
                using var mcpWire = System.Text.Json.JsonDocument.Parse(WireJson(preview)[1]);
                var applyArgs = mcpWire.RootElement.GetProperty("suggested_next_action").GetProperty("args");
                var result = await McpTools.UpgradeWorkflow(remote, applyArgs.GetProperty("name").GetString(), applyArgs.GetProperty("dryRun").GetBoolean(),
                    applyArgs.GetProperty("expectByteHash").GetString(), applyArgs.GetProperty("expectPath").GetString(),
                    applyArgs.TryGetProperty("sessionId", out var target) ? target.GetString() : null);
                Assert.True(result.Changed, result.Reason);
                AssertNoNextAction(result);
                var humanRead = await f.Engine.GetWorkflowDocumentAsync("upgrade-test");
                Assert.Equal(result.Document.ByteHash, humanRead.ByteHash);
                Assert.Equal(preview.ProposedText, humanRead.ExactText);
                var again = await remote.UpgradeWorkflowAsync("upgrade-test", false, humanRead.ByteHash, humanRead.Path);
                Assert.False(again.Changed);
                AssertNoNextAction(again);
                Assert.Contains("already uses version 2", again.Reason);
            }
            finally { cts.Cancel(); try { await running; } catch (OperationCanceledException) { } }
        }
    }
}
