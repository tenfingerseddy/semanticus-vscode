using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>
    /// The three honest sentences for a live preview. "Already matches" is only true when both
    /// directions of the diff are empty. Extra objects on the live model are named, never hidden.
    /// </summary>
    internal static class LiveMatchCopy
    {
        public static string ForPreview(string database, int totalChanges, IReadOnlyCollection<string> liveOnly)
        {
            if (totalChanges > 0) return null;
            var extras = (liveOnly ?? Array.Empty<string>()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
            return extras.Length == 0 ? Empty(database) : LiveOnly(database, extras);
        }

        public static string Empty(string database) =>
            "Nothing to publish. " + Name(database) + " already has everything in your copy.";

        public static string LiveOnly(string database, IReadOnlyList<string> liveOnly)
        {
            var n = liveOnly == null ? 0 : liveOnly.Count;
            if (n == 0) return Empty(database);
            var shown = liveOnly.Take(12).Select(ShortName).ToArray();
            var names = string.Join(", ", shown);
            var more = n > 12 ? " and " + (n - 12) + " more" : "";
            var obj = n == 1 ? "1 object is" : n + " objects are";
            var tick = n == 1
                ? "Publishing never removes it unless you tick it."
                : "Publishing never removes them unless you tick them.";
            return "Nothing new to publish. " + obj + " on " + Name(database)
                + " that your copy does not have: " + names + more + ". " + tick;
        }

        public static string PreviewFailed(string database, string reason) =>
            "Could not check " + Name(database) + " for changes: " + (reason ?? "unknown error") + ". Sign in and try again.";

        public static string DeleteRefusedNoRestore(string reason) =>
            "Delete refused: a restore point could not be written (" + (reason ?? "unknown error")
            + "), and a delete on a published model cannot be undone without one. Nothing was written. Untick the deletions to publish the rest, or free some disk space and retry.";

        public static string ShortName(string liveOnlyRef)
        {
            if (string.IsNullOrEmpty(liveOnlyRef)) return liveOnlyRef;
            var r = liveOnlyRef;
            var extra = r.IndexOf(" (live-only", StringComparison.Ordinal);
            if (extra >= 0) r = r.Substring(0, extra);
            var colon = AlmRef.IndexOfUnescaped(r, ':', 0);
            var rest = colon < 0 ? r : r.Substring(colon + 1);
            var slash = AlmRef.LastIndexOfUnescaped(rest, '/');
            var name = slash < 0 ? rest : rest.Substring(slash + 1);
            return AlmRef.Unesc(name);
        }

        public static string[] NormalizeDeleteRefs(IEnumerable<string> deleteRefs) =>
            (deleteRefs ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

        private static string Name(string database) =>
            string.IsNullOrWhiteSpace(database) ? "The live model" : database.Trim();
    }
}
