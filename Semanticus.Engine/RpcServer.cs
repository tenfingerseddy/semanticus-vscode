using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using StreamJsonRpc;

namespace Semanticus.Engine
{
    /// <summary>
    /// Named-pipe JSON-RPC host. Accepts MULTIPLE concurrent clients (e.g. the VS Code UI and, in
    /// the cross-process case, the MCP proxy), all sharing one <see cref="SessionManager"/>. When the
    /// shared <see cref="ChangeBus"/> fires, every connected client receives a <c>model/didChange</c>
    /// notification — which is exactly how one driver's edit becomes visible to the other live.
    /// Framing is Content-Length headers + JSON (compatible with vscode-jsonrpc).
    /// </summary>
    public sealed class RpcServer : IDisposable
    {
        private readonly IEngine _engine;
        private readonly SessionManager _sessions;
        private readonly string _pipeName;
        private readonly List<JsonRpc> _clients = new List<JsonRpc>();
        private readonly object _gate = new object();

        public RpcServer(SessionManager sessions, IEngine engine, string pipeName)
        {
            _sessions = sessions;
            _engine = engine;
            _pipeName = pipeName;
            _sessions.Bus.Changed += OnChanged;
            _sessions.Bus.PlanChanged += OnPlanChanged;
            _sessions.Bus.SpecChanged += OnSpecChanged;
            _sessions.Bus.Activity += OnActivity;
            _sessions.Bus.LayoutChanged += OnLayoutChanged;
            _sessions.Bus.WorkflowChanged += OnWorkflowChanged;
            _sessions.Bus.WorkflowLibraryChanged += OnWorkflowLibraryChanged;
        }

        public string PipeName => _pipeName;

        private void OnChanged(ChangeNotification n) => Broadcast("model/didChange", n);
        private void OnPlanChanged(ChangePlanView v) => Broadcast("plan/didChange", v);
        private void OnSpecChanged(SpecView v) => Broadcast("spec/didChange", v);
        private void OnActivity(ActivityEvent e) => Broadcast("model/activity", e);
        private void OnLayoutChanged(LayoutChange v) => Broadcast("layout/didChange", v);
        private void OnWorkflowChanged(WorkflowRunView v) => Broadcast("workflow/didChange", v);
        private void OnWorkflowLibraryChanged(WorkflowInfo[] v) => Broadcast("workflow/libraryDidChange", v);

        private void Broadcast(string method, object payload)
        {
            JsonRpc[] snapshot;
            lock (_gate) snapshot = _clients.ToArray();
            foreach (var c in snapshot)
            {
                try { _ = c.NotifyAsync(method, payload); }
                catch { /* client may be disconnecting */ }
            }
        }

        /// <summary>Accept connections until cancelled. Each connection is served on its own task.</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                try
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    pipe.Dispose();
                    break;
                }

                _ = ServeAsync(pipe);
            }
        }

        private async Task ServeAsync(NamedPipeServerStream pipe)
        {
            var rpc = new JsonRpc(CreateHandler(pipe), new EngineRpcTarget(_engine));
            lock (_gate) _clients.Add(rpc);
            rpc.StartListening();
            try { await rpc.Completion.ConfigureAwait(false); }
            catch { /* normal on disconnect */ }
            finally
            {
                lock (_gate) _clients.Remove(rpc);
                rpc.Dispose();
                try { pipe.Dispose(); } catch { }
            }
        }

        public static IJsonRpcMessageHandler CreateHandler(Stream stream)
        {
            var formatter = new JsonMessageFormatter();
            ConfigureSerializer(formatter.JsonSerializer);
            return new HeaderDelimitedMessageHandler(stream, stream, formatter);
        }

        /// <summary>The wire contract, in ONE place so it is testable. camelCase PROPERTY names (the shape the webview
        /// DTOs mirror) but NOT dictionary KEYS: those are DATA/identifiers — capability ids ("QueryData"), model
        /// object names, filter-context strings — that must round-trip verbatim. Newtonsoft's
        /// CamelCasePropertyNamesContractResolver defaults ProcessDictionaryKeys=true, which silently rewrote the
        /// agent-policy matrix keys (QueryData -> queryData) so the Permissions webview's PascalCase lookups all missed
        /// and every cell rendered "deny". OverrideSpecifiedNames=true keeps parity with the old resolver for props.</summary>
        internal static void ConfigureSerializer(JsonSerializer s)
        {
            s.ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false, OverrideSpecifiedNames = true },
            };
            s.NullValueHandling = NullValueHandling.Ignore;
        }

        public void Dispose()
        {
            _sessions.Bus.Changed -= OnChanged;
            _sessions.Bus.PlanChanged -= OnPlanChanged;
            _sessions.Bus.SpecChanged -= OnSpecChanged;
            _sessions.Bus.Activity -= OnActivity;
            _sessions.Bus.LayoutChanged -= OnLayoutChanged;
            _sessions.Bus.WorkflowChanged -= OnWorkflowChanged;
            _sessions.Bus.WorkflowLibraryChanged -= OnWorkflowLibraryChanged;
            JsonRpc[] snapshot;
            lock (_gate) { snapshot = _clients.ToArray(); _clients.Clear(); }
            foreach (var c in snapshot) { try { c.Dispose(); } catch { } }
        }
    }
}
