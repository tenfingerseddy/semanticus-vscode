using System;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// Shared refuse for a session-replacing open while the live model has unsaved edits.
    /// Both doors call this before they swap: MCP tools and the RPC target. LocalEngine.OpenAsync
    /// itself stays the swap primitive tests and smokes call directly.
    /// </summary>
    internal static class UnsavedWorkGuard
    {
        public const string BlockedMessage =
            "The open model has unsaved edits. Save them first, or discard them to continue.";

        /// <summary>Refuse a session-replacing open while the live model has unsaved edits. With
        /// <paramref name="discardUnsaved"/> the open goes on, except when the caller is the assistant: throwing away the
        /// person's unsaved work is held on the same row as any other assistant edit to the open model, and the person is
        /// told plainly that it will be lost. <paramref name="action"/> finishes "Your assistant wants to ...".
        /// An assistant that holds the gated engine (<see cref="AssistantEngine"/>) is asked by that door, on the open call itself,
        /// so it is asked once; through <see cref="RemoteEngine"/> the owner's door asks it, on that connection's origin.</summary>
        public static async Task ThrowIfBlockedAsync(IEngine engine, bool discardUnsaved, string origin = "human", string action = null)
        {
            if (engine == null) return;
            if (discardUnsaved)
            {
                // Not an assistant, a gated or remote engine (their door asks on the open itself): nothing to ask here. A LocalEngine
                // reached directly by an assistant's tool is the one case this guard still asks for.
                if (!string.Equals(origin, "agent", StringComparison.Ordinal) || engine is not LocalEngine local) return;
                var lost = await engine.SessionInfoAsync().ConfigureAwait(false);
                if (lost?.HasUnsavedChanges != true) return;
                var held = local.HoldDiscardUnsaved(origin, action ?? "replace the open model");
                if (held != null) throw new InvalidOperationException(held);
                return;
            }
            var info = await engine.SessionInfoAsync().ConfigureAwait(false);
            if (info?.HasUnsavedChanges == true)
                throw new InvalidOperationException(BlockedMessage);
        }
    }
}
