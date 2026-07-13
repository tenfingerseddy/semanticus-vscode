using System.Collections.Generic;
using System.Text;

namespace Semanticus.Engine
{
    /// <summary>
    /// The ONE ref grammar for the ALM selective-push lane (ModelCompare ⇄ LiveDeploy), on RAW TOM. Distinct from
    /// the wrapper-side <see cref="ObjectRefs"/> (which operates on the open session's TOMWrapper and documents the
    /// '/'-in-names collision as an accepted limitation): here a ref both SELECTS an object AND resolves the target
    /// of an IRREVERSIBLE live delete, so it MUST round-trip to exactly one (kind, table, name). Object names legally
    /// contain '/' and ':' (Profit/Loss, Price:Unit), so the separators are ESCAPED per component — table A + child
    /// "B/C" and table "A/B" + child C no longer serialize to the same string (which used to tick/delete both).
    ///
    /// Grammar (post-escape): child = "kind:{esc(table)}/{esc(name)}"; top-level = "kind:{esc(name)}";
    /// relationship = "relationship:{RelSig}" where RelSig is compared WHOLE (never re-split), so it carries its own
    /// injective escaping (see EscRel). Clean names (containing none of '\' '/' ':' — and for RelSig also '[' ']')
    /// escape to themselves, so existing refs/tests are byte-for-byte unchanged. The escaping only bites the
    /// pathological names that used to collide: a name containing '\' '/' or ':' produces a different (correct) ref
    /// than the old grammar would have — safe here because the ALM lane has NO persisted refs (the diff is recomputed
    /// on demand and reconciled within one run).
    /// </summary>
    internal static class AlmRef
    {
        // The kinds ModelCompare emits as a table-qualified child "kind:table/name" (split on the first UNESCAPED '/').
        // Every other kind (table/role/perspective/culture/datasource/expression) + relationship keep the whole suffix.
        internal static readonly HashSet<string> TableQualifiedKinds =
            new HashSet<string>(System.StringComparer.Ordinal) { "measure", "column", "partition", "hierarchy" };

        // Escape a name component so ':' and '/' inside it can't forge ref structure. '\' escapes itself.
        internal static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (s.IndexOfAny(EscChars) < 0) return s;   // fast path — clean names escape to themselves
            var sb = new StringBuilder(s.Length + 4);
            foreach (var ch in s)
            {
                if (ch == '\\' || ch == '/' || ch == ':') sb.Append('\\');
                sb.Append(ch);
            }
            return sb.ToString();
        }
        private static readonly char[] EscChars = { '\\', '/', ':' };

        // Reverse Esc — collapse each "\x" back to "x".
        internal static string Unesc(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // A relationship endpoint signature is compared as a WHOLE string (never re-split into parts), so it only needs
        // to be INJECTIVE. Escape the template delimiters '[' ']' (so a name containing them can't forge an endpoint
        // boundary) and ':' (so ParseDeleteRef's kind-split stays unambiguous). That breaks the '->' delimiter too — a
        // part can no longer contain a literal unescaped ']', which is the only way '->' could bleed in.
        internal static string EscRel(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (s.IndexOfAny(RelChars) < 0) return s;
            var sb = new StringBuilder(s.Length + 4);
            foreach (var ch in s)
            {
                if (ch == '\\' || ch == '[' || ch == ']' || ch == ':') sb.Append('\\');
                sb.Append(ch);
            }
            return sb.ToString();
        }
        private static readonly char[] RelChars = { '\\', '[', ']', ':' };

        internal static string Child(string kind, string table, string name) => kind + ":" + Esc(table) + "/" + Esc(name);
        internal static string Top(string kind, string name) => kind + ":" + Esc(name);

        // First occurrence of ch that is NOT escaped (i.e. preceded by an even run of backslashes).
        internal static int IndexOfUnescaped(string s, char ch, int start)
        {
            for (int i = start; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }   // skip the escaped char
                if (s[i] == ch) return i;
            }
            return -1;
        }

        internal static int LastIndexOfUnescaped(string s, char ch)
        {
            int found = -1;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\') { i++; continue; }
                if (s[i] == ch) found = i;
            }
            return found;
        }

        // Parse a ref back to (kind, table, name). Relationship refs keep the ESCAPED signature verbatim (it's compared
        // whole against RelSig, which is likewise escaped — unescaping it would break the compare); every other kind
        // unescapes its component(s). Only the table-qualified kinds split the suffix on the first unescaped '/'.
        internal static (string kind, string table, string name) Parse(string raw)
        {
            var c = IndexOfUnescaped(raw, ':', 0);
            var kind = c < 0 ? "" : raw.Substring(0, c).Trim().ToLowerInvariant();
            var rest = c < 0 ? raw : raw.Substring(c + 1);
            if (kind == "relationship") return (kind, null, rest);   // escaped sig, compared whole
            if (!TableQualifiedKinds.Contains(kind)) return (kind, null, Unesc(rest));
            var slash = IndexOfUnescaped(rest, '/', 0);
            return slash < 0 ? (kind, null, Unesc(rest)) : (kind, Unesc(rest.Substring(0, slash)), Unesc(rest.Substring(slash + 1)));
        }
    }
}
