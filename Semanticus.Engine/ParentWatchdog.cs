using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Semanticus.Engine
{
    /// <summary>
    /// Ties an OWNER engine's lifetime to the process that started it.
    ///
    /// WHY: `serve` is spawned by the VS Code extension host and does nothing else to learn that its editor
    /// has gone. Closing the window leaves the engine reparented to init (systemd --user on Linux) and still
    /// serving its pipe. Measured on Kane's machine 2026-09-21: nine such engines were alive, six from
    /// extension version 1.1.1 spawned ten days earlier and two from 1.2.0 spawned seven days earlier. Each
    /// one holds a workspace lock, a TOM model in memory, and a published engine.json that a later window
    /// can attach to by mistake.
    ///
    /// The extension passes <see cref="Option"/>; a launch that omits it watches the OS parent instead, so an
    /// older extension (or a hand-run engine started from a terminal) is covered too. A launch with no real
    /// parent to watch (already reparented, or a parent we cannot discover) is left alone rather than
    /// guessed at: killing an engine on a bad guess is worse than the leak this fixes.
    ///
    /// The MCP door deliberately does NOT use this. That process is spawned by the assistant and dies with
    /// its stdio, so a second lifetime rule there could only take a healthy agent session down early.
    /// </summary>
    public static class ParentWatchdog
    {
        /// <summary>The command-line option the extension passes so the engine knows who owns it.</summary>
        public const string Option = "--parent-pid";

        /// <summary>How often the Unix probe asks whether the parent is still there.</summary>
        public static readonly TimeSpan DefaultPoll = TimeSpan.FromSeconds(1);

        private const int ESRCH = 3;   // POSIX "no such process"

        /// <summary>The pid named by <see cref="Option"/>, or null when it is absent, unparseable, or not a
        /// pid a process can have. A malformed value is treated as absent so a bad launch line degrades to
        /// the OS-parent fallback instead of refusing to start.</summary>
        public static int? ParsePid(string[] args)
        {
            if (args == null) return null;
            var i = Array.IndexOf(args, Option);
            if (i < 0 || i + 1 >= args.Length) return null;
            return int.TryParse(args[i + 1], out var pid) && pid > 0 ? pid : (int?)null;
        }

        /// <summary>Which process this engine should die with: the explicitly named parent, else the OS
        /// parent. Null means "nothing to watch": a pid of 0 or 1 is init or an already-orphaned launch,
        /// and watching init would mean never exiting anyway.</summary>
        public static int? ResolveWatchTarget(string[] args, Func<int?> osParentPid = null)
        {
            var explicitPid = ParsePid(args);
            if (explicitPid.HasValue) return IsWatchable(explicitPid.Value) ? explicitPid : null;
            var parent = (osParentPid ?? OsParentPid)();
            return parent.HasValue && IsWatchable(parent.Value) ? parent : null;
        }

        /// <summary>A pid worth watching. 0 is "unknown" and 1 is init, which outlives every engine.</summary>
        public static bool IsWatchable(int pid) => pid > 1;

        /// <summary>This process's parent, or null when the platform will not say. Never throws: a watchdog
        /// that cannot identify a parent must degrade to "no watch", not take the engine down with it.</summary>
        public static int? OsParentPid()
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return getppid();
                return WindowsParentPid(Environment.ProcessId);
            }
            catch { return null; }
        }

        /// <summary>Does a process with this pid exist right now? EPERM (or any Windows access denial) is
        /// evidence that it DOES: only a definite "no such process" counts as gone, because a false negative
        /// here kills a live engine.</summary>
        public static bool IsAlive(int pid)
        {
            if (pid <= 0) return false;
            if (OperatingSystem.IsWindows())
            {
                try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
                catch (ArgumentException) { return false; }   // no such process
                catch { return true; }                        // cannot inspect it: not proof it is gone
            }
            try
            {
                if (kill(pid, 0) == 0) return true;
                return Marshal.GetLastWin32Error() != ESRCH;
            }
            catch { return true; }
        }

        /// <summary>Start watching <paramref name="pid"/>; <paramref name="onGone"/> runs once, on a
        /// background thread, when it dies. Dispose to stop watching. <paramref name="isAlive"/> is for
        /// tests; production passes null and gets the platform probe.
        ///
        /// The watch thread OWNS the cancellation source and is the only code that disposes it. Dispose here
        /// only cancels. Reading a token whose source someone else already disposed throws, and an exception
        /// on a background thread in an engine host ends the process, not the watch (PR 386 on CI).</summary>
        public static IDisposable Watch(int pid, Action onGone, TimeSpan? poll = null, Func<int, bool> isAlive = null)
        {
            if (onGone == null) throw new ArgumentNullException(nameof(onGone));
            var stop = new CancellationTokenSource();
            var interval = poll ?? DefaultPoll;
            var probe = isAlive ?? DefaultProbe(pid);
            var thread = new Thread(() =>
            {
                try
                {
                    // Windows: wait on the process handle. Holding the handle also pins the pid, so a recycled
                    // pid can never be mistaken for the parent still being alive.
                    if (isAlive == null && OperatingSystem.IsWindows() && WaitForWindowsExit(pid, stop.Token))
                    {
                        if (!stop.IsCancellationRequested) onGone();
                        return;
                    }
                    while (!stop.IsCancellationRequested)
                    {
                        if (!probe(pid))
                        {
                            if (!stop.IsCancellationRequested) onGone();
                            return;
                        }
                        stop.Token.WaitHandle.WaitOne(interval);
                    }
                }
                catch (Exception ex)
                {
                    // Never the thing that crashes the engine. One line, then this watch is over.
                    Console.Error.WriteLine("[engine] the parent watch stopped after an error: " + ex.Message);
                }
                finally { try { stop.Dispose(); } catch { } }
            })
            { IsBackground = true, Name = "semanticus-parent-watchdog" };
            thread.Start();
            return new Stopper(stop);
        }

        /// <summary>The Unix probe, closed over the parent pid we started with. Reparenting is the RELIABLE
        /// signal on Unix and kill(pid,0) alone is not: a parent that died but has not been reaped is a
        /// zombie, which still answers "alive". If the pid we are watching was our own parent and no longer
        /// is, it is gone whatever the pid table says.</summary>
        private static Func<int, bool> DefaultProbe(int pid)
        {
            if (OperatingSystem.IsWindows()) return IsAlive;
            var original = OsParentPid();
            var watchingOwnParent = original.HasValue && original.Value == pid;
            return p =>
            {
                if (watchingOwnParent)
                {
                    try { if (getppid() != p) return false; } catch { /* fall through to the pid probe */ }
                }
                return IsAlive(p);
            };
        }

        private static bool WaitForWindowsExit(int pid, CancellationToken stop)
        {
            Process p;
            try { p = Process.GetProcessById(pid); }
            catch (ArgumentException) { return true; }   // already gone
            catch { return false; }                      // no handle: let the caller poll instead
            using (p)
            {
                while (!stop.IsCancellationRequested)
                {
                    try { if (p.WaitForExit(250)) return true; }
                    catch { return false; }
                }
                return false;
            }
        }

        /// <summary>Cancel only. The watch thread disposes the source; see Watch.</summary>
        private sealed class Stopper : IDisposable
        {
            private readonly CancellationTokenSource _stop;
            public Stopper(CancellationTokenSource stop) => _stop = stop;
            public void Dispose()
            {
                try { _stop.Cancel(); } catch { }   // already disposed by its own thread: nothing left to wake
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);

        [DllImport("libc")]
        private static extern int getppid();

        // ---- Windows parent discovery -------------------------------------------------------------------
        // Win32 has no getppid. The snapshot walk is the standard answer and is only reached when the
        // extension did not pass --parent-pid, so a failure here costs the fallback, never a running engine.

        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static int? WindowsParentPid(int pid)
        {
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return null;
            try
            {
                var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
                if (!Process32FirstW(snapshot, ref entry)) return null;
                do
                {
                    if (entry.th32ProcessID == (uint)pid) return (int)entry.th32ParentProcessID;
                }
                while (Process32NextW(snapshot, ref entry));
                return null;
            }
            finally { CloseHandle(snapshot); }
        }
    }
}
