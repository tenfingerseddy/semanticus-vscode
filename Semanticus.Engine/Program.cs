using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Semanticus.Engine
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            switch (mode)
            {
                case "serve": return await Serve(args);
                case "mcp":
                case "--mcp": return await Mcp(args);
                default:
                    Console.Error.WriteLine("Semanticus.Engine");
                    Console.Error.WriteLine("Usage:");
                    Console.Error.WriteLine("  serve --workspace <dir> [--open <modelPathOrFolder>] [--pipe <name>] --ui-challenge-stdin [--license-stdin]");
                    Console.Error.WriteLine("        Owner mode: hosts the model over a named pipe for the VS Code UI.");
                    Console.Error.WriteLine("  mcp   --workspace <dir> [--open <modelPathOrFolder>]");
                    Console.Error.WriteLine("        MCP stdio server for Claude Code. Attaches to a running engine if present,");
                    Console.Error.WriteLine("        otherwise owns the model itself. Register with: claude mcp add.");
                    Console.Error.WriteLine("        A Pro license reaches the owner over stdin (--license-stdin), not as a command-line value.");
                    return mode == "help" ? 0 : 1;
            }
        }

        private static async Task<int> Serve(string[] args)
        {
            var workspace = Path.GetFullPath(GetOpt(args, "--workspace") ?? Directory.GetCurrentDirectory());
            var open = GetOpt(args, "--open");
            var pipe = GetOpt(args, "--pipe") ?? EngineBroker.PipeNameFor(workspace);
            var uiChallenge = Array.IndexOf(args, "--ui-challenge-stdin") >= 0 ? Console.In.ReadLine() : null;
            if (string.IsNullOrWhiteSpace(uiChallenge))
            {
                Console.Error.WriteLine("[engine] --ui-challenge-stdin is required in owner/UI mode; refusing to expose an unauthenticated human RPC door.");
                return 2;
            }
            // An out-of-range challenge is refused HERE, not left to RpcServer's constructor: an ArgumentException
            // escaping Main is an unhandled exception, and the runtime answers that with an abort (exit 134, core
            // dump) instead of a message. Same plain refusal and same exit code as the missing-value case above.
            if (!RpcHandshake.IsValidChallenge(uiChallenge))
            {
                Console.Error.WriteLine("[engine] --ui-challenge-stdin must be 32 to 128 characters; refusing to start the human RPC door with a weak challenge.");
                return 2;
            }

            var existing = EngineBroker.ReadInfo(workspace);
            if (EngineBroker.IsAlive(existing))
            {
                if (EngineBroker.HasExecutableProvenance(existing) && !EngineBroker.ExecutableMatches(existing))
                {
                    Console.Error.WriteLine("[engine] FATAL: a different Semanticus engine is already running for this workspace. Restart the engine, then reconnect the AI Assistant.");
                    return 1;
                }
                if (!EngineBroker.HasExecutableProvenance(existing))
                    Console.Error.WriteLine("[engine] WARNING: the running engine predates executable provenance. Leaving it in place; restart the engine to record its source.");
                Console.Error.WriteLine($"[engine] already running for {workspace} (pid {existing.Pid}, pipe {existing.PipeName}).");
                return 0;
            }

            var lockStream = EngineBroker.TryAcquireOwnerLock(workspace);
            if (lockStream == null)
            {
                Console.Error.WriteLine("[engine] another process owns this workspace; exiting.");
                return 0;
            }

            using (lockStream)
            {
                var sessions = new SessionManager();
                // Entitlement is delivered via stdin (--license-stdin) → env → the user license file. The OWNER
                // engine's entitlement is authoritative: an attaching MCP proxy inherits it over RPC. Never put the
                // token on argv: /proc/<pid>/cmdline is world-readable.
                var engine = new LocalEngine(sessions, Entitlement.LicenseEntitlement.FromEnvironmentOrToken(Entitlement.LicenseTokenDelivery.FromArgs(args, Console.In)), workspace);
                // Learning Loop L0: the owner host tees the dual-drive stream to .semanticus/experience.jsonl
                // (beside the model; live/unsaved sessions fall back to the workspace's .semanticus/).
                using var experience = new ExperienceTee(sessions, workspace);
                // Number time-machine (feature #3): ambient vital-signs capture is HOST-ATTACHED like the tee
                // (owner only — tests/attached proxies never write sidecar files unless they opt in).
                engine.AmbientVitalsEnabled = true;

                if (!string.IsNullOrEmpty(open))
                {
                    var r = await engine.OpenAsync(open);
                    Console.Error.WriteLine($"[engine] opened '{r.ModelName}': {r.Tables} tables, {r.Measures} measures.");
                }

                using var server = new RpcServer(sessions, engine, pipe, uiChallenge);
                EngineBroker.WriteInfo(workspace, NewInfo(pipe, workspace));
                Console.Error.WriteLine($"[engine] listening on \\\\.\\pipe\\{pipe} (workspace {workspace}).");

                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
                try { await server.RunAsync(cts.Token); }
                catch (OperationCanceledException) { }

                sessions.Dispose();
            }
            return 0;
        }

        /// <summary>
        /// MCP stdio server. CRITICAL: stdout carries the MCP protocol, so ALL human-readable output
        /// must go to stderr (logging is configured to stderr below).
        /// </summary>
        private static async Task<int> Mcp(string[] args)
        {
            var workspace = Path.GetFullPath(GetOpt(args, "--workspace") ?? Directory.GetCurrentDirectory());
            var open = GetOpt(args, "--open");

            IEngine engine = null;
            // Held for the process lifetime when WE own the workspace (kept rooted by GC.KeepAlive below, never
            // disposed here — releasing it would surrender ownership). null when we attached as a proxy instead.
            FileStream ownerLock = null;
            // OWNER ELECTION, under one overall deadline. Attach to a live owner when one exists (so we edit the
            // SAME live model the UI sees; the workspace lets the proxy RE-attach when the owner restarts); else
            // try to take the owner lock ourselves. CRITICAL: a null lock is NOT a licence to proceed lockless —
            // that would make TWO independent owners for one workspace and shatter the single-writer invariant.
            // And EVERY probe here is inherently racy (the owner can die between the aliveness check and the pipe
            // connect), so any step failing re-enters the election rather than exiting — until the deadline, then
            // fail LOUD. The deadline is checked at loop entry, immediately before the (slow) pipe connect, and
            // immediately before the owner-lock acquisition (file-system I/O can stall too); each backoff delay
            // is capped to the time remaining, so a 600ms sleep started just under the deadline can't overrun it.
            // The connect itself is capped to 1s (and to time remaining) so a miss against a missing socket cannot
            // consume the whole election the way ConnectAsync's old 5s default did (D-118).
            var election = System.Diagnostics.Stopwatch.StartNew();
            var deadline = TimeSpan.FromSeconds(5);
            var sawAliveOwner = false;
            int FailElection()
            {
                Console.Error.WriteLine(EngineBroker.McpAttachFailureMessage(deadline, sawAliveOwner));
                return 1;   // fail LOUD — never run as a second, lockless owner
            }
            for (var attempt = 0; ; attempt++)
            {
                if (election.Elapsed >= deadline) return FailElection();
                var info = EngineBroker.ReadInfo(workspace);
                if (EngineBroker.IsAlive(info))
                {
                    sawAliveOwner = true;
                    if (EngineBroker.HasExecutableProvenance(info) && !EngineBroker.ExecutableMatches(info))
                    {
                        Console.Error.WriteLine("[mcp] FATAL: the running Semanticus engine belongs to a different installed or F5 build. Restart the engine, then reconnect the AI Assistant.");
                        return 1;
                    }
                    if (!EngineBroker.HasExecutableProvenance(info))
                        Console.Error.WriteLine("[mcp] WARNING: the running engine predates executable provenance. Joining for this session; restart the engine to record its source.");
                    // Re-check right before the connect: it is the one slow step, and entering it at the deadline
                    // would stretch the election well past what the message promises.
                    if (election.Elapsed >= deadline) return FailElection();
                    try
                    {
                        // Cap the connect so one miss against a missing socket cannot consume the whole election
                        // (D-118: the advertised short name often is not where this process looks).
                        var remainMs = (int)Math.Max(50, (deadline - election.Elapsed).TotalMilliseconds);
                        var connectMs = Math.Min(1000, remainMs);
                        engine = await RemoteEngine.ConnectAsync(info.PipeName, workspace, timeoutMs: connectMs, requireMatchingExecutable: true, pipePath: info.PipePath);
                        Console.Error.WriteLine($"[mcp] joined the running session (pid {info.Pid}, pipe {info.PipeName}).");
                        break;
                    }
                    catch (Exception ex)
                    {
                        // The owner died (or its pipe vanished) between the aliveness probe and the connect — the
                        // TOCTOU is unavoidable, exiting on it is not. Re-enter; we may now win the lock ourselves.
                        Console.Error.WriteLine($"[mcp] session pid {info.Pid} looked alive but could not be joined ({ex.Message}): trying again.");
                    }
                }
                else
                {
                    // The lock acquisition is file-system I/O — a stalled filesystem must not stretch the
                    // election past what the message promises, so re-check the deadline before it too.
                    if (election.Elapsed >= deadline) return FailElection();
                    ownerLock = EngineBroker.TryAcquireOwnerLock(workspace);
                    if (ownerLock != null) break;   // we own it — set up the owned engine below
                    // Lock held but nothing alive published yet: the winner is still starting up, or a dead
                    // process's OS lock hasn't been reclaimed yet. Back off and retry within the deadline.
                }
                // Cap the backoff to the time remaining: a full 600ms sleep started just under the deadline would
                // otherwise cross it silently; capped, we wake exactly at the deadline and fail loud at loop entry.
                var remaining = deadline - election.Elapsed;
                if (remaining <= TimeSpan.Zero) return FailElection();
                var backoff = TimeSpan.FromMilliseconds(150 * Math.Min(attempt + 1, 4));
                await Task.Delay(backoff < remaining ? backoff : remaining);
            }

            if (engine == null)
            {
                var sessions = new SessionManager();
                // Claude became the owner (no VS Code engine running): read the license from env or the user license
                // file. An attaching MCP process inherits the owner's entitlement over the pipe, so .mcp.json must
                // not carry the token on argv.
                var local = new LocalEngine(sessions, Entitlement.LicenseEntitlement.FromEnvironmentOrToken(Entitlement.LicenseTokenDelivery.FromArgs(args, null)), workspace);
                // Learning Loop L0 (owner only — an attached proxy must not double-write the owner's log).
                // Process-lifetime: the bus holds the subscription; the host never detaches it.
                _ = new ExperienceTee(sessions, workspace);
                // Number time-machine (feature #3): ambient vital-signs capture, owner-attached like the tee.
                local.AmbientVitalsEnabled = true;
                var pipe = EngineBroker.PipeNameFor(workspace);
                // An MCP-owned server has no human proof to trust, so it explicitly rejects the UI handshake.
                // VS Code treats that authenticated rejection as the ownership-reclaim signal and restarts this
                // process as the human owner; a subsequent MCP launch then attaches through RemoteEngine.
                var server = new RpcServer(sessions, local, pipe);
                _ = server.RunAsync(CancellationToken.None);
                EngineBroker.WriteInfo(workspace, NewInfo(pipe, workspace));   // we hold the lock ⇒ publish unconditionally
                if (!string.IsNullOrEmpty(open))
                {
                    var r = await local.OpenAsync(open);
                    Console.Error.WriteLine($"[mcp] opened '{r.ModelName}': {r.Tables} tables, {r.Measures} measures.");
                }
                engine = local;
                Console.Error.WriteLine($"[mcp] no running engine; became owner on pipe {pipe}.");
            }

            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            builder.Services.AddSingleton<IEngine>(engine);
            builder.Services
                // §6 orientation (docs/harness-engineering.md): server instructions are read once at
                // initialization as the agent's system context. FIRST line points at the blessed primer so a
                // fresh/compacted agent orients unprompted. Kept short — it must not duplicate tool descriptions.
                .AddMcpServer(o => o.ServerInstructions =
                    "Call get_model_summary first for orientation: it is the token-budgeted session-start primer " +
                    "(connection, tier, model shape + grade, in-flight work, last-session tail, suggested next actions) " +
                    "and names the drill-down op for each section. Semanticus operates one LIVE semantic model through " +
                    "this MCP surface and the VS Code UI. Every edit is undoable. The VS Code view updates at once; " +
                    "the AI Assistant sees the change on its next call.")
                .WithStdioServerTransport()
                .WithToolsFromAssembly()
                // A failed tool call must carry the engine's TEACHING message (scrubbed), not the SDK's opaque
                // "An error occurred invoking 'X'." — see McpErrorBoundary. Its success-path sibling appends the
                // health-delta block to a mutating tool's result (Pro, threshold-suppressed) — see McpHealthAppender.
                .WithRequestFilters(f =>
                {
                    f.AddCallToolFilter(McpErrorBoundary.Wrap);
                    f.AddCallToolFilter(next => McpHealthAppender.Wrap(engine, next));
                });

            Console.Error.WriteLine("[mcp] serving Semanticus tools over stdio.");
            await builder.Build().RunAsync();
            GC.KeepAlive(ownerLock);   // keep the owner lock held across the whole server lifetime (assigned, not read otherwise)
            return 0;
        }

        private static EngineInfo NewInfo(string pipe, string workspace) => new EngineInfo
        {
            PipeName = pipe,
            Pid = Environment.ProcessId,
            StartedUtc = DateTime.UtcNow.ToString("o"),
            Workspace = workspace
        };

        private static string GetOpt(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
        }
    }
}
