using System.Diagnostics;
using Semanticus.Dax.Syntax.Green;

namespace Semanticus.Dax.Syntax.Parsing;

// The operator ladder, A2 §4.2 wired to A0-language-scope §3.2 verbatim: one descent function per
// published level, in the published order. Levels 10, 9, 7, 6, 5 and 4 are left associative `while` loops;
// levels 8 and 3 are prefix recursion; level 2 is right associative (A2 §5.1).

internal sealed partial class Parser
{
    /// <summary>
    /// A FULL-EXPRESSION position (A2 §4.8): the only place a <c>variable-expression</c> may be produced.
    /// The five positions are the root, an argument, a parenthesized/row/table element, a variable
    /// initializer or return body, and a UDF default value or lambda body. Everywhere else the ladder is
    /// entered directly, which is why <c>1 + VAR x = 1 RETURN x</c> goes to recovery and
    /// <c>1 + (VAR x = 1 RETURN x)</c> parses.
    /// </summary>
    private GreenSyntaxNode ParseExpression()
        => CurrentKind == SyntaxKind.VarKeyword ? ParseVariableExpression() : ParseOr();

    // ---- level 10: || ---------------------------------------------------------------------------

    private GreenSyntaxNode ParseOr()
    {
        var left = ParseAnd();
        while (CurrentKind == SyntaxKind.OrOrToken)
        {
            var op = Eat();
            left = new GreenBinaryExpression(left, op, ParseAnd());
        }
        return left;
    }

    // ---- level 9: && ----------------------------------------------------------------------------

    private GreenSyntaxNode ParseAnd()
    {
        var left = ParseNot();
        while (CurrentKind == SyntaxKind.AndAndToken)
        {
            var op = Eat();
            left = new GreenBinaryExpression(left, op, ParseNot());
        }
        return left;
    }

    // ---- level 8: prefix NOT --------------------------------------------------------------------

    /// <summary>
    /// <c>NOT</c> is ALWAYS the prefix operator, never a call name (A2 §5.5): <c>NOT(ISBLANK([x]))</c> is
    /// a prefix expression over a parenthesized expression, with no diagnostic. There is no combined
    /// <c>NOT IN</c> operator either; <c>NOT [a] IN {1}</c> is <c>NOT([a] IN {1})</c> because <c>IN</c> is
    /// level 7 (A2 §5.1).
    /// </summary>
    private GreenSyntaxNode ParseNot()
    {
        if (CurrentKind != SyntaxKind.NotKeyword) return ParseComparison();

        if (!TryOpenProduction()) return MissingExpression();
        try
        {
            var op = Eat();
            return new GreenPrefixUnaryExpression(op, ParseNot());
        }
        finally { CloseProduction(); }
    }

    // ---- level 7: comparisons and IN ------------------------------------------------------------

    private GreenSyntaxNode ParseComparison()
    {
        var left = ParseConcat();
        while (true)
        {
            var kind = CurrentKind;
            if (kind == SyntaxKind.InKeyword)
            {
                // IN is an ordinary level-7 left-associative binary operator (A2 §5.1); the node kind is
                // the only thing that differs.
                var keyword = Eat();
                left = new GreenMembershipExpression(left, keyword, ParseConcat());
                continue;
            }

            if (DaxSyntaxFacts.GetBinaryOperatorPrecedence(kind) != 7) return left;

            // Chained comparisons parse left-associatively and get NO DAXP diagnostic; the host's objection
            // is a DAXB decision (A2 §5.1, golden G-P-PREC-012).
            var op = Eat();
            left = new GreenBinaryExpression(left, op, ParseConcat());
        }
    }

    // ---- level 6: & -----------------------------------------------------------------------------

    private GreenSyntaxNode ParseConcat()
    {
        var left = ParseAdditive();
        while (CurrentKind == SyntaxKind.AmpersandToken)
        {
            var op = Eat();
            left = new GreenBinaryExpression(left, op, ParseAdditive());
        }
        return left;
    }

    // ---- level 5: binary + - --------------------------------------------------------------------

