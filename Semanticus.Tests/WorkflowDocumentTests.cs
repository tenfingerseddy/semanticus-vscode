using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class WorkflowDocumentTests
    {
        private static readonly string Markdown = """
            ---
            schemaVersion: 2
            name: document-test
            title: Café source
            version: 7
            ---
            <!-- keep this comment and spacing -->
            ## Step 1: First
            Exact text.  
            ```yaml step
            id: first
            ```
            """ + (char)10;

        private sealed class Free : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "free" };
        }

        private sealed class Fixture : IDisposable
        {
            public string Workspace { get; } = Path.Combine(Path.GetTempPath(), "smx-document-" + Guid.NewGuid().ToString("N"));
            public SessionManager Sessions { get; } = new SessionManager();
            public LocalEngine Engine { get; }
            public string FilePath => Path.Combine(Workspace, ".semanticus", "workflows", "document-test.md");
            public Fixture()
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                Engine = new LocalEngine(Sessions, new Free(), Workspace);
            }
            public void Write(string text) => File.WriteAllBytes(FilePath, new UTF8Encoding(false, true).GetBytes(text));
            public void Dispose() { Sessions.Dispose(); Directory.Delete(Workspace, true); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public async Task Raw_read_and_noop_preserve_bom_newlines_comments_and_hash(bool bom, bool crlf)
        {
            using var f = new Fixture();
            var lf = ((char)10).ToString();
            var text = (bom ? ((char)0xfeff).ToString() : "") + Markdown.TrimEnd((char)10).Replace(lf, crlf ? ((char)13).ToString() + lf : lf) + "  ";
            f.Write(text);
            var bytes = File.ReadAllBytes(f.FilePath);
            var fixedTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(f.FilePath, fixedTime);
            var broadcasts = 0;
            f.Sessions.Bus.WorkflowLibraryChanged += _ => broadcasts++;
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            Assert.Equal(text, doc.ExactText);
            Assert.Equal("sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), doc.ByteHash);
            Assert.Equal(Path.GetFullPath(f.FilePath), doc.Path);
            Assert.Equal("user", doc.Library);
            Assert.True(doc.Metadata.Parses, doc.Metadata.ParseError);
            Assert.Equal(2, doc.Metadata.SchemaVersion);
            Assert.Equal(7, doc.Metadata.Version);
            Assert.True(doc.Metadata.ExplicitIds);
            Assert.Equal(new[] { "first" }, doc.Metadata.StepIds);
            var result = await f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, doc.ExactText, doc.Path, "human");
            Assert.False(result.Changed);
            Assert.Equal(text, result.Document.ExactText);
            Assert.Equal(bytes, File.ReadAllBytes(f.FilePath));
            Assert.Equal(fixedTime, File.GetLastWriteTimeUtc(f.FilePath));
            Assert.Equal(0, broadcasts);
        }

        [Fact]
        public async Task Changed_edit_persists_exact_bytes_broadcasts_once_and_keeps_run_snapshot()
        {
            using var f = new Fixture();
            f.Write(Markdown);
            var run = await f.Engine.StartWorkflowAsync("document-test", "human");
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var broadcasts = 0;
            f.Sessions.Bus.WorkflowLibraryChanged += _ => broadcasts++;
            var changed = doc.ExactText.Replace("Exact text.", "Changed café text.") + ((char)13).ToString() + (char)10;
            var edit = await f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, changed, doc.Path, "human");
            Assert.True(edit.Changed, edit.Reason);
            Assert.Equal(changed, edit.Document.ExactText);
            Assert.Equal(edit.ByteHash, edit.Document.ByteHash);
            Assert.NotEqual(doc.ByteHash, edit.ByteHash);
            Assert.Equal(new UTF8Encoding(false, true).GetBytes(changed), File.ReadAllBytes(f.FilePath));
            Assert.Equal(1, broadcasts);
            Assert.Equal(run.CurrentStep.Instructions, (await f.Engine.GetWorkflowRunAsync(run.RunId)).CurrentStep.Instructions);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(f.FilePath)!, "*.tmp"));
        }

        [Fact]
        public async Task Stale_hash_returns_current_source_and_never_writes_the_draft()
        {
            using var f = new Fixture();
            f.Write(Markdown);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var external = Markdown + " external edit";
            f.Write(external);
            var broadcasts = 0;
            f.Sessions.Bus.WorkflowLibraryChanged += _ => broadcasts++;
            var result = await f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown + " draft", doc.Path, "human");
            Assert.False(result.Changed);
            Assert.Contains("changed on disk", result.Reason);
            Assert.Equal(external, result.Document.ExactText);
            Assert.Equal(result.ByteHash, result.Document.ByteHash);
            Assert.NotEqual(doc.ByteHash, result.ByteHash);
            Assert.Equal(external, File.ReadAllText(f.FilePath));
            Assert.Equal(0, broadcasts);
        }

        [Theory]
        [InlineData("missing-hash")]
        [InlineData("missing-path")]
        [InlineData("wrong-path")]
        [InlineData("broken")]
        [InlineData("renamed")]
        [InlineData("invalid-unicode")]
        public async Task Invalid_edits_return_current_document_without_writing(string scenario)
        {
            using var f = new Fixture();
            f.Write(Markdown);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var proposed = scenario == "broken" ? "not markdown frontmatter" : scenario == "renamed"
                ? Markdown.Replace("name: document-test", "name: another-name") : scenario == "invalid-unicode"
                ? Markdown + (char)0xd800 : Markdown + " edited";
            var expectPath = scenario == "missing-path" ? "" : scenario == "wrong-path" ? doc.Path + ".other" : doc.Path;
            var result = await f.Engine.EditWorkflowDocumentAsync(doc.Name, scenario == "missing-hash" ? "" : doc.ByteHash, proposed, expectPath, "agent");
            Assert.False(result.Changed);
            Assert.Equal(doc.ByteHash, result.ByteHash);
            Assert.Equal(Markdown, result.Document.ExactText);
            Assert.Equal(Markdown, File.ReadAllText(f.FilePath));
        }

        [Fact]
        public async Task Invalid_saved_syntax_remains_readable_and_can_be_repaired()
        {
            using var f = new Fixture();
            f.Write("broken saved source");
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            Assert.False(doc.Metadata.Parses);
            Assert.NotNull(doc.Metadata.ParseError);
            Assert.Equal("broken saved source", doc.ExactText);
            var edit = await f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown, doc.Path, "human");
            Assert.True(edit.Changed, edit.Reason);
            Assert.True(edit.Document.Metadata.Parses);
        }

        [Fact]
        public async Task Invalid_utf8_refuses_read_and_edit_without_replacement_bytes()
        {
            using var f = new Fixture();
            var bytes = new byte[] { 0xff, 0xfe, 0x61 };
            File.WriteAllBytes(f.FilePath, bytes);
            var read = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.GetWorkflowDocumentAsync("document-test"));
            Assert.Contains("UTF-8", read.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.EditWorkflowDocumentAsync("document-test", "hash", Markdown, f.FilePath, "human"));
            Assert.Equal(bytes, File.ReadAllBytes(f.FilePath));
        }

        [Fact]
        public async Task Stock_is_read_only_and_copy_creation_never_replaces_a_newer_project_copy()
        {
            using var f = new Fixture();
            var stock = await f.Engine.GetWorkflowDocumentAsync("new-measure");
            Assert.Equal("stock", stock.Library);
            var refused = await f.Engine.EditWorkflowDocumentAsync(stock.Name, stock.ByteHash, stock.ExactText + " edit", stock.Path, "human");
            Assert.False(refused.Changed);
            Assert.Contains("read-only", refused.Reason);
            Assert.Equal(stock.ExactText, refused.Document.ExactText);
            await f.Engine.SaveWorkflowAsync(stock.Name, stock.ExactText + " newer copy", "agent", createOnly: true);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.SaveWorkflowAsync(stock.Name, stock.ExactText, "human", createOnly: true));
            Assert.Contains("already exists", error.Message);
            Assert.EndsWith(" newer copy", (await f.Engine.GetWorkflowDocumentAsync(stock.Name)).ExactText);
            await f.Engine.DeleteWorkflowAsync(stock.Name, "human");
            Assert.Equal("stock", (await f.Engine.GetWorkflowDocumentAsync(stock.Name)).Library);
        }

        [Theory]
        [InlineData("../document-test")]
        [InlineData("/tmp/document-test")]
        [InlineData("two/paths")]
        public async Task Every_document_writer_and_reader_rejects_path_traversal(string name)
        {
            using var f = new Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.GetWorkflowDocumentAsync(name));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.EditWorkflowDocumentAsync(name, "hash", Markdown, f.FilePath, "human"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.SaveWorkflowAsync(name, Markdown, "human"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.DeleteWorkflowAsync(name, "human"));
        }

        [Fact]
        public async Task Same_hash_competing_edits_have_one_winner_across_engine_instances()
        {
            using var f = new Fixture();
            using var otherSessions = new SessionManager();
            var other = new LocalEngine(otherSessions, new Free(), f.Workspace);
            f.Write(Markdown);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var results = await Task.WhenAll(
                Task.Run(() => f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown + " first", doc.Path, "human")),
                Task.Run(() => other.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown + " second", doc.Path, "agent")));
            Assert.Single(results, r => r.Changed);
            Assert.Single(results, r => !r.Changed);
            Assert.Equal(results.Single(r => r.Changed).Document.ExactText, File.ReadAllText(f.FilePath));
        }

        [Theory]
        [InlineData("read")]
        [InlineData("edit")]
        [InlineData("save")]
        [InlineData("delete")]
        public async Task Queued_document_operation_refuses_after_context_replacement(string operation)
        {
            using var f = new Fixture();
            f.Write(Markdown);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var before = f.Sessions.CurrentContext;
            await before.WorkflowGate.WaitAsync();
            Task pending = operation switch
            {
                "read" => f.Engine.GetWorkflowDocumentAsync(doc.Name),
                "edit" => f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown + " edit", doc.Path, "human"),
                "save" => f.Engine.SaveWorkflowAsync(doc.Name, Markdown + " save", "human"),
                _ => f.Engine.DeleteWorkflowAsync(doc.Name, "human"),
            };
            Assert.False(pending.IsCompleted);
            f.Sessions.ExchangeRetiredContext(new SessionContext(null));
            before.WorkflowGate.Release();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            Assert.Contains("model changed", error.Message);
            Assert.Equal(Markdown, File.ReadAllText(f.FilePath));
            before.Dispose();
        }

        [Fact]
        public async Task Queued_edit_keeps_its_original_project_when_the_same_session_moves()
        {
            using var f = new Fixture();
            var session = await f.Sessions.CreateAsync("Document model", 1600);
            session.SourcePath = Path.Combine(f.Workspace, "model.bim");
            f.Write(Markdown);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var context = f.Sessions.CurrentContext;
            await context.WorkflowGate.WaitAsync();
            var pending = f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown + " old project edit", doc.Path, "human");
            Assert.False(pending.IsCompleted);
            var secondRoot = Path.Combine(f.Workspace, "second-project");
            var secondFile = Path.Combine(secondRoot, ".semanticus", "workflows", "document-test.md");
            Directory.CreateDirectory(Path.GetDirectoryName(secondFile)!);
            File.WriteAllText(secondFile, Markdown);
            session.SourcePath = Path.Combine(secondRoot, "model.bim");
            context.WorkflowGate.Release();
            var result = await pending;
            Assert.True(result.Changed, result.Reason);
            Assert.Equal(f.FilePath, result.Document.Path);
            Assert.EndsWith(" old project edit", File.ReadAllText(f.FilePath));
            Assert.Equal(Markdown, File.ReadAllText(secondFile));
        }

        [Fact]
        public async Task Mcp_and_named_pipe_edit_the_same_document_and_honor_session_targeting()
        {
            using var f = new Fixture();
            f.Write(Markdown);
            var pipe = "semanticus-document-" + Guid.NewGuid().ToString("N");
            using var server = new RpcServer(f.Sessions, f.Engine, pipe);
            using var cts = new CancellationTokenSource();
            var running = server.RunAsync(cts.Token);
            try
            {
                using var remote = await RemoteEngine.ConnectAsync(pipe);
                var doc = await remote.GetWorkflowDocumentAsync("document-test");
                var wrongFile = await McpTools.EditWorkflowDocument(remote, doc.Name, doc.ByteHash, doc.ExactText, doc.Path + ".other");
                Assert.False(wrongFile.Changed);
                Assert.Contains("different file", wrongFile.Reason);
                var agentEdit = await McpTools.EditWorkflowDocument(remote, doc.Name, doc.ByteHash, Markdown + " agent edit", doc.Path);
                Assert.True(agentEdit.Changed, agentEdit.Reason);
                var humanRead = await f.Engine.GetWorkflowDocumentAsync(doc.Name);
                Assert.Equal(agentEdit.ByteHash, humanRead.ByteHash);
                var stale = await remote.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown + " old human draft", doc.Path, "human");
                Assert.False(stale.Changed);
                Assert.Equal(humanRead.ExactText, stale.Document.ExactText);
                await Assert.ThrowsAnyAsync<Exception>(() => remote.GetWorkflowDocumentAsync(doc.Name, "stale-session"));
                await Assert.ThrowsAnyAsync<Exception>(() => McpTools.EditWorkflowDocument(remote, doc.Name, humanRead.ByteHash, Markdown, humanRead.Path, "stale-session"));
                Assert.Equal(humanRead.ExactText, File.ReadAllText(f.FilePath));
            }
            finally { cts.Cancel(); try { await running; } catch (OperationCanceledException) { } }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Read_then_project_change_refuses_to_edit_identical_file_in_new_project(bool replaceSession)
        {
            using var f = new Fixture();
            var first = await f.Sessions.CreateAsync("First model", 1600);
            first.SourcePath = Path.Combine(f.Workspace, "model.bim");
            f.Write(Markdown);
            var doc = await f.Engine.GetWorkflowDocumentAsync("document-test");
            var secondRoot = Path.Combine(f.Workspace, "second-project");
            var secondFile = Path.Combine(secondRoot, ".semanticus", "workflows", "document-test.md");
            Directory.CreateDirectory(Path.GetDirectoryName(secondFile)!);
            File.WriteAllText(secondFile, Markdown);
            var current = replaceSession ? await f.Sessions.CreateAsync("Second model", 1600) : first;
            current.SourcePath = Path.Combine(secondRoot, "model.bim");

            var result = await f.Engine.EditWorkflowDocumentAsync(doc.Name, doc.ByteHash, Markdown + " old draft", doc.Path, "human");

            Assert.False(result.Changed);
            Assert.Contains("different file", result.Reason);
            Assert.Equal(Markdown, File.ReadAllText(f.FilePath));
            Assert.Equal(Markdown, File.ReadAllText(secondFile));
        }
    }
}
