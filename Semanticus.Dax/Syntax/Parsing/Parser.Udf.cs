using Semanticus.Dax.Syntax.Green;
using Semanticus.Dax.Text;

namespace Semanticus.Dax.Syntax.Parsing;

// A2 §4.9 and §4.9.1, the model-UDF lambda-body root.

internal sealed partial class Parser
{
    private GreenSyntaxNode ParseLambda()
    {
        var opened = TryOpenProduction();
        try
        {
            var parameterList = ParseParameterList();
            var arrow = Expect(SyntaxKind.LambdaArrowToken, DaxParserDiagnosticCodes.LambdaArrowExpected);
            var body = ParseExpression();
            return new GreenLambdaExpression(parameterList, arrow, body);
        }
        finally
        {
            if (opened) CloseProduction();
        }
    }

    /// <summary>
    /// A missing opening <c>(</c> inserts a zero-width one, emits exactly one <c>DAXP1068</c>, and then
    /// parses the parameter list FROM THAT SAME TOKEN: nothing is skipped and nothing is looked ahead at
    /// (A2 §4.9). So <c>x) =&gt; x</c> is the ordinary shape with <c>DAXP1068</c> as its only diagnostic,
    /// and <c>x =&gt; x</c> adds the ordinary missing-<c>)</c> rule on top, one each.
    /// </summary>
    private GreenSyntaxNode ParseParameterList()
    {
        var open = CurrentKind == SyntaxKind.OpenParenToken
            ? Eat()
            : InsertMissingToken(SyntaxKind.OpenParenToken, DaxParserDiagnosticCodes.ParameterListOpenParenExpected);

        var savedResync = _resync;
        _resync = Resync.UdfParameters;
        _parenDepth++;
        _listDepth++;
        try
        {
            var parameters = new List<GreenNode>();
            var separators = new List<GreenToken>();

            if (CurrentKind != SyntaxKind.CloseParenToken)
            {
                while (true)
                {
                    parameters.Add(ParseParameter());

                    if (CurrentKind is not (SyntaxKind.CommaToken or SyntaxKind.CloseParenToken))
                        SkipUnexpectedTokens(DaxParserDiagnosticCodes.UnexpectedTokensSkipped);

                    if (CurrentKind != SyntaxKind.CommaToken) break;
                    separators.Add(Eat());
                }
            }

            var close = Expect(SyntaxKind.CloseParenToken, DaxParserDiagnosticCodes.CloseParenExpected);
            return new GreenParameterList(open, PollingSeparatedList(parameters, separators), close);
        }
        finally
        {
            // NO CloseProduction here. This method never calls TryOpenProduction: the lambda's single
            // production is opened by ParseLambda and closed there. Closing it a second time handed the
            // lambda BODY one level more than MaximumNestingDepth allows and left the counter at -1 for
            // every UDF parse (A2PR1-S1). The caret guard above reads that same counter, so an inexact
            // counter is a silent hole in the guard rather than a cosmetic off-by-one.
            _listDepth--;
            _parenDepth--;
            _resync = savedResync;
        }
    }

    private GreenSyntaxNode ParseParameter()
    {
        var identifier = ParseParameterName();

        GreenSyntaxNode? typeClause = CurrentKind == SyntaxKind.ColonToken ? ParseParameterTypeClause() : null;

        // Within a parameter, '=' can only introduce a default: there is no comparison context here.
        GreenSyntaxNode? defaultValue = null;
        if (CurrentKind == SyntaxKind.EqualsToken)
        {
            var equals = Eat();
            defaultValue = new GreenDefaultValueClause(equals, ParseExpression());
        }

        return new GreenParameter(identifier, typeClause, defaultValue);
    }