    private GreenSyntaxNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (CurrentKind is SyntaxKind.PlusToken or SyntaxKind.MinusToken)
        {
            var op = Eat();
            left = new GreenBinaryExpression(left, op, ParseMultiplicative());
        }
        return left;
    }

    // ---- level 4: * / ---------------------------------------------------------------------------

    private GreenSyntaxNode ParseMultiplicative()
    {
        var left = ParseUnary();
        while (CurrentKind is SyntaxKind.StarToken or SyntaxKind.SlashToken)
        {
            var op = Eat();
            left = new GreenBinaryExpression(left, op, ParseUnary());
        }
        return left;
    }

    // ---- level 3: prefix + - --------------------------------------------------------------------

    private GreenSyntaxNode ParseUnary()
    {
        // A2 §6.3/§8.3: a reserved keyword in a name position is rejected HERE rather than one level down,
        // so that what follows still gets the full level-3 reading. `AT + 1` is one DAXP1043 and then an
        // ordinary prefix expression; `START(1)` is one DAXP1043 and then a parenthesized expression. The
        // loop (not recursion) is what keeps a long run of keywords off the stack.
        while (TryRejectKeywordName()) { }

        if (CurrentKind is not (SyntaxKind.PlusToken or SyntaxKind.MinusToken)) return ParsePower();

        if (!TryOpenProduction()) return MissingExpression();
        try
        {
            var op = Eat();
            return new GreenPrefixUnaryExpression(op, ParseUnary());
        }
        finally { CloseProduction(); }
    }

    // ---- level 2: ^ -----------------------------------------------------------------------------

    /// <summary>
    /// Base from level 1, right operand from LEVEL 3 (A2 §4.2). That single choice produces all three
    /// A0-workplan §1.4 pins at once: <c>2^3^2 == 2^(3^2)</c>, <c>-2^2 == -(2^2)</c>, and a legal signed
    /// exponent <c>2^-3</c>. Do not re-derive it.
    ///
    /// <para>
    /// The right operand is a DESCENT, not a loop iteration: <c>ParseUnary</c> falls straight back into
    /// <c>ParsePower</c> when no sign is present, so each caret costs two stack frames. That makes a caret
    /// chain genuine NESTING and it must be counted (A2 §2.1). Without the guard below, <c>ParsePower</c>
    /// and <c>ParseUnary</c> were the only ladder functions on no <c>TryOpenProduction</c> path at all, so
    /// <c>1 ^ 1 ^ 1 ...</c> recursed until the CLR killed the process — and a
    /// <c>StackOverflowException</c> cannot be caught, so the host died with it (A2PR1-M1).
    /// </para>
    /// <para>
    /// This is a guard, not a shape change: it leaves associativity exactly where A2 §5.1 pinned it.
    /// </para>
    /// </summary>
    private GreenSyntaxNode ParsePower()
    {
        var left = ParsePrimary();
        while (CurrentKind == SyntaxKind.CaretToken)
        {
            var op = Eat();

            // On failure TryOpenProduction has already reported DAXP1007 and consumed to the enclosing
            // resynchronization point, so the operand is a MissingExpression without a second complaint and
            // there is nothing left for this loop to iterate over.
            if (!TryOpenProduction()) return new GreenBinaryExpression(left, op, MissingExpression());

            try { left = new GreenBinaryExpression(left, op, ParseUnary()); }
            finally { CloseProduction(); }
        }
        return left;
    }

    // ---- level 1: primaries ---------------------------------------------------------------------

    private static bool CanStartPrimary(SyntaxKind kind) => kind is
        SyntaxKind.IntegerLiteralToken or SyntaxKind.DecimalLiteralToken or SyntaxKind.ScientificLiteralToken or
        SyntaxKind.DateLiteralToken or SyntaxKind.StringLiteralToken or
        SyntaxKind.IdentifierToken or SyntaxKind.QuotedIdentifierToken or SyntaxKind.BracketedIdentifierToken or
        SyntaxKind.DotToken or SyntaxKind.OpenParenToken or SyntaxKind.OpenBraceToken or
        SyntaxKind.NotKeyword;

    /// <summary>
    /// A2 §8.3: at a required-primary position a stopper wins (one <c>DAXP1001</c>, nothing consumed), a
    /// keyword with a role there wins (no diagnostic), and any other reserved keyword takes exactly one
    /// <c>DAXP1043</c> with no companion <c>DAXP1001</c>.
    ///
    /// <para>
    /// <c>VAR</c> is treated as a stopper. A2 §8.3 row 2 says "the production wins", but at a required
    /// primary inside the ladder <c>VAR</c> has no production (A2 §4.8 confines variable expressions to
    /// full-expression positions), and A0 §11.5 already makes the next <c>VAR</c> a resynchronization
    /// point. One <c>DAXP1001</c> and no consumption is the only reading that satisfies both, and it is
    /// what golden G-P-PREC-017 calls recovery.
    /// </para>
    /// </summary>
    private static bool IsRequiredPrimaryStopper(SyntaxKind kind) => kind is
        SyntaxKind.CloseParenToken or SyntaxKind.CloseBraceToken or SyntaxKind.CommaToken or
        SyntaxKind.ReturnKeyword or SyntaxKind.EndOfFileToken or SyntaxKind.VarKeyword;

    private GreenSyntaxNode ParsePrimary()
    {
        while (true)
        {
            Poll();
            var kind = CurrentKind;

            switch (kind)
            {
                case SyntaxKind.IntegerLiteralToken:
                case SyntaxKind.DecimalLiteralToken:
                case SyntaxKind.ScientificLiteralToken:
                case SyntaxKind.DateLiteralToken:
                case SyntaxKind.StringLiteralToken:
                    // TRUE/FALSE/BLANK are IdentifierTokens (A1 §2.5) and are never literal nodes (A2 §4.3).
                    return new GreenLiteralExpression(Eat());

                case SyntaxKind.IdentifierToken:
                case SyntaxKind.QuotedIdentifierToken:
                case SyntaxKind.BracketedIdentifierToken:
                case SyntaxKind.DotToken:
                    return ParseNameOrCall();

                case SyntaxKind.OpenParenToken:
                    return ParseParenthesizedOrRow();

                case SyntaxKind.OpenBraceToken:
                    return ParseTableConstructor();

                case SyntaxKind.NotKeyword:
                    // A2 §8.3 row 2: the production wins. NOT reaching a required primary means an operand
                    // position below level 8, which is not valid DAX, but the parser records what was
                    // written rather than inventing a diagnostic the binder owns.
                    return ParseNot();
            }

            if (IsRequiredPrimaryStopper(kind)) return MissingExpression();
            if (TryRejectKeywordName()) continue;

            var before = _index;
            SkipUnexpectedTokens(DaxParserDiagnosticCodes.UnexpectedTokensSkipped, stopAtPrimaryStart: true);
            if (_index == before) return MissingExpression();   // A0 §11.6: never loop without progress
        }
    }

    // ---- constructors (A2 §4.6, §5.3) -----------------------------------------------------------

    /// <summary>
    /// ARITY decides, context is irrelevant (A2 §5.3). One or more top-level commas makes a row
    /// constructor in EVERY position, including the left operand of <c>IN</c>, so nothing here ever scans
    /// ahead past the matching <c>)</c>.
    /// </summary>
    private GreenSyntaxNode ParseParenthesizedOrRow()
    {
        if (!TryOpenProduction()) return MissingExpression();

        var savedResync = _resync;
        _resync = Resync.Parenthesized;
        _parenDepth++;
        try
        {
            var open = Eat();

            // '(' ')' is a parenthesized expression with a missing child, not an empty row (A2 §4.6).
            var first = ParseExpression();

            if (CurrentKind != SyntaxKind.CommaToken)
            {
                SkipUnexpectedTokens(DaxParserDiagnosticCodes.UnexpectedTokensSkipped);
                if (CurrentKind != SyntaxKind.CommaToken)
                {
                    var closeParen = Expect(SyntaxKind.CloseParenToken, DaxParserDiagnosticCodes.CloseParenExpected);
                    return new GreenParenthesizedExpression(open, first, closeParen);
                }
            }

            _listDepth++;
            try
            {
                var fields = new List<GreenNode> { first };
                var separators = new List<GreenToken>();

                while (CurrentKind == SyntaxKind.CommaToken)
                {
                    separators.Add(Eat());
                    fields.Add(ParseExpression());
                    if (CurrentKind is not (SyntaxKind.CommaToken or SyntaxKind.CloseParenToken))
                        SkipUnexpectedTokens(DaxParserDiagnosticCodes.UnexpectedTokensSkipped);
                }

                var close = Expect(SyntaxKind.CloseParenToken, DaxParserDiagnosticCodes.CloseParenExpected);
                return new GreenRowConstructorExpression(open, PollingSeparatedList(fields, separators), close);
            }
            finally { _listDepth--; }
        }
        finally
        {
            _parenDepth--;
            _resync = savedResync;
            CloseProduction();
        }
    }

    /// <summary><c>{ }</c> is a table constructor with ZERO elements and NO diagnostic (A2 §4.6).</summary>
    private GreenSyntaxNode ParseTableConstructor()
    {
        if (!TryOpenProduction()) return MissingExpression();

        var savedResync = _resync;
        _resync = Resync.ArgumentOrConstructorList;
        _braceDepth++;
        _listDepth++;
        try
        {
            var open = Eat();

            var elements = new List<GreenNode>();
            var separators = new List<GreenToken>();

            if (CurrentKind != SyntaxKind.CloseBraceToken)
            {
                while (true)
                {
                    elements.Add(ParseExpression());

                    if (CurrentKind is not (SyntaxKind.CommaToken or SyntaxKind.CloseBraceToken))
                        SkipUnexpectedTokens(DaxParserDiagnosticCodes.UnexpectedTokensSkipped);

                    if (CurrentKind != SyntaxKind.CommaToken) break;
                    separators.Add(Eat());
                }
            }

            var close = Expect(SyntaxKind.CloseBraceToken, DaxParserDiagnosticCodes.CloseBraceExpected);
            return new GreenTableConstructorExpression(open, PollingSeparatedList(elements, separators), close);
        }
        finally
        {
            _listDepth--;
            _braceDepth--;
            _resync = savedResync;
            CloseProduction();
        }
    }

    // ---- argument lists (A2 §4.5) ---------------------------------------------------------------

    /// <summary>
    /// <c>F()</c> is ZERO elements and zero separators: an <c>OmittedArgument</c> is produced only when the
    /// list is non-empty, i.e. only when at least one comma is present (A2 §4.5).
    /// </summary>
    private GreenSyntaxNode ParseArgumentList()
    {
        // Depth recovery has already consumed to the resync point; the shell keeps the call's shape without
        // inventing a second complaint about the same defect. It TAKES the DAXP1007 rather than leaving it
        // on trivia outside itself, so the shell can say it recovered (A0 §11.2, A2PR1-M4). The pair of
        // delimiters is one defect, so the diagnostic sits on the opening one and is not repeated on the
        // closing one (A2 §8.1 rule 5).
        if (!TryOpenProduction(out var depthDiagnostic))
        {
            _suppressExpressionExpected = false;
            Debug.Assert(depthDiagnostic is not null, "TryOpenProduction returned false without a DAXP1007 to hand over.");
            return new GreenArgumentList(
                GreenFactory.MissingToken(SyntaxKind.OpenParenToken, new[] { depthDiagnostic! }),
                GreenSyntaxList.Empty,
                GreenFactory.MissingToken(SyntaxKind.CloseParenToken));
        }

        var savedResync = _resync;
        _resync = Resync.ArgumentOrConstructorList;
        _parenDepth++;
        _listDepth++;
        try
        {
            var open = Eat();

            var arguments = new List<GreenNode>();
            var separators = new List<GreenToken>();

            if (CurrentKind != SyntaxKind.CloseParenToken)
            {
                while (true)
                {
                    arguments.Add(CurrentKind is SyntaxKind.CommaToken or SyntaxKind.CloseParenToken
                        ? new GreenOmittedArgument()
                        : new GreenArgument(ParseExpression()));

                    if (CurrentKind is not (SyntaxKind.CommaToken or SyntaxKind.CloseParenToken))
                        SkipUnexpectedTokens(DaxParserDiagnosticCodes.UnexpectedTokensSkipped);

                    if (CurrentKind != SyntaxKind.CommaToken) break;
                    separators.Add(Eat());
                }
            }

            var close = Expect(SyntaxKind.CloseParenToken, DaxParserDiagnosticCodes.CloseParenExpected);
            return new GreenArgumentList(open, PollingSeparatedList(arguments, separators), close);
        }
        finally
        {
            _listDepth--;
            _parenDepth--;
            _resync = savedResync;
            CloseProduction();
        }
    }
}
