using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TabularEditor.TOMWrapper;
using TabularEditor.TOMWrapper.Linguistics;

namespace Semanticus.Engine
{
    /// <summary>
    /// Detailed model search (Phase 1 of Find &amp; Replace): match modes (case / whole-word / regex), a WIDER indexed
    /// surface than the legacy name/desc/expr, and rich per-match results (MatchClass + raw-offset spans) that make
    /// safe, highlight-able replace possible. The old first-match-wins substring search becomes: one hit per matching
    /// (object, field), and — inside a DAX body — one hit per MatchClass so references (read-only) are separated from
    /// literals/comments (replaceable). Deterministic, offline, runs on the dispatcher thread (reads TOM only).
    /// </summary>
    internal static class ModelSearch
    {
        // Field keys (the `field` on a hit, and the values search_model / the UI pass in opts.Fields).
        internal const string FName = "name", FDesc = "description", FExpr = "expression",
            FFolder = "displayFolder", FFormat = "formatString", FRls = "rlsFilter",
            FM = "mExpression", FSyn = "synonyms";

        // The legacy default surface — a 2-arg search_model (and any caller not passing fields) behaves EXACTLY as before.
        private static readonly string[] LegacyFields = { FName, FDesc, FExpr };

        public static SearchResult Run(Model m, SearchOptions o)
        {
            o ??= new SearchOptions();
            var query = (o.Query ?? string.Empty).Trim();
            var res = new SearchResult { Query = query, Offset = Math.Max(0, o.Offset) };
            if (query.Length == 0) return res;

            var matcher = SearchMatcher.Create(query, o.CaseSensitive, o.WholeWord, o.Regex);
            if (matcher.Error != null) { res.Error = matcher.Error; return res; }   // fail-soft: invalid regex → Error, no throw

            int max = o.Max <= 0 ? 100 : o.Max;
            var fields = (o.Fields == null || o.Fields.Length == 0)
                ? new HashSet<string>(LegacyFields, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(o.Fields, StringComparer.OrdinalIgnoreCase);
            var kinds = (o.Kinds == null || o.Kinds.Length == 0) ? null : new HashSet<string>(o.Kinds, StringComparer.OrdinalIgnoreCase);
            ParseScope(o.Scope, out var scopeTable, out var scopeFolder);

            var hits = new List<SearchHit>();
            bool KindOk(TabularNamedObject obj) => kinds == null || kinds.Contains(ObjectRefs.KindOf(obj));
            bool FolderOk(string folder) => scopeFolder == null || string.Equals(folder ?? "", scopeFolder, StringComparison.OrdinalIgnoreCase);

            foreach (var t in m.Tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (scopeTable != null && !string.Equals(t.Name, scopeTable, StringComparison.OrdinalIgnoreCase)) continue;

                if (KindOk(t))
                {
                    Text(hits, matcher, fields, t, null, FName, t.Name);
                    Text(hits, matcher, fields, t, null, FDesc, t.Description);
                }

                foreach (var me in t.Measures)
                {
                    if (!KindOk(me) || !FolderOk(me.DisplayFolder)) continue;
                    Text(hits, matcher, fields, me, t.Name, FName, me.Name);
                    Text(hits, matcher, fields, me, t.Name, FDesc, me.Description);
                    Text(hits, matcher, fields, me, t.Name, FFolder, me.DisplayFolder);
                    Text(hits, matcher, fields, me, t.Name, FFormat, me.FormatString);
                    Dax(hits, matcher, fields, me, t.Name, FExpr, me.Expression);
                    Syn(hits, matcher, fields, m, me, t.Name);
                }
                foreach (var c in t.Columns.Where(c => c.Type != ColumnType.RowNumber))
                {
                    if (!KindOk(c) || !FolderOk(c.DisplayFolder)) continue;
                    Text(hits, matcher, fields, c, t.Name, FName, c.Name);
                    Text(hits, matcher, fields, c, t.Name, FDesc, c.Description);
                    Text(hits, matcher, fields, c, t.Name, FFolder, c.DisplayFolder);
                    Text(hits, matcher, fields, c, t.Name, FFormat, c.FormatString);
                    Dax(hits, matcher, fields, c, t.Name, FExpr, (c as CalculatedColumn)?.Expression);
                    Syn(hits, matcher, fields, m, c, t.Name);
                }
                foreach (var h in t.Hierarchies)
                {
                    if (!KindOk(h) || !FolderOk(h.DisplayFolder)) continue;
                    Text(hits, matcher, fields, h, t.Name, FName, h.Name);
                    Text(hits, matcher, fields, h, t.Name, FDesc, h.Description);
                    Text(hits, matcher, fields, h, t.Name, FFolder, h.DisplayFolder);
                    Syn(hits, matcher, fields, m, h, t.Name);
                }
                if (t is CalculationGroupTable cgt)
                    foreach (var ci in cgt.CalculationItems)
                    {
                        if (!KindOk(ci)) continue;
                        Text(hits, matcher, fields, ci, t.Name, FName, ci.Name);
                        Text(hits, matcher, fields, ci, t.Name, FDesc, ci.Description);
                        Dax(hits, matcher, fields, ci, t.Name, FExpr, ci.Expression);
                    }
                foreach (var p in t.Partitions.Where(p => p.SourceType == PartitionSourceType.M))
                {
                    if (!KindOk(p)) continue;
                    Text(hits, matcher, fields, p, t.Name, FName, p.Name);
                    MExpr(hits, matcher, fields, ObjectRefs.For(p), "partition", p.Name, t.Name, p.Expression);
                }
            }

            foreach (var f in m.Functions.Where(KindOk))
            {
                Text(hits, matcher, fields, f, null, FName, f.Name);
                Text(hits, matcher, fields, f, null, FDesc, f.Description);
                Dax(hits, matcher, fields, f, null, FExpr, f.Expression);
            }
            foreach (var role in m.Roles.Where(KindOk))
            {
                Text(hits, matcher, fields, role, null, FName, role.Name);
                if (fields.Contains(FRls))
                    foreach (var tp in role.TablePermissions.Where(tp => !string.IsNullOrWhiteSpace(tp.FilterExpression)))
                        Rls(hits, matcher, role, tp.Table?.Name, tp.FilterExpression);
            }
            foreach (var p in m.Perspectives.Where(KindOk))
            {
                Text(hits, matcher, fields, p, null, FName, p.Name);
                Text(hits, matcher, fields, p, null, FDesc, p.Description);
            }

            // Shared (named) M expressions — searched only when the M field is requested (opt-in surface).
            if (fields.Contains(FM) && (kinds == null || kinds.Contains("namedexpression")))
                foreach (var e in m.Expressions)
                {
                    if (scopeTable != null) continue;   // named expressions aren't scoped to a table
                    MExpr(hits, matcher, fields, "namedexpr:" + e.Name, "namedexpression", e.Name, null, e.Expression);
                }

            res.Total = hits.Count;
            static FacetCount[] Facets(IEnumerable<IGrouping<string, SearchHit>> gs) =>
                gs.Select(g => new FacetCount { Key = g.Key, Count = g.Count() }).OrderByDescending(f => f.Count).ToArray();
            res.ByField = Facets(hits.GroupBy(h => h.Field));
            res.ByMatchClass = Facets(hits.GroupBy(h => h.MatchClass));
            res.ByKind = Facets(hits.GroupBy(h => h.Kind));
            res.Hits = hits.Skip(res.Offset).Take(max).ToArray();
            res.Truncated = res.Total > res.Offset + res.Hits.Length;
            return res;
        }

        // ---- per-field emitters -------------------------------------------------------------------------------

        // A plain-text field (name / description / folder / format / synonyms) → one hit, MatchClass ObjectName or PlainText.
        private static void Text(List<SearchHit> hits, SearchMatcher matcher, HashSet<string> fields,
            TabularNamedObject obj, string table, string field, string value)
        {
            if (!fields.Contains(field) || string.IsNullOrEmpty(value)) return;
            var spans = matcher.Find(value);
            if (spans.Count == 0) return;
            var cls = field == FName ? "ObjectName" : "PlainText";
            hits.Add(Make(ObjectRefs.For(obj), ObjectRefs.KindOf(obj), obj.Name, table, field, cls, value, spans));
        }

        // A DAX field → tokenize once, then one hit PER MatchClass (references separated from literals/comments/code).
        private static void Dax(List<SearchHit> hits, SearchMatcher matcher, HashSet<string> fields,
            TabularNamedObject obj, string table, string field, string expr)
        {
            if (!fields.Contains(field) || string.IsNullOrEmpty(expr)) return;
            var spans = matcher.Find(expr);
            if (spans.Count == 0) return;
            var tokens = DaxMatchClassifier.Tokenize(expr);
            foreach (var g in spans.GroupBy(s => DaxMatchClassifier.Classify(tokens, s.Start, s.Len)))
                hits.Add(Make(ObjectRefs.For(obj), ObjectRefs.KindOf(obj), obj.Name, table, field, g.Key, expr, g.ToList()));
        }

        // An M / partition / named-expression body → MExpression (literal replace, FormulaFixup does NOT cover M).
        private static void MExpr(List<SearchHit> hits, SearchMatcher matcher, HashSet<string> fields,
            string objRef, string kind, string name, string table, string expr)
        {
            if (!fields.Contains(FM) || string.IsNullOrEmpty(expr)) return;
            var spans = matcher.Find(expr);
            if (spans.Count == 0) return;
            hits.Add(Make(objRef, kind, name, table, FM, "MExpression", expr, spans));
        }

        // An RLS FilterExpression (role table-permission) → DAX-classified, but replace is BLOCKED in v1 (rename only).
        private static void Rls(List<SearchHit> hits, SearchMatcher matcher, ModelRole role, string table, string expr)
        {
            var spans = matcher.Find(expr);
            if (spans.Count == 0) return;
            var tokens = DaxMatchClassifier.Tokenize(expr);
            foreach (var g in spans.GroupBy(s => DaxMatchClassifier.Classify(tokens, s.Start, s.Len)))
            {
                var h = Make("role:" + role.Name, "role", role.Name, null, FRls, g.Key, expr, g.ToList());
                h.Context = table;                              // which table's filter this is (role → table)
                h.Replaceable = false;                          // v1: RLS edits route through rename of the referenced object
                h.ReplaceHint = "This is an RLS security filter. To change a referenced table/column here, rename that object (its references update safely); direct filter replace isn't supported in this version.";
                hits.Add(h);
            }
        }

        // Synonyms (LSDL). Opt-in field: reading requires parsing each culture's linguistic JSON, so we only pay it when
        // requested. Context carries the culture name so a replace edits the right one.
        private static void Syn(List<SearchHit> hits, SearchMatcher matcher, HashSet<string> fields, Model m, TabularNamedObject obj, string table)
        {
            if (!fields.Contains(FSyn)) return;
            foreach (var culture in m.Cultures)
            {
                string terms;
                try { terms = SynonymHelper.GetSynonyms(obj, culture); } catch { continue; }
                if (string.IsNullOrEmpty(terms)) continue;
                var spans = matcher.Find(terms);
                if (spans.Count == 0) continue;
                var h = Make(ObjectRefs.For(obj), ObjectRefs.KindOf(obj), obj.Name, table, FSyn, "PlainText", terms, spans);
                h.Context = culture.Name;
                hits.Add(h);
            }
        }

        private static SearchHit Make(string objRef, string kind, string name, string table,
            string field, string matchClass, string value, List<SearchSpan> spans)
        {
            bool replaceable = matchClass switch
            {
                "ObjectName" => true, "PlainText" => true, "MExpression" => true,
                DaxMatchClassifier.Literal => true, DaxMatchClassifier.Comment => true,
                _ => false,   // DaxReference / DaxCode
            };
            return new SearchHit
            {
                Ref = objRef, Kind = kind, Name = name, Table = table,
                Field = field, Where = LegacyWhere(field), MatchClass = matchClass,
                Spans = spans.ToArray(), Snippet = Snip(value, spans[0]),
                Replaceable = replaceable, ReplaceHint = replaceable ? null : HintFor(matchClass),
            };
        }

        private static string HintFor(string matchClass) => matchClass == DaxMatchClassifier.Reference
            ? "This is a reference to another object. Rename that object (rename_object) to change it everywhere safely — a text replace would break the formula."
            : "This is part of the DAX formula (a function, operator, or number). Edit the expression directly to change it.";

        private static string LegacyWhere(string field) => field switch
        {
            FName => "Name", FDesc => "Description", FExpr => "Expression", FFolder => "DisplayFolder",
            FFormat => "FormatString", FRls => "RLS Filter", FM => "M Expression", FSyn => "Synonyms", _ => field,
        };

        private static void ParseScope(string scope, out string table, out string folder)
        {
            table = folder = null;
            if (string.IsNullOrWhiteSpace(scope)) return;
            var i = scope.IndexOf(':');
            if (i < 0) return;
            var kind = scope.Substring(0, i).Trim().ToLowerInvariant();
            var rest = scope.Substring(i + 1).Trim();
            if (kind == "table") table = rest;
            else if (kind == "folder") folder = rest;
        }

        // A short single-line context window around a match, elided with … on each side (flattened for display).
        private static string Snip(string text, SearchSpan span)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var flat = text.Replace("\r", " ").Replace("\n", " ");
            var i = Math.Min(Math.Max(0, span.Start), Math.Max(0, flat.Length - 1));
            var len = Math.Min(span.Len <= 0 ? 1 : span.Len, flat.Length - i);
            var start = Math.Max(0, i - 24);
            var window = Math.Min(flat.Length - start, len + 56);
            return (start > 0 ? "…" : "") + flat.Substring(start, window) + (start + window < flat.Length ? "…" : "");
        }
    }

