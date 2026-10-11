using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Semanticus.Engine
{
    /// <summary>
    /// What a path is, for an approval to be about THAT file: the real path of the deepest part that exists (every link
    /// followed, in a parent folder as much as in the file itself) plus the rest of the path as named, and the state there:
    /// <see cref="Absent"/>, or a hash of what is there (a file's bytes, or a folder's files by name, size and time). An
    /// approval is bound to both. A link put in a parent folder after the question changes the real path; a file that
    /// appeared where there was none changes the state from absent; a changed file changes the hash. A write the person
    /// approved as "a new file" is made with CreateNew, so it also fails if one appears in the instant between.
    /// </summary>
    internal static class PathWitness
    {
        public const string Absent = "absent";
        private const int MostEntries = 50_000;
        private const long WholeFileLimit = 256L * 1024 * 1024;

        /// <summary>The state an unresolved path carries: a link that never ends, one that cannot be read, or one that points at nothing. It is never
        /// <see cref="Absent"/> (that would read as a new file the person approved) and a write to it is refused, not asked about.</summary>
        public const string Unresolved = "unresolved:links";

        /// <summary>The most links one path may pass through before it is called unresolved (Linux allows 40).</summary>
        private const int MostLinkHops = 40;

        public static bool IsUnresolved(string state) => state != null && state.StartsWith("unresolved", StringComparison.Ordinal);

        public static (string Key, string State) Of(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return ("", Absent);
            string full;
            try { full = Path.GetFullPath(path.Trim()); }
            catch { return (path.Trim(), Absent); }
            var rest = "";
            var probe = full.TrimEnd(Path.DirectorySeparatorChar);
            // Walk up to the deepest part that exists (a file, a folder, or a link: Exists follows it, so a broken link is not).
            while (!string.IsNullOrEmpty(probe) && !File.Exists(probe) && !Directory.Exists(probe))
            {
                // A link that points at nothing is not "a part that is not there": the write would land wherever it points. Fail closed.
                if (IsLink(probe)) return (full, Unresolved);
                var name = Path.GetFileName(probe);
                rest = rest.Length == 0 ? name : name + Path.DirectorySeparatorChar + rest;
                var parent = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, probe, StringComparison.Ordinal)) { probe = parent ?? ""; break; }
                probe = parent;
            }
            var real = string.IsNullOrEmpty(probe) ? "" : Real(probe);
            if (real == null) return (full, Unresolved);
            var key = rest.Length == 0 ? real : Path.Combine(real, rest);
            if (rest.Length > 0) return (key, Absent);
            return (key, StateOf(real));
        }

        private static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 && new FileInfo(path).LinkTarget != null; }
            catch { return false; }
        }

        /// <summary>The real path of something that exists: EVERY link in it resolved, in the last part and in every folder above it, and in
        /// every folder above the target of a link (a link whose target runs through another link is followed to the end). A link is
        /// followed by putting its target's parts back in front of the parts still to walk, so nothing is trusted unresolved. At most
        /// <see cref="MostLinkHops"/> links are followed in all. Returns null when a link cannot be read or the limit is passed: the caller
        /// treats the path as unresolved and fails closed.</summary>
        public static string Real(string existing)
        {
            var full = Path.GetFullPath(existing);
            var root = Path.GetPathRoot(full) ?? "";
            var pending = new LinkedList<string>(full.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries));
            var current = root;
            var hops = 0;
            while (pending.Count > 0)
            {
                var part = pending.First.Value;
                pending.RemoveFirst();
                if (part == ".") continue;
                if (part == "..")
                {
                    var up = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar));
                    current = string.IsNullOrEmpty(up) ? root : up;
                    continue;
                }
                var next = Path.Combine(current, part);
                string target;
                try
                {
                    FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                    target = info.LinkTarget;
                }
                catch { return null; }   // a link we cannot read is not one we can vouch for
                if (target == null) { current = next; continue; }
                if (++hops > MostLinkHops) return null;
                var targetFull = Path.IsPathRooted(target) ? target : Path.Combine(current, target);
                var targetRoot = Path.GetPathRoot(targetFull) ?? "";
                var parts = targetFull.Substring(targetRoot.Length).Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                for (var i = parts.Length - 1; i >= 0; i--) pending.AddFirst(parts[i]);   // the target's own folders are walked, link by link
                current = string.IsNullOrEmpty(targetRoot) ? root : targetRoot;
            }
            return current;
        }

        private static string StateOf(string real)
        {
            try
            {
                using var sha = SHA256.Create();
                if (File.Exists(real))
                {
                    var info = new FileInfo(real);
                    if (info.Length > WholeFileLimit)
                        return "big:" + info.Length + ":" + info.LastWriteTimeUtc.Ticks;
                    using var stream = new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    return "file:" + Convert.ToHexString(sha.ComputeHash(stream)).Substring(0, 32).ToLowerInvariant();
                }
                if (Directory.Exists(real))
                {
                    var sb = new StringBuilder();
                    var n = 0;
                    foreach (var f in Directory.EnumerateFiles(real, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                    {
                        if (++n > MostEntries) { sb.Append("(more)"); break; }
                        var fi = new FileInfo(f);
                        sb.Append(Path.GetRelativePath(real, f)).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append('\n');
                    }
                    return "dir:" + Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 32).ToLowerInvariant();
                }
            }
            catch (Exception ex) { return "unreadable:" + ex.GetType().Name; }
            return Absent;
        }
    }
}
