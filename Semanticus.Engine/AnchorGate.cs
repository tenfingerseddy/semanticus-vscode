using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Semanticus.Engine
{
    // ============================================================================================
    // The expected_values verify kind's PURE decision layer (the enforcement half of the anchor-based
    // verification design). Mirrors EquivalenceGate's shape: a small pure class the workflow executor
    // drives, unit-testable offline with hand-built input. It owns four things — parsing the LOCKED
    // ANCHORS a text input carries (defensively AND strictly: a malformed set, an unknown property,
    // a non-column-ref context key, or a lost value type is surfaced, never guessed — the context
    // key grammar is the injection boundary, so nothing authored ever reaches the query text
    // verbatim), the typed revision-receipt fields, the gold-style value tolerance, and canonical
    // anchor/context fingerprints. The executor (LocalEngine.Workflows) owns the run-level first-set
    // lock, mechanical revision delta checks, QueryData permission boundary, live receipt extracts,
    // and accepted-revision audit. This pure layer never touches a connection.
    // ============================================================================================

    public static class AnchorGate
    {
        /// <summary>Hard cap on anchors per verify — a runaway anchor list must refuse loudly (`unavailable`
        /// naming the cap), never turn one gate into an unbounded query storm.</summary>
        public const int MaxAnchors = 16;
        /// <summary>Hard cap on context (column→value) pairs per anchor.</summary>
        public const int MaxContextPairs = 8;
        /// <summary>Hard cap on shaped-anchor axis columns. This matches the equivalence-grid width cap.</summary>
        public const int MaxAxisColumns = 6;

        /// <summary>One PARSED axis column. Only the unescaped table and column parts survive parsing, so later
        /// stages can rebuild a canonical reference without carrying authored query text across the boundary.</summary>
        public sealed class AnchorColumn
        {
            public string Table { get; set; }
            public string Column { get; set; }
        }

        /// <summary>One PARSED context filter: the strictly-parsed table/column (unescaped; the executor rebinds
        /// them to the model's canonical names) and the exact DAX literal the typed JSON value compiled to. The
        /// query text is always rebuilt from these parts — the authored key/value text itself is never emitted.</summary>
        public sealed class AnchorFilter
        {
            public string Table { get; set; }       // unescaped table name (executor overwrites with the model's canonical casing)
            public string Column { get; set; }      // unescaped column name (ditto)
            public string Literal { get; set; }     // the exact DAX literal emitted: 2023 | "Monday" | TRUE()
            public string ValueLabel { get; set; }  // display form of the authored value
        }

        /// <summary>One locked anchor: a filter CONTEXT (strictly-parsed column refs → typed literals; an empty
        /// context is the grand total) and the EXPECTED value the target measure must produce there. Expect is
        /// exactly one of Number (a finite JSON number), Blank (the string "BLANK"), or Text (any other JSON
        /// string — an EXACT text-valued comparison, see <see cref="Matches"/>).</summary>
        public sealed class Anchor
        {
            public AnchorFilter[] Context { get; set; } = Array.Empty<AnchorFilter>();
            public AnchorColumn[] AxisColumns { get; set; } = Array.Empty<AnchorColumn>();
            public double? Number { get; set; }   // set iff expect was a JSON number (finite — parse rejects non-finite)
            public bool Blank { get; set; }        // set iff expect was "BLANK" (case-insensitive)
            public string Text { get; set; }       // set iff expect was any other JSON string
            // Optional only on a later run-level REVISION. The executor mechanically requires all three receipt
            // fields for every changed expectation, proves original/corrected against the accepted lock, and runs
            // ExtractQuery live before accepting the change.
            public AnchorExpectation OriginalExpect { get; set; }
            public AnchorExpectation CorrectedExpect { get; set; }
            public string ExtractQuery { get; set; }

            /// <summary>A token-lean label for the expected value (payload/receipt text).</summary>
            public string ExpectLabel => Blank ? "BLANK"
                : Number.HasValue ? Number.Value.ToString("R", CultureInfo.InvariantCulture)
                : "\"" + Text + "\"";

            /// <summary>A token-lean label for the filter context ("(grand total)" when empty).</summary>
            public string ContextLabel => Context.Length == 0 ? "(grand total)"
                : string.Join(", ", Context.Select(f => CanonicalRef(f) + "=" + f.ValueLabel));

            /// <summary>True when this anchor carries visual-axis semantics.</summary>
            public bool IsShaped => (AxisColumns?.Length ?? 0) != 0;

            /// <summary>The context entries that identify the visible axis row.</summary>
            public AnchorFilter[] RowCoordinate => ContextPartition(onAxis: true);

            /// <summary>The context entries that slice the visual without becoming row coordinates.</summary>
            public AnchorFilter[] Slicers => ContextPartition(onAxis: false);

            private AnchorFilter[] ContextPartition(bool onAxis)
            {
                var axisKeys = new HashSet<string>((AxisColumns ?? Array.Empty<AnchorColumn>()).Select(ReferenceKey), StringComparer.Ordinal);
                return (Context ?? Array.Empty<AnchorFilter>())
                    .Where(f => axisKeys.Contains(ReferenceKey(f)) == onAxis)
                    .ToArray();
            }
        }

        public sealed class AnchorExpectation
        {
            public double? Number { get; set; }
            public bool Blank { get; set; }
            public string Text { get; set; }
            public string Label => Blank ? "BLANK"
                : Number.HasValue ? Number.Value.ToString("R", CultureInfo.InvariantCulture)
                : "\"" + Text + "\"";
        }

        /// <summary>The canonically ESCAPED reference for a filter — always rebuilt from the parsed parts
        /// (QuoteTable doubles ', BracketName doubles ]), never the authored text.</summary>
        public static string CanonicalRef(AnchorFilter f) => DaxBench.QuoteTable(f.Table) + DaxBench.BracketName(f.Column);

        /// <summary>The canonically escaped reference for a parsed axis column.</summary>
        public static string CanonicalRef(AnchorColumn c) => DaxBench.QuoteTable(c.Table) + DaxBench.BracketName(c.Column);

        private static string ReferenceKey(AnchorFilter f) => ReferenceKey(f.Table, f.Column);
        private static string ReferenceKey(AnchorColumn c) => ReferenceKey(c.Table, c.Column);
        private static string ReferenceKey(string table, string column) =>
            (table ?? "").ToUpperInvariant() + "\n" + (column ?? "").ToUpperInvariant();

        /// <summary>Parse the anchors a text input carries — a fenced ```json array (or a bare JSON array) of
        /// {context, optional axis, expect} objects. DEFENSIVE AND STRICT by contract: malformed JSON, an unknown or duplicated
        /// property (a "contex" typo must never become a silent grand-total anchor), a context key that is not a
        /// pure qualified column ref, a non-scalar/non-finite value, or a breached cap sets <paramref name="error"/>
        /// (the caller refuses the verify as `unavailable`, naming the defect) and returns null — the enforcement
        /// half never guesses. An empty array parses clean (the caller treats zero anchors as `unavailable`).</summary>
        public static Anchor[] Parse(string raw, out string error)
        {
            error = null;
            var json = ExtractJson(raw);
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "the anchors input carried no JSON. Author it as a fenced ```json array of {\"context\": {\"'Table'[Column]\": value}, \"expect\": number|\"BLANK\"|string}.";
                return null;
            }
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (Exception ex)
            {
                error = "the anchors JSON did not parse (" + ex.Message + "). Author it as a JSON array of {\"context\": {\"'Table'[Column]\": value}, \"expect\": number|\"BLANK\"|string}.";
                return null;
            }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    error = $"the anchors JSON must be an ARRAY of {{context, expect}} objects (got {doc.RootElement.ValueKind.ToString().ToLowerInvariant()}).";
                    return null;
                }
                var list = new List<Anchor>();
                var i = 0;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    i++;
                    if (i > MaxAnchors)
                    {
                        error = $"the anchor set exceeds the cap of {MaxAnchors} anchors per verify. Split the proof or keep the strongest {MaxAnchors}.";
                        return null;
                    }
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        error = $"anchor #{i} is not an object. Each anchor is {{\"context\": {{...}}, \"expect\": ...}}.";
                        return null;
                    }

                    // STRICT property surface: the ordinary anchor fields plus the three mechanically-enforced
                    // revision-receipt fields, each at most once. Anything else (a "contex" typo, a stray key) must
                    // refuse — a mis-spelled context would otherwise silently become a grand-total anchor and the
                    // gate would enforce the wrong thing.
                    var seenProps = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var p in el.EnumerateObject())
                    {
                        if (!seenProps.Add(p.Name))
                        {
                            error = $"anchor #{i} declares '{p.Name}' more than once.";
                            return null;
                        }
                        if (p.Name != "context" && p.Name != "axis" && p.Name != "expect"
                            && p.Name != "originalExpect" && p.Name != "correctedExpect" && p.Name != "extractQuery")
                        {
                            error = $"anchor #{i} has an unknown property '{p.Name}'. Only 'context', 'axis', 'expect', 'originalExpect', 'correctedExpect', and 'extractQuery' are allowed. Remove the property or correct the typo because it changes what is enforced.";
                            return null;
                        }
                    }

                    var a = new Anchor();
                    if (el.TryGetProperty("context", out var ctx))
                    {
                        if (ctx.ValueKind != JsonValueKind.Object)
                        {
                            error = $"anchor #{i} 'context' must be an object of column→value pairs (an empty {{}} = the grand total).";
                            return null;
                        }
                        var filters = new List<AnchorFilter>();
                        var seenRefs = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var p in ctx.EnumerateObject())
                        {
                            if (filters.Count >= MaxContextPairs)
                            {
                                error = $"anchor #{i} exceeds the cap of {MaxContextPairs} context pairs per anchor.";
                                return null;
                            }
                            // THE INJECTION BOUNDARY: the key must be a PURE qualified column ref — 'Table'[Column]
                            // or Table[Column] with ''/]] escapes, nothing before or after. Anything else (a filter
                            // expression, a trailing comment, an operator) is refused by grammar, and the emitted
                            // query is rebuilt from the parsed parts, never this text.
                            if (!TryParseColumnRef(p.Name, out var tbl, out var col))
                            {
                                error = $"anchor #{i} context key '{p.Name}' is not a qualified column reference. Use exactly 'Table'[Column] (or Table[Column]); '' escapes a quote in the table name, ]] escapes a bracket in the column name. Nothing else is accepted.";
                                return null;
                            }
                            // DAX names are case-insensitive: a re-cased duplicate is the same column twice.
                            if (!seenRefs.Add(ReferenceKey(tbl, col)))
                            {
                                error = $"anchor #{i} context declares column '{p.Name}' more than once.";
                                return null;
                            }
                            // TYPED value → exact DAX literal (B2: the JSON type is the contract, never re-guessed):
                            //   JSON number  ⇒ its invariant numeral, emitted bare (JSON's number grammar is a
                            //                  subset of DAX's numeric-literal grammar);
                            //   JSON string  ⇒ a quoted DAX string ALWAYS — "2023" stays a STRING filter (a numeric
                            //                  column needs the JSON number 2023); "1,234" is a string, never bad DAX;
                            //   JSON boolean ⇒ TRUE()/FALSE().
                            // Date-typed columns take the string form (quoted) — DAX coerces the literal; the
                            // executor notes the coercion caveat in the payload when the resolved column is a date.
                            string literal, label;
                            switch (p.Value.ValueKind)
                            {
                                case JsonValueKind.Number:
                                    // TryGetDouble + finite: an overflowing numeral (1e999) must refuse at parse,
                                    // never throw out of this method or ride into the query as a nonsense literal.
                                    if (!p.Value.TryGetDouble(out var dv) || !double.IsFinite(dv))
                                    {
                                        error = $"anchor #{i} context['{p.Name}'] is not a finite number.";
                                        return null;
                                    }
                                    literal = p.Value.GetRawText(); label = literal; break;
                                case JsonValueKind.String:
                                    var sv = p.Value.GetString() ?? "";
                                    literal = "\"" + sv.Replace("\"", "\"\"") + "\""; label = "\"" + sv + "\""; break;
                                case JsonValueKind.True:
                                    literal = "TRUE()"; label = "TRUE"; break;
                                case JsonValueKind.False:
                                    literal = "FALSE()"; label = "FALSE"; break;
                                default:
                                    error = $"anchor #{i} context['{p.Name}'] must be a string, number, or boolean filter value (got {p.Value.ValueKind.ToString().ToLowerInvariant()}).";
                                    return null;
                            }
                            filters.Add(new AnchorFilter { Table = tbl, Column = col, Literal = literal, ValueLabel = label });
                        }
                        a.Context = filters.ToArray();
                    }

                    if (el.TryGetProperty("axis", out var axis))
                    {
                        if (axis.ValueKind != JsonValueKind.Array)
                        {
                            error = $"anchor #{i} 'axis' must be an array of 1 to {MaxAxisColumns} qualified column-reference strings. Use exactly 'Table'[Column] for each entry, or omit 'axis' for a flat anchor.";
                            return null;
                        }
                        if (axis.GetArrayLength() == 0)
                        {
                            error = $"anchor #{i} 'axis' must contain 1 to {MaxAxisColumns} qualified column references. Omit 'axis' for a flat anchor.";
                            return null;
                        }
                        if (axis.GetArrayLength() > MaxAxisColumns)
                        {
                            error = $"anchor #{i} 'axis' exceeds the cap of {MaxAxisColumns} columns. Keep at most {MaxAxisColumns} axis columns.";
                            return null;
                        }

                        var columns = new List<AnchorColumn>();
                        var seenAxisRefs = new HashSet<string>(StringComparer.Ordinal);
                        var contextRefs = new HashSet<string>(a.Context.Select(ReferenceKey), StringComparer.Ordinal);
                        var axisIndex = 0;
                        foreach (var entry in axis.EnumerateArray())
                        {
                            axisIndex++;
                            if (entry.ValueKind != JsonValueKind.String)
                            {
                                error = $"anchor #{i} axis entry #{axisIndex} must be a string containing exactly 'Table'[Column]. Replace it with a qualified column-reference string.";
                                return null;
                            }
                            var authoredRef = entry.GetString();
                            if (!TryParseColumnRef(authoredRef, out var tbl, out var col))
                            {
                                error = $"anchor #{i} axis entry #{axisIndex} is not a qualified column reference. Use exactly 'Table'[Column] (or Table[Column]); '' escapes a quote in the table name and ]] escapes a bracket in the column name. Nothing else is accepted.";
                                return null;
                            }
                            var parsed = new AnchorColumn { Table = tbl, Column = col };
                            var refKey = ReferenceKey(parsed);
                            if (!seenAxisRefs.Add(refKey))
                            {
                                error = $"anchor #{i} axis declares column '{CanonicalRef(parsed)}' more than once. Remove duplicate axis entries.";
                                return null;
                            }
                            if (!contextRefs.Contains(refKey))
                            {
                                error = $"anchor #{i} axis column '{CanonicalRef(parsed)}' is not a key of 'context'. Add that column and its row-coordinate value to 'context', or remove it from 'axis'.";
                                return null;
                            }
                            columns.Add(parsed);
                        }
                        a.AxisColumns = columns.ToArray();
                    }

                    if (!el.TryGetProperty("expect", out var ex2))
                    {
                        error = $"anchor #{i} is missing 'expect' (the value the measure must produce at that context).";
                        return null;
                    }
                    if (!TryParseExpectation(ex2, $"anchor #{i} 'expect'", out var parsedExpect, out error))
                        return null;
                    a.Number = parsedExpect.Number;
                    a.Blank = parsedExpect.Blank;
                    a.Text = parsedExpect.Text;

                    if (el.TryGetProperty("originalExpect", out var original))
                    {
                        if (!TryParseExpectation(original, $"anchor #{i} 'originalExpect'", out var parsedOriginal, out error))
                            return null;
                        a.OriginalExpect = parsedOriginal;
                    }
                    if (el.TryGetProperty("correctedExpect", out var corrected))
                    {
                        if (!TryParseExpectation(corrected, $"anchor #{i} 'correctedExpect'", out var parsedCorrected, out error))
                            return null;
                        a.CorrectedExpect = parsedCorrected;
                    }
                    if (el.TryGetProperty("extractQuery", out var extractQuery))
                    {
                        if (extractQuery.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(extractQuery.GetString()))
                        {
                            error = $"anchor #{i} 'extractQuery' must be a non-empty DAX query string.";
                            return null;
                        }
                        a.ExtractQuery = extractQuery.GetString().Trim();
                    }

                    // A SHAPED anchor is proven at a VISIBLE row of a single-measure visual, and such a visual never
                    // renders a row whose only measure is BLANK (SUMMARIZECOLUMNS prunes it). So a BLANK expectation
                    // is not provable at a visible row coordinate — refuse it here, at the parser boundary every anchor
                    // acceptance flows through. The rule: NEITHER expect NOR a declared correctedExpect may be BLANK
                    // on a shaped anchor. Evaluation reads the expect-derived fields (Matches consumes Blank/Number/
                    // Text) while the revision receipt requires correctedExpect == expect, so a BLANK in either field
                    // is a false pass or an inconsistent receipt — a legal shaped anchor never carries one. The one
                    // sanctioned BLANK, originalExpect (the historical record of a receipted flat-to-shaped repair),
                    // is deliberately left alone. A flat anchor (no axis) keeps its v6 BLANK behavior — a flat anchor
                    // is exactly how blankness is proven.
                    if (a.IsShaped && (a.Blank || a.CorrectedExpect?.Blank == true))
                    {
                        error = $"anchor #{i}: a shaped anchor cannot expect BLANK. A visual does not render a row whose only measure is BLANK, so a BLANK expectation is not provable at a visible row coordinate. Prove blankness with a flat anchor (no axis) at the same context.";
                        return null;
                    }
                    list.Add(a);
                }
                return list.ToArray();
            }
        }

        private static bool TryParseExpectation(JsonElement value, string field, out AnchorExpectation parsed, out string error)
        {
            parsed = new AnchorExpectation();
            error = null;
            switch (value.ValueKind)
            {
                case JsonValueKind.Number:
                    if (!value.TryGetDouble(out var num) || !double.IsFinite(num))
                    {
                        error = field + " is not a finite number.";
                        return false;
                    }
                    parsed.Number = num;
                    return true;
                case JsonValueKind.String:
                    var str = value.GetString();
                    if (string.Equals(str?.Trim(), "BLANK", StringComparison.OrdinalIgnoreCase)) parsed.Blank = true;
                    else parsed.Text = str;
                    return true;
                default:
                    error = field + " must be a number, the string \"BLANK\", or a string value (got "
                        + value.ValueKind.ToString().ToLowerInvariant() + ").";
                    return false;
            }
        }

        /// <summary>Strictly parse a PURE qualified column reference: <c>'Table'[Column]</c> (with '' escaping a
        /// quote) or <c>Table[Column]</c> (a bareword identifier), optional whitespace between table and bracket,
        /// and NOTHING else — the whole string must be consumed. This is the grammar gate that keeps authored
        /// context keys out of the query text (a filter expression, a trailing comment, or any operator fails
        /// here). Returns the UNESCAPED table/column names.</summary>
        internal static bool TryParseColumnRef(string raw, out string table, out string column)
        {
            table = null; column = null;
            var s = (raw ?? "").Trim();
            int i = 0, n = s.Length;
            if (n == 0) return false;

            var t = new StringBuilder();
            if (s[0] == '\'')
            {
                i = 1;
                var closed = false;
                while (i < n)
                {
                    if (s[i] == '\'')
                    {
                        if (i + 1 < n && s[i + 1] == '\'') { t.Append('\''); i += 2; continue; }
                        i++; closed = true; break;
                    }
                    t.Append(s[i]); i++;
                }
                if (!closed || t.Length == 0) return false;
            }
            else
            {
                if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) { t.Append(s[i]); i++; }
            }

            while (i < n && char.IsWhiteSpace(s[i])) i++;
            if (i >= n || s[i] != '[') return false;
            i++;
            var c = new StringBuilder();
            var closedCol = false;
            while (i < n)
            {
                if (s[i] == ']')
                {
                    if (i + 1 < n && s[i + 1] == ']') { c.Append(']'); i += 2; continue; }
                    i++; closedCol = true; break;
                }
                c.Append(s[i]); i++;
            }
            if (!closedCol || c.Length == 0) return false;
            if (i != n) return false;   // trailing text (an operator, a comment, a second ref) = not a pure column ref

            table = t.ToString(); column = c.ToString();
            return true;
        }

        /// <summary>Pull the JSON array out of a text answer: a ```json (or bare ```) fenced block if present,
        /// else the whole trimmed text. Only the FIRST fence is honored — an agent authors one anchor block.</summary>
        private static string ExtractJson(string raw)
        {
            var s = (raw ?? "").Trim();
            var fence = s.IndexOf("```", StringComparison.Ordinal);
            if (fence < 0) return s;
            var afterOpen = s.IndexOf('\n', fence);
            if (afterOpen < 0) return "";                    // a lone "```..." with no body
            var body = s.Substring(afterOpen + 1);
            var close = body.IndexOf("```", StringComparison.Ordinal);
            return (close >= 0 ? body.Substring(0, close) : body).Trim();
        }

        /// <summary>Compile ONE parsed context filter into a SARGable CALCULATE column predicate. The reference is
        /// REBUILT canonically escaped from the parsed table/column (never the authored key text) and the literal
        /// is the typed compilation from <see cref="Parse"/> — each is a point predicate the engine can push to
        /// storage, so evaluation should be milliseconds.</summary>
        public static string CompileContextFilter(AnchorFilter f) => CanonicalRef(f) + " = " + f.Literal;

        /// <summary>Gold-style value equality: numbers match within ABS 1e-6 OR REL 1e-9 (below magnitude ~1000 the
        /// absolute band dominates — deliberately, matching the gold scorer); non-finite values (NaN/±Inf) on EITHER
        /// side never match; a BLANK expectation matches ONLY a blank (null) actual, and a non-blank expectation
        /// never matches a blank; a TEXT expectation is an EXACT text-valued comparison — the actual must itself be
        /// text (a numeric actual against a string expect is a type mismatch, named in the label; formatted-display
        /// matching is deliberately not done — locale quicksand). <paramref name="actualLabel"/> receives the
        /// actual's display form ("(blank)" for null) for the evidence payload.</summary>
        public static bool Matches(Anchor a, object actual, out string actualLabel)
        {
            actualLabel = DaxBench.Fmt(actual);   // "(blank)" for null
            var isBlank = actual == null;

            if (a.Blank) return isBlank;
            if (isBlank) return false;            // a concrete expectation can never match a blank actual

            if (a.Number.HasValue)
            {
                if (!IsNumeric(actual))
                {
                    actualLabel += " (a " + TypeLabel(actual) + ", not a number)";
                    return false;
                }
                var da = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
                var db = a.Number.Value;
                // Non-finite never matches: ∞ <= ∞ would satisfy any tolerance test, so an infinite/NaN actual
                // (or expectation, however constructed) is rejected BEFORE the bands.
                if (!double.IsFinite(da) || !double.IsFinite(db)) return false;
                var diff = Math.Abs(da - db);
                var scale = Math.Max(Math.Abs(da), Math.Abs(db));
                return diff <= 1e-6 || diff <= scale * 1e-9;
            }

            // TEXT expect: an exact string comparison against a TEXT-valued actual only.
            if (actual is string str) return string.Equals(str, a.Text ?? "", StringComparison.Ordinal);
            actualLabel += " (a " + TypeLabel(actual) + ", not text)";
            return false;
        }

        private static string TypeLabel(object o) =>
            IsNumeric(o) ? "number" : o is DateTime ? "date" : o is bool ? "boolean" : o is string ? "text" : o.GetType().Name.ToLowerInvariant();

        private static bool IsNumeric(object o) =>
            o is byte || o is sbyte || o is short || o is ushort || o is int || o is uint
            || o is long || o is ulong || o is float || o is double || o is decimal;

        /// <summary>Canonical identity of an anchor context and shape, independent of authored order and reference
        /// casing. A flat anchor retains the exact v6 context encoding; only a shaped anchor emits an axis component.</summary>
        internal static string CanonicalContextKey(Anchor anchor)
        {
            var context = (anchor?.Context ?? Array.Empty<AnchorFilter>())
                .Select(f => new { r = CanonicalRef(f).ToUpperInvariant(), v = f.Literal })
                .OrderBy(x => x.r, StringComparer.Ordinal)
                .ToArray();
            if (anchor?.IsShaped != true)
                return JsonSerializer.Serialize(context);

            var axis = anchor.AxisColumns
                .Select(c => CanonicalRef(c).ToUpperInvariant())
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToArray();
            return JsonSerializer.Serialize(new { c = context, a = axis });
        }

        /// <summary>Typed expectation identity. A numeric 1 and text "1" are deliberately different.</summary>
        internal static string ExpectationKey(Anchor anchor) => anchor == null ? null
            : anchor.Blank ? "BLANK"
            : anchor.Number.HasValue ? "N:" + anchor.Number.Value.ToString("R", CultureInfo.InvariantCulture)
            : "S:" + (anchor.Text ?? "");

        internal static string ExpectationKey(AnchorExpectation expectation) => expectation == null ? null
            : expectation.Blank ? "BLANK"
            : expectation.Number.HasValue ? "N:" + expectation.Number.Value.ToString("R", CultureInfo.InvariantCulture)
            : "S:" + (expectation.Text ?? "");

        /// <summary>The canonical fingerprint of an anchor SET — the anti-laundering key. Serializes the TYPED,
        /// PARSED execution representation as deterministic JSON (refs canonically escaped and case-folded — DAX
        /// names are case-insensitive; values as the EXACT emitted literals; context keys sorted; the anchors
        /// themselves sorted), then SHA-256 over that. JSON escaping makes the encoding injective (no delimiter
        /// collisions: a value containing ';' or '=' can never collide with a two-key set), a pure reorder /
        /// re-format / re-case is NOT a change, and any real context or expected-value edit IS. Lower-hex.</summary>
        public static string CanonicalHash(Anchor[] anchors)
        {
            var canon = (anchors ?? Array.Empty<Anchor>())
                .Select(a =>
                {
                    var context = a.Context
                        .Select(f => new
                        {
                            r = CanonicalRef(f).ToUpperInvariant(),
                            v = f.Literal,
                        })
                        .OrderBy(x => x.r, StringComparer.Ordinal)
                        .ToArray();
                    if (!a.IsShaped)
                    {
                        // Keep this exact c/e object shape for byte-identical v6 flat-anchor fingerprints.
                        return JsonSerializer.Serialize(new
                        {
                            c = context,
                            e = ExpectationKey(a),
                        });
                    }

                    var axis = a.AxisColumns
                        .Select(c => CanonicalRef(c).ToUpperInvariant())
                        .OrderBy(r => r, StringComparer.Ordinal)
                        .ToArray();
                    return JsonSerializer.Serialize(new
                    {
                        c = context,
                        a = axis,
                        e = ExpectationKey(a),
                    });
                })
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canon)))).ToLowerInvariant();
        }

        /// <summary>Legacy per-verify ANCHOR-SET LOCK retained for compatibility with existing state/tests. Production
        /// expected_values execution now uses the stronger run-level lock and live receipt validation. This helper
        /// mirrors EquivalenceGate.RegisterPartition: lock the canonical
        /// fingerprint for this verify at the first actually-run evaluation; a later submission whose fingerprint
        /// DIFFERS appends an {before, after, stepId, timestamp} revision receipt, re-locks the NEW set, and returns
        /// the block reason (the caller refuses THAT submission as `unavailable`) — so the next submission evaluates
        /// fresh under the new locked anchors while the receipt and prior verify results stay on the run record
        /// (evidence is never erased). Closes the laundering path: author anchors, see the measure fail them,
        /// silently re-submit with the anchors edited to match the wrong actual. Returns null when the set is
        /// unchanged (or first seen). Caller holds _workflowGate. <paramref name="verifyIndex"/> is the verify's
        /// ordinal within its step, so two same-step anchor verifies never share one lock.</summary>
        public static string RegisterAnchorSet(WorkflowRunState run, string stepId, int verifyIndex, string anchorsInput, string canonicalHash)
        {
            var key = stepId + "|" + verifyIndex.ToString(CultureInfo.InvariantCulture) + "|" + (anchorsInput ?? "");
            if (!run.AnchorLocks.TryGetValue(key, out var locked)) { run.AnchorLocks[key] = canonicalHash; return null; }
            if (string.Equals(locked, canonicalHash, StringComparison.Ordinal)) return null;
            run.AnchorRevisions.Add(new AnchorRevision
            {
                Key = key,
                BeforeHash = locked, AfterHash = canonicalHash,
                StepId = stepId, TimestampUtc = DateTime.UtcNow.ToString("o"),
            });
            run.AnchorLocks[key] = canonicalHash;   // the NEXT submission evaluates fresh under the new locked set
            return "the locked anchor set changed after evidence was seen (its expected-value fingerprint no longer matches the set first evaluated). An anchor-revision receipt was recorded and the prior verify results stay on the run; re-submit to evaluate fresh under the new anchor set.";
        }
    }
}