    /// <summary>
    /// One matcher for a query, shared by search (find spans) and replace (locate + splice). Modes: literal substring
    /// (case-sensitive optional), whole-word (word-boundary), and regex (validated, with a match TIMEOUT to defuse
    /// catastrophic backtracking). Replacement is applied LITERALLY (no $1 capture expansion in v1 — a deliberate
    /// safety choice; capture-group renames are a documented follow-up).
    /// </summary>
    internal sealed class SearchMatcher
    {
        private const int MaxMatchesPerField = 2000;   // guard a pathological field/pattern
        private readonly string _find;
        private readonly StringComparison _cmp;
        private readonly bool _wholeWord;
        private readonly Regex _rx;                     // non-null iff regex mode
        public string Error { get; private set; }

        private SearchMatcher(string find, StringComparison cmp, bool wholeWord, Regex rx)
        { _find = find; _cmp = cmp; _wholeWord = wholeWord; _rx = rx; }

        public static SearchMatcher Create(string find, bool caseSensitive, bool wholeWord, bool regex)
        {
            var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (!regex) return new SearchMatcher(find, cmp, wholeWord, null);
            try
            {
                var opts = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                var pattern = wholeWord ? $@"(?<!\w)(?:{find})(?!\w)" : find;
                var rx = new Regex(pattern, opts, TimeSpan.FromSeconds(1));
                return new SearchMatcher(find, cmp, wholeWord, rx);
            }
            catch (ArgumentException ex) { return new SearchMatcher(find, cmp, wholeWord, null) { Error = "Invalid regex: " + ex.Message }; }
        }

