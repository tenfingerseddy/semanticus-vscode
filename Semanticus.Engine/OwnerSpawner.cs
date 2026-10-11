using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Semanticus.Engine
{
    /// <summary>
    /// How the MCP door takes ownership of a workspace: it starts a separate owner process (`serve`) and joins it
    /// like any other client, instead of hosting the model inside itself.
    ///
    /// WHY A SEPARATE PROCESS. VS Code reclaims a workspace from an owner it cannot authenticate by stopping the
    /// pid published in engine.json (extension.ts reclaimUiOwnership → killEngine). When the assistant started
    /// first, that pid WAS the MCP server, so opening Studio stopped the assistant's connection mid-session
    /// (measured 2026-09-23: engine.json Pid equalled the MCP process pid). With the owner in its own process the
    /// reclaim stops only that process, and the MCP door rejoins VS Code's owner on its next call.
    ///
    /// The started owner dies with this MCP process (--parent-pid, plus the usual grace), rejects the UI
    /// handshake because nobody else knows its random challenge (so VS Code still reclaims it, exactly as it did
    /// an MCP-hosted owner), and speaks only through its pipe: its stdout and stderr are drained into this
    /// process's stderr, never onto the MCP protocol stream.
    /// </summary>
    internal static class OwnerSpawner
    {
        /// <summary>Marks the started owner's record (EngineInfo.StartedBy), so an updated VS Code window can tell
        /// it from another open window.</summary>
        public const string AssistantOwnerFlag = "--assistant-owner";

        /// <summary>Start an owner for <paramref name="workspace"/>. False when the process could not be started
        /// at all (the caller then falls back or reports). A true result does not promise this child won the
        /// workspace: another owner may have got there first, which is equally fine for a caller that rejoins
        /// whoever is published.</summary>
        public static bool TryStart(string workspace, string open, string licenseToken)
        {
            try
            {
                var (file, prefix) = LaunchCommand();
                if (string.IsNullOrEmpty(file)) return false;
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,   // NEVER inherited: this process's stdout is the MCP stream
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in prefix) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add("serve");
                psi.ArgumentList.Add("--workspace");
                psi.ArgumentList.Add(workspace);
                psi.ArgumentList.Add(ParentWatchdog.Option);
                psi.ArgumentList.Add(Environment.ProcessId.ToString());
                psi.ArgumentList.Add("--ui-challenge-stdin");
                psi.ArgumentList.Add(Entitlement.LicenseTokenDelivery.StdinFlag);
                psi.ArgumentList.Add(AssistantOwnerFlag);
                if (!string.IsNullOrEmpty(open)) { psi.ArgumentList.Add("--open"); psi.ArgumentList.Add(open); }

                StopHandleInheritance();
                var child = Process.Start(psi);
                if (child == null) return false;
                child.OutputDataReceived += (_, e) => { if (e.Data != null) Forward(e.Data); };
                child.ErrorDataReceived += (_, e) => { if (e.Data != null) Forward(e.Data); };
                child.BeginOutputReadLine();
                child.BeginErrorReadLine();
                // The challenge, then the licence line (empty → the owner falls back to env and the user licence
                // file, the same sources an MCP-hosted owner read). Closed at once, as the extension does.
                child.StandardInput.WriteLine(NewChallenge());
                child.StandardInput.WriteLine(licenseToken ?? string.Empty);
                child.StandardInput.Close();
                Console.Error.WriteLine($"[mcp] started Semanticus for this folder (pid {child.Id}).");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[mcp] could not start Semanticus for this folder: " + ex.Message);
                return false;
            }
        }

        /// <summary>This same build: the apphost itself, or dotnet plus this assembly when dotnet-hosted (F5). The
        /// owner records its own executable, so starting exactly this binary is what keeps the proxy's
        /// same-build check true when it joins.</summary>
        private static (string file, string[] prefix) LaunchCommand()
        {
            var exe = EngineBroker.CurrentExecutablePath();
            if (string.IsNullOrEmpty(exe)) return (null, Array.Empty<string>());
            var name = Path.GetFileNameWithoutExtension(exe);
            if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var dll = typeof(OwnerSpawner).Assembly.Location;
                return string.IsNullOrEmpty(dll) ? (null, Array.Empty<string>()) : (exe, new[] { dll });
            }
            return (exe, Array.Empty<string>());
        }

        private static string NewChallenge() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));   // 64 chars

        private static void Forward(string line)
        {
            try { Console.Error.WriteLine("[owner] " + line); } catch { /* stderr gone: nothing to tell anyone */ }
        }

        /// <summary>Windows hands a child EVERY inheritable handle, and this process's own standard handles came
        /// from the assistant as inheritable pipes. A child holding them would keep the assistant's pipes open after
        /// this process exits, so the assistant would not see the server go. Unix closes them on exec already.</summary>
        private static void StopHandleInheritance()
        {
            if (!OperatingSystem.IsWindows()) return;
            foreach (var id in new[] { StdInput, StdOutput, StdError })
            {
                try
                {
                    var h = GetStdHandle(id);
                    if (h != IntPtr.Zero && h != new IntPtr(-1)) SetHandleInformation(h, HandleFlagInherit, 0);
                }
                catch { /* best effort: the worst case is the old behaviour */ }
            }
        }

        private const int StdInput = -10, StdOutput = -11, StdError = -12;
        private const uint HandleFlagInherit = 0x00000001;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
    }
}
