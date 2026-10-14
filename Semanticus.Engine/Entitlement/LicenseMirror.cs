using System;
using System.IO;

namespace Semanticus.Engine.Entitlement
{
    /// <summary>
    /// The licence file that lets an engine started OUTSIDE VS Code (Claude Desktop, Codex, Cursor, a terminal) run as Pro.
    ///
    /// WHY IT EXISTS. The extension keeps the person's token in VS Code's secret store and hands it to the engine it starts,
    /// on stdin. An engine started by another assistant when VS Code is closed is not handed anything: it reads the
    /// SEMANTICUS_LICENSE variable, then the user licence file, and found neither, so a paying person got the free product there.
    ///
    /// WHAT IT DOES. When the person connects another assistant, the Pro token this engine already verified is saved to the
    /// user licence file (owner read and write only). From then on every engine on this machine reads the same licence.
    /// The result says so in a sentence. After that the file follows the licence: a replaced licence is rewritten, a removed one
    /// deletes it. A change makes the file only when an assistant is already connected (the person connected it on Free, then Pro began); with none connected it only keeps an existing file in step.
    /// </summary>
    internal static class LicenseMirror
    {
        public enum Outcome { Saved, AlreadySaved, NotPro, Failed }

        /// <summary>Save the verified token of <paramref name="entitlement"/> to <paramref name="path"/> when it is a real Pro licence.</summary>
        public static Outcome EnsureSaved(IEntitlement entitlement, string path)
        {
            var token = LicenseEntitlement.CloudCredentialOf(entitlement);
            if (string.IsNullOrWhiteSpace(token)) return Outcome.NotPro;
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Trim() == token.Trim()) return Outcome.AlreadySaved;
                Write(path, token.Trim());
                return Outcome.Saved;
            }
            catch (Exception) { return Outcome.Failed; }
        }

        /// <summary>The licence changed in this engine: keep the file in step, but only if the file is already there, or <paramref name="create"/> (an assistant is connected and has no file yet).</summary>
        public static void Follow(string path, string newToken, bool create = false)
        {
            try
            {
                if (!File.Exists(path) && !(create && !string.IsNullOrWhiteSpace(newToken))) return;
                if (string.IsNullOrWhiteSpace(newToken)) File.Delete(path);
                else Write(path, newToken.Trim());
            }
            catch (Exception) { /* a file that cannot change is read as before: the next start reports the licence it holds */ }
        }

        private static void Write(string path, string token)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp-" + Environment.ProcessId;
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;   // 0600
            using (var fs = new FileStream(tmp, options))
            using (var w = new StreamWriter(fs)) w.Write(token);
            File.Move(tmp, path, true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
