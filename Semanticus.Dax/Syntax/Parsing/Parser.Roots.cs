using Semanticus.Dax.Syntax.Green;

namespace Semanticus.Dax.Syntax.Parsing;

// A2 §3.1, §4.1, §4.9. Two root grammars in PR-1. The nine expression source kinds share ONE parse:
// same node kinds, same child order, same spans, same diagnostics. Nothing below branches on the source
// kind except this one dispatch, which selects the root grammar.

internal sealed partial class Parser
{
    internal GreenSyntaxNode ParseRoot()
    {
        Checkpoint();

        return DaxSyntaxTree.GetRootFamily(_options.SourceKind) switch
        {
            DaxRootFamily.Expression => ParseExpressionCompilationUnit(),
            DaxRootFamily.UdfBody => ParseUdfBodyCompilationUnit(),
            _ => throw new NotSupportedException("The query, visual-calculation, and formula-definition roots are reserved for PR-2. See A2 §10.")
        };
    }

    /// <summary>
    /// <c>expression-compilation-unit ::= [ '=' ] expression EndOfFileToken</c> (A2 §4.1).
    /// </summary>
    private GreenSyntaxNode ParseExpressionCompilationUnit()
    {
        // A2 §5.2 pins 2-3: a leading '=' is ALWAYS consumed into the slot, whatever AllowLeadingEquals
        // says; the option only decides whether DAXP1020 is emitted. That is the whole reason the tree is
        // option-independent (A2 §2 rule 5) and goldens G-P-ROOT-001/-002 assert one tree, two diagnostic
        // lists. '==' and '=>' are distinct A1 kinds and are never the root token.
        GreenToken? leadingEquals = null;
        if (CurrentKind == SyntaxKind.EqualsToken)
        {
            var span = CurrentRaw.Span;
            leadingEquals = Eat();
            if (!_options.AllowLeadingEquals)
                leadingEquals = (GreenToken)leadingEquals.WithAdditionalDiagnostics(
                    Diagnostic(DaxParserDiagnosticCodes.LeadingEqualsNotPermitted, span));
        }

        var expression = ParseExpression();
        var endOfFile = ParseEndOfFile(DaxParserDiagnosticCodes.UnexpectedContentAfterExpression);
        return new GreenExpressionCompilationUnit(leadingEquals, expression, endOfFile);
    }

    /// <summary>
    /// <c>udf-body-compilation-unit ::= lambda EndOfFileToken</c> (A2 §4.9). The leading parenthesized
    /// group is the parameter list by ROOT GRAMMAR, so no lookahead is needed and no row-constructor
    /// ambiguity exists at that position (A2 §5.3).
    /// </summary>
    private GreenSyntaxNode ParseUdfBodyCompilationUnit()
    {
        var lambda = ParseLambda();
        var endOfFile = ParseEndOfFile(DaxParserDiagnosticCodes.UnexpectedContentAfterExpression);
        return new GreenUdfBodyCompilationUnit(lambda, endOfFile);
    }

    /// <summary>
    /// Everything left before EOF becomes skipped-token trivia on the EOF token with ONE diagnostic
    /// spanning the whole trailing run (A2 §4.1, §8.4 rule 4). The root's expression child is never widened
    /// to swallow it, so golden G-P-ROOT-005's second fragment stays visibly separate.
    /// </summary>
    private GreenToken ParseEndOfFile(string trailingCode)
    {
        _resync = Resync.Root;

        var runStart = -1;
        while (!AtRealEof)
        {
            if (runStart < 0) runStart = _index;
            SkipCurrent();
        }
        if (runStart >= 0 && runStart != _index) AddRunDiagnostic(trailingCode, runStart, _index);

        return Eat();
    }
}
