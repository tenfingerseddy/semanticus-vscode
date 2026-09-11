using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// D-118: the agent door must join the UI owner's advertised pipe, and must not start a second
    /// owner when one is already serving. The short pipe name is not the address on Unix: .NET places
    /// the socket under this process's temp directory, which often differs between VS Code and the
    /// AI Assistant process. Clients have to open the stamped absolute path.
    /// </summary>
    public sealed class McpAttachTests
    {
        [Fact]
        public async Task Agent_door_opens_the_advertised_pipe_path_when_the_short_name_does_not_exist()
        {
            if (OperatingSystem.IsWindows())
                return;   // Windows named pipes are the short name; the missing-socket failure is Unix.

            var pipeName = "semanticus-attach-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var advertisedPath = EngineBroker.PipePathFor(pipeName);
            var sessions = new SessionManager();
            var owner = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro());
            using var server = new RpcServer(sessions, owner, pipeName);
            using var cts = new CancellationTokenSource();
            var running = server.RunAsync(cts.Token);
            RemoteEngine first = null;
            RemoteEngine remote = null;
            try
            {
                await WaitForPipeAsync(advertisedPath);
                // The UI door is already on the session, as in the UAT repro.
                first = await RemoteEngine.ConnectAsync(pipeName, timeoutMs: 2000);
                await first.CreateModelAsync("AttachProbe", 1604);
                // The advertised short name is missing (the UAT observation). The stamped path is the live socket.
                var missingName = "semanticus-missing-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                Assert.False(File.Exists(EngineBroker.PipePathFor(missingName)));
                remote = await RemoteEngine.ConnectAsync(missingName, timeoutMs: 2000, pipePath: advertisedPath);
                var info = await remote.SessionInfoAsync();
                Assert.Equal("AttachProbe", info.ModelName);
            }
            finally
            {
                remote?.Dispose();
                first?.Dispose();
                cts.Cancel();
                try { await running; } catch { }
                owner.Dispose();
                sessions.Dispose();
            }
        }

        [Fact]
        public async Task Agent_door_joins_the_live_owner_published_in_the_workspace_and_does_not_take_the_lock()
        {
            var ws = Path.Combine(Path.GetTempPath(), "smx-attach-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            var pipeName = EngineBroker.PipeNameFor(ws);
            var sessions = new SessionManager();
            var owner = new LocalEngine(sessions, Semanticus.Engine.Entitlement.LicenseEntitlement.DevPro(), ws);
            using var server = new RpcServer(sessions, owner, pipeName);
            using var cts = new CancellationTokenSource();
            var running = server.RunAsync(cts.Token);
            FileStream ownerLock = null;
            RemoteEngine remote = null;
            try
            {
                ownerLock = EngineBroker.TryAcquireOwnerLock(ws);
                Assert.NotNull(ownerLock);
                Assert.Null(EngineBroker.TryAcquireOwnerLock(ws));   // a second owner must not start
                EngineBroker.WriteInfo(ws, new EngineInfo
                {
                    PipeName = pipeName,
                    Pid = Environment.ProcessId,
                    StartedUtc = DateTime.UtcNow.ToString("o"),
                    Workspace = ws,
                });
                var published = EngineBroker.ReadInfo(ws);
                Assert.False(string.IsNullOrEmpty(published.PipePath));
                await WaitForPipeAsync(published.PipePath);
                remote = await RemoteEngine.ConnectAsync(published.PipeName, ws, timeoutMs: 2000, pipePath: published.PipePath);
                await owner.CreateModelAsync("AttachProbe", 1604);
                var info = await remote.SessionInfoAsync();
                Assert.Equal("AttachProbe", info.ModelName);
                Assert.Null(EngineBroker.TryAcquireOwnerLock(ws));
            }
            finally
            {
                remote?.Dispose();
                cts.Cancel();
                try { await running; } catch { }
                owner.Dispose();
                sessions.Dispose();
                try { ownerLock?.Dispose(); } catch { }
                try { Directory.Delete(ws, true); } catch { }
            }
        }

        // ---- D-118, the whole door, across real processes --------------------------------------------------------
        // The two tests above keep the owner IN-PROCESS, which is why they passed while the real UAT failed: the
        // door's first gate is EngineBroker.IsAlive, and it only sees a Unix apphost name when the owner is a
        // SEPARATE process. This runs the shipped door end to end on the platform that broke: a real `serve` owner
        // and a real `mcp` agent, two processes, two temp directories (the UAT shape). Before the name fix the
        // agent printed "could not start for this folder within 5s" and never joined.
        [Fact]
        public async Task Agent_door_process_joins_a_live_owner_process()
        {
            var apphost = FindEngineApphost();
            if (apphost == null)
            {
                Console.WriteLine("skipped: no built engine apphost beside the tests (build Semanticus.Engine first)");
                return;
            }

            var ws = Path.Combine(Path.GetTempPath(), "smx-door-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var agentTmp = Path.Combine(Path.GetTempPath(), "smx-door-tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(agentTmp);
            Process owner = null, agent = null;
            var agentErr = new StringBuilder();
            try
            {
                owner = StartEngine(apphost, new[] { "serve", "--workspace", ws, "--ui-challenge-stdin" }, null);
                owner.StandardInput.WriteLine("smoke-ui-challenge-0123456789abcdef012345");
                owner.StandardInput.Flush();   // the challenge is read once; stdin stays open for the process lifetime

                var info = await WaitForOwnerAsync(ws, owner);
                Assert.True(EngineBroker.IsAlive(info), "the door's liveness gate rejected the live owner");

                // The agent gets its OWN temp directory: on Unix the socket lives under the owner's, so only the
                // stamped absolute path can name it. That is the other half of D-118.
                agent = StartEngine(apphost, new[] { "mcp", "--workspace", ws }, agentTmp,
                    line => { lock (agentErr) agentErr.AppendLine(line); });

                var joined = await WaitForAsync(() =>
                {
                    lock (agentErr) return agentErr.ToString().Contains("joined the running session", StringComparison.Ordinal);
                }, TimeSpan.FromSeconds(30));
                lock (agentErr)
                    Assert.True(joined, "the agent door never joined the live owner. Its output was:\n" + agentErr);
            }
            finally
            {
                Kill(agent);
                Kill(owner);
                try { Directory.Delete(ws, true); } catch { }
                try { Directory.Delete(agentTmp, true); } catch { }
            }
        }

        /// <summary>The shipped apphost beside the test binaries: &lt;repo&gt;/Semanticus.Engine/bin/&lt;config&gt;/net8.0/.
        /// Null when nothing is built there, so the test reports a skip a reader can act on.</summary>
        private static string FindEngineApphost()
        {
            var here = new DirectoryInfo(AppContext.BaseDirectory);      // .../Semanticus.Tests/bin/<config>/net8.0/
            var config = here.Parent?.Name;
            var root = here.Parent?.Parent?.Parent?.Parent?.FullName;
            if (config == null || root == null) return null;
            var name = OperatingSystem.IsWindows() ? "Semanticus.Engine.exe" : "Semanticus.Engine";
            var path = Path.Combine(root, "Semanticus.Engine", "bin", config, "net8.0", name);
            return File.Exists(path) ? path : null;
        }

        private static Process StartEngine(string apphost, string[] args, string tmpDir, Action<string> onErr = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = apphost,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (tmpDir != null) psi.Environment["TMPDIR"] = tmpDir;
            // An apphost resolves its runtime from DOTNET_ROOT. Point it at the runtime running THIS test so the
            // child starts wherever the tests run, instead of whatever the machine happens to have on PATH.
            psi.Environment["DOTNET_ROOT"] = RuntimeRoot();
            var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + apphost);
            // Every line is drained (a full pipe would stall the child); callers that care add their own sink
            // before the reader starts, so they see the first line too.
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) onErr?.Invoke(e.Data); };
            p.BeginErrorReadLine();
            return p;
        }

        private static string RuntimeRoot()
        {
            var runtimeDir = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            return runtimeDir.Parent?.Parent?.Parent?.FullName ?? runtimeDir.FullName;   // <root>/shared/<framework>/<ver>
        }

        private static async Task<EngineInfo> WaitForOwnerAsync(string workspace, Process owner)
        {
            for (var i = 0; i < 300; i++)
            {
                if (owner.HasExited) throw new InvalidOperationException("the owner engine exited during startup");
                var info = EngineBroker.ReadInfo(workspace);
                if (info != null && !string.IsNullOrEmpty(info.PipePath)
                    && (OperatingSystem.IsWindows() || File.Exists(info.PipePath)))
                    return info;
                await Task.Delay(50);
            }
            throw new TimeoutException("the owner engine never published its pipe for " + workspace);
        }

        private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                await Task.Delay(50);
            }
            return condition();
        }

        private static void Kill(Process p)
        {
            try { if (p != null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p?.Dispose(); } catch { }
        }

        private static async Task WaitForPipeAsync(string pipePath)
        {
            if (OperatingSystem.IsWindows())
            {
                await Task.Delay(50);
                return;
            }
            for (var i = 0; i < 50; i++)
            {
                if (File.Exists(pipePath)) return;
                await Task.Delay(20);
            }
            throw new TimeoutException("Owner pipe was not published at " + pipePath);
        }
    }
}
