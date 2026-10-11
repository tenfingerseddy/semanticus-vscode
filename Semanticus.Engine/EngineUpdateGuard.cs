using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Semanticus.Engine
{
    /// <summary>Has the build installed beside this process moved away from the build this process is running?
    ///
    /// Kane, 25 September: an in-place reinstall of the same version left older engines running, and the assistant's
    /// connection from that morning was one of them. The assistant cannot see that from the outside; this process can,
    /// by comparing the build it started from (<see cref="BuildIdentity.Current"/>, read first thing in Main) with the
    /// files in its folder now. A folder that is gone counts as updated: VS Code removes an old version's folder once
    /// a newer one is installed.
    ///
    /// Asked on every tool call, so it is cheap: the hash is recomputed only when a Semanticus assembly's size or write
    /// time moved (<see cref="Hashes"/> counts the recomputes, for the test that holds that).</summary>
    public sealed class InstalledBuildWatch
    {
        private readonly string _directory;
        private readonly string _running;
        private readonly object _gate = new object();
        private string _stamp;
        private bool _updated;

        public InstalledBuildWatch(string directory, string running)
        {
            _directory = directory;
            _running = running;
        }

        internal int Hashes { get; private set; }

        public bool Updated()
        {
            // A process that could not read its own build (a development run) cannot judge the files beside it.
            if (string.IsNullOrEmpty(_running)) return false;
            lock (_gate)
            {
                var stamp = Stamp();
                if (stamp == _stamp) return _updated;
                _stamp = stamp;
                Hashes++;
                var installed = BuildIdentity.ForDirectory(_directory);
                _updated = !string.Equals(installed, _running, StringComparison.OrdinalIgnoreCase);
                return _updated;
            }
        }

        /// <summary>Name, size and write time of each Semanticus assembly: what moves when an install replaces them.</summary>
        private string Stamp()
        {
            try
            {
                if (string.IsNullOrEmpty(_directory) || !Directory.Exists(_directory)) return "missing";
                var text = new StringBuilder();
                foreach (var f in new DirectoryInfo(_directory).GetFiles()
                             .Where(f => f.Name.StartsWith("semanticus.", StringComparison.OrdinalIgnoreCase)
                                         && f.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                    text.Append(f.Name).Append('|').Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks).Append('\n');
                return text.ToString();
            }
            catch { return "unreadable"; }
        }
    }

    /// <summary>The assistant door's half of the version guard. Two sentences, and the filter that adds the first one.
    ///
    /// <see cref="AssistantOutdatedText"/> rides on every tool result while this process is older than the files
    /// installed beside it, so the assistant learns on its next call that it should restart, and can tell the person.
    /// <see cref="PersonText"/> replaces a raw "No method by the name 'x' is found." from an older engine this process
    /// joined (<see cref="McpErrorBoundary"/>); it is the same sentence VS Code shows (Semanticus.VSCode/src/versionGuard.ts).</summary>
    public static class EngineUpdateGuard
    {
        public const string PersonText = "Semanticus was updated. Close VS Code and your assistant, then open them again to finish.";

        public const string AssistantOutdatedText =
            "Semanticus was updated after this assistant connected, so this connection is still running the older version. "
            + "Restart the assistant to finish (in Claude Code, reconnect with /mcp). Tell the person the same if they ask why something looks out of date.";

        private static readonly Lazy<InstalledBuildWatch> _installed =
            new Lazy<InstalledBuildWatch>(() => new InstalledBuildWatch(AppContext.BaseDirectory, BuildIdentity.Current));

        /// <summary>This process's own watch: its load folder against the build it started from.</summary>
        public static bool InstalledBuildMoved()
        {
            try { return _installed.Value.Updated(); }
            catch { return false; }   // the notice is best-effort; it must never break a tool result
        }

        /// <summary>Did the other end of an RPC call say it has no such method? The StreamJsonRpc type is the proof; the
        /// sentence covers a hop that re-wrapped it in something else.</summary>
        public static bool IsMethodNotFound(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is StreamJsonRpc.RemoteMethodNotFoundException) return true;
                if (MethodNotFoundSentence.IsMatch(e.Message ?? "")) return true;
            }
            return false;
        }

        private static readonly Regex MethodNotFoundSentence = new Regex(@"No method by the name '[^']*' is found", RegexOptions.CultureInvariant);

        /// <summary>The call-tool filter registered in <see cref="McpDoorFilters"/>, outermost, so it sees the final
        /// result of every call, errors included.</summary>
        public static McpRequestHandler<CallToolRequestParams, CallToolResult> Wrap(
            McpRequestHandler<CallToolRequestParams, CallToolResult> next)
            => (request, ct) => InvokeAsync(InstalledBuildMoved, () => next(request, ct));

        internal static async ValueTask<CallToolResult> InvokeAsync(Func<bool> updated, Func<ValueTask<CallToolResult>> next)
        {
            var result = await next().ConfigureAwait(false);
            if (result == null) return result;
            bool moved;
            try { moved = updated(); } catch { moved = false; }
            if (!moved) return result;
            try
            {
                var block = new TextContentBlock { Text = AssistantOutdatedText };
                if (result.Content == null) result.Content = new List<ContentBlock> { block };
                else
                {
                    try { result.Content.Add(block); }
                    catch (NotSupportedException) { result.Content = new List<ContentBlock>(result.Content) { block }; }
                }
            }
            catch { /* the ride-along must never break the tool result */ }
            return result;
        }
    }
}
