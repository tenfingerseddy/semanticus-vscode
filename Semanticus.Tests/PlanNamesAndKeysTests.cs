// Review fix, lane m11/plan-waits, round 2 (verifier FAIL, defects D1 to D3). Two things a plan line's footprint did not
// state, each of which let the engine call a pair of rows independent when one order changed the model or was refused:
//
//   KEYS. A date mark sets a key and clears the old one, and six readiness rules sweep "the keys" or "what is not a
//   key". The mark now writes model:keys when it moves a key one of those rules would find something different
//   about, and the rows those rules feed read it. Northstar's Date[Date] (a date with a format and no summary) is
//   not such a key, so marking it still writes nothing more and its row keeps its win.
//
//   NAMES. A line reaches its object by a name: a column through its table's name, a relationship through its two
//   columns' names (TOMWrapper spells a relationship's name with them), and a create or a permission through the
//   objects named in its arguments. A rename or a delete moves that name, so it writes model:name:<ref> and the line
//   reads it. A setting written on the object (a table's description) is not its name, so no new wait appears there.
//
// The rule tests build footprints by hand; the drift test runs every readiness rule on a real model; the commutation
// test does the cardinality pair both ways on a fixture where a starter row reads the cardinality.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Semanticus.Analysis;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanNamesAndKeysTests
    {
        // ---- keys --------------------------------------------------------------------------------------------------

        /// <summary>A date table with a date column, a number, a year and a serial; Sales joins it on DateKey. The snapshot
        /// says which keys a rule would find something different about (FootprintSnapshot.KeyStatusRead, from
        /// ReadinessRuleSet.KeyStatusCanMatter): here not Date[Date], as on Northstar.</summary>
        private static FootprintInputs KeysModel(params string[] keys) => new FootprintInputs
        {
            Model = new FootprintSnapshot
            {
                ColumnsByTable = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Date"] = new[] { "column:Date/Date", "column:Date/DateKey", "column:Date/Year", "column:Date/Serial" },
                    ["Sales"] = new[] { "column:Sales/Amount", "column:Sales/DateKey", "column:Sales/OrderDate" },
                },
                KeyColumns = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase),
                KeyStatusRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "column:Date/DateKey", "column:Date/Year", "column:Date/Serial", "column:Sales/Amount", "column:Sales/DateKey" },
            },
            ProposalFamily = ProposalFamilyOfTheEngine,
        };

        private static readonly Func<ReadinessFinding, string> ProposalFamilyOfTheEngine = f =>
            (string)typeof(LocalEngine).GetMethod("ProposalFamily", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { f });

        private static Footprint Mark(FootprintInputs x, string key) =>
            PlanFootprint.FromLines(new[] { new ModelPlanChangeLine { Op = "mark_date_table", Target = "table:Date", After = key } }, x);

        /// <summary>Northstar's case first: the key is a date no rule would find anything about as a key, and nothing is
        /// keyed yet, so the mark writes no keys. Then each way a mark does move a key a rule sees: a key it sets that a
        /// rule sees (verify D1: a date that sums by Count, which FMT-SUMMARIZE then finds), an old key it clears (verify
        /// D2: a number with no format, which FMT-COLUMN then finds), and a key column the snapshot does not know yet.</summary>
        [Theory]
        [InlineData("Date", new string[0], false)]                           // Northstar: nothing to find, no old key
        [InlineData("Date", new[] { "column:Date/Date" }, false)]            // already the key: nothing moves
        [InlineData("DateKey", new string[0], true)]                         // sets a key a rule sees (D1)
        [InlineData("Date", new[] { "column:Date/Serial" }, true)]           // clears a key a rule sees (D2)
        [InlineData("Made Later", new string[0], true)]                      // a column another row adds: counted as seen
        public void A_mark_writes_the_keys_only_when_it_moves_a_key_a_population_can_see(string key, string[] keyed, bool writes)
        {
            var fp = Mark(KeysModel(keyed), key);
            Assert.Equal(writes, fp.Writes.Contains(PlanFootprint.Keys));
        }

        /// <summary>With no key named, the mark chooses among the table's columns, so any of them may move: it writes the
        /// keys when any column of the table is one a rule sees.</summary>
        [Fact]
        public void A_mark_that_names_no_key_writes_the_keys_when_any_column_of_its_table_is_seen()
        {
            var fp = PlanFootprint.FromLines(new[] { new ModelPlanChangeLine { Op = "mark_date_table", Target = "table:Date" } }, KeysModel());
            Assert.Contains(PlanFootprint.Keys, fp.Writes);
        }

        /// <summary>The rows the key status rules feed read the keys, and only those: the Formatting safe fixes
        /// (FMT-SUMMARIZE, SUMMARIZE-DIMENSION), the formats, visibility and implicit measure proposals, and Jev's column
        /// format rows. A describe row, the Visibility safe fixes and the names proposal do not.</summary>
        [Theory]
        [InlineData("safefix/Formatting", true)]
        [InlineData("proposal/formats", true)]
        [InlineData("proposal/visibility", true)]
        [InlineData("proposal/implicit-measures", true)]
        [InlineData("jev.column-format.table:Date", true)]
        [InlineData("safefix/Visibility", false)]
        [InlineData("proposal/names", false)]
        [InlineData("describe/model", false)]
        public void The_rows_whose_population_reads_keys_read_the_keys(string id, bool reads)
        {
            var x = KeysModel();
            var line = new[] { new ModelPlanChangeLine { Op = "set_format_string", Target = "column:Date/Year", After = "0" } };
            var fp = PlanFootprint.Of(id, line, x);
            Assert.Equal(reads, fp.Reads.Contains(PlanFootprint.Keys));
            // And the pair the verifier proved: a mark that moves a seen key is ordered against it; Northstar's mark is not.
            if (reads) Assert.False(PlanFootprint.Independent(Mark(x, "DateKey"), fp));
            Assert.True(PlanFootprint.Independent(Mark(x, "Date"), fp), CommutationRows.Why(Mark(x, "Date"), fp));
        }

        /// <summary>The drift guard for <see cref="ReadinessRuleSet.KeyStatusRules"/> and
        /// <see cref="ReadinessRuleSet.KeyStatusCanMatter"/>. On a model with every kind of column those rules tell apart,
        /// each column is keyed on its own (its table's other keys cleared first, so only it moves) and every default
        /// readiness rule runs before and after. A rule whose FINDINGS change must be on the list, and the column must be
        /// one KeyStatusCanMatter calls seen; a rule whose population changes must be on the list too. So a new rule that
        /// reads IsKey, or a finding the predicate does not know, fails here before the plan can call a pair independent
        /// over it. A population that moves with nothing to find changes no row, so only findings bind the predicate.</summary>
        [Fact]
        public async Task Every_rule_that_sees_a_key_move_is_named_and_a_key_with_nothing_to_find_is_seen_by_none()
        {
            using var f = new DecisionFixture(TestEntitlements.Pro);
            var e = f.Engine;
            await e.CreateModelAsync("Contoso", 1604);
            await e.CreateTableAsync("Date", "human");
            await e.CreateColumnAsync("table:Date", "Date", "DateTime", "Date", "human");
            await e.CreateColumnAsync("table:Date", "DateKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Date", "Year", "Int64", null, "human");
            await e.CreateColumnAsync("table:Date", "Serial", "Double", null, "human");
            await e.CreateColumnAsync("table:Date", "Holiday", "DateTime", null, "human");
            // Northstar's Date[Date]: a date with a format and no summary, joined to nothing.
            await e.CreateColumnAsync("table:Date", "Calendar Date", "DateTime", null, "human");
            await e.SetObjectPropertyAsync("column:Date/Calendar Date", "FormatString", "yyyy-mm-dd", "human");
            await e.SetObjectPropertyAsync("column:Date/Calendar Date", "SummarizeBy", "None", "human");
            await e.CreateTableAsync("Sales", "human");
            await e.CreateColumnAsync("table:Sales", "Amount", "Double", null, "human");
            await e.CreateColumnAsync("table:Sales", "DateKey", "Int64", null, "human");
            await e.CreateColumnAsync("table:Sales", "OrderDate", "DateTime", null, "human");
            await e.CreateColumnAsync("table:Sales", "Ship Date", "String", null, "human");
            await e.CreateColumnAsync("table:Sales", "Code", "String", null, "human");
            await e.CreateColumnAsync("table:Sales", "Hidden Amount", "Double", null, "human");
            await e.SetColumnMetadataAsync("column:Sales/Hidden Amount", true, null, null, null, "human");
            await e.CreateRelationshipAsync("column:Sales/DateKey", "column:Date/DateKey", null, true, "human");
            await e.CreateRelationshipAsync("column:Sales/OrderDate", "column:Date/Date", null, false, "human");
            await e.MarkDateTableAsync("table:Date", "Date", "human");

            var m = f.Sessions.Current.Model;
            var rules = ReadinessRuleSet.Default();
            string Findings(RuleEvaluation ev) => ev == null ? "-" : string.Join(",",
                ev.Violations.Select(v => v.ObjectRef + "/" + v.Message).OrderBy(s => s, StringComparer.Ordinal));
            Dictionary<string, (int Applicable, string Findings)> Run() =>
                rules.ToDictionary(r => r.Id, r => { var ev = r.Evaluate(m); return (ev?.Applicable ?? -1, Findings(ev)); }, StringComparer.Ordinal);

            var seenBy = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var unseen = new List<string>();
            foreach (var c in m.AllColumns.Where(c => c.Type != ColumnType.RowNumber).ToArray())
            {
                var keys = c.Table.Columns.Where(k => k.IsKey).ToArray();
                foreach (var k in keys) k.IsKey = false;
                var before = Run();
                c.IsKey = true;
                var after = Run();
                c.IsKey = false;
                foreach (var k in keys) k.IsKey = true;

                var moved = before.Keys.Where(id => before[id] != after[id]).ToArray();
                var changed = before.Keys.Where(id => before[id].Findings != after[id].Findings).ToArray();
                var name = ObjectRefs.For(c);
                Assert.True(moved.All(ReadinessRuleSet.KeyStatusRules.Contains),
                    name + ": a rule not in KeyStatusRules sees its key: " + string.Join(", ", moved.Where(id => !ReadinessRuleSet.KeyStatusRules.Contains(id))));
                if (changed.Length > 0)
                    Assert.True(ReadinessRuleSet.KeyStatusCanMatter(c), name + " is seen by " + string.Join(", ", changed) + " but KeyStatusCanMatter says none can see it");
                else if (!ReadinessRuleSet.KeyStatusCanMatter(c)) unseen.Add(name);
                foreach (var id in changed) (seenBy.TryGetValue(id, out var s) ? s : seenBy[id] = new HashSet<string>()).Add(name);
            }

            // Teeth: the unseen ones are the date relationship ends, the many side's join column (no rule finds anything
            // there: VIS-FK owns it) and Northstar's kind of date, and every rule on the list finds something different on
            // this model, so the list is not padded either.
            Assert.Equal(new[] { "column:Date/Calendar Date", "column:Date/Date", "column:Sales/DateKey", "column:Sales/OrderDate" },
                unseen.OrderBy(s => s, StringComparer.Ordinal).ToArray());
            Assert.Equal(ReadinessRuleSet.KeyStatusRules.OrderBy(s => s, StringComparer.Ordinal),
                         seenBy.Keys.OrderBy(s => s, StringComparer.Ordinal));
        }

        // ---- names -------------------------------------------------------------------------------------------------

        private const string ProductRel = "'Sales'[ProductKey] --> 'Product'[ProductKey]";

        private static FootprintInputs NamesModel() => new FootprintInputs
        {
            Model = new FootprintSnapshot
            {
                ColumnsByTable = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Customer"] = new[] { "column:Customer/CustomerKey", "column:Customer/Name", "column:Customer/City" },
                    ["Product"] = new[] { "column:Product/ProductKey", "column:Product/Colour" },
                    ["Sales"] = new[] { "column:Sales/Amount", "column:Sales/ProductKey", "column:Sales/CustomerKey" },
                },
                RelationshipTables = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [ProductRel] = new[] { "Sales", "Product" } },
                RelationshipColumns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                    { [ProductRel] = new[] { "column:Sales/ProductKey", "column:Product/ProductKey" } },
            },
        };

        private static Footprint L(string op, string target, string after = null, params (string Key, string Value)[] args) =>
            PlanFootprint.FromLines(new[]
            {
                new ModelPlanChangeLine
                {
                    Op = op, Target = target, After = after,
                    Args = args.Length == 0 ? null : args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal),
                },
            }, NamesModel());

        /// <summary>Each pair: a rename, a delete or a create, and a line that reaches the object it moves by name, often
        /// only in its arguments. Every one was called independent; in every one, one order refuses the line or changes
        /// what it names (verify D3 and the kinds the verifier listed as the same root cause).</summary>
        private static IEnumerable<(string Why, Footprint A, Footprint B)> OrderedPairList()
        {
            (string, Footprint, Footprint) P(string why, Footprint a, Footprint b) => (why, a, b);
            var renameCustomer = L("rename", "table:Customer", "Client");
            yield return P("a column line, through its table", renameCustomer, L("set_format_string", "column:Customer/Name", "@"));
            yield return P("a delete of the table", L("delete", "table:Customer"), L("set_description", "column:Customer/Name", "x"));
            yield return P("create_column's table argument", renameCustomer,
                L("create_column", "column:Customer/Segment", null, ("tableRef", "table:Customer"), ("name", "Segment"), ("dataType", "String")));
            yield return P("create_measure's own table", renameCustomer, L("create_measure", "measure:Customer/Customers", "COUNTROWS ( Customer )"));
            yield return P("set_dax that names the table alone", renameCustomer, L("set_dax", "measure:Sales/Buyers", "COUNTROWS ( Customer )"));
            yield return P("create_calculation_item's DAX", renameCustomer, L("create_calculation_item", "table:Scenario",
                "CALCULATE ( SELECTEDMEASURE (), Customer[City] = \"NZ\" )", ("calcGroupRef", "table:Scenario"), ("name", "NZ")));
            yield return P("create_relationship's other end", L("rename", "table:Product", "Item"),
                L("create_relationship", "column:Customer/City", null, ("fromColumnRef", "column:Customer/City"), ("toColumnRef", "column:Product/Colour")));
            yield return P("a relationship, through its table", L("rename", "table:Product", "Item"),
                L("set_relationship_cardinality", "relationship:" + ProductRel, null, ("fromCardinality", "One"), ("toCardinality", "One")));
            yield return P("a relationship, through its column", L("rename", "column:Product/ProductKey", "Id"),
                L("set_relationship_active", "relationship:" + ProductRel, "false"));
            yield return P("a permission's role, renamed", L("rename", "role:Auditors", "Reviewers"),
                L("set_table_permission", "table:Sales", "TRUE ()", ("roleName", "Auditors"), ("tableRef", "table:Sales")));
            yield return P("a permission's role, deleted", L("delete", "role:Auditors"),
                L("set_table_permission", "table:Sales", "TRUE ()", ("roleName", "Auditors"), ("tableRef", "table:Sales")));
            yield return P("a table another row creates", L("create_import_table", "table:Region", "let S = 1 in S", ("name", "Region")),
                L("create_column", "column:Region/Code", null, ("tableRef", "table:Region"), ("name", "Code"), ("dataType", "String")));
            yield return P("a column another row creates", L("create_column", "column:Product/Size", null, ("tableRef", "table:Product"), ("name", "Size")),
                L("create_relationship", "column:Customer/City", null, ("fromColumnRef", "column:Customer/City"), ("toColumnRef", "column:Product/Size")));
        }

        public static IEnumerable<object[]> OrderedPairs() => OrderedPairList().Select(p => new object[] { p.Why });

        [Theory]
        [MemberData(nameof(OrderedPairs))]
        public void A_move_of_a_name_is_ordered_against_a_line_that_reaches_its_object_by_it(string why)
        {
            var (_, mover, line) = OrderedPairList().Single(p => p.Why == why);
            Assert.True(mover.Known && line.Known, why);
            Assert.False(PlanFootprint.Independent(mover, line), why + ": called independent");
        }

        /// <summary>The other side: a name is not a setting. A table's description, or a rename or a delete somewhere else,
        /// does not meet a line inside the table, so none of these pairs gains a wait from this fix.</summary>
        private static IEnumerable<(string Why, Footprint A, Footprint B)> StillIndependentList()
        {
            (string, Footprint, Footprint) P(string why, Footprint a, Footprint b) => (why, a, b);
            yield return P("a table's description and a line on its column",
                L("set_description", "table:Customer", "x"), L("set_format_string", "column:Customer/Name", "@"));
            yield return P("a table's description and a column added to it",
                L("set_description", "table:Customer", "x"),
                L("create_column", "column:Customer/Segment", null, ("tableRef", "table:Customer"), ("name", "Segment")));
            yield return P("a rename of another column of the same table",
                L("rename", "column:Customer/Name", "Full Name"), L("set_format_string", "column:Customer/City", "@"));
            yield return P("a rename of another table", L("rename", "table:Customer", "Client"), L("set_format_string", "column:Sales/Amount", "0"));
            yield return P("a relationship line and a rename of a table it does not join",
                L("rename", "table:Customer", "Client"), L("set_relationship_active", "relationship:" + ProductRel, "false"));
        }

        public static IEnumerable<object[]> StillIndependent() => StillIndependentList().Select(p => new object[] { p.Why });

        [Theory]
        [MemberData(nameof(StillIndependent))]
        public void A_setting_or_a_name_elsewhere_does_not_meet_a_line_by_its_name(string why)
        {
            var (_, a, b) = StillIndependentList().Single(p => p.Why == why);
            Assert.True(PlanFootprint.Independent(a, b), why + ": " + CommutationRows.Why(a, b));
        }

        /// <summary>A census setting on a relationship (set_property CrossFilteringBehavior) is a relationship change like
        /// set_relationship_crossfilter, so it writes the relationships at its tables, which the safe fixes and the starter
        /// rows read.</summary>
        [Fact]
        public void A_census_setting_on_a_relationship_writes_the_relationships_at_its_tables()
        {
            var fp = L("set_property", "relationship:" + ProductRel, "BothDirections");
            Assert.Contains(PlanFootprint.RelationshipsAt("Sales"), fp.Writes);
            Assert.Contains(PlanFootprint.RelationshipsAt("Product"), fp.Writes);
        }

        /// <summary>Jev's starter rows choose fact tables and columns the way the starter rule does (the many side of a
        /// relationship, by its cardinality; a column's key and relationship role), so they read what the rule's row reads:
        /// the table, its columns and the relationships at it. They read only the measures and the objects facet, so a
        /// cardinality change was called independent of them.</summary>
        [Fact]
        public void A_Jev_starter_row_reads_the_relationships_at_its_table()
        {
            var x = NamesModel();
            var starter = PlanFootprint.Of("jev.starter.table:Sales", new[]
            {
                new ModelPlanChangeLine { Op = "create_measure", Target = "measure:Sales/Total Amount", After = "SUM ( Sales[Amount] )" },
            }, x);
            Assert.Contains(PlanFootprint.RelationshipsAt("Sales"), starter.Reads);
            Assert.Contains("column:Sales/ProductKey", starter.Reads);
            Assert.False(PlanFootprint.Independent(starter, L("set_relationship_cardinality", "relationship:" + ProductRel, null)));
        }
    }

    public sealed partial class PlanCommutationTests
    {
        /// <summary>Star, with the Returns relationship turned round by a seeded cardinality line (From One, To Many). The
        /// starter rule takes the many side of a relationship as a fact (StarterMeasureTemplates.FactTables), so the line
        /// takes Returns out of the fact tables, and the starter row for Returns has nothing to add once it lands.</summary>
        private static readonly CommutationFixture StarCardinalityFixture = new CommutationFixture
        {
            Name = "StarCardinality",
            Build = CommutationFixtures.StarAsync,
            PostOpen = f => CommutationRows.SeedItemsAsync(f, "asst/set_relationship_cardinality"),
        };

        /// <summary>The starter rule reads cardinality (StarterMeasureTemplates.FactTables, RelationshipRoles), and it reads
        /// the relationships at its table, which a cardinality line writes. On the Creates fixture the pair commutes because
        /// One and One leaves the many side where it was; turned round, the pair is a proven conflict: done cardinality
        /// first, the starter row for Returns adds nothing, so the model differs.</summary>
        [Fact]
        public async Task A_cardinality_change_that_moves_the_many_side_is_ordered_against_the_starter_row()
        {
            const string cardinality = "asst/set_relationship_cardinality", starter = "starter/table:Returns";
            var saved = CommutationRows.SeededItems[cardinality];
            CommutationRows.SeededItems[cardinality] = m => CommutationRows.SeededCardinality(m, "Returns", "CustomerKey", "One", "Many");
            try
            {
                var log = new StringBuilder();
                var (rows, fps) = await FootprintsAsync(StarCardinalityFixture, log);
                Assert.True(rows.Contains(cardinality) && rows.Contains(starter), "rows: " + string.Join(", ", rows) + "\n" + log);
                Assert.False(PlanFootprint.Independent(fps[cardinality], fps[starter]), "called independent\n" + log);

                var o = await RunPairAsync(StarCardinalityFixture, cardinality, starter, fps, "star-cardinality");
                log.AppendLine(Line(o));
                Write("star-cardinality", new List<PairOutcome> { o }, log);
                Assert.True(!string.IsNullOrEmpty(o.RunError) || !(o.Check1 && o.Check2),
                    "the pair commutes, so it does not show the order matters: " + Line(o));
            }
            finally { CommutationRows.SeededItems[cardinality] = saved; }
        }
    }
}
