using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>The structure manifest of a spec-first start (M11, lane sf-manifest): the one pure function from a source's
    /// schema, a person's overrides, the picks made for base measures and an identity seed to the spec the build reads.
    /// No connection is involved: the schema is written as the INFORMATION_SCHEMA read gives it.
    ///
    /// Two promises. (1) The DEFAULT manifest gives, byte for byte, the spec the two-step draft gave before it called the
    /// manifest (the golden is recorded from the old code, StructureManifestGolden.cs). (2) Everything the manifest adds
    /// is only added when asked: a key of several columns builds no link, a pick of none removes a measure, time
    /// intelligence follows additive picks with their own active date link, a fiscal year end reaches the year-to-date
    /// measure, and a seed makes every identity repeat.</summary>
    public sealed class StructureManifestTests
    {
        // The same serialisation the engine keeps a spec in (LocalEngine.SpecJson): indented, nulls left out.
        private static readonly JsonSerializerOptions Spec = new()
        {
            PropertyNameCaseInsensitive = true, WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private static string Json(ModelSpec spec) => JsonSerializer.Serialize(spec, Spec).Replace("\r\n", "\n");
        private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        private static FabricColumn C(string name, string type) => new FabricColumn { Name = name, DataType = type };
        private static FabricTable T(string name, string[] keys, params FabricColumn[] cols) =>
            new FabricTable { Schema = "dbo", Name = name, KeyColumns = keys, Columns = cols };
        private static FabricForeignKey Fk(string from, string fromCol, string to, string toCol, string constraint = null, int ordinal = 0) => new FabricForeignKey
        { FromSchema = "dbo", FromTable = from, FromColumn = fromCol, ToSchema = "dbo", ToTable = to, ToColumn = toCol, ConstraintName = constraint, Ordinal = ordinal };

        private static StructureManifest Derive(FabricSchema s, ManifestOverrides o = null, ManifestDecision[] d = null, string seed = null, string storage = "import") =>
            StructureManifest.Derive(s, o, d, seed, "x", "DW", storage);

        // ---- the schemas ---------------------------------------------------------------------------------------------

        /// <summary>Sales with a unit price and a quantity, a product and a date, each linked by its own key.</summary>
        private static FabricSchema Sales(params FabricForeignKey[] extra) => new FabricSchema
        {
            Tables = new[]
            {
                T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime"), C("FY_NO", "Int64")),
                T("dim_product", new[] { "PRODUCT_KEY" }, C("PRODUCT_KEY", "Int64"), C("PRODUCT_NM", "String")),
                T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("PRODUCT_KEY", "Int64"), C("DATE_KEY", "Int64"),
                    C("Qty", "Int64"), C("Unit Price", "Decimal"), C("Amount", "Decimal")),
            },
            ForeignKeys = new[] { Fk("fact_sales", "PRODUCT_KEY", "dim_product", "PRODUCT_KEY"), Fk("fact_sales", "DATE_KEY", "dim_date", "DATE_KEY") }.Concat(extra).ToArray(),
        };

        /// <summary>A sales fact that links to the date table and a returns fact that reaches it only through the store.</summary>
        private static FabricSchema TwoFacts() => new FabricSchema
        {
            Tables = new[]
            {
                T("dim_date", new[] { "DATE_KEY" }, C("DATE_KEY", "Int64"), C("CAL_DT", "DateTime")),
                T("dim_store", new[] { "STORE_KEY" }, C("STORE_KEY", "Int64"), C("STORE_NM", "String"), C("OPEN_DATE_KEY", "Int64")),
                T("fact_sales", new[] { "SALE_ID" }, C("SALE_ID", "Int64"), C("STORE_KEY", "Int64"), C("DATE_KEY", "Int64"), C("Qty", "Int64")),
                T("fact_returns", new[] { "RET_ID" }, C("RET_ID", "Int64"), C("STORE_KEY", "Int64"), C("Returned", "Int64")),
            },
            ForeignKeys = new[]
            {
                Fk("fact_sales", "STORE_KEY", "dim_store", "STORE_KEY"), Fk("fact_sales", "DATE_KEY", "dim_date", "DATE_KEY"),
                Fk("fact_returns", "STORE_KEY", "dim_store", "STORE_KEY"), Fk("dim_store", "OPEN_DATE_KEY", "dim_date", "DATE_KEY"),
            },
        };

        /// <summary>Sales whose product key is two columns: a tenant and a product inside it.</summary>
        private static FabricSchema Composite(int columns)
        {
            var parts = new[] { "TenantId", "RegionId", "ProductId" }.Skip(3 - columns).ToArray();
            return new FabricSchema
            {
                Tables = new[]
                {
                    T("Product", parts, parts.Select(p => C(p, "Int64")).Append(C("Name", "String")).ToArray()),
                    T("Sales", new[] { "SaleId" }, parts.Select(p => C(p, "Int64")).Prepend(C("SaleId", "Int64")).Append(C("Qty", "Int64")).ToArray()),
                },
                ForeignKeys = parts.Select((p, i) => Fk("Sales", p, "Product", p, "FK_Sales_Product", i + 1)).ToArray(),
            };
        }

        private static async Task<(SessionManager Sessions, LocalEngine Engine, SpecBuildReport Report)> BuildAsync(ModelSpec spec)
        {
            var sessions = new SessionManager();
            var engine = new LocalEngine(sessions, TestEntitlements.Pro);
            await engine.CreateModelAsync("Start", 1604);
            await engine.SetSpecAsync(JsonSerializer.Serialize(spec, Camel), "human");
            var report = await engine.BuildModelFromSpecAsync("human");
            return (sessions, engine, report);
        }

        // ============================================================================================================
        // The finish line: the default manifest is today's draft, byte for byte
        // ============================================================================================================

        [Fact]
        public void The_default_manifest_gives_the_J8_import_spec_byte_for_byte()
        {
            var json = Json(Derive(SqlDraftBuildTests.J8Schema()).ToSpec());
            Assert.Equal(StructureManifestGolden.J8Import.Replace("\r\n", "\n"), json);   // the golden is a raw string literal: it carries the checkout's line ends (CRLF on a Windows checkout)
        }

        [Fact]
        public void The_two_step_draft_and_the_default_manifest_are_one_function()
        {
            Assert.Equal(Json(LocalEngine.FabricSchemaToSpec(SqlDraftBuildTests.J8Schema(), "x", "DW", "import")),
                         Json(Derive(SqlDraftBuildTests.J8Schema()).ToSpec()));
        }

        [Fact]
        public void The_default_manifest_gives_the_J8_Direct_Lake_spec_byte_for_byte()
        {
            Assert.Equal(StructureManifestGolden.J8DirectLakeSha256, Sha(Json(Derive(SqlDraftBuildTests.J8Schema(), storage: "directLake").ToSpec())));
        }

        [Fact]
        public void Every_recorded_shape_gives_its_recorded_bytes()
        {
            Assert.All(StructureManifestGolden.Hashes, h =>
                Assert.True(h.Sha256 == Sha(Json(Derive(SqlDraftBuildTests.Shape(h.Shape)).ToSpec())), $"{h.Shape}: the default manifest no longer gives the spec the draft gave"));
        }

        /// <summary>A seed only ADDS identities. With them taken out the spec is the recorded one, so a start plan and the
        /// two-step draft cannot drift apart on anything but names the model would have made up anyway.</summary>
        [Fact]
        public void A_seed_adds_identities_and_nothing_else()
        {
            var node = JsonNode.Parse(JsonSerializer.Serialize(Derive(SqlDraftBuildTests.J8Schema(), seed: "seed-1").ToSpec(), Spec));
            var stripped = Strip(node);
            Assert.Equal(StructureManifestGolden.J8Import.Replace("\r\n", "\n"), stripped.ToJsonString(Spec).Replace("\r\n", "\n"));

            static JsonNode Strip(JsonNode n)
            {
                switch (n)
                {
                    case JsonObject o:
                        o.Remove("LineageTag");
                        foreach (var kv in o.ToArray()) { if (kv.Key == "Relationships" && kv.Value is JsonArray rels) foreach (var r in rels) ((JsonObject)r).Remove("Name"); else Strip(kv.Value); }
                        return o;
                    case JsonArray a: foreach (var x in a) Strip(x); return a;
                    default: return n;
                }
            }
        }

        // ============================================================================================================
        // Fails first (L3): a key of several columns
        // ============================================================================================================

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public async Task A_key_of_several_columns_builds_no_link_and_gives_one_unresolved_line(int columns)
        {
            var m = Derive(Composite(columns));
            var link = Assert.Single(m.Links);
            Assert.True(link.Unresolved);
            Assert.False(string.IsNullOrEmpty(link.Note));
            Assert.Equal(columns, link.FromColumns.Length);
            Assert.Equal(link.FromColumns, link.ToColumns);
            var need = Assert.Single(m.Needs);
            Assert.Equal("composite-key", need.Kind);
            // The line names the combined key it would need.
            foreach (var part in link.FromColumns) Assert.Contains(part, need.Text);
            Assert.Contains("one combined key", need.Text);

            var spec = m.ToSpec();
            Assert.Empty(spec.Relationships);
            Assert.Equal(new[] { link.Note }, spec.Unresolved);

            var (sessions, engine, report) = await BuildAsync(spec);
            using (sessions) using (engine)
            {
                Assert.Empty(report.Errors);
                Assert.Contains(link.Note, report.Notes);   // the two-step path says so too: nothing is dropped quietly
                Assert.Empty((await engine.GetModelGraphAsync()).Relationships);
            }
        }

        /// <summary>The key columns of a key that builds no link are still keys: no total of half a key, and the tables
        /// keep the roles the key gave them (the sales table is the many side).</summary>
        [Fact]
        public void The_columns_of_a_key_that_builds_no_link_are_still_keys()
        {
            var m = Derive(Composite(2));
            Assert.Equal("fact", m.Tables.Single(t => t.Name == "Sales").Role);
            Assert.Equal("dimension", m.Tables.Single(t => t.Name == "Product").Role);
            Assert.Equal(new[] { "Total Qty" }, m.BaseMeasures.Select(b => b.Name).ToArray());
        }

        /// <summary>A schema written without constraint names (every older caller) is read as it always was: each row is a
        /// key of one column.</summary>
        [Fact]
        public void Rows_with_no_constraint_name_stay_one_column_links()
        {
            var s = Composite(2);
            foreach (var f in s.ForeignKeys) { f.ConstraintName = null; f.Ordinal = 0; }
            var spec = Derive(s).ToSpec();
            Assert.Equal(2, spec.Relationships.Length);
        }

        [Fact]
        public void Foreign_keys_group_by_constraint_in_the_keys_own_order()
        {
            var s = new FabricSchema
            {
                ForeignKeys = new[]
                {
                    Fk("Sales", "ProductId", "Product", "ProductId", "FK_P", 2), Fk("Sales", "RegionId", "Region", "RegionId", "FK_R", 1),
                    Fk("Sales", "TenantId", "Product", "TenantId", "FK_P", 1),
                },
            };
            var groups = s.ForeignKeyGroups();
            Assert.Equal(2, groups.Length);
            Assert.Equal("FK_P", groups[0].ConstraintName);
            Assert.Equal(new[] { "TenantId", "ProductId" }, groups[0].FromColumns);
            Assert.Equal(new[] { "TenantId", "ProductId" }, groups[0].ToColumns);
            Assert.True(groups[0].IsComposite);
            Assert.False(groups[1].IsComposite);
        }

        /// <summary>A catalog join that lists one key's row twice must not turn a one-column key into a composite.</summary>
        [Fact]
        public void The_same_column_pair_listed_twice_is_still_a_one_column_key()
        {
            var s = new FabricSchema { ForeignKeys = new[] { Fk("Sales", "ProductId", "Product", "ProductId", "FK_P", 1), Fk("Sales", "ProductId", "Product", "ProductId", "FK_P", 1) } };
            var g = Assert.Single(s.ForeignKeyGroups());
            Assert.False(g.IsComposite);
        }

        /// <summary>Two keys with one name on two tables are two keys, not one key of the columns of both.</summary>
        [Fact]
        public void A_constraint_name_is_scoped_to_its_table()
        {
            var s = new FabricSchema { ForeignKeys = new[] { Fk("Sales", "ProductId", "Product", "ProductId", "FK_P", 1), Fk("Returns", "ProductId", "Product", "ProductId", "FK_P", 1) } };
            Assert.Equal(2, s.ForeignKeyGroups().Length);
        }

        [Fact]
        public void A_spec_with_no_unresolved_key_carries_no_unresolved_field()
        {
            Assert.Null(Derive(Sales()).ToSpec().Unresolved);
        }

        [Fact]
        public void The_schema_read_asks_for_each_keys_name_and_each_columns_place_in_it()
        {
            Assert.Contains("CONSTRAINT_NAME", FabricSqlSchema.ForeignKeySql);
            Assert.Contains("ORDINAL_POSITION", FabricSqlSchema.ForeignKeySql);
            Assert.Contains("ORDER BY", FabricSqlSchema.ForeignKeySql);
        }

        // ============================================================================================================
        // Fails first (L3): a base measure's pick
        // ============================================================================================================

        [Fact]
        public async Task Pick_average_on_Unit_Price_gives_an_Average_measure_and_no_time_intelligence()
        {
            // By default nobody totals a price: the rule picks none for it.
            Assert.DoesNotContain(Derive(Sales()).BaseMeasures, b => b.Column == "Unit Price");

            var m = Derive(Sales(), d: new[] { new ManifestDecision { Table = "fact_sales", Column = "Unit Price", Pick = "average", DecidedBy = "jev" } });
            var avg = m.BaseMeasures.Single(b => b.Column == "Unit Price");
            Assert.Equal("average", avg.Pick);
            Assert.Equal("Average Unit Price", avg.Name);
            Assert.Equal("jev", avg.DecidedBy);

            var spec = m.ToSpec();
            var measure = spec.Measures.Single(x => x.Name == "Average Unit Price");
            Assert.Equal("AVERAGE ( 'fact_sales'[Unit Price] )", measure.Dax);
            // The additive ones still get time intelligence; the average is never among them.
            Assert.DoesNotContain("Average Unit Price", spec.TimeIntelligenceBaseMeasures);
            Assert.Contains("Total Qty", spec.TimeIntelligenceBaseMeasures);

            var (sessions, engine, report) = await BuildAsync(spec);
            using (sessions) using (engine)
            {
                Assert.Empty(report.Errors);
                var names = await sessions.Current.ReadAsync(mm => mm.AllMeasures.Select(x => x.Name).ToArray());
                Assert.Contains("Average Unit Price", names);
                Assert.DoesNotContain(names, n => n.StartsWith("Average Unit Price ", StringComparison.Ordinal));
                Assert.Contains("Total Qty YTD", names);
            }
        }

        [Fact]
        public void Time_intelligence_is_never_made_over_a_non_additive_pick()
        {
            foreach (var pick in new[] { "average", "min", "max", "count", "distinctcount" })
            {
                // Drop the additive picks so the only base measure is the one under test.
                var m = Derive(Sales(), d: new[]
                {
                    new ManifestDecision { Table = "fact_sales", Column = "Qty", Pick = pick },
                    new ManifestDecision { Table = "fact_sales", Column = "Amount", Pick = "none" },
                });
                var spec = m.ToSpec();
                Assert.Single(spec.Measures);
                Assert.Empty(spec.TimeIntelligence);
                Assert.Empty(spec.TimeIntelligenceBaseMeasures);
            }
        }

        [Fact]
        public async Task Pick_none_removes_the_measure()
        {
            Assert.Contains(Derive(Sales()).ToSpec().Measures, x => x.Name == "Total Qty");

            var m = Derive(Sales(), d: new[] { new ManifestDecision { Table = "fact_sales", Column = "Qty", Pick = "none" } });
            Assert.DoesNotContain(m.BaseMeasures, b => b.Column == "Qty");
            var spec = m.ToSpec();
            Assert.DoesNotContain(spec.Measures, x => x.Name == "Total Qty");
            Assert.DoesNotContain("Total Qty", spec.TimeIntelligenceBaseMeasures);
            Assert.Contains("Total Amount", spec.TimeIntelligenceBaseMeasures);

            var (sessions, engine, report) = await BuildAsync(spec);
            using (sessions) using (engine)
            {
                Assert.Empty(report.Errors);
                var names = await sessions.Current.ReadAsync(mm => mm.AllMeasures.Select(x => x.Name).ToArray());
                Assert.DoesNotContain(names, n => n.StartsWith("Total Qty", StringComparison.Ordinal));
                Assert.Contains("Total Amount YTD", names);
            }
        }

        /// <summary>The column's own summarize-by setting is not the pick (design S4): taking a measure away leaves the
        /// column as the source drafted it.</summary>
        [Fact]
        public void A_pick_is_held_apart_from_the_columns_summarize_setting()
        {
            var spec = Derive(Sales(), d: new[] { new ManifestDecision { Table = "fact_sales", Column = "Qty", Pick = "none" } }).ToSpec();
            Assert.Equal("Sum", spec.Tables.Single(t => t.Name == "fact_sales").Columns.Single(c => c.Name == "Qty").SummarizeBy);
        }

        [Fact]
        public void A_pick_that_does_not_fit_the_column_is_left_out_and_asked_about()
        {
            var m = Derive(Sales(), d: new[] { new ManifestDecision { Table = "dim_product", Column = "PRODUCT_NM", Pick = "sum" } });
            Assert.DoesNotContain(m.BaseMeasures, b => b.Table == "dim_product");
            Assert.Contains(m.Needs, n => n.Kind == "pick-unusable" && n.Text.Contains("PRODUCT_NM"));

            var ok = Derive(Sales(), d: new[] { new ManifestDecision { Table = "dim_product", Column = "PRODUCT_NM", Pick = "distinctcount" } });
            Assert.Equal("Distinct Count of PRODUCT_NM", ok.BaseMeasures.Single(b => b.Table == "dim_product").Name);
        }

        // ============================================================================================================
        // Fails first (L3): time intelligence follows the fact's own active date link
        // ============================================================================================================

        [Fact]
        public async Task Pick_sum_gives_time_intelligence_only_with_the_facts_own_active_date_link()
        {
            var m = Derive(TwoFacts());
            Assert.Contains(m.BaseMeasures, b => b.Table == "fact_returns" && b.Pick == "sum");
            var ti = m.DeriveTimeIntelligence();
            Assert.Equal(new[] { "Total Qty" }, ti.BaseMeasures);
            Assert.Contains("fact_returns", ti.Note);
            Assert.Contains("no link of its own", ti.Note);

            var spec = m.ToSpec();
            Assert.Equal(new[] { "Total Qty" }, spec.TimeIntelligenceBaseMeasures);
            var (sessions, engine, report) = await BuildAsync(spec);
            using (sessions) using (engine)
            {
                Assert.Empty(report.Errors);
                var names = await sessions.Current.ReadAsync(mm => mm.AllMeasures.Select(x => x.Name).ToArray());
                Assert.Contains("Total Qty YTD", names);
                Assert.Contains("Total Returned", names);
                Assert.DoesNotContain(names, n => n.StartsWith("Total Returned ", StringComparison.Ordinal));
            }
        }

        [Fact]
        public void A_facts_date_link_that_is_off_gives_it_no_time_intelligence()
        {
            var off = new ManifestOverrides { Links = new[] { new ManifestLinkOverride { FromTable = "fact_sales", FromColumn = "DATE_KEY", Active = false } } };
            var m = Derive(TwoFacts(), off);
            Assert.False(m.Links.Single(l => l.FromTable == "fact_sales" && l.ToTable == "dim_date").Active);
            Assert.Empty(m.DeriveTimeIntelligence().BaseMeasures);
            Assert.Empty(m.ToSpec().TimeIntelligence);
        }

        /// <summary>A link turned on by hand is refused when it would give a table a second path: an override can never
        /// make a build that fails.</summary>
        [Fact]
        public void A_link_turned_on_that_would_make_a_second_path_stays_off_and_asks()
        {
            var on = new ManifestOverrides { Links = new[] { new ManifestLinkOverride { FromTable = "dim_store", FromColumn = "OPEN_DATE_KEY", Active = true } } };
            var m = Derive(TwoFacts(), on);
            Assert.False(m.Links.Single(l => l.FromTable == "dim_store").Active);
            Assert.Contains(m.Needs, n => n.Kind == "link-not-turned-on");
        }

        // ============================================================================================================
        // Fails first (L3): the fiscal year end
        // ============================================================================================================

        [Fact]
        public async Task FiscalYearEnd_6_30_gives_TOTALYTD_with_a_year_end()
        {
            var fiscal = new ManifestOverrides { DateTable = new ManifestDateOverride { FiscalYearEnd = "6/30" } };
            var spec = Derive(Sales(), fiscal).ToSpec();
            Assert.Equal("6/30", spec.DateTable.FiscalYearEnd);

            var (sessions, engine, report) = await BuildAsync(spec);
            using (sessions) using (engine)
            {
                Assert.Empty(report.Errors);
                var (ytd, qtd, py) = await sessions.Current.ReadAsync(mm => (mm.AllMeasures.Single(x => x.Name == "Total Qty YTD").Expression,
                    mm.AllMeasures.Single(x => x.Name == "Total Qty QTD").Expression, mm.AllMeasures.Single(x => x.Name == "Total Qty PY").Expression));
                Assert.Equal("TOTALYTD([Total Qty], 'dim_date'[CAL_DT], \"6/30\")", ytd);
                Assert.DoesNotContain("6/30", qtd);   // TOTALQTD takes no year end
                Assert.DoesNotContain("6/30", py);
            }
        }

        [Fact]
        public void A_calendar_year_end_adds_nothing_and_a_bad_one_asks()
        {
            Assert.Null(Derive(Sales(), new ManifestOverrides { DateTable = new ManifestDateOverride { FiscalYearEnd = "12/31" } }).ToSpec().DateTable.FiscalYearEnd);
            Assert.Equal("6/30", Derive(Sales(), new ManifestOverrides { DateTable = new ManifestDateOverride { FiscalYearEnd = "06-30" } }).DateTable.FiscalYearEnd);
            foreach (var bad in new[] { "13/45", "2/30", "June 30", "6/30\")+1", "2/29" })
            {
                var m = Derive(Sales(), new ManifestOverrides { DateTable = new ManifestDateOverride { FiscalYearEnd = bad } });
                Assert.Null(m.DateTable.FiscalYearEnd);
                Assert.Contains(m.Needs, n => n.Kind == "fiscal-year-end");
            }
        }

        /// <summary>The build refuses a year end it did not check itself: the manifest is not the only door a spec comes in by.</summary>
        [Fact]
        public async Task The_build_refuses_a_year_end_that_is_not_a_month_and_day()
        {
            var spec = Derive(Sales()).ToSpec();
            spec.DateTable.FiscalYearEnd = "6/30\") + 1 + (\"";
            var (sessions, engine, report) = await BuildAsync(spec);
            using (sessions) using (engine)
            {
                Assert.Contains(report.Errors, e => e.Contains("fiscal year end"));
                var names = await sessions.Current.ReadAsync(mm => mm.AllMeasures.Select(x => x.Name).ToArray());
                Assert.DoesNotContain("Total Qty YTD", names);
            }
        }

        // ============================================================================================================
        // Fails first (L3): the same seed gives the same identities
        // ============================================================================================================

        private static async Task<string> SnapshotAsync(SessionManager sessions) => await sessions.Current.ReadAsync(m =>
        {
            var sb = new StringBuilder();
            foreach (var t in m.Tables)
            {
                sb.AppendLine($"table {t.Name} {t.LineageTag}");
                foreach (var c in t.Columns) sb.AppendLine($"  column {c.Name} {((ILineageTagObject)c).LineageTag}");
                foreach (var x in t.Measures) sb.AppendLine($"  measure {x.Name} {x.LineageTag} {x.Expression}");
            }
            foreach (var r in m.Relationships) sb.AppendLine($"relationship {r.ID}");
            foreach (var e in m.Expressions) sb.AppendLine($"expression {e.Name} {e.LineageTag}");
            return sb.ToString();
        });

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task The_same_seed_twice_gives_identical_relationship_names_and_lineage_tags()
        {
            var one = Derive(SqlDraftBuildTests.J8Schema(), seed: "seed-1").ToSpec();
            var two = Derive(SqlDraftBuildTests.J8Schema(), seed: "seed-1").ToSpec();
            Assert.Equal(Json(one), Json(two));

            var (s1, e1, r1) = await BuildAsync(one);
            var (s2, e2, r2) = await BuildAsync(two);
            using (s1) using (e1) using (s2) using (e2)
            {
                Assert.Empty(r1.Errors); Assert.Empty(r2.Errors);
                var a = await SnapshotAsync(s1); var b = await SnapshotAsync(s2);
                Assert.Equal(a, b);
                // Not vacuous: the identities are there, every link has its own name, and every table, column and measure a tag.
                var (links, linkNames, tagless) = await s1.Current.ReadAsync(m => (m.Relationships.Count(), m.Relationships.Select(r => r.ID).Distinct().Count(),
                    m.Tables.Count(t => string.IsNullOrEmpty(t.LineageTag)) + m.Tables.SelectMany(t => t.Columns).Count(c => string.IsNullOrEmpty(((ILineageTagObject)c).LineageTag))
                        + m.AllMeasures.Count(x => string.IsNullOrEmpty(x.LineageTag))));
                Assert.True(links > 0); Assert.Equal(links, linkNames); Assert.Equal(0, tagless);
                // The whole database, byte for byte: nothing else the build makes differs between two builds of one plan.
                var j1 = await s1.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase));
                var j2 = await s2.Current.ReadAsync(m => Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeDatabase(m.Database.TOMDatabase));
                Assert.Equal(j1, j2);
            }
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task A_different_seed_gives_different_identities_and_no_seed_gives_the_models_own()
        {
            var (s1, e1, _) = await BuildAsync(Derive(Sales(), seed: "seed-1").ToSpec());
            var (s2, e2, _) = await BuildAsync(Derive(Sales(), seed: "seed-2").ToSpec());
            var (s3, e3, _) = await BuildAsync(Derive(Sales()).ToSpec());
            var (s4, e4, _) = await BuildAsync(Derive(Sales()).ToSpec());
            using (s1) using (e1) using (s2) using (e2) using (s3) using (e3) using (s4) using (e4)
            {
                Assert.NotEqual(await SnapshotAsync(s1), await SnapshotAsync(s2));
                Assert.NotEqual(await SnapshotAsync(s3), await SnapshotAsync(s4));   // today's behaviour: a fresh GUID each build
            }
        }

        /// <summary>An identity is worked out from the SOURCE name, so a rename never moves one and an exclusion never shifts another.</summary>
        [Fact]
        public void An_identity_follows_the_source_not_the_name_in_the_model()
        {
            var plain = Derive(Sales(), seed: "seed-1");
            var renamed = Derive(Sales(), new ManifestOverrides
            {
                Tables = new[] { new ManifestTableOverride { Table = "fact_sales", Name = "Sales Orders" } },
                Columns = new[] { new ManifestColumnOverride { Table = "fact_sales", Column = "Qty", Name = "Quantity" } },
            }, seed: "seed-1");
            Assert.Equal(plain.Tables.Single(t => t.SourceName == "fact_sales").Identity, renamed.Tables.Single(t => t.SourceName == "fact_sales").Identity);
            Assert.Equal(plain.Links.Select(l => l.Identity), renamed.Links.Select(l => l.Identity));
            Assert.Equal(plain.BaseMeasures.Single(b => b.Column == "Qty").Identity, renamed.BaseMeasures.Single(b => b.Column == "Quantity").Identity);
            Assert.Equal("Sales Orders", renamed.Links.First().FromTable);
            Assert.Equal("Total Quantity", renamed.BaseMeasures.Single(b => b.Column == "Quantity").Name);

            var without = Derive(Sales(), new ManifestOverrides { Tables = new[] { new ManifestTableOverride { Table = "dim_product", Include = false } } }, seed: "seed-1");
            Assert.Equal(plain.Links.Single(l => l.ToTable == "dim_date").Identity, without.Links.Single(l => l.ToTable == "dim_date").Identity);
        }

        [Fact]
        public void Every_identity_in_one_manifest_is_different_and_a_guid()
        {
            var m = Derive(SqlDraftBuildTests.J8Schema(), seed: "seed-1");
            var all = m.Tables.Select(t => t.Identity)
                .Concat(m.Tables.SelectMany(t => t.Columns).Select(c => c.Identity))
                .Concat(m.Links.Select(l => l.Identity)).Concat(m.BaseMeasures.Select(b => b.Identity)).Append(m.SourceIdentity).ToArray();
            Assert.All(all, id => Assert.True(Guid.TryParse(id, out _), id));
            Assert.Equal(all.Length, all.Distinct().Count());
        }

        // ============================================================================================================
        // What the verifier attacks: self links, snowflakes, exclusions
        // ============================================================================================================

        [Fact]
        public async Task A_link_to_itself_is_drafted_off_and_the_build_leaves_it_out_with_a_seed()
        {
            var s = new FabricSchema
            {
                Tables = new[]
                {
                    T("dim_employee", new[] { "EMP_ID" }, C("EMP_ID", "Int64"), C("MGR_ID", "Int64"), C("EMP_NM", "String")),
                    T("fact_hours", new[] { "ID" }, C("ID", "Int64"), C("EMP_ID", "Int64"), C("HRS", "Decimal")),
                },
                ForeignKeys = new[] { Fk("dim_employee", "MGR_ID", "dim_employee", "EMP_ID"), Fk("fact_hours", "EMP_ID", "dim_employee", "EMP_ID") },
            };
            var m = Derive(s, seed: "seed-1");
            var self = m.Links.Single(l => l.FromTable == l.ToTable);
            Assert.False(self.Active);
            Assert.Contains("itself", self.Note);
            Assert.Equal("fact", m.Tables.Single(t => t.Name == "fact_hours").Role);

            var (sessions, engine, report) = await BuildAsync(m.ToSpec());
            using (sessions) using (engine)
            {
                Assert.Empty(report.Errors);
                Assert.Single(await sessions.Current.ReadAsync(mm => mm.Relationships.Select(r => r.ID).ToArray()));
            }
        }

        /// <summary>A composite key from a table to itself builds no link and does not turn the table into a dimension.</summary>
        [Fact]
        public void A_composite_key_to_itself_is_one_unresolved_line()
        {
            var s = new FabricSchema
            {
                Tables = new[] { T("Part", new[] { "A", "B" }, C("A", "Int64"), C("B", "Int64"), C("ParentA", "Int64"), C("ParentB", "Int64"), C("Cost", "Decimal")) },
                ForeignKeys = new[] { Fk("Part", "ParentA", "Part", "A", "FK_Parent", 1), Fk("Part", "ParentB", "Part", "B", "FK_Parent", 2) },
            };
            var m = Derive(s);
            Assert.Single(m.Links);
            Assert.True(m.Links[0].Unresolved);
            Assert.Empty(m.ToSpec().Relationships);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Theory]
        [InlineData("diamond")]
        [InlineData("diamond-no-fact-key-to-geography")]
        [InlineData("galaxy")]
        public async Task A_snowflake_with_a_seed_builds_with_no_errors_and_fixed_names(string shape)
        {
            var m = Derive(SqlDraftBuildTests.Shape(shape), seed: "seed-1");
            var (sessions, engine, report) = await BuildAsync(m.ToSpec());
            using (sessions) using (engine)
            {
                Assert.Empty(report.Errors);
                var names = await sessions.Current.ReadAsync(mm => mm.Relationships.Select(r => r.ID).ToHashSet());
                Assert.Equal(m.Links.Select(l => l.Identity).ToHashSet(), names);
                // Each fact still has an active link to every table it names (the draft's ruling is unchanged by a seed).
                var active = await sessions.Current.ReadAsync(mm => mm.Relationships.OfType<SingleColumnRelationship>().Count(r => r.IsActive));
                Assert.Equal(m.Links.Count(l => l.Active), active);
            }
        }

        [Fact]
        public void A_table_left_out_takes_its_links_and_its_measures_with_it()
        {
            var m = Derive(Sales(), new ManifestOverrides { Tables = new[] { new ManifestTableOverride { Table = "dim_product", Include = false } } });
            Assert.DoesNotContain(m.Links, l => l.ToTable == "dim_product");
            var spec = m.ToSpec();
            Assert.DoesNotContain(spec.Tables, t => t.Name == "dim_product");
            Assert.DoesNotContain(spec.Relationships, r => r.ToTable == "dim_product");
            Assert.Contains(m.Tables, t => t.SourceName == "dim_product" && !t.Include);   // still listed, so it can be put back
        }

        [Fact]
        public void A_column_left_out_leaves_its_link_unbuilt_and_asks()
        {
            var m = Derive(Sales(), new ManifestOverrides { Columns = new[] { new ManifestColumnOverride { Table = "fact_sales", Column = "PRODUCT_KEY", Include = false } } });
            var link = m.Links.Single(l => l.ToTable == "dim_product");
            Assert.True(link.Unresolved);
            Assert.False(string.IsNullOrEmpty(link.Note));
            Assert.Contains(m.Needs, n => n.Kind == "link-left-out");
            var spec = m.ToSpec();
            Assert.DoesNotContain(spec.Tables.Single(t => t.Name == "fact_sales").Columns, c => c.Name == "PRODUCT_KEY");
            Assert.DoesNotContain(spec.Relationships, r => r.ToTable == "dim_product");
        }

        [Fact]
        public void The_date_table_can_be_named_or_declined()
        {
            var declined = Derive(Sales(), new ManifestOverrides { DateTable = new ManifestDateOverride { Name = "" } });
            Assert.Equal("", declined.ToSpec().DateTable.Name);
            Assert.Empty(declined.ToSpec().TimeIntelligence);
            Assert.NotNull(declined.ToSpec().DateTable.Note);

            var missing = Derive(Sales(), new ManifestOverrides { DateTable = new ManifestDateOverride { Name = "dim_product" } });
            Assert.Contains(missing.Needs, n => n.Kind == "date-table");
            Assert.Equal("dim_date", missing.DateTable.Name);   // the draft's own choice stands
        }

        [Fact]
        public void Two_tables_with_one_name_in_two_schemas_are_told_apart_and_a_clash_is_asked_about()
        {
            var s = new FabricSchema
            {
                Tables = new[]
                {
                    new FabricTable { Schema = "dbo", Name = "Sales", Columns = new[] { C("Id", "Int64") } },
                    new FabricTable { Schema = "staging", Name = "Sales", Columns = new[] { C("Id", "Int64") } },
                },
            };
            Assert.Equal(new[] { "dbo_Sales", "staging_Sales" }, Derive(s).Tables.Select(t => t.Name).ToArray());
            // Leaving one out leaves the other under its own name.
            Assert.Equal(new[] { "Sales" }, Derive(s, new ManifestOverrides { Tables = new[] { new ManifestTableOverride { Table = "staging.Sales", Include = false } } })
                .Tables.Where(t => t.Include).Select(t => t.Name).ToArray());
            var clash = Derive(s, new ManifestOverrides { Tables = new[] { new ManifestTableOverride { Table = "dbo.Sales", Name = "X" }, new ManifestTableOverride { Table = "staging.Sales", Name = "x" } } });
            Assert.Contains(clash.Needs, n => n.Kind == "name-clash");
        }

        [Fact]
        public void The_manifest_is_plain_data_and_survives_a_round_trip()
        {
            var m = Derive(SqlDraftBuildTests.J8Schema(), new ManifestOverrides { DateTable = new ManifestDateOverride { FiscalYearEnd = "6/30" } },
                new[] { new ManifestDecision { Table = "fact_timesheet", Column = "BILL_RATE", Pick = "average" } }, "seed-1");
            var back = JsonSerializer.Deserialize<StructureManifest>(JsonSerializer.Serialize(m, Spec), Spec);
            Assert.Equal(Json(m.ToSpec()), Json(back.ToSpec()));
            Assert.Equal(m.DeriveTimeIntelligence().BaseMeasures, back.DeriveTimeIntelligence().BaseMeasures);
        }

        // Verifier (sf-manifest) note 1: undo then redo of a seeded build used to bring the links back under fresh GUIDs,
        // because the fixed name was set on the raw TOM relationship AFTER the add was recorded, so the undo record held the
        // random one. Same plan, same bytes has to hold after a redo too.
        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public async Task Redo_of_a_seeded_build_keeps_the_fixed_relationship_names()
        {
            var spec = Derive(SqlDraftBuildTests.Shape("galaxy"), seed: "s").ToSpec();
            var (sessions, engine, report) = await BuildAsync(spec);
            using (sessions) using (engine)
            {
                Assert.Empty(report.Errors);
                var before = await sessions.Current.ReadAsync(m => m.Relationships.Select(r => r.ID).OrderBy(x => x).ToArray());
                Assert.NotEmpty(before);
                await engine.UndoAsync("human");
                await engine.RedoAsync("human");
                var after = await sessions.Current.ReadAsync(m => m.Relationships.Select(r => r.ID).OrderBy(x => x).ToArray());
                Assert.Equal(before, after);
            }
        }
    }
}
