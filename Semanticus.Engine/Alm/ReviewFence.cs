using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Semanticus.Engine
{
    /// <summary>
    /// Binds a write to the preview that was reviewed. The token covers the session, the destination, and
    /// the exact change set. A commit without a matching token is refused in plain words.
    /// </summary>
    internal static class ReviewFence
    {
        private static readonly string Secret = Guid.NewGuid().ToString("N");

        internal const string Missing =
            "This write needs the review token from the preview you just looked at. Run the preview again, then confirm with that token. Nothing was written.";
        internal const string Stale =
            "The model or the destination changed since you reviewed it. Run the preview again and confirm the new list. Nothing was written.";

        internal static string Mint(string sessionId, long revision, string targetIdentity, IEnumerable<string> changeSet)
        {
            var changes = string.Join("\n", (changeSet ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrEmpty(s))
                .OrderBy(s => s, StringComparer.Ordinal));
            var basis = (sessionId ?? "") + "|"
                + revision.ToString(CultureInfo.InvariantCulture) + "|"
                + (targetIdentity ?? "") + "|"
                + changes + "|"
                + Secret;
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(basis));
            return "REVIEW-" + Convert.ToHexString(h).Substring(0, 24);
        }

        internal static string TargetIdentity(string endpoint, string database) =>
            (endpoint ?? "").Trim() + "\n" + (database ?? "").Trim();

        internal static string FileTargetIdentity(string path) => "file:" + (path ?? "").Trim();

        internal static string SessionTargetIdentity() => "session";

        internal static string HashFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return "";
            using var sha = SHA256.Create();
            using var fs = System.IO.File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }

        internal static string HashBytes(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes ?? Array.Empty<byte>()));
        }

        /// <summary>Null when the commit may proceed. Otherwise the refusal in plain words.</summary>
        internal static string Refusal(bool commit, string confirmToken, string expectedToken)
        {
            if (!commit) return null;
            if (string.IsNullOrWhiteSpace(confirmToken)) return Missing;
            if (!string.Equals(confirmToken, expectedToken, StringComparison.Ordinal)) return Stale;
            return null;
        }

        internal static string Fingerprint(IEnumerable<string> parts)
        {
            var text = string.Join("\n", (parts ?? Enumerable.Empty<string>()).Where(s => s != null));
            if (text.Length == 0) return "";
            return HashBytes(Encoding.UTF8.GetBytes(text));
        }

        internal static string[] ChangeSetFrom(DeployReport report)
        {
            if (report == null) return Array.Empty<string>();
            var parts = new List<string>();
            if (report.SyncedRefs != null) parts.AddRange(report.SyncedRefs.Where(s => !string.IsNullOrEmpty(s)));
            if (!string.IsNullOrEmpty(report.ChangeFingerprint)) parts.Add(report.ChangeFingerprint);
            else if (parts.Count == 0 && report.Changes != null)
                parts.AddRange(report.Changes.Where(s => !string.IsNullOrEmpty(s)));
            return parts.ToArray();
        }
    }
}
