using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    /// <summary>
    /// The MCP door's error contract (docs/harness-engineering.md §1: tool results ARE the agent's feedback).
    /// The MCP SDK's outermost call-tool handler swallows any non-McpException into a bare
    /// "An error occurred invoking 'X'." — which is exactly where the deploy gate's teaching refusal (and every
    /// other engine exception) died on the wire and cost a whole diagnosis session. This filter runs INSIDE that
    /// catch, so it sees the real exception first: it unwraps to the root cause, scrubs secrets (same scrubber
    /// the Fabric lane uses), and returns the message as the tool result. Cancellation and protocol errors are
    /// rethrown — the SDK's semantics for those are correct.
    /// </summary>
    internal static class McpErrorBoundary
    {
        /// <summary>The call-tool filter registered in Program.Mcp via WithRequestFilters.</summary>
        public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
            McpRequestHandler<CallToolRequestParams, CallToolResult> next)
            => (request, ct) => InvokeAsync(request?.Params?.Name, () => next(request, ct), ct);

        /// <summary>The testable core: run the tool, convert a failure into a surfaced teaching result.</summary>
        internal static async ValueTask<CallToolResult> InvokeAsync(
            string toolName, Func<ValueTask<CallToolResult>> next, CancellationToken ct = default)
        {
            try { return await next().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not McpProtocolException)
            {
                var msg = FabricRest.Scrub(Root(ex).Message);
                if (string.IsNullOrWhiteSpace(msg)) msg = ex.GetType().Name;   // never regress to an empty error
                var content = new List<ContentBlock> { new TextContentBlock { Text = $"{toolName ?? "tool"} failed: {msg}" } };
                // A call that committed then threw carries its drained health on the exception's Data (set by
                // McpHealthAppender when it runs inside this boundary). Surface it: the honest story is "the call
                // failed but changes were committed; model health moved" — not pure failure the agent would retry.
                var health = HealthCarriedBy(ex);
                if (!string.IsNullOrEmpty(health)) content.Add(new TextContentBlock { Text = health });
                return new CallToolResult { IsError = true, Content = content };
            }
        }

        // The health block a committed-then-threw call stashed on its exception (checked on the caught instance
        // AND the unwrapped root — reflection/async plumbing may re-wrap between the filters). Null when absent.
        private static string HealthCarriedBy(Exception ex)
        {
            try
            {
                return ex.Data?[McpHealthAppender.HealthDataKey] as string
                    ?? Root(ex).Data?[McpHealthAppender.HealthDataKey] as string;
            }
            catch { return null; }
        }

        /// <summary>Peel wrapper exceptions (reflection/async plumbing) so the ENGINE's message surfaces, not the wrapper's.</summary>
        internal static Exception Root(Exception ex)
        {
            for (var depth = 0; depth < 8; depth++)
            {
                var inner = ex switch
                {
                    AggregateException a when a.InnerExceptions.Count == 1 => a.InnerException,
                    System.Reflection.TargetInvocationException t when t.InnerException != null => t.InnerException,
                    _ => null,
                };
                if (inner == null) return ex;
                ex = inner;
            }
            return ex;
        }
    }
}
