using System;
using System.Threading;
using Semanticus.Engine.Jev;

namespace Semanticus.Engine
{
    /// <summary>
    /// Where the engine COMPOSES the Jev foundation's two engine-owned answers.
    ///
    /// 1. THE SESSION GENERATION. <see cref="SessionManager"/> owns it, because its context exchange is the
    ///    only place the open model is actually swapped. The engine just hands the manager out as the source,
    ///    so a job runner is never given a counter that somebody else is guessing at.
    ///
    /// 2. ASSISTANT PRESENCE. Astra B8: the handoff queue checked consent and nothing else, so with consent on
    ///    and nobody connected a job queued a request and waited for a reply that could never come. Consent is
    ///    a permission; presence is a different question, and only the engine can see both doors at once:
    ///      - the RPC door: <see cref="RpcServer"/> classifies every named-pipe connection as human or agent,
    ///        and an attached assistant (the MCP proxy) is an agent-role client. The host hands us its count
    ///        with <see cref="ObserveAgentClients"/>.
    ///      - the MCP door: when THIS process is the MCP server, its stdio peer is the assistant, attached by
    ///        construction. That host calls <see cref="AttachAssistantDoor"/>.
    ///    A build that wires neither answers no, and a job stops at a named stub instead of waiting on nobody.
    /// </summary>
    public sealed partial class LocalEngine
    {
        private readonly object _assistantGate = new object();
        private Func<int> _agentClients = () => 0;
        private int _assistantDoors;
        private IAssistantPresence _assistantPresence;
        private IAssistantRequestQueue _assistantRequests;

        /// <summary>The session generation source a job runner binds jobs to. The manager, never a copy.</summary>
        public ISessionGenerationSource SessionGenerations => _sessions;

        /// <summary>The RPC host tells the engine how to count its agent-role clients. Called once during
        /// composition, before the door serves anything.</summary>
        public void ObserveAgentClients(Func<int> agentClientCount) =>
            _agentClients = agentClientCount ?? (() => 0);

        /// <summary>The MCP door registers its own attachment: an MCP session IS an assistant attached. Held
        /// for as long as that door serves; disposing it detaches.</summary>
        public IDisposable AttachAssistantDoor()
        {
            Interlocked.Increment(ref _assistantDoors);
            return new AssistantDoorAttachment(this);
        }

        /// <summary>Is anyone there to ask, right now. False is a real answer, not a missing one.</summary>
        private bool AssistantAttached
        {
            get
            {
                if (Volatile.Read(ref _assistantDoors) > 0) return true;
                try { return _agentClients() > 0; } catch { return false; }
            }
        }

        /// <summary>The engine's one presence answer, composed from both doors above.</summary>
        public IAssistantPresence AssistantPresence
        {
            get
            {
                lock (_assistantGate)
                    return _assistantPresence ??= new DelegatedAssistantPresence(
                        () => AssistantAttached,
                        "No assistant is connected to this session.");
            }
        }

        /// <summary>
        /// The engine-owned assistant request queue, and the one both doors read. Since Plan at the heart (lane E3,
        /// contract 3.2) it is a VIEW over the Plan's tasks, not a store: a request never exists without an owner
        /// record, and posting waits for an assistant rather than refusing when none is attached (the AMENDS in
        /// docs/redesign/contracts/assistant-handoff.md). Presence is still answered above, for the doors that ask.
        ///
        /// Consent is still the reference queue's default (always on). Wiring the handoff's consent to a real
        /// setting is a separate hook and is deliberately not claimed here.
        /// </summary>
        public IAssistantRequestQueue AssistantRequests
        {
            get { lock (_assistantGate) return _assistantRequests ??= new PlanTaskQueue(this); }
        }

        private sealed class AssistantDoorAttachment : IDisposable
        {
            private LocalEngine _engine;

            public AssistantDoorAttachment(LocalEngine engine) => _engine = engine;

            public void Dispose()
            {
                // Exchange, so a second Dispose cannot take the count below zero.
                var engine = Interlocked.Exchange(ref _engine, null);
                if (engine != null) Interlocked.Decrement(ref engine._assistantDoors);
            }
        }
    }
}
