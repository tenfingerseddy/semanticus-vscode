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
                var msg = PlainFrameworkError(FabricRest.Scrub(Root(ex).Message));
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

        /// <summary>Turn .NET binder jargon ("arguments dictionary is missing... (Parameter 'arguments')") and
        /// System.Text.Json type-mismatch text into a short instruction. Other messages pass through after the
        /// Parameter suffix is dropped.</summary>
        internal static string PlainFrameworkError(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return msg;

            // A wrong-shaped argument (JOURNEY-09). System.Text.Json says which .NET type it wanted and nothing
            // else: no argument name, and "$ | LineNumber: 0 | BytePositionInLine: 4" coordinates a person cannot
            // use. Measured on the wire 2026-09-11 (submit_workflow_step with callGate:true, instantiate_workflow_
            // template with valuesJson:{}, optimize_measure with verifyGroupBy:"x"); the binder exception carries no
            // parameter name, so the plain form names the SHAPE and points at the description instead of guessing
            // an argument. Never claim which argument it was — we cannot know it from here.
            const string wrongType = "The JSON value could not be converted to ";
            var wrong = msg.IndexOf(wrongType, StringComparison.Ordinal);
            if (wrong >= 0)
            {
                var rest = msg.Substring(wrong + wrongType.Length);
                var cut = rest.IndexOf(". Path:", StringComparison.Ordinal);   // "System.String. Path: $ | LineNumber: 0"
                var typeName = (cut >= 0 ? rest.Substring(0, cut) : rest).Trim();
                return "One of the arguments had the wrong kind of value. This action wants "
                    + PlainArgumentShape(typeName)
                    + ", but it was sent something else. Check each argument against the action's description, then call again.";
            }

            const string missing = "missing a value for the required parameter '";
            var start = msg.IndexOf(missing, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                var nameStart = start + missing.Length;
                var nameEnd = msg.IndexOf("'", nameStart, StringComparison.Ordinal);
                var name = nameEnd > nameStart ? msg.Substring(nameStart, nameEnd - nameStart) : "this argument";
                // The binder only reaches this branch for an argument the tool's own schema marks required, so it
                // has no default to fall back on. Saying "or omit it to use the default" here told the caller to do
                // the one thing the schema forbids (D-206/D-207, UX-03). Name the argument and stop there.
                return "This action needs '" + name + "'. Check the description and pass that argument with the call.";
            }

            var param = msg.LastIndexOf(" (Parameter '", StringComparison.Ordinal);
            return param >= 0 ? msg.Substring(0, param) : msg;
        }

        /// <summary>The .NET type a failed argument wanted, in the words a person reads. Unknown shapes stay vague
        /// on purpose: a wrong guess here would be a second untrue refusal on top of the first.</summary>
        private static string PlainArgumentShape(string dotnetType)
        {
            if (string.IsNullOrEmpty(dotnetType)) return "a different kind of value";
            if (dotnetType.EndsWith("[]", StringComparison.Ordinal))
                return "a list of " + PlainArgumentShape(dotnetType.Substring(0, dotnetType.Length - 2)) + " values";
            switch (dotnetType)
            {
                case "System.String": return "text";
                case "System.Boolean": return "true or false";
                case "System.Int16":
                case "System.Int32":
                case "System.Int64":
                case "System.Decimal":
                case "System.Double":
                case "System.Single": return "a number";
                default: return "a different kind of value";
            }
        }
    }
}
