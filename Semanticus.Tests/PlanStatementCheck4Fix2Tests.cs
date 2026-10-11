using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Semanticus.Engine;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// Plan statement check 4, fix round 2, R5: a date column that only the DT = DATE reading found, on a table that other tables point at. The first fix dropped it, to keep a
    /// dimension (a staff table with a hire date) from being a second path to the date table. But a header fact that a line fact points at has no other way to the date table,
    /// and it lost its date link, so the model had no date table at all. It keeps the link when no table that points at it already reaches the date table.
    /// </summary>
    public sealed class PlanStatementCheck4Fix2Tests
    {
        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };

        private static FabricSchema HeaderAndLines(bool lineHasItsOwnDate, bool headerHasTwoDates = false)
        {
            var header = new List<FabricColumn> { C("ORDER_ID", "Int64"), C("CUST_ID", "Int64"), C("ORD_DT_KEY", "Int64"), C("ORD_TOT", "Decimal") };
            if (headerHasTwoDates) header.Add(C("SHIP_DT_KEY", "Int64"));
            var line = new List<FabricColumn> { C("LINE_ID", "Int64"), C("ORDER_ID", "Int64"), C("QTY", "Int64"), C("LINE_AMT", "Decimal") };
            if (lineHasItsOwnDate) line.Add(C("LINE_DT_KEY", "Int64"));
            return new FabricSchema
            {
                Server = "x", Database = "Sales", ForeignKeys = new FabricForeignKey[0],
                Tables = new[]
                {
                    new FabricTable { Schema = "dbo", Name = "dim_date", KeyColumns = new string[0], Columns = new[] { C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime"), C("FY_NO", "Int64") } },
                    new FabricTable { Schema = "dbo", Name = "dim_cust", KeyColumns = new string[0], Columns = new[] { C("CUST_ID", "Int64"), C("CUST_NM", "String") } },
                    new FabricTable { Schema = "dbo", Name = "fact_order", KeyColumns = new string[0], Columns = header.ToArray() },
                    new FabricTable { Schema = "dbo", Name = "fact_order_line", KeyColumns = new string[0], Columns = line.ToArray() },
                },
            };
        }

        private static LinkGuessFacts Sample(bool lineHasItsOwnDate, bool headerHasTwoDates = false)
        {
            var header = new Dictionary<string, bool> { ["ORDER_ID"] = true, ["CUST_ID"] = false, ["ORD_DT_KEY"] = false, ["ORD_TOT"] = false };
            if (headerHasTwoDates) header["SHIP_DT_KEY"] = false;
            var line = new Dictionary<string, bool> { ["LINE_ID"] = true, ["ORDER_ID"] = false, ["QTY"] = false, ["LINE_AMT"] = false };
            if (lineHasItsOwnDate) line["LINE_DT_KEY"] = false;
            return LinkGuessFacts.NamesOnly()
                .Measured("dbo.dim_date", new Dictionary<string, bool> { ["DATE_KEY"] = true, ["CAL_DT"] = true, ["FY_NO"] = false })
                .Measured("dbo.dim_cust", new Dictionary<string, bool> { ["CUST_ID"] = true, ["CUST_NM"] = true })
                .Measured("dbo.fact_order", header)
                .Measured("dbo.fact_order_line", line);
        }

        private static string Key(ManifestLink l) => l.FromTable + "." + l.FromColumns[0] + ">" + l.ToTable + "." + l.ToColumns[0];

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_header_fact_that_a_line_fact_points_at_keeps_its_only_date_link(bool withSample)
        {
            var m = StructureManifest.Derive(HeaderAndLines(false), null, null, "seed", "x", "DW", "import", withSample ? Sample(false) : LinkGuessFacts.NamesOnly());
            var keys = m.Links.Select(Key).ToArray();
            // The line fact points at the header (that is what makes the header a table others point at) and the header reaches the date table by its only date.
            Assert.Contains("fact_order_line.ORDER_ID>fact_order.ORDER_ID", keys);
            var date = Assert.Single(m.Links, l => l.ToTable == "dim_date");
            Assert.Equal("fact_order.ORD_DT_KEY>dim_date.DATE_KEY", Key(date));
            Assert.True(date.Active);
            var spec = m.ToSpec();
            Assert.True(spec.DateTable.MarkAsDate, spec.DateTable.Note);
            Assert.Equal("dim_date", spec.DateTable.Name);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_header_date_link_stays_out_when_the_line_fact_reaches_the_date_table_itself(bool withSample)
        {
            // Two paths to the date table (the line's own and the one through the header) is the ambiguity the first fix keeps out: the line's own date is the link.
            var m = StructureManifest.Derive(HeaderAndLines(true), null, null, "seed", "x", "DW", "import", withSample ? Sample(true) : LinkGuessFacts.NamesOnly());
            var date = Assert.Single(m.Links, l => l.ToTable == "dim_date");
            Assert.Equal("fact_order_line.LINE_DT_KEY>dim_date.DATE_KEY", Key(date));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_pointed_at_table_with_two_date_roles_is_not_given_either_of_them(bool withSample)
        {
            // Only a table's ONLY date is kept (the header here has two): which of the two is "the" date of a table that others point at is not a thing the names can say, so neither is guessed.
            var m = StructureManifest.Derive(HeaderAndLines(false, true), null, null, "seed", "x", "DW", "import", withSample ? Sample(false, true) : LinkGuessFacts.NamesOnly());
            Assert.DoesNotContain(m.Links, l => l.ToTable == "dim_date" && l.FromTable == "fact_order");
        }

        [Fact]
        public void The_sign_in_words_name_the_choice_the_way_the_Connections_screen_does()
        {
            // R6: the words said "the Microsoft account sign-in", and no screen calls it that. The Connections hub's own label is what the words must say.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            // The SQL source form picks its sign-in with AccountPicker (connectionshub.tsx), whose choices are SIGN_IN_MODES in copy.ts. "Browser sign-in" belongs to another form (Add a model by its address).
            var src = Path.Combine(dir!.FullName, "Semanticus.VSCode", "webview", "src");
            Assert.Contains("<AccountPicker mode={form.authMode}", File.ReadAllText(Path.Combine(src, "connectionshub.tsx")));
            var label = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(src, "copy.ts")), "value: 'interactive', label: '([^']+)'").Groups[1].Value;
            Assert.Equal("Sign in in a browser", label);
            Assert.Contains("choose " + label + " for this source in Connections", StartSourceCopy.NoSignIn());
            Assert.Contains("choose " + label + " for this source in Connections", StartSourceCopy.SignInRefused());
        }
    }
}
