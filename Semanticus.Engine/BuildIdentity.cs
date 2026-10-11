using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Semanticus.Engine
{
    /// <summary>Which BUILD of the engine this is, as opposed to which version or which path (F-241).
    ///
    /// An in-place reinstall keeps the version and the executable path and changes only the bytes, so a window that
    /// compared paths joined an engine the previous build had started and ran new screens on old engine code. The
    /// identity is a hash of the Semanticus assemblies beside the engine: every change to our own code changes at
    /// least one of them, and none of the runtime or third-party files take part, so it is cheap to compute (a few
    /// MB) and does not move when nothing of ours did.
    ///
    /// The recipe is shared with VS Code, which computes the same value from the folder it would launch from
    /// (Semanticus.VSCode/src/engineResolution.ts, engineBuildIdentity). Both sides pin the same test vector, because
    /// a drift between them would make every window replace every engine:
    ///   for each file named Semanticus.*.dll, lower-cased name, sorted ordinally:
    ///     "{name}\n{sha256 hex of its bytes}\n"
    ///   identity = lower-case sha256 hex of that text (UTF-8).</summary>
    public static class BuildIdentity
    {
        private static readonly Lazy<string> _current = new Lazy<string>(() => ForDirectory(AppContext.BaseDirectory));

        /// <summary>This process's build, read once from the folder it was loaded from. Null when that folder holds
        /// no Semanticus assembly, which a published engine never does.</summary>
        public static string Current => _current.Value;

        /// <summary>The identity of the build in <paramref name="directory"/>, or null when there is nothing of ours
        /// there or it cannot be read. Null never matches anything, so an unreadable build is treated as a different
        /// one rather than silently as this one.</summary>
        public static string ForDirectory(string directory)
        {
            try
            {
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;
                var files = Directory.GetFiles(directory)
                    .Select(p => (path: p, name: Path.GetFileName(p).ToLowerInvariant()))
                    .Where(f => f.name.StartsWith("semanticus.", StringComparison.Ordinal) && f.name.EndsWith(".dll", StringComparison.Ordinal))
                    .OrderBy(f => f.name, StringComparer.Ordinal)
                    .ToArray();
                if (files.Length == 0) return null;
                var text = new StringBuilder();
                foreach (var f in files)
                    text.Append(f.name).Append('\n').Append(Hex(SHA256.HashData(File.ReadAllBytes(f.path)))).Append('\n');
                return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
            }
            catch { return null; }
        }

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
