using Xunit;

namespace Semanticus.DaxSpike;

// Hand-written cases: the grammar subset + nasty trivia, plus golden structure spot-checks.
public class HandwrittenTests
{
    // ---- round-trip: the invariant is byte-exact ToFullString() == source ----
    public static IEnumerable<object[]> RoundTripCases()
    {
        var xs = new[]
        {
            // literals + primaries
            "1", "1.5", ".5", "\"hello\"", "\"a\"\"b\"", "TRUE()", "FALSE()", "BLANK()",
            "DT\"2020-01-01\"", "[Measure]", "'Table'", "'Sales Table'[Amount]", "Table[Col]",
            // operators + precedence
            "1 + 2 * 3", "-2^2", "2^-3", "a && b || c", "NOT a = b", "1 & 2 & 3",
            "a <= b", "a <> b", "a == b", "x IN {1,2,3}", "(1 + 2)", "( 1 , 2 )",
            // calls
            "SUM('Table'[c])", "CALCULATE([Sales], Region = \"West\")", "SUMX(Table, [a] * [b])",
            "IF(1, 2, 3)", "COALESCE()", "FUNC(1,,3)", "INFO.VIEW.MEASURES()",
            "OUTER(INNER(1, 2), {3, 4})",
            // VAR / RETURN incl nested
            "VAR x = 1 RETURN x",
            "VAR a = 1\nVAR b = 2\nRETURN a + b",
            "VAR x = (VAR y = 1 RETURN y) RETURN x",
            // trivia torture: every comment kind, CRLF, tabs, leading/trailing space
            "  1 + 2  ", "\t1\t+\t2\t", "1 +\r\n2", "// lead\n1 + 2",
            "1 /* mid */ + 2", "1 + 2 // trail",
            "1 -- dash\n+ 2", "1 /* /* nested */ */ + 2", "1 + /* unterminated",
            "SUM(\n    'Table'[Amount]  -- inline\n)",
            // recovery / lexer-hostile input (must still round-trip byte-exactly)
            "SUM(@x)", "a ; b", "1 #$% 2", "= 1 + 2", "1.5E10", "1E5",
            "@param", "'unterminated", "\"unterminated", "[unterminated",
            // degenerate
            "", "   ", "\n\n", "// only a comment", "/* only */",
        };
        foreach (var x in xs) yield return new object[] { x };
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void RoundTrips_byte_exact(string src)
    {
        var r = DaxParser.Parse(src);
        Assert.Equal(src, r.Root.ToFullString());
    }

    // ---- golden structure spot-checks ----
    private static GreenSyntax FirstExpr(string src) => (GreenSyntax)((GreenSyntax)DaxParser.Parse(src).Root).Slots[0];
    private static string Raw(GreenNode n) => ((GreenToken)n).RawText;
    private static GreenSyntax N(GreenNode n) => (GreenSyntax)n;

    [Fact]
    public void Precedence_mul_binds_tighter_than_add()
    {
        var e = FirstExpr("1 + 2 * 3");
        Assert.Equal(NodeKind.BinaryExpr, e.Kind);
        Assert.Equal("+", Raw(e.Slots[1]));
        var right = N(e.Slots[2]);
        Assert.Equal(NodeKind.BinaryExpr, right.Kind);
        Assert.Equal("*", Raw(right.Slots[1]));   // (1 + (2 * 3))
    }

    [Fact]
    public void Power_binds_tighter_than_unary_minus()
    {
        var e = FirstExpr("-2^2");                 // -(2^2)
        Assert.Equal(NodeKind.UnaryExpr, e.Kind);
        Assert.Equal("-", Raw(e.Slots[0]));
        var pow = N(e.Slots[1]);
        Assert.Equal(NodeKind.BinaryExpr, pow.Kind);
        Assert.Equal("^", Raw(pow.Slots[1]));
    }

    [Fact]
    public void Or_binds_loosest()
    {
        var e = FirstExpr("a && b || c");          // ((a && b) || c)
        Assert.Equal(NodeKind.BinaryExpr, e.Kind);
        Assert.Equal("||", Raw(e.Slots[1]));
        Assert.Equal("&&", Raw(N(e.Slots[0]).Slots[1]));
    }

    [Fact]
    public void Not_is_lower_than_comparison()
    {
        var e = FirstExpr("NOT a = b");            // NOT (a = b)
        Assert.Equal(NodeKind.UnaryExpr, e.Kind);
        Assert.Equal("NOT", Raw(e.Slots[0]));
        Assert.Equal(NodeKind.BinaryExpr, N(e.Slots[1]).Kind);
        Assert.Equal("=", Raw(N(e.Slots[1]).Slots[1]));
    }

    [Fact]
    public void StrictEquality_is_one_operator_token()
    {
        var e = FirstExpr("a == b");
        Assert.Equal(NodeKind.BinaryExpr, e.Kind);
        Assert.Equal("==", Raw(e.Slots[1]));       // two lexer '=' merged to one '==' token
    }

    [Fact]
    public void QualifiedColumn_node_shape()
    {
        var e = FirstExpr("'Sales Table'[Amount]");
        Assert.Equal(NodeKind.QualifiedRef, e.Kind);
        Assert.Equal("'Sales Table'", Raw(e.Slots[0]));   // RAW spelling, not decoded 'Sales Table'
        Assert.Equal("[Amount]", Raw(e.Slots[1]));
    }

    [Fact]
    public void Var_return_scoping_shape()
    {
        var e = FirstExpr("VAR x = 1 RETURN x");
        Assert.Equal(NodeKind.VarExpr, e.Kind);
        var decl = N(e.Slots[0]);
        Assert.Equal(NodeKind.VarDecl, decl.Kind);
        Assert.Equal("VAR", Raw(decl.Slots[0]));
        Assert.Equal("x", Raw(decl.Slots[1]));
        Assert.Equal("=", Raw(decl.Slots[2]));
        Assert.Equal(NodeKind.LiteralExpr, N(decl.Slots[3]).Kind);
        var ret = N(e.Slots[1]);
        Assert.Equal(NodeKind.ReturnClause, ret.Kind);
        Assert.Equal("RETURN", Raw(ret.Slots[0]));
        Assert.Equal(NodeKind.NameRef, N(ret.Slots[1]).Kind);
    }

    [Fact]
    public void Dotted_call_name_is_one_identifier_despite_lexer_dropping_dots()
    {
        var e = FirstExpr("INFO.VIEW.MEASURES()");
        Assert.Equal(NodeKind.CallExpr, e.Kind);
        Assert.Equal("INFO.VIEW.MEASURES", Raw(e.Slots[0]));   // merged over the lexer defect
    }

    [Fact]
    public void Omitted_argument_is_explicit_zero_width_node()
    {
        var e = FirstExpr("FUNC(1,,3)");
        Assert.Equal(NodeKind.CallExpr, e.Kind);
        var args = N(e.Slots[1]);                  // ArgumentList: ( arg , omitted , arg )
        Assert.Contains(args.Slots, s => s is GreenSyntax g && g.Kind == NodeKind.OmittedArgument);
    }

    [Fact]
    public void Unparseable_run_becomes_skipped_tokens_but_round_trips()
    {
        var r = DaxParser.Parse("1 #$% 2");
        Assert.Equal("1 #$% 2", r.Root.ToFullString());
        Assert.True(r.Root.ContainsSkipped || r.Root.ContainsMissing);  // recovery happened
    }
}
