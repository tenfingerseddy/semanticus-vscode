// Which way "Waits for" points (Plan at the heart, slice 7 integration; contract 5.2 as amended 24 Sep).
//
// What these tests hold, one line each:
//   on a fresh model, with the person's cleanup and proposal rows still open, some Semanticus row is Ready and one
//     press of Run the plan applies it (a reader of a population or of the reports never holds a writer back)
//   a person's row that feeds a Semanticus row only through the population facet waits for it (5.2 amended)
//   between two Semanticus rows, and when a real object key links them, producer-first still holds
//   a mutual pair (each writes what the other reads, or both write one real key) is still decided by plan order
//   a task the person gave goes before a population or Needs you row that Semanticus does not do itself, and
//     still waits for Semanticus's own rows, real writers of its objects, and earlier tasks (5.2, Coordinator 25 Sep)
//   a person's row that holds a KEPT change to the task's named object is a real writer: the task waits for it,
//     in either plan order (Sol 6 slice 9 review); an unchosen candidate (cleanup's proposed deletes before any
//     line is worked out and kept) is not a pending write, so the task is still offered (Coordinator 25 Sep)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    public sealed class PlanWaitDirectionTests
    {
        private static string Dump(ModelPlanView plan) => string.Join("\n", plan.Items.Select(i =>
            i.Id + " doer=" + i.Doer + " word=" + i.StateWord + " status=" + i.Status
            + " reads=[" + string.Join(",", i.Reads ?? Array.Empty<string>()) + "]"
            + " writes=[" + string.Join(",", i.Writes ?? Array.Empty<string>()) + "]"
            + " waits=[" + string.Join(",", (i.WaitsFor ?? Array.Empty<PlanWaitRef>()).Select(w => w.Id)) + "]"));

        /// <summary>Coordinator's finding from lane E5: the person's open cleanup and proposal rows (population and
        /// report-aware READERS) held every Semanticus row (a WRITER), so Run the plan did nothing on a fresh model.</summary>
        [Theory]
        [InlineData("Gappy")]
        [InlineData("Wide")]
        [InlineData("Star")]
        public async Task A_fresh_model_with_the_persons_rows_open_still_has_a_Ready_Semanticus_row_that_Run_the_plan_applies(string fixture)
        {
            using var f = await CommutationFixtures.All.Single(x => x.Name == fixture).Copy("WaitDir" + fixture);
            var plan = await f.Engine.GetModelPlanAsync();

            // The person's rows are open: nothing has been set aside to clear the way.
            var persons = plan.Items.Where(i => (i.Id.StartsWith("cleanup/", StringComparison.Ordinal)
                                                  || i.Id.StartsWith("proposal/", StringComparison.Ordinal))
                                                 && (i.Status == ModelPlanStatuses.Ready || i.Status == ModelPlanStatuses.Proposed))
                                .ToArray();
            Assert.True(persons.Length > 0, fixture + " has no open cleanup or proposal row to test against:\n" + Dump(plan));

            var ready = plan.Items.Where(i => i.Doer == PlanDoers.Semanticus && i.StateWord == PlanStateWords.Ready && !i.Held)
                                  .Select(i => i.Id).ToArray();
            Assert.True(ready.Length > 0, fixture + ": no Semanticus row is Ready while the person's rows are open:\n" + Dump(plan));
            // None of the Ready Semanticus rows names a person's reader as its blocker.
            foreach (var p in persons)
                foreach (var id in ready)
                    Assert.DoesNotContain(plan.Items.Single(i => i.Id == id).WaitsFor, w => w.Id == p.Id);

            var result = await f.Engine.ApplyPlanItemsAsync(null, "human", plan.ReadyDigest);
            Assert.True(result.Performed, fixture + ": " + result.Sentence + "\n" + Dump(plan));
            Assert.True(result.Applied > 0, fixture + ": " + result.Sentence);
            Assert.NotEmpty(result.ItemIds);
        }

        private static Footprint Fp(string[] reads, string[] writes) =>
            new Footprint().ReadAll(reads).WriteAll(writes);

        // A safe fix shaped as PlanFootprint.SafeFix states it (it reads the objects facet), and a cleanup shaped
        // as PlanFootprint.Cleanup states it (it deletes, so it writes the facet). Only the facet links them.
        private static Footprint SafeFixShape() =>
            Fp(new[] { "column:Sales/CustomerKey", "model:relationships:Sales", PlanFootprint.Objects },
               new[] { "column:Sales/CustomerKey", PlanFootprint.Revision });
        private static Footprint CleanupShape() =>
            Fp(new[] { "measure:Sales/Old Total", PlanFootprint.Reports, PlanFootprint.Objects },
               new[] { "measure:Sales/Old Total", PlanFootprint.Objects, PlanFootprint.Revision });

        [Fact]
        public void A_persons_row_that_feeds_a_Semanticus_row_only_through_the_population_facet_waits_for_it()
        {
            var rows = new[] { ("cleanup/model", CleanupShape()), ("safefix/Visibility", SafeFixShape()) };
            var doers = new Dictionary<string, string> { ["cleanup/model"] = PlanDoers.You, ["safefix/Visibility"] = PlanDoers.Semanticus };

            var (waits, _) = PlanFootprint.Waits(rows, doers);

            Assert.False(waits.ContainsKey("safefix/Visibility"));
            Assert.Equal(new[] { "safefix/Visibility" }, waits["cleanup/model"]);
        }

        [Fact]
        public void Between_two_Semanticus_rows_the_population_reader_still_waits_for_the_writer()
        {
            var rows = new[] { ("cleanup/model", CleanupShape()), ("safefix/Visibility", SafeFixShape()) };
            var doers = new Dictionary<string, string> { ["cleanup/model"] = PlanDoers.Semanticus, ["safefix/Visibility"] = PlanDoers.Semanticus };

            var (waits, _) = PlanFootprint.Waits(rows, doers);

            Assert.Equal(new[] { "cleanup/model" }, waits["safefix/Visibility"]);
            Assert.False(waits.ContainsKey("cleanup/model"));
        }

        [Fact]
        public void A_persons_row_that_also_touches_the_same_object_keeps_producer_first()
        {
            // The cleanup deletes the very column the safe fix hides: a real key links them, so the exception
            // does not apply and they conflict on the column.
            var cleanup = Fp(new[] { "column:Sales/CustomerKey", PlanFootprint.Objects },
                             new[] { "column:Sales/CustomerKey", PlanFootprint.Objects, PlanFootprint.Revision });
            // The person has kept the delete (a pending write). A row with nothing chosen yields to every automated row since the 8 Oct
            // "automation first" rule, so only a kept line makes this the case the test is about.
            cleanup.Chosen.Add("column:Sales/CustomerKey");
            var rows = new[] { ("cleanup/model", cleanup), ("safefix/Visibility", SafeFixShape()) };
            var doers = new Dictionary<string, string> { ["cleanup/model"] = PlanDoers.You, ["safefix/Visibility"] = PlanDoers.Semanticus };

            var (waits, _) = PlanFootprint.Waits(rows, doers);

            Assert.Equal(new[] { "cleanup/model" }, waits["safefix/Visibility"]);
        }

        // ---- unchosen candidates hold nothing (Coordinator 26 Sep, F-281 option (a) widened to every row) ----

        // A Jev format row as PlanFootprint.Jev states it: it writes the column it formats and reads the population.
        private static Footprint FormatShape() =>
            Fp(new[] { "column:Sales/Amount", PlanFootprint.Objects, Measure },
               new[] { "column:Sales/Amount", PlanFootprint.Revision });
        // The cleanup before anybody chose a line: its candidates include the very column the format row writes.
        private static Footprint UnchosenCleanup()
        {
            var fp = Fp(new[] { "column:Sales/Amount", Measure, PlanFootprint.Reports, PlanFootprint.Objects },
                        new[] { "column:Sales/Amount", PlanFootprint.Objects, PlanFootprint.Revision });
            fp.Candidate = true;
            return fp;
        }

        [Fact]
        public void An_unchosen_cleanup_does_not_hold_a_row_that_touches_one_of_its_candidates()
        {
            // The cleanup is FIRST in plan order and its candidate is the format row's column: before, the format row
            // waited for it; now the candidate list holds nothing and the cleanup waits for the format row.
            var rows = new[] { ("cleanup/model", UnchosenCleanup()), ("jev.column-format.table:Sales", FormatShape()) };
            var doers = new Dictionary<string, string> { ["cleanup/model"] = PlanDoers.You, ["jev.column-format.table:Sales"] = PlanDoers.Semanticus };

            var (waits, _) = PlanFootprint.Waits(rows, doers);

            Assert.False(waits.ContainsKey("jev.column-format.table:Sales"), Describe(waits));
            Assert.Equal(new[] { "jev.column-format.table:Sales" }, waits["cleanup/model"]);
        }

        [Fact]
        public void A_kept_cleanup_line_on_the_object_holds_the_row_that_touches_it()
        {
            var cleanup = UnchosenCleanup();
            cleanup.Candidate = false;
            cleanup.Chosen.Add("column:Sales/Amount");
            var rows = new[] { ("cleanup/model", cleanup), ("jev.column-format.table:Sales", FormatShape()) };
            var doers = new Dictionary<string, string> { ["cleanup/model"] = PlanDoers.You, ["jev.column-format.table:Sales"] = PlanDoers.Semanticus };

            var (waits, _) = PlanFootprint.Waits(rows, doers);

            Assert.Equal(new[] { "cleanup/model" }, waits["jev.column-format.table:Sales"]);
        }

        private static string Describe(Dictionary<string, List<string>> waits) =>
            string.Join("; ", waits.Select(kv => kv.Key + " <- " + string.Join(",", kv.Value)));

        [Fact]
        public void A_mutual_pair_is_still_decided_by_plan_order()
        {
            // Each writes what the other reads.
            var a = Fp(new[] { "column:Sales/Amount" }, new[] { "measure:Sales/Total" });
            var b = Fp(new[] { "measure:Sales/Total" }, new[] { "column:Sales/Amount" });
            var (waits, _) = PlanFootprint.Waits(new[] { ("a", a), ("b", b) });
            Assert.Equal(new[] { "a" }, waits["b"]);
            Assert.False(waits.ContainsKey("a"));

            // Both write the same real (non-event) key.
            var c = Fp(Array.Empty<string>(), new[] { "column:Sales/Amount" });
            var d = Fp(Array.Empty<string>(), new[] { "column:Sales/Amount" });
            (waits, _) = PlanFootprint.Waits(new[] { ("d", d), ("c", c) });
            Assert.Equal(new[] { "d" }, waits["c"]);
            Assert.False(waits.ContainsKey("d"));
        }

        // ---- a task the person gave goes before the Plan's population and proposal rows (5.2, Coordinator 25 Sep)

        private const string Measure = "measure:Sales/Total Sales";

        // A task given from a finding on the measure, as PlanFootprint.Asked states it: unknown, its floor written and read.
        private static Footprint TaskShape()
        {
            var fp = Footprint.Unknown();
            fp.WriteAll(new[] { Measure, PlanFootprint.Revision }).ReadAll(new[] { Measure });
            return fp;
        }
        // describe/model as PlanFootprint.Describe states it: writes and reads the measure, reads the population.
        private static Footprint DescribeShape() =>
            Fp(new[] { Measure, PlanFootprint.Objects }, new[] { Measure, PlanFootprint.Revision });

        [Fact]
        public void A_task_given_from_a_finding_goes_before_the_describe_and_cleanup_rows_earlier_in_plan_order()
        {
            // Cleanup as PlanFootprint.Cleanup states it when the task's measure is in use: it reads every measure and
            // the population, and deletes other objects. It does not write the task's measure.
            var cleanup = Fp(new[] { Measure, PlanFootprint.Reports, PlanFootprint.Objects },
                             new[] { "measure:Sales/Old Margin", PlanFootprint.Objects, PlanFootprint.Revision });
            var rows = new[] { ("describe/model", DescribeShape()), ("cleanup/model", cleanup), ("pt-1", TaskShape()) };
            var doers = new Dictionary<string, string>
            {
                ["describe/model"] = PlanDoers.Assistant, ["cleanup/model"] = PlanDoers.You, ["pt-1"] = PlanDoers.Assistant,
            };

            var (waits, _) = PlanFootprint.Waits(rows, doers);

            Assert.False(waits.ContainsKey("pt-1"));
            Assert.Contains("pt-1", waits["describe/model"]);
            Assert.Contains("pt-1", waits["cleanup/model"]);
            // Direction only: the pair still conflicts and is never independent.
            Assert.True(PlanFootprint.Conflict(TaskShape(), DescribeShape()));
            Assert.False(PlanFootprint.Independent(TaskShape(), DescribeShape()));
        }

        /// <summary>A footprint whose change set holds a kept line writing <paramref name="kept"/>.</summary>
        private static Footprint Kept(Footprint fp, params string[] kept) { fp.Chosen.UnionWith(kept); return fp; }

        [Fact]
        public void A_task_waits_for_a_persons_row_that_writes_its_object_in_either_plan_order()
        {
            // Flipped (Sol 6 slice 9 review): this once locked in the task going first, so the assistant worked the
            // measure before the person's own pending change to it.
            var needsYou = Kept(Fp(new[] { Measure }, new[] { Measure, PlanFootprint.Revision }), Measure);
            var doers = new Dictionary<string, string> { ["datetable/Date"] = PlanDoers.You, ["pt-1"] = PlanDoers.Assistant };

            var (waits, reordered) = PlanFootprint.Waits(new[] { ("datetable/Date", needsYou), ("pt-1", TaskShape()) }, doers);
            Assert.Equal(new[] { "datetable/Date" }, waits["pt-1"]);
            Assert.False(waits.ContainsKey("datetable/Date"));
            Assert.DoesNotContain("pt-1", reordered);   // the rule decided it, not plan order

            // The task earlier in plan order changes nothing: the person's writer still goes first.
            (waits, _) = PlanFootprint.Waits(new[] { ("pt-1", TaskShape()), ("datetable/Date", needsYou) }, doers);
            Assert.Equal(new[] { "datetable/Date" }, waits["pt-1"]);
            Assert.False(waits.ContainsKey("datetable/Date"));

            // A person's population row with a kept delete of the task's measure is the same: the task waits for it.
            var cleanup = Kept(Fp(new[] { Measure, PlanFootprint.Reports, PlanFootprint.Objects },
                                  new[] { Measure, PlanFootprint.Objects, PlanFootprint.Revision }), Measure);
            doers["cleanup/model"] = PlanDoers.You;
            (waits, _) = PlanFootprint.Waits(new[] { ("pt-1", TaskShape()), ("cleanup/model", cleanup) }, doers);
            Assert.Equal(new[] { "cleanup/model" }, waits["pt-1"]);
            Assert.False(waits.ContainsKey("cleanup/model"));
        }

        [Fact]
        public void A_task_goes_before_a_persons_row_that_only_names_its_object_as_an_unchosen_candidate()
        {
            // cleanup/model's declared footprint names every unused measure as a delete candidate. Nothing is chosen
            // or kept yet, so it is not a pending write of the measure, and the task given from a finding leads
            // (Coordinator 25 Sep, option a). The same row with the delete line kept is a writer (above).
            var candidates = Fp(new[] { Measure, PlanFootprint.Reports, PlanFootprint.Objects },
                                new[] { Measure, PlanFootprint.Objects, PlanFootprint.Revision });
            var doers = new Dictionary<string, string> { ["cleanup/model"] = PlanDoers.You, ["pt-1"] = PlanDoers.Assistant };

            var (waits, _) = PlanFootprint.Waits(new[] { ("cleanup/model", candidates), ("pt-1", TaskShape()) }, doers);

            Assert.False(waits.ContainsKey("pt-1"));
            Assert.Equal(new[] { "pt-1" }, waits["cleanup/model"]);
        }

        [Fact]
        public async Task A_task_on_an_unused_column_is_never_held_by_the_cleanup_even_when_its_delete_line_is_kept()
        {
            // An unused COLUMN (since m11/plan-harm round 2 the cleanup never offers a measure), and the task is never
            // held by the cleanup, kept line or not: the cleanup is always optional and no row waits for it
            // (coordinator ruling, 28 Sep). It used to wait once the delete was kept.
            using var f = await PlanQueueKit.GappyAsync("WaitDirKept.bim");
            // Round 4 (THE CLEANUP OFFER RULE, version 2): the row offers a column of a related table only when it can find that
            // table's label. Customer had a city and no name, so it offered nothing; the fixture gains a Name and a plain unused
            // Segment, and the task and the delete are about Segment. The rule is unchanged.
            await f.Engine.CreateColumnAsync("table:Customer", "Name", "String", null, "human");
            await f.Engine.CreateColumnAsync("table:Customer", "Segment", "String", null, "human");
            await f.SaveAsAsync("WaitDirKept.bim");
            await PlanHarmKit.AsExistingModelWithAReportReadAsync(f, "WaitDirKept.bim");
            const string objRef = "column:Customer/Segment";
            var added = await f.Engine.AddPlanTaskAsync(new PlanTaskRequest
            {
                Words = "Describe Segment on Customer", SourcePage = "readiness",
                Objects = new[] { objRef }, DoneWhen = PlanDoneWhen.ModelCheck,
                OriginLink = new PlanOriginLink { Kind = PlanOriginKinds.Finding, Ref = "DESC-COLUMN|" + objRef },
            });
            ModelPlanItem Task(ModelPlanView p) => p.Items.Single(i => i.Id == added.ItemId);
            // Only the cleanup is under test: the task may wait for another writer of Segment.
            bool HeldByCleanup(ModelPlanView p) => (Task(p).WaitsFor ?? Array.Empty<PlanWaitRef>()).Any(w => w.Id == "cleanup/model");

            // The column is unused, so cleanup/model names it only as a candidate: it holds nothing.
            var plan = await f.Engine.GetModelPlanAsync();
            Assert.False(HeldByCleanup(plan), "unchosen candidate:\n" + Dump(plan));

            // The cleanup's lines worked out. Round 5: Segment is a plain text column of a labelled table, so it arrives TICKED as a
            // suggestion, and a ticked line of an optional row still holds nothing.
            await f.Engine.ModelPlanActionAsync("cleanup/model", "propose", "human");
            plan = await f.Engine.GetModelPlanAsync();
            var line = plan.Items.Single(i => i.Id == "cleanup/model").ChangeSet.Lines.Single(l => l.Target == objRef);
            Assert.True(line.Kept && line.Suggested);
            Assert.False(HeldByCleanup(plan), "worked out, ticked as a suggestion:\n" + Dump(plan));

            // The person drops it, then keeps it themselves: a pending write, and still the cleanup holds the task back from
            // nothing. The optional cleanup waits for the task instead.
            await f.Engine.SetPlanItemAsync(line.PlanItemId, null, false, "human");
            plan = await f.Engine.GetModelPlanAsync();
            Assert.False(plan.Items.Single(i => i.Id == "cleanup/model").ChangeSet.Lines.Single(l => l.Target == objRef).Kept);
            Assert.False(HeldByCleanup(plan), "worked out, not kept:\n" + Dump(plan));
            await f.Engine.SetPlanItemAsync(line.PlanItemId, null, true, "human");
            plan = await f.Engine.GetModelPlanAsync();
            Assert.True(plan.Items.Single(i => i.Id == "cleanup/model").ChangeSet.Lines.Single(l => l.Target == objRef).Kept);
            Assert.False(HeldByCleanup(plan), "kept line:\n" + Dump(plan));
            Assert.Contains(plan.Items.Single(i => i.Id == "cleanup/model").WaitsFor, w => w.Id == added.ItemId);

            // Dropping that one line: still nothing held.
            await f.Engine.SetPlanItemAsync(line.PlanItemId, null, false, "human");
            plan = await f.Engine.GetModelPlanAsync();
            Assert.False(HeldByCleanup(plan), "dropped line:\n" + Dump(plan));
        }

        [Fact]
        public void A_task_still_goes_before_a_persons_row_that_only_reads_its_object()
        {
            // A person's row that reads the measure and writes elsewhere: the task feeds it, and it waits for the task.
            var reader = Fp(new[] { Measure, PlanFootprint.Objects }, new[] { "measure:Sales/Other", PlanFootprint.Revision });
            var doers = new Dictionary<string, string> { ["proposal/naming"] = PlanDoers.You, ["pt-1"] = PlanDoers.Assistant };

            var (waits, _) = PlanFootprint.Waits(new[] { ("proposal/naming", reader), ("pt-1", TaskShape()) }, doers);

            Assert.False(waits.ContainsKey("pt-1"));
            Assert.Equal(new[] { "pt-1" }, waits["proposal/naming"]);
        }

        [Fact]
        public void A_task_still_waits_for_a_real_writer_of_its_object()
        {
            // A row that is no population and not the person's: a real writer, earlier in plan order.
            var writer = Fp(new[] { Measure }, new[] { Measure, PlanFootprint.Revision });
            var rows = new[] { ("asst/abc", writer), ("pt-1", TaskShape()) };
            var doers = new Dictionary<string, string> { ["asst/abc"] = PlanDoers.Assistant, ["pt-1"] = PlanDoers.Assistant };

            var (waits, _) = PlanFootprint.Waits(rows, doers);

            Assert.Equal(new[] { "asst/abc" }, waits["pt-1"]);
        }

        [Fact]
        public void A_task_still_waits_for_Semanticus_own_safe_row_on_its_object()
        {
            // A safe fix on the task's measure, shaped as PlanFootprint.SafeFix states it: it reads the population,
            // but Semanticus does it itself, so it is the automatic work that goes first (Run the plan clears it).
            var safefix = Fp(new[] { Measure, "model:relationships:Sales", PlanFootprint.Objects }, new[] { Measure, PlanFootprint.Revision });
            var rows = new[] { ("safefix/Formatting", safefix), ("pt-1", TaskShape()) };
            var doers = new Dictionary<string, string> { ["safefix/Formatting"] = PlanDoers.Semanticus, ["pt-1"] = PlanDoers.Assistant };

            var (waits, _) = PlanFootprint.Waits(rows, doers);

            Assert.Equal(new[] { "safefix/Formatting" }, waits["pt-1"]);
            Assert.False(waits.ContainsKey("safefix/Formatting"));
        }

        [Fact]
        public void Two_tasks_on_one_object_keep_plan_order_and_are_never_independent()
        {
            var rows = new[] { ("pt-1", TaskShape()), ("pt-2", TaskShape()) };
            var doers = new Dictionary<string, string> { ["pt-1"] = PlanDoers.Assistant, ["pt-2"] = PlanDoers.Assistant };

            var (waits, _) = PlanFootprint.Waits(rows, doers);

            Assert.Equal(new[] { "pt-1" }, waits["pt-2"]);
            Assert.False(waits.ContainsKey("pt-1"));
            Assert.False(PlanFootprint.Independent(TaskShape(), TaskShape()));
        }
    }
}