        /// <summary>All non-overlapping matches in <paramref name="text"/>, left to right.</summary>
        public List<SearchSpan> Find(string text)
        {
            var result = new List<SearchSpan>();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(_find)) return result;
            if (_rx != null)
            {
                try
                {
                    for (var mch = _rx.Match(text); mch.Success && result.Count < MaxMatchesPerField; mch = mch.NextMatch())
                    {
                        if (mch.Length == 0) { if (mch.Index >= text.Length) break; continue; }   // skip zero-width
                        result.Add(new SearchSpan { Start = mch.Index, Len = mch.Length });
                    }
                }
                catch (RegexMatchTimeoutException) { /* return what we have — never hang the door */ }
                return result;
            }
            int from = 0;
            while (from <= text.Length - _find.Length && result.Count < MaxMatchesPerField)
            {
                int i = text.IndexOf(_find, from, _cmp);
                if (i < 0) break;
                if (!_wholeWord || IsWholeWord(text, i, _find.Length)) result.Add(new SearchSpan { Start = i, Len = _find.Length });
                from = i + _find.Length;
            }
            return result;
        }

        /// <summary>Apply the replacement: to a single span if <paramref name="only"/> is given, else every match.
        /// Splices right-to-left so earlier offsets stay valid. Returns the new text; sets <paramref name="count"/>.</summary>
        public string Apply(string text, string replacement, SearchSpan only, out int count)
        {
            count = 0;
            if (text == null) return null;
            var spans = Find(text);
            if (only != null) spans = spans.Where(s => s.Start == only.Start && s.Len == only.Len).ToList();
            if (spans.Count == 0) return text;
            replacement ??= string.Empty;
            var sb = new System.Text.StringBuilder(text);
            foreach (var s in spans.OrderByDescending(s => s.Start))
            {
                if (s.Start < 0 || s.Start + s.Len > sb.Length) continue;
                sb.Remove(s.Start, s.Len);
                sb.Insert(s.Start, replacement);
                count++;
            }
            return sb.ToString();
        }

        /// <summary>Apply the replacement at exactly the given spans (already located, e.g. only the LITERAL/COMMENT
        /// spans of a DAX body — never its reference spans). Splices right-to-left so earlier offsets stay valid.</summary>
        public static string ApplyAt(string text, string replacement, IEnumerable<SearchSpan> spans, out int count)
        {
            count = 0;
            if (text == null || spans == null) return text;
            replacement ??= string.Empty;
            var sb = new System.Text.StringBuilder(text);
            foreach (var s in spans.OrderByDescending(s => s.Start))
            {
                if (s.Start < 0 || s.Len <= 0 || s.Start + s.Len > sb.Length) continue;
                sb.Remove(s.Start, s.Len);
                sb.Insert(s.Start, replacement);
                count++;
            }
            return sb.ToString();
        }

        private static bool IsWholeWord(string text, int start, int len)
        {
            bool before = start == 0 || !IsWordChar(text[start - 1]);
            int end = start + len;
            bool after = end >= text.Length || !IsWordChar(text[end]);
            return before && after;
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
