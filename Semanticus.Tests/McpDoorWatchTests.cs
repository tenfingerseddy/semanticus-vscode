using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Claude Desktop starts the MCP door with its working folder set to the person's HOME. The generic host
    /// treats that folder as its content root and watches it (recursively, through inotify) for appsettings.json
    /// changes. Measured 13 Oct on a real laptop: two doors held about 165,000 watches each, the user's limit was
    /// reached, VS Code logged "ENOSPC: System limit for number of file watchers reached", and opening a new model
    /// failed. The door has no settings file, so it must hold no watch on the folder it was started from.
    /// </summary>
    public sealed class McpDoorWatchTests
    {
        private const int FolderCount = 2000;
        private const int WatchBudget = 50;

        [EngineApphostFact(unixOnly: true)]
        public async Task The_mcp_door_started_in_a_big_folder_holds_almost_no_file_watches()
        {
            if (!OperatingSystem.IsLinux()) return;   // inotify is the Linux limit; the count is read from /proc
            var apphost = EngineApphost.Find();       // present: EngineApphostFact skips the test otherwise
            var root = Path.Combine(Path.GetTempPath(), "smx-watch-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            var cwd = Path.Combine(root, "home");
            var ws = Path.Combine(root, "ws");
            Directory.CreateDirectory(ws);
            for (var i = 0; i < FolderCount; i++)
                Directory.CreateDirectory(Path.Combine(cwd, "d" + (i % 40), "e" + (i % 7), "f" + i));

            Process p = null;
            var err = new StringBuilder();
            var replies = new ConcurrentDictionary<int, JObject>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = apphost,
                    WorkingDirectory = cwd,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                foreach (var a in new[] { "mcp", "--workspace", ws }) psi.ArgumentList.Add(a);
                psi.Environment["DOTNET_ROOT"] = RuntimeRoot();
                psi.Environment[ParentLifetime.GraceEnvironmentVariable] = "1000";
                p = Process.Start(psi);
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                p.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    try
                    {
                        var o = JObject.Parse(e.Data);
                        if (o["id"] != null && o["id"].Type == JTokenType.Integer) replies[o["id"].Value<int>()] = o;
                    }
                    catch { }
                };
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();

                async Task<JObject> Ask(int id, string method, JObject args)
                {
                    var msg = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = args };
                    await p.StandardInput.WriteLineAsync(msg.ToString(Newtonsoft.Json.Formatting.None));
                    await p.StandardInput.FlushAsync();
                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                    while (DateTime.UtcNow < deadline)
                    {
                        if (replies.TryRemove(id, out var r)) return r;
                        if (p.HasExited) throw new InvalidOperationException("the MCP door exited:\n" + err);
                        await Task.Delay(20);
                    }
                    throw new TimeoutException($"no reply to {method} within 60s:\n" + err);
                }

                await Ask(1, "initialize", new JObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JObject(),
                    ["clientInfo"] = new JObject { ["name"] = "watch-test", ["version"] = "0" },
                });
                await p.StandardInput.WriteLineAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToString(Newtonsoft.Json.Formatting.None));
                await p.StandardInput.FlushAsync();

                // The happy path first: the door still serves its tools.
                var list = await Ask(2, "tools/list", new JObject());
                Assert.True(list["result"]?["tools"]?.Count() > 10, "the door did not list its tools:\n" + list + "\n" + err);

                var watches = InotifyWatchesOf(p.Id);
                Assert.True(watches < WatchBudget,
                    $"the door started in a folder with {FolderCount} sub-folders holds {watches} inotify watches (limit {WatchBudget})");
            }
            finally
            {
                try { if (p != null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                try { p?.WaitForExit(5000); } catch { }
                try { p?.Dispose(); } catch { }
                try { Directory.Delete(root, true); } catch { }
            }
        }

        /// <summary>The kernel lists every watch of an inotify descriptor as one "inotify wd:" line in its fdinfo.</summary>
        private static int InotifyWatchesOf(int pid) =>
            Directory.GetFiles($"/proc/{pid}/fdinfo")
                .Sum(f =>
                {
                    try { return File.ReadAllLines(f).Count(l => l.StartsWith("inotify ", StringComparison.Ordinal)); }
                    catch { return 0; }
                });

        private static string RuntimeRoot()
        {
            var runtimeDir = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            return runtimeDir.Parent?.Parent?.Parent?.FullName ?? runtimeDir.FullName;
        }
    }
}
