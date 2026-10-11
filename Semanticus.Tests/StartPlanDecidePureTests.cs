// The start plan's decide loop and its edits, the PURE half (lane sf-decide): arbitration, folding picks into base measures, the
// oscillation guard, canonical identities, Needs follow with knock-on attribution. Nothing here builds a model or asks anything; the
// loop over a real scratch is StartPlanDecideTests.cs.

using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Decisions;
using Semanticus.Engine;
using Semanticus.Engine.StartPlan;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class StartPlanDecidePureTests
    {
        private const string SumPoint = DecisionRegistry.ColumnSummarize;
        private const string SalesItem = "jev.column-summarize.table:Sales";

        internal static StartPlanDoc Doc()
        {
            ManifestColumn Col(string name, string type) => new ManifestColumn { Name = name, SourceColumn = name, DataType = type, Identity = "c-" + name };
            var sales = new ManifestTable
            {
                Name = "Sales", SourceSchema = "dbo", SourceName = "Sales", Identity = "t-sales",
                Columns = new List<ManifestColumn> { Col("Amount", "Double"), Col("Unit Price", "Double"), Col("Qty", "Int64") },
            };
            return new StartPlanDoc
            {
                Id = "plan-1", IdentitySeed = "seed-1",
                Manifest = new StructureManifest { IdentitySeed = "seed-1", Tables = new List<ManifestTable> { sales } },
            };
        }

        private static void AddLink(StartPlanDoc doc, ManifestLink link) => doc.Manifest.Links.Add(link);

        private static ChangeItem JevLine(string kind, string objectRef, string target, string after, string status = "approved", string needs = null,
                                          string ruleId = null, string point = SumPoint) =>
            new ChangeItem
            {
                ObjectRef = objectRef, ObjectName = objectRef.Substring(objectRef.LastIndexOf('/') + 1), Kind = kind, Target = target, After = after,
                Source = "jev", JevItem = SalesItem, DecisionPoint = point, Status = status, RuleId = ruleId, Title = kind, Needs = needs,
            };

        private static ChangeItem RuleLine(string ruleId, string objectRef, string kind, string target, string after) =>
            new ChangeItem { ObjectRef = objectRef, ObjectName = objectRef, Kind = kind, Target = target, After = after, Source = "deterministic", RuleId = ruleId, Status = "approved" };

        private static StartPlanDecide.RoundInput Round(int n, IEnumerable<ChangeItem> jev = null, IEnumerable<ChangeItem> rules = null, IEnumerable<string> decided = null,
                                                        Func<string, bool> resolves = null) =>
            new StartPlanDecide.RoundInput
            {
                Round = n, JevLines = (jev ?? Enumerable.Empty<ChangeItem>()).ToList(), RuleLines = (rules ?? Enumerable.Empty<ChangeItem>()).ToList(),
                Decided = new HashSet<string>(decided ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase), Resolves = resolves,
            };

        private static LocalEngine.SummarizePick Pick(string column, string pick, bool sure, string measure = null) =>
            new LocalEngine.SummarizePick { TableName = "Sales", ColumnName = column, ColumnRef = "column:Sales/" + column, Pick = pick, Sure = sure, MeasureName = measure };

                // ------------------------------------------------------------------ fold

        [Fact]
        public void A_sure_pick_becomes_a_base_measure_and_none_takes_it_away()
        {
            var doc = Doc();
            var moved = StartPlanDecide.FoldPicks(doc.Manifest, new[] { Pick("Amount", "sum", true, "Total Amount"), Pick("Unit Price", "average", true, "Average Unit Price") });
            Assert.Equal(2, moved);
            var amount = doc.Manifest.BaseMeasures.Single(b => b.Column == "Amount");
            Assert.Equal(("sum", "Total Amount", StartDecidedBy.Jev), (amount.Pick, amount.Name, amount.DecidedBy));
            Assert.False(string.IsNullOrEmpty(amount.Identity));
            // The manifest's own allocation: the same seed, source table and source column always give the same identity.
            Assert.Equal(amount.Identity, StartPlanDecide.BaseMeasureIdentity(doc.Manifest, "Sales", "Amount"));
            Assert.NotEqual(amount.Identity, StartPlanDecide.BaseMeasureIdentity(doc.Manifest, "Sales", "Qty"));

            // The same picks again are no change at all.
            Assert.Equal(0, StartPlanDecide.FoldPicks(doc.Manifest, new[] { Pick("Amount", "sum", true, "Total Amount") }));

            // None takes the measure away.
            Assert.Equal(1, StartPlanDecide.FoldPicks(doc.Manifest, new[] { Pick("Amount", "none", true) }));
            Assert.DoesNotContain(doc.Manifest.BaseMeasures, b => b.Column == "Amount");
        }

        [Fact]
        public void A_new_pick_replaces_the_measure_and_a_persons_pick_is_never_overwritten()
        {
            var doc = Doc();
            StartPlanDecide.FoldPicks(doc.Manifest, new[] { Pick("Unit Price", "sum", true, "Total Unit Price") });
            Assert.Equal(1, StartPlanDecide.FoldPicks(doc.Manifest, new[] { Pick("Unit Price", "average", true, "Average Unit Price") }));
            var price = doc.Manifest.BaseMeasures.Single();
            Assert.Equal(("average", "Average Unit Price"), (price.Pick, price.Name));

            price.DecidedBy = StartDecidedBy.Person;
            Assert.Equal(0, StartPlanDecide.FoldPicks(doc.Manifest, new[] { Pick("Unit Price", "sum", true, "Total Unit Price") }));
            Assert.Equal("average", price.Pick);
        }

        [Fact]
        public void A_pick_the_rule_made_because_jev_was_unsure_is_the_rules_and_goes_to_Needs_you()
        {
            var doc = Doc();
            StartPlanDecide.FoldPicks(doc.Manifest, new[] { Pick("Amount", "sum", false, "Total Amount") });
            var bm = doc.Manifest.BaseMeasures.Single();
            Assert.Equal(StartDecidedBy.Rule, bm.DecidedBy);
            // Until Jev has looked (a Free plan, one waiting for permission) a rule's pick is the source's default and nobody was unsure of it.
            Assert.Empty(StartPlanDecide.NeedsYou(doc));
            doc.Decide.State = StartDecideState.Stable;
            var need = Assert.Single(StartPlanDecide.NeedsYou(doc));
            Assert.Equal(StartNeedKind.UnsurePick, need.Kind);
            Assert.Equal("sum", need.Recommended);

            // Answering it settles the measure by hand and the question goes away.
            var outcome = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "answer", Target = need.Id, Value = "average" }, "human");
            Assert.True(outcome.Ok, outcome.Refused);
            Assert.Equal(("average", StartDecidedBy.Person), (bm.Pick, bm.DecidedBy));
            Assert.Empty(StartPlanDecide.NeedsYou(doc));
        }

        // ------------------------------------------------------------------ arbitration

        [Fact]
        public void A_round_adds_lines_and_the_same_round_again_changes_nothing()
        {
            var doc = Doc();
            var line = JevLine("set_measure_format", "measure:Sales/Total Amount", "formatString", "#,0");
            var flips = new Dictionary<string, int>();
            var first = StartPlanDecide.Merge(doc, Round(1, new[] { line }), flips);
            Assert.Equal(1, first.Added);
            var again = StartPlanDecide.Merge(doc, Round(2, new[] { JevLine("set_measure_format", "measure:Sales/Total Amount", "formatString", "#,0") }), flips);
            Assert.False(again.Any);
            // And a round where the family has nothing new (the scratch has the write applied) keeps the line.
            var silent = StartPlanDecide.Merge(doc, Round(3), flips);
            Assert.False(silent.Any);
            Assert.Single(doc.Lines);
        }

        [Fact]
        public void The_rules_stand_aside_where_a_live_family_decided_and_one_line_is_kept_per_change()
        {
            var doc = Doc();
            // FMT-SUMMARIZE writes the same None the base measure family decides: it stands aside for a column Jev decided.
            var rule = RuleLine("FMT-SUMMARIZE", "column:Sales/Amount", "set_summarize_by", "summarizeBy", "None");
            var other = RuleLine("FMT-SUMMARIZE", "column:Sales/Qty", "set_summarize_by", "summarizeBy", "None");
            var change = StartPlanDecide.Merge(doc, Round(1, rules: new[] { rule, other }, decided: new[] { LocalEngine.JevRun.UnansweredKey(SumPoint, "column:Sales/Amount") }), new Dictionary<string, int>());
            Assert.Equal(1, change.Added);
            Assert.Equal("column:Sales/Qty", Assert.Single(doc.Lines).Item.ObjectRef);

            // A rule's line and a Jev line for the same write: one line, Jev's.
            var doc2 = Doc();
            var jev = JevLine("set_measure_format", "measure:Sales/M", "formatString", "0.0%", point: DecisionRegistry.MeasureFolderAndFormat);
            var rule2 = RuleLine("FMT-MEASURE", "measure:Sales/M", "set_measure_format", "formatString", "#,0");
            StartPlanDecide.Merge(doc2, Round(1, new[] { jev }, new[] { rule2 }), new Dictionary<string, int>());
            var kept = Assert.Single(doc2.Lines);
            Assert.Equal((StartDecidedBy.Jev, "0.0%"), (kept.DecidedBy, kept.Item.After));
        }

        [Fact]
        public void The_loops_own_good_practice_lines_are_the_rules_and_a_sure_pick_is_jevs()
        {
            Assert.Equal(StartDecidedBy.Rule, StartPlanDecide.DecidedByOf(JevLine("set_summarize_by", "column:Sales/Amount", "summarizeBy", "None", ruleId: ColumnSummarizeFamily.OffRuleId)));
            Assert.Equal(StartDecidedBy.Rule, StartPlanDecide.DecidedByOf(JevLine("set_column_hidden", "column:Sales/Amount", "isHidden", "Hidden", ruleId: ColumnSummarizeFamily.HideRuleId)));
            Assert.Equal(StartDecidedBy.Jev, StartPlanDecide.DecidedByOf(JevLine("set_measure_format", "measure:Sales/M", "formatString", "#,0")));
            Assert.Equal(StartDecidedBy.Rule, StartPlanDecide.DecidedByOf(RuleLine("FMT-MEASURE", "measure:Sales/M", "set_measure_format", "formatString", "#,0")));
        }

        [Fact]
        public void A_persons_pin_keep_or_drop_is_never_overwritten_and_a_pin_outlives_its_line()
        {
            var doc = Doc();
            var flips = new Dictionary<string, int>();
            StartPlanDecide.Merge(doc, Round(1, new[] { JevLine("set_measure_format", "measure:Sales/M", "formatString", "#,0") }), flips);
            var pin = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "pin", Target = "measure:Sales/M|formatString", Value = "$#,0.00" }, "human");
            Assert.True(pin.Ok, pin.Refused);
            var again = StartPlanDecide.Merge(doc, Round(2, new[] { JevLine("set_measure_format", "measure:Sales/M", "formatString", "0.0%") }), flips);
            Assert.False(again.Any);
            Assert.Equal("$#,0.00", Assert.Single(doc.Lines).Item.After);

            // The line goes (its object was left out), then the family proposes it again: the pinned value comes back.
            doc.Lines.Clear();
            StartPlanDecide.Merge(doc, Round(3, new[] { JevLine("set_measure_format", "measure:Sales/M", "formatString", "0.0%") }), flips);
            var back = Assert.Single(doc.Lines);
            Assert.Equal(("$#,0.00", StartDecidedBy.Person), (back.Item.After, back.DecidedBy));
        }

        [Fact]
        public void A_line_stands_while_its_object_is_there_and_goes_when_it_is_not()
        {
            var doc = Doc();
            var flips = new Dictionary<string, int>();
            StartPlanDecide.Merge(doc, Round(1, new[] { JevLine("set_data_category", "column:Sales/Amount", "dataCategory", "Currency", point: DecisionRegistry.ColumnCategory) }), flips);
            Assert.False(StartPlanDecide.Merge(doc, Round(2, resolves: _ => true), flips).Any);
            var gone = StartPlanDecide.Merge(doc, Round(3, resolves: _ => false), flips);
            Assert.Equal(1, gone.Removed);
            Assert.Empty(doc.Lines);
        }

        [Fact]
        public void A_follower_of_a_measure_nobody_makes_never_stands_and_never_counts_as_a_change()
        {
            var doc = Doc();
            var need = JevFamily.NeedsKey("create_measure", "measure:Sales/Total Amount");
            var follower = JevLine("set_measure_format", "measure:Sales/Total Amount", "formatString", "#,0", needs: need);

            // No base measure holds that name (the person dropped it): the family proposes the follower, and it is not a line and not a change.
            var change = StartPlanDecide.Merge(doc, Round(1, new[] { follower }), new Dictionary<string, int>());
            Assert.False(change.Any);
            Assert.Empty(doc.Lines);

            // With the measure in the manifest the same follower stands.
            StartPlanDecide.FoldPicks(doc.Manifest, new[] { Pick("Amount", "sum", true, "Total Amount") });
            var kept = StartPlanDecide.Merge(doc, Round(2, new[] { JevLine("set_measure_format", "measure:Sales/Total Amount", "formatString", "#,0", needs: need) }), new Dictionary<string, int>());
            Assert.Equal(1, kept.Added);
            Assert.Equal(need, StartPlanDecide.NeedsOf(Assert.Single(doc.Lines)));
        }

        [Fact]
        public void A_line_Jev_keeps_changing_its_mind_about_is_frozen_dropped_and_sent_to_Needs_you()
        {
            var doc = Doc();
            var flips = new Dictionary<string, int>();
            ChangeItem Cat(string v) => JevLine("set_data_category", "column:Sales/Region", "dataCategory", v, point: DecisionRegistry.ColumnCategory);
            Assert.Equal(1, StartPlanDecide.Merge(doc, Round(1, new[] { Cat("Country") }), flips).Added);
            Assert.Equal(1, StartPlanDecide.Merge(doc, Round(2, new[] { Cat("City") }), flips).Changed);
            var frozen = StartPlanDecide.Merge(doc, Round(3, new[] { Cat("Country") }), flips);
            Assert.Equal(1, frozen.Frozen.Count);
            var line = Assert.Single(doc.Lines);
            Assert.False(line.Kept);
            Assert.StartsWith(StartDecideCopy.OscillatesPrefix, line.Why);

            // It stays frozen and counts as no change however Jev answers next, so the loop ends.
            Assert.False(StartPlanDecide.Merge(doc, Round(4, new[] { Cat("Region") }), flips).Any);
            Assert.False(StartPlanDecide.Merge(doc, Round(5, new[] { Cat("Country") }), flips).Any);

            doc.Decide.State = StartDecideState.Stable;
            var need = Assert.Single(StartPlanDecide.NeedsYou(doc));
            Assert.Equal(StartNeedKind.Oscillation, need.Kind);

            // The person answers keep: it is theirs now.
            var answered = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "answer", Target = need.Id, Value = "keep" }, "human");
            Assert.True(answered.Ok, answered.Refused);
            Assert.True(doc.Lines.Single().Kept);
            Assert.Equal(StartDecidedBy.Person, doc.Lines.Single().DecidedBy);
            Assert.Empty(StartPlanDecide.NeedsYou(doc));
        }

        [Fact]
        public void An_unsure_line_arrives_dropped_goes_to_Needs_you_and_blocks_nothing()
        {
            var doc = Doc();
            var unsure = JevLine("set_measure_format", "measure:Sales/M", "formatString", "0.0%", status: "rejected", point: DecisionRegistry.MeasureFolderAndFormat);
            StartPlanDecide.Merge(doc, Round(1, new[] { unsure }), new Dictionary<string, int>());
            var line = Assert.Single(doc.Lines);
            Assert.False(line.Kept);
            doc.Decide.State = StartDecideState.Stable;
            var need = Assert.Single(StartPlanDecide.NeedsYou(doc));
            Assert.Equal(StartNeedKind.UnsureLine, need.Kind);
        }

        // ------------------------------------------------------------------ identities

        [Fact]
        public void A_rename_between_two_rounds_is_not_one_object_going_and_another_coming()
        {
            var doc = Doc();
            var line = JevLine("set_data_category", "column:Sales/Amount", "dataCategory", "Currency", point: DecisionRegistry.ColumnCategory);
            var before = StartRefs.ShapeKey(doc.Manifest, line);
            doc.Manifest.Tables[0].Columns[0].Name = "Revenue";
            line.ObjectRef = "column:Sales/Revenue";
            Assert.Equal(before, StartRefs.ShapeKey(doc.Manifest, line));
            doc.Manifest.Tables[0].Name = "Orders";
            line.ObjectRef = "column:Orders/Revenue";
            Assert.Equal(before, StartRefs.ShapeKey(doc.Manifest, line));
        }

        // ------------------------------------------------------------------ edits: Needs follow and knock-on attribution (S12)

        private static StartPlanDoc DocWithMeasureAndFollowers()
        {
            var doc = Doc();
            StartPlanDecide.FoldPicks(doc.Manifest, new[] { Pick("Amount", "sum", true, "Total Amount") });
            var need = JevFamily.NeedsKey("create_measure", "measure:Sales/Total Amount");
            var hideNeed = JevFamily.NeedsKey("set_column_hidden", "column:Sales/Amount");
            var flips = new Dictionary<string, int>();
            StartPlanDecide.Merge(doc, Round(1, new[]
            {
                JevLine("set_measure_format", "measure:Sales/Total Amount", "formatString", "#,0", needs: need),
                JevLine("set_description", "measure:Sales/Total Amount", "description", "The total of Amount in Sales.", needs: need),
                JevLine("set_column_hidden", "column:Sales/Amount", "isHidden", "Hidden", needs: need, ruleId: ColumnSummarizeFamily.HideRuleId),
                JevLine("set_is_available_in_mdx", "column:Sales/Amount", "isAvailableInMdx", "false", needs: hideNeed, ruleId: ColumnSummarizeFamily.HideRuleId),
                JevLine("set_summarize_by", "column:Sales/Amount", "summarizeBy", "None", ruleId: ColumnSummarizeFamily.OffRuleId),
            }), flips);
            Assert.Equal(5, doc.Lines.Count);
            return doc;
        }

        [Fact]
        public void Dropping_a_measure_drops_its_followers_and_says_why_on_each()
        {
            var doc = DocWithMeasureAndFollowers();
            doc.Approval = new StartApproval { Digest = "d", Utc = DateTime.UtcNow };

            var outcome = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = "measure:Sales/Total Amount" }, "human");

            Assert.True(outcome.Ok, outcome.Refused);
            Assert.Equal("none", doc.Manifest.BaseMeasures.Single().Pick);
            // The format, description and hide need the measure; the Excel line needs the hide. The None line needs nothing and stays.
            Assert.Equal(4, doc.Lines.Count(l => !l.Kept));
            Assert.Equal(new[] { "set_summarize_by" }, doc.Lines.Where(l => l.Kept).Select(l => l.Item.Kind).ToArray());
            Assert.Equal(4, outcome.KnockOns.Count);
            Assert.All(outcome.KnockOns, k => { Assert.Equal("dropped", k.Action); Assert.Contains("Total Amount", k.Because + "|" + k.Ref); });
            Assert.All(doc.Lines.Where(l => !l.Kept), l => { Assert.Equal(StartDecidedBy.Person, l.DecidedBy); Assert.False(string.IsNullOrWhiteSpace(l.Why)); });
            Assert.Null(doc.Approval);   // any edit voids the approval
        }

        [Fact]
        public void Keeping_a_follower_keeps_its_measure_and_the_rest_of_its_pair()
        {
            var doc = DocWithMeasureAndFollowers();
            StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = "measure:Sales/Total Amount" }, "human");
            Assert.Equal("none", doc.Manifest.BaseMeasures.Single().Pick);

            // Keep ONE follower (the description): the measure comes back with the pick it had, and the others that need it with it.
            var outcome = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "keep_line", Target = "set_description|measure:Sales/Total Amount|description" }, "human");

            Assert.True(outcome.Ok, outcome.Refused);
            Assert.Equal("sum", doc.Manifest.BaseMeasures.Single().Pick);
            Assert.All(doc.Lines, l => Assert.True(l.Kept, StartPlanDecide.LineKey(l)));
            Assert.Contains(outcome.KnockOns, k => k.Action == "kept" && k.Ref == "measure:Sales/Total Amount");
        }

        [Fact]
        public void Dropping_a_line_other_lines_need_takes_them_with_it_and_keeping_one_brings_its_need_back()
        {
            var doc = DocWithMeasureAndFollowers();
            var drop = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = "set_column_hidden|column:Sales/Amount|isHidden" }, "agent");
            Assert.True(drop.Ok, drop.Refused);
            Assert.False(doc.Lines.Single(l => l.Item.Kind == "set_is_available_in_mdx").Kept);
            Assert.True(doc.Lines.Single(l => l.Item.Kind == "set_measure_format").Kept);   // the measure's other lines do not need the hide
            Assert.Equal(StartDecidedBy.Assistant, doc.Lines.Single(l => l.Item.Kind == "set_column_hidden").DecidedBy);
            Assert.Contains("your assistant", drop.KnockOns.Single().Because);

            var keep = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "keep_line", Target = "set_is_available_in_mdx|column:Sales/Amount|isAvailableInMdx" }, "human");
            Assert.True(keep.Ok, keep.Refused);
            Assert.True(doc.Lines.Single(l => l.Item.Kind == "set_column_hidden").Kept);   // the Excel line cannot stand without the hide
        }

        [Fact]
        public void Followers_are_found_from_what_a_stored_line_keeps_in_its_public_arguments()
        {
            // Needs is internal and a stored document loses it; the copy in Args["needs"] is what survives a restart.
            var doc = DocWithMeasureAndFollowers();
            var json = System.Text.Json.JsonSerializer.Serialize(doc);
            var back = System.Text.Json.JsonSerializer.Deserialize<StartPlanDoc>(json);
            var format = back.Lines.Single(l => l.Item.Kind == "set_measure_format");
            Assert.Null(format.Item.Needs);
            Assert.Equal(JevFamily.NeedsKey("create_measure", "measure:Sales/Total Amount"), StartPlanDecide.NeedsOf(format));
            Assert.Equal(JevFamily.NeedsKey("create_measure", "measure:Sales/Total Amount"), StartPlanDecide.ToChangeItem(format).Needs);

            var outcome = StartPlanEditing.Apply(back, new StartPlanEdit { Op = "drop_line", Target = "measure:Sales/Total Amount" }, "human");
            Assert.Equal(4, outcome.KnockOns.Count);
        }

        [Fact]
        public void A_new_pick_removes_the_old_measures_followers_and_a_none_pick_can_be_undone_by_keeping()
        {
            var doc = DocWithMeasureAndFollowers();
            var outcome = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_pick", Target = "Sales/Amount", Value = "average" }, "human");
            Assert.True(outcome.Ok, outcome.Refused);
            var bm = doc.Manifest.BaseMeasures.Single();
            Assert.Equal(("average", StartDecidedBy.Person), (bm.Pick, bm.DecidedBy));
            Assert.NotEqual("Total Amount", bm.Name);
            // The lines about the measure that went are gone (they were about something that no longer exists); the column's own lines stay.
            Assert.DoesNotContain(doc.Lines, l => l.Item.ObjectRef == "measure:Sales/Total Amount");
            Assert.All(outcome.KnockOns, k => Assert.Equal("removed", k.Action));

            var refused = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_pick", Target = "Sales/Amount", Value = "median" }, "human");
            Assert.False(refused.Ok);
            Assert.Contains("is not a way to add up", refused.Refused);
        }

        [Fact]
        public void A_rename_follows_everywhere_a_ref_is_kept_so_nothing_is_lost()
        {
            var doc = DocWithMeasureAndFollowers();
            AddLink(doc, new ManifestLink { Identity = "l1", FromTable = "Sales", FromColumns = new[] { "Amount" }, ToTable = "Date", ToColumns = new[] { "Key" }, Active = true });
            doc.Pins["pin-key"] = "x";
            var outcome = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "rename", Target = "table:Sales", Value = "Orders" }, "human");
            Assert.True(outcome.Ok, outcome.Refused);
            Assert.Equal("Orders", doc.Manifest.Tables[0].Name);
            Assert.Equal("Orders", doc.Manifest.BaseMeasures.Single().Table);
            Assert.Equal("Orders", doc.Manifest.Links.Single().FromTable);
            Assert.All(doc.Lines, l => Assert.StartsWith(l.Item.ObjectRef.StartsWith("measure") ? "measure:Orders/" : "column:Orders/", l.Item.ObjectRef));
            // What each line needs is rewritten too, so the followers still find their measure.
            Assert.Equal(JevFamily.NeedsKey("create_measure", "measure:Orders/Total Amount"), StartPlanDecide.NeedsOf(doc.Lines.Single(l => l.Item.Kind == "set_measure_format")));
            var drop = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = "measure:Orders/Total Amount" }, "human");
            Assert.Equal(4, drop.KnockOns.Count);

            var col = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "rename", Target = "column:Orders/Amount", Value = "Revenue" }, "human");
            Assert.True(col.Ok, col.Refused);
            Assert.Equal("Revenue", doc.Manifest.BaseMeasures.Single().Column);
            Assert.Equal(new[] { "Revenue" }, doc.Manifest.Links.Single().FromColumns);
            Assert.Contains(doc.Lines, l => l.Item.ObjectRef == "column:Orders/Revenue");
            Assert.Equal(JevFamily.NeedsKey("set_column_hidden", "column:Orders/Revenue"), StartPlanDecide.NeedsOf(doc.Lines.Single(l => l.Item.Kind == "set_is_available_in_mdx")));

            Assert.False(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "rename", Target = "column:Orders/Revenue", Value = "Unit Price" }, "human").Ok);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "rename", Target = "table:Orders", Value = "orders" }, "human").Ok);   // a change of case is a rename
        }

        [Fact]
        public void Leaving_a_table_or_column_out_removes_the_lines_and_measures_about_it_and_says_so()
        {
            var doc = DocWithMeasureAndFollowers();
            var column = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "include_column", Target = "Sales/Amount", Value = "false" }, "human");
            Assert.True(column.Ok, column.Refused);
            // The base measure is KEPT (it builds nothing while its column is out, and comes back with the column); the loop's lines about it are removed.
            Assert.False(doc.Manifest.IsBuilt(doc.Manifest.BaseMeasures.Single()));
            Assert.Empty(doc.Lines);
            Assert.All(column.KnockOns, k => Assert.Equal("removed", k.Action));
            Assert.Equal(5, column.KnockOns.Count);

            var doc2 = DocWithMeasureAndFollowers();
            var table = StartPlanEditing.Apply(doc2, new StartPlanEdit { Op = "include_table", Target = "Sales", Value = "false" }, "human");
            Assert.True(table.Ok, table.Refused);
            Assert.False(doc2.Manifest.Tables[0].Include);
            Assert.Empty(doc2.Lines);
            Assert.False(doc2.Manifest.IsBuilt(doc2.Manifest.BaseMeasures.Single()));
        }

        [Fact]
        public void The_fiscal_year_a_link_and_a_bad_edit_are_handled_with_plain_refusals()
        {
            var doc = Doc();
            Assert.False(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_fiscal_year_end", Value = "6/30" }, "human").Ok);   // no date table (the manifest has a date table with no name)
            doc.Manifest.DateTable = new ManifestDateTable { Name = "Date", DateColumn = "Date" };
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_fiscal_year_end", Value = "6/30" }, "human").Ok);
            Assert.Equal("6/30", doc.Manifest.DateTable.FiscalYearEnd);
            Assert.False(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_fiscal_year_end", Value = "13/40" }, "human").Ok);
            Assert.True(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_fiscal_year_end", Value = "calendar" }, "human").Ok);
            Assert.Null(doc.Manifest.DateTable.FiscalYearEnd);

            AddLink(doc, new ManifestLink { Identity = "l1", FromTable = "Sales", FromColumns = new[] { "A", "B" }, ToTable = "Date", ToColumns = new[] { "A", "B" }, Unresolved = true, Note = "Sales links to Date on A and B together.", Active = false });
            var on = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_link", Target = "l1", Value = "on" }, "human");
            Assert.False(on.Ok);
            Assert.Contains("combined key", on.Refused);
            Assert.False(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "fly" }, "human").Ok);
            Assert.False(StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "drop_line", Target = "nope" }, "human").Ok);
        }

        [Fact]
        public void An_edit_that_changes_nothing_leaves_the_approval_alone()
        {
            var doc = Doc();
            AddLink(doc, new ManifestLink { Identity = "l1", FromTable = "Sales", FromColumns = new[] { "A" }, ToTable = "Date", ToColumns = new[] { "A" }, Active = true });
            doc.Approval = new StartApproval { Digest = "d", Utc = DateTime.UtcNow };
            var same = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_link", Target = "l1", Value = "on" }, "human");
            Assert.True(same.Ok);
            Assert.False(same.Changed);
            Assert.NotNull(doc.Approval);
            var off = StartPlanEditing.Apply(doc, new StartPlanEdit { Op = "set_link", Target = "l1", Value = "off" }, "human");
            Assert.True(off.Changed);
            Assert.Null(doc.Approval);
        }
    }
}
