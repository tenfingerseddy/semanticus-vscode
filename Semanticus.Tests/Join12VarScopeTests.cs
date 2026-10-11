using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Join 12, J-V: a VAR name is visible only in its own VAR ... RETURN block (Microsoft's VAR page; SQLBI, "When are variables evaluated in DAX?"). The block ends at the end of its
    /// RETURN expression: a closing bracket, an argument comma, or the RETURN of a block that declares it in its own expression. Model as in VerifySolFx2Tests (Sales and Other both have P).
    /// </summary>
    public sealed class Join12VarScopeTests
    {
        public static IEnumerable<object[]> Cases() => new (string Id, string Host, bool Lean, string Before, string Rename, string To, string After)[]
        {
            ("a VAR straight after RETURN opens a block, Other", "m", false, "VAR t = Sales RETURN VAR t = Other RETURN SUMX ( t, [P] )", "column:Other/P", "R", "VAR t = Sales RETURN VAR t = Other RETURN SUMX ( t, [R] )"),
            ("a VAR straight after RETURN opens a block, Sales", "m", false, "VAR t = Sales RETURN VAR t = Other RETURN SUMX ( t, [P] )", "column:Sales/P", "Q", "VAR t = Sales RETURN VAR t = Other RETURN SUMX ( t, [P] )"),
            ("an argument comma ends an inner block, Sales", "m", false, "VAR t = Sales RETURN IF ( TRUE (), VAR t = Other RETURN SUMX ( t, [P] ), SUMX ( t, [P] ) )", "column:Sales/P", "Q", "VAR t = Sales RETURN IF ( TRUE (), VAR t = Other RETURN SUMX ( t, [P] ), SUMX ( t, [Q] ) )"),
            ("an argument comma ends an inner block, Other", "m", false, "VAR t = Sales RETURN IF ( TRUE (), VAR t = Other RETURN SUMX ( t, [P] ), SUMX ( t, [P] ) )", "column:Other/P", "R", "VAR t = Sales RETURN IF ( TRUE (), VAR t = Other RETURN SUMX ( t, [R] ), SUMX ( t, [P] ) )"),
            ("two VARs of one block are both visible in the RETURN", "m", false, "VAR a = Sales VAR b = Other RETURN SUMX ( a, [P] ) + SUMX ( b, [P] )", "column:Sales/P", "Q", "VAR a = Sales VAR b = Other RETURN SUMX ( a, [Q] ) + SUMX ( b, [P] )"),
            ("a name hidden in a block is the outer one again after it, three deep", "m", false, "VAR t = Sales RETURN SUMX ( Other, VAR t = Other RETURN SUMX ( Sales, VAR t = Sales RETURN COUNTROWS ( t ) ) ) + SUMX ( t, [P] )", "column:Sales/P", "Q", "VAR t = Sales RETURN SUMX ( Other, VAR t = Other RETURN SUMX ( Sales, VAR t = Sales RETURN COUNTROWS ( t ) ) ) + SUMX ( t, [Q] )"),
            // Join 12 fix round (Sol A7 finding 1): a block ends when the initializer expression it stands in ends, and it gives back what it hid.
            ("three deep, then a sibling VAR reads the outer table, Sales", "m", false, "VAR t = Sales VAR a = VAR t = Other RETURN VAR t = Sales RETURN VAR t = Other RETURN SUMX ( t, [P] ) VAR b = SUMX ( t, [P] ) RETURN a + b", "column:Sales/P", "Q", "VAR t = Sales VAR a = VAR t = Other RETURN VAR t = Sales RETURN VAR t = Other RETURN SUMX ( t, [P] ) VAR b = SUMX ( t, [Q] ) RETURN a + b"),
            ("three deep, then a sibling VAR reads the outer table, Other", "m", false, "VAR t = Sales VAR a = VAR t = Other RETURN VAR t = Sales RETURN VAR t = Other RETURN SUMX ( t, [P] ) VAR b = SUMX ( t, [P] ) RETURN a + b", "column:Other/P", "R", "VAR t = Sales VAR a = VAR t = Other RETURN VAR t = Sales RETURN VAR t = Other RETURN SUMX ( t, [R] ) VAR b = SUMX ( t, [P] ) RETURN a + b"),
            ("two sibling VARs after a nested block both read the outer table, Sales", "m", false, "VAR t = Sales VAR a = VAR t = Other RETURN SUMX ( t, [P] ) VAR b = SUMX ( t, [P] ) VAR c = SUMX ( t, [P] ) RETURN a + b + c", "column:Sales/P", "Q", "VAR t = Sales VAR a = VAR t = Other RETURN SUMX ( t, [P] ) VAR b = SUMX ( t, [Q] ) VAR c = SUMX ( t, [Q] ) RETURN a + b + c"),
            ("two sibling VARs after a nested block, the nested one reads its own, Other", "m", false, "VAR t = Sales VAR a = VAR t = Other RETURN SUMX ( t, [P] ) VAR b = SUMX ( t, [P] ) VAR c = SUMX ( t, [P] ) RETURN a + b + c", "column:Other/P", "R", "VAR t = Sales VAR a = VAR t = Other RETURN SUMX ( t, [R] ) VAR b = SUMX ( t, [P] ) VAR c = SUMX ( t, [P] ) RETURN a + b + c"),
            ("a nested block in a function argument ends at the comma, then a sibling VAR, Sales", "m", false, "VAR t = Sales VAR a = IF ( TRUE (), VAR t = Other RETURN SUMX ( t, [P] ), 0 ) VAR b = SUMX ( t, [P] ) RETURN a + b", "column:Sales/P", "Q", "VAR t = Sales VAR a = IF ( TRUE (), VAR t = Other RETURN SUMX ( t, [P] ), 0 ) VAR b = SUMX ( t, [Q] ) RETURN a + b"),
            ("a nested block in a function argument ends at the comma, then a sibling VAR, Other", "m", false, "VAR t = Sales VAR a = IF ( TRUE (), VAR t = Other RETURN SUMX ( t, [P] ), 0 ) VAR b = SUMX ( t, [P] ) RETURN a + b", "column:Other/P", "R", "VAR t = Sales VAR a = IF ( TRUE (), VAR t = Other RETURN SUMX ( t, [R] ), 0 ) VAR b = SUMX ( t, [P] ) RETURN a + b"),
            ("a declaring block inside an argument with a nested block, then the outer RETURN, Sales", "m", false, "VAR t = Sales RETURN IF ( TRUE (), VAR u = VAR t = Other RETURN SUMX ( t, [P] ) VAR w = SUMX ( t, [P] ) RETURN u + w, 0 ) + SUMX ( t, [P] )", "column:Sales/P", "Q", "VAR t = Sales RETURN IF ( TRUE (), VAR u = VAR t = Other RETURN SUMX ( t, [P] ) VAR w = SUMX ( t, [Q] ) RETURN u + w, 0 ) + SUMX ( t, [Q] )"),
        }.Select(c => new object[] { c.Id, c.Host, c.Lean, c.Before, c.Rename, c.To, c.After });

        [Theory]
        [MemberData(nameof(Cases))]
        public async Task A_rename_follows_the_binding_the_dax_pages_give(string id, string host, bool lean, string before, string rename, string to, string after)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Join12VarScope", 1612);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable("Sales");
                sales.AddDataColumn("Label", "Label", null, DataType.String);
                sales.AddDataColumn("Amount", "Amount", null, DataType.Decimal);
                sales.AddDataColumn("P", "P", null, DataType.Decimal);
                var other = m.AddTable("Other");
                if (lean) other.AddDataColumn("K", "K", null, DataType.Decimal); else other.AddDataColumn("P", "P", null, DataType.Decimal);
                var metrics = m.AddTable("Metrics");
                if (!lean) { metrics.AddMeasure("P", "0.5"); metrics.AddMeasure("New P", "0.9"); }
                if (host == "m") metrics.AddMeasure("Host", before);
                else (host == "s" ? sales : other).AddCalculatedColumn("Host", before);
            });
            await engine.RenameObjectAsync(rename, to, "human");
            var hostTable = host == "m" ? "Metrics" : host == "s" ? "Sales" : "Other";
            var got = await sessions.Require().ReadAsync(m => host == "m"
                ? m.Tables[hostTable].Measures["Host"].Expression
                : ((CalculatedColumn)m.Tables[hostTable].Columns["Host"]).Expression);
            Assert.Equal(id + ": " + after, id + ": " + got);
        }
    }
}
