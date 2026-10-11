using System;
using System.Collections.Generic;
using System.Threading;

namespace Semanticus.Engine
{
    /// <summary>
    /// The approval one assistant call carries into the engine, minted by <see cref="AssistantEngine"/> after the person said
    /// yes to exactly that call: its capability, its method, its arguments, the model it was asked about and what it was
    /// asked at (the revision for a model edit, the state of the path for a file write). A sink reads it from
    /// <see cref="GateContext"/>; a sink with no ticket for an assistant's write that needs one refuses (fail closed), and a
    /// sink that has one checks it against the world as it stands inside its own turn, so a change made between the
    /// question and the commit is refused rather than ridden over.
    /// </summary>
    internal sealed class AssistantTicket
    {
        public AgentCapability[] Capabilities { get; init; } = Array.Empty<AgentCapability>();
        public string Method { get; init; }
        public string GrantKey { get; init; }
        /// <summary>The session the person was asked about. A ticket is never good for another model, nor for the same file
        /// opened again (a new session).</summary>
        public string SessionId { get; init; }
        /// <summary>The revision the approved call may start from, or -1 when the call is not bound to one. It moves forward
        /// by this call's own commits (a call may commit more than once), never by anyone else's.</summary>
        public long ExpectedRevision;
        /// <summary>The content the call was approved at, where its row binds one besides the revision (an apply_plan's lines); null when not.</summary>
        public string ContentBasis { get; init; }
        /// <summary>Paths the call may write, each with the state it was in when asked ("absent" or a hash of what was there),
        /// keyed by the full path with links resolved.</summary>
        public IReadOnlyDictionary<string, string> Paths { get; init; } = new Dictionary<string, string>();
        /// <summary>The state the open model's own files were in when the person was asked, or null when the call does not write them.
        /// The first write of those files checks it and lets go: the call's own writes change the files after that.</summary>
        public string ModelFilesState { get; set; }

        public bool Covers(AgentCapability cap, string sessionId) =>
            Array.IndexOf(Capabilities, cap) >= 0
            && (string.IsNullOrEmpty(SessionId) || string.Equals(SessionId, sessionId, StringComparison.Ordinal));
    }

    /// <summary>
    /// The ambient record of an assistant's call, set by <see cref="AssistantEngine"/> around the engine method it forwards.
    /// It exists so a sink with no origin of its own (an open that would throw away unsaved work) still knows an assistant is
    /// behind it, and so a sink can find the approval that call carries. Read on the CALLER's thread before any hop to the
    /// model's dispatcher thread (AsyncLocal need not flow there, the same rule <see cref="DryRunScope"/> follows).
    /// </summary>
    internal static class GateContext
    {
        private sealed class Scope { public string Method; public AssistantTicket Ticket; public IEngine Gated; public string OpenEntryBasis; }
        private static readonly AsyncLocal<Scope> _current = new();

        public static bool InAssistantCall => _current.Value != null;
        public static AssistantTicket Ticket => _current.Value?.Ticket;
        public static string Method => _current.Value?.Method;
        /// <summary>The gated engine the assistant's call came in through, so a tool the engine runs on the assistant's behalf (a dry run)
        /// uses it as its receiver and every write it makes meets the gate again.</summary>
        public static IEngine Gated => _current.Value?.Gated;

        /// <summary>What an assistant's open would have thrown away from the session's memory (spec, plan lines) when it started, as a basis hash, or null when none was
        /// recorded. The swap compares it with what is there at its point of no return, so work that arrived while the model loaded is not dropped unasked.
        /// Kept on the scope, which every continuation of the call shares.</summary>
        public static string OpenEntryBasis => _current.Value?.OpenEntryBasis;
        public static void NoteOpenEntry(string basis) { if (_current.Value is { } scope) scope.OpenEntryBasis = basis; }

        public static IDisposable Enter(string method, AssistantTicket ticket, IEngine gated = null)
        {
            var before = _current.Value;
            _current.Value = new Scope { Method = method, Ticket = ticket, Gated = gated ?? before?.Gated };
            return new Restore(before);
        }

        private sealed class Restore : IDisposable
        {
            private readonly Scope _before;
            private int _done;
            public Restore(Scope before) { _before = before; }
            public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) _current.Value = _before; }
        }
    }
}

namespace Semanticus.Engine
{
    /// <summary>Test seam: names a sink whose refusal is switched off, so a test can show that its tripwire case really depends on it
    /// (the red proof: with a sink off, the case that guards it fails). Empty in every real run; nothing in the product sets it.</summary>
    internal static class GateSeams
    {
        internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> DisabledForTest = new();
        internal static bool Off(string sink) => !DisabledForTest.IsEmpty && DisabledForTest.ContainsKey(sink);
    }
}
