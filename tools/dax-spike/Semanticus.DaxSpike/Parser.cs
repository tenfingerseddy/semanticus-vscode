namespace Semanticus.DaxSpike;

public sealed record ParseResult(string Source, GreenSyntax Root, IReadOnlyList<string> Diagnostics)
{
    public bool RoundTrips => Root.ToFullString() == Source;
    public bool CleanParse => !Root.ContainsSkipped && !Root.ContainsMissing;
}

// Recursive-descent DAX expression parser producing an immutable green tree. Targets the A0
// "core expression language" subset (language-scope §3): literals, references, calls (incl. dotted
// and empty/omitted args), constructors, the full operator precedence ladder, and VAR/RETURN.
// Every finite input yields a tree (design §5.5): unparseable runs become SkippedTokens, required-
// but-absent tokens become zero-width missing tokens, and the whole tree round-trips byte-exactly.
public sealed class DaxParser
{
    private readonly TokenTape _tape;
    private int _pos;
    private readonly List<string> _diag = new();

    private DaxParser(TokenTape tape) => _tape = tape;

    public static ParseResult Parse(string source)
    {
        var lex = DaxLexAdapter.Lex(source ?? "");
        var tape = new TokenTape(source ?? "", lex);
        var p = new DaxParser(tape);
        var root = p.ParseCompilationUnit();
        return new ParseResult(source ?? "", root, p._diag);
    }

    // ---- cursor ----
    private TokenKind Cur => _tape.Kind(_pos);
    private bool AtEof => _pos >= _tape.Count;
    private GreenToken Eat() { var t = _tape.Real(_pos); _pos++; return t; }
    private static GreenToken Missing(TokenKind k) => TokenTape.Missing(k);
    private void Diag(string m) => _diag.Add(m);

    // ---- roots ----
    private GreenSyntax ParseCompilationUnit()
    {
        var items = new List<GreenNode>();
        if (!AtEof) items.Add(ParseExpression());
        if (!AtEof) items.Add(SkipUntil(_ => false));   // capture any trailing unconsumed source
        items.Add(_tape.Eof());
        return new GreenSyntax(NodeKind.CompilationUnit, items.ToArray());
    }

    private GreenNode ParseExpression()
    {
        if (Cur == TokenKind.KwVar) return ParseVarExpr();
        return ParseOr();
    }

    // VAR name = expr (VAR ...)* RETURN expr  (language-scope §3.4; nested VAR allowed via ParseExpression)
    private GreenNode ParseVarExpr()
    {
        var parts = new List<GreenNode>();
        while (Cur == TokenKind.KwVar)
        {
            var kw = Eat();
            var name = (Cur is TokenKind.Ident or TokenKind.FuncName) ? Eat() : MissingWithDiag(TokenKind.Ident, "VAR name expected");
            var eq = (Cur == TokenKind.Eq) ? Eat() : MissingWithDiag(TokenKind.Eq, "'=' expected in VAR declaration");
            var init = ParseExpression();
            parts.Add(new GreenSyntax(NodeKind.VarDecl, kw, name, eq, init));
        }
        GreenToken ret; GreenNode body;
        if (Cur == TokenKind.KwReturn) { ret = Eat(); body = ParseExpression(); }
        else { ret = MissingWithDiag(TokenKind.KwReturn, "RETURN expected after VAR declarations"); body = MissingExpr(); }
        parts.Add(new GreenSyntax(NodeKind.ReturnClause, ret, body));
        return new GreenSyntax(NodeKind.VarExpr, parts.ToArray());
    }

