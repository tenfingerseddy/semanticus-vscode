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
            // Fix which build this process IS before anything else runs. BuildIdentity.Current reads the files beside
            // the engine lazily, and read late (after an in-place reinstall under a running engine) it would report the
            // NEW files as this process, so every build comparison would pass on old code (Kane, 25 Sep).
            _ = BuildIdentity.Current;
            var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
            // The per-user home is chosen before anything can read a store, and it travels to every engine this
            // process starts through the environment (see SemanticusHome.ApplyCommandLine).
            try { SemanticusHome.ApplyCommandLine(args); }
            catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 1; }
            // No per-user folder (HOME names a folder that does not exist) means no safe place for any store. Refuse
            // now, before one of them resolves against the working folder instead.
            if (mode is "serve" or "mcp" or "--mcp")
            {
                try { SemanticusHome.EnsureResolvable(); }
                catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 1; }
            }
            switch (mode)
            {
                case "serve": return await Serve(args);
                case "mcp":
                case "--mcp": return await Mcp(args);
                default:
                    Console.Error.WriteLine("Semanticus.Engine");
                    Console.Error.WriteLine("Usage:");
                    Console.Error.WriteLine("  serve --workspace <dir> [--open <modelPathOrFolder>] [--pipe <name>] [--parent-pid <pid>] [--state-dir <dir>] [--interactive-signin] --ui-challenge-stdin [--license-stdin]");
                    Console.Error.WriteLine("        Owner mode: hosts the model over a named pipe for the VS Code UI.");
                    Console.Error.WriteLine("        --interactive-signin lets a person's browser sign-in open the system browser. VS Code passes it; without it no browser is ever opened.");
                    Console.Error.WriteLine("        --parent-pid names the process this engine belongs to; the engine exits when that process does.");
                    Console.Error.WriteLine("  mcp   --workspace <dir> [--open <modelPathOrFolder>] [--state-dir <dir>]");
                    Console.Error.WriteLine("        MCP stdio server for Claude Code. Attaches to a running engine if present,");
                    Console.Error.WriteLine("        otherwise owns the model itself. Register with: claude mcp add.");
                    Console.Error.WriteLine("        A Pro license reaches the owner over stdin (--license-stdin), not as a command-line value.");
                    Console.Error.WriteLine("  --state-dir <dir>  Keep per-user state (saved connections, history, policy, knowledge, licence) in <dir>");
                    Console.Error.WriteLine("        instead of ~/.semanticus. The SEMANTICUS_HOME environment variable does the same.");
                    Console.Error.WriteLine("        --state-dir=<dir> works too. Saved accounts and sign-ins move with it.");
                    Console.Error.WriteLine("        An engine given a state folder never joins an owner that keeps its state elsewhere, and one given none");
                    Console.Error.WriteLine("        never joins an owner another run started with a folder of its own: it stops with a message.");
                    return mode == "help" ? 0 : 1;
            }
        }

        private static async Task<int> Serve(string[] args)
        {
            // Only an owner told so may open the system browser for a person's sign-in (InteractiveSignIn). The MCP door
            // never is, and an owner the assistant starts is never given the option.
            InteractiveSignIn.ApplyCommandLine(args);
            var workspace = Path.GetFullPath(GetOpt(args, "--workspace") ?? Directory.GetCurrentDirectory());
            var open = GetOpt(args, "--open");
            var pipe = GetOpt(args, "--pipe") ?? EngineBroker.PipeNameFor(workspace);

            // LIFETIME. An owner engine belongs to the editor that started it, and until now nothing told it
            // when that editor went away: closing VS Code left the process reparented to init and still serving
            // its pipe for days (measured on Kane's machine 2026-09-21, nine live engines across two extension
            // versions). The watch starts BEFORE the challenge read below, because that read blocks on stdin
            // and a parent can die while we sit in it. Started here, cancellation reaches the whole of Serve.
            // NOT stdin-close: the extension calls stdin.end() the instant it has written the challenge and the
            // licence, so stdin is at EOF within milliseconds of every healthy start.
            // A dead parent starts a grace rather than an exit, so a reloading window can adopt this engine and
            // keep its unsaved session. See ParentLifetime for why.
            using var lifetime = new CancellationTokenSource();
            var watchTarget = ParentWatchdog.ResolveWatchTarget(args);
            // Every owner keeps unsaved work when the editor closes. The next window adopts that same session,
            // so the extension does not need to open a dirty placeholder tab while the assistant works.
            // Filled in once the server exists; before that there is nothing to lose.
            var assistantOwner = Array.IndexOf(args, OwnerSpawner.AssistantOwnerFlag) >= 0;
            Func<bool> hold = null;
            Action keepBeforeLeaving = null;   // filled in once the sessions exist: unsaved live work is written down before the engine goes
            using var parent = new ParentLifetime(watchTarget, () => GraceExpired(workspace, lifetime, keepBeforeLeaving),
                // An adoption changes who we die with, so the published record has to say so. A reader that
                // still saw the OLD parent would judge this engine by a window that has already gone.
                onAdopted: adoptedPid => EngineBroker.UpdateParentPid(workspace, Environment.ProcessId, adoptedPid),
                holdOpen: () => hold?.Invoke() ?? false);
            Console.Error.WriteLine(watchTarget.HasValue
                ? $"[engine] tied to parent process {watchTarget.Value}; it exits {parent.Grace.TotalSeconds:0}s after that process goes, unless a returning window adopts it."
                : "[engine] no parent process to watch; this engine runs until it is stopped.");

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
                    // Same event as the extension's refusal, same advice. Not "restart": that would stop the
                    // engine the other window is using and take its unsaved work with it.
                    Console.Error.WriteLine("[engine] FATAL: another Semanticus engine, from a different version, is already serving this folder. Close the other VS Code window, then open this folder again.");
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
                var engine = new LocalEngine(sessions, Entitlement.LicenseEntitlement.FromEnvironmentOrToken(Entitlement.LicenseTokenDelivery.FromArgs(args, Console.In)), workspace, WorkspaceIsFolder(args));
                // Learning Loop L0: the owner host tees the dual-drive stream to .semanticus/experience.jsonl
                // (beside the model; live/unsaved sessions fall back to the workspace's .semanticus/).
                using var experience = new ExperienceTee(sessions, WorkspaceIsFolder(args) ? workspace : null);   // never the start folder (N1)
                // Number time-machine (feature #3): ambient vital-signs capture is HOST-ATTACHED like the tee
                // (owner only — tests/attached proxies never write sidecar files unless they opt in).
                engine.AmbientVitalsEnabled = true;

                if (!string.IsNullOrEmpty(open))
                {
                    var r = await engine.OpenAsync(open);
                    Console.Error.WriteLine($"[engine] opened '{r.ModelName}': {r.Tables} tables, {r.Measures} measures.");
                }

                // The lifetime goes to the server so an authenticated HUMAN connection can call adoptParent.
                // Human-role only: an agent must not be able to retarget which process this engine dies with.
                // A window replacing this engine with its own build asks it to leave, and it agrees only while clean
                // (F-244). Leaving takes the published record with it first, so no window attaches to a pipe that is
                // about to go, then exits once the answer has had time to reach the window.
                using var cleanStop = new CleanStop(sessions, () => LeaveForReplacement(workspace));
                using var server = new RpcServer(sessions, engine, pipe, uiChallenge, parent, cleanStop);
                // ASSISTANT PRESENCE, door one: an attached assistant is an agent-role client on this pipe.
                // Without this the engine answers "nobody is there" and a job that needs words stops at a stub.
                engine.ObserveAgentClients(() => server.AgentClientCount);
                hold = () => KeepOwnerAlive(assistantOwner, server.ClientCount, sessions.Current);
                keepBeforeLeaving = () => KeepLiveWork(sessions.Current);
                var info = NewInfo(pipe, workspace, parent.WatchedPid);
                if (assistantOwner) info.StartedBy = EngineInfo.StartedByAssistant;
                EngineBroker.WriteInfo(workspace, info);
                Console.Error.WriteLine($"[engine] listening on \\\\.\\pipe\\{pipe} (workspace {workspace}).");

                Console.CancelKeyPress += (s, e) => { e.Cancel = true; lifetime.Cancel(); };
                try { await server.RunAsync(lifetime.Token); }
                catch (OperationCanceledException) { }

                sessions.Dispose();
            }
            // A clean exit takes its published record with it, so the next window never attaches to a pipe
            // that stopped listening a moment ago. Only ours: a record naming another pid is another owner's.
            EngineBroker.ClearInfo(workspace, Environment.ProcessId);
            return 0;
        }

        // File edits survive in recovery. Live and never-saved models still need their owner in memory.
        // A copy kept of the work holds every change so far (KeptCopyPath is null once a newer change is in no copy), so a close loses nothing: the work is
        // on disk and the service has what was published. Without this a person who closed VS Code after the assistant kept a copy waited the full ten minutes.
        internal static bool KeepOwnerAlive(bool assistantOwner, int clientCount, Session session)
            => (assistantOwner && clientCount > 0) || (session != null && session.HasUnsavedEdits && session.KeptCopyPath == null
                && (string.IsNullOrEmpty(session.SourcePath) || session.LiveOrigin != null || session.SnapshotOf != null));

        /// <summary>A window is replacing this engine and it was clean (F-244). The dispatcher is held by then, so
        /// the exit is the only thing left to do; it waits a moment so the clean answer reaches the window first.</summary>
        private static void LeaveForReplacement(string workspace)
        {
            Console.Error.WriteLine("[engine] a window is replacing this engine with its own build, and it has no unsaved changes; leaving.");
            try { EngineBroker.ClearInfo(workspace, Environment.ProcessId); } catch { }
            new Thread(() =>
            {
                Thread.Sleep(500);
                Environment.Exit(0);
            })
            { IsBackground = true, Name = "semanticus-replaced-exit" }.Start();
        }

        /// <summary>Grace given to the graceful shutdown before the watchdog leaves anyway. Long enough for the
        /// accept loop to unwind and the session to dispose, short enough that "closed VS Code" and "engine
        /// gone" read as one event.</summary>
        private const int ParentGoneExitGraceMs = 2000;

        /// <summary>The parent died and nobody came back inside the grace. Only NOW is the record cleared:
        /// during the grace it stays published, because a returning window has to find this pipe to adopt it.</summary>
        /// <summary>The longest the leaving engine waits for the kept copy of a live model. A big model on a busy dispatcher can take well over
        /// the five seconds this used to allow, and an exit before the write reached its move lost the unpublished edits (Codex on pull request 402).
        /// The copy is written to a temporary file and moved over the old one in one step, so giving up at the cap leaves the earlier copy whole.</summary>
        internal static TimeSpan LiveKeepCap = TimeSpan.FromSeconds(60);

        /// <summary>Writes the unsaved edits of a model opened live to its kept copy. It returns when the copy is in place, or when the cap passes:
        /// a stuck dispatcher must not keep the engine alive for ever, but a slow write is waited for.</summary>
        internal static void KeepLiveWork(Session session)
        {
            try
            {
                // Nothing to keep means nothing to wait for: a close behind a busy dispatcher (a long query) must not hold the engine for the cap.
                if (session == null || !session.HasLiveWorkToKeep) return;
                if (!session.RunAsync(() => session.FlushLiveRecovery()).Wait(LiveKeepCap))
                    Console.Error.WriteLine("[engine] the unsaved live work was still being kept when the wait ended; leaving anyway. The earlier kept copy, if any, is untouched.");
            }
            catch (Exception ex) { Console.Error.WriteLine("[engine] could not keep the unsaved live work: " + ex.GetType().Name); }
        }

        private static void GraceExpired(string workspace, CancellationTokenSource lifetime, Action keepBeforeLeaving = null)
        {
            Console.Error.WriteLine("[engine] no window came back within the grace; shutting this engine down.");
            try { keepBeforeLeaving?.Invoke(); } catch { /* leaving matters more */ }
            try { EngineBroker.ClearInfo(workspace, Environment.ProcessId); } catch { }
            try { lifetime.Cancel(); } catch { }
            // Cancelling unwinds the accept loop, but a start still blocked reading the challenge from stdin
            // cannot be interrupted at all, and a shutdown that stalls IS the orphan this exists to prevent.
            // Background so a graceful exit still wins the race; foreground would hold the process open.
            new Thread(() =>
            {
                Thread.Sleep(ParentGoneExitGraceMs);
                Environment.Exit(0);
            })
            { IsBackground = true, Name = "semanticus-parent-watchdog-exit" }.Start();
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
            // Only the legacy argv shape can carry a token here; an owner this process starts gets it on stdin.
            var licenseToken = Entitlement.LicenseTokenDelivery.FromArgs(args, null);
            // TAKING OWNERSHIP means starting a separate owner process and joining it, never hosting the model in
            // this process (see OwnerSpawner for why). The same starter is handed to the proxy, so an owner that
            // leaves mid-session (VS Code closed) is replaced on the next call instead of failing every call.
            Func<Task<bool>> startOwner = () => Task.FromResult(OwnerSpawner.TryStart(workspace, null, licenseToken));
            var startedOwner = false;
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
            // When no owner is alive this process starts one and joins it like any other client. Only when that
            // start fails outright does it fall back to hosting the owner in-process, as earlier builds always did.
            var election = System.Diagnostics.Stopwatch.StartNew();
            var deadline = TimeSpan.FromSeconds(5);
            var sawAliveOwner = false;
            var waitingOnOtherBuild = false;
            int FailElection()
            {
                Console.Error.WriteLine(waitingOnOtherBuild
                    ? EngineBroker.McpDifferentBuildMessage
                    : EngineBroker.McpAttachFailureMessage(deadline, sawAliveOwner));
                return 1;   // fail LOUD — never run as a second, lockless owner
            }
            for (var attempt = 0; ; attempt++)
            {
                if (election.Elapsed >= deadline) return FailElection();
                var info = EngineBroker.ReadInfo(workspace);
                var alive = EngineBroker.IsAlive(info);
                var verdict = alive ? EngineBroker.McpJoinVerdict(info) : McpJoin.Join;
                waitingOnOtherBuild = alive && verdict == McpJoin.DifferentBuild;
                if (waitingOnOtherBuild)
                {
                    // F-241: the same path, another build (an in-place reinstall). Do not join it, and never stop it
                    // from here: it may hold someone's unsaved work, and VS Code's Save offer owns that. Wait for it
                    // to go (VS Code replaces a clean one; an unwatched one leaves after its grace), then start and
                    // join this build's own owner on a later pass. The lock makes starting one over it pointless.
                    if (!sawAliveOwner)
                        Console.Error.WriteLine($"[mcp] Semanticus for this folder is a different build (pid {info.Pid}). Not joining it and not stopping it; waiting for it to go.");
                    sawAliveOwner = true;
                    if (deadline < OwnedStartDeadline) deadline = OwnedStartDeadline;
                }
                else if (alive)
                {
                    sawAliveOwner = true;
                    if (verdict == McpJoin.DifferentInstall)
                    {
                        Console.Error.WriteLine(EngineBroker.McpDifferentBuildMessage);
                        return 1;
                    }
                    // This run never joins an owner that keeps state in a home it did not choose: given its own state
                    // folder, any other one; given none (the person's own assistant), an isolated run's. It cannot start
                    // its own owner either: the folder has one owner, and that one is alive.
                    if (EngineBroker.StateFolderMismatch(info))
                    {
                        Console.Error.WriteLine(EngineBroker.StateFolderMismatchMessage(info));
                        return 1;
                    }
                    if (verdict == McpJoin.Legacy)
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
                        engine = await RemoteEngine.ConnectAsync(info.PipeName, workspace, timeoutMs: connectMs, requireMatchingExecutable: true, pipePath: info.PipePath, startOwner: startOwner);
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
                else if (!startedOwner)
                {
                    // Start one owner per election. If another process wins the workspace first, ours exits on
                    // the lock and we join the winner on a later pass: either way the next live record is joined.
                    startedOwner = OwnerSpawner.TryStart(workspace, open, licenseToken);
                    if (startedOwner)
                    {
                        // A cold owner start (runtime load, antivirus on first run) needs more than the join budget,
                        // counted from now: after waiting out another build the budget may already be mostly spent.
                        var need = election.Elapsed + OwnedStartDeadline;
                        if (deadline < need) deadline = need;
                    }
                    else
                    {
                        // Could not start a process at all. Host the owner here rather than fail: the lock keeps it
                        // single-writer, and this is exactly what every build before this one did.
                        if (election.Elapsed >= deadline) return FailElection();
                        ownerLock = EngineBroker.TryAcquireOwnerLock(workspace);
                        if (ownerLock != null) break;   // we own it: set up the owned engine below
                    }
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
                var local = new LocalEngine(sessions, Entitlement.LicenseEntitlement.FromEnvironmentOrToken(Entitlement.LicenseTokenDelivery.FromArgs(args, null)), workspace, WorkspaceIsFolder(args));
                // Learning Loop L0 (owner only — an attached proxy must not double-write the owner's log).
                // Process-lifetime: the bus holds the subscription; the host never detaches it.
                _ = new ExperienceTee(sessions, WorkspaceIsFolder(args) ? workspace : null);   // never the start folder (N1)
                // Number time-machine (feature #3): ambient vital-signs capture, owner-attached like the tee.
                local.AmbientVitalsEnabled = true;
                var pipe = EngineBroker.PipeNameFor(workspace);
                // An MCP-owned server has no human proof to trust, so it explicitly rejects the UI handshake.
                // VS Code treats that authenticated rejection as the ownership-reclaim signal and restarts this
                // process as the human owner; a subsequent MCP launch then attaches through RemoteEngine.
                var server = new RpcServer(sessions, local, pipe);
                // ASSISTANT PRESENCE, both doors. This process serves the MCP door itself, so its stdio peer
                // IS the assistant and is attached by construction; the attachment is held for the process
                // lifetime and deliberately never disposed. A second assistant attaching over the pipe is
                // counted by the same rule as in the UI-owned case above.
                local.ObserveAgentClients(() => server.AgentClientCount);
                _ = local.AttachAssistantDoor();
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

            // No file watching from the host. The default builder makes the working folder its content root and watches it
            // recursively (inotify) for appsettings.json changes; Claude Desktop starts this door in HOME, which cost two doors
            // about 165,000 watches each and made VS Code fail to open a model (ENOSPC). The door has no settings file, so it
            // roots at its own install folder, loads no configuration files, and adds back only the logging it uses.
            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory,
                DisableDefaults = true,
            });
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            // The only engine the assistant holds is the gated one (AssistantEngine): every tool call is looked up in the registry and,
            // when the person's policy asks, answered with a question before the engine sees it. An engine that is a proxy to another
            // process is not wrapped: the owner gates each connection that authenticated as an assistant, so a call is asked once.
            builder.Services.AddSingleton<IEngine>(AssistantEngine.ForAssistant(engine));
            builder.Services
                // §6 orientation (docs/harness-engineering.md): server instructions are read once at
                // initialization as the agent's system context. FIRST line points at the blessed primer so a
                // fresh/compacted agent orients unprompted. Kept short — it must not duplicate tool descriptions.
                .AddMcpServer(o => o.ServerInstructions =
                    "Call get_model_summary first for orientation: it is the token-budgeted session-start primer " +
                    "(connection, tier, model shape + grade, in-flight work, last-session tail, suggested next actions) " +
                    "and names the drill-down op for each section. Semanticus operates one LIVE semantic model through " +
                    "this MCP surface and the VS Code UI. Model edits are undoable with undo_change; saved files, git " +
                    "operations and writes to a live destination are not. An edit changes Semanticus's working copy on this computer only, " +
                    "also for a model opened from a Power BI workspace: nothing reaches the workspace until the model is saved back to it or published. The VS Code view updates at once; " +
                    "the AI Assistant sees the change on its next call. When someone else changed the model since your " +
                    "previous call, the result ends with one line, 'changed since your last call (in Studio): <refs>; rev N'; " +
                    "re-read those objects before relying on them. When a result starts a line with 'Plan:', the person " +
                    "has tasks for you: list them with list_assistant_requests, claim one with claim_request, and finish " +
                    "it with complete_request; while you hold a claim, your edits count toward that task (the one you claimed most recently). When the person tells you in chat " +
                    "to change the Plan (add, pause, reassign, remove or finish a task or an aim, approve a draft or a start plan), do it with the Plan tools; only the Permissions page is theirs alone. When an edit " +
                    "moves model health, its result ends with a text block starting 'health:': grade is the AI-readiness " +
                    "grade change, new lists rule ids of new findings on the objects the edit touched, findings counts " +
                    "them, and impact counts the downstream objects the edit reaches. Semanticus has one writer. On Pro use " +
                    "write_words, edit_objects, waive_findings and create_measures: one bulk call per page of work. " +
                    "Send these one after another. Parallel calls wait for the same writer and are not faster. " +
                    "When asked to work the Plan or improve the model, do every row a tool can do in one go and do not ask: " +
                    "anything undo_change can take back (renames, hiding, removing unused objects with remove_safe_objects, removing duplicates with remove_safe_objects once no report or measure uses them (hiding one that is used), synonyms, descriptions, " +
                    "a compatibility level raise, a calculated date table) is done first and reported after. Ask the person only for a sign-in or permission, " +
                    "a fact no tool can find, or a step undo cannot take back. " +
                    "Save with save_model and no path: it writes the model's own file (a model published from a service, or never saved, gets a file of its own and the result names the path), so never save a copy beside it unless the person asks for one. A copy is for a risky change or a request; saving to the same copy path again replaces it. Tell the person the path. " +
                    "A publish can be undone: the Publish page has Undo publish (rollback_push). Only undo_change is limited to the working copy, so never tell the person a publish cannot be undone. " +
                    "Keep going until the Plan has no Ready row a tool can do ('Fix N best-practice issues' and 'Remove N unused columns' are tool rows: do them). " +
                    "First find the model's reports (local folders and published ones), then set_report_scope and check_reports; if the model has none, call set_report_scope with an empty list, which counts as checked. Remove an unused generic column with unused_objects then remove_safe_objects (it re-checks each one and the chosen reports, and never removes a column a hierarchy, sort-by, role, calculation item, measure, relationship or report uses); hide a column when something uses it and say why on the row; delete_object a visibleAttribute column only when unused_objects marks it, which it does only after at least one report is chosen and every chosen report is checked (or the model has no reports); with no report checked, hide the column and say why on the row. A table nothing uses (no relationship, no measure, no role, no checked report) is removed the same way once the reports are checked, then reported, and you never ask whether to keep it because undo_change takes it back; a table something uses is kept and left out of what Copilot sees, with the reason on the row. " +
                    "Then read BOTH grades again (ai_readiness_scan and bpa_scan with detail='summary'), work any finding your own changes caused, and report both grades at the end. " +
                    "A row you leave or cannot do carries its reason on the row (decline_request or complete_request with a reason), in plain words, for example 'Needs a date column: none exists in this model'. " +
                    "Never say a tool name to the person.")
                .WithStdioServerTransport()
                .WithToolsFromAssembly()
                // A failed tool call must carry the engine's TEACHING message (scrubbed), not the SDK's opaque
                // "An error occurred invoking 'X'." — see McpErrorBoundary. Its success-path sibling appends the
                // health-delta block to a mutating tool's result (Pro, threshold-suppressed) — see McpHealthAppender.
                .WithRequestFilters(f => McpDoorFilters.Add(f, engine));

            Console.Error.WriteLine("[mcp] serving Semanticus tools over stdio.");
            await builder.Build().RunAsync();
            GC.KeepAlive(ownerLock);   // keep the owner lock held across the whole server lifetime (assigned, not read otherwise)
            return 0;
        }

        /// <summary>The election budget once this process has started an owner of its own.</summary>
        private static readonly TimeSpan OwnedStartDeadline = TimeSpan.FromSeconds(20);

        private static EngineInfo NewInfo(string pipe, string workspace, int? parentPid = null) => new EngineInfo
        {
            PipeName = pipe,
            Pid = Environment.ProcessId,
            StartedUtc = DateTime.UtcNow.ToString("o"),
            Workspace = workspace,
            // Who this engine dies with, so a later window can tell an engine whose editor is still open
            // from one whose editor is gone. Null on the MCP door, which has no parent watch at all.
            ParentPid = parentPid
        };

        /// <summary>The extension sets SEMANTICUS_NO_FOLDER=1 when no folder is open: the workspace it passes is then only the host's working folder.
        /// An engine started with no --workspace has only the process's working folder too, which is no place a person chose (UAT round 2, N1).</summary>
        private static bool WorkspaceIsFolder(string[] args) =>
            GetOpt(args, "--workspace") != null && Environment.GetEnvironmentVariable("SEMANTICUS_NO_FOLDER") != "1";

        private static string GetOpt(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
        }
    }
}
