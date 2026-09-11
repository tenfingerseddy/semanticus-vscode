using Semanticus.Dax.Syntax.Green;
using Semanticus.Dax.Text;

namespace Semanticus.Dax.Syntax.Parsing;

// A2 §4.4 and §4.5. Names ARE expressions: there is no name-expression wrapper, so a bare [Measure] is a
// BracketedName used directly. Qualification binds greedily on TOKEN adjacency, and trivia is invisible
// to the parser (A2 §5.4), so `Sales /*c*/ [Amount]` and `Sales\n[Amount]` are both qualified.

internal sealed partial class Parser
{
    private GreenSyntaxNode ParseNameOrCall()
    {
        var name = ParseName(out var isDotted, out var nameSpan);

        // Greedy qualification. There is no DAX production that places two expressions adjacent without a
        // separator, so this can never steal a bracketed name from another construct (A2 §4.4).
        if (CurrentKind == SyntaxKind.BracketedIdentifierToken)
        {
            if (isDotted)
                name = WithDiagnostic(name, DaxParserDiagnosticCodes.DottedNameCannotQualify, nameSpan);

            var bracketed = new GreenBracketedName(Eat());
            return new GreenQualifiedName(name, bracketed);
        }

        if (CurrentKind == SyntaxKind.OpenParenToken)
        {
            // callable-name admits a quoted or bracketed name, and keeps the call shape, so `'Table'(1,2)`
            // does not degenerate into two unrelated fragments (A2 §4.5, A0 §11.1 goal 3).
            if (name.Kind is SyntaxKind.QuotedName or SyntaxKind.BracketedName)
                name = WithDiagnostic(name, DaxParserDiagnosticCodes.NotCallable, nameSpan);

            return new GreenCallExpression(name, ParseArgumentList());
        }

        // A dotted name is a function/UDF name only; the parser does not fabricate member access
        // (A0-language-scope §3.1, A2 §4.4).
        if (isDotted)
            name = WithDiagnostic(name, DaxParserDiagnosticCodes.DottedNameNotCallable, nameSpan);

        return name;
    }

    private GreenSyntaxNode ParseName(out bool isDotted, out TextSpan span)
    {
        var start = CurrentRaw.Span.Start;
        isDotted = false;

        switch (CurrentKind)
        {
            case SyntaxKind.QuotedIdentifierToken:
            {
                // A bare quoted name is a name expression in its own right, not a fragment and not an
                // error: TOTALYTD(expr, 'Fiscal') is the exact shipped calendars form (A2 §4.4).
                var token = Eat();
                span = TextSpan.FromBounds(start, _lastAccepted!.Span.End);
                return new GreenQuotedName(token);
            }

            case SyntaxKind.BracketedIdentifierToken:
            {
                var token = Eat();
                span = TextSpan.FromBounds(start, _lastAccepted!.Span.End);
                return new GreenBracketedName(token);
            }
        }

        // simple-name or dotted-name. A1 preserves every dot (A1 §3.4); composition is diagnosed here.
        var segments = new List<GreenNode>();
        var separators = new List<GreenToken>();

        segments.Add(new GreenIdentifierName(CurrentKind == SyntaxKind.DotToken
            ? InsertMissingToken(SyntaxKind.IdentifierToken, DaxParserDiagnosticCodes.EmptyDottedSegment)
            : Eat()));

        while (CurrentKind == SyntaxKind.DotToken)
        {
            isDotted = true;
            separators.Add(Eat());
            segments.Add(new GreenIdentifierName(CurrentKind == SyntaxKind.IdentifierToken
                ? Eat()
                : InsertMissingToken(SyntaxKind.IdentifierToken, DaxParserDiagnosticCodes.EmptyDottedSegment)));
        }

        span = TextSpan.FromBounds(start, _lastAccepted?.Span.End ?? start);

        // A leading '.' begins a dotted name with a missing first segment; `.5` was already consumed by A1
        // as a decimal literal, so no conflict exists (A2 §4.4).
        if (!isDotted && segments.Count == 1) return (GreenSyntaxNode)segments[0];

        return new GreenDottedName(PollingSeparatedList(segments, separators));
    }
}
