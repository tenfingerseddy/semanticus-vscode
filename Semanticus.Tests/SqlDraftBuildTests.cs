using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>"Draft from a SQL source" must give a draft that BUILDS CLEANLY (plan statement check, 28 Sep, defect 4).
    /// The source is the J8 fixture's schema (uplift-services: 7 tables, 9 declared foreign keys) written as the
    /// INFORMATION_SCHEMA read a SQL endpoint gives, and it goes through the product's own mapping,
    /// <see cref="LocalEngine.FabricSchemaToSpec"/>, the pure function behind the wizard's SQL step. No live endpoint
    /// is involved: there is no Fabric here, and the network half (the sign-in and the schema read) is not what failed.
    ///
    /// What failed, measured: every declared foreign key came out active, so the second and third links from
    /// fact_invoice to dim_date failed to build ("An active relationship already exists"), the direct link from
    /// fact_invoice to dim_client made a second path beside the one through dim_project, and a calculated CALENDAR
    /// table "Date" was added next to the source's own dim_date instead of marking it.</summary>
    public sealed class SqlDraftBuildTests
    {
        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };

        private static FabricForeignKey Fk(string from, string fromCol, string to, string toCol) => new FabricForeignKey
        { FromSchema = "dbo", FromTable = from, FromColumn = fromCol, ToSchema = "dbo", ToTable = to, ToColumn = toCol };

        /// <summary>The J8 schema exactly as the check's j8-schema.json carries it (tables, columns, types, keys and the
        /// nine foreign keys, in the order the endpoint lists them).</summary>
        internal static FabricSchema J8Schema() => new FabricSchema
        {
            Server = "contoso-sql.example.com",
            Database = "ContosoConsulting",
            Tables = new[]
            {
                new FabricTable { Schema = "dbo", Name = "ref_codes", KeyColumns = new[] { "CODE_TYPE", "CODE" },
                    Columns = new[] { C("CODE_TYPE", "String"), C("CODE", "String"), C("CODE_DESC", "String") } },
                new FabricTable { Schema = "dbo", Name = "dim_date", KeyColumns = new[] { "DATE_KEY" },
                    Columns = new[] { C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime"), C("FY_NO", "Int64"), C("FY_QTR", "Int64"),
                        C("MTH_NO", "Int64"), C("MTH_ABBR", "String"), C("DOW_NO", "Int64"), C("WKDAY_FLG", "String") } },
                new FabricTable { Schema = "dbo", Name = "dim_client", KeyColumns = new[] { "CLNT_ID" },
                    Columns = new[] { C("CLNT_ID", "String"), C("CLNT_NM", "String"), C("IND_CD", "String"), C("RGN_CD", "String"),
                        C("ACTV_FLG", "String"), C("ONBRD_DT_KEY", "Int64") } },
                new FabricTable { Schema = "dbo", Name = "dim_staff", KeyColumns = new[] { "EMP_NO" },
                    Columns = new[] { C("EMP_NO", "String"), C("EMP_NM", "String"), C("GRADE_CD", "String"), C("PRAC_CD", "String"),
                        C("STD_RATE", "Decimal"), C("HIRE_DT_KEY", "Int64"), C("TERM_FLG", "String") } },
                new FabricTable { Schema = "dbo", Name = "dim_project", KeyColumns = new[] { "PRJ_ID" },
                    Columns = new[] { C("PRJ_ID", "String"), C("CLNT_ID", "String"), C("PRJ_NM", "String"), C("PRAC_CD", "String"),
                        C("ENG_TYPE_CD", "String"), C("BUDGET_HRS", "Int64"), C("BUDGET_AMT", "Decimal"), C("START_DT_KEY", "Int64"),
                        C("END_DT_KEY", "Int64"), C("STAT_CD", "String") } },
                new FabricTable { Schema = "dbo", Name = "fact_timesheet", KeyColumns = new[] { "TS_LINE_ID" },
                    Columns = new[] { C("TS_LINE_ID", "String"), C("EMP_NO", "String"), C("PRJ_ID", "String"), C("WORK_DT_KEY", "Int64"),
                        C("HRS", "Decimal"), C("BILL_FLG", "String"), C("BILL_RATE", "Decimal"), C("APPR_FLG", "String"), C("SRC_ROW_TXT", "String") } },
                new FabricTable { Schema = "dbo", Name = "fact_invoice", KeyColumns = new[] { "INV_NO" },
                    Columns = new[] { C("INV_NO", "String"), C("PRJ_ID", "String"), C("CLNT_ID", "String"), C("INV_DT_KEY", "Int64"),
                        C("DUE_DT_KEY", "Int64"), C("PAID_DT_KEY", "Int64"), C("INV_AMT", "Decimal"), C("TAX_AMT", "Decimal"),
                        C("CURR_CD", "String"), C("VOID_FLG", "String") } },
            },
            ForeignKeys = new[]
            {
                Fk("fact_timesheet", "EMP_NO", "dim_staff", "EMP_NO"),
                Fk("fact_timesheet", "PRJ_ID", "dim_project", "PRJ_ID"),
                Fk("fact_timesheet", "WORK_DT_KEY", "dim_date", "DATE_KEY"),
                Fk("dim_project", "CLNT_ID", "dim_client", "CLNT_ID"),
                Fk("fact_invoice", "PRJ_ID", "dim_project", "PRJ_ID"),
                Fk("fact_invoice", "INV_DT_KEY", "dim_date", "DATE_KEY"),
                Fk("fact_invoice", "CLNT_ID", "dim_client", "CLNT_ID"),
                Fk("fact_invoice", "DUE_DT_KEY", "dim_date", "DATE_KEY"),
                Fk("fact_invoice", "PAID_DT_KEY", "dim_date", "DATE_KEY"),
            },
        };

        private static ModelSpec J8Draft() => LocalEngine.FabricSchemaToSpec(J8Schema(), "contoso-sql.example.com", "ContosoConsulting", "import");

        private static SpecRelationship Rel(ModelSpec spec, string from, string fromCol) =>
            spec.Relationships.Single(r => r.FromTable == from && r.FromColumn == fromCol);

        [Fact]
        public void A_second_and_third_link_to_the_same_table_are_drafted_inactive_and_say_why()
        {
            var spec = J8Draft();

            // The first declared link from fact_invoice to dim_date stays the active one.
            Assert.NotEqual(false, Rel(spec, "fact_invoice", "INV_DT_KEY").IsActive);
            foreach (var col in new[] { "DUE_DT_KEY", "PAID_DT_KEY" })
            {
                var r = Rel(spec, "fact_invoice", col);
                Assert.False(r.IsActive, $"fact_invoice[{col}] -> dim_date must be drafted inactive");
                Assert.False(string.IsNullOrWhiteSpace(r.Note), $"fact_invoice[{col}] must carry a note saying why it is off");
                Assert.Contains("dim_date", r.Note);
                Assert.Contains("USERELATIONSHIP", r.Note);
            }
        }

        /// <summary>fact_invoice links to dim_client directly AND through dim_project. Round 1 kept the chain and turned the
        /// fact's own link off. The coordinator's ruling (28 Sep, review-fix round 2) is the other way round: a fact's
        /// own links always stay on, and the link that closes the second path and is not a fact's own link goes off,
        /// here the snowflake hop from dim_project to dim_client.</summary>
        [Fact]
        public void A_second_path_to_the_same_table_is_drafted_inactive_and_says_why()
        {
            var spec = J8Draft();

            // The fact's own link to the client stays on.
            Assert.NotEqual(false, Rel(spec, "fact_invoice", "CLNT_ID").IsActive);
            // The hop from project to client is the second path, and it says which table already reaches which.
            var hop = Rel(spec, "dim_project", "CLNT_ID");
            Assert.False(hop.IsActive);
            Assert.Contains("fact_invoice", hop.Note);
            Assert.Contains("dim_client", hop.Note);
            Assert.Contains("USERELATIONSHIP", hop.Note);
            // Every other link stays on with no note.
            foreach (var r in spec.Relationships.Where(r => r != hop
                && !(r.FromTable == "fact_invoice" && (r.FromColumn == "DUE_DT_KEY" || r.FromColumn == "PAID_DT_KEY"))))
            {
                Assert.NotEqual(false, r.IsActive);
                Assert.Null(r.Note);
            }
            Assert.Equal(3, spec.Relationships.Count(r => r.IsActive == false));
        }

        // ---- review-fix round 2: the link rule decides by what a link is (coordinator's ruling, 28 Sep) ------------

        private static FabricTable T(string name, string[] keys, params FabricColumn[] cols) =>
            new FabricTable { Schema = "dbo", Name = name, KeyColumns = keys, Columns = cols };

        /// <summary>Every table a fact declares a key to, the fact can be filtered by: it has an active link to it.</summary>
        private static void AssertEachFactKeepsALinkToEachTableItNames(ModelSpec spec)
        {
            // A table that names other tables and that no other table names is a fact, whatever key it has to itself
            // (verifier R2-H1): without this the loop below never looks at a fact that was drafted as a dimension.
            foreach (var t in spec.Tables.Where(t => t.Role != "date"
                         && spec.Relationships.Any(r => r.FromTable == t.Name && r.ToTable != t.Name)
                         && !spec.Relationships.Any(r => r.ToTable == t.Name && r.FromTable != t.Name)))
                Assert.True(t.Role == "fact", $"{t.Name} names other tables and nothing names it, yet it is drafted as a {t.Role}");
            foreach (var fact in spec.Tables.Where(t => t.Role == "fact"))
                foreach (var target in spec.Relationships.Where(r => r.FromTable == fact.Name && r.FromTable != r.ToTable).Select(r => r.ToTable).Distinct())
                    Assert.True(spec.Relationships.Any(r => r.FromTable == fact.Name && r.ToTable == target && r.IsActive != false),
                        $"{fact.Name} has no active link to {target}, so {target} cannot filter it");
        }

        public static TheoryData<string> Shapes => new() { "j8", "j8-onboarding-date", "diamond", "galaxy", "role-playing-declared-backwards",
            "j8-credit-note", "order-replaces-order", "diamond-no-fact-key-to-geography", "dimension-with-two-dates" };

        internal static FabricSchema Shape(string name)
        {
            switch (name)
            {
                case "j8": return J8Schema();
                case "j8-onboarding-date":
                    var j8 = J8Schema();
                    j8.ForeignKeys = j8.ForeignKeys.Concat(new[] { Fk("dim_client", "ONBRD_DT_KEY", "dim_date", "DATE_KEY"), Fk("dim_project", "START_DT_KEY", "dim_date", "DATE_KEY") }).ToArray();
                    return j8;
                case "diamond":
                    return new FabricSchema
                    {
                        Tables = new[]
                        {
                            T("dim_geography", new[] { "GEO_ID" }, C("GEO_ID", "Int64"), C("CITY", "String")),
                            T("dim_store", new[] { "STORE_ID" }, C("STORE_ID", "Int64"), C("GEO_ID", "Int64")),
                            T("dim_customer", new[] { "CUST_ID" }, C("CUST_ID", "Int64"), C("GEO_ID", "Int64")),
                            T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("STORE_ID", "Int64"), C("CUST_ID", "Int64"), C("GEO_ID", "Int64"), C("AMT", "Decimal")),
                        },
                        ForeignKeys = new[]
                        {
                            Fk("dim_store", "GEO_ID", "dim_geography", "GEO_ID"),
                            Fk("dim_customer", "GEO_ID", "dim_geography", "GEO_ID"),
                            Fk("fact_sales", "STORE_ID", "dim_store", "STORE_ID"),
                            Fk("fact_sales", "CUST_ID", "dim_customer", "CUST_ID"),
                            Fk("fact_sales", "GEO_ID", "dim_geography", "GEO_ID"),
                        },
                    };
                case "galaxy":
                    return new FabricSchema
                    {
                        Tables = new[]
                        {
                            T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                            T("dim_product", new[] { "PROD_ID" }, C("PROD_ID", "Int64"), C("CAT_ID", "Int64")),
                            T("dim_category", new[] { "CAT_ID" }, C("CAT_ID", "Int64"), C("LAUNCH_DT_KEY", "Int64")),
                            T("fact_sales", new[] { "ID" }, C("ID", "Int64"), C("PROD_ID", "Int64"), C("DATE_KEY", "Int64"), C("AMT", "Decimal")),
                            T("fact_stock", new[] { "ID" }, C("ID", "Int64"), C("PROD_ID", "Int64"), C("DATE_KEY", "Int64"), C("QTY", "Decimal")),
                        },
                        ForeignKeys = new[]
                        {
                            Fk("dim_category", "LAUNCH_DT_KEY", "dim_date", "DATE_KEY"),
                            Fk("dim_product", "CAT_ID", "dim_category", "CAT_ID"),
                            Fk("fact_sales", "PROD_ID", "dim_product", "PROD_ID"),
                            Fk("fact_sales", "DATE_KEY", "dim_date", "DATE_KEY"),
                            Fk("fact_stock", "PROD_ID", "dim_product", "PROD_ID"),
                            Fk("fact_stock", "DATE_KEY", "dim_date", "DATE_KEY"),
                        },
                    };
                case "role-playing-declared-backwards":
                    return new FabricSchema
                    {
                        Tables = new[]
                        {
                            T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                            T("fact_order", new[] { "ORDER_ID" }, C("ORDER_ID", "Int64"), C("ORDER_DT_KEY", "Int64"), C("SHIP_DT_KEY", "Int64"), C("AMT", "Decimal")),
                        },
                        ForeignKeys = new[]
                        {
                            Fk("fact_order", "SHIP_DT_KEY", "dim_date", "DATE_KEY"),
                            Fk("fact_order", "ORDER_DT_KEY", "dim_date", "DATE_KEY"),
                        },
                    };
                case "j8-credit-note":
                    // The verifier's R2-H1 shape: a credit note names the invoice it credits.
                    var credit = J8Schema();
                    credit.Tables = credit.Tables.Select(t => t.Name != "fact_invoice" ? t : new FabricTable
                    {
                        Schema = t.Schema, Name = t.Name, KeyColumns = t.KeyColumns,
                        Columns = t.Columns.Append(C("CREDIT_FOR_INV_NO", "String")).ToArray(),
                    }).ToArray();
                    credit.ForeignKeys = credit.ForeignKeys.Append(Fk("fact_invoice", "CREDIT_FOR_INV_NO", "fact_invoice", "INV_NO")).ToArray();
                    return credit;
                case "order-replaces-order":
                    return new FabricSchema
                    {
                        Tables = new[]
                        {
                            T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                            T("dim_customer", new[] { "CUST_ID" }, C("CUST_ID", "Int64"), C("CUST_NM", "String"), C("SIGNUP_DT_KEY", "Int64")),
                            T("fact_order", new[] { "ORDER_ID" }, C("ORDER_ID", "Int64"), C("REPLACES_ORDER_ID", "Int64"), C("CUST_ID", "Int64"), C("ORDER_DT_KEY", "Int64"), C("AMT", "Decimal")),
                        },
                        ForeignKeys = new[]
                        {
                            Fk("dim_customer", "SIGNUP_DT_KEY", "dim_date", "DATE_KEY"),
                            Fk("fact_order", "CUST_ID", "dim_customer", "CUST_ID"),
                            Fk("fact_order", "ORDER_DT_KEY", "dim_date", "DATE_KEY"),
                            Fk("fact_order", "REPLACES_ORDER_ID", "fact_order", "ORDER_ID"),
                        },
                    };
                case "diamond-no-fact-key-to-geography":
                    // Verifier R2-L1: with no fact key to the shared table, which hop stays on was decided by the order
                    // the endpoint listed the keys.
                    return new FabricSchema
                    {
                        Tables = new[]
                        {
                            T("dim_geography", new[] { "GEO_ID" }, C("GEO_ID", "Int64"), C("CITY", "String")),
                            T("dim_store", new[] { "STORE_ID" }, C("STORE_ID", "Int64"), C("GEO_ID", "Int64")),
                            T("dim_customer", new[] { "CUST_ID" }, C("CUST_ID", "Int64"), C("GEO_ID", "Int64")),
                            T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("STORE_ID", "Int64"), C("CUST_ID", "Int64"), C("AMT", "Decimal")),
                        },
                        ForeignKeys = new[]
                        {
                            Fk("dim_store", "GEO_ID", "dim_geography", "GEO_ID"),
                            Fk("dim_customer", "GEO_ID", "dim_geography", "GEO_ID"),
                            Fk("fact_sales", "STORE_ID", "dim_store", "STORE_ID"),
                            Fk("fact_sales", "CUST_ID", "dim_customer", "CUST_ID"),
                        },
                    };
                case "dimension-with-two-dates":
                    // Verifier R2-L1: a dimension with a hire date and a termination date kept whichever was listed first.
                    return new FabricSchema
                    {
                        Tables = new[]
                        {
                            T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                            T("dim_staff", new[] { "EMP_NO" }, C("EMP_NO", "Int64"), C("HIRE_DT_KEY", "Int64"), C("TERM_DT_KEY", "Int64")),
                            T("fact_hours", new[] { "ID" }, C("ID", "Int64"), C("EMP_NO", "Int64"), C("HRS", "Decimal")),
                        },
                        ForeignKeys = new[]
                        {
                            Fk("dim_staff", "TERM_DT_KEY", "dim_date", "DATE_KEY"),
                            Fk("dim_staff", "HIRE_DT_KEY", "dim_date", "DATE_KEY"),
                            Fk("fact_hours", "EMP_NO", "dim_staff", "EMP_NO"),
                        },
                    };
                default: throw new ArgumentOutOfRangeException(nameof(name));
            }
        }

        /// <summary>The property the ruling protects, over five shapes: no fact is ever left unable to be filtered by a
        /// table it names, and it builds with 0 errors. Failed first at 4255fb78 on "j8" (fact_invoice to dim_client off),
        /// "j8-onboarding-date" (every fact date link off), "diamond" (fact_sales to dim_customer off) and "galaxy".</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [MemberData(nameof(Shapes))]
        public async Task Each_fact_keeps_an_active_link_to_every_table_it_names_and_the_draft_builds(string shape)
        {
            var spec = LocalEngine.FabricSchemaToSpec(Shape(shape), "x", "DW", "import");

            AssertEachFactKeepsALinkToEachTableItNames(spec);
            Assert.All(spec.Relationships.Where(r => r.IsActive == false), r => Assert.False(string.IsNullOrWhiteSpace(r.Note)));

            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Shape", 1604);
            await engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            var report = await engine.BuildModelFromSpecAsync("human");
            Assert.Empty(report.Errors);
        }

        /// <summary>A dimension's own date key closes a second path from each fact to the date table, so it is the link
        /// that goes off, and its note names the fact that already reaches the date table and why.</summary>
        [Fact]
        public void A_dimension_date_key_is_the_link_drafted_off_and_its_note_says_why()
        {
            var spec = LocalEngine.FabricSchemaToSpec(Shape("j8-onboarding-date"), "x", "DW", "import");

            var onboarding = Rel(spec, "dim_client", "ONBRD_DT_KEY");
            Assert.False(onboarding.IsActive);
            Assert.Contains("dim_date", onboarding.Note);
            Assert.Contains("second path", onboarding.Note);
            Assert.Contains("own links", onboarding.Note);
            Assert.False(Rel(spec, "dim_project", "START_DT_KEY").IsActive);
            Assert.NotEqual(false, Rel(spec, "fact_invoice", "INV_DT_KEY").IsActive);
            Assert.NotEqual(false, Rel(spec, "fact_timesheet", "WORK_DT_KEY").IsActive);
        }

        /// <summary>A diamond: the extra outrigger hop goes off, never the fact's link to a dimension. With the fact's own
        /// link to dim_geography as well, both outrigger hops go off.</summary>
        [Fact]
        public void In_a_diamond_the_outrigger_hops_go_off_and_every_fact_link_stays_on()
        {
            var spec = LocalEngine.FabricSchemaToSpec(Shape("diamond"), "x", "DW", "import");

            Assert.All(spec.Relationships.Where(r => r.FromTable == "fact_sales"), r => Assert.NotEqual(false, r.IsActive));
            Assert.False(Rel(spec, "dim_store", "GEO_ID").IsActive);
            Assert.False(Rel(spec, "dim_customer", "GEO_ID").IsActive);
        }

        /// <summary>A galaxy whose category also carries a launch date: the hierarchy hop (product to category) stays on and
        /// the category's own date key goes off, whichever the source declares first. Both facts keep their own links.</summary>
        [Fact]
        public void In_a_galaxy_the_hierarchy_stays_on_and_a_dimension_date_key_goes_off()
        {
            var spec = LocalEngine.FabricSchemaToSpec(Shape("galaxy"), "x", "DW", "import");

            Assert.NotEqual(false, Rel(spec, "dim_product", "CAT_ID").IsActive);
            Assert.False(Rel(spec, "dim_category", "LAUNCH_DT_KEY").IsActive);
            Assert.Equal(1, spec.Relationships.Count(r => r.IsActive == false));
        }

        /// <summary>Two of a fact's own links to one table: the one the source lists first among the fact's COLUMNS stays
        /// on, whatever order the keys come back in (the endpoint's key query has no order). Failed first at 4255fb78:
        /// the first DECLARED key, the ship date, was kept.</summary>
        [Fact]
        public void Of_two_fact_links_to_one_table_the_first_column_stays_on()
        {
            var spec = LocalEngine.FabricSchemaToSpec(Shape("role-playing-declared-backwards"), "x", "DW", "import");

            Assert.NotEqual(false, Rel(spec, "fact_order", "ORDER_DT_KEY").IsActive);
            var ship = Rel(spec, "fact_order", "SHIP_DT_KEY");
            Assert.False(ship.IsActive);
            Assert.Contains("ORDER_DT_KEY", ship.Note);
        }

        /// <summary>LOW, verifier L7: a table's key to itself (an employee's manager) was drafted on and built with 0
        /// errors offline, then failed at deploy. It is drafted off with a note, and the build leaves it out and says so.</summary>
        [Fact]
        public async Task A_link_from_a_table_to_itself_is_drafted_off_and_not_built()
        {
            var schema = new FabricSchema
            {
                Tables = new[]
                {
                    T("dim_employee", new[] { "EMP_ID" }, C("EMP_ID", "Int64"), C("MGR_ID", "Int64"), C("EMP_NM", "String")),
                    T("fact_hours", new[] { "ID" }, C("ID", "Int64"), C("EMP_ID", "Int64"), C("HRS", "Decimal")),
                },
                ForeignKeys = new[] { Fk("dim_employee", "MGR_ID", "dim_employee", "EMP_ID"), Fk("fact_hours", "EMP_ID", "dim_employee", "EMP_ID") },
            };
            var spec = LocalEngine.FabricSchemaToSpec(schema, "x", "DW", "import");
            var self = Rel(spec, "dim_employee", "MGR_ID");
            Assert.False(self.IsActive);
            Assert.Contains("itself", self.Note);

            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Self", 1604);
            await engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            var report = await engine.BuildModelFromSpecAsync("human");

            Assert.Empty(report.Errors);
            Assert.Contains(report.Notes, n => n.Contains("dim_employee[MGR_ID]") && n.Contains("itself"));
            var graph = await engine.GetModelGraphAsync();
            Assert.DoesNotContain(graph.Relationships, r => r.FromTable == r.ToTable);
            Assert.Single(graph.Relationships);
        }

        /// <summary>LOW, verifier L1: with no declared keys (common on Fabric), a table that only mentions a calendar and
        /// is listed first ("calendar_events") was marked as the date table instead of dim_date.</summary>
        [Fact]
        public void With_no_declared_keys_a_table_named_only_for_dates_is_the_date_table()
        {
            var schema = new FabricSchema
            {
                Tables = new[]
                {
                    T("calendar_events", Array.Empty<string>(), C("EVENT_ID", "Int64"), C("START_DT", "DateTime"), C("END_DT", "DateTime")),
                    T("dim_date", Array.Empty<string>(), C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                    T("fact_sales", Array.Empty<string>(), C("SALE_ID", "Int64"), C("DATE_KEY", "Int64"), C("AMT", "Decimal")),
                },
                ForeignKeys = Array.Empty<FabricForeignKey>(),
            };
            Assert.Equal("dim_date", LocalEngine.FabricSchemaToSpec(schema, "x", "DW", "import").DateTable.Name);
        }

        /// <summary>LOW, verifier L4: the build note said "is off. Off because", the same words twice.</summary>
        [Fact]
        public async Task The_build_note_for_a_link_that_is_off_does_not_repeat_itself()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("ContosoConsulting", 1604);
            await engine.SetSpecAsync(JsonSerializer.Serialize(J8Draft(), Camel), "human");
            var report = await engine.BuildModelFromSpecAsync("human");

            var linkNotes = report.Notes.Where(n => n.Contains("USERELATIONSHIP")).ToArray();
            Assert.Equal(3, linkNotes.Length);
            Assert.All(linkNotes, n => Assert.DoesNotContain("is off. Off", n));
        }

        /// <summary>MEDIUM, the rest of verifier M1: a spec date table of the spec's own with two date columns, not marked
        /// and with no column named, is left unmarked with a plain note, not an error that names an argument.</summary>
        [Fact]
        public async Task An_own_date_table_with_two_date_columns_and_no_column_named_gets_a_note_not_an_error()
        {
            var spec = new ModelSpec
            {
                Name = "Hand", StorageMode = "import",
                Tables = new[]
                {
                    new SpecTable { Name = "Calendar", Role = "date", Columns = new[] { new SpecColumn { Name = "Day", DataType = "DateTime" }, new SpecColumn { Name = "Month Start", DataType = "DateTime" } } },
                },
                DateTable = new SpecDateTable { Name = "Calendar" },
            };
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Hand", 1604);
            await engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            var report = await engine.BuildModelFromSpecAsync("human");

            Assert.Empty(report.Errors);
            var note = Assert.Single(report.Notes, n => n.StartsWith("Calendar was not marked", StringComparison.Ordinal));
            Assert.DoesNotContain("dateColumn", note);
            Assert.Contains("Mark as Date Table", note);
        }

        [Fact]
        public void A_source_date_table_is_marked_instead_of_adding_a_calendar_table()
        {
            var spec = J8Draft();

            Assert.NotNull(spec.DateTable);
            Assert.Equal("dim_date", spec.DateTable.Name);
            Assert.Equal("CAL_DT", spec.DateTable.DateColumn);
            Assert.DoesNotContain(spec.Tables, t => t.Name == "Date");
            var dimDate = spec.Tables.Single(t => t.Name == "dim_date");
            Assert.Equal("date", dimDate.Role);
            // A table has one key column, and a marked date table's key is its date column.
            Assert.True(dimDate.Columns.Single(c => c.Name == "CAL_DT").IsKey);
            Assert.False(dimDate.Columns.Single(c => c.Name == "DATE_KEY").IsKey);
            Assert.True(dimDate.Columns.Single(c => c.Name == "DATE_KEY").Hidden);
        }

        [Fact]
        public void A_source_with_no_date_table_gets_no_calendar_table_and_marks_none()
        {
            var schema = new FabricSchema
            {
                Tables = new[]
                {
                    new FabricTable { Schema = "dbo", Name = "Sales", KeyColumns = new[] { "Id" },
                        Columns = new[] { C("Id", "Int64"), C("CustomerId", "Int64"), C("OrderDate", "DateTime"), C("Amount", "Decimal") } },
                    new FabricTable { Schema = "dbo", Name = "Customer", KeyColumns = new[] { "CustomerId" },
                        Columns = new[] { C("CustomerId", "Int64"), C("Name", "String") } },
                },
                ForeignKeys = new[] { Fk("Sales", "CustomerId", "Customer", "CustomerId") },
            };
            var spec = LocalEngine.FabricSchemaToSpec(schema, "x", "DW", "import");

            // Round 7 (verifier round 6, S08): a calendar no fact links to filters nothing, so none is added and none is marked.
            Assert.True(string.IsNullOrEmpty(spec.DateTable.Name));
            Assert.Null(spec.DateTable.DateColumn);
            Assert.False(spec.DateTable.MarkAsDate);
            Assert.DoesNotContain(spec.Tables, t => t.Name == "Date");
            Assert.Contains("OrderDate", spec.DateTable.Note);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_J8_draft_builds_with_no_errors_and_no_date_table_findings()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("ContosoConsulting", 1604);
            await engine.SetSpecAsync(JsonSerializer.Serialize(J8Draft(), Camel), "human");

            var report = await engine.BuildModelFromSpecAsync("human");

            Assert.Empty(report.Errors);
            Assert.Contains(report.Notes, n => n.Contains("dim_date") && n.Contains("date table"));
            Assert.Equal(3, report.Notes.Count(n => n.Contains("USERELATIONSHIP")));

            var graph = await engine.GetModelGraphAsync();
            Assert.Equal(9, graph.Relationships.Length);
            Assert.Equal(3, graph.Relationships.Count(r => !r.IsActive));
            Assert.Contains(graph.Relationships, r => r.FromTable == "fact_invoice" && r.FromColumn == "INV_DT_KEY" && r.IsActive);

            var (tables, category, calKey, dateKey, tiDax) = await sessions.Current.ReadAsync(m => (
                m.Tables.Select(t => t.Name).ToArray(),
                m.Tables["dim_date"].DataCategory,
                m.Tables["dim_date"].Columns["CAL_DT"].IsKey,
                m.Tables["dim_date"].Columns["DATE_KEY"].IsKey,
                m.AllMeasures.Where(x => x.DisplayFolder == "Time Intelligence").Select(x => x.Expression).ToArray()));

            Assert.DoesNotContain("Date", tables);
            Assert.DoesNotContain(tables, t => t.Equals("Date", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Time", category);
            Assert.True(calKey);
            Assert.False(dateKey);
            Assert.NotEmpty(tiDax);
            Assert.All(tiDax, d => Assert.Contains("'dim_date'[CAL_DT]", d));

            var scan = await engine.BpaScanAsync();
            var ids = scan.Violations.Select(v => v.RuleId).ToHashSet();
            Assert.DoesNotContain("MODEL_SHOULD_HAVE_A_DATE_TABLE", ids);
            Assert.DoesNotContain("DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE", ids);
            Assert.DoesNotContain("REDUCE_USAGE_OF_CALCULATED_TABLES", ids);
        }

        /// <summary>The ask bar sends these to the engine's router (they name a place, not a task on this model), and
        /// the router must answer with the Model Spec, whose empty-model view is the new model wizard. Plan
        /// statement check, defect 6: "model spec" and "build a model from scratch" became tasks.</summary>
        [Theory]
        [InlineData("model spec")]
        [InlineData("build a model from scratch")]
        [InlineData("build the model from my SQL database")]
        [InlineData("where is the model spec?")]
        public void The_new_model_words_route_to_the_model_spec(string ask)
        {
            Assert.Equal("spec", AskRoutePoint.MatchWords(ask)?.Tool);
        }

        /// <summary>Round 6, F4 said "new model from a SQL source" is not a request, because "new" is not a verb. Round 7 (verifier round 6,
        /// held-out: "new model please, from SQL Server", "new dataset from the SQL source please") widened the ruling by intent: a bare
        /// "new model ..." with a source named asks for one, and it is the Model Spec's.</summary>
        [Fact]
        public void A_bare_new_model_from_sql_is_the_model_spec()
        {
            Assert.Equal("spec", AskRoutePoint.MatchWords("new model from a SQL source")?.Tool);
        }

        [Fact]
        public async Task A_hand_written_spec_that_names_one_of_its_own_tables_marks_that_table()
        {
            // The same contract without the SQL mapping: a dateTable naming one of the spec's own tables is that table.
            var spec = new ModelSpec
            {
                Name = "Hand", StorageMode = "import",
                Tables = new[]
                {
                    new SpecTable { Name = "Calendar", Role = "date", Columns = new[] { new SpecColumn { Name = "Day", DataType = "DateTime" }, new SpecColumn { Name = "Year", DataType = "Int64" } } },
                },
                DateTable = new SpecDateTable { Name = "Calendar" },
            };
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Hand", 1604);
            await engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");

            var report = await engine.BuildModelFromSpecAsync("human");

            Assert.Empty(report.Errors);
            var (count, category, dayKey) = await sessions.Current.ReadAsync(m => (
                m.Tables.Count, m.Tables["Calendar"].DataCategory, m.Tables["Calendar"].Columns["Day"].IsKey));
            Assert.Equal(1, count);
            Assert.Equal("Time", category);
            Assert.True(dayKey);
        }

        // ---- Verifier round 2 (at 95d0b86f), beside the verifier's own SqlDraftVerifyR2Tests ------------------------

        private static string[] ActiveLinks(ModelSpec spec) => spec.Relationships.Where(r => r.IsActive != false)
            .Select(r => $"{r.FromTable}[{r.FromColumn}]->{r.ToTable}").OrderBy(x => x, StringComparer.Ordinal).ToArray();

        /// <summary>R2-H1: a fact with a key to itself is still a fact, so it gets its starter measures, and the self link
        /// is drafted off with a note that fits a fact (the row it names can still be found), not the parent and child
        /// advice meant for a manager key on a dimension.</summary>
        [Theory]
        [InlineData("j8-credit-note", "fact_invoice", "CREDIT_FOR_INV_NO", "INV_AMT")]
        [InlineData("order-replaces-order", "fact_order", "REPLACES_ORDER_ID", "AMT")]
        public void A_fact_with_a_key_to_itself_gets_its_starter_measures_and_a_note_on_the_self_link(string shape, string fact, string selfKey, string amount)
        {
            var spec = LocalEngine.FabricSchemaToSpec(Shape(shape), "x", "DW", "import");

            Assert.Contains(spec.Measures, m => m.Table == fact && m.Dax.Contains(amount));
            Assert.DoesNotContain(spec.Measures, m => m.Table == fact && m.Dax.Contains(selfKey));
            var self = Rel(spec, fact, selfKey);
            Assert.False(self.IsActive);
            Assert.Contains("itself", self.Note);
            Assert.Contains(selfKey, self.Note);
            Assert.DoesNotContain("PATH", self.Note);
        }

        /// <summary>A table whose only key is to itself (an employee list with a manager key and nothing else) names no
        /// other table and nothing names it: it is not a fact and gets no starter measures. A guard for the R2-H1 fix,
        /// which leaves a self key out of the role rule on both sides.</summary>
        [Fact]
        public void A_table_whose_only_key_is_to_itself_is_not_a_fact()
        {
            var schema = new FabricSchema
            {
                Tables = new[] { T("dim_employee", new[] { "EMP_ID" }, C("EMP_ID", "Int64"), C("MGR_ID", "Int64"), C("SALARY", "Decimal")) },
                ForeignKeys = new[] { Fk("dim_employee", "MGR_ID", "dim_employee", "EMP_ID") },
            };
            var spec = LocalEngine.FabricSchemaToSpec(schema, "x", "DW", "import");

            Assert.NotEqual("fact", spec.Tables.Single().Role);
            Assert.Empty(spec.Measures);
            Assert.False(Rel(spec, "dim_employee", "MGR_ID").IsActive);
        }

        /// <summary>R2-L1: the endpoint's key query has no ORDER BY, so the same warehouse can list its keys in any order.
        /// The set of links drafted on must be the same whatever that order is: reversed, and five seeded shuffles.</summary>
        [Theory]
        [MemberData(nameof(Shapes))]
        public void The_draft_does_not_depend_on_the_order_the_source_lists_its_keys(string shape)
        {
            var expected = ActiveLinks(LocalEngine.FabricSchemaToSpec(Shape(shape), "x", "DW", "import"));
            var orders = new System.Collections.Generic.List<FabricForeignKey[]> { Enumerable.Reverse(Shape(shape).ForeignKeys).ToArray() };
            for (var seed = 1; seed <= 5; seed++)
            {
                var rng = new Random(seed);
                orders.Add(Shape(shape).ForeignKeys.OrderBy(_ => rng.Next()).ToArray());
            }
            foreach (var keys in orders)
            {
                var schema = Shape(shape);
                schema.ForeignKeys = keys;
                Assert.Equal(expected, ActiveLinks(LocalEngine.FabricSchemaToSpec(schema, "x", "DW", "import")));
            }
        }

        /// <summary>R2-L1: of a dimension's two keys to one table, the one the source lists first among its columns stays
        /// on, as rule 2 does for a fact, whichever key the endpoint lists first.</summary>
        [Fact]
        public void Of_a_dimensions_two_keys_to_one_table_the_first_column_stays_on()
        {
            var spec = LocalEngine.FabricSchemaToSpec(Shape("dimension-with-two-dates"), "x", "DW", "import");

            Assert.NotEqual(false, Rel(spec, "dim_staff", "HIRE_DT_KEY").IsActive);
            Assert.False(Rel(spec, "dim_staff", "TERM_DT_KEY").IsActive);
            Assert.Contains("HIRE_DT_KEY", Rel(spec, "dim_staff", "TERM_DT_KEY").Note);
        }

        /// <summary>R2-M1, the other half: a model with an ordinary table called Date that has no date column (a date
        /// stored as a number) and is not marked. Autogenerate from model names it as the date table, and Build into
        /// model failed with "Add a date column first". It is left alone with a note instead, as the lane's base did
        /// (without the note).</summary>
        [Fact]
        public async Task Rebuilding_a_model_whose_unmarked_Date_table_has_no_date_column_reports_no_error()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("M", 1604);
            var d = await engine.CreateTableAsync("Date", "human");
            await engine.CreateColumnAsync(d, "DateKey", "Int64", "DateKey", "human");
            await engine.AutogenerateSpecFromModelAsync("human");

            var report = await engine.BuildModelFromSpecAsync("human");

            Assert.Empty(report.Errors);
            Assert.Contains(report.Notes, n => n.StartsWith("Date was not marked as the date table, because it has no date column"));
            Assert.NotEqual("Time", await sessions.Current.ReadAsync(m => m.Tables["Date"].DataCategory));
        }

        /// <summary>R2-M1: the unmarked calculated Date table is left alone too, and the note says why and what to do.</summary>
        [Fact]
        public async Task Rebuilding_a_model_with_an_unmarked_calculated_Date_table_says_why_it_was_not_marked()
        {
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("M", 1604);
            await engine.CreateCalculatedTableAsync("Date", "CALENDARAUTO()", "human");
            await engine.AutogenerateSpecFromModelAsync("human");

            var report = await engine.BuildModelFromSpecAsync("human");

            Assert.Empty(report.Errors);
            Assert.Contains(report.Notes, n => n.StartsWith("Date was not marked as the date table") && n.Contains("Mark as Date Table"));
        }

        /// <summary>R2-M2: asks that say a MODEL comes from SQL still reach the Model Spec after its words stopped
        /// owning "from the sql" on its own.</summary>
        [Theory]
        [InlineData("make a model from the sql endpoint")]
        [InlineData("create a semantic model from my sql database")]
        [InlineData("start a new model from sql")]
        public void Asks_for_a_model_from_sql_still_route_to_the_model_spec(string ask)
        {
            Assert.Equal("spec", AskRoutePoint.MatchWords(ask)?.Tool);
        }

        // ---- Verifier round 3 (at e921fd3d): fix round 4 ------------------------------------------------------------

        private static FabricColumn Col(string name, string type) => new FabricColumn { Name = name, DataType = type };

        /// <summary>N1 in round 4 said a link from a dimension still counts toward the date table. Round 6 (F1) reverses that:
        /// filtering by a date table only a dimension points at would not filter the fact, so it is never the one marked.
        /// Here nothing a fact links to is a date table, so none is marked and the note says so.</summary>
        [Fact]
        public void A_date_table_only_a_dimension_points_at_is_not_marked()
        {
            var schema = new FabricSchema
            {
                Tables = new[]
                {
                    new FabricTable { Schema = "dbo", Name = "dim_calendar", KeyColumns = new[] { "DAY_KEY" },
                        Columns = new[] { Col("DAY_KEY", "Int64"), Col("DAY_DT", "DateTime"), Col("PREV_DAY_KEY", "Int64") } },
                    new FabricTable { Schema = "dbo", Name = "dim_date", KeyColumns = new[] { "DATE_KEY" },
                        Columns = new[] { Col("DATE_KEY", "Int64"), Col("CAL_DT", "DateTime") } },
                    new FabricTable { Schema = "dbo", Name = "dim_store", KeyColumns = new[] { "STORE_KEY" },
                        Columns = new[] { Col("STORE_KEY", "Int64"), Col("OPEN_KEY", "Int64") } },
                    new FabricTable { Schema = "dbo", Name = "fact_sales", KeyColumns = new[] { "SALE_ID" },
                        Columns = new[] { Col("SALE_ID", "Int64"), Col("STORE_KEY", "Int64"), Col("AMT", "Decimal") } },
                },
                ForeignKeys = new[]
                {
                    new FabricForeignKey { FromSchema = "dbo", FromTable = "fact_sales", FromColumn = "STORE_KEY", ToSchema = "dbo", ToTable = "dim_store", ToColumn = "STORE_KEY" },
                    new FabricForeignKey { FromSchema = "dbo", FromTable = "dim_store", FromColumn = "OPEN_KEY", ToSchema = "dbo", ToTable = "dim_date", ToColumn = "DATE_KEY" },
                    new FabricForeignKey { FromSchema = "dbo", FromTable = "dim_calendar", FromColumn = "PREV_DAY_KEY", ToSchema = "dbo", ToTable = "dim_calendar", ToColumn = "DAY_KEY" },
                },
            };

            var spec = LocalEngine.FabricSchemaToSpec(schema, "x", "DW", "import");

            Assert.True(string.IsNullOrEmpty(spec.DateTable.Name));
            Assert.False(spec.DateTable.MarkAsDate);
            Assert.Contains("No fact table links to a date table", spec.DateTable.Note);
            Assert.Empty(spec.TimeIntelligence);
        }

        /// <summary>R2-M2, both sides at once: the SQL words are the Model Spec's only with a making word beside them.</summary>
        [Theory]
        [InlineData("create a model from an SQL database")]
        [InlineData("set up a model from our sql warehouse")]
        [InlineData("generate the model from the fabric sql endpoint")]
        [InlineData("start from my sql database")]
        [InlineData("draft a semantic model from sql server")]
        [InlineData("build a new model using my sql database")]
        public void A_making_word_with_a_model_from_sql_is_the_model_spec(string ask)
        {
            Assert.Equal("spec", AskRoutePoint.MatchWords(ask)?.Tool);
            Assert.Equal("spec", PlanTargets.JumpFor(ask)?.Tool);
        }

        [Theory]
        [InlineData("refresh the model from the sql endpoint")]
        [InlineData("update my model from the sql database")]
        [InlineData("reload the model from my sql database")]
        [InlineData("reconcile the model from the sql database")]
        [InlineData("what changed in the model from the sql source")]
        public void A_model_from_sql_with_no_making_word_is_not_the_model_spec(string ask)
        {
            Assert.NotEqual("spec", AskRoutePoint.MatchWords(ask)?.Tool);
            Assert.NotEqual("spec", PlanTargets.JumpFor(ask)?.Tool);
        }

        /// <summary>A check of a model from SQL goes where it went before the SQL words existed: Validation.</summary>
        [Theory]
        [InlineData("check the model from the sql source")]
        [InlineData("test the model from the sql endpoint")]
        public void A_check_of_a_model_from_sql_goes_to_validation(string ask)
        {
            Assert.Equal("tests", AskRoutePoint.MatchWords(ask)?.Tool);
        }

        [Fact]
        public void The_making_words_are_the_ones_round_7_named()
        {
            Assert.Equal(new[] { "build", "create", "make", "generate", "draft", "start", "begin", "set up", "spin up", "put together", "scaffold", "produce", "design" },
                AskRouteCatalog.MakingWords);
        }

        // ---- Round 5 --------------------------------------------------------------------------------------------

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        /// <summary>The one list of asks both doors are run through. The ask bar's isPlaceAsk reads the same file
        /// (Semanticus.VSCode/test/sql-start.test.mjs), so "the same rule in two places" is proved by the asks and not by
        /// comparing word lists.</summary>
        public static IEnumerable<object[]> ModelFromSqlAsks()
        {
            var path = Path.Combine(RepoRoot(), "Semanticus.VSCode", "test", "model-from-sql-asks.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var a in doc.RootElement.GetProperty("asks").EnumerateArray())
                yield return new object[] { a.GetProperty("ask").GetString(), a.GetProperty("place").GetBoolean() };
        }

        /// <summary>R2-M2, L1 and L2 of the round 4 verdict: a making word counts for the SQL words only when it governs
        /// them directly ("create / make / build ... [me] [a, the, my, new, power bi, semantic, starter] model from ... sql"),
        /// on the engine's door. The bar's half of this list is in test/sql-start.test.mjs.</summary>
        [Theory]
        [MemberData(nameof(ModelFromSqlAsks))]
        public void One_list_of_asks_routes_to_the_model_spec_exactly_when_a_making_word_governs_the_sql(string ask, bool place)
        {
            var tool = AskRoutePoint.MatchWords(ask)?.Tool;
            var jump = PlanTargets.JumpFor(ask)?.Tool;
            if (place)
            {
                Assert.Equal("spec", tool);
                Assert.Equal("spec", jump);
            }
            else
            {
                Assert.NotEqual("spec", tool);
                Assert.NotEqual("spec", jump);
            }
        }

        public static IEnumerable<object[]> ModelFromSqlIntents()
        {
            var path = Path.Combine(RepoRoot(), "Semanticus.VSCode", "test", "model-from-sql-asks.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var a in doc.RootElement.GetProperty("asks").EnumerateArray())
                yield return new object[] { a.GetProperty("ask").GetString(), a.GetProperty("intent").GetString() };
        }

        /// <summary>Round 7: the same list read by intent. AskMakeIntent.Classify says yes, no or unsure for every ask, and the ask bar's
        /// classifyMakeIntent says the same (test/sql-start.test.mjs reads the same file), so the two doors agree ask by ask.</summary>
        [Theory]
        [MemberData(nameof(ModelFromSqlIntents))]
        public void One_list_of_asks_is_read_the_same_by_intent(string ask, string intent)
        {
            Assert.Equal(intent, AskMakeIntent.Classify(ask).ToString().ToLowerInvariant());
        }

        /// <summary>L2: a clear request for the wizard, with words between "model from" and "sql", found no place in the ask box.</summary>
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_ask_box_answers_a_model_from_the_tables_in_my_sql_database_with_the_model_spec()
        {
            using var f = new DecisionFixture();
            await f.BuildModelAsync("AskRouteSqlR5.bim");
            f.UseNull();

            var result = await f.Engine.RouteAskAsync("build a model from the tables in my sql database");

            Assert.Equal("spec", result.Tool);
        }

        /// <summary>L3: MakingWords carries its own summary and CreateVerbs carries its own; neither has the other's.</summary>
        [Fact]
        public void Each_word_list_in_the_catalogue_has_its_own_summary()
        {
            var src = File.ReadAllText(Path.Combine(RepoRoot(), "Semanticus.Engine", "Plan", "AskRouteCatalog.cs"));
            var making = src.IndexOf("public static readonly IReadOnlyList<string> MakingWords", StringComparison.Ordinal);
            var create = src.IndexOf("public static readonly IReadOnlyList<string> CreateVerbs", StringComparison.Ordinal);
            Assert.True(making > 0 && create > 0);
            static string Before(string s, int at)
            {
                var start = s.LastIndexOf("public static readonly IReadOnlyList<string>", Math.Max(0, at - 1), StringComparison.Ordinal);
                var from = start < 0 ? Math.Max(0, at - 1200) : s.IndexOf(';', start);
                return s.Substring(from, at - from);
            }
            var beforeCreate = Before(src, create);
            Assert.Contains("I want to make one of these", beforeCreate);
            Assert.DoesNotContain("I want a new model", beforeCreate);
            Assert.DoesNotContain("<summary>The verbs that mean \"I want to make one of these", Before(src, making));
        }

        // ---- N2: the date table is the one the facts name --------------------------------------------------------

        /// <summary>Round 4 ranked a date table a fact reaches THROUGH a dimension. Round 6 (F1): only a fact's own active,
        /// direct link counts, so with both date tables reached through dimensions none is marked and the note names both.</summary>
        [Fact]
        public void A_date_table_a_fact_reaches_only_through_a_dimension_is_not_marked()
        {
            var schema = new FabricSchema
            {
                Tables = new[]
                {
                    T("dim_calendar", new[] { "DAY_KEY" }, C("DAY_KEY", "Int64"), C("DAY_DT", "DateTime")),
                    T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                    T("dim_promo", new[] { "PROMO_KEY" }, C("PROMO_KEY", "Int64"), C("START_KEY", "Int64"), C("END_KEY", "Int64")),
                    T("dim_store", new[] { "STORE_KEY" }, C("STORE_KEY", "Int64"), C("OPEN_KEY", "Int64")),
                    T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("STORE_KEY", "Int64"), C("AMT", "Decimal")),
                    T("fact_returns", new[] { "RET_ID" }, C("RET_ID", "Int64"), C("PROMO_KEY", "Int64"), C("AMT", "Decimal")),
                },
                ForeignKeys = new[]
                {
                    Fk("fact_sales", "STORE_KEY", "dim_store", "STORE_KEY"),
                    Fk("dim_store", "OPEN_KEY", "dim_date", "DATE_KEY"),
                    Fk("fact_returns", "PROMO_KEY", "dim_promo", "PROMO_KEY"),
                    Fk("dim_promo", "START_KEY", "dim_calendar", "DAY_KEY"),
                    Fk("dim_promo", "END_KEY", "dim_calendar", "DAY_KEY"),
                },
            };

            var spec = LocalEngine.FabricSchemaToSpec(schema, "x", "DW", "import");

            Assert.True(string.IsNullOrEmpty(spec.DateTable.Name));
            Assert.Contains("dim_calendar and dim_date look like date tables", spec.DateTable.Note);
        }

        private static FabricSchema KeysButNoneToTheDate() => new FabricSchema
        {
            Tables = new[]
            {
                T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                T("dim_store", new[] { "STORE_KEY" }, C("STORE_KEY", "Int64"), C("STORE_NM", "String")),
                T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("STORE_KEY", "Int64"), C("SALE_DT", "DateTime"), C("AMT", "Decimal")),
            },
            ForeignKeys = new[] { Fk("fact_sales", "STORE_KEY", "dim_store", "STORE_KEY") },
        };

        /// <summary>Coordinator ruling on N2: when the source declares its keys and no fact names any date table, none is
        /// marked (a date table nothing filters by would make every time-intelligence number silently wrong), the draft says
        /// so in its own note, and no time-intelligence measures are drafted over it.</summary>
        [Fact]
        public void When_the_source_has_keys_and_no_fact_names_a_date_table_none_is_marked_and_the_draft_says_so()
        {
            var spec = LocalEngine.FabricSchemaToSpec(KeysButNoneToTheDate(), "x", "DW", "import");

            Assert.NotNull(spec.DateTable);
            Assert.True(string.IsNullOrEmpty(spec.DateTable.Name));
            Assert.False(spec.DateTable.MarkAsDate);
            Assert.Contains("dim_date", spec.DateTable.Note);
            Assert.Contains("Link a fact to one", spec.DateTable.Note);
            Assert.DoesNotContain("Plan", spec.DateTable.Note);
            Assert.DoesNotContain(spec.Tables, t => t.Role == "date");
            Assert.Empty(spec.TimeIntelligence);
            Assert.Empty(spec.TimeIntelligenceBaseMeasures);
        }

        [Fact]
        public async Task A_draft_with_no_date_table_builds_cleanly_and_the_build_repeats_the_note()
        {
            var spec = LocalEngine.FabricSchemaToSpec(KeysButNoneToTheDate(), "x", "DW", "import");
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("NoDate", 1604);
            await engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");

            var report = await engine.BuildModelFromSpecAsync("human");

            Assert.Empty(report.Errors);
            Assert.Contains(report.Notes, n => n.Contains("dim_date") && n.Contains("Link a fact to one"));
            var graph = await engine.GetModelGraphAsync();
            Assert.DoesNotContain(graph.Tables, t => t.IsDateTable);
            Assert.DoesNotContain(graph.Tables, t => t.Name == "Date" || t.Name == "Calendar");
        }

        // ---- L6, L7: wording ---------------------------------------------------------------------------------------

        [Fact]
        public async Task A_date_table_marked_on_a_rebuild_does_not_say_the_source_has_its_own()
        {
            var schema = new FabricSchema
            {
                Tables = new[]
                {
                    T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                    T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("DATE_KEY", "Int64"), C("AMT", "Decimal")),
                },
                ForeignKeys = new[] { Fk("fact_sales", "DATE_KEY", "dim_date", "DATE_KEY") },
            };
            var spec = LocalEngine.FabricSchemaToSpec(schema, "x", "DW", "import");
            using var sessions = new SessionManager();
            using var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Marked", 1604);
            await engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");

            var report = await engine.BuildModelFromSpecAsync("human");

            Assert.Empty(report.Errors);
            Assert.Contains(report.Notes, n => n.Contains("dim_date is marked as the date table"));
            Assert.DoesNotContain(report.Notes, n => n.Contains("the source has its own"));
        }

        // ---- Round 6 (verifier round 5, F1 to F4): the date table follows the fact's own link --------------------------

        private static FabricSchema S6(FabricForeignKey[] keys, params FabricTable[] tables) =>
            new FabricSchema { Tables = tables.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray(), ForeignKeys = keys };

        private static string[] TiTables(ModelSpec spec) => (spec.TimeIntelligenceBaseMeasures ?? Array.Empty<string>())
            .Select(b => spec.Measures.First(m => m.Name == b).Table).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();

        private static ModelSpec Draft6(FabricSchema s) => LocalEngine.FabricSchemaToSpec(s, "x", "DW", "import");

        private static FabricSchema OrderHeaderAndLines() => S6(new[]
        {
            Fk("order_line", "ORDER_ID", "order_header", "ORDER_ID"),
            Fk("order_header", "ORDER_DT_KEY", "dim_date", "DATE_KEY"),
        },
            T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
            T("order_header", new[] { "ORDER_ID" }, C("ORDER_ID", "Int64"), C("ORDER_DT_KEY", "Int64"), C("ORDER_TOTAL", "Decimal")),
            T("order_line", new[] { "LINE_ID" }, C("LINE_ID", "Int64"), C("ORDER_ID", "Int64"), C("QTY", "Int64")));

        /// <summary>Ruling 1: a header the lines name, and the header names the date table. The date table is marked (a fact,
        /// the header, reaches it by its own link), but the lines have no link of their own to it, so they get no
        /// time-intelligence measures, and the note says which facts did not and why.</summary>
        [Fact]
        public void A_line_that_reaches_the_date_table_only_through_its_header_gets_no_time_intelligence()
        {
            var spec = Draft6(OrderHeaderAndLines());

            Assert.Equal("dim_date", spec.DateTable.Name);
            Assert.Empty(spec.TimeIntelligenceBaseMeasures);
            Assert.Empty(spec.TimeIntelligence);
            Assert.Contains("order_line", spec.DateTable.Note);
            Assert.Contains("no year-to-date", spec.DateTable.Note, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The same draft built: the build repeats the note beside the "is marked" line, with 0 errors.</summary>
        [Fact]
        public async Task A_build_that_left_facts_out_of_time_intelligence_repeats_why()
        {
            using var f = new DecisionFixture();
            await f.Engine.CreateModelAsync("R6Partial", 1604);
            await f.Engine.SetSpecAsync(JsonSerializer.Serialize(Draft6(OrderHeaderAndLines()), Camel), "human");

            var report = await f.Engine.BuildModelFromSpecAsync("human");

            Assert.Empty(report.Errors);
            Assert.Contains(report.Notes, n => n.Contains("dim_date is marked as the date table"));
            Assert.Contains(report.Notes, n => n.Contains("order_line") && n.Contains("year-to-date", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Two facts that name the same date table each get time-intelligence measures, and there is nothing to
        /// explain, so the draft says nothing about it.</summary>
        [Fact]
        public void Two_facts_that_name_the_same_date_table_both_get_time_intelligence()
        {
            var spec = Draft6(S6(new[]
            {
                Fk("fact_budget", "DAY_KEY", "dim_date", "DATE_KEY"),
                Fk("fact_sales", "DAY_KEY", "dim_date", "DATE_KEY"),
            },
                T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                T("fact_budget", new[] { "BUD_ID" }, C("BUD_ID", "Int64"), C("DAY_KEY", "Int64"), C("BUDGET_AMT", "Decimal")),
                T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("DAY_KEY", "Int64"), C("AMT", "Decimal"))));

            Assert.Equal("dim_date", spec.DateTable.Name);
            Assert.Equal(new[] { "fact_budget", "fact_sales" }, TiTables(spec));
            Assert.True(string.IsNullOrEmpty(spec.DateTable.Note), spec.DateTable.Note);
        }

        /// <summary>Most facts win: two facts name dim_date and one names dim_calendar, so dim_date is marked, the two get
        /// time intelligence, and the note names the third and the reason.</summary>
        [Fact]
        public void The_date_table_most_facts_reach_is_marked_and_the_rest_are_named_in_the_note()
        {
            var spec = Draft6(S6(new[]
            {
                Fk("fact_budget", "DAY_KEY", "dim_calendar", "DAY_KEY"),
                Fk("fact_returns", "DAY_KEY", "dim_date", "DATE_KEY"),
                Fk("fact_sales", "DAY_KEY", "dim_date", "DATE_KEY"),
            },
                T("dim_calendar", new[] { "DAY_KEY" }, C("DAY_KEY", "Int64"), C("DAY_DT", "DateTime")),
                T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                T("fact_budget", new[] { "BUD_ID" }, C("BUD_ID", "Int64"), C("DAY_KEY", "Int64"), C("BUDGET_AMT", "Decimal")),
                T("fact_returns", new[] { "RET_ID" }, C("RET_ID", "Int64"), C("DAY_KEY", "Int64"), C("RET_AMT", "Decimal")),
                T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("DAY_KEY", "Int64"), C("AMT", "Decimal"))));

            Assert.Equal("dim_date", spec.DateTable.Name);
            Assert.Equal(new[] { "fact_returns", "fact_sales" }, TiTables(spec));
            Assert.Contains("fact_budget", spec.DateTable.Note);
            Assert.DoesNotContain("fact_sales", spec.DateTable.Note.Split("None were made")[^1]);
        }

        /// <summary>A bridge of keys only is not a fact for dates: its date keys never outrank the balances' one link, and it
        /// is not named in the note (it has no measures to leave out).</summary>
        [Fact]
        public void A_bridge_of_keys_only_is_not_a_fact_for_dates_and_is_not_named_in_the_note()
        {
            var spec = Draft6(S6(new[]
            {
                Fk("bridge_cust_acct", "CUST_KEY", "dim_customer", "CUST_KEY"),
                Fk("bridge_cust_acct", "ACCT_KEY", "dim_account", "ACCT_KEY"),
                Fk("bridge_cust_acct", "START_DAY_KEY", "dim_calendar", "DAY_KEY"),
                Fk("bridge_cust_acct", "END_DAY_KEY", "dim_calendar", "DAY_KEY"),
                Fk("fact_balance", "ACCT_KEY", "dim_account", "ACCT_KEY"),
                Fk("fact_balance", "BAL_DT_KEY", "dim_date", "DATE_KEY"),
            },
                T("bridge_cust_acct", new[] { "CUST_KEY", "ACCT_KEY" }, C("CUST_KEY", "Int64"), C("ACCT_KEY", "Int64"), C("START_DAY_KEY", "Int64"), C("END_DAY_KEY", "Int64")),
                T("dim_account", new[] { "ACCT_KEY" }, C("ACCT_KEY", "Int64"), C("ACCT_NM", "String")),
                T("dim_calendar", new[] { "DAY_KEY" }, C("DAY_KEY", "Int64"), C("DAY_DT", "DateTime")),
                T("dim_customer", new[] { "CUST_KEY" }, C("CUST_KEY", "Int64"), C("CUST_NM", "String")),
                T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                T("fact_balance", new[] { "BAL_ID" }, C("BAL_ID", "Int64"), C("ACCT_KEY", "Int64"), C("BAL_DT_KEY", "Int64"), C("BAL_AMT", "Decimal"))));

            Assert.Equal("dim_date", spec.DateTable.Name);
            Assert.Equal(new[] { "fact_balance" }, TiTables(spec));
            Assert.True(string.IsNullOrEmpty(spec.DateTable.Note), spec.DateTable.Note);
        }

        /// <summary>The added CALENDAR (a source with no date table at all) has nothing linked to it, so it gets no
        /// time-intelligence measures, and the note says so and what the person can do.</summary>
        [Fact]
        public void A_calendar_added_beside_a_source_with_no_date_table_gets_no_time_intelligence_and_says_so()
        {
            var spec = Draft6(S6(new[] { Fk("fact_sales", "STORE_KEY", "dim_store", "STORE_KEY") },
                T("dim_store", new[] { "STORE_KEY" }, C("STORE_KEY", "Int64"), C("STORE_NM", "String")),
                T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("STORE_KEY", "Int64"), C("AMT", "Decimal"))));

            Assert.Empty(spec.TimeIntelligence);
            Assert.Empty(spec.TimeIntelligenceBaseMeasures);
            Assert.False(string.IsNullOrWhiteSpace(spec.DateTable.Note));
            Assert.Contains("no year-to-date", spec.DateTable.Note, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Ruling 2: a note promises only what happens. No note the draft writes says the Plan offers anything
        /// (the Plan makes no such offer), and the no-date note tells the person what to do.</summary>
        [Fact]
        public void No_note_the_draft_writes_promises_a_plan_offer()
        {
            var shapes = new[]
            {
                KeysButNoneToTheDate(), OrderHeaderAndLines(),
                S6(new[] { Fk("fact_sales", "STORE_KEY", "dim_store", "STORE_KEY") },
                    T("dim_calendar", new[] { "DAY_KEY" }, C("DAY_KEY", "Int64"), C("DAY_DT", "DateTime")),
                    T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                    T("dim_store", new[] { "STORE_KEY" }, C("STORE_KEY", "Int64")),
                    T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("STORE_KEY", "Int64"), C("AMT", "Decimal"))),
            };
            foreach (var s in shapes)
            {
                var spec = Draft6(s);
                var note = spec.DateTable.Note ?? "";
                Assert.DoesNotContain("Plan", note);
                Assert.DoesNotContain("offers", note, StringComparison.OrdinalIgnoreCase);
            }
            var none = Draft6(shapes[0]).DateTable.Note;
            Assert.Contains("No fact table links to a date table", none);
            Assert.Contains("mark it as the date table", none);
        }

        /// <summary>Two candidates read in the plural: "dim_calendar and dim_date look like date tables".</summary>
        [Fact]
        public void The_no_date_note_reads_right_with_two_candidates()
        {
            var spec = Draft6(S6(new[] { Fk("fact_sales", "STORE_KEY", "dim_store", "STORE_KEY") },
                T("dim_calendar", new[] { "DAY_KEY" }, C("DAY_KEY", "Int64"), C("DAY_DT", "DateTime")),
                T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                T("dim_store", new[] { "STORE_KEY" }, C("STORE_KEY", "Int64")),
                T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("STORE_KEY", "Int64"), C("AMT", "Decimal"))));

            Assert.Contains("dim_calendar and dim_date look like date tables", spec.DateTable.Note);
            Assert.DoesNotContain("looks like one", spec.DateTable.Note);
        }
    }
}