    // ---- precedence ladder (highest = primary, lowest = ||); see language-scope §3.2 ----
    private GreenNode ParseOr()
    {
        var left = ParseAnd();
        while (Cur == TokenKind.PipePipe) { var op = Eat(); left = new GreenSyntax(NodeKind.BinaryExpr, left, op, ParseAnd()); }
        return left;
    }
    private GreenNode ParseAnd()
    {
        var left = ParseNot();
        while (Cur == TokenKind.AmpAmp) { var op = Eat(); left = new GreenSyntax(NodeKind.BinaryExpr, left, op, ParseNot()); }
        return left;
    }
    // NOT is a prefix unary lower than comparison but higher than && (published order).
    private GreenNode ParseNot()
    {
        if (Cur == TokenKind.KwNot) { var op = Eat(); return new GreenSyntax(NodeKind.UnaryExpr, op, ParseNot()); }
        return ParseComparison();
    }
    private GreenNode ParseComparison()
    {
        var left = ParseConcat();
        while (Cur is TokenKind.Eq or TokenKind.EqEq or TokenKind.Ne or TokenKind.Lt or TokenKind.Le
               or TokenKind.Gt or TokenKind.Ge or TokenKind.KwIn)
        {
            var op = Eat();
            var right = ParseConcat();
            var kind = op.TokenKind == TokenKind.KwIn ? NodeKind.MembershipExpr : NodeKind.BinaryExpr;
            left = new GreenSyntax(kind, left, op, right);
        }
        return left;
    }
    private GreenNode ParseConcat()
    {
        var left = ParseAdd();
        while (Cur == TokenKind.Amp) { var op = Eat(); left = new GreenSyntax(NodeKind.BinaryExpr, left, op, ParseAdd()); }
        return left;
    }
    private GreenNode ParseAdd()
    {
        var left = ParseMul();
        while (Cur is TokenKind.Plus or TokenKind.Minus) { var op = Eat(); left = new GreenSyntax(NodeKind.BinaryExpr, left, op, ParseMul()); }
        return left;
    }
    private GreenNode ParseMul()
    {
        var left = ParseUnary();
        while (Cur is TokenKind.Star or TokenKind.Slash) { var op = Eat(); left = new GreenSyntax(NodeKind.BinaryExpr, left, op, ParseUnary()); }
        return left;
    }
    // unary +/- (level 3). Sign is NOT part of a numeric token (language-scope §3.3).
    private GreenNode ParseUnary()
    {
        if (Cur is TokenKind.Plus or TokenKind.Minus) { var op = Eat(); return new GreenSyntax(NodeKind.UnaryExpr, op, ParseUnary()); }
        return ParsePower();
    }
    // ^ (level 2) binds tighter than unary sign; RHS via ParseUnary makes -2^2 == -(2^2) and 2^-3 legal.
    private GreenNode ParsePower()
    {
        var b = ParsePrimary();
        if (Cur == TokenKind.Caret) { var op = Eat(); return new GreenSyntax(NodeKind.BinaryExpr, b, op, ParseUnary()); }
        return b;
    }

    private static bool CanStartPrimary(TokenKind k) => k is
        TokenKind.Number or TokenKind.String or TokenKind.DateLit or TokenKind.KwTrue or TokenKind.KwFalse
        or TokenKind.KwBlank or TokenKind.Ident or TokenKind.FuncName or TokenKind.QuotedName
        or TokenKind.BracketName or TokenKind.OpenParen or TokenKind.OpenBrace or TokenKind.KwVar;

    private static bool IsStopper(TokenKind k) => k is
        TokenKind.CloseParen or TokenKind.CloseBrace or TokenKind.Comma or TokenKind.Eof or TokenKind.KwReturn;

    private GreenNode ParsePrimary()
    {
        switch (Cur)
        {
            case TokenKind.Number:
            case TokenKind.String:
            case TokenKind.DateLit:
                return new GreenSyntax(NodeKind.LiteralExpr, Eat());

            case TokenKind.KwTrue:
            case TokenKind.KwFalse:
            case TokenKind.KwBlank:
            {
                var name = Eat();
                if (Cur == TokenKind.OpenParen) return ParseCallTail(name);
                return new GreenSyntax(NodeKind.NameRef, name);   // bare TRUE/FALSE (rare)
            }

            case TokenKind.Ident:
            case TokenKind.FuncName:
            {
                var name = Eat();
                if (Cur == TokenKind.OpenParen) return ParseCallTail(name);
                if (Cur == TokenKind.BracketName) return new GreenSyntax(NodeKind.QualifiedRef, name, Eat());
                return new GreenSyntax(NodeKind.NameRef, name);
            }

            case TokenKind.QuotedName:
            {
                var q = Eat();
                if (Cur == TokenKind.BracketName) return new GreenSyntax(NodeKind.QualifiedRef, q, Eat());
                return new GreenSyntax(NodeKind.NameRef, q);       // bare 'Table'
            }

            case TokenKind.BracketName:
                return new GreenSyntax(NodeKind.BracketRef, Eat()); // [Measure] / unqualified [Column]

            // ASC / DESC appear as bare enum arguments to window/order functions in model DAX
            // (e.g. WINDOW(.., ORDERBY([x], ASC), ..)). The binder classifies the enum; the parser
            // treats them as name references, matching the "enum arg is a structured name" design.
            case TokenKind.KwAsc:
            case TokenKind.KwDesc:
                return new GreenSyntax(NodeKind.NameRef, Eat());

            case TokenKind.OpenParen:
                return ParseParenOrRow();

            case TokenKind.OpenBrace:
                return ParseTableConstructor();

            default:
                // Unexpected token where a primary is required.
                if (IsStopper(Cur) || AtEof) { Diag($"expression expected"); return MissingExpr(); }
                Diag($"unexpected token '{_tape.Real(_pos).RawText}'");
                return SkipUntil(k => CanStartPrimary(k) || IsStopper(k));
        }
    }

