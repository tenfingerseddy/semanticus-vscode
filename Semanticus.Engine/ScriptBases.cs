using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Semanticus.Engine
{
    /// <summary>
    /// The base of a scripted block (M11 UAT round 4, a redesign of round 3). A script is a copy of objects as they stood
    /// when it was made, and apply writes blocks back, so a block nobody edited used to revert a newer change the other
    /// door had made in between. Each block header carries <c>@base:&lt;hash&gt;</c>: the hash of the block's REF and body
    /// as scripted (line ends trimmed, line endings normalised). Judging is by OBJECT, never by line:
    /// the block still equals its base and the live object moved, so it is left as it is now; the block was edited and the
    /// live object did not move, so it is applied; the block was edited AND the live object moved, so it is a conflict and
    /// nothing is written for it (the person may choose to apply theirs anyway). Lines of two versions are never mixed.
    /// A script with no marker (typed by hand, scripted by an older build, an assistant's own call that never scripted
    /// first) keeps the old behaviour.
    /// </summary>
    internal static class ScriptBases
    {
        private const int Capacity = 512;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string> Refs = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Queue<string> Order = new Queue<string>();

        /// <summary>One header line: <c>// @object &lt;ref&gt;</c> with an optional trailing base marker. A ref may hold spaces.</summary>
        internal static readonly Regex Header = new Regex(@"^//\s*@object\s+(.+?)(?:\s+@base:([0-9a-f]{12}))?\s*$", RegexOptions.Compiled);

        /// <summary>A block as apply parses it: surrounding blank lines and spaces dropped. This is the text that is written.</summary>
        internal static string TrimBlock(string s) => (s ?? string.Empty).Replace("\r\n", "\n").Trim('\n', '\r').Trim();

        /// <summary>A DAX block as it is hashed: line endings normalised and line-end spaces an editor may trim dropped.</summary>
        internal static string NormalizeDax(string s) => NormalizeTmdl(s).Trim();

        /// <summary>A TMDL block as it is hashed: line endings normalised and line-end spaces an editor may trim dropped.</summary>
        internal static string NormalizeTmdl(string s)
            => string.Join("\n", (s ?? string.Empty).Replace("\r\n", "\n").Trim('\n', '\r').Split('\n').Select(l => l.TrimEnd()));

        /// <summary>The hash of a block: its ref and its normalised body, so pointing a header at another object changes it.</summary>
        internal static string Hash(string objRef, string normalizedBody)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes((objRef ?? string.Empty).Trim() + "\n" + (normalizedBody ?? string.Empty)));
            return string.Concat(bytes.Take(6).Select(b => b.ToString("x2")));
        }

        /// <summary>Header line for a scripted block. The ref behind the hash is remembered so a header later pointed at
        /// another object (a formula copied to B) is told apart from an object that moved.</summary>
        internal static string HeaderFor(string objRef, string normalizedBody)
        {
            var h = Hash(objRef, normalizedBody);
            lock (Gate)
            {
                if (!Refs.ContainsKey(h))
                {
                    Refs[h] = objRef.Trim();
                    Order.Enqueue(h);
                    while (Order.Count > Capacity) Refs.Remove(Order.Dequeue());
                }
            }
            return $"// @object {objRef} @base:{h}";
        }

        private static string RefOf(string hash)
        {
            lock (Gate) return Refs.TryGetValue(hash, out var r) ? r : null;
        }

        internal enum Fate { Apply, LeaveAsIs, Conflict, AlreadyThere, Untouched }

        /// <summary>The decision for one block, by object. <paramref name="objRef"/> is the CANONICAL ref of the object the header
        /// resolved to (a header re-spelled in another case still names the same object, so it hashes and compares as that
        /// object); <paramref name="canonical"/> turns the ref a base was made for into its canonical spelling. No base keeps
        /// today's behaviour, and so does a header pointed at a DIFFERENT object than the one scripted (it is the person's
        /// edit for that object, with no base to compare). A block that already equals the live object has nothing to write,
        /// whatever moved: untouched over an object that did not move (nothing to say), or already written over one that did (applying one script twice looks like this). Otherwise: a live object that did not move
        /// is written; one that moved is left when the block is untouched and a conflict when the person edited it.</summary>
        internal static Fate Judge(string baseHash, string objRef, string blockNormalized, string liveNormalized, Func<string, string> canonical = null)
        {
            if (baseHash == null || liveNormalized == null) return Fate.Apply;
            var scriptedAs = RefOf(baseHash);
            if (scriptedAs != null)
            {
                var same = string.Equals(canonical?.Invoke(scriptedAs) ?? scriptedAs, objRef?.Trim(), StringComparison.Ordinal);
                if (!same) return Fate.Apply;
            }
            var unmoved = Hash(objRef, liveNormalized) == baseHash;
            if (string.Equals(blockNormalized, liveNormalized, StringComparison.Ordinal)) return unmoved ? Fate.Untouched : Fate.AlreadyThere;
            if (unmoved) return Fate.Apply;   // nothing moved underneath
            return Hash(objRef, blockNormalized) == baseHash ? Fate.LeaveAsIs : Fate.Conflict;
        }

        /// <summary>Said for a block the person edited that the object already holds, only when the object moved since the script
        /// (their own earlier apply, or the other door arriving at the same text). An untouched block over an unmoved object
        /// says nothing: it is simply not written.</summary>
        internal static string AlreadyNote(string objRef)
            => $"{objRef}: already as you wrote it, so there was nothing to write.";

        internal static string LeftNote(string objRef)
            => $"{objRef}: left as it is now. It changed after you scripted it and you did not edit it.";

        internal static string ConflictNote(string objRef)
            => $"{objRef} changed after you scripted it, and you edited it too. Your version is still in the script; nothing was written for it.";

        internal static string AnywayNote(string objRef)
            => $"{objRef}: your version was written over the change made after you scripted it, as you asked.";

        /// <summary>The refs the person chose to write anyway, or null when none were named (the normal apply).</summary>
        internal static HashSet<string> Anyway(string[] refs, IEnumerable<string> refsInScript, string tool)
        {
            var named = (refs ?? Array.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
            if (named.Count == 0) return null;
            var inScript = new HashSet<string>(refsInScript.Select(r => r?.Trim()), StringComparer.Ordinal);
            var missing = named.Where(r => !inScript.Contains(r)).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"{tool}: applyAnyway names {string.Join(", ", missing)}, which is not a block of this script. Nothing was written. Name only the refs listed in the result's conflicts[].");
            return new HashSet<string>(named, StringComparer.Ordinal);
        }
    }
}
