using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Verifier, m11/solfx round 2 (Y1 SUMMARIZE, Y2 GROUPBY + CURRENTGROUP, Y3 VAR tables). Each case states the binding the DAX pages give.
    /// Model: Sales ( Label, Amount, P ), Other ( P ) [lean: Other has no P], Metrics ( measure P = 0.5, measure New P = 0.9 ) [lean: no measures].
    /// Host: m = measure on Metrics, s = calculated column on Sales, o = calculated column on Other.
    /// Sources: learn.microsoft.com/en-us/dax/summarize-function-dax, groupby-function-dax, currentgroup-function-dax, var-dax; dax.guide/summarize, dax.guide/var;
    /// sqlbi.com/articles/all-the-secrets-of-summarize ("a row context, containing the cluster headers").
    /// Left failing on purpose (verify, solfx round 2): P1, P2, P2b, P2c. The walk keeps ONE flat map of VAR names for the whole formula, so an inner VAR that reuses an
    /// outer name (legal DAX: sqlbi.com/articles/when-are-variables-evaluated-in-dax, "the outer Result is not visible in the inner block because it is being hidden by a
    /// closer variable with the same name") overwrites the outer one for the rest of the formula. SUMX ( t, [P] ) after the inner block then reads Other's row: renaming
    /// Other[P] to "New P" moves a scan of Sales onto measure [New P] (0.9 for each row instead of Sales[P]), a wrong number. P2, P2b, P2c pass on 0f6ee998 (the VAR was
    /// unknown there) and are new in round 2; P1 (Sales[P] renamed, [P] left on measure [P]) is wrong on both.
    /// </summary>
    public sealed class VerifySolFx2Tests
    {
        public static IEnumerable<object[]> Cases() => new (string Id, string Host, bool Lean, string Before, string Rename, string To, string After)[]
        {
            // ---- Y1 SUMMARIZE: the expression row holds the groupBy columns only
            ("Y1a rollup grouped", "m", false, "SUMMARIZE ( Sales, ROLLUP ( Sales[P] ), \"x\", [P] )", "column:Sales/P", "Q", "SUMMARIZE ( Sales, ROLLUP ( 'Sales'[Q] ), \"x\", [Q] )"),
            ("Y1b rollup ungrouped onto measure", "m", false, "SUMX ( SUMMARIZE ( Sales, ROLLUP ( Sales[Label] ), \"x\", [P] ), [x] )", "column:Sales/P", "New P", "SUMX ( SUMMARIZE ( Sales, ROLLUP ( Sales[Label] ), \"x\", [P] ), [x] )"),
            ("Y1c rollupgroup grouped", "m", false, "SUMMARIZE ( Sales, ROLLUP ( ROLLUPGROUP ( Sales[Label], Sales[P] ) ), \"x\", [P] )", "column:Sales/P", "Q", "SUMMARIZE ( Sales, ROLLUP ( ROLLUPGROUP ( Sales[Label], 'Sales'[Q] ) ), \"x\", [Q] )"),
            ("Y1d two groupBy, rename P", "m", false, "SUMMARIZE ( Sales, Sales[Label], Sales[P], \"x\", [P] & [Label] )", "column:Sales/P", "Q", "SUMMARIZE ( Sales, Sales[Label], 'Sales'[Q], \"x\", [Q] & [Label] )"),
            ("Y1e two groupBy, rename Label", "m", false, "SUMMARIZE ( Sales, Sales[Label], Sales[P], \"x\", [P] & [Label] )", "column:Sales/Label", "L2", "SUMMARIZE ( Sales, 'Sales'[L2], Sales[P], \"x\", [P] & [L2] )"),
            ("Y1f two groupBy, ungrouped onto measure", "m", false, "SUMMARIZE ( Sales, Sales[Label], Sales[Amount], \"x\", [P] )", "column:Sales/P", "New P", "SUMMARIZE ( Sales, Sales[Label], Sales[Amount], \"x\", [P] )"),
            ("Y1g groupBy of other table, rename it", "m", false, "SUMMARIZE ( Sales, Other[P], \"x\", [P] )", "column:Other/P", "R", "SUMMARIZE ( Sales, 'Other'[R], \"x\", [R] )"),
            ("Y1h groupBy of other table, rename Sales", "m", false, "SUMMARIZE ( Sales, Other[P], \"x\", [P] )", "column:Sales/P", "New P", "SUMMARIZE ( Sales, Other[P], \"x\", [P] )"),
            ("Y1i quoted qualifier", "m", false, "SUMMARIZE ( Sales, 'Sales'[P], \"x\", [P] )", "column:Sales/P", "Q", "SUMMARIZE ( Sales, 'Sales'[Q], \"x\", [Q] )"),
            ("Y1j quoted other qualifier", "m", false, "SUMMARIZE ( Sales, 'Other'[P], \"x\", [P] )", "column:Sales/P", "New P", "SUMMARIZE ( Sales, 'Other'[P], \"x\", [P] )"),
            ("Y1k addcolumns over summarize, ungrouped", "m", false, "ADDCOLUMNS ( SUMMARIZE ( Sales, Sales[Label] ), \"y\", [P] )", "column:Sales/P", "New P", "ADDCOLUMNS ( SUMMARIZE ( Sales, Sales[Label] ), \"y\", [P] )"),
            ("Y1l addcolumns over summarize expression", "m", false, "ADDCOLUMNS ( SUMMARIZE ( Sales, Sales[Label], \"x\", [P] ), \"y\", [x] )", "column:Sales/P", "New P", "ADDCOLUMNS ( SUMMARIZE ( Sales, Sales[Label], \"x\", [P] ), \"y\", [x] )"),
            ("Y1n calc column on Sales, ungrouped is the caller row", "s", false, "COUNTROWS ( SUMMARIZE ( Sales, Sales[Label], \"x\", [P] ) )", "column:Sales/P", "Q", "COUNTROWS ( SUMMARIZE ( Sales, Sales[Label], \"x\", [Q] ) )"),
            ("Y1o calc column on Other, Sales rename", "o", false, "COUNTROWS ( SUMMARIZE ( Sales, Sales[Label], \"x\", [P] ) )", "column:Sales/P", "Q", "COUNTROWS ( SUMMARIZE ( Sales, Sales[Label], \"x\", [P] ) )"),
            ("Y1p calc column on Other, Other rename", "o", false, "COUNTROWS ( SUMMARIZE ( Sales, Sales[Label], \"x\", [P] ) )", "column:Other/P", "R", "COUNTROWS ( SUMMARIZE ( Sales, Sales[Label], \"x\", [R] ) )"),
            ("Y1q scan inside summarize expression", "m", false, "SUMMARIZE ( Sales, Sales[Label], \"x\", SUMX ( Other, [P] ) )", "column:Other/P", "R", "SUMMARIZE ( Sales, Sales[Label], \"x\", SUMX ( Other, [R] ) )"),
            ("Y1r bare groupBy", "m", false, "SUMMARIZE ( Sales, [Label], \"x\", [Label] )", "column:Sales/Label", "L2", "SUMMARIZE ( Sales, [L2], \"x\", [L2] )"),
            ("Y1s DocPBI2 shape", "m", false, "SUMX ( SUMMARIZE ( Sales, Sales[Label], 'Sales'[Amount], \"x\", [P] * [Amount] ), [x] )", "column:Sales/Amount", "A2", "SUMX ( SUMMARIZE ( Sales, Sales[Label], 'Sales'[A2], \"x\", [P] * [A2] ), [x] )"),
            ("Y1t summarize over var", "m", false, "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMMARIZE ( t, Sales[P], \"x\", [P] )", "column:Sales/P", "Q", "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMMARIZE ( t, 'Sales'[Q], \"x\", [Q] )"),
            ("Y1u summarize over addcolumns, grouped", "m", false, "SUMMARIZE ( ADDCOLUMNS ( Sales, \"z\", 1 ), Sales[P], \"x\", [P] )", "column:Sales/P", "Q", "SUMMARIZE ( ADDCOLUMNS ( Sales, \"z\", 1 ), 'Sales'[Q], \"x\", [Q] )"),
            ("Y1v no spaces", "m", false, "SUMMARIZE(Sales,Sales[Label],\"x\",[P])", "column:Sales/P", "New P", "SUMMARIZE(Sales,Sales[Label],\"x\",[P])"),
            // ---- Y2 GROUPBY + CURRENTGROUP: the X aggregator scans rows of GROUPBY's table argument
            ("Y2a sumx", "m", false, "GROUPBY ( Sales, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [P] ) )", "column:Sales/P", "Q", "GROUPBY ( Sales, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [Q] ) )"),
            ("Y2b maxx", "m", false, "GROUPBY ( Sales, Sales[Label], \"x\", MAXX ( CURRENTGROUP (), [P] ) )", "column:Sales/P", "Q", "GROUPBY ( Sales, Sales[Label], \"x\", MAXX ( CURRENTGROUP (), [Q] ) )"),
            ("Y2c countx", "m", false, "GROUPBY ( Sales, Sales[Label], \"x\", COUNTX ( CURRENTGROUP (), [P] ) )", "column:Sales/P", "Q", "GROUPBY ( Sales, Sales[Label], \"x\", COUNTX ( CURRENTGROUP (), [Q] ) )"),
            ("Y2d other rename onto measure", "m", false, "GROUPBY ( Sales, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [P] ) )", "column:Other/P", "New P", "GROUPBY ( Sales, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [P] ) )"),
            ("Y2e inside an Other scan, Other rename", "m", false, "SUMX ( Other, COUNTROWS ( GROUPBY ( Sales, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [P] ) ) ) )", "column:Other/P", "New P", "SUMX ( Other, COUNTROWS ( GROUPBY ( Sales, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [P] ) ) ) )"),
            ("Y2f inside an Other scan, Sales rename", "m", false, "SUMX ( Other, COUNTROWS ( GROUPBY ( Sales, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [P] ) ) ) )", "column:Sales/P", "Q", "SUMX ( Other, COUNTROWS ( GROUPBY ( Sales, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [Q] ) ) ) )"),
            ("Y2g over filter", "m", false, "GROUPBY ( FILTER ( Sales, Sales[Amount] > 0 ), Sales[Label], \"x\", MAXX ( CURRENTGROUP (), [P] ) )", "column:Sales/P", "Q", "GROUPBY ( FILTER ( Sales, Sales[Amount] > 0 ), Sales[Label], \"x\", MAXX ( CURRENTGROUP (), [Q] ) )"),
            ("Y2h over addcolumns stays unknown", "m", false, "GROUPBY ( ADDCOLUMNS ( Sales, \"z\", 1 ), Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [P] ) )", "column:Other/P", "New P", "GROUPBY ( ADDCOLUMNS ( Sales, \"z\", 1 ), Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [P] ) )"),
            ("Y2i nested groupby, [P] is the inner extension column", "m", false, "GROUPBY ( GROUPBY ( Sales, Sales[Label], \"P\", SUMX ( CURRENTGROUP (), [Amount] ) ), Sales[Label], \"m\", MAXX ( CURRENTGROUP (), [P] ) )", "column:Sales/P", "New P", "GROUPBY ( GROUPBY ( Sales, Sales[Label], \"P\", SUMX ( CURRENTGROUP (), [Amount] ) ), Sales[Label], \"m\", MAXX ( CURRENTGROUP (), [P] ) )"),
            ("Y2j nested groupby, lean model", "m", true, "GROUPBY ( GROUPBY ( Sales, Sales[Label], \"P\", SUMX ( CURRENTGROUP (), [Amount] ) ), Sales[Label], \"m\", MAXX ( CURRENTGROUP (), [P] ) )", "column:Sales/P", "Q", "GROUPBY ( GROUPBY ( Sales, Sales[Label], \"P\", SUMX ( CURRENTGROUP (), [Amount] ) ), Sales[Label], \"m\", MAXX ( CURRENTGROUP (), [P] ) )"),
            ("Y2k over a var", "m", false, "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN GROUPBY ( t, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [P] ) )", "column:Sales/P", "Q", "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN GROUPBY ( t, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [Q] ) )"),
            ("Y2l groupby of Other inside a Sales scan", "m", false, "SUMX ( Sales, COUNTROWS ( GROUPBY ( Other, Other[P], \"x\", SUMX ( CURRENTGROUP (), [P] ) ) ) )", "column:Sales/P", "New P", "SUMX ( Sales, COUNTROWS ( GROUPBY ( Other, Other[P], \"x\", SUMX ( CURRENTGROUP (), [P] ) ) ) )"),
            ("Y2m groupby of Other inside a Sales scan, Other rename", "m", false, "SUMX ( Sales, COUNTROWS ( GROUPBY ( Other, Other[P], \"x\", SUMX ( CURRENTGROUP (), [P] ) ) ) )", "column:Other/P", "R", "SUMX ( Sales, COUNTROWS ( GROUPBY ( Other, 'Other'[R], \"x\", SUMX ( CURRENTGROUP (), [R] ) ) ) )"),
            ("Y2n no spaces", "m", false, "GROUPBY(Sales,Sales[Label],\"x\",SUMX(CURRENTGROUP(),[P]))", "column:Sales/P", "Q", "GROUPBY(Sales,Sales[Label],\"x\",SUMX(CURRENTGROUP(),[Q]))"),
            ("Y2o calc column on Other, groupby of Sales", "o", false, "COUNTROWS ( GROUPBY ( Sales, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [P] ) ) )", "column:Other/P", "R", "COUNTROWS ( GROUPBY ( Sales, Sales[Label], \"x\", SUMX ( CURRENTGROUP (), [P] ) ) )"),
            // ---- Y3 VAR tables: a scan of the variable reads the table its expression stands for
            ("Y3a filter", "m", false, "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [P] )", "column:Sales/P", "Q", "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [Q] )"),
            ("Y3b filter, other rename onto measure", "m", false, "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [P] )", "column:Other/P", "New P", "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [P] )"),
            ("Y3c all", "m", false, "VAR t = ALL ( Sales ) RETURN SUMX ( t, [P] )", "column:Sales/P", "Q", "VAR t = ALL ( Sales ) RETURN SUMX ( t, [Q] )"),
            ("Y3d plain table", "m", false, "VAR t = Sales RETURN SUMX ( t, [P] )", "column:Sales/P", "Q", "VAR t = Sales RETURN SUMX ( t, [Q] )"),
            ("Y3e chained", "m", false, "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) VAR u = FILTER ( t, [Amount] > 1 ) RETURN SUMX ( u, [P] )", "column:Sales/P", "Q", "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) VAR u = FILTER ( t, [Amount] > 1 ) RETURN SUMX ( u, [Q] )"),
            ("Y3f chained, rename the filter column", "m", false, "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) VAR u = FILTER ( t, [Amount] > 1 ) RETURN SUMX ( u, [P] )", "column:Sales/Amount", "A2", "VAR t = FILTER ( Sales, 'Sales'[A2] > 0 ) VAR u = FILTER ( t, [A2] > 1 ) RETURN SUMX ( u, [P] )"),
            ("Y3g two scans", "m", false, "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [P] ) + MAXX ( t, [P] )", "column:Sales/P", "Q", "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [Q] ) + MAXX ( t, [Q] )"),
            ("Y3h two scans, other rename", "m", false, "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [P] ) + MAXX ( t, [P] )", "column:Other/P", "New P", "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [P] ) + MAXX ( t, [P] )"),
            ("Y3i addcolumns stays unknown (Sales)", "m", false, "VAR t = ADDCOLUMNS ( Sales, \"z\", 1 ) RETURN SUMX ( t, [P] )", "column:Sales/P", "New P", "VAR t = ADDCOLUMNS ( Sales, \"z\", 1 ) RETURN SUMX ( t, [P] )"),
            ("Y3j addcolumns stays unknown (Other)", "m", false, "VAR t = ADDCOLUMNS ( Sales, \"z\", 1 ) RETURN SUMX ( t, [P] )", "column:Other/P", "New P", "VAR t = ADDCOLUMNS ( Sales, \"z\", 1 ) RETURN SUMX ( t, [P] )"),
            ("Y3k filter over an addcolumns var stays unknown", "m", false, "VAR t = ADDCOLUMNS ( Sales, \"z\", 1 ) VAR u = FILTER ( t, [z] > 0 ) RETURN SUMX ( u, [P] )", "column:Other/P", "New P", "VAR t = ADDCOLUMNS ( Sales, \"z\", 1 ) VAR u = FILTER ( t, [z] > 0 ) RETURN SUMX ( u, [P] )"),
            ("Y3l var over Other", "m", false, "VAR t = FILTER ( Other, Other[P] > 0 ) RETURN SUMX ( t, [P] )", "column:Other/P", "R", "VAR t = FILTER ( Other, 'Other'[R] > 0 ) RETURN SUMX ( t, [R] )"),
            ("Y3m var over Other, Sales rename", "m", false, "VAR t = FILTER ( Other, Other[P] > 0 ) RETURN SUMX ( t, [P] )", "column:Sales/P", "New P", "VAR t = FILTER ( Other, Other[P] > 0 ) RETURN SUMX ( t, [P] )"),
            ("Y3n topn var", "m", false, "VAR t = TOPN ( 5, Sales, Sales[Amount] ) RETURN SUMX ( t, [P] )", "column:Sales/P", "Q", "VAR t = TOPN ( 5, Sales, Sales[Amount] ) RETURN SUMX ( t, [Q] )"),
            ("Y3o sibling vars of one name, Sales", "m", false, "SUMX ( Other, VAR t = Other RETURN SUMX ( t, [P] ) ) + ( VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [P] ) )", "column:Sales/P", "Q", "SUMX ( Other, VAR t = Other RETURN SUMX ( t, [P] ) ) + ( VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [Q] ) )"),
            ("Y3p sibling vars of one name, Other", "m", false, "( VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [P] ) ) + SUMX ( Other, VAR t = Other RETURN SUMX ( t, [P] ) )", "column:Other/P", "R", "( VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [P] ) ) + SUMX ( Other, VAR t = Other RETURN SUMX ( t, [R] ) )"),
            ("Y3q calc column on Sales, var over Other", "s", false, "VAR t = FILTER ( Other, Other[P] > 0 ) RETURN SUMX ( t, [P] ) + [P]", "column:Sales/P", "Q", "VAR t = FILTER ( Other, Other[P] > 0 ) RETURN SUMX ( t, [P] ) + [Q]"),
            ("Y3r calc column on Sales, var over Other, Other rename", "s", false, "VAR t = FILTER ( Other, Other[P] > 0 ) RETURN SUMX ( t, [P] ) + [P]", "column:Other/P", "R", "VAR t = FILTER ( Other, 'Other'[R] > 0 ) RETURN SUMX ( t, [R] ) + [P]"),
            ("Y3s var case differs", "m", false, "VAR T = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [P] )", "column:Sales/P", "Q", "VAR T = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( t, [Q] )"),
            // ---- an inner VAR may reuse an outer VAR's name and hides it only inside its own block (SQLBI, "When are variables evaluated in DAX?": "the outer Result is not
            // visible in the inner block because it is being hidden by a closer variable with the same name"). After the inner block, t is Sales again.
            ("P1 inner var of an outer name (shadowing), Sales", "m", false, "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( Other, VAR t = Other RETURN COUNTROWS ( t ) ) + SUMX ( t, [P] )", "column:Sales/P", "Q", "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( Other, VAR t = Other RETURN COUNTROWS ( t ) ) + SUMX ( t, [Q] )"),
            ("P2 inner var of an outer name (shadowing), Other onto measure", "m", false, "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( Other, VAR t = Other RETURN COUNTROWS ( t ) ) + SUMX ( t, [P] )", "column:Other/P", "New P", "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) RETURN SUMX ( Other, VAR t = Other RETURN COUNTROWS ( t ) ) + SUMX ( t, [P] )"),
            ("P2b var inside a var expression of an outer name, Other onto measure", "m", false, "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) VAR n = VAR t = Other RETURN COUNTROWS ( t ) RETURN SUMX ( t, [P] ) + n", "column:Other/P", "New P", "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) VAR n = VAR t = Other RETURN COUNTROWS ( t ) RETURN SUMX ( t, [P] ) + n"),
            ("P2c the SQLBI shape (an inner VAR hides the outer one), Other onto measure", "m", false, "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) VAR Result = SUMX ( Other, VAR t = Other RETURN COUNTROWS ( t ) ) RETURN Result + SUMX ( t, [P] )", "column:Other/P", "New P", "VAR t = FILTER ( Sales, Sales[Amount] > 0 ) VAR Result = SUMX ( Other, VAR t = Other RETURN COUNTROWS ( t ) ) RETURN Result + SUMX ( t, [P] )"),
            ("Y3t var with a line comment", "m", false, "VAR t = Sales -- note\nRETURN SUMX ( t, [P] )", "column:Sales/P", "Q", "VAR t = Sales -- note\nRETURN SUMX ( t, [Q] )"),
        }.Select(c => new object[] { c.Id, c.Host, c.Lean, c.Before, c.Rename, c.To, c.After });

        // Lean suite (8 Oct): the default run keeps these rows; the whole matrix is the sweep.
        public static IEnumerable<object[]> CasesSample() => Cases().Where(r => new[] { "Y1a", "Y2a", "Y3a", "P1", "P2", "P2b", "P2c" }.Contains(((string)r[0]).Split(' ')[0]));

        [Theory]
        [MemberData(nameof(CasesSample))]
        public Task A_rename_follows_the_binding_the_dax_pages_give_sample(string id, string host, bool lean, string before, string rename, string to, string after) => A_rename_follows_the_binding_the_dax_pages_give(id, host, lean, before, rename, to, after);

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Cases))]
        public async Task A_rename_follows_the_binding_the_dax_pages_give(string id, string host, bool lean, string before, string rename, string to, string after)
        {
            var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("VerifyScan2", 1601);
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
