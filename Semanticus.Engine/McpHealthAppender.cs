using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    /// <summary>
    /// The SUCCESS-path sibling of <see cref="McpErrorBoundary"/> (feature #4, "health delta everywhere"):
    /// after a mutating tool call commits, append ONE terse block to the tool's own result —
    /// <c>health: {"grade":"B-&gt;C","new":["SYN-MISSING"],"findings":2,"impact":3}</c> — the ground-truth
    /// feedback loop (docs/harness-engineering.md §3) made ambient: the agent self-corrects in the same turn.
    ///
    /// Correlation is BY TOOL-CALL IDENTITY: this filter mints an id per call, flows it ambiently
    /// (<see cref="HealthCorrelation"/>) into every commit the call performs, and drains exactly that id from
    /// the engine's <see cref="AgentHealthMailbox"/> — so two parallel mutating calls each get their own delta
    /// and a model swap mid-call can't cross-attribute. Read-only calls stash nothing; the free tier and
    /// below-threshold edits stash nothing — so the common case costs zero tokens (P-Efficiency). Works
    /// identically when the MCP process OWNS the model (LocalEngine) or ATTACHES to a running engine
    /// (RemoteEngine pulls the owner's mailbox over the pipe; those commits ride the unscoped slot).
    ///
    /// A call that COMMITS then THROWS must not read as pure failure — the human chip already showed the delta
    /// at commit time, so the doors would disagree. The drained health rides the exception's Data (the same
    /// instance rethrows, so the tool's teaching error text is untouched) and <see cref="McpErrorBoundary"/>
    /// surfaces it on the error result; when this filter sits OUTSIDE the boundary it appends to the IsError
    /// result directly. Either order yields one honest block. The ride-along must never break a tool result,
    /// hence the blanket catches.
    /// </summary>
    internal static class McpHealthAppender
    {
        /// <summary>Exception.Data key carrying the display text of a committed-then-threw call's health block
        /// (set here, surfaced by <see cref="McpErrorBoundary"/>).</summary>
        internal const string HealthDataKey = "semanticus.healthDelta";

        private static readonly JsonSerializerOptions Terse = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,   // omit-when-unchanged, field by field
        };

        /// <summary>The call-tool filter registered in Program.Mcp via WithRequestFilters (after the error
        /// boundary; order-agnostic — it tolerates both a thrown exception and an IsError result from next).</summary>
        public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
            IEngine engine, McpRequestHandler<CallToolRequestParams, CallToolResult> next)
            => (request, ct) => InvokeAsync(engine, () => next(request, ct));

        /// <summary>The testable core: mint the call id, run the tool under it, then drain-and-append this
        /// call's health block (success or failure — failure carries it on the exception's Data).</summary>
        internal static async ValueTask<CallToolResult> InvokeAsync(IEngine engine, Func<ValueTask<CallToolResult>> next)
        {
            var callId = Guid.NewGuid().ToString("N");
            CallToolResult result;
            using (HealthCorrelation.Begin(callId))
            {
                try { result = await next().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    var health = await DrainAsync(engine, callId).ConfigureAwait(false);
                    if (health != null)
                    {
                        // Same instance rethrows (type + teaching message intact); the boundary reads Data.
                        try { ex.Data[HealthDataKey] = FailureText(health); } catch { /* Data may be read-only */ }
                    }
                    throw;
                }
            }
            var delta = await DrainAsync(engine, callId).ConfigureAwait(false);
            if (delta != null && result != null)
                Append(result, result.IsError == true ? FailureText(delta) : SuccessText(delta));
            return result;
        }

        private static string SuccessText(HealthDelta h) => "health: " + JsonSerializer.Serialize(h, Terse);

        // The honest commit-then-throw message: the call FAILED, but these changes were already committed and
        // model health moved — pure-failure retry logic would double-apply. Keep it terse and actionable.
        private static string FailureText(HealthDelta h) =>
            "health: " + JsonSerializer.Serialize(h, Terse)
            + " — the call failed but these changes were already committed (model health moved as shown). "
            + "Verify the model state before retrying; undo_change can revert the committed part.";

        /// <summary>Append one text block, defensively: Content may be null or a non-resizable IList (a fixed
        /// array wrapper would throw NotSupportedException straight into the silent catch) — copy to a fresh
        /// List when it isn't appendable.</summary>
        private static void Append(CallToolResult result, string text)
        {
            try
            {
                var block = new TextContentBlock { Text = text };
                var content = result.Content;
                if (content == null)
                {
                    result.Content = new List<ContentBlock> { block };
                    return;
                }
                try { content.Add(block); }
                catch (NotSupportedException)
                {
                    result.Content = new List<ContentBlock>(content) { block };
                }
            }
            catch { /* the ride-along must never break the tool result */ }
        }

        private static async ValueTask<HealthDelta> DrainAsync(IEngine engine, string callId)
        {
            try { return await engine.PullAgentHealthAsync(callId).ConfigureAwait(false); }
            catch { return null; }   // e.g. the owner pipe dropped mid-call — health is best-effort
        }
    }
}
