// Footprints, "Waits for" and Ready (Plan-heart contract 5.1 to 5.4, lane E2).
//
// Two kinds of test live here. The rule tests build footprints by hand and check the arithmetic of contract 5.2:
// read after write waits, write-write waits in plan order, read-read never waits, a cycle is broken by plan order,
// an unknown footprint is never independent, and the two careful-rule facets are EVENT facets. The model tests
// run the real compose and check what reaches the wire.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanFootprintTests
    {
        // ---- the leftover assistant row (P0b carried finding) ----------------------------------------------

        /// <summary>After a safe fix is approved its gap closes and its item leaves the plan. Its lines stay on the
        /// change plan as APPLIED lines, the receipt of the run. They used to be filed under an assistant row,
        /// because nothing claimed them any more, so the plan grew "asst/set_column_hidden" with no change set:
        /// a row that says the assistant proposed work that had in fact already run. A phantom row also feeds the
        /// footprint rule a row with writes nobody will ever make.</summary>
        [Fact]
        public async Task Applied_safe_fix_lines_do_not_open_an_assistant_row()
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == "Wide").Copy("phantom");
            await CommutationRows.WorkOutAsync(f, "safefix/Visibility");
            var before = await f.Engine.GetModelPlanAsync();
            Assert.Contains(before.Items, i => i.Id == "safefix/Visibility" && i.ChangeSet?.Lines.Length > 0);

            Assert.Null(await CommutationRows.ApproveAsync(f, "safefix/Visibility"));

            var after = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(after.Items, i => i.Id == "safefix/Visibility" && i.Status != ModelPlanStatuses.Done);
            var phantom = after.Items.Where(i => i.Id.StartsWith("asst/set_column_hidden", StringComparison.Ordinal)).ToArray();
            Assert.True(phantom.Length == 0,
                "a leftover assistant row: " + string.Join(", ", phantom.Select(i => i.Id + " " + i.Status + " set=" + (i.ChangeSet?.Lines.Length ?? -1))));
            // The seeded assistant line is still pending, so ITS row stays, which proves the fix did not simply
            // stop assistant rows from opening.
            Assert.Contains(after.Items, i => i.Id == "asst/set_display_folder" && i.ChangeSet?.Lines.Length == 1);
        }

        // ---- the rule (contract 5.2), over footprints built by hand --------------------------------------------

        private static Footprint Fp(string[] reads, string[] writes) =>
            new Footprint().ReadAll(reads ?? Array.Empty<string>()).WriteAll(writes ?? Array.Empty<string>());

        private static readonly string[] None = Array.Empty<string>();

        private static Dictionary<string, List<string>> WaitsOf(params (string Id, Footprint Fp)[] rows) =>
            PlanFootprint.Waits(rows).Waits;

        private static string[] ReadyOf((string Id, Footprint Fp)[] rows)
        {
            var w = PlanFootprint.Waits(rows).Waits;
            return rows.Select(r => r.Id).Where(id => !w.ContainsKey(id)).ToArray();
        }

        [Fact]
        public void A_read_after_a_write_waits_whatever_the_plan_order()
        {
            var writer = Fp(None, new[] { "measure:Sales/Total" });
            var reader = Fp(new[] { "measure:Sales/Total" }, new[] { "measure:Sales/Other" });
            // The reader first in plan order still waits: one-way, the producer goes first.
            var w = WaitsOf(("reader", reader), ("writer", writer));
            Assert.Equal(new[] { "writer" }, w["reader"]);
            Assert.False(w.ContainsKey("writer"));
        }

        [Fact]
        public void A_write_write_waits_in_plan_order()
        {
            var a = Fp(None, new[] { "column:Sales/Amount" });
            var b = Fp(None, new[] { "column:Sales/Amount" });
            var w = WaitsOf(("a", a), ("b", b));
            Assert.Equal(new[] { "a" }, w["b"]);
            Assert.False(w.ContainsKey("a"));
            w = WaitsOf(("b", b), ("a", a));
            Assert.Equal(new[] { "b" }, w["a"]);
        }

        [Fact]
        public void A_read_read_overlap_never_waits()
        {
            var a = Fp(new[] { "column:Sales/Amount", "model:objects" }, new[] { "measure:Sales/A" });
            var b = Fp(new[] { "column:Sales/Amount", "model:objects" }, new[] { "measure:Sales/B" });
            Assert.Empty(WaitsOf(("a", a), ("b", b)));
            Assert.True(PlanFootprint.Independent(a, b));
        }

        [Fact]
        public void A_mutual_pair_ends_with_exactly_one_ready_row_and_says_why()
        {
            // Each feeds the other: the first draft made both wait for each other forever (Sol 24 Sep).
            var a = Fp(new[] { "measure:Sales/Y" }, new[] { "measure:Sales/X" });
            var b = Fp(new[] { "measure:Sales/X" }, new[] { "measure:Sales/Y" });
            var rows = new[] { ("a", a), ("b", b) };
            Assert.Equal(new[] { "a" }, ReadyOf(rows));
            var (waits, reordered) = PlanFootprint.Waits(rows);
            Assert.Equal(new[] { "a" }, waits["b"]);
            Assert.Contains("b", reordered);

            // On the wire the note is the later row's qualifier, and its reasons are left alone.
            var first = AsstRow("set_dax", "measure:Sales/X", "First", "[Y]");
            var second = AsstRow("set_dax", "measure:Sales/Y", "Second", "[X]");
            first.Why = second.Why = new[] { "one", "two", "three" };
            var inputs = new FootprintInputs { Model = new FootprintSnapshot
            {
                MeasureByName = new[] { ("X", "measure:Sales/X"), ("Y", "measure:Sales/Y") }
                    .ToLookup(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase),
            } };
            PlanFootprint.Stamp(new[] { first, second }, inputs, Open);
            Assert.Empty(first.WaitsFor);
            Assert.Equal("First", Assert.Single(second.WaitsFor).Title);
            Assert.Equal(PlanFootprint.OrderedByPlanNote, second.StateNote);
            Assert.Equal(3, second.Why.Length);
        }

        [Fact]
        public void A_three_row_loop_is_broken_by_plan_order_and_one_row_is_ready()
        {
            // One-way edges that close a loop: a feeds b, b feeds c, c feeds a.
            var a = Fp(new[] { "k:c" }, new[] { "k:a" });
            var b = Fp(new[] { "k:a" }, new[] { "k:b" });
            var c = Fp(new[] { "k:b" }, new[] { "k:c" });
            var rows = new[] { ("a", a), ("b", b), ("c", c) };
            Assert.Equal(new[] { "a" }, ReadyOf(rows));
            var (waits, reordered) = PlanFootprint.Waits(rows);
            Assert.Equal(new[] { "a" }, waits["b"]);
            Assert.Equal(new[] { "b" }, waits["c"]);
            Assert.Contains("b", reordered);
            Assert.Contains("c", reordered);
            Assert.DoesNotContain("a", reordered);
        }

        [Fact]
        public void An_unknown_footprint_is_never_independent_and_neither_waits_nor_blocks()
        {
            var unknown = Footprint.Unknown();
            var known = Fp(None, new[] { "measure:Sales/X" });
            Assert.False(PlanFootprint.Independent(unknown, known));
            Assert.False(PlanFootprint.Independent(known, unknown));
            Assert.False(PlanFootprint.Independent(unknown, Footprint.Unknown()));
            Assert.Empty(WaitsOf(("u", unknown), ("k", known)));
            Assert.Empty(WaitsOf(("k", known), ("u", unknown)));
        }

        [Fact]
        public void Two_rows_that_only_both_write_an_event_facet_are_independent_and_a_reader_waits()
        {
            // Two creates: both write model:objects and model:revision, and nothing else they share.
            var create1 = Fp(None, new[] { "measure:Sales/A", "model:objects", "model:revision" });
            var create2 = Fp(None, new[] { "measure:Returns/B", "model:objects", "model:revision" });
            Assert.True(PlanFootprint.Independent(create1, create2));
            Assert.Empty(WaitsOf(("c1", create1), ("c2", create2)));

            // A population row reads model:objects, so it waits for a create even when it comes first.
            var population = Fp(new[] { "model:objects" }, new[] { "column:Sales/Amount", "model:revision" });
            Assert.False(PlanFootprint.Independent(create1, population));
            Assert.Equal(new[] { "c1" }, WaitsOf(("pop", population), ("c1", create1))["pop"]);

            // A report-aware row reads model:revision, so it waits for any commit.
            var check = Fp(new[] { "model:revision" }, None);
            Assert.Equal(new[] { "c2" }, WaitsOf(("check", check), ("c2", create2))["check"]);

            // Any other key keeps the write-write rule.
            var d1 = Fp(None, new[] { "model:date-table" });
            var d2 = Fp(None, new[] { "model:date-table" });
            Assert.False(PlanFootprint.Independent(d1, d2));
        }

        // ---- stamping the plan: which rows count, and what the wire says -------------------------------------

        private static ModelPlanItem AsstRow(string kind, string target, string title, string after = "x",
                                             string status = ModelPlanStatuses.Proposed, string state = ModelPlanItemStates.Proposed) =>
            new ModelPlanItem
            {
                Id = LocalEngine.AssistantItemPrefix + kind + "#" + title,
                Title = title,
                Sub = "before",
                Status = status,
                State = state,
                ChangeSet = new ModelPlanChangeSet
                {
                    State = ModelPlanChangeStates.Proposed,
                    Lines = new[] { new ModelPlanChangeLine { Op = kind, Target = target, After = after } },
                },
            };

        /// <summary>A model snapshot with one measure, Sales[Base], a column beside it, and a table whose name needs quotes.</summary>
        private static FootprintInputs BaseMeasureModel() => new FootprintInputs
        {
            Model = new FootprintSnapshot
            {
                MeasureRefs = new[] { "measure:Sales/Base", "measure:Sales/Ratio", "measure:Sales Plan/Target" },
                ColumnsByTable = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Sales"] = new[] { "column:Sales/Amount" },
                    ["Sales Plan"] = new[] { "column:Sales Plan/Goal" },
                },
                MeasuresByTable = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Sales"] = new[] { "measure:Sales/Base", "measure:Sales/Ratio" },
                    ["Sales Plan"] = new[] { "measure:Sales Plan/Target" },
                },
                MeasureByName = new[] { ("Base", "measure:Sales/Base"), ("Ratio", "measure:Sales/Ratio"), ("Target", "measure:Sales Plan/Target") }
                    .ToLookup(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase),
            },
        };

        /// <summary>Sol 6, slice 7, finding 2. A set_dax that reads a measure through a table-qualified reference,
        /// Sales[Base], was matched only as a column, so the measure was missed: a row rewriting Base could be called
        /// independent of a row that reads it. Every spelling of the reference must be a read, and the reader waits.</summary>
        [Theory]
        [InlineData("Sales[Base] / 2")]
        [InlineData("'Sales'[Base] / 2")]
        [InlineData("DIVIDE ( Sales[Base], 2 )")]
        [InlineData("[Base] / 2")]
        [InlineData("DIVIDE([Base],2)")]
        public void A_set_dax_that_reads_a_measure_by_any_spelling_waits_for_the_row_that_rewrites_it(string dax)
        {
            var x = BaseMeasureModel();
            var writer = AsstRow("set_dax", "measure:Sales/Base", "Rewrite Base", "SUM ( Sales[Amount] )");
            var reader = AsstRow("set_dax", "measure:Sales/Ratio", "Rewrite Ratio", dax);

            var fr = PlanFootprint.FromLines(reader.ChangeSet.Lines, x);
            var fw = PlanFootprint.FromLines(writer.ChangeSet.Lines, x);
            Assert.Contains("measure:Sales/Base", fr.Reads);
            Assert.False(PlanFootprint.Independent(fw, fr), dax + " was called independent of the row that rewrites Base");

            // The reader first in plan order still waits for the writer.
            PlanFootprint.Stamp(new[] { reader, writer }, x, Open);
            Assert.Equal("Rewrite Base", Assert.Single(reader.WaitsFor).Title);
            Assert.Empty(writer.WaitsFor);
        }

        /// <summary>The same finding for a table name that needs quotes, and for a measure name with an escaped bracket.</summary>
        [Fact]
        public void A_quoted_table_and_an_escaped_bracket_still_resolve_to_the_measure()
        {
            var x = BaseMeasureModel();
            Assert.Contains("measure:Sales Plan/Target", PlanFootprint.RefsIn(x.Model, "'Sales Plan'[Target] * 2"));
            Assert.Contains("column:Sales Plan/Goal", PlanFootprint.RefsIn(x.Model, "SUM ( 'Sales Plan'[Goal] )"));
            x.Model.MeasureByName = new[] { ("Odd]Name", "measure:Sales/Odd]Name") }.ToLookup(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("measure:Sales/Odd]Name", PlanFootprint.RefsIn(x.Model, "[Odd]]Name] + Sales[Odd]]Name]"));
        }

        private static bool Open(ModelPlanItem i) =>
            i.Status == ModelPlanStatuses.Ready || i.Status == ModelPlanStatuses.Proposed;

        [Fact]
        public void An_open_blocker_keeps_its_dependent_waiting_and_named_and_a_closed_one_does_not()
        {
            // The blocker writes the measure's expression; the dependent writes a description of the same measure,
            // so they write the same object and the later waits for the earlier.
            var blocker = AsstRow("set_dax", "measure:Sales/Total", "Rewrite Total", "SUM ( Sales[Amount] )");
            var dependent = AsstRow("set_description", "measure:Sales/Total", "Describe Total");
            PlanFootprint.Stamp(new[] { blocker, dependent }, new FootprintInputs(), Open);
            Assert.True(blocker.FootprintKnown);
            Assert.Contains("measure:Sales/Total", blocker.Writes);
            Assert.Contains("model:revision", blocker.Writes);
            Assert.Empty(blocker.WaitsFor);
            Assert.Equal("Rewrite Total", Assert.Single(dependent.WaitsFor).Title);
            Assert.Equal("Waits for: Rewrite Total", dependent.Sub);

            // Done, set aside and applied-checking rows do not block.
            foreach (var (status, state) in new[]
            {
                (ModelPlanStatuses.Done, ModelPlanItemStates.Done),
                (ModelPlanStatuses.Skipped, ModelPlanItemStates.Skipped),
                (ModelPlanStatuses.Ready, ModelPlanItemStates.Applied),
            })
            {
                var closed = AsstRow("set_dax", "measure:Sales/Total", "Rewrite Total", "SUM ( Sales[Amount] )", status, state);
                var d = AsstRow("set_description", "measure:Sales/Total", "Describe Total");
                PlanFootprint.Stamp(new[] { closed, d }, new FootprintInputs(), Open);
                Assert.Empty(d.WaitsFor);
                Assert.Equal("before", d.Sub);
            }
        }

        [Fact]
        public void A_time_intelligence_line_reads_the_date_table_facet()
        {
            var fp = PlanFootprint.FromLines(new[]
            {
                new ModelPlanChangeLine { Op = "set_dax", Target = "measure:Sales/LY",
                                          After = "CALCULATE ( [Total Sales], SAMEPERIODLASTYEAR ( 'Date'[Date] ) )" },
            }, new FootprintInputs());
            Assert.Contains("model:date-table", fp.Reads);
            var plain = PlanFootprint.FromLines(new[]
            {
                new ModelPlanChangeLine { Op = "set_dax", Target = "measure:Sales/T", After = "SUM ( Sales[Amount] )" },
            }, new FootprintInputs());
            Assert.DoesNotContain("model:date-table", plain.Reads);
        }

        /// <summary>A relationship line reads BOTH columns it joins and writes the relationships at both tables, so a
        /// row that deletes the key it joins, or changes a relationship at either table, is never run beside it.</summary>
        [Fact]
        public void A_relationship_line_reads_both_columns_and_writes_the_relationships_at_both_tables()
        {
            var rel = PlanFootprint.FromLines(new[]
            {
                new ModelPlanChangeLine { Op = "create_relationship", Target = "column:Sales/ProductKey", Property = "relationship",
                                          After = "Product[ProductKey], many to one, filters one way, active" },
            }, new FootprintInputs());
            Assert.Contains("column:Sales/ProductKey", rel.Reads);
            Assert.Contains("column:Product/ProductKey", rel.Reads);
            Assert.Contains(PlanFootprint.RelationshipsAt("Sales"), rel.Writes);
            Assert.Contains(PlanFootprint.RelationshipsAt("Product"), rel.Writes);

            var deleteKey = PlanFootprint.FromLines(new[]
            {
                new ModelPlanChangeLine { Op = "delete", Target = "column:Product/ProductKey" },
            }, new FootprintInputs());
            Assert.False(PlanFootprint.Independent(deleteKey, rel), "deleting the key a relationship joins was called independent of it");
        }

        /// <summary>A bracket can sit in the key's table name or in its column name, and the display text alone cannot
        /// tell which. With the model's own column refs the footprint reads exactly the column that exists; without
        /// them it reads every column the text could name, which only ever makes a line wait longer, never run beside
        /// a row it touches.</summary>
        [Fact]
        public void A_relationship_key_with_a_bracket_is_read_from_the_model_refs()
        {
            var line = new ModelPlanChangeLine { Op = "create_relationship", Target = "column:Sales/OldKey", Property = "relationship",
                                                 After = "Dim[Old[Key], many to one, filters one way, active" };
            var model = new FootprintInputs();
            model.Model.ColumnsByTable["Dim"] = new[] { "column:Dim/Old[Key" };
            model.Model.ColumnsByTable["Sales"] = new[] { "column:Sales/OldKey" };

            var known = PlanFootprint.FromLines(new[] { line }, model);
            Assert.Contains("column:Dim/Old[Key", known.Reads);
            Assert.DoesNotContain("column:Dim[Old/Key", known.Reads);
            Assert.Contains(PlanFootprint.RelationshipsAt("Dim"), known.Writes);
            Assert.DoesNotContain(PlanFootprint.RelationshipsAt("Dim[Old"), known.Writes);

            var unknown = PlanFootprint.FromLines(new[] { line }, new FootprintInputs());
            Assert.Contains("column:Dim/Old[Key", unknown.Reads);
            Assert.Contains("column:Dim[Old/Key", unknown.Reads);
        }

        /// <summary>verify: the key a relationship line joins is read off the line's display text ("Product[ProductKey], many
        /// to one, ..."). A key whose table name holds a bracket ("Dim [Old]", a legal TOM name) is split at the first
        /// bracket, so the footprint reads a column that does not exist and a row deleting the real key is called
        /// independent of the relationship that joins it.</summary>
        [Fact]
        public void Verify_a_relationship_line_to_a_table_whose_name_holds_a_bracket_still_reads_its_key()
        {
            var rel = PlanFootprint.FromLines(new[]
            {
                new ModelPlanChangeLine { Op = "create_relationship", Target = "column:Sales/OldKey", Property = "relationship",
                                          After = "Dim [Old][OldKey], many to one, filters one way, active" },
            }, new FootprintInputs());
            var deleteKey = PlanFootprint.FromLines(new[]
            {
                new ModelPlanChangeLine { Op = "delete", Target = "column:Dim [Old]/OldKey" },
            }, new FootprintInputs());

            Assert.Contains("column:Dim [Old]/OldKey", rel.Reads);
            Assert.False(PlanFootprint.Independent(deleteKey, rel), "deleting the key a relationship joins was called independent of it");
        }

        // ---- the date mark (lane m11/plan-waits, UAT 26 Sep) ------------------------------------------------------

        /// <summary>Northstar in miniature: a Date table, a ratio measure with no time intelligence, a year to date
        /// measure, a measure that filters on the Date table, and a calculation item that holds time intelligence.</summary>
        private static FootprintInputs DateModel() => new FootprintInputs
        {
            Model = new FootprintSnapshot
            {
                MeasureRefs = new[] { "measure:Sales/Net Sales", "measure:Sales/Margin %", "measure:Sales/Sales YTD", "measure:Sales/Sales 2024" },
                ColumnsByTable = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Date"] = new[] { "column:Date/Date", "column:Date/Year" },
                    ["Sales"] = new[] { "column:Sales/Amount", "column:Sales/OrderDate" },
                },
                MeasuresByTable = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Sales"] = new[] { "measure:Sales/Net Sales", "measure:Sales/Margin %", "measure:Sales/Sales YTD", "measure:Sales/Sales 2024" },
                },
                MeasureByName = new[] { ("Net Sales", "measure:Sales/Net Sales"), ("Margin %", "measure:Sales/Margin %"),
                                        ("Sales YTD", "measure:Sales/Sales YTD"), ("Sales 2024", "measure:Sales/Sales 2024") }
                    .ToLookup(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase),
                Expressions = new[]
                {
                    ("measure:Sales/Net Sales", "SUM ( Sales[Amount] )"),
                    // A date FUNCTION is not the Date table: DATE ( ... ) names no table.
                    ("measure:Sales/Margin %", "DIVIDE ( [Net Sales], CALCULATE ( [Net Sales], Sales[OrderDate] >= DATE ( 2020, 1, 1 ) ) )"),
                    ("measure:Sales/Sales YTD", "TOTALYTD ( [Net Sales], 'Date'[Date] )"),
                    ("measure:Sales/Sales 2024", "CALCULATE ( [Net Sales], 'Date'[Year] = 2024 )"),
                },
                CalculationItems = new[] { ("calcitem:Time Intelligence/YTD", "CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'Date'[Date] ) )") },
            },
        };

        private static ModelPlanItem DateRow(string id) => new ModelPlanItem
        {
            Id = id, Title = "Mark Date as the date table", Sub = "before", Doer = PlanDoers.Semanticus,
            Status = ModelPlanStatuses.Proposed, State = ModelPlanItemStates.Proposed,
            ChangeSet = new ModelPlanChangeSet
            {
                State = ModelPlanChangeStates.Proposed,
                Lines = new[] { new ModelPlanChangeLine { Op = "mark_date_table", Target = "table:Date", After = "Date", Kept = true } },
            },
        };

        private static ModelPlanItem TaskRow(string id, string title, params string[] objects) => new ModelPlanItem
        {
            Id = id, Title = title, Sub = "before", Doer = PlanDoers.Assistant, Objects = objects,
            Status = ModelPlanStatuses.Ready, State = ModelPlanItemStates.Gap,
        };

        /// <summary>UAT 26 Sep, lane o: "Mark Date as the date table" waited for two tasks about Margin %, because the
        /// row read EVERY measure. Marking writes the table's date category and its key column; what it can change is
        /// time intelligence, and anything that filters on that table. A measure with neither is not read, so a writer
        /// of it holds nothing. Both rows that mark: the rule's and Jev's.</summary>
        [Theory]
        [InlineData("datetable/model")]
        [InlineData("jev.date-table.table:Date")]
        public void The_date_mark_reads_what_the_mark_can_change_and_not_every_measure(string id)
        {
            var x = DateModel();
            var fp = PlanFootprint.Of(id, DateRow(id).ChangeSet.Lines, x);
            Assert.True(fp.Known);
            Assert.DoesNotContain("measure:Sales/Margin %", fp.Reads);
            Assert.DoesNotContain("measure:Sales/Net Sales", fp.Reads);
            Assert.Contains("measure:Sales/Sales YTD", fp.Reads);          // time intelligence
            Assert.Contains("measure:Sales/Sales 2024", fp.Reads);         // filters on the Date table
            Assert.Contains("calcitem:Time Intelligence/YTD", fp.Reads);   // time intelligence in a calculation group
            Assert.Contains("model:date-table", fp.Writes);
            Assert.Contains("table:Date", fp.Writes);
            Assert.Contains("column:Date/Date", fp.Writes);

            // The wait that follows: a task on Margin % holds nothing; a task on Sales YTD still does.
            var mark = DateRow(id);
            var margin = TaskRow("pt-1", "Fix Margin % to a ratio of totals", "measure:Sales/Margin %");
            var ytd = TaskRow("pt-2", "Check Sales YTD", "measure:Sales/Sales YTD");
            PlanFootprint.Stamp(new[] { mark, margin, ytd }, x, Open);
            Assert.Equal(new[] { "pt-2" }, mark.WaitsFor.Select(w => w.Id).ToArray());
        }

        /// <summary>A real conflict stays: a line that puts time intelligence INTO a measure the mark does not read now
        /// reads the date table, so the two rows still conflict.</summary>
        [Fact]
        public void A_line_that_adds_time_intelligence_to_a_measure_still_conflicts_with_the_date_mark()
        {
            var x = DateModel();
            var mark = PlanFootprint.Of("datetable/model", DateRow("datetable/model").ChangeSet.Lines, x);
            var ly = PlanFootprint.FromLines(new[]
            {
                new ModelPlanChangeLine { Op = "set_dax", Target = "measure:Sales/Margin %",
                                          After = "CALCULATE ( [Net Sales], SAMEPERIODLASTYEAR ( 'Date'[Date] ) )" },
            }, x);
            Assert.False(PlanFootprint.Independent(mark, ly));
            var plain = PlanFootprint.FromLines(new[]
            {
                new ModelPlanChangeLine { Op = "set_dax", Target = "measure:Sales/Margin %", After = "DIVIDE ( [Net Sales], 2 )" },
            }, x);
            Assert.True(PlanFootprint.Independent(mark, plain));
        }

        /// <summary>Until a line names the table, which table is marked is not known, so the rule's row still reads
        /// every measure and every calculation item: wide is the safe side.</summary>
        [Fact]
        public void Until_a_line_names_the_table_the_date_row_reads_every_measure()
        {
            var x = DateModel();
            var fp = PlanFootprint.Of("datetable/model", Array.Empty<ModelPlanChangeLine>(), x);
            Assert.Contains("measure:Sales/Margin %", fp.Reads);
            Assert.Contains("measure:Sales/Net Sales", fp.Reads);
            Assert.Contains("calcitem:Time Intelligence/YTD", fp.Reads);
        }

        /// <summary>Jev's "add a date table" line creates a table and marks it. A create writes the objects facet (a
        /// population row gains the new table and its columns) and a mark writes the date table facet. It used to fall
        /// through to "a property of one object", so it was called independent of the describe sweep.</summary>
        [Fact]
        public void Adding_a_date_table_writes_the_objects_and_the_date_table()
        {
            var x = DateModel();
            var fp = PlanFootprint.Of("jev.date-table.table:Date 2", new[]
            {
                new ModelPlanChangeLine { Op = "generate_date_table", Target = "table:Date 2", Property = "generate_date_table", After = "Date 2" },
            }, x);
            Assert.Contains("table:Date 2", fp.Writes);
            Assert.Contains("model:objects", fp.Writes);
            Assert.Contains("model:date-table", fp.Writes);
            var describe = new Footprint().Read("model:objects").Write("table:Customer").Read("table:Customer");
            Assert.False(PlanFootprint.Independent(fp, describe));
            var mark = PlanFootprint.Of("datetable/model", DateRow("datetable/model").ChangeSet.Lines, x);
            Assert.False(PlanFootprint.Independent(fp, mark));
        }

        /// <summary>UAT 26 Sep, lane m11/plan-waits: once the tasks no longer held it, "Mark Date as the date table" still
        /// waited for "Format 1 column in Date", because the mark read every column of the table. A mark that names its
        /// key column checks only that column (MarkDateTableCore: it exists and is a date), sets the table's category and
        /// the key, and clears the key on the column that holds it now. Nothing else in the table is read or written, and
        /// only a mark ever writes a key (the date table facet orders marks against each other). With no key named, the
        /// mark chooses among the table's date columns, so it reads them all, as before.</summary>
        [Theory]
        [InlineData("datetable/model", true)]
        [InlineData("jev.date-table.table:Date", true)]
        [InlineData("datetable/model", false)]   // the rule's row before its line is worked out
        public void A_mark_reads_its_key_column_and_not_every_column_of_the_table(string id, bool withLine)
        {
            var x = RuleDateModel();
            x.Model.ColumnsByTable["Date"] = new[] { "column:Date/Date", "column:Date/Year", "column:Date/IsWeekend", "column:Date/Day" };
            x.Model.KeyColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "column:Date/Day" };
            var lines = withLine ? DateRow(id).ChangeSet.Lines : Array.Empty<ModelPlanChangeLine>();
            var mark = PlanFootprint.Of(id, lines, x);

            Assert.Contains("column:Date/Date", mark.Reads);
            Assert.DoesNotContain("column:Date/IsWeekend", mark.Reads);
            Assert.DoesNotContain("column:Date/IsWeekend", mark.Writes);
            Assert.Contains("column:Date/Day", mark.Writes);                // its key is cleared
            var format = PlanFootprint.FromLines(new[]
            {
                new ModelPlanChangeLine { Op = "set_format_string", Target = "column:Date/IsWeekend", After = "0" },
            }, x);
            Assert.True(PlanFootprint.Independent(mark, format), CommutationRows.Why(mark, format));
            // Still ordered against what the mark does touch: the key column, the table, and the column whose key it clears.
            foreach (var target in new[] { "column:Date/Date", "column:Date/Day", "table:Date" })
                Assert.False(PlanFootprint.Independent(mark, PlanFootprint.FromLines(new[]
                {
                    new ModelPlanChangeLine { Op = "set_description", Target = target, After = "x" },
                }, x)), target);
        }

        [Fact]
        public void A_mark_that_names_no_key_reads_every_column_of_the_table()
        {
            var x = DateModel();
            var fp = PlanFootprint.FromLines(new[] { new ModelPlanChangeLine { Op = "mark_date_table", Target = "table:Date" } }, x);
            Assert.Contains("column:Date/Date", fp.Reads);
            Assert.Contains("column:Date/Year", fp.Reads);
        }

        // ---- the plan kinds that fell through to "a property of one object" (lane m11/plan-waits, coordinator) ----

        private static Footprint LineFp(FootprintInputs x, string op, string target, string after = null) =>
            PlanFootprint.FromLines(new[] { new ModelPlanChangeLine { Op = op, Target = target, After = after } }, x);

        /// <summary>Every kind ApplyOneItem runs that CREATES something writes the objects facet (the careful rule: a
        /// create adds members to a population row's population). They fell through to the default and wrote only
        /// their target, so each was called independent of the describe sweep although it adds a thing to describe.</summary>
        [Theory]
        [InlineData("create_column", "column:Sales/Segment", null)]
        [InlineData("create_import_table", "table:Region", "let Source = 1 in Source")]
        [InlineData("create_calculation_group", "table:Scenario", null)]
        [InlineData("create_calculation_item", "table:Scenario", "CALCULATE ( SELECTEDMEASURE (), Sales[Amount] > 0 )")]
        [InlineData("create_role", "role:Auditors", null)]
        [InlineData("create_relationship", "column:Sales/OrderDate", null)]
        [InlineData("generate_time_intelligence", "measure:Sales/Net Sales", null)]
        public void A_create_writes_the_objects_facet(string op, string target, string after)
        {
            var x = DateModel();
            var fp = LineFp(x, op, target, after);
            Assert.True(fp.Known);
            Assert.Contains("model:objects", fp.Writes);
            var describe = new Footprint().Read("model:objects").Write("table:Customer").Read("table:Customer");
            Assert.False(PlanFootprint.Independent(fp, describe), op);
        }

        /// <summary>A relationship kind writes the relationships at its tables: the safe fixes, the date mark and the
        /// relationship proposals read them. A new relationship's other table is not on its line (the line names the
        /// from column only), so it writes the relationships at every table: wide is the safe side.</summary>
        [Theory]
        [InlineData("create_relationship", "column:Sales/OrderDate")]
        [InlineData("set_relationship_active", "relationship:Sales-Promo")]
        [InlineData("set_relationship_cardinality", "relationship:Sales-Promo")]
        public void A_relationship_kind_writes_the_relationships_at_its_tables(string op, string target)
        {
            var x = RuleDateModel();
            var fp = LineFp(x, op, target, op == "set_relationship_active" ? "false" : null);
            Assert.Contains("model:relationships:Sales", fp.Writes);
            Assert.Contains("model:relationships:Promo", fp.Writes);
            var safeFix = new Footprint().Write("column:Sales/Amount").Read("column:Sales/Amount").Read("model:relationships:Sales");
            Assert.False(PlanFootprint.Independent(fp, safeFix), op);
            Assert.False(PlanFootprint.Independent(fp, PlanFootprint.Of("datetable/model", Array.Empty<ModelPlanChangeLine>(), x)), op);
        }

        /// <summary>A table permission needs its role: it names the role only in the line's arguments, so a role and a
        /// permission meet on the roles facet, and two permissions do too.</summary>
        [Fact]
        public void A_role_and_a_table_permission_meet_on_the_roles_facet()
        {
            var x = DateModel();
            var role = LineFp(x, "create_role", "role:Auditors");
            var permission = LineFp(x, "set_table_permission", "table:Sales", "Sales[Amount] > 0");
            Assert.False(PlanFootprint.Independent(role, permission));
            Assert.False(PlanFootprint.Independent(permission, LineFp(x, "set_table_permission", "table:Customer", "TRUE ()")));
            Assert.Contains("column:Sales/Amount", permission.Reads);   // its filter's DAX
        }

        /// <summary>Time intelligence made by a line reads the date table: generated measures, and a calculation item
        /// whose DAX uses it, so both are ordered against a date mark.</summary>
        [Fact]
        public void Generated_time_intelligence_and_a_time_intelligence_calculation_item_read_the_date_table()
        {
            var x = DateModel();
            var mark = PlanFootprint.Of("datetable/model", DateRow("datetable/model").ChangeSet.Lines, x);
            Assert.False(PlanFootprint.Independent(mark, LineFp(x, "generate_time_intelligence", "measure:Sales/Net Sales")));
            Assert.False(PlanFootprint.Independent(mark, LineFp(x, "create_calculation_item", "table:Scenario",
                "CALCULATE ( SELECTEDMEASURE (), DATESYTD ( 'Date'[Date] ) )")));
        }

        /// <summary>The rule's row before Run the plan works its line out (UAT 26 Sep, lane m11/plan-waits, second pass:
        /// before consent "Mark Date as the date table" still waited for the Margin % tasks). The row already knows which
        /// table and key it will mark (PlanDateMark), because that is how it was titled. So the model is DateModel plus
        /// what the work-out reads: a Promo table whose date column no DAX reads yet, and the date columns and readers
        /// the mark's choice rests on. Sales is the many side of a relationship, so Sales[OrderDate] is no candidate.</summary>
        private static FootprintInputs RuleDateModel()
        {
            var x = DateModel();
            x.Model.ColumnsByTable["Promo"] = new[] { "column:Promo/StartDate", "column:Promo/Name" };
            x.Model.CandidateDateColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "column:Date/Date", "column:Promo/StartDate" };
            x.Model.DateReaders = new[] { "measure:Sales/Sales YTD", "calcitem:Time Intelligence/YTD" };
            x.Model.RelationshipTables["Sales-Promo"] = new[] { "Sales", "Promo" };
            x.Findings = new[] { new Semanticus.Analysis.ReadinessFinding { RuleId = "DATE-MARK", ObjectRef = "obj:Model/Model" } };
            x.DateMark = new PlanDateMarkCandidate { TableRef = "table:Date", Table = "Date", Column = "Date" };
            return x;
        }

        private static ModelPlanItem RuleDateRow() => new ModelPlanItem
        {
            Id = "datetable/model", Title = "Mark Date as the date table", Sub = "before", Doer = PlanDoers.Semanticus,
            Status = ModelPlanStatuses.Ready, State = ModelPlanItemStates.Gap,
        };

        [Fact]
        public void Before_its_line_is_worked_out_the_rule_row_reads_what_its_mark_can_change()
        {
            var x = RuleDateModel();
            var fp = PlanFootprint.Of("datetable/model", Array.Empty<ModelPlanChangeLine>(), x);
            Assert.True(fp.Known);
            Assert.DoesNotContain("measure:Sales/Margin %", fp.Reads);
            Assert.DoesNotContain("measure:Sales/Net Sales", fp.Reads);
            Assert.Contains("measure:Sales/Sales YTD", fp.Reads);          // time intelligence, and it reads the key
            Assert.Contains("measure:Sales/Sales 2024", fp.Reads);         // filters on the Date table
            Assert.Contains("calcitem:Time Intelligence/YTD", fp.Reads);
            // The mark it will make: the table, the key column it named, and the date table facet. Not every column.
            Assert.Contains("table:Date", fp.Writes);
            Assert.Contains("column:Date/Date", fp.Writes);
            Assert.Contains("model:date-table", fp.Writes);
            Assert.DoesNotContain("column:Date/Year", fp.Writes);
            Assert.DoesNotContain("obj:Model/Model", fp.Writes);
            // Which table is the many side of a relationship decides the choice, so the relationships are read.
            Assert.Contains("model:relationships:Promo", fp.Reads);

            var mark = RuleDateRow();
            var margin = TaskRow("pt-1", "Fix Margin % to a ratio of totals", "measure:Sales/Margin %");
            var ytd = TaskRow("pt-2", "Check Sales YTD", "measure:Sales/Sales YTD");
            PlanFootprint.Stamp(new[] { mark, margin, ytd }, x, Open);
            Assert.Equal(new[] { "pt-2" }, mark.WaitsFor.Select(w => w.Id).ToArray());
        }

        /// <summary>The conflicts the narrow read keeps: any line that can change WHICH table and key the work-out picks.
        /// A line that makes DAX read a date column no DAX read before (a second candidate), a data type change (a column
        /// becomes, or stops being, a date), and a relationship change (a table becomes the many side).</summary>
        [Fact]
        public void A_line_that_can_change_which_table_the_rule_row_marks_still_conflicts_with_it()
        {
            var x = RuleDateModel();
            var mark = PlanFootprint.Of("datetable/model", Array.Empty<ModelPlanChangeLine>(), x);
            Footprint Line(string op, string target, string after) =>
                PlanFootprint.FromLines(new[] { new ModelPlanChangeLine { Op = op, Target = target, After = after } }, x);

            Assert.False(PlanFootprint.Independent(mark, Line("set_dax", "measure:Sales/Margin %",
                "CALCULATE ( [Net Sales], Promo[StartDate] >= DATE ( 2024, 1, 1 ) )")));
            Assert.False(PlanFootprint.Independent(mark, Line("create_measure", "measure:Sales/Promo Sales",
                "CALCULATE ( [Net Sales], Promo[StartDate] >= DATE ( 2024, 1, 1 ) )")));
            Assert.False(PlanFootprint.Independent(mark, Line("set_data_type", "column:Promo/Name", "DateTime")));
            Assert.False(PlanFootprint.Independent(mark, Line("set_relationship_crossfilter", "relationship:Sales-Promo", "Both")));
            // A measure that keeps away from every date the choice rests on is independent. Sales[OrderDate] sits on the
            // many side, so reading it changes nothing.
            Assert.True(PlanFootprint.Independent(mark, Line("set_dax", "measure:Sales/Margin %", "DIVIDE ( [Net Sales], 2 )")));
            Assert.True(PlanFootprint.Independent(mark, Line("set_dax", "measure:Sales/Margin %",
                "CALCULATE ( [Net Sales], Sales[OrderDate] >= DATE ( 2020, 1, 1 ) )")));
        }

        /// <summary>Contract 5.2: a paused row still counts as open, so a row that waits for it stays Waiting, and its
        /// sub line NAMES the paused row. It named only the first blocker in plan order, so a paused row further down
        /// the list held the row without being named (UAT 26 Sep).</summary>
        [Fact]
        public void A_row_that_waits_for_a_paused_row_stays_waiting_and_names_it()
        {
            var x = DateModel();
            var mark = DateRow("datetable/model");
            var first = TaskRow("pt-1", "Check Sales YTD", "measure:Sales/Sales YTD");
            var paused = TaskRow("pt-2", "Check Sales 2024", "measure:Sales/Sales 2024");
            paused.Held = true;
            PlanFootprint.Stamp(new[] { mark, first, paused }, x, Open);
            Assert.Equal(new[] { "pt-2", "pt-1" }, mark.WaitsFor.Select(w => w.Id).ToArray());
            Assert.Equal("Waits for: Check Sales 2024 and 1 more", mark.Sub);

            // Resumed: the plan's order again.
            mark = DateRow("datetable/model");
            paused.Held = false;
            PlanFootprint.Stamp(new[] { mark, first, paused }, x, Open);
            Assert.Equal(new[] { "pt-1", "pt-2" }, mark.WaitsFor.Select(w => w.Id).ToArray());
            Assert.Equal("Waits for: Check Sales YTD and 1 more", mark.Sub);
        }

        // ---- completeness --------------------------------------------------------------------------------------

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln"))) dir = dir.Parent;
            Assert.True(dir != null, "could not find Semanticus.sln above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }

        /// <summary>Every family id prefix the compose's source spells has a footprint function. Parsed from the
        /// source, the same shape as OpParameterKindsTests, so a new family cannot land without one.</summary>
        [Fact]
        public void Every_family_the_plan_composes_has_a_footprint()
        {
            var engine = Path.Combine(RepoRoot(), "Semanticus.Engine");
            var files = new[] { Path.Combine(engine, "LocalEngine.Plan.cs") }
                .Concat(Directory.GetFiles(Path.Combine(engine, "Plan"), "*.cs", SearchOption.AllDirectories));
            var prefixes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
                foreach (Match m in Regex.Matches(string.Join("\n", File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))), "\"(asst|safefix|check|cleanup|describe|proposal|recheck|ship|starter|target|datetable|calendar|[a-z]+)/"))
                    prefixes.Add(m.Groups[1].Value + "/");
            // Not item ids: a change-set JOB id, and ref kinds spelled inside ids ("starter/table:Sales").
            prefixes.Remove("modelplan/");
            // A rule AIM's target id (PlanTargets.RuleAims), not a plan item id: aims reach the plan as "target/".
            prefixes.Remove("rules/");
            var missing = prefixes.Where(p => !PlanFootprint.Families.ContainsKey(p)).ToArray();
            Assert.True(missing.Length == 0, "families with no footprint: " + string.Join(", ", missing));
            // The prefixes spelled as constants rather than literals.
            Assert.True(PlanFootprint.Families.ContainsKey(LocalEngine.AssistantItemPrefix));
            Assert.True(PlanFootprint.Families.ContainsKey(JevFamily.ItemPrefix));
            Assert.True(PlanFootprint.Families.ContainsKey(StarterMeasuresFamily.RuleItemPrefix));
            Assert.True(PlanFootprint.Families.ContainsKey(PlanTargets.ItemPrefix));
            Assert.True(prefixes.Count >= 12, "the source scan found only " + prefixes.Count + " prefixes: " + string.Join(", ", prefixes));
        }

        /// <summary>On real composed plans, every row but an aim carries a known footprint that says what it
        /// writes, and every waiting row names rows that are on the same plan.</summary>
        [Fact]
        public async Task Every_composed_row_carries_its_footprint_on_the_wire()
        {
            foreach (var fx in CommutationFixtures.All)
            {
                using var f = await fx.Copy("wire-" + fx.Name);
                var plan = await f.Engine.GetModelPlanAsync();
                var ids = new HashSet<string>(plan.Items.Select(i => i.Id), StringComparer.Ordinal);
                foreach (var i in plan.Items.Where(i => i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed))
                {
                    if (i.Id.StartsWith(PlanTargets.ItemPrefix, StringComparison.Ordinal)) continue;
                    Assert.True(i.FootprintKnown, fx.Name + " " + i.Id + " has no known footprint");
                    if (!i.Id.StartsWith("check/", StringComparison.Ordinal) && !i.Id.StartsWith("recheck/", StringComparison.Ordinal)
                        && !i.Id.StartsWith("ship/", StringComparison.Ordinal))
                        Assert.True(i.Writes.Length > 0, fx.Name + " " + i.Id + " writes nothing");
                    foreach (var w in i.WaitsFor) Assert.Contains(w.Id, ids);
                }
                // Some row is always Ready: the rule is acyclic by construction.
                if (plan.Items.Any(i => i.FootprintKnown && i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed))
                    Assert.Contains(plan.Items, i => i.FootprintKnown && i.Status is ModelPlanStatuses.Ready or ModelPlanStatuses.Proposed && i.WaitsFor.Length == 0);
            }
        }

        // ---- the finish line (lanes.md E2; mockup section 6, step 4) ------------------------------------------

        /// <summary>On a model with no marked date table, a time intelligence change waits for "Mark the date
        /// table"; after the table is marked, the very next read shows it with nothing to wait for, and no other
        /// call was made in between.</summary>
        [Fact]
        public async Task A_time_intelligence_row_waits_for_the_date_table_and_turns_ready_once_it_is_marked()
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == "Wide").Copy("finish");
            // On a measure a report-free model still uses (Sales YTD names it), so the cleanup row, which removes
            // what nothing uses, has no claim on it: this row's only blocker is the date table.
            await f.Engine.AddPlanItemAsync("measure:Sales/Total Sales", "set_dax",
                "CALCULATE ( COUNTROWS ( Sales ), SAMEPERIODLASTYEAR ( 'Date'[Date] ) )", null, null, null, "agent");

            var plan = await f.Engine.GetModelPlanAsync();
            var mark = Assert.Single(plan.Items, i => i.Id == "datetable/model");
            Assert.Contains("model:date-table", mark.Writes);
            var ti = Assert.Single(plan.Items, i => i.Id == "asst/set_dax");
            Assert.Contains("model:date-table", ti.Reads);
            Assert.Contains(ti.WaitsFor, w => w.Id == "datetable/model" && w.Title == mark.Title);
            Assert.True(ti.Sub.StartsWith("Waits for: ", StringComparison.Ordinal) && ti.Sub.Contains(ti.WaitsFor[0].Title),
                ti.Sub + " | waits for: " + string.Join(", ", ti.WaitsFor.Select(w => w.Id)));
            Assert.True(ti.WaitsFor.Length == 1 && ti.WaitsFor[0].Id == "datetable/model",
                "the date table should be its only blocker: " + string.Join(", ", ti.WaitsFor.Select(w => w.Id + " (" + w.Title + ")")));
            Assert.Empty(mark.WaitsFor.Where(w => w.Id == ti.Id));

            await f.Engine.MarkDateTableAsync("table:Date", "Date", "human");

            plan = await f.Engine.GetModelPlanAsync();
            Assert.DoesNotContain(plan.Items, i => i.Id == "datetable/model" && i.Status != ModelPlanStatuses.Done);
            ti = Assert.Single(plan.Items, i => i.Id == "asst/set_dax");
            Assert.True(ti.WaitsFor.Length == 0, "still waits for: " + string.Join(", ", ti.WaitsFor.Select(w => w.Title)));
            Assert.DoesNotContain("Waits for", ti.Sub ?? "");
        }

        // ---- names shared across kinds, and deletes that remove relationships (review round 3, siblings of PW1 to PW3) ----

        private static Footprint Line(FootprintInputs x, string op, string target, string after = null, params (string K, string V)[] args) =>
            PlanFootprint.FromLines(new[]
            {
                new ModelPlanChangeLine { Op = op, Target = target, After = after,
                    Args = args.Length == 0 ? null : args.ToDictionary(a => a.K, a => a.V, StringComparer.Ordinal) },
            }, x);

        [Fact]
        public void Lines_that_take_one_member_or_measure_name_are_never_independent_and_different_names_are()
        {
            var x = new FootprintInputs { Model = new FootprintSnapshot
            {
                ColumnsByTable = { ["Sales"] = new[] { "column:Sales/Amount" }, ["Customer"] = new[] { "column:Customer/Name" } },
            } };
            // A delete frees a measure name another table's new measure takes (measure names are model-wide).
            Assert.False(PlanFootprint.Independent(Line(x, "delete", "measure:Sales/Margin"), Line(x, "create_measure", "measure:Customer/Margin", "1")));
            // A rename of a measure to a name a new column takes on the same table.
            Assert.False(PlanFootprint.Independent(Line(x, "rename", "measure:Sales/Old", "Region"),
                Line(x, "create_column", "table:Sales", null, ("tableRef", "table:Sales"), ("name", "Region"))));
            // A rename of a column to a name a new measure takes on the same table.
            Assert.False(PlanFootprint.Independent(Line(x, "rename", "column:Sales/Amount", "Total"), Line(x, "create_measure", "measure:Sales/Total", "1")));
            // Different names stay independent: the claims only meet on the same name.
            Assert.True(PlanFootprint.Independent(Line(x, "create_measure", "measure:Sales/A", "1"), Line(x, "create_measure", "measure:Customer/B", "1")));
            // A column name may repeat on another table, so those stay independent too.
            Assert.True(PlanFootprint.Independent(
                Line(x, "create_column", "table:Sales", null, ("tableRef", "table:Sales"), ("name", "Key")),
                Line(x, "create_column", "table:Customer", null, ("tableRef", "table:Customer"), ("name", "Key"))));
        }

        [Fact]
        public void A_delete_that_removes_a_relationship_is_ordered_against_switching_on_another_between_its_tables()
        {
            const string active = "'Customer'[City] -> 'Product'[Colour]";
            const string other = "'Customer'[CustomerKey] -> 'Product'[ProductKey]";
            var x = new FootprintInputs { Model = new FootprintSnapshot
            {
                ColumnsByTable =
                {
                    ["Customer"] = new[] { "column:Customer/City", "column:Customer/CustomerKey" },
                    ["Product"] = new[] { "column:Product/Colour", "column:Product/ProductKey" },
                    ["Sales"] = new[] { "column:Sales/Amount" },
                },
                RelationshipTables = { [active] = new[] { "Customer", "Product" }, [other] = new[] { "Customer", "Product" } },
                RelationshipColumns =
                {
                    [active] = new[] { "column:Customer/City", "column:Product/Colour" },
                    [other] = new[] { "column:Customer/CustomerKey", "column:Product/ProductKey" },
                },
            } };
            var switchOn = Line(x, "set_relationship_active", "relationship:" + other, "true");
            // The relationship itself, a column that ends it, and a table at one end: each delete removes it.
            foreach (var target in new[] { "relationship:" + active, "column:Customer/City", "column:Product/Colour", "table:Product" })
                Assert.False(PlanFootprint.Independent(Line(x, "delete", target), switchOn), target);
            // A delete of a column that ends no relationship does not reach the pair.
            Assert.True(PlanFootprint.Independent(Line(x, "delete", "column:Sales/Amount"), switchOn));
        }
    }
}
