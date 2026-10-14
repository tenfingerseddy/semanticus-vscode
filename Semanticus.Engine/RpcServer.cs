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
        private readonly string _uiChallenge;
        private readonly List<JsonRpc> _clients = new List<JsonRpc>();
        // The agent-role subset of _clients. An attached assistant (the MCP proxy) connects as an agent, so
        // this count is one of the engine's two PRESENCE signals for the assistant handoff: see
        // LocalEngine.Jev.cs. Held as its own set rather than derived, so the two cannot drift.
        private readonly HashSet<JsonRpc> _agentClients = new HashSet<JsonRpc>();
        private readonly object _gate = new object();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        // Null except in the owner `serve` host. Handed only to the HUMAN target, so adoptParent cannot be
        // reached from an agent connection: an agent must not retarget which process this engine dies with.
        private readonly ParentLifetime _parentLifetime;
        // Null except in the owner `serve` host, and handed only to the HUMAN target for the same reason (F-244).
        private readonly CleanStop _cleanStop;

        public RpcServer(SessionManager sessions, IEngine engine, string pipeName, string uiChallenge = null, ParentLifetime parentLifetime = null,
                         CleanStop cleanStop = null)
        {
            _cleanStop = cleanStop;
            _sessions = sessions;
            _engine = engine;
            _pipeName = pipeName;
            _parentLifetime = parentLifetime;
            _uiChallenge = uiChallenge == null ? null : RpcHandshake.ValidateChallenge(uiChallenge);
            _sessions.Bus.Changed += OnChanged;
            _sessions.Bus.PlanChanged += OnPlanChanged;
            _sessions.Bus.PlanRunProgressed += OnPlanRunProgress;
            _sessions.Bus.Progress += OnProgress;
            _sessions.Bus.SpecChanged += OnSpecChanged;
            _sessions.Bus.Activity += OnActivity;
            _sessions.Bus.LayoutChanged += OnLayoutChanged;
            _sessions.Bus.WorkflowChanged += OnWorkflowChanged;
            _sessions.Bus.WorkflowLibraryChanged += OnWorkflowLibraryChanged;
            _sessions.Bus.WorkflowLayoutChanged += OnWorkflowLayoutChanged;
            _sessions.Bus.DraftChanged += OnDraftChanged;
            _sessions.Bus.StartPlanChanged += OnStartPlanChanged;
            _sessions.Bus.SessionChanged += OnSessionChanged;
            // The Studio wire (1.4): the assistant says something, suggests a next step, or starts a call.
            _sessions.Bus.StudioShow += OnStudioShow;
            _sessions.Bus.StudioSuggest += OnStudioSuggest;
            _sessions.Bus.StudioNextStep += OnStudioNextStep;
            _sessions.Bus.StudioAnswer += OnStudioAnswer;
            _sessions.Bus.AgentCall += OnAgentCall;
            _sessions.Bus.EntitlementChanged += OnEntitlementChanged;
            _sessions.Bus.HumanClientAttached = () => { lock (_gate) return _clients.Count > _agentClients.Count; };
        }

        public string PipeName => _pipeName;

        /// <summary>How many AGENT-role clients are attached right now. A human UI connection is not one of
        /// these. The assistant handoff reads it to answer "is anyone there to ask", so a job stops at a named
        /// stub instead of queueing words nobody will ever read.</summary>
        public int AgentClientCount { get { lock (_gate) return _agentClients.Count; } }

        /// <summary>Every attached client, either role.</summary>
        public int ClientCount { get { lock (_gate) return _clients.Count; } }

        private void OnChanged(ChangeNotification n) => Broadcast("model/didChange", n);
        private void OnPlanChanged(ChangePlanView v) => Broadcast("plan/didChange", v);
        private void OnPlanRunProgress(PlanRunProgress v) => Broadcast("plan/runProgress", v);
        private void OnProgress(OperationProgress v) => Broadcast("progress/didChange", v);
        private void OnSpecChanged(SpecView v) => Broadcast("spec/didChange", v);
        private void OnActivity(ActivityEvent e) => Broadcast("model/activity", e);
        private void OnStudioShow(StudioNote n) => Broadcast("studio/show", n);
        private void OnStudioSuggest(StudioSuggestion v) => Broadcast("studio/suggest", v);
        private void OnStudioNextStep(NextStepView v) => Broadcast("studio/nextStep", v);
        private void OnStudioAnswer(StudioAnswer v) => Broadcast("studio/answer", v);
        private void OnAgentCall(AgentCallEvent v) => Broadcast("assistant/call", v);
        // A licence applied, removed, or past its end (the free Pro month ending while a window is open).
        private void OnEntitlementChanged(Entitlement.EntitlementInfo v) => Broadcast("entitlement/didChange", v);
        private void OnLayoutChanged(LayoutChange v) => Broadcast("layout/didChange", v);
        private void OnWorkflowChanged(WorkflowRunView v) => Broadcast("workflow/didChange", v);
        private void OnWorkflowLibraryChanged(WorkflowInfo[] v) => Broadcast("workflow/libraryDidChange", v);
        private void OnWorkflowLayoutChanged(WorkflowLayout v) => Broadcast("workflow/layoutDidChange", v);
        private void OnDraftChanged(Composer.DraftView v) => Broadcast("draft/didChange", v);
        // The spec-first start plan (M11): the whole view, to both roles, like a draft. The agent door learns of it on its next call (the fold).
        private void OnStartPlanChanged(StartPlan.StartPlanView v) => Broadcast("startPlan/didChange", v);
        // The connection that asked for the swap is skipped: it refreshes from its own answer, and an echo racing that
        // answer would tell its Studio the model was replaced while the request was still in flight.
        private void OnSessionChanged(SessionChangeNotification n) => Broadcast("session/didChange", n, exceptWriter: n?.Writer);

        private void Broadcast(string method, object payload, string exceptWriter = null)
        {
            JsonRpc[] snapshot;
            lock (_gate) snapshot = _clients.ToArray();
            foreach (var c in snapshot)
            {
                if (exceptWriter != null && c is WriterStampedJsonRpc w && w.Writer == exceptWriter) continue;
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

                _ = ServeAsync(pipe, _stop.Token);
            }
        }

        private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            JsonRpc rpc = null;
            string connectionId = null;
            try
            {
                RpcConnectionRole role;
                try
                {
                    role = await RpcHandshake.ReadAsync(pipe, _uiChallenge, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // A rejection is explicit on the wire. Without it, a client can mistake a successful socket
                    // write for authentication and hand a pipe that the server already closed to JSON-RPC.
                    try { await RpcHandshake.WriteResponseAsync(pipe, false).ConfigureAwait(false); } catch { }
                    return;
                }
                await RpcHandshake.WriteResponseAsync(pipe, true, cancellationToken).ConfigureAwait(false);
                // F-246: every request on this connection is stamped with its id, so a commit knows who sent it, and
                // an agent connection is a reader of the change journal from this moment on (its own commits excluded).
                connectionId = "rpc:" + Interlocked.Increment(ref _nextConnection);
                rpc = new WriterStampedJsonRpc(CreateHandler(pipe), connectionId, _engine, role == RpcConnectionRole.Human);
                var reader = role == RpcConnectionRole.Human ? null : connectionId;
                if (reader != null) _sessions.Changes.Register(reader);
                rpc.AddLocalRpcTarget(new EngineRpcTarget(_engine, role == RpcConnectionRole.Human ? "human" : "agent", reader));
                if (role == RpcConnectionRole.Human)
                    rpc.AddLocalRpcTarget(new HumanGovernanceRpcTarget(_engine, _parentLifetime, _cleanStop));
                lock (_gate)
                {
                    _clients.Add(rpc);
                    if (role != RpcConnectionRole.Human) _agentClients.Add(rpc);
                }
                rpc.StartListening();
                await rpc.Completion.ConfigureAwait(false);
            }
            catch { /* malformed/auth-failed handshakes and disconnects are closed without a method surface */ }
            finally
            {
                if (connectionId != null) _sessions.Changes.Unregister(connectionId);
                if (rpc != null)
                {
                    lock (_gate)
                    {
                        _clients.Remove(rpc);
                        _agentClients.Remove(rpc);
                    }
                    rpc.Dispose();
                }
                try { pipe.Dispose(); } catch { }
            }
        }

        private long _nextConnection;

        /// <summary>F-246: runs every request of one connection under that connection's writer id
        /// (<see cref="ChangeWriter"/>), so <c>Session.TrackAsync</c> can stamp the commits it causes.</summary>
        private sealed class WriterStampedJsonRpc : JsonRpc
        {
            private readonly string _writer;
            private readonly IEngine _engine;
            private readonly bool _human;

            public WriterStampedJsonRpc(IJsonRpcMessageHandler handler, string writer, IEngine engine = null, bool human = true) : base(handler)
            {
                _human = human;
                _writer = writer;
                _engine = engine;
            }

            internal string Writer => _writer;

            protected override async ValueTask<StreamJsonRpc.Protocol.JsonRpcMessage> DispatchRequestAsync(
                StreamJsonRpc.Protocol.JsonRpcRequest request, TargetMethod targetMethod, CancellationToken cancellationToken)
            {
                // Lane E3, contract 8.4: an attached assistant's edits count toward the task this connection claimed
                // most recently, stamped here in the owner because the proxy's ambient scope does not cross the pipe.
                // C1 (drafts-sol round 2): the model the sender's page was showing rides on the request itself, so the person's door can refuse a write
                // whose page is not on the model that is open (PersonFence), whichever method it is and whichever editor sent it.
                request.TryGetTopLevelProperty<string>(RequestModelIdentity.WireName, out var named);
                // Round 3: the extension's own writer says it names a model on EVERY request. One that says so and names none could not tell which model it meant.
                request.TryGetTopLevelProperty<bool>(RequestModelIdentity.StrictWireName, out var strict);
                // A page's button says so on the request (SignInPress): only then may a sign-in window open for it.
                request.TryGetTopLevelProperty<bool>(SignInPress.WireName, out var pressed);
                using (RequestModelIdentity.Begin(named, strict))
                using (pressed && _human ? SignInPress.Press() : null)   // only a person's connection can press: an assistant's "pressed" is ignored
                using (ChangeWriter.Begin(_writer))
                using (McpHealthAppender.TaskCause(_engine, _writer))
                    return await base.DispatchRequestAsync(request, targetMethod, cancellationToken).ConfigureAwait(false);
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
            s.Converters.Add(new JsonElementWire());   // System.Text.Json elements (edit_objects values) must survive this wire
        }

        public void Dispose()
        {
            _stop.Cancel();
            _sessions.Bus.Changed -= OnChanged;
            _sessions.Bus.PlanChanged -= OnPlanChanged;
            _sessions.Bus.PlanRunProgressed -= OnPlanRunProgress;
            _sessions.Bus.Progress -= OnProgress;
            _sessions.Bus.SpecChanged -= OnSpecChanged;
            _sessions.Bus.Activity -= OnActivity;
            _sessions.Bus.LayoutChanged -= OnLayoutChanged;
            _sessions.Bus.WorkflowChanged -= OnWorkflowChanged;
            _sessions.Bus.WorkflowLibraryChanged -= OnWorkflowLibraryChanged;
            _sessions.Bus.WorkflowLayoutChanged -= OnWorkflowLayoutChanged;
            _sessions.Bus.DraftChanged -= OnDraftChanged;
            _sessions.Bus.StartPlanChanged -= OnStartPlanChanged;
            _sessions.Bus.SessionChanged -= OnSessionChanged;
            _sessions.Bus.StudioShow -= OnStudioShow;
            _sessions.Bus.StudioSuggest -= OnStudioSuggest;
            _sessions.Bus.StudioNextStep -= OnStudioNextStep;
            _sessions.Bus.StudioAnswer -= OnStudioAnswer;
            _sessions.Bus.AgentCall -= OnAgentCall;
            _sessions.Bus.EntitlementChanged -= OnEntitlementChanged;
            _sessions.Bus.HumanClientAttached = null;
            JsonRpc[] snapshot;
            lock (_gate) { snapshot = _clients.ToArray(); _clients.Clear(); _agentClients.Clear(); }
            foreach (var c in snapshot) { try { c.Dispose(); } catch { } }
            _stop.Dispose();
        }
    }
}
