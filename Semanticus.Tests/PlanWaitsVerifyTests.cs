// verify: lane m11/plan-waits. Tests the verifier added against the lane's footprints, run through the lane's own
// commutation harness (PlanCommutationHarness.cs). Each one asks the gate's own question of a pair the full gate
// never runs: when the engine calls two rows independent, do they give the same model and the same lines in either
// order? A test here that fails proves a pair the footprint calls independent when the order matters.
//
// They live in the PlanCommutationTests class on purpose: xunit runs one class's tests one at a time, so the
// assistant row the rename tests register (asst/rename) cannot reach another commutation test mid-run.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    public sealed partial class PlanCommutationTests
    {
        // ---- 1. a mark changes which columns are keys, and two populations read that -------------------------------

        /// <summary>The date table's key column is a date the person summarizes by Count (the grid offers it for dates),
        /// and the table joins the facts on a whole number DateKey, so Date[Date] is no relationship's end. Before the
        /// mark it is in no Formatting population; the mark keys it, and FMT-SUMMARIZE ("a key that aggregates") then
        /// reads it. The Formatting row also sets Date[DateKey] and Date[Year], which the old mark read (it read every
        /// column of its table) and the narrowed mark no longer does.</summary>
        private static readonly CommutationFixture VerifyNewKeyFixture = new CommutationFixture
        {
            Name = "VerifyNewKey",
            Build = async file =>
            {
                var f = new DecisionFixture(TestEntitlements.Pro);
                var e = f.Engine;
                await e.CreateModelAsync("Contoso", 1604);
                await e.CreateTableAsync("Date", "human");
                await e.CreateColumnAsync("table:Date", "Date", "DateTime", "Date", "human");
                await e.SetObjectPropertyAsync("column:Date/Date", "SummarizeBy", "Count", "human");
                await e.CreateColumnAsync("table:Date", "DateKey", "Int64", null, "human");
                await e.CreateColumnAsync("table:Date", "Year", "Int64", null, "human");
                await e.CreateTableAsync("Sales", "human");
                await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
                await e.SetObjectPropertyAsync("column:Sales/Amount", "FormatString", "#,0.00", "human");
                await e.CreateColumnAsync("table:Sales", "DateKey", "Int64", null, "human");
                await e.CreateRelationshipAsync("column:Sales/DateKey", "column:Date/DateKey", null, true, "human");
                await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
                await e.CreateMeasureAsync("table:Sales", "Sales YTD", "TOTALYTD ( [Total Sales], 'Date'[Date] )", "human");
                await DescribeEverythingAsync(f);
                await f.SaveAsAsync(file);
                f.UseNull();
                return f;
            },
        };

        /// <summary>The date table already has a key, a number column (DateSerial) nobody marked as a date. Keying Date[Date]
        /// clears it (TOMWrapper Column.cs), and FMT-COLUMN ("a visible number column with no format") reads every column
        /// that is not a key, so the formats row gains DateSerial once the mark lands. The formats row also formats
        /// Date[Rate], which the old mark read and the narrowed mark does not.</summary>
        private static readonly CommutationFixture VerifyOldKeyFixture = new CommutationFixture
        {
            Name = "VerifyOldKey",
            Build = async file =>
            {
                var f = new DecisionFixture(TestEntitlements.Pro);
                var e = f.Engine;
                await e.CreateModelAsync("Contoso", 1604);
                await e.CreateTableAsync("Date", "human");
                await e.CreateColumnAsync("table:Date", "Date", "DateTime", "Date", "human");
                await e.CreateColumnAsync("table:Date", "DateSerial", "Double", null, "human");
                await e.SetObjectPropertyAsync("column:Date/DateSerial", "IsKey", "true", "human");
                await e.SetObjectPropertyAsync("column:Date/DateSerial", "SummarizeBy", "None", "human");
                await e.CreateColumnAsync("table:Date", "Rate", "Double", null, "human");
                await e.CreateTableAsync("Sales", "human");
                await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
                await e.SetObjectPropertyAsync("column:Sales/Amount", "FormatString", "#,0.00", "human");
                await e.CreateColumnAsync("table:Sales", "OrderDate", "DateTime", null, "human");
                await e.CreateRelationshipAsync("column:Sales/OrderDate", "column:Date/Date", null, true, "human");
                await e.CreateMeasureAsync("table:Sales", "Total Sales", "SUM ( Sales[Amount] )", "human");
                await e.CreateMeasureAsync("table:Sales", "Sales YTD", "TOTALYTD ( [Total Sales], 'Date'[Date] )", "human");
                await DescribeEverythingAsync(f);
                await f.SaveAsAsync(file);
                f.UseNull();
                return f;
            },
        };

        private static async Task DescribeEverythingAsync(DecisionFixture f)
        {
            var refs = await f.Sessions.Current.ReadAsync(m =>
                m.AllColumns.Select(c => ObjectRefs.For(c))
                 .Concat(m.AllMeasures.Select(x => ObjectRefs.For(x)))
                 .Concat(m.Tables.Select(t => ObjectRefs.For(t)))
                 .ToArray());
            foreach (var r in refs) await f.Engine.SetDescriptionAsync(r, "Described by the fixture.", "human");
        }

        /// <summary>The gate's rule, on the date mark with its line and the row whose population the mark's key changes.
        /// On the lane's base the pair was ordered (the mark read every column of its table, and the partner writes one
        /// of them); the lane narrowed the mark to its key column and the columns keyed now, so the engine now calls the
        /// pair independent. Done both ways, the partner's lines and the model differ, so the order matters.</summary>
        [Theory]
        [InlineData("VerifyNewKey", "safefix/Formatting")]
        [InlineData("VerifyOldKey", "proposal/formats")]
        public async Task Verify_a_date_mark_is_ordered_against_a_row_whose_population_its_key_changes(string fixture, string partner)
        {
            var fx = fixture == "VerifyNewKey" ? VerifyNewKeyFixture : VerifyOldKeyFixture;
            var log = new StringBuilder();
            var (rows, fps) = await FootprintsAsync(fx, log);
            const string mark = "datetable/model";
            Assert.True(rows.Contains(mark) && rows.Contains(partner), "rows: " + string.Join(", ", rows) + "\n" + log);

            var o = await RunPairAsync(fx, mark, partner, fps, "verify-" + fixture);
            log.AppendLine(Line(o));
            Write("verify-" + fixture, new List<PairOutcome> { o }, log);
            Assert.True(!o.Independent || (string.IsNullOrEmpty(o.RunError) && o.Check1 && o.Check2),
                "the engine calls this pair independent and the order matters:\n" + Line(o) + "\n" + log);
        }

        // ---- 2. a rename, against the lines that name what it renames only in their arguments ------------------------

        /// <summary>The creates fixture (lane m11/plan-waits) plus a role the model already has, and an assistant row of
        /// renames: two tables that the seeded lines name in their arguments (Customer, Product), and that role. The
        /// seeded create_role is left out, because this model already has its role.</summary>
        private static readonly (string Ref, string After)[] VerifyRenames =
        {
            ("table:Customer", "Client"),
            ("table:Product", "Item"),
            ("role:Auditors", "Reviewers"),
        };

        private static readonly CommutationFixture VerifyCreatesRenamedFixture = new CommutationFixture
        {
            Name = "VerifyCreatesRenamed",
            Build = async file =>
            {
                var f = await CommutationFixtures.Creates.Build(file);
                await f.Engine.CreateRoleAsync("Auditors", "Read", "human");
                await f.SaveAsAsync(file);
                return f;
            },
            PostOpen = async f =>
            {
                foreach (var id in CommutationRows.SeededItems.Keys.Where(k => k != "asst/create_role"))
                    await CommutationRows.SeedItemsAsync(f, id);
                foreach (var (r, a) in VerifyRenames)
                    await f.Engine.AddPlanItemAsync(r, "rename", a, null, null, null, "agent");
            },
        };

        private static (string[] Rows, Dictionary<string, Footprint> Fps)? _verifyRenamed;
        private static readonly System.Threading.SemaphoreSlim VerifyRenamedGate = new System.Threading.SemaphoreSlim(1, 1);

        /// <summary>Five of the kinds the lane gave facets name a table, a column or a role only in their arguments
        /// (create_column its table, create_relationship its two ends, create_calculation_item the columns its DAX reads,
        /// set_table_permission its role), and a relationship's ref is spelled with its tables' names (TOMWrapper
        /// SingleColumnRelationship.Name). None of them reads what a rename of that table or role writes, and the rename
        /// writes only the table's or role's own refs, so the engine calls each pair independent. Done rename first, the
        /// line is refused ("table:Customer is not a table", "Role 'Auditors' not found") or names a relationship that is
        /// no longer there. The gate's rule: an independent pair must give the same model and the same lines both ways.
        /// (set_relationship_active: the harness cannot even work the row out again after the rename, because the
        /// relationship it names is gone under that name.)</summary>
        [Theory]
        [InlineData("asst/create_column")]
        [InlineData("asst/create_relationship")]
        [InlineData("asst/create_calculation_item")]
        [InlineData("asst/set_table_permission")]
        [InlineData("asst/set_relationship_cardinality")]
        [InlineData("asst/set_relationship_active")]
        public async Task Verify_a_rename_is_ordered_against_a_line_that_names_what_it_renames(string partner)
        {
            CommutationRows.AsstSeeds["asst/rename"] = VerifyRenames;
            try
            {
                var fx = VerifyCreatesRenamedFixture;
                var log = new StringBuilder();
                await VerifyRenamedGate.WaitAsync();
                try { _verifyRenamed ??= await FootprintsAsync(fx, log); }
                finally { VerifyRenamedGate.Release(); }
                var (rows, fps) = _verifyRenamed.Value;
                const string rename = "asst/rename";
                Assert.True(rows.Contains(rename) && rows.Contains(partner), "rows: " + string.Join(", ", rows) + "\n" + log);

                var o = await RunPairAsync(fx, rename, partner, fps, "verify-rename-" + partner.Replace('/', '_'));
                log.AppendLine(Line(o));
                Write("verify-rename-" + partner.Replace('/', '_'), new List<PairOutcome> { o }, log);
                Assert.True(!o.Independent || (string.IsNullOrEmpty(o.RunError) && o.Check1 && o.Check2),
                    "the engine calls this pair independent and the order matters:\n" + Line(o)
                    + "\nrename: W={" + string.Join(", ", fps[rename].Writes.OrderBy(x => x)) + "} R={" + string.Join(", ", fps[rename].Reads.OrderBy(x => x)) + "}"
                    + "\n" + partner + ": W={" + string.Join(", ", fps[partner].Writes.OrderBy(x => x)) + "} R={" + string.Join(", ", fps[partner].Reads.OrderBy(x => x)) + "}");
            }
            finally { CommutationRows.AsstSeeds.Remove("asst/rename"); }
        }

        // ---- 3. round 2 (verifier of 70d901fd..402d57e0): what a line's arguments name that its footprint still misses ----

        /// <summary>Wide, and nothing seeded: each test below seeds its own two rows.</summary>
        private static CommutationFixture VerifyPairFixture(string name, Func<DecisionFixture, Task> seed, Func<DecisionFixture, Task> extra = null) =>
            new CommutationFixture
            {
                Name = name,
                Build = async file =>
                {
                    var f = await CommutationFixtures.WideAsync(file);
                    if (extra != null) { await extra(f); await f.SaveAsAsync(file); }
                    return f;
                },
                PostOpen = seed,
            };

        private static async Task<PairOutcome> VerifyPairAsync(CommutationFixture fx, string a, string b, StringBuilder log)
        {
            var (rows, fps) = await FootprintsAsync(fx, log);
            Assert.True(rows.Contains(a) && rows.Contains(b), "rows: " + string.Join(", ", rows) + "\n" + log);
            var o = await RunPairAsync(fx, a, b, fps, "verify-r2-" + fx.Name);
            log.AppendLine(Line(o));
            log.AppendLine(a + ": W={" + string.Join(", ", fps[a].Writes.OrderBy(x => x)) + "} R={" + string.Join(", ", fps[a].Reads.OrderBy(x => x)) + "}");
            log.AppendLine(b + ": W={" + string.Join(", ", fps[b].Writes.OrderBy(x => x)) + "} R={" + string.Join(", ", fps[b].Reads.OrderBy(x => x)) + "}");
            return o;
        }

        private static ChangeItem[] VerifySeeded(string objRef, string kind, string after, params (string Key, string Value)[] args) =>
            new[]
            {
                new ChangeItem
                {
                    ObjectRef = objRef, ObjectName = objRef, Kind = kind, Source = "ai", Target = kind, Risk = "ai",
                    Severity = "Medium", Title = kind + " " + objRef, Rationale = "Seeded by the verifier.",
                    After = after ?? objRef, Status = "approved", Generated = true,
                    Args = args.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
                },
            };

        /// <summary>A column and a measure may not share a name on one table (NameTaken; the engine's own duplicate path
        /// says so: "Measure names are model-wide unique and must not collide with a column on the landing table").
        /// create_column writes the name of column:Customer/Segment and create_measure the name of
        /// measure:Customer/Segment, two different keys, so the engine calls the pair independent. Column first, the
        /// measure is refused ("already has a column named Segment"); measure first, the column is added beside it.</summary>
        [Fact]
        public async Task Verify_a_column_and_a_measure_given_one_name_on_one_table_are_ordered()
        {
            CommutationRows.SeededItems["asst/create_measure"] = _ => VerifySeeded("measure:Customer/Segment", "create_measure", "COUNTROWS ( Customer )");
            try
            {
                var fx = VerifyPairFixture("VerifyColumnMeasureName", async f =>
                {
                    await CommutationRows.SeedItemsAsync(f, "asst/create_column");
                    await CommutationRows.SeedItemsAsync(f, "asst/create_measure");
                });
                var log = new StringBuilder();
                var o = await VerifyPairAsync(fx, "asst/create_column", "asst/create_measure", log);
                Write("verify-r2-column-measure-name", new List<PairOutcome> { o }, log);
                Assert.True(!o.Independent || (string.IsNullOrEmpty(o.RunError) && o.Check1 && o.Check2),
                    "the engine calls this pair independent and the order matters:\n" + log);
            }
            finally { CommutationRows.SeededItems.Remove("asst/create_measure"); }
        }

        /// <summary>Measure names are unique across the whole model. A rename of Sales[Sales YTD] to "Customer Count" and a
        /// new measure Customer[Customer Count] name the same measure name on two tables, and the footprint keys a measure's
        /// name by its table, so the engine calls the pair independent. Rename first, the new measure is refused ("A
        /// measure named Customer Count already exists"); create first, the rename meets a name already taken.</summary>
        [Fact]
        public async Task Verify_a_rename_and_a_new_measure_that_take_one_measure_name_are_ordered()
        {
            CommutationRows.SeededItems["asst/create_measure"] = _ => VerifySeeded("measure:Customer/Customer Count", "create_measure", "COUNTROWS ( Customer )");
            CommutationRows.AsstSeeds["asst/rename"] = new[] { ("measure:Sales/Sales YTD", "Customer Count") };
            try
            {
                var fx = VerifyPairFixture("VerifyMeasureNameModelWide", async f =>
                {
                    await CommutationRows.SeedItemsAsync(f, "asst/create_measure");
                    await f.Engine.AddPlanItemAsync("measure:Sales/Sales YTD", "rename", "Customer Count", null, null, null, "agent");
                });
                var log = new StringBuilder();
                var o = await VerifyPairAsync(fx, "asst/rename", "asst/create_measure", log);
                Write("verify-r2-measure-name-model-wide", new List<PairOutcome> { o }, log);
                Assert.True(!o.Independent || (string.IsNullOrEmpty(o.RunError) && o.Check1 && o.Check2),
                    "the engine calls this pair independent and the order matters:\n" + log);
            }
            finally
            {
                CommutationRows.SeededItems.Remove("asst/create_measure");
                CommutationRows.AsstSeeds.Remove("asst/rename");
            }
        }

        /// <summary>Only one relationship between two tables can be active (GuardNoOtherActiveOnTablePair). Customer and
        /// Product are joined twice here: City to Colour active, CustomerKey to ProductKey inactive. A delete of the active
        /// one writes that relationship's ref and name only, not the relationships at its tables, and switching the other
        /// one on writes the relationships at Customer and Product, so the engine calls the pair independent. Delete first,
        /// the switch goes through; switch first, it is refused ("An active relationship already exists").</summary>
        [Fact]
        public async Task Verify_deleting_an_active_relationship_is_ordered_against_switching_on_another_between_the_same_tables()
        {
            var fx = VerifyPairFixture("VerifyRelationshipDelete",
                async f =>
                {
                    var active = await f.Sessions.Current.ReadAsync(m => ObjectRefs.For(m.Relationships.OfType<SingleColumnRelationship>()
                        .Single(r => r.FromTable?.Name == "Customer" && r.FromColumn?.Name == "City")));
                    CommutationRows.AsstSeeds["asst/delete"] = new[] { (active, "(delete)") };
                    await f.Engine.AddPlanItemAsync(active, "delete", "(delete)", null, null, null, "agent");
                    await CommutationRows.SeedItemsAsync(f, "asst/set_relationship_active");
                },
                async f =>
                {
                    await f.Engine.CreateRelationshipAsync("column:Customer/CustomerKey", "column:Product/ProductKey", null, false, "human");
                    await f.Engine.CreateRelationshipAsync("column:Customer/City", "column:Product/Colour", null, true, "human");
                });
            try
            {
                var log = new StringBuilder();
                var o = await VerifyPairAsync(fx, "asst/delete", "asst/set_relationship_active", log);
                Write("verify-r2-relationship-delete", new List<PairOutcome> { o }, log);
                Assert.True(!o.Independent || (string.IsNullOrEmpty(o.RunError) && o.Check1 && o.Check2),
                    "the engine calls this pair independent and the order matters:\n" + log);
            }
            finally { CommutationRows.AsstSeeds.Remove("asst/delete"); }
        }

        // ---- 4. round 3 (verifier of 165ba3d0..fd784aea): the siblings of PW1 to PW3 the claims still miss ----------

        /// <summary>The sibling of PW2 that the round 3 claims miss: a measure's name is unique across the whole model
        /// (StarterMeasureTemplates.NameTaken), and deleting a TABLE removes every measure on it, which frees their names.
        /// A delete writes the claims of its own target only, and a table's claims are its own name, so a delete of
        /// Scratch (which holds the measure Scratch Count) and a new measure Customer[Scratch Count] meet on nothing and
        /// the engine calls them independent. Delete first, the new measure is added; create first, it is refused ("A
        /// measure named Scratch Count already exists in this model").</summary>
        [Fact]
        public async Task Verify_deleting_a_table_frees_the_names_of_its_measures_for_a_new_measure()
        {
            CommutationRows.SeededItems["asst/create_measure"] = _ => VerifySeeded("measure:Customer/Scratch Count", "create_measure", "COUNTROWS ( Customer )");
            CommutationRows.AsstSeeds["asst/delete"] = new[] { ("table:Scratch", "(delete)") };
            try
            {
                var fx = VerifyPairFixture("VerifyTableDeleteMeasureName",
                    async f =>
                    {
                        await f.Engine.AddPlanItemAsync("table:Scratch", "delete", "(delete)", null, null, null, "agent");
                        await CommutationRows.SeedItemsAsync(f, "asst/create_measure");
                    },
                    async f =>
                    {
                        await f.Engine.CreateTableAsync("Scratch", "human");
                        await f.Engine.CreateColumnAsync("table:Scratch", "Note", "String", null, "human");
                        await f.Engine.CreateMeasureAsync("table:Scratch", "Scratch Count", "COUNTROWS ( Scratch )", "human");
                    });
                var log = new StringBuilder();
                var o = await VerifyPairAsync(fx, "asst/delete", "asst/create_measure", log);
                Write("verify-r3-table-delete-measure-name", new List<PairOutcome> { o }, log);
                Assert.True(!o.Independent || (string.IsNullOrEmpty(o.RunError) && o.Check1 && o.Check2),
                    "the engine calls this pair independent and the order matters:\n" + log);
            }
            finally
            {
                CommutationRows.SeededItems.Remove("asst/create_measure");
                CommutationRows.AsstSeeds.Remove("asst/delete");
            }
        }
    }
}
