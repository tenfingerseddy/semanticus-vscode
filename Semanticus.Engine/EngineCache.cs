using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Semanticus.Engine
{
    /// <summary>Keep installed POSIX engines outside VS Code's disposable extension folders.
    /// Both doors relocate before starting a session. exec preserves the pid, parent and stdio protocol.
    /// The original install remains the provenance and update-watch location, not the assembly load location.</summary>
    internal static class EngineCache
    {
        private const string SourceFile = ".semanticus-source";
        private const string Users = ".users";
        private const string PendingVariable = "SEMANTICUS_ENGINE_PENDING_LEASE";
        private static readonly Lazy<string> _source = new Lazy<string>(() =>
            OperatingSystem.IsWindows() || !File.Exists(Path.Combine(AppContext.BaseDirectory, SourceFile))
                ? null : File.ReadAllText(Path.Combine(AppContext.BaseDirectory, SourceFile)));

        internal static string InstallationExecutable => _source.Value;
        internal static string InstallationDirectory => InstallationExecutable == null
            ? AppContext.BaseDirectory : Path.GetDirectoryName(InstallationExecutable);

        internal static void Relocate(string[] args)
        {
            if (OperatingSystem.IsWindows() || InstallationExecutable != null) return;
            var source = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var extension = Path.GetFileName(Path.GetDirectoryName(source));
            if (Path.GetFileName(source) != "engine"
                || !extension.StartsWith("semanticus-vscode.semanticus-vscode-", StringComparison.Ordinal)
                || Path.GetFileName(EngineBroker.CurrentExecutablePath()) != "Semanticus.Engine") return;

            var root = Path.Combine(SemanticusHome.Root(), "engine");
            Directory.CreateDirectory(root);
            string target;
            using (Lock(root))
            {
                // Include dependencies, native libraries and stock files, including same-version reinstalls.
                var content = ContentIdentity(source);
                var origin = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
                target = Path.Combine(root, content + "-" + origin.Substring(0, 16));
                if (!Directory.Exists(target))
                {
                    var stage = Path.Combine(root, ".tmp-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.Combine(stage, Users));
                    WriteLease(stage, Environment.ProcessId.ToString(), Environment.ProcessId);
                    try
                    {
                        CopyTree(source, stage);
                        if (ContentIdentity(stage) != content)
                            throw new IOException("The installed engine changed while it was being copied. Reconnect the assistant.");
                        File.WriteAllText(Path.Combine(stage, SourceFile), Path.Combine(source, "Semanticus.Engine"));
                        Directory.Move(stage, target); // publish the complete copy in one step
                    }
                    catch { Directory.Delete(stage, true); throw; }
                }
                // Reserve this pid before releasing the cleanup lock and replacing this process with the copy.
                WriteLease(target, Environment.ProcessId.ToString(), Environment.ProcessId);
                Sweep(root, target);
            }
            Exec(Path.Combine(target, "Semanticus.Engine"), args);
        }

        internal static void HoldProcess()
        {
            if (InstallationExecutable == null) return;
            var directory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            using (Lock(Path.GetDirectoryName(directory)))
            {
                WriteLease(directory, Environment.ProcessId.ToString(), Environment.ProcessId);
                var pending = Environment.GetEnvironmentVariable(PendingVariable);
                if (!string.IsNullOrEmpty(pending) && Path.GetFileName(pending) == pending)
                    File.Delete(Path.Combine(directory, Users, pending));
            }
            // Keep the lease through runtime teardown. Cleanup removes it only after the pid has exited.
        }

        // A child must be protected during native/runtime startup, before its Main can register its own pid.
        // An interrupted start with unknown child identity is retained conservatively rather than deleted.
        internal static ChildLease ReserveChild()
        {
            if (InstallationExecutable == null) return null;
            var directory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var name = "pending-" + Guid.NewGuid().ToString("N");
            using (Lock(Path.GetDirectoryName(directory)))
                File.WriteAllText(Path.Combine(directory, Users, name), "pending");
            return new ChildLease(directory, name);
        }

        private static string ContentIdentity(string directory)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetRelativePath(directory, f) != SourceFile
                    && !Path.GetRelativePath(directory, f).StartsWith(Users + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .OrderBy(f => Path.GetRelativePath(directory, f), StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, file) + "\n"));
                using var stream = File.OpenRead(file);
                hash.AppendData(SHA256.HashData(stream));
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        private static void CopyTree(string source, string target)
        {
            foreach (var dir in Directory.EnumerateDirectories(source))
            {
                var destination = Path.Combine(target, Path.GetFileName(dir));
                Directory.CreateDirectory(destination);
                CopyTree(dir, destination);
            }
            foreach (var file in Directory.EnumerateFiles(source))
            {
                var destination = Path.Combine(target, Path.GetFileName(file));
                File.Copy(file, destination);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(file));
            }
        }

        private static FileStream Lock(string root)
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                try { return new FileStream(Path.Combine(root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(30)) { Thread.Sleep(25); }
            }
        }

        private static void WriteLease(string directory, string name, int pid)
        {
            Directory.CreateDirectory(Path.Combine(directory, Users));
            File.WriteAllText(Path.Combine(directory, Users, name), pid.ToString());
        }

        private static void Sweep(string root, string current)
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (directory == current) continue;
                if (!File.Exists(Path.Combine(directory, SourceFile))
                    && !Path.GetFileName(directory).StartsWith(".tmp-", StringComparison.Ordinal)) continue;
                try
                {
                    var inUse = false;
                    foreach (var lease in Directory.EnumerateFiles(Path.Combine(directory, Users)))
                    {
                        if (!int.TryParse(File.ReadAllText(lease), out var pid) || pid <= 0) { inUse = true; continue; }
                        bool alive;
                        try
                        {
                            using var process = Process.GetProcessById(pid);
                            // PID reuse can retain an unused copy longer. Clock drift must never delete a live copy.
                            alive = !process.HasExited;
                        }
                        catch (ArgumentException) { alive = false; }
                        if (alive) inUse = true;
                        else File.Delete(lease);
                    }
                    if (!inUse) Directory.Delete(directory, true);
                }
                catch { /* unreadable process identity or files: retain the copy */ }
            }
        }

        internal sealed class ChildLease : IDisposable
        {
            private readonly string _directory;
            private bool _started;
            internal string Name { get; }
            internal ChildLease(string directory, string name) { _directory = directory; Name = name; }
            internal void Started(int pid)
            {
                _started = true;
                try
                {
                    using (Lock(Path.GetDirectoryName(_directory)))
                        if (File.Exists(Path.Combine(_directory, Users, Name))) WriteLease(_directory, Name, pid);
                }
                catch { /* keep the pending reservation if the child's identity is not yet readable */ }
            }
            public void Dispose()
            {
                if (_started) return; // the child removes the reservation when it registers
                using (Lock(Path.GetDirectoryName(_directory))) File.Delete(Path.Combine(_directory, Users, Name));
            }
        }

        private static void Exec(string executable, string[] args)
        {
            var values = new[] { executable }.Concat(args).Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            var argv = Marshal.AllocHGlobal((values.Length + 1) * IntPtr.Size);
            try
            {
                for (var i = 0; i < values.Length; i++) Marshal.WriteIntPtr(argv, i * IntPtr.Size, values[i]);
                Marshal.WriteIntPtr(argv, values.Length * IntPtr.Size, IntPtr.Zero);
                if (execv(executable, argv) == -1) throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            finally
            {
                foreach (var value in values) Marshal.FreeCoTaskMem(value);
                Marshal.FreeHGlobal(argv);
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int execv([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, IntPtr argv);
    }
}
