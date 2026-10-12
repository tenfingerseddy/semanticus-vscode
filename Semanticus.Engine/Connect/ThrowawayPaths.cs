// Which model paths are throwaway, so opening one does not take a place in the saved connections list.
//
// The saved list is capped. Every open used to be added to it, so an agent or a test that opened copies under the
// system temp folder pushed the person's real connections out (measured 27 Sep: 30 of 40 saved rows on the build
// laptop were /tmp paths left by agent and test runs). A copy under a temp folder is by definition not somewhere
// the person will come back to. The open still gets its row in the connection timeline; it just is not SAVED.
//
// The throwaway roots are the process temp folder (Path.GetTempPath, which follows TMPDIR / TEMP) and, off
// Windows, /tmp itself (and /private/tmp, which is what /tmp resolves to on macOS), since a caller's TMPDIR can
// point elsewhere while it still writes copies into /tmp.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Semanticus.Engine
{
    public static class ThrowawayPaths
    {
        /// <summary>Test seam: replaces the throwaway roots. Tests may only write under temp, so a test that needs an
        /// open to be SAVED moves the roots rather than the model. Null in the product.</summary>
        public static IReadOnlyList<string> RootsOverride { get; set; }

        public static IEnumerable<string> Roots()
        {
            if (RootsOverride != null) return RootsOverride;
            var roots = new List<string> { Path.GetTempPath() };
            if (!OperatingSystem.IsWindows()) roots.AddRange(new[] { "/tmp", "/private/tmp" });
            return roots;
        }

        /// <summary>True when <paramref name="path"/> is one of the throwaway roots or anywhere beneath one. A name that
        /// merely starts with a root ("/tmpfiles") is not beneath it.</summary>
        public static bool IsThrowaway(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string full;
            try { full = Trim(Path.GetFullPath(path)); } catch { return false; }
            if (IsLearnCopy(full)) return true;
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var root in Roots().Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                string r;
                try { r = Trim(Path.GetFullPath(root)); } catch { continue; }
                if (string.Equals(full, r, cmp)) return true;
                if (full.StartsWith(r + Path.DirectorySeparatorChar, cmp)) return true;
            }
            return false;
        }

        /// <summary>The extension's scratch Learn copies, including those made by earlier builds.</summary>
        public static bool IsLearnCopy(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir?.Parent != null; dir = dir.Parent)
                    if (string.Equals(dir.Parent.Name, "learn", StringComparison.OrdinalIgnoreCase)
                        && Regex.IsMatch(dir.Name, @"^(plan|ask|edit|review|publish)-\d{8}-\d{6}(-\d+)?$")) return true;
            }
            catch { }
            return false;
        }

        private static string Trim(string p) =>
            p.Length > 1 ? p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : p;
    }
}
