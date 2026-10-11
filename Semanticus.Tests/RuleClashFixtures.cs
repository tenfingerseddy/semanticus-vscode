using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;

namespace Semanticus.Tests
{
    /// <summary>
    /// The models the rule-clash matrix accepts findings on, beside the AdventureWorks sample the repository already ships. Placeholder
    /// names only (Sales, Customer, Product, Budget, Date). "kitchen" is a small star with a fault of nearly every kind on purpose:
    /// each object below exists to make one or two bundled rules fire, and the matrix says which rules still do not.
    /// "instructions" is the same star with a synonym store and AI instructions that are too long and leave a field code undefined.
    /// </summary>
    internal static class RuleClashFixtures
    {
        internal static async Task BuildAsync(DecisionFixture f, string name)
        {
            var e = f.Engine;
            await e.CreateModelAsync("Matrix" + name, 1604);
            await f.Sessions.Require().MutateAsync("human", "matrix fixture " + name, m => Kitchen(m));
            if (name == "kitchen") return;
            await e.CreateLinguisticSchemaAsync(null, "agent");
            if (name == "schema") return;
            await e.SetAiInstructionsAsync("MARKER-TEXT use fiscal years and report currency.", null, "agent");
            await f.Sessions.Require().MutateAsync("human", "matrix fixture: instructions over the cap, with a code left undefined", m =>
            {
                var cult = m.Cultures.First(c => c.Content != null && c.Content.Contains("MARKER-TEXT"));
                cult.Content = cult.Content.Replace("MARKER-TEXT", new string('a', 10600));
            });
            await e.SetSynonymsAsync("column:Sales/Amount", new[] { "revenue", "sales value" }, null, "agent");
            await e.SetSynonymsAsync("column:Customer/Customer Name", new[] { "revenue", "client" }, null, "agent");
            await e.SetSynonymsAsync("column:Sales/Temp Syn", new[] { "scratch" }, null, "agent");
            await f.Sessions.Require().MutateAsync("human", "matrix fixture: a synonym bound to a column that is gone", m => m.Tables["Sales"].Columns["Temp Syn"].Delete());
        }

        private static DataColumn Col(Table t, string name, DataType type) => t.AddDataColumn(name, name, null, type);

