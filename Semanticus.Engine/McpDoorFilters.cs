using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    /// <summary>
    /// The MCP door's call-tool filters, registered in one place so the stdio server (Program.Mcp) and the in-process
    /// door tests run the exact same chain. The retired-tool answer comes first: a call to a merged or removed tool
    /// name gets a sentence naming the call that replaced it, instead of the SDK's bare "Unknown tool". The SDK runs
    /// call-tool filters before its own unknown-name refusal (ToolMergeTests proves it on SDK 1.4.1). Then
    /// McpErrorBoundary turns a failure into the engine's teaching message, and McpHealthAppender adds the
    /// health-delta block to a mutating tool's result. EngineUpdateGuard sits outside all of them and adds the
    /// restart line to every result while this process is older than the installed files.
    /// </summary>
    public static class McpDoorFilters
    {
        public static void Add(IMcpRequestFilterBuilder f, IEngine engine)
        {
            // Outermost, so every result carries the "Semanticus was updated, restart the assistant" line once this
            // process is older than the files installed beside it, errors and retired names included.
            f.AddCallToolFilter(EngineUpdateGuard.Wrap);
            f.AddCallToolFilter(RetiredTools);
            f.AddCallToolFilter(UnknownArguments);
            f.AddCallToolFilter(McpErrorBoundary.Wrap);
            f.AddCallToolFilter(next => McpHealthAppender.Wrap(engine, next));
        }

        /// <summary>Answer a retired tool name with its replacement; every other call passes through untouched.</summary>
        internal static McpRequestHandler<CallToolRequestParams, CallToolResult> RetiredTools(
            McpRequestHandler<CallToolRequestParams, CallToolResult> next)
            => (request, ct) =>
            {
                var message = OpAliases.RetiredToolMessage(request?.Params?.Name);
                if (message == null) return next(request, ct);
                return new ValueTask<CallToolResult>(new CallToolResult
                {
                    IsError = true,
                    Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
                });
            };

        /// <summary>Refuse, in words, an argument a strict tool does not take. The SDK binds the names it knows and drops
        /// the rest without a word, which is harmless for most tools and wrong for ask_data: its answer hands the
        /// assistant a parts object to pass back, and a dropped "groupBy" once turned "Net Sales by Region" into one
        /// total. Only the tools named here are strict.</summary>
        internal static McpRequestHandler<CallToolRequestParams, CallToolResult> UnknownArguments(
            McpRequestHandler<CallToolRequestParams, CallToolResult> next)
            => (request, ct) =>
            {
                var p = request?.Params;
                if (p?.Name != "ask_data" || p.Arguments == null) return next(request, ct);
                var message = McpTools.UnknownAskDataArgument(p.Arguments.Keys);
                if (message == null) return next(request, ct);
                return new ValueTask<CallToolResult>(new CallToolResult
                {
                    IsError = true,
                    Content = new List<ContentBlock> { new TextContentBlock { Text = message } },
                });
            };
    }
}
