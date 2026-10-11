// Typo-tolerant word matching (Kane's UAT on 25 Sep: he typed "create a meaure" and the word match found nothing).
// Shared by the word match (DraftMenu.Best), which is what a session without Jev uses, and by nothing that asks a
// service: plain string distance over a fixed list, no inference.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Semanticus.Engine.Composer
{
    /// <summary>Typo-tolerant word matching for the word match (DraftMenu.Best).
    /// A word of five letters or more matches with one letter wrong (two from nine letters); a four-letter word only
    /// with two neighbouring letters swapped; anything shorter must be exact. That keeps "rate" off "date".</summary>
    internal static class FuzzyWords
    {
        // A number with a decimal point or thousands commas ("35.8", "1,234.56") is ONE word, so its 8 is never the value 8 (Kane, 8 Oct).
        private static readonly Regex Word = new Regex(@"(?<![\p{L}\p{N}])\p{N}+(?:[.,]\p{N}+)+(?![\p{L}\p{N}])|[\p{L}\p{N}]+", RegexOptions.CultureInvariant);

        public static IEnumerable<(string Word, int At)> Words(string text)
        {
            foreach (Match m in Word.Matches(text ?? ""))
                yield return (m.Value.ToLowerInvariant(), m.Index);
        }

        // The word match tries every keyword of the menu on the same words one after another (DraftMenu.Best, the menu's
        // Hits), so the last words split are kept for the next keyword: a draft at the cap is sixteen thousand words, and
        // splitting them again for each of seventy keywords took seconds on every view (verifier on m11/sol-fixes-1 round
        // 2). Kept per thread and matched by reference, so a different text, even an equal one, is split afresh.
        [ThreadStatic] private static string _splitText;
        [ThreadStatic] private static (string Word, int At)[] _split;

        private static (string Word, int At)[] Split(string text)
        {
            if (!ReferenceEquals(text, _splitText) || _split == null)
            {
                var words = new List<(string, int)>();
                foreach (var w in Words(text)) words.Add(w);
                _split = words.ToArray();
                _splitText = text;
            }
            return _split;
        }

        /// <summary>True when <paramref name="typed"/> is <paramref name="known"/> with a slip of the fingers.</summary>
        public static bool Near(string known, string typed)
        {
            if (known == null || typed == null) return false;
            if (string.Equals(known, typed, StringComparison.Ordinal)) return true;
            if (known.Length < 4 || typed.Length < 4) return false;
            if (known.Length == 4)
                return typed.Length == 4 && Distance(known, typed) == 1 && Swapped(known, typed);
            // A typed word under five letters is taken as meant: "test" is not a slip for "tests".
            if (typed.Length < 5) return false;
            var allowed = known.Length >= 9 ? 2 : 1;
            return Math.Abs(known.Length - typed.Length) <= allowed && Distance(known, typed) <= allowed;
        }

        private static bool Swapped(string a, string b)
        {
            for (var i = 0; i < a.Length - 1; i++)
                if (a[i] != b[i]) return a[i] == b[i + 1] && a[i + 1] == b[i] && a.Substring(i + 2) == b.Substring(i + 2);
            return false;
        }

        /// <summary>Optimal string alignment distance: insert, delete, substitute, or swap two neighbours.</summary>
        public static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) d[0, j] = j;
            for (var i = 1; i <= a.Length; i++)
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                        d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            return d[a.Length, b.Length];
        }

        /// <summary>Where a single-word keyword appears in the clause with a slip, or -1. The keyword may also be the
        /// start of a longer word ("check" in "checks"), the way the exact match reads it.</summary>
        public static int FindNear(string clause, string keyword)
        {
            if (string.IsNullOrEmpty(keyword) || keyword.IndexOf(' ') >= 0) return -1;
            foreach (var (w, at) in Split(clause))
            {
                if (Near(keyword, w)) return at;
                if (w.Length > keyword.Length && Near(keyword, w.Substring(0, keyword.Length)) && keyword.Length >= 5) return at;
            }
            return -1;
        }
    }
}
