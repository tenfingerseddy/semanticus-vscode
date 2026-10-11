using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Engine;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>
    /// M11 uat7-links (Kane, 3 Oct: "Guess links by name"). A Fabric warehouse usually declares no foreign keys, so the Free start found no fact table and built no
    /// measures. The manifest now matches key names across tables when the source declares no key for a pair, and shows each guessed link for the person to keep or drop.
    ///
    /// THE RULE, in plain words. Only for a pair of tables with no declared key. A column in table A points at table B when its name matches a key column of B (same name,
    /// "CustomerId" to Customers[Id], or "OrderDateKey" to Date[DateKey]), the types match, and B's column is a key: its declared primary key, a column the sample shows
    /// unique on every row, or (with no rows read) a column named like B's own key. B repeating in the sample rules the link out. A's own key is never the many side.
    /// A column that two tables answer to keeps one link on and the rest off, with the reason; a second link between the same two tables is off like a role-playing date.
    ///
    /// The fixture is the column lists of the UAT source (Northstar Source: uat-sol/evidence/J7-S1/saved-tmdl), which declares no key at all. Written before the rule.
    /// </summary>
    public sealed class StartLinkGuessTests
    {
        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };
        private static FabricTable T(string name, string[] keys, params FabricColumn[] cols) => new FabricTable { Schema = "dbo", Name = name, KeyColumns = keys, Columns = cols };
        private static FabricForeignKey Fk(string from, string fromCol, string to, string toCol) =>
            new FabricForeignKey { FromSchema = "dbo", FromTable = from, FromColumn = fromCol, ToSchema = "dbo", ToTable = to, ToColumn = toCol };

        /// <summary>The UAT warehouse: five tables, no primary key and no foreign key declared.</summary>
        internal static FabricSchema Northstar(params FabricForeignKey[] declared) => new FabricSchema
        {
            Server = "ns.datawarehouse.fabric.microsoft.com", Database = "Northstar Source",
            Tables = new[]
            {
                T("Customer", new string[0], C("CustomerKey", "Int64"), C("CustomerCode", "String"), C("CustomerName", "String"), C("Segment", "String"), C("City", "String"), C("Region", "String"), C("JoinDate", "DateTime")),
                T("Date", new string[0], C("DateKey", "Int64"), C("Date", "DateTime"), C("Year", "Int64"), C("Quarter", "String"), C("MonthNumber", "Int64"), C("MonthName", "String"), C("YearMonth", "String"),
                    C("MonthStart", "DateTime"), C("DayOfWeek", "String"), C("DayOfWeekNumber", "Int64"), C("IsWeekend", "Int64")),
                T("Product", new string[0], C("ProductKey", "Int64"), C("SKU", "String"), C("ProductName", "String"), C("Subcategory", "String"), C("Category", "String"), C("CategorySortOrder", "Int64"),
                    C("Brand", "String"), C("ListPrice", "Decimal"), C("UnitCost", "Decimal")),
                T("Sales", new string[0], C("SalesLineKey", "Int64"), C("OrderNumber", "String"), C("OrderLine", "Int64"), C("OrderDateKey", "Int64"), C("ProductKey", "Int64"), C("CustomerKey", "Int64"),
                    C("StoreKey", "Int64"), C("Quantity", "Int64"), C("UnitPrice", "Decimal"), C("DiscountAmount", "Decimal"), C("NetSalesAmount", "Decimal"), C("CostAmount", "Decimal"),
                    C("IsReturn", "Int64"), C("ReturnOfSalesLineKey", "Int64"), C("AuditNote", "String")),
                T("Store", new string[0], C("StoreKey", "Int64"), C("StoreCode", "String"), C("StoreName", "String"), C("City", "String"), C("Region", "String"), C("Channel", "String"),
                    C("OpenDate", "DateTime"), C("CloseDate", "DateTime")),
            },
            ForeignKeys = declared,
        };

        /// <summary>What a sample of the Northstar rows shows: each table's own key is unique, and nothing else the rule reads is.</summary>
        internal static LinkGuessFacts NorthstarSample(bool customerKeyUnique = true) => LinkGuessFacts.NamesOnly()
            .Measured("dbo.Customer", new Dictionary<string, bool> { ["CustomerKey"] = customerKeyUnique, ["CustomerCode"] = true, ["City"] = false })
            .Measured("dbo.Date", new Dictionary<string, bool> { ["DateKey"] = true, ["Date"] = true, ["Year"] = false })
            .Measured("dbo.Product", new Dictionary<string, bool> { ["ProductKey"] = true, ["SKU"] = true })
            .Measured("dbo.Sales", new Dictionary<string, bool> { ["SalesLineKey"] = true, ["ProductKey"] = false, ["CustomerKey"] = false, ["StoreKey"] = false, ["OrderDateKey"] = false })
            .Measured("dbo.Store", new Dictionary<string, bool> { ["StoreKey"] = true, ["StoreCode"] = true, ["City"] = false });

        private static StructureManifest Derive(FabricSchema s, LinkGuessFacts facts, string seed = "seed", ManifestOverrides o = null) =>
            StructureManifest.Derive(s, o, null, seed, "x", "DW", "import", facts);

        private static string Key(ManifestLink l) => l.FromTable + "." + l.FromColumns[0] + ">" + l.ToTable + "." + l.ToColumns[0];
        private static string[] Keys(StructureManifest m) => m.Links.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        private static readonly string[] NorthstarLinks =
            { "Sales.CustomerKey>Customer.CustomerKey", "Sales.OrderDateKey>Date.DateKey", "Sales.ProductKey>Product.ProductKey", "Sales.StoreKey>Store.StoreKey" };

        // ================================================================== the Northstar shape

        [Fact]
        public void Without_the_rule_a_source_with_no_declared_keys_builds_no_fact_and_no_measures()
        {
            // The honest baseline (what Sol saw): no guess asked for, so nothing links.
            var m = StructureManifest.Derive(Northstar(), null, null, "seed", "x", "DW", "import");
            Assert.Empty(m.Links);
            Assert.Empty(m.BaseMeasures);
        }

        [Fact]
        public void The_Northstar_shape_gets_its_four_links_a_fact_table_and_measures_with_names_alone()
        {
            var m = Derive(Northstar(), LinkGuessFacts.NamesOnly());
            Assert.Equal(NorthstarLinks, Keys(m));
            Assert.All(m.Links, l => { Assert.True(l.Guessed); Assert.True(l.Active); Assert.False(l.Dropped); Assert.False(l.Unresolved); });
            Assert.Equal("fact", m.Tables.Single(t => t.Name == "Sales").Role);
            Assert.All(new[] { "Customer", "Date", "Product", "Store" }, n => Assert.Equal("dimension", m.Tables.Single(t => t.Name == n).Role));
            // The existing Free rules then find the measures exactly as they do with declared keys: totals of the numbers, none of the keys.
            var names = m.BaseMeasures.Select(b => b.Name).ToList();
            Assert.Contains("Total NetSalesAmount", names);
            Assert.Contains("Total Quantity", names);
            Assert.DoesNotContain(names, n => n.Contains("Key"));
            // The date table is found and the time measures follow.
            Assert.Equal("Date", m.DateTable.Name);
            Assert.NotEmpty(m.TimeMeasures());
            // And the spec the build reads carries the four links.
            Assert.Equal(4, m.ToSpec().Relationships.Length);
        }

        [Fact]
        public void A_column_that_only_looks_like_a_link_is_not_guessed()
        {
            var m = Derive(Northstar(), NorthstarSample());
            // A table's own key, a link of a table to itself, and two dimensions that both have a City or a Region are not links.
            Assert.DoesNotContain(m.Links, l => l.FromColumns[0] == "SalesLineKey" || l.FromColumns[0] == "ReturnOfSalesLineKey");
            Assert.DoesNotContain(m.Links, l => l.FromTable == "Customer" || l.FromTable == "Store" || l.FromTable == "Product" || l.FromTable == "Date");
        }

        [Fact]
        public void A_sample_that_shows_the_key_unique_is_what_the_line_says()
        {
            var m = Derive(Northstar(), NorthstarSample());
            var link = m.Links.Single(l => Key(l) == "Sales.CustomerKey>Customer.CustomerKey");
            Assert.Equal("sample", link.GuessEvidence);
            Assert.Equal("same_name", link.GuessKind);
            Assert.Equal("Linked Sales[CustomerKey] to Customer[CustomerKey]: same name, and CustomerKey is unique in Customer (from the sample)", LinkGuesser.Words(link));
            var date = m.Links.Single(l => Key(l) == "Sales.OrderDateKey>Date.DateKey");
            Assert.Equal("ends_with", date.GuessKind);
            Assert.Equal("Linked Sales[OrderDateKey] to Date[DateKey]: OrderDateKey ends with DateKey, and DateKey is unique in Date (from the sample)", LinkGuesser.Words(date));
        }

        [Fact]
        public void With_no_rows_read_the_line_says_the_rows_were_not_read()
        {
            var m = Derive(Northstar(), LinkGuessFacts.NamesOnly());
            var link = m.Links.Single(l => Key(l) == "Sales.CustomerKey>Customer.CustomerKey");
            Assert.Equal("name", link.GuessEvidence);
            var words = LinkGuesser.Words(link);
            Assert.StartsWith("Linked Sales[CustomerKey] to Customer[CustomerKey]: same name, and CustomerKey is named like the key of Customer", words);
            Assert.DoesNotContain("(from the sample)", words);
            Assert.Contains("rows not read", words);
        }

        // ================================================================== K23: a fact that points at another fact

        /// <summary>The demo's Gold lakehouse (9 Oct): two fact tables and four dimensions, no key declared, and fact_returns holding sales_key.</summary>
        private static FabricSchema Gold(params FabricForeignKey[] declared) => new FabricSchema
        {
            Server = "gold.datawarehouse.fabric.microsoft.com", Database = "Gold",
            Tables = new[]
            {
                T("dim_customer", new string[0], C("customer_key", "Int64"), C("customer_name", "String")),
                T("dim_date", new string[0], C("date_key", "Int64"), C("date", "DateTime"), C("year", "Int64")),
                T("dim_product", new string[0], C("product_key", "Int64"), C("product_name", "String")),
                T("dim_store", new string[0], C("store_key", "Int64"), C("store_name", "String")),
                T("fact_returns", new string[0], C("return_key", "Int64"), C("sales_key", "Int64"), C("date_key", "Int64"), C("product_key", "Int64"), C("customer_key", "Int64"), C("store_key", "Int64"), C("return_amount", "Decimal"), C("return_quantity", "Int64")),
                T("fact_sales", new string[0], C("sales_key", "Int64"), C("date_key", "Int64"), C("product_key", "Int64"), C("customer_key", "Int64"), C("store_key", "Int64"), C("net_sales_amount", "Decimal"), C("quantity", "Int64")),
            },
            ForeignKeys = declared,
        };

        /// <summary>K23. A key from one fact to another gives the first fact two paths to every shared dimension. The link between the facts is the one
        /// switched off: every link from a fact to a dimension stays on, so the totals by year, product and store are right.</summary>
        [Fact]
        public void A_key_from_one_fact_to_another_is_the_link_switched_off_never_a_fact_to_dimension_link()
        {
            var m = Derive(Gold(), LinkGuessFacts.NamesOnly());
            var off = m.Links.Where(l => !l.Active).Select(Key).ToArray();
            Assert.True(off.SequenceEqual(new[] { "fact_returns.sales_key>fact_sales.sales_key" }), string.Join(" | ", m.Tables.Select(t => t.Name + "=" + t.Role)) + " :: " + string.Join(" | ", m.Links.Where(l => !l.Active).Select(l => Key(l) + " " + l.Note)));
            Assert.Equal(8, m.Links.Count(l => l.Active && l.ToTable.StartsWith("dim_", StringComparison.Ordinal)));
            Assert.Equal(4, m.Links.Count(l => l.Active && l.FromTable == "fact_sales"));
        }

        /// <summary>K23, the same with every key declared by the source: the declared link between the facts is the one drafted off.</summary>
        [Fact]
        public void A_declared_key_from_one_fact_to_another_is_the_link_drafted_off()
        {
            var fks = new List<FabricForeignKey> { Fk("fact_returns", "sales_key", "fact_sales", "sales_key") };
            foreach (var fact in new[] { "fact_returns", "fact_sales" })
                foreach (var dim in new[] { "customer", "date", "product", "store" })
                    fks.Add(Fk(fact, dim + "_key", "dim_" + dim, dim + "_key"));
            var m = Derive(Gold(fks.ToArray()), LinkGuessFacts.NamesOnly());
            Assert.True(m.Links.Where(l => !l.Active).Select(Key).SequenceEqual(new[] { "fact_returns.sales_key>fact_sales.sales_key" }), string.Join(" | ", m.Tables.Select(t => t.Name + "=" + t.Role)) + " :: " + string.Join(" | ", m.Links.Where(l => !l.Active).Select(l => Key(l) + " " + l.Note)));
        }

        // ================================================================== a declared key always wins

        [Fact]
        public void A_declared_key_for_the_pair_is_the_only_link_and_is_not_a_guess()
        {
            var s = Northstar(Fk("Sales", "CustomerKey", "Customer", "CustomerKey"));
            var m = Derive(s, NorthstarSample());
            var toCustomer = m.Links.Where(l => l.ToTable == "Customer").ToList();
            Assert.Single(toCustomer);
            Assert.False(toCustomer[0].Guessed);
            // The other three pairs declare nothing, so they are still guessed.
            Assert.Equal(3, m.Links.Count(l => l.Guessed));
        }

        [Fact]
        public void A_declared_key_on_another_column_of_the_pair_still_blocks_the_guess()
        {
            // The source says Sales reaches Customer through ReturnOfSalesLineKey-like columns of its own choosing: whatever it declared for the pair, nobody adds a second.
            var s = Northstar(Fk("Sales", "SalesLineKey", "Customer", "CustomerCode"));
            var m = Derive(s, NorthstarSample());
            Assert.Single(m.Links.Where(l => l.ToTable == "Customer"));
            Assert.False(m.Links.Single(l => l.ToTable == "Customer").Guessed);
        }

        [Fact]
        public void A_declared_key_in_the_other_direction_blocks_the_guess_too()
        {
            var s = Northstar(Fk("Customer", "CustomerKey", "Sales", "CustomerKey"));
            var m = Derive(s, NorthstarSample());
            Assert.DoesNotContain(m.Links, l => l.Guessed && l.FromTable == "Sales" && l.ToTable == "Customer");
        }

        [Fact]
        public void Without_the_rule_asked_for_a_declared_schema_gives_exactly_what_it_gave_before()
        {
            var s = Northstar(Fk("Sales", "CustomerKey", "Customer", "CustomerKey"));
            var plain = StructureManifest.Derive(s, null, null, "seed", "x", "DW", "import");
            Assert.Single(plain.Links);
            Assert.False(plain.Links[0].Guessed);
        }

        // ================================================================== ambiguity is marked, never guessed between silently

        [Fact]
        public void A_column_two_tables_answer_to_keeps_one_link_on_and_marks_the_other_off_with_its_reason()
        {
            var s = Northstar();
            s.Tables = s.Tables.Concat(new[] { T("CustomerAddress", new string[0], C("CustomerKey", "Int64"), C("Street", "String")) }).ToArray();
            var facts = NorthstarSample().Measured("dbo.CustomerAddress", new Dictionary<string, bool> { ["CustomerKey"] = true });
            var m = Derive(s, facts);
            var both = m.Links.Where(l => l.FromTable == "Sales" && l.FromColumns[0] == "CustomerKey").ToList();
            Assert.Equal(2, both.Count);
            var on = both.Single(l => l.Active);
            var off = both.Single(l => !l.Active);
            Assert.Equal("Customer", on.ToTable);   // CustomerKey is Customer's own key name
            Assert.Equal("CustomerAddress", off.ToTable);
            Assert.Contains("CustomerAddress", off.Note);
            Assert.Contains("Customer", off.Note);
            Assert.Contains("USERELATIONSHIP", off.Note);
            Assert.Contains("CustomerAddress", on.GuessAlso);
            // Both are built, the second switched off, so the person can see and turn it on.
            var rels = m.ToSpec().Relationships.Where(r => r.FromTable == "Sales" && r.FromColumn == "CustomerKey").ToList();
            Assert.Equal(2, rels.Count);
            Assert.Equal(1, rels.Count(r => r.IsActive == false));
        }

        [Fact]
        public void A_role_playing_date_keeps_the_first_column_on_and_the_rest_off()
        {
            var s = Northstar();
            var sales = s.Tables.Single(t => t.Name == "Sales");
            sales.Columns = sales.Columns.Concat(new[] { C("ShipDateKey", "Int64") }).ToArray();
            var m = Derive(s, NorthstarSample());
            var dates = m.Links.Where(l => l.ToTable == "Date").OrderBy(l => l.FromColumns[0], StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "OrderDateKey", "ShipDateKey" }, dates.Select(l => l.FromColumns[0]).ToArray());
            Assert.True(dates[0].Active);
            Assert.False(dates[1].Active);
            Assert.Contains("Only one link between two tables can be on", dates[1].Note);
        }

        // ================================================================== the other side must be a key, of a compatible type

        [Fact]
        public void A_key_the_sample_shows_repeating_is_not_linked()
        {
            var m = Derive(Northstar(), NorthstarSample(customerKeyUnique: false));
            Assert.DoesNotContain(m.Links, l => l.ToTable == "Customer");
            Assert.Equal(3, m.Links.Count);
        }

        [Fact]
        public void A_column_of_another_type_is_not_linked()
        {
            var s = Northstar();
            s.Tables.Single(t => t.Name == "Sales").Columns.Single(c => c.Name == "CustomerKey").DataType = "String";
            var m = Derive(s, NorthstarSample());
            Assert.DoesNotContain(m.Links, l => l.ToTable == "Customer");
        }

        [Fact]
        public void Table_names_in_the_plural_and_a_plain_Id_key_count()
        {
            var s = new FabricSchema
            {
                Tables = new[]
                {
                    T("Customers", new string[0], C("Id", "Int64"), C("Name", "String")),
                    T("Orders", new string[0], C("Id", "Int64"), C("CustomerId", "Int64"), C("Total", "Decimal")),
                },
            };
            var m = Derive(s, LinkGuessFacts.NamesOnly());
            var link = Assert.Single(m.Links);
            Assert.Equal("Orders.CustomerId>Customers.Id", Key(link));
            Assert.Equal("table_then_key", link.GuessKind);
            Assert.Equal("fact", m.Tables.Single(t => t.Name == "Orders").Role);
        }

        [Fact]
        public void A_table_named_in_the_plural_matches_its_singular_key_name()
        {
            var s = new FabricSchema
            {
                Tables = new[]
                {
                    T("Customers", new string[0], C("CustomerKey", "Int64"), C("Name", "String")),
                    T("Invoices", new string[0], C("InvoiceKey", "Int64"), C("CustomerKey", "Int64"), C("Amount", "Decimal")),
                },
            };
            var link = Assert.Single(Derive(s, LinkGuessFacts.NamesOnly()).Links);
            Assert.Equal("Invoices.CustomerKey>Customers.CustomerKey", Key(link));
        }

        [Fact]
        public void Names_are_compared_without_case_or_separators()
        {
            var s = new FabricSchema
            {
                Tables = new[]
                {
                    T("dim_customer", new string[0], C("customer_key", "Int64"), C("name", "String")),
                    T("fact_sales", new string[0], C("sale_id", "Int64"), C("CustomerKey", "Int64"), C("Amount", "Decimal")),
                },
            };
            var m = Derive(s, LinkGuessFacts.NamesOnly());
            var link = Assert.Single(m.Links);
            Assert.Equal("fact_sales.CustomerKey>dim_customer.customer_key", Key(link));
        }

        [Fact]
        public void Two_dimensions_that_share_a_plain_column_name_are_not_linked()
        {
            var s = new FabricSchema
            {
                Tables = new[]
                {
                    T("Customer", new string[0], C("CustomerKey", "Int64"), C("Name", "String")),
                    T("Product", new string[0], C("ProductKey", "Int64"), C("Name", "String")),
                },
            };
            var facts = LinkGuessFacts.NamesOnly()
                .Measured("dbo.Customer", new Dictionary<string, bool> { ["CustomerKey"] = true, ["Name"] = true })
                .Measured("dbo.Product", new Dictionary<string, bool> { ["ProductKey"] = true, ["Name"] = true });
            Assert.Empty(Derive(s, facts).Links);
        }

        // ================================================================== a dropped guess is not built, and the plan recomputes

        private static StartPlanDoc DocOf(StructureManifest m) => new StartPlanDoc { Id = "sp-test", Manifest = m, IdentitySeed = "seed" };
        private static string Ref(ManifestLink l) => "relationship:" + l.Identity;

        [Fact]
        public void A_dropped_guess_is_not_built_and_is_not_in_what_the_review_lists_as_built()
        {
            var m = Derive(Northstar(), NorthstarSample());
            var doc = DocOf(m);
            var customer = m.Links.Single(l => l.ToTable == "Customer");
            var done = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = Ref(customer) }, "human");
            Assert.True(done.Ok, done.Refused);
            Assert.True(done.Changed);
            Assert.True(customer.Dropped);
            Assert.Equal("person", customer.DroppedBy);
            Assert.DoesNotContain(m.ToSpec().Relationships, r => r.ToTable == "Customer");
            Assert.DoesNotContain("Sales.CustomerKey>Customer.CustomerKey", StartPlanViewBuilder.Reviewed(doc).Links);
            Assert.Equal(3, StartPlanViewBuilder.Reviewed(doc).Links.Count);
            // The line is still there to read, and it reads as left out.
            var line = StartPlanViewBuilder.Build(doc).Sections.SelectMany(s => s.Groups).SelectMany(g => g.Lines).Single(l => l.Item.ObjectRef == Ref(customer));
            Assert.False(line.Kept);
        }

        [Fact]
        public void An_assistants_drop_is_marked_as_the_assistants()
        {
            var m = Derive(Northstar(), NorthstarSample());
            var doc = DocOf(m);
            var store = m.Links.Single(l => l.ToTable == "Store");
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = Ref(store) }, "agent").Ok);
            Assert.Equal("assistant", store.DroppedBy);
        }

        [Fact]
        public void Keeping_a_dropped_guess_brings_it_back()
        {
            var m = Derive(Northstar(), NorthstarSample());
            var doc = DocOf(m);
            var customer = m.Links.Single(l => l.ToTable == "Customer");
            StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = Ref(customer) }, "human");
            var back = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "keep_line", Target = Ref(customer) }, "human");
            Assert.True(back.Ok, back.Refused);
            Assert.False(customer.Dropped);
            Assert.Contains(m.ToSpec().Relationships, r => r.ToTable == "Customer");
        }

        [Fact]
        public void Dropping_one_guess_keeps_the_fact_table_and_dropping_every_one_takes_the_fact_table_and_its_measures_away()
        {
            var m = Derive(Northstar(), NorthstarSample());
            var doc = DocOf(m);
            foreach (var l in m.Links.Where(l => l.ToTable != "Store").ToList())
                Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = Ref(l) }, "human").Ok);
            Assert.Equal("fact", m.Tables.Single(t => t.Name == "Sales").Role);   // the Store link still stands
            Assert.NotEmpty(m.BuiltBases());
            // Without the date link there is no date table the fact reaches, so no time measures.
            Assert.Empty(m.TimeMeasures());
            Assert.Equal("", m.DateTable.Name);

            var store = m.Links.Single(l => l.ToTable == "Store");
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = Ref(store) }, "human").Ok);
            Assert.Equal("isolated", m.Tables.Single(t => t.Name == "Sales").Role);
            Assert.Empty(m.BuiltBases());
            Assert.Empty(m.ToSpec().Measures);
            Assert.Empty(m.ToSpec().Relationships);
        }

        [Fact]
        public void Keeping_the_guesses_again_brings_the_measures_and_the_date_table_back()
        {
            var m = Derive(Northstar(), NorthstarSample());
            var doc = DocOf(m);
            var before = m.ToSpec().Measures.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            foreach (var l in m.Links.ToList()) StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = Ref(l) }, "human");
            Assert.Empty(m.ToSpec().Measures);
            foreach (var l in m.Links.ToList()) Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "keep_line", Target = Ref(l) }, "human").Ok);
            Assert.Equal(before, m.ToSpec().Measures.Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.Equal("Date", m.DateTable.Name);
            Assert.NotEmpty(m.TimeMeasures());
        }

        [Fact]
        public void A_measure_the_person_dropped_stays_dropped_when_the_links_change()
        {
            var m = Derive(Northstar(), NorthstarSample());
            var doc = DocOf(m);
            var qty = m.BaseMeasures.Single(b => b.Column == "Quantity");
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = "measure:Sales/" + qty.Name }, "human").Ok);
            var customer = m.Links.Single(l => l.ToTable == "Customer");
            StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = Ref(customer) }, "human");
            StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "keep_line", Target = Ref(customer) }, "human");
            Assert.Equal("none", m.BaseMeasures.Single(b => b.Column == "Quantity").Pick);
        }

        [Fact]
        public void An_ambiguous_loser_that_would_give_a_second_path_stays_off_until_the_winner_is_off()
        {
            // CustomerAddress also carries CustomerKey, so it links on to Customer: turning Sales to CustomerAddress on beside Sales to Customer is two paths to Customer.
            var s = Northstar();
            s.Tables = s.Tables.Concat(new[] { T("CustomerAddress", new string[0], C("CustomerKey", "Int64"), C("Street", "String")) }).ToArray();
            var facts = NorthstarSample().Measured("dbo.CustomerAddress", new Dictionary<string, bool> { ["CustomerKey"] = true });
            var m = Derive(s, facts);
            var doc = DocOf(m);
            var on = m.Links.Single(l => l.Guessed && l.Active && l.FromTable == "Sales" && l.ToTable == "Customer");
            var off = m.Links.Single(l => l.Guessed && !l.Active && l.ToTable == "CustomerAddress");
            Assert.Contains(m.Links, l => l.FromTable == "CustomerAddress" && l.ToTable == "Customer" && l.Active);
            var refused = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_link", Target = off.Identity, Value = "on" }, "human");
            Assert.False(refused.Ok);
            Assert.Contains("second path", refused.Refused);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_link", Target = on.Identity, Value = "off" }, "human").Ok);
            var done = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_link", Target = off.Identity, Value = "on" }, "human");
            Assert.True(done.Ok, done.Refused);
            Assert.True(off.Active);
            Assert.Null(off.Note);
        }

        [Fact]
        public void A_twin_of_an_active_link_cannot_be_turned_on_until_the_other_is_off()
        {
            var s = Northstar();
            var sales = s.Tables.Single(t => t.Name == "Sales");
            sales.Columns = sales.Columns.Concat(new[] { C("ShipDateKey", "Int64") }).ToArray();
            var m = Derive(s, NorthstarSample());
            var doc = DocOf(m);
            var first = m.Links.Single(l => l.FromColumns[0] == "OrderDateKey");
            var second = m.Links.Single(l => l.FromColumns[0] == "ShipDateKey");
            var refused = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_link", Target = second.Identity, Value = "on" }, "human");
            Assert.False(refused.Ok);
            Assert.Contains("Turn the other link off first", refused.Refused);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_link", Target = first.Identity, Value = "off" }, "human").Ok);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_link", Target = second.Identity, Value = "on" }, "human").Ok);
            Assert.True(second.Active);
        }

        [Fact]
        public void A_dropped_twin_that_comes_back_after_the_other_took_its_place_comes_back_off()
        {
            var s = Northstar();
            var sales = s.Tables.Single(t => t.Name == "Sales");
            sales.Columns = sales.Columns.Concat(new[] { C("ShipDateKey", "Int64") }).ToArray();
            var m = Derive(s, NorthstarSample());
            var doc = DocOf(m);
            var first = m.Links.Single(l => l.FromColumns[0] == "OrderDateKey");
            var second = m.Links.Single(l => l.FromColumns[0] == "ShipDateKey");
            // Drop the first, turn the second on in its place, then bring the first back: two on between the same tables would fail the build.
            StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = Ref(first) }, "human");
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_link", Target = second.Identity, Value = "on" }, "human").Ok);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "keep_line", Target = Ref(first) }, "human").Ok);
            Assert.False(first.Dropped);
            Assert.False(first.Active);
            Assert.Contains("Only one link between two tables can be on", first.Note);
            Assert.Equal(1, m.ToSpec().Relationships.Count(r => r.ToTable == "Date" && r.IsActive != false));
        }

        [Fact]
        public void Dropping_a_declared_link_line_turns_it_off_the_way_set_link_does()
        {
            var m = Derive(Northstar(Fk("Sales", "CustomerKey", "Customer", "CustomerKey")), NorthstarSample());
            var doc = DocOf(m);
            var declared = m.Links.Single(l => !l.Guessed);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = Ref(declared) }, "human").Ok);
            Assert.False(declared.Active);
            Assert.False(declared.Dropped);
        }

        // ================================================================== the sample lands after the first plan was made

        [Fact]
        public void A_sample_that_arrives_later_takes_a_link_out_when_the_key_repeats_and_keeps_the_persons_drops()
        {
            var m = Derive(Northstar(), LinkGuessFacts.NamesOnly());
            var doc = DocOf(m);
            doc.Schema = Northstar();
            var product = m.Links.Single(l => l.ToTable == "Product");
            StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = Ref(product) }, "human");

            var changed = LinkGuesser.Reconsider(doc, NorthstarSample(customerKeyUnique: false));
            Assert.True(changed);
            Assert.DoesNotContain(m.Links, l => l.ToTable == "Customer");
            Assert.True(m.Links.Single(l => l.ToTable == "Product").Dropped);   // the person's drop stands
            Assert.Equal("sample", m.Links.Single(l => l.ToTable == "Store").GuessEvidence);
            Assert.Equal("fact", m.Tables.Single(t => t.Name == "Sales").Role);
        }

        [Fact]
        public void A_sample_that_changes_nothing_changes_nothing()
        {
            var m = Derive(Northstar(), NorthstarSample());
            var doc = DocOf(m);
            doc.Schema = Northstar();
            Assert.False(LinkGuesser.Reconsider(doc, NorthstarSample()));
        }

        [Fact]
        public void A_sample_never_touches_a_declared_link()
        {
            var s = Northstar(Fk("Sales", "CustomerKey", "Customer", "CustomerKey"));
            var m = Derive(s, LinkGuessFacts.NamesOnly());
            var doc = DocOf(m);
            doc.Schema = s;
            LinkGuesser.Reconsider(doc, NorthstarSample(customerKeyUnique: false));
            var declared = m.Links.Single(l => l.ToTable == "Customer");
            Assert.False(declared.Guessed);
            Assert.True(declared.Active);
        }
    }
}
