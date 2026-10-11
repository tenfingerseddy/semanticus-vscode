using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verify j12fx: VAR scope attacks against Microsoft's VAR page and dax.guide ("the nested variables are not visible outside of the scope of expression"; a variable is not
    /// visible in its own definition). Model as in Join12VarScopeTests (Sales and Other both have P; Metrics has measures P and New P).
    /// </summary>
    public sealed class VerifyJ12fxVarScopeTests
    {
        public static IEnumerable<object[]> Cases() => new (string Id, string Before, string Rename, string To, string After)[]
        {
            ("A1 two nested blocks then a sibling, Sales", "VAR t = Sales VAR a = VAR t = Other RETURN SUMX ( t, [P] ) VAR b = VAR u = 1 RETURN SUMX ( t, [P] ) + u VAR c = SUMX ( t, [P] ) RETURN a + b + c", "column:Sales/P", "Q", "VAR t = Sales VAR a = VAR t = Other RETURN SUMX ( t, [P] ) VAR b = VAR u = 1 RETURN SUMX ( t, [Q] ) + u VAR c = SUMX ( t, [Q] ) RETURN a + b + c"),
            ("A1 two nested blocks then a sibling, Other", "VAR t = Sales VAR a = VAR t = Other RETURN SUMX ( t, [P] ) VAR b = VAR u = 1 RETURN SUMX ( t, [P] ) + u VAR c = SUMX ( t, [P] ) RETURN a + b + c", "column:Other/P", "R", "VAR t = Sales VAR a = VAR t = Other RETURN SUMX ( t, [R] ) VAR b = VAR u = 1 RETURN SUMX ( t, [P] ) + u VAR c = SUMX ( t, [P] ) RETURN a + b + c"),
            ("A2 three deep with a sibling at the middle level, Sales", "VAR t = Sales VAR a = VAR t = Other VAR b = VAR t = Sales RETURN SUMX ( t, [P] ) VAR c = SUMX ( t, [P] ) RETURN b + c VAR d = SUMX ( t, [P] ) RETURN a + d", "column:Sales/P", "Q", "VAR t = Sales VAR a = VAR t = Other VAR b = VAR t = Sales RETURN SUMX ( t, [Q] ) VAR c = SUMX ( t, [P] ) RETURN b + c VAR d = SUMX ( t, [Q] ) RETURN a + d"),
            ("A2 three deep with a sibling at the middle level, Other", "VAR t = Sales VAR a = VAR t = Other VAR b = VAR t = Sales RETURN SUMX ( t, [P] ) VAR c = SUMX ( t, [P] ) RETURN b + c VAR d = SUMX ( t, [P] ) RETURN a + d", "column:Other/P", "R", "VAR t = Sales VAR a = VAR t = Other VAR b = VAR t = Sales RETURN SUMX ( t, [P] ) VAR c = SUMX ( t, [R] ) RETURN b + c VAR d = SUMX ( t, [P] ) RETURN a + d"),
            ("A3 nested block in a FILTER argument inside CALCULATE, Sales", "VAR t = Sales RETURN CALCULATE ( SUMX ( t, [P] ), FILTER ( ALL ( Other ), VAR t = Other RETURN SUMX ( t, [P] ) > 0 ) ) + SUMX ( t, [P] )", "column:Sales/P", "Q", "VAR t = Sales RETURN CALCULATE ( SUMX ( t, [Q] ), FILTER ( ALL ( Other ), VAR t = Other RETURN SUMX ( t, [P] ) > 0 ) ) + SUMX ( t, [Q] )"),
            ("A3 nested block in a FILTER argument inside CALCULATE, Other", "VAR t = Sales RETURN CALCULATE ( SUMX ( t, [P] ), FILTER ( ALL ( Other ), VAR t = Other RETURN SUMX ( t, [P] ) > 0 ) ) + SUMX ( t, [P] )", "column:Other/P", "R", "VAR t = Sales RETURN CALCULATE ( SUMX ( t, [P] ), FILTER ( ALL ( Other ), VAR t = Other RETURN SUMX ( t, [R] ) > 0 ) ) + SUMX ( t, [P] )"),
            ("A4 VAR after an open bracket inside a block, Sales", "VAR t = Sales VAR a = ( VAR t = Other RETURN SUMX ( t, [P] ) ) VAR b = SUMX ( t, [P] ) RETURN a + b", "column:Sales/P", "Q", "VAR t = Sales VAR a = ( VAR t = Other RETURN SUMX ( t, [P] ) ) VAR b = SUMX ( t, [Q] ) RETURN a + b"),
            ("A4 VAR after an open bracket inside a block, Other", "VAR t = Sales VAR a = ( VAR t = Other RETURN SUMX ( t, [P] ) ) VAR b = SUMX ( t, [P] ) RETURN a + b", "column:Other/P", "R", "VAR t = Sales VAR a = ( VAR t = Other RETURN SUMX ( t, [R] ) ) VAR b = SUMX ( t, [P] ) RETURN a + b"),
            ("A5 VAR after a comma inside a block, Sales", "VAR t = Sales VAR a = DIVIDE ( 1, VAR t = Other RETURN SUMX ( t, [P] ) ) VAR b = SUMX ( t, [P] ) RETURN a + b", "column:Sales/P", "Q", "VAR t = Sales VAR a = DIVIDE ( 1, VAR t = Other RETURN SUMX ( t, [P] ) ) VAR b = SUMX ( t, [Q] ) RETURN a + b"),
            ("A5 VAR after a comma inside a block, Other", "VAR t = Sales VAR a = DIVIDE ( 1, VAR t = Other RETURN SUMX ( t, [P] ) ) VAR b = SUMX ( t, [P] ) RETURN a + b", "column:Other/P", "R", "VAR t = Sales VAR a = DIVIDE ( 1, VAR t = Other RETURN SUMX ( t, [R] ) ) VAR b = SUMX ( t, [P] ) RETURN a + b"),
            ("A6 a RETURN that is itself an argument, Sales", "VAR t = Sales RETURN DIVIDE ( VAR t = Other RETURN SUMX ( t, [P] ), SUMX ( t, [P] ) )", "column:Sales/P", "Q", "VAR t = Sales RETURN DIVIDE ( VAR t = Other RETURN SUMX ( t, [P] ), SUMX ( t, [Q] ) )"),
            ("A6 a RETURN that is itself an argument, Other", "VAR t = Sales RETURN DIVIDE ( VAR t = Other RETURN SUMX ( t, [P] ), SUMX ( t, [P] ) )", "column:Other/P", "R", "VAR t = Sales RETURN DIVIDE ( VAR t = Other RETURN SUMX ( t, [R] ), SUMX ( t, [P] ) )"),
            ("A7 VAR inside a CALCULATE filter, Sales", "VAR t = Sales RETURN CALCULATE ( SUMX ( t, [P] ), VAR t = Other RETURN FILTER ( t, [P] > 0 ) ) + SUMX ( t, [P] )", "column:Sales/P", "Q", "VAR t = Sales RETURN CALCULATE ( SUMX ( t, [Q] ), VAR t = Other RETURN FILTER ( t, [P] > 0 ) ) + SUMX ( t, [Q] )"),
            ("A7 VAR inside a CALCULATE filter, Other", "VAR t = Sales RETURN CALCULATE ( SUMX ( t, [P] ), VAR t = Other RETURN FILTER ( t, [P] > 0 ) ) + SUMX ( t, [P] )", "column:Other/P", "R", "VAR t = Sales RETURN CALCULATE ( SUMX ( t, [P] ), VAR t = Other RETURN FILTER ( t, [R] > 0 ) ) + SUMX ( t, [P] )"),
            ("A8 shadow, then a sibling in the same inner block, then the outer, Sales", "VAR t = Sales RETURN VAR a = SUMX ( t, [P] ) VAR t = Other RETURN a + SUMX ( t, [P] )", "column:Sales/P", "Q", "VAR t = Sales RETURN VAR a = SUMX ( t, [Q] ) VAR t = Other RETURN a + SUMX ( t, [P] )"),
            ("A8 shadow, then a sibling in the same inner block, then the outer, Other", "VAR t = Sales RETURN VAR a = SUMX ( t, [P] ) VAR t = Other RETURN a + SUMX ( t, [P] )", "column:Other/P", "R", "VAR t = Sales RETURN VAR a = SUMX ( t, [P] ) VAR t = Other RETURN a + SUMX ( t, [R] )"),
            ("A9 comments and strings that hold VAR and RETURN, Sales", "VAR t = Sales VAR a = /* VAR t = Other RETURN */ VAR t = Other /* RETURN 1 VAR x = 2 */ RETURN SUMX ( t, [P] ) VAR s = \"VAR t = Other RETURN\" VAR b = SUMX ( t, [P] ) RETURN a + b", "column:Sales/P", "Q", "VAR t = Sales VAR a = /* VAR t = Other RETURN */ VAR t = Other /* RETURN 1 VAR x = 2 */ RETURN SUMX ( t, [P] ) VAR s = \"VAR t = Other RETURN\" VAR b = SUMX ( t, [Q] ) RETURN a + b"),
            ("A9 comments and strings that hold VAR and RETURN, Other", "VAR t = Sales VAR a = /* VAR t = Other RETURN */ VAR t = Other /* RETURN 1 VAR x = 2 */ RETURN SUMX ( t, [P] ) VAR s = \"VAR t = Other RETURN\" VAR b = SUMX ( t, [P] ) RETURN a + b", "column:Other/P", "R", "VAR t = Sales VAR a = /* VAR t = Other RETURN */ VAR t = Other /* RETURN 1 VAR x = 2 */ RETURN SUMX ( t, [R] ) VAR s = \"VAR t = Other RETURN\" VAR b = SUMX ( t, [P] ) RETURN a + b"),
            ("A10 an inner VAR's own initializer reads the outer name (after RETURN), Sales", "VAR t = Sales RETURN VAR t = FILTER ( Other, SUMX ( t, [P] ) > 0 ) RETURN COUNTROWS ( t )", "column:Sales/P", "Q", "VAR t = Sales RETURN VAR t = FILTER ( Other, SUMX ( t, [Q] ) > 0 ) RETURN COUNTROWS ( t )"),
            ("A10 an inner VAR's own initializer reads the outer name (after RETURN), Other", "VAR t = Sales RETURN VAR t = FILTER ( Other, SUMX ( t, [P] ) > 0 ) RETURN COUNTROWS ( t )", "column:Other/P", "R", "VAR t = Sales RETURN VAR t = FILTER ( Other, SUMX ( t, [P] ) > 0 ) RETURN COUNTROWS ( t )"),
            ("A10b an inner VAR's own initializer reads the outer name (nested initializer), Sales", "VAR t = Sales VAR a = VAR t = FILTER ( Other, SUMX ( t, [P] ) > 0 ) RETURN COUNTROWS ( t ) RETURN a", "column:Sales/P", "Q", "VAR t = Sales VAR a = VAR t = FILTER ( Other, SUMX ( t, [Q] ) > 0 ) RETURN COUNTROWS ( t ) RETURN a"),
            ("A10b an inner VAR's own initializer reads the outer name (nested initializer), Other", "VAR t = Sales VAR a = VAR t = FILTER ( Other, SUMX ( t, [P] ) > 0 ) RETURN COUNTROWS ( t ) RETURN a", "column:Other/P", "R", "VAR t = Sales VAR a = VAR t = FILTER ( Other, SUMX ( t, [P] ) > 0 ) RETURN COUNTROWS ( t ) RETURN a"),
            ("A12 a block that ends at a closing bracket while its owner is open, Sales", "VAR t = Sales VAR a = SUMX ( Other, VAR t = Other VAR u = SUMX ( t, [P] ) RETURN u ) VAR b = SUMX ( t, [P] ) RETURN a + b", "column:Sales/P", "Q", "VAR t = Sales VAR a = SUMX ( Other, VAR t = Other VAR u = SUMX ( t, [P] ) RETURN u ) VAR b = SUMX ( t, [Q] ) RETURN a + b"),
            ("A12 a block that ends at a closing bracket while its owner is open, Other", "VAR t = Sales VAR a = SUMX ( Other, VAR t = Other VAR u = SUMX ( t, [P] ) RETURN u ) VAR b = SUMX ( t, [P] ) RETURN a + b", "column:Other/P", "R", "VAR t = Sales VAR a = SUMX ( Other, VAR t = Other VAR u = SUMX ( t, [R] ) RETURN u ) VAR b = SUMX ( t, [P] ) RETURN a + b"),
            ("A13 a block in an inner RETURN, then a sibling of the outer block, Sales", "VAR t = Sales VAR a = VAR t = Other RETURN VAR u = SUMX ( t, [P] ) RETURN u VAR b = SUMX ( t, [P] ) RETURN a + b", "column:Sales/P", "Q", "VAR t = Sales VAR a = VAR t = Other RETURN VAR u = SUMX ( t, [P] ) RETURN u VAR b = SUMX ( t, [Q] ) RETURN a + b"),
            ("A13 a block in an inner RETURN, then a sibling of the outer block, Other", "VAR t = Sales VAR a = VAR t = Other RETURN VAR u = SUMX ( t, [P] ) RETURN u VAR b = SUMX ( t, [P] ) RETURN a + b", "column:Other/P", "R", "VAR t = Sales VAR a = VAR t = Other RETURN VAR u = SUMX ( t, [R] ) RETURN u VAR b = SUMX ( t, [P] ) RETURN a + b"),
            ("A14 a table-keeping VAR over the outer name after a nested block, Sales", "VAR t = Sales VAR a = VAR t = Other RETURN COUNTROWS ( t ) VAR f = FILTER ( t, [P] > 0 ) RETURN a + SUMX ( f, [P] )", "column:Sales/P", "Q", "VAR t = Sales VAR a = VAR t = Other RETURN COUNTROWS ( t ) VAR f = FILTER ( t, [Q] > 0 ) RETURN a + SUMX ( f, [Q] )"),
            ("A14 a table-keeping VAR over the outer name after a nested block, Other", "VAR t = Sales VAR a = VAR t = Other RETURN COUNTROWS ( t ) VAR f = FILTER ( t, [P] > 0 ) RETURN a + SUMX ( f, [P] )", "column:Other/P", "R", "VAR t = Sales VAR a = VAR t = Other RETURN COUNTROWS ( t ) VAR f = FILTER ( t, [P] > 0 ) RETURN a + SUMX ( f, [P] )"),
        }.Select(c => new object[] { c.Id, c.Before, c.Rename, c.To, c.After });

        // Lean suite (8 Oct): the default run keeps these rows; the whole matrix is the sweep.
        public static IEnumerable<object[]> CasesSample() => Cases().Where(r => new[] { "A1", "A10", "A8" }.Contains(((string)r[0]).Split(' ')[0]));

        [Theory]
        [MemberData(nameof(CasesSample))]
        public Task A_rename_follows_the_binding_the_dax_pages_give_sample(string id, string before, string rename, string to, string after) => A_rename_follows_the_binding_the_dax_pages_give(id, before, rename, to, after);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Cases))]
        public async Task A_rename_follows_the_binding_the_dax_pages_give(string id, string before, string rename, string to, string after)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("VerifyJ12fx", 1612);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                var sales = m.AddTable("Sales");
                sales.AddDataColumn("P", "P", null, DataType.Decimal);
                var other = m.AddTable("Other");
                other.AddDataColumn("P", "P", null, DataType.Decimal);
                var metrics = m.AddTable("Metrics");
                metrics.AddMeasure("New P", "0.9");
                metrics.AddMeasure("Host", before);
            });
            await engine.RenameObjectAsync(rename, to, "human");
            var got = await sessions.Require().ReadAsync(m => m.Tables["Metrics"].Measures["Host"].Expression);
            Assert.Equal(id + ": " + after, id + ": " + got);
        }
        // A10 as a number (Sol's A7 shape): the inner t's own initializer reads the OUTER t (Sales), since a VAR is visible only in the expressions after its own (dax.guide: "visible in
        // all the expressionN+1 of following variables"). Renaming Other[P] to "New P" must leave SUMX ( t, [P] ) alone; rewriting it makes it read measure [New P] (900) for each Sales row.
        [Fact]
        public async Task An_inner_var_s_own_initializer_reads_the_outer_table_not_itself()
        {
            const string before = "VAR t = Sales RETURN VAR t = FILTER ( Other, SUMX ( t, [P] ) > 15 ) RETURN COUNTROWS ( t )";
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("VerifyJ12fxOwnInit", 1612);
            await sessions.Require().MutateAsync("human", "seed", m =>
            {
                m.AddTable("Sales").AddDataColumn("P", "P", null, DataType.Decimal);
                m.AddTable("Other").AddDataColumn("P", "P", null, DataType.Decimal);
                var metrics = m.AddTable("Metrics");
                metrics.AddMeasure("New P", "900");
                metrics.AddMeasure("Host", before);
            });
            await engine.RenameObjectAsync("column:Other/P", "New P", "human");
            Assert.Equal(before, await sessions.Require().ReadAsync(m => m.Tables["Metrics"].Measures["Host"].Expression));
        }
    }
}