        private static void Kitchen(Model m)
        {
            var sales = m.AddTable("Sales");
            Col(sales, "Amount", DataType.Double);
            Col(sales, "Quantity", DataType.Int64);
            Col(sales, "CustomerKey", DataType.Int64);
            Col(sales, "ProductKey", DataType.Int64);
            Col(sales, "Order Date", DataType.DateTime);
            Col(sales, "Ship Date", DataType.DateTime);
            Col(sales, "Country", DataType.String);
            Col(sales, "Website", DataType.String);
            Col(sales, "Row Hash", DataType.String);
            Col(sales, "Order Year", DataType.Int64);
            Col(sales, "cust_id", DataType.String);
            Col(sales, "Discount Pct", DataType.Double);
            Col(sales, "Order Month", DataType.String);
            Col(sales, "Order Month Number", DataType.Int64);
            Col(sales, "Column1", DataType.String);
            Col(sales, "Edge Space ", DataType.String);
            Col(sales, "Notes", DataType.String);
            Col(sales, "Returned Date Text", DataType.String);
            Col(sales, "Web URL", DataType.String);
            Col(sales, "Code", DataType.String);
            Col(sales, "Temp Syn", DataType.String);
            Col(sales, "Tab" + (char)9 + "Name", DataType.String);
            Col(sales, "Bell" + (char)0x90 + "Name", DataType.String);
            sales.AddDataColumn("No Source", null, null, DataType.String);
            Col(sales, "Month Start", DataType.DateTime);
            sales.Columns["Order Month Number"].IsAvailableInMDX = false;
            sales.Columns["Order Month"].SortByColumn = sales.Columns["Order Month Number"];
            sales.Columns["Returned Date Text"].SetAnnotation("DateTimeWithHourMinSec", "1");
            sales.AddCalculatedColumn("Customer Name Copy", "RELATED ( Customer[Customer Name] )");
            sales.Columns["Amount"].Description = "Amount";
            sales.Columns["Web URL"].Description = "A link" + (char)0x90 + " to the order page";                                  // echoes its own name
            sales.Columns["Notes"].Description = "TBD";                                      // a placeholder
            sales.Columns["Quantity"].Description = new string('q', 260);                    // too long
            sales.Description = "Sales";                                                     // echo

            var cust = m.AddTable("Customer");
            Col(cust, "CustomerKey", DataType.Int64);
            Col(cust, "IsActive", DataType.Int64);
            Col(cust, "Customer Name", DataType.String);
            Col(cust, "City", DataType.String);
            Col(cust, "Name", DataType.String);
            Col(cust, "RegionKey", DataType.Int64);
            cust.Columns["CustomerKey"].IsKey = true;

            var prod = m.AddTable("Product");
            Col(prod, "ProductKey", DataType.Int64);
            Col(prod, "ProductName", DataType.String);
            Col(prod, "Category", DataType.String);
            Col(prod, "Name", DataType.String);

            var budget = m.AddTable("Budget");
            Col(budget, "ProductKey", DataType.Int64);
            Col(budget, "Budget Amount", DataType.Double);

            var date = m.AddTable("Date");
            Col(date, "Date", DataType.DateTime);
            Col(date, "Year", DataType.Int64);
            Col(date, "Month Name", DataType.String);
            Col(date, "Month Number", DataType.Int64);
            Col(date, "Quarter", DataType.String);

            var region = m.AddTable("dim_Region");
            Col(region, "RegionKey", DataType.Int64);
            Col(region, "Region", DataType.String);

            var geo = m.AddTable("Geography");
            Col(geo, "Country", DataType.String);
            Col(geo, "State", DataType.String);
            Col(geo, "City", DataType.String);

            var lonely = m.AddTable("Lonely");
            Col(lonely, "Label", DataType.String);

            var hidden = m.AddTable("Hidden All");
            foreach (var n in new[] { "H1", "H2" }) Col(hidden, n, DataType.String).IsHidden = true;

            m.AddCalculatedTable("Calc Tbl", "DISTINCT ( Sales[Country] )");
            m.AddCalculatedTable("LocalDateTable_1234", "CALENDAR ( DATE ( 2020, 1, 1 ), DATE ( 2020, 12, 31 ) )");

            var orders = m.AddTable("Fact Orders");
            Col(orders, "Order Id Label", DataType.String);
            var pivoted = m.AddTable("Pivoted");
            foreach (var mo in new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" }) Col(pivoted, mo + " Amount", DataType.Double);
            var load = pivoted.AddMPartition("Pivoted Load", "let Source = Table.AddColumn(#table({\"A\"}, {{1}}), \"B\", each 1) in Source");
            foreach (var extra in pivoted.Partitions.Where(x => x != (Partition)load).ToList()) extra.Delete();
            sales.SetAnnotation("Vertipaq_RowCount", "30000000");
            sales.Columns["CustomerKey"].SetAnnotation("Vertipaq_Cardinality", "200000");
            sales.Columns["Notes"].SetAnnotation("LongLengthRowCount", "900000");

            // relationships
            Rel(m, sales, "CustomerKey", cust, "CustomerKey", both: true);
            Rel(m, sales, "ProductKey", prod, "ProductKey");
            Rel(m, sales, "Order Date", date, "Date");
            Rel(m, sales, "Ship Date", date, "Date", active: false);
            Rel(m, cust, "RegionKey", region, "RegionKey");
            var m2m = Rel(m, budget, "ProductKey", prod, "ProductKey", both: true);
            Rel(m, sales, "cust_id", cust, "CustomerKey").SetAnnotation("Vertipaq_RIViolationInvalidRows", "12");
            m2m.FromCardinality = RelationshipEndCardinality.Many;
            m2m.ToCardinality = RelationshipEndCardinality.Many;

            // measures
            sales.AddMeasure("Total Sales", "SUM ( Sales[Amount] ) + SUM ( Sales[Quantity] )").FormatString = "#,0.00";
            sales.AddMeasure("Order Count", "COUNTROWS ( Sales )");
            sales.AddMeasure("Sales YTD", "TOTALYTD ( [Total Sales], 'Date'[Date] )").FormatString = "#,0.00";
            sales.AddMeasure("total margin ", "SUM ( Sales[Amount] ) - SUM ( Sales[Quantity] )");
            sales.AddMeasure("m_amt", "SUM ( Sales[Amount] )").FormatString = "#,0.00";
            sales.AddMeasure("Measure 3", "SUM ( Sales[Quantity] )").FormatString = "#,0";
            sales.AddMeasure("Margin Ratio", "IFERROR ( [Total Sales] / [Order Count], 0 )").FormatString = "0.00";
            sales.AddMeasure("Ratio Calc", "[Total Sales] / [Order Count]").FormatString = "0.00";
            sales.AddMeasure("Growth", "1 - ( [Total Sales] / [Sales YTD] )").FormatString = "0.00";
            sales.AddMeasure("_helper total", "SUM ( Sales[Amount] )").FormatString = "#,0.00";
            sales.AddMeasure("Pct Share", "DIVIDE ( [Total Sales], [Sales YTD] )").FormatString = "0.00%";
            sales.AddMeasure("Sales Alias", "[Total Sales]").FormatString = "#,0.00";
            sales.AddMeasure("Sales Filtered", "CALCULATE ( [Total Sales], FILTER ( Sales, [Order Count] > 1 ) )").FormatString = "#,0.00";
            sales.AddMeasure("Sales Bare Table", "CALCULATE ( [Total Sales], Sales )").FormatString = "#,0.00";
            sales.AddMeasure("Sales Predicate", "CALCULATE ( [Total Sales], [Order Count] > 0 )").FormatString = "#,0.00";
            sales.AddMeasure("Sales All", "CALCULATE ( [Total Sales], ALL ( Product ) )").FormatString = "#,0.00";
            sales.AddMeasure("Sales Earlier", "SUMX ( Sales, CALCULATE ( SUM ( Sales[Amount] ), FILTER ( Sales, Sales[Amount] > EARLIER ( Sales[Amount] ) ) ) )").FormatString = "#,0.00";
            sales.AddMeasure("Sales Error", "IF ( [Order Count] < 0, ERROR ( \"negative\" ), [Total Sales] )").FormatString = "#,0.00";
            sales.AddMeasure("Search Position", "IFERROR ( SEARCH ( \"a\", \"abc\" ), 0 )").FormatString = "#,0";
            sales.AddMeasure("Not Blank", "IF ( [Total Sales] <> BLANK (), 1, 0 )").FormatString = "#,0";
            sales.AddMeasure("Summarized", "COUNTROWS ( SUMMARIZE ( Sales, Sales[Country], \"x\", SUM ( Sales[Amount] ) ) )").FormatString = "#,0";
            sales.AddMeasure("Var Alias", "VAR t = SUM ( Sales[Amount] ) RETURN CALCULATE ( t, ALL ( Product ) )").FormatString = "#,0.00";
            sales.AddMeasure("Var Unused", "VAR unused = 1 RETURN [Total Sales]").FormatString = "#,0.00";
            sales.AddMeasure("Qualified Ref", "Sales[Total Sales] * 2").FormatString = "#,0.00";
            sales.AddMeasure("Dangling Ref", "SUM ( 'Ghost Table'[Amount] )").FormatString = "#,0.00";   // names a table the model does not have: DAX-REFERENCE and FORMULA_REFERENCE_NOT_IN_MODEL fire (plan statement check 4, P2)
            sales.AddMeasure("Empty Expr", " ");
            sales.AddMeasure("Not Used Hidden", "1").IsHidden = true;
            sales.AddMeasure("Ship Amount", "CALCULATE ( [Total Sales], USERELATIONSHIP ( Sales[Ship Date], 'Date'[Date] ) )").FormatString = "#,0.00";
            sales.AddMeasure("QRX Revenue", "1").FormatString = "#,0";
            sales.AddMeasure("Long Described", "1").FormatString = "#,0";
            sales.Measures["Long Described"].Description = new string('d', 260);
            sales.AddMeasure("Bell" + (char)0x90 + "Measure", "1").FormatString = "#,0";
            sales.AddMeasure("Uses Slash Growth", "1 - SUM ( Sales[Amount] ) / SUM ( Sales[Quantity] )").FormatString = "0.00";
            sales.AddMeasure("Sales Copy", "SUM ( Sales[Amount] ) + SUM ( Sales[Quantity] )").FormatString = "#,0.00";
            sales.AddMeasure("Eval Log", "EVALUATEANDLOG ( [Total Sales] )").FormatString = "#,0.00";
            sales.AddMeasure("Unfinished", "IF ( 1 = 1, \"TODO: finish\", 0 )").FormatString = "#,0.00";
            sales.AddMeasure("Very Long", "1" + string.Concat(Enumerable.Repeat(" + 1", 2600))).FormatString = "#,0";
            sales.AddMeasure("Sales Overlap", "COUNTROWS ( INTERSECT ( VALUES ( Sales[ProductKey] ), VALUES ( Product[ProductKey] ) ) )").FormatString = "#,0";
            sales.AddMeasure("Distinct Products", "COUNTROWS ( DISTINCT ( Sales[ProductKey] ) )").FormatString = "#,0";
            sales.AddMeasure("Guarded Ratio", "IF ( [Order Count] = 0, 0, [Total Sales] / [Order Count] )").FormatString = "0.00";
            sales.AddMeasure("Selected Category", "IF ( HASONEVALUE ( Product[Category] ), VALUES ( Product[Category] ) )");
            sales.Measures["Total Sales"].Description = "Total Sales";
            sales.Measures["Order Count"].Description = "TBD";

            // a hierarchy with one level, and a geography with drill columns and no hierarchy
            date.AddHierarchy("Calendar", null, date.Columns["Year"]);
            geo.AddHierarchy("geoH_1", null, geo.Columns["Country"], geo.Columns["State"]);
            m.Tables["Product"].AddHierarchy("Category Path", null, m.Tables["Product"].Columns["Category"]);

            // a calculation group whose description leaves an item out, and an empty one
            var cg = m.AddCalculationGroup("Time Calc");
            cg.AddCalculationItem("YTD", "TOTALYTD ( SELECTEDMEASURE (), 'Date'[Date] )");
            cg.AddCalculationItem("Prior Year", "CALCULATE ( SELECTEDMEASURE (), SAMEPERIODLASTYEAR ( 'Date'[Date] ) )");
            m.AddCalculationGroup("Empty Group");
            cg.Description = "Time views. YTD only.";

            // roles and perspectives
            m.AddRole("Empty Role");
            var rls = m.AddRole("Region Reader");
            rls.RowLevelSecurity[cust] = "'Customer'[City] = \"Oslo\"";
            rls.RowLevelSecurity[sales] = "UPPER ( 'Sales'[Country] ) = \"NORWAY\"";
            rls.RowLevelSecurity[prod] = "LEFT ( 'Product'[ProductName], 1 ) = \"A\"";
            rls.RowLevelSecurity[date] = "'Date'[Year] > 2000";
            m.AddPerspective("Nothing In It");
        }

        private static SingleColumnRelationship Rel(Model m, Table from, string fromCol, Table to, string toCol, bool both = false, bool active = true)
        {
            var r = m.AddRelationship();
            r.FromColumn = from.Columns[fromCol];
            r.ToColumn = to.Columns[toCol];
            if (both) r.CrossFilteringBehavior = CrossFilteringBehavior.BothDirections;
            r.IsActive = active;
            return r;
        }
    }
}