    /// <summary>
    /// A reserved keyword is RETAINED here with <c>DAXP1069</c> (Error) so the parameter survives; retained
    /// is not accepted, and the token's kind is never rewritten (A2 §6.2 rule 4, §6.3 P6).
    /// </summary>
    private GreenToken ParseParameterName()
    {
        var kind = CurrentKind;

        if (kind == SyntaxKind.IdentifierToken && PeekKind(1) != SyntaxKind.DotToken) return Eat();

        if (DaxSyntaxFacts.IsKeyword(kind))
        {
            var span = CurrentRaw.Span;
            return (GreenToken)Eat().WithAdditionalDiagnostics(
                Diagnostic(DaxParserDiagnosticCodes.KeywordNotPermittedAsParameterName, span));
        }

        if (kind is SyntaxKind.QuotedIdentifierToken or SyntaxKind.BracketedIdentifierToken or SyntaxKind.DotToken ||
            (kind == SyntaxKind.IdentifierToken && PeekKind(1) == SyntaxKind.DotToken))
        {
            // A2 §4.9: a quoted, bracketed, or dotted spelling yields a missing identifier plus DAXP1061,
            // with the offending tokens becoming skipped-token trivia. The diagnostic is anchored on the
            // insertion point before anything moves, per A2 §8.2's span column.
            var missing = InsertMissingToken(SyntaxKind.IdentifierToken, DaxParserDiagnosticCodes.ParameterNameExpected);
            while (CurrentKind is SyntaxKind.QuotedIdentifierToken or SyntaxKind.BracketedIdentifierToken or
                   SyntaxKind.DotToken or SyntaxKind.IdentifierToken)
                SkipCurrent();
            return missing;
        }

        return InsertMissingToken(SyntaxKind.IdentifierToken, DaxParserDiagnosticCodes.ParameterNameExpected);
    }

    // ---- the hint run (A2 §4.9.1) ---------------------------------------------------------------

    private const int MaximumHints = 3;

    // The three CLOSED vocabularies of A0-language-scope §5.1, compared OrdinalIgnoreCase to match A1 §2.4
    // keyword comparison. Positional assignment is impossible: all six occupancy patterns occur in real
    // model files, including mode-only (A2 §4.9.1).
    private static readonly HashSet<string> TypeWords = new(StringComparer.OrdinalIgnoreCase)
        { "ANYVAL", "SCALAR", "TABLE", "ANYREF", "CALENDARREF", "COLUMNREF", "MEASUREREF", "TABLEREF" };

    private static readonly HashSet<string> SubtypeWords = new(StringComparer.OrdinalIgnoreCase)
        { "BOOLEAN", "DATETIME", "DECIMAL", "DOUBLE", "INT64", "NUMERIC", "STRING", "VARIANT" };

    private static readonly HashSet<string> PassingModeWords = new(StringComparer.OrdinalIgnoreCase)
        { "VAL", "EXPR" };

