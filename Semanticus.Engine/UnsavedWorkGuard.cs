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

        public static async Task ThrowIfBlockedAsync(IEngine engine, bool discardUnsaved)
        {
            if (discardUnsaved || engine == null) return;
            var info = await engine.SessionInfoAsync().ConfigureAwait(false);
            if (info?.HasUnsavedChanges == true)
                throw new InvalidOperationException(BlockedMessage);
        }
    }
}