    private GreenNode ParseCallTail(GreenToken name)
    {
        var lp = Eat(); // (
        var argItems = new List<GreenNode>();
        if (Cur != TokenKind.CloseParen)
        {
            argItems.Add(ParseArgument());
            while (Cur == TokenKind.Comma)
            {
                argItems.Add(Eat());               // comma retained (separated list, design §7.5)
                argItems.Add(ParseArgument());
            }
        }
        if (Cur != TokenKind.CloseParen && !AtEof)
            argItems.Add(SkipUntil(k => k is TokenKind.CloseParen or TokenKind.Comma));
        var rp = (Cur == TokenKind.CloseParen) ? Eat() : MissingWithDiag(TokenKind.CloseParen, "')' expected");
        var list = new GreenSyntax(NodeKind.ArgumentList,
            Prepend(lp, Append(argItems, rp)));
        return new GreenSyntax(NodeKind.CallExpr, name, list);
    }

    // An argument, including an explicit zero-width OmittedArgument between commas (Func(1,,3)).
    private GreenNode ParseArgument()
    {
        if (Cur is TokenKind.Comma or TokenKind.CloseParen)
            return new GreenSyntax(NodeKind.OmittedArgument);       // zero children => zero width
        return new GreenSyntax(NodeKind.Argument, ParseExpression());
    }

    private GreenNode ParseParenOrRow()
    {
        var lp = Eat(); // (
        var first = ParseExpression();
        if (Cur == TokenKind.Comma)
        {
            var items = new List<GreenNode> { lp, first };
            while (Cur == TokenKind.Comma) { items.Add(Eat()); items.Add(ParseExpression()); }
            if (Cur != TokenKind.CloseParen && !AtEof) items.Add(SkipUntil(k => k == TokenKind.CloseParen));
            items.Add((Cur == TokenKind.CloseParen) ? Eat() : MissingWithDiag(TokenKind.CloseParen, "')' expected"));
            return new GreenSyntax(NodeKind.RowConstructor, items.ToArray());
        }
        var rp = (Cur == TokenKind.CloseParen) ? Eat() : MissingWithDiag(TokenKind.CloseParen, "')' expected");
        return new GreenSyntax(NodeKind.ParenExpr, lp, first, rp);
    }

    private GreenNode ParseTableConstructor()
    {
        var lb = Eat(); // {
        var items = new List<GreenNode> { lb };
        if (Cur != TokenKind.CloseBrace)
        {
            items.Add(ParseExpression());
            while (Cur == TokenKind.Comma) { items.Add(Eat()); items.Add(ParseExpression()); }
        }
        if (Cur != TokenKind.CloseBrace && !AtEof) items.Add(SkipUntil(k => k is TokenKind.CloseBrace or TokenKind.Comma));
        items.Add((Cur == TokenKind.CloseBrace) ? Eat() : MissingWithDiag(TokenKind.CloseBrace, "'}' expected"));
        return new GreenSyntax(NodeKind.TableConstructor, items.ToArray());
    }

    // ---- recovery helpers ----
    // Consume >=1 token until `stop` is true (or EOF), wrapping them in a SkippedTokens node.
    private GreenNode SkipUntil(Func<TokenKind, bool> stop)
    {
        var toks = new List<GreenNode>();
        if (AtEof) return new GreenSyntax(NodeKind.SkippedTokens);
        do { toks.Add(Eat()); } while (!AtEof && !stop(Cur));
        return new GreenSyntax(NodeKind.SkippedTokens, toks.ToArray());
    }

    private GreenNode MissingExpr() => new GreenSyntax(NodeKind.NameRef, Missing(TokenKind.Ident));
    private GreenToken MissingWithDiag(TokenKind k, string msg) { Diag(msg); return Missing(k); }

    private static GreenNode[] Prepend(GreenNode head, List<GreenNode> tail)
    { var a = new GreenNode[tail.Count + 1]; a[0] = head; for (int i = 0; i < tail.Count; i++) a[i + 1] = tail[i]; return a; }
    private static List<GreenNode> Append(List<GreenNode> list, GreenNode tail) { list.Add(tail); return list; }
}
