using System;
using System.IO;
using System.Text.Json;

namespace Semanticus.Engine
{
    /// <summary>
    /// A tiny cross-process read-modify-write for a single JSON file under <c>~/.semanticus/</c>. Factored out because
    /// two engine processes share this home (the VS Code extension and the MCP server each own one), so an in-process
    /// lock alone loses updates — the exact bug an adversarial review caught in the connection registry. Every mutation
    /// holds a cross-process file lock across load → mutate → save, and an unreadable EXISTING file is moved aside
    /// rather than silently overwritten (which would destroy governance data on a transient read error).
    /// </summary>
    internal static class HomeFile
    {
        internal static string Root(string rootOverride) => rootOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".semanticus");

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>Read a file for display: any failure degrades to <paramref name="empty"/>. Never writes.</summary>
        internal static T Read<T>(string path, Func<T> empty)
        {
            try { return File.Exists(path) ? (JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? empty()) : empty(); }
            catch { return empty(); }
        }

        internal static bool Exists(string path) => File.Exists(path);

        /// <summary>Read that THROWS on a parse/IO failure — for a caller that must distinguish "file absent" (a normal
        /// default) from "file present but unreadable" (fail closed), which the swallowing <see cref="Read"/> cannot.</summary>
        internal static T ReadStrict<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);

        /// <summary>Run a mutation under a cross-process lock: lock → read (fresh, this side of the lock) → mutate →
        /// atomic save → unlock. An unreadable existing file is preserved (`.corrupt-<stamp>`) before the fresh start.</summary>
        internal static TResult Mutate<TState, TResult>(string path, Func<TState> empty, Func<TState, TResult> mutate)
        {
            var dir = Path.GetDirectoryName(path);
            Directory.CreateDirectory(dir);
            using (AcquireLock(path))
            {
                var state = ReadForWrite(path, empty);
                var result = mutate(state);
                AtomicWrite(path, JsonSerializer.Serialize(state, Json));
                return result;
            }
        }

        private static TState ReadForWrite<TState>(string path, Func<TState> empty)
        {
            if (!File.Exists(path)) return empty();
            try { return JsonSerializer.Deserialize<TState>(File.ReadAllText(path), Json) ?? empty(); }
            catch
            {
                // Preserve the unreadable bytes under a collision-resistant name BEFORE any write can replace them. If
                // even that move fails, ABORT — never overwrite a file we could not read, which could be a good policy
                // we simply failed to parse this once (a transient IO error must not destroy governance data).
                var aside = path + ".corrupt-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                try { File.Move(path, aside); }
                catch (Exception ex) { throw new IOException($"Refusing to overwrite an unreadable policy file at {path} (could not preserve it: {ex.Message}).", ex); }
                return empty();
            }
        }

        private static void AtomicWrite(string path, string text)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);   // a crash mid-write leaves the old file + a .tmp, never a truncated one
        }

        // A cross-process lock the mutation MUST hold. On timeout it THROWS rather than proceeding unlocked: for the
        // approval ledger, an unlocked read-modify-write lets two processes double-spend one grant (both read it, both
        // remove it in private copies, both act). A rare thrown failure under heavy contention is fail-closed and
        // recoverable; a silent double-spend is neither.
        private static IDisposable AcquireLock(string path)
        {
            var lockPath = path + ".lock";
            for (var i = 0; i < 200; i++)
            {
                try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (IOException) { System.Threading.Thread.Sleep(15); }
            }
            throw new IOException($"Could not acquire the lock for {path} within 3s — another process is holding it. Try again.");
        }
    }
}
