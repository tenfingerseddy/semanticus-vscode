using Semanticus.Dax.Syntax.Green;
using Semanticus.Dax.Text;

namespace Semanticus.Dax.Syntax.Parsing;

// A2 §4.7. Declarations are an ORDERED list: never reordered, merged, or flattened. Sequential scoping is
// representable because order is preserved and each initializer is a child of its own declaration, but
// resolution is the binder's job and the parser emits nothing about it. Shadowing is not a parse error.

internal sealed partial class Parser
{
    private GreenSyntaxNode ParseVariableExpression()
    {
        if (!TryOpenProduction()) return MissingExpression();

        var savedResync = _resync;
        _resync = Resync.VarBlock;
        try
        {
            var declarations = new List<GreenNode?>();

            while (CurrentKind == SyntaxKind.VarKeyword)
            {
                declarations.Add(ParseVariableDeclaration());

                // A0 §11.5, VAR block: resynchronize on the next VAR, RETURN, an OPEN enclosing delimiter,
                // or EOF. Golden G-P-REC-010 (`VAR a = ) RETURN a`) turns on the "open" part: at the root
                // nothing is open, so the stray ')' is skipped and the RETURN clause survives.
                if (CurrentKind is not (SyntaxKind.VarKeyword or SyntaxKind.ReturnKeyword) && !AtResyncPoint())
                    SkipUnexpectedTokens(DaxParserDiagnosticCodes.UnexpectedTokensSkipped);
            }

            GreenSyntaxNode returnClause;
            if (CurrentKind == SyntaxKind.ReturnKeyword)
            {
                var keyword = Eat();
                returnClause = new GreenReturnClause(keyword, ParseExpression());
            }
            else
            {
                // A0 §11.2 names this case: insert a zero-width RETURN plus a MissingExpression body. The
                // body carries no DAXP1001 of its own; DAXP1080 is the whole story (golden G-P-VAR-006).
                var keyword = InsertMissingToken(SyntaxKind.ReturnKeyword, DaxParserDiagnosticCodes.ReturnExpected);
                returnClause = new GreenReturnClause(keyword, new GreenMissingExpression());
            }

            return new GreenVariableExpression(PollingList(declarations), returnClause);
        }
        finally
        {
            _resync = savedResync;
            CloseProduction();
        }
    }

    private GreenSyntaxNode ParseVariableDeclaration()
    {
        var varKeyword = Eat();
        var identifier = ParseVariableName();
        var equals = Expect(SyntaxKind.EqualsToken, DaxParserDiagnosticCodes.VariableEqualsExpected);
        var initializer = ParseExpression();
        return new GreenVariableDeclaration(varKeyword, identifier, equals, initializer);
    }

    /// <summary>
    /// <c>variable-name</c> is an <c>IdentifierToken</c> only (A2 §4.7).
    ///
    /// <para>
    /// A reserved keyword is RETAINED in the slot with <c>DAXP1084</c> (Error): the slot is unambiguous, so
    /// refusing the token would destroy a whole declaration over a naming rule (A2 §6.2 rule 4). The
    /// token's kind is never rewritten (A2 §2 rule 1) — a consumer detects it with
    /// <c>DaxSyntaxFacts.IsKeyword</c> plus the position.
    /// </para>
    /// </summary>
    private GreenToken ParseVariableName()
    {
        var kind = CurrentKind;

        if (kind == SyntaxKind.IdentifierToken && PeekKind(1) != SyntaxKind.DotToken) return Eat();

        if (DaxSyntaxFacts.IsKeyword(kind))
        {
            var span = CurrentRaw.Span;
            return (GreenToken)Eat().WithAdditionalDiagnostics(
                Diagnostic(DaxParserDiagnosticCodes.KeywordNotPermittedAsVariableName, span));
        }

        // A delimited or dotted spelling is OUT (A0-language-scope §3.4): the offending run becomes
        // skipped-token trivia and the slot takes a missing identifier carrying the one DAXP1083. DAXP1081 is
        // for a name that is genuinely absent, so the two never both fire (golden G-P-VAR-009).
        if (kind is SyntaxKind.QuotedIdentifierToken or SyntaxKind.BracketedIdentifierToken or SyntaxKind.DotToken ||
            (kind == SyntaxKind.IdentifierToken && PeekKind(1) == SyntaxKind.DotToken))
        {
            var runStart = _index;
            while (CurrentKind is SyntaxKind.QuotedIdentifierToken or SyntaxKind.BracketedIdentifierToken or
                   SyntaxKind.DotToken or SyntaxKind.IdentifierToken)
                SkipCurrent();

            // The DAXP1083 goes ON the inserted token, not on the skipped run. A0 §11.2 requires an inserted
            // missing token to carry a DAXP diagnostic, and a bare missing name is exactly the thing a later
            // phase must not be able to read as a clean name (A2PR1-M4). Same code, same span as the run
            // diagnostic it replaces: moved, not duplicated.
            return GreenFactory.MissingToken(
                SyntaxKind.IdentifierToken,
                new[]
                {
                    Diagnostic(
                        DaxParserDiagnosticCodes.VariableNameCannotBeDelimited,
                        TextSpan.FromBounds(_raw[runStart].Span.Start, _raw[_index - 1].Span.End))
                });
        }

        return InsertMissingToken(SyntaxKind.IdentifierToken, DaxParserDiagnosticCodes.VariableNameExpected);
    }
}