    /// <summary>
    /// <c>type-clause ::= ':' hint-run</c>, <c>hint-word ::= IdentifierToken | 'TABLE'</c>.
    ///
    /// <para>
    /// ONE hint node per word, in SOURCE ORDER, always. Nothing is ever reordered: reordering would make
    /// <c>ToFullString()</c> disagree with the source, which is exactly why the three fixed slots were
    /// rejected. Classification decides the node's KIND and nothing else, and a <c>DAXP1063</c>-carrying
    /// node's kind is a structural position, not a claim about meaning (A2 §4.9.1).
    /// </para>
    /// </summary>
    private GreenSyntaxNode ParseParameterTypeClause()
    {
        var colonEnd = CurrentRaw.Span.End;
        var colon = Eat();

        // Rule 1: a maximal run. It ends at the first token that is not a hint-word terminal, which
        // includes any reserved keyword other than TABLE; whatever ends it is left for the enclosing
        // production or for ordinary UDF-parameter recovery (A0 §11.5).
        var words = new List<GreenToken>();
        var spans = new List<TextSpan>();
        while (CurrentKind is SyntaxKind.IdentifierToken or SyntaxKind.TableKeyword)
        {
            spans.Add(CurrentRaw.Span);
            words.Add(Eat());
        }

        if (words.Count == 0)
        {
            // Rule 8: an empty run is an EMPTY Hints list plus DAXP1062, zero width immediately after ':'.
            var empty = new GreenParameterTypeClause(colon, GreenSyntaxList.Empty);
            return WithDiagnostic(empty, DaxParserDiagnosticCodes.ParameterHintExpected, new TextSpan(colonEnd, 0));
        }

        var hints = new List<GreenNode?>();
        var occupied = new bool[3];
        var highestSeen = -1;
        var outOfOrderReported = false;

        var emitted = Math.Min(words.Count, MaximumHints);
        for (var i = 0; i < emitted; i++)
        {
            var diagnostics = new List<DaxDiagnostic>();
            var category = Classify(words[i]);

            if (category < 0)
            {
                // Rule 4: an unclassified word takes the first category not yet occupied, in declared
                // order, so a future Microsoft subtype parses into a structured node instead of destroying
                // the parameter.
                category = FirstFreeCategory(occupied);
                diagnostics.Add(Diagnostic(DaxParserDiagnosticCodes.UnrecognizedParameterHint, spans[i]));
            }
            else
            {
                // Rule 6: a duplicated category stays its own node; the SECOND and each later word carries
                // the diagnostic, never the first.
                if (occupied[category])
                    diagnostics.Add(Diagnostic(DaxParserDiagnosticCodes.DuplicateParameterHint, spans[i]));

                // Rule 5: out-of-order words keep their kinds and their source positions; only the FIRST
                // one is reported.
                if (category < highestSeen && !outOfOrderReported)
                {
                    diagnostics.Add(Diagnostic(DaxParserDiagnosticCodes.ParameterHintsOutOfOrder, spans[i]));
                    outOfOrderReported = true;
                }
            }

            occupied[category] = true;
            if (category > highestSeen) highestSeen = category;

            hints.Add(CreateHint(category, words[i], diagnostics));
        }

        // Rule 7: the first three are emitted; the remainder becomes skipped-token trivia with ONE
        // DAXP1064 spanning it (A2 §8.4 rules 4 and 6).
        if (words.Count > MaximumHints)
        {
            for (var i = MaximumHints; i < words.Count; i++) _pendingSkipped.Add(words[i]);
            _pendingSkippedDiagnostics.Add(Diagnostic(
                DaxParserDiagnosticCodes.TooManyParameterHints,
                TextSpan.FromBounds(spans[MaximumHints].Start, spans[^1].End)));
        }

        return new GreenParameterTypeClause(colon, PollingList(hints));
    }

    /// <summary>0 = type, 1 = subtype, 2 = passing mode, -1 = unclassified.</summary>
    private static int Classify(GreenToken word)
    {
        // TABLE arrives as TableKeyword (A1 §2.4) and MUST be accepted here; every other vocabulary word is
        // already an IdentifierToken (A1 §2.5). This is contextual grammar, not keyword admission (A2 §6.2
        // rule 3), so it produces no diagnostic.
        if (word.Kind == SyntaxKind.TableKeyword) return 0;
        if (TypeWords.Contains(word.Text)) return 0;
        if (SubtypeWords.Contains(word.Text)) return 1;
        if (PassingModeWords.Contains(word.Text)) return 2;
        return -1;
    }

    private static int FirstFreeCategory(bool[] occupied)
    {
        for (var i = 0; i < occupied.Length; i++)
            if (!occupied[i]) return i;
        return 0;   // all three taken: fall back to the first, so the word still gets a structured node
    }

    private static GreenSyntaxNode CreateHint(int category, GreenToken token, List<DaxDiagnostic> diagnostics)
    {
        var owned = diagnostics.Count > 0 ? diagnostics.ToArray() : null;
        return category switch
        {
            0 => new GreenTypeHint(token, owned),
            1 => new GreenSubtypeHint(token, owned),
            _ => new GreenPassingModeHint(token, owned)
        };
    }
}
