using Semanticus.Dax.Syntax;

namespace Semanticus.Dax.Tests;

/// <summary>
/// Every A2 §11 <c>G-P-*</c> input in one place, with the options its row is authored under. The goldens
/// themselves live one row per test; this is the corpus the PROPERTY tests sweep — the <c>G-P-REC-012</c>
/// bad-token mutation, the §8.1 rule 6 conservation invariant, the §9 capability sweep, and the §3.1
/// parity sweep — so those can never silently cover a smaller set than the goldens do.
///
/// <para>
/// The three depth rows (<c>G-P-REC-007</c>, <c>-008</c>, <c>-009</c>) are deliberately absent: they are
/// hundreds to hundreds of thousands of tokens long, and mutating every boundary of them would turn a
/// property sweep into a benchmark. They carry their own assertions in
/// <see cref="ParserRootRecoveryTests"/>.
/// </para>
/// </summary>
internal static class ParserBattery
{
    private static readonly DaxParseOptions Expression = DaxParseOptions.Default;
    private static readonly DaxParseOptions LeadingEquals = new() { AllowLeadingEquals = true };
    private static readonly DaxParseOptions UdfBody = new() { SourceKind = DaxSourceKind.UdfBody };

    public static IEnumerable<(string Id, string Source, DaxParseOptions Options)> All()
    {
        // §11.1 precedence
        yield return ("G-P-PREC-001", "-2 ^ 2", Expression);
        yield return ("G-P-PREC-002", "2 ^ -3", Expression);
        yield return ("G-P-PREC-003", "2 ^ 3 ^ 2", Expression);
        yield return ("G-P-PREC-004", "- - 2", Expression);
        yield return ("G-P-PREC-005", "1 + 2 * 3", Expression);
        yield return ("G-P-PREC-006", "1 - 2 - 3", Expression);
        yield return ("G-P-PREC-007", "1 & 2 & 3", Expression);
        yield return ("G-P-PREC-008", "a = b & c", Expression);
        yield return ("G-P-PREC-009", "NOT a = b", Expression);
        yield return ("G-P-PREC-010", "NOT a && b", Expression);
        yield return ("G-P-PREC-011", "a && b || c", Expression);
        yield return ("G-P-PREC-012", "a < b < c", Expression);
        yield return ("G-P-PREC-013", "a == b", Expression);
        yield return ("G-P-PREC-014", "[x] IN {1,2}", Expression);
        yield return ("G-P-PREC-015", "NOT [x] IN {1}", Expression);
        yield return ("G-P-PREC-016", "NOT(ISBLANK([x]))", Expression);
        yield return ("G-P-PREC-017", "1 + VAR x = 1 RETURN x", Expression);
        yield return ("G-P-PREC-018", "1 + (VAR x = 1 RETURN x)", Expression);
        yield return ("G-P-PREC-019", "1 & 2 + 3", Expression);
        yield return ("G-P-PREC-020", "1 + 2 & 3", Expression);
        yield return ("G-P-PREC-021", "1 & 2 * 3", Expression);
        yield return ("G-P-PREC-022", "a & b && c", Expression);

        // §11.2 names
        yield return ("G-P-NAME-001", "Sales[Amount]", Expression);
        yield return ("G-P-NAME-002", "'Sales Table'[Amount]", Expression);
        yield return ("G-P-NAME-003", "[Amount]", Expression);
        yield return ("G-P-NAME-004", "Sales   [Amount]", Expression);
        yield return ("G-P-NAME-005", "Sales\n[Amount]", Expression);
        yield return ("G-P-NAME-006", "Sales /*c*/ [Amount]", Expression);
        yield return ("G-P-NAME-007", "INFO.VIEW.MEASURES()", Expression);
        yield return ("G-P-NAME-008", "DaxLib.Format.Pct(1)", Expression);
        yield return ("G-P-NAME-009", "INFO.VIEW.MEASURES", Expression);
        yield return ("G-P-NAME-010", "A..B", Expression);
        yield return ("G-P-NAME-011", "A.", Expression);
        yield return ("G-P-NAME-012", ".A", Expression);
        yield return ("G-P-NAME-013", ".5", Expression);
        yield return ("G-P-NAME-014", "A.B[C]", Expression);
        yield return ("G-P-NAME-015", "'Table'(1)", Expression);
        yield return ("G-P-NAME-016a", "TRUE()", Expression);
        yield return ("G-P-NAME-016b", "FALSE()", Expression);
        yield return ("G-P-NAME-016c", "BLANK()", Expression);
        yield return ("G-P-NAME-017", "Date[Key]", Expression);
        yield return ("G-P-NAME-018", "TOTALYTD([Sales], 'Fiscal')", Expression);
        yield return ("G-P-NAME-019", "'Sales Table'", Expression);

        // §11.3 calls
        yield return ("G-P-CALL-001", "F()", Expression);
        yield return ("G-P-CALL-002", "F(1)", Expression);
        yield return ("G-P-CALL-003", "F(1,,3)", Expression);
        yield return ("G-P-CALL-004", "F(,)", Expression);
        yield return ("G-P-CALL-005", "F(1,)", Expression);
        yield return ("G-P-CALL-006", "F(,1)", Expression);
        yield return ("G-P-CALL-007", "F(G(1), H())", Expression);
        yield return ("G-P-CALL-008", "F(1", Expression);
        yield return ("G-P-CALL-009", "F(VAR x = 1 RETURN x)", Expression);

        // §11.4 constructors
        yield return ("G-P-CTOR-001", "(1)", Expression);
        yield return ("G-P-CTOR-002", "(1, 2)", Expression);
        yield return ("G-P-CTOR-003", "(a, b) IN {(1,2)}", Expression);
        yield return ("G-P-CTOR-004", "{1,2,3}", Expression);
        yield return ("G-P-CTOR-005", "{(1,2,3)}", Expression);
        yield return ("G-P-CTOR-006", "{}", Expression);
        yield return ("G-P-CTOR-007", "()", Expression);
        yield return ("G-P-CTOR-008", "(1,)", Expression);
        yield return ("G-P-CTOR-009", "{1,}", Expression);
        yield return ("G-P-CTOR-010", "{1, 2", Expression);
        yield return ("G-P-CTOR-011", "F((1,2))", Expression);

        // §11.5 variables
        yield return ("G-P-VAR-001", "VAR a = 1 RETURN a", Expression);
        yield return ("G-P-VAR-002", "VAR a = 1 VAR b = a RETURN b", Expression);
        yield return ("G-P-VAR-003", "VAR a = 1 VAR a = 2 RETURN a", Expression);
        yield return ("G-P-VAR-004", "VAR a = VAR b = 1 RETURN b RETURN a", Expression);
        yield return ("G-P-VAR-005", "VAR a = 1 RETURN VAR b = 2 RETURN b", Expression);
        yield return ("G-P-VAR-006", "VAR a = 1", Expression);
        yield return ("G-P-VAR-007", "VAR = 1 RETURN 1", Expression);
        yield return ("G-P-VAR-008", "VAR a 1 RETURN a", Expression);
        yield return ("G-P-VAR-009", "VAR 'a' = 1 RETURN 1", Expression);
        yield return ("G-P-VAR-010", "VAR __a = 1 RETURN __a", Expression);

        // §11.6 UDF body
        yield return ("G-P-UDF-001", "() => 1", UdfBody);
        yield return ("G-P-UDF-002", "(x) => x", UdfBody);
        yield return ("G-P-UDF-003", "(position: STRING) => position", UdfBody);
        yield return ("G-P-UDF-004", "(startTime: EXPR) => startTime", UdfBody);
        yield return ("G-P-UDF-005", "(c: ANYREF EXPR) => 1", UdfBody);
        yield return ("G-P-UDF-006", "(n: SCALAR INT64) => n", UdfBody);
        yield return ("G-P-UDF-007", "(e: NUMERIC EXPR) => e", UdfBody);
        yield return ("G-P-UDF-008", "(t: TABLE VAL) => t", UdfBody);
        yield return ("G-P-UDF-009", "(x: WIDGET) => x", UdfBody);
        yield return ("G-P-UDF-010", "(x: EXPR ANYREF) => x", UdfBody);
        yield return ("G-P-UDF-011", "(x: SCALAR SCALAR) => x", UdfBody);
        yield return ("G-P-UDF-012", "(x: A B C D) => x", UdfBody);
        yield return ("G-P-UDF-013", "(x: ) => x", UdfBody);
        yield return ("G-P-UDF-014", "(x = 1) => x", UdfBody);
        yield return ("G-P-UDF-015", "(x: INT64 = 1, y) => x + y", UdfBody);
        yield return ("G-P-UDF-016", "(x) 1", UdfBody);
        yield return ("G-P-UDF-017", "(x,) => x", UdfBody);
        yield return ("G-P-UDF-018", "1 + ((x) => x)", Expression);
        yield return ("G-P-UDF-019", "// doc\n(x) => x", UdfBody);
        yield return ("G-P-UDF-020", "/// <summary>\n/// Adds one.\n/// </summary>\n/// <param name=\"x\">the value</param>\n(x) => x + 1", UdfBody);
        yield return ("G-P-UDF-021", "x) => x", UdfBody);
        yield return ("G-P-UDF-022", "x => x", UdfBody);

        // §11.7 reserved keywords in name positions
        yield return ("G-P-KW-001", "TOTAL[Amount]", Expression);
        yield return ("G-P-KW-002", "VAR Total = 1 RETURN Total", Expression);
        yield return ("G-P-KW-003", "GROUP[Id]", Expression);
        yield return ("G-P-KW-004", "MEASURE[Value]", Expression);
        yield return ("G-P-KW-005", "COLUMN[Value]", Expression);
        yield return ("G-P-KW-006", "SUM(TABLE[X])", Expression);
        yield return ("G-P-KW-007", "AXIS.SUB.F(1)", Expression);
        yield return ("G-P-KW-008", "START(1)", Expression);
        yield return ("G-P-KW-009", "AT + 1", Expression);
        yield return ("G-P-KW-010", "VAR RETURN = 1 RETURN 1", Expression);
        yield return ("G-P-KW-011", "SUM(NOT)", Expression);
        yield return ("G-P-KW-012", "SUM(IN)", Expression);
        yield return ("G-P-KW-013", "SUM(RETURN)", Expression);
        yield return ("G-P-KW-014a", "'Total'[x]", Expression);
        yield return ("G-P-KW-014b", "[Group]", Expression);
        yield return ("G-P-KW-014c", "[Column]", Expression);
        yield return ("G-P-KW-014d", "'Group'", Expression);
        yield return ("G-P-KW-015", "define[x]", Expression);
        yield return ("G-P-KW-016", "(TABLE: TABLE VAL) => TABLE", UdfBody);
        foreach (var keyword in Keywords) yield return ($"G-P-KW-017-{keyword}", keyword, Expression);

        // §11.8 roots and recovery (the three depth rows are excluded; see the class remarks)
        yield return ("G-P-ROOT-001", "= 1 + 1", LeadingEquals);
        yield return ("G-P-ROOT-002", "= 1 + 1", Expression);
        yield return ("G-P-ROOT-003", "1 = 1", Expression);
        yield return ("G-P-ROOT-004", "== 1", Expression);
        yield return ("G-P-ROOT-005", "1 + 1 2 + 2", Expression);
        yield return ("G-P-ROOT-006", "", Expression);
        yield return ("G-P-ROOT-007", "   // just a comment", Expression);
        yield return ("G-P-ROOT-008", "VAR a = SUM('T'[c], ) RETURN a + TOTAL[x]", Expression);
        yield return ("G-P-REC-001", "SUM(", Expression);
        yield return ("G-P-REC-002", ")))", Expression);
        yield return ("G-P-REC-003", "SUM(#$%)", Expression);
        yield return ("G-P-REC-004", "\"unterminated", Expression);
        yield return ("G-P-REC-005", "SUM(1))", Expression);
        yield return ("G-P-REC-006", ",,,,", Expression);
        yield return ("G-P-REC-010", "VAR a = ) RETURN a", Expression);
        yield return ("G-P-REC-011", "F(1, , )", Expression);
        yield return ("G-P-REC-013", "SUM(1 2 3)", Expression);
        yield return ("G-P-REC-014", "1 + 1 2 3 4", Expression);
        yield return ("G-P-REC-015", "A..B..C", Expression);
    }

    /// <summary>The 21 A1 §2.4 reserved keywords, in table order (A2 §7.2).</summary>
    public static readonly string[] Keywords =
    {
        "DEFINE", "EVALUATE", "ORDER", "BY", "START", "AT", "VAR", "RETURN", "MEASURE", "COLUMN", "TABLE",
        "FUNCTION", "WITH", "VISUAL", "SHAPE", "AXIS", "GROUP", "TOTAL", "DENSIFY", "IN", "NOT"
    };
}
