using System.Collections.Generic;
using TabularEditor.TOMWrapper.Utils;

namespace Semanticus.Engine
{
    /// <summary>
    /// Design gap G1 — classify a match offset inside a DAX body as an IDENTIFIER (reference to another object),
    /// a STRING LITERAL, a COMMENT, or other CODE (keyword/operator/number). This is the load-bearing safety
    /// decision for find/replace: an identifier match is a *consequence* of the referenced object's name — poking
    /// it as text half-rewrites the model — so it is routed to a reference-aware rename, never a literal replace.
    /// Only string-literal and comment spans are safe to substitute in place.
    ///
    /// It runs over the SAME ANTLR DAX lexer FormulaFixup/DependsOn use (compiled into Core), so classification and
    /// fixup agree by construction. Purely lexical + offset-based — no model handle, no dispatcher thread needed.
    /// </summary>
    internal static class DaxMatchClassifier
    {
        // MatchClass wire values.
        public const string Reference = "DaxReference"; // 'Table', [Column]/[Measure], bare table/VAR identifier → read-only, rename instead
        public const string Literal   = "DaxLiteral";   // inside a "string literal" → safe literal substitution
        public const string Comment   = "DaxComment";   // inside a // or /* */ comment → safe literal substitution
        public const string Code      = "DaxCode";      // keyword / operator / numeric literal / whitespace → read-only (edit the formula directly)

        /// <summary>Tokenize once; reuse the result to classify many spans of the same expression.</summary>
        public static IList<DaxToken> Tokenize(string dax) => DaxDependencyHelper.Tokenize(dax ?? string.Empty, includeHidden: true);

        /// <summary>
        /// Classify the [start, start+len) span. Conservative on overlap: ANY overlap with an identifier token makes
        /// the whole span a Reference (never literal-replaceable); a span cleanly inside a single string literal or a
        /// single comment is Literal/Comment; anything else (keywords, operators, numbers, or a span straddling token
        /// kinds) is Code (read-only). Erring toward read-only can never corrupt a model — it only declines a replace.
        /// </summary>
        public static string Classify(IList<DaxToken> tokens, int start, int len)
        {
            if (tokens == null || tokens.Count == 0) return Code;
            int end = start + (len <= 0 ? 1 : len);   // exclusive
            bool anyRef = false, anyLiteral = false, anyComment = false, anyOther = false;

            foreach (var tk in tokens)
            {
                // DaxToken StartIndex/StopIndex are INCLUSIVE character offsets into the raw expression.
                if (tk.StartIndex >= end || tk.StopIndex < start) continue;   // no overlap with [start,end)
                switch (tk.Type)
                {
                    case DaxToken.TABLE:              // 'Table Name'
                    case DaxToken.COLUMN_OR_MEASURE:  // [Column] / [Measure]
                    case DaxToken.TABLE_OR_VARIABLE:  // bare Table / a VAR name — identifier either way, never text-poke it
                        anyRef = true; break;
                    case DaxToken.STRING_LITERAL:
                        anyLiteral = true; break;
                    case DaxToken.SINGLE_LINE_COMMENT:
                    case DaxToken.DELIMITED_COMMENT:
                        anyComment = true; break;
                    default:
                        anyOther = true; break;       // keyword / operator / numeric literal / whitespace
                }
            }

            if (anyRef) return Reference;                                   // any identifier overlap wins (safest)
            if (anyLiteral && !anyComment && !anyOther) return Literal;      // wholly inside string literal(s)
            if (anyComment && !anyLiteral && !anyOther) return Comment;      // wholly inside comment(s)
            return Code;                                                     // mixed / keyword / operator / number
        }

        /// <summary>True for the classes replace_in_object will literal-substitute (Literal/Comment). References and
        /// code are refused in the engine.</summary>
        public static bool IsReplaceable(string matchClass) => matchClass == Literal || matchClass == Comment;
    }
}
