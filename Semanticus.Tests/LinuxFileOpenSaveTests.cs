using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using StreamJsonRpc;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// UAT C3.2: every supported on-disk format must open and save on Linux. The four defects this
    /// pins are D-032 (BIM save always fails), D-033 (TE folder backslash paths), D-035 (Reference
    /// Model load cannot bind a lone object argument), and the engine half of D-146's dual-drive
    /// (a .bim path must still be a BIM save when the UI door forgets to name the format).
    /// </summary>
    public sealed class LinuxFileOpenSaveTests
    {
        private const string Challenge = "rpc-linux-file-test-challenge-0123456789ab";

        [Fact]
        public async Task D032_saving_an_opened_bim_with_the_ui_tmdl_default_overwrites_the_bim()
        {
            // The Save Model command always sends format TMDL with a null path. On a model opened
            // from a .bim that used to mean "write a TMDL folder at the .bim path", which TE2
            // refuses because the file already exists. Save must keep the BIM bytes and land the edit.
            var root = Path.Combine(Path.GetTempPath(), "semanticus-d032-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var bim = Path.Combine(root, "parallel-b.bim");
            File.Copy(TestModels.FindBim(), bim);
            var before = File.ReadAllBytes(bim);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.OpenAsync(bim);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "1 + 1", "human");

                var saved = await engine.SaveAsync(null, "TMDL");

                Assert.Equal(bim, saved.Path);
                Assert.Equal("ModelSchemaOnly", saved.Format);
                Assert.True(File.Exists(bim));
                Assert.False(Directory.Exists(bim));
                var after = File.ReadAllBytes(bim);
                Assert.NotEqual(before, after);
                Assert.Contains("1 + 1", File.ReadAllText(bim), StringComparison.Ordinal);

                await engine.OpenAsync(bim);
                Assert.Equal("1 + 1", (await engine.ListMeasuresAsync()).First(m => m.Ref == measure.Ref).Expression);
            }
            finally
            {
                engine.Dispose();
                try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
            }
        }

        [Fact]
        public async Task D032_mcp_save_model_with_null_path_keeps_bim_format()
        {
            var root = Path.Combine(Path.GetTempPath(), "semanticus-d032-mcp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var bim = Path.Combine(root, "parallel-c.bim");
            File.Copy(TestModels.FindBim(), bim);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.OpenAsync(bim);
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "2 + 2", "agent");

                var saved = await McpTools.SaveModel(engine, null, "TMDL");

                Assert.Equal(bim, saved.Path);
                Assert.Equal("ModelSchemaOnly", saved.Format);
                Assert.Contains("2 + 2", File.ReadAllText(bim), StringComparison.Ordinal);
            }
            finally
            {
                engine.Dispose();
                try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
            }
        }

        [Fact]
        public async Task D033_te_folder_save_and_open_use_real_directories_on_linux()
        {
            // TE2 concatenates a literal backslash into folder paths. On Linux that writes a file
            // named "S-TE\database.json" instead of a folder. Both doors must create a real
            // directory with database.json inside it, then reopen that folder.
            var root = Path.Combine(Path.GetTempPath(), "semanticus-d033-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var folder = Path.Combine(root, "S-TE");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "3 + 3", "human");

                var saved = await engine.SaveAsync(folder, "folder");
                Assert.Equal("TabularEditorFolder", saved.Format);
                Assert.True(Directory.Exists(folder), "save_model must create a real directory, not a file whose name contains a backslash");
                Assert.True(File.Exists(Path.Combine(folder, "database.json")));
                Assert.False(File.Exists(folder + "\\database.json") && !File.Exists(Path.Combine(folder, "database.json")));

                await engine.OpenAsync(folder);
                Assert.Equal("3 + 3", (await engine.ListMeasuresAsync()).First(m => m.Ref == measure.Ref).Expression);

                var reopened = await McpTools.GetReferenceTree(engine, folder);
                Assert.Contains(reopened, n => n.Ref == measure.Ref);
            }
            finally
            {
                engine.Dispose();
                try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
            }
        }

        [Fact]
        public async Task D033_opening_an_existing_te_folder_finds_database_json()
        {
            var root = Path.Combine(Path.GetTempPath(), "semanticus-d033-open-" + Guid.NewGuid().ToString("N"));
            var folder = Path.Combine(root, "existing-te");
            Directory.CreateDirectory(folder);
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions);
            try
            {
                await engine.OpenAsync(TestModels.FindBim());
                await engine.SaveAsync(folder, "TabularEditorFolder");
                Assert.True(File.Exists(Path.Combine(folder, "database.json")));
                engine.Dispose();

                var engine2 = new LocalEngine(new SessionManager());
                try
                {
                    var opened = await engine2.OpenAsync(folder);
                    Assert.True(opened.Tables > 0);
                    Assert.DoesNotContain("database.json file", opened.ModelName, StringComparison.OrdinalIgnoreCase);
                }
                finally { engine2.Dispose(); }
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
            }
        }

        [Fact]
        public async Task D035_list_reference_tree_accepts_a_lone_file_ref_object_on_the_rpc_wire()
        {
            // vscode-jsonrpc sends a lone object argument as named params. StreamJsonRpc then looks
            // for a method whose parameter names match the object's keys (kind, path, ...) and
            // reports "Unable to find method 'listR...'". The UI door must still load a local file
            // or TMDL folder as a reference model.
            var pipeName = "semanticus-d035-" + Guid.NewGuid().ToString("N");
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            using var server = new RpcServer(sessions, engine, pipeName, Challenge);
            using var stop = new CancellationTokenSource();
            var serving = server.RunAsync(stop.Token);
            RpcClient ui = null;
            var tmp = Path.Combine(Path.GetTempPath(), "semanticus-d035-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var bim = Path.Combine(tmp, "ref.bim");
            File.Copy(TestModels.FindBim(), bim);
            try
            {
                ui = await RpcClient.ConnectAsync(pipeName, "human", Challenge);
                await ui.InvokeAsync<OpenResult>("open", bim);

                var nodes = await ui.InvokeWithNamedArgsAsync<TreeNode[]>("listReferenceTree",
                    new ModelRef { Kind = "file", Path = bim });

                Assert.Contains(nodes, n => n.Kind == "table" || n.Kind == "calcgroup");
                Assert.Contains(nodes, n => n.Kind == "measure");
            }
            finally
            {
                ui?.Dispose();
                stop.Cancel();
                try { await serving; } catch (OperationCanceledException) { }
                sessions.Dispose();
                try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort temp cleanup */ }
            }
        }

        private sealed class RpcClient : IDisposable
        {
            private readonly System.IO.Pipes.NamedPipeClientStream _pipe;
            private readonly JsonRpc _rpc;

            private RpcClient(System.IO.Pipes.NamedPipeClientStream pipe, JsonRpc rpc)
            {
                _pipe = pipe;
                _rpc = rpc;
            }

            internal static async Task<RpcClient> ConnectAsync(string pipeName, string role, string challenge = null)
            {
                var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                await RpcHandshake.WriteAsync(pipe,
                    role == "human" ? RpcConnectionRole.Human : RpcConnectionRole.Agent, challenge);
                await RpcHandshake.ReadAcceptedAsync(pipe);
                var rpc = new JsonRpc(RpcServer.CreateHandler(pipe));
                rpc.StartListening();
                return new RpcClient(pipe, rpc);
            }

            internal Task<T> InvokeAsync<T>(string method, params object[] args) => _rpc.InvokeAsync<T>(method, args);

            // Named params: the exact wire shape vscode-jsonrpc uses for a lone object argument
            // unless the caller forces ParameterStructures.byPosition.
            internal Task<T> InvokeWithNamedArgsAsync<T>(string method, object named)
                => _rpc.InvokeWithParameterObjectAsync<T>(method, named);

            public void Dispose()
            {
                try { _rpc.Dispose(); } catch { }
                try { _pipe.Dispose(); } catch { }
            }
        }
    }
}
